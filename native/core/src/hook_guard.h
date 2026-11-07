// SPDX-License-Identifier: MIT
#pragma once
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>

#ifdef _WIN32
#include <windows.h>
#endif

namespace kiln::hooks {

// Wrap runtime disable(), not safetyhook's implementation. The owner serializes its lifecycle calls (npcai's
// tick thread). Capture once on each disabled -> enabled transition; never recapture an already armed hook:
// another engine may have put its own jump on top. Keep the underlying hook/trampoline alive after a refusal.
// Arbitrary concurrent writes by a foreign engine cannot be synchronized through safetyhook's public API.
class HookGuard {
public:
    void armed(const void* target) {
        target_ = target;
        valid_ = copy(bytes_.data(), target_, bytes_.size());
    }

    template <class Hook, class Log>
    bool disable(Hook& hook, Log&& log) {
        if (!hook.enabled()) return true;
        std::array<uint8_t, 14> live{};
        if (!valid_ || !copy(live.data(), target_, live.size()) || live != bytes_) {
            if (!warned_) { warned_ = true; log(); }
            return false;
        }
        return static_cast<bool>(hook.disable());
    }

private:
    // Includes E9 rel32, FF 25 [rip+0] + abs64, and mov rax,imm64/jmp rax. Comparing all 14 bytes is conservative
    // for a short jump: a nearby foreign patch or an unreadable section edge also leaves the hook armed.
    std::array<uint8_t, 14> bytes_{};
    const void* target_ = nullptr;
    bool valid_ = false, warned_ = false;

    static bool copy(void* dst, const void* src, size_t n) {
        if (!src) return false;
#ifdef _WIN32
        SIZE_T got = 0;
        return ReadProcessMemory(GetCurrentProcess(), src, dst, n, &got) && got == n;
#else
        // Host regressions supply ordinary byte arrays, not process addresses.
        std::memcpy(dst, src, n);
        return true;
#endif
    }
};

}  // namespace kiln::hooks
