// SPDX-License-Identifier: MIT
// Host tests of the capability registry (src/services/caps.cpp, kiln.caps v1): the report rules, subscribers, the
// core's rows from a real resolver run (the test PE of sigdisk_test.cpp), the file's JSON and its once-a-second writer.
// Run by test-caps.sh (ASan/UBSan). The owner registry, the tick hook, paths and the file write are stubs below.
#include <windows.h>  // tests/stubs/windows.h
struct EXCEPTION_POINTERS;
inline unsigned long GetCurrentProcessId() { return 4242; }
#ifndef _WIN32
#define __try try
#define __except(x) catch (...)
#endif
#define main sigdisk_helpers_main
#include "sigdisk_test.cpp"
#undef main
#include "../src/sigdb.cpp"
#include "../src/services/service.cpp"
#include "../src/gates.cpp"  // kiln_gates.g.h: test-caps.sh generates it from native/core/gates
#include "../src/sfi_release_policy.cpp"  // the release policy's rows gates.cpp adds
#include "../src/config.cpp"
#include "../src/services/caps.cpp"
#include "../src/services/client_building_input.cpp"  // the building input row asks it whether its producer is bound
#include "../src/services/client_crosshair.cpp"  // the crosshair row asks it whether its producer is ready or bound
#include "../src/services/client_game_input.cpp"  // the game input row asks it whether its hooks are ready or in

#include <cstdarg>
#include <barrier>
#include <condition_variable>
#include <future>

// ---- stubs ----
namespace {
std::mutex t_mutex;  // the stubs' state below
std::map<KilnOwner, std::string> t_owners;
KilnOwner t_next_owner = 1;
std::atomic<uint64_t> t_version{1};
std::atomic<bool> t_tick{true}, t_lifecycle{true}, t_wait{true};
std::vector<std::string> t_errors;  // KLOG_E lines
std::vector<std::string> t_writes;  // texts write_file_atomic wrote (successful writes only)
std::vector<double> t_attempts;     // the fake clock at every call of write_file_atomic, failed ones too
int t_fail_next = 0;                // the next n calls of write_file_atomic fail (as a rename under Wine while a
                                    // reader holds the file open)
bool t_block = false;               // write_file_atomic waits until t_block is false again (a write in flight)
bool t_in_write = false;            // a write_file_atomic call is inside its wait
std::condition_variable t_write_cv;
std::vector<std::string> t_infos;   // KLOG_I / KLOG_W lines
std::filesystem::path t_last_file;
kiln::Paths t_paths;
double t_now = 0;
std::mutex t_clock_mutex;
// Each set_clock starts a new epoch; a read of the clock by another thread than main's (the writer thread: flush and
// boot_complete run on main here) marks the epoch seen. The writer reads it only under caps::g_wmutex, to measure its
// gap and to take a write, so a seen epoch tells the test that the writer looked at the time set last. The same for
// the writer's reads of the tick hook's state (refresh_tick, at the top of each of its rounds).
const std::thread::id t_main = std::this_thread::get_id();  // static initialisation runs on main's thread
uint64_t t_clock_epoch = 0, t_seen_epoch = 0;  // under t_clock_mutex
uint64_t t_tick_epoch = 0, t_tick_seen = 0;    // under t_clock_mutex
double fake_clock() {
    std::lock_guard l(t_clock_mutex);
    if (std::this_thread::get_id() != t_main) t_seen_epoch = t_clock_epoch;
    return t_now;
}
void tick_read_noted() {
    std::lock_guard l(t_clock_mutex);
    if (std::this_thread::get_id() != t_main) t_tick_seen = t_tick_epoch;
}
void set_clock(double v) {
    {
        std::lock_guard l(t_clock_mutex);
        t_now = v;
        ++t_clock_epoch;
    }
    kiln::caps::g_wcv.notify_all();  // the writer looks again (a real wake-up would come from its own timeout)
}
// The bound of a wait that must end. Generous: the shared build machine can be loaded far beyond its cores, and a
// passing wait returns as soon as its condition holds.
constexpr std::chrono::seconds kMustEnd{300};
size_t writes() {
    std::lock_guard l(t_mutex);
    return t_writes.size();
}
std::string last_write() {
    std::lock_guard l(t_mutex);
    return t_writes.empty() ? std::string() : t_writes.back();
}
}  // namespace

namespace kiln {
ImageInfo test_image;
const ImageInfo& image() { return test_image; }
double now_ms() { return 0; }
std::string to_utf8(std::wstring_view w) { return std::string(w.begin(), w.end()); }
std::wstring to_wide(std::string_view s) { return std::wstring(s.begin(), s.end()); }
const char* side_name(uint32_t side) { return side == KILN_SIDE_CLIENT ? "client" : "server"; }
const Paths& paths() { return t_paths; }
std::string utc_now() { return "2026-11-11T22:40:00Z"; }
bool write_file_atomic(const std::filesystem::path& file, std::string_view text) {
    std::unique_lock l(t_mutex);
    t_attempts.push_back(fake_clock());
    if (t_block) {
        t_in_write = true;
        t_write_cv.notify_all();
        t_write_cv.wait(l, [] { return !t_block; });
        t_in_write = false;
    }
    if (t_fail_next > 0) {
        --t_fail_next;
        return false;
    }
    t_writes.emplace_back(text);
    t_last_file = file;
    return true;
}
namespace log {
void set_level(int) {}
void writef(int level, const char*, const char* fmt, ...) {
    char b[512];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(b, sizeof b, fmt, ap);
    va_end(ap);
    std::lock_guard l(t_mutex);
    (level == KILN_LOG_ERROR ? t_errors : t_infos).emplace_back(b);
}
}  // namespace log
namespace reg {
std::string owner_name(KilnOwner owner) {
    if (owner == 0) return "kiln";
    std::lock_guard l(t_mutex);
    const auto it = t_owners.find(owner);
    return it == t_owners.end() ? "(revoked)" : it->second;
}
bool owner_alive(KilnOwner owner) {
    if (owner == 0) return true;
    std::lock_guard l(t_mutex);
    return t_owners.count(owner) != 0;
}
uint64_t version() { return t_version.load(); }
}  // namespace reg
namespace tick {
bool installed() {
    tick_read_noted();
    return t_tick;
}
bool lifecycle() { return t_lifecycle; }
bool waits_jobs() { return t_wait; }
StopDrain stop_drain(uint32_t kind) { return stop_drain_of(kind, image().side, t_tick, t_lifecycle, t_wait); }
}  // namespace tick
namespace crash {
int filter(EXCEPTION_POINTERS*, Capture*, unsigned long*) { return 1; }
void report(const Capture&, const char*, const void*) {}
}  // namespace crash
}  // namespace kiln

// In-core services the core rows are built from (besides kiln.caps itself).
namespace {
const KilnServiceHeader kFakeTable{sizeof(KilnServiceHeader), 3};
bool init_yes() { return true; }
bool init_no() { return false; }
}  // namespace
KILN_SERVICE(fakeok, "kiln.fakeok", 3, &kFakeTable, &init_yes);
KILN_SERVICE(fakeno, "kiln.fakeno", 2, &kFakeTable, &init_no);
KILN_SERVICE(fakehooks, "kiln.hooks", 2, &kFakeTable, &init_yes);
KILN_SERVICE(fakeclient, "kiln.client", 1, &kFakeTable, &init_yes);
KILN_SERVICE(fakecrosshair, "kiln.client.crosshair", 1, &kFakeTable, &init_yes);
KILN_SERVICE(fakegameinput, "kiln.client.game_input", 1, &kFakeTable, &init_yes);

