// SPDX-License-Identifier: MIT
// kiln.content gamedata, host test: the pack journal of gamedata.h (gd_pack_mark / gd_pack_rollback / gd_pack_unmark).
// A mod's pack that throws partway leaves nothing of its gamedata behind: packs.cpp marks the mod's owner
// before its first pack and rolls it back at the throw. Over the real gamedata_clones.cpp (clone definitions, the pack
// application's seen set, clones_packs_end's orphaning) and gamedata_packs.cpp (config adds, other services' sections),
// with no world (nothing is registered) and a fake service section:
//  - a template clone the marked owner's pack adds: orphaned at the next clones_packs_end, as a removed pack's clone
//  - a clone it redefines: its fields, body and texts from before, a new version, seen as before
//  - a clone it takes from another mod's pack: that pack's again
//  - config adds of its layers: forgotten (its array layer came off with store_drop_owner), another owner's stay
//  - another service's section it applied: reverted (owner "pack:<mod>", every source) and forgotten in this application;
//    one it only had from the application before comes off too and applies again with the packs from before
//  - one waiting for its service: forgotten
//  - another owner's rollback, and after gd_pack_unmark: nothing is journaled or touched
// gdundo_test.sh builds and runs it.
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <ctime>
#include <cctype>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <map>
#include <mutex>
#include <set>
#include <unordered_set>

#include "../gamedata.h"
#include "services/kiln_capacity_util.hpp"  // libstdc++'s fstream has its own __try: parsed before the macro below
#undef __try
#define __try if (true)
#define __except(x) else if (false)
inline long long _InterlockedOr64(volatile long long* p, long long v) {
    auto old = *p;
    *p = old | v;
    return old;
}
inline long long _InterlockedExchange64(volatile long long* p, long long v) {
    auto old = *p;
    *p = v;
    return old;
}
#include "../gamedata_clones.cpp"
#include "../gamedata_packs.cpp"

// ============================================================================================= the fake game =====
namespace fake {
std::vector<std::string> lines;
std::map<KilnOwner, std::string> labels;
struct Call {
    std::string what, section, owner, source, json;
};
std::vector<Call> calls;  // the fake service's apply / revert calls, in order
std::map<std::pair<std::string, std::string>, std::string> state;  // the service's: (owner, source) -> what it applied
KilnResult KILN_CALL apply(void*, KilnStr section, KilnStr json, KilnStr owner, KilnStr source, uint64_t, uint32_t, char* report,
                           size_t cap, size_t* len) {
    const std::string o(owner.ptr, owner.len), s(source.ptr, source.len), j(json.ptr, json.len);
    calls.push_back({"apply", std::string(section.ptr, section.len), o, s, j});
    state[{o, s}] = j;
    const std::string r = R"({"applied": 1})";
    std::snprintf(report, cap, "%s", r.c_str());
    *len = r.size();
    return KILN_OK;
}
void KILN_CALL revert(void*, KilnStr section, KilnStr owner, KilnStr source) {
    const std::string o(owner.ptr, owner.len), s(source.ptr, source.len);
    calls.push_back({"revert", std::string(section.ptr, section.len), o, s, ""});
    std::erase_if(state, [&](const auto& kv) { return (o.empty() || kv.first.first == o) && (s.empty() || kv.first.second == s); });
}
uintptr_t KILN_CALL world(uint32_t) { return 0; }
bool KILN_CALL on_world_thread(uint32_t) { return true; }
}  // namespace fake

