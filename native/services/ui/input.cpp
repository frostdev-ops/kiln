// SPDX-License-Identifier: MIT
// kiln.ui: the game window's input (docs/re/client-render.md).
//  - The game registers raw input for mouse + keyboard (usage pages 1/2 and 1/6, flags 0: no RIDEV_NOLEGACY) and reads
//    it with GetRawInputData in its WndProc; it imports no GetAsyncKeyState/GetKeyState. Kiln subclasses the WndProc:
//    keys (from WM_INPUT, or WM_KEYDOWN when no raw keyboard arrives) go to the menu key and the key subscribers; while
//    Kiln captures input, WM_INPUT and the key/mouse messages are kept from the game (key releases always pass, so
//    nothing stays held) and queued for ImGui, which reads them on the render thread before each frame.
//  - While capturing, the game's own SetCursorPos and ClipCursor calls (its import slots) are ignored so the cursor can
//    move freely; ImGui draws the cursor.
//  - subscribe_window_input (kiln_ui.h v5): the window's key, button and focus-loss records go to native subscribers
//    as received, before any of the above decides what the game gets.
//  - The middle and side buttons and the wheel go to the key subscribers that ask for them (subscribe_key_ex with
//    KILN_UI_KEYS_MOUSE, kiln_ui.h v7; Kiln.Ui does), as key presses (mouse_press.h): once per physical input although
//    the window gets each one as a raw record and as a message, a notch per WHEEL_DELTA. A press such a subscriber takes
//    is kept from the game like a bound key (its raw record and its message); releases always pass. Over a Kiln panel
//    the middle button and the wheel stay the panel's. Without such a subscriber none of this runs.
#include <windows.h>
#include <windowsx.h>

#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <memory>
#include <mutex>
#include <optional>
#include <unordered_map>
#include <vector>

#include "backends/imgui_impl_win32.h"
#include "imgui.h"
#include "mouse_press.h"
#include "safetyhook.hpp"
#include "ui.h"
#include "raw_input_source.h"
#include "wndproc_chain.h"

extern IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandler(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);