namespace {
namespace caps = kiln::caps;
const KilnCapsApi* T = caps::table();

KilnOwner owner(const std::string& name) {
    std::lock_guard l(t_mutex);
    const KilnOwner o = t_next_owner++;
    t_owners[o] = name;
    return o;
}
// The owner goes from the owner registry only (as native_mods.cpp and early_plugins.cpp revoke): the net must see it.
void kill(KilnOwner o) {
    {
        std::lock_guard l(t_mutex);
        t_owners.erase(o);
    }
    ++t_version;
}
// The owner goes the way api_owner_revoke does it.
void revoke(KilnOwner o) {
    kill(o);
    caps::owner_gone(o);
}

void reset() {
    std::lock_guard l(caps::g_mutex);
    caps::g_names.clear();
    caps::g_session.clear();
    caps::g_owner_reports.clear();
    caps::g_dll_failed.clear();
    caps::g_fragment_rows.clear();
    for (auto& s : caps::g_subs) s->live = false;
    caps::g_subs.clear();
    caps::g_generation = 1;
    caps::g_order.clear();
    caps::g_order_gen = 0;
    caps::g_rows_built = false;
    caps::g_gates_built = false;
    caps::g_hooks = false;
    caps::g_verified = false;
    caps::g_safe_idle.clear();
    caps::g_feature_names.clear();
    caps::g_reg_checked = 0;
}

KilnStr ks(const std::string& s) { return KilnStr{s.data(), s.size()}; }

KilnResult rep(KilnOwner o, const std::string& name, uint32_t state, uint32_t reason, const std::string& text = "") {
    return T->report(o, ks(name), state, reason, ks(text));
}

struct Got {
    KilnResult rc = KILN_ERR_INTERNAL;
    uint32_t state = 99, reason = 99, flags = 99;
    std::string name, text, source;
};
Got get(const std::string& name) {
    KilnCapRow r{};
    r.size = sizeof r;
    Got g;
    g.rc = T->find(ks(name), &r);
    if (g.rc != KILN_OK) return g;
    g.state = r.state;
    g.reason = r.reason;
    g.flags = r.flags;
    g.name.assign(r.name.ptr, r.name.len);
    g.text.assign(r.text.ptr, r.text.len);
    g.source.assign(r.source.ptr, r.source.len);
    return g;
}
bool is(const std::string& name, uint32_t state, uint32_t reason) {
    const Got g = get(name);
    return g.rc == KILN_OK && g.state == state && g.reason == reason;
}

// A subscriber that records every change it gets.
struct Seen {
    std::mutex m;
    std::vector<std::tuple<std::string, uint32_t, uint32_t, std::string>> events;  // name, change, state, text
    size_t size() {
        std::lock_guard l(m);
        return events.size();
    }
    std::set<std::string> names() {
        std::lock_guard l(m);
        std::set<std::string> out;
        for (const auto& e : events) out.insert(std::get<0>(e));
        return out;
    }
};
void KILN_CALL record_change(const KilnCapRow* row, uint32_t change, void* user) {
    auto* s = static_cast<Seen*>(user);
    std::lock_guard l(s->m);
    s->events.emplace_back(std::string(row->name.ptr, row->name.len), change, row->state,
                           std::string(row->text.ptr, row->text.len));
}

constexpr uint32_t A = KILN_CAP_AVAILABLE, U = KILN_CAP_UNAVAILABLE, P = KILN_CAP_PENDING;

void rules() {
    reset();
    const KilnOwner a = owner("mod:a"), b = owner("mod:b"), c = owner("mod:c");
    Seen seen;
    KilnHandle h = 0;
    const KilnOwner watcher = owner("host");
    CHECK(T->subscribe(watcher, &record_change, &seen, &h) == KILN_OK && (h & caps::kHandleTag),
          "subscribe: a handle of kiln.caps' own");
    CHECK(T->generation() == 1 && T->count() == 0, "generation: starts at 1 with no rows");

    // worst report wins
    CHECK(rep(a, "x.one", A, KILN_CAP_REASON_NONE) == KILN_OK && is("x.one", A, KILN_CAP_REASON_NONE) &&
              get("x.one").source == "mod:a" && T->generation() == 2,
          "report: a new row, source is the owner's name, one change");
    CHECK(rep(b, "x.one", U, KILN_CAP_REASON_POLICY, "b says off") == KILN_OK && is("x.one", U, KILN_CAP_REASON_POLICY) &&
              get("x.one").text == "b says off" && get("x.one").source == "mod:b",
          "worst wins: unavailable over available");
    CHECK(rep(a, "x.one", P, KILN_CAP_REASON_SESSION, "waits") == KILN_OK && is("x.one", U, KILN_CAP_REASON_POLICY) &&
              get("x.one").flags == KILN_CAP_FLAG_SESSION,
          "worst wins: unavailable over pending; the name is flagged session at once");
    revoke(b);
    CHECK(is("x.one", P, KILN_CAP_REASON_SESSION) && get("x.one").text == "waits", "owner gone: the next worst shows");
    CHECK(rep(a, "x.one", A, KILN_CAP_REASON_NONE, "v2") == KILN_OK && is("x.one", A, KILN_CAP_REASON_NONE) &&
              get("x.one").flags == KILN_CAP_FLAG_SESSION,
          "replace: the owner's own report is replaced; the session flag stays");
    const size_t before = seen.size();
    kill(a);  // the net: a is gone from the owner registry, nobody told kiln.caps
    CHECK(get("x.one").rc == KILN_ERR_NOT_FOUND && T->count() == 0, "net: a dead owner's reports go; no report, no row");
    CHECK(seen.size() == before + 1 && std::get<1>(seen.events.back()) == KILN_CAP_CHANGE_REMOVED &&
              std::get<3>(seen.events.back()) == "v2",
          "removed: one REMOVED change carrying what the row said last");
    CHECK(rep(c, "x.one", A, KILN_CAP_REASON_NONE) == KILN_OK && get("x.one").flags == KILN_CAP_FLAG_SESSION,
          "session flag: sticky for the process, also after the row was removed and came back");

    // ties
    const KilnOwner d = owner("mod:d"), e = owner("mod:e");
    rep(d, "t.tie", U, KILN_CAP_REASON_FAULTED, "d first");
    rep(e, "t.tie", U, KILN_CAP_REASON_POLICY, "e second");
    CHECK(get("t.tie").text == "d first", "ties: among equals the oldest report shows");
    rep(d, "t.tie", U, KILN_CAP_REASON_FAULTED, "d again");
    CHECK(get("t.tie").text == "d again", "ties: an owner that replaces its report keeps its place");
    rep(d, "kiln.yy", U, KILN_CAP_REASON_POLICY, "older, from a mod");
    caps::service_dll("kiln-yy.dll", "failed", "LoadLibrary failed (126)");
    CHECK(get("kiln.yy").source == "core" && get("kiln.yy").reason == KILN_CAP_REASON_SERVICE_MISSING,
          "ties: among equals the core's report shows, even when a mod's is older");
    CHECK(rep(d, "kiln.yy", P, KILN_CAP_REASON_SESSION) == KILN_ERR_INVALID_ARGUMENT &&
              rep(d, "kiln.yy", U, KILN_CAP_REASON_FAULTED) == KILN_OK && get("kiln.yy").flags == 0,
          "core names are static: pending refused (and no session flag), unavailable allowed");
    caps::op([&](std::vector<caps::Event>& ev) {
        caps::set_core_locked("events.death", caps::kFeature, A, KILN_CAP_REASON_NONE, "resolved", "sigdb", 0, ev);
        return KILN_OK;
    });
    CHECK(rep(d, "events.death", P, KILN_CAP_REASON_SESSION, "wait for Pre") == KILN_OK &&
          is("events.death", P, KILN_CAP_REASON_SESSION), "decision hook row can wait for safe installation");
    CHECK(rep(d, "events.death", A, KILN_CAP_REASON_NONE) == KILN_OK && is("events.death", A, KILN_CAP_REASON_NONE),
          "decision hook row becomes available after installation");
    rep(d, "t.pend", P, KILN_CAP_REASON_SESSION);
    rep(e, "t.pend", A, KILN_CAP_REASON_NONE);
    CHECK(is("t.pend", P, KILN_CAP_REASON_SESSION), "worst wins: pending over available");

    // no change, no event
    const uint64_t g0 = T->generation();
    const size_t n0 = seen.size();
    CHECK(rep(d, "t.tie", U, KILN_CAP_REASON_FAULTED, "d again") == KILN_OK && T->generation() == g0 && seen.size() == n0,
          "no change: the same report again raises nothing and keeps the generation");
    CHECK(rep(e, "t.tie", U, KILN_CAP_REASON_POLICY, "e changed its mind") == KILN_OK && T->generation() == g0 &&
              seen.size() == n0,
          "no change: a report under the shown one raises nothing");
    rep(d, "t.tie", U, KILN_CAP_REASON_FAULTED, "d third");
    CHECK(T->generation() == g0 + 1 && seen.size() == n0 + 1, "generation: one visible change, one step");

    // limits
    const KilnOwner f = owner("mod:f");
    bool all = true;
    for (uint32_t i = 0; i < KILN_CAP_OWNER_MAX; ++i) all = all && rep(f, "f.n" + std::to_string(i), A, 0) == KILN_OK;
    CHECK(all && rep(f, "f.over", A, 0) == KILN_ERR_UNAVAILABLE && get("f.over").rc == KILN_ERR_NOT_FOUND,
          "limits: KILN_CAP_OWNER_MAX reports per owner, the next new name refused");
    CHECK(rep(f, "f.n0", U, KILN_CAP_REASON_FAULTED) == KILN_OK && rep(d, "f.over", A, 0) == KILN_OK,
          "limits: replacing a held report is fine; the limit is per owner");
    const std::string euro = "\xE2\x82\xAC";
    rep(d, "t.text", U, KILN_CAP_REASON_POLICY, std::string(KILN_CAP_TEXT_MAX - 1, 'a') + euro);
    const std::string cut1 = get("t.text").text;
    rep(d, "t.text", U, KILN_CAP_REASON_POLICY, std::string(KILN_CAP_TEXT_MAX - 3, 'a') + euro + "zz");
    const std::string cut2 = get("t.text").text;
    rep(d, "t.text", U, KILN_CAP_REASON_POLICY, std::string(KILN_CAP_TEXT_MAX + 100, 'b'));
    const std::string cut3 = get("t.text").text;
    CHECK(cut1 == std::string(KILN_CAP_TEXT_MAX - 1, 'a') && cut2 == std::string(KILN_CAP_TEXT_MAX - 3, 'a') + euro &&
              cut3 == std::string(KILN_CAP_TEXT_MAX, 'b'),
          "limits: text cut at KILN_CAP_TEXT_MAX on a UTF-8 boundary (%zu, %zu, %zu bytes)", cut1.size(), cut2.size(),
          cut3.size());

    // bad arguments
    const std::string long_name(KILN_CAP_NAME_MAX + 1, 'n');
    CHECK(rep(d, "", A, 0) == KILN_ERR_INVALID_ARGUMENT && rep(d, "has space", A, 0) == KILN_ERR_INVALID_ARGUMENT &&
              rep(d, "uml\xC3\xA4ut", A, 0) == KILN_ERR_INVALID_ARGUMENT &&
              rep(d, long_name, A, 0) == KILN_ERR_INVALID_ARGUMENT &&
              rep(d, std::string(KILN_CAP_NAME_MAX, 'n'), A, 0) == KILN_OK &&
              rep(d, "Ok.name/with-all_09", A, 0) == KILN_OK,
          "bad arguments: names are 1 to 96 bytes of [A-Za-z0-9._/-]");
    CHECK(T->report(d, KilnStr{nullptr, 3}, A, 0, KilnStr{}) == KILN_ERR_INVALID_ARGUMENT &&
              T->report(d, ks("t.null"), U, KILN_CAP_REASON_POLICY, KilnStr{nullptr, 2}) == KILN_ERR_INVALID_ARGUMENT &&
              T->report(d, ks("t.null"), U, KILN_CAP_REASON_POLICY, KilnStr{nullptr, 0}) == KILN_OK,
          "bad arguments: a null pointer with a length; a null empty text is fine");
    CHECK(rep(d, "t.bad", 3, KILN_CAP_REASON_POLICY) == KILN_ERR_INVALID_ARGUMENT &&
              rep(d, "t.bad", U, KILN_CAP_REASON_NONE) == KILN_ERR_INVALID_ARGUMENT &&
              rep(d, "t.bad", A, KILN_CAP_REASON_POLICY) == KILN_ERR_INVALID_ARGUMENT &&
              rep(d, "t.bad", U, KILN_CAP_REASON_UNKNOWN) == KILN_ERR_INVALID_ARGUMENT &&
              rep(0, "t.bad", U, KILN_CAP_REASON_POLICY) == KILN_ERR_INVALID_ARGUMENT &&
              rep(b, "t.bad", U, KILN_CAP_REASON_POLICY) == KILN_ERR_REVOKED && get("t.bad").rc == KILN_ERR_NOT_FOUND,
          "bad arguments: state, NONE exactly with available, UNKNOWN, owner 0; a revoked owner");
    CHECK(rep(d, "t.future", U, 11, "a newer code") == KILN_OK, "reason codes after UNKNOWN are kept (newer callers)");
    KilnCapRow small{};
    small.size = sizeof(KilnCapRow) - 1;
    KilnCapRow out{};
    out.size = sizeof out;
    out.state = 77;
    CHECK(T->find(ks("t.tie"), nullptr) == KILN_ERR_INVALID_ARGUMENT && T->find(ks("t.tie"), &small) == KILN_ERR_INVALID_ARGUMENT &&
              T->at(0, nullptr) == KILN_ERR_INVALID_ARGUMENT && T->at(0, &small) == KILN_ERR_INVALID_ARGUMENT &&
              T->find(ks("no.such"), &out) == KILN_ERR_NOT_FOUND && out.state == 77 &&
              T->at(T->count(), &out) == KILN_ERR_NOT_FOUND,
          "bad arguments: at/find check out and its size; NOT_FOUND leaves out alone; at past the end");

    // sorted walk
    for (const char* n : {"b.z", "B.a", "a.m", "_x", "-y", "9.n", "a/b", "a.mm"}) rep(e, n, A, 0);
    const uint64_t g1 = T->generation();
    const uint32_t count = T->count();
    std::vector<std::string> names;
    for (uint32_t i = 0; i < count; ++i) {
        KilnCapRow r{};
        r.size = sizeof r;
        if (T->at(i, &r) == KILN_OK) names.emplace_back(r.name.ptr, r.name.len);
    }
    bool sorted = names.size() == count && T->generation() == g1;
    for (size_t i = 1; i < names.size(); ++i) sorted = sorted && names[i - 1] < names[i];
    CHECK(sorted && std::find(names.begin(), names.end(), "-y") < std::find(names.begin(), names.end(), "B.a") &&
              std::find(names.begin(), names.end(), "B.a") < std::find(names.begin(), names.end(), "_x") &&
              std::find(names.begin(), names.end(), "_x") < std::find(names.begin(), names.end(), "a.m"),
          "walk: count/at give every row, sorted by name in byte order (%zu rows)", names.size());

    // strings per thread
    KilnCapRow mine{};
    mine.size = sizeof mine;
    T->find(ks("a.m"), &mine);
    std::promise<void> read, done;
    std::string other;
    std::thread t([&] {
        KilnCapRow r{};
        r.size = sizeof r;
        T->find(ks("b.z"), &r);
        other.assign(r.name.ptr, r.name.len);
        read.set_value();
        done.get_future().wait();
        other += std::string("|") + std::string(r.name.ptr, r.name.len);  // still its own row
    });
    read.get_future().wait();
    const std::string here(mine.name.ptr, mine.name.len);
    done.set_value();
    t.join();
    CHECK(here == "a.m" && other == "b.z|b.z", "strings: at/find copy into storage of the calling thread");
    KilnCapRow again{};
    again.size = sizeof again;
    T->find(ks("b.z"), &again);
    CHECK(std::string(mine.name.ptr, mine.name.len) == "b.z", "strings: valid until the same thread's next at/find");
    T->unsubscribe(h);
}

// Subscribers that call back into the registry, faulting subscribers, owners going.
struct Reentrant {
    std::atomic<int> calls{0};
    std::atomic<bool> found{false};
    KilnOwner owner = 0;
};
void KILN_CALL reenter(const KilnCapRow* row, uint32_t change, void* user) {
    auto* r = static_cast<Reentrant*>(user);
    ++r->calls;
    const std::string name(row->name.ptr, row->name.len);
    if (change == KILN_CAP_CHANGE_SET && name == "s.one") {
        r->found = get("s.one").rc == KILN_OK;                         // find() from inside: no deadlock
        rep(r->owner, "s.echo", KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_FAULTED, "from a subscriber");  // report() too
    }
}
void KILN_CALL throws(const KilnCapRow*, uint32_t, void*) { throw std::runtime_error("subscriber fault"); }

void subscribers() {
    reset();
    const KilnOwner m = owner("mod:m"), host = owner("host"), bad = owner("mod:bad"), gone = owner("mod:gone"),
                    net = owner("mod:net");
    Reentrant r;
    r.owner = m;
    Seen ok, of_gone, of_net;
    KilnHandle hr = 0, hb = 0, hg = 0, hn = 0, ho = 0;
    CHECK(T->subscribe(0, &record_change, &ok, &ho) == KILN_ERR_INVALID_ARGUMENT &&
              T->subscribe(host, nullptr, &ok, &ho) == KILN_ERR_INVALID_ARGUMENT,
          "subscribe: owner 0 and a null function refused");
    const KilnOwner dead = owner("mod:dead");
    kill(dead);
    CHECK(T->subscribe(dead, &record_change, &ok, &ho) == KILN_ERR_REVOKED, "subscribe: a revoked owner refused");
    T->subscribe(host, &reenter, &r, &hr);
    T->subscribe(bad, &throws, nullptr, &hb);
    T->subscribe(gone, &record_change, &of_gone, &hg);
    T->subscribe(net, &record_change, &of_net, &hn);
    T->subscribe(host, &record_change, &ok, &ho);
    CHECK(rep(m, "s.one", KILN_CAP_AVAILABLE, 0) == KILN_OK && r.found && is("s.echo", U, KILN_CAP_REASON_FAULTED),
          "subscribers: called outside the lock, find and report from inside work");
    CHECK(ok.size() == 2 && std::get<0>(ok.events[0]) == "s.echo" && std::get<0>(ok.events[1]) == "s.one",
          "subscribers: the change a subscriber caused reaches the others too (%zu changes)", ok.size());
    bool logged = false;
    {
        std::lock_guard l(t_mutex);
        for (const auto& line : t_errors) logged = logged || line.find("mod:bad") != std::string::npos;
    }
    CHECK(logged && T->unsubscribe(hb) == KILN_ERR_NOT_FOUND, "subscribers: one that faults is removed and logged");
    revoke(gone);
    kill(net);
    const size_t gone_seen = of_gone.size(), net_seen = of_net.size();
    rep(m, "s.two", KILN_CAP_AVAILABLE, 0);
    CHECK(of_gone.size() == gone_seen && of_net.size() == net_seen && ok.size() == 3,
          "subscribers: removed with their owner (revoke and the net)");
    CHECK(T->unsubscribe(hg) == KILN_ERR_NOT_FOUND && T->unsubscribe(hn) == KILN_ERR_NOT_FOUND,
          "subscribers: an owner's handles go with it");
    CHECK(T->unsubscribe(ho) == KILN_OK && T->unsubscribe(ho) == KILN_ERR_NOT_FOUND && T->unsubscribe(0) == KILN_ERR_NOT_FOUND,
          "unsubscribe: once");
    rep(m, "s.three", KILN_CAP_AVAILABLE, 0);
    CHECK(ok.size() == 3, "unsubscribe: no call after it");
    T->unsubscribe(hr);
}

void KILN_CALL nested_read(const KilnCapRow*, uint32_t, void* user) {
    auto* calls = static_cast<int*>(user);
    ++*calls;
    get("bb.nested");
    KilnCapRow row{};
    row.size = sizeof row;
    T->at(1, &row);
}

void reentrant_read_strings() {
    for (bool by_index : {false, true}) {
        reset();
        const KilnOwner host = owner("host"), dead = owner("dead");
        rep(host, "aa.outer", U, KILN_CAP_REASON_POLICY, "outer reason");
        rep(host, "bb.nested", A, 0, "nested");
        rep(dead, "zz.dead", A, 0);
        int calls = 0;
        KilnHandle h = 0;
        T->subscribe(host, &nested_read, &calls, &h);
        kill(dead);  // the outer read itself must prune and deliver, before returning its row
        KilnCapRow row{};
        row.size = sizeof row;
        const auto rc = by_index ? T->at(0, &row) : T->find(ks("aa.outer"), &row);
        CHECK(rc == KILN_OK && calls == 1 && std::string(row.name.ptr, row.name.len) == "aa.outer" &&
                  std::string(row.text.ptr, row.text.len) == "outer reason" &&
                  std::string(row.source.ptr, row.source.len) == "host" && row.state == U,
              "strings: outer %s survives subscriber at/find during its own change delivery", by_index ? "at" : "find");
        T->unsubscribe(h);
    }
}

void write(const std::filesystem::path& file, const char* text) {
    std::ofstream o(file);
    o << text;
}

// The test PE "loaded" at another base (sigdb_runtime_test.cpp's Loaded).
struct Loaded {
    TestPe pe;
    std::vector<uint8_t> live;
    Loaded() : live(0x3040, 0) {
        pe::FileText ft;
        std::string err;
        if (!pe::parse_file_text(pe.file.data(), pe.file.size(), ft, err)) std::abort();
        std::vector<uint8_t> text(ft.text_vsize, 0);
        std::memcpy(text.data(), pe.file.data() + ft.text_raw, ft.file_bytes());
        pe::relocate_text(text.data(), text.size(), ft, reinterpret_cast<uintptr_t>(live.data()));
        std::memcpy(live.data() + ft.text_rva, text.data(), text.size());
        auto& img = kiln::test_image;
        img = {};
        img.base = reinterpret_cast<uintptr_t>(live.data());
        img.size = ft.size_of_image;
        img.text = live.data() + ft.text_rva;
        img.text_size = ft.text_vsize;
        img.timestamp = ft.timestamp;
        img.file = pe.file.data();
        img.file_size = pe.file.size();
        img.sha256 = std::string(64, 'a');
        sigdb::g_code = {};
    }
};

// Definitions over the test PE: `root` is the function at kFunc, `tail` its last bytes with a constant.
const char* kDefs = R"({"format":1,
  "symbols":{
    "root":{"find":[{"pattern":"%ROOT%"}]},
    "tail":{"find":[{"pattern":"8B 81 {field:d32} C3"}]}
  },
  "features":{
    "items.fuel":{"symbols":["root"]},
    "net.kick":{"symbols":["tail"],"constants":["field"]},
    "tick":{"symbols":["tail"]},
    "tick.wait_jobs":{"symbols":["tail"]},
    "world.lifecycle":{"symbols":["tail"]}
  }})";

