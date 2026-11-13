// SPDX-License-Identifier: MIT
// kiln.ui: the pause menu in the game's own scroll view, an experiment (menu_scroll.h has the rules and the reasons;
// docs/re/game-ui.md "Layout and scrolling" the evidence). Feature ui.menu_scroll (sigdb client.d/ui.json).
//
// Two mid-function hooks in the pause menu's builder, both on the UI thread inside one builder call:
//  - ui/SystemMenuColumnOpened: the column frame (a vertical stack, the builder's local at rbp+sysmenu.column) is set
//    up and has no child yet. When the menu would run off the screen (menu_scroll::wants), Kiln begins the game's
//    scrolling layout there exactly as the credits do (parameters from ScrollParamsInit, a 4-byte state block on the
//    column's node, ScrollLayoutBegin(layout, 1, state, 0, params), the scroll speed) and opens a vertical stack in it
//    for the menu's own children (aligned and sized as the column's children are).
//  - ui/SystemMenuColumnClosing: right before the builder closes the column. Kiln closes its stack and ends the
//    scroll view begun in the same call.
// There is no jump out of the builder between the two sites and none into the code between them (static analysis of
// 26430625), so every begin meets its end; menu_scroll::Pair enforces it besides.
#include <windows.h>

#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>

#include "kiln_hook.hpp"
#include "menu_scroll.h"
#include "services/kiln_caps.h"
#include "ui.h"

