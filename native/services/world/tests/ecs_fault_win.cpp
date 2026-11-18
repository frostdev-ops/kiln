// SPDX-License-Identifier: MIT
// Windows test (headless Wine, tests/wine.sh; never shipped, never built by default): kiln.world's engine backend
// (ecs_engine.cpp) under the verified-copy logic, over the real Windows primitives of kiln.engine (memory_win.cpp: the
// SEH copy and the Interlocked words; resolve.cpp: the views) and a fake engine in VirtualAlloc'd memory laid out from
// the dedicated server's exact profile (fake_engine.h, ecs_fake_world.h). Pages under the reads are made PAGE_NOACCESS,
// PAGE_GUARD or decommitted: every read must come back refused (unreadable or unavailable) with the caller's buffer
// untouched, and the process must live on; with the pages back the same reads succeed. A thread flips a chunk page's
// protection while two readers read: no read crashes, none returns a partial buffer.
// Compare-exchange writes (ecs_writes.cpp) over the same primitives and kiln.engine's real write gate (gate.cpp, the
// Interlocked words, the SEH write): a write whose second page is read-only faults part-way and is rolled back and
// proven; a page that turns PAGE_NOACCESS or decommitted between the write and its readback leaves the write uncertain
// and latched, a PAGE_GUARD one faults once and the rollback is proven; a guard page under the first read refuses before any write; a simulated frame holding
// the Scene word is busy; a faulting event subscriber is contained by SEH. And the hold's time with real memory.
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <functional>
#include <memory>
#include <string>
#include <thread>
#include <tuple>
#include <vector>

#include "ecs_fake_world.h"
#include "include/kiln_engine_native.h"
#include "memory.h"
#include "profiles.g.h"
#include "resolve.h"

#include "../ecs_engine.h"
#include "../ecs_epochs.h"
#include "../ecs_writes.h"
#include "../transform_layout.h"
#include "engine_state.h"
#include "gate.h"

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

// kiln.internal.engine's gate members, the same bodies engine.cpp publishes, for a caller off the world threads.
using kiln::engine::EcsGate;
KilnResult KILN_CALL acquire_member(const kiln::engine::EcsView* expected, EcsGate* gate, std::uint32_t* reason) {
    const auto r = kiln::engine::gate_acquire(g_layout, *expected, *gate, kiln::engine::CallerThread::Other, g_image.load());
    *reason = static_cast<std::uint32_t>(r);
    return r == kiln::engine::Reason::None ? KILN_OK : KILN_ERR_UNAVAILABLE;
}
std::int32_t KILN_CALL validate_member(const EcsGate* gate) { return kiln::engine::gate_validate(g_layout, *gate, g_image.load()) ? 1 : 0; }
std::int32_t KILN_CALL component_member(const EcsGate* gate, std::uint16_t c) { return kiln::engine::gate_component_supported(*gate, c) ? 1 : 0; }
KilnResult KILN_CALL write_member(const EcsGate* gate, std::uintptr_t address, const void* data, std::size_t bytes) {
    switch (kiln::engine::gate_write(*gate, address, data, bytes)) {
        case kiln::engine::GateWrite::Ok: return KILN_OK;
        case kiln::engine::GateWrite::TooLarge: return KILN_ERR_BUFFER_TOO_SMALL;
        case kiln::engine::GateWrite::NotHeld: return KILN_ERR_INVALID_ARGUMENT;
        default: return KILN_ERR_UNAVAILABLE;
    }
}
void KILN_CALL release_member(EcsGate* gate) { kiln::engine::gate_release(*gate); }

