// SPDX-License-Identifier: MIT
// Host-only Win32 shim for the service lifecycle test. No image is mapped and no game code runs.
#pragma once
#include <cstdint>
#include <cstdio>
using DWORD = uint32_t;
struct IMAGE_DOS_HEADER { int32_t e_lfanew; };
struct IMAGE_NT_HEADERS64 {
    struct { uint16_t NumberOfSections; } FileHeader;
    struct { uint32_t SizeOfImage; } OptionalHeader;
};
struct IMAGE_SECTION_HEADER { char Name[8]; uint32_t VirtualAddress; struct { uint32_t VirtualSize; } Misc; };
struct MEMORY_BASIC_INFORMATION { void* BaseAddress; size_t RegionSize; DWORD State, Protect; };
constexpr DWORD PAGE_READONLY = 2, PAGE_READWRITE = 4, PAGE_WRITECOPY = 8, PAGE_EXECUTE_READ = 32,
    PAGE_EXECUTE_READWRITE = 64, PAGE_EXECUTE_WRITECOPY = 128, PAGE_GUARD = 256, PAGE_NOACCESS = 1, MEM_COMMIT = 4096;
constexpr DWORD MOVEFILE_REPLACE_EXISTING = 1;
inline void* GetModuleHandleW(const void*) { return nullptr; }
inline size_t VirtualQuery(const void*, MEMORY_BASIC_INFORMATION*, size_t) { return 0; }
inline IMAGE_SECTION_HEADER* IMAGE_FIRST_SECTION(const IMAGE_NT_HEADERS64*) { return nullptr; }
inline bool MoveFileExW(const char* from, const char* to, DWORD) { return std::rename(from, to) == 0; }
inline uint32_t GetCurrentProcessId() { return 42; }
