// SPDX-License-Identifier: MIT
// Shared by the proxyhost pieces: one line appended to %PROXYHOST_LOG% (UTF-8), whoever writes it.
#pragma once
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

#include <cstdarg>

inline void ph_log(const char* fmt, ...) {
    wchar_t path[1024];
    const DWORD n = GetEnvironmentVariableW(L"PROXYHOST_LOG", path, 1024);
    if (n == 0 || n >= 1024) return;
    char line[2048];
    va_list ap;
    va_start(ap, fmt);
    int k = wvsprintfA(line, fmt, ap);
    va_end(ap);
    if (k < 0) return;
    line[k++] = '\n';
    HANDLE h = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;
    DWORD written = 0;
    WriteFile(h, line, static_cast<DWORD>(k), &written, nullptr);
    CloseHandle(h);
}

inline unsigned char* ph_entry() {
    auto* image = reinterpret_cast<unsigned char*>(GetModuleHandleW(nullptr));
    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(image);
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(image + dos->e_lfanew);
    return image + nt->OptionalHeader.AddressOfEntryPoint;
}
