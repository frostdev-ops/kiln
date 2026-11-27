// SPDX-License-Identifier: MIT
// kiln.items v8 (since 0.2.0-dev.29.19, the equipment wave): gear writes (kiln_items.h "gear", docs/re/items.md
// section 19).
//
// The game keeps an instance's rarity as its enhancement count (PerkContainerNew {availablePerks, unlockedPerks}) and
// derives the shown rarity, level and damage from it every step (item_state_init); max durability is computed once by
// item_durability_init while ItemState lacks DurabilityInitialized. kiln_items_gear is a SYSTEM program over kiln.world
// with kiln_items' own singletons, accessors and event senders (so a later cost transaction can run in it) plus three
// other-entity accessors of its own: Durability, Level, PerkContainerNew. Serial (other-write), appended after the game's
// programs. Each queued request is checked against the item entity's v4 stamp, planned by gear_logic.h and written
// through the game's tracked other-entity write (WriteOther: the job's tracker marks the component for replication and
// the save) and the attribute's own formulas (AttrChanged; the UInt32 variant for Level, as the game's UnlockPerk). The
// program first checks the marking's per-component bits: ItemState (where the game puts the rarity, level and damage it
// derives, what players are sent) must be replicated, PerkContainerNew and Level saved; if not, gear writes stay off.
// Upgrades (package P3, kiln_items.h "upgrades", upgrade_logic.h) run in the same program: the write planned and its
// components marked, the costs counted in the payers and taken on one inventory transaction of the program's own,
// counted again after the commit, and only then the write: all or nothing.
#include "gear.h"

#include <windows.h>
#include <malloc.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <deque>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include "costs.h"
#include "engine.h"
#include "gear_logic.h"
#include "upgrade_logic.h"
#include "instances.h"
#include "services/kiln_caps.h"
#include "services/kiln_saves.h"
#include "services/kiln_types.h"
#include "wear.h"

