// SPDX-License-Identifier: MIT
// Exercise the production writer and world reset against synthetic attribute storage, without game data or Wine.
#include <cassert>
#include <cstdint>
#include <iostream>
#include "../flame.cpp"

namespace kiln::knowledge {
const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
void logf(int, const char*, ...) {}
}

int main() {
    using namespace kiln::knowledge;
    struct Attribute { uint32_t root; uint16_t offset; uint8_t flags, count; uint32_t value, cap; } level{7, 8, 1, 2, 3, 20};
    alignas(8) static uint8_t info[16]{};
    std::memcpy(info, &level.root, 4);
    g_find = [](void* out, uint32_t, void*) -> void* {
        auto* p = static_cast<uint8_t*>(out);
        p[0] = 0;
        *reinterpret_cast<void**>(p + 8) = info;
        return out;
    };
    int recomputed = 0, marked = 0;
    static int* recomputed_p = &recomputed;
    static int* marked_p = &marked;
    g_recompute = [](void* component, void*) {
        ++*recomputed_p;
        auto* a = static_cast<Attribute*>(component);
        if (a->value > a->cap) a->value = a->cap;
    };
    KilnWorldApi world{};
    world.mark_written = [](KilnProgramCtx*, const uint64_t*) { ++*marked_p; };
    W = &world;
    s_level = 0;
    uint64_t iter[6]{reinterpret_cast<uint64_t>(&level), 0, 0, 0, 0, 0};
    int registry = 0;

    // A component without a tracker must stay untouched, and must not claim a persistent write succeeded.
    apply_one(nullptr, iter, &level, &registry, Pending{1, 4}, 1);
    KilnFlameResult r{}; r.size = sizeof r;
    assert(flame_result(1, &r) == KILN_OK && r.status == KILN_ERR_UNAVAILABLE);
    assert(level.value == 3 && marked == 0 && recomputed == 0 && !g_await.active);

    // The real writer marks first, runs the game's formula, and remembers the resulting (clamped) target.
    iter[5] = 1;
    apply_one(nullptr, iter, &level, &registry, Pending{2, 250}, 2);
    assert(level.value == 20 && marked == 1 && recomputed == 1);
    assert(g_await.active && g_await.res.marked && g_await.res.clamped && g_await.target == 20);
    finish_await(true, 1, nullptr);
    r.size = sizeof r;
    assert(flame_result(2, &r) == KILN_OK && r.status == KILN_OK && r.mirrored && r.after == 20);

    // A mirror that was never observed in this world cannot be completed by the next world's equal value.
    apply_one(nullptr, iter, &level, &registry, Pending{3, 4}, 3);
    g_pending.push_back({4, 6});
    g_pending_n = 1;
    KilnWorldEvent ended{sizeof(KilnWorldEvent), KILN_WORLD_STOPPING, KILN_WORLD_SERVER, 1, 0};
    flame_world(&ended, nullptr);
    assert(!g_await.active && !g_awaiting && g_pending.empty() && g_pending_n == 0);
    r.size = sizeof r;
    assert(flame_result(3, &r) == KILN_OK && r.status == KILN_ERR_UNAVAILABLE && !r.mirrored);
    assert(std::strstr(r.reason, "world ended"));
    r.size = sizeof r;
    assert(flame_result(4, &r) == KILN_OK && r.status == KILN_ERR_UNAVAILABLE);

    // A failing game formula is a failure, never a successful result with invented readback.
    const auto old = level;
    g_recompute = [](void* component, void*) {
        static_cast<Attribute*>(component)->cap = 2;  // partial formula work before its fault
        throw 1;
    };
    apply_one(nullptr, iter, &level, &registry, Pending{5, 250}, 5);
    r.size = sizeof r;
    assert(flame_result(5, &r) == KILN_OK && r.status == KILN_ERR_INTERNAL && !r.mirrored);
    assert(level.value == old.value && level.cap == old.cap);
    assert(std::strstr(r.reason, "restored"));

    // If guarded restoration itself fails, the final result explicitly says state may have changed.
    level.value = 250; level.cap = 2;
    KilnFlameResult failed{}; failed.size = sizeof failed; failed.request = 6; failed.marked = 1;
    fail_apply(failed, &level.value, old.value, old.cap, "injected fault", [](void*, uint32_t) { return false; });
    r.size = sizeof r;
    assert(flame_result(6, &r) == KILN_OK && r.status == KILN_ERR_INTERNAL);
    assert(level.value == 250 && level.cap == 2 && std::strstr(r.reason, "ROLLBACK FAILED"));
    level = old;

    // One active plus eight queued writes take 9 * (180 + 2) updates without a network mirror. They remain work until each
    // final native result; callers cannot use a shorter independent deadline and claim the later writes were lost.
    g_recompute = [](void*, void*) {};
    g_ok = true; g_updates = 0; s_nsp = 6; s_reg = 7;
    uint16_t network = 0;
    uint64_t queue_iter[8]{reinterpret_cast<uint64_t>(&level), 0, 0, 0, 0, 1,
                           reinterpret_cast<uint64_t>(&network), reinterpret_cast<uint64_t>(&registry)};
    for (uint64_t id = 10; id < 18; ++id) g_pending.push_back({id, static_cast<uint32_t>(id - 9)});
    g_pending_n = 8;
    flame_each(nullptr, queue_iter, 7, nullptr);  // one active, seven waiting
    g_pending.push_back({18, 9}); g_pending_n = 8;
    static_assert(KILN_FLAME_BATCH_UPDATES == 1638);
    for (uint64_t update = 1; update <= KILN_FLAME_BATCH_UPDATES; ++update) {
        if (update > 1) flame_each(nullptr, queue_iter, 7, nullptr);
        assert(level.value == (update - 1) / (kMirrorSteps + 2) + 1);  // later queued requests really mutate later
        for (uint64_t id = 10; id < 19; ++id) {
            r.size = sizeof r;
            const auto result = flame_result(id, &r);
            const bool done = update >= (id - 9) * (kMirrorSteps + 2);
            assert((result == KILN_OK) == done);
            if (done) assert(r.status == KILN_OK && !r.mirrored);
        }
    }
    assert(g_pending.empty() && !g_await.active);
    std::cout << "flame host: PASS tracker refusal, marked/clamped write, mirror, world cancellation, verified rollback, explicit rollback failure, serialized queue bound\n";
}
