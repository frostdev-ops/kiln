// SPDX-License-Identifier: MIT
// kiln.content registry stage in kiln-content.dll: the tables kiln.registry v1 (native/core/abi/services/kiln_registry.h) and
// kiln.internal.registry v1 (include/kiln_registry_host.h, Kiln.Host only), and the runner's environment (stage.h): the base
// is kiln.assets' live bytes at resources-ready through a base view of kiln.content's own (boot_archive_view.h LiveView,
// bound the first time the stage reads, inside the resources-ready callback, so it is up at once); the regmap from
// <game>/kiln/regmap; the mods' data sections from their content/*.json; registry.json under state/ (written whole, then
// renamed over the old one); the id table, the plans and the base cache under <data>/content/registry/.
// The stage itself runs from gamedata_bootpatch.cpp's resources-ready apply (one call site, the boot writes in the same copy
// as the old documents).
#include <windows.h>

#include <chrono>
#include <cstring>
#include <ctime>
#include <cwctype>
#include <filesystem>
#include <fstream>

#include "../../../core/src/services/boot_archive_view.h"
#include "../content.h"
#include "../refs.h"
#include "../registry_live.h"
#include "../include/kiln_registry_host.h"
#include "../../../core/src/owners_internal.h"
#include "kiln_boot_collector.hpp"
#include "pristine.h"
#include "archive_base.h"
#include "archive_journal.h"
#include "drift.h"
#include "entry_compat.h"
#include "visibility.h"
#include "services/kiln_assets.h"
#include "services/kiln_caps.h"
#include "services/kiln_eml.h"
#include "services/kiln_launch.h"
#include "services/kiln_registry.h"
#include "services/kiln_registry_live.h"
#include "boot_hooks.h"
#include "proof.h"
#include "stage.h"
#include "stage_owner.h"
#include "live_table.h"

namespace kc {
bool eml_live_generation(std::string* hex, std::string* why);  // gamedata_bootview_eml.cpp
}

