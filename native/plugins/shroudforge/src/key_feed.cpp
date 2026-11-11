// SPDX-License-Identifier: MIT
// kiln-shroudforge: the key state behind ShroudForge's mod shortcuts (shroudforge.input.is_keybind_down,
// was_keybind_pressed). ShroudForge's runtime reads it from a hidden helper, <game>/shroudforge/shroudforge.exe
// --keyboard-input, which Kiln's bundled component does not ship. A runtime built from Kiln's fork has seven optional
// exports instead (the fork's kiln/abi/input_host.h, "host key feed" v1): Kiln passes them the game window's own key,
// button and focus records from kiln.ui (subscribe_window_input, kiln_ui.h v5). docs/kiln/shroudforge-compat.md 5.6.
//
// Order (input_host.h): begin before shroudforge_create (a 0 means ShroudForge's own input already runs: feed
// nothing), forward, and at teardown unsubscribe (kiln.ui waits for a record in flight), end, then shroudforge_destroy.
// The fork keeps the feed's state in process-wide statics, not in the runtime handle, so end and destroy may come in
// either order; end first means the last export call is before destroy. The runtime DLL stays loaded for the life of the
// process (plugin.cpp never frees the chosen one).
//
// When. In a real start the worker, and so shroudforge_create, starts from the early on_ready (kiln_plugin_early), before
// Kiln has loaded any service; kiln.ui is published by kiln-ui.dll at Kiln's plugin step, whose on_ready comes later on
// the same boot thread, and never in safe idle. So key_feed_start decides begin at the early on_ready and key_feed_attach
// subscribes at the plugin step:
//   - kiln.ui already there (a start without the early pass, the old test order): subscribe with forwarding still shut,
//     then begin, as one step ("kiln").
//   - not yet, no shroudforge.exe installed, and kiln.hooks served to the early plugins (the core serves it on every
//     start that may hook, so this start has a plugin step): begin now ("waiting"), then attach subscribes ("kiln") or
//     says why it cannot ("none"). Nothing is lost: without the feed there are no shortcuts at all. No record is passed
//     before the attach; the game window does not exist yet.
//   - not yet, shroudforge.exe installed: no begin. begin gives up the helper for the session (end does not bring it
//     back) and kiln.ui may not come (a kiln-ui.dll that did not load or is older, policy "any" on a new game build):
//     the helper keeps the key state ("helper").
//   - not yet, and kiln.hooks not served: a safe idle start, which has no plugin step: no begin ("none" or "helper").
//
// Threads and locks: records come on the game's window thread. It takes g_mu for one forwarded record, and the
// runtime's own feed lock inside the export; the mods loop never takes either (its queries read the shortcut state
// under the runtime's short locks). The attach runs on the boot thread while the worker may already be inside
// shroudforge_create or its loop: it subscribes holding nothing and makes the subscription live under g_mu only while
// teardown has not begun; otherwise it removes its own subscription again, holding nothing. Teardown runs on the mods
// worker: take the subscription under g_mu, unsubscribe holding nothing, then g_mu around end, so every export call is
// under g_mu (g_mu before the runtime's feed lock, always), and none comes after end. Whichever of attach and teardown
// first finds the feed waiting gives the final word on it; the other says nothing. reset (about 500 GetKeyNameTextW
// calls) runs on the window thread only, once per focus loss (kiln.ui sends one record per loss).
#include <atomic>
#include <mutex>

#include "../../../services/ui/raw_input_source.h"
#include "kiln_hook.hpp"
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
std::mutex g_mu;
const KilnUiApi* g_ui = nullptr;  // the subscription's kiln.ui (g_mu)
KilnHandle g_sub = 0;             // (g_mu)
bool g_begun = false;             // begin returned 1 and end is still owed (g_mu)
bool g_live = false;              // begun and subscribed: records are forwarded (g_mu)
bool g_waiting = false;           // begun at the early on_ready, the plugin step's attach still to come (g_mu)
// Teardown has begun: records are dropped before they ask for g_mu. A window thread that delivers without a pause (and
// a kiln.ui that could not remove the subscription) would otherwise keep taking the lock ahead of the worker's end.
std::atomic<bool> g_stopping{false};
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
    if (g_stopping.load(std::memory_order_acquire)) return;
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
        g_live = g_begun = false;  // no end: the runtime faulted inside its own feed
        logf(KILN_LOG_ERROR, "ShroudForge's host key feed faulted on the window thread: Kiln stops passing keys to it; "
                             "ShroudForge mod shortcuts do not fire for the rest of this session");
    }
}

// kiln.ui v5 with the window input subscription, or null.
const KilnUiApi* window_input(const KilnApi* api) {
    const auto* ui = static_cast<const KilnUiApi*>(api->service(KilnStr{KILN_UI_SERVICE, sizeof(KILN_UI_SERVICE) - 1}, 5));
    if (!ui || ui->header.version < 5 ||
        ui->header.size < offsetof(KilnUiApi, subscribe_window_input) + sizeof(void*) || !ui->subscribe_window_input ||
        !ui->unsubscribe)
        return nullptr;
    return ui;
}

constexpr char kNoHelper[] = ", and no shroudforge.exe is installed: ShroudForge mods' shortcuts do not fire";

}  // namespace