// SEH containment of an event subscriber, as kiln.world's seh_call.
bool seh_guarded(void (*fn)(void*), void* ctx, unsigned long* code) {
    __try {
        fn(ctx);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
writes::HoldPoint g_spoil_at = writes::HoldPoint::Begin;
std::function<void()> g_spoil;
void hold_point(writes::HoldPoint p) {
    if (g_spoil && p == g_spoil_at) {
        auto f = std::move(g_spoil);
        g_spoil = nullptr;
        f();
    }
}
int g_delivered = 0;
void KILN_CALL faulting_subscriber(const KilnEcsWriteEvent*, void*) {
    *reinterpret_cast<volatile int*>(fake::unmapped()) = 1;  // an access violation
}
void KILN_CALL counting_subscriber(const KilnEcsWriteEvent*, void*) { ++g_delivered; }

}  // namespace

namespace kiln::engine {
Stats& stats() {
    static Stats s;
    return s;
}
}  // namespace kiln::engine
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

    // ---- compare-exchange writes over the real gate ----
    {
        native.ecs_gate_acquire = &acquire_member;
        native.ecs_gate_validate = &validate_member;
        native.ecs_gate_component_supported = &component_member;
        native.ecs_gate_write = &write_member;
        native.ecs_gate_release = &release_member;
        w.reg("Counter", 8);
        w.reg("Span", 256);
        w.reg("Largest", 4328);
        w.reg("Max", 65535);
        const uintptr_t counters = w.archetype({"keen::ecs::Counter"});
        w.add(7000, 2, counters, 0, nullptr);
        const uintptr_t span = w.archetype({"keen::ecs::Span"});
        put<uint16_t>(span + E.component_offsets + w.comp("keen::ecs::Span").index * 2, 4096 - 128);
        w.add(7001, 1, span, 0, nullptr);
        w.add(7002, 1, w.archetype({"keen::ecs::Largest"}), 0, nullptr);
        w.add(7003, 1, w.archetype({"keen::ecs::Max"}), 0, nullptr);
        // kiln.types: every one of them a static array of bytes (plain)
        static std::vector<std::pair<std::string, KilnTypeInfo>> infos;
        static KilnTypeInfo u8{};
        u8.size = sizeof u8; u8.kind = KILN_TYPE_U8; u8.type_size = 1;
        for (const char* n : {"Counter", "Span", "Largest", "Max"}) {
            KilnTypeInfo t{};
            t.size = sizeof t;
            t.kind = KILN_TYPE_STATIC_ARRAY;
            t.type_size = w.comp(std::string("keen::ecs::") + n).size;
            t.hash = H(w, n);
            t.inner = reinterpret_cast<const KilnType*>(&u8);
            infos.emplace_back(n, t);
        }
        static KilnTypesApi types{};
        types.header = {sizeof types, KILN_TYPES_VERSION};
        types.type_by_hash = [](uint32_t h) -> const KilnType* {
            for (auto& [n, t] : infos)
                if (t.hash == h) return reinterpret_cast<const KilnType*>(&t);
            return nullptr;
        };
        types.type_info = [](const KilnType* t, KilnTypeInfo* out) -> KilnResult {
            const uint32_t size = out->size;
            *out = *reinterpret_cast<const KilnTypeInfo*>(t);
            out->size = size;
            return KILN_OK;
        };
        types.type_field = [](const KilnType*, uint32_t, KilnTypeField*) -> KilnResult { return KILN_ERR_NOT_FOUND; };
        static engine::Reader* s_reader = &reader;
        static Worlds* s_worlds = &worlds;
        writes::WriterSource ws;
        ws.reader = [](void*) { return s_reader; };
        ws.engine = &native;
        ws.types = &types;
        ws.exact_profile = true;
        ws.server_step = [](void*) -> uint64_t { return s_worlds->steps; };
        ws.guarded = &seh_guarded;
        writes::Writer wr(ws);
        writes::g_hold_test_point = &hold_point;
        check(wr.available(), "writes: available over the real gate");
        auto open = [&](uint32_t id) {
            KilnObjectRef r{};
            entity_open(b, kOwner, kOwner, KILN_WORLD_SERVER, 0, id, &r);
            return r;
        };
        auto cx = [&](const KilnObjectRef& r, const char* name, const std::vector<uint8_t>& exp, const std::vector<uint8_t>& des,
                      KilnMutationReply& reply) {
            ++worlds.steps;
            reply = KilnMutationReply{};
            reply.size = sizeof reply;
            return wr.compare_exchange(kOwner, &r, H(w, name), exp.data(), nullptr, des.data(),
                                       static_cast<uint32_t>(exp.size()), nullptr, &reply);
        };
        auto bytes_of = [](uintptr_t a, size_t n) {
            std::vector<uint8_t> v(n);
            std::memcpy(v.data(), reinterpret_cast<const void*>(a), n);
            return v;
        };
        KilnMutationReply rep{};
        const KilnObjectRef counter = open(7000);
        const uintptr_t at = w.row_address(counters, 0, "keen::ecs::Counter");
        std::vector<uint8_t> zero(8, 0), one(8, 0);
        one[0] = 1;
        check(cx(counter, "Counter", zero, one, rep) == KILN_OK && (rep.flags & KILN_MUTATION_WRITTEN) && bytes_of(at, 8) == one,
              "writes: a write through the SEH write, read back");
        // a frame holds the Scene word Shared
        InterlockedIncrement(reinterpret_cast<volatile LONG*>(e.scene_word()));
        check(cx(counter, "Counter", one, zero, rep) == KILN_ECS_ERR_BUSY && bytes_of(at, 8) == one, "writes: a frame: busy");
        InterlockedDecrement(reinterpret_cast<volatile LONG*>(e.scene_word()));
        // Span crosses a page boundary: its second page read-only makes the write fault part-way
        const KilnObjectRef sp = open(7001);
        const uintptr_t sat = w.row_address(span, 0, "keen::ecs::Span");
        const uintptr_t page = (sat + 4095) & ~uintptr_t{4095};
        std::vector<uint8_t> cur(256), want(256);
        for (size_t i = 0; i < 256; ++i) { cur[i] = static_cast<uint8_t>(i); want[i] = static_cast<uint8_t>(~i); }
        std::memcpy(reinterpret_cast<void*>(sat), cur.data(), 256);
        DWORD old = 0;
        VirtualProtect(reinterpret_cast<void*>(page), 4096, PAGE_READONLY, &old);
        check(cx(sp, "Span", cur, want, rep) == KILN_ECS_ERR_UNREADABLE && rep.flags == KILN_MUTATION_ROLLED_BACK &&
                  bytes_of(sat, 256) == cur,
              "writes: a write faulting part-way (read-only page) is rolled back and proven");
        VirtualProtect(reinterpret_cast<void*>(page), 4096, PAGE_READWRITE, &old);
        // a guard page under the first read: refused before any write
        VirtualProtect(reinterpret_cast<void*>(page), 4096, PAGE_READWRITE | PAGE_GUARD, &old);
        check(cx(sp, "Span", cur, want, rep) == KILN_ECS_ERR_UNREADABLE && rep.flags == 0 && bytes_of(sat, 128) == std::vector<uint8_t>(cur.begin(), cur.begin() + 128),
              "writes: a guard page under the first read: unreadable, nothing written");
        VirtualProtect(reinterpret_cast<void*>(page), 4096, PAGE_READWRITE, &old);
        // the page turns unreadable between the write and its readback: uncertain, latched
        for (int how = 0; how < 3; ++how) {
            worlds.session += 1;  // each mode latches in a session of its own
            const KilnObjectRef r = open(7001);
            std::memcpy(reinterpret_cast<void*>(sat), cur.data(), 256);
            std::unique_ptr<Spoil> spoiled;
            g_spoil_at = writes::HoldPoint::AfterWrite;
            g_spoil = [&] { spoiled = std::make_unique<Spoil>(page, 4096, how); };
            const KilnResult rc = cx(r, "Span", cur, want, rep);
            spoiled.reset();
            const KilnResult again = cx(r, "Span", cur, want, rep);
            if (how == 1) {
                // A guard page faults once: the readback trips it, the rollback then writes and reads back cleanly.
                check(rc == KILN_ECS_ERR_UNREADABLE && again == KILN_OK, std::string("writes: the page ") + mode_name(how) +
                                                                            " after the write: one fault, rolled back and proven");
            } else {
                check(rc == KILN_ECS_ERR_UNCERTAIN && again == KILN_ECS_ERR_UNCERTAIN && (rep.flags & KILN_MUTATION_TARGET_LATCHED),
                      std::string("writes: the page ") + mode_name(how) + " after the write: uncertain, then latched");
            }
        }
        // a subscriber that faults (an access violation) is contained; the next one still gets the event
        wr.drain();
        KilnHandle h1 = 0, h2 = 0;
        wr.subscribe(kOwner, &faulting_subscriber, nullptr, &h1);
        wr.subscribe(kOwner, &counting_subscriber, nullptr, &h2);
        worlds.session += 1;
        const KilnObjectRef c2 = open(7000);
        std::memcpy(reinterpret_cast<void*>(at), zero.data(), 8);
        cx(c2, "Counter", zero, one, rep);
        const size_t drained = wr.drain();
        check(drained == 1 && g_delivered == 1 && wr.faults() == 1 && wr.unsubscribe(h1) == KILN_ERR_NOT_FOUND,
              "writes: an access violation in a subscriber is contained and ends it");
        wr.unsubscribe(h2);
        // the hold with real memory: the largest writable component of both builds (4328 bytes) and 65535 bytes
        for (const auto& [name, id, size] : {std::tuple<const char*, uint32_t, uint32_t>{"Largest", 7002, 4328},
                                             std::tuple<const char*, uint32_t, uint32_t>{"Max", 7003, 65535}}) {
            const KilnObjectRef r = open(id);
            std::vector<uint8_t> a(size, 0), n(size, 0);
            std::vector<uint64_t> times;
            for (int i = 0; i < 1000; ++i) {
                std::fill(n.begin(), n.end(), static_cast<uint8_t>(i + 1));
                if (cx(r, name, a, n, rep) != KILN_OK) break;
                times.push_back(rep.hold_ns);
                a = n;
                wr.drain();
            }
            std::sort(times.begin(), times.end());
            check(times.size() == 1000, std::string("writes: 1000 timed writes of ") + name);
            if (!times.empty())
                std::printf("  hold, %u bytes: median %.1f us, p99 %.1f us, worst %.1f us\n", size,
                            times[times.size() / 2] / 1000.0, times[times.size() * 99 / 100] / 1000.0, times.back() / 1000.0);
        }
        writes::g_hold_test_point = nullptr;
    }

    std::printf("ecs_fault_win: %d checks, %d failures\n", checks, failures);
    return failures ? 1 : 0;
}
