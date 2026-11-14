// SPDX-License-Identifier: MIT
// kiln.boot v1 (abi/services/kiln_boot.h): Mod.OnBoot's definition phase. The contract, the phase's steps, the IR
// envelope and the failure policy are in the header; this file keeps the process's one generation.
//
// State. One mutex guards everything but the published root, which is one atomic pointer (active_plan() reads it
// without a lock). Collector callbacks and capability reports run with the mutex released, so a prepare or a caps
// subscriber that calls back into kiln.boot does not deadlock: commit() moves the status to PREPARING first, and every
// call that would change the phase answers as for a closed phase while it runs.
//
// boot.cpp drives it: decide() before the service DLLs load (deferred mode on or off, and the rows), bound() after
// every on_ready (registration closes; mods.boot says whether a collector is missing), after_clr() once .NET started
// (anything but a published generation holds the entry point and ends the process through g_exit), report() for
// boot.json. The host test (native/core/tests/boot_service_test.cpp) includes this file with stubs for the rest.
#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <map>
#include <mutex>
#include <set>
#include <string>
#include <vector>

#include "../config.h"
#include "../crash.h"
#include "../image.h"
#include "../log.h"
#include "../profile.h"
#include "../registry.h"
#include "nlohmann/json.hpp"
#include "services/kiln_boot.h"
#include "services/kiln_caps.h"
#include "boot_gamedata.h"
#include "service.h"

namespace kiln::caps {
const KilnCapsApi* table();
}

