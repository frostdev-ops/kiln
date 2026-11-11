// SPDX-License-Identifier: MIT
// Under Wine: kset::ModuleWatch gets DLL-load notifications (LdrRegisterDllNotification), a DLL load makes exactly one
// re-check due, and the steady-state gate costs nothing next to the full module check (which it must keep off the
// per-frame path). Run by test-wine.sh; exit 0 = pass.
#include <windows.h>

#include <chrono>
#include <cstdio>

#include "../src/coexist.h"

#if defined(KSET_WATCH_DLL)
// Built from this same source as an unloadable fixture. Its static watch must unregister even if the host
// skips explicit teardown; /MT's DLL detach runs the destructor before the code/context can be unmapped.
namespace { kset::ModuleWatch dll_watch; }
extern "C" __declspec(dllexport) bool arm_watch() { return dll_watch.arm(); }
extern "C" __declspec(dllexport) void disarm_watch() { dll_watch.disarm(); }
#elif defined(KSET_SILENT_DLL)
extern "C" __declspec(dllexport) const char* marker() { return "Shrouded Engine Tweaks"; }
#else
#include <atomic>
#include <thread>

namespace {
int failures = 0;
void check(bool ok, const char* what) {
    std::printf("%s: %s\n", ok ? "ok  " : "FAIL", what);
    if (!ok) ++failures;
}
double us_since(std::chrono::steady_clock::time_point t0, int n) {
    return std::chrono::duration<double, std::micro>(std::chrono::steady_clock::now() - t0).count() / n;
}
}  // namespace

