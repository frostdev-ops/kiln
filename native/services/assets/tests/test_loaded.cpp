// SPDX-License-Identifier: MIT
// Host test of kiln.assets' loaded-resource reads (loaded.cpp, kiln_assets.h loaded_snapshot / loaded_list): the exact
// code over a fake engine (a resource service, its index, chunk table and item registry in plain memory, read through a
// fake kiln.internal.engine copy that refuses any address outside its regions). Built twice by tests/run.sh: ASan+UBSan,
// and TSan for the concurrent case. No game, no Wine.
#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <map>
#include <mutex>
#include <set>
#include <thread>
#include <vector>

#include "loaded.h"

namespace {
int g_failures = 0;
#define CHECK(c)                                                              \
    do {                                                                      \
        if (!(c)) {                                                           \
            std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #c);          \
            ++g_failures;                                                     \
        }                                                                     \
    } while (0)

// ---- fake engine memory -----------------------------------------------------------------------------------------
std::mutex g_mem;                       // the fake engine's own lock (the mutator takes it too)
std::map<std::uintptr_t, std::size_t> g_regions;
int g_copies_until_mutation = -1;       // a mutation hook fires on this copy (counted down), -1 never
void (*g_mutation)() = nullptr;
bool g_view_ok = true;
bool g_authority = true;
std::uint64_t g_server_session = 77;    // the writes' server session (fake_session)
int g_views_until_new_session = -1;     // a new server session begins on this ecs_view call (counted down), -1 never

bool inside(std::uintptr_t a, std::size_t n) {
    auto it = g_regions.upper_bound(a);
    if (it == g_regions.begin()) return false;
    --it;
    return a >= it->first && n <= it->second && a - it->first <= it->second - n;
}

KilnResult KILN_CALL fake_copy(std::uintptr_t address, void* out, std::size_t bytes) {
    void (*fire)() = nullptr;
    {
        std::lock_guard lock(g_mem);
        if (!bytes) return KILN_OK;
        if (!out) return KILN_ERR_INVALID_ARGUMENT;
        if (!inside(address, bytes)) return KILN_ERR_UNAVAILABLE;
        std::memcpy(out, reinterpret_cast<const void*>(address), bytes);
        if (g_copies_until_mutation > 0 && --g_copies_until_mutation == 0) fire = g_mutation;
    }
    if (fire) fire();
    return KILN_OK;
}

// ---- the fake service ---------------------------------------------------------------------------------------------
constexpr std::size_t kKeys = 64;
struct Fake {
    alignas(16) unsigned char image[0x400]{};
    alignas(16) unsigned char main_owner[0x40]{};
    alignas(16) unsigned char context[0x40]{};
    alignas(16) unsigned char registry[0x40]{};
    alignas(16) unsigned char service[0x80]{};
    alignas(16) unsigned char reader[0x40]{};
    // metadata: the map header at +0x20, then buckets, keys and values (rel32 from the header's fields)
    alignas(16) unsigned char metadata[0x20 + 24 + 8 * 16 + kKeys * 32 + kKeys * 8]{};
    alignas(16) unsigned char chunks[2 * 16]{};
    alignas(16) unsigned char data[2][4096]{};
};
Fake* g_fake = nullptr;
kiln::engine::Layout g_layout{};
KilnEngineNativeApi g_engine{};

KilnResult KILN_CALL fake_view(std::uint32_t kind, kiln::engine::EcsView* out, std::uint32_t* reason) {
    if (!out || !reason || kind > 1) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mem);
    if (g_views_until_new_session >= 0 && g_views_until_new_session-- == 0) ++g_server_session;
    if (!g_view_ok) {
        *reason = 1;
        return KILN_ERR_UNAVAILABLE;
    }
    *out = {};
    out->owners.image = reinterpret_cast<std::uintptr_t>(g_fake->image);
    out->owners.main_owner = reinterpret_cast<std::uintptr_t>(g_fake->main_owner);
    out->role = g_authority ? kiln::engine::Role::LocalAuthority : kiln::engine::Role::ClientReplica;
    *reason = 0;
    return KILN_OK;
}

template <typename T>
void put(unsigned char* base, std::size_t offset, T value) {
    std::memcpy(base + offset, &value, sizeof value);
}
std::uintptr_t addr(const void* p) { return reinterpret_cast<std::uintptr_t>(p); }

KilnAssetKey key_of(std::uint8_t seed, std::uint32_t type, std::uint32_t part = 0) {
    KilnAssetKey k{};
    for (int i = 0; i < 16; ++i) k.guid[i] = static_cast<std::uint8_t>(seed * 7 + i + 1);
    k.type = type;
    k.part = part;
    return k;
}

// The index: key i of `keys` lies in data chunk (i % 2) at offset i * 48, `sizes[i]` bytes of (seed i).
struct Entry {
    KilnAssetKey key;
    std::uint32_t size, offset;
    std::uint8_t chunk;
};
std::vector<Entry> g_entries;
constexpr std::uint32_t kTypeA = 0xb5ce8765u, kTypeB = 0x11112222u;
constexpr std::size_t kMap = 0x20, kBuckets = kMap + 24, kKeysAt = kBuckets + 8 * 16, kValuesAt = kKeysAt + kKeys * 32;
constexpr std::uint32_t kBucketCount = 16;

void write_index() {  // with g_mem held or before any thread
    unsigned char* m = g_fake->metadata;
    std::vector<std::size_t> order(g_entries.size());
    for (std::size_t i = 0; i < order.size(); ++i) order[i] = i;
    auto bucket = [](const KilnAssetKey& k) { return ka::loaded::canonical_resource_hash(k) & (kBucketCount - 1); };
    std::stable_sort(order.begin(), order.end(),
                     [&](std::size_t a, std::size_t b) { return bucket(g_entries[a].key) < bucket(g_entries[b].key); });
    std::memset(m + kBuckets, 0, 8 * kBucketCount);
    for (std::size_t pos = 0; pos < order.size(); ++pos) {
        const Entry& e = g_entries[order[pos]];
        unsigned char* k = m + kKeysAt + pos * 32;
        std::memset(k, 0, 32);
        std::memcpy(k, e.key.guid, 16);
        std::memcpy(k + 16, &e.key.type, 4);
        std::memcpy(k + 20, &e.key.part, 4);
        unsigned char* v = m + kValuesAt + pos * 8;
        std::memcpy(v, &e.offset, 4);
        const std::uint32_t packed = (e.size & 0xFFFFFFu) | (static_cast<std::uint32_t>(e.chunk) << 24);
        std::memcpy(v + 4, &packed, 4);
        const auto b = bucket(e.key);
        std::uint32_t first{}, count{};
        std::memcpy(&first, m + kBuckets + b * 8, 4);
        std::memcpy(&count, m + kBuckets + b * 8 + 4, 4);
        if (!count) first = static_cast<std::uint32_t>(pos);
        ++count;
        std::memcpy(m + kBuckets + b * 8, &first, 4);
        std::memcpy(m + kBuckets + b * 8 + 4, &count, 4);
    }
    const auto n = static_cast<std::uint32_t>(g_entries.size());
    put<std::uint32_t>(m, kMap + 12, n);
    put<std::uint32_t>(m, kMap + 20, n);
    // the registry handle: the position of entry 0 (the item registry's own resource)
    for (std::size_t pos = 0; pos < order.size(); ++pos)
        if (order[pos] == 0) put<std::uint32_t>(g_fake->registry, 16, static_cast<std::uint32_t>(pos + 1));
}

std::uint32_t handle_of(std::size_t entry) {
    auto bucket = [](const KilnAssetKey& k) { return ka::loaded::canonical_resource_hash(k) & (kBucketCount - 1); };
    std::vector<std::size_t> order(g_entries.size());
    for (std::size_t i = 0; i < order.size(); ++i) order[i] = i;
    std::stable_sort(order.begin(), order.end(),
                     [&](std::size_t a, std::size_t b) { return bucket(g_entries[a].key) < bucket(g_entries[b].key); });
    for (std::size_t pos = 0; pos < order.size(); ++pos)
        if (order[pos] == entry) return static_cast<std::uint32_t>(pos + 1);
    return 0;
}