namespace kc {
KilnApi g_fake_api = [] {
    KilnApi a{};
    a.size = sizeof a;
    a.world = &fake::world;
    a.on_world_thread = &fake::on_world_thread;
    return a;
}();
const KilnApi* g_api = &g_fake_api;
StoreMutex mu;
StoreMutex& store_mutex() { return mu; }
Engine E{};
bool rd(uintptr_t p, void* out, size_t n) {
    if (!p) return false;
    std::memcpy(out, reinterpret_cast<void*>(p), n);
    return true;
}
uint32_t fnv1a(const void* p, size_t n) {
    uint32_t h = 2166136261u;
    for (size_t i = 0; i < n; i++) h = (h ^ static_cast<const uint8_t*>(p)[i]) * 16777619u;
    return h;
}
bool guid_parse(const std::string& s, uint8_t out[16]) {
    std::string hex;
    for (char c : s)
        if (c != '-') hex += c;
    if (hex.size() != 32) return false;
    for (int i = 0; i < 16; ++i) out[i] = static_cast<uint8_t>(std::strtoul(hex.substr(i * 2, 2).c_str(), nullptr, 16));
    return true;
}
std::string guid_text(const uint8_t* g) {
    char b[40];
    std::snprintf(b, sizeof b, "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x", g[0], g[1], g[2], g[3], g[4], g[5],
                  g[6], g[7], g[8], g[9], g[10], g[11], g[12], g[13], g[14], g[15]);
    return b;
}
std::string owner_label(KilnOwner o) {
    const auto it = fake::labels.find(o);
    return it == fake::labels.end() ? "kiln" : it->second;
}
std::string mod_of_label(const std::string& label) {
    const size_t sp = label.find(' ');
    return sp == std::string::npos ? std::string() : label.substr(sp + 1);
}
void logf(int, const char* fmt, ...) {
    char b[2048];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(b, sizeof b, fmt, ap);
    va_end(ap);
    fake::lines.emplace_back(b);
    if (std::getenv("GDUNDO_TEST_LOG")) std::printf("  log: %s\n", b);
}
uintptr_t active_world() { return 0; }
uint32_t active_kind() { return KILN_WORLD_SERVER; }
bool map_read(uintptr_t, std::vector<uint32_t>*, std::vector<uint64_t>*) { return false; }
std::string data_dir() { return "/nonexistent/kiln/data"; }
PhaseTimer::PhaseTimer(Phase) {}
PhaseTimer::~PhaseTimer() {}
bool store_drop(KilnOwner, uintptr_t) { return false; }
size_t store_flush(std::string*) { return 0; }
void StoreMutex::lock() { m_.lock(); }
void StoreMutex::unlock() { m_.unlock(); }
bool StoreMutex::try_lock() { return m_.try_lock(); }
std::string lower(std::string s) {
    for (char& c : s) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return s;
}
// Reached from gamedata's other paths (resolving paths, building records, config adds); never called by these cases: the
// sections here are no gamedata sections, and no world registers anything.
KilnContentValue decode(const Field&, const uint8_t*) { return {}; }
double as_double(const KilnContentValue&) { return 0; }
bool nc_is_new(uint32_t, uint32_t) { return false; }
void* near_alloc(uintptr_t, size_t) { return nullptr; }
uint64_t next_code_seq() { return 1; }
bool store_original(uintptr_t, uint8_t, uint8_t*) { return false; }
bool store_put(KilnOwner, uint64_t, uint32_t, uint32_t, int32_t, int32_t, uintptr_t, uint8_t, const uint8_t*, const std::string&) { return false; }
namespace gd {
GRegistry g_none;
GRegistry& greg(uint32_t) { return g_none; }
const GEntry* GRegistry::get(uint32_t) const { return nullptr; }
const GEntry* GRegistry::lookup(const std::string&) const { return nullptr; }  // no world here: no entry by name or GUID
std::vector<const GEntry*> GRegistry::select(const std::string&) const { return {}; }
const char* section_name(uint32_t) { return "templates"; }
uint32_t section_of(const std::string&) { return 0; }
uint64_t generation() { return 1; }
Node root_of(uint32_t, const GEntry&) { return {}; }
bool g_first_missing = false;  // value_slot_absent: what resolve says of the path's first segment
bool resolve(Root, Node, const std::string&, std::vector<Resolved>*, std::string* err, bool* first_missing) {
    *err = "not in this test";
    if (first_missing) *first_missing = g_first_missing;
    return false;
}
bool valid_section(uint32_t s) { return s >= KILN_GD_TEMPLATES && s <= KILN_GD_ITEMS; }
bool identity_field(const std::string&) { return false; }
bool leaf_of(const TType*, Field*) { return false; }
bool blob_array(uintptr_t, uintptr_t*, uint32_t*) { return false; }
bool blob_variant(uintptr_t, uint32_t*, uintptr_t*, uint32_t*) { return false; }
std::string config_name(uint32_t) { return "?"; }
bool config_exemplar(uint32_t, Exemplar*, std::string* err) {
    *err = "not in this test";
    return false;
}
bool parse_leaf(const Field&, const gjson&, const uint8_t*, uint8_t*, bool*, std::string* err) {
    *err = "not in this test";
    return false;
}
const TType* type_hash(uint32_t) { return nullptr; }
const TType* type_named(const std::string&) { return nullptr; }
const TType* TType::inner() const { return nullptr; }
const TType* TType::resolved() const { return this; }
const TField* TType::field(const std::string&) const { return nullptr; }
}  // namespace gd
}  // namespace kc

