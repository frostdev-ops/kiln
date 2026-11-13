// SPDX-License-Identifier: MIT
// A native consumer built against kiln.world v2: compiled with the header 0.2.0-dev.26 shipped (tests/v2, kept as it
// was released, never edited), linked into writes_test, which hands it a v3 table. It does what such a consumer does
// (items.cpp, combat.cpp): accept the table when header.size covers its own struct and the version is at least the one
// it asked for, then call through its own view of the fields. The test checks that every call lands where v3 put
// that entry and that each struct the two share has kept its size.
#include <cstddef>
#include <cstdint>

#include "services/kiln_world.h"  // tests/v2/services/kiln_world.h: run.sh puts tests/v2 first

static_assert(KILN_WORLD_VERSION == 2u, "this file must see the released v2 header, not the current one");

extern "C" {

// The v2 consumer's own acceptance: what it would do with the table `service("kiln.world", 2)` returned.
bool v2_accepts(const void* table) {
    const auto* w = static_cast<const KilnWorldApi*>(table);
    return w && w->header.size >= sizeof(KilnWorldApi) && w->header.version >= 2;
}

// Calls every entry of the v2 table once with harmless arguments; the provider's stubs record which entry was hit.
void v2_call_all(const void* table) {
    const auto* w = static_cast<const KilnWorldApi*>(table);
    KilnStr none{"", 0};
    w->register_program(0, nullptr, nullptr);
    w->program_slot(0, 0, none);
    w->program_info(0, nullptr);
    w->iter_begin(nullptr, nullptr, 0);
    w->iter_next(nullptr, nullptr, 0);
    w->current_entity(nullptr);
    w->command_buffer(nullptr);
    w->queue_push(nullptr, 0, 0, 0);
    w->queue_add(nullptr, 0, 0, nullptr, 0);
    w->queue_remove(nullptr, 0, 0);
    w->mark_written(nullptr, nullptr);
    w->read_other(nullptr, 0, 0, nullptr);
    w->component_hash(none);
    w->component(0, 0, nullptr);
    w->exists(0);
    w->transform(0, nullptr);
    w->position(0, nullptr);
    w->find(0, nullptr, 0, nullptr);
    w->entity_count();
    w->server_world();
    w->teleport(0, nullptr, nullptr);
    w->teleport_cancel(0);
    w->status(nullptr);
    w->write_other(nullptr, 0, 0, nullptr);
    w->unregister_program(0);
    w->bind_program(0, none, none, nullptr, nullptr, nullptr);
    w->capabilities();
    w->program_events(nullptr, none, nullptr, nullptr);
    w->program_emit(nullptr, none, nullptr, 0);
}

uint32_t v2_entries() { return 29; }
uint32_t v2_table_size() { return sizeof(KilnWorldApi); }
uint32_t v2_cap_bits() { return KILN_WORLD_CAP_WRITE_OTHER | KILN_WORLD_CAP_MULTI_EVENTS | KILN_WORLD_CAP_BIND_PROGRAM; }

// The sizes of the structs a v2 consumer passes or receives, in this order.
uint32_t v2_struct_sizes(uint32_t* out, uint32_t capacity) {
    const uint32_t sizes[] = {
        sizeof(KilnProgramDesc), sizeof(KilnProgramInfo), sizeof(KilnNames), sizeof(KilnTransform), sizeof(KilnVec3),
        sizeof(KilnTeleportRequest), sizeof(KilnTeleportResult), sizeof(KilnWorldStatus),
    };
    const uint32_t n = sizeof sizes / sizeof sizes[0];
    for (uint32_t i = 0; i < n && i < capacity; ++i) out[i] = sizes[i];
    return n;
}

}  // extern "C"
