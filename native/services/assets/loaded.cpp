// SPDX-License-Identifier: MIT
// kiln.assets' loaded-resource reads (loaded.h). Adapted from ShroudForge (MIT, Copyright (c) 2026 bonsaibauer),
// modules/game-runtime/native/loaded-resources/{resolve_loaded_resource,read_loaded_resource,
// find_loaded_resources_by_type}.cpp; the checks keep its order and its refusal names (resolve_view's fail texts).
#include "loaded.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstddef>
#include <cstring>
#include <set>
#include <type_traits>

namespace ka::loaded {
namespace {

using kiln::engine::EcsView;
using kiln::engine::ResourceLayout;

bool add(std::uintptr_t base, std::size_t offset, std::uintptr_t& result) {
    if (!base || offset > UINTPTR_MAX - base) return false;
    result = base + offset;
    return true;
}

bool copy_memory(const Source& s, std::uintptr_t address, void* out, std::size_t size) {
    if (!size) return true;
    if (!address || size > UINTPTR_MAX - address) return false;
    return s.engine->copy(address, out, size) == KILN_OK;
}

template <typename T>
bool read_value(const Source& s, std::uintptr_t address, T& value) {
    static_assert(std::is_trivially_copyable_v<T>);
    return copy_memory(s, address, &value, sizeof value);
}

// A rel32 field: 0 is null; otherwise the field's own address plus the signed distance.
bool relative_pointer(const Source& s, std::uintptr_t field, std::uintptr_t& pointer) {
    std::int32_t relative{};
    if (!read_value(s, field, relative)) return false;
    if (!relative) {
        pointer = 0;
        return true;
    }
    if (relative < 0) {
        const auto magnitude = static_cast<std::uint64_t>(-static_cast<std::int64_t>(relative));
        if (magnitude > field) return false;
        pointer = field - static_cast<std::uintptr_t>(magnitude);
        return true;
    }
    return add(field, static_cast<std::size_t>(relative), pointer);
}

bool span_from_entry(const Source& s, const View& view, std::size_t index, const unsigned char* entry, Span& span) {
    const ResourceLayout& l = s.layout->resource;
    if (index >= view.value_count || !entry) return false;
    std::uint32_t offset{}, packed{};
    std::memcpy(&offset, entry + l.resource_offset_offset, sizeof offset);
    std::memcpy(&packed, entry + l.packed_size_offset, sizeof packed);
    const auto chunk_id = entry[l.chunk_index_offset];
    const auto size = static_cast<std::size_t>(packed & static_cast<std::uint32_t>(l.size_mask));
    if (size > l.maximum_resource_bytes || chunk_id >= view.chunk_count) return false;
    std::uintptr_t chunk{};
    if (!add(view.chunks, static_cast<std::size_t>(chunk_id) * l.chunk_stride, chunk)) return false;
    std::uintptr_t base_field{}, size_field{}, base{};
    std::uint64_t chunk_size{};
    if (!add(chunk, l.chunk_base_pointer_offset, base_field) || !add(chunk, l.chunk_size_offset, size_field) ||
        !read_value(s, base_field, base) || !read_value(s, size_field, chunk_size) || !base || offset > chunk_size ||
        size > chunk_size - offset)
        return false;
    std::uintptr_t data{};
    if (!add(base, offset, data) || size > UINTPTR_MAX - data) return false;
    span = {data, size, static_cast<std::uint32_t>(index + 1), chunk_id};
    return true;
}

// The entry layout the lookups read (8-byte value entries, keys of at least 24 bytes): what ShroudForge's reader assumes.
bool layout_usable(const ResourceLayout& l) {
    return l.value_stride >= 8 && l.key_stride >= 24 && l.resource_offset_offset + 4 <= 8 && l.packed_size_offset + 4 <= 8 &&
           l.chunk_index_offset < 8 && l.bucket_stride >= 8 && l.chunk_stride &&
           l.chunk_base_pointer_offset + sizeof(std::uintptr_t) <= l.chunk_stride &&
           l.chunk_size_offset + sizeof(std::uint64_t) <= l.chunk_stride && l.maximum_entries && l.maximum_chunks;
}

void reset(KilnLoadedInfo* info) {
    info->status = KILN_LOADED_OK;
    info->flags = 0;
    info->handle = 0;
    info->bytes = 0;
}

KilnResult refuse(KilnLoadedInfo* info, std::uint32_t status) {
    info->status = status;
    return status == KILN_LOADED_MISSING ? KILN_ERR_NOT_FOUND : KILN_ERR_UNAVAILABLE;
}

using Identity = std::array<unsigned char, 24>;

Identity identity_of(const KilnAssetKey& key) {
    Identity id{};
    std::memcpy(id.data(), key.guid, 16);
    std::memcpy(id.data() + 16, &key.type, 4);
    std::memcpy(id.data() + 20, &key.part, 4);
    return id;
}

bool snapshot_arrays(const Source& s, const View& view, std::vector<unsigned char>& keys, std::vector<unsigned char>& values,
                     std::vector<unsigned char>& chunks) {
    const ResourceLayout& l = s.layout->resource;
    keys.resize(static_cast<std::size_t>(view.key_count) * l.key_stride);
    values.resize(static_cast<std::size_t>(view.value_count) * l.value_stride);
    chunks.resize(static_cast<std::size_t>(view.chunk_count) * l.chunk_stride);
    return copy_memory(s, view.keys, keys.data(), keys.size()) && copy_memory(s, view.values, values.data(), values.size()) &&
           copy_memory(s, view.chunks, chunks.data(), chunks.size());
}

// list_type (find_loaded_resources_by_type.cpp:20-82): false when the index cannot be copied, holds a key twice, or
// changed while it was walked.
bool list_type(const Source& s, const View& view, std::uint32_t type, std::vector<KilnAssetKey>& result) {
    const ResourceLayout& l = s.layout->resource;
    std::vector<unsigned char> keys, values, chunks;
    if (!snapshot_arrays(s, view, keys, values, chunks)) return false;
    std::set<Identity> identities;
    result.clear();
    for (std::size_t index = 0; index < view.key_count; ++index) {
        const unsigned char* key = keys.data() + index * l.key_stride;
        std::uint32_t key_type{};
        std::memcpy(&key_type, key + 16, sizeof key_type);
        if (key_type != type) continue;
        Identity id{};
        std::memcpy(id.data(), key, id.size());
        if (!identities.insert(id).second) return false;
        const unsigned char* entry = values.data() + index * l.value_stride;
        std::uint32_t offset{}, packed{};
        std::memcpy(&offset, entry + l.resource_offset_offset, sizeof offset);
        std::memcpy(&packed, entry + l.packed_size_offset, sizeof packed);
        const auto chunk_id = entry[l.chunk_index_offset];
        const auto size = static_cast<std::size_t>(packed & static_cast<std::uint32_t>(l.size_mask));
        if (size > l.maximum_resource_bytes || chunk_id >= view.chunk_count) continue;
        const unsigned char* chunk = chunks.data() + static_cast<std::size_t>(chunk_id) * l.chunk_stride;
        std::uintptr_t base{};
        std::uint64_t chunk_size{};
        std::memcpy(&base, chunk + l.chunk_base_pointer_offset, sizeof base);
        std::memcpy(&chunk_size, chunk + l.chunk_size_offset, sizeof chunk_size);
        if (!base || offset > chunk_size || size > chunk_size - offset) continue;
        std::uintptr_t data{};
        if (!add(base, offset, data) || size > UINTPTR_MAX - data) continue;
        if (s.readable && !s.readable(s.user, data, size)) continue;
        KilnAssetKey out{};
        std::memcpy(out.guid, key, 16);
        out.type = key_type;
        std::memcpy(&out.part, key + 20, sizeof out.part);
        result.push_back(out);
    }
    View after{};
    std::vector<unsigned char> keys_after, values_after, chunks_after;
    return resolve_view(s, after) && after == view && snapshot_arrays(s, after, keys_after, values_after, chunks_after) &&
           keys == keys_after && values == values_after && chunks == chunks_after;
}

}  // namespace

std::uint32_t canonical_resource_hash(const KilnAssetKey& key) {
    std::uint32_t hash{};
    std::memcpy(&hash, key.guid, sizeof hash);
    const std::uint32_t fields[]{key.type, key.part};
    for (const auto field : fields)
        for (unsigned shift = 0; shift < 32; shift += 8) {
            hash ^= (field >> shift) & 0xff;
            hash *= 0x01000193u;
        }
    return hash;
}

bool resolve_view(const Source& s, View& view, const char** reason) {
    view = {};
    if (!s.engine || !s.layout || !s.engine->copy || !s.engine->ecs_view) {
        if (reason) *reason = "no-exact-resource-read-profile";
        return false;
    }
    EcsView ecs{};
    std::uint32_t why = 0;
    if (s.engine->ecs_view(0, &ecs, &why) != KILN_OK) {
        if (reason) *reason = "resource-owner-view-unavailable";
        return false;
    }
    return resolve_view_with(s, ecs.owners, view, reason);
}

bool resolve_view_with(const Source& s, const kiln::engine::Owners& owners, View& view, const char** reason) {
    const auto fail = [reason](const char* text) {
        if (reason) *reason = text;
        return false;
    };
    view = {};
    if (!s.engine || !s.layout || !s.engine->copy) return fail("no-exact-resource-read-profile");
    const ResourceLayout& l = s.layout->resource;
    if (!layout_usable(l)) return fail("resource-layout-unsupported");
    if ((owners.dedicated_server != 0) != s.layout->dedicated_server) return fail("resource-owner-target-mismatch");
    view.image = owners.image;
    view.root = owners.root;
    view.client = owners.client;
    view.controller = owners.controller;
    view.main_owner = owners.main_owner;
    // MainOwner and the context are allocator-backed data objects, not proven polymorphic classes.
    std::uintptr_t field{};
    const std::uintptr_t resource_owner = s.layout->dedicated_server ? owners.world_context : view.main_owner;
    if (!add(resource_owner, l.main_active_context_offset, field) || !read_value(s, field, view.context))
        return fail("active-context-unavailable");
    if (!add(view.context, l.context_item_registry_offset, field) || !read_value(s, field, view.registry) || !view.registry)
        return fail("item-registry-pointer-unavailable");
    std::uint32_t registry_handle{};
    std::uintptr_t resource_field{};
    if (!add(view.registry, l.item_registry_service_offset, field) || !read_value(s, field, view.service) ||
        !add(view.registry, l.item_registry_handle_offset, field) || !read_value(s, field, registry_handle) ||
        !registry_handle || !add(view.registry, l.item_registry_resource_pointer_offset, resource_field))
        return fail("item-registry-service-unavailable");
    if (!read_value(s, resource_field, view.registry_resource) || !view.registry_resource)
        return fail("item-registry-resource-unavailable");
    // The original constructor installs these two callbacks in the service: validate it by that contract, never call one.
    std::uintptr_t slot{}, callback{}, wanted{};
    if (!add(view.service, l.resource_service_id_callback_offset, slot) || !read_value(s, slot, callback) ||
        !add(view.image, l.resource_service_id_callback_rva, wanted) || callback != wanted ||
        !add(view.service, l.resource_service_handle_callback_offset, slot) || !read_value(s, slot, callback) ||
        !add(view.image, l.resource_service_handle_callback_rva, wanted) || callback != wanted)
        return fail("resource-service-callback-mismatch");
    if (!add(view.service, l.resource_service_reader_offset, field) || !read_value(s, field, view.reader))
        return fail("resource-reader-unavailable");
    if (!add(view.reader, l.reader_metadata_offset, field) || !read_value(s, field, view.metadata) ||
        !add(view.metadata, l.metadata_map_offset, view.map))
        return fail("resource-index-metadata-unavailable");
    if (l.map_header_size > UINTPTR_MAX - view.map) return fail("resource-index-header-range-overflow");
    std::uintptr_t count_field{};
    if (!add(view.map, l.bucket_rel32_offset, field) || !relative_pointer(s, field, view.buckets) ||
        !add(view.map, l.keys_rel32_offset, field) || !relative_pointer(s, field, view.keys) ||
        !add(view.map, l.values_rel32_offset, field) || !relative_pointer(s, field, view.values) ||
        !add(view.map, l.bucket_count_offset, count_field) || !read_value(s, count_field, view.bucket_count) ||
        !add(view.map, l.key_count_offset, count_field) || !read_value(s, count_field, view.key_count) ||
        !add(view.map, l.value_count_offset, count_field) || !read_value(s, count_field, view.value_count))
        return fail("resource-index-header-invalid");
    if (!view.buckets || !view.keys || !view.values || !view.bucket_count || (view.bucket_count & (view.bucket_count - 1)) ||
        view.bucket_count > l.maximum_entries || !view.key_count || view.key_count != view.value_count ||
        view.key_count > l.maximum_entries)
        return fail("resource-index-counts-or-relative-pointers-invalid");
    view.registry_handle = registry_handle;
    const auto bucket_bytes = static_cast<std::size_t>(view.bucket_count) * l.bucket_stride;
    const auto key_bytes = static_cast<std::size_t>(view.key_count) * l.key_stride;
    const auto value_bytes = static_cast<std::size_t>(view.value_count) * l.value_stride;
    if (bucket_bytes > UINTPTR_MAX - view.buckets || key_bytes > UINTPTR_MAX - view.keys ||
        value_bytes > UINTPTR_MAX - view.values)
        return fail("resource-index-arrays-range-overflow");
    std::uintptr_t chunk_count_field{};
    if (!add(view.service, l.resource_service_chunk_table_offset, field) || !read_value(s, field, view.chunks) ||
        !add(view.service, l.resource_service_chunk_count_offset, chunk_count_field) ||
        !read_value(s, chunk_count_field, view.chunk_count) || !view.chunks || !view.chunk_count ||
        view.chunk_count > l.maximum_chunks ||
        static_cast<std::size_t>(view.chunk_count) * l.chunk_stride > UINTPTR_MAX - view.chunks)
        return fail("resource-data-chunks-unavailable");
    return true;
}

bool registry_is_bound(const Source& s, const View& view) {
    const ResourceLayout& l = s.layout->resource;
    if (!view.registry_handle || view.registry_handle > view.value_count) return false;
    std::array<unsigned char, 8> entry{};
    std::uintptr_t address{};
    const auto index = static_cast<std::size_t>(view.registry_handle - 1);
    if (!add(view.values, index * l.value_stride, address) || !copy_memory(s, address, entry.data(), entry.size()))
        return false;
    Span span{};
    return span_from_entry(s, view, index, entry.data(), span) && span.handle == view.registry_handle &&
           span.data == view.registry_resource;
}

bool locate(const Source& s, const View& view, const KilnAssetKey& key, Span& span) {
    const ResourceLayout& l = s.layout->resource;
    if (std::all_of(std::begin(key.guid), std::end(key.guid), [](auto b) { return b == 0; })) return false;
    const auto bucket_index = static_cast<std::size_t>(canonical_resource_hash(key)) & (view.bucket_count - 1);
    std::uintptr_t bucket{};
    if (!add(view.buckets, bucket_index * l.bucket_stride, bucket)) return false;
    std::uint32_t first{}, count{};
    if (!read_value(s, bucket, first) || !read_value(s, bucket + sizeof first, count) || first > view.key_count ||
        count > view.key_count - first)
        return false;
    const Identity wanted = identity_of(key);
    std::size_t found = view.key_count;
    for (std::size_t position = first; position < static_cast<std::size_t>(first) + count; ++position) {
        Identity candidate{};
        std::uintptr_t address{};
        if (!add(view.keys, position * l.key_stride, address) ||
            !copy_memory(s, address, candidate.data(), candidate.size()))
            return false;
        if (candidate == wanted) {
            if (found != view.key_count) return false;
            found = position;
        }
    }
    if (found == view.key_count) return false;
    std::array<unsigned char, 8> entry{};
    std::uintptr_t address{};
    if (!add(view.values, found * l.value_stride, address) || !copy_memory(s, address, entry.data(), entry.size()))
        return false;
    return span_from_entry(s, view, found, entry.data(), span);
}

// Read (read_loaded_resource.cpp:6-39) behind kiln.assets' served copy.
KilnResult snapshot(const Source& s, KilnOwner owner, const KilnAssetKey* key, void* bytes, std::size_t capacity,
                    std::size_t* required, KilnLoadedInfo* info) {
    if (!owner || !key || !required || !info || info->size < sizeof(KilnLoadedInfo) || (!bytes && capacity))
        return KILN_ERR_INVALID_ARGUMENT;
    reset(info);
    *required = 0;
    View before{};
    if (!resolve_view(s, before) || !registry_is_bound(s, before)) return refuse(info, KILN_LOADED_NOT_READY);
    const auto still_bound = [&] {
        View after{};
        return resolve_view(s, after) && registry_is_bound(s, after) && after == before;
    };
    std::vector<std::uint8_t> served;
    bool is_new = false;
    if (s.served && s.served(s.user, *key, &served, &is_new)) {
        Span game{};
        info->flags = KILN_LOADED_SERVED;
        info->handle = !is_new && locate(s, before, *key, game) ? game.handle : 0;
        info->bytes = served.size();
        *required = served.size();
        if (capacity < served.size()) return KILN_ERR_BUFFER_TOO_SMALL;
        if (!still_bound()) return refuse(info, KILN_LOADED_CHANGED);
        if (!served.empty()) std::memcpy(bytes, served.data(), served.size());
        return KILN_OK;
    }
    Span span{};
    if (!locate(s, before, *key, span)) return refuse(info, still_bound() ? KILN_LOADED_MISSING : KILN_LOADED_CHANGED);
    info->handle = span.handle;
    info->bytes = span.size;
    *required = span.size;
    if (capacity < span.size) return KILN_ERR_BUFFER_TOO_SMALL;
    std::vector<std::uint8_t> copy(span.size), confirmation(span.size);
    if (!copy_memory(s, span.data, copy.data(), copy.size()) ||
        !copy_memory(s, span.data, confirmation.data(), confirmation.size()) || copy != confirmation)
        return refuse(info, KILN_LOADED_CHANGED);
    View after{};
    Span again{};
    if (!resolve_view(s, after) || !registry_is_bound(s, after) || !(after == before) || !locate(s, after, *key, again) ||
        again.data != span.data || again.size != span.size || again.handle != span.handle)
        return refuse(info, KILN_LOADED_CHANGED);
    if (!copy.empty()) std::memcpy(bytes, copy.data(), copy.size());
    return KILN_OK;
}

// ResourcesByType (find_loaded_resources_by_type.cpp:86-106), then the mods' new keys of the type.
KilnResult list(const Source& s, KilnOwner owner, std::uint32_t type, KilnAssetKey* out, std::size_t capacity,
                std::size_t* required, KilnLoadedInfo* info) {
    if (!owner || !required || !info || info->size < sizeof(KilnLoadedInfo) || (!out && capacity))
        return KILN_ERR_INVALID_ARGUMENT;
    reset(info);
    *required = 0;
    View view{};
    if (!resolve_view(s, view) || !registry_is_bound(s, view)) return refuse(info, KILN_LOADED_NOT_READY);
    std::vector<KilnAssetKey> keys;
    if (!list_type(s, view, type, keys)) return refuse(info, KILN_LOADED_CHANGED);
    if (s.new_keys) {
        std::vector<KilnAssetKey> extra;
        s.new_keys(s.user, type, &extra);
        if (!extra.empty()) {
            std::set<Identity> seen;
            for (const auto& k : keys) seen.insert(identity_of(k));
            for (const auto& k : extra)
                if (k.type == type && seen.insert(identity_of(k)).second) keys.push_back(k);
        }
    }
    info->bytes = keys.size();
    *required = keys.size();
    if (capacity < keys.size()) return KILN_ERR_BUFFER_TOO_SMALL;
    if (!keys.empty()) std::memcpy(out, keys.data(), keys.size() * sizeof(KilnAssetKey));
    return KILN_OK;
}


// ---- the scalar compare-exchange ----------------------------------------------------------------------------------
// Adapted from ShroudForge (MIT, Copyright (c) 2026 bonsaibauer), modules/game-runtime/native/loaded-resources/
// compare_exchange_resource_scalar.cpp (compare_exchange_scalar for a session: the complete expected snapshot, the Scene
// gate with the content worker's lifetime guard borrowed from it, the lookup again inside the hold, the full compare,
// ScalarDataProtection, the interlocked exchange, the payload and owner checks after it, the uncertain latch). Kiln's:
// the gate is kiln.engine's (its admission, closing and priority raise); a key kiln.assets serves is refused before the
// gate; every refusal has its KILN_ECS_* outcome; the latch is the process's (ShroudForge's lasts until its resource
// runtime is initialized again, which is the next start).
namespace {
std::atomic<bool> g_write_latched{false};

using kiln::engine::EcsGateScope;
using kiln::engine::GateAdmission;
using kiln::engine::Reason;
using kiln::engine::ResourceGuardScope;
using kiln::engine::Role;

// The gate's refusals as kiln.world answers them (ecs_writes.cpp gate_status): busy can win later, the rest cannot.
std::uint32_t gate_status(Reason r, std::uint32_t* why) {
    switch (r) {
        case Reason::InFrame:
            *why = KILN_ECS_WRITE_WHY_IN_FRAME;
            return KILN_ECS_BUSY;
        case Reason::KilnHolderBusy:
        case Reason::KilnBusyLatched:
        case Reason::EcsSceneFrameBusy:
        case Reason::EcsThreadRegistryBusy:
        case Reason::EcsEngineOwnerChangedDuringRead:
        case Reason::EcsServerThreadChangedDuringRead:
        case Reason::EcsActiveSceneListChanged:
        case Reason::EcsReadySceneContextChanged:
        case Reason::EcsWriteOwnerChangedOrNotAuthoritative:
            *why = KILN_ECS_WRITE_WHY_GATE;
            return KILN_ECS_BUSY;
        default:
            *why = KILN_ECS_WRITE_WHY_GATE;
            return KILN_ECS_UNAVAILABLE;
    }
}

KilnResult result_of(std::uint32_t status) {
    switch (status) {
        case KILN_ECS_OK: return KILN_OK;
        case KILN_ECS_STALE: return KILN_ECS_ERR_STALE;
        case KILN_ECS_UNREADABLE: return KILN_ECS_ERR_UNREADABLE;
        case KILN_ECS_BUSY: return KILN_ECS_ERR_BUSY;
        case KILN_ECS_CONFLICT: return KILN_ECS_ERR_CONFLICT;
        case KILN_ECS_NOT_WRITABLE: return KILN_ECS_ERR_NOT_WRITABLE;
        case KILN_ECS_UNCERTAIN: return KILN_ECS_ERR_UNCERTAIN;
        default: return KILN_ERR_UNAVAILABLE;
    }
}

// Whether the engine's bytes at [address, address + size) equal `expected`, the one scalar at `patch_at` (width bytes)
// compared with `patch` instead when `patch` is set. Hold-safe: one fixed stack buffer, a bound of size / 4096 rounds.
// `patch` NULL with a width: the scalar's bytes are not compared at all (a conflict's other bytes).
bool payload_matches(const Source& s, std::uintptr_t address, const std::uint8_t* expected, std::size_t size,
                     std::size_t patch_at = 0, const std::uint8_t* patch = nullptr, std::size_t width = 0) noexcept {
    std::uint8_t chunk[4096];
    for (std::size_t at = 0; at < size; at += sizeof chunk) {
        const std::size_t n = size - at < sizeof chunk ? size - at : sizeof chunk;
        if (s.engine->copy(address + at, chunk, n) != KILN_OK) return false;
        for (std::size_t i = 0; i < n; ++i) {
            const std::size_t pos = at + i;
            const bool in_scalar = width && pos >= patch_at && pos < patch_at + width;
            if (in_scalar && !patch) continue;
            if (chunk[i] != (in_scalar ? patch[pos - patch_at] : expected[pos])) return false;
        }
    }
    return true;
}
}  // namespace

bool write_latched() { return g_write_latched.load(std::memory_order_acquire); }
void clear_write_latch_for_tests() { g_write_latched.store(false, std::memory_order_release); }

const char* write_refusal(const Source& s) {
    const KilnEngineNativeApi* e = s.engine;
    if (!e || !s.layout) return "no exact engine profile";
    if (e->header.size < offsetof(KilnEngineNativeApi, resource_guard_release) + sizeof(void*) || !e->ecs_view || !e->copy ||
        !e->ecs_gate_acquire || !e->ecs_gate_validate || !e->ecs_gate_release || !e->resource_guard_acquire ||
        !e->resource_guard_validate || !e->resource_guard_contains || !e->resource_guard_release)
        return "kiln.internal.engine has no write gate or content-worker lifetime guard";
    if (!s.layout->write.enabled) return "the profile's write gate is disabled";
    if (!s.layout->resource_write.enabled) return "the profile has no content-worker lifetime layout";
    if (!s.server_session || !s.protect || !s.restore || !s.cas) return "no page protection or compare-exchange here";
    return nullptr;
}

KilnResult compare_exchange(const Source& s, KilnOwner owner, const KilnAssetKey* key, std::uint64_t expected_session,
                            const void* expected_full, std::size_t resource_size, std::size_t offset, const void* desired,
                            std::uint32_t width, KilnMutationReply* reply) {
    return compare_exchange_named(s, owner, key, expected_session, expected_full, resource_size, offset, desired, width,
                                  KilnStr{nullptr, 0}, KilnStr{nullptr, 0}, reply);
}

KilnResult compare_exchange_named(const Source& s, KilnOwner owner, const KilnAssetKey* key, std::uint64_t expected_session,
                                  const void* expected_full, std::size_t resource_size, std::size_t offset, const void* desired,
                                  std::uint32_t width, KilnStr writer, KilnStr field, KilnMutationReply* reply) {
    if (!owner || !key || !expected_full || !desired || !reply || reply->size < sizeof(KilnMutationReply) ||
        !expected_session || !(width == 1 || width == 2 || width == 4 || width == 8) || !resource_size ||
        offset % width || offset > resource_size || width > resource_size - offset || !valid_name(writer) || !valid_name(field))
        return KILN_ERR_INVALID_ARGUMENT;
    const std::uint32_t reply_size = reply->size;
    *reply = KilnMutationReply{};
    reply->size = reply_size;
    reply->session = expected_session;
    const auto finish = [&](std::uint32_t status, std::uint32_t why, std::uint32_t flags = 0) {
        reply->status = status;
        reply->why_code = why;
        reply->flags |= flags;
        return result_of(status);
    };
    if (write_latched()) return finish(KILN_ECS_UNCERTAIN, KILN_ECS_WRITE_WHY_TARGET_LATCHED, KILN_MUTATION_TARGET_LATCHED);
    if (write_refusal(s)) return finish(KILN_ECS_UNAVAILABLE, KILN_ECS_WRITE_WHY_NO_PROFILE);
    const kiln::engine::ResourceLayout& l = s.layout->resource;
    if (resource_size > l.maximum_resource_bytes) return finish(KILN_ECS_NOT_WRITABLE, KILN_ECS_WRITE_WHY_SIZE);
    const std::uint64_t session = s.server_session(s.user);
    if (!session) return finish(KILN_ECS_UNAVAILABLE, KILN_ECS_WRITE_WHY_NO_WORLD);
    if (session != expected_session) return finish(KILN_ECS_STALE, KILN_ECS_WRITE_WHY_REF);
    // A key kiln.assets serves: the game reads the served copy, so the resident bytes are not what it uses.
    {
        std::vector<std::uint8_t> served;
        bool is_new = false;
        if (s.served && s.served(s.user, *key, &served, &is_new)) return finish(KILN_ECS_NOT_WRITABLE, KILN_ECS_WRITE_WHY_NOT_PLAIN);
    }
    reply->step = s.server_step ? s.server_step(s.user) : 0;
    // Staging, before any gate: the caller's buffers are copied once and never read inside the hold.
    std::vector<std::uint8_t> expected(resource_size);
    std::memcpy(expected.data(), expected_full, resource_size);
    std::uint64_t expected_bits = 0, desired_bits = 0;
    std::memcpy(&expected_bits, expected.data() + offset, width);
    std::memcpy(&desired_bits, desired, width);
    // The service and the local authority's view the gate takes.
    View initial{};
    if (!resolve_view(s, initial) || !registry_is_bound(s, initial)) return finish(KILN_ECS_UNAVAILABLE, KILN_ECS_WRITE_WHY_NO_WORLD);
    kiln::engine::EcsView ecs{};
    std::uint32_t engine_reason = 0;
    if (s.engine->ecs_view(0, &ecs, &engine_reason) != KILN_OK) {
        reply->engine_reason = engine_reason;
        std::uint32_t why = 0;
        const std::uint32_t status = gate_status(static_cast<Reason>(engine_reason), &why);
        return finish(status, why);
    }
    if (ecs.role != Role::LocalAuthority) return finish(KILN_ECS_UNAVAILABLE, KILN_ECS_WRITE_WHY_NOT_SERVER_WORLD);
    // Both views (the service's and the gate's) were taken in the expected session: sampled again after them, as
    // ShroudForge pins the session's owners for the whole write; the gate then proves those owners through the hold.
    if (s.server_session(s.user) != expected_session) return finish(KILN_ECS_STALE, KILN_ECS_WRITE_WHY_REF);
    std::uint32_t status = KILN_ECS_UNAVAILABLE, why = KILN_ECS_WRITE_WHY_GATE, flags = 0;
    const char* uncertain = nullptr;
    std::uint64_t hold_ns = 0;
    {
        EcsGateScope gate(*s.engine, ecs, owner, reply->step ? reply->step : 1, GateAdmission::KilnMod);
        if (!gate.held()) {
            const Reason r = gate.reason();
            gate.release();
            reply->engine_reason = static_cast<std::uint32_t>(r);
            std::uint32_t gwhy = 0;
            const std::uint32_t gstatus = gate_status(r, &gwhy);
            return finish(gstatus, gwhy);
        }
        const auto acquired = std::chrono::steady_clock::now();
        // ---- inside the hold: no allocation, lock, wait, log or KilnApi call ----
        [&]() noexcept {
            ResourceGuardScope guard(*s.engine, gate, initial.service, initial.reader);
            if (!guard.held()) {
                reply->engine_reason = static_cast<std::uint32_t>(guard.reason());
                status = KILN_ECS_UNAVAILABLE;
                why = KILN_ECS_WRITE_WHY_GATE;
                return;
            }
            const kiln::engine::Owners owners = guard.guard().owners;
            View before{};
            Span span{};
            if (!guard.validate() || !gate.validate() || !(owners == ecs.owners) || !resolve_view_with(s, owners, before) ||
                !(before == initial) || !registry_is_bound(s, before) || !locate(s, before, *key, span) ||
                !guard.contains(span.data, span.size)) {
                status = KILN_ECS_STALE;
                why = KILN_ECS_WRITE_WHY_REF;
                return;
            }
            if (span.size != resource_size) {
                status = KILN_ECS_STALE;
                why = KILN_ECS_WRITE_WHY_SIZE;
                return;
            }
            std::uintptr_t scalar = 0;
            if (span.data > UINTPTR_MAX - offset || (scalar = span.data + offset) % width) {
                status = KILN_ECS_STALE;
                why = KILN_ECS_WRITE_WHY_REF;
                return;
            }
            const auto stable = [&]() noexcept {
                View after{};
                Span current{};
                return guard.validate() && gate.validate() && guard.guard().owners == owners &&
                       resolve_view_with(s, owners, after) && after == before && registry_is_bound(s, after) &&
                       locate(s, after, *key, current) && current.data == span.data && current.size == span.size &&
                       current.handle == span.handle && current.chunk == span.chunk && guard.contains(current.data, current.size);
            };
            ProtectState protection{};
            const auto restore = [&]() noexcept { return s.restore(s.user, &protection); };
            // ShroudForge's ~ScalarDataProtection: a restore that failed gets one last attempt before the guard and the
            // gate go (the outcome stays uncertain: the failure is sticky).
            struct LastRestore {
                const Source& source;
                ProtectState& state;
                ~LastRestore() {
                    if (state.changed) source.restore(source.user, &state);
                }
            } last_restore{s, protection};
            if (!s.protect(s.user, scalar, width, guard.payload(), &protection)) {
                if (!restore()) uncertain = "resource-page-protection-restore-uncertain";
                status = KILN_ECS_UNAVAILABLE;
                why = KILN_ECS_WRITE_WHY_FAULT;
                return;
            }
            if (!stable()) {
                if (!restore()) uncertain = "resource-page-protection-restore-uncertain";
                status = KILN_ECS_STALE;
                why = KILN_ECS_WRITE_WHY_REF;
                return;
            }
            // The whole resource image the caller's field offset means (descriptors included), not only the scalar.
            if (!payload_matches(s, span.data, expected.data(), resource_size)) {
                if (!restore()) uncertain = "resource-page-protection-restore-uncertain";
                status = KILN_ECS_CONFLICT;
                why = KILN_ECS_WRITE_WHY_COMPARE;
                return;
            }
            std::uint64_t observed = 0;
            const bool executed = s.cas(s.user, scalar, width, expected_bits, desired_bits, &observed);
            bool unchanged_payload = true;
            if (executed && observed == expected_bits) {
                // written: the whole resource is the expected one with the desired scalar
                std::uint8_t written[8] = {};
                std::memcpy(written, &desired_bits, width);
                unchanged_payload = payload_matches(s, span.data, expected.data(), resource_size, offset, written, width);
            } else if (executed) {
                // a scalar conflict, no write of ours: every other byte (the variant descriptors) still the expected ones
                unchanged_payload = payload_matches(s, span.data, expected.data(), resource_size, offset, nullptr, width);
            }
            const bool unchanged_owner = stable();
            if (!restore()) {
                uncertain = "resource-page-protection-restore-uncertain";
            } else if (!executed) {
                uncertain = "resource-scalar-cas-uncertain";
            } else if (!unchanged_owner) {
                uncertain = "resource-scalar-owner-changed";
            } else if (!unchanged_payload) {
                uncertain = "resource-scalar-payload-changed-after-cas";
            }
            if (uncertain) return;
            if (observed == expected_bits) {
                status = KILN_ECS_OK;
                flags = KILN_MUTATION_WRITTEN | KILN_MUTATION_CONFIRMED;
            } else {
                status = KILN_ECS_CONFLICT;
                why = KILN_ECS_WRITE_WHY_COMPARE;
            }
        }();
        gate.release();
        hold_ns = static_cast<std::uint64_t>(
            std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now() - acquired).count());
    }
    reply->hold_ns = hold_ns;
    if (uncertain) {
        g_write_latched.store(true, std::memory_order_release);
        return finish(KILN_ECS_UNCERTAIN, std::strcmp(uncertain, "resource-scalar-cas-uncertain") == 0 ? KILN_ECS_WRITE_WHY_FAULT
                                                                                                       : KILN_ECS_WRITE_WHY_READBACK,
                      KILN_MUTATION_TARGET_LATCHED);
    }
    if (status == KILN_ECS_OK && s.committed) {
        Commit c;
        c.owner = owner;
        c.key = *key;
        c.offset = offset;
        c.width = width;
        c.session = expected_session;
        c.before = expected_bits;
        c.after = desired_bits;
        if (writer.len) c.writer = std::string_view(writer.ptr, writer.len);
        if (field.len) c.field = std::string_view(field.ptr, field.len);
        s.committed(s.user, c);
    }
    return finish(status, why, flags);
}

