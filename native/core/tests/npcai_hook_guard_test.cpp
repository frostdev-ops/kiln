// SPDX-License-Identifier: MIT
// The actual npcai lifecycle with fake kiln.hooks links (services/npcai/tests/stubs/kiln_hook.hpp). Since dev.21 the
// hooks are kiln.hooks v2 links: turning them off revokes the link and never touches the game's bytes, so a foreign
// hook over Kiln's detour can no longer be stripped (the old HookGuard checks are gone with the unhooking).
#include "../../services/npcai/npcai.cpp"
#include <cassert>

int main() {
    KilnApi api{};
    int warnings = 0;
    static int* warning_count;
    warning_count = &warnings;
    api.log = [](KilnOwner, int32_t level, KilnStr) { if (level == KILN_LOG_WARN) ++*warning_count; };
    api.server_world = [] { return uintptr_t{0x1000}; };
    g_api = &api;
    std::array<std::array<uint8_t, 32>, 5> code{};
    for (size_t i = 0; i < code.size(); ++i) {
        code[i][0] = 0xE9;
        g_at[i] = reinterpret_cast<uintptr_t>(code[i].data());
    }
    g_resolved = 31;
    const auto before = code;
    assert(prepare() && install() && g_installed == 31 && g_answering);
    for (auto& h : g_h) assert(h);
    uninstall();
    assert(g_installed == 0 && !g_answering && warnings == 0);
    for (auto& h : g_h) assert(!h && h.original<void*>());  // revoked; the continuation stays callable

    const int revokes = kiln::test::revokes;
    assert(install() && g_answering);  // links come back on the next relation
    for (auto& c : code) ++c[1];       // a foreign hook over Kiln's detour: nothing of ours reads or restores it
    uninstall();
    uninstall();                       // idempotent
    assert(g_installed == 0 && warnings == 0 && kiln::test::revokes == revokes + 5);
    for (auto& c : code) --c[1];
    assert(code == before);

    std::array<uint8_t, 32> observer{};
    g_destroy_at = reinterpret_cast<uintptr_t>(observer.data());
    KilnWorldApi world{};
    g_world = &world;
    g_faction_hash = 1;
    g_lifecycle = true;
    g_storage_offset = 0;
    assert(entity_support() && g_destroy);
    assert(entity_support() && g_destroy);  // already linked: no second link
    uninstall();
    assert(!g_destroy && warnings == 0);
    kiln::test::fail_target = g_destroy_at;
    const int attempts = kiln::test::attempts;
    assert(!entity_support() && !g_destroy && warnings == 1);  // refused once: one warning
    assert(kiln::test::attempts == attempts + 1);
    // entity_support() runs per tracked creature per tick: a refusal is final for that target, no install() again
    for (int i = 0; i < 100; ++i) assert(!entity_support());
    assert(kiln::test::attempts == attempts + 1 && warnings == 1);
    kiln::test::fail_target = 0;
    assert(!entity_support() && kiln::test::attempts == attempts + 1);  // the same target stays refused
    uninstall();                                                        // so does a world stop or idle spell
    assert(!entity_support() && kiln::test::attempts == attempts + 1);
    std::array<uint8_t, 32> moved{};  // another target (resolved again): a retry is sound
    g_destroy_at = reinterpret_cast<uintptr_t>(moved.data());
    assert(entity_support() && g_destroy && kiln::test::attempts == attempts + 2);
    std::puts("PASS npcai: links revoked (never unhooked), re-added on demand, lifetime observer linked once, a refusal is not retried");
}
