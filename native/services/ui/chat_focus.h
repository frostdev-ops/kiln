// SPDX-License-Identifier: MIT
// Issue #44: tolerate a short native chat edit-focus gap without discarding
// Up/Down browsing state. Keyboard capture is still disabled on EVERY
// unfocused frame; this only delays forgetting the unsent draft.
#pragma once

namespace kui::chat_focus {
class Grace {
public:
    static constexpr unsigned kMissingFramesBeforeReset = 4;

    // UI-thread only. True once when focus stays absent long enough to
    // invalidate the history cursor, false for brief UI frame transitions.
    bool frame(bool editing) {
        if (editing) {
            missing_ = 0;
            return false;
        }
        if (missing_ >= kMissingFramesBeforeReset) return false;  // already reset: not again every unfocused frame
        return ++missing_ == kMissingFramesBeforeReset;
    }

    unsigned missing() const { return missing_; }
private:
    unsigned missing_ = 0;
};
} // namespace kui::chat_focus