int main() {
    kset::ModuleWatch watch;
    check(watch.arm() && watch.notifying(), "LdrRegisterDllNotification is available and registered");
    check(watch.fingerprinting(), "the PEB loader-list fingerprint works (LdrLockLoaderLock try-only)");
    check(kset::runtime_conflict().empty(), "a clean process has no SET conflict");
    {  // the retry gate (coexist.h): a failed inventory is retried, a seen SET steps aside at once, five failures in a row do too
        kset::ConflictGate gate;
        check(gate.startup().empty() && gate.failures() == 0 && !gate.retry_due(), "the gate starts on a clean inventory");
        const kset::Inspection failed{kset::Inventory::failed, "loaded modules could not be inspected"};
        const kset::Inspection foreign{kset::Inventory::foreign, "a non-system powrprof.dll is loaded (x)"};
        const uint64_t now = GetTickCount64();
        check(gate.judge(failed, now - 2 * kset::ConflictGate::retry_ms).empty() && gate.failures() == 1 && gate.retry_due() && !gate.retry_due(),
              "one failed inventory keeps running and asks for one retry");
        check(gate.judge({}, now).empty() && gate.failures() == 0 && !gate.retry_due(), "a clean inventory resets the count");
        for (int i = 0; i < kset::ConflictGate::failures_to_step_aside - 1; ++i) check(gate.judge(failed, now).empty(), "...still running");
        check(!gate.retry_due(), "the retry waits its interval");
        check(gate.judge(failed, now).find("5 checks in a row") != std::string::npos, "five failed inventories in a row step aside");
        gate.reset();
        check(gate.judge(failed, now).empty() && gate.judge(foreign, now) == foreign.text, "a foreign runtime that is seen steps aside at once");
    }
    int due = 0;
    for (int i = 0; i < 100000; ++i) due += watch.due();
    check(due == 0, "no re-check is due without a DLL load (100000 frames)");
    const HMODULE dll = LoadLibraryW(L"version.dll");
    check(dll != nullptr, "loaded version.dll");
    check(watch.due(), "a DLL load makes a re-check due");
    check(!watch.due(), "...exactly once");
    if (dll) FreeLibrary(dll);
    check(!watch.due(), "an unload makes no re-check due at once");

    constexpr int checks = 50, gates = 1000000;
    auto t0 = std::chrono::steady_clock::now();
    for (int i = 0; i < checks; ++i) kset::runtime_conflict();
    const double full = us_since(t0, checks);
    t0 = std::chrono::steady_clock::now();
    for (int i = 0; i < gates; ++i) due += watch.due();
    const double gate = us_since(t0, gates);
    constexpr int prints = 100000;
    uint64_t fp = 0;
    int printed = 0;
    t0 = std::chrono::steady_clock::now();
    for (int i = 0; i < prints; ++i) printed += watch.fingerprint(&fp) > 0;
    const double print = us_since(t0, prints);
    std::printf("cost: full module check %.1f us, steady-state gate %.4f us, fingerprint (safety round) %.2f us\n",
                full, gate, print);
    check(gate < 1.0, "the steady-state gate costs under a microsecond");
    check(printed == prints && print < 100.0, "a fingerprint round costs under 100 us (no wineserver call)");

    // The unload above changed the list after the last baseline: at most one (harmless) check at the next round.
    {
        int settle = 0;
        const auto end = GetTickCount64() + kset::ModuleWatch::safety_ms + 500;
        while (GetTickCount64() < end) { settle += watch.due(); Sleep(16); }
        std::printf("after the unload: %d check(s) at the next safety round\n", settle);
        check(settle <= 1, "an unload costs at most one check");
    }
    // Steady state across several safety rounds: 10.5 s of 16 ms frames, zero full checks.
    {
        int steady = 0, frames = 0;
        const auto end = GetTickCount64() + 10500;
        while (GetTickCount64() < end) { steady += watch.due(); ++frames; Sleep(16); }
        std::printf("steady state: %d checks due in %d frames over 10.5 s\n", steady, frames);
        check(steady == 0, "steady state over 10.5 s (3 safety rounds): no full check");
    }

    watch.disarm();
    check(!watch.notifying(), "disarm unregisters");
    const HMODULE again = LoadLibraryW(L"version.dll");  // must not call into the unregistered watch
    if (again) FreeLibrary(again);
    wchar_t exe[32768]{};
    GetModuleFileNameW(nullptr, exe, static_cast<DWORD>(std::size(exe)));
    const auto dir = std::filesystem::path(exe).parent_path();
    check(watch.arm(), "re-arm for the silent-load safety scan");
    const HMODULE silent = LoadLibraryExW((dir / L"silent-loader.dll").c_str(), nullptr, DONT_RESOLVE_DLL_REFERENCES);
    check(silent != nullptr, "mapped a DLL without resolving imports");
    check(!kset::runtime_conflict().empty(), "the mapped loader is visible to the existing conflict policy");
    // Some loader paths (notably Wine's no-resolve path) skip the callback. Either way the check must become due
    // within the safety interval; do not assume that Windows and Wine notify on precisely the same paths.
    const bool immediately = watch.due();
    bool by_print = false;
    if (!immediately) {
        const auto end = GetTickCount64() + kset::ModuleWatch::safety_ms + 500;
        while (!by_print && GetTickCount64() < end) { by_print = watch.due(); Sleep(16); }
    }
    check(immediately || by_print, "a no-resolve (silent) load is due within the safety interval");
    std::printf("no-resolve load: %s\n", immediately ? "notified" : by_print ? "caught by the fingerprint" : "missed");
    {  // and nothing more after it
        int after = 0;
        const auto end = GetTickCount64() + kset::ModuleWatch::safety_ms + 500;
        while (GetTickCount64() < end) { after += watch.due(); Sleep(16); }
        check(after == 0, "...once: the next safety round sees no change");
    }
    if (silent) FreeLibrary(silent);
    watch.disarm();

    // Race actual DLL unloading (both explicit disarm and static-destruction-only teardown) with loader activity.
    std::atomic<bool> stop{false};
    std::thread loading([&] {
        while (!stop) { const HMODULE h = LoadLibraryW(L"version.dll"); if (h) FreeLibrary(h); }
    });
    bool unload_ok = true;
    for (int i = 0; i < 100 && unload_ok; ++i) {
        const HMODULE h = LoadLibraryW((dir / L"watch-fixture.dll").c_str());
        if (!h) { unload_ok = false; break; }
        const auto arm = reinterpret_cast<bool(*)()>(GetProcAddress(h, "arm_watch"));
        const auto disarm = reinterpret_cast<void(*)()>(GetProcAddress(h, "disarm_watch"));
        unload_ok = arm && disarm && arm();
        if (unload_ok && i % 2 == 0) disarm();
        if (!FreeLibrary(h)) unload_ok = false;
    }
    stop = true;
    loading.join();
    check(unload_ok, "100 DLL unloads during DLL loads: explicit disarm and static destructor");
    // Fallback: without a registration, at most once per interval.
    kset::ModuleWatch throttle;
    int fallback = 0;
    for (int i = 0; i < 100000; ++i) fallback += throttle.due();
    check(fallback == 1, "the unarmed fallback is due once, then throttled");
    std::printf(failures ? "wine watch test: FAIL (%d)\n" : "wine watch test: PASS\n", failures);
    return failures;
}
#endif
