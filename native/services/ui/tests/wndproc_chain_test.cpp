// SPDX-License-Identifier: MIT
// Host test of wndproc_chain.h: Kiln's window procedure and another module's on the same window.
//
// The case from the field: shroudlink's client addon subclasses the game window when a world opens. Kiln looked once a
// second, saw a procedure that was not its own on the window and subclassed again, with the addon's procedure as the
// one to pass on to. The addon's passes on to Kiln's: the two called each other until the window thread's stack ended.
#include <cassert>
#include <cstdio>

#include "../wndproc_chain.h"

namespace {

using kui::chain::Result;
using kui::chain::State;

State g_kiln;
int g_game_calls, g_game2_calls, g_kiln_calls, g_addon_calls, g_first_calls, g_deepest;
WNDPROC g_addon_prev, g_first_prev;

LRESULT game_proc(HWND, UINT, WPARAM, LPARAM) { return ++g_game_calls; }
LRESULT game_proc2(HWND, UINT, WPARAM, LPARAM) { return ++g_game2_calls; }

LRESULT kiln_proc(HWND h, UINT m, WPARAM w, LPARAM l) {
    const kui::chain::Depth depth;
    if (kui::chain::t_depth > g_deepest) g_deepest = kui::chain::t_depth;
    if (depth.looped()) return DefWindowProcW(h, m, w, l);
    ++g_kiln_calls;
    return CallWindowProcW(g_kiln.below, h, m, w, l);
}

// A mod that subclasses the window and passes every message on to the procedure it replaced.
LRESULT addon_proc(HWND h, UINT m, WPARAM w, LPARAM l) {
    ++g_addon_calls;
    return CallWindowProcW(g_addon_prev, h, m, w, l);
}

// A mod that was on the window before Kiln.
LRESULT first_proc(HWND h, UINT m, WPARAM w, LPARAM l) {
    ++g_first_calls;
    return CallWindowProcW(g_first_prev, h, m, w, l);
}

bool games(WNDPROC p) { return p == &game_proc || p == &game_proc2; }

void subclass(HWND h, WNDPROC mine, WNDPROC& prev) {
    prev = reinterpret_cast<WNDPROC>(SetWindowLongPtrW(h, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(mine)));
}

LRESULT send(HWND h) {
    g_game_calls = g_game2_calls = g_kiln_calls = g_addon_calls = g_first_calls = g_deepest = 0;
    return CallWindowProcW(reinterpret_cast<WNDPROC>(GetWindowLongPtrW(h, GWLP_WNDPROC)), h, 0x0200, 0, 0);
}

WNDPROC top(HWND h) { return reinterpret_cast<WNDPROC>(GetWindowLongPtrW(h, GWLP_WNDPROC)); }

}  // namespace

int main() {
    const HWND win = fakewin::window(0);
    fakewin::procs[0] = &game_proc;

    // nothing to subclass
    assert(kui::chain::attach(g_kiln, nullptr, &kiln_proc, games) == Result::Failed);
    assert(kui::chain::attach(g_kiln, fakewin::window(1), &kiln_proc, games) == Result::Failed);  // no procedure yet
    assert(fakewin::sets == 0);

    // first time: Kiln's on top of the game's
    assert(kui::chain::attach(g_kiln, win, &kiln_proc, games) == Result::Subclassed);
    assert(top(win) == &kiln_proc && g_kiln.below == &game_proc && g_kiln.hwnd == win);
    send(win);
    assert(g_kiln_calls == 1 && g_game_calls == 1);
    const int sets = fakewin::sets;
    assert(kui::chain::attach(g_kiln, win, &kiln_proc, games) == Result::Unchanged);
    assert(fakewin::sets == sets);

    // the addon subclasses after Kiln: Kiln stays below it, however often it looks
    subclass(win, &addon_proc, g_addon_prev);
    assert(g_addon_prev == &kiln_proc);
    for (int i = 0; i < 5; ++i) {
        assert(kui::chain::attach(g_kiln, win, &kiln_proc, games) == Result::Below);
        assert(top(win) == &addon_proc && g_kiln.below == &game_proc && g_kiln.above == &addon_proc);
    }
    assert(fakewin::sets == sets + 1);  // only the addon's own
    send(win);
    assert(g_addon_calls == 1 && g_kiln_calls == 1 && g_game_calls == 1 && g_deepest == 1);
    assert(fakewin::defaults == 0);

    // the addon leaves the way it came
    SetWindowLongPtrW(win, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(g_addon_prev));
    assert(kui::chain::attach(g_kiln, win, &kiln_proc, games) == Result::Unchanged);
    assert(g_kiln.above == nullptr);

    // the game puts its own procedure back: Kiln is out of the chain and subclasses again
    SetWindowLongPtrW(win, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(&game_proc));
    assert(kui::chain::attach(g_kiln, win, &kiln_proc, games) == Result::Again);
    assert(top(win) == &kiln_proc && g_kiln.below == &game_proc);
    send(win);
    assert(g_kiln_calls == 1 && g_game_calls == 1);

    // another procedure of the game, never seen before
    SetWindowLongPtrW(win, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(&game_proc2));
    assert(kui::chain::attach(g_kiln, win, &kiln_proc, games) == Result::Again);
    assert(g_kiln.below == &game_proc2);
    send(win);
    assert(g_kiln_calls == 1 && g_game2_calls == 1 && g_game_calls == 0);

    // the addon on top again, then the game takes the window back over both
    subclass(win, &addon_proc, g_addon_prev);
    assert(kui::chain::attach(g_kiln, win, &kiln_proc, games) == Result::Below);
    SetWindowLongPtrW(win, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(&game_proc));
    assert(kui::chain::attach(g_kiln, win, &kiln_proc, games) == Result::Again);
    assert(top(win) == &kiln_proc && g_kiln.below == &game_proc);

    // another window: a mod was there first, Kiln goes on top of it; the mod leaves by restoring the game's
    const HWND win2 = fakewin::window(1);
    fakewin::procs[1] = &game_proc;
    subclass(win2, &first_proc, g_first_prev);
    assert(kui::chain::attach(g_kiln, win2, &kiln_proc, games) == Result::Subclassed);
    assert(g_kiln.hwnd == win2 && g_kiln.below == &first_proc && g_kiln.n_seen == 1);
    send(win2);
    assert(g_kiln_calls == 1 && g_first_calls == 1 && g_game_calls == 1);
    SetWindowLongPtrW(win2, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(g_first_prev));
    assert(kui::chain::attach(g_kiln, win2, &kiln_proc, games) == Result::Again);
    assert(g_kiln.below == &game_proc);
    // a procedure that was below Kiln's counts as "Kiln is out", whoever it belongs to
    SetWindowLongPtrW(win2, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(&first_proc));
    assert(kui::chain::attach(g_kiln, win2, &kiln_proc, games) == Result::Again);
    assert(g_kiln.below == &first_proc);

    // A loop somebody else closed (here by hand: Kiln's passes on to the addon's, the addon's to Kiln's). The message
    // leaves it through DefWindowProc instead of ending the stack.
    g_addon_prev = &kiln_proc;
    g_kiln.below = &addon_proc;
    fakewin::defaults = 0;
    send(win2);
    assert(fakewin::defaults == 1);
    assert(g_deepest == kui::chain::kMaxDepth + 1);
    assert(g_kiln_calls == kui::chain::kMaxDepth && g_addon_calls == kui::chain::kMaxDepth);
    assert(kui::chain::t_depth == 0);

    std::puts("wndproc_chain_test: ok");
    return 0;
}
