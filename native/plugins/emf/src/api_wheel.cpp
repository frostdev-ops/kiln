// SPDX-License-Identifier: MIT
// The EmfApi table, v10: the radial wheel (wheel_open), drawn by kiln.ui's wheel (native/services/ui/wheel.cpp) in the
// game client.
//  - EMF's definition (EmfWheel in emf_api.h): 1..64 entries, 8 per page (the mouse wheel turns the page), a hold key or a
//    click picks, Esc or a right click cancel, the result is the index or -1. kiln.ui does the drawing, the input and
//    those rules; v26_model.cpp's copy_wheel_def checks and copies the mod's definition as EMF does.
//  - The result reaches the mod's callback after the next step of a world of def->world_kind, on that world's thread
//    (EMF's rule), as the window events do: kiln.ui calls back on the render thread, kiln-emf queues it.
//  - Refused with false (logged) as EMF documents: a bad definition, another wheel open, the overlay unable to draw. A
//    dedicated server has no overlay, so wheel_open is always refused there. Also refused: no world of def->world_kind
//    runs (its callback could never come), and the Kiln menu is open (it has the input, as EMF's console would).
//  - When the world ends while the wheel is open it is closed without a callback, and picks not yet delivered are dropped
//    (the callback needs the world).
//  - Not reproduced: EMF hides a hold key that is still down when the wheel closes from the mods that poll the keyboard
//    ("reads as up to mods until it is released"); kiln-emf cannot change what a mod's own GetAsyncKeyState sees.
#include <intrin.h>

#include <deque>
#include <optional>

#include "emf_internal.h"
#include "v26_model.h"

namespace kemf {

namespace {

struct Open {
    KilnHandle handle = 0;
    Mod* mod = nullptr;
    v26::WheelRequest req;
    uint64_t serial = 0;
};
struct Result {
    Mod* mod;
    EmfWheelCallback cb;
    void* user;
    uint8_t world_kind;
    int choice;
};

std::mutex g_mutex;
std::optional<Open> g_open;  // the wheel kiln-emf has open (kiln.ui allows one)
std::deque<Result> g_results;  // ended, waiting for a step of their world
bool g_stopping[KILN_WORLD_KINDS]{};
uint64_t g_serial = 0;
std::atomic<bool> g_shutdown{false};

const char* no_overlay() {
    return g_side == KILN_SIDE_SERVER ? "a dedicated server has no overlay (keep chat commands for players)"
                                      : "the in-game overlay is off (kiln.ui is not available)";
}

// kiln.ui's callback, on the render thread: queue the result for the world's thread.
void KILN_CALL on_wheel(int32_t choice, void* user) {
    std::lock_guard lock(g_mutex);
    if (!g_open || g_open->serial != reinterpret_cast<uintptr_t>(user)) return;
    Result r{g_open->mod, g_open->req.cb, g_open->req.user, g_open->req.world_kind, choice};
    g_open.reset();
    if (g_shutdown || g_stopping[r.world_kind]) return;
    if (g_results.size() >= 16) g_results.pop_front();
    g_results.push_back(r);
}

bool api_wheel_open(const EmfWheel* def) {
    Mod* m = caller_mod(_ReturnAddress());
    v26::WheelRequest r;
    std::string why;
    if (!v26::copy_wheel_def(def, &r, &why)) return refuse(m, "wheel_open", why.c_str()), false;
    if (g_side == KILN_SIDE_SERVER || !S.ui) return refuse(m, "wheel_open", no_overlay(), true), false;
    if (!ui_has_wheel()) return unsupported(m, "wheel_open", "this kiln.ui has no wheel (an older Kiln core)"), false;
    // state refusals are logged once per mod and reason: a mod that asks every frame must not flood the log
    if (!g_api->world(r.world_kind))
        return refuse(m, "wheel_open",
                      r.world_kind == EMF_WORLD_SERVER
                          ? "no server world runs (in someone else's game pass EMF_WORLD_CLIENT): its callback would never come"
                          : "no client world runs: its callback would never come",
                      true),
               false;
    std::lock_guard lock(g_mutex);
    if (g_shutdown || g_stopping[r.world_kind]) return refuse(m, "wheel_open", "the world is ending", true), false;
    if (g_open) return refuse(m, "wheel_open", "another wheel is open", true), false;
    g_open.emplace(Open{0, m, std::move(r), ++g_serial});
    std::vector<KilnStr> items;
    items.reserve(g_open->req.items.size());
    for (const std::string& s : g_open->req.items) items.push_back(kstr(s));
    KilnUiWheel d{};
    d.size = sizeof d;
    d.title = kstr(g_open->req.title);
    d.items = items.data();
    d.count = static_cast<uint32_t>(items.size());
    d.hold_key = g_open->req.hold_vk;
    KilnHandle h = 0;
    // kiln.ui calls on_wheel on the render thread, which waits for g_mutex: the entry is in place before it can end
    const KilnResult rc = S.ui->wheel_open(g_owner, &d, &on_wheel, reinterpret_cast<void*>(static_cast<uintptr_t>(g_open->serial)), &h);
    if (rc != KILN_OK) {
        g_open.reset();
        refuse(m, "wheel_open",
               rc == KILN_ERR_UNAVAILABLE
                   ? (S.ui->wheel_active() ? "another wheel is open"
                                           : "the overlay cannot draw it now (the Kiln menu is open, or the game window is not ready)")
                   : "kiln.ui refused the definition",
               true);
        return false;
    }
    g_open->handle = h;
    return true;
}

}  // namespace

bool wheel_is_open() { return ui_has_wheel() && S.ui->wheel_active(); }

void wheel_after_step(EmfWorld* w, uint8_t kind) {
    std::vector<Result> due;
    {
        std::lock_guard lock(g_mutex);
        for (auto it = g_results.begin(); it != g_results.end();) {
            if (it->world_kind == kind) {
                due.push_back(*it);
                it = g_results.erase(it);
            } else {
                ++it;
            }
        }
    }
    for (const Result& r : due) {
        struct Call {
            EmfWheelCallback cb;
            EmfWorld* w;
            int choice;
            void* user;
        } c{r.cb, w, r.choice, r.user};
        ContextScope scope(r.mod, w, true);
        guarded(r.mod, "a wheel callback",
                [](void* p) {
                    auto* x = static_cast<Call*>(p);
                    x->cb(x->w, x->choice, x->user);
                },
                &c);
    }
}

void wheel_world_stopping(uint8_t kind) {
    KilnHandle close = 0;
    {
        std::lock_guard lock(g_mutex);
        g_stopping[kind] = true;
        for (auto it = g_results.begin(); it != g_results.end();) it = it->world_kind == kind ? g_results.erase(it) : std::next(it);
        if (g_open && g_open->req.world_kind == kind) {
            close = g_open->handle;
            g_open.reset();
        }
    }
    if (close && ui_has_wheel()) S.ui->wheel_close(close);  // no callback: its world is going
}

void wheel_world_started(uint8_t kind) {
    wheel_world_stopping(kind);  // also drop what an older core's missed stop left
    std::lock_guard lock(g_mutex);
    g_stopping[kind] = false;
}

void wheel_shutdown() {
    g_shutdown = true;
    wheel_world_stopping(EMF_WORLD_SERVER);
    wheel_world_stopping(EMF_WORLD_CLIENT);
}

void fill_wheel(EmfApi* t) { t->wheel_open = &api_wheel_open; }

}  // namespace kemf
