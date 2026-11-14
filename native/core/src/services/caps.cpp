// SPDX-License-Identifier: MIT
// kiln.caps v1 (abi/services/kiln_caps.h): the capability registry, and the only writer of <state>/capabilities.json.
//
// Reports. Every owner holds at most one report per name; the core's own (owner 0) are the sigdb feature rows, the
// in-core service rows, the rows of services a DLL published and the rows of service DLLs that did not make it. A
// reader sees, per name, the worst report: unavailable, then pending, then available; among equals the core's, then
// the oldest (an owner that replaces its report keeps its place). Reports go with their owner: api_owner_revoke and
// load_one call owner_gone(); as a net under every other path that revokes an owner (native mods, early plugins), each
// call into the registry first drops the reports of owners reg::owner_alive() no longer knows (checked again only
// when reg::version() moved).
//
// Changes. A change of what a reader sees (state, reason, text, source, flags, a row added or removed) counts one in
// generation(), marks the file dirty and is handed to the subscribers on the thread that caused it, after the lock
// was released, each call under an SEH guard (a faulting subscriber is removed). A report that leaves the visible row
// as it was changes nothing.
//
// The file. boot_complete() writes it once on the boot thread and starts a writer thread that parks on a condition
// variable while nothing is dirty and writes at most once a second when something is. flush() writes a last time when
// something is still dirty (the exit path), bounded in time.
//
// Rows of the tick hook: the hook can fault at run time (tick::installed() / tick::lifecycle() turn false) and tick.cpp
// does not say so. Registry calls, KilnApi.feature() answering false, and the writer (at least once a second while
// hooks are on) compare the bits with what the rows were built from and rebuild the tick rows when they moved.
//
// The client gate (gates.h): for every capability held until its game test the core's own report is "unavailable,
// policy, game test pending: <check ids>", whatever the name is otherwise (a sigdb feature, a service's row, a managed
// row). It is the core's report of the highest kind, so nothing the core derives replaces it, and as an unavailable
// report it is what a reader sees. A service may still report such a name pending: the held row hides it.
#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstring>
#include <map>
#include <memory>
#include <mutex>
#include <set>
#include <thread>
#include <unordered_map>
#include <vector>

#include "../crash.h"
#include "../gates.h"
#include "../image.h"
#include "../log.h"
#include "../registry.h"
#include "../sigdb.h"
#include "../tick.h"
#include "nlohmann/json.hpp"
#include "services/kiln_caps.h"
#include "services/kiln_profile.h"
#include "services/kiln_progress.h"
#include "service.h"

namespace kiln::caps {
namespace {

constexpr KilnHandle kHandleTag = 1ull << 60;  // kiln.caps handles, so one passed to another unsubscribe is not found
constexpr double kMinGapMs = 1000;            // capabilities.json: at most one write a second
constexpr size_t kSourceMax = 256;            // bytes of a source kept (owner names are file names)
constexpr uint32_t kReasonNames = 10;          // reason codes with a name in the file (NONE .. SESSION)

const char* const kStateName[] = {"unavailable", "available", "pending"};
const char* const kReasonName[kReasonNames] = {"none",   "symbol-missing", "service-missing", "service-older",
                                               "wrong-side", "needs-kiln-client", "policy", "faulted",
                                               "offline", "session"};

// The kinds of the core's own reports, by precedence: a name the core reports from two lists shows the higher one.
enum Kind : int { kNotCore = 0, kIdleDll = 1, kDll = 2, kPublished = 3, kService = 4, kFeature = 5, kGate = 6 };

struct Report {
    KilnOwner owner = 0;
    KilnOwner tied = 0;  // a core report that goes with this owner (a published service's row)
    int kind = kNotCore;
    uint64_t seq = 0;    // age: lower is older
    uint32_t state = KILN_CAP_UNAVAILABLE, reason = KILN_CAP_REASON_NONE;
    std::string text, source;
};

struct Shown {
    uint32_t state = 0, reason = 0, flags = 0;
    std::string text, source;
    bool operator==(const Shown&) const = default;
};

struct Name {
    std::vector<Report> reports;
    bool shown = false;  // `last` is what readers see
    Shown last;
};

struct Event {
    std::string name;
    Shown row;
    uint32_t change;
};

struct Sub {
    KilnHandle handle;
    KilnOwner owner;
    KilnCapsChangedFn fn;
    void* user;
    std::atomic<bool> live{true};
};
using Subs = std::vector<std::shared_ptr<Sub>>;

double clock_ms() { return now_ms(); }
double (*g_clock)() = &clock_ms;  // host tests drive it

// The locks are never destroyed: the writer thread may still be parked on them while the process ends.
std::timed_mutex& g_mutex = *new std::timed_mutex;  // timed: flush() at exit must not wait for ever
std::map<std::string, Name, std::less<>> g_names;
std::set<std::string, std::less<>> g_session;               // names ever reported pending, for the process's life
std::unordered_map<KilnOwner, uint32_t> g_owner_reports;    // reports per owner (KILN_CAP_OWNER_MAX)
std::map<std::string, Report, std::less<>> g_dll_failed;    // service DLLs that did not make it, by row name
Subs g_subs;
uint64_t g_seq = 0;
KilnHandle g_next_handle = 1;
std::atomic<uint64_t> g_generation{1};
uint64_t g_reg_checked = 0;                  // reg::version() at the last liveness check
std::vector<const std::string*> g_order;     // at(): index -> name, rebuilt when the generation moved
uint64_t g_order_gen = 0;

// What the core rows were built from (core_rows); read by every refresh.
std::atomic<bool> g_rows_built{false};
std::atomic<bool> g_hooks{false};  // read by refresh_tick() before it takes the lock
bool g_verified = false;
std::string g_safe_idle;
std::vector<std::string> g_feature_names;
std::atomic<uint8_t> g_tick_seen{0};  // bit 0 tick::installed(), bit 1 tick::lifecycle()

// ---- small helpers ----
bool name_ok(std::string_view n) {
    if (n.empty() || n.size() > KILN_CAP_NAME_MAX) return false;
    for (const char c : n)
        if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' ||
              c == '/' || c == '-'))
            return false;
    return true;
}

