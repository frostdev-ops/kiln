// SPDX-License-Identifier: MIT
// kiln-shroudforge: the key state behind ShroudForge's mod shortcuts (shroudforge.input.is_keybind_down,
// was_keybind_pressed). ShroudForge's runtime reads it from a hidden helper, <game>/shroudforge/shroudforge.exe
// --keyboard-input, which Kiln's bundled component does not ship. A runtime built from Kiln's fork has seven optional
// exports instead (the fork's kiln/abi/input_host.h, "host key feed" v1): Kiln passes them the game window's own key,
// button and focus records from kiln.ui (subscribe_window_input, kiln_ui.h v5). docs/kiln/shroudforge-compat.md 5.6.
//
// Order (input_host.h): subscribe with forwarding still shut, begin before shroudforge_create (a 0 means
// ShroudForge's own input already runs: unsubscribe, feed nothing), forward, and at teardown unsubscribe (kiln.ui waits
// for a record in flight), end, then shroudforge_destroy. The fork keeps the feed's state in process-wide statics, not in
// the runtime handle, so end and destroy may come in either order; end first means the last export call is before
// destroy. The runtime DLL stays loaded for the life of the process (plugin.cpp never frees the chosen one).
//
// Threads and locks: records come on the game's window thread. It takes g_mu for one forwarded record, and the
// runtime's own feed lock inside the export; the mods loop never takes either (its queries read the shortcut state
// under the runtime's short locks). Teardown runs on the mods worker: unsubscribe holding nothing, then g_mu around end,
// so every export call is under g_mu (g_mu before the runtime's feed lock, always), and none comes after end. reset
// (about 500 GetKeyNameTextW calls) runs on the window thread only, once per focus loss (kiln.ui sends one record per
// loss).
#include <mutex>

#include "../../../services/ui/raw_input_source.h"
#include "services/kiln_ui.h"
#include "sf_internal.h"

