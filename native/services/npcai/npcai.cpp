// SPDX-License-Identifier: MIT
// kiln.npcai: who fights whom. Contract: native/core/abi/services/kiln_npcai.h; evidence: docs/re/npc-internals.md 3.6;
// usage: docs/kiln/modding/npc-ai.md.
//
// The game answers "would a fight b" and "is b on a's side" in five leaf functions over keen::FactionType (sigdb
// server.d/npcai.json). Their pointer forms get the two entities' Faction components, so a creature's own relations
// (rows) and its SenseFilter (pairs) can be told apart from everybody else's: in Pre, when no job runs, the components
// of the entities that have any are mapped back to them in a table the hooks read. The hooks run on the game's job
// threads: they only read that table and the faction table, published as immutable snapshots or atomic bytes. No managed callback runs in a hook.
#include <windows.h>

#include <atomic>
#include <array>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <map>
#include <memory>
#include <cmath>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include "kiln_api.h"
#include "kiln_hook.hpp"
#include "safetyhook.hpp"  // safetyhook::Context: the mid handler's register view
#include "services/kiln_npcai.h"
#include "services/kiln_world.h"

namespace {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
const KilnWorldApi* g_world = nullptr;
uint32_t g_faction_hash = 0;

void logf(int level, const char* fmt, ...) {
    char buf[512];
    va_list ap;
    va_start(ap, fmt);
    int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    if (n >= static_cast<int>(sizeof buf)) n = sizeof buf - 1;
    std::string line = "npcai: ";
    line.append(buf, static_cast<size_t>(n));
    g_api->log(g_owner, level, KilnStr{line.data(), line.size()});
}
#define NA_I(...) logf(KILN_LOG_INFO, __VA_ARGS__)
#define NA_W(...) logf(KILN_LOG_WARN, __VA_ARGS__)

bool on_tick() { return g_api->on_tick_thread && g_api->on_tick_thread(); }
bool server_world_ok() { return g_api->server_world && g_api->server_world() != 0; }

// ------------------------------------------------------------------------------------------------ state (tick) ----

std::mutex g_owner_mu;
std::map<std::string, uint32_t> g_owner_ids;
uint32_t g_next_owner = 1;

struct Cell {
    uint8_t rel;
    uint64_t seq;
};
uint64_t g_seq = 1;
// owner -> (a << 8 | b) -> what it set
std::map<uint32_t, std::map<uint16_t, Cell>> g_cells;
// the effective table: what the hooks read (bytes, written on the tick, read anywhere)
std::atomic<uint8_t> g_table[256 * 256]{};
std::atomic<uint32_t> g_table_n{0};

uint64_t g_next_life = 1;
struct Life {
    uint64_t id = g_next_life++;  // created on the tick only
    std::atomic<bool> alive{true};
};
using Lives = std::unordered_map<KilnEntity, std::shared_ptr<Life>>;
Lives g_lives;  // tick only; hooks read the immutable published index
std::atomic<std::shared_ptr<const Lives>> g_life_index;
std::atomic<uintptr_t> g_storage{0};
int64_t g_storage_offset = -1;
// A kiln.hooks link, added when the first creature relation needs it and revoked when none is left. Revoking only
// takes the link out of Kiln's chain (the detour stays, so another module's hook over it is never undone).
kiln::MidHook2 g_destroy;
uintptr_t g_destroy_at = 0;
bool g_lifecycle = false;

std::shared_ptr<Life> life_of(KilnEntity e) {
    auto& life = g_lives[e];
    if (!life || !life->alive.load(std::memory_order_acquire)) life = std::make_shared<Life>();
    g_life_index.store(std::make_shared<const Lives>(g_lives), std::memory_order_release);
    return life;
}

// Entry observer only: RCX is storage, EDX is the id. Do not change registers, flags, RIP or the stack.
// Invalidate before memory can be recycled, including a destroy followed by a create in the same step.
// A refused destroy conservatively drops overrides too; it can never transfer them to another entity.
void hk_destroy(safetyhook::Context& ctx) {
    const auto storage = g_storage.load(std::memory_order_acquire);
    if (!storage || ctx.rcx != storage) return;
    if (auto lives = g_life_index.load(std::memory_order_acquire)) {
        auto it = lives->find(static_cast<KilnEntity>(ctx.rdx));
        if (it != lives->end()) it->second->alive.store(false, std::memory_order_release);
    }
}

struct Row {
    uint32_t owner;
    uint8_t rel[256];
    std::shared_ptr<Life> life;
};
std::unordered_map<KilnEntity, Row> g_rows;

struct PairKey {
    KilnEntity a, b;
    bool operator<(const PairKey& o) const { return a != o.a ? a < o.a : b < o.b; }
};
struct Pair {
    uint32_t owner;
    uint8_t rel;
    uint64_t until;  // GetTickCount64 ms; 0 = until cleared
    std::shared_ptr<Life> a, b;
};
std::map<PairKey, Pair> g_pairs;

// ------------------------------------------------------------------------------------- published map (job read) ----
// Open addressing on the Faction component's address. A row slot points at its 256 relations; pairs hash both
// addresses. Readers retain immutable snapshots; publishing again never mutates a snapshot a worker still uses.
struct Snapshot {
    struct RowSlot {
        const uint8_t* key;
        const uint8_t* rel;
        std::shared_ptr<Life> life;
    };
    struct PairSlot {
        const uint8_t* a;
        const uint8_t* b;
        uint8_t rel;
        std::shared_ptr<Life> la, lb;
    };
    std::vector<RowSlot> rows;    // power of two, empty key = free
    std::vector<PairSlot> pairs;  // power of two
    std::vector<uint8_t> row_data;
    uint32_t mapped = 0;
};
std::atomic<std::shared_ptr<const Snapshot>> g_live;
std::atomic<uint32_t> g_rows_n{0}, g_pairs_n{0};

inline size_t ptr_hash(const void* p) {
    auto x = reinterpret_cast<uintptr_t>(p);
    x ^= x >> 17;
    x *= 0xed5ad4bbu;
    x ^= x >> 11;
    return static_cast<size_t>(x);
}

inline const uint8_t* find_row(const Snapshot* s, const uint8_t* key) {
    if (s->rows.empty()) return nullptr;
    const size_t mask = s->rows.size() - 1;
    for (size_t i = ptr_hash(key) & mask, n = 0; n < s->rows.size(); i = (i + 1) & mask, ++n) {
        const auto& r = s->rows[i];
        if (!r.key) return nullptr;
        if (r.key == key) return r.life->alive.load(std::memory_order_acquire) ? r.rel : nullptr;
    }
    return nullptr;
}

inline uint8_t find_pair(const Snapshot* s, const uint8_t* a, const uint8_t* b) {
    if (s->pairs.empty()) return 0;
    const size_t mask = s->pairs.size() - 1;
    for (size_t i = (ptr_hash(a) * 31 + ptr_hash(b)) & mask, n = 0; n < s->pairs.size(); i = (i + 1) & mask, ++n) {
        const auto& p = s->pairs[i];
        if (!p.a) return 0;
        if (p.a == a && p.b == b)
            return p.la->alive.load(std::memory_order_acquire) && p.lb->alive.load(std::memory_order_acquire) ? p.rel : 0;
    }
    return 0;
}

// ---------------------------------------------------------------------------------------------------- hooks ----
using PtrFn = bool (*)(const uint8_t*, const uint8_t*);
using PtrOptFn = bool (*)(const uint8_t*, const uint8_t*, const void*);
using ValFn = bool (*)(uint8_t, uint8_t);

enum : uint32_t { kHostile = 1, kHostileValue = 2, kFriend = 4, kFriendOpt = 8, kFriendValue = 16 };
uintptr_t g_at[5] = {};
// kiln.hooks links: added with the first relation, revoked when the last one goes. A link's continuation (original())
// stays valid after revoke, so a game thread still inside a handler finishes safely.
std::array<kiln::RawHook, 5> g_h;
uint32_t g_resolved = 0;
// kiln.hooks never frees a link (65536 in all, five per cycle here), so relations that come and go must not add five
// every time: after kMaxCycles sets of links the links stay in through idle spells and just answer nothing.
constexpr uint32_t kMaxCycles = 64;
uint32_t g_cycles = 0;  // sets of links added so far (tick thread)
std::atomic<uint32_t> g_installed{0};
std::atomic<bool> g_answering{false}, g_install_failed{false};
// The game's function past Kiln's link: the link's continuation once it was ever installed (it stays valid after
// revoke), else the function's own entry (query() before the first relation: no link of ours is on it).
template <class F>
F orig(int i) {
    const F f = g_h[i].original<F>();
    return f ? f : reinterpret_cast<F>(g_at[i]);
}
bool g_o_hostile(const uint8_t* a, const uint8_t* b) { return orig<PtrFn>(0)(a, b); }
bool g_o_hostile_value(uint8_t a, uint8_t b) { return orig<ValFn>(1)(a, b); }
bool g_o_friend(const uint8_t* a, const uint8_t* b) { return orig<PtrFn>(2)(a, b); }
bool g_o_friend_opt(const uint8_t* a, const uint8_t* b, const void* o) { return orig<PtrOptFn>(3)(a, b, o); }
bool g_o_friend_value(uint8_t a, uint8_t b) { return orig<ValFn>(4)(a, b); }

std::atomic<uint64_t> g_calls{0}, g_answered{0}, g_by_pair{0}, g_by_row{0}, g_by_table{0};

// a's relation toward b, by their Faction components; 0 = the game's
uint8_t relation_ptr(const uint8_t* a, const uint8_t* b, bool counted = true) {
    if (!a || !b) return 0;
    if (auto s = g_live.load(std::memory_order_acquire)) {
        if (uint8_t r = find_pair(s.get(), a, b)) {
            if (counted) g_by_pair.fetch_add(1, std::memory_order_relaxed);
            return r;
        }
        if (const uint8_t* row = find_row(s.get(), a)) {
            if (uint8_t r = row[*b]) {
                if (counted) g_by_row.fetch_add(1, std::memory_order_relaxed);
                return r;
            }
        }
    }
    if (g_table_n.load(std::memory_order_relaxed)) {
        if (uint8_t r = g_table[(static_cast<uint32_t>(*a) << 8) | *b].load(std::memory_order_relaxed)) {
            if (counted) g_by_table.fetch_add(1, std::memory_order_relaxed);
            return r;
        }
    }
    return 0;
}

uint8_t relation_val(uint8_t a, uint8_t b) {
    if (!g_table_n.load(std::memory_order_relaxed)) return 0;
    uint8_t r = g_table[(static_cast<uint32_t>(a) << 8) | b].load(std::memory_order_relaxed);
    if (r) g_by_table.fetch_add(1, std::memory_order_relaxed);
    return r;
}

inline int hostile_of(uint8_t r) { return r == KILN_REL_HOSTILE ? 1 : r ? 0 : -1; }
inline int friend_of(uint8_t r) { return r == KILN_REL_FRIENDLY ? 1 : r ? 0 : -1; }

bool hk_hostile(const uint8_t* a, const uint8_t* b) {
    if (!g_answering.load(std::memory_order_acquire)) return g_o_hostile(a, b);
    g_calls.fetch_add(1, std::memory_order_relaxed);
    if (int v = hostile_of(relation_ptr(a, b)); v >= 0) {
        g_answered.fetch_add(1, std::memory_order_relaxed);
        return v != 0;
    }
    return g_o_hostile(a, b);
}
bool hk_friend(const uint8_t* a, const uint8_t* b) {
    if (!g_answering.load(std::memory_order_acquire)) return g_o_friend(a, b);
    g_calls.fetch_add(1, std::memory_order_relaxed);
    if (int v = friend_of(relation_ptr(a, b)); v >= 0) {
        g_answered.fetch_add(1, std::memory_order_relaxed);
        return v != 0;
    }
    return g_o_friend(a, b);
}
bool hk_friend_opt(const uint8_t* a, const uint8_t* b, const void* o) {
    if (!g_answering.load(std::memory_order_acquire)) return g_o_friend_opt(a, b, o);
    g_calls.fetch_add(1, std::memory_order_relaxed);
    if (int v = friend_of(relation_ptr(a, b)); v >= 0) {
        g_answered.fetch_add(1, std::memory_order_relaxed);
        return v != 0;
    }
    return g_o_friend_opt(a, b, o);
}
bool hk_hostile_value(uint8_t a, uint8_t b) {
    if (!g_answering.load(std::memory_order_acquire)) return g_o_hostile_value(a, b);
    g_calls.fetch_add(1, std::memory_order_relaxed);
    if (int v = hostile_of(relation_val(a, b)); v >= 0) {
        g_answered.fetch_add(1, std::memory_order_relaxed);
        return v != 0;
    }
    return g_o_hostile_value(a, b);
}
bool hk_friend_value(uint8_t a, uint8_t b) {
    if (!g_answering.load(std::memory_order_acquire)) return g_o_friend_value(a, b);
    g_calls.fetch_add(1, std::memory_order_relaxed);
    if (int v = friend_of(relation_val(a, b)); v >= 0) {
        g_answered.fetch_add(1, std::memory_order_relaxed);
        return v != 0;
    }
    return g_o_friend_value(a, b);
}

void* const kDetours[5] = {reinterpret_cast<void*>(&hk_hostile), reinterpret_cast<void*>(&hk_hostile_value),
                           reinterpret_cast<void*>(&hk_friend), reinterpret_cast<void*>(&hk_friend_opt),
                           reinterpret_cast<void*>(&hk_friend_value)};
const char* const kNames[5] = {"npcai/FactionHostile", "npcai/FactionHostileValue", "npcai/FactionFriend",
                               "npcai/FactionFriendOpt", "npcai/FactionFriendValue"};

void resolve() {
    for (int i = 0; i < 5; ++i) {
        uintptr_t at = 0;
        if (g_api->symbol(KilnStr{kNames[i], std::strlen(kNames[i])}, &at) == KILN_OK && at) {
            g_at[i] = at;
            g_resolved |= 1u << i;
        }
    }
}

// Require the whole family: a partial override can make the same pair both hostile and friendly.
bool usable() { return g_resolved == 0x1f; }

// Checked before game entry: the links themselves go in with the first relation (kiln.hooks v2 adds and revokes them
// while the game runs, without freezing threads).
bool prepare() { return usable() && kiln::hooks_v2(g_api) != nullptr; }

void uninstall() {
    g_answering.store(false, std::memory_order_release);
    for (int i = 0; i < 5; ++i) {
        if (!g_h[i]) continue;
        g_h[i].revoke();
        g_installed.fetch_and(~(1u << i));
    }
    if (g_lives.empty() && g_destroy) g_destroy.revoke();
}

// No relation is left: answers stop now. The links go out too, unless they have come and gone kMaxCycles times already.
void idle() {
    if (g_cycles >= kMaxCycles && g_installed.load() == 0x1f) {
        g_answering.store(false, std::memory_order_release);
        return;
    }
    uninstall();
}

bool install() {
    if (g_install_failed.load()) return false;
    if (g_installed.load() == 0x1f) {
        g_answering.store(true, std::memory_order_release);
        return true;
    }
    if (g_installed.load() == 0) ++g_cycles;
    for (int i = 0; i < 5; ++i) {
        if (g_h[i]) continue;
        if (!g_h[i].install(g_api, g_owner, g_at[i], kDetours[i], kNames[i])) {
            NA_W("the hook on %s failed; the game decides alone", kNames[i]);
            uninstall();
            g_install_failed.store(true);
            return false;
        }
        g_installed.fetch_or(1u << i);
    }
    g_answering.store(true, std::memory_order_release);
    return true;
}

bool entity_support() {
    if (!g_world || !g_faction_hash || !g_destroy_at || !g_lifecycle || !server_world_ok()) return false;
    g_storage.store(g_api->server_world() + static_cast<uintptr_t>(g_storage_offset), std::memory_order_release);
    if (g_destroy) return true;
    if (!g_destroy.install(g_api, g_owner, g_destroy_at, &hk_destroy, "spawn/DestroyEntity")) {
        static std::atomic<bool> warned{false};
        if (!warned.exchange(true)) NA_W("the entity lifetime observer did not install (%s): per-creature relations are refused", g_destroy.error());
        return false;
    }
    return true;
}

// ------------------------------------------------------------------------------------------------ Pre ----
bool g_dirty_table = false;

// Each cell is atomic: callers of relation_get and the game may read while a tick updates it.
void rebuild_table() {
    static uint8_t next[256 * 256];
    std::memset(next, 0, sizeof next);
    std::map<uint16_t, Cell> best;
    for (auto& [o, cells] : g_cells)
        for (auto& [k, c] : cells)
            if (auto it = best.find(k); it == best.end() || it->second.seq < c.seq) best[k] = c;
    for (auto& [k, c] : best) next[k] = c.rel;
    for (size_t i = 0; i < sizeof next; ++i)
        g_table[i].store(next[i], std::memory_order_relaxed);
    g_table_n.store(static_cast<uint32_t>(best.size()), std::memory_order_relaxed);
    g_dirty_table = false;
}

const uint8_t* faction_of(KilnEntity e) {
    if (!g_world || !g_faction_hash || !e) return nullptr;
    uint32_t size = 0;
    auto* p = static_cast<const uint8_t*>(g_world->component(e, g_faction_hash, &size));
    return p && size >= 1 ? p : nullptr;
}

size_t pow2(size_t n) {
    size_t s = 16;
    while (s < n * 2) s <<= 1;
    return s;
}

void publish() {
    const uint64_t now = GetTickCount64();
    for (auto it = g_pairs.begin(); it != g_pairs.end();)
        it = it->second.until && it->second.until <= now ? g_pairs.erase(it) : std::next(it);
    auto next = std::make_shared<Snapshot>();
    Snapshot& s = *next;
    s.rows.assign(g_rows.empty() ? 0 : pow2(g_rows.size()), Snapshot::RowSlot{});
    s.pairs.assign(g_pairs.empty() ? 0 : pow2(g_pairs.size()), Snapshot::PairSlot{});
    s.row_data.resize(g_rows.size() * 256);
    s.mapped = 0;
    std::unordered_map<KilnEntity, const uint8_t*> cache;
    auto fac = [&](KilnEntity e) -> const uint8_t* {
        auto it = cache.find(e);
        if (it != cache.end()) return it->second;
        const uint8_t* p = faction_of(e);
        cache[e] = p;
        if (p) ++s.mapped;
        return p;
    };
    size_t k = 0;
    for (auto it = g_rows.begin(); it != g_rows.end();) {
        if (!it->second.life->alive.load(std::memory_order_acquire) || !g_world->exists(it->first)) {
            it = g_rows.erase(it);
            continue;
        }
        if (const uint8_t* key = fac(it->first)) {
            uint8_t* data = s.row_data.data() + k * 256;
            std::memcpy(data, it->second.rel, 256);
            ++k;
            const size_t mask = s.rows.size() - 1;
            for (size_t i = ptr_hash(key) & mask;; i = (i + 1) & mask)
                if (!s.rows[i].key) {
                    s.rows[i] = {key, data, it->second.life};
                    break;
                }
        }
        ++it;
    }
    for (auto it = g_pairs.begin(); it != g_pairs.end();) {
        if (!it->second.a->alive.load(std::memory_order_acquire) || !it->second.b->alive.load(std::memory_order_acquire) ||
            !g_world->exists(it->first.a) || !g_world->exists(it->first.b)) {
            it = g_pairs.erase(it);
            continue;
        }
        const uint8_t* a = fac(it->first.a);
        const uint8_t* b = fac(it->first.b);
        if (a && b) {
            const size_t mask = s.pairs.size() - 1;
            for (size_t i = (ptr_hash(a) * 31 + ptr_hash(b)) & mask;; i = (i + 1) & mask)
                if (!s.pairs[i].a) {
                    s.pairs[i] = {a, b, it->second.rel, it->second.a, it->second.b};
                    break;
                }
        }
        ++it;
    }
    g_rows_n.store(static_cast<uint32_t>(g_rows.size()));
    g_pairs_n.store(static_cast<uint32_t>(g_pairs.size()));
    g_live.store(std::move(next), std::memory_order_release);
    // Keep lifetime tokens until destruction; managed summons also use them without a relation row.
    for (auto it = g_lives.begin(); it != g_lives.end();)
        it = !it->second->alive.load(std::memory_order_acquire) || !g_world->exists(it->first) ? g_lives.erase(it) : std::next(it);
    g_life_index.store(std::make_shared<const Lives>(g_lives), std::memory_order_release);
}

void KILN_CALL on_pre(const KilnTickInfo*, void*) {
    if (g_dirty_table) rebuild_table();
    if (g_world && server_world_ok()) publish();
    const bool want = g_table_n.load(std::memory_order_relaxed) || !g_rows.empty() || !g_pairs.empty();
    if (want && !g_answering.load(std::memory_order_relaxed)) {
        if (!install()) {
            // keep the settings; the game answers alone
            static bool warned = false;
            if (!warned) NA_W("relations are set, and the faction hooks could not go in: the game decides alone");
            warned = true;
        }
    }
    if (!want) {
        g_live.store(nullptr, std::memory_order_release);
        idle();
    }
}

// ------------------------------------------------------------------------------------------------ API ----
uint32_t KILN_CALL owner_open(KilnStr name) {
    std::lock_guard<std::mutex> g(g_owner_mu);
    std::string n(name.ptr ? name.ptr : "", name.ptr ? name.len : 0);
    auto it = g_owner_ids.find(n);
    if (it != g_owner_ids.end()) return it->second;
    const uint32_t id = g_next_owner++;
    g_owner_ids[n] = id;
    return id;
}

KilnResult KILN_CALL owner_release(uint32_t owner) {
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    if (g_cells.erase(owner)) rebuild_table();
    for (auto it = g_rows.begin(); it != g_rows.end();) it = it->second.owner == owner ? g_rows.erase(it) : std::next(it);
    for (auto it = g_pairs.begin(); it != g_pairs.end();) it = it->second.owner == owner ? g_pairs.erase(it) : std::next(it);
    return KILN_OK;
}

KilnResult KILN_CALL relation_set(uint32_t owner, uint8_t a, uint8_t b, uint32_t relation) {
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    if (!owner || relation > KILN_REL_NEUTRAL) return KILN_ERR_INVALID_ARGUMENT;
    if (!usable() || g_install_failed.load()) return KILN_ERR_UNAVAILABLE;
    const uint16_t k = static_cast<uint16_t>((a << 8) | b);
    if (relation == KILN_REL_GAME) {
        auto it = g_cells.find(owner);
        if (it != g_cells.end() && it->second.erase(k)) g_dirty_table = true;
    } else {
        g_cells[owner][k] = Cell{static_cast<uint8_t>(relation), g_seq++};
        g_dirty_table = true;
    }
    // The table takes effect at once through atomic cells; hooks go in at the next Pre.
    rebuild_table();
    return KILN_OK;
}

uint32_t KILN_CALL relation_get(uint8_t a, uint8_t b) { return g_table[(static_cast<uint32_t>(a) << 8) | b].load(std::memory_order_relaxed); }

KilnResult KILN_CALL entity_row(uint32_t owner, KilnEntity e, const uint8_t* row) {
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    if (!owner || !e) return KILN_ERR_INVALID_ARGUMENT;
    if (!usable() || g_install_failed.load()) return KILN_ERR_UNAVAILABLE;
    if (!row) {
        if (auto it = g_rows.find(e); it != g_rows.end() && it->second.owner == owner) g_rows.erase(it);
        return KILN_OK;
    }
    for (int i = 0; i < 256; ++i)
        if (row[i] > KILN_REL_NEUTRAL) return KILN_ERR_INVALID_ARGUMENT;
    if (g_world && !g_world->exists(e)) return KILN_ERR_NOT_FOUND;
    bool any = false;
    for (int i = 0; i < 256; ++i) any |= row[i] != KILN_REL_GAME;
    if (!any) return entity_row(owner, e, nullptr);
    if (!entity_support()) return KILN_ERR_UNAVAILABLE;
    Row r{owner, {}, life_of(e)};
    std::memcpy(r.rel, row, 256);
    g_rows[e] = r;
    return KILN_OK;
}

KilnResult KILN_CALL pair_set(uint32_t owner, KilnEntity e, KilnEntity other, uint32_t relation, float seconds) {
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    if (!owner || !e || !other || relation > KILN_REL_NEUTRAL || !std::isfinite(seconds) || seconds < 0 || seconds > 86400) return KILN_ERR_INVALID_ARGUMENT;
    if (!usable() || g_install_failed.load()) return KILN_ERR_UNAVAILABLE;
    if (relation == KILN_REL_GAME) {
        auto it = g_pairs.find(PairKey{e, other});
        if (it != g_pairs.end() && it->second.owner == owner) g_pairs.erase(it);
        return KILN_OK;
    }
    if (!entity_support()) return KILN_ERR_UNAVAILABLE;
    if (!g_world->exists(e) || !g_world->exists(other)) return KILN_ERR_NOT_FOUND;
    const uint64_t until = seconds > 0 ? GetTickCount64() + static_cast<uint64_t>(seconds * 1000.0f) : 0;
    g_pairs[PairKey{e, other}] = Pair{owner, static_cast<uint8_t>(relation), until, life_of(e), life_of(other)};
    return KILN_OK;
}

KilnResult KILN_CALL pair_clear(uint32_t owner, KilnEntity e) {
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    for (auto it = g_pairs.lower_bound(PairKey{e, 0}); it != g_pairs.end() && it->first.a == e;)
        it = it->second.owner == owner ? g_pairs.erase(it) : std::next(it);
    return KILN_OK;
}

KilnResult KILN_CALL query(KilnEntity a, KilnEntity b, uint32_t* hostile, uint32_t* friendly) {
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    if (!usable() || g_install_failed.load()) return KILN_ERR_UNAVAILABLE;
    const uint8_t* fa = faction_of(a);
    const uint8_t* fb = faction_of(b);
    if (!fa || !fb) return KILN_ERR_NOT_FOUND;
    // as the hooks answer (they may not be in before the next Pre): Kiln's relation, else the game's own code
    const uint8_t r = relation_ptr(fa, fb, false);
    const PtrFn game_hostile = orig<PtrFn>(0);
    const PtrOptFn game_friend = orig<PtrOptFn>(3);
    const uint8_t opt[2] = {0, 0};
    if (hostile) *hostile = hostile_of(r) >= 0 ? static_cast<uint32_t>(hostile_of(r)) : game_hostile(fa, fb) ? 1u : 0u;
    if (friendly) *friendly = friend_of(r) >= 0 ? static_cast<uint32_t>(friend_of(r)) : game_friend && game_friend(fa, fb, opt) ? 1u : 0u;
    return KILN_OK;
}

KilnResult KILN_CALL query_factions(uint8_t a, uint8_t b, uint32_t* hostile, uint32_t* friendly) {
    if (!usable() || g_install_failed.load()) return KILN_ERR_UNAVAILABLE;
    const uint8_t r = g_table[(static_cast<uint32_t>(a) << 8) | b].load(std::memory_order_relaxed);
    const ValFn game_hostile = g_o_hostile_value;
    const ValFn game_friend = g_o_friend_value;
    if (hostile) *hostile = hostile_of(r) >= 0 ? static_cast<uint32_t>(hostile_of(r)) : game_hostile(a, b) ? 1u : 0u;
    if (friendly) *friendly = friend_of(r) >= 0 ? static_cast<uint32_t>(friend_of(r)) : game_friend(a, b) ? 1u : 0u;
    return KILN_OK;
}

KilnResult KILN_CALL status(KilnNpcAiStatus* out) {
    if (!out || out->size < sizeof(KilnNpcAiStatus)) return KILN_ERR_INVALID_ARGUMENT;
    out->resolved = g_resolved;
    out->installed = g_installed;
    out->relations = g_table_n.load(std::memory_order_relaxed);
    out->rows = g_rows_n.load();
    out->pairs = g_pairs_n.load();
    auto s = g_live.load(std::memory_order_acquire);
    out->mapped = s ? s->mapped : 0;
    out->reserved = g_install_failed.load() ? 1u : 0u;
    out->calls = g_calls.load(std::memory_order_relaxed);
    out->answered = g_answered.load(std::memory_order_relaxed);
    out->by_pair = g_by_pair.load(std::memory_order_relaxed);
    out->by_row = g_by_row.load(std::memory_order_relaxed);
    out->by_table = g_by_table.load(std::memory_order_relaxed);
    return KILN_OK;
}

uint64_t KILN_CALL entity_lifetime(KilnEntity e) {
    if (!on_tick() || !entity_support() || !g_world->exists(e)) return 0;
    auto it = g_lives.find(e);
    return it != g_lives.end() && it->second->alive.load(std::memory_order_acquire) ? it->second->id : life_of(e)->id;
}

uint64_t KILN_CALL tick_step() {
    KilnTickInfo info{};
    info.size = sizeof info;
    return g_api->tick_info && g_api->tick_info(&info) == KILN_OK ? info.step : 0;
}

const KilnNpcAiApi kTable = {{sizeof(KilnNpcAiApi), KILN_NPCAI_VERSION},
                             &owner_open,
                             &owner_release,
                             &relation_set,
                             &relation_get,
                             &entity_row,
                             &pair_set,
                             &pair_clear,
                             &query,
                             &query_factions,
                             &status,
                             &entity_lifetime,
                             &tick_step};

void clear_entities() {
    for (auto& [e, life] : g_lives) life->alive.store(false, std::memory_order_release);
    g_live.store(nullptr);
    g_life_index.store(nullptr);
    g_rows.clear();
    g_pairs.clear();
    g_lives.clear();
    g_rows_n.store(0);
    g_pairs_n.store(0);
    g_storage.store(0);
}

void KILN_CALL on_world(const KilnWorldEvent* ev, void*) {
    if (ev->kind == KILN_WORLD_SERVER && ev->event == KILN_WORLD_STOPPING) {
        clear_entities();
        g_cells.clear();
        rebuild_table();
        uninstall();
    }
}

void KILN_CALL on_unload(void*) {
    clear_entities();
    g_cells.clear();
    for (auto& cell : g_table) cell.store(0);
    g_table_n.store(0);
    uninstall();
}

void KILN_CALL on_ready(void*) {
    g_world = static_cast<const KilnWorldApi*>(g_api->service(KilnStr{KILN_WORLD_SERVICE, sizeof(KILN_WORLD_SERVICE) - 1}, KILN_WORLD_VERSION));
    if (g_world && g_world->component_hash) g_faction_hash = g_world->component_hash(KilnStr{"Faction", 7});
    // the lifetime observer's link goes in with the first creature relation (entity_support)
    if (!(g_api->constant(KilnStr{"world.storage", 13}, &g_storage_offset) == KILN_OK && g_storage_offset > 0 &&
          g_api->symbol(KilnStr{"spawn/DestroyEntity", 19}, &g_destroy_at) == KILN_OK && g_destroy_at))
        g_destroy_at = 0;
    if (!g_destroy_at) NA_W("entity lifetime observer unavailable: per-creature relations are refused");
    if (!g_world) NA_W("kiln.world is not here: only the faction table works (no creature's own relations)");
}

}  // namespace

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = KilnStr{KILN_NPCAI_SERVICE, sizeof(KILN_NPCAI_SERVICE) - 1};
    info->version = KilnStr{"1", 1};
    info->on_ready = &on_ready;
    info->on_unload = &on_unload;
    resolve();
    if (!usable() || !prepare()) {
        NA_W("the faction functions are not found on this build (0x%x): kiln.npcai is unavailable, the game decides who fights whom", g_resolved);
        return KILN_ERR_UNAVAILABLE;
    }
    KilnHandle h = 0;
    g_lifecycle = api->feature && api->feature(KilnStr{"world.lifecycle", 15}) && api->subscribe_world &&
                  api->subscribe_world(owner, &on_world, nullptr, &h) == KILN_OK;
    if (!g_lifecycle) {
        NA_W("world lifecycle unavailable: kiln.npcai is unavailable so relations cannot outlive their world");
        return KILN_ERR_UNAVAILABLE;
    }
    if (KilnResult rc = api->subscribe_tick(owner, KILN_TICK_PRE, &on_pre, nullptr, &h); rc != KILN_OK) return rc;
    NA_I("resolved 0x%x of 0x1f (Hostile, HostileValue, Friend, FriendOpt, FriendValue); hooks go in with the first relation", g_resolved);
    return api->register_service(owner, KilnStr{KILN_NPCAI_SERVICE, sizeof(KILN_NPCAI_SERVICE) - 1}, &kTable);
}
