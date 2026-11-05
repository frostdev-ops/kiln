// SPDX-License-Identifier: MIT
// kiln.progression: level, experience, skill tree and skill points of the players in game (kiln_progression.h;
// evidence docs/re/progression.md, symbols sigdb/symbols/server.d/progression.json).
//
//  - Reads (Pre, tick thread): the player's Level / Experience attributes through the game's attribute lookup, the
//    skill points through the game's own sums (total = SkillPointsTotal, spent = SkillPointsSpent over masks rebuilt
//    with the game's SkillUiRebuild from UnlockedSkillNodes), the skill-point knowledge through the game's knowledge
//    blocks. A snapshot of every player in game is taken every 8 steps; the diff gives the level / skills events.
//  - Writes: queued requests are applied by the program kiln_players_progression (kiln.world, write Experience, Level,
//    UnlockedSkillNodes; with PlayerState; host only) for its current entity, after MarkWritten, with the game's
//    functions: attribute formulas re-run like player_level does, nodes set with the game's SkillNodeSet, the tree
//    re-derived by player_skilltree (UnlockedSkillNodes.changeCount), knowledge pushed into KnowledgeTriggerQueue.
//  - Results are completed in Pre two steps after the change (the game's player_level / player_skilltree ran by then).
//  - Every call into the game is SEH-guarded; any fault turns that part off (logged), the server runs on.
#include <windows.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <string_view>
#include <vector>

#include "kiln_api.h"
#include "kiln_progression.h"
#include "../skills/kiln_skills.h"
#include "progression.h"
#include "services/kiln_world.h"

