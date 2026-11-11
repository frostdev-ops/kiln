// SPDX-License-Identifier: MIT
// Kiln dinput8.dll proxy.
//
// The game (enshrouded.exe and enshrouded_server.exe) imports one function from DINPUT8 (DirectInput8Create), so this
// DLL next to the exe is loaded into every start (Wine: GE-Proton loads it as is; older Proton builds need
// WINEDLLOVERRIDES=dinput8=n,b). Every dinput8 export forwards to the system DLL.
//
// DllMain, in this order (everything under the loader lock, before any game code):
//   0. The game's crash reporter (its own exe started again, "--upload-dump <dir> --process_duration <t>", on the client
//      and the dedicated server): nothing at all. It only uploads a dump and shows a dialog; a core here would rotate
//      kiln.log away from the session that crashed, and play-once.ini and proxy.log are not its to touch.
//   1. KILN_DISABLE=1: nothing at all (no chain, no gate): the game runs vanilla.
//   2. <game>/kiln/play-once.ini: read, then deleted at once (a flag that cannot be deleted is ignored, so it can never
//      stick). Ignored when older than 600 s, in the future, invalid, or on a dedicated server.
//        mode=vanilla  no chain, no gate: this start is vanilla.
//        mode=no-mods  the chain is listed but not loaded (SKIPPED); the core boots without any mod.
//        mode=profile  kiln/chain/once.ini instead of kiln/chain.ini [profile]; the core boots from kiln/profile.once.json.
//                      once.ini may carry its own [chain] and [late] (dev.21): the play-once profile's compat rules
//                      applied. A section it has replaces chain.ini's for this start; one it lacks leaves chain.ini's.
//   3. Chain loading (docs/kiln/launcher/contracts.md, "chain.ini"), in this order:
//        kiln/chain.ini [chain]    proxies the installer displaced (EMF's loader, unknown dinput8 mods)
//        <game>/dinput8-chain.ini  EMF's chain file, [Chain], [EMF] (the DLLs EMF 0.5's installer renamed) and lines
//                                  before any section, in file order, EMF's rules
//        kiln/chain.ini [profile]  native mods of the profile with phase "dllmain"
//      Each DLL: resolved, deduplicated by canonical path (GetFinalPathNameByHandleW), checked through a read-only file
//      mapping (never loaded for that) and refused when it is this proxy, the system dinput8, another Kiln proxy or
//      ShroudForge's dinput8 (EMF's loader, which also exports ShroudForge's chain-host name, is accepted), then LoadLibraryExW(LOAD_WITH_ALTERED_SEARCH_PATH) under an SEH guard. Never FreeLibrary.
//      The results go to the core in KilnBootInfo.chain (and kiln/logs/proxy.log). Before the first DLL loads, the length
//      of <game>\shroudlink_client.log goes to KilnBootInfo.addon_log_size (v3): where that addon's lines of this start begin.
//      kiln/chain.ini [late] (dev.21): an entry of the lists above whose DLL is also named in [late] passes the same
//      checks but is not loaded: its entry gets status KILN_CHAIN_LATE and kiln-core loads it after Kiln's services,
//      plugins and native mods ran (a closed-source DLL that hooks the same functions then hooks over Kiln instead of
//      under it). Matching is by canonical path (the resolved path when the file is missing), in every mode (also
//      play-once mode=profile). A [late] line that names no entry loads nothing (proxy.log says so). An older proxy
//      ignores the unknown section and loads the entry as before. One limit: EMF's own loader (a [chain] entry) is
//      never deferred, [late] or not: it starts EMF itself, and kiln-emf decides when the plugins start whether the
//      real EMF is in the process. shroudlink's client addon (shroudlink_shutdown + DirectInput8Create exports) is never
//      deferred either: its live-code scans must finish before Kiln hooks. A late DLL may patch the entry (step 4).
//   4. Entry gate (technique from ShroudForge's windows-bootstrap/startup_gate.h, MIT): the first 14 bytes of the exe's
//      entry point become a jump to `gate_entry`. Installed after the chain, so an entry patch made by a chained DLL
//      (EMF holds a server's start this way) is what the gate saves as "original": at the entry, Kiln's gate runs first,
//      puts those bytes back and jumps there, so the chained DLL's patch runs next and unwinds to the real entry
//      (last in, first out).
// The gate runs on the main thread after the loader lock is released and before any game code: it restores the entry
// bytes, loads <game>\kiln\kiln-core.dll (or %KILN_CORE%), calls kiln_boot with KilnBootInfo v3 and continues into the
// entry point. The bytes go back before the core starts, so a DLL the core loads (a [late] entry, a native mod) that
// patches the entry point saves the right bytes and its patch runs first when the gate continues: last in, first out
// again. If anything fails the game starts vanilla.
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

#include <cstdarg>
#include <cstddef>
#include <cstdint>
#include <cstring>

#include "../core/abi/kiln_api.h"
#include "kiln_version.h"

// KilnProxyInfo (kiln_api.h): the installer's classify_proxy finds the marker text "KilnProxy/<abi> <version>" in the
// file without loading it; a loaded proxy answers the KilnProxyInfo export.
#define KILN_PROXY_MARKER "KilnProxy/2 " KILN_VERSION
static_assert(KILN_PROXY_ABI == 2u, "update KILN_PROXY_MARKER");

namespace {

constexpr DWORD kPath = 4096;           // wide chars for any path
constexpr uint32_t kMaxPerList = 256;   // EMF's limit for dinput8-chain.ini; the same for each chain.ini section
constexpr DWORD kMaxIni = 256 * 1024;   // bytes; larger files are refused
constexpr int64_t kPlayOnceMaxAge = 600;  // s
constexpr int64_t kPlayOnceFutureSlack = 60;  // s of clock skew tolerated for "created" in the future

const char kMarker[] = KILN_PROXY_MARKER;  // referenced by kProxyInfo, so the linker keeps it
const KilnProxyInfoData kProxyInfo = {
    sizeof(KilnProxyInfoData), KILN_PROXY_ABI, {KILN_VERSION, sizeof(KILN_VERSION) - 1},
    {kMarker, sizeof(KILN_PROXY_MARKER) - 1}, sizeof(KilnBootInfo), 0,
};

HMODULE g_self = nullptr;
HMODULE g_real = nullptr;
INIT_ONCE g_real_once = INIT_ONCE_STATIC_INIT;

unsigned char* g_entry = nullptr;
unsigned char g_original[14]{};

wchar_t g_game[kPath];  // directory of the exe, no trailing backslash
wchar_t g_kiln[kPath];  // <game>\kiln
bool g_server = false;

KilnBootInfo g_info{};
KilnChainEntry* g_chain = nullptr;    // every listed DLL, in load order (HeapAlloc'd, lives for the process)
const wchar_t** g_canon = nullptr;    // canonical path per entry (nullptr when unknown)
uint32_t g_count = 0, g_capacity = 0;
const wchar_t* g_self_canon = nullptr;
const wchar_t* g_system_canon = nullptr;
const wchar_t* g_game_canon = nullptr;

HANDLE g_log = INVALID_HANDLE_VALUE;  // kiln\logs\proxy.log, this start only

// ---- small helpers (no CRT allocation; HeapAlloc is fine under the loader lock) ----

void* halloc(size_t n) { return HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, n); }

size_t wlen(const wchar_t* s) {
    size_t n = 0;
    while (s && s[n]) ++n;
    return n;
}

wchar_t* wdup(const wchar_t* s) {
    const size_t n = wlen(s);
    auto* d = static_cast<wchar_t*>(halloc((n + 1) * sizeof(wchar_t)));
    if (d) memcpy(d, s, n * sizeof(wchar_t));
    return d;
}

// UTF-8 copy for the core (NUL-terminated; lives for the process).
KilnStr utf8(const wchar_t* w) {
    const int wn = static_cast<int>(wlen(w));
    if (wn == 0) return KilnStr{"", 0};
    const int n = WideCharToMultiByte(CP_UTF8, 0, w, wn, nullptr, 0, nullptr, nullptr);
    auto* s = static_cast<char*>(halloc(static_cast<size_t>(n) + 1));
    if (!s) return KilnStr{"", 0};
    WideCharToMultiByte(CP_UTF8, 0, w, wn, s, n, nullptr, nullptr);
    return KilnStr{s, static_cast<size_t>(n)};
}

bool wieq(const wchar_t* a, const wchar_t* b) {
    return CompareStringOrdinal(a, -1, b, -1, TRUE) == CSTR_EQUAL;
}

bool ends_with_i(const wchar_t* s, const wchar_t* suffix) {
    const size_t n = wlen(s), m = wlen(suffix);
    return n >= m && CompareStringOrdinal(s + n - m, static_cast<int>(m), suffix, static_cast<int>(m), TRUE) == CSTR_EQUAL;
}

void trace(const wchar_t* fmt, ...) {
    wchar_t msg[1024];
    va_list ap;
    va_start(ap, fmt);
    wvsprintfW(msg, fmt, ap);
    va_end(ap);
    OutputDebugStringW(msg);
}

