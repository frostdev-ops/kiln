// SPDX-License-Identifier: MIT
// kiln.assets: the content blob catalog (kiln_assets.h, blob_catalog_open/read/close; capability assets.blob_catalog).
// A snapshot is built from two copies: the keys of the game's own content index (hooks.cpp, game_index_keys) and the live
// registration ledger (store.cpp, blob_ledger, copied under the store lock). Neither copy is made while a catalog lock is
// held, no game function is called under one, and no reader keeps a pointer into game memory: snapshots are immutable
// vectors behind shared pointers, and a page copy holds its own reference, so a close (or the owner's revert) in the
// middle of a copy only drops the handle.
//
// What an entry says (chapter 13 §13b.4): one per distinct hash. origins: GAME when the game's index lists the hash, ORed
// with the origin of every live registration of it. The served winner is the store's (the last registration of the hash
// in the store's order), else the game's own (owner 0, sequence 0). OVERRIDDEN: the game lists it and a registration wins.
#include <algorithm>
#include <memory>
#include <unordered_set>

#include "assets.h"
#include "services/kiln_hooks.h"

namespace ka {
namespace {

constexpr size_t kMaxEntries = 1000000;
constexpr size_t kMaxSnapshotBytes = size_t(64) << 20;
constexpr size_t kMaxHeldBytes = size_t(128) << 20;
constexpr size_t kMaxHandles = 64;

struct Snapshot {
    uint64_t generation = 0;
    uint32_t mode = 0, status = 0, flags = 0;
    std::vector<KilnBlobCatalogEntry> entries;
    size_t bytes() const { return entries.capacity() * sizeof(KilnBlobCatalogEntry); }
};
struct Open {
    KilnHandle id;
    KilnOwner owner;
    std::shared_ptr<const Snapshot> snap;
};

std::mutex g_build_mu;  // one build at a time: the copies and the sort are bounded by the budgets
std::mutex g_cat_mu;    // the handles and the shared snapshots; held for bookkeeping only, never during a copy
std::vector<Open> g_open;
KilnHandle g_next = 1;
std::weak_ptr<const Snapshot> g_newest[2];  // per mode: what the next open at the same generation shares
std::vector<std::weak_ptr<const Snapshot>> g_snapshots;  // includes snapshots still retained by a read after close
std::unordered_set<KilnOwner> g_revoked;  // native owner ids are never reused; a late call must not reopen a dead owner's handle

// Registered before waiting for the build lock: revert also cancels opens that have not published a handle yet.
struct Opening;
std::vector<Opening*> g_opening;  // under g_cat_mu; pointers live until the call returns
struct Opening {
    KilnOwner owner;
    bool released = false;
    explicit Opening(KilnOwner o) : owner(o) {
        std::lock_guard lock(g_cat_mu);
        released = g_revoked.contains(owner);
        g_opening.push_back(this);
    }
    ~Opening() {
        std::lock_guard lock(g_cat_mu);
        std::erase(g_opening, this);
    }
};

bool hash_less(const HashK& a, const HashK& b) {
    if (a.size != b.size) return a.size < b.size;
    if (a.h0 != b.h0) return a.h0 < b.h0;
    if (a.h1 != b.h1) return a.h1 < b.h1;
    return a.h2 < b.h2;
}
HashK hash_of(const KilnBlobCatalogEntry& e) { return HashK{e.hash.size, e.hash.hash0, e.hash.hash1, e.hash.hash2}; }

KilnBlobCatalogEntry entry_for(const HashK& h) {
    KilnBlobCatalogEntry e{};
    e.size = sizeof(KilnBlobCatalogEntry);
    e.hash = KilnContentHash{h.size, h.h0, h.h1, h.h2};
    return e;
}

// What the registrations of one hash add up to: every origin, and the winner (the last one in the store's order).
struct Contribution {
    uint32_t origins = 0;
    LedgerEntry winner{};
};

// The snapshot of `keys` (sorted, distinct; the game's index, or empty) and `ledger`.
std::shared_ptr<Snapshot> build(uint32_t mode, uint64_t generation, const std::vector<HashK>& keys,
                                const std::vector<LedgerEntry>& ledger) {
    std::unordered_map<HashK, Contribution, HashKHash> regs;
    regs.reserve(ledger.size() * 2 + 1);
    for (const LedgerEntry& r : ledger) {
        Contribution& c = regs[r.hash];
        c.origins |= r.origin;
        c.winner = r;  // the later registration of a hash is the one served (store.cpp: rebuild_content_locked)
    }
    auto s = std::make_shared<Snapshot>();
    s->generation = generation;
    s->mode = mode;
    s->entries.reserve(keys.size() + regs.size());
    for (const HashK& k : keys) {
        KilnBlobCatalogEntry e = entry_for(k);
        e.origins = KILN_BLOB_ORIGIN_GAME;
        e.selected_origin = KILN_BLOB_ORIGIN_GAME;
        s->entries.push_back(e);
    }
    const size_t game = s->entries.size();
    for (const auto& [h, c] : regs) {
        KilnBlobCatalogEntry* e = nullptr;
        auto it = std::lower_bound(s->entries.begin(), s->entries.begin() + static_cast<ptrdiff_t>(game), h,
                                   [](const KilnBlobCatalogEntry& x, const HashK& y) { return hash_less(hash_of(x), y); });
        if (it != s->entries.begin() + static_cast<ptrdiff_t>(game) && hash_of(*it) == h) {
            e = &*it;
            e->flags |= KILN_BLOB_CATALOG_OVERRIDDEN;
        } else {
            s->entries.push_back(entry_for(h));
            e = &s->entries.back();
        }
        e->origins |= c.origins;
        e->selected_origin = c.winner.origin;
        e->selected_owner = c.winner.owner;
        e->selected_sequence = c.winner.sequence;
    }
    std::sort(s->entries.begin(), s->entries.end(),
              [](const KilnBlobCatalogEntry& a, const KilnBlobCatalogEntry& b) { return hash_less(hash_of(a), hash_of(b)); });
    return s;
}

void fill(KilnBlobCatalogInfo* info, uint32_t status, uint64_t generation, uint64_t total, uint32_t flags, uint32_t reason) {
    const uint32_t size = info->size;
    std::memset(info, 0, sizeof(KilnBlobCatalogInfo));
    info->size = size;
    info->status = status;
    info->generation = generation;
    info->total = total;
    info->flags = flags;
    info->reason = reason;
}

KilnResult refuse(KilnBlobCatalogInfo* info, uint32_t status, uint32_t reason) {
    fill(info, status, catalog_generation(), 0, 0, reason);
    return status == KILN_BLOB_CATALOG_LIMIT_EXCEEDED ? KILN_ERR_BUFFER_TOO_SMALL : KILN_ERR_UNAVAILABLE;
}

// The bytes all surviving snapshots take up, including page copies whose handles closed (under g_cat_mu).
size_t held_bytes_locked(const Snapshot* extra) {
    std::vector<const Snapshot*> seen;
    size_t n = 0;
    auto count = [&](const Snapshot* s) {
        if (!s || std::find(seen.begin(), seen.end(), s) != seen.end()) return;
        seen.push_back(s);
        n += s->bytes();
    };
    std::erase_if(g_snapshots, [](const auto& w) { return w.expired(); });
    for (const auto& w : g_snapshots)
        if (auto s = w.lock()) count(s.get());
    count(extra);
    return n;
}

}  // namespace

bool catalog_start() {
    static std::once_flag once;
    static bool watching = false;
    std::call_once(once, [] {
        if (!g_api || !g_api->service) return;
        const auto* hooks = static_cast<const KilnHooksApi*>(
            g_api->service(KilnStr{KILN_HOOKS_SERVICE, sizeof(KILN_HOOKS_SERVICE) - 1}, KILN_HOOKS_VERSION));
        if (!hooks || hooks->header.size < offsetof(KilnHooksApi, subscribe_owner_retiring) + sizeof(void*) ||
            !hooks->subscribe_owner_retiring) return;
        KilnHandle handle = 0;
        watching = hooks->subscribe_owner_retiring(g_self, [](KilnOwner owner, void*) { catalog_release_owner(owner, true); },
                                                   nullptr, &handle) == KILN_OK;
    });
    return watching;
}

KilnResult op_catalog_open(KilnOwner owner, uint32_t mode, KilnHandle* handle, KilnBlobCatalogInfo* info) {
    if (handle) *handle = 0;
    if (!handle || !info || info->size < sizeof(KilnBlobCatalogInfo) || !owner ||
        (mode != KILN_BLOB_CATALOG_REQUIRE_COMPLETE && mode != KILN_BLOB_CATALOG_REGISTRATIONS_ONLY))
        return KILN_ERR_INVALID_ARGUMENT;
    if (!content_hooks_on() || !catalog_start())
        return refuse(info, KILN_BLOB_CATALOG_UNAVAILABLE, KILN_BLOB_CATALOG_REASON_UNSUPPORTED_TABLE);
    Opening opening(owner);
    std::shared_ptr<const Snapshot> snap;
    {
        std::lock_guard build_lock(g_build_mu);
        {
            std::lock_guard lock(g_cat_mu);
            if (auto newest = g_newest[mode].lock(); newest && newest->generation == catalog_generation()) snap = newest;
        }
        if (!snap) {
            std::vector<HashK> keys;
            if (mode == KILN_BLOB_CATALOG_REQUIRE_COMPLETE) {
                uint64_t total = 0;
                std::string why;
                switch (game_index_keys(&keys, kMaxEntries, &total, &why)) {
                    case GameIndex::ok: break;
                    case GameIndex::not_ready: return refuse(info, KILN_BLOB_CATALOG_NOT_READY, KILN_BLOB_CATALOG_REASON_GAME_NOT_READY);
                    case GameIndex::unreadable:
                        LOGW("assets: blob catalog: %s", why.c_str());
                        return refuse(info, KILN_BLOB_CATALOG_NOT_READY, KILN_BLOB_CATALOG_REASON_UNREADABLE_MAP);
                    case GameIndex::too_many: return refuse(info, KILN_BLOB_CATALOG_LIMIT_EXCEEDED, KILN_BLOB_CATALOG_REASON_ENTRY_BUDGET);
                    case GameIndex::unsupported:
                        LOGW("assets: blob catalog: %s", why.c_str());
                        return refuse(info, KILN_BLOB_CATALOG_UNAVAILABLE, KILN_BLOB_CATALOG_REASON_UNSUPPORTED_TABLE);
                }
                std::sort(keys.begin(), keys.end(), hash_less);
                keys.erase(std::unique(keys.begin(), keys.end()), keys.end());
            }
            uint64_t generation = 0;
            const std::vector<LedgerEntry> ledger = blob_ledger(&generation);  // the store lock, for the copy only
            if (keys.size() + ledger.size() > kMaxEntries) {
                // an upper bound: count the distinct hashes before refusing
                std::vector<HashK> all = keys;
                for (const LedgerEntry& r : ledger) all.push_back(r.hash);
                std::sort(all.begin(), all.end(), hash_less);
                all.erase(std::unique(all.begin(), all.end()), all.end());
                if (all.size() > kMaxEntries) return refuse(info, KILN_BLOB_CATALOG_LIMIT_EXCEEDED, KILN_BLOB_CATALOG_REASON_ENTRY_BUDGET);
            }
            auto built = build(mode, generation, keys, ledger);
            if (built->bytes() > kMaxSnapshotBytes)
                return refuse(info, KILN_BLOB_CATALOG_LIMIT_EXCEEDED, KILN_BLOB_CATALOG_REASON_MEMORY_BUDGET);
            built->status = mode == KILN_BLOB_CATALOG_REQUIRE_COMPLETE ? KILN_BLOB_CATALOG_COMPLETE : KILN_BLOB_CATALOG_INCOMPLETE;
            built->flags = mode == KILN_BLOB_CATALOG_REQUIRE_COMPLETE
                               ? KILN_BLOB_CATALOG_GAME_COMPLETE | KILN_BLOB_CATALOG_REGISTRATIONS_COMPLETE
                               : KILN_BLOB_CATALOG_REGISTRATIONS_COMPLETE;
            snap = std::move(built);
        }
        std::lock_guard lock(g_cat_mu);
        if (opening.released) {
            fill(info, KILN_BLOB_CATALOG_UNAVAILABLE, catalog_generation(), 0, 0, KILN_BLOB_CATALOG_REASON_NONE);
            return KILN_ERR_REVOKED;
        }
        if (g_open.size() >= kMaxHandles || held_bytes_locked(snap.get()) > kMaxHeldBytes)
            return refuse(info, KILN_BLOB_CATALOG_LIMIT_EXCEEDED, KILN_BLOB_CATALOG_REASON_MEMORY_BUDGET);
        if (std::none_of(g_snapshots.begin(), g_snapshots.end(), [&](const auto& w) { return w.lock() == snap; }))
            g_snapshots.push_back(snap);
        if (auto newest = g_newest[mode].lock(); !newest || newest->generation <= snap->generation) g_newest[mode] = snap;
        *handle = g_next++;
        g_open.push_back({*handle, owner, snap});
    }
    fill(info, snap->status, snap->generation, snap->entries.size(), snap->flags, KILN_BLOB_CATALOG_REASON_NONE);
    return KILN_OK;
}

KilnResult op_catalog_read(KilnHandle handle, uint64_t first, KilnBlobCatalogEntry* out, uint32_t entry_size, size_t cap,
                           size_t* written) {
    if (written) *written = 0;
    if (!written || entry_size < sizeof(KilnBlobCatalogEntry) || (cap && !out)) return KILN_ERR_INVALID_ARGUMENT;
    std::shared_ptr<const Snapshot> snap;  // the copy's own reference: a close meanwhile keeps the entries alive
    {
        std::lock_guard lock(g_cat_mu);
        for (const Open& o : g_open)
            if (o.id == handle) snap = o.snap;
    }
    if (!snap) return KILN_ERR_NOT_FOUND;
#ifdef KILN_ASSETS_CATALOG_TEST
    // The host test can hold the read at the same point a concurrent thread can close its last handle.
    extern void catalog_read_acquired();
    catalog_read_acquired();
#endif
    const uint64_t total = snap->entries.size();
    if (first > total) return KILN_ERR_INVALID_ARGUMENT;
    const size_t n = static_cast<size_t>(std::min<uint64_t>(cap, total - first));
    auto* dst = reinterpret_cast<uint8_t*>(out);
    for (size_t i = 0; i < n; ++i, dst += entry_size) {
        std::memcpy(dst, &snap->entries[static_cast<size_t>(first) + i], sizeof(KilnBlobCatalogEntry));
        if (entry_size > sizeof(KilnBlobCatalogEntry)) std::memset(dst + sizeof(KilnBlobCatalogEntry), 0, entry_size - sizeof(KilnBlobCatalogEntry));
    }
    *written = n;
    return KILN_OK;
}

KilnResult op_catalog_close(KilnHandle handle) {
    std::shared_ptr<const Snapshot> last;  // released after the lock
    std::lock_guard lock(g_cat_mu);
    for (auto it = g_open.begin(); it != g_open.end(); ++it)
        if (it->id == handle) {
            last = std::move(it->snap);
            g_open.erase(it);
            return KILN_OK;
        }
    return KILN_ERR_NOT_FOUND;
}

void catalog_release_owner(KilnOwner owner, bool revoked) {
    std::vector<std::shared_ptr<const Snapshot>> dropped;  // released after the lock
    std::lock_guard lock(g_cat_mu);
    if (revoked) g_revoked.insert(owner);
    for (Opening* opening : g_opening)
        if (opening->owner == owner) opening->released = true;
    for (auto it = g_open.begin(); it != g_open.end();)
        if (it->owner == owner) {
            dropped.push_back(std::move(it->snap));
            it = g_open.erase(it);
        } else {
            ++it;
        }
}

}  // namespace ka