namespace kui {
namespace {

static_assert(mouse::kVkMiddle == VK_MBUTTON && mouse::kVkX1 == VK_XBUTTON1 && mouse::kVkX2 == VK_XBUTTON2 &&
                  mouse::kVkWheelUp == KILN_UI_VK_WHEEL_UP && mouse::kVkWheelDown == KILN_UI_VK_WHEEL_DOWN &&
                  mouse::kVkWheelLeft == KILN_UI_VK_WHEEL_LEFT && mouse::kVkWheelRight == KILN_UI_VK_WHEEL_RIGHT &&
                  mouse::kNotch == WHEEL_DELTA,
              "mouse_press.h's codes are kiln_ui.h's and Windows'");
static_assert(WM_MOUSEHWHEEL <= WM_MOUSELAST, "is_mouse_msg covers the horizontal wheel");

struct Msg {
    HWND hwnd;
    UINT msg;
    WPARAM w;
    LPARAM l;
};

std::mutex g_qmu;
std::deque<Msg> g_queue;
// Kiln's place in the game window's procedure chain (wndproc_chain.h). `below` is what wndproc passes messages on to.
chain::State g_chain;
HWND g_attached = nullptr;
bool g_loop_said = false;
bool g_raw_keyboard_seen = false;
std::atomic<bool> g_down[256] = {};  // keys down, as the window's messages told (the wheel asks from other threads)
bool g_consumed[256] = {};  // vk consumed by a binding: swallow its legacy messages too
std::atomic<uint32_t> g_pressed_vk{0}, g_pressed_mods{0};
bool g_was_capturing = false;

// Raw mouse input as a fallback pointer: while the game has the pointer locked (mouse look), Wine may deliver only
// relative raw motion and no WM_MOUSEMOVE; Kiln then moves its own cursor by the raw deltas.
constexpr UINT kRawMouseMsg = WM_APP + 0x4B1;
// Capture just started: shows the system cursor for one message. During play the game hides it, confines it and warps it,
// and under XWayland that is a pointer lock: the compositor sends relative motion only, so a real mouse works (raw deltas,
// below) but an absolute pointer does nothing. A visible cursor ends the lock; ImGui draws the cursor from then on.
constexpr UINT kShowCursorMsg = WM_APP + 0x4B2;
std::atomic<uint64_t> g_n_move{0}, g_n_raw{0}, g_n_raw_abs{0}, g_n_buttons{0};
uint64_t g_last_legacy_move = 0, g_last_stats = 0;
// Raw mouse buttons stand in for WM_xBUTTON* only until the window has delivered one of those: with both, every
// click would reach ImGui twice (a Button would toggle twice; the game may drain WM_INPUT ahead of the legacy messages).
// The raw wheel likewise stands in for WM_MOUSEWHEEL only until one came (before, a Kiln screen scrolled twice a notch).
bool g_legacy_buttons_seen = false, g_legacy_wheel_seen = false;
float g_vx = -1, g_vy = -1;

// The middle and side buttons and the wheel as key presses (mouse_press.h). Window thread.
mouse::Twins g_twins;            // a raw record or a message that led, for its twin from the other source
mouse::Notches g_notches;        // the part of a wheel notch turned so far
bool g_notches_captured = false; // the notches above were turned while Kiln captured input
bool g_mixed_record_said = false, g_twin_time_said = false;

// The game reads raw input through its own import of user32!GetRawInputData, in its window procedure (no
// GetRawInputBuffer: docs/re/client-render.md). Kiln's import slot hook (input_install_iat) takes the flags of the
// presses a key subscriber took out of the record the game reads, while the window procedure hands that WM_INPUT on:
// motion and the other buttons stay. Null: the slot was not found (the record then reaches the game whole).
using GetRawInputDataFn = UINT(WINAPI*)(HRAWINPUT, UINT, LPVOID, PUINT, UINT);
GetRawInputDataFn g_game_raw_read = nullptr;
bool g_game_raw_read_hooked = false;
struct RawEdit {
    HRAWINPUT handle = nullptr;
    USHORT flags = 0;  // RI_MOUSE_* to take out (a wheel flag takes usButtonData too)
};
thread_local RawEdit t_raw_edit;  // the window thread, around the call down the chain only

// Any WM_INPUT read of the record being handed on loses the taken flags; every other read is untouched (the size query
// too: the record keeps its size).
UINT WINAPI hk_get_raw_input_data(HRAWINPUT h, UINT cmd, LPVOID data, PUINT size, UINT header) {
    const UINT n = g_game_raw_read(h, cmd, data, size, header);
    const RawEdit e = t_raw_edit;
    if (e.flags && h == e.handle && cmd == RID_INPUT && data && header == sizeof(RAWINPUTHEADER) &&
        n != static_cast<UINT>(-1) && n >= sizeof(RAWINPUTHEADER) + sizeof(RAWMOUSE)) {
        auto* ri = static_cast<RAWINPUT*>(data);
        if (ri->header.dwType == RIM_TYPEMOUSE) {
            ri->data.mouse.usButtonFlags = static_cast<USHORT>(ri->data.mouse.usButtonFlags & ~e.flags);
            if (e.flags & (RI_MOUSE_WHEEL | RI_MOUSE_HWHEEL)) ri->data.mouse.usButtonData = 0;
        }
    }
    return n;
}

using SetCursorPosFn = BOOL(WINAPI*)(int, int);
using ClipCursorFn = BOOL(WINAPI*)(const RECT*);
SetCursorPosFn g_real_set_cursor_pos = nullptr;
ClipCursorFn g_real_clip_cursor = nullptr;
RECT g_game_clip{};
bool g_game_clipped = false;

uint32_t current_mods() {
    uint32_t m = 0;
    if (GetKeyState(VK_CONTROL) & 0x8000) m |= KILN_UI_MOD_CTRL;
    if (GetKeyState(VK_SHIFT) & 0x8000) m |= KILN_UI_MOD_SHIFT;
    if (GetKeyState(VK_MENU) & 0x8000) m |= KILN_UI_MOD_ALT;
    return m;
}

void enqueue(HWND h, UINT m, WPARAM w, LPARAM l) {
    std::lock_guard<std::mutex> lk(g_qmu);
    if (g_queue.size() >= 1024) g_queue.pop_front();
    g_queue.push_back({h, m, w, l});
}

bool is_mouse_msg(UINT m) {
    return (m >= WM_MOUSEFIRST && m <= WM_MOUSELAST) || m == WM_MOUSELEAVE || m == WM_NCMOUSEMOVE;
}

bool is_key_msg(UINT m) {
    return m == WM_KEYDOWN || m == WM_KEYUP || m == WM_SYSKEYDOWN || m == WM_SYSKEYUP || m == WM_CHAR || m == WM_SYSCHAR ||
           m == WM_UNICHAR;
}

// One key transition: menu key + subscribers. true = keep it from the game. A mouse button (mouse_press.h) never
// repeats (a press while it is down means its release was lost) and is not a key for take_pressed_key.
bool on_key(uint32_t vk, uint32_t scan, bool down, bool from_mouse = false) {
    if (vk == 0 || vk > 255) return false;
    const bool repeat = down && !from_mouse && g_down[vk].load();
    g_down[vk].store(down);
    KilnUiKey k{};
    k.size = sizeof(k);
    k.vk = vk;
    k.scan = scan;
    k.mods = current_mods();
    k.down = down ? 1 : 0;
    k.repeat = repeat ? 1 : 0;
    k.text_input = g_text_input.load() ? 1 : 0;
    k.captured = capturing() ? 1 : 0;
    if (down && !repeat && !from_mouse) {
        g_pressed_mods.store(k.mods);
        g_pressed_vk.store(vk);
    }
    const bool consumed = dispatch_key(k, from_mouse);
    if (!down) {
        g_consumed[vk] = false;
        return false;  // releases always reach the game
    }
    g_consumed[vk] = consumed;
    return consumed;
}

// A middle or side button transition from one source: dispatched once per physical transition, whichever of its raw
// record and its message comes first (mouse_press.h Twins), to the key subscribers that take mouse presses
// (KILN_UI_KEYS_MOUSE, keys.cpp). true = keep this press from the game. No such subscriber: nothing happens.
// The decision of this input's twin, when one waits (also after the last mouse subscriber left: its decision stands).
std::optional<bool> follow_twin(mouse::Source s, uint32_t kind, int32_t delta, uint32_t time) {
    if (!g_twins.waiting()) return std::nullopt;
    int32_t apart_ms = 0;
    const auto twin = g_twins.follow(s, kind, delta, time, &apart_ms);
    if (!twin && apart_ms && !g_twin_time_said) {
        g_twin_time_said = true;
        logf(KILN_LOG_INFO, "ui: a mouse input came as a raw record or a window message only, or the two carried message "
             "times %d ms apart; Kiln pairs a record with its message by their time, so if a mouse key binding counts a "
             "press twice, send this line (said once)", apart_ms);
    }
    return twin;
}

bool mouse_button(mouse::Source s, uint32_t vk, bool down) {
    const uint32_t kind = mouse::button_kind(vk, down);
    const auto time = static_cast<uint32_t>(GetMessageTime());
    if (const auto twin = follow_twin(s, kind, 0, time)) return *twin;
    if (!keys_mouse_wanted()) return false;
    const bool kept = on_key(vk, 0, down, true);
    g_twins.lead(s, kind, 0, time, kept);
    return kept;
}

// One wheel turn from one source: a press per notch it completes, with the modifiers held now (mouse_press.h).
// true = keep this turn from the game: a notch a subscriber took, or a part of a notch a subscriber would keep (asked as a
// repeat of the notch's press, so the answer is today's, never a decision carried from an earlier notch). While Kiln
// captures input the notches come with captured 1 (the Keys page's capture) and the screen scrolls. Only key subscribers
// with KILN_UI_KEYS_MOUSE get them; with none, nothing happens (a waiting twin's decision still stands).
bool mouse_wheel(mouse::Source s, int axis, int32_t delta) {
    if (delta == 0) return false;
    const uint32_t kind = mouse::wheel_kind(axis, delta);
    const auto time = static_cast<uint32_t>(GetMessageTime());
    if (const auto twin = follow_twin(s, kind, delta, time)) return *twin;
    if (!keys_mouse_wanted()) return false;
    const bool cap = capturing();
    if (cap != g_notches_captured) {  // a notch begun in a Kiln screen does not end in play, nor the other way
        g_notches.clear();
        g_notches_captured = cap;
    }
    const int n = g_notches.add(axis, delta);
    KilnUiKey k{};
    k.size = sizeof(k);
    k.vk = mouse::wheel_vk(axis, delta);
    k.mods = current_mods();
    k.down = 1;
    k.text_input = g_text_input.load() ? 1 : 0;
    k.captured = cap ? 1 : 0;
    bool kept = false;
    if (n == 0) {
        k.repeat = 1;  // a part of a notch: not a press; a subscriber that would take the notch keeps it from the game
        kept = dispatch_key(k, true);
    }
    for (int i = 0; i < std::min(std::abs(n), mouse::kMaxNotchesPerTurn); ++i) kept = dispatch_key(k, true) || kept;
    g_twins.lead(s, kind, delta, time, kept);
    return kept;
}

// Mouse presses run at all: a key subscriber takes them, or a decision waits for its twin.
bool mouse_presses_wanted() { return keys_mouse_wanted() || g_twins.waiting() != 0; }

constexpr USHORT kRawButtonFlags = RI_MOUSE_LEFT_BUTTON_DOWN | RI_MOUSE_LEFT_BUTTON_UP | RI_MOUSE_RIGHT_BUTTON_DOWN |
                                   RI_MOUSE_RIGHT_BUTTON_UP | RI_MOUSE_MIDDLE_BUTTON_DOWN | RI_MOUSE_MIDDLE_BUTTON_UP |
                                   RI_MOUSE_BUTTON_4_DOWN | RI_MOUSE_BUTTON_4_UP | RI_MOUSE_BUTTON_5_DOWN |
                                   RI_MOUSE_BUTTON_5_UP | RI_MOUSE_WHEEL | RI_MOUSE_HWHEEL;

// The presses a raw mouse record carries (middle and side buttons, presses before releases, and the wheel), dispatched.
// Returns the record's flags whose presses Kiln keeps from the game. Over a Kiln panel the middle button's press and the
// wheel are the panel's: no press.
USHORT raw_mouse_presses(const RAWMOUSE& ms, bool over_panel) {
    static constexpr struct {
        uint32_t vk;
        USHORT down, up;
    } kButtons[] = {{VK_MBUTTON, RI_MOUSE_MIDDLE_BUTTON_DOWN, RI_MOUSE_MIDDLE_BUTTON_UP},
                    {VK_XBUTTON1, RI_MOUSE_BUTTON_4_DOWN, RI_MOUSE_BUTTON_4_UP},
                    {VK_XBUTTON2, RI_MOUSE_BUTTON_5_DOWN, RI_MOUSE_BUTTON_5_UP}};
    const USHORT f = ms.usButtonFlags;
    USHORT kept = 0;
    for (const auto& b : kButtons)
        if ((f & b.down) && !(over_panel && b.vk == VK_MBUTTON) && mouse_button(mouse::Source::Raw, b.vk, true)) kept |= b.down;
    for (const auto& b : kButtons)
        if (f & b.up) mouse_button(mouse::Source::Raw, b.vk, false);
    // usButtonData holds one wheel's delta: a record turns one wheel
    const int32_t delta = static_cast<SHORT>(ms.usButtonData);
    if (!over_panel && (f & RI_MOUSE_WHEEL)) {
        if (mouse_wheel(mouse::Source::Raw, 0, delta)) kept |= RI_MOUSE_WHEEL;
    } else if (!over_panel && (f & RI_MOUSE_HWHEEL)) {
        if (mouse_wheel(mouse::Source::Raw, 1, delta)) kept |= RI_MOUSE_HWHEEL;
    }
    return kept;
}

// The presses a mouse message carries (middle and side buttons, the wheel), dispatched. true = a press Kiln keeps from
// the game. Over a Kiln panel the middle button and the wheel are the panel's: no press.
bool message_mouse_presses(UINT m, WPARAM w, bool over_panel) {
    using mouse::Source;
    switch (m) {
        case WM_MBUTTONDOWN:
        case WM_MBUTTONDBLCLK: return !over_panel && mouse_button(Source::Message, VK_MBUTTON, true);
        case WM_MBUTTONUP: mouse_button(Source::Message, VK_MBUTTON, false); return false;
        case WM_XBUTTONDOWN:
        case WM_XBUTTONDBLCLK:
        case WM_XBUTTONUP: {
            const bool down = m != WM_XBUTTONUP;
            bool kept = false;
            if (GET_XBUTTON_WPARAM(w) & XBUTTON1) kept = mouse_button(Source::Message, VK_XBUTTON1, down) || kept;
            if (GET_XBUTTON_WPARAM(w) & XBUTTON2) kept = mouse_button(Source::Message, VK_XBUTTON2, down) || kept;
            return down && kept;
        }
        case WM_MOUSEWHEEL: return !over_panel && mouse_wheel(Source::Message, 0, GET_WHEEL_DELTA_WPARAM(w));
        case WM_MOUSEHWHEEL: return !over_panel && mouse_wheel(Source::Message, 1, GET_WHEEL_DELTA_WPARAM(w));
        default: return false;
    }
}

// Releases can go to the newly focused window. Forget local repeat/consumption state without inventing key messages
// for either subscribers or the game (the v5 focus record lets subscribers reset their own state).
void forget_keys() {
    for (uint32_t vk = 0; vk < 256; ++vk) {
        g_down[vk].store(false, std::memory_order_relaxed);
        g_consumed[vk] = false;
    }
    g_pressed_vk.store(0, std::memory_order_relaxed);
    g_twins.clear();
    g_notches.clear();
}

// A Kiln panel next to a game screen (set_hover_capture) has the mouse over it: its clicks and wheel are Kiln's.
bool hover_capture() {
    return g_hover_panels.load(std::memory_order_relaxed) > 0 && want_draw() && g_want_mouse.load(std::memory_order_relaxed);
}

bool is_button_up(UINT m) {
    return m == WM_LBUTTONUP || m == WM_RBUTTONUP || m == WM_MBUTTONUP || m == WM_XBUTTONUP;
}

// ---- the window's input records for native subscribers (kiln_ui.h v5, subscribe_window_input) ----
// Built from the message as the window received it, before Kiln's own handling, so what Kiln keeps from the game reaches
// the subscribers too. Window thread. While nobody subscribed the window procedure reads g_feed_count only.
struct FeedSub : CallbackLifetime {
    KilnHandle handle = 0;
    KilnOwner owner = 0;
    KilnUiWindowInputFn fn = nullptr;
    void* user = nullptr;
    // Coalesce the two notifications per subscriber: joining must not rearm another subscriber's loss.
    std::atomic<bool> focus_lost{false};
};
std::mutex g_feed_mu;
std::vector<std::shared_ptr<FeedSub>> g_feed_subs;
std::unordered_map<KilnHandle, std::shared_ptr<FeedSub>> g_feed_retiring;  // unsubscribed, a call may still be in flight
std::atomic<uint32_t> g_feed_count{0};

bool guarded_feed(KilnUiWindowInputFn fn, const KilnUiWindowInput* r, void* user) {
    __try {
        fn(r, user);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void feed(KilnUiWindowInput r) {
    r.size = sizeof r;
    std::vector<std::shared_ptr<FeedSub>> subs;
    {
        std::lock_guard lk(g_feed_mu);
        subs = g_feed_subs;
    }
    for (const auto& s : subs) {
        std::lock_guard call(s->gate);
        if (!s->active.load(std::memory_order_acquire)) continue;
        if (r.kind == KILN_UI_INPUT_FOCUS_LOST) {
            if (s->focus_lost.exchange(true, std::memory_order_relaxed)) continue;
        } else {
            s->focus_lost.store(false, std::memory_order_relaxed);
        }
        CallbackLifetime::Invocation invocation(s.get());
        auto* outer = t_callback;
        t_callback = s.get();
        const bool ok = guarded_feed(s->fn, &r, s->user);
        t_callback = outer;
        if (!ok) {
            s->active.store(false, std::memory_order_release);
            set_error("a window input callback faulted; removed");
            std::lock_guard lk(g_feed_mu);
            std::erase_if(g_feed_subs, [&](const auto& x) { return x == s; });
            g_feed_count.store(static_cast<uint32_t>(g_feed_subs.size()), std::memory_order_release);
        }
    }
}

KilnUiWindowInput feed_record(uint32_t kind, UINT m, WPARAM w, LPARAM l) {
    KilnUiWindowInput r{};
    r.kind = kind;
    r.message = m;
    r.wparam = static_cast<uint64_t>(w);
    r.lparam = static_cast<int64_t>(l);
    return r;
}

void feed_button(UINT m, WPARAM w, LPARAM l, uint32_t button, bool down) {
    KilnUiWindowInput r = feed_record(KILN_UI_INPUT_MOUSE_BUTTON, m, w, l);
    r.button = button;
    r.down = down ? 1 : 0;
    feed(r);
}

// A raw keyboard record unchanged; a raw mouse record as one record per button flag, presses before releases.
void feed_raw(const RAWINPUT& ri, UINT m, WPARAM w, LPARAM l) {
    if (ri.header.dwType == RIM_TYPEKEYBOARD) {
        KilnUiWindowInput r = feed_record(KILN_UI_INPUT_RAW_KEY, m, w, l);
        r.make_code = ri.data.keyboard.MakeCode;
        r.flags = ri.data.keyboard.Flags;
        r.vkey = ri.data.keyboard.VKey;
        feed(r);
        return;
    }
    if (ri.header.dwType != RIM_TYPEMOUSE) return;
    static constexpr struct {
        uint32_t button;
        USHORT down, up;
    } kButtons[] = {{1, RI_MOUSE_LEFT_BUTTON_DOWN, RI_MOUSE_LEFT_BUTTON_UP},
                    {2, RI_MOUSE_MIDDLE_BUTTON_DOWN, RI_MOUSE_MIDDLE_BUTTON_UP},
                    {3, RI_MOUSE_RIGHT_BUTTON_DOWN, RI_MOUSE_RIGHT_BUTTON_UP},
                    {4, RI_MOUSE_BUTTON_4_DOWN, RI_MOUSE_BUTTON_4_UP},
                    {5, RI_MOUSE_BUTTON_5_DOWN, RI_MOUSE_BUTTON_5_UP}};
    const USHORT flags = ri.data.mouse.usButtonFlags;
    for (const auto& b : kButtons)
        if (flags & b.down) feed_button(m, w, l, b.button, true);
    for (const auto& b : kButtons)
        if (flags & b.up) feed_button(m, w, l, b.button, false);
}

// WM_[LMR]BUTTON{DOWN,UP,DBLCLK} and WM_XBUTTON*: a double click is the second press.
void feed_button_message(UINT m, WPARAM w, LPARAM l) {
    switch (m) {
        case WM_LBUTTONDOWN: case WM_LBUTTONDBLCLK: return feed_button(m, w, l, 1, true);
        case WM_LBUTTONUP: return feed_button(m, w, l, 1, false);
        case WM_MBUTTONDOWN: case WM_MBUTTONDBLCLK: return feed_button(m, w, l, 2, true);
        case WM_MBUTTONUP: return feed_button(m, w, l, 2, false);
        case WM_RBUTTONDOWN: case WM_RBUTTONDBLCLK: return feed_button(m, w, l, 3, true);
        case WM_RBUTTONUP: return feed_button(m, w, l, 3, false);
        case WM_XBUTTONDOWN: case WM_XBUTTONDBLCLK: case WM_XBUTTONUP: {
            const bool down = m != WM_XBUTTONUP;
            if (GET_XBUTTON_WPARAM(w) & XBUTTON1) feed_button(m, w, l, 4, down);
            if (GET_XBUTTON_WPARAM(w) & XBUTTON2) feed_button(m, w, l, 5, down);
            return;
        }
        default: return;
    }
}

void feed_focus_lost(UINT m, WPARAM w, LPARAM l) {
    feed(feed_record(KILN_UI_INPUT_FOCUS_LOST, m, w, l));
}
void feed_focus_gained() {
    std::lock_guard lk(g_feed_mu);
    for (const auto& s : g_feed_subs) s->focus_lost.store(false, std::memory_order_relaxed);
}

LRESULT CALLBACK wndproc(HWND h, UINT m, WPARAM w, LPARAM l) {
    // A loop in the window's procedure chain (another module subclassing over and over) would end the window thread's
    // stack. The message leaves the loop here; input_attach never closes one itself.
    const chain::Depth depth;
    if (depth.looped()) {
        if (!g_loop_said) {
            g_loop_said = true;
            logf(KILN_LOG_ERROR, "ui: the game window's procedures call each other in a loop (message 0x%x, %d levels deep): "
                 "another module subclassed the window in a way that leads back to Kiln's. Kiln stops passing such messages on; "
                 "the game may miss input", m, chain::kMaxDepth);
        }
        return DefWindowProcW(h, m, w, l);
    }
    const bool cap = capturing();
    const bool fed = g_feed_count.load(std::memory_order_relaxed) != 0;  // subscribe_window_input: records before Kiln's handling
    switch (m) {
        case kShowCursorMsg:
            if (cap) SetCursor(LoadCursorW(nullptr, IDC_ARROW));
            return 0;
        case WM_INPUT: {
            UINT size = 0;
            GetRawInputData(reinterpret_cast<HRAWINPUT>(l), RID_INPUT, nullptr, &size, sizeof(RAWINPUTHEADER));
            alignas(8) uint8_t buf[256];
            if (size > 0 && size <= sizeof(buf) &&
                GetRawInputData(reinterpret_cast<HRAWINPUT>(l), RID_INPUT, buf, &size, sizeof(RAWINPUTHEADER)) == size) {
                const RAWINPUT* ri = reinterpret_cast<const RAWINPUT*>(buf);
                if (fed) feed_raw(*ri, m, w, l);
                if (ri->header.dwType == RIM_TYPEKEYBOARD) {
                    g_raw_keyboard_seen = true;
                    const RAWKEYBOARD& kb = ri->data.keyboard;
                    const bool down = (kb.Flags & RI_KEY_BREAK) == 0;
                    const uint32_t vk = kb.VKey;
                    const bool consumed = on_key(vk, kb.MakeCode | ((kb.Flags & RI_KEY_E0) ? 0x100u : 0u), down);
                    if ((consumed || cap) && down) {
                        DefWindowProcW(h, m, w, l);  // lets the system clean up the raw input
                        return 0;
                    }
                } else if (ri->header.dwType == RIM_TYPEMOUSE && !cap && hover_capture() &&
                           (ri->data.mouse.usButtonFlags & (RI_MOUSE_LEFT_BUTTON_DOWN | RI_MOUSE_RIGHT_BUTTON_DOWN |
                                                            RI_MOUSE_MIDDLE_BUTTON_DOWN | RI_MOUSE_WHEEL)) &&
                           !(ri->data.mouse.usButtonFlags & (RI_MOUSE_LEFT_BUTTON_UP | RI_MOUSE_RIGHT_BUTTON_UP |
                                                             RI_MOUSE_MIDDLE_BUTTON_UP))) {
                    DefWindowProcW(h, m, w, l);  // a click / wheel over a Kiln panel: not the game's
                    return 0;
                } else if (ri->header.dwType == RIM_TYPEMOUSE && cap) {
                    const RAWMOUSE& ms = ri->data.mouse;
                    // middle and side buttons and wheel notches go to the key subscribers with captured 1 (the Keys
                    // page takes one for a binding); a notch taken there does not scroll the screen too
                    const USHORT taken = (ms.usButtonFlags & kRawButtonFlags) && mouse_presses_wanted()
                                             ? raw_mouse_presses(ms, false) & (RI_MOUSE_WHEEL | RI_MOUSE_HWHEEL)
                                             : 0;
                    // a release the game saw the press of (e.g. the click that opened a Kiln screen) still reaches
                    // it, like key releases: nothing stays held
                    constexpr USHORT kUp = RI_MOUSE_LEFT_BUTTON_UP | RI_MOUSE_RIGHT_BUTTON_UP | RI_MOUSE_MIDDLE_BUTTON_UP |
                                           RI_MOUSE_BUTTON_4_UP | RI_MOUSE_BUTTON_5_UP;
                    constexpr USHORT kDown = RI_MOUSE_LEFT_BUTTON_DOWN | RI_MOUSE_RIGHT_BUTTON_DOWN | RI_MOUSE_MIDDLE_BUTTON_DOWN |
                                             RI_MOUSE_BUTTON_4_DOWN | RI_MOUSE_BUTTON_5_DOWN | RI_MOUSE_WHEEL | RI_MOUSE_HWHEEL;
                    const bool release_only = (ms.usButtonFlags & kUp) && !(ms.usButtonFlags & kDown) && ms.lLastX == 0 && ms.lLastY == 0;
                    g_n_raw.fetch_add(1, std::memory_order_relaxed);
                    if (ms.usFlags & MOUSE_MOVE_ABSOLUTE) g_n_raw_abs.fetch_add(1, std::memory_order_relaxed);
                    const WPARAM pw = static_cast<WPARAM>(static_cast<uint32_t>(ms.lLastX)) |
                                      (static_cast<WPARAM>(static_cast<uint32_t>(ms.lLastY)) << 32);
                    const LPARAM pl = static_cast<LPARAM>(ms.usFlags) |
                                      (static_cast<LPARAM>(static_cast<USHORT>(ms.usButtonFlags & ~taken)) << 16) |
                                      (static_cast<LPARAM>(static_cast<uint16_t>(ms.usButtonData)) << 32);
                    enqueue(h, kRawMouseMsg, pw, pl);
                    if (release_only) break;
                    DefWindowProcW(h, m, w, l);
                    return 0;
                } else if (ri->header.dwType == RIM_TYPEMOUSE && (ri->data.mouse.usButtonFlags & kRawButtonFlags) &&
                           mouse_presses_wanted()) {
                    // play: middle and side buttons and wheel notches as key presses. A record holding only presses a
                    // subscriber took is kept from the game whole, as a key's; one that also holds motion or another
                    // button's or the wheel's input reaches the game with only the taken flags taken out (the import hook)
                    const RAWMOUSE& ms = ri->data.mouse;
                    const USHORT kept = raw_mouse_presses(ms, hover_capture());
                    // a kept press's own release in the same record (a very fast click) needs no game either
                    constexpr USHORT kPresses = RI_MOUSE_MIDDLE_BUTTON_DOWN | RI_MOUSE_BUTTON_4_DOWN | RI_MOUSE_BUTTON_5_DOWN;
                    const USHORT own = kept | static_cast<USHORT>((kept & kPresses) << 1);  // each *_UP is its *_DOWN << 1
                    if (kept) {
                        const bool moved = ms.lLastX != 0 || ms.lLastY != 0 || (ms.usFlags & MOUSE_MOVE_ABSOLUTE);
                        if (!moved && !(ms.usButtonFlags & kRawButtonFlags & ~own)) {
                            DefWindowProcW(h, m, w, l);  // lets the system clean up the raw input
                            return 0;
                        }
                        if (g_game_raw_read_hooked) {
                            const RawEdit outer = t_raw_edit;
                            t_raw_edit = {reinterpret_cast<HRAWINPUT>(l), own};
                            const LRESULT r = CallWindowProcW(g_chain.below, h, m, w, l);
                            t_raw_edit = outer;
                            return r;
                        }
                        if (!g_mixed_record_said) {
                            g_mixed_record_said = true;
                            logf(KILN_LOG_INFO, "ui: a raw mouse record held a press a key binding took and other input "
                                 "(flags 0x%x): with no GetRawInputData import of the game to edit, it reached the game "
                                 "whole (said once)", ms.usButtonFlags);
                        }
                    }
                }
            }
            break;
        }
        case WM_KEYDOWN:
        case WM_SYSKEYDOWN: {
            if (fed) feed(feed_record(KILN_UI_INPUT_WINDOW_KEY, m, w, l));
            const uint32_t vk = static_cast<uint32_t>(w) & 0xFF;
            bool consumed = false;
            if (g_raw_keyboard_seen && !raw_input::registered_for(6, h)) g_raw_keyboard_seen = false;
            if (!g_raw_keyboard_seen) consumed = on_key(vk, (static_cast<uint32_t>(l) >> 16) & 0x1FF, true);
            else consumed = g_consumed[vk];
            enqueue(h, m, w, l);
            if (cap || consumed) {
                if (m == WM_SYSKEYDOWN && vk == VK_F4) break;  // Alt+F4 still closes the game
                return 0;
            }
            break;
        }
        case WM_KEYUP:
        case WM_SYSKEYUP: {
            if (fed) feed(feed_record(KILN_UI_INPUT_WINDOW_KEY, m, w, l));
            const uint32_t vk = static_cast<uint32_t>(w) & 0xFF;
            if (g_raw_keyboard_seen && !raw_input::registered_for(6, h)) g_raw_keyboard_seen = false;
            if (!g_raw_keyboard_seen) on_key(vk, (static_cast<uint32_t>(l) >> 16) & 0x1FF, false);
            enqueue(h, m, w, l);
            break;  // releases always reach the game
        }
        case WM_CHAR:
        case WM_SYSCHAR:
        case WM_UNICHAR:
            enqueue(h, m, w, l);
            if (cap) return 0;
            break;
        case WM_SETFOCUS:
            if (fed) feed_focus_gained();
            enqueue(h, m, w, l);
            break;
        case WM_KILLFOCUS:
            if (fed) feed_focus_lost(m, w, l);
            forget_keys();
            enqueue(h, m, w, l);
            break;
        case WM_DPICHANGED:
            enqueue(h, m, w, l);
            break;
        case WM_ACTIVATEAPP:
            if (fed) {
                if (w) feed_focus_gained();
                else feed_focus_lost(m, w, l);
            }
            if (!w) forget_keys();
            break;
        case WM_SETCURSOR:
            if (cap && LOWORD(l) == HTCLIENT) {
                SetCursor(nullptr);  // ImGui draws the cursor
                return TRUE;
            }
            break;
        default:
            if (is_mouse_msg(m)) {
                if (fed) feed_button_message(m, w, l);
                if (m == WM_MOUSEMOVE) g_n_move.fetch_add(1, std::memory_order_relaxed);
                else if (m != WM_MOUSELEAVE && m != WM_NCMOUSEMOVE) g_n_buttons.fetch_add(1, std::memory_order_relaxed);
                // middle and side buttons and wheel notches as key presses (once with their raw records)
                const bool kept = m != WM_MOUSEMOVE && mouse_presses_wanted() && message_mouse_presses(m, w, !cap && hover_capture());
                const bool wheel = m == WM_MOUSEWHEEL || m == WM_MOUSEHWHEEL;
                if (!(cap && kept && wheel)) enqueue(h, m, w, l);  // a notch the Keys page took does not scroll it too
                if (cap && !is_button_up(m)) return 0;  // releases reach the game (see WM_INPUT)
                if (kept) return (m == WM_XBUTTONDOWN || m == WM_XBUTTONDBLCLK) ? TRUE : 0;  // a key binding took it
                // presses and the wheel over a Kiln panel are Kiln's; releases always reach the game (no stuck drag)
                if ((m == WM_LBUTTONDOWN || m == WM_RBUTTONDOWN || m == WM_MBUTTONDOWN || m == WM_LBUTTONDBLCLK ||
                     m == WM_RBUTTONDBLCLK || m == WM_MOUSEWHEEL || m == WM_MOUSEHWHEEL) && hover_capture())
                    return 0;
            }
            break;
    }
    return CallWindowProcW(g_chain.below, h, m, w, l);
}

BOOL WINAPI hk_set_cursor_pos(int x, int y) {
    if (capturing()) return TRUE;  // the game recentres the cursor for mouse look: not while Kiln has it
    return g_real_set_cursor_pos(x, y);
}

BOOL WINAPI hk_clip_cursor(const RECT* r) {
    if (r) {
        g_game_clip = *r;
        g_game_clipped = true;
    } else {
        g_game_clipped = false;
    }
    if (capturing()) return g_real_clip_cursor(nullptr);
    return g_real_clip_cursor(r);
}

// Replaces the game's import slot user32!<name> with `hook`; returns the previous target.
void* patch_iat(HMODULE mod, const char* dll, const char* name, void* hook) {
    auto* base = reinterpret_cast<uint8_t*>(mod);
    auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    auto* nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
    const auto& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!dir.VirtualAddress) return nullptr;
    for (auto* imp = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + dir.VirtualAddress); imp->Name; ++imp) {
        if (_stricmp(reinterpret_cast<const char*>(base + imp->Name), dll) != 0) continue;
        auto* names = reinterpret_cast<IMAGE_THUNK_DATA64*>(base + (imp->OriginalFirstThunk ? imp->OriginalFirstThunk : imp->FirstThunk));
        auto* slots = reinterpret_cast<IMAGE_THUNK_DATA64*>(base + imp->FirstThunk);
        for (; names->u1.AddressOfData; ++names, ++slots) {
            if (IMAGE_SNAP_BY_ORDINAL64(names->u1.Ordinal)) continue;
            auto* ibn = reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(base + names->u1.AddressOfData);
            if (std::strcmp(reinterpret_cast<const char*>(ibn->Name), name) != 0) continue;
            DWORD old = 0;
            if (!VirtualProtect(&slots->u1.Function, sizeof(void*), PAGE_READWRITE, &old)) return nullptr;
            void* prev = reinterpret_cast<void*>(slots->u1.Function);
            slots->u1.Function = reinterpret_cast<ULONGLONG>(hook);
            VirtualProtect(&slots->u1.Function, sizeof(void*), old, &old);
            return prev;
        }
    }
    return nullptr;
}


// ---- gamepad (XInput) while Kiln has the input ----
// The game loads XInput at run time (XInput1_4/1_3/9_1_0, XInputGetState by name) and polls it. While a Kiln screen or
// the Kiln menu has the input, the game gets an idle pad (no buttons, sticks centred: whatever it held is released),
// and Kiln feeds the pad to ImGui's gamepad navigation (d-pad / left stick move, A activates, LB/RB switch tabs);
// B closes the top Kiln screen like Escape.
struct PadState {
    WORD buttons;
    BYTE lt, rt;
    SHORT lx, ly, rx, ry;
};
struct XState {
    DWORD packet;
    PadState pad;
};
SafetyHookInline g_xinput_hook;
std::mutex g_pad_mu;
PadState g_pad{};
bool g_pad_seen = false;
WORD g_pad_prev[4] = {};
PadState g_pad_fed{};
bool g_pad_fed_any = false;

void synth_escape() {
    KilnUiKey k{};
    k.size = sizeof k;
    k.vk = VK_ESCAPE;
    k.scan = 0x01;
    k.down = 1;
    k.text_input = g_text_input.load() ? 1 : 0;
    k.captured = 1;
    dispatch_key(k);
    k.down = 0;
    dispatch_key(k);
}

DWORD WINAPI hk_xinput_get_state(DWORD user, XState* st) {
    const DWORD r = g_xinput_hook.call<DWORD>(user, st);
    if (r != ERROR_SUCCESS || !st || user >= 4) return r;
    if (!capturing()) {
        g_pad_prev[user] = st->pad.buttons;
        return r;
    }
    const WORD b = st->pad.buttons;
    const bool b_edge = (b & 0x2000) && !(g_pad_prev[user] & 0x2000);  // XINPUT_GAMEPAD_B
    g_pad_prev[user] = b;
    {
        std::lock_guard<std::mutex> lk(g_pad_mu);
        if (b || st->pad.lx > 8000 || st->pad.lx < -8000 || st->pad.ly > 8000 || st->pad.ly < -8000 || !g_pad_seen) g_pad = st->pad;
        g_pad_seen = true;
    }
    std::memset(&st->pad, 0, sizeof st->pad);  // the game sees an idle pad while Kiln has the input
    if (b_edge) synth_escape();
    return r;
}

void feed_pad(ImGuiIO& io) {
    PadState p{};
    {
        std::lock_guard<std::mutex> lk(g_pad_mu);
        if (!g_pad_seen) return;
        p = g_pad;
    }
    io.BackendFlags |= ImGuiBackendFlags_HasGamepad;
    if (g_pad_fed_any && std::memcmp(&p, &g_pad_fed, sizeof p) == 0) return;
    g_pad_fed = p;
    g_pad_fed_any = true;
    auto key = [&](ImGuiKey k, WORD mask) { io.AddKeyEvent(k, (p.buttons & mask) != 0); };
    key(ImGuiKey_GamepadDpadUp, 0x0001);
    key(ImGuiKey_GamepadDpadDown, 0x0002);
    key(ImGuiKey_GamepadDpadLeft, 0x0004);
    key(ImGuiKey_GamepadDpadRight, 0x0008);
    key(ImGuiKey_GamepadStart, 0x0010);
    key(ImGuiKey_GamepadBack, 0x0020);
    key(ImGuiKey_GamepadL1, 0x0100);
    key(ImGuiKey_GamepadR1, 0x0200);
    key(ImGuiKey_GamepadFaceDown, 0x1000);
    key(ImGuiKey_GamepadFaceRight, 0x2000);
    key(ImGuiKey_GamepadFaceLeft, 0x4000);
    key(ImGuiKey_GamepadFaceUp, 0x8000);
    auto axis = [&](ImGuiKey k, float v) {
        const float dz = 8000.0f / 32767.0f;
        const float a = v < dz ? 0.0f : std::min(1.0f, (v - dz) / (1.0f - dz));
        io.AddKeyAnalogEvent(k, a > 0.1f, a);
    };
    const float lx = p.lx / 32767.0f, ly = p.ly / 32767.0f;
    axis(ImGuiKey_GamepadLStickLeft, -lx);
    axis(ImGuiKey_GamepadLStickRight, lx);
    axis(ImGuiKey_GamepadLStickUp, ly);
    axis(ImGuiKey_GamepadLStickDown, -ly);
    io.AddKeyAnalogEvent(ImGuiKey_GamepadL2, p.lt > 30, p.lt / 255.0f);
    io.AddKeyAnalogEvent(ImGuiKey_GamepadR2, p.rt > 30, p.rt / 255.0f);
}

}  // namespace

void input_hook_xinput() {
    // Runs after every library load, on the thread that loaded. Two loads can end at the same moment (they no longer
    // queue behind the LoadLibraryExW hook's mutex), so one thread at a time looks for an XInput library and makes the
    // hook: it is made once and is not replaced while another thread reads it. A thread that finds one at work says
    // "look again" and leaves; the library its own load brought may have come after the other's look, and no later
    // load may follow. Flags, not a mutex: a load made by the working thread itself comes back here and must pass
    // too, and nothing here may wait (the thread can hold the loader lock). The flags keep the default, strictest
    // order: "look again" is written before `busy` is tried and read after `busy` is let go, so one of the two sees it.
    static std::atomic<bool> hooked{false}, again{false};
    static std::atomic_flag busy;
    if (hooked.load()) return;
    again.store(true);
    while (again.load() && !hooked.load()) {
        if (busy.test_and_set()) return;
        if (hooked.load()) {  // made by another thread between this loop's look and the flag: never made twice
            busy.clear();
            return;
        }
        again.store(false);
        for (const wchar_t* dll : {L"xinput1_4.dll", L"xinput1_3.dll", L"xinput9_1_0.dll"}) {
            HMODULE m = GetModuleHandleW(dll);
            if (!m) continue;
            void* fn = reinterpret_cast<void*>(GetProcAddress(m, "XInputGetState"));
            if (!fn) continue;
            g_xinput_hook = safetyhook::create_inline(fn, reinterpret_cast<void*>(&hk_xinput_get_state));
            char name[32];
            std::snprintf(name, sizeof name, "%ls", dll);
            logf(g_xinput_hook ? KILN_LOG_INFO : KILN_LOG_WARN, "ui: gamepad: %s XInputGetState %s", name,
                 g_xinput_hook ? "hooked (idle for the game while Kiln has the input)" : "could not be hooked");
            if (g_xinput_hook) hooked.store(true);  // not hooked: the next load tries again
            break;
        }
        busy.clear();
    }
}

namespace {
}  // namespace

void input_install_iat() {
    HMODULE exe = GetModuleHandleW(nullptr);
    g_real_set_cursor_pos = reinterpret_cast<SetCursorPosFn>(GetProcAddress(GetModuleHandleW(L"user32.dll"), "SetCursorPos"));
    g_real_clip_cursor = reinterpret_cast<ClipCursorFn>(GetProcAddress(GetModuleHandleW(L"user32.dll"), "ClipCursor"));
    void* a = patch_iat(exe, "USER32.dll", "SetCursorPos", reinterpret_cast<void*>(&hk_set_cursor_pos));
    void* b = patch_iat(exe, "USER32.dll", "ClipCursor", reinterpret_cast<void*>(&hk_clip_cursor));
    if (a) g_real_set_cursor_pos = reinterpret_cast<SetCursorPosFn>(a);
    if (b) g_real_clip_cursor = reinterpret_cast<ClipCursorFn>(b);
    // the raw-input read: set before the slot points at the hook (the window thread may call it at once)
    if (!g_game_raw_read)
        g_game_raw_read = reinterpret_cast<GetRawInputDataFn>(GetProcAddress(GetModuleHandleW(L"user32.dll"), "GetRawInputData"));
    void* c = g_game_raw_read ? patch_iat(exe, "USER32.dll", "GetRawInputData", reinterpret_cast<void*>(&hk_get_raw_input_data)) : nullptr;
    if (c) {
        g_game_raw_read = reinterpret_cast<GetRawInputDataFn>(c);
        g_game_raw_read_hooked = true;
    }
    logf(a && b ? KILN_LOG_INFO : KILN_LOG_WARN, "ui: cursor import slots: SetCursorPos %s, ClipCursor %s; GetRawInputData %s",
         a ? "hooked" : "not found", b ? "hooked" : "not found", c ? "hooked (a taken mouse press leaves the record the game reads)" : "not found");
}

namespace {

// `p` belongs to the game's own executable (not to a mod, a system DLL or a thunk handle).
bool games_procedure(WNDPROC p) {
    HMODULE m = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            reinterpret_cast<LPCWSTR>(p), &m))
        return false;
    return m && m == GetModuleHandleW(nullptr);
}

