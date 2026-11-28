// SPDX-License-Identifier: MIT
// kiln.combat.speed's melee contact sampler, the part without the game (header-only; tests/speed_test.cpp). NOT BOUND
// to the game in this release: Kiln.ActionSpeed's Status.MeleeSampling is false and capability combat.melee_sampling is
// lowered. The sites it will use resolve in sigdb (combat-speed.json: speed/HandSlots, SlotToWorld, ColliderCreate,
// ColliderMove, CastShape, ContactWriter, ImpactContacts, CollisionBackend; docs/re/action-speed.md).
//
// What it decides, per melee impact collider and step, with fixed storage only (no allocation, a capped sample count):
//  - which part of the impact's window this step covers: the step's sequence interval [lo, hi] (after the rate)
//    intersected with the impact window [t, t + duration]. A window that starts and ends inside one step is sampled
//    whole; one that crosses steps is sampled in parts; a collider that appears late (a step after its window opened,
//    or after it closed) samples, on its first sweep, the window from its start, from the hand's pose history;
//  - when to sample: evenly over that interval, at most kMaxSamples, never fewer than one;
//  - the pose at a sample time: from the actor's hand pose history (one pose per step), position and scale lerped,
//    rotation slerped (estimated poses between measured frames);
//  - which contacts to hand to the original impact: distinct targets not already in its stream (also as the reversed
//    impact/target pair), within the stream's capacity, so block and repeat-hit rules apply as for the game's own.
#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>