// At most `max` bytes, cut on a UTF-8 boundary (never inside a sequence).
std::string cut(std::string_view s, size_t max) {
    if (s.size() <= max) return std::string(s);
    size_t n = max;
    while (n > 0 && (static_cast<unsigned char>(s[n]) & 0xC0) == 0x80) --n;
    return std::string(s.substr(0, n));
}

int rank(uint32_t state) { return state == KILN_CAP_UNAVAILABLE ? 0 : state == KILN_CAP_PENDING ? 1 : 2; }

// `a` is shown rather than `b`.
bool shows_over(const Report& a, const Report& b) {
    if (rank(a.state) != rank(b.state)) return rank(a.state) < rank(b.state);
    if ((a.owner == 0) != (b.owner == 0)) return a.owner == 0;
    return a.seq < b.seq;
}

Report* core_report(Name& n) {
    for (Report& r : n.reports)
        if (r.owner == 0) return &r;
    return nullptr;
}

std::string reason_name(uint32_t reason) {
    return reason < kReasonNames ? kReasonName[reason] : "other-" + std::to_string(reason);
}

// ---- the registry (callers hold g_mutex) ----
void refresh_locked(std::map<std::string, Name, std::less<>>::iterator it, std::vector<Event>& ev) {
    Name& n = it->second;
    if (n.reports.empty()) {
        if (n.shown) {
            ev.push_back({it->first, n.last, KILN_CAP_CHANGE_REMOVED});
            g_generation.fetch_add(1, std::memory_order_acq_rel);
        }
        g_names.erase(it);
        return;
    }
    const Report* w = &n.reports[0];
    for (const Report& r : n.reports)
        if (shows_over(r, *w)) w = &r;
    Shown s{w->state, w->reason, g_session.count(it->first) ? static_cast<uint32_t>(KILN_CAP_FLAG_SESSION) : 0u, w->text,
            w->source};
    if (n.shown && s == n.last) return;
    n.shown = true;
    n.last = s;
    ev.push_back({it->first, std::move(s), KILN_CAP_CHANGE_SET});
    g_generation.fetch_add(1, std::memory_order_acq_rel);
}

void drop_owner_locked(KilnOwner owner, std::vector<Event>& ev) {
    for (auto it = g_names.begin(); it != g_names.end();) {
        const auto next = std::next(it);
        auto& reports = it->second.reports;
        const size_t before = reports.size();
        bool published_gone = false;
        std::erase_if(reports, [&](const Report& r) {
            if (r.owner == owner) return true;
            if (r.owner == 0 && r.tied == owner) return published_gone = true;
            return false;
        });
        if (published_gone) {  // a service DLL of that name that did not make it shows again
            const auto f = g_dll_failed.find(it->first);
            if (f != g_dll_failed.end()) {
                reports.push_back(f->second);
                reports.back().seq = ++g_seq;
            }
        }
        if (reports.size() != before || published_gone) refresh_locked(it, ev);
        it = next;
    }
    g_owner_reports.erase(owner);
    std::erase_if(g_subs, [&](const std::shared_ptr<Sub>& s) {
        if (s->owner != owner) return false;
        s->live.store(false, std::memory_order_release);
        return true;
    });
}

