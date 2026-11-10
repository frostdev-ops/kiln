// SPDX-License-Identifier: MIT
// kiln.progress v1 (abi/services/kiln_progress.h): a small window with a line of text and a progress bar for work that
// runs before the game's own window exists (kiln-eml's game-data phase can take minutes the first time).
//
// Plain Win32, no dependency. The window lives on a thread of its own with its own message loop, so the caller never
// waits for painting; the model (progress_model.h) is the only state the two sides share. The thread starts at the first
// begin() and then parks (no timer, no wake-up) whenever no task runs. A window is created only when a task has run for
// the delay (KILN_PROGRESS_DELAY_MS, default 2000 ms) and there is a desktop to show it on; it never takes the focus
// (WS_EX_NOACTIVATE, SW_SHOWNOACTIVATE, MA_NOACTIVATE) and is destroyed on its own thread when the last task ends, which
// end() waits for (at most 300 ms): the game's window must not appear with ours still on screen.
//
// No window, no error: with KILN_UI_DISABLE=1 (the switch of the overlay), on a dedicated server, in a window station
// that is not the interactive one (a service) and when the window cannot be created (Wine without a display); begin()
// returns 0 then and the other calls do nothing.
//
// Under gamescope (the Steam Deck's game mode, recognised by GAMESCOPE_WAYLAND_DISPLAY, or KILN_PROGRESS_FULLSCREEN=1)
// gamescope scales the one window it shows to the whole display, so the window covers the screen with the panel in the
// middle instead of a small box that would be blown up.
#include <atomic>
#include <chrono>
#include <thread>

#include "../common.h"
#include "../image.h"
#include "../log.h"
#include "../progress.h"
#include "../progress_model.h"
#include "service.h"

namespace kiln::progress {
namespace {

constexpr wchar_t kClass[] = L"KilnProgressWindow";
constexpr DWORD kPollMs = 100;      // the window follows the model at about ten frames a second
constexpr DWORD kCloseWaitMs = 300;  // end() waits this long for the window to be gone

uint64_t now_ms() {
    using namespace std::chrono;
    return static_cast<uint64_t>(duration_cast<milliseconds>(steady_clock::now().time_since_epoch()).count());
}

bool env_is(const wchar_t* name, const wchar_t* value) {
    wchar_t buf[8]{};
    const DWORD n = GetEnvironmentVariableW(name, buf, 8);
    return n > 0 && n < 8 && std::wstring_view(buf, n) == value;
}

bool env_set(const wchar_t* name) {
    wchar_t buf[2]{};
    return GetEnvironmentVariableW(name, buf, 2) > 0;
}

uint64_t delay_ms() {
    wchar_t buf[16]{};
    const DWORD n = GetEnvironmentVariableW(L"KILN_PROGRESS_DELAY_MS", buf, 16);
    if (n == 0 || n >= 16) return 2000;
    uint64_t v = 0;
    for (DWORD i = 0; i < n; ++i) {
        if (buf[i] < L'0' || buf[i] > L'9') return 2000;
        v = v * 10 + (buf[i] - L'0');
    }
    return std::min<uint64_t>(v, 60000);
}

// Is there an interactive desktop to show a window on? A service's window station is not visible: no window there.
bool desktop_available() {
    HWINSTA station = GetProcessWindowStation();
    if (station) {
        USEROBJECTFLAGS flags{};
        DWORD needed = 0;
        if (GetUserObjectInformationW(station, UOI_FLAGS, &flags, sizeof flags, &needed) && !(flags.dwFlags & WSF_VISIBLE))
            return false;
    }
    return GetSystemMetrics(SM_CXSCREEN) > 0 && GetSystemMetrics(SM_CYSCREEN) > 0;
}

std::wstring wide(const std::string& s) { return to_wide(s); }

UINT system_dpi() {
    using Fn = UINT(WINAPI*)();
    if (HMODULE u = GetModuleHandleW(L"user32.dll"))
        if (auto fn = reinterpret_cast<Fn>(GetProcAddress(u, "GetDpiForSystem"))) {
            const UINT dpi = fn();
            if (dpi >= 48) return dpi;
        }
    UINT dpi = 96;
    if (HDC dc = GetDC(nullptr)) {
        dpi = static_cast<UINT>(GetDeviceCaps(dc, LOGPIXELSX));
        ReleaseDC(nullptr, dc);
    }
    return dpi >= 48 ? dpi : 96;
}

constexpr COLORREF rgb(int r, int g, int b) { return RGB(r, g, b); }
constexpr COLORREF kBackdrop = rgb(0x0b, 0x10, 0x18);
constexpr COLORREF kPanel = rgb(0x12, 0x1a, 0x24);
constexpr COLORREF kBorder = rgb(0x2a, 0x3b, 0x4d);
constexpr COLORREF kTitle = rgb(0xe6, 0xee, 0xf7);
constexpr COLORREF kText = rgb(0x9f, 0xb3, 0xc8);
constexpr COLORREF kTrack = rgb(0x1d, 0x29, 0x37);
constexpr COLORREF kFill = rgb(0x35, 0xd0, 0xf0);  // the frostdev cyan

LRESULT CALLBACK wnd_proc(HWND, UINT, WPARAM, LPARAM);

class Ui {
public:
    uint64_t begin(std::string_view title) {
        if (!enabled()) return 0;
        ensure_thread();
        const uint64_t id = model_.begin(title, now_ms());
        if (id && wake_) SetEvent(wake_);
        return id;
    }

