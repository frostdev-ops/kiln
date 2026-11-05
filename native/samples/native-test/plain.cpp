// SPDX-License-Identifier: MIT
// native-plain: a DLL mod that knows nothing about Kiln (no init export). Its DllMain appends one line to
// <its folder>\native-plain.log, so a check can see that and when it was loaded (also from the proxy, phase dllmain).
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, void*) {
    if (reason != DLL_PROCESS_ATTACH) return TRUE;
    DisableThreadLibraryCalls(inst);
    wchar_t path[1024];
    const DWORD n = GetModuleFileNameW(inst, path, 1000);
    if (n == 0 || n >= 1000) return TRUE;
    wchar_t* slash = path;
    for (wchar_t* p = path; *p; ++p)
        if (*p == L'\\') slash = p;
    lstrcpyW(slash, L"\\native-plain.log");
    wchar_t name[260];
    GetModuleFileNameW(inst, name, 260);
    char line[600];
    // the exe's entry point: "ff 25" = a gate's jump is in place (loaded after Kiln's gate), else the original code
    const auto* image = reinterpret_cast<const unsigned char*>(GetModuleHandleW(nullptr));
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(image + reinterpret_cast<const IMAGE_DOS_HEADER*>(image)->e_lfanew);
    const unsigned char* entry = image + nt->OptionalHeader.AddressOfEntryPoint;
    const int k = wsprintfA(line, "attach pid %lu module %ls entry %02x %02x\r\n", GetCurrentProcessId(), name, entry[0], entry[1]);
    HANDLE h = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h != INVALID_HANDLE_VALUE) {
        DWORD w = 0;
        WriteFile(h, line, static_cast<DWORD>(k), &w, nullptr);
        CloseHandle(h);
    }
    return TRUE;
}
