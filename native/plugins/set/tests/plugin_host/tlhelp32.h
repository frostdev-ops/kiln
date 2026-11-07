#pragma once
#include "windows.h"
#define TH32CS_SNAPMODULE 8
#define TH32CS_SNAPMODULE32 16
struct MODULEENTRY32W { DWORD dwSize; HMODULE hModule; wchar_t szExePath[MAX_PATH]; };
inline HANDLE CreateToolhelp32Snapshot(DWORD, DWORD) { loader_test::cursor = 0; return reinterpret_cast<void*>(2); }
inline bool Module32NextW(HANDLE, MODULEENTRY32W* out) {
    if (loader_test::cursor >= loader_test::modules.size()) return false;
    out->hModule = reinterpret_cast<void*>(loader_test::cursor++ + 2); return true;
}
inline bool Module32FirstW(HANDLE h, MODULEENTRY32W* out) { return Module32NextW(h, out); }