// "<file name>+0x<offset>" of a procedure, for the log.
void describe_procedure(WNDPROC p, char* out, size_t cap) {
    HMODULE m = nullptr;
    wchar_t path[MAX_PATH] = {};
    if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCWSTR>(p), &m) && m && GetModuleFileNameW(m, path, MAX_PATH)) {
        const wchar_t* name = path;
        for (const wchar_t* c = path; *c; ++c)
            if (*c == L'\\' || *c == L'/') name = c + 1;
        char narrow[MAX_PATH] = {};
        WideCharToMultiByte(CP_UTF8, 0, name, -1, narrow, sizeof(narrow) - 1, nullptr, nullptr);
        snprintf(out, cap, "%s+0x%llx", narrow,
                 static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(p) - reinterpret_cast<uintptr_t>(m)));
    } else {
        snprintf(out, cap, "%p", reinterpret_cast<void*>(p));
    }
}

}  // namespace

void input_attach(HWND hwnd) {
    const WNDPROC above_before = g_chain.hwnd == hwnd ? g_chain.above : nullptr;
    switch (chain::attach(g_chain, hwnd, &wndproc, games_procedure)) {
        case chain::Result::Subclassed:
            logf(KILN_LOG_INFO, "ui: game window %p subclassed", reinterpret_cast<void*>(hwnd));
            g_attached = hwnd;
            break;
        case chain::Result::Again:
            logf(KILN_LOG_INFO, "ui: the game set its own window procedure again; game window %p subclassed again",
                 reinterpret_cast<void*>(hwnd));
            g_attached = hwnd;
            break;
        case chain::Result::Unchanged:
            g_attached = hwnd;
            break;
        case chain::Result::Below:
            // Said once per procedure: this is looked at every second.
            if (g_chain.above != above_before) {
                char who[MAX_PATH + 32];
                describe_procedure(g_chain.above, who, sizeof(who));
                logf(KILN_LOG_INFO, "ui: %s subclassed the game window after Kiln; Kiln's procedure stays in the chain below it", who);
            }
            break;
        case chain::Result::Failed: {
            static HWND said_for = nullptr;  // once per window: this is looked at every second
            if (hwnd && IsWindow(hwnd) && g_attached != hwnd && said_for != hwnd) {
                said_for = hwnd;
                logf(KILN_LOG_WARN, "ui: subclassing window %p failed (%lu)", reinterpret_cast<void*>(hwnd), GetLastError());
            }
            break;
        }
    }
}

