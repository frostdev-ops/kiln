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
//     a faulting subscriber is removed and the others go on.
// Lines: "PASS ...", "FAIL ...". Exit code 0 = no FAIL.
#include <windows.h>

#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

#include "ui.h"

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
// Kiln's own key handling (the menu key, bindings, the wheel): what reaches it, and one key it consumes.
std::vector<std::string> g_kiln_keys;
uint32_t g_consume_vk = 0;
bool dispatch_key(const KilnUiKey& k) {
    char b[64];
    std::snprintf(b, sizeof b, "vk=%u scan=%x down=%u repeat=%u captured=%u", k.vk, k.scan, k.down, k.repeat, k.captured);
    g_kiln_keys.push_back(b);
    return k.down && k.vk == g_consume_vk;
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
                    std::snprintf(b, sizeof b, "00ff mouse %x %ld %ld", ri->data.mouse.usButtonFlags, ri->data.mouse.lLastX, ri->data.mouse.lLastY);
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

    std::printf("%s: %d checks, %d failed\n", g_fails ? "FAIL" : "PASS", g_checks, g_fails);
    return g_fails ? 1 : 0;
}
