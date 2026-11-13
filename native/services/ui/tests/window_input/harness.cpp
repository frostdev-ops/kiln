// SPDX-License-Identifier: MIT
// ui-window-input-test.exe: kiln.ui's production window procedure (input.cpp, wndproc_chain.h) on a message-only window
// under Wine, no display: subscribe_window_input (kiln_ui.h v5). Run by native/services/ui/tests/test-window-input-wine.sh.
//
// The "game" is this exe's own window procedure; it logs every message it gets. WM_INPUT carries made-up HRAWINPUT
// values that this exe's import of GetRawInputData (patched below; input.cpp is linked into this exe) answers with
// RAWINPUT records of the test's choice. Messages are sent with SendMessageW from the window's own thread, as the
// game's message loop would dispatch them. Checks:
//   - every record of each kind arrives once, unchanged and in the window's order (raw keys, window keys, raw and
//     message mouse buttons, focus loss), and nothing else does (characters, motion, the wheel);
//   - the game's procedure and Kiln's own key handling get exactly what they get without a subscriber, also while Kiln
//     captures input (menu open, the wheel) and for a key a Kiln binding consumes, and those records still arrive;
//   - one focus loss (WM_ACTIVATEAPP 0 and WM_KILLFOCUS) gives one record;
//   - unsubscribe stops delivery, waits for a call in flight on the window thread, works from inside the callback;
//     a faulting subscriber is removed and the others go on;
//   - the middle and side buttons and the wheel as key presses (mouse_press.h, since 0.2.0-dev.27): a raw record and
//     the message of the same notch or click give ONE press, in either order; a high-resolution wheel's small deltas
//     add up to one press per notch; a chord a key subscriber takes is kept from the game (record and message), one it
//     does not take passes; while Kiln captures input the press comes with captured 1 and the game gets nothing; over a
//     Kiln panel the wheel is the panel's; the left and right buttons are never presses; take_pressed_key stays
//     keyboard only; key_name names them; only subscribe_key_ex(KILN_UI_KEYS_MOUSE) subscribers get mouse presses
//     (keys.cpp, linked here): a subscribe_key subscriber gets none and cannot take one, and with no mouse subscriber
//     the mouse reaches the game untouched.
// Lines: "PASS ...", "FAIL ...". Exit code 0 = no FAIL.
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

#include "ui.h"
#include "raw_input_source.h"

// ---- what input.cpp needs from ui.cpp ----
namespace kui {
const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
std::atomic<uint32_t> g_demand{0}, g_interactive{0}, g_render_demand{0}, g_hover_panels{0};
std::atomic<bool> g_menu_open{false}, g_text_input{false}, g_want_mouse{false}, g_wheel_active{false}, g_page_capture{false};
thread_local CallbackLifetime* t_callback = nullptr;
int g_errors = 0;
void logf(int, const char* fmt, ...) {
    char b[1024];
    va_list a;
    va_start(a, fmt);
    std::vsnprintf(b, sizeof b, fmt, a);
    va_end(a);
    std::printf("KILN: %s\n", b);
}
void set_error(const char* fmt, ...) {
    char b[256];
    va_list a;
    va_start(a, fmt);
    std::vsnprintf(b, sizeof b, fmt, a);
    va_end(a);
    std::printf("KILN ERROR: %s\n", b);
    ++g_errors;
}
uint64_t now_ms() { return GetTickCount64(); }
KilnHandle next_handle() {
    static std::atomic<KilnHandle> h{1};
    return h++;
}
// Kiln's own key handling (the menu key, bindings, the wheel): what reaches it, and one key it consumes (with these
// modifiers only, unless g_consume_mods is ~0u). The real key subscribers (keys.cpp: subscribe_key, subscribe_key_ex)
// get it after, as in ui.cpp.
std::vector<std::string> g_kiln_keys;
uint32_t g_consume_vk = 0, g_consume_mods = ~0u;
bool dispatch_key(const KilnUiKey& k, bool mouse) {
    char b[96];
    std::snprintf(b, sizeof b, "vk=%u scan=%x mods=%u down=%u repeat=%u captured=%u", k.vk, k.scan, k.mods, k.down, k.repeat,
                  k.captured);
    g_kiln_keys.push_back(b);
    const bool subscribers = keys_dispatch(k, mouse);
    return subscribers || (k.down && k.vk == g_consume_vk && (g_consume_mods == ~0u || k.mods == g_consume_mods));
}
}  // namespace kui