void resolve_with(const std::filesystem::path& dir, const std::string& root_pattern) {
    std::string defs = kDefs;
    defs.replace(defs.find("%ROOT%"), 6, root_pattern);
    const auto file = dir / "caps-server.json";
    write(file, defs.c_str());
    sigdb::g_core = sigdb::Resolver();
    CHECK(sigdb::load_and_resolve(file), "resolver: the definitions load");
    std::filesystem::remove(file);
}

void core_rows(const std::filesystem::path& dir) {
    Loaded x;
    kiln::svc::init_all();
    t_tick = t_lifecycle = true;

    // the brief's check: one symbol's pattern broken, the feature names it, the others stay available
    reset();
    resolve_with(dir, "48 B8 ?? ?? ?? ?? ?? ?? ?? ?? E8 {helper:rel32}");
    caps::core_rows(true, true, "");
    CHECK(get("kiln.internal.original-code").rc == KILN_OK && get("kiln.internal.original-code").state == KILN_CAP_AVAILABLE,
          "private service lookup has an honest capability row");
    bool all = true;
    for (const char* f : {"items.fuel", "net.kick", "tick", "tick.wait_jobs", "world.lifecycle"})
        all = all && is(f, A, KILN_CAP_REASON_NONE) && get(f).source == "sigdb";
    CHECK(all, "features: a resolver run with every symbol found gives every feature available, source sigdb");
    CHECK(is("hooks.read_original", A, 0) && is("hooks.code_state", A, 0),
          "hooks inspection: verified file and running service publish both static rows");
    const bool from_disk = sigdb::code().disk;
    sigdb::g_code.view.disk = false;
    caps::core_rows(true, true, "");
    CHECK(is("hooks.read_original", U, KILN_CAP_REASON_SYMBOL_MISSING) && is("hooks.code_state", U, KILN_CAP_REASON_SYMBOL_MISSING) &&
              get("hooks.code_state").text == "verified executable file code unavailable; live fallback is not original code",
          "hooks inspection: on sigdb's live-code fallback both rows are unavailable, symbol-missing (never faulted)");
    sigdb::g_code.view.disk = from_disk;
    kiln_service_fakehooks.available = false;
    caps::core_rows(true, true, "");
    CHECK(is("hooks.read_original", U, KILN_CAP_REASON_SERVICE_MISSING), "hooks inspection: missing service refuses");
    kiln_service_fakehooks.available = true;
    caps::core_rows(false, false, "test idle");
    CHECK(is("hooks.code_state", U, KILN_CAP_REASON_POLICY), "hooks inspection: safe idle policy refuses");
    static const kiln::gates::Gate inspection_gate[] = {
        {"hooks.code_state", "hooks-code", kiln::gates::kBoth}, {nullptr, nullptr, 0}};
    kiln::gates::use_list_for_tests(inspection_gate);
    kiln::gates::configure(KILN_SIDE_SERVER, false, {});
    reset();
    caps::core_rows(true, true, "");
    CHECK(is("hooks.code_state", U, KILN_CAP_REASON_POLICY) && is("hooks.read_original", A, 0),
          "hooks inspection: central hold overrides support without holding the other member");
    static const kiln::gates::Gate no_inspection_gate[] = {{nullptr, nullptr, 0}};
    kiln::gates::use_list_for_tests(no_inspection_gate);
    kiln::gates::configure(KILN_SIDE_SERVER, false, {});
    reset();
    caps::core_rows(true, true, "");
    // kiln.client's building input: the client only, kiln.client running, its producer bound
    {
        const uint32_t side = kiln::test_image.side;
        kiln::test_image.side = KILN_SIDE_CLIENT;
        kiln::building_input::reset_for_tests();
        caps::core_rows(true, true, "");
        const char* why = nullptr;
        kiln::building_input::bound(&why);
        CHECK(is("client.building_input", U, KILN_CAP_REASON_SYMBOL_MISSING) && get("client.building_input").text == why &&
                  get("client.building_input").source == "core",
              "building input: no producer bound on this build: unavailable, symbol-missing, with the producer's reason");
        static const kiln::building_input::Layout layout{760, 936, 500, 160, 0, 1, 2, 3, 4, 5, 7, 40};
        // the lazy producer: ready (an installer waits for the first request, nothing hooked) reads available
        kiln::building_input::set_installer(
            +[](kiln::building_input::Layout* l, const char**) { *l = layout; return kiln::building_input::Install::Bound; });
        caps::core_rows(true, true, "");
        CHECK(is("client.building_input", A, 0) &&
                  get("client.building_input").text.find("hooked at the first request") != std::string::npos,
              "building input: a ready producer on the client: available, hooked at the first request (%s)",
              get("client.building_input").text.c_str());
        // bound later (the first request's install): the row follows at the next registry call, no rebuild
        kiln::building_input::bind(&layout);
        CHECK(is("client.building_input", A, 0) &&
                  get("client.building_input").text == "the input producer is bound to this game build",
              "building input: bound after the rows were built: the row follows (%s)", get("client.building_input").text.c_str());
        // a first install that failed for good, through a submit after the rows were built: the row follows, faulted
        // (its hook could not be installed, not a symbol this build lacks), with the installer's reason
        kiln::building_input::reset_for_tests();
        kiln::building_input::set_installer(+[](kiln::building_input::Layout*, const char** why) {
            *why = "the test install failed";
            return kiln::building_input::Install::Failed;
        });
        caps::core_rows(true, true, "");
        {
            KilnBuildingInput undo{};
            undo.size = sizeof undo;
            undo.action = KILN_BUILDING_ACTION_UNDO;
            undo.player = 1;
            uint32_t id = 0;
            const kiln::building_input::Env env{true, true, true, 1, nullptr};
            CHECK(kiln::building_input::submit(env, 1, 0, &undo, &id) == KILN_ERR_UNAVAILABLE && id == 0,
                  "building input: a first submit whose install fails answers unavailable");
        }
        CHECK(is("client.building_input", U, KILN_CAP_REASON_FAULTED) &&
                  get("client.building_input").text == "the test install failed",
              "building input: a failed first install after the rows were built: unavailable, faulted, with its reason (%s)",
              get("client.building_input").text.c_str());
        kiln::building_input::bind(&layout);
        caps::core_rows(true, true, "");
        CHECK(is("client.building_input", A, 0), "building input: a bound producer on the client: available");
        kiln_service_fakeclient.available = false;
        caps::core_rows(true, true, "");
        CHECK(is("client.building_input", U, KILN_CAP_REASON_SERVICE_MISSING), "building input: kiln.client missing refuses");
        kiln_service_fakeclient.available = true;
        caps::core_rows(false, false, "test idle");
        CHECK(is("client.building_input", U, KILN_CAP_REASON_POLICY) && get("client.building_input").text == "not started in safe idle: test idle",
              "building input: safe idle says policy");
        kiln::test_image.side = KILN_SIDE_SERVER;
        caps::core_rows(true, true, "");
        CHECK(is("client.building_input", U, KILN_CAP_REASON_WRONG_SIDE), "building input: a dedicated server says wrong side");
        // the binder's reason (client.cpp: held, no local player, hooking failed) is the row's text while unbound
        kiln::test_image.side = KILN_SIDE_CLIENT;
        kiln::building_input::bind(nullptr);
        kiln::building_input::unbound_reason("hooking the local input observation point failed (kiln.log says why)");
        caps::core_rows(true, true, "");
        CHECK(is("client.building_input", U, KILN_CAP_REASON_SYMBOL_MISSING) &&
                  get("client.building_input").text == "hooking the local input observation point failed (kiln.log says why)",
              "building input: unbound, the row says the binder's reason");
        // held by a client gate row (the compiled list no longer holds it; the mechanism stays): the gate's row is what
        // a reader sees, bound or not; kiln.json "preview" naming it lifts it
        static const kiln::gates::Gate building_gate[] = {
            {"client.building_input", "building-input.select, building-input.joined", kiln::gates::kClient}, {nullptr, nullptr, 0}};
        kiln::gates::use_list_for_tests(building_gate);
        kiln::gates::configure(KILN_SIDE_CLIENT, false, {});
        kiln::building_input::bind(&layout);
        reset();
        caps::core_rows(true, true, "");
        CHECK(is("client.building_input", U, KILN_CAP_REASON_POLICY) &&
                  get("client.building_input").text.find("building-input.joined") != std::string::npos &&
                  kiln_caps_held(T, ks("client.building_input")) == 1,
              "building input: held by the client gate even with a bound producer (%s)", get("client.building_input").text.c_str());
        kiln::gates::configure(KILN_SIDE_CLIENT, false, {"client.building_input"});
        reset();
        caps::core_rows(true, true, "");
        CHECK(is("client.building_input", A, 0) && kiln_caps_held(T, ks("client.building_input")) == 0,
              "building input: preview naming it lifts the hold");
        kiln::gates::configure(KILN_SIDE_SERVER, false, {});
        CHECK(!kiln::gates::is_held("client.building_input"), "building input: the gate holds it on the client only");
        kiln::gates::use_list_for_tests(nullptr);  // the compiled list: since the hold was lifted it holds it nowhere
        kiln::gates::configure(KILN_SIDE_CLIENT, false, {});
        CHECK(!kiln::gates::is_held("client.building_input"), "building input: the compiled list does not hold it on the client");
        kiln::gates::configure(KILN_SIDE_SERVER, false, {});
        CHECK(!kiln::gates::is_held("client.building_input"), "building input: nor on a dedicated server");
        kiln::gates::use_list_for_tests(no_inspection_gate);
        kiln::gates::configure(KILN_SIDE_SERVER, false, {});
        kiln::building_input::reset_for_tests();
        kiln::test_image.side = side;
        reset();
        caps::core_rows(true, true, "");
    }
    // kiln.client.crosshair: the client only, its service running, its producer ready (hooked at the first read) or
    // bound; a failed first install is faulted; the producer's reason is the row's text while unbound
    {
        namespace ch = kiln::crosshair;
        const uint32_t side = kiln::test_image.side;
        kiln::test_image.side = KILN_SIDE_CLIENT;
        ch::reset_for_tests();
        caps::core_rows(true, true, "");
        const char* why = nullptr;
        ch::usable(&why, nullptr);
        CHECK(is("client.crosshair", U, KILN_CAP_REASON_SYMBOL_MISSING) && get("client.crosshair").text == why &&
                  get("client.crosshair").source == "core",
              "crosshair: no verified trace on this build: unavailable, symbol-missing, with the producer's reason (%s)",
              get("client.crosshair").text.c_str());
        ch::set_installer(+[](uint32_t* hit, const char**) { *hit = 0xa90; return ch::Install::Bound; });
        CHECK(is("client.crosshair", A, 0) && get("client.crosshair").text.find("hooked at the first read") != std::string::npos,
              "crosshair: a ready producer reads available, hooked at the first read, without a rebuild (%s)",
              get("client.crosshair").text.c_str());
        KilnCrosshairHit hit{};
        hit.size = sizeof hit;
        uint32_t reason = 0;
        CHECK(ch::get({true, true, 0}, &hit, &reason) == KILN_OK && reason == KILN_CROSSHAIR_REASON_NO_SAMPLE,
              "crosshair: the first read installs and answers no sample");
        CHECK(is("client.crosshair", A, 0) && get("client.crosshair").text == "the game's crosshair trace is hooked",
              "crosshair: hooked after the rows were built: the row follows (%s)", get("client.crosshair").text.c_str());
        ch::reset_for_tests();
        ch::set_installer(+[](uint32_t*, const char** w) { *w = "the test install failed"; return ch::Install::Failed; });
        caps::core_rows(true, true, "");
        CHECK(ch::get({true, true, 0}, &hit, &reason) == KILN_ERR_UNAVAILABLE, "crosshair: a failed first install refuses the read");
        CHECK(is("client.crosshair", U, KILN_CAP_REASON_FAULTED) && get("client.crosshair").text == "the test install failed",
              "crosshair: a failed first install: unavailable, faulted, with its reason (%s)", get("client.crosshair").text.c_str());
        ch::reset_for_tests();
        ch::bind(0xa90);
        kiln_service_fakecrosshair.available = false;
        caps::core_rows(true, true, "");
        CHECK(is("client.crosshair", U, KILN_CAP_REASON_SERVICE_MISSING), "crosshair: kiln.client.crosshair missing refuses");
        kiln_service_fakecrosshair.available = true;
        caps::core_rows(false, false, "test idle");
        CHECK(is("client.crosshair", U, KILN_CAP_REASON_POLICY) && get("client.crosshair").text == "not started in safe idle: test idle",
              "crosshair: safe idle says policy");
        kiln::test_image.side = KILN_SIDE_SERVER;
        caps::core_rows(true, true, "");
        CHECK(is("client.crosshair", U, KILN_CAP_REASON_WRONG_SIDE), "crosshair: a dedicated server says wrong side");
        kiln::test_image.side = KILN_SIDE_CLIENT;
        ch::reset_for_tests();
        ch::unbound_reason("held by the client gate until its guided client checks pass");
        caps::core_rows(true, true, "");
        CHECK(is("client.crosshair", U, KILN_CAP_REASON_SYMBOL_MISSING) &&
                  get("client.crosshair").text == "held by the client gate until its guided client checks pass",
              "crosshair: unbound, the row says the binder's reason");
        ch::reset_for_tests();
        kiln::test_image.side = side;
        reset();
        caps::core_rows(true, true, "");
    }
    // kiln.client.game_input: the client only, its service running, its hooks ready (installed at the first use) or in;
    // a failed first install is faulted; the producer's reason is the row's text while unbound
    {
        namespace gi = kiln::game_input;
        const uint32_t side = kiln::test_image.side;
        kiln::test_image.side = KILN_SIDE_CLIENT;
        gi::reset_for_tests();
        caps::core_rows(true, true, "");
        const char* why = nullptr;
        gi::usable(&why, nullptr);
        CHECK(is("client.game_input", U, KILN_CAP_REASON_SYMBOL_MISSING) && get("client.game_input").text == why &&
                  get("client.game_input").source == "core",
              "game input: no verified sites on this build: unavailable, symbol-missing, with the producer's reason (%s)",
              get("client.game_input").text.c_str());
        gi::set_installer(+[](gi::Layout*, const char**) { return gi::Install::Bound; });
        CHECK(is("client.game_input", A, 0) && get("client.game_input").text.find("hooked at the first use") != std::string::npos,
              "game input: ready hooks read available, hooked at the first use, without a rebuild (%s)",
              get("client.game_input").text.c_str());
        KilnGameInputSample sample{};
        sample.size = sizeof sample;
        uint32_t reason = 0;
        CHECK(gi::last({true, true, 0, nullptr}, &sample, &reason) == KILN_OK && reason == KILN_GAME_INPUT_REASON_NO_SAMPLE,
              "game input: the first read installs and answers no sample");
        CHECK(is("client.game_input", A, 0) &&
                  get("client.game_input").text == "the game's local input writer and input message are hooked",
              "game input: hooked after the rows were built: the row follows (%s)", get("client.game_input").text.c_str());
        gi::reset_for_tests();
        gi::set_installer(+[](gi::Layout*, const char** w) { *w = "the test install failed"; return gi::Install::Failed; });
        caps::core_rows(true, true, "");
        CHECK(gi::last({true, true, 0, nullptr}, &sample, &reason) == KILN_ERR_UNAVAILABLE,
              "game input: a failed first install refuses the read");
        CHECK(is("client.game_input", U, KILN_CAP_REASON_FAULTED) && get("client.game_input").text == "the test install failed",
              "game input: a failed first install: unavailable, faulted, with its reason (%s)",
              get("client.game_input").text.c_str());
        gi::reset_for_tests();
        gi::set_installer(+[](gi::Layout*, const char**) { return gi::Install::Bound; });
        kiln_service_fakegameinput.available = false;
        caps::core_rows(true, true, "");
        CHECK(is("client.game_input", U, KILN_CAP_REASON_SERVICE_MISSING), "game input: kiln.client.game_input missing refuses");
        kiln_service_fakegameinput.available = true;
        caps::core_rows(false, false, "test idle");
        CHECK(is("client.game_input", U, KILN_CAP_REASON_POLICY) &&
                  get("client.game_input").text == "not started in safe idle: test idle",
              "game input: safe idle says policy");
        kiln::test_image.side = KILN_SIDE_SERVER;
        caps::core_rows(true, true, "");
        CHECK(is("client.game_input", U, KILN_CAP_REASON_WRONG_SIDE), "game input: a dedicated server says wrong side");
        gi::reset_for_tests();
        kiln::test_image.side = side;
        reset();
        caps::core_rows(true, true, "");
    }
    // kiln.tasks' pool: tasks.pool follows the pool's health (read at every registry call, outside the pool's locks)
    {
        static std::atomic<int> health{0};
        CHECK(is("tasks.pool", U, KILN_CAP_REASON_SERVICE_MISSING) && get("tasks.pool").source == "core",
              "tasks.pool: no pool source (kiln.tasks did not start): unavailable, service-missing, source core");
        caps::tasks_pool_source(+[] { return health.load(); });
        CHECK(is("tasks.pool", U, KILN_CAP_REASON_FAULTED), "tasks.pool: every worker start failed: unavailable, faulted");
        health = 1;
        CHECK(is("tasks.pool", A, 0), "tasks.pool: a retried start succeeded: the row becomes available on its own");
        health = 0;
        CHECK(is("tasks.pool", U, KILN_CAP_REASON_FAULTED), "tasks.pool: every worker lost again: unavailable");
        health = 1;
        caps::core_rows(false, false, "test idle");
        CHECK(is("tasks.pool", U, KILN_CAP_REASON_POLICY), "tasks.pool: safe idle: policy");
        caps::tasks_pool_source(nullptr);
    }
    reset();
    resolve_with(dir, "48 B8 ?? ?? ?? ?? ?? ?? ?? ?? E9");  // E8 -> E9: no match on this build
    caps::core_rows(true, true, "");
    const Got fuel = get("items.fuel");
    CHECK(fuel.rc == KILN_OK && fuel.state == U && fuel.reason == KILN_CAP_REASON_SYMBOL_MISSING &&
              fuel.text == "symbol root: root.find[0]: 0 matches" && !sigdb::feature("items.fuel"),
          "features: a broken pattern makes its feature unavailable, naming the symbol (%s)", fuel.text.c_str());
    CHECK(is("net.kick", A, 0) && is("tick", A, 0) && is("world.lifecycle", A, 0),
          "features: the other features stay available");

    // the hooks behind tick* and world.lifecycle
    reset();
    t_tick = false;
    t_lifecycle = false;
    caps::core_rows(true, true, "");
    CHECK(is("tick", U, KILN_CAP_REASON_FAULTED) && is("tick.wait_jobs", U, KILN_CAP_REASON_FAULTED) &&
              get("tick").text.find("tick hook") != std::string::npos && is("world.lifecycle", U, KILN_CAP_REASON_FAULTED) &&
              get("world.lifecycle").text.find("WorldDestroy") != std::string::npos && is("net.kick", A, 0),
          "features: tick* without the tick hook, world.lifecycle without the destroy hook: faulted");
    reset();
    t_tick = t_lifecycle = true;
    caps::core_rows(true, true, "");
    Seen seen;
    KilnHandle h = 0;
    T->subscribe(owner("host"), &record_change, &seen, &h);
    t_tick = false;  // the hook faults at run time
    CHECK(is("tick", U, KILN_CAP_REASON_FAULTED) && is("tick.wait_jobs", U, KILN_CAP_REASON_FAULTED) && seen.size() == 3 &&
              (seen.names() == std::set<std::string>{"tick", "tick.wait_jobs", "world.stop_drain.server"}),
          "features: a tick hook that faults later shows at the next read, with a change each (the server's stop_drain row "
          "follows)");
    t_lifecycle = false;
    caps::feature_off("world.lifecycle");  // KilnApi.feature answered false
    CHECK(caps::g_names.find("world.lifecycle")->second.last.reason == KILN_CAP_REASON_FAULTED,
          "features: KilnApi.feature() answering false corrects the row");
    T->unsubscribe(h);
    t_tick = t_lifecycle = true;

    // world.stop_drain.<kind> (since 0.2.0-dev.29): what the core guarantees about a world kind's jobs when it dispatches
    // the world's STOPPING (tick_drain.h): drained by the engine, a bounded wait only, no stop at all, or no such world.
    {
        const uint32_t side = test_image.side;
        test_image.side = KILN_SIDE_SERVER;
        reset();
        resolve_with(dir, "48 B8 ?? ?? ?? ?? ?? ?? ?? ?? E8 {helper:rel32}");
        t_tick = t_lifecycle = t_wait = true;
        caps::core_rows(true, true, "");
        CHECK(is("world.stop_drain.server", A, 0) && get("world.stop_drain.server").text.find("WaitWorldJobs") != std::string::npos &&
                  is("world.stop_drain.client", U, KILN_CAP_REASON_WRONG_SIDE) &&
                  is("world.stop_drain.prediction", U, KILN_CAP_REASON_WRONG_SIDE),
              "stop_drain: a dedicated server's world is drained by the engine; no client or prediction world there");
        test_image.side = KILN_SIDE_CLIENT;
        reset();
        caps::core_rows(true, true, "");
        CHECK(is("world.stop_drain.server", A, 0) && is("world.stop_drain.client", A, 0) &&
                  is("world.stop_drain.prediction", A, 0) &&
                  get("world.stop_drain.client").text.find("WaitWorldJobs") != std::string::npos &&
                  get("world.stop_drain.prediction").text.find("stops with the client world") != std::string::npos,
              "stop_drain: in a client every kind is drained by the engine (the prediction world with the client world)");
        t_wait = false;
        reset();
        caps::core_rows(true, true, "");
        CHECK(is("world.stop_drain.server", U, KILN_CAP_REASON_SYMBOL_MISSING) &&
                  is("world.stop_drain.client", U, KILN_CAP_REASON_SYMBOL_MISSING) &&
                  get("world.stop_drain.client").text.rfind("bounded wait only", 0) == 0,
              "stop_drain: without WaitWorldJobs every kind has a bounded wait only, and says so");
        t_wait = true;
        reset();
        caps::core_rows(true, true, "");
        t_lifecycle = false;  // the destroy hook faults at run time: no STOPPING at all from now on
        CHECK(is("world.stop_drain.client", U, KILN_CAP_REASON_FAULTED) &&
                  get("world.stop_drain.client").text.rfind("no world stop is seen", 0) == 0 &&
                  is("world.stop_drain.server", U, KILN_CAP_REASON_FAULTED),
              "stop_drain: a hook that faults later turns every kind's row to faulted at the next read");
        t_lifecycle = true;
        reset();
        caps::core_rows(false, false, "idle for the test");
        CHECK(is("world.stop_drain.server", U, KILN_CAP_REASON_POLICY), "stop_drain: in safe idle the row says policy");
        test_image.side = side;
    }

    // safe idle: every feature says policy, with boot's sentence
    reset();
    const std::string idle = "build " + std::string(64, 'a') + " is not verified (no record) and policy is verified-only";
    const auto services = dir / "kiln" / "services";
    std::filesystem::remove_all(dir / "kiln");
    std::filesystem::create_directories(services);
    write(services / "kiln-items.dll", "");
    write(services / "Kiln-Chat.DLL", "");
    write(services / "readme.txt", "");
    t_paths.kiln = dir / "kiln";
    t_paths.state = dir / "kiln" / "state";
    caps::core_rows(false, false, idle);
    t_tick = false;  // no refresh in safe idle
    all = true;
    for (const char* f : {"items.fuel", "net.kick", "tick", "world.lifecycle"})
        all = all && is(f, U, KILN_CAP_REASON_POLICY) && get(f).text == idle;
    t_tick = true;
    CHECK(all, "safe idle: every feature unavailable, policy, with the safe-idle sentence");
    CHECK(is("kiln.caps", A, 0) && get("kiln.caps").text == "v1" && is("kiln.fakeok", U, KILN_CAP_REASON_POLICY) &&
              is("kiln.fakeno", U, KILN_CAP_REASON_POLICY),
          "safe idle: kiln.caps answers, the other in-core services say policy");
    CHECK(is("kiln.items", U, KILN_CAP_REASON_POLICY) && get("kiln.items").text == "kiln-items.dll is not loaded in safe idle: " + idle &&
              is("kiln.chat", U, KILN_CAP_REASON_POLICY) && get("kiln.readme").rc == KILN_ERR_NOT_FOUND,
          "safe idle: a row for each DLL in kiln/services");
    std::filesystem::remove_all(dir / "kiln");

    // in-core services with hooks on
    reset();
    caps::core_rows(true, true, "");
    CHECK(is("kiln.fakeok", A, 0) && get("kiln.fakeok").text == "v3" && get("kiln.fakeok").source == "core" &&
              is("kiln.caps", A, 0),
          "services: an in-core service that started is available with its version");
    const Got no = get("kiln.fakeno");
    CHECK(no.state == U && no.reason == KILN_CAP_REASON_SERVICE_MISSING && no.text.find("init") != std::string::npos &&
              no.text.find("kiln.log") != std::string::npos,
          "services: one whose init said no is service-missing and points at kiln.log (%s)", no.text.c_str());

    // services a DLL published, and service DLLs that did not make it
    const KilnOwner pub = owner("service:kiln-items.dll");
    caps::service_published(pub, "kiln.items", 3);
    CHECK(is("kiln.items", A, 0) && get("kiln.items").text == "v3" && get("kiln.items").source == "service:kiln-items.dll",
          "published: available, its version, source the publisher");
    CHECK(rep(owner("mod:x"), "kiln.items", P, KILN_CAP_REASON_SESSION) == KILN_ERR_INVALID_ARGUMENT,
          "published: a core name, pending refused");
    caps::service_dll("kiln-items.dll", "failed", "kiln_plugin_main faulted (0xc0000005)");  // record() before revoke
    CHECK(is("kiln.items", A, 0), "published: a DLL failure of the same name waits while the publisher lives");
    revoke(pub);
    const Got items = get("kiln.items");
    CHECK(items.state == U && items.reason == KILN_CAP_REASON_FAULTED &&
              items.text == "kiln-items.dll failed: kiln_plugin_main faulted (0xc0000005)" && items.source == "core",
          "service DLL: a fault shows once its publisher is revoked (%s)", items.text.c_str());
    caps::service_dll("kiln-chat.dll", "skipped", "kiln_plugin_main returned 3 (unavailable)");
    caps::service_dll("kiln-net.dll", "failed", "LoadLibrary failed (126)");
    caps::service_dll("kiln-ok.dll", "loaded", "");
    CHECK(is("kiln.chat", U, KILN_CAP_REASON_SERVICE_MISSING) &&
              get("kiln.chat").text == "kiln-chat.dll skipped: kiln_plugin_main returned 3 (unavailable); kiln.log says why" &&
              is("kiln.net", U, KILN_CAP_REASON_SERVICE_MISSING) && get("kiln.ok").rc == KILN_ERR_NOT_FOUND,
          "service DLL: skipped and failed ones are service-missing; a loaded one gets no row from record()");
    caps::service_published(owner("plugin:x.dll"), "kiln.fakeok", 9);
    CHECK(get("kiln.fakeok").text == "v3", "published: an in-core service of the same name wins, as in api_service");
    const KilnOwner late = owner("plugin:late.dll");
    kill(late);
    caps::service_published(late, "late.svc", 1);
    CHECK(get("late.svc").rc == KILN_ERR_NOT_FOUND, "published: nothing for an owner that is gone already");
    const KilnOwner p2 = owner("plugin:p2.dll");
    caps::service_published(p2, "p2.svc", 1);
    kill(p2);
    CHECK(get("p2.svc").rc == KILN_ERR_NOT_FOUND, "published: the row goes with its publisher (the net)");
    CHECK(caps::dll_row_name("kiln-items.dll") == "kiln.items" && caps::dll_row_name("MyService.DLL") == "kiln.myservice" &&
              caps::dll_row_name("kiln-a b.dll") == "kiln.a_b" && caps::dll_row_name("kiln-.dll").empty(),
          "service DLL: row names from file names");
    sigdb::g_core = sigdb::Resolver();
}

