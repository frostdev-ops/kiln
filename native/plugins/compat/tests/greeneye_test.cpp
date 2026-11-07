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

#include <greeneye/greeneye.h>

namespace {
const GreeneyeHost* g_host = nullptr;
int (*g_orig)(int, int) = nullptr;
int (*g_orig_call)(int, int) = nullptr;

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
    uint8_t* t = host->find("8D 04 11 05 DE C0 17 5A 90 90 90 90 C3", nullptr);
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

    uint8_t* target = host->find("8D 04 11 05 DE C0 17 5A 90 90 90 90 C3", nullptr);
    check("find", target != nullptr);
    check("find_other_module_null", host->find("8D 04 11 05", reinterpret_cast<void*>(0x1234)) == nullptr);
    if (target) {
        g_orig = reinterpret_cast<int (*)(int, int)>(host->hook(target, reinterpret_cast<void*>(&detour), 12, nullptr, 0));
        check("hook", g_orig != nullptr);
        uint8_t before[12] = {};
        host->read(target, before, sizeof before);
        check("read_before_hooks", before[0] == 0x8D && before[3] == 0x05 && before[11] == 0x90);
        check("live_is_patched", target[0] != 0x8D);
        check("find_after_hook", host->find("8D 04 11 05 DE C0 17 5A 90 90 90 90 C3", nullptr) == target);
    }
    uint8_t* marker = host->find("B8 DE C0 A7 7E E8", nullptr);
    check("find_call_marker", marker != nullptr);
    if (marker) {
        g_orig_call = reinterpret_cast<int (*)(int, int)>(host->redirectCall(marker + 5, reinterpret_cast<void*>(&detour_call)));
        check("redirect_call", g_orig_call != nullptr);
    }
    auto* a = static_cast<uint32_t*>(host->keep("state", 16));
    auto* b = static_cast<uint32_t*>(host->keep("state", 64));
    // keep: zeroed for the first generation; a reload gets the same block back, with what the old one wrote.
    check("keep_same_block", a && a == b && a[0] == (host->generation == 1 ? 0u : 42u));
    if (a) a[0] = 42;
    char gen[48];
    std::snprintf(gen, sizeof gen, "generation=%u", host->generation);
    host->log(gen);
    if (host->generation == 1) {
        // A generation becomes the active one only after GreeneyeModStart returned: a lease taken while it starts is
        // inactive (the detours' leases are active later, which gt_target's +1000 shows). Not during a reload: the
        // swap holds this mod's gate then, and a lease would wait for it (as under Greeneye Loader).
        greeneye::Lease lease;
        check("lease_inactive_while_starting", !static_cast<bool>(lease));
    }
    host->poll();
    check("poll_returns", true);
    return true;
}
