// SPDX-License-Identifier: MIT
// Windows test (headless Wine, tests/wine.sh; never shipped, never built by default): kiln.world's engine backend
// (ecs_engine.cpp) under the verified-copy logic, over the real Windows primitives of kiln.engine (memory_win.cpp: the
// SEH copy and the Interlocked words; resolve.cpp: the views) and a fake engine in VirtualAlloc'd memory laid out from
// the dedicated server's exact profile (fake_engine.h, ecs_fake_world.h). Pages under the reads are made PAGE_NOACCESS,
// PAGE_GUARD or decommitted: every read must come back refused (unreadable or unavailable) with the caller's buffer
// untouched, and the process must live on; with the pages back the same reads succeed. A thread flips a chunk page's
// protection while two readers read: no read crashes, none returns a partial buffer.
#include <windows.h>

#include <atomic>
#include <cstdio>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

#include "ecs_fake_world.h"
#include "include/kiln_engine_native.h"
#include "memory.h"
#include "profiles.g.h"
#include "resolve.h"

#include "../ecs_engine.h"
#include "../ecs_epochs.h"
#include "../transform_layout.h"

namespace {
using namespace fakeworld;
using namespace kw::ecs;
std::atomic<std::uintptr_t> g_image{0};
const kiln::engine::Layout* g_layout = nullptr;
int checks = 0, failures = 0;
void check(bool ok, const std::string& what) {
    ++checks;
    if (!ok) { ++failures; std::printf("FAIL %s\n", what.c_str()); }
}
bool all_eq(const std::vector<uint8_t>& v, uint8_t b) { for (auto x : v) if (x != b) return false; return true; }

// kiln.internal.engine's read members, the same bodies engine.cpp publishes (copy, ecs_view), over the real primitives.
const kiln::engine::Layout* KILN_CALL layout_member() { return g_layout; }
KilnResult KILN_CALL copy_member(std::uintptr_t address, void* out, std::size_t bytes) {
    if (!bytes) return KILN_OK;
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    return kiln::engine::mem::read_span(address, out, bytes) ? KILN_OK : KILN_ERR_UNAVAILABLE;
}
KilnResult KILN_CALL view_member(std::uint32_t kind, kiln::engine::EcsView* out, std::uint32_t* reason) {
    kiln::engine::EcsView v{};
    const auto r = kiln::engine::ecs_view(g_layout, static_cast<kiln::engine::ViewKind>(kind), v, g_image.load());
    *reason = static_cast<std::uint32_t>(r);
    if (r != kiln::engine::Reason::None) return KILN_ERR_UNAVAILABLE;
    *out = v;
    return KILN_OK;
}

struct Worlds {
    uintptr_t world = 0;
    uint64_t session = 77, steps = 1;
    OwnerEpochs epochs;
};
uint32_t world_fact(void* u, uint32_t kind, engine::WorldFact* out) {
    auto& w = *static_cast<Worlds*>(u);
    out->world = kind == KILN_WORLD_SERVER ? w.world : 0;
    out->session = kind == KILN_WORLD_SERVER ? w.session : 0;
    return KILN_ECS_OK;
}
uint64_t epoch_of(void* u, KilnOwner o) { return static_cast<Worlds*>(u)->epochs.current(o); }
uint64_t open_epoch(void* u, KilnOwner o, uint64_t e) { return static_cast<Worlds*>(u)->epochs.open(o, e); }
bool steps_of(void* u, uint32_t, uint64_t* out) { *out = static_cast<Worlds*>(u)->steps; return true; }

// One page (or more) of a fake region made unreadable in one of three ways, and put back.
struct Spoil {
    void* at;
    size_t size;
    DWORD old = 0;
    int how;  // 0 PAGE_NOACCESS, 1 PAGE_GUARD, 2 decommitted
    Spoil(uintptr_t address, size_t bytes, int mode) : at(reinterpret_cast<void*>(address & ~uintptr_t{4095})),
                                                       size((bytes + 4095) & ~size_t{4095}), how(mode) {
        if (how == 2) {
            std::vector<uint8_t> keep(size);
            std::memcpy(keep.data(), at, size);
            saved = keep;
            VirtualFree(at, size, MEM_DECOMMIT);
        } else {
            VirtualProtect(at, size, how == 0 ? PAGE_NOACCESS : (PAGE_READWRITE | PAGE_GUARD), &old);
        }
    }
    ~Spoil() {
        if (how == 2) {
            VirtualAlloc(at, size, MEM_COMMIT, PAGE_READWRITE);
            std::memcpy(at, saved.data(), size);
        } else {
            DWORD ignored = 0;
            VirtualProtect(at, size, PAGE_READWRITE, &ignored);
        }
    }
    std::vector<uint8_t> saved;
};
const char* mode_name(int how) { return how == 0 ? "PAGE_NOACCESS" : how == 1 ? "PAGE_GUARD" : "decommitted"; }

}  // namespace

