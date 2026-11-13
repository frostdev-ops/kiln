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
bool g_broken = false;     // a step faulted: no more begins in this process
menu_scroll::Nest g_nest;  // what the current builder call's begin opened (UI thread)
bool g_marker_written = false;
std::atomic<bool> g_clear_marker{false};  // the trial passed: after_frame removes the marker
uint32_t g_scrolled_frames = 0;
menu_scroll::Trial g_trial;
std::wstring g_trying, g_off;  // the marker files
std::wstring g_gave_up;       // why Kiln ended the game on purpose (give_up), for the next start and the player
std::string g_trying_u8, g_off_u8;

std::wstring widen(const std::string& s) {
    if (s.empty()) return {};
    const int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
    std::wstring w(static_cast<size_t>(n), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), w.data(), n);
    return w;
}

// Absent only when the file (or its folder) is not there; any other failure counts as there (menu_scroll::File).
menu_scroll::File read_file(const std::wstring& path, std::string& out) {
    out.clear();
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
    if (h == INVALID_HANDLE_VALUE) {
        const DWORD e = GetLastError();
        return e == ERROR_FILE_NOT_FOUND || e == ERROR_PATH_NOT_FOUND ? menu_scroll::File::absent : menu_scroll::File::unreadable;
    }
    char buf[512];
    DWORD n = 0;
    const bool ok = ReadFile(h, buf, sizeof buf, &n, nullptr);
    if (ok) out.assign(buf, n);
    CloseHandle(h);
    return ok ? menu_scroll::File::read : menu_scroll::File::unreadable;
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

// Nesting that cannot be restored: the game would fail on its column a moment later anyway (its layout errors are
// fatal and cannot be caught). End the process now, said in kiln.log; the crash guard's marker is on the disk, so the
// next start keeps scrolling off.
// Why, where the next start and the player find it: kiln/state/menu-scroll.gave-up, written through to the disk by this
// thread with no allocation (the path is made at install). kiln.log's writer is another thread that dies with the
// process, so the line given to it may never reach the file.
void say_on_disk(const char* what) noexcept {
    try {
        if (g_gave_up.empty()) return;
        HANDLE h = CreateFileW(g_gave_up.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_FLAG_WRITE_THROUGH, nullptr);
        if (h == INVALID_HANDLE_VALUE) return;
        static const char kHead[] = "Kiln ended the game on purpose: the scrolling pause menu ";
        static const char kTail[] = " and the game's menu could not be put back in order. Scrolling stays off from the next "
                                    "start (menu-scroll.off).\r\n";
        DWORD n = 0;
        WriteFile(h, kHead, sizeof kHead - 1, &n, nullptr);
        WriteFile(h, what, static_cast<DWORD>(std::strlen(what)), &n, nullptr);
        WriteFile(h, kTail, sizeof kTail - 1, &n, nullptr);
        FlushFileBuffers(h);
        CloseHandle(h);
    } catch (...) {  // /EHa: a fault here too. Nothing may keep the process from ending below
    }
}

// The same in kiln.log when its writer still works: the line is queued, and the writer gets a moment to put it out.
void say_in_log(const char* what) noexcept {
    try {
        logf(KILN_LOG_ERROR, "ui: the scrolling pause menu %s and the game's menu cannot be put back in order: the game "
                             "ends now; scrolling stays off on the next start (kiln/state/menu-scroll.off)", what);
        Sleep(250);
    } catch (...) {
    }
}

[[noreturn]] void give_up(const char* what) noexcept {
    say_on_disk(what);
    say_in_log(what);
    __fastfail(7);  // FAST_FAIL_FATAL_APP_EXIT: reached whatever the two calls above did
}

// One of the game's calls, under a guard: false when it faulted (an access violation; the game's own error handling
// ends the game and cannot be caught here).
template <typename Fn>
bool guarded(Fn&& f) {
    __try {
        f();
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// Closes what this call's begin opened, innermost first (menu_scroll::unwind_order). False when a close faulted.
bool unwind() {
    menu_scroll::Close order[2];
    const int n = menu_scroll::unwind_order(g_nest, order);
    bool ok = true;
    for (int i = 0; i < n && ok; ++i)
        ok = order[i] == menu_scroll::Close::inner ? guarded([] { F.frame_end(g_inner); }) : guarded([] { F.end(g_layout); });
    g_nest = {};
    return ok;
}

// The game's calls that begin the scroll view. Before the game's begin a fault leaves nothing open (false: the
// builder goes on as without Kiln). From the begin on, a fault closes what was opened, in order; a begin or a stack that
// faulted half way, or a close that faults, cannot be put back in order: give_up.
bool begin_scroll(uint8_t* column) {
    void* state = nullptr;
    const bool before = guarded([&] {
        void* node = *reinterpret_cast<void**>(column + (F.column_node - F.column));
        if (!node) return;
        std::memset(g_params, 0, sizeof g_params);
        F.params_init(g_params);  // the game's defaults: scissor on, auto scrolling on, navigation as the credits have it
        state = F.state(node, kStateHash, 4, F.state_nop, 1);
        if (state && F.state_new(state)) *static_cast<uint32_t*>(state) = 0;
    });
    if (!before || !state) return false;
    g_nest = {};
    std::memset(g_layout, 0, sizeof g_layout);
    if (!guarded([&] { F.begin(g_layout, 1, state, nullptr, g_params); })) give_up("faulted inside the game's begin");
    g_nest.layout = true;
    if (!guarded([] { F.set_speed(g_layout, F.speed(g_layout)); })) {
        if (!unwind()) give_up("faulted, and so did closing its scroll view");
        return false;
    }
    std::memset(g_inner, 0, sizeof g_inner);
    if (!guarded([] { F.vstack(g_inner, 0.0f); })) give_up("faulted inside the game's stack");  // the column's own kind
    g_nest.inner = true;
    const bool set = guarded([] {
        if (void* inner = *reinterpret_cast<void**>(g_inner + 8)) {
            F.align(inner, &kColumnAlign);
            F.size(inner, pack(1.0f, 0.0f));  // full width, height from the entries
        }
    });
    if (!set) {
        if (!unwind()) give_up("faulted, and so did closing what it opened");
        return false;
    }
    return true;
}

void on_open(KilnRegisters& r) {
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
        // nothing of it is open any more (begin_scroll closed what it had opened): the builder goes on as without Kiln
        g_broken = true;
        g_pair.open = false;
        turn_off(KILN_UI_SCROLL_OFF_FAULTED, "beginning the game's scroll view faulted (closed again in order)");
    }
}

void on_close(KilnRegisters& r) {
    if (menu_scroll::at_close(g_pair) != menu_scroll::At::end) return;
    if (!unwind()) give_up("faulted while closing its scroll view");
    // the crash guard's trial: wrapped builder calls that ran to their close site
    if (g_marker_written && menu_scroll::trial_step(g_trial)) g_clear_marker = true;
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
    g_gave_up = widen(dir + "\\menu-scroll.gave-up");
    {
        // an earlier start ended the game on purpose (give_up): say it once here, where kiln.log is whole
        std::string why;
        if (read_file(g_gave_up, why) == menu_scroll::File::read && !why.empty()) {
            while (!why.empty() && (why.back() == '\n' || why.back() == '\r')) why.pop_back();
            logf(KILN_LOG_WARN, "ui: at an earlier start: %s (kiln/state/menu-scroll.gave-up)", why.c_str());
        }
    }
    KilnBuildInfo bi{};
    bi.size = sizeof bi;
    std::string kiln = "?", build = "?";
    if (g_api->build_info(&bi) == KILN_OK) {
        build = bi.exe_sha256;
        if (bi.kiln_version.ptr) kiln.assign(bi.kiln_version.ptr, bi.kiln_version.len);
    }
    std::string trying_text, off_text;
    const menu_scroll::File trying = read_file(g_trying, trying_text);
    const menu_scroll::File off = read_file(g_off, off_text);
    switch (menu_scroll::decide(trying, off, off_text, kiln, build)) {
    case menu_scroll::Start::off_after_crash: {
        // the game ended while an earlier start tried it: its marker becomes the off record (its version and build);
        // the marker goes only once the record is on the disk, so the evidence is never lost
        const bool moved = MoveFileExW(g_trying.c_str(), g_off.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH);
        const bool written = !moved && trying == menu_scroll::File::read && write_file(g_off, trying_text);
        if (menu_scroll::may_delete_trying(moved, written)) DeleteFileW(g_trying.c_str());
        g_status = KILN_UI_SCROLL_OFF_CRASHED;
        logf(KILN_LOG_WARN, "ui: the pause menu does not scroll: the game ended while an earlier start tried it, so it "
                            "stays off with this Kiln version and game build (delete %s to try again%s)",
             moved || written ? g_off_u8.c_str() : g_trying_u8.c_str(),
             moved || written ? "" : "; the off record could not be written, the marker stays");
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
        if (off != menu_scroll::File::absent) DeleteFileW(g_off.c_str());  // another Kiln version or game build: one more try
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
bool scroll_wrapped_now() { return g_pair.open; }

void scroll_after_frame(uint64_t) {
    if (!g_clear_marker.exchange(false)) return;
    if (DeleteFileW(g_trying.c_str()) || GetLastError() == ERROR_FILE_NOT_FOUND)
        logf(KILN_LOG_INFO, "ui: the scrolling pause menu drew %u frames without trouble: its crash guard is cleared",
             menu_scroll::kTrialFrames);
}

}  // namespace kui
