// SPDX-License-Identifier: MIT
// Pure Vulkan hook checks, also exercised by tests/vk_hooks_test.cpp on the host.
#pragma once
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>

namespace kui::vk_hooks {

// safetyhook decodes up to 14 bytes plus one x86 instruction (at most 15 bytes).
inline constexpr size_t probe_size = 32;
using Bytes = std::array<uint8_t, probe_size>;

struct Snapshot {
    uintptr_t address = 0;
    size_t size = 0;  // the actual overwritten instruction span, not an arbitrary 16 bytes
    Bytes original{}, patch{};
    bool have_patch = false;
};

enum class Canary { First, Moved, Ours, Pristine, Reloaded, Foreign, Unreadable };

struct Mapping {
    uintptr_t address, owner;
    uint64_t loads, unloads;
};

inline bool replaced(const Mapping& old, const Mapping& now) {
    return old.owner && (now.loads != old.loads ||
        (now.unloads != old.unloads && now.owner != old.owner && now.address != old.address));
}

inline Canary classify(const Snapshot& old, uintptr_t now, const Bytes* bytes, bool owner_replaced) {
    if (!now || !bytes) return Canary::Unreadable;
    if (!old.address) return Canary::First;
    // A different export address alone does not prove the previous hook is dead (forwarders/aliases).
    if (now != old.address) return owner_replaced ? Canary::Moved : Canary::Foreign;
    if (old.have_patch && old.size && std::memcmp(bytes->data(), old.patch.data(), old.size) == 0) return Canary::Ours;
    if (old.size && std::memcmp(bytes->data(), old.original.data(), old.size) == 0) return Canary::Pristine;
    // A confirmed replacement of the code owner proves a new mapping, even when its prologue changed or an overlay
    // got there first. An unload notice alone, or one for some other vulkan-1.dll, is not that proof.
    return owner_replaced ? Canary::Reloaded : Canary::Foreign;
}

inline bool may_hook(Canary c) {
    return c == Canary::First || c == Canary::Moved || c == Canary::Pristine || c == Canary::Reloaded;
}

// Recognise our actual safetyhook jump even if somebody changed the NOPs/trailing bytes. Windows' pristine loader
// thunks also start with jumps: only a destination in OUR trampoline (or directly at OUR detour) counts.
inline bool own_jump(const Bytes& b, uintptr_t at, uintptr_t trampoline, size_t size, uintptr_t detour) {
    if (b[0] == 0xe9) {
        int32_t displacement;
        std::memcpy(&displacement, b.data() + 1, sizeof displacement);
        const uintptr_t to = at + 5 + static_cast<uintptr_t>(displacement);
        if (to >= trampoline && to - trampoline < size) return true;
    }
    if (b[0] == 0xff && b[1] == 0x25) {
        uint32_t displacement;
        uintptr_t to;
        std::memcpy(&displacement, b.data() + 2, sizeof displacement);
        std::memcpy(&to, b.data() + 6, sizeof to);
        if (!displacement && to == detour) return true;
    }
    return false;
}

inline bool contains(uintptr_t base, size_t size, uintptr_t address) {
    return address >= base && address - base < size;
}

}  // namespace kui::vk_hooks
