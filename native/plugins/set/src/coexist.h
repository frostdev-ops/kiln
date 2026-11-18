// SPDX-License-Identifier: MIT
#pragma once
#include <algorithm>
#include <array>
#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <cstdio>
#include <filesystem>
#include <istream>
#include <fstream>
#include <map>
#include <mutex>
#include <optional>
#include <thread>
#if defined(_WIN32) || defined(KSET_LOADER_HOST_TEST)
#include <windows.h>
#include "../../compat/module_watch.h"  // and winternl.h: LDR_DATA_TABLE_ENTRY
#include "../../compat/module_image.h"
#endif
#include <string>
#include <string_view>
#include <vector>

#include "../../compat/set_marker.h"

namespace kset {
// SET's name in a stream (kcompat::set_marks, compat/set_marker.h: incremental, markers split over a read boundary
// included, never the whole DLL in RAM).
inline bool set_marker(std::istream& in) { return kcompat::set_marks(in, false).name; }

// A powrprof.dll outside the system folder. Only a candidate: whether it is SET's is decided by SET's markers
// (kcompat::power_proxy_owner), not by this name (issue 23: a proxy loader installed as POWRPROF.dll is not SET).
inline bool power_proxy(const std::filesystem::path& file, const std::filesystem::path& system) {
    if (!kcompat::named_powrprof(file)) return false;
    std::error_code ec;
    // A folder that cannot be compared counts as non-system (its markers then decide). Checking the game directory
    // alone misses relocated proxies.
    return !std::filesystem::equivalent(file.parent_path(), system, ec) || bool(ec);
}
#if defined(_WIN32) || defined(KSET_LOADER_HOST_TEST)
// What one look at the loaded modules found. foreign: SET's runtime is in the process (a module with SET's markers).
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
// A path for a prefix comparison: no \\?\ prefix, backslashes, ASCII lower case.
inline std::wstring folded_path(std::wstring s) {
    if (s.rfind(L"\\\\?\\", 0) == 0 && s.rfind(L"\\\\?\\UNC\\", 0) != 0) s.erase(0, 4);
    for (auto& c : s) {
        if (c == L'/') c = L'\\';
        else if (c >= L'A' && c <= L'Z') c = static_cast<wchar_t>(c - L'A' + L'a');
    }
    return s;
}
// The Windows folder (GetWindowsDirectoryW), folded, with its trailing backslash; empty when it cannot be read (then
// nothing is skipped by it). Everything below it is Windows' or a driver's: System32, its DriverStore (a graphics
// driver's DLLs: about 400 MB for an NVIDIA card, loaded at the game's first frames), WinSxS. SET is none of them: it
// ships as a powrprof.dll in the game folder, and the game loads powrprof.dll from its own folder, else from System32.
inline std::wstring windows_folder() {
    std::wstring dir(32768, L'\0');  // on the heap: inspections run on threads with small stacks too
    const UINT n = GetWindowsDirectoryW(dir.data(), 32768);
    if (!n || n >= 32768) return {};
    dir.resize(n);
    dir = folded_path(std::move(dir));
    if (dir.back() != L'\\') dir += L'\\';
    return dir;
}
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
    const std::wstring windows = windows_folder();
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
        // Windows' and the drivers' own files (windows_folder): not opened, not read. This skips about two thirds of a
        // game's modules, and the graphics driver's DLLs that the first frames load (a whole-file read each, below).
        if (!windows.empty() && folded_path(module_path).rfind(windows, 0) == 0) continue;
        // Wine's builtin powrprof.dll reports a game-folder powrprof.dll's path without running that file: only a
        // module whose mapped image is the file at its path counts (module_image.h).
        if (!kcompat::module_is_file(held, file)) continue;
        // A powrprof.dll outside System32 is SET's only when it carries SET's markers. A proxy loader that only
        // forwards the system DLL's exports (issue 23: a server's POWRPROF.dll loading other mods), Shared XP or
        // Ember is another tool, and kiln-set runs next to it. Read on every look (not cached): at most one such file.
        if (kset::power_proxy(file, system)) {
            switch (kcompat::power_proxy_owner(file)) {
                case kcompat::PowerProxy::set: return foreign("SET's powrprof.dll is loaded (" + utf8(file) + ")");
                case kcompat::PowerProxy::unreadable:
                    return failed("a non-system powrprof.dll could not be read to tell whether it is SET's (" + utf8(file) + ")");
                case kcompat::PowerProxy::other: continue;
            }
        }
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

// The re-checks after a DLL load, on a thread of their own: the game's UI frame and server step only start a look and
// take its result on a later frame or step, they never read module files. A look reads a newly loaded module whole for
// SET's name (cached afterwards by file identity): at the first frames of a hard-disk player's game, the DLSS DLL and
// what else loads with the renderer took the window into "not responding" for seconds (perfdiag 2026-11-17, inbox 50).
// One look at a time; nothing is lost while one runs (the caller keeps its request and starts the next look when it
// takes this one's result). The startup check (on_ready) stays on its caller's thread: nothing runs before it.
// start(), take(), running() and wait() are serialized by the caller; finished() is lock-free for the per-frame path.
class LookWorker {
public:
    LookWorker() = default;
    LookWorker(const LookWorker&) = delete;
    LookWorker& operator=(const LookWorker&) = delete;
    // At process exit the thread is gone or about to be: a DLL detach never waits for it. A plugin that is unloaded
    // while the game runs calls wait() first (on_unload).
    ~LookWorker() {
        if (thread_.joinable()) thread_.detach();
    }
    // Host tests: run each look on the caller's thread, so start() has its result at once.
    bool run_inline = false;
    // Starts a look unless one runs or a finished one waits to be taken. False when no look could start (no thread).
    bool start() {
        std::unique_lock lock(mu_);
        if (busy_ || result_) return false;
        if (thread_.joinable()) thread_.join();  // the last look's thread, done (busy_ is false)
        busy_ = true;
        if (run_inline) {
            lock.unlock();
            look();
            return true;
        }
        try {
            thread_ = std::thread([this] { look(); });
        } catch (...) {
            busy_ = false;
            return false;
        }
        return true;
    }
    // A finished look is waiting to be taken.
    bool finished() const { return done_.load(std::memory_order_acquire); }
    // The finished look, once; nothing while one runs or none was started.
    std::optional<Inspection> take() {
        if (!finished()) return std::nullopt;
        std::lock_guard lock(mu_);
        done_.store(false, std::memory_order_relaxed);
        std::optional<Inspection> seen = std::move(result_);
        result_.reset();
        return seen;
    }
    bool running() const {
        std::lock_guard lock(mu_);
        return busy_;
    }
    // Until a running look has finished (its result stays to be taken).
    void wait() {
        std::unique_lock lock(mu_);
        cv_.wait(lock, [this] { return !busy_; });
        lock.unlock();
        if (thread_.joinable()) thread_.join();
    }

private:
    void look() {
        Inspection seen;
        try {
            seen = inspect_runtime();
        } catch (...) {
            seen = {Inventory::failed, "loaded modules could not be inspected: the check ran out of memory"};
        }
        std::lock_guard lock(mu_);
        result_ = std::move(seen);
        busy_ = false;
        done_.store(true, std::memory_order_release);
        cv_.notify_all();
    }
    mutable std::mutex mu_;
    std::condition_variable cv_;
    std::thread thread_;
    bool busy_ = false;
    std::optional<Inspection> result_;
    std::atomic<bool> done_{false};
};

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