namespace {

int g_fails = 0, g_checks = 0;
void check(bool ok, const char* what, ...) {
    char b[2048];
    va_list a;
    va_start(a, what);
    std::vsnprintf(b, sizeof b, what, a);
    va_end(a);
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", b);
    ++g_checks;
    if (!ok) ++g_fails;
}

// ---- the game: a window procedure of this exe that logs what it gets ----
HWND g_win = nullptr;
std::vector<std::string> g_game;
bool interesting(UINT m) {
    return m == WM_INPUT || (m >= WM_KEYFIRST && m <= WM_KEYLAST) || (m >= WM_MOUSEFIRST && m <= WM_MOUSELAST) ||
           m == WM_SETFOCUS || m == WM_KILLFOCUS || m == WM_ACTIVATEAPP;
}
LRESULT CALLBACK game_proc(HWND h, UINT m, WPARAM w, LPARAM l) {
    if (interesting(m)) {
        char b[160];
        std::snprintf(b, sizeof b, "%04x %llx %llx", m, static_cast<unsigned long long>(w), static_cast<unsigned long long>(l));
        if (m == WM_INPUT) {  // as the game does: read the record (the handle differs from run to run, its record not)
            alignas(8) unsigned char data[256];
            UINT size = sizeof data;
            if (GetRawInputData(reinterpret_cast<HRAWINPUT>(l), RID_INPUT, data, &size, sizeof(RAWINPUTHEADER)) != static_cast<UINT>(-1)) {
                const auto* ri = reinterpret_cast<const RAWINPUT*>(data);
                if (ri->header.dwType == RIM_TYPEKEYBOARD)
                    std::snprintf(b, sizeof b, "00ff keyboard %x %x %x", ri->data.keyboard.MakeCode, ri->data.keyboard.Flags, ri->data.keyboard.VKey);
                else
                    std::snprintf(b, sizeof b, "00ff mouse %x %ld %ld %d", ri->data.mouse.usButtonFlags, ri->data.mouse.lLastX,
                                  ri->data.mouse.lLastY, static_cast<SHORT>(ri->data.mouse.usButtonData));
            }
        }
        g_game.push_back(b);
    }
    return DefWindowProcW(h, m, w, l);
}

// ---- WM_INPUT: made-up HRAWINPUT values 0x1000.. answered by this exe's patched GetRawInputData import ----
using GetRawInputDataFn = UINT(WINAPI*)(HRAWINPUT, UINT, LPVOID, PUINT, UINT);
GetRawInputDataFn g_real_raw = nullptr;
std::vector<RAWINPUT> g_raw;
int g_raw_reads = 0;
constexpr uintptr_t kRawBase = 0x1000;
UINT WINAPI fake_raw(HRAWINPUT h, UINT cmd, LPVOID data, PUINT size, UINT header) {
    const uintptr_t i = reinterpret_cast<uintptr_t>(h) - kRawBase;
    if (i >= g_raw.size() || cmd != RID_INPUT || header != sizeof(RAWINPUTHEADER) || !size)
        return g_real_raw(h, cmd, data, size, header);
    ++g_raw_reads;
    const RAWINPUT& r = g_raw[i];
    if (!data) {
        *size = r.header.dwSize;
        return 0;
    }
    if (*size < r.header.dwSize) return static_cast<UINT>(-1);
    std::memcpy(data, &r, r.header.dwSize);
    return r.header.dwSize;
}
bool patch_import(const char* function, void* with, void** original) {
    auto* image = reinterpret_cast<unsigned char*>(GetModuleHandleW(nullptr));
    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(image);
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(image + dos->e_lfanew);
    const auto dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    for (auto* imp = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(image + dir.VirtualAddress); imp->Name; ++imp) {
        auto* names = reinterpret_cast<IMAGE_THUNK_DATA64*>(image + (imp->OriginalFirstThunk ? imp->OriginalFirstThunk : imp->FirstThunk));
        auto* slots = reinterpret_cast<IMAGE_THUNK_DATA64*>(image + imp->FirstThunk);
        for (; names->u1.AddressOfData; ++names, ++slots) {
            if (IMAGE_SNAP_BY_ORDINAL64(names->u1.Ordinal)) continue;
            const auto* named = reinterpret_cast<const IMAGE_IMPORT_BY_NAME*>(image + names->u1.AddressOfData);
            if (std::strcmp(reinterpret_cast<const char*>(named->Name), function)) continue;
            DWORD old = 0;
            if (!VirtualProtect(&slots->u1.Function, sizeof(void*), PAGE_READWRITE, &old)) return false;
            *original = reinterpret_cast<void*>(slots->u1.Function);
            slots->u1.Function = reinterpret_cast<ULONGLONG>(with);
            VirtualProtect(&slots->u1.Function, sizeof(void*), old, &old);
            return true;
        }
    }
    return false;
}

LPARAM raw_key(USHORT make_code, USHORT flags, USHORT vkey) {
    RAWINPUT r{};
    r.header.dwType = RIM_TYPEKEYBOARD;
    r.header.dwSize = sizeof(RAWINPUTHEADER) + sizeof(RAWKEYBOARD);
    r.data.keyboard.MakeCode = make_code;
    r.data.keyboard.Flags = flags;
    r.data.keyboard.VKey = vkey;
    r.data.keyboard.Message = (flags & RI_KEY_BREAK) ? WM_KEYUP : WM_KEYDOWN;
    g_raw.push_back(r);
    return static_cast<LPARAM>(kRawBase + g_raw.size() - 1);
}
LPARAM raw_mouse(USHORT buttons, LONG dx = 0, LONG dy = 0, USHORT data = 0) {
    RAWINPUT r{};
    r.header.dwType = RIM_TYPEMOUSE;
    r.header.dwSize = sizeof(RAWINPUTHEADER) + sizeof(RAWMOUSE);
    r.data.mouse.usButtonFlags = buttons;
    r.data.mouse.usButtonData = data;
    r.data.mouse.lLastX = dx;
    r.data.mouse.lLastY = dy;
    g_raw.push_back(r);
    return static_cast<LPARAM>(kRawBase + g_raw.size() - 1);
}

// ---- subscribers ----
struct Got {
    std::vector<std::string> lines;
};
std::string text(const KilnUiWindowInput& r) {
    char b[160];
    switch (r.kind) {
        case KILN_UI_INPUT_RAW_KEY:
            std::snprintf(b, sizeof b, "raw %x %x %x", r.make_code, r.flags, r.vkey);
            break;
        case KILN_UI_INPUT_WINDOW_KEY:
            std::snprintf(b, sizeof b, "key %04x %llx %llx", r.message, static_cast<unsigned long long>(r.wparam),
                          static_cast<unsigned long long>(r.lparam));
            break;
        case KILN_UI_INPUT_MOUSE_BUTTON:
            std::snprintf(b, sizeof b, "button %u %u via %04x", r.button, r.down, r.message);
            break;
        case KILN_UI_INPUT_FOCUS_LOST:
            std::snprintf(b, sizeof b, "focus-lost %04x %llx", r.message, static_cast<unsigned long long>(r.wparam));
            break;
        default:
            std::snprintf(b, sizeof b, "kind %u", r.kind);
    }
    if (r.size != sizeof(KilnUiWindowInput)) std::snprintf(b + std::strlen(b), sizeof b - std::strlen(b), " size %u", r.size);
    return b;
}
void KILN_CALL collect(const KilnUiWindowInput* r, void* user) { static_cast<Got*>(user)->lines.push_back(text(*r)); }

std::string join(const std::vector<std::string>& v) {
    std::string s;
    for (const auto& x : v) s += "\n      " + x;
    return s.empty() ? " (none)" : s;
}

void send(UINT m, WPARAM w, LPARAM l) { SendMessageW(g_win, m, w, l); }

// lParam of a key message: repeat 1, scan code, extended, context (Alt), previous state, transition
LPARAM key_l(UINT scan, bool extended, bool up, bool alt = false) {
    return static_cast<LPARAM>(1u | (scan << 16) | (extended ? 1u << 24 : 0) | (alt ? 1u << 29 : 0) | (up ? 3u << 30 : 0));
}

// The sequence a player's input gives the window, and what a subscriber must get from it.
void play_everything() {
    send(WM_INPUT, RIM_INPUT, raw_key(0x43, RI_KEY_MAKE, VK_F9));
    send(WM_KEYDOWN, VK_F9, key_l(0x43, false, false));
    send(WM_CHAR, 'x', 1);                                             // no record
    send(WM_INPUT, RIM_INPUT, raw_key(0x43, RI_KEY_BREAK, VK_F9));
    send(WM_KEYUP, VK_F9, key_l(0x43, false, true));
    send(WM_SYSKEYDOWN, VK_MENU, key_l(0x38, false, false, true));
    send(WM_SYSKEYUP, VK_MENU, key_l(0x38, false, true, true));
    send(WM_INPUT, RIM_INPUT, raw_key(0x1d, RI_KEY_E0, VK_CONTROL));    // right Ctrl
    send(WM_INPUT, RIM_INPUT, raw_key(0x1d, RI_KEY_E0 | RI_KEY_BREAK, VK_CONTROL));
    send(WM_INPUT, RIM_INPUT, raw_key(0x1d, RI_KEY_E1, VK_PAUSE));      // Pause: the E1 record, then its key
    send(WM_INPUT, RIM_INPUT, raw_key(0x45, RI_KEY_MAKE, 0xff));
    send(WM_INPUT, RIM_INPUT, raw_key(0x1d, RI_KEY_E1 | RI_KEY_BREAK, VK_PAUSE));
    send(WM_INPUT, RIM_INPUT, raw_key(0x45, RI_KEY_BREAK, 0xff));
    send(WM_INPUT, RIM_INPUT, raw_key(0, RI_KEY_MAKE, VK_VOLUME_UP));   // a key without a make code: unchanged
    send(WM_INPUT, RIM_INPUT, raw_key(0, RI_KEY_BREAK, VK_VOLUME_UP));
    send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_BUTTON_4_DOWN));
    send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_RIGHT_BUTTON_UP | RI_MOUSE_LEFT_BUTTON_DOWN | RI_MOUSE_BUTTON_5_UP));
    send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_WHEEL, 0, 0, 120));     // no record
    send(WM_INPUT, RIM_INPUT, raw_mouse(0, 5, -3));                    // motion: no record
    send(WM_LBUTTONDOWN, MK_LBUTTON, MAKELPARAM(10, 20));
    send(WM_LBUTTONUP, 0, MAKELPARAM(10, 20));
    send(WM_LBUTTONDBLCLK, MK_LBUTTON, MAKELPARAM(10, 20));
    send(WM_RBUTTONDOWN, MK_RBUTTON, MAKELPARAM(1, 2));
    send(WM_RBUTTONUP, 0, MAKELPARAM(1, 2));
    send(WM_MBUTTONDOWN, MK_MBUTTON, 0);
    send(WM_MBUTTONUP, 0, 0);
    send(WM_XBUTTONDOWN, MAKEWPARAM(MK_XBUTTON1, XBUTTON1), 0);
    send(WM_XBUTTONUP, MAKEWPARAM(0, XBUTTON2), 0);
    send(WM_MOUSEMOVE, 0, MAKELPARAM(30, 40));                         // no record
    send(WM_MOUSEWHEEL, MAKEWPARAM(0, WHEEL_DELTA), 0);                // no record
    // One focus loss: both messages arrive, one record (the first message's).
    send(WM_ACTIVATEAPP, FALSE, 0);
    send(WM_KILLFOCUS, 0, 0);
    send(WM_KILLFOCUS, 0, 0);                                          // still the same loss
    send(WM_SETFOCUS, 0, 0);
    send(WM_KILLFOCUS, 0, 0);                                          // a new loss
    send(WM_ACTIVATEAPP, FALSE, 0);                                    // the same one
    send(WM_ACTIVATEAPP, TRUE, 0);
    send(WM_ACTIVATEAPP, FALSE, 0);                                    // a new loss
    send(WM_INPUT, RIM_INPUT, raw_key(0x1e, RI_KEY_MAKE, 'A'));         // a key while the window had lost the focus...
    send(WM_KILLFOCUS, 0, 0);                                          // ...so this loss clears it again
    send(WM_INPUT, RIM_INPUT, raw_key(0x1e, RI_KEY_BREAK, 'A'));
}
const std::vector<std::string> kEverything = {
    "raw 43 0 78",
    "key 0100 78 430001",
    "raw 43 1 78",
    "key 0101 78 c0430001",
    "key 0104 12 20380001",
    "key 0105 12 e0380001",
    "raw 1d 2 11",
    "raw 1d 3 11",
    "raw 1d 4 13",
    "raw 45 0 ff",
    "raw 1d 5 13",
    "raw 45 1 ff",
    "raw 0 0 af",
    "raw 0 1 af",
    "button 4 1 via 00ff",
    "button 1 1 via 00ff",
    "button 3 0 via 00ff",
    "button 5 0 via 00ff",
    "button 1 1 via 0201",
    "button 1 0 via 0202",
    "button 1 1 via 0203",
    "button 3 1 via 0204",
    "button 3 0 via 0205",
    "button 2 1 via 0207",
    "button 2 0 via 0208",
    "button 4 1 via 020b",
    "button 5 0 via 020c",
    "focus-lost 001c 0",
    "focus-lost 0008 0",
    "focus-lost 001c 0",
    "raw 1e 0 41",
    "focus-lost 0008 0",
    "raw 1e 1 41",
};

struct Run {
    std::vector<std::string> game, kiln, got;
    int raw_reads = 0;
};
template <class F>
Run run(F&& play, Got* got) {
    g_game.clear();
    kui::g_kiln_keys.clear();
    g_raw_reads = 0;
    if (got) got->lines.clear();
    play();
    return Run{g_game, kui::g_kiln_keys, got ? got->lines : std::vector<std::string>{}, g_raw_reads};
}

