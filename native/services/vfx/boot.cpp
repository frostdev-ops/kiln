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
#include "pending.h"
#include "pending_cleanup.h"
#include "kiln_boot_collector.hpp"
#include "ledger.h"
#include "resource_read.h"
#include "services/kiln_assets.h"
#include "services/kiln_assets_vfx.h"
#include "services/kiln_capacity_util.hpp"
#include "services/kiln_caps.h"
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
KilnHandle g_reskin_tick = 0;  // one subscription for the process, including prepare retries
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
// `refused`: each file or folder left out, "<prefix>/<file>: <why>" (a boot generation's required mod is refused for the first).
void add_limited(const fs::path& mod, const std::wstring& root, const fs::path& folder, const char* prefix,
                 const kvx_generated::Limits& lim, kvb::ModInput* m, std::vector<std::string>* refused = nullptr) {
    const auto left_out = [&](const std::string& what, const std::string& why) {
        if (refused) refused->push_back(what + ": not read: " + why);
    };
    std::error_code ec;
    if (!fs::is_directory(folder, ec)) return;
    {
        OwnedHandle dh(open_for_read(folder, true));
        if (!dh.ok()) {
            left_out(std::string(prefix) + "/", "it cannot be opened");
            return;
        }
        if (!kvx_generated::path_inside(root, final_path(dh.h)) || reparse_on_the_way(mod, folder)) {
            KV_W("vfx: %s: %s/ is a link or reparse point, or leads outside the mod's folder: not read", m->id.c_str(), prefix);
            left_out(std::string(prefix) + "/", "a link or reparse point, or it leads outside the mod's folder");
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
        left_out(std::string(prefix) + "/", "more than " + std::to_string(lim.files) + " JSON files or " + std::to_string(lim.entries) + " entries");
        return;
    }
    std::sort(files.begin(), files.end());
    size_t n = 0;
    uint64_t total = 0;
    for (const fs::path& f : files) {
        const std::string name = to_utf8(f.filename());
        const std::string what = std::string(prefix) + "/" + name;
        OwnedHandle fh(open_for_read(f, false));
        if (!fh.ok()) {
            KV_W("vfx: %s: %s: not read: it cannot be opened", m->id.c_str(), what.c_str());
            left_out(what, "it cannot be opened");
            continue;
        }
        if (!kvx_generated::path_inside(root, final_path(fh.h)) || reparse_on_the_way(mod, f)) {
            KV_W("vfx: %s: %s/%s: not read: a link or reparse point, or it leads outside the mod's folder", m->id.c_str(), prefix, name.c_str());
            left_out(what, "a link or reparse point, or it leads outside the mod's folder");
            continue;
        }
        LARGE_INTEGER size{};
        if (!GetFileSizeEx(fh.h, &size) || size.QuadPart < 0) {
            left_out(what, "its size cannot be read");
            continue;
        }
        const uint64_t bytes = static_cast<uint64_t>(size.QuadPart);
        if (const char* why = kvx_generated::refusal(lim, n, bytes, total)) {
            KV_W("vfx: %s: %s/%s: not read: %s (%llu bytes; limits: %zu files, %llu bytes each, %llu in all)", m->id.c_str(), prefix,
                 name.c_str(), why, static_cast<unsigned long long>(bytes), lim.files, static_cast<unsigned long long>(lim.file_bytes),
                 static_cast<unsigned long long>(lim.total_bytes));
            left_out(what, why);
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
            left_out(what, "it changed while it was read");
            continue;
        }
        if (!kvx_generated::depth_ok(text, lim.depth)) {
            KV_W("vfx: %s: %s/%s: not read: nested deeper than %d", m->id.c_str(), prefix, name.c_str(), lim.depth);
            left_out(what, "nested deeper than " + std::to_string(lim.depth));
            continue;
        }
        ++n;
        total += bytes;
        m->documents.emplace_back(std::string(prefix) + "/" + name, std::move(text));
    }
}

// Pending replay uses the cooker's existing final-handle containment/reparse checks, with its own quota.
void add_pending(const fs::path& root, kvb::ModInput* m, kvx_pending::Budget& budget) {
    const auto reject = [&](const fs::path& path, const std::string& why) {
        static std::mutex mu;
        static std::set<fs::path> warned;
        bool say = false;
        { std::lock_guard lk(mu); if (warned.size() < kvx_pending::kEntries) say = warned.insert(path).second; }
        if (say) KV_W("vfx: %s: pending/%s: not read: %s", m->id.c_str(), to_utf8(path.lexically_relative(root)).c_str(), why.c_str());
    };
    if (!ledger::valid_mod(m->id)) { reject(root, "invalid mod id"); return; }
    const fs::path folder = root / to_path(m->id);
    std::error_code ec;
    const auto folder_status = fs::symlink_status(folder,ec);
    if (ec == std::errc::no_such_file_or_directory || folder_status.type() == fs::file_type::not_found) return;
    if (ec || fs::is_symlink(folder_status)) { reject(folder,"linked or uninspectable pending folder"); return; }
    const DWORD attrs = GetFileAttributesW(root.c_str());
    if (attrs == INVALID_FILE_ATTRIBUTES || (attrs & FILE_ATTRIBUTE_REPARSE_POINT) || reparse_on_the_way(root, folder)) {
        reject(folder,"link, reparse point or uninspectable pending folder"); return;
    }
    OwnedHandle rh(open_for_read(root,true));
    const std::wstring final = rh.ok() ? final_path(rh.h) : std::wstring();
    if(final.empty()){reject(root,"pending root cannot be opened");return;}
    auto files = kvx_pending::read(root,folder,budget,[&](const fs::path& path,size_t max,std::string* text,std::string* why) {
        OwnedHandle fh(CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,
                                  FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT,nullptr));
        BY_HANDLE_FILE_INFORMATION info{};
        LARGE_INTEGER size{};
        if(!fh.ok() || !GetFileInformationByHandle(fh.h,&info) || (info.dwFileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT|FILE_ATTRIBUTE_DIRECTORY)) ||
           GetFileType(fh.h)!=FILE_TYPE_DISK || !kvx_generated::path_inside(final,final_path(fh.h)) || reparse_on_the_way(root,path)) {
            *why="link, reparse point, non-file or path outside pending root";return false;
        }
        if(!GetFileSizeEx(fh.h,&size) || size.QuadPart<0 || uint64_t(size.QuadPart)>max){*why="file exceeds pending file/total byte bound, or size is unreadable";return false;}
        text->resize(static_cast<size_t>(size.QuadPart));DWORD got=0,more=0;char extra;
        if(!ReadFile(fh.h,text->data(),static_cast<DWORD>(text->size()),&got,nullptr) || got!=text->size() ||
           !ReadFile(fh.h,&extra,1,&more,nullptr) || more || !kvx_generated::depth_ok(*text,64)) {
            *why="file changed, could not be read completely, or exceeds JSON depth 64";return false;
        }
        return true;
    },reject);
    for(auto& [name,text]:files) m->documents.emplace_back("pending/"+name,std::move(text));
}

