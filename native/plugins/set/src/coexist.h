// SPDX-License-Identifier: MIT
#pragma once
#include <algorithm>
#include <array>
#include <filesystem>
#include <istream>
#include <fstream>
#include <map>
#include <mutex>
#if defined(_WIN32) || defined(KSET_LOADER_HOST_TEST)
#include <windows.h>
#include <tlhelp32.h>
#include "../../compat/module_watch.h"
#include "../../compat/module_image.h"
#endif
#include <string>
#include <string_view>

namespace kset {
// Scan incrementally, including markers split over a read boundary. Never load a whole DLL into RAM.
inline bool set_marker(std::istream& in) {
    const std::string ascii = "Shrouded Engine Tweaks";
    std::string wide;
    for (char c : ascii) { wide += c; wide += '\0'; }
    std::array<char, 65536> block{};
    std::string tail;
    constexpr size_t limit = 256 * 1024 * 1024;
    for (size_t read = 0; in && read < limit;) {
        in.read(block.data(), std::min(block.size(), limit - read));
        const size_t n = static_cast<size_t>(in.gcount());
        if (!n) break;
        read += n;
        tail.append(block.data(), n);
        if (tail.find(ascii) != std::string::npos || tail.find(wide) != std::string::npos) return true;
        if (tail.size() >= wide.size()) tail.erase(0, tail.size() - wide.size() + 1);
    }
    return false;
}

inline bool power_proxy(const std::filesystem::path& file, const std::filesystem::path& system) {
    auto name = file.filename().wstring();
    std::transform(name.begin(), name.end(), name.begin(), [](wchar_t c) { return c >= L'A' && c <= L'Z' ? c + 32 : c; });
    if (name != L"powrprof.dll") return false;
    std::error_code ec;
    // Fail closed for an unidentifiable proxy. Checking the game directory alone misses relocated proxies.
    return !std::filesystem::equivalent(file.parent_path(), system, ec) || bool(ec);
}
#if defined(_WIN32) || defined(KSET_LOADER_HOST_TEST)
// Both kiln-set and kiln.types use this policy. Kiln plugins are excluded because the detector itself contains
// SET's branding string; native SET does not export kiln_plugin_main, even when renamed.
inline std::string runtime_conflict() {
    static std::mutex mutex;
    std::lock_guard lock(mutex);
    auto utf8 = [](const std::filesystem::path& p) { auto s = p.u8string(); return std::string(s.begin(), s.end()); };
    wchar_t system_path[32768]{};
    const UINT n = GetSystemDirectoryW(system_path, 32768);
    if (!n || n >= 32768) return "the Windows system directory could not be identified";
    const std::filesystem::path system(system_path);
    HMODULE self = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCWSTR>(&runtime_conflict), &self)) return "the inspecting module could not be identified";
    const HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, GetCurrentProcessId());
    if (snapshot == INVALID_HANDLE_VALUE) return "loaded modules could not be inspected";
    struct Close { HANDLE h; ~Close() { CloseHandle(h); } } close{snapshot};
    MODULEENTRY32W mod{};
    mod.dwSize = sizeof mod;
    if (!Module32FirstW(snapshot, &mod)) return "loaded modules could not be inspected";
    // An unload and reload can both occur between scans, with the same path and even the same base address.
    // Cache only by file identity, write time and size. This keeps re-checks from re-reading the game image,
    // but cannot reuse a negative result for a rebuilt/replaced DLL. If a fingerprint is unavailable, read again.
    using Fingerprint = std::array<DWORD, 7>;
    struct Marker { Fingerprint stamp; bool found; };
    auto fingerprint = [](const std::filesystem::path& file, Fingerprint& stamp) {
        const HANDLE h = CreateFileW(file.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                     nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h == INVALID_HANDLE_VALUE) return false;
        BY_HANDLE_FILE_INFORMATION i{};
        const bool ok = GetFileInformationByHandle(h, &i) != 0 && (i.nFileIndexHigh || i.nFileIndexLow);
        CloseHandle(h);
        if (ok) stamp = {i.dwVolumeSerialNumber, i.nFileIndexHigh, i.nFileIndexLow, i.nFileSizeHigh, i.nFileSizeLow,
                         i.ftLastWriteTime.dwHighDateTime, i.ftLastWriteTime.dwLowDateTime};
        return ok;
    };
    static std::map<std::filesystem::path, Marker> inspected;
    std::map<std::filesystem::path, Marker> current;
    do {
        if (mod.hModule == self) continue;
        wchar_t module_path[32768]{};
        const DWORD length = GetModuleFileNameW(mod.hModule, module_path, 32768);
        if (!length || length >= 32768) return "a loaded module's path could not be inspected";
        const std::filesystem::path file(module_path);
        // Wine's builtin powrprof.dll reports a game-folder powrprof.dll's path without running that file: only a
        // module whose mapped image is the file at its path counts (module_image.h).
        if (!kcompat::module_is_file(mod.hModule, file)) continue;
        if (kset::power_proxy(file, system)) return "a non-system powrprof.dll is loaded (" + utf8(file) + ")";
        // Kiln's own files name SET too (kiln\host\Kiln.Ui.dll's mod catalogue, plugins, the proxy): never SET's runtime.
        if (kcompat::kiln_own_module(mod.hModule, file)) continue;
        std::error_code ec;
        if (std::filesystem::equivalent(file.parent_path(), system, ec) && !ec) continue;
        auto it = inspected.find(file);
        Fingerprint stamp{};
        const bool stamped = fingerprint(file, stamp);
        bool found = false;
        if (stamped && it != inspected.end() && it->second.stamp == stamp) found = it->second.found;
        else { std::ifstream f(file, std::ios::binary); found = kset::set_marker(f); }
        if (stamped) current[file] = {stamp, found};
        if (found) return "a SET-branded module is loaded (" + utf8(file) + ")";
    } while (Module32NextW(snapshot, &mod));
    if (GetLastError() != ERROR_NO_MORE_FILES) return "the loaded module inventory was incomplete";
    inspected.swap(current);
    return {};
}

// runtime_conflict() costs several wineserver round trips per loaded module under Wine (snapshot, path, stats, file
// opens): never run it per frame or tick. Gate it with ModuleWatch (DLL loads, plus module-list changes seen by a cheap
// 3 s fingerprint; the throttled full check only where the fingerprint is unavailable).
using ModuleWatch = kcompat::ModuleWatch;
#endif
}  // namespace kset