void fill_data(std::size_t i, unsigned char seed) {
    const Entry& e = g_entries[i];
    for (std::uint32_t b = 0; b < e.size; ++b) g_fake->data[e.chunk][e.offset + b] = static_cast<unsigned char>(seed + b);
}

void build() {
    delete g_fake;
    g_fake = new Fake{};
    g_regions.clear();
    for (auto [p, n] : {std::pair{addr(g_fake->image), sizeof g_fake->image},
                        {addr(g_fake->main_owner), sizeof g_fake->main_owner},
                        {addr(g_fake->context), sizeof g_fake->context},
                        {addr(g_fake->registry), sizeof g_fake->registry},
                        {addr(g_fake->service), sizeof g_fake->service},
                        {addr(g_fake->reader), sizeof g_fake->reader},
                        {addr(g_fake->metadata), sizeof g_fake->metadata},
                        {addr(g_fake->chunks), sizeof g_fake->chunks},
                        {addr(g_fake->data[0]), sizeof g_fake->data[0]},
                        {addr(g_fake->data[1]), sizeof g_fake->data[1]}})
        g_regions[p] = n;
    g_layout = {};
    g_layout.dedicated_server = false;
    auto& r = g_layout.resource;
    r.main_active_context_offset = 0x10;
    r.context_item_registry_offset = 0x18;
    r.item_registry_service_offset = 8;
    r.item_registry_handle_offset = 16;
    r.item_registry_resource_pointer_offset = 32;
    r.resource_service_reader_offset = 8;
    r.resource_service_chunk_table_offset = 48;
    r.resource_service_id_callback_offset = 0x60;
    r.resource_service_handle_callback_offset = 0x68;
    r.resource_service_id_callback_rva = 0x100;
    r.resource_service_handle_callback_rva = 0x200;
    r.resource_service_chunk_count_offset = 88;
    r.reader_metadata_offset = 0x10;
    r.metadata_map_offset = kMap;
    r.bucket_rel32_offset = 0;
    r.bucket_count_offset = 4;
    r.keys_rel32_offset = 8;
    r.key_count_offset = 12;
    r.values_rel32_offset = 16;
    r.value_count_offset = 20;
    r.map_header_size = 24;
    r.bucket_stride = 8;
    r.key_stride = 32;
    r.resource_key_size = 24;
    r.value_stride = 8;
    r.resource_offset_offset = 0;
    r.packed_size_offset = 4;
    r.chunk_index_offset = 7;
    r.size_mask = 0xFFFFFF;
    r.chunk_stride = 16;
    r.chunk_base_pointer_offset = 0;
    r.chunk_size_offset = 8;
    r.maximum_entries = 1u << 20;
    r.maximum_chunks = 65536;
    r.maximum_resource_bytes = 256u << 20;
    Fake& f = *g_fake;
    put<std::uintptr_t>(f.main_owner, 0x10, addr(f.context));
    put<std::uintptr_t>(f.context, 0x18, addr(f.registry));
    put<std::uintptr_t>(f.registry, 8, addr(f.service));
    put<std::uintptr_t>(f.service, 8, addr(f.reader));
    put<std::uintptr_t>(f.service, 48, addr(f.chunks));
    put<std::uint32_t>(f.service, 88, 2);
    put<std::uintptr_t>(f.service, 0x60, addr(f.image) + 0x100);
    put<std::uintptr_t>(f.service, 0x68, addr(f.image) + 0x200);
    put<std::uintptr_t>(f.reader, 0x10, addr(f.metadata));
    unsigned char* m = f.metadata;
    put<std::int32_t>(m, kMap + 0, static_cast<std::int32_t>(kBuckets - (kMap + 0)));
    put<std::uint32_t>(m, kMap + 4, kBucketCount);
    put<std::int32_t>(m, kMap + 8, static_cast<std::int32_t>(kKeysAt - (kMap + 8)));
    put<std::int32_t>(m, kMap + 16, static_cast<std::int32_t>(kValuesAt - (kMap + 16)));
    for (int c = 0; c < 2; ++c) {
        put<std::uintptr_t>(f.chunks, c * 16, addr(f.data[c]));
        put<std::uint64_t>(f.chunks, c * 16 + 8, sizeof f.data[c]);
    }
    g_entries.clear();
    // entry 0: the item registry's resource; then 6 of type A, 3 of type B
    for (std::uint8_t i = 0; i < 10; ++i) {
        const std::uint32_t type = i == 0 ? 0x99990000u : (i <= 6 ? kTypeA : kTypeB);
        g_entries.push_back({key_of(i, type, i == 3 ? 1u : 0u), static_cast<std::uint32_t>(16 + i * 4),
                             static_cast<std::uint32_t>(i * 48), static_cast<std::uint8_t>(i % 2)});
    }
    for (std::size_t i = 0; i < g_entries.size(); ++i) fill_data(i, static_cast<unsigned char>(i * 16));
    put<std::uintptr_t>(f.registry, 32, addr(f.data[g_entries[0].chunk]) + g_entries[0].offset);
    write_index();
    g_engine = {};
    g_engine.header = {sizeof g_engine, KILN_ENGINE_NATIVE_VERSION};
    g_engine.copy = &fake_copy;
    g_engine.ecs_view = &fake_view;
    g_view_ok = true;
    g_authority = true;
    g_layout.write.enabled = true;
    g_layout.resource_write.enabled = true;
    g_copies_until_mutation = -1;
    g_mutation = nullptr;
}

// ---- served copies and the readability filter ------------------------------------------------------------------------
std::map<std::vector<unsigned char>, std::pair<std::vector<std::uint8_t>, bool>> g_served;
std::vector<KilnAssetKey> g_new;
std::set<std::uintptr_t> g_unreadable;
std::vector<unsigned char> id_bytes(const KilnAssetKey& k) {
    std::vector<unsigned char> v(24);
    std::memcpy(v.data(), k.guid, 16);
    std::memcpy(v.data() + 16, &k.type, 4);
    std::memcpy(v.data() + 20, &k.part, 4);
    return v;
}
bool served(void*, const KilnAssetKey& k, std::vector<std::uint8_t>* out, bool* is_new) {
    const auto it = g_served.find(id_bytes(k));
    if (it == g_served.end()) return false;
    *out = it->second.first;
    *is_new = it->second.second;
    return true;
}
void new_keys(void*, std::uint32_t type, std::vector<KilnAssetKey>* out) {
    for (const auto& k : g_new)
        if (k.type == type) out->push_back(k);
}
bool readable(void*, std::uintptr_t a, std::size_t) { return !g_unreadable.count(a); }

ka::loaded::Source source() {
    ka::loaded::Source s;
    s.engine = &g_engine;
    s.layout = &g_layout;
    s.served = &served;
    s.new_keys = &new_keys;
    s.readable = &readable;
    return s;
}

KilnLoadedInfo info() {
    KilnLoadedInfo i{};
    i.size = sizeof i;
    return i;
}

std::vector<unsigned char> expected_bytes(std::size_t i, unsigned char seed) {
    std::vector<unsigned char> v(g_entries[i].size);
    for (std::size_t b = 0; b < v.size(); ++b) v[b] = static_cast<unsigned char>(seed + b);
    return v;
}

