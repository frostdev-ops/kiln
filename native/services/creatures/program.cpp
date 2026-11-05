// SPDX-License-Identifier: MIT
// kiln.creatures: two ECS programs registered through kiln.world.
//  - kiln_creatures (SYSTEM): sends queued commands as the game's own events with the game's emitter (the same path
//    kiln.gameplay's inject uses): AddThreatTargetEvent (enemy_threat_events), StartGuardingEvent /
//    StopGuardingEvent (enemy01), RestoreEnemyHealthEvent (enemy01_pre_update), RegisterControlledCombatTargetEvent
//    (control_combat_target). Delivered to their readers in the next sim step (docs/re/gameplay.md 1).
//  - kiln_creatures_marks (raw): marks components Kiln wrote on the tick thread as written (MarkWritten), so the
//    engine's change tracking replicates and saves them like a game write. It iterates only in steps with pending
//    marks; otherwise it returns at once without touching the iterator.
#include <cstdio>

#include "internal.h"
#include "services/kiln_world.h"

namespace kcr {

namespace {

const KilnWorldApi* W = nullptr;
KilnHandle g_cmd_prog = 0, g_mark_prog = 0;
bool g_cmd_ok = false, g_mark_ok = false;

enum { EV_THREAT, EV_CONTROL, EV_RESTORE, EV_START_GUARD, EV_STOP_GUARD, EV_NPC, EV_COUNT };
const char* const kEvents[EV_COUNT] = {"AddThreatTargetEvent", "RegisterControlledCombatTargetEvent",
                                       "RestoreEnemyHealthEvent", "StartGuardingEvent", "StopGuardingEvent",
                                       "NpcCommandEvent"};
const uint32_t kEventSize[EV_COUNT] = {24, 16, 24, 16, 16, 72};  // reflection sizes (docs/re/creatures.md 2.5)
int32_t g_ev_slot[EV_COUNT] = {-1, -1, -1, -1, -1, -1};
uint32_t g_ev_hash[EV_COUNT] = {};

using ProgramTimeFn = int64_t* (*)(void* ctx, int64_t* out);
ProgramTimeFn g_time = nullptr;

std::mutex g_mu;
std::vector<KilnCreatureCommand> g_queue;
std::vector<KilnNpcCommand> g_npc_queue;
std::atomic<uint64_t> g_sent{0}, g_marked{0}, g_mark_runs{0};
std::atomic<int> g_mark_logs{0};

struct MarkName {
    uint32_t bit;
    const char* name;
};
const MarkName kMarks[] = {
    {MK_FACTION, "Faction"},           {MK_SCALE, "ScaleComponent"},     {MK_TRANSFORM, "CurrentTransform"},
    {MK_LEVEL, "Level"},               {MK_XP, "ExperienceSource"},      {MK_LOOT, "RandomLoot"},
    {MK_LOOT_SLOTS, "RandomLootSlotCount"}, {MK_RESIST, "DamageSusceptibility"}, {MK_DAMAGE, "DamageMod"},
    {MK_MOVEMENT, "Movement"},         {MK_AI, "DynamicEnemy01BehaviorState"},
};
constexpr size_t kMarkCount = sizeof kMarks / sizeof kMarks[0];
int32_t g_mark_slot[kMarkCount];
uint32_t g_iter_size = 0;

bool emit(KilnProgramCtx* ctx, const uint64_t* iter, int which, uint8_t** out) {
    *out = nullptr;
    if (g_ev_slot[which] < 0) return false;
    void* job = reinterpret_cast<void*>(iter[g_ev_slot[which]]);
    if (!job) return false;
    void* buf = fn.job_buffer(job);
    const uint16_t pid = fn.job_program(job);
    auto* e = static_cast<uint8_t*>(fn.emit(buf, g_ev_hash[which], kEventSize[which], pid));
    if (!e) return false;
    int64_t now = 0;
    if (g_time) g_time(ctx, &now);
    std::memcpy(e, &now, 8);  // GameEvent.timeStamp
    *out = e;
    return true;
}

// NpcCommandEvent {Time timeStamp, type +8, npcId +0xc, WorldTransform target +0x10 (position 32.32 fixed, quaternion
// +0x28, scale +0x38)}: the same fill as the game's summon path (server 0x447e6 area, docs/re/creatures.md 7.6).
void send_npc_commands(KilnProgramCtx* ctx, uint64_t* iter) {
    std::vector<KilnNpcCommand> q;
    {
        std::lock_guard lock(g_mu);
        if (g_npc_queue.empty()) return;
        q.swap(g_npc_queue);
    }
    for (const auto& c : q) {
        uint8_t* e = nullptr;
        if (!emit(ctx, iter, EV_NPC, &e)) {
            KC_W("npc command %u for 0x%08x dropped: the event buffer is full or the program lacks its slot", c.type, c.npc_id);
            continue;
        }
        e[8] = static_cast<uint8_t>(c.type);
        std::memcpy(e + 0xC, &c.npc_id, 4);
        const auto fixed = [](double m) { return static_cast<int64_t>(m * 4294967296.0); };
        const int64_t pos[3] = {fixed(c.position.x), fixed(c.position.y), fixed(c.position.z)};
        std::memcpy(e + 0x10, pos, 24);
        float rot[4] = {c.rotation[0], c.rotation[1], c.rotation[2], c.rotation[3]};
        if (rot[0] == 0 && rot[1] == 0 && rot[2] == 0 && rot[3] == 0) rot[3] = 1;
        std::memcpy(e + 0x28, rot, 16);
        const float scale[3] = {1, 1, 1};
        std::memcpy(e + 0x38, scale, 12);
        g_sent.fetch_add(1, std::memory_order_relaxed);
    }
}

void KILN_CALL cmd_each(KilnProgramCtx* ctx, uint64_t* iter, KilnEntity, void*) {
    send_npc_commands(ctx, iter);
    std::vector<KilnCreatureCommand> q;
    {
        std::lock_guard lock(g_mu);
        if (g_queue.empty()) return;
        q.swap(g_queue);
    }
    for (const auto& c : q) {
        uint8_t* e = nullptr;
        switch (c.kind) {
            case KILN_CREATURE_CMD_ATTACK:
                if (!emit(ctx, iter, EV_THREAT, &e)) break;
                std::memcpy(e + 0x8, &c.entity, 4);   // attackerId
                std::memcpy(e + 0xC, &c.target, 4);   // targetId
                std::memcpy(e + 0x10, &c.value, 4);   // threatValue
                std::memcpy(e + 0x14, &c.pool, 4);    // threatPoolId
                break;
            case KILN_CREATURE_CMD_CONTROL:
                if (!emit(ctx, iter, EV_CONTROL, &e)) break;
                std::memcpy(e + 0x8, &c.entity, 4);
                std::memcpy(e + 0xC, &c.target, 4);
                break;
            case KILN_CREATURE_CMD_HEAL: {
                if (!emit(ctx, iter, EV_RESTORE, &e)) break;
                std::memcpy(e + 0x8, &c.entity, 4);   // enemyId
                const int64_t ns = static_cast<int64_t>(static_cast<double>(c.value) * 1e9);
                std::memcpy(e + 0x10, &ns, 8);        // duration
                break;
            }
            case KILN_CREATURE_CMD_GUARD:
            case KILN_CREATURE_CMD_STOP_GUARD:
                if (!emit(ctx, iter, c.kind == KILN_CREATURE_CMD_GUARD ? EV_START_GUARD : EV_STOP_GUARD, &e)) break;
                std::memcpy(e + 0x8, &c.entity, 4);   // guardingEntityId
                std::memcpy(e + 0xC, &c.target, 4);   // guardedEntityId
                break;
            default: break;
        }
        if (e) g_sent.fetch_add(1, std::memory_order_relaxed);
        else KC_W("command %u for %u dropped: the event buffer is full or the program lacks its slot", c.kind, c.entity);
    }
}

void KILN_CALL mark_update(KilnProgramCtx* ctx, void*) {
    static std::atomic<bool> first{true};
    if (first.exchange(false)) KC_I("marks: program kiln_creatures_marks runs");
    if (!marks_pending()) return;  // nothing to mark: no iteration at all (allowed in raw mode)
    std::unordered_map<KilnEntity, uint32_t> todo;
    mark_snapshot(&todo);
    if (todo.empty()) return;
    std::vector<KilnEntity> done;
    alignas(16) uint64_t iter[512];
    const uint32_t size = g_iter_size && g_iter_size <= sizeof iter ? g_iter_size : static_cast<uint32_t>(sizeof iter);
    const KilnResult br = W->iter_begin(ctx, iter, size);
    g_mark_runs.fetch_add(1, std::memory_order_relaxed);
    if (br != KILN_OK) {
        if (g_mark_logs.fetch_add(1) < 3) KC_W("marks: iter_begin failed (%d, size %u)", br, size);
        return;
    }
    size_t left = todo.size(), seen = 0, absent = 0, untracked = 0;
    const uint64_t before = g_marked.load();
    while (W->iter_next(ctx, iter, size)) {
        ++seen;
        if (!left) continue;  // must still run to the end of the slice
        const KilnEntity e = W->current_entity(ctx);
        auto it = todo.find(e);
        if (it == todo.end()) continue;
        --left;
        done.push_back(e);
        for (size_t k = 0; k < kMarkCount; ++k)
            if ((it->second & kMarks[k].bit) && g_mark_slot[k] >= 0 && !iter[g_mark_slot[k]]) ++absent;
        for (size_t k = 0; k < kMarkCount; ++k) {
            if (!(it->second & kMarks[k].bit) || g_mark_slot[k] < 0) continue;
            if (!iter[g_mark_slot[k]]) continue;  // optional component absent
            if (!iter[g_mark_slot[k] + 5]) {      // no change tracker in the write record: MarkWritten would not run
                ++untracked;
                continue;
            }
            W->mark_written(ctx, &iter[g_mark_slot[k]]);
            g_marked.fetch_add(1, std::memory_order_relaxed);
        }
    }
    if (!done.empty()) mark_done(done);
    if (!done.empty() && g_mark_logs.fetch_add(1) < 3)
        KC_I("marks: %zu entity(ies) to mark, %zu iterated, %zu not found, %llu component(s) marked, %zu absent, %zu "
             "without a tracker",
             todo.size(), seen, left, static_cast<unsigned long long>(g_marked.load() - before), absent, untracked);
}

}  // namespace

void program_register(const void* api) {
    W = static_cast<const KilnWorldApi*>(api);
    if (!W || W->header.size < sizeof(KilnWorldApi)) {
        W = nullptr;
        KC_W("kiln.world is unavailable: commands and replication marks are off");
        return;
    }
    uintptr_t t = 0;
    if (g_api->symbol(kstr("gameplay/ProgramTime"), &t) == KILN_OK && t) g_time = reinterpret_cast<ProgramTimeFn>(t);
    // commands
    if (F.commands) {
        KilnStr out[EV_COUNT];
        for (int i = 0; i < EV_COUNT; ++i) {
            out[i] = kstr(kEvents[i]);
            g_ev_hash[i] = ecs_hash(kEvents[i]);
        }
        KilnProgramDesc d{};
        d.size = sizeof d;
        d.flags = KILN_PROGRAM_SYSTEM | KILN_PROGRAM_HOST_ONLY;
        d.name = kstr("kiln_creatures");
        d.each = &cmd_each;
        d.events_out = KilnNames{out, EV_COUNT, 0};
        if (W->register_program(g_owner, &d, &g_cmd_prog) == KILN_OK) {
            bool all = true;
            for (int i = 0; i < EV_COUNT; ++i) {
                g_ev_slot[i] = W->program_slot(g_cmd_prog, KILN_SLOT_EVENT_OUT, out[i]);
                all &= g_ev_slot[i] >= 0;
            }
            g_cmd_ok = all;
            KC_I("program kiln_creatures accepted (event-out slots %d %d %d %d %d %d, program time %s)", g_ev_slot[0],
                 g_ev_slot[1], g_ev_slot[2], g_ev_slot[3], g_ev_slot[4], g_ev_slot[5], g_time ? "yes" : "no");
        } else {
            KC_W("program kiln_creatures was refused by kiln.world: commands are off");
        }
    }
    // replication marks
    {
        KilnStr req[] = {kstr("Faction")};
        KilnStr opt[kMarkCount - 1];
        for (size_t k = 1; k < kMarkCount; ++k) opt[k - 1] = kstr(kMarks[k].name);
        KilnProgramDesc d{};
        d.size = sizeof d;
        d.flags = KILN_PROGRAM_HOST_ONLY;
        d.name = kstr("kiln_creatures_marks");
        d.update = &mark_update;
        d.write = KilnNames{req, 1, 0};
        d.write_optional = KilnNames{opt, static_cast<uint32_t>(kMarkCount - 1), 0};
        if (W->register_program(g_owner, &d, &g_mark_prog) == KILN_OK) {
            bool all = true;
            for (size_t k = 0; k < kMarkCount; ++k) {
                g_mark_slot[k] = W->program_slot(g_mark_prog, KILN_SLOT_WRITE, kstr(kMarks[k].name));
                all &= g_mark_slot[k] >= 0;
            }
            g_mark_ok = all;
            KC_I("program kiln_creatures_marks accepted (%zu write slots%s)", kMarkCount, all ? "" : ", some missing");
        } else {
            KC_W("program kiln_creatures_marks was refused by kiln.world: Kiln's writes replicate only where the game "
                 "copies them itself (Level -> NetworkLevel)");
        }
    }
}

namespace {
bool active(KilnHandle h, bool ok) {
    if (!ok || !W) return false;
    KilnProgramInfo info{};
    info.size = sizeof info;
    if (W->program_info(h, &info) != KILN_OK || info.state != KILN_PROGRAM_ACTIVE) return false;
    if (h == g_mark_prog && !g_iter_size) g_iter_size = info.iter_size;
    return true;
}
}  // namespace

bool program_commands_active() { return active(g_cmd_prog, g_cmd_ok); }
bool program_marks_active() { return active(g_mark_prog, g_mark_ok); }
uint64_t program_commands_sent() { return g_sent.load(); }
uint64_t program_marks_written() { return g_marked.load(); }

KilnResult program_command(const KilnCreatureCommand* c) {
    if (!c || c->size < sizeof(KilnCreatureCommand) || c->kind < KILN_CREATURE_CMD_ATTACK ||
        c->kind > KILN_CREATURE_CMD_CONTROL || !c->entity)
        return KILN_ERR_INVALID_ARGUMENT;
    if (!program_commands_active()) return KILN_ERR_UNAVAILABLE;
    std::lock_guard lock(g_mu);
    if (g_queue.size() >= 256) return KILN_ERR_BUFFER_TOO_SMALL;
    g_queue.push_back(*c);
    return KILN_OK;
}

KilnResult KILN_CALL program_npc_command(const KilnNpcCommand* c) {
    if (!c || c->size < sizeof(KilnNpcCommand) || c->type > KILN_NPC_CMD_GOTO || !c->npc_id) return KILN_ERR_INVALID_ARGUMENT;
    if (!program_commands_active()) return KILN_ERR_UNAVAILABLE;
    std::lock_guard lock(g_mu);
    if (g_npc_queue.size() >= 256) return KILN_ERR_BUFFER_TOO_SMALL;
    g_npc_queue.push_back(*c);
    return KILN_OK;
}

}  // namespace kcr