KeySource key_feed_start(const KilnApi* api, KilnOwner owner, HMODULE runtime, const std::filesystem::path& game,
                         bool server, bool plugin_step_later) {
    if (server) return {"none", "a dedicated server has no game window, so ShroudForge mods get no shortcuts"};
    std::error_code ec;
    const bool helper = std::filesystem::is_regular_file(game / L"shroudforge" / L"shroudforge.exe", ec);
    // Without the feed the runtime does what it always did: it starts the installed helper when a mod with a shortcut
    // loads, and without one the shortcuts never fire.
    const auto without = [&](const std::string& why) -> KeySource {
        if (helper)
            return {"helper", why + "; ShroudForge's own shroudforge.exe --keyboard-input gives the key state (the runtime "
                                    "starts it when a mod with a shortcut loads)"};
        return {"none", why + kNoHelper};
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
    const KilnUiApi* ui = window_input(api);
    if (!ui) {
        if (!plugin_step_later) return without("kiln.ui v5 (the game window's input records) is not available in this start");
        if (!kiln::hooks_v2(api))
            return without("this start has no Kiln plugin step (safe idle: kiln.hooks is not served to early plugins), so "
                           "kiln.ui v5 (the game window's input records) does not come");
        if (helper)
            return without("kiln.ui v5 (the game window's input records) comes only at Kiln's plugin step, after "
                           "ShroudForge's runtime starts, and a begun feed cannot hand the key state back to the helper "
                           "if it does not come");
    }
    uint32_t version = 0;
    if (!guarded_value(fn.version, &version) || version != kFeedVersion)
        return without("this runtime's host key feed is version " + std::to_string(version) + ", Kiln knows version " +
                       std::to_string(kFeedVersion));
    g_fn = fn;
    // kiln.ui already there: subscribed before begin, forwarding shut until begin said yes, so that a refused
    // subscription never leaves a begun runtime without its helper.
    KilnHandle sub = 0;
    if (ui && (ui->subscribe_window_input(owner, &on_window_input, nullptr, &sub) != KILN_OK || !sub))
        return without("kiln.ui refused the window input subscription");
    uint32_t took = 0;
    const bool answered = guarded_value(fn.begin, &took);
    if (!answered || took != 1) {
        if (sub) ui->unsubscribe(sub);
        if (answered && took == 0) return {"helper", "ShroudForge's own keyboard input already runs in this process"};
        return without(answered ? "the runtime's host key feed answered begin with " + std::to_string(took)
                                : "the runtime's host key feed faulted in begin");
    }
    {
        std::lock_guard lock(g_mu);
        g_raw_keys = g_raw_buttons = false;
        g_ui = ui;
        g_sub = sub;
        g_begun = true;
        g_live = ui != nullptr;
        g_waiting = ui == nullptr;
        g_stopping.store(false, std::memory_order_release);
    }
    if (!ui)
        return {"waiting", "the runtime's host key feed has begun (before ShroudForge's mods load, so it starts no helper); "
                           "Kiln passes the game window's records once kiln.ui v5 is published at Kiln's plugin step, and "
                           "none before: no shortcut fires until then"};
    return {"kiln", "Kiln passes the game window's key, mouse button and focus records to the runtime (host key feed v1); "
                    "this is not a check that a mod's shortcut fires"};
}

bool key_feed_attach(const KilnApi* api, KilnOwner owner, KeySource* out) {
    {
        std::lock_guard lock(g_mu);
        if (!g_waiting || g_stopping.load(std::memory_order_acquire)) return false;
    }
    const KilnUiApi* ui = window_input(api);
    KilnHandle sub = 0;
    const bool subscribed = ui && ui->subscribe_window_input(owner, &on_window_input, nullptr, &sub) == KILN_OK && sub;
    bool decided = false, attached = false;
    {
        std::lock_guard lock(g_mu);
        if (g_waiting && !g_stopping.load(std::memory_order_acquire)) {
            decided = true;
            g_waiting = false;
            if (subscribed) {
                g_raw_keys = g_raw_buttons = false;
                g_ui = ui;
                g_sub = sub;
                g_live = true;
                attached = true;
            }
        }
    }
    // The teardown came first: it ended the feed and gives the word; this subscription must not stay behind.
    if (subscribed && !attached) ui->unsubscribe(sub);
    if (!decided) return false;
    if (attached)
        *out = {"kiln", "Kiln passes the game window's key, mouse button and focus records to the runtime (host key feed "
                        "v1: begun before ShroudForge's mods loaded, records from Kiln's plugin step on); this is not a "
                        "check that a mod's shortcut fires"};
    else if (!ui)
        *out = {"none", std::string("the runtime's host key feed had begun, but kiln.ui v5 (the game window's input "
                                    "records) is not there at Kiln's plugin step either") + kNoHelper};
    else
        *out = {"none", std::string("kiln.ui refused the window input subscription at Kiln's plugin step, after the "
                                    "runtime's host key feed had begun") + kNoHelper};
    return true;
}

bool key_feed_stop(KeySource* out) {
    g_stopping.store(true, std::memory_order_release);
    const KilnUiApi* ui = nullptr;
    KilnHandle sub = 0;
    {
        std::lock_guard lock(g_mu);
        ui = g_ui;
        sub = g_sub;
        g_sub = 0;
    }
    if (sub && ui) {
        const KilnResult r = ui->unsubscribe(sub);
        if (r != KILN_OK)
            logf(KILN_LOG_WARN, "kiln.ui did not remove ShroudForge's key subscription (%d); its records are dropped from "
                                "here on", r);
    }
    std::lock_guard lock(g_mu);
    const bool waited = g_waiting;
    g_waiting = false;
    if (waited)
        *out = {"none", std::string("ShroudForge's runtime stopped before Kiln's plugin step attached its host key feed: no "
                                    "key records were passed") + kNoHelper};
    if (g_begun) {
        g_begun = g_live = false;
        if (!guarded(g_fn.end)) logf(KILN_LOG_WARN, "ShroudForge's host key feed faulted when it was ended");
    }
    return waited;
}

}  // namespace ksf
