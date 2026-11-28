// SPDX-License-Identifier: MIT
// kiln.client.game_input v1 (abi/services/kiln_client_game_input.h): the table, the live facts and the hooks. The rules
// are client_game_input.cpp's (host-tested); this file gives them the game:
//  - the samples: kiln.client's observation of the local input writer (client_input.h; one link shared with building
//    input, client/InputObserved), which hands each visit of the local player here (set_consumer);
//  - the stale press guard: one kiln.hooks mid link at client/InputMessageCopy, in the input message right before it
//    copies the record (r13 = the record, edi = its entity, r14d = the message's step);
//  - the gates, from kiln.ui (its screen and status) and the foreground window, at each client world Pre;
//  - the held item's kind from kiln.items' catalog and the bow's draw from the local player's NetworkRangedWeapon in the
//    client world (the game's GetComponent).
// Both links go in at the first last, subscribe or repeat of the process (the lazy producer, client_game_input.h),
// never at the start: a game without a mod that uses game input never has them.
#include <atomic>
#include <cstring>

#include "../api.h"
#include "../gates.h"
#include "../guard.h"
#include "../image.h"
#include "../log.h"
#include "../registry.h"
#include "../sigdb.h"
#include "../tick.h"
#include "client_game_input.h"
#include "client_input.h"
#include "hooks_chain.h"
#include "hooks_internal.h"
#include "services/kiln_client_game_input.h"
#include "services/kiln_items.h"
#include "services/kiln_ui.h"
#include "service.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

namespace kiln::hooks::seh {
bool copy(void* dst, const void* src, size_t n);  // hooks_seh.cpp: a write that may fault returns false
}  // namespace kiln::hooks::seh

namespace kiln::game_input_seh {
bool call(KilnGameInputFn fn, KilnGameInputSample* sample, void* user, unsigned long* code);
bool call_cond(KilnGameInputCondFn fn, const KilnGameInputSample* sample, void* user, bool* out, unsigned long* code);
bool get_component(void* fn, uintptr_t world, uint32_t hash, uint32_t entity, uintptr_t* out);
}  // namespace kiln::game_input_seh

