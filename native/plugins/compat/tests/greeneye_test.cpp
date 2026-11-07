// SPDX-License-Identifier: MIT
// A Greeneye mod (API 2) for compathost: exercises every GreeneyeHost function the way Greeneye's mods use them.
//   find + hook:          gt_target (12 whole-instruction bytes) -> detour adds 1000 under a lease
//   find + redirectCall:  the "call rel32" after the marker in gt_caller -> detour adds 100
//   read:                 the bytes at gt_target as they were before the hook
//   keep:                 the same zeroed block for the same key
//   enter/leave, poll, log, generation, modDir, gameDir, side
// Each check logs "CHECK <name> ok|fail" through host->log. Built with GT_BAD: a mod whose short hook is refused.
#include <cstdio>
#include <cstring>
#include <string>
#include <limits>

#include <greeneye/jumppatch.h>

#include <greeneye/greeneye.h>

namespace {
const GreeneyeHost* g_host = nullptr;
int (*g_orig)(int, int) = nullptr;
int (*g_orig_call)(int, int) = nullptr;
uint8_t* g_target = nullptr;
uint8_t g_expectedRead[12] = {};
int g_preDelta = 0;
// gt_target, its alignment padding and the start of gt_target2. A host safetyhook (--prehook) replaces gt_target's first
// 8 bytes with "jmp rel32" + nops; Greeneye's find behind a hook needs 6 exact bytes after it, so the pattern reaches
// into the next function (the 3 padding bytes are wildcards).
constexpr const char* kTargetPattern = "8D 04 11 05 DE C0 17 5A 90 90 90 90 C3 ?? ?? ?? 8D 04 51 C3";
void check(const char* name, bool ok);

DWORD WINAPI wrong_thread_hook(void*) {
    return g_host->hook(g_target, reinterpret_cast<void*>(1), 12, nullptr, 0) == nullptr ? 0 : 1;
}

DWORD WINAPI find_thread(void*) {
    return g_host->find(kTargetPattern, nullptr) ? 0 : 1;
}

bool valid_value(int value) {
    value -= g_preDelta;
    return value >= 5 && value <= 20005 && (value - 5) % 1000 == 0;
}

DWORD WINAPI exercise_live_hooks(void*) {
    Sleep(100); // after the factory succeeded or faulted and staged sites were retired
    const int original = g_orig ? g_orig(2, 3) - 0x5A17C0DE : -1;
    check("retained_original_callable", valid_value(original));
    if (GetFileAttributesW((std::wstring(g_host->modDir) + L"hot-fault").c_str()) != INVALID_FILE_ATTRIBUTES ||
        GetFileAttributesW((std::wstring(g_host->modDir) + L"hot-invalid").c_str()) != INVALID_FILE_ATTRIBUTES)
        return 0; // this generation was deliberately rejected; only its retained original should run
    const ULONGLONG end = GetTickCount64() + (g_host->hotReload ? 1500 : 80);
    bool ok = true, observed = false;
    while (GetTickCount64() < end) {
        // Start has returned before any live patch can appear; no uninitialized original is callable.
        if (g_target[0] != 0x8D) {
            observed = true;
            const int value = reinterpret_cast<int (*)(int, int)>(g_target)(2, 3) - 0x5A17C0DE;
            ok &= valid_value(value); // stale generations call through; other mods remain in the chain
            uint8_t bytes[12] = {};
            g_host->read(g_target, bytes, sizeof bytes);
            ok &= !std::memcmp(bytes, g_expectedRead, sizeof bytes);
        }
        Sleep(1);
    }
    check("concurrent_hooks_and_read", observed && ok);
    return 0;
}

void check_write_protection() {
    auto* pages = static_cast<uint8_t*>(VirtualAlloc(nullptr, 8192, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
    if (!pages) { check("write_page_protections", false); return; }
    DWORD old = 0;
    VirtualProtect(pages + 4096, 4096, PAGE_READONLY, &old);
    const uint8_t bytes[4] = {1, 2, 3, 4};
    const bool wrote = jumppatch::Write(pages + 4094, bytes, sizeof bytes);
    MEMORY_BASIC_INFORMATION a{}, b{};
    VirtualQuery(pages, &a, sizeof a);
    VirtualQuery(pages + 4096, &b, sizeof b);
    check("write_page_protections", wrote && !std::memcmp(pages + 4094, bytes, 4) &&
          a.Protect == PAGE_READWRITE && b.Protect == PAGE_READONLY);
    check("write_fault_contained", !jumppatch::Write(pages + 4096, reinterpret_cast<void*>(1), 4));
    VirtualQuery(pages + 4096, &b, sizeof b);
    check("write_fault_restores_protection", b.Protect == PAGE_READONLY);
    check("write_wrapping_range", !jumppatch::Write(reinterpret_cast<void*>((std::numeric_limits<uintptr_t>::max)() - 1), bytes, 4));
    VirtualFree(pages, 0, MEM_RELEASE);
    check("alloc_near_huge_refused", !jumppatch::AllocNear(pages, (std::numeric_limits<size_t>::max)()));
}

void check(const char* name, bool ok) {
    char line[160];
    std::snprintf(line, sizeof line, "CHECK %s %s", name, ok ? "ok" : "fail");
    g_host->log(line);
}

int detour(int a, int b) {
    greeneye::Lease lease;
    if (!lease) return g_orig(a, b);
    return g_orig(a, b) + 1000;
}

int detour_call(int a, int b) {
    greeneye::Lease lease;
    if (!lease) return g_orig_call(a, b);
    return g_orig_call(a, b) + 100;
}
}  // namespace

GREENEYE_MOD_START {
    greeneye::UseHost(host);
    g_host = host;
    if (auto delta = reinterpret_cast<int (*)()>(GetProcAddress(GetModuleHandleW(nullptr), "gt_prehook_delta")))
        g_preDelta = delta();
#ifdef GT_LAB
    // Lab probe (a real dedicated server): find the game's chat-log function (Kiln's chat service may have hooked it
    // already, so this also exercises "find behind another hook"), read its first bytes as they were, keep, poll.
    // No hook: the probe never changes game code.
    {
        uint8_t* f = host->find("40 55 56 41 54 41 55 41 57 48 83 EC 30 49 8B 41 08 33 ED 4D 8B F9 45 0F B6 E0 48 8B F2 4C 8B E9 48 3D", nullptr);
        char line[200];
        uint8_t b[13] = {};
        if (f) host->read(f, b, sizeof b);
        std::snprintf(line, sizeof line, "lab: side=%d gen=%u ChatLogAdd %s at rva 0x%llx; live first byte %02X; before hooks %02X %02X %02X %02X",
                      host->side, host->generation, f ? "found" : "NOT FOUND",
                      f ? static_cast<unsigned long long>(f - reinterpret_cast<uint8_t*>(GetModuleHandleW(nullptr))) : 0ull,
                      f ? f[0] : 0, b[0], b[1], b[2], b[3]);
        host->log(line);
        check("lab_find", f != nullptr);
        // read lays back only Greeneye's own hooks: another loader's jump (Kiln's chat detour) stays visible, as under
        // Greeneye Loader; the bytes after it are the original ones.
        check("lab_read", f && b[0] == f[0] && (b[0] == 0x40 || b[0] == 0xE9));
        check("lab_keep", host->keep("lab", 8) != nullptr);
        host->poll();
        return true;
    }
#endif
#ifdef GT_BAD
    // A hook shorter than Greeneye's 12-byte jump is refused, and the loader then declines the whole start.
    uint8_t* t = host->find(kTargetPattern, nullptr);
    check("short_hook_refused", host->hook(t, reinterpret_cast<void*>(&detour), 5, nullptr, 0) == nullptr);
    return true;
#endif
    check("host_has_hooks", greeneye::HostHasHooks(host));
    check("api_version", host->apiVersion == kGreeneyeApiVersion && host->size >= sizeof(GreeneyeHost));
    check("generation", host->generation >= 1);
    check("hot_reload_flag", host->generation == 1 || host->hotReload);
    check("mod_dir_slash", host->modDir && host->modDir[0] && host->modDir[wcslen(host->modDir) - 1] == L'\\');
    check("game_dir_slash", host->gameDir && host->gameDir[0] && host->gameDir[wcslen(host->gameDir) - 1] == L'\\');
    check("side_known", host->side == kGreeneyeClient || host->side == kGreeneyeServer);
    char side[64];
    std::snprintf(side, sizeof side, "side=%d hotReload=%d", host->side, host->hotReload ? 1 : 0);
    host->log(side);

    HANDLE searchers[4] = {};
    for (HANDLE& t : searchers) t = CreateThread(nullptr, 0, find_thread, nullptr, 0, nullptr);
    bool concurrentFind = true;
    for (HANDLE t : searchers) {
        DWORD result = 1;
        if (!t || WaitForSingleObject(t, 5000) != WAIT_OBJECT_0 || !GetExitCodeThread(t, &result) || result) concurrentFind = false;
        if (t) CloseHandle(t);
    }
    check("find_concurrent_initialization", concurrentFind);
    check("find_null_refused", host->find(nullptr, nullptr) == nullptr);
    check("find_invalid_hex_refused", host->find("GG", nullptr) == nullptr);
    check("find_truncated_hex_refused", host->find("8D 0", nullptr) == nullptr);
    check("find_empty_refused", host->find(" \t\r\n", nullptr) == nullptr);
    const std::string tooLong = std::string("90 ") + std::string(1024 * 1024, '?');
    check("find_oversized_refused", host->find(tooLong.c_str(), nullptr) == nullptr);
    check_write_protection();
    uint8_t guard = 0xA5;
    host->read(nullptr, &guard, 1);
    check("read_null_contained", guard == 0xA5);
    host->read(reinterpret_cast<void*>((std::numeric_limits<uintptr_t>::max)() - 1), &guard, 4);
    check("read_wrapping_range_contained", guard == 0xA5);
    host->read(&guard, nullptr, 1);
    check("read_null_destination_contained", guard == 0xA5);
    host->leave(); // unmatched leave must not corrupt TLS lease bookkeeping

    uint8_t* target = host->find(kTargetPattern, nullptr);
    check("find", target != nullptr);
    check("find_other_module_null", host->find("8D 04 11 05", reinterpret_cast<void*>(0x1234)) == nullptr);
    if (target) {
        g_target = target;
        HANDLE wrong = CreateThread(nullptr, 0, wrong_thread_hook, nullptr, 0, nullptr);
        DWORD result = 1;
        if (wrong) {
            WaitForSingleObject(wrong, 5000);
            GetExitCodeThread(wrong, &result);
            CloseHandle(wrong);
        }
        check("hook_from_other_thread_refused", result == 0);
        uint8_t liveBefore[12] = {};
        std::memcpy(liveBefore, target, sizeof liveBefore);
        host->read(target, g_expectedRead, sizeof g_expectedRead);
        g_orig = reinterpret_cast<int (*)(int, int)>(host->hook(target, reinterpret_cast<void*>(&detour), 12, nullptr, 0));
        check("hook", g_orig != nullptr);
        uint8_t before[12] = {};
        host->read(target, before, sizeof before);
        check("read_before_hooks", !std::memcmp(before, g_expectedRead, sizeof before));
        check("unpublished_while_starting", !std::memcmp(liveBefore, target, sizeof liveBefore));
        check("find_after_hook", host->find(kTargetPattern, nullptr) == target);
    }
    uint8_t* marker = host->find("B8 DE C0 A7 7E E8", nullptr);
    check("find_call_marker", marker != nullptr);
    if (marker) {
        g_orig_call = reinterpret_cast<int (*)(int, int)>(host->redirectCall(marker + 5, reinterpret_cast<void*>(&detour_call)));
        check("redirect_call", g_orig_call != nullptr);
    }
    std::string keepKey("state:");
    for (const wchar_t* p = host->modDir; *p; ++p) keepKey.push_back(static_cast<char>(*p));
    auto* a = static_cast<uint32_t*>(host->keep(keepKey.c_str(), 16));
    auto* b = static_cast<uint32_t*>(host->keep(keepKey.c_str(), 64));
    // keep: zeroed for the first generation; a reload gets the same block back, with what the old one wrote.
    check("keep_same_block", a && a == b && a[0] == (host->generation == 1 ? 0u : 42u));
    if (a) a[0] = 42;
    char gen[48];
    std::snprintf(gen, sizeof gen, "generation=%u", host->generation);
    host->log(gen);
    {
        // A generation becomes the active one only after GreeneyeModStart returned: a lease taken while it starts is
        // inactive, including a reload whose swapping thread already holds the exclusive generation gate.
        greeneye::Lease lease;
        check("lease_inactive_while_starting", !static_cast<bool>(lease));
    }
    if (host->generation == 1 && target) {
        HANDLE live = CreateThread(nullptr, 0, exercise_live_hooks, nullptr, 0, nullptr);
        check("live_worker_created", live != nullptr);
        if (live) CloseHandle(live);
    }
    host->poll();
    check("poll_returns", true);
    return true;
}

#ifndef GT_LAB
namespace {
bool hot_prepare() {
    g_host->log("hot prepare");
    if (GetFileAttributesW((std::wstring(g_host->modDir) + L"prepare-fault").c_str()) != INVALID_FILE_ATTRIBUTES)
        RaiseException(EXCEPTION_ACCESS_VIOLATION, 0, 0, nullptr);
    {
        greeneye::Lease lease;
        check("lease_inactive_during_prepare", !static_cast<bool>(lease));
    }
    const int value = reinterpret_cast<int (*)(int, int)>(g_target)(2, 3) - 0x5A17C0DE;
    check("prepare_can_call_own_detour", valid_value(value));
    return true;
}
bool hot_ready() {
    g_host->log("hot ready");
    if (GetFileAttributesW((std::wstring(g_host->modDir) + L"ready-fault").c_str()) != INVALID_FILE_ATTRIBUTES)
        RaiseException(EXCEPTION_ACCESS_VIOLATION, 0, 0, nullptr);
    return true;
}
void hot_resume() {
    g_host->log("hot resume");
    if (GetFileAttributesW((std::wstring(g_host->modDir) + L"resume-fault").c_str()) != INVALID_FILE_ATTRIBUTES)
        RaiseException(EXCEPTION_ACCESS_VIOLATION, 0, 0, nullptr);
}
}
GREENEYE_MOD_HOT {
    if (g_host && GetFileAttributesW((std::wstring(g_host->modDir) + L"hot-fault").c_str()) != INVALID_FILE_ATTRIBUTES)
        RaiseException(EXCEPTION_ACCESS_VIOLATION, 0, 0, nullptr);
    if (g_host && GetFileAttributesW((std::wstring(g_host->modDir) + L"hot-invalid").c_str()) != INVALID_FILE_ATTRIBUTES)
        return reinterpret_cast<const GreeneyeHot*>(1);
    static const GreeneyeHot api{sizeof(GreeneyeHot), &hot_prepare, &hot_ready, &hot_resume};
    return &api;
}
#endif