bool input_attached() { return g_attached != nullptr; }

bool input_key_held(uint32_t vk) { return vk > 0 && vk < 256 && g_down[vk].load(); }

void input_replay() {
    std::deque<Msg> q;
    {
        std::lock_guard<std::mutex> lk(g_qmu);
        q.swap(g_queue);
    }
    ImGuiIO& io = ImGui::GetIO();
    const uint64_t t = now_ms();
    if (!g_legacy_buttons_seen)
        for (const auto& m : q)
            if ((m.msg >= WM_LBUTTONDOWN && m.msg <= WM_MBUTTONDBLCLK) || (m.msg >= WM_XBUTTONDOWN && m.msg <= WM_XBUTTONDBLCLK)) {
                g_legacy_buttons_seen = true;
                logf(KILN_LOG_INFO, "ui: the game window delivers mouse button messages; raw buttons ignored");
                break;
            }
    if (!g_legacy_wheel_seen)
        for (const auto& m : q)
            if (m.msg == WM_MOUSEWHEEL) {
                g_legacy_wheel_seen = true;
                logf(KILN_LOG_INFO, "ui: the game window delivers wheel messages; the raw wheel ignored");
                break;
            }
    for (const auto& m : q) {
        if (m.msg == WM_MOUSEMOVE) g_last_legacy_move = t;
        if (m.msg != kRawMouseMsg) {
            ImGui_ImplWin32_WndProcHandler(m.hwnd, m.msg, m.w, m.l);
            if (m.msg == WM_MOUSEMOVE) {
                g_vx = static_cast<float>(GET_X_LPARAM(m.l));
                g_vy = static_cast<float>(GET_Y_LPARAM(m.l));
            }
            continue;
        }
        const int32_t dx = static_cast<int32_t>(m.w & 0xFFFFFFFF), dy = static_cast<int32_t>(m.w >> 32);
        const uint16_t flags = static_cast<uint16_t>(m.l & 0xFFFF), buttons = static_cast<uint16_t>((m.l >> 16) & 0xFFFF);
        const int16_t data = static_cast<int16_t>((m.l >> 32) & 0xFFFF);
        const float W = io.DisplaySize.x > 0 ? io.DisplaySize.x : 1920, H = io.DisplaySize.y > 0 ? io.DisplaySize.y : 1080;
        if (t - g_last_legacy_move > 250 && (dx || dy || (flags & MOUSE_MOVE_ABSOLUTE))) {
            if (g_vx < 0) {
                g_vx = W / 2;
                g_vy = H / 2;
            }
            if (flags & MOUSE_MOVE_ABSOLUTE) {
                const bool virt = (flags & MOUSE_VIRTUAL_DESKTOP) != 0;
                const int sx = GetSystemMetrics(virt ? SM_XVIRTUALSCREEN : 0), sy = GetSystemMetrics(virt ? SM_YVIRTUALSCREEN : 0);
                const int sw = GetSystemMetrics(virt ? SM_CXVIRTUALSCREEN : SM_CXSCREEN), sh = GetSystemMetrics(virt ? SM_CYVIRTUALSCREEN : SM_CYSCREEN);
                POINT p{sx + static_cast<LONG>(dx / 65535.0 * sw), sy + static_cast<LONG>(dy / 65535.0 * sh)};
                if (ScreenToClient(m.hwnd, &p)) {
                    g_vx = static_cast<float>(p.x);
                    g_vy = static_cast<float>(p.y);
                }
            } else {
                g_vx = std::clamp(g_vx + static_cast<float>(dx), 0.0f, W - 1);
                g_vy = std::clamp(g_vy + static_cast<float>(dy), 0.0f, H - 1);
            }
            io.AddMousePosEvent(g_vx, g_vy);
        }
        if (!g_legacy_buttons_seen) {
            if (buttons & RI_MOUSE_LEFT_BUTTON_DOWN) io.AddMouseButtonEvent(0, true);
            if (buttons & RI_MOUSE_LEFT_BUTTON_UP) io.AddMouseButtonEvent(0, false);
            if (buttons & RI_MOUSE_RIGHT_BUTTON_DOWN) io.AddMouseButtonEvent(1, true);
            if (buttons & RI_MOUSE_RIGHT_BUTTON_UP) io.AddMouseButtonEvent(1, false);
            if (buttons & RI_MOUSE_MIDDLE_BUTTON_DOWN) io.AddMouseButtonEvent(2, true);
            if (buttons & RI_MOUSE_MIDDLE_BUTTON_UP) io.AddMouseButtonEvent(2, false);
        }
        if ((buttons & RI_MOUSE_WHEEL) && !g_legacy_wheel_seen) io.AddMouseWheelEvent(0, static_cast<float>(data) / WHEEL_DELTA);
    }
    if (capturing()) feed_pad(io);
    // one line per capture, 2 s in: which input reaches the overlay (was every 2 s while open)
    static bool capture_was = false, stats_done = false;
    const bool cap = capturing();
    if (cap && !capture_was) {
        g_last_stats = t;
        stats_done = false;
    }
    capture_was = cap;
    if (cap && !stats_done && t - g_last_stats >= 2000) {
        stats_done = true;
        logf(KILN_LOG_INFO, "ui: input while capturing: %llu WM_MOUSEMOVE, %llu button msgs, %llu raw mouse (%llu absolute); cursor (%.0f, %.0f)",
             static_cast<unsigned long long>(g_n_move.load()), static_cast<unsigned long long>(g_n_buttons.load()),
             static_cast<unsigned long long>(g_n_raw.load()), static_cast<unsigned long long>(g_n_raw_abs.load()), io.MousePos.x, io.MousePos.y);
    }
}

