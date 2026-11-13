// SPDX-License-Identifier: MIT
// The libraries ui-load-hook-test.exe loads (harness.cpp), one source, three builds:
//   ui-load-hook-plain.dll    nothing
//   ui-load-hook-blocker.dll  its DllMain says it runs, then waits for the test (5 s at most), the loader lock held;
//                             the two events are the run's own (KILN_LOAD_HOOK_RUN names them)
//   ui-load-hook-nested.dll   its DllMain loads the plain library (a load from inside a load)
#include <windows.h>

#include <cwchar>

#ifdef FIXTURE_NESTED
static HMODULE g_inner = nullptr;
extern "C" __declspec(dllexport) int kiln_fixture_inner_loaded() { return g_inner != nullptr; }
#endif

BOOL WINAPI DllMain(HINSTANCE self, DWORD reason, LPVOID) {
    (void)self;
    if (reason != DLL_PROCESS_ATTACH) return TRUE;
#ifdef FIXTURE_BLOCKER
    // the two events of the run that loads this library: their names end in what the test put into the environment
    wchar_t run[64], name[128];
    const DWORD got = GetEnvironmentVariableW(L"KILN_LOAD_HOOK_RUN", run, 64);
    if (got && got < 64) {
        std::swprintf(name, 128, L"Local\\kiln-load-hook-in-%ls", run);
        HANDLE in = OpenEventW(EVENT_MODIFY_STATE, FALSE, name);
        std::swprintf(name, 128, L"Local\\kiln-load-hook-release-%ls", run);
        HANDLE release = OpenEventW(SYNCHRONIZE, FALSE, name);
        if (in && release) {
            SetEvent(in);
            WaitForSingleObject(release, 5000);
        }
        if (in) CloseHandle(in);
        if (release) CloseHandle(release);
    }
#endif
#ifdef FIXTURE_NESTED
    wchar_t path[MAX_PATH];
    const DWORD n = GetModuleFileNameW(self, path, MAX_PATH);
    wchar_t* slash = n && n < MAX_PATH ? std::wcsrchr(path, L'\\') : nullptr;
    const wchar_t plain[] = L"ui-load-hook-plain.dll";
    if (slash && static_cast<size_t>(slash + 1 - path) + sizeof plain / sizeof plain[0] <= MAX_PATH) {
        std::wcscpy(slash + 1, plain);
        g_inner = LoadLibraryExW(path, nullptr, 0);
    }
#endif
    return TRUE;
}
