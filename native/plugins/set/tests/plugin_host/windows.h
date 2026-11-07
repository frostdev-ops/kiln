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
}
inline DWORD GetLastError() { return ERROR_NO_MORE_FILES; }
inline UINT GetSystemDirectoryW(wchar_t* out, UINT n) {
    if (loader_test::system.size() >= n) return n;
    std::wcscpy(out, loader_test::system.c_str()); return loader_test::system.size();
}
inline bool GetModuleHandleExW(DWORD, LPCWSTR, HMODULE* out) { *out = reinterpret_cast<void*>(1); return true; }
inline HMODULE GetModuleHandleW(LPCWSTR) { return nullptr; }
inline void* GetProcAddress(HMODULE h, const char*) {
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
