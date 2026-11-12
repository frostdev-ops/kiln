// SPDX-License-Identifier: MIT
#pragma once
#include <algorithm>
#include <array>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <filesystem>
#include <istream>
#include <fstream>
#include <map>
#include <mutex>
#if defined(_WIN32) || defined(KSET_LOADER_HOST_TEST)
#include <windows.h>
#include "../../compat/module_watch.h"  // and winternl.h: LDR_DATA_TABLE_ENTRY
#include "../../compat/module_image.h"
#endif
#include <string>
#include <string_view>
#include <vector>

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
    std::string text;  // what was seen, or what could not be read (the call and its code); empty when clean
    size_t modules = 0;  // clean: how many modules the loader list had
};
// "<call> failed (error <code>)": what a player's report has to show for a failed inventory.
inline std::string call_failed(const char* call, unsigned long code) {
    char text[192];
    std::snprintf(text, sizeof text, "%s failed (error %lu)", call, code);
    return text;
}
// One entry of the process's loader list, as LdrEnumerateLoadedModules showed it.
struct Listed {
    HMODULE base;
    std::wstring name;
};
struct Listing {
    std::vector<Listed> modules;
    bool copy_failed = false;
};
// Runs inside LdrEnumerateLoadedModules with the loader's lock held: it only copies, and no exception may unwind
// through ntdll's frames. No loader, file or waiting call here: on Windows the walk may also hold the loader's module
// table lock, which GetModuleHandleExW takes again, so the references are taken after the walk.
inline void WINAPI copy_listed(LDR_DATA_TABLE_ENTRY* entry, void* context, BOOLEAN* stop) {
    auto* out = static_cast<Listing*>(context);
    try {
        const UNICODE_STRING& name = entry->FullDllName;
        out->modules.push_back({static_cast<HMODULE>(entry->DllBase),
                                name.Buffer ? std::wstring(name.Buffer, name.Length / sizeof(wchar_t)) : std::wstring()});
    } catch (...) {
        out->copy_failed = true;
        *stop = 1;
    }
}
using LdrEnumerate = LONG(WINAPI*)(void* reserved, void(WINAPI* callback)(LDR_DATA_TABLE_ENTRY*, void*, BOOLEAN*), void* context);
// Both kiln-set and kiln.types use this policy. Kiln plugins are excluded because the detector itself contains
// SET's branding string; native SET does not export kiln_plugin_main, even when renamed.
// The list of loaded modules is ntdll's own (LdrEnumerateLoadedModules): read in this process's memory under the
// loader's locks, on Windows and under Wine. It is not a Toolhelp snapshot: Wine builds those through a handle to the
// own pid (OpenProcess, then one wineserver read per entry and string, done with process_vm_readv or by stopping the
// process's first thread with ptrace), so a process DACL without PROCESS_VM_READ or a process wineserver cannot read
// fails every snapshot for as long as that lasts; and its walk takes no loader lock, so an unload under it cuts the
// list short without an error. A failure here names the call and its code.
inline Inspection inspect_runtime() {
    static std::mutex mutex;
    // A DLL initializer can enter while holding the loader lock and another inspector is waiting for it.
    // Never wait for that inspector; an unread inventory is retried by ConflictGate, not reported clean.
    std::unique_lock lock(mutex, std::try_to_lock);
    const auto failed = [](std::string what) { return Inspection{Inventory::failed, std::move(what)}; };
    if (!lock.owns_lock()) return failed("loaded modules could not be inspected: another inspection is in progress");
    const auto foreign = [](std::string what) { return Inspection{Inventory::foreign, std::move(what)}; };
    auto utf8 = [](const std::filesystem::path& p) { auto s = p.u8string(); return std::string(s.begin(), s.end()); };
    wchar_t system_path[32768]{};
    const UINT n = GetSystemDirectoryW(system_path, 32768);
    if (!n || n >= 32768)
        return failed(std::string("the Windows system directory could not be identified: ") +
                      (n ? std::string("GetSystemDirectoryW: the path is too long") : call_failed("GetSystemDirectoryW", GetLastError())));
    const std::filesystem::path system(system_path);
    HMODULE self = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCWSTR>(&inspect_runtime), &self))
        return failed("the inspecting module could not be identified: " + call_failed("GetModuleHandleExW", GetLastError()));
    const HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
    if (!ntdll) return failed("loaded modules could not be inspected: " + call_failed("GetModuleHandleW(ntdll.dll)", GetLastError()));
    const auto walk = reinterpret_cast<LdrEnumerate>(reinterpret_cast<void*>(GetProcAddress(ntdll, "LdrEnumerateLoadedModules")));
    if (!walk)
        return failed("loaded modules could not be inspected: " +
                      call_failed("GetProcAddress(ntdll.dll, LdrEnumerateLoadedModules)", GetLastError()));
    Listing listing;
    listing.modules.reserve(512);  // the copy inside the walk then rarely allocates
    const LONG status = walk(nullptr, &copy_listed, &listing);
    if (status < 0) {
        char text[96];
        std::snprintf(text, sizeof text, "LdrEnumerateLoadedModules failed (NTSTATUS 0x%08lX)", static_cast<unsigned long>(static_cast<ULONG>(status)));
        return failed(std::string("loaded modules could not be inspected: ") + text);
    }
    if (listing.copy_failed)
        return failed("loaded modules could not be inspected: copying the loader list failed after " +
                      std::to_string(listing.modules.size()) + " entries (out of memory)");
    // The list always has the module doing this: an empty or cut list is a failure, never a clean result.
    if (std::none_of(listing.modules.begin(), listing.modules.end(), [&](const Listed& m) { return m.base == self; }))
        return failed("loaded modules could not be inspected: LdrEnumerateLoadedModules listed " +
                      std::to_string(listing.modules.size()) + " modules but not the inspecting one");
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
    for (const Listed& mod : listing.modules) {
        if (mod.base == self) continue;
        // The walk keeps nothing loaded. Hold a reference until the image/export reads finish; a module that is gone
        // by now (unloaded after the walk) is no longer in the process, so it is no runtime either.
        HMODULE held = nullptr;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, reinterpret_cast<LPCWSTR>(mod.base), &held)) {
            const DWORD error = GetLastError();
            if (error == ERROR_MOD_NOT_FOUND) continue;
            return failed("a loaded module could not be retained for inspection: " + call_failed("GetModuleHandleExW", error) +
                          " for " + utf8(mod.name));
        }
        struct Release { HMODULE h; ~Release() { FreeLibrary(h); } } release{held};
        // The held module's own path: if another module took the address since the walk, that is the one inspected.
        wchar_t module_path[32768]{};
        const DWORD length = GetModuleFileNameW(held, module_path, 32768);
        if (!length || length >= 32768)
            return failed("a loaded module's path could not be inspected: " +
                          (length ? std::string("GetModuleFileNameW: the path is too long") : call_failed("GetModuleFileNameW", GetLastError())) +
                          " for " + utf8(mod.name));
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
    }
    inspected.swap(current);
    return {Inventory::clean, {}, listing.modules.size()};
}
// One look, as text: empty when clean. A failed inventory reads like a conflict here; callers that keep running use
// ConflictGate, which tells the two apart.
inline std::string runtime_conflict() { return inspect_runtime().text; }