// ---- named loaded writes (sfi R3) -----------------------------------------------------------------------------------

bool valid_name(KilnStr name) {
    if (!name.len) return true;
    if (!name.ptr || name.len > KILN_LOADED_WRITE_NAME_MAX) return false;
    const auto* p = reinterpret_cast<const unsigned char*>(name.ptr);
    for (std::size_t i = 0; i < name.len;) {
        const unsigned char c = p[i];
        if (c < 0x20 || c == 0x7f) return false;
        if (c < 0x80) {
            ++i;
            continue;
        }
        std::size_t n = 0;
        std::uint32_t cp = 0, min = 0;
        if ((c & 0xE0) == 0xC0) n = 2, cp = c & 0x1F, min = 0x80;
        else if ((c & 0xF0) == 0xE0) n = 3, cp = c & 0x0F, min = 0x800;
        else if ((c & 0xF8) == 0xF0) n = 4, cp = c & 0x07, min = 0x10000;
        else return false;
        if (n > name.len - i) return false;
        for (std::size_t k = 1; k < n; ++k) {
            if ((p[i + k] & 0xC0) != 0x80) return false;
            cp = cp << 6 | (p[i + k] & 0x3F);
        }
        if (cp < min || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF) || (cp >= 0x80 && cp < 0xA0)) return false;
        i += n;
    }
    return true;
}