// The net: reports and subscribers of owners the registry of owners no longer knows.
void prune_dead_locked(std::vector<Event>& ev) {
    const uint64_t v = reg::version();
    if (v == g_reg_checked) return;
    g_reg_checked = v;
    std::set<KilnOwner> owners;
    for (const auto& [name, n] : g_names)
        for (const Report& r : n.reports) {
            if (r.owner) owners.insert(r.owner);
            if (r.tied) owners.insert(r.tied);
        }
    for (const auto& s : g_subs) owners.insert(s->owner);
    for (const KilnOwner o : owners)
        if (!reg::owner_alive(o)) drop_owner_locked(o, ev);
}

// Sets the core's own report for `name`, unless the core reports it already from a list that takes precedence.
void set_core_locked(const std::string& name, int kind, uint32_t state, uint32_t reason, std::string_view text,
                     std::string_view source, KilnOwner tied, std::vector<Event>& ev) {
    auto it = g_names.find(name);
    if (it != g_names.end()) {
        if (Report* c = core_report(it->second)) {
            if (kind < c->kind) return;
            c->kind = kind;
            c->tied = tied;
            c->state = state;
            c->reason = reason;
            c->text = cut(text, KILN_CAP_TEXT_MAX);
            c->source = cut(source, kSourceMax);
            refresh_locked(it, ev);
            return;
        }
    } else {
        it = g_names.emplace(name, Name{}).first;
    }
    Report r;
    r.tied = tied;
    r.kind = kind;
    r.seq = ++g_seq;
    r.state = state;
    r.reason = reason;
    r.text = cut(text, KILN_CAP_TEXT_MAX);
    r.source = cut(source, kSourceMax);
    it->second.reports.push_back(std::move(r));
    refresh_locked(it, ev);
}

// ---- change delivery ----
// Holds no C++ object with a destructor, as __try requires (guard.cpp's pattern).
bool call_guarded(KilnCapsChangedFn fn, const KilnCapRow* row, uint32_t change, void* user, unsigned long* code) {
    crash::Capture cap{};
    static_cast<void>(code);  // host tests: their __except stand-in does not use it
    __try {
        fn(row, change, user);
        return true;
    } __except (crash::filter(GetExceptionInformation(), &cap, code)) {
        crash::report(cap, "kiln.caps change subscriber", reinterpret_cast<const void*>(fn));
        return false;
    }
}

KilnCapRow make_row(const std::string& name, const Shown& s) {
    KilnCapRow row{};
    row.size = sizeof(KilnCapRow);
    row.state = s.state;
    row.reason = s.reason;
    row.flags = s.flags;
    row.name = kstr(name);
    row.text = kstr(s.text);
    row.source = kstr(s.source);
    return row;
}

void mark_dirty();

// After the lock is released: the file is dirty, and every live subscriber gets every change.
void finish(const std::vector<Event>& ev, const Subs& subs) {
    if (ev.empty()) return;
    mark_dirty();
    for (const Event& e : ev) {
        const KilnCapRow row = make_row(e.name, e.row);
        for (const auto& s : subs) {
            if (!s->live.load(std::memory_order_acquire)) continue;
            unsigned long code = 0;
            if (call_guarded(s->fn, &row, e.change, s->user, &code)) continue;
            s->live.store(false, std::memory_order_release);
            {
                std::lock_guard lock(g_mutex);
                std::erase_if(g_subs, [&](const std::shared_ptr<Sub>& x) { return x == s; });
            }
            KLOG_E("kiln.caps: a change subscriber of '%s' faulted (0x%08lx); it was removed",
                   reg::owner_name(s->owner).c_str(), code);
        }
    }
}

// One registry operation: the liveness net first, then `body` under the lock, then delivery.
template <class F>
auto op(F&& body) {
    std::vector<Event> ev;
    Subs subs;
    struct Done {
        std::vector<Event>& ev;
        Subs& subs;
        ~Done() {
            try {
                finish(ev, subs);
            } catch (...) {  // out of memory while delivering: the change stands, a subscriber missed it
            }
        }
    };
    Done done{ev, subs};
    std::lock_guard lock(g_mutex);
    prune_dead_locked(ev);
    auto result = body(ev);
    if (!ev.empty()) subs = g_subs;
    return result;
}
// Done's destructor runs after the lock_guard's (reverse order of declaration): delivery happens unlocked.

// ---- the core's rows ----
struct Verdict {
    uint32_t state, reason;
    std::string text;
};

bool is_tick_feature(std::string_view f) { return f.substr(0, 4) == "tick" || f == "world.lifecycle"; }