// One line into kiln\logs\proxy.log (and the debugger). wvsprintfW format (%s = wide string).
void plog(const wchar_t* fmt, ...) {
    wchar_t msg[1024];
    va_list ap;
    va_start(ap, fmt);
    const int n = wvsprintfW(msg, fmt, ap);
    va_end(ap);
    OutputDebugStringW(msg);
    if (g_log == INVALID_HANDLE_VALUE || n <= 0) return;
    char out[3200];
    int k = WideCharToMultiByte(CP_UTF8, 0, msg, n, out, sizeof out - 2, nullptr, nullptr);
    if (k <= 0) return;
    out[k++] = '\r';
    out[k++] = '\n';
    DWORD written = 0;
    WriteFile(g_log, out, static_cast<DWORD>(k), &written, nullptr);
}

void open_log() {
    if (GetFileAttributesW(g_kiln) == INVALID_FILE_ATTRIBUTES) return;  // no kiln\ folder: write nothing anywhere
    wchar_t path[kPath];
    lstrcpynW(path, g_kiln, kPath - 32);
    lstrcatW(path, L"\\logs");
    CreateDirectoryW(path, nullptr);
    lstrcatW(path, L"\\proxy.log");
    g_log = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr, CREATE_ALWAYS,
                        FILE_ATTRIBUTE_NORMAL, nullptr);
    SYSTEMTIME t;
    GetLocalTime(&t);
    plog(L"Kiln proxy %S (dinput8.dll, pid %lu) %04u-%02u-%02u %02u:%02u:%02u", KILN_VERSION, GetCurrentProcessId(),
         t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond);
}

// ---- forwarding to the system dinput8 ----

BOOL CALLBACK load_real(PINIT_ONCE, void*, void**) {
    wchar_t path[MAX_PATH];
    const UINT n = GetSystemDirectoryW(path, MAX_PATH);
    if (n == 0 || n > MAX_PATH - 16) return FALSE;
    lstrcatW(path, L"\\dinput8.dll");
    g_real = LoadLibraryW(path);
    return g_real != nullptr;
}

FARPROC real(const char* name) {
    InitOnceExecuteOnce(&g_real_once, load_real, nullptr, nullptr);
    return g_real ? GetProcAddress(g_real, name) : nullptr;
}

// ---- the exe: folder and side ----

bool init_game_paths() {
    wchar_t exe[kPath];
    const DWORD n = GetModuleFileNameW(nullptr, exe, kPath);
    if (n == 0 || n >= kPath - 32) return false;
    wchar_t* slash = nullptr;
    for (wchar_t* p = exe; *p; ++p)
        if (*p == L'\\' || *p == L'/') slash = p;
    if (!slash) return false;
    const wchar_t* name = slash + 1;
    *slash = 0;
    lstrcpyW(g_game, exe);
    lstrcpyW(g_kiln, exe);
    lstrcatW(g_kiln, L"\\kiln");
    // Server or client, as the core decides it (image.cpp): the PDB name in the exe's CodeView record, else the exe
    // name. Only the server matters here (play-once is a client feature).
    bool known = false;
    auto* base = reinterpret_cast<const unsigned char*>(GetModuleHandleW(nullptr));
    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    if (base && dos->e_magic == IMAGE_DOS_SIGNATURE) {
        const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
        const auto& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_DEBUG];
        if (nt->Signature == IMAGE_NT_SIGNATURE && dir.VirtualAddress && dir.Size) {
            const auto* d = reinterpret_cast<const IMAGE_DEBUG_DIRECTORY*>(base + dir.VirtualAddress);
            for (size_t i = 0; i < dir.Size / sizeof(IMAGE_DEBUG_DIRECTORY) && !known; ++i, ++d) {
                if (d->Type != IMAGE_DEBUG_TYPE_CODEVIEW || !d->AddressOfRawData || d->SizeOfData < 25) continue;
                const auto* cv = base + d->AddressOfRawData;
                if (memcmp(cv, "RSDS", 4) != 0) continue;
                const char* path = reinterpret_cast<const char*>(cv + 24);
                const char* file = path;
                for (const char* p = path; *p && p < reinterpret_cast<const char*>(cv) + d->SizeOfData; ++p)
                    if (*p == '\\' || *p == '/') file = p + 1;
                if (lstrcmpiA(file, "enshrouded_server.pdb") == 0) g_server = known = true;
                else if (lstrcmpiA(file, "enshrouded.pdb") == 0) known = true;
            }
        }
    }
    if (!known) g_server = wieq(name, L"enshrouded_server.exe") || !wieq(name, L"enshrouded.exe");
    return true;
}

// ---- text files: UTF-16LE with BOM, UTF-8 with or without BOM, else the ANSI code page ----

wchar_t* read_text(const wchar_t* path, DWORD* error) {
    *error = 0;
    HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) {
        *error = GetLastError();
        return nullptr;
    }
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(h, &size) || size.QuadPart > kMaxIni) {
        *error = ERROR_FILE_TOO_LARGE;
        CloseHandle(h);
        return nullptr;
    }
    const DWORD n = static_cast<DWORD>(size.QuadPart);
    auto* bytes = static_cast<unsigned char*>(halloc(n + 2));
    DWORD got = 0;
    const BOOL ok = bytes && ReadFile(h, bytes, n, &got, nullptr) && got == n;
    CloseHandle(h);
    if (!ok) {
        *error = bytes ? GetLastError() : ERROR_OUTOFMEMORY;
        return nullptr;
    }
    if (n >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) {  // UTF-16LE
        const DWORD chars = (n - 2) / 2;
        auto* w = static_cast<wchar_t*>(halloc((chars + 1) * sizeof(wchar_t)));
        if (w) memcpy(w, bytes + 2, chars * sizeof(wchar_t));
        return w;
    }
    const unsigned char* p = bytes;
    DWORD len = n;
    if (len >= 3 && p[0] == 0xEF && p[1] == 0xBB && p[2] == 0xBF) p += 3, len -= 3;
    UINT cp = CP_UTF8;
    int chars = len ? MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(p),
                                          static_cast<int>(len), nullptr, 0)
                    : 0;
    if (len && chars == 0) {
        cp = CP_ACP;
        chars = MultiByteToWideChar(CP_ACP, 0, reinterpret_cast<const char*>(p), static_cast<int>(len), nullptr, 0);
    }
    auto* w = static_cast<wchar_t*>(halloc((static_cast<size_t>(chars) + 1) * sizeof(wchar_t)));
    if (w && chars) MultiByteToWideChar(cp, 0, reinterpret_cast<const char*>(p), static_cast<int>(len), w, chars);
    return w;
}

// Splits `text` into trimmed lines in place; calls fn(line) for each (lines may be empty).
template <typename F>
void each_line(wchar_t* text, F&& fn) {
    wchar_t* p = text;
    while (p && *p) {
        wchar_t* end = p;
        while (*end && *end != L'\n') ++end;
        const bool last = *end == 0;
        *end = 0;
        wchar_t* s = p;
        while (*s == L' ' || *s == L'\t' || *s == 0xFEFF) ++s;
        wchar_t* e = s + wlen(s);
        while (e > s && (e[-1] == L' ' || e[-1] == L'\t' || e[-1] == L'\r')) *--e = 0;
        fn(s);
        if (last) break;
        p = end + 1;
    }
}

bool is_comment(const wchar_t* s) { return *s == L';' || *s == L'#' || (s[0] == L'/' && s[1] == L'/'); }

// Lower-case copy of s[0, n) without leading and trailing blanks.
void lower_trimmed(const wchar_t* s, size_t n, wchar_t* out, size_t cap) {
    while (n && (*s == L' ' || *s == L'\t')) ++s, --n;
    while (n && (s[n - 1] == L' ' || s[n - 1] == L'\t')) --n;
    size_t k = 0;
    for (size_t i = 0; i < n && k + 1 < cap; ++i) {
        wchar_t c = s[i];
        if (c >= L'A' && c <= L'Z') c = static_cast<wchar_t>(c - L'A' + L'a');
        out[k++] = c;
    }
    out[k] = 0;
}

// "[name]" -> name (trimmed, lower-case, into out); false when the line is not a section header.
bool section_of(const wchar_t* s, wchar_t* out, size_t cap) {
    const size_t n = wlen(s);
    if (n < 2 || s[0] != L'[' || s[n - 1] != L']') return false;
    lower_trimmed(s + 1, n - 2, out, cap);
    return true;
}

// "key = value" -> value (trimmed); a line without '=' is its own value. (schema/fixtures/ini/kini.py)
wchar_t* value_of(wchar_t* s) {
    wchar_t* eq = s;
    while (*eq && *eq != L'=') ++eq;
    if (*eq == L'=') {
        s = eq + 1;
        while (*s == L' ' || *s == L'\t') ++s;
    }
    return s;
}

// "key = value": the key (trimmed, lower-case) and the value; false for lines without '='.
bool key_value(wchar_t* s, wchar_t* key, size_t cap, wchar_t** value) {
    wchar_t* eq = s;
    while (*eq && *eq != L'=') ++eq;
    if (*eq != L'=') return false;
    lower_trimmed(s, static_cast<size_t>(eq - s), key, cap);
    *value = value_of(s);
    return true;
}

// ---- paths ----

