// SPDX-License-Identifier: MIT
#include <cassert>
#include <stdexcept>
#include <thread>
#include <array>
#include <condition_variable>
#define KSET_LOADER_HOST_TEST
#include "../src/plugin.cpp"
namespace { int shutdowns = 0, alive_shutdowns = 0, ticks = 0, frames = 0, queued = 0, settles = 0; bool throw_queue = false; std::string overlay_reason; std::vector<std::string> fileLines, consoleLines; }
namespace rt {
void gameClosing(bool gameAlive) { ++shutdowns; alive_shutdowns += gameAlive; }
void setFeature(const char* id, bool, std::string reason, bool) { if (std::string(id) == "overlay") overlay_reason = reason; }
uint32_t gameBuild() { return 0; }
void tick(uintptr_t) { ++ticks; } void settleWrites() { ++settles; }
void afterTick(uintptr_t) {} void worldLeft() {} void frame() { ++frames; }
int overlayOpen = -1; void setOverlayOpen(bool open) { overlayOpen = open; } bool hasDraw() { return false; }
bool tryLockDraw() { return false; } void drawLocked() {}
void setSystemsAvailable(bool) {} void bindKiln(const KilnApi*, KilnOwner) {}
void configure(const std::string&, Sink) {} void setGameBuild(uint32_t) {}
std::vector<ModReport> reports; void (*reported)() = nullptr;
std::vector<ModReport> modReports() { return reports; } void onModsReported(void (*fn)()) { reported = fn; }
void queue(std::string) { if (throw_queue) throw std::runtime_error("queue failed"); ++queued; }
}
namespace setui {
bool windowOpen = false, windowInput = false;
void line(const char* text) { consoleLines.emplace_back(text); } void draw() {} void stop() {} bool isOpen() { return windowOpen; }
bool hasInput() { return windowInput; }
int sessionOffs = 0; void sessionOff(std::filesystem::path) { ++sessionOffs; }
void configure(std::filesystem::path, std::filesystem::path, void (*)(const char*)) {} void bind(const KilnUiApi*, KilnOwner) {}
const Api* service() { static const Api api{}; return &api; }
}
namespace hk { void setLog(Log) {} }
namespace contracts {
int checks = 0;
const std::vector<Entry>& catalog() { static const std::vector<Entry> none; return none; }
std::vector<Status> check(uintptr_t, uint32_t, const std::vector<Entry>&, bool) { ++checks; return {}; }
void setChecked(std::vector<Status>) {}
}
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
// A DLL initializer owns the loader lock while a game thread inspects modules. If it enters the same
// hook/inventory, neither the inventory mutex nor the caller's check mutex may wait for that game thread.
int loader_lock_check(bool plugin) {
    std::mutex mu;
    std::condition_variable cv;
    bool referencing = false, done = false;
    std::thread watchdog([&] {
        std::unique_lock lock(mu);
        if (!cv.wait_for(lock, std::chrono::seconds(3), [&] { return done; })) {
            std::fprintf(stderr, "FAIL %s: loader-lock owner blocked behind an inspector waiting for the loader lock\n",
                         plugin ? "may_run" : "inspect_runtime");
            std::_Exit(1);
        }
    });
    if (plugin) { g_running = true; assert(g_modules.arm()); loader_test::dll_loaded(); }
    loader_test::on_walk = [&] {
        std::lock_guard lock(mu);
        referencing = true;
        cv.notify_all();
    };
    std::unique_lock loader(loader_test::loader_lock);
    std::thread inspector([&] {
        if (plugin) assert(may_run());
        else assert(kset::inspect_runtime().state == kset::Inventory::clean);
    });
    {
        std::unique_lock lock(mu);
        cv.wait(lock, [&] { return referencing; });
    }
    if (plugin) {
        loader_test::dll_loaded();
        assert(may_run());
    } else {
        const auto seen = kset::inspect_runtime();
        assert(seen.state == kset::Inventory::failed && seen.text.find("in progress") != std::string::npos);
    }
    loader.unlock();
    inspector.join();
    loader_test::on_walk = {};
    if (plugin) {
        // The second caller consumed a fresh notification. It must leave a recheck pending, not lose it.
        const size_t before = loader_test::walks;
        assert(may_run() && loader_test::walks == before + 1);
        g_modules.disarm();
    }
    {
        std::lock_guard lock(mu);
        done = true;
    }
    cv.notify_all();
    watchdog.join();
    std::printf("PASS %s: no loader-lock inversion; a contended check is retried\n", plugin ? "may_run" : "inspect_runtime");
    return 0;
}
int main(int argc, char** argv) {
    assert(argc == 2 || argc == 3);
    assert(kiln_plugin_main(nullptr, 0, nullptr) == KILN_ERR_UNAVAILABLE);
    const std::filesystem::path root(argv[1]);
    std::filesystem::remove_all(root / "control");
    std::filesystem::create_directories(root / "System32");
    std::filesystem::create_directories(root / "relocated");
    std::ofstream(root / "System32/powrprof.dll") << "Windows power API";
    // SET's powrprof.dll, known by its folder name (SET reads shrouded_engine_tweaks\plugins\ and \mods\).
    std::ofstream(root / "relocated/powrprof.dll") << "SET's proxy: shrouded_engine_tweaks\\plugins";
    // Issue 23: a proxy loader's powrprof.dll that only forwards is no SET; it is loaded through every check below.
    std::filesystem::create_directories(root / "forward");
    std::ofstream(root / "forward/powrprof.dll") << "Forwarding exports to %s\\powrprof.dll";
    std::ofstream(root / "relocated/renamed.dll") << "Shrouded Engine Tweaks";
    std::ofstream(root / "relocated/framework.dll") << "Shrouded Engine Tweaks";
    loader_test::system = (root / "System32").wstring();
    const std::vector<std::wstring> clean = {(root / "System32/powrprof.dll").wstring(),
                                             (root / "relocated/framework.dll").wstring(),
                                             (root / "forward/powrprof.dll").wstring()};
    loader_test::modules = clean;
    KilnApi api{};
    api.log = [](KilnOwner, int32_t, KilnStr text) { fileLines.emplace_back(text.ptr, text.len); };
    api.server_world = []() -> uintptr_t { return 1; };
    g_api = &api;
    if (argc == 3) return loader_lock_check(std::string(argv[2]) == "plugin-lock");
    // Tool debug and LV_LOG diagnostics are file-only; ordinary SET output still reaches the private console.
    ui_sink("tool diagnostics"); sink(LV_LOG "file only"); sink(LV_WARN "warning"); sink("plain");
    assert((fileLines == std::vector<std::string>{"[set] tool diagnostics", "[set] file only", "[set] warning", "[set] plain"}));
    assert((consoleLines == std::vector<std::string>{"warning", "plain"}));
    fileLines.clear(); consoleLines.clear();
    g_control = root / "control";
    // Startup, in on_ready's order: arm the DLL-load notification, then the one full check.
    assert(g_modules.arm() && g_modules.notifying() && g_modules.fingerprinting() && loader_test::registrations == 1);
    assert(set_in_process().empty());
    // Kiln's own files name SET (kiln\host\Kiln.Ui.dll's mod catalogue): never SET's runtime. The same file chained
    // from kiln\chain\ is a foreign loader and still counts.
    loader_test::exe = (root / "enshrouded.exe").wstring();
    std::filesystem::create_directories(root / "kiln/host");
    std::filesystem::create_directories(root / "kiln/chain");
    std::ofstream(root / "kiln/host/Kiln.Ui.dll") << "mods for Shrouded Engine Tweaks, Flame, Shroudtopia";
    std::ofstream(root / "kiln/chain/Kiln.Ui.dll") << "mods for Shrouded Engine Tweaks, Flame, Shroudtopia";
    loader_test::modules = {(root / "kiln/host/Kiln.Ui.dll").wstring()};
    assert(set_in_process().empty());
    loader_test::modules = {(root / "kiln/chain/Kiln.Ui.dll").wstring()};
    assert(set_in_process().find("SET-branded") != std::string::npos);
    loader_test::modules = clean;
    loader_test::exe.clear();
    g_running = true;
    // Steady state: 1000 UI frames and 1000 server steps over 16 s never re-run the module check (it was once per
    // frame/step), not even at the 3 s safety rounds: those only fingerprint the PEB list, which has not changed.
    size_t base = loader_test::walks;
    const size_t probes = loader_test::lock_tries;
    frames_and_steps(1000);
    std::printf("steady state: %zu module checks, %zu fingerprint rounds in 1000 frames + 1000 steps over 16 s\n",
                loader_test::walks - base, loader_test::lock_tries - probes);
    assert(loader_test::walks == base && ticks == 1000 && frames == 1000 && shutdowns == 0);
    assert(loader_test::lock_tries - probes == 5);
    // A silent load (in the PEB list, no notification) is caught at the next safety round, by one full check.
    loader_test::modules.push_back((root / "relocated/silent.dll").wstring());
    frames_and_steps(200);  // 3.2 s
    assert(loader_test::walks == base + 1 && shutdowns == 0);
    frames_and_steps(400);  // 6.4 s more, nothing new
    assert(loader_test::walks == base + 1);
    loader_test::modules = clean;  // an unload is a list change too: one more (harmless) check
    frames_and_steps(200);
    assert(loader_test::walks == base + 2);
    base = loader_test::walks;
    ticks = 1000;  // as if the silent-load rounds above had not stepped
    // A DLL load makes exactly one re-check due.
    loader_test::dll_loaded();
    on_pre(nullptr, nullptr); assert(loader_test::walks == base + 1 && ticks == 1001);
    frames_and_steps(100, 0); assert(loader_test::walks == base + 1 && ticks == 1101 && shutdowns == 0);
    // SET's proxy loading later is still caught at the next step after its load.
    loader_test::modules.push_back((root / "relocated/powrprof.dll").wstring());
    loader_test::dll_loaded();
    on_pre(nullptr, nullptr);
    assert(!g_running && ticks == 1101 && shutdowns == 1);
    assert(alive_shutdowns == 1);  // stepping aside while the game runs on: gameClosing(true) undoes the mods' changes
    // Stepped aside on the UI thread, those changes wait for the tick thread: the next server step still settles them.
    {
        const int settled = settles;
        on_pre(nullptr, nullptr); assert(shutdowns == 1 && ticks == 1101 && settles == settled + 1);
    }
    loader_test::modules = {(root / "relocated/renamed.dll").wstring()};
    g_running = true;
    loader_test::dll_loaded();
    assert(!may_run() && shutdowns == 2);
    // A module inventory that fails is not SET: kiln-set keeps running and looks again one second later (one loader-list
    // walk per look). One clean look resets the count; only five failed looks in a row step aside.
    loader_test::modules = clean;
    g_running = true; g_gate.reset();
    {
        const int before = shutdowns, ticked = ticks;
        loader_test::walk_failures = 1;              // one whole look fails
        loader_test::dll_loaded();
        on_pre(nullptr, nullptr);
        assert(g_running && shutdowns == before && ticks == ticked + 1 && g_gate.failures() == 1);
        size_t looks = loader_test::walks;
        frames_and_steps(30);                        // 0.5 s: the retry is not due yet, and nothing looks per frame
        assert(loader_test::walks == looks && g_running);
        frames_and_steps(40);                        // past 1 s: one retry, clean this time
        assert(loader_test::walks == looks + 1 && g_running && g_gate.failures() == 0 && shutdowns == before);
        looks = loader_test::walks;
        frames_and_steps(120);                       // nothing pending any more
        assert(loader_test::walks == looks && g_running);
        // Four failed looks, then a clean one: still running.
        loader_test::walk_failures = 4;
        loader_test::dll_loaded();
        frames_and_steps(400);                       // 6.4 s
        assert(g_running && g_gate.failures() == 0 && shutdowns == before && loader_test::walk_failures == 0);
        // A foreign proxy seen while failures were pending steps aside at once.
        loader_test::walk_failures = 1;
        loader_test::dll_loaded();
        on_pre(nullptr, nullptr);
        assert(g_running && g_gate.failures() == 1);
        loader_test::modules.push_back((root / "relocated/powrprof.dll").wstring());
        frames_and_steps(70);
        assert(!g_running && shutdowns == before + 1 && g_aside.find("powrprof.dll") != std::string::npos);
        // The inventory keeps failing: five looks in a row, about four seconds, then kiln-set steps aside and says why.
        loader_test::modules = clean;
        g_running = true; g_gate.reset();
        loader_test::walk_failures = 1000;
        loader_test::dll_loaded();
        frames_and_steps(200);                       // 3.2 s: four looks so far
        assert(g_running && g_gate.failures() == 4);
        frames_and_steps(100);
        assert(!g_running && shutdowns == before + 2 && g_aside.find("5 checks in a row") != std::string::npos);
        loader_test::walk_failures = 0;
        shutdowns = 2;                               // as the checks below count them
        g_running = true; g_gate.reset();
    }
    // Unload unregisters the notification (the loader must never call into an unmapped plugin).
    g_modules.disarm(); assert(loader_test::registrations == 0 && !g_modules.notifying());
    // Without any ntdll loader API (no notification, no loader lock): a time throttle (every 3 s), never per frame.
    loader_test::notifications = false;
    loader_test::modules = clean;
    assert(!g_modules.arm() && !g_modules.notifying() && !g_modules.fingerprinting());
    g_running = true;
    base = loader_test::walks;
    frames_and_steps(1000);  // 16 s
    const size_t checks = loader_test::walks - base;
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
        loader_test::on_walk = [] { loader_test::dll_loaded(); };
        set_in_process();
        loader_test::on_walk = {};
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
    // Startup: a failed inventory is retried on the spot; kiln-set starts when one look succeeds, and refuses only
    // when five in a row fail.
    loader_test::modules = clean;
    loader_test::walk_failures = 4;
    on_ready(nullptr);
    check(g_running && g_initialized && loader_test::walk_failures == 0, "startup retries a failed module inventory and starts");
    on_unload(nullptr);
    g_running = false; g_initialized = false;
    loader_test::walk_failures = 5;
    on_ready(nullptr);
    check(!g_running && !g_initialized && g_aside.find("5 checks in a row") != std::string::npos,
          "startup steps aside when the inventory fails five times in a row");
    loader_test::walk_failures = 0;
    loader_test::on_walk = [&] {
        check(loader_test::registrations == 1, "on_ready arms before scanning");
        loader_test::dll_loaded();
    };
    on_ready(nullptr);
    loader_test::on_walk = {};
    check(g_running && g_modules.due(), "on_ready preserves a load during its startup scan");
    check(contracts::checks > 0 && overlay_reason == "Dedicated server: No overlay", "on_ready checks the rule catalog and reports the overlay feature");
    {
        const int alive = alive_shutdowns;
        on_unload(nullptr);
        check(alive_shutdowns == alive, "on_unload (the game closing) restores nothing in the game");
    }
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
    {  // The runtime's "overlay open" (Keys off, onOverlayOpen) means SET tools that have the input, not any SET window:
       // the crash notice that opened by itself draws while the game keeps its input.
        g_running = true;
        KilnUiFrame frame{};
        frame.size = sizeof frame;
        setui::windowOpen = true; setui::windowInput = false;
        on_frame(&frame, nullptr);
        check(rt::overlayOpen == 0, "a SET window without input is not the overlay open");
        setui::windowInput = true;
        on_frame(&frame, nullptr);
        check(rt::overlayOpen == 1, "SET tools with input are");
        setui::windowOpen = setui::windowInput = false; frame.menu_open = 1;
        on_frame(&frame, nullptr);
        check(rt::overlayOpen == 1, "...and so is the Kiln menu");
        g_running = false;
    }
    {  // A client start that runs no SET mods (play once without mods, an invalid profile, SET's own runtime at start)
       // says so in the session marker: a crash in it is not blamed on SET at the next start.
        static KilnProfileApi prof{};
        prof.header = {sizeof prof, 1};
        prof.mode = []() -> uint32_t { return KILN_PROFILE_NO_MODS; };
        api.service = [](KilnStr n, uint32_t) -> const void* {
            return std::string_view(n.ptr, n.len) == KILN_PROFILE_SERVICE ? static_cast<const void*>(&prof) : nullptr;
        };
        api.side = []() -> uint32_t { return KILN_SIDE_CLIENT; };
        loader_test::modules = clean;
        g_running = false; g_initialized = false;
        const int offs = setui::sessionOffs;
        on_ready(nullptr);
        check(!g_running && setui::sessionOffs == offs + 1, "a client start without SET mods clears the session marker");
        loader_test::modules = {(root / "relocated/powrprof.dll").wstring()};
        on_ready(nullptr);
        check(!g_running && setui::sessionOffs == offs + 2, "...and so does stepping aside at start");
        loader_test::modules = clean;
        api.service = [](KilnStr, uint32_t) -> const void* { return nullptr; };
        api.side = []() -> uint32_t { return KILN_SIDE_SERVER; };
    }
    {  // The field (a real client under Proton, 2026-11-11): from the game's loading screen on, the module snapshot of the
       // own process (Toolhelp) failed in every check for 42 s while the loader list was healthy, and kiln-set stepped
       // aside after five checks. The inventory comes from the loader list, so a failing snapshot changes nothing.
        loader_test::modules = clean;
        loader_test::snapshot_failures = 1000000;    // every CreateToolhelp32Snapshot call fails
        const std::string seen = set_in_process();
        std::printf("inventory while every module snapshot fails: %s\n", seen.empty() ? "clean" : seen.c_str());
        check(seen.empty(), "a failing module snapshot leaves the inventory clean: the loader list is read");
        check(g_modules.arm(), "the DLL-load watch is armed, as after on_ready");
        g_running = true; g_gate.reset();
        const size_t walked = loader_test::walks, snapped = loader_test::snapshots;
        int looks = 0;
        for (int i = 0; i < 11; ++i) {               // the field: a DLL load, then the loading screen, one check after another
            loader_test::dll_loaded();
            frames_and_steps(63);                    // 1 s
            looks += g_running;
        }
        std::printf("11 s of failing snapshots: %zu loader-list walks, %zu snapshot calls\n", loader_test::walks - walked,
                    loader_test::snapshots - snapped);
        check(looks == 11 && g_running && g_gate.failures() == 0 && loader_test::walks - walked == 11,
              "...and kiln-set keeps running through 11 checks over 11 s of failing snapshots (the field stepped aside after 5)");
        loader_test::modules.push_back((root / "relocated/powrprof.dll").wstring());
        loader_test::dll_loaded();
        on_pre(nullptr, nullptr);
        check(!g_running && g_aside.find("powrprof.dll") != std::string::npos, "...and still steps aside for a proxy it sees");
        loader_test::snapshot_failures = 0;
        loader_test::modules = clean;
    }
    {  // A failure that remains names the call and its code, in kiln.log and in set.json's reason.
        loader_test::snapshot_failures = 1000000;    // the old enumeration's failure
        loader_test::walk_failures = 1000000;        // the loader-list walk's
        const std::string why = set_in_process();
        std::printf("a failed inventory reads: %s\n", why.c_str());
        const std::string call = "LdrEnumerateLoadedModules failed (NTSTATUS 0xC0000001)";
        check(why.find(call) != std::string::npos, "a failed inventory names the call and its status");
        g_running = true; g_gate.reset();
        fileLines.clear();
        loader_test::dll_loaded();
        frames_and_steps(400);                       // 6.4 s: a check at once, then one each second
        check(!g_running && g_aside.find(call) != std::string::npos && g_aside.find("5 checks in a row") != std::string::npos,
              "five failed checks in a row step aside, with the call and its status in the reason");
        const bool logged = std::any_of(fileLines.begin(), fileLines.end(), [&](const std::string& l) {
            return l.find(call) != std::string::npos && l.find("checking again") != std::string::npos;
        });
        check(logged, "kiln.log's first-failure line names the call and its status");
        const bool told = std::any_of(fileLines.begin(), fileLines.end(), [&](const std::string& l) {
            return l.rfind("kiln-set steps aside: ", 0) == 0 && l.find(call) != std::string::npos &&
                   l.find("cannot tell whether SET's own runtime is loaded") != std::string::npos;
        });
        check(told, "...and its step-aside line says it could not tell, rather than that SET runs");
        std::ifstream f(g_state / "set.json");
        std::stringstream ss;
        ss << f.rdbuf();
        check(ss.str().find(call) != std::string::npos && ss.str().find("\"steppedAside\": true") != std::string::npos,
              "set.json's reason names the call and its status");
        loader_test::snapshot_failures = 0;
        loader_test::walk_failures = 0;
        loader_test::modules = clean;
    }
    {  // A module the walk listed that is gone before the scan holds it is not in the process any more: skipped. Any other
       // refusal is a failed look that names the call, its code and the module.
        loader_test::modules = clean;
        loader_test::modules.push_back((root / "relocated/renamed.dll").wstring());  // SET-branded, if it were inspected
        loader_test::gone = reinterpret_cast<void*>(loader_test::modules.size() + 1);  // its HMODULE (index + 2)
        check(set_in_process().empty(), "a module unloaded after the walk is skipped, neither inspected nor a failure");
        loader_test::gone_error = 5;
        const std::string refused = set_in_process();
        std::printf("a listed module that cannot be held: %s\n", refused.c_str());
        check(refused.find("GetModuleHandleExW failed (error 5) for ") != std::string::npos && refused.find("renamed.dll") != std::string::npos,
              "...any other refusal to hold it names the call, its code and the module");
        loader_test::gone = nullptr;
        loader_test::gone_error = ERROR_MOD_NOT_FOUND;
        loader_test::last_error = ERROR_NO_MORE_FILES;
        loader_test::modules = clean;
        loader_test::walk_export = false;
        loader_test::last_error = 127;
        check(set_in_process() == "loaded modules could not be inspected: GetProcAddress(ntdll.dll, LdrEnumerateLoadedModules) failed (error 127)",
              "no LdrEnumerateLoadedModules in ntdll: a failed inventory that says so");
        loader_test::walk_export = true;
        loader_test::last_error = ERROR_NO_MORE_FILES;
    }
    {  // What kiln-set logs around the checks: the diagnostic handle read when it changes, a recovery after failed looks.
        loader_test::modules = clean;
        g_running = true; g_gate.reset();
        fileLines.clear();
        auto lines = [&](const char* text) {
            return std::count_if(fileLines.begin(), fileLines.end(), [&](const std::string& l) { return l.find(text) != std::string::npos; });
        };
        loader_test::open_own_process = false;      // a process DACL without PROCESS_VM_READ, say
        loader_test::dll_loaded();
        on_pre(nullptr, nullptr);
        loader_test::dll_loaded();
        on_pre(nullptr, nullptr);
        check(g_running && lines("kiln-set: diagnostic: this process cannot be read through a handle to its own pid: "
                                 "OpenProcess(this process, PROCESS_VM_READ) failed (error 18)") == 1,
              "the diagnostic handle read is logged once when it starts failing, with the call and its code");
        loader_test::open_own_process = true;
        loader_test::read_own_process = false;
        loader_test::dll_loaded();
        on_pre(nullptr, nullptr);
        check(lines("cannot be read through a handle to its own pid: ReadProcessMemory through that handle failed (error 18)") == 1,
              "...and again when the read itself fails");
        loader_test::read_own_process = true;
        loader_test::dll_loaded();
        on_pre(nullptr, nullptr);
        check(lines("kiln-set: diagnostic: this process can be read through a handle to its own pid") == 1, "...and when it works again");
        loader_test::walk_failures = 2;
        loader_test::dll_loaded();
        frames_and_steps(200);                       // a failed look, its retry (failed), the next (clean)
        check(g_running && g_gate.failures() == 0 && lines("kiln-set: a check of the loaded modules failed once: ") == 1 &&
                  lines("kiln-set: the loaded modules were read again after 2 failed check(s): 4 modules, no SET runtime") == 1,
              "a recovery after failed looks is logged, with the module count");
        g_modules.disarm();
        // The startup line says how many modules the loader list had.
        g_running = false; g_initialized = false;
        fileLines.clear();
        on_ready(nullptr);
        check(g_running && lines("; 4 loaded modules read from the loader list, no SET runtime") == 1, "the startup line has the module count");
        on_unload(nullptr);
        g_running = false; g_initialized = false;
    }
    check(failures == 0, "all edge cases");
    if (failures) return 1;
    std::puts("plugin coexistence/lifecycle/per-frame cost review: PASS");
}
