// SPDX-License-Identifier: MIT
// kcompat::module_is_file: whether a loaded module is the DLL file its path names. Header-only; used by the
// compatibility plugins' own-loader checks (compat.h module_in_dir, kiln-flame, kiln-shroudtopia, kiln-ember,
// kiln-enshwick, kiln-greeneye) and by kiln-set's coexist.h.
//
// Under Wine/Proton a native DLL in the game folder that has the name of a Wine builtin (dbghelp.dll, winmm.dll,
// powrprof.dll, dinput8.dll ...) is not run without a DLL override, yet Wine's builtin then reports that game-folder
// path from GetModuleFileNameW. A check that reads the file at that path (marker texts, a "non-system" path) would
// see the other loader although it never ran. The mapped image keeps its own PE header: when its TimeDateStamp or
// SizeOfImage differ from the file's, the module is not that file.
#pragma once
#include <windows.h>

#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string>

namespace kcompat {

// False only when both headers are readable and differ (or the image is a Wine builtin and the file is not). An
// unreadable file, or a mapped image whose header is gone (a module that wipes its own header after loading), keeps
// the caller's old answer (true), so a check never becomes weaker than before.
inline bool module_is_file(HMODULE h, const std::filesystem::path& file) {
    if (!h) return false;
    const auto* base = reinterpret_cast<const unsigned char*>(reinterpret_cast<uintptr_t>(h) & ~uintptr_t(3));
    char head[4096];
    std::ifstream f(file, std::ios::binary);
    if (!f) return true;
    f.read(head, sizeof head);
    const size_t n = static_cast<size_t>(f.gcount());
    auto u16 = [](const void* p) { uint16_t v; std::memcpy(&v, p, 2); return v; };
    auto u32 = [](const void* p) { uint32_t v; std::memcpy(&v, p, 4); return v; };
    if (n < 0x40 || head[0] != 'M' || head[1] != 'Z') return true;
    const uint32_t file_pe = u32(head + 0x3C);
    if (file_pe > n || n - file_pe < 24 + 60 || u32(head + file_pe) != 0x00004550) return true;
    // The mapped header (the loader keeps the first page readable for every image).
    if (base[0] != 'M' || base[1] != 'Z') return true;
    const uint32_t mem_pe = u32(base + 0x3C);
    if (mem_pe > 4096 - 24 - 60 || u32(base + mem_pe) != 0x00004550) return true;
    const uint32_t file_stamp = u32(head + file_pe + 8), mem_stamp = u32(base + mem_pe + 8);
    // SizeOfImage: optional header offset 56 (the same in PE32 and PE32+)
    const uint32_t file_size = u32(head + file_pe + 24 + 56), mem_size = u32(base + mem_pe + 24 + 56);
    if (file_stamp != mem_stamp || file_size != mem_size || u16(head + file_pe + 4) != u16(base + mem_pe + 4)) return false;
    // Wine marks its builtins in the DOS stub.
    static const char kWine[] = "Wine builtin DLL";
    const bool mem_wine = std::memcmp(base + 0x40, kWine, sizeof kWine - 1) == 0;
    const bool file_wine = n >= 0x40 + sizeof kWine && std::memcmp(head + 0x40, kWine, sizeof kWine - 1) == 0;
    return mem_wine == file_wine;
}

// True for Kiln's own files, which name every engine (the in-game mod catalogue kiln\host\Kiln.Ui.dll, plugins,
// services, kiln-core.dll, Kiln's proxy) and so must never count as another engine's loader in a text or marker scan:
// modules exporting KilnProxyInfo, kiln_boot or kiln_plugin_main; kiln-core.dll; anything under <game>\kiln\ except
// <game>\kiln\chain\, where Kiln chains foreign loaders (those still count). <game> is the exe's folder.
inline bool kiln_own_module(HMODULE h, const std::filesystem::path& file) {
    if (h && (GetProcAddress(h, "KilnProxyInfo") || GetProcAddress(h, "kiln_boot") || GetProcAddress(h, "kiln_plugin_main")))
        return true;
    auto low = [](std::wstring s) {
        for (auto& c : s) {
            if (c >= L'A' && c <= L'Z') c = static_cast<wchar_t>(c - L'A' + L'a');
            if (c == L'/') c = L'\\';
        }
        return s;
    };
    const std::wstring f = low(file.wstring());
    const std::wstring name = f.substr(f.find_last_of(L'\\') == std::wstring::npos ? 0 : f.find_last_of(L'\\') + 1);
    if (name == L"kiln-core.dll") return true;
    std::wstring exe(32768, L'\0');  // on the heap: callers scan modules on threads with small stacks too
    const DWORD n = GetModuleFileNameW(nullptr, exe.data(), 32768);
    if (!n || n >= 32768) return false;
    exe.resize(n);
    std::wstring game = low(exe);
    const auto slash = game.find_last_of(L'\\');
    if (slash == std::wstring::npos) return false;
    game.resize(slash + 1);
    const std::wstring kiln = game + L"kiln\\", chain = kiln + L"chain\\";
    return f.rfind(kiln, 0) == 0 && f.rfind(chain, 0) != 0;
}

}  // namespace kcompat
