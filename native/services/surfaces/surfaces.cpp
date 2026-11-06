// SPDX-License-Identifier: MIT
// kiln.surfaces: the plugin. cook/ does the work over bytes; this file finds the moment (a kiln.worldtables table writer), reads the game's
// tables through kiln.assets, cooks with a disk cache, serves blobs and tables, and reports. native/core/abi/services/kiln_surfaces.h,
// docs/kiln/services/surfaces.md.
//
// Everything is served or nothing is: a problem leaves the game's tables untouched, with a line in kiln.log. New materials then draw the shader's
// fallback (a flat stone brick), which is safe: the renderer bounds the slot index by the table's count.
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cstdarg>
#include <cstddef>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <string>
#include <vector>

#include "cook/surfacecook.h"
#include "kiln_api.h"
#include "services/kiln_assets.h"
#include "services/kiln_surfaces.h"
#include "services/kiln_vfx.h"
#include "services/kiln_worldtables.h"

namespace ksf {
namespace {

namespace fs = std::filesystem;

const KilnApi* g_api = nullptr;
KilnOwner g_self = 0;
const KilnAssetsApi* g_assets = nullptr;
const KilnWorldTablesApi* g_wt = nullptr;
std::atomic<int32_t> g_state{KILN_SURFACES_OFF};
std::mutex g_mu;
std::string g_status = "{\"state\":\"off\"}";

void logf(int level, const char* fmt, ...) {
    char buf[2048];
    va_list ap;
    va_start(ap, fmt);
    const int n = std::vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    if (n <= 0 || !g_api) return;
    g_api->log(g_self, level, KilnStr{buf, static_cast<size_t>(n < static_cast<int>(sizeof(buf)) ? n : sizeof(buf) - 1)});
}
#define SF_I(...) logf(KILN_LOG_INFO, __VA_ARGS__)
#define SF_W(...) logf(KILN_LOG_WARN, __VA_ARGS__)
#define SF_E(...) logf(KILN_LOG_ERROR, __VA_ARGS__)

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
std::string str(const KilnStr& s) { return s.ptr && s.len ? std::string(s.ptr, s.len) : std::string(); }
bool read_file(const fs::path& p, Bytes* out) {
    std::ifstream f(p, std::ios::binary);
    if (!f) return false;
    out->assign(std::istreambuf_iterator<char>(f), {});
    return true;
}
fs::path data_dir() { return to_path(api_path(KILN_PATH_DATA)) / L"surfaces"; }

void write_atomic(const fs::path& path, const std::string& text) {
    std::error_code ec;
    fs::create_directories(path.parent_path(), ec);
    const fs::path tmp = fs::path(path).concat(L".tmp");
    {
        std::ofstream f(tmp, std::ios::binary);
        if (!f) return;
        f.write(text.data(), static_cast<std::streamsize>(text.size()));
    }
    if (!MoveFileExW(tmp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING)) fs::remove(tmp, ec);
}

const char* state_name(int32_t s) {
    switch (s) {
        case KILN_SURFACES_WAITING: return "waiting";
        case KILN_SURFACES_NO_TABLES: return "no tables";
        case KILN_SURFACES_UNTOUCHED: return "untouched";
        case KILN_SURFACES_WRITTEN: return "written";
        default: return "off";
    }
}
void set_status(int32_t state, ojson j) {
    g_state.store(state);
    j["state"] = state_name(state);
    std::lock_guard lock(g_mu);
    g_status = j.dump(1);
    write_atomic(data_dir() / L"status.json", g_status);
}
void go_off(const std::string& why) {
    SF_E("surfaces: %s. The game's surface tables are untouched: new building materials draw the fallback stone", why.c_str());
    set_status(KILN_SURFACES_OFF, {{"why", why}});
}

// kiln/data/surfaces/cache/<key>.tex: {ContentHash of the chain, the chain}. An entry that doesn't check out is rebuilt.
kvb::TextureCache disk_cache() {
    kvb::TextureCache c;
    const fs::path dir = data_dir() / L"cache";
    c.get = [dir](const std::string& key, Bytes* out) {
        Bytes raw;
        if (!read_file(dir / to_path(key + ".tex"), &raw) || raw.size() < 16) return false;
        ContentHash want;
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
            const ContentHash h = kvb::content_hash(chain.data(), chain.size());
            f.write(reinterpret_cast<const char*>(&h), 16);
            f.write(reinterpret_cast<const char*>(chain.data()), static_cast<std::streamsize>(chain.size()));
        }
        fs::rename(tmp, dir / to_path(key + ".tex"), ec);
        if (ec) fs::remove(tmp, ec);
    };
    return c;
}

kvb::ImageView own_image() {
    const auto* base = reinterpret_cast<const uint8_t*>(GetModuleHandleW(nullptr));
    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
    return kvb::ImageView{base, nt->OptionalHeader.SizeOfImage, reinterpret_cast<uint64_t>(base)};
}

// One resource of `type`: its key and the bytes the game gets now.
bool table_bytes(uint32_t type, KilnAssetKey* key, Bytes* out) {
    size_t n = 0;
    g_assets->resource_list(type, nullptr, 0, &n);
    if (n != 1) return false;
    if (g_assets->resource_list(type, key, 1, &n) != KILN_OK) return false;
    const void* p = nullptr;
    size_t len = 0;
    if (g_assets->resource_data(key, &p, &len) == KILN_OK && p) {
        out->assign(static_cast<const uint8_t*>(p), static_cast<const uint8_t*>(p) + len);
        return true;
    }
    size_t need = 0;
    g_assets->resource_read(key, nullptr, 0, &need);
    if (!need) return false;
    out->resize(need);
    return g_assets->resource_read(key, out->data(), out->size(), &need) == KILN_OK && need == out->size();
}

// What kiln.vfx already added to the game's streamed textures: the same 16,384 slots.
uint32_t vfx_streamed() {
    const auto* vfx = static_cast<const KilnVfxApi*>(g_api->service(KilnStr{KILN_VFX_SERVICE, sizeof(KILN_VFX_SERVICE) - 1}, KILN_VFX_VERSION));
    if (!vfx || vfx->header.size < offsetof(KilnVfxApi, build_status) + sizeof(void*) || !vfx->build_status) return 0;
    std::string buf(1 << 16, '\0');
    size_t n = 0;
    if (vfx->build_status(buf.data(), buf.size(), &n) != KILN_OK || n >= buf.size()) return 0;
    const ojson j = ojson::parse(buf.substr(0, n), nullptr, false);
    if (j.is_discarded()) return 0;
    try {
        return j.at("budget").at("streamedTextures").at("mods").get<uint32_t>();
    } catch (...) {
        return 0;
    }
}

void KILN_CALL write_tables(const KilnWorldTablesApi* wt, void*) {
    try {
        KilnAssetKey kb{}, kg{}, kp{};
        Bytes rb, rg, rp;
        if (!table_bytes(kTypeBlending, &kb, &rb)) {
            set_status(KILN_SURFACES_NO_TABLES, ojson::object());
            return;
        }
        if (!table_bytes(kTypeGi, &kg, &rg) || !table_bytes(kTypeParams, &kp, &rp)) {
            go_off("the GI or parameter table isn't there");
            return;
        }
        BlendingTable b;
        GiTable g;
        ParamsTable p;
        std::string why;
        if (!read_blending(rb.data(), rb.size(), &b, &why) || !read_gi(rg.data(), rg.size(), &g, &why) || !read_params(rp.data(), rp.size(), &p, &why)) {
            go_off(why);
            return;
        }

        std::vector<SurfaceJob> jobs;
        const uint32_t n = wt->material_count();
        for (uint32_t i = 0; i < n; ++i) {
            KilnWtMaterial m{};
            m.size = sizeof m;
            if (wt->material_at(i, &m) != KILN_OK || m.state != KILN_WT_LIVE) continue;
            SurfaceJob j;
            j.mod = str(m.mod);
            j.block = str(m.block);
            j.dir = str(m.mod_dir);
            j.label = "block buildingMaterial " + j.block + " (mod " + j.mod + ")";
            std::memcpy(j.guid.b, m.guid, 16);
            j.slot = m.cell_id - 128;
            const ojson def = ojson::parse(str(m.definition), nullptr, false, true);
            j.roof = def.is_object() && def.value("roof", false);
            // its block item's icon, unless the block names one: the item's GUID follows from the mod and the block's (oldest) name
            j.icon = def.is_object() && !def.contains("icon");
            j.item = kvb::block_id(j.mod, "item", def.is_object() && def.contains("was") && def["was"].is_string() ? def["was"].get<std::string>() : j.block);
            if (def.is_object() && def.contains("surface")) j.surface = def["surface"];
            jobs.push_back(std::move(j));
        }
        if (wt->header.size >= offsetof(KilnWorldTablesApi, tweak_at) + sizeof(void*)) {
            const uint32_t tn = wt->tweak_count();
            for (uint32_t i = 0; i < tn; ++i) {
                KilnWtTweak t{};
                t.size = sizeof t;
                if (wt->tweak_at(i, &t) != KILN_OK || t.cell_id < 128) continue;
                const ojson def = ojson::parse(str(t.definition), nullptr, false, true);
                if (!def.is_object() || !def.contains("surface")) continue;
                SurfaceJob j;
                j.mod = str(t.mod);
                j.block = str(t.block);
                j.dir = str(t.mod_dir);
                j.label = "block materialTweak " + j.block + " (mod " + j.mod + ")";
                const kvb::Guid g = kvb::block_id(j.mod, "materialtweak", j.block);
                j.guid = g;
                j.slot = t.cell_id - 128;
                j.tweak = true;
                j.surface = def["surface"];
                jobs.push_back(std::move(j));
            }
        }
        if (jobs.empty()) {
            set_status(KILN_SURFACES_UNTOUCHED, ojson::object());
            return;
        }

        WriteOptions opt;
        opt.load = [](const std::string& dir, const std::string& rel, kvb::RawImage* img, std::string* why) {
            Bytes png;
            if (!read_file(to_path(dir) / to_path(rel), &png)) {
                *why = "no such file in the mod";
                return false;
            }
            return kvb::decode_png(png, kvb::kMaxMaterialTextureSide, img, why);
        };
        opt.cache = disk_cache();
        const bool has_names = wt->header.size >= offsetof(KilnWorldTablesApi, game_material_id) + sizeof(void*);
        opt.game_material = [wt, has_names](const std::string& text) -> int {
            if (has_names) return wt->game_material_id(KilnStr{text.data(), text.size()});
            if (text.rfind("Cell", 0) == 0) return std::atoi(text.c_str() + 4);
            return -1;
        };
        opt.read_blob = [](const kvb::ContentRef& r, Bytes* out) {
            KilnContentHash h{r.hash.size, r.hash.h0, r.hash.h1, r.hash.h2};
            out->resize(r.hash.size);
            size_t got = 0;
            return g_assets->blob_read(&h, out->data(), out->size(), &got) == KILN_OK && got == out->size();
        };
        const kvb::GameCounts game;
        const kvb::Capacity cap;
        const uint32_t vfx = vfx_streamed();
        opt.streamed_room = cap.streamed_textures > game.streamed_textures + vfx ? cap.streamed_textures - game.streamed_textures - vfx : 0;

        const uint64_t t0 = GetTickCount64();
        WriteResult w;
        if (!write_surfaces(b, g, p, jobs, opt, &w)) {
            go_off(w.errors.empty() ? "the tables don't read" : w.errors[0]);
            return;
        }
        for (const std::string& e : w.errors) SF_E("surfaces: %s", e.c_str());
        for (const std::string& x : w.warnings) SF_W("surfaces: %s", x.c_str());

        // Blobs first, read back: a texture whose blob is missing is fatal for the game at its first use.
        std::vector<KilnContentHash> served;
        const auto undo = [&](const std::vector<const KilnAssetKey*>& put) {
            for (const KilnAssetKey* k : put) g_assets->resource_remove(g_self, k);
            for (const KilnContentHash& h : served) g_assets->blob_remove(g_self, &h);
        };
        for (const ServedBlob& blob : w.blobs) {
            KilnContentHash h{blob.hash.size, blob.hash.h0, blob.hash.h1, blob.hash.h2};
            Bytes back(blob.data.size());
            size_t got = 0;
            if (g_assets->blob_override(g_self, &h, blob.data.data(), blob.data.size()) != KILN_OK ||
                g_assets->blob_read(&h, back.data(), back.size(), &got) != KILN_OK || got != back.size() || back != blob.data) {
                undo({});
                go_off("kiln.assets didn't serve the blob of " + blob.what);
                return;
            }
            served.push_back(h);
        }
        const Bytes nb = write_blending(w.blend), ng = write_gi(w.gi), np = write_params(w.params);
        std::vector<const KilnAssetKey*> put;
        for (const auto& [key, bytes, game, name] : {std::tuple{&kb, &nb, &rb, "blending"}, std::tuple{&kg, &ng, &rg, "GI"}, std::tuple{&kp, &np, &rp, "parameter"}}) {
            if (*bytes == *game) continue;
            if (g_assets->resource_put(g_self, key, bytes->data(), bytes->size(), KILN_ASSET_PUT_OVERRIDE) != KILN_OK) {
                undo(put);
                go_off(std::string("kiln.assets refused the ") + name + " table");
                return;
            }
            put.push_back(key);
        }
        // Icons last: a refusal costs a picture, never the tables.
        size_t icons = 0;
        for (const BuiltIcon& icon : w.icons) {
            if (g_assets->icon_register_png(g_self, icon.item.b, icon.png.data(), icon.png.size()) == KILN_OK) ++icons;
            else SF_W("surfaces: %s:%s: kiln.assets didn't take its block's icon: it shows the starter block's", icon.mod.c_str(), icon.block.c_str());
        }
        ojson report = report_json(w);
        report["iconsRegistered"] = icons;
        report["streamedRoom"] = opt.streamed_room;
        report["ms"] = GetTickCount64() - t0;
        for (const JobReport& r : w.jobs)
            SF_I("surfaces: %s:%s: slot %u (cell %u): %s, %u textures, %u new", r.mod.c_str(), r.block.c_str(), r.slot, r.slot + 128, r.status.c_str(), r.textures, r.new_textures);
        SF_I("surfaces: %zu surface(s), layerCount %u, %u new streamed texture(s) of room for %u, %zu blob(s), in %llu ms", w.jobs.size(), w.blend.layer_count, w.new_streamed,
             opt.streamed_room, w.blobs.size(), static_cast<unsigned long long>(GetTickCount64() - t0));
        set_status(put.empty() ? KILN_SURFACES_UNTOUCHED : KILN_SURFACES_WRITTEN, std::move(report));
    } catch (const std::exception& e) {
        go_off(std::string("writing the surfaces failed: ") + e.what());
    }
}

int32_t KILN_CALL t_state() { return g_state.load(); }
KilnResult KILN_CALL t_status(char* buf, size_t cap, size_t* out_len) {
    std::lock_guard lock(g_mu);
    if (out_len) *out_len = g_status.size();
    if (!buf || cap <= g_status.size()) return KILN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(buf, g_status.data(), g_status.size());
    buf[g_status.size()] = 0;
    return KILN_OK;
}
const KilnSurfacesApi kTable = {{sizeof(KilnSurfacesApi), KILN_SURFACES_VERSION}, &t_state, &t_status};

// kiln/config/surfaces.json {"enabled": false} turns it off.
bool enabled() {
    const std::string dir = api_path(KILN_PATH_CONFIG);
    if (dir.empty()) return true;
    Bytes raw;
    if (!read_file(to_path(dir) / L"surfaces.json", &raw)) return true;
    const ojson j = ojson::parse(std::string(raw.begin(), raw.end()), nullptr, false, true);
    return j.is_discarded() || !j.is_object() || j.value("enabled", true);
}

void KILN_CALL on_ready(void*) {
    g_assets = static_cast<const KilnAssetsApi*>(g_api->service(KilnStr{KILN_ASSETS_SERVICE, sizeof(KILN_ASSETS_SERVICE) - 1}, KILN_ASSETS_VERSION));
    g_wt = static_cast<const KilnWorldTablesApi*>(g_api->service(KilnStr{KILN_WORLDTABLES_SERVICE, sizeof(KILN_WORLDTABLES_SERVICE) - 1}, KILN_WORLDTABLES_VERSION));
    if (!g_assets || g_assets->header.size < sizeof(KilnAssetsApi)) {
        go_off("kiln.assets isn't there");
        return;
    }
    // Its state isn't settled before every on_ready has run; a writer of a service that turns off is simply never called.
    if (!g_wt || g_wt->header.size < offsetof(KilnWorldTablesApi, add_writer) + sizeof(void*)) {
        go_off("kiln.worldtables isn't there");
        return;
    }
    const std::string diff = kvb::compare_layouts(kvb::read_layouts(own_image(), surface_layouts()));
    if (!diff.empty()) {
        go_off("this game build changed the surface tables' layouts: " + diff);
        return;
    }
    if (g_wt->add_writer(g_self, &write_tables, nullptr) != KILN_OK) {
        go_off("kiln.worldtables didn't take the table writer");
        return;
    }
    set_status(KILN_SURFACES_WAITING, ojson::object());
}

}  // namespace
}  // namespace ksf

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    using namespace ksf;
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_self = owner;
    info->id = KilnStr{KILN_SURFACES_SERVICE, sizeof(KILN_SURFACES_SERVICE) - 1};
    info->version = KilnStr{"1", 1};
    const KilnResult rc = api->register_service(owner, KilnStr{KILN_SURFACES_SERVICE, sizeof(KILN_SURFACES_SERVICE) - 1}, &kTable);
    if (rc != KILN_OK) return rc;
    if (api->side() != KILN_SIDE_CLIENT) {
        g_state.store(KILN_SURFACES_NO_TABLES);
        g_status = "{\"state\":\"no tables\"}";
        return KILN_OK;
    }
    if (!enabled()) {
        SF_I("surfaces: off by kiln/config/surfaces.json: the game's surface tables are untouched");
        return KILN_OK;
    }
    KilnBuildInfo bi{sizeof bi};
    api->build_info(&bi);
    if (!bi.hooks_enabled) {
        SF_I("surfaces: hooks disabled (safe idle): the game's surface tables are untouched");
        return KILN_OK;
    }
    info->on_ready = &on_ready;
    return KILN_OK;
}
