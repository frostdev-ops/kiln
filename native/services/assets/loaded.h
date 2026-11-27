// SPDX-License-Identifier: MIT
// kiln.assets' loaded-resource reads (kiln_assets.h loaded_snapshot, loaded_list): the game's resource service as the exact
// engine profile lays it out (kiln_engine_layout.h ResourceLayout), read from any thread through kiln.internal.engine's
// guarded copy and owner view only. Portable: no Windows and no KilnApi here, so the host test (tests/test_loaded.cpp) runs
// this exact code over a fake engine and loaded_prod.cpp wires it to the running game.
//
// Adapted from ShroudForge (MIT, Copyright (c) 2026 bonsaibauer), modules/game-runtime/native/loaded-resources/:
//   resolve_loaded_resource.cpp (resolve_view, same_view, span_from_entry, registry_is_bound, locate, relative_pointer,
//     canonical_resource_hash), read_loaded_resource.cpp (Read: two equal copies of a span found again afterwards),
//   find_loaded_resources_by_type.cpp (snapshot_arrays, list_type, ResourcesByType: the index copied twice and equal).
// The same reads and checks in the same order. Differences, all Kiln's:
// - the owners come from kiln.internal.engine's ECS view (the local authority when this process hosts one, else the client
//   replica), so a read refuses when that view does, and every engine byte goes through its SEH copy;
// - a key kiln.assets serves (an override, or a mod's new resource) answers the served bytes the game reads, and a
//   listing has the mods' new keys after the game's;
// - a lookup that finds nothing in an index that changed meanwhile answers CHANGED, not MISSING.
#pragma once
#include <compare>
#include <cstddef>
#include <cstdint>
#include <deque>
#include <map>
#include <mutex>
#include <string>
#include <string_view>
#include <vector>

#include "kiln_engine_native.h"
#include "services/kiln_assets.h"

namespace ka::loaded {

// A committed scalar write, as kiln.assets records it (after the hold): the caller's names (sfi R3, kiln_assets.h named
// loaded writes; views of the caller's text for the call only, empty: none), the scalar it was over and the one it left.
struct Commit {
    KilnOwner owner = 0;
    KilnAssetKey key{};
    std::size_t offset = 0;
    std::uint32_t width = 0;
    std::uint64_t session = 0;
    std::uint64_t before = 0, after = 0;  // the scalar's bits (little end first, width bytes)
    std::string_view writer, field;
};

// The resource service as one instant of the engine (ShroudForge's View).
struct View {
    std::uintptr_t image, root, client, controller, main_owner, context;
    std::uintptr_t registry, registry_resource, service, reader, metadata, map;
    std::uintptr_t buckets, keys, values, chunks;
    std::uint32_t registry_handle, bucket_count, key_count, value_count, chunk_count;
    bool operator==(const View&) const = default;
};

// Where one resource's bytes lie (ShroudForge's Span): handle is 1-based.
struct Span {
    std::uintptr_t data;
    std::size_t size;
    std::uint32_t handle;
    std::uint8_t chunk;
};

struct ProtectState;

struct Source {
    const KilnEngineNativeApi* engine = nullptr;   // copy and ecs_view (NULL: not ready)
    const kiln::engine::Layout* layout = nullptr;  // the exact profile's layout (NULL: not ready)
    void* user = nullptr;
    // The whole span is committed readable memory (ShroudForge's readable(), VirtualQuery); NULL: assume readable.
    bool (*readable)(void* user, std::uintptr_t address, std::size_t size) = nullptr;
    // Kiln's served copy of `key` (an override or a mod's new resource) into *out; false: kiln.assets serves none.
    bool (*served)(void* user, const KilnAssetKey& key, std::vector<std::uint8_t>* out, bool* is_new) = nullptr;
    // The mods' new keys of `type`, in kiln.assets' order.
    void (*new_keys)(void* user, std::uint32_t type, std::vector<KilnAssetKey>* out) = nullptr;