// The same input with and without a subscriber: the game and Kiln's own key handling see the same; the subscriber gets
// `want`.
void same_for_game(const char* what, void (*play)(), const std::vector<std::string>& want) {
    const Run before = run(play, nullptr);
    Got got;
    KilnHandle h = 0;
    check(kui::input_feed_subscribe(7, &collect, &got, &h) == KILN_OK && h, "%s: subscribed", what);
    const Run with = run(play, &got);
    check(with.got == want, "%s: the subscriber gets every record, unchanged and in order:%s\n    expected:%s", what,
          join(with.got).c_str(), join(want).c_str());
    check(with.game == before.game, "%s: the game's procedure gets the same %zu messages as without a subscriber:%s\n    without:%s",
          what, with.game.size(), join(with.game).c_str(), join(before.game).c_str());
    check(with.kiln == before.kiln, "%s: Kiln's own key handling gets the same %zu keys as without a subscriber", what, with.kiln.size());
    check(with.raw_reads == before.raw_reads, "%s: no extra GetRawInputData calls (%d, %d without)", what, with.raw_reads, before.raw_reads);
    check(kui::input_feed_unsubscribe(h) == KILN_OK, "%s: unsubscribed", what);
    const Run after = run(play, &got);
    check(after.got.empty() && after.game == before.game, "%s: after unsubscribe nothing is delivered (%zu records)", what, after.got.size());
}

bool contains(const std::vector<std::string>& v, const std::string& prefix) {
    for (const auto& x : v)
        if (x.rfind(prefix, 0) == 0) return true;
    return false;
}

void play_captured() {
    send(WM_INPUT, RIM_INPUT, raw_key(0x43, RI_KEY_MAKE, VK_F9));
    send(WM_KEYDOWN, VK_F9, key_l(0x43, false, false));
    send(WM_CHAR, 'x', 1);
    send(WM_INPUT, RIM_INPUT, raw_key(0x43, RI_KEY_BREAK, VK_F9));
    send(WM_KEYUP, VK_F9, key_l(0x43, false, true));
    send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_LEFT_BUTTON_DOWN));
    send(WM_LBUTTONDOWN, MK_LBUTTON, 0);
    send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_LEFT_BUTTON_UP));
    send(WM_LBUTTONUP, 0, 0);
}
const std::vector<std::string> kCaptured = {
    "raw 43 0 78", "key 0100 78 430001", "raw 43 1 78", "key 0101 78 c0430001",
    "button 1 1 via 00ff", "button 1 1 via 0201", "button 1 0 via 00ff", "button 1 0 via 0202",
};

void play_consumed() {  // Insert: the key a Kiln binding consumes here (the menu key's default)
    send(WM_INPUT, RIM_INPUT, raw_key(0x52, RI_KEY_E0, VK_INSERT));
    send(WM_KEYDOWN, VK_INSERT, key_l(0x52, true, false));
    send(WM_INPUT, RIM_INPUT, raw_key(0x52, RI_KEY_E0 | RI_KEY_BREAK, VK_INSERT));
    send(WM_KEYUP, VK_INSERT, key_l(0x52, true, true));
}
const std::vector<std::string> kConsumed = {"raw 52 2 2d", "key 0100 2d 1520001", "raw 52 3 2d", "key 0101 2d c1520001"};

void play_edge_keys() {
    send(WM_INPUT, RIM_INPUT, raw_key(0x2a, RI_KEY_E0, VK_SHIFT)); // Print Screen's fake extended Shift
    send(WM_INPUT, RIM_INPUT, raw_key(0x37, RI_KEY_E0, VK_SNAPSHOT));
    send(WM_INPUT, RIM_INPUT, raw_key(0x37, RI_KEY_E0 | RI_KEY_BREAK, VK_SNAPSHOT));
    send(WM_KEYUP, VK_SNAPSHOT, key_l(0x37, true, true));
    send(WM_INPUT, RIM_INPUT, raw_key(0x2a, RI_KEY_E0 | RI_KEY_BREAK, VK_SHIFT));
    send(WM_INPUT, RIM_INPUT, raw_key(0x38, RI_KEY_E0, VK_MENU)); // right Alt
    send(WM_INPUT, RIM_INPUT, raw_key(0x38, RI_KEY_E0 | RI_KEY_BREAK, VK_MENU));
    send(WM_INPUT, RIM_INPUT, raw_key(0x48, 0, VK_NUMPAD8));
    send(WM_INPUT, RIM_INPUT, raw_key(0x48, 0, VK_NUMPAD8)); // repeat is still a raw record
    send(WM_INPUT, RIM_INPUT, raw_key(0x48, RI_KEY_BREAK, VK_NUMPAD8));
    send(WM_INPUT, RIM_INPUT, raw_key(0x48, RI_KEY_E0, VK_UP)); // same make code, extended navigation key
    send(WM_INPUT, RIM_INPUT, raw_key(0x48, RI_KEY_E0 | RI_KEY_BREAK, VK_UP));
    send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_BUTTON_4_DOWN | RI_MOUSE_BUTTON_4_UP));
    send(WM_XBUTTONDBLCLK, MAKEWPARAM(MK_XBUTTON2, XBUTTON2), 0);
}
const std::vector<std::string> kEdgeKeys = {
    "raw 2a 2 10", "raw 37 2 2c", "raw 37 3 2c", "key 0101 2c c1370001", "raw 2a 3 10",
    "raw 38 2 12", "raw 38 3 12", "raw 48 0 68", "raw 48 0 68", "raw 48 1 68",
    "raw 48 2 26", "raw 48 3 26", "button 4 1 via 00ff", "button 4 0 via 00ff", "button 5 1 via 020d",
};

void raw_boundaries() {
    RAWINPUT hid{};
    hid.header.dwType = RIM_TYPEHID;
    hid.header.dwSize = 512; // only the size query succeeds; both readers' buffers are too small
    g_raw.push_back(hid);
    const LPARAM handle = static_cast<LPARAM>(kRawBase + g_raw.size() - 1);
    Got got;
    KilnHandle h = 0;
    kui::input_feed_subscribe(1, &collect, &got, &h);
    g_game.clear();
    g_raw_reads = 0;
    send(WM_INPUT, RIM_INPUT, handle);
    check(got.lines.empty() && g_game.size() == 1 && g_raw_reads == 2,
          "oversized HID record is not decoded, still reaches the game, and causes no oversized read");
    kui::input_feed_unsubscribe(h);

    const HWND other = CreateWindowExW(0, L"KilnWindowInputGame", L"other raw target", 0, 0, 0, 0, 0,
                                       HWND_MESSAGE, nullptr, GetModuleHandleW(nullptr), nullptr);
    const RAWINPUTDEVICE target{1, 6, 0, other};
    check(other && RegisterRawInputDevices(&target, 1, sizeof target), "raw keyboard targets another message-only window");
    check(kui::raw_input::registered_for(6, other) && !kui::raw_input::registered_for(6, g_win),
          "source selection distinguishes the raw target from a replacement window");
    DestroyWindow(other);
    check(!kui::raw_input::registered_for(6, g_win), "destroyed raw target does not disable the replacement's legacy input");
    const RAWINPUTDEVICE restore{1, 6, 0, nullptr};
    check(RegisterRawInputDevices(&restore, 1, sizeof restore), "raw keyboard registration restored");
}

void held_focus_lost() {
    send(WM_INPUT, RIM_INPUT, raw_key(0x24, RI_KEY_BREAK, 'J'));
    send(WM_INPUT, RIM_INPUT, raw_key(0x24, RI_KEY_MAKE, 'J'));
    send(WM_KILLFOCUS, 0, 0);
    check(!kui::input_key_held('J'), "focus loss clears Kiln's held keys when no release arrives");
    uint32_t mods = 0;
    check(kui::input_take_pressed(&mods) == 0, "focus loss discards a pending bind-capture press");
    send(WM_SETFOCUS, 0, 0);
    send(WM_INPUT, RIM_INPUT, raw_key(0x24, RI_KEY_MAKE, 'J'));
    check(kui::g_kiln_keys.back().find("repeat=0") != std::string::npos,
          "first key after refocus is not mistaken for a repeat: %s", kui::g_kiln_keys.back().c_str());
    send(WM_ACTIVATEAPP, FALSE, 0);
    check(!kui::input_key_held('J'), "app deactivation also clears Kiln's held keys");
    send(WM_INPUT, RIM_INPUT, raw_key(0x24, RI_KEY_BREAK, 'J'));
}

void raw_unregistered() {
    kui::g_consume_vk = 'J';
    const RAWINPUTDEVICE remove{1, 6, RIDEV_REMOVE, nullptr};
    check(RegisterRawInputDevices(&remove, 1, sizeof remove), "raw keyboard unregistered");
    kui::g_kiln_keys.clear();
    g_game.clear();
    send(WM_KEYDOWN, 'J', key_l(0x24, false, false));
    check(kui::input_key_held('J') && !contains(g_game, "0100 "),
          "legacy key after raw stops reaches Kiln's binding and is consumed");
    send(WM_KEYUP, 'J', key_l(0x24, false, true));
    check(!kui::input_key_held('J') && kui::g_kiln_keys.size() == 2,
          "legacy release after raw stops clears Kiln's held key (%zu callbacks)", kui::g_kiln_keys.size());
    const RAWINPUTDEVICE add{1, 6, 0, nullptr};
    check(RegisterRawInputDevices(&add, 1, sizeof add), "raw keyboard registered again");
    kui::g_consume_vk = 0;
}

