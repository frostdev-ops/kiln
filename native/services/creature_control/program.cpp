// SPDX-License-Identifier: MIT
// kiln.creature_control: the broker's world program kiln_creature_control (SYSTEM, host only). It declares other-write
// ActorInput and SteeringAgent, which makes it serial and conflicting with every program that reads or writes either:
// the scheduler runs it after the game's steering program (registration order), and a client's feed program registered
// after it can never run beside it. It does only what the last Pre decided (Broker::pre): zero a body's movement input
// (write_other), queue the removal of its SteeringAgent, queue the saved one back. The engine applies the queued
// component changes at the end of the step; the next Pre checks them at its job barrier. It holds no lock while it
// calls the game: the ops are taken out of the shared list first.
#include <mutex>

#include "internal.h"
#include "snapshot.h"

namespace kcc {

namespace {

const KilnWorldApi* W = nullptr;
KilnHandle g_program = 0;
int32_t g_slot_input = -1;
uint32_t h_steering = 0;
InputLayout g_input{};

std::mutex g_mu;          // guards g_ops only
std::vector<Op> g_ops;
std::atomic<uint64_t> g_runs{0}, g_zeroed{0}, g_removed{0}, g_added{0}, g_failed{0};
std::atomic<int> g_warned{0};

void warn_once(const char* what, KilnEntity e, int rc) {
    if (g_warned.fetch_add(1, std::memory_order_relaxed) < 8)
        logf(KILN_LOG_WARN, "program: %s for body %u failed (%d)", what, e, rc);
}

void KILN_CALL each(KilnProgramCtx* ctx, uint64_t* iter, KilnEntity, void*) {
    g_runs.fetch_add(1, std::memory_order_relaxed);
    std::vector<Op> ops;
    {
        std::lock_guard lock(g_mu);
        if (g_ops.empty()) return;
        ops.swap(g_ops);
    }
    for (const Op& op : ops) {
        switch (op.kind) {
            case OpKind::ZeroInput: {
                uint32_t n = 0;
                auto* p = g_slot_input >= 0 ? static_cast<uint8_t*>(W->write_other(ctx, iter[g_slot_input], op.entity, &n))
                                            : nullptr;
                if (p && zero_input(p, n, g_input)) g_zeroed.fetch_add(1, std::memory_order_relaxed);
                else {
                    g_failed.fetch_add(1, std::memory_order_relaxed);
                    warn_once("zeroing ActorInput", op.entity, p ? -1 : 0);
                }
                break;
            }
            case OpKind::RemoveSteering: {
                const KilnResult rc = W->queue_remove(ctx, op.entity, h_steering);
                if (rc == KILN_OK) g_removed.fetch_add(1, std::memory_order_relaxed);
                else {
                    g_failed.fetch_add(1, std::memory_order_relaxed);
                    warn_once("queue_remove SteeringAgent", op.entity, rc);
                }
                break;
            }
            case OpKind::AddSteering: {
                const KilnResult rc = W->queue_add(ctx, op.entity, h_steering, op.data.data(), static_cast<uint32_t>(op.data.size()));
                if (rc == KILN_OK) g_added.fetch_add(1, std::memory_order_relaxed);
                else {
                    g_failed.fetch_add(1, std::memory_order_relaxed);
                    warn_once("queue_add SteeringAgent", op.entity, rc);
                }
                break;
            }
        }
    }
}

}  // namespace

bool program_register(const KilnWorldApi* world, std::string* why) {
    W = world;
    h_steering = W->component_hash(KilnStr{"SteeringAgent", 13});
    g_input.size = g_layout.input_size;
    g_input.local_move = g_layout.input_local_move;
    g_input.world_move = g_layout.input_world_move;
    g_input.speed = g_layout.input_speed;
    static const KilnStr kOther[] = {KilnStr{"ActorInput", 10}, KilnStr{"SteeringAgent", 13}};
    KilnProgramDesc d{};
    d.size = sizeof d;
    d.flags = KILN_PROGRAM_SYSTEM | KILN_PROGRAM_HOST_ONLY;
    d.name = KilnStr{"kiln_creature_control", 21};
    d.each = &each;
    d.other_write = KilnNames{kOther, 2, 0};
    if (W->register_program(g_owner, &d, &g_program) != KILN_OK) {
        *why = "kiln.world refused the program kiln_creature_control";
        return false;
    }
    g_slot_input = W->program_slot(g_program, KILN_SLOT_OTHER_WRITE, kOther[0]);
    if (g_slot_input < 0) {
        *why = "the program kiln_creature_control has no other-write slot for ActorInput";
        return false;
    }
    logf(KILN_LOG_INFO, "program kiln_creature_control accepted (SYSTEM, host only, other-write ActorInput + SteeringAgent; "
                        "ActorInput slot %d)", g_slot_input);
    return true;
}

uint32_t program_state() {
    if (!W || !g_program) return KILN_PROGRAM_REFUSED;
    KilnProgramInfo info{};
    info.size = sizeof info;
    return W->program_info(g_program, &info) == KILN_OK ? info.state : KILN_PROGRAM_REFUSED;
}

void program_publish(std::vector<Op>&& ops) {
    std::lock_guard lock(g_mu);
    g_ops = std::move(ops);
}

ProgramCounters program_counters() {
    return ProgramCounters{g_runs.load(), g_zeroed.load(), g_removed.load(), g_added.load(), g_failed.load()};
}

}  // namespace kcc