namespace kc::registry::stage {

namespace {

const KilnOwnersInternalApi* owners() {
    static const auto* table = g_api ? static_cast<const KilnOwnersInternalApi*>(g_api->service(
        KilnStr{KILN_OWNERS_INTERNAL_SERVICE, sizeof(KILN_OWNERS_INTERNAL_SERVICE) - 1}, 2)) : nullptr;
    return table;
}

std::string api_path(int kind) {
    char b[1024];
    size_t n = 0;
    if (!g_api || g_api->path(kind, b, sizeof b, &n) != KILN_OK) return {};
    return std::string(b, n);
}

namespace fs = std::filesystem;

fs::path u8(const std::string& s) { return fs::path(std::u8string(s.begin(), s.end())); }

const KilnAssetsApi* assets() {
    const auto* a = g_api ? static_cast<const KilnAssetsApi*>(g_api->service(KilnStr{KILN_ASSETS_SERVICE, sizeof(KILN_ASSETS_SERVICE) - 1}, 1)) : nullptr;
    return a && a->header.size >= offsetof(KilnAssetsApi, when_resources_ready) + sizeof(void*) && a->resource_read && a->resource_list ? a : nullptr;
}
kiln::boot_view::LiveView& live_view() {
    static kiln::boot_view::LiveView& v = *new kiln::boot_view::LiveView([]() -> const KilnAssetsApi* { return assets(); });
    return v;
}
// The registry's base view: kiln.assets' live bytes with kiln.content's own store writes taken back out, so a base decoded
// after packs applied is still the game's (review4 NS1). The resident range comes from resource_data (address only).
PristineView& pristine_view() {
    static PristineView& v = *new PristineView(
        live_view(),
        [](const KilnAssetKey& key, uintptr_t* at, size_t* len) {
            const KilnAssetsApi* a = assets();
            if (!a || a->header.size < offsetof(KilnAssetsApi, resource_data) + sizeof(void*) || !a->resource_data) return false;
            const void* p = nullptr;
            size_t n = 0;
            if (a->resource_data(&key, &p, &n) != KILN_OK || !p) return false;
            *at = reinterpret_cast<uintptr_t>(p), *len = n;
            return true;
        },
        [](uintptr_t at, size_t len, uint8_t* bytes) { store_pristine(at, len, bytes); });
    return v;
}

bool write_whole(const fs::path& p, const std::string& text) {
    std::error_code ec;
    fs::create_directories(p.parent_path(), ec);
    fs::path tmp = p;
    tmp += u8".tmp";
    {
        std::ofstream f(tmp, std::ios::binary | std::ios::trunc);
        if (!f) return false;
        f.write(text.data(), static_cast<std::streamsize>(text.size()));
        if (!f) return false;
    }
    if (!MoveFileExW(tmp.wstring().c_str(), p.wstring().c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        fs::remove(tmp, ec);
        return false;
    }
    return true;
}
std::optional<std::string> read_whole(const fs::path& p) {
    std::ifstream f(p, std::ios::binary);
    if (!f) return std::nullopt;
    return std::string((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
}
fs::path data_root() { return u8(data_dir()) / u8"content" / u8"registry"; }

// Install-wide compatibility, deliberately outside the per-build cache tree and every save directory.
bool pin_definition(const EntryRef& ref, const json& body, std::string* why) {
    const auto directory = u8(data_dir()) / u8"registry";
    const auto file = directory / u8"entry-compat.json";
    std::error_code ec; fs::create_directories(directory, ec);
    if (ec) return *why = "entry compatibility directory is unavailable; this entry was refused", false;
    const auto lock_file = directory / u8"entry-compat.lock";
    const HANDLE handle = CreateFileW(lock_file.wstring().c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (handle == INVALID_HANDLE_VALUE) return *why = "entry compatibility record is locked or unavailable; this entry was refused", false;
    struct Close { HANDLE handle; ~Close() { CloseHandle(handle); } } close{handle};
    const bool exists = fs::exists(file, ec);
    if (ec || (exists && fs::file_size(file, ec) > 32u * 1024 * 1024) || ec)
        return *why = "entry compatibility record is unreadable or oversized; this entry was refused", false;
    const auto stored = exists ? read_whole(file) : std::optional<std::string>{};
    if (exists && !stored) return *why = "entry compatibility record is unreadable; this entry was refused", false;
    auto write = [&](const std::string& text) {
        auto temporary = file; temporary += u8".tmp";
        std::ofstream stream(temporary, std::ios::binary | std::ios::trunc);
        stream.write(text.data(), static_cast<std::streamsize>(text.size())); stream.flush(); stream.close();
        const bool ok = stream && MoveFileExW(temporary.wstring().c_str(), file.wstring().c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH);
        if (!ok) fs::remove(temporary, ec);
        return ok;
    };
    return registry::pin_entry(stored, ref.id.substr(0, ref.id.find(':')), ref.id, ref.registry,
                               body.value("guid", ""), body.value("base", ""), write, why);
}

std::string version_of(const fs::path& dir) {
    const auto text = read_whole(dir / L"kiln.mod.json");
    const json j = text ? json::parse(*text, nullptr, false, true) : json();
    return j.is_object() && j.contains("version") && j["version"].is_string() ? j["version"].get<std::string>() : std::string();
}

std::string utc_now() {
    const std::time_t t = std::chrono::system_clock::to_time_t(std::chrono::system_clock::now());
    std::tm tm{};
    gmtime_s(&tm, &t);
    char b[32];
    std::strftime(b, sizeof b, "%Y-%m-%dT%H:%M:%SZ", &tm);
    return b;
}

// This start's archive writer and what Kiln knows it from (archive_base.h, registry-followups 2): kiln.launch's backend,
// kiln-eml's state (kiln.eml state_json: the text of kiln/state/eml.json), and whether kiln-eml's ownership receipt and
// ShroudForge's record of its pass exist (their names only; neither is read).
registry::ArchiveBase archive_writer() {
    registry::ArchiveBaseInputs in;
    if (const auto* t = g_api ? static_cast<const KilnLaunchApi*>(g_api->service(KilnStr{KILN_LAUNCH_SERVICE, sizeof(KILN_LAUNCH_SERVICE) - 1}, KILN_LAUNCH_VERSION)) : nullptr;
        t && t->header.size >= offsetof(KilnLaunchApi, status) + sizeof(void*) && t->status) {
        KilnLaunchStatus st{};
        st.size = sizeof st;
        if (t->status(&st) == KILN_OK)
            in.backend = st.backend == KILN_LAUNCH_BACKEND_KILN        ? "kiln"
                         : st.backend == KILN_LAUNCH_BACKEND_HOSTED    ? "hosted"
                         : st.backend == KILN_LAUNCH_BACKEND_INSTALLED ? "installed"
                                                                       : "none";
    }
    if (const auto* e = g_api ? static_cast<const KilnEmlApi*>(g_api->service(KilnStr{KILN_EML_SERVICE, sizeof(KILN_EML_SERVICE) - 1}, 1)) : nullptr;
        e && e->header.size >= offsetof(KilnEmlApi, state_json) + sizeof(void*) && e->state_json) {
        size_t len = 0;
        e->state_json(nullptr, 0, &len);
        if (len > 0 && len < (size_t{4} << 20)) {
            std::string text(len, '\0');
            size_t got = 0;
            if (e->state_json(text.data(), text.size(), &got) == KILN_OK || got == len) {
                in.eml_state = json::parse(text.substr(0, std::min(got, len)), nullptr, false);
                in.eml = in.eml_state.is_object();
            }
        }
    }
    wchar_t exe[MAX_PATH * 2] = {};
    const DWORD n = GetModuleFileNameW(nullptr, exe, static_cast<DWORD>(std::size(exe)));
    if (n > 0 && n < std::size(exe)) {
        const std::u8string stem = fs::path(exe).stem().u8string();
        in.stem.assign(stem.begin(), stem.end());
    }
    const std::string game = api_path(KILN_PATH_GAME);
    in.exists = [game](const std::string& rel) {
        if (game.empty() || rel.find("..") != std::string::npos) return false;
        std::error_code ec;
        return fs::is_regular_file(u8(game) / u8(rel), ec);
    };
    return registry::archive_base(in);
}
std::string writer_of() { return archive_writer().writer; }

// kiln-eml's change journal of this start (archive_journal.h read_archive_journal): <kiln>/eml/ownership/<exe
// stem>.change-journal.json, only from the game folder itself (no part of the path a symbolic link, a junction or another
// reparse point, as kiln-eml refuses for its own writes), read and closed at once, at most 16 MiB.
std::optional<std::string> archive_journal(std::string* generation, std::string* why) {
    if (!eml_live_generation(generation, why)) return std::nullopt;
    wchar_t exe[MAX_PATH * 2] = {};
    const DWORD n = GetModuleFileNameW(nullptr, exe, static_cast<DWORD>(std::size(exe)));
    if (n == 0 || n >= std::size(exe)) return *why = "the game's file name is not known", std::nullopt;
    const auto reparse = [](const fs::path& p) {
        const DWORD a = GetFileAttributesW(p.c_str());
        return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_REPARSE_POINT) != 0;
    };
    return read_archive_journal(u8(api_path(KILN_PATH_KILN)), fs::path(exe).stem(), reparse, why);
}

// The drift probe's suspects (drift.h): the hook map's foreign modules and hosted ShroudForge. Once per world, no lock held.
std::vector<std::string> drift_suspects_now() {
    bool hosted = false;
    if (const auto* t = g_api ? static_cast<const KilnLaunchApi*>(g_api->service(KilnStr{KILN_LAUNCH_SERVICE, sizeof(KILN_LAUNCH_SERVICE) - 1}, KILN_LAUNCH_VERSION)) : nullptr;
        t && t->header.size >= offsetof(KilnLaunchApi, status) + sizeof(void*) && t->status) {
        KilnLaunchStatus st{};
        st.size = sizeof st;
        hosted = t->status(&st) == KILN_OK && (st.backend == KILN_LAUNCH_BACKEND_HOSTED || st.backend == KILN_LAUNCH_BACKEND_INSTALLED);
    }
    json map;
    const auto* hooks = g_api ? static_cast<const KilnHooksApi*>(g_api->service(KilnStr{KILN_HOOKS_SERVICE, sizeof(KILN_HOOKS_SERVICE) - 1}, 1)) : nullptr;
    if (hooks && hooks->header.size >= offsetof(KilnHooksApi, map_json) + sizeof(void*) && hooks->map_json) {
        size_t len = 0;
        hooks->map_json(nullptr, 0, &len);
        if (len > 0 && len < (size_t{64} << 20)) {
            std::string text(len + 1, '\0');
            size_t got = 0;
            if (hooks->map_json(text.data(), text.size(), &got) == KILN_OK) {
                text.resize(std::min(got, len));
                map = json::parse(text, nullptr, false);
            }
        }
    }
    return drift_suspects(map, hosted);
}

Env make_env() {
    Env e;
    KilnBuildInfo bi{};
    bi.size = sizeof bi;
    if (g_api && g_api->build_info && g_api->build_info(&bi) == KILN_OK) {
        e.exe_sha256 = bi.exe_sha256;
        e.side = bi.side == KILN_SIDE_CLIENT ? "client" : "server";
        if (bi.kiln_version.ptr) e.kiln_version.assign(bi.kiln_version.ptr, bi.kiln_version.len);
    }
    e.owner_alive = [](uint64_t owner) {
        const auto* table = owners();
        uint32_t grants = 0;
        return table && table->header.size >= offsetof(KilnOwnersInternalApi, grants) + sizeof(table->grants) &&
               table->grants && table->grants(owner, &grants) == KILN_OK;
    };
    e.prepare_live = true;
    e.owner_gone = &registry_live_owner_gone;
    e.pid = GetCurrentProcessId();
    e.now = &utc_now;
    {
        const registry::ArchiveBase a = archive_writer();
        e.writer = a.writer;
        e.archive = a.archive;
    }
    e.archive_journal = &archive_journal;
    e.source = [](std::string* why) -> std::unique_ptr<model::Source> {
        kiln::boot_view::LiveView& v = live_view();
        v.restart(); // each owned source is a new read generation; the prior stage/ledger source may be released
        pristine_view().restart();
        if (!v.ready(why)) return nullptr;
        return model::live_source(pristine_view(), [](uint32_t type, std::vector<KilnAssetKey>* out) {
            const KilnAssetsApi* a = assets();
            if (!a) return false;
            size_t n = 0;
            if (a->resource_list(type, nullptr, 0, &n) != KILN_OK && n == 0) return n == 0;
            out->resize(n);
            size_t got = 0;
            if (a->resource_list(type, out->data(), out->size(), &got) != KILN_OK) return false;
            out->resize(std::min(got, out->size()));
            return true;
        });
    };
    const std::string side = e.side, exe = e.exe_sha256;
    e.regmap = [side, exe](map::RegMap* out, std::vector<std::string>* errs) {
        return map::load_dir(api_path(KILN_PATH_KILN) + "\\regmap", side, exe, out, errs);
    };
    const auto* caps = g_api && g_api->service ? static_cast<const KilnCapsApi*>(
        g_api->service(KilnStr{KILN_CAPS_SERVICE, sizeof(KILN_CAPS_SERVICE) - 1}, KILN_CAPS_VERSION)) : nullptr;
    if (caps && caps->header.size >= offsetof(KilnCapsApi, report) + sizeof(caps->report) && caps->report) {
        e.report_cap = [caps, owner = g_owner](const std::string& name, uint32_t state, uint32_t reason, const std::string& detail) {
            return caps->report(owner, KilnStr{name.data(), name.size()}, state, reason, KilnStr{detail.data(), detail.size()});
        };
    }
    e.folders = [] {
        std::vector<std::pair<std::string, std::string>> out;
        for (const fs::path& dir : mod_folder_list()) {
            const std::string id = kiln::boot::mod_id_of(dir);
            if (!id.empty()) out.emplace_back(id, version_of(dir));
        }
        return out;
    };
    // the data sections as kiln.content's packs read them (packs.cpp registry_sections over the enabled mods' pack files)
    e.data = [](const Schema& schema) {
        std::vector<Env::DataFile> out;
        std::string signature;
        for (RegistrySection& s : registry_sections(discover_packs(&signature), schema))
            out.push_back({s.mod, s.version, s.rel, std::move(s.batches), std::move(s.refused), s.error, s.side});
        return out;
    };
    e.write_report = [](const std::string& text) { return write_whole(u8(api_path(KILN_PATH_STATE)) / u8"registry.json", text); };
    e.write_data = [](const std::string& rel, const std::string& text) { return write_whole(data_root() / u8(rel), text); };
    e.read_data = [](const std::string& rel) { return read_whole(data_root() / u8(rel)); };
    e.data_exists = [](const std::string& rel) {
        std::error_code ec;
        return fs::is_regular_file(data_root() / u8(rel), ec);
    };
    // the plans' pruning (registry-fixes item 6): the regular files of a data folder with their last write, and removing one
    e.list_data = [](const std::string& rel) {
        std::vector<Env::DataEntry> out;
        std::error_code ec;
        for (auto it = fs::directory_iterator(data_root() / u8(rel), ec); !ec && it != fs::directory_iterator(); it.increment(ec)) {
            std::error_code fe;
            if (!it->is_regular_file(fe) || it->is_symlink(fe)) continue;
            const auto when = fs::last_write_time(it->path(), fe);
            if (fe) continue;
            const std::u8string name = it->path().filename().u8string();
            out.push_back({std::string(name.begin(), name.end()), static_cast<int64_t>(when.time_since_epoch().count())});
        }
        return out;
    };
    e.remove_data = [](const std::string& rel) {
        std::error_code ec;
        return fs::remove(data_root() / u8(rel), ec) && !ec;
    };
    e.entry_support = &nc_registry_supports;
    e.stack_guard = [](std::string* why) { return guard_set(true, why); };
    e.log = [](int level, const std::string& line) {
        if (level >= 2) LOGE("%s", line.c_str());
        else if (level == 1) LOGW("%s", line.c_str());
        else LOGI("%s", line.c_str());
    };
    return e;
}

// The lab's registry proof (proof.h): nothing at all without <data>/content/registry/proof/request.json (one lookup). With
// it, the proof reads the base before the stage runs and before any patch of this start is written (a view that is not
// open is opened for it and released again; the stage then opens its own read generation).
void lab_proof() {
    proof::Env e;
    e.read_data = [](const std::string& rel) { return read_whole(data_root() / u8(rel)); };
    e.exists = [](const std::string& rel) {
        std::error_code ec;
        return fs::is_regular_file(data_root() / u8(rel), ec);
    };
    if (!proof::requested(e)) return;
    Env s = make_env();
    e.write_data = s.write_data;
    e.regmap = s.regmap;
    e.log = s.log;
    e.source = s.source;
    e.side = s.side, e.exe_sha256 = s.exe_sha256, e.writer = s.writer;
    e.schema = &registry_schema();
    // open the view only when it is not open yet, and release it again only then
    kiln::boot_view::LiveView& v = live_view();
    std::string closed;
    const bool reopened = !v.ready(&closed);
    if (reopened) v.restart();
    e.bytes = [&v](const KilnAssetKey& key, std::vector<uint8_t>* out, std::string* why) {
        std::string b;
        if (v.bytes(key, size_t{256} << 20, &b, why) != KILN_OK) return false;
        out->assign(b.begin(), b.end());
        return true;
    };
    e.common_files = [] {
        std::vector<std::pair<std::string, std::string>> out;
        std::error_code ec;
        for (const auto& f : fs::directory_iterator(u8(api_path(KILN_PATH_KILN)) / u8"regmap" / u8"common", ec))
            if (f.path().extension() == L".json")
                if (auto t = read_whole(f.path())) out.emplace_back(f.path().filename().string(), std::move(*t));
        return out;
    };
    proof::run(e);
    if (reopened) v.release();
}

// ---- the C tables ----
#if defined(_MSC_VER) || defined(__clang__) && defined(_WIN32)
// A mod's native callback, guarded: a fault inside it is caught (only plain data in this frame).
bool guarded_stage(KilnRegistryStageFn fn, const KilnRegistryStageInfo* info, void* user) {
    __try {
        fn(info, user);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool guarded_ready(KilnRegistryReadyFn fn, const KilnRegistryReady* r, void* user) {
    __try {
        fn(r, user);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
#else
bool guarded_stage(KilnRegistryStageFn fn, const KilnRegistryStageInfo* info, void* user) { return fn(info, user), true; }
bool guarded_ready(KilnRegistryReadyFn fn, const KilnRegistryReady* r, void* user) { return fn(r, user), true; }
#endif

// The mod id Kiln binds an owner to (kiln_registry.h "Mods"): the core's id of the owner (kiln.internal.owners) without its
// kind prefix ("mod:", "native:", ...), folded to lower case. Empty: no such owner, or an id that is no mod id.
std::string bound_mod(KilnOwner owner, bool* revoked = nullptr) {
    if (revoked) *revoked = false;
    const auto* t = owners();
    if (!t || t->header.version < 2 || t->header.size < offsetof(KilnOwnersInternalApi, owner_id) + sizeof(void*) || !t->owner_id) return {};  // v2: owner_id
    char buf[256];
    size_t len = 0;
    const KilnResult rc = t->owner_id(owner, buf, sizeof buf, &len);
    if ((rc == KILN_ERR_REVOKED || rc == KILN_ERR_NOT_FOUND) && revoked) *revoked = true;  // revoked, or never made
    if (rc != KILN_OK) return {};
    std::string id(buf, len);
    if (const size_t colon = id.find(':'); colon != std::string::npos) id = id.substr(colon + 1);
    for (char& c : id)
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    std::string err;
    return mod_id_ok(id, &err) ? id : std::string();
}

// The core's id of an owner as it keeps it ("emf:big_stacks", "mod:bigstacks"), for a code writer's row in registry.json
// (registry_live.cpp name_code_writer). Empty: no such owner (revoked or never made), or an id that is not plain text.
std::string owner_text(KilnOwner owner) {
    const auto* t = owners();
    if (!owner || !t || t->header.version < 2 || t->header.size < offsetof(KilnOwnersInternalApi, owner_id) + sizeof(void*) || !t->owner_id) return {};
    char buf[128];
    size_t len = 0;
    if (t->owner_id(owner, buf, sizeof buf, &len) != KILN_OK || len == 0 || len >= sizeof buf) return {};
    for (size_t i = 0; i < len; ++i)
        if (static_cast<unsigned char>(buf[i]) < 0x20 || buf[i] == 0x7f) return {};
    return std::string(buf, len);
}

KilnResult KILN_CALL t_status(KilnRegistryStatus* out) {
    if (!out || out->size < 40) return KILN_ERR_INVALID_ARGUMENT;
    const Runner::Counts c = runner().counts();
    std::memset(reinterpret_cast<char*>(out) + sizeof(uint32_t), 0, 40 - sizeof(uint32_t));
    out->size = 40;
    out->phase = static_cast<uint32_t>(runner().phase());
    out->generation = runner().generation();
    out->edits = c.edits, out->refused = c.refused, out->conflicts = c.conflicts;
    out->world = KILN_WORLD_KINDS;
    out->flags = c.failed ? KILN_REGISTRY_STATUS_FAILED : 0;
    return KILN_OK;
}
KilnResult KILN_CALL t_schema(char* buf, size_t cap, size_t* out_len) { return copy_out(runner().schema_json(), buf, cap, out_len); }
KilnResult KILN_CALL t_field_info(KilnStr q, KilnRegistryFieldInfo* out) {
    if (!out || out->size < 192 || !str_ok(q, 1024)) return KILN_ERR_INVALID_ARGUMENT;
    Runner::FieldInfo fi;
    if (!runner().field_info(str(q), &fi)) return KILN_ERR_NOT_FOUND;
    out->size = 192;
    out->applies = fi.applies, out->mapping = fi.mapping, out->type = fi.type;
    out->flags = fi.read_only ? KILN_REGISTRY_FIELD_READ_ONLY : 0;
    out->reserved = 0;
    const size_t n = std::min(fi.reason.size(), sizeof out->reason - 1);
    std::memcpy(out->reason, fi.reason.data(), n);
    out->reason[n] = 0;
    return KILN_OK;
}
KilnResult KILN_CALL t_subscribe_stage(KilnOwner owner, KilnRegistryStageFn fn, void* user, KilnHandle* out) {
    if (!owner || !fn || !out) return KILN_ERR_INVALID_ARGUMENT;
    bool revoked = false;
    const std::string m = bound_mod(owner, &revoked);
    if (revoked) return KILN_ERR_REVOKED;
    if (m.empty()) {
        LOGW("registry: owner %llu has no mod id Kiln can bind it to: its stage callback is refused", static_cast<unsigned long long>(owner));
        return KILN_ERR_INVALID_ARGUMENT;
    }
    return runner().subscribe_stage(owner, m, [fn, user, m](Stage s, const View&) {
        KilnRegistryStageInfo info{};
        info.size = sizeof info;
        info.stage = s == Stage::Add ? KILN_REGISTRY_PHASE_ADD : s == Stage::Alter ? KILN_REGISTRY_PHASE_ALTER : KILN_REGISTRY_PHASE_FINAL;
        info.mod = KilnStr{m.data(), m.size()};
        return guarded_stage(fn, &info, user);
    }, out);
}
KilnResult KILN_CALL t_submit(KilnOwner owner, KilnStr ir, KilnRegistrySubmitResult* out, char* report, size_t cap, size_t* out_len) {
    if (!owner || !str_ok(ir, KILN_REGISTRY_MAX_IR_BYTES) || (out && out->size < 16) || (!report && cap)) return KILN_ERR_INVALID_ARGUMENT;
    uint32_t acc = 0, ref = 0;
    std::string rep;
    bool revoked = false;
    const std::string m = bound_mod(owner, &revoked);
    if (revoked) {  // kiln_registry.h: a revoked owner is KILN_ERR_REVOKED
        if (out) out->size = 16, out->accepted = 0, out->refused = 0, out->reserved = 0;
        report_out("{\"accepted\":0,\"refused\":[],\"error\":\"the owner is revoked\"}", report, cap, out_len);
        return KILN_ERR_REVOKED;
    }
    const KilnResult rc = runner().submit(owner, m, str(ir), &acc, &ref, &rep);
    if (out) out->size = 16, out->accepted = acc, out->refused = ref, out->reserved = 0;
    report_out(rep, report, cap, out_len);
    return rc;
}
KilnResult KILN_CALL t_read(uint32_t view, KilnStr reg, KilnStr id, KilnStr field, char* buf, size_t cap, size_t* out_len) {
    if (!str_ok(reg, 1024) || !str_ok(id, 1024) || !str_ok(field, 1024)) return KILN_ERR_INVALID_ARGUMENT;
    std::string text;
    const KilnResult rc = runner().read(view, str(reg), str(id), str(field), &text);
    return rc == KILN_OK ? copy_out(text, buf, cap, out_len) : rc;
}
KilnResult KILN_CALL t_select(uint32_t view, KilnStr reg, KilnStr sel, char* buf, size_t cap, size_t* out_len) {
    if (!str_ok(reg, 1024) || !str_ok(sel, KILN_REGISTRY_MAX_IR_BYTES)) return KILN_ERR_INVALID_ARGUMENT;
    std::string text;
    const KilnResult rc = runner().select(view, str(reg), str(sel), &text);
    return rc == KILN_OK ? copy_out(text, buf, cap, out_len) : rc;
}
KilnResult KILN_CALL t_report(char* buf, size_t cap, size_t* out_len) {
    std::string text;
    const KilnResult rc = runner().report_json(&text);
    return rc == KILN_OK ? copy_out(text, buf, cap, out_len) : rc;
}
KilnResult KILN_CALL t_subscribe_ready(KilnOwner owner, KilnRegistryReadyFn fn, void* user, KilnHandle* out) {
    if (!owner || !fn || !out) return KILN_ERR_INVALID_ARGUMENT;
    return runner().subscribe_ready(owner, [fn, user](Phase p, uint64_t gen) {
        KilnRegistryReady r{};
        r.size = sizeof r;
        r.phase = static_cast<uint32_t>(p);
        r.generation = gen;
        r.world = KILN_WORLD_KINDS;
        return guarded_ready(fn, &r, user);
    }, out);
}
KilnResult KILN_CALL t_unsubscribe(KilnHandle h) { return runner().unsubscribe(h); }

const KilnRegistryApi kTable = {
    {sizeof(KilnRegistryApi), KILN_REGISTRY_VERSION},
    &t_status, &t_schema, &t_field_info, &t_subscribe_stage, &t_submit, &t_read, &t_select, &t_report, &t_subscribe_ready, &t_unsubscribe,
};

KilnResult KILN_CALL h_set_order(KilnStr order) {
    if (!str_ok(order, KILN_REGISTRY_MAX_IR_BYTES)) return KILN_ERR_INVALID_ARGUMENT;
    const KilnResult rc = runner().set_order(str(order));
    if (rc == KILN_OK) LOGI("registry: Kiln.Host gave the runtime load order (the stage runs mods in it)");
    return rc;
}
KilnResult KILN_CALL h_set_mode(uint32_t mode) { return runner().set_mode(mode); }
KilnResult KILN_CALL h_read_pass(KilnOwner owner, KilnStr reg, KilnStr id, KilnStr field, char* buf, size_t cap, size_t* len) {
    if (!str_ok(reg, 1024) || !str_ok(id, 1024) || !str_ok(field, 1024) || (!buf && cap)) return KILN_ERR_INVALID_ARGUMENT;
    std::string mod;
    const KilnResult own = bound_owner(owner, &mod);
    if (own != KILN_OK) return own;
    std::string text;
    const KilnResult rc = registry_live_read(str(reg), str(id), str(field), &text, mod);
    return rc == KILN_OK ? copy_out(text, buf, cap, len) : rc;
}
const KilnRegistryHostApi kHostTable = {{sizeof(KilnRegistryHostApi), KILN_REGISTRY_HOST_VERSION}, &h_set_order, &h_set_mode, &h_read_pass};

}  // namespace

Runner& runner() {
    static Runner& r = *new Runner(make_env());  // never destroyed: the tables live as long as the process
    return r;
}

void bind_service() {
    if (!g_api) return;
    const auto* owner_hooks = static_cast<const KilnHooksApi*>(g_api->service(
        KilnStr{KILN_HOOKS_SERVICE, sizeof(KILN_HOOKS_SERVICE) - 1}, 1));
    const KilnResult cleanup = bind_owner_cleanup(owner_hooks, g_owner, &runner());
    if (cleanup != KILN_OK) {
        LOGE("registry: owner-retiring notification unavailable (rc %d): the registry service was not registered", cleanup);
        return;  // never retain callbacks into mods without their owner's cleanup
    }
    static const boothook::Hooks hooks{
        [](const std::vector<boothook::OldDoc>& docs) {
            // lab only, nothing without the request file (proof.h). Before the stage: the proof opens its own read generation of
            // the view, which would invalidate the source a deferred stage keeps for first use (lazy_source).
            lab_proof();
            std::vector<std::pair<KilnAssetKey, std::string>> freeze;
            for (const auto& doc : docs) freeze.emplace_back(doc.res, doc.type);
            // An unused registry defers decoding, but old writers must still compose over their pre-write bytes.
            // Retain owned documents, never the callback's stack reference.
            Runner::OldWritesFn old;
            if (!docs.empty()) old = [docs](model::Model& model) { return boothook::old_writes(docs, model); };
            return runner().run(old, freeze);
        },
        &boothook::write, [](const std::vector<BootOutcome>& o) { runner().finish(o); }};
    boothook::set(&hooks);
    // the live back-end (registry_live.cpp, S9): now and worldLoad fields as store layers at each world load
    runner().set_live([](const LiveHandoff& h) {
        RegistryLive in;
        in.schema = std::make_shared<const Schema>(*h.schema);
        in.regmap = std::make_shared<const map::RegMap>(*h.regmap);
        in.result = *h.result;
        in.ids = *h.ids;
        in.nums = *h.nums;
        in.base = *h.base;
        in.old = *h.old;
        in.mods = *h.inputs;
        in.decoded = *h.decoded;
        in.added_guid = &model::added_guid;
        in.define_entry = &registry_entry_define_native;
        in.forget_entry = &registry_entry_forget_native;
        in.pin_definition = &pin_definition;
        in.entry_registered = [](const EntryRef& ref, std::string* why) {
            return nc_registry_registered(ref.registry == "items" ? KILN_CONTENT_ITEMS : KILN_CONTENT_RECIPES, ref.id, why);
        };
        in.prepare_visibility = [](const Result& result, const std::map<std::string, uint32_t>& nums) {
            static uint64_t generation = 0; // store lock, world's registration thread
            auto view = std::make_shared<Visibility>(); view->generation = ++generation;
            std::map<EntryRef, std::string> failed;
            for (const auto& ref : result.removed) {
                const auto number = nums.find(ref.registry + "|" + ref.id);
                if (number == nums.end() || !number->second) { failed[ref] = "entry visibility has no stable numeric identity on this build"; continue; }
                if (ref.registry == "items") view->items.insert(number->second);
                else if (ref.registry == "recipes") view->recipes.insert(number->second);
                else if (ref.registry == "loot") view->loot.insert(number->second);
            }
            nc_registry_visibility(std::move(view));
            std::string why;
            if (!nc_registry_apply_visibility(&why))
                for (const auto& ref : result.removed)
                    if (ref.registry == "items" || ref.registry == "recipes") failed[ref] = why;
            // A dependent recipe cannot be excluded separately while claiming its item removal succeeded.
            if (std::any_of(failed.begin(), failed.end(), [](const auto& row) { return row.first.registry == "recipes"; }))
                for (const auto& ref : result.removed)
                    if (ref.registry == "items") failed[ref] = "dependent recipe visibility failed; item removal was not applied";
            return failed;
        };
        in.verify_visibility = &nc_registry_verify_visibility;
        in.publish_visibility = &nc_registry_publish_visibility;
        in.record_entry = [](const EntryRef& ref, const json& fields) {
            nc_registry_record(ref.registry == "items" ? KILN_CONTENT_ITEMS : KILN_CONTENT_RECIPES, ref.id, fields.dump());
        };
        if (h.pre_refused) in.pre_refused = *h.pre_refused;
        if (h.boot_fields) in.boot_fields = *h.boot_fields;
        in.check = h.check;
        in.report = h.report;
        in.changed = h.changed;
        in.prepare = h.prepare;
        in.rollback = h.rollback;
        in.left_out = h.left_out;
        in.on_world_thread = [](uint32_t kind) { return g_api && g_api->on_world_thread && g_api->on_world_thread(kind); };
        in.suspects = &drift_suspects_now;
        in.owner_name = &owner_text;
#if KC_GAMEDATA
        // dev.32: buffs and abilities are kiln.gamedata's entries
        in.gd_kind = &gd_registry_kind;
        in.gd_entry = &gd_registry_entry;
        in.gd_slots = &gd_registry_slots;
        in.gd_written = &gd_registry_written;
#endif
        if (!in.result.definitions.empty()) registry_entries_prepare(&in);
        const Result reconciled = in.result;
        registry_live_install(std::move(in));
        if (h.report) h.report(reconciled, false); // same start generation; failed definitions must never stay reported as added
    });
    const KilnResult a = g_api->register_service(g_owner, KilnStr{KILN_REGISTRY_SERVICE, sizeof(KILN_REGISTRY_SERVICE) - 1}, &kTable);
    const KilnResult b = g_api->register_service(g_owner, KilnStr{KILN_REGISTRY_HOST_SERVICE, sizeof(KILN_REGISTRY_HOST_SERVICE) - 1}, &kHostTable);
    LiveTableEnv live_env;
    live_env.bound_mod = [](KilnOwner owner, bool* revoked) { return bound_mod(owner, revoked); };
    live_env.generation = [] { return runner().generation(); };
    const KilnResult c = g_api->register_service(g_owner, KilnStr{KILN_REGISTRY_LIVE_SERVICE, sizeof(KILN_REGISTRY_LIVE_SERVICE) - 1}, live_table(live_env));
    if (a != KILN_OK || b != KILN_OK || c != KILN_OK) LOGW("registry: kiln.registry rc %d, kiln.internal.registry rc %d, kiln.registry.live rc %d", a, b, c);
}

}  // namespace kc::registry::stage
