// SPDX-License-Identifier: MIT
// The shared ray budget and the "is there a world to ask" rule (chapter 01 section 2.6). One bucket per server session
// and published step: Pre, Post and the tasks drained before the next step is published share it, because the core
// drains posted tasks before it publishes the new step. The count is hard; the time is checked before a call, so the
// bucket can run over by one call's time. Pure: the caller gives the session, the step and the time a call took.
#ifndef KILN_QUERIES_BUDGET_H
#define KILN_QUERIES_BUDGET_H
#include <cstdint>

namespace kq {

struct Limits {
    uint32_t max_rays = 64;
    uint32_t max_microseconds = 500;
};

inline constexpr uint32_t kMinRays = 1, kMaxRays = 4096, kMinMicroseconds = 1, kMaxMicroseconds = 10000;

enum class Admit { Ok, WorldNotReady, BudgetExceeded };

class Budget {
public:
    void set_limits(Limits l) { limits_ = l; }
    Limits limits() const { return limits_; }

    // The server world of `session` started; `step` is the step published before its first Pre.
    void world_started(uint32_t session, uint64_t step) {
        session_ = session;
        started_step_ = step;
        live_ = true;
        calls_ = 0;
        elapsed_ns_ = 0;
        bucket_step_ = step;
    }

    // The server world is going: nothing is admitted until the next one's first published step.
    void world_stopping() { live_ = false; }

    // There is a server world whose first Pre was published.
    bool ready(uint32_t session, uint64_t step) const { return live_ && session != 0 && session == session_ && step > started_step_; }

    // One ray wants in. Ok counts it.
    Admit admit(uint32_t session, uint64_t step) {
        if (!ready(session, step)) return Admit::WorldNotReady;
        if (step != bucket_step_) {
            bucket_step_ = step;
            calls_ = 0;
            elapsed_ns_ = 0;
        }
        if (calls_ >= limits_.max_rays || elapsed_ns_ >= static_cast<uint64_t>(limits_.max_microseconds) * 1000u) return Admit::BudgetExceeded;
        ++calls_;
        return Admit::Ok;
    }

    // The time the admitted call took, whatever it answered.
    void charge(uint64_t nanoseconds) { elapsed_ns_ += nanoseconds; }

    uint32_t calls() const { return calls_; }
    uint64_t elapsed_ns() const { return elapsed_ns_; }

private:
    Limits limits_{};
    uint32_t session_ = 0;
    uint64_t started_step_ = 0, bucket_step_ = 0;
    bool live_ = false;
    uint32_t calls_ = 0;
    uint64_t elapsed_ns_ = 0;
};

}  // namespace kq
#endif
