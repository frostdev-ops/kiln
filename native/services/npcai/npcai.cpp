// SPDX-License-Identifier: MIT
// kiln.npcai: who fights whom. Contract: native/core/abi/services/kiln_npcai.h; evidence: docs/re/npc-internals.md 3.6;
// usage: docs/kiln/modding/npc-ai.md.
//
// The game answers "would a fight b" and "is b on a's side" in four leaf functions over keen::FactionType (sigdb
// server.d/npcai.json). Their pointer forms get the two entities' Faction components, so a creature's own relations
// (rows) and its SenseFilter (pairs) can be told apart from everybody else's: in Pre, when no job runs, the components
// of the entities that have any are mapped back to them in a table the hooks read. The hooks run on the game's job
// threads: they only read that table and the faction table, both written in Pre or as single bytes.
#include <windows.h>

#include <atomic>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <map>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include "kiln_api.h"
#include "safetyhook.hpp"
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
uint8_t g_table[256 * 256];
std::atomic<uint32_t> g_table_n{0};

struct Row {
    uint32_t owner;
    uint8_t rel[256];
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
};
std::map<PairKey, Pair> g_pairs;

// ------------------------------------------------------------------------------------- published map (job read) ----
// Open addressing on the Faction component's address. A row slot points at its 256 relations; pairs hash both
// addresses. Rebuilt in Pre (no job runs then), so the hooks read a stable snapshot without locks.
struct Snapshot {
    struct RowSlot {
        const uint8_t* key;
        const uint8_t* rel;
    };
    struct PairSlot {
        const uint8_t* a;
        const uint8_t* b;
        uint8_t rel;
    };
    std::vector<RowSlot> rows;    // power of two, empty key = free
    std::vector<PairSlot> pairs;  // power of two
    std::vector<uint8_t> row_data;
    uint32_t mapped = 0;
};
Snapshot g_snap[2];
std::atomic<Snapshot*> g_live{nullptr};
int g_snap_next = 0;

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
        if (r.key == key) return r.rel;
    }
    return nullptr;
}

inline uint8_t find_pair(const Snapshot* s, const uint8_t* a, const uint8_t* b) {
    if (s->pairs.empty()) return 0;
    const size_t mask = s->pairs.size() - 1;
    for (size_t i = (ptr_hash(a) * 31 + ptr_hash(b)) & mask, n = 0; n < s->pairs.size(); i = (i + 1) & mask, ++n) {
        const auto& p = s->pairs[i];
        if (!p.a) return 0;
        if (p.a == a && p.b == b) return p.rel;
    }
    return 0;
}

// ---------------------------------------------------------------------------------------------------- hooks ----
using PtrFn = bool (*)(const uint8_t*, const uint8_t*);
using PtrOptFn = bool (*)(const uint8_t*, const uint8_t*, const void*);
using ValFn = bool (*)(uint8_t, uint8_t);

enum : uint32_t { kHostile = 1, kHostileValue = 2, kFriend = 4, kFriendOpt = 8, kFriendValue = 16 };
uintptr_t g_at[5] = {};
safetyhook::InlineHook g_h[5];
uint32_t g_resolved = 0, g_installed = 0;
PtrFn g_o_hostile = nullptr, g_o_friend = nullptr;
PtrOptFn g_o_friend_opt = nullptr;
ValFn g_o_hostile_value = nullptr, g_o_friend_value = nullptr;

std::atomic<uint64_t> g_calls{0}, g_answered{0}, g_by_pair{0}, g_by_row{0}, g_by_table{0};

// a's relation toward b, by their Faction components; 0 = the game's
uint8_t relation_ptr(const uint8_t* a, const uint8_t* b) {
    if (!a || !b) return 0;
    if (const Snapshot* s = g_live.load(std::memory_order_acquire)) {
        if (uint8_t r = find_pair(s, a, b)) {
            g_by_pair.fetch_add(1, std::memory_order_relaxed);
            return r;
        }
        if (const uint8_t* row = find_row(s, a)) {
            if (uint8_t r = row[*b]) {
                g_by_row.fetch_add(1, std::memory_order_relaxed);
                return r;
            }
        }
    }
    if (g_table_n.load(std::memory_order_relaxed)) {
        if (uint8_t r = g_table[(static_cast<uint32_t>(*a) << 8) | *b]) {
            g_by_table.fetch_add(1, std::memory_order_relaxed);
            return r;
        }
    }
    return 0;
}

