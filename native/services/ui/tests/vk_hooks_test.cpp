// SPDX-License-Identifier: MIT
// g++ -std=c++20 -Wall -Wextra -Werror -fsanitize=address,undefined vk_hooks_test.cpp -o /tmp/vk_hooks_test
#include "../vk_hooks.h"
#include <cassert>
#include <cstdio>
#include <limits>

using namespace kui::vk_hooks;

int main() {
    constexpr uintptr_t address = 0x10000, trampoline = 0x20000, detour = 0x30000;
    const Mapping old_mapping{address, address - 0x1000, 3, 2};
    assert(!replaced({0, 0, 0, 0}, old_mapping));
    assert(!replaced(old_mapping, old_mapping));
    // Unload notifications arrive before unmapping and cannot authorise a re-hook of covered live code.
    assert(!replaced(old_mapping, {address, old_mapping.owner, 3, 3}));
    assert(replaced(old_mapping, {address, old_mapping.owner, 4, 3}));
    assert(!replaced(old_mapping, {address + 8, old_mapping.owner, 3, 3}));
    assert(!replaced(old_mapping, {address + 0x1000, old_mapping.owner + 0x1000, 3, 2}));
    assert(replaced(old_mapping, {address + 0x1000, old_mapping.owner + 0x1000, 3, 3}));
    Snapshot s;
    Bytes fresh{};
    fresh.fill(0x90);
    assert(classify(s, address, &fresh, false) == Canary::First);
    assert(!may_hook(classify(s, 0, &fresh, false)));
    assert(!may_hook(classify(s, address, nullptr, false)));

    s.address = address;
    s.size = 7;
    s.original = fresh;
    s.patch = fresh;
    s.patch[0] = 0xe9;
    const int32_t displacement = static_cast<int32_t>(trampoline - (address + 5));
    std::memcpy(s.patch.data() + 1, &displacement, sizeof displacement);
    s.have_patch = true;

    // Live hooks win over replacement evidence. The installer consumes that sampled epoch so a subsequent unknown
    // patch is Foreign again. Notices for other vulkan-1.dll mappings never advance this export's epoch.
    assert(classify(s, address, &s.patch, false) == Canary::Ours);
    assert(classify(s, address, &s.patch, true) == Canary::Ours);
    assert(!may_hook(Canary::Ours));
    assert(own_jump(s.patch, address, trampoline, 32, detour));

    assert(classify(s, address, &fresh, false) == Canary::Pristine);
    assert(classify(s, address, &fresh, true) == Canary::Pristine);
    assert(may_hook(Canary::Pristine));  // desktop probe reload at the same address
    Bytes changed_image = fresh;
    changed_image[0] = 0x55;
    assert(classify(s, address, &changed_image, false) == Canary::Foreign);
    assert(classify(s, address, &changed_image, true) == Canary::Reloaded);
    assert(may_hook(Canary::Reloaded));  // changed image, or an overlay arriving before us on the new mapping
    assert(classify(s, address + 0x1000, &fresh, false) == Canary::Foreign);
    assert(classify(s, address + 0x1000, &fresh, true) == Canary::Moved);
    assert(may_hook(Canary::Moved));
    assert(classify(s, address + 0x1000, nullptr, true) == Canary::Unreadable);
    assert(!may_hook(Canary::Foreign));
    assert(!may_hook(Canary::Unreadable));

    // A foreign patch on a live mapping may chain through Kiln; leave it alone even though its bytes differ.
    Bytes overlay = s.patch;
    overlay[1] ^= 0x40;
    assert(classify(s, address, &overlay, false) == Canary::Foreign);
    assert(!own_jump(overlay, address, trampoline, 32, detour));

    // Only the actual overwritten span matters: data after it can change without making a hook fresh.
    Bytes changed_tail = s.patch;
    changed_tail[s.size] ^= 0xff;
    assert(classify(s, address, &changed_tail, false) == Canary::Ours);
    changed_tail[s.size - 1] ^= 0xff;
    assert(classify(s, address, &changed_tail, false) == Canary::Foreign);
    assert(own_jump(changed_tail, address, trampoline, 32, detour));  // still our live jump: also never re-hook

    // Far FF25 hooks and Windows loader thunks share an opcode, but only OUR destination is a Kiln patch.
    Bytes far{};
    far[0] = 0xff;
    far[1] = 0x25;
    std::memcpy(far.data() + 6, &detour, sizeof detour);
    assert(own_jump(far, address, trampoline, 32, detour));
    const uintptr_t loader = 0x40000;
    std::memcpy(far.data() + 6, &loader, sizeof loader);
    assert(!own_jump(far, address, trampoline, 32, detour));
    Snapshot windows = s;
    windows.original = far;
    assert(classify(windows, address, &far, false) == Canary::Pristine);
    far[2] = 8;  // indirect jump through a different slot is not our inline FF25 patch
    assert(!own_jump(far, address, trampoline, 32, detour));

    // Backward relative jumps use signed rel32 arithmetic.
    Bytes backward{};
    backward[0] = 0xe9;
    const int32_t back = -0x1005;
    std::memcpy(backward.data() + 1, &back, sizeof back);
    assert(own_jump(backward, address, address - 0x1000, 32, detour));

    // One restored export must not re-hook its live peers. The old single-canary decision did just that.
    const Canary mixed[] = {classify(s, address, &fresh, false), classify(s, address, &s.patch, false),
                            classify(s, address, &overlay, false)};
    assert(may_hook(mixed[0]) && !may_hook(mixed[1]) && !may_hook(mixed[2]));
    s.have_patch = false;  // failed/raced post-enable fingerprint is never taken as pristine code
    assert(classify(s, address, &s.patch, false) == Canary::Foreign);

    // Code-owner matching includes forwarded winevulkan addresses and ignores unrelated same-name DLLs.
    assert(contains(address, 0x1000, address));
    assert(contains(address, 0x1000, address + 0xfff));
    assert(!contains(address, 0x1000, address + 0x1000));
    assert(!contains(address + 0x1000, 0x1000, address));
    assert(!contains(address, 0x1000, 0));
    const auto top = std::numeric_limits<uintptr_t>::max();
    assert(contains(top - 8, 8, top - 1));
    assert(!contains(top - 8, 8, top));
    assert(!contains(top - 8, 8, 1));

    std::puts("Vulkan hook classification checks passed");
}
