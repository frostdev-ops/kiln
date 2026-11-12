// SPDX-License-Identifier: MIT
// Host test of menu_site.h: which calls of the game's main-menu entry renderer are the Settings entry, the one Kiln
// draws its own entries before. The stacks here are laid out as the real ones are: the renderer's return address,
// then the caller's frame going up, then that caller's own return address.
#include <cstdio>
#include <vector>

#include "../menu_site.h"

using namespace kui::menu_site;

namespace {
int failures = 0, checks = 0;
void check(bool ok, const char* what) {
    ++checks;
    if (!ok) { ++failures; std::printf("FAIL %s\n", what); }
}

// the game's image and its two Settings call sites, a ShroudForge runtime DLL far above it, a code cave near the image
constexpr uintptr_t kImageLo = 0x140000000, kImageHi = 0x142a3f000;
constexpr uintptr_t kSiteA = 0x140c2b455, kSiteB = 0x140c2bab3, kPlaySite = 0x140c2a9f1;
constexpr uintptr_t kWrapper = 0x6fffee85ab19, kThunk = 0x13fff0000;

Where where(uintptr_t caller, const std::vector<uintptr_t>& above) {
    Where w;
    w.caller = caller;
    w.site[0] = kSiteA;
    w.site[1] = kSiteB;
    w.image_lo = kImageLo;
    w.image_hi = kImageHi;
    w.above = above.data();
    w.slots = above.size();
    return w;
}
}  // namespace

int main() {
    const std::vector<uintptr_t> game_frame = {0x1, 0x10020099638, 0x0, 0x140c69fa2, 0x2, 0x140bd13aa};

    // the game's own calls: the Settings entry from either layout branch, and another entry
    check(classify(where(kSiteA, game_frame)) == Call::direct, "the game's Settings call (branch A) is the Settings entry");
    check(classify(where(kSiteB, game_frame)) == Call::direct, "the game's Settings call (branch B) is the Settings entry");
    check(classify(where(kPlaySite, game_frame)) == Call::other, "another entry of the game's is not");

    // ShroudForge's wrapper: its frame (a descriptor copy, saved registers, shadow space), then the game's return
    // address its patched call pushed, then the game's frames
    std::vector<uintptr_t> wrapped = {0x0, 0x0, 0x0, 0x0, 0x7ffc0000, 0xb, 0x4a11f00d, 0x0, 0x10020099638, 0x30, 0x0, 0x0, kSiteA};
    wrapped.insert(wrapped.end(), game_frame.begin(), game_frame.end());
    check(classify(where(kWrapper, wrapped)) == Call::wrapped, "the Settings call through ShroudForge's wrapper is the Settings entry");
    // as Kiln's build of the 1.7.9 runtime lays it out (kfc-runtime.dll, draw_with_mods: sub rsp,0x480 after five
    // pushes): 0x4a8 bytes between the renderer's return address and the game's
    std::vector<uintptr_t> real(0x4a8 / 8, 0x0);
    real.push_back(kSiteA);
    real.insert(real.end(), game_frame.begin(), game_frame.end());
    check(classify(where(kWrapper, real)) == Call::wrapped, "... with the 0x4a8-byte frame of the shipped ShroudForge runtime");
    wrapped[12] = kSiteB;
    check(classify(where(kWrapper, wrapped)) == Call::wrapped, "... from the other layout branch too");
    // a wrapper that is a thunk in memory of no module, below the image
    check(classify(where(kThunk, wrapped)) == Call::wrapped, "a wrapper outside every module counts as well");
    // a wrapper around another entry: the game's return address above it is not a Settings site
    wrapped[12] = kPlaySite;
    check(classify(where(kWrapper, wrapped)) == Call::other, "a wrapper around another entry is not the Settings entry");

    // a foreign caller with no site above it (it draws an entry of its own from somewhere else)
    check(classify(where(kWrapper, game_frame)) == Call::other, "a foreign call with no Settings site above it is not");
    // the site address deeper than the window: not looked at
    std::vector<uintptr_t> deep(kMaxSlots, 0x5);
    deep.push_back(kSiteA);
    check(classify(where(kWrapper, deep)) == Call::other, "a site address beyond the window is not looked at");
    deep[kMaxSlots - 1] = kSiteA;
    check(classify(where(kWrapper, deep)) == Call::wrapped, "the last slot of the window is");
    // a caller inside the game's image is never a wrapper, whatever lies above it (a site address left in a frame)
    std::vector<uintptr_t> stale = {0x0, kSiteA, 0x0};
    check(classify(where(kPlaySite, stale)) == Call::other, "a game call with a site address above it is still another entry");

    // what is not known is not guessed
    Where unknown = where(kWrapper, wrapped);
    unknown.site[0] = unknown.site[1] = 0;
    wrapped[12] = 0;
    check(classify(unknown) == Call::other, "unknown sites match nothing, a zero slot included");
    wrapped[12] = kSiteA;
    Where no_stack = where(kWrapper, wrapped);
    no_stack.above = nullptr;
    check(classify(no_stack) == Call::other, "no readable stack: not the Settings entry");
    no_stack = where(kWrapper, wrapped);
    no_stack.slots = 5;
    check(classify(no_stack) == Call::other, "only the readable slots are looked at");
    Where no_image = where(kWrapper, wrapped);
    no_image.image_lo = no_image.image_hi = 0;
    check(classify(no_image) == Call::wrapped, "an unknown image range does not hide a wrapper");
    Where direct_no_image = where(kSiteA, game_frame);
    direct_no_image.image_lo = direct_no_image.image_hi = 0;
    check(classify(direct_no_image) == Call::direct, "... nor the game's own call");

    // a wrapper calls the renderer twice for one Settings entry: Kiln's entries go before the first call of a frame
    OncePerFrame once;
    check(once.first(0), "frame 0: the wrapper's first call draws Kiln's entries");
    check(!once.first(0), "frame 0: its second call does not");
    check(once.first(1), "frame 1: the first call again");
    check(!once.first(1), "frame 1: and not the second");
    check(once.first(7) && !once.first(7) && once.first(8), "later frames the same");

    // what draws: the game's own call always, except after a wrapped call of the same frame (a wrapper that tail-calls
    // the renderer for the Settings entry gets there with the game's return address)
    OncePerFrame w;
    check(draws(Call::direct, w, 3) && draws(Call::direct, w, 3), "the game's own call draws, as before, whatever the frame counter does");
    check(!draws(Call::other, w, 3), "another entry draws nothing");
    check(draws(Call::wrapped, w, 4), "a wrapped call draws");
    check(!draws(Call::wrapped, w, 4), "the wrapper's second call of the frame does not");
    check(!draws(Call::direct, w, 4), "nor a direct call after it in that frame");
    check(draws(Call::direct, w, 5), "the next frame's direct call draws again");

    std::printf("menu site: %d checks, %d failed\n", checks, failures);
    return failures ? 1 : 0;
}
