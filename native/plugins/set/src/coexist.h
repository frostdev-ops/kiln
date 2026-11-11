// SPDX-License-Identifier: MIT
#pragma once
#include <algorithm>
#include <array>
#include <atomic>
#include <cstdint>
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
// What one look at the loaded modules found. foreign: SET's runtime (or a non-system power proxy) is in the process.
// failed: the inventory itself could not be read, which says nothing about SET either way.
enum class Inventory { clean, foreign, failed };
struct Inspection {
    Inventory state = Inventory::clean;
    std::string text;  // what was seen, or what could not be read; empty when clean
};
// Both kiln-set and kiln.types use this policy. Kiln plugins are excluded because the detector itself contains
// SET's branding string; native SET does not export kiln_plugin_main, even when renamed.
inline Inspection inspect_runtime() {
    static std::mutex mutex;
    std::lock_guard lock(mutex);
    const auto failed = [](const char* what) { return Inspection{Inventory::failed, what}; };
    const auto foreign = [](std::string what) { return Inspection{Inventory::foreign, std::move(what)}; };
    auto utf8 = [](const std::filesystem::path& p) { auto s = p.u8string(); return std::string(s.begin(), s.end()); };
    wchar_t system_path[32768]{};
    const UINT n = GetSystemDirectoryW(system_path, 32768);
    if (!n || n >= 32768) return failed("the Windows system directory could not be identified");
    const std::filesystem::path system(system_path);
    HMODULE self = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCWSTR>(&inspect_runtime), &self)) return failed("the inspecting module could not be identified");
    // A module snapshot fails now and then while the module list changes under it (a thread or DLL going away): Windows
    // documents ERROR_BAD_LENGTH as "try again". Three tries here; ConflictGate retries later checks.
    HANDLE snapshot = INVALID_HANDLE_VALUE;
    for (int attempt = 0; attempt < 3 && snapshot == INVALID_HANDLE_VALUE; ++attempt)
        snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, GetCurrentProcessId());
    if (snapshot == INVALID_HANDLE_VALUE) return failed("loaded modules could not be inspected");
    struct Close { HANDLE h; ~Close() { CloseHandle(h); } } close{snapshot};
    MODULEENTRY32W mod{};
    mod.dwSize = sizeof mod;
    if (!Module32FirstW(snapshot, &mod)) return failed("loaded modules could not be inspected");
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
        // A Toolhelp snapshot does not keep its modules loaded. Hold a reference until the image/export reads finish.
        HMODULE held = nullptr;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, reinterpret_cast<LPCWSTR>(mod.hModule), &held))
            return failed("a loaded module could not be retained for inspection");
        struct Release { HMODULE h; ~Release() { FreeLibrary(h); } } release{held};
        wchar_t module_path[32768]{};
        const DWORD length = GetModuleFileNameW(held, module_path, 32768);
        if (!length || length >= 32768) return failed("a loaded module's path could not be inspected");
        const std::filesystem::path file(module_path);
        // Wine's builtin powrprof.dll reports a game-folder powrprof.dll's path without running that file: only a
        // module whose mapped image is the file at its path counts (module_image.h).
        if (!kcompat::module_is_file(held, file)) continue;
        if (kset::power_proxy(file, system)) return foreign("a non-system powrprof.dll is loaded (" + utf8(file) + ")");
        // Kiln's own files name SET too (kiln\host\Kiln.Ui.dll's mod catalogue, plugins, the proxy): never SET's runtime.
        if (kcompat::kiln_own_module(held, file)) continue;
        std::error_code ec;
        if (std::filesystem::equivalent(file.parent_path(), system, ec) && !ec) continue;
        auto it = inspected.find(file);
        Fingerprint stamp{};
        const bool stamped = fingerprint(file, stamp);
        bool found = false;
        if (stamped && it != inspected.end() && it->second.stamp == stamp) found = it->second.found;
        else { std::ifstream f(file, std::ios::binary); found = kset::set_marker(f); }
        if (stamped) current[file] = {stamp, found};
        if (found) return foreign("a SET-branded module is loaded (" + utf8(file) + ")");
    } while (Module32NextW(snapshot, &mod));
    if (GetLastError() != ERROR_NO_MORE_FILES) return failed("the loaded module inventory was incomplete");
    inspected.swap(current);
    return {};
}
// One look, as text: empty when clean. A failed inventory reads like a conflict here; callers that keep running use
// ConflictGate, which tells the two apart.
inline std::string runtime_conflict() { return inspect_runtime().text; }

// When to step aside. A foreign runtime that was actually seen: at once. A failed inventory: not on its own. It is
// tried again every retry_ms, and only failures_to_step_aside failed checks in a row (with no clean one between) count
// as "cannot tell, so do not run beside a possible SET". One clean inventory resets the count.
// Five checks one second apart: a failure seen in the field lasted one check (the game's window thread had just ended),
// so five in a row over four seconds is a lasting fault and not a moment; each check costs wineserver round trips per
// module under Wine, so the retries are bounded and spaced, never per frame.
// check() and startup() are serialized by the caller (the plugin's lifecycle or its re-check lock); retry_due() is
// lock-free for the per-tick path.
class ConflictGate {
public:
    static constexpr int failures_to_step_aside = 5;
    static constexpr uint64_t retry_ms = 1000;
    // The verdict for one inspection at time now: the reason to step aside, or "" to keep running.
    std::string judge(const Inspection& seen, uint64_t now) {
        if (seen.state == Inventory::clean) {
            failures_ = 0;
            retry_at_.store(0, std::memory_order_release);
            return {};
        }
        if (seen.state == Inventory::foreign) {
            retry_at_.store(0, std::memory_order_release);
            return seen.text;
        }
        if (++failures_ >= failures_to_step_aside) {
            retry_at_.store(0, std::memory_order_release);
            return seen.text + " (" + std::to_string(failures_) + " checks in a row)";
        }
        retry_at_.store(now + retry_ms ? now + retry_ms : 1, std::memory_order_release);
        return {};
    }
    std::string check() { return judge(inspect_runtime(), GetTickCount64()); }
    // The startup check, before anything runs: the retries happen here, a few milliseconds apart, so a start never
    // goes ahead on an inventory that was not read.
    std::string startup() {
        for (;;) {
            const std::string verdict = check();
            if (!verdict.empty() || !failures_) return verdict;
            Sleep(20);
        }
    }
    // True once when a failed inventory's retry has come due. No time is read while no retry is pending.
    bool retry_due() {
        uint64_t at = retry_at_.load(std::memory_order_acquire);
        if (!at || GetTickCount64() < at) return false;
        return retry_at_.compare_exchange_strong(at, 0, std::memory_order_acq_rel);
    }
    int failures() const { return failures_; }
    void reset() { failures_ = 0; retry_at_.store(0, std::memory_order_release); }

private:
    int failures_ = 0;
    std::atomic<uint64_t> retry_at_{0};
};

// runtime_conflict() costs several wineserver round trips per loaded module under Wine (snapshot, path, stats, file
// opens): never run it per frame or tick. Gate it with ModuleWatch (DLL loads, plus module-list changes seen by a cheap
// 3 s fingerprint; the throttled full check only where the fingerprint is unavailable).
using ModuleWatch = kcompat::ModuleWatch;
#endif
}  // namespace kset
