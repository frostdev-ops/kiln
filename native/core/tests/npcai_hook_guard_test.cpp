// SPDX-License-Identifier: MIT
// The actual npcai lifecycle with fake hook objects and explicit, writable code footprints.
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
    assert(prepare() && install() && g_installed == 31 && g_answering);
    uninstall();
    assert(g_installed == 0 && !g_answering && warnings == 0);
    for (auto& h : g_h) assert(!h.enabled());

    assert(install());
    for (auto& c : code) ++c[1];  // a foreign E9 over every Kiln hook, including the same opcode
    uninstall();
    assert(g_installed == 31 && !g_answering && warnings == 5);
    for (auto& h : g_h) assert(h.enabled());
    uninstall();
    assert(warnings == 5);  // no repeated warnings or recaptured foreign footprints
    assert(install() && g_answering);  // all hooks retained: answers must resume on the fast path too
    uninstall();
    assert(!g_answering && warnings == 5);
    for (auto& c : code) --c[1];  // the foreign engine unhooks, restoring Kiln's jump
    uninstall();
    assert(g_installed == 0 && warnings == 5);

    std::array<uint8_t, 32> observer{};
    observer[0] = 0xE9;
    g_destroy_at = reinterpret_cast<uintptr_t>(observer.data());
    g_destroy = *safetyhook::MidHook::create(nullptr, nullptr, safetyhook::MidHook::StartDisabled);
    KilnWorldApi world{};
    g_world = &world;
    g_faction_hash = 1;
    g_lifecycle = true;
    g_storage_offset = 0;
    assert(entity_support() && g_destroy.enabled());
    uninstall();
    assert(!g_destroy.enabled() && warnings == 5);
    assert(entity_support());
    ++observer[1];
    uninstall();
    uninstall();
    assert(g_destroy.enabled() && warnings == 6);
    assert(entity_support());  // do not adopt a covering jump as our own
    uninstall();
    assert(g_destroy.enabled() && warnings == 6);
    --observer[1];
    uninstall();
    assert(!g_destroy.enabled() && warnings == 6);
    std::puts("PASS npcai: normal disable, covered inline/mid hooks, one warning per hook, and resuming answers");
}
