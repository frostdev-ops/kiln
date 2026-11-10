// SPDX-License-Identifier: MIT
#include <cassert>
#include <stdexcept>
#include <thread>
#include <array>
#define KSET_LOADER_HOST_TEST
#include "../src/plugin.cpp"
namespace { int shutdowns = 0, ticks = 0, frames = 0, queued = 0; bool throw_queue = false; }
namespace rt {
void gameClosing() { ++shutdowns; }
void tick(uintptr_t) { ++ticks; }
void afterTick(uintptr_t) {} void worldLeft() {} void frame() { ++frames; }
void setOverlayOpen(bool) {} bool hasDraw() { return false; }
bool tryLockDraw() { return false; } void drawLocked() {}
void setSystemsAvailable(bool) {} void bindKiln(const KilnApi*, KilnOwner) {}
void configure(const std::string&, Sink) {} void setGameBuild(uint32_t) {}
std::vector<ModReport> reports; void (*reported)() = nullptr;
std::vector<ModReport> modReports() { return reports; } void onModsReported(void (*fn)()) { reported = fn; }
void queue(std::string) { if (throw_queue) throw std::runtime_error("queue failed"); ++queued; }
}
namespace hk { void setLog(Log) {} }
namespace kset {
std::string bind(const KilnApi*) { return {}; } bool systemsReady() { return false; }
void bindHooks(const KilnApi*, KilnOwner) {} void bindUi(const KilnUiApi*) {}
}
namespace {
// One client frame of a hosted game: the UI tick and a server step, 16 ms apart.
void frames_and_steps(int n, uint64_t elapsed_ms = 16) {
    for (int i = 0; i < n; ++i) {
        on_ui_tick(static_cast<uint64_t>(i), nullptr);
        on_pre(nullptr, nullptr);
        loader_test::now_ms += elapsed_ms;
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
    assert(g_modules.arm() && g_modules.notifying() && g_modules.fingerprinting() && loader_test::registrations == 1);
    assert(set_in_process().empty());
    g_running = true;
    // Steady state: 1000 UI frames and 1000 server steps over 16 s never re-run the module check (it was once per
    // frame/step), not even at the 3 s safety rounds: those only fingerprint the PEB list, which has not changed.
    size_t base = loader_test::snapshots;
    const size_t probes = loader_test::lock_tries;
    frames_and_steps(1000);
    std::printf("steady state: %zu module checks, %zu fingerprint rounds in 1000 frames + 1000 steps over 16 s\n",
                loader_test::snapshots - base, loader_test::lock_tries - probes);
    assert(loader_test::snapshots == base && ticks == 1000 && frames == 1000 && shutdowns == 0);
    assert(loader_test::lock_tries - probes == 5);
    // A silent load (in the PEB list, no notification) is caught at the next safety round, by one full check.
    loader_test::modules.push_back((root / "relocated/silent.dll").wstring());
    frames_and_steps(200);  // 3.2 s
    assert(loader_test::snapshots == base + 1 && shutdowns == 0);
    frames_and_steps(400);  // 6.4 s more, nothing new
    assert(loader_test::snapshots == base + 1);
    loader_test::modules = clean;  // an unload is a list change too: one more (harmless) check
    frames_and_steps(200);
    assert(loader_test::snapshots == base + 2);
    base = loader_test::snapshots;
    ticks = 1000;  // as if the silent-load rounds above had not stepped
    // A DLL load makes exactly one re-check due.
    loader_test::dll_loaded();
    on_pre(nullptr, nullptr); assert(loader_test::snapshots == base + 1 && ticks == 1001);
    frames_and_steps(100, 0); assert(loader_test::snapshots == base + 1 && ticks == 1101 && shutdowns == 0);
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
    // Without any ntdll loader API (no notification, no loader lock): a time throttle (every 3 s), never per frame.
    loader_test::notifications = false;
    loader_test::modules = clean;
    assert(!g_modules.arm() && !g_modules.notifying() && !g_modules.fingerprinting());
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
    int failures = 0;
    auto check = [&](bool ok, const char* name) {
        std::printf("%s: %s\n", ok ? "ok" : "FAIL", name);
        failures += !ok;
    };
    loader_test::notifications = true;
    loader_test::load_during_register = true;
    {
        kset::ModuleWatch watch;
        check(watch.arm(), "edge cases register");
        loader_test::load_during_register = false;
        check(watch.due() && !watch.due(), "a load while arming survives registration");
        // A load during a check must still be pending after that check finishes.
        loader_test::modules = clean;
        loader_test::dll_loaded();
        check(watch.due(), "consume before the inventory check");
        loader_test::on_snapshot = [] { loader_test::dll_loaded(); };
        set_in_process();
        loader_test::on_snapshot = {};
        check(watch.due() && !watch.due(), "a load during the check is not lost");
        // Only one consumer may claim an event, even with UI and tick threads racing.
        loader_test::dll_loaded();
        std::atomic<int> claims{0};
        std::array<std::thread, 8> consumers;
        for (auto& t : consumers) t = std::thread([&] { if (watch.due()) ++claims; });
        for (auto& t : consumers) t.join();
        check(claims == 1, "one concurrent due caller claims a notification");
        // Wine's no-resolve load path can add a module without a load notification: the fingerprint catches it.
        loader_test::modules.push_back((root / "relocated/powrprof.dll").wstring());
        check(!watch.due(), "a silent load waits for the safety round");
        loader_test::now_ms += kset::ModuleWatch::safety_ms;
        check(watch.due() && !set_in_process().empty(), "silent loads get a bounded safety re-check while registered");
        int safety_checks = 0;
        const size_t rounds = loader_test::lock_tries;
        for (int i = 0; i < 1000; ++i) {
            safety_checks += watch.due();
            safety_checks += watch.due();  // UI and tick share one deadline
            loader_test::now_ms += 16;
        }
        check(safety_checks == 0 && loader_test::lock_tries - rounds == 5,
              "steady state: 5 fingerprint rounds, no full check over 1000 frames and steps / 16 s");
        loader_test::now_ms += 60000;
        check(!watch.due(), "resuming after a pause: no change, no check");
        loader_test::modules.push_back((root / "relocated/late.dll").wstring());
        check(!watch.due(), "...and no catch-up burst of rounds");
        loader_test::now_ms += kset::ModuleWatch::safety_ms;
        check(watch.due() && !watch.due(), "a silent load after the pause is checked once");
        // A busy loader lock skips the round (try-only, never blocks) and retries shortly.
        loader_test::modules.push_back((root / "relocated/busy.dll").wstring());
        loader_test::lock_busy = true;
        loader_test::now_ms += kset::ModuleWatch::safety_ms;
        const size_t busy = loader_test::lock_busy_results;
        check(!watch.due() && loader_test::lock_busy_results == busy + 1, "a busy loader lock skips the round");
        loader_test::lock_busy = false;
        check(!watch.due(), "...without retrying every frame");
        loader_test::now_ms += kset::ModuleWatch::retry_ms;
        check(watch.due() && !watch.due(), "...and retries after retry_ms");
        // A notified load re-baselines: it costs one full check, not a second one at the next safety round.
        loader_test::modules.push_back((root / "relocated/notified.dll").wstring());
        loader_test::dll_loaded();
        check(watch.due(), "a notified load is due at once");
        loader_test::now_ms += kset::ModuleWatch::safety_ms;
        check(!watch.due(), "...and not again at the next safety round");
        // Unregistration and notification are serialized by the OS loader lock. Race repeated notifications
        // against teardown; after disarm returns, the loader must have no callback/context left to call.
        std::atomic<bool> started{false}, stop{false};
        std::thread loading([&] {
            started = true;
            while (!stop) loader_test::dll_loaded();
        });
        while (!started) std::this_thread::yield();
        watch.disarm();
        stop = true;
        loading.join();
        check(!watch.notifying() && loader_test::registrations == 0 && !loader_test::notify,
              "disarm during DLL loads removes the callback before returning");
        // Leave armed so destruction exercises the fallback lifetime guard too.
        check(watch.arm(), "re-arm after teardown");
    }
    check(loader_test::registrations == 0, "destruction unregisters a still-armed watch");
    for (int mode = 0; mode < 2; ++mode) {
        loader_test::registration_fails = mode == 0;
        loader_test::unregister_export = mode != 1;
        kset::ModuleWatch watch;
        check(!watch.arm() && !watch.notifying() && watch.fingerprinting(),
              "failed or incomplete notification API: the fingerprint alone");
        loader_test::now_ms += kset::ModuleWatch::safety_ms;
        check(!watch.due(), "without notifications, an unchanged list costs no check");
        loader_test::modules.push_back((root / "relocated/mode.dll").wstring());
        loader_test::now_ms += kset::ModuleWatch::safety_ms;
        check(watch.due() && !watch.due(), "without notifications, a load is checked once per round");
        loader_test::modules.pop_back();
    }
    {  // No loader-lock exports (or no PEB walk): the old throttled full check.
        loader_test::registration_fails = false;
        loader_test::unregister_export = true;
        loader_test::lock_exports = false;
        kset::ModuleWatch watch;
        check(watch.arm() && watch.notifying() && !watch.fingerprinting(), "no loader-lock API: no fingerprint");
        check(!watch.due(), "the fallback waits for the first interval after the startup scan");
        loader_test::now_ms += kset::ModuleWatch::fallback_ms;
        check(watch.due() && !watch.due(), "the fallback runs the full check once per interval");
        loader_test::lock_exports = true;
    }
    loader_test::registration_fails = false;
    loader_test::unregister_export = true;
    // A replacement at the same path (and reused HMODULE) cannot keep the old negative marker cache entry.
    const auto replacement = root / "relocated/replacement.dll";
    std::ofstream(replacement) << std::string(64, 'x');
    const auto original_time = std::filesystem::last_write_time(replacement);
    loader_test::modules = clean;
    loader_test::modules.push_back(replacement.wstring());
    check(set_in_process().empty(), "cache a clean module");
    std::string branded = "Shrouded Engine Tweaks";
    branded.resize(64, 'x');
    std::ofstream(root / "relocated/new.dll") << branded;
    std::filesystem::last_write_time(root / "relocated/new.dll", original_time);
    std::filesystem::rename(root / "relocated/new.dll", replacement);
    check(!set_in_process().empty(), "reloaded file at the same path is inspected again");
    // A failed poll must release its busy guard, and a busy caller must not spend the next poll's interval.
    loader_test::modules = clean;
    g_running = true;
    g_console_next = 0;
    std::ofstream(root / "control/set/fault.lua") << "print(3)\n";
    throw_queue = true;
    try { maybe_poll_console(); } catch (const std::runtime_error&) {}
    throw_queue = false;
    check(!g_console_busy, "console poll exception releases the busy flag");
    g_console_busy = true;
    g_console_next = loader_test::now_ms;
    maybe_poll_console();
    check(g_console_next == loader_test::now_ms, "a busy console poll does not postpone the next scan");
    g_console_busy = false;
    std::ofstream(root / "control/set/recover.lua") << "print(4)\n";
    maybe_poll_console();
    check(queued == 3, "console polling recovers after the exception and busy caller");
    loader_test::now_ms += console_ms;
    std::ofstream(root / "control/set/race.lua") << "print(5)\n";
    std::array<std::thread, 8> pollers;
    for (auto& t : pollers) t = std::thread([] { maybe_poll_console(); });
    for (auto& t : pollers) t.join();
    check(queued == 4, "concurrent console callers queue each command once");
    // Exercise production on_ready, including modules present before this plugin and loads during its scan.
    api.path = [](int32_t kind, char* buffer, size_t capacity, size_t* n) -> KilnResult {
        const auto text = u8(kind == KILN_PATH_CONTROL ? g_control : g_control.parent_path());
        if (text.size() > capacity) return KILN_ERR_INVALID_ARGUMENT;
        std::memcpy(buffer, text.data(), text.size()); *n = text.size(); return KILN_OK;
    };
    api.service = [](KilnStr, uint32_t) -> const void* { return nullptr; };
    api.side = []() -> uint32_t { return KILN_SIDE_SERVER; };
    api.subscribe_tick = [](KilnOwner, int32_t, KilnTickFn, void*, KilnHandle* h) -> KilnResult { *h = 1; return KILN_OK; };
    api.subscribe_world = [](KilnOwner, KilnWorldEventFn, void*, KilnHandle* h) -> KilnResult { *h = 2; return KILN_OK; };
    g_running = false; g_initialized = false;
    loader_test::modules = {(root / "relocated/powrprof.dll").wstring()};
    on_ready(nullptr);
    check(!g_running && !g_initialized && loader_test::registrations == 0,
          "startup catches an already-loaded proxy and unregisters without starting Lua");
    loader_test::modules = clean;
    loader_test::on_snapshot = [&] {
        check(loader_test::registrations == 1, "on_ready arms before scanning");
        loader_test::dll_loaded();
    };
    on_ready(nullptr);
    loader_test::on_snapshot = {};
    check(g_running && g_modules.due(), "on_ready preserves a load during its startup scan");
    on_unload(nullptr);
    check(loader_test::registrations == 0, "on_unload explicitly unregisters");
    // state/set.json: kiln-set's report in its siblings' shape (pid, updatedUnixMs, status, mods[] with id/status/reason)
    {
        g_state = root / "state";
        g_game = root;
        std::filesystem::remove_all(g_state);
        rt::reports = {{"good", "loaded", "", "Good \"Mod\"", "1.0.0", "C:\\g\\good"},
                       {"bad", "failed", "init.lua:1: boom\nline two", "", "", "C:\\g\\bad"}};
        check(rt::reported == &on_mods_reported, "on_ready asks for the load reports");
        rt::reported();
        std::ifstream f(g_state / "set.json");
        std::stringstream ss;
        ss << f.rdbuf();
        const std::string j = ss.str();
        auto has = [&](const char* t) { return j.find(t) != std::string::npos; };
        check(has("\"format\": 1") && has("\"plugin\": \"kiln-set\"") && has("\"pid\": 1") && has("\"updatedUnixMs\": ") &&
                  has("\"status\": \"running\"") && has("1 of 2 mods loaded"),
              "set.json has the sibling fields and the overall status");
        check(has("{\"id\": \"good\", \"name\": \"Good \\\"Mod\\\"\", \"version\": \"1.0.0\", \"status\": \"loaded\"") &&
                  has("\"id\": \"bad\", \"name\": \"bad\"") && has("boom\\nline two") && has("C:\\\\g\\\\bad"),
              "set.json lists each mod with escaped text");
        check(!std::filesystem::exists(g_state / "set.json.tmp"), "set.json is written through a temporary file");
        write_state("stepped-aside", "SET's own powrprof.dll runs", {});
        std::ifstream f2(g_state / "set.json");
        std::stringstream s2;
        s2 << f2.rdbuf();
        check(s2.str().find("\"steppedAside\": true") != std::string::npos && s2.str().find("\"mods\": []") != std::string::npos,
              "stepping aside is reported with no mods");
    }
    check(failures == 0, "all edge cases");
    if (failures) return 1;
    std::puts("plugin coexistence/lifecycle/per-frame cost review: PASS");
}
