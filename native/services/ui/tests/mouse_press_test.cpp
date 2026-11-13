// SPDX-License-Identifier: MIT
// Host test of mouse_press.h: the rules that make the middle and side buttons and the wheel key presses, without a
// window. Twins pairs a raw record with its message (one press, in either order, within kTwinMs of message time);
// Notches adds a high-resolution wheel's deltas up to one press per WHEEL_DELTA; KeptChords remembers which chord's
// notches a key binding takes. The window procedure that applies them runs under Wine in window_input/harness.cpp.
#include "../mouse_press.h"

#include <cstdio>

using namespace kui::mouse;

namespace {
int g_fails = 0, g_checks = 0;
void check(bool ok, const char* what) {
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", what);
    ++g_checks;
    if (!ok) ++g_fails;
}

void twins() {
    Twins t;
    const uint32_t up = wheel_kind(0, 120), down = wheel_kind(0, -120), x1 = button_kind(kVkX1, true);
    check(!t.follow(Source::Raw, up, 1000), "a first record leads");
    t.lead(Source::Raw, up, 1000, true);
    check(!t.follow(Source::Raw, up, 1000), "a second record of the same source leads too (two notches drained ahead)");
    t.lead(Source::Raw, up, 1000, false);
    auto a = t.follow(Source::Message, up, 1000), b = t.follow(Source::Message, up, 1000);
    check(a && *a && b && !*b, "the messages follow their records, oldest first, with their decisions");
    check(!t.follow(Source::Message, up, 1000), "...and a third message has no twin: it leads");
    t.clear();

    t.lead(Source::Message, down, 2000, true);
    auto c = t.follow(Source::Raw, down, 2000);
    check(c && *c && t.waiting() == 0, "a message first: its record follows");
    t.lead(Source::Raw, up, 3000, false);
    check(!t.follow(Source::Message, down, 3000), "another direction is not a twin");
    check(!t.follow(Source::Message, x1, 3000), "a button is not a wheel's twin");
    check(t.follow(Source::Message, up, 3000 + kTwinMs).has_value(), "a twin within kTwinMs of message time");
    t.lead(Source::Raw, up, 4000, true);
    check(!t.follow(Source::Message, up, 4000 + kTwinMs + 1) && t.waiting() == 0,
          "a lead older than kTwinMs is forgotten (no message came): a later message leads");
    t.clear();

    t.lead(Source::Raw, up, 0xFFFFFFF0u, true);  // message time wraps
    check(t.follow(Source::Message, up, 0x10u).has_value(), "message times that wrap still pair");
    for (int i = 0; i < 100; ++i) t.lead(Source::Raw, up, 5000, false);  // a window without messages
    check(t.waiting() == kMaxWaiting, "leads nothing follows are bounded");
}

void notches() {
    Notches n;
    check(n.add(0, 120) == 1 && n.add(0, -120) == -1, "a notch each way");
    check(n.add(0, 30) == 0 && n.add(0, 30) == 0 && n.add(0, 30) == 0 && n.add(0, 30) == 1, "four quarters make one notch");
    check(n.add(0, 90) == 0 && n.add(0, -90) == 0 && n.add(0, -30) == -1, "a turn the other way starts a new notch");
    check(n.add(0, 360) == 3 && n.add(0, -250) == -2, "several notches in one delta");
    check(n.add(1, 60) == 0 && n.add(0, 60) == 0 && n.add(1, 60) == 1, "the two wheels add up apart");
    n.clear();
    check(n.add(0, 100) == 0 && (n.clear(), n.add(0, 30)) == 0, "clear drops a part of a notch");
    check(n.add(0, 0) == 0, "a zero delta is nothing");
}

void kept() {
    KeptChords k;
    check(!k.get(kVkWheelUp, 1), "nothing kept at first");
    k.set(kVkWheelUp, 1, true);
    check(k.get(kVkWheelUp, 1) && !k.get(kVkWheelUp, 0) && !k.get(kVkWheelDown, 1), "per direction and modifiers");
    k.set(kVkWheelRight, 7, true);
    check(k.get(kVkWheelRight, 7) && !k.get(kVkWheelLeft, 7), "the horizontal wheel too");
    k.clear();
    check(!k.get(kVkWheelUp, 1) && !k.get(kVkWheelRight, 7), "clear");
    check(wheel_vk(0, 1) == kVkWheelUp && wheel_vk(0, -1) == kVkWheelDown && wheel_vk(1, 1) == kVkWheelRight &&
              wheel_vk(1, -1) == kVkWheelLeft,
          "the codes of the four directions");
    check(is_wheel_vk(0x100) && is_wheel_vk(0x103) && !is_wheel_vk(0xFF) && !is_wheel_vk(0x104) && is_button_vk(4) &&
              is_button_vk(6) && !is_button_vk(1) && !is_button_vk(2),
          "wheel codes are above 0xFF; the left and right buttons are not press buttons");
}
}  // namespace

int main() {
    twins();
    notches();
    kept();
    std::printf("%s: mouse_press %d checks, %d failed\n", g_fails ? "FAIL" : "PASS", g_checks, g_fails);
    return g_fails ? 1 : 0;
}
