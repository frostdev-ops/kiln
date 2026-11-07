// SPDX-License-Identifier: MIT
// A Flame mod for compathost: exports FlameStartRuntime (no arguments, no result). It writes started.txt next to
// itself (found through its own module, as a Flame runtime finds its sigs/ and symbols/) with the thread it ran on and
// whether that was the process's first thread. Built with FT_FAULT: its FlameStartRuntime faults.
#include <windows.h>

#include <cstdio>
#ifdef FT_DEPENDENCY
extern "C" __declspec(dllimport) const char compat_fake_loader_marker[];
#endif

extern "C" __declspec(dllexport) void FlameStartRuntime() {
#ifdef FT_DEPENDENCY
    if (!compat_fake_loader_marker[0]) return;
#endif
#ifdef FT_SLOW
    Sleep(500);
#endif
#ifdef FT_LOAD_LOADER
    LoadLibraryW(L"dinput8-flame.dll");
#endif
#ifdef FT_FAULT
    volatile int* p = nullptr;
    *p = 1;
#endif
    HMODULE self = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       reinterpret_cast<LPCWSTR>(&FlameStartRuntime), &self);
    wchar_t path[MAX_PATH] = L"";
    GetModuleFileNameW(self, path, MAX_PATH);
    if (wchar_t* slash = wcsrchr(path, L'\\')) wcscpy(slash + 1, L"started.txt");
    if (FILE* f = _wfopen(path, L"w")) {
        std::fprintf(f, "started thread=%lu\n", GetCurrentThreadId());
        std::fclose(f);
    }
}

BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID) {
#ifdef FT_DLLMAIN_FAULT
    if (reason == DLL_PROCESS_ATTACH) RaiseException(0xE0424242, 0, 0, nullptr);
#endif
    return TRUE;
}