// kiln.net's Steam peer-to-peer messages row: the core says why in safe idle and when kiln-net.dll did
// not start; otherwise kiln-net.dll reports it (pending until Steam is resolved, then available).
void steam_messages_row(const std::filesystem::path& dir) {
    reset();
    t_paths.kiln = dir / "kiln-none";
    caps::core_rows(false, false, "test idle");
    CHECK(is("net.steam_messages", U, KILN_CAP_REASON_POLICY) &&
              get("net.steam_messages").text == "not started in safe idle: test idle" &&
              get("net.steam_messages").source == "core",
          "steam messages: safe idle is policy, with the reason (%s)", get("net.steam_messages").text.c_str());
    reset();
    caps::core_rows(true, true, "");
    CHECK(get("net.steam_messages").rc == KILN_ERR_NOT_FOUND, "steam messages: hooks on, no core row (kiln-net.dll reports it)");
    const KilnOwner net = owner("service:kiln-net.dll");
    CHECK(rep(net, "net.steam_messages", P, KILN_CAP_REASON_SESSION, "waits for Steam") == KILN_OK &&
              is("net.steam_messages", P, KILN_CAP_REASON_SESSION) && get("net.steam_messages").source == "service:kiln-net.dll",
          "steam messages: kiln-net.dll may report it pending");
    CHECK(rep(net, "net.steam_messages", A, KILN_CAP_REASON_NONE, "resolved") == KILN_OK && is("net.steam_messages", A, 0) &&
              (get("net.steam_messages").flags & KILN_CAP_FLAG_SESSION),
          "steam messages: then available, a session row");
    revoke(net);
    caps::service_dll("kiln-net.dll", "skipped", "kiln_plugin_main returned 3 (unavailable)");
    const Got missing = get("net.steam_messages");
    CHECK(missing.state == U && missing.reason == KILN_CAP_REASON_SERVICE_MISSING &&
              missing.text.rfind("kiln.net did not start: kiln-net.dll skipped", 0) == 0 && missing.source == "core",
          "steam messages: kiln-net.dll did not start: service-missing (%s)", missing.text.c_str());
    caps::service_dll("kiln-items.dll", "failed", "LoadLibrary failed (126)");
    CHECK(get("net.steam_messages").text == missing.text, "steam messages: another DLL's failure leaves it alone");
}

