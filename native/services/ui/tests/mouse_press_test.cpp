// SPDX-License-Identifier: MIT
// Host test of mouse_press.h: the rules that make the middle and side buttons and the wheel key presses, without a
// window. Twins pairs a raw record with its message (one press, in either order: the same input and direction at the
// closest message time within kTolMs, wheel deltas reconciled, so a message Wine merged from two turns covers both
// records); Notches adds a high-resolution wheel's deltas up to one press per WHEEL_DELTA. The
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

// input.cpp's mouse_wheel without the window: twins cover what they can, the rest is new input; counts the presses.
struct Window {
    Twins twins;
    Notches notches;
    int presses = 0;
    int32_t last_apart = 0;
    bool take = false;  // a binding takes this direction now
    bool wheel(Source s, int32_t delta, uint32_t time) {
        const uint32_t kind = wheel_kind(0, delta);
        const auto f = twins.follow(s, kind, delta, time);
        if (f.matched) {
            delta = f.rest;
            last_apart = f.apart_ms;
        }
        if (!delta) return f.kept;
        presses += std::abs(notches.add(0, delta));
        twins.lead(s, kind, delta, time, take);
        return take || f.kept;
    }
};

void twins() {
    Twins t;
    const uint32_t up = wheel_kind(0, 120), down = wheel_kind(0, -120), x1 = button_kind(kVkX1, true);
    check(!t.follow(Source::Raw, up, 120, 1000).matched, "a first record leads");
    t.lead(Source::Raw, up, 120, 1000, true);
    check(!t.follow(Source::Raw, up, 120, 1000).matched, "a second record of the same source leads too");
    t.lead(Source::Raw, up, 120, 1000, false);
    auto a = t.follow(Source::Message, up, 120, 1000), b = t.follow(Source::Message, up, 120, 1000);
    check(a.matched && a.kept && a.rest == 0 && b.matched && !b.kept, "the messages follow their records, oldest first");
    check(!t.follow(Source::Message, up, 120, 1000).matched, "...and a third message has no twin: it leads");
    t.clear();
    t.lead(Source::Raw, up, 120, 3000, false);
    check(!t.follow(Source::Message, down, -120, 3000).matched, "another direction is not a twin");
    check(!t.follow(Source::Message, x1, 0, 3000).matched, "a button is not a wheel's twin");
    t.clear();

    // buttons: one lead covers one follower, within kTolMs
    t.lead(Source::Raw, x1, 0, 1000, true);
    auto c = t.follow(Source::Message, x1, 0, 1000 + kTolMs);
    check(c.matched && c.kept && c.apart_ms == static_cast<int32_t>(kTolMs) && t.waiting() == 0,
          "a button's message kTolMs after its record is its twin");
    t.lead(Source::Raw, x1, 0, 1000, true);
    check(!t.follow(Source::Message, x1, 0, 1000 + kTolMs + 1).matched, "...one ms more is another input");
    t.clear();

    t.lead(Source::Raw, up, 120, 0xFFFFFFF0u, true);  // message time wraps
    check(t.follow(Source::Message, up, 120, 0x10u).matched, "twins across the wrap of message time pair (32 ms apart)");
    t.lead(Source::Raw, up, 120, 0xFFFFFFF0u, true);
    check(!t.follow(Source::Message, up, 120, 0x10u + kTwinMs).matched && t.waiting() == 0,
          "a lead kTwinMs older than a later input is dropped");
    for (size_t i = 0; i < kMaxWaiting + 200; ++i) t.lead(Source::Raw, up, 120, 5000, false);  // a window without messages
    check(t.waiting() == kMaxWaiting, "leads nothing follows are bounded");
    t.clear();
    // a hitch on a high-resolution wheel: 80 records of +15 (ten notches) wait, then one merged message of +1200
    // covers all of them and brings no new input (with 64 places the first sixteen were gone and counted again)
    for (int i = 0; i < 80; ++i) t.lead(Source::Raw, up, 15, 9000 + static_cast<uint32_t>(i) * 4, true);
    {
        const auto r = t.follow(Source::Message, up, 1200, 9000 + 79 * 4);
        check(r.matched && r.rest == 0 && t.waiting() == 0, "a merged message covers every record of a long turn");
    }
}

