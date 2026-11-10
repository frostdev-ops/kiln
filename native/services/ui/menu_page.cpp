// SPDX-License-Identifier: MIT
// kiln.ui: the Kiln menu as a page of the game's main menu (feature ui.menu_page; docs/re/game-ui.md "Main-menu
// pages", docs/kiln/ui.md "Two hosts"). Static analysis of client 26430625; nothing here has been seen running yet.
//
// The game's main menu (PreGame) has pages: MainMenu 3, Settings 5, Credits 6, PlaySelection 8, ... The current page is
// an int in the PreGame state block (+pregame.page); PreGameGoTo(pregame, page) only compares and writes it. Each
// UI frame the PreGame frame calls the page's draw function from a jump table; pages 5, 6, 8 and 10 make the
// background camera glide to the close-up shot. Credits is `bool Credits(void)`: true = stay, false = the game itself
// calls GoTo(pregame, MainMenu) (0xc6aa9b-0xc6aaab), and the camera glides back.
//
//  - Open: Kiln's own MODS entry, drawn by game.cpp's MainMenuEntry hook at the Settings call site, returned true.
//    There, as the game does for its own entries right after the entry renderer returns, Kiln reads the current page
//    of pregame = entry state - pregame.main_menu (the entry's first argument is pregame+8 at those call sites) and, when
//    it is MainMenu, calls PreGameGoTo(pregame, Credits). Inside the hook is the only place the PreGame block is
//    known to be alive: the UI's state store frees blocks a frame does not touch, so the pointer is never kept, and
//    the after-frame queue (outside GameUiFrame, with the UI context gone) is not used for it.
//  - Show: the Credits hook. Called from the PreGame page switch (return address ui/PreGameCreditsRet) while Kiln's
//    page is open, it does not call the game's Credits: it notes the frame and returns true (stay) or, once Kiln asked
//    to leave, false (the game goes back to MainMenu). Any other call (the real Credits entry, the end-game credits,
//    another caller, Kiln's page closed) runs the game's Credits untouched.
//  - The game leaving by itself (an invite moves the page to MainMenu directly): the hook stops being called; after
//    3 UI frames without it the page counts as hidden. Nothing is written into the game.
//  - Watchdog: when no host drew the page for 30 UI frames and 1 s, the hook returns false (leave) and the game page is
//    given up for the session (the MODS entry opens the stand-alone Kiln menu again).
// Threads: open/show/after-frame on the game's UI thread; leave/escape/menu key on the window thread; state, drawn on
// the render thread. Shared state is atomics; no lock is held across a call into the game.
#include <windows.h>

#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>

#include "kiln_hook.hpp"
#include "ui.h"