std::uint64_t WriteRecords::commit(const Commit& c) {
    Place place{};
    std::memcpy(place.key, &c.key, sizeof place.key);
    place.offset = c.offset;
    place.width = c.width;
    std::lock_guard lock(mu_);
    const std::uint64_t id = ++ids_;
    auto it = records_.find(place);
    if (it != records_.end()) {
        Record& r = it->second;
        by_id_.erase(r.id);
        // the same owner and writer over the value its last write left: the run goes on (it keeps what it began over)
        const bool run = r.owner == c.owner && r.writer == c.writer && r.after == c.before;
        if (!run) r.before = c.before;
        r.id = id, r.owner = c.owner, r.session = c.session, r.after = c.after;
        r.writer.assign(c.writer), r.field.assign(c.field);
    } else {
        while (records_.size() >= places_ && !by_id_.empty()) {  // the place written longest ago makes room
            records_.erase(by_id_.begin()->second);
            by_id_.erase(by_id_.begin());
            ++dropped_;
        }
        Record r;
        r.id = id, r.owner = c.owner, r.session = c.session, r.before = c.before, r.after = c.after;
        r.writer.assign(c.writer), r.field.assign(c.field);
        records_.emplace(place, std::move(r));
    }
    by_id_.emplace(id, place);
    recent_.push_back({id, c.owner, c.key, c.offset, c.width, c.session, std::string(c.writer), std::string(c.field)});
    if (recent_.size() > 256) recent_.pop_front();
    return id;
}

