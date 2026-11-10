// SPDX-License-Identifier: MIT
// kiln.ui: keeping Kiln's window procedure in the game window's chain without ever closing a loop.
//
// Kiln subclasses the game window (GWLP_WNDPROC) and looks once a second whether its procedure is still the window's.
// When it is not, there are two different cases:
//  - the game put a procedure of its own back, which never calls Kiln's: Kiln subclasses again;
//  - another module subclassed the window after Kiln (shroudlink's client addon does when a world opens). That
//    procedure calls the one it replaced, which is Kiln's, so Kiln is still in the chain. Subclassing again would make
//    Kiln's procedure call the other one, which calls Kiln's, until the window thread's stack ends: on Windows the
//    game crashes, under Wine the window thread ends and the game runs on without a window.
// So Kiln subclasses again only over a procedure that was below its own before, or one that belongs to the game. A
// procedure of anybody else stays on top. `Depth` is the last line of defence against a loop somebody else closes.
#pragma once
#include <windows.h>

namespace kui::chain {

struct State {
    HWND hwnd = nullptr;        // the window Kiln's procedure is on
    WNDPROC below = nullptr;    // what Kiln's procedure calls; written before the procedure is installed
    WNDPROC seen[8] = {};       // every procedure that was below Kiln's on this window
    int n_seen = 0;
    WNDPROC above = nullptr;    // another module's procedure, last seen on top of Kiln's
};

enum class Result {
    Unchanged,   // Kiln's procedure is the window's
    Subclassed,  // first time on this window
    Again,       // the game had put its own procedure back
    Below,       // another module's procedure is on top; Kiln's stays in the chain below it
    Failed,
};

inline bool was_below(const State& s, WNDPROC p) {
    for (int i = 0; i < s.n_seen; ++i)
        if (s.seen[i] == p) return true;
    return false;
}

inline void remember(State& s, WNDPROC p) {
    if (!p || was_below(s, p)) return;
    constexpr int cap = static_cast<int>(sizeof(s.seen) / sizeof(s.seen[0]));
    if (s.n_seen < cap) s.seen[s.n_seen++] = p;
    else s.seen[cap - 1] = p;  // the first ones are the game's own; keep those
}

/// Puts `ours` on `hwnd`, or leaves the window alone when that could close a loop. `games(p)` says whether `p` is a
/// procedure of the game itself.
template <class Games>
Result attach(State& s, HWND hwnd, WNDPROC ours, Games&& games) {
    if (!hwnd || !IsWindow(hwnd)) return Result::Failed;
    const auto current = reinterpret_cast<WNDPROC>(GetWindowLongPtrW(hwnd, GWLP_WNDPROC));
    if (!current) return Result::Failed;
    const bool same = s.hwnd == hwnd;
    if (current == ours) {
        if (!same && !s.below) return Result::Failed;  // ours without a procedure to pass on to: not Kiln's doing
        s.hwnd = hwnd;
        s.above = nullptr;
        return Result::Unchanged;
    }
    if (!same) {
        s.n_seen = 0;
        s.above = nullptr;
    } else if (!was_below(s, current) && !games(current)) {
        s.above = current;
        return Result::Below;
    }
    // The procedure to pass on to is stored before the swap: the window thread may call ours at once.
    s.below = current;
    remember(s, current);
    const auto prev = reinterpret_cast<WNDPROC>(SetWindowLongPtrW(hwnd, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(ours)));
    if (!prev) return Result::Failed;
    if (prev != ours && prev != current) {  // changed between the two calls
        s.below = prev;
        remember(s, prev);
    }
    s.hwnd = hwnd;
    s.above = nullptr;
    return same ? Result::Again : Result::Subclassed;
}

/// How deep Kiln's procedure is nested on this thread. A game sends messages from inside its procedure, so a few
/// levels are normal. Past `kMaxDepth` the chain loops back into Kiln's procedure: the message goes to DefWindowProc
/// instead of round again.
constexpr int kMaxDepth = 64;
inline thread_local int t_depth = 0;
struct Depth {
    Depth() { ++t_depth; }
    ~Depth() { --t_depth; }
    Depth(const Depth&) = delete;
    Depth& operator=(const Depth&) = delete;
    bool looped() const { return t_depth > kMaxDepth; }
};

}  // namespace kui::chain
