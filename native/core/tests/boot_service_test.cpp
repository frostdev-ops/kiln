// SPDX-License-Identifier: MIT
// Host tests of kiln.boot (src/services/boot_service.cpp, abi/services/kiln_boot.h): the deferred-mode decision from
// the mods' manifests, staging, and the commit protocol with six fake collectors (BOOT-PREPARE-ATOMIC with a failure
// injected at each collector, the per-mod drop of coordinator ruling 7, contract breaks, warnings), the hold path
// through a fake OS-exit adapter (never the test runner's own exit), and the buffer rules.
// Run by test-boot-service.sh (ASan/UBSan): test-boot-service.sh [scratch dir]. Prints one line per case and
// "BOOT SERVICE TESTS <n> PASS <m> FAIL"; exits 1 when anything failed.
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <functional>
#include <map>
#include <stdexcept>
#include <string>
#include <vector>

#include <windows.h>  // tests/stubs/windows.h
struct EXCEPTION_POINTERS;
#ifndef _WIN32
#define __try try
#define __except(x) catch (...)
#endif
[[noreturn]] inline void ExitProcess(unsigned int) { std::abort(); }  // the adapter is replaced before any call

#include "../src/services/service.cpp"
#include "../src/services/boot_service.cpp"

namespace fs = std::filesystem;
using nlohmann::json;

// ---- stubs ----
namespace {
std::vector<std::string> t_log;
std::map<KilnOwner, std::string> t_owners;
std::vector<KilnOwner> t_revoked;
KilnOwner t_next_owner = 100;
kiln::Config t_config;
kiln::ImageInfo t_image;
kiln::Paths t_paths;
kiln::profile::State t_profile;
uint32_t t_dropins = 1u << KILN_PROFILE_KIND_KILN;

// The capability registry, reduced: per name the reports of each owner, the worst shown (kiln_caps.h).
std::map<std::string, std::map<KilnOwner, KilnCapRow>> t_caps;
std::map<std::string, std::string> t_texts;  // storage for the shown row's text
KilnResult KILN_CALL fake_report(KilnOwner owner, KilnStr name, uint32_t state, uint32_t reason, KilnStr text) {
    static std::map<std::string, std::string> keep;
    const std::string n(name.ptr, name.len), key = n + "#" + std::to_string(owner);
    keep[key] = std::string(text.ptr ? text.ptr : "", text.len);
    KilnCapRow row{};
    row.size = sizeof row;
    row.state = state;
    row.reason = reason;
    row.text = KilnStr{keep[key].data(), keep[key].size()};
    t_caps[n][owner] = row;
    return KILN_OK;
}
KilnResult KILN_CALL fake_find(KilnStr name, KilnCapRow* out) {
    const auto it = t_caps.find(std::string(name.ptr, name.len));
    if (it == t_caps.end() || it->second.empty()) return KILN_ERR_NOT_FOUND;
    const KilnCapRow* w = nullptr;
    auto rank = [](uint32_t s) { return s == KILN_CAP_UNAVAILABLE ? 0 : s == KILN_CAP_PENDING ? 1 : 2; };
    for (const auto& [o, r] : it->second)
        if (!w || rank(r.state) < rank(w->state)) w = &r;
    *out = *w;
    return KILN_OK;
}
KilnCapsApi t_caps_table = [] {
    KilnCapsApi t{};
    t.header = {sizeof(KilnCapsApi), 1};
    t.report = &fake_report;
    t.find = &fake_find;
    return t;
}();
const KilnCapRow* shown(const std::string& name) {
    static KilnCapRow r;
    r.size = sizeof r;
    return fake_find(KilnStr{name.data(), name.size()}, &r) == KILN_OK ? &r : nullptr;
}
}  // namespace

namespace kiln {
const Config& config() { return t_config; }
const ImageInfo& image() { return t_image; }
const Paths& paths() { return t_paths; }
double now_ms() { return 0; }
std::string to_utf8(std::wstring_view w) { return std::string(w.begin(), w.end()); }
std::wstring to_wide(std::string_view s) { return std::wstring(s.begin(), s.end()); }
bool read_file(const std::filesystem::path& file, std::string* out) {
    std::ifstream in(file, std::ios::binary);
    if (!in) return false;
    out->assign(std::istreambuf_iterator<char>(in), {});
    return true;
}
std::string jsonc_to_json(std::string_view text) { return std::string(text); }  // the fixtures are plain JSON
namespace log {
void writef(int, const char*, const char* fmt, ...) {
    char b[1024];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(b, sizeof b, fmt, ap);
    va_end(ap);
    t_log.emplace_back(b);
}
}  // namespace log
namespace reg {
KilnOwner owner_create(std::string_view id) {
    t_owners[t_next_owner] = std::string(id);
    return t_next_owner++;
}
void owner_revoke(KilnOwner owner) {
    t_owners.erase(owner);
    t_revoked.push_back(owner);
}
std::string owner_name(KilnOwner owner) {
    const auto it = t_owners.find(owner);
    return it == t_owners.end() ? "(revoked)" : it->second;
}
}  // namespace reg
namespace profile {
const State& state() { return t_profile; }
bool dropins(uint32_t kind) { return (t_dropins & (1u << kind)) != 0; }
}  // namespace profile
namespace crash {
int filter(EXCEPTION_POINTERS*, Capture*, unsigned long*) { return 1; }
void report(const Capture&, const char*, const void*) {}
}  // namespace crash
namespace caps {
const KilnCapsApi* table() { return &t_caps_table; }
}  // namespace caps
}  // namespace kiln