// The same answer as KilnApi.feature() (api_feature in api.cpp), with the reason.
Verdict feature_verdict(const std::string& f) {
    if (!g_hooks) return {KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_POLICY, g_safe_idle};
    if (!sigdb::feature(f)) {
        std::string name, text;
        bool constant = false;
        if (sigdb::feature_problem(f, &name, &text, &constant))
            return {KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_SYMBOL_MISSING,
                    std::string(constant ? "constant " : "symbol ") + name + ": " + text};
        return {KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_SYMBOL_MISSING, "not resolved on this game build"};
    }
    if (f.substr(0, 4) == "tick" && !tick::installed())
        return {KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_FAULTED,
                "the tick hook (on the sim step) is not installed or faulted; kiln.log says why"};
    if (f == "world.lifecycle" && !tick::lifecycle())
        return {KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_FAULTED,
                "the world-destroy hook (tick/WorldDestroy) is not installed or faulted; kiln.log says why"};
    return {KILN_CAP_AVAILABLE, KILN_CAP_REASON_NONE, {}};
}

uint8_t tick_bits() { return (tick::installed() ? 1 : 0) | (tick::lifecycle() ? 2 : 0); }

void set_feature_locked(const std::string& f, std::vector<Event>& ev) {
    const Verdict v = feature_verdict(f);
    set_core_locked(f, kFeature, v.state, v.reason, v.text, "sigdb", 0, ev);
}

// The tick hook faulted (or came back) since the rows were built: its rows follow.
void refresh_tick() {
    if (!g_rows_built.load(std::memory_order_acquire) || !g_hooks) return;
    const uint8_t bits = tick_bits();
    if (bits == g_tick_seen.load(std::memory_order_acquire)) return;
    op([&](std::vector<Event>& ev) {
        g_tick_seen.store(bits, std::memory_order_release);
        for (const std::string& f : g_feature_names)
            if (is_tick_feature(f)) set_feature_locked(f, ev);
        return 0;
    });
}

// Served in safe idle too (api_service special-cases them).
bool served_in_safe_idle(std::string_view name) {
    return name == KILN_PROFILE_SERVICE || name == KILN_PROGRESS_SERVICE || name == KILN_CAPS_SERVICE;
}

bool in_core_service(std::string_view name) {
    struct Find {
        std::string_view name;
        bool found;
    } f{name, false};
    svc::each([](const svc::Service& s, void* user) {
        auto* f = static_cast<Find*>(user);
        if (f->name == s.name) f->found = true;
    }, &f);
    return f.found;
}

// "kiln-items.dll" -> "kiln.items"; a character a name may not have becomes '_'. Empty when nothing is left.
std::string dll_row_name(const std::filesystem::path& file) {
    std::string stem = u8(file.stem());
    for (char& c : stem)
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    if (stem.rfind("kiln-", 0) == 0) stem.erase(0, 5);
    if (stem.empty()) return {};
    std::string name = "kiln.";
    for (const char c : stem) name += name_ok(std::string_view(&c, 1)) ? c : '_';
    return name.size() > KILN_CAP_NAME_MAX ? name.substr(0, KILN_CAP_NAME_MAX) : name;
}

// ---- the table ----
struct ThreadRow {  // at()/find(): the strings of the last row this thread read (trivial: no TLS destructor)
    char name[KILN_CAP_NAME_MAX];
    char text[KILN_CAP_TEXT_MAX];
    char source[kSourceMax];
};
thread_local ThreadRow t_row;

void fill(const std::string& name, const Shown& s, KilnCapRow* out) {
    const size_t nn = std::min(name.size(), sizeof t_row.name), tn = std::min(s.text.size(), sizeof t_row.text),
                 sn = std::min(s.source.size(), sizeof t_row.source);
    std::memcpy(t_row.name, name.data(), nn);
    std::memcpy(t_row.text, s.text.data(), tn);
    std::memcpy(t_row.source, s.source.data(), sn);
    out->state = s.state;
    out->reason = s.reason;
    out->flags = s.flags;
    out->name = KilnStr{t_row.name, nn};
    out->text = KilnStr{t_row.text, tn};
    out->source = KilnStr{t_row.source, sn};
}

uint32_t KILN_CALL c_count() {
    try {
        refresh_tick();
        return op([](std::vector<Event>&) { return static_cast<uint32_t>(g_names.size()); });
    } catch (...) {
        return 0;
    }
}