// ---- lifetime ----
struct Blocking {
    HANDLE inside, release;
    std::atomic<int> calls{0};
};
void KILN_CALL blocking(const KilnUiWindowInput*, void* user) {
    auto* b = static_cast<Blocking*>(user);
    if (b->calls.fetch_add(1) == 0) {
        SetEvent(b->inside);
        WaitForSingleObject(b->release, 10000);
    }
}
struct Self {
    KilnHandle handle = 0;
    int calls = 0;
    KilnResult result = -1;
};
void KILN_CALL self_unsubscribe(const KilnUiWindowInput*, void* user) {
    auto* s = static_cast<Self*>(user);
    ++s->calls;
    s->result = kui::input_feed_unsubscribe(s->handle);
}
void KILN_CALL faulting(const KilnUiWindowInput*, void* user) {
    ++*static_cast<int*>(user);
    RaiseException(0xC0000005, 0, 0, nullptr);
}

struct Nested {
    KilnHandle outer_handle = 0, inner_handle = 0;
    bool inside = false, finished = false;
    int calls = 0;
    KilnResult result = -1;
};
void KILN_CALL outer_callback(const KilnUiWindowInput*, void* user) {
    auto* n = static_cast<Nested*>(user);
    ++n->calls;
    if (n->inside || n->finished) return;
    n->inside = true;
    send(WM_KEYDOWN, 'N', key_l(0x31, false, false));
    n->inside = false;
    n->finished = true;
}
void KILN_CALL inner_callback(const KilnUiWindowInput*, void* user) {
    auto* n = static_cast<Nested*>(user);
    if (n->inside && n->result == -1) n->result = kui::input_feed_unsubscribe(n->outer_handle);
}
void nested_unsubscribe() {
    Nested n;
    kui::input_feed_subscribe(1, &outer_callback, &n, &n.outer_handle);
    kui::input_feed_subscribe(2, &inner_callback, &n, &n.inner_handle);
    send(WM_KEYDOWN, 'M', key_l(0x32, false, false));
    check(n.finished && n.result == KILN_ERR_WRONG_THREAD,
          "a nested callback cannot drain an outer callback still on this thread's stack (result %d)", n.result);
    check(kui::input_feed_unsubscribe(n.outer_handle) == KILN_OK,
          "the canceled outer callback can be drained after it returns");
    send(WM_KEYUP, 'M', key_l(0x32, false, true));
    check(n.calls == 2, "the canceled outer callback gets no more records (%d calls)", n.calls);
    kui::input_feed_unsubscribe(n.inner_handle);
}

void KILN_CALL recursive_self(const KilnUiWindowInput*, void* user) {
    auto* n = static_cast<Nested*>(user);
    if (!n->inside) {
        n->inside = true;
        send(WM_KEYDOWN, 'P', key_l(0x19, false, false));
        n->inside = false;
    } else {
        n->result = kui::input_feed_unsubscribe(n->outer_handle);
    }
}
void recursive_unsubscribe() {
    Nested n;
    kui::input_feed_subscribe(1, &recursive_self, &n, &n.outer_handle);
    send(WM_KEYDOWN, 'O', key_l(0x18, false, false));
    check(n.result == KILN_ERR_WRONG_THREAD,
          "self-unsubscribe cannot drain an earlier invocation of the same callback (result %d)", n.result);
    check(kui::input_feed_unsubscribe(n.outer_handle) == KILN_OK, "recursive self-unsubscribe can be retried after return");
}

struct Mutation {
    KilnHandle victim = 0, added = 0;
    Got got;
    int calls = 0;
    KilnResult canceled = -1;
};
void KILN_CALL mutate(const KilnUiWindowInput*, void* user) {
    auto* m = static_cast<Mutation*>(user);
    if (m->calls++ != 0) return;
    kui::input_feed_subscribe(3, &collect, &m->got, &m->added);
    m->canceled = kui::input_feed_unsubscribe(m->victim);
}
void delivery_mutation() {
    Mutation m;
    Got victim;
    KilnHandle first = 0;
    kui::input_feed_subscribe(1, &mutate, &m, &first);
    kui::input_feed_subscribe(2, &collect, &victim, &m.victim);
    send(WM_KEYDOWN, 'Q', key_l(0x10, false, false));
    check(m.canceled == KILN_OK && victim.lines.empty(), "a callback cancels another queued callback without waiting");
    check(m.added && m.got.lines.empty(), "a subscriber added during delivery does not receive that record");
    send(WM_KEYUP, 'Q', key_l(0x10, false, true));
    check(m.got.lines.size() == 1 && victim.lines.empty(), "the new subscriber receives the next record, not the canceled one");
    kui::input_feed_unsubscribe(first);
    kui::input_feed_unsubscribe(m.added);

    Blocking b;
    b.inside = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    b.release = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    KilnHandle blocked = 0;
    kui::input_feed_subscribe(1, &blocking, &b, &blocked);
    KilnResult result = -1;
    std::thread other([&] {
        if (WaitForSingleObject(b.inside, 10000) == WAIT_OBJECT_0) {
            kui::CallbackLifetime caller;
            kui::CallbackLifetime::Invocation invocation(&caller);
            kui::t_callback = &caller;
            result = kui::input_feed_unsubscribe(blocked);
            kui::t_callback = nullptr;
        }
        SetEvent(b.release);
    });
    send(WM_KEYDOWN, 'R', key_l(0x13, false, false));
    other.join();
    check(result == KILN_ERR_WRONG_THREAD, "another thread's callback cancels without waiting for an active callback (%d)", result);
    check(kui::input_feed_unsubscribe(blocked) == KILN_OK, "a canceled cross-thread callback drains on retry outside callbacks");
    CloseHandle(b.inside);
    CloseHandle(b.release);
}

void lifetime() {
    // A call in flight on the window thread: unsubscribe from another thread waits for it, and nothing comes after.
    Blocking b;
    b.inside = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    b.release = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    KilnHandle h = 0;
    kui::input_feed_subscribe(1, &blocking, &b, &h);
    std::atomic<bool> returned{false};
    KilnResult result = -1;
    bool waited = false;
    std::thread other([&] {
        if (WaitForSingleObject(b.inside, 10000) != WAIT_OBJECT_0) return;
        std::thread unsub([&] {
            result = kui::input_feed_unsubscribe(h);
            returned = true;
        });
        Sleep(300);
        waited = !returned.load();
        SetEvent(b.release);
        unsub.join();
    });
    send(WM_INPUT, RIM_INPUT, raw_key(0x43, RI_KEY_MAKE, VK_F9));  // the window thread: blocks inside the callback
    other.join();
    check(waited, "unsubscribe from another thread waits while the callback runs on the window thread");
    check(returned && result == KILN_OK, "...and returns KILN_OK once it ended (%d)", result);
    const int calls = b.calls.load();
    send(WM_INPUT, RIM_INPUT, raw_key(0x43, RI_KEY_BREAK, VK_F9));
    send(WM_KEYUP, VK_F9, key_l(0x43, false, true));
    send(WM_KILLFOCUS, 0, 0);
    check(calls == 1 && b.calls.load() == 1, "no call after unsubscribe returned (%d calls)", b.calls.load());
    check(kui::input_feed_unsubscribe(h) == KILN_ERR_NOT_FOUND, "a second unsubscribe: not found");

    // From inside its own callback.
    Self s;
    kui::input_feed_subscribe(1, &self_unsubscribe, &s, &s.handle);
    send(WM_KEYDOWN, 'B', key_l(0x30, false, false));
    send(WM_KEYUP, 'B', key_l(0x30, false, true));
    check(s.calls == 1 && s.result == KILN_OK, "a callback can unsubscribe itself (%d calls, result %d)", s.calls, s.result);

    // A faulting subscriber is removed; the others go on.
    int faults = 0;
    Got got;
    KilnHandle hf = 0, hg = 0;
    kui::input_feed_subscribe(1, &faulting, &faults, &hf);
    kui::input_feed_subscribe(2, &collect, &got, &hg);
    const int errors = kui::g_errors;
    send(WM_KEYDOWN, 'C', key_l(0x2e, false, false));
    send(WM_KEYUP, 'C', key_l(0x2e, false, true));
    check(faults == 1 && kui::g_errors == errors + 1, "a faulting callback runs once and is removed, with an error line (%d)", faults);
    check(got.lines.size() == 2, "the other subscriber still gets both records (%zu)", got.lines.size());
    check(kui::input_feed_unsubscribe(hf) == KILN_ERR_NOT_FOUND, "the faulting one is gone");
    // Two subscribers get the same records.
    Got second;
    KilnHandle h2 = 0;
    kui::input_feed_subscribe(3, &collect, &second, &h2);
    got.lines.clear();
    send(WM_INPUT, RIM_INPUT, raw_key(0x2e, RI_KEY_MAKE, 'C'));
    check(got.lines == second.lines && got.lines.size() == 1, "two subscribers get the same records");
    // A new subscription starts with the focus as "not lost": its first loss gives a record.
    send(WM_KILLFOCUS, 0, 0);
    const auto old_loss = got.lines;
    Got late;
    KilnHandle hl = 0;
    kui::input_feed_subscribe(5, &collect, &late, &hl);
    send(WM_ACTIVATEAPP, FALSE, 0);  // the second notification of the same loss, with a new subscriber in between
    check(got.lines == old_loss, "adding a subscriber does not deliver the same focus loss twice to existing subscribers");
    check(late.lines == std::vector<std::string>{"focus-lost 001c 0"}, "the late subscriber gets its first loss notification");
    kui::input_feed_unsubscribe(hl);
    kui::input_feed_unsubscribe(hg);
    kui::input_feed_unsubscribe(h2);
    Got fresh;
    KilnHandle h3 = 0;
    kui::input_feed_subscribe(4, &collect, &fresh, &h3);
    send(WM_KILLFOCUS, 0, 0);
    check(fresh.lines == std::vector<std::string>{"focus-lost 0008 0"}, "a new subscriber gets its first focus loss:%s", join(fresh.lines).c_str());
    kui::input_feed_unsubscribe(h3);
    check(kui::input_feed_subscribe(1, nullptr, nullptr, &h3) == KILN_ERR_INVALID_ARGUMENT, "no fn: INVALID_ARGUMENT");
}

