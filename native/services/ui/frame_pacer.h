// SPDX-License-Identifier: MIT
// kiln.ui: Kiln's frame cap and the readout's frame counts (graphics.cpp; docs/kiln/graphics.md). Plain logic on a tick
// clock, no windows.h: host test tests/graphics_test.cpp.
//
// The cap paces the frames the game renders: after each of the game's presents the presenting thread waits until the
// next slot in a schedule of one frame per interval, so the game starts its next frame there (a frame is never held
// once it is done). Frames that come late do not build up a debt that later frames would pay back in a burst: a frame
// later than half an interval starts a new schedule from itself. So with the game's own frame limit or V-Sync at a
// lower rate than the cap, the game is never held (theirs decides); at a higher rate, the cap decides.
//
// Frame generation (a later phase) adds presents of its own between the game's: those are output frames, not rendered
// ones. The pacer sees only the game's (`FrameStats::rendered` vs `output`), so a cap of 60 with 2x generation shows
// 120: the cap applies to the rendered frames, before generation multiplies them.
#pragma once
#include <algorithm>
#include <cstdint>

namespace kui::gfx {

// The frame cap's range: 0 off, else 20..500 frames a second. 1..19 counts as 20 (a cap that low is a typo or a test).
inline constexpr uint32_t kMinFps = 20, kMaxFps = 500;
inline uint32_t clamp_fps(int64_t fps) {
    if (fps <= 0) return 0;
    return static_cast<uint32_t>(std::clamp<int64_t>(fps, kMinFps, kMaxFps));
}

class FramePacer {
public:
    // fps 0: off. `freq`: clock ticks a second. A change starts a new schedule.
    void set(uint32_t fps, int64_t freq) {
        const int64_t interval = fps && freq > 0 ? (freq + fps / 2) / fps : 0;
        if (interval == interval_) return;
        interval_ = interval;
        next_ = 0;
    }
    int64_t interval() const { return interval_; }
    bool on() const { return interval_ > 0; }

    // A frame boundary of the game at `now` (its frame was just presented): the tick the game may go on at (`now` when at
    // once). `resynced` is set when this frame started a new schedule (the first frame, or one late by over half an
    // interval).
    int64_t plan(int64_t now, bool* resynced = nullptr) {
        if (resynced) *resynced = false;
        if (!interval_) return now;
        int64_t target = next_;
        if (!next_ || now - target > interval_ / 2) {
            target = now;
            if (resynced) *resynced = true;
        }
        next_ = target + interval_;
        return std::max(now, target);
    }

private:
    int64_t interval_ = 0;
    int64_t next_ = 0;  // when the next frame is due; 0: no schedule yet
};

// How to wait `ticks`: a timer for most of it (`timer` ticks), then a spin on the clock for the rest. A timer wakes late
// by up to its resolution; the spin takes the frame to its tick. `margin`: the timer's worst lateness in ticks
// (TimerMargin below).
struct WaitPlan {
    int64_t timer = 0;  // ticks to sleep on the timer first (0: none)
    bool spin = false;  // then spin until the target
};
inline WaitPlan wait_plan(int64_t ticks, int64_t margin) {
    WaitPlan p;
    if (ticks <= 0) return p;
    p.spin = true;
    if (ticks > margin) p.timer = ticks - margin;
    return p;
}

// The timer's worst lateness, learnt as frames go: the largest of the current and the last window of 64 waits, plus a
// quarter of a millisecond, within 0.25..4 ms; 2 ms until a window is full. A timer that wakes on time spins little
// (Windows' high-resolution timer), one that wakes late (a plain timer, Wine's) spins enough to still be on time.
class TimerMargin {
public:
    int64_t margin(int64_t freq) const {
        const int64_t q = freq / 4000;  // 0.25 ms
        if (!full_) return freq / 500;  // 2 ms
        return std::clamp<int64_t>(std::max(last_max_, cur_max_) + q, q, freq / 250);
    }
    // One timer wait ended `late` ticks after its due time (negative: early).
    void observe(int64_t late) {
        cur_max_ = std::max(cur_max_, std::max<int64_t>(late, 0));
        if (++count_ < 64) return;
        last_max_ = cur_max_;
        cur_max_ = 0;
        count_ = 0;
        full_ = true;
    }

private:
    int64_t cur_max_ = 0, last_max_ = 0;
    int count_ = 0;
    bool full_ = false;
};

// The readout's numbers: frames a second and frame times over windows of about half a second.
class FrameStats {
public:
    // One present of the game (`rendered`: the game's own frame; false: a frame Kiln generated, a later phase).
    void frame(int64_t now, int64_t freq, bool rendered = true) {
        if (!start_) {
            start_ = last_ = now;
            return;
        }
        ++output_;
        if (rendered) {
            ++rendered_;
            const int64_t dt = now - last_rendered_or(now);
            if (dt > 0) {
                sum_ += dt;
                max_ = std::max(max_, dt);
            }
            last_ = now;
        }
        const int64_t span = now - start_;
        if (freq > 0 && span >= freq / 2) {
            const double s = static_cast<double>(span) / static_cast<double>(freq);
            rendered_fps_ = static_cast<float>(rendered_ / s);
            output_fps_ = static_cast<float>(output_ / s);
            frame_ms_ = rendered_ ? static_cast<float>(1000.0 * static_cast<double>(sum_) / static_cast<double>(rendered_) / static_cast<double>(freq)) : 0.0f;
            max_ms_ = static_cast<float>(1000.0 * static_cast<double>(max_) / static_cast<double>(freq));
            start_ = now;
            rendered_ = output_ = 0;
            sum_ = max_ = 0;
        }
    }
    // Nothing presented for a while (a loading screen that stops presenting, a minimised window): the next frame starts
    // a new window instead of counting the gap as one long frame.
    void reset() { *this = FrameStats{}; }

    float rendered_fps() const { return rendered_fps_; }
    float output_fps() const { return output_fps_; }
    float frame_ms() const { return frame_ms_; }  // mean time between the game's frames in the last window
    float max_ms() const { return max_ms_; }      // the longest of them

private:
    int64_t last_rendered_or(int64_t now) const { return last_ ? last_ : now; }
    int64_t start_ = 0, last_ = 0;
    uint64_t rendered_ = 0, output_ = 0;
    int64_t sum_ = 0, max_ = 0;
    float rendered_fps_ = 0, output_fps_ = 0, frame_ms_ = 0, max_ms_ = 0;
};

}  // namespace kui::gfx