KilnResult KILN_CALL c_at(uint32_t index, KilnCapRow* out) {
    if (!out || out->size < sizeof(KilnCapRow)) return KILN_ERR_INVALID_ARGUMENT;
    try {
        refresh_tick();
        std::string name;
        Shown row;
        const auto rc = op([&](std::vector<Event>&) -> KilnResult {
            const uint64_t gen = g_generation.load(std::memory_order_acquire);
            if (g_order_gen != gen || g_order.size() != g_names.size()) {
                g_order.clear();
                for (const auto& [name, n] : g_names) g_order.push_back(&name);
                g_order_gen = gen;
            }
            if (index >= g_order.size()) return KILN_ERR_NOT_FOUND;
            const auto it = g_names.find(*g_order[index]);
            name = it->first;
            row = it->second.last;
            return KILN_OK;
        });
        // Delivery inside op() may reenter at/find on this thread and reuse t_row. Publish our copy only afterwards.
        if (rc == KILN_OK) fill(name, row, out);
        return rc;
    } catch (...) {
        return KILN_ERR_INTERNAL;
    }
}

KilnResult KILN_CALL c_find(KilnStr name, KilnCapRow* out) {
    if (!out || out->size < sizeof(KilnCapRow)) return KILN_ERR_INVALID_ARGUMENT;
    if (name.len && !name.ptr) return KILN_ERR_INVALID_ARGUMENT;
    try {
        refresh_tick();
        std::string found;
        Shown row;
        const auto rc = op([&](std::vector<Event>&) -> KilnResult {
            const auto it = g_names.find(sv(name));
            if (it == g_names.end()) return KILN_ERR_NOT_FOUND;
            found = it->first;
            row = it->second.last;
            return KILN_OK;
        });
        if (rc == KILN_OK) fill(found, row, out);
        return rc;
    } catch (...) {
        return KILN_ERR_INTERNAL;
    }
}

KilnResult KILN_CALL c_report(KilnOwner owner, KilnStr name, uint32_t state, uint32_t reason, KilnStr text) {
    if (owner == 0 || (name.len && !name.ptr) || (text.len && !text.ptr)) return KILN_ERR_INVALID_ARGUMENT;
    const std::string_view n = sv(name);
    if (!name_ok(n) || state > KILN_CAP_PENDING || reason == KILN_CAP_REASON_UNKNOWN) return KILN_ERR_INVALID_ARGUMENT;
    if ((state == KILN_CAP_AVAILABLE) != (reason == KILN_CAP_REASON_NONE)) return KILN_ERR_INVALID_ARGUMENT;
    try {
        refresh_tick();
        const std::string source = reg::owner_name(owner);
        std::string t = cut(sv(text), KILN_CAP_TEXT_MAX);
        return op([&](std::vector<Event>& ev) -> KilnResult {
            if (!reg::owner_alive(owner)) return KILN_ERR_REVOKED;
            auto it = g_names.find(n);
            Report* mine = nullptr;
            if (it != g_names.end()) {
                // a name the core reports itself is static; the client gate's report does not make it so
                const Report* core = core_report(it->second);
                if (state == KILN_CAP_PENDING && core && core->kind != kGate) return KILN_ERR_INVALID_ARGUMENT;
                for (Report& r : it->second.reports)
                    if (r.owner == owner) mine = &r;
            }
            if (!mine && g_owner_reports[owner] >= KILN_CAP_OWNER_MAX) return KILN_ERR_UNAVAILABLE;
            if (state == KILN_CAP_PENDING) g_session.emplace(n);
            if (it == g_names.end()) it = g_names.emplace(std::string(n), Name{}).first;
            if (!mine) {
                Report r;
                r.owner = owner;
                r.seq = ++g_seq;
                it->second.reports.push_back(std::move(r));
                mine = &it->second.reports.back();
                ++g_owner_reports[owner];
            }
            mine->state = state;
            mine->reason = reason;
            mine->text = std::move(t);
            mine->source = cut(source, kSourceMax);
            refresh_locked(it, ev);
            return KILN_OK;
        });
    } catch (...) {
        return KILN_ERR_INTERNAL;
    }
}

KilnResult KILN_CALL c_subscribe(KilnOwner owner, KilnCapsChangedFn fn, void* user, KilnHandle* out) {
    if (owner == 0 || !fn) return KILN_ERR_INVALID_ARGUMENT;
    try {
        return op([&](std::vector<Event>&) -> KilnResult {
            if (!reg::owner_alive(owner)) return KILN_ERR_REVOKED;
            auto s = std::make_shared<Sub>();
            s->handle = kHandleTag | g_next_handle++;
            s->owner = owner;
            s->fn = fn;
            s->user = user;
            g_subs.push_back(s);
            if (out) *out = s->handle;
            return KILN_OK;
        });
    } catch (...) {
        return KILN_ERR_INTERNAL;
    }
}

