// Greeneye Loader jumppatch.h (MIT, Copyright (c) 2026 Greeneye0; licenses/GreeneyeLoader-LICENSE.txt), from
// github.com/Greeneye0/greeneye-loader-enshrouded 1.2.2 (commit 4b13108), with bounded writes and protection restoration.
#pragma once
// Another mod's hook at the start of a function, and chaining onto it. Shared by every mod in
// this folder (see common/README.md): release hooks use it through each mod's
// InstallHookChained, and the hot loaders use it for their permanent hooks.
//
// Quick Deposit, Auto Loot, Continue and Find write "mov rax, imm64; jmp rax". safetyhook
// (Creative Mode) writes "jmp rel32" to a stub near the function, or "jmp [rip+disp32]".
// Chaining points that jump at our detour and hands back its previous destination, which leads
// (through the other mod) to the original code. The other mod's bytes after its jump, which its
// trampoline returns to, are never touched.
#include <windows.h>
#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>

namespace jumppatch {

enum Kind { None, MovRax, Rel32, RipIndirect };

inline Kind KindOf(const uint8_t* t)
{
    if (t[0] == 0x48 && t[1] == 0xB8 && t[10] == 0xFF && t[11] == 0xE0)
        return MovRax;
    if (t[0] == 0xE9)
        return Rel32;
    if (t[0] == 0xFF && t[1] == 0x25)
        return RipIndirect;
    return None;
}

// Bytes the jump of that kind occupies.
inline size_t Length(Kind kind)
{
    switch (kind) {
    case MovRax:
        return 12;
    case Rel32:
        return 5;
    case RipIndirect:
        return 6;
    default:
        return 0;
    }
}

// Where the jump at t leads, or null if t is no jump.
inline void* Destination(const uint8_t* t)
{
    int32_t d;
    void* p = nullptr;
    switch (KindOf(t)) {
    case MovRax:
        memcpy(&p, t + 2, 8);
        return p;
    case Rel32:
        memcpy(&d, t + 1, 4);
        return const_cast<uint8_t*>(t) + 5 + d;
    case RipIndirect:
        memcpy(&d, t + 2, 4);
        memcpy(&p, t + 6 + d, 8);
        return p;
    default:
        return nullptr;
    }
}

// Executable memory a rel32 jump at from can reach.
inline uint8_t* AllocNear(const uint8_t* from, size_t size)
{
    SYSTEM_INFO si;
    GetSystemInfo(&si);
    const uintptr_t gran = si.dwAllocationGranularity, at = reinterpret_cast<uintptr_t>(from);
    const uintptr_t reach = 0x7FF00000;
    if (!from || !size || size > reach) return nullptr;
    uintptr_t lo = at > reach ? at - reach : 0;
    uintptr_t hi = at > (std::numeric_limits<uintptr_t>::max)() - reach
                       ? (std::numeric_limits<uintptr_t>::max)() : at + reach;
    lo = (std::max)(lo, reinterpret_cast<uintptr_t>(si.lpMinimumApplicationAddress));
    hi = (std::min)(hi, reinterpret_cast<uintptr_t>(si.lpMaximumApplicationAddress));
    MEMORY_BASIC_INFORMATION mbi;
    for (uintptr_t p = lo; p < hi && VirtualQuery(reinterpret_cast<void*>(p), &mbi, sizeof(mbi));
         p = reinterpret_cast<uintptr_t>(mbi.BaseAddress) + mbi.RegionSize) {
        if (mbi.State != MEM_FREE)
            continue;
        uintptr_t base = (reinterpret_cast<uintptr_t>(mbi.BaseAddress) + gran - 1) & ~(gran - 1);
        if (base > hi || size > hi - base || base < reinterpret_cast<uintptr_t>(mbi.BaseAddress) ||
            base - reinterpret_cast<uintptr_t>(mbi.BaseAddress) > mbi.RegionSize ||
            size > mbi.RegionSize - (base - reinterpret_cast<uintptr_t>(mbi.BaseAddress)))
            continue;
        if (void* m = VirtualAlloc(reinterpret_cast<void*>(base), size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE))
            return static_cast<uint8_t*>(m);
    }
    return nullptr;
}

// The copy is a separate, never-inlined call on purpose: clang-cl (without /EHa) only maps a hardware fault to a
// __try's handler when it happens inside a call made from the guarded block; an inlined memcpy of a few bytes becomes
// plain moves there, and a fault in them escapes the __except.
__declspec(noinline) inline void CopyBytes(void* at, const void* bytes, size_t n)
{
    memcpy(at, bytes, n);
}

inline bool Write(void* at, const void* bytes, size_t n)
{
    if (!n) return true;
    const auto address = reinterpret_cast<uintptr_t>(at);
    if (!at || !bytes || n > (std::numeric_limits<uintptr_t>::max)() - address) return false;
    struct Region { void* at; size_t size; DWORD protection; } regions[16];
    size_t count = 0;
    const uintptr_t end = address + n;
    // No allocation: this is also used while the other threads are suspended for a hook commit.
    for (uintptr_t p = address; p < end;) {
        MEMORY_BASIC_INFORMATION mbi{};
        if (count == 16 || !VirtualQuery(reinterpret_cast<void*>(p), &mbi, sizeof mbi) || mbi.State != MEM_COMMIT)
            return false;
        const auto base = reinterpret_cast<uintptr_t>(mbi.BaseAddress);
        if (!mbi.RegionSize || mbi.RegionSize > (std::numeric_limits<uintptr_t>::max)() - base) return false;
        const uintptr_t next = (std::min)(end, base + mbi.RegionSize);
        if (next <= p) return false;
        regions[count++] = {reinterpret_cast<void*>(p), static_cast<size_t>(next - p), mbi.Protect};
        p = next;
    }
    size_t changed = 0;
    for (; changed < count; ++changed) {
        DWORD old = 0;
        if (!VirtualProtect(regions[changed].at, regions[changed].size, PAGE_EXECUTE_READWRITE, &old)) break;
        regions[changed].protection = old;
    }
    bool copied = false;
    if (changed == count) {
        __try { CopyBytes(at, bytes, n); copied = true; }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
    }
    bool restored = true;
    while (changed) {
        Region& r = regions[--changed];
        DWORD ignored = 0;
        if (!VirtualProtect(r.at, r.size, r.protection, &ignored)) restored = false;
    }
    if (copied) FlushInstructionCache(GetCurrentProcess(), at, n);
    return copied && restored;
}

// Points the jump at t to detour. Only the jump's own operand changes.
inline bool Redirect(uint8_t* t, void* detour)
{
    switch (KindOf(t)) {
    case MovRax:
        return Write(t + 2, &detour, 8);
    case Rel32: {
        uint8_t* stub = AllocNear(t, 16); // jmp [rip+0]; detour
        if (!stub)
            return false;
        stub[0] = 0xFF;
        stub[1] = 0x25;
        memset(stub + 2, 0, 4);
        memcpy(stub + 6, &detour, 8);
        FlushInstructionCache(GetCurrentProcess(), stub, 16);
        int32_t d = static_cast<int32_t>(stub - (t + 5));
        return Write(t + 1, &d, 4);
    }
    case RipIndirect: {
        int32_t d;
        memcpy(&d, t + 2, 4);
        return Write(t + 6 + d, &detour, 8);
    }
    default:
        return false;
    }
}

// Chains onto the hook jump at target: points it at detour and returns its previous
// destination (the function to call on), or null if target holds no hook jump.
inline void* Chain(uint8_t* target, void* detour)
{
    void* previous = Destination(target);
    if (!previous || !Redirect(target, detour))
        return nullptr;
    return previous;
}

// Where the "call rel32" (E8) at call leads, or null if call is no such call.
inline void* CallTarget(const uint8_t* call)
{
    if (call[0] != 0xE8)
        return nullptr;
    int32_t d;
    memcpy(&d, call + 1, 4);
    return const_cast<uint8_t*>(call) + 5 + d;
}

// Hooks one call site instead of the function it calls: points the "call rel32" at call to
// detour (through a jump stub near it) and returns what it called before, which detour calls
// on. That may be the game's function or another mod's stub, so a mod that redirected the
// same call keeps working, in either load order. Only the call's operand changes.
inline void* RedirectCall(uint8_t* call, void* detour)
{
    void* previous = CallTarget(call);
    if (!previous)
        return nullptr;
    uint8_t* stub = AllocNear(call, 16); // jmp [rip+0]; detour
    if (!stub)
        return nullptr;
    stub[0] = 0xFF;
    stub[1] = 0x25;
    memset(stub + 2, 0, 4);
    memcpy(stub + 6, &detour, 8);
    FlushInstructionCache(GetCurrentProcess(), stub, 16);
    int32_t d = static_cast<int32_t>(stub - (call + 5));
    return Write(call + 1, &d, 4) ? previous : nullptr;
}

// True if the function at p matches pattern (n entries, -1 = any byte) except that another
// mod's hook jump, and the nop/int3 padding it left, replaced its first instructions. At least
// 6 exact bytes must match behind the hook. p must be readable for max(n, 32) bytes.
inline bool MatchesBehindHook(const uint8_t* p, const int* pattern, size_t n)
{
    size_t i = Length(KindOf(p)), exact = 0;
    if (!i || n <= i)
        return false;
    while (i < n && i < 32 && (p[i] == 0x90 || p[i] == 0xCC) && pattern[i] != p[i])
        ++i;
    for (; i < n; ++i) {
        if (pattern[i] < 0)
            continue;
        if (p[i] != pattern[i])
            return false;
        ++exact;
    }
    return exact >= 6;
}

// The one place in [text, text + size) where pattern matches behind another mod's hook (see
// MatchesBehindHook), or null if there is none or more than one. For a function whose own
// signature is gone because another mod hooked it first.
inline uint8_t* FindBehindHook(uint8_t* text, size_t size, const int* pattern, size_t n)
{
    const size_t span = (std::max)(n, size_t{32});
    if (size < span)
        return nullptr;
    uint8_t* found = nullptr;
    for (size_t offset = 0; offset <= size - span; ++offset) {
        uint8_t* p = text + offset;
        if (p[0] != 0x48 && p[0] != 0xE9 && p[0] != 0xFF)
            continue;
        if (!MatchesBehindHook(p, pattern, n))
            continue;
        if (found)
            return nullptr;
        found = p;
    }
    return found;
}

} // namespace jumppatch
