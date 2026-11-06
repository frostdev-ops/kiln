// SPDX-License-Identifier: MIT
// kiln.playerstate inside kiln-players.dll (kiln_playerstate.h): what a player is doing, read from the game's components.
//
//  - A "source" is one field of a component plus the game enum that names its bits or values. Both come from the game's
//    reflection (TypeByHash) on the running build: nothing here depends on an offset compiled into Kiln, and a state
//    the game adds shows up with its own name. A source that does not resolve reads as unknown.
//  - Reads run on the world's own thread with no sim step running (the managed pump calls them in Pre): every field
//    read is guarded, a fault makes that entity read as unknown, the server runs on.
//  - Stopping an action calls the game's own reset (0x34040 on server 26430253), what actor_simulation_start does to a
//    sequence whose keep-alive failed. Evidence: docs/re/player-state.md.
//  - A client action can only be refused before the game's program runs it, and the game copies a client's input in
//    inside the sim step, after Pre: so while a listener asks for it, new versions are held for one step at the start
//    of DeliverEvents, the last call before the programs ("holding client actions" below).
#include <windows.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cctype>
#include <cstdarg>
#include <cstdio>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>
#include <string_view>
#include <vector>

#include "kiln_api.h"
#include "kiln_playerstate.h"
#include "playerstate.h"
#include "safetyhook.hpp"
#include "services/kiln_build.h"
#include "services/kiln_client.h"