namespace kiln::progression {
namespace {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
const KilnWorldApi* W = nullptr;

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
#define PI(...) logf(KILN_LOG_INFO, "progression: " __VA_ARGS__)
#define PW(...) logf(KILN_LOG_WARN, "progression: " __VA_ARGS__)

KilnStr kstr(const char* s) { return KilnStr{s, std::strlen(s)}; }

constexpr uint32_t fnv1a(std::string_view s, uint32_t h = 0x811c9dc5u) {
    for (unsigned char c : s) h = (h ^ c) * 0x1000193u;
    return h;
}
constexpr uint32_t ecs_hash(std::string_view name) { return fnv1a(name, fnv1a("keen::ecs::")); }

// ------------------------------------------------------------------------------------------------ the game ------
using AttrFindFn = void* (*)(void* out16, uint32_t id, void* registry);
using AttrRecomputeFn = void (*)(void* component, void* registry);
using XpForLevelFn = uint32_t (*)(uint32_t level, void* balancing);
using LevelCapFn = uint32_t (*)(void* balancing);
using PerLevelFn = uint32_t (*)(void* balancing);
using NodeByIdFn = uint8_t* (*)(uint32_t id, void* tree);
using ReachableFn = bool (*)(uint32_t id, const void* ui_masks, void* tree);
using NodeSetFn = bool (*)(uint32_t id, void* unlocked, bool unlock, void* tree);
using UiRebuildFn = bool (*)(void* ui, const void* unlocked, void* tree);
using ConnectedFn = bool (*)(const void* ui, void* tree);
using PointsTotalFn = void* (*)(void* out12, void* knowledge, void* balancing, uint32_t level, uint32_t entity);
using PointsSpentFn = int32_t (*)(const void* ui_masks, const void* ui_levels, void* tree);
using KnowledgeIndexFn = uint64_t (*)(const void* block, const uint32_t* id);
using PlayerContextFn = uint32_t (*)(uint32_t entity);
using QueuePushFn = bool (*)(void* queue, const void* op16);
using EmitFn = void* (*)(void* buffer, uint32_t hash, uint32_t size, uint16_t program);
using JobProgramIdFn = uint16_t (*)(void* job);
using JobBufferFn = void* (*)(void* job);
using ProgramTimeFn = int64_t* (*)(void* ctx, int64_t* out);

struct Fns {
    AttrFindFn attr_find = nullptr;
    AttrRecomputeFn recompute = nullptr, recompute_need = nullptr;
    XpForLevelFn xp_for_level = nullptr;
    LevelCapFn level_cap = nullptr;
    PerLevelFn per_level = nullptr;
    NodeByIdFn node_by_id = nullptr;
    ReachableFn reachable = nullptr;
    NodeSetFn node_set = nullptr;
    UiRebuildFn ui_rebuild = nullptr;
    ConnectedFn connected = nullptr;
    PointsTotalFn points_total = nullptr;
    PointsSpentFn points_spent = nullptr;
    KnowledgeIndexFn know_index = nullptr;
    PlayerContextFn know_ctx = nullptr;
    QueuePushFn know_push = nullptr;
    EmitFn emit = nullptr;
    JobProgramIdFn job_program = nullptr;
    JobBufferFn job_buffer = nullptr;
    ProgramTimeFn program_time = nullptr;
} F;

struct Layout {
    uint32_t attr_level = 0, attr_xp = 0, attr_pending = 0, attr_need = 0;
    uint32_t ev_xp = 0, ev_xp_size = 0;
    int64_t know_system = -1, know_single = -1, know_blocks = -1, know_valid = -1, know_data = -1, know_res = -1,
            know_points = -1, know_cap = -1, know_values = -1, know_stride = 0;
} L;

// Component layouts (reflection, docs/re/progression.md).
constexpr uint32_t kExperienceSize = 20, kLevelSize = 16, kUnlockedSize = 2316, kUiSize = 136;
constexpr size_t kU_Known = 0x4, kU_Levels = 0x804, kU_Version = 0x904, kU_Change = 0x908, kU_Count = 256;
constexpr size_t kUi_Levels = 0x20, kUi_Change = 0x84;
constexpr size_t kNodeSize = 0x8c, kN_UiPos = 0x4, kN_ActiveSkill = 0x28, kN_In = 0x78, kN_Out = 0x80, kN_Cost = 0x84,
                 kN_Type = 0x86, kN_Class = 0x87, kN_MaxLevel = 0x88, kN_LevelIndex = 0x89;
constexpr size_t kTreeResource = 0x20;  // g38_skilltree::SkillTree -> SkillTreeResource data (the game's sums read it)

const uint32_t H_EXPERIENCE = ecs_hash("Experience"), H_LEVEL = ecs_hash("Level"),
               H_UNLOCKED = ecs_hash("UnlockedSkillNodes"), H_UI = ecs_hash("PlayerSkillsUi");

// What works (set in main / at the first world step; read anywhere).
std::atomic<bool> g_level_ok{false}, g_skills_ok{false}, g_know_ok{false}, g_events_ok{false};
std::atomic<bool> g_program_ok{false}, g_world_ok{false};
std::string g_reason;  // guarded by g_mu
std::mutex g_mu;

// Singletons of the current server world (tick thread; the program reads them through its own slots).
struct World {
    uintptr_t world = 0;
    void* attr_reg = nullptr;
    void* balancing = nullptr;
    void* tree = nullptr;
    void* knowledge = nullptr;
    void* queue = nullptr;
    bool looked = false;
} g_w;

// The static tree and the skill-point knowledge, read once per world.
std::vector<KilnSkillNode> g_tree;  // guarded by g_mu
struct KnowEntry {
    uint32_t id;
    uint8_t amount;
};
std::vector<KnowEntry> g_know;  // tick thread + program (read only after setup)
std::atomic<uint32_t> g_level_cap{0}, g_per_level{0}, g_know_plus1{0};

void set_reason(const char* fmt, ...) {
    char buf[160];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    std::lock_guard lock(g_mu);
    if (!g_reason.empty()) g_reason += "; ";
    g_reason += buf;
    if (g_reason.size() > 150) g_reason.resize(150);
}

// ------------------------------------------------------------------------------------------------ guarded calls --
// No C++ objects with destructors in these functions (__try).
bool g_memcpy(void* dst, const void* src, size_t n) {
    if (!src) return false;
    __try {
        std::memcpy(dst, src, n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// The u32 storage word of attribute `id` in an attribute component, or nullptr.
uint32_t* attr_slot(void* comp, uint32_t id, void* reg) {
    if (!comp || !reg || !F.attr_find) return nullptr;
    __try {
        alignas(8) uint8_t out[16] = {};
        F.attr_find(out, id, reg);
        if (out[0] != 0) return nullptr;
        const uint8_t* info = *reinterpret_cast<uint8_t* const*>(out + 8);
        auto* c = static_cast<uint8_t*>(comp);
        if (!info || *reinterpret_cast<const uint32_t*>(info) != *reinterpret_cast<const uint32_t*>(c)) return nullptr;
        if (!(c[6] & 1)) return nullptr;  // not initialised
        const uint16_t slot = *reinterpret_cast<const uint16_t*>(info + 0xc);
        if (slot >= c[7]) return nullptr;  // storageCount
        return reinterpret_cast<uint32_t*>(c + *reinterpret_cast<const uint16_t*>(c + 4) + slot * 4u);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
}

bool g_recompute(AttrRecomputeFn fn, void* comp, void* reg) {
    if (!fn) return false;
    __try {
        fn(comp, reg);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool g_xp_for_level(uint32_t level, void* bal, uint32_t* out) {
    __try {
        *out = F.xp_for_level(level, bal);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool g_u32_of(LevelCapFn fn, void* bal, uint32_t* out) {
    __try {
        *out = fn(bal);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

uint8_t* g_node(uint32_t id, void* tree) {
    if (!F.node_by_id || !tree) return nullptr;
    __try {
        return F.node_by_id(id, tree);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
}

// result: 0 false, 1 true, -1 fault
int g_reachable(uint32_t id, const void* ui, void* tree) {
    __try {
        return F.reachable(id, ui, tree) ? 1 : 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
}
int g_node_set(uint32_t id, void* unlocked, bool unlock, void* tree) {
    __try {
        return F.node_set(id, unlocked, unlock, tree) ? 1 : 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
}
int g_ui_rebuild(void* ui, const void* unlocked, void* tree) {
    __try {
        return F.ui_rebuild(ui, unlocked, tree) ? 1 : 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
}
int g_connected(const void* ui, void* tree) {
    __try {
        return F.connected(ui, tree) ? 1 : 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
}
// -1 fault, -2 the game answered an error
int64_t g_points_total(void* know, void* bal, uint32_t level, uint32_t entity) {
    __try {
        alignas(8) uint8_t out[16] = {};
        F.points_total(out, know, bal, level, entity);
        if (out[0] != 0) return -2;
        return *reinterpret_cast<const uint32_t*>(out + 8);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
}
int64_t g_points_spent(const void* ui, void* tree) {
    __try {
        return F.points_spent(ui, static_cast<const uint8_t*>(ui) + kUi_Levels, tree);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
}
// 1 owned, 0 not owned, -1 fault / no block
int g_know_owned(const uint8_t* block, uint32_t id) {
    __try {
        const uint64_t i = F.know_index(block, &id);
        const uint64_t cap = *reinterpret_cast<const uint64_t*>(block + L.know_cap);
        if (i >= cap) return 0;
        const uint64_t* values = *reinterpret_cast<uint64_t* const*>(block + L.know_values);
        return static_cast<uint32_t>(values[i]) != 0 ? 1 : 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
}
bool g_know_push(void* queue, uint32_t id, uint32_t entity) {
    __try {
        alignas(8) uint8_t op[16] = {};
        std::memcpy(op, &id, 4);  // KnowledgeUnlockOperation {id, type Bool (0), value 1} + the player's context
        const uint32_t value = 1;
        std::memcpy(op + 8, &value, 4);
        const uint32_t ctx = F.know_ctx(entity);
        std::memcpy(op + 12, &ctx, 4);
        return F.know_push(queue, op);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
void* g_emit(void* job, uint32_t hash, uint32_t size) {
    __try {
        void* buf = F.job_buffer(job);
        const uint16_t pid = F.job_program(job);
        return F.emit(buf, hash, size, pid);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
}
int64_t g_time(void* ctx) {
    if (!F.program_time) return 0;
    __try {
        int64_t t = 0;
        const int64_t* r = F.program_time(ctx, &t);
        return r ? *r : t;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

// ------------------------------------------------------------------------------------------------ setup ---------
bool constant(const char* name, int64_t* out) { return g_api->constant(kstr(name), out) == KILN_OK; }
template <class T>
bool sym(const char* name, T* out) {
    uintptr_t a = 0;
    if (g_api->symbol(kstr(name), &a) != KILN_OK || !a) return false;
    *out = reinterpret_cast<T>(a);
    return true;
}

// The player's knowledge block (the layout of the game's KnowledgePoints sum), or nullptr.
const uint8_t* know_block(void* knowledge, KilnEntity entity) {
    if (!g_know_ok || !knowledge || entity == 0 || entity > 64) return nullptr;
    uintptr_t sys = 0, blocks = 0;
    uint8_t single = 0, valid = 0;
    if (!g_memcpy(&sys, static_cast<uint8_t*>(knowledge) + L.know_system, 8) || !sys) return nullptr;
    if (!g_memcpy(&single, reinterpret_cast<void*>(sys + L.know_single), 1) ||
        !g_memcpy(&blocks, reinterpret_cast<void*>(sys + L.know_blocks), 8) || !blocks)
        return nullptr;
    const uintptr_t block = blocks + (single ? 0u : entity - 1u) * static_cast<uintptr_t>(L.know_stride);
    if (!g_memcpy(&valid, reinterpret_cast<void*>(block + L.know_valid), 1) || !valid) return nullptr;
    return reinterpret_cast<const uint8_t*>(block);
}

// Reads the static tree and the skill-point knowledge of the world; checks them against the game's own lookups.
void setup_world() {
    const uintptr_t world = g_api->server_world();
    if (!world) return;
    if (g_w.looked && g_w.world == world) return;
    g_w = World{};
    g_w.world = world;
    g_w.looked = true;
    g_w.attr_reg = reinterpret_cast<void*>(g_api->singleton(world, kstr("impact::AttributeRegistry")));
    g_w.balancing = reinterpret_cast<void*>(g_api->singleton(world, kstr("BalancingRegistry")));
    g_w.tree = reinterpret_cast<void*>(g_api->singleton(world, kstr("g38_skilltree::SkillTree")));
    g_w.knowledge = reinterpret_cast<void*>(g_api->singleton(world, kstr("G38Knowledge")));
    g_w.queue = reinterpret_cast<void*>(g_api->singleton(world, kstr("KnowledgeTriggerQueue")));
    g_world_ok = g_w.attr_reg && g_w.balancing;
    if (!g_world_ok) {
        set_reason("singletons AttributeRegistry/BalancingRegistry not found");
        g_level_ok = g_skills_ok = g_know_ok = false;
        PW("the server world has no AttributeRegistry/BalancingRegistry: progression is off");
        return;
    }
    uint32_t cap = 0, per = 0, need1 = 0;
    if (g_level_ok && (!g_u32_of(F.level_cap, g_w.balancing, &cap) || cap == 0 || cap > 1000 ||
                       !g_xp_for_level(1, g_w.balancing, &need1) || need1 == 0)) {
        set_reason("the level cap / experience table did not read (cap %u, need(1) %u)", cap, need1);
        g_level_ok = false;
    }
    g_level_cap = cap;
    // The tree: every node of the resource must be the node the game's lookup returns for its id.
    std::vector<KilnSkillNode> tree;
    if (g_skills_ok && !g_w.tree) {
        set_reason("singleton g38_skilltree::SkillTree not found");
        g_skills_ok = false;
    }
    if (g_skills_ok) {
        uintptr_t res = 0;
        int32_t rel = 0;
        uint32_t count = 0;
        bool ok = g_memcpy(&res, static_cast<uint8_t*>(g_w.tree) + kTreeResource, 8) && res &&
                  g_memcpy(&rel, reinterpret_cast<void*>(res), 4) && g_memcpy(&count, reinterpret_cast<void*>(res + 4), 4) &&
                  rel != 0 && count > 0 && count <= 4096;
        uint32_t mismatch = 0;
        if (ok) {
            const uintptr_t base = res + static_cast<intptr_t>(rel);
            for (uint32_t i = 0; i < count && ok; ++i) {
                uint8_t n[kNodeSize];
                if (!g_memcpy(n, reinterpret_cast<void*>(base + i * kNodeSize), kNodeSize)) {
                    ok = false;
                    break;
                }
                KilnSkillNode s{};
                std::memcpy(&s.id, n, 4);
                std::memcpy(&s.ui_x, n + kN_UiPos, 4);
                std::memcpy(&s.ui_y, n + kN_UiPos + 4, 4);
                std::memcpy(&s.active_skill_unlock, n + kN_ActiveSkill, 4);
                std::memcpy(&s.cost, n + kN_Cost, 2);
                s.type = n[kN_Type];
                s.class_alignment = n[kN_Class];
                s.max_level = n[kN_MaxLevel];
                s.level_index = n[kN_LevelIndex];
                uint32_t in = 0, out = 0;
                std::memcpy(&in, n + kN_In + 4, 4);
                std::memcpy(&out, n + kN_Out + 4, 4);
                s.incoming = static_cast<uint8_t>(std::min<uint32_t>(in, 255));
                s.outgoing = static_cast<uint8_t>(std::min<uint32_t>(out, 255));
                s.index = i;
                if (reinterpret_cast<uintptr_t>(g_node(s.id, g_w.tree)) != base + i * kNodeSize) ++mismatch;
                tree.push_back(s);
            }
        }
        if (!ok || mismatch) {
            set_reason("the skill tree did not match the game's node lookup (%u of %zu)", mismatch, tree.size());
            g_skills_ok = false;
            tree.clear();
        }
        if (g_skills_ok && (!g_u32_of(F.per_level, g_w.balancing, &per) || per > 100)) {
            set_reason("skill points per level did not read");
            g_skills_ok = false;
        }
    }
    g_per_level = per;
    // Skill-point knowledge (GameKnowledgeResource.skillPoints) through the knowledge system the game's sum reads.
    std::vector<KnowEntry> know;
    if (g_know_ok && (!g_w.knowledge || !g_w.queue)) {
        set_reason("singletons G38Knowledge/KnowledgeTriggerQueue not found");
        g_know_ok = false;
    }
    if (g_know_ok) {
        uintptr_t sys = 0, data = 0, res = 0;
        int32_t rel = 0;
        uint32_t count = 0;
        bool ok = g_memcpy(&sys, static_cast<uint8_t*>(g_w.knowledge) + L.know_system, 8) && sys &&
                  g_memcpy(&data, reinterpret_cast<void*>(sys + L.know_data), 8) && data &&
                  g_memcpy(&res, reinterpret_cast<void*>(data + L.know_res), 8) && res &&
                  g_memcpy(&rel, reinterpret_cast<void*>(res + L.know_points), 4) &&
                  g_memcpy(&count, reinterpret_cast<void*>(res + L.know_points + 4), 4) && count <= 4096;
        if (ok && rel && count) {
            const uintptr_t base = res + L.know_points + static_cast<intptr_t>(rel);
            for (uint32_t i = 0; i < count && ok; ++i) {
                uint8_t e[8];
                ok = g_memcpy(e, reinterpret_cast<void*>(base + i * 8u), 8);
                KnowEntry k{};
                std::memcpy(&k.id, e, 4);
                k.amount = e[4];
                if (ok && k.id) know.push_back(k);
            }
        }
        if (!ok || know.empty()) {
            set_reason("the skill-point knowledge list did not read");
            g_know_ok = false;
            know.clear();
        }
    }
    uint32_t plus1 = 0, sum = 0;
    for (const KnowEntry& k : know) {
        plus1 += k.amount == 1;
        sum += k.amount;
    }
    g_know_plus1 = plus1;
    {
        std::lock_guard lock(g_mu);
        g_tree = std::move(tree);
    }
    g_know = std::move(know);
    uint32_t need[6] = {};
    for (uint32_t i = 1; i <= 5 && g_level_ok; ++i) g_xp_for_level(i, g_w.balancing, &need[i]);
    uint32_t need_cap = 0;
    if (g_level_ok) g_xp_for_level(cap, g_w.balancing, &need_cap);
    PI("world %p: level %s (cap %u; experience per level 1..5: %u %u %u %u %u, at the cap %u), skills %s (%zu nodes, "
       "%u points per level), skill-point knowledge %s (%zu entries, %u of them +1, %u points in all)",
       reinterpret_cast<void*>(world), g_level_ok ? "on" : "OFF", cap, need[1], need[2], need[3], need[4], need[5],
       need_cap, g_skills_ok ? "on" : "OFF", g_tree.size(), per, g_know_ok ? "on" : "OFF", g_know.size(), plus1, sum);
}

// ------------------------------------------------------------------------------------------------ reading -------
// Lab: one player-template entity counts as a player in game (lab_player), so tests can reach apply_skill without a client.
std::atomic<KilnEntity> g_lab_player{0};
bool playing(KilnEntity e) { return kiln::players::in_game(e) || (e != 0 && e == g_lab_player.load(std::memory_order_relaxed)); }

// kiln.skills' shadow table (nodes past the game's caps; loaded after this DLL, so looked up on first use). Their levels
// are not in UnlockedSkillNodes, and the sums here run on scratch masks the shadow hooks can't attribute to a player, so
// without this the player's points ignore them, /skill lock can't see them and a respec leaves them.
const KilnSkillsApi* shadow_api() {
    static const KilnSkillsApi* s = nullptr;
    if (s) return s;
    const auto* t = static_cast<const KilnSkillsApi*>(g_api->service(kstr(KILN_SKILLS_SERVICE), 1));
    if (!t || t->header.size < sizeof(KilnSkillsApi) || !t->shadow_on || !t->shadow_on()) return nullptr;
    s = t;
    return s;
}

// While alive, the game's skill functions on this thread count this player's shadow nodes on any masks.
class ShadowScope {
public:
    explicit ShadowScope(KilnEntity e) : s_(shadow_api()) {
        if (s_) s_->shadow_scope(KILN_WORLD_SERVER, e);
    }
    ~ShadowScope() {
        if (s_) s_->shadow_scope(0, 0);
    }
    ShadowScope(const ShadowScope&) = delete;
    ShadowScope& operator=(const ShadowScope&) = delete;

private:
    const KilnSkillsApi* s_;
};

uint32_t shadow_taken(KilnEntity e) {
    const KilnSkillsApi* s = shadow_api();
    uint32_t n = 0;
    if (s) s->shadow_get(KILN_WORLD_SERVER, e, nullptr, 0, &n);
    return n;
}

struct Comps {
    void* exp = nullptr;
    void* level = nullptr;
    void* unlocked = nullptr;
    void* ui = nullptr;  // the game's PlayerSkillsUi (only for the record; sums use masks rebuilt from unlocked)
};

bool comps_of(KilnEntity e, Comps* c) {
    if (!W) return false;
    uint32_t n = 0;
    c->exp = const_cast<void*>(W->component(e, H_EXPERIENCE, &n));
    if (c->exp && n != kExperienceSize) c->exp = nullptr;
    c->level = const_cast<void*>(W->component(e, H_LEVEL, &n));
    if (c->level && n != kLevelSize) c->level = nullptr;
    c->unlocked = const_cast<void*>(W->component(e, H_UNLOCKED, &n));
    if (c->unlocked && n != kUnlockedSize) c->unlocked = nullptr;
    c->ui = const_cast<void*>(W->component(e, H_UI, &n));
    if (c->ui && n != kUiSize) c->ui = nullptr;
    return c->exp && c->level;
}

uint32_t read_u32(const uint32_t* p) {
    uint32_t v = 0;
    g_memcpy(&v, p, 4);
    return v;
}

// One read with explicit singletons (the program passes its own slots; Pre passes g_w).
struct Ctx {
    void* reg;
    void* bal;
    void* tree;
    void* know;
};

KilnProgression read(KilnEntity e, const Comps& c, const Ctx& x, uint64_t step) {
    KilnProgression p{};
    p.size = sizeof p;
    p.entity = e;
    p.step = step;
    p.level_cap = g_level_cap;
    p.points_per_level = g_per_level;
    if (g_level_ok && c.level && c.exp) {
        uint32_t* lv = attr_slot(c.level, L.attr_level, x.reg);
        uint32_t* xp = attr_slot(c.exp, L.attr_xp, x.reg);
        uint32_t* pend = attr_slot(c.exp, L.attr_pending, x.reg);
        uint32_t* need = attr_slot(c.exp, L.attr_need, x.reg);
        if (lv && xp && pend && need) {
            p.level = read_u32(lv);
            p.experience = read_u32(xp);
            p.experience_pending = read_u32(pend);
            p.experience_needed = read_u32(need);
            p.flags |= KILN_PROG_HAS_LEVEL;
            if (p.level_cap && p.level >= p.level_cap) p.flags |= KILN_PROG_AT_CAP;
        }
    }
    if (g_skills_ok && c.unlocked && x.tree) {
        ShadowScope scope(e);
        alignas(8) uint8_t ui[kUiSize] = {};
        if (g_ui_rebuild(ui, c.unlocked, x.tree) == 1) {
            const int64_t spent = g_points_spent(ui, x.tree);
            if (spent >= 0) {
                p.points_spent = static_cast<uint32_t>(spent);
                uint32_t nodes = shadow_taken(e);
                for (int i = 0; i < 4; ++i) {
                    uint64_t m;
                    std::memcpy(&m, ui + i * 8, 8);
                    nodes += static_cast<uint32_t>(__builtin_popcountll(m));
                }
                p.nodes = nodes;
                p.flags |= KILN_PROG_HAS_SKILLS;
            }
        }
        if ((p.flags & KILN_PROG_HAS_LEVEL) && x.know && p.level >= 1) {
            const int64_t total = g_points_total(x.know, x.bal, p.level, e);
            if (total >= 0) {
                p.points_total = static_cast<uint32_t>(total);
                p.points_from_level = g_per_level * (p.level - 1);
                p.points_from_knowledge = p.points_total >= p.points_from_level ? p.points_total - p.points_from_level : 0;
                p.flags |= KILN_PROG_HAS_POINTS;
                if (p.flags & KILN_PROG_HAS_SKILLS) {
                    p.points_available = static_cast<int32_t>(p.points_total) - static_cast<int32_t>(p.points_spent);
                    if (p.points_available < 0) p.flags |= KILN_PROG_OVER_BUDGET;
                }
            }
        }
    }
    if (g_know_ok) {
        if (const uint8_t* block = know_block(x.know, e)) {
            uint32_t givable = 0;
            bool fault = false;
            for (const KnowEntry& k : g_know) {
                if (k.amount != 1) continue;
                const int o = g_know_owned(block, k.id);
                if (o < 0) {
                    fault = true;
                    break;
                }
                givable += o == 0;
            }
            if (!fault) {
                p.points_givable = givable;
                p.flags |= KILN_PROG_HAS_KNOWLEDGE;
            }
        }
    }
    return p;
}

// ------------------------------------------------------------------------------------------------ requests ------
struct Pending {
    uint64_t id = 0;
    KilnOwner owner = 0;
    KilnProgressionRequest req{};
    uint64_t submitted_step = 0, applied_step = 0;
    KilnProgressionResult res{};
};

std::deque<Pending> g_queue;   // not applied yet (g_mu)
std::deque<Pending> g_applied; // applied by the program, completed in Pre (g_mu)
std::array<KilnProgressionResult, 64> g_results{};  // ring by request id (g_mu)
uint64_t g_next_id = 1;
std::atomic<uint64_t> g_step{0};
std::atomic<uint64_t> g_submitted{0}, g_applied_n{0}, g_failed{0}, g_updates{0};

void set_res_reason(KilnProgressionResult& r, const char* fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(r.reason, sizeof r.reason, fmt, ap);
    va_end(ap);
}

// ------------------------------------------------------------------------------------------------ events --------
std::array<KilnProgressionEvent, 64> g_ring{};
uint64_t g_seq = 0;  // g_mu
std::atomic<uint64_t> g_seq_pub{0};
struct SlotSeen {
    bool valid = false;
    uint32_t level = 0, spent = 0;
    bool skills = false;
};
std::array<SlotSeen, 65> g_seen{};  // by entity (1..64), tick thread
std::array<KilnProgression, 65> g_snap{};  // by entity, g_mu

void push_event(uint32_t kind, KilnEntity e, uint32_t a, uint32_t b, uint64_t request) {
    std::lock_guard lock(g_mu);
    KilnProgressionEvent ev{};
    ev.seq = ++g_seq;
    ev.kind = kind;
    ev.entity = e;
    ev.step = g_step.load();
    ev.old_value = a;
    ev.new_value = b;
    ev.request = request;
    g_ring[ev.seq % g_ring.size()] = ev;
    g_seq_pub.store(g_seq);
}

// Diff against what was seen last for this player; records the snapshot.
void observe(const KilnProgression& p, uint64_t request) {
    if (p.entity == 0 || p.entity > 64) return;
    SlotSeen& s = g_seen[p.entity];
    const bool has_level = p.flags & KILN_PROG_HAS_LEVEL, has_skills = p.flags & KILN_PROG_HAS_SKILLS;
    if (s.valid && has_level && s.level != p.level) push_event(KILN_PROG_EVENT_LEVEL, p.entity, s.level, p.level, request);
    if (s.valid && has_skills && s.skills && s.spent != p.points_spent)
        push_event(KILN_PROG_EVENT_SKILLS, p.entity, s.spent, p.points_spent, request);
    if (has_level) {
        s.level = p.level;
        s.valid = true;
    }
    if (has_skills) {
        s.spent = p.points_spent;
        s.skills = true;
    }
    std::lock_guard lock(g_mu);
    g_snap[p.entity] = p;
}

// ------------------------------------------------------------------------------------------------ the program ---
KilnHandle g_prog = 0;
int32_t s_exp = -1, s_level = -1, s_unlocked = -1, s_reg = -1, s_bal = -1, s_tree = -1, s_know = -1, s_queue = -1,
        s_ev = -1;
std::atomic<int> g_prog_logs{0};

void mark(KilnProgramCtx* ctx, const uint64_t* iter, int32_t slot) {
    if (slot >= 0 && iter[slot] && iter[slot + 5]) W->mark_written(ctx, &iter[slot]);
}

bool set_attr(void* comp, uint32_t id, void* reg, uint32_t value) {
    uint32_t* p = attr_slot(comp, id, reg);
    if (!p) return false;
    return g_memcpy(p, &value, 4);
}

void apply_level(Pending& q, const Comps& c, const Ctx& x) {
    KilnProgressionResult& r = q.res;
    uint32_t level = q.req.value;
    const uint32_t cap = g_level_cap.load();
    if (level < 1) level = 1;
    if (cap && level > cap) level = cap;
    uint32_t need = 0;
    if (!g_xp_for_level(level, x.bal, &need)) {
        r.status = KILN_ERR_INTERNAL;
        set_res_reason(r, "the game's experience table faulted");
        return;
    }
    // as AdminSetCharacterLevel: level, granted 0, experience 0; needed as player_level sets it for a level reached
    // by playing (the admin command itself sets the next level's value)
    bool ok = set_attr(c.level, L.attr_level, x.reg, level) && g_recompute(F.recompute, c.level, x.reg);
    ok = ok && set_attr(c.exp, L.attr_pending, x.reg, 0) && set_attr(c.exp, L.attr_xp, x.reg, 0) &&
         g_recompute(F.recompute, c.exp, x.reg);
    ok = ok && set_attr(c.exp, L.attr_need, x.reg, need) && g_recompute(F.recompute_need, c.exp, x.reg);
    if (!ok) {
        r.status = KILN_ERR_INTERNAL;
        set_res_reason(r, "an attribute write failed");
        return;
    }
    r.applied = level;
    if (level != q.req.value) set_res_reason(r, "level %u is out of range: set to %u (cap %u)", q.req.value, level, cap);
}

void apply_experience(Pending& q, const Comps& c, const Ctx& x, KilnProgramCtx* ctx, const uint64_t* iter,
                      KilnEntity e) {
    KilnProgressionResult& r = q.res;
    if (q.req.op == KILN_PROG_OP_SET_EXPERIENCE) {
        if (!set_attr(c.exp, L.attr_xp, x.reg, q.req.value) || !g_recompute(F.recompute, c.exp, x.reg)) {
            r.status = KILN_ERR_INTERNAL;
            set_res_reason(r, "an attribute write failed");
            return;
        }
        r.applied = q.req.value;
        return;
    }
    if (r.before.level_cap && r.before.level >= r.before.level_cap) {
        r.status = KILN_ERR_UNAVAILABLE;
        set_res_reason(r, "level %u is the level cap: the game discards new experience", r.before.level);
        return;
    }
    uint32_t* pend = attr_slot(c.exp, L.attr_pending, x.reg);
    if (!pend) {
        r.status = KILN_ERR_INTERNAL;
        set_res_reason(r, "the granted-experience attribute did not resolve");
        return;
    }
    const uint32_t old = read_u32(pend);
    const uint64_t sum = static_cast<uint64_t>(old) + q.req.value;
    const uint32_t v = sum > 0x7fffffffu ? 0x7fffffffu : static_cast<uint32_t>(sum);
    // as the game's quest/item-set rewards: granted += amount, the formulas re-run, XPGainedEvent
    if (!g_memcpy(pend, &v, 4) || !g_recompute(F.recompute, c.exp, x.reg)) {
        r.status = KILN_ERR_INTERNAL;
        set_res_reason(r, "an attribute write failed");
        return;
    }
    r.applied = v - old;
    if (!(q.req.flags & KILN_PROG_NO_EVENT) && g_events_ok && s_ev >= 0 && iter[s_ev]) {
        if (auto* ev = static_cast<uint8_t*>(g_emit(reinterpret_cast<void*>(iter[s_ev]), L.ev_xp, L.ev_xp_size))) {
            const int64_t t = g_time(ctx);
            std::memcpy(ev, &t, 8);
            std::memcpy(ev + 8, &e, 4);
            std::memcpy(ev + 0xc, &r.applied, 4);
            ev[0x10] = static_cast<uint8_t>(q.req.source);
        }
    }
}

// The slot of a node in UnlockedSkillNodes, or -1.
int find_known(const uint8_t* u, uint32_t id) {
    for (int i = 0; i < static_cast<int>(kU_Count); ++i) {
        uint32_t v;
        std::memcpy(&v, u + kU_Known + i * 4, 4);
        if (v == id) return i;
    }
    return -1;
}

// A forced unlock skips the game's rules, and the game checks them again at the character's next join: it resets the whole
// tree then. So force says so instead of leaving a surprise for later (seen: a forced tree that skipped a link).
void warn_forced(KilnProgressionResult& r, const uint8_t* u, const Ctx& x, KilnEntity e) {
    alignas(8) uint8_t ui[kUiSize] = {};
    if (g_ui_rebuild(ui, u, x.tree) != 1) return;
    if (g_connected(ui, x.tree) != 1) {
        set_res_reason(r, "forced: this tree is not linked to a root, so the game resets it at this character's next join");
        return;
    }
    const int64_t spent = g_points_spent(ui, x.tree);
    const int64_t total = x.know ? g_points_total(x.know, x.bal, r.before.level, e) : -1;
    if (spent >= 0 && total >= 0 && spent > total)
        set_res_reason(r, "forced: it costs %lld of %lld points, so the game or Kiln resets it at this character's next join",
                       static_cast<long long>(spent), static_cast<long long>(total));
}

void apply_skill(Pending& q, const Comps& c, const Ctx& x, KilnEntity e, KilnProgramCtx* ctx, const uint64_t* iter) {
    KilnProgressionResult& r = q.res;
    auto* u = static_cast<uint8_t*>(c.unlocked);
    uint8_t init = 0;
    if (!g_memcpy(&init, u, 1) || !init) {
        r.status = KILN_ERR_UNAVAILABLE;
        set_res_reason(r, "the game has not set up this player's skill tree yet");
        return;
    }
    alignas(8) uint8_t ui[kUiSize] = {};
    const bool force = q.req.flags & KILN_PROG_FORCE;
    const KilnSkillsApi* shadow = shadow_api();
    ShadowScope scope(e);
    if (q.req.op == KILN_PROG_OP_RESPEC) {
        // the game's own reset (player_skilltree at login when the tree costs too much), free
        mark(ctx, iter, s_unlocked);
        uint32_t n = 0;
        for (size_t i = 0; i < kU_Count; ++i) {
            uint32_t id;
            std::memcpy(&id, u + kU_Known + i * 4, 4);
            uint8_t& lvl = u[kU_Levels + i];
            if (id == 0) {
                lvl = 0;
                continue;
            }
            const uint8_t* node = g_node(id, x.tree);
            uint8_t max = 0;
            if (node) g_memcpy(&max, node + kN_MaxLevel, 1);
            if (!node || max == 0) {
                std::memset(u + kU_Known + i * 4, 0, 4);
                lvl = 0;
                ++n;
            } else if (lvl >> 5) {
                lvl = static_cast<uint8_t>(lvl & 0x1f);
                ++n;
            }
        }
        ++u[kU_Change];
        uint32_t shadow_levels = 0;
        if (shadow) shadow->shadow_reset(KILN_WORLD_SERVER, e, &shadow_levels);  // their points come back too
        r.applied = n + shadow_levels;
        return;
    }
    const uint32_t id = q.req.node;
    const uint8_t* node = g_node(id, x.tree);
    if (!node) {
        r.status = KILN_ERR_NOT_FOUND;
        set_res_reason(r, "no skill node 0x%08x in the skill tree", id);
        return;
    }
    uint16_t cost = 0;
    uint8_t type = 0, max = 0;
    g_memcpy(&cost, node + kN_Cost, 2);
    g_memcpy(&type, node + kN_Type, 1);
    g_memcpy(&max, node + kN_MaxLevel, 1);
    if (type == 0) {
        r.status = KILN_ERR_INVALID_ARGUMENT;
        set_res_reason(r, "0x%08x is a root node: always unlocked", id);
        return;
    }
    // a shadow node's level is in kiln.skills' table, never in UnlockedSkillNodes
    uint32_t shadow_level = 0;
    const bool is_shadow = shadow && shadow->shadow_level(KILN_WORLD_SERVER, e, id, &shadow_level, nullptr) == KILN_OK;
    const int slot = is_shadow ? -1 : find_known(u, id);
    const uint8_t level = is_shadow ? static_cast<uint8_t>(shadow_level)
                          : slot >= 0 ? static_cast<uint8_t>(u[kU_Levels + slot] >> 5) : 0;
    const bool taken = is_shadow ? shadow_level > 0 : slot >= 0;
    if (q.req.op == KILN_PROG_OP_UNLOCK) {
        if (taken && (max == 0 || level >= max)) {
            r.status = KILN_ERR_INVALID_ARGUMENT;
            set_res_reason(r, max ? "already at its last level (%u)" : "already unlocked", static_cast<unsigned>(max));
            return;
        }
        if (!force) {
            if (g_ui_rebuild(ui, u, x.tree) != 1) {
                r.status = KILN_ERR_INTERNAL;
                set_res_reason(r, "the game's tree rebuild failed");
                return;
            }
            const int reach = g_reachable(id, ui, x.tree);
            if (reach != 1) {
                r.status = KILN_ERR_INVALID_ARGUMENT;
                set_res_reason(r, "not linked to an unlocked node (force skips this)");
                return;
            }
            const int64_t spent = g_points_spent(ui, x.tree);
            const int64_t total = x.know ? g_points_total(x.know, x.bal, r.before.level, e) : -1;
            if (spent < 0 || total < 0) {
                r.status = KILN_ERR_UNAVAILABLE;
                set_res_reason(r, "the skill points did not read (force skips the cost check)");
                return;
            }
            if (total < spent + cost) {
                r.status = KILN_ERR_INVALID_ARGUMENT;
                set_res_reason(r, "needs %u skill point(s), %lld available (give points, raise the level, or force)",
                               static_cast<unsigned>(cost), static_cast<long long>(total - spent));
                return;
            }
        }
        mark(ctx, iter, s_unlocked);
        if (g_node_set(id, u, true, x.tree) != 1) {
            r.status = KILN_ERR_INTERNAL;
            set_res_reason(r, "the game's unlock refused it (the node list is full?)");
            return;
        }
        if (!is_shadow) ++u[kU_Change];
        r.applied = 1;
        if (force) warn_forced(r, u, x, e);
        return;
    }
    // lock
    if (!taken || (max != 0 && level == 0)) {
        r.status = KILN_ERR_INVALID_ARGUMENT;
        set_res_reason(r, "not unlocked");
        return;
    }
    if (!force && shadow && g_ui_rebuild(ui, u, x.tree) == 1) {
        // a shadow node that would hang in the air: the game's connectivity check only follows native lists
        int32_t linked = 1;
        const bool last_level = is_shadow && level <= 1;
        if (is_shadow && last_level) shadow->shadow_linked(KILN_WORLD_SERVER, e, ui, x.tree, id, &linked);
        if (!linked) {
            r.status = KILN_ERR_INVALID_ARGUMENT;
            set_res_reason(r, "other unlocked nodes depend on it (lock them first, respec, or force)");
            return;
        }
    }
    mark(ctx, iter, s_unlocked);
    if (is_shadow && force && level <= 1) {
        // force also takes a node others hang on: the table is rewritten without it (NodeSet would refuse)
        KilnSkillsShadowEntry entries[256];
        uint32_t n = 0;
        shadow->shadow_get(KILN_WORLD_SERVER, e, entries, 256, &n);
        uint32_t w = 0;
        for (uint32_t i = 0; i < n && i < 256; ++i)
            if (entries[i].node != id) entries[w++] = entries[i];
        if (shadow->shadow_set(KILN_WORLD_SERVER, e, entries, w) != KILN_OK) {
            r.status = KILN_ERR_INTERNAL;
            set_res_reason(r, "the shadow table refused it");
            return;
        }
        r.applied = 1;
        return;
    }
    if (g_node_set(id, u, false, x.tree) != 1) {
        r.status = KILN_ERR_INTERNAL;
        set_res_reason(r, "the game's unlearn refused it");
        return;
    }
    if (is_shadow) {
        r.applied = 1;
        return;  // native nodes didn't change: nothing for the game to re-derive
    }
    if (!force) {
        // as the game's unlearn: the rest must stay connected, else undo
        int connected = g_ui_rebuild(ui, u, x.tree) == 1 ? g_connected(ui, x.tree) : -1;
        if (connected == 1 && shadow) {
            // the game's check follows native lists only: a shadow node may hang on this one
            int32_t linked = 1;
            shadow->shadow_linked(KILN_WORLD_SERVER, e, ui, x.tree, 0, &linked);
            if (!linked) connected = 0;
        }
        if (connected != 1) {
            g_node_set(id, u, true, x.tree);
            r.status = KILN_ERR_INVALID_ARGUMENT;
            set_res_reason(r, "other unlocked nodes depend on it (lock them first, respec, or force)");
            return;
        }
    }
    ++u[kU_Change];
    r.applied = 1;
}

void apply_points(Pending& q, const Ctx& x, void* queue, KilnEntity e) {
    KilnProgressionResult& r = q.res;
    const uint8_t* block = know_block(x.know, e);
    if (!block) {
        r.status = KILN_ERR_UNAVAILABLE;
        set_res_reason(r, "this player's knowledge is not loaded");
        return;
    }
    uint32_t pushed = 0;
    for (const KnowEntry& k : g_know) {
        if (pushed >= q.req.value) break;
        if (k.amount != 1) continue;
        const int o = g_know_owned(block, k.id);
        if (o != 0) continue;
        if (!g_know_push(queue, k.id, e)) break;  // the queue is full (0x800 entries)
        ++pushed;
    }
    r.applied = pushed;
    if (pushed == 0) {
        r.status = KILN_ERR_UNAVAILABLE;
        set_res_reason(r, "no skill-point knowledge left to give (raise the level for more points)");
    } else if (pushed < q.req.value) {
        set_res_reason(r, "only %u of %u: no more skill-point knowledge left", pushed, q.req.value);
    }
}

void KILN_CALL prog_each(KilnProgramCtx* ctx, uint64_t* iter, KilnEntity e, void*) {
    g_updates.fetch_add(1, std::memory_order_relaxed);
    std::vector<Pending> mine;
    {
        std::lock_guard lock(g_mu);
        if (g_queue.empty()) return;
        for (auto it = g_queue.begin(); it != g_queue.end();) {
            if (it->req.entity == e) {
                mine.push_back(std::move(*it));
                it = g_queue.erase(it);
            } else {
                ++it;
            }
        }
    }
    if (mine.empty()) return;
    Comps c;
    c.exp = s_exp >= 0 ? reinterpret_cast<void*>(iter[s_exp]) : nullptr;
    c.level = s_level >= 0 ? reinterpret_cast<void*>(iter[s_level]) : nullptr;
    c.unlocked = s_unlocked >= 0 ? reinterpret_cast<void*>(iter[s_unlocked]) : nullptr;
    Ctx x{s_reg >= 0 ? reinterpret_cast<void*>(iter[s_reg]) : nullptr,
          s_bal >= 0 ? reinterpret_cast<void*>(iter[s_bal]) : nullptr,
          s_tree >= 0 ? reinterpret_cast<void*>(iter[s_tree]) : nullptr,
          s_know >= 0 ? reinterpret_cast<void*>(iter[s_know]) : nullptr};
    const uint64_t step = g_step.load();
    for (Pending& q : mine) {
        KilnProgressionResult& r = q.res;
        r.before = read(e, c, x, step);
        r.status = KILN_OK;
        switch (q.req.op) {
        case KILN_PROG_OP_SET_LEVEL:
        case KILN_PROG_OP_ADD_EXPERIENCE:
        case KILN_PROG_OP_SET_EXPERIENCE:
            if (!g_level_ok || !(r.before.flags & KILN_PROG_HAS_LEVEL) || !x.reg || !x.bal) {
                r.status = KILN_ERR_UNAVAILABLE;
                set_res_reason(r, "levels do not work on this build or for this entity");
                break;
            }
            mark(ctx, iter, s_exp);
            if (q.req.op == KILN_PROG_OP_SET_LEVEL) mark(ctx, iter, s_level);
            if (q.req.op == KILN_PROG_OP_SET_LEVEL) apply_level(q, c, x);
            else apply_experience(q, c, x, ctx, iter, e);
            break;
        case KILN_PROG_OP_UNLOCK:
        case KILN_PROG_OP_LOCK:
        case KILN_PROG_OP_RESPEC:
            if (!g_skills_ok || !c.unlocked || !x.tree) {
                r.status = KILN_ERR_UNAVAILABLE;
                set_res_reason(r, "the skill tree does not work on this build or for this entity");
                break;
            }
            apply_skill(q, c, x, e, ctx, iter);
            break;
        case KILN_PROG_OP_GIVE_POINTS:
            if (!g_know_ok || s_queue < 0 || !iter[s_queue]) {
                r.status = KILN_ERR_UNAVAILABLE;
                set_res_reason(r, "skill-point knowledge does not work on this build");
                break;
            }
            apply_points(q, x, reinterpret_cast<void*>(iter[s_queue]), e);
            break;
        default:
            r.status = KILN_ERR_INVALID_ARGUMENT;
            set_res_reason(r, "unknown op %u", q.req.op);
        }
        q.applied_step = step;
    }
    std::lock_guard lock(g_mu);
    for (Pending& q : mine) g_applied.push_back(std::move(q));
}

const KilnStr kWrite[] = {{"Experience", 10}, {"Level", 5}, {"UnlockedSkillNodes", 18}};
const KilnStr kWith[] = {{"PlayerState", 11}};
const KilnStr kSingRead[] = {{"impact::AttributeRegistry", 25}, {"BalancingRegistry", 17},
                             {"g38_skilltree::SkillTree", 24}, {"G38Knowledge", 12}};
const KilnStr kSingWrite[] = {{"KnowledgeTriggerQueue", 21}};
const KilnStr kEventsOut[] = {{"XPGainedEvent", 13}};

void register_program() {
    if (!W || W->header.size < sizeof(KilnWorldApi)) {
        set_reason("kiln.world is unavailable: changes are off");
        PW("kiln.world is unavailable: progression changes are off (reads work)");
        return;
    }
    if (!g_level_ok && !g_skills_ok && !g_know_ok) return;
    KilnProgramDesc d{};
    d.size = sizeof d;
    d.flags = KILN_PROGRAM_HOST_ONLY;
    d.name = kstr("kiln_players_progression");
    d.each = &prog_each;
    d.write = KilnNames{kWrite, 3, 0};
    d.with = KilnNames{kWith, 1, 0};
    d.singletons_read = KilnNames{kSingRead, 4, 0};
    d.singletons_read_optional = 0x0Cu;  // the tree and the knowledge may be missing (levels still work)
    d.singletons_write = KilnNames{kSingWrite, 1, 0};
    d.singletons_write_optional = 0x1u;
    if (g_events_ok) d.events_out = KilnNames{kEventsOut, 1, 0};
    if (W->register_program(g_owner, &d, &g_prog) != KILN_OK) {
        set_reason("kiln.world refused the program: changes are off");
        PW("kiln.world refused kiln_players_progression: changes are off (reads work)");
        return;
    }
    s_exp = W->program_slot(g_prog, KILN_SLOT_WRITE, kWrite[0]);
    s_level = W->program_slot(g_prog, KILN_SLOT_WRITE, kWrite[1]);
    s_unlocked = W->program_slot(g_prog, KILN_SLOT_WRITE, kWrite[2]);
    s_reg = W->program_slot(g_prog, KILN_SLOT_SINGLETON, kSingRead[0]);
    s_bal = W->program_slot(g_prog, KILN_SLOT_SINGLETON, kSingRead[1]);
    s_tree = W->program_slot(g_prog, KILN_SLOT_SINGLETON, kSingRead[2]);
    s_know = W->program_slot(g_prog, KILN_SLOT_SINGLETON, kSingRead[3]);
    s_queue = W->program_slot(g_prog, KILN_SLOT_SINGLETON, kSingWrite[0]);
    if (g_events_ok) s_ev = W->program_slot(g_prog, KILN_SLOT_EVENT_OUT, kEventsOut[0]);
    PI("program kiln_players_progression accepted (write slots %d %d %d, singletons %d %d %d %d %d, XPGainedEvent "
       "slot %d)",
       s_exp, s_level, s_unlocked, s_reg, s_bal, s_tree, s_know, s_queue, s_ev);
    g_program_ok = s_exp >= 0 && s_level >= 0 && s_unlocked >= 0 && s_reg >= 0 && s_bal >= 0;
}

bool program_active() {
    if (!g_program_ok || !W) return false;
    KilnProgramInfo info{};
    info.size = sizeof info;
    return W->program_info(g_prog, &info) == KILN_OK && info.state == KILN_PROGRAM_ACTIVE;
}

// ------------------------------------------------------------------------------------------------ the table -----
bool on_tick() { return g_api && g_api->on_tick_thread(); }
bool available() { return g_level_ok || g_skills_ok || g_know_ok; }

KilnResult KILN_CALL t_status(KilnProgressionStatus* out) {
    if (!out || out->size < sizeof(KilnProgressionStatus)) return KILN_ERR_INVALID_ARGUMENT;
    KilnProgressionStatus s{};
    s.size = sizeof s;
    s.level = g_level_ok;
    s.skills = g_skills_ok;
    s.knowledge = g_know_ok;
    s.events = g_events_ok;
    s.program = program_active();
    s.world = g_world_ok;
    s.level_cap = g_level_cap;
    s.points_per_level = g_per_level;
    s.knowledge_plus1 = g_know_plus1;
    s.submitted = g_submitted;
    s.applied = g_applied_n;
    s.failed = g_failed;
    s.program_updates = g_updates;
    s.last_seq = g_seq_pub;
    {
        std::lock_guard lock(g_mu);
        s.tree_nodes = static_cast<uint32_t>(g_tree.size());
        s.knowledge_entries = static_cast<uint32_t>(g_know.size());
        s.pending = static_cast<uint32_t>(g_queue.size() + g_applied.size());
        std::snprintf(s.reason, sizeof s.reason, "%s", g_reason.c_str());
    }
    *out = s;
    return KILN_OK;
}

KilnResult KILN_CALL t_get(KilnEntity e, KilnProgression* out) {
    if (!out || e == 0) return KILN_ERR_INVALID_ARGUMENT;
    if (!available()) return KILN_ERR_UNAVAILABLE;
    if (!playing(e)) return KILN_ERR_NOT_FOUND;
    if (!on_tick()) {
        if (e > 64) return KILN_ERR_NOT_FOUND;
        std::lock_guard lock(g_mu);
        if (g_snap[e].size == 0) return KILN_ERR_NOT_FOUND;
        *out = g_snap[e];
        out->flags |= KILN_PROG_SNAPSHOT;
        return KILN_OK;
    }
    setup_world();
    Comps c;
    if (!comps_of(e, &c)) return KILN_ERR_NOT_FOUND;
    *out = read(e, c, Ctx{g_w.attr_reg, g_w.balancing, g_w.tree, g_w.knowledge}, g_step.load());
    return KILN_OK;
}

KilnResult KILN_CALL t_nodes(KilnEntity e, KilnSkillNodeState* out, uint32_t capacity, uint32_t* out_count) {
    if ((!out && capacity) || e == 0) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_skills_ok) return KILN_ERR_UNAVAILABLE;
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    if (!playing(e)) return KILN_ERR_NOT_FOUND;
    setup_world();
    Comps c;
    comps_of(e, &c);
    if (!c.unlocked) return KILN_ERR_NOT_FOUND;
    uint8_t u[kUnlockedSize];
    if (!g_memcpy(u, c.unlocked, sizeof u)) return KILN_ERR_INTERNAL;
    uint32_t n = 0;
    for (size_t i = 0; i < kU_Count; ++i) {
        uint32_t id;
        std::memcpy(&id, u + kU_Known + i * 4, 4);
        if (!id) continue;
        const uint8_t* node = g_node(id, g_w.tree);
        uint8_t max = 0, type = 0;
        uint16_t cost = 0;
        if (node) {
            g_memcpy(&max, node + kN_MaxLevel, 1);
            g_memcpy(&type, node + kN_Type, 1);
            g_memcpy(&cost, node + kN_Cost, 2);
        }
        const uint8_t level = max ? static_cast<uint8_t>(u[kU_Levels + i] >> 5) : 1;
        if (max && level == 0) continue;  // a node with levels at level 0 is not unlocked
        if (n < capacity) {
            out[n].id = id;
            out[n].level = level;
            out[n].max_level = max;
            out[n].cost = static_cast<uint16_t>(type == 0 ? 0 : cost * (max ? level : 1));
        }
        ++n;
    }
    // the player's shadow nodes (kiln.skills' table): the same list as far as anyone reading it is concerned
    if (const KilnSkillsApi* shadow = shadow_api()) {
        KilnSkillsShadowEntry entries[256];
        uint32_t m = 0;
        shadow->shadow_get(KILN_WORLD_SERVER, e, entries, 256, &m);
        for (uint32_t i = 0; i < m && i < 256; ++i) {
            const uint8_t* node = g_node(entries[i].node, g_w.tree);
            uint8_t max = 0;
            uint16_t cost = 0;
            if (node) {
                g_memcpy(&max, node + kN_MaxLevel, 1);
                g_memcpy(&cost, node + kN_Cost, 2);
            }
            if (n < capacity) {
                out[n].id = entries[i].node;
                out[n].level = entries[i].level;
                out[n].max_level = max;
                out[n].cost = static_cast<uint16_t>(cost * (max ? entries[i].level : 1));
            }
            ++n;
        }
    }
    if (out_count) *out_count = n;
    return n > capacity ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

KilnResult KILN_CALL t_tree(KilnSkillNode* out, uint32_t capacity, uint32_t* out_count) {
    if (!out && capacity) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_skills_ok) return KILN_ERR_UNAVAILABLE;
    std::lock_guard lock(g_mu);
    const uint32_t n = static_cast<uint32_t>(g_tree.size());
    for (uint32_t i = 0; i < n && i < capacity; ++i) out[i] = g_tree[i];
    if (out_count) *out_count = n;
    if (n == 0) return KILN_ERR_NOT_FOUND;  // before the world's first step
    return n > capacity ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

KilnResult KILN_CALL t_submit(KilnOwner owner, const KilnProgressionRequest* rq, uint64_t* out_request) {
    if (!rq || rq->size < sizeof(KilnProgressionRequest) || rq->entity == 0) return KILN_ERR_INVALID_ARGUMENT;
    switch (rq->op) {
    case KILN_PROG_OP_SET_LEVEL:
        if (rq->value < 1 || rq->value > 1000) return KILN_ERR_INVALID_ARGUMENT;
        if (!g_level_ok) return KILN_ERR_UNAVAILABLE;
        break;
    case KILN_PROG_OP_ADD_EXPERIENCE:
        if (rq->value < 1 || rq->value > 100000000u || rq->source > 6) return KILN_ERR_INVALID_ARGUMENT;
        if (!g_level_ok) return KILN_ERR_UNAVAILABLE;
        break;
    case KILN_PROG_OP_SET_EXPERIENCE:
        if (rq->value > 100000000u) return KILN_ERR_INVALID_ARGUMENT;
        if (!g_level_ok) return KILN_ERR_UNAVAILABLE;
        break;
    case KILN_PROG_OP_GIVE_POINTS:
        if (rq->value < 1 || rq->value > 1000) return KILN_ERR_INVALID_ARGUMENT;
        if (!g_know_ok) return KILN_ERR_UNAVAILABLE;
        break;
    case KILN_PROG_OP_UNLOCK:
    case KILN_PROG_OP_LOCK:
        if (rq->node == 0) return KILN_ERR_INVALID_ARGUMENT;
        [[fallthrough]];
    case KILN_PROG_OP_RESPEC:
        if (!g_skills_ok) return KILN_ERR_UNAVAILABLE;
        break;
    default: return KILN_ERR_INVALID_ARGUMENT;
    }
    if (rq->flags & ~(KILN_PROG_FORCE | KILN_PROG_NO_EVENT)) return KILN_ERR_INVALID_ARGUMENT;
    if (!program_active()) return KILN_ERR_UNAVAILABLE;
    if (!playing(rq->entity)) return KILN_ERR_NOT_FOUND;
    std::lock_guard lock(g_mu);
    if (g_queue.size() >= 256) return KILN_ERR_BUFFER_TOO_SMALL;
    Pending p;
    p.id = g_next_id++;
    p.owner = owner;
    p.req = *rq;
    p.submitted_step = g_step.load();
    p.res.size = sizeof p.res;
    p.res.op = rq->op;
    p.res.request = p.id;
    p.res.entity = rq->entity;
    g_queue.push_back(p);
    g_submitted.fetch_add(1);
    if (out_request) *out_request = p.id;
    return KILN_OK;
}

KilnResult KILN_CALL t_result(uint64_t request, KilnProgressionResult* out) {
    if (!out || request == 0) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    const KilnProgressionResult& r = g_results[request % g_results.size()];
    if (r.request != request) return KILN_ERR_NOT_FOUND;
    *out = r;
    return KILN_OK;
}

KilnResult KILN_CALL t_events(uint64_t after, KilnProgressionEvent* out, uint32_t capacity, uint32_t* out_count) {
    if (!out && capacity) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    uint64_t from = after + 1;
    const uint64_t oldest = g_seq >= g_ring.size() ? g_seq - g_ring.size() + 1 : 1;
    if (from < oldest) from = oldest;
    uint32_t n = 0;
    for (uint64_t s = from; s <= g_seq && n < capacity; ++s) out[n++] = g_ring[s % g_ring.size()];
    if (out_count) *out_count = n;
    return KILN_OK;
}

uint64_t KILN_CALL t_last_seq() { return g_seq_pub.load(); }

KilnResult KILN_CALL t_xp_for_level(uint32_t level, uint32_t* out_needed, uint32_t* out_cap) {
    if (!out_needed || level == 0 || level > 1000) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_level_ok) return KILN_ERR_UNAVAILABLE;
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    setup_world();
    if (!g_w.balancing) return KILN_ERR_UNAVAILABLE;
    if (!g_xp_for_level(level, g_w.balancing, out_needed)) return KILN_ERR_INTERNAL;
    if (out_cap) *out_cap = g_level_cap;
    return KILN_OK;
}

KilnResult KILN_CALL t_simulate(const KilnSkillSimStep* steps, uint32_t count, KilnSkillSimResult* out) {
    if ((!steps || !out) && count) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_skills_ok) return KILN_ERR_UNAVAILABLE;
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    setup_world();
    if (!g_w.tree) return KILN_ERR_UNAVAILABLE;
    // A fresh UnlockedSkillNodes: initialised, nothing known (root nodes are implicit: the game's link check counts a
    // root neighbour as unlocked).
    alignas(8) static uint8_t u[kUnlockedSize];
    alignas(8) static uint8_t ui[kUiSize];
    std::memset(u, 0, sizeof u);
    u[0] = 1;
    for (uint32_t i = 0; i < count; ++i) {
        KilnSkillSimResult& r = out[i];
        r = KilnSkillSimResult{};
        r.op = steps[i].op;
        r.node = steps[i].node;
        if (g_ui_rebuild(ui, u, g_w.tree) != 1) return KILN_ERR_INTERNAL;
        r.reachable = g_reachable(steps[i].node, ui, g_w.tree) == 1;
        switch (steps[i].op) {
        case KILN_SIM_UNLOCK: r.ok = g_node_set(steps[i].node, u, true, g_w.tree) == 1; break;
        case KILN_SIM_LOCK: r.ok = g_node_set(steps[i].node, u, false, g_w.tree) == 1; break;
        case KILN_SIM_RESPEC:
            std::memset(u, 0, sizeof u);
            u[0] = 1;
            r.ok = true;
            break;
        case KILN_SIM_CHECK: r.ok = true; break;
        default: return KILN_ERR_INVALID_ARGUMENT;
        }
        if (g_ui_rebuild(ui, u, g_w.tree) != 1) return KILN_ERR_INTERNAL;
        r.connected = g_connected(ui, g_w.tree) == 1;
        const int64_t spent = g_points_spent(ui, g_w.tree);
        r.spent = spent > 0 ? static_cast<uint32_t>(spent) : 0;
        for (int k = 0; k < 4; ++k) {
            uint64_t m;
            std::memcpy(&m, ui + k * 8, 8);
            r.nodes += static_cast<uint32_t>(__builtin_popcountll(m));
        }
        const int slot = find_known(u, steps[i].node);
        r.level = slot >= 0 ? static_cast<uint8_t>(u[kU_Levels + slot] >> 5) : 0;
    }
    return KILN_OK;
}

// The root id the registry gives an attribute (the value the game puts into a component's rootId at init).
bool attr_root(uint32_t id, void* reg, uint32_t* root) {
    __try {
        alignas(8) uint8_t out[16] = {};
        F.attr_find(out, id, reg);
        const uint8_t* info = *reinterpret_cast<uint8_t* const*>(out + 8);
        if (out[0] != 0 || !info) return false;
        *root = *reinterpret_cast<const uint32_t*>(info);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

KilnResult KILN_CALL t_attr_probe(KilnAttrProbe* pr) {
    if (!pr || pr->size < sizeof(KilnAttrProbe) || pr->component > 1) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_level_ok) return KILN_ERR_UNAVAILABLE;
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    setup_world();
    if (!g_w.attr_reg) return KILN_ERR_UNAVAILABLE;
    const uint32_t count = pr->component == 0 ? 3u : 2u;
    uint32_t root = 0;
    if (!attr_root(pr->component == 0 ? L.attr_xp : L.attr_level, g_w.attr_reg, &root)) return KILN_ERR_NOT_FOUND;
    alignas(8) uint8_t c[8 + 16] = {};
    std::memcpy(c, &root, 4);
    const uint16_t off = 8;
    std::memcpy(c + 4, &off, 2);
    c[6] = 1;  // initialised
    c[7] = static_cast<uint8_t>(count);
    std::memcpy(c + 8, pr->storage, 4 * count);
    const uint32_t ids[4] = {L.attr_level, L.attr_xp, L.attr_pending, L.attr_need};
    for (int i = 0; i < 4; ++i) {
        uint32_t* s = attr_slot(c, ids[i], g_w.attr_reg);
        pr->slots[i] = s ? static_cast<int32_t>((reinterpret_cast<uint8_t*>(s) - (c + 8)) / 4) : -1;
    }
    pr->root = root;
    if (pr->set_id) {
        if (!set_attr(c, pr->set_id, g_w.attr_reg, pr->set_value)) return KILN_ERR_NOT_FOUND;
        const bool ok = g_recompute(pr->set_id == L.attr_need ? F.recompute_need : F.recompute, c, g_w.attr_reg);
        if (!ok) return KILN_ERR_INTERNAL;
    }
    std::memcpy(pr->storage, c + 8, 4 * count);
    return KILN_OK;
}

KilnResult KILN_CALL t_lab_player(KilnEntity e) {
    g_lab_player.store(e);
    return KILN_OK;
}

const KilnProgressionApi kTable = {
    {sizeof(KilnProgressionApi), KILN_PROGRESSION_VERSION},
    &t_status, &t_get, &t_nodes, &t_tree, &t_submit, &t_result, &t_events, &t_last_seq, &t_xp_for_level, &t_simulate,
    &t_attr_probe, &t_lab_player,
};

// ------------------------------------------------------------------------------------------------ Pre -----------
bool g_world_logged = false;

void finish(Pending& q) {
    KilnProgressionResult& r = q.res;
    r.steps = static_cast<uint32_t>(g_step.load() - q.submitted_step);
    Comps c;
    if (playing(r.entity) && comps_of(r.entity, &c))
        r.after = read(r.entity, c, Ctx{g_w.attr_reg, g_w.balancing, g_w.tree, g_w.knowledge}, g_step.load());
    if (r.status == KILN_OK) {
        g_applied_n.fetch_add(1);
        if (r.after.size) observe(r.after, r.request);
    } else {
        g_failed.fetch_add(1);
    }
    {
        std::lock_guard lock(g_mu);
        g_results[r.request % g_results.size()] = r;
    }
    static const char* const kOps[] = {"?", "set_level", "add_experience", "set_experience", "give_points", "unlock",
                                       "lock", "respec"};
    logf(r.status == KILN_OK ? KILN_LOG_INFO : KILN_LOG_WARN,
         "progression: request %llu %s entity %u value %u node 0x%08x: %s (applied %u; level %u -> %u, experience %u "
         "-> %u (+%u granted), points %d -> %d of %u, nodes %u -> %u)%s%s",
         static_cast<unsigned long long>(r.request), kOps[r.op < 8 ? r.op : 0], r.entity, q.req.value, q.req.node,
         r.status == KILN_OK ? "ok" : "refused", r.applied, r.before.level, r.after.level, r.before.experience,
         r.after.experience, r.after.experience_pending, r.before.points_available, r.after.points_available,
         r.after.points_total, r.before.nodes, r.after.nodes, r.reason[0] ? ": " : "", r.reason);
    if (q.req.callback) q.req.callback(&r, q.req.user);
}

}  // namespace

void main(const KilnApi* api, KilnOwner owner) {
    g_api = api;
    g_owner = owner;
    auto feat = [&](const char* n) { return api->feature(kstr(n)); };
    int64_t v = 0;
    auto c32 = [&](const char* n, uint32_t* out) {
        if (!constant(n, &v)) return false;
        *out = static_cast<uint32_t>(v);
        return true;
    };
    bool level = feat("players.progression") && sym("players/AttrFind", &F.attr_find) &&
                 sym("players/AttrRecompute", &F.recompute) && sym("players/AttrRecomputeNeed", &F.recompute_need) &&
                 sym("players/XpForLevel", &F.xp_for_level) && sym("players/LevelCap", &F.level_cap) &&
                 c32("players/attr.level", &L.attr_level) && c32("players/attr.xp", &L.attr_xp) &&
                 c32("players/attr.pending", &L.attr_pending) && c32("players/attr.need", &L.attr_need);
    bool skills = feat("players.skills") && sym("players/SkillNodeById", &F.node_by_id) &&
                  sym("players/SkillReachable", &F.reachable) && sym("players/SkillNodeSet", &F.node_set) &&
                  sym("players/SkillUiRebuild", &F.ui_rebuild) && sym("players/SkillTreeConnected", &F.connected) &&
                  sym("players/SkillPointsTotal", &F.points_total) && sym("players/SkillPointsSpent", &F.points_spent) &&
                  sym("players/SkillPointsPerLevel", &F.per_level);
    int64_t k[10] = {};
    bool know = feat("players.skill_knowledge") && sym("players/KnowledgeIndex", &F.know_index) &&
                sym("players/KnowledgePlayerContext", &F.know_ctx) && sym("players/KnowledgeQueuePush", &F.know_push) &&
                constant("players/know.system", &k[0]) && constant("players/know.single", &k[1]) &&
                constant("players/know.blocks", &k[2]) && constant("players/know.block_valid", &k[3]) &&
                constant("players/know.data", &k[4]) && constant("players/know.data_resource", &k[5]) &&
                constant("players/know.skill_points", &k[6]) && constant("players/know.block_capacity", &k[7]) &&
                constant("players/know.block_values", &k[8]) && constant("players/know.block_stride", &k[9]);
    if (know) {
        L.know_system = k[0], L.know_single = k[1], L.know_blocks = k[2], L.know_valid = k[3], L.know_data = k[4];
        L.know_res = k[5], L.know_points = k[6], L.know_cap = k[7], L.know_values = k[8], L.know_stride = k[9];
        know = L.know_stride > 0 && L.know_stride < 0x1000;
    }
    bool events = level && feat("players.progression_events") && sym("players/EmitEvent", &F.emit) &&
                  sym("players/JobProgramId", &F.job_program) && sym("players/JobEventBuffer", &F.job_buffer) &&
                  c32("players/event.xp_gained", &L.ev_xp) && c32("players/event.xp_gained_size", &L.ev_xp_size);
    if (events) sym("players/ProgramTime", &F.program_time);
    if (!level) set_reason("feature players.progression is off on this build");
    if (!skills) set_reason("feature players.skills is off on this build");
    if (!know) set_reason("feature players.skill_knowledge is off on this build");
    // Knowledge points and the cost check need the level; the knowledge part needs the skills for its sums.
    g_level_ok = level;
    g_skills_ok = skills && level;
    g_know_ok = know && skills && level;
    g_events_ok = events;
    PI("layout: level/experience %s, skill tree %s, skill-point knowledge %s, XPGainedEvent %s", g_level_ok ? "resolved" : "OFF",
       g_skills_ok ? "resolved" : "OFF", g_know_ok ? "resolved" : "OFF", g_events_ok ? "resolved" : "off");
    if (!available()) {
        PW("nothing of kiln.progression works on this build: not published");
        return;
    }
    api->register_service(owner, kstr(KILN_PROGRESSION_SERVICE), &kTable);
}

void ready() {
    if (!available()) return;
    W = static_cast<const KilnWorldApi*>(g_api->service(kstr(KILN_WORLD_SERVICE), 1));
    register_program();
}

void pre(uint64_t step) {
    if (!available()) return;
    g_step.store(step);
    if (!g_api->server_world()) return;
    setup_world();
    if (!g_world_logged && g_w.looked) {
        g_world_logged = true;
        if (g_program_ok)
            PI("program kiln_players_progression is %s", program_active() ? "active" : "NOT active (changes are off)");
    }
    // Complete the requests the program applied two steps ago (the game's player_level / player_skilltree ran since).
    std::vector<Pending> done;
    {
        std::lock_guard lock(g_mu);
        for (auto it = g_applied.begin(); it != g_applied.end();) {
            if (it->applied_step + 2 <= step) {
                done.push_back(std::move(*it));
                it = g_applied.erase(it);
            } else {
                ++it;
            }
        }
        // Requests the program never reached: the player left, or the program does not run.
        for (auto it = g_queue.begin(); it != g_queue.end();) {
            const bool gone = !playing(it->req.entity);
            if (gone || step > it->submitted_step + 250) {
                Pending p = std::move(*it);
                it = g_queue.erase(it);
                p.res.status = gone ? KILN_ERR_NOT_FOUND : KILN_ERR_UNAVAILABLE;
                set_res_reason(p.res, gone ? "the player left before the change" : "the program did not reach the player");
                p.applied_step = step;
                done.push_back(std::move(p));
            } else {
                ++it;
            }
        }
    }
    for (Pending& q : done) finish(q);
    // Snapshot + events every 8 steps.
    if (step % 8 != 0 || !W) return;
    for (KilnEntity e = 1; e <= 64; ++e) {
        if (!kiln::players::in_game(e)) {
            if (g_seen[e].valid || g_seen[e].skills) {
                g_seen[e] = SlotSeen{};
                std::lock_guard lock(g_mu);
                g_snap[e] = KilnProgression{};
            }
            continue;
        }
        Comps c;
        if (!comps_of(e, &c)) continue;
        observe(read(e, c, Ctx{g_w.attr_reg, g_w.balancing, g_w.tree, g_w.knowledge}, step), 0);
    }
}

void session_end() {
    std::vector<Pending> drop;
    {
        std::lock_guard lock(g_mu);
        for (Pending& p : g_queue) drop.push_back(std::move(p));
        for (Pending& p : g_applied) drop.push_back(std::move(p));
        g_queue.clear();
        g_applied.clear();
        for (auto& s : g_snap) s = KilnProgression{};
        g_tree.clear();
    }
    for (auto& s : g_seen) s = SlotSeen{};
    for (Pending& p : drop) {
        p.res.status = KILN_ERR_NOT_FOUND;
        set_res_reason(p.res, "the world closed");
        finish(p);
    }
    g_w = World{};
    g_world_logged = false;
}

}  // namespace kiln::progression