namespace kiln::boot_svc {
namespace {

using nlohmann::json;

constexpr size_t kCollectorCount = 7;
// The fixed prepare order (chapter 08 §4.3 step 4).
constexpr const char* kCollectors[kCollectorCount] = {"assets", "worldtables", "content", "skills", "knowledge", "vfx",
                                                       "patches"};  // patches (S10): kiln.content's resource field patches, last
// The boot kinds (chapter 08 §4.1): the plural block sections a staged definition may use. Closed.
constexpr const char* kKinds[] = {"statuses",     "effects",          "items",            "spells",     "talents",
                                  "talentTrees",  "npcs",             "creatures",        "materialTweaks",
                                  "buildingMaterials", "terrainMaterials", "blueprints", "props",      "stations",
                                  "vfx",          "materials",        "quests",           "dialogues",  "bodies",
                                  "animations",   "ais",              "behaviours",       "perks",      "abilities",
                                  "looks",        "strengths",        "surfaces",         "resourcePatches"};
constexpr uint64_t kGeneration = 1;  // one generation per process
constexpr uint32_t kRoleBits = KILN_BOOT_ROLE_DEDICATED | KILN_BOOT_ROLE_HOSTED | KILN_BOOT_ROLE_CLIENT;
constexpr size_t kManifestMax = 1u << 20;

struct Owner {
    std::string id;
    KilnOwner author = 0;  // the transient author owner of begin_owner; 0 for a participant named by the envelope only
    uint32_t roles = 0;
    bool required = false;
    bool open = false;      // its author frame is open
    bool envelope = false;  // the envelope lists it
    uint32_t state = KILN_BOOT_STATE_STAGED;
    std::map<std::string, std::string> defs;  // "<kind>/<key>" -> the canonical JSON as staged
    std::vector<std::string> needs;            // the envelope's "requires"
    KilnOwner process = 0;                     // "boot:<id>", from the commit on
    uint64_t read_bytes = 0;                   // game-data values read_resource gave it (KILN_BOOT_AUTHOR_READ_MAX)
};

struct Issue {
    uint32_t code;
    std::string mod, kind, key, phase, reason;
    bool operator==(const Issue&) const = default;
};

struct Collector {
    bool registered = false;
    KilnOwner owner = 0;
    KilnBootPrepareFnV1 fn = nullptr;
    void* user = nullptr;
};

struct Root {
    uint64_t generation;
    const void* plans[kCollectorCount];
};

struct Row {  // a capability report, sent after the mutex is released
    std::string name;
    uint32_t state, reason;
    std::string text;
};

void os_exit(uint32_t code) { ExitProcess(code); }
// The stop after the entry point (b_activation_fault): the game's and the CLR's threads are live, so the process is
// ended without running any DLL's detach code under the loader lock. What the launcher reads is on disk before this.
void os_exit_late(uint32_t code) { TerminateProcess(GetCurrentProcess(), code); }

std::mutex& g_mutex = *new std::mutex;  // never destroyed: a late reader at process exit must still find it
// The offline game-data view: the boot snapshot's reader (boot_gamedata.h). Opened once in decide(); it has a lock of
// its own, and g_mutex is never held across its file IO.
boot_gd::Reader& g_gamedata = *new boot_gd::Reader;
bool g_decided = false;
bool g_deferred = false;
bool g_bound = false;                  // registration is over (bound() or commit())
std::string g_why;                     // decide()'s sentence: why deferred mode is on or off
std::vector<std::string> g_boot_mods;  // what the manifest scan found ("id" or "id (required)")
uint32_t g_status = KILN_BOOT_STATUS_COLLECTING;
std::map<std::string, Owner> g_owners;  // by mod id
std::map<KilnOwner, std::string> g_by_author;
KilnOwner g_host = 0;  // the owner that staged the envelope (and must commit)
json g_envelope;       // null until staged
size_t g_envelope_bytes = 0;
std::vector<Issue> g_issues;
Collector g_collectors[kCollectorCount];
std::atomic<const Root*> g_root{nullptr};
KilnOwner g_self = 0;     // kiln.boot's own owner, for its capability reports
uint32_t g_exit_code = 0;  // the code the hold path ended the process with (boot.json)
void (*g_exit)(uint32_t) = &os_exit;  // the OS-exit adapter (the host test replaces it)
void (*g_exit_late)(uint32_t) = &os_exit_late;  // the same for the stop after the entry point
void (*g_late_hold)(void* user, const std::string& last_line) = nullptr;  // boot.cpp: the state files of a stop after the entry point
void* g_late_hold_user = nullptr;

// ---- small helpers ----
template <size_t N>
void put(char (&dst)[N], std::string_view s) {
    size_t n = std::min(s.size(), N - 1);
    if (n < s.size())
        while (n > 0 && (static_cast<unsigned char>(s[n]) & 0xC0) == 0x80) --n;  // not inside a UTF-8 sequence
    std::memcpy(dst, s.data(), n);
    dst[n] = 0;
}

template <size_t N>
std::string got(const char (&src)[N]) {
    return std::string(src, strnlen(src, N));
}

bool lower_digit(char c) { return (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'); }
bool hex(char c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'); }

// Manifest.IdPattern: ^[a-z0-9][a-z0-9._-]{0,63}$
bool id_ok(std::string_view s) {
    if (s.empty() || s.size() > 64 || !lower_digit(s[0])) return false;
    for (const char c : s)
        if (!lower_digit(c) && c != '.' && c != '_' && c != '-') return false;
    return true;
}

// ^[a-z][a-z0-9_.-]{0,95}$, no parent segment
bool key_ok(std::string_view s) {
    if (s.empty() || s.size() > 96 || !(s[0] >= 'a' && s[0] <= 'z')) return false;
    for (const char c : s)
        if (!lower_digit(c) && c != '.' && c != '_' && c != '-') return false;
    return s.find("..") == std::string_view::npos;
}

bool kind_ok(std::string_view s) {
    for (const char* k : kKinds)
        if (s == k) return true;
    return false;
}

int collector_index(std::string_view s) {
    for (size_t i = 0; i < kCollectorCount; ++i)
        if (s == kCollectors[i]) return static_cast<int>(i);
    return -1;
}

// A game debug name or material name: letters, digits, '_', '.', '-'; no path, URL or parent segment.
bool name_ok(std::string_view s, bool dots) {
    if (s.empty() || s.size() > 128) return false;
    for (const char c : s) {
        const bool alnum = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
        if (!alnum && c != '_' && !(dots && (c == '.' || c == '-'))) return false;
    }
    return s.find("..") == std::string_view::npos;
}

bool hex_run(std::string_view s, size_t n, bool lower_only) {
    if (s.size() != n) return false;
    for (const char c : s)
        if (!hex(c) || (lower_only && c >= 'A' && c <= 'F')) return false;
    return true;
}

// The five query forms of IReadOnlyGameData.Read (chapter 08 §4.1).
bool query_ok(std::string_view q) {
    const auto colon = q.find(':');
    if (colon == std::string_view::npos) return false;
    const std::string_view form = q.substr(0, colon), arg = q.substr(colon + 1);
    if (form == "item" || form == "template") return name_ok(arg, true);
    if (form == "material") return name_ok(arg, false);
    if (form == "skill") return arg.size() == 10 && arg[0] == '0' && arg[1] == 'x' && hex_run(arg.substr(2), 8, false);
    if (form != "resource") return false;
    // <canonical GUID>/<8 hex typeHash>/<decimal part>
    const auto a = arg.find('/'), b = a == std::string_view::npos ? a : arg.find('/', a + 1);
    if (a == std::string_view::npos || b == std::string_view::npos) return false;
    const std::string_view guid = arg.substr(0, a), type = arg.substr(a + 1, b - a - 1), part = arg.substr(b + 1);
    if (guid.size() != 36) return false;
    for (size_t i = 0; i < guid.size(); ++i) {
        const bool dash = i == 8 || i == 13 || i == 18 || i == 23;
        if (dash ? guid[i] != '-' : !hex_run(guid.substr(i, 1), 1, true)) return false;
    }
    if (!hex_run(type, 8, false) || part.empty() || part.size() > 10 || (part.size() > 1 && part[0] == '0')) return false;
    uint64_t v = 0;
    for (const char c : part) {
        if (c < '0' || c > '9') return false;
        v = v * 10 + static_cast<uint64_t>(c - '0');
    }
    return v <= 0x7fffffffu;
}

const char* status_name(uint32_t s) {
    static const char* const n[] = {"collecting", "preparing", "published", "aborted", "activation-fault"};
    return s < 5 ? n[s] : "unknown";
}

const char* state_name(uint32_t s) {
    static const char* const n[] = {"not-participating", "staged",           "committed",
                                    "excluded-callback", "excluded-dependency", "rejected-prepare",
                                    "generation-aborted", "missed-phase",      "activation-fault"};
    return s < 9 ? n[s] : "unknown";
}

std::string code_name(uint32_t code) {
    switch (code) {
        case KILN_BOOT_ISSUE_DUPLICATE_DEFINITION: return "DuplicateDefinition";
        case KILN_BOOT_ISSUE_CALLBACK_FAILED: return "CallbackFailed";
        case KILN_BOOT_ISSUE_DEPENDENCY: return "Dependency";
        case KILN_BOOT_ISSUE_PREPARE_REFUSED: return "PrepareRefused";
        case KILN_BOOT_ISSUE_COLLECTOR_FAULT: return "CollectorFault";
        case KILN_BOOT_ISSUE_COLLECTOR_MISSING: return "CollectorMissing";
        case KILN_BOOT_ISSUE_GENERATION_ABORTED: return "GenerationAborted";
        case KILN_BOOT_ISSUE_NOT_COMMITTED: return "NotCommitted";
        case KILN_BOOT_ISSUE_WRONG_ROLE: return "WrongRole";
        case KILN_BOOT_ISSUE_ACTIVATION_FAULT: return "ActivationFault";
        case KILN_BOOT_ISSUE_SHADOWED_BY_FILE: return "ShadowedByFile";
        default: {
            char b[32];
            std::snprintf(b, sizeof b, "0x%08x", code);
            return b;
        }
    }
}

bool excluded(const Owner& o) {
    return o.state == KILN_BOOT_STATE_EXCLUDED_CALLBACK || o.state == KILN_BOOT_STATE_EXCLUDED_DEPENDENCY ||
           o.state == KILN_BOOT_STATE_REJECTED_PREPARE;
}

void issue_locked(Issue i) {
    if (std::find(g_issues.begin(), g_issues.end(), i) != g_issues.end()) return;  // the same, again (a second round)
    const bool warning = (i.code & KILN_BOOT_ISSUE_WARNING) != 0;
    log::writef(warning ? KILN_LOG_WARN : KILN_LOG_ERROR, "kiln", "boot: %s %s%s%s%s%s (%s): %s", code_name(i.code).c_str(),
                i.mod.empty() ? "-" : i.mod.c_str(), i.kind.empty() ? "" : "/", i.kind.c_str(), i.key.empty() ? "" : "/",
                i.key.c_str(), i.phase.c_str(), i.reason.c_str());
    g_issues.push_back(std::move(i));
}

uint32_t issues_of_locked(const std::string& id) {
    return static_cast<uint32_t>(std::count_if(g_issues.begin(), g_issues.end(), [&](const Issue& i) { return i.mod == id; }));
}

void send(const std::vector<Row>& rows) {
    const KilnCapsApi* caps = caps::table();
    if (!caps || !g_self) return;
    for (const Row& r : rows) caps->report(g_self, kstr(r.name), r.state, r.reason, kstr(r.text));
}

Row phase_row_locked() {
    for (const auto& [id, o] : g_owners)
        if (o.open) return {"mods.boot.phase", KILN_CAP_AVAILABLE, KILN_CAP_REASON_NONE, "boot-author frame open: " + id};
    if (g_status != KILN_BOOT_STATUS_COLLECTING)
        return {"mods.boot.phase", KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_POLICY,
                std::string("the boot phase of this process is over (generation 1 ") + status_name(g_status) + ")"};
    return {"mods.boot.phase", KILN_CAP_PENDING, KILN_CAP_REASON_SESSION, "no boot-author frame is open (generation 1 collecting)"};
}

// ---- deferred mode: the manifest scan ----
struct BootMod {
    std::string id;
    bool required;
};

// kiln.mod.json's "boot" of one folder: 0 none, 1 true, 2 "required". `id` gets the manifest's id.
int manifest_boot(const std::filesystem::path& dir, uint32_t side, std::string* id) {
    std::error_code ec;
    const auto file = dir / L"kiln.mod.json";
    const auto size = std::filesystem::file_size(file, ec);
    if (ec || size > kManifestMax) return 0;
    std::string text;
    if (!read_file(file, &text)) return 0;
    const json doc = json::parse(jsonc_to_json(text), nullptr, false);
    if (!doc.is_object() || !doc.contains("boot")) return 0;
    if (doc.contains("id") && doc["id"].is_string()) *id = doc["id"].get<std::string>();
    // A server process runs no client-only mod; a client process authors for every role it may take (§4.2).
    const std::string mod_side = doc.contains("side") && doc["side"].is_string() ? doc["side"].get<std::string>() : "server";
    if (side == KILN_SIDE_SERVER && mod_side == "client") return 0;
    const json& b = doc["boot"];
    if (b.is_boolean() && b.get<bool>()) return 1;
    if (b.is_string() && b.get<std::string>() == "required") return 2;
    return 0;
}

// The enabled Kiln mods of this profile whose manifest asks for the boot phase, as Kiln.Host will discover them: the
// profile's kiln entries that are enabled, then the drop-ins (kiln/mods/<folder>/kiln.mod.json without .disabled) when
// the profile loads them and does not list their id. A mod the resolver later refuses leaves a generation of files
// only, which is still correct (wp/08-onboot.md §7 gap 4).
std::vector<BootMod> scan(uint32_t side) {
    std::vector<BootMod> out;
    const auto& st = profile::state();
    if (st.mode == KILN_PROFILE_NO_MODS || st.mode == KILN_PROFILE_INVALID) return out;
    std::set<std::string> listed;
    for (const auto& e : st.mods[KILN_PROFILE_KIND_KILN]) {
        listed.insert(e.id);
        if (!e.enabled) continue;
        std::string id;
        const int b = manifest_boot(std::filesystem::path(to_wide(e.path)), side, &id);
        if (b && id == e.id) out.push_back({e.id, b == 2});
    }
    if (!profile::dropins(KILN_PROFILE_KIND_KILN)) return out;
    std::error_code ec;
    std::vector<std::filesystem::path> dirs;
    for (auto it = std::filesystem::directory_iterator(paths().mods, ec); !ec && it != std::filesystem::directory_iterator();
         it.increment(ec)) {
        std::error_code e2;
        if (it->is_directory(e2) && !std::filesystem::exists(it->path() / L".disabled", e2)) dirs.push_back(it->path());
    }
    std::sort(dirs.begin(), dirs.end());
    for (const auto& d : dirs) {
        std::string id;
        const int b = manifest_boot(d, side, &id);
        if (b && id_ok(id) && !listed.count(id)) out.push_back({id, b == 2});
    }
    return out;
}

bool row_available(const char* name) {
    const KilnCapsApi* caps = caps::table();
    KilnCapRow row{};
    row.size = sizeof row;
    return caps && caps->find(kstr(name), &row) == KILN_OK && row.state == KILN_CAP_AVAILABLE;
}

std::string row_text(const char* name) {
    const KilnCapsApi* caps = caps::table();
    KilnCapRow row{};
    row.size = sizeof row;
    if (!caps || caps->find(kstr(name), &row) != KILN_OK) return "no row";
    return std::string(sv(row.text));
}

// ---- collector calls (no C++ object with a destructor next to __try: guard.cpp's pattern) ----
bool prepare_guarded(KilnBootPrepareFnV1 fn, void* user, const KilnBootPrepareInputV1* in, KilnBootPreparedV1* out,
                     KilnBootIssueV1* issue, KilnResult* rc, unsigned long* code) {
    crash::Capture cap{};
    static_cast<void>(code);  // host tests: their __except stand-in does not use it
    __try {
        *rc = fn(user, in, out, issue);
        return true;
    } __except (crash::filter(GetExceptionInformation(), &cap, code)) {
        crash::report(cap, "kiln.boot collector prepare", reinterpret_cast<const void*>(fn));
        return false;
    }
}

bool destroy_guarded(void(KILN_CALL* fn)(const void*), const void* plan, unsigned long* code) {
    crash::Capture cap{};
    static_cast<void>(code);
    __try {
        fn(plan);
        return true;
    } __except (crash::filter(GetExceptionInformation(), &cap, code)) {
        crash::report(cap, "kiln.boot collector plan destroy", reinterpret_cast<const void*>(fn));
        return false;
    }
}

// ---- the table ----
KilnResult KILN_CALL b_status(KilnBootCommitResultV1* out) {
    std::lock_guard lk(g_mutex);
    if (!g_deferred) return KILN_ERR_UNAVAILABLE;
    if (!out || out->size < sizeof(KilnBootCommitResultV1)) return KILN_ERR_INVALID_ARGUMENT;
    out->status = g_status;
    out->owner_count = static_cast<uint32_t>(g_owners.size());
    out->issue_count = static_cast<uint32_t>(g_issues.size());
    out->generation = kGeneration;
    return KILN_OK;
}

KilnResult KILN_CALL b_begin_owner(KilnOwner author, KilnStr mod_id, uint32_t roles, uint64_t* generation) {
    Row phase;
    {
        std::lock_guard lk(g_mutex);
        if (!g_deferred || g_status != KILN_BOOT_STATUS_COLLECTING) return KILN_ERR_UNAVAILABLE;
        const std::string id(sv(mod_id));
        if (!author || !generation || !id_ok(id) || !(roles & kRoleBits) ||
            (roles & ~(kRoleBits | KILN_BOOT_ROLE_REQUIRED)) || author == g_host || g_by_author.count(author) ||
            g_owners.count(id))
            return KILN_ERR_INVALID_ARGUMENT;
        Owner& o = g_owners[id];
        o.id = id;
        o.author = author;
        o.roles = roles & kRoleBits;
        o.required = (roles & KILN_BOOT_ROLE_REQUIRED) != 0;
        o.open = true;
        o.state = KILN_BOOT_STATE_STAGED;
        g_by_author[author] = id;
        *generation = kGeneration;
        KLOG_I("boot: OnBoot of %s (roles %u%s)", id.c_str(), o.roles, o.required ? ", required" : "");
        phase = phase_row_locked();
    }
    send({phase});
    return KILN_OK;
}

// The envelope as Kiln.Host stages it: checked whole before it is kept (header, "The IR envelope").
bool envelope_ok(const json& e, std::string* why) {
    if (!e.is_object() || e.value("schema", 0) != 1) return *why = "not an object with \"schema\": 1", false;
    std::set<std::string> ids;
    if (e.contains("owners")) {
        if (!e["owners"].is_array()) return *why = "\"owners\" is not an array", false;
        for (const auto& o : e["owners"]) {
            if (!o.is_object() || !o.contains("id") || !o["id"].is_string() || !id_ok(o["id"].get<std::string>()))
                return *why = "an owner without a valid \"id\"", false;
            if (!ids.insert(o["id"].get<std::string>()).second) return *why = "an owner listed twice", false;
            if (o.contains("requires")) {
                if (!o["requires"].is_array()) return *why = "\"requires\" is not an array", false;
                for (const auto& r : o["requires"])
                    if (!r.is_string()) return *why = "\"requires\" holds a non-string", false;
            }
        }
    }
    if (e.contains("reservations") && !e["reservations"].is_object()) return *why = "\"reservations\" is not an object", false;
    if (e.contains("collectors")) {
        if (!e["collectors"].is_object()) return *why = "\"collectors\" is not an object", false;
        for (const auto& [c, list] : e["collectors"].items()) {
            if (collector_index(c) < 0) return *why = "an unknown collector \"" + c + "\"", false;
            if (!list.is_array()) return *why = "collector \"" + c + "\" is not an array", false;
            for (const auto& d : list)
                if (!d.is_object() || !d.contains("owner") || !d["owner"].is_string() ||
                    !ids.count(d["owner"].get<std::string>()) || !d.contains("key") || !d["key"].is_string())
                    return *why = "an entry of \"" + c + "\" without \"key\" or with an owner \"owners\" does not list",
                           false;
        }
    }
    return true;
}

KilnResult KILN_CALL b_stage(KilnOwner owner, uint64_t generation, KilnStr kind, KilnStr key, KilnStr canonical_json,
                             KilnBootStageResultV1* out) {
    if (!out || out->size < sizeof(KilnBootStageResultV1)) return KILN_ERR_INVALID_ARGUMENT;
    const std::string k(sv(kind)), name(sv(key));
    auto refuse = [&](std::string_view why) {
        out->ok = 0;
        put(out->reason, why);
        return KILN_OK;
    };
    std::lock_guard lk(g_mutex);
    if (!g_deferred) return KILN_ERR_UNAVAILABLE;
    if (!owner || generation != kGeneration) return KILN_ERR_INVALID_ARGUMENT;
    const auto author = g_by_author.find(owner);
    if (k == "ir") {  // the host's envelope
        if (author != g_by_author.end() || name != "envelope") return KILN_ERR_INVALID_ARGUMENT;
        if (g_status != KILN_BOOT_STATUS_COLLECTING) return refuse("PhaseClosed: the boot generation is committed");
        if (canonical_json.len > KILN_BOOT_ENVELOPE_MAX || !canonical_json.ptr) return KILN_ERR_INVALID_ARGUMENT;
        if (g_host && g_host != owner) return KILN_ERR_INVALID_ARGUMENT;
        if (!g_envelope.is_null()) return refuse("DuplicateDefinition: the IR envelope is staged already");
        json e = json::parse(sv(canonical_json), nullptr, false);
        std::string why;
        if (e.is_discarded() || !envelope_ok(e, &why)) {
            KLOG_E("boot: the IR envelope is refused: %s", e.is_discarded() ? "not JSON" : why.c_str());
            return KILN_ERR_INVALID_ARGUMENT;
        }
        g_host = owner;
        g_envelope = std::move(e);
        g_envelope_bytes = canonical_json.len;
        out->ok = 1;
        out->reason[0] = 0;
        return KILN_OK;
    }
    if (!kind_ok(k) || !key_ok(name) || !canonical_json.ptr || !canonical_json.len ||
        canonical_json.len > KILN_BOOT_DEFINITION_MAX)
        return KILN_ERR_INVALID_ARGUMENT;
    if (author == g_by_author.end()) return KILN_ERR_INVALID_ARGUMENT;  // not an author of this generation
    Owner& o = g_owners[author->second];
    if (!o.open || g_status != KILN_BOOT_STATUS_COLLECTING)
        return refuse("PhaseClosed: " + o.id + "'s OnBoot frame is closed; definitions are staged inside OnBoot only");
    const json doc = json::parse(sv(canonical_json), nullptr, false);
    if (!doc.is_object()) return KILN_ERR_INVALID_ARGUMENT;
    const std::string slot = k + "/" + name;
    const std::string text(sv(canonical_json));
    const auto it = o.defs.find(slot);
    if (it != o.defs.end()) {
        if (it->second == text) {
            out->ok = 1;
            out->reason[0] = 0;
            return KILN_OK;
        }
        issue_locked({KILN_BOOT_ISSUE_DUPLICATE_DEFINITION, o.id, k, name, "stage",
                      "staged twice with different content; the first stays"});
        return refuse("DuplicateDefinition: " + o.id + ":" + name + " (" + k + ") is staged already with other content");
    }
    if (o.defs.size() >= KILN_BOOT_OWNER_DEFINITIONS)
        return refuse("TooMany: one mod stages at most " + std::to_string(KILN_BOOT_OWNER_DEFINITIONS) + " definitions");
    o.defs.emplace(slot, text);
    out->ok = 1;
    out->reason[0] = 0;
    return KILN_OK;
}

KilnResult KILN_CALL b_finish_owner(KilnOwner owner, uint64_t generation, uint32_t accept) {
    Row phase;
    {
        std::lock_guard lk(g_mutex);
        if (!g_deferred) return KILN_ERR_UNAVAILABLE;
        const auto author = g_by_author.find(owner);
        if (author == g_by_author.end() || generation != kGeneration || accept > KILN_BOOT_FINISH_DEPENDENCY)
            return KILN_ERR_INVALID_ARGUMENT;
        Owner& o = g_owners[author->second];
        if (!o.open) return KILN_ERR_INVALID_ARGUMENT;
        o.open = false;
        if (accept == KILN_BOOT_FINISH_ACCEPT) {
            KLOG_I("boot: %s staged %zu definition(s)", o.id.c_str(), o.defs.size());
        } else {
            const bool dep = accept == KILN_BOOT_FINISH_DEPENDENCY;
            o.state = dep ? KILN_BOOT_STATE_EXCLUDED_DEPENDENCY : KILN_BOOT_STATE_EXCLUDED_CALLBACK;
            issue_locked({static_cast<uint32_t>(dep ? KILN_BOOT_ISSUE_DEPENDENCY : KILN_BOOT_ISSUE_CALLBACK_FAILED), o.id, "", "", "callback",
                          dep ? "a mod it requires is out of the boot generation; none of its definitions or files are in"
                              : "its OnBoot failed (kiln.log has the exception); none of its definitions or files are in"});
            o.defs.clear();
        }
        phase = phase_row_locked();
    }
    send({phase});
    return KILN_OK;
}

// The offline game-data view over the boot snapshot (boot_gamedata.h). The frame and the budget are checked under
// g_mutex, the files are read without it (boot-thread calls are serialized, kiln_boot.h), and the bytes are charged to
// the author when the result is written: a BUFFER_TOO_SMALL answer costs nothing.
KilnResult KILN_CALL b_read_resource(KilnOwner owner, KilnStr query, char* out, size_t capacity, size_t* required) {
    uint64_t left = 0;
    std::string id;
    {
        std::lock_guard lk(g_mutex);
        if (!g_deferred) return KILN_ERR_UNAVAILABLE;
        if (!required || !query_ok(sv(query)) || (!out && capacity)) return KILN_ERR_INVALID_ARGUMENT;
        const auto author = g_by_author.find(owner);
        if (author == g_by_author.end()) return KILN_ERR_INVALID_ARGUMENT;
        const Owner& o = g_owners[author->second];
        if (!o.open || g_status != KILN_BOOT_STATUS_COLLECTING) return KILN_ERR_REVOKED;
        id = o.id;
        left = KILN_BOOT_AUTHOR_READ_MAX - std::min<uint64_t>(o.read_bytes, KILN_BOOT_AUTHOR_READ_MAX);
    }
    if (!g_gamedata.is_open()) return KILN_ERR_UNAVAILABLE;  // row mods.boot.game_data says why
    std::string json, why;
    size_t need = 0, value = 0;
    const KilnResult rc = g_gamedata.read(sv(query), capacity, KILN_BOOT_RESULT_MAX, left, &json, &need, &value, &why);
    if (rc == KILN_ERR_BUFFER_TOO_SMALL) {
        *required = need;
        return rc;
    }
    if (rc == KILN_ERR_UNAVAILABLE)
        KLOG_W("boot: %s: game data %.*s: %s", id.c_str(), static_cast<int>(std::min<size_t>(query.len, 200)), query.ptr, why.c_str());
    if (rc != KILN_OK) return rc;
    {
        std::lock_guard lk(g_mutex);
        const auto author = g_by_author.find(owner);
        if (author == g_by_author.end()) return KILN_ERR_REVOKED;
        g_owners[author->second].read_bytes += value;
    }
    std::memcpy(out, json.c_str(), json.size() + 1);
    return KILN_OK;
}

// ---- commit ----
// Owners that require an excluded one are excluded too (ExcludedDependency), until nothing changes.
void propagate_locked(const std::string& phase) {
    for (bool changed = true; changed;) {
        changed = false;
        for (auto& [id, o] : g_owners) {
            if (excluded(o)) continue;
            for (const auto& r : o.needs) {
                const auto it = g_owners.find(r);
                if (it == g_owners.end() || !excluded(it->second)) continue;
                o.state = KILN_BOOT_STATE_EXCLUDED_DEPENDENCY;
                issue_locked({KILN_BOOT_ISSUE_DEPENDENCY, id, "", "", phase,
                              "requires " + r + ", which is out (" + state_name(it->second.state) + ")"});
                changed = true;
                break;
            }
        }
    }
}

// The envelope this round's prepares see: excluded owners' entries left out, the core's fields written.
std::string build_ir_locked() {
    json ir = g_envelope.is_null() ? json{{"schema", 1}} : g_envelope;
    std::set<std::string> out;
    for (const auto& [id, o] : g_owners)
        if (excluded(o)) out.insert(id);
    ir["generation"] = kGeneration;
    ir["excluded"] = json(std::vector<std::string>(out.begin(), out.end()));
    json owners = json::array();
    if (ir.contains("owners"))
        for (auto& o : ir["owners"]) {
            const std::string id = o["id"].get<std::string>();
            if (out.count(id)) continue;
            o["owner"] = g_owners[id].process;
            owners.push_back(o);
        }
    ir["owners"] = owners;
    if (!ir.contains("reservations")) ir["reservations"] = json::object();
    for (auto& [name, list] : ir["reservations"].items())
        if (list.is_array()) {
            json kept = json::array();
            for (auto& r : list)
                if (!(r.is_object() && r.contains("owner") && r["owner"].is_string() && out.count(r["owner"].get<std::string>())))
                    kept.push_back(r);
            list = kept;
        }
    if (!ir.contains("collectors")) ir["collectors"] = json::object();
    for (auto& [name, list] : ir["collectors"].items()) {
        json kept = json::array();
        for (auto& d : list)
            if (!out.count(d["owner"].get<std::string>())) kept.push_back(d);
        list = kept;
    }
    return ir.dump();
}

struct Prepared {
    size_t index;
    const void* plan;
    void(KILN_CALL* destroy)(const void*);
};

void destroy_reverse(std::vector<Prepared>& plans) {
    for (auto it = plans.rbegin(); it != plans.rend(); ++it) {
        if (!it->destroy) continue;
        unsigned long code = 0;
        if (!destroy_guarded(it->destroy, it->plan, &code))
            KLOG_E("boot: collector %s faulted (0x%08lx) destroying its unpublished plan", kCollectors[it->index], code);
    }
    plans.clear();
}

// One collector's prepare. Returns "" when it prepared (the plan is in `plans`); else the refusal: `culprit` names the
// participant it is about, empty when it is about none (or the collector broke the contract: `fatal`).
void prepare_one(size_t i, const Collector& c, const std::string& ir, std::vector<Prepared>& plans,
                 std::set<std::string>& culprits, bool& fatal) {
    KilnBootPrepareInputV1 in{};
    in.size = sizeof in;
    in.version = KILN_BOOT_VERSION;
    in.generation = kGeneration;
    in.canonical_ir_json = kstr(ir);
    KilnBootPreparedV1 prep{};
    prep.size = sizeof prep;
    KilnBootIssueV1 is{};
    is.size = sizeof is;
    KilnResult rc = KILN_ERR_INTERNAL;
    unsigned long code = 0;
    const double t0 = now_ms();
    const bool ran = prepare_guarded(c.fn, c.user, &in, &prep, &is, &rc, &code);
    const double ms = now_ms() - t0;
    is.mod_id[sizeof is.mod_id - 1] = is.kind[sizeof is.kind - 1] = is.key[sizeof is.key - 1] = 0;
    is.phase[sizeof is.phase - 1] = is.reason[sizeof is.reason - 1] = 0;
    const std::string coll = kCollectors[i];
    Issue issue{is.code, got(is.mod_id), got(is.kind), got(is.key), got(is.phase), got(is.reason)};
    if (issue.phase.empty()) issue.phase = "prepare";
    std::lock_guard lk(g_mutex);
    auto broken = [&](const std::string& why) {
        fatal = true;
        issue_locked({KILN_BOOT_ISSUE_COLLECTOR_FAULT, "", "", "", "prepare", "collector " + coll + ": " + why});
    };
    if (!ran) {
        char b[96];
        std::snprintf(b, sizeof b, "faulted in its prepare (0x%08lx; kiln/crash-reports)", code);
        return broken(b);
    }
    if (rc != KILN_OK) return broken("its prepare answered " + std::to_string(rc) + " (not KILN_OK)");
    if (prep.ok == 1) {
        if (!prep.plan) return broken("its prepare said ok without a plan");
        plans.push_back({i, prep.plan, prep.destroy});
        if (is.code && !(is.code & KILN_BOOT_ISSUE_WARNING))
            return broken("its prepare said ok with an error issue (" + code_name(is.code) + ": " + issue.reason + ")");
        if (is.code) issue_locked(std::move(issue));
        KLOG_I("boot: collector %s prepared in %.0f ms", coll.c_str(), ms);
        return;
    }
    if (prep.plan && prep.destroy) plans.push_back({i, prep.plan, prep.destroy});  // a plan with a refusal: freed below
    if (prep.ok != 0) return broken("its prepare answered ok = " + std::to_string(prep.ok));
    if (!is.code || (is.code & KILN_BOOT_ISSUE_WARNING)) issue.code = KILN_BOOT_ISSUE_PREPARE_REFUSED;
    if (issue.reason.empty()) issue.reason = "refused without a reason";
    issue.reason = "collector " + coll + ": " + issue.reason;
    const auto it = g_owners.find(issue.mod);
    if (issue.mod.empty() || it == g_owners.end() || excluded(it->second)) {
        if (!issue.mod.empty() && it == g_owners.end()) issue.reason += " (names " + issue.mod + ", no participant)";
        issue.mod.clear();
        fatal = true;
    } else {
        culprits.insert(issue.mod);
    }
    issue_locked(std::move(issue));
}

// Abort: nothing is published; the culprits are rejected, every other participant still in is GenerationAborted.
void abort_locked(const std::set<std::string>& culprits, const std::string& why) {
    for (auto& [id, o] : g_owners) {
        if (culprits.count(id)) {
            if (!excluded(o)) o.state = KILN_BOOT_STATE_REJECTED_PREPARE;
        } else if (!excluded(o)) {
            o.state = KILN_BOOT_STATE_GENERATION_ABORTED;
            issue_locked({KILN_BOOT_ISSUE_GENERATION_ABORTED, id, "", "", "commit", "not published: " + why});
        }
    }
    g_status = KILN_BOOT_STATUS_ABORTED;
    KLOG_E("boot: generation 1 aborted, nothing is published: %s", why.c_str());
}

KilnResult KILN_CALL b_commit(KilnOwner host, uint64_t generation, KilnBootCommitResultV1* out) {
    std::vector<Row> rows;
    {
        std::lock_guard lk(g_mutex);
        if (!g_deferred || g_status != KILN_BOOT_STATUS_COLLECTING) return KILN_ERR_UNAVAILABLE;
        if (!host || g_by_author.count(host) || (g_host && g_host != host) || generation != kGeneration || !out ||
            out->size < sizeof(KilnBootCommitResultV1))
            return KILN_ERR_INVALID_ARGUMENT;
        g_host = host;
        g_status = KILN_BOOT_STATUS_PREPARING;
        g_bound = true;
        for (auto& [id, o] : g_owners)
            if (o.open) {
                o.open = false;
                o.state = KILN_BOOT_STATE_EXCLUDED_CALLBACK;
                o.defs.clear();
                issue_locked({KILN_BOOT_ISSUE_CALLBACK_FAILED, id, "", "", "callback",
                              "its OnBoot frame was still open when the generation was committed"});
            }
        if (!g_envelope.is_null() && g_envelope.contains("owners"))
            for (const auto& e : g_envelope["owners"]) {
                const std::string id = e["id"].get<std::string>();
                Owner& o = g_owners[id];
                if (o.id.empty()) o.id = id;  // a participant with files only
                o.envelope = true;
                if (e.contains("requires")) o.needs = e["requires"].get<std::vector<std::string>>();
            }
        propagate_locked("callback");
        for (auto& [id, o] : g_owners)
            if (!excluded(o)) o.process = reg::owner_create("boot:" + id);
        KLOG_I("boot: preparing generation 1: %zu participant(s), envelope %zu bytes", g_owners.size(), g_envelope_bytes);
        rows.push_back(phase_row_locked());
    }
    send(rows);
    rows.clear();

    Collector cs[kCollectorCount];
    {
        std::lock_guard lk(g_mutex);
        std::copy(std::begin(g_collectors), std::end(g_collectors), std::begin(cs));
    }
    std::set<std::string> culprits;
    bool fatal = false;
    std::string why;
    std::vector<Prepared> plans;
    for (;;) {
        std::string ir;
        {
            std::lock_guard lk(g_mutex);
            // Ruling 7: a required participant that is out stops the start.
            for (const auto& [id, o] : g_owners)
                if (excluded(o) && o.required) {
                    culprits.insert(id);
                    why = id + "'s boot content is required and it is out (" + state_name(o.state) + ")";
                }
            if (!why.empty()) break;
            // Definitions for a collector that did not register: their owners are refused before anything prepares.
            if (!g_envelope.is_null() && g_envelope.contains("collectors"))
                for (const auto& [c, list] : g_envelope["collectors"].items()) {
                    const int i = collector_index(c);
                    if (cs[i].registered) continue;
                    for (const auto& d : list) {
                        const std::string id = d["owner"].get<std::string>();
                        if (excluded(g_owners[id]) || culprits.count(id)) continue;
                        culprits.insert(id);
                        issue_locked({KILN_BOOT_ISSUE_COLLECTOR_MISSING, id, "", d["key"].get<std::string>(), "prepare",
                                      "collector " + c + " is not running in this process; its definitions cannot be prepared"});
                    }
                }
            if (culprits.empty()) ir = build_ir_locked();
        }
        if (culprits.empty())
            for (size_t i = 0; i < kCollectorCount && culprits.empty() && !fatal; ++i)
                if (cs[i].registered) prepare_one(i, cs[i], ir, plans, culprits, fatal);
        if (culprits.empty() && !fatal) break;  // every collector prepared
        destroy_reverse(plans);
        std::lock_guard lk(g_mutex);
        if (fatal) {
            why = "a collector refused something no participant can be blamed for, or broke its contract";
            break;
        }
        for (const auto& id : culprits)
            if (g_owners[id].required) why = id + "'s boot content is required and a collector refused it";
        if (!why.empty()) break;
        // Ruling 7: drop the culprits' whole contribution (and their dependents'), then every collector again.
        for (const auto& id : culprits) {
            Owner& o = g_owners[id];
            o.state = KILN_BOOT_STATE_REJECTED_PREPARE;
            KLOG_W("boot: %s is dropped from generation 1 (nothing of it is published); the collectors prepare again "
                   "without it",
                   id.c_str());
        }
        propagate_locked("prepare");
        for (auto& [id, o] : g_owners)
            if (excluded(o) && o.process) {
                reg::owner_revoke(o.process);
                o.process = 0;
            }
        culprits.clear();
    }
    {
        std::lock_guard lk(g_mutex);
        if (why.empty()) {
            auto* root = new Root{kGeneration, {}};
            for (const auto& p : plans) root->plans[p.index] = p.plan;
            size_t committed = 0, out_count = 0;
            for (auto& [id, o] : g_owners) {
                if (excluded(o)) {
                    ++out_count;
                    continue;
                }
                o.state = KILN_BOOT_STATE_COMMITTED;
                ++committed;
            }
            g_root.store(root, std::memory_order_release);  // the one publication: every plan at once
            g_status = KILN_BOOT_STATUS_PUBLISHED;
            KLOG_I("boot: generation 1 published: %zu mod(s) committed, %zu out, %zu plan(s)", committed, out_count,
                   plans.size());
        } else {
            abort_locked(culprits, why);
        }
        out->status = g_status;
        out->owner_count = static_cast<uint32_t>(g_owners.size());
        out->issue_count = static_cast<uint32_t>(g_issues.size());
        out->generation = kGeneration;
        rows.push_back(phase_row_locked());
    }
    send(rows);
    return KILN_OK;
}

KilnResult KILN_CALL b_owner_state(KilnStr mod_id, KilnBootOwnerStateV1* out) {
    if (!out || out->size < sizeof(KilnBootOwnerStateV1)) return KILN_ERR_INVALID_ARGUMENT;
    const std::string id(sv(mod_id));
    std::lock_guard lk(g_mutex);
    const auto it = g_owners.find(id);
    out->state = it == g_owners.end() ? KILN_BOOT_STATE_NOT_PARTICIPATING : it->second.state;
    out->definition_count = it == g_owners.end() ? 0 : static_cast<uint32_t>(it->second.defs.size());
    out->issue_count = issues_of_locked(id);
    out->generation = g_deferred ? kGeneration : 0;
    put(out->mod_id, id);
    return KILN_OK;
}

KilnResult KILN_CALL b_issues(uint64_t generation, KilnBootIssueV1* out, uint32_t capacity, uint32_t stride,
                              uint32_t* required) {
    if (!required || stride < sizeof(KilnBootIssueV1)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lk(g_mutex);
    const uint32_t n = g_deferred && generation == kGeneration ? static_cast<uint32_t>(g_issues.size()) : 0;
    *required = n;
    if (capacity < n) return KILN_ERR_BUFFER_TOO_SMALL;
    if (n && !out) return KILN_ERR_INVALID_ARGUMENT;
    auto* p = reinterpret_cast<uint8_t*>(out);
    for (uint32_t i = 0; i < n; ++i) {
        KilnBootIssueV1 r{};
        r.size = sizeof r;
        r.code = g_issues[i].code;
        put(r.mod_id, g_issues[i].mod);
        put(r.kind, g_issues[i].kind);
        put(r.key, g_issues[i].key);
        put(r.phase, g_issues[i].phase);
        put(r.reason, g_issues[i].reason);
        std::memcpy(p + static_cast<size_t>(i) * stride, &r, sizeof r);
    }
    return KILN_OK;
}

KilnResult KILN_CALL b_collector(KilnOwner owner, KilnStr collector_id, KilnBootPrepareFnV1 fn, void* user) {
    std::lock_guard lk(g_mutex);
    if (!g_deferred || g_bound) return KILN_ERR_UNAVAILABLE;
    const int i = collector_index(sv(collector_id));
    if (i < 0 || !fn || g_collectors[i].registered) return KILN_ERR_INVALID_ARGUMENT;
    g_collectors[i] = {true, owner, fn, user};
    KLOG_I("boot: collector %s registered (%s)", kCollectors[i], reg::owner_name(owner).c_str());
    return KILN_OK;
}

const void* KILN_CALL b_active_plan(KilnStr collector_id, uint64_t* generation) {
    const Root* root = g_root.load(std::memory_order_acquire);
    const int i = collector_index(sv(collector_id));
    if (generation) *generation = root && i >= 0 && root->plans[i] ? root->generation : 0;
    return root && i >= 0 ? root->plans[i] : nullptr;
}

// A consumer could not bind published content of `mod_id` (S10: a resource patch whose resource is not there, an index
// past its array). The issue is recorded and the mod reads ActivationFault. A mod whose boot content is required stops
// the start here as an unpublished generation does before the entry point: the state files are written (g_late_hold,
// boot.cpp) and the process ends with KILN_BOOT_EXIT_CODE. Any thread; nothing is undone.
KilnResult KILN_CALL b_activation_fault(KilnOwner collector, KilnStr mod_id, KilnStr kind, KilnStr key, KilnStr reason) {
    const std::string id(sv(mod_id));
    bool stop = false;
    std::string last;
    std::vector<Row> rows;
    {
        std::lock_guard lk(g_mutex);
        if (!g_deferred || g_status == KILN_BOOT_STATUS_COLLECTING || g_status == KILN_BOOT_STATUS_PREPARING) return KILN_ERR_UNAVAILABLE;
        bool known = false;
        for (const Collector& c : g_collectors) known |= c.registered && c.owner == collector;
        const auto it = g_owners.find(id);
        if (!collector || !known || it == g_owners.end() || !reason.ptr || !reason.len) return KILN_ERR_INVALID_ARGUMENT;
        Owner& o = it->second;
        if (o.state == KILN_BOOT_STATE_COMMITTED) o.state = KILN_BOOT_STATE_ACTIVATION_FAULT;
        if (g_status == KILN_BOOT_STATUS_PUBLISHED) g_status = KILN_BOOT_STATUS_ACTIVATION_FAULT;
        issue_locked({KILN_BOOT_ISSUE_ACTIVATION_FAULT, id, std::string(sv(kind)), std::string(sv(key)), "activation", std::string(sv(reason))});
        rows.push_back(phase_row_locked());
        stop = o.required && !g_exit_code;
        if (stop) {
            g_exit_code = KILN_BOOT_EXIT_CODE;
            char line[96];
            std::snprintf(line, sizeof line, "boot: the start stops (exit code 0x%04X)", KILN_BOOT_EXIT_CODE);
            last = std::string(line) + ": " + id + "'s boot content is required and could not be applied: " + std::string(sv(reason)) +
                   "; state/boot.json lists every boot mod's state and issue";
        }
    }
    send(rows);
    if (!stop) {
        KLOG_E("boot: %s: %.*s (the start goes on without it)", id.c_str(), static_cast<int>(reason.len), reason.ptr);
        return KILN_OK;
    }
    KLOG_E("%s", last.c_str());
    if (g_late_hold) g_late_hold(g_late_hold_user, last);
    g_exit_late(KILN_BOOT_EXIT_CODE);
    return KILN_OK;  // the host test's exit adapter returned
}

const KilnBootApiV1 kTable = {
    {sizeof(KilnBootApiV1), KILN_BOOT_VERSION},
    &b_status,
    &b_begin_owner,
    &b_stage,
    &b_finish_owner,
    &b_read_resource,
    &b_commit,
    &b_owner_state,
    &b_issues,
    &b_collector,
    &b_active_plan,
    &b_activation_fault,
};

}  // namespace

const KilnBootApiV1* table() { return &kTable; }

// The row mods.boot.game_data: available with a snapshot of exactly this game build (docs/kiln/modding/
// boot-gamedata.md), which is looked for here, once; otherwise unavailable with what to run and the test that failed.
// One read of at most 64 KiB of the game's .kfc, index.json and eight file checks: no index is loaded before a mod asks.
static Row game_data_row(bool hooks, const std::string& idle) {
    if (!hooks) return {"mods.boot.game_data", KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_POLICY, idle};
    std::filesystem::path kfc = paths().exe;
    kfc.replace_extension(L".kfc");
    std::string why;
    if (g_gamedata.open(paths().data / L"boot-gamedata", image().sha256, kfc, &why)) {
        KLOG_I("boot: game-data snapshot %s found: OnBoot can read the game's data", g_gamedata.key().c_str());
        return {"mods.boot.game_data", KILN_CAP_AVAILABLE, KILN_CAP_REASON_NONE,
                "the boot game-data snapshot " + g_gamedata.key() + " matches this game build"};
    }
    KLOG_I("boot: %s (%s)", boot_gd::kHint, why.c_str());
    return {"mods.boot.game_data", KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_SERVICE_MISSING, std::string(boot_gd::kHint) + " (" + why + ")"};
}

// boot.cpp, after caps::core_rows and before the service DLLs and plugins load: is this a boot profile? Also reports
// the rows mods.boot.phase, mods.boot.game_data, mods.boot.client and mods.boot.vfx (mods.boot itself is the sigdb
// feature row; bound() adds to it).
void decide(bool hooks, const std::string& safe_idle) {
    if (!g_self) g_self = reg::owner_create("kiln.boot");
    const bool client = image().side == KILN_SIDE_CLIENT;
    const std::string idle = "safe idle: " + safe_idle;
    // The rows that do not depend on the decision first: the decision reads mods.boot.client. On a client both rows say
    // what this build can do; the client gate (native/core/gates/dev28-08-onboot.json) holds them until the guided
    // checks 08-boot.first-start-definitions and 08-loader-order pass, and kiln.json "preview" lifts it.
    std::vector<Row> rows = {{"mods.boot.phase", KILN_CAP_PENDING, KILN_CAP_REASON_SESSION, "no boot-author frame is open"},
                             game_data_row(hooks, idle)};
    if (client) {
        rows.push_back({"mods.boot.client", KILN_CAP_AVAILABLE, KILN_CAP_REASON_NONE,
                        "boot mods on a game client, for the hosted world and the client"});
        rows.push_back({"mods.boot.vfx", KILN_CAP_AVAILABLE, KILN_CAP_REASON_NONE,
                        "the vfx collector cooks staged VFX and materials on the client"});
    } else {
        rows.push_back({"mods.boot.client", KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_WRONG_SIDE,
                        "boot mods on a game client (hosted and joined); a dedicated server's are mods.boot"});
        rows.push_back({"mods.boot.vfx", KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_WRONG_SIDE,
                        "a dedicated server renders no VFX: its vfx collector prepares an empty plan"});
    }
    send(rows);
    std::vector<BootMod> mods;
    std::string why;
    bool deferred = false;
    if (!hooks) {
        why = idle;
    } else if (!config().clr) {
        why = ".NET is off (kiln.json \"clr\"), so no mod runs OnBoot";
    } else {
        mods = scan(image().side);
        if (mods.empty()) why = "no enabled mod has \"boot\": true";
        else if (!row_available("mods.boot"))
            why = "mods.boot is unavailable (" + row_text("mods.boot") + "): boot mods are refused";
        else if (client && (kiln_caps_held(caps::table(), kstr("mods.boot.client")) || !row_available("mods.boot.client")))
            why = "boot mods on a game client wait for the client pass (mods.boot.client: " + row_text("mods.boot.client") +
                  ")";
        else deferred = true;
    }
    std::vector<std::string> names;
    std::string list;
    for (const auto& m : mods) {
        names.push_back(m.id + (m.required ? " (required)" : ""));
        list += (list.empty() ? "" : ", ") + names.back();
    }
    if (deferred) why = "boot mod(s) " + list;
    {
        std::lock_guard lk(g_mutex);
        g_decided = true;
        g_deferred = deferred;
        g_why = why;
        g_boot_mods = names;
        g_status = KILN_BOOT_STATUS_COLLECTING;
    }
    if (deferred) {
        KLOG_I("boot: deferred generation on for %s: the collectors prepare after OnBoot, before the game's entry point",
               list.c_str());
        return;
    }
    KLOG_I("boot: no deferred generation (%s); the collectors keep their on_ready paths%s%s", why.c_str(),
           list.empty() ? "" : "; boot mods found: ", list.c_str());
    send({{"mods.boot.phase", KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_POLICY, "no boot phase in this process: " + why}});
}

// boot.cpp, after every service DLL and plugin ran on_ready: registration closes. In deferred mode a collector that did
// not register makes mods.boot unavailable (its definitions could not be prepared); the host refuses boot mods then.
void bound() {
    std::vector<Row> rows;
    {
        std::lock_guard lk(g_mutex);
        g_bound = true;
        if (!g_deferred) return;
        std::string have, missing;
        for (size_t i = 0; i < kCollectorCount; ++i) {
            std::string& list = g_collectors[i].registered ? have : missing;
            list += (list.empty() ? "" : ", ") + std::string(kCollectors[i]);
        }
        KLOG_I("boot: collectors registered: %s%s%s", have.empty() ? "none" : have.c_str(),
               missing.empty() ? "" : "; missing: ", missing.c_str());
        if (!missing.empty())
            rows.push_back({"mods.boot", KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_SERVICE_MISSING,
                            "boot collector(s) not running: " + missing + "; boot mods are refused"});
    }
    send(rows);
}

// boot.cpp, after clr::boot: a deferred generation must be published before the game's entry point runs. Otherwise
// the entry point stays held: `write_state` (boot.cpp) writes boot.json, capabilities.json and progress.json and waits
// for `last_line` in kiln.log, then the process ends with KILN_BOOT_EXIT_CODE. Returns true when the start goes on;
// false only when g_exit returned (the host test's adapter).
bool after_clr(bool clr_started, void (*write_state)(void* user, const std::string& last_line), void* user) {
    std::string why;
    {
        std::lock_guard lk(g_mutex);
        if (!g_deferred || g_status == KILN_BOOT_STATUS_PUBLISHED) return true;
        if (g_status == KILN_BOOT_STATUS_COLLECTING || g_status == KILN_BOOT_STATUS_PREPARING) {
            const std::string what = clr_started ? "Kiln.Host did not commit the boot generation (kiln.log, [host] lines)"
                                                 : "the .NET host did not start, so no OnBoot ran (kiln.log, [clr] lines)";
            issue_locked({KILN_BOOT_ISSUE_NOT_COMMITTED, "", "", "", "commit", what});
            abort_locked({}, what);
        }
        for (auto it = g_issues.rbegin(); it != g_issues.rend() && why.empty(); ++it)
            if (!(it->code & KILN_BOOT_ISSUE_WARNING) && it->code != KILN_BOOT_ISSUE_GENERATION_ABORTED)
                why = (it->mod.empty() ? std::string() : it->mod + ": ") + it->reason;
        g_exit_code = KILN_BOOT_EXIT_CODE;
    }
    char line[96];
    std::snprintf(line, sizeof line, "boot: the start stops before the game's entry point (exit code 0x%04X)",
                  KILN_BOOT_EXIT_CODE);
    const std::string last = std::string(line) + ": " + (why.empty() ? "the boot generation was not published" : why) +
                             "; state/boot.json lists every boot mod's state and issue";
    KLOG_E("%s", last.c_str());
    if (write_state) write_state(user, last);
    g_exit(KILN_BOOT_EXIT_CODE);
    return false;
}

// boot.cpp: what writes the state files when a required mod's published content cannot be applied (b_activation_fault).
void late_hold(void (*write_state)(void* user, const std::string& last_line), void* user) {
    std::lock_guard lk(g_mutex);
    g_late_hold = write_state;
    g_late_hold_user = user;
}

// boot.json "boot".
json report() {
    std::lock_guard lk(g_mutex);
    json owners = json::array(), issues = json::array(), collectors = json::array();
    for (const auto& [id, o] : g_owners)
        owners.push_back({{"id", id},
                          {"state", state_name(o.state)},
                          {"required", o.required},
                          {"author", o.author != 0},
                          {"definitions", o.defs.size()},
                          {"issues", issues_of_locked(id)}});
    for (const auto& i : g_issues)
        issues.push_back({{"mod", i.mod.empty() ? json(nullptr) : json(i.mod)},
                          {"kind", i.kind.empty() ? json(nullptr) : json(i.kind)},
                          {"key", i.key.empty() ? json(nullptr) : json(i.key)},
                          {"phase", i.phase},
                          {"code", code_name(i.code)},
                          {"warning", (i.code & KILN_BOOT_ISSUE_WARNING) != 0},
                          {"reason", i.reason}});
    for (size_t i = 0; i < kCollectorCount; ++i)
        if (g_collectors[i].registered) collectors.push_back(kCollectors[i]);
    return {{"deferred", g_deferred},
            {"reason", g_why},
            {"bootMods", g_boot_mods},
            {"status", g_deferred ? json(status_name(g_status)) : json(nullptr)},
            {"generation", g_deferred ? json(kGeneration) : json(nullptr)},
            {"published", g_root.load(std::memory_order_acquire) != nullptr},
            {"exitCode", g_exit_code ? json(g_exit_code) : json(nullptr)},
            {"collectors", collectors},
            {"owners", owners},
            {"issues", issues}};
}

}  // namespace kiln::boot_svc

KILN_SERVICE(boot, KILN_BOOT_SERVICE, KILN_BOOT_VERSION, &kiln::boot_svc::kTable, nullptr);