void json_file() {
    using nlohmann::json;
    reset();
    caps::core_rows(false, false, "the game image cannot be parsed");
    const KilnOwner o = owner("host");
    rep(o, "items.moves", P, KILN_CAP_REASON_SESSION, "no world is loaded yet");
    rep(o, "z.future", U, 11, "bad \xFF byte");
    rep(o, "a.ok", A, 0, "v3");
    json doc = json::parse(caps::render(kMustEnd));
    bool top = doc["format"] == 1 && doc["kiln"].is_string() && doc["pid"] == 4242 && doc["written"].is_string() &&
               doc["side"] == "server" && doc["exe"]["sha256"].is_string() && doc["exe"]["verified"] == false &&
               doc["hooks"] == false && doc["safeIdle"] == "the game image cannot be parsed" &&
               doc["generation"] == T->generation() && doc["capabilities"].is_array();
    CHECK(top, "json: the schema's top-level fields, safe idle with its sentence");
    bool rows = doc["capabilities"].size() == T->count();
    std::string last;
    for (const auto& r : doc["capabilities"]) {
        rows = rows && r["name"].is_string() && r["state"].is_string() && r["reason"].is_string() &&
               r["reasonText"].is_string() && r["source"].is_string() && r["name"].get<std::string>() > last;
        rows = rows && (!r.contains("session") || r["session"] == true);
        last = r["name"].get<std::string>();
    }
    CHECK(rows, "json: every row has name, state, reason, reasonText and source, sorted by name");
    auto row = [&](const std::string& n) {
        for (const auto& r : doc["capabilities"])
            if (r["name"] == n) return r;
        return json();
    };
    CHECK(row("items.moves")["state"] == "pending" && row("items.moves")["reason"] == "session" &&
              row("items.moves")["session"] == true && row("a.ok")["state"] == "available" &&
              row("a.ok")["reason"] == "none" && !row("a.ok").contains("session") &&
              row("kiln.caps")["source"] == "core",
          "json: state and reason names, session only on session rows");
    CHECK(row("z.future")["reason"] == "other-11" && row("z.future")["state"] == "unavailable" &&
              row("z.future")["reasonText"].get<std::string>().find("bad ") == 0,
          "json: a code with no name is other-<n>; a bad UTF-8 byte does not stop the file");
    caps::core_rows(true, true, "");
    doc = json::parse(caps::render(kMustEnd));
    CHECK(doc["hooks"] == true && doc["safeIdle"].is_null() && doc["exe"]["verified"] == true,
          "json: hooks on, safeIdle null");
}