void input_capture_changed() {
    const bool cap = capturing();
    if (cap == g_was_capturing) return;
    g_was_capturing = cap;
    if (ImGui::GetCurrentContext()) {
        ImGuiIO& io = ImGui::GetIO();
        io.MouseDrawCursor = cap;
        if (!cap) {
            io.ClearInputKeys();
            g_pad_fed_any = false;
        }
    }
    if (g_real_clip_cursor) {
        if (cap) g_real_clip_cursor(nullptr);
        else if (g_game_clipped) g_real_clip_cursor(&g_game_clip);
    }
    if (cap && g_attached) PostMessageW(g_attached, kShowCursorMsg, 0, 0);  // SetCursor belongs on the window's thread
    logf(KILN_LOG_INFO, "ui: input capture %s", cap ? "on" : "off");
    g_vx = g_vy = -1;
}

KilnResult input_feed_subscribe(KilnOwner owner, KilnUiWindowInputFn fn, void* user, KilnHandle* out) {
    if (!fn) return KILN_ERR_INVALID_ARGUMENT;
    auto sub = std::make_shared<FeedSub>();
    sub->owner = owner;
    sub->fn = fn;
    sub->user = user;
    std::lock_guard lk(g_feed_mu);
    sub->handle = next_handle();
    if (out) *out = sub->handle;
    g_feed_subs.push_back(std::move(sub));
    g_feed_count.store(static_cast<uint32_t>(g_feed_subs.size()), std::memory_order_release);
    return KILN_OK;
}

