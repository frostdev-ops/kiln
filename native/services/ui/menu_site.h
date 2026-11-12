// SPDX-License-Identifier: MIT
// kiln.ui: is this call of the game's main-menu entry renderer the Settings entry, the one Kiln draws its own entries
// before (game.cpp)? Plain logic: no Windows, no game memory, so tests/menu_site_test.cpp runs it on the build machine.
//
// The game calls the renderer once per entry, each from its own call site. Kiln knows the Settings entry by the
// renderer's return address: the address after the game's Settings call (two sites, one per layout branch).
//
// Another module can stand between the two. ShroudForge's native runtime (modules/game-runtime/native/main-menu/
// manage_native_main_menu.cpp, 1.7.6 to 1.7.9, also when Kiln hosts it) rewrites both Settings calls to enter its own
// function, which calls the renderer itself: for its "ShroudForge" row while its Modloader is there, then for the
// Settings entry. The renderer's return address is then inside that module, and with the return-address rule alone
// Kiln's entries were never drawn: a real client with ShroudForge mods in the profile had no "Mods" and no "SET tools"
// in its main menu (2026-11-11). The game's own return address is still on the stack, above the wrapper's frame: its
// call pushed it before the patched call entered the wrapper. So a renderer call from outside the game's image, with
// one of the two site addresses in the stack slots above its return address, is the Settings entry reached through a
// wrapper. A call from inside the game's image at any other address is another entry of the game's.
//
// A wrapper may call the renderer more than once for one Settings entry (its own row, then the game's): Kiln's entries
// go before the first of those calls in a UI frame (OncePerFrame). The game's own call draws them as it always did,
// except in a frame where a wrapped call already has (a wrapper that ends in a tail call reaches the renderer with the
// game's return address).
#pragma once

#include <cstddef>
#include <cstdint>

namespace kui::menu_site {

enum class Call {
    other,    // not the Settings entry
    direct,   // the game's own Settings call
    wrapped,  // the Settings call, reached through another module's code
};

struct Where {
    uintptr_t caller = 0;                  // the renderer's return address
    uintptr_t site[2] = {0, 0};            // the addresses the game's two Settings calls return to (0: unknown)
    uintptr_t image_lo = 0, image_hi = 0;  // the game's executable image, [lo, hi); lo == hi: unknown
    const uintptr_t* above = nullptr;      // the stack slots above the renderer's return address, nearest first
    size_t slots = 0;                      // how many of them may be read
};

// How far above the renderer's return address the game's own is looked for: 512 slots (4 KiB). Kiln's build of
// ShroudForge 1.7.9's runtime keeps 0x4a8 bytes there (149 slots: its wrapper's frame holds a 1 KiB buffer of an
// inlined helper, and five saved registers); another build of it may keep more.
constexpr size_t kMaxSlots = 512;

inline Call classify(const Where& w) {
    const auto is_site = [&](uintptr_t v) { return v != 0 && (v == w.site[0] || v == w.site[1]); };
    if (is_site(w.caller)) return Call::direct;
    if (w.image_lo < w.image_hi && w.caller >= w.image_lo && w.caller < w.image_hi) return Call::other;
    if (!w.above) return Call::other;
    const size_t n = w.slots < kMaxSlots ? w.slots : kMaxSlots;
    for (size_t i = 0; i < n; ++i)
        if (is_site(w.above[i])) return Call::wrapped;
    return Call::other;
}

// The first wrapped Settings call of a UI frame draws Kiln's entries; the wrapper's further calls in that frame do
// not, and neither does a direct call that follows them in it.
struct OncePerFrame {
    bool seen = false;
    uint64_t frame = 0;
    bool drew(uint64_t now) const { return seen && frame == now; }
    bool first(uint64_t now) {
        if (drew(now)) return false;
        seen = true;
        frame = now;
        return true;
    }
};

// Does this call draw Kiln's entries? `frame` is the UI frame being built.
inline bool draws(Call call, OncePerFrame& wrapped, uint64_t frame) {
    switch (call) {
        case Call::direct: return !wrapped.drew(frame);
        case Call::wrapped: return wrapped.first(frame);
        default: return false;
    }
}

}  // namespace kui::menu_site