// ---- the middle and side buttons and the wheel as key presses (mouse_press.h) ----
size_t count_prefix(const std::vector<std::string>& v, const std::string& prefix) {
    size_t n = 0;
    for (const auto& x : v) n += x.rfind(prefix, 0) == 0;
    return n;
}
size_t count_if_has(const std::vector<std::string>& v, const std::string& part) {
    size_t n = 0;
    for (const auto& x : v) n += x.find(part) != std::string::npos;
    return n;
}
// Posted, then taken from the queue and dispatched as a game's message loop does: the message carries its own message
// time (GetMessageTime), unlike a sent one, which carries the time of the last message taken.
void post_pump(UINT m, WPARAM w, LPARAM l) {
    PostMessageW(g_win, m, w, l);
    MSG msg;
    while (PeekMessageW(&msg, g_win, 0, 0, PM_REMOVE)) DispatchMessageW(&msg);
}
// Kiln's key lines for one vk and transition ("vk=256 ... down=1"), presses only: a part of a wheel notch is asked as a
// repeat (would a subscriber keep the notch?), which `parts` counts.
size_t presses(const Run& r, uint32_t vk, bool down = true, bool repeat = false) {
    size_t n = 0;
    char want[32];
    std::snprintf(want, sizeof want, "down=%u repeat=%u", down ? 1u : 0u, repeat ? 1u : 0u);
    for (const auto& x : r.kiln) {
        unsigned v = 0;
        if (std::sscanf(x.c_str(), "vk=%u", &v) == 1 && v == vk && x.find(want) != std::string::npos) ++n;
    }
    return n;
}
size_t parts(const Run& r, uint32_t vk) { return presses(r, vk, true, true); }
size_t all_presses(const Run& r) { return r.kiln.size() - count_if_has(r.kiln, "repeat=1"); }
// The modifiers the window thread reads (GetKeyState): Ctrl and Shift held or not.
void hold(bool ctrl, bool shift = false) {
    BYTE ks[256] = {};
    GetKeyboardState(ks);
    ks[VK_CONTROL] = ks[VK_LCONTROL] = ctrl ? 0x80 : 0;
    ks[VK_SHIFT] = ks[VK_LSHIFT] = shift ? 0x80 : 0;
    SetKeyboardState(ks);
}
// The window forgets pending twins and notches with the focus (as after alt-tab): each case starts clean.
void fresh() {
    send(WM_KILLFOCUS, 0, 0);
    send(WM_SETFOCUS, 0, 0);
}
WPARAM wheel_w(int delta) { return MAKEWPARAM(0, static_cast<WORD>(static_cast<SHORT>(delta))); }
void raw_wheel(int delta, USHORT more = 0, LONG dx = 0, LONG dy = 0) {
    send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_WHEEL | more, dx, dy, static_cast<USHORT>(static_cast<SHORT>(delta))));
}
void msg_wheel(int delta) { send(WM_MOUSEWHEEL, wheel_w(delta), MAKELPARAM(10, 10)); }
std::string game_text(const Run& r) { return join(r.game); }
bool game_has_raw_wheel(const Run& r) {
    for (const auto& x : r.game) {
        unsigned flags = 0;
        if (std::sscanf(x.c_str(), "00ff mouse %x", &flags) == 1 && (flags & (RI_MOUSE_WHEEL | RI_MOUSE_HWHEEL))) return true;
    }
    return false;
}

void mouse_wheel_presses() {
    fresh();
    Run r = run([] { raw_wheel(120); msg_wheel(120); }, nullptr);
    check(presses(r, KILN_UI_VK_WHEEL_UP) == 1 && r.kiln.size() == 1,
          "wheel: a raw record and the WM_MOUSEWHEEL of the same notch give ONE press (WheelUp):%s", join(r.kiln).c_str());
    check(r.kiln.size() == 1 && r.kiln[0].find("mods=0 down=1 repeat=0 captured=0") != std::string::npos,
          "wheel: the notch is a press without modifiers, no repeat, not captured: %s", r.kiln.empty() ? "" : r.kiln[0].c_str());
    check(game_has_raw_wheel(r) && contains(r.game, "020a "), "wheel: no key subscriber took it: the game got both:%s",
          game_text(r).c_str());
    r = run([] { msg_wheel(-120); raw_wheel(-120); }, nullptr);
    check(presses(r, KILN_UI_VK_WHEEL_DOWN) == 1 && r.kiln.size() == 1,
          "wheel: the message before its raw record also gives ONE press (WheelDown):%s", join(r.kiln).c_str());
    r = run([] { raw_wheel(120); raw_wheel(120); msg_wheel(120); msg_wheel(120); }, nullptr);
    check(presses(r, KILN_UI_VK_WHEEL_UP) == 2 && r.kiln.size() == 2,
          "wheel: two raw records drained ahead of their two messages give two presses:%s", join(r.kiln).c_str());
    fresh();
    r = run([] { raw_wheel(120); raw_wheel(120); raw_wheel(120); }, nullptr);
    check(presses(r, KILN_UI_VK_WHEEL_UP) == 3, "wheel: raw records without messages: a press each (%zu)", presses(r, KILN_UI_VK_WHEEL_UP));
    fresh();
    r = run([] { msg_wheel(120); msg_wheel(120); }, nullptr);
    check(presses(r, KILN_UI_VK_WHEEL_UP) == 2, "wheel: messages without raw records: a press each (%zu)", presses(r, KILN_UI_VK_WHEEL_UP));
    fresh();
    r = run([] {
        send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_HWHEEL, 0, 0, 120));
        send(WM_MOUSEHWHEEL, wheel_w(120), 0);
        send(WM_MOUSEHWHEEL, wheel_w(-120), 0);
        send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_HWHEEL, 0, 0, static_cast<USHORT>(-120)));
    }, nullptr);
    check(presses(r, KILN_UI_VK_WHEEL_RIGHT) == 1 && presses(r, KILN_UI_VK_WHEEL_LEFT) == 1 && r.kiln.size() == 2,
          "wheel: the horizontal wheel the same way (WheelRight, WheelLeft once each):%s", join(r.kiln).c_str());
    fresh();
    r = run([] { raw_wheel(240); msg_wheel(240); }, nullptr);
    check(presses(r, KILN_UI_VK_WHEEL_UP) == 2, "wheel: a turn of two notches in one record gives two presses (%zu)",
          presses(r, KILN_UI_VK_WHEEL_UP));
}

void mouse_wheel_small_deltas() {
    fresh();
    Run r = run([] {
        for (int i = 0; i < 3; ++i) { raw_wheel(30); msg_wheel(30); }
    }, nullptr);
    check(all_presses(r) == 0 && parts(r, KILN_UI_VK_WHEEL_UP) == 3,
          "high-resolution wheel: three quarters of a notch give no press yet, each asked once as a part (repeat 1):%s",
          join(r.kiln).c_str());
    r = run([] { raw_wheel(30); msg_wheel(30); }, nullptr);
    check(presses(r, KILN_UI_VK_WHEEL_UP) == 1 && r.kiln.size() == 1, "high-resolution wheel: the fourth quarter gives ONE press:%s",
          join(r.kiln).c_str());
    r = run([] {
        raw_wheel(60); msg_wheel(60);       // half a notch up...
        raw_wheel(-60); msg_wheel(-60);     // ...then down: a new notch begins
        raw_wheel(-60); msg_wheel(-60);
    }, nullptr);
    check(presses(r, KILN_UI_VK_WHEEL_DOWN) == 1 && all_presses(r) == 1,
          "high-resolution wheel: a turn the other way starts a new notch (one WheelDown, no WheelUp):%s", join(r.kiln).c_str());
}