namespace kui {
namespace {

using ScrollBeginFn = void(__fastcall*)(void* layout, uint8_t kind, void* state, void* r9, void* params);
using ScrollEndFn = void(__fastcall*)(void* layout);
using ScrollSpeedFn = float(__fastcall*)(void* layout);
using ScrollSetSpeedFn = void(__fastcall*)(void* layout, float speed);
using ParamsInitFn = void*(__fastcall*)(void* params);
using FrameStateFn = void*(__fastcall*)(void* node, uint64_t hash, uint32_t size, void* ctor, uint32_t flag);
using StateIsNewFn = bool(__fastcall*)(void* state);
using VStackFn = void*(__fastcall*)(void* builder, float spacing);
using FrameEndFn = void(__fastcall*)(void* builder);
using NodeAlignFn = void(__fastcall*)(void* node, const uint16_t* align);
using NodeSizeFn = void(__fastcall*)(void* node, uint64_t size);  // two floats in one register, as the game passes them
using NodeRectFn = const float*(__fastcall*)(void* node);

struct Fns {
    ScrollBeginFn begin = nullptr;
    ScrollEndFn end = nullptr;
    ScrollSpeedFn speed = nullptr;
    ScrollSetSpeedFn set_speed = nullptr;
    ParamsInitFn params_init = nullptr;
    FrameStateFn state = nullptr;
    void* state_nop = nullptr;
    StateIsNewFn state_new = nullptr;
    VStackFn vstack = nullptr;
    FrameEndFn frame_end = nullptr;
    NodeAlignFn align = nullptr;
    NodeSizeFn size = nullptr;
    NodeRectFn rect = nullptr;
    int64_t column = 0, column_node = 0;  // the builder's column (rbp-relative) and its node pointer
} F;

// Kiln's own state-block key on the column's node (the credits use 0xeb56c10a5766ad62 on theirs): FNV-1a 64 of
// "kiln.ui.menu_scroll".
constexpr uint64_t kStateHash = 0x6a1f3c2be08d4e71ull;
constexpr uint16_t kColumnAlign = 0x0201;  // what the builder gives the column (the pattern holds these bytes)

// Storage the game's calls fill (UI thread only; one pause menu at a time). Sizes: the layout writes up to +0x468, the
// parameters are 0x104 bytes, a frame builder 0x23.
alignas(16) uint8_t g_layout[0x800];
alignas(16) uint8_t g_params[0x200];
alignas(16) uint8_t g_inner[0x80];

kiln::MidHook2 g_open_hook, g_close_hook;
std::atomic<bool> g_on{false};
std::atomic<uint32_t> g_status{KILN_UI_SCROLL_UNAVAILABLE};
menu_scroll::Pair g_pair;  // UI thread
bool g_broken = false;     // a begin faulted: nothing more is called on that layout
bool g_marker_written = false;
bool g_scrolled_this_frame = false;
uint32_t g_scrolled_frames = 0;
menu_scroll::Trial g_trial;
std::wstring g_trying, g_off;  // the marker files
std::string g_trying_u8, g_off_u8;

std::wstring widen(const std::string& s) {
    if (s.empty()) return {};
    const int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
    std::wstring w(static_cast<size_t>(n), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), w.data(), n);
    return w;
}

bool read_file(const std::wstring& path, std::string& out) {
    out.clear();
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    char buf[512];
    DWORD n = 0;
    if (ReadFile(h, buf, sizeof buf, &n, nullptr)) out.assign(buf, n);
    CloseHandle(h);
    return true;
}

// Written through to the disk before the call returns: the game may end right after it.
bool write_file(const std::wstring& path, const std::string& text) {
    HANDLE h = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_FLAG_WRITE_THROUGH, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    DWORD n = 0;
    const bool ok = WriteFile(h, text.data(), static_cast<DWORD>(text.size()), &n, nullptr) && n == text.size() &&
                    FlushFileBuffers(h);
    CloseHandle(h);
    return ok;
}

void report(uint32_t state, uint32_t reason, const char* text) {
    const auto* caps = static_cast<const KilnCapsApi*>(g_api->service(KilnStr{KILN_CAPS_SERVICE, sizeof(KILN_CAPS_SERVICE) - 1}, 1));
    if (!caps || !caps->report) return;
    static const char kRow[] = "ui.menu_scroll";
    caps->report(g_owner, KilnStr{kRow, sizeof(kRow) - 1}, state, reason, KilnStr{text, std::strlen(text)});
}

// Off for the rest of the process (the rule "Few" applies again). Said once.
void turn_off(uint32_t status, const char* why) {
    if (!g_on.exchange(false)) return;
    g_status = status;
    logf(KILN_LOG_WARN, "ui: the pause menu no longer scrolls in this game: %s; mods' entries follow the rule again "
                        "(at most 3 in the menu, every one on Kiln's Mods page)", why);
    report(KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_FAULTED, why);
}

uint64_t pack(float a, float b) {
    uint64_t v = 0;
    std::memcpy(&v, &a, 4);
    std::memcpy(reinterpret_cast<uint8_t*>(&v) + 4, &b, 4);
    return v;
}

void rect_of(void* node, float out[4]) {
    out[0] = out[1] = out[2] = out[3] = NAN;
    if (!node) return;
    __try {
        const float* r = F.rect(node);
        for (int i = 0; i < 4; ++i) out[i] = r[i];
    } __except (EXCEPTION_EXECUTE_HANDLER) {
    }
}

bool finite_rect(const float r[4]) {
    for (int i = 0; i < 4; ++i)
        if (!std::isfinite(r[i]) || std::fabs(r[i]) > 100000.0f) return false;
    return true;
}

// The game's calls that begin the scroll view; false when one faulted (an access violation: the game's own error
// handling ends the game and cannot be caught).
bool begin_scroll(uint8_t* column) {
    __try {
        void* node = *reinterpret_cast<void**>(column + (F.column_node - F.column));
        if (!node) return false;
        std::memset(g_params, 0, sizeof g_params);
        F.params_init(g_params);  // the game's defaults: scissor on, auto scrolling on, navigation as the credits have it
        void* state = F.state(node, kStateHash, 4, F.state_nop, 1);
        if (!state) return false;
        if (F.state_new(state)) *static_cast<uint32_t*>(state) = 0;
        std::memset(g_layout, 0, sizeof g_layout);
        F.begin(g_layout, 1, state, nullptr, g_params);
        F.set_speed(g_layout, F.speed(g_layout));
        std::memset(g_inner, 0, sizeof g_inner);
        F.vstack(g_inner, 0.0f);  // the column's own kind: a vertical stack without spacing
        void* inner = *reinterpret_cast<void**>(g_inner + 8);
        if (inner) {
            F.align(inner, &kColumnAlign);
            F.size(inner, pack(1.0f, 0.0f));  // full width, height from the entries
        }
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool end_scroll() {
    __try {
        F.frame_end(g_inner);
        F.end(g_layout);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void on_open(KilnRegisters& r) {
    g_scrolled_this_frame = false;
    if (!g_on.load(std::memory_order_relaxed) || g_broken) return;
    const bool want = menu_scroll::wants(game_menu_mode(), game_pause_mods_rows());
    switch (menu_scroll::at_open(g_pair, want)) {
    case menu_scroll::At::unbalanced:
        turn_off(KILN_UI_SCROLL_OFF_FAULTED, "the pause menu's column opened twice without closing in between (not as "
                                             "the static analysis says)");
        return;
    case menu_scroll::At::nothing:
        return;
    default:
        break;
    }
    if (!g_marker_written) {
        // the crash guard: written and on the disk before the first begin in this process
        KilnBuildInfo bi{};
        bi.size = sizeof bi;
        std::string kiln = "?", build = "?";
        if (g_api->build_info(&bi) == KILN_OK) {
            build = bi.exe_sha256;
            if (bi.kiln_version.ptr) kiln.assign(bi.kiln_version.ptr, bi.kiln_version.len);
        }
        if (!write_file(g_trying, menu_scroll::marker_text(kiln, build))) {
            g_pair.open = false;
            turn_off(KILN_UI_SCROLL_OFF_FAULTED, "its crash guard could not write kiln/state/menu-scroll.trying");
            return;
        }
        g_marker_written = true;
    }
    auto* column = reinterpret_cast<uint8_t*>(r.rbp + static_cast<intptr_t>(F.column));
    if (!begin_scroll(column)) {
        g_broken = true;  // nothing more on this layout, not even its end
        g_pair.open = false;
        turn_off(KILN_UI_SCROLL_OFF_FAULTED, "beginning the game's scroll view faulted");
        return;
    }
    g_scrolled_this_frame = true;
}

void on_close(KilnRegisters& r) {
    if (menu_scroll::at_close(g_pair) != menu_scroll::At::end) return;
    if (!end_scroll()) {
        g_broken = true;
        turn_off(KILN_UI_SCROLL_OFF_FAULTED, "ending the game's scroll view faulted");
        return;
    }
    // the first frames: what the layout says (once the game laid the scroll view out at least once)
    if (++g_scrolled_frames > 8) return;
    auto* column = reinterpret_cast<uint8_t*>(r.rbp + static_cast<intptr_t>(F.column));
    float col[4], inner[4], outer[4];
    rect_of(*reinterpret_cast<void**>(column + (F.column_node - F.column)), col);
    rect_of(*reinterpret_cast<void**>(g_inner + 8), inner);
    rect_of(*reinterpret_cast<void**>(g_layout + 8), outer);
    if (g_scrolled_frames < 3) return;
    if (!finite_rect(col) || !finite_rect(inner) || !finite_rect(outer)) {
        char why[192];
        std::snprintf(why, sizeof why, "the scroll view's layout is not plausible (column h %.1f, view h %.1f, entries h %.1f)",
                      col[3], outer[3], inner[3]);
        turn_off(KILN_UI_SCROLL_OFF_FAULTED, why);
        return;
    }
    if (g_scrolled_frames == 3)
        logf(KILN_LOG_INFO, "ui: the pause menu scrolls: %zu mods' entries; column %.0fx%.0f at (%.0f, %.0f), scroll view "
                            "%.0fx%.0f, entries %.0fx%.0f (UI units; entries taller than the view = it scrolls)",
             game_pause_mods_rows(), col[2], col[3], col[0], col[1], outer[2], outer[3], inner[2], inner[3]);
}

bool sym(const char* n, uintptr_t* out) { return g_api->symbol(KilnStr{n, std::strlen(n)}, out) == KILN_OK && *out; }
template <typename T>
bool fn(const char* n, T* out) {
    uintptr_t a = 0;
    if (!sym(n, &a)) return false;
    *out = reinterpret_cast<T>(a);
    return true;
}
bool cst(const char* n, int64_t* out) { return g_api->constant(KilnStr{n, std::strlen(n)}, out) == KILN_OK; }

std::string state_dir() {
    char b[1024];
    size_t n = 0;
    if (g_api->path(KILN_PATH_STATE, b, sizeof b, &n) != KILN_OK || n == 0 || n >= sizeof b) return {};
    return std::string(b, n);
}

}  // namespace

bool scroll_install(bool system_menu_hooked) {
    static const char kFeature[] = "ui.menu_scroll";
    if (!system_menu_hooked) {
        logf(KILN_LOG_INFO, "ui: the pause menu cannot scroll (Kiln's pause-menu entries are off on this build)");
        return false;
    }
    if (!g_api->feature(KilnStr{kFeature, sizeof(kFeature) - 1})) {
        logf(KILN_LOG_INFO, "ui: the pause menu cannot scroll on this game build (feature ui.menu_scroll is off: a "
                            "symbol did not resolve)");
        return false;
    }
    uintptr_t open_site = 0, close_site = 0;
    const bool ok = sym("ui/SystemMenuColumnOpened", &open_site) && sym("ui/SystemMenuColumnClosing", &close_site) &&
                    fn("ui/ScrollLayoutBegin", &F.begin) && fn("ui/ScrollLayoutEnd", &F.end) &&
                    fn("ui/ScrollLayoutSpeed", &F.speed) && fn("ui/ScrollLayoutSetSpeed", &F.set_speed) &&
                    fn("ui/ScrollParamsInit", &F.params_init) && fn("ui/UiFrameState", &F.state) &&
                    fn("ui/UiStateNop", &F.state_nop) && fn("ui/UiStateIsNew", &F.state_new) &&
                    fn("ui/UiVStack", &F.vstack) && fn("ui/UiFrameEnd", &F.frame_end) && fn("ui/UiNodeAlign", &F.align) &&
                    fn("ui/UiNodeSize", &F.size) && fn("ui/UiNodeRect", &F.rect) && cst("ui/sysmenu.column", &F.column) &&
                    cst("ui/sysmenu.column_node", &F.column_node);
    // the constants are rbp-relative bytes of the game's code (int8): the column's builder and its node pointer at +8
    if (ok) {
        F.column = static_cast<int8_t>(F.column & 0xff);
        F.column_node = static_cast<int8_t>(F.column_node & 0xff);
    }
    if (!ok || F.column_node - F.column != 8) {
        logf(KILN_LOG_WARN, "ui: the pause menu cannot scroll: a name of feature ui.menu_scroll did not resolve, or the "
                            "column's layout is not as analysed");
        report(KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_SYMBOL_MISSING, "a symbol or constant did not resolve as analysed");
        return false;
    }
    // the crash guard: kiln/state/menu-scroll.trying (this start tries), menu-scroll.off (an earlier try ended the game)
    const std::string dir = state_dir();
    if (dir.empty()) {
        logf(KILN_LOG_WARN, "ui: the pause menu does not scroll: no kiln/state folder for its crash guard");
        report(KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_FAULTED, "no kiln/state folder for the crash guard");
        return false;
    }
    g_trying_u8 = dir + "\\menu-scroll.trying";
    g_off_u8 = dir + "\\menu-scroll.off";
    g_trying = widen(g_trying_u8);
    g_off = widen(g_off_u8);
    KilnBuildInfo bi{};
    bi.size = sizeof bi;
    std::string kiln = "?", build = "?";
    if (g_api->build_info(&bi) == KILN_OK) {
        build = bi.exe_sha256;
        if (bi.kiln_version.ptr) kiln.assign(bi.kiln_version.ptr, bi.kiln_version.len);
    }
    std::string trying_text, off_text;
    const bool trying = read_file(g_trying, trying_text);
    const bool off = read_file(g_off, off_text);
    switch (menu_scroll::decide(trying, off ? &off_text : nullptr, kiln, build)) {
    case menu_scroll::Start::off_after_crash: {
        // the game ended while an earlier start tried it: keep its version and build as the off record
        if (!MoveFileExW(g_trying.c_str(), g_off.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
            write_file(g_off, trying_text);
            DeleteFileW(g_trying.c_str());
        }
        g_status = KILN_UI_SCROLL_OFF_CRASHED;
        logf(KILN_LOG_WARN, "ui: the pause menu does not scroll: the game ended while an earlier start tried it, so it "
                            "stays off with this Kiln version and game build (delete %s to try again)", g_off_u8.c_str());
        report(KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_FAULTED, "the game ended while an earlier start tried it (kiln/state/menu-scroll.off)");
        return false;
    }
    case menu_scroll::Start::off_recorded:
        g_status = KILN_UI_SCROLL_OFF_RECORDED;
        logf(KILN_LOG_INFO, "ui: the pause menu does not scroll: it ended the game before with this Kiln version and game "
                            "build (delete %s to try again)", g_off_u8.c_str());
        report(KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_FAULTED, "it ended the game before with this Kiln version and game build (kiln/state/menu-scroll.off)");
        return false;
    case menu_scroll::Start::try_it:
        if (off) DeleteFileW(g_off.c_str());  // another Kiln version or game build: one more try
        break;
    }
    g_open_hook.install(g_api, g_owner, open_site, &on_open, "ui/SystemMenuColumnOpened");
    g_close_hook.install(g_api, g_owner, close_site, &on_close, "ui/SystemMenuColumnClosing");
    if (!g_open_hook || !g_close_hook) {
        g_open_hook = {};
        g_close_hook = {};
        logf(KILN_LOG_WARN, "ui: the pause menu cannot scroll: its hooks could not be installed");
        report(KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_FAULTED, "its hooks could not be installed");
        return false;
    }
    g_status = KILN_UI_SCROLL_ON;
    g_on = true;
    logf(KILN_LOG_INFO, "ui: the pause menu can scroll (experiment): it does once it has more than %u mods' entries; "
                        "crash guard %s", menu_rows::kFew, g_trying_u8.c_str());
    return true;
}

uint32_t scroll_status() { return g_status.load(); }
bool scroll_on() { return g_on.load(std::memory_order_relaxed); }

void scroll_after_frame(uint64_t frame) {
    if (!g_marker_written) return;
    const bool scrolled = g_scrolled_this_frame;
    g_scrolled_this_frame = false;
    if (menu_scroll::trial_step(g_trial, frame, GetTickCount64(), scrolled)) {
        if (DeleteFileW(g_trying.c_str()) || GetLastError() == ERROR_FILE_NOT_FOUND)
            logf(KILN_LOG_INFO, "ui: the pause menu scrolled for a few seconds and closed again: its crash guard is "
                                "cleared for this start");
    }
}

}  // namespace kui
