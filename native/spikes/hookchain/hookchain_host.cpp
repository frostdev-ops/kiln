// SPDX-License-Identifier: MIT
// Real safetyhook chaining and guarded disable on Linux, without Wine or a game.
#include <sys/mman.h>
#include <array>
#include <cassert>
#include <cstdio>
#include <cstring>
#include "safetyhook.hpp"
#include "../../core/src/hook_guard.h"

namespace {
int failures = 0, calls = 0;
using Fn = int (*)(int);
safetyhook::InlineHook kiln_hook;
Fn foreign_original;
int kiln_detour(int x) { calls |= 1; return kiln_hook.call<int>(x); }
int foreign_detour(int x) { calls |= 2; return foreign_original(x); }
void kiln_mid(safetyhook::Context& ctx) { calls |= 1; ++ctx.rdi; }
void check(bool ok, const char* text) { std::printf("%s %s\n", ok ? "PASS" : "FAIL", text); failures += !ok; }
void reljmp(uint8_t* at, const void* target) {
    at[0] = 0xE9;
    auto d = static_cast<int32_t>(reinterpret_cast<intptr_t>(target) - reinterpret_cast<intptr_t>(at + 5));
    std::memcpy(at + 1, &d, 4);
}
void absjmp(uint8_t* at, const void* target) {
    at[0] = 0xFF; at[1] = 0x25;
    std::memset(at + 2, 0, 4);
    std::memcpy(at + 6, &target, 8);
}
struct Arena {
    uint8_t* mem = static_cast<uint8_t*>(mmap(nullptr, 4096, PROT_READ | PROT_WRITE | PROT_EXEC,
                                             MAP_PRIVATE | MAP_ANONYMOUS, -1, 0));
    uint8_t* fn;
    Arena() {
        if (mem == MAP_FAILED) std::abort();
        fn = mem + 0x100;
        std::memset(mem, 0x90, 4096);
        const uint8_t code[] = {0x89, 0xF8, 0x83, 0xC0, 0x01, 0xC3}; // mov eax,edi; add eax,1; ret
        std::memcpy(fn, code, sizeof code);
    }
    ~Arena() { munmap(mem, 4096); }
    int call() { calls = 0; return reinterpret_cast<Fn>(fn)(41); }
    void cover() {
        auto* relay = mem + 0x800;
        auto* tramp = mem + 0x900;
        assert(fn[0] == 0xE9);
        int32_t d;
        std::memcpy(&d, fn + 1, 4);
        reljmp(tramp, fn + 5 + d); // move Kiln's jump, as the foreign engine would
        absjmp(tramp + 5, fn + 5);
        absjmp(relay, reinterpret_cast<void*>(&foreign_detour));
        foreign_original = reinterpret_cast<Fn>(tramp);
        reljmp(fn, relay);
    }
};
template <class Hook>
void covered_disable(Arena& a, Hook& hook, int value, bool unguarded) {
    kiln::hooks::HookGuard guard;
    guard.armed(a.fn);
    std::array<uint8_t, 14> own, foreign;
    std::memcpy(own.data(), a.fn, own.size());
    a.cover();
    std::memcpy(foreign.data(), a.fn, foreign.size());
    check(a.call() == value && calls == 3, "host: both real hooks chain before disable");
    int logs = 0;
    bool disabled = unguarded ? static_cast<bool>(hook.disable()) : guard.disable(hook, [&] { ++logs; });
    check(!disabled && hook.enabled() && std::memcmp(a.fn, foreign.data(), foreign.size()) == 0,
          "host: guarded disable preserves foreign jump and armed trampoline");
    check(a.call() == value && calls == 3, "host: both real trampolines remain callable after refused disable");
    if (!unguarded) {
        check(!guard.disable(hook, [&] { ++logs; }) && logs == 1, "host: covered hook logs once");
        std::memcpy(a.fn, own.data(), own.size());
        check(guard.disable(hook, [&] { ++logs; }) && !hook.enabled(), "host: uncovered hook disables normally");
    }
    check(a.call() == 42 && calls == 0, "host: normal disable restores original behaviour");
}
}

int main(int argc, char**) {
    const bool unguarded = argc > 1; // red control: demonstrate the original unconditional disable
    {
        Arena a;
        kiln_hook = safetyhook::create_inline(a.fn, reinterpret_cast<void*>(&kiln_detour));
        if (!kiln_hook) return 2;
        covered_disable(a, kiln_hook, 42, unguarded);
        kiln_hook.reset();
    }
    {
        Arena a;
        auto mid = safetyhook::create_mid(a.fn, &kiln_mid);
        if (!mid) return 2;
        covered_disable(a, mid, 43, unguarded);
        mid.reset();
    }
    return failures ? 1 : 0;
}
