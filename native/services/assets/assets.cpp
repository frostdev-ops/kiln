// SPDX-License-Identifier: MIT
// kiln.assets: the service entry, the kiln.assets v1 table, the assets.json manifest loader, PNG texture overrides,
// and the dump / status / console surface. Resource and content RE: docs/re/resources.md. Guide:
// docs/kiln/services/assets.md.
#include <algorithm>
#include <filesystem>
#include <optional>
#include <type_traits>

#include "assets.h"
#include "blob_hash.h"
#include "bootpack_internal.h"
#include "contained.h"
#include "kiln_boot_collector.hpp"
#include "cooktrust.h"
#include "nlohmann/json.hpp"
#include "services/kiln_profile.h"
#include "texture_rgba.h"

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
    uint32_t origin;
    uint8_t guid[16];
    Rgba img;
    std::string label;
};
std::vector<PendingIcon> g_pending_icons;
std::mutex g_pending_mu;
// A boot generation's owners of required participants (g_pending_mu): a texture or icon of theirs queued until S appears that then
// fails stops the start. Also resolves raw resources' late omission provenance (including dedicated servers).
std::map<KilnOwner, std::string> g_boot_required_owner;
}  // namespace

void report_resource_omission(const ResourceOmission& omission) {
    std::string mod = omission.mod;
    if (mod.empty()) {
        std::lock_guard lock(g_pending_mu);
        if (const auto it = g_boot_required_owner.find(omission.owner); it != g_boot_required_owner.end()) mod = it->second;
    }
    const std::string key = key_str(omission.key);
    LOGW("assets: mod %s owner %llu resource %s refused: %s", mod.empty() ? "<unknown>" : mod.c_str(),
         static_cast<unsigned long long>(omission.owner), key.c_str(), omission.why.c_str());
    if (!mod.empty())
        kiln::boot::activation_fault(g_api, g_self, mod, "assets", key, "kiln.assets: " + omission.why);
}

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
        PendingIcon p{owner, current_origin(), {}, img, label};
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

// This game's cook receipts and its "cooked" policy (cooktrust.h), with what could not be read logged once. The store
// must be <game>/kiln/state/cook-receipts itself: a link there, or at kiln/state, is refused (by name, as Wine shows its
// Unix links).
CookTrust read_cook_trust() {
    const fs::path kiln = widen(api_path(KILN_PATH_KILN)), state = widen(api_path(KILN_PATH_STATE));
    auto linked = [](const fs::path& p) {
        const DWORD a = GetFileAttributesW(p.c_str());
        return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_REPARSE_POINT);
    };
    CookTrust t = load_cook_trust(kiln, state, g_client, linked(state) || linked(state / L"cook-receipts"));
    for (const auto& n : t.notes) LOGW("assets: cook receipts: %s", n.c_str());
    if (t.policy == CookedPolicy::Any)
        LOGW("assets: kiln.json \"cooked\": \"any\": cooked files load whoever made them (a setting for development servers; a game "
             "that keeps the rule loads only what its own cook made)");
    return t;
}

// Whether `fp` (a file a manifest names, read through read_contained: physically in `base`) is a cook output of the
// mod whose assets folder is `base`: below assets/cooked.
bool in_cooked(const fs::path& base, const fs::path& fp) {
    return is_cooked_path(narrow(fp.lexically_normal().lexically_relative(base.lexically_normal()).generic_wstring()));
}

// Reads <mod_dir>/assets/assets.json. Each entry: {kind, guid?, type?, part?, file?, hash?, mode?, patch?}. A file of
// the mod's assets/cooked loads only when a cook receipt of this game lists its bytes (cooktrust.h); `trust` null: they
// are read here.
// The mod folders of this start's load order (load_profile_manifests): whose cooked files no other name may reach.
std::vector<std::string> g_loaded_dirs;
std::mutex g_loaded_mu;

// Standard base64 (a boot document's inline resource); false on a character outside the alphabet.
bool base64_decode(const std::string& in, std::vector<uint8_t>* out) {
    auto val = [](char c) -> int {
        if (c >= 'A' && c <= 'Z') return c - 'A';
        if (c >= 'a' && c <= 'z') return c - 'a' + 26;
        if (c >= '0' && c <= '9') return c - '0' + 52;
        return c == '+' ? 62 : c == '/' ? 63 : -1;
    };
    out->clear();
    out->reserve(in.size() / 4 * 3);
    uint32_t acc = 0;
    int bits = 0;
    for (const char c : in) {
        if (c == '=') break;
        const int v = val(c);
        if (v < 0) return false;
        acc = (acc << 6) | static_cast<uint32_t>(v);
        bits += 6;
        if (bits >= 8) {
            bits -= 8;
            out->push_back(static_cast<uint8_t>((acc >> bits) & 0xFF));
        }
    }
    return true;
}