namespace kui {

std::atomic<bool> g_page_capture{false};

namespace {

constexpr uint64_t kGoneFrames = 3;        // UI frames without the Credits hook: the game left the page
constexpr uint64_t kOpenFrames = 3;        // UI frames for the page to appear after GoTo
constexpr uint64_t kKeyFrames = 3;         // UI frames for the main menu to take a menu-key request
constexpr uint64_t kWatchdogFrames = 30;   // UI frames without a host drawing the page ...
constexpr uint64_t kWatchdogMs = 1000;     // ... and this long: leave
// The game's PlaySelection page padding (FbUiPreGame.playMenusPadding, 0xd41360): left/right 80, top/bottom 50 UI
// units. Its value lives in the UI bundle; the bundle field is not reached from here, so these are the build's values.
constexpr float kPadX = 80.0f, kPadY = 50.0f;

struct PageLayout {
    uintptr_t go_to = 0, credits = 0, credits_ret = 0;
    int64_t page = 0, page_credits = 0, page_main_menu = 0, main_menu = 0;
    int64_t frame_arg = 0, canvas_w = 0, canvas_h = 0, viewport = 0, ppu = 0;  // system offsets (frame_arg: sys10 = system + it)
    int64_t popups = 0, popup_error = 0;                                      // sys10 + popups + popup_error: u8
    uintptr_t ui_app = 0, play_sound = 0;
    int64_t ui_config = 0, sound_cancel = 0;
    bool on = false, popup = false, sound = false;
} P;

kiln::RawHook g_credits_hook;
using GoToFn = void(__fastcall*)(uintptr_t pregame, int32_t page);
using PlaySoundFn = void(__fastcall*)(uintptr_t ctx, const void* sound);

std::atomic<uint32_t> g_phase{KILN_UI_PAGE_HIDDEN};
std::atomic<uint32_t> g_by{0};
std::atomic<bool> g_host{false}, g_broken{false}, g_leave{false};
std::atomic<bool> g_popup{false};          // the game's error popup shows over the page: the game gets the input
std::atomic<uint64_t> g_ui_frame{0};       // UI frames finished (page_after_frame)
std::atomic<uint64_t> g_open_frame{0}, g_shown_frame{0}, g_drawn_frame{0}, g_drawn_ms{0};
std::atomic<uint64_t> g_main_seen{0};      // the UI frame the main menu last drew its Settings entry
std::atomic<uint64_t> g_key_req{0};        // a menu-key request, the UI frame it was made in (0 = none)
std::atomic<uint64_t> g_opens{0}, g_leaves{0}, g_gone{0}, g_watchdogs{0}, g_refused{0};

// The canvas metrics and the popup flag, written by the UI thread, read by any (seqlock over atomic words).
struct Metrics {
    float canvas_w, canvas_h, vx, vy, vw, vh, ppu;
    uint32_t flags;  // 1 metrics valid, 2 error popup
};
static_assert(sizeof(Metrics) == 32);
std::atomic<uint32_t> g_seq{0};
std::atomic<uint32_t> g_words[8];

void publish(const Metrics& m) {
    uint32_t w[8];
    std::memcpy(w, &m, sizeof w);
    const uint32_t s = g_seq.load(std::memory_order_relaxed);
    g_seq.store(s + 1, std::memory_order_relaxed);
    std::atomic_thread_fence(std::memory_order_release);
    for (int i = 0; i < 8; ++i) g_words[i].store(w[i], std::memory_order_relaxed);
    g_seq.store(s + 2, std::memory_order_release);
}

bool snapshot(Metrics* out) {
    for (int tries = 0; tries < 8; ++tries) {
        const uint32_t s1 = g_seq.load(std::memory_order_acquire);
        if (s1 & 1) continue;
        uint32_t w[8];
        for (int i = 0; i < 8; ++i) w[i] = g_words[i].load(std::memory_order_relaxed);
        std::atomic_thread_fence(std::memory_order_acquire);
        if (g_seq.load(std::memory_order_relaxed) != s1) continue;
        std::memcpy(out, w, sizeof w);
        return true;
    }
    return false;
}

// ---- guarded game memory and calls (no C++ objects in these frames: __try) ----
template <typename T>
bool rd(uintptr_t a, T& out) {
    if (a < 0x10000) return false;
    __try {
        out = *reinterpret_cast<const T*>(a);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool call_go_to(uintptr_t fn, uintptr_t pregame, int32_t page) {
    __try {
        reinterpret_cast<GoToFn>(fn)(pregame, page);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// UiPlaySound(*UiApp, &sound): the game's back sound, as its PlaySelection page plays it (0xc5e4c5). The 16-byte sound
// reference is copied from the UI config first, as the game does. Only inside a UI frame (UiApp is current then).
bool play_cancel_sound() {
    if (!P.sound) return false;
    uintptr_t ctx = 0, cfg = 0;
    if (!rd(P.ui_app, ctx) || !ctx || !rd(ctx + P.ui_config, cfg) || !cfg) return false;
    struct alignas(16) Sound {
        uint64_t a, b;
    } sound{};
    if (!rd(cfg + P.sound_cancel, sound)) return false;
    __try {
        reinterpret_cast<PlaySoundFn>(P.play_sound)(ctx, &sound);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

const char* by_name(uint32_t by) { return by == KILN_UI_PAGE_BY_KEY ? "the menu key" : "the MODS entry"; }

void close_page(const char* why) {
    g_phase.store(KILN_UI_PAGE_HIDDEN);
    g_page_capture.store(false);
    g_popup.store(false);
    g_leave.store(false);
    logf(KILN_LOG_INFO, "ui: Kiln page in the main menu closed (%s)", why);
}

bool usable() {
    return P.on && g_host.load(std::memory_order_relaxed) && !g_broken.load(std::memory_order_relaxed);
}

// Asks the game for its Credits page slot. UI thread, inside the main menu's draw (the entry hook), with the entry's
// first argument. False: nothing changed (the stand-alone menu opens instead).
bool open_now(void* entry_state, uint32_t by) {
    if (!usable() || g_phase.load() != KILN_UI_PAGE_HIDDEN || !entry_state) return false;
    const uintptr_t pregame = reinterpret_cast<uintptr_t>(entry_state) - static_cast<uintptr_t>(P.main_menu);
    int32_t page = -1;
    if (!rd(pregame + P.page, page) || page != static_cast<int32_t>(P.page_main_menu)) {
        g_refused.fetch_add(1, std::memory_order_relaxed);
        logf(KILN_LOG_WARN, "ui: the main menu is on page %d, not %lld: the Kiln menu opens over it instead", page,
             static_cast<long long>(P.page_main_menu));
        return false;
    }
    const uint64_t f = g_ui_frame.load() + 1;  // the frame being built
    g_leave.store(false);
    g_open_frame.store(f);
    g_shown_frame.store(0);
    g_drawn_frame.store(f);
    g_drawn_ms.store(now_ms());
    g_by.store(by);
    g_phase.store(KILN_UI_PAGE_OPENING);
    g_page_capture.store(true);
    if (!call_go_to(P.go_to, pregame, static_cast<int32_t>(P.page_credits))) {
        g_phase.store(KILN_UI_PAGE_HIDDEN);
        g_page_capture.store(false);
        g_broken.store(true);
        logf(KILN_LOG_ERROR, "ui: the game's page switch faulted: the Kiln menu opens over the main menu from now on");
        return false;
    }
    g_menu_open.store(false);  // one host at a time
    g_opens.fetch_add(1, std::memory_order_relaxed);
    logf(KILN_LOG_INFO, "ui: Kiln page in the main menu opened by %s (UI frame %llu)", by_name(by), static_cast<unsigned long long>(f));
    return true;
}

bool __fastcall hk_credits() {
    const uintptr_t r = g_credits_hook.caller();  // the game's call site, read before the original
    const uint32_t ph = g_phase.load();
    if (ph == KILN_UI_PAGE_HIDDEN || r != P.credits_ret) return g_credits_hook.call<bool>();
    const uint64_t f = g_ui_frame.load() + 1;
    if (g_leave.exchange(false)) {
        play_cancel_sound();
        g_leaves.fetch_add(1, std::memory_order_relaxed);
        close_page("left; the game goes back to its main menu");
        return false;
    }
    // (not while the game's error popup has the input: the overlay may draw nothing then)
    if (ph == KILN_UI_PAGE_SHOWN && !g_popup.load() && f > g_drawn_frame.load() + kWatchdogFrames &&
        now_ms() > g_drawn_ms.load() + kWatchdogMs) {
        g_broken.store(true);
        g_watchdogs.fetch_add(1, std::memory_order_relaxed);
        close_page("no host drew it for a while: back to the game's main menu; the MODS entry opens the Kiln menu over it from now on");
        return false;
    }
    if (ph == KILN_UI_PAGE_OPENING) {
        g_drawn_frame.store(f);  // the host's grace starts when the page appears
        g_drawn_ms.store(now_ms());
        g_phase.store(KILN_UI_PAGE_SHOWN);
    }
    g_shown_frame.store(f);
    return true;
}

bool sym(const char* n, uintptr_t* out) { return g_api->symbol(KilnStr{n, std::strlen(n)}, out) == KILN_OK && *out; }
bool cst(const char* n, int64_t* out) { return g_api->constant(KilnStr{n, std::strlen(n)}, out) == KILN_OK; }
bool feat(const char* n) { return g_api->feature(KilnStr{n, std::strlen(n)}); }

bool plausible(const Metrics& m) {
    auto in = [](float v, float lo, float hi) { return std::isfinite(v) && v >= lo && v <= hi; };
    return in(m.ppu, 0.05f, 50.0f) && in(m.canvas_w, 100.0f, 100000.0f) && in(m.canvas_h, 100.0f, 100000.0f) &&
           in(m.vw, 1.0f, 65536.0f) && in(m.vh, 1.0f, 65536.0f) && in(m.vx, -65536.0f, 65536.0f) && in(m.vy, -65536.0f, 65536.0f);
}

}  // namespace

bool page_install(bool main_menu_hooked) {
    P = PageLayout{};
    if (!main_menu_hooked) return false;
    P.on = feat("ui.menu_page") && sym("ui/PreGameGoTo", &P.go_to) && sym("ui/PreGameCredits", &P.credits) &&
           sym("ui/PreGameCreditsRet", &P.credits_ret) && cst("ui/pregame.page", &P.page) &&
           cst("ui/pregame.page_credits", &P.page_credits) && cst("ui/pregame.page_main_menu", &P.page_main_menu) &&
           cst("ui/pregame.main_menu", &P.main_menu) && cst("ui/sys.frame_arg", &P.frame_arg) &&
           cst("ui/sys.canvas_w", &P.canvas_w) && cst("ui/sys.canvas_h", &P.canvas_h) && cst("ui/sys.viewport", &P.viewport) &&
           cst("ui/sys.px_per_unit", &P.ppu);
    // the values the code relies on: an int page in the block, small page ids, the entry state inside the block
    if (P.on && (P.page <= 0 || P.page > 0x10000 || P.page_credits < 0 || P.page_credits > 64 || P.page_main_menu < 0 ||
                 P.page_main_menu > 64 || P.page_credits == P.page_main_menu || P.main_menu < 0 || P.main_menu > 0x1000)) {
        logf(KILN_LOG_WARN, "ui: ui.menu_page constants look wrong on this build: the Kiln menu opens over the main menu");
        P.on = false;
    }
    if (!P.on) {
        logf(KILN_LOG_INFO, "ui: main-menu page unavailable on this build (feature ui.menu_page off): the Kiln menu opens over the main menu");
        return false;
    }
    g_credits_hook.install(g_api, g_owner, P.credits, reinterpret_cast<void*>(&hk_credits), "ui/PreGameCredits");
    if (!g_credits_hook) {
        logf(KILN_LOG_WARN, "ui: hooking the game's Credits page failed (%s): the Kiln menu opens over the main menu",
             g_credits_hook.error() ? g_credits_hook.error() : "?");
        P.on = false;
        return false;
    }
    P.popup = feat("ui.error_popup") && cst("ui/sys.popups", &P.popups) && cst("ui/popups.error", &P.popup_error);
    P.sound = feat("ui.menu_page_sound") && sym("ui/UiApp", &P.ui_app) && sym("ui/UiPlaySound", &P.play_sound) &&
              cst("ui/ui.config", &P.ui_config) && cst("ui/cfg.sound_entry_cancel", &P.sound_cancel);
    logf(KILN_LOG_INFO, "ui: main-menu page on (Credits slot, page %lld; back sound %s, error popup %s)",
         static_cast<long long>(P.page_credits), P.sound ? "on" : "off", P.popup ? "on" : "off");
    return true;
}

bool page_usable() { return usable(); }
bool page_active() { return g_phase.load(std::memory_order_relaxed) != KILN_UI_PAGE_HIDDEN; }

bool page_open_from_entry(void* entry_state) { return open_now(entry_state, KILN_UI_PAGE_BY_ENTRY); }

void page_main_menu_drawn(void* entry_state) {
    if (!P.on) return;
    g_main_seen.store(g_ui_frame.load(std::memory_order_relaxed) + 1, std::memory_order_relaxed);
    if (g_key_req.load(std::memory_order_relaxed) && g_key_req.exchange(0)) {
        if (!open_now(entry_state, KILN_UI_PAGE_BY_KEY)) g_menu_open.store(true);  // as the key did before
    }
}

void page_after_frame(uintptr_t sys10, uint64_t frame) {
    g_ui_frame.store(frame);
    if (!P.on) return;
    const uint32_t ph = g_phase.load();
    if (ph == KILN_UI_PAGE_HIDDEN) {
        const uint64_t k = g_key_req.load(std::memory_order_relaxed);
        if (k && frame >= k + kKeyFrames && g_key_req.exchange(0)) g_menu_open.store(true);  // the main menu did not take it
        return;
    }
    if (ph == KILN_UI_PAGE_OPENING && frame >= g_open_frame.load() + kOpenFrames) {
        g_gone.fetch_add(1, std::memory_order_relaxed);
        close_page("the game did not show it");
        return;
    }
    if (ph == KILN_UI_PAGE_SHOWN && frame >= g_shown_frame.load() + kGoneFrames) {
        g_gone.fetch_add(1, std::memory_order_relaxed);
        close_page("the game left the page by itself");
        return;
    }
    // the page is up: the game's canvas, live (the window may be resized while it shows)
    Metrics m{};
    const uintptr_t sys = sys10 - static_cast<uintptr_t>(P.frame_arg);
    struct Viewport {
        float x, y, w, h;
    } vp{};
    const bool ok = sys10 && rd(sys + P.canvas_w, m.canvas_w) && rd(sys + P.canvas_h, m.canvas_h) && rd(sys + P.ppu, m.ppu) &&
                    rd(sys + P.viewport, vp);
    m.vx = vp.x, m.vy = vp.y, m.vw = vp.w, m.vh = vp.h;
    if (ok && plausible(m)) m.flags |= 1;
    uint8_t popup = 0;
    if (P.popup && sys10 && rd(sys10 + P.popups + P.popup_error, popup) && popup) m.flags |= 2;
    publish(m);
    // while the game's error popup shows, the page steps aside: the game gets the mouse, the keys and the pad
    const bool up = (m.flags & 2) != 0;
    if (g_popup.exchange(up) != up) {
        g_page_capture.store(!up);
        g_drawn_frame.store(frame);  // the host's grace starts again
        g_drawn_ms.store(now_ms());
        logf(KILN_LOG_INFO, "ui: the game's error popup %s the Kiln page", up ? "shows over" : "is gone from");
    }
}

bool page_menu_key() {
    if (page_active()) {
        page_leave();
        return true;
    }
    if (!usable() || g_menu_open.load()) return false;
    const uint64_t f = g_ui_frame.load();
    if (g_main_seen.load() + 1 < f || f == 0) return false;  // the main menu's entries were not drawn last frame
    g_key_req.store(f + 1);
    return true;
}

bool page_escape() {
    if (!page_active() || g_popup.load()) return false;  // Escape is the popup's
    page_leave();
    return true;
}

void page_leave() {
    if (page_active()) g_leave.store(true);
}

void page_drawn() {
    if (!page_active()) return;
    g_drawn_frame.store(g_ui_frame.load(std::memory_order_relaxed), std::memory_order_relaxed);
    g_drawn_ms.store(now_ms(), std::memory_order_relaxed);
}

void page_host(uint32_t on) {
    g_host.store(on != 0);
    if (!on) page_leave();
}

KilnResult page_state(KilnUiMenuPage* out) {
    if (!out || out->size < sizeof(KilnUiMenuPage)) return KILN_ERR_INVALID_ARGUMENT;
    std::memset(out, 0, sizeof *out);
    out->size = sizeof *out;
    out->ui_frame = g_ui_frame.load(std::memory_order_relaxed);
    if (!P.on) return KILN_ERR_UNAVAILABLE;
    out->available = usable() ? 1 : 0;
    out->state = static_cast<uint8_t>(g_phase.load());
    out->opened_by = static_cast<uint8_t>(g_by.load(std::memory_order_relaxed));
    out->opened_frame = g_open_frame.load(std::memory_order_relaxed);
    out->shown_frame = g_shown_frame.load(std::memory_order_relaxed);
    out->pad_left = out->pad_right = kPadX;
    out->pad_top = out->pad_bottom = kPadY;
    Metrics m{};
    if (out->state != KILN_UI_PAGE_HIDDEN && snapshot(&m)) {
        out->error_popup = (m.flags & 2) ? 1 : 0;
        if (m.flags & 1) {
            out->metrics = 1;
            out->canvas_width = m.canvas_w;
            out->canvas_height = m.canvas_h;
            out->viewport_x = m.vx;
            out->viewport_y = m.vy;
            out->viewport_width = m.vw;
            out->viewport_height = m.vh;
            out->pixels_per_unit = m.ppu;
            out->x0 = m.vx + kPadX * m.ppu;
            out->y0 = m.vy + kPadY * m.ppu;
            out->x1 = m.vx + (m.canvas_w - kPadX) * m.ppu;
            out->y1 = m.vy + (m.canvas_h - kPadY) * m.ppu;
        }
    }
    return KILN_OK;
}

void page_status_line(char* out, size_t cap) {
    static const char* names[] = {"hidden", "opening", "shown"};
    const uint32_t ph = g_phase.load();
    std::snprintf(out, cap, "main-menu page %s%s%s, %s; opened %llu, left %llu, left by the game %llu, watchdog %llu, refused %llu",
                  P.on ? "on" : "off", P.on && !g_host.load() ? " (no host)" : "", g_broken.load() ? " (given up)" : "",
                  ph < 3 ? names[ph] : "?", static_cast<unsigned long long>(g_opens.load()),
                  static_cast<unsigned long long>(g_leaves.load()), static_cast<unsigned long long>(g_gone.load()),
                  static_cast<unsigned long long>(g_watchdogs.load()), static_cast<unsigned long long>(g_refused.load()));
}

#ifdef KILN_UI_PAGE_TESTS
// host tests: back to a fresh process state
void page_reset_for_tests() {
    g_phase = KILN_UI_PAGE_HIDDEN;
    g_page_capture = false;
    g_host = g_broken = g_leave = g_popup = false;
    g_ui_frame = g_open_frame = g_shown_frame = g_drawn_frame = g_drawn_ms = g_main_seen = g_key_req = 0;
    g_by = 0;
    g_seq = 0;
    for (auto& w : g_words) w = 0;
    g_credits_hook.revoke();
}
#endif

}  // namespace kui
