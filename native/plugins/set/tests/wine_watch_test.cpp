// SPDX-License-Identifier: MIT
// Under Wine: kset::ModuleWatch gets DLL-load notifications (LdrRegisterDllNotification), a DLL load makes exactly one
// re-check due, and the steady-state gate costs nothing next to the full module check (which it must keep off the
// per-frame path). The module inventory reads the loader list, so it stays clean while Toolhelp snapshots of the
// process fail, and a module unloading around the scan is neither a crash nor a failed look. Run by test-wine.sh;
// exit 0 = pass.
#include <windows.h>

#include <chrono>
#include <cstdio>

#if !defined(KSET_WATCH_DLL) && !defined(KSET_SILENT_DLL) && !defined(KSET_POWER_DLL)
namespace {
HMODULE unload_during_path = nullptr;
bool unloaded_after_path = false;
DWORD WINAPI path_then_unload(HMODULE h, wchar_t* out, DWORD cap) {
    const DWORD n = GetModuleFileNameW(h, out, cap);
    if (n && h && h == unload_during_path) {
        unload_during_path = nullptr;
        unloaded_after_path = FreeLibrary(h) != 0;
    }
    return n;
}
HMODULE unload_before_hold = nullptr;
bool unloaded_before_hold = false;
BOOL WINAPI unload_then_hold(DWORD flags, LPCWSTR address, HMODULE* out) {
    if (unload_before_hold && (flags & GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS) &&
        reinterpret_cast<HMODULE>(const_cast<wchar_t*>(address)) == unload_before_hold) {
        const HMODULE h = unload_before_hold;
        unload_before_hold = nullptr;
        unloaded_before_hold = FreeLibrary(h) != 0;
    }
    return GetModuleHandleExW(flags, address, out);
}
}
// Deterministically schedule another caller's last FreeLibrary after the scan got the module's path, or after the
// loader-list walk listed it but before the scan holds it.
#define GetModuleFileNameW path_then_unload
#define GetModuleHandleExW unload_then_hold
#endif
#include "../src/coexist.h"
#undef GetModuleFileNameW
#undef GetModuleHandleExW

#if defined(KSET_WATCH_DLL)
// Built from this same source as an unloadable fixture. Its static watch must unregister even if the host
// skips explicit teardown; /MT's DLL detach runs the destructor before the code/context can be unmapped.
namespace { kset::ModuleWatch dll_watch; }
extern "C" __declspec(dllexport) bool arm_watch() { return dll_watch.arm(); }
extern "C" __declspec(dllexport) void disarm_watch() { dll_watch.disarm(); }
#elif defined(KSET_SILENT_DLL)
extern "C" __declspec(dllexport) const char* marker() { return "Shrouded Engine Tweaks"; }
#elif defined(KSET_POWER_DLL)
// Built twice as powrprof.dll (issue 23): KSET_POWER_DLL=1, a proxy loader that forwards the system DLL's exports and
// loads other DLLs (the text of a server's ProxyLoader); KSET_POWER_DLL=2, one with SET's markers (its folder name, as
// SET's powrprof.dll reads shrouded_engine_tweaks\plugins\ and \mods\).
extern "C" __declspec(dllexport) DWORD WINAPI PowerGetActiveScheme(HKEY, GUID**) { return ERROR_CALL_NOT_IMPLEMENTED; }
#if KSET_POWER_DLL == 2
extern "C" __declspec(dllexport) const wchar_t* marker() { return L"shrouded_engine_tweaks\\plugins"; }
#else
extern "C" __declspec(dllexport) const char* marker() { return "Forwarding exports to %s\\powrprof.dll"; }
#endif
#else
#include <aclapi.h>
#include <tlhelp32.h>

#include <atomic>
#include <thread>
#pragma comment(lib, "advapi32.lib")

