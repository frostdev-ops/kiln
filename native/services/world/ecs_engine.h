// SPDX-License-Identifier: MIT
// The engine backend of kiln.world's verified-copy reads (ecs_reads.h): one world's entity table, component
// registration catalog and component rows, read from any thread through kiln.internal.engine's ECS view and guarded
// copy only, laid out by the exact engine profile's ecsReadLayout (kiln_engine_layout.h). Portable: no Windows and no
// KilnApi here, so host tests run this exact code over a fake engine (tests/ecs_engine_test.cpp) and ecs_prod.cpp
// wires it to the running game.
//
// Adapted from ShroudForge (MIT, Copyright (c) 2026 bonsaibauer):
//   modules/game-runtime/native/ecs/read_entity_membership.cpp:116-201 (the entity table's id lookup: the id's hash,
//     the bounded linear probe over the occupancy bits and keys, the record's id, generation, layout, storage, row
//     and definition);
//   modules/game-runtime/native/ecs/resolve_component_storage.cpp:4-38 (component presence from the layout's bits,
//     offset and stride from its tables, the stride equal to the registered size, the row's address);
//   modules/game-runtime/native/ecs/registry/read_component_registry.h:44-160 (the registration catalog: its owner's
//     records, count and size array, each row's name, hash and size);
//   modules/game-runtime/native/ecs/read_building_prop.cpp:4-19 (the template UUID through the definition pointer).
// Every engine byte goes through KilnEngineNativeApi::copy; a fault is a refusal (unreadable), never a crash.
#pragma once
#include <atomic>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>

#include "ecs_reads.h"
#include "kiln_engine_native.h"

namespace kw::ecs::engine {

// Kiln's own facts of one world kind, sampled by the source (ecs_prod.cpp: KilnApi.session and game_world_session).
struct WorldFact {
    uintptr_t world = 0;  // the live world object of the kind (KilnSessionInfo.worlds), 0 = none
    uint64_t session = 0; // game_world_session(kind)
};

struct Source {
    const KilnEngineNativeApi* engine = nullptr;  // kiln.internal.engine: copy and ecs_view (NULL: reads unavailable)
    const kiln::engine::Layout* layout = nullptr; // the exact profile's layout (engine->layout()), NULL without one
    bool exact_profile = false;                   // kiln.engine matched an exact profile (reported even without reads)
    bool reads = false;                           // entity reads are on (see layout_refusal and disagreement)
    void* user = nullptr;
    // KILN_ECS_OK with the kind's facts; UNREADABLE when the facts moved while they were read; UNAVAILABLE otherwise.
    uint32_t (*world)(void* user, uint32_t kind, WorldFact* out) = nullptr;
    uint64_t (*epoch)(void* user, KilnOwner owner) = nullptr;                      // the owner's current epoch
    uint64_t (*open_epoch)(void* user, KilnOwner owner, uint64_t epoch) = nullptr; // see Backend::open_epoch
    // The server step counter (KilnSessionInfo.steps): bumped once per server sim step, inside the GameServer frame.
    // false when unknown (frame-quiet reads are then unavailable).
    bool (*steps)(void* user, uint32_t kind, uint64_t* out) = nullptr;
};

// Why `layout` cannot serve entity reads, or nullptr when it can: the profile's read section with its entity id index
// (occupancy bits and keys) and every record and layout-table offset the reads use.
const char* layout_refusal(const kiln::engine::Layout* layout);

// kiln.world's own resolution of the same fields on the running exe (sigdb constants; -1 = not resolved). Independent
// of the profile: it comes from the code of the loaded image (docs/re/world.md, "Entities").
struct OwnResolution {
    int64_t storage = -1;        // world.storage: the entity manager inside the world object
    int64_t map = -1;            // world/storage.entities: the entity map inside the manager
    int64_t map_cap = -1, map_values = -1, map_bits = -1, map_keys = -1; // hash map fields, relative to the map
    int64_t entity_id = -1, entity_layout = -1, entity_chunk = -1, entity_row = -1; // the entity record
    int64_t layout_offsets = -1, layout_strides = -1;                               // the archetype layout
};
// "" when the profile's entity table agrees with kiln.world's own resolution field by field; else one sentence that
// names the first field that differs or that kiln.world could not resolve.
std::string disagreement(const kiln::engine::Layout& layout, const OwnResolution& own);

// The component registration catalog of one registry owner: hash -> (registration index, registered size).
struct Catalog;

class Reader {
public:
    explicit Reader(const Source& source) : src_(source), id_(next_id_.fetch_add(1) + 1) {}
    Reader(const Reader&) = delete;
    Reader& operator=(const Reader&) = delete;
    ~Reader();

    // The verified-copy logic's backend over this reader (ecs_reads.h). The reader must outlive its use.
    Backend backend();

    // The last reason a read was refused unavailable or unreadable (a static string, "" when none), for diagnostics.
    const char* last_refusal() const { return last_.load(std::memory_order_relaxed); }
    // The engine image base the views named (0 before the first view), for diagnostics.
    uintptr_t image() const { return image_.load(std::memory_order_relaxed); }

    // ---- the backend's ops (public for the op thunks; use backend()) ----
    uint32_t context(KilnOwner owner, uint32_t kind, ContextFact* out);
    uint32_t entity(const ContextFact& ctx, KilnEntity id, EntityFact* out);
    uint32_t component(const ContextFact& ctx, const EntityFact& entity, uint32_t hash, bool stamp, ComponentFact* out);
    uint32_t entity_ids(const ContextFact& ctx, std::vector<KilnEntity>* out);
    uint32_t position(const ContextFact& ctx, KilnEntity id, double out[3]);
    bool copy(uintptr_t address, void* out, size_t bytes) const;
    uint32_t quiet_begin(const ContextFact& ctx, uint64_t* token);
    uint32_t quiet_end(const ContextFact& ctx, uint64_t token);
    const Source& source() const { return src_; }

private:
    struct Table {
        uint64_t count = 0;
        uintptr_t slots = 0, bits = 0, keys = 0;
        bool operator==(const Table&) const = default;
    };
    uint32_t table(uintptr_t manager, Table& out) const;
    uint32_t catalog(uintptr_t registry, std::shared_ptr<const Catalog>& out);
    uint32_t refuse(uint32_t status, const char* why);

    Source src_;
    std::mutex mu_; // guards catalog_ only (held for a pointer copy, never across an engine read)
    std::shared_ptr<const Catalog> catalog_;
    const uint64_t id_;                // this reader's identity for threads' cached catalogs (never reused)
    static inline std::atomic<uint64_t> next_id_{0};
    std::atomic<bool> dropped_{false}; // a catalog row stopped matching: threads' cached catalogs are not trusted
    std::atomic<const char*> last_{""};
    std::atomic<uintptr_t> image_{0};
};

} // namespace kw::ecs::engine
