// SPDX-License-Identifier: MIT
// kiln.ui: the middle and side mouse buttons and the wheel as key presses (key bindings, the Kiln menu key, the Keys
// page's "press a key"), the rules without a window. input.cpp applies them in the window procedure, for the key
// subscribers that ask for mouse presses (subscribe_key_ex with KILN_UI_KEYS_MOUSE, keys.cpp; subscribe_key gets keyboard
// keys only); tests/mouse_press_test.cpp checks them on the host.
//  - Codes: the buttons keep their Windows virtual keys (VK_MBUTTON 0x04, VK_XBUTTON1 0x05, VK_XBUTTON2 0x06). The
//    wheel has none: KILN_UI_VK_WHEEL_UP/DOWN/LEFT/RIGHT (kiln_ui.h) are 0x100..0x103, above every virtual key, so no key
//    can be taken for a notch. The left and right buttons are never presses here: Kiln's own screens need them.
//  - One press per physical input. The game registers raw mouse input without RIDEV_NOLEGACY, so the window gets each
//    button transition and wheel turn twice: a WM_INPUT record and a legacy message (WM_XBUTTONDOWN, WM_MOUSEWHEEL, ...),
//    usually the record first, but a game that drains WM_INPUT ahead of its other messages can get two records before
//    their two messages, and nothing forbids the other order. Twins pairs them: the first of a pair to arrive leads (it is
//    dispatched and decides whether the game gets it), the other follows that decision and is never dispatched. A twin
//    is the same input from the other source (the same button and transition, or the same wheel axis and direction) at
//    the closest waiting message time within kTolMs. Wine stamps a record and its message with the one hardware event's
//    time (server/queue.c, queue_mouse_message); Windows' message times count in system ticks (10 to 16 ms), so a pair
//    split across a tick boundary is a tick apart: kTolMs covers three ticks. A pair matched only through kTolMs is
//    reported (follow's apart_ms) for kiln.log.
//  - Wheel deltas are reconciled, not compared: Wine merges a queued WM_MOUSEWHEEL with the next one at the same cursor
//    position, across the raw records queued between them (server/queue.c merge_mousewheel, the later turn's time), so
//    two +120 records can come with one +240 message; and one raw record may carry two notches its messages split. A
//    follower takes what waiting leads of the other source cover, the one closest in time first (it must be within
//    kTolMs), then the next closest within kTwinMs; what no lead covers is new input. A lead nothing follows (a window
//    without raw mouse input, or without legacy messages) is dropped once a later input is kTwinMs of message time past.
//  - The wheel: one press per WHEEL_DELTA (120) turned, the direction from the delta's sign. A high-resolution wheel sends
//    smaller deltas: they add up (Notches), and a turn the other way starts a new notch. A delta that completes no notch
//    is not a press: input.cpp asks the subscribers about it as a repeat of the notch's press (KilnUiKey.repeat 1), so a
//    binding that takes Ctrl+WheelUp keeps every part of the notch from the game's camera, answered at that moment (no
//    decision is carried from an earlier notch).
#pragma once
#include <algorithm>
#include <cstdint>
#include <cstdlib>
#include <deque>

