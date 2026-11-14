// SPDX-License-Identifier: MIT
// progress_probe: the real kiln.progress service (core/src/services/progress.cpp), the core's logging, image and service
// registry replaced by stubs, driven the way kiln-eml drives it. Run under Wine on a virtual display (never the user's
// desktop): native/core/tests/test-progress-window.sh. Prints "PROBE ..." lines; the exit code is 0 when every check passed.
//
//   progress_probe show        a task runs 6 s; the window must appear after the delay, keep the focus where it was, show the
//                              bar and be gone when end() returns ("SHOT-READY" tells the script to take a screenshot)
//   progress_probe quick       a task shorter than the delay: no window may ever be created
//   progress_probe nobar       a task with no total: the window shows an indeterminate bar
//   progress_probe disabled    KILN_UI_DISABLE=1 (set by the script): begin() returns 0, nothing else happens
//   progress_probe server      the process is a dedicated server: begin() returns 0
//   progress_probe nowindow    no desktop to show a window on (the script has no display): no error, nothing crashes
//   progress_probe forget      a task that is never ended: close_all() (the early step's safety net) closes the window
#include <windows.h>

#include <chrono>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <string>
#include <thread>

#include "common.h"
#include "image.h"
#include "log.h"
#include "progress.h"
#include "progress_state.h"
#include "services/service.h"

// ---- stubs for the rest of kiln-core ----
// progress.json's writer (services/progress_file.cpp) is not part of this probe: the window is what it tests.
namespace kiln::progress_state {
void window_text(std::string_view) {}
KilnResult KILN_CALL api_step(KilnStr, KilnStr, KilnStr, uint64_t, uint64_t) { return KILN_OK; }
KilnResult KILN_CALL api_mark(uint32_t) { return KILN_OK; }
}  // namespace kiln::progress_state
namespace kiln {
extern uint32_t g_image_side_for_probe;
std::string to_utf8(std::wstring_view w) {
    if (w.empty()) return {};
    const int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), nullptr, 0, nullptr, nullptr);
    std::string s(static_cast<size_t>(n), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), s.data(), n, nullptr, nullptr);
    return s;
}
std::wstring to_wide(std::string_view s) {
    if (s.empty()) return {};
    const int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
    std::wstring w(static_cast<size_t>(n), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), w.data(), n);
    return w;
}
static ImageInfo g_image;
uint32_t g_image_side_for_probe = KILN_SIDE_CLIENT;
const ImageInfo& image() {
    g_image.side = g_image_side_for_probe;
    return g_image;
}
namespace log {
void writef(int level, const char* source, const char* fmt, ...) {
    char buf[512];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    std::printf("PROBE log %d [%s] %s\n", level, source, buf);
    std::fflush(stdout);
}
}  // namespace log
namespace svc {
Registrar::Registrar(Service*) {}
}  // namespace svc
}  // namespace kiln

static int failures = 0;
static void check(bool ok, const char* what) {
    std::printf("PROBE %s: %s\n", ok ? "ok" : "FAIL", what);
    std::fflush(stdout);
    if (!ok) ++failures;
}

static KilnStr str(const std::string& s) { return KilnStr{s.data(), s.size()}; }
static HWND find() { return FindWindowW(L"KilnProgressWindow", nullptr); }
static void sleep_ms(int ms) { std::this_thread::sleep_for(std::chrono::milliseconds(ms)); }

// A window of this process that has the focus, as the game's would: the progress window must not take it.
static HWND make_focus_window() {
    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"ProbeGame";
    RegisterClassW(&wc);
    HWND h = CreateWindowExW(0, L"ProbeGame", L"game", WS_OVERLAPPEDWINDOW | WS_VISIBLE, 40, 40, 400, 300, nullptr, nullptr, wc.hInstance, nullptr);
    if (h) {
        ShowWindow(h, SW_SHOW);
        SetForegroundWindow(h);
        SetFocus(h);
    }
    return h;
}

