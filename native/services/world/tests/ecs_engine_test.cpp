// SPDX-License-Identifier: MIT
// Host test: kiln.world's engine backend (ecs_engine.cpp) under the verified-copy logic (ecs_reads.cpp), over the real
// kiln.engine (engine.cpp booted through kiln_plugin_main with a fake core; its kiln.internal.engine table gives the
// views and the guarded copy) and a fake engine laid out from each exact profile's offsets
// (native/services/engine/tests/fake_engine.h). The test adds an entity world to it: the component registration
// catalog (records, names in the image, the size array), the entity id table (occupancy bits, keys, record slots,
// the engine's own id hash and probe), entity records, archetype layouts (bits, offsets, strides), chunk rows and
// template definitions. Every engine byte goes through the table's copy (host: process_vm_readv, so an unmapped page
// fails the copy as SEH does on Windows). Mutations between and during reads: a recycled id, a removed entity, an
// archetype change, a moved row, faulting pages, a catalog that does not check out, a stride that disagrees with the
// registration, a view naming another world, a session change; concurrent writers under ThreadSanitizer.
#include "ecs_harness.h"

namespace {
using namespace kw::ecs;
using namespace fakeworld;
using kiln::engine::Profile;
constexpr KilnOwner kOwner = 21, kOther = 22;
std::vector<uint8_t> read_component(Backend& b, const KilnObjectRef& r, uint32_t hash, uint32_t size, KilnResult* rc) {
    std::vector<uint8_t> buf(size, 0xAA);
    uint32_t req = 0;
    *rc = ecs_snapshot(b, kOwner, &r, hash, buf.data(), size, &req);
    return buf;
}

// Everything a mod reads from a task, on one world kind.
void reads(const Profile& p, World& w, Reader& reader, Worlds& worlds, uint32_t kind) {
    Backend b = reader.backend();
    const std::string k = kind == KILN_WORLD_SERVER ? "server world" : "client world";
    KilnEcsContextInfo ci{}; ci.size = sizeof ci;
    check(ecs_context_snapshot(b, &ci) == KILN_OK && ci.exact_profile == 1 && (ci.present_mask & (1u << kind)) &&
              ci.sessions[kind] == worlds.session[kind] && ci.layout_stamp != 0,
          k + ": context snapshot names the world, its session and a layout stamp");

    KilnObjectRef player{};
    check(entity_open(b, kOwner, 1, kind, 0, 1, &player) == KILN_OK && player.entity == 1 && player.generation == 1 &&
              player.session == worlds.session[kind] && player.reload_epoch == 1 &&
              player.layout_stamp == get<uint64_t>(w.rec_of[1] + p.layout.entities.entity_layout),
          k + ": open the player: id, generation, session, epoch, layout");
    KilnObjectInfo info{}; info.size = sizeof info;
    check(entity_validate(b, kOwner, &player, &info) == KILN_OK && (info.flags & KILN_OBJECT_ALIVE) &&
              (info.flags & KILN_OBJECT_HAS_TEMPLATE) && info.generation == 1,
          k + ": the player validates alive with a template");

    KilnResult rc{};
    auto t = read_component(b, player, kw::kCurrentTransformHash, 56, &rc);
    KilnTransform tr{};
    check(rc == KILN_OK && kw::transform_decode(t.data(), 56, &tr) && tr.position.x == 100.0 && tr.position.y == 50.0 &&
              tr.position.z == -20.0,
          k + ": CurrentTransform read from a task decodes the player's position");
    auto inv = read_component(b, player, H(w, "Inventory"), 48, &rc);
    check(rc == KILN_OK && inv[0] == 1 && inv[47] == 48, k + ": the player's Inventory bytes");
    uint32_t req = 0;
    std::vector<uint8_t> small(4, 0xAA);
    check(ecs_snapshot(b, kOwner, &player, H(w, "Inventory"), small.data(), 4, &req) == KILN_ERR_BUFFER_TOO_SMALL &&
              req == 48 && all_eq(small, 0xAA),
          k + ": a short buffer gets the registered size and nothing");
    check(ecs_snapshot(b, kOwner, &player, H(w, "Unused"), small.data(), 4, &req) == KILN_ERR_NOT_FOUND,
          k + ": a registered component the entity's archetype lacks is absent");
    check(ecs_snapshot(b, kOwner, &player, 0x12345678u, small.data(), 4, &req) == KILN_ERR_NOT_FOUND,
          k + ": a hash no component is registered under is absent");

    uint8_t guid[16]{};
    check(ecs_template_uuid(b, kOwner, &player, guid) == KILN_OK && std::memcmp(guid, kUuidPlayer, 16) == 0,
          k + ": the player's template UUID through its definition");
    KilnObjectRef rock{};
    check(entity_open(b, kOwner, 1, kind, 0, 4099, &rock) == KILN_OK && entity_validate(b, kOwner, &rock, &info) == KILN_OK &&
              !(info.flags & KILN_OBJECT_HAS_TEMPLATE) && ecs_template_uuid(b, kOwner, &rock, guid) == KILN_ERR_NOT_FOUND,
          k + ": a save-loaded entity without a definition has no template");

    KilnEcsSnapshotSpec specs[3] = {{kw::kCurrentTransformHash, 0}, {H(w, "Unused"), 0}, {H(w, "Health"), 0}};
    std::vector<uint8_t> packed(64, 0xAA);
    uint32_t presence[3] = {9, 9, 9};
    check(ecs_snapshot_many(b, kOwner, &player, specs, 3, packed.data(), 64, &req, presence) == KILN_OK && req == 64 &&
              presence[0] == KILN_ECS_OK && presence[1] == KILN_ECS_ABSENT && presence[2] == KILN_ECS_OK &&
              packed[56] == 0x64 && packed[63] == 0x3f,
          k + ": snapshot_many packs CurrentTransform and Health, the absent one takes no room");

    // Queries: every entity with CurrentTransform; containers (Inventory) near the player.
    KilnEcsQueryRequest q{}; q.size = sizeof q; q.world_kind = kind;
    uint32_t ct[1] = {kw::kCurrentTransformHash};
    q.components = ct; q.component_count = 1;
    KilnObjectRef refs[16] = {};
    uint32_t state = 0;
    check(ecs_query(b, kOwner, &q, refs, 16, &req, &state) == KILN_OK && req == 7 && state == KILN_ECS_OK &&
              refs[0].entity == 1 && refs[1].entity == 4097 && refs[6].entity > 5000 && refs[3].reload_epoch == 1,
          k + ": query every CurrentTransform: complete, ascending, with the caller's epoch");
    uint32_t containers[2] = {kw::kCurrentTransformHash, H(w, "Inventory")};
    q.components = containers; q.component_count = 2;
    q.flags = KILN_ECS_QUERY_BOUNDS; q.center = {100, 50, -20}; q.radius = 5;
    check(ecs_query(b, kOwner, &q, refs, 16, &req, &state) == KILN_OK && req == 2 && refs[0].entity == 1 &&
              refs[1].entity == 4097,
          k + ": query containers within 5 m of the player: the player and the chest");
    q.radius = 2;
    check(ecs_query(b, kOwner, &q, refs, 16, &req, &state) == KILN_OK && req == 1 && refs[0].entity == 1,
          k + ": a 2 m radius leaves the chest out");
    q.flags = 0;
    KilnObjectRef chest{};
    check(entity_open(b, kOwner, 1, kind, 0, 4097, &chest) == KILN_OK && chest.generation == 3, k + ": open the chest");
    auto cinv = read_component(b, chest, H(w, "Inventory"), 48, &rc);
    check(rc == KILN_OK && cinv[0] == (1 ^ 0x80), k + ": the chest's Inventory bytes");
    check(ecs_template_uuid(b, kOwner, &chest, guid) == KILN_OK && std::memcmp(guid, kUuidChest, 16) == 0,
          k + ": the chest's template UUID");

    // Colliding ids are found past each other in the probe; an id that is not there is absent.
    std::vector<uint32_t> same;
    for (uint32_t id = 5000; same.size() < 3; ++id)
        if ((id_hash(id) & (w.cap - 1)) == (id_hash(5000) & (w.cap - 1))) same.push_back(id);
    KilnObjectRef c{};
    check(entity_open(b, kOwner, 1, kind, 0, same[2], &c) == KILN_OK && c.entity == same[2], k + ": the third of three colliding ids");
    check(entity_open(b, kOwner, 1, kind, 0, 777777, &c) == KILN_ERR_NOT_FOUND, k + ": an id that is not in the table is absent");
}

// Mutations between and during reads, on the server world of `w`.
void mutations(const Profile& p, World& w, Reader& reader, Worlds& worlds) {
    Backend b = reader.backend();
    const auto& E = p.layout.entities;
    KilnObjectRef player{}, chest{};
    entity_open(b, kOwner, 1, KILN_WORLD_SERVER, 0, 1, &player);
    entity_open(b, kOwner, 1, KILN_WORLD_SERVER, 0, 4097, &chest);
    KilnObjectInfo info{}; info.size = sizeof info;
    KilnResult rc{};

    // Recycled id: the slot names a record of a new generation.
    const uintptr_t rec = w.rec_of[4097];
    put<uint32_t>(rec + E.entity_generation, 4);
    check(entity_validate(b, kOwner, &chest, &info) == KILN_ECS_ERR_STALE && info.generation == 4 &&
              !(info.flags & KILN_OBJECT_ALIVE),
          "recycled id: stale, and the info names the new generation");
    auto bytes = read_component(b, chest, H(w, "Inventory"), 48, &rc);
    check(rc == KILN_ECS_ERR_STALE && all_eq(bytes, 0xAA), "recycled id: a snapshot is stale and copies nothing");
    put<uint32_t>(rec + E.entity_generation, 3);
    check(entity_validate(b, kOwner, &chest, &info) == KILN_OK, "the generation back: valid again");

    // Archetype change (a component added): the layout stamp moves, stale.
    const uintptr_t old_layout = get<uint64_t>(rec + E.entity_layout);
    const uintptr_t grown = w.archetype({"keen::ecs::CurrentTransform", "keen::ecs::Inventory", "keen::ecs::Health"});
    put<uint64_t>(rec + E.entity_layout, grown);
    check(entity_validate(b, kOwner, &chest, &info) == KILN_ECS_ERR_STALE, "archetype change: stale");
    put<uint64_t>(rec + E.entity_layout, old_layout);

    // A row moved inside the same archetype: the same entity, its bytes elsewhere; a ref stays valid and reads them.
    const uintptr_t lay = old_layout;
    std::vector<uint8_t> inv(48);
    std::memcpy(inv.data(), reinterpret_cast<void*>(w.row_address(lay, 0, "keen::ecs::Inventory")), 48);
    std::memcpy(reinterpret_cast<void*>(w.row_address(lay, 5, "keen::ecs::Inventory")), inv.data(), 48);
    std::vector<uint8_t> t(56);
    std::memcpy(t.data(), reinterpret_cast<void*>(w.row_address(lay, 0, "keen::ecs::CurrentTransform")), 56);
    std::memcpy(reinterpret_cast<void*>(w.row_address(lay, 5, "keen::ecs::CurrentTransform")), t.data(), 56);
    put<uint32_t>(rec + E.entity_row, 5);
    bytes = read_component(b, chest, H(w, "Inventory"), 48, &rc);
    check(rc == KILN_OK && bytes == inv, "a moved row: the ref still validates and reads the bytes at the new row");
    put<uint32_t>(rec + E.entity_row, 0);

    // Removed: the occupancy bit is clear.
    w.remove(4097);
    check(entity_validate(b, kOwner, &chest, &info) == KILN_ERR_NOT_FOUND && info.status == KILN_ECS_ABSENT,
          "removed entity: absent");
    KilnObjectRef none{};
    check(entity_open(b, kOwner, 1, KILN_WORLD_SERVER, 0, 4097, &none) == KILN_ERR_NOT_FOUND && none.entity == 0,
          "removed entity: open is absent");
    const size_t s = w.slot_of(4097);
    put<uint64_t>(w.occ.at((s / 64) * 8), get<uint64_t>(w.occ.at((s / 64) * 8)) | (uint64_t{1} << (s % 64)));
    check(entity_validate(b, kOwner, &chest, &info) == KILN_OK, "back in the table: valid");

    // A slot whose record names another id: the record is not the key's (unreadable, never that record's facts).
    const uint32_t real_id = get<uint32_t>(rec + E.entity_id);
    put<uint32_t>(rec + E.entity_id, 9999);
    check(entity_validate(b, kOwner, &chest, &info) == KILN_ECS_ERR_UNREADABLE, "a record whose id is not its key's: unreadable");
    put<uint32_t>(rec + E.entity_id, real_id);
    // The id gets a new record while its old one is being read (the slot is rewritten under the read): the old record's
    // facts must not be answered as the id's.
    {
        const uintptr_t fresh = w.recs.at(900 * kRecord);
        std::memcpy(reinterpret_cast<void*>(fresh), reinterpret_cast<const void*>(rec), kRecord);
        put<uint32_t>(fresh + E.entity_generation, 4);
        const size_t slot = w.slot_of(4097);
        bool armed = true;
        g_copy_hook = [&](uintptr_t address) {
            if (armed && address == rec + E.entity_generation) {
                armed = false;
                put<uint64_t>(w.slots.at(slot * 8), fresh);
            }
        };
        check(entity_validate(b, kOwner, &chest, &info) != KILN_OK,
              "the slot rewritten while the old record is read: not answered as the id's entity");
        g_copy_hook = nullptr;
        put<uint64_t>(w.slots.at(slot * 8), rec);
        check(entity_validate(b, kOwner, &chest, &info) == KILN_OK, "the slot back: valid");
    }

    // An id inserted while its first lookup runs: absent is answered only by two lookups that agree.
    {
        const uint32_t late = 6001;
        const size_t home = id_hash(late) & (w.cap - 1);
        // The first lookup reads the home word with the bit clear; the entity appears as the second one reads it.
        bool armed = true;
        int seen = 0;
        g_copy_hook = [&](uintptr_t address) {
            if (address == w.occ.at((home / 64) * 8) && ++seen == 2 && armed) {
                armed = false;
                w.add(late, 1, get<uint64_t>(rec + E.entity_layout), 9, nullptr);
            }
        };
        KilnObjectRef r{};
        const KilnResult rc = entity_open(b, kOwner, 1, KILN_WORLD_SERVER, 0, late, &r);
        g_copy_hook = nullptr;
        check(rc != KILN_ERR_NOT_FOUND, "an id inserted during its first lookup is not answered absent (" + std::to_string(rc) + ")");
        check(entity_open(b, kOwner, 1, KILN_WORLD_SERVER, 0, late, &r) == KILN_OK, "the inserted id opens afterwards");
        w.remove(late);
    }

    // Faulting pages: the record, the chunk, the occupancy words. Unreadable, never a crash, nothing copied.
    const uintptr_t saved_slot = get<uint64_t>(w.slots.at(s * 8));
    put<uint64_t>(w.slots.at(s * 8), faulting());
    check(entity_validate(b, kOwner, &chest, &info) == KILN_ECS_ERR_UNREADABLE && info.status == KILN_ECS_UNREADABLE,
          "a record on a faulting page: unreadable");
    put<uint64_t>(w.slots.at(s * 8), saved_slot);
    const uintptr_t saved_storage = get<uint64_t>(rec + E.entity_storage);
    put<uint64_t>(rec + E.entity_storage, faulting());
    bytes = read_component(b, chest, H(w, "Inventory"), 48, &rc);
    check(rc == KILN_ECS_ERR_UNREADABLE && all_eq(bytes, 0xAA), "component bytes on a faulting page: unreadable, nothing copied");
    put<uint64_t>(rec + E.entity_storage, saved_storage);
    const uintptr_t saved_occ = get<uint64_t>(w.manager + E.manager_occupancy);
    put<uint64_t>(w.manager + E.manager_occupancy, faulting());
    check(entity_validate(b, kOwner, &chest, &info) == KILN_ECS_ERR_UNREADABLE, "occupancy words on a faulting page: unreadable");
    put<uint64_t>(w.manager + E.manager_occupancy, saved_occ);
    check(entity_validate(b, kOwner, &chest, &info) == KILN_OK, "every page back: valid");

    // A registration row whose hash does not match its name: the catalog does not check out.
    const Comp inv_comp = w.comp("keen::ecs::Inventory");
    const uintptr_t row = w.catalog.at(inv_comp.index * 256);
    put<uint32_t>(row + 32, inv_comp.hash ^ 1);
    bytes = read_component(b, chest, H(w, "Inventory"), 48, &rc);
    check(rc != KILN_OK && all_eq(bytes, 0xAA), "a catalog row that changed under the cache: refused, nothing copied");
    bytes = read_component(b, chest, H(w, "Inventory"), 48, &rc);
    check(rc == KILN_ERR_UNAVAILABLE && all_eq(bytes, 0xAA) && std::string(reader.last_refusal()).find("catalog") != std::string::npos,
          "a catalog row whose hash is not its name's: unavailable, and the reason names the catalog");
    put<uint32_t>(row + 32, inv_comp.hash);
    bytes = read_component(b, chest, H(w, "Inventory"), 48, &rc);
    check(rc == KILN_OK, "the row restored: reads again");

    // A stride that disagrees with the registered size: unavailable, never a guessed size.
    const uintptr_t stride = lay + E.component_strides + inv_comp.index * 2;
    put<uint16_t>(stride, 40);
    bytes = read_component(b, chest, H(w, "Inventory"), 48, &rc);
    check(rc == KILN_ERR_UNAVAILABLE && all_eq(bytes, 0xAA), "a stride that is not the registered size: unavailable");
    put<uint16_t>(stride, 48);

    // Kiln's world of the kind is not the object the engine view resolves: no entity read.
    const uintptr_t real = worlds.world[KILN_WORLD_SERVER].load();
    worlds.world[KILN_WORLD_SERVER] = real + 0x1000;
    check(entity_validate(b, kOwner, &chest, &info) == KILN_ERR_UNAVAILABLE &&
              std::string(reader.last_refusal()).find("another world") != std::string::npos,
          "a view naming another world than Kiln's: unavailable, with the reason");
    KilnEcsContextInfo ci{}; ci.size = sizeof ci;
    check(ecs_context_snapshot(b, &ci) == KILN_OK && (ci.present_mask & 1u), "the context still answers");
    worlds.world[KILN_WORLD_SERVER] = real;

    // A new session (the world was replaced): every ref of the old one is stale.
    worlds.session[KILN_WORLD_SERVER] = worlds.session[KILN_WORLD_SERVER] + 1;
    check(entity_validate(b, kOwner, &player, &info) == KILN_ECS_ERR_STALE && info.session == worlds.session[KILN_WORLD_SERVER],
          "a session change: stale");
    worlds.session[KILN_WORLD_SERVER] = worlds.session[KILN_WORLD_SERVER] - 1;

    // Reload epochs: the owner reloads (opens under 2), its refs of epoch 1 go stale, others keep their own.
    KilnObjectRef p2{};
    check(entity_open(b, kOwner, 2, KILN_WORLD_SERVER, 0, 1, &p2) == KILN_OK && p2.reload_epoch == 2, "reload: open under 2");
    check(entity_validate(b, kOwner, &player, &info) == KILN_ECS_ERR_STALE, "reload: the epoch-1 ref is stale");
    check(entity_validate(b, kOwner, &p2, &info) == KILN_OK, "reload: the epoch-2 ref validates");
    check(entity_open(b, kOwner, 1, KILN_WORLD_SERVER, 0, 1, &none) == KILN_ECS_ERR_STALE, "reload: opening under 1 again is stale");
    KilnObjectRef other{};
    check(entity_open(b, kOther, 0, KILN_WORLD_SERVER, 0, 1, &other) == KILN_OK && entity_validate(b, kOther, &other, &info) == KILN_OK,
          "another owner keeps its own epoch");
    worlds.epochs.retire(kOwner);
    check(entity_validate(b, kOwner, &p2, &info) == KILN_ECS_ERR_STALE, "a retired owner's refs are stale");

    // The table is replaced (resized): refs still validate through the new table.
    fake::Region bigger_occ(8192), bigger_keys(32768), bigger_slots(65536);
    const uint64_t old_cap = w.cap;
    const uintptr_t o_occ = w.occ.at(), o_keys = w.keys.at(), o_slots = w.slots.at();
    std::map<uint32_t, uintptr_t> moved = w.rec_of;
    w.cap = 512;
    put<uint64_t>(w.manager + E.manager_count, 512);
    put<uint64_t>(w.manager + E.manager_occupancy, bigger_occ.at());
    put<uint64_t>(w.manager + E.manager_keys, bigger_keys.at());
    put<uint64_t>(w.manager + E.manager_table, bigger_slots.at());
    for (const auto& [id, r] : moved) {
        size_t sl = id_hash(id) & 511;
        while ((get<uint64_t>(bigger_occ.at((sl / 64) * 8)) >> (sl % 64)) & 1) sl = (sl + 1) & 511;
        put<uint64_t>(bigger_occ.at((sl / 64) * 8), get<uint64_t>(bigger_occ.at((sl / 64) * 8)) | (uint64_t{1} << (sl % 64)));
        put<uint32_t>(bigger_keys.at(sl * 4), id);
        put<uint64_t>(bigger_slots.at(sl * 8), r);
    }
    KilnObjectRef p3{};
    check(entity_open(b, kOwner, 3, KILN_WORLD_SERVER, 0, 4097, &p3) == KILN_OK && entity_validate(b, kOwner, &p3, &info) == KILN_OK,
          "a resized table: lookups go through the new one");
    w.cap = old_cap;
    put<uint64_t>(w.manager + E.manager_count, old_cap);
    put<uint64_t>(w.manager + E.manager_occupancy, o_occ);
    put<uint64_t>(w.manager + E.manager_keys, o_keys);
    put<uint64_t>(w.manager + E.manager_table, o_slots);

    // A table without a free slot on the probe path: bounded, unreadable, never a spin.
    fake::Region full_occ(4096);
    std::memset(full_occ.base, 0xff, 4096);
    put<uint64_t>(w.manager + E.manager_occupancy, full_occ.at());
    check(entity_open(b, kOwner, 3, KILN_WORLD_SERVER, 0, 999999, &none) == KILN_ECS_ERR_UNREADABLE,
          "a probe that never meets a free slot: unreadable (bounded)");
    put<uint64_t>(w.manager + E.manager_occupancy, o_occ);
}

// Writers beside readers (also the ThreadSanitizer build): the component rewritten whole, as the game's systems store
// it (one copy of the value), between rows swapped and the id recycled and restored. A reader gets a whole value or a
// refusal, never a mix, and gets whole values while the writer pauses. `byte_stores` makes the writer adversarial
// (byte by byte, so it can be descheduled with half a value stored): a mix that stays put for every read is the
// limit of any read that takes no engine lock; that run is reported, not checked (docs/kiln/services/world.md).
void concurrent(const Profile& p, World& w, Reader& reader, bool byte_stores) {
    Backend b = reader.backend();
    const auto& E = p.layout.entities;
    KilnObjectRef chest{};
    check(entity_open(b, kOwner, 9, KILN_WORLD_SERVER, 0, 4097, &chest) == KILN_OK, "concurrent: open");
    const uintptr_t rec = w.rec_of[4097];
    const uintptr_t lay = get<uint64_t>(rec + E.entity_layout);
    const uintptr_t row0 = w.row_address(lay, 0, "keen::ecs::Inventory");
    const uintptr_t row6 = w.row_address(lay, 6, "keen::ecs::Inventory");
    std::memset(reinterpret_cast<void*>(row0), 1, 48);
    std::memset(reinterpret_cast<void*>(row6), 1, 48);
    std::atomic<bool> stop{false};
    std::atomic<int> whole{0}, refused{0}, torn{0};
    std::thread writer([&] {
        unsigned n = 0;
        uint8_t value[2][48];
        std::memset(value[0], 1, 48);
        std::memset(value[1], 2, 48);
        while (!stop.load(std::memory_order_relaxed)) {
            const uint8_t* v = value[n & 1];
            if (byte_stores) {
                auto* a = reinterpret_cast<volatile uint8_t*>(row0);
                auto* c = reinterpret_cast<volatile uint8_t*>(row6);
                for (int i = 0; i < 48; ++i) { a[i] = v[i]; c[i] = v[i]; }
            } else {
                std::memcpy(reinterpret_cast<void*>(row0), v, 48);
                std::memcpy(reinterpret_cast<void*>(row6), v, 48);
            }
            if (n % 7 == 0) {  // the row moves, same generation
                const uint32_t r = get<uint32_t>(rec + E.entity_row);
                put<uint32_t>(rec + E.entity_row, r ? 0 : 6);
            }
            if (n % 11 == 0) {  // the id is recycled and given back
                put<uint32_t>(rec + E.entity_generation, 99);
                put<uint32_t>(rec + E.entity_generation, 3);
            }
            ++n;
            if (!byte_stores) std::this_thread::sleep_for(std::chrono::milliseconds(2));
        }
    });
    std::vector<std::thread> readers;
    for (int t = 0; t < 2; ++t)
        readers.emplace_back([&] {
            std::vector<uint8_t> buf(48);
            for (int i = 0; i < 400; ++i) {
                uint32_t req = 0;
                const KilnResult rc = ecs_snapshot(b, kOwner, &chest, H(w, "Inventory"), buf.data(), 48, &req);
                if (rc != KILN_OK) { refused.fetch_add(1); continue; }
                if (all_eq(buf, 1) || all_eq(buf, 2)) whole.fetch_add(1);
                else torn.fetch_add(1);
            }
        });
    for (auto& r : readers) r.join();
    stop = true;
    writer.join();
    const std::string counts = std::to_string(whole.load()) + " whole, " + std::to_string(refused.load()) + " refused, " +
                               std::to_string(torn.load()) + " mixed";
    if (byte_stores) {
        std::printf("  [%s] adversarial byte-store writer: %s (a read that takes no engine lock)\n", p.id, counts.c_str());
    } else {
        check(torn.load() == 0, "concurrent: never a mixed value (" + counts + ")");
        check(whole.load() > 0, "concurrent: whole values get through between the writes (" + counts + ")");
    }
    std::memset(reinterpret_cast<void*>(row0), 1, 48);
    put<uint32_t>(rec + E.entity_row, 0);
    put<uint32_t>(rec + E.entity_generation, 3);
}

// Frame-quiet reads over the fake engine's own Scene word (the word the gate takes) and Kiln's server step counter.
void frame_quiet(const Profile& p, World& w, Reader& reader, Worlds& worlds, fake::Engine& e, World* client) {
    Backend b = reader.backend();
    KilnObjectRef chest{};
    check(entity_open(b, kOwner, 11, KILN_WORLD_SERVER, 0, 4097, &chest) == KILN_OK, "frame-quiet: open the chest");
    KilnEcsSnapshotSpec quiet[1] = {{H(w, "Inventory"), KILN_ECS_READ_FRAME_QUIET}};
    std::vector<uint8_t> buf(48, 0xAA);
    uint32_t req = 0, presence[1] = {9};
    std::atomic<std::int32_t>* word = reinterpret_cast<std::atomic<std::int32_t>*>(e.scene_word());
    word->store(0);
    check(ecs_snapshot_many(b, kOwner, &chest, quiet, 1, buf.data(), 48, &req, presence) == KILN_OK && req == 48 &&
              presence[0] == KILN_ECS_OK && all_eq(buf, 1),
          "frame-quiet: between frames the read succeeds");
    word->store(1);  // the GameServer frame holds the word Shared
    buf.assign(48, 0xAA);
    check(ecs_snapshot_many(b, kOwner, &chest, quiet, 1, buf.data(), 48, &req, presence) == KILN_ECS_ERR_BUSY &&
              presence[0] == KILN_ECS_BUSY && all_eq(buf, 0xAA),
          "frame-quiet: a frame holding the Scene word: busy, nothing copied");
    word->store(static_cast<std::int32_t>(0x80000000u));  // a writer holds it exclusively (the write gate)
    check(ecs_snapshot_many(b, kOwner, &chest, quiet, 1, buf.data(), 48, &req, presence) == KILN_ECS_ERR_BUSY && all_eq(buf, 0xAA),
          "frame-quiet: an exclusive holder: busy");
    // A frame that was running when the read began and ends during it, its step already counted: busy.
    worlds.word = word;
    word->store(1);
    // Released at the ninth world sample: after the two validations before the window opens, inside the window.
    worlds.release_after = 8;
    buf.assign(48, 0xAA);
    check(ecs_snapshot_many(b, kOwner, &chest, quiet, 1, buf.data(), 48, &req, presence) == KILN_ECS_ERR_BUSY && all_eq(buf, 0xAA),
          "frame-quiet: a frame running at the start that ends during the read: busy");
    worlds.release_after = -1;
    word->store(0);
    worlds.step_each_read = true;
    buf.assign(48, 0xAA);
    check(ecs_snapshot_many(b, kOwner, &chest, quiet, 1, buf.data(), 48, &req, presence) == KILN_ECS_ERR_BUSY &&
              presence[0] == KILN_ECS_BUSY && all_eq(buf, 0xAA),
          "frame-quiet: the server step moved across the copy: busy, nothing copied");
    worlds.step_each_read = false;
    uint64_t step_before = worlds.steps[KILN_WORLD_SERVER].load();
    KilnEcsQueryRequest q{}; q.size = sizeof q; q.world_kind = KILN_WORLD_SERVER;
    q.flags = KILN_ECS_QUERY_FRAME_QUIET | KILN_ECS_QUERY_BOUNDS; q.center = {100, 50, -20}; q.radius = 5;
    uint32_t comps[2] = {kw::kCurrentTransformHash, H(w, "Inventory")};
    q.components = comps; q.component_count = 2;
    KilnObjectRef refs[8] = {};
    uint32_t state = 0;
    check(ecs_query(b, kOwner, &q, refs, 8, &req, &state) == KILN_OK && req == 2 && state == KILN_ECS_OK,
          "frame-quiet query with bounds between frames");
    word->store(1);
    check(ecs_query(b, kOwner, &q, refs, 8, &req, &state) == KILN_ECS_ERR_BUSY && state == KILN_ECS_BUSY,
          "frame-quiet query during a frame: busy");
    word->store(0);
    check(worlds.steps[KILN_WORLD_SERVER].load() == step_before, "frame-quiet only reads the step counter");
    if (client) {
        KilnObjectRef c{};
        check(entity_open(b, kOwner, 11, KILN_WORLD_CLIENT, 0, 4097, &c) == KILN_OK, "frame-quiet: open in the client world");
        buf.assign(48, 0xAA);
        presence[0] = 9;
        check(ecs_snapshot_many(b, kOwner, &c, quiet, 1, buf.data(), 48, &req, presence) == KILN_ECS_ERR_UNSUPPORTED &&
                  presence[0] == KILN_ECS_UNSUPPORTED && all_eq(buf, 0xAA),
              "frame-quiet on the client world: unsupported, nothing copied, no stable copy instead");
        q.world_kind = KILN_WORLD_CLIENT;
        check(ecs_query(b, kOwner, &q, refs, 8, &req, &state) == KILN_ECS_ERR_UNSUPPORTED && state == KILN_ECS_UNSUPPORTED,
              "frame-quiet query on the client world: unsupported");
    }
    (void)p;
}

// The adversarial writer again (byte stores, descheduled half way), now inside frames: it holds the Scene word Shared
// and bumps the server step, as the GameServer update does. Frame-quiet reads never return a mixed value.
void frame_quiet_writer(const Profile& p, World& w, Reader& reader, Worlds& worlds, fake::Engine& e, int round) {
    Backend b = reader.backend();
    const auto& E = p.layout.entities;
    KilnObjectRef chest{};
    check(entity_open(b, kOwner, 12, KILN_WORLD_SERVER, 0, 4097, &chest) == KILN_OK, "frame-quiet writer: open");
    const uintptr_t rec = w.rec_of[4097];
    const uintptr_t lay = get<uint64_t>(rec + E.entity_layout);
    const uintptr_t row0 = w.row_address(lay, 0, "keen::ecs::Inventory");
    const uintptr_t row6 = w.row_address(lay, 6, "keen::ecs::Inventory");
    std::memset(reinterpret_cast<void*>(row0), 1, 48);
    std::memset(reinterpret_cast<void*>(row6), 1, 48);
    auto* word = reinterpret_cast<std::atomic<std::int32_t>*>(e.scene_word());
    word->store(0);
    std::atomic<bool> stop{false};
    std::atomic<int> whole{0}, busy{0}, other{0}, torn{0};
    std::atomic<int> codes[128] = {};
    std::thread writer([&] {
        unsigned n = 0;
        while (!stop.load(std::memory_order_relaxed)) {
            word->fetch_add(1);                                 // the frame starts: the word is held Shared
            worlds.steps[KILN_WORLD_SERVER].fetch_add(1);       // Kiln's Pre of the step, inside the frame
            const uint8_t v = (n & 1) ? 2 : 1;
            auto* a = reinterpret_cast<volatile uint8_t*>(row0);
            auto* c = reinterpret_cast<volatile uint8_t*>(row6);
            for (int i = 0; i < 48; ++i) {
                a[i] = v; c[i] = v;
                if (i == 24 && n % 3 == 0) std::this_thread::yield();  // descheduled half way through the value
            }
            if (n % 7 == 0) put<uint32_t>(rec + E.entity_row, get<uint32_t>(rec + E.entity_row) ? 0 : 6);
            word->fetch_sub(1);                                 // the frame ends
            ++n;
            std::this_thread::sleep_for(std::chrono::microseconds(n % 2 ? 50 : 8000));
        }
    });
    std::vector<std::thread> readers;
    for (int t = 0; t < 2; ++t)
        readers.emplace_back([&] {
            std::vector<uint8_t> buf(48);
            KilnEcsSnapshotSpec quiet[1] = {{H(w, "Inventory"), KILN_ECS_READ_FRAME_QUIET}};
            for (int i = 0; i < 300; ++i) {
                uint32_t req = 0, presence[1] = {9};
                const KilnResult rc = ecs_snapshot_many(b, kOwner, &chest, quiet, 1, buf.data(), 48, &req, presence);
                if (rc == KILN_ECS_ERR_BUSY) { busy.fetch_add(1); continue; }
                if (rc != KILN_OK) { other.fetch_add(1); codes[rc % 128].fetch_add(1); continue; }
                if (all_eq(buf, 1) || all_eq(buf, 2)) whole.fetch_add(1);
                else torn.fetch_add(1);
            }
        });
    for (auto& r : readers) r.join();
    stop = true;
    writer.join();
    for (int c = 0; c < 128; ++c) if (codes[c].load()) std::printf("    refusal %d: %d\n", c, codes[c].load());
    const std::string counts = std::to_string(whole.load()) + " whole, " + std::to_string(busy.load()) + " busy, " +
                               std::to_string(other.load()) + " other refusals, " + std::to_string(torn.load()) + " mixed";
    std::printf("  [%s] frame-quiet round %d against the adversarial writer: %s\n", p.id, round, counts.c_str());
    check(torn.load() == 0, "frame-quiet: never a mixed value against the adversarial writer (" + counts + ")");
    check(whole.load() > 0, "frame-quiet: whole values between frames (" + counts + ")");
    std::memset(reinterpret_cast<void*>(row0), 1, 48);
    put<uint32_t>(rec + E.entity_row, 0);
}

void exact(const Profile& p) {
    g_case = p.id;
    KilnApi api = make_api();
    const KilnEngineNativeApi* native = boot_engine(p, api);
    check(native && native->layout() == &p.layout, "the real kiln.internal.engine table for the exact profile");
    if (!native) return;
    fake::Engine e(p.layout);
    kiln::engine::mem::test_image = e.image.at();
    check(kw::ecs::engine::layout_refusal(&p.layout) == nullptr, "the exact profile's read layout serves entity reads");
    Worlds worlds;
    worlds.world[KILN_WORLD_SERVER] = e.exec.at();
    worlds.session[KILN_WORLD_SERVER] = 41;
    World server(p.layout, e.image, e.registry_owner(), e.manager());
    populate(server);
    std::unique_ptr<World> client;
    if (!p.layout.dedicated_server) {
        worlds.world[KILN_WORLD_CLIENT] = e.main_exec.at();
        worlds.session[KILN_WORLD_CLIENT] = 42;
        worlds.world[KILN_WORLD_PREDICTION] = e.main_exec.at() + 64;  // a storage only: no view
        worlds.session[KILN_WORLD_PREDICTION] = 43;
        client = std::make_unique<World>(p.layout, e.image, e.main_exec.at(p.layout.registry_owner_offset),
                                         e.main_exec.at(p.layout.entity_manager_offset));
        populate(*client);
    }
    g_real = native;
    KilnEngineNativeApi hooked = *native;
    hooked.copy = &hooked_copy;
    Reader reader(source_of(&hooked, worlds));
    reads(p, server, reader, worlds, KILN_WORLD_SERVER);
    if (client) {
        reads(p, *client, reader, worlds, KILN_WORLD_CLIENT);
        Backend b = reader.backend();
        KilnObjectRef r{};
        check(entity_open(b, kOwner, 1, KILN_WORLD_PREDICTION, 0, 1, &r) == KILN_ERR_UNAVAILABLE,
              "the prediction world has no engine view: unavailable");
    } else {
        Backend b = reader.backend();
        KilnObjectRef r{};
        check(entity_open(b, kOwner, 1, KILN_WORLD_CLIENT, 0, 1, &r) == KILN_ERR_UNAVAILABLE,
              "a dedicated server has no client world");
    }
    mutations(p, server, reader, worlds);
    concurrent(p, server, reader, false);
    concurrent(p, server, reader, true);
    frame_quiet(p, server, reader, worlds, e, client.get());
    for (int round = 1; round <= 3; ++round) frame_quiet_writer(p, server, reader, worlds, e, round);
    kiln::engine::mem::test_image = 0;
}

// The availability decision: the profile's fields against kiln.world's own resolution of the same fields.
void availability() {
    g_case = "availability";
    OwnResolution own;  // the values sigdb resolves on both exact builds (tools/sigport/sigdb_check.py)
    own.storage = 0xE179C0; own.map = 0x148; own.map_cap = 0x10; own.map_values = 0x40; own.map_bits = 0x8;
    own.map_keys = 0x28; own.entity_id = 0x10; own.entity_layout = 0x18; own.entity_chunk = 0x20; own.entity_row = 0x30;
    own.layout_offsets = 0x84; own.layout_strides = 0xA84;
    for (const auto& p : generated::kProfiles)
        check(kw::ecs::engine::disagreement(p.layout, own).empty(), std::string(p.id) + ": agrees with kiln.world's own resolution");
    const Layout& L = generated::kProfiles[0].layout;
    OwnResolution bad = own;
    bad.map_keys = 0x30;
    const std::string why = kw::ecs::engine::disagreement(L, bad);
    check(why.find("keys") != std::string::npos, "a differing field is named: " + why);
    bad = own;
    bad.layout_strides = -1;
    check(kw::ecs::engine::disagreement(L, bad).find("not resolve") != std::string::npos, "an unresolved field refuses");
    check(kw::ecs::engine::layout_refusal(nullptr) != nullptr, "no profile: refused");
    Layout no_index = L;
    no_index.entities.manager_occupancy = 0;
    check(kw::ecs::engine::layout_refusal(&no_index) != nullptr, "no entity id index: refused");
    Layout no_ecs = L;
    no_ecs.ecs = false;
    check(kw::ecs::engine::layout_refusal(&no_ecs) != nullptr, "no ECS read section: refused");

    // Without kiln.internal.engine (or without reads) the context still answers and entity reads are unavailable.
    Worlds worlds;
    worlds.world[KILN_WORLD_SERVER] = 0x1000;
    worlds.session[KILN_WORLD_SERVER] = 5;
    Source s;
    s.exact_profile = true;
    s.user = &worlds; s.world = &world_fact; s.epoch = &epoch_of; s.open_epoch = &open_epoch;
    Reader reader(s);
    Backend b = reader.backend();
    KilnEcsContextInfo ci{}; ci.size = sizeof ci;
    check(ecs_context_snapshot(b, &ci) == KILN_OK && ci.exact_profile == 1 && ci.present_mask == 1 && ci.sessions[0] == 5,
          "no engine table: the context answers");
    KilnObjectRef r{};
    check(entity_open(b, kOwner, 0, KILN_WORLD_SERVER, 0, 1, &r) == KILN_ERR_UNAVAILABLE, "no engine table: entity reads unavailable");
}

void epochs_bound() {
    g_case = "epochs";
    OwnerEpochs e;
    check(e.open(5, 0) == 0 && e.size() == 0, "epoch 0 is stored nowhere");
    check(e.open(5, 3) == 3 && e.open(5, 2) == 3 && e.open(5, 4) == 4 && e.current(5) == 4, "the newest epoch wins");
    for (KilnOwner o = 100; o < 100 + OwnerEpochs::kMaxOwners + 10; ++o) e.open(o, 1);
    check(e.size() == OwnerEpochs::kMaxOwners && e.current(5) == 0 && e.current(100 + OwnerEpochs::kMaxOwners + 9) == 1,
          "bounded: the oldest owners are forgotten first");
    e.retire(100 + OwnerEpochs::kMaxOwners + 9);
    check(e.current(100 + OwnerEpochs::kMaxOwners + 9) == 0, "a retired owner is dropped");
}

}  // namespace


int main() {
    g_caps.header = {sizeof g_caps, KILN_CAPS_VERSION};
    g_caps.report = &cap_report;
    for (const auto& p : generated::kProfiles) exact(p);
    availability();
    epochs_bound();
    std::printf("ecs_engine: %d checks, %d failures\n", checks, failures);
    return failures ? 1 : 0;
}