// Diagnostic only: whether this process can be read through a handle to its own pid, which is what a Toolhelp module
// snapshot of it needs (and other loaders' module scans through a process handle). "" when it can, else the call and
// code that failed: OpenProcess refused (a process DACL without PROCESS_VM_READ), or the read itself (under Wine:
// wineserver could not read the process). inspect_runtime() does not depend on it.
inline std::string handle_read_probe() {
    const HANDLE process = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, FALSE, GetCurrentProcessId());
    if (!process) return call_failed("OpenProcess(this process, PROCESS_VM_READ)", GetLastError());
    static const uint32_t canary = 0x6e6c696b;
    uint32_t copy = 0;
    SIZE_T got = 0;
    const bool read = ReadProcessMemory(process, &canary, &copy, sizeof copy, &got) != 0;
    const DWORD error = read ? 0 : GetLastError();
    CloseHandle(process);
    if (!read) return call_failed("ReadProcessMemory through that handle", error);
    return got == sizeof copy && copy == canary ? std::string() : std::string("ReadProcessMemory through that handle copied other bytes");
}

// When to step aside. A foreign runtime that was actually seen: at once. A failed inventory: not on its own. It is
// tried again every retry_ms, and only failures_to_step_aside failed checks in a row (with no clean one between) count
// as "cannot tell, so do not run beside a possible SET". One clean inventory resets the count.
// Five checks one second apart: a failure seen in the field lasted one check (the game's window thread had just ended),
// so five in a row over four seconds is a lasting fault and not a moment; each check costs wineserver round trips per
// module under Wine, so the retries are bounded and spaced, never per frame. After that verdict a retry still comes due
// every passive_retry_ms, for a caller that stays loaded and can take the verdict back on a clean look (kiln.types);
// cannot_tell() says the verdict was of that kind.
// check() and startup() are serialized by the caller (the plugin's lifecycle or its re-check lock); retry_due() is
// lock-free for the per-tick path.
class ConflictGate {
public:
    static constexpr int failures_to_step_aside = 5;
    static constexpr uint64_t retry_ms = 1000, passive_retry_ms = 5000;
    // The verdict for one inspection at time now: the reason to step aside, or "" to keep running.
    std::string judge(const Inspection& seen, uint64_t now) {
        last_ = seen.state;
        if (seen.state == Inventory::clean) {
            failures_ = 0;
            modules_ = seen.modules;
            retry_at_.store(0, std::memory_order_release);
            return {};
        }
        if (seen.state == Inventory::foreign) {
            retry_at_.store(0, std::memory_order_release);
            return seen.text;
        }
        const bool verdict = ++failures_ >= failures_to_step_aside;
        const uint64_t at = now + (verdict ? passive_retry_ms : retry_ms);
        retry_at_.store(at ? at : 1, std::memory_order_release);
        return verdict ? seen.text + " (" + std::to_string(failures_) + " checks in a row)" : std::string();
    }
    // The last verdict came from inventories that could not be read (failures_to_step_aside in a row), not from a
    // runtime that was seen: a later clean inventory may take it back.
    bool cannot_tell() const { return last_ == Inventory::failed && failures_ >= failures_to_step_aside; }
    size_t modules() const { return modules_; }  // in the last clean inventory
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
    void reset() { failures_ = 0; last_ = Inventory::clean; retry_at_.store(0, std::memory_order_release); }

private:
    int failures_ = 0;
    Inventory last_ = Inventory::clean;
    size_t modules_ = 0;
    std::atomic<uint64_t> retry_at_{0};
};

// runtime_conflict() costs wineserver round trips per loaded module under Wine (file opens and stats): never run it per
// frame or tick. Gate it with ModuleWatch (DLL loads, plus module-list changes seen by a cheap
// 3 s fingerprint; the throttled full check only where the fingerprint is unavailable).
using ModuleWatch = kcompat::ModuleWatch;
#endif
}  // namespace kset