KilnResult WriteRecords::read(KilnLoadedWriteRecord* out, std::uint32_t record_size, std::size_t capacity,
                              std::size_t* required) const {
    if (!required || record_size < sizeof(KilnLoadedWriteRecord) || (!out && capacity)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(mu_);
    *required = by_id_.size();
    if (capacity < by_id_.size()) return KILN_ERR_BUFFER_TOO_SMALL;
    auto* at = reinterpret_cast<unsigned char*>(out);
    for (const auto& [id, place] : by_id_) {
        const Record& r = records_.at(place);
        KilnLoadedWriteRecord w{};
        w.size = sizeof w;
        w.width = place.width;
        w.id = r.id;
        w.owner = r.owner;
        w.session = r.session;
        std::memcpy(&w.key, place.key, sizeof w.key);
        w.offset = place.offset;
        std::memcpy(w.before, &r.before, place.width);
        std::memcpy(w.after, &r.after, place.width);
        std::memcpy(w.writer, r.writer.data(), std::min(r.writer.size(), sizeof w.writer - 1));
        std::memcpy(w.field, r.field.data(), std::min(r.field.size(), sizeof w.field - 1));
        std::memcpy(at, &w, sizeof w);
        std::memset(at + sizeof w, 0, record_size - sizeof w);
        at += record_size;
    }
    return KILN_OK;
}

std::vector<WriteRecords::Recent> WriteRecords::recent() const {
    std::lock_guard lock(mu_);
    return {recent_.begin(), recent_.end()};
}

std::size_t WriteRecords::dropped() const {
    std::lock_guard lock(mu_);
    return dropped_;
}

}  // namespace ka::loaded
