// SPDX-License-Identifier: MIT
// kiln.vfx's builder in the game. The VFX system enumerates VFX, mesh and curve resources once, through the resource service kiln.assets
// answers, so everything has to be in place before that: this runs in on_ready, before the game's entry point. cook/ does the building and
// is the same code kiln-vfxcook runs offline; this file only finds the mods, checks this game and hands the result over.
//
// Order matters to the game. A blob is served before anything names it, and a VFX is put after the meshes and textures it names: a missing
// blob is fatal ("error streaming vfx mesh data", "error streaming vfx pipeline").
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <map>
#include <mutex>
#include <set>
#include <string>
#include <vector>

#include "boot.h"
#if __has_include("kiln_build.h")
#include "kiln_build.h"  // KILN_BUILD: the build stamp ("+20261113.2e75c1f"), empty without it
#endif
#ifndef KILN_BUILD
#define KILN_BUILD ""
#endif
#include "services/kiln_progress.h"
#include "cook/vfxcook.h"
#include "core.h"
#include "generated.h"
#include "services/kiln_assets.h"
#include "services/kiln_profile.h"

namespace kvx {
namespace {

namespace fs = std::filesystem;
using kvb::Bytes;
using kvb::ojson;

const KilnApi* g_api = nullptr;
KilnOwner g_self = 0;
const KilnAssetsApi* g_assets = nullptr;

std::mutex g_mu;  // the state below, after on_ready
struct Built {
    uint64_t definition = 0;  // a hash of the block's JSON
    int64_t one_shot_ns = 0;  // 0: runs until stopped
    std::string name;         // <mod>:<block>, for messages
    uint64_t served = 0;      // a VFX: a hash of the VfxResource bytes kiln.assets serves
    uint32_t served_size = 0;
};
std::map<kvb::Guid, Built> g_built;
std::set<kvb::Guid> g_foreign;  // Kiln VFX whose GUID the game's table resolves to other data: refused like unknown ones
std::atomic<int> g_own_checks{0};          // updates left to check that the game's table holds Kiln's own data for Kiln's VFX
std::atomic<bool> g_any_foreign{false};

uint64_t bytes_hash(const uint8_t* p, size_t n) {
    uint64_t h = 1469598103934665603ull;
    for (size_t i = 0; i < n; ++i) h = (h ^ p[i]) * 1099511628211ull;
    return h;
}
std::string g_status = "{}";

// ---- reskins: materials of mods that draw in place of a game material ("replaces") ---------------------------------------------------
// Each is served as a kiln.assets override under an owner of its mod's ("vfx-reskin:<mod>"), never under kiln.vfx's own, so it goes away
// with the mod. Kiln.Blocks compiles the block when the mod loads and passes the mod's owner to define_json: that owner is watched, and
// when it is revoked (the mod unloads) the override is taken off and the next mod in the load order that replaces the same material, if
// any, is served instead. A reload attaches the new owner and serves it again. kiln.assets serves the bytes; the game keeps the copy of
// the material it loaded at start, so what is drawn changes back at the next start (docs/kiln/modding/materials.md).
struct Reskin {
    std::string mod, name;     // the mod and the material block, for messages
    kvb::Guid guid;            // the game material it replaces
    Bytes data;                // the RenderMaterialResource
    size_t order = 0;          // the mod's place in the load order: the first one not released draws
    KilnOwner owner = 0;       // vfx-reskin:<mod>, what the override is served under
    KilnOwner watch = 0;       // the mod's own owner, once Kiln.Blocks compiled the block; 0 = not attached (boot only)
    bool served = false;       // kiln.assets has the override now
    bool released = false;     // the mod unloaded
    bool failed = false;       // kiln.assets refused it: no such game material
};
std::vector<Reskin> g_reskins;                 // under g_mu
std::map<std::pair<std::string, kvb::Guid>, uint64_t> g_reskin_defs;  // what was built at start, by mod and game material
std::map<std::string, KilnOwner> g_reskin_owners;
std::atomic<bool> g_reskin_watch{false};
std::set<kvb::Guid> g_game_materials;  // original KFC keys only: resource_put accepts unknown overrides before binding
// Every material the boot build built, by mod and block: define_json takes a block of the same definition whose files still have the same
// bytes from here instead of decoding its textures again (kvb::MaterialRecord). Written once in on_ready, read under g_mu.
std::map<std::pair<std::string, std::string>, kvb::MaterialRecord> g_material_records;
kvb::GameCounts g_game;
std::atomic<bool> g_off{false};           // the late check failed
std::atomic<bool> g_late_done{true};      // nothing to check until something was built

fs::path to_path(const std::string& utf8) { return fs::path(std::u8string(reinterpret_cast<const char8_t*>(utf8.data()), utf8.size())); }
std::string to_utf8(const fs::path& p) {
    const std::u8string s = p.u8string();
    return std::string(reinterpret_cast<const char*>(s.data()), s.size());
}

std::string api_path(int32_t kind) {
    char buf[1024];
    size_t n = 0;
    if (g_api->path(kind, buf, sizeof buf, &n) != KILN_OK || n >= sizeof buf) return "";
    return std::string(buf, n);
}

bool read_all(const std::string& utf8, Bytes* out, std::string* why) {
    std::error_code ec;
    const fs::path p = to_path(utf8);
    const auto size = fs::file_size(p, ec);
    if (ec) {
        *why = "not there";
        return false;
    }
    std::ifstream f(p, std::ios::binary);
    if (!f) {
        *why = "can't open it";
        return false;
    }
    out->resize(static_cast<size_t>(size));
    f.read(reinterpret_cast<char*>(out->data()), static_cast<std::streamsize>(out->size()));
    if (!f) {
        *why = "can't read it";
        return false;
    }
    return true;
}

std::string read_text(const fs::path& p) {
    std::ifstream f(p, std::ios::binary);
    return std::string(std::istreambuf_iterator<char>(f), {});
}

// kiln/data/vfx/cache/<key>.tex: {ContentHash of the chain, the chain}. An entry that doesn't check out is rebuilt.
kvb::TextureCache disk_cache() {
    kvb::TextureCache c;
    const std::string data = api_path(KILN_PATH_DATA);
    if (data.empty()) return c;
    const fs::path dir = to_path(data) / L"vfx" / L"cache";
    c.get = [dir](const std::string& key, Bytes* out) {
        Bytes raw;
        std::string why;
        if (!read_all(to_utf8(dir / to_path(key + ".tex")), &raw, &why) || raw.size() < 16) return false;
        kvb::ContentHash want;
        std::memcpy(&want, raw.data(), 16);
        if (want != kvb::content_hash(raw.data() + 16, raw.size() - 16)) return false;
        out->assign(raw.begin() + 16, raw.end());
        return true;
    };
    c.put = [dir](const std::string& key, const Bytes& chain) {
        std::error_code ec;
        fs::create_directories(dir, ec);
        const fs::path tmp = dir / to_path(key + ".tmp");
        {
            std::ofstream f(tmp, std::ios::binary);
            if (!f) return;
            const kvb::ContentHash h = kvb::content_hash(chain.data(), chain.size());
            f.write(reinterpret_cast<const char*>(&h), 16);
            f.write(reinterpret_cast<const char*>(chain.data()), static_cast<std::streamsize>(chain.size()));
        }
        fs::rename(tmp, dir / to_path(key + ".tex"), ec);
        if (ec) fs::remove(tmp, ec);
    };
    return c;
}

// An open handle, closed on scope exit.
struct OwnedHandle {
    HANDLE h = INVALID_HANDLE_VALUE;
    explicit OwnedHandle(HANDLE x) : h(x) {}
    OwnedHandle(const OwnedHandle&) = delete;
    OwnedHandle& operator=(const OwnedHandle&) = delete;
    ~OwnedHandle() {
        if (h != INVALID_HANDLE_VALUE) CloseHandle(h);
    }
    bool ok() const { return h != INVALID_HANDLE_VALUE; }
};

HANDLE open_for_read(const fs::path& p, bool folder) {
    return CreateFileW(p.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                       folder ? FILE_FLAG_BACKUP_SEMANTICS : FILE_ATTRIBUTE_NORMAL, nullptr);
}

// Where an opened handle really is (links and junctions resolved); empty when it cannot be told.
std::wstring final_path(HANDLE h) {
    std::wstring out(512, L'\0');
    for (int attempt = 0; attempt < 2; ++attempt) {
        const DWORD n = GetFinalPathNameByHandleW(h, out.data(), static_cast<DWORD>(out.size()), FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
        if (n == 0) return {};
        if (n < out.size()) {
            out.resize(n);
            return out;
        }
        out.resize(n + 1);
    }
    return {};
}

// A link, junction or other reparse point on the way from the mod's folder down to `p` (the folders below the mod's and `p`).
bool reparse_on_the_way(const fs::path& mod, const fs::path& p) {
    const fs::path rel = p.lexically_relative(mod);  // the path as built from the mod's folder: no link is followed here
    if (rel.empty()) return true;
    fs::path at = mod;
    for (const fs::path& part : rel) {
        if (part == L"..") return true;
        at /= part;
        const DWORD a = GetFileAttributesW(at.c_str());
        if (a == INVALID_FILE_ATTRIBUTES || (a & FILE_ATTRIBUTE_REPARSE_POINT)) return true;
    }
    return false;
}

// The JSON files of one of the mod's folders, with the limits of generated.h: blocks/ (the author's data files) and
// blocks/generated/ (what Kiln.Sdk baked from the mod's code). The folder and each file are opened first; what was opened
// is then checked (its final path lies inside the mod's folder, and nothing on the way is a link or reparse point), so a
// link, junction or path swapped anywhere from the mod's folder down cannot lead out; the file is read from that handle,
// its size first. The listing stops at the limits. What is refused is a kiln.log line, and is left out.
// Under Wine (the Linux server and Proton): Wine resolves no link in a handle's final path, and marks a link to a folder as a
// reparse point but a link to a file as a plain file (lab, GE-Proton). So there a link to a folder is refused (on blocks
// itself too), a link to a file is not told from a file: it is read, within the same size, count and depth limits.
void add_limited(const fs::path& mod, const std::wstring& root, const fs::path& folder, const char* prefix,
                 const kvx_generated::Limits& lim, kvb::ModInput* m) {
    std::error_code ec;
    if (!fs::is_directory(folder, ec)) return;
    {
        OwnedHandle dh(open_for_read(folder, true));
        if (!dh.ok()) return;
        if (!kvx_generated::path_inside(root, final_path(dh.h)) || reparse_on_the_way(mod, folder)) {
            KV_W("vfx: %s: %s/ is a link or reparse point, or leads outside the mod's folder: not read", m->id.c_str(), prefix);
            return;
        }
    }
    std::vector<fs::path> files;
    size_t entries = 0;
    bool over = false;
    for (auto it = fs::directory_iterator(folder, ec); !ec && it != fs::directory_iterator(); it.increment(ec)) {
        if (++entries > lim.entries) {
            over = true;
            break;
        }
        if (it->path().extension() != L".json") continue;
        files.push_back(it->path());
        if (files.size() > lim.files) {
            over = true;
            break;
        }
    }
    if (over) {
        KV_W("vfx: %s: %s/ has more than %zu JSON files (or more than %zu entries): none of it is read", m->id.c_str(), prefix,
             lim.files, lim.entries);
        return;
    }
    std::sort(files.begin(), files.end());
    size_t n = 0;
    uint64_t total = 0;
    for (const fs::path& f : files) {
        const std::string name = to_utf8(f.filename());
        OwnedHandle fh(open_for_read(f, false));
        if (!fh.ok()) continue;
        if (!kvx_generated::path_inside(root, final_path(fh.h)) || reparse_on_the_way(mod, f)) {
            KV_W("vfx: %s: %s/%s: not read: a link or reparse point, or it leads outside the mod's folder", m->id.c_str(), prefix, name.c_str());
            continue;
        }
        LARGE_INTEGER size{};
        if (!GetFileSizeEx(fh.h, &size) || size.QuadPart < 0) continue;
        const uint64_t bytes = static_cast<uint64_t>(size.QuadPart);
        if (const char* why = kvx_generated::refusal(lim, n, bytes, total)) {
            KV_W("vfx: %s: %s/%s: not read: %s (%llu bytes; limits: %zu files, %llu bytes each, %llu in all)", m->id.c_str(), prefix,
                 name.c_str(), why, static_cast<unsigned long long>(bytes), lim.files, static_cast<unsigned long long>(lim.file_bytes),
                 static_cast<unsigned long long>(lim.total_bytes));
            continue;
        }
        std::string text(static_cast<size_t>(bytes), '\0');
        uint64_t got = 0;
        while (got < bytes) {
            DWORD part = 0;
            const DWORD want = static_cast<DWORD>(bytes - got < (1u << 20) ? bytes - got : (1u << 20));
            if (!ReadFile(fh.h, text.data() + got, want, &part, nullptr) || part == 0) break;
            got += part;
        }
        char extra = 0;
        DWORD more = 0;
        if (got != bytes || (ReadFile(fh.h, &extra, 1, &more, nullptr) && more != 0)) {
            KV_W("vfx: %s: %s/%s: not read: it changed while it was read", m->id.c_str(), prefix, name.c_str());
            continue;
        }
        if (!kvx_generated::depth_ok(text, lim.depth)) {
            KV_W("vfx: %s: %s/%s: not read: nested deeper than %d", m->id.c_str(), prefix, name.c_str(), lim.depth);
            continue;
        }
        ++n;
        total += bytes;
        m->documents.emplace_back(std::string(prefix) + "/" + name, std::move(text));
    }
}

// One mod's folder -> its input. false: nothing to do here.
bool read_mod(const std::string& dir, const fs::path& pending_root, kvb::ModInput* m) {
    const fs::path d = to_path(dir);
    std::error_code ec;
    const fs::path manifest = d / L"kiln.mod.json";
    if (!fs::exists(manifest, ec)) return false;
    const ojson j = kvb::parse_relaxed(read_text(manifest));
    if (j.is_discarded() || !j.is_object() || !j.contains("id") || !j["id"].is_string()) {
        KV_W("vfx: %s: kiln.mod.json has no id: its VFX blocks are skipped", dir.c_str());
        return false;
    }
    m->id = j["id"].get<std::string>();
    m->dir = dir;
    m->side = j.value("side", "server");
    m->clients = j.value("clients", m->side == "server" ? "none" : "mod");
    m->advanced = j.contains("advanced") && j["advanced"].is_boolean() && j["advanced"].get<bool>();
    // Kiln's own data (what it kept from code at an earlier start): read as before
    auto add_folder = [&](const fs::path& folder, const char* prefix) {
        std::vector<fs::path> files;
        std::error_code e2;
        for (auto it = fs::directory_iterator(folder, e2); !e2 && it != fs::directory_iterator(); it.increment(e2))
            if (it->path().extension() == L".json") files.push_back(it->path());
        std::sort(files.begin(), files.end());
        for (const fs::path& f : files) m->documents.emplace_back(std::string(prefix) + "/" + to_utf8(f.filename()), read_text(f));
    };
    // the mod's own folder: its data files, then what Kiln.Sdk baked from its code (before what Kiln kept from code at an
    // earlier start, so a block of the same name in both is the mod's: the cook keeps the first), each with limits
    std::wstring root;
    {
        OwnedHandle rh(open_for_read(d, true));
        if (rh.ok()) root = final_path(rh.h);
    }
    if (root.empty()) {
        KV_W("vfx: %s: the mod's folder cannot be opened to check where its files are: its blocks are not read", m->id.c_str());
    } else {
        add_limited(d, root, d / L"blocks", "blocks", kvx_generated::kBlocks, m);
        add_limited(d, root, d / L"blocks" / L"generated", "blocks/generated", kvx_generated::kGenerated, m);
    }
    add_folder(pending_root / to_path(m->id), "pending");
    return !m->documents.empty();
}

// Every mod folder in the load order: the profile's, and kiln/mods when no profile says otherwise. The rule kiln.assets uses for manifests.
std::vector<std::string> mod_folders() {
    std::vector<std::string> out;
    const auto* prof = static_cast<const KilnProfileApi*>(g_api->service(KilnStr{"kiln.profile", 12}, 1));
    bool scan_dropins = true;
    if (prof && prof->header.size >= sizeof(KilnProfileApi)) {
        const uint32_t n = prof->count(KILN_PROFILE_KIND_KILN);
        for (uint32_t i = 0; i < n; ++i) {
            KilnProfileMod m{};
            m.size = sizeof m;
            if (prof->get(KILN_PROFILE_KIND_KILN, i, &m) == KILN_OK && m.enabled) out.emplace_back(m.path.ptr, m.path.len);
        }
        if (prof->mode() == KILN_PROFILE_ACTIVE || prof->mode() == KILN_PROFILE_ONCE) scan_dropins = (prof->dropins() & (1u << KILN_PROFILE_KIND_KILN)) != 0;
    }
    if (scan_dropins) {
        const std::string mods = api_path(KILN_PATH_MODS);
        std::error_code ec;
        std::vector<std::string> found;
        for (auto it = fs::directory_iterator(to_path(mods), ec); !ec && it != fs::directory_iterator(); it.increment(ec)) {
            std::error_code e2;
            if (it->is_directory(e2) && !fs::exists(it->path() / L".disabled", e2)) found.push_back(to_utf8(it->path()));
        }
        std::sort(found.begin(), found.end());
        for (std::string& f : found)
            if (std::find(out.begin(), out.end(), f) == out.end()) out.push_back(std::move(f));
    }
    return out;
}

// "shaders": false turns every mod's own particle module off (Kiln's sprite draws those particles); "skipShaderMods" lists mods to do that for.
void shader_switches(kvb::CookOptions* opt) {
    const std::string dir = api_path(KILN_PATH_CONFIG);
    if (dir.empty()) return;
    const ojson j = kvb::parse_relaxed(read_text(to_path(dir) / L"vfx.json"));
    if (j.is_discarded() || !j.is_object()) return;
    const auto on = j.find("shaders");
    if (on != j.end() && on->is_boolean() && !on->get<bool>()) opt->skip_shaders = true;
    const auto list = j.find("skipShaderMods");
    if (list != j.end() && list->is_array())
        for (const ojson& v : *list)
            if (v.is_string()) opt->skip_shader_mods.insert(v.get<std::string>());
}

std::vector<std::string> skip_list() {
    std::vector<std::string> out;
    const std::string dir = api_path(KILN_PATH_CONFIG);
    if (dir.empty()) return out;
    const ojson j = kvb::parse_relaxed(read_text(to_path(dir) / L"vfx.json"));
    if (j.is_discarded() || !j.is_object()) return out;
    const auto it = j.find("skipMods");
    if (it == j.end() || !it->is_array()) return out;
    for (const ojson& v : *it)
        if (v.is_string()) out.push_back(v.get<std::string>());
    return out;
}

// The game's reflection in this process: the same reader kiln-vfxcook runs on the exe file.
kvb::ImageView own_image() {
    const auto* base = reinterpret_cast<const uint8_t*>(GetModuleHandleW(nullptr));
    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
    return kvb::ImageView{base, nt->OptionalHeader.SizeOfImage, reinterpret_cast<uint64_t>(base)};
}
std::string check_layouts() { return kvb::compare_layouts(kvb::read_layouts(own_image())); }
std::string check_material_layouts() { return kvb::compare_layouts(kvb::read_layouts(own_image(), kvb::material_layouts())); }

bool game_has_blob(const kvb::ContentHash& hash) { return kvb::kfc_has_content(to_utf8(to_path(api_path(KILN_PATH_GAME)) / L"enshrouded.kfc"), hash); }

// Read only the original material keys. Looking through kiln.assets here would also find mod-defined and even unbound overrides.
// Keep allocation bounded and reject truncated tables before accepting any keys. Layout: tools/kfc/kfc/archive.py.
bool read_game_materials(const fs::path& path, std::set<kvb::Guid>* out) {
    out->clear();
    std::ifstream f(path, std::ios::binary | std::ios::ate);
    if (!f) return false;
    const auto size = f.tellg();
    if (size < 0x70) return false;
    f.seekg(0);
    uint8_t head[0x70];
    if (!f.read(reinterpret_cast<char*>(head), sizeof head) || std::memcmp(head, "KFC4", 4)) return false;
    uint32_t rel, count;
    std::memcpy(&rel, head + 0x60, 4);
    std::memcpy(&count, head + 0x64, 4);
    const uint64_t first = 0x60ull + rel, length = static_cast<uint64_t>(size);
    if (first > length || count > (length - first) / 32) return false;
    f.seekg(static_cast<std::streamoff>(first));
    std::set<kvb::Guid> keys;
    for (uint32_t i = 0; i < count; ++i) {
        uint8_t key[32];
        if (!f.read(reinterpret_cast<char*>(key), sizeof key)) return false;
        uint32_t type, part;
        std::memcpy(&type, key + 16, 4);
        std::memcpy(&part, key + 20, 4);
        if (type == kvb::kTypeMaterial && part == 0) {
            kvb::Guid g;
            std::memcpy(g.b, key, 16);
            keys.insert(g);
        }
    }
    *out = std::move(keys);
    return true;
}

kvb::GameCounts counts_from_game() {
    const fs::path game = to_path(api_path(KILN_PATH_GAME));
    std::map<uint32_t, kvb::KfcBundle> bundles;
    std::string why;
    if (!kvb::read_kfc_bundles(to_utf8(game / L"enshrouded.kfc"), &bundles, &why)) {
        KV_W("vfx: %s: the budget uses the known numbers for the game's own VFX", why.c_str());
        return {};
    }
    return kvb::game_counts_from(bundles);
}

// What made the carry-over index: Kiln's version with its build stamp (a new Kiln never takes an old index), the game exe and the side.
// An empty game: not known, no index.
kvb::CarryHeader carry_header(const KilnApi* api) {
    kvb::CarryHeader h;
    const KilnStr v = api->version();
    h.kiln = std::string(v.ptr ? v.ptr : "", v.len) + KILN_BUILD;
    KilnBuildInfo bi{};
    bi.size = sizeof bi;
    if (api->build_info(&bi) == KILN_OK && bi.exe_sha256[0]) {
        h.game = std::string(bi.exe_sha256, strnlen(bi.exe_sha256, sizeof bi.exe_sha256));
        h.side = bi.side == KILN_SIDE_CLIENT ? "client" : "server";
    }
    return h;
}

// Atomically, under a name of this process's own (a client and a server sharing a data folder each write whole files; the last one stays,
// and a record either of them reads is checked before it is used).
void write_carry_index(const fs::path& file, const std::string& text) {
    std::error_code ec;
    fs::create_directories(file.parent_path(), ec);
    const fs::path tmp = file.parent_path() / to_path("index." + std::to_string(GetCurrentProcessId()) + ".tmp");
    {
        std::ofstream f(tmp, std::ios::binary | std::ios::trunc);
        f.write(text.data(), static_cast<std::streamsize>(text.size()));
        if (!f) {
            f.close();
            fs::remove(tmp, ec);
            return;
        }
    }
    if (!MoveFileExW(tmp.c_str(), file.c_str(), MOVEFILE_REPLACE_EXISTING)) fs::remove(tmp, ec);
}

void set_status(const char* state, const std::string& detail, const kvb::CookResult* r) {
    ojson j = r ? kvb::report_json(*r) : ojson::object();
    j["fingerprint"] = state;
    if (!detail.empty()) j["detail"] = detail;
    std::lock_guard lk(g_mu);
    g_status = j.dump();
}

KilnStr as_str(const std::string& s) { return KilnStr{s.data(), s.size()}; }

KilnOwner reskin_owner_locked(const std::string& mod) {
    auto it = g_reskin_owners.find(mod);
    if (it != g_reskin_owners.end()) return it->second;
    const std::string id = "vfx-reskin:" + mod;
    const KilnOwner o = g_api->owner_create(KilnStr{id.data(), id.size()});
    if (o) g_reskin_owners[mod] = o;
    return o;
}

// The first replacement of `guid` in load order that is neither released nor refused is served; the others are not. Under g_mu.
void reskin_reconcile_locked(const kvb::Guid& guid) {
    Reskin* want = nullptr;
    for (Reskin& r : g_reskins)
        if (r.guid == guid && !r.released && !r.failed && (!want || r.order < want->order)) want = &r;
    KilnAssetKey k{};
    std::memcpy(k.guid, guid.b, 16);
    k.type = kvb::kTypeMaterial;
    for (Reskin& r : g_reskins) {
        if (r.guid != guid || &r == want || !r.served) continue;
        g_assets->resource_remove(r.owner, &k);
        r.served = false;
        KV_I("vfx: %s: material %s no longer replaces %s%s", r.mod.c_str(), r.name.c_str(), kvb::guid_text(guid).c_str(),
             r.released ? " (the mod unloaded): the game draws its own material again from the next start" : "");
    }
    while (want && !want->served) {
        const KilnResult rc = g_assets->resource_put(want->owner, &k, want->data.data(), want->data.size(), KILN_ASSET_PUT_OVERRIDE);
        if (rc == KILN_OK) {
            want->served = true;
            KV_I("vfx: %s: material %s draws in place of the game material %s", want->mod.c_str(), want->name.c_str(), kvb::guid_text(guid).c_str());
            break;
        }
        want->failed = true;
        KV_W("vfx: %s: material %s: kiln.assets refused to replace %s (%d): not a material of this game build? The game's own draws", want->mod.c_str(),
             want->name.c_str(), kvb::guid_text(guid).c_str(), static_cast<int>(rc));
        Reskin* next = nullptr;
        for (Reskin& r : g_reskins)
            if (r.guid == guid && !r.released && !r.failed && (!next || r.order < next->order)) next = &r;
        want = next;
    }
}

void KILN_CALL reskin_noop(void*) {}

// Once a second on the tick: a watched mod owner that is gone (owner_revoke when the mod unloads) releases its reskins.
void KILN_CALL reskin_tick(const KilnTickInfo*, void*) {
    static uint32_t n = 0;
    if (!g_reskin_watch.load(std::memory_order_acquire) || ++n % 60 != 0) return;
    std::lock_guard lk(g_mu);
    std::set<kvb::Guid> changed;
    std::map<KilnOwner, bool> alive;
    for (Reskin& r : g_reskins) {
        if (!r.watch || r.released) continue;
        auto it = alive.find(r.watch);
        // post() refuses a revoked owner; for a live one it queues a no-op task, the only probe the API offers without an ABI change.
        if (it == alive.end()) it = alive.emplace(r.watch, g_api->post(r.watch, &reskin_noop, nullptr) != KILN_ERR_REVOKED).first;
        if (it->second) continue;
        r.released = true;
        r.watch = 0;
        changed.insert(r.guid);
        KV_I("vfx: %s unloaded: its material %s no longer claims %s", r.mod.c_str(), r.name.c_str(), kvb::guid_text(r.guid).c_str());
    }
    for (const kvb::Guid& g : changed) reskin_reconcile_locked(g);
}

// Kiln.Blocks compiled a material that replaces `guid` for `mod` under the mod's own owner: watch that owner, and serve it again if an
// earlier unload released it (a reload). Under g_mu.
bool reskin_attach_locked(const std::string& mod, const kvb::Guid& guid, KilnOwner owner) {
    if (!owner || g_api->post(owner, &reskin_noop, nullptr) == KILN_ERR_REVOKED) return false;
    for (Reskin& r : g_reskins) {
        if (r.mod != mod || r.guid != guid) continue;
        if (owner && r.watch != owner)
            KV_I("vfx: %s: material %s's replacement of %s now follows the mod's lifetime%s", mod.c_str(), r.name.c_str(), kvb::guid_text(guid).c_str(),
                 r.failed ? " (the game has no such material here: nothing is replaced)" : "");
        r.watch = owner;
        if (r.released) {
            r.released = false;
            r.failed = false;
            KV_I("vfx: %s: material %s replaces %s again (the mod loaded again)", mod.c_str(), r.name.c_str(), kvb::guid_text(guid).c_str());
        }
        reskin_reconcile_locked(guid);
        if (owner) g_reskin_watch.store(true, std::memory_order_release);
        return !r.failed;
    }
    return false;
}

uint32_t reskin_release_locked(const std::string& mod) {
    uint32_t released = 0;
    std::set<kvb::Guid> changed;
    for (Reskin& r : g_reskins) {
        if (r.mod != mod || r.released) continue;
        r.released = true;
        r.watch = 0;
        changed.insert(r.guid);
        ++released;
        KV_I("vfx: %s unloaded: its material %s no longer claims %s", r.mod.c_str(), r.name.c_str(), kvb::guid_text(r.guid).c_str());
    }
    for (const kvb::Guid& g : changed) reskin_reconcile_locked(g);
    return released;
}

// Serves the result. false: something can't be served, and then no VFX is registered, since a VFX with a missing blob is fatal.
bool serve(const kvb::CookResult& r) {
    for (const kvb::ServedBlob& b : r.blobs) {
        KilnContentHash h{b.hash.size, b.hash.h0, b.hash.h1, b.hash.h2};
        const KilnResult rc = g_assets->blob_override(g_self, &h, b.data.data(), b.data.size());
        if (rc != KILN_OK) {
            KV_E("vfx: blob %s (%s): kiln.assets refused it (%d): no Kiln VFX this session", b.what.c_str(), kvb::content_hash_text(b.hash).c_str(), static_cast<int>(rc));
            return false;
        }
        // Read it back: the game reads through this same map. A shader blob it can't find or that doesn't hash right would be fatal.
        Bytes back(b.data.size());
        size_t got = 0;
        if (g_assets->blob_read(&h, back.data(), back.size(), &got) != KILN_OK || got != b.data.size() || kvb::content_hash(back.data(), got) != b.hash) {
            KV_E("vfx: blob %s: what kiln.assets serves is not what was built: no Kiln VFX this session", b.what.c_str());
            return false;
        }
    }
    for (const kvb::ServedResource& s : r.resources) {
        if (s.type == kvb::kTypeVfx) continue;
        KilnAssetKey k{};
        std::memcpy(k.guid, s.guid.b, 16);
        k.type = s.type;
        // A game material's replacement belongs to its mod, never to kiln.vfx: it goes away when the mod unloads (reskin_tick).
        if (s.override) {
            if (s.mod.empty()) continue;
            std::lock_guard lk(g_mu);
            Reskin r;
            r.mod = s.mod;
            const std::string prefix = s.mod + " material ";
            r.name = s.what.rfind(prefix, 0) == 0 ? s.what.substr(prefix.size()) : s.what;
            r.guid = s.guid;
            r.data = s.data;
            r.order = g_reskins.size();  // the cook lists them in load order
            r.owner = reskin_owner_locked(s.mod);
            if (!r.owner) {
                KV_W("vfx: %s: cannot allocate a reskin owner: the game's material draws", s.mod.c_str());
                continue;
            }
            g_reskins.push_back(std::move(r));
            reskin_reconcile_locked(s.guid);
            continue;
        }
        const KilnResult rc = g_assets->resource_put(g_self, &k, s.data.data(), s.data.size(), KILN_ASSET_PUT_NEW);
        if (rc != KILN_OK) {
            KV_E("vfx: %s: kiln.assets refused it (%d): no Kiln VFX this session", s.what.c_str(), static_cast<int>(rc));
            return false;
        }
    }
    for (const kvb::ServedResource& s : r.resources) {
        if (s.type != kvb::kTypeVfx) continue;
        KilnAssetKey k{};
        std::memcpy(k.guid, s.guid.b, 16);
        k.type = s.type;
        const KilnResult rc = g_assets->resource_put(g_self, &k, s.data.data(), s.data.size(), KILN_ASSET_PUT_NEW);
        if (rc != KILN_OK) KV_E("vfx: %s: kiln.assets refused it (%d): this VFX is not registered", s.what.c_str(), static_cast<int>(rc));
    }
    return true;
}

}  // namespace

void build_on_ready(const KilnApi* api, KilnOwner self) {
    g_api = api;
    g_self = self;
    try {
        g_assets = static_cast<const KilnAssetsApi*>(api->service(KilnStr{KILN_ASSETS_SERVICE, sizeof(KILN_ASSETS_SERVICE) - 1}, 1));
        if (!g_assets || g_assets->header.size < sizeof(KilnAssetsApi)) {
            KV_W("vfx: kiln.assets is not there: no Kiln VFX this session");
            set_status("not checked", "no kiln.assets", nullptr);
            return;
        }
        const std::string diff = check_layouts();
        if (!diff.empty()) {
            KV_E("vfx: this game build changed what Kiln's particle modules were made for: mod particles are off until Kiln is updated; game VFX still "
                 "work. %s",
                 diff.c_str());
        }
        const std::string material_diff = check_material_layouts();
        if (!material_diff.empty()) {
            KV_E("vfx: this game build changed what Kiln's materials are written for: new materials are off until Kiln is updated, and models that name one "
                 "don't draw; the game's own materials still work. %s",
                 material_diff.c_str());
        }
        if (!diff.empty() && !material_diff.empty()) {
            set_status("changed", diff + "; " + material_diff, nullptr);
            return;
        }
        g_game = counts_from_game();
        if (!read_game_materials(to_path(api_path(KILN_PATH_GAME)) / L"enshrouded.kfc", &g_game_materials))
            KV_W("vfx: original material keys could not be read: material replacements are refused this session");

        const std::vector<std::string> skip = skip_list();
        const fs::path pending = to_path(api_path(KILN_PATH_DATA)) / L"vfx" / L"pending";
        std::vector<kvb::ModInput> mods;
        for (const std::string& dir : mod_folders()) {
            kvb::ModInput m;
            if (!read_mod(dir, pending, &m)) continue;
            if (std::find(skip.begin(), skip.end(), m.id) != skip.end()) {
                KV_I("vfx: %s: skipped by kiln/config/vfx.json", m.id.c_str());
                continue;
            }
            mods.push_back(std::move(m));
        }
        kvb::CookOptions opt;
        opt.game = g_game;
        opt.game_has_material = [](const kvb::Guid& g) { return g_game_materials.count(g) != 0; };
        opt.read_file = &read_all;
        opt.cache = disk_cache();
        opt.game_has_blob = game_has_blob;
        opt.skip_vfx = !diff.empty();
        opt.skip_materials = !material_diff.empty();
        shader_switches(&opt);
        const char* fingerprint = diff.empty() && material_diff.empty() ? "ok" : "changed";
        const std::string fingerprint_detail = diff.empty() ? material_diff : diff;
        // progress.json: this runs in on_ready, kiln-core's step "plugins"; the detail says which mod's block is built
        // (kiln.progress v2; nothing with an older core, and kiln-core writes it at most every 250 ms)
        const auto* progress = static_cast<const KilnProgressApi*>(api->service(KilnStr{KILN_PROGRESS_SERVICE, sizeof(KILN_PROGRESS_SERVICE) - 1}, 1));
        if (progress && progress->header.size >= offsetof(KilnProgressApi, step) + sizeof(void*) && progress->step)
            opt.progress = [progress](const std::string& mod, size_t nth, size_t of, const char* kind, const std::string& name, size_t done, size_t total) {
                const std::string which = of > 1 ? " (mod " + std::to_string(nth) + " of " + std::to_string(of) + ")" : "";
                const std::string count = " (" + std::to_string(done + 1) + " of " + std::to_string(total) + ")";
                // a material taken from the last start's build is not "built": the detail says what really happens
                const std::string detail = std::string_view(kind).starts_with("material as")
                                               ? "kiln-vfx: " + mod + which + ": materials as the last start built them: " + name + count
                                               : "kiln-vfx: building the materials and VFX of " + mod + which + ": " + kind + " " + name + count;
                progress->step(KilnStr{"plugins", 7}, KilnStr{nullptr, 0}, KilnStr{detail.data(), detail.size()}, 0, 0);
            };
        std::vector<kvb::MaterialRecord> records;
        opt.record_material = [&records](const kvb::MaterialRecord& rec) { records.push_back(rec); };
        // The carry-over index of the last starts (kvb::MaterialRecord, carry.cpp): a material whose definition and files did not change
        // takes its maps from the texture cache without decoding a texture. Only beside a texture cache.
        const fs::path index_file = to_path(api_path(KILN_PATH_DATA)) / L"vfx" / L"cache" / L"index.json";
        const kvb::CarryHeader header = carry_header(api);
        std::vector<kvb::MaterialRecord> carried;
        std::map<std::pair<std::string, std::string>, size_t> carried_at;
        if (opt.cache.get && !header.game.empty()) {
            std::error_code ec;
            std::string why;
            if (!fs::exists(index_file, ec)) {
                KV_D("vfx: no carry-over index yet (%s): every material is built from its textures", to_utf8(index_file).c_str());
            } else if (!kvb::read_carry_index(read_text(index_file), header, &carried, &why)) {
                KV_I("vfx: the carry-over index is not used (%s): every material is built from its textures, and the index is written again", why.c_str());
            } else {
                for (size_t i = 0; i < carried.size(); ++i) carried_at[{carried[i].mod, carried[i].name}] = i;
                opt.carried_material = [&carried, &carried_at](const std::string& mod, const std::string& name, const std::string& def) -> const kvb::MaterialRecord* {
                    const auto it = carried_at.find({mod, name});
                    return it != carried_at.end() && carried[it->second].definition_text == def ? &carried[it->second] : nullptr;
                };
            }
        }
        const uint64_t t0 = GetTickCount64();
        const kvb::CookResult r = kvb::cook_mods(mods, opt);
        if (r.materials_carried || r.materials_built) {
            KV_I("vfx: %u material(s) taken as the last start built them, %u built from their textures, in %llu ms", r.materials_carried, r.materials_built,
                 static_cast<unsigned long long>(GetTickCount64() - t0));
            if (opt.carried_material)
                for (const std::string& n : r.carry_notes) KV_D("vfx: built again: %s", n.c_str());
        }
        if (opt.cache.get && !header.game.empty()) write_carry_index(index_file, kvb::carry_index_json(header, kvb::carry_merge(records, carried)));
        {
            std::lock_guard lk(g_mu);
            for (kvb::MaterialRecord& rec : records) {
                const auto key = std::make_pair(rec.mod, rec.name);
                g_material_records[key] = std::move(rec);
            }
        }
        for (const std::string& e : r.errors) KV_E("vfx: %s", e.c_str());
        for (const std::string& e : r.refused_mods) KV_E("vfx: %s", e.c_str());
        for (const std::string& w : r.warnings) KV_W("vfx: %s", w.c_str());
        for (const std::string& n : r.notes) KV_I("vfx: %s", n.c_str());  // once, here: Kiln.Blocks' define reads only the warnings
        if (r.blocks.empty() && r.materials.empty()) {
            set_status(fingerprint, fingerprint_detail, &r);
            if (r.mods_with_vfx) KV_W("vfx: no Kiln VFX or materials built from %u mod(s) with such blocks", r.mods_with_vfx);
            return;
        }
        if (!serve(r)) {
            set_status(fingerprint, "kiln.assets refused a part: nothing registered", &r);
            return;
        }
        {
            std::lock_guard lk(g_mu);
            if (!g_reskins.empty()) {
                KilnHandle h = 0;
                if (api->subscribe_tick(self, KILN_TICK_POST, &reskin_tick, nullptr, &h) != KILN_OK)
                    KV_W("vfx: no tick: a reskin stays until the next start even if its mod unloads");
            }
        }
        {
            std::lock_guard lk(g_mu);
            for (const kvb::BuiltBlock& b : r.blocks)
                g_built[b.guid] = Built{b.definition, b.loops ? 0 : static_cast<int64_t>(static_cast<double>(b.stop_after) * 1e9), b.mod + ":" + b.name};
            for (const kvb::BuiltMaterialBlock& m : r.materials) {
                // Replacements share the game material's GUID across mods: they are known by mod and GUID.
                if (m.replaces) g_reskin_defs[{m.mod, m.guid}] = m.definition;
                else g_built[m.guid] = Built{m.definition, 0, m.mod + ":" + m.name};
            }
            for (const kvb::ServedResource& s : r.resources)
                if (s.type == kvb::kTypeVfx)
                    if (auto it = g_built.find(s.guid); it != g_built.end()) {
                        it->second.served = bytes_hash(s.data.data(), s.data.size());
                        it->second.served_size = static_cast<uint32_t>(s.data.size());
                    }
        }
        if (!r.blocks.empty()) g_own_checks.store(600);
        set_status(fingerprint, fingerprint_detail, &r);
        const kvb::Capacity& cap = r.capacity;
        if (!r.materials.empty()) {
            const auto replacing = static_cast<unsigned>(std::count_if(r.materials.begin(), r.materials.end(), [](const kvb::BuiltMaterialBlock& m) { return m.replaces; }));
            KV_I("vfx: built %u Kiln material(s): materials %u+%u of %u, streamed textures %u+%u of %u", r.used_materials, g_game.materials, r.used_materials, cap.materials,
                 g_game.streamed_textures, r.used_streamed_textures, cap.streamed_textures);
            if (replacing) KV_I("vfx: %u material(s) of mods draw in place of the game's own (\"replaces\")", replacing);
        }
        if (r.blocks.empty()) return;
        g_late_done.store(false);
        if (r.mod_modules) KV_I("vfx: %u particle shader(s) of mods passed their checks and are served (kiln/config/vfx.json \"shaders\": false turns them off)", r.mod_modules);
        KV_I("vfx: built %u Kiln VFX in %llu ms: VFX %u+%u of %u, meshes %u+%u of %u (%llu of %llu bytes free), sounds %u+%u of %u, curves %u+%u of %u; fingerprint ok",
             r.used_vfx, static_cast<unsigned long long>(GetTickCount64() - t0), g_game.vfx, r.used_vfx, cap.vfx, g_game.meshes, r.used_meshes, cap.meshes,
             static_cast<unsigned long long>(cap.mesh_bytes - g_game.mesh_bytes - r.used_mesh_bytes), static_cast<unsigned long long>(cap.mesh_bytes), g_game.sounds,
             r.used_sounds, cap.sounds, g_game.curves, r.used_curves, cap.curves);
    } catch (const std::exception& e) {
        KV_E("vfx: building mods' VFX failed (%s): no Kiln VFX this session", e.what());
        set_status("not checked", e.what(), nullptr);
    }
}

// The game resolves a VFX GUID through its own table, filled when it listed the VFX at start. That each of Kiln's resolves to the bytes Kiln
// served is checked once the system has them: a GUID that resolves to other data would play another effect (the game's or another
// mod's) under Kiln's name. Such a VFX is refused like an unknown one, so it shows nothing rather than something else.
void check_own_data() {
    if (g_own_checks.load(std::memory_order_acquire) <= 0) return;
    kvx::Game* game = kvx::core_game();
    if (!game || game->is_sim()) {
        g_own_checks.store(0);
        return;
    }
    std::vector<std::pair<kvb::Guid, Built>> vfx;
    {
        std::lock_guard lk(g_mu);
        for (const auto& [g, b] : g_built)
            if (b.served_size) vfx.emplace_back(g, b);
    }
    if (vfx.empty()) {
        g_own_checks.store(0);
        return;
    }
    if (game->is_registered(nullptr, vfx.front().first.b) != 1) {  // the game hasn't listed them yet
        if (g_own_checks.fetch_sub(1) == 1)
            KV_W("vfx: Kiln's VFX are not in the game's VFX table after 600 updates: whether they resolve to their own data was not checked");
        return;
    }
    g_own_checks.store(0);
    uint32_t own = 0;
    std::string other;
    std::vector<kvb::Guid> foreign;
    std::vector<uint8_t> buf;
    for (const auto& [g, b] : vfx) {
        buf.assign(b.served_size, 0);
        const bool read = game->read_resource(nullptr, g.b, buf.data(), buf.size());
        if (read && bytes_hash(buf.data(), buf.size()) == b.served) {
            ++own;
            continue;
        }
        char text[40];
        kvx::guid_text(g.b, text);
        other += (other.empty() ? "" : ", ") + b.name + " (" + text + (read ? ")" : ", not readable)");
        foreign.push_back(g);
    }
    if (foreign.empty()) {
        KV_I("vfx: %u Kiln VFX resolve to their own data in the game's VFX table", own);
    } else if (!own) {
        // Not one matches: more likely this build keeps resources another way than that every VFX is wrong. Nothing is refused.
        KV_W("vfx: none of Kiln's %zu VFX reads back as the data Kiln served (%s): the check doesn't hold on this game build, nothing refused. Send kiln.log",
             vfx.size(), other.c_str());
    } else {
        {
            std::lock_guard lk(g_mu);
            g_foreign.insert(foreign.begin(), foreign.end());
        }
        g_any_foreign.store(true, std::memory_order_release);
        KV_E("vfx: %zu Kiln VFX resolve to other data in the game's VFX table and would play another effect: %s. They are refused (they show "
             "nothing); %u resolve to their own. Send kiln.log", foreign.size(), other.c_str(), own);
    }
}

void build_on_update() {
    check_own_data();
    if (g_late_done.load(std::memory_order_acquire)) return;
    bool expected = false;
    if (!g_late_done.compare_exchange_strong(expected, true)) return;
    try {
        // The shader binding list lives in vfx/vfx.gpc, a compressed resource, so it can only be read once the game has it resident.
        KilnAssetKey k{};
        const kvb::Guid gpc = kvb::vfx_gpc_guid();
        std::memcpy(k.guid, gpc.b, 16);
        k.type = kvb::kTypeGpc;
        const void* p = nullptr;
        size_t n = 0;
        if (g_assets->resource_data(&k, &p, &n) != KILN_OK || !p) {
            KV_W("vfx: vfx/vfx.gpc is not loaded: the binding list was not checked");
            return;
        }
        uint32_t hash = 0, count = 0;
        const bool found = kvb::binding_hash_of_gpc(static_cast<const uint8_t*>(p), n, &hash, &count);
        if (found && hash == kvb::kBindingHash) {
            KV_I("vfx: the particle bindings match (%u)", count);
            return;
        }
        g_off.store(true);
        KV_E("vfx: this game build changed the particle bindings (%s %08x, made for %08x): mod particles are off until Kiln is updated; game VFX still work",
             found ? "hash" : "no list,", hash, kvb::kBindingHash);
    } catch (const std::exception& e) {
        KV_E("vfx: the late check failed (%s)", e.what());
    }
}

bool build_refuses(const uint8_t guid[16]) {
    if (!g_off.load(std::memory_order_acquire) && !g_any_foreign.load(std::memory_order_acquire)) return false;
    kvb::Guid g;
    std::memcpy(g.b, guid, 16);
    std::lock_guard lk(g_mu);
    if (g_foreign.count(g)) return true;
    return g_off.load(std::memory_order_acquire) && g_built.count(g) != 0;
}

int64_t build_one_shot_ns(const uint8_t guid[16]) {
    kvb::Guid g;
    std::memcpy(g.b, guid, 16);
    std::lock_guard lk(g_mu);
    const auto it = g_built.find(g);
    return it == g_built.end() ? 0 : it->second.one_shot_ns;
}

KilnResult build_define_json(KilnOwner owner, KilnStr json, char* report, size_t cap, size_t* out_len) {
    KilnResult rc = KILN_OK;
    std::string text;
    try {
        const ojson doc = kvb::parse_relaxed(std::string(json.ptr, json.len));
        ojson result = ojson::object();
        // {"format": 1, "mod": id, "release": true}: Kiln.Blocks saw the mod unload. Its reskins go, as when its owner is revoked: this covers
        // a mod whose blocks are compiled under an owner of Kiln's (data files only, or code that never defines a block).
        if (doc.is_object() && doc.contains("release") && doc["release"].is_boolean() && doc["release"].get<bool>() && doc.contains("mod") &&
            doc["mod"].is_string()) {
            const std::string mod = doc["mod"].get<std::string>();
            uint32_t released = 0;
            {
                std::lock_guard lk(g_mu);
                released = reskin_release_locked(mod);
            }
            result["ok"] = true;
            result["released"] = released;
            text = result.dump();
            if (out_len) *out_len = text.size();
            if (report && cap) {
                const size_t n = text.size() < cap - 1 ? text.size() : cap - 1;
                std::memcpy(report, text.data(), n);
                report[n] = 0;
                if (cap <= text.size()) rc = KILN_ERR_BUFFER_TOO_SMALL;
            }
            return rc;
        }
        const auto section_ok = [&](const char* key) { return !doc.contains(key) || doc[key].is_object(); };
        if (doc.is_discarded() || !doc.is_object() || !doc.contains("mod") || !doc["mod"].is_string() || !doc.contains("dir") || !doc["dir"].is_string() ||
            !section_ok("vfx") || !section_ok("materials") || (!doc.contains("vfx") && !doc.contains("materials"))) {
            rc = KILN_ERR_INVALID_ARGUMENT;
            result["ok"] = false;
            result["errors"] = ojson::array({"expected {\"format\": 1, \"mod\": id, \"dir\": folder, \"vfx\": {...}, \"materials\": {...}}"});
        } else {
            kvb::ModInput m;
            m.id = doc["mod"].get<std::string>();
            m.dir = doc["dir"].get<std::string>();
            m.side = "client";  // the manifest's rule was checked when the mod loaded
            ojson only = ojson::object();
            only["format"] = 1;
            if (doc.contains("vfx")) only["vfx"] = doc["vfx"];
            if (doc.contains("materials")) only["materials"] = doc["materials"];
            m.documents.emplace_back("code", only.dump());
            // A particle in this definition may name a material of the mod's data files, or one already kept from code.
            if (doc.contains("vfx")) {
                kvb::ModInput all;
                const fs::path pending_root = to_path(api_path(KILN_PATH_DATA)) / L"vfx" / L"pending";
                if (read_mod(m.dir, pending_root, &all))
                    for (auto& [file, text] : all.documents) m.lookup_documents.push_back(std::move(text));
                m.advanced = all.advanced;  // read from the manifest even when the mod has no data files
            }
            kvb::CookOptions opt;
            shader_switches(&opt);
            opt.game = g_game;
            opt.game_has_material = [](const kvb::Guid& g) { return g_game_materials.count(g) != 0; };
            opt.read_file = &read_all;
            opt.cache = disk_cache();
            opt.game_has_blob = game_has_blob;
            // A material the boot build built from the same definition and the same file bytes is not built again (kvb::MaterialRecord):
            // the report is the same, without decoding its textures. The copy is taken under g_mu, the cook runs without it.
            opt.known_material = [copy = kvb::MaterialRecord{}](const std::string& mod, const std::string& name, uint64_t definition) mutable -> const kvb::MaterialRecord* {
                std::lock_guard lk(g_mu);
                const auto it = g_material_records.find({mod, name});
                if (it == g_material_records.end() || it->second.definition != definition) return nullptr;
                copy = it->second;
                return &copy;
            };
            const kvb::CookResult r = kvb::cook_mods({m}, opt);
            if (r.materials_reused) KV_D("vfx: %s: %u material(s) as the boot build made them (same definition and files)", m.id.c_str(), r.materials_reused);
            result = kvb::report_json(r);
            if (!r.errors.empty() || !r.refused_mods.empty()) rc = KILN_ERR_INVALID_ARGUMENT;
            const fs::path pending = to_path(api_path(KILN_PATH_DATA)) / L"vfx" / L"pending" / to_path(m.id);
            std::lock_guard lk(g_mu);
            const auto keep = [&](const char* section, const std::string& name, const std::string& file) {
                ojson one = ojson::object();
                one["format"] = 1;
                one[section] = ojson::object();
                one[section][name] = doc[section][name];
                std::error_code ec;
                fs::create_directories(pending, ec);
                std::ofstream f(pending / to_path(file), std::ios::binary);
                const std::string s = one.dump(1);
                f.write(s.data(), static_cast<std::streamsize>(s.size()));
                if (!f) KV_W("vfx: %s:%s: can't keep it for the next start", m.id.c_str(), name.c_str());
            };
            for (size_t i = 0; i < r.blocks.size(); ++i) {
                const kvb::BuiltBlock& b = r.blocks[i];
                const auto it = g_built.find(b.guid);
                if (it != g_built.end() && it->second.definition == b.definition) {
                    result["blocks"][i]["status"] = "registered";
                    continue;
                }
                // The game enumerates VFX once: this one is there from the next start.
                result["blocks"][i]["status"] = "pending";
                keep("vfx", b.name, b.name + ".json");
            }
            for (size_t i = 0; i < r.materials.size(); ++i) {
                const kvb::BuiltMaterialBlock& b = r.materials[i];
                const auto it = g_built.find(b.guid);
                const auto rd = g_reskin_defs.find({m.id, b.guid});
                if (b.replaces ? rd != g_reskin_defs.end() && rd->second == b.definition : it != g_built.end() && it->second.definition == b.definition) {
                    result["materials"][i]["status"] = "registered";
                    // The reskin is now this mod's: unloading the mod takes it off (reskin_tick).
                    if (b.replaces && !reskin_attach_locked(m.id, b.guid, owner)) {
                        result["materials"][i]["status"] = "refused";
                        result["errors"].push_back("materials." + b.name + ".replaces: material owner has been revoked or the replacement is unavailable");
                        rc = KILN_ERR_INVALID_ARGUMENT;
                    }
                    continue;
                }
                // The renderer lists the textures of every material once at boot, and a texture it didn't list is fatal: this one is there from the next start.
                result["materials"][i]["status"] = "pending";
                keep("materials", b.name, "material-" + b.name + ".json");
            }
        }
        text = result.dump();
    } catch (const std::exception& e) {
        rc = KILN_ERR_INTERNAL;
        text = std::string("{\"ok\":false,\"errors\":[\"") + e.what() + "\"]}";
    }
    if (out_len) *out_len = text.size();
    if (report && cap) {
        const size_t n = text.size() < cap - 1 ? text.size() : cap - 1;
        std::memcpy(report, text.data(), n);
        report[n] = 0;
        if (cap <= text.size() && rc == KILN_OK) rc = KILN_ERR_BUFFER_TOO_SMALL;
    }
    return rc;
}

KilnResult build_status_json(char* buf, size_t cap, size_t* out_len) {
    std::lock_guard lk(g_mu);
    if (out_len) *out_len = g_status.size();
    if (!buf || cap <= g_status.size()) return KILN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(buf, g_status.data(), g_status.size());
    buf[g_status.size()] = 0;
    return KILN_OK;
}

}  // namespace kvx