KilnResult KILN_CALL c_unsubscribe(KilnHandle handle) {
    try {
        return op([&](std::vector<Event>&) -> KilnResult {
            for (auto it = g_subs.begin(); it != g_subs.end(); ++it)
                if ((*it)->handle == handle) {
                    (*it)->live.store(false, std::memory_order_release);
                    g_subs.erase(it);
                    return KILN_OK;
                }
            return KILN_ERR_NOT_FOUND;
        });
    } catch (...) {
        return KILN_ERR_INTERNAL;
    }
}

uint64_t KILN_CALL c_generation() {
    try {
        refresh_tick();
        return op([](std::vector<Event>&) { return g_generation.load(std::memory_order_acquire); });
    } catch (...) {
        return g_generation.load(std::memory_order_acquire);
    }
}

const KilnCapsApi kTable = {
    {sizeof(KilnCapsApi), KILN_CAPS_VERSION},
    &c_count, &c_at, &c_find, &c_report, &c_subscribe, &c_unsubscribe, &c_generation,
};

// ---- capabilities.json ----
// g_dirty: a change that no finished, successful write holds yet (a failed write sets it again). g_in_flight: a write
// was taken (dirt cleared) and has not finished; one at a time, whoever takes it (boot, the writer thread, flush). The
// file is current exactly when neither is set.
std::mutex& g_wmutex = *new std::mutex;  // the writer's state below
std::condition_variable& g_wcv = *new std::condition_variable;    // the writer thread: dirt, or a write finished
std::condition_variable& g_wdone = *new std::condition_variable;  // flush(): a write in flight finished
bool g_dirty = false;
bool g_in_flight = false;
bool g_booted = false;  // boot_complete() ran: writes are allowed
double g_last_write = 0;  // when the last write was taken (also a failed one: retries keep the gap)
std::timed_mutex& g_file_mutex = *new std::timed_mutex;  // one writer of the file at a time
std::once_flag g_thread_once;
std::atomic<bool> g_write_failed_logged{false};

void mark_dirty() {
    {
        std::lock_guard lock(g_wmutex);
        g_dirty = true;
    }
    g_wcv.notify_one();
}

// The file's text (schema/state-capabilities.schema.json). Empty when the registry's lock was not had in time.
std::string render(std::chrono::milliseconds wait) {
    using json = nlohmann::ordered_json;
    std::vector<std::pair<std::string, Shown>> rows;
    uint64_t gen = 0;
    bool hooks = false, verified = false;
    std::string safe_idle;
    {
        std::unique_lock lock(g_mutex, std::defer_lock);
        if (!lock.try_lock_for(wait)) return {};
        rows.reserve(g_names.size());
        for (const auto& [name, n] : g_names) rows.emplace_back(name, n.last);
        gen = g_generation.load(std::memory_order_acquire);
        hooks = g_hooks;
        verified = g_verified;
        safe_idle = g_safe_idle;
    }
    json list = json::array();
    for (const auto& [name, s] : rows) {
        json r = {{"name", name},
                  {"state", s.state < 3 ? kStateName[s.state] : "unavailable"},
                  {"reason", reason_name(s.reason)},
                  {"reasonText", s.text},
                  {"source", s.source}};
        if (s.flags & KILN_CAP_FLAG_SESSION) r["session"] = true;
        list.push_back(std::move(r));
    }
    json doc = {
        {"format", 1},
        {"kiln", std::string(kVersion)},
        {"pid", static_cast<uint64_t>(GetCurrentProcessId())},
        {"written", utc_now()},
        {"side", side_name(image().side)},
        {"exe", {{"sha256", image().sha256}, {"verified", verified}}},
        {"hooks", hooks},
        {"safeIdle", hooks ? json(nullptr) : json(safe_idle)},
        {"generation", gen},
        {"capabilities", std::move(list)},
    };
    // texts come from plugins and file names: never let a bad UTF-8 byte stop the file
    return doc.dump(2, ' ', false, json::error_handler_t::replace) + "\n";
}

bool write_now(std::chrono::milliseconds wait) {
    std::string text;
    try {
        text = render(wait);
    } catch (const std::exception& e) {
        KLOG_W("state: capabilities.json not written: %s", e.what());
        return false;
    }
    if (text.empty()) return false;
    if (write_file_atomic(paths().state / L"capabilities.json", text)) {
        if (g_write_failed_logged.exchange(false)) KLOG_I("state: capabilities.json written again");
        return true;
    }
    // once per run of failures: the writer tries again each second while it fails (a reader holding the file under
    // Wine makes the replacing rename fail)
    if (!g_write_failed_logged.exchange(true))
        KLOG_W("state: cannot write %s; trying again each second", u8(paths().state / L"capabilities.json").c_str());
    return false;
}

// g_wmutex held: take the dirt for one write.
void take_write_locked() {
    g_dirty = false;
    g_in_flight = true;
    g_last_write = g_clock();
}

