// SPDX-License-Identifier: MIT
// kiln.assets: the store (resources, content blobs, icons). The hooks (hooks.cpp) read immutable snapshots this file
// publishes under one lock. Memory the game may hold a pointer into is never freed.
//
// Everything reduces to two kinds of registration:
//  - a resource: bytes served for a KilnAssetKey, either new (synthetic handle, added to its type's list) or an
//    override of a game resource (keeps the game's handle, swaps the data).
//  - a content blob: bytes served for a ContentHash, either new (hash derived from the bytes) or an override of a
//    game hash. Resources reference blobs; the client streams a blob when it needs the resource's pixels / mesh.
// An icon is a blob (RGBA8 pixels) + a new UiTextureResource that references it, plus a tracked UI state for the GUID.
#include <algorithm>

#include "assets.h"

namespace ka {

const KilnApi* g_api = nullptr;
KilnOwner g_self = 0;
bool g_client = false;
std::atomic<const ResSnap*> g_res{nullptr};
std::atomic<const ContentSnap*> g_content{nullptr};
std::atomic<uint64_t> g_content_gen{0};
std::atomic<const IconSnap*> g_icons{nullptr};
std::atomic<uint64_t> g_icon_want{0};
std::atomic<uint64_t> g_icon_served{0};

namespace {

std::mutex g_mu;  // guards the item vectors and rebuilds

struct ResItem {
    KilnOwner owner;
    Key key;
    const uint8_t* bytes;  // keep_alloc'd (the game holds pointers): never freed
    size_t size;
    uint32_t flags;
    bool bound = false;
    bool is_new = false;
    uint32_t handle = 0;  // synthetic (new) or the game's (override)
};
struct BlobItem {
    KilnOwner owner;
    HashK hash;
    const uint8_t* bytes;
    size_t size;
    bool is_override;
};
struct IconItem {
    KilnOwner owner;
    Guid16 guid;
    std::shared_ptr<std::atomic<int32_t>> state;
    bool ok = false;  // false = FAILED (bad PNG, etc.)
};

std::vector<ResItem> g_items;
std::vector<BlobItem> g_blobs;
std::vector<IconItem> g_iconv;
uint32_t g_next_synth = 0;

// ---- snapshot builders (under g_mu) -----------------------------------------------------------------------------
void rebuild_content_locked() {
    auto* cs = new ContentSnap();
    cs->gen = g_content_gen.fetch_add(1) + 1;
    uint64_t cursor = 0;
    auto place = [&](size_t size) {
        const uint64_t off = cursor;
        cursor += (size + 0x3FFF) & ~uint64_t(0x3FFF);
        return off;
    };
    for (auto& b : g_blobs) {
        const uint64_t off = place(b.size);
        cs->entries[b.hash] = off;
        cs->extents.push_back({off, b.size, b.bytes});
    }
    std::sort(cs->extents.begin(), cs->extents.end(), [](const Extent& a, const Extent& b) { return a.offset < b.offset; });
    const ContentSnap* old = g_content.exchange(cs, std::memory_order_acq_rel);
    (void)old;  // kept: a reader mid-flight may still hold it; content snapshots are few.
}

void bind_resources_locked(uintptr_t S) {
    for (auto& it : g_items) {
        if (it.bound || !S) continue;
        const uint32_t gh = game_handle(S, it.key);
        const bool want_new = it.flags & KILN_ASSET_PUT_NEW;
        const bool want_over = it.flags & KILN_ASSET_PUT_OVERRIDE;
        if (gh) {
            if (want_new) {
                if (it.key.type == KILN_ASSET_TYPE_UI_TEXTURE)  // an icon for a game item: assets.cpp retextures it instead
                    LOGD("assets: %s is a game item's icon texture: retextured instead of a new one", key_str(it.key).c_str());
                else
                    LOGW("assets: %s already exists in the game; 'new' skipped", key_str(it.key).c_str());
                it.bound = true;
                continue;
            }
            it.is_new = false;
            it.handle = gh;
        } else {
            if (want_over) {
                LOGW("assets: %s is not in the game; 'override' skipped", key_str(it.key).c_str());
                it.bound = true;
                continue;
            }
            it.is_new = true;
            it.handle = kSynthBase + g_next_synth++;
        }
        it.bound = true;
    }
}

void rebuild_resources_locked() {
    auto* s = new ResSnap();
    static std::atomic<uint64_t> gen{0};
    s->gen = gen.fetch_add(1) + 1;
    if (const ResSnap* prev = g_res.load(std::memory_order_acquire)) s->ever_new = prev->ever_new;
    for (auto& it : g_items) {
        if (!it.bound) continue;
        Served sv{it.bytes, it.size, it.handle, it.is_new};
        s->keys[it.key] = sv;
        if (it.handle) s->handles[it.handle] = sv;
        if (it.is_new) {
            s->ever_new[it.key] = true;
            s->new_by_type[it.key.type].push_back(it.key);
            s->synth_keys[it.handle] = it.key;
        }
    }
    const ResSnap* old = g_res.exchange(s, std::memory_order_acq_rel);
    (void)old;
}

void rebuild_icons_locked() {
    auto* ic = new IconSnap();
    static std::atomic<uint64_t> gen{0};
    ic->gen = gen.fetch_add(1) + 1;
    for (auto& i : g_iconv) ic->state[i.guid] = i.state;
    const IconSnap* old = g_icons.exchange(ic, std::memory_order_acq_rel);
    (void)old;
    g_icon_want.fetch_add(1);  // the UI cache should refill to learn new icon keys
}

// resource_put core (under g_mu). Replaces this owner's earlier resource for the same key.
KilnResult put_resource_locked(KilnOwner owner, const Key& k, const void* data, size_t len, uint32_t flags) {
    const uint8_t* copy = keep_copy(data, len);
    if (!copy && len) return KILN_ERR_INTERNAL;
    for (auto& it : g_items)
        if (it.owner == owner && it.key == k) {
            it.bytes = copy;
            it.size = len;
            it.flags = flags;
            it.bound = false;  // rebind (bytes changed; a new override keeps its handle after bind)
            return KILN_OK;
        }
    g_items.push_back({owner, k, copy, len, flags});
    return KILN_OK;
}

KilnResult add_blob_locked(KilnOwner owner, const void* data, size_t len, const HashK* fixed, bool is_override,
                           HashK* out) {
    const HashK h = fixed ? *fixed : content_hash_of(data, len);
    if (is_override && fixed && fixed->size != len) return KILN_ERR_INVALID_ARGUMENT;
    const uint8_t* copy = keep_copy(data, len);
    if (!copy && len) return KILN_ERR_INTERNAL;
    for (auto& b : g_blobs)
        if (b.owner == owner && b.hash == h) {
            b.bytes = copy;
            b.size = len;
            b.is_override = is_override;
            if (out) *out = h;
            return KILN_OK;
        }
    g_blobs.push_back({owner, h, copy, len, is_override});
    if (out) *out = h;
    return KILN_OK;
}

}  // namespace

uint8_t* keep_alloc(size_t n) { return static_cast<uint8_t*>(::operator new(n, std::nothrow)); }
const uint8_t* keep_copy(const void* data, size_t n) {
    uint8_t* p = keep_alloc(n);
    if (p && data) std::memcpy(p, data, n);
    return p;
}

void on_resource_system_ready(uintptr_t S) {
    {
        std::lock_guard lock(g_mu);
        bind_resources_locked(S);
        rebuild_resources_locked();
        if (!g_items.empty()) LOGI("assets: bound %zu resource(s) to the game", g_items.size());
    }
    // Outside g_mu: a boot-time texture override reads vanilla resources (needs S) and takes g_mu itself.
    flush_pending_textures();
}

// hooks.cpp tells us the UI cache has taken in the key set of generation `gen_used`.
void icons_after_populate(uint64_t gen_used) {
    g_icon_served.store(gen_used);
    // set every icon of that generation (and earlier) to SERVED, unless it failed
    std::lock_guard lock(g_mu);
    for (auto& i : g_iconv)
        if (i.ok && i.state->load() == KILN_ICON_PENDING) i.state->store(KILN_ICON_SERVED);
    LOGI("assets: the UI texture cache refilled; %zu icon(s) now served", g_iconv.size());
}

// ---- public operations (called by the service table and the manifest loader) -----------------------------------
KilnResult op_resource_put(KilnOwner owner, const KilnAssetKey* key, const void* data, size_t len, uint32_t flags) {
    if (!key || (!data && len)) return KILN_ERR_INVALID_ARGUMENT;
    if (!resources_on()) return KILN_ERR_UNAVAILABLE;
    Key k;
    std::memcpy(k.g, key->guid, 16);
    k.type = key->type;
    k.part = key->part;
    std::lock_guard lock(g_mu);
    KilnResult r = put_resource_locked(owner, k, data, len, flags);
    if (r == KILN_OK) {
        bind_resources_locked(g_S.load());
        rebuild_resources_locked();
    }
    return r;
}

KilnResult op_resource_remove(KilnOwner owner, const KilnAssetKey* key) {
    if (!key) return KILN_ERR_INVALID_ARGUMENT;
    Key k;
    std::memcpy(k.g, key->guid, 16);
    k.type = key->type;
    k.part = key->part;
    std::lock_guard lock(g_mu);
    const size_t n = g_items.size();
    g_items.erase(std::remove_if(g_items.begin(), g_items.end(),
                                 [&](const ResItem& it) { return it.owner == owner && it.key == k; }),
                  g_items.end());
    if (g_items.size() != n) rebuild_resources_locked();
    return KILN_OK;
}

KilnResult op_resource_read(const KilnAssetKey* key, void* buffer, size_t cap, size_t* out_len) {
    if (!key) return KILN_ERR_INVALID_ARGUMENT;
    Key k;
    std::memcpy(k.g, key->guid, 16);
    k.type = key->type;
    k.part = key->part;
    // an override / new resource first
    if (const ResSnap* s = g_res.load(std::memory_order_acquire)) {
        auto it = s->keys.find(k);
        if (it != s->keys.end()) {
            if (out_len) *out_len = it->second.size;
            if (cap < it->second.size) return KILN_ERR_BUFFER_TOO_SMALL;
            std::memcpy(buffer, it->second.data, it->second.size);
            return KILN_OK;
        }
    }
    // else the game's own
    const uintptr_t S = g_S.load();
    const uint32_t h = game_handle(S, k);
    if (!h) return KILN_ERR_NOT_FOUND;
    DataSpan sp{};
    if (!game_data(S, h, &sp) || !sp.data) return KILN_ERR_NOT_FOUND;
    if (out_len) *out_len = sp.size;
    if (cap < sp.size) return KILN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(buffer, sp.data, sp.size);
    return KILN_OK;
}

// Zero-copy view: a Kiln override/new resource's bytes (stable, never freed), else the game's resident copy.
KilnResult op_resource_data(const KilnAssetKey* key, const void** ptr, size_t* len) {
    if (!key || !ptr || !len) return KILN_ERR_INVALID_ARGUMENT;
    Key k;
    std::memcpy(k.g, key->guid, 16);
    k.type = key->type;
    k.part = key->part;
    if (const ResSnap* s = g_res.load(std::memory_order_acquire)) {
        auto it = s->keys.find(k);
        if (it != s->keys.end()) {
            *ptr = it->second.data;
            *len = it->second.size;
            return KILN_OK;
        }
    }
    const uintptr_t S = g_S.load();
    const uint32_t h = game_handle(S, k);
    if (!h) return KILN_ERR_NOT_FOUND;
    DataSpan sp{};
    if (!game_data(S, h, &sp) || !sp.data) return KILN_ERR_NOT_FOUND;
    *ptr = sp.data;
    *len = static_cast<size_t>(sp.size);
    return KILN_OK;
}

int32_t op_resource_is_new(const KilnAssetKey* key) {
    if (!key) return 0;
    Key k;
    std::memcpy(k.g, key->guid, 16);
    k.type = key->type;
    k.part = key->part;
    if (const ResSnap* s = g_res.load(std::memory_order_acquire)) return s->ever_new.count(k) ? 1 : 0;
    return 0;
}

size_t op_resource_new_count(uint32_t type) {
    size_t n = 0;
    if (const ResSnap* s = g_res.load(std::memory_order_acquire))
        for (const auto& [key, unused] : s->ever_new) n += key.type == type ? 1 : 0;
    return n;
}

KilnResult op_resource_list(uint32_t type, KilnAssetKey* out, size_t cap, size_t* out_count) {
    const uintptr_t S = g_S.load();
    const uint8_t* keys = nullptr;
    uint64_t gc = 0;
    game_type_list(S, type, &keys, &gc);
    std::vector<Key> extra;
    if (const ResSnap* s = g_res.load(std::memory_order_acquire)) {
        auto it = s->new_by_type.find(type);
        if (it != s->new_by_type.end()) extra = it->second;
    }
    const size_t total = static_cast<size_t>(gc) + extra.size();
    if (out_count) *out_count = total;
    if (cap < total) return KILN_ERR_BUFFER_TOO_SMALL;
    for (uint64_t i = 0; i < gc; ++i) {
        std::memcpy(out[i].guid, keys + i * 32, 16);
        std::memcpy(&out[i].type, keys + i * 32 + 16, 4);
        std::memcpy(&out[i].part, keys + i * 32 + 20, 4);
    }
    for (size_t i = 0; i < extra.size(); ++i) {
        std::memcpy(out[gc + i].guid, extra[i].g, 16);
        out[gc + i].type = extra[i].type;
        out[gc + i].part = extra[i].part;
    }
    return KILN_OK;
}

KilnResult op_blob_add(KilnOwner owner, const void* data, size_t len, KilnContentHash* out) {
    if (!data && len) return KILN_ERR_INVALID_ARGUMENT;
    if (!content_hooks_on()) return KILN_ERR_UNAVAILABLE;
    std::lock_guard lock(g_mu);
    HashK h;
    KilnResult r = add_blob_locked(owner, data, len, nullptr, false, &h);
    if (r == KILN_OK) {
        rebuild_content_locked();
        if (out) std::memcpy(out, &h, 16);
    }
    return r;
}

KilnResult op_blob_override(KilnOwner owner, const KilnContentHash* hash, const void* data, size_t len) {
    if (!hash || (!data && len)) return KILN_ERR_INVALID_ARGUMENT;
    if (!content_hooks_on()) return KILN_ERR_UNAVAILABLE;
    HashK h;
    std::memcpy(&h, hash, 16);
    std::lock_guard lock(g_mu);
    KilnResult r = add_blob_locked(owner, data, len, &h, true, nullptr);
    if (r == KILN_OK) rebuild_content_locked();
    return r;
}

KilnResult op_blob_remove(KilnOwner owner, const KilnContentHash* hash) {
    if (!hash) return KILN_ERR_INVALID_ARGUMENT;
    HashK h;
    std::memcpy(&h, hash, 16);
    std::lock_guard lock(g_mu);
    const size_t n = g_blobs.size();
    g_blobs.erase(std::remove_if(g_blobs.begin(), g_blobs.end(),
                                 [&](const BlobItem& b) { return b.owner == owner && b.hash == h; }),
                  g_blobs.end());
    if (g_blobs.size() != n) rebuild_content_locked();
    return KILN_OK;
}

KilnResult op_blob_read(const KilnContentHash* hash, void* buffer, size_t cap, size_t* out_len) {
    if (!hash) return KILN_ERR_INVALID_ARGUMENT;
    HashK h;
    std::memcpy(&h, hash, 16);
    if (const ContentSnap* cs = g_content.load(std::memory_order_acquire)) {
        auto it = cs->entries.find(h);
        if (it != cs->entries.end())
            for (const Extent& e : cs->extents)
                if (e.offset == it->second) {
                    if (out_len) *out_len = e.size;
                    if (cap < e.size) return KILN_ERR_BUFFER_TOO_SMALL;
                    std::memcpy(buffer, e.data, e.size);
                    return KILN_OK;
                }
    }
    // the game's own blob: find it in the content map and read the container (from memory if it is Kiln's)
    GameBlob gb;
    if (!game_blob_find(h, &gb)) return KILN_ERR_NOT_FOUND;
    const std::string path = game_container_path(gb.container);
    if (path.empty()) return KILN_ERR_NOT_FOUND;
    std::vector<uint8_t> buf;
    std::string why;
    if (!read_file(widen(path), &buf, &why)) return KILN_ERR_INTERNAL;
    if (gb.offset + h.size > buf.size()) return KILN_ERR_NOT_FOUND;
    if (out_len) *out_len = h.size;
    if (cap < h.size) return KILN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(buffer, buf.data() + gb.offset, h.size);
    return KILN_OK;
}

// ---- icons ------------------------------------------------------------------------------------------------------
// Builds the UiTextureResource (RGBA8, 1 mip) + its pixel blob, keyed by the item GUID, and tracks the UI state.
KilnResult op_icon_register(KilnOwner owner, const uint8_t guid[16], const Rgba& img) {
    if (!g_client || !ui_on()) return KILN_ERR_UNAVAILABLE;
    Rgba sq = img;
    const uint32_t side = std::min(std::max({img.w, img.h, 1u}), 512u);
    if (img.w != side || img.h != side) sq = resize(img, side, side);
    // pixel blob (RGBA8)
    const HashK blob = content_hash_of(sq.px.data(), sq.px.size());
    // UiTextureResource: 56 bytes (docs/re/resources.md)
    uint8_t res[56] = {};
    std::memcpy(res, &blob, 16);           // ResourceContentReference.hash
    const uint32_t wh[2] = {sq.w, sq.h};
    std::memcpy(res + 0x28, wh, 8);        // size uint2
    res[0x30] = 1;                         // TextureType Texture2D
    res[0x31] = 37;                        // PixelFormat R8G8B8A8_unorm
    res[0x32] = 1;                         // levelCount
    res[0x33] = 0;                         // isTiled
    Key k;
    std::memcpy(k.g, guid, 16);
    k.type = KILN_ASSET_TYPE_UI_TEXTURE;
    k.part = 0;
    std::lock_guard lock(g_mu);
    put_resource_locked(owner, k, res, sizeof res, KILN_ASSET_PUT_NEW);
    add_blob_locked(owner, sq.px.data(), sq.px.size(), nullptr, false, nullptr);
    // track the GUID state
    Guid16 g;
    std::memcpy(g.g, guid, 16);
    IconItem* found = nullptr;
    for (auto& i : g_iconv)
        if (i.owner == owner && i.guid == g) found = &i;
    if (!found) {
        g_iconv.push_back({owner, g, std::make_shared<std::atomic<int32_t>>(KILN_ICON_PENDING), true});
        found = &g_iconv.back();
    } else {
        found->state->store(KILN_ICON_PENDING);
        found->ok = true;
    }
    bind_resources_locked(g_S.load());
    rebuild_resources_locked();
    rebuild_content_locked();
    rebuild_icons_locked();
    return KILN_OK;
}

KilnResult op_icon_remove(KilnOwner owner, const uint8_t guid[16]) {
    Guid16 g;
    std::memcpy(g.g, guid, 16);
    Key k;
    std::memcpy(k.g, guid, 16);
    k.type = KILN_ASSET_TYPE_UI_TEXTURE;
    k.part = 0;
    std::lock_guard lock(g_mu);
    for (auto& i : g_iconv)
        if (i.owner == owner && i.guid == g) i.state->store(KILN_ICON_NONE);
    g_iconv.erase(std::remove_if(g_iconv.begin(), g_iconv.end(),
                                 [&](const IconItem& i) { return i.owner == owner && i.guid == g; }),
                  g_iconv.end());
    g_items.erase(std::remove_if(g_items.begin(), g_items.end(),
                                 [&](const ResItem& it) { return it.owner == owner && it.key == k; }),
                  g_items.end());
    rebuild_resources_locked();
    return KILN_OK;
}

int32_t op_icon_state(const uint8_t guid[16]) {
    const IconSnap* s = g_icons.load(std::memory_order_acquire);
    if (!s) return KILN_ICON_NONE;
    Guid16 g;
    std::memcpy(g.g, guid, 16);
    auto it = s->state.find(g);
    return it == s->state.end() ? KILN_ICON_NONE : it->second->load(std::memory_order_relaxed);
}

void op_revert_owner(KilnOwner owner) {
    std::lock_guard lock(g_mu);
    bool res = false, blob = false, icon = false;
    auto re = [&](auto& v, auto pred, bool& flag) {
        const size_t n = v.size();
        v.erase(std::remove_if(v.begin(), v.end(), pred), v.end());
        flag = v.size() != n;
    };
    re(g_items, [&](const ResItem& it) { return it.owner == owner; }, res);
    re(g_blobs, [&](const BlobItem& b) { return b.owner == owner; }, blob);
    for (auto& i : g_iconv)
        if (i.owner == owner) i.state->store(KILN_ICON_NONE);
    re(g_iconv, [&](const IconItem& i) { return i.owner == owner; }, icon);
    if (res) rebuild_resources_locked();
    if (blob || icon) rebuild_content_locked();
    if (icon) rebuild_icons_locked();
}

std::string store_status_json() {
    std::lock_guard lock(g_mu);
    size_t new_res = 0, over_res = 0;
    for (auto& it : g_items) (it.flags & KILN_ASSET_PUT_OVERRIDE ? over_res : new_res)++;
    char b[384];
    std::snprintf(b, sizeof b, "\"resources\":%zu,\"resourcesNew\":%zu,\"resourcesOverride\":%zu,\"blobs\":%zu,\"icons\":%zu",
                  g_items.size(), new_res, over_res, g_blobs.size(), g_iconv.size());
    return b;
}

}  // namespace ka
