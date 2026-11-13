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
//    dispatched and decides whether the game gets it), the other follows that decision and is never dispatched. A pair
//    is the same input (button and transition, or wheel axis and direction) from the other source within kTwinMs of
//    message time (the twins carry the same time); the oldest waiting lead is taken first. A lead nothing follows (a
//    window without raw mouse input, or without legacy messages) is forgotten once kTwinMs have passed.
//  - The wheel: one press per WHEEL_DELTA (120) turned, the direction from the delta's sign. A high-resolution wheel sends
//    smaller deltas: they add up (Notches), and a turn the other way starts a new notch. A delta that completes no notch
//    is kept from the game when the last notch of the same chord was (KeptChords): a binding on Ctrl+WheelUp keeps the
//    whole of each notch after its first from the game's camera, not only the delta that completes it.
#pragma once
#include <cstdint>
#include <cstdlib>
#include <deque>
#include <optional>

namespace kui::mouse {

constexpr uint32_t kVkMiddle = 0x04, kVkX1 = 0x05, kVkX2 = 0x06;
constexpr uint32_t kVkWheelUp = 0x100, kVkWheelDown = 0x101, kVkWheelLeft = 0x102, kVkWheelRight = 0x103;
constexpr int32_t kNotch = 120;              // WHEEL_DELTA
constexpr int kMaxNotchesPerTurn = 16;       // one message never makes more presses than this
constexpr uint32_t kTwinMs = 250;            // a record and its message are this close in message time at most
constexpr size_t kMaxWaiting = 32;           // leads waiting for their twin

enum class Source : uint8_t { Raw, Message };

inline bool is_wheel_vk(uint32_t vk) { return vk >= kVkWheelUp && vk <= kVkWheelRight; }
inline bool is_button_vk(uint32_t vk) { return vk == kVkMiddle || vk == kVkX1 || vk == kVkX2; }

// axis 0 vertical (WM_MOUSEWHEEL, RI_MOUSE_WHEEL: positive = away from the player = up), 1 horizontal
// (WM_MOUSEHWHEEL, RI_MOUSE_HWHEEL: positive = right).
inline uint32_t wheel_vk(int axis, int32_t delta) {
    return axis == 0 ? (delta > 0 ? kVkWheelUp : kVkWheelDown) : (delta > 0 ? kVkWheelRight : kVkWheelLeft);
}

// What pairs a record with its message: a button and its transition, or a wheel axis and direction.
inline uint32_t button_kind(uint32_t vk, bool down) { return vk | (down ? 0x10000u : 0u); }
inline uint32_t wheel_kind(int axis, int32_t delta) { return wheel_vk(axis, delta) | 0x20000u; }

class Twins {
public:
    // The decision of this input's twin from the other source (true: kept from the game), which is forgotten; none when
    // no twin waits: this input leads.
    std::optional<bool> follow(Source s, uint32_t kind, uint32_t time) {
        forget_older_than(time);
        for (auto it = q_.begin(); it != q_.end(); ++it) {
            if (it->source == s || it->kind != kind || !within_twin_ms(it->time, time)) continue;
            const bool kept = it->kept;
            q_.erase(it);
            return kept;
        }
        return std::nullopt;
    }
    // A lead and what was decided for it, for its twin to follow.
    void lead(Source s, uint32_t kind, uint32_t time, bool kept) {
        if (q_.size() >= kMaxWaiting) q_.pop_front();
        q_.push_back({s, kind, time, kept});
    }
    void clear() { q_.clear(); }
    size_t waiting() const { return q_.size(); }

private:
    struct Entry {
        Source source;
        uint32_t kind;
        uint32_t time;
        bool kept;
    };
    // Message times are milliseconds that wrap: compare their difference.
    static bool within_twin_ms(uint32_t a, uint32_t b) {
        const int32_t d = static_cast<int32_t>(a - b);
        return d <= static_cast<int32_t>(kTwinMs) && d >= -static_cast<int32_t>(kTwinMs);
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

// Per wheel direction and modifiers (KILN_UI_MOD_*): whether its last notch was kept from the game.
class KeptChords {
public:
    bool get(uint32_t wheel_vk, uint32_t mods) const { return kept_[index(wheel_vk)][mods & 7]; }
    void set(uint32_t wheel_vk, uint32_t mods, bool kept) { kept_[index(wheel_vk)][mods & 7] = kept; }
    void clear() {
        for (auto& d : kept_)
            for (bool& b : d) b = false;
    }

private:
    static size_t index(uint32_t vk) { return is_wheel_vk(vk) ? vk - kVkWheelUp : 0; }
    bool kept_[4][8] = {};
};

}  // namespace kui::mouse