bool sep(wchar_t c) { return c == L'\\' || c == L'/'; }

// Kiln's files (chain.ini, once.ini, play-once.ini profile=): absolute = a drive letter and a separator (Z:\...,
// C:\...) or UNC (\\server\...); anything else is relative to <game>\kiln\.
bool is_absolute(const wchar_t* s) {
    const bool drive = ((s[0] >= L'A' && s[0] <= L'Z') || (s[0] >= L'a' && s[0] <= L'z')) && s[1] == L':' && sep(s[2]);
    return drive || (sep(s[0]) && sep(s[1]));
}

// EMF's dinput8-chain.ini takes paths inside the game folder only: no root, drive letter, UNC or '..'.
bool emf_outside(const wchar_t* s);

bool has_dotdot(const wchar_t* s) {
    for (const wchar_t* p = s; *p; ++p) {
        const bool start = p == s || p[-1] == L'\\' || p[-1] == L'/';
        if (start && p[0] == L'.' && p[1] == L'.' && (p[2] == 0 || p[2] == L'\\' || p[2] == L'/')) return true;
    }
    return false;
}

bool emf_outside(const wchar_t* s) {
    if (sep(s[0]) || has_dotdot(s)) return true;
    for (const wchar_t* p = s; *p; ++p)
        if (*p == L':') return true;
    return false;
}

// base\rel (or rel when absolute), normalized by GetFullPathNameW. '/' becomes '\'.
bool resolve(const wchar_t* base, const wchar_t* rel, wchar_t* out) {
    wchar_t joined[kPath];
    if (is_absolute(rel)) {
        if (wlen(rel) >= kPath) return false;
        lstrcpyW(joined, rel);
    } else {
        if (wlen(base) + wlen(rel) + 2 >= kPath) return false;
        lstrcpyW(joined, base);
        lstrcatW(joined, L"\\");
        lstrcatW(joined, rel);
    }
    for (wchar_t* p = joined; *p; ++p)
        if (*p == L'/') *p = L'\\';
    const DWORD n = GetFullPathNameW(joined, kPath, out, nullptr);
    return n > 0 && n < kPath;
}

// The file's final path (symlinks, 8.3 names, case and drive mappings resolved); nullptr when it cannot be opened.
const wchar_t* canonical(const wchar_t* path) {
    HANDLE h = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                           FILE_FLAG_BACKUP_SEMANTICS, nullptr);
    if (h == INVALID_HANDLE_VALUE) return nullptr;
    wchar_t buf[kPath];
    const DWORD n = GetFinalPathNameByHandleW(h, buf, kPath, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    CloseHandle(h);
    if (n == 0 || n >= kPath) return nullptr;
    const wchar_t* p = buf;
    if (wcsncmp(buf, L"\\\\?\\UNC\\", 8) == 0) {
        buf[6] = L'\\';  // \\?\UNC\server\share -> \\server\share
        p = buf + 6;
    } else if (wcsncmp(buf, L"\\\\?\\", 4) == 0) {
        p = buf + 4;
    }
    return wdup(p);
}

// ---- refusals: read-only file mapping, never LoadLibrary ----

// KilnChainEntry.detail for REFUSED (KILN_CHAIN_REFUSED_*); kAccept = load it.
enum Refusal : uint32_t {
    kAccept = 0,
    kSelf = KILN_CHAIN_REFUSED_SELF,
    kSystem = KILN_CHAIN_REFUSED_SYSTEM,
    kKiln = KILN_CHAIN_REFUSED_KILN,
    kShroudForge = KILN_CHAIN_REFUSED_SHROUDFORGE,
    kNotDll = KILN_CHAIN_REFUSED_NOT_DLL,
    kOutside = KILN_CHAIN_REFUSED_OUTSIDE,
    kTooMany = KILN_CHAIN_REFUSED_TOO_MANY,
};

const wchar_t* refusal_text(uint32_t r) {
    switch (r) {
        case kSelf: return L"this proxy itself";
        case kSystem: return L"the system dinput8";
        case kKiln: return L"a Kiln proxy or core";
        case kShroudForge: return L"ShroudForge's dinput8 (it runs through its dbghelp.dll)";
        case kNotDll: return L"not a 64-bit PE DLL";
        case kOutside: return L"outside the game folder (dinput8-chain.ini takes relative paths only)";
        case kTooMany: return L"more than 256 entries";
        default: return L"refused";
    }
}

bool contains(const unsigned char* hay, size_t n, const void* needle, size_t m) {
    const auto* nd = static_cast<const unsigned char*>(needle);
    if (m == 0 || n < m) return false;
    const unsigned char first = nd[0];
    const unsigned char* end = hay + n - m + 1;
    for (const unsigned char* p = hay; p < end;) {
        p = static_cast<const unsigned char*>(memchr(p, first, static_cast<size_t>(end - p)));
        if (!p) return false;
        if (memcmp(p, nd, m) == 0) return true;
        ++p;
    }
    return false;
}

// All export inspection uses the on-disk mapping. No LoadLibrary/GetProcAddress during classification.
struct FilePe {
    const unsigned char* bytes;
    size_t size;
    const IMAGE_NT_HEADERS64* nt;
    const IMAGE_SECTION_HEADER* sections;
    const unsigned char* rva(DWORD address, size_t need, size_t* remaining = nullptr) const {
        for (WORD i = 0; i < nt->FileHeader.NumberOfSections; ++i) {
            const auto& s = sections[i];
            if (address < s.VirtualAddress) continue;
            const size_t delta = static_cast<size_t>(address - s.VirtualAddress);
            if (delta >= s.SizeOfRawData || need > s.SizeOfRawData - delta) continue;
            if (remaining) *remaining = s.SizeOfRawData - delta;
            return bytes + s.PointerToRawData + delta;
        }
        const size_t headers = nt->OptionalHeader.SizeOfHeaders;
        if (headers <= size && address < headers && need <= headers - address) {
            if (remaining) *remaining = headers - address;
            return bytes + address;
        }
        return nullptr;
    }
};

bool export_flags(const unsigned char* b, size_t n, unsigned* flags) {
    *flags = 0;
    if (n < sizeof(IMAGE_DOS_HEADER)) return false;
    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(b);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0) return false;
    const size_t at = static_cast<size_t>(dos->e_lfanew);
    const size_t fixed = offsetof(IMAGE_NT_HEADERS64, OptionalHeader);
    if (at > n || fixed > n - at) return false;
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(b + at);
    const size_t opt = nt->FileHeader.SizeOfOptionalHeader;
    const size_t dirs = offsetof(IMAGE_OPTIONAL_HEADER64, DataDirectory);
    if (nt->Signature != IMAGE_NT_SIGNATURE || nt->FileHeader.Machine != IMAGE_FILE_MACHINE_AMD64 ||
        !(nt->FileHeader.Characteristics & IMAGE_FILE_DLL) || opt < dirs || opt > n - at - fixed ||
        nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC ||
        nt->OptionalHeader.NumberOfRvaAndSizes > (opt - dirs) / sizeof(IMAGE_DATA_DIRECTORY)) return false;
    const size_t table = at + fixed + opt;
    if (nt->FileHeader.NumberOfSections > (n - table) / sizeof(IMAGE_SECTION_HEADER)) return false;
    const auto* sections = reinterpret_cast<const IMAGE_SECTION_HEADER*>(b + table);
    for (WORD i = 0; i < nt->FileHeader.NumberOfSections; ++i) {
        if (sections[i].PointerToRawData > n || sections[i].SizeOfRawData > n - sections[i].PointerToRawData) return false;
    }
    const FilePe pe{b, n, nt, sections};
    if (!nt->OptionalHeader.NumberOfRvaAndSizes) return true;
    const DWORD address = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT].VirtualAddress;
    if (!address) return true;
    const auto* ed = reinterpret_cast<const IMAGE_EXPORT_DIRECTORY*>(pe.rva(address, sizeof(IMAGE_EXPORT_DIRECTORY)));
    if (!ed || ed->NumberOfNames > 65536) return false;
    if (!ed->NumberOfNames) return true;
    const auto* names = pe.rva(ed->AddressOfNames, static_cast<size_t>(ed->NumberOfNames) * sizeof(DWORD));
    if (!names) return false;
    for (DWORD i = 0; i < ed->NumberOfNames; ++i) {
        DWORD name_rva;
        memcpy(&name_rva, names + i * sizeof(DWORD), sizeof(DWORD));
        size_t left = 0;
        const auto* name = reinterpret_cast<const char*>(pe.rva(name_rva, 1, &left));
        if (!name || !memchr(name, 0, left < 1024 ? left : 1024)) return false;
        if (!strcmp(name, "KilnProxyInfo") || !strcmp(name, "kiln_boot")) *flags |= 1;
        if (!strcmp(name, "emf_loader_identity") || !strcmp(name, "emf_loader_play_once")) *flags |= 2;
        if (!strcmp(name, "ShroudforgeSetDinput8Provider")) *flags |= 4;
        if (!strcmp(name, "shroudlink_shutdown")) *flags |= 8;
        if (!strcmp(name, "DirectInput8Create")) *flags |= 16;
    }
    return true;
}

