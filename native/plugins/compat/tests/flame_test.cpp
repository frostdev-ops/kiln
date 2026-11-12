// SPDX-License-Identifier: MIT
// A Flame mod for compathost: exports FlameStartRuntime (no arguments, no result). It writes started.txt next to
// itself (found through its own module) with the thread it ran on, and whether it found its data where Creative Mode's
// Flame runtime looks for it: <game>\mods\<its name>\sigs\<build key>.json, the build key being the game exe's
// TimeDateStamp and SizeOfImage ("6AEB144C-2A3F000"), whatever folder its DLL was loaded from (Creative Mode 3.2.0 run
// by kiln-flame from the Library: "Mod directory: S:\...\Enshrouded\mods\creative"). Built with FT_FAULT: its
// FlameStartRuntime faults.
#include <windows.h>

#include <cstdio>
#include <cwchar>
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
    // <game>\mods\<the DLL's name without .dll>\sigs\<build key>.json
    wchar_t data[MAX_PATH] = L"";
    {
        wchar_t game[MAX_PATH] = L"";
        GetModuleFileNameW(nullptr, game, MAX_PATH);
        if (wchar_t* slash = wcsrchr(game, L'\\')) *slash = 0;
        const wchar_t* name = wcsrchr(path, L'\\') ? wcsrchr(path, L'\\') + 1 : path;
        wchar_t stem[MAX_PATH] = L"";
        wcsncpy(stem, name, MAX_PATH - 1);
        if (wchar_t* dot = wcsrchr(stem, L'.')) *dot = 0;
        const auto* base = reinterpret_cast<const unsigned char*>(GetModuleHandleW(nullptr));
        const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + reinterpret_cast<const IMAGE_DOS_HEADER*>(base)->e_lfanew);
        _snwprintf(data, MAX_PATH - 1, L"%ls\\mods\\%ls\\sigs\\%08lX-%lX.json", game, stem,
                   static_cast<unsigned long>(nt->FileHeader.TimeDateStamp), static_cast<unsigned long>(nt->OptionalHeader.SizeOfImage));
    }
    const bool found = GetFileAttributesW(data) != INVALID_FILE_ATTRIBUTES;
    if (wchar_t* slash = wcsrchr(path, L'\\')) wcscpy(slash + 1, L"started.txt");
    if (FILE* f = _wfopen(path, L"w")) {
        std::fprintf(f, "started thread=%lu\ndata=%s\n", GetCurrentThreadId(), found ? "found" : "missing");
        std::fclose(f);
    }
}

BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID) {
#ifdef FT_DLLMAIN_FAULT
    if (reason == DLL_PROCESS_ATTACH) RaiseException(0xE0424242, 0, 0, nullptr);
#endif
    return TRUE;
}
