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
#include <string>
#include <vector>

#include "boot.h"
#include "cook/vfxcook.h"
#include "core.h"
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
};
std::map<kvb::Guid, Built> g_built;
std::string g_status = "{}";
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
    auto add_folder = [&](const fs::path& folder, const char* prefix) {
        std::vector<fs::path> files;
        std::error_code e2;
        for (auto it = fs::directory_iterator(folder, e2); !e2 && it != fs::directory_iterator(); it.increment(e2))
            if (it->path().extension() == L".json") files.push_back(it->path());
        std::sort(files.begin(), files.end());
        for (const fs::path& f : files) m->documents.emplace_back(std::string(prefix) + "/" + to_utf8(f.filename()), read_text(f));
    };
    add_folder(d / L"blocks", "blocks");
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
std::string check_layouts() {
    const auto* base = reinterpret_cast<const uint8_t*>(GetModuleHandleW(nullptr));
    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
    const kvb::ImageView view{base, nt->OptionalHeader.SizeOfImage, reinterpret_cast<uint64_t>(base)};
    return kvb::compare_layouts(kvb::read_layouts(view));
}

bool game_has_blob(const kvb::ContentHash& hash) { return kvb::kfc_has_content(to_utf8(to_path(api_path(KILN_PATH_GAME)) / L"enshrouded.kfc"), hash); }

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

void set_status(const char* state, const std::string& detail, const kvb::CookResult* r) {
    ojson j = r ? kvb::report_json(*r) : ojson::object();
    j["fingerprint"] = state;
    if (!detail.empty()) j["detail"] = detail;
    std::lock_guard lk(g_mu);
    g_status = j.dump();
}

KilnStr as_str(const std::string& s) { return KilnStr{s.data(), s.size()}; }

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
            set_status("changed", diff, nullptr);
            return;
        }
        g_game = counts_from_game();

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
        opt.read_file = &read_all;
        opt.cache = disk_cache();
        opt.game_has_blob = game_has_blob;
        const uint64_t t0 = GetTickCount64();
        const kvb::CookResult r = kvb::cook_mods(mods, opt);
        for (const std::string& e : r.errors) KV_E("vfx: %s", e.c_str());
        for (const std::string& e : r.refused_mods) KV_E("vfx: %s", e.c_str());
        for (const std::string& w : r.warnings) KV_W("vfx: %s", w.c_str());
        if (r.blocks.empty()) {
            set_status("ok", "", &r);
            if (r.mods_with_vfx) KV_W("vfx: no Kiln VFX built from %u mod(s) with VFX blocks", r.mods_with_vfx);
            return;
        }
        if (!serve(r)) {
            set_status("ok", "kiln.assets refused a part: nothing registered", &r);
            return;
        }
        {
            std::lock_guard lk(g_mu);
            for (const kvb::BuiltBlock& b : r.blocks) g_built[b.guid] = Built{b.definition, b.loops ? 0 : static_cast<int64_t>(static_cast<double>(b.stop_after) * 1e9)};
        }
        set_status("ok", "", &r);
        g_late_done.store(false);
        const kvb::Capacity& cap = r.capacity;
        KV_I("vfx: built %u Kiln VFX in %llu ms: VFX %u+%u of %u, meshes %u+%u of %u (%llu of %llu bytes free), sounds %u+%u of %u, curves %u+%u of %u; fingerprint ok",
             r.used_vfx, static_cast<unsigned long long>(GetTickCount64() - t0), g_game.vfx, r.used_vfx, cap.vfx, g_game.meshes, r.used_meshes, cap.meshes,
             static_cast<unsigned long long>(cap.mesh_bytes - g_game.mesh_bytes - r.used_mesh_bytes), static_cast<unsigned long long>(cap.mesh_bytes), g_game.sounds,
             r.used_sounds, cap.sounds, g_game.curves, r.used_curves, cap.curves);
    } catch (const std::exception& e) {
        KV_E("vfx: building mods' VFX failed (%s): no Kiln VFX this session", e.what());
        set_status("not checked", e.what(), nullptr);
    }
}

void build_on_update() {
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
    if (!g_off.load(std::memory_order_acquire)) return false;
    kvb::Guid g;
    std::memcpy(g.b, guid, 16);
    std::lock_guard lk(g_mu);
    return g_built.count(g) != 0;
}

int64_t build_one_shot_ns(const uint8_t guid[16]) {
    kvb::Guid g;
    std::memcpy(g.b, guid, 16);
    std::lock_guard lk(g_mu);
    const auto it = g_built.find(g);
    return it == g_built.end() ? 0 : it->second.one_shot_ns;
}

KilnResult build_define_json(KilnOwner, KilnStr json, char* report, size_t cap, size_t* out_len) {
    KilnResult rc = KILN_OK;
    std::string text;
    try {
        const ojson doc = kvb::parse_relaxed(std::string(json.ptr, json.len));
        ojson result = ojson::object();
        if (doc.is_discarded() || !doc.is_object() || !doc.contains("mod") || !doc["mod"].is_string() || !doc.contains("dir") || !doc["dir"].is_string() ||
            !doc.contains("vfx") || !doc["vfx"].is_object()) {
            rc = KILN_ERR_INVALID_ARGUMENT;
            result["ok"] = false;
            result["errors"] = ojson::array({"expected {\"format\": 1, \"mod\": id, \"dir\": folder, \"vfx\": {...}}"});
        } else {
            kvb::ModInput m;
            m.id = doc["mod"].get<std::string>();
            m.dir = doc["dir"].get<std::string>();
            m.side = "client";  // the manifest's rule was checked when the mod loaded
            ojson only = ojson::object();
            only["format"] = 1;
            only["vfx"] = doc["vfx"];
            m.documents.emplace_back("code", only.dump());
            kvb::CookOptions opt;
            opt.game = g_game;
            opt.read_file = &read_all;
            opt.cache = disk_cache();
            opt.game_has_blob = game_has_blob;
            const kvb::CookResult r = kvb::cook_mods({m}, opt);
            result = kvb::report_json(r);
            if (!r.errors.empty() || !r.refused_mods.empty()) rc = KILN_ERR_INVALID_ARGUMENT;
            const fs::path pending = to_path(api_path(KILN_PATH_DATA)) / L"vfx" / L"pending" / to_path(m.id);
            std::lock_guard lk(g_mu);
            for (size_t i = 0; i < r.blocks.size(); ++i) {
                const kvb::BuiltBlock& b = r.blocks[i];
                const auto it = g_built.find(b.guid);
                if (it != g_built.end() && it->second.definition == b.definition) {
                    result["blocks"][i]["status"] = "registered";
                    continue;
                }
                // The game enumerates VFX once: this one is there from the next start.
                result["blocks"][i]["status"] = "pending";
                ojson one = ojson::object();
                one["format"] = 1;
                one["vfx"] = ojson::object();
                one["vfx"][b.name] = doc["vfx"][b.name];
                std::error_code ec;
                fs::create_directories(pending, ec);
                std::ofstream f(pending / to_path(b.name + ".json"), std::ios::binary);
                const std::string s = one.dump(1);
                f.write(s.data(), static_cast<std::streamsize>(s.size()));
                if (!f) KV_W("vfx: %s:%s: can't keep it for the next start", m.id.c_str(), b.name.c_str());
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