uint8_t relation_val(uint8_t a, uint8_t b) {
    if (!g_table_n.load(std::memory_order_relaxed)) return 0;
    uint8_t r = g_table[(static_cast<uint32_t>(a) << 8) | b];
    if (r) g_by_table.fetch_add(1, std::memory_order_relaxed);
    return r;
}

inline int hostile_of(uint8_t r) { return r == KILN_REL_HOSTILE ? 1 : r ? 0 : -1; }
inline int friend_of(uint8_t r) { return r == KILN_REL_FRIENDLY ? 1 : r ? 0 : -1; }

bool hk_hostile(const uint8_t* a, const uint8_t* b) {
    g_calls.fetch_add(1, std::memory_order_relaxed);
    if (int v = hostile_of(relation_ptr(a, b)); v >= 0) {
        g_answered.fetch_add(1, std::memory_order_relaxed);
        return v != 0;
    }
    return g_o_hostile(a, b);
}
bool hk_friend(const uint8_t* a, const uint8_t* b) {
    g_calls.fetch_add(1, std::memory_order_relaxed);
    if (int v = friend_of(relation_ptr(a, b)); v >= 0) {
        g_answered.fetch_add(1, std::memory_order_relaxed);
        return v != 0;
    }
    return g_o_friend(a, b);
}
bool hk_friend_opt(const uint8_t* a, const uint8_t* b, const void* o) {
    g_calls.fetch_add(1, std::memory_order_relaxed);
    if (int v = friend_of(relation_ptr(a, b)); v >= 0) {
        g_answered.fetch_add(1, std::memory_order_relaxed);
        return v != 0;
    }
    return g_o_friend_opt(a, b, o);
}
bool hk_hostile_value(uint8_t a, uint8_t b) {
    g_calls.fetch_add(1, std::memory_order_relaxed);
    if (int v = hostile_of(relation_val(a, b)); v >= 0) {
        g_answered.fetch_add(1, std::memory_order_relaxed);
        return v != 0;
    }
    return g_o_hostile_value(a, b);
}
bool hk_friend_value(uint8_t a, uint8_t b) {
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

// The hostile pointer form is what targeting asks: without it nothing here would mean anything.
bool usable() { return (g_resolved & kHostile) != 0; }

// Called in Pre only: no job runs, so no hook is inside the functions it patches.
bool install() {
    if (g_installed) return true;
    if (!usable()) return false;
    const uint64_t t0 = GetTickCount64();
    for (int i = 0; i < 5; ++i) {
        if (!g_at[i]) continue;
        auto r = safetyhook::create_inline(reinterpret_cast<void*>(g_at[i]), kDetours[i]);
        if (!r) {
            NA_W("the hook on %s (0x%llx) failed", kNames[i], static_cast<unsigned long long>(g_at[i]));
            continue;
        }
        g_h[i] = std::move(r);
        g_installed |= 1u << i;
    }
    if (!(g_installed & kHostile)) {
        for (auto& h : g_h) h = {};
        g_installed = 0;
        return false;
    }
    g_o_hostile = g_h[0].original<PtrFn>();
    g_o_hostile_value = g_h[1] ? g_h[1].original<ValFn>() : nullptr;
    g_o_friend = g_h[2] ? g_h[2].original<PtrFn>() : nullptr;
    g_o_friend_opt = g_h[3] ? g_h[3].original<PtrOptFn>() : nullptr;
    g_o_friend_value = g_h[4] ? g_h[4].original<ValFn>() : nullptr;
    NA_I("faction hooks installed (0x%x of 0x%x, %llu ms)", g_installed, g_resolved,
         static_cast<unsigned long long>(GetTickCount64() - t0));
    return true;
}

void uninstall() {
    if (!g_installed) return;
    for (auto& h : g_h) h = {};
    g_installed = 0;
    NA_I("faction hooks removed: nothing sets a relation");
}

// ------------------------------------------------------------------------------------------------ Pre ----
bool g_dirty_table = false;
bool g_wanted = false;  // anything set: hooks wanted

// Cell by cell, never through zero: a hook may read the table while this runs.
void rebuild_table() {
    static uint8_t next[256 * 256];
    std::memset(next, 0, sizeof next);
    std::map<uint16_t, Cell> best;
    for (auto& [o, cells] : g_cells)
        for (auto& [k, c] : cells)
            if (auto it = best.find(k); it == best.end() || it->second.seq < c.seq) best[k] = c;
    for (auto& [k, c] : best) next[k] = c.rel;
    for (size_t i = 0; i < sizeof next; ++i)
        if (g_table[i] != next[i]) reinterpret_cast<volatile uint8_t*>(g_table)[i] = next[i];
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
    Snapshot& s = g_snap[g_snap_next];
    s.rows.assign(g_rows.empty() ? 0 : pow2(g_rows.size()), Snapshot::RowSlot{nullptr, nullptr});
    s.pairs.assign(g_pairs.empty() ? 0 : pow2(g_pairs.size()), Snapshot::PairSlot{nullptr, nullptr, 0});
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
        if (!g_world->exists(it->first)) {
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
                    s.rows[i] = {key, data};
                    break;
                }
        }
        ++it;
    }
    for (auto it = g_pairs.begin(); it != g_pairs.end();) {
        if (!g_world->exists(it->first.a) || !g_world->exists(it->first.b)) {
            it = g_pairs.erase(it);
            continue;
        }
        const uint8_t* a = fac(it->first.a);
        const uint8_t* b = fac(it->first.b);
        if (a && b) {
            const size_t mask = s.pairs.size() - 1;
            for (size_t i = (ptr_hash(a) * 31 + ptr_hash(b)) & mask;; i = (i + 1) & mask)
                if (!s.pairs[i].a) {
                    s.pairs[i] = {a, b, it->second.rel};
                    break;
                }
        }
        ++it;
    }
    g_live.store(&s, std::memory_order_release);
    g_snap_next ^= 1;
}