// Polls until done() holds, at most kMustEnd.
template <class F>
bool poll_until(F done) {
    const auto give_up = std::chrono::steady_clock::now() + kMustEnd;
    while (!done()) {
        if (std::chrono::steady_clock::now() > give_up) return false;
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    return true;
}
bool wait_writes(size_t n) {
    return poll_until([n] { return writes() >= n; });
}
// The writer thread looked at the time set last (see fake_clock) and is done deciding: the test then takes g_wmutex,
// under which the writer measures its gap and takes a write, so the writer is either waiting out its gap or has taken
// a write (g_in_flight). True for the first: a must-not-write check that follows proves the writer saw the change and
// the time, and chose to wait. Called while something is dirty: a writer found clean has written it (false at once).
bool writer_waits() {
    bool seen = false, idle = false;
    poll_until([&] {
        {
            std::lock_guard l(t_clock_mutex);
            seen = t_seen_epoch == t_clock_epoch;
        }
        std::lock_guard l(caps::g_wmutex);
        idle = !caps::g_in_flight;
        return seen || (idle && !caps::g_dirty);
    });
    return seen && idle;
}
// Nothing dirty and no write in flight: the writer thread takes no write until the next change marks the file dirty.
// Polls, since a write that just reached the stub has not ended yet.
bool writer_clean() {
    return poll_until([] {
        std::lock_guard l(caps::g_wmutex);
        return !caps::g_dirty && !caps::g_in_flight;
    });
}

void writer() {
    reset();
    caps::g_clock = &fake_clock;
    set_clock(10000);
    const auto dir = std::filesystem::path("state-dir");
    t_paths.state = dir;
    const KilnOwner o = owner("host");
    rep(o, "w.zero", A, 0);
    CHECK(writes() == 0 && caps::flush(), "writer: nothing is written before boot completes");
    caps::boot_complete();
    CHECK(writes() == 1 && t_last_file == dir / "capabilities.json" && last_write().find("w.zero") != std::string::npos,
          "writer: boot_complete writes at once, to <state>/capabilities.json");
    rep(o, "w.one", A, 0);
    set_clock(10999);
    CHECK(writer_waits() && writes() == 1, "writer: a change within the second waits (the writer saw it at 10999)");
    set_clock(11000);
    CHECK(wait_writes(2) && writes() == 2 && last_write().find("w.one") != std::string::npos,
          "writer: written once the second is over");
    for (int i = 0; i < 20; ++i) {
        rep(o, "w.many", U, KILN_CAP_REASON_POLICY, "step " + std::to_string(i));
        set_clock(11000 + i * 40);
    }
    CHECK(writer_waits() && writes() == 2, "writer: twenty changes in the next second, no write yet");
    set_clock(12000);
    CHECK(wait_writes(3), "writer: then one write");
    CHECK(writer_clean() && writes() == 3 && last_write().find("step 19") != std::string::npos,
          "writer: one write with the last state, and nothing left dirty");
    set_clock(50000);
    CHECK(writer_clean() && writes() == 3 && caps::flush() && writes() == 3,
          "writer: nothing dirty, nothing written (thread or flush)");
    rep(o, "w.last", A, 0);
    CHECK(caps::flush() && writes() == 4 && last_write().find("w.last") != std::string::npos,
          "writer: flush writes what is dirty at once, inside the second");
    set_clock(60000);
    CHECK(writer_clean() && writes() == 4, "writer: after flush the thread has nothing left to write");
}

size_t attempts() {
    std::lock_guard l(t_mutex);
    return t_attempts.size();
}
bool wait_attempts(size_t n) {
    return poll_until([n] { return attempts() >= n; });
}
size_t infos_with(const char* what) {
    std::lock_guard l(t_mutex);
    return std::count_if(t_infos.begin(), t_infos.end(), [&](const std::string& s) { return s.find(what) != std::string::npos; });
}
void fail_next(int n) {
    std::lock_guard l(t_mutex);
    t_fail_next = n;
}
void block(bool on) {
    {
        std::lock_guard l(t_mutex);
        t_block = on;
    }
    t_write_cv.notify_all();
}
bool wait_in_write() {
    std::unique_lock l(t_mutex);
    return t_write_cv.wait_for(l, kMustEnd, [] { return t_in_write; });
}
bool in_write() {
    std::lock_guard l(t_mutex);
    return t_in_write;
}

// A write that fails (the stub's t_fail_next, as Wine's replacing rename while a reader holds the file) is tried again
// after the gap, without another change; flush() keeps the dirt of a failed write and waits for a write in flight.
// Runs after writer(): the writer thread is up, the last write was at fake 50000.
void writer_retry() {
    const KilnOwner o = owner("host");
    const size_t a0 = attempts(), w0 = writes();
    set_clock(70000);
    fail_next(2);
    rep(o, "r.one", A, 0);
    CHECK(wait_attempts(a0 + 1) && writes() == w0, "retry: the writer's write fails");
    set_clock(70999);
    CHECK(writer_waits() && attempts() == a0 + 1, "retry: no second try within the second (no busy loop)");
    set_clock(71000);
    CHECK(wait_attempts(a0 + 2) && writes() == w0, "retry: tried again once the second is over, with no new change; fails again");
    set_clock(71999);
    CHECK(writer_waits() && attempts() == a0 + 2, "retry: still at most one try a second across failures");
    set_clock(72000);
    CHECK(wait_writes(w0 + 1) && attempts() == a0 + 3 && last_write().find("r.one") != std::string::npos,
          "retry: the third try writes, and the file holds the change");
    set_clock(90000);
    CHECK(writer_clean() && attempts() == a0 + 3 && writes() == w0 + 1, "retry: then parked, nothing dirty");
    CHECK(infos_with("cannot write") == 1 && infos_with("written again") == 1,
          "retry: one line for the failures, one when writing works again");

    // flush: a failed one keeps the dirt, the next one writes
    const size_t w1 = writes();
    fail_next(1);
    {
        std::lock_guard l(caps::g_wmutex);
        caps::g_last_write = 90000;  // as if written just now: the writer thread waits out its gap, flush ignores it
    }
    rep(o, "r.two", A, 0);
    const bool first = caps::flush();
    CHECK(!first && writes() == w1, "flush: a failed write answers false");
    const bool second = caps::flush();
    CHECK(second && writes() == w1 + 1 && last_write().find("r.two") != std::string::npos,
          "flush: the dirt of the failed write stayed, so the next flush writes it");

    // flush during the writer's write in flight. The stub holds that write for as long as the test says, so flush's own
    // bound (1 s) runs out with the write still in flight: flush answers false and writes nothing (it never claims a
    // file it has not seen written). Then the write ends, and flush answers true with no second write.
    const size_t w2 = writes();
    rep(o, "r.three", A, 0);
    block(true);
    set_clock(91000);  // past flush's last try (90000): the writer takes the write and stays inside it
    const bool entered = wait_in_write();
    auto f0 = std::chrono::steady_clock::now();
    const bool held = caps::flush();
    const auto waited = std::chrono::steady_clock::now() - f0;
    const bool still = in_write() && writes() == w2;
    block(false);
    // flush took its whole bound before it answered: it waited for the write in flight (a flush that gave up at once
    // would answer false too). A lower bound, so a loaded machine cannot fail it.
    CHECK(entered && !held && still && waited >= std::chrono::milliseconds(1000),
          "flush: a write in flight past flush's bound: flush waits its 1 s for it, answers false and writes nothing "
          "(%lld ms)",
          static_cast<long long>(std::chrono::duration_cast<std::chrono::milliseconds>(waited).count()));
    CHECK(wait_writes(w2 + 1) && writer_clean() && caps::flush() && writes() == w2 + 1 &&
              last_write().find("r.three") != std::string::npos,
          "flush: once the write in flight is in the file, flush answers true (one write)");

    // the write in flight fails: its dirt comes back, the writer waits out its gap (the fake clock stays), and flush
    // writes itself
    const size_t w3 = writes(), a3 = attempts();
    rep(o, "r.four", A, 0);
    fail_next(1);
    block(true);
    set_clock(92000);
    const bool entered2 = wait_in_write();
    f0 = std::chrono::steady_clock::now();
    const bool held2 = caps::flush();
    const auto waited2 = std::chrono::steady_clock::now() - f0;
    block(false);
    const bool failed = wait_attempts(a3 + 1) && poll_until([] {
        std::lock_guard l(caps::g_wmutex);
        return caps::g_dirty && !caps::g_in_flight;
    });
    const bool result = caps::flush();
    CHECK(entered2 && !held2 && waited2 >= std::chrono::milliseconds(1000) && failed && result && writes() == w3 + 1 &&
              attempts() == a3 + 2 && last_write().find("r.four") != std::string::npos,
          "flush: waits its 1 s for a write in flight; when that write fails, flush does it again and answers true");
}

// No registry entry point after the fault: a subscriber (the managed snapshot's source), the stored rows and the
// file must follow on their own, even though no later tick or capability query runs.
void idle_tick_fault(const std::filesystem::path& dir) {
    Loaded image;
    sigdb::init_code();  // a second test image in this process; code()'s once_flag has already run
    reset();
    resolve_with(dir, "48 B8 ?? ?? ?? ?? ?? ?? ?? ?? E8 {helper:rel32}");
    t_tick = t_lifecycle = true;
    caps::core_rows(true, true, "");
    CHECK(is("tick", A, 0) && is("tick.wait_jobs", A, 0) && is("world.lifecycle", A, 0),
          "idle fault: all watched rows start available (%s; %s; %s)", get("tick").text.c_str(),
          get("tick.wait_jobs").text.c_str(), get("world.lifecycle").text.c_str());
    set_clock(100000);
    caps::boot_complete();
    Seen seen;
    KilnHandle h = 0;
    T->subscribe(owner("host"), &record_change, &seen, &h);
    // The writer parks with a clean file: woken once, it read the tick hook's state (refresh_tick, the top of its round)
    // and, with nothing dirty, has nothing to do but wait. Moving this fake clock does not wake it after the fault.
    {
        std::lock_guard c(t_clock_mutex);
        ++t_tick_epoch;
    }
    caps::g_wcv.notify_all();  // one more round now rather than at its next second
    const bool parked = poll_until([] {
        {
            std::lock_guard l(t_clock_mutex);
            if (t_tick_seen != t_tick_epoch) return false;
        }
        std::lock_guard l(caps::g_wmutex);
        if (!caps::g_dirty && !caps::g_in_flight) return true;
        std::lock_guard c(t_clock_mutex);  // not clean yet: one more round
        ++t_tick_epoch;
        return false;
    });
    CHECK(parked, "idle fault: the writer read the tick hook's state and parked with nothing dirty");
    const size_t before = writes();
    const auto start = std::chrono::steady_clock::now();
    {
        std::lock_guard l(t_clock_mutex);
        t_now = 101000;
    }
    t_tick = t_lifecycle = false;
    // the three tick rows and, derived from the same hooks, the server world's stop_drain row (dev.29). The writer
    // looks each second; how long it took is shown, not checked (a loaded machine stretches it).
    poll_until([&] { return seen.size() >= 4 && writes() > before; });
    const auto took = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - start);
    bool rows = true;
    {
        std::lock_guard l(caps::g_mutex);
        for (const char* name : {"tick", "tick.wait_jobs", "world.lifecycle", "world.stop_drain.server"}) {
            const auto& row = caps::g_names.at(name).last;
            rows = rows && row.state == U && row.reason == KILN_CAP_REASON_FAULTED;
        }
    }
    CHECK(rows && seen.size() == 4 && writes() > before,
          "idle fault: rows, subscriber snapshot and file follow without a registry call (rows %d, events %zu, writes %zu -> %zu, after %lld ms)",
          rows, seen.size(), before, writes(), static_cast<long long>(took.count()));
    const auto doc = nlohmann::json::parse(last_write());
    unsigned faulted = 0;
    for (const auto& row : doc["capabilities"])
        if ((row["name"] == "tick" || row["name"] == "tick.wait_jobs" || row["name"] == "world.lifecycle" ||
             row["name"] == "world.stop_drain.server") &&
            row["state"] == "unavailable" && row["reason"] == "faulted") ++faulted;
    CHECK(faulted == 4, "idle fault: capabilities.json holds all four faulted rows");
    T->unsubscribe(h);
    reset();
    sigdb::g_core = sigdb::Resolver();
}

}  // namespace

