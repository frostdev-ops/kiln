// SPDX-License-Identifier: MIT
// Chat input's narrow window-thread -> UI-thread handoff. The native edit hook,
// not the HUD screen number, must assert focus. With focus off, input is vanilla.
#pragma once
#include <atomic>
#include <cstdint>
#include <deque>
#include <mutex>

namespace kui::chat_keys {

enum class Action : uint8_t { Older = 1, Newer = 2 };

// What ends a history browse: the player changed the field's text (docs/re/chat-input.md, the field's keys).
// A character the field inserts: printable text and Tab (the game's character path keeps '\t' and drops the other
// control characters). Enter, Escape, Backspace and Ctrl+letter arrive as control characters and are not edits here.
constexpr bool inserts_text(uint32_t ch) { return (ch >= 0x20 && ch != 0x7f) || ch == '\t'; }
// A key that changes the text without a character: Backspace, Delete, Shift+Insert (paste), Ctrl+V/X (paste, cut),
// Ctrl+Z/Y (undo, redo). Ctrl+A/C only select or copy. mods: KilnUiKey bits (1 Ctrl, 2 Shift, 4 Alt).
constexpr bool edits_text(uint32_t vk, uint32_t mods) {
    constexpr uint32_t kBack = 0x08, kDelete = 0x2E, kInsert = 0x2D, kCtrl = 1, kShift = 2;
    return vk == kBack || vk == kDelete || ((mods & kShift) && vk == kInsert) ||
           ((mods & kCtrl) && (vk == 'V' || vk == 'X' || vk == 'Z' || vk == 'Y'));
}

class Router {
public:
    void focus(bool yes) {
        if (!yes) {
            active_.store(false, std::memory_order_release);
            std::lock_guard lock(mu_);
            pending_.clear();
        } else {
            active_.store(true, std::memory_order_release);
        }
    }

    bool focused() const { return active_.load(std::memory_order_acquire); }

    // WndProc deduplicates the raw and legacy key messages before calling this.
    // Never swallow key releases; the game must not retain a stuck key.
    bool key(uint32_t vk, uint32_t mods, bool down) {
        if (!focused() || mods != 0 || (vk != 0x26 && vk != 0x28)) return false;
        if (down) {
            std::lock_guard lock(mu_);
            // Focus can be lost between the first check and acquiring the queue lock.
            // Recheck under the lock so a closed field cannot leave a stale press queued.
            if (!active_.load(std::memory_order_acquire)) return false;
            if (pending_.size() < 32) pending_.push_back(vk == 0x26 ? Action::Older : Action::Newer);
        }
        return down;
    }

    // Called only on the game's UI thread by a scoped chat edit hook.
    bool take(Action& out) {
        std::lock_guard lock(mu_);
        if (pending_.empty() || !active_.load(std::memory_order_acquire)) return false;
        out = pending_.front();
        pending_.pop_front();
        return true;
    }

private:
    std::atomic<bool> active_{false};
    std::mutex mu_;
    std::deque<Action> pending_;
};

} // namespace kui::chat_keys