// g_wmutex held: the write taken ended. A failed one leaves the registry dirty, so it is tried again after the gap.
void end_write_locked(bool ok) {
    g_in_flight = false;
    if (!ok) g_dirty = true;
    g_wdone.notify_all();
    g_wcv.notify_all();
}

// One write taken under g_wmutex (held by `lk` on entry and on return), done with it released.
bool write_taken(std::unique_lock<std::mutex>& lk, std::chrono::milliseconds file_wait, std::chrono::milliseconds wait) {
    take_write_locked();
    lk.unlock();
    bool ok = false;
    try {
        std::unique_lock file(g_file_mutex, std::defer_lock);
        if (file.try_lock_for(file_wait)) ok = write_now(wait);
    } catch (...) {
    }
    lk.lock();
    end_write_locked(ok);
    return ok;
}

void writer_loop() {
    std::unique_lock lk(g_wmutex);
    for (;;) {
        // refresh_tick delivers changes and marks the file dirty: neither may run under the writer's lock.
        lk.unlock();
        try {
            refresh_tick();
        } catch (...) {
        }
        lk.lock();
        const auto ready = [] { return g_dirty && !g_in_flight; };
        if (!ready()) {
            if (g_hooks)
                g_wcv.wait_for(lk, std::chrono::seconds(1), ready);
            else
                g_wcv.wait(lk, [&] { return g_hooks || ready(); });
            continue;
        }
        const double wait = g_last_write + kMinGapMs - g_clock();
        if (wait > 0) {  // also after a failed write: the next try waits out the gap
            g_wcv.wait_for(lk, std::chrono::duration<double, std::milli>(wait));
            continue;
        }
        write_taken(lk, std::chrono::milliseconds(5000), std::chrono::milliseconds(5000));
    }
}

}  // namespace

const KilnCapsApi* table() { return &kTable; }

// Boot (boot.cpp), after sigdb, the tick hook and svc::init_all, before the service DLLs and plugins load; in safe idle
// and in the early exit too. `safe_idle`: boot()'s sentence for why hooks are off.
void core_rows(bool hooks, bool verified, const std::string& safe_idle) {
    std::vector<std::string> features = sigdb::features();
    std::sort(features.begin(), features.end());
    features.erase(std::unique(features.begin(), features.end()), features.end());
    struct Svc {
        std::string name;
        uint32_t version;
        bool available;
    };
    std::vector<Svc> services;
    svc::each([](const svc::Service& s, void* user) {
        static_cast<std::vector<Svc>*>(user)->push_back({s.name, s.version, s.available});
    }, &services);
    std::vector<std::filesystem::path> idle_dlls;  // safe idle: what kiln/services would have loaded
    if (!hooks) {
        std::error_code ec;
        for (auto& e : std::filesystem::directory_iterator(paths().kiln / L"services", ec)) {
            std::string ext = u8(e.path().extension());
            for (char& c : ext)
                if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
            if (ext == ".dll") idle_dlls.push_back(e.path());
        }
        std::sort(idle_dlls.begin(), idle_dlls.end());
    }
    op([&](std::vector<Event>& ev) {
        g_hooks = hooks;
        g_verified = verified;
        g_safe_idle = safe_idle;
        g_feature_names.clear();
        for (const std::string& f : features) {
            if (!name_ok(f)) {
                KLOG_W("kiln.caps: sigdb feature '%s' is not a capability name; no row", f.c_str());
                continue;
            }
            g_feature_names.push_back(f);
        }
        g_tick_seen.store(tick_bits(), std::memory_order_release);
        for (const std::string& f : g_feature_names) set_feature_locked(f, ev);
        // The client gate, after the feature rows and before anything else reports: kGate outranks every other kind.
        for (const gates::Held& h : gates::held()) {
            if (!name_ok(h.name)) continue;  // gen.py refuses such a name; a test list might not
            set_core_locked(h.name, kGate, KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_POLICY, h.text, "core", 0, ev);
        }
        for (const Svc& s : services) {
            if (!name_ok(s.name)) continue;
            const std::string v = "v" + std::to_string(s.version);
            if (served_in_safe_idle(s.name) || (hooks && s.available))
                set_core_locked(s.name, kService, KILN_CAP_AVAILABLE, KILN_CAP_REASON_NONE, v, "core", 0, ev);
            else if (hooks)
                set_core_locked(s.name, kService, KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_SERVICE_MISSING,
                                "service " + s.name + " " + v + " did not start (its init said no); kiln.log says why",
                                "core", 0, ev);
            else
                set_core_locked(s.name, kService, KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_POLICY,
                                "not started in safe idle: " + safe_idle, "core", 0, ev);
        }
        for (const auto& file : idle_dlls) {
            const std::string name = dll_row_name(file);
            if (name.empty()) continue;
            set_core_locked(name, kIdleDll, KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_POLICY,
                            u8(file.filename()) + " is not loaded in safe idle: " + safe_idle, "core", 0, ev);
        }
        g_rows_built.store(true, std::memory_order_release);
        return 0;
    });
}