// ASCII or UTF-16LE text markers, ASCII case ignored; sizes come from the literal, never hand-written lengths.
template <size_t N>
bool marker(const unsigned char* b, size_t n, const char (&text)[N]) {
    for (size_t stride = 1; stride <= 2; ++stride) {
        const size_t len = (N - 1) * stride;
        for (size_t at = 0; len <= n && at <= n - len; ++at) {
            bool same = true;
            for (size_t j = 0; j < N - 1 && same; ++j) {
                unsigned char c = b[at + j * stride];
                unsigned char t = static_cast<unsigned char>(text[j]);
                if (c >= 'A' && c <= 'Z') c += 'a' - 'A';
                if (t >= 'A' && t <= 'Z') t += 'a' - 'A';
                same = c == t && (stride == 1 || b[at + j * stride + 1] == 0);
            }
            if (same) return true;
        }
    }
    return false;
}

// `emf_loader` (optional): set when the DLL is EMF's own loader (its exports or marker text).
// `gated_addon`: same two-export identity as kiln-manage's is_gated_addon; never defer its live-code scans.
uint32_t classify(const wchar_t* path, bool* emf_loader = nullptr, bool* gated_addon = nullptr) {
    HANDLE f = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) return kAccept;  // LoadLibrary reports the real error
    LARGE_INTEGER size{};
    GetFileSizeEx(f, &size);
    uint32_t result = kNotDll;
    if (size.QuadPart >= 0x200 && size.QuadPart < (1ll << 31)) {
        HANDLE m = CreateFileMappingW(f, nullptr, PAGE_READONLY, 0, 0, nullptr);
        const auto* b = m ? static_cast<const unsigned char*>(MapViewOfFile(m, FILE_MAP_READ, 0, 0, 0)) : nullptr;
        if (b) {
            const size_t n = static_cast<size_t>(size.QuadPart);
            unsigned flags = 0;
            if (export_flags(b, n, &flags)) {
                if (gated_addon) *gated_addon = (flags & (8 | 16)) == (8 | 16);
                static const wchar_t kCore[] = L"\\kiln\\kiln-core.dll";
                static const char kWine[] = "Wine builtin DLL";
                if (memcmp(b + 0x40, kWine, sizeof kWine - 1) == 0) result = kSystem;
                else if ((flags & 1) || contains(b, n, kCore, sizeof kCore - sizeof(wchar_t)) || marker(b, n, "KilnProxy/"))
                    result = kKiln;
                else if ((flags & 2) || marker(b, n, "EMF-LOADER-7d3c9f")) {
                    result = kAccept;
                    if (emf_loader) *emf_loader = true;
                }
                else if ((flags & 4) || marker(b, n, "shroudforge")) result = kShroudForge;
                else result = kAccept;
            }
            UnmapViewOfFile(b);
        }
        if (m) CloseHandle(m);
    }
    CloseHandle(f);
    return result;
}

// ---- chain loading ----

// chain.ini [late]: the DLLs the core loads after the plugins. `used`: some entry of the other lists named the same file.
struct Late {
    wchar_t* raw;
    const wchar_t* full;   // resolved like any chain.ini path (relative: to <game>\kiln\, else to <game>\)
    const wchar_t* canon;  // nullptr when the file cannot be opened
    bool used;
};
Late* g_late = nullptr;
uint32_t g_late_count = 0;

// Index of the [late] line that names this file (canonical paths when both can be opened, else resolved paths), or -1.
int find_late(const wchar_t* canon, const wchar_t* full) {
    for (uint32_t i = 0; i < g_late_count; ++i) {
        const Late& l = g_late[i];
        if (canon && l.canon ? wieq(canon, l.canon) : wieq(full, l.full)) return static_cast<int>(i);
    }
    return -1;
}

HMODULE guarded_load(const wchar_t* path, DWORD* error) {
    __try {
        HMODULE h = LoadLibraryExW(path, nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        *error = h ? 0 : GetLastError();
        return h;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *error = GetExceptionCode();
        return nullptr;
    }
}

const wchar_t* source_name(uint32_t source) {
    switch (source) {
        case KILN_CHAIN_SOURCE_CHAIN: return L"chain.ini [chain]";
        case KILN_CHAIN_SOURCE_PROFILE: return L"chain.ini [profile]";
        default: return L"dinput8-chain.ini";
    }
}

KilnChainEntry* add(uint32_t source, const wchar_t* path, uint32_t status) {
    if (g_count >= g_capacity) return nullptr;
    KilnChainEntry* e = &g_chain[g_count];
    g_canon[g_count] = nullptr;
    ++g_count;
    e->path = utf8(path);
    e->status = status;
    e->source = source;
    return e;
}

// One DLL of a list. `base` resolves relative paths; `skip`: list it, load nothing (play-once no-mods).
void chain_one(uint32_t source, const wchar_t* base, const wchar_t* raw, uint32_t pre_refusal, bool skip) {
    wchar_t full[kPath];
    if (!resolve(base, raw, full)) lstrcpynW(full, raw, kPath);
    const wchar_t* canon = canonical(full);  // nullptr when the file cannot be opened (missing)
    int late_at = find_late(canon, full);  // [late] names this file: it passes every check, the core loads it
    if (late_at >= 0) g_late[late_at].used = true;
    if (pre_refusal != kAccept) {
        if (auto* e = add(source, full, KILN_CHAIN_REFUSED)) e->detail = pre_refusal;
        plog(L"chain: %s: %s refused: %s", source_name(source), full, refusal_text(pre_refusal));
        return;
    }
    if (skip) {
        add(source, full, KILN_CHAIN_SKIPPED);
        plog(L"chain: %s: %s skipped (%s)", source_name(source), full,
             g_info.play_once == KILN_PLAY_ONCE_NO_MODS ? L"play-once no-mods" : L"the profile file is corrupt");
        return;
    }
    const DWORD attrs = GetFileAttributesW(full);
    if (attrs == INVALID_FILE_ATTRIBUTES || (attrs & FILE_ATTRIBUTE_DIRECTORY)) {
        add(source, full, KILN_CHAIN_MISSING);
        plog(L"chain: %s: %s missing", source_name(source), full);
        return;
    }
    const wchar_t* shown = canon ? canon : full;
    uint32_t refusal = kAccept;
    if (source == KILN_CHAIN_SOURCE_EMF) {
        // Final path of the DLL inside the final path of the game folder: both resolved the same way, so a game folder
        // reached through a link or a mapped drive (Proton's S:) still matches. A drive root keeps its separator.
        // Under Wine, Unix symlinks are invisible to GetFinalPathNameByHandleW (it returns the path as opened).
        const size_t root_len = g_game_canon ? wlen(g_game_canon) : 0;
        const bool root_sep = root_len && sep(g_game_canon[root_len - 1]);
        if (!canon || !root_len || _wcsnicmp(canon, g_game_canon, root_len) != 0 ||
            (root_sep ? !canon[root_len] : !sep(canon[root_len])))
            refusal = kOutside;
    }
    if (refusal == kAccept && canon && g_self_canon && wieq(canon, g_self_canon)) refusal = kSelf;
    else if (refusal == kAccept && canon && g_system_canon && wieq(canon, g_system_canon)) refusal = kSystem;
    if (refusal == kAccept && canon) {
        for (uint32_t i = 0; i < g_count; ++i) {
            if (!g_canon[i] || !wieq(g_canon[i], canon)) continue;
            KilnChainEntry* e = add(source, shown, KILN_CHAIN_DUPLICATE);
            if (e) {
                e->module = g_chain[i].module;
                g_canon[g_count - 1] = canon;
            }
            plog(L"chain: %s: %s duplicate of entry %u; loaded once", source_name(source), shown, i + 1);
            return;
        }
    }
    bool emf_loader = false, gated_addon = false;
    if (refusal == kAccept) refusal = classify(shown, &emf_loader, &gated_addon);
    if (late_at >= 0 && refusal == kAccept && gated_addon) {
        plog(L"chain: %s: %s is shroudlink's client addon: its scans must finish before Kiln hooks, so chain.ini [late] "
             L"does not defer it; loaded now", source_name(source), shown);
        late_at = -1;
    }
    if (late_at >= 0 && refusal == kAccept && emf_loader && source == KILN_CHAIN_SOURCE_CHAIN) {
        // EMF's loader starts EMF itself (its core and the DLLs of dinput8-chain.ini). kiln-emf decides when the plugins
        // start whether the real EMF is in the process; a loader that came after that would run EMF next to kiln-emf.
        // It loads now, in its place in the chain, [late] or not.
        plog(L"chain: %s: %s is EMF's loader: it starts EMF itself, so chain.ini [late] does not defer it; loaded now",
             source_name(source), shown);
        late_at = -1;
    }
    KilnChainEntry* e = add(source, shown, KILN_CHAIN_LOADED);
    if (!e) return;
    g_canon[g_count - 1] = canon;
    if (refusal != kAccept) {
        e->status = KILN_CHAIN_REFUSED;
        e->detail = refusal;
        plog(L"chain: %s: %s refused: %s", source_name(source), shown, refusal_text(refusal));
        return;
    }
    if (late_at >= 0) {
        e->status = KILN_CHAIN_LATE;
        plog(L"chain: %s: %s deferred: the core loads it after the plugins (chain.ini [late])", source_name(source), shown);
        return;
    }
    DWORD error = 0;
    HMODULE h = guarded_load(shown, &error);
    if (!h) {
        e->status = KILN_CHAIN_FAILED;
        e->error = static_cast<int32_t>(error);
        plog(L"chain: %s: %s failed to load (error %lu)", source_name(source), shown, error);
        return;
    }
    e->module = h;
    plog(L"chain: %s: %s loaded at %p", source_name(source), shown, h);
}

// A list of DLL lines (pointers into a file's text), growable.
struct List {
    wchar_t** items = nullptr;
    uint32_t count = 0, capacity = 0;
    void push(wchar_t* v) {
        // Retain one overflow entry for the refusal report, then stop allocating/logging under the loader lock.
        if (count > kMaxPerList) return;
        if (count == capacity) {
            const uint32_t cap = capacity ? capacity * 2 : 32;
            void* p = items ? HeapReAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, items, cap * sizeof(wchar_t*))
                            : halloc(cap * sizeof(wchar_t*));
            if (!p) return;
            items = static_cast<wchar_t**>(p);
            capacity = cap;
        }
        items[count++] = v;
    }
};

