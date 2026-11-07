// SPDX-License-Identifier: MIT
// A stand-in for Enshwick's loader in kiln-enshwick's host tests: exports enshwick_start with the loader's signature,
// records how it was called and what <game>/mods holds into <game>/logs/enshwick_test.log (named like the loader's
// own logs), then calls release as the loader does once its hooks are armed.
// ENSHWICK_FAKE=slow waits 11 s before release (past the hold); ENSHWICK_FAKE=norelease never calls it.
#include <windows.h>

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>

static std::wstring game_dir() {
    wchar_t exe[MAX_PATH] = L"";
    GetModuleFileNameW(nullptr, exe, MAX_PATH);
    std::wstring p(exe);
    return p.substr(0, p.find_last_of(L'\\'));
}

static std::string narrow(const std::wstring& w) {
    std::string s;
    for (wchar_t c : w) s += c < 128 ? static_cast<char>(c) : '?';
    return s;
}

extern "C" __declspec(dllexport) void __stdcall enshwick_start(uint32_t stub_abi, uint32_t missing,
                                                               uint32_t(__stdcall* release)()) {
    const std::wstring game = game_dir();
    CreateDirectoryW((game + L"\\logs").c_str(), nullptr);
    FILE* f = _wfopen((game + L"\\logs\\enshwick_test.log").c_str(), L"w");
    if (!f) return;
    wchar_t self[MAX_PATH] = L"";
    HMODULE me = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       reinterpret_cast<LPCWSTR>(&enshwick_start), &me);
    GetModuleFileNameW(me, self, MAX_PATH);
    std::fprintf(f, "INFO enshwick: Enshwick fake starting on Wine, stub_abi=%u missing=%u loader=%s\n", stub_abi, missing,
                 narrow(self).c_str());
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW((game + L"\\mods\\*").c_str(), &fd);
    int count = 0;
    if (h != INVALID_HANDLE_VALUE) {
        do {
            const std::wstring n = fd.cFileName;
            if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || n == L"." || n == L".." || _wcsicmp(n.c_str(), L"enshwick") == 0)
                continue;
            std::fprintf(f, "INFO enshwick: mod folder %s initialised\n", narrow(n).c_str());
            ++count;
        } while (FindNextFileW(h, &fd));
        FindClose(h);
    }
    char mode[32] = "";
    GetEnvironmentVariableA("ENSHWICK_FAKE", mode, sizeof mode);
    if (!strcmp(mode, "slow")) Sleep(11000);
    if (strcmp(mode, "norelease") != 0) {
        const uint32_t r = release ? release() : 0;
        std::fprintf(f, "INFO enshwick: release=%u%s\n", r,
                     r == 1 ? " the server's own code starts after the hooks" : r == 2 ? " the server's own code started before the hooks" : "");
    }
    std::fprintf(f, "INFO enshwick: ready: %d of %d mods running\n", count, count);
    std::fclose(f);
}

BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
