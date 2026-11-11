// SPDX-License-Identifier: MIT
// shroudlink 0.8.0's client addon scans the live exe on its own init thread: link first, then native UI. Every UI
// pattern must match before it enables the first UI hook. kiln-content's ItemRegistryFind hook hides one required
// prologue if it gets there first. Wait at the game entry point, outside the loader lock, before Kiln serves hooks.
// This does not change anyone's scan semantics, patch the addon, or make unsupported game builds work.
#pragma once
#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <functional>
#include <string>
#include <string_view>
#include <vector>

namespace kiln::foreign {

inline constexpr const char* kAddonExport = "shroudlink_shutdown";
inline constexpr const char* kAddonProxyExport = "DirectInput8Create";
inline constexpr const char* kAddonLog = "shroudlink_client.log";
// SIG_UI_DISPATCH in shroudlink 0.8.0 client/src/native.rs. All its scans precede this hook's enable().
inline constexpr const char* kAddonUiPattern = "4C 8B DC 55 49 8D AB E8 FD FF FF 48 81 EC 10 03 00 00";
inline constexpr uint64_t kWaitMs = 10000;  // one budget for the whole boot, not per loaded copy

enum class AddonState { Unknown, Ready, UiOff, Off };
struct AddonLog {
    AddonState state = AddonState::Unknown;
    std::string line;
};

inline bool number(std::string_view s, unsigned base, uint64_t& out) {
    if (s.empty()) return false;
    uint64_t n = 0;
    for (char c : s) {
        const unsigned d = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : 255;
        if (d >= base || n > (UINT64_MAX - d) / base) return false;
        n = n * base + d;
    }
    out = n;
    return true;
}

// Only complete lines in this process's time range, after the matching copy's start marker. A cut tail, partial
// append, stale session at the same module address, overflowing number or another copy's result is not readiness.
inline AddonLog read_addon_log(std::string_view text, uint64_t module, uint64_t not_before) {
    AddonLog out;
    bool ours = false;
    size_t pos = 0;
    while (pos < text.size()) {
        const size_t end = text.find('\n', pos);
        if (end == std::string_view::npos) break;
        std::string_view line = text.substr(pos, end - pos);
        pos = end + 1;
        if (!line.empty() && line.back() == '\r') line.remove_suffix(1);
        if (line.empty() || line.front() != '[') continue;
        const size_t close = line.find("] ");
        uint64_t stamp = 0;
        if (close == std::string_view::npos || !number(line.substr(1, close - 1), 10, stamp) || stamp < not_before) continue;
        const std::string_view msg = line.substr(close + 2);
        constexpr std::string_view kStart = "starting (module 0x";
        if (msg.starts_with("shroudlink client ") && msg.find(" starting (") != std::string_view::npos) {
            const size_t s = msg.find(kStart);
            ours = false;
            if (s != std::string_view::npos) {
                const size_t first = s + kStart.size(), space = msg.find(' ', first);
                uint64_t m = 0;
                ours = space != std::string_view::npos && number(msg.substr(first, space - first), 16, m) && m == module;
                if (ours) out = {};
            }
            continue;
        }
        if (!ours) continue;
        if (msg.starts_with("native UI ready:")) out = {AddonState::Ready, std::string(msg)};
        else if (msg.starts_with("native UI disabled:")) out = {AddonState::UiOff, std::string(msg)};
        else if ((msg.starts_with("game code not recognised (") && msg.ends_with("; addon disabled")) ||
                 msg == "this copy has already started in this process; not starting again" ||
                 msg == "could not inspect the game executable")
            out = {AddonState::Off, std::string(msg)};
    }
    return out;
}

// Recognise only bounded jump chains ending inside this addon, not merely "the UI bytes changed" (a different
// loader's hook or in-place patch is not proof this addon finished scanning). Reader must safely copy or fail.
inline uintptr_t relative(uintptr_t pc, size_t len, int32_t disp) {
    if (pc > UINTPTR_MAX - len) return 0;
    pc += len;
    if (disp >= 0) return pc <= UINTPTR_MAX - static_cast<uint32_t>(disp) ? pc + disp : 0;
    const auto back = static_cast<uint32_t>(-static_cast<int64_t>(disp));
    return pc >= back ? pc - back : 0;
}

template <class Read>
bool hook_reaches(uintptr_t pc, uintptr_t module, size_t size, Read&& read) {
    if (!module || !size || module > UINTPTR_MAX - size) return false;
    for (int hop = 0; hop <= 4 && pc; ++hop) {
        if (pc >= module && pc - module < size) {
            uint8_t first = 0;
            return read(pc, &first, sizeof first);  // adapter also validates executable ownership at the destination
        }
        if (hop == 4) break;
        uint8_t b[14]{};
        if (!read(pc, b, sizeof b)) return false;
        int32_t disp = 0;
        if (b[0] == 0xE9) {
            std::memcpy(&disp, b + 1, 4);
            pc = relative(pc, 5, disp);
        } else if (b[0] == 0xEB) {
            pc = relative(pc, 2, static_cast<int8_t>(b[1]));
        } else if (b[0] == 0xFF && b[1] == 0x25) {
            std::memcpy(&disp, b + 2, 4);
            const uintptr_t slot = relative(pc, 6, disp);
            if (!slot || !read(slot, &pc, sizeof pc)) return false;
        } else if (b[0] == 0x48 && b[1] == 0xB8 && b[10] == 0xFF && b[11] == 0xE0) {
            std::memcpy(&pc, b + 2, sizeof pc);
        } else return false;
    }
    return false;
}

enum class WaitEnd { Ready, Hooked, SwitchedOff, Timeout };
inline const char* wait_end_name(WaitEnd e) {
    switch (e) {
        case WaitEnd::Ready: return "ready";
        case WaitEnd::Hooked: return "hooked";
        case WaitEnd::SwitchedOff: return "switched-off";
        case WaitEnd::Timeout: return "timeout";
    }
    return "?";
}
struct WaitEnv {
    std::function<bool()> ui_hooked;        // verified jump into THIS addon (empty: unknown)
    std::function<std::string()> log_text;  // bounded log tail (empty/missing: unknown)
    std::function<uint64_t()> now_ms;       // monotonic clock
    std::function<void(uint32_t)> sleep_ms;
    uint64_t module = 0;
    uint64_t not_before = 0;  // process creation time, rounded UP to the next Unix second: no previous-run same-second log
};
struct WaitResult {
    WaitEnd end = WaitEnd::Timeout;
    uint64_t waited_ms = 0;
    std::string line;
};
inline WaitResult wait_for_addon(const WaitEnv& env, uint64_t timeout_ms, uint32_t poll_ms = 20) {
    const uint64_t t0 = env.now_ms();
    for (;;) {
        const uint64_t waited = env.now_ms() - t0;
        const AddonLog log = env.log_text ? read_addon_log(env.log_text(), env.module, env.not_before) : AddonLog{};
        if (log.state == AddonState::Ready) return {WaitEnd::Ready, waited, log.line};
        if (log.state == AddonState::UiOff || log.state == AddonState::Off) return {WaitEnd::SwitchedOff, waited, log.line};
        if (env.ui_hooked && env.ui_hooked()) return {WaitEnd::Hooked, waited, {}};
        // No oversleep, including a zero poll interval. Unavailable signals never hold up boot indefinitely.
        const uint64_t elapsed = env.now_ms() - t0;
        if (elapsed >= timeout_ms) return {WaitEnd::Timeout, elapsed, {}};
        env.sleep_ms(static_cast<uint32_t>(std::min<uint64_t>(std::max(1u, poll_ms), timeout_ms - elapsed)));
    }
}

struct Wait {
    std::string module;
    WaitEnd end = WaitEnd::Timeout;
    uint64_t ms = 0;
    std::string line;
};
void wait_for_scanners();
const std::vector<Wait>& waits();  // state/boot.json

}  // namespace kiln::foreign