// One mod's folder -> its input. false: nothing to do here.
// `boot`: a boot generation's prepare (chapter 08): a mod it excludes is left out; the mod's OnBoot documents (a blocks file's form) come after
// its data files and baked blocks and before what Kiln kept from code at an earlier start.
bool read_mod(const std::string& dir, const fs::path& pending_root, kvb::ModInput* m, const kiln::boot::Input* boot = nullptr, std::vector<BuildFailure>* failures = nullptr, kvx_pending::Budget* pending_budget = nullptr) {
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
    m->advanced = kvb::manifest_native(j);  // "native": true, or its older name "advanced": true
    if (boot && boot->out(m->id)) {
        KV_I("vfx: %s is out of the boot generation: its VFX and materials are not built", m->id.c_str());
        return false;
    }
    // the mod's own folder: its data files, then what Kiln.Sdk baked from its code (before what Kiln kept from code at an
    // earlier start, so a block of the same name in both is the mod's: the cook keeps the first), each with limits
    std::wstring root;
    {
        OwnedHandle rh(open_for_read(d, true));
        if (rh.ok()) root = final_path(rh.h);
    }
    // a required participant prepares strictly (kiln_boot_collector.hpp, entry_error): a file of its own left out refuses it
    std::vector<std::string> refused;
    if (root.empty()) {
        KV_W("vfx: %s: the mod's folder cannot be opened to check where its files are: its blocks are not read", m->id.c_str());
        if (failures) failures->push_back({m->id, "vfx", "blocks", "kiln.vfx: the mod's folder cannot be opened: its blocks are not read"});
        kiln::boot::entry_error(boot, m->id, "vfx", "blocks", "kiln.vfx: the mod's folder cannot be opened: its blocks are not read");
    } else {
        add_limited(d, root, d / L"blocks", "blocks", kvx_generated::kBlocks, m, &refused);
        add_limited(d, root, d / L"blocks" / L"generated", "blocks/generated", kvx_generated::kGenerated, m, &refused);
    }
    if (failures)
        for (const auto& what : refused)
            failures->push_back({m->id, "vfx", what.substr(0, what.find(": not read")), "kiln.vfx: " + what});
    if (!refused.empty()) {
        const std::string what = refused.front();
        kiln::boot::entry_error(boot, m->id, "vfx", what.substr(0, what.find(": not read")), "kiln.vfx: " + what);
    }
    if (boot)
        for (const auto& b : boot->docs("vfx", m->id)) {
            if (!b.document.is_object())
                throw kiln::boot::Refusal(m->id, "vfx", b.key, "kiln.vfx: a boot document is not a blocks file (a JSON object)");
            m->documents.emplace_back("boot/" + b.key, b.document.dump());
        }
    kvx_pending::Budget local;
    add_pending(pending_root,m,pending_budget ? *pending_budget : local);
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

// `vfx_counted`: the game's own VfxResources were counted in its archive (false: the known number stands in for them).
kvb::GameCounts counts_from_game(bool* vfx_counted) {
    *vfx_counted = false;
    const fs::path game = to_path(api_path(KILN_PATH_GAME));
    std::map<uint32_t, kvb::KfcBundle> bundles;
    std::string why;
    if (!kvb::read_kfc_bundles(to_utf8(game / L"enshrouded.kfc"), &bundles, &why)) {
        KV_W("vfx: %s: the budget uses the known numbers for the game's own VFX", why.c_str());
        return {};
    }
    const auto vfx = bundles.find(kvb::kTypeVfx);
    *vfx_counted = vfx != bundles.end() && vfx->second.count != 0;
    return kvb::game_counts_from(bundles);
}

// The VFX resources the game's VFX system holds, as its code says: the count limit VfxSystemCreate reserves its GUID map with
// (sigdb caps/vfx.map_limit_request, client.d/dev29-17a-vfx-cap.json). 0: not resolved on this build (a dedicated server, a
// game update); the cook's own number (kvb::Capacity) admits then, as before. Read at the start, before the game runs.
uint32_t capacity_in_code() {
    int64_t v = 0;
    static constexpr char kName[] = "caps/vfx.map_limit_request";
    if (g_api->constant(KilnStr{kName, sizeof kName - 1}, &v) != KILN_OK || v <= 0 || v > (1 << 20)) return 0;
    return static_cast<uint32_t>(v);
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

// The index file, read only up to kvb::kCarryIndexMaxBytes: its size is checked before anything is read, and the read is of that size.
enum class IndexFile { ok, none, too_large, unreadable };
IndexFile read_index_file(const fs::path& file, std::string* out) {
    std::error_code ec;
    if (!fs::exists(file, ec)) return IndexFile::none;
    const auto size = fs::file_size(file, ec);
    if (ec) return IndexFile::unreadable;
    if (size > kvb::kCarryIndexMaxBytes) return IndexFile::too_large;
    std::ifstream f(file, std::ios::binary);
    if (!f) return IndexFile::unreadable;
    out->resize(static_cast<size_t>(size));
    f.read(out->data(), static_cast<std::streamsize>(size));
    out->resize(static_cast<size_t>(f.gcount()));  // a file that shrank meanwhile: what was there (the reader decides)
    return IndexFile::ok;
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
bool serve(const kvb::CookResult& r, const kiln::boot::Input* boot = nullptr, std::vector<BuildFailure>* failures = nullptr) {
    const auto* vfx = g_api->service ? static_cast<const KilnAssetsVfxApi*>(
        g_api->service(KilnStr{KILN_ASSETS_VFX_SERVICE, sizeof(KILN_ASSETS_VFX_SERVICE) - 1}, 1)) : nullptr;
    if (vfx && vfx->header.size < sizeof(KilnAssetsVfxApi)) vfx = nullptr;
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
                if (boot && boot->strict(s.mod)) return false;
                continue;
            }
            g_reskins.push_back(std::move(r));
            reskin_reconcile_locked(s.guid);
            if (boot && boot->strict(s.mod) && g_reskins.back().failed) return false;
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
        const KilnResult rc = vfx && vfx->resource_put
            ? vfx->resource_put(g_self, KilnStr{s.mod.data(), s.mod.size()}, &k, s.data.data(), s.data.size(), KILN_ASSET_PUT_NEW)
            : g_assets->resource_put(g_self, &k, s.data.data(), s.data.size(), KILN_ASSET_PUT_NEW);
        if (rc != KILN_OK) {
            KV_E("vfx: %s: kiln.assets refused it (%d): this VFX is not registered", s.what.c_str(), static_cast<int>(rc));
            if (failures && !s.mod.empty())
                failures->push_back({s.mod, "vfx", s.what, "kiln.assets refused the VFX resource " + s.what + "; see kiln.log"});
            if (boot) return false;  // ordinary on_ready keeps its per-VFX fallback
        }
    }
    return true;
}

}  // namespace

