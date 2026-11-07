// Host-only declarations used to compile the real service adapters without
// Windows.
#pragma once
#include <cstddef>
using HMODULE = void *;
using LPCWSTR = const wchar_t *;
constexpr unsigned GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 4, GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 2;
constexpr size_t MAX_PATH = 260;
inline bool GetModuleHandleExW(unsigned, LPCWSTR, HMODULE *) { return false; }
inline unsigned GetModuleFileNameW(HMODULE, wchar_t *, unsigned) { return 0; }
