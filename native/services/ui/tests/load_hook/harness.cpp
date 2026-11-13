// SPDX-License-Identifier: MIT
// ui-load-hook-test.exe: kiln.ui's hook on LoadLibraryExW (load_hook.h, the lines ui.cpp runs) in a Windows process
// under Wine: no game, no display. Run by native/services/ui/tests/test-load-hook-wine.sh; the libraries it loads are
// fixture.cpp's, beside this exe. Checks:
//   - the hook goes in, with the original known before the first call can reach the detour;
//   - a load gives its module and its "loaded" call runs on the loading thread;
//   - a load that fails gives null and the caller's last error, and no "loaded" call;
//   - a load in progress does not hold up another thread's call (GitHub PR 27): one thread's load sits in a DllMain
//     that waits for the test; a second thread's call must return meanwhile. With the original called under the
//     hook's mutex (SafetyHookInline::call), the second call waited until the DllMain gave up after 5 s; in the game
//     the two waited for each other for good;
//   - a load made from a DllMain (the loader lock held) goes through the detour and works;
//   - eight threads loading and freeing at once: every load gives the module, every one is counted.
// Lines: "PASS ...", "FAIL ...". Exit code 0 = no FAIL.
#include <windows.h>

#include <atomic>
#include <cstdio>
#include <string>
#include <thread>
#include <vector>

#include "load_hook.h"

namespace {

int g_failed = 0;
void check(bool ok, const char* what) {
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", what);
    if (!ok) ++g_failed;
}

// The "loaded" calls: all of them, and this thread's. A load can bring further loads with it (the library's run-time
// start-up asks for others), so the checks ask for "at least", per thread.
std::atomic<int> g_loaded_calls{0};
thread_local int t_loaded_calls = 0;

// A file beside this exe.
std::wstring beside(const wchar_t* name) {
    wchar_t path[MAX_PATH];
    GetModuleFileNameW(nullptr, path, MAX_PATH);
    std::wstring s(path);
    s.resize(s.find_last_of(L"\\/") + 1);
    return s + name;
}

}  // namespace

int main() {
    using namespace kui;
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    const std::wstring plain = beside(L"ui-load-hook-plain.dll");
    const std::wstring blocker = beside(L"ui-load-hook-blocker.dll");
    const std::wstring nested = beside(L"ui-load-hook-nested.dll");
    HANDLE in = CreateEventW(nullptr, TRUE, FALSE, L"Local\\kiln-load-hook-in");
    HANDLE release = CreateEventW(nullptr, TRUE, FALSE, L"Local\\kiln-load-hook-release");
    check(in && release, "the test's two events exist");

    check(load_hook::install([] {
              ++g_loaded_calls;
              ++t_loaded_calls;
          }),
          "LoadLibraryExW is hooked");
    check(load_hook::g_original.load() != nullptr, "the original is published");

    // ---- a load, and loads that fail ----
    HMODULE m = LoadLibraryExW(plain.c_str(), nullptr, 0);
    check(m != nullptr && GetModuleHandleW(L"ui-load-hook-plain.dll") == m, "a load gives its module");
    std::printf("INFO loaded calls after the first load: %d, %d on this thread\n", g_loaded_calls.load(), t_loaded_calls);
    check(t_loaded_calls >= 1 && g_loaded_calls == t_loaded_calls, "its loaded call ran on the loading thread");
    int before = t_loaded_calls;
    HMODULE w = LoadLibraryW(plain.c_str());
    check(w == m && t_loaded_calls > before, "LoadLibraryW goes through the same hook");
    if (w) FreeLibrary(w);

    g_loaded_calls = 0;
    SetLastError(0);
    HMODULE none = LoadLibraryExW(L"kiln-no-such-library-27.dll", nullptr, 0);
    DWORD error = GetLastError();
    check(!none && error == ERROR_MOD_NOT_FOUND, "a library that is not there: null and ERROR_MOD_NOT_FOUND");
    SetLastError(0);
    none = LoadLibraryExW(nullptr, nullptr, 0);
    error = GetLastError();
    check(!none && error == ERROR_INVALID_PARAMETER, "no name: null and ERROR_INVALID_PARAMETER");
    check(g_loaded_calls == 0, "no loaded call after a load that failed");

    // ---- a load in progress does not hold up another thread's call ----
    HMODULE blocked = nullptr;
    std::atomic<DWORD> blocked_ms{0};
    std::thread first([&] {
        const ULONGLONG t = GetTickCount64();
        blocked = LoadLibraryExW(blocker.c_str(), nullptr, 0);  // its DllMain waits for `release`, 5 s at most
        blocked_ms = static_cast<DWORD>(GetTickCount64() - t);
    });
    check(WaitForSingleObject(in, 20000) == WAIT_OBJECT_0, "the first thread's load is inside the library's DllMain");
    // a call that needs no loader lock (it fails on its argument), from a second thread: this one
    const ULONGLONG t = GetTickCount64();
    SetLastError(0);
    none = LoadLibraryExW(nullptr, nullptr, 0);
    error = GetLastError();
    const DWORD second_ms = static_cast<DWORD>(GetTickCount64() - t);
    SetEvent(release);
    first.join();
    std::printf("INFO the second thread's call took %lu ms, the first thread's load %lu ms\n", second_ms, blocked_ms.load());
    check(!none && error == ERROR_INVALID_PARAMETER, "the second thread's call gave its own result");
    check(second_ms < 2000, "the second thread's call returned while the first load was in progress");
    check(blocked != nullptr && blocked_ms < 4500, "the first load ended when the test let it (not by its 5 s limit)");

    // ---- a load from inside a DllMain ----
    if (m) FreeLibrary(m);  // the nested load is then a real load of the plain library
    before = t_loaded_calls;
    HMODULE outer = LoadLibraryExW(nested.c_str(), nullptr, 0);
    auto inner_loaded = outer ? reinterpret_cast<int (*)()>(GetProcAddress(outer, "kiln_fixture_inner_loaded")) : nullptr;
    check(outer && inner_loaded && inner_loaded() == 1, "a library whose DllMain loads a library: both loaded");
    std::printf("INFO loaded calls for the two: %d\n", t_loaded_calls - before);
    check(t_loaded_calls - before >= 2, "loaded calls for both, on the loading thread: the inner load's and the outer's");

    // ---- many threads at once ----
    g_loaded_calls = 0;
    constexpr int kThreads = 8, kRounds = 200;
    std::atomic<int> bad{0};
    std::vector<std::thread> pool;
    for (int i = 0; i < kThreads; ++i) {
        pool.emplace_back([&] {
            for (int r = 0; r < kRounds; ++r) {
                HMODULE h = LoadLibraryExW(plain.c_str(), nullptr, 0);
                if (!h) ++bad;
                else FreeLibrary(h);
            }
        });
    }
    for (auto& th : pool) th.join();
    check(bad == 0, "1600 loads from eight threads: every one gave the module");
    check(g_loaded_calls >= kThreads * kRounds, "a loaded call for each of them");

    std::printf("%s: %d failed\n", g_failed ? "FAILED" : "ok", g_failed);
    return g_failed ? 1 : 0;
}
