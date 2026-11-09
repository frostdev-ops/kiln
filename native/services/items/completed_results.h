// SPDX-License-Identifier: MIT
#pragma once
#include <cstdint>
#include <vector>

namespace kitems {
// Protected by the service's result mutex. A delivery batch stays here until every callback returns, since a callback
// may synchronously save the world and ask about another committed transaction whose callback has not run yet.
template <class Result>
class CompletedResults {
    std::vector<Result> waiting_, delivering_;
public:
    void push_back(const Result& result) { waiting_.push_back(result); }
    bool empty() const { return waiting_.empty(); }
    bool delivering() const { return !delivering_.empty(); }
    const std::vector<Result>& begin_delivery() { delivering_.swap(waiting_); return delivering_; }
    void end_delivery() { delivering_.clear(); }
    bool peek(uint64_t ticket, Result* out) const {
        for (const Result& result : waiting_)
            if (result.ticket == ticket) { *out = result; return true; }
        for (const Result& result : delivering_)
            if (result.ticket == ticket) { *out = result; return true; }
        return false;
    }
};
}