// The first required participant of a boot generation whose own blocks or OnBoot documents hold a "vfx" (vfx) or "materials"
// (materials) section: {mod, file}, or empty. A Refusal of read_mod (a file of its left out) goes up as it is.
std::pair<std::string, std::string> required_vfx_content(const KilnApi* api, const kiln::boot::Input& boot, bool vfx, bool materials) {
    if (!g_api) g_api = api;
    if (boot.required.empty()) return {};
    const fs::path pending = to_path(api_path(KILN_PATH_DATA)) / L"vfx" / L"pending";
    kvx_pending::Budget pending_budget;
    for (const std::string& dir : mod_folders()) {
        kvb::ModInput m;
        if (!read_mod(dir, pending, &m, &boot, nullptr, &pending_budget) || !boot.strict(m.id)) continue;
        for (const auto& [file, text] : m.documents) {
            if (file.rfind("pending/", 0) == 0) continue;  // Kiln's own cache of its code
            const ojson doc = kvb::parse_relaxed(text);
            if (!doc.is_object()) continue;
            for (const auto& [key, want] : {std::pair{"vfx", vfx}, std::pair{"materials", materials}})
                if (want && doc.contains(key) && doc[key].is_object() && !doc[key].empty()) return {m.id, file};
        }
    }
    return {};
}

namespace {
// ---- the resource budget (package 17a S2; ledger.h) ----------------------------------------------------------------------------

// The capability rows of the appended members. vfx.budget: the game's table can be read on this build; blocks.vfx_registration:
// also its resources' bytes can be compared and Kiln's VFX are built this session (`off` empty). Reported with the table, before
// the mods load (chapter 17 I1: a table not read yet is the answers' readiness, not a missing capability), and again when the
// start's build or the guard turns them off.
struct Row {
    std::string off;  // why it is off; empty: on
    uint32_t reason = KILN_CAP_REASON_NONE;
};
Row g_budget_row{"kiln.vfx has not started", KILN_CAP_REASON_SERVICE_MISSING}, g_registration_row{"kiln.vfx has not started", KILN_CAP_REASON_SERVICE_MISSING};
std::mutex g_rows_mu;  // the two rows
bool g_rows_started = false;

void report_row(const char* name, const Row& row) {
    const auto* caps = static_cast<const KilnCapsApi*>(g_api->service(KilnStr{KILN_CAPS_SERVICE, sizeof(KILN_CAPS_SERVICE) - 1}, 1));
    if (!caps || caps->header.size < offsetof(KilnCapsApi, report) + sizeof(void*) || !caps->report) return;
    static constexpr char kOn[] = "kiln.vfx v1, 0.2.0-dev.29";
    const std::string text = row.off.empty() ? std::string(kOn) : row.off;
    caps->report(g_self, KilnStr{name, std::strlen(name)}, row.off.empty() ? KILN_CAP_AVAILABLE : KILN_CAP_UNAVAILABLE,
                 row.off.empty() ? KILN_CAP_REASON_NONE : row.reason, KilnStr{text.data(), text.size()});
}

// Lowers a row that is on (the first reason stays) and reports both; the members follow (ledger::set_available).
void lower_rows(bool budget, bool registration, const std::string& why, uint32_t reason) {
    Row b, r;
    {
        std::lock_guard lk(g_rows_mu);
        if (!g_rows_started) return;
        bool changed = false;
        if (budget && g_budget_row.off.empty()) g_budget_row = {why, reason}, changed = true;
        if ((budget || registration) && g_registration_row.off.empty()) g_registration_row = {why, reason}, changed = true;
        if (!changed) return;
        b = g_budget_row, r = g_registration_row;
    }
    ledger::set_available(b.off.empty(), r.off.empty());
    KV_W("vfx: %s%s off: %s", budget ? "vfx.budget and " : "", "blocks.vfx_registration", why.c_str());
    report_row("vfx.budget", b);
    report_row("blocks.vfx_registration", r);
}

void rows_after_build(const std::string& off) {
    if (!off.empty()) lower_rows(false, true, off, KILN_CAP_REASON_FAULTED);
}

// What became, at this start, of each definition a mod stored at an earlier one (a pending file with a "registration"): the
// receipt is that definition's only when its id derives from this file's block (a file edited by hand is not it).
std::vector<ledger::Stored> stored_receipts(const std::vector<kvb::ModInput>& mods, const kvb::CookResult& r) {
    std::vector<ledger::Stored> out;
    for (const kvb::ModInput& m : mods)
        for (const auto& [file, text] : m.documents) {
            if (file.rfind("pending/", 0) != 0) continue;
            const ojson doc = kvb::parse_relaxed(text);
            if (!doc.is_object() || !doc.contains("registration") || !doc["registration"].is_string() || !doc.contains("vfx") ||
                !doc["vfx"].is_object() || doc["vfx"].size() != 1 || !doc["vfx"].begin().value().is_object())
                continue;
            ledger::Stored st;
            if (!ledger::uuid_parse(doc["registration"].get<std::string>(), &st.registration)) continue;
            st.mod = m.id;
            st.name = doc["vfx"].begin().key();
            const ojson& block = doc["vfx"].begin().value();
            const std::string id_name = block.contains("was") && block["was"].is_string() ? block["was"].get<std::string>() : st.name;
            const kvb::Guid guid = kvb::block_id(m.id, "vfx", id_name);
            std::memcpy(st.guid.data(), guid.b, 16);
            if (ledger::registration_id(m.id, st.guid, nlohmann::json::parse(block.dump()).dump()) != st.registration) continue;
            const std::string written = block.dump();
            st.definition = bytes_hash(reinterpret_cast<const uint8_t*>(written.data()), written.size());  // as the builder hashes a block
            const auto built = std::find_if(r.blocks.begin(), r.blocks.end(), [&](const kvb::BuiltBlock& b) { return b.mod == m.id && b.name == st.name; });
            if (built != r.blocks.end() && built->guid == guid && built->definition == st.definition) {
                st.built = true;
            } else if (built != r.blocks.end()) {
                st.code = KILN_DEFINE_SUPERSEDED;
                st.why = "the mod's own block " + st.name + " (a data file or a baked one comes first) is what this start built";
            } else {
                st.code = KILN_DEFINE_GAME_REFUSED;
                st.why = "this start did not build it (kiln.log says why)";
                if (const auto e = r.mod_errors.find(m.id); e != r.mod_errors.end()) {
                    for (const std::string& line : e->second) {
                        if (line.find("over the game's limits") != std::string::npos) {
                            st.code = KILN_DEFINE_CAPACITY_EXCEEDED, st.why = line;
                            break;
                        }
                        if (line.find("vfx." + st.name) != std::string::npos) st.why = line;
                    }
                }
            }
            out.push_back(std::move(st));
        }
    return out;
}

ledger::Id id_of(const kvb::Guid& g) {
    ledger::Id id{};
    std::memcpy(id.data(), g.b, 16);
    return id;
}

}  // namespace

