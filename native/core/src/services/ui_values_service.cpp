// SPDX-License-Identifier: MIT
// kiln.ui.values in kiln-core: the production environment of ui_values*.cpp (owners, kiln.caps rows, sigdb, kiln.hooks
// links, kiln.client's local player and session, kiln.ui's last screen, guarded game-memory access, the log), the SEH
// guard around providers, and the service's registration.
//
// Client only: on a dedicated server init() says no (there is no game UI to hook). On the client nothing is hooked at
// boot: each kind installs its links when the first provider of that kind registers and kiln.caps shows the kind's row
// available and not held by the client gate, so a row held until the owner's client pass never reaches game code.
#include <cstddef>
#include <cstring>
#include <string>

#include "../api.h"
#include "../common.h"
#include "../crash.h"
#include "../guard.h"
#include "../image.h"
#include "../log.h"
#include "../registry.h"
#include "../sigdb.h"
#include "hooks_internal.h"
#include "service.h"
#include "services/kiln_caps.h"
#include "services/kiln_client.h"
#include "services/kiln_ui.h"
#include "ui_values_internal.h"

namespace kiln::hooks::seh {
bool copy(void* dst, const void* src, size_t n);  // hooks_seh.cpp: a write that may fault returns false
}  // namespace kiln::hooks::seh