// One section-based DLL list file. `wanted` maps a section name to a list (nullptr: ignored); lines before any
// section go to `first`. One DLL per line, "key = value" counts by its value, only values ending in .dll, ';' '#'
// '//' comments. Same rules as schema/fixtures/ini/kini.py (dll_entries).
struct Section {
    const wchar_t* name;
    List* list;
    bool seen = false;  // the file has this section's header
};
void read_dll_list(const wchar_t* file, List* first, Section* sections, size_t nsections) {
    DWORD error = 0;
    wchar_t* text = read_text(file, &error);
    if (!text) {
        if (error != ERROR_FILE_NOT_FOUND && error != ERROR_PATH_NOT_FOUND)
            plog(L"chain: cannot read %s (error %lu)", file, error);
        return;
    }
    List* current = first;
    wchar_t section[128];
    each_line(text, [&](wchar_t* line) {
        if (!*line || is_comment(line)) return;
        if (section_of(line, section, 128)) {
            current = nullptr;
            for (size_t i = 0; i < nsections; ++i)
                if (lstrcmpW(section, sections[i].name) == 0) current = sections[i].list, sections[i].seen = true;
            return;
        }
        if (!current) return;  // other sections belong to other tools
        wchar_t* v = value_of(line);
        if (*v && ends_with_i(v, L".dll")) current->push(v);
    });
}

void load_list(uint32_t source, const wchar_t* base, const List& list, bool emf_rules, bool skip) {
    for (uint32_t i = 0; i < list.count; ++i) {
        uint32_t pre = kAccept;
        if (i >= kMaxPerList) pre = kTooMany;
        else if (emf_rules && emf_outside(list.items[i])) pre = kOutside;
        chain_one(source, base, list.items[i], pre, skip);
    }
}

// ---- is the profile file well-formed JSON? (a corrupt profile loads no mods: not even its dllmain ones) ----

