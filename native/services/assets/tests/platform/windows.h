// SPDX-License-Identifier: MIT
// Host-only Win32 shim for kiln.assets' store test: what store.cpp and util.cpp name, no more. No file is read through it and no game
// code runs: CreateFileW always fails, the wide-string calls copy ASCII.
#pragma once
#include <cctype>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <thread>

using DWORD = uint32_t;
using ULONG = uint32_t;
using BOOL = int;
using PUCHAR = unsigned char*;
using HANDLE = void*;
union LARGE_INTEGER {
    int64_t QuadPart;
};
#define INVALID_HANDLE_VALUE (reinterpret_cast<HANDLE>(static_cast<intptr_t>(-1)))
constexpr DWORD GENERIC_READ = 0x80000000u, FILE_SHARE_READ = 1, OPEN_EXISTING = 3, FILE_ATTRIBUTE_NORMAL = 0x80;
constexpr unsigned CP_UTF8 = 65001;

inline void YieldProcessor() {}
inline void Sleep(DWORD) { std::this_thread::yield(); }
inline HANDLE CreateFileW(const wchar_t*, DWORD, DWORD, void*, DWORD, DWORD, void*) { return INVALID_HANDLE_VALUE; }
inline BOOL GetFileSizeEx(HANDLE, LARGE_INTEGER* size) {
    size->QuadPart = 0;
    return 0;
}
inline BOOL ReadFile(HANDLE, void*, DWORD, DWORD*, void*) { return 0; }
inline BOOL CloseHandle(HANDLE) { return 1; }
inline int MultiByteToWideChar(unsigned, DWORD, const char* s, int n, wchar_t* out, int cap) {
    if (out)
        for (int i = 0; i < n && i < cap; ++i) out[i] = static_cast<unsigned char>(s[i]);
    return n;
}
inline int WideCharToMultiByte(unsigned, DWORD, const wchar_t* s, int n, char* out, int cap, const char*, BOOL*) {
    if (out)
        for (int i = 0; i < n && i < cap; ++i) out[i] = static_cast<char>(s[i]);
    return n;
}