void test_snapshot() {
    build();
    const auto s = source();
    unsigned char buf[256];
    std::size_t need = 0;
    auto in = info();
    // a resident key: two equal copies of a span found again, the game's handle, no flag
    CHECK(ka::loaded::snapshot(s, 7, &g_entries[2].key, buf, sizeof buf, &need, &in) == KILN_OK);
    CHECK(need == g_entries[2].size && in.bytes == need && in.status == KILN_LOADED_OK && in.flags == 0);
    CHECK(in.handle == handle_of(2));
    CHECK(std::memcmp(buf, expected_bytes(2, 32).data(), need) == 0);
    // a part index is part of the key
    in = info();
    CHECK(ka::loaded::snapshot(s, 7, &g_entries[3].key, buf, sizeof buf, &need, &in) == KILN_OK && need == g_entries[3].size);
    KilnAssetKey other_part = g_entries[3].key;
    other_part.part = 0;
    in = info();
    CHECK(ka::loaded::snapshot(s, 7, &other_part, buf, sizeof buf, &need, &in) == KILN_ERR_NOT_FOUND &&
          in.status == KILN_LOADED_MISSING && need == 0);
    // too small: the size, nothing copied
    std::memset(buf, 0xEE, sizeof buf);
    in = info();
    CHECK(ka::loaded::snapshot(s, 7, &g_entries[2].key, buf, 4, &need, &in) == KILN_ERR_BUFFER_TOO_SMALL);
    CHECK(need == g_entries[2].size && in.bytes == need && buf[0] == 0xEE);
    in = info();
    CHECK(ka::loaded::snapshot(s, 7, &g_entries[2].key, nullptr, 0, &need, &in) == KILN_ERR_BUFFER_TOO_SMALL &&
          need == g_entries[2].size);
    // missing, and the zero GUID
    const KilnAssetKey absent = key_of(200, kTypeA);
    in = info();
    CHECK(ka::loaded::snapshot(s, 7, &absent, buf, sizeof buf, &need, &in) == KILN_ERR_NOT_FOUND &&
          in.status == KILN_LOADED_MISSING);
    KilnAssetKey zero{};
    zero.type = kTypeA;
    in = info();
    CHECK(ka::loaded::snapshot(s, 7, &zero, buf, sizeof buf, &need, &in) == KILN_ERR_NOT_FOUND);
    // bad arguments: info untouched
    in = info();
    in.status = 77;
    CHECK(ka::loaded::snapshot(s, 0, &g_entries[2].key, buf, sizeof buf, &need, &in) == KILN_ERR_INVALID_ARGUMENT &&
          in.status == 77);
    CHECK(ka::loaded::snapshot(s, 7, nullptr, buf, sizeof buf, &need, &in) == KILN_ERR_INVALID_ARGUMENT);
    CHECK(ka::loaded::snapshot(s, 7, &g_entries[2].key, nullptr, 8, &need, &in) == KILN_ERR_INVALID_ARGUMENT);
    CHECK(ka::loaded::snapshot(s, 7, &g_entries[2].key, buf, sizeof buf, nullptr, &in) == KILN_ERR_INVALID_ARGUMENT);
    CHECK(ka::loaded::snapshot(s, 7, &g_entries[2].key, buf, sizeof buf, &need, nullptr) == KILN_ERR_INVALID_ARGUMENT);
    in.size = 16;
    CHECK(ka::loaded::snapshot(s, 7, &g_entries[2].key, buf, sizeof buf, &need, &in) == KILN_ERR_INVALID_ARGUMENT);
    std::printf("PASS loaded.snapshot (copy, handle, part, too small, missing, zero GUID, arguments)\n");
}

void expect_not_ready(const char* what) {
    const auto s = source();
    unsigned char buf[256];
    std::size_t need = 99;
    auto in = info();
    const KilnResult r = ka::loaded::snapshot(s, 7, &g_entries[2].key, buf, sizeof buf, &need, &in);
    CHECK(r == KILN_ERR_UNAVAILABLE && in.status == KILN_LOADED_NOT_READY && need == 0);
    KilnAssetKey keys[16];
    in = info();
    CHECK(ka::loaded::list(s, 7, kTypeA, keys, 16, &need, &in) == KILN_ERR_UNAVAILABLE && in.status == KILN_LOADED_NOT_READY);
    if (r != KILN_ERR_UNAVAILABLE) std::printf("  (not ready case: %s)\n", what);
}

void test_not_ready() {
    build();
    g_view_ok = false;
    expect_not_ready("owner view refused");
    build();
    g_layout.dedicated_server = true;  // the profile is a server's, the owners a client's
    expect_not_ready("owner target");
    build();
    put<std::uintptr_t>(g_fake->service, 0x68, addr(g_fake->image) + 0x204);
    expect_not_ready("callback");
    build();
    put<std::uintptr_t>(g_fake->registry, 32, addr(g_fake->data[1]) + 1);  // the registry resource is not the bound one
    expect_not_ready("registry not bound");
    build();
    put<std::uintptr_t>(g_fake->main_owner, 0x10, 0);  // no active context (the main menu)
    expect_not_ready("no context");
    build();
    put<std::uint32_t>(g_fake->metadata, kMap + 4, 12);  // a bucket count that is no power of two
    expect_not_ready("bucket count");
    build();
    put<std::uint32_t>(g_fake->metadata, kMap + 20, 3);  // keys and values disagree
    expect_not_ready("counts");
    build();
    {
        auto s = source();
        s.layout = nullptr;
        unsigned char buf[8];
        std::size_t need = 0;
        auto in = info();
        CHECK(ka::loaded::snapshot(s, 7, &g_entries[2].key, buf, sizeof buf, &need, &in) == KILN_ERR_UNAVAILABLE &&
              in.status == KILN_LOADED_NOT_READY);
        s = source();
        s.engine = nullptr;
        CHECK(ka::loaded::snapshot(s, 7, &g_entries[2].key, buf, sizeof buf, &need, &in) == KILN_ERR_UNAVAILABLE);
    }
    std::printf("PASS loaded.not-ready (owner view, target, callbacks, unbound registry, no context, index header, no profile)\n");
}

void test_changed() {
    unsigned char buf[256];
    std::size_t need = 0;
    // the bytes change between the two copies: never published
    build();
    {
        const auto s = source();
        // copies of one read: view (many), locate, then the two payload copies; mutate the payload right after the first
        int copies = 0;
        g_copies_until_mutation = 1000000;
        auto in = info();
        ka::loaded::snapshot(s, 7, &g_entries[2].key, buf, sizeof buf, &need, &in);
        copies = 1000000 - g_copies_until_mutation;  // how many copies one read makes
        // find the copy index of the first payload copy: the read makes 2 payload copies, then the after-view
        bool changed_once = false;
        for (int at = 1; at <= copies; ++at) {
            build();
            const auto s2 = source();
            g_copies_until_mutation = at;
            g_mutation = [] { fill_data(2, 99); };
            std::memset(buf, 0, sizeof buf);
            auto i2 = info();
            const KilnResult r = ka::loaded::snapshot(s2, 7, &g_entries[2].key, buf, sizeof buf, &need, &i2);
            if (r == KILN_OK) {
                const auto a = expected_bytes(2, 32), b = expected_bytes(2, 99);
                CHECK(std::memcmp(buf, a.data(), a.size()) == 0 || std::memcmp(buf, b.data(), b.size()) == 0);
            } else {
                CHECK(r == KILN_ERR_UNAVAILABLE && i2.status == KILN_LOADED_CHANGED);
                changed_once = true;
            }
        }
        CHECK(changed_once);
    }
    // the span moves after the copies (the value entry points elsewhere): CHANGED
    {
        bool moved = false;
        for (int at = 1; at < 200 && !moved; ++at) {
            build();
            const auto s = source();
            g_copies_until_mutation = at;
            g_mutation = [] {
                std::lock_guard lock(g_mem);
                g_entries[2].offset += 8;
                write_index();
            };
            auto in = info();
            const KilnResult r = ka::loaded::snapshot(s, 7, &g_entries[2].key, buf, sizeof buf, &need, &in);
            if (r == KILN_ERR_UNAVAILABLE) {
                CHECK(in.status == KILN_LOADED_CHANGED);
                moved = true;
            }
        }
        CHECK(moved);
    }
    // a lookup that misses in an index that changed meanwhile is CHANGED, not MISSING
    {
        bool seen = false;
        for (int at = 1; at < 200 && !seen; ++at) {
            build();
            const auto s = source();
            g_copies_until_mutation = at;
            g_mutation = [] {
                std::lock_guard lock(g_mem);
                put<std::uintptr_t>(g_fake->reader, 0x10, addr(g_fake->metadata) + 0);  // same
                put<std::uint32_t>(g_fake->service, 88, 1);  // the chunk count moves: a different view
            };
            const KilnAssetKey absent = key_of(201, kTypeA);
            auto in = info();
            const KilnResult r = ka::loaded::snapshot(s, 7, &absent, buf, sizeof buf, &need, &in);
            if (r == KILN_ERR_UNAVAILABLE && in.status == KILN_LOADED_CHANGED) seen = true;
            else CHECK(r == KILN_ERR_NOT_FOUND || r == KILN_ERR_UNAVAILABLE);
        }
        CHECK(seen);
    }
    std::printf("PASS loaded.changed (torn copy never published, moved span, a miss in a changed index)\n");
}

