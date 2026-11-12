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
std::atomic<const ResView*> g_res{nullptr};
std::atomic<const ContentSnap*> g_content{nullptr};
std::atomic<uint64_t> g_content_gen{0};
std::atomic<const IconSnap*> g_icons{nullptr};
std::atomic<uint64_t> g_icon_want{0};
std::atomic<uint64_t> g_icon_served{0};

// ---- readers and reclamation ----------------------------------------------------------------------------------
namespace {
std::atomic<uint64_t> g_epoch{0};
std::atomic<int64_t> g_inside[2];  // readers inside, by the parity of the epoch they entered in

// Returns once no reader can still use what was unpublished before the call (a writer, under g_mu). Readers are short: a hook's lookup.
void synchronize() {
    const uint64_t e = g_epoch.fetch_add(1);
    for (int spins = 0; g_inside[e & 1].load() != 0; ++spins) {
        if (spins < 64) YieldProcessor();
        else Sleep(0);
    }
}
}  // namespace

ReadGuard::ReadGuard() {
    for (;;) {
        epoch_ = g_epoch.load();
        g_inside[epoch_ & 1].fetch_add(1);
        if (g_epoch.load() == epoch_) return;  // counted in the epoch that is current: a writer that moves on waits for it
        g_inside[epoch_ & 1].fetch_sub(1);
    }
}

ReadGuard::~ReadGuard() { g_inside[epoch_ & 1].fetch_sub(1); }

const Served* ResView::key(const Key& k) const {
    if (auto it = delta->keys.find(k); it != delta->keys.end()) return &it->second;
    auto it = base->keys.find(k);
    return it == base->keys.end() ? nullptr : &it->second;
}

const Served* ResView::handle(uint32_t h) const {
    if (auto it = delta->handles.find(h); it != delta->handles.end()) return &it->second;
    auto it = base->handles.find(h);
    return it == base->handles.end() ? nullptr : &it->second;
}

const Key* ResView::synth(uint32_t h) const {
    if (auto it = delta->synth_keys.find(h); it != delta->synth_keys.end()) return &it->second;
    auto it = base->synth_keys.find(h);
    return it == base->synth_keys.end() ? nullptr : &it->second;
}

bool ResView::ever_new(const Key& k) const { return delta->ever_new.count(k) || base->ever_new.count(k); }

size_t ResView::new_count(uint32_t type) const {
    size_t n = 0;
    for (const ResSnap* s : {base, delta})
        if (auto it = s->ever_new_by_type.find(type); it != s->ever_new_by_type.end()) n += it->second;
    return n;
}

void ResView::new_keys(uint32_t type, std::vector<Key>* out) const {
    for (const ResSnap* s : {base, delta})
        if (auto it = s->new_by_type.find(type); it != s->new_by_type.end()) out->insert(out->end(), it->second.begin(), it->second.end());
}

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
// Keys put with KILN_ASSET_PUT_PIN, and the owner that pinned each: process-lived. A pinned key's item is the only item of that key (a
// pin is refused while another owner has one) and stays: no put, no removal and no revert_owner takes it, the pinning owner's included.
std::unordered_map<Key, KilnOwner, KeyHash> g_pinned;
std::unordered_map<Key, std::vector<size_t>, KeyHash> g_item_at;  // g_items indices by key
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
    if (old) {
        synchronize();
        delete old;  // the blobs' bytes stay (the game reads them), and so do the merged content maps (hooks.cpp)
    }
}

void bind_item_locked(ResItem& it, uintptr_t S) {
    if (it.bound || !S) return;
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
            return;
        }
        it.is_new = false;
        it.handle = gh;
    } else {
        if (want_over) {
            LOGW("assets: %s is not in the game; 'override' skipped", key_str(it.key).c_str());
            it.bound = true;
            return;
        }
        it.is_new = true;
        it.handle = kSynthBase + g_next_synth++;
    }
    it.bound = true;
}

void bind_resources_locked(uintptr_t S) {
    for (auto& it : g_items) bind_item_locked(it, S);
}