namespace {
int failures = 0;
// This process's own object: deny PROCESS_VM_READ to everyone (deny = true), or back to no DACL.
bool deny_vm_read(bool deny) {
    if (!deny)
        return SetSecurityInfo(GetCurrentProcess(), SE_KERNEL_OBJECT, DACL_SECURITY_INFORMATION | UNPROTECTED_DACL_SECURITY_INFORMATION,
                               nullptr, nullptr, nullptr, nullptr) == ERROR_SUCCESS;
    SID_IDENTIFIER_AUTHORITY world = SECURITY_WORLD_SID_AUTHORITY;
    PSID everyone = nullptr;
    if (!AllocateAndInitializeSid(&world, 1, SECURITY_WORLD_RID, 0, 0, 0, 0, 0, 0, 0, &everyone)) return false;
    EXPLICIT_ACCESSW ea[2]{};
    ea[0].grfAccessPermissions = PROCESS_VM_READ;
    ea[0].grfAccessMode = DENY_ACCESS;
    ea[1].grfAccessPermissions = PROCESS_ALL_ACCESS & ~PROCESS_VM_READ;
    ea[1].grfAccessMode = GRANT_ACCESS;
    for (auto& e : ea) {
        e.Trustee.TrusteeForm = TRUSTEE_IS_SID;
        e.Trustee.ptstrName = static_cast<LPWSTR>(everyone);
    }
    PACL acl = nullptr;
    bool ok = SetEntriesInAclW(2, ea, nullptr, &acl) == ERROR_SUCCESS &&
              SetSecurityInfo(GetCurrentProcess(), SE_KERNEL_OBJECT, DACL_SECURITY_INFORMATION, nullptr, nullptr, acl, nullptr) == ERROR_SUCCESS;
    if (acl) LocalFree(acl);
    FreeSid(everyone);
    return ok;
}
void check(bool ok, const char* what) {
    std::printf("%s: %s\n", ok ? "ok  " : "FAIL", what);
    if (!ok) ++failures;
}
double us_since(std::chrono::steady_clock::time_point t0, int n) {
    return std::chrono::duration<double, std::micro>(std::chrono::steady_clock::now() - t0).count() / n;
}
void inspect_now() { (void)kset::inspect_runtime(); }
bool inspection_survives_unload() {
    __try { inspect_now(); return true; }
    __except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) {
        return false;
    }
}
}  // namespace