void test_served() {
    build();
    g_served.clear();
    g_new.clear();
    const auto s = source();
    unsigned char buf[256];
    std::size_t need = 0;
    // an override: the served bytes, the game's handle, SERVED
    g_served[id_bytes(g_entries[4].key)] = {{1, 2, 3}, false};
    auto in = info();
    CHECK(ka::loaded::snapshot(s, 7, &g_entries[4].key, buf, sizeof buf, &need, &in) == KILN_OK);
    CHECK(need == 3 && buf[0] == 1 && buf[2] == 3 && in.flags == KILN_LOADED_SERVED && in.handle == handle_of(4));
    // a mod's new resource: handle 0
    const KilnAssetKey fresh = key_of(150, kTypeA);
    g_served[id_bytes(fresh)] = {{9, 9, 9, 9, 9}, true};
    g_new.push_back(fresh);
    in = info();
    CHECK(ka::loaded::snapshot(s, 7, &fresh, buf, sizeof buf, &need, &in) == KILN_OK && need == 5 &&
          in.flags == KILN_LOADED_SERVED && in.handle == 0);
    in = info();
    CHECK(ka::loaded::snapshot(s, 7, &fresh, buf, 2, &need, &in) == KILN_ERR_BUFFER_TOO_SMALL && need == 5);
    // served bytes answer only in a world
    g_view_ok = false;
    in = info();
    CHECK(ka::loaded::snapshot(s, 7, &fresh, buf, sizeof buf, &need, &in) == KILN_ERR_UNAVAILABLE &&
          in.status == KILN_LOADED_NOT_READY);
    g_served.clear();
    g_new.clear();
    std::printf("PASS loaded.served (an override's bytes with the game's handle, a new resource, only in a world)\n");
}

void test_list() {
    build();
    g_served.clear();
    g_new.clear();
    g_unreadable.clear();
    auto s = source();
    KilnAssetKey keys[32];
    std::size_t need = 0;
    auto in = info();
    // every key of the type, in the index's order
    CHECK(ka::loaded::list(s, 7, kTypeA, keys, 32, &need, &in) == KILN_OK && need == 6 && in.bytes == 6);
    std::set<std::vector<unsigned char>> got;
    for (std::size_t i = 0; i < need; ++i) got.insert(id_bytes(keys[i]));
    for (std::size_t i = 1; i <= 6; ++i) CHECK(got.count(id_bytes(g_entries[i].key)) == 1);
    in = info();
    CHECK(ka::loaded::list(s, 7, kTypeB, keys, 32, &need, &in) == KILN_OK && need == 3);
    in = info();
    CHECK(ka::loaded::list(s, 7, 0x12345678u, keys, 32, &need, &in) == KILN_OK && need == 0);
    // too small: the count, nothing copied
    std::memset(keys, 0xEE, sizeof keys);
    in = info();
    CHECK(ka::loaded::list(s, 7, kTypeA, keys, 2, &need, &in) == KILN_ERR_BUFFER_TOO_SMALL && need == 6 &&
          keys[0].guid[0] == 0xEE);
    in = info();
    CHECK(ka::loaded::list(s, 7, kTypeA, nullptr, 0, &need, &in) == KILN_ERR_BUFFER_TOO_SMALL && need == 6);
    CHECK(ka::loaded::list(s, 7, kTypeA, nullptr, 4, &need, &in) == KILN_ERR_INVALID_ARGUMENT);
    CHECK(ka::loaded::list(s, 0, kTypeA, keys, 32, &need, &in) == KILN_ERR_INVALID_ARGUMENT);
    // a span that is not readable is left out
    g_unreadable.insert(addr(g_fake->data[g_entries[5].chunk]) + g_entries[5].offset);
    in = info();
    CHECK(ka::loaded::list(s, 7, kTypeA, keys, 32, &need, &in) == KILN_OK && need == 5);
    g_unreadable.clear();
    // the mods' new keys after the game's, once each (an override's key is the game's already)
    const KilnAssetKey fresh = key_of(151, kTypeA);
    g_new = {fresh, g_entries[1].key, fresh};
    in = info();
    CHECK(ka::loaded::list(s, 7, kTypeA, keys, 32, &need, &in) == KILN_OK && need == 7);
    CHECK(id_bytes(keys[6]) == id_bytes(fresh));
    g_new.clear();
    // a key held twice by the index: CHANGED
    build();
    s = source();
    g_entries[6].key = g_entries[5].key;
    write_index();
    in = info();
    CHECK(ka::loaded::list(s, 7, kTypeA, keys, 32, &need, &in) == KILN_ERR_UNAVAILABLE && in.status == KILN_LOADED_CHANGED);
    // the index changes between its two copies: CHANGED
    bool changed = false;
    for (int at = 1; at < 100 && !changed; ++at) {
        build();
        s = source();
        g_copies_until_mutation = at;
        g_mutation = [] {
            std::lock_guard lock(g_mem);
            g_entries[3].offset += 4;
            write_index();
        };
        in = info();
        const KilnResult r = ka::loaded::list(s, 7, kTypeA, keys, 32, &need, &in);
        if (r == KILN_ERR_UNAVAILABLE && in.status == KILN_LOADED_CHANGED) changed = true;
        else CHECK(r == KILN_OK);
    }
    CHECK(changed);
    std::printf("PASS loaded.list (index order, too small, unreadable spans left out, new keys once, duplicate, changed)\n");
}

void test_hash() {
    // ShroudForge's canonical StaticHash, coded again from its definition: seeded with the GUID's first four bytes
    // (little endian), then FNV-1a's step over the type's and the part's bytes, little endian.
    for (std::uint8_t seed : {1, 2, 77, 200}) {
        const KilnAssetKey k = key_of(seed, 0xdeadbeefu, seed);
        std::uint32_t h = static_cast<std::uint32_t>(k.guid[0]) | static_cast<std::uint32_t>(k.guid[1]) << 8 |
                          static_cast<std::uint32_t>(k.guid[2]) << 16 | static_cast<std::uint32_t>(k.guid[3]) << 24;
        unsigned char tail[8];
        std::memcpy(tail, &k.type, 4);
        std::memcpy(tail + 4, &k.part, 4);
        for (unsigned char b : tail) h = (h ^ b) * 0x01000193u;
        CHECK(ka::loaded::canonical_resource_hash(k) == h);
    }
    std::printf("PASS loaded.hash (the canonical resource hash)\n");
}

// Readers on four threads while a writer flips a resource between two complete states and moves another: every answer is
// one whole state or a refusal (TSan: no race but the fake engine's own, which it locks).
void test_concurrent() {
    build();
    g_served.clear();
    g_new.clear();
    const auto s = source();
    std::atomic<bool> stop{false};
    std::atomic<int> torn{0}, ok{0};
    std::thread writer([&] {
        for (int i = 0; i < 2000; ++i) {
            std::lock_guard lock(g_mem);
            fill_data(2, (i & 1) ? 99 : 32);
            g_entries[4].offset = (i & 1) ? 4 * 48 + 8 : 4 * 48;
            write_index();
        }
        stop = true;
    });
    std::vector<std::thread> readers;
    for (int t = 0; t < 4; ++t)
        readers.emplace_back([&] {
            unsigned char buf[256];
            KilnAssetKey keys[32];
            while (!stop) {
                std::size_t need = 0;
                auto in = info();
                if (ka::loaded::snapshot(s, 7, &g_entries[2].key, buf, sizeof buf, &need, &in) == KILN_OK) {
                    bool whole = true;
                    for (std::size_t b = 1; b < need; ++b)
                        if (buf[b] != static_cast<unsigned char>(buf[0] + b)) whole = false;
                    if (!whole || (buf[0] != 32 && buf[0] != 99)) ++torn;
                    ++ok;
                }
                in = info();
                ka::loaded::list(s, 7, kTypeA, keys, 32, &need, &in);
            }
        });
    writer.join();
    for (auto& r : readers) r.join();
    CHECK(torn == 0);
    std::printf("PASS loaded.concurrent (%d whole reads, no torn copy)\n", ok.load());
}

