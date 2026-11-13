// SPDX-License-Identifier: MIT
// kiln.items v5: equip and unequip through the game's own functions (docs/re/items.md section 16).
//
// The game's equipment_actions program (0x1472c0) handles a client's changeEquipmentAction in a thin shell (0x144950):
// an inventory transaction set up from the program's accessors, then equip 0x1b4e70 or unequip 0x1b9b80
// (out, tx, inventorySlotId, equipmentSlot), commit on error 0, roll back otherwise. Both functions take the player
// from the program's current entity (ProgramContext + 8, the same getter kiln.world's current_entity uses), so they can
// only run in a program that iterates the player. kiln_items_equip is that program: an entity program over the player
// characters (read PlayerInput), with kiln_items' own singletons, other-entity accessors and event senders, registered
// through kiln.world. For a player with a waiting request it builds the transaction as equipment_actions does and calls
// the same function, so every check of the game's equip applies, and a refusal rolls everything back.
#include "equip.h"

#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <unordered_map>
#include <vector>

#include "engine.h"
#include "equip_logic.h"
#include "instances.h"
#include "services/kiln_players.h"
#include "services/kiln_types.h"
#include "xfer.h"

namespace kitems {
namespace {

constexpr const char* kProgram = "kiln_items_equip";
constexpr uint32_t kMaxPending = 1024;
constexpr uint32_t kPerPlayerStep = 4;    // requests of one player per step (each is a transaction)
constexpr uint64_t kTimeoutSteps = 250;   // ~10 s at 24.5 Hz
constexpr uint32_t kSelectionSize = 216;  // ecs::SlotSelection (equipment: 20 x InventorySlotId at +0)

#define LOGI(...) items_log(KILN_LOG_INFO, __VA_ARGS__)
#define LOGW(...) items_log(KILN_LOG_WARN, __VA_ARGS__)
#define LOGE(...) items_log(KILN_LOG_ERROR, __VA_ARGS__)

template <typename T>
T rd(const void* p, int64_t off) {
    T v;
    std::memcpy(&v, static_cast<const uint8_t*>(p) + off, sizeof v);
    return v;
}

struct Request {
    uint64_t ticket;
    KilnOwner owner;
    KilnItemsEquipRequest r;
    uint64_t submitted;
    // The player's session at the submission (kiln.players: the connection handle carries the slot's generation, and
    // the step the session was first seen in game). A player entity is slot + 1: the next player in the slot gets the
    // same id, so a request runs only for the session it was made for. Ids from 1024 on (characters spawned by a lab)
    // come from the engine's counter and are never reused: no session.
    bool session = false;
    uint32_t handle = 0;
    uint64_t joined_step = 0;
    uint64_t verified_step = 0;  // the Pre that checked the session; the program runs it only in that step
};
struct Callback {
    KilnItemsEquipDoneFn fn;
    void* user;
    KilnOwner owner;
};

const KilnWorldApi* g_w = nullptr;
KilnHandle g_handle = 0;
std::atomic<int> g_state{KILN_ITEMS_PROGRAM_NONE};
std::atomic<uint32_t> g_pending_n{0};
std::atomic<uint64_t> g_changed{0}, g_refused{0};
std::mutex g_mu;  // the containers below and g_detail; never held while calling the game or a callback
std::vector<Request> g_pending;
std::vector<KilnItemsEquipResult> g_done;
std::unordered_map<uint64_t, Callback> g_callbacks;
char g_detail[160] = "not registered";

// Iterator slots (from kiln.world) and the verified accessors of the current server world.
int g_sing[4] = {-1, -1, -1, -1}, g_acc[A_COUNT], g_ev[2] = {-1, -1};
bool g_slots = false;
uint8_t* g_tx = nullptr;  // one InventoryTx: the program is serial (other-write)
uint32_t g_hash_input = 0;
int g_actor_slot = -1;           // the program's read slot of the player's Actor (the death check)
int64_t g_dead_offset = -1;      // Actor.currentState (u64 StateMask), from kiln.types
int g_dead_bit = -1;             // keen::actor::StateFlag.Dead
const KilnPlayersApi* g_players = nullptr;

constexpr KilnEntity kLastSlotId = 1023;  // player entities are slot + 1 within the engine's reserved range

bool slot_player(KilnEntity e) { return e >= 1 && e <= kLastSlotId; }

// The session of a player entity (kiln.players' last scan): false when no player in game has the id.
bool session_of(KilnEntity e, uint32_t* handle, uint64_t* joined) {
    if (!g_players) return false;
    KilnPlayer p{};
    p.size = sizeof p;
    if (g_players->get(e, &p) != KILN_OK || p.state != KILN_PLAYER_IN_GAME) return false;
    *handle = p.handle;
    *joined = p.joined_step;
    return true;
}

// A game client with no server world (its menu, or joined to another machine's world): a request would never run.
bool no_server_world_here() {
    KilnSessionInfo si{};
    si.size = sizeof si;
    if (items_api()->session(&si) != KILN_OK) return false;
    return si.side == KILN_SIDE_CLIENT && si.worlds[KILN_WORLD_SERVER] == 0;
}

// Actor.currentState and the Dead flag by the game's own reflection (kiln.types), base classes included. False: the
// death check cannot be made, and equip stays off.
bool resolve_death_check(char* why, size_t cap) {
    const auto* t = static_cast<const KilnTypesApi*>(items_api()->service(KilnStr{KILN_TYPES_SERVICE, std::strlen(KILN_TYPES_SERVICE)}, 1));
    if (!t || t->header.size < sizeof(KilnTypesApi)) return std::snprintf(why, cap, "kiln.types is not loaded"), false;
    auto str = [](const char* s) { return KilnStr{s, std::strlen(s)}; };
    const KilnType* actor = t->type_by_name(str("keen::ecs::Actor"));
    for (int depth = 0; actor && depth < 8 && g_dead_offset < 0; ++depth) {
        KilnTypeInfo ti{};
        ti.size = sizeof ti;
        if (t->type_info(actor, &ti) != KILN_OK) break;
        for (uint32_t i = 0; i < ti.field_count; ++i) {
            KilnTypeField f{};
            if (t->type_field(actor, i, &f) == KILN_OK && f.name.len == 12 && std::memcmp(f.name.ptr, "currentState", 12) == 0) {
                KilnTypeInfo fi{};
                fi.size = sizeof fi;
                if (t->type_info(f.type, &fi) == KILN_OK && fi.type_size == 8) g_dead_offset = static_cast<int64_t>(f.offset);
                break;
            }
        }
        actor = ti.inner;  // the base class (BaseActor holds currentState)
    }
    if (const KilnType* flag = t->type_by_name(str("keen::actor::StateFlag"))) {
        KilnTypeInfo ti{};
        ti.size = sizeof ti;
        if (t->type_info(flag, &ti) == KILN_OK)
            for (uint32_t i = 0; i < ti.field_count; ++i) {
                KilnStr name{};
                int64_t value = 0;
                if (t->type_enum(flag, i, &name, &value) == KILN_OK && name.len == 4 && std::memcmp(name.ptr, "Dead", 4) == 0 &&
                    value >= 0 && value < 64)
                    g_dead_bit = static_cast<int>(value);
            }
    }
    if (g_dead_offset < 0 || g_dead_bit < 0)
        return std::snprintf(why, cap, "Actor.currentState or StateFlag.Dead is not in this build's reflection"), false;
    return true;
}

uint32_t fnv(const char* s) {
    uint32_t h = 0x811c9dc5u;
    for (; *s; ++s) h = (h ^ static_cast<uint8_t>(*s)) * 0x01000193u;
    return h;
}

void set_detail(const char* s) {
    std::lock_guard lock(g_mu);
    std::snprintf(g_detail, sizeof g_detail, "%s", s);
}

void broken(const char* why) {
    if (g_state.exchange(KILN_ITEMS_PROGRAM_BROKEN) != KILN_ITEMS_PROGRAM_BROKEN)
        LOGE("equip is off until the server restarts: %s", why);
    set_detail(why);
}

const char* const kSing[4] = {"BalancingRegistry", "g38_items::ItemRegistry", "impact::AttributeRegistry",
                              "KnowledgeTriggerQueue"};
const char* const kEvents[2] = {"InventoryFull", "PickupEvent"};

bool resolve_slots() {
    if (g_slots) return true;
    auto str = [](const char* s) { return KilnStr{s, std::strlen(s)}; };
    for (int i = 0; i < 4; ++i) g_sing[i] = g_w->program_slot(g_handle, KILN_SLOT_SINGLETON, str(kSing[i]));
    for (int i = 0; i < A_COUNT; ++i) g_acc[i] = g_w->program_slot(g_handle, KILN_SLOT_OTHER_WRITE, str(kAccNames[i]));
    for (int i = 0; i < 2; ++i) g_ev[i] = g_w->program_slot(g_handle, KILN_SLOT_EVENT_OUT, str(kEvents[i]));
    g_actor_slot = g_w->program_slot(g_handle, KILN_SLOT_READ, str("Actor"));
    for (int s : g_sing) if (s < 1 || s >= 64) return false;
    for (int s : g_acc) if (s < 1 || s >= 64) return false;
    for (int s : g_ev) if (s < 1 || s >= 64) return false;
    if (g_actor_slot < 1 || g_actor_slot >= 64) return false;
    g_slots = true;
    return true;
}

// ---- inside the program: plain reads through the program's accessors ----
const void* other(const Access& a, Acc which, KilnEntity e, uint64_t min_size) {
    PtrSize ps{};
    E.read_other(&ps, a.storage, a.acc[which], e);
    return ps.ptr && ps.size >= min_size ? ps.ptr : nullptr;
}

struct Selection {
    uint64_t eq[kEquipSlots];
};

bool read_selection(const Access& a, KilnEntity p, Selection* s) {
    const auto* sel = static_cast<const uint8_t*>(other(a, A_SLOTSEL, p, kSelectionSize));
    if (!sel) return false;
    for (uint32_t i = 0; i < kEquipSlots; ++i) {
        const uint32_t inv = rd<uint32_t>(sel, i * 8);
        s->eq[i] = inv ? KILN_SLOT_ID(inv, sel[i * 8 + 4]) : 0;
    }
    return true;
}

const Stack* stack_in(const PageSet& ps, uint64_t slot) {
    if (!slot || (slot >> 40) != 0 || KILN_SLOT_INDEX(slot) >= kSlotsPerPage) return nullptr;
    for (uint32_t i = 0; i < ps.n; ++i)
        if (ps.p[i].entity == KILN_SLOT_INVENTORY(slot)) return &ps.p[i].s[KILN_SLOT_INDEX(slot)];
    return nullptr;
}

// A free slot in one of the player's Generic (4) links: where the game's unequip puts the item (0x1b5a50).
bool has_free_generic(const Access& a, KilnEntity p) {
    const auto* setup = static_cast<const uint8_t*>(other(a, A_SETUP, p, kSetupSize));
    if (!setup) return true;  // unknown: do not claim "no room"
    for (int k = 0; k < 16; ++k) {
        const uint32_t link = rd<uint32_t>(setup, 4 * k);
        if (link == 0) break;
        if (setup[0x40 + k] != 4) continue;
        const auto* inv = static_cast<const uint8_t*>(other(a, A_INVENTORY, link, kInventorySize));
        if (!inv) continue;
        for (uint32_t j = 0; j < 8; ++j)
            if (rd<uint32_t>(inv, 12 * j + 4) == 0) return true;
    }
    return false;
}

// The setup block equipment_actions builds for its transaction (0x144950): flags 4 (an unequip that finds no room
// sends the player InventoryFull), the context, item and balancing registries, the accessors, InventoryFull's sender,
// the player as operator; no attribute registry, no Fuel, no PickupEvent, no knowledge queue.
void fill_equip_setup(const Access& a, KilnEntity player, bool dry, uint8_t* s) {
    std::memset(s, 0, kSetupBlock);
    s[0x00] = dry ? 0 : 0x04;
    auto put = [s](int off, const void* v) { std::memcpy(s + off, &v, 8); };
    auto put_acc = [s](int off, uint64_t v) { std::memcpy(s + off, &v, 8); };
    put(0x08, a.ctx);
    put(0x10, a.items);
    put(0x18, a.balancing);
    put_acc(0x28, a.acc[A_SETUP]);
    put_acc(0x30, a.acc[A_INVENTORY]);
    put_acc(0x38, a.acc[A_SLOTSEL]);
    put_acc(0x40, a.acc[A_ITEMSTATE]);
    put_acc(0x48, a.acc[A_OWNER]);
    put_acc(0x50, a.acc[A_SETUP]);
    put_acc(0x58, a.acc[A_EMPTY]);
    put(0xA8, a.ev_full);
    std::memcpy(s + E.setup_operator, &player, 4);
}

void fail(KilnItemsEquipResult* res, int32_t status, const char* fmt, uint32_t x = 0, uint32_t y = 0) {
    res->status = status;
    std::snprintf(res->message, sizeof res->message, fmt, x, y);
}

void equip_impl(const Access& a, KilnEntity p, const KilnItemsEquipRequest& r, uint8_t* tx, KilnItemsEquipResult* res) {
    std::vector<PageSet> v(2);
    bool init = false;
    if (!read_pages(a, p, &v[0], &init) || !init)
        return fail(res, KILN_ITEMS_NO_INVENTORY, "player %u has no initialized inventory", p);
    Selection before{};
    if (!read_selection(a, p, &before)) return fail(res, KILN_ITEMS_NO_INVENTORY, "entity %u has no equipment (SlotSelection)", p);
    const bool unequip = (r.flags & KILN_ITEMS_EQUIP_UNEQUIP) != 0;
    const bool dry = (r.flags & KILN_ITEMS_EQUIP_DRY_RUN) != 0;
    const uint8_t gslot = r.equipment_slot;
    const int idx = gslot == KILN_ITEMS_EQUIP_ANY ? -1 : selection_index(gslot);
    Stack src{};
    uint64_t from = 0;
    if (unequip) {
        from = before.eq[idx];
        const Stack* s = stack_in(v[0], from);
        if (!s || is_empty(*s)) return fail(res, KILN_ITEMS_SLOT_CHANGED, "nothing is equipped in that slot of player %u", p);
        src = *s;
    } else {
        from = r.slot;
        const Stack* s = stack_in(v[0], from);
        if (!s) return fail(res, KILN_ITEMS_SLOT_CHANGED, "the slot (inventory %u) is not one of player %u's", KILN_SLOT_INVENTORY(from), p);
        if (is_empty(*s)) return fail(res, KILN_ITEMS_SLOT_CHANGED, "the slot holds nothing");
        if (r.pide && pide_of(*s) != r.pide)
            return fail(res, KILN_ITEMS_SLOT_CHANGED, "the slot holds item entity %u, not %u", pide_of(*s), r.pide);
        src = *s;
    }
    res->item = src.item;
    res->pide = pide_of(src);
    res->from = from;
    const auto* info = static_cast<const uint8_t*>(E.item_find(src.item, a.items));
    const bool fits = unequip || idx < 0 || !info || info[0x104] == slot_type_of(gslot);  // ItemInfo +0x104 EquipmentSlotType

    alignas(16) uint8_t s[kSetupBlock];
    fill_equip_setup(a, p, dry, s);
    E.tx_ctor(tx);
    E.tx_init(tx, s);
    alignas(8) Result12 out[3] = {};
    if (unequip) E.unequip_item(&out[0], tx, from, gslot);
    else E.equip_item(&out[0], tx, from, idx >= 0 ? gslot : kEquipSlotChoose);
    const uint8_t err = out[0].error;
    // what changed: the inventory moves are in place before the commit (and before a dry run's rollback); the
    // selection of a slot that was empty is written at the commit (the lab: SlotSelection.equipment of an empty ring slot
    // still reads 0 between the call and the commit)
    bool room = true;
    int got = idx;
    if (err == 0) {
        read_pages(a, p, &v[1], nullptr);
        res->to = where_now(v[0].p, v[0].n, v[1].p, v[1].n, src.item, res->pide, from);
        Selection mid{};
        if (!unequip && read_selection(a, p, &mid)) {
            if (idx >= 0 && mid.eq[idx]) res->to = mid.eq[idx];
            for (uint32_t i = 0; got < 0 && i < kEquipSlots; ++i)
                if (mid.eq[i] && mid.eq[i] == res->to) got = static_cast<int>(i);
        }
    } else if (unequip) {
        room = has_free_generic(a, p);
    }
    if (err == 0 && !dry) E.commit(tx);
    else E.rollback(tx);
    E.free_arrays(tx);
    if (err == 0 && !unequip) {
        // the item chose its slot: the committed selection names it (a dry run cannot know it: 0xFF)
        Selection after{};
        if (got < 0 && !dry && read_selection(a, p, &after))
            for (uint32_t i = 0; got < 0 && i < kEquipSlots; ++i)
                if (after.eq[i] && after.eq[i] == res->to) got = static_cast<int>(i);
        res->equipment_slot = got >= 0 ? game_slot_of(got) : 0xFF;
        if (got >= 0 && before.eq[got] && before.eq[got] != from) {
            if (const Stack* old = stack_in(v[0], before.eq[got]); old && !is_empty(*old)) {
                res->replaced = old->item;
                res->replaced_pide = pide_of(*old);
                res->replaced_to = where_now(v[0].p, v[0].n, v[1].p, v[1].n, old->item, res->replaced_pide, res->to);
            }
        }
    }
    res->game_error = err;
    res->status = equip_status_of(err, unequip, room);
    if (err != 0) {
        res->to = 0;
        std::snprintf(res->message, sizeof res->message, "%s", equip_error_text(err, unequip, fits, room));
        return;
    }
    res->stamp = stamp_for(res->pide, res->item);
    res->replaced_stamp = stamp_for(res->replaced_pide, res->replaced);
}

bool run_equip(const Access& a, KilnEntity p, const KilnItemsEquipRequest& r, uint8_t* tx, KilnItemsEquipResult* res,
               unsigned long* code) {
    __try {
        equip_impl(a, p, r, tx, res);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

KilnItemsEquipResult result_of(const Request& q, uint64_t step) {
    KilnItemsEquipResult res{};
    res.size = sizeof res;
    res.ticket = q.ticket;
    res.player = q.r.player;
    res.flags = q.r.flags;
    res.equipment_slot = q.r.equipment_slot;
    res.steps = static_cast<uint32_t>(step - q.submitted);
    return res;
}

void finish(const KilnItemsEquipResult& res) {
    std::lock_guard lock(g_mu);
    g_done.push_back(res);
}

// The iterated player's Actor (a declared read of the program), right before the transaction. False on a fault.
bool dead_now(const uint64_t* it, bool* dead) {
    __try {
        const auto* actor = reinterpret_cast<const uint8_t*>(it[g_actor_slot]);
        *dead = actor && ((rd<uint64_t>(actor, g_dead_offset) >> g_dead_bit) & 1u) != 0;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// kiln.world calls this once per player character per step (each mode: `ctx` iterates that entity).
void KILN_CALL each(KilnProgramCtx* ctx, uint64_t* it, KilnEntity player, void*) {
    if (g_pending_n.load(std::memory_order_acquire) == 0) return;
    Request batch[kPerPlayerStep];
    uint32_t n = 0;
    {
        std::lock_guard lock(g_mu);
        const uint64_t now_step = current_step();
        for (auto i = g_pending.begin(); i != g_pending.end() && n < kPerPlayerStep;) {
            if (i->r.player == player && i->verified_step == now_step) {
                batch[n++] = *i;
                i = g_pending.erase(i);
            } else {
                ++i;
            }
        }
        g_pending_n.store(static_cast<uint32_t>(g_pending.size()), std::memory_order_release);
    }
    if (n == 0) return;
    const uint64_t step = current_step();
    unsigned long code = 0;
    Access a{};
    bool ok = false;
    char why[160] = "";
    if (g_state.load() == KILN_ITEMS_PROGRAM_BROKEN) {
        std::snprintf(why, sizeof why, "equip was switched off (see kiln.log)");
    } else if (!resolve_slots()) {
        broken("kiln.world did not report kiln_items_equip's iterator slots");
        std::snprintf(why, sizeof why, "equip is off (see kiln.log)");
    } else if (!verify_access(it, g_sing, g_acc, g_ev, ctx, &a, &ok, why, &code)) {
        char b[160];
        std::snprintf(b, sizeof b, "checking kiln_items_equip's iterator faulted (0x%08lx)", code);
        broken(b);
        std::snprintf(why, sizeof why, "equip is off (see kiln.log)");
    } else if (!ok) {
        broken(why);
    } else if (g_state.exchange(KILN_ITEMS_PROGRAM_RUNNING) != KILN_ITEMS_PROGRAM_RUNNING) {
        set_detail("running");
        LOGI("program %s runs: accessors verified against the world's component map (player %u)", kProgram, player);
    }
    for (uint32_t i = 0; i < n; ++i) {
        KilnItemsEquipResult res = result_of(batch[i], step);
        bool dead = false;
        if (g_state.load() == KILN_ITEMS_PROGRAM_RUNNING && !dead_now(it, &dead)) {
            broken("reading the player's Actor faulted");
            std::snprintf(why, sizeof why, "equip is off (see kiln.log)");
        }
        if (g_state.load() != KILN_ITEMS_PROGRAM_RUNNING) {
            res.status = KILN_ITEMS_UNAVAILABLE;
            std::snprintf(res.message, sizeof res.message, "%s", why[0] ? why : "equip is off (see kiln.log)");
        } else if (dead) {
            // the game's equip has no such check; a dead character's inventory is being moved to its tombstone
            res.status = KILN_ITEMS_REFUSED;
            std::snprintf(res.message, sizeof res.message, "the player is dead");
        } else if (!run_equip(a, player, batch[i].r, g_tx, &res, &code)) {
            abandon_tx(g_tx);
            res.status = KILN_ITEMS_UNAVAILABLE;
            std::snprintf(res.message, sizeof res.message, "the game's equip faulted (0x%08lx)", code);
            char b[160];
            std::snprintf(b, sizeof b, "the game's %s faulted (0x%08lx) for player %u",
                          (batch[i].r.flags & KILN_ITEMS_EQUIP_UNEQUIP) ? "unequip" : "equip", code, player);
            broken(b);
        }
        if (res.status == KILN_ITEMS_DONE && !(res.flags & KILN_ITEMS_EQUIP_DRY_RUN)) g_changed.fetch_add(1);
        else if (res.status != KILN_ITEMS_DONE) g_refused.fetch_add(1);
        finish(res);
    }
}

// ---- tick thread ----
bool guarded_component(uintptr_t world, uint32_t hash, KilnEntity e, bool* found, unsigned long* code) {
    __try {
        PtrSize ps{};
        E.get_component(&ps, world, hash, e);
        *found = ps.ptr != nullptr;
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool guarded_done(KilnItemsEquipDoneFn fn, const KilnItemsEquipResult* r, void* user, unsigned long* code) {
    __try {
        fn(r, user);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void poll_program() {
    if (!g_w || g_state.load() != KILN_ITEMS_PROGRAM_PENDING) return;
    KilnProgramInfo pi{};
    pi.size = sizeof pi;
    if (g_w->program_info(g_handle, &pi) != KILN_OK) return;
    if (pi.state == KILN_PROGRAM_ACTIVE) {
        int expected = KILN_ITEMS_PROGRAM_PENDING;
        if (g_state.compare_exchange_strong(expected, KILN_ITEMS_PROGRAM_ACTIVE))
            set_detail("program registered (kiln.world), waiting for a request");
    } else if (pi.state == KILN_PROGRAM_REFUSED || pi.state == KILN_PROGRAM_FAULTED || pi.state == KILN_PROGRAM_SKIPPED) {
        g_state.store(KILN_ITEMS_PROGRAM_NONE);
        char b[160];
        std::snprintf(b, sizeof b, "kiln.world reports kiln_items_equip as state %u (see kiln.log)", pi.state);
        set_detail(b);
        LOGE("%s", b);
    }
}

}  // namespace

void equip_register(const KilnWorldApi* w) {
    if (!E.equip_ok) {
        set_detail("items.equip is off on this build (the game's equip or unequip did not resolve)");
        return;
    }
    if (!w || w->header.size < sizeof(KilnWorldApi)) {
        set_detail("equip needs kiln.world (its entity programs): kiln.world is not loaded");
        LOGW("equip unavailable: kiln.world is not loaded");
        return;
    }
    g_players = static_cast<const KilnPlayersApi*>(items_api()->service(KilnStr{KILN_PLAYERS_SERVICE, std::strlen(KILN_PLAYERS_SERVICE)}, 1));
    if (!g_players || g_players->header.size < sizeof(KilnPlayersApi)) {
        g_players = nullptr;
        set_detail("equip needs kiln.players (to tell two sessions of one player slot apart): kiln.players is not loaded");
        LOGW("equip unavailable: kiln.players is not loaded");
        return;
    }
    char why[160] = "";
    if (!resolve_death_check(why, sizeof why)) {
        char b[160];
        std::snprintf(b, sizeof b, "the death check cannot be made: %s", why);
        set_detail(b);
        LOGW("equip unavailable: %s", b);
        return;
    }
    g_tx = static_cast<uint8_t*>(VirtualAlloc(nullptr, static_cast<SIZE_T>(E.tx_size), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
    if (!g_tx) {
        set_detail("no memory for the equip transaction");
        return;
    }
    g_hash_input = fnv("keen::ecs::PlayerInput");
    auto str = [](const char* s) { return KilnStr{s, std::strlen(s)}; };
    std::vector<KilnStr> read{str("Actor"), str("PlayerInput")}, sr, sw{str(kSing[3])}, ow, eo;
    for (int i = 0; i < 3; ++i) sr.push_back(str(kSing[i]));
    for (int i = 0; i < A_COUNT; ++i) ow.push_back(str(kAccNames[i]));
    for (const char* e : kEvents) eo.push_back(str(e));
    KilnProgramDesc d{};
    d.size = sizeof d;
    d.flags = KILN_PROGRAM_HOST_ONLY;  // an entity query: the player characters
    d.name = str(kProgram);
    d.each = &each;
    d.read = KilnNames{read.data(), static_cast<uint32_t>(read.size()), 0};
    d.singletons_read = KilnNames{sr.data(), static_cast<uint32_t>(sr.size()), 0};
    d.singletons_write = KilnNames{sw.data(), static_cast<uint32_t>(sw.size()), 0};
    d.singletons_write_optional = 1;  // KnowledgeTriggerQueue, as kiln_items
    d.other_write = KilnNames{ow.data(), static_cast<uint32_t>(ow.size()), 0};
    d.events_out = KilnNames{eo.data(), static_cast<uint32_t>(eo.size()), 0};
    KilnHandle h = 0;
    const KilnResult rc = w->register_program(items_owner(), &d, &h);
    if (rc != KILN_OK) {
        set_detail("kiln.world refused the program kiln_items_equip (see kiln.log)");
        LOGW("kiln.world refused program %s (%d): equip unavailable", kProgram, rc);
        return;
    }
    g_w = w;
    g_handle = h;
    g_state.store(KILN_ITEMS_PROGRAM_PENDING);
    set_detail("waiting for the server world (kiln.world)");
    LOGI("program %s registered through kiln.world (equip and unequip)", kProgram);
}

void equip_pre(uintptr_t world, uint64_t step) {
    poll_program();
    const int state = g_state.load();
    std::vector<KilnItemsEquipResult> ended;
    std::vector<std::pair<uint64_t, KilnEntity>> check;
    {
        std::lock_guard lock(g_mu);
        for (auto i = g_pending.begin(); i != g_pending.end();) {
            const bool off = state == KILN_ITEMS_PROGRAM_NONE || state == KILN_ITEMS_PROGRAM_BROKEN;
            const bool stale = step > i->submitted + kTimeoutSteps;
            if (off || stale) {
                KilnItemsEquipResult r = result_of(*i, step);
                fail(&r, off ? KILN_ITEMS_UNAVAILABLE : KILN_ITEMS_DROPPED,
                     off ? "equip is off (see kiln.log)" : "kiln_items_equip did not run for player %u for 10 s", i->r.player);
                ended.push_back(r);
                i = g_pending.erase(i);
                continue;
            }
            // the session the request was made for, checked in the Pre of the step its program runs in
            if (i->session) {
                uint32_t handle = 0;
                uint64_t joined = 0;
                if (!session_of(i->r.player, &handle, &joined) || handle != i->handle || joined != i->joined_step) {
                    KilnItemsEquipResult r = result_of(*i, step);
                    fail(&r, KILN_ITEMS_NO_INVENTORY, "player %u's session ended (left the game); the id may be another player's now",
                         i->r.player);
                    ended.push_back(r);
                    i = g_pending.erase(i);
                    continue;
                }
            }
            i->verified_step = step;
            check.emplace_back(i->ticket, i->r.player);
            ++i;
        }
        g_pending_n.store(static_cast<uint32_t>(g_pending.size()), std::memory_order_release);
    }
    // A request for an entity that is no player character in this world would wait forever: end it now.
    if (world && E.get_component && !check.empty()) {
        std::vector<uint64_t> gone;
        for (const auto& [ticket, player] : check) {
            bool found = true;
            unsigned long code = 0;
            if (guarded_component(world, g_hash_input, player, &found, &code) && !found) gone.push_back(ticket);
        }
        if (!gone.empty()) {
            std::lock_guard lock(g_mu);
            for (auto i = g_pending.begin(); i != g_pending.end();) {
                if (std::find(gone.begin(), gone.end(), i->ticket) == gone.end()) {
                    ++i;
                    continue;
                }
                KilnItemsEquipResult r = result_of(*i, step);
                fail(&r, KILN_ITEMS_NO_INVENTORY, "entity %u is no player character in the world", i->r.player);
                ended.push_back(r);
                i = g_pending.erase(i);
            }
            g_pending_n.store(static_cast<uint32_t>(g_pending.size()), std::memory_order_release);
        }
    }
    std::vector<KilnItemsEquipResult> done;
    std::vector<Callback> cbs;
    {
        std::lock_guard lock(g_mu);
        for (auto& r : ended) g_done.push_back(r);
        if (g_done.empty()) return;
        done.swap(g_done);
        for (const KilnItemsEquipResult& r : done) {
            auto it = g_callbacks.find(r.ticket);
            if (it == g_callbacks.end()) {
                cbs.push_back(Callback{});
            } else {
                cbs.push_back(it->second);
                g_callbacks.erase(it);
            }
        }
    }
    for (size_t i = 0; i < done.size(); ++i) {
        const KilnItemsEquipResult& r = done[i];
        items_log(KILN_LOG_INFO,
                  "equip ticket %llu%s: %s player %u slot %u -> status %d (game error 0x%02x), item 0x%08x pide %u, from "
                  "%u:%u to %u:%u, replaced 0x%08x, %u step(s)%s%s",
                  static_cast<unsigned long long>(r.ticket), (r.flags & KILN_ITEMS_EQUIP_DRY_RUN) ? " (dry run)" : "",
                  (r.flags & KILN_ITEMS_EQUIP_UNEQUIP) ? "unequip" : "equip", r.player, r.equipment_slot, r.status,
                  r.game_error, r.item, r.pide, KILN_SLOT_INVENTORY(r.from), KILN_SLOT_INDEX(r.from),
                  KILN_SLOT_INVENTORY(r.to), KILN_SLOT_INDEX(r.to), r.replaced, r.steps, r.message[0] ? ": " : "",
                  r.message);
        unsigned long code = 0;
        if (cbs[i].fn && !guarded_done(cbs[i].fn, &r, cbs[i].user, &code))
            LOGE("an equip result callback (owner %llu) faulted (0x%08lx); ignored",
                 static_cast<unsigned long long>(cbs[i].owner), code);
    }
}

void equip_world_closed() {
    {
        std::lock_guard lock(g_mu);
        const uint64_t step = current_step();
        for (const Request& q : g_pending) {
            KilnItemsEquipResult r = result_of(q, step);
            fail(&r, KILN_ITEMS_DROPPED, "the server world closed");
            g_done.push_back(r);
        }
        g_pending.clear();
        g_pending_n.store(0, std::memory_order_release);
    }
    int expected = KILN_ITEMS_PROGRAM_RUNNING;
    if (g_state.compare_exchange_strong(expected, KILN_ITEMS_PROGRAM_ACTIVE))
        set_detail("program registered, not run yet in this world");
}

KilnResult equip_submit(KilnOwner owner, const KilnItemsEquipRequest* r, KilnItemsEquipDoneFn done, void* user,
                        uint64_t* out_ticket) {
    if (out_ticket) *out_ticket = 0;
    if (!r || r->size < sizeof(KilnItemsEquipRequest) || !equip_request_valid(*r)) return KILN_ERR_INVALID_ARGUMENT;
    const int state = g_state.load();
    if (!E.equip_ok || !g_w || !g_tx || !g_players || state == KILN_ITEMS_PROGRAM_NONE || state == KILN_ITEMS_PROGRAM_BROKEN ||
        no_server_world_here())
        return KILN_ERR_UNAVAILABLE;
    Request q{};
    if (slot_player(r->player)) {
        // no player in game with the id: the request comes back NO_INVENTORY at the next Pre
        q.session = true;
        session_of(r->player, &q.handle, &q.joined_step);
    }
    q.ticket = items_next_ticket();
    q.owner = owner;
    q.r = *r;
    q.r.size = sizeof(KilnItemsEquipRequest);
    q.submitted = current_step();
    {
        std::lock_guard lock(g_mu);
        if (g_pending.size() >= kMaxPending) return KILN_ERR_BUFFER_TOO_SMALL;  // a refusal for now, not a missing capability
        g_pending.push_back(q);
        if (done) g_callbacks.emplace(q.ticket, Callback{done, user, owner});
        g_pending_n.store(static_cast<uint32_t>(g_pending.size()), std::memory_order_release);
    }
    if (out_ticket) *out_ticket = q.ticket;
    return KILN_OK;
}

KilnResult equip_status(KilnItemsEquipStatus* out) {
    if (!out || out->size < sizeof(KilnItemsEquipStatus)) return KILN_ERR_INVALID_ARGUMENT;
    std::memset(out, 0, sizeof *out);
    out->size = sizeof *out;
    const int state = g_state.load();
    out->program = static_cast<uint8_t>(state);
    const bool remote = no_server_world_here();
    out->available = E.equip_ok && g_w && g_tx && g_players && !remote &&
                     (state == KILN_ITEMS_PROGRAM_PENDING || state == KILN_ITEMS_PROGRAM_ACTIVE ||
                      state == KILN_ITEMS_PROGRAM_RUNNING);
    out->done = g_changed.load();
    out->refused = g_refused.load();
    std::lock_guard lock(g_mu);
    out->pending = static_cast<uint32_t>(g_pending.size());
    std::snprintf(out->detail, sizeof out->detail, "%s",
                  out->available ? "" : remote ? "no server world in this process (a game client in its menu or joined to another machine's world): equip runs where the world runs"
                                              : g_detail);
    return KILN_OK;
}

bool equip_cancel(uint64_t ticket) {
    std::lock_guard lock(g_mu);
    return g_callbacks.erase(ticket) != 0;
}

void equip_cancel_owner(KilnOwner owner) {
    std::lock_guard lock(g_mu);
    for (auto it = g_callbacks.begin(); it != g_callbacks.end();)
        it = it->second.owner == owner ? g_callbacks.erase(it) : std::next(it);
}

}  // namespace kitems
