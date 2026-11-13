// SPDX-License-Identifier: MIT
// kiln.assets: the service entry, the kiln.assets v1 table, the assets.json manifest loader, PNG texture overrides,
// and the dump / status / console surface. Resource and content RE: docs/re/resources.md. Guide:
// docs/kiln/services/assets.md.
#include <filesystem>

#include "assets.h"
#include "nlohmann/json.hpp"
#include "services/kiln_profile.h"

namespace fs = std::filesystem;
using nlohmann::json;

namespace ka {

// A texture override needs to read the vanilla texture resource, so it needs the resource service object S. S is
// created during the game's init, after every plugin's on_ready, so a texture named in assets.json at boot is queued
// and flushed when S appears (store.cpp's on_resource_system_ready calls flush_pending_textures).
namespace {
struct PendingTexture {
    KilnOwner owner;
    KilnAssetKey key;
    std::vector<uint8_t> png;
    std::string label;
};
std::vector<PendingTexture> g_pending_tex;
// Icons registered before S appears (assets.json at boot): a game item's GUID cannot be told from a new item's yet, so
// the icon is registered as new and looked at again when S appears (a game item's: its own texture is retextured).
struct PendingIcon {
    KilnOwner owner;
    uint8_t guid[16];
    Rgba img;
    std::string label;
};
std::vector<PendingIcon> g_pending_icons;
std::mutex g_pending_mu;
}  // namespace

namespace {

// ---- texture override -------------------------------------------------------------------------------------------
// Replaces the content blob a texture resource points at (keen::UiTextureResource / RenderTextureResource) with a PNG
// re-encoded to the texture's own format and size. The resource bytes (and its ContentHash) are read first.
KilnResult texture_override_img(KilnOwner owner, const KilnAssetKey* key, const Rgba& img, std::string* why);

KilnResult texture_override(KilnOwner owner, const KilnAssetKey* key, const void* png, size_t len, std::string* why) {
    Rgba img;
    if (!png_decode(png, len, &img, why)) return KILN_ERR_INVALID_ARGUMENT;
    return texture_override_img(owner, key, img, why);
}

KilnResult texture_override_img(KilnOwner owner, const KilnAssetKey* key, const Rgba& img, std::string* why) {
    if (!g_client) {
        *why = "textures are client-only";
        return KILN_ERR_UNAVAILABLE;
    }
    if (!resources_on() || !content_hooks_on()) {
        *why = "resource/content hooks off";
        return KILN_ERR_UNAVAILABLE;
    }
    std::vector<uint8_t> rb(4096);
    size_t rn = 0;
    KilnResult rr = op_resource_read(key, rb.data(), rb.size(), &rn);
    if (rr == KILN_ERR_BUFFER_TOO_SMALL) {
        rb.resize(rn);
        rr = op_resource_read(key, rb.data(), rb.size(), &rn);
    }
    if (rr != KILN_OK) {
        *why = "texture resource not found";
        return rr;
    }
    rb.resize(rn);
    // Both UiTextureResource (56 B) and RenderTextureResource (72 B) start with a ResourceContentReference
    // {ContentHash hash(16)} ... but their size/format fields differ. Read from reflection-derived offsets.
    uint32_t w = 0, h = 0, format = 0, levels = 1;
    HashK old{};
    if (key->type == KILN_ASSET_TYPE_UI_TEXTURE && rn >= 56) {
        std::memcpy(&old, rb.data(), 16);
        std::memcpy(&w, rb.data() + 0x28, 4);
        std::memcpy(&h, rb.data() + 0x2c, 4);
        format = rb[0x31];
        levels = rb[0x32] ? rb[0x32] : 1;
    } else if (rn >= 72) {  // RenderTextureResource: width u16 @0, height u16 @2, levelCount u8 @8, format u8 @0xa, content @0x10
        uint16_t w16, h16;
        std::memcpy(&w16, rb.data() + 0, 2);
        std::memcpy(&h16, rb.data() + 2, 2);
        w = w16, h = h16;
        levels = rb[8] ? rb[8] : 1;
        format = rb[0xa];
        std::memcpy(&old, rb.data() + 0x10, 16);
    } else {
        *why = "unrecognised texture resource";
        return KILN_ERR_INVALID_ARGUMENT;
    }
    Rgba sized = (img.w == w && img.h == h) ? img : resize(img, w, h);
    std::vector<uint8_t> enc = encode_texture(sized, format, levels, why);
    if (enc.empty()) return KILN_ERR_INVALID_ARGUMENT;
    // Serve the new pixels under the texture's existing ContentHash (override), keeping its size so the streamer's
    // byte count is unchanged. If the encoded size differs from the resource's recorded size, override the resource's
    // ContentHash too (new hash) so the game asks for exactly our bytes.
    if (enc.size() == old.size) {
        KilnContentHash ch{old.size, old.h0, old.h1, old.h2};
        KilnResult r = op_blob_override(owner, &ch, enc.data(), enc.size());
        if (r != KILN_OK) {
            *why = "blob override failed";
            return r;
        }
    } else {
        KilnContentHash nh{};
        op_blob_add(owner, enc.data(), enc.size(), &nh);
        std::memcpy(rb.data() + (key->type == KILN_ASSET_TYPE_UI_TEXTURE ? 0 : 0x10), &nh, 16);
        op_resource_put(owner, key, rb.data(), rb.size(), KILN_ASSET_PUT_OVERRIDE);
    }
    LOGI("assets: retexture %s -> %ux%u %s (%zu bytes, %u mip%s)", key_str({{0}, key->type, key->part}).c_str(), w, h,
         pixel_format_name(format), enc.size(), levels, levels == 1 ? "" : "s");
    return KILN_OK;
}

// ---- item icons ------------------------------------------------------------------------------------------------
// A game item's icon is its own keen::UiTextureResource under the item's GUID (BC7 256x256 with mips on the current
// client). An icon registered for such a GUID (a model override of a game item that needs a matching icon) retextures
// that resource in its own format, size and mips; a new item's GUID gets a new UiTextureResource (op_icon_register).
bool is_game_icon(const uint8_t g[16]) {
    const uintptr_t S = g_S.load();
    if (!S) return false;
    Key k{};
    std::memcpy(k.g, g, 16);
    k.type = KILN_ASSET_TYPE_UI_TEXTURE;
    return game_handle(S, k) != 0;
}

KilnResult retexture_game_icon(KilnOwner owner, const uint8_t g[16], const Rgba& img, const std::string& label) {
    KilnAssetKey k{};
    std::memcpy(k.guid, g, 16);
    k.type = KILN_ASSET_TYPE_UI_TEXTURE;
    std::string why;
    const KilnResult r = texture_override_img(owner, &k, img, &why);
    if (r == KILN_OK) LOGI("assets: icon %s: %s is a game item: its own icon texture is retextured", label.c_str(), guid_str(g).c_str());
    else LOGW("assets: icon %s: retexturing the game item %s's icon failed: %s", label.c_str(), guid_str(g).c_str(), why.c_str());
    return r;
}

KilnResult icon_for_item(KilnOwner owner, const uint8_t g[16], const Rgba& img, const std::string& label) {
    if (!g_client) return KILN_ERR_UNAVAILABLE;
    if (g_S.load()) return is_game_icon(g) ? retexture_game_icon(owner, g, img, label) : op_icon_register(owner, g, img);
    const KilnResult r = op_icon_register(owner, g, img);
    if (r == KILN_OK) {
        std::lock_guard lock(g_pending_mu);
        PendingIcon p{owner, {}, img, label};
        std::memcpy(p.guid, g, 16);
        g_pending_icons.push_back(std::move(p));
    }
    return r;
}

// ---- manifest ---------------------------------------------------------------------------------------------------
// Resolve a manifest-relative path (subfolders allowed, e.g. "cooked/x.bin") under assets/. Rejects absolute paths
// and any ".." component so a manifest cannot reach outside its own assets folder.
bool safe_path(const fs::path& base, const std::string& rel, fs::path* out, std::string* why) {
    if (rel.empty()) { *why = "empty file"; return false; }
    fs::path r = widen(rel);
    if (r.is_absolute()) { *why = "absolute path not allowed: " + rel; return false; }
    for (const auto& part : r) {
        const std::string s = narrow(part.wstring());
        if (s == "..") { *why = "'..' not allowed in path: " + rel; return false; }
    }
    *out = (base / r).lexically_normal();
    return true;
}

bool load_png_file(const fs::path& p, Rgba* out, std::string* why) {
    std::vector<uint8_t> bytes;
    if (!read_file(p.wstring(), &bytes, why)) return false;
    return png_decode(bytes.data(), bytes.size(), out, why);
}

// Reads <mod_dir>/assets/assets.json. Each entry: {kind, guid?, type?, part?, file?, hash?, mode?, patch?}.
KilnResult load_manifest(KilnOwner owner, const std::string& mod_dir, std::string* report) {
    const fs::path base = fs::path(widen(mod_dir)) / L"assets";
    const fs::path mf = base / L"assets.json";
    std::error_code ec;
    if (!fs::exists(mf, ec)) {
        *report = "no assets/assets.json";
        return KILN_ERR_NOT_FOUND;
    }
    std::vector<uint8_t> text;
    std::string why;
    if (!read_file(mf.wstring(), &text, &why)) {
        *report = why;
        return KILN_ERR_INTERNAL;
    }
    json doc = json::parse(text.begin(), text.end(), nullptr, false, true);
    if (doc.is_discarded() || !doc.is_object()) {
        *report = "assets.json is not valid JSON";
        return KILN_ERR_INVALID_ARGUMENT;
    }
    int icons = 0, res = 0, blobs = 0, tex = 0, errs = 0, icons_server = 0, marked_models = 0;
    std::string problems;
    auto fail = [&](const std::string& m) {
        ++errs;
        problems += (problems.empty() ? "" : "; ") + m;
        LOGW("assets: %s: %s", mod_dir.c_str(), m.c_str());
    };
    const json entries = doc.value("entries", json::array());
    // What only a game client uses, by what the entry is (a model and its render data): a dedicated server skips it. The manifest is
    // the same file on a server and in a player's game; a server's cook made no model, and one copied from a PC is not served either.
    std::vector<bool> clients_only;
    if (!g_client) {
        std::vector<ManifestItem> items;
        for (const json& e : entries) {
            ManifestItem it;
            bool ok;
            if (e.is_object()) {
                it.kind = e.value("kind", "");
                it.file = e.value("file", "");
                if (it.kind == "resource") {
                    it.type = type_hash(e.value("type", "0x0"), &ok);
                    for (const json& p : e.value("patch", json::array()))
                        if (p.is_object()) it.patch_blobs.push_back(p.value("blob", ""));
                }
            }
            items.push_back(std::move(it));
        }
        clients_only = client_only_entries(items);
    }
    size_t index = 0;
    for (const json& e : entries) {
        const size_t i = index++;
        const std::string kind = e.value("kind", "");
        const std::string file = e.value("file", "");
        // "side": "client" (soundcook's sounds and cues): a dedicated server has no sound system: not an error
        if (!g_client && e.value("side", "") == "client") {
            ++icons_server;
            if (kind == "resource" && clients_only[i]) ++marked_models;
            continue;
        }
        if (!g_client && clients_only[i]) { ++icons_server; continue; }
        fs::path fp;
        if (kind != "blob" || !file.empty()) {  // every kind but a hash-only blob names a file
            if (!safe_path(base, file, &fp, &why)) { fail(kind + ": " + why); continue; }
        }
        if (kind == "icon") {
            uint8_t g[16];
            if (!parse_guid(e.value("guid", ""), g)) { fail("icon: bad guid"); continue; }
            if (!g_client) { ++icons_server; continue; }  // a dedicated server draws no icons: not an error
            Rgba img;
            if (!load_png_file(fp, &img, &why)) { fail("icon " + file + ": " + why); continue; }
            if (icon_for_item(owner, g, img, "icon " + file) == KILN_OK) ++icons; else fail("icon " + file + ": register failed");
        } else if (kind == "texture") {
            uint8_t g[16];
            bool ok;
            KilnAssetKey k{};
            if (!parse_guid(e.value("guid", ""), g)) { fail("texture: bad guid"); continue; }
            if (!g_client) { ++icons_server; continue; }  // drawn by clients only
            std::memcpy(k.guid, g, 16);
            k.type = type_hash(e.value("type", "keen::UiTextureResource"), &ok);
            k.part = e.value("part", 0);
            std::vector<uint8_t> png;
            if (!read_file(fp.wstring(), &png, &why)) { fail("texture " + file + ": " + why); continue; }
            if (g_S.load()) {  // the resource service is up: do it now
                if (texture_override(owner, &k, png.data(), png.size(), &why) == KILN_OK) ++tex; else fail("texture " + file + ": " + why);
            } else {  // boot: queue until S appears (flushed by flush_pending_textures)
                std::lock_guard lock(g_pending_mu);
                g_pending_tex.push_back({owner, k, std::move(png), "texture " + file});
                ++tex;  // counted as accepted; a flush failure is logged then
            }
        } else if (kind == "blob") {
            std::vector<uint8_t> bytes;
            if (!read_file(fp.wstring(), &bytes, &why)) { fail("blob " + file + ": " + why); continue; }
            HashK fixed;
            KilnResult r;
            if (e.contains("hash") && parse_hash(e["hash"].get<std::string>(), &fixed)) {
                KilnContentHash ch{fixed.size, fixed.h0, fixed.h1, fixed.h2};
                r = op_blob_override(owner, &ch, bytes.data(), bytes.size());
            } else {
                r = op_blob_add(owner, bytes.data(), bytes.size(), nullptr);
            }
            if (r == KILN_OK) ++blobs; else fail("blob " + file + ": failed");
        } else if (kind == "resource") {
            uint8_t g[16];
            bool ok;
            if (!parse_guid(e.value("guid", ""), g)) { fail("resource: bad guid"); continue; }
            std::vector<uint8_t> bytes;
            if (!read_file(fp.wstring(), &bytes, &why)) { fail("resource " + file + ": " + why); continue; }
            // patch: write a referenced blob's ContentHash at the given offset
            for (const json& p : e.value("patch", json::array())) {
                const size_t off = p.value("offset", 0);
                const std::string bf = p.value("blob", "");
                fs::path bp;
                if (!safe_path(base, bf, &bp, &why)) { fail("patch blob " + bf + ": " + why); continue; }
                std::vector<uint8_t> bb;
                if (!read_file(bp.wstring(), &bb, &why)) { fail("patch blob " + bf + ": " + why); continue; }
                HashK h = content_hash_of(bb.data(), bb.size());
                op_blob_add(owner, bb.data(), bb.size(), nullptr);
                if (off + 16 <= bytes.size()) std::memcpy(bytes.data() + off, &h, 16);
                else fail("patch offset past resource end");
            }
            KilnAssetKey k{};
            std::memcpy(k.guid, g, 16);
            k.type = type_hash(e.value("type", "0x0"), &ok);
            k.part = e.value("part", 0);
            const std::string mode = e.value("mode", "any");
            uint32_t flags = mode == "override" ? KILN_ASSET_PUT_OVERRIDE : mode == "new" ? KILN_ASSET_PUT_NEW : 0;
            if (op_resource_put(owner, &k, bytes.data(), bytes.size(), flags) == KILN_OK) {
                ++res;
            } else {
                Key kk;
                std::memcpy(kk.g, k.guid, 16);
                kk.type = k.type;
                kk.part = k.part;
                fail(player_graph_key(kk) ? "resource " + file + ": refused: " + key_str(kk) +
                                                " is the player's animation graph, reserved for kiln.playeranim (add player animation states in"
                                                " assets/player-states.json instead)"
                                          : "resource " + file + ": put failed");
            }
        } else {
            fail("unknown kind '" + kind + "'");
        }
    }
    char b[256];
    std::snprintf(b, sizeof b, "icons %d%s, textures %d, resources %d, blobs %d, errors %d", icons,
                  icons_server ? (" (" + std::to_string(icons_server) + " entries for clients only, skipped on this server)").c_str() : "", tex, res,
                  blobs, errs);
    *report = std::string(b) + (problems.empty() ? "" : " (" + problems + ")");
    LOGI("assets: %s: %s", mod_dir.c_str(), report->c_str());
    // A model's entry needs no mark (it is skipped by its type). The cook of Kiln 0.2.0-dev.26 wrote the mark into a server's copy of
    // assets.json, which made that copy another file than the players': their games then see this mod as changed.
    if (marked_models)
        LOGW("assets: %s: %d model entr%s in assets/assets.json carr%s a \"side\": \"client\" mark. A cook of Kiln 0.2.0-dev.26 wrote such marks "
             "into a server's copy of the file; players' copies have none, so their games see another mod than this server's. Cook the mod "
             "again on this server (kiln cook): that takes the marks out",
             mod_dir.c_str(), marked_models, marked_models == 1 ? "y" : "ies", marked_models == 1 ? "ies" : "y");
    return KILN_OK;
}

// ---- dump -------------------------------------------------------------------------------------------------------
KilnResult dump(const std::string& spec, std::string* report) {
    const std::string dir = api_path(KILN_PATH_DATA) + "\\assets\\dump";
    CreateDirectoryW(widen(api_path(KILN_PATH_DATA) + "\\assets").c_str(), nullptr);
    CreateDirectoryW(widen(dir).c_str(), nullptr);
    auto write = [&](const std::string& name, const uint8_t* d, size_t n) {
        HANDLE h = CreateFileW(widen(dir + "\\" + name).c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
        if (h == INVALID_HANDLE_VALUE) return false;
        DWORD w;
        WriteFile(h, d, static_cast<DWORD>(n), &w, nullptr);
        CloseHandle(h);
        return true;
    };
    if (spec.rfind("blob:", 0) == 0) {
        HashK hk;
        if (!parse_hash(spec.substr(5), &hk)) { *report = "bad blob spec"; return KILN_ERR_INVALID_ARGUMENT; }
        std::vector<uint8_t> buf(hk.size);
        size_t n = 0;
        KilnContentHash ch{hk.size, hk.h0, hk.h1, hk.h2};
        if (op_blob_read(&ch, buf.data(), buf.size(), &n) != KILN_OK) { *report = "blob not found"; return KILN_ERR_NOT_FOUND; }
        write(hash_str(hk) + ".blob", buf.data(), n);
        *report = "wrote " + std::to_string(n) + " bytes to " + dir + "\\" + hash_str(hk) + ".blob";
        return KILN_OK;
    }
    if (spec == "list" || spec.rfind("type:", 0) == 0) {
        bool ok;
        uint32_t type = spec == "list" ? KILN_ASSET_TYPE_UI_TEXTURE : type_hash(spec.substr(5), &ok);
        std::vector<KilnAssetKey> keys(200000);
        size_t cnt = 0;
        op_resource_list(type, keys.data(), keys.size(), &cnt);
        json arr = json::array();
        for (size_t i = 0; i < cnt && i < keys.size(); ++i) arr.push_back(key_str({{0}, keys[i].type, keys[i].part}));
        std::string out = arr.dump(2);
        write("list-" + type_name(type) + ".json", reinterpret_cast<const uint8_t*>(out.data()), out.size());
        *report = std::to_string(cnt) + " resource(s) of " + type_name(type);
        return KILN_OK;
    }
    // "<guid>[:<type>[:<part>]]" where <type> may contain "::"; the part, if present, is the trailing numeric token.
    std::string g = spec, ts = "keen::RenderModel", ps = "0";
    if (auto c = spec.find(':'); c != std::string::npos) {
        g = spec.substr(0, c);
        std::string rest = spec.substr(c + 1);
        const auto last = rest.rfind(':');
        if (last != std::string::npos && last + 1 < rest.size() &&
            rest.find_first_not_of("0123456789", last + 1) == std::string::npos &&
            rest[last - 1] != ':') {  // a trailing :<digits>, not the second ':' of "::"
            ts = rest.substr(0, last);
            ps = rest.substr(last + 1);
        } else {
            ts = rest;
        }
    }
    KilnAssetKey k{};
    if (!parse_guid(g, k.guid)) { *report = "bad guid"; return KILN_ERR_INVALID_ARGUMENT; }
    bool ok;
    k.type = type_hash(ts, &ok);
    k.part = static_cast<uint32_t>(std::strtoul(ps.c_str(), nullptr, 10));
    std::vector<uint8_t> buf(16);
    size_t n = 0;
    KilnResult r = op_resource_read(&k, buf.data(), buf.size(), &n);
    if (r == KILN_ERR_BUFFER_TOO_SMALL) { buf.resize(n); r = op_resource_read(&k, buf.data(), buf.size(), &n); }
    if (r != KILN_OK) { *report = "resource not found: " + key_str({{0}, k.type, k.part}); return r; }
    const std::string name = guid_str(k.guid) + "." + type_name(k.type) + ".bin";
    write(name, buf.data(), n);
    *report = "wrote " + std::to_string(n) + " bytes to " + dir + "\\" + name;
    return KILN_OK;
}

}  // namespace

// Called by store.cpp's on_resource_system_ready, on the game's thread, once S is available.
void flush_pending_textures() {
    std::vector<PendingTexture> pend;
    std::vector<PendingIcon> icons;
    {
        std::lock_guard lock(g_pending_mu);
        pend.swap(g_pending_tex);
        icons.swap(g_pending_icons);
    }
    for (auto& p : icons)
        if (is_game_icon(p.guid)) {  // registered as new at boot: the game has this key, so it is a game item's icon
            op_icon_remove(p.owner, p.guid);
            retexture_game_icon(p.owner, p.guid, p.img, p.label);
        }
    for (auto& p : pend) {
        std::string why;
        if (texture_override(p.owner, &p.key, p.png.data(), p.png.size(), &why) == KILN_OK)
            LOGI("assets: %s applied", p.label.c_str());
        else
            LOGW("assets: %s failed: %s", p.label.c_str(), why.c_str());
    }
}

namespace {

void load_profile_manifests() {
    // Every mod folder in the load order: kiln.profile when present, else the legacy kiln/mods drop-ins.
    const KilnProfileApi* prof = static_cast<const KilnProfileApi*>(g_api->service(KilnStr{"kiln.profile", 12}, 1));
    int loaded = 0;
    auto one = [&](const std::string& dir) {
        std::error_code ec;
        if (!fs::exists(fs::path(widen(dir)) / L"assets" / L"assets.json", ec)) return;
        KilnOwner o = g_api->owner_create(KilnStr{dir.c_str(), dir.size()});
        std::string rep;
        if (load_manifest(o, dir, &rep) == KILN_OK) ++loaded;
    };
    bool used_profile = false;
    bool scan_dropins = true;  // legacy / no profile: scan kiln/mods
    if (prof && prof->header.size >= sizeof(KilnProfileApi)) {
        const uint32_t n = prof->count(KILN_PROFILE_KIND_KILN);
        for (uint32_t i = 0; i < n; ++i) {
            KilnProfileMod m{sizeof m};
            if (prof->get(KILN_PROFILE_KIND_KILN, i, &m) == KILN_OK && m.enabled) one(std::string(m.path.ptr, m.path.len));
        }
        used_profile = prof->mode() == KILN_PROFILE_ACTIVE || prof->mode() == KILN_PROFILE_ONCE;
        // When a profile is active, kiln/mods is scanned only if the profile opts its drop-ins in (bit 0), like
        // Kiln.Host. LEGACY / NO_MODS: dropins() is all bits / 0, so this keeps today's behaviour.
        if (used_profile) scan_dropins = (prof->dropins() & (1u << KILN_PROFILE_KIND_KILN)) != 0;
    }
    if (scan_dropins) {  // every subfolder of kiln/mods not marked .disabled (and not already loaded from the profile)
        const std::string mods = api_path(KILN_PATH_MODS);
        std::error_code ec;
        for (auto it = fs::directory_iterator(widen(mods), ec); !ec && it != fs::directory_iterator(); it.increment(ec))
            if (it->is_directory(ec) && !fs::exists(it->path() / L".disabled", ec)) one(narrow(it->path().wstring()));
    }
    if (loaded) LOGI("assets: loaded assets.json for %d mod(s)", loaded);
}

// ---- service table ----------------------------------------------------------------------------------------------
KilnResult KILN_CALL t_icon_png(KilnOwner o, const uint8_t g[16], const void* png, size_t n) {
    Rgba img;
    std::string why;
    if (!png_decode(png, n, &img, &why)) {
        LOGW("assets: icon PNG: %s", why.c_str());
        return KILN_ERR_INVALID_ARGUMENT;
    }
    return icon_for_item(o, g, img, "PNG (" + std::to_string(n) + " bytes)");
}
KilnResult KILN_CALL t_icon_file(KilnOwner o, const uint8_t g[16], KilnStr path) {
    std::string p(path.ptr, path.len);
    fs::path fp = widen(p);
    if (fp.is_relative()) fp = fs::path(widen(api_path(KILN_PATH_KILN))) / fp;
    Rgba img;
    std::string why;
    if (!load_png_file(fp, &img, &why)) {
        LOGW("assets: icon file %s: %s", p.c_str(), why.c_str());
        return KILN_ERR_INVALID_ARGUMENT;
    }
    return icon_for_item(o, g, img, p);
}
KilnResult KILN_CALL t_icon_remove(KilnOwner o, const uint8_t g[16]) { return op_icon_remove(o, g); }
int32_t KILN_CALL t_icon_state(const uint8_t g[16]) { return op_icon_state(g); }

KilnResult KILN_CALL t_res_put(KilnOwner o, const KilnAssetKey* k, const void* d, size_t n, uint32_t f) {
    return op_resource_put(o, k, d, n, f);
}
KilnResult KILN_CALL t_res_remove(KilnOwner o, const KilnAssetKey* k) { return op_resource_remove(o, k); }
KilnResult KILN_CALL t_res_read(const KilnAssetKey* k, void* b, size_t c, size_t* n) { return op_resource_read(k, b, c, n); }
KilnResult KILN_CALL t_res_list(uint32_t t, KilnAssetKey* o, size_t c, size_t* n) { return op_resource_list(t, o, c, n); }
KilnResult KILN_CALL t_blob_add(KilnOwner o, const void* d, size_t n, KilnContentHash* h) { return op_blob_add(o, d, n, h); }
KilnResult KILN_CALL t_blob_over(KilnOwner o, const KilnContentHash* h, const void* d, size_t n) {
    return op_blob_override(o, h, d, n);
}
KilnResult KILN_CALL t_blob_remove(KilnOwner o, const KilnContentHash* h) { return op_blob_remove(o, h); }
KilnResult KILN_CALL t_blob_read(const KilnContentHash* h, void* b, size_t c, size_t* n) { return op_blob_read(h, b, c, n); }
KilnResult KILN_CALL t_tex_png(KilnOwner o, const KilnAssetKey* k, const void* png, size_t n) {
    std::string why;
    return texture_override(o, k, png, n, &why);
}
KilnResult KILN_CALL t_dump(KilnStr spec, char* rep, size_t cap, size_t* out) {
    std::string r;
    KilnResult rc = dump(std::string(spec.ptr, spec.len), &r);
    if (out) *out = r.size();
    if (rep && cap) std::snprintf(rep, cap, "%s", r.c_str());
    return rc;
}
KilnResult KILN_CALL t_status(char* json_out, size_t cap, size_t* out) {
    std::string s = "{" + store_status_json() + ",\"hooks\":" + hooks_status_json() + "}";
    if (out) *out = s.size();
    if (json_out && cap) std::snprintf(json_out, cap, "%s", s.c_str());
    return cap > s.size() ? KILN_OK : KILN_ERR_BUFFER_TOO_SMALL;
}
void KILN_CALL t_revert(KilnOwner o) { op_revert_owner(o); }
KilnResult KILN_CALL t_manifest(KilnOwner o, KilnStr dir, char* rep, size_t cap, size_t* out) {
    std::string r;
    KilnResult rc = load_manifest(o, std::string(dir.ptr, dir.len), &r);
    if (out) *out = r.size();
    if (rep && cap) std::snprintf(rep, cap, "%s", r.c_str());
    return rc;
}
KilnResult KILN_CALL t_res_data(const KilnAssetKey* k, const void** ptr, size_t* len) { return op_resource_data(k, ptr, len); }
int32_t KILN_CALL t_res_is_new(const KilnAssetKey* k) { return op_resource_is_new(k); }
size_t KILN_CALL t_res_new_count(uint32_t t) { return op_resource_new_count(t); }
KilnResult KILN_CALL t_when_ready(void(KILN_CALL* fn)(void*), void* user) { return op_when_resources_ready(fn, user); }

const KilnAssetsApi kTable = {
    {sizeof(KilnAssetsApi), KILN_ASSETS_VERSION},
    &t_icon_png, &t_icon_file, &t_icon_remove, &t_icon_state,
    &t_res_put, &t_res_remove, &t_res_read, &t_res_list,
    &t_blob_add, &t_blob_over, &t_blob_remove, &t_blob_read,
    &t_tex_png,
    &t_dump, &t_status, &t_revert, &t_manifest,
    &t_res_data,
    &t_res_is_new,
    &t_res_new_count,
    &t_when_ready,
};

// ---- control commands: kiln/control/assets.cmd -> assets.result.json (handled by the core's console) ------------
// We expose "assets dump <spec>" / "assets status" through a tick task that polls the control folder ourselves is
// heavy; instead the managed Kiln.Assets exposes Assets.Dump via the table. A native console command would need a
// core hook we do not own, so the dump is reachable through C# and through the table's dump() for the models worker.

void KILN_CALL on_ready(void*) {
    // The resource system may already be up (services load after the game's early init on the client); try to bind.
    if (uintptr_t S = g_S.load()) on_resource_system_ready(S);
    // Load every mod's assets.json now, before the UI builds, so boot-time icons are served from the first frame.
    load_profile_manifests();
    std::string why;
    if (content_on()) {
        if (!verify_content_map(&why)) LOGW("assets: content map check: %s (blob reads may be unreliable)", why.c_str());
    }
    LOGI("assets: ready; %s", (std::string("{") + store_status_json() + "}").c_str());
}

}  // namespace
}  // namespace ka

using namespace ka;

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_self = owner;
    info->id = KilnStr{KILN_ASSETS_SERVICE, sizeof(KILN_ASSETS_SERVICE) - 1};
    info->version = KilnStr{"1", 1};
    info->on_ready = &on_ready;

    KilnBuildInfo bi{sizeof bi};
    api->build_info(&bi);
    g_client = bi.side == KILN_SIDE_CLIENT;
    if (!bi.hooks_enabled) {
        LOGI("assets: hooks disabled (safe idle); service unavailable");
        // still publish the table so managed code sees "unavailable" cleanly via each op's checks
    } else {
        std::string why;
        if (install_resource_hooks(&why)) {
            LOGI("assets: resource service hooked");
            if (install_content_hooks(&why)) LOGI("assets: content reader hooked"); else LOGW("assets: content off: %s", why.c_str());
            if (g_client) {
                if (install_ui_hooks(&why)) LOGI("assets: UI icon cache hooked"); else LOGW("assets: UI icons off: %s", why.c_str());
            }
        } else {
            LOGW("assets: resources unavailable: %s", why.c_str());
        }
    }
    return api->register_service(owner, KilnStr{KILN_ASSETS_SERVICE, sizeof(KILN_ASSETS_SERVICE) - 1}, &kTable);
}