void reindex_locked() {
    g_item_at.clear();
    for (size_t i = 0; i < g_items.size(); ++i) g_item_at[g_items[i].key].push_back(i);
}

// One bound item into a level of the index; `known` (the base, for a delta): what the level adds to.
void add_served(ResSnap* s, const ResItem& it, const ResSnap* known) {
    const Served sv{it.bytes, it.size, it.handle, it.is_new};
    s->keys[it.key] = sv;
    if (it.handle) s->handles[it.handle] = sv;
    if (it.is_new) {
        if (!(known && known->ever_new.count(it.key)) && s->ever_new.emplace(it.key, true).second) ++s->ever_new_by_type[it.key.type];
        s->new_by_type[it.key.type].push_back(it.key);
        s->synth_keys[it.handle] = it.key;
    }
}

// Publishes {base, delta}; what the old view had and the new one does not is freed once no reader can hold it.
void publish_locked(const ResSnap* base, const ResSnap* delta) {
    static uint64_t gen = 0;
    auto* v = new ResView();
    v->gen = ++gen;
    v->base = base;
    v->delta = delta;
    const ResView* old = g_res.exchange(v, std::memory_order_acq_rel);
    if (!old) return;
    synchronize();
    if (old->base != base) delete old->base;
    if (old->delta != delta) delete old->delta;
    delete old;  // the merged key lists it handed out stay: the game holds them
}

// Everything again: a new base from every bound item, an empty delta.
void rebuild_resources_locked() {
    auto* b = new ResSnap();
    if (const ResView* cur = g_res.load(std::memory_order_acquire)) {  // keys once new stay so
        b->ever_new = cur->base->ever_new;
        b->ever_new_by_type = cur->base->ever_new_by_type;
        for (const auto& [k, unused] : cur->delta->ever_new)
            if (b->ever_new.emplace(k, true).second) ++b->ever_new_by_type[k.type];
    }
    for (const auto& it : g_items)
        if (it.bound) add_served(b, it, nullptr);
    publish_locked(b, new ResSnap());
}

// A bound item with a key no other item has, at the end of g_items: into a copy of the small delta; the base is built again when the
// delta has grown, so a put costs about the same however many resources there are.
constexpr size_t kDeltaMax = 64;
void add_resource_locked(const ResItem& it) {
    const ResView* cur = g_res.load(std::memory_order_acquire);
    if (!cur || cur->delta->keys.size() >= kDeltaMax) return rebuild_resources_locked();
    auto* d = new ResSnap(*cur->delta);
    add_served(d, it, cur->base);
    publish_locked(cur->base, d);
}

void rebuild_icons_locked() {
    auto* ic = new IconSnap();
    static std::atomic<uint64_t> gen{0};
    ic->gen = gen.fetch_add(1) + 1;
    for (auto& i : g_iconv) ic->state[i.guid] = i.state;
    const IconSnap* old = g_icons.exchange(ic, std::memory_order_acq_rel);
    if (old) {
        synchronize();
        delete old;
    }
    g_icon_want.fetch_add(1);  // the UI cache should refill to learn new icon keys
}