namespace kiln {
namespace {

namespace gi = game_input;

constexpr const char* kCap = "client.game_input";
constexpr const char* kFeature = "client.game_input_sites";

uintptr_t g_message = 0;   // client/InputMessageCopy
void* g_get_component = nullptr;
gi::Layout g_layout_ready{};
// keen::ecs::NetworkRangedWeapon (sigdb client.game_input_sites constants)
struct RangedLayout {
    uint32_t hash, state, type, charge, start, item, size;
} g_rw{};
std::atomic<bool> g_live{false};  // the hooks are in: the client Pre reads the gate facts

// ---- the game, guarded ----
bool read(void* dst, uintptr_t src, size_t n) { return src && guard::copy(dst, src, n); }
bool write(uintptr_t dst, const void* src, size_t n) { return dst && hooks::seh::copy(reinterpret_cast<void*>(dst), src, n); }

bool ranged(uintptr_t world, KilnEntity entity, gi::Ranged* out) {
    uintptr_t c = 0;
    if (!g_get_component || !world || !entity ||
        !game_input_seh::get_component(g_get_component, world, g_rw.hash, entity, &c) || !c)
        return false;
    uint8_t raw[0x70] = {};
    if (g_rw.size > sizeof raw || !guard::copy(raw, c, g_rw.size)) return false;
    gi::Ranged r{};
    r.present = true;
    r.state = raw[g_rw.state];
    r.weapon_type = raw[g_rw.type];
    std::memcpy(&r.charge, raw + g_rw.charge, 8);
    std::memcpy(&r.start, raw + g_rw.start, 8);
    std::memcpy(&r.item, raw + g_rw.item, 4);
    *out = r;
    return true;
}

// kiln.items is published by kiln-items.dll, not an in-core service (ui_values_service.cpp asks the same way).
bool item(uint32_t id, gi::ItemFacts* out) {
    static std::atomic<const KilnItemsApi*> items{nullptr};
    const KilnItemsApi* t = items.load(std::memory_order_acquire);
    if (!t) {
        t = static_cast<const KilnItemsApi*>(api()->service(KilnStr{KILN_ITEMS_SERVICE, sizeof(KILN_ITEMS_SERVICE) - 1}, 1));
        if (!t || t->header.size < offsetof(KilnItemsApi, item_info) + sizeof(void*) || !t->item_info) return false;
        items.store(t, std::memory_order_release);
    }
    KilnItemInfo info{};
    info.size = sizeof info;
    if (t->item_info(id, &info) != KILN_OK) return false;
    gi::ItemFacts f{};
    f.found = true;
    f.equip_slot = info.equip_slot;
    for (size_t i = 0; info.name.ptr && i + 3 <= info.name.len; ++i)
        if (std::memcmp(info.name.ptr + i, "Bow", 3) == 0) f.bow_name = true;
    *out = f;
    return true;
}

bool call(KilnGameInputFn fn, KilnGameInputSample* sample, void* user) {
    unsigned long code = 0;
    return game_input_seh::call(fn, sample, user, &code);
}
bool call_cond(KilnGameInputCondFn fn, const KilnGameInputSample* sample, void* user, bool* out) {
    unsigned long code = 0;
    return game_input_seh::call_cond(fn, sample, user, out, &code);
}

const gi::Game kGame{&read, &write, &ranged, &item, &call, &call_cond};

uint32_t live_client_session() {
    KilnSessionInfo s{};
    s.size = sizeof s;
    if (tick::session(&s) != KILN_OK) return 0;
    const bool in_game = s.state == KILN_SESSION_HOST || s.state == KILN_SESSION_REMOTE;
    return in_game && s.worlds[KILN_WORLD_CLIENT] ? s.sessions[KILN_WORLD_CLIENT] : 0;
}

// kiln.client's observation, for the local player only (client_input.cpp's visit).
void on_visit(uintptr_t record, uintptr_t input, uintptr_t world, KilnEntity entity, uint32_t session, uint32_t version) {
    gi::observe({record, input, world, entity, session, version}, kGame);
}

// At client/InputMessageCopy: r13 = the record the message copies next, edi = its entity, r14d = the message's step.
// Reads registers only; may clear Kiln's own bits in the record (client_game_input.cpp, message()).
void KILN_CALL on_message(KilnRegisters* r, void*) {
    gi::message(r->r13, static_cast<KilnEntity>(r->rdi & 0xffffffffu), static_cast<uint32_t>(r->r14 & 0xffffffffu),
                live_client_session(), kGame);
}

// ---- the gates: kiln.ui's screen and status, and the foreground window, at each client world Pre ----
const KilnUiApi* ui_table() {
    static std::atomic<const KilnUiApi*> ui{nullptr};
    const KilnUiApi* t = ui.load(std::memory_order_acquire);
    if (t) return t;
    t = static_cast<const KilnUiApi*>(api()->service(KilnStr{KILN_UI_SERVICE, sizeof(KILN_UI_SERVICE) - 1}, 1));
    if (!t || t->header.size < offsetof(KilnUiApi, screen) + sizeof(void*) || !t->screen || !t->status) return nullptr;
    ui.store(t, std::memory_order_release);
    return t;
}

bool game_window_focused() {
    const HWND w = GetForegroundWindow();
    DWORD pid = 0;
    return w && GetWindowThreadProcessId(w, &pid) && pid == GetCurrentProcessId();
}

uint64_t g_faults_seen = 0;
bool g_gate_logged = false;

void KILN_CALL on_client_pre(const KilnTickInfo*, void*) {
    if (!g_live.load(std::memory_order_acquire)) return;
    gi::GateFacts f{};
    if (const KilnUiApi* ui = ui_table()) {
        KilnUiScreen screen{};
        screen.size = sizeof screen;
        KilnUiStatus status{};
        status.size = sizeof status;
        if (ui->screen(&screen) == KILN_OK && screen.available && ui->status(&status) == KILN_OK) {
            f.known = true;
            f.playing = screen.ui_state == KILN_UI_STATE_PLAY && live_client_session() != 0;
            f.hud = screen.hud_screen;
            f.kiln_ui = status.menu_open || status.capturing;
            f.focused = game_window_focused();
        }
    }
    gi::publish_gate(f);
    if (!f.known && !g_gate_logged) {
        g_gate_logged = true;
        KLOG_W("client.game_input: kiln.ui's screen state is not available (kiln-ui.dll missing or its feature ui.screens "
               "off on this build): game input subscribers are never asked (samples are still read)");
    }
    const uint64_t faults = gi::stats().sub_faults;
    if (faults != g_faults_seen) {
        KLOG_W("client.game_input: %llu game input subscriber(s) or repeat condition(s) faulted and were removed",
               static_cast<unsigned long long>(faults - g_faults_seen));
        g_faults_seen = faults;
    }
}

void KILN_CALL on_world(const KilnWorldEvent* ev, void*) {
    if (ev->kind == KILN_WORLD_CLIENT && ev->event == KILN_WORLD_STOPPING) gi::session_ended(ev->session);
}

void KILN_CALL on_owner_retiring(KilnOwner owner, void*) { gi::owner_gone(owner); }

// The lazy installer: the first use's thread, under client_game_input's install mutex. The guard first, so no
// synthetic press can go out before it is in; then the observation (shared with building input; it may be in already).
gi::Install install_producer(gi::Layout* layout, const char** why) {
    hooks::chain::Order o;
    o.label = "kiln.client.game_input/InputMessageCopy";
    static KilnHandle guard_link = 0;  // under the install mutex
    if (!guard_link) {
        uint32_t unchecked = 0;
        const KilnResult rc = hooks::ensure_ready()
            ? hooks::chain::install_mid(0, g_message, o, &on_message, nullptr, &guard_link, &unchecked)
            : KILN_ERR_UNAVAILABLE;
        if (rc != KILN_OK && unchecked) {
            guard_link = 0;
            KLOG_W("client.game_input: hooking the input message (client/InputMessageCopy) was refused: %u thread(s) "
                   "could not be checked; a later use tries again",
                   static_cast<unsigned>(unchecked));
            *why = "hooking the input message was refused on every attempt: threads that could not be checked (kiln.log "
                   "says which)";
            return gi::Install::Again;
        }
        if (rc != KILN_OK) {
            guard_link = 0;
            KLOG_E("client.game_input: hooking the input message (client/InputMessageCopy) failed (%d); game input stays "
                   "off until the game restarts", rc);
            *why = "hooking the input message failed at the first use (kiln.log says why)";
            return gi::Install::Failed;
        }
    }
    switch (client_input::ensure_link(why)) {
        case client_input::Link::Again:
            return gi::Install::Again;
        case client_input::Link::Failed:
            return gi::Install::Failed;
        case client_input::Link::Bound:
            break;
    }
    *layout = g_layout_ready;
    client_input::set_consumer(&on_visit);
    g_live.store(true, std::memory_order_release);
    KLOG_I("client.game_input: hooked at the first use (input message +0x%llx; digitalInput %u, play +0x%x, held item "
           "+0x%x, period +0x%x)",
           static_cast<unsigned long long>(g_message - image().base), layout->digital, layout->play, layout->held,
           layout->period);
    return gi::Install::Bound;
}

// The build's constants, each inside its type; nullptr when all resolved, else the reason.
const char* read_layout(gi::Layout* l) {
    struct Want {
        const char* name;
        int64_t lo, hi;
        int64_t value;
    };
    Want want[] = {
        {"client/cpi.digital", 0, 1536 - 8, 0},          {"client/input.play", 0, 0x10000, 0},
        {"client/play.held_item", 0, 0x100000, 0},       {"client/input.step", 0, 0x7ffffff0, 0},
        {"client/world.period", 0, 0x7ffffff0, 0},       {"client/pit.mainhand", 0, 63, 0},
        {"client/pit.mainhand_tap", 0, 63, 0},           {"client/pit.mainhand_hold", 0, 63, 0},
        {"client/pit.mainhand_release", 0, 63, 0},       {"client/pit.contextual", 0, 63, 0},
        {"client/pit.contextual_tap", 0, 63, 0},         {"client/pit.contextual_hold", 0, 63, 0},
        {"client/pit.block", 0, 63, 0},                  {"client/pit.evade", 0, 63, 0},
        {"client/pit.jump", 0, 63, 0},                   {"client/pit.ranged_arm", 0, 63, 0},
        {"client/pit.ranged_shoot", 0, 63, 0},           {"client/pit.ranged_shoot_tap", 0, 63, 0},
        {"client/nrw.hash", 1, 0xffffffffll, 0},         {"client/nrw.size", 0x40, 0x70, 0},
        {"client/nrw.state", 0, 0x6f, 0},                {"client/nrw.type", 0, 0x6f, 0},
        {"client/nrw.charge", 0, 0x68, 0},               {"client/nrw.start", 0, 0x68, 0},
        {"client/nrw.item", 0, 0x6c, 0},
    };
    for (Want& w : want)
        if (!sigdb::constant(w.name, &w.value) || w.value < w.lo || w.value > w.hi)
            return "a client.game_input_sites layout constant did not resolve or is outside its type";
    auto v = [&](size_t i) { return static_cast<uint32_t>(want[i].value); };
    auto b = [&](size_t i) { return static_cast<uint8_t>(want[i].value); };
    gi::Layout layout{};
    layout.digital = v(0);
    layout.play = v(1);
    layout.held = v(2);
    layout.step = v(3);
    layout.period = v(4);
    const gi::Bits bits{b(5), b(6), b(7), b(8), b(9), b(10), b(11), b(12), b(13), b(14), b(15), b(16), b(17)};
    if (!gi::make_layout(bits, &layout)) return "a client.game_input_sites action bit is above 63";
    g_rw = {v(18), v(20), v(21), v(22), v(23), v(24), v(19)};
    if (g_rw.charge + 8 > g_rw.size || g_rw.start + 8 > g_rw.size || g_rw.item + 4 > g_rw.size ||
        g_rw.state >= g_rw.size || g_rw.type >= g_rw.size)
        return "a client.game_input_sites NetworkRangedWeapon offset is outside the component";
    *l = layout;
    return nullptr;
}

// Once, from init() on the game client: readies the lazy producer (nothing is hooked here), or leaves it unbound with
// the reason kiln.caps shows and one kiln.log line.
void ready_producer() {
    const char* why = nullptr;
    uintptr_t gc = 0;
    if (!hooks_enabled()) {
        why = "hooks are off (safe idle)";
    } else if (!client_input::observation_ready(&why)) {
        // why: the observation point's own reason (no local player, no client.input_observation on this build)
    } else if (!sigdb::feature(kFeature) || !sigdb::symbol("client/InputMessageCopy", &g_message) || !g_message ||
               !sigdb::symbol("GetComponent", &gc) || !gc) {
        g_message = 0;
        gi::unbound_reason(nullptr);  // the default reason: no verified sites on this build
        KLOG_W("client.game_input: unavailable: sigdb %s is not resolved on this game build (GameInput stays off)",
               kFeature);
        return;
    } else if (gates::is_held(kCap)) {
        why = "held by the client gate until its guided client checks pass (kiln.json \"preview\" lifts it)";
    } else {
        why = read_layout(&g_layout_ready);
    }
    if (why) {
        gi::unbound_reason(why);
        KLOG_W("client.game_input: not available: %s", why);
        return;
    }
    g_get_component = reinterpret_cast<void*>(gc);
    gi::set_installer(&install_producer);
    KLOG_I("client.game_input: ready on this build (input message +0x%llx); hooked at the first use",
           static_cast<unsigned long long>(g_message - image().base));
}

gi::Env env() { return {image().side == KILN_SIDE_CLIENT, hooks_enabled(), live_client_session(), &reg::owner_alive}; }

KilnResult KILN_CALL c_last(KilnGameInputSample* out, uint32_t* reason) { return gi::last(env(), out, reason); }
KilnResult KILN_CALL c_subscribe(KilnOwner owner, KilnGameInputFn fn, void* user, KilnHandle* out) {
    return gi::subscribe(env(), owner, fn, user, out);
}
KilnResult KILN_CALL c_repeat(KilnOwner owner, uint32_t action, uint32_t interval_ms, KilnGameInputCondFn cond,
                              void* user, KilnHandle* out) {
    return gi::repeat(env(), owner, action, interval_ms, cond, user, out);
}
KilnResult KILN_CALL c_unsubscribe(KilnHandle handle) { return gi::unsubscribe(handle); }

const KilnClientGameInputApi kTable = {
    {sizeof(KilnClientGameInputApi), KILN_CLIENT_GAME_INPUT_VERSION},
    &c_last,
    &c_subscribe,
    &c_repeat,
    &c_unsubscribe,
};

bool init() {
    if (!tick::installed()) return false;
    if (image().side != KILN_SIDE_CLIENT) return true;  // the table answers: every member is unavailable on a server
    KilnHandle h = 0;
    tick::subscribe_world(0, &on_world, nullptr, &h);
    tick::subscribe_world_tick(0, KILN_WORLD_CLIENT, KILN_TICK_PRE, &on_client_pre, nullptr, &h);
    reg::add_owner_retiring(0, &on_owner_retiring, nullptr, &h);
    ready_producer();
    return true;
}

}  // namespace
}  // namespace kiln

KILN_SERVICE(client_game_input, KILN_CLIENT_GAME_INPUT_SERVICE, KILN_CLIENT_GAME_INPUT_VERSION, &kiln::kTable,
             &kiln::init);