// `doc_in`: a manifest that is not the file (a boot generation's document, chapter 08), its files still the mod's own.
// `errors`: each entry error, "entries[<i>] (<kind> <file>): <what>" (a boot generation's prepare refuses a required mod for
// the first one), a cooked file this game's cook did not make and that is therefore not loaded included.
KilnResult load_manifest(KilnOwner owner, const std::string& mod_dir, std::string* report, const CookTrust* trust_in = nullptr,
                         const CookedIds* cooked_ids_in = nullptr, const json* doc_in = nullptr, std::vector<std::string>* errors = nullptr) {
    OriginScope origin(KILN_BLOB_ORIGIN_MANIFEST);  // every blob this registers (icons and textures too): catalog provenance
    const fs::path base = fs::path(widen(mod_dir)) / L"assets";
    const fs::path mf = base / L"assets.json";
    std::error_code ec;
    std::string why;
    json doc;
    if (doc_in) {
        doc = *doc_in;
    } else {
        if (!fs::exists(mf, ec)) {
            *report = "no assets/assets.json";
            return KILN_ERR_NOT_FOUND;
        }
        std::vector<uint8_t> text;
        if (!read_file(mf.wstring(), &text, &why)) {
            *report = "assets/assets.json cannot be read: " + why;
            return KILN_ERR_INTERNAL;
        }
        doc = json::parse(text.begin(), text.end(), nullptr, false, true);
    }
    if (doc.is_discarded() || !doc.is_object()) {
        *report = "assets.json is not valid JSON";
        return KILN_ERR_INVALID_ARGUMENT;
    }
    int icons = 0, res = 0, blobs = 0, tex = 0, errs = 0, icons_server = 0, marked_models = 0, not_here = 0, unmade = 0;
    std::string problems, not_here_names, unmade_names;
    size_t at = 0;  // the entry being read: entry errors name it
    std::string at_kind, at_file;
    auto entry_label = [&] {
        return "entries[" + std::to_string(at) + "]" + (at_kind.empty() && at_file.empty() ? std::string() : " (" + at_kind + (at_file.empty() ? "" : " " + at_file) + ")");
    };
    std::optional<CookTrust> own_trust;
    auto trust = [&]() -> const CookTrust& {
        if (trust_in) return *trust_in;
        if (!own_trust) own_trust = read_cook_trust();
        return *own_trust;
    };
    // a cooked file this game's cook did not make: with "local" not loaded, with "warn" loaded; counted and named (the
    // first three) either way
    auto made_here = [&](const fs::path& fp, const std::vector<uint8_t>& bytes) {
        if (!in_cooked(base, fp)) return true;
        const CookedVerdict v = cooked_verdict(trust(), bytes.data(), bytes.size());
        if (v == CookedVerdict::Load) return true;
        const std::string name = narrow(fp.lexically_normal().lexically_relative(base.lexically_normal()).generic_wstring());
        int& n = v == CookedVerdict::Refuse ? not_here : unmade;
        std::string& names = v == CookedVerdict::Refuse ? not_here_names : unmade_names;
        if (++n <= 3) names += (names.empty() ? "" : ", ") + name;
        if (v == CookedVerdict::Refuse && errors)
            errors->push_back(entry_label() + ": cooked file " + name + " was not cooked by this game's cook, so it is not loaded (cook the mod here)");
        return v == CookedVerdict::LoadUnmade;
    };
    // every file is read from the mod's own assets folder: a link or junction there (to another mod's assets/cooked,
    // to anywhere) lends nothing, nor does another name for a cooked file of any mod of the load order (contained.cpp)
    std::optional<CookedIds> own_ids;
    const CookedIds* cooked_ids = cooked_ids_in;
    if (!cooked_ids) {
        own_ids.emplace();
        std::vector<std::string> dirs;
        {
            std::lock_guard lock(g_loaded_mu);
            dirs = g_loaded_dirs;
        }
        dirs.push_back(mod_dir);
        std::vector<fs::path> paths;
        for (const auto& d : dirs) paths.push_back(fs::path(widen(d)));
        own_ids = cooked_ids_of(paths);
        for (const auto& c : own_ids->cut) LOGW("assets: a cooked folder could not be scanned whole (%s)", c.c_str());
        cooked_ids = &*own_ids;
    }
    // a file outside its own assets/cooked that a cut scan could not clear: loaded under "warn" and said once (below)
    int unproven = 0;
    std::string unproven_names;
    auto read = [&](const fs::path& fp, std::vector<uint8_t>* out, std::string* w) {
        bool u = false;
        if (!read_contained(base, fp, out, w, cooked_ids, trust().policy, &u)) return false;
        if (u && ++unproven <= 3)
            unproven_names += (unproven_names.empty() ? "" : ", ") + narrow(fp.lexically_normal().lexically_relative(base.lexically_normal()).generic_wstring());
        return true;
    };
    auto fail = [&](const std::string& m) {
        ++errs;
        problems += (problems.empty() ? "" : "; ") + m;
        LOGW("assets: %s: %s", mod_dir.c_str(), m.c_str());
        if (errors) errors->push_back(entry_label() + ": " + m);
    };
    if (doc.contains("entries") && !doc["entries"].is_array()) {
        *report = "assets.json: \"entries\" is not an array";
        if (errors) errors->push_back(*report);
        return KILN_ERR_INVALID_ARGUMENT;
    }
    const json entries = doc.value("entries", json::array());
    // What only a game client uses, by what the entry is (a model and its render data): a dedicated server skips it. The manifest is
    // the same file on a server and in a player's game; a server's cook made no model, and one copied from a PC is not served either.
    std::vector<bool> clients_only;
    if (!g_client) {
        std::vector<ManifestItem> items;
        for (const json& e : entries) {
            ManifestItem it;
            bool ok;
            if (e.is_object()) try {
                it.kind = e.value("kind", "");
                it.file = e.value("file", "");
                it.fixed_hash = it.kind == "blob" && e.contains("hash");
                if (it.kind == "resource") {
                    it.type = type_hash(e.value("type", "0x0"), &ok);
                    for (const json& p : e.value("patch", json::array()))
                        if (p.is_object()) it.patch_blobs.push_back(p.value("blob", ""));
                }
            } catch (const json::exception&) {
                it = ManifestItem{};  // a malformed entry: the loop below says what is wrong with it
            }
            items.push_back(std::move(it));
        }
        clients_only = client_only_entries(items);
    }
    size_t index = 0;
    for (const json& e : entries) {
        const size_t i = index++;
        at = i;
        at_kind.clear();
        at_file.clear();
        if (!e.is_object()) {
            fail("not an object");
            continue;
        }
        try {
        const std::string kind = e.value("kind", "");
        const std::string file = e.value("file", "");
        at_kind = kind;
        at_file = file;
        // "side": "client" (soundcook's sounds and cues): a dedicated server has no sound system: not an error
        if (!g_client && e.value("side", "") == "client") {
            ++icons_server;
            if (kind == "resource" && clients_only[i]) ++marked_models;
            continue;
        }
        if (!g_client && clients_only[i]) { ++icons_server; continue; }
        fs::path fp;
        // a boot generation's resource carries its bytes (chapter 08: Kiln.Blocks' lowered programs), base64 in "data"
        const bool inline_data = doc_in && kind == "resource" && e.contains("data") && e["data"].is_string();
        if (!inline_data && (kind != "blob" || !file.empty())) {  // every kind but a hash-only blob names a file
            if (!safe_path(base, file, &fp, &why)) { fail(kind + ": " + why); continue; }
        }
        if (kind == "icon") {
            uint8_t g[16];
            if (!parse_guid(e.value("guid", ""), g)) { fail("icon: bad guid"); continue; }
            if (!g_client) { ++icons_server; continue; }  // a dedicated server draws no icons: not an error
            std::vector<uint8_t> png;
            Rgba img;
            if (!read(fp, &png, &why)) { fail("icon " + file + ": " + why); continue; }
            if (!made_here(fp, png)) continue;
            if (!png_decode(png.data(), png.size(), &img, &why)) { fail("icon " + file + ": " + why); continue; }
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
            if (!read(fp, &png, &why)) { fail("texture " + file + ": " + why); continue; }
            if (!made_here(fp, png)) continue;
            if (g_S.load()) {  // the resource service is up: do it now
                if (texture_override(owner, &k, png.data(), png.size(), &why) == KILN_OK) ++tex; else fail("texture " + file + ": " + why);
            } else {  // boot: queue until S appears (flushed by flush_pending_textures)
                std::lock_guard lock(g_pending_mu);
                g_pending_tex.push_back({owner, k, std::move(png), "texture " + file});
                ++tex;  // counted as accepted; a flush failure is logged then
            }
        } else if (kind == "blob") {
            // "hash": the key the game reads the bytes by. One that is not <decimal size>-<h0>-<h1>-<h2> refuses the entry; it
            // never falls back to a key kiln.assets derives, which the game would not ask for (blob_hash.h, host-tested)
            HashK fixed{};
            bool has_hash = false;
            if (const std::string bad = blob_entry_hash(e, file, &has_hash, &fixed); !bad.empty()) { fail(bad); continue; }
            std::vector<uint8_t> bytes;
            if (!read(fp, &bytes, &why)) { fail("blob " + file + ": " + why); continue; }
            if (!made_here(fp, bytes)) continue;
            if (const std::string bad = has_hash ? blob_size_refusal(file, fixed, bytes.size()) : std::string(); !bad.empty()) {
                fail(bad);
                continue;
            }
            KilnResult r;
            if (has_hash) {
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
            if (inline_data) {
                if (!base64_decode(e["data"].get<std::string>(), &bytes)) { fail("resource: \"data\" is not base64"); continue; }
            } else {
                if (!read(fp, &bytes, &why)) { fail("resource " + file + ": " + why); continue; }
                if (!made_here(fp, bytes)) continue;
            }
            // patch: write a referenced blob's ContentHash at the given offset. Every blob is read and judged before
            // anything is added: a resource whose blob this game's cook did not make is left out whole.
            std::vector<std::pair<size_t, std::vector<uint8_t>>> patches;
            bool blobs_here = true;
            for (const json& p : e.value("patch", json::array())) {
                const size_t off = p.value("offset", 0);
                const std::string bf = p.value("blob", "");
                fs::path bp;
                if (!safe_path(base, bf, &bp, &why)) { fail("patch blob " + bf + ": " + why); continue; }
                std::vector<uint8_t> bb;
                if (!read(bp, &bb, &why)) { fail("patch blob " + bf + ": " + why); continue; }
                if (!made_here(bp, bb)) { blobs_here = false; break; }
                patches.emplace_back(off, std::move(bb));
            }
            if (!blobs_here) continue;
            for (auto& [off, bb] : patches) {
                HashK h = content_hash_of(bb.data(), bb.size());
                op_blob_add(owner, bb.data(), bb.size(), nullptr);
                // off comes from the mod's manifest, and a negative number there is a huge one here: the room is
                // asked of the resource's size, never of off + 16 (which wraps, and wrote before the buffer)
                if (off <= bytes.size() && bytes.size() - off >= 16) std::memcpy(bytes.data() + off, &h, 16);
                else fail("patch offset past resource end");
            }
            KilnAssetKey k{};
            std::memcpy(k.guid, g, 16);
            k.type = type_hash(e.value("type", "0x0"), &ok);
            k.part = e.value("part", 0);
            const std::string mode = e.value("mode", "any");
            uint32_t flags = mode == "override" ? KILN_ASSET_PUT_OVERRIDE : mode == "new" ? KILN_ASSET_PUT_NEW : 0;
            const KilnResult put = op_resource_put(owner, &k, bytes.data(), bytes.size(), flags);
            if (put == KILN_OK) {
                ++res;
            } else {
                Key kk;
                std::memcpy(kk.g, k.guid, 16);
                kk.type = k.type;
                kk.part = k.part;
                fail(k.type == 0xe64ee036u && put == KILN_ERR_UNAVAILABLE
                         ? "resource " + file + ": VFX preflight refused " + key_str(kk) +
                               ": capacity exhausted or original census/enumeration unavailable (see assets log)"
                         : player_graph_key(kk) ? "resource " + file + ": refused: " + key_str(kk) +
                                                " is the player's animation graph, reserved for kiln.playeranim (add player animation states in"
                                                " assets/player-states.json instead)"
                                          : "resource " + file + ": put failed");
            }
        } else {
            fail("unknown kind '" + kind + "'");
        }
        } catch (const json::exception& x) {  // a field of the wrong type (e.value on a string where a number belongs)
            fail(std::string("malformed: ") + x.what());
        }
    }
    char b[256];
    std::snprintf(b, sizeof b, "icons %d%s, textures %d, resources %d, blobs %d, errors %d", icons,
                  icons_server ? (" (" + std::to_string(icons_server) + " entries for clients only, skipped on this server)").c_str() : "", tex, res,
                  blobs, errs);
    *report = std::string(b) + (not_here ? ", " + std::to_string(not_here) + " cooked file" + (not_here == 1 ? "" : "s") + " not made here" : "") +
              (unmade ? ", " + std::to_string(unmade) + " cooked file" + (unmade == 1 ? "" : "s") + " not made here (loaded)" : "") +
              (problems.empty() ? "" : " (" + problems + ")");
    LOGI("assets: %s: %s", mod_dir.c_str(), report->c_str());
    if (unproven)
        LOGW("assets: %s: %d file%s loaded that cannot be shown to be no other name for a cooked file (%s%s): a mod's cooked folder could not "
             "be scanned whole (said above). Under kiln.json \"cooked\": \"local\" they are not loaded",
             mod_dir.c_str(), unproven, unproven == 1 ? "" : "s", unproven_names.c_str(), unproven > 3 ? ", ..." : "");
    if (unmade)
        LOGW("assets: %s: %d cooked file%s loaded that %s not cooked on this machine (%s%s). Cook the mod here: the launcher does it before "
             "the game starts; on a dedicated server run kiln cook <mod folder> --game <server folder>. A later release of Kiln will not "
             "load cooked files no cook of this game made (kiln.json \"cooked\": \"local\" does so now)",
             mod_dir.c_str(), unmade, unmade == 1 ? "" : "s", unmade == 1 ? "was" : "were", unmade_names.c_str(), unmade > 3 ? ", ..." : "");
    if (not_here)
        LOGW("assets: %s: %d cooked file%s not loaded (%s%s): no cook of this game made %s. Cooked files are made on each PC from its own "
             "game, never taken from a download or a copy: the launcher cooks the mod before the game starts; on a dedicated server run "
             "kiln cook <mod folder> --game <server folder>",
             mod_dir.c_str(), not_here, not_here == 1 ? "" : "s", not_here_names.c_str(), not_here > 3 ? ", ..." : "",
             not_here == 1 ? "it" : "them");
    // A model's entry needs no mark (it is skipped by its type). The cook of Kiln 0.2.0-dev.26 wrote the mark into a server's copy of
    // assets.json, which made that copy another file than the players'. A mod may also ship the mark itself, so this is a note.
    if (marked_models)
        LOGI("assets: %s: %d model entr%s in assets/assets.json carr%s a \"side\": \"client\" mark. If a cook of Kiln 0.2.0-dev.26 wrote it on "
             "this server (the mod ships the file without it), players' games see another mod than this server's: run kiln cook for this mod "
             "again (it takes that cook's marks out), or put the mod's own assets/assets.json back",
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
        if (!parse_hash(spec.substr(5), &hk)) {
            *report = "bad blob spec (want blob:<decimal size>-<h0>-<h1>-<h2>, the words in hex)";
            return KILN_ERR_INVALID_ARGUMENT;
        }
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
    std::map<KilnOwner, std::string> required;
    {
        std::lock_guard lock(g_pending_mu);
        pend.swap(g_pending_tex);
        icons.swap(g_pending_icons);
        required = g_boot_required_owner;
    }
    struct Late {
        std::string mod, key, why;
    };
    std::vector<Late> late;
    std::set<std::string> said;
    auto failed = [&](KilnOwner owner, const std::string& label, const std::string& why) {
        const auto it = required.find(owner);
        if (it != required.end() && said.insert(it->second).second) late.push_back({it->second, label, label + ": " + why});
    };
    for (auto& p : icons) {
        OriginScope origin(p.origin);
        if (is_game_icon(p.guid)) {  // registered as new at boot: the game has this key, so it is a game item's icon
            op_icon_remove(p.owner, p.guid);
            if (retexture_game_icon(p.owner, p.guid, p.img, p.label) != KILN_OK) failed(p.owner, p.label, "the game item's icon was not retextured");
        }
    }
    for (auto& p : pend) {
        OriginScope origin(KILN_BLOB_ORIGIN_MANIFEST);  // only load_manifest queues texture overrides
        std::string why;
        if (texture_override(p.owner, &p.key, p.png.data(), p.png.size(), &why) == KILN_OK) {
            LOGI("assets: %s applied", p.label.c_str());
        } else {
            LOGW("assets: %s failed: %s", p.label.c_str(), why.c_str());
            failed(p.owner, p.label, why);
        }
    }
    // a required boot mod's texture that could not be applied: kiln.boot stops the start (the call does not return then)
    for (const Late& l : late)
        kiln::boot::activation_fault(g_api, g_self, l.mod, "assets", l.key, "kiln.assets, when the game's resources came up: " + l.why);
}

namespace {

// A boot generation's part of kiln.assets (chapter 08): the owners its prepare loaded under, reverted by destroy.
struct BootPlan {
    std::vector<KilnOwner> owners;
    size_t manifests = 0, documents = 0;
};

// Every mod's assets.json of the load order. `boot`: a boot generation's prepare (kiln_boot_collector.hpp): the mods it
// excludes are left out, its assets documents follow each mod's file, and every owner goes into `plan`.
void load_profile_manifests(const kiln::boot::Input* boot = nullptr, BootPlan* plan = nullptr) {
    // Every mod folder in the load order: kiln.profile when present, else the legacy kiln/mods drop-ins.
    const KilnProfileApi* prof = static_cast<const KilnProfileApi*>(g_api->service(KilnStr{"kiln.profile", 12}, 1));
    int loaded = 0;
    // collected first: the cooked files of every enabled mod (with an assets.json or not: another mod's manifest can
    // reach a data-only mod's cooked folder) are known before any manifest is read
    std::vector<std::string> order;
    auto one = [&](const std::string& dir) { order.push_back(dir); };
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
    const CookTrust trust = read_cook_trust();  // read once for every mod of the load order
    std::vector<fs::path> paths;
    for (const auto& dir : order) paths.push_back(fs::path(widen(dir)));
    const CookedIds cooked_ids = cooked_ids_of(paths);
    for (const auto& c : cooked_ids.cut)
        LOGW("assets: a cooked folder could not be scanned whole (%s): a file a manifest names outside its own assets/cooked cannot be shown "
             "to be no other name for a cooked file, so under \"cooked\": \"local\" none is loaded, and under \"warn\" each is named", c.c_str());
    {
        std::lock_guard lock(g_loaded_mu);
        g_loaded_dirs = order;
    }
    for (const auto& dir : order) {
        std::error_code ec;
        const std::string id = boot ? kiln::boot::mod_id_of(fs::path(widen(dir))) : std::string();
        if (boot && !id.empty() && boot->out(id)) {
            LOGI("assets: %s is out of the boot generation: its assets.json is not loaded", id.c_str());
            continue;
        }
        // a required participant's entry errors refuse it (kiln_boot_collector.hpp, entry_error); another mod's are logged and skipped
        auto strict = [&](KilnResult rc, const std::string& key, const std::string& rep, const std::vector<std::string>& errs) {
            if (!boot || id.empty() || (rc == KILN_OK && errs.empty())) return;
            const std::string first = !errs.empty() ? errs.front() : rep;
            const std::string more = errs.size() > 1 ? " (and " + std::to_string(errs.size() - 1) + " more entry error(s), kiln.log)" : "";
            kiln::boot::entry_error(boot, id, "assets", key, "kiln.assets: " + key + ": " + first + more);
        };
        auto mark = [&](KilnOwner o) {  // a required participant's owner: its textures applied later are checked (flush_pending_textures)
            if (!boot || id.empty() || !boot->strict(id)) return;
            std::lock_guard lock(g_pending_mu);
            g_boot_required_owner[o] = id;
        };
        if (fs::exists(fs::path(widen(dir)) / L"assets" / L"assets.json", ec)) {
            KilnOwner o = g_api->owner_create(KilnStr{dir.c_str(), dir.size()});
            if (plan) plan->owners.push_back(o);
            mark(o);
            std::string rep;
            std::vector<std::string> errs;
            const KilnResult rc = load_manifest(o, dir, &rep, &trust, &cooked_ids, nullptr, &errs);
            if (rc == KILN_OK) ++loaded;
            else LOGW("assets: %s: assets/assets.json is not loaded: %s", dir.c_str(), rep.c_str());
            if (plan) ++plan->manifests;
            strict(rc, "assets/assets.json", rep, errs);
        }
        if (!boot || id.empty()) continue;
        for (const auto& d : boot->docs("assets", id)) {  // the mod's OnBoot code: after its own file
            if (!d.document.is_object() || (d.document.contains("entries") && !d.document["entries"].is_array()))
                throw kiln::boot::Refusal(id, "assets", d.key, "kiln.assets: a boot document is not an assets.json manifest");
            const auto it = boot->owner.find(id);
            const KilnOwner o = it != boot->owner.end() && it->second ? it->second : g_api->owner_create(KilnStr{dir.c_str(), dir.size()});
            if (std::find(plan->owners.begin(), plan->owners.end(), o) == plan->owners.end()) plan->owners.push_back(o);
            mark(o);
            std::string rep;
            std::vector<std::string> errs;
            const KilnResult rc = load_manifest(o, dir, &rep, &trust, &cooked_ids, &d.document, &errs);
            if (rc != KILN_OK) LOGW("assets: %s: boot document %s: %s", id.c_str(), d.key.c_str(), rep.c_str());
            ++plan->documents;
            strict(rc, d.key, rep, errs);
        }
    }
    if (loaded) LOGI("assets: loaded assets.json for %d mod(s)", loaded);
}

// Prepare (kiln.boot's commit): the manifests that on_ready loads without a boot profile, plus the generation's
// documents, under owners nothing reads before the game's entry point; destroy reverts them.
void KILN_CALL destroy_boot(const void* p) {
    const auto* plan = static_cast<const BootPlan*>(p);
    for (const KilnOwner o : plan->owners) {
        op_revert_owner(o);
        std::lock_guard lock(g_pending_mu);
        std::erase_if(g_pending_tex, [&](const PendingTexture& t) { return t.owner == o; });
        std::erase_if(g_pending_icons, [&](const PendingIcon& i) { return i.owner == o; });
        g_boot_required_owner.erase(o);
    }
    {
        std::lock_guard lock(g_loaded_mu);
        g_loaded_dirs.clear();
    }
    LOGI("assets: the boot generation's prepared manifests are reverted (%zu owner(s))", plan->owners.size());
    delete plan;
}

KilnResult KILN_CALL prepare_boot(void*, const KilnBootPrepareInputV1* in, KilnBootPreparedV1* out, KilnBootIssueV1* issue) {
    const kiln::boot::Input input = kiln::boot::parse(in);
    if (!input.ok) return kiln::boot::refuse(out, issue, "", "", "", "kiln.assets: the boot envelope is not JSON");
    auto* plan = new BootPlan;
    try {
        load_profile_manifests(&input, plan);
    } catch (const kiln::boot::Refusal& r) {
        destroy_boot(plan);  // what was loaded before the refusal goes with it
        return kiln::boot::refuse(out, issue, r.mod, r.kind, r.key, r.what());
    }
    LOGI("assets: boot generation %llu prepared: %zu manifest(s), %zu document(s)", static_cast<unsigned long long>(input.generation),
         plan->manifests, plan->documents);
    return kiln::boot::prepared(out, plan, &destroy_boot);
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
    std::string s = "{" + store_status_json() + ",\"hooks\":" + hooks_status_json() + ",\"loadedWrites\":" + loaded_writes_json() + "}";
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
KilnResult KILN_CALL t_blob_add_ex(KilnOwner o, const void* d, size_t n, KilnContentHash* h, uint32_t origin) {
    return op_blob_add_ex(o, d, n, h, origin);
}
KilnResult KILN_CALL t_blob_over_ex(KilnOwner o, const KilnContentHash* h, const void* d, size_t n, uint32_t origin) {
    return op_blob_override_ex(o, h, d, n, origin);
}
KilnResult KILN_CALL t_cat_open(KilnOwner o, uint32_t mode, KilnHandle* h, KilnBlobCatalogInfo* info) {
    return op_catalog_open(o, mode, h, info);
}
KilnResult KILN_CALL t_cat_read(KilnHandle h, uint64_t first, KilnBlobCatalogEntry* out, uint32_t entry_size, size_t cap, size_t* n) {
    return op_catalog_read(h, first, out, entry_size, cap, n);
}
KilnResult KILN_CALL t_cat_close(KilnHandle h) { return op_catalog_close(h); }

// texture_rgba (0.2.0-dev.29.15): a texture's pixels, one mip level, as RGBA8 (texture_rgba.h). Kiln.Ui draws the game's own
// item icons with it; nothing is written anywhere. The pixel blob is read whole (an item icon's chain is 87 KB), without the
// content map's lazy accessor (the caller is a worker). A level no mip fits (a mod's one-level PNG icon) is scaled down.
KilnResult KILN_CALL t_tex_rgba(const KilnAssetKey* k, uint32_t min_side, uint8_t* rgba, size_t cap, uint32_t* out_w, uint32_t* out_h) {
    if (out_w) *out_w = 0;
    if (out_h) *out_h = 0;
    if (!k || (cap && !rgba)) return KILN_ERR_INVALID_ARGUMENT;
    if (!resources_on()) return KILN_ERR_UNAVAILABLE;
    std::vector<uint8_t> rb(256);
    size_t rn = 0;
    KilnResult rr = op_resource_read(k, rb.data(), rb.size(), &rn);
    if (rr == KILN_ERR_BUFFER_TOO_SMALL && rn <= (1u << 20)) {
        rb.resize(rn);
        rr = op_resource_read(k, rb.data(), rb.size(), &rn);
    }
    if (rr != KILN_OK) return rr == KILN_ERR_BUFFER_TOO_SMALL ? KILN_ERR_INVALID_ARGUMENT : rr;
    tex::Desc d;
    if (!tex::describe(k->type, rb.data(), rn, &d) || !tex::supported(d.format)) return KILN_ERR_INVALID_ARGUMENT;
    uint32_t lw = 0, lh = 0, ow = 0, oh = 0;
    const uint32_t level = tex::pick_level(d, min_side);
    const size_t at = tex::level_offset(d, level, &lw, &lh), need = tex::level_bytes(d.format, lw, lh);
    tex::fit_size(lw, lh, min_side, &ow, &oh);
    if (out_w) *out_w = ow;
    if (out_h) *out_h = oh;
    if (!rgba || cap < static_cast<size_t>(ow) * oh * 4) return KILN_ERR_BUFFER_TOO_SMALL;
    if (at + need > d.blob_size() || d.blob_size() > (64u << 20)) return KILN_ERR_INVALID_ARGUMENT;  // the resource does not add up
    std::vector<uint8_t> blob(d.blob_size());
    KilnContentHash h;
    std::memcpy(&h, d.hash, 16);
    size_t got = 0;
    const KilnResult br = op_blob_read(&h, blob.data(), blob.size(), &got, /*resident=*/true);
    if (br != KILN_OK) return br == KILN_ERR_INTERNAL ? KILN_ERR_INTERNAL : KILN_ERR_NOT_FOUND;
    if (got < at + need) return KILN_ERR_NOT_FOUND;
    if (ow == lw && oh == lh) return tex::decode(d.format, lw, lh, blob.data() + at, got - at, rgba) ? KILN_OK : KILN_ERR_NOT_FOUND;
    std::vector<uint8_t> level_px(static_cast<size_t>(lw) * lh * 4);
    if (!tex::decode(d.format, lw, lh, blob.data() + at, got - at, level_px.data())) return KILN_ERR_NOT_FOUND;
    tex::downscale_box(level_px.data(), lw, lh, rgba, ow, oh);
    return KILN_OK;
}

// blob_read_resident (0.2.0-dev.29.15): blob_read without the content map's lazy accessor, for callers off the game's threads.
KilnResult KILN_CALL t_blob_read_resident(const KilnContentHash* h, void* b, size_t c, size_t* n) {
    return op_blob_read(h, b, c, n, /*resident=*/true);
}

// loaded_snapshot, loaded_list (0.2.0-dev.30.3): the resources the game has loaded now, from any thread (loaded_prod.cpp).
KilnResult KILN_CALL t_loaded_snapshot(KilnOwner o, const KilnAssetKey* k, void* b, size_t c, size_t* n, KilnLoadedInfo* i) {
    return op_loaded_snapshot(o, k, b, c, n, i);
}
KilnResult KILN_CALL t_loaded_list(KilnOwner o, uint32_t t, KilnAssetKey* out, size_t c, size_t* n, KilnLoadedInfo* i) {
    return op_loaded_list(o, t, out, c, n, i);
}

KilnResult KILN_CALL t_loaded_cas(KilnOwner o, const KilnAssetKey* k, uint64_t session, const void* expected, size_t size,
                                  size_t offset, const void* desired, uint32_t width, KilnMutationReply* reply) {
    return op_loaded_scalar_cas(o, k, session, expected, size, offset, desired, width, reply);
}
// The named loaded writes (sfi R3): the same write, its record naming the writer and field; the records (loaded_prod.cpp).
KilnResult KILN_CALL t_loaded_cas_named(KilnOwner o, const KilnAssetKey* k, uint64_t session, const void* expected, size_t size,
                                        size_t offset, const void* desired, uint32_t width, KilnStr writer, KilnStr field,
                                        KilnMutationReply* reply) {
    return op_loaded_scalar_cas_named(o, k, session, expected, size, offset, desired, width, writer, field, reply);
}
KilnResult KILN_CALL t_loaded_write_records(KilnLoadedWriteRecord* out, uint32_t record_size, size_t capacity, size_t* required) {
    return op_loaded_write_records(out, record_size, capacity, required);
}

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
    &t_blob_add_ex, &t_blob_over_ex,  // since 0.2.0-dev.28
    &t_cat_open, &t_cat_read, &t_cat_close,
    &t_tex_rgba,  // since 0.2.0-dev.29.15
    &t_blob_read_resident,
    &t_loaded_snapshot, &t_loaded_list, &t_loaded_cas,  // since 0.2.0-dev.30.3
    &t_loaded_cas_named, &t_loaded_write_records,       // since 0.2.0-dev.32
};

const KilnAssetsVfxApi kVfxTable = {
    {sizeof(KilnAssetsVfxApi), KILN_ASSETS_VFX_VERSION}, &op_vfx_budget, &op_vfx_set_capacity, &op_vfx_resource_put,
};

// ---- control commands: kiln/control/assets.cmd -> assets.result.json (handled by the core's console) ------------
// We expose "assets dump <spec>" / "assets status" through a tick task that polls the control folder ourselves is
// heavy; instead the managed Kiln.Assets exposes Assets.Dump via the table. A native console command would need a
// core hook we do not own, so the dump is reachable through C# and through the table's dump() for the models worker.

void KILN_CALL on_ready(void*) {
    const bool boot_profile = kiln::boot::collect(g_api, g_self, "assets", &prepare_boot, nullptr);
    // The resource system may already be up (services load after the game's early init on the client); try to bind.
    if (uintptr_t S = g_S.load()) on_resource_system_ready(S);
    // Load every mod's assets.json now, before the UI builds, so boot-time icons are served from the first frame. A boot
    // profile (chapter 08) loads them in kiln.boot's prepare instead, after OnBoot, still before the game's entry point.
    if (boot_profile)
        LOGI("assets: boot profile: the mods' assets.json load with the boot generation");
    else
        load_profile_manifests();
    std::string why;
    if (content_on()) {
        if (!verify_content_map(&why)) LOGW("assets: content map check: %s (blob reads may be unreliable)", why.c_str());
    }
    loaded_report_capability();
    LOGI("assets: ready; %s", (std::string("{") + store_status_json() + "}").c_str());
}

}  // namespace
}  // namespace ka

using namespace ka;

// 16f: an admitted authored body's cooked resources for its mod's boot-pack owner (bootpack_internal.h).
extern "C" KILN_EXPORT KilnResult KILN_CALL ka_boot_body_v1(KilnOwner owner, const char* mod_dir, size_t dir_length, const char* text,
                                                           size_t length, char* report, size_t capacity, size_t* written) {
    const auto reply = [&](KilnResult result, const std::string& message) {
        if (written) *written = message.size();
        if (report && capacity) {
            const size_t n = std::min(capacity - 1, message.size());
            std::memcpy(report, message.data(), n);
            report[n] = 0;
        }
        return result;
    };
    try {
        if (!owner || !mod_dir || !dir_length || !text) return reply(KILN_ERR_INVALID_ARGUMENT, "owner, mod folder and entries are required");
        const json doc = json::parse(text, text + length, nullptr, false);
        if (doc.is_discarded() || !doc.is_object() || !doc.contains("entries") || !doc["entries"].is_array())
            return reply(KILN_ERR_INVALID_ARGUMENT, "the body entries are not {\"entries\": [...]}");
        for (const json& e : doc["entries"]) {
            const std::string kind = e.is_object() ? e.value("kind", "") : std::string();
            if ((kind != "resource" && kind != "blob") || e.contains("data") || e.contains("hash") ||
                (kind == "resource" && e.value("mode", "") != "new"))
                return reply(KILN_ERR_INVALID_ARGUMENT, "a body entry must be a new resource or a blob of the mod's own files");
        }
        std::string rep;
        std::vector<std::string> errors;
        const KilnResult rc = ka::load_manifest(owner, std::string(mod_dir, dir_length), &rep, nullptr, nullptr, &doc, &errors);
        if (rc != KILN_OK) return reply(rc, rep);
        if (!errors.empty()) return reply(KILN_ERR_INVALID_ARGUMENT, errors.front());
        return reply(KILN_OK, rep);
    } catch (const std::exception& error) {
        return reply(KILN_ERR_INTERNAL, std::string("body entries failed: ") + error.what());
    } catch (...) {
        return reply(KILN_ERR_INTERNAL, "body entries failed with an unknown error");
    }
}

static_assert(std::is_same_v<decltype(&ka_boot_body_v1), ka::bootpack::Call>, "bootpack_internal.h and the export agree");

// 16f review3 S4: the NPC body cache is a cooked file: the same verdict as every file of a mod's assets/cooked.
extern "C" KILN_EXPORT int KILN_CALL ka_cooked_verdict_v1(const void* data, size_t length) {
    try {
        if (!data && length) return 2;
        switch (cooked_verdict(read_cook_trust(), data, length)) {
        case CookedVerdict::Load: return 0;
        case CookedVerdict::LoadUnmade: return 1;
        default: return 2;
        }
    } catch (...) {
        return 2;
    }
}

static_assert(std::is_same_v<decltype(&ka_cooked_verdict_v1), ka::bootpack::Verdict>, "bootpack_internal.h and the export agree");

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_self = owner;
    if (!catalog_start())
        LOGW("assets: blob catalog off: native owner cleanup is unavailable (a revoked owner's registrations stay until revert_owner)");
    info->id = KilnStr{KILN_ASSETS_SERVICE, sizeof(KILN_ASSETS_SERVICE) - 1};
    info->version = KilnStr{"1", 1};
    info->on_ready = &on_ready;

    KilnBuildInfo bi{sizeof bi};
    api->build_info(&bi);
    g_client = bi.side == KILN_SIDE_CLIENT;
    // Census only the original metadata; loader additions are accounted again against the actual type
    // enumeration at bind/list time. Never read the executable or infer a larger capacity from options.
    const std::string game_dir = api_path(KILN_PATH_GAME);
    fs::path archive = game_dir.empty() ? fs::path{} : fs::path(widen(game_dir)) / (g_client ? L"enshrouded.kfc" : L"enshrouded_server.kfc");
    fs::path original = archive;
    original += L".bak";
    std::error_code ec;
    if (!game_dir.empty() && (fs::exists(original, ec) || ec)) archive = original;
    int64_t capacity = 0;
    constexpr char cap_name[] = "caps/vfx.map_limit_request";
    if (api->constant(KilnStr{cap_name, sizeof cap_name - 1}, &capacity) != KILN_OK || capacity <= 0 || capacity > (1 << 20))
        capacity = 2048;
    vfx_preflight_start(archive.wstring(), static_cast<uint32_t>(capacity));
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
    KilnResult rc = api->register_service(owner, KilnStr{KILN_ASSETS_SERVICE, sizeof(KILN_ASSETS_SERVICE) - 1}, &kTable);
    if (rc != KILN_OK) return rc;
    return api->register_service(owner, KilnStr{KILN_ASSETS_VFX_SERVICE, sizeof(KILN_ASSETS_VFX_SERVICE) - 1}, &kVfxTable);
}