    void update(uint64_t id, std::string_view text, uint64_t done, uint64_t total) {
        if (id) model_.update(id, text, done, total);
    }

    void end(uint64_t id) {
        if (!id) return;
        model_.end(id);
        close_if_idle();
    }

    void end_all() {
        model_.end_all();
        close_if_idle();
    }

    // WM_PAINT (window thread): the whole client area in one go from a memory bitmap, so that nothing flickers.
    void paint(HWND hwnd);

private:
    bool enabled() {
        std::call_once(decided_, [this] {
            if (env_is(L"KILN_UI_DISABLE", L"1")) {
                KLOG_I("progress: disabled by KILN_UI_DISABLE=1");
            } else if (image().side == KILN_SIDE_SERVER) {
                // a dedicated server has nobody to show a window to; its progress goes to the log and kiln/state/eml.json
            } else if (!desktop_available()) {
                KLOG_D("progress: no desktop here; no window");
            } else {
                enabled_ = true;
            }
        });
        return enabled_;
    }

    void ensure_thread() {
        std::call_once(started_, [this] {
            wake_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);          // auto-reset: the window thread has a look
            closed_ = CreateEventW(nullptr, TRUE, TRUE, nullptr);          // manual-reset: set while no window exists
            fullscreen_ = env_is(L"KILN_PROGRESS_FULLSCREEN", L"1") || env_set(L"GAMESCOPE_WAYLAND_DISPLAY");
            delay_ = delay_ms();
            if (!wake_ || !closed_) return;
            std::thread([this] { run(); }).detach();
        });
    }

    void close_if_idle() {
        if (!wake_ || model_.active()) return;
        SetEvent(wake_);
        if (shown_.load(std::memory_order_acquire)) WaitForSingleObject(closed_, kCloseWaitMs);
    }

    HWND create_window() {
        HINSTANCE inst = GetModuleHandleW(nullptr);
        WNDCLASSEXW wc{};
        wc.cbSize = sizeof wc;
        wc.lpfnWndProc = &wnd_proc;
        wc.hInstance = inst;
        wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        wc.lpszClassName = kClass;
        if (!RegisterClassExW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return nullptr;
        dpi_ = system_dpi();
        const int screen_w = GetSystemMetrics(SM_CXSCREEN), screen_h = GetSystemMetrics(SM_CYSCREEN);
        RECT work{0, 0, screen_w, screen_h};
        SystemParametersInfoW(SPI_GETWORKAREA, 0, &work, 0);
        int w = scale(520), h = scale(112), x, y;
        if (fullscreen_) {
            x = 0;
            y = 0;
            w = screen_w;
            h = screen_h;
        } else {
            x = work.left + ((work.right - work.left) - w) / 2;
            y = work.top + ((work.bottom - work.top) - h) / 2;
        }
        return CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE, kClass, L"Kiln", WS_POPUP, x, y, w, h,
                               nullptr, nullptr, inst, nullptr);
    }