// ---- the checks ----
namespace {
int t_pass = 0, t_fail = 0;
std::string t_case;
#define CHECK(x) check((x), #x, __LINE__)
void check(bool ok, const char* what, int line) {
    if (ok) {
        ++t_pass;
        return;
    }
    ++t_fail;
    std::printf("FAIL %s: %s (line %d)\n", t_case.c_str(), what, line);
}

const KilnBootApiV1& B = kiln::boot_svc::kTable;
KilnStr ks(const std::string& s) { return KilnStr{s.data(), s.size()}; }

// The OS-exit adapter: records the code and returns (production never returns from ExitProcess).
std::vector<uint32_t> t_exits;
void fake_exit(uint32_t code) { t_exits.push_back(code); }
std::vector<std::string> t_held;  // write_state's last lines
void fake_write_state(void*, const std::string& last) { t_held.push_back(last); }

// ---- six fake collectors ----
enum class Do { Ok, Refuse, RefuseNobody, Error, OkNoPlan, Throw, Warn, OkWithError };
struct Fake {
    int index = 0;
    Do act = Do::Ok;
    int act_round = 0;     // the round (1-based) the action applies to; 0: every round
    std::string blame;     // Refuse: the participant
    int calls = 0;
    std::vector<json> seen;  // the IR of each call
};
Fake t_fake[6];
std::vector<std::string> t_events;  // "prepare:<c>", "destroy:<c>:<round>"
struct Plan {
    int collector, round;
};

void KILN_CALL destroy_plan(const void* p) {
    const auto* plan = static_cast<const Plan*>(p);
    t_events.push_back("destroy:" + std::string(kiln::boot_svc::kCollectors[plan->collector]) + ":" +
                       std::to_string(plan->round));
    delete plan;
}

KilnResult KILN_CALL fake_prepare(void* user, const KilnBootPrepareInputV1* in, KilnBootPreparedV1* out, KilnBootIssueV1* issue) {
    auto& f = *static_cast<Fake*>(user);
    ++f.calls;
    t_events.push_back("prepare:" + std::string(kiln::boot_svc::kCollectors[f.index]));
    f.seen.push_back(json::parse(std::string(in->canonical_ir_json.ptr, in->canonical_ir_json.len)));
    const Do act = f.act_round == 0 || f.act_round == f.calls ? f.act : Do::Ok;
    auto plan = [&] { return new Plan{f.index, f.calls}; };
    switch (act) {
        case Do::Ok:
            out->ok = 1;
            out->plan = plan();
            out->destroy = &destroy_plan;
            return KILN_OK;
        case Do::Warn:
            out->ok = 1;
            out->plan = plan();
            out->destroy = &destroy_plan;
            issue->code = KILN_BOOT_ISSUE_WARNING | 0x10001u;
            std::snprintf(issue->reason, sizeof issue->reason, "a warning");
            return KILN_OK;
        case Do::OkWithError:
            out->ok = 1;
            out->plan = plan();
            out->destroy = &destroy_plan;
            issue->code = 0x10002u;
            return KILN_OK;
        case Do::Refuse:
        case Do::RefuseNobody:
            out->ok = 0;
            issue->code = KILN_BOOT_ISSUE_PREPARE_REFUSED;
            if (act == Do::Refuse) std::snprintf(issue->mod_id, sizeof issue->mod_id, "%s", f.blame.c_str());
            std::snprintf(issue->kind, sizeof issue->kind, "items");
            std::snprintf(issue->key, sizeof issue->key, "bad-item");
            std::snprintf(issue->reason, sizeof issue->reason, "over the item table's capacity");
            return KILN_OK;
        case Do::Error: return KILN_ERR_INTERNAL;
        case Do::OkNoPlan: out->ok = 1; return KILN_OK;
        case Do::Throw: throw std::runtime_error("collector fault");
    }
    return KILN_OK;
}

void reset_boot() {
    using namespace kiln::boot_svc;
    g_decided = g_deferred = g_bound = false;
    g_why.clear();
    g_boot_mods.clear();
    g_status = KILN_BOOT_STATUS_COLLECTING;
    g_owners.clear();
    g_by_author.clear();
    g_host = 0;
    g_envelope = nullptr;
    g_envelope_bytes = 0;
    g_issues.clear();
    for (auto& c : g_collectors) c = {};
    // A published root lives until the process exits; between cases the test frees it and its fake plans itself.
    if (const Root* r = g_root.exchange(nullptr)) {
        for (const void* p : r->plans) delete static_cast<const Plan*>(p);
        delete r;
    }
    g_self = 0;
    g_exit_code = 0;
    g_exit = &fake_exit;
    t_exits.clear();
    t_held.clear();
    t_events.clear();
    t_revoked.clear();
    t_caps.clear();
    t_log.clear();
    for (int i = 0; i < 6; ++i) {
        t_fake[i] = Fake{};
        t_fake[i].index = i;
    }
}

fs::path t_root;
void write_mod(const fs::path& dir, const json& manifest) {
    fs::create_directories(dir);
    std::ofstream(dir / "kiln.mod.json") << manifest.dump(2);
}

// A profile with one Kiln mod folder per entry, and deferred mode decided with the server's rows in place.
struct ModSpec {
    std::string id;
    json boot;  // null: no "boot" key
    bool enabled = true;
    std::string side = "server";
};
void profile_with(const std::vector<ModSpec>& mods) {
    t_profile = kiln::profile::State{};
    t_profile.mode = KILN_PROFILE_ACTIVE;
    const fs::path base = t_root / ("profile-" + std::to_string(t_next_owner));
    for (const auto& m : mods) {
        json man = {{"id", m.id}, {"name", m.id}, {"version", "1.0.0"}, {"apiVersion", "0.2"}, {"side", m.side}};
        if (!m.boot.is_null()) man["boot"] = m.boot;
        write_mod(base / m.id, man);
        kiln::profile::Entry e;
        e.id = m.id;
        e.path = (base / m.id).string();
        e.enabled = m.enabled;
        t_profile.mods[KILN_PROFILE_KIND_KILN].push_back(e);
    }
}

void server_rows(bool feature) {
    fake_report(0, ks("mods.boot"), feature ? KILN_CAP_AVAILABLE : KILN_CAP_UNAVAILABLE,
                feature ? KILN_CAP_REASON_NONE : KILN_CAP_REASON_SYMBOL_MISSING, ks(feature ? "" : "symbol assets/ResourceInit: no match"));
}

// Deferred mode with authors `ids` (required ones end in "!"), every collector registered, bound.
void deferred_with(const std::vector<std::string>& ids) {
    reset_boot();
    t_image.side = KILN_SIDE_SERVER;
    t_config.clr = true;
    std::vector<ModSpec> mods;
    for (auto id : ids) {
        const bool req = id.back() == '!';
        if (req) id.pop_back();
        mods.push_back({id, req ? json("required") : json(true)});
    }
    profile_with(mods);
    server_rows(true);
    kiln::boot_svc::decide(true, "");
    for (int i = 5; i >= 0; --i)  // registration order is not the prepare order
        B.collector(50 + i, ks(kiln::boot_svc::kCollectors[i]), &fake_prepare, &t_fake[i]);
    kiln::boot_svc::bound();
}

std::map<std::string, KilnOwner> t_authors;
KilnOwner author_of(const std::string& id) {
    const auto it = t_authors.find(id);
    return it != t_authors.end() ? it->second : (t_authors[id] = 1000 + t_authors.size());
}
constexpr KilnOwner kHost = 7;

// OnBoot of each author: one staged item; required ones with the REQUIRED role.
void run_authors(const std::vector<std::string>& ids, const std::string& fail_callback = "") {
    for (auto id : ids) {
        const bool req = id.back() == '!';
        if (req) id.pop_back();
        uint64_t gen = 0;
        const auto a = author_of(id);
        CHECK(B.begin_owner(a, ks(id), KILN_BOOT_ROLE_DEDICATED | (req ? KILN_BOOT_ROLE_REQUIRED : 0u), &gen) == KILN_OK);
        CHECK(gen == 1);
        KilnBootStageResultV1 r{};
        r.size = sizeof r;
        const std::string doc = R"({"name":"Boot Token","stack":20})";
        CHECK(B.stage(a, gen, ks("items"), ks("token"), ks(doc), &r) == KILN_OK && r.ok == 1);
        CHECK(B.finish_owner(a, gen, id == fail_callback ? KILN_BOOT_FINISH_CALLBACK_FAILED : KILN_BOOT_FINISH_ACCEPT) == KILN_OK);
    }
}

// The envelope: every author as an owner (with "requires"), one document per owner for every collector.
std::string envelope(const std::vector<std::string>& ids, const std::map<std::string, std::vector<std::string>>& needs = {},
                     const std::vector<std::string>& skip_collectors = {}) {
    json e = {{"schema", 1}, {"owners", json::array()}, {"reservations", {{"worldtables", json::array()}}}, {"collectors", json::object()}};
    for (auto id : ids) {
        if (id.back() == '!') id.pop_back();
        json o = {{"id", id}, {"roles", 1}, {"fingerprint", "00"}};
        if (needs.count(id)) o["requires"] = needs.at(id);
        e["owners"].push_back(o);
        e["reservations"]["worldtables"].push_back({{"owner", id}, {"key", "slot"}, {"index", 1}});
        for (const char* c : kiln::boot_svc::kCollectors) {
            if (std::find(skip_collectors.begin(), skip_collectors.end(), c) != skip_collectors.end()) continue;
            e["collectors"][c].push_back({{"owner", id}, {"key", "token"}, {"document", {{"x", 1}}}});
        }
    }
    return e.dump();
}

KilnBootCommitResultV1 commit_with(const std::string& env) {
    KilnBootStageResultV1 r{};
    r.size = sizeof r;
    if (!env.empty()) CHECK(B.stage(kHost, 1, ks("ir"), ks("envelope"), ks(env), &r) == KILN_OK && r.ok == 1);
    KilnBootCommitResultV1 c{};
    c.size = sizeof c;
    CHECK(B.commit(kHost, 1, &c) == KILN_OK);
    return c;
}

uint32_t state_of(const std::string& id) {
    KilnBootOwnerStateV1 s{};
    s.size = sizeof s;
    B.owner_state(ks(id), &s);
    return s.state;
}

bool no_plan_published() {
    for (const char* c : kiln::boot_svc::kCollectors) {
        uint64_t g = 99;
        if (B.active_plan(ks(c), &g) || g != 0) return false;
    }
    return true;
}

bool all_plans_published() {
    for (const char* c : kiln::boot_svc::kCollectors) {
        uint64_t g = 0;
        if (!B.active_plan(ks(c), &g) || g != 1) return false;
    }
    return true;
}

std::vector<std::string> events_with(const std::string& prefix) {
    std::vector<std::string> out;
    for (const auto& e : t_events)
        if (e.rfind(prefix, 0) == 0) out.push_back(e);
    return out;
}

void pass_line(const std::string& id, int fail_before) {
    std::printf("%s %s\n", id.c_str(), t_fail == fail_before ? "PASS" : "FAIL");
}

// ---- cases ----
void decision_cases() {
    t_case = "decide";
    const int f0 = t_fail;
    // hooks off: no deferred mode, the rows say why
    reset_boot();
    profile_with({{"a", true}});
    server_rows(true);
    kiln::boot_svc::decide(false, "build x is not verified");
    KilnBootCommitResultV1 st{};
    st.size = sizeof st;
    CHECK(B.status(&st) == KILN_ERR_UNAVAILABLE);
    CHECK(B.collector(1, ks("assets"), &fake_prepare, &t_fake[0]) == KILN_ERR_UNAVAILABLE);
    CHECK(shown("mods.boot.phase") && shown("mods.boot.phase")->state == KILN_CAP_UNAVAILABLE);
    CHECK(shown("mods.boot.game_data") && shown("mods.boot.game_data")->reason == KILN_CAP_REASON_POLICY);
    // no boot mod
    reset_boot();
    profile_with({{"a", nullptr}, {"b", false}});
    server_rows(true);
    kiln::boot_svc::decide(true, "");
    CHECK(!kiln::boot_svc::g_deferred);
    CHECK(kiln::boot_svc::g_why == "no enabled mod has \"boot\": true");
    // a boot mod: deferred, Collecting
    reset_boot();
    profile_with({{"a", nullptr}, {"b", true}});
    server_rows(true);
    kiln::boot_svc::decide(true, "");
    CHECK(kiln::boot_svc::g_deferred);
    CHECK(B.status(&st) == KILN_OK && st.status == KILN_BOOT_STATUS_COLLECTING && st.generation == 1);
    CHECK(shown("mods.boot.phase") && shown("mods.boot.phase")->state == KILN_CAP_PENDING);
    CHECK(shown("mods.boot.client") && shown("mods.boot.client")->reason == KILN_CAP_REASON_WRONG_SIDE);
    // "required"
    reset_boot();
    profile_with({{"r", "required"}});
    server_rows(true);
    kiln::boot_svc::decide(true, "");
    CHECK(kiln::boot_svc::g_deferred && kiln::boot_svc::g_boot_mods == std::vector<std::string>{"r (required)"});
    // other values are not a boot mod
    reset_boot();
    profile_with({{"x", "yes"}, {"y", 1}});
    server_rows(true);
    kiln::boot_svc::decide(true, "");
    CHECK(!kiln::boot_svc::g_deferred);
    // disabled in the profile
    reset_boot();
    profile_with({{"b", true, false}});
    server_rows(true);
    kiln::boot_svc::decide(true, "");
    CHECK(!kiln::boot_svc::g_deferred);
    // a client-only boot mod on a server
    reset_boot();
    profile_with({{"c", true, true, "client"}});
    server_rows(true);
    kiln::boot_svc::decide(true, "");
    CHECK(!kiln::boot_svc::g_deferred);
    // .NET off
    reset_boot();
    profile_with({{"b", true}});
    server_rows(true);
    t_config.clr = false;
    kiln::boot_svc::decide(true, "");
    CHECK(!kiln::boot_svc::g_deferred);
    t_config.clr = true;
    // mods.boot unresolved: refused, the on_ready paths run
    reset_boot();
    profile_with({{"b", true}});
    server_rows(false);
    kiln::boot_svc::decide(true, "");
    CHECK(!kiln::boot_svc::g_deferred && kiln::boot_svc::g_why.find("mods.boot is unavailable") == 0);
    // a game client: the client gate holds mods.boot.client (the core's own report, as caps.cpp makes it)
    reset_boot();
    t_image.side = KILN_SIDE_CLIENT;
    profile_with({{"c", true, true, "client"}});
    server_rows(true);
    fake_report(0, ks("mods.boot.client"), KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_POLICY,
                ks("game test pending: 08-boot.first-start-definitions, 08-loader-order"));
    kiln::boot_svc::decide(true, "");
    CHECK(!kiln::boot_svc::g_deferred && kiln::boot_svc::g_why.find("client pass") != std::string::npos);
    CHECK(kiln_caps_held(&t_caps_table, ks("mods.boot.client")));
    CHECK(t_caps["mods.boot.client"][kiln::boot_svc::g_self].state == KILN_CAP_AVAILABLE);  // kiln.boot's own report
    // ... and with the gate lifted (kiln.json "preview"): a client boot profile is deferred
    reset_boot();
    server_rows(true);
    kiln::boot_svc::decide(true, "");
    CHECK(kiln::boot_svc::g_deferred);
    t_image.side = KILN_SIDE_SERVER;
    // drop-ins: kiln/mods/<folder>, .disabled, and one the profile lists (the profile wins)
    reset_boot();
    t_profile = kiln::profile::State{};
    t_profile.mode = KILN_PROFILE_LEGACY;
    t_paths.mods = t_root / "dropins";
    write_mod(t_paths.mods / "d1", {{"id", "d1"}, {"boot", true}});
    std::ofstream(t_paths.mods / "d1" / ".disabled") << "";
    server_rows(true);
    kiln::boot_svc::decide(true, "");
    CHECK(!kiln::boot_svc::g_deferred);
    reset_boot();
    fs::remove(t_paths.mods / "d1" / ".disabled");
    server_rows(true);
    kiln::boot_svc::decide(true, "");
    CHECK(kiln::boot_svc::g_deferred);
    reset_boot();
    t_profile.mode = KILN_PROFILE_ACTIVE;
    kiln::profile::Entry listed;
    listed.id = "d1";
    listed.path = (t_root / "elsewhere").string();
    listed.enabled = false;
    t_profile.mods[KILN_PROFILE_KIND_KILN].push_back(listed);
    server_rows(true);
    kiln::boot_svc::decide(true, "");
    CHECK(!kiln::boot_svc::g_deferred);
    t_dropins = 0;  // the profile loads no drop-ins
    t_profile.mods[KILN_PROFILE_KIND_KILN].clear();
    reset_boot();
    server_rows(true);
    kiln::boot_svc::decide(true, "");
    CHECK(!kiln::boot_svc::g_deferred);
    t_dropins = 1u << KILN_PROFILE_KIND_KILN;
    t_paths.mods = t_root / "no-dropins";  // the later cases see the profile's mods only
    pass_line("BOOT-DECIDE (manifest scan, client gate, safe idle)", f0);
}

void staging_cases() {
    t_case = "staging";
    const int f0 = t_fail;
    deferred_with({"a", "b"});
    uint64_t gen = 0;
    const KilnOwner a = 31, b = 32;
    CHECK(B.begin_owner(0, ks("a"), 1, &gen) == KILN_ERR_INVALID_ARGUMENT);
    CHECK(B.begin_owner(a, ks("Bad"), 1, &gen) == KILN_ERR_INVALID_ARGUMENT);
    CHECK(B.begin_owner(a, ks("a"), 0, &gen) == KILN_ERR_INVALID_ARGUMENT);
    CHECK(B.begin_owner(a, ks("a"), 8, &gen) == KILN_ERR_INVALID_ARGUMENT);
    CHECK(B.begin_owner(a, ks("a"), 1, &gen) == KILN_OK && gen == 1);
    CHECK(B.begin_owner(a, ks("b"), 1, &gen) == KILN_ERR_INVALID_ARGUMENT);   // the same author twice
    CHECK(B.begin_owner(b, ks("a"), 1, &gen) == KILN_ERR_INVALID_ARGUMENT);   // the same id twice
    CHECK(shown("mods.boot.phase") && shown("mods.boot.phase")->state == KILN_CAP_AVAILABLE);
    KilnBootStageResultV1 r{};
    r.size = sizeof r;
    const std::string doc = R"({"name":"Boot Token"})";
    for (const char* key : {"Bad", "a/b", "a:b", "a..b", "1a", ""})
        CHECK(B.stage(a, 1, ks("items"), ks(key), ks(doc), &r) == KILN_ERR_INVALID_ARGUMENT);
    CHECK(B.stage(a, 1, ks("items"), ks(std::string(97, 'a')), ks(doc), &r) == KILN_ERR_INVALID_ARGUMENT);
    CHECK(B.stage(a, 1, ks("items"), ks(std::string(96, 'a')), ks(doc), &r) == KILN_OK && r.ok == 1);
    CHECK(B.stage(a, 1, ks("weapons"), ks("w"), ks(doc), &r) == KILN_ERR_INVALID_ARGUMENT);  // not a boot kind
    CHECK(B.stage(a, 1, ks("items"), ks("k"), ks("[1]"), &r) == KILN_ERR_INVALID_ARGUMENT);   // not an object
    CHECK(B.stage(a, 1, ks("items"), ks("k"), ks("{"), &r) == KILN_ERR_INVALID_ARGUMENT);
    CHECK(B.stage(a, 2, ks("items"), ks("k"), ks(doc), &r) == KILN_ERR_INVALID_ARGUMENT);     // wrong generation
    CHECK(B.stage(99, 1, ks("items"), ks("k"), ks(doc), &r) == KILN_ERR_INVALID_ARGUMENT);    // not an author
    const std::string big = "{\"s\":\"" + std::string(KILN_BOOT_DEFINITION_MAX, 'x') + "\"}";
    CHECK(B.stage(a, 1, ks("items"), ks("big"), ks(big), &r) == KILN_ERR_INVALID_ARGUMENT);
    // too small: nothing written
    KilnBootStageResultV1 small{};
    std::memset(&small, 0xCD, sizeof small);
    small.size = sizeof small - 1;
    CHECK(B.stage(a, 1, ks("items"), ks("k"), ks(doc), &small) == KILN_ERR_INVALID_ARGUMENT && small.ok == 0xCDCDCDCDu);
    // staged, copied, idempotent, DuplicateDefinition
    std::string buf = doc;
    CHECK(B.stage(a, 1, ks("items"), ks("token"), ks(buf), &r) == KILN_OK && r.ok == 1);
    std::fill(buf.begin(), buf.end(), 'z');  // the caller's bytes change: the staged copy does not
    CHECK(B.stage(a, 1, ks("items"), ks("token"), ks(doc), &r) == KILN_OK && r.ok == 1);
    CHECK(B.stage(a, 1, ks("items"), ks("token"), ks(R"({"name":"Other"})"), &r) == KILN_OK && r.ok == 0 &&
          std::string(r.reason).rfind("DuplicateDefinition:", 0) == 0);
    CHECK(B.stage(a, 1, ks("spells"), ks("token"), ks(doc), &r) == KILN_OK && r.ok == 1);  // another kind, same key
    // the offline view: grammar, then no reader in this build
    char out[64];
    size_t need = 0;
    for (const char* q : {"item:Material_T1_Wood_Logs", "template:NPC_Workshop_Fisher_Trader", "skill:0x8efcc472",
                          "material:StoneRough", "material:Cell12", "resource:0f8fad5b-d9cb-469f-a165-70867728950e/1a2b3c4d/0"})
        CHECK(B.read_resource(a, ks(q), out, sizeof out, &need) == KILN_ERR_UNAVAILABLE);
    for (const char* q : {"item:", "item:a/b", "item:../x", "item:http://x", "skill:0x8efcc47", "skill:8efcc472",
                          "material:Stone-Rough", "resource:0F8FAD5B-D9CB-469F-A165-70867728950E/1a2b3c4d/0",
                          "resource:0f8fad5b-d9cb-469f-a165-70867728950e/1a2b3c4d/01", "resource:0f8fad5b/1a2b3c4d/0",
                          "resource:0f8fad5b-d9cb-469f-a165-70867728950e/1a2b3c4d/4294967296", "npc:Fisher", "Item:X"})
        CHECK(B.read_resource(a, ks(q), out, sizeof out, &need) == KILN_ERR_INVALID_ARGUMENT);
    // state
    KilnBootOwnerStateV1 s{};
    s.size = sizeof s;
    CHECK(B.owner_state(ks("a"), &s) == KILN_OK && s.state == KILN_BOOT_STATE_STAGED && s.definition_count == 3 &&
          s.issue_count == 1 && s.generation == 1 && std::string(s.mod_id) == "a");
    CHECK(B.owner_state(ks("nobody"), &s) == KILN_OK && s.state == KILN_BOOT_STATE_NOT_PARTICIPATING);
    KilnBootOwnerStateV1 s_small{};
    std::memset(&s_small, 0xCD, sizeof s_small);
    s_small.size = sizeof s_small - 1;
    CHECK(B.owner_state(ks("a"), &s_small) == KILN_ERR_INVALID_ARGUMENT && s_small.state == 0xCDCDCDCDu);
    // a closed frame: PhaseClosed, REVOKED for reads
    CHECK(B.finish_owner(a, 1, 3) == KILN_ERR_INVALID_ARGUMENT);
    CHECK(B.finish_owner(a, 1, KILN_BOOT_FINISH_ACCEPT) == KILN_OK);
    CHECK(B.finish_owner(a, 1, KILN_BOOT_FINISH_ACCEPT) == KILN_ERR_INVALID_ARGUMENT);
    CHECK(B.stage(a, 1, ks("items"), ks("late"), ks(doc), &r) == KILN_OK && r.ok == 0 &&
          std::string(r.reason).rfind("PhaseClosed:", 0) == 0);
    CHECK(B.read_resource(a, ks("item:Material_T1_Wood_Logs"), out, sizeof out, &need) == KILN_ERR_REVOKED);
    CHECK(shown("mods.boot.phase") && shown("mods.boot.phase")->state == KILN_CAP_PENDING);
    // collectors: closed after bound(); unknown ids, null functions and a second registration are refused before it
    CHECK(B.collector(1, ks("assets"), &fake_prepare, &t_fake[0]) == KILN_ERR_UNAVAILABLE);
    kiln::boot_svc::g_bound = false;
    CHECK(B.collector(1, ks("assets"), &fake_prepare, &t_fake[0]) == KILN_ERR_INVALID_ARGUMENT);  // registered
    CHECK(B.collector(1, ks("textures"), &fake_prepare, &t_fake[0]) == KILN_ERR_INVALID_ARGUMENT);
    kiln::boot_svc::g_collectors[0] = {};
    CHECK(B.collector(1, ks("assets"), nullptr, nullptr) == KILN_ERR_INVALID_ARGUMENT);
    kiln::boot_svc::g_bound = true;
    // the envelope's checks
    auto stage_ir = [&](KilnOwner o, const std::string& e) { return B.stage(o, 1, ks("ir"), ks("envelope"), ks(e), &r); };
    CHECK(stage_ir(kHost, "{") == KILN_ERR_INVALID_ARGUMENT);
    CHECK(stage_ir(kHost, R"({"schema":2})") == KILN_ERR_INVALID_ARGUMENT);
    CHECK(stage_ir(kHost, R"({"schema":1,"owners":[{"id":"a"}],"collectors":{"textures":[]}})") == KILN_ERR_INVALID_ARGUMENT);
    CHECK(stage_ir(kHost, R"({"schema":1,"owners":[{"id":"a"}],"collectors":{"items":[{"owner":"z","key":"k"}]}})") ==
          KILN_ERR_INVALID_ARGUMENT);
    CHECK(stage_ir(kHost, R"({"schema":1,"owners":[{"id":"a"}],"collectors":{"content":[{"owner":"z","key":"k"}]}})") ==
          KILN_ERR_INVALID_ARGUMENT);
    CHECK(stage_ir(kHost, R"({"schema":1,"owners":[{"id":"a"},{"id":"a"}]})") == KILN_ERR_INVALID_ARGUMENT);
    CHECK(stage_ir(a, R"({"schema":1})") == KILN_ERR_INVALID_ARGUMENT);  // an author is not the host
    CHECK(B.stage(kHost, 1, ks("ir"), ks("other"), ks(R"({"schema":1})"), &r) == KILN_ERR_INVALID_ARGUMENT);
    CHECK(stage_ir(kHost, R"({"schema":1})") == KILN_OK && r.ok == 1);
    CHECK(stage_ir(kHost, R"({"schema":1})") == KILN_OK && r.ok == 0);  // once
    KilnBootCommitResultV1 c{};
    std::memset(&c, 0xCD, sizeof c);
    c.size = sizeof c - 1;
    CHECK(B.commit(kHost, 1, &c) == KILN_ERR_INVALID_ARGUMENT && c.status == 0xCDCDCDCDu);
    c.size = sizeof c;
    CHECK(B.commit(8, 1, &c) == KILN_ERR_INVALID_ARGUMENT);   // not the owner that staged the envelope
    CHECK(B.commit(a, 1, &c) == KILN_ERR_INVALID_ARGUMENT);   // an author
    CHECK(B.commit(kHost, 2, &c) == KILN_ERR_INVALID_ARGUMENT);
    KilnBootCommitResultV1 st{};
    st.size = sizeof st;
    CHECK(B.status(&st) == KILN_OK && st.status == KILN_BOOT_STATUS_COLLECTING);  // the refusals changed nothing
    pass_line("BOOT-STAGE (grammar, copy, idempotent, DuplicateDefinition, PhaseClosed, envelope)", f0);
}

void publish_case() {
    t_case = "publish";
    const int f0 = t_fail;
    deferred_with({"a", "b"});
    run_authors({"a", "b"});
    CHECK(no_plan_published());
    const auto c = commit_with(envelope({"a", "b"}));
    CHECK(c.status == KILN_BOOT_STATUS_PUBLISHED && c.generation == 1 && c.owner_count == 2);
    CHECK(all_plans_published());
    CHECK(state_of("a") == KILN_BOOT_STATE_COMMITTED && state_of("b") == KILN_BOOT_STATE_COMMITTED);
    // the fixed order, whatever the registration order
    CHECK(events_with("prepare:") == (std::vector<std::string>{"prepare:assets", "prepare:worldtables", "prepare:content",
                                                               "prepare:skills", "prepare:knowledge", "prepare:vfx"}));
    CHECK(events_with("destroy:").empty());  // a published plan lives until the process exits
    // what a prepare sees: the generation, nothing excluded, the process owners
    const json& ir = t_fake[2].seen.at(0);
    CHECK(ir["generation"] == 1 && ir["excluded"].empty() && ir["owners"].size() == 2);
    CHECK(ir["owners"][0]["owner"].get<KilnOwner>() != 0 && t_owners[ir["owners"][0]["owner"].get<KilnOwner>()] == "boot:a");
    CHECK(ir["collectors"]["content"].size() == 2);
    // after the commit the phase is closed
    KilnBootCommitResultV1 again{};
    again.size = sizeof again;
    CHECK(B.commit(kHost, 1, &again) == KILN_ERR_UNAVAILABLE);
    uint64_t gen = 0;
    CHECK(B.begin_owner(77, ks("late"), 1, &gen) == KILN_ERR_UNAVAILABLE);
    KilnBootStageResultV1 r{};
    r.size = sizeof r;
    CHECK(B.stage(author_of("a"), 1, ks("items"), ks("late"), ks("{}"), &r) == KILN_OK && r.ok == 0);
    CHECK(shown("mods.boot.phase") && shown("mods.boot.phase")->state == KILN_CAP_PENDING);
    // the start goes on: no hold, no exit
    CHECK(kiln::boot_svc::after_clr(true, &fake_write_state, nullptr) && t_exits.empty() && t_held.empty());
    const json rep = kiln::boot_svc::report();
    CHECK(rep["deferred"] == true && rep["status"] == "published" && rep["published"] == true && rep["exitCode"].is_null());
    pass_line("BOOT-PUBLISH (six plans in one root, fixed order, generation 1)", f0);
}

// BOOT-PREPARE-ATOMIC: a required participant's refusal at collector `at` aborts everything.
void atomic_required_case(int at) {
    t_case = std::string("atomic-required-") + kiln::boot_svc::kCollectors[at];
    const int f0 = t_fail;
    deferred_with({"req!", "other", "third"});
    run_authors({"req!", "other", "third"});
    t_fake[at].act = Do::Refuse;
    t_fake[at].blame = "req";
    const auto c = commit_with(envelope({"req!", "other", "third"}));
    CHECK(c.status == KILN_BOOT_STATUS_ABORTED);
    CHECK(no_plan_published());
    // every plan prepared before the refusal is destroyed, last first; nothing after it was prepared
    std::vector<std::string> expect;
    for (int i = at - 1; i >= 0; --i) expect.push_back("destroy:" + std::string(kiln::boot_svc::kCollectors[i]) + ":1");
    CHECK(events_with("destroy:") == expect);
    CHECK(events_with("prepare:").size() == static_cast<size_t>(at + 1));
    CHECK(state_of("req") == KILN_BOOT_STATE_REJECTED_PREPARE);
    CHECK(state_of("other") == KILN_BOOT_STATE_GENERATION_ABORTED && state_of("third") == KILN_BOOT_STATE_GENERATION_ABORTED);
    // the entry point stays held: boot.json first, then the exit adapter with 0x4B01
    CHECK(!kiln::boot_svc::after_clr(true, &fake_write_state, nullptr));
    CHECK(t_exits == std::vector<uint32_t>{0x4B01u} && t_held.size() == 1 && t_held[0].find("0x4B01") != std::string::npos);
    CHECK(t_held[0].find("req") != std::string::npos);
    const json rep = kiln::boot_svc::report();
    CHECK(rep["status"] == "aborted" && rep["published"] == false && rep["exitCode"] == 0x4B01);
    pass_line(std::string("BOOT-PREPARE-ATOMIC required refusal at ") + kiln::boot_svc::kCollectors[at], f0);
}

// A refusal no participant is blamed for, at collector `at`: everything aborts, everyone GenerationAborted.
void atomic_unattributed_case(int at) {
    t_case = std::string("atomic-nobody-") + kiln::boot_svc::kCollectors[at];
    const int f0 = t_fail;
    deferred_with({"a", "b"});
    run_authors({"a", "b"});
    t_fake[at].act = Do::RefuseNobody;
    const auto c = commit_with(envelope({"a", "b"}));
    CHECK(c.status == KILN_BOOT_STATUS_ABORTED && no_plan_published());
    CHECK(events_with("destroy:").size() == static_cast<size_t>(at));
    CHECK(state_of("a") == KILN_BOOT_STATE_GENERATION_ABORTED && state_of("b") == KILN_BOOT_STATE_GENERATION_ABORTED);
    CHECK(!kiln::boot_svc::after_clr(true, &fake_write_state, nullptr) && t_exits == std::vector<uint32_t>{0x4B01u});
    pass_line(std::string("BOOT-PREPARE-ATOMIC unattributed refusal at ") + kiln::boot_svc::kCollectors[at], f0);
}

// Ruling 7: an optional participant's refusal at collector `at` drops it and its dependent; the rest publishes.
void drop_case(int at) {
    t_case = std::string("drop-") + kiln::boot_svc::kCollectors[at];
    const int f0 = t_fail;
    deferred_with({"opt", "dep", "keep"});
    run_authors({"opt", "dep", "keep"});
    t_fake[at].act = Do::Refuse;
    t_fake[at].act_round = 1;
    t_fake[at].blame = "opt";
    const auto c = commit_with(envelope({"opt", "dep", "keep"}, {{"dep", {"opt"}}}));
    CHECK(c.status == KILN_BOOT_STATUS_PUBLISHED && all_plans_published());
    // round 1's plans destroyed last first, round 2 prepared every collector and nothing of it was destroyed
    std::vector<std::string> expect;
    for (int i = at - 1; i >= 0; --i) expect.push_back("destroy:" + std::string(kiln::boot_svc::kCollectors[i]) + ":1");
    CHECK(events_with("destroy:") == expect);
    CHECK(events_with("prepare:").size() == static_cast<size_t>(at + 1 + 6));
    CHECK(state_of("opt") == KILN_BOOT_STATE_REJECTED_PREPARE && state_of("dep") == KILN_BOOT_STATE_EXCLUDED_DEPENDENCY &&
          state_of("keep") == KILN_BOOT_STATE_COMMITTED);
    // what round 2 saw: neither dropped mod, in "excluded" so the collectors skip their files too
    const json& ir = t_fake[5].seen.back();
    CHECK(ir["excluded"] == json::array({"dep", "opt"}));
    CHECK(ir["owners"].size() == 1 && ir["owners"][0]["id"] == "keep");
    CHECK(ir["collectors"]["vfx"].size() == 1 && ir["collectors"]["vfx"][0]["owner"] == "keep");
    CHECK(ir["reservations"]["worldtables"].size() == 1 && ir["reservations"]["worldtables"][0]["owner"] == "keep");
    CHECK(t_revoked.size() == 2);  // boot:opt and boot:dep
    CHECK(kiln::boot_svc::after_clr(true, &fake_write_state, nullptr) && t_exits.empty());
    pass_line(std::string("BOOT-DROP-OPTIONAL (ruling 7) refusal at ") + kiln::boot_svc::kCollectors[at], f0);
}

void dependent_required_case() {
    t_case = "drop-required-dependent";
    const int f0 = t_fail;
    deferred_with({"opt", "dep!"});
    run_authors({"opt", "dep!"});
    t_fake[1].act = Do::Refuse;
    t_fake[1].blame = "opt";
    const auto c = commit_with(envelope({"opt", "dep!"}, {{"dep", {"opt"}}}));
    CHECK(c.status == KILN_BOOT_STATUS_ABORTED && no_plan_published());
    CHECK(state_of("opt") == KILN_BOOT_STATE_REJECTED_PREPARE && state_of("dep") == KILN_BOOT_STATE_EXCLUDED_DEPENDENCY);
    CHECK(!kiln::boot_svc::after_clr(true, &fake_write_state, nullptr) && t_exits == std::vector<uint32_t>{0x4B01u});
    pass_line("BOOT-DROP-OPTIONAL a required dependent of a dropped mod stops the start", f0);
}

void callback_cases() {
    t_case = "callback";
    const int f0 = t_fail;
    // optional: excluded with its dependents, the rest publishes
    deferred_with({"bad", "dep", "ok"});
    run_authors({"bad", "dep", "ok"}, "bad");
    auto c = commit_with(envelope({"ok", "dep"}, {{"dep", {"bad"}}}));
    CHECK(c.status == KILN_BOOT_STATUS_PUBLISHED);
    CHECK(state_of("bad") == KILN_BOOT_STATE_EXCLUDED_CALLBACK && state_of("dep") == KILN_BOOT_STATE_EXCLUDED_DEPENDENCY &&
          state_of("ok") == KILN_BOOT_STATE_COMMITTED);
    KilnBootOwnerStateV1 s{};
    s.size = sizeof s;
    CHECK(B.owner_state(ks("bad"), &s) == KILN_OK && s.definition_count == 0);  // its staged definitions are gone
    CHECK(t_fake[0].seen.at(0)["excluded"] == json::array({"bad", "dep"}));
    // required: the start stops before any collector prepares
    deferred_with({"req!", "ok"});
    run_authors({"req!", "ok"}, "req");
    c = commit_with(envelope({"ok"}));
    CHECK(c.status == KILN_BOOT_STATUS_ABORTED && no_plan_published() && events_with("prepare:").empty());
    CHECK(state_of("req") == KILN_BOOT_STATE_EXCLUDED_CALLBACK && state_of("ok") == KILN_BOOT_STATE_GENERATION_ABORTED);
    CHECK(!kiln::boot_svc::after_clr(true, &fake_write_state, nullptr) && t_exits == std::vector<uint32_t>{0x4B01u});
    // a frame still open at the commit counts as failed
    deferred_with({"open", "ok"});
    uint64_t gen = 0;
    CHECK(B.begin_owner(author_of("open"), ks("open"), 1, &gen) == KILN_OK);
    run_authors({"ok"});
    c = commit_with("");
    CHECK(c.status == KILN_BOOT_STATUS_PUBLISHED && state_of("open") == KILN_BOOT_STATE_EXCLUDED_CALLBACK);
    pass_line("BOOT-CALLBACK-EXCLUDED (native side: optional, required, open frame)", f0);
}

void contract_cases() {
    const struct {
        Do act;
        const char* name;
        size_t destroyed;
    } cases[] = {{Do::Error, "a non-OK result", 2}, {Do::OkNoPlan, "ok without a plan", 2}, {Do::Throw, "a fault", 2},
                 {Do::OkWithError, "ok with an error code", 3}};
    for (const auto& k : cases) {
        t_case = std::string("contract-") + k.name;
        const int f0 = t_fail;
        deferred_with({"a"});
        run_authors({"a"});
        t_fake[2].act = k.act;
        const auto c = commit_with(envelope({"a"}));
        CHECK(c.status == KILN_BOOT_STATUS_ABORTED && no_plan_published());
        CHECK(events_with("destroy:").size() == k.destroyed);  // with an error code the collector's own plan goes too
        CHECK(state_of("a") == KILN_BOOT_STATE_GENERATION_ABORTED);
        CHECK(!kiln::boot_svc::after_clr(true, &fake_write_state, nullptr) && t_exits == std::vector<uint32_t>{0x4B01u});
        pass_line(std::string("BOOT-PREPARE-ATOMIC collector contract: ") + k.name, f0);
    }
}

void warning_case() {
    t_case = "warning";
    const int f0 = t_fail;
    deferred_with({"a"});
    run_authors({"a"});
    t_fake[3].act = Do::Warn;
    const auto c = commit_with(envelope({"a"}));
    CHECK(c.status == KILN_BOOT_STATUS_PUBLISHED && all_plans_published() && events_with("destroy:").empty());
    uint32_t need = 0;
    CHECK(B.issues(1, nullptr, 0, sizeof(KilnBootIssueV1), &need) == KILN_ERR_BUFFER_TOO_SMALL && need == 1);
    KilnBootIssueV1 is{};
    CHECK(B.issues(1, &is, 1, sizeof is, &need) == KILN_OK && (is.code & KILN_BOOT_ISSUE_WARNING) && is.size == sizeof is);
    CHECK(state_of("a") == KILN_BOOT_STATE_COMMITTED);
    CHECK(kiln::boot_svc::after_clr(true, &fake_write_state, nullptr) && t_exits.empty());
    pass_line("BOOT-WARNING (bit 31) does not abort", f0);
}

void missing_collector_cases() {
    t_case = "missing-collector";
    const int f0 = t_fail;
    // vfx does not register: mods.boot unavailable; an optional mod with vfx documents is dropped
    reset_boot();
    t_image.side = KILN_SIDE_SERVER;
    profile_with({{"a", true}, {"b", true}});
    server_rows(true);
    kiln::boot_svc::decide(true, "");
    for (int i = 0; i < 5; ++i) B.collector(50 + i, ks(kiln::boot_svc::kCollectors[i]), &fake_prepare, &t_fake[i]);
    kiln::boot_svc::bound();
    CHECK(shown("mods.boot") && shown("mods.boot")->state == KILN_CAP_UNAVAILABLE &&
          shown("mods.boot")->reason == KILN_CAP_REASON_SERVICE_MISSING);
    run_authors({"a", "b"});
    json e = json::parse(envelope({"a", "b"}, {}, {"vfx"}));
    e["collectors"]["vfx"] = json::array({{{"owner", "b"}, {"key", "glow"}, {"document", json::object()}}});
    auto c = commit_with(e.dump());
    CHECK(c.status == KILN_BOOT_STATUS_PUBLISHED && state_of("b") == KILN_BOOT_STATE_REJECTED_PREPARE &&
          state_of("a") == KILN_BOOT_STATE_COMMITTED);
    uint64_t g = 7;
    CHECK(B.active_plan(ks("vfx"), &g) == nullptr && g == 0 && B.active_plan(ks("assets"), &g) && g == 1);
    CHECK(events_with("prepare:").size() == 5);  // dropped before anything prepared
    // the same for a required mod: the start stops
    reset_boot();
    profile_with({{"a", true}, {"b", "required"}});
    server_rows(true);
    kiln::boot_svc::decide(true, "");
    for (int i = 0; i < 5; ++i) B.collector(50 + i, ks(kiln::boot_svc::kCollectors[i]), &fake_prepare, &t_fake[i]);
    kiln::boot_svc::bound();
    run_authors({"a", "b!"});
    c = commit_with(e.dump());
    CHECK(c.status == KILN_BOOT_STATUS_ABORTED && no_plan_published());
    CHECK(!kiln::boot_svc::after_clr(true, &fake_write_state, nullptr) && t_exits == std::vector<uint32_t>{0x4B01u});
    pass_line("BOOT-COLLECTOR-MISSING (mods.boot off, owner dropped or start stopped)", f0);
}

void not_committed_cases() {
    for (const bool clr : {true, false}) {
        t_case = clr ? "not-committed" : "clr-down";
        const int f0 = t_fail;
        deferred_with({"a"});
        if (clr) run_authors({"a"});
        CHECK(!kiln::boot_svc::after_clr(clr, &fake_write_state, nullptr));
        CHECK(t_exits == std::vector<uint32_t>{0x4B01u} && no_plan_published());
        KilnBootCommitResultV1 st{};
        st.size = sizeof st;
        CHECK(B.status(&st) == KILN_OK && st.status == KILN_BOOT_STATUS_ABORTED);
        if (clr) CHECK(state_of("a") == KILN_BOOT_STATE_GENERATION_ABORTED);
        const json rep = kiln::boot_svc::report();
        bool named = false;
        for (const auto& i : rep["issues"]) named |= i["code"] == "NotCommitted";
        CHECK(named);
        pass_line(clr ? "BOOT-HOLD Kiln.Host never committed: exit 0x4B01" : "BOOT-HOLD .NET did not start: exit 0x4B01", f0);
    }
    // no deferred mode: after_clr lets the start go on whatever happened
    t_case = "not-deferred";
    const int f0 = t_fail;
    reset_boot();
    profile_with({{"a", nullptr}});
    server_rows(true);
    kiln::boot_svc::decide(true, "");
    CHECK(kiln::boot_svc::after_clr(false, &fake_write_state, nullptr) && t_exits.empty());
    pass_line("BOOT-NO-BOOT-PROFILE takes no hold path", f0);
}

void buffer_cases() {
    t_case = "buffers";
    const int f0 = t_fail;
    deferred_with({"a", "b"});
    run_authors({"a", "b"}, "a");  // one issue
    KilnBootStageResultV1 r{};
    r.size = sizeof r;
    CHECK(B.stage(author_of("b"), 1, ks("items"), ks("x"), ks("{}"), &r) == KILN_OK);  // closed: no issue
    uint32_t need = 0;
    CHECK(B.issues(1, nullptr, 0, sizeof(KilnBootIssueV1), &need) == KILN_ERR_BUFFER_TOO_SMALL && need == 1);
    CHECK(B.issues(1, nullptr, 0, sizeof(KilnBootIssueV1) - 1, &need) == KILN_ERR_INVALID_ARGUMENT);
    CHECK(B.issues(1, nullptr, 0, sizeof(KilnBootIssueV1), nullptr) == KILN_ERR_INVALID_ARGUMENT);
    CHECK(B.issues(2, nullptr, 0, sizeof(KilnBootIssueV1), &need) == KILN_OK && need == 0);  // another generation
    // two records needed, room for one: nothing written
    {
        KilnBootStageResultV1 r2{};
        r2.size = sizeof r2;
        uint64_t gen = 0;
        B.begin_owner(55, ks("c"), 1, &gen);
        B.stage(55, 1, ks("items"), ks("k"), ks(R"({"a":1})"), &r2);
        B.stage(55, 1, ks("items"), ks("k"), ks(R"({"a":2})"), &r2);  // DuplicateDefinition: a second issue
    }
    std::vector<uint8_t> buf(sizeof(KilnBootIssueV1) * 2 + 64, 0xCD);
    CHECK(B.issues(1, reinterpret_cast<KilnBootIssueV1*>(buf.data()), 1, sizeof(KilnBootIssueV1), &need) ==
              KILN_ERR_BUFFER_TOO_SMALL && need == 2);
    CHECK(std::all_of(buf.begin(), buf.end(), [](uint8_t b) { return b == 0xCD; }));
    // a larger stride: records `stride` apart, each with the v1 size, the gap untouched
    const uint32_t stride = sizeof(KilnBootIssueV1) + 32;
    std::vector<uint8_t> wide(stride * 2, 0xCD);
    CHECK(B.issues(1, reinterpret_cast<KilnBootIssueV1*>(wide.data()), 2, stride, &need) == KILN_OK && need == 2);
    KilnBootIssueV1 first{}, second{};
    std::memcpy(&first, wide.data(), sizeof first);
    std::memcpy(&second, wide.data() + stride, sizeof second);
    CHECK(first.size == sizeof first && first.code == KILN_BOOT_ISSUE_CALLBACK_FAILED && std::string(first.mod_id) == "a");
    CHECK(second.code == KILN_BOOT_ISSUE_DUPLICATE_DEFINITION && std::string(second.key) == "k");
    CHECK(std::all_of(wide.begin() + sizeof first, wide.begin() + stride, [](uint8_t b) { return b == 0xCD; }));
    // status and commit with too-small results write nothing
    KilnBootCommitResultV1 st{};
    std::memset(&st, 0xCD, sizeof st);
    st.size = 8;
    CHECK(B.status(&st) == KILN_ERR_INVALID_ARGUMENT && st.status == 0xCDCDCDCDu && st.generation == 0xCDCDCDCDCDCDCDCDull);
    pass_line("BOOT-BUFFERS too-small outputs write nothing", f0);
}
}  // namespace

int main(int argc, char** argv) {
    t_root = fs::path(argc > 1 ? argv[1] : ".") / "boot-service-test";
    fs::remove_all(t_root);
    fs::create_directories(t_root);
    t_paths.mods = t_root / "no-dropins";
    decision_cases();
    staging_cases();
    publish_case();
    for (int i = 0; i < 6; ++i) atomic_required_case(i);
    for (int i = 0; i < 6; ++i) atomic_unattributed_case(i);
    for (int i = 0; i < 6; ++i) drop_case(i);
    dependent_required_case();
    callback_cases();
    contract_cases();
    warning_case();
    missing_collector_cases();
    not_committed_cases();
    buffer_cases();
    reset_boot();
    fs::remove_all(t_root);
    std::printf("BOOT SERVICE TESTS %d PASS %d FAIL\n", t_pass, t_fail);
    return t_fail ? 1 : 0;
}
