// SPDX-License-Identifier: MIT
// kiln.knowledge v1 (kiln_knowledge.h; usage docs/kiln/services/knowledge.md, evidence docs/re/quests.md and
// docs/re/creatures.md section 7, symbols sigdb/symbols/{server,client}.d/quests.json).
//
//  - Reads (tick thread, Pre/Post): the knowledge registry's world map and player blocks (open hash maps id -> u64
//    state) with the game's own lookup (KnowledgeIndex) and the static objects of GameKnowledgeResource with the
//    game's StaticWorldFind / StaticPlayerFind.
//  - Changes: a detour on the game's queue apply (KnowledgeApplyQueue, called once per step by
//    update_knowledge_system_server, a barrier program: nothing else runs meanwhile). After the game's own queue,
//    Kiln's requests run in order: grants through the game's per-operation apply (KnowledgeApplyOp: XP, analytics,
//    change list, version), revokes / sets as direct writes of the same three things. Results are completed there and
//    delivered in Post.
//  - Change events: in Post the change lists of this step (cleared by the game at the start of its next apply) are
//    diffed against Kiln's shadow copy of each map.
//  - Program kiln_knowledge (kiln.world, SYSTEM, events_in ServerSelectDialogOptionEvent): dialogue select / stop
//    events; the fallback grant push when the detour is off.
//  - Security (opt-in, kiln/config/knowledge.json): mid hooks before the push of the client's TriggerKnowledgeAction
//    (handle_knowledge_trigger_input) and after FindDialogLine in unlock_knowledge_from_dialog_option_input. A held
//    operation is neutralised in place (id 0, which the game drops) and decided in Post.
//  - Every call into the game and every read of its memory is SEH-guarded; a fault turns that part off (logged).
#include <windows.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <bit>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <deque>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include "knowledge_internal.h"
#include "nlohmann/json.hpp"
#include "safetyhook.hpp"
#include "services/kiln_world.h"