// ---- the scalar compare-exchange --------------------------------------------------------------------------------------
std::atomic<bool> g_gate_taken{false};  // the real gate admits one Kiln holder at a time
std::uint32_t g_gate_refuse = 0, g_guard_refuse = 0;
bool g_gate_valid = true;
std::atomic<int> g_acquires{0}, g_releases{0}, g_guards{0}, g_guard_releases{0}, g_protects{0}, g_restores{0}, g_commits{0};
bool g_protect_fail = false, g_restore_fail = false;
void (*g_before_cas)() = nullptr;  // runs right before the exchange (another writer)
void (*g_after_cas)() = nullptr;   // right after it
int g_restore_failures = 0;        // the next this many restores fail, then they work again

KilnResult KILN_CALL fake_gate_acquire(const kiln::engine::EcsView* expected, kiln::engine::EcsGate* gate, std::uint32_t* reason) {
    if (!expected || !gate || !reason) return KILN_ERR_INVALID_ARGUMENT;
    if (g_gate_refuse) {
        *reason = g_gate_refuse;
        return KILN_ERR_UNAVAILABLE;
    }
    if (g_gate_taken.exchange(true)) {
        *reason = static_cast<std::uint32_t>(kiln::engine::Reason::KilnHolderBusy);
        return KILN_ERR_UNAVAILABLE;
    }
    ++g_acquires;
    gate->held = 1;
    gate->registry_lock = gate->scene_lock = 1;
    gate->expected = *expected;
    *reason = 0;
    return KILN_OK;
}
std::int32_t KILN_CALL fake_gate_validate(const kiln::engine::EcsGate* gate) { return gate->held && g_gate_valid ? 1 : 0; }
void KILN_CALL fake_gate_release(kiln::engine::EcsGate* gate) {
    if (gate->held) {
        gate->held = 0;
        ++g_releases;
        g_gate_taken = false;
    }
}
KilnResult KILN_CALL fake_guard_acquire(const kiln::engine::EcsGate* gate, std::uintptr_t service, std::uintptr_t reader,
                                       kiln::engine::ResourceGuard* out, std::uint32_t* reason) {
    if (!out || !reason || !service || !reader) return KILN_ERR_INVALID_ARGUMENT;
    if (g_guard_refuse || !gate || !gate->held) {
        *reason = g_guard_refuse ? g_guard_refuse : static_cast<std::uint32_t>(kiln::engine::Reason::ResourceLifetimeGateNotHeld);
        return KILN_ERR_UNAVAILABLE;
    }
    ++g_guards;
    out->held = 1;
    out->gate = gate;
    out->registry_lock = gate->registry_lock;
    out->service = service;
    out->reader = reader;
    out->payload = addr(g_fake->data[0]);
    out->payload_size = sizeof g_fake->data;
    out->owners = gate->expected.owners;
    return KILN_OK;
}
std::int32_t KILN_CALL fake_guard_validate(const kiln::engine::ResourceGuard* g) { return g->held && g->gate && g->gate->held ? 1 : 0; }
std::int32_t KILN_CALL fake_guard_contains(const kiln::engine::ResourceGuard* g, std::uintptr_t a, std::size_t n) {
    return g->held && a >= g->payload && a - g->payload <= g->payload_size && n <= g->payload_size - (a - g->payload) ? 1 : 0;
}
void KILN_CALL fake_guard_release(kiln::engine::ResourceGuard* g) {
    if (g->held) ++g_guard_releases;
    g->held = 0;
}

std::uint64_t fake_session(void*) { return g_server_session; }
std::uint64_t fake_step(void*) { return 5; }
bool fake_protect(void*, std::uintptr_t a, std::size_t w, std::uintptr_t base, ka::loaded::ProtectState* st) {
    ++g_protects;
    *st = {};
    st->address = a;
    st->width = w;
    st->allocation_base = base;
    CHECK(base == addr(g_fake->data[0]));  // the guard's payload base
    st->changed = true;                     // as a read-only page made writable
    return !g_protect_fail;
}
// As restore_scalar: a failed restore leaves the protection changed and the failure sticky.
bool fake_restore(void*, ka::loaded::ProtectState* st) {
    if (st->address) ++g_restores;
    if (g_restore_fail || g_restore_failures > 0) {
        if (g_restore_failures > 0) --g_restore_failures;
        st->restore_failed = true;
        return false;
    }
    st->changed = false;
    return !st->restore_failed;
}
bool fake_cas(void*, std::uintptr_t a, std::size_t w, std::uint64_t expected, std::uint64_t desired, std::uint64_t* observed) {
    if (g_before_cas) g_before_cas();
    {
        std::lock_guard lock(g_mem);
        std::uint64_t now = 0;
        std::memcpy(&now, reinterpret_cast<const void*>(a), w);
        *observed = now;
        if (now == expected) std::memcpy(reinterpret_cast<void*>(a), &desired, w);
    }
    if (g_after_cas) g_after_cas();
    return true;
}
// The last committed write as the record gets it (names copied: the views are the caller's for the call only).
struct SeenCommit {
    KilnOwner owner = 0;
    std::size_t offset = 0;
    std::uint32_t width = 0;
    std::uint64_t session = 0, before = 0, after = 0;
    std::string writer, field;
};
std::mutex g_commit_mu;  // writers commit from several threads (test_write_concurrent)
SeenCommit g_last_commit;
ka::loaded::WriteRecords g_commit_records;  // every committed write, as kiln.assets keeps them (TSan: concurrent commits)
void fake_committed(void*, const ka::loaded::Commit& c) {
    ++g_commits;
    g_commit_records.commit(c);
    std::lock_guard lock(g_commit_mu);
    g_last_commit = {c.owner, c.offset, c.width, c.session, c.before, c.after, std::string(c.writer), std::string(c.field)};
}

ka::loaded::Source write_source() {
    auto s = source();
    g_engine.ecs_gate_acquire = &fake_gate_acquire;
    g_engine.ecs_gate_validate = &fake_gate_validate;
    g_engine.ecs_gate_release = &fake_gate_release;
    g_engine.resource_guard_acquire = &fake_guard_acquire;
    g_engine.resource_guard_validate = &fake_guard_validate;
    g_engine.resource_guard_contains = &fake_guard_contains;
    g_engine.resource_guard_release = &fake_guard_release;
    s.server_session = &fake_session;
    s.server_step = &fake_step;
    s.protect = &fake_protect;
    s.restore = &fake_restore;
    s.cas = &fake_cas;
    s.committed = &fake_committed;
    return s;
}

void reset_write_knobs() {
    g_gate_refuse = g_guard_refuse = 0;
    g_gate_valid = true;
    g_protect_fail = g_restore_fail = false;
    g_restore_failures = 0;
    g_views_until_new_session = -1;
    g_before_cas = g_after_cas = nullptr;
    g_server_session = 77;
    g_served.clear();
    ka::loaded::clear_write_latch_for_tests();
}

KilnMutationReply reply() {
    KilnMutationReply r{};
    r.size = sizeof r;
    return r;
}

std::vector<unsigned char> current(std::size_t i) {
    std::lock_guard lock(g_mem);
    const Entry& e = g_entries[i];
    const unsigned char* p = g_fake->data[e.chunk] + e.offset;
    return {p, p + e.size};
}

KilnResult write_u32(const ka::loaded::Source& s, std::size_t i, const std::vector<unsigned char>& expected, std::size_t offset,
                     std::uint32_t value, KilnMutationReply& r, std::size_t size = 0) {
    return ka::loaded::compare_exchange(s, 7, &g_entries[i].key, 77, expected.data(), size ? size : expected.size(), offset,
                                        &value, 4, &r);
}