struct JsonCheck {
    const unsigned char* p;
    const unsigned char* end;
    int depth = 0;
    void ws() {
        while (p < end && (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n')) ++p;
    }
    bool lit(const char* w) {
        for (; *w; ++w, ++p)
            if (p >= end || *p != static_cast<unsigned char>(*w)) return false;
        return true;
    }
    bool str() {
        if (p >= end || *p != '"') return false;
        for (++p; p < end; ++p) {
            if (*p == '"') return ++p, true;
            if (*p < 0x20) return false;
            if (*p == '\\' && ++p >= end) return false;
        }
        return false;
    }
    bool num() {
        const unsigned char* start = p;
        if (p < end && *p == '-') ++p;
        while (p < end && ((*p >= '0' && *p <= '9') || *p == '.' || *p == 'e' || *p == 'E' || *p == '+' || *p == '-')) ++p;
        return p > start;
    }
    bool value() {
        ws();
        if (p >= end || ++depth > 64) return false;
        bool ok;
        if (*p == '{' || *p == '[') {
            const unsigned char close = *p == '{' ? '}' : ']';
            const bool obj = *p == '{';
            ++p;
            ws();
            ok = true;
            if (p < end && *p == close) {
                ++p;
            } else {
                for (;;) {
                    if (obj) {
                        ws();
                        if (!str()) return false;
                        ws();
                        if (p >= end || *p++ != ':') return false;
                    }
                    if (!value()) return false;
                    ws();
                    if (p < end && *p == ',') {
                        ++p;
                        continue;
                    }
                    if (p < end && *p == close) {
                        ++p;
                        break;
                    }
                    return false;
                }
            }
        } else if (*p == '"') {
            ok = str();
        } else if (*p == 't') {
            ok = lit("true");
        } else if (*p == 'f') {
            ok = lit("false");
        } else if (*p == 'n') {
            ok = lit("null");
        } else {
            ok = num();
        }
        --depth;
        return ok;
    }
};

// Missing file: true (nothing to judge; the legacy layout has no profile). Unreadable or not one well-formed JSON
// object: false.
bool profile_well_formed(const wchar_t* file) {
    if (GetFileAttributesW(file) == INVALID_FILE_ATTRIBUTES) return true;
    HANDLE h = CreateFileW(file, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER size{};
    bool ok = GetFileSizeEx(h, &size) && size.QuadPart > 0 && size.QuadPart < (16ll << 20);
    unsigned char* bytes = ok ? static_cast<unsigned char*>(halloc(static_cast<size_t>(size.QuadPart))) : nullptr;
    DWORD got = 0;
    ok = ok && bytes && ReadFile(h, bytes, static_cast<DWORD>(size.QuadPart), &got, nullptr) && got == size.QuadPart;
    CloseHandle(h);
    if (!ok) return false;
    JsonCheck c{bytes, bytes + got};
    c.ws();
    if (c.p >= c.end || *c.p != '{' || !c.value()) return false;
    c.ws();
    const bool result = c.p == c.end;
    HeapFree(GetProcessHeap(), 0, bytes);
    return result;
}

// kiln\chain.ini [chain] (lines before any section count as [chain]) -> <game>\dinput8-chain.ini ([Chain], [EMF] and
// lines before any section, EMF's rules) -> kiln\chain.ini [profile], or with play-once mode=profile kiln\chain\once.ini
// ([profile], lines before any section count) in place of chain.ini [profile]. once.ini may have a [chain] and a [late]
// of its own (the play-once profile's compat rules applied, written by the launcher); each one it has replaces the
// same section of chain.ini for this start, each it lacks leaves chain.ini's (a once.ini from before dev.21).
void load_chain(bool profile_once, bool skip) {
    g_game_canon = canonical(g_game);
    wchar_t buf[kPath];
    if (GetModuleFileNameW(g_self, buf, kPath)) g_self_canon = canonical(buf);
    const UINT sn = GetSystemDirectoryW(buf, kPath - 16);
    if (sn && sn < kPath - 16) {
        lstrcatW(buf, L"\\dinput8.dll");
        g_system_canon = canonical(buf);
    }
    List chain, profile, emf, ignored, late;
    wchar_t file[kPath];
    lstrcpynW(file, g_kiln, kPath - 32);
    lstrcatW(file, L"\\chain.ini");
    Section chain_sections[] = {{L"chain", &chain}, {L"profile", profile_once ? &ignored : &profile}, {L"late", &late}};
    read_dll_list(file, &chain, chain_sections, 3);
    if (profile_once) {
        List once_chain, once_late;
        lstrcpynW(file, g_kiln, kPath - 32);
        lstrcatW(file, L"\\chain\\once.ini");
        Section once_sections[] = {{L"profile", &profile}, {L"chain", &once_chain}, {L"late", &once_late}};
        read_dll_list(file, &profile, once_sections, 3);
        if (once_sections[1].seen) {
            chain = once_chain;
            plog(L"chain: %s has its own [chain] (%u entries): used for this start instead of chain.ini's", file, chain.count);
        }
        if (once_sections[2].seen) {
            late = once_late;
            plog(L"chain: %s has its own [late] (%u entries): used for this start instead of chain.ini's", file, late.count);
        }
    }
    lstrcpynW(file, g_game, kPath - 32);
    lstrcatW(file, L"\\dinput8-chain.ini");
    // [EMF]: the DLLs EMF 0.5's installer renamed (only EMF's loader read them). One list with [Chain], in file
    // order, as EMF's own reader takes them; a DLL that EMF's chained loader also starts is loaded once by Windows.
    Section emf_sections[] = {{L"chain", &emf}, {L"emf", &emf}};
    read_dll_list(file, &emf, emf_sections, 2);
    const uint32_t late_n = late.count < kMaxPerList ? late.count : kMaxPerList;  // [late] has no overflow report
    const uint32_t total = chain.count + emf.count + profile.count;  // [late] adds no entries of its own
    if (total == 0) return;
    g_chain = static_cast<KilnChainEntry*>(halloc(total * sizeof(KilnChainEntry)));
    g_canon = static_cast<const wchar_t**>(halloc(total * sizeof(wchar_t*)));
    if (!g_chain || !g_canon) return;
    g_capacity = total;
    // The profile's dllmain mods belong to the profile: when its file is corrupt the core loads no mods, so neither
    // does the proxy. (Schema errors in well-formed JSON are only seen by the core; the entries load then.)
    bool profile_ok = true;
    if (profile.count && !skip) {
        wchar_t pfile[kPath];
        if (profile_once && g_info.play_once_profile.len) {
            MultiByteToWideChar(CP_UTF8, 0, g_info.play_once_profile.ptr, -1, pfile, kPath);
        } else {
            lstrcpynW(pfile, g_kiln, kPath - 32);
            lstrcatW(pfile, profile_once ? L"\\profile.once.json" : L"\\profile.json");
        }
        profile_ok = profile_well_formed(pfile);
        if (!profile_ok) plog(L"chain: %s is not well-formed JSON: its [profile] DLLs are skipped (no mods this start)", pfile);
    }
    if (late_n) {
        g_late = static_cast<Late*>(halloc(late_n * sizeof(Late)));
        if (g_late) {
            for (uint32_t i = 0; i < late_n; ++i) {
                wchar_t full[kPath];
                if (!resolve(g_kiln, late.items[i], full)) lstrcpynW(full, late.items[i], kPath);
                // dinput8-chain.ini names its DLLs relative to the game folder: accept that spelling here when the
                // file is not under kiln\ but is under the game folder.
                if (!is_absolute(late.items[i]) && GetFileAttributesW(full) == INVALID_FILE_ATTRIBUTES) {
                    wchar_t alt[kPath];
                    if (resolve(g_game, late.items[i], alt) && GetFileAttributesW(alt) != INVALID_FILE_ATTRIBUTES)
                        lstrcpyW(full, alt);
                }
                g_late[i] = Late{late.items[i], wdup(full), canonical(full), false};
            }
            g_late_count = late_n;
        }
        if (late.count > kMaxPerList) plog(L"chain: chain.ini [late]: more than %u entries; the rest are ignored", kMaxPerList);
    }
    load_list(KILN_CHAIN_SOURCE_CHAIN, g_kiln, chain, false, skip);
    load_list(KILN_CHAIN_SOURCE_EMF, g_game, emf, true, skip);
    load_list(KILN_CHAIN_SOURCE_PROFILE, g_kiln, profile, false, skip || !profile_ok);
    // [late] only defers entries of the lists above; a line that names none of them loads nothing.
    for (uint32_t i = 0; i < g_late_count; ++i)
        if (!g_late[i].used) plog(L"chain: chain.ini [late]: %s names no entry of [chain], [profile] or dinput8-chain.ini; ignored", g_late[i].full);
    g_info.chain = g_chain;
    g_info.chain_count = g_count;
}

// ---- play-once.ini ----

// The clock play-once.ini is judged by. KILN_PROXY_TEST_NOW=<unix seconds> pins it for the parity tests on
// schema/fixtures/ini (native/spikes/proxyhost/run.py); it can only make a flag count that the user wrote anyway.
int64_t unix_now() {
    wchar_t pinned[24];
    const DWORD pn = GetEnvironmentVariableW(L"KILN_PROXY_TEST_NOW", pinned, 24);
    if (pn > 0 && pn < 24) {
        int64_t v = 0;
        bool ok = true;
        for (DWORD i = 0; i < pn; ++i) ok = ok && pinned[i] >= L'0' && pinned[i] <= L'9', v = v * 10 + (pinned[i] - L'0');
        if (ok) return v;
    }
    FILETIME ft;
    GetSystemTimeAsFileTime(&ft);
    const int64_t t = (static_cast<int64_t>(ft.dwHighDateTime) << 32) | ft.dwLowDateTime;
    return (t - 116444736000000000ll) / 10000000ll;
}

bool digits(const wchar_t*& p, int count, int* out) {
    int v = 0;
    for (int i = 0; i < count; ++i, ++p) {
        if (*p < L'0' || *p > L'9') return false;
        v = v * 10 + (*p - L'0');
    }
    *out = v;
    return true;
}

// Unix seconds (1-12 digits), or RFC 3339 with a zone: "2026-11-04T12:00:00Z", "...T13:00:00.5+01:00".
bool parse_time(const wchar_t* s, int64_t* out) {
    if (!*s) return false;
    size_t n = 0;
    bool all_digits = true;
    for (const wchar_t* p = s; *p; ++p, ++n) all_digits = all_digits && *p >= L'0' && *p <= L'9';
    if (all_digits) {
        if (n > 12) return false;
        int64_t v = 0;
        for (const wchar_t* p = s; *p; ++p) v = v * 10 + (*p - L'0');
        *out = v;
        return true;
    }
    const wchar_t* p = s;
    SYSTEMTIME st{};
    int y, mo, d, h, mi, se;
    if (!digits(p, 4, &y) || *p++ != L'-' || !digits(p, 2, &mo) || *p++ != L'-' || !digits(p, 2, &d)) return false;
    if (*p != L'T' && *p != L't' && *p != L' ') return false;
    ++p;
    if (!digits(p, 2, &h) || *p++ != L':' || !digits(p, 2, &mi) || *p++ != L':' || !digits(p, 2, &se)) return false;
    if (*p == L'.') {
        if (p[1] < L'0' || p[1] > L'9') return false;
        for (++p; *p >= L'0' && *p <= L'9'; ++p) {
        }
    }
    int offset = 0;
    if (*p == L'Z' || *p == L'z') {
        ++p;
    } else if (*p == L'+' || *p == L'-') {
        const int sign = *p++ == L'-' ? -1 : 1;
        int oh, om;
        if (!digits(p, 2, &oh) || *p++ != L':' || !digits(p, 2, &om)) return false;
        offset = sign * (oh * 3600 + om * 60);
    } else {
        return false;  // no zone: refuse rather than guess
    }
    if (*p) return false;
    st.wYear = static_cast<WORD>(y), st.wMonth = static_cast<WORD>(mo), st.wDay = static_cast<WORD>(d);
    st.wHour = static_cast<WORD>(h), st.wMinute = static_cast<WORD>(mi), st.wSecond = static_cast<WORD>(se);
    FILETIME ft;
    if (!SystemTimeToFileTime(&st, &ft)) return false;
    const int64_t t = (static_cast<int64_t>(ft.dwHighDateTime) << 32) | ft.dwLowDateTime;
    *out = (t - 116444736000000000ll) / 10000000ll - offset;
    return true;
}

// Reads and deletes kiln\play-once.ini; fills g_info.play_once (+ nonce, profile). Returns the mode to act on.
uint32_t play_once() {
    wchar_t file[kPath];
    lstrcpynW(file, g_kiln, kPath - 32);
    lstrcatW(file, L"\\play-once.ini");
    if (GetFileAttributesW(file) == INVALID_FILE_ATTRIBUTES) return KILN_PLAY_ONCE_NONE;
    DWORD error = 0;
    wchar_t* text = read_text(file, &error);
    // Delete first: whatever it said, it must never count twice. A file that stays would count at every start.
    // Wine's DeleteFileW reports success before the unlink (it happens when the handle closes, and its failure is not
    // reported back), so check that the file is really gone.
    const BOOL deleted = DeleteFileW(file);
    const DWORD delete_error = deleted ? 0 : GetLastError();
    if (!deleted || GetFileAttributesW(file) != INVALID_FILE_ATTRIBUTES) {
        g_info.play_once = KILN_PLAY_ONCE_IGNORED_UNDELETABLE;
        plog(L"play-once: cannot delete %s (error %lu): ignored, this start is a normal one", file,
             deleted ? ERROR_ACCESS_DENIED : delete_error);
        return KILN_PLAY_ONCE_NONE;
    }
    if (g_server) {
        g_info.play_once = KILN_PLAY_ONCE_IGNORED_SERVER;
        plog(L"play-once: dedicated server: %s deleted and ignored", file);
        return KILN_PLAY_ONCE_NONE;
    }
    wchar_t* mode = nullptr;
    wchar_t* created = nullptr;
    wchar_t* nonce = nullptr;
    wchar_t* profile = nullptr;
    if (text)
        each_line(text, [&](wchar_t* line) {
            wchar_t key[32];
            wchar_t* value = nullptr;
            if (!*line || is_comment(line) || *line == L'[' || !key_value(line, key, 32, &value)) return;
            if (lstrcmpW(key, L"mode") == 0) mode = value;
            else if (lstrcmpW(key, L"created") == 0) created = value;
            else if (lstrcmpW(key, L"nonce") == 0) nonce = value;
            else if (lstrcmpW(key, L"profile") == 0) profile = value;
        });
    if (nonce) g_info.play_once_nonce = utf8(nonce);
    uint32_t m = KILN_PLAY_ONCE_IGNORED_INVALID;
    if (mode && wieq(mode, L"vanilla")) m = KILN_PLAY_ONCE_VANILLA;
    else if (mode && wieq(mode, L"no-mods")) m = KILN_PLAY_ONCE_NO_MODS;
    else if (mode && wieq(mode, L"profile")) m = KILN_PLAY_ONCE_PROFILE;
    int64_t when = 0;
    if (m == KILN_PLAY_ONCE_IGNORED_INVALID || !created || !parse_time(created, &when) || !nonce || !*nonce) {
        g_info.play_once = KILN_PLAY_ONCE_IGNORED_INVALID;
        plog(L"play-once: %s is invalid (mode '%s', created '%s', nonce '%s'): ignored", file, mode ? mode : L"",
             created ? created : L"", nonce ? nonce : L"");
        return KILN_PLAY_ONCE_NONE;
    }
    const int64_t age = unix_now() - when;
    if (age > kPlayOnceMaxAge || age < -kPlayOnceFutureSlack) {
        g_info.play_once = KILN_PLAY_ONCE_IGNORED_EXPIRED;
        plog(L"play-once: %s was created %d s ago (limit %d s): ignored", file, static_cast<int>(age),
             static_cast<int>(kPlayOnceMaxAge));
        return KILN_PLAY_ONCE_NONE;
    }
    g_info.play_once = m;
    if (m == KILN_PLAY_ONCE_PROFILE) {
        wchar_t path[kPath];
        if (!profile || !*profile || !resolve(g_kiln, profile, path)) {
            lstrcpynW(path, g_kiln, kPath - 32);
            lstrcatW(path, L"\\profile.once.json");
        }
        g_info.play_once_profile = utf8(path);
        plog(L"play-once: mode profile (%s, chain\\once.ini), nonce '%s'", path, nonce ? nonce : L"");
    } else {
        plog(L"play-once: mode %s, nonce '%s'", m == KILN_PLAY_ONCE_VANILLA ? L"vanilla" : L"no-mods",
             nonce ? nonce : L"");
    }
    return m;
}

// ---- entry gate ----

bool core_path(wchar_t* path, DWORD capacity) {
    DWORD n = GetEnvironmentVariableW(L"KILN_CORE", path, capacity);
    if (n > 0 && n < capacity) return true;
    if (wlen(g_kiln) + 20 >= capacity) return false;
    lstrcpyW(path, g_kiln);
    lstrcatW(path, L"\\kiln-core.dll");  // the text "\kiln\kiln-core.dll" marks Kiln proxies (classify)
    return true;
}

void boot_core() {
    wchar_t path[kPath];
    if (!core_path(path, kPath) || GetFileAttributesW(path) == INVALID_FILE_ATTRIBUTES) return;
    HMODULE core = LoadLibraryExW(path, nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!core) {
        trace(L"[kiln] proxy: LoadLibrary(%s) failed: %lu\n", path, GetLastError());
        return;
    }
    auto boot = reinterpret_cast<KilnBootFn>(GetProcAddress(core, KILN_BOOT));
    if (!boot) {
        trace(L"[kiln] proxy: %s has no kiln_boot\n", path);
        return;
    }
    boot(&g_info);
}

// proxy.log was closed before the core started (it writes kiln.log from there): append one line to it.
void log_after_boot(const wchar_t* line) {
    wchar_t path[kPath];
    lstrcpynW(path, g_kiln, kPath - 32);
    lstrcatW(path, L"\\logs\\proxy.log");
    g_log = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                        FILE_ATTRIBUTE_NORMAL, nullptr);
    plog(L"%s", line);
    if (g_log != INVALID_HANDLE_VALUE) CloseHandle(g_log);
    g_log = INVALID_HANDLE_VALUE;
}

bool restore_entry() {
    DWORD protection = 0;
    if (!VirtualProtect(g_entry, sizeof g_original, PAGE_EXECUTE_READWRITE, &protection)) return false;
    memcpy(g_entry, g_original, sizeof g_original);
    DWORD ignored = 0;
    VirtualProtect(g_entry, sizeof g_original, protection, &ignored);
    return FlushInstructionCache(GetCurrentProcess(), g_entry, sizeof g_original) != 0;
}

bool executable(const void* p) {
    MEMORY_BASIC_INFORMATION m{};
    if (!VirtualQuery(p, &m, sizeof m) || m.State != MEM_COMMIT || (m.Protect & (PAGE_GUARD | PAGE_NOACCESS))) return false;
    return (m.Protect & (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)) != 0;
}

// The entry point was patched while the core booted. True when the patch is a plain jump (jmp rel32, jmp [rip+d] or
// mov rax, imm64; jmp rax) whose target is not code any more: the DLL that wrote it was unloaded again (a DllMain that
// failed after patching, a DLL the core refused as a plugin). Any other patch is taken as it is.
bool entry_patch_is_dead() {
    const unsigned char* e = g_entry;
    const unsigned char* target = nullptr;
    if (e[0] == 0xe9) {
        int32_t rel = 0;
        memcpy(&rel, e + 1, sizeof rel);
        target = e + 5 + rel;
    } else if (e[0] == 0xff && e[1] == 0x25) {
        int32_t rel = 0;
        memcpy(&rel, e + 2, sizeof rel);
        const unsigned char* cell = e + 6 + rel;
        MEMORY_BASIC_INFORMATION m{};
        if (rel != 0 && (!VirtualQuery(cell, &m, sizeof m) || m.State != MEM_COMMIT || (m.Protect & (PAGE_GUARD | PAGE_NOACCESS))))
            return true;  // the jump reads its target from memory that is gone
        memcpy(&target, cell, sizeof target);
    } else if (e[0] == 0x48 && e[1] == 0xb8 && e[10] == 0xff && e[11] == 0xe0) {
        memcpy(&target, e + 2, sizeof target);
    } else {
        return false;
    }
    return !executable(target);
}

// Reached by a jmp from the exe's entry point, so the stack looks like a call to the entry: return its value.
int __cdecl gate_entry() {
    if (g_log != INVALID_HANDLE_VALUE) {  // the core writes kiln.log from here on
        CloseHandle(g_log);
        g_log = INVALID_HANDLE_VALUE;
    }
    // The saved bytes go back before the core starts, not after it. Nothing runs the entry point until this function
    // continues into it (this thread is the one that would), so the order is free, and a DLL the core loads (chain.ini
    // [late], a native mod) that patches the entry point then saves the real bytes instead of Kiln's jump. Saved over
    // the jump, its patch could only be dropped: chaining to it would run this gate a second time.
    if (!restore_entry()) ExitProcess(ERROR_DLL_INIT_FAILED);  // the entry point would jump back here for ever
    boot_core();
    if (memcmp(g_entry, g_original, sizeof g_original) != 0) {
        if (entry_patch_is_dead()) {
            // Continuing would jump into memory that is gone. The saved bytes are the entry as it was before that DLL.
            if (!restore_entry()) ExitProcess(ERROR_DLL_INIT_FAILED);
            log_after_boot(L"entry gate: a DLL that loaded while Kiln started patched the game's entry point and is not in "
                           L"the process any more; its patch was removed, the game starts without it");
        } else {
            log_after_boot(L"entry gate: a DLL that loaded while Kiln started (chain.ini [late], or a native mod) patched "
                           L"the game's entry point; its patch runs now, before the game's own entry");
        }
    }
    // The bytes at the entry point may be another DLL's patch (a chained loader that holds the start, or one from the
    // boot above): it runs now and unwinds to the real entry point in turn.
    return reinterpret_cast<int(__cdecl*)()>(g_entry)();
}

bool install_gate() {
    auto* image = reinterpret_cast<unsigned char*>(GetModuleHandleW(nullptr));
    if (!image) return false;
    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(image);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0) return false;
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(image + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE || nt->FileHeader.Machine != IMAGE_FILE_MACHINE_AMD64 ||
        nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC || !nt->OptionalHeader.AddressOfEntryPoint ||
        nt->OptionalHeader.AddressOfEntryPoint > nt->OptionalHeader.SizeOfImage - sizeof g_original)
        return false;
    g_entry = image + nt->OptionalHeader.AddressOfEntryPoint;
    // jmp qword ptr [rip+0]; dq gate_entry. Preserves every register; the whole saved entry is put back before it
    // runs, so nothing needs relocating.
    unsigned char jump[14]{0xff, 0x25, 0, 0, 0, 0};
    auto target = &gate_entry;
    memcpy(jump + 6, &target, sizeof target);
    DWORD protection = 0;
    if (!VirtualProtect(g_entry, sizeof jump, PAGE_EXECUTE_READWRITE, &protection)) return false;
    memcpy(g_original, g_entry, sizeof g_original);
    memcpy(g_entry, jump, sizeof jump);
    DWORD ignored = 0;
    VirtualProtect(g_entry, sizeof jump, protection, &ignored);
    return FlushInstructionCache(GetCurrentProcess(), g_entry, sizeof jump) != 0;
}

bool disabled() {
    wchar_t v[8];
    const DWORD n = GetEnvironmentVariableW(L"KILN_DISABLE", v, 8);
    return n == 1 && v[0] == L'1';
}

// Is this start the game's crash reporter? The game builds `<exe> --upload-dump "<dir>" --process_duration <t>` (strings
// in enshrouded.exe and enshrouded_server.exe; no other command line in them starts the exe itself). An argument is compared
// whole, so a path that merely contains the words does not count, and argv[0] is never compared. Quoting as in
// CommandLineToArgvW, which lives in shell32: not for the loader lock.
bool crash_reporter() {
    const wchar_t* p = GetCommandLineW();
    if (!p) return false;
    bool first = true;
    while (*p) {
        while (*p == L' ' || *p == L'\t') ++p;
        if (!*p) break;
        wchar_t arg[32];
        size_t n = 0;
        bool quoted = false, whole = true;
        for (; *p && (quoted || (*p != L' ' && *p != L'\t')); ++p) {
            if (*p == L'"') {
                quoted = !quoted;
            } else if (n + 1 < sizeof arg / sizeof *arg) {
                arg[n++] = *p;
            } else {
                whole = false;
            }
        }
        arg[n] = 0;
        if (!first && whole && wieq(arg, L"--upload-dump")) return true;
        first = false;
    }
    return false;
}

// shroudlink's client addon (a chained dinput8.dll) appends its start-up result to <game>\shroudlink_client.log from a
// thread its DllMain starts. The log's length now, before any chained DLL loads, is where this start's lines begin:
// kiln-core's wait for that addon reads only what follows (core/src/foreign_scan.h), never a second-rounded time.
uint64_t addon_log_size() {
    wchar_t path[kPath];
    if (wlen(g_game) + 32 >= kPath) return KILN_BOOT_LOG_UNKNOWN;
    lstrcpyW(path, g_game);
    lstrcatW(path, L"\\shroudlink_client.log");
    HANDLE h = CreateFileW(path, FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) {
        const DWORD e = GetLastError();
        return e == ERROR_FILE_NOT_FOUND || e == ERROR_PATH_NOT_FOUND ? 0 : KILN_BOOT_LOG_UNKNOWN;
    }
    LARGE_INTEGER size{};
    const bool ok = GetFileSizeEx(h, &size) && size.QuadPart >= 0;
    CloseHandle(h);
    return ok ? static_cast<uint64_t>(size.QuadPart) : KILN_BOOT_LOG_UNKNOWN;
}

void attach() {
    g_info.size = sizeof(KilnBootInfo);
    g_info.proxy_module = g_self;
    g_info.addon_log_size = KILN_BOOT_LOG_UNKNOWN;
    if (crash_reporter()) {
        trace(L"[kiln] proxy: the game's crash reporter: no chain, no Kiln, no log\n");
        return;
    }
    if (disabled()) {
        trace(L"[kiln] proxy: KILN_DISABLE=1: no chain, no Kiln\n");
        return;
    }
    if (!init_game_paths()) {
        trace(L"[kiln] proxy: cannot find the game folder; Kiln is off\n");
        return;
    }
    open_log();
    const uint32_t mode = play_once();
    if (mode == KILN_PLAY_ONCE_VANILLA) {
        plog(L"play-once vanilla: no chained DLL, no Kiln for this start");
        if (g_log != INVALID_HANDLE_VALUE) CloseHandle(g_log), g_log = INVALID_HANDLE_VALUE;
        return;
    }
    g_info.addon_log_size = addon_log_size();  // before the chain: see addon_log_size()
    load_chain(mode == KILN_PLAY_ONCE_PROFILE, mode == KILN_PLAY_ONCE_NO_MODS);
    if (install_gate()) {
        plog(L"entry gate installed (%u chained DLL(s) handled)", g_count);
    } else {
        plog(L"entry gate not installed; Kiln is off");
        if (g_log != INVALID_HANDLE_VALUE) CloseHandle(g_log), g_log = INVALID_HANDLE_VALUE;
    }
}

// ---- GetdfDIJoystick fallback: c_dfDIJoystick (DIJOYSTATE), for system dinput8 builds without the export (Wine) ----

struct DiObjectFormat {
    const GUID* guid;
    DWORD offset;
    DWORD type;
    DWORD flags;
};
struct DiDataFormat {
    DWORD size;
    DWORD object_size;
    DWORD flags;
    DWORD data_size;
    DWORD objects;
    const DiObjectFormat* format;
};

constexpr GUID kAxis(unsigned long d1) { return GUID{d1, 0xC9F3, 0x11CF, {0xBF, 0xC7, 0x44, 0x45, 0x53, 0x54, 0x00, 0x00}}; }
const GUID kXAxis = kAxis(0xA36D02E0), kYAxis = kAxis(0xA36D02E1), kZAxis = kAxis(0xA36D02E2),
           kRxAxis = kAxis(0xA36D02F4), kRyAxis = kAxis(0xA36D02F5), kRzAxis = kAxis(0xA36D02E3),
           kSlider = kAxis(0xA36D02E4), kPov = kAxis(0xA36D02F2);
constexpr DWORD kOptional = 0x80000000, kAnyInstance = 0x00FFFF00;
constexpr DWORD kAxisT = kOptional | kAnyInstance | 0x3, kPovT = kOptional | kAnyInstance | 0x10,
                kButtonT = kOptional | kAnyInstance | 0xC;
#define KILN_BTN(n) {nullptr, 48 + (n), kButtonT, 0}
const DiObjectFormat kJoystickObjects[44] = {
    {&kXAxis, 0, kAxisT, 0},  {&kYAxis, 4, kAxisT, 0},   {&kZAxis, 8, kAxisT, 0},   {&kRxAxis, 12, kAxisT, 0},
    {&kRyAxis, 16, kAxisT, 0}, {&kRzAxis, 20, kAxisT, 0}, {&kSlider, 24, kAxisT, 0}, {&kSlider, 28, kAxisT, 0},
    {&kPov, 32, kPovT, 0},     {&kPov, 36, kPovT, 0},     {&kPov, 40, kPovT, 0},     {&kPov, 44, kPovT, 0},
    KILN_BTN(0),  KILN_BTN(1),  KILN_BTN(2),  KILN_BTN(3),  KILN_BTN(4),  KILN_BTN(5),  KILN_BTN(6),  KILN_BTN(7),
    KILN_BTN(8),  KILN_BTN(9),  KILN_BTN(10), KILN_BTN(11), KILN_BTN(12), KILN_BTN(13), KILN_BTN(14), KILN_BTN(15),
    KILN_BTN(16), KILN_BTN(17), KILN_BTN(18), KILN_BTN(19), KILN_BTN(20), KILN_BTN(21), KILN_BTN(22), KILN_BTN(23),
    KILN_BTN(24), KILN_BTN(25), KILN_BTN(26), KILN_BTN(27), KILN_BTN(28), KILN_BTN(29), KILN_BTN(30), KILN_BTN(31),
};
#undef KILN_BTN
const DiDataFormat kJoystickFormat = {sizeof(DiDataFormat), sizeof(DiObjectFormat), 1 /* DIDF_ABSAXIS */,
                                      80 /* sizeof(DIJOYSTATE) */, 44, kJoystickObjects};

}  // namespace