// End of boot (every exit of boot()): the first write, on this thread, then the writer thread.
void boot_complete() {
    try {
        {
            std::unique_lock lk(g_wmutex);
            g_booted = true;
            // a second call (boot's catch blocks) may meet the writer's write: one at a time, bounded
            if (g_wdone.wait_for(lk, std::chrono::milliseconds(6000), [] { return !g_in_flight; }))
                write_taken(lk, std::chrono::milliseconds(5000), std::chrono::milliseconds(5000));  // failed: the thread retries
            else
                g_dirty = true;
        }
        std::call_once(g_thread_once, [] { std::thread(&writer_loop).detach(); });
    } catch (const std::exception& e) {
        KLOG_W("kiln.caps: %s; capabilities.json may stay as it is", e.what());
    } catch (...) {
    }
}

// A last write when something changed since the last one (the exit path), ignoring the gap. Any thread. Waits at most
// about 2 s: 1 s for a write in flight to end (and the file), 1 s for the registry. True only when the file holds the
// current state: nothing dirty and no write in flight, or its own write succeeded. A failed write stays dirty.
bool flush() {
    try {
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(1000);
        std::unique_lock lk(g_wmutex);
        if (!g_booted) return true;
        if (!g_wdone.wait_until(lk, deadline, [] { return !g_in_flight; })) return false;  // the writer's, still going
        if (!g_dirty) return true;  // the last write succeeded (a failed one leaves the dirt)
        const auto left = std::chrono::duration_cast<std::chrono::milliseconds>(deadline - std::chrono::steady_clock::now());
        return write_taken(lk, std::max(left, std::chrono::milliseconds(0)), std::chrono::milliseconds(1000));
    } catch (...) {
        return false;
    }
}

// The owner was revoked (api_owner_revoke, load_one): its reports, its subscribers and the rows tied to it go.
void owner_gone(KilnOwner owner) {
    if (owner == 0) return;
    try {
        op([&](std::vector<Event>& ev) {
            drop_owner_locked(owner, ev);
            return 0;
        });
    } catch (...) {
    }
}

// KilnApi.feature() answered false (rare): the tick rows follow a hook that faulted since boot.
void feature_off(std::string_view) {
    try {
        refresh_tick();
    } catch (...) {
    }
}

// KilnApi.register_service accepted `name` from `owner` (also after boot).
void service_published(KilnOwner owner, std::string_view name, uint32_t version) {
    if (!name_ok(name) || in_core_service(name)) return;  // an in-core service of that name wins (api_service)
    try {
        const std::string source = reg::owner_name(owner);
        op([&](std::vector<Event>& ev) {
            if (!reg::owner_alive(owner)) return 0;
            set_core_locked(std::string(name), kPublished, KILN_CAP_AVAILABLE, KILN_CAP_REASON_NONE,
                            "v" + std::to_string(version), source, owner, ev);
            return 0;
        });
    } catch (...) {
    }
}

// load_one's record() for a service DLL (boot.json lists plugins only): one that did not make it gets a row.
void service_dll(const std::filesystem::path& file, std::string_view status, const std::string& error) {
    if (status == "loaded") return;
    const std::string name = dll_row_name(file);
    if (name.empty()) return;
    try {
        // load_one's own words: the fault branch is the one whose error says "faulted"
        const bool faulted = error.rfind("kiln_plugin_main faulted", 0) == 0;
        std::string text = u8(file.filename()) + " " + std::string(status) + ": " + error;
        if (status == "skipped") text += "; kiln.log says why";
        op([&](std::vector<Event>& ev) {
            Report r;
            r.kind = kDll;
            r.state = KILN_CAP_UNAVAILABLE;
            r.reason = faulted ? KILN_CAP_REASON_FAULTED : KILN_CAP_REASON_SERVICE_MISSING;
            r.text = cut(text, KILN_CAP_TEXT_MAX);
            r.source = "core";
            g_dll_failed[name] = r;
            set_core_locked(name, kDll, r.state, r.reason, r.text, r.source, 0, ev);
            return 0;
        });
    } catch (...) {
    }
}

}  // namespace kiln::caps

KILN_SERVICE(caps, KILN_CAPS_SERVICE, KILN_CAPS_VERSION, &kiln::caps::kTable, nullptr);