int main() {
    kset::ModuleWatch watch;
    check(watch.arm() && watch.notifying(), "LdrRegisterDllNotification is available and registered");
    check(watch.fingerprinting(), "the PEB loader-list fingerprint works (LdrLockLoaderLock try-only)");
    check(kset::runtime_conflict().empty(), "a clean process has no SET conflict");
    {  // the retry gate (coexist.h): a failed inventory is retried, a seen SET steps aside at once, five failures in a row do too
        kset::ConflictGate gate;
        check(gate.startup().empty() && gate.failures() == 0 && !gate.retry_due(), "the gate starts on a clean inventory");
        const kset::Inspection failed{kset::Inventory::failed, "loaded modules could not be inspected"};
        const kset::Inspection foreign{kset::Inventory::foreign, "a non-system powrprof.dll is loaded (x)"};
        const uint64_t now = GetTickCount64();
        check(gate.judge(failed, now - 2 * kset::ConflictGate::retry_ms).empty() && gate.failures() == 1 && gate.retry_due() && !gate.retry_due(),
              "one failed inventory keeps running and asks for one retry");
        check(gate.judge({}, now).empty() && gate.failures() == 0 && !gate.retry_due(), "a clean inventory resets the count");
        for (int i = 0; i < kset::ConflictGate::failures_to_step_aside - 1; ++i) check(gate.judge(failed, now).empty(), "...still running");
        check(!gate.retry_due(), "the retry waits its interval");
        check(gate.judge(failed, now).find("5 checks in a row") != std::string::npos, "five failed inventories in a row step aside");
        // kiln.types stays loaded after that verdict: it is told the verdict means "cannot tell", and keeps getting
        // (slower) retries, so a clean inventory can take it back.
        check(gate.cannot_tell(), "...and that verdict is a cannot-tell one");
        check(gate.judge(failed, now - kset::ConflictGate::passive_retry_ms + 2000).find("6 checks in a row") != std::string::npos &&
                  !gate.retry_due(), "after the verdict the retry waits passive_retry_ms");
        check(!gate.judge(failed, now - kset::ConflictGate::passive_retry_ms - 1).empty() && gate.retry_due() && !gate.retry_due(),
              "...then comes due once");
        check(gate.judge({kset::Inventory::clean, {}, 42}, now).empty() && !gate.cannot_tell() && gate.failures() == 0 &&
                  gate.modules() == 42 && !gate.retry_due(), "a clean inventory then clears it");
        for (int i = 0; i < kset::ConflictGate::failures_to_step_aside; ++i) (void)gate.judge(failed, now);
        check(gate.cannot_tell() && gate.judge(foreign, now) == foreign.text && !gate.cannot_tell() && !gate.retry_due(),
              "a runtime seen after a cannot-tell verdict is a verdict of its own (no retry)");
        gate.reset();
        check(gate.judge(failed, now).empty() && gate.judge(foreign, now) == foreign.text, "a foreign runtime that is seen steps aside at once");
    }
    {  // The field (a real client under Proton, 2026-11-11): the module snapshot of the own process failed in every check
       // for 42 s while the loader list was healthy. Wine's Toolhelp reads that list through a handle to the own pid
       // (OpenProcess, then wineserver reads); a process DACL that denies PROCESS_VM_READ makes it fail the same way.
        wchar_t exe[32768]{};
        GetModuleFileNameW(nullptr, exe, static_cast<DWORD>(std::size(exe)));
        const auto dir = std::filesystem::path(exe).parent_path();
        check(deny_vm_read(true), "this process's DACL denies PROCESS_VM_READ");
        const HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, GetCurrentProcessId());
        const DWORD error = snap == INVALID_HANDLE_VALUE ? GetLastError() : 0;
        if (snap != INVALID_HANDLE_VALUE) CloseHandle(snap);
        std::printf("Toolhelp module snapshot of this process: %s (error %lu)\n", snap == INVALID_HANDLE_VALUE ? "failed" : "made", error);
        check(snap == INVALID_HANDLE_VALUE, "...so Toolhelp's module snapshot of this process fails, as in the field");
        const kset::Inspection seen = kset::inspect_runtime();
        std::printf("inventory: %s\n", seen.text.empty() ? "clean" : seen.text.c_str());
        check(seen.state == kset::Inventory::clean && seen.modules > 2, "the inventory still reads the loaded modules: clean");
        const std::string probe = kset::handle_read_probe();
        std::printf("handle-read probe: %s\n", probe.empty() ? "works" : probe.c_str());
        check(probe == "OpenProcess(this process, PROCESS_VM_READ) failed (error 5)", "the diagnostic probe names the refused call and its code");
        kset::ConflictGate gate;
        bool running = true;
        for (int i = 0; i < 2 * kset::ConflictGate::failures_to_step_aside; ++i) running = running && gate.check().empty();
        check(running && gate.failures() == 0, "ten checks in a row: no failure, no stepping aside");
        const HMODULE branded = LoadLibraryW((dir / L"silent-loader.dll").c_str());
        check(branded && kset::inspect_runtime().state == kset::Inventory::foreign, "...and a SET-branded module that loads is still seen");
        if (branded) FreeLibrary(branded);
        check(deny_vm_read(false) && kset::handle_read_probe().empty(), "the DACL is restored, and the probe says so");
        (void)watch.due();  // the load above: consumed, so the frame counts below start from a quiet list
    }
    int due = 0;
    for (int i = 0; i < 100000; ++i) due += watch.due();
    check(due == 0, "no re-check is due without a DLL load (100000 frames)");
    const HMODULE dll = LoadLibraryW(L"version.dll");
    check(dll != nullptr, "loaded version.dll");
    check(watch.due(), "a DLL load makes a re-check due");
    check(!watch.due(), "...exactly once");
    if (dll) FreeLibrary(dll);
    check(!watch.due(), "an unload makes no re-check due at once");

    constexpr int checks = 50, gates = 1000000;
    auto t0 = std::chrono::steady_clock::now();
    for (int i = 0; i < checks; ++i) kset::runtime_conflict();
    const double full = us_since(t0, checks);
    t0 = std::chrono::steady_clock::now();
    for (int i = 0; i < gates; ++i) due += watch.due();
    const double gate = us_since(t0, gates);
    constexpr int prints = 100000;
    uint64_t fp = 0;
    int printed = 0;
    t0 = std::chrono::steady_clock::now();
    for (int i = 0; i < prints; ++i) printed += watch.fingerprint(&fp) > 0;
    const double print = us_since(t0, prints);
    std::printf("cost: full module check %.1f us, steady-state gate %.4f us, fingerprint (safety round) %.2f us\n",
                full, gate, print);
    check(gate < 1.0, "the steady-state gate costs under a microsecond");
    check(printed == prints && print < 100.0, "a fingerprint round costs under 100 us (no wineserver call)");

    // The unload above changed the list after the last baseline: at most one (harmless) check at the next round.
    {
        int settle = 0;
        const auto end = GetTickCount64() + kset::ModuleWatch::safety_ms + 500;
        while (GetTickCount64() < end) { settle += watch.due(); Sleep(16); }
        std::printf("after the unload: %d check(s) at the next safety round\n", settle);
        check(settle <= 1, "an unload costs at most one check");
    }
    // Steady state across several safety rounds: 10.5 s of 16 ms frames, zero full checks.
    {
        int steady = 0, frames = 0;
        const auto end = GetTickCount64() + 10500;
        while (GetTickCount64() < end) { steady += watch.due(); ++frames; Sleep(16); }
        std::printf("steady state: %d checks due in %d frames over 10.5 s\n", steady, frames);
        check(steady == 0, "steady state over 10.5 s (3 safety rounds): no full check");
    }

    watch.disarm();
    check(!watch.notifying(), "disarm unregisters");
    const HMODULE again = LoadLibraryW(L"version.dll");  // must not call into the unregistered watch
    if (again) FreeLibrary(again);
    wchar_t exe[32768]{};
    GetModuleFileNameW(nullptr, exe, static_cast<DWORD>(std::size(exe)));
    const auto dir = std::filesystem::path(exe).parent_path();
    check(watch.arm(), "re-arm for the silent-load safety scan");
    const HMODULE silent = LoadLibraryExW((dir / L"silent-loader.dll").c_str(), nullptr, DONT_RESOLVE_DLL_REFERENCES);
    check(silent != nullptr, "mapped a DLL without resolving imports");
    check(!kset::runtime_conflict().empty(), "the mapped loader is visible to the existing conflict policy");
    // Some loader paths (notably Wine's no-resolve path) skip the callback. Either way the check must become due
    // within the safety interval; do not assume that Windows and Wine notify on precisely the same paths.
    const bool immediately = watch.due();
    bool by_print = false;
    if (!immediately) {
        const auto end = GetTickCount64() + kset::ModuleWatch::safety_ms + 500;
        while (!by_print && GetTickCount64() < end) { by_print = watch.due(); Sleep(16); }
    }
    check(immediately || by_print, "a no-resolve (silent) load is due within the safety interval");
    std::printf("no-resolve load: %s\n", immediately ? "notified" : by_print ? "caught by the fingerprint" : "missed");
    {  // and nothing more after it
        int after = 0;
        const auto end = GetTickCount64() + kset::ModuleWatch::safety_ms + 500;
        while (GetTickCount64() < end) { after += watch.due(); Sleep(16); }
        check(after == 0, "...once: the next safety round sees no change");
    }
    if (silent) FreeLibrary(silent);
    watch.disarm();

    // Race actual DLL unloading (both explicit disarm and static-destruction-only teardown) with loader activity.
    std::atomic<bool> stop{false};
    std::thread loading([&] {
        while (!stop) { const HMODULE h = LoadLibraryW(L"version.dll"); if (h) FreeLibrary(h); }
    });
    bool unload_ok = true;
    for (int i = 0; i < 100 && unload_ok; ++i) {
        const HMODULE h = LoadLibraryW((dir / L"watch-fixture.dll").c_str());
        if (!h) { unload_ok = false; break; }
        const auto arm = reinterpret_cast<bool(*)()>(GetProcAddress(h, "arm_watch"));
        const auto disarm = reinterpret_cast<void(*)()>(GetProcAddress(h, "disarm_watch"));
        unload_ok = arm && disarm && arm();
        if (unload_ok && i % 2 == 0) disarm();
        if (!FreeLibrary(h)) unload_ok = false;
    }
    stop = true;
    loading.join();
    check(unload_ok, "100 DLL unloads during DLL loads: explicit disarm and static destructor");
    // Fallback: without a registration, at most once per interval.
    kset::ModuleWatch throttle;
    int fallback = 0;
    for (int i = 0; i < 100000; ++i) fallback += throttle.due();
    check(fallback == 1, "the unarmed fallback is due once, then throttled");
    const HMODULE racing = LoadLibraryW((dir / L"watch-fixture.dll").c_str());
    check(racing != nullptr, "loaded the inventory-unload fixture");
    if (racing) {
        unload_during_path = racing;
        const bool survived = inspection_survives_unload();
        check(unloaded_after_path, "the fixture's caller released its last reference after the path lookup");
        check(survived, "module inspection survives an unload after its path lookup");
        HMODULE retained = nullptr;
        check(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                                 reinterpret_cast<LPCWSTR>(racing), &retained), "the inventory releases its module reference after inspection");
        if (unload_during_path) { FreeLibrary(unload_during_path); unload_during_path = nullptr; }
    }
    // Listed by the walk, then unloaded before the scan holds it: no longer in the process, so not a runtime and not a
    // failed inventory either (Wine's GetModuleHandleExW reports such an address as ERROR_MOD_NOT_FOUND).
    const HMODULE leaving = LoadLibraryW((dir / L"watch-fixture.dll").c_str());
    check(leaving != nullptr, "loaded the walk-then-unload fixture");
    if (leaving) {
        unload_before_hold = leaving;
        const kset::Inspection seen = kset::inspect_runtime();
        std::printf("a module unloaded after the walk: %s\n", seen.text.empty() ? "clean" : seen.text.c_str());
        check(unloaded_before_hold, "the fixture's last reference went between the walk and the scan's own reference");
        check(seen.state == kset::Inventory::clean, "a module unloaded after the walk is skipped, not a failed inventory");
        if (unload_before_hold) { FreeLibrary(unload_before_hold); unload_before_hold = nullptr; }
    }
    {  // Issue 23: a server's POWRPROF.dll that is a proxy loader (it only forwards and loads other DLLs) is not SET, so
       // kiln-set and kiln.types keep running; a powrprof.dll with SET's markers still is. Loaded from folders that are
       // not System32 (test-wine.sh sets powrprof=n,b, so Wine maps these files, not its builtin).
        const HMODULE forwarding = LoadLibraryW((dir / L"forward" / L"powrprof.dll").c_str());
        check(forwarding != nullptr, "loaded a forwarding powrprof.dll from another folder");
        const kset::Inspection proxy = kset::inspect_runtime();
        std::printf("a forwarding powrprof.dll: %s\n", proxy.text.empty() ? "clean" : proxy.text.c_str());
        check(proxy.state == kset::Inventory::clean, "a powrprof.dll without SET's markers is not SET (issue 23)");
        if (forwarding) FreeLibrary(forwarding);
        const HMODULE set = LoadLibraryW((dir / L"set" / L"powrprof.dll").c_str());
        check(set != nullptr, "loaded a powrprof.dll with SET's markers");
        const kset::Inspection seen = kset::inspect_runtime();
        std::printf("a SET-like powrprof.dll: %s\n", seen.text.empty() ? "clean" : seen.text.c_str());
        check(seen.state == kset::Inventory::foreign && seen.text.find("SET's powrprof.dll is loaded") == 0,
              "a powrprof.dll with SET's markers is SET");
        if (set) FreeLibrary(set);
    }
    std::printf(failures ? "wine watch test: FAIL (%d)\n" : "wine watch test: PASS\n", failures);
    return failures;
}
#endif