extern "C" {

__declspec(dllexport) HRESULT WINAPI DirectInput8Create(HINSTANCE inst, DWORD version, REFIID iid, void** out,
                                                        void* outer) {
    using fn = HRESULT(WINAPI*)(HINSTANCE, DWORD, REFIID, void**, void*);
    if (auto f = reinterpret_cast<fn>(real("DirectInput8Create"))) return f(inst, version, iid, out, outer);
    if (out) *out = nullptr;
    return E_FAIL;
}

__declspec(dllexport) HRESULT WINAPI DllCanUnloadNow() {
    using fn = HRESULT(WINAPI*)();
    auto f = reinterpret_cast<fn>(real("DllCanUnloadNow"));
    return f ? f() : S_FALSE;
}

__declspec(dllexport) HRESULT WINAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** out) {
    using fn = HRESULT(WINAPI*)(REFCLSID, REFIID, void**);
    if (auto f = reinterpret_cast<fn>(real("DllGetClassObject"))) return f(clsid, iid, out);
    if (out) *out = nullptr;
    return CLASS_E_CLASSNOTAVAILABLE;
}

__declspec(dllexport) HRESULT WINAPI DllRegisterServer() {
    using fn = HRESULT(WINAPI*)();
    auto f = reinterpret_cast<fn>(real("DllRegisterServer"));
    return f ? f() : E_FAIL;
}

__declspec(dllexport) HRESULT WINAPI DllUnregisterServer() {
    using fn = HRESULT(WINAPI*)();
    auto f = reinterpret_cast<fn>(real("DllUnregisterServer"));
    return f ? f() : E_FAIL;
}

// LPCDIDATAFORMAT WINAPI GetdfDIJoystick(void) (Windows 10+ dinput8): the system's when it has it, else Kiln's copy of
// c_dfDIJoystick (Wine's dinput8 does not export it).
__declspec(dllexport) const void* WINAPI GetdfDIJoystick() {
    using fn = const void*(WINAPI*)();
    if (auto f = reinterpret_cast<fn>(real("GetdfDIJoystick"))) return f();
    return &kJoystickFormat;
}

// Which proxy this is (the installer reads the same facts from the file: the KilnProxy/<abi> marker text).
__declspec(dllexport) const KilnProxyInfoData* KILN_CALL KilnProxyInfo() { return &kProxyInfo; }

}  // extern "C"

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, void*) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_self = inst;
        DisableThreadLibraryCalls(inst);
        attach();
    }
    return TRUE;
}
