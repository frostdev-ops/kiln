// SPDX-License-Identifier: MIT
#include <cassert>
#define KSET_LOADER_HOST_TEST
#include "../src/plugin.cpp"
namespace { int shutdowns = 0, ticks = 0, frames = 0, queued = 0; }
namespace rt {
void gameClosing() { ++shutdowns; }
void tick(uintptr_t) { ++ticks; }
void afterTick(uintptr_t) {} void worldLeft() {} void frame() { ++frames; }
void setOverlayOpen(bool) {} bool hasDraw() { return false; }
bool tryLockDraw() { return false; } void drawLocked() {}
void setSystemsAvailable(bool) {} void bindKiln(const KilnApi*, KilnOwner) {}
void configure(const std::string&, Sink) {} void setGameBuild(uint32_t) {} void queue(std::string) { ++queued; }
}
namespace hk { void setLog(Log) {} }
namespace kset {
std::string bind(const KilnApi*) { return {}; } bool systemsReady() { return false; }
void bindHooks(const KilnApi*, KilnOwner) {} void bindUi(const KilnUiApi*) {}
}
namespace {
// One client frame of a hosted game: the UI tick and a server step, 16 ms apart.
void frames_and_steps(int n) {
    for (int i = 0; i < n; ++i) {
        on_ui_tick(static_cast<uint64_t>(i), nullptr);
        on_pre(nullptr, nullptr);
        loader_test::now_ms += 16;
    }
}
}
int main(int argc, char** argv) {
    assert(argc == 2);
    assert(kiln_plugin_main(nullptr, 0, nullptr) == KILN_ERR_UNAVAILABLE);
    const std::filesystem::path root(argv[1]);
    std::filesystem::remove_all(root / "control");
    std::filesystem::create_directories(root / "System32");
    std::filesystem::create_directories(root / "relocated");
    std::ofstream(root / "System32/powrprof.dll") << "Windows power API";
    std::ofstream(root / "relocated/powrprof.dll") << "an unbranded proxy still fails closed";
    std::ofstream(root / "relocated/renamed.dll") << "Shrouded Engine Tweaks";
    std::ofstream(root / "relocated/framework.dll") << "Shrouded Engine Tweaks";
    loader_test::system = (root / "System32").wstring();
    const std::vector<std::wstring> clean = {(root / "System32/powrprof.dll").wstring(),
                                             (root / "relocated/framework.dll").wstring()};
    loader_test::modules = clean;
    KilnApi api{};
    api.log = [](KilnOwner, int32_t, KilnStr) {};
    api.server_world = []() -> uintptr_t { return 1; };
    g_api = &api;
    g_control = root / "control";
    // Startup, in on_ready's order: arm the DLL-load notification, then the one full check.
    assert(g_modules.arm() && g_modules.notifying() && loader_test::registrations == 1);
    assert(set_in_process().empty());
    g_running = true;
    // Steady state: 1000 UI frames and 1000 server steps never re-run the module check (it was once per frame/step).
    size_t base = loader_test::snapshots;
    frames_and_steps(1000);
    assert(loader_test::snapshots == base && ticks == 1000 && frames == 1000 && shutdowns == 0);
    // A DLL load makes exactly one re-check due.
    loader_test::dll_loaded();
    on_pre(nullptr, nullptr); assert(loader_test::snapshots == base + 1 && ticks == 1001);
    frames_and_steps(100); assert(loader_test::snapshots == base + 1 && ticks == 1101 && shutdowns == 0);
    // SET's proxy loading later is still caught at the next step after its load.
    loader_test::modules.push_back((root / "relocated/powrprof.dll").wstring());
    loader_test::dll_loaded();
    on_pre(nullptr, nullptr);
    assert(!g_running && ticks == 1101 && shutdowns == 1);
    on_pre(nullptr, nullptr); assert(shutdowns == 1 && ticks == 1101);
    loader_test::modules = {(root / "relocated/renamed.dll").wstring()};
    g_running = true;
    loader_test::dll_loaded();
    assert(!may_run() && shutdowns == 2);
    // Unload unregisters the notification (the loader must never call into an unmapped plugin).
    g_modules.disarm(); assert(loader_test::registrations == 0 && !g_modules.notifying());
    // Without LdrRegisterDllNotification: a time throttle (every 3 s), never per frame.
    loader_test::notifications = false;
    loader_test::modules = clean;
    assert(!g_modules.arm() && !g_modules.notifying());
    g_running = true;
    base = loader_test::snapshots;
    frames_and_steps(1000);  // 16 s
    const size_t checks = loader_test::snapshots - base;
    std::printf("fallback: %zu module checks in 1000 frames + 1000 steps over 16 s\n", checks);
    assert(checks >= 4 && checks <= 6 && g_running && shutdowns == 2);
    // ...and still steps aside within the interval once SET appears.
    loader_test::modules.push_back((root / "relocated/powrprof.dll").wstring());
    frames_and_steps(200);  // 3.2 s
    assert(!g_running && shutdowns == 3);
    // The console folder is checked by time (every 0.5 s), not every 12th frame.
    loader_test::modules = clean;
    g_running = true;
    std::filesystem::create_directories(root / "control/set");
    std::ofstream(root / "control/set/a.lua") << "print(1)\n";
    g_console_next = 0;
    on_ui_tick(0, nullptr);
    assert(queued == 1 && !std::filesystem::exists(root / "control/set/a.lua"));
    std::ofstream(root / "control/set/b.lua") << "print(2)\n";
    for (int i = 0; i < 100; ++i) { on_ui_tick(static_cast<uint64_t>(i), nullptr); on_pre(nullptr, nullptr); }
    assert(queued == 1 && std::filesystem::exists(root / "control/set/b.lua"));
    loader_test::now_ms += console_ms;
    on_pre(nullptr, nullptr);
    assert(queued == 2 && !std::filesystem::exists(root / "control/set/b.lua"));
    // Partial subscription failure must still close a configured runtime on plugin unload.
    g_initialized = true; g_running = false; on_unload(nullptr); assert(shutdowns == 4);
    std::puts("plugin coexistence/lifecycle/per-frame cost review: PASS");
}