void KILN_CALL on_pre(const KilnTickInfo*, void*) {
    if (g_dirty_table) rebuild_table();
    const bool want = g_table_n.load(std::memory_order_relaxed) || !g_rows.empty() || !g_pairs.empty();
    if (want && !g_installed) {
        if (!install()) {
            // keep the settings; the game answers alone
            static bool warned = false;
            if (!warned) NA_W("relations are set, and the faction hooks could not go in: the game decides alone");
            warned = true;
        }
    }
    if (g_world && server_world_ok()) publish();
    if (!want && g_installed) {
        g_live.store(nullptr, std::memory_order_release);
        uninstall();
    }
    g_wanted = want;
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
    if (!usable()) return KILN_ERR_UNAVAILABLE;
    const uint16_t k = static_cast<uint16_t>((a << 8) | b);
    if (relation == KILN_REL_GAME) {
        auto it = g_cells.find(owner);
        if (it != g_cells.end() && it->second.erase(k)) g_dirty_table = true;
    } else {
        g_cells[owner][k] = Cell{static_cast<uint8_t>(relation), g_seq++};
        g_dirty_table = true;
    }
    // The table takes effect at once (single bytes, the hooks read them as they are), the hooks from the next Pre.
    rebuild_table();
    return KILN_OK;
}

uint32_t KILN_CALL relation_get(uint8_t a, uint8_t b) { return g_table[(static_cast<uint32_t>(a) << 8) | b]; }

KilnResult KILN_CALL entity_row(uint32_t owner, KilnEntity e, const uint8_t* row) {
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    if (!owner || !e) return KILN_ERR_INVALID_ARGUMENT;
    if (!usable()) return KILN_ERR_UNAVAILABLE;
    if (!row) {
        g_rows.erase(e);
        return KILN_OK;
    }
    for (int i = 0; i < 256; ++i)
        if (row[i] > KILN_REL_NEUTRAL) return KILN_ERR_INVALID_ARGUMENT;
    if (g_world && !g_world->exists(e)) return KILN_ERR_NOT_FOUND;
    Row r{owner, {}};
    std::memcpy(r.rel, row, 256);
    g_rows[e] = r;
    return KILN_OK;
}

KilnResult KILN_CALL pair_set(uint32_t owner, KilnEntity e, KilnEntity other, uint32_t relation, float seconds) {
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    if (!owner || !e || !other || relation > KILN_REL_NEUTRAL) return KILN_ERR_INVALID_ARGUMENT;
    if (!usable()) return KILN_ERR_UNAVAILABLE;
    if (relation == KILN_REL_GAME) {
        g_pairs.erase(PairKey{e, other});
        return KILN_OK;
    }
    const uint64_t until = seconds > 0 ? GetTickCount64() + static_cast<uint64_t>(seconds * 1000.0f) : 0;
    g_pairs[PairKey{e, other}] = Pair{owner, static_cast<uint8_t>(relation), until};
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
    if (!usable()) return KILN_ERR_UNAVAILABLE;
    const uint8_t* fa = faction_of(a);
    const uint8_t* fb = faction_of(b);
    if (!fa || !fb) return KILN_ERR_NOT_FOUND;
    // as the hooks answer (they may not be in before the next Pre): Kiln's relation, else the game's own code
    const uint8_t r = relation_ptr(fa, fb);
    const PtrFn game_hostile = g_installed ? g_o_hostile : reinterpret_cast<PtrFn>(g_at[0]);
    const PtrOptFn game_friend = g_installed && g_o_friend_opt ? g_o_friend_opt : reinterpret_cast<PtrOptFn>(g_at[3]);
    const uint8_t opt[2] = {0, 0};
    if (hostile) *hostile = hostile_of(r) >= 0 ? static_cast<uint32_t>(hostile_of(r)) : game_hostile(fa, fb) ? 1u : 0u;
    if (friendly) *friendly = friend_of(r) >= 0 ? static_cast<uint32_t>(friend_of(r)) : game_friend && game_friend(fa, fb, opt) ? 1u : 0u;
    return KILN_OK;
}

