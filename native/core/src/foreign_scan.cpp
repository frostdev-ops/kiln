// SPDX-License-Identifier: MIT
// Windows adapter for foreign_scan.h. No foreign code is called, and no module or game bytes are written.
#include "foreign_scan.h"

#include <windows.h>
#include <psapi.h>

#include <filesystem>

#include "common.h"
#include "log.h"
#include "sigdb.h"

namespace kiln::foreign {
namespace {

std::vector<Wait> g_waits;

bool executable_in(HMODULE module, const void* addr) {
    MEMORY_BASIC_INFORMATION mbi{};
    if (!addr || !VirtualQuery(addr, &mbi, sizeof mbi) || mbi.AllocationBase != module || mbi.State != MEM_COMMIT ||
        (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS))) return false;
    return (mbi.Protect & (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)) != 0;
}

// Keep a reference while inspecting exports and waiting: enumeration alone does not protect against FreeLibrary.
struct Module {
    HMODULE handle = nullptr;
    size_t size = 0;
    Module(HMODULE h, size_t n) : handle(h), size(n) {}
    Module(Module&& other) noexcept : handle(other.handle), size(other.size) { other.handle = nullptr; }
    ~Module() { if (handle) FreeLibrary(handle); }
};
std::vector<Module> addon_modules() {
    constexpr size_t kMaxModules = 4096;
    std::vector<HMODULE> mods(kMaxModules);
    DWORD need = 0;
    if (!K32EnumProcessModules(GetCurrentProcess(), mods.data(), static_cast<DWORD>(mods.size() * sizeof(HMODULE)), &need)) return {};
    mods.resize(std::min<size_t>(mods.size(), need / sizeof(HMODULE)));
    std::vector<Module> out;
    for (HMODULE candidate : mods) {
        HMODULE m = nullptr;
        if (!candidate || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, reinterpret_cast<LPCWSTR>(candidate), &m)) continue;
        MODULEINFO info{};
        const auto shutdown = GetProcAddress(m, kAddonExport), proxy = GetProcAddress(m, kAddonProxyExport);
        if (K32GetModuleInformation(GetCurrentProcess(), m, &info, sizeof info) && info.lpBaseOfDll == m && info.SizeOfImage &&
            executable_in(m, reinterpret_cast<const void*>(shutdown)) && executable_in(m, reinterpret_cast<const void*>(proxy)))
            out.emplace_back(m, info.SizeOfImage);
        else FreeLibrary(m);  // forwarded exports and unrelated proxies do not qualify
    }
    return out;
}

std::string module_name(HMODULE m) {
    wchar_t path[MAX_PATH] = {};
    const DWORD n = GetModuleFileNameW(m, path, MAX_PATH);
    return n && n < MAX_PATH ? to_utf8(std::filesystem::path(std::wstring(path, n)).filename().wstring()) : std::string("?");
}

// This start's part of the log (this_start: after `from`, at most 64 KiB), shared with the addon's writer.
std::string tail(const std::filesystem::path& file, uint64_t from) {
    if (from == kLogUnknown) return {};
    HANDLE h = CreateFileW(file.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return {};
    std::string out;
    LARGE_INTEGER size{};
    uint64_t start = 0;
    if (GetFileSizeEx(h, &size) && size.QuadPart > 0 && this_start(from, static_cast<uint64_t>(size.QuadPart), kLogTail, start) &&
        static_cast<uint64_t>(size.QuadPart) > start) {
        LARGE_INTEGER at{};
        at.QuadPart = static_cast<int64_t>(start);
        out.resize(static_cast<size_t>(static_cast<uint64_t>(size.QuadPart) - start));
        DWORD got = 0;
        if (!SetFilePointerEx(h, at, nullptr, FILE_BEGIN) || !ReadFile(h, out.data(), static_cast<DWORD>(out.size()), &got, nullptr)) got = 0;
        out.resize(got);
    }
    CloseHandle(h);
    return out;
}

uintptr_t addon_ui_target() {
    sigdb::Pattern p;
    std::string err;
    if (!sigdb::parse_pattern(kAddonUiPattern, p, err)) return 0;
    const auto hits = sigdb::scan_code(p, 2);  // original sigdb semantics: exactly one match, never first of many
    return hits.size() == 1 ? hits[0] : 0;
}

}  // namespace

void wait_for_scanners(uint64_t log_from) {
    const auto mods = addon_modules();
    if (mods.empty()) return;
    const auto started = static_cast<uint64_t>(now_ms());
    const uintptr_t ui = addon_ui_target();
    const auto log_file = paths().game / kAddonLog;
    for (const auto& m : mods) {
        const std::string name = module_name(m.handle);
        KLOG_I("foreign: waiting for shroudlink client addon %s to finish its live-code scans before Kiln hooks (10 s boot budget)%s",
               name.c_str(), log_from == kLogUnknown ? "; where its lines of this start begin in shroudlink_client.log is not "
                                                       "known (an older dinput8.dll), so only its UI hook can end the wait" : "");
        WaitEnv env;
        env.module = reinterpret_cast<uintptr_t>(m.handle);
        env.now_ms = [] { return static_cast<uint64_t>(now_ms()); };
        env.sleep_ms = [](uint32_t ms) { Sleep(ms); };
        env.log_text = [&] { return tail(log_file, log_from); };
        if (ui) {
            env.ui_hooked = [&m, ui] {
                return hook_reaches(ui, reinterpret_cast<uintptr_t>(m.handle), m.size,
                    [&m](uintptr_t addr, void* out, size_t n) {
                        // Safe even for unmapped relay pointers, page boundaries and malformed hook operands.
                        if (!addr || addr > UINTPTR_MAX - n) return false;
                        const auto base = reinterpret_cast<uintptr_t>(m.handle);
                        if (addr >= base && addr - base < m.size && !executable_in(m.handle, reinterpret_cast<const void*>(addr))) return false;
                        SIZE_T got = 0;
                        if (!ReadProcessMemory(GetCurrentProcess(), reinterpret_cast<const void*>(addr), out, n, &got) || got != n) return false;
                        // hook_reaches checks the final module range; module validation above excludes forwarders.
                        return true;
                    });
            };
        }
        const uint64_t elapsed = static_cast<uint64_t>(now_ms()) - started;
        const WaitResult r = wait_for_addon(env, elapsed >= kWaitMs ? 0 : kWaitMs - elapsed);
        g_waits.push_back({name, r.end, r.waited_ms, r.line});
        if (r.end == WaitEnd::Ready || r.end == WaitEnd::Hooked)
            KLOG_I("foreign: %s finished scanning (%s after %llu ms); Kiln goes on", name.c_str(), wait_end_name(r.end),
                   static_cast<unsigned long long>(r.waited_ms));
        else if (r.end == WaitEnd::SwitchedOff)
            KLOG_W("foreign: %s switched its UI off: %s; the MODS tab will not show", name.c_str(), r.line.c_str());
        else
            KLOG_W("foreign: %s: scan wait timed out after %llu ms; Kiln goes on. If MODS is missing, check shroudlink_client.log",
                   name.c_str(), static_cast<unsigned long long>(r.waited_ms));
    }
}

const std::vector<Wait>& waits() { return g_waits; }

}  // namespace kiln::foreign