void reconcile() {
    {  // Wine merges two queued turns' messages: two records, then one +240 message (with the later turn's time)
        Window w;
        w.take = true;
        w.wheel(Source::Raw, 120, 1000);
        w.wheel(Source::Raw, 120, 1030);
        w.take = false;
        const bool kept = w.wheel(Source::Message, 240, 1030);
        check(w.presses == 2 && kept && w.twins.waiting() == 0,
              "two +120 records and one merged +240 message: two presses, the message keeps the records' decision");
    }
    {  // the same merge seen message first
        Window w;
        w.wheel(Source::Message, 240, 1030);
        w.wheel(Source::Raw, 120, 1000);
        w.wheel(Source::Raw, 120, 1030);
        check(w.presses == 2 && w.twins.waiting() == 0, "a merged +240 message before its two records: two presses");
    }
    {  // mirror: one raw record of two notches, its two messages of one notch each, either order
        Window w;
        w.wheel(Source::Raw, -240, 1000);
        w.wheel(Source::Message, -120, 1000);
        w.wheel(Source::Message, -120, 1000);
        check(w.presses == 2 && w.twins.waiting() == 0, "one -240 record, two -120 messages: two presses");
        Window v;
        v.wheel(Source::Message, -120, 1000);
        v.wheel(Source::Message, -120, 1000);
        v.wheel(Source::Raw, -240, 1000);
        check(v.presses == 2 && v.twins.waiting() == 0, "two -120 messages, then one -240 record: two presses");
    }
    {  // a pair one tick apart (Windows' clock): one press, said through apart_ms
        Window w;
        w.wheel(Source::Raw, 120, 1000);
        w.wheel(Source::Message, 120, 1016);
        check(w.presses == 1 && w.last_apart == 16, "a record and its message 16 ms apart: one press (16 ms reported)");
    }
    {  // two real notches 30 ms apart from a source that sends both forms: two presses, in either delivery order
        Window w;
        w.take = true;
        w.wheel(Source::Raw, 120, 1000);
        const bool first = w.wheel(Source::Message, 120, 1000);
        w.take = false;
        w.wheel(Source::Raw, 120, 1030);
        const bool second = w.wheel(Source::Message, 120, 1030);
        check(w.presses == 2 && first && !second, "two notches 30 ms apart: two presses, each message with its own record");
        Window v;
        v.take = true;
        v.wheel(Source::Raw, 120, 1000);
        v.take = false;
        v.wheel(Source::Raw, 120, 1030);
        const bool a = v.wheel(Source::Message, 120, 1000), b = v.wheel(Source::Message, 120, 1030);
        check(v.presses == 2 && a && !b, "...drained ahead (two records, then two messages): two presses, the closest pairs");
    }
    {  // the review's case: a taken raw-only notch, then an unbound message-only notch far apart: two inputs
        Window w;
        w.take = true;
        w.wheel(Source::Raw, 120, 1000);
        w.take = false;
        const bool kept = w.wheel(Source::Message, 120, 1100);
        check(w.presses == 2 && !kept, "a lone record and a lone message 100 ms apart: two presses, the second not kept");
    }
    {  // a merge over a hitch: the turns 300 ms apart, both records before the merged message
        Window w;
        w.wheel(Source::Raw, 120, 1000);
        w.wheel(Source::Raw, 120, 1300);
        w.wheel(Source::Message, 240, 1300);
        check(w.presses == 2, "a merged message covers records up to kTwinMs older than the closest one");
    }
    {  // a message that covers more than waits: the rest is new input
        Window w;
        w.wheel(Source::Raw, 120, 1000);
        w.wheel(Source::Message, 240, 1000);
        check(w.presses == 2 && w.twins.waiting() == 1, "a +240 message with one +120 record waiting: one press more, its "
              "new half waits for its own record");
        w.wheel(Source::Raw, 120, 1000);
        check(w.presses == 2 && w.twins.waiting() == 0, "...which then follows it");
    }
    {  // high-resolution parts pair the same way
        Window w;
        for (int i = 0; i < 4; ++i) {
            w.wheel(Source::Raw, 30, 1000 + 10 * i);
            w.wheel(Source::Message, 30, 1000 + 10 * i);
        }
        check(w.presses == 1 && w.twins.waiting() == 0, "four quarter-notch pairs 10 ms apart: one press");
    }
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
    reconcile();
    notches();
    codes();
    std::printf("%s: mouse_press %d checks, %d failed\n", g_fails ? "FAIL" : "PASS", g_checks, g_fails);
    return g_fails ? 1 : 0;
}
