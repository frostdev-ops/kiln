// SPDX-License-Identifier: MIT
// Host test of mouse_press.h: the rules that make the middle and side buttons and the wheel key presses, without a
// window. Twins pairs a raw record with its message (one press, in either order: the same input, delta and message time;
// a lead waits kTwinMs at most); Notches adds a high-resolution wheel's deltas up to one press per WHEEL_DELTA. The
// window procedure that applies them runs under Wine in window_input/harness.cpp.
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
    check(!t.follow(Source::Raw, up, 120, 1000), "a first record leads");
    t.lead(Source::Raw, up, 120, 1000, true);
    check(!t.follow(Source::Raw, up, 120, 1000), "a second record of the same source leads too (two notches drained ahead)");
    t.lead(Source::Raw, up, 120, 1000, false);
    auto a = t.follow(Source::Message, up, 120, 1000), b = t.follow(Source::Message, up, 120, 1000);
    check(a && *a && b && !*b, "the messages follow their records, oldest first, with their decisions");
    check(!t.follow(Source::Message, up, 120, 1000), "...and a third message has no twin: it leads");
    t.clear();

    t.lead(Source::Message, down, -120, 2000, true);
    auto c = t.follow(Source::Raw, down, -120, 2000);
    check(c && *c && t.waiting() == 0, "a message first: its record follows");
    t.lead(Source::Raw, up, 120, 3000, false);
    check(!t.follow(Source::Message, down, -120, 3000), "another direction is not a twin");
    check(!t.follow(Source::Message, x1, 0, 3000), "a button is not a wheel's twin");
    check(!t.follow(Source::Message, up, 30, 3000), "another delta is not a twin");
    check(t.follow(Source::Message, up, 120, 3000).has_value(), "the same input, delta and message time is the twin");
    t.clear();

    // review finding 2: a taken raw-only WheelUp at 1000, then an unbound message-only WheelUp at 1100
    t.lead(Source::Raw, up, 120, 1000, true);
    int32_t apart_ms = 0;
    check(!t.follow(Source::Message, up, 120, 1100, &apart_ms) && apart_ms == 100,
          "a message 100 ms of message time after a lone record is another input: it leads (and the log can say 100 ms)");
    check(t.waiting() == 1, "...the lone record still waits for its own twin");
    check(t.follow(Source::Message, up, 120, 1000).has_value(), "...which still finds it");
    t.lead(Source::Raw, up, 120, 4000, true);
    check(!t.follow(Source::Message, up, 120, 4000 + kTwinMs + 1) && t.waiting() == 0,
          "a lead kTwinMs of message time older than a later input is dropped");
    t.clear();

    t.lead(Source::Raw, up, 120, 0xFFFFFFF0u, true);  // message time wraps
    check(t.follow(Source::Message, up, 120, 0xFFFFFFF0u).has_value(), "a twin at the edge of the wrap pairs");
    t.lead(Source::Raw, up, 120, 0xFFFFFFF0u, true);
    check(!t.follow(Source::Message, up, 120, 0x10u + kTwinMs) && t.waiting() == 0, "a lead older across the wrap is dropped");
    for (int i = 0; i < 100; ++i) t.lead(Source::Raw, up, 120, 5000, false);  // a window without messages
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

void codes() {
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
    codes();
    std::printf("%s: mouse_press %d checks, %d failed\n", g_fails ? "FAIL" : "PASS", g_checks, g_fails);
    return g_fails ? 1 : 0;
}
