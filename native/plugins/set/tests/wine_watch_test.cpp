// SPDX-License-Identifier: MIT
// Under Wine: kset::ModuleWatch gets DLL-load notifications (LdrRegisterDllNotification), a DLL load makes exactly one
// re-check due, and the steady-state gate costs nothing next to the full module check (which it must keep off the
// per-frame path). Run by test-wine.sh; exit 0 = pass.
#include <windows.h>

#include <chrono>
#include <cstdio>

#include "../src/coexist.h"

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
    check(kset::runtime_conflict().empty(), "a clean process has no SET conflict");
    int due = 0;
    for (int i = 0; i < 100000; ++i) due += watch.due();
    check(due == 0, "no re-check is due without a DLL load (100000 frames)");
    const HMODULE dll = LoadLibraryW(L"version.dll");
    check(dll != nullptr, "loaded version.dll");
    check(watch.due(), "a DLL load makes a re-check due");
    check(!watch.due(), "...exactly once");
    if (dll) FreeLibrary(dll);
    check(!watch.due(), "an unload makes no re-check due");

    constexpr int checks = 50, gates = 1000000;
    auto t0 = std::chrono::steady_clock::now();
    for (int i = 0; i < checks; ++i) kset::runtime_conflict();
    const double full = us_since(t0, checks);
    t0 = std::chrono::steady_clock::now();
    for (int i = 0; i < gates; ++i) due += watch.due();
    const double gate = us_since(t0, gates);
    std::printf("cost: full module check %.1f us, steady-state gate %.4f us\n", full, gate);
    check(gate < 1.0, "the steady-state gate costs under a microsecond");

    watch.disarm();
    check(!watch.notifying(), "disarm unregisters");
    const HMODULE again = LoadLibraryW(L"version.dll");  // must not call into the unregistered watch
    if (again) FreeLibrary(again);
    // Fallback: without a registration, at most once per interval.
    kset::ModuleWatch throttle;
    int fallback = 0;
    for (int i = 0; i < 100000; ++i) fallback += throttle.due();
    check(fallback == 1, "the unarmed fallback is due once, then throttled");
    std::printf(failures ? "wine watch test: FAIL (%d)\n" : "wine watch test: PASS\n", failures);
    return failures;
}