void build_on_ready(const KilnApi* api, KilnOwner self, const kiln::boot::Input* boot, std::vector<BuildFailure>* failures) {
    g_api = api;
    g_self = self;
    // a boot generation: a required participant whose VFX or materials this start cannot build at all is refused (kiln.boot stops the
    // start); another mod's are off with the lines below, as they always were
    const auto refuse_required = [&](bool vfx, bool materials, const std::string& why) {
        if (!boot) {
            // The on_ready file build (no boot input yet): every mod's file with such a section is noted, as required_vfx_content finds
            // them; the collector's prepare refuses the ones of a required participant (held.h).
            if (failures) {
                const fs::path pending = to_path(api_path(KILN_PATH_DATA)) / L"vfx" / L"pending";
                kvx_pending::Budget pending_budget;
                for (const auto& dir : mod_folders()) {
                    kvb::ModInput m;
                    if (!read_mod(dir, pending, &m, nullptr, nullptr, &pending_budget)) continue;
                    for (const auto& [file, text] : m.documents) {
                        if (file.rfind("pending/", 0) == 0) continue;  // Kiln's own cache of its code
                        const ojson doc = kvb::parse_relaxed(text);
                        if (!doc.is_object()) continue;
                        for (const auto& [key, want] : {std::pair{"vfx", vfx}, std::pair{"materials", materials}})
                            if (want && doc.contains(key) && doc[key].is_object() && !doc[key].empty()) {
                                failures->push_back({m.id, "vfx", file, "kiln.vfx: " + file + ": " + why});
                                break;
                            }
                    }
                }
            }
            return;
        }
        std::pair<std::string, std::string> hit;
        try {
            hit = required_vfx_content(api, *boot, vfx, materials);
        } catch (const kiln::boot::Refusal&) {
            throw;
        } catch (const std::exception&) {
            return;  // what cannot even be read is said by the lines of the build
        }
        if (!hit.first.empty())
            throw kiln::boot::Refusal(hit.first, "vfx", hit.second, "kiln.vfx: " + hit.second + ": its " + (vfx && materials ? "VFX and materials" : vfx ? "VFX" : "materials") +
                                                                       " cannot be built in this start: " + why);
    };
    // The resource budget's view of this start (ledger.h): what was built and served, what each definition stored at an
    // earlier start became. Published on every way out of this function, also when nothing was built (`off` says why
    // Kiln's VFX are not built at all this session: then blocks.vfx_registration is off).
    struct LedgerBoot {
        ledger::Boot b;
        std::string off;
        bool served = false;  // this start's VFX were served (else none of them is registered)
        ~LedgerBoot() {
            try {
                if (!served) {
                    b.built.clear();
                    for (ledger::Stored& st : b.stored)
                        if (st.built)
                            st.built = false, st.code = KILN_DEFINE_GAME_REFUSED,
                            st.why = off.empty() ? "kiln.assets refused part of this start's VFX: none of them is registered" : off;
                }
                ledger::boot(std::move(b));
                rows_after_build(off);
            } catch (...) {
            }
        }
    } lb;
    {
        const std::string data = api_path(KILN_PATH_DATA);
        ledger::set_pending_root(data.empty() ? fs::path() : to_path(data) / L"vfx" / L"pending");
        const uint32_t in_code = capacity_in_code();
        lb.b.admission = in_code ? in_code : kvb::Capacity{}.vfx;
        if (!in_code) KV_I("vfx: the game's VFX count limit is not known from its code on this build: the build admits against %u", lb.b.admission);
    }
    try {
        g_assets = static_cast<const KilnAssetsApi*>(api->service(KilnStr{KILN_ASSETS_SERVICE, sizeof(KILN_ASSETS_SERVICE) - 1}, 1));
        if (!g_assets || g_assets->header.size < sizeof(KilnAssetsApi)) {
            KV_W("vfx: kiln.assets is not there: no Kiln VFX this session");
            set_status("not checked", "no kiln.assets", nullptr);
            lb.off = "kiln.assets is not there: no Kiln VFX this session";
            refuse_required(true, true, "kiln.assets is not there, so no Kiln VFX or material is built");
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
        if (!diff.empty()) lb.off = "this game build changed what Kiln's particle modules are written for: mod VFX are off until Kiln is updated";
        if (!diff.empty() && !material_diff.empty()) {
            set_status("changed", diff + "; " + material_diff, nullptr);
            refuse_required(true, true, "this game build changed what Kiln's particle modules and materials are written for (kiln.log says what)");
            return;
        }
        if (!diff.empty()) refuse_required(true, false, "this game build changed what Kiln's particle modules are written for (kiln.log says what)");
        if (!material_diff.empty()) refuse_required(false, true, "this game build changed what Kiln's materials are written for (kiln.log says what)");
        g_game = counts_from_game(&lb.b.game_counted);
        std::string vfx_admission_error;
        // In an ordinary start the assets manifests have been read by now. In a boot generation
        // their collector may register more afterward. The asset store enforces the same limit
        // at each resource_put, including calls made after this snapshot.
        if (api->side() == KILN_SIDE_CLIENT && core_game() && !core_game()->is_sim()) {
            const auto* budget_api = static_cast<const KilnAssetsVfxApi*>(
                api->service(KilnStr{KILN_ASSETS_VFX_SERVICE, sizeof(KILN_ASSETS_VFX_SERVICE) - 1}, 1));
            KilnAssetsVfxBudget budget{};
            budget.size = sizeof budget;
            if (!budget_api || budget_api->header.size < sizeof(KilnAssetsVfxApi) || !budget_api->budget ||
                budget_api->budget(&budget) != KILN_OK || !budget.capacity ||
                budget.game > budget.capacity || budget.reserved > budget.capacity - budget.game) {
                vfx_admission_error = "the shared VFX resource count is unavailable; new VFX cannot be admitted";
                lb.off = vfx_admission_error;
                KV_W("vfx: %s", vfx_admission_error.c_str());
                refuse_required(true, false, vfx_admission_error);
            } else {
                g_game.vfx = budget.game;
                lb.b.game_counted = true;
                lb.b.reserved = budget.reserved;
                lb.b.admission = budget.capacity;
            }
        }
        lb.b.game_vfx = g_game.vfx;
        if (!read_game_materials(to_path(api_path(KILN_PATH_GAME)) / L"enshrouded.kfc", &g_game_materials))
            KV_W("vfx: original material keys could not be read: material replacements are refused this session");

        const std::vector<std::string> skip = skip_list();
        const fs::path pending = to_path(api_path(KILN_PATH_DATA)) / L"vfx" / L"pending";
        std::vector<kvb::ModInput> mods;
        kvx_pending::Budget pending_budget;
        pending_budget.skipped = skip;
        for (const std::string& dir : mod_folders()) {
            kvb::ModInput m;
            if (!read_mod(dir, pending, &m, boot, failures, &pending_budget)) continue;
            if (std::find(skip.begin(), skip.end(), m.id) != skip.end()) {
                KV_I("vfx: %s: skipped by kiln/config/vfx.json", m.id.c_str());
                continue;
            }
            mods.push_back(std::move(m));
        }
        kvb::CookOptions opt;
        opt.game = g_game;
        opt.game.vfx += lb.b.reserved;
        opt.capacity.vfx = lb.b.admission;  // the game's own count limit where its code says it (the budget reads the live one)
        opt.game_has_material = [](const kvb::Guid& g) { return g_game_materials.count(g) != 0; };
        opt.read_file = &read_all;
        opt.cache = disk_cache();
        opt.game_has_blob = game_has_blob;
        opt.skip_vfx = !diff.empty() || !vfx_admission_error.empty();
        opt.skip_materials = !material_diff.empty();
        shader_switches(&opt);
        const char* fingerprint = diff.empty() && material_diff.empty() ? "ok" : "changed";
        const std::string fingerprint_detail = diff.empty() ? material_diff : diff;
        // progress.json: this runs in on_ready, kiln-core's step "plugins"; the detail says which mod's block is built
        // (kiln.progress v2; nothing with an older core, and kiln-core writes it at most every 250 ms)
        const auto* progress = static_cast<const KilnProgressApi*>(api->service(KilnStr{KILN_PROGRESS_SERVICE, sizeof(KILN_PROGRESS_SERVICE) - 1}, 1));
        if (progress && progress->header.size >= offsetof(KilnProgressApi, step) + sizeof(void*) && progress->step)
            opt.progress = [progress](const std::string& mod, size_t nth, size_t of, const char* kind, const std::string& name, size_t done, size_t total) {
                try {  // a progress line is worth nothing next to the build: out of memory here costs the line only
                const std::string which = of > 1 ? " (mod " + std::to_string(nth) + " of " + std::to_string(of) + ")" : "";
                const std::string count = " (" + std::to_string(done + 1) + " of " + std::to_string(total) + ")";
                // a material taken from the last start's build is not "built": the detail says what really happens
                const std::string detail = std::string_view(kind).starts_with("material as")
                                               ? "kiln-vfx: " + mod + which + ": materials as the last start built them: " + name + count
                                               : "kiln-vfx: building the materials and VFX of " + mod + which + ": " + kind + " " + name + count;
                progress->step(KilnStr{"plugins", 7}, KilnStr{nullptr, 0}, KilnStr{detail.data(), detail.size()}, 0, 0);
                } catch (...) {
                }
            };
        std::vector<kvb::MaterialRecord> records;
        bool records_whole = true;  // a record lost (out of memory): the index is not rewritten from this start
        opt.record_material = [&records, &records_whole](const kvb::MaterialRecord& rec) {
            try {
                records.push_back(rec);
            } catch (...) {
                records_whole = false;
            }
        };
        // The carry-over index of the last starts (kvb::MaterialRecord, carry.cpp): a material whose definition and files did not change
        // takes its maps from the texture cache without decoding a texture. Only beside a texture cache. The index is a hint: whatever goes
        // wrong in reading, parsing, using or writing it costs the index and nothing else (never this function's catch below, which would
        // give up every material and VFX).
        std::vector<kvb::MaterialRecord> carried;
        std::map<std::pair<std::string, std::string>, size_t> carried_at;
        fs::path index_file;
        kvb::CarryHeader header;
        bool use_index = false;
        try {
            index_file = to_path(api_path(KILN_PATH_DATA)) / L"vfx" / L"cache" / L"index.json";
            header = carry_header(api);
            use_index = opt.cache.get && !header.game.empty();
            std::string text, why;
            if (use_index) {
                switch (read_index_file(index_file, &text)) {
                    case IndexFile::none:
                        KV_D("vfx: no carry-over index yet (%s): every material is built from its textures", to_utf8(index_file).c_str());
                        break;
                    case IndexFile::too_large:
                        KV_I("vfx: the carry-over index is not used (larger than %zu MiB): every material is built from its textures, and the index is written again",
                             kvb::kCarryIndexMaxBytes >> 20);
                        break;
                    case IndexFile::unreadable:
                        KV_I("vfx: the carry-over index is not used (it cannot be read): every material is built from its textures, and the index is written again");
                        break;
                    case IndexFile::ok:
                        if (!kvb::read_carry_index(text, header, &carried, &why)) {
                            KV_I("vfx: the carry-over index is not used (%s): every material is built from its textures, and the index is written again", why.c_str());
                        } else {
                            for (size_t i = 0; i < carried.size(); ++i) carried_at[{carried[i].mod, carried[i].name}] = i;
                            opt.carried_material = [&carried, &carried_at](const std::string& mod, const std::string& name,
                                                                            const std::string& def) -> const kvb::MaterialRecord* {
                                const auto it = carried_at.find({mod, name});
                                return it != carried_at.end() && carried[it->second].definition_text == def ? &carried[it->second] : nullptr;
                            };
                        }
                        break;
                }
            }
        } catch (...) {
            carried.clear();
            carried_at.clear();
            opt.carried_material = nullptr;
            KV_I("vfx: the carry-over index is not used (it could not be read): every material is built from its textures");
        }
        const uint64_t t0 = GetTickCount64();
        const kvb::CookResult r = kvb::cook_mods(mods, opt);
        // a required participant whose VFX or materials did not all build is refused before anything is served (the prepare's
        // catch takes back what this build did); another mod's errors are logged below as they always were
        record_cook_failures(failures, r.mod_errors);
        if (boot)
            for (const auto& [id, errs] : r.mod_errors)
                if (!errs.empty() && boot->strict(id)) {
                    const std::string& e = errs.front();
                    const size_t a = e.find(": "), b = a == std::string::npos ? a : e.find(": ", a + 2);  // "<mod>: <file>: ..."
                    const std::string key = b == std::string::npos ? std::string() : e.substr(a + 2, b - a - 2);
                    throw kiln::boot::Refusal(id, "vfx", key.find('/') != std::string::npos ? key : std::string(),
                                              "kiln.vfx: " + e + (errs.size() > 1 ? " (and " + std::to_string(errs.size() - 1) + " more, kiln.log)" : ""));
                }
        if (r.materials_carried || r.materials_built) {
            KV_I("vfx: %u material(s) taken as the last start built them, %u built from their textures, in %llu ms", r.materials_carried, r.materials_built,
                 static_cast<unsigned long long>(GetTickCount64() - t0));
            if (opt.carried_material)
                for (const std::string& n : r.carry_notes) KV_D("vfx: built again: %s", n.c_str());
        }
        if (use_index && records_whole) {
            try {
                write_carry_index(index_file, kvb::carry_index_json(header, kvb::carry_merge(records, carried)));
            } catch (...) {
                KV_W("vfx: the carry-over index could not be written: the next start builds every material from its textures");
            }
        }
        {
            std::lock_guard lk(g_mu);
            for (kvb::MaterialRecord& rec : records) {
                const auto key = std::make_pair(rec.mod, rec.name);
                g_material_records[key] = std::move(rec);
            }
        }
        lb.b.stored = stored_receipts(mods, r);
        for (const std::string& e : r.errors) KV_E("vfx: %s", e.c_str());
        for (const std::string& e : r.refused_mods) KV_E("vfx: %s", e.c_str());
        for (const std::string& w : r.warnings) KV_W("vfx: %s", w.c_str());
        for (const std::string& n : r.notes) KV_I("vfx: %s", n.c_str());  // once, here: Kiln.Blocks' define reads only the warnings
        if (r.blocks.empty() && r.materials.empty()) {
            set_status(fingerprint, fingerprint_detail, &r);
            if (r.mods_with_vfx) KV_W("vfx: no Kiln VFX or materials built from %u mod(s) with such blocks", r.mods_with_vfx);
            return;
        }
        if (!serve(r, boot, failures)) {
            build_forget();
            set_status(fingerprint, "kiln.assets refused a part: nothing registered", &r);
            refuse_required(true, true, "kiln.assets refused part of the built VFX or materials");
            return;
        }
        {
            std::lock_guard lk(g_mu);
            if (!g_reskins.empty() && !g_reskin_tick) {
                if (api->subscribe_tick(self, KILN_TICK_POST, &reskin_tick, nullptr, &g_reskin_tick) != KILN_OK)
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
        lb.served = true;
        for (const kvb::BuiltBlock& b : r.blocks) {
            ledger::BuiltVfx v;
            std::memcpy(v.guid.data(), b.guid.b, 16);
            v.mod = b.mod, v.name = b.name, v.definition = b.definition;
            lb.b.built.push_back(std::move(v));
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
        KV_I("vfx: built %u Kiln VFX in %llu ms: VFX %u game + %u external + %u cooked of %u, meshes %u+%u of %u (%llu of %llu bytes free), sounds %u+%u of %u, curves %u+%u of %u; fingerprint ok",
             r.used_vfx, static_cast<unsigned long long>(GetTickCount64() - t0), g_game.vfx, lb.b.reserved, r.used_vfx, cap.vfx, g_game.meshes, r.used_meshes, cap.meshes,
             static_cast<unsigned long long>(cap.mesh_bytes - g_game.mesh_bytes - r.used_mesh_bytes), static_cast<unsigned long long>(cap.mesh_bytes), g_game.sounds,
             r.used_sounds, cap.sounds, g_game.curves, r.used_curves, cap.curves);
    } catch (const kiln::boot::Refusal&) {
        throw;  // a boot document: the boot generation's prepare refuses the mod
    } catch (const std::exception& e) {
        build_forget();
        lb.served = false;
        lb.b.built.clear();
        lb.off = std::string("building mods' VFX failed (") + e.what() + "): no Kiln VFX this session";
        KV_E("vfx: building mods' VFX failed (%s): no Kiln VFX this session", e.what());
        set_status("not checked", e.what(), nullptr);
        refuse_required(true, true, std::string("building mods' VFX failed (") + e.what() + ")");
    }
}

// A boot generation's prepared build that is not published (chapter 08): what kiln.assets serves for it goes, and nothing is known built.
void build_forget() {
    std::lock_guard lk(g_mu);
    if (g_assets) {
        g_assets->revert_owner(g_self);
        for (const Reskin& r : g_reskins)
            if (r.owner) g_assets->revert_owner(r.owner);
    }
    g_built.clear();
    g_reskins.clear();
    g_reskin_defs.clear();
    g_material_records.clear();
    g_foreign.clear();
    g_own_checks.store(0);
    g_reskin_watch.store(false);
    g_late_done.store(true);
    g_off.store(false);
    g_status = "{}";
    ledger::forget();
    KV_I("vfx: the boot generation's prepared VFX and materials are taken back");
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
        if (b.served_size > kMaxVerifiedResourceBytes) continue;
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

// The resource budget's reading of the game's VFX table (ledger::observe), on this update thread, the one place kiln.vfx reads the
// game's memory for it: the count limit, count and state every 30 updates, and once the state is 2 what each of Kiln's VFX is
// there (in the map, with the bytes Kiln served). The membership check is a read of the map (Game::is_registered), never
// vfx/Resolve, which would acquire the resource. Readers of the budget copy what this publishes.
namespace {
std::atomic<uint32_t> g_observe_n{0};
std::mutex g_observe_mu;
void* g_observe_controller = nullptr;
std::vector<std::pair<ledger::Id, uint8_t>> g_verdicts;  // update thread only
bool g_verdicts_done = false;                            // update thread only
bool g_guard_said = false;                               // update thread only

void observe_registry(void* fxc) {
    std::lock_guard observation(g_observe_mu);
    if (fxc != g_observe_controller) {
        g_observe_controller = fxc;
        g_observe_n = 0;
        g_verdicts_done = false;
        ledger::observe({});
    }
    if (g_observe_n.fetch_add(1, std::memory_order_relaxed) % 30 != 0) return;
    kvx::Game* game = kvx::core_game();
    if (!game) { ledger::observe({}); return; }
    if (!game->has_guard()) {
        // the guard's map check failed or a read faulted: the map is not what the signatures promised, so nothing is read
        ledger::observe({});
        if (!g_guard_said) {
            g_guard_said = true;
            lower_rows(true, true, "the guard is off: the game's VFX map does not read as the signatures promised (kiln.log says why)",
                       KILN_CAP_REASON_FAULTED);
        }
        return;
    }
    Registry reg;
    if (!game->registry(fxc, &reg) || reg.init_state != 2) {
        ledger::observe({});
        return;
    }
    {
        std::vector<std::pair<kvb::Guid, Built>> vfx;
        {
            std::lock_guard lk(g_mu);
            for (const auto& [g, b] : g_built)
                if (b.served_size) vfx.emplace_back(g, b);
        }
        std::vector<std::pair<ledger::Id, uint8_t>> out;
        uint32_t own = 0, other = 0;
        std::vector<uint8_t> buf;
        for (const auto& [g, b] : vfx) {
            const int in_map = game->is_registered(fxc, g.b);
            if (in_map < 0) { ledger::observe({}); return; }  // not ready after all
            uint8_t v = ledger::kMissing;
            if (in_map == 1) {
                if (b.served_size > kMaxVerifiedResourceBytes) {
                    out.emplace_back(id_of(g), ledger::kUnchecked);
                    continue;
                }
                buf.assign(b.served_size, 0);
                v = !game->read_resource(fxc, g.b, buf.data(), buf.size()) ? ledger::kUnchecked
                    : bytes_hash(buf.data(), buf.size()) == b.served             ? ledger::kOwn
                                                                                 : ledger::kOther;
            }
            own += v == ledger::kOwn;
            other += v == ledger::kOther;
            out.emplace_back(id_of(g), v);
        }
        // as check_own_data: when not one matches, the comparison does not hold on this build, so none is proven either way
        if (!own && other)
            for (auto& [id, v] : out)
                if (v == ledger::kOther) v = ledger::kUnchecked;
        g_verdicts = std::move(out);
        if (!g_verdicts_done) KV_I("vfx: the game's VFX table: %llu of %u resources (state %u); %u of Kiln's %zu VFX in it with Kiln's bytes",
             static_cast<unsigned long long>(reg.count), reg.limit, reg.init_state, own, g_verdicts.size());
        g_verdicts_done = true;
    }
    ledger::Seen seen;
    seen.limit = reg.limit;
    seen.count = reg.count;
    seen.init_state = reg.init_state;
    seen.kiln = g_verdicts;
    ledger::observe(seen);
}
}  // namespace

void build_world_ended() {
    std::lock_guard observation(g_observe_mu);
    g_observe_controller = nullptr;
    g_observe_n = 0;
    g_verdicts_done = false;
    g_verdicts.clear();
    ledger::observe({});
}

void build_on_update(void* fxc) {
    check_own_data();
    observe_registry(fxc);
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

namespace {
// The builder on one definition from code (define_json, define_vfx_result): `lookup`: a particle may name a material of the
// mod's data files or one already kept from code, so those are read for it.
kvb::CookResult cook_code(kvb::ModInput m, bool lookup) {
    if (lookup) {
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
    return kvb::cook_mods({std::move(m)}, opt);
}
}  // namespace

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
            // its VFX stay in the game's table for the process: the budget counts them reserved until a block of the mod compiles
            const uint32_t kept_for_mod = ledger::release(mod);
            result["ok"] = true;
            result["released"] = released;
            result["vfxKept"] = kept_for_mod;
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
            const kvb::CookResult r = cook_code(m, doc.contains("vfx"));
            if (r.materials_reused) KV_D("vfx: %s: %u material(s) as the boot build made them (same definition and files)", m.id.c_str(), r.materials_reused);
            result = kvb::report_json(r);
            if (!r.errors.empty() || !r.refused_mods.empty()) rc = KILN_ERR_INVALID_ARGUMENT;
            const fs::path pending = to_path(api_path(KILN_PATH_DATA)) / L"vfx" / L"pending" / to_path(m.id);
            std::unique_lock lk(g_mu);
            // a definition kept for the next start: written whole or not at all (kiln_capacity_util.hpp write_atomic); a receipt whose
            // stored definition the file held is superseded by another content (ledger::kept)
            const auto keep = [&](const char* section, const std::string& name, const std::string& file, uint64_t definition) {
                ojson one = ojson::object();
                one["format"] = 1;
                one[section] = ojson::object();
                one[section][name] = doc[section][name];
                // The observer also takes g_mu. Storage must not hold it, even on the legacy path.
                const fs::path path = pending / to_path(file);
                const std::string bytes = one.dump(1);
                if (bytes.size() > kvx_pending::kFileBytes) {
                    KV_W("vfx: %s:%s: can't keep it for the next start: exceeds 65536-byte pending file bound",m.id.c_str(),name.c_str());
                    return;
                }
                lk.unlock();
                std::unique_lock storage(kvx::pending_io_mutex());
                const bool wrote = kiln::capacity::write_atomic(path, bytes);
                lk.lock();
                if (!wrote) {
                    KV_W("vfx: %s:%s: can't keep it for the next start", m.id.c_str(), name.c_str());
                    return;
                }
                if (std::strcmp(section, "vfx") == 0) ledger::kept(m.id, name, definition);
            };
            if (rc == KILN_OK && (!r.blocks.empty() || !r.materials.empty())) ledger::activate(m.id);  // a block of the mod compiles: it is loaded
            for (size_t i = 0; i < r.blocks.size(); ++i) {
                const kvb::BuiltBlock& b = r.blocks[i];
                const auto it = g_built.find(b.guid);
                if (it != g_built.end() && it->second.definition == b.definition) {
                    result["blocks"][i]["status"] = "registered";
                    continue;
                }
                // The game enumerates VFX once: this one is there from the next start.
                result["blocks"][i]["status"] = "pending";
                keep("vfx", b.name, b.name + ".json", b.definition);
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
                keep("materials", b.name, "material-" + b.name + ".json", b.definition);
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

// ---- the resource budget's C members (kiln_vfx.h, since 0.2.0-dev.29; package 17a S2) -----------------------------------------

void budget_start(const KilnApi* api, KilnOwner self) {
    if (!g_api) g_api = api;
    if (!g_self) g_self = self;
    Game* game = core_game();
    Row b, r;
    {
        std::lock_guard lk(g_rows_mu);
        g_rows_started = true;
        if (!game) g_budget_row = {"kiln.vfx has no game layer here", KILN_CAP_REASON_SERVICE_MISSING};
        else if (const char* why = game->registry_missing()) g_budget_row = {why, KILN_CAP_REASON_SYMBOL_MISSING};
        else g_budget_row = {};
        if (!g_budget_row.off.empty()) g_registration_row = g_budget_row;
        else if (const char* why = game->bytes_missing()) g_registration_row = {why, KILN_CAP_REASON_SYMBOL_MISSING};
        else g_registration_row = {};
        b = g_budget_row, r = g_registration_row;
    }
    ledger::set_available(b.off.empty(), r.off.empty());
    report_row("vfx.budget", b);
    report_row("blocks.vfx_registration", r);
}

KilnResult build_resource_budget(KilnCapacityInfo* out) {
    if (!out || out->size < sizeof(KilnCapacityInfo)) return KILN_ERR_INVALID_ARGUMENT;
    return ledger::budget(out);
}

namespace {
KilnResult answer(KilnResult rc, const std::string& rep, char* report, size_t cap, size_t* out_len) {
    const size_t n = rep.size() < cap ? rep.size() : cap - 1;  // a report is under kReportMax, and cap is at least that
    std::memcpy(report, rep.data(), n);
    report[n] = 0;
    if (out_len) *out_len = rep.size();
    return rc;
}
}  // namespace

KilnResult build_define_vfx_result(KilnOwner owner, KilnStr name, KilnStr json, uint32_t timing, KilnDefinitionReply* out, char* report, size_t cap,
                                   size_t* out_len) {
    if (out_len) *out_len = ledger::kReportMax;
    if (!report || cap < ledger::kReportMax) return KILN_ERR_BUFFER_TOO_SMALL;  // before anything is read or changed
    std::string rep;
    KilnResult rc = KILN_OK;
    const auto bad = [&](const std::string& why) {
        rep = ledger::report_json(why, {});
        rc = KILN_ERR_INVALID_ARGUMENT;
    };
    try {
        const std::string block_name = name.ptr ? std::string(name.ptr, name.len) : std::string();
        const ojson doc = json.ptr ? kvb::parse_relaxed(std::string(json.ptr, json.len)) : ojson();
        const bool shape = doc.is_object() && doc.contains("mod") && doc["mod"].is_string() && doc.contains("dir") && doc["dir"].is_string() &&
                           doc.contains("block") && doc["block"].is_object() &&
                           (!doc.contains("format") || (doc["format"].is_number_integer() && doc["format"].get<int>() == 1));
        if (!out || out->size < sizeof(KilnDefinitionReply)) {
            bad("the answer struct is missing or smaller than KilnDefinitionReply");
        } else if (timing != KILN_DEFINE_REQUIRE_REGISTERED && timing != KILN_DEFINE_ALLOW_NEXT_START) {
            bad("timing " + std::to_string(timing) + " is not REQUIRE_REGISTERED (0) or ALLOW_NEXT_START (1)");
        } else if (!ledger::valid_name(block_name)) {
            bad("\"" + block_name + "\" is not a block name (letters, digits, '-', '_', '.'; at most 128)");
        } else if (!shape) {
            bad("expected {\"format\": 1, \"mod\": id, \"dir\": folder, \"block\": {...one VFX block...}}");
        } else if (!ledger::valid_mod(doc["mod"].get<std::string>())) {
            bad("\"" + doc["mod"].get<std::string>() + "\" is not a mod id");
        } else if (!ledger::registration_available()) {
            rc = KILN_ERR_UNAVAILABLE;
            rep = ledger::report_json("blocks.vfx_registration is off in this process (Capabilities.Why says why)", {});
        } else {
            kvb::ModInput m;
            m.id = doc["mod"].get<std::string>();
            m.dir = doc["dir"].get<std::string>();
            m.side = "client";  // the manifest's rule was checked when the mod loaded
            ojson only = ojson::object();
            only["format"] = 1;
            only["vfx"] = ojson::object();
            only["vfx"][block_name] = doc["block"];
            m.documents.emplace_back("code", only.dump());
            // the same validation and builder as a blocks file; a block it refuses is a bad argument, as for define_json
            const kvb::CookResult r = cook_code(m, true);
            const auto built = std::find_if(r.blocks.begin(), r.blocks.end(), [&](const kvb::BuiltBlock& b) { return b.name == block_name; });
            if (!r.errors.empty()) bad(r.errors.front());
            else if (!r.refused_mods.empty()) bad(r.refused_mods.front());
            else if (built == r.blocks.end()) bad("kiln.vfx did not build it and did not say why");
            else {
                ledger::Request q;
                q.mod = m.id;
                q.name = block_name;
                q.guid = id_of(built->guid);
                q.definition = built->definition;
                q.block = doc["block"].dump();  // in its own key order: the builder hashes this text, a pending file stores it
                q.canonical = nlohmann::json::parse(q.block).dump();
                q.timing = timing;
                // post() refuses a revoked owner; for a live one it queues a no-op task (the probe reskins use)
                q.owner_live = owner != 0 && g_api && g_api->post(owner, &reskin_noop, nullptr) != KILN_ERR_REVOKED;
                q.warnings = r.warnings;
                rc = ledger::define(q, out, &rep);
                if (rc == KILN_OK)
                    KV_I("vfx: %s: vfx %s defined with an answer: %s", m.id.c_str(), block_name.c_str(),
                         out->disposition == KILN_DEFINITION_REGISTERED          ? "registered"
                         : out->disposition == KILN_DEFINITION_ACCEPTED_NEXT_START ? "accepted for the next start"
                         : out->disposition == KILN_DEFINITION_RETAINED          ? "retained"
                                                                                 : "refused");
            }
        }
    } catch (const std::exception& e) {
        rc = KILN_ERR_INTERNAL;
        rep = ledger::report_json(e.what(), {});
    }
    return answer(rc, rep, report, cap, out_len);
}

KilnResult build_vfx_registration(KilnOwner, const uint8_t registration_id[16], KilnDefinitionReply* out, char* report, size_t cap, size_t* out_len) {
    if (out_len) *out_len = ledger::kReportMax;
    if (!report || cap < ledger::kReportMax) return KILN_ERR_BUFFER_TOO_SMALL;
    std::string rep;
    KilnResult rc = KILN_OK;
    try {
        rc = ledger::receipt(registration_id, out, &rep);
    } catch (const std::exception& e) {
        rc = KILN_ERR_INTERNAL;
        rep = ledger::report_json(e.what(), {});
    }
    return answer(rc, rep, report, cap, out_len);
}

}  // namespace kvx