void test_write() {
    build();
    reset_write_knobs();
    const auto s = write_source();
    const int acquires = g_acquires, releases = g_releases, guards = g_guards, guard_releases = g_guard_releases;
    // written: the scalar changed, everything else as it was, the gate and guard released, one record
    auto before = current(2);
    auto r = reply();
    CHECK(write_u32(s, 2, before, 4, 0xAABBCCDDu, r) == KILN_OK);
    CHECK(r.status == KILN_ECS_OK && r.flags == (KILN_MUTATION_WRITTEN | KILN_MUTATION_CONFIRMED) && r.session == 77 && r.step == 5);
    auto after = current(2);
    std::uint32_t got = 0;
    std::memcpy(&got, after.data() + 4, 4);
    CHECK(got == 0xAABBCCDDu);
    CHECK(std::equal(after.begin(), after.begin() + 4, before.begin()) && std::equal(after.begin() + 8, after.end(), before.begin() + 8));
    CHECK(g_acquires - acquires == 1 && g_releases - releases == 1 && g_guards - guards == 1 && g_guard_releases - guard_releases == 1);
    CHECK(g_commits == 1 && g_protects >= 1 && g_restores >= 1);
    // a stale snapshot: CONFLICT, nothing written, no record
    r = reply();
    CHECK(write_u32(s, 2, before, 4, 1u, r) == KILN_ECS_ERR_CONFLICT && r.status == KILN_ECS_CONFLICT &&
          r.why_code == KILN_ECS_WRITE_WHY_COMPARE);
    CHECK(current(2) == after && g_commits == 1);
    // another writer changes the scalar right before the exchange: CONFLICT, not uncertain
    before = current(2);
    g_before_cas = [] {
        std::lock_guard lock(g_mem);
        g_fake->data[g_entries[2].chunk][g_entries[2].offset + 4] ^= 0x01;
    };
    r = reply();
    CHECK(write_u32(s, 2, before, 4, 9u, r) == KILN_ECS_ERR_CONFLICT && !ka::loaded::write_latched());
    g_before_cas = nullptr;
    // another byte changes during the exchange: UNCERTAIN, latched for every later write
    before = current(2);
    g_after_cas = [] {
        std::lock_guard lock(g_mem);
        g_fake->data[g_entries[2].chunk][g_entries[2].offset + 0] ^= 0x10;
    };
    r = reply();
    CHECK(write_u32(s, 2, before, 4, 3u, r) == KILN_ECS_ERR_UNCERTAIN && r.status == KILN_ECS_UNCERTAIN &&
          (r.flags & KILN_MUTATION_TARGET_LATCHED) && ka::loaded::write_latched());
    g_after_cas = nullptr;
    const int acquired_before_latch = g_acquires;
    before = current(4);
    r = reply();
    CHECK(write_u32(s, 4, before, 4, 3u, r) == KILN_ECS_ERR_UNCERTAIN && r.why_code == KILN_ECS_WRITE_WHY_TARGET_LATCHED &&
          g_acquires == acquired_before_latch);
    ka::loaded::clear_write_latch_for_tests();
    // the protection cannot be restored: UNCERTAIN; cannot be prepared (restored): UNAVAILABLE, not latched
    before = current(4);
    g_restore_fail = true;
    r = reply();
    CHECK(write_u32(s, 4, before, 4, 3u, r) == KILN_ECS_ERR_UNCERTAIN && ka::loaded::write_latched());
    g_restore_fail = false;
    ka::loaded::clear_write_latch_for_tests();
    before = current(4);
    g_protect_fail = true;
    r = reply();
    CHECK(write_u32(s, 4, before, 4, 3u, r) == KILN_ERR_UNAVAILABLE && r.why_code == KILN_ECS_WRITE_WHY_FAULT &&
          !ka::loaded::write_latched() && current(4) == before);
    g_protect_fail = false;
    // one restore per write; a restore that failed gets one last attempt before the guard and gate go (ShroudForge's
    // ~ScalarDataProtection), and the write stays uncertain
    before = current(4);
    int restores = g_restores;
    r = reply();
    CHECK(write_u32(s, 4, before, 4, 5u, r) == KILN_OK && g_restores - restores == 1);
    before = current(4);
    restores = g_restores;
    g_restore_failures = 1;
    r = reply();
    CHECK(write_u32(s, 4, before, 4, 6u, r) == KILN_ECS_ERR_UNCERTAIN && ka::loaded::write_latched());
    CHECK(g_restores - restores == 2 && g_restore_failures == 0);
    ka::loaded::clear_write_latch_for_tests();
    before = current(4);
    restores = g_restores;
    g_protect_fail = true;
    g_restore_failures = 1;
    r = reply();
    CHECK(write_u32(s, 4, before, 4, 7u, r) == KILN_ECS_ERR_UNCERTAIN && g_restores - restores == 2 && current(4) == before);
    g_protect_fail = false;
    ka::loaded::clear_write_latch_for_tests();
    std::printf("PASS loaded.write (written and read back, stale snapshot, scalar conflict, payload changed: latched, protection)\n");
}

void test_write_refusals() {
    build();
    reset_write_knobs();
    const auto s = write_source();
    auto before = current(2);
    auto r = reply();
    const int acquires = g_acquires;
    // a key kiln.assets serves: never the gate
    g_served[id_bytes(g_entries[2].key)] = {{1, 2, 3, 4}, false};
    CHECK(write_u32(s, 2, before, 4, 1u, r) == KILN_ECS_ERR_NOT_WRITABLE && r.why_code == KILN_ECS_WRITE_WHY_NOT_PLAIN);
    g_served.clear();
    // another session, none
    g_server_session = 78;
    r = reply();
    CHECK(write_u32(s, 2, before, 4, 1u, r) == KILN_ECS_ERR_STALE && r.why_code == KILN_ECS_WRITE_WHY_REF);
    g_server_session = 0;
    r = reply();
    CHECK(write_u32(s, 2, before, 4, 1u, r) == KILN_ERR_UNAVAILABLE && r.why_code == KILN_ECS_WRITE_WHY_NO_WORLD);
    g_server_session = 77;
    // a new server session begins while the write takes its views (the service's, the gate's): STALE, never the gate
    for (int at = 0; at < 2; ++at) {
        g_views_until_new_session = at;
        const int gate_before = g_acquires;
        r = reply();
        CHECK(write_u32(s, 2, before, 4, 1u, r) == KILN_ECS_ERR_STALE && r.why_code == KILN_ECS_WRITE_WHY_REF);
        CHECK(current(2) == before && g_acquires == gate_before && g_server_session == 78);
        g_views_until_new_session = -1;
        g_server_session = 77;
    }
    // a joined client's replica
    g_authority = false;
    r = reply();
    CHECK(write_u32(s, 2, before, 4, 1u, r) == KILN_ERR_UNAVAILABLE && r.why_code == KILN_ECS_WRITE_WHY_NOT_SERVER_WORLD);
    g_authority = true;
    CHECK(g_acquires == acquires);
    // the gate busy (a frame), the gate unavailable, the guard refused
    g_gate_refuse = static_cast<std::uint32_t>(kiln::engine::Reason::EcsSceneFrameBusy);
    r = reply();
    CHECK(write_u32(s, 2, before, 4, 1u, r) == KILN_ECS_ERR_BUSY && r.engine_reason == g_gate_refuse);
    g_gate_refuse = static_cast<std::uint32_t>(kiln::engine::Reason::EcsWriteGateDisabled);
    r = reply();
    CHECK(write_u32(s, 2, before, 4, 1u, r) == KILN_ERR_UNAVAILABLE && r.why_code == KILN_ECS_WRITE_WHY_GATE);
    g_gate_refuse = 0;
    g_guard_refuse = static_cast<std::uint32_t>(kiln::engine::Reason::ResourceLifetimeWorkerUnregistered);
    const int releases = g_releases;
    r = reply();
    CHECK(write_u32(s, 2, before, 4, 1u, r) == KILN_ERR_UNAVAILABLE && r.engine_reason == g_guard_refuse && g_releases == releases + 1);
    g_guard_refuse = 0;
    // the gate stops validating inside the hold: STALE, nothing written
    g_gate_valid = false;
    r = reply();
    CHECK(write_u32(s, 2, before, 4, 1u, r) == KILN_ECS_ERR_STALE && current(2) == before);
    g_gate_valid = true;
    // the resource's size is not the snapshot's
    auto longer = before;
    longer.resize(before.size() + 4);
    r = reply();
    CHECK(write_u32(s, 2, longer, 4, 1u, r) == KILN_ECS_ERR_STALE && r.why_code == KILN_ECS_WRITE_WHY_SIZE);
    // no content-worker layout
    g_layout.resource_write.enabled = false;
    r = reply();
    CHECK(write_u32(s, 2, before, 4, 1u, r) == KILN_ERR_UNAVAILABLE && r.why_code == KILN_ECS_WRITE_WHY_NO_PROFILE);
    g_layout.resource_write.enabled = true;
    // arguments: width, alignment, bounds, session 0, a short reply
    std::uint32_t v = 1;
    r = reply();
    CHECK(ka::loaded::compare_exchange(s, 7, &g_entries[2].key, 77, before.data(), before.size(), 3, &v, 3, &r) ==
          KILN_ERR_INVALID_ARGUMENT);
    CHECK(ka::loaded::compare_exchange(s, 7, &g_entries[2].key, 77, before.data(), before.size(), 2, &v, 4, &r) ==
          KILN_ERR_INVALID_ARGUMENT);
    CHECK(ka::loaded::compare_exchange(s, 7, &g_entries[2].key, 77, before.data(), before.size(), before.size() - 2, &v, 4, &r) ==
          KILN_ERR_INVALID_ARGUMENT);
    CHECK(ka::loaded::compare_exchange(s, 7, &g_entries[2].key, 0, before.data(), before.size(), 4, &v, 4, &r) ==
          KILN_ERR_INVALID_ARGUMENT);
    CHECK(ka::loaded::compare_exchange(s, 0, &g_entries[2].key, 77, before.data(), before.size(), 4, &v, 4, &r) ==
          KILN_ERR_INVALID_ARGUMENT);
    r.size = 8;
    CHECK(ka::loaded::compare_exchange(s, 7, &g_entries[2].key, 77, before.data(), before.size(), 4, &v, 4, &r) ==
          KILN_ERR_INVALID_ARGUMENT);
    CHECK(current(2) == before && !g_gate_taken);
    std::printf("PASS loaded.write-refusals (served key, session, replica, busy and closed gate, guard, size, layout, arguments)\n");
}