// ============================================================================================= the cases =========
namespace {

int failures = 0;
void check(bool ok, const std::string& what) {
    std::printf("  %s %s\n", ok ? "ok  " : "FAIL", what.c_str());
    if (!ok) ++failures;
}

constexpr KilnOwner kA = 21, kB = 22;  // pack a, pack b
constexpr const char* kT1 = "22222222-0000-4000-8000-000000000001";  // added by a's pack that throws
constexpr const char* kT2 = "22222222-0000-4000-8000-000000000002";  // a's, redefined by the pack that throws
constexpr const char* kT3 = "22222222-0000-4000-8000-000000000003";  // b's, taken by a's pack that throws

std::string clone(const char* guid, const char* name, int value) {
    return std::string(R"({"guid": ")") + guid + R"(", "name": ")" + name + R"(", "base": "Base", "fields": {"x": )" +
           std::to_string(value) + "}}";
}
bool define(KilnOwner owner, const std::string& text) {
    std::string report;
    return kc::gd::clones_define(owner, KILN_GD_TEMPLATES, text, true, "content/x.json", &report) == KILN_OK;
}
const kc::gd::Def* def(const char* guid) {
    uint8_t g[16];
    kc::guid_parse(guid, g);
    for (const kc::gd::Def& d : kc::gd::g_defs)
        if (std::memcmp(d.guid, g, 16) == 0) return &d;
    return nullptr;
}
void sections(KilnOwner owner, const std::string& doc, const std::string& source = "content/x.json") {
    kc::ApplyResult r;
    kc::gd_pack_sections(owner, 0, kc::gjson::parse(doc), source, &r);
}
size_t count_calls(const std::string& what, const std::string& owner) {
    return static_cast<size_t>(std::count_if(fake::calls.begin(), fake::calls.end(), [&](const fake::Call& c) { return c.what == what && c.owner == owner; }));
}

void run() {
    fake::labels = {{kA, "pack a"}, {kB, "pack b"}};
    kc::gd::External x;
    x.name = "creatures";
    x.apply = &fake::apply;
    x.revert = &fake::revert;
    check(kc::gd::register_external(x) == KILN_OK, "the fake service registers its section \"creatures\"");

    // ---- the application before: a's pack defines T2 and applies creatures; b's pack defines T3 and applies creatures
    kc::gd_packs_begin();
    check(define(kA, clone(kT2, "TplTwo", 1)) && define(kB, clone(kT3, "TplThree", 3)), "before: a's pack clones T2, b's pack T3");
    sections(kA, R"({"creatures": {"v": "old"}})");
    sections(kB, R"({"creatures": {"v": "b"}})", "content/b.json");
    kc::gd_packs_end();
    check(fake::state[{"pack:a", "content/x.json"}] == R"({"v":"old"})" && fake::state[{"pack:b", "content/b.json"}] == R"({"v":"b"})",
          "before: the service holds a's and b's sections");
    const uint64_t t2_version = def(kT2)->version;

    // ---- the application with the pack that throws: b first, then a (marked)
    kc::gd_packs_begin();
    define(kB, clone(kT3, "TplThree", 3));
    sections(kB, R"({"creatures": {"v": "b"}})", "content/b.json");
    kc::gd::g_added[{kB, 0x2000}] = {kc::gd::Added{7, 0x9000, 1, 4}};  // b's layers added a config to an array
    kc::gd_pack_mark(kA);
    check(define(kA, clone(kT1, "TplOne", 10)), "throw: a's pack clones T1");
    check(define(kA, clone(kT2, "TplTwo", 20)) && def(kT2)->fields["x"] == 20, "throw: a's pack redefines T2 (x 20)");
    check(define(kA, clone(kT3, "TplThree", 30)) && def(kT3)->owner == kA, "throw: a's pack takes T3 from b's pack");
    kc::gd::g_added[{kA, 0x1000}] = {kc::gd::Added{5, 0x8000, 1, 4}, kc::gd::Added{6, 0x8010, 1, 4}};  // a's layers added two configs
    sections(kA, R"({"creatures": {"v": "new"}})");
    sections(kA, R"({"spawns": {"v": "waits"}})");  // a section of a service that has not registered: kept until it does
    check(fake::state[{"pack:a", "content/x.json"}] == R"({"v":"new"})", "throw: the service holds a's new section");
    fake::calls.clear();
    check(kc::gd_pack_rollback(kB) == 0 && def(kT3)->owner == kA && count_calls("revert", "pack:b") == 0, "another owner's rollback does nothing");
    const uint32_t n = kc::gd_pack_rollback(kA);
    check(n == 3 + 2 + 1 + 1, "rollback: three clones, two config adds, one section and one waiting section come off (" + std::to_string(n) + ")");
    check(def(kT1) && def(kT1)->orphan, "rollback: T1, which the pack added, is orphaned (its record stays reserved)");
    check(def(kT2)->fields["x"] == 1 && def(kT2)->owner == kA && def(kT2)->version == t2_version + 2 && !def(kT2)->orphan,
          "rollback: T2 is its definition from before (x 1), as a new version");
    check(def(kT3)->owner == kB && def(kT3)->fields["x"] == 3, "rollback: T3 is b's pack's again");
    check(!kc::gd::g_added.count({kA, 0x1000}) && kc::gd::g_added.count({kB, 0x2000}), "rollback: a's config adds are forgotten, b's stay");
    check(count_calls("revert", "pack:a") == 1 && fake::state.count({"pack:a", "content/x.json"}) == 0,
          "rollback: the service took a's section off (every source of pack:a)");
    check(fake::state[{"pack:b", "content/b.json"}] == R"({"v":"b"})", "rollback: b's section stays");
    {
        std::lock_guard lock(kc::gd::g_ext_mu);
        check(std::none_of(kc::gd::g_ext_cur.begin(), kc::gd::g_ext_cur.end(), [](const auto& a) { return a.o == kA; }),
              "rollback: this application has no section of a's any more");
        check(std::none_of(kc::gd::g_stash.begin(), kc::gd::g_stash.end(), [](const auto& a) { return a.o == kA; }),
              "rollback: a's section waiting for its service is forgotten");
    }
    check(kc::gd_pack_rollback(kA) == 0, "a second rollback with nothing new does nothing");

    // ---- the packs from before apply again in a's place: T2 as before and its section; then the application ends
    check(define(kA, clone(kT2, "TplTwo", 1)), "again: a's packs from before clone T2");
    sections(kA, R"({"creatures": {"v": "old"}})");
    kc::gd_pack_unmark();
    check(define(kA, clone(kT1, "TplOne", 10)) && !def(kT1)->orphan, "after the mark: a clones T1 (adopted)");
    check(kc::gd_pack_rollback(kA) == 0 && !def(kT1)->orphan, "unmarked: nothing is journaled, a rollback does nothing");
    fake::calls.clear();
    kc::gd_packs_end();
    check(fake::state[{"pack:a", "content/x.json"}] == R"({"v":"old"})" && fake::state[{"pack:b", "content/b.json"}] == R"({"v":"b"})",
          "end: the service holds a's section from before and b's");
    check(count_calls("revert", "pack:a") == 0, "end: a's section from before is not taken off again (it applied in this application)");
    check(!def(kT2)->orphan && !def(kT3)->orphan, "end: T2 and T3 stay (seen in this application)");
}

// registry-fixes item 4 (review-registry-reload perf note): packs.cpp marks every mod at every application, so the journal runs
// for every clone a pack re-applies, not only when a pack throws. It copied every clone a pack touched, found them by a
// linear scan of what it held, and its rollback looked each one up in every clone: quadratic per mod. Membership is a hash
// lookup now, the rollback finds clones through one index, and an unchanged redefinition keeps no copy.
// The thread's own CPU time (review-registry-fixes): a wall clock also counts the time the thread waits while other work runs,
// and the slot tool runs these tests at the lowest CPU priority, so on a busy runner a timed run that was paused read several
// times longer (x50 to x62 for a linear x8 in 3 of 8 runs of gdundo beside three busy loops on its core). Best of five rounds,
// the two sizes in turn in each, so a slower moment of the runner falls on both.
double cpu_seconds() {
    timespec t{};
    clock_gettime(CLOCK_THREAD_CPUTIME_ID, &t);
    return static_cast<double>(t.tv_sec) + static_cast<double>(t.tv_nsec) * 1e-9;
}
constexpr int kRounds = 5;
std::string bench_guid(int i) {
    char b[40];
    std::snprintf(b, sizeof b, "55555555-0000-4000-8000-%012d", i);
    return b;
}
void journal_cost() {
    constexpr KilnOwner kP = 41;
    constexpr int kSmall = 1000, kLarge = 8000;
    fake::labels[kP] = "pack p";
    kc::gd_packs_begin();
    for (int i = 0; i < kLarge; ++i) define(kP, clone(bench_guid(i).c_str(), ("Bench" + std::to_string(i)).c_str(), i));
    kc::gd_packs_end();
    std::vector<std::array<uint8_t, 16>> guids(kLarge);
    for (int i = 0; i < kLarge; ++i) kc::guid_parse(bench_guid(i), guids[i].data());
    check(def(bench_guid(kLarge - 1).c_str()) != nullptr, "cost: pack p clones " + std::to_string(kLarge) + " templates");
    // the journal's lookup alone: a second touch of each of n clones under the mark finds the first one's entry (best of five; the
    // first touches, which add the entries, are not timed). Linear: x8 for x8 clones; a scan per clone: about x64. One round:
    // the lookups' and the rollback's CPU time, each kept when it is the best so far.
    // (the clones as they are, found once: nothing below adds a clone, so g_defs keeps its places)
    std::vector<const kc::gd::Def*> now(kLarge);
    for (int i = 0; i < kLarge; ++i) now[i] = def(bench_guid(i).c_str());
    auto touches = [&](int first, int n, double* lookup, double* rollback) {
        kc::gd_pack_mark(kP);
        for (int i = 0; i < n; ++i) kc::gd::journal_touch(kP, guids[first + i].data(), now[first + i], false);
        double t0 = cpu_seconds();
        for (int i = 0; i < n; ++i) kc::gd::journal_touch(kP, guids[first + i].data(), now[first + i], false);
        *lookup = std::min(*lookup, cpu_seconds() - t0);
        t0 = cpu_seconds();
        kc::gd::clones_pack_rollback(kP);
        *rollback = std::min(*rollback, cpu_seconds() - t0);
        kc::gd_pack_unmark();
    };
    double small = 1e9, large = 1e9, unused = 1e9;
    for (int k = 0; k < kRounds; ++k) touches(0, kSmall, &small, &unused), touches(0, kLarge, &large, &unused);
    const double ratio = large / std::max(small, 1e-9);
    std::printf("  info journal: %d lookups %.3f ms, %d lookups %.3f ms (x%.1f for x%d)\n", kSmall, small * 1e3, kLarge, large * 1e3, ratio,
                kLarge / kSmall);
    check(ratio < 24, "cost: the journal's lookup grows linearly with the clones a pack touches (x" + std::to_string(ratio) + " for x8, at most x24)");
    // the rollback's lookup of each clone: the first and the last 1,000 of the 8,000 clones, the same work but for where the
    // clones are among all clones. One index: the same time; a scan of every clone for each: the last ones several times slower.
    double first_rb = 1e9, last_rb = 1e9;
    for (int k = 0; k < kRounds; ++k) touches(0, kSmall, &unused, &first_rb), touches(kLarge - kSmall, kSmall, &unused, &last_rb);
    const double where = last_rb / std::max(first_rb, 1e-9);
    std::printf("  info journal: rollback of the first %d clones %.2f ms, of the last %d %.2f ms (x%.2f)\n", kSmall, first_rb * 1e3, kSmall,
                last_rb * 1e3, where);
    check(where < 1.6, "cost: the rollback finds a clone without a scan of every clone (the last 1,000 x" + std::to_string(where) +
                           " the first 1,000, at most x1.6)");

    // the whole path at about 4,000 clones: the same pack applies again under the mark (unchanged), then a rollback
    constexpr int kPath = 4000;
    kc::gd_packs_begin();
    kc::gd_pack_mark(kP);
    double t0 = cpu_seconds();
    for (int i = 0; i < kPath; ++i) define(kP, clone(bench_guid(i).c_str(), ("Bench" + std::to_string(i)).c_str(), i));
    const double again = cpu_seconds() - t0;
    check(kc::gd::clones_pack_journal_copies() == 0, "cost: the same pack applying again copies no clone (" +
                                                         std::to_string(kc::gd::clones_pack_journal_copies()) + " copies)");
    t0 = cpu_seconds();
    const uint32_t back = kc::gd_pack_rollback(kP);
    const double rollback = cpu_seconds() - t0;
    std::printf("  info journal: %d unchanged redefinitions under the mark %.1f ms, their rollback %.1f ms\n", kPath, again * 1e3, rollback * 1e3);
    check(back == kPath, "cost: the rollback counts every clone the pack touched (" + std::to_string(back) + ")");
    check(def(bench_guid(7).c_str())->fields["x"] == 7 && def(bench_guid(7).c_str())->owner == kP && !def(bench_guid(7).c_str())->orphan,
          "cost: an unchanged clone rolled back is as at the mark");
    // one clone redefined unchanged, then changed, in one application: the rollback gives it its definition from the mark
    check(define(kP, clone(bench_guid(3).c_str(), "Bench3", 3)) && define(kP, clone(bench_guid(3).c_str(), "Bench3", 333)) &&
              def(bench_guid(3).c_str())->fields["x"] == 333,
          "cost: Bench3 redefined unchanged, then changed (x 333)");
    check(kc::gd::clones_pack_journal_copies() == 1, "cost: the change copied it, once");
    check(kc::gd_pack_rollback(kP) == 1 && def(bench_guid(3).c_str())->fields["x"] == 3, "cost: the rollback gives Bench3 its x 3 back");
    kc::gd_pack_unmark();
    kc::gd_packs_end();
}

}  // namespace

// review registry-r2a: gd_value_slot's "this entry has none of it" (GdValueSlot::absent, a per-entry refusal in Kiln words) is
// only for a template's component the entry lacks; a perk's or an arsenal's path whose first member the root type lacks is a
// mapping this build broke (every entry shares the type): "missing on this game build", for the proof a disagreement
void value_slot_absent() {
    static kc::gd::TType root;
    root.name = "keen::Perk", root.brief = "Perk";
    kc::gd::g_none.type = &root, kc::gd::g_none.available = true;
    kc::Entry e;
    e.id = 7, e.name = "Perk_Armor_Fog", e.addr = 0x1000;
    kc::gd::g_first_missing = true;
    for (const auto& [root_kind, section, want] : {std::tuple{kc::gd::Root::Template, uint32_t(KILN_GD_TEMPLATES), true},
                                                   std::tuple{kc::gd::Root::Plain, uint32_t(KILN_GD_PERKS), false},
                                                   std::tuple{kc::gd::Root::Plain, uint32_t(KILN_GD_ARSENALS), false}}) {
        kc::gd::g_none.root = root_kind;
        kc::GdValueSlot s;
        std::string why;
        const bool refused = !kc::gd_value_slot(section, e, "perkArmorSetup.isSet", &s, &why);
        check(refused && s.absent == want,
              std::string(want ? "a template without the component: this entry has none" : "a plain root without the member: a broken mapping, not an absent value") +
                  " (absent " + (s.absent ? "true" : "false") + ")");
    }
    kc::gd::g_first_missing = false;
    kc::gd::g_none = kc::gd::GRegistry{};
}

// dev.32: the registry's entry and slot over a gamedata section (gd_section_entry / gd_value_slot, the one writer the skills
// registry's live back-end shares with creatures, arsenals and perks) find nothing where this world has no such entry (a skill
// node by its 32-bit id: no GUID), and refuse a section that is not one of kiln.gamedata's (no world here: no entries).
void registry_slot_refusals() {
    kc::GdValueSlot slot;
    kc::Entry e;
    std::string why;
    check(!kc::gd_section_entry(KILN_GD_SKILLS, "", 0x4475e8dd, &e), "a skill node no world has: no entry");
    e.id = 1;
    check(!kc::gd_value_slot(99, e, "costs", &slot, &why) && why == "no such gamedata section", "another section: \"" + why + "\"");
    check(slot.addr == 0, "no slot is made");
}

int main() {
    run();
    journal_cost();
    value_slot_absent();
    registry_slot_refusals();
    if (failures) std::printf("gdundo-test: %d check(s) FAILED\n", failures);
    else std::printf("gdundo-test: all checks passed\n");
    return failures ? 1 : 0;
}