namespace kiln::engine::mem {
std::uintptr_t image_base() { return g_image.load(); }
}  // namespace kiln::engine::mem

int main() {
    const kiln::engine::Profile* server = nullptr;
    for (const auto& p : kiln::engine::generated::kProfiles)
        if (p.layout.dedicated_server) server = &p;
    if (!server) { std::printf("FAIL no dedicated-server profile\n"); return 1; }
    g_layout = &server->layout;
    fake::Engine e(server->layout);
    g_image = e.image.at();
    World w(server->layout, e.image, e.registry_owner(), e.manager());
    populate(w);

    KilnEngineNativeApi native{};
    native.header = {sizeof native, KILN_ENGINE_NATIVE_VERSION};
    native.layout_size = sizeof(kiln::engine::Layout);
    native.profile_size = sizeof(kiln::engine::Profile);
    native.layout = &layout_member;
    native.copy = &copy_member;
    native.ecs_view = &view_member;
    Worlds worlds;
    worlds.world = e.exec.at();
    engine::Source s;
    s.engine = &native;
    s.layout = g_layout;
    s.exact_profile = true;
    s.reads = engine::layout_refusal(g_layout) == nullptr;
    s.user = &worlds;
    s.world = &world_fact;
    s.epoch = &epoch_of;
    s.open_epoch = &open_epoch;
    s.steps = &steps_of;
    engine::Reader reader(s);
    Backend b = reader.backend();
    constexpr KilnOwner kOwner = 5;

    KilnObjectRef chest{}, player{};
    check(entity_open(b, kOwner, 1, KILN_WORLD_SERVER, 0, 4097, &chest) == KILN_OK &&
              entity_open(b, kOwner, 1, KILN_WORLD_SERVER, 0, 1, &player) == KILN_OK,
          "the reads work over the real SEH copy");
    const uint32_t inventory = H(w, "Inventory");
    std::vector<uint8_t> buf(48, 0xAA);
    uint32_t req = 0;
    check(ecs_snapshot(b, kOwner, &chest, inventory, buf.data(), 48, &req) == KILN_OK && buf[0] == (1 ^ 0x80),
          "the chest's Inventory read");

    const auto& E = server->layout.entities;
    const uintptr_t rec = w.rec_of[4097];
    const uintptr_t lay = get<uint64_t>(rec + E.entity_layout);
    struct Case { const char* what; uintptr_t at; size_t size; };
    const Case cases[] = {
        {"the entity records", w.recs.at(), w.recs.size},
        {"the component chunk", w.chunk_of(lay), kChunk},
        {"the occupancy bits", w.occ.at(), w.occ.size},
        {"the keys", w.keys.at(), w.keys.size},
        {"the record slots", w.slots.at(), w.slots.size},
        {"the archetype layout", lay, kLayout},
        {"the registration rows", w.catalog.at(), w.catalog.size},
        {"the template definitions", w.defs.at(), w.defs.size},
    };
    for (const Case& c : cases)
        for (int how = 0; how < 3; ++how) {
            const std::string name = std::string(c.what) + " " + mode_name(how);
            {
                Spoil spoil(c.at, c.size, how);
                buf.assign(48, 0xAA);
                const KilnResult rc = ecs_snapshot(b, kOwner, &chest, inventory, buf.data(), 48, &req);
                check(rc != KILN_OK && all_eq(buf, 0xAA), name + ": the snapshot is refused (" + std::to_string(rc) +
                                                              ") and the caller's buffer untouched");
                uint8_t guid[16];
                std::memset(guid, 0xAA, sizeof guid);
                const KilnResult grc = ecs_template_uuid(b, kOwner, &chest, guid);
                bool untouched = true;
                for (uint8_t g : guid) untouched = untouched && g == 0xAA;
                check(grc == KILN_OK || untouched, name + ": a refused template read writes nothing");
                KilnEcsQueryRequest q{}; q.size = sizeof q; q.world_kind = KILN_WORLD_SERVER;
                q.components = &inventory; q.component_count = 1;
                KilnObjectRef refs[8];
                std::memset(refs, 0xAB, sizeof refs);
                uint32_t state = 0;
                const KilnResult qrc = ecs_query(b, kOwner, &q, refs, 8, &req, &state);
                check(qrc == KILN_OK || refs[0].size == 0xABABABABu, name + ": a refused query copies nothing");
            }
            buf.assign(48, 0xAA);
            check(ecs_snapshot(b, kOwner, &chest, inventory, buf.data(), 48, &req) == KILN_OK && buf[0] == (1 ^ 0x80),
                  name + ": with the pages back the read succeeds");
        }

    // Frame-quiet: the Scene word's page unreadable is a refusal too, never a fault.
    {
        Spoil spoil(e.scene_word(), 4, 0);
        KilnEcsSnapshotSpec quiet[1] = {{inventory, KILN_ECS_READ_FRAME_QUIET}};
        uint32_t presence[1] = {9};
        buf.assign(48, 0xAA);
        const KilnResult rc = ecs_snapshot_many(b, kOwner, &chest, quiet, 1, buf.data(), 48, &req, presence);
        check(rc != KILN_OK && all_eq(buf, 0xAA), "frame-quiet with the Scene word unreadable: refused");
    }
    {
        KilnEcsSnapshotSpec quiet[1] = {{inventory, KILN_ECS_READ_FRAME_QUIET}};
        uint32_t presence[1] = {9};
        check(ecs_snapshot_many(b, kOwner, &chest, quiet, 1, buf.data(), 48, &req, presence) == KILN_OK,
              "frame-quiet with the word back: read");
    }

    // A page that flips under two readers: no crash, never a partial buffer.
    std::atomic<bool> stop{false};
    std::atomic<int> ok{0}, refused{0}, partial{0};
    void* page = reinterpret_cast<void*>(w.chunk_of(lay) & ~uintptr_t{4095});
    std::thread flipper([&] {
        DWORD old = 0;
        while (!stop.load()) {
            VirtualProtect(page, 4096 * 2, PAGE_NOACCESS, &old);
            std::this_thread::yield();
            VirtualProtect(page, 4096 * 2, PAGE_READWRITE, &old);
            std::this_thread::yield();
        }
    });
    std::vector<std::thread> readers;
    for (int t = 0; t < 2; ++t)
        readers.emplace_back([&] {
            std::vector<uint8_t> mine(48);
            for (int i = 0; i < 2000; ++i) {
                std::memset(mine.data(), 0xAA, mine.size());
                uint32_t r = 0;
                if (ecs_snapshot(b, kOwner, &chest, inventory, mine.data(), 48, &r) == KILN_OK) {
                    ++ok;
                } else {
                    ++refused;
                    if (!all_eq(mine, 0xAA)) ++partial;
                }
            }
        });
    for (auto& r : readers) r.join();
    stop = true;
    flipper.join();
    DWORD old = 0;
    VirtualProtect(page, 4096 * 2, PAGE_READWRITE, &old);
    std::printf("  flipping page: %d read, %d refused\n", ok.load(), refused.load());
    check(partial.load() == 0 && ok.load() + refused.load() == 4000, "a page flipping under two readers: no partial buffer");
    check(refused.load() > 0, "the flipping page faulted some copies (contained)");

    std::printf("ecs_fault_win: %d checks, %d failures\n", checks, failures);
    return failures ? 1 : 0;
}