// Four writers add 1 to one counter with snapshot and compare-exchange retries, beside readers: no lost update (the gate
// admits one holder; TSan: no race but the fake engine's own, which it locks).
void test_write_concurrent() {
    build();
    reset_write_knobs();
    const auto s = write_source();
    std::atomic<int> wins{0};
    std::vector<std::thread> threads;
    for (int t = 0; t < 4; ++t)
        threads.emplace_back([&] {
            unsigned char buf[256];
            for (int n = 0; n < 50;) {
                std::size_t need = 0;
                auto in = info();
                if (ka::loaded::snapshot(s, 7, &g_entries[3].key, buf, sizeof buf, &need, &in) != KILN_OK) continue;
                std::vector<unsigned char> expected(buf, buf + need);
                std::uint32_t value = 0;
                std::memcpy(&value, buf + 8, 4);
                auto r = reply();
                const KilnResult rc = write_u32(s, 3, expected, 8, value + 1, r);
                if (rc == KILN_OK) {
                    ++wins;
                    ++n;
                } else {
                    CHECK(rc == KILN_ECS_ERR_CONFLICT || rc == KILN_ECS_ERR_BUSY);
                }
            }
        });
    for (auto& t : threads) t.join();
    std::uint32_t start = 0, end = 0;
    const auto now = current(3);
    std::memcpy(&end, now.data() + 8, 4);
    start = static_cast<std::uint32_t>(48 + 8) | static_cast<std::uint32_t>(48 + 9) << 8 | static_cast<std::uint32_t>(48 + 10) << 16 |
            static_cast<std::uint32_t>(48 + 11) << 24;
    CHECK(wins == 200 && end == start + 200 && !ka::loaded::write_latched());
    // every committed write reached the records, from four threads (each write's record under the records' own lock)
    std::size_t places = 0;
    CHECK(g_commit_records.read(nullptr, sizeof(KilnLoadedWriteRecord), 0, &places) != KILN_ERR_INVALID_ARGUMENT && places >= 1);
    CHECK(g_commit_records.recent().size() == 256 || g_commit_records.recent().size() >= 200);
    std::printf("PASS loaded.write-concurrent (%d writes, no lost update)\n", wins.load());
}

// ---- named loaded writes (sfi R3: loaded_scalar_compare_exchange_named, loaded_write_records) ----------------------------
KilnStr str(const char* s) { return KilnStr{s, std::strlen(s)}; }
KilnResult write_named(const ka::loaded::Source& s, std::size_t i, const std::vector<unsigned char>& expected, std::size_t offset,
                       std::uint32_t value, KilnStr writer, KilnStr field, KilnMutationReply& r) {
    return ka::loaded::compare_exchange_named(s, 7, &g_entries[i].key, 77, expected.data(), expected.size(), offset, &value, 4,
                                              writer, field, &r);
}

// The write's record gets the caller's names, the scalar it was over and the one it left; the unnamed member records none.
// A name that is not text of at most 127 bytes is refused before anything (no gate, no reply, nothing written).
void test_write_named() {
    build();
    reset_write_knobs();
    const auto s = write_source();
    auto before = current(2);
    std::uint32_t old = 0;
    std::memcpy(&old, before.data() + 4, 4);
    auto r = reply();
    const int commits = g_commits;
    CHECK(write_named(s, 2, before, 4, 1234u, str("shroudforge:alpha"), str("maxStackSize"), r) == KILN_OK);
    CHECK(r.status == KILN_ECS_OK && g_commits == commits + 1);
    CHECK(g_last_commit.owner == 7 && g_last_commit.offset == 4 && g_last_commit.width == 4 && g_last_commit.session == 77);
    CHECK(g_last_commit.before == old && g_last_commit.after == 1234u);
    CHECK(g_last_commit.writer == "shroudforge:alpha" && g_last_commit.field == "maxStackSize");
    // the unnamed member: the same write, recorded without names
    before = current(2);
    r = reply();
    CHECK(write_u32(s, 2, before, 4, 1235u, r) == KILN_OK && g_commits == commits + 2);
    CHECK(g_last_commit.writer.empty() && g_last_commit.field.empty() && g_last_commit.before == 1234u && g_last_commit.after == 1235u);
    // names of 127 bytes, and none at all, are fine
    const std::string longest(127, 'w');
    before = current(2);
    r = reply();
    CHECK(write_named(s, 2, before, 4, 1236u, KilnStr{longest.data(), longest.size()}, KilnStr{nullptr, 0}, r) == KILN_OK);
    CHECK(g_last_commit.writer == longest && g_last_commit.field.empty());
    // refused names: nothing happens, not even the reply
    const std::string too_long(128, 'w');
    const char control[] = "shroud\nforge";
    const char del[] = "a\x7f";
    const char broken[] = "caf\xc3";
    const char overlong[] = "\xc0\xaf";
    const char surrogate[] = "\xed\xa0\x80";
    const KilnStr bad[] = {KilnStr{too_long.data(), too_long.size()}, str(control), str(del), str(broken), str(overlong), str(surrogate),
                           KilnStr{nullptr, 3}};
    before = current(2);
    const int acquires = g_acquires;
    for (const KilnStr& name : bad)
        for (int as_field = 0; as_field < 2; ++as_field) {
            KilnMutationReply untouched{};
            untouched.size = sizeof untouched;
            untouched.status = 0xEEEEu;
            const KilnResult rc = as_field ? write_named(s, 2, before, 4, 9u, str("shroudforge:alpha"), name, untouched)
                                           : write_named(s, 2, before, 4, 9u, name, str("hp"), untouched);
            CHECK(rc == KILN_ERR_INVALID_ARGUMENT && untouched.status == 0xEEEEu);
        }
    CHECK(current(2) == before && g_acquires == acquires && g_commits == commits + 3);
    // UTF-8 beyond ASCII is text
    before = current(2);
    r = reply();
    CHECK(write_named(s, 2, before, 4, 1237u, str("shroudforge:caf\xc3\xa9"), str("\xe2\x82\xac.value"), r) == KILN_OK);
    CHECK(g_last_commit.writer == "shroudforge:caf\xc3\xa9");
    std::printf("PASS loaded.write-named (the record's names, before and after; unnamed; refused names touch nothing)\n");
}