namespace ksf {

namespace {

using VersionFn = uint32_t(__cdecl*)();
using BeginFn = uint32_t(__cdecl*)();
using RawKeyFn = void(__cdecl*)(uint32_t, uint32_t, uint32_t);
using WindowKeyFn = void(__cdecl*)(uintptr_t, intptr_t);
using MouseButtonFn = void(__cdecl*)(uint32_t, uint32_t);
using VoidFn = void(__cdecl*)();
constexpr uint32_t kFeedVersion = 1;

struct Feed {
    VersionFn version = nullptr;
    BeginFn begin = nullptr;
    RawKeyFn raw_key = nullptr;
    WindowKeyFn window_key = nullptr;
    MouseButtonFn mouse_button = nullptr;
    VoidFn reset = nullptr, end = nullptr;
};
Feed g_fn;
const KilnUiApi* g_ui = nullptr;
KilnHandle g_sub = 0;
std::mutex g_mu;
bool g_live = false;              // begin returned 1 and end has not been called (g_mu)
bool g_raw_keys = false;          // the window has given a raw keyboard record (g_mu)
bool g_raw_buttons = false;       // ...a raw mouse button record (g_mu)

// The exports are foreign code: a fault stops the feed instead of the window thread (POD-only frames).
template <class F, class... A> bool guarded(F fn, A... args) {
    __try { fn(args...); return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
template <class F> bool guarded_value(F fn, uint32_t* out) {
    __try { *out = fn(); return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

// One key goes through one of the two key functions (input_host.h): the raw record once the window gives raw keyboard
// input, the window message while it has given none. The window gets both for a key (the game registers raw input
// without RIDEV_NOLEGACY) and may take WM_INPUT ahead of the messages: a tap can arrive as raw press, raw release, then
// WM_KEYDOWN, WM_KEYUP, which forwarded both ways would be two presses, and Print Screen's lone WM_KEYUP after its raw
// release a second press too. Buttons the same way: raw button records, else the button messages. If the registration
// is removed or targets a different window, resume legacy input, including the release of an already held raw key.
void KILN_CALL on_window_input(const KilnUiWindowInput* r, void*) {
    if (!r || r->size < sizeof(KilnUiWindowInput)) return;
    std::lock_guard lock(g_mu);
    if (!g_live) return;
    bool ok = true;
    switch (r->kind) {
        case KILN_UI_INPUT_RAW_KEY:
            g_raw_keys = true;
            ok = guarded(g_fn.raw_key, r->make_code, r->flags, r->vkey);
            break;
        case KILN_UI_INPUT_WINDOW_KEY:
            if (g_raw_keys && kui::raw_input::registered_for(6, GetFocus())) return;
            g_raw_keys = false;
            ok = guarded(g_fn.window_key, static_cast<uintptr_t>(r->wparam), static_cast<intptr_t>(r->lparam));
            break;
        case KILN_UI_INPUT_MOUSE_BUTTON:
            if (r->message == WM_INPUT) g_raw_buttons = true;
            else {
                if (g_raw_buttons && kui::raw_input::registered_for(2, GetFocus())) return;
                g_raw_buttons = false;
            }
            ok = guarded(g_fn.mouse_button, r->button, r->down);
            break;
        case KILN_UI_INPUT_FOCUS_LOST:
            ok = guarded(g_fn.reset);
            break;
        default:
            return;
    }
    if (!ok) {
        g_live = false;  // no end: the runtime faulted inside its own feed
        logf(KILN_LOG_ERROR, "ShroudForge's host key feed faulted on the window thread: Kiln stops passing keys to it; "
                             "ShroudForge mod shortcuts do not fire for the rest of this session");
    }
}

}  // namespace

KeySource key_feed_start(const KilnApi* api, KilnOwner owner, HMODULE runtime, const std::filesystem::path& game,
                         bool server) {
    if (server) return {"none", "a dedicated server has no game window, so ShroudForge mods get no shortcuts"};
    std::error_code ec;
    const bool helper = std::filesystem::is_regular_file(game / L"shroudforge" / L"shroudforge.exe", ec);
    // Without the feed the runtime does what it always did: it starts the installed helper when a mod with a shortcut
    // loads, and without one the shortcuts never fire.
    const auto without = [&](const std::string& why) -> KeySource {
        if (helper)
            return {"helper", why + "; ShroudForge's own shroudforge.exe --keyboard-input gives the key state (the runtime "
                                    "starts it when a mod with a shortcut loads)"};
        return {"none", why + ", and no shroudforge.exe is installed: ShroudForge mods' shortcuts do not fire"};
    };
    wchar_t env[8] = L"";
    const DWORD n = GetEnvironmentVariableW(L"KILN_SF_KEY_FEED", env, 8);
    if (n > 0 && n < 8 && !_wcsicmp(env, L"off")) return without("KILN_SF_KEY_FEED=off");
    Feed fn;
    fn.version = reinterpret_cast<VersionFn>(GetProcAddress(runtime, "shroudforge_input_host_version"));
    fn.begin = reinterpret_cast<BeginFn>(GetProcAddress(runtime, "shroudforge_input_host_begin"));
    fn.raw_key = reinterpret_cast<RawKeyFn>(GetProcAddress(runtime, "shroudforge_input_host_raw_key"));
    fn.window_key = reinterpret_cast<WindowKeyFn>(GetProcAddress(runtime, "shroudforge_input_host_window_key"));
    fn.mouse_button = reinterpret_cast<MouseButtonFn>(GetProcAddress(runtime, "shroudforge_input_host_mouse_button"));
    fn.reset = reinterpret_cast<VoidFn>(GetProcAddress(runtime, "shroudforge_input_host_reset"));
    fn.end = reinterpret_cast<VoidFn>(GetProcAddress(runtime, "shroudforge_input_host_end"));
    if (!fn.version || !fn.begin || !fn.raw_key || !fn.window_key || !fn.mouse_button || !fn.reset || !fn.end)
        return without("this ShroudForge runtime has no host key feed");
    const auto* ui = static_cast<const KilnUiApi*>(api->service(KilnStr{KILN_UI_SERVICE, sizeof(KILN_UI_SERVICE) - 1}, 5));
    if (!ui || ui->header.version < 5 ||
        ui->header.size < offsetof(KilnUiApi, subscribe_window_input) + sizeof(void*) || !ui->subscribe_window_input ||
        !ui->unsubscribe)
        return without("kiln.ui v5 (the game window's input records) is not available in this start");
    uint32_t version = 0;
    if (!guarded_value(fn.version, &version) || version != kFeedVersion)
        return without("this runtime's host key feed is version " + std::to_string(version) + ", Kiln knows version " +
                       std::to_string(kFeedVersion));
    g_fn = fn;
    g_ui = ui;
    // Subscribed before begin, forwarding shut until begin said yes: a begin followed by end would leave the runtime
    // with neither the feed nor its helper.
    KilnHandle sub = 0;
    if (ui->subscribe_window_input(owner, &on_window_input, nullptr, &sub) != KILN_OK || !sub)
        return without("kiln.ui refused the window input subscription");
    g_sub = sub;
    uint32_t took = 0;
    const bool answered = guarded_value(fn.begin, &took);
    if (!answered || took != 1) {
        ui->unsubscribe(sub);
        g_sub = 0;
        if (answered && took == 0) return {"helper", "ShroudForge's own keyboard input already runs in this process"};
        return without(answered ? "the runtime's host key feed answered begin with " + std::to_string(took)
                                : "the runtime's host key feed faulted in begin");
    }
    {
        std::lock_guard lock(g_mu);
        g_raw_keys = g_raw_buttons = false;
        g_live = true;
    }
    return {"kiln", "Kiln passes the game window's key, mouse button and focus records to the runtime (host key feed v1); "
                    "this is not a check that a mod's shortcut fires"};
}

void key_feed_stop() {
    if (g_sub && g_ui) {
        const KilnResult r = g_ui->unsubscribe(g_sub);
        if (r != KILN_OK)
            logf(KILN_LOG_WARN, "kiln.ui did not remove ShroudForge's key subscription (%d); its records are dropped from "
                                "here on", r);
        g_sub = 0;
    }
    std::lock_guard lock(g_mu);
    if (!g_live) return;
    g_live = false;
    if (!guarded(g_fn.end)) logf(KILN_LOG_WARN, "ShroudForge's host key feed faulted when it was ended");
}

}  // namespace ksf