    int scale(int px) const { return MulDiv(px, static_cast<int>(dpi_), 96); }

    void run() {
        HWND hwnd = nullptr;
        bool window_failed = false;  // creating it failed once: this process has no usable desktop, do not retry
        uint64_t painted = ~0ull;
        for (;;) {
            const bool active = model_.active();
            DWORD timeout = INFINITE;
            if (active) timeout = hwnd ? kPollMs : 50;
            MsgWaitForMultipleObjects(1, &wake_, FALSE, timeout, QS_ALLINPUT);
            MSG msg;
            while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
            const View v = model_.view();
            if (!v.active) {
                if (hwnd) {
                    // shown_ goes false first: an end() that arrives after this has nothing to wait for
                    DestroyWindow(hwnd);
                    hwnd = nullptr;
                    shown_.store(false, std::memory_order_release);
                    SetEvent(closed_);
                    painted = ~0ull;
                }
                continue;
            }
            if (!hwnd && !window_failed && model_.due(now_ms(), delay_)) {
                ResetEvent(closed_);
                shown_.store(true, std::memory_order_release);
                hwnd = create_window();
                if (!hwnd) {
                    window_failed = true;
                    shown_.store(false, std::memory_order_release);
                    SetEvent(closed_);
                    KLOG_D("progress: the window could not be created (%lu); no window this start", GetLastError());
                } else {
                    ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                    SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                    painted = ~0ull;
                }
            }
            if (hwnd && (v.revision != painted || v.indeterminate)) {
                painted = v.revision;
                InvalidateRect(hwnd, nullptr, FALSE);
            }
        }
    }

public:
    using View = Model::View;

private:
    Model model_;
    std::once_flag decided_, started_;
    bool enabled_ = false;
    bool fullscreen_ = false;
    uint64_t delay_ = 2000;
    UINT dpi_ = 96;
    HANDLE wake_ = nullptr;
    HANDLE closed_ = nullptr;
    std::atomic<bool> shown_{false};
};

void Ui::paint(HWND hwnd) {
        PAINTSTRUCT ps;
        HDC dc = BeginPaint(hwnd, &ps);
        RECT client;
        GetClientRect(hwnd, &client);
        HDC mem = CreateCompatibleDC(dc);
        HBITMAP bmp = CreateCompatibleBitmap(dc, client.right, client.bottom);
        HGDIOBJ old_bmp = SelectObject(mem, bmp);

        const View v = model_.view();
        // the panel: the whole window, or a box in the middle of the backdrop
        RECT panel = client;
        if (fullscreen_) {
            HBRUSH back = CreateSolidBrush(kBackdrop);
            FillRect(mem, &client, back);
            DeleteObject(back);
            const int w = scale(520), h = scale(112);
            panel = {(client.right - w) / 2, (client.bottom - h) / 2, (client.right + w) / 2, (client.bottom + h) / 2};
        }
        HBRUSH fill = CreateSolidBrush(kPanel), edge = CreateSolidBrush(kBorder);
        FillRect(mem, &panel, edge);
        RECT inner = panel;
        InflateRect(&inner, -1, -1);
        FillRect(mem, &inner, fill);
        DeleteObject(fill);
        DeleteObject(edge);

        const int pad = scale(18);
        SetBkMode(mem, TRANSPARENT);
        HFONT title_font = CreateFontW(-scale(16), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS,
                                       CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_SWISS, L"Segoe UI");
        HFONT text_font = CreateFontW(-scale(13), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS,
                                      CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_SWISS, L"Segoe UI");
        HGDIOBJ old_font = SelectObject(mem, title_font);
        RECT line{panel.left + pad, panel.top + pad, panel.right - pad, panel.top + pad + scale(22)};
        SetTextColor(mem, kTitle);
        const std::wstring title = wide(v.title.empty() ? std::string("Kiln") : v.title);
        DrawTextW(mem, title.c_str(), static_cast<int>(title.size()), &line, DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);
        SelectObject(mem, text_font);
        SetTextColor(mem, kText);
        line.top = line.bottom + scale(4);
        line.bottom = line.top + scale(20);
        const std::wstring text = wide(v.text);
        DrawTextW(mem, text.c_str(), static_cast<int>(text.size()), &line, DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);

        // the bar
        const int bar_h = scale(8);
        RECT track{panel.left + pad, panel.bottom - pad - bar_h, panel.right - pad, panel.bottom - pad};
        HBRUSH track_brush = CreateSolidBrush(kTrack), fill_brush = CreateSolidBrush(kFill);
        FillRect(mem, &track, track_brush);
        const int width = track.right - track.left;
        RECT bar = track;
        if (v.indeterminate) {
            // a segment that runs back and forth: the work has no total to show
            const int seg = width / 4;
            const int period = 1600;
            const int t = static_cast<int>(GetTickCount64() % period);
            const int pos = (t < period / 2 ? t : period - t) * (width - seg) / (period / 2);
            bar.left = track.left + pos;
            bar.right = bar.left + seg;
        } else {
            bar.right = track.left + static_cast<int>(static_cast<int64_t>(width) * v.percent / 100);
        }
        if (bar.right > bar.left) FillRect(mem, &bar, fill_brush);
        DeleteObject(track_brush);
        DeleteObject(fill_brush);

        SelectObject(mem, old_font);
        DeleteObject(title_font);
        DeleteObject(text_font);
        BitBlt(dc, 0, 0, client.right, client.bottom, mem, 0, 0, SRCCOPY);
        SelectObject(mem, old_bmp);
        DeleteObject(bmp);
        DeleteDC(mem);
        EndPaint(hwnd, &ps);
}

Ui& ui() {
    static Ui* instance = new Ui;  // never destroyed: its thread may outlive static destruction
    return *instance;
}

LRESULT CALLBACK wnd_proc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
        case WM_PAINT:
            ui().paint(hwnd);
            return 0;
        case WM_ERASEBKGND:
            return 1;
        case WM_MOUSEACTIVATE:
            return MA_NOACTIVATE;
        case WM_NCHITTEST:
            return HTTRANSPARENT;  // clicks go through to whatever is under it
        case WM_CLOSE:
            return 0;  // only end() closes it
        default:
            return DefWindowProcW(hwnd, msg, wp, lp);
    }
}

std::string_view sv(KilnStr s) { return s.ptr && s.len ? std::string_view(s.ptr, s.len) : std::string_view(); }

uint64_t KILN_CALL p_begin(KilnStr title) {
    try {
        return ui().begin(sv(title));
    } catch (...) {
        return 0;  // a progress window must never take the start down
    }
}

void KILN_CALL p_update(uint64_t task, KilnStr text, uint64_t done, uint64_t total) {
    try {
        ui().update(task, sv(text), done, total);
    } catch (...) {
    }
}

void KILN_CALL p_end(uint64_t task) {
    try {
        ui().end(task);
    } catch (...) {
    }
}

const KilnProgressApi kTable = {{sizeof(KilnProgressApi), KILN_PROGRESS_VERSION}, &p_begin, &p_update, &p_end};

}  // namespace

const KilnProgressApi* table() { return &kTable; }

void close_all() {
    try {
        ui().end_all();
    } catch (...) {
    }
}

}  // namespace kiln::progress

KILN_SERVICE(progress, KILN_PROGRESS_SERVICE, KILN_PROGRESS_VERSION, &kiln::progress::kTable, nullptr);
