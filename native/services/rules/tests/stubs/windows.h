// SPDX-License-Identifier: MIT
// Host execution of the creative hooks. C++ exceptions stand in for SEH; the Windows build checks the SEH path.
#pragma once
#include <cstdint>
#define __fastcall
#define __try try
#define __except(...) catch (...)
#define EXCEPTION_EXECUTE_HANDLER 1
using HMODULE = void*;
inline HMODULE GetModuleHandleW(const wchar_t*) { return nullptr; }
inline void* GetProcAddress(HMODULE, const char*) { return nullptr; }
// kiln.items' pide.cpp (built here by pide_test) seeds its per-thread roll from these
inline uint32_t GetCurrentThreadId() { return 1; }
inline uint64_t GetTickCount64() { return 0; }
inline thread_local uintptr_t test_return_address = 0;
#define _ReturnAddress() reinterpret_cast<void*>(test_return_address)