// The client gate (gates.h): held names read unavailable/policy with the check ids, whatever else reports them;
// kiln.json's "preview" lifts them; a held name may be reported pending.
void client_gate(const std::filesystem::path& dir) {
    namespace gates = kiln::gates;
    Loaded x;
    sigdb::init_code();  // a second test image in this process; code()'s once_flag has already run
    t_tick = t_lifecycle = true;
    static const gates::Gate list[] = {
        {"items.fuel", "99-fuel-a, 99-fuel-b", gates::kBoth},        // a sigdb feature whose symbols resolve
        {"camera.fov", "03-c11", gates::kBoth},                      // a service's own row
        {"markers.compass", "04-m03", gates::kClient},               // held on the client only
        {"queries.server_only", "01-q1", gates::kServer},
        {nullptr, nullptr, 0},
    };
    const auto boot = [&](uint32_t side, bool all, std::vector<std::string> names) {
        reset();
        resolve_with(dir, "48 B8 ?? ?? ?? ?? ?? ?? ?? ?? E8 {helper:rel32}");
        gates::use_list_for_tests(list);
        gates::configure(side, all, names);
        caps::core_rows(true, true, "");
    };
    const auto warned = [](const std::string& part) {
        std::lock_guard l(t_mutex);
        for (const std::string& i : t_infos)
            if (i.find(part) != std::string::npos) return true;
        return false;
    };

    // Early plugins and in-core init callbacks can query the table before core_rows has run.
    reset();
    gates::use_list_for_tests(list);
    gates::configure(KILN_SIDE_SERVER, false, {});
    CHECK(kiln_caps_held(T, ks("camera.fov")) == 1,
          "gate: an init callback sees the hold before core_rows and leaves its hook off");
    const KilnOwner early = owner("plugin:early");
    rep(early, "camera.fov", A, KILN_CAP_REASON_NONE);
    CHECK(is("camera.fov", U, KILN_CAP_REASON_POLICY),
          "gate: a service reporting before core_rows cannot expose a held row as available");
    revoke(early);
    reset();
    gates::use_list_for_tests(list);  // init_image failed: configure is never called
    CHECK(kiln_caps_held(T, ks("markers.compass")) == 1 && kiln_caps_held(T, ks("queries.server_only")) == 1,
          "gate: early safe-idle readers see holds from both sides before core_rows");

    boot(KILN_SIDE_SERVER, false, {});
    const Got fuel = get("items.fuel");
    CHECK(fuel.rc == KILN_OK && fuel.state == U && fuel.reason == KILN_CAP_REASON_POLICY &&
              fuel.text == "game test pending: 99-fuel-a, 99-fuel-b" && fuel.source == "core" && sigdb::feature("items.fuel"),
          "gate: a held sigdb feature whose symbols resolved reads unavailable, policy, with its check ids (%s)",
          fuel.text.c_str());
    CHECK(gates::is_held("items.fuel") && gates::is_held("camera.fov") && !gates::is_held("net.kick") &&
              !gates::is_held("items.fue") && !gates::is_held(""),
          "gate: is_held answers for exactly the held names (KilnApi.feature() asks it)");
    CHECK(is("net.kick", A, 0) && is("tick", A, 0), "gate: names that are not held are untouched");
    CHECK(is("camera.fov", U, KILN_CAP_REASON_POLICY) && get("camera.fov").text == "game test pending: 03-c11",
          "gate: a held name nobody else reports has its row already, before any service loads");
    CHECK(is("queries.server_only", U, KILN_CAP_REASON_POLICY) && get("markers.compass").rc == KILN_ERR_NOT_FOUND &&
              !gates::is_held("markers.compass"),
          "gate: on a server, a client-only gate holds nothing and a server-only one holds");

    // whatever a service reports, the held row is what a reader sees; the helper of kiln_caps.h says so
    const KilnOwner svc = owner("service:kiln-camera.dll");
    CHECK(rep(svc, "camera.fov", A, KILN_CAP_REASON_NONE, "v1") == KILN_OK && is("camera.fov", U, KILN_CAP_REASON_POLICY) &&
              get("camera.fov").text == "game test pending: 03-c11",
          "gate: a service's 'available' does not show while the name is held");
    CHECK(kiln_caps_held(T, ks("camera.fov")) == 1 && kiln_caps_held(T, ks("items.fuel")) == 1 &&
              kiln_caps_held(T, ks("net.kick")) == 0 && kiln_caps_held(T, ks("no.such.row")) == 0 &&
              kiln_caps_held(nullptr, ks("camera.fov")) == 0,
          "gate: kiln_caps_held is true for held rows only");
    CHECK(rep(svc, "queries.server_only", KILN_CAP_PENDING, KILN_CAP_REASON_SESSION, "no world yet") == KILN_OK &&
              is("queries.server_only", U, KILN_CAP_REASON_POLICY) && (get("queries.server_only").flags & KILN_CAP_FLAG_SESSION),
          "gate: a held name may be reported pending (the gate's report does not make it a static core row)");
    CHECK(rep(svc, "items.fuel", KILN_CAP_PENDING, KILN_CAP_REASON_SESSION) == KILN_OK,
          "gate: also when the held name is a sigdb feature");
    const KilnOwner other = owner("mod:policy");
    rep(other, "net.kick", U, KILN_CAP_REASON_POLICY, "switched off by the server's rules");
    CHECK(kiln_caps_held(T, ks("net.kick")) == 0, "gate: a row that is off for another policy reason is not 'held'");
    revoke(svc);
    revoke(other);
    CHECK(is("camera.fov", U, KILN_CAP_REASON_POLICY), "gate: the held row stays when the reporting service goes");

    // a tick refresh rebuilds feature rows: a held tick-family feature must stay held
    static const gates::Gate tick_list[] = {{"tick.wait_jobs", "99-t1", gates::kBoth}, {nullptr, nullptr, 0}};
    reset();
    resolve_with(dir, "48 B8 ?? ?? ?? ?? ?? ?? ?? ?? E8 {helper:rel32}");
    gates::use_list_for_tests(tick_list);
    gates::configure(KILN_SIDE_SERVER, false, {});
    caps::core_rows(true, true, "");
    t_tick = false;
    CHECK(is("tick", U, KILN_CAP_REASON_FAULTED) && is("tick.wait_jobs", U, KILN_CAP_REASON_POLICY), 
          "gate: a refresh of the tick rows does not replace a held one");
    t_tick = true;
    CHECK(is("tick", A, 0) && is("tick.wait_jobs", U, KILN_CAP_REASON_POLICY) &&
              get("tick.wait_jobs").text == "game test pending: 99-t1",
          "gate: and when the hook is back the held row still says the game test is pending");

    // the client side
    boot(KILN_SIDE_CLIENT, false, {});
    CHECK(is("markers.compass", U, KILN_CAP_REASON_POLICY) && get("queries.server_only").rc == KILN_ERR_NOT_FOUND &&
              is("items.fuel", U, KILN_CAP_REASON_POLICY),
          "gate: on a client, the client-only gate holds and the server-only one does not");

    // kiln.json "preview": names
    {
        std::lock_guard l(t_mutex);
        t_infos.clear();
    }
    boot(KILN_SIDE_SERVER, false, {"items.fuel", "markers.compass", "no.such.gate"});
    CHECK(is("items.fuel", A, 0) && get("items.fuel").source == "sigdb" && !gates::is_held("items.fuel") &&
              is("camera.fov", U, KILN_CAP_REASON_POLICY),
          "preview: a named gate is lifted (the feature's own row shows), the others hold");
    CHECK(warned("'no.such.gate'") && !warned("'markers.compass'") && !warned("'items.fuel'"),
          "preview: a name the list does not hold is a warning; a gate of the other side is not");
    // kiln.json "preview": true
    boot(KILN_SIDE_SERVER, true, {});
    CHECK(is("items.fuel", A, 0) && get("camera.fov").rc == KILN_ERR_NOT_FOUND && gates::held().empty() &&
              !gates::is_held("camera.fov"),
          "preview: true lifts every gate (a name nobody reports has no row)");

    // safe idle: held rows keep the gate's text; everything else says why hooks are off
    reset();
    resolve_with(dir, "48 B8 ?? ?? ?? ?? ?? ?? ?? ?? E8 {helper:rel32}");
    gates::use_list_for_tests(list);
    gates::configure(KILN_SIDE_SERVER, false, {});
    caps::core_rows(false, false, "the build is not verified");
    t_tick = false;
    CHECK(is("items.fuel", U, KILN_CAP_REASON_POLICY) && get("items.fuel").text == "game test pending: 99-fuel-a, 99-fuel-b" &&
              get("net.kick").text == "the build is not verified",
          "gate: in safe idle a held row still names its checks");
    t_tick = true;

    // the compiled list (native/core/gates/*.json): every name is a capability name, sorted, with checks
    gates::use_list_for_tests(nullptr);
    bool good = true;
    std::string prev;
    for (const gates::Held& h : gates::held()) {
        good = good && caps::name_ok(h.name) && h.text.size() > std::strlen(KILN_CAP_GATE_TEXT) && prev < h.name;
        prev = h.name;
    }
    CHECK(good, "gate: the compiled list holds %zu well-formed, distinct names", gates::held().size());
    gates::configure(KILN_SIDE_SERVER, false, {});
    reset();

    // the release hold of Kiln's interpreter for ShroudForge mods (sfi_release_policy.h) as kiln.caps shows it
    {
        namespace sfi = kiln::sfi_release_policy;
        const auto sfi_boot = [&](bool all, std::vector<std::string> names) {
            reset();
            resolve_with(dir, "48 B8 ?? ?? ?? ?? ?? ?? ?? ?? E8 {helper:rel32}");
            gates::use_list_for_tests(nullptr);  // the compiled list: native/core/gates/sfi.json and the policy
            gates::configure(KILN_SIDE_CLIENT, all, names);
            caps::core_rows(true, true, "");
        };
        // the compiled list holds kiln-sfi.interpreter while it is a preview; without that row it is a normal choice
        bool listed = false;
        for (const gates::Gate* g = gates::kBuiltIn; g->name; ++g)
            listed = listed || (std::string_view(g->name) == sfi::kInterpreter && g->side == gates::kBoth);
        sfi_boot(false, {});
        if (listed)
            CHECK(is("kiln-sfi.interpreter", U, KILN_CAP_REASON_POLICY) && kiln_caps_held(T, ks("kiln-sfi.interpreter")) == 1 &&
                      get("kiln-sfi.interpreter").text.find("sfi-ui-page") != std::string::npos,
                  "sfi: kiln-sfi.interpreter reads held, with its guided checks, and kiln_caps_held is true");
        else
            CHECK(get("kiln-sfi.interpreter").rc == KILN_ERR_NOT_FOUND && kiln_caps_held(T, ks("kiln-sfi.interpreter")) == 0 &&
                      !gates::is_held("kiln-sfi.interpreter"),
                  "sfi: without its row kiln-sfi.interpreter is not held, with no preview (a name nobody reports has no row)");
        CHECK(is("kiln-sfi.sf18", U, KILN_CAP_REASON_POLICY) && get("kiln-sfi.sf18").text == std::string(sfi::kVetoText),
              "sfi: kiln-sfi.sf18 reads held by the release policy's veto");
        const KilnOwner plugin = owner("plugin:kiln-sfi.dll");
        rep(plugin, "kiln-sfi.interpreter", A, KILN_CAP_REASON_NONE);
        rep(plugin, "kiln-sfi.sf18", A, KILN_CAP_REASON_NONE);
        if (listed)
            CHECK(is("kiln-sfi.interpreter", U, KILN_CAP_REASON_POLICY) && is("kiln-sfi.sf18", U, KILN_CAP_REASON_POLICY),
                  "sfi: a plugin's 'available' does not open either held row");
        else
            CHECK(is("kiln-sfi.interpreter", A, KILN_CAP_REASON_NONE) && is("kiln-sfi.sf18", U, KILN_CAP_REASON_POLICY),
                  "sfi: a plugin's 'available' reads as reported for the open interpreter, never for the sf18 veto");
        revoke(plugin);
        sfi_boot(true, {});
        CHECK(get("kiln-sfi.interpreter").rc == KILN_ERR_NOT_FOUND && !gates::is_held("kiln-sfi.interpreter"),
              "sfi: preview true lifts kiln-sfi.interpreter (a name nobody reports has no row)");
        CHECK(is("kiln-sfi.sf18", U, KILN_CAP_REASON_POLICY) && get("kiln-sfi.sf18").text == std::string(sfi::kVetoText) &&
                  kiln_caps_held(T, ks("kiln-sfi.sf18")) == 1 && gates::is_held("kiln-sfi.sf18"),
              "sfi: preview true does not lift kiln-sfi.sf18 (veto), and kiln_caps_held is true for it");
        sfi_boot(false, {"kiln-sfi.sf18", "kiln-sfi.interpreter"});
        CHECK(is("kiln-sfi.sf18", U, KILN_CAP_REASON_POLICY) && get("kiln-sfi.interpreter").rc == KILN_ERR_NOT_FOUND,
              "sfi: preview naming both rows lifts the interpreter only");
        gates::use_list_for_tests(nullptr);
        gates::configure(KILN_SIDE_SERVER, false, {});
        reset();
    }
}

