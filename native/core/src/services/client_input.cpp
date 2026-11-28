// SPDX-License-Identifier: MIT
// kiln.client's local input selection and the entry of its observation (client_input.h).
#include "client_input.h"

#include <atomic>

#include "../guard.h"

namespace kiln::client_input {
namespace {

std::atomic<uint64_t> g_selection{0};  // session << 32 | entity: one atomic word, so a reader never mixes two
std::atomic<uint64_t> g_visits{0}, g_visit_faults{0};
std::atomic<Consumer> g_consumer{nullptr};

bool guarded_read(void*, void* dst, uintptr_t src, size_t n) { return guard::copy(dst, src, n); }
bool guarded_write(void*, uintptr_t dst, const void* src, size_t n) {
    return dst && guard::copy(reinterpret_cast<void*>(dst), reinterpret_cast<uintptr_t>(src), n);
}

}  // namespace

void publish(KilnEntity entity, uint32_t session) {
    g_selection.store(static_cast<uint64_t>(session) << 32 | entity, std::memory_order_release);
}

Selection selection() {
    const uint64_t v = g_selection.load(std::memory_order_acquire);
    return {static_cast<KilnEntity>(v & 0xffffffffu), static_cast<uint32_t>(v >> 32)};
}

void observe(uintptr_t record, KilnEntity entity, uint32_t version) {
    const Selection s = selection();
    if (!record || !s.entity || !s.session || entity != s.entity) return;  // only the local player's input
    building_input::observe({record, entity, s.session, version}, guarded_memory());
}

void visit(uintptr_t input, uintptr_t record, KilnEntity entity, const Site& site) {
    g_visits.fetch_add(1, std::memory_order_relaxed);
    if (!input || !record) return;
    uintptr_t world = 0;
    uint32_t step = 0;
    // The game's own rule for its input actions' version: the client world's step counter, read where it reads it.
    if (!guard::copy(&world, input + site.world, sizeof world) || !world ||
        !guard::copy(&step, world + site.step, sizeof step)) {
        g_visit_faults.fetch_add(1, std::memory_order_relaxed);
        return;
    }
    const Selection s = selection();
    if (const Consumer c = g_consumer.load(std::memory_order_acquire); c && s.entity && s.session && entity == s.entity)
        c(record, input, world, entity, s.session, step);
    observe(record, entity, step);
}

void set_consumer(Consumer consumer) { g_consumer.store(consumer, std::memory_order_release); }

Stats stats() { return {g_visits.load(std::memory_order_relaxed), g_visit_faults.load(std::memory_order_relaxed)}; }

void reset_stats_for_tests() {
    g_visits.store(0, std::memory_order_relaxed);
    g_visit_faults.store(0, std::memory_order_relaxed);
}

building_input::Memory guarded_memory() { return {&guarded_read, &guarded_write, nullptr}; }

}  // namespace kiln::client_input
