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
#include "../src/services/caps.cpp"

#include <cstdarg>
#include <condition_variable>
#include <future>

// ---- stubs ----
namespace {
std::mutex t_mutex;  // the stubs' state below
std::map<KilnOwner, std::string> t_owners;
KilnOwner t_next_owner = 1;
std::atomic<uint64_t> t_version{1};
std::atomic<bool> t_tick{true}, t_lifecycle{true};
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
double fake_clock() {
    std::lock_guard l(t_clock_mutex);
    return t_now;
}
void set_clock(double v) {
    {
        std::lock_guard l(t_clock_mutex);
        t_now = v;
    }
    kiln::caps::g_wcv.notify_all();  // the writer looks again (a real wake-up would come from its own timeout)
}
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
bool installed() { return t_tick; }
bool lifecycle() { return t_lifecycle; }
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
    for (auto& s : caps::g_subs) s->live = false;
    caps::g_subs.clear();
    caps::g_generation = 1;
    caps::g_order.clear();
    caps::g_order_gen = 0;
    caps::g_rows_built = false;
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
    bool all = true;
    for (const char* f : {"items.fuel", "net.kick", "tick", "tick.wait_jobs", "world.lifecycle"})
        all = all && is(f, A, KILN_CAP_REASON_NONE) && get(f).source == "sigdb";
    CHECK(all, "features: a resolver run with every symbol found gives every feature available, source sigdb");
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
    CHECK(is("tick", U, KILN_CAP_REASON_FAULTED) && is("tick.wait_jobs", U, KILN_CAP_REASON_FAULTED) && seen.size() == 2,
          "features: a tick hook that faults later shows at the next read, with a change each");
    t_lifecycle = false;
    caps::feature_off("world.lifecycle");  // KilnApi.feature answered false
    CHECK(caps::g_names.find("world.lifecycle")->second.last.reason == KILN_CAP_REASON_FAULTED,
          "features: KilnApi.feature() answering false corrects the row");
    T->unsubscribe(h);
    t_tick = t_lifecycle = true;

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

void json_file() {
    using nlohmann::json;
    reset();
    caps::core_rows(false, false, "the game image cannot be parsed");
    const KilnOwner o = owner("host");
    rep(o, "items.moves", P, KILN_CAP_REASON_SESSION, "no world is loaded yet");
    rep(o, "z.future", U, 11, "bad \xFF byte");
    rep(o, "a.ok", A, 0, "v3");
    json doc = json::parse(caps::render(std::chrono::milliseconds(1000)));
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
    doc = json::parse(caps::render(std::chrono::milliseconds(1000)));
    CHECK(doc["hooks"] == true && doc["safeIdle"].is_null() && doc["exe"]["verified"] == true,
          "json: hooks on, safeIdle null");
}

bool wait_writes(size_t n) {
    for (int i = 0; i < 300 && writes() < n; ++i) std::this_thread::sleep_for(std::chrono::milliseconds(10));
    return writes() >= n;
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
    std::this_thread::sleep_for(std::chrono::milliseconds(150));
    set_clock(10999);
    std::this_thread::sleep_for(std::chrono::milliseconds(150));
    CHECK(writes() == 1, "writer: a change within the second waits");
    set_clock(11000);
    CHECK(wait_writes(2) && writes() == 2 && last_write().find("w.one") != std::string::npos,
          "writer: written once the second is over");
    for (int i = 0; i < 20; ++i) {
        rep(o, "w.many", U, KILN_CAP_REASON_POLICY, "step " + std::to_string(i));
        set_clock(11000 + i * 40);
    }
    std::this_thread::sleep_for(std::chrono::milliseconds(150));
    CHECK(writes() == 2, "writer: twenty changes in the next second, no write yet");
    set_clock(12000);
    CHECK(wait_writes(3), "writer: then one write");
    std::this_thread::sleep_for(std::chrono::milliseconds(150));
    CHECK(writes() == 3 && last_write().find("step 19") != std::string::npos, "writer: one write with the last state");
    set_clock(50000);
    std::this_thread::sleep_for(std::chrono::milliseconds(150));
    CHECK(writes() == 3 && caps::flush() && writes() == 3, "writer: nothing dirty, nothing written (thread or flush)");
    rep(o, "w.last", A, 0);
    CHECK(caps::flush() && writes() == 4 && last_write().find("w.last") != std::string::npos,
          "writer: flush writes what is dirty at once, inside the second");
    set_clock(60000);
    std::this_thread::sleep_for(std::chrono::milliseconds(150));
    CHECK(writes() == 4, "writer: after flush the thread has nothing left to write");
}

size_t attempts() {
    std::lock_guard l(t_mutex);
    return t_attempts.size();
}
bool wait_attempts(size_t n) {
    for (int i = 0; i < 300 && attempts() < n; ++i) std::this_thread::sleep_for(std::chrono::milliseconds(10));
    return attempts() >= n;
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
    return t_write_cv.wait_for(l, std::chrono::seconds(3), [] { return t_in_write; });
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
    std::this_thread::sleep_for(std::chrono::milliseconds(150));
    set_clock(70999);
    std::this_thread::sleep_for(std::chrono::milliseconds(150));
    CHECK(attempts() == a0 + 1, "retry: no second try within the second (no busy loop)");
    set_clock(71000);
    CHECK(wait_attempts(a0 + 2) && writes() == w0, "retry: tried again once the second is over, with no new change; fails again");
    set_clock(71999);
    std::this_thread::sleep_for(std::chrono::milliseconds(150));
    CHECK(attempts() == a0 + 2, "retry: still at most one try a second across failures");
    set_clock(72000);
    CHECK(wait_writes(w0 + 1) && attempts() == a0 + 3 && last_write().find("r.one") != std::string::npos,
          "retry: the third try writes, and the file holds the change");
    std::this_thread::sleep_for(std::chrono::milliseconds(150));
    set_clock(90000);
    std::this_thread::sleep_for(std::chrono::milliseconds(150));
    CHECK(attempts() == a0 + 3 && writes() == w0 + 1, "retry: then parked, nothing dirty");
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

    // flush during the writer's write in flight
    const size_t w2 = writes();
    rep(o, "r.three", A, 0);
    block(true);
    set_clock(91000);  // past flush's last try (90000): the writer takes the write and stays inside it
    const bool entered = wait_in_write();
    std::atomic<bool> returned{false};
    bool result = false;
    std::thread f([&] {
        result = caps::flush();
        returned = true;
    });
    std::this_thread::sleep_for(std::chrono::milliseconds(200));
    const bool waited = !returned;
    block(false);
    f.join();
    CHECK(entered && waited && result && writes() == w2 + 1 && last_write().find("r.three") != std::string::npos,
          "flush: waits for the write in flight and answers true once it is in the file (one write)");

    // the write in flight fails: flush writes itself
    const size_t w3 = writes();
    rep(o, "r.four", A, 0);
    fail_next(1);
    block(true);
    set_clock(92000);
    const bool entered2 = wait_in_write();
    returned = false;
    result = false;
    std::thread g([&] {
        result = caps::flush();
        returned = true;
    });
    std::this_thread::sleep_for(std::chrono::milliseconds(200));
    const bool waited2 = !returned;
    block(false);
    g.join();
    CHECK(entered2 && waited2 && result && writes() == w3 + 1 && last_write().find("r.four") != std::string::npos,
          "flush: a write in flight that fails is done again by flush, which then answers true");
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
    // Let the writer park with a clean file. Moving this fake clock does not wake it after the fault.
    std::this_thread::sleep_for(std::chrono::milliseconds(100));
    const size_t before = writes();
    const auto start = std::chrono::steady_clock::now();
    {
        std::lock_guard l(t_clock_mutex);
        t_now = 101000;
    }
    t_tick = t_lifecycle = false;
    const auto deadline = start + std::chrono::milliseconds(1250);
    while ((seen.size() < 3 || writes() == before) && std::chrono::steady_clock::now() < deadline)
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    bool rows = true;
    {
        std::lock_guard l(caps::g_mutex);
        for (const char* name : {"tick", "tick.wait_jobs", "world.lifecycle"}) {
            const auto& row = caps::g_names.at(name).last;
            rows = rows && row.state == U && row.reason == KILN_CAP_REASON_FAULTED;
        }
    }
    CHECK(rows && seen.size() == 3 && writes() > before,
          "idle fault: rows, subscriber snapshot and file follow within 1250 ms without a registry call (rows %d, events %zu, writes %zu -> %zu)",
          rows, seen.size(), before, writes());
    const auto doc = nlohmann::json::parse(last_write());
    unsigned faulted = 0;
    for (const auto& row : doc["capabilities"])
        if ((row["name"] == "tick" || row["name"] == "tick.wait_jobs" || row["name"] == "world.lifecycle") &&
            row["state"] == "unavailable" && row["reason"] == "faulted") ++faulted;
    CHECK(faulted == 3, "idle fault: capabilities.json holds all three faulted rows");
    T->unsubscribe(h);
    reset();
    sigdb::g_core = sigdb::Resolver();
}

}  // namespace

int main(int argc, char** argv) {
    if (argc != 2) return 2;
    const std::filesystem::path dir = argv[1];
    rules();
    subscribers();
    core_rows(dir);
    json_file();
    writer();
    writer_retry();
    idle_tick_fault(dir);
    return g_fail ? 1 : 0;
}