namespace kui::mouse {

constexpr uint32_t kVkMiddle = 0x04, kVkX1 = 0x05, kVkX2 = 0x06;
constexpr uint32_t kVkWheelUp = 0x100, kVkWheelDown = 0x101, kVkWheelLeft = 0x102, kVkWheelRight = 0x103;
constexpr int32_t kNotch = 120;              // WHEEL_DELTA
constexpr int kMaxNotchesPerTurn = 16;       // one message never makes more presses than this
constexpr uint32_t kTolMs = 50;              // a twin's message time may differ this much (three ticks of Windows' clock)
constexpr uint32_t kTwinMs = 1000;           // how long (message time) a lead waits; a merged message covers leads this old
constexpr size_t kMaxWaiting = 64;           // leads waiting for their twin

enum class Source : uint8_t { Raw, Message };

inline bool is_wheel_vk(uint32_t vk) { return vk >= kVkWheelUp && vk <= kVkWheelRight; }
inline bool is_button_vk(uint32_t vk) { return vk == kVkMiddle || vk == kVkX1 || vk == kVkX2; }

// axis 0 vertical (WM_MOUSEWHEEL, RI_MOUSE_WHEEL: positive = away from the player = up), 1 horizontal
// (WM_MOUSEHWHEEL, RI_MOUSE_HWHEEL: positive = right).
inline uint32_t wheel_vk(int axis, int32_t delta) {
    return axis == 0 ? (delta > 0 ? kVkWheelUp : kVkWheelDown) : (delta > 0 ? kVkWheelRight : kVkWheelLeft);
}

// What pairs a record with its message (with the delta and the message time): a button and its transition, or a wheel
// axis and direction.
inline uint32_t button_kind(uint32_t vk, bool down) { return vk | (down ? 0x10000u : 0u); }
inline uint32_t wheel_kind(int axis, int32_t delta) { return wheel_vk(axis, delta) | 0x20000u; }

class Twins {
public:
    struct Follow {
        bool matched = false;  // waiting leads covered (some of) this input: their decisions stand for it
        bool kept = false;     // one of them was kept from the game
        int32_t rest = 0;      // the part of a wheel delta no lead covered (same sign): new input
        int32_t apart_ms = 0;  // the closest lead's message-time distance, when not 0 (for the log)
    };
    // What waiting leads of the other source cover of this input; they are used up as they cover it. `delta`: the
    // wheel's (0 for a button, which one lead covers).
    Follow follow(Source s, uint32_t kind, int32_t delta, uint32_t time) {
        forget_older_than(time);
        Follow r;
        int32_t need = delta ? std::abs(delta) : 1;
        while (need > 0) {
            auto best = q_.end();
            uint32_t best_dt = UINT32_MAX;
            for (auto it = q_.begin(); it != q_.end(); ++it) {
                if (it->source == s || it->kind != kind) continue;
                const uint32_t dt = distance(it->time, time);
                if (dt < best_dt) {  // the oldest of equally close ones
                    best = it;
                    best_dt = dt;
                }
            }
            if (best == q_.end() || best_dt > (r.matched ? kTwinMs : kTolMs)) break;
            if (!r.matched) r.apart_ms = static_cast<int32_t>(best_dt);
            r.matched = true;
            r.kept = r.kept || best->kept;
            const int32_t take = std::min(best->left, need);
            best->left -= take;
            need -= take;
            if (best->left == 0) q_.erase(best);
        }
        if (delta) r.rest = delta > 0 ? need : -need;
        return r;
    }
    // A lead (for a wheel: the delta it brought) and what was decided for it, for its twin to follow.
    void lead(Source s, uint32_t kind, int32_t delta, uint32_t time, bool kept) {
        if (q_.size() >= kMaxWaiting) q_.pop_front();
        q_.push_back({s, kind, delta ? std::abs(delta) : 1, time, kept});
    }
    void clear() { q_.clear(); }
    size_t waiting() const { return q_.size(); }

private:
    struct Entry {
        Source source;
        uint32_t kind;
        int32_t left;  // the wheel delta not yet covered by a twin (1 for a button)
        uint32_t time;
        bool kept;
    };
    // Message times are milliseconds that wrap: compare their difference.
    static uint32_t distance(uint32_t a, uint32_t b) {
        const int32_t d = static_cast<int32_t>(a - b);
        return d < 0 ? static_cast<uint32_t>(-static_cast<int64_t>(d)) : static_cast<uint32_t>(d);
    }
    void forget_older_than(uint32_t now) {
        while (!q_.empty() && static_cast<int32_t>(now - q_.front().time) > static_cast<int32_t>(kTwinMs)) q_.pop_front();
    }
    std::deque<Entry> q_;
};

// The part of a notch turned so far, per axis.
class Notches {
public:
    // Adds one turn's delta; returns the notches it completes, with the turn's sign (0 while a notch is not full).
    int add(int axis, int32_t delta) {
        int32_t& rest = rest_[axis ? 1 : 0];
        if (delta == 0) return 0;
        if ((rest > 0 && delta < 0) || (rest < 0 && delta > 0)) rest = 0;  // the other way: a new notch
        rest += delta;
        const int32_t n = rest / kNotch;  // toward zero: the rest keeps the turn's sign
        rest -= n * kNotch;
        return static_cast<int>(n);
    }
    void clear() { rest_[0] = rest_[1] = 0; }

private:
    int32_t rest_[2] = {0, 0};
};

}  // namespace kui::mouse
