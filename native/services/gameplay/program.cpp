// SPDX-License-Identifier: MIT
// kiln_gameplay: Kiln's own system program (registered through kiln.world). It sends the game's request events that
// inject() queued, with the game's own emitter, so they reach the game's handlers (and kiln.gameplay's Pre) exactly as a
// client's or a villager's would: CraftRecipeEvent (handle_crafting_events), AcceptOfferedInteractionEvent
// (interaction_attempt), LootInteractionEvent (loot_pickup_item, immediate_loot_action).
#include "internal.h"
#include "services/kiln_world.h"

namespace kg {
namespace {

const KilnWorldApi* g_world_api = nullptr;
KilnHandle g_program = 0;
int32_t g_slot_accept = -1, g_slot_craft = -1, g_slot_loot = -1;
using ProgramTimeFn = int64_t* (*)(void* ctx, int64_t* out);
ProgramTimeFn g_time = nullptr;

std::mutex g_mu;
std::vector<KilnGameplayInject> g_queue;
bool g_active = false;
std::atomic<uint16_t> g_pid{0};

void emit_one(KilnProgramCtx* ctx, const uint64_t* iter, const KilnGameplayInject& r) {
    int32_t slot = -1;
    uint32_t hash = 0, size = 0;
    switch (r.kind) {
    case KILN_GAMEPLAY_INJECT_CRAFT: slot = g_slot_craft, hash = H.craft_request, size = kCraftSize; break;
    case KILN_GAMEPLAY_INJECT_INTERACT: slot = g_slot_accept, hash = H.accept_offer, size = kAcceptSize; break;
    case KILN_GAMEPLAY_INJECT_LOOT: slot = g_slot_loot, hash = H.loot, size = kLootSize; break;
    default: return;
    }
    if (slot < 0) return;
    void* job = reinterpret_cast<void*>(iter[slot]);
    if (!job) return;
    void* buf = fn.job_buffer(job);
    const uint16_t pid = fn.job_program(job);
    g_pid.store(pid, std::memory_order_relaxed);
    auto* e = static_cast<uint8_t*>(fn.emit(buf, hash, size, pid));
    if (!e) {
        KG_W("inject: the event buffer is full; request kind %u dropped", r.kind);
        return;
    }
    int64_t now = 0;
    if (g_time) g_time(ctx, &now);
    std::memcpy(e, &now, 8);
    std::memcpy(e + 0x8, &r.actor, 4);
    std::memcpy(e + 0xC, &r.target, 4);
    std::memcpy(e + 0x10, &r.id, 4);
    if (r.kind == KILN_GAMEPLAY_INJECT_CRAFT) {
        const uint16_t amount = static_cast<uint16_t>(r.amount ? r.amount : 1);
        std::memcpy(e + 0x14, &amount, 2);  // input category selections (+0x18..) stay 0: the game picks
    } else if (r.kind == KILN_GAMEPLAY_INJECT_LOOT) {
        e[0x14] = (r.flags & 1) ? 1 : 0;  // lootAll
        e[0x78] = (r.flags & 2) ? 1 : 0;  // bypassInteraction
    }
}

void KILN_CALL each(KilnProgramCtx* ctx, uint64_t* iter, KilnEntity, void*) {
    std::vector<KilnGameplayInject> q;
    {
        std::lock_guard lock(g_mu);
        if (g_queue.empty()) return;
        q.swap(g_queue);
    }
    for (auto& r : q) emit_one(ctx, iter, r);
}

}  // namespace

void program_register(const void* api) {
    g_world_api = static_cast<const KilnWorldApi*>(api);
    if (!F.emit) return;
    if (!g_world_api || g_world_api->header.size < sizeof(KilnWorldApi)) {
        KG_W("kiln.world is unavailable: inject() is off");
        return;
    }
    uintptr_t t = 0;
    if (g_api->symbol(KilnStr{"gameplay/ProgramTime", 20}, &t) == KILN_OK && t) g_time = reinterpret_cast<ProgramTimeFn>(t);
    static const KilnStr kOut[] = {kstr("AcceptOfferedInteractionEvent"), kstr("CraftRecipeEvent"),
                                   kstr("LootInteractionEvent")};
    KilnProgramDesc d{};
    d.size = sizeof d;
    d.flags = KILN_PROGRAM_SYSTEM | KILN_PROGRAM_HOST_ONLY;
    d.name = kstr("kiln_gameplay");
    d.each = &each;
    d.events_out = KilnNames{kOut, 3, 0};
    if (g_world_api->register_program(g_owner, &d, &g_program) != KILN_OK) {
        KG_W("program kiln_gameplay was refused by kiln.world: inject() is off");
        return;
    }
    g_slot_accept = g_world_api->program_slot(g_program, KILN_SLOT_EVENT_OUT, kOut[0]);
    g_slot_craft = g_world_api->program_slot(g_program, KILN_SLOT_EVENT_OUT, kOut[1]);
    g_slot_loot = g_world_api->program_slot(g_program, KILN_SLOT_EVENT_OUT, kOut[2]);
    g_active = g_slot_accept >= 0 && g_slot_craft >= 0 && g_slot_loot >= 0;
    KG_I("program kiln_gameplay accepted (event-out slots %d %d %d, program time %s)", g_slot_accept, g_slot_craft,
         g_slot_loot, g_time ? "yes" : "no");
}

uint16_t program_pid() { return g_pid.load(std::memory_order_relaxed); }

bool program_active() {
    if (!g_active || !g_world_api) return false;
    KilnProgramInfo info{};
    info.size = sizeof info;
    return g_world_api->program_info(g_program, &info) == KILN_OK && info.state == KILN_PROGRAM_ACTIVE;
}

KilnResult inject(const KilnGameplayInject* r) {
    if (!r || r->size < sizeof(KilnGameplayInject)) return KILN_ERR_INVALID_ARGUMENT;
    if (r->kind < KILN_GAMEPLAY_INJECT_CRAFT || r->kind > KILN_GAMEPLAY_INJECT_LOOT) return KILN_ERR_INVALID_ARGUMENT;
    if (!r->target) return KILN_ERR_INVALID_ARGUMENT;
    if (!program_active()) return KILN_ERR_UNAVAILABLE;
    std::lock_guard lock(g_mu);
    if (g_queue.size() >= 256) return KILN_ERR_BUFFER_TOO_SMALL;
    g_queue.push_back(*r);
    return KILN_OK;
}

}  // namespace kg