namespace kiln::playerstate {
namespace {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
const KilnBuildApi* g_build = nullptr;
const KilnClientApi* g_client = nullptr;

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
#define PSI(...) logf(KILN_LOG_INFO, "playerstate: " __VA_ARGS__)
#define PSW(...) logf(KILN_LOG_WARN, "playerstate: " __VA_ARGS__)

KilnStr kstr(const char* s) { return KilnStr{s, std::strlen(s)}; }

constexpr uint32_t fnv1a(std::string_view s, uint32_t h = 0x811c9dc5u) {
    for (unsigned char c : s) h = (h ^ c) * 0x1000193u;
    return h;
}

bool seh_copy(const void* from, void* to, size_t n) {
    __try {
        std::memcpy(to, from, n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
template <class T>
bool rd(uintptr_t a, T& v) {
    return a && seh_copy(reinterpret_cast<const void*>(a), &v, sizeof v);
}

// ------------------------------------------------------------------------------------------- the game's functions ----
using TypeByHashFn = uintptr_t (*)(uint32_t);
using GetComponentFn = void (*)(void* out, uintptr_t world, uint32_t hash, uint32_t entity);
using GetTransformFn = const int64_t* (*)(uintptr_t world, uint32_t entity);
using ItemFindFn = const uint8_t* (*)(uint32_t id, void* registry);
using BuffFindFn = const uint8_t* (*)(uint32_t id, void* registry);
using SubSequenceFn = const uint8_t* (*)(void* registry, uint32_t sequence, uint8_t sub);
using ActionResetFn = void (*)(void* actor);
struct EntityTime {
    uint32_t entity, pad;
    int64_t time;
};
using TriggerFn = void* (*)(void* out, void* actor_input, const EntityTime* who, uint32_t sequence, uint16_t trigger, uint8_t input);

struct Fns {
    TypeByHashFn type_by_hash = nullptr;
    GetComponentFn get_component = nullptr;
    GetTransformFn get_transform = nullptr;
    ItemFindFn item_find = nullptr;
    BuffFindFn buff_find = nullptr;
    SubSequenceFn sub_sequence = nullptr;
    ActionResetFn action_reset = nullptr;
    TriggerFn trigger = nullptr;
} F;

struct TypeLayout {
    int64_t qname = 32, inner = 56, size = 64, count = 72, kind = 76, fields = 88, enums = 96;
    int64_t field_stride = 48, field_type = 16, field_offset = 24, enum_stride = 40, enum_value = 16;
} T;
int64_t g_clock = -1, g_period = -1, g_storage = -1;

enum : uint32_t { K_ENUM = 0x0C, K_TYPEDEF = 0x11, K_STRUCT = 0x12 };

// ----------------------------------------------------------------------------------------------- reflection ----
struct Rec {
    uintptr_t p = 0;
    uint32_t size = 0, count = 0, kind = 0;
    uintptr_t inner = 0, fields = 0, enums = 0;
};

bool load(uintptr_t p, Rec* r) {
    uint32_t kind = 0;
    if (!p || !rd(p + T.size, r->size) || !rd(p + T.count, r->count) || !rd(p + T.kind, kind) ||
        !rd(p + T.inner, r->inner) || !rd(p + T.fields, r->fields) || !rd(p + T.enums, r->enums))
        return false;
    r->p = p;
    r->kind = kind & 0xFF;
    return r->size < (1u << 24) && r->count < 4096;
}

bool resolve(uintptr_t p, Rec* r) {
    for (int i = 0; i < 8; ++i) {
        if (!load(p, r)) return false;
        if (r->kind != K_TYPEDEF) return true;
        p = r->inner;
    }
    return false;
}

std::string text(uintptr_t p, uint64_t n) {
    if (!p || n == 0 || n > 256) return {};
    std::string s(static_cast<size_t>(n), '\0');
    return seh_copy(reinterpret_cast<const void*>(p), s.data(), s.size()) ? s : std::string{};
}

uintptr_t guarded_type(uint32_t hash) {
    __try {
        return F.type_by_hash ? F.type_by_hash(hash) : 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}
uintptr_t type_named(std::string_view name) { return guarded_type(fnv1a(name)); }

// One field of a struct, base classes included (a struct's inner record is its base).
bool find_field(uintptr_t type, std::string_view name, int64_t* off, uintptr_t* ftype) {
    for (int depth = 0; depth < 6 && type; ++depth) {
        Rec r;
        if (!resolve(type, &r) || r.kind != K_STRUCT) return false;
        for (uint32_t i = 0; i < r.count; ++i) {
            const uintptr_t f = r.fields + static_cast<uintptr_t>(i) * static_cast<uintptr_t>(T.field_stride);
            uintptr_t np = 0, tp = 0;
            uint64_t nl = 0, o = 0;
            if (!rd(f, np) || !rd(f + 8, nl) || !rd(f + T.field_type, tp) || !rd(f + T.field_offset, o)) return false;
            if (text(np, nl) == name) {
                *off = static_cast<int64_t>(o);
                *ftype = tp;
                return true;
            }
        }
        type = r.inner;
    }
    return false;
}

struct Field {
    int64_t off = -1;
    uint32_t width = 0;
    uintptr_t type = 0;
    explicit operator bool() const { return off >= 0; }
};

// "currentAction.triggerContext.usedItem.itemId" inside a type.
Field path_in(uintptr_t type, std::string_view path) {
    Field out;
    int64_t base = 0;
    while (type) {
        const size_t dot = path.find('.');
        const std::string_view head = path.substr(0, dot);
        int64_t off = 0;
        uintptr_t ft = 0;
        if (!find_field(type, head, &off, &ft)) return Field{};
        base += off;
        if (dot == std::string_view::npos) {
            Rec r;
            if (!resolve(ft, &r)) return Field{};
            out.off = base;
            out.width = r.size;
            out.type = ft;
            return out;
        }
        type = ft;
        path = path.substr(dot + 1);
    }
    return Field{};
}

Field field_of(std::string_view type, std::string_view path) { return path_in(type_named(type), path); }

// ----------------------------------------------------------------------------------------------- components ----
enum Comp : uint32_t {
    C_ACTOR, C_LOCO, C_PSTATE, C_PINPUT, C_AINPUT, C_CLIMB, C_HOOK, C_RANGED, C_FISH, C_COOK, C_ENV, C_ATTR, C_NETACTOR,
    C_SLOTS, C_BUFFS, C_INV, C_BUFFNET, C_CONSUMED, C_COUNT,
};
constexpr std::array<const char*, C_COUNT> kCompNames = {
    "keen::ecs::Actor", "keen::ecs::DynamicLocomotion", "keen::ecs::PlayerState", "keen::ecs::PlayerInput",
    "keen::ecs::ActorInput", "keen::ecs::Climb", "keen::ecs::HookShot", "keen::ecs::NetworkRangedWeapon",
    "keen::ecs::NetworkFishing", "keen::ecs::NetworkCookingData", "keen::ecs::EnvironmentSensor",
    "keen::ecs::NetworkPlayerAttributes", "keen::ecs::NetworkActor", "keen::ecs::SlotSelection",
    "keen::ecs::BuffContainer", "keen::ecs::Inventory", "keen::ecs::BuffNetworkData", "keen::ecs::ServerConsumedPlayerInput",
};
std::array<uint32_t, C_COUNT> g_comp_hash{};

struct Source {
    uint32_t id;
    Comp comp;
    const char* path;
    const char* enum_type;
    uint32_t kind;  // 0 enum, 1 mask
    uint32_t tier;
    Field field{};
    bool resolved = false;
    struct Entry {
        uint64_t value;
        std::string name;
    };
    std::vector<Entry> entries{};
};

constexpr uint32_t kActor = KILN_PLAYERSTATE_WANT_ACTOR, kMotion = KILN_PLAYERSTATE_WANT_MOTION, kInput = KILN_PLAYERSTATE_WANT_INPUT,
                   kExtra = KILN_PLAYERSTATE_WANT_EXTRA;
Source g_sources[] = {
    {1, C_ACTOR, "currentState", "keen::actor::StateFlag", 1, kActor},
    {2, C_LOCO, "state", "keen::ecs::LocomotionState", 0, kMotion},
    {3, C_PSTATE, "playerStateMask", "keen::ecs::PlayerFlags", 1, kMotion},
    {4, C_LOCO, "flags", "keen::ecs::LocomotionFlags", 1, kMotion},
    {5, C_PINPUT, "fromClient.mask", "keen::ecs::ClientPlayerFlags", 1, kInput},
    {6, C_ACTOR, "currentAbilityMask", "keen::actor::AbilityFlag", 1, kActor},
    {7, C_PINPUT, "fromClient.digitalInput", "keen::ecs::PlayerInputType", 1, kInput},
    {8, C_ACTOR, "currentAction.triggerContext.triggerType", "keen::actor::ActionSequenceTrigger", 0, kActor},
    {9, C_LOCO, "jumpType", "keen::actor::JumpType", 0, kMotion},
    {10, C_AINPUT, "lastLocomotionSpeed", "keen::ecs::LocomotionSpeedSelector", 0, kInput},
    {11, C_ACTOR, "currentColliderMode", "keen::ecs::ColliderMode", 0, kActor},
    {12, C_CLIMB, "climbState", "keen::ecs::ClimbState", 0, kExtra},
    {13, C_CLIMB, "climbType", "keen::ecs::ClimbType", 0, kExtra},
    {14, C_HOOK, "hookShotState", "keen::ecs::HookShotState", 0, kExtra},
    {15, C_HOOK, "hookShotType", "keen::ecs::HookShotType", 0, kExtra},
    {16, C_RANGED, "currentState", "keen::ranged_weapon::State", 0, kExtra},
    {17, C_FISH, "state", "keen::fishing::State", 0, kExtra},
    {18, C_COOK, "mask", "keen::ecs::CookingFlags", 1, kExtra},
    {19, C_ATTR, "revivieData.state", "keen::ecs::ReviveState", 0, kExtra},
    {20, C_ENV, "fogType", "keen::FogType", 0, kExtra},
    {21, C_ENV, "temperature", "keen::TemperatureVoxelType", 0, kExtra},
};
constexpr uint32_t kSourceCount = sizeof(g_sources) / sizeof(g_sources[0]);

struct Fields {
    // Actor
    Field pb_state, pb_set, pb_unset, pb_played, pb_length, pb_sub;
    Field seq_id, seq_time, seq_call;
    Field input, used_slot, used_item, used_pide, ammo_item, cost_item, host;
    // DynamicLocomotion, PlayerState, PlayerInput
    Field jump_count, last_jump, water, can_swim, can_dive, glider_time;
    // NetworkActor
    Field na_state, na_set, na_unset, na_seq, na_seq_time, na_seq_call, na_item, na_trigger;
    // NetworkPlayerAttributes
    Field v_health, v_health_max, v_stamina, v_stamina_max, v_mana, v_mana_max, v_heat, v_heat_max, v_special, v_special_max,
        v_fog, v_fog_max, v_oxygen, v_oxygen_max, v_level, v_comfort;
    // SlotSelection
    Field slot_equipment, slot_held_slot, slot_held_item, slot_hotbar, slot_bars;
    // BuffContainer, BuffNetworkData
    Field b_food, b_buff, b_debuff, b_rested, b_terrain, bn_life, bn_end, bn_level;
    // ActorInput
    Field ctx_used_slot, ctx_used_item;
    // game data
    Field item_category, item_ammo, item_slot, item_buff, item_name, buff_slot, sub_name;
};
Fields L;
std::vector<std::string> g_equip_names;

struct ClientAction {
    std::string name;
    int64_t offset = -1, version = -1, consumed = -1;  // in PlayerInput, PlayerInput, ServerConsumedPlayerInput
    uint32_t bytes = 0;
};
std::vector<ClientAction> g_client_actions;
std::array<int32_t, KILN_PLAYERSTATE_CLIENT_ACTIONS> g_client_action_versions{};  // the version offsets, for the guarded loop

std::once_flag g_once;
std::atomic<bool> g_ready{false};
bool g_reflection = false, g_cancel = false, g_names = false, g_items = false, g_lab = false;
bool g_hold_ok = false;  // the hold point is hooked: client actions can be held and canceled
uint32_t g_resolved = 0;
std::string g_detail;
std::atomic<uint64_t> g_scans{0}, g_entities{0};

void add_detail(const std::string& s) {
    if (g_detail.size() < 150) g_detail += (g_detail.empty() ? "" : "; ") + s;
}

template <class T>
T rd_field(const uint8_t* p, const Field& f, T fallback = T{}) {
    if (!p || f.off < 0) return fallback;
    T v = fallback;
    std::memcpy(&v, p + f.off, sizeof v < f.width ? sizeof v : f.width);
    return v;
}

// Enum entries by the type's own table. A bitmask's enum holds bit numbers.
void load_enum(Source& s) {
    const uintptr_t t = type_named(s.enum_type);
    Rec r;
    if (!t || !resolve(t, &r) || r.kind != K_ENUM || !r.enums) {
        PSW("source %u: enum %s is not in the game's reflection on this build: its states read unknown", s.id, s.enum_type);
        add_detail(std::string("no enum ") + s.enum_type);
        return;
    }
    s.entries.reserve(r.count);
    for (uint32_t i = 0; i < r.count; ++i) {
        const uintptr_t e = r.enums + static_cast<uintptr_t>(i) * static_cast<uintptr_t>(T.enum_stride);
        uintptr_t np = 0;
        uint64_t nl = 0, value = 0;
        if (!rd(e, np) || !rd(e + 8, nl) || !rd(e + T.enum_value, value)) continue;
        std::string name = text(np, nl);
        if (name.empty()) continue;
        s.entries.push_back({value, std::move(name)});
    }
    s.resolved = s.field && !s.entries.empty();
    if (!s.field) {
        PSW("source %u: %s.%s is not in the game's reflection on this build: its states read unknown", s.id, kCompNames[s.comp], s.path);
        add_detail(std::string("no field ") + s.path);
    }
}

// The actions a client sends: every field of the two input structs with a versionData field. Their consumed version in
// ServerConsumedPlayerInput is named after them, except for three admin ones.
void collect_client_actions(const char* group) {
    const uintptr_t pi = type_named("keen::ecs::PlayerInput");
    const uintptr_t scpi = type_named("keen::ecs::ServerConsumedPlayerInput");
    int64_t group_off = 0;
    uintptr_t group_type = 0;
    if (!find_field(pi, group, &group_off, &group_type)) return;
    Rec r;
    if (!resolve(group_type, &r) || r.kind != K_STRUCT) return;
    for (uint32_t i = 0; i < r.count && g_client_actions.size() < KILN_PLAYERSTATE_CLIENT_ACTIONS; ++i) {
        const uintptr_t f = r.fields + static_cast<uintptr_t>(i) * static_cast<uintptr_t>(T.field_stride);
        uintptr_t np = 0, tp = 0;
        uint64_t nl = 0, off = 0;
        if (!rd(f, np) || !rd(f + 8, nl) || !rd(f + T.field_type, tp) || !rd(f + T.field_offset, off)) continue;
        const std::string name = text(np, nl);
        int64_t ver = 0;
        uintptr_t vt = 0;
        Rec fr;
        if (name.empty() || !resolve(tp, &fr) || fr.kind != K_STRUCT || !find_field(tp, "versionData", &ver, &vt)) continue;
        ClientAction a;
        a.name = name;
        a.offset = group_off + static_cast<int64_t>(off);
        a.version = a.offset + ver;
        a.bytes = fr.size;
        std::string consumed = "consumed" + name;
        consumed[8] = static_cast<char>(std::toupper(static_cast<unsigned char>(consumed[8])));
        if (name == "adminSpawnCommand") consumed = "consumedAdminSpawnAction";
        else if (name == "adminKillFaction") consumed = "consumedAdminKillFactionAction";
        else if (name == "adminDebugEnemyAction") consumed = "consumedDebugEnemyAction";
        int64_t c = 0;
        uintptr_t ct = 0;
        if (scpi && find_field(scpi, consumed, &c, &ct)) a.consumed = c;
        g_client_actions.push_back(std::move(a));
    }
}

void resolve_all() {
    for (uint32_t i = 0; i < C_COUNT; ++i) g_comp_hash[i] = fnv1a(kCompNames[i]);
    if (!F.type_by_hash || !F.get_component) {
        add_detail(!F.type_by_hash ? "the game's TypeByHash did not resolve" : "GetComponent did not resolve");
        PSW("%s on this build: every player state reads unknown, no player state event fires", g_detail.c_str());
        return;
    }
    if (!type_named("keen::ecs::Actor")) {
        add_detail("keen::ecs::Actor is not in the game's reflection");
        PSW("%s: every player state reads unknown, no player state event fires", g_detail.c_str());
        return;
    }
    for (Source& s : g_sources) {
        s.field = field_of(kCompNames[s.comp], s.path);
        load_enum(s);
        if (s.resolved) ++g_resolved;
    }
    g_reflection = g_resolved > 0;

    const auto ac = [](const char* p) { return field_of("keen::ecs::Actor", p); };
    L.pb_state = ac("currentAction.playbackContext.state");
    L.pb_set = ac("currentAction.playbackContext.setStateMask");
    L.pb_unset = ac("currentAction.playbackContext.unsetStateMask");
    L.pb_played = ac("currentAction.playbackContext.playedTime");
    L.pb_length = ac("currentAction.playbackContext.sequenceLength");
    L.pb_sub = ac("currentAction.playbackContext.subSequenceId.index");
    L.seq_id = ac("sequenceRuntimeId.sequenceId");
    L.seq_time = ac("sequenceRuntimeId.triggerTime");
    L.seq_call = ac("sequenceRuntimeId.callIndex");
    L.input = ac("currentAction.triggerContext.inputType");
    L.used_slot = ac("currentAction.triggerContext.usedItem.slotId");
    L.used_item = ac("currentAction.triggerContext.usedItem.itemId");
    L.used_pide = ac("currentAction.triggerContext.usedItem.pide");
    L.ammo_item = ac("currentAction.triggerContext.usedAmmunition.itemId");
    L.cost_item = ac("currentAction.triggerContext.costItem.itemId");
    L.host = ac("currentAction.triggerContext.interactionHostId");
    const auto lo = [](const char* p) { return field_of("keen::ecs::DynamicLocomotion", p); };
    L.jump_count = lo("jumpCount");
    L.last_jump = lo("lastJumpTime");
    L.water = lo("currentWaterState.waterDepth");
    L.can_swim = lo("canSwim");
    L.can_dive = lo("canDive");
    L.glider_time = field_of("keen::ecs::PlayerState", "gliderActivationTime");
    const auto na = [](const char* p) { return field_of("keen::ecs::NetworkActor", p); };
    L.na_state = na("currentState");
    L.na_set = na("setStateMask");
    L.na_unset = na("unsetStateMask");
    L.na_seq = na("sequenceRuntimeId.sequenceId");
    L.na_seq_time = na("sequenceRuntimeId.triggerTime");
    L.na_seq_call = na("sequenceRuntimeId.callIndex");
    L.na_item = na("usedItemId");
    L.na_trigger = na("triggerType");
    const auto at = [](const char* p) { return field_of("keen::ecs::NetworkPlayerAttributes", p); };
    L.v_health = at("health");
    L.v_health_max = at("healthMax");
    L.v_stamina = at("stamina");
    L.v_stamina_max = at("staminaMax");
    L.v_mana = at("mana");
    L.v_mana_max = at("manaMax");
    L.v_heat = at("bodyHeat");
    L.v_heat_max = at("bodyHeatMax");
    L.v_special = at("specialAbility");
    L.v_special_max = at("specialAbilityMax");
    L.v_fog = at("fogResistance");
    L.v_fog_max = at("fogResistanceMax");
    L.v_oxygen = at("oxygen");
    L.v_oxygen_max = at("oxygenMax");
    L.v_level = at("level");
    L.v_comfort = at("comfortLevel");
    const auto sl = [](const char* p) { return field_of("keen::ecs::SlotSelection", p); };
    L.slot_equipment = sl("equipment");
    L.slot_held_slot = sl("actionItem.slotId");
    L.slot_held_item = sl("actionItem.itemId");
    L.slot_hotbar = sl("actionbarSlotSelection.index");
    L.slot_bars = sl("actionbarEntityIds");
    if (L.slot_equipment) {
        Rec r;
        if (resolve(L.slot_equipment.type, &r) && r.kind == K_STRUCT)
            for (uint32_t i = 0; i < r.count && i < KILN_PLAYERSTATE_SLOTS; ++i) {
                const uintptr_t f = r.fields + static_cast<uintptr_t>(i) * static_cast<uintptr_t>(T.field_stride);
                uintptr_t np = 0;
                uint64_t nl = 0;
                if (rd(f, np) && rd(f + 8, nl)) g_equip_names.push_back(text(np, nl));
            }
    }
    const auto bc = [](const char* p) { return field_of("keen::ecs::BuffContainer", p); };
    L.b_food = bc("foodBuffs");
    L.b_buff = bc("genericBuffs");
    L.b_debuff = bc("genericDebuffs");
    L.b_rested = bc("restedBuff");
    L.b_terrain = bc("terrainDebuff");
    L.bn_life = field_of("keen::ecs::BuffNetworkData", "lifeTime");
    L.bn_end = field_of("keen::ecs::BuffNetworkData", "endOfLife");
    L.bn_level = field_of("keen::ecs::BuffNetworkData", "level");
    collect_client_actions("fromClient");
    collect_client_actions("fromAdminClient");
    for (size_t i = 0; i < g_client_actions.size(); ++i) g_client_action_versions[i] = static_cast<int32_t>(g_client_actions[i].version);
    L.ctx_used_slot = field_of("keen::ecs::ActorInput", "triggerContext.usedItem.slotId");
    L.ctx_used_item = field_of("keen::ecs::ActorInput", "triggerContext.usedItem.itemId");
    L.item_category = field_of("keen::ItemInfo", "category");
    L.item_ammo = field_of("keen::ItemInfo", "ammunitionType");
    L.item_slot = field_of("keen::ItemInfo", "equipment.slot");
    L.item_buff = field_of("keen::ItemInfo", "equipment.appliedBuff");
    L.item_name = field_of("keen::ItemInfo", "debugName");
    L.buff_slot = field_of("keen::BuffType", "slot");
    L.sub_name = field_of("keen::actor::ActorSubSequenceResource", "name");

    g_cancel = F.action_reset && L.seq_id && L.seq_time;
    g_names = F.sub_sequence && L.sub_name;
    g_items = F.item_find && L.item_name && L.item_category;
    if (!g_cancel) {
        add_detail("the game's action reset did not resolve");
        PSW("the game's action reset did not resolve on this build: player actions can be seen but not canceled");
    }
    if (!g_names) PSW("the game's sequence lookup did not resolve on this build: action names read empty");
    if (!g_items) PSW("item details did not resolve on this build: event items carry only their id");
    if (!g_hold_ok) PSW("the point before the game's programs did not resolve on this build: client actions can be seen but not canceled");
    PSI("%u of %u state sources from the game's reflection, %u client actions (%s), actions %s, sequence names %s, item details %s", g_resolved,
        kSourceCount, static_cast<unsigned>(g_client_actions.size()), g_hold_ok ? "cancelable" : "observe only", g_cancel ? "cancelable" : "observe only",
        g_names ? "yes" : "no", g_items ? "yes" : "no");
}

void ensure() {
    std::call_once(g_once, [] {
        resolve_all();
        g_ready.store(true);
    });
}

// ------------------------------------------------------------------------------------------------- worlds ----
struct Worlds {
    uintptr_t server = 0, prediction = 0, client = 0;
    KilnEntity local = 0;
    bool any() const { return server || client; }
};

// Which worlds this thread may read. The server world's thread reads only the server world, also on a host.
Worlds worlds_here() {
    Worlds w;
    if (g_api->on_world_thread(KILN_WORLD_SERVER)) {
        w.server = g_api->world(KILN_WORLD_SERVER);
        return w;
    }
    if (g_api->on_world_thread(KILN_WORLD_CLIENT)) {
        w.client = g_api->world(KILN_WORLD_CLIENT);
        w.prediction = g_api->world(KILN_WORLD_PREDICTION);
        if (g_client) {
            KilnClientState s{};
            s.size = sizeof s;
            if (g_client->state(&s) == KILN_OK) w.local = s.local_player;
        }
    }
    return w;
}

struct PtrSize {
    const uint8_t* p;
    uint64_t size;
};

const uint8_t* comp_of(uintptr_t world, Comp c, uint32_t entity, uint64_t* size = nullptr) {
    alignas(16) uint8_t out[48] = {};
    __try {
        F.get_component(out, world, g_comp_hash[c], entity);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
    PtrSize ps;
    std::memcpy(&ps, out, sizeof ps);
    if (size) *size = ps.size;
    return ps.size ? ps.p : nullptr;
}

int64_t ecs_now(uintptr_t world) {
    if (!world || g_storage < 0 || g_clock < 0 || g_period < 0) return 0;
    __try {
        const uintptr_t s = world + static_cast<uintptr_t>(g_storage);
        const int64_t period = *reinterpret_cast<const int64_t*>(s + g_period);
        const uint32_t tick = *reinterpret_cast<const uint32_t*>(s + g_clock);
        return period * static_cast<int64_t>(tick);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

// ---- reading one player ----
// All game memory goes through one guard: a fault leaves known as it was, and the fields read so far stand.
void read_authoritative(uintptr_t world, KilnEntity e, uint32_t want, KilnPlayerSnapshot* s) {
    const uint8_t* parts[C_COUNT] = {};
    for (const Source& src : g_sources) {
        if (!src.resolved || !(src.tier & want) || parts[src.comp]) continue;
        parts[src.comp] = comp_of(world, src.comp, e);
    }
    __try {
        for (const Source& src : g_sources) {
            const uint8_t* p = parts[src.comp];
            if (!src.resolved || !p || !(src.tier & want)) continue;
            uint64_t v = rd_field<uint64_t>(p, src.field);
            if (src.id == 1 && (rd_field<uint8_t>(p, L.pb_state) & 1) != 0)  // the running sequence's own state bits
                v = (v | rd_field<uint64_t>(p, L.pb_set)) & ~rd_field<uint64_t>(p, L.pb_unset);
            s->values[src.id] = v;
            s->known |= 1u << src.id;
        }
        if (const uint8_t* a = parts[C_ACTOR]) {
            KilnPlayerAction& ac = s->action;
            ac.sequence = rd_field<uint32_t>(a, L.seq_id);
            ac.trigger_time = rd_field<int64_t>(a, L.seq_time);
            ac.call_index = rd_field<uint8_t>(a, L.seq_call);
            ac.trigger = static_cast<uint16_t>(s->values[8]);
            ac.input = rd_field<uint8_t>(a, L.input);
            ac.played_ns = rd_field<int64_t>(a, L.pb_played);
            ac.length_ns = rd_field<int64_t>(a, L.pb_length);
            ac.sub_index = rd_field<uint8_t>(a, L.pb_sub);
            ac.used_slot = rd_field<uint64_t>(a, L.used_slot);
            ac.used_item = rd_field<uint32_t>(a, L.used_item);
            ac.used_pide = rd_field<uint32_t>(a, L.used_pide);
            ac.ammo_item = rd_field<uint32_t>(a, L.ammo_item);
            ac.cost_item = rd_field<uint32_t>(a, L.cost_item);
            // GameObjectId {u64 value; u8 type}: type 0 is an entity
            if (L.host && a[L.host.off + 8] == 0) ac.host = rd_field<uint32_t>(a, L.host);
            s->has_action = L.seq_id ? 1 : 0;
        }
        if (const uint8_t* l = parts[C_LOCO]) {
            s->jump_count = rd_field<uint8_t>(l, L.jump_count);
            s->last_jump_time = rd_field<int64_t>(l, L.last_jump);
            s->water_depth = rd_field<float>(l, L.water);
            s->can_swim = rd_field<uint8_t>(l, L.can_swim);
            s->can_dive = rd_field<uint8_t>(l, L.can_dive);
            s->has_jump = L.jump_count ? 1 : 0;
        }
        if (const uint8_t* ps = parts[C_PSTATE]) s->glider_time = rd_field<int64_t>(ps, L.glider_time);
        if (const uint8_t* pi = parts[C_PINPUT]) {
            const size_t n = g_client_actions.size();
            for (size_t i = 0; i < n; ++i) std::memcpy(&s->client_versions[i], pi + g_client_action_versions[i], 4);
            s->known |= 1u << KILN_PLAYERSTATE_KNOWN_CLIENT_ACTIONS;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        s->known = 0;
        s->has_action = s->has_jump = 0;
    }
}

// What a joined client has for a player it does not predict: the replicated actor. The actor flags and the running
// action are there; the locomotion state is derived the way the game derives it from those flags (0x1c5180).
void read_replicated(uintptr_t world, KilnEntity e, KilnPlayerSnapshot* s) {
    const uint8_t* n = comp_of(world, C_NETACTOR, e);
    if (!n) return;
    __try {
        const uint64_t v = (rd_field<uint64_t>(n, L.na_state) | rd_field<uint64_t>(n, L.na_set)) & ~rd_field<uint64_t>(n, L.na_unset);
        s->values[1] = v;
        s->known |= 1u << 1;
        s->values[8] = rd_field<uint16_t>(n, L.na_trigger);
        s->known |= 1u << 8;
        KilnPlayerAction& ac = s->action;
        ac.sequence = rd_field<uint32_t>(n, L.na_seq);
        ac.trigger_time = rd_field<int64_t>(n, L.na_seq_time);
        ac.call_index = rd_field<uint8_t>(n, L.na_seq_call);
        ac.trigger = static_cast<uint16_t>(s->values[8]);
        ac.used_item = rd_field<uint32_t>(n, L.na_item);
        s->has_action = 1;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        s->known = 0;
        s->has_action = 0;
    }
}

void read_vitals(uintptr_t world, KilnEntity e, KilnPlayerSnapshot* s) {
    const uint8_t* a = comp_of(world, C_ATTR, e);
    if (!a || !L.v_health) return;
    __try {
        s->health = rd_field<uint16_t>(a, L.v_health);
        s->health_max = rd_field<uint16_t>(a, L.v_health_max);
        s->stamina = rd_field<uint16_t>(a, L.v_stamina);
        s->stamina_max = rd_field<uint16_t>(a, L.v_stamina_max);
        s->mana = rd_field<uint16_t>(a, L.v_mana);
        s->mana_max = rd_field<uint16_t>(a, L.v_mana_max);
        s->body_heat = rd_field<uint16_t>(a, L.v_heat);
        s->body_heat_max = rd_field<uint16_t>(a, L.v_heat_max);
        s->special = rd_field<uint16_t>(a, L.v_special);
        s->special_max = rd_field<uint16_t>(a, L.v_special_max);
        s->fog = rd_field<uint16_t>(a, L.v_fog);
        s->fog_max = rd_field<uint16_t>(a, L.v_fog_max);
        s->oxygen = rd_field<uint16_t>(a, L.v_oxygen);
        s->oxygen_max = rd_field<uint16_t>(a, L.v_oxygen_max);
        s->level = rd_field<uint16_t>(a, L.v_level);
        s->comfort = rd_field<uint8_t>(a, L.v_comfort);
        s->has_vitals = 1;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        s->has_vitals = 0;
    }
}

// How many buffs of each kind: the slot arrays of BuffContainer (a server-only component). thirst_and_hunger (0x5ddd0)
// keeps its own marker in a food slot while the player has eaten nothing; it is not food.
constexpr uint32_t kHungerMarker = 0x89cb0b34u;

void read_buff_counts(uintptr_t world, KilnEntity e, KilnPlayerSnapshot* s) {
    const uint8_t* p = comp_of(world, C_BUFFS, e);
    if (!p || !L.b_food) return;
    const auto count = [p, s](const Field& f, uint32_t n) -> uint8_t {
        if (!f) return 0;
        uint8_t c = 0;
        for (uint32_t i = 0; i < n; ++i) {
            uint32_t id = 0;
            if (!seh_copy(p + f.off + i * 12ull + 4, &id, 4) || !id) continue;
            if (id == kHungerMarker) s->hungry = 1;
            else ++c;
        }
        return c;
    };
    s->food_buffs = count(L.b_food, 4);
    s->buffs = count(L.b_buff, 16);
    s->debuffs = count(L.b_debuff, 16);
    s->rested_buff = count(L.b_rested, 1);
    s->terrain_debuff = count(L.b_terrain, 1);
    s->has_buffs = 1;
}

// In a player base: the game's own build zones through kiln.build, refreshed once per call batch. Server world only.
std::array<KilnBuildBase, 64> g_bases{};
size_t g_base_count = 0;
bool g_bases_ok = false;

void refresh_bases() {
    g_bases_ok = false;
    g_base_count = 0;
    if (!g_build || !g_build->bases) return;
    for (KilnBuildBase& b : g_bases) b.size = sizeof b;
    size_t n = 0;
    const KilnResult rc = g_build->bases(g_bases.data(), g_bases.size(), &n);
    if (rc != KILN_OK && rc != KILN_ERR_BUFFER_TOO_SMALL) return;
    g_base_count = n < g_bases.size() ? n : g_bases.size();
    g_bases_ok = true;
}

bool position_of(uintptr_t world, KilnEntity e, double out[3]) {
    if (!F.get_transform) return false;
    __try {
        const int64_t* t = F.get_transform(world, e);
        if (!t) return false;
        for (int i = 0; i < 3; ++i) out[i] = static_cast<double>(t[i]) / 4294967296.0;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void read_base_zone(uintptr_t world, KilnEntity e, KilnPlayerSnapshot* s) {
    s->in_base = 2;
    double p[3];
    if (!g_bases_ok || !position_of(world, e, p)) return;
    s->in_base = 0;
    for (size_t i = 0; i < g_base_count; ++i) {
        const KilnBuildBase& b = g_bases[i];
        if (std::abs(p[0] - b.center.x) <= b.half.x && std::abs(p[1] - b.center.y) <= b.half.y && std::abs(p[2] - b.center.z) <= b.half.z) {
            s->in_base = 1;
            return;
        }
    }
}

// ------------------------------------------------------------------------------------------------ the table ----
KilnResult KILN_CALL t_status(KilnPlayerStateStatus* out) {
    if (!out || out->size < sizeof *out) return KILN_ERR_INVALID_ARGUMENT;
    ensure();
    out->reflection = g_reflection;
    out->cancel = g_cancel;
    out->names = g_names;
    out->items = g_items;
    out->sources = g_resolved;
    out->client_hold = g_hold_ok;
    out->scans = g_scans.load();
    out->entities_read = g_entities.load();
    std::snprintf(out->detail, sizeof out->detail, "%s", g_detail.c_str());
    return KILN_OK;
}

const Source* source_by_id(uint32_t id) {
    for (const Source& s : g_sources)
        if (s.id == id) return &s;
    return nullptr;
}

KilnResult KILN_CALL t_source_info(uint32_t id, KilnPlayerSourceInfo* out) {
    if (!out || out->size < sizeof *out) return KILN_ERR_INVALID_ARGUMENT;
    ensure();
    const Source* s = source_by_id(id);
    if (!s) return KILN_ERR_NOT_FOUND;
    out->id = s->id;
    out->kind = s->kind;
    out->count = static_cast<uint32_t>(s->entries.size());
    out->tier = s->tier;
    out->resolved = s->resolved;
    std::snprintf(out->component, sizeof out->component, "%s", kCompNames[s->comp]);
    std::snprintf(out->field, sizeof out->field, "%s", s->path);
    std::snprintf(out->enum_type, sizeof out->enum_type, "%s", s->enum_type);
    return KILN_OK;
}

KilnResult copy_text(const std::string& s, char* name, uint32_t capacity, uint32_t* len) {
    if (len) *len = static_cast<uint32_t>(s.size());
    if (!name || capacity == 0) return KILN_OK;
    const size_t n = s.size() < capacity - 1 ? s.size() : capacity - 1;
    std::memcpy(name, s.data(), n);
    name[n] = 0;
    return s.size() < capacity ? KILN_OK : KILN_ERR_BUFFER_TOO_SMALL;
}

KilnResult KILN_CALL t_enum_entry(uint32_t id, uint32_t index, uint64_t* value, char* name, uint32_t capacity, uint32_t* name_len) {
    ensure();
    const Source* s = source_by_id(id);
    if (!s) return KILN_ERR_NOT_FOUND;
    if (index >= s->entries.size()) return KILN_ERR_NOT_FOUND;
    if (value) *value = s->entries[index].value;
    return copy_text(s->entries[index].name, name, capacity, name_len);
}

KilnResult KILN_CALL t_scan(const KilnEntity* entities, uint32_t count, uint32_t want, KilnPlayerSnapshot* out) {
    if (!entities || !out) return KILN_ERR_INVALID_ARGUMENT;
    ensure();
    if (!g_reflection) return KILN_ERR_UNAVAILABLE;
    const Worlds w = worlds_here();
    if (!w.any()) return g_api->on_world_thread(KILN_WORLD_SERVER) || g_api->on_world_thread(KILN_WORLD_CLIENT) ? KILN_ERR_UNAVAILABLE : KILN_ERR_WRONG_THREAD;
    if (want & KILN_PLAYERSTATE_WANT_BASE_ZONE) {
        // the list changes when a flame altar is placed or removed: every few reads is plenty
        static uint32_t age = 0;
        if (!w.server) g_bases_ok = false;
        else if (!g_bases_ok || count == 1 || ++age >= 8) {
            age = 0;
            refresh_bases();
        }
    }
    const int64_t now = ecs_now(w.server ? w.server : w.client);
    for (uint32_t i = 0; i < count; ++i) {
        KilnPlayerSnapshot* s = &out[i];
        const uint32_t size = s->size;
        if (size < sizeof *s) return KILN_ERR_INVALID_ARGUMENT;
        std::memset(s, 0, sizeof *s);
        s->size = size;
        const KilnEntity e = entities[i];
        s->entity = e;
        s->in_base = 2;
        if (w.server) {
            s->world = KILN_PLAYERSTATE_SERVER;
            s->server_time = now;
            read_authoritative(w.server, e, want, s);
            if (want & KILN_PLAYERSTATE_WANT_VITALS) read_vitals(w.server, e, s);
            if (want & KILN_PLAYERSTATE_WANT_EXTRA) read_buff_counts(w.server, e, s);
            if (want & KILN_PLAYERSTATE_WANT_BASE_ZONE) read_base_zone(w.server, e, s);
        } else {
            s->server_time = now;
            if (w.prediction && e == w.local) {
                s->world = KILN_PLAYERSTATE_PREDICTION;
                read_authoritative(w.prediction, e, want, s);
            }
            if (!(s->known & (1u << 1))) {
                s->world = KILN_PLAYERSTATE_REPLICATED;
                read_replicated(w.client, e, s);
            }
            if (want & KILN_PLAYERSTATE_WANT_VITALS) read_vitals(w.client, e, s);
        }
    }
    g_scans.fetch_add(1, std::memory_order_relaxed);
    g_entities.fetch_add(count, std::memory_order_relaxed);
    return KILN_OK;
}

uintptr_t read_world_here() {
    const Worlds w = worlds_here();
    return w.server ? w.server : w.client;
}

KilnResult slot_item_in(uintptr_t world, uint64_t slot, uint32_t* item, uint32_t* stack, uint32_t* pide) {
    const uint32_t inv = static_cast<uint32_t>(slot & 0xFFFFFFFFull);
    const uint32_t index = static_cast<uint32_t>((slot >> 32) & 0xFF);
    if (item) *item = 0;
    if (stack) *stack = 0;
    if (pide) *pide = 0;
    if (!inv || index >= 8) return KILN_ERR_NOT_FOUND;
    uint64_t size = 0;
    const uint8_t* p = comp_of(world, C_INV, inv, &size);
    if (!p || size < (index + 1) * 12ull) return KILN_ERR_NOT_FOUND;
    uint32_t rec[3] = {};
    if (!seh_copy(p + index * 12ull, rec, sizeof rec)) return KILN_ERR_INTERNAL;
    if (item) *item = rec[0];
    if (stack) *stack = rec[1];
    if (pide) *pide = rec[2];
    return KILN_OK;
}

KilnResult KILN_CALL t_equipment(KilnEntity e, KilnPlayerEquipment* out) {
    if (!out || out->size < sizeof *out) return KILN_ERR_INVALID_ARGUMENT;
    ensure();
    const uintptr_t world = read_world_here();
    if (!world) return KILN_ERR_WRONG_THREAD;
    if (!L.slot_equipment) return KILN_ERR_UNAVAILABLE;
    const uint8_t* p = comp_of(world, C_SLOTS, e);
    if (!p) return KILN_ERR_NOT_FOUND;
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    out->hotbar = -1;
    out->count = static_cast<uint32_t>(g_equip_names.size());
    if (!seh_copy(p + L.slot_equipment.off, out->slots, out->count * sizeof(uint64_t))) return KILN_ERR_INTERNAL;
    uint32_t bar[2] = {};
    __try {
        out->held_slot = rd_field<uint64_t>(p, L.slot_held_slot);
        out->held_item = rd_field<uint32_t>(p, L.slot_held_item);
        if (L.slot_hotbar) out->hotbar = rd_field<uint8_t>(p, L.slot_hotbar);
        if (L.slot_bars) std::memcpy(bar, p + L.slot_bars.off, sizeof bar);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return KILN_ERR_INTERNAL;
    }
    // In hand: what the running action uses, else the selected bar slot, 8 slots to each of the two bar inventories (0x1444a0).
    if (!out->held_slot && out->hotbar >= 0 && out->hotbar < 16) {
        const uint32_t inv = bar[out->hotbar >> 3], index = static_cast<uint32_t>(out->hotbar) & 7;
        out->held_slot = inv ? inv | (static_cast<uint64_t>(index) << 32) : 0;
        slot_item_in(world, out->held_slot, &out->held_item, nullptr, nullptr);
    }
    for (uint32_t i = 0; i < out->count; ++i) slot_item_in(world, out->slots[i], &out->items[i], &out->stack[i], nullptr);
    return KILN_OK;
}

KilnResult KILN_CALL t_equipment_slot_name(uint32_t index, char* name, uint32_t capacity, uint32_t* name_len) {
    ensure();
    if (index >= g_equip_names.size()) return KILN_ERR_NOT_FOUND;
    return copy_text(g_equip_names[index], name, capacity, name_len);
}

KilnResult KILN_CALL t_buffs(KilnEntity e, KilnPlayerBuff* out, uint32_t capacity, uint32_t* count) {
    if (!count) return KILN_ERR_INVALID_ARGUMENT;
    ensure();
    *count = 0;
    const uintptr_t world = read_world_here();
    if (!world) return KILN_ERR_WRONG_THREAD;
    if (!L.b_food) return KILN_ERR_UNAVAILABLE;
    const uint8_t* p = comp_of(world, C_BUFFS, e);
    if (!p) return KILN_ERR_NOT_FOUND;  // server-only component: a joined client has none
    struct Group {
        const Field* f;
        uint32_t n, slot;
    };
    const Group groups[] = {{&L.b_food, 4, 0}, {&L.b_buff, 16, 1}, {&L.b_debuff, 16, 2}, {&L.b_rested, 1, 3}, {&L.b_terrain, 1, 4}};
    uint32_t n = 0;
    for (const Group& g : groups) {
        if (!*g.f) continue;
        for (uint32_t i = 0; i < g.n; ++i) {
            uint32_t rec[3] = {};
            if (!seh_copy(p + g.f->off + i * 12ull, rec, sizeof rec) || !rec[1]) continue;
            if (out && n < capacity) {
                KilnPlayerBuff& b = out[n];
                b = KilnPlayerBuff{};
                b.id = rec[1];
                b.entity = rec[0];
                b.slot = g.slot;
                b.cleansed = rec[2] & 0xFF;
                if (const uint8_t* net = rec[0] ? comp_of(world, C_BUFFNET, rec[0]) : nullptr) {
                    __try {
                        b.life_ns = rd_field<int64_t>(net, L.bn_life);
                        b.end_ns = rd_field<int64_t>(net, L.bn_end);
                        b.level = rd_field<uint32_t>(net, L.bn_level);
                    } __except (EXCEPTION_EXECUTE_HANDLER) {
                        b.end_ns = 0;
                    }
                }
            }
            ++n;
        }
    }
    *count = n;
    return out && n > capacity ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

KilnResult KILN_CALL t_slot_item(uint64_t slot, uint32_t* item, uint32_t* stack, uint32_t* pide) {
    ensure();
    const uintptr_t world = read_world_here();
    return world ? slot_item_in(world, slot, item, stack, pide) : KILN_ERR_WRONG_THREAD;
}

// The game's name of an enum value (keen::ItemCategory 7 -> "Consumables"); "" when unknown.
void enum_name_of(const char* type, uint64_t value, char* out, size_t capacity) {
    out[0] = 0;
    Rec r;
    const uintptr_t t = type_named(type);
    if (!t || !resolve(t, &r) || r.kind != K_ENUM || !r.enums) return;
    for (uint32_t i = 0; i < r.count; ++i) {
        const uintptr_t e = r.enums + static_cast<uintptr_t>(i) * static_cast<uintptr_t>(T.enum_stride);
        uintptr_t np = 0;
        uint64_t nl = 0, v = 0;
        if (!rd(e, np) || !rd(e + 8, nl) || !rd(e + T.enum_value, v) || v != value) continue;
        std::snprintf(out, capacity, "%s", text(np, nl).c_str());
        return;
    }
}

const uint8_t* guarded_item(uint32_t id, void* registry) {
    __try {
        return F.item_find(id, registry);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
}
const uint8_t* guarded_buff(uint32_t id, void* registry) {
    __try {
        return F.buff_find ? F.buff_find(id, registry) : nullptr;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
}
const uint8_t* guarded_sub(void* registry, uint32_t sequence, uint8_t sub) {
    __try {
        return F.sub_sequence(registry, sequence, sub);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
}

// A keen::BlobString: {i32 offset from the field, u32 length}.
std::string blob_string(const uint8_t* field) {
    int32_t rel = 0;
    uint32_t len = 0;
    if (!seh_copy(field, &rel, 4) || !seh_copy(field + 4, &len, 4) || !rel || !len || len > 256) return {};
    return text(reinterpret_cast<uintptr_t>(field) + rel, len);
}

bool read_item_fields(const uint8_t* info, KilnPlayerItemInfo* out) {
    __try {
        out->category = rd_field<uint8_t>(info, L.item_category);
        out->ammunition = rd_field<uint8_t>(info, L.item_ammo);
        out->slot = rd_field<uint8_t>(info, L.item_slot);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

KilnResult KILN_CALL t_item_info(uint32_t item, KilnPlayerItemInfo* out) {
    if (!out || out->size < sizeof *out) return KILN_ERR_INVALID_ARGUMENT;
    ensure();
    if (!g_items) return KILN_ERR_UNAVAILABLE;
    const uintptr_t world = read_world_here();
    if (!world) return KILN_ERR_WRONG_THREAD;
    const uintptr_t registry = g_api->singleton(world, kstr("g38_items::ItemRegistry"));
    if (!registry) return KILN_ERR_UNAVAILABLE;
    const uint8_t* info = guarded_item(item, reinterpret_cast<void*>(registry));
    if (!info) return KILN_ERR_NOT_FOUND;
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    out->id = item;
    out->buff_slot = 0xFFFFFFFFu;
    if (!read_item_fields(info, out)) return KILN_ERR_INTERNAL;
    if (L.item_buff) {
        uint8_t guid[16] = {};
        if (seh_copy(info + L.item_buff.off, guid, sizeof guid)) {
            bool zero = true;
            uint32_t h = 0x811c9dc5u;
            for (uint8_t b : guid) {
                zero &= b == 0;
                h = (h ^ b) * 0x1000193u;
            }
            out->buff = zero ? 0 : h;
        }
    }
    if (out->buff && L.buff_slot) {
        const uintptr_t buffs = g_api->singleton(world, kstr("BuffRegistry"));
        if (const uint8_t* bt = buffs ? guarded_buff(out->buff, reinterpret_cast<void*>(buffs)) : nullptr) {
            uint8_t slot = 0;
            if (seh_copy(bt + L.buff_slot.off, &slot, 1)) out->buff_slot = slot;
        }
    }
    enum_name_of("keen::ItemCategory", out->category, out->category_name, sizeof out->category_name);
    enum_name_of("keen::EquipmentSlotType", out->slot, out->slot_name, sizeof out->slot_name);
    enum_name_of("keen::AmmunitionType", out->ammunition, out->ammunition_name, sizeof out->ammunition_name);
    if (out->buff_slot != 0xFFFFFFFFu) enum_name_of("keen::BuffSlotType", out->buff_slot, out->buff_slot_name, sizeof out->buff_slot_name);
    const std::string name = blob_string(info + L.item_name.off);
    out->name_len = static_cast<uint32_t>(name.size());
    std::snprintf(out->name, sizeof out->name, "%s", name.c_str());
    return KILN_OK;
}

KilnResult KILN_CALL t_sequence_name(uint32_t sequence, uint32_t sub, char* name, uint32_t capacity, uint32_t* name_len) {
    ensure();
    if (!g_names) return KILN_ERR_UNAVAILABLE;
    const uintptr_t world = read_world_here();
    if (!world) return KILN_ERR_WRONG_THREAD;
    const uintptr_t registry = g_api->singleton(world, kstr("ActionSequenceRegistry"));
    if (!registry) return KILN_ERR_UNAVAILABLE;
    const uint8_t* s = guarded_sub(reinterpret_cast<void*>(registry), sequence, static_cast<uint8_t>(sub));
    if (!s) return KILN_ERR_NOT_FOUND;
    return copy_text(blob_string(s + L.sub_name.off), name, capacity, name_len);
}

bool guarded_reset(void* actor) {
    __try {
        F.action_reset(actor);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

KilnResult KILN_CALL t_cancel_action(KilnEntity e, uint32_t sequence, int64_t trigger_time) {
    ensure();
    if (!g_cancel) return KILN_ERR_UNAVAILABLE;
    if (!g_api->on_world_thread(KILN_WORLD_SERVER)) return KILN_ERR_WRONG_THREAD;
    const uintptr_t world = g_api->world(KILN_WORLD_SERVER);
    if (!world) return KILN_ERR_UNAVAILABLE;
    const uint8_t* a = comp_of(world, C_ACTOR, e);
    if (!a) return KILN_ERR_NOT_FOUND;
    uint32_t cur = 0;
    int64_t time = 0;
    if (!seh_copy(a + L.seq_id.off, &cur, 4) || !seh_copy(a + L.seq_time.off, &time, 8)) return KILN_ERR_INTERNAL;
    if (cur != sequence || time != trigger_time) return KILN_ERR_NOT_FOUND;
    return guarded_reset(const_cast<uint8_t*>(a)) ? KILN_OK : KILN_ERR_INTERNAL;
}

bool guarded_trigger(void* out, void* ai, const EntityTime* who, uint32_t seq, uint16_t trigger, uint8_t input) {
    __try {
        F.trigger(out, ai, who, seq, trigger, input);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

KilnResult KILN_CALL t_lab_use_item(KilnEntity e, uint32_t sequence, uint32_t trigger, uint32_t input, uint64_t slot, uint32_t item) {
    ensure();
    if (!g_lab || !F.trigger || !L.ctx_used_item || !L.ctx_used_slot) return KILN_ERR_UNAVAILABLE;
    if (!g_api->on_world_thread(KILN_WORLD_SERVER)) return KILN_ERR_WRONG_THREAD;
    const uintptr_t world = g_api->world(KILN_WORLD_SERVER);
    uint8_t* ai = world ? const_cast<uint8_t*>(comp_of(world, C_AINPUT, e)) : nullptr;
    if (!ai) return KILN_ERR_NOT_FOUND;
    alignas(8) uint8_t out[0x18] = {};
    const EntityTime who{e, 0, ecs_now(world)};
    if (!guarded_trigger(out, ai, &who, sequence, static_cast<uint16_t>(trigger), static_cast<uint8_t>(input))) return KILN_ERR_INTERNAL;
    __try {
        std::memcpy(ai + L.ctx_used_slot.off, &slot, sizeof slot);
        std::memcpy(ai + L.ctx_used_item.off, &item, sizeof item);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return KILN_ERR_INTERNAL;
    }
    return KILN_OK;
}

// ------------------------------------------------------------------------------- holding client actions ----
// The game copies each player's queued client input into PlayerInput inside the sim step (players/InputApply, 0x600ff0
// on server 26430253), after Kiln's Pre and right before DeliverEvents and every program. A new version runs in the
// step it arrives in, so Pre only ever sees versions that already ran: marking one consumed there changes nothing.
// While a listener holds, Kiln looks again at the start of DeliverEvents. That is after the input copy (and after a
// version a mod writes in Pre or in a detour of the copy) and before any program. A new version of a cancelable action
// is hidden for one step: PlayerInput's copy shows the consumed version, so the game's program sees nothing new, and
// the action's bytes are kept. The listener answers in the next Pre. At the start of the next DeliverEvents a released
// action gets its bytes back and the program runs it; a canceled one is marked consumed, the way the game ignores an
// old version, and never runs. ServerConsumedPlayerInput (which the client gets back) does not move while one is held.
constexpr uint32_t kHeldBytes = sizeof(KilnPlayerHeldAction::payload);
constexpr uint32_t kHoldStale = 8;  // DeliverEvents calls without a hold() before holding ends by itself
enum : uint8_t { D_WAIT, D_RUN, D_DROP };

struct Held {
    KilnEntity entity = 0;
    uint32_t index = 0, version = 0;
    uint32_t shown = 0;  // the version PlayerInput's copy shows when no newer input comes: the consumed one
    uint8_t decision = D_WAIT;
    bool listed = false;
    std::array<uint8_t, kHeldBytes> bytes{};
};
struct Passed {
    KilnEntity entity;
    uint32_t index, version;
};

SafetyHookInline g_deliver_hook;
std::vector<Held> g_held;            // the server world's thread only, like everything below
std::vector<Passed> g_passed;        // released versions: one a program leaves pending for a while is not held again
std::vector<KilnEntity> g_hold_for;  // from hold(), in Pre
uint32_t g_hold_age = 0;

bool holdable(const ClientAction& a) {
    return g_hold_ok && a.consumed >= 0 && a.version >= a.offset && a.bytes >= a.version - a.offset + 4 && a.bytes <= kHeldBytes;
}

bool holds_for(KilnEntity e) { return std::find(g_hold_for.begin(), g_hold_for.end(), e) != g_hold_for.end(); }

bool passed(KilnEntity e, uint32_t index, uint32_t version) {
    for (const Passed& p : g_passed)
        if (p.entity == e && p.index == index) return p.version == version;
    return false;
}

void remember_passed(KilnEntity e, uint32_t index, uint32_t version) {
    for (Passed& p : g_passed)
        if (p.entity == e && p.index == index) {
            p.version = version;
            return;
        }
    g_passed.push_back({e, index, version});
}

bool held(KilnEntity e, uint32_t index, const std::vector<Held>& in) {
    for (const Held& h : in)
        if (h.entity == e && h.index == index) return true;
    return false;
}

// One step after a hold: the listener's answer. A newer version that arrived meanwhile is appended to `keep`, held.
void resolve(uintptr_t world, const Held& h, bool holding, std::vector<Held>& keep) {
    const ClientAction& a = g_client_actions[h.index];
    uint8_t* pi = const_cast<uint8_t*>(comp_of(world, C_PINPUT, h.entity));
    uint8_t* sc = const_cast<uint8_t*>(comp_of(world, C_CONSUMED, h.entity));
    if (!pi || !sc) return;  // the player is gone
    const uint8_t d = h.decision == D_WAIT ? D_RUN : h.decision;  // nobody answered: it runs
    Held next;
    bool newer = false;
    __try {
        uint32_t v = 0, c = 0;
        std::memcpy(&v, pi + a.version, 4);
        std::memcpy(&c, sc + a.consumed, 4);
        newer = v != h.version && v != h.shown && v != c;  // the client sent another one of this kind meanwhile
        if (d == D_RUN) {
            if (v != h.version) {
                // PlayerInput shows the hidden version (no input came since) or a newer one: put this one back so the
                // program runs it now; the newer one is held in turn and shows h.version until its own input returns.
                if (newer && holding) {
                    next.entity = h.entity;
                    next.index = h.index;
                    next.version = v;
                    next.shown = h.version;
                    std::memcpy(next.bytes.data(), pi + a.offset, a.bytes);
                }
                std::memcpy(pi + a.offset, h.bytes.data(), a.bytes);
            }
        } else {
            std::memcpy(sc + a.consumed, &h.version, 4);
            if (v == h.shown) std::memcpy(pi + a.version, &h.version, 4);  // the hidden copy shows its real, now consumed, version
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return;
    }
    if (d == D_RUN) {
        remember_passed(h.entity, h.index, h.version);
        if (newer && holding) keep.push_back(next);
    }
}

// New versions of the players held for: hide each one for this step.
void hold_new(uintptr_t world, KilnEntity e, std::vector<Held>& keep) {
    uint8_t* pi = const_cast<uint8_t*>(comp_of(world, C_PINPUT, e));
    const uint8_t* sc = comp_of(world, C_CONSUMED, e);
    if (!pi || !sc) return;
    for (uint32_t i = 0; i < g_client_actions.size(); ++i) {
        const ClientAction& a = g_client_actions[i];
        if (!holdable(a) || held(e, i, keep)) continue;
        Held h;
        __try {
            uint32_t v = 0, c = 0;
            std::memcpy(&v, pi + a.version, 4);
            std::memcpy(&c, sc + a.consumed, 4);
            if (v == c || passed(e, i, v)) continue;
            h.entity = e;
            h.index = i;
            h.version = v;
            h.shown = c;
            std::memcpy(h.bytes.data(), pi + a.offset, a.bytes);
            std::memcpy(pi + a.version, &c, 4);  // the game's program sees no new version in this step
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            return;
        }
        keep.push_back(h);
    }
}

void hold_check(uintptr_t storage) {
    if ((g_held.empty() && g_hold_for.empty()) || g_storage < 0) return;
    const uintptr_t world = storage - static_cast<uintptr_t>(g_storage);
    if (world != g_api->world(KILN_WORLD_SERVER) || !g_api->on_world_thread(KILN_WORLD_SERVER)) return;
    if (++g_hold_age > kHoldStale && !g_hold_for.empty()) {
        PSW("client actions: no listener renewed the hold for %u steps; held actions run and holding stops", kHoldStale);
        g_hold_for.clear();
    }
    const bool holding = !g_hold_for.empty();
    std::vector<Held> keep;
    keep.reserve(g_held.size());
    for (const Held& h : g_held) {
        // not offered to the listener yet (a step without Pre): it stays hidden
        if (h.decision == D_WAIT && !h.listed && holding && holds_for(h.entity)) keep.push_back(h);
        else resolve(world, h, holding, keep);
    }
    if (holding)
        for (const KilnEntity e : g_hold_for) hold_new(world, e, keep);
    g_held.swap(keep);
}

// DeliverEvents(storage): SimStep's call right after InputApply. Every register argument goes through untouched.
uintptr_t __fastcall hk_deliver_events(uintptr_t storage, uintptr_t rdx, uintptr_t r8, uintptr_t r9) {
    try {
        hold_check(storage);
    } catch (...) {
        // out of memory: the game runs on unheld
    }
    return g_deliver_hook.call<uintptr_t>(storage, rdx, r8, r9);
}

KilnResult KILN_CALL t_client_action_hold(const KilnEntity* entities, uint32_t count) {
    ensure();
    if (!g_hold_ok) return KILN_ERR_UNAVAILABLE;
    if (!g_api->on_world_thread(KILN_WORLD_SERVER)) return KILN_ERR_WRONG_THREAD;
    if (count && !entities) return KILN_ERR_INVALID_ARGUMENT;
    try {
        g_hold_for.assign(entities, entities + count);
        std::erase_if(g_passed, [](const Passed& p) { return !holds_for(p.entity); });
    } catch (...) {
        g_hold_for.clear();
        return KILN_ERR_INTERNAL;
    }
    g_hold_age = 0;
    return KILN_OK;
}

KilnResult KILN_CALL t_client_action_held(KilnPlayerHeldAction* out, uint32_t capacity, uint32_t* count) {
    if (!count) return KILN_ERR_INVALID_ARGUMENT;
    *count = 0;
    if (!g_hold_ok) return KILN_ERR_UNAVAILABLE;
    if (!g_api->on_world_thread(KILN_WORLD_SERVER)) return KILN_ERR_WRONG_THREAD;
    for (Held& h : g_held) {
        if (h.listed) continue;
        if (*count >= capacity || !out) return KILN_ERR_BUFFER_TOO_SMALL;
        KilnPlayerHeldAction& o = out[*count];
        if (o.size < sizeof o) return KILN_ERR_INVALID_ARGUMENT;
        o.entity = h.entity;
        o.index = h.index;
        o.version = h.version;
        o.bytes = g_client_actions[h.index].bytes;
        o.reserved = 0;
        std::memset(o.payload, 0, sizeof o.payload);
        std::memcpy(o.payload, h.bytes.data(), o.bytes);
        h.listed = true;
        ++*count;
    }
    return KILN_OK;
}

KilnResult KILN_CALL t_client_action_decide(KilnEntity e, uint32_t index, uint32_t version, uint32_t cancel) {
    if (!g_hold_ok) return KILN_ERR_UNAVAILABLE;
    if (!g_api->on_world_thread(KILN_WORLD_SERVER)) return KILN_ERR_WRONG_THREAD;
    for (Held& h : g_held)
        if (h.entity == e && h.index == index && h.version == version) {
            h.decision = cancel ? D_DROP : D_RUN;
            return KILN_OK;
        }
    return KILN_ERR_NOT_FOUND;
}

KilnResult KILN_CALL t_client_action_info(uint32_t index, KilnPlayerClientAction* out) {
    if (!out || out->size < sizeof *out) return KILN_ERR_INVALID_ARGUMENT;
    ensure();
    if (index >= g_client_actions.size()) return KILN_ERR_NOT_FOUND;
    const ClientAction& a = g_client_actions[index];
    out->index = index;
    out->bytes = a.bytes;
    out->cancelable = holdable(a);
    std::snprintf(out->name, sizeof out->name, "%s", a.name.c_str());
    return KILN_OK;
}

KilnResult KILN_CALL t_client_action_bytes(KilnEntity e, uint32_t index, void* out, uint32_t capacity, uint32_t* bytes) {
    ensure();
    if (index >= g_client_actions.size()) return KILN_ERR_NOT_FOUND;
    const uintptr_t world = read_world_here();
    if (!world) return KILN_ERR_WRONG_THREAD;
    const ClientAction& a = g_client_actions[index];
    if (bytes) *bytes = a.bytes;
    if (!out || capacity < a.bytes) return KILN_ERR_BUFFER_TOO_SMALL;
    const uint8_t* pi = comp_of(world, C_PINPUT, e);
    if (!pi) return KILN_ERR_NOT_FOUND;
    return seh_copy(pi + a.offset, out, a.bytes) ? KILN_OK : KILN_ERR_INTERNAL;
}

// A version seen in Pre without a hold has already run (see "holding client actions"): only a held one can be canceled.
KilnResult KILN_CALL t_client_action_cancel(KilnEntity e, uint32_t index) {
    ensure();
    if (index >= g_client_actions.size()) return KILN_ERR_NOT_FOUND;
    if (!holdable(g_client_actions[index])) return KILN_ERR_UNAVAILABLE;
    if (!g_api->on_world_thread(KILN_WORLD_SERVER)) return KILN_ERR_WRONG_THREAD;
    for (Held& h : g_held)
        if (h.entity == e && h.index == index) {
            h.decision = D_DROP;
            return KILN_OK;
        }
    return KILN_ERR_NOT_FOUND;
}

KilnEntity KILN_CALL t_local_player() {
    if (!g_client) return 0;
    KilnClientState s{};
    s.size = sizeof s;
    return g_client->state(&s) == KILN_OK ? s.local_player : 0;
}

static_assert(sizeof(KilnPlayerSnapshot) == 544 && sizeof(KilnPlayerClientAction) == 64 && sizeof(KilnPlayerItemInfo) == 288 && sizeof(KilnPlayerEquipment) == 408 &&
              sizeof(KilnPlayerBuff) == 40 && sizeof(KilnPlayerStateStatus) == 200 && sizeof(KilnPlayerSourceInfo) == 216 &&
              sizeof(KilnPlayerHeldAction) == 184);

const KilnPlayerStateApi kTable = {
    {sizeof(KilnPlayerStateApi), KILN_PLAYERSTATE_VERSION},
    &t_status,
    &t_source_info,
    &t_enum_entry,
    &t_scan,
    &t_equipment,
    &t_equipment_slot_name,
    &t_buffs,
    &t_slot_item,
    &t_item_info,
    &t_sequence_name,
    &t_cancel_action,
    &t_lab_use_item,
    &t_local_player,
    &t_client_action_info,
    &t_client_action_bytes,
    &t_client_action_cancel,
    &t_client_action_hold,
    &t_client_action_held,
    &t_client_action_decide,
};

template <class Fn>
void sym(const char* name, Fn* out) {
    uintptr_t a = 0;
    if (g_api->symbol(kstr(name), &a) == KILN_OK) *out = reinterpret_cast<Fn>(a);
}

void constant(const char* name, int64_t* out) {
    int64_t v = 0;
    if (g_api->constant(kstr(name), &v) == KILN_OK) *out = v;
}

}  // namespace

void main(const KilnApi* api, KilnOwner owner) {
    g_api = api;
    g_owner = owner;
    if (api->feature(kstr("players.reflection"))) {
        sym("players/TypeByHash", &F.type_by_hash);
        constant("players/type.qname", &T.qname);
        constant("players/type.inner", &T.inner);
        constant("players/type.size", &T.size);
        constant("players/type.count", &T.count);
        constant("players/type.kind", &T.kind);
        constant("players/type.fields", &T.fields);
        constant("players/type.enum_values", &T.enums);
        constant("players/field.stride", &T.field_stride);
        constant("players/field.type", &T.field_type);
        constant("players/field.offset", &T.field_offset);
        constant("players/enum.stride", &T.enum_stride);
        constant("players/enum.value", &T.enum_value);
    }
    sym("GetComponent", &F.get_component);
    sym("GetEntityTransform", &F.get_transform);
    sym("items/ItemRegistryFind", &F.item_find);
    sym("gamedata/GetBuffType", &F.buff_find);
    if (api->feature(kstr("players.action_cancel"))) sym("players/ActionReset", &F.action_reset);
    if (api->feature(kstr("players.sequence_names"))) sym("players/SubSequence", &F.sub_sequence);
    // Client actions can be refused only before the step's programs run (see "holding client actions").
    uintptr_t deliver = 0;
    if (api->feature(kstr("players.client_action_hold")) && api->symbol(kstr("gameplay/DeliverEvents"), &deliver) == KILN_OK && deliver) {
        g_deliver_hook = safetyhook::create_inline(reinterpret_cast<void*>(deliver), reinterpret_cast<void*>(&hk_deliver_events));
        g_hold_ok = static_cast<bool>(g_deliver_hook);
        if (!g_hold_ok) PSW("hooking DeliverEvents at %p failed: client actions can be seen but not canceled", reinterpret_cast<void*>(deliver));
    }
    sym("abilities/TriggerSequence", &F.trigger);
    constant("world.storage", &g_storage);
    constant("storage.clock", &g_clock);
    constant("storage.clock_period", &g_period);
    char lab[8] = {};
    g_lab = GetEnvironmentVariableA("KILN_PLAYERSTATE_LAB", lab, sizeof lab) > 0 && lab[0] == '1';
    if (g_lab) PSW("KILN_PLAYERSTATE_LAB is set: lab_use_item is on. Never set this on a real server");
    api->register_service(owner, kstr(KILN_PLAYERSTATE_SERVICE), &kTable);
}

void ready() {
    g_build = static_cast<const KilnBuildApi*>(g_api->service(kstr(KILN_BUILD_SERVICE), KILN_BUILD_VERSION));
    g_client = static_cast<const KilnClientApi*>(g_api->service(kstr(KILN_CLIENT_SERVICE), KILN_CLIENT_VERSION));
}

}  // namespace kiln::playerstate
