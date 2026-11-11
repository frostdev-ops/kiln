// SPDX-License-Identifier: MIT
// Host test of foreign_scan.h: when Kiln may hook game code next to shroudlink's client addon. The log lines are the
// addon's own (shroudlink 0.8.0, client/src/lib.rs + native.rs), in the shape the owner's client wrote them under Kiln.
//   native/core/tests/test-foreign-scan.sh
#include <cassert>
#include <cstdio>
#include <string>

#include "../src/foreign_scan.h"

using namespace kiln::foreign;
static unsigned checks = 0;
#define CHECK(...) do { ++checks; assert((__VA_ARGS__)); } while (0)

namespace {

constexpr uint64_t kModule = 0x6ffff9980000ull;
constexpr uint64_t kNow = 1794348589;  // this start of the game

std::string start_line(uint64_t stamp, const char* module) {
    return "[" + std::to_string(stamp) + "] shroudlink client 0.8.0 starting (module " + module +
           " Z:\\games\\library\\native\\dinput8-dfec77\\0.0.0+9fa2802b\\dinput8.dll)\n";
}

std::string line(uint64_t stamp, const char* text) { return "[" + std::to_string(stamp) + "] " + text + "\n"; }

// A fake clock and log for wait_for_addon.
struct Fake {
    uint64_t t = 0;
    uint64_t hook_at = UINT64_MAX;   // the addon's UI hook appears then
    uint64_t ready_at = UINT64_MAX;  // its "native UI ready" line appears then
    const char* ready_text = "native UI ready: game ui +0x283f4a8, builder +0x283f408";
    int polls = 0;
    WaitEnv env() {
        WaitEnv e;
        e.module = kModule;
        e.not_before = kNow - 2;
        e.now_ms = [this] { return t; };
        e.sleep_ms = [this](uint32_t ms) {
            t += ms;
            ++polls;
        };
        e.log_text = [this] {
            std::string s = start_line(kNow, "0x6ffff9980000") + line(kNow, "shroudlink client hooks: tick +0x86f350, send +0x863e70, store +0xe6e6d0, player at +0x466b44");
            if (t >= ready_at) s += line(kNow + 1, ready_text);
            return s;
        };
        e.ui_hooked = [this] { return t >= hook_at; };
        return e;
    }
};

}  // namespace

