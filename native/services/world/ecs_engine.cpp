// SPDX-License-Identifier: MIT
// The engine backend of kiln.world's verified-copy reads; see ecs_engine.h.
// Adapted from ShroudForge (MIT, Copyright (c) 2026 bonsaibauer): modules/game-runtime/native/ecs/
// read_entity_membership.cpp:116-201, resolve_component_storage.cpp:4-38, registry/read_component_registry.h:44-160
// and read_building_prop.cpp:4-19 (details in ecs_engine.h). Where ShroudForge runs these reads on its own dispatcher,
// this file runs them on the caller's thread and leaves the before/after comparison to ecs_reads.cpp.
#include "ecs_engine.h"
#include "ecs_lookup.h"

#include <algorithm>
#include <bit>
#include <cstring>
#include <string_view>
#include <unordered_map>

#include "transform_layout.h"

namespace kw::ecs::engine {

using kiln::engine::EcsView;
using kiln::engine::Layout;
using kiln::engine::Reason;
using kiln::engine::Role;

struct Catalog {
    struct Row {
        uint16_t index = 0;
        uint16_t size = 0;
    };
    uintptr_t owner = 0, records = 0;
    uint64_t count = 0;
    std::unordered_map<uint32_t, Row> rows;  // component hash -> registration row
};

namespace {

// The registration catalog of one registry owner (ShroudForge's "layout v1", read_component_registry.h:44-160):
// records pointer +8, count +16, capacity +24; the u16 size array pointer +232 and its count +240; 256-byte rows with
// the name pointer +16 and its length (terminator included) +24, the FNV-1a hash of the name +32, the size in the low
// half of +56.
constexpr uintptr_t kRegRecords = 8, kRegSizes = 232;
constexpr size_t kRowBytes = 256, kRowPrefix = 64, kMaxComponents = 1024, kMaxCapacity = 4096;
constexpr size_t kRowName = 16, kRowNameLength = 24, kRowHash = 32, kRowSize = 56;

uint32_t fnv1a32(std::string_view s) {
    uint32_t h = 0x811c9dc5u;
    for (unsigned char c : s) h = (h ^ c) * 0x01000193u;
    return h;
}
uint64_t fnv1a64(const uint8_t* p, size_t n, uint64_t h = 0xcbf29ce484222325ull) {
    for (size_t i = 0; i < n; ++i) h = (h ^ p[i]) * 0x100000001b3ull;
    return h;
}
uint64_t mix(std::initializer_list<uint64_t> values) {
    uint64_t h = 0xcbf29ce484222325ull;
    for (uint64_t v : values) h = fnv1a64(reinterpret_cast<const uint8_t*>(&v), sizeof v, h);
    return h ? h : 1;
}
// A view refusal that a later read may not see again (a word was busy, something moved during the resolution).
bool transient(uint32_t reason) {
    switch (static_cast<Reason>(reason)) {
        case Reason::EcsThreadRegistryBusy:
        case Reason::EcsEngineOwnerChangedDuringRead:
        case Reason::EcsServerThreadChangedDuringRead:
        case Reason::EcsActiveSceneListChanged:
        case Reason::EcsReadySceneContextChanged:
            return true;
        default:
            return false;
    }
}

Reader& R(void* u) { return *static_cast<Reader*>(u); }
uint32_t op_context(void* u, KilnOwner o, uint32_t k, ContextFact* f) { return R(u).context(o, k, f); }
uint32_t op_entity(void* u, const ContextFact& c, KilnEntity id, EntityFact* f) { return R(u).entity(c, id, f); }
uint32_t op_component(void* u, const ContextFact& c, const EntityFact& e, KilnEntity, uint32_t h, ComponentFact* f) {
    return R(u).component(c, e, h, true, f);
}
uint32_t op_has(void* u, const ContextFact& c, const EntityFact& e, KilnEntity, uint32_t h, bool* present) {
    ComponentFact f{};
    const uint32_t s = R(u).component(c, e, h, false, &f);
    *present = f.present;
    return s;
}
bool op_copy(void* u, uintptr_t a, void* out, size_t n) { return R(u).copy(a, out, n); }
uint32_t op_ids(void* u, const ContextFact& c, std::vector<KilnEntity>* out) { return R(u).entity_ids(c, out); }
uint32_t op_position(void* u, const ContextFact& c, KilnEntity id, double out[3]) { return R(u).position(c, id, out); }
uint32_t op_quiet_begin(void* u, const ContextFact& c, uint64_t* token) { return R(u).quiet_begin(c, token); }
uint32_t op_quiet_end(void* u, const ContextFact& c, uint64_t token) { return R(u).quiet_end(c, token); }
uint64_t op_open_epoch(void* u, KilnOwner o, uint64_t e) {
    const Source& s = R(u).source();
    return s.open_epoch ? s.open_epoch(s.user, o, e) : e;
}

}  // namespace

const char* layout_refusal(const Layout* layout) {
    if (!layout) return "no exact engine profile for this build";
    if (!layout->ecs) return "the exact engine profile has no ECS read layout";
    const auto& e = layout->entities;
    if (!e.manager_occupancy || !e.manager_keys)
        return "the exact engine profile has no entity id index (occupancy bits and keys)";
    if (!e.manager_count || !e.manager_table || !e.maximum_entities || !e.entity_layout || !e.entity_storage ||
        !e.entity_row || e.component_offsets == e.component_strides || e.entity_id == e.entity_generation)
        return "the exact engine profile's entity record or layout table offsets are incomplete";
    return nullptr;
}

std::string disagreement(const Layout& layout, const OwnResolution& own) {
    const auto& e = layout.entities;
    struct Pair { const char* what; int64_t mine; uint64_t profile; };
    const auto rel = [&](int64_t field) { return own.map < 0 || field < 0 ? int64_t{-1} : own.map + field; };
    const Pair pairs[] = {
        {"the entity manager in the world (world.storage)", own.storage, layout.entity_manager_offset},
        {"the entity map's capacity (world/storage.entities.cap)", rel(own.map_cap), e.manager_count},
        {"the entity map's record slots (world/storage.entities.values)", rel(own.map_values), e.manager_table},
        {"the entity map's occupancy bits (hash map bits)", rel(own.map_bits), e.manager_occupancy},
        {"the entity map's keys (hash map keys)", rel(own.map_keys), e.manager_keys},
        {"the entity record's id (spawn/record.id)", own.entity_id, e.entity_id},
        {"the entity record's layout (world/entity.layout)", own.entity_layout, e.entity_layout},
        {"the entity record's chunk (world/entity.chunk)", own.entity_chunk, e.entity_storage},
        {"the entity record's row (world/entity.index)", own.entity_row, e.entity_row},
        {"the layout's component offsets (world/layout.offsets)", own.layout_offsets, e.component_offsets},
        {"the layout's component strides (world/layout.strides)", own.layout_strides, e.component_strides},
    };
    for (const Pair& p : pairs) {
        if (p.mine < 0) return std::string("kiln.world could not resolve ") + p.what + " on this build to confirm the profile";
        if (static_cast<uint64_t>(p.mine) != p.profile)
            return std::string("the engine profile and kiln.world disagree on ") + p.what + " (profile " +
                   std::to_string(p.profile) + ", kiln.world " + std::to_string(p.mine) + ")";
    }
    // The layout's component bits are read at +0 by the same code (GetComponentByType); the profile says so too.
    if (e.component_bits != 0)
        return "the engine profile puts the layout's component bits at " + std::to_string(e.component_bits) +
               "; kiln.world's component lookup reads them at 0";
    return {};
}

Reader::~Reader() = default;

Backend Reader::backend() {
    Backend b{this, &op_context, &op_entity, &op_component, &op_copy, &op_ids, &op_position};
    b.has = &op_has;
    b.open_epoch = &op_open_epoch;
    b.quiet_begin = &op_quiet_begin;
    b.quiet_end = &op_quiet_end;
    return b;
}

uint32_t Reader::context(KilnOwner owner, uint32_t kind, ContextFact* out) {
    out->exact_profile = src_.exact_profile;
    out->epoch_tracked = owner != 0;
    out->reload_epoch = owner && src_.epoch ? src_.epoch(src_.user, owner) : 0;
    out->engine_status = KILN_ECS_UNAVAILABLE;
    if (kind >= KILN_WORLD_KINDS || !src_.world) return KILN_ECS_UNAVAILABLE;
    WorldFact before{};
    if (const uint32_t s = src_.world(src_.user, kind, &before); s != KILN_ECS_OK) return s;
    out->present = before.world != 0;
    out->session = before.session;
    if (!out->present) return KILN_ECS_OK;  // no world is an answer; entity reads need one
    if (!src_.reads || !src_.layout || !src_.engine || !src_.engine->ecs_view) {
        refuse(0, "entity reads are off on this build (see the world.ecs_read capability row)");
        return KILN_ECS_OK;
    }
    // The engine's own view of this world kind: the local authority (a dedicated server, or a client while it hosts)
    // for the server world, the client's main world for the client world. The prediction world is a storage that no
    // view names.
    if (kind == KILN_WORLD_PREDICTION) {
        refuse(0, "the prediction world has no engine view");
        return KILN_ECS_OK;
    }
    EcsView view{};
    uint32_t reason = 0;
    if (src_.engine->ecs_view(kind == KILN_WORLD_SERVER ? 0u : 1u, &view, &reason) != KILN_OK) {
        out->engine_status = transient(reason) ? KILN_ECS_UNREADABLE : KILN_ECS_UNAVAILABLE;
        refuse(0, kiln::engine::reason_name(static_cast<Reason>(reason)));
        return KILN_ECS_OK;
    }
    image_.store(view.owners.image, std::memory_order_relaxed);
    if (kind == KILN_WORLD_SERVER && view.role != Role::LocalAuthority) {
        refuse(0, "the engine view of the server world is not the local authority");
        return KILN_ECS_OK;
    }
    // The view must name the very world Kiln sees for this kind (the world object whose entity manager the profile's
    // offsets point into): a profile that resolves to another object reads nothing.
    if (view.execution_owner != before.world) {
        refuse(0, "the engine view names another world than the one Kiln sees for this kind");
        return KILN_ECS_OK;
    }
    uint64_t head[2] = {};  // the registry's records pointer and count: the catalog's identity
    Table table{};
    if (!copy(view.registry_owner + kRegRecords, head, sizeof head) ||
        this->table(view.manager, table) != KILN_ECS_OK) {
        out->engine_status = KILN_ECS_UNREADABLE;
        refuse(0, "the world's entity table or component registry is unreadable");
        return KILN_ECS_OK;
    }
    WorldFact after{};
    if (src_.world(src_.user, kind, &after) != KILN_ECS_OK || after.world != before.world ||
        after.session != before.session)
        return KILN_ECS_UNREADABLE;  // the world moved while it was read: no context of either instant
    out->layout_stamp = mix({view.registry_owner, head[0], head[1]});
    out->revision = mix({table.count, table.slots, table.bits, table.keys});
    out->manager = view.manager;
    out->registry = view.registry_owner;
    // The local authority's Scene word (the gate's: it checks that this view's scene owner is the registered
    // GameServer Scene before it takes the word), for frame-quiet reads of the server world.
    uintptr_t word = 0;
    if (kind == KILN_WORLD_SERVER && view.scene_owner && add(view.scene_owner, src_.layout->write.scene_lock, word))
        out->scene_word = word;
    out->engine_status = KILN_ECS_OK;
    return KILN_ECS_OK;
}

uint32_t Reader::entity(const ContextFact& ctx, KilnEntity id, EntityFact* out) { return find(ctx, id, kMaxProbe, out); }

uint32_t Reader::catalog(uintptr_t registry, std::shared_ptr<const Catalog>& out) {
    uint64_t head[2] = {};  // records, count
    if (!copy(registry + kRegRecords, head, sizeof head)) return KILN_ECS_UNREADABLE;
    // This thread's last catalog first: no lock on the way of a read (a frame-quiet window takes none).
    thread_local uint64_t t_reader = 0;
    thread_local std::shared_ptr<const Catalog> t_catalog;
    if (t_reader == id_ && t_catalog && t_catalog->owner == registry && t_catalog->records == head[0] &&
        t_catalog->count == head[1] && !dropped_.load(std::memory_order_acquire)) {
        out = t_catalog;
        return KILN_ECS_OK;
    }
    {
        std::lock_guard lock(mu_);
        if (catalog_ && catalog_->owner == registry && catalog_->records == head[0] && catalog_->count == head[1]) {
            out = catalog_;
            t_reader = id_;
            t_catalog = catalog_;
            dropped_.store(false, std::memory_order_release);
            return KILN_ECS_OK;
        }
    }
    // Build outside the lock (two threads may both build; the last one publishes).
    const uintptr_t image = image_.load(std::memory_order_relaxed);
    const uint64_t image_size = src_.layout->image_size;
    uint64_t capacity = 0, sized[2] = {};  // the size array's pointer and count
    if (!copy(registry + kRegRecords + 16, &capacity, sizeof capacity) || !copy(registry + kRegSizes, sized, sizeof sized))
        return KILN_ECS_UNREADABLE;
    const uint64_t count = head[1];
    const char* bad = "the component registration catalog does not have the layout Kiln reads";
    if (!head[0] || !count || count > kMaxComponents || capacity < count || capacity > kMaxCapacity || !sized[0] ||
        sized[1] != count || !image)
        return refuse(KILN_ECS_UNAVAILABLE, bad);
    std::vector<uint8_t> rows(count * kRowBytes), again(count * kRowBytes);
    std::vector<uint16_t> sizes(count);
    if (!copy(head[0], rows.data(), rows.size()) || !copy(sized[0], sizes.data(), sizes.size() * 2))
        return KILN_ECS_UNREADABLE;
    auto cat = std::make_shared<Catalog>();
    cat->owner = registry;
    cat->records = head[0];
    cat->count = count;
    std::string name;
    for (uint64_t i = 0; i < count; ++i) {
        const uint8_t* r = rows.data() + i * kRowBytes;
        uint64_t text = 0, length = 0;
        uint32_t hash = 0, size_word = 0;
        std::memcpy(&text, r + kRowName, 8);
        std::memcpy(&length, r + kRowNameLength, 8);
        std::memcpy(&hash, r + kRowHash, 4);
        std::memcpy(&size_word, r + kRowSize, 4);
        // The name lies in the image (ShroudForge's in_image), and its hash is the one the row and every caller use.
        if (!length || length > 512 || text < image || length > image_size || text - image > image_size - length)
            return refuse(KILN_ECS_UNAVAILABLE, bad);
        name.assign(static_cast<size_t>(length), '\0');
        if (!copy(text, name.data(), name.size())) return KILN_ECS_UNREADABLE;
        if (name.back() == '\0') name.pop_back();
        const uint16_t size = static_cast<uint16_t>(size_word & 0xffffu);
        if (name.empty() || name.find('\0') != std::string::npos || name.rfind("keen::ecs::", 0) != 0 ||
            fnv1a32(name) != hash || sizes[i] != size || !cat->rows.emplace(hash, Catalog::Row{static_cast<uint16_t>(i), size}).second)
            return refuse(KILN_ECS_UNAVAILABLE, bad);
    }
    // The same catalog after the walk: its identity, and each row's prefix (name, hash, size).
    uint64_t head2[2] = {};
    if (!copy(registry + kRegRecords, head2, sizeof head2) || head2[0] != head[0] || head2[1] != head[1] ||
        !copy(head[0], again.data(), again.size()))
        return KILN_ECS_UNREADABLE;
    for (uint64_t i = 0; i < count; ++i)
        if (std::memcmp(rows.data() + i * kRowBytes, again.data() + i * kRowBytes, kRowPrefix)) return KILN_ECS_UNREADABLE;
    std::shared_ptr<const Catalog> published = std::move(cat);
    {
        std::lock_guard lock(mu_);
        catalog_ = published;
    }
    dropped_.store(false, std::memory_order_release);
    t_reader = id_;
    t_catalog = published;
    out = std::move(published);
    return KILN_ECS_OK;
}

uint32_t Reader::registration(const ContextFact& ctx, uint32_t hash, bool* present, uint16_t* index, uint16_t* size) {
    *present = false;
    if (ctx.engine_status != KILN_ECS_OK) return ctx.engine_status;
    std::shared_ptr<const Catalog> cat;
    if (const uint32_t s = catalog(ctx.registry, cat); s != KILN_ECS_OK) return s;
    const auto it = cat->rows.find(hash);
    if (it == cat->rows.end()) return KILN_ECS_OK;
    *present = true;
    *index = it->second.index;
    *size = it->second.size;
    return KILN_ECS_OK;
}

uint32_t Reader::component(const ContextFact& ctx, const EntityFact& entity, uint32_t hash, bool stamp,
                           ComponentFact* out) {
    *out = ComponentFact{};
    if (ctx.engine_status != KILN_ECS_OK) return ctx.engine_status;
    if (!entity.alive || !entity.record) return KILN_ECS_ABSENT;
    const auto& e = src_.layout->entities;
    std::shared_ptr<const Catalog> cat;
    if (const uint32_t s = catalog(ctx.registry, cat); s != KILN_ECS_OK) return s;
    const auto it = cat->rows.find(hash);
    if (it == cat->rows.end()) return KILN_ECS_OK;  // no component is registered under this hash: absent
    const Catalog::Row row = it->second;
    // The cached row still describes the live catalog: its identity and this row's hash and size.
    uint64_t head[2] = {};
    uint8_t prefix[kRowPrefix];
    uint32_t live_hash = 0, live_size = 0;
    if (!copy(ctx.registry + kRegRecords, head, sizeof head) || !copy(cat->records + row.index * kRowBytes, prefix, sizeof prefix))
        return KILN_ECS_UNREADABLE;
    std::memcpy(&live_hash, prefix + kRowHash, 4);
    std::memcpy(&live_size, prefix + kRowSize, 4);
    if (head[0] != cat->records || head[1] != cat->count || live_hash != hash || (live_size & 0xffffu) != row.size) {
        {
            std::lock_guard lock(mu_);
            if (catalog_ == cat) catalog_.reset();
        }
        dropped_.store(true, std::memory_order_release);  // every thread's cached copy is rebuilt or re-fetched
        return refuse(KILN_ECS_UNREADABLE, "the component registration catalog changed during the read");
    }
    // Presence from the archetype layout's bits; offset and stride from its tables (GetComponentByType).
    const uintptr_t layout = static_cast<uintptr_t>(entity.layout_stamp);
    uint64_t word = 0;
    if (!copy(layout + e.component_bits + (row.index / 64) * 8, &word, sizeof word)) return KILN_ECS_UNREADABLE;
    if (!((word >> (row.index % 64)) & 1)) return KILN_ECS_OK;  // the entity's archetype lacks it: absent
    uint16_t offset = 0, stride = 0;
    if (!copy(layout + e.component_offsets + row.index * 2, &offset, sizeof offset) ||
        !copy(layout + e.component_strides + row.index * 2, &stride, sizeof stride))
        return KILN_ECS_UNREADABLE;
    if (stride != row.size)
        return refuse(KILN_ECS_UNAVAILABLE, "a component's stride in its layout is not its registered size");
    const uint64_t delta = offset + static_cast<uint64_t>(entity.row) * stride;
    if (entity.storage > UINTPTR_MAX - delta || entity.storage + delta > UINTPTR_MAX - stride) return KILN_ECS_UNREADABLE;
    out->present = true;
    out->address = entity.storage + static_cast<uintptr_t>(delta);
    out->size = stride;
    if (stamp && stride) {
        // The engine keeps no version per component: the stamp is the bytes' own hash, so stamp, copy, stamp is a
        // series of equal reads of the same bytes.
        uint8_t chunk[256];
        uint64_t h = 0xcbf29ce484222325ull;
        for (size_t done = 0; done < stride;) {
            const size_t n = std::min<size_t>(sizeof chunk, stride - done);
            if (!copy(out->address + done, chunk, n)) return KILN_ECS_UNREADABLE;
            h = fnv1a64(chunk, n, h);
            done += n;
        }
        out->content_stamp = h;
    }
    return KILN_ECS_OK;
}

uint32_t Reader::entity_ids(const ContextFact& ctx, std::vector<KilnEntity>* out) {
    if (ctx.engine_status != KILN_ECS_OK) return ctx.engine_status;
    Table t{};
    if (const uint32_t s = table(ctx.manager, t); s != KILN_ECS_OK) return s;
    std::vector<uint64_t> bits((t.count + 63) / 64);
    std::vector<uint32_t> keys(t.count);
    std::vector<uintptr_t> slots(t.count);
    if (!copy(t.bits, bits.data(), bits.size() * 8) || !copy(t.keys, keys.data(), keys.size() * 4) ||
        !copy(t.slots, slots.data(), slots.size() * sizeof(uintptr_t)))
        return KILN_ECS_UNREADABLE;
    Table t2{};
    if (table(ctx.manager, t2) != KILN_ECS_OK || !(t2 == t)) return KILN_ECS_UNREADABLE;
    out->clear();
    for (size_t w = 0; w < bits.size(); ++w)
        for (uint64_t b = bits[w]; b; b &= b - 1) {
            const size_t i = w * 64 + static_cast<size_t>(std::countr_zero(b));
            if (i < t.count && keys[i] && slots[i]) out->push_back(keys[i]);
        }
    return KILN_ECS_OK;
}

uint32_t Reader::position(const ContextFact& ctx, KilnEntity id, double out[3]) {
    EntityFact fact{};
    if (const uint32_t s = entity(ctx, id, &fact); s != KILN_ECS_OK) return s;
    ComponentFact cf{};
    if (const uint32_t s = component(ctx, fact, kCurrentTransformHash, false, &cf); s != KILN_ECS_OK) return s;
    if (!cf.present) return KILN_ECS_ABSENT;
    if (cf.size < kTransformMinSize) return refuse(KILN_ECS_UNAVAILABLE, "CurrentTransform is smaller than a transform");
    uint8_t bytes[64] = {};
    if (!copy(cf.address, bytes, std::min<size_t>(cf.size, sizeof bytes))) return KILN_ECS_UNREADABLE;
    KilnTransform t{};
    if (!transform_decode(bytes, cf.size, &t)) return KILN_ECS_UNAVAILABLE;
    out[0] = t.position.x;
    out[1] = t.position.y;
    out[2] = t.position.z;
    return KILN_ECS_OK;
}

// The frame-quiet window: the server step, then the Scene word (the word the GameServer frame holds Shared for its
// whole update and the write gate takes exclusively) free; at the end the word free again and the step unmoved. A frame
// that overlaps the copies either holds the word at one of the two looks or runs entirely between them, and then Kiln's
// Pre of its step moved the counter. Read-only: two guarded word reads and two counter reads, no CAS, no gate.
uint32_t Reader::quiet_begin(const ContextFact& ctx, uint64_t* token) {
    if (ctx.world_kind != KILN_WORLD_SERVER) return KILN_ECS_UNSUPPORTED;
    if (ctx.engine_status != KILN_ECS_OK) return ctx.engine_status;
    uint64_t step = 0;
    if (!ctx.scene_word || !src_.steps || !src_.steps(src_.user, KILN_WORLD_SERVER, &step))
        return refuse(KILN_ECS_UNAVAILABLE, "frame-quiet reads need the server's Scene word and step counter");
    int32_t word = 0;
    if (!copy(ctx.scene_word, &word, sizeof word)) return KILN_ECS_UNREADABLE;
    if (word != 0) return KILN_ECS_BUSY;
    *token = step;
    return KILN_ECS_OK;
}

uint32_t Reader::quiet_end(const ContextFact& ctx, uint64_t token) {
    int32_t word = 0;
    uint64_t step = 0;
    if (!ctx.scene_word || !copy(ctx.scene_word, &word, sizeof word)) return KILN_ECS_UNREADABLE;
    if (word != 0) return KILN_ECS_BUSY;
    if (!src_.steps || !src_.steps(src_.user, KILN_WORLD_SERVER, &step)) return KILN_ECS_UNAVAILABLE;
    return step == token ? KILN_ECS_OK : KILN_ECS_BUSY;
}

}  // namespace kw::ecs::engine