void mouse_wheel_bound() {
    fresh();
    kui::g_consume_vk = KILN_UI_VK_WHEEL_UP;  // a binding on Ctrl+WheelUp
    kui::g_consume_mods = KILN_UI_MOD_CTRL;
    hold(true);
    Run r = run([] { raw_wheel(120); msg_wheel(120); }, nullptr);
    check(presses(r, KILN_UI_VK_WHEEL_UP) == 1 && r.kiln.size() == 1 && r.kiln[0].find("mods=1 ") != std::string::npos,
          "bound Ctrl+WheelUp: one press with Ctrl:%s", join(r.kiln).c_str());
    check(!game_has_raw_wheel(r) && !contains(r.game, "020a "),
          "bound Ctrl+WheelUp: kept from the game, raw record and message (the camera does not zoom):%s", game_text(r).c_str());
    r = run([] { msg_wheel(120); raw_wheel(120); }, nullptr);
    check(r.kiln.size() == 1 && !game_has_raw_wheel(r) && !contains(r.game, "020a "),
          "bound Ctrl+WheelUp, message first: one press, both kept from the game:%s", game_text(r).c_str());
    hold(false);
    r = run([] { raw_wheel(120); msg_wheel(120); }, nullptr);
    check(presses(r, KILN_UI_VK_WHEEL_UP) == 1 && game_has_raw_wheel(r) && contains(r.game, "020a "),
          "the same wheel without Ctrl: one press no binding takes; the game gets both (the camera zooms):%s", game_text(r).c_str());
    hold(true);
    r = run([] { raw_wheel(-120); msg_wheel(-120); }, nullptr);
    check(presses(r, KILN_UI_VK_WHEEL_DOWN) == 1 && game_has_raw_wheel(r) && contains(r.game, "020a "),
          "Ctrl+WheelDown, not bound: passes to the game:%s", game_text(r).c_str());
    // A taken press in a record with other input: the game reads the record with only the taken flags (and the wheel's
    // delta) taken out, through its own GetRawInputData import (input_install_iat hooked it).
    r = run([] { raw_wheel(120, 0, 5, -3); msg_wheel(120); }, nullptr);
    check(r.kiln.size() == 1 && r.game == std::vector<std::string>{"00ff mouse 0 5 -3 0"},
          "bound notch in a record with motion: the game gets the motion, without the notch:%s", game_text(r).c_str());
    r = run([] { raw_wheel(120, RI_MOUSE_LEFT_BUTTON_UP); msg_wheel(120); }, nullptr);
    check(r.kiln.size() == 1 && r.game == std::vector<std::string>{"00ff mouse 2 0 0 0"},
          "bound notch in a record with another button's release: the game gets the release, without the notch:%s",
          game_text(r).c_str());
    r = run([] { raw_wheel(120, RI_MOUSE_RIGHT_BUTTON_DOWN | RI_MOUSE_BUTTON_5_UP, -7, 4); msg_wheel(120); }, nullptr);
    check(r.kiln.size() == 2 && r.game == std::vector<std::string>{"00ff mouse 204 -7 4 0"},
          "...with a right press, a Mouse5 release and motion: all three stay, only the notch goes:%s", game_text(r).c_str());
    // A high-resolution wheel on a bound chord: each part of a notch is asked (repeat 1) and kept like the notch.
    fresh();
    kui::g_consume_vk = KILN_UI_VK_WHEEL_DOWN;
    r = run([] {
        for (int i = 0; i < 8; ++i) { raw_wheel(-30); msg_wheel(-30); }
    }, nullptr);
    size_t to_game = count_prefix(r.game, "00ff ") + count_prefix(r.game, "020a ");
    check(presses(r, KILN_UI_VK_WHEEL_DOWN) == 2 && parts(r, KILN_UI_VK_WHEEL_DOWN) == 6 && to_game == 0,
          "bound Ctrl+WheelDown, high resolution: two presses, six parts asked; none of the 16 messages reaches the game "
          "(%zu)", to_game);
    // The binding gone: the very next part is asked again and passes
    kui::g_consume_vk = 0;
    r = run([] {
        for (int i = 0; i < 4; ++i) { raw_wheel(-30); msg_wheel(-30); }
    }, nullptr);
    to_game = count_prefix(r.game, "00ff ") + count_prefix(r.game, "020a ");
    check(presses(r, KILN_UI_VK_WHEEL_DOWN) == 1 && to_game == 8, "unbound again: every part reaches the game at once (%zu of 8)",
          to_game);
    // Review finding 3: Ctrl+WheelUp taken once, then the binding gone, then a high-resolution wheel going back and forth
    // with Ctrl (each turn the other way starts a new notch, none completes): nothing may stay kept.
    fresh();
    kui::g_consume_vk = KILN_UI_VK_WHEEL_UP;
    run([] { raw_wheel(120); msg_wheel(120); }, nullptr);
    kui::g_consume_vk = 0;
    r = run([] {
        for (int i = 0; i < 50; ++i) { raw_wheel(30); msg_wheel(30); raw_wheel(-30); msg_wheel(-30); }
    }, nullptr);
    check(r.game.size() == 200 && all_presses(r) == 0,
          "the binding gone, 100 parts back and forth with Ctrl: all 200 reach the game, no press (%zu reached it)", r.game.size());
    // Review finding 2: a twin carries the same message time. A raw record whose message never came, taken at one time,
    // and an unbound message 150 ms of message time later are two inputs: the second is dispatched and reaches the game.
    fresh();
    kui::g_consume_vk = KILN_UI_VK_WHEEL_UP;
    r = run([] {
        post_pump(WM_NULL, 0, 0);  // this thread's message time: now
        hold(true);
        raw_wheel(120);  // taken by Ctrl+WheelUp; its message never comes
        hold(false);
        Sleep(150);
        post_pump(WM_MOUSEWHEEL, wheel_w(120), 0);  // another turn, without Ctrl
    }, nullptr);
    check(presses(r, KILN_UI_VK_WHEEL_UP) == 2 && r.game == std::vector<std::string>{"020a 780000 0"},
          "a message 150 ms after a lone taken record is not its twin: two presses, the unbound one reaches the game:%s%s",
          join(r.kiln).c_str(), game_text(r).c_str());
    hold(false);
    kui::g_consume_mods = ~0u;
}

// Without the GetRawInputData hook (the game's import not found): a record with a taken press and other input reaches the
// game whole, so its other input is never lost; its message is still kept. A record with only the press is kept whole.
void mouse_mixed_without_hook() {
    fresh();
    kui::g_consume_vk = KILN_UI_VK_WHEEL_UP;
    kui::g_consume_mods = KILN_UI_MOD_CTRL;
    hold(true);
    Run r = run([] { raw_wheel(120, 0, 5, -3); msg_wheel(120); }, nullptr);
    check(r.kiln.size() == 1 && r.game == std::vector<std::string>{"00ff mouse 400 5 -3 120"},
          "no import to edit: a taken notch with motion reaches the game whole (motion kept), its message is kept:%s",
          game_text(r).c_str());
    r = run([] { raw_wheel(120); msg_wheel(120); }, nullptr);
    check(r.kiln.size() == 1 && r.game.empty(), "no import to edit: a record with only the taken notch is still kept:%s",
          game_text(r).c_str());
    hold(false);
    kui::g_consume_vk = 0;
    kui::g_consume_mods = ~0u;
}

