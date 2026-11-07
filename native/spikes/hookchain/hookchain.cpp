// SPDX-License-Identifier: MIT
// Does a Kiln (safetyhook) hook chain correctly on a function another engine has already hooked?
//
// Each scenario builds a small function in fresh executable memory (returns its argument + 1), lets a stand-in
// "other engine" hook it the way Enshwick (5-byte E9 jmp to a nearby relay) or a 14-byte absolute jmp (FF 25) hooker
// does, then arms a safetyhook inline or mid hook on the same address and calls the function. The call log shows who
// ran, in which order; the result shows the original still ran exactly once.
//
// What safetyhook does: e9_hook decodes the first >= 5 bytes at the target. An E9 rel32 (or FF 25 [rip+0]) there is
// an IP-relative instruction, so it is copied into the trampoline with its displacement rewritten for the
// trampoline's address. The trampoline therefore still jumps to the other engine's relay: Kiln's detour runs first
// (armed last), its call to the original enters the other engine's hook, which calls the real function.
#include <windows.h>

#include <cstdio>
#include <cstring>
#include <string>

#include "safetyhook.hpp"

namespace {

int g_fail = 0;
std::string g_log;

void check(bool ok, const char* what, const std::string& detail = "") {
    std::printf("%s %s%s%s\n", ok ? "PASS" : "FAIL", what, detail.empty() ? "" : ": ", detail.c_str());
    if (!ok) ++g_fail;
}

// push rbx; sub rsp,20h; mov eax,ecx; add eax,1; add rsp,20h; pop rbx; ret. Instruction boundaries at 1, 5, 7, 10, 14.
const uint8_t kFunc[] = {0x53, 0x48, 0x83, 0xEC, 0x20, 0x8B, 0xC1, 0x83, 0xC0, 0x01,
                         0x48, 0x83, 0xC4, 0x20, 0x5B, 0xC3};

struct Arena {
    uint8_t* mem = nullptr;
    uint8_t* func = nullptr;   // the game function
    uint8_t* relay = nullptr;  // the other engine's relay (near the function, as a 5-byte jmp needs)
    uint8_t* tramp = nullptr;  // the other engine's trampoline (its "call original")
    Arena() {
        mem = static_cast<uint8_t*>(VirtualAlloc(nullptr, 0x2000, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE));
        func = mem + 0x100, relay = mem + 0x800, tramp = mem + 0x900;
        std::memset(mem, 0xCC, 0x2000);
        std::memcpy(func, kFunc, sizeof kFunc);
    }
    ~Arena() { VirtualFree(mem, 0, MEM_RELEASE); }
    int call(int x) const { return reinterpret_cast<int (*)(int)>(func)(x); }
};

void abs_jmp(uint8_t* at, const void* to) {  // FF 25 00000000 <abs64>
    at[0] = 0xFF, at[1] = 0x25;
    std::memset(at + 2, 0, 4);
    const uint64_t a = reinterpret_cast<uint64_t>(to);
    std::memcpy(at + 6, &a, 8);
}
void rel_jmp(uint8_t* at, const void* to) {  // E9 rel32
    at[0] = 0xE9;
    const int32_t rel = static_cast<int32_t>(static_cast<const uint8_t*>(to) - (at + 5));
    std::memcpy(at + 1, &rel, 4);
}

// ---- the other engine ----
uint8_t* g_other_tramp = nullptr;
int other_detour(int x) {
    g_log += 'O';
    return reinterpret_cast<int (*)(int)>(g_other_tramp)(x);
}

// Enshwick style: relay = abs jmp to the detour; trampoline = the moved prologue + jmp back; target = E9 to the relay.
// `moved` bytes are copied from the target; an E9 among them (a hook armed earlier) gets its rel32 rewritten.
void other_hook_e9(Arena& a, size_t moved = 5) {
    abs_jmp(a.relay, reinterpret_cast<void*>(&other_detour));
    std::memcpy(a.tramp, a.func, moved);
    if (a.func[0] == 0xE9) {  // relocate an existing jmp (Kiln's), as Enshwick's decoder does
        int32_t rel;
        std::memcpy(&rel, a.func + 1, 4);
        rel_jmp(a.tramp, a.func + 5 + rel);
    }
    abs_jmp(a.tramp + moved, a.func + moved);
    g_other_tramp = a.tramp;
    DWORD old;
    VirtualProtect(a.func, 5, PAGE_EXECUTE_READWRITE, &old);
    rel_jmp(a.func, a.relay);
}

// 14-byte absolute jmp straight to the detour (MinHook's far fallback, many simple hookers).
void other_hook_ff25(Arena& a) {
    std::memcpy(a.tramp, a.func, 14);
    abs_jmp(a.tramp + 14, a.func + 14);
    g_other_tramp = a.tramp;
    abs_jmp(a.func, reinterpret_cast<void*>(&other_detour));
}

// ---- Kiln ----
safetyhook::InlineHook g_kiln;
int kiln_detour(int x) {
    g_log += 'K';
    return g_kiln.call<int>(x);
}
void kiln_mid(safetyhook::Context& ctx) {
    g_log += 'M';
    ctx.rcx += 1;  // a mid hook that changes the argument: 41 -> 42, result 43
}

std::string hex(const uint8_t* p, size_t n) {
    std::string s;
    char b[4];
    for (size_t i = 0; i < n; ++i) std::snprintf(b, sizeof b, "%02X ", p[i]), s += b;
    return s;
}

void e9_first_inline() {
    std::puts("-- another engine's E9 jmp first, then a Kiln inline hook");
    Arena a;
    other_hook_e9(a);
    g_log.clear();
    check(a.call(41) == 42 && g_log == "O", "the other engine's hook alone", g_log);
    auto r = safetyhook::InlineHook::create(a.func, reinterpret_cast<void*>(&kiln_detour));
    check(r.has_value(), "safetyhook hooks a target that starts with a jmp");
    if (!r) return;
    g_kiln = std::move(*r);
    const uint8_t* t = g_kiln.trampoline().data();
    int32_t rel = 0;
    std::memcpy(&rel, t + 1, 4);
    check(t[0] == 0xE9 && t + 5 + rel == a.relay, "the trampoline starts with the other engine's jmp, retargeted",
          hex(t, 5));
    g_log.clear();
    const int v = a.call(41);
    check(v == 42 && g_log == "KO", "both run: Kiln (armed last), then the other engine, then the original once",
          g_log + " -> " + std::to_string(v));
    g_kiln = {};
    check(a.func[0] == 0xE9, "Kiln's unhook restores the other engine's jmp");
    g_log.clear();
    check(a.call(41) == 42 && g_log == "O", "the other engine's hook still works after Kiln unhooks", g_log);
}

void ff25_first_inline() {
    std::puts("-- another engine's 14-byte FF 25 jmp first, then a Kiln inline hook");
    Arena a;
    other_hook_ff25(a);
    auto r = safetyhook::InlineHook::create(a.func, reinterpret_cast<void*>(&kiln_detour));
    check(r.has_value(), "safetyhook hooks a target that starts with FF 25 [rip+0]");
    if (!r) return;
    g_kiln = std::move(*r);
    g_log.clear();
    const int v = a.call(41);
    check(v == 42 && g_log == "KO", "both run in order", g_log + " -> " + std::to_string(v));
    g_kiln = {};
    g_log.clear();
    check(a.call(41) == 42 && g_log == "O", "the other engine's hook still works after Kiln unhooks", g_log);
}

void e9_first_mid() {
    std::puts("-- another engine's E9 jmp first, then a Kiln mid hook");
    Arena a;
    other_hook_e9(a);
    auto r = safetyhook::MidHook::create(a.func, &kiln_mid);
    check(r.has_value(), "safetyhook mid hook on a target that starts with a jmp");
    if (!r) return;
    auto mid = std::move(*r);
    g_log.clear();
    const int v = a.call(41);
    check(v == 43 && g_log == "MO", "the mid hook runs, then the other engine, then the original with its change",
          g_log + " -> " + std::to_string(v));
}

void kiln_first() {
    std::puts("-- Kiln first, then another engine that relocates Kiln's jmp (Enshwick's decoder)");
    Arena a;
    auto r = safetyhook::InlineHook::create(a.func, reinterpret_cast<void*>(&kiln_detour));
    check(r.has_value(), "Kiln hooks the clean function");
    if (!r) return;
    g_kiln = std::move(*r);
    other_hook_e9(a);
    g_log.clear();
    const int v = a.call(41);
    check(v == 42 && g_log == "OK", "both run: the other engine (armed last), then Kiln, then the original once",
          g_log + " -> " + std::to_string(v));
    // Known limit: safetyhook's disable() writes its saved prologue back unconditionally. Unhooking Kiln *under*
    // another engine's later jmp removes that jmp too (the other engine's hook stops running, and its trampoline
    // now leads into Kiln's freed trampoline). Kiln's hooks normally stay armed for the process lifetime.
    g_kiln = {};
    check(std::memcmp(a.func, kFunc, 5) == 0, "Kiln's unhook writes the original prologue back over the other's jmp",
          hex(a.func, 5));
    g_log.clear();
    const int w = a.call(41);
    check(w == 42 && g_log.empty(), "so the function runs vanilla: the other engine's hook is gone (known limit)",
          "log '" + g_log + "'");
}

}  // namespace

int main() {
    e9_first_inline();
    ff25_first_inline();
    e9_first_mid();
    kiln_first();
    std::printf(g_fail ? "%d FAILED\n" : "all passed\n", g_fail);
    return g_fail ? 1 : 0;
}