int main(int argc, char** argv) {
    const std::string mode = argc > 1 ? argv[1] : "show";
    const KilnProgressApi* api = kiln::progress::table();
    check(api && api->header.size >= sizeof(KilnProgressApi) && api->header.version == KILN_PROGRESS_VERSION, "the service table is there");
    if (mode == "server") kiln::g_image_side_for_probe = KILN_SIDE_SERVER;
    if (mode == "disabled" || mode == "server") {
        const uint64_t id = api->begin(str("Preparing"));
        check(id == 0, "begin() returns 0: nothing to show with");
        api->update(id, str("x"), 1, 2);
        api->end(id);
        check(find() == nullptr, "no window");
        return failures ? 1 : 0;
    }
    if (mode == "nowindow") {
        const uint64_t id = api->begin(str("Preparing the game data for EML mods"));
        std::printf("PROBE begin returned %llu\n", static_cast<unsigned long long>(id));
        for (int i = 0; i <= 10; ++i) {
            api->update(id, str("Checking the game data"), static_cast<uint64_t>(i), 10);
            sleep_ms(120);
        }
        check(find() == nullptr, "no window on a process without a desktop");
        api->end(id);
        check(true, "end() returned without error");
        return failures ? 1 : 0;
    }

    HWND game = make_focus_window();
    const HWND foreground_before = GetForegroundWindow();
    std::printf("PROBE game window %p, foreground %p\n", static_cast<void*>(game), static_cast<void*>(foreground_before));

    const uint64_t id = api->begin(str("Preparing the game data for EML mods"));
    check(id != 0, "begin() returns a task");
    if (mode == "quick") {
        for (int i = 0; i < 6; ++i) {
            api->update(id, str("Checking the game data"), static_cast<uint64_t>(i), 6);
            sleep_ms(50);
            check(find() == nullptr, "no window before the delay");
        }
        const auto t0 = std::chrono::steady_clock::now();
        api->end(id);
        check(std::chrono::steady_clock::now() - t0 < std::chrono::milliseconds(200), "end() of a task that never showed a window returns at once");
        sleep_ms(300);
        check(find() == nullptr, "no window ever");
        return failures ? 1 : 0;
    }

    const bool indeterminate = mode == "nobar";
    HWND win = nullptr;
    const auto t0 = std::chrono::steady_clock::now();
    bool announced = false;
    for (int i = 0; i < 60; ++i) {  // 6 s
        const uint64_t total = indeterminate ? 0 : 60ull << 30;
        const uint64_t done = indeterminate ? 0 : static_cast<uint64_t>(i) * (1ull << 30);
        char text[96];
        std::snprintf(text, sizeof text, indeterminate ? "Running the mods" : "Checking the game data: %d.0 of 60.0 GiB (%d%%)", i, i * 100 / 60);
        api->update(id, str(text), done, total);
        sleep_ms(100);
        if (!win) win = find();
        if (win && !announced && std::chrono::steady_clock::now() - t0 > std::chrono::milliseconds(1500)) {
            announced = true;
            std::printf("PROBE SHOT-READY\n");
            std::fflush(stdout);
            sleep_ms(1200);  // the script takes its screenshot now
        }
        if (mode == "forget" && i == 30) break;
    }
    check(win != nullptr, "the window appeared once the task had run for the delay");
    if (win) {
        RECT r{};
        GetWindowRect(win, &r);
        std::printf("PROBE window %ld,%ld %ldx%ld\n", r.left, r.top, r.right - r.left, r.bottom - r.top);
        const LONG ex = GetWindowLongW(win, GWL_EXSTYLE);
        check((ex & WS_EX_NOACTIVATE) != 0 && (ex & WS_EX_TOPMOST) != 0 && (ex & WS_EX_TOOLWINDOW) != 0, "no-activate, topmost tool window");
        check(IsWindowVisible(win) != 0, "visible");
        check(GetForegroundWindow() == foreground_before, "the keyboard focus stayed where it was");
    }
    const auto t1 = std::chrono::steady_clock::now();
    if (mode == "forget") {
        kiln::progress::close_all();
    } else {
        api->end(id);
    }
    const auto waited = std::chrono::steady_clock::now() - t1;
    check(find() == nullptr, "the window is gone when end() returns");
    check(waited < std::chrono::milliseconds(600), "and end() did not wait long for it");
    std::printf("PROBE end() took %lld ms\n", static_cast<long long>(std::chrono::duration_cast<std::chrono::milliseconds>(waited).count()));
    check(GetForegroundWindow() == foreground_before, "the focus is still the game's");
    // a second task starts a fresh delay: no window right away
    const uint64_t again = api->begin(str("Preparing"));
    sleep_ms(300);
    check(find() == nullptr, "a new task starts its own delay");
    api->end(again);
    return failures ? 1 : 0;
}