void mouse_buttons() {
    fresh();
    Run r = run([] {
        send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_BUTTON_4_DOWN));
        send(WM_XBUTTONDOWN, MAKEWPARAM(MK_XBUTTON1, XBUTTON1), 0);
        send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_BUTTON_4_UP));
        send(WM_XBUTTONUP, MAKEWPARAM(0, XBUTTON1), 0);
    }, nullptr);
    check(presses(r, VK_XBUTTON1, true) == 1 && presses(r, VK_XBUTTON1, false) == 1 && r.kiln.size() == 2,
          "Mouse4 from raw input and WM_XBUTTONDOWN: ONE press and ONE release:%s", join(r.kiln).c_str());
    check(r.game.size() == 4, "Mouse4, not bound: the game gets all four (%zu):%s", r.game.size(), game_text(r).c_str());
    r = run([] {
        send(WM_XBUTTONDOWN, MAKEWPARAM(MK_XBUTTON2, XBUTTON2), 0);
        send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_BUTTON_5_DOWN));
        send(WM_XBUTTONUP, MAKEWPARAM(0, XBUTTON2), 0);
        send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_BUTTON_5_UP));
    }, nullptr);
    check(presses(r, VK_XBUTTON2, true) == 1 && presses(r, VK_XBUTTON2, false) == 1 && r.kiln.size() == 2,
          "Mouse5, message before raw: ONE press and ONE release:%s", join(r.kiln).c_str());
    r = run([] {
        send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_MIDDLE_BUTTON_DOWN));
        send(WM_MBUTTONDOWN, MK_MBUTTON, 0);
        send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_MIDDLE_BUTTON_UP));
        send(WM_MBUTTONUP, 0, 0);
        send(WM_XBUTTONDBLCLK, MAKEWPARAM(MK_XBUTTON1, XBUTTON1), 0);  // a double click is a press, not a repeat
        send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_BUTTON_4_DOWN));
        send(WM_XBUTTONUP, MAKEWPARAM(0, XBUTTON1), 0);
        send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_BUTTON_4_UP));
    }, nullptr);
    check(presses(r, VK_MBUTTON, true) == 1 && presses(r, VK_MBUTTON, false) == 1 && presses(r, VK_XBUTTON1, true) == 1 &&
              r.kiln.size() == 4 && !contains(r.kiln, "vk=5 scan=0 mods=0 down=1 repeat=1"),
          "MouseMiddle once each way; a double click of Mouse4 one press:%s", join(r.kiln).c_str());
    r = run([] {
        send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_LEFT_BUTTON_DOWN));
        send(WM_LBUTTONDOWN, MK_LBUTTON, 0);
        send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_RIGHT_BUTTON_DOWN | RI_MOUSE_LEFT_BUTTON_UP));
        send(WM_LBUTTONUP, 0, 0);
        send(WM_RBUTTONDOWN, MK_RBUTTON, 0);
        send(WM_RBUTTONUP, 0, 0);
        send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_RIGHT_BUTTON_UP));
    }, nullptr);
    check(r.kiln.empty() && r.game.size() == 7, "left and right clicks are never key presses (%zu) and all reach the game",
          r.kiln.size());

    // Shift+Mouse4 bound
    fresh();
    kui::g_consume_vk = VK_XBUTTON1;
    kui::g_consume_mods = KILN_UI_MOD_SHIFT;
    hold(false, true);
    r = run([] {
        send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_BUTTON_4_DOWN));
        send(WM_XBUTTONDOWN, MAKEWPARAM(MK_XBUTTON1 | MK_SHIFT, XBUTTON1), 0);
        send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_BUTTON_4_UP));
        send(WM_XBUTTONUP, MAKEWPARAM(MK_SHIFT, XBUTTON1), 0);
    }, nullptr);
    check(presses(r, VK_XBUTTON1, true) == 1 && r.kiln.size() == 2 && r.kiln[0].find("mods=2 ") != std::string::npos,
          "bound Shift+Mouse4: one press with Shift, one release:%s", join(r.kiln).c_str());
    check(r.game == std::vector<std::string>{"00ff mouse 80 0 0 0", "020c 10004 0"},
          "bound Shift+Mouse4: the game gets neither press, both releases:%s", game_text(r).c_str());
    r = run([] {
        send(WM_XBUTTONDOWN, MAKEWPARAM(MK_XBUTTON1 | MK_SHIFT, XBUTTON1), 0);
        send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_BUTTON_4_DOWN | RI_MOUSE_BUTTON_4_UP));  // a fast click in one record
        send(WM_XBUTTONUP, MAKEWPARAM(MK_SHIFT, XBUTTON1), 0);
    }, nullptr);
    check(presses(r, VK_XBUTTON1, true) == 1 && r.game == std::vector<std::string>{"020c 10004 0"},
          "bound Shift+Mouse4, message first and a press with its release in one record: one press, the game gets "
          "the release message only:%s", game_text(r).c_str());
    hold(false);
    r = run([] {
        send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_BUTTON_4_DOWN));
        send(WM_XBUTTONDOWN, MAKEWPARAM(MK_XBUTTON1, XBUTTON1), 0);
        send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_BUTTON_4_UP));
        send(WM_XBUTTONUP, MAKEWPARAM(0, XBUTTON1), 0);
    }, nullptr);
    check(presses(r, VK_XBUTTON1, true) == 1 && r.game.size() == 4, "Mouse4 without Shift: not taken, the game gets all four:%s",
          game_text(r).c_str());
    uint32_t mods = 0;
    kui::input_take_pressed(&mods);
    hold(false, true);
    send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_BUTTON_4_DOWN));
    send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_BUTTON_4_UP));
    send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_WHEEL, 0, 0, 120));
    check(kui::input_take_pressed(&mods) == 0, "take_pressed_key stays keyboard only (no mouse button, no notch)");
    hold(false);
    kui::g_consume_vk = 0;
    kui::g_consume_mods = ~0u;
}

void mouse_captured() {
    fresh();
    kui::g_menu_open = true;  // a Kiln screen has the input
    Run r = run([] {
        raw_wheel(120);
        msg_wheel(120);
        send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_BUTTON_4_DOWN));
        send(WM_XBUTTONDOWN, MAKEWPARAM(MK_XBUTTON1, XBUTTON1), 0);
        send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_BUTTON_4_UP));
        send(WM_XBUTTONUP, MAKEWPARAM(0, XBUTTON1), 0);
    }, nullptr);
    check(presses(r, KILN_UI_VK_WHEEL_UP) == 1 && presses(r, VK_XBUTTON1) == 1 && r.kiln.size() == 3 &&
              count_prefix(r.kiln, "vk=") == static_cast<size_t>(std::count_if(r.kiln.begin(), r.kiln.end(), [](const std::string& x) {
                  return x.find("captured=1") != std::string::npos;
              })),
          "Kiln captures input: one notch and one click, each once, with captured 1 (bindings do not fire on it; the Keys "
          "page takes it):%s", join(r.kiln).c_str());
    check(!game_has_raw_wheel(r) && !contains(r.game, "020a ") && !contains(r.game, "020b ") &&
              r.game == std::vector<std::string>{"00ff mouse 80 0 0 0", "020c 10000 0"},
          "Kiln captures input: the game gets no notch and no press, only the releases:%s", game_text(r).c_str());
    kui::g_menu_open = false;

    // a Kiln panel next to a game screen has the mouse over it: the wheel is the panel's, no press
    fresh();
    kui::g_hover_panels = 1;
    kui::g_want_mouse = true;
    kui::g_demand = 1;
    r = run([] { raw_wheel(120); msg_wheel(120); }, nullptr);
    check(r.kiln.empty() && r.game.empty(), "over a Kiln panel the wheel is the panel's: no press, not the game's:%s",
          join(r.kiln).c_str());
    kui::g_hover_panels = 0;
    kui::g_want_mouse = false;
    kui::g_demand = 0;
}

