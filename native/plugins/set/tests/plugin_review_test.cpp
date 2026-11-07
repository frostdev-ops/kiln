// SPDX-License-Identifier: MIT
#include <cassert>
#define KSET_LOADER_HOST_TEST
#include "../src/plugin.cpp"
namespace { int shutdowns = 0, ticks = 0; }
namespace rt {
void gameClosing() { ++shutdowns; }
void tick(uintptr_t) { ++ticks; }
void afterTick(uintptr_t) {} void worldLeft() {} void frame() {}
void setOverlayOpen(bool) {} bool hasDraw() { return false; }
bool tryLockDraw() { return false; } void drawLocked() {}
void setSystemsAvailable(bool) {} void bindKiln(const KilnApi*, KilnOwner) {}
void configure(const std::string&, Sink) {} void setGameBuild(uint32_t) {} void queue(std::string) {}
}
namespace hk { void setLog(Log) {} }
namespace kset {
std::string bind(const KilnApi*) { return {}; } bool systemsReady() { return false; }
void bindHooks(const KilnApi*, KilnOwner) {} void bindUi(const KilnUiApi*) {}
}
int main(int argc, char** argv) {
    assert(argc == 2);
    assert(kiln_plugin_main(nullptr, 0, nullptr) == KILN_ERR_UNAVAILABLE);
    const std::filesystem::path root(argv[1]);
    std::filesystem::create_directories(root / "System32");
    std::filesystem::create_directories(root / "relocated");
    std::ofstream(root / "System32/powrprof.dll") << "Windows power API";
    std::ofstream(root / "relocated/powrprof.dll") << "an unbranded proxy still fails closed";
    std::ofstream(root / "relocated/renamed.dll") << "Shrouded Engine Tweaks";
    std::ofstream(root / "relocated/framework.dll") << "Shrouded Engine Tweaks";
    loader_test::system = (root / "System32").wstring();
    loader_test::modules = {(root / "System32/powrprof.dll").wstring()};
    loader_test::modules.push_back((root / "relocated/framework.dll").wstring());
    KilnApi api{};
    api.log = [](KilnOwner, int32_t, KilnStr) {};
    api.server_world = []() -> uintptr_t { return 1; };
    g_api = &api; g_running = true;
    assert(may_run());
    on_pre(nullptr, nullptr); assert(ticks == 1 && shutdowns == 0);
    loader_test::modules.push_back((root / "relocated/powrprof.dll").wstring());
    on_pre(nullptr, nullptr);
    assert(!g_running && ticks == 1 && shutdowns == 1);
    on_pre(nullptr, nullptr); assert(shutdowns == 1 && ticks == 1);
    loader_test::modules = {(root / "relocated/renamed.dll").wstring()};
    g_running = true;
    assert(!may_run() && shutdowns == 2);
    // Partial subscription failure must still close a configured runtime on plugin unload.
    g_initialized = true; g_running = false; on_unload(nullptr); assert(shutdowns == 3);
    std::puts("plugin coexistence/lifecycle review: PASS");
}