// kiln.json "tasks": the four global knobs, their defaults, bounds, wrong types and side overrides (config.cpp).
void tasks_config(const std::filesystem::path& dir) {
    const auto old = t_paths.kiln;
    t_paths.kiln = dir / "tasks-config";
    std::filesystem::create_directories(t_paths.kiln);
    const auto read = [&](const char* text) {
        kiln::g_config = kiln::Config{};
        kiln::g_doc = nlohmann::json{};
        {
            std::lock_guard l(t_mutex);
            t_infos.clear();
        }
        write(t_paths.kiln / "kiln.json", text);
        kiln::load_config();
        kiln::apply_side(KILN_SIDE_SERVER);
        return kiln::config().tasks;
    };
    const auto warned = [&](const char* key, const char* bound) {
        std::lock_guard l(t_mutex);
        int n = 0;
        for (const auto& line : t_infos)
            n += line.find(std::string("tasks.") + key) != std::string::npos && line.find(bound) != std::string::npos;
        return n;
    };
    auto c = read(R"({})");
    CHECK(c.workers == 0 && c.long_running_global == 8 && c.watchdog_running_s == 30 && c.watchdog_queued_s == 30,
          "tasks config: absent: the defaults (workers 0 = clamp(cores/4, 1, 4), 8, 30 s, 30 s)");
    c = read(R"({"tasks":{"workers":3,"long_running_global":12,"watchdog_running_s":5,"watchdog_queued_s":600}})");
    CHECK(c.workers == 3 && c.long_running_global == 12 && c.watchdog_running_s == 5 && c.watchdog_queued_s == 600,
          "tasks config: values inside their bounds are taken (bounds inclusive)");
    c = read(R"({"tasks":{"workers":9,"long_running_global":0,"watchdog_running_s":4,"watchdog_queued_s":601}})");
    CHECK(c.workers == 0 && c.long_running_global == 8 && c.watchdog_running_s == 30 && c.watchdog_queued_s == 30 &&
              warned("workers", "1..8") == 1 && warned("long_running_global", "1..16") == 1 &&
              warned("watchdog_running_s", "5..600") == 1 && warned("watchdog_queued_s", "5..600") == 1,
          "tasks config: out of bounds: the default and one warning naming the key and its bound");
    c = read(R"({"tasks":{"workers":"4","long_running_global":2.5,"watchdog_running_s":true,"watchdog_queued_s":null}})");
    CHECK(c.workers == 0 && c.long_running_global == 8 && c.watchdog_running_s == 30 && c.watchdog_queued_s == 30 &&
              warned("workers", "1..8") == 1 && warned("long_running_global", "1..16") == 1 &&
              warned("watchdog_running_s", "5..600") == 1 && warned("watchdog_queued_s", "5..600") == 1,
          "tasks config: a wrong type: the default and one warning each");
    c = read(R"({"tasks":7})");
    {
        std::lock_guard l(t_mutex);
        int n = 0;
        for (const auto& line : t_infos) n += line.find("\"tasks\"") != std::string::npos;
        CHECK(c.workers == 0 && c.long_running_global == 8 && n == 1, "tasks config: \"tasks\" not an object: defaults, one warning");
    }
    c = read(R"({"tasks":{"workers":2,"watchdog_running_s":60},"server":{"tasks":{"workers":4}},"client":{"tasks":{"workers":1}}})");
    CHECK(c.workers == 4 && c.watchdog_running_s == 60, "tasks config: the side section overrides key by key; the rest stays");
    c = read(R"({"tasks":{"workers":2},"server":{"tasks":{"workers":99}}})");
    CHECK(c.workers == 0 && warned("workers", "1..8") == 1, "tasks config: an invalid side value gives the default, warned");
    t_paths.kiln = old;
}

void client_preview(const std::filesystem::path& dir) {
    const auto old = t_paths.kiln;
    t_paths.kiln = dir / "preview-config";
    std::filesystem::create_directories(t_paths.kiln);
    const auto read = [&](const char* text) {
        kiln::g_config = kiln::Config{};
        kiln::g_doc = nlohmann::json{};
        write(t_paths.kiln / "kiln.json", text);
        kiln::load_config();
        kiln::apply_side(KILN_SIDE_CLIENT);
        return kiln::config();
    };
    CHECK(read(R"({"preview":true})").preview_all && read(R"({"preview":"all"})").preview_all,
          "preview config: true and all lift every gate");
    CHECK(read(R"({"preview":["camera.fov",7,"items.fuel"]})").preview ==
              std::vector<std::string>({"camera.fov", "items.fuel"}),
          "preview config: a list keeps names and ignores non-strings");
    CHECK(!read(R"({"preview":true,"client":{"preview":["camera.fov"]}})").preview_all &&
              kiln::config().preview == std::vector<std::string>({"camera.fov"}) &&
              !read(R"({"preview":true,"client":{"preview":false}})").preview_all,
          "preview config: the side list or false replaces top-level true");
    for (const char* text : {R"({"preview":7})", R"({"preview":"true"})", R"({"preview":{}})",
                             R"({"preview":true,"client":{"preview":null}})"}) {
        const auto cfg = read(text);
        CHECK(!cfg.preview_all && cfg.preview.empty(), "preview config: a wrong type leaves all gates held (%s)", text);
    }
    const auto bad = read(R"({"preview":true,"client":{"clr":"bad","preview":false}})");
    CHECK(!bad.preview_all && bad.preview.empty(),
          "preview config: an unrelated malformed side key cannot discard preview false");
    std::filesystem::remove_all(t_paths.kiln);
    t_paths.kiln = old;
}

// The unconfigured path is also used after boot's early safe-idle exit. Its first callers may be worker threads.
void client_gate_threads() {
    namespace gates = kiln::gates;
    std::vector<std::string> names;
    for (int i = 0; i < 2048; ++i) names.push_back("gate." + std::to_string(i));
    std::vector<gates::Gate> list;
    for (const auto& name : names) list.push_back({name.c_str(), "99-thread", gates::kBoth});
    list.push_back({nullptr, nullptr, 0});
    gates::use_list_for_tests(list.data());
    std::barrier start(16);
    std::atomic<bool> good{true};
    std::vector<std::thread> readers;
    for (int i = 0; i < 16; ++i) readers.emplace_back([&] {
        start.arrive_and_wait();
        for (int n = 0; n < 20; ++n)
            if (!gates::is_held("gate.1024") || gates::held().size() != names.size()) good = false;
    });
    for (auto& reader : readers) reader.join();
    CHECK(good, "gate: concurrent first reads in safe idle keep every held name");
    std::atomic<bool> done{false};
    readers.clear();
    for (int i = 0; i < 8; ++i) readers.emplace_back([&] {
        while (!done)
            if (!gates::is_held("gate.1024") || gates::held().size() != names.size()) good = false;
    });
    for (int i = 0; i < 100; ++i) gates::configure(KILN_SIDE_CLIENT, false, {});
    done = true;
    for (auto& reader : readers) reader.join();
    CHECK(good, "gate: boot configuration can overlap readers without invalidating their lists");
    gates::use_list_for_tests(nullptr);
    gates::configure(KILN_SIDE_SERVER, false, {});
}

// A mod's own symbol fragment (kiln.hooks load_symbols: Symbols.LoadShipped) gives "sig/<modid>/<Name>" rows like Kiln's
// own described symbols, for as long as it is loaded (the owner's dev.30 client pass, ui-hooks.c8: the row of
// ClientHookSample's own descriptor was never registered, so the sample's Capabilities.Is check kept its hook off).
void fragment_rows(const std::filesystem::path& dir) {
    Loaded x;
    sigdb::init_code();  // a second test image in this process; code()'s once_flag has already run
    reset();
    t_tick = t_lifecycle = true;
    resolve_with(dir, "48 B8 ?? ?? ?? ?? ?? ?? ?? ?? E8 {helper:rel32}");
    caps::core_rows(true, true, "");
    const auto file = dir / "m-client.json";
    const char* const full = R"({"format":1,"side":"client","symbols":{
      "m/Tail":{"find":[{"pattern":"8B 81 ?? ?? ?? ?? C3"}],
                "sig":{"ret":"u32","args":[{"name":"self","type":"ptr"}],"abi":{"reads":["rcx"],"stack":[],"ret":"rax"}}},
      "m/Wrong":{"find":[{"pattern":"8B 81 ?? ?? ?? ?? C3"}],"sig":{"args":[{"name":"scale","type":"f32"}]}},
      "m/Gone":{"find":[{"pattern":"DE AD BE EF 01 02 03 04 05"}],"sig":{"args":[]}}
    }})";
    write(file, full);
    CHECK(get("sig/m/Tail").rc == KILN_ERR_NOT_FOUND, "fragment: no row before the fragment loads");
    sigdb::FragmentSummary fs;
    std::string err;
    CHECK(sigdb::load_fragment("m", file, {}, &fs, &err), "fragment: loads (%s)", err.c_str());
    const uint64_t before = T->generation();
    caps::fragment_rows("m");
    CHECK(is("sig/m/Tail", A, KILN_CAP_REASON_NONE) && get("sig/m/Tail").source == "sigdb" && T->generation() > before,
          "fragment: a described symbol that resolved and fits is an available row, source sigdb (got %u %u '%s')",
          get("sig/m/Tail").state, get("sig/m/Tail").reason, get("sig/m/Tail").text.c_str());
    CHECK(is("sig/m/Wrong", U, KILN_CAP_REASON_SYMBOL_MISSING) &&
              get("sig/m/Wrong").text == "symbol m/Wrong: descriptor arg 1 'scale' is xmm0, the function reads [rcx]",
          "fragment: a descriptor that does not fit the code is off with sigdb's reason (got '%s')", get("sig/m/Wrong").text.c_str());
    CHECK(is("sig/m/Gone", U, KILN_CAP_REASON_SYMBOL_MISSING) && get("sig/m/Gone").text.rfind("symbol m/Gone: ", 0) == 0,
          "fragment: a pattern that matches nowhere is off, naming the symbol (got '%s')", get("sig/m/Gone").text.c_str());
    CHECK(is("items.fuel", A, 0) && get("items.fuel").source == "sigdb", "fragment: Kiln's own feature rows stay");

    // a reload (hot reload, a new version of the mod) replaces the rows: a name it no longer has goes
    write(file, R"({"format":1,"symbols":{
      "m/Tail":{"find":[{"pattern":"8B 81 ?? ?? ?? ?? C3"}],
                "sig":{"ret":"u32","args":[{"name":"self","type":"ptr"}],"abi":{"reads":["rcx"],"stack":[],"ret":"rax"}}}
    }})");
    CHECK(sigdb::load_fragment("m", file, {}, &fs, &err), "fragment: reloads");
    caps::fragment_rows("m");
    CHECK(is("sig/m/Tail", A, 0) && get("sig/m/Wrong").rc == KILN_ERR_NOT_FOUND && get("sig/m/Gone").rc == KILN_ERR_NOT_FOUND,
          "fragment: a reload keeps the rows it still has and drops the others");

    // the mod unloads: its rows go, Kiln's stay
    caps::fragment_gone("m");
    sigdb::unload_fragment("m");
    CHECK(get("sig/m/Tail").rc == KILN_ERR_NOT_FOUND && is("items.fuel", A, 0), "fragment: unloaded, its rows are gone");
    caps::fragment_gone("m");  // twice, or for a fragment never loaded: nothing
    caps::fragment_rows("nothing");
    CHECK(get("sig/nothing").rc == KILN_ERR_NOT_FOUND && is("items.fuel", A, 0), "fragment: an unknown id changes nothing");
    std::filesystem::remove(file);
}

int main(int argc, char** argv) {
    if (argc != 2) return 2;
    const std::filesystem::path dir = argv[1];
    static const kiln::gates::Gate empty_gates[] = {{nullptr, nullptr, 0}};
    kiln::gates::use_list_for_tests(empty_gates);  // the registry tests below supply their own rows
    rules();
    subscribers();
    reentrant_read_strings();
    core_rows(dir);
    fragment_rows(dir);
    client_gate(dir);
    client_gate_threads();
    client_preview(dir);
    tasks_config(dir);
    // kiln.net's Steam peer-to-peer messages row
    steam_messages_row(dir);
    json_file();
    writer();
    writer_retry();
    idle_tick_fault(dir);
    return g_fail ? 1 : 0;
}