namespace kc::sampler {

constexpr uint32_t kMaxSamples = 10;    // intermediate shape casts per collider and step (CATT's cap)
constexpr uint32_t kHistory = 6;        // hand poses kept per actor (one per step): covers a collider 4 steps late
constexpr uint32_t kMaxContacts = 32;   // contacts one cast may return into Kiln's bounded list

struct Interval {
    int64_t lo = 0, hi = 0;  // sequence time, ns
    bool empty() const { return hi <= lo; }
    int64_t length() const { return hi > lo ? hi - lo : 0; }
};

// The part of an impact window [t, t + duration] a step covers. step = [lo, hi] (the sequence time before and after this
// step's advance). first: the collider's first sweep (it may have appeared late): its interval starts at the window's
// start even when that lies in an earlier step. Empty: nothing to sample this step.
inline Interval window_part(Interval step, int64_t t, int64_t duration, bool first) {
    if (duration <= 0 || step.hi <= step.lo) return {};
    const int64_t end = t + duration;
    Interval r{first ? t : std::max(step.lo, t), std::min(step.hi, end)};
    if (r.lo < t) r.lo = t;
    return r.hi > r.lo ? r : Interval{};
}

// Sample times over `part`, evenly: the interval's midpoints of n equal pieces, n = ceil(length / spacing) clamped to
// [1, cap]. Returns n; times are written to out[0..n).
inline uint32_t sample_times(Interval part, int64_t spacing, uint32_t cap, int64_t* out) {
    if (part.empty() || cap == 0) return 0;
    cap = std::min(cap, kMaxSamples);
    const int64_t len = part.length();
    uint64_t n = spacing > 0 ? static_cast<uint64_t>((len + spacing - 1) / spacing) : cap;
    n = std::clamp<uint64_t>(n, 1, cap);
    for (uint64_t i = 0; i < n; ++i)
        out[i] = part.lo + static_cast<int64_t>((static_cast<double>(len) * (2.0 * static_cast<double>(i) + 1.0)) / (2.0 * static_cast<double>(n)));
    return static_cast<uint32_t>(n);
}

// ---- poses ----
struct Pose {
    double pos[3] = {0, 0, 0};  // world metres (the game's 32.32 fixed point, converted)
    float rot[4] = {0, 0, 0, 1}; // quaternion x, y, z, w
    float scale[3] = {1, 1, 1};
};

inline void slerp(const float a[4], const float b0[4], float f, float out[4]) {
    float b[4] = {b0[0], b0[1], b0[2], b0[3]};
    double dot = double(a[0]) * b[0] + double(a[1]) * b[1] + double(a[2]) * b[2] + double(a[3]) * b[3];
    if (dot < 0) {  // the shorter way round
        for (float& x : b) x = -x;
        dot = -dot;
    }
    double wa, wb;
    if (dot > 0.9995) {
        wa = 1.0 - f, wb = f;  // nearly the same: lerp (normalised below)
    } else {
        const double th = std::acos(std::clamp(dot, -1.0, 1.0)), s = std::sin(th);
        wa = std::sin((1.0 - f) * th) / s, wb = std::sin(f * th) / s;
    }
    double q[4], n = 0;
    for (int i = 0; i < 4; ++i) q[i] = wa * a[i] + wb * b[i], n += q[i] * q[i];
    n = n > 0 ? std::sqrt(n) : 1.0;
    for (int i = 0; i < 4; ++i) out[i] = static_cast<float>(q[i] / n);
}

inline Pose interpolate(const Pose& a, const Pose& b, float f) {
    Pose p;
    for (int i = 0; i < 3; ++i) {
        p.pos[i] = a.pos[i] + (b.pos[i] - a.pos[i]) * f;
        p.scale[i] = a.scale[i] + (b.scale[i] - a.scale[i]) * f;
    }
    slerp(a.rot, b.rot, f, p.rot);
    return p;
}

// One actor's measured hand poses, one per step, keyed by the step's sequence time (fixed ring, no allocation).
class PoseHistory {
public:
    void push(int64_t time, const Pose& p) {
        if (count_ && time <= at_[(head_ + count_ - 1) % kHistory]) {  // a repeat or a rewind: start over
            count_ = 0;
        }
        const uint32_t i = (head_ + count_) % kHistory;
        if (count_ == kHistory) head_ = (head_ + 1) % kHistory;
        else ++count_;
        at_[i] = time;
        pose_[i] = p;
    }
    uint32_t size() const { return count_; }
    // The pose at `time`: interpolated between the two measured poses around it, the nearest one outside them.
    bool at(int64_t time, Pose* out) const {
        if (!count_) return false;
        const uint32_t first = head_, last = (head_ + count_ - 1) % kHistory;
        if (time <= at_[first]) return *out = pose_[first], true;
        if (time >= at_[last]) return *out = pose_[last], true;
        for (uint32_t k = 0; k + 1 < count_; ++k) {
            const uint32_t i = (head_ + k) % kHistory, j = (head_ + k + 1) % kHistory;
            if (time <= at_[j]) {
                const double f = double(time - at_[i]) / double(at_[j] - at_[i]);
                *out = interpolate(pose_[i], pose_[j], static_cast<float>(f));
                return true;
            }
        }
        return *out = pose_[last], true;
    }
    void clear() { count_ = 0, head_ = 0; }

private:
    int64_t at_[kHistory] = {};
    Pose pose_[kHistory];
    uint32_t head_ = 0, count_ = 0;
};

// ---- contacts ----
// The fields of a CollisionEvent (0x40 bytes) the merge reads (the author's layout; checked again when it is bound).
struct Contact {
    uint32_t impact = 0, target = 0, query = 0;
    float pos[3] = {0, 0, 0}, normal[3] = {0, 0, 0};
    uint32_t target_flags = 0;
};

inline bool same_pair(const Contact& a, uint32_t impact, uint32_t target) {
    return (a.impact == impact && a.target == target) || (a.impact == target && a.target == impact);
}

// Appends the pending contacts whose target the stream (and the earlier pending ones) does not name yet, as the
// impact/target pair or reversed, while there is room: count grows up to capacity, nothing is reallocated. Returns how
// many were appended.
inline uint32_t merge(Contact* stream, uint32_t* count, uint32_t capacity, const Contact* pending, uint32_t n) {
    uint32_t added = 0;
    for (uint32_t i = 0; i < n && *count < capacity; ++i) {
        const Contact& c = pending[i];
        if (!c.impact || !c.target) continue;
        bool known = false;
        for (uint32_t k = 0; k < *count && !known; ++k) known = same_pair(stream[k], c.impact, c.target);
        if (known) continue;
        stream[(*count)++] = c;
        ++added;
    }
    return added;
}

// Contacts a cast found for one impact this step, kept until the impact's preparation takes them (fixed storage).
class Pending {
public:
    static constexpr uint32_t kImpacts = 64, kPerImpact = 16;
    // false: no room (the contact is dropped; the game's own contacts still count)
    bool add(const Contact& c) {
        Slot* s = find(c.impact);
        if (!s) s = claim(c.impact);
        if (!s || s->n >= kPerImpact) return false;
        for (uint32_t i = 0; i < s->n; ++i)
            if (same_pair(s->c[i], c.impact, c.target)) return true;  // already there
        s->c[s->n++] = c;
        return true;
    }
    // Hands the impact's contacts to `fn(const Contact*, uint32_t)` and forgets them.
    template <class Fn>
    void take(uint32_t impact, Fn&& fn) {
        Slot* s = find(impact);
        if (!s) return;
        fn(static_cast<const Contact*>(s->c), s->n);
        s->impact = 0, s->n = 0;
    }
    uint32_t impacts() const {
        uint32_t n = 0;
        for (const Slot& s : slots_) n += s.impact != 0;
        return n;
    }
    void clear() {
        for (Slot& s : slots_) s.impact = 0, s.n = 0;
    }

private:
    struct Slot {
        uint32_t impact = 0, n = 0;
        Contact c[kPerImpact];
    };
    Slot* find(uint32_t impact) {
        for (Slot& s : slots_)
            if (s.impact == impact) return &s;
        return nullptr;
    }
    Slot* claim(uint32_t impact) {
        if (!impact) return nullptr;
        for (Slot& s : slots_)
            if (!s.impact) return s.impact = impact, s.n = 0, &s;
        return nullptr;
    }
    Slot slots_[kImpacts];
};

}  // namespace kc::sampler