// resource_put core (under g_mu). Replaces this owner's earlier resource for the same key.
KilnResult put_resource_locked(KilnOwner owner, const Key& k, const void* data, size_t len, uint32_t flags) {
    const uint8_t* copy = keep_copy(data, len);
    if (!copy && len) return KILN_ERR_INTERNAL;
    std::vector<size_t>& at = g_item_at[k];
    for (size_t i : at)
        if (ResItem& it = g_items[i]; it.owner == owner) {
            it.bytes = copy;
            it.size = len;
            it.flags = flags;
            it.bound = false;  // rebind (bytes changed; a new override keeps its handle after bind)
            return KILN_OK;
        }
    at.push_back(g_items.size());
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

// The player's animation graph: 21319e83-5087-4fff-8795-22997843649e (game byte order below), keen::anim_graph::runtime_graph::
// AnimationGraphResource2_0, part 0. Reserved: only a pinning put takes it (kiln.playeranim's merge, kiln_assets.h).
bool player_graph_key(const Key& k) {
    static const uint8_t g[16] = {0x83, 0x9e, 0x31, 0x21, 0x87, 0x50, 0xff, 0x4f, 0x87, 0x95, 0x22, 0x99, 0x78, 0x43, 0x64, 0x9e};
    return k.part == 0 && k.type == kPlayerGraphType && std::memcmp(k.g, g, 16) == 0;
}

uint8_t* keep_alloc(size_t n) { return static_cast<uint8_t*>(::operator new(n, std::nothrow)); }
const uint8_t* keep_copy(const void* data, size_t n) {
    uint8_t* p = keep_alloc(n);
    if (p && data) std::memcpy(p, data, n);
    return p;
}

// Services waiting for the resource service: each runs once, whichever way S was first seen.
namespace {
struct ReadyCallback {
    void(KILN_CALL* fn)(void*);
    void* user;
};
std::mutex g_ready_mu;
std::vector<ReadyCallback> g_ready;
bool g_ready_ran = false;

void run_ready_callbacks() {
    std::vector<ReadyCallback> run;
    {
        std::lock_guard lock(g_ready_mu);
        g_ready_ran = true;
        run.swap(g_ready);
    }
    for (const ReadyCallback& c : run) c.fn(c.user);
}
}  // namespace

void on_resource_system_ready(uintptr_t S) {
    {
        std::lock_guard lock(g_mu);
        bind_resources_locked(S);
        rebuild_resources_locked();
        if (!g_items.empty()) LOGI("assets: bound %zu resource(s) to the game", g_items.size());
    }
    // Outside g_mu: both read vanilla resources (needs S) and take g_mu themselves.
    run_ready_callbacks();
    flush_pending_textures();
}

KilnResult op_when_resources_ready(void(KILN_CALL* fn)(void*), void* user) {
    if (!fn) return KILN_ERR_INVALID_ARGUMENT;
    if (!resources_on()) return KILN_ERR_UNAVAILABLE;
    {
        std::lock_guard lock(g_ready_mu);
        if (!g_ready_ran) {
            g_ready.push_back({fn, user});
            return KILN_OK;
        }
    }
    fn(user);
    return KILN_OK;
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
    // Refusals come before anything is added: a pinned key, or the reserved player graph, never gets a second item.
    if (const auto p = g_pinned.find(k); p != g_pinned.end()) {
        LOGW("assets: owner %llu resource %s refused: key pinned for this process by owner %llu",
             static_cast<unsigned long long>(owner), key_str(k).c_str(), static_cast<unsigned long long>(p->second));
        return KILN_ERR_UNAVAILABLE;
    }
    if (player_graph_key(k) && !(flags & KILN_ASSET_PUT_PIN)) {
        LOGW("assets: owner %llu resource %s refused: player graph is reserved for kiln.playeranim; use player-states.json",
             static_cast<unsigned long long>(owner), key_str(k).c_str());
        return KILN_ERR_UNAVAILABLE;
    }
    const auto known = g_item_at.find(k);
    const bool fresh = known == g_item_at.end() || known->second.empty();  // no item has this key yet
    if ((flags & KILN_ASSET_PUT_PIN) && !fresh)  // the later item of a key is served: a pin over another owner's item could lose to it
        for (size_t i : known->second)
            if (g_items[i].owner != owner) {
                LOGW("assets: owner %llu resource %s pin refused: owner %llu has an item of that key",
                     static_cast<unsigned long long>(owner), key_str(k).c_str(), static_cast<unsigned long long>(g_items[i].owner));
                return KILN_ERR_UNAVAILABLE;
            }
    KilnResult r = put_resource_locked(owner, k, data, len, flags);
    if (r != KILN_OK) return r;
    if (flags & KILN_ASSET_PUT_PIN) g_pinned.emplace(k, owner);
    const uintptr_t S = g_S.load();
    if (fresh) {
        ResItem& it = g_items.back();
        bind_item_locked(it, S);
        if (it.bound) add_resource_locked(it);
    } else {  // a replaced resource, or another owner's under the same key: everything again, as the order of items decides
        bind_resources_locked(S);
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
    if (const auto p = g_pinned.find(k); p != g_pinned.end()) {
        LOGW("assets: owner %llu resource %s removal refused: key pinned for this process by owner %llu",
             static_cast<unsigned long long>(owner), key_str(k).c_str(), static_cast<unsigned long long>(p->second));
        return KILN_ERR_UNAVAILABLE;
    }
    const size_t n = g_items.size();
    g_items.erase(std::remove_if(g_items.begin(), g_items.end(),
                                 [&](const ResItem& it) { return it.owner == owner && it.key == k; }),
                  g_items.end());
    if (g_items.size() != n) {
        reindex_locked();
        rebuild_resources_locked();
    }
    return KILN_OK;
}

KilnResult op_resource_read(const KilnAssetKey* key, void* buffer, size_t cap, size_t* out_len) {
    if (!key) return KILN_ERR_INVALID_ARGUMENT;
    Key k;
    std::memcpy(k.g, key->guid, 16);
    k.type = key->type;
    k.part = key->part;
    // an override / new resource first
    {
        ReadGuard g;
        if (const ResView* v = g_res.load(std::memory_order_acquire))
            if (const Served* sv = v->key(k)) {
                if (out_len) *out_len = sv->size;
                if (cap < sv->size) return KILN_ERR_BUFFER_TOO_SMALL;
                std::memcpy(buffer, sv->data, sv->size);
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
    {
        ReadGuard g;
        if (const ResView* v = g_res.load(std::memory_order_acquire))
            if (const Served* sv = v->key(k)) {
                *ptr = sv->data;  // the bytes are never freed
                *len = sv->size;
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
    ReadGuard g;
    if (const ResView* v = g_res.load(std::memory_order_acquire)) return v->ever_new(k) ? 1 : 0;
    return 0;
}

size_t op_resource_new_count(uint32_t type) {
    ReadGuard g;
    const ResView* v = g_res.load(std::memory_order_acquire);
    return v ? v->new_count(type) : 0;
}

KilnResult op_resource_list(uint32_t type, KilnAssetKey* out, size_t cap, size_t* out_count) {
    const uintptr_t S = g_S.load();
    const uint8_t* keys = nullptr;
    uint64_t gc = 0;
    game_type_list(S, type, &keys, &gc);
    std::vector<Key> extra;
    {
        ReadGuard g;
        if (const ResView* v = g_res.load(std::memory_order_acquire)) v->new_keys(type, &extra);
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
    {
        ReadGuard g;
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
    if (g_pinned.count(k)) {
        LOGW("assets: owner %llu icon %s refused: key pinned for this process", static_cast<unsigned long long>(owner), key_str(k).c_str());
        return KILN_ERR_UNAVAILABLE;
    }
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
    if (!g_pinned.count(k))  // a pinned UI texture key keeps its item (the icon's UI state goes as above)
        g_items.erase(std::remove_if(g_items.begin(), g_items.end(),
                                     [&](const ResItem& it) { return it.owner == owner && it.key == k; }),
                      g_items.end());
    reindex_locked();
    rebuild_resources_locked();
    return KILN_OK;
}

int32_t op_icon_state(const uint8_t guid[16]) {
    ReadGuard reading;
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
    re(g_items, [&](const ResItem& it) { return it.owner == owner && !g_pinned.count(it.key); }, res);
    re(g_blobs, [&](const BlobItem& b) { return b.owner == owner; }, blob);
    for (auto& i : g_iconv)
        if (i.owner == owner) i.state->store(KILN_ICON_NONE);
    re(g_iconv, [&](const IconItem& i) { return i.owner == owner; }, icon);
    if (res) reindex_locked(), rebuild_resources_locked();
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