KilnResult KILN_CALL query_factions(uint8_t a, uint8_t b, uint32_t* hostile, uint32_t* friendly) {
    if (!(g_resolved & kHostileValue) || !(g_resolved & kFriendValue)) return KILN_ERR_UNAVAILABLE;
    const uint8_t r = g_table[(static_cast<uint32_t>(a) << 8) | b];
    const ValFn game_hostile = g_installed && g_o_hostile_value ? g_o_hostile_value : reinterpret_cast<ValFn>(g_at[1]);
    const ValFn game_friend = g_installed && g_o_friend_value ? g_o_friend_value : reinterpret_cast<ValFn>(g_at[4]);
    if (hostile) *hostile = hostile_of(r) >= 0 ? static_cast<uint32_t>(hostile_of(r)) : game_hostile(a, b) ? 1u : 0u;
    if (friendly) *friendly = friend_of(r) >= 0 ? static_cast<uint32_t>(friend_of(r)) : game_friend(a, b) ? 1u : 0u;
    return KILN_OK;
}

KilnResult KILN_CALL status(KilnNpcAiStatus* out) {
    if (!out || out->size < sizeof(KilnNpcAiStatus)) return KILN_ERR_INVALID_ARGUMENT;
    out->resolved = g_resolved;
    out->installed = g_installed;
    out->relations = g_table_n.load(std::memory_order_relaxed);
    out->rows = static_cast<uint32_t>(g_rows.size());
    out->pairs = static_cast<uint32_t>(g_pairs.size());
    const Snapshot* s = g_live.load(std::memory_order_acquire);
    out->mapped = s ? s->mapped : 0;
    out->reserved = 0;
    out->calls = g_calls.load(std::memory_order_relaxed);
    out->answered = g_answered.load(std::memory_order_relaxed);
    out->by_pair = g_by_pair.load(std::memory_order_relaxed);
    out->by_row = g_by_row.load(std::memory_order_relaxed);
    out->by_table = g_by_table.load(std::memory_order_relaxed);
    return KILN_OK;
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
                             &status};

void KILN_CALL on_ready(void*) {
    g_world = static_cast<const KilnWorldApi*>(g_api->service(KilnStr{KILN_WORLD_SERVICE, sizeof(KILN_WORLD_SERVICE) - 1}, KILN_WORLD_VERSION));
    if (g_world && g_world->component_hash) g_faction_hash = g_world->component_hash(KilnStr{"Faction", 7});
    if (!g_world) NA_W("kiln.world is not here: only the faction table works (no creature's own relations)");
}

}  // namespace

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < offsetof(KilnApi, side)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = KilnStr{KILN_NPCAI_SERVICE, sizeof(KILN_NPCAI_SERVICE) - 1};
    info->version = KilnStr{"1", 1};
    info->on_ready = &on_ready;
    resolve();
    if (!usable()) {
        NA_W("the faction functions are not found on this build (0x%x): kiln.npcai is unavailable, the game decides who fights whom", g_resolved);
        return KILN_ERR_UNAVAILABLE;
    }
    KilnHandle h = 0;
    if (KilnResult rc = api->subscribe_tick(owner, KILN_TICK_PRE, &on_pre, nullptr, &h); rc != KILN_OK) return rc;
    NA_I("resolved 0x%x of 0x1f (Hostile, HostileValue, Friend, FriendOpt, FriendValue); hooks go in with the first relation", g_resolved);
    return api->register_service(owner, KilnStr{KILN_NPCAI_SERVICE, sizeof(KILN_NPCAI_SERVICE) - 1}, &kTable);
}