ka::loaded::Commit commit_of(std::size_t entry, std::size_t offset, std::uint64_t before, std::uint64_t after, const char* writer,
                             KilnOwner owner = 7, const char* field = "hp") {
    ka::loaded::Commit c{};
    c.owner = owner;
    c.key = g_entries[entry].key;
    c.offset = offset;
    c.width = 4;
    c.session = 77;
    c.before = before;
    c.after = after;
    c.writer = writer;
    c.field = field;
    return c;
}
std::vector<KilnLoadedWriteRecord> read_all(const ka::loaded::WriteRecords& w) {
    std::size_t n = 0;
    CHECK(w.read(nullptr, sizeof(KilnLoadedWriteRecord), 0, &n) == (n ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK));
    std::vector<KilnLoadedWriteRecord> out(n);
    std::size_t got = 0;
    CHECK(w.read(out.data(), sizeof(KilnLoadedWriteRecord), out.size(), &got) == KILN_OK && got == n);
    return out;
}
std::uint32_t u32_of(const std::uint8_t b[8]) {
    std::uint32_t v = 0;
    std::memcpy(&v, b, 4);
    return v;
}

// One record per place: a run of the same owner and writer, each write over the value the one before left, keeps the value
// it began over; any other write there begins a new record (an unnamed one too). The place written longest ago goes first
// when the records are full. The copy: oldest first, record_size apart, its tail zeroed, all or nothing.
void test_write_records() {
    build();
    ka::loaded::WriteRecords w(4);
    CHECK(read_all(w).empty());
    const std::uint64_t first = w.commit(commit_of(2, 4, 10, 12, "shroudforge:alpha"));
    const std::uint64_t second = w.commit(commit_of(2, 4, 12, 13, "shroudforge:alpha"));
    CHECK(first != 0 && second > first);
    auto all = read_all(w);
    CHECK(all.size() == 1);
    if (all.size() == 1) {
        const KilnLoadedWriteRecord& r = all[0];
        CHECK(r.size == sizeof(KilnLoadedWriteRecord) && r.width == 4 && r.id == second && r.owner == 7 && r.session == 77);
        CHECK(std::memcmp(&r.key, &g_entries[2].key, sizeof r.key) == 0 && r.offset == 4);
        CHECK(u32_of(r.before) == 10 && u32_of(r.after) == 13 && std::string(r.writer) == "shroudforge:alpha" && std::string(r.field) == "hp");
        CHECK(std::all_of(r.before + 4, r.before + 8, [](std::uint8_t b) { return b == 0; }));
    }
    // another writer, then an unnamed write, then the first writer again over a value it did not leave: new records
    w.commit(commit_of(2, 4, 13, 20, "shroudforge:beta"));
    all = read_all(w);
    CHECK(all.size() == 1 && u32_of(all[0].before) == 13 && u32_of(all[0].after) == 20 && std::string(all[0].writer) == "shroudforge:beta");
    w.commit(commit_of(2, 4, 20, 21, ""));
    all = read_all(w);
    CHECK(all.size() == 1 && u32_of(all[0].before) == 20 && u32_of(all[0].after) == 21 && all[0].writer[0] == 0);
    w.commit(commit_of(2, 4, 21, 30, "shroudforge:alpha"));
    w.commit(commit_of(2, 4, 5, 31, "shroudforge:alpha"));  // not over 30: someone else wrote between
    all = read_all(w);
    CHECK(all.size() == 1 && u32_of(all[0].before) == 5 && u32_of(all[0].after) == 31);
    // the same writer name from another owner is another writer
    w.commit(commit_of(2, 4, 31, 32, "shroudforge:alpha", 8));
    all = read_all(w);
    CHECK(all.size() == 1 && u32_of(all[0].before) == 31 && all[0].owner == 8);
    // other places: another offset of the resource, other resources; the fifth place drops the one written longest ago
    w.commit(commit_of(2, 8, 1, 2, "shroudforge:alpha"));
    w.commit(commit_of(3, 4, 1, 2, "shroudforge:alpha"));
    w.commit(commit_of(4, 4, 1, 2, "shroudforge:alpha"));
    CHECK(read_all(w).size() == 4 && w.dropped() == 0);
    w.commit(commit_of(2, 4, 32, 33, "shroudforge:alpha", 8));  // the first place written again: now the newest
    w.commit(commit_of(5, 4, 1, 2, "shroudforge:gamma"));
    all = read_all(w);
    CHECK(all.size() == 4 && w.dropped() == 1);
    bool has_offset8 = false;
    for (const auto& r : all) has_offset8 = has_offset8 || (std::memcmp(&r.key, &g_entries[2].key, sizeof r.key) == 0 && r.offset == 8);
    CHECK(!has_offset8);  // (2, 8) was the place written longest ago
    for (std::size_t i = 1; i < all.size(); ++i) CHECK(all[i - 1].id < all[i].id);
    CHECK(std::string(all.back().writer) == "shroudforge:gamma");
    // the copy's arguments
    std::size_t n = 0;
    std::vector<KilnLoadedWriteRecord> one(1);
    std::memset(one.data(), 0xAB, sizeof one[0]);
    CHECK(w.read(one.data(), sizeof(KilnLoadedWriteRecord), 1, &n) == KILN_ERR_BUFFER_TOO_SMALL && n == 4);
    CHECK(reinterpret_cast<const unsigned char*>(one.data())[0] == 0xAB);  // nothing copied
    CHECK(w.read(one.data(), sizeof(KilnLoadedWriteRecord) - 1, 1, &n) == KILN_ERR_INVALID_ARGUMENT);
    CHECK(w.read(nullptr, sizeof(KilnLoadedWriteRecord), 4, &n) == KILN_ERR_INVALID_ARGUMENT);
    CHECK(w.read(one.data(), sizeof(KilnLoadedWriteRecord), 1, nullptr) == KILN_ERR_INVALID_ARGUMENT);
    const std::size_t wide = sizeof(KilnLoadedWriteRecord) + 40;
    std::vector<unsigned char> big(wide * 4, 0xCD);
    CHECK(w.read(reinterpret_cast<KilnLoadedWriteRecord*>(big.data()), static_cast<std::uint32_t>(wide), 4, &n) == KILN_OK && n == 4);
    for (std::size_t i = 0; i < 4; ++i) {
        KilnLoadedWriteRecord r;
        std::memcpy(&r, big.data() + i * wide, sizeof r);
        CHECK(r.size == sizeof(KilnLoadedWriteRecord) && r.id == all[i].id);
        CHECK(std::all_of(big.begin() + i * wide + sizeof r, big.begin() + (i + 1) * wide, [](unsigned char b) { return b == 0; }));
    }
    // the last 256 writes for status, with their names
    const auto recent = w.recent();
    CHECK(recent.size() == 12 && recent.back().writer == "shroudforge:gamma" && recent.back().field == "hp" && recent.back().offset == 4);
    ka::loaded::WriteRecords many(KILN_LOADED_WRITE_PLACES);
    for (int i = 0; i < 300; ++i) many.commit(commit_of(2, 4, static_cast<std::uint64_t>(i), static_cast<std::uint64_t>(i + 1), "x"));
    CHECK(many.recent().size() == 256 && read_all(many).size() == 1);
    std::printf("PASS loaded.write-records (runs, new records, owners, the oldest place dropped, the copy, recent)\n");
}
}  // namespace

int main(int argc, char** argv) {
    const bool concurrent_only = argc > 1 && std::strcmp(argv[1], "--concurrent") == 0;
    if (!concurrent_only) {
        test_hash();
        test_snapshot();
        test_not_ready();
        test_changed();
        test_served();
        test_list();
        test_write();
        test_write_refusals();
        test_write_named();
        test_write_records();
    }
    test_concurrent();
    test_write_concurrent();
    if (g_failures) {
        std::printf("FAIL loaded: %d check(s)\n", g_failures);
        return 1;
    }
    std::printf("PASS loaded (all)\n");
    return 0;
}