int main() {
    // ---- the log ----
    const std::string owner = start_line(kNow, "0x6ffff9980000") +
                              line(kNow, "shroudlink client hooks: tick +0x86f350, send +0x863e70, store +0xe6e6d0, player at +0x466b44") +
                              line(kNow + 1, "MODS tab: drawn by the game's tab widget") +
                              line(kNow + 1, "native UI ready: game ui +0x283f4a8, builder +0x283f408");
    auto r = read_addon_log(owner, kModule, kNow - 2);
    CHECK(r.state == AddonState::Ready && r.line.rfind("native UI ready", 0) == 0);

    // the module address is the same on every start: an earlier start's lines do not count
    const std::string earlier = start_line(kNow - 600, "0x6ffff9980000") + line(kNow - 599, "native UI ready: game ui +0x1, builder +0x2");
    r = read_addon_log(earlier + start_line(kNow, "0x6ffff9980000"), kModule, kNow - 2);
    CHECK(r.state == AddonState::Unknown);
    r = read_addon_log(earlier, kModule, kNow - 2);  // this start has not written its first line yet
    CHECK(r.state == AddonState::Unknown);

    // Reproduced scan-order failure: Kiln's hook on the item lookup came first.
    const std::string lost = start_line(kNow, "0x6ffff9980000") +
                             line(kNow, "shroudlink client hooks: tick +0x86f350, send +0x863e70, store +0xe6e6d0, player at +0x466b44") +
                             line(kNow + 1, "native UI disabled: pattern not found: 89 4C 24 08 48 83 EC 28 85 C9 74 ?? 48 8D 4A 38 48 8D 54 24 30 E8 ?? ?? ?? ?? 48 85 C0 74 ??");
    r = read_addon_log(lost, kModule, kNow - 2);
    CHECK(r.state == AddonState::UiOff && r.line.find("pattern not found: 89 4C 24 08") != std::string::npos);

    // the addon stopped altogether
    r = read_addon_log(start_line(kNow, "0x6ffff9980000") + line(kNow + 120, "game code not recognised (pattern not found: 48 89); addon disabled"),
                       kModule, kNow - 2);
    CHECK(r.state == AddonState::Off);
    r = read_addon_log(start_line(kNow, "0x6ffff9980000") + line(kNow, "this copy has already started in this process; not starting again"),
                       kModule, kNow - 2);
    CHECK(r.state == AddonState::Off);

    // another copy's lines (a hot-reloaded addon at another address) belong to that copy
    r = read_addon_log(owner + start_line(kNow + 5, "0x7ff812340000") + line(kNow + 5, "native UI disabled: pattern not found: 40 53"),
                       kModule, kNow - 2);
    CHECK(r.state == AddonState::Ready);
    r = read_addon_log(owner + start_line(kNow + 5, "0x7ff812340000") + line(kNow + 5, "native UI disabled: pattern not found: 40 53"),
                       0x7ff812340000ull, kNow - 2);
    CHECK(r.state == AddonState::UiOff);

    // CRLF, a cut first line (the log's tail), junk
    r = read_addon_log("16] cut\r\n" + start_line(kNow, "0x6FFFF9980000") + "[abc] x\r\n[" + std::to_string(kNow + 1) + "] native UI ready: x\r\n",
                       kModule, kNow - 2);
    CHECK(r.state == AddonState::Ready && r.line == "native UI ready: x");
    CHECK(read_addon_log("", kModule, 0).state == AddonState::Unknown);

    // Strict parsing: incomplete appends and untrusted numeric fields must not manufacture readiness.
    const auto start = start_line(kNow, "0x6ffff9980000");
    const auto ready = line(kNow, "native UI ready: x");
    CHECK(read_addon_log(start + ready.substr(0, ready.size() - 1), kModule, kNow).state == AddonState::Unknown);
    CHECK(read_addon_log(start + ready, kModule, kNow).state == AddonState::Ready);
    CHECK(read_addon_log(ready, kModule, kNow).state == AddonState::Unknown);  // no start marker
    CHECK(read_addon_log(start + "[18446744073709551616000000001] native UI ready: x\n", kModule, kNow).state == AddonState::Unknown);
    CHECK(read_addon_log(start_line(kNow, "0x1000000000000000000006ffff9980000") + ready, kModule, kNow).state == AddonState::Unknown);
    CHECK(read_addon_log(start_line(kNow, "0x6ffff9980000junk") + ready, kModule, kNow).state == AddonState::Unknown);
    CHECK(read_addon_log(owner + start, kModule, kNow).state == AddonState::Unknown);  // last start wins
    CHECK(read_addon_log(start + line(kNow, "native UI ready-ish"), kModule, kNow).state == AddonState::Unknown);
    CHECK(read_addon_log(start + ready, kModule, kNow + 1).state == AddonState::Unknown);  // ambiguous first second excluded
    CHECK(read_addon_log(start + line(kNow, "could not inspect the game executable"), kModule, kNow).state == AddonState::Off);

    // Safe, bounded hook attribution: only jumps reaching the addon, never arbitrary changed bytes.
    {
        uint8_t bytes[256]{};
        constexpr uintptr_t base = 0x1000, addon = base + 200;
        unsigned reads = 0;
        auto read = [&](uintptr_t p, void* out, size_t n) {
            ++reads;
            if (p < base || p - base > sizeof bytes || n > sizeof bytes - (p - base)) return false;
            std::memcpy(out, bytes + (p - base), n);
            return true;
        };
        auto rel = [&](size_t at, uintptr_t to) {
            bytes[at] = 0xE9;
            const int32_t d = static_cast<int32_t>(to - (base + at + 5));
            std::memcpy(bytes + at + 1, &d, 4);
        };
        CHECK(!hook_reaches(base, addon, 10, read));  // not a jump
        rel(0, addon);
        CHECK(hook_reaches(base, addon, 10, read));
        rel(0, addon - 1);
        CHECK(!hook_reaches(base, addon, 10, read));  // outside module, even adjacent
        rel(0, addon + 10);
        CHECK(!hook_reaches(base, addon, 10, read));
        rel(0, base);  // self loop: fixed hop bound, no recursion
        reads = 0;
        CHECK(!hook_reaches(base, addon, 10, read) && reads == 4);
        rel(0, base + 32);
        bytes[32] = 0xFF; bytes[33] = 0x25;
        const int32_t slot_disp = 10;  // indirect pointer at +48
        std::memcpy(bytes + 34, &slot_disp, 4);
        const uintptr_t dest = addon;
        std::memcpy(bytes + 48, &dest, sizeof dest);
        CHECK(hook_reaches(base, addon, 10, read));  // E9 -> FF25 relay -> addon
        bytes[32] = 0x48; bytes[33] = 0xB8;
        std::memcpy(bytes + 34, &dest, sizeof dest);
        bytes[42] = 0xFF; bytes[43] = 0xE0;
        CHECK(hook_reaches(base, addon, 10, read));  // absolute relay
        CHECK(!hook_reaches(1, addon, 10, read));  // unreadable memory
        CHECK(!hook_reaches(base + 255, addon, 10, read));  // page/buffer edge
        CHECK(!hook_reaches(base, 0, 10, read));
        CHECK(!hook_reaches(base, UINTPTR_MAX - 1, 10, read));
        CHECK(!hook_reaches(base, addon, 0, read));
        CHECK(relative(UINTPTR_MAX, 5, 1) == 0);
        CHECK(relative(UINTPTR_MAX - 10, 5, 10) == 0);
        CHECK(relative(1, 1, INT32_MIN) == 0);
        CHECK(relative(base, 5, -5) == base);
        auto inaccessible = [](uintptr_t, void*, size_t) { return false; };
        CHECK(!hook_reaches(addon, addon, 10, inaccessible));  // final target also needs readable executable ownership
    }

    // ---- the wait ----
    {  // the UI hook goes in after 0.9 s (the owner's client): Kiln waits that long
        Fake f;
        f.hook_at = 900;
        const auto w = wait_for_addon(f.env(), kWaitMs);
        CHECK(w.end == WaitEnd::Hooked && w.waited_ms >= 900 && w.waited_ms < 940);
    }
    {  // its log says ready first
        Fake f;
        f.ready_at = 2400;
        const auto w = wait_for_addon(f.env(), kWaitMs);
        CHECK(w.end == WaitEnd::Ready && w.waited_ms >= 2400 && w.line.rfind("native UI ready", 0) == 0);
    }
    {  // the addon switched its UI off for its own reasons: no waiting for the hook that will not come
        Fake f;
        f.ready_at = 300;
        f.ready_text = "native UI disabled: pattern not found: 40 53 48 83 EC 20";
        const auto w = wait_for_addon(f.env(), kWaitMs);
        CHECK(w.end == WaitEnd::SwitchedOff && w.waited_ms < 340);
    }
    {  // nothing ever: Kiln goes on after the limit
        Fake f;
        const auto w = wait_for_addon(f.env(), kWaitMs);
        CHECK(w.end == WaitEnd::Timeout && w.waited_ms >= kWaitMs && w.waited_ms < kWaitMs + 40);
    }
    {  // already done before Kiln looked: no wait at all
        Fake f;
        f.hook_at = 0;
        const auto w = wait_for_addon(f.env(), kWaitMs);
        CHECK(w.end == WaitEnd::Hooked && w.waited_ms == 0 && f.polls == 0);
    }
    {  // the UI function is not on this build: the log alone decides
        Fake f;
        f.ready_at = 1200;
        auto e = f.env();
        e.ui_hooked = nullptr;
        const auto w = wait_for_addon(e, kWaitMs);
        CHECK(w.end == WaitEnd::Ready && w.waited_ms >= 1200);
    }
    {  // no usable log at all: live hook still decides (e.g. startup's ambiguous first second)
        Fake f;
        f.hook_at = 120;
        auto e = f.env();
        e.log_text = [] { return std::string{}; };
        CHECK(wait_for_addon(e, 1000).end == WaitEnd::Hooked);
    }
    {  // stale/partial success is never readiness, exact deadline even when poll does not divide it
        Fake f;
        auto e = f.env();
        e.log_text = [&] { return earlier + start + ready.substr(0, ready.size() - 1); };
        const auto w = wait_for_addon(e, 53, 20);
        CHECK(w.end == WaitEnd::Timeout && w.waited_ms == 53 && f.t == 53);
    }
    {
        Fake f;
        CHECK(wait_for_addon(f.env(), 3, 0).waited_ms == 3);  // zero poll cannot spin forever
        CHECK(wait_for_addon(f.env(), 0).waited_ms == 0);  // already spent boot budget: no sleeps
    }
    {  // terminal failure wins over a hook signal, so diagnosis still names a UI disabled after partial install
        Fake f;
        f.ready_at = f.hook_at = 0;
        f.ready_text = "native UI disabled: unsupported prologue";
        CHECK(wait_for_addon(f.env(), 100).end == WaitEnd::SwitchedOff);
    }
    std::printf("foreign_scan_test: ok (%u checks)\n", checks);
    return 0;
}