    // ---- the scalar compare-exchange (loaded_scalar_compare_exchange) ----
    // The server world's session now (kiln.world game_world_session(KILN_WORLD_SERVER)), 0 without one.
    std::uint64_t (*server_session)(void* user) = nullptr;
    // The core's server step counter (KilnSessionInfo.steps), 0 when unknown.
    std::uint64_t (*server_step)(void* user) = nullptr;
    // Inside the hold (no allocation, lock, wait or log): make [address, address + width) writable for one scalar of a
    // private committed page of the allocation at `allocation_base` (ShroudForge's ScalarDataProtection::Prepare), and
    // put its protection back (Restore: true only when the old protection is in place again and checked; a failure is
    // sticky, and a failed restore that left the protection changed is called once more before the hold ends).
    bool (*protect)(void* user, std::uintptr_t address, std::size_t width, std::uintptr_t allocation_base,
                    struct ProtectState* state) = nullptr;
    bool (*restore)(void* user, struct ProtectState* state) = nullptr;
    // One interlocked compare-exchange of `width` bytes at `address` under SEH: false when it faulted (outcome unknown).
    bool (*cas)(void* user, std::uintptr_t address, std::size_t width, std::uint64_t expected, std::uint64_t desired,
                std::uint64_t* observed) = nullptr;
    // A committed write (after the hold, for kiln.assets' record and log).
    void (*committed)(void* user, const Commit& commit) = nullptr;
};

// ShroudForge's ScalarDataProtection state (the caller's, on its stack).
struct ProtectState {
    std::uintptr_t address, allocation_base;
    std::size_t width;
    std::uint32_t old_protection;
    bool changed, restore_failed;
};

// The resource service now, or false with a static reason. Reads only.
bool resolve_view(const Source& s, View& view, const char** reason = nullptr);
// The same with owners a held gate or guard proved (no owner view is taken: hold-safe, no allocation).
bool resolve_view_with(const Source& s, const kiln::engine::Owners& owners, View& view, const char** reason = nullptr);
bool registry_is_bound(const Source& s, const View& view);
bool locate(const Source& s, const View& view, const KilnAssetKey& key, Span& span);
std::uint32_t canonical_resource_hash(const KilnAssetKey& key);

// The table members (kiln_assets.h).
KilnResult snapshot(const Source& s, KilnOwner owner, const KilnAssetKey* key, void* bytes, std::size_t capacity,
                    std::size_t* required, KilnLoadedInfo* info);
KilnResult list(const Source& s, KilnOwner owner, std::uint32_t type, KilnAssetKey* out, std::size_t capacity,
                std::size_t* required, KilnLoadedInfo* info);
KilnResult compare_exchange(const Source& s, KilnOwner owner, const KilnAssetKey* key, std::uint64_t expected_session,
                            const void* expected_full, std::size_t resource_size, std::size_t offset, const void* desired,
                            std::uint32_t width, KilnMutationReply* reply);
// The same with the writer and field its record names (loaded_scalar_compare_exchange_named): each valid_name text.
KilnResult compare_exchange_named(const Source& s, KilnOwner owner, const KilnAssetKey* key, std::uint64_t expected_session,
                                  const void* expected_full, std::size_t resource_size, std::size_t offset, const void* desired,
                                  std::uint32_t width, KilnStr writer, KilnStr field, KilnMutationReply* reply);
// A name a named write may carry: 0 to KILN_LOADED_WRITE_NAME_MAX bytes of UTF-8 (no overlong form, surrogate or code
// point past U+10FFFF) without a control character (below 0x20, 0x7f, U+0080 to U+009F); ptr may be NULL only when len is 0.
bool valid_name(KilnStr name);

// kiln.assets' records of the committed writes (kiln_assets.h named loaded writes): one per place (key, offset, width) for
// its newest write, a run of the same owner and writer (each write over the value the one before left) keeping the value it
// began over; at most `places` of them, the place written longest ago dropped first. And the last 256 writes, for status.
// Any thread: one lock, held for copies only.
class WriteRecords {
public:
    struct Recent {  // one write, for kiln.assets' status ("loadedWrites")
        std::uint64_t id;
        KilnOwner owner;
        KilnAssetKey key;
        std::size_t offset;
        std::uint32_t width;
        std::uint64_t session;
        std::string writer, field;
    };
    explicit WriteRecords(std::size_t places = KILN_LOADED_WRITE_PLACES) : places_(places ? places : 1) {}
    // Records a committed write; returns its id (1, 2, ...).
    std::uint64_t commit(const Commit& c);
    // loaded_write_records: the records, oldest newest-write first.
    KilnResult read(KilnLoadedWriteRecord* out, std::uint32_t record_size, std::size_t capacity, std::size_t* required) const;
    std::vector<Recent> recent() const;
    std::size_t dropped() const;  // places dropped for room

private:
    struct Place {
        std::uint8_t key[24];  // KilnAssetKey's bytes
        std::uint64_t offset;
        std::uint32_t width;
        auto operator<=>(const Place&) const = default;
    };
    struct Record {
        std::uint64_t id = 0;
        KilnOwner owner = 0;
        std::uint64_t session = 0, before = 0, after = 0;
        std::string writer, field;
    };
    mutable std::mutex mu_;
    std::size_t places_;
    std::map<Place, Record> records_;
    std::map<std::uint64_t, Place> by_id_;  // each record's newest write id -> its place (the eviction order)
    std::deque<Recent> recent_;
    std::uint64_t ids_ = 0;
    std::size_t dropped_ = 0;
};
// Whether an uncertain write latched every later compare-exchange of this process (and clears it: host tests only).
bool write_latched();
void clear_write_latch_for_tests();
// Why the writes cannot run on `s` at all (static), or nullptr when they can: the gate and guard members and an enabled
// content-worker layout.
const char* write_refusal(const Source& s);

}  // namespace ka::loaded
