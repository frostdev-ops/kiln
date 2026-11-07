// SPDX-License-Identifier: MIT
// Host-only shim for sigdb.cpp's diagnostic APIs. No production build includes this directory.
#pragma once
#include <cstddef>
#include <cstdint>
using HMODULE = void*;
using LPCWSTR = const wchar_t*;
using DWORD = unsigned long;
struct MEMORY_BASIC_INFORMATION { void* BaseAddress; size_t RegionSize; unsigned State, Protect; };
inline constexpr unsigned MEM_COMMIT = 0x1000, PAGE_NOACCESS = 1, PAGE_GUARD = 0x100;
inline constexpr unsigned GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 4,
                          GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 2;
inline constexpr unsigned MAX_PATH = 260;
inline MEMORY_BASIC_INFORMATION test_mbi{};
inline bool test_query = false;
inline size_t VirtualQuery(const void*, MEMORY_BASIC_INFORMATION* out, size_t n) {
    if (!test_query) return 0;
    *out = test_mbi;
    return n;
}
inline bool GetModuleHandleExW(unsigned, LPCWSTR, HMODULE*) { return false; }
inline DWORD GetModuleFileNameW(HMODULE, wchar_t*, DWORD) { return 0; }