namespace kiln::knowledge {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;

void logf(int level, const char* fmt, ...) {
    if (!g_api) return;
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    if (n > static_cast<int>(sizeof buf) - 1) n = sizeof buf - 1;
    g_api->log(g_owner, level, KilnStr{buf, static_cast<size_t>(n)});
}

bool copy_guarded(void* dst, const void* src, size_t n) {
    if (!src) return false;
    __try {
        std::memcpy(dst, src, n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool blob(uintptr_t field, uintptr_t* base, uint32_t* count) {
    int32_t rel = 0;
    uint32_t n = 0;
    if (!rd(field, &rel) || !rd(field + 4, &n)) return false;
    *count = n;
    *base = rel ? field + static_cast<intptr_t>(rel) : 0;
    return true;
}

namespace {

static_assert(sizeof(KilnKnowledgeInfo) == 28 && sizeof(KilnKnowledgeState) == 32 && sizeof(KilnKnowledgeResult) == 184 &&
              sizeof(KilnKnowledgeRequest) == 40 && sizeof(KilnKnowledgeQuery) == 12 && sizeof(KilnKnowledgeName) == 32 &&
              sizeof(KilnJournalQuest) == 64 && sizeof(KilnJournalStep) == 56 && sizeof(KilnKnowledgeNpc) == 36 &&
              sizeof(KilnKnowledgeEvent) == 64 && sizeof(KilnKnowledgeOp) == 16 && sizeof(KilnKnowledgeSettings) == 20 &&
              sizeof(KilnKnowledgeStatus) == 296);

const KilnWorldApi* W = nullptr;

// ------------------------------------------------------------------------------------------- the game ---------
using IndexFn = uint64_t (*)(const void* map, const uint32_t* id);
using ContextFn = uint32_t (*)(uint32_t entity);
using PushFn = bool (*)(void* queue, const void* op16);
using StaticFindFn = const uint8_t* (*)(const void* map, uint32_t id);
using ApplyOpFn = void (*)(void* reg, void* sink, const void* op16);
using ApplyQueueFn = void (*)(void* reg, void* queue, void* sink);
using ListAppendFn = void (*)(void* list, uint32_t id, uint64_t state);
using StoreFn = bool (*)(void* map, const uint32_t* id, const uint64_t* state);
using QueryEvalFn = uint8_t* (*)(uint8_t* out16, void* knowledge, const void* query12, uint32_t player, const void* opts);
using ProgramEntityFn = const uint32_t* (*)(void* ctx, uint32_t* out);

struct Fns {
    IndexFn index = nullptr;
    ContextFn context = nullptr;
    PushFn push = nullptr;
    StaticFindFn world_find = nullptr, player_find = nullptr;
    ApplyOpFn apply_op = nullptr;
    uintptr_t apply_queue = 0;
    ListAppendFn list_append = nullptr;
    StoreFn store = nullptr;
    QueryEvalFn query_eval = nullptr;
    ProgramEntityFn program_entity = nullptr;
    uintptr_t dialog_site = 0, input_site = 0;
} F;

struct Layout {
    int64_t system = -1;  // G38Knowledge -> registry
    int64_t reg_data = -1, data_resource = -1, data_player_map = -1, static_type = -1, static_flags = -1;
    int64_t world_map = -1, world_cap = -1, world_values = -1, world_bits = -1, world_count = -1, world_keys = -1,
            world_dirty = -1, world_changes = -1, world_version = -1;
    int64_t single = -1, blocks = -1, block_valid = -1, block_cap = -1, block_values = -1, block_bits = -1,
            block_count = -1, block_keys = -1, block_changes = -1, block_version = -1, block_dirty = -1, stride = 0;
    int64_t list_count = -1, list_entry = 16;
    int64_t queue_count = -1, queue_cap = 2048;
    int64_t npcdialog_resource = -1, input_knowledge = -1;
    // event queue of a program (combat/evq.*, the engine's generic layout)
    int64_t evq_first = -1, evq_blocks = -1, evq_count = -1, evq_next = -1, evq_shift = -1;
} L;

// What works (set in main / per world; read anywhere).
std::atomic<bool> g_know_ok{false}, g_changes_ok{false}, g_journal_ok{false}, g_npc_ok{false}, g_dialogue_ok{false},
    g_input_ok{false}, g_program_ok{false}, g_world_ok{false};
std::mutex g_mu;          // reason, results, pending, events, held
std::string g_reason;

void set_reason(const char* fmt, ...) {
    char buf[160];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    {
        std::lock_guard lock(g_mu);
        if (g_reason.empty()) g_reason = buf;
    }
    KW("%s", buf);
}

// ------------------------------------------------------------------------------------------- guarded calls ----
uint64_t g_index(const void* map, uint32_t id) {
    __try {
        return F.index(map, &id);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return ~0ull;
    }
}
const uint8_t* g_find(StaticFindFn fn, const void* map, uint32_t id, bool* fault) {
    __try {
        return fn(map, id);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *fault = true;
        return nullptr;
    }
}
bool g_push(void* queue, const void* op16) {
    __try {
        return F.push(queue, op16);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
uint32_t g_context(uint32_t entity) {
    __try {
        return F.context(entity);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}
bool g_apply_op(void* reg, void* sink, const void* op16) {
    __try {
        F.apply_op(reg, sink, op16);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool g_list_append(void* list, uint32_t id, uint64_t state) {
    __try {
        F.list_append(list, id, state);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool g_store(void* map, uint32_t id, uint64_t state) {
    __try {
        return F.store(map, &id, &state);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
// 0 ok, else the game's error byte (0x100 = fault)
int g_query_eval(void* know, const void* q12, uint32_t player, uint32_t mode, uint8_t* result) {
    __try {
        alignas(8) uint8_t out[16] = {};
        alignas(8) uint8_t opts[8] = {};
        std::memcpy(opts, &mode, 4);
        opts[4] = mode != 0 ? 1 : 0;
        F.query_eval(out, know, q12, player, opts);
        *result = out[8];
        return out[0];
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0x100;
    }
}
uint32_t g_program_entity(void* ctx) {
    __try {
        uint32_t tmp = 0;
        const uint32_t* p = F.program_entity(ctx, &tmp);
        return p ? *p : 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}
template <class T>
bool wr(uintptr_t a, const T& v) {
    if (!a) return false;
    __try {
        *reinterpret_cast<T*>(a) = v;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// ------------------------------------------------------------------------------------------- world ------------
struct World {
    uintptr_t world = 0;
    uintptr_t g38 = 0, reg = 0, queue = 0, journal = 0, npcreg = 0;
    uintptr_t resource = 0;  // GameKnowledgeResource
    uint32_t world_objects = 0, player_objects = 0;
    bool looked = false;
} g_w;  // tick thread; reg/resource also read from other threads (set once per world, before use)
std::atomic<uintptr_t> g_reg_any{0}, g_res_any{0};
std::atomic<uint64_t> g_step{0};

uintptr_t player_block(uintptr_t reg, KilnEntity e, bool need_valid = true) {
    if (!reg || e == 0 || e > 1023) return 0;
    uintptr_t blocks = 0;
    uint64_t n = 0;
    uint8_t single = 0, valid = 0;
    if (!rd(reg + L.single, &single) || !rd(reg + L.blocks, &blocks) || !blocks || !rd(reg + L.blocks + 8, &n)) return 0;
    const uint64_t i = single ? 0 : e - 1;
    if (i >= n || n > 64) return 0;
    const uintptr_t b = blocks + i * static_cast<uintptr_t>(L.stride);
    if (need_valid && (!rd(b + L.block_valid, &valid) || !valid)) return 0;
    return b;
}
uint32_t block_count(uintptr_t reg) {
    uint64_t n = 0;
    uint8_t single = 0;
    if (!reg || !rd(reg + L.blocks + 8, &n) || n > 64) return 0;
    rd(reg + L.single, &single);
    return single ? 1u : static_cast<uint32_t>(n);
}

// A map view: base = the struct KnowledgeIndex takes; cap / values / bits / keys / count at fixed offsets from it (the
// world map at reg + world_map has the block layout: checked at setup).
struct MapView {
    uintptr_t base = 0;
    uintptr_t changes = 0, version = 0, dirty = 0;
    uint8_t dirty_value = 0;
};
MapView map_of(uintptr_t reg, KilnEntity target) {
    MapView m;
    if (!reg) return m;
    if (target == 0) {
        m.base = reg + L.world_map;
        m.changes = reg + L.world_changes;
        m.version = reg + L.world_version;
        m.dirty = reg + L.world_dirty;
        m.dirty_value = 2;
        return m;
    }
    const uintptr_t b = player_block(reg, target);
    if (!b) return m;
    m.base = b;
    m.changes = b + L.block_changes;
    m.version = b + L.block_version;
    m.dirty = b + L.block_dirty;
    m.dirty_value = 0;
    return m;
}
// state of `id` in map (0xff00000000 for an id without an entry); false on a fault
bool map_state(const MapView& m, uint32_t id, uint64_t* state, bool* present) {
    *present = false;
    *state = 0xff00000000ull;
    if (!m.base) return false;
    const uint64_t i = g_index(reinterpret_cast<const void*>(m.base), id);
    uint64_t cap = 0;
    uintptr_t values = 0;
    if (i == ~0ull || !rd(m.base + L.block_cap, &cap) || !rd(m.base + L.block_values, &values)) return false;
    if (i >= cap) return true;
    *present = true;
    return rd(values + i * 8, state);
}
// every set entry (calls fn(id, state)); false on a fault
template <class Fn>
bool map_each(uintptr_t base, Fn&& fn) {
    uint64_t cap = 0;
    uintptr_t bits = 0, keys = 0, values = 0;
    if (!rd(base + L.block_cap, &cap) || !rd(base + L.block_bits, &bits) || !rd(base + L.block_keys, &keys) ||
        !rd(base + L.block_values, &values) || cap > (1ull << 24) || !bits || !keys || !values)
        return false;
    std::vector<uint64_t> word((cap + 63) / 64);
    if (!word.empty() && !copy_guarded(word.data(), reinterpret_cast<const void*>(bits), word.size() * 8)) return false;
    std::vector<uint32_t> k;
    std::vector<uint64_t> v;
    for (uint64_t w = 0; w < word.size(); ++w) {
        uint64_t x = word[w];
        while (x) {
            const unsigned b = static_cast<unsigned>(std::countr_zero(x));
            x &= x - 1;
            const uint64_t i = w * 64 + b;
            if (i >= cap) break;
            uint32_t id = 0;
            uint64_t st = 0;
            if (!rd(keys + i * 4, &id) || !rd(values + i * 8, &st)) return false;
            fn(id, st);
        }
    }
    return true;
}

// The static objects (GameKnowledgeResource maps). Any thread (the resource is read-only data).
const uint8_t* world_object(uint32_t id) {
    const uintptr_t res = g_res_any.load(std::memory_order_acquire);
    if (!res || !F.world_find) return nullptr;
    bool fault = false;
    const uint8_t* o = g_find(F.world_find, reinterpret_cast<const void*>(res), id, &fault);
    return fault ? nullptr : o;
}
const uint8_t* player_object(uint32_t id) {
    const uintptr_t res = g_res_any.load(std::memory_order_acquire);
    if (!res || !F.player_find) return nullptr;
    bool fault = false;
    const uint8_t* o = g_find(F.player_find, reinterpret_cast<const void*>(res + L.data_player_map), id, &fault);
    return fault ? nullptr : o;
}
uint8_t object_type(const uint8_t* o) {
    uint8_t t = KILN_KNOW_NONE;
    return o && rd(reinterpret_cast<uintptr_t>(o) + L.static_type, &t) ? t : KILN_KNOW_NONE;
}
uint32_t object_flags(const uint8_t* o) {
    uint32_t f = 0;
    return o && rd(reinterpret_cast<uintptr_t>(o) + L.static_flags, &f) ? f : 0;
}

KilnResult fill_info(uint32_t id, KilnKnowledgeInfo* out) {
    KilnKnowledgeInfo i{};
    i.size = sizeof i;
    i.id = id;
    i.world_type = i.player_type = KILN_KNOW_NONE;
    if (const uint8_t* w = world_object(id)) {
        i.flags |= KILN_KNOW_WORLD;
        i.world_type = object_type(w);
        i.world_flags = object_flags(w);
        uint8_t b[12] = {};
        if (copy_guarded(b, w, 12)) {
            if (b[4]) i.flags |= KILN_KNOW_ANALYTICS;
            if (b[5]) i.flags |= KILN_KNOW_TWIN;
        }
    }
    if (const uint8_t* p = player_object(id)) {
        i.flags |= KILN_KNOW_PLAYER;
        i.player_type = object_type(p);
        i.player_flags = object_flags(p);
        uint8_t b[24] = {};
        if (copy_guarded(b, p, 24)) {
            if (b[4]) i.flags |= KILN_KNOW_ANALYTICS;
            std::memcpy(&i.experience_id, b + 0x10, 4);
            i.skill_points = b[0x14];
            if (i.experience_id) i.flags |= KILN_KNOW_EXPERIENCE;
            if (i.skill_points) i.flags |= KILN_KNOW_SKILL_POINTS;
        }
    }
    const uint32_t size = out->size ? std::min<uint32_t>(out->size, sizeof i) : sizeof i;
    std::memcpy(out, &i, size);
    out->size = size;
    return i.flags ? KILN_OK : KILN_ERR_NOT_FOUND;
}

// ------------------------------------------------------------------------------------------- events -----------
constexpr size_t kRing = 4096;
std::array<KilnKnowledgeEvent, kRing> g_ring{};
std::atomic<uint64_t> g_seq{0};
std::atomic<uint64_t> c_changes{0}, c_client{0}, c_vetoed{0}, c_dlg{0}, c_held{0}, c_dropped{0}, c_submitted{0},
    c_applied{0}, c_failed{0};
std::atomic<uint32_t> g_queue_max{0};

void emit(KilnKnowledgeEvent e) {  // caller holds g_mu
    e.seq = g_seq.load() + 1;
    e.step = e.step ? e.step : g_step.load();
    g_ring[e.seq % kRing] = e;
    g_seq.store(e.seq);
}

// ------------------------------------------------------------------------------------------- requests ---------
struct Req {
    uint64_t id = 0;
    KilnKnowledgeRequest r{};
    uint64_t submitted = 0;  // step
    uint8_t type = KILN_KNOW_NONE;
};
std::deque<Req> g_pending;                  // guarded by g_mu
std::atomic<uint64_t> g_next_req{1};
struct Done {
    KilnKnowledgeResult res{};
    KilnKnowledgeDoneFn cb = nullptr;
    void* user = nullptr;
};
std::vector<Done> g_done;                   // applied in the detour, delivered in Post (g_mu)
std::deque<KilnKnowledgeResult> g_results;  // the last 256 (g_mu)
std::unordered_map<uint64_t, uint64_t> g_sources;  // (target << 32 | id) -> request, this step's applies (g_mu)
std::atomic<uint64_t> g_detour_runs{0}, g_last_detour_step{0};
// The game's knowledge update has run in this world. It starts when the world runs (lab: step ~270 of a world whose
// first step was ~12 s earlier), after the save was parsed: before that the maps are empty and changes wait.
std::atomic<bool> g_applying{false};
constexpr uint64_t kApplyWait = 50;      // steps a request may wait for an apply in a running world
constexpr uint64_t kLoadWait = 15000;    // ... while the world is still loading (about 10 minutes)
void baseline_world(uintptr_t reg);

void res_reason(KilnKnowledgeResult& r, const char* fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(r.reason, sizeof r.reason, fmt, ap);
    va_end(ap);
}

// The value a grant / revoke / set leads to (from the merge rules of 0x99dc90), for checks and results.
uint64_t new_state(uint32_t op, uint8_t type, uint64_t old, uint32_t value) {
    const uint32_t lo = static_cast<uint32_t>(old);
    uint64_t hi = old & 0xffffffff00000000ull;
    switch (op) {
    case KILN_KNOW_OP_REVOKE:
        if (type == KILN_KNOW_FLAGS && value != KILN_KNOW_ALL) {
            const uint32_t bit = 1u << (value & 31);
            if (value < 8) hi |= static_cast<uint64_t>(bit) << 32;
            return hi | (lo & ~bit);
        }
        return 0xff00000000ull;
    case KILN_KNOW_OP_SET:
        return value ? (hi | value) : 0xff00000000ull;
    default:
        return old;
    }
}

// Applies one request inside the game's apply (detour). reg / sink: the apply's own arguments.
void apply_request(uintptr_t reg, void* sink, const Req& q, Done& d) {
    KilnKnowledgeResult& r = d.res;
    r.size = sizeof r;
    r.op = q.r.op;
    r.request = q.id;
    r.target = q.r.target;
    r.id = q.r.id;
    r.step = g_step.load();
    r.steps = static_cast<uint32_t>(r.step >= q.submitted ? r.step - q.submitted : 0);
    MapView m = map_of(reg, q.r.target);
    if (!m.base) {
        r.status = KILN_ERR_UNAVAILABLE;
        res_reason(r, q.r.target ? "player %u is not in game (no knowledge loaded)" : "no world knowledge", q.r.target);
        return;
    }
    uint64_t before = 0;
    bool present = false;
    if (!map_state(m, q.r.id, &before, &present)) {
        r.status = KILN_ERR_INTERNAL;
        res_reason(r, "the knowledge map did not read");
        return;
    }
    r.before = static_cast<uint32_t>(before);
    if (q.r.op == KILN_KNOW_OP_GRANT) {
        alignas(8) uint8_t op[16] = {};
        std::memcpy(op, &q.r.id, 4);
        op[4] = q.type;
        const uint32_t v = q.type == KILN_KNOW_BOOL ? 1u : q.r.value;
        std::memcpy(op + 8, &v, 4);
        const uint32_t ctx = q.r.target ? g_context(q.r.target) : 0u;
        std::memcpy(op + 12, &ctx, 4);
        if (!g_apply_op(reinterpret_cast<void*>(reg), sink, op)) {
            r.status = KILN_ERR_INTERNAL;
            res_reason(r, "the game's knowledge apply faulted");
            g_changes_ok = false;
            return;
        }
    } else {
        const uint64_t ns = new_state(q.r.op, q.type, before, q.r.value);
        if (static_cast<uint32_t>(ns) != static_cast<uint32_t>(before)) {
            bool ok = true;
            if (present) {
                uintptr_t values = 0;
                const uint64_t i = g_index(reinterpret_cast<const void*>(m.base), q.r.id);
                ok = rd(m.base + L.block_values, &values) && values && wr(values + i * 8, ns);
            } else {
                ok = g_store(reinterpret_cast<void*>(m.base), q.r.id, ns);
            }
            uint16_t ver = 0;
            ok = ok && wr(m.dirty, m.dirty_value) && g_list_append(reinterpret_cast<void*>(m.changes), q.r.id, ns) &&
                 rd(m.version, &ver) && wr(m.version, static_cast<uint16_t>(ver + (ver == 0xffff ? 2 : 1)));
            if (!ok) {
                r.status = KILN_ERR_INTERNAL;
                res_reason(r, "the direct knowledge write faulted");
                return;
            }
        }
    }
    uint64_t after = 0;
    map_state(m, q.r.id, &after, &present);
    r.after = static_cast<uint32_t>(after);
    r.changed = r.after != r.before;
    r.status = KILN_OK;
    if (!r.changed)
        res_reason(r, q.r.op == KILN_KNOW_OP_GRANT ? "no change (already set: the game only raises values)"
                                                   : "no change (the value was already that)");
}

safetyhook::InlineHook g_apply_hook;

void hk_apply_queue(void* reg, void* queue, void* sink) {
    uint32_t n = 0;
    if (queue && rd(reinterpret_cast<uintptr_t>(queue) + L.queue_count, &n)) {
        uint32_t m = g_queue_max.load();
        while (n > m && !g_queue_max.compare_exchange_weak(m, n)) {
        }
    }
    const bool ours = g_changes_ok.load(std::memory_order_relaxed) && reinterpret_cast<uintptr_t>(reg) == g_reg_any.load();
    if (ours && !g_applying.exchange(true)) baseline_world(reinterpret_cast<uintptr_t>(reg));
    g_apply_hook.call<void>(reg, queue, sink);
    if (!ours) return;
    g_detour_runs.fetch_add(1);
    g_last_detour_step = g_step.load();
    std::deque<Req> todo;
    {
        std::lock_guard lock(g_mu);
        todo.swap(g_pending);
    }
    if (todo.empty()) return;
    std::vector<Done> done;
    done.reserve(todo.size());
    for (const Req& q : todo) {
        Done d;
        d.cb = q.r.callback;
        d.user = q.r.user;
        apply_request(reinterpret_cast<uintptr_t>(reg), sink, q, d);
        done.push_back(d);
    }
    std::lock_guard lock(g_mu);
    for (Done& d : done) {
        if (d.res.status == KILN_OK && d.res.changed)
            g_sources[(static_cast<uint64_t>(d.res.target) << 32) | d.res.id] = d.res.request;
        g_done.push_back(d);
    }
}

// ------------------------------------------------------------------------------------------- shadows / changes --
struct Shadow {
    bool valid = false;
    uint16_t version = 0;
    std::unordered_map<uint32_t, uint32_t> v;
};
Shadow g_world_sh;
std::array<Shadow, 64> g_block_sh;

bool fill_shadow(uintptr_t base, uintptr_t version_at, Shadow& sh) {
    sh.v.clear();
    bool ok = map_each(base, [&](uint32_t id, uint64_t st) {
        if (static_cast<uint32_t>(st)) sh.v[id] = static_cast<uint32_t>(st);
    });
    rd(version_at, &sh.version);
    sh.valid = ok;
    return ok;
}

// The world's first knowledge update: the save's knowledge is in the map now. Its load is not a change (a lab run
// without this reported all 566 entries of the save as gained), so this is where the change events start from.
void baseline_world(uintptr_t reg) {
    uint32_t cnt = 0;
    rd(reg + L.world_map + L.block_count, &cnt);
    {
        std::lock_guard lock(g_mu);
        fill_shadow(reg + L.world_map, reg + L.world_version, g_world_sh);
    }
    KI("first knowledge update of this world at step %llu: world map %u entries (the save's knowledge, the change "
       "events' baseline)", static_cast<unsigned long long>(g_step.load()), cnt);
}

// The change list of one map this step -> events (g_mu held by the caller for emit)
void scan_changes(uintptr_t base, uintptr_t list, uintptr_t version_at, KilnEntity target, Shadow& sh, bool world) {
    uint16_t ver = 0;
    if (!rd(version_at, &ver)) return;
    if (!sh.valid) {
        fill_shadow(base, version_at, sh);
        return;
    }
    if (ver == sh.version) return;
    sh.version = ver;
    uintptr_t data = 0;
    uint64_t count = 0;
    bool full = !rd(list, &data) || !rd(list + L.list_count, &count) || count > 100000 || (count && !data);
    std::vector<std::pair<uint32_t, uint32_t>> seen;
    for (uint64_t i = 0; !full && i < count; ++i) {
        uint8_t e[16] = {};
        if (!copy_guarded(e, reinterpret_cast<const void*>(data + i * static_cast<uintptr_t>(L.list_entry)), 16)) {
            full = true;
            break;
        }
        uint32_t id = 0, value = 0;
        std::memcpy(&id, e, 4);
        std::memcpy(&value, e + 4, 4);
        if (e[12] == 2 && !world) {
            // The game's mark of a character's record: nothing else writes it. The load (0x9a1110) put the record straight
            // into the map and raised a flag; the game's knowledge update (0x9a48e0, in this step) took the flag down,
            // cleared this list and left this one entry (the whole map goes to the client). So the record is the map now,
            // and it is no change. (A flag can't show this from Post: it is down again by then. A grant applied later in the
            // same step, after the entry, is inside the record too; a player that hasn't arrived gets none.)
            fill_shadow(base, version_at, sh);
            KI("player %u: knowledge map %zu set entries from the character's record (the change events' baseline)", target, sh.v.size());
            return;
        }
        if (e[12] != 0) {  // a clear / resend marker: rescan the whole map
            full = true;
            break;
        }
        if (id) seen.emplace_back(id, value);
    }
    if (full || (count == 0 && seen.empty())) {
        // version changed without a readable list: diff the whole map
        std::unordered_map<uint32_t, uint32_t> now;
        if (!map_each(base, [&](uint32_t id, uint64_t st) {
                if (static_cast<uint32_t>(st)) now[id] = static_cast<uint32_t>(st);
            }))
            return;
        seen.clear();
        for (auto& [id, v] : now)
            if (auto it = sh.v.find(id); it == sh.v.end() || it->second != v) seen.emplace_back(id, v);
        for (auto& [id, v] : sh.v)
            if (!now.contains(id)) seen.emplace_back(id, 0u);
    }
    for (auto& [id, value] : seen) {
        auto it = sh.v.find(id);
        const uint32_t old = it == sh.v.end() ? 0u : it->second;
        if (old == value) continue;
        if (value) sh.v[id] = value;
        else if (it != sh.v.end()) sh.v.erase(it);
        KilnKnowledgeEvent ev{};
        ev.kind = KILN_KNOW_EVENT_CHANGED;
        ev.target = target;
        ev.id = id;
        ev.old_value = old;
        ev.new_value = value;
        if (auto s = g_sources.find((static_cast<uint64_t>(target) << 32) | id); s != g_sources.end()) ev.request = s->second;
        ev.extra = world ? object_type(world_object(id)) : object_type(player_object(id));
        emit(ev);
        c_changes.fetch_add(1);
    }
}

// ------------------------------------------------------------------------------------------- holds / security --
struct Held {
    uint64_t id = 0;
    uint32_t kind = 0;  // KILN_KNOW_LISTEN_CLIENT / _DIALOGUE
    KilnEntity player = 0, npc = 0;
    uint32_t line = 0;
    KilnKnowledgeQuery requirement{};
    std::vector<KilnKnowledgeOp> ops;
    uint64_t held_step = 0;
    bool announced = false;
    uint32_t checks = 0;
    float distance = -1.f;
    int32_t decision = KILN_KNOW_DEFAULT;  // ALLOW / DENY from decide(), DEFAULT: the default rule
    bool due = false;                      // decide() was called: resolve at the next pass
};
std::deque<Held> g_held;  // g_mu
std::atomic<uint64_t> g_next_held{1};
std::atomic<uint32_t> g_listen{0};

struct Settings {
    bool veto_client = false, check_dialogue = false, log_client = true;
    float dialogue_range = 12.f;
};
Settings g_set;                        // g_mu
std::unordered_set<uint32_t> g_allow;  // g_mu: file list + runtime list
std::vector<uint32_t> g_allow_runtime;
std::vector<std::string> g_allow_names;  // names from the file, resolved when the catalog exists
std::atomic<bool> g_veto{false}, g_check{false};
std::unordered_set<uint64_t> g_logged_client;  // (player << 32 | id) logged once (g_mu)
std::filesystem::file_time_type g_cfg_time{};
std::filesystem::path g_cfg_path;

void update_flags_locked() {
    g_veto = g_set.veto_client;
    g_check = g_set.check_dialogue;
}

// handle_knowledge_trigger_input, just before the game's KnowledgeQueuePush: rdx = the operation (16 bytes). A vetoed
// operation keeps its slot with id 0 (the game drops unknown ids), so the input still counts as consumed.
safetyhook::MidHook g_input_hook, g_dialog_hook;

void hk_input(safetyhook::Context& c) {
    uint8_t op[16] = {};
    if (!copy_guarded(op, reinterpret_cast<const void*>(c.rdx), 16)) return;
    KilnKnowledgeOp o{};
    std::memcpy(&o.id, op, 4);
    o.type = op[4];
    std::memcpy(&o.value, op + 8, 4);
    std::memcpy(&o.context, op + 12, 4);
    if (!o.id) return;
    c_client.fetch_add(1);
    const KilnEntity player = (o.context & 0xff) == 1 ? (o.context >> 16) : 0;
    bool veto = false;
    uint64_t held = 0;
    {
        std::lock_guard lock(g_mu);
        if (g_veto.load() && !g_allow.contains(o.id)) {
            veto = true;
            held = g_next_held.fetch_add(1);
            Held h;
            h.id = held;
            h.kind = KILN_KNOW_LISTEN_CLIENT;
            h.player = player;
            h.ops.push_back(o);
            h.held_step = g_step.load();
            g_held.push_back(std::move(h));
        }
        KilnKnowledgeEvent ev{};
        ev.kind = KILN_KNOW_EVENT_CLIENT;
        ev.target = player;
        ev.id = o.id;
        ev.old_value = o.type;
        ev.new_value = o.value;
        ev.request = held;
        ev.flags = veto ? 1u : 0u;
        emit(ev);
        if (g_set.log_client && g_logged_client.insert((static_cast<uint64_t>(player) << 32) | o.id).second)
            KI("client knowledge from player %u: 0x%08x type %u value %u (%s; first time this session)", player, o.id,
               o.type, o.value, veto ? "held by the veto" : "passed");
    }
    if (veto) {
        const uint32_t zero = 0;
        if (!copy_guarded(reinterpret_cast<void*>(c.rdx), &zero, 4)) KW("could not neutralise a client operation");
        c_vetoed.fetch_add(1);
    }
}

// unlock_knowledge_from_dialog_option_input, right after FindDialogLine: rax = the line (NpcDialogOptionResourceBase),
// rsi = the player's SelectDialogOptionAction {line id, target NPC, version}, r14 = the program context. Holding sets
// rax to 0: the game then pushes nothing for this input and still marks it consumed.
void hk_dialog(safetyhook::Context& c) {
    if (!c.rax || !(g_check.load(std::memory_order_relaxed) || (g_listen.load(std::memory_order_relaxed) & KILN_KNOW_LISTEN_DIALOGUE)))
        return;
    uint32_t act[2] = {};
    if (!copy_guarded(act, reinterpret_cast<const void*>(c.rsi), 8)) return;
    const uint32_t player = g_program_entity(reinterpret_cast<void*>(c.r14));
    if (!player) return;
    Held h;
    h.kind = KILN_KNOW_LISTEN_DIALOGUE;
    h.player = player;
    h.npc = act[1];
    h.line = act[0];
    copy_guarded(&h.requirement, reinterpret_cast<const void*>(c.rax + 0x20), sizeof h.requirement);
    uintptr_t base = 0;
    uint32_t n = 0;
    const uint32_t ctx = g_context(player);
    if (blob(c.rax + 0x18, &base, &n) && base && n <= 8) {
        for (uint32_t i = 0; i < n; ++i) {
            uint8_t e[12] = {};
            if (!copy_guarded(e, reinterpret_cast<const void*>(base + i * 12u), 12)) break;
            KilnKnowledgeOp o{};
            std::memcpy(&o.id, e, 4);
            o.type = e[4];
            std::memcpy(&o.value, e + 8, 4);
            o.context = ctx;
            h.ops.push_back(o);
        }
    }
    h.ops.push_back(KilnKnowledgeOp{act[0], KILN_KNOW_BOOL, {}, 1u, ctx});  // "line heard" (the line id itself)
    h.held_step = g_step.load();
    {
        std::lock_guard lock(g_mu);
        h.id = g_next_held.fetch_add(1);
        g_held.push_back(std::move(h));
    }
    c_held.fetch_add(1);
    c.rax = 0;
}

std::vector<uint32_t> parse_ids_locked();
void read_settings_file(bool log);

// ------------------------------------------------------------------------------------------- program ----------
KilnHandle g_prog = 0;
int32_t s_evq = -1, s_queue = -1;
struct DlgRec {
    KilnEntity npc, player;
    uint32_t line, kind;
};
std::vector<DlgRec> g_dlg;  // g_mu

void walk_events(const uint8_t* q) {
    if (!q || L.evq_count < 0) return;
    uint32_t n = 0;
    if (!rd(reinterpret_cast<uintptr_t>(q) + L.evq_count, &n) || n == 0 || n > 4096) return;
    uintptr_t blocks = 0;
    uint16_t idx = 0;
    if (!rd(reinterpret_cast<uintptr_t>(q) + L.evq_blocks, &blocks) || !rd(reinterpret_cast<uintptr_t>(q) + L.evq_first, &idx))
        return;
    const uint64_t bs = 1ull << L.evq_shift;
    uintptr_t block = idx ? blocks + (idx - 1) * bs : 0;
    uint32_t off = 0;
    constexpr uint32_t kRec = 0x18;  // ServerSelectDialogOptionEvent: 24 bytes
    std::vector<DlgRec> out;
    for (uint32_t i = 0; i < n; ++i) {
        if (off + kRec > bs - 8) {
            uint16_t nx = 0;
            if (!block || !rd(block + L.evq_next, &nx)) return;
            block = nx ? blocks + (nx - 1) * bs : 0;
            off = 0;
        }
        if (!block) return;
        uint8_t e[24] = {};
        if (!copy_guarded(e, reinterpret_cast<const void*>(block + 8 + off), 24)) return;
        DlgRec r{};
        std::memcpy(&r.npc, e + 8, 4);
        std::memcpy(&r.player, e + 12, 4);
        std::memcpy(&r.line, e + 16, 4);
        r.kind = e[20];
        out.push_back(r);
        off += kRec;
    }
    std::lock_guard lock(g_mu);
    for (auto& r : out)
        if (g_dlg.size() < 4096) g_dlg.push_back(r);
}

void KILN_CALL prog_each(KilnProgramCtx*, uint64_t* iter, KilnEntity, void*) {
    if (s_evq >= 0) walk_events(reinterpret_cast<const uint8_t*>(iter[s_evq]));
    if (g_changes_ok.load(std::memory_order_relaxed) || s_queue < 0) return;
    // fallback: grants through the game's queue (applied in the next step's knowledge update)
    void* queue = reinterpret_cast<void*>(iter[s_queue]);
    if (!queue) return;
    std::deque<Req> todo;
    {
        std::lock_guard lock(g_mu);
        for (auto it = g_pending.begin(); it != g_pending.end();) {
            if (it->r.op == KILN_KNOW_OP_GRANT) {
                todo.push_back(*it);
                it = g_pending.erase(it);
            } else {
                ++it;
            }
        }
    }
    std::vector<Done> done;
    for (const Req& q : todo) {
        alignas(8) uint8_t op[16] = {};
        std::memcpy(op, &q.r.id, 4);
        op[4] = q.type;
        const uint32_t v = q.type == KILN_KNOW_BOOL ? 1u : q.r.value;
        std::memcpy(op + 8, &v, 4);
        const uint32_t ctx = q.r.target ? g_context(q.r.target) : 0u;
        std::memcpy(op + 12, &ctx, 4);
        Done d;
        d.cb = q.r.callback;
        d.user = q.r.user;
        d.res.size = sizeof d.res;
        d.res.op = q.r.op;
        d.res.request = q.id;
        d.res.target = q.r.target;
        d.res.id = q.r.id;
        d.res.step = g_step.load();
        if (g_push(queue, op)) {
            d.res.status = KILN_OK;
            res_reason(d.res, "queued through KnowledgeTriggerQueue (fallback): before/after not measured");
        } else {
            d.res.status = KILN_ERR_BUFFER_TOO_SMALL;
            res_reason(d.res, "KnowledgeTriggerQueue is full");
        }
        done.push_back(d);
    }
    std::lock_guard lock(g_mu);
    for (auto& d : done) g_done.push_back(d);
}

const KilnStr kEvIn[] = {{"ServerSelectDialogOptionEvent", 29}};
const KilnStr kSingRead[] = {{"G38Knowledge", 12}};
const KilnStr kSingWrite[] = {{"KnowledgeTriggerQueue", 21}};

void register_program() {
    W = static_cast<const KilnWorldApi*>(g_api->service(kstr(KILN_WORLD_SERVICE), 1));
    if (!W || W->header.size < sizeof(KilnWorldApi)) {
        W = nullptr;
        set_reason("kiln.world is not available: no dialogue events, no fallback grants");
        return;
    }
    KilnProgramDesc d{};
    d.size = sizeof d;
    d.flags = KILN_PROGRAM_SYSTEM | KILN_PROGRAM_HOST_ONLY;
    d.name = kstr("kiln_knowledge");
    d.each = &prog_each;
    d.singletons_read = KilnNames{kSingRead, 1, 0};
    d.singletons_read_optional = 0x1u;
    d.singletons_write = KilnNames{kSingWrite, 1, 0};
    d.singletons_write_optional = 0x1u;
    const bool events = L.evq_count >= 0 && g_dialogue_ok;
    if (events) d.events_in = KilnNames{kEvIn, 1, 0};
    if (W->register_program(g_owner, &d, &g_prog) != KILN_OK) {
        set_reason("kiln.world refused the program kiln_knowledge");
        g_prog = 0;
        return;
    }
    s_queue = W->program_slot(g_prog, KILN_SLOT_SINGLETON, kSingWrite[0]);
    if (events) s_evq = W->program_slot(g_prog, KILN_SLOT_EVENT_IN, kEvIn[0]);
    KI("program kiln_knowledge accepted (dialogue events slot %d, queue slot %d)", s_evq, s_queue);
}

// ------------------------------------------------------------------------------------------- world setup ------
void setup_world() {
    const uintptr_t world = g_api->server_world();
    if (!world) {
        g_world_ok = false;
        return;
    }
    if (g_w.looked && g_w.world == world) return;
    g_w = World{};
    g_w.world = world;
    g_w.looked = true;
    g_w.g38 = g_api->singleton(world, kstr("G38Knowledge"));
    g_w.queue = g_api->singleton(world, kstr("KnowledgeTriggerQueue"));
    g_w.journal = g_api->singleton(world, kstr("g38_journal::JournalRegistry"));
    g_w.npcreg = g_api->singleton(world, kstr("NpcRegistry"));
    uintptr_t reg = 0, data = 0, res = 0;
    if (!g_w.g38 || !rd(g_w.g38 + L.system, &reg) || !reg || !rd(reg + L.reg_data, &data) || !data ||
        !rd(data + L.data_resource, &res) || !res) {
        set_reason("G38Knowledge / its registry not found in this world");
        g_world_ok = false;
        return;
    }
    g_w.reg = reg;
    g_w.resource = res;
    uint32_t wn = 0, pn = 0;
    rd(res + 8 + 4, &wn);                     // worldKnowledge.keys count
    rd(res + L.data_player_map + 8 + 4, &pn);  // playerKnowledge.keys count
    g_w.world_objects = wn;
    g_w.player_objects = pn;
    g_res_any.store(res, std::memory_order_release);
    g_reg_any.store(reg, std::memory_order_release);
    uint64_t cap = 0;
    uint32_t cnt = 0;
    rd(reg + L.world_map + L.block_cap, &cap);
    rd(reg + L.world_map + L.block_count, &cnt);
    g_world_ok = true;
    g_world_sh = Shadow{};
    for (auto& s : g_block_sh) s = Shadow{};
    KI("world %p: registry %p, GameKnowledgeResource %u world + %u player objects, world map %u entries (capacity "
       "%llu), %u player blocks, queue %p, journal %p, NpcRegistry %p",
       reinterpret_cast<void*>(world), reinterpret_cast<void*>(reg), wn, pn, cnt, static_cast<unsigned long long>(cap),
       block_count(reg), reinterpret_cast<void*>(g_w.queue), reinterpret_cast<void*>(g_w.journal),
       reinterpret_cast<void*>(g_w.npcreg));
    KI("GameKnowledgeResource %p (registry data %p + 0x%llx) %s", reinterpret_cast<void*>(res), reinterpret_cast<void*>(data),
       static_cast<unsigned long long>(L.data_resource), knowledge_resource_origin(res).c_str());
    if (!g_w.journal) KW("singleton g38_journal::JournalRegistry not found: no journal");
    if (!g_w.npcreg) KW("singleton NpcRegistry not found: no NPC roster");
}

void world_reset() {
    g_world_ok = false;
    g_applying = false;
    g_reg_any = 0;
    g_res_any = 0;
    g_w = World{};
    catalog_clear();
    std::lock_guard lock(g_mu);
    g_world_sh = Shadow{};
    for (auto& s : g_block_sh) s = Shadow{};
    for (auto& q : g_pending) {
        Done d;
        d.res.size = sizeof d.res;
        d.res.op = q.r.op;
        d.res.request = q.id;
        d.res.target = q.r.target;
        d.res.id = q.r.id;
        d.res.status = KILN_ERR_UNAVAILABLE;
        res_reason(d.res, "the world stopped before the change was applied");
        d.cb = q.r.callback;
        d.user = q.r.user;
        g_done.push_back(d);
    }
    g_pending.clear();
    g_held.clear();
    g_dlg.clear();
    g_sources.clear();
}

// ------------------------------------------------------------------------------------------- held decisions ---
void resolve_held(uint64_t step) {
    if (!g_w.reg) return;
    std::vector<Held> todo;
    {
        std::lock_guard lock(g_mu);
        for (auto it = g_held.begin(); it != g_held.end();) {
            Held& h = *it;
            const bool listening = (g_listen.load() & h.kind) != 0;
            if (!h.announced) {
                ++it;
                continue;  // announced first (below), decided from the next pass on
            }
            const bool due = h.due || !listening || step >= h.held_step + 8;
            if (!due) {
                ++it;
                continue;
            }
            todo.push_back(std::move(h));
            it = g_held.erase(it);
        }
    }
    for (Held& h : todo) {
        bool allow;
        if (h.decision != KILN_KNOW_DEFAULT) allow = h.decision == KILN_KNOW_ALLOW;
        else if (h.kind == KILN_KNOW_LISTEN_CLIENT) allow = false;  // held only by the veto: dropped
        else allow = !(g_check.load() && (h.checks & (KILN_KNOW_CHECK_REQUIREMENT | KILN_KNOW_CHECK_DISTANCE)));
        bool pushed = true;
        if (allow) {
            for (const KilnKnowledgeOp& o : h.ops) {
                alignas(8) uint8_t op[16] = {};
                std::memcpy(op, &o.id, 4);
                op[4] = o.type;
                std::memcpy(op + 8, &o.value, 4);
                std::memcpy(op + 12, &o.context, 4);
                pushed = g_w.queue && g_push(reinterpret_cast<void*>(g_w.queue), op) && pushed;
            }
        } else {
            c_dropped.fetch_add(h.kind == KILN_KNOW_LISTEN_DIALOGUE ? 1 : 0);
        }
        std::lock_guard lock(g_mu);
        KilnKnowledgeEvent ev{};
        ev.kind = KILN_KNOW_EVENT_RESOLVED;
        ev.target = h.player;
        ev.npc = h.npc;
        ev.id = h.kind == KILN_KNOW_LISTEN_DIALOGUE ? h.line : (h.ops.empty() ? 0 : h.ops[0].id);
        ev.request = h.id;
        ev.old_value = h.kind;
        ev.new_value = allow && pushed ? 1u : 0u;
        ev.flags = h.due ? 1u : 0u;
        emit(ev);
        if (allow && !pushed) KW("held entry %llu: KnowledgeTriggerQueue full, part of it was lost", static_cast<unsigned long long>(h.id));
    }
}

// The checks of a held dialogue line (tick thread, Post) and its announcement.
void announce_held() {
    std::lock_guard lock(g_mu);
    for (Held& h : g_held) {
        if (h.announced) continue;
        h.announced = true;
        if (h.kind == KILN_KNOW_LISTEN_DIALOGUE) {
            if (h.requirement.id && g_journal_ok && g_w.g38) {
                uint8_t ok = 0;
                const int err = g_query_eval(reinterpret_cast<void*>(g_w.g38), &h.requirement, h.player,
                                             KILN_KNOW_MODE_PLAYER, &ok);
                if (err) h.checks |= KILN_KNOW_CHECK_ERROR;
                else if (!ok) h.checks |= KILN_KNOW_CHECK_REQUIREMENT;
            }
            KilnVec3 a{}, b{};
            if (W && W->position(h.player, &a) == KILN_OK && W->position(h.npc, &b) == KILN_OK) {
                const double dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
                h.distance = static_cast<float>(std::sqrt(dx * dx + dy * dy + dz * dz));
                if (g_set.dialogue_range > 0 && h.distance > g_set.dialogue_range) h.checks |= KILN_KNOW_CHECK_DISTANCE;
            } else {
                h.checks |= KILN_KNOW_CHECK_ERROR;
            }
            KilnKnowledgeEvent ev{};
            ev.kind = KILN_KNOW_EVENT_DIALOGUE_HELD;
            ev.target = h.player;
            ev.npc = h.npc;
            ev.id = h.line;
            ev.request = h.id;
            ev.flags = h.checks;
            ev.extra = static_cast<uint32_t>(h.ops.size());
            ev.distance = h.distance;
            emit(ev);
        }
    }
}

// ------------------------------------------------------------------------------------------- settings ---------
std::string lower(std::string s) {
    for (char& c : s) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return s;
}
bool parse_raw_id(const std::string& s, uint32_t* out) {
    if (s.empty()) return false;
    char* end = nullptr;
    unsigned long long v = 0;
    if (s.size() > 2 && s[0] == '0' && (s[1] == 'x' || s[1] == 'X')) v = std::strtoull(s.c_str() + 2, &end, 16);
    else v = std::strtoull(s.c_str(), &end, 10);
    if (!end || *end || v > 0xffffffffull) return false;
    *out = static_cast<uint32_t>(v);
    return true;
}

// The allow list from the file's names + ids and the runtime list (g_mu held).
void rebuild_allow_locked() {
    g_allow.clear();
    for (uint32_t id : g_allow_runtime) g_allow.insert(id);
    for (const std::string& n : g_allow_names) {
        uint32_t id = 0;
        if (parse_raw_id(n, &id)) {
            g_allow.insert(id);
            continue;
        }
        KilnKnowledgeName kn{};
        if (catalog_ready() && catalog_resolve(lower(n), &kn) && kn.kind == KILN_KNOW_NAME_KNOWLEDGE) g_allow.insert(kn.id);
    }
}

void read_settings_file(bool log) {
    if (g_cfg_path.empty()) return;
    std::error_code ec;
    const auto t = std::filesystem::last_write_time(g_cfg_path, ec);
    if (ec) {
        if (log) KI("settings: %s not found; security checks off (the defaults)", g_cfg_path.string().c_str());
        return;
    }
    if (!log && t == g_cfg_time) return;
    g_cfg_time = t;
    try {
        std::ifstream f(g_cfg_path);
        const auto j = nlohmann::json::parse(f, nullptr, true, true);
        if (!j.is_object()) throw std::runtime_error("not an object");
        std::lock_guard lock(g_mu);
        g_set.veto_client = j.value("vetoClientKnowledge", false);
        g_set.check_dialogue = j.value("checkDialogue", false);
        g_set.log_client = j.value("logClientKnowledge", true);
        g_set.dialogue_range = static_cast<float>(j.value("dialogueRange", 12.0));
        g_allow_names.clear();
        if (j.contains("allowClientKnowledge") && j["allowClientKnowledge"].is_array())
            for (const auto& v : j["allowClientKnowledge"]) {
                if (v.is_string()) g_allow_names.push_back(v.get<std::string>());
                else if (v.is_number_unsigned()) g_allow_names.push_back(std::to_string(v.get<uint64_t>()));
            }
        rebuild_allow_locked();
        update_flags_locked();
        KI("settings: vetoClientKnowledge %s, checkDialogue %s (range %.1f m), %zu allowed ids, logClientKnowledge %s",
           g_set.veto_client ? "ON" : "off", g_set.check_dialogue ? "ON" : "off", g_set.dialogue_range, g_allow.size(),
           g_set.log_client ? "on" : "off");
    } catch (const std::exception& e) {
        KW("settings: %s is unreadable (%s); keeping the previous values", g_cfg_path.string().c_str(), e.what());
    }
}

std::string api_path(int kind) {
    char b[1024];
    size_t n = 0;
    if (g_api->path(kind, b, sizeof b, &n) != KILN_OK) return {};
    return std::string(b, n);
}

// ------------------------------------------------------------------------------------------- ticks ------------
void KILN_CALL on_pre(const KilnTickInfo* tick, void*) {
    g_step = tick->step;
    if (!g_know_ok) return;
    setup_world();
}

bool call_done(KilnKnowledgeDoneFn cb, const KilnKnowledgeResult* r, void* user) {
    __try {
        cb(r, user);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void deliver_done() {
    std::vector<Done> done;
    {
        std::lock_guard lock(g_mu);
        done.swap(g_done);
        for (const Done& d : done) {
            g_results.push_back(d.res);
            if (g_results.size() > 256) g_results.pop_front();
            if (d.res.status == KILN_OK) c_applied.fetch_add(1);
            else c_failed.fetch_add(1);
        }
    }
    for (const Done& d : done)
        if (d.cb && !call_done(d.cb, &d.res, d.user))
            KW("a knowledge result callback faulted (request %llu)", static_cast<unsigned long long>(d.res.request));
}

void KILN_CALL on_post(const KilnTickInfo* tick, void*) {
    g_step = tick->step;
    if (!g_know_ok || !g_world_ok || !g_w.reg) {
        deliver_done();
        return;
    }
    if (!catalog_ready() && (tick->step % 8) == 0) {
        catalog_build(g_w.npcreg, g_w.journal);
        std::lock_guard lock(g_mu);
        rebuild_allow_locked();
    }
    // pending requests that no apply took: they wait while the world loads (the game's knowledge update starts when
    // the world runs; a change before that would land in an empty map), then fail after 50 steps
    {
        const uint64_t wait = g_changes_ok && !g_applying ? kLoadWait : kApplyWait;
        std::lock_guard lock(g_mu);
        for (auto it = g_pending.begin(); it != g_pending.end();) {
            if (tick->step > it->submitted + wait) {
                Done d;
                d.res.size = sizeof d.res;
                d.res.op = it->r.op;
                d.res.request = it->id;
                d.res.target = it->r.target;
                d.res.id = it->r.id;
                d.res.status = KILN_ERR_UNAVAILABLE;
                res_reason(d.res, "not applied within %llu steps (the game's knowledge update did not run)",
                           static_cast<unsigned long long>(wait));
                d.cb = it->r.callback;
                d.user = it->r.user;
                g_done.push_back(d);
                it = g_pending.erase(it);
            } else {
                ++it;
            }
        }
    }
    deliver_done();
    // change events of this step (the offsets exist with quests.changes; the world map once it holds the save's)
    {
        std::lock_guard lock(g_mu);
        if (g_changes_ok) {
            const uintptr_t reg = g_w.reg;
            if (g_applying) scan_changes(reg + L.world_map, reg + L.world_changes, reg + L.world_version, 0, g_world_sh, true);
            const uint32_t nb = std::min<uint32_t>(block_count(reg), 64);
            for (uint32_t i = 0; i < nb; ++i) {
                const KilnEntity e = i + 1;
                const uintptr_t b = player_block(reg, e);
                Shadow& sh = g_block_sh[i];
                if (!b) {
                    if (sh.valid) sh = Shadow{};
                    continue;
                }
                // A joining player: the game activates the block (0x99f3d0: valid 1, version 0), which is the first baseline
                // (empty). The record comes later; scan_changes takes the game's mark of it as the baseline. Neither is a
                // change, and the shadow must hold the record or a later revoke would look like 0 -> 0.
                if (!sh.valid) {
                    fill_shadow(b, b + L.block_version, sh);
                    KI("player %u: knowledge map %zu set entries (the change events' baseline)", e, sh.v.size());
                    continue;
                }
                scan_changes(b, b + L.block_changes, b + L.block_version, e, sh, false);
            }
            g_sources.clear();
        }
        // dialogue events seen by the program this step
        for (const DlgRec& r : g_dlg) {
            KilnKnowledgeEvent ev{};
            ev.kind = KILN_KNOW_EVENT_DIALOGUE;
            ev.target = r.player;
            ev.npc = r.npc;
            ev.id = r.line;
            ev.old_value = r.kind;
            emit(ev);
            c_dlg.fetch_add(1);
        }
        g_dlg.clear();
    }
    announce_held();
    resolve_held(tick->step);
    if (tick->step % 100 == 0) read_settings_file(false);  // only re-reads when the file changed
}

void KILN_CALL on_world(const KilnWorldEvent* e, void*) {
    if (e->kind != KILN_WORLD_SERVER) return;
    if (e->event == KILN_WORLD_STOPPING) {
        KI("server world stopping: knowledge state dropped");
        world_reset();
    }
}

// ------------------------------------------------------------------------------------------- the table --------
bool tick_thread() { return g_api->on_tick_thread(); }

KilnResult KILN_CALL t_status(KilnKnowledgeStatus* out) {
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    KilnKnowledgeStatus s{};
    s.size = sizeof s;
    s.knowledge = g_know_ok;
    s.changes = g_changes_ok;
    s.journal = g_journal_ok;
    s.npc = g_npc_ok;
    s.dialogue = g_dialogue_ok && s_evq >= 0;
    s.client_input = g_input_ok;
    if (W && g_prog) {
        KilnProgramInfo pi{};
        pi.size = sizeof pi;
        s.program = W->program_info(g_prog, &pi) == KILN_OK && pi.state == KILN_PROGRAM_ACTIVE;
    }
    s.world = g_world_ok;
    s.applying = g_applying;
    s.world_objects = g_w.world_objects;
    s.player_objects = g_w.player_objects;
    const uintptr_t reg = g_reg_any.load();
    if (reg) {
        rd(reg + L.world_map + L.block_count, &s.world_entries);
        const uint32_t nb = block_count(reg);
        for (uint32_t i = 0; i < nb; ++i) s.players += player_block(reg, i + 1) != 0;
    }
    s.npcs = catalog_count(KILN_KNOW_CAT_NPCS);
    s.quests = catalog_count(KILN_KNOW_CAT_QUESTS);
    s.queries = catalog_count(KILN_KNOW_CAT_QUERIES);
    s.submitted = c_submitted;
    s.applied = c_applied;
    s.failed = c_failed;
    s.changes_seen = c_changes;
    s.client_ops = c_client;
    s.client_vetoed = c_vetoed;
    s.dialogue_events = c_dlg;
    s.dialogue_held = c_held;
    s.dialogue_dropped = c_dropped;
    s.last_seq = g_seq;
    s.queue_max = g_queue_max;
    {
        std::lock_guard lock(g_mu);
        s.pending = static_cast<uint32_t>(g_pending.size());
        std::snprintf(s.reason, sizeof s.reason, "%s", g_reason.c_str());
    }
    const uint32_t size = out->size ? std::min<uint32_t>(out->size, sizeof s) : sizeof s;
    std::memcpy(out, &s, size);
    out->size = size;
    return KILN_OK;
}

KilnResult KILN_CALL t_info(uint32_t id, KilnKnowledgeInfo* out) {
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_know_ok || !g_res_any) return KILN_ERR_UNAVAILABLE;
    return fill_info(id, out);
}

KilnResult KILN_CALL t_get(KilnEntity target, uint32_t id, KilnKnowledgeState* out) {
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    if (!tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!g_know_ok || !g_world_ok || !g_w.reg) return KILN_ERR_UNAVAILABLE;
    KilnKnowledgeState s{};
    s.size = sizeof s;
    s.target = target;
    s.id = id;
    s.step = g_step;
    s.type = target ? object_type(player_object(id)) : object_type(world_object(id));
    s.new_mask = 0xff;
    const uint32_t size = out->size ? std::min<uint32_t>(out->size, sizeof s) : sizeof s;
    if (s.type == KILN_KNOW_NONE) {
        std::memcpy(out, &s, size);
        out->size = size;
        return KILN_ERR_NOT_FOUND;
    }
    MapView m = map_of(g_w.reg, target);
    if (!m.base) return KILN_ERR_UNAVAILABLE;
    uint64_t st = 0;
    bool present = false;
    if (!map_state(m, id, &st, &present)) return KILN_ERR_INTERNAL;
    s.value = static_cast<uint32_t>(st);
    s.new_mask = static_cast<uint8_t>(st >> 32);
    s.present = present;
    std::memcpy(out, &s, size);
    out->size = size;
    return KILN_OK;
}

KilnResult KILN_CALL t_list(KilnEntity target, KilnKnowledgeEntry* out, uint32_t cap, uint32_t* count) {
    if (!count || (cap && !out)) return KILN_ERR_INVALID_ARGUMENT;
    if (!tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!g_know_ok || !g_world_ok || !g_w.reg) return KILN_ERR_UNAVAILABLE;
    MapView m = map_of(g_w.reg, target);
    if (!m.base) return KILN_ERR_UNAVAILABLE;
    uint32_t n = 0;
    if (!map_each(m.base, [&](uint32_t id, uint64_t st) {
            if (!static_cast<uint32_t>(st)) return;
            if (n < cap) out[n] = KilnKnowledgeEntry{id, static_cast<uint32_t>(st)};
            ++n;
        }))
        return KILN_ERR_INTERNAL;
    *count = n;
    return n > cap ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

KilnResult KILN_CALL t_submit(KilnOwner owner, const KilnKnowledgeRequest* req, uint64_t* out_request) {
    (void)owner;
    if (!req || req->size < sizeof(KilnKnowledgeRequest) || req->op < KILN_KNOW_OP_GRANT || req->op > KILN_KNOW_OP_SET ||
        (req->flags & ~static_cast<uint32_t>(KILN_KNOW_REQ_UNCHECKED)) != 0)
        return KILN_ERR_INVALID_ARGUMENT;
    if (!g_know_ok || !g_res_any) return KILN_ERR_UNAVAILABLE;
    if (req->op != KILN_KNOW_OP_GRANT && !g_changes_ok) return KILN_ERR_UNAVAILABLE;
    if (req->target > 1023) return KILN_ERR_INVALID_ARGUMENT;
    const uint8_t* o = req->target ? player_object(req->id) : world_object(req->id);
    const bool unchecked = (req->flags & KILN_KNOW_REQ_UNCHECKED) != 0 && req->op == KILN_KNOW_OP_GRANT;
    if (!o && !unchecked) return KILN_ERR_NOT_FOUND;
    const uint8_t type = o ? object_type(o) : static_cast<uint8_t>(KILN_KNOW_BOOL);
    if (o && type == KILN_KNOW_FLAGS && !(req->op == KILN_KNOW_OP_SET || (req->op == KILN_KNOW_OP_REVOKE && req->value == KILN_KNOW_ALL))) {
        if (req->value > 31 || !((object_flags(o) >> req->value) & 1)) return KILN_ERR_INVALID_ARGUMENT;
    }
    if (type == KILN_KNOW_COUNTER && req->op == KILN_KNOW_OP_GRANT && req->value == 0) return KILN_ERR_INVALID_ARGUMENT;
    if (req->target) {
        const uintptr_t reg = g_reg_any.load();
        if (!reg || !player_block(reg, req->target)) return KILN_ERR_UNAVAILABLE;
    }
    Req q;
    q.id = g_next_req.fetch_add(1);
    q.r = *req;
    q.submitted = g_step;
    q.type = type;
    {
        std::lock_guard lock(g_mu);
        if (g_pending.size() >= 4096) return KILN_ERR_BUFFER_TOO_SMALL;
        g_pending.push_back(q);
    }
    c_submitted.fetch_add(1);
    if (out_request) *out_request = q.id;
    return KILN_OK;
}

KilnResult KILN_CALL t_result(uint64_t request, KilnKnowledgeResult* out) {
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    for (auto it = g_results.rbegin(); it != g_results.rend(); ++it)
        if (it->request == request) {
            const uint32_t size = out->size ? std::min<uint32_t>(out->size, sizeof *it) : sizeof *it;
            std::memcpy(out, &*it, size);
            out->size = size;
            return KILN_OK;
        }
    return KILN_ERR_NOT_FOUND;
}

KilnResult KILN_CALL t_grant(KilnOwner owner, KilnEntity target, uint32_t id, uint32_t value, uint64_t* out) {
    KilnKnowledgeRequest r{sizeof r, KILN_KNOW_OP_GRANT, target, id, value, 0, nullptr, nullptr};
    return t_submit(owner, &r, out);
}
KilnResult KILN_CALL t_revoke(KilnOwner owner, KilnEntity target, uint32_t id, uint32_t flag, uint64_t* out) {
    KilnKnowledgeRequest r{sizeof r, KILN_KNOW_OP_REVOKE, target, id, flag, 0, nullptr, nullptr};
    return t_submit(owner, &r, out);
}

KilnResult KILN_CALL t_query(KilnEntity target, const KilnKnowledgeQuery* q, uint32_t mode, uint8_t* out) {
    if (!q || !out || mode > KILN_KNOW_MODE_PERSONAL) return KILN_ERR_INVALID_ARGUMENT;
    if (!tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!g_journal_ok || !g_world_ok || !g_w.g38) return KILN_ERR_UNAVAILABLE;
    if (q->id == 0) return KILN_ERR_INVALID_ARGUMENT;
    if (target && !player_block(g_w.reg, target)) return KILN_ERR_UNAVAILABLE;
    if (q->type == KILN_KNOW_Q_EXTERN) {
        const CatQuery* cq = catalog_query_by_id(q->id);
        if (!cq) return catalog_ready() ? KILN_ERR_NOT_FOUND : KILN_ERR_UNAVAILABLE;
        if (!target && cq->player) return KILN_ERR_INVALID_ARGUMENT;  // a player query needs a player
    } else if (!(target ? (player_object(q->id) != nullptr || (!q->player_only && world_object(q->id) != nullptr))
                         : world_object(q->id) != nullptr)) {
        return KILN_ERR_NOT_FOUND;
    }
    uint8_t r = 0;
    const int err = g_query_eval(reinterpret_cast<void*>(g_w.g38), q, target, target ? mode : 0u, &r);
    if (err == 0x100) {
        KW("the game's query evaluator faulted (query 0x%08x type %u, target %u)", q->id, q->type, target);
        return KILN_ERR_INTERNAL;
    }
    if (err == 0x1f) return KILN_ERR_NOT_FOUND;
    if (err) return KILN_ERR_UNAVAILABLE;
    *out = r ? 1 : 0;
    return KILN_OK;
}

KilnResult KILN_CALL t_resolve(KilnStr name, KilnKnowledgeName* out) {
    if (!out || !name.ptr) return KILN_ERR_INVALID_ARGUMENT;
    if (!tick_thread()) return KILN_ERR_WRONG_THREAD;
    std::string s(name.ptr, name.len);
    while (!s.empty() && std::isspace(static_cast<unsigned char>(s.back()))) s.pop_back();
    while (!s.empty() && std::isspace(static_cast<unsigned char>(s.front()))) s.erase(s.begin());
    KilnKnowledgeName n{};
    n.size = sizeof n;
    uint32_t id = 0;
    bool ok = false;
    if (parse_raw_id(s, &id)) {
        n.kind = KILN_KNOW_NAME_KNOWLEDGE;
        n.id = id;
        n.query = KilnKnowledgeQuery{id, 1, KILN_KNOW_CMP_EQUALS, KILN_KNOW_Q_BOOL, 0, 0};
        ok = true;
    } else {
        const std::string l = lower(s);
        if (l.starts_with("line:")) {
            if (catalog_resolve_line(s, &id)) {
                n.kind = KILN_KNOW_NAME_KNOWLEDGE;
                n.id = id;
                n.query = KilnKnowledgeQuery{id, 1, KILN_KNOW_CMP_EQUALS, KILN_KNOW_Q_BOOL, 0, 0};
                ok = true;
            }
        } else if (native_pack_resolve(l, &id)) {  // a mod's own object: "<mod>:<name>"
            n.kind = KILN_KNOW_NAME_KNOWLEDGE;
            n.id = id;
            n.query = KilnKnowledgeQuery{id, 1, KILN_KNOW_CMP_EQUALS, KILN_KNOW_Q_BOOL, 0, 0};
            ok = true;
        } else {
            ok = catalog_resolve(l, &n);
        }
    }
    if (!ok) return catalog_ready() ? KILN_ERR_NOT_FOUND : KILN_ERR_UNAVAILABLE;
    const uint32_t size = out->size ? std::min<uint32_t>(out->size, sizeof n) : sizeof n;
    std::memcpy(out, &n, size);
    out->size = size;
    return KILN_OK;
}

KilnResult copy_name(const std::string& s, char* buf, size_t cap, size_t* len) {
    if (len) *len = s.size();
    if (buf && cap) {
        const size_t n = std::min(s.size(), cap - 1);
        std::memcpy(buf, s.data(), n);
        buf[n] = 0;
    }
    return buf && cap && s.size() >= cap ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

KilnResult KILN_CALL t_name_of(uint32_t id, uint32_t kind, char* buf, size_t cap, size_t* len) {
    if (!tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!catalog_ready()) return KILN_ERR_UNAVAILABLE;
    const std::string s = catalog_name_of(id, kind);
    copy_name(s, buf, cap, len);
    return s.empty() ? KILN_ERR_NOT_FOUND : KILN_OK;
}

uint32_t KILN_CALL t_catalog_count(uint32_t cat) { return catalog_count(cat); }

KilnResult KILN_CALL t_catalog_at(uint32_t cat, uint32_t index, KilnKnowledgeName* out, char* buf, size_t cap, size_t* len) {
    if (!tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!catalog_ready()) return KILN_ERR_UNAVAILABLE;
    if (index >= catalog_count(cat)) return KILN_ERR_NOT_FOUND;
    const std::string name = catalog_entry_name(cat, index);
    if (out) {
        KilnKnowledgeName n{};
        n.size = sizeof n;
        catalog_resolve(lower(name), &n);
        const uint32_t size = out->size ? std::min<uint32_t>(out->size, sizeof n) : sizeof n;
        std::memcpy(out, &n, size);
        out->size = size;
    }
    return copy_name(name, buf, cap, len);
}

KilnResult KILN_CALL t_journal_quest(uint32_t index, KilnJournalQuest* out) {
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    if (!tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!catalog_ready()) return KILN_ERR_UNAVAILABLE;
    const CatQuest* q = catalog_quest(index);
    if (!q) return KILN_ERR_NOT_FOUND;
    const uint32_t size = out->size ? std::min<uint32_t>(out->size, sizeof q->quest) : sizeof q->quest;
    std::memcpy(out, &q->quest, size);
    out->size = size;
    return KILN_OK;
}

KilnResult KILN_CALL t_journal_step(uint32_t quest, uint32_t step, KilnJournalStep* out) {
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    if (!tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!catalog_ready()) return KILN_ERR_UNAVAILABLE;
    const CatQuest* q = catalog_quest(quest);
    if (!q || step >= q->steps.size()) return KILN_ERR_NOT_FOUND;
    const uint32_t size = out->size ? std::min<uint32_t>(out->size, sizeof(KilnJournalStep)) : sizeof(KilnJournalStep);
    std::memcpy(out, &q->steps[step], size);
    out->size = size;
    return KILN_OK;
}

KilnResult KILN_CALL t_npc(uint32_t index, KilnKnowledgeNpc* out, char* buf, size_t cap, size_t* len) {
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    if (!tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!catalog_ready()) return KILN_ERR_UNAVAILABLE;
    const CatNpc* n = catalog_npc(index);
    if (!n) return KILN_ERR_NOT_FOUND;
    KilnKnowledgeNpc v = n->npc;
    v.unlocked = 0xff;
    uint64_t mask = 0;
    uint16_t ver = 0;
    if (g_npc_ok && index < 64 && npc_unlock_mask(&mask, &ver)) v.unlocked = (mask >> index) & 1;
    const uint32_t size = out->size ? std::min<uint32_t>(out->size, sizeof v) : sizeof v;
    std::memcpy(out, &v, size);
    out->size = size;
    return copy_name(n->name, buf, cap, len);
}

KilnResult KILN_CALL t_events(uint64_t after, KilnKnowledgeEvent* out, uint32_t cap, uint32_t* count) {
    if (!count || (cap && !out)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    const uint64_t last = g_seq.load();
    uint64_t first = after + 1;
    if (last >= kRing && first <= last - kRing) first = last - kRing + 1;
    uint32_t n = 0;
    for (uint64_t s = first; s <= last && n < cap; ++s) out[n++] = g_ring[s % kRing];
    *count = n;
    return KILN_OK;
}

uint64_t KILN_CALL t_last_seq() { return g_seq.load(); }

KilnResult KILN_CALL t_held_ops(uint64_t held, KilnKnowledgeOp* out, uint32_t cap, uint32_t* count) {
    if (!count || (cap && !out)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    for (const Held& h : g_held)
        if (h.id == held) {
            const uint32_t n = static_cast<uint32_t>(h.ops.size());
            for (uint32_t i = 0; i < n && i < cap; ++i) out[i] = h.ops[i];
            *count = n;
            return n > cap ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
        }
    return KILN_ERR_NOT_FOUND;
}

KilnResult KILN_CALL t_decide(uint64_t held, int32_t decision) {
    if (decision < KILN_KNOW_DEFAULT || decision > KILN_KNOW_ALLOW) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    for (Held& h : g_held)
        if (h.id == held) {
            h.decision = decision;
            h.due = true;
            return KILN_OK;
        }
    return KILN_ERR_NOT_FOUND;
}

KilnResult KILN_CALL t_listen(uint32_t kinds) {
    if (kinds & ~(KILN_KNOW_LISTEN_CLIENT | KILN_KNOW_LISTEN_DIALOGUE)) return KILN_ERR_INVALID_ARGUMENT;
    g_listen = kinds;
    return KILN_OK;
}

KilnResult KILN_CALL t_settings(KilnKnowledgeSettings* out) {
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    KilnKnowledgeSettings s{};
    s.size = sizeof s;
    {
        std::lock_guard lock(g_mu);
        s.veto_client = g_set.veto_client;
        s.check_dialogue = g_set.check_dialogue;
        s.log_client = g_set.log_client;
        s.dialogue_range = g_set.dialogue_range;
        s.allow_count = static_cast<uint32_t>(g_allow.size());
    }
    const uint32_t size = out->size ? std::min<uint32_t>(out->size, sizeof s) : sizeof s;
    std::memcpy(out, &s, size);
    out->size = size;
    return KILN_OK;
}

KilnResult KILN_CALL t_configure(const KilnKnowledgeSettings* in) {
    if (!in || in->size < sizeof(KilnKnowledgeSettings) || !(in->dialogue_range >= 0.f && in->dialogue_range < 1e6f))
        return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    g_set.veto_client = in->veto_client != 0;
    g_set.check_dialogue = in->check_dialogue != 0;
    g_set.log_client = in->log_client != 0;
    g_set.dialogue_range = in->dialogue_range;
    update_flags_locked();
    KI("settings (configure): vetoClientKnowledge %s, checkDialogue %s (range %.1f m)", g_set.veto_client ? "ON" : "off",
       g_set.check_dialogue ? "ON" : "off", g_set.dialogue_range);
    return KILN_OK;
}

KilnResult KILN_CALL t_allow(const uint32_t* ids, uint32_t count) {
    if (count && !ids) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    g_allow_runtime.assign(ids, ids + count);
    rebuild_allow_locked();
    return KILN_OK;
}

KilnResult KILN_CALL t_native_manifest(char* buffer, size_t capacity, size_t* out_len) {
    const std::string m = native_pack_manifest();
    if (out_len) *out_len = m.size();
    if (!buffer || capacity <= m.size()) return KILN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(buffer, m.data(), m.size());
    buffer[m.size()] = 0;
    return KILN_OK;
}

// Lab only: a character's record arriving on a free block (kiln_knowledge.h). Offsets: 0x99f3d0 (activate), 0x9a1110 (load).
uint64_t g_lab_blocks = 0;  // blocks this function activated: it touches no other
KilnResult KILN_CALL t_lab_record(KilnEntity target, uint32_t phase, const uint32_t* ids, const uint32_t* values, uint32_t count) {
    if (!tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!g_changes_ok || L.block_dirty != L.block_valid + 2) return KILN_ERR_UNAVAILABLE;
    if (target == 0 || target > 64 || (count && (!ids || !values))) return KILN_ERR_INVALID_ARGUMENT;
    const uintptr_t reg = g_w.reg;
    const uintptr_t b = reg ? player_block(reg, target, false) : 0;
    if (!b) return KILN_ERR_NOT_FOUND;
    const uint64_t bit = 1ull << (target - 1);
    uint8_t valid = 0;
    if (!rd(b + L.block_valid, &valid)) return KILN_ERR_INTERNAL;
    switch (phase) {
    case 0: {
        if (valid) return KILN_ERR_INVALID_ARGUMENT;
        g_lab_blocks |= bit;
        const bool ok = wr(b + L.block_version, static_cast<uint16_t>(0)) && wr(b + L.block_valid + 1, static_cast<uint16_t>(0x100)) &&
                        wr(b + L.block_valid, static_cast<uint8_t>(1));
        return ok ? KILN_OK : KILN_ERR_INTERNAL;
    }
    case 1: {
        if (!(g_lab_blocks & bit) || !valid) return KILN_ERR_INVALID_ARGUMENT;
        for (uint32_t i = 0; i < count; ++i)
            if (!g_store(reinterpret_cast<void*>(b), ids[i], (0xffull << 32) | values[i])) return KILN_ERR_INTERNAL;
        return wr(b + L.block_valid + 1, static_cast<uint16_t>(0x101)) ? KILN_OK : KILN_ERR_INTERNAL;
    }
    case 2:
        if (!(g_lab_blocks & bit)) return KILN_ERR_INVALID_ARGUMENT;
        g_lab_blocks &= ~bit;
        return wr(b + L.block_valid, static_cast<uint8_t>(0)) ? KILN_OK : KILN_ERR_INTERNAL;
    default:
        return KILN_ERR_INVALID_ARGUMENT;
    }
}

const KilnKnowledgeApi kTable = {
    {sizeof(KilnKnowledgeApi), KILN_KNOWLEDGE_VERSION},
    &t_status, &t_info, &t_get, &t_list, &t_submit, &t_result, &t_grant, &t_revoke, &t_query, &t_resolve, &t_name_of,
    &t_catalog_count, &t_catalog_at, &t_journal_quest, &t_journal_step, &t_npc, &t_events, &t_last_seq, &t_held_ops,
    &t_decide, &t_listen, &t_settings, &t_configure, &t_allow, &t_native_manifest, &t_lab_record,
    &flame_status, &flame_set, &flame_result,
};

// ------------------------------------------------------------------------------------------- setup ------------
bool cst(const char* name, int64_t* out) { return g_api->constant(kstr(name), out) == KILN_OK; }
template <class T>
bool sym(const char* name, T* out) {
    uintptr_t a = 0;
    if (g_api->symbol(kstr(name), &a) != KILN_OK || !a) return false;
    *out = reinterpret_cast<T>(a);
    return true;
}
bool feat(const char* name) { return g_api->feature(kstr(name)); }

bool load_layout() {
    bool know = feat("quests.knowledge") && sym("players/KnowledgeIndex", &F.index) &&
                sym("players/KnowledgePlayerContext", &F.context) && sym("players/KnowledgeQueuePush", &F.push) &&
                sym("quests/StaticWorldFind", &F.world_find) && sym("quests/StaticPlayerFind", &F.player_find) &&
                cst("players/know.system", &L.system) && cst("players/know.single", &L.single) &&
                cst("quests/reg.data", &L.reg_data) && cst("quests/data.resource", &L.data_resource) &&
                cst("quests/data.player_map", &L.data_player_map) && cst("quests/static.type", &L.static_type) &&
                cst("quests/static.expected_flags", &L.static_flags) && cst("quests/reg.world_map", &L.world_map) &&
                cst("quests/reg.world_cap", &L.world_cap) && cst("quests/reg.world_values", &L.world_values) &&
                cst("quests/reg.world_bits", &L.world_bits) && cst("quests/reg.world_count", &L.world_count) &&
                cst("quests/reg.world_keys", &L.world_keys) && cst("quests/reg.world_version", &L.world_version) &&
                cst("quests/reg.blocks", &L.blocks) && cst("quests/block.valid", &L.block_valid) &&
                cst("quests/block.cap", &L.block_cap) && cst("quests/block.values", &L.block_values) &&
                cst("quests/block.bits", &L.block_bits) && cst("quests/block.count", &L.block_count) &&
                cst("quests/block.keys", &L.block_keys) && cst("quests/block.version", &L.block_version) &&
                cst("quests/block.stride", &L.stride) && cst("quests/queue.count", &L.queue_count) &&
                cst("quests/queue.capacity", &L.queue_cap);
    // the world map must have the player-block layout (KnowledgeIndex and the store are called on both)
    if (know && (L.world_cap - L.world_map != L.block_cap || L.world_values - L.world_map != L.block_values ||
                 L.world_bits - L.world_map != L.block_bits || L.world_keys - L.world_map != L.block_keys ||
                 L.world_count - L.world_map != L.block_count || L.stride < 0x60 || L.stride > 0x400)) {
        set_reason("the world map does not have the player-block layout on this build");
        know = false;
    }
    g_know_ok = know;
    if (!know) {
        set_reason("feature quests.knowledge is off on this build: kiln.knowledge unavailable");
        return false;
    }
    g_changes_ok = feat("quests.changes") && sym("quests/KnowledgeApplyQueue", &F.apply_queue) &&
                   sym("quests/KnowledgeApplyOp", &F.apply_op) && sym("quests/ChangeListAppend", &F.list_append) &&
                   sym("quests/BlockStore", &F.store) && cst("quests/reg.world_changes", &L.world_changes) &&
                   cst("quests/reg.world_dirty", &L.world_dirty) && cst("quests/block.changes", &L.block_changes) &&
                   cst("quests/block.dirty", &L.block_dirty) && cst("quests/list.count", &L.list_count) &&
                   cst("quests/list.entry", &L.list_entry) && L.list_entry == 16;
    if (!g_changes_ok) set_reason("feature quests.changes is off: no revoke/set, grants through the queue, no change events");
    g_journal_ok = feat("quests.journal") && sym("quests/QueryEval", &F.query_eval);
    g_npc_ok = feat("quests.npc");
    g_dialogue_ok = feat("quests.dialogue") && sym("quests/DialogLineFound", &F.dialog_site) &&
                    sym("quests/ProgramEntity", &F.program_entity) && cst("quests/npcdialog.resource", &L.npcdialog_resource);
    g_input_ok = feat("quests.client_input") && sym("quests/KnowledgeInputPush", &F.input_site) &&
                 cst("quests/input.knowledge", &L.input_knowledge);
    // the engine's events-in queue layout (captured by kiln.combat from trigger_knowledge_on_kill)
    if (!(cst("combat/evq.first", &L.evq_first) && cst("combat/evq.blocks", &L.evq_blocks) &&
          cst("combat/evq.count", &L.evq_count) && cst("combat/evq.next", &L.evq_next) &&
          cst("combat/evq.block_shift", &L.evq_shift) && L.evq_shift >= 8 && L.evq_shift <= 16)) {
        L.evq_count = -1;
        KW("the events-in queue layout (combat/evq.*) is not resolved: no dialogue events");
    }
    return true;
}

void install_hooks() {
    if (g_changes_ok) {
        auto h = safetyhook::InlineHook::create(reinterpret_cast<void*>(F.apply_queue), reinterpret_cast<void*>(&hk_apply_queue));
        if (!h) {
            set_reason("detour on KnowledgeApplyQueue failed: no revoke/set");
            g_changes_ok = false;
        } else {
            g_apply_hook = std::move(*h);
        }
    }
    if (g_input_ok) {
        auto m = safetyhook::MidHook::create(reinterpret_cast<void*>(F.input_site), &hk_input);
        if (!m) {
            set_reason("mid hook on the client knowledge input failed: no client veto");
            g_input_ok = false;
        } else {
            g_input_hook = std::move(*m);
        }
    }
    if (g_dialogue_ok) {
        auto m = safetyhook::MidHook::create(reinterpret_cast<void*>(F.dialog_site), &hk_dialog);
        if (!m) {
            set_reason("mid hook on the dialogue-line knowledge failed: no dialogue hold");
            g_dialogue_ok = false;
        } else {
            g_dialog_hook = std::move(*m);
        }
    }
}

void KILN_CALL on_ready(void*) {
    native_pack_ready();
    flame_ready();
    if (!g_know_ok) return;
    register_program();
}

}  // namespace
}  // namespace kiln::knowledge

using namespace kiln::knowledge;

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = KilnStr{KILN_KNOWLEDGE_SERVICE, sizeof(KILN_KNOWLEDGE_SERVICE) - 1};
    info->version = KilnStr{"1", 1};
    info->on_ready = &on_ready;
    if (!load_layout()) return KILN_ERR_UNAVAILABLE;
    install_hooks();
    native_pack_install();
    std::string dir = api_path(KILN_PATH_CONFIG);
    if (dir.empty()) {
        const std::string kiln = api_path(KILN_PATH_KILN);
        if (!kiln.empty()) dir = kiln + "\\config";
    }
    if (!dir.empty()) g_cfg_path = std::filesystem::path(std::u8string(dir.begin(), dir.end())) / "knowledge.json";
    read_settings_file(true);
    KilnHandle h = 0;
    api->subscribe_world(owner, &on_world, nullptr, &h);
    if (api->subscribe_tick(owner, KILN_TICK_PRE, &on_pre, nullptr, &h) != KILN_OK ||
        api->subscribe_tick(owner, KILN_TICK_POST, &on_post, nullptr, &h) != KILN_OK) {
        KW("no tick on this build: kiln.knowledge unavailable");
        return KILN_ERR_UNAVAILABLE;
    }
    KI("layout: knowledge on, in-step apply %s (revoke/set, ordered grants, change events), journal %s, NPC registry %s, "
       "dialogue %s, client input %s",
       g_changes_ok ? "on" : "OFF", g_journal_ok ? "on" : "OFF", g_npc_ok ? "on" : "OFF", g_dialogue_ok ? "on" : "OFF",
       g_input_ok ? "on" : "OFF");
    return api->register_service(owner, KilnStr{KILN_KNOWLEDGE_SERVICE, sizeof(KILN_KNOWLEDGE_SERVICE) - 1}, &kTable);
}