KilnResult input_feed_unsubscribe(KilnHandle h) {
    std::shared_ptr<FeedSub> removed;
    {
        std::lock_guard lk(g_feed_mu);
        const auto it = std::find_if(g_feed_subs.begin(), g_feed_subs.end(), [&](const auto& s) { return s->handle == h; });
        if (it != g_feed_subs.end()) {
            removed = *it;
            removed->active.store(false, std::memory_order_release);
            g_feed_retiring[h] = removed;
            g_feed_subs.erase(it);
            g_feed_count.store(static_cast<uint32_t>(g_feed_subs.size()), std::memory_order_release);
        } else if (const auto r = g_feed_retiring.find(h); r != g_feed_retiring.end()) {
            removed = r->second;
        }
    }
    if (!removed) return KILN_ERR_NOT_FOUND;
    // A call in flight ends first (never waited for under g_feed_mu: the callback may itself unsubscribe). Inside another
    // kiln.ui callback this only tries, so two callbacks cancelling each other cannot deadlock their threads.
    auto drain = removed->drain(t_callback != nullptr);
    if (!drain.owns_lock()) return KILN_ERR_WRONG_THREAD;
    std::lock_guard lk(g_feed_mu);
    g_feed_retiring.erase(h);
    return KILN_OK;
}

