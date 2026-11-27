// SPDX-License-Identifier: MIT
#pragma once
// The brush-frame adapter's trace: one relaxed counter per exit, and the last exit. The frame hook only records
// (atomics, no allocation, no lock, no log); a client Post publisher turns a change of state into one log line and the
// tools.brush_families capability text, at most once a second (a state that flips every frame becomes one line naming
// how many changes it covers). Host-tested in tests/brush_trace.cpp.
#include "brush_frame.h"
#include <array>
#include <atomic>
#include <cstdint>
#include <string>
namespace kiln::tools {
inline const char* brush_exit_name(BrushFrameExit exit) noexcept {
    switch(exit) {
    case BrushFrameExit::Disabled: return "disabled";
    case BrushFrameExit::NoFamily: return "no held family";
    case BrushFrameExit::NoIdentity: return "no identity";
    case BrushFrameExit::FrameMismatch: return "other frame";
    case BrushFrameExit::NotSelected: return "not selected";
    case BrushFrameExit::NotResolved: return "not resolved";
    case BrushFrameExit::OutputNotFresh: return "output not fresh";
    case BrushFrameExit::TagMismatch: return "tag mismatch";
    case BrushFrameExit::ReadFault: return "read fault";
    case BrushFrameExit::Patched: return "patched";
    case BrushFrameExit::WriteFault: return "write fault";
    default: return "unknown";
    }
}
inline const char* brush_exit_meaning(BrushFrameExit exit) noexcept {
    switch(exit) {
    case BrushFrameExit::Disabled: return "the brush adapter is not installed or retired after a write fault";
    case BrushFrameExit::NoFamily: return "the picker opened without a registered tool family in hand (a vanilla tool, or no current held-item sample)";
    case BrushFrameExit::NoIdentity: return "the held family has no identity record";
    case BrushFrameExit::FrameMismatch: return "the picker frame is not the Overgrowth branch's (Decay, or another layout)";
    case BrushFrameExit::NotSelected: return "the picker's selected hotbar item is neither the held family's tool nor one of its brushes";
    case BrushFrameExit::NotResolved: return "the picker's resolved item is not its selected hotbar item";
    case BrushFrameExit::OutputNotFresh: return "the picker's brush list is not the fresh empty 16-row array";
    case BrushFrameExit::TagMismatch: return "the picker's include/ratio tags are not vanilla Overgrowth's";
    case BrushFrameExit::ReadFault: return "reading the picker frame faulted";
    case BrushFrameExit::Patched: return "the family's brushes were written into the picker";
    case BrushFrameExit::WriteFault: return "writing the picker frame faulted; that query was skipped and the adapter retired";
    default: return "unknown";
    }
}
class BrushTrace {
    static constexpr size_t n=size_t(BrushFrameExit::Count);
    std::array<std::atomic<uint64_t>,n> counts_{};
    std::atomic<uint64_t> truncated_{},changes_{},published_changes_{};
    std::atomic<uint8_t> last_{uint8_t(BrushFrameExit::Count)},published_{uint8_t(BrushFrameExit::Count)};
    std::atomic<uint32_t> rows_{};
public:
    // Hot path: the frame hook. Never allocates, locks or logs.
    void record(const BrushFrameOutcome& outcome) noexcept {
        const auto i=size_t(outcome.exit);if(i>=n)return;
        counts_[i].fetch_add(1,std::memory_order_relaxed);
        if(outcome.truncated)truncated_.fetch_add(1,std::memory_order_relaxed);
        if(outcome.exit==BrushFrameExit::Patched)rows_.store(outcome.rows,std::memory_order_relaxed);
        if(last_.exchange(uint8_t(i),std::memory_order_relaxed)!=uint8_t(i))changes_.fetch_add(1,std::memory_order_relaxed);
    }
    void record(BrushFrameExit exit) noexcept {record(BrushFrameOutcome{BrushFrameResult::Unchanged,exit,0,false});}
    uint64_t count(BrushFrameExit exit) const noexcept {const auto i=size_t(exit);return i<n?counts_[i].load(std::memory_order_relaxed):0;}
    uint64_t truncated() const noexcept {return truncated_.load(std::memory_order_relaxed);}
    uint64_t changes() const noexcept {return changes_.load(std::memory_order_relaxed);}
    bool any() const noexcept {return last_.load(std::memory_order_relaxed)<n;}
    BrushFrameExit last() const noexcept {const auto v=last_.load(std::memory_order_relaxed);return v<n?BrushFrameExit(v):BrushFrameExit::Count;}
    // The publisher's cheap check: a change of state that no take has reported yet.
    bool pending() const noexcept {return changes_.load(std::memory_order_relaxed)!=published_changes_.load(std::memory_order_relaxed);}
    // The publisher: true once per change of state since the previous take (one cheap load when nothing changed).
    // `covered` is how many state changes this publication stands for (more than one when they came faster than it).
    bool take(BrushFrameExit* state,uint64_t* covered) noexcept {
        const auto now=changes_.load(std::memory_order_relaxed),before=published_changes_.load(std::memory_order_relaxed);
        if(now==before)return false;
        published_changes_.store(now,std::memory_order_relaxed);
        const auto v=last_.load(std::memory_order_relaxed);published_.store(v,std::memory_order_relaxed);
        if(state)*state=v<n?BrushFrameExit(v):BrushFrameExit::Count;
        if(covered)*covered=now-before;
        return true;
    }
    // "patched 3 (3 rows), no held family 120, ..." in exit order, nonzero counters only. Allocates: publisher only.
    std::string summary() const {
        std::string text;
        for(size_t i=0;i<n;++i) {
            const auto c=counts_[i].load(std::memory_order_relaxed);if(!c)continue;
            if(!text.empty())text+=", ";
            text+=brush_exit_name(BrushFrameExit(i));text+=' ';text+=std::to_string(c);
            if(BrushFrameExit(i)==BrushFrameExit::Patched)text+=" ("+std::to_string(rows_.load(std::memory_order_relaxed))+" rows)";
        }
        if(const auto t=truncated())text+=", truncated to 16 rows "+std::to_string(t);
        return text.empty()?"no picker frames yet":text;
    }
    void reset() noexcept {
        for(auto& c:counts_)c.store(0,std::memory_order_relaxed);
        truncated_.store(0);changes_.store(0);published_changes_.store(0);rows_.store(0);
        last_.store(uint8_t(n));published_.store(uint8_t(n));
    }
};
} // namespace kiln::tools