namespace kiln::uivalues {
namespace {

KilnOwner g_owner = 0;  // the service's own owner: its kiln.caps reports
std::atomic<const KilnUiApi*> g_ui{nullptr};

const KilnCapsApi* caps() { return static_cast<const KilnCapsApi*>(svc::find(KILN_CAPS_SERVICE, 1)); }

// A kind's hooks go in only when the client gate does not hold its row (native/core/gates/dev28-05a-value-providers
// .json) and the row is available (its sigdb feature resolved, nobody reported it off).
bool row_available(const char* name) {
    const KilnCapsApi* c = caps();
    const KilnStr n{name, std::strlen(name)};
    if (!c || kiln_caps_held(c, n)) return false;
    KilnCapRow row{};
    row.size = sizeof row;
    return c->find(n, &row) == KILN_OK && row.state == KILN_CAP_AVAILABLE;
}

void row_off(const char* name, const char* text) {
    const KilnCapsApi* c = caps();
    if (c && g_owner)
        c->report(g_owner, KilnStr{name, std::strlen(name)}, KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_FAULTED,
                  KilnStr{text, std::strlen(text)});
}

bool client_now(ClientNow* out) {
    static const auto* client = static_cast<const KilnClientApi*>(svc::find(KILN_CLIENT_SERVICE, 1));
    if (!client) return false;
    KilnClientState s{};
    s.size = sizeof s;
    if (client->state(&s) != KILN_OK) return false;
    out->local_player = s.local_player;
    out->session = s.client_session;
    out->step = s.client_steps;
    return true;
}

bool screen(KilnUiScreen* out) {
    const KilnUiApi* ui = g_ui.load(std::memory_order_acquire);
    if (!ui) {  // kiln-ui.dll publishes its table after the core started
        ui = static_cast<const KilnUiApi*>(api()->service(KilnStr{KILN_UI_SERVICE, sizeof(KILN_UI_SERVICE) - 1}, 1));
        if (!ui || ui->header.size < offsetof(KilnUiApi, screen) + sizeof(void*)) return false;
        g_ui.store(ui, std::memory_order_release);
    }
    out->size = sizeof(KilnUiScreen);
    return ui->screen(out) == KILN_OK;
}

KilnResult hook_raw(uintptr_t target, const char* label, int32_t priority, void* fn, void** original) {
    if (!target || !hooks::ensure_ready()) return KILN_ERR_UNAVAILABLE;
    hooks::chain::Order o;
    o.priority = priority;
    o.label = label;
    KilnHandle h = 0;
    return hooks::chain::install_raw(0, target, o, fn, original, &h);
}

KilnResult hook_mid(uintptr_t target, const char* label, KilnMidFn fn) {
    if (!target || !hooks::ensure_ready()) return KILN_ERR_UNAVAILABLE;
    hooks::chain::Order o;
    o.label = label;
    KilnHandle h = 0;
    return hooks::chain::install_mid(0, target, o, fn, nullptr, &h);
}

// Holds no C++ object with a destructor, as __try requires (guard.cpp's pattern).
bool call_value(KilnUiValueFn fn, const KilnUiProviderContext* c, const KilnUiItemView* v, const KilnUiBarValue* in,
                KilnUiBarEdit* e, void* user, KilnResult* rc) {
    crash::Capture cap{};
    unsigned long code = 0;
    __try {
        *rc = fn(c, v, in, e, user);
        return true;
    } __except (crash::filter(GetExceptionInformation(), &cap, &code)) {
        crash::report(cap, "kiln.ui.values provider", reinterpret_cast<const void*>(fn));
        return false;
    }
}

bool call_changed(KilnUiValueChangedFn fn, const KilnUiValueChange* c, void* user) {
    crash::Capture cap{};
    unsigned long code = 0;
    __try {
        fn(c, user);
        return true;
    } __except (crash::filter(GetExceptionInformation(), &cap, &code)) {
        crash::report(cap, "kiln.ui.values change subscriber", reinterpret_cast<const void*>(fn));
        return false;
    }
}

bool call_lines(KilnUiLinesFn fn, const KilnUiProviderContext* c, const KilnUiItemView* v, KilnUiLineWriter* w,
                void* user, KilnResult* rc) {
    crash::Capture cap{};
    unsigned long code = 0;
    __try {
        *rc = fn(c, v, w, user);
        return true;
    } __except (crash::filter(GetExceptionInformation(), &cap, &code)) {
        crash::report(cap, "kiln.ui.values lines provider", reinterpret_cast<const void*>(fn));
        return false;
    }
}

bool call_slots(KilnUiSlotsFn fn, const KilnUiSlotsFrame* f, const KilnUiVisibleSlot* s, uint32_t n, void* user) {
    crash::Capture cap{};
    unsigned long code = 0;
    __try {
        fn(f, s, n, user);
        return true;
    } __except (crash::filter(GetExceptionInformation(), &cap, &code)) {
        crash::report(cap, "kiln.ui.values slots subscriber", reinterpret_cast<const void*>(fn));
        return false;
    }
}

// kiln.ui's UI tick (after every game UI frame, on the UI thread) drives change publication.
void (*g_frame_fn)(uint64_t, void*) = nullptr;
void* g_frame_user = nullptr;
void KILN_CALL on_ui_tick(const KilnUiScreen* now, void*) {
    if (g_frame_fn && now) g_frame_fn(now->frame, g_frame_user);
}
bool subscribe_frames(void (*fn)(uint64_t, void*), void* user) {
    KilnUiScreen probe{};
    if (!screen(&probe)) return false;  // also finds kiln.ui's table
    const KilnUiApi* ui = g_ui.load(std::memory_order_acquire);
    if (!ui || ui->header.size < offsetof(KilnUiApi, subscribe_ui_tick) + sizeof(void*) || !g_owner) return false;
    g_frame_fn = fn;
    g_frame_user = user;
    KilnHandle h = 0;
    return ui->subscribe_ui_tick(g_owner, &on_ui_tick, nullptr, &h) == KILN_OK;
}

void wire() {
    Env& e = env();
    e.owner_name = [](KilnOwner o) { return reg::owner_name(o); };
    e.owner_alive = [](KilnOwner o) { return reg::owner_alive(o); };
    e.registry_version = [] { return reg::version(); };
    e.now_ms = [] { return kiln::now_ms(); };
    e.tick_ms = []() -> uint64_t { return GetTickCount64(); };
    e.client_side = [] { return image().side == KILN_SIDE_CLIENT; };
    e.row_available = &row_available;
    e.row_off = &row_off;
    e.client = &client_now;
    e.screen = &screen;
    e.read = [](void* dst, uintptr_t src, size_t n) { return src != 0 && guard::copy(dst, src, n); };
    e.write = [](uintptr_t dst, const void* src, size_t n) {
        return dst != 0 && hooks::seh::copy(reinterpret_cast<void*>(dst), src, n);
    };
    e.symbol = [](const char* n, uintptr_t* out) { return sigdb::symbol(n, out) && *out != 0; };
    e.constant = [](const char* n, int64_t* out) { return sigdb::constant(n, out); };
    e.hook_raw = &hook_raw;
    e.hook_mid = &hook_mid;
    e.call_value = &call_value;
    e.call_changed = &call_changed;
    e.call_slots = &call_slots;
    e.call_lines = &call_lines;
    e.subscribe_frames = &subscribe_frames;
    e.log = [](int level, const char* text) { kiln::log::write(level, "kiln", text); };
}

bool init() {
    if (image().side != KILN_SIDE_CLIENT) {
        KLOG_I("ui.values: client only, not started on a server");
        return false;
    }
    wire();
    g_owner = reg::owner_create("kiln.ui.values");
    // Each feature resolves on its own; the rows (and the client gate) decide at registration time.
    static const char* const kFeatures[] = {"ui.values.detail", "ui.values.slot",          "ui.values.held",
                                            "ui.values.hud",    "ui.values.tooltip_lines", "ui.slots.inspect"};
    std::string on, off;
    for (const char* f : kFeatures) {
        std::string& to = sigdb::feature(f) ? on : off;
        to += ' ';
        to += f;
    }
    KLOG_I(
        "ui.values: features resolved:%s; not resolved:%s. A kind hooks the game when its first provider "
        "registers and its row is available",
        on.empty() ? " none" : on.c_str(), off.empty() ? " none" : off.c_str());
    return true;
}

}  // namespace
}  // namespace kiln::uivalues

KILN_SERVICE(ui_values, KILN_UI_VALUES_SERVICE, KILN_UI_VALUES_VERSION, kiln::uivalues::table(), &kiln::uivalues::init);
