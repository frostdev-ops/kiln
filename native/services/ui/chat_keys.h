// SPDX-License-Identifier: MIT
// One narrowly focused window-thread -> UI-thread chat key router.
// Preserves PR #63's history behaviour; completion is inert until the client
// explicitly supplies live suggestions for the focused native chat editor.
#pragma once
#include <atomic>
#include <array>
#include <cstdint>
#include <deque>
#include <mutex>

namespace kui::chat_keys {

enum class Action : uint8_t {
    Older = 1, Newer = 2,
    CompleteNext = 3, CompletePrevious = 4,
    SelectOlder = 5, SelectNewer = 6, Dismiss = 7,
    SearchStart = 8, SearchOlder = 9, SearchNewer = 10,
    SearchAccept = 11, SearchCancel = 12, SearchBackspace = 13,
    SearchCharacter = 14
};

// What ends a history browse: actual typed characters, not the transient
// before/after difference of the game's own chat widget.
constexpr bool inserts_text(uint32_t ch) { return (ch >= 0x20 && ch != 0x7f) || ch == '\t'; }
// Keys changing the text without a character. 1=Ctrl, 2=Shift, 4=Alt.
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
            tab_character_.store(false, std::memory_order_release);
            tab_down_.store(false, std::memory_order_release);
            completion_.store(0, std::memory_order_release);
            searching_.store(false, std::memory_order_release);
            ctrl_r_down_.store(false, std::memory_order_release);
            enter_down_.store(false, std::memory_order_release);
            enter_char_pending_.store(false, std::memory_order_release);
            escape_char_pending_.store(false, std::memory_order_release);
            for (auto& held : search_keys_down_) held.store(false, std::memory_order_release);
        } else {
            active_.store(true, std::memory_order_release);
        }
    }

    bool focused() const { return active_.load(std::memory_order_acquire); }

    // Called from the UI thread after validated native chat input readback.
    // The lower bit enables completion. Never consume Tab with no live
    // candidates unless its list was already activated. Native chat keeps its
    // ordinary Tab behaviour when no candidates exist.
    void completion(bool enabled, bool suggestions, bool list_open) {
        constexpr uint8_t kEnabled = 1, kSuggestions = 2, kList = 4;
        const uint8_t bits = enabled ? static_cast<uint8_t>(kEnabled |
                                (suggestions ? kSuggestions : 0) | (list_open ? kList : 0)) : 0;
        completion_.store(bits, std::memory_order_release);
        if (!enabled) tab_character_.store(false, std::memory_order_release);
    }

    // History and completion are independent user options. Preserve history
    // as enabled in this standalone router's original host tests by default.
    void history(bool enabled) {
        history_.store(enabled, std::memory_order_release);
        if (!enabled) search(false);
    }

    void search_enabled(bool enabled) {
        search_enabled_.store(enabled, std::memory_order_release);
        if (!enabled) search(false);
    }
    bool searching() const { return searching_.load(std::memory_order_acquire); }
    // The UI thread closes search after an accept/cancel. No game input is changed here.
    void search(bool active) {
        searching_.store(active, std::memory_order_release);
        // Keep the paired Enter WM_CHAR guarded until it arrives, even after accept.
    }
    // IME owns the keyboard once a composition starts; native search fails closed.
    void ime_started() {
        if (!searching_.exchange(false, std::memory_order_acq_rel)) return;
        std::lock_guard lock(mu_);
        pending_.clear();
        pending_.push_back({Action::SearchCancel, 0});
    }
    // Window-thread WM_CHAR. Search is deliberately separate from Enshrouded's edit.
    // The game must not see even a single typed character while this is active.
    bool search_character(uint32_t ch) {
        if (!focused()) return false;
        if (ch == 0x0d && enter_char_pending_.exchange(false, std::memory_order_acq_rel)) return true;
        if (ch == 0x1b && escape_char_pending_.exchange(false, std::memory_order_acq_rel)) return true;
        if (!searching()) return false;
        if (ch == 0x0d) { // legacy WM_CHAR without a preceding native keydown
            if (!enter_down_.exchange(true, std::memory_order_acq_rel)) {
                std::lock_guard lock(mu_);
                if (pending_.size() < 32) pending_.push_back({Action::SearchAccept, 0});
            }
            return true;
        }
        if (ch == 0x09 || ch == 0x1b || ch < 0x20 || ch == 0x7f) return true;
        std::lock_guard lock(mu_);
        if (pending_.size() < 32) pending_.push_back({Action::SearchCharacter, ch});
        return true;
    }

    // Raw/legacy keydown is deduplicated by input.cpp. This method MUST never
    // swallow key releases; native game input needs those to avoid stuck keys.
    bool key(uint32_t vk, uint32_t mods, bool down) {
        constexpr uint32_t kUp = 0x26, kDown = 0x28, kTab = 0x09, kEscape = 0x1b;
        constexpr uint32_t kReturn = 0x0d, kBack = 0x08, kCtrl = 1, kShift = 2, kAlt = 4;
        if (!down) {
            if (vk == kTab) tab_down_.store(false, std::memory_order_release);
            if (vk < search_keys_down_.size()) search_keys_down_[vk].store(false, std::memory_order_release);
            if (vk == 'R') ctrl_r_down_.store(false, std::memory_order_release);
            // Keep Escape's paired-character guard across key-up too: Proton
            // can deliver the WM_CHAR after the release, as with Enter.
            if (vk == kReturn) {
                enter_down_.store(false, std::memory_order_release);
                // A delayed WM_CHAR can follow key-up on Proton. Keep its
                // one-character guard until it arrives or another press starts.
            }
            return false; // NEVER intercept releases, including search's
        }
        if (!focused()) return false;
        // A previous accepted Enter may not have produced WM_CHAR. Disarm
        // that stale guard on the next physical press before native chat can
        // handle a completely new Enter normally.
        if (vk == kReturn && !searching())
            enter_char_pending_.store(false, std::memory_order_release);
        if (vk == kEscape && !searching())
            escape_char_pending_.store(false, std::memory_order_release);
        const bool history_enabled = history_.load(std::memory_order_acquire);
        const bool can_search = history_enabled && search_enabled_.load(std::memory_order_acquire);
        if (can_search && vk == 'R' && (mods == kCtrl || mods == (kCtrl | kShift))) {
            if (!searching() && mods != kCtrl) return false;
            std::lock_guard lock(mu_);
            if (pending_.size() >= 32) {
                // In an active search, a stalled UI thread must never let
                // Ctrl+R escape to the game's editor. Drop only the action.
                ctrl_r_down_.store(true, std::memory_order_release);
                return searching();
            }
            if (!ctrl_r_down_.exchange(true, std::memory_order_acq_rel)) {
                const bool was_open = searching_.exchange(true, std::memory_order_acq_rel);
                if (!was_open) pending_.clear(); // stale history/completion events must not run inside search
                pending_.push_back({!was_open ? Action::SearchStart :
                                    mods == kCtrl ? Action::SearchOlder : Action::SearchNewer, 0});
                completion_.store(0, std::memory_order_release);
            }
            return true;
        }
        if (searching()) {
            // A search is its own text-entry mode: no typing or editing reaches
            // the underlying editor. Alt+Tab/system shortcuts stay with Windows.
            // Alt-only shortcuts belong to Windows. AltGr is Ctrl+Alt on
            // Windows/Proton, so its printable characters must stay in the
            // private search query instead of leaking into the game editor.
            if ((mods & kAlt) && !(mods & kCtrl)) return false;
            Action action{};
            if (mods == 0 && (vk == kReturn || vk == kTab)) action = Action::SearchAccept;
            else if (mods == 0 && vk == kEscape) action = Action::SearchCancel;
            else if (mods == 0 && vk == kBack) action = Action::SearchBackspace;
            else if (mods == 0 && vk == kUp) action = Action::SearchNewer;
            else if (mods == 0 && vk == kDown) action = Action::SearchOlder;
            if (action != Action{}) {
                std::lock_guard lock(mu_);
                if (pending_.size() >= 32) {
                    // Always swallow an active search's keydowns, even when
                    // no more actions can be queued. In particular, Enter
                    // must never reach Enshrouded and send the saved draft.
                    // Keep paired WM_CHAR guards for these dropped presses.
                    if (vk == kTab) {
                        tab_down_.store(true, std::memory_order_release);
                        tab_character_.store(true, std::memory_order_release);
                    } else if (vk == kReturn) {
                        enter_down_.store(true, std::memory_order_release);
                        enter_char_pending_.store(true, std::memory_order_release);
                    } else if (vk == kEscape) {
                        escape_char_pending_.store(true, std::memory_order_release);
                    }
                    return true;
                }
                if (vk == kTab) {
                    if (!tab_down_.exchange(true, std::memory_order_acq_rel))
                        pending_.push_back({action, 0});
                    tab_character_.store(true, std::memory_order_release);
                } else if (vk == kEscape) {
                    escape_char_pending_.store(true, std::memory_order_release);
                    if (vk >= search_keys_down_.size() ||
                        !search_keys_down_[vk].exchange(true, std::memory_order_acq_rel))
                        pending_.push_back({action, 0});
                } else if (vk == kReturn) {
                    if (!enter_down_.exchange(true, std::memory_order_acq_rel))
                        pending_.push_back({action, 0});
                    enter_char_pending_.store(true, std::memory_order_release);
                } else if (vk >= search_keys_down_.size() ||
                           !search_keys_down_[vk].exchange(true, std::memory_order_acq_rel))
                    pending_.push_back({action, 0});
            }
            return true; // every other edit key is swallowed without action
        }

        constexpr uint8_t kEnabled = 1, kSuggestions = 2, kList = 4;
        const uint8_t comp = completion_.load(std::memory_order_acquire);
        const bool completion_enabled = (comp & kEnabled) != 0;
        const bool list_open = completion_enabled && (comp & kList) != 0;
        const bool has_candidates = completion_enabled && (comp & kSuggestions) != 0;
        Action action{};

        if (vk == kTab && (mods == 0 || mods == kShift) && (has_candidates || list_open)) {
            action = mods == kShift ? Action::CompletePrevious : Action::CompleteNext;
        } else if (vk == kEscape && mods == 0 && list_open) {
            action = Action::Dismiss;
        } else if (vk == kUp || vk == kDown) {
            if (list_open && mods == 0) {
                action = vk == kUp ? Action::SelectOlder : Action::SelectNewer;
            } else if (history_enabled && (mods == 0 || (list_open && mods == kCtrl))) {
                action = vk == kUp ? Action::Older : Action::Newer;
            } else return false;
        } else return false;

        std::lock_guard lock(mu_);
        if (!active_.load(std::memory_order_acquire) || pending_.size() >= 32) return false;
        if (vk == kTab) {
            if (!tab_down_.exchange(true, std::memory_order_acq_rel))
                pending_.push_back({action, 0});
            tab_character_.store(true, std::memory_order_release);
            return true;
        }
        pending_.push_back({action, 0});
        return true;
    }

    // The game accepts a literal '\t' in WM_CHAR. Only swallow one that is
    // paired with an accepted Tab keydown in this focused chat input.
    bool take_tab_character() {
        if (!focused()) return false;
        return tab_character_.exchange(false, std::memory_order_acq_rel);
    }

    // Called only on the game's UI thread from the scoped TextInputProcess.
    bool take(Action& out, uint32_t* character = nullptr) {
        std::lock_guard lock(mu_);
        if (pending_.empty() || !active_.load(std::memory_order_acquire)) return false;
        out = pending_.front().action;
        if (character) *character = pending_.front().character;
        pending_.pop_front();
        return true;
    }

private:
    std::atomic<bool> active_{false};
    std::atomic<bool> history_{true};
    std::atomic<uint8_t> completion_{0};
    std::atomic<bool> search_enabled_{false};
    std::atomic<bool> searching_{false};
    std::atomic<bool> ctrl_r_down_{false};
    std::atomic<bool> enter_down_{false};
    std::atomic<bool> enter_char_pending_{false};
    std::atomic<bool> escape_char_pending_{false};
    std::array<std::atomic<bool>, 256> search_keys_down_{}; // dedupe raw/legacy navigation & Backspace
    std::atomic<bool> tab_character_{false};
    std::atomic<bool> tab_down_{false};
    std::mutex mu_;
    struct Event { Action action; uint32_t character; };
    std::deque<Event> pending_;
};

} // namespace kui::chat_keys