// ---- who gets mouse presses: subscribe_key (keyboard only, as before) and subscribe_key_ex(KILN_UI_KEYS_MOUSE) ----
struct KeyLog {
    std::vector<std::string> lines;
    uint32_t take_vk = 0;   // the press this subscriber takes
    bool take_all = false;  // a plugin that takes every press it gets
};
bool KILN_CALL key_collect(const KilnUiKey* k, void* user) {
    auto* log = static_cast<KeyLog*>(user);
    char b[48];
    std::snprintf(b, sizeof b, "vk=%u down=%u", k->vk, k->down);
    log->lines.push_back(b);
    return log->take_all || (k->down && k->vk == log->take_vk);
}
bool has_code_above_ff_or_button(const std::vector<std::string>& lines) {
    for (const auto& x : lines) {
        unsigned vk = 0;
        if (std::sscanf(x.c_str(), "vk=%u", &vk) == 1 && (vk > 0xFF || vk == VK_MBUTTON || vk == VK_XBUTTON1 || vk == VK_XBUTTON2))
            return true;
    }
    return false;
}
void play_mouse_all() {
    raw_wheel(120);
    msg_wheel(120);
    raw_wheel(-120);
    msg_wheel(-120);
    send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_BUTTON_4_DOWN));
    send(WM_XBUTTONDOWN, MAKEWPARAM(MK_XBUTTON1, XBUTTON1), 0);
    send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_BUTTON_4_UP));
    send(WM_XBUTTONUP, MAKEWPARAM(0, XBUTTON1), 0);
    send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_MIDDLE_BUTTON_DOWN));
    send(WM_MBUTTONDOWN, MK_MBUTTON, 0);
    send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_MIDDLE_BUTTON_UP));
    send(WM_MBUTTONUP, 0, 0);
}
struct Leaving {
    KilnHandle handle = 0;
    int calls = 0;
    KilnResult left = -1;
};
bool KILN_CALL take_and_leave(const KilnUiKey* k, void* user) {
    auto* l = static_cast<Leaving*>(user);
    ++l->calls;
    if (k->vk == VK_XBUTTON1 && k->down) {
        l->left = kui::keys_unsubscribe(l->handle);
        return true;
    }
    return false;
}
void mouse_subscribers() {
    fresh();
    // A plugin built before 0.2.0-dev.27 (subscribe_key) that takes every press it gets: alone, it changes nothing for
    // the mouse, and Kiln makes no mouse press at all.
    KeyLog old_plugin;
    old_plugin.take_all = true;
    KilnHandle old_h = 0, new_h = 0;
    check(kui::keys_subscribe(11, &key_collect, &old_plugin, 0, &old_h) == KILN_OK, "a subscribe_key subscriber");
    Run r = run(&play_mouse_all, nullptr);
    check(old_plugin.lines.empty(), "subscribe_key: no mouse press, no wheel code (%zu calls)", old_plugin.lines.size());
    check(r.kiln.empty(), "no KILN_UI_KEYS_MOUSE subscriber: no mouse press is made at all (%zu)", r.kiln.size());
    check(r.game.size() == 12 && count_prefix(r.game, "020a ") == 2 && game_has_raw_wheel(r),
          "...and the game gets every record and message untouched (%zu of 12):%s", r.game.size(), game_text(r).c_str());
    r = run([] {
        send(WM_INPUT, RIM_INPUT, raw_key(0x43, RI_KEY_MAKE, VK_F9));
        send(WM_INPUT, RIM_INPUT, raw_key(0x43, RI_KEY_BREAK, VK_F9));
    }, nullptr);
    check(old_plugin.lines == std::vector<std::string>{"vk=120 down=1", "vk=120 down=0"} && r.game.size() == 1,
          "subscribe_key still gets keyboard keys and still takes the press it wants:%s", join(old_plugin.lines).c_str());

    // Kiln.Ui's way: subscribe_key_ex with KILN_UI_KEYS_MOUSE, next to the old plugin in the same process.
    KeyLog bindings;
    bindings.take_vk = KILN_UI_VK_WHEEL_UP;  // a binding on WheelUp
    check(kui::keys_subscribe(12, &key_collect, &bindings, KILN_UI_KEYS_MOUSE, &new_h) == KILN_OK,
          "a subscribe_key_ex(KILN_UI_KEYS_MOUSE) subscriber");
    old_plugin.lines.clear();
    r = run(&play_mouse_all, nullptr);
    check(old_plugin.lines.empty(), "the subscribe_key subscriber still gets no mouse press (%zu calls), so it cannot take one",
          old_plugin.lines.size());
    check(!has_code_above_ff_or_button(old_plugin.lines), "...no wheel code and no button code reached it");
    check(bindings.lines == std::vector<std::string>{"vk=256 down=1", "vk=257 down=1", "vk=5 down=1", "vk=5 down=0", "vk=4 down=1",
                                                     "vk=4 down=0"},
          "the KILN_UI_KEYS_MOUSE subscriber gets each notch and click once:%s", join(bindings.lines).c_str());
    check(r.game.size() == 10 && count_prefix(r.game, "020a ") == 1 && count_prefix(r.game, "00ff mouse 400 ") == 1,
          "the notch it took is kept from the game (record and message), the rest passes (%zu of 12):%s", r.game.size(),
          game_text(r).c_str());
    bindings.lines.clear();
    old_plugin.take_all = false;
    send(WM_INPUT, RIM_INPUT, raw_key(0x43, RI_KEY_MAKE, VK_F9));
    send(WM_INPUT, RIM_INPUT, raw_key(0x43, RI_KEY_BREAK, VK_F9));
    check(bindings.lines.size() == 2 && old_plugin.lines.size() == 2, "a keyboard key reaches both");
    KeyLog bad;
    KilnHandle bad_h = 0;
    check(kui::keys_subscribe(13, &key_collect, &bad, 2, &bad_h) == KILN_ERR_INVALID_ARGUMENT &&
              kui::keys_subscribe(13, &key_collect, &bad, KILN_UI_KEYS_MOUSE | 0x80000000u, &bad_h) == KILN_ERR_INVALID_ARGUMENT &&
              kui::keys_subscribe(13, nullptr, &bad, KILN_UI_KEYS_MOUSE, &bad_h) == KILN_ERR_INVALID_ARGUMENT,
          "subscribe_key_ex refuses flags it does not know and a missing callback");

    // The last KILN_UI_KEYS_MOUSE subscriber gone: the mouse passes untouched again.
    check(kui::keys_unsubscribe(new_h) == KILN_OK && kui::keys_unsubscribe(new_h) == KILN_ERR_NOT_FOUND,
          "unsubscribe, once");
    fresh();
    r = run([] { raw_wheel(120); msg_wheel(120); }, nullptr);
    check(r.kiln.empty() && r.game.size() == 2, "no KILN_UI_KEYS_MOUSE subscriber left: no press, the game gets both (%zu)",
          r.game.size());

    // Review finding 5: the only mouse subscriber takes a raw Mouse4 press and leaves inside its callback. The press's
    // message comes after: the decision still stands, so the game gets neither form of the press.
    static Leaving leaving;
    leaving = {};
    check(kui::keys_subscribe(14, &take_and_leave, &leaving, KILN_UI_KEYS_MOUSE, &leaving.handle) == KILN_OK,
          "a sole mouse subscriber that leaves when it takes Mouse4");
    r = run([] {
        send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_BUTTON_4_DOWN));
        send(WM_XBUTTONDOWN, MAKEWPARAM(MK_XBUTTON1, XBUTTON1), 0);
        send(WM_INPUT, RIM_INPUT, raw_mouse(RI_MOUSE_BUTTON_4_UP));
        send(WM_XBUTTONUP, MAKEWPARAM(0, XBUTTON1), 0);
        raw_wheel(120);
        msg_wheel(120);
    }, nullptr);
    check(leaving.calls == 1 && leaving.left == KILN_OK, "it took the press and unsubscribed inside its callback (%d calls, %d)",
          leaving.calls, leaving.left);
    check(r.game == std::vector<std::string>{"00ff mouse 80 0 0 0", "020c 10000 0", "00ff mouse 400 0 0 120", "020a 780000 a000a"},
          "the press's message is kept as its record was; the releases and a later notch reach the game:%s", game_text(r).c_str());
    kui::keys_unsubscribe(old_h);
}

void mouse_key_names() {
    const struct {
        uint32_t vk;
        const char* name;
    } names[] = {{VK_MBUTTON, "MouseMiddle"}, {VK_XBUTTON1, "Mouse4"}, {VK_XBUTTON2, "Mouse5"},
                 {KILN_UI_VK_WHEEL_UP, "WheelUp"}, {KILN_UI_VK_WHEEL_DOWN, "WheelDown"},
                 {KILN_UI_VK_WHEEL_LEFT, "WheelLeft"}, {KILN_UI_VK_WHEEL_RIGHT, "WheelRight"}};
    for (const auto& n : names) {
        char b[32];
        kui::key_name(n.vk, b, sizeof b);
        check(std::strcmp(b, n.name) == 0, "key_name(0x%x) = %s (%s)", n.vk, n.name, b);
    }
}

}  // namespace

int main() {
    if (!patch_import("GetRawInputData", reinterpret_cast<void*>(&fake_raw), reinterpret_cast<void**>(&g_real_raw))) {
        std::printf("FAIL this exe imports no GetRawInputData to stand in for\n");
        return 1;
    }
    WNDCLASSW wc{};
    wc.lpfnWndProc = &game_proc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"KilnWindowInputGame";
    RegisterClassW(&wc);
    // Message-only: no display needed, nothing reaches a desktop.
    g_win = CreateWindowExW(0, wc.lpszClassName, L"window input", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, wc.hInstance, nullptr);
    check(g_win && !IsWindowVisible(g_win), "message-only window %p", static_cast<void*>(g_win));
    kui::input_attach(g_win);
    check(kui::input_attached(), "Kiln's window procedure is on the window");
    const RAWINPUTDEVICE keyboard{1, 6, 0, nullptr};
    check(RegisterRawInputDevices(&keyboard, 1, sizeof keyboard), "raw keyboard registered");

    same_for_game("play", &play_everything, kEverything);
    same_for_game("extended, repeated and double-click input", &play_edge_keys, kEdgeKeys);

    kui::g_menu_open = true;  // the Kiln menu: Kiln keeps presses, characters and clicks from the game
    same_for_game("menu open", &play_captured, kCaptured);
    const Run captured = run(&play_captured, nullptr);
    check(!contains(captured.game, "0100 ") && !contains(captured.game, "0201 "),
          "menu open: the game got no key press and no click (the records above were kept from it):%s", join(captured.game).c_str());
    kui::g_menu_open = false;
    kui::g_wheel_active = true;  // the radial wheel captures the same way
    same_for_game("wheel open", &play_captured, kCaptured);
    kui::g_wheel_active = false;

    kui::g_consume_vk = VK_INSERT;  // a key Kiln consumes (the menu key, a binding)
    same_for_game("consumed key", &play_consumed, kConsumed);
    const Run consumed = run(&play_consumed, nullptr);
    check(!contains(consumed.game, "0100 ") && contains(consumed.game, "0101 "),
          "consumed key: the game got its release only:%s", join(consumed.game).c_str());
    kui::g_consume_vk = 0;

    lifetime();
    raw_unregistered();
    held_focus_lost();
    nested_unsubscribe();
    recursive_unsubscribe();
    delivery_mutation();
    raw_boundaries();

    const RAWINPUTDEVICE mouse{1, 2, 0, nullptr};  // the game registers raw mouse input too
    check(RegisterRawInputDevices(&mouse, 1, sizeof mouse), "raw mouse registered");
    // Kiln.Ui's key subscriber asks for mouse presses (subscribe_key_ex, KILN_UI_KEYS_MOUSE); its bindings are played by
    // dispatch_key's consume rule above.
    KeyLog kiln_ui;
    KilnHandle kiln_ui_h = 0;
    check(kui::keys_subscribe(10, &key_collect, &kiln_ui, KILN_UI_KEYS_MOUSE, &kiln_ui_h) == KILN_OK,
          "Kiln.Ui's key subscriber, with KILN_UI_KEYS_MOUSE");
    mouse_mixed_without_hook();
    kui::input_install_iat();  // as at the game's start: the game's import slots, GetRawInputData among them
    mouse_wheel_presses();
    mouse_wheel_small_deltas();
    mouse_wheel_bound();
    mouse_buttons();
    mouse_captured();
    kui::keys_unsubscribe(kiln_ui_h);
    mouse_subscribers();
    mouse_key_names();

    std::printf("%s: %d checks, %d failed\n", g_fails ? "FAIL" : "PASS", g_checks, g_fails);
    return g_fails ? 1 : 0;
}