namespace kitems {
void items_log(int level, const char* fmt, ...);
const KilnApi* items_api();
KilnOwner items_owner();
uintptr_t items_server_world();
uint64_t items_next_ticket();

namespace {

#define LOGI(...) items_log(KILN_LOG_INFO, __VA_ARGS__)
#define LOGW(...) items_log(KILN_LOG_WARN, __VA_ARGS__)
#define LOGE(...) items_log(KILN_LOG_ERROR, __VA_ARGS__)

constexpr const char* kProgram = "kiln_items_gear";
constexpr const char* kFeature = "items.gear";
constexpr uint32_t kMaxPending = 1024;
constexpr uint32_t kPerStep = 32;         // writes per sim step (no transaction: a few component writes each)
constexpr uint64_t kTimeoutSteps = 250;   // ~10 s at 24.5 Hz without a program update: DROPPED

constexpr uint32_t fnv(const char* s) {
    uint32_t h = 0x811c9dc5u;
    for (; *s; ++s) h = (h ^ static_cast<uint8_t>(*s)) * 0x01000193u;
    return h;
}
// The three components of this program's own (FNV-1a-32 of "keen::ecs::<name>"; the same as instances.cpp's).
constexpr uint32_t kHashDurability = fnv("keen::ecs::Durability");
constexpr uint32_t kHashLevel = fnv("keen::ecs::Level");
constexpr uint32_t kHashPerks = fnv("keen::ecs::PerkContainerNew");
static_assert(kHashDurability == 0xdffb1608u && kHashLevel == 0xe550da37u && kHashPerks == 0xe288d50cu);
constexpr uint32_t kAttrHeader = 8;  // attribute block {u32 group; u16 values offset; u8 flags (1: values); u8 count}

template <typename T>
T rd(const void* p, int64_t off) {
    T v;
    std::memcpy(&v, static_cast<const uint8_t*>(p) + off, sizeof v);
    return v;
}
KilnStr str(const char* s) { return KilnStr{s, std::strlen(s)}; }

// ---- what resolve() and the layout check found ----
using AttrChangedU32Fn = void(__fastcall*)(void* component, void* registry);
struct Facts {
    bool sym = false;            // feature items.gear: symbols and constants
    AttrChangedU32Fn attr_changed_u32 = nullptr;
    uint32_t attr_level = 0, attr_level_max = 0, attr_dur = 0, attr_dur_max = 0;
    int64_t replicated = 0, saved = 0;  // world/registry.replicated, .saved: per-component bit arrays of the ECS registry
    // the layouts by this build's reflection (kiln.types), checked once
    bool layout = false;
    uint32_t info_rarity = 0, info_perk_ids = 0, info_levels = 0;  // keen::ItemInfo
    uint32_t state_item = 0, state_bits = 0, state_rarity = 0, state_level = 0;  // keen::ecs::ItemState
    uint16_t init_bit = 0, equipped_bit = 0;                       // keen::ItemStateFlag masks
};
Facts F;

std::mutex g_mu;  // the containers below and g_detail; never held while calling the game or a callback
struct Req {
    uint64_t ticket;
    KilnOwner owner;
    KilnItemsGearRequest r;
    uint64_t submitted;
};
struct Callback {
    KilnItemsGearDoneFn fn;
    void* user;
    KilnOwner owner;
};
std::deque<Req> g_pending;
std::vector<KilnItemsGearResult> g_done;
std::unordered_map<uint64_t, Callback> g_callbacks;
char g_detail[160] = "not registered";

const KilnWorldApi* g_w = nullptr;
KilnHandle g_prog = 0;
std::atomic<int> g_state{KILN_ITEMS_PROGRAM_NONE};
std::atomic<bool> g_off{false};
std::atomic<uint32_t> g_pending_n{0};
std::atomic<uint64_t> g_writes{0}, g_refused{0}, g_last_update{0};
std::atomic<uint8_t> g_replicated{0}, g_saved{0};
std::once_flag g_layout_once;

void set_detail(const char* s) {
    std::lock_guard lock(g_mu);
    std::snprintf(g_detail, sizeof g_detail, "%s", s);
}

void lower(uint32_t reason, const char* why) {
    set_detail(why);
    const KilnApi* api = items_api();
    const auto* caps = static_cast<const KilnCapsApi*>(api ? api->service(str(KILN_CAPS_SERVICE), KILN_CAPS_VERSION) : nullptr);
    if (caps && caps->report) caps->report(items_owner(), str(kFeature), KILN_CAP_UNAVAILABLE, reason, str(why));
}

void switch_off(uint32_t reason, const char* why) {
    if (!g_off.exchange(true)) {
        LOGE("gear writes are off until the server, or the hosting player's game, restarts: %s", why);
        lower(reason, why);
    }
}

bool guard(void (*fn)(void*), void* p, unsigned long* code) {
    __try {
        fn(p);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// ---- layouts by reflection (kiln.types) ----
bool field(const KilnTypesApi* t, const KilnType* type, const char* name, uint32_t* offset, uint32_t* size) {
    KilnTypeInfo ti{};
    ti.size = sizeof ti;
    if (!type || t->type_info(type, &ti) != KILN_OK) return false;
    const size_t n = std::strlen(name);
    for (uint32_t i = 0; i < ti.field_count; ++i) {
        KilnTypeField f{};
        if (t->type_field(type, i, &f) != KILN_OK || f.name.len != n || std::memcmp(f.name.ptr, name, n) != 0) continue;
        KilnTypeInfo fi{};
        fi.size = sizeof fi;
        if (t->type_info(f.type, &fi) != KILN_OK) return false;
        *offset = static_cast<uint32_t>(f.offset);
        *size = fi.type_size;
        return true;
    }
    return false;
}

int enum_value(const KilnTypesApi* t, const KilnType* type, const char* name) {
    KilnTypeInfo ti{};
    ti.size = sizeof ti;
    if (!type || t->type_info(type, &ti) != KILN_OK) return -1;
    const size_t n = std::strlen(name);
    for (uint32_t i = 0; i < ti.field_count; ++i) {
        KilnStr s{};
        int64_t v = 0;
        if (t->type_enum(type, i, &s, &v) == KILN_OK && s.len == n && std::memcmp(s.ptr, name, n) == 0) return v >= 0 && v < 16 ? int(v) : -1;
    }
    return -1;
}

// The fields gear reads, by this build's own reflection; the offsets kiln.items already uses must agree with it.
bool resolve_layout(char* why, size_t cap) {
    const auto* t = static_cast<const KilnTypesApi*>(items_api()->service(str(KILN_TYPES_SERVICE), 1));
    if (!t || t->header.size < sizeof(KilnTypesApi)) return std::snprintf(why, cap, "kiln.types is not loaded"), false;
    auto ty = [t](const char* n) { return t->type_by_name(str(n)); };
    uint32_t o = 0, s = 0;
    const KilnType* info = ty("keen::ItemInfo");
    if (!field(t, info, "rarity", &F.info_rarity, &s) || s != 1 || F.info_rarity != 0x17)
        return std::snprintf(why, cap, "this build's ItemInfo.rarity is not at 0x17 (1 byte)"), false;
    if (!field(t, info, "perkIds", &F.info_perk_ids, &s) || s != 4 * gear::kPerkSlots)
        return std::snprintf(why, cap, "this build's ItemInfo.perkIds is not 5 x 4 bytes"), false;
    if (!field(t, info, "itemLevelRange", &F.info_levels, &s) || s != 8 || F.info_levels != 0x674)
        return std::snprintf(why, cap, "this build's ItemInfo.itemLevelRange is not at 0x674 (8 bytes)"), false;
    const KilnType* state = ty("keen::ecs::ItemState");
    if (!field(t, state, "itemId", &F.state_item, &s) || s != 4 || !field(t, state, "itemState", &F.state_bits, &s) || s != 2 ||
        F.state_bits != 0x0E || F.state_item + 4 > kItemStateSize || !field(t, state, "itemRarityUi", &F.state_rarity, &s) || s != 1 ||
        !field(t, state, "itemLevelUi", &F.state_level, &s) || s != 1 || F.state_rarity >= kItemStateSize || F.state_level >= kItemStateSize)
        return std::snprintf(why, cap, "this build's ItemState is not what gear writes read (itemId, itemState at 0x0e, the UI bytes)"), false;
    const KilnType* perks = ty("keen::ecs::PerkContainerNew");
    uint32_t a = 0, u = 0, sa = 0, su = 0;
    if (!field(t, perks, "availablePerks", &a, &sa) || !field(t, perks, "unlockedPerks", &u, &su) || a != 0 || u != 1 ||
        sa != 1 || su != 1)
        return std::snprintf(why, cap, "this build's PerkContainerNew is not {u8 availablePerks, u8 unlockedPerks}"), false;
    const KilnType* flag = ty("keen::ItemStateFlag");
    const int init = enum_value(t, flag, "DurabilityInitialized"), equipped = enum_value(t, flag, "IsEquipped");
    if (init < 0 || equipped < 0)
        return std::snprintf(why, cap, "ItemStateFlag.DurabilityInitialized or IsEquipped is not in this build's reflection"), false;
    F.init_bit = static_cast<uint16_t>(1u << init);
    F.equipped_bit = static_cast<uint16_t>(1u << equipped);
    (void)o;
    return true;
}

bool layout_ok(char* why, size_t cap) {
    static char reason[160] = "";
    std::call_once(g_layout_once, [] { F.layout = resolve_layout(reason, sizeof reason); });
    if (!F.layout && why) std::snprintf(why, cap, "%s", reason);
    return F.layout;
}

// ---- reads (plain data, SEH-guarded by the caller) ----
// One component of an entity: the program's accessor (ReadOther) or, on the tick thread, GetComponent by hash.
struct Reader {
    const Access* a = nullptr;       // program
    uint64_t acc_dur = 0, acc_level = 0, acc_perks = 0;
    uintptr_t world = 0;             // tick thread
    void* attributes = nullptr;
    void* items = nullptr;
};
const uint8_t* comp(const Reader& r, int which, KilnEntity e, uint64_t min) {
    PtrSize ps{};
    if (r.a) {
        const uint64_t acc = which == 0 ? r.a->acc[A_ITEMSTATE] : which == 1 ? r.acc_perks : which == 2 ? r.acc_level : r.acc_dur;
        E.read_other(&ps, r.a->storage, acc, e);
    } else {
        const uint32_t h = which == 0 ? kHashItemState : which == 1 ? kHashPerks : which == 2 ? kHashLevel : kHashDurability;
        E.get_component(&ps, r.world, h, e);
    }
    return ps.ptr && ps.size >= min ? static_cast<const uint8_t*>(ps.ptr) : nullptr;
}

// An attribute's value index in its block (the game's own lookup, the group checked). -1: not there.
int attr_index(void* attributes, const uint8_t* block, uint32_t attr) {
    if (!block || !attr || !(block[6] & 1)) return -1;
    AttrRef ref{};
    E.attr_find(&ref, attr, attributes);
    if (ref.error || !ref.desc || rd<uint32_t>(ref.desc, 0) != rd<uint32_t>(block, 0)) return -1;
    const uint16_t index = rd<uint16_t>(ref.desc, 0xC);
    return index < block[7] ? index : -1;
}
uint32_t* attr_values(const uint8_t* block) {
    return reinterpret_cast<uint32_t*>(const_cast<uint8_t*>(block) + rd<uint16_t>(block, 4));
}

struct Blocks {
    const uint8_t* state = nullptr;
    const uint8_t* perks = nullptr;
    const uint8_t* level = nullptr;
    const uint8_t* dur = nullptr;
    int lv = -1, lv_max = -1, du = -1, du_max = -1;
};

// The gear of one item entity; false: no item entity (no ItemState) or its item is not in the registry.
bool read_state(const Reader& r, KilnEntity e, KilnItemsGearState* s, Blocks* b) {
    *s = KilnItemsGearState{};
    *b = Blocks{};
    b->state = comp(r, 0, e, kItemStateSize);
    if (!b->state) return false;
    s->item = rd<uint32_t>(b->state, F.state_item);
    s->state = rd<uint16_t>(b->state, F.state_bits);
    s->rarity_ui = b->state[F.state_rarity];
    s->level_ui = b->state[F.state_level];
    const auto* info = static_cast<const uint8_t*>(E.item_find(s->item, r.items));
    if (!info) return false;
    s->type_rarity = info[F.info_rarity];
    uint32_t ids[gear::kPerkSlots];
    std::memcpy(ids, info + F.info_perk_ids, sizeof ids);
    s->max_available = gear::max_available(ids);
    s->type_level_min = rd<uint32_t>(info, F.info_levels);
    s->type_level_max = rd<uint32_t>(info, F.info_levels + 4);
    if (s->state & F.equipped_bit) s->flags |= KILN_ITEMS_GEAR_EQUIPPED_NOW;
    if (s->state & F.init_bit) s->flags |= KILN_ITEMS_GEAR_DURABILITY_INIT;
    if ((b->perks = comp(r, 1, e, 2)) != nullptr) {
        s->flags |= KILN_ITEMS_GEAR_HAS_PERKS;
        s->available = b->perks[0];
        s->unlocked = b->perks[1];
    }
    if ((b->level = comp(r, 2, e, kAttrHeader)) != nullptr) {
        b->lv = attr_index(r.attributes, b->level, F.attr_level);
        b->lv_max = attr_index(r.attributes, b->level, F.attr_level_max);
        if (b->lv >= 0 && b->lv_max >= 0 && b->lv != b->lv_max) {
            s->flags |= KILN_ITEMS_GEAR_HAS_LEVEL;
            s->level = attr_values(b->level)[b->lv];
            s->level_max = attr_values(b->level)[b->lv_max];
        }
    }
    if ((b->dur = comp(r, 3, e, kAttrHeader)) != nullptr) {
        b->du = attr_index(r.attributes, b->dur, F.attr_dur);
        b->du_max = attr_index(r.attributes, b->dur, F.attr_dur_max);
        if (b->du >= 0 && b->du_max >= 0 && b->du != b->du_max) {
            s->flags |= KILN_ITEMS_GEAR_HAS_DURABILITY;
            s->durability = static_cast<int32_t>(attr_values(b->dur)[b->du]);
            s->durability_max = static_cast<int32_t>(attr_values(b->dur)[b->du_max]);
        }
    }
    s->rarity = gear::rarity_of((s->flags & KILN_ITEMS_GEAR_HAS_PERKS) != 0, s->available, s->type_rarity);
    return true;
}

// ---- one write inside the program ----
struct RunCall {
    const Reader* reader;
    const Req* q;
    KilnItemsGearResult* res;
};

void fail(KilnItemsGearResult* res, int32_t status, const char* why) {
    res->status = status;
    std::snprintf(res->message, sizeof res->message, "%s", why);
}

// The block WriteOther hands back must be the one read: the same component of the same entity, now marked.
bool marked(const Access& a, uint64_t acc, KilnEntity e, const uint8_t* read) {
    PtrSize w{};
    E.write_other(&w, a.storage, E.job_tracker(a.job), acc, e, E.job_write_flag(a.ctx));
    return w.ptr == read;
}

// Marks every component a plan writes through the tracked path; true when each is the block that was read.
// ItemState is marked whenever anything is written (bytes unchanged unless clear_init): it carries the replication bit,
// so players get the entity again this step, and once more when item_state_init derives the new UI values.
bool mark_plan(const Access& a, const Reader& rd_, KilnEntity e, const Blocks& b, const gear::Plan& plan) {
    const bool dur = plan.write_durability || plan.write_durability_max;
    const bool any = plan.write_perks || plan.write_level || dur || plan.clear_init;
    return !((plan.write_perks && !marked(a, rd_.acc_perks, e, b.perks)) ||
             (plan.write_level && !marked(a, rd_.acc_level, e, b.level)) ||
             (dur && !marked(a, rd_.acc_dur, e, b.dur)) ||
             (any && !marked(a, a.acc[A_ITEMSTATE], e, b.state)));
}

// The writes of a plan into the marked blocks.
void write_plan(const Reader& rd_, const Blocks& b, const gear::Plan& plan) {
    if (plan.write_perks) {
        auto* pk = const_cast<uint8_t*>(b.perks);
        pk[0] = plan.after.available;
        pk[1] = plan.after.unlocked;
    }
    if (plan.write_level) {
        uint32_t* v = attr_values(b.level);
        v[b.lv_max] = plan.after.level_max;  // max first, as UnlockPerk raises it before the level
        v[b.lv] = plan.after.level;
        F.attr_changed_u32(const_cast<uint8_t*>(b.level), rd_.attributes);
    }
    if (plan.write_durability || plan.write_durability_max) {
        // max first (package P7: DURABILITY_MAX), then current at its share; the attribute's formulas once after both
        if (plan.write_durability_max) attr_values(b.dur)[b.du_max] = static_cast<uint32_t>(plan.after.durability_max);
        if (plan.write_durability) attr_values(b.dur)[b.du] = static_cast<uint32_t>(plan.after.durability);
        E.attr_changed(const_cast<uint8_t*>(b.dur), rd_.attributes);
    }
    if (plan.clear_init) {
        auto* st = const_cast<uint8_t*>(b.state);
        const uint16_t bits = static_cast<uint16_t>(rd<uint16_t>(st, F.state_bits) & ~F.init_bit);
        std::memcpy(st + F.state_bits, &bits, sizeof bits);
    }
}

void run_impl(void* p) {
    auto* c = static_cast<RunCall*>(p);
    const Reader& rd_ = *c->reader;
    const Access& a = *rd_.a;
    const KilnItemsGearRequest& r = c->q->r;
    KilnItemsGearResult* res = c->res;
    Blocks b;
    if (!read_state(rd_, r.entity, &res->before, &b))
        return fail(res, KILN_ITEMS_GEAR_STALE, "the entity is no item entity now");
    res->after = res->before;
    if ((r.item && res->before.item != r.item) || stamp_for(r.entity, res->before.item) != r.stamp)
        return fail(res, KILN_ITEMS_GEAR_STALE, "the entity is not the stamped item entity any more");
    const gear::Plan plan = gear::plan(r, res->before, F.init_bit, F.equipped_bit);
    if (plan.status != KILN_ITEMS_GEAR_DONE) return fail(res, plan.status, plan.why);
    res->changed = plan.changed;
    if (r.flags & KILN_ITEMS_GEAR_DRY_RUN) {
        res->after = plan.after;
        res->status = KILN_ITEMS_GEAR_DONE;
        return;
    }
    // Mark every component first through the tracked path; write only when each is the block that was read.
    if (!mark_plan(a, rd_, r.entity, b, plan)) {
        res->changed = 0;
        return fail(res, KILN_ITEMS_GEAR_REFUSED, "the game's tracked write did not return the component that was read");
    }
    write_plan(rd_, b, plan);
    Blocks after;
    read_state(rd_, r.entity, &res->after, &after);  // read back: what the formulas left
    res->status = KILN_ITEMS_GEAR_DONE;
}


// ---- one upgrade inside the program (package P3: kiln_items.h "upgrades", upgrade_logic.h) ----
struct UpReq {
    uint64_t ticket;
    KilnOwner owner;
    KilnItemsUpgradeRequest r;
    uint64_t submitted;
};
struct UpCallback {
    KilnItemsUpgradeDoneFn fn;
    void* user;
    KilnOwner owner;
};
std::deque<UpReq> g_up_pending;                          // g_mu
std::vector<KilnItemsUpgradeResult> g_up_done;           // g_mu
std::unordered_map<uint64_t, UpCallback> g_up_callbacks; // g_mu
constexpr uint32_t kUpgradesPerStep = 8;  // each is an inventory transaction
uint8_t* g_tx = nullptr;                  // E.tx_size bytes, 16-aligned (the program is serial: one user)

// g_mu held: what waits for the program, writes and upgrades together.
void count_pending() {
    g_pending_n.store(static_cast<uint32_t>(g_pending.size() + g_up_pending.size()), std::memory_order_release);
}

void up_fail(KilnItemsUpgradeResult* res, int32_t status, const char* fmt, ...) {
    res->status = status;
    res->gear.status = status <= KILN_ITEMS_GEAR_REFUSED ? status : KILN_ITEMS_GEAR_REFUSED;
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(res->gear.message, sizeof res->gear.message, fmt, ap);
    va_end(ap);
}

struct UpCall {
    const Reader* reader;
    const UpReq* q;
    KilnItemsUpgradeResult* res;
    bool tx_open;    // a transaction of ours is open on g_tx (a fault: abandon it)
    bool paid;       // the payment committed
    bool invariant;  // paid, then the write could not be made: gear writes go off
};

PageSet g_pages;  // the program's own scratch (serial)

// Every payer's count of each cost item (plain stacks only: a stack with an item entity is never a cost).
bool count_costs(const Access& a, const KilnItemsUpgradeRequest& r, uint32_t (*have)[upgrade::kMaxPayers], uint64_t* total,
                 KilnItemsUpgradeResult* res) {
    for (uint32_t c = 0; c < r.cost_count; ++c) total[c] = 0;
    for (uint32_t p = 0; p < r.payer_count; ++p) {
        bool init = false;
        if (!read_pages(a, r.payers[p], &g_pages, &init) || !init)
            return up_fail(res, KILN_ITEMS_UPGRADE_BAD_PAYER, "payer %u has no initialized inventory", r.payers[p]), false;
        for (uint32_t c = 0; c < r.cost_count; ++c) {
            uint64_t n = 0;
            for (uint32_t i = 0; i < g_pages.n; ++i)
                for (const Stack& s : g_pages.p[i].s)
                    if (!is_empty(s) && pide_of(s) == 0 && s.item == r.costs[c].item) n += s.count;
            have[c][p] = n > 0xFFFFFFFFull ? 0xFFFFFFFFu : static_cast<uint32_t>(n);
            total[c] += n;
        }
    }
    return true;
}

void run_upgrade_impl(void* p) {
    auto* c = static_cast<UpCall*>(p);
    const Reader& rd_ = *c->reader;
    const Access& a = *rd_.a;
    const KilnItemsUpgradeRequest& r = c->q->r;
    KilnItemsUpgradeResult* res = c->res;
    KilnItemsGearResult& g = res->gear;
    const bool write = (r.flags & KILN_ITEMS_UPGRADE_WRITE) != 0, dry = (r.flags & KILN_ITEMS_UPGRADE_DRY_RUN) != 0;
    Blocks b;
    if (!read_state(rd_, r.gear.entity, &g.before, &b)) return up_fail(res, KILN_ITEMS_GEAR_STALE, "the entity is no item entity now");
    g.after = g.before;
    if ((r.gear.item && g.before.item != r.gear.item) || stamp_for(r.gear.entity, g.before.item) != r.gear.stamp)
        return up_fail(res, KILN_ITEMS_GEAR_STALE, "the entity is not the stamped item entity any more");
    if (!upgrade::as_expected(r, g.before))
        return up_fail(res, KILN_ITEMS_UPGRADE_CHANGED, "the item has %u enhancement slots now, not the %u the upgrade was priced for",
                       g.before.available, r.expect_available);
    gear::Plan plan{};
    plan.after = g.before;
    if (write) {
        plan = gear::plan(r.gear, g.before, F.init_bit, F.equipped_bit);
        if (plan.status != KILN_ITEMS_GEAR_DONE) return up_fail(res, plan.status, "%s", plan.why);
    }
    // the payers: the player, or containers (an initialized inventory that is no player's and no NPC's)
    for (uint32_t i = 0; i < r.payer_count; ++i) {
        if (r.payers[i] == r.player) continue;
        uint8_t fl = 0;
        if (!setup_flags(a, r.payers[i], &fl))
            return up_fail(res, KILN_ITEMS_UPGRADE_BAD_PAYER, "payer %u has no inventory (not loaded, or no container)", r.payers[i]);
        if (fl & 0x03) return up_fail(res, KILN_ITEMS_UPGRADE_BAD_PAYER, "payer %u is another player or an NPC, not a container", r.payers[i]);
    }
    // the cost items: in the registry, plain stacks (never an item with an entity of its own: it could be the item itself)
    for (uint32_t i = 0; i < r.cost_count; ++i) {
        const auto* info = static_cast<const uint8_t*>(E.item_find(r.costs[i].item, a.items));
        if (!info) return up_fail(res, KILN_ITEMS_GEAR_REFUSED, "cost item 0x%08x is not in the item registry", r.costs[i].item);
        if (rd<uint32_t>(info, E.item_flags) & 2u)
            return up_fail(res, KILN_ITEMS_GEAR_REFUSED, "cost item 0x%08x has an item entity of its own (gear, a gem): not a cost", r.costs[i].item);
    }
    uint32_t have[upgrade::kMaxCosts][upgrade::kMaxPayers] = {};
    uint64_t before[upgrade::kMaxCosts] = {}, after[upgrade::kMaxCosts] = {};
    upgrade::Split split[upgrade::kMaxCosts];
    if (r.cost_count) {
        if (!count_costs(a, r, have, before, res)) return;
        for (uint32_t i = 0; i < r.cost_count; ++i) {
            split[i] = upgrade::split(r.costs[i].count, have[i], r.payer_count);
            res->have[i] = split[i].have > 0xFFFFFFFFull ? 0xFFFFFFFFu : static_cast<uint32_t>(split[i].have);
        }
        for (uint32_t i = 0; i < r.cost_count; ++i)
            if (!split[i].enough)
                return up_fail(res, KILN_ITEMS_UPGRADE_CANT_PAY, "the payers hold %llu of the %u of item 0x%08x it costs",
                               static_cast<unsigned long long>(split[i].have), r.costs[i].count, r.costs[i].item);
    }
    if (dry) {
        for (uint32_t i = 0; i < r.cost_count; ++i) res->paid[i] = r.costs[i].count;
        g.after = plan.after;
        g.changed = plan.changed;
        res->status = KILN_ITEMS_GEAR_DONE;
        return;
    }
    // everything the write needs is checked before anything is paid
    if (write && !mark_plan(a, rd_, r.gear.entity, b, plan))
        return up_fail(res, KILN_ITEMS_GEAR_REFUSED, "the game's tracked write did not return the component that was read");
    if (r.cost_count) {
        tx_begin(a, r.player, g_tx);
        c->tx_open = true;
        for (uint32_t i = 0; i < r.cost_count; ++i)
            for (uint32_t k = 0; k < r.payer_count; ++k) {
                if (!split[i].take[k]) continue;
                Result12 out{};
                E.remove_items(&out, g_tx, r.payers[k], r.costs[i].item, 0, split[i].take[k]);
                if (out.error || out.value) {
                    E.rollback(g_tx);
                    E.free_arrays(g_tx);
                    c->tx_open = false;
                    return up_fail(res, KILN_ITEMS_GEAR_REFUSED, "the game refused taking item 0x%08x from %u (error 0x%02x, %u missing)",
                                   r.costs[i].item, r.payers[k], out.error, out.value);
                }
            }
        if (!tx_takes_plain_only(g_tx)) {
            E.rollback(g_tx);
            E.free_arrays(g_tx);
            c->tx_open = false;
            return up_fail(res, KILN_ITEMS_GEAR_REFUSED, "the game's transaction would have taken more than plain stacks: nothing taken");
        }
        E.commit(g_tx);
        E.free_arrays(g_tx);
        c->tx_open = false;
        uint32_t ignore[upgrade::kMaxCosts][upgrade::kMaxPayers] = {};
        if (!count_costs(a, r, ignore, after, res) || !upgrade::commit_stood(before, after, r.costs, r.cost_count))
            return up_fail(res, KILN_ITEMS_GEAR_REFUSED, "the game's commit did not take the cost (it rolled back by itself): nothing written");
        c->paid = true;
        for (uint32_t i = 0; i < r.cost_count; ++i) {
            res->paid[i] = r.costs[i].count;
            for (uint32_t k = 0; k < r.payer_count; ++k)
                if (r.payers[k] == r.player) res->paid_from_player += split[i].take[k];
        }
    }
    if (write) {
        // the commit changed only the payers' inventories; read the item again and mark what it hands back now
        Blocks now;
        KilnItemsGearState again{};
        if (!read_state(rd_, r.gear.entity, &again, &now) || !mark_plan(a, rd_, r.gear.entity, now, plan))
            return c->invariant = true, up_fail(res, KILN_ITEMS_GEAR_REFUSED, "paid, but the item could not be written after the payment");
        write_plan(rd_, now, plan);
        g.changed = plan.changed;
        Blocks back;
        read_state(rd_, r.gear.entity, &g.after, &back);  // read back: what the formulas left
    }
    res->status = KILN_ITEMS_GEAR_DONE;
}

// ---- the program ----
const char* const kSing[4] = {"BalancingRegistry", "g38_items::ItemRegistry", "impact::AttributeRegistry", "KnowledgeTriggerQueue"};
const char* const kEvents[2] = {"InventoryFull", "PickupEvent"};
const char* const kOwn[3] = {"Durability", "Level", "PerkContainerNew"};
int g_sing[4] = {-1, -1, -1, -1}, g_acc[A_COUNT], g_ev[2] = {-1, -1}, g_own[3] = {-1, -1, -1};
bool g_slots = false;
bool g_verified = false;

bool resolve_slots() {
    if (g_slots) return true;
    for (int i = 0; i < 4; ++i) g_sing[i] = g_w->program_slot(g_prog, KILN_SLOT_SINGLETON, str(kSing[i]));
    for (int i = 0; i < A_COUNT; ++i) g_acc[i] = g_w->program_slot(g_prog, KILN_SLOT_OTHER_WRITE, str(kAccNames[i]));
    for (int i = 0; i < 3; ++i) g_own[i] = g_w->program_slot(g_prog, KILN_SLOT_OTHER_WRITE, str(kOwn[i]));
    for (int i = 0; i < 2; ++i) g_ev[i] = g_w->program_slot(g_prog, KILN_SLOT_EVENT_OUT, str(kEvents[i]));
    auto good = [](int s) { return s >= 1 && s < 96; };
    bool ok = true;
    for (int s : g_sing) ok = ok && good(s);
    for (int s : g_acc) ok = ok && good(s);
    for (int s : g_own) ok = ok && good(s);
    for (int s : g_ev) ok = ok && good(s);
    return g_slots = ok;
}

struct OwnCheck {
    const Access* a;
    const uint64_t* own;
    bool ok;
    uint8_t replicated, saved;
    uint8_t bits[4];  // ItemState, Durability, Level, PerkContainerNew: bit 0 replicated, bit 1 saved
    char why[160];
};
// The three accessors of this program's own are the components they name. Then the marking's per-component bits
// (docs/re/items.md sections 12 and 19): the game sends players the rarity, level and damage it derives into ItemState
// (itemRarityUi, itemLevelUi, baseDamageUi; item_state_init's tracked write), so ItemState must be replicated, and the
// save must keep PerkContainerNew and Level, the state itself.
void own_check_impl(void* p) {
    auto* x = static_cast<OwnCheck*>(p);
    void* registry = *static_cast<void**>(x->a->storage);
    const uint32_t hashes[3] = {kHashDurability, kHashLevel, kHashPerks};
    uint16_t idx[4] = {};
    for (int i = 0; i < 3; ++i) {
        const uint16_t* want = E.component_map_find(static_cast<uint8_t*>(registry) + E.registry_components, &hashes[i]);
        if (!x->own[i] || !want || *reinterpret_cast<const uint16_t*>(x->own[i]) != *want) {
            std::snprintf(x->why, sizeof x->why, "the %s accessor of %s is not that component", kOwn[i], kProgram);
            return;
        }
        idx[i + 1] = *want;
    }
    idx[0] = *reinterpret_cast<const uint16_t*>(x->a->acc[A_ITEMSTATE]);
    auto bit = [registry](int64_t base, uint16_t type) {
        const uint64_t word = rd<uint64_t>(registry, base + (type >> 6) * 8);
        return ((word >> (type & 63)) & 1u) != 0;
    };
    for (int i = 0; i < 4; ++i) x->bits[i] = static_cast<uint8_t>((bit(F.replicated, idx[i]) ? 1 : 0) | (bit(F.saved, idx[i]) ? 2 : 0));
    x->replicated = (x->bits[0] & 1) != 0;
    x->saved = (x->bits[2] & 2) && (x->bits[3] & 2);
    if (!x->replicated || !x->saved) {
        std::snprintf(x->why, sizeof x->why, "this world does not %s: no player would keep a gear write",
                      !x->replicated ? "replicate ItemState" : "save PerkContainerNew and Level");
        return;
    }
    x->ok = true;
}

// Up to kUpgradesPerStep upgrades, each one transaction of ours (g_tx) around its write.
void run_upgrades(const Reader& reader, uint64_t step, unsigned long* code) {
    std::vector<UpReq> batch;
    {
        std::lock_guard lock(g_mu);
        while (batch.size() < kUpgradesPerStep && !g_up_pending.empty()) {
            batch.push_back(g_up_pending.front());
            g_up_pending.pop_front();
        }
        count_pending();
    }
    if (batch.empty()) return;
    std::vector<KilnItemsUpgradeResult> results(batch.size());
    for (size_t i = 0; i < batch.size(); ++i) {
        KilnItemsUpgradeResult& res = results[i];
        res = KilnItemsUpgradeResult{};
        res.size = sizeof res;
        res.ticket = batch[i].ticket;
        res.flags = batch[i].r.flags;
        res.cost_count = batch[i].r.cost_count;
        KilnItemsGearResult& g = res.gear;
        g.size = sizeof g;
        g.ticket = batch[i].ticket;
        g.entity = batch[i].r.gear.entity;
        g.stamp = batch[i].r.gear.stamp;
        g.flags = batch[i].r.gear.flags;
        g.step = step;
        g.steps = static_cast<uint32_t>(step - batch[i].submitted);
        if (g_off.load()) {
            up_fail(&res, KILN_ITEMS_GEAR_UNAVAILABLE, "gear writes were switched off (see kiln.log)");
            continue;
        }
        if (!g_tx) {
            up_fail(&res, KILN_ITEMS_GEAR_UNAVAILABLE, "no memory for the upgrades' transaction");
            continue;
        }
        UpCall call{&reader, &batch[i], &res, false, false, false};
        if (!guard(&run_upgrade_impl, &call, code)) {
            if (call.tx_open) abandon_tx(g_tx);
            char b[160];
            std::snprintf(b, sizeof b, "an upgrade faulted (0x%08lx) on entity %u%s", *code, g.entity,
                          call.paid ? " after its payment committed" : "");
            res.gear.changed = 0;
            up_fail(&res, KILN_ITEMS_GEAR_UNAVAILABLE, "%s", b);
            switch_off(KILN_CAP_REASON_FAULTED, b);
            continue;
        }
        if (call.invariant) {
            LOGE("upgrade ticket %llu: %s (entity %u)", static_cast<unsigned long long>(res.ticket), res.gear.message, g.entity);
            switch_off(KILN_CAP_REASON_FAULTED, "an upgrade was paid but its item could not be written (see kiln.log)");
        }
        if (res.status == KILN_ITEMS_GEAR_DONE && g.changed && !(res.flags & KILN_ITEMS_UPGRADE_DRY_RUN))
            g_writes.fetch_add(1, std::memory_order_relaxed);
    }
    std::lock_guard lock(g_mu);
    for (auto& r : results) g_up_done.push_back(r);
}

// ---- kiln.item_costs (since 0.2.0-dev.32, kiln_item_costs.h, costs.h): one exact cost per job, in this program ----
constexpr size_t kCostsPerStep = 8;  // a stack cost is one inventory transaction

struct CostCall {
    const Reader* reader;
    const costs::Job* job;
    KilnCostResult* res;
    bool committing;  // the game's commit or WearDelta ran (a fault from here on is UNCERTAIN)
};

void cost_refuse(KilnCostResult* r, uint32_t refusal, const char* fmt, ...) {
    r->state = KILN_COST_REFUSED;
    r->refusal = refusal;
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(r->reason, sizeof r->reason, fmt, ap);
    va_end(ap);
}

// The stack in `slot`, when that slot is one of `holder`'s (its inventory tree, Virtual links expanded).
bool holder_stack(const Access& a, KilnEntity holder, uint64_t slot, Stack* out) {
    bool init = false;
    if (!read_pages(a, holder, &g_pages, &init) || !init) return false;
    const uint32_t inv = static_cast<uint32_t>(slot & 0xFFFFFFFFu), index = static_cast<uint32_t>((slot >> 32) & 0xFFu);
    for (uint32_t i = 0; i < g_pages.n; ++i)
        if (g_pages.p[i].entity == inv && index < kSlotsPerPage) {
            *out = g_pages.p[i].s[index];
            return true;
        }
    return false;
}

void cost_stack(CostCall* c) {
    const Access& a = *c->reader->a;
    const KilnSelectedCost& k = c->job->cost;
    KilnCostResult* r = c->res;
    const auto* info = static_cast<const uint8_t*>(E.item_find(k.item, a.items));
    if (!info) return cost_refuse(r, KILN_COST_STALE_SELECTION, "item 0x%08x is not in the item registry", k.item);
    if (rd<uint32_t>(info, E.item_flags) & 2u)
        return cost_refuse(r, KILN_COST_NOT_A_STACK, "item 0x%08x has an item entity of its own (gear, a gem): not a stack", k.item);
    Job j{};
    j.r.size = sizeof j.r;
    j.r.entity = k.holder;
    j.r.item = k.item;
    j.r.count = k.amount;
    KilnItemsResult res{};
    res.size = sizeof res;
    unsigned long code = 0;
    if (k.kind == KILN_COST_STACK_TAKE) {
        Stack before{};
        if (!holder_stack(a, k.holder, k.slot, &before))
            return cost_refuse(r, KILN_COST_STALE_SELECTION, "the slot is not one of entity %u's inventory slots now", k.holder);
        if (before.item != k.item || pide_of(before) != 0 || before.count != k.expected_count)
            return cost_refuse(r, KILN_COST_STALE_SELECTION, "the slot holds %u of item 0x%08x%s now, not the %u selected", before.count,
                               before.item, pide_of(before) ? " (an item entity)" : "", k.expected_count);
        j.take = true;
        j.r.flags = KILN_ITEMS_EXACT_SLOT;
        j.r.slot = k.slot;
        c->committing = true;  // run_job commits inside; a fault there may have taken the cost
        const bool ran = run_job(a, j, g_tx, &res, &code);
        if (!ran) abandon_tx(g_tx);
        Stack after{};
        const bool read = holder_stack(a, k.holder, k.slot, &after);
        const uint32_t left = read && after.item == k.item ? after.count : 0;
        const uint32_t taken = read && left <= before.count ? before.count - left : 0;
        r->game_error = res.game_error;
        if (!ran) {
            r->state = KILN_COST_UNCERTAIN;
            r->quantity_taken = taken;
            r->disposition = taken == k.amount ? KILN_COST_EXACT : KILN_COST_PARTIAL;
            std::snprintf(r->reason, sizeof r->reason, "the game's take faulted (0x%08lx); the slot holds %u now (%u before)", code, left, before.count);
            return;
        }
        if (res.status != KILN_ITEMS_DONE) {
            if (taken != 0) {  // refused, yet the slot changed: say so, never call it nothing
                r->state = KILN_COST_UNCERTAIN;
                r->quantity_taken = taken;
                r->disposition = KILN_COST_PARTIAL;
                std::snprintf(r->reason, sizeof r->reason, "the take ended %d (%s) but the slot holds %u, not %u", res.status, res.message, left, before.count);
                return;
            }
            const uint32_t refusal = res.status == KILN_ITEMS_SLOT_CHANGED || res.status == KILN_ITEMS_NOT_ENOUGH ? KILN_COST_STALE_SELECTION
                                     : res.status == KILN_ITEMS_REFUSED                                       ? KILN_COST_GAME_REFUSED
                                                                                                              : KILN_COST_NOT_RUN;
            return cost_refuse(r, refusal, "%s", res.message[0] ? res.message : "the game's transaction refused the take");
        }
        r->quantity_taken = taken;
        if (taken == k.amount) {
            r->state = KILN_COST_COMMITTED;
            r->disposition = KILN_COST_EXACT;
        } else if (taken == 0) {
            r->state = KILN_COST_UNCERTAIN;  // the commit said done, the slot did not change: never an unverified success
            std::snprintf(r->reason, sizeof r->reason, "the take reported done but the slot still holds %u", left);
        } else {
            r->state = KILN_COST_COMMITTED;
            r->disposition = KILN_COST_PARTIAL;
            std::snprintf(r->reason, sizeof r->reason, "the slot went from %u to %u, not by %u", before.count, left, k.amount);
        }
        return;
    }
    // a refund: given back to the holder, the cost's slot first; merging into stacks is fine (stacks are fungible)
    j.r.slot = k.slot;
    c->committing = true;
    if (!run_job(a, j, g_tx, &res, &code)) {
        abandon_tx(g_tx);
        r->state = KILN_COST_UNCERTAIN;
        std::snprintf(r->reason, sizeof r->reason, "the game's give faulted (0x%08lx)", code);
        return;
    }
    r->game_error = res.game_error;
    if (res.change_count) r->slot = res.changes[0].slot;
    if (res.status == KILN_ITEMS_DONE || res.status == KILN_ITEMS_PARTIAL) {
        r->state = KILN_COST_COMMITTED;
        r->quantity_taken = res.done;
        r->disposition = res.done == k.amount ? KILN_COST_EXACT : KILN_COST_PARTIAL;
        if (r->disposition == KILN_COST_PARTIAL) std::snprintf(r->reason, sizeof r->reason, "only %u of %u fit", res.done, k.amount);
        return;
    }
    cost_refuse(r, res.status == KILN_ITEMS_NO_ROOM ? KILN_COST_NO_ROOM : KILN_COST_GAME_REFUSED, "%s",
                res.message[0] ? res.message : "the game's transaction refused the give");
}

void cost_gear(CostCall* c) {
    const Reader& rd_ = *c->reader;
    const Access& a = *rd_.a;
    const KilnSelectedCost& k = c->job->cost;
    KilnCostResult* r = c->res;
    const wear::Facts& wf = wear::facts();
    KilnItemsGearState s{};
    Blocks b;
    if (!read_state(rd_, k.item_entity, &s, &b)) return cost_refuse(r, KILN_COST_STALE_SELECTION, "entity %u is no item entity now", k.item_entity);
    if (s.item != k.item || stamp_for(k.item_entity, s.item) != k.item_stamp)
        return cost_refuse(r, KILN_COST_STALE_SELECTION, "entity %u is not the stamped item entity any more", k.item_entity);
    const uint64_t at = rd<uint64_t>(b.state, 0);  // ItemState.containedInSlotId
    Stack st{};
    if (!holder_stack(a, k.holder, at, &st) || pide_of(st) != k.item_entity)
        return cost_refuse(r, KILN_COST_STALE_SELECTION, "the item is not in entity %u's inventories now", k.holder);
    if (k.kind == KILN_COST_GEAR_WEAR && at != k.slot)
        return cost_refuse(r, KILN_COST_STALE_SELECTION, "the item moved out of the selected slot");
    if (!(s.flags & KILN_ITEMS_GEAR_HAS_DURABILITY) || !b.dur || b.du < 0)
        return cost_refuse(r, KILN_COST_NOT_GEAR, "item 0x%08x has no durability", s.item);
    r->durability_before = r->durability_after = s.durability;
    const bool enabled = wf.enable_durability(a.balancing);
    const costs::WearPlan p = k.kind == KILN_COST_GEAR_WEAR ? costs::plan_wear(s.durability, s.durability_max, k.amount, k.allow_break != 0, enabled)
                                                            : costs::plan_restore(s.durability, s.durability_max, k.amount);
    if (p.refusal != KILN_COST_REFUSAL_NONE) return cost_refuse(r, p.refusal, "%s", p.why);
    if (p.waived || p.delta == 0) {
        r->state = KILN_COST_COMMITTED;
        r->disposition = costs::gear_disposition(p, 0);
        return;
    }
    // the game's own path: the tracked Durability write (as WearItem gets it), then WearDelta with no skills (no
    // skill-saving roll: an authored cost is exact). The notification of a break needs a NotifyEvent output this program
    // does not have, so a wear that breaks the tool names no actor (the game then sends no break notice).
    if (!marked(a, rd_.acc_dur, k.item_entity, b.dur))
        return cost_refuse(r, KILN_COST_GAME_REFUSED, "the game's tracked write did not return the Durability that was read");
    wear::WearEnv env{};
    env.durability_index = static_cast<uint16_t>(b.du);
    env.ctx = a.ctx;
    env.attributes = a.attributes;
    c->committing = true;
    wf.wear_delta(const_cast<uint8_t*>(b.dur), nullptr, p.delta, p.crosses_zero ? 0u : k.holder, &env);
    KilnItemsGearState now{};
    Blocks back;
    if (!read_state(rd_, k.item_entity, &now, &back) || !(now.flags & KILN_ITEMS_GEAR_HAS_DURABILITY)) {
        r->state = KILN_COST_UNCERTAIN;
        std::snprintf(r->reason, sizeof r->reason, "the item could not be read again after WearDelta");
        return;
    }
    r->durability_after = now.durability;
    r->wear_applied = k.kind == KILN_COST_GEAR_WEAR ? s.durability - now.durability : now.durability - s.durability;
    r->state = KILN_COST_COMMITTED;
    r->disposition = costs::gear_disposition(p, r->wear_applied);
}

void cost_impl(void* p) {
    auto* c = static_cast<CostCall*>(p);
    if (costs::is_stack(c->job->cost.kind)) cost_stack(c);
    else cost_gear(c);
}

void run_costs(const Reader& reader, uint64_t step) {
    if (!costs::pending()) return;
    const std::vector<costs::Job> jobs = costs::take(kCostsPerStep);
    for (const costs::Job& j : jobs) {
        KilnCostResult res = costs::blank_result(j.ticket, j.cost);
        res.step = step;
        char why[160] = "";
        if (g_off.load() || (costs::is_stack(j.cost.kind) ? !gear_costs_stacks(why) : !gear_costs_wear(why))) {
            cost_refuse(&res, KILN_COST_NOT_RUN, "%s", why[0] ? why : "gear writes were switched off (see kiln.log)");
            costs::settle(res);
            continue;
        }
        CostCall call{&reader, &j, &res, false};
        unsigned long code = 0;
        if (!guard(&cost_impl, &call, &code)) {
            // run_job rolls its own transaction back; a fault anywhere else never had one open
            res.state = call.committing ? KILN_COST_UNCERTAIN : KILN_COST_REFUSED;
            res.refusal = call.committing ? KILN_COST_REFUSAL_NONE : KILN_COST_NOT_RUN;
            char b[160];
            std::snprintf(b, sizeof b, "an item cost faulted (0x%08lx) on entity %u%s", code, j.cost.holder,
                          call.committing ? " while the game changed the item: what changed is not known" : "");
            std::snprintf(res.reason, sizeof res.reason, "%s", b);
            switch_off(KILN_CAP_REASON_FAULTED, b);
        }
        costs::settle(res);
    }
}

void KILN_CALL each(KilnProgramCtx* ctx, uint64_t* it, KilnEntity, void*) {
    g_last_update.store(current_step(), std::memory_order_relaxed);
    if (g_off.load() || (g_pending_n.load(std::memory_order_acquire) == 0 && !costs::pending())) return;
    if (!resolve_slots()) return switch_off(KILN_CAP_REASON_FAULTED, "kiln.world did not report kiln_items_gear's iterator slots");
    Reader reader{};
    Access a{};
    bool ok = false;
    char why[160] = "";
    unsigned long code = 0;
    if (!verify_access(it, g_sing, g_acc, g_ev, ctx, &a, &ok, why, &code) || !ok)
        return switch_off(KILN_CAP_REASON_FAULTED, why[0] ? why : "checking kiln_items_gear's iterator faulted");
    reader.a = &a;
    reader.attributes = a.attributes;
    reader.items = a.items;
    const uint64_t own[3] = {it[g_own[0]], it[g_own[1]], it[g_own[2]]};
    reader.acc_dur = own[0];
    reader.acc_level = own[1];
    reader.acc_perks = own[2];
    if (!g_verified) {
        OwnCheck x{&a, own, false, 0, 0, {0, 0, 0, 0}, ""};
        if (!guard(&own_check_impl, &x, &code)) return switch_off(KILN_CAP_REASON_FAULTED, "checking kiln_items_gear's accessors faulted");
        g_replicated.store(x.replicated);
        g_saved.store(x.saved);
        LOGI("%s: the marking's bits (replicated, saved): ItemState %u %u, Durability %u %u, Level %u %u, PerkContainerNew %u %u", kProgram,
             x.bits[0] & 1, x.bits[0] >> 1, x.bits[1] & 1, x.bits[1] >> 1, x.bits[2] & 1, x.bits[2] >> 1, x.bits[3] & 1, x.bits[3] >> 1);
        if (!x.ok) return switch_off(KILN_CAP_REASON_SYMBOL_MISSING, x.why);
        g_verified = true;
        g_state.store(KILN_ITEMS_PROGRAM_RUNNING);
        set_detail("running");
        LOGI("program %s runs: accessors verified; ItemState is replicated, PerkContainerNew and Level are saved", kProgram);
    }
    std::vector<Req> batch;
    {
        std::lock_guard lock(g_mu);
        while (batch.size() < kPerStep && !g_pending.empty()) {
            batch.push_back(g_pending.front());
            g_pending.pop_front();
        }
        count_pending();
    }
    std::vector<KilnItemsGearResult> results(batch.size());
    const uint64_t step = current_step();
    for (size_t i = 0; i < batch.size(); ++i) {
        KilnItemsGearResult& res = results[i];
        res = KilnItemsGearResult{};
        res.size = sizeof res;
        res.ticket = batch[i].ticket;
        res.entity = batch[i].r.entity;
        res.stamp = batch[i].r.stamp;
        res.flags = batch[i].r.flags;
        res.step = step;
        res.steps = static_cast<uint32_t>(step - batch[i].submitted);
        if (g_off.load()) {
            fail(&res, KILN_ITEMS_GEAR_UNAVAILABLE, "gear writes were switched off (see kiln.log)");
            continue;
        }
        RunCall call{&reader, &batch[i], &res};
        if (!guard(&run_impl, &call, &code)) {
            res.changed = 0;
            char b[160];
            std::snprintf(b, sizeof b, "a gear write faulted (0x%08lx) on entity %u", code, res.entity);
            fail(&res, KILN_ITEMS_GEAR_UNAVAILABLE, b);
            switch_off(KILN_CAP_REASON_FAULTED, b);
            continue;
        }
        if (res.status == KILN_ITEMS_GEAR_DONE && res.changed && !(res.flags & KILN_ITEMS_GEAR_DRY_RUN))
            g_writes.fetch_add(1, std::memory_order_relaxed);
    }
    {
        std::lock_guard lock(g_mu);
        for (auto& r : results) g_done.push_back(r);
    }
    run_upgrades(reader, step, &code);
    run_costs(reader, step);
}

bool guarded_done(KilnItemsGearDoneFn fn, const KilnItemsGearResult* r, void* user) {
    __try {
        fn(r, user);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

KilnItemsGearResult dropped(const Req& q, int32_t status, const char* why) {
    KilnItemsGearResult r{};
    r.size = sizeof r;
    r.ticket = q.ticket;
    r.entity = q.r.entity;
    r.stamp = q.r.stamp;
    r.flags = q.r.flags;
    r.steps = static_cast<uint32_t>(current_step() - q.submitted);
    fail(&r, status, why);
    return r;
}

void poll_program() {
    if (!g_w || g_state.load() != KILN_ITEMS_PROGRAM_PENDING) return;
    KilnWorldProgramInfo pi{};
    pi.size = sizeof pi;
    if (g_w->program_info(g_prog, &pi) != KILN_OK) return;
    if (pi.state == KILN_PROGRAM_ACTIVE) {
        int expected = KILN_ITEMS_PROGRAM_PENDING;
        g_state.compare_exchange_strong(expected, KILN_ITEMS_PROGRAM_ACTIVE);
    } else if (pi.state == KILN_PROGRAM_REFUSED || pi.state == KILN_PROGRAM_FAULTED || pi.state == KILN_PROGRAM_SKIPPED) {
        g_state.store(KILN_ITEMS_PROGRAM_NONE);
        char b[160];
        std::snprintf(b, sizeof b, "kiln.world reports %s as state %u (see kiln.log)", kProgram, pi.state);
        switch_off(KILN_CAP_REASON_FAULTED, b);
    }
}

bool usable() {
    const int s = g_state.load();
    return F.sym && !g_off.load() && g_w && (s == KILN_ITEMS_PROGRAM_PENDING || s == KILN_ITEMS_PROGRAM_ACTIVE ||
                                             s == KILN_ITEMS_PROGRAM_RUNNING);
}

}  // namespace


// ============================================================================================= lab self-test ===
// Labs only: on when <kiln data>/kiln.items/gear-selftest exists at boot (a world with gear in its chests: a copied
// save). Result lines "gear selftest: PASS|FAIL|SKIP ...". It picks an item entity in a container whose type has a
// perk slot above its rarity, then: a dry run; rarity + 1 with a durability refresh; what the game derives in the next
// steps (itemRarityUi, DurabilityInitialized set again, max durability at the new rarity with the ratio kept); two
// unlocks (Level follows); lowering to Common (unlocked and Level come down); a stale stamp; NO_SLOT on a type without
// the next slot; then a final state written to <data>/kiln.items/gear-selftest.saved and a save requested. The next
// boot of the same lab reads that file and checks the item kept the state through the save and the load.
namespace {
namespace st {
enum Phase : int { Off, Wait, Pick, Dry, Write, Derive, Unlock, UnlockUi, Lower, LowerUi, Stale, NoSlot, Final, Persist, Done, Verify };
struct State {
    int phase = Off;
    uint64_t until = 0;
    bool verify = false;
    std::filesystem::path dir;
    KilnEntity holder = 0, e = 0, noslot = 0;
    uint32_t flat = 0, stamp = 0, noslot_stamp = 0;
    KilnItemsGearState before{}, final_{};
    bool waiting = false, have = false;
    KilnItemsGearResult res{};
    int fails = 0;
} T;

const KilnItemsApi* table() {
    const auto* t = static_cast<const KilnItemsApi*>(items_api()->service(str(KILN_ITEMS_SERVICE), KILN_ITEMS_VERSION));
    return t && t->header.size >= sizeof(KilnItemsApi) ? t : nullptr;
}

void line(bool ok, const char* fmt, ...) {
    char b[400];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(b, sizeof b, fmt, ap);
    va_end(ap);
    T.fails += !ok;
    LOGI("gear selftest: %s %s", ok ? "PASS" : "FAIL", b);
}

void KILN_CALL on_result(const KilnItemsGearResult* r, void*) {
    T.res = *r;
    T.have = true;
    T.waiting = false;
}

bool submit(uint32_t flags, uint8_t available, uint8_t unlocked, uint32_t stamp = 0, KilnEntity e = 0) {
    KilnItemsGearRequest r{};
    r.size = sizeof r;
    r.flags = flags;
    r.entity = e ? e : T.e;
    r.stamp = stamp ? stamp : T.stamp;
    if (flags & KILN_ITEMS_GEAR_AVAILABLE) r.available = available;
    if (flags & KILN_ITEMS_GEAR_UNLOCKED) r.unlocked = unlocked;
    T.have = false;
    T.waiting = true;
    const KilnResult rc = gear_write(0x6ea75e1fu, &r, &on_result, nullptr, nullptr);
    if (rc != KILN_OK) {
        T.waiting = false;
        line(false, "gear_write refused the request at once (%d)", rc);
        T.phase = Done;
        return false;
    }
    return true;
}

KilnItemsGearState now(KilnEntity e = 0, uint32_t stamp = 0) {
    KilnItemsGearState s{};
    gear_read(e ? e : T.e, stamp ? stamp : T.stamp, &s);
    return s;
}

// Every item entity of every container: fn(holder, instance); stops when fn returns true.
template <typename Fn>
void each_item(Fn&& fn) {
    const KilnItemsApi* t = table();
    if (!t) return;
    std::vector<KilnEntity> ids(1024);
    uint32_t n = 0;
    t->find_inventories(KILN_ITEMS_FIND_CONTAINERS | KILN_ITEMS_FIND_NONEMPTY, ids.data(), static_cast<uint32_t>(ids.size()), &n);
    n = std::min<uint32_t>(n, static_cast<uint32_t>(ids.size()));
    std::vector<KilnItemsInstance> items(256);
    for (uint32_t i = 0; i < n; ++i) {
        uint32_t count = 0;
        t->read_holder(ids[i], items.data(), static_cast<uint32_t>(items.size()), sizeof(KilnItemsInstance), &count, nullptr);
        count = std::min<uint32_t>(count, static_cast<uint32_t>(items.size()));
        for (uint32_t k = 0; k < count; ++k)
            if (fn(ids[i], items[k])) return;
    }
}

void pick() {
    bool found = false;
    uint32_t seen = 0, gear_n = 0, holders = 0;
    KilnEntity last_holder = 0;
    each_item([&](KilnEntity holder, const KilnItemsInstance& it) {
        if (holder != last_holder) ++holders, last_holder = holder;
        ++seen;
        if (it.flags & KILN_ITEMS_INSTANCE_PERKS) ++gear_n;
        const uint32_t need = KILN_ITEMS_INSTANCE_FOUND | KILN_ITEMS_INSTANCE_PLACED | KILN_ITEMS_INSTANCE_PERKS | KILN_ITEMS_INSTANCE_LEVEL;
        if ((it.flags & need) != need || (it.flags & KILN_ITEMS_INSTANCE_EQUIPPED) || !it.stamp) return false;
        KilnItemsGearState s = now(it.entity, it.stamp);
        if (!(s.flags & KILN_ITEMS_GEAR_HAS_PERKS)) return false;
        if (!T.noslot && s.max_available == s.available && s.available < gear::kPerkSlots) {
            T.noslot = it.entity;
            T.noslot_stamp = it.stamp;
        }
        if (!found && s.available >= 1 && s.available < gear::kPerkSlots && s.max_available > s.available && s.unlocked == 0 &&
            (s.flags & KILN_ITEMS_GEAR_HAS_DURABILITY) && s.durability_max > 0 && s.level + 2 <= gear::kMaxLevel) {
            found = true;
            T.holder = holder;
            T.flat = it.flat_slot;
            T.e = it.entity;
            T.stamp = it.stamp;
            T.before = s;
        }
        return found && T.noslot != 0;
    });
    if (!found) {
        line(false, "no item entity with a free perk slot above its rarity in any container of this world (%u containers with item "
                    "entities, %u item entities, %u with perks)", holders, seen, gear_n);
        T.phase = Done;
        return;
    }
    LOGI("gear selftest: picked entity %u (stamp %u, item 0x%08x) in holder %u slot %u: perks %u/%u (type slots %u), rarity %u "
         "(ui %u), level %u/%u (ui %u, range %u..%u), durability %d/%d, state 0x%04x; NO_SLOT candidate %u",
         T.e, T.stamp, T.before.item, T.holder, T.flat, T.before.unlocked, T.before.available, T.before.max_available, T.before.rarity,
         T.before.rarity_ui, T.before.level, T.before.level_max, T.before.level_ui, T.before.type_level_min, T.before.type_level_max,
         T.before.durability, T.before.durability_max, T.before.state, T.noslot);
    T.phase = Dry;
}

double ratio(const KilnItemsGearState& s) { return s.durability_max > 0 ? double(s.durability) / s.durability_max : 0; }

void write_marker() {
    std::ofstream f(T.dir / "gear-selftest.saved", std::ios::trunc);
    f << T.holder << ' ' << T.flat << ' ' << T.final_.item << ' ' << unsigned(T.final_.available) << ' ' << unsigned(T.final_.unlocked)
      << ' ' << T.final_.level << '\n';
}

void verify() {
    std::ifstream f(T.dir / "gear-selftest.saved");
    unsigned long long holder = 0, flat = 0, item = 0, avail = 0, unl = 0, level = 0;
    if (!(f >> holder >> flat >> item >> avail >> unl >> level)) {
        line(false, "after the restart: gear-selftest.saved cannot be read");
        return;
    }
    bool in_holder = false, kept = false;
    KilnItemsGearState seen{};
    each_item([&](KilnEntity h, const KilnItemsInstance& it) {
        if (it.item != item || it.flat_slot != flat) return false;
        const KilnItemsGearState s = now(it.entity, it.stamp);
        const bool same = s.available == avail && s.unlocked == unl && s.level == level;
        if (h == holder) {
            in_holder = true;
            seen = s;
            kept = same;
            return true;
        }
        if (same && !kept) {  // the holder's id changed with the load: the same item at the same slot elsewhere
            kept = true;
            seen = s;
        }
        return false;
    });
    line(kept, "after the save and a restart: item 0x%08llx at slot %llu (holder %llu %s) has perks %u/%u level %u (rarity ui %u); "
               "expected %llu/%llu level %llu",
         item, flat, holder, in_holder ? "found" : "not found by id", seen.unlocked, seen.available, seen.level, seen.rarity_ui, unl, avail,
         level);
    line(!kept || seen.rarity_ui == seen.available - 1, "after the restart the game derives the saved rarity (ui %u, %u slots)", seen.rarity_ui,
         seen.available);
    std::error_code ec;
    std::filesystem::rename(T.dir / "gear-selftest.saved", T.dir / "gear-selftest.verified", ec);
}

void step(uint64_t s) {
    if (T.phase == Off || T.phase == Done || T.waiting) return;
    if (T.until && s < T.until) return;
    T.until = 0;
    const KilnItemsGearState& B = T.before;
    switch (T.phase) {
    case Wait: {
        const int p = g_state.load();
        if (p != KILN_ITEMS_PROGRAM_ACTIVE && p != KILN_ITEMS_PROGRAM_RUNNING) return;
        if (!instances_on()) {
            line(false, "item instances are off: no stamps");
            T.phase = Done;
            return;
        }
        T.phase = T.verify ? Verify : Pick;
        T.until = s + 600;  // let the world settle (~25 s)
        return;
    }
    case Verify:
        verify();
        T.phase = Done;
        LOGI("gear selftest: done (%d failure(s))", T.fails);
        return;
    case Pick:
        pick();
        return;
    case Dry:
        if (!T.have) {
            submit(KILN_ITEMS_GEAR_AVAILABLE | KILN_ITEMS_GEAR_DURABILITY_REFRESH | KILN_ITEMS_GEAR_DRY_RUN, B.available + 1, 0);
            return;
        }
        {
            const KilnItemsGearState n = now();
            line(T.res.status == KILN_ITEMS_GEAR_DONE && T.res.after.available == B.available + 1 &&
                     (T.res.changed & KILN_ITEMS_GEAR_CHANGED_PERKS) && n.available == B.available && n.state == B.state,
                 "dry run: plans %u slots (status %d, changed 0x%x), the item still has %u", T.res.after.available, T.res.status,
                 T.res.changed, n.available);
        }
        T.have = false;
        T.phase = Write;
        return;
    case Write:
        if (!T.have) {
            submit(KILN_ITEMS_GEAR_AVAILABLE | KILN_ITEMS_GEAR_DURABILITY_REFRESH, B.available + 1, 0);
            return;
        }
        line(T.res.status == KILN_ITEMS_GEAR_DONE && T.res.after.available == B.available + 1 && T.res.after.unlocked == 0 &&
                 !(T.res.after.state & F.init_bit) && T.res.after.level == B.level,
             "rarity %u -> %u: PerkContainerNew %u/%u -> %u/%u, DurabilityInitialized cleared (state 0x%04x -> 0x%04x), level %u (status %d%s%s)",
             B.rarity, T.res.after.rarity, B.unlocked, B.available, T.res.after.unlocked, T.res.after.available, B.state, T.res.after.state,
             T.res.after.level, T.res.status, T.res.message[0] ? ": " : "", T.res.message);
        T.have = false;
        T.phase = Derive;
        T.until = s + 10;
        return;
    case Derive: {
        const KilnItemsGearState n = now();
        line(n.rarity_ui == B.available, "the game derived itemRarityUi %u (was %u) within 10 steps", n.rarity_ui, B.rarity_ui);
        line((n.state & F.init_bit) != 0, "item_durability_init ran again: DurabilityInitialized is set (state 0x%04x)", n.state);
        line(n.durability_max != B.durability_max && std::abs(ratio(n) - ratio(B)) <= 0.01 + 1.0 / n.durability_max,
             "max durability recomputed at the new rarity: %d/%d -> %d/%d (ratio %.3f -> %.3f)", B.durability, B.durability_max,
             n.durability, n.durability_max, ratio(B), ratio(n));
        T.phase = Unlock;
        return;
    }
    case Unlock:
        if (!T.have) {
            submit(KILN_ITEMS_GEAR_UNLOCKED, 0, 2);
            return;
        }
        line(T.res.status == KILN_ITEMS_GEAR_DONE && T.res.after.unlocked == 2 && T.res.after.level == B.level + 2 &&
                 T.res.after.level_max >= T.res.after.level,
             "two unlocks: Level %u -> %u (max %u -> %u)", B.level, T.res.after.level, B.level_max, T.res.after.level_max);
        T.have = false;
        T.phase = UnlockUi;
        T.until = s + 10;
        return;
    case UnlockUi: {
        const KilnItemsGearState n = now();
        line(n.level_ui == ((B.level + 2) & 0xFF), "the game derived itemLevelUi %u (was %u)", n.level_ui, B.level_ui);
        T.phase = Lower;
        return;
    }
    case Lower:
        if (!T.have) {
            submit(KILN_ITEMS_GEAR_AVAILABLE | KILN_ITEMS_GEAR_DURABILITY_REFRESH, 1, 0);
            return;
        }
        line(T.res.status == KILN_ITEMS_GEAR_DONE && T.res.after.available == 1 && T.res.after.unlocked == 1 && T.res.after.level == B.level + 1 &&
                 T.res.after.rarity == 0,
             "to Common with two unlocked: perks %u/%u, level %u (unlocked and Level came down)", T.res.after.unlocked,
             T.res.after.available, T.res.after.level);
        T.have = false;
        T.phase = LowerUi;
        T.until = s + 10;
        return;
    case LowerUi: {
        const KilnItemsGearState n = now();
        line(n.rarity_ui == 0 && n.level_ui == ((B.level + 1) & 0xFF), "the game derived Common (ui %u) and level %u", n.rarity_ui, n.level_ui);
        T.phase = Stale;
        return;
    }
    case Stale:
        if (!T.have) {
            submit(KILN_ITEMS_GEAR_AVAILABLE, 2, 0, T.stamp + 100000);
            return;
        }
        line(T.res.status == KILN_ITEMS_GEAR_STALE && now().available == 1, "another stamp: STALE, nothing written (status %d)", T.res.status);
        T.have = false;
        T.phase = NoSlot;
        return;
    case NoSlot:
        if (!T.noslot) {
            LOGI("gear selftest: SKIP NO_SLOT: no item in this world's containers whose type lacks the next slot");
            T.phase = Final;
            return;
        }
        if (!T.have) {
            const KilnItemsGearState n = now(T.noslot, T.noslot_stamp);
            submit(KILN_ITEMS_GEAR_AVAILABLE, static_cast<uint8_t>(n.available + 1), 0, T.noslot_stamp, T.noslot);
            return;
        }
        line(T.res.status == KILN_ITEMS_GEAR_NO_SLOT && T.res.changed == 0, "a type without the next perk slot: NO_SLOT (status %d)", T.res.status);
        T.have = false;
        T.phase = Final;
        return;
    case Final:  // the state that has to survive the save: one more slot than at the start, one unlocked
        if (!T.have) {
            submit(KILN_ITEMS_GEAR_AVAILABLE | KILN_ITEMS_GEAR_UNLOCKED | KILN_ITEMS_GEAR_DURABILITY_REFRESH, B.available + 1, 1);
            return;
        }
        T.final_ = T.res.after;
        line(T.res.status == KILN_ITEMS_GEAR_DONE && T.final_.available == B.available + 1 && T.final_.unlocked == 1 && T.final_.level == B.level + 1,
             "final state: perks %u/%u, level %u", T.final_.unlocked, T.final_.available, T.final_.level);
        T.have = false;
        T.phase = Persist;
        T.until = s + 10;
        return;
    case Persist: {
        write_marker();
        const auto* saves = static_cast<const KilnSavesApi*>(items_api()->service(str(KILN_SAVES_SERVICE), KILN_SAVES_VERSION));
        const KilnResult rc = saves && saves->request ? saves->request(KILN_SAVE_REASON_REQUEST) : KILN_ERR_UNAVAILABLE;
        LOGI("gear selftest: state written to gear-selftest.saved, save requested (%d); stop and start the lab to check it "
             "survives (%d failure(s) so far)", rc, T.fails);
        T.phase = Done;
        return;
    }
    default:
        return;
    }
}
}  // namespace st
}  // namespace

void gear_selftest_init() {
    char buf[1024];
    size_t len = 0;
    if (items_api()->path(KILN_PATH_DATA, buf, sizeof buf, &len) != KILN_OK) return;
    const std::u8string dir(reinterpret_cast<const char8_t*>(buf), len);
    st::T.dir = std::filesystem::path(dir) / "kiln.items";
    std::error_code ec;
    if (!std::filesystem::exists(st::T.dir / "gear-selftest", ec)) return;
    st::T.verify = std::filesystem::exists(st::T.dir / "gear-selftest.saved", ec);
    st::T.phase = st::Wait;
    LOGI("gear selftest enabled (%s)", st::T.verify ? "checking the state saved by the last boot" : "writes, then a save");
}

void gear_resolve() {
    const KilnApi* api = items_api();
    auto sym = [api](const char* n, uintptr_t* out) { return api->symbol(str(n), out) == KILN_OK && *out; };
    auto cst = [api](const char* n, int64_t* out) { return api->constant(str(n), out) == KILN_OK; };
    if (!api->feature(str(kFeature))) {
        set_detail("the sigdb feature items.gear is off on this build");
        LOGI("gear writes: off: the sigdb feature items.gear is off on this build");
        return;
    }
    uintptr_t changed = 0;
    int64_t lv = 0, lv_max = 0;
    const bool ok = E.fuel_ok && E.instances_ok && E.read_ok && sym("items/AttrChangedU32", &changed) &&
                    cst("items/attr.level", &lv) && cst("items/attr.level_max", &lv_max) &&
                    cst("world/registry.replicated", &F.replicated) && cst("world/registry.saved", &F.saved) && lv && lv_max &&
                    lv != lv_max && F.replicated > 0 && F.saved > 0 && F.replicated < 0x1000 && F.saved < 0x1000;
    if (!ok) {
        const char* why = "items.gear needs the tracked writes (items.fuel), item instances and the Level attribute ids";
        LOGW("gear writes: off: %s", why);
        lower(KILN_CAP_REASON_SYMBOL_MISSING, why);
        return;
    }
    F.attr_changed_u32 = reinterpret_cast<AttrChangedU32Fn>(changed);
    F.attr_level = static_cast<uint32_t>(lv);
    F.attr_level_max = static_cast<uint32_t>(lv_max);
    F.attr_dur = E.attr_durability;
    F.attr_dur_max = E.attr_durability_max;
    F.sym = true;
    // upgrades (package P3) pay on an inventory transaction of their own: the program is serial, so one buffer
    if (E.tx_ok && E.tx_size > 0 && E.tx_size < 0x10000)
        g_tx = static_cast<uint8_t*>(_aligned_malloc(static_cast<size_t>(E.tx_size), 16));  // process lifetime
    LOGI("gear writes: the symbols resolved (Level 0x%08x / max 0x%08x); upgrades %s", F.attr_level, F.attr_level_max,
         g_tx ? "can pay (the inventory transaction)" : "OFF (no inventory transaction)");
}

void gear_register(const KilnWorldApi* w) {
    if (!F.sym) return;
    if (!w) {
        F.sym = false;
        lower(KILN_CAP_REASON_SERVICE_MISSING, "gear writes need kiln.world (the program kiln_items_gear)");
        return;
    }
    char why[160] = "";
    if (!layout_ok(why, sizeof why)) {
        F.sym = false;
        LOGW("gear writes: off: %s", why);
        lower(KILN_CAP_REASON_SYMBOL_MISSING, why);
        return;
    }
    g_w = w;
    std::vector<KilnStr> sr, sw, ow, eo;
    for (int i = 0; i < 3; ++i) sr.push_back(str(kSing[i]));
    sw.push_back(str(kSing[3]));
    for (int i = 0; i < A_COUNT; ++i) ow.push_back(str(kAccNames[i]));
    for (const char* n : kOwn) ow.push_back(str(n));
    for (const char* n : kEvents) eo.push_back(str(n));
    KilnProgramDesc d{};
    d.size = sizeof d;
    d.flags = KILN_PROGRAM_SYSTEM | KILN_PROGRAM_HOST_ONLY;
    d.name = str(kProgram);
    d.each = &each;
    d.singletons_read = KilnNames{sr.data(), static_cast<uint32_t>(sr.size()), 0};
    d.singletons_write = KilnNames{sw.data(), static_cast<uint32_t>(sw.size()), 0};
    d.singletons_write_optional = 1;  // KnowledgeTriggerQueue, as kiln_items
    d.other_write = KilnNames{ow.data(), static_cast<uint32_t>(ow.size()), 0};
    d.events_out = KilnNames{eo.data(), static_cast<uint32_t>(eo.size()), 0};
    if (w->register_program(items_owner(), &d, &g_prog) != KILN_OK) {
        F.sym = false;
        lower(KILN_CAP_REASON_FAULTED, "kiln.world refused the program kiln_items_gear (see kiln.log)");
        return;
    }
    g_state.store(KILN_ITEMS_PROGRAM_PENDING);
    set_detail("waiting for the server world (kiln.world)");
    LOGI("program %s registered through kiln.world (gear writes; DurabilityInitialized 0x%04x, IsEquipped 0x%04x)", kProgram,
         F.init_bit, F.equipped_bit);
    gear_selftest_init();
}

namespace {
bool guarded_up_done(KilnItemsUpgradeDoneFn fn, const KilnItemsUpgradeResult* r, void* user) {
    __try {
        fn(r, user);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

KilnItemsUpgradeResult up_dropped(const UpReq& q, int32_t status, const char* why) {
    KilnItemsUpgradeResult r{};
    r.size = sizeof r;
    r.ticket = q.ticket;
    r.flags = q.r.flags;
    r.cost_count = q.r.cost_count;
    r.gear.size = sizeof r.gear;
    r.gear.ticket = q.ticket;
    r.gear.entity = q.r.gear.entity;
    r.gear.stamp = q.r.gear.stamp;
    r.gear.flags = q.r.gear.flags;
    r.gear.steps = static_cast<uint32_t>(current_step() - q.submitted);
    up_fail(&r, status, "%s", why);
    return r;
}

// Tick thread, Pre: the upgrades' results (g_mu not held).
void upgrades_pre(uint64_t step) {
    std::vector<KilnItemsUpgradeResult> done;
    std::vector<UpCallback> cbs;
    {
        std::lock_guard lock(g_mu);
        while (g_off.load() && !g_up_pending.empty()) {
            g_up_done.push_back(up_dropped(g_up_pending.front(), KILN_ITEMS_GEAR_UNAVAILABLE, "gear writes were switched off (see kiln.log)"));
            g_up_pending.pop_front();
        }
        const uint64_t last = g_last_update.load(std::memory_order_relaxed);
        while (!g_up_pending.empty() && step > g_up_pending.front().submitted + kTimeoutSteps &&
               last < g_up_pending.front().submitted + kTimeoutSteps) {
            g_up_done.push_back(up_dropped(g_up_pending.front(), KILN_ITEMS_GEAR_DROPPED, "the server world did not run kiln_items_gear for 10 s"));
            g_up_pending.pop_front();
        }
        count_pending();
        if (g_up_done.empty()) return;
        done.swap(g_up_done);
        for (const auto& r : done) {
            auto it = g_up_callbacks.find(r.ticket);
            if (it != g_up_callbacks.end()) {
                cbs.push_back(it->second);
                g_up_callbacks.erase(it);
            } else {
                cbs.push_back(UpCallback{nullptr, nullptr, 0});
            }
        }
    }
    for (size_t i = 0; i < done.size(); ++i) {
        const KilnItemsUpgradeResult& r = done[i];
        if (r.status != KILN_ITEMS_GEAR_DONE) g_refused.fetch_add(1, std::memory_order_relaxed);
        LOGI("upgrade ticket %llu%s: entity %u (stamp %u) write 0x%x, %u cost(s) -> status %d, changed 0x%x: perks %u/%u -> %u/%u, "
             "level %u -> %u; paid %u %u %u%s%s",
             static_cast<unsigned long long>(r.ticket), (r.flags & KILN_ITEMS_UPGRADE_DRY_RUN) ? " (dry run)" : "", r.gear.entity,
             r.gear.stamp, (r.flags & KILN_ITEMS_UPGRADE_WRITE) ? r.gear.flags : 0u, r.cost_count, r.status, r.gear.changed,
             r.gear.before.unlocked, r.gear.before.available, r.gear.after.unlocked, r.gear.after.available, r.gear.before.level,
             r.gear.after.level, r.paid[0], r.paid[1], r.paid[2], r.gear.message[0] ? ": " : "", r.gear.message);
        if (cbs[i].fn && !guarded_up_done(cbs[i].fn, &r, cbs[i].user))
            LOGE("an upgrade result callback (owner %llu) faulted; ignored", static_cast<unsigned long long>(cbs[i].owner));
    }
}

}  // namespace

void gear_pre(uint64_t step) {
    poll_program();
    st::step(step);
    upgrades_pre(step);
    std::vector<KilnItemsGearResult> done;
    std::vector<Callback> cbs;
    {
        std::lock_guard lock(g_mu);
        // switched off: whatever waits ends UNAVAILABLE now (the program no longer takes requests)
        while (g_off.load() && !g_pending.empty()) {
            g_done.push_back(dropped(g_pending.front(), KILN_ITEMS_GEAR_UNAVAILABLE, "gear writes were switched off (see kiln.log)"));
            g_pending.pop_front();
        }
        // a world that does not run the program: the oldest waiting requests end DROPPED after ~10 s
        const uint64_t last = g_last_update.load(std::memory_order_relaxed);
        while (!g_pending.empty() && step > g_pending.front().submitted + kTimeoutSteps && last < g_pending.front().submitted + kTimeoutSteps) {
            g_done.push_back(dropped(g_pending.front(), KILN_ITEMS_GEAR_DROPPED, "the server world did not run kiln_items_gear for 10 s"));
            g_pending.pop_front();
        }
        count_pending();
        if (g_done.empty()) return;
        done.swap(g_done);
        for (const auto& r : done) {
            auto it = g_callbacks.find(r.ticket);
            if (it != g_callbacks.end()) {
                cbs.push_back(it->second);
                g_callbacks.erase(it);
            } else {
                cbs.push_back(Callback{nullptr, nullptr, 0});
            }
        }
    }
    for (size_t i = 0; i < done.size(); ++i) {
        const KilnItemsGearResult& r = done[i];
        if (r.status != KILN_ITEMS_GEAR_DONE) g_refused.fetch_add(1, std::memory_order_relaxed);
        LOGI("gear ticket %llu%s: entity %u (stamp %u) flags 0x%x -> status %d, changed 0x%x: perks %u/%u -> %u/%u, level %u -> %u, "
             "durability %d/%d -> %d/%d%s%s",
             static_cast<unsigned long long>(r.ticket), (r.flags & KILN_ITEMS_GEAR_DRY_RUN) ? " (dry run)" : "", r.entity, r.stamp,
             r.flags, r.status, r.changed, r.before.unlocked, r.before.available, r.after.unlocked, r.after.available, r.before.level,
             r.after.level, r.before.durability, r.before.durability_max, r.after.durability, r.after.durability_max,
             r.message[0] ? ": " : "", r.message);
        if (cbs[i].fn && !guarded_done(cbs[i].fn, &r, cbs[i].user))
            LOGE("a gear result callback (owner %llu) faulted; ignored", static_cast<unsigned long long>(cbs[i].owner));
    }
}

void gear_world_closed() {
    std::lock_guard lock(g_mu);
    while (!g_pending.empty()) {
        g_done.push_back(dropped(g_pending.front(), KILN_ITEMS_GEAR_DROPPED, "the server world closed"));
        g_pending.pop_front();
    }
    while (!g_up_pending.empty()) {
        g_up_done.push_back(up_dropped(g_up_pending.front(), KILN_ITEMS_GEAR_DROPPED, "the server world closed"));
        g_up_pending.pop_front();
    }
    count_pending();
    g_verified = false;  // the next world's registry is checked again
    if (g_state.load() == KILN_ITEMS_PROGRAM_RUNNING) g_state.store(KILN_ITEMS_PROGRAM_ACTIVE);
}

bool gear_cancel(uint64_t ticket) {
    std::lock_guard lock(g_mu);
    return g_callbacks.erase(ticket) != 0 || g_up_callbacks.erase(ticket) != 0;
}

void gear_cancel_owner(KilnOwner owner) {
    std::lock_guard lock(g_mu);
    for (auto it = g_callbacks.begin(); it != g_callbacks.end();) {
        if (it->second.owner == owner) it = g_callbacks.erase(it);
        else ++it;
    }
    for (auto it = g_up_callbacks.begin(); it != g_up_callbacks.end();) {
        if (it->second.owner == owner) it = g_up_callbacks.erase(it);
        else ++it;
    }
}

KilnResult gear_write(KilnOwner owner, const KilnItemsGearRequest* request, KilnItemsGearDoneFn done, void* user,
                      uint64_t* out_ticket) {
    if (!request || !gear::request_valid(*request)) return KILN_ERR_INVALID_ARGUMENT;
    if (!usable()) return KILN_ERR_UNAVAILABLE;
    Req q{};
    q.ticket = items_next_ticket();
    q.owner = owner;
    q.r = *request;
    q.r.size = sizeof(KilnItemsGearRequest);
    q.submitted = current_step();
    {
        std::lock_guard lock(g_mu);
        if (g_pending.size() >= kMaxPending) return KILN_ERR_UNAVAILABLE;
        g_pending.push_back(q);
        if (done) g_callbacks[q.ticket] = Callback{done, user, owner};
        count_pending();
    }
    if (out_ticket) *out_ticket = q.ticket;
    return KILN_OK;
}

KilnResult gear_upgrade(KilnOwner owner, const KilnItemsUpgradeRequest* request, KilnItemsUpgradeDoneFn done, void* user,
                        uint64_t* out_ticket) {
    if (!request || !upgrade::request_valid(*request)) return KILN_ERR_INVALID_ARGUMENT;
    if (!usable() || !E.tx_ok || !g_tx) return KILN_ERR_UNAVAILABLE;
    UpReq q{};
    q.ticket = items_next_ticket();
    q.owner = owner;
    q.r = *request;
    q.r.size = sizeof(KilnItemsUpgradeRequest);
    q.r.gear.size = sizeof(KilnItemsGearRequest);
    q.submitted = current_step();
    {
        std::lock_guard lock(g_mu);
        if (g_pending.size() + g_up_pending.size() >= kMaxPending) return KILN_ERR_UNAVAILABLE;
        g_up_pending.push_back(q);
        if (done) g_up_callbacks[q.ticket] = UpCallback{done, user, owner};
        count_pending();
    }
    if (out_ticket) *out_ticket = q.ticket;
    return KILN_OK;
}

namespace {
struct ReadCall {
    Reader reader;
    KilnEntity entity;
    KilnItemsGearState* out;
    bool found;
};
void read_impl(void* p) {
    auto* c = static_cast<ReadCall*>(p);
    Blocks b;
    c->found = read_state(c->reader, c->entity, c->out, &b);
}
}  // namespace

KilnResult gear_read(KilnEntity entity, uint32_t stamp, KilnItemsGearState* out) {
    if (!out || entity == 0) return KILN_ERR_INVALID_ARGUMENT;
    *out = KilnItemsGearState{};
    if (!F.sym || !F.layout || !E.read_ok) return KILN_ERR_UNAVAILABLE;
    const uintptr_t world = items_server_world();
    if (!world) return KILN_ERR_UNAVAILABLE;
    ReadCall c{};
    c.reader.world = world;
    c.reader.attributes = reinterpret_cast<void*>(items_api()->singleton(world, str("impact::AttributeRegistry")));
    c.reader.items = reinterpret_cast<void*>(items_api()->singleton(world, str("g38_items::ItemRegistry")));
    if (!c.reader.attributes || !c.reader.items) return KILN_ERR_UNAVAILABLE;
    c.entity = entity;
    c.out = out;
    unsigned long code = 0;
    if (!guard(&read_impl, &c, &code)) {
        LOGE("reading entity %u's gear faulted (0x%08lx)", entity, code);
        *out = KilnItemsGearState{};
        return KILN_ERR_UNAVAILABLE;
    }
    if (!c.found || (stamp && stamp_for(entity, out->item) != stamp)) {
        *out = KilnItemsGearState{};
        return KILN_ERR_NOT_FOUND;
    }
    return KILN_OK;
}

// kiln.item_costs (costs.h): what this program can run now.
bool gear_costs_stacks(char* why) {
    const char* no = !F.sym ? "gear writes are off on this build (items.gear), so kiln_items_gear does not run"
                     : g_off.load() ? "kiln_items_gear was switched off (see kiln.log)"
                     : !usable() ? "kiln_items_gear is not registered with kiln.world"
                     : (!E.tx_ok || !g_tx) ? "the game's inventory transaction is not available on this build"
                                           : nullptr;
    if (no && why) std::snprintf(why, 160, "%s", no);
    return no == nullptr;
}

bool gear_costs_wear(char* why) {
    if (!gear_costs_stacks(why)) return false;
    const char* no = !E.wear_ok || !wear::facts().ok ? "the durability debit (WearDelta, enableDurability) is not known on this build (items.tool_wear_debit)"
                     : !F.attr_dur || !F.attr_dur_max ? "the durability attributes are not known on this build"
                                                       : nullptr;
    if (no && why) std::snprintf(why, 160, "%s: %s", no, wear::facts().why[0] ? wear::facts().why : "see kiln.log");
    return no == nullptr;
}

uint64_t gear_last_update_step() { return g_last_update.load(std::memory_order_relaxed); }

bool gear_switched_off(char* why) {
    if (!g_off.load()) return false;
    if (why) std::snprintf(why, 160, "kiln_items_gear was switched off (see kiln.log)");
    return true;
}

KilnResult gear_status(KilnItemsGearStatus* out) {
    if (!out || out->size < sizeof(KilnItemsGearStatus)) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    out->available = usable() ? 1 : 0;
    out->program = static_cast<uint8_t>(g_state.load());
    out->replicated = g_replicated.load();
    out->saved = g_saved.load();
    out->pending = g_pending_n.load();
    out->writes = g_writes.load();
    out->refused = g_refused.load();
    std::lock_guard lock(g_mu);
    std::snprintf(out->detail, sizeof out->detail, "%s", g_detail);
    return KILN_OK;
}

}  // namespace kitems