uint32_t input_take_pressed(uint32_t* mods) {
    const uint32_t vk = g_pressed_vk.exchange(0);
    if (mods) *mods = g_pressed_mods.load();
    return vk;
}

void key_name(uint32_t vk, char* out, size_t cap) {
    if (!out || cap == 0) return;
    out[0] = 0;
    static const struct {
        uint32_t vk;
        const char* name;
    } names[] = {{VK_INSERT, "Insert"}, {VK_DELETE, "Delete"}, {VK_HOME, "Home"}, {VK_END, "End"}, {VK_PRIOR, "PageUp"},
                 {VK_NEXT, "PageDown"}, {VK_LEFT, "Left"}, {VK_RIGHT, "Right"}, {VK_UP, "Up"}, {VK_DOWN, "Down"},
                 {VK_RETURN, "Enter"}, {VK_ESCAPE, "Escape"}, {VK_TAB, "Tab"}, {VK_BACK, "Backspace"}, {VK_SPACE, "Space"},
                 {VK_PAUSE, "Pause"}, {VK_SCROLL, "ScrollLock"}, {VK_CAPITAL, "CapsLock"}, {VK_NUMLOCK, "NumLock"},
                 {VK_OEM_3, "Backquote"}, {VK_OEM_MINUS, "Minus"}, {VK_OEM_PLUS, "Equals"}, {VK_OEM_4, "LeftBracket"},
                 {VK_OEM_6, "RightBracket"}, {VK_OEM_5, "Backslash"}, {VK_OEM_1, "Semicolon"}, {VK_OEM_7, "Quote"},
                 {VK_OEM_COMMA, "Comma"}, {VK_OEM_PERIOD, "Period"}, {VK_OEM_2, "Slash"}, {VK_MULTIPLY, "NumpadMultiply"},
                 {VK_ADD, "NumpadAdd"}, {VK_SUBTRACT, "NumpadSubtract"}, {VK_DECIMAL, "NumpadDecimal"},
                 {VK_DIVIDE, "NumpadDivide"}, {VK_LSHIFT, "LeftShift"}, {VK_RSHIFT, "RightShift"},
                 {VK_LCONTROL, "LeftCtrl"}, {VK_RCONTROL, "RightCtrl"}, {VK_LMENU, "LeftAlt"}, {VK_RMENU, "RightAlt"},
                 {VK_SHIFT, "Shift"}, {VK_CONTROL, "Ctrl"}, {VK_MENU, "Alt"}, {VK_XBUTTON1, "Mouse4"}, {VK_XBUTTON2, "Mouse5"},
                 {VK_MBUTTON, "MouseMiddle"}, {KILN_UI_VK_WHEEL_UP, "WheelUp"}, {KILN_UI_VK_WHEEL_DOWN, "WheelDown"},
                 {KILN_UI_VK_WHEEL_LEFT, "WheelLeft"}, {KILN_UI_VK_WHEEL_RIGHT, "WheelRight"}};
    for (const auto& n : names)
        if (n.vk == vk) {
            std::snprintf(out, cap, "%s", n.name);
            return;
        }
    if (vk >= VK_F1 && vk <= VK_F24) std::snprintf(out, cap, "F%u", vk - VK_F1 + 1);
    else if ((vk >= '0' && vk <= '9') || (vk >= 'A' && vk <= 'Z')) std::snprintf(out, cap, "%c", static_cast<char>(vk));
    else if (vk >= VK_NUMPAD0 && vk <= VK_NUMPAD9) std::snprintf(out, cap, "Numpad%u", vk - VK_NUMPAD0);
    else std::snprintf(out, cap, "Key%u", vk);
}

}  // namespace kui
