// Windows loader shim used only by the standalone plugin lifecycle regression test.
#pragma once
#include <cstdint>
#include <cstddef>
#include <cwchar>
#include <string>
#include <vector>
#include "../hooks_host/windows.h"
using DWORD = uint32_t; using UINT = unsigned; using SIZE_T = size_t;
using HANDLE = void*; using HMODULE = void*; using LPCWSTR = const wchar_t*;
#define MAX_PATH 260
#define WINAPI
#define INVALID_HANDLE_VALUE ((HANDLE)(intptr_t)-1)
#define GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS 4
#define GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT 2
#define IMAGE_DOS_SIGNATURE 0x5a4d
#define IMAGE_NT_SIGNATURE 0x4550
#define ERROR_NO_MORE_FILES 18
struct IMAGE_DOS_HEADER { uint16_t e_magic; int32_t e_lfanew; };
struct IMAGE_NT_HEADERS64 { uint32_t Signature; struct { uint32_t TimeDateStamp; } FileHeader; };
namespace loader_test {
inline std::wstring system;
inline std::vector<std::wstring> modules;
inline size_t cursor = 0;
// Counters and controls for the per-frame cost regression test.
inline size_t snapshots = 0;           // CreateToolhelp32Snapshot calls (the full conflict check)
inline uint64_t now_ms = 1000000;      // GetTickCount64
inline bool notifications = true;      // whether ntdll exports LdrRegisterDllNotification
inline void (*notify)(uint32_t, const void*, void*) = nullptr;
inline void* notify_context = nullptr;
inline int registrations = 0;
inline int32_t register_dll(uint32_t, void (*fn)(uint32_t, const void*, void*), void* context, void** cookie) {
    notify = fn; notify_context = context; ++registrations; *cookie = reinterpret_cast<void*>(0x77); return 0;
}
inline int32_t unregister_dll(void* cookie) {
    if (cookie == reinterpret_cast<void*>(0x77)) { notify = nullptr; --registrations; } return 0;
}
// What the loader does after mapping a DLL.
inline void dll_loaded() { if (notify) notify(1, nullptr, notify_context); }
inline void* const ntdll = reinterpret_cast<void*>(0x5000);
}
inline uint64_t GetTickCount64() { return loader_test::now_ms; }
inline DWORD GetLastError() { return ERROR_NO_MORE_FILES; }
inline UINT GetSystemDirectoryW(wchar_t* out, UINT n) {
    if (loader_test::system.size() >= n) return n;
    std::wcscpy(out, loader_test::system.c_str()); return loader_test::system.size();
}
inline bool GetModuleHandleExW(DWORD, LPCWSTR, HMODULE* out) { *out = reinterpret_cast<void*>(1); return true; }
inline HMODULE GetModuleHandleW(LPCWSTR name) {
    return name && std::wstring(name) == L"ntdll.dll" ? loader_test::ntdll : nullptr;
}
inline void* GetProcAddress(HMODULE h, const char* name) {
    if (h == loader_test::ntdll) {
        if (!loader_test::notifications) return nullptr;
        if (std::string(name) == "LdrRegisterDllNotification") return reinterpret_cast<void*>(&loader_test::register_dll);
        if (std::string(name) == "LdrUnregisterDllNotification") return reinterpret_cast<void*>(&loader_test::unregister_dll);
        return nullptr;
    }
    const size_t i = reinterpret_cast<uintptr_t>(h) - 2;
    return i < loader_test::modules.size() && loader_test::modules[i].find(L"framework.dll") != std::wstring::npos
        ? reinterpret_cast<void*>(1) : nullptr;
}
inline DWORD GetModuleFileNameW(HMODULE h, wchar_t* out, DWORD n) {
    const size_t i = reinterpret_cast<uintptr_t>(h) - 2;
    if (i >= loader_test::modules.size() || loader_test::modules[i].size() >= n) return 0;
    std::wcscpy(out, loader_test::modules[i].c_str()); return loader_test::modules[i].size();
}
inline DWORD GetCurrentProcessId() { return 1; }
inline HANDLE GetCurrentProcess() { return nullptr; }
inline bool CloseHandle(HANDLE) { return true; }
inline bool ReadProcessMemory(HANDLE, const void*, void*, SIZE_T, SIZE_T*) { return false; }
