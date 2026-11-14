// SPDX-License-Identifier: MIT
// kiln.ui.values: the player-UI update, the HUD pairs and the held item (chapter 05a §2 PlayerUiUpdate /
// PlayerUiPlayCapture / HeldItemCapture, §4.3, §5.1), and the pre-provider stage 09-ui-blocks reads (chapter 09 §4.4).
//
// The game's player-UI update (void(iterator)) copies each iterated entity's replicated values into the UI's play data
// as u32 pairs: health, stamina, mana and the held item's fuel, plus the inventory models with their item views. The
// UI frame then draws from those copies, so a pair changed right after the update changes only the picture. UiApp,
// the frame context, can be null here: the play pointer is captured inside the update instead.
//
//   raw link on PlayerUiUpdate   saves the thread's capture (a nested update restores it), starts an empty one, calls
//                                the rest of the chain once, then works on what the update captured;
//   mid PlayerUiPlayCapture      rsi = play, before the health store: kept only when play+localEntity is the local
//                                player (the loop reloads rsi per iterated entity); a second play claiming the local
//                                player makes the update's capture ambiguous (nothing is patched);
//   mid HeldItemCapture          rsi = play, edi = the held stack's pide, rbx = its NetworkFuel or null (also reached
//                                with pide 0 when nothing is held).
// After the original returned: play+localEntity must still be the local player. Then the stage observers get the
// original pairs and the local player's own inventory; then the held and HUD providers run and only the pairs they
// changed are written. No play pointer is kept past the update. Held and HUD maxima must be positive; no provider is
// called for a pair whose game maximum is 0, nor without a local player.
#include <algorithm>
#include <cstring>
#include <memory>
#include <mutex>

#include "ui_values_internal.h"

namespace kiln::uivalues {
namespace {

constexpr uint64_t kMaxModels = 64;   // inventory models of one update (the player's own, an open container, ...)
constexpr uint64_t kMaxViews = 4096;  // item views of one model
constexpr size_t kViewChunk = 64;     // views copied per guarded read

struct UpdateLayout {
    int64_t local = 0;                                     // play + localEntity: the iterated entity (u32)
    int64_t cur[kValueKinds] = {}, max[kValueKinds] = {};  // the u32 pairs of held (2) and the HUD kinds (3..5)
    int64_t models = 0, models_count = 0, model_owner = 0, model_stride = 0, model_views = 0, model_views_count = 0;
    ViewLayout view;
};
UpdateLayout g_upd;  // written by the installers before the flag that publishes each part
std::atomic<bool> g_base{false}, g_hud{false}, g_held{false}, g_models{false};
using UpdateFn = void (*)(void*);
UpdateFn g_update_next = nullptr;  // the continuation of the raw link (never freed by kiln.hooks)

// ---- the thread's capture of the running update ----
struct Capture {
    bool active = false;
    uint32_t local = 0;      // the local player when the update began (0 = none: nothing is captured)
    uintptr_t play = 0;      // the play data whose entity was the local player
    bool ambiguous = false;  // a second, different play data claimed the local player
    uintptr_t held_play = 0;
    uint32_t held_pide = 0;
    bool held_fuel = false;  // the held stack had a NetworkFuel
};
thread_local Capture t_capture;

// ---- stage observers ----
struct StageSub {
    KilnHandle handle;
    KilnOwner owner;
    StageFn fn;
    void* user;
};
using StageList = std::vector<StageSub>;
std::atomic<uint32_t> g_stage_count{0};
struct StageSubs {
    std::mutex mu;
    StageList list;
    std::shared_ptr<const StageList> snap = std::make_shared<const StageList>();  // what an update reads
    KilnHandle next = 1;
    void publish() {  // lock held: a new immutable snapshot for the next update
        snap = std::make_shared<const StageList>(list);
        g_stage_count.store(static_cast<uint32_t>(list.size()), std::memory_order_release);
    }
};
StageSubs& g_stage = *new StageSubs;  // never destroyed: the game's thread may still update while the process ends

constexpr KilnHandle kStageTag = 0x5573ull << 48;  // "Us": not a registration handle of the public table

bool load_models() {  // the held installer and stage_subscribe both ask; the first that resolves it publishes
    static std::mutex& mu = *new std::mutex;
    std::lock_guard lk(mu);
    if (g_models.load(std::memory_order_acquire)) return true;
    UpdateLayout& u = g_upd;
    const bool ok =
        sig_constant("ui/play.models", &u.models) && sig_constant("ui/play.modelsCount", &u.models_count) &&
        sig_constant("ui/model.owner", &u.model_owner) && sig_constant("ui/model.stride", &u.model_stride) &&
        sig_constant("ui/model.views", &u.model_views) && sig_constant("ui/model.viewsCount", &u.model_views_count) &&
        load_view_layout(&u.view) && u.model_stride > 0 && u.model_owner >= 0 && u.model_owner + 4 <= u.model_stride &&
        u.model_views >= 0 && u.model_views + 8 <= u.model_stride && u.model_views_count >= 0 &&
        u.model_views_count + 8 <= u.model_stride;
    if (ok) g_models.store(true, std::memory_order_release);
    return ok;
}

// Walks the local player's own inventory models of `play`: fn(owner, view bytes) for every view, until it says stop.
// False when the models could not be read.
template <class F>
bool each_own_view(uintptr_t play, uint32_t local, F&& fn) {
    const UpdateLayout& u = g_upd;
    uintptr_t models = 0;
    uint64_t count = 0;
    if (!game_read(play + u.models, &models) || !game_read(play + u.models_count, &count)) return false;
    if (count > kMaxModels || (count && !models)) return false;
    uint8_t chunk[kViewChunk * kViewBytes];
    for (uint64_t m = 0; m < count; ++m) {
        const uintptr_t model = models + m * static_cast<uint64_t>(u.model_stride);
        uint32_t owner = 0;
        uintptr_t views = 0;
        uint64_t n = 0;
        if (!game_read(model + u.model_owner, &owner)) return false;
        if (owner != local) continue;
        if (!game_read(model + u.model_views, &views) || !game_read(model + u.model_views_count, &n)) return false;
        if (n > kMaxViews || (n && !views)) return false;
        for (uint64_t i = 0; i < n; i += kViewChunk) {
            const size_t k = static_cast<size_t>(std::min<uint64_t>(kViewChunk, n - i));
            if (!env().read(chunk, views + i * kViewBytes, k * kViewBytes)) return false;
            for (size_t j = 0; j < k; ++j)
                if (!fn(owner, chunk + j * kViewBytes)) return true;
        }
    }
    return true;
}

uint32_t view_u32(const uint8_t* raw, int64_t at) {
    uint32_t v;
    std::memcpy(&v, raw + at, 4);
    return v;
}

// The held stack's view: the one view of the local player's models with that pide. Missing, unreadable, or two views
// with that pide naming different items: false (a held value is never invented).
bool held_view(uintptr_t play, uint32_t local, uint32_t pide, uint8_t* out) {
    const ViewLayout& v = g_upd.view;
    bool found = false, mismatch = false;
    const bool read = each_own_view(play, local, [&](uint32_t, const uint8_t* raw) {
        if (view_u32(raw, v.pide) != pide) return true;
        if (found && view_u32(raw, v.item) != view_u32(out, v.item)) {
            mismatch = true;
            return false;
        }
        if (!found) std::memcpy(out, raw, kViewBytes);
        found = true;
        return true;
    });
    return read && found && !mismatch && view_u32(out, v.item) != 0;
}

std::atomic<bool> g_held_missing_logged{false}, g_write_failed_logged{false};

// The stage: original pairs and own inventory rows, for every observer, before any provider.
void publish_stage(uintptr_t play, uint32_t local, const ClientNow& c, const uint32_t* cur, const uint32_t* max,
                   bool vitals) {
    std::shared_ptr<const StageList> subs;
    {
        std::lock_guard lk(g_stage.mu);
        subs = g_stage.snap;  // a reference, no copy: the list is rebuilt only when it changes
    }
    const auto dead = [](const StageSub& sub) { return sub.owner != 0 && !env().owner_alive(sub.owner); };
    if (std::any_of(subs->begin(), subs->end(), dead)) {  // a retired owner's observers go for good
        std::lock_guard lk(g_stage.mu);
        std::erase_if(g_stage.list, dead);
        g_stage.publish();
        subs = g_stage.snap;
    }
    if (subs->empty()) return;
    thread_local std::vector<StageRow>* t_rows = nullptr;  // reused, never freed: no TLS destructor in kiln-core
    if (!t_rows) t_rows = new std::vector<StageRow>;
    std::vector<StageRow>& rows = *t_rows;
    rows.clear();
    uint32_t total = 0;
    bool inventory = false;
    if (g_models.load(std::memory_order_acquire)) {
        const ViewLayout& v = g_upd.view;
        inventory = each_own_view(play, local, [&](uint32_t owner, const uint8_t* raw) {
            const uint32_t item = view_u32(raw, v.item);
            if (item == 0) return true;  // an empty slot
            ++total;
            rows.push_back(StageRow{owner, view_u32(raw, v.page), raw[v.index], item, view_u32(raw, v.count),
                                    view_u32(raw, v.pide)});
            return true;
        });
        if (!inventory) {
            rows.clear();
            total = 0;
        }
        std::sort(rows.begin(), rows.end(), [](const StageRow& a, const StageRow& b) {
            return a.inventory_page != b.inventory_page ? a.inventory_page < b.inventory_page
                                                        : a.slot_index < b.slot_index;
        });
        if (rows.size() > kStageRowsMax) rows.resize(kStageRowsMax);
    }
    Stage s{};
    s.size = sizeof s;
    s.available = (vitals ? kStageVitals : 0u) | (inventory ? kStageInventory : 0u);
    s.local_player = local;
    if (vitals) {
        s.health = cur[KUI_HEALTH];
        s.health_max = max[KUI_HEALTH];
        s.stamina = cur[KUI_STAMINA];
        s.stamina_max = max[KUI_STAMINA];
        s.mana = cur[KUI_MANA];
        s.mana_max = max[KUI_MANA];
    }
    s.rows_total = total;
    s.row_count = static_cast<uint32_t>(rows.size());
    s.rows = rows.data();
    s.session = c.session;
    s.step = c.step;
    s.capture_ms = env().tick_ms ? env().tick_ms() : 0;
    for (const StageSub& sub : *subs) {
        try {
            sub.fn(&s, sub.user);
        } catch (...) {
        }
    }
}

// Runs the providers of a held or HUD kind over the pair and writes the pair back when they changed it.
void run_pair(uint32_t kind, uintptr_t play, const KilnUiItemView& view, uint32_t cur, uint32_t max,
              const ChainStamp& st) {
    KilnUiBarValue in{};
    in.size = sizeof in;
    in.kind = kind;
    in.fraction = static_cast<float>(std::min(1.0, static_cast<double>(cur) / max));
    in.current = cur;
    in.maximum = max;
    const KilnUiBarValue out = run_chain(kind, view, in, st);
    bool ok = true;
    if (out.current != cur) ok &= env().write(play + g_upd.cur[kind], &out.current, sizeof out.current);
    if (out.maximum != max) ok &= env().write(play + g_upd.max[kind], &out.maximum, sizeof out.maximum);
    if (!ok && !g_write_failed_logged.exchange(true))
        logf(KILN_LOG_ERROR, "ui.values: writing a player-UI pair failed");
    publish_value(kind, view, out);
}

void after_update(const Capture& cap, const ClientNow& c) {
    if (!cap.play || cap.ambiguous || !cap.local) return;
    const bool hud = g_hud.load(std::memory_order_acquire), held = g_held.load(std::memory_order_acquire);
    const bool want_hud = hud && (any(KUI_HEALTH) || any(KUI_STAMINA) || any(KUI_MANA));
    const bool want_held = held && any(KUI_HELD);
    const bool want_stage = g_stage_count.load(std::memory_order_acquire) != 0;
    if (!want_hud && !want_held && !want_stage) return;
    const uintptr_t play = cap.play;
    uint32_t entity = 0;
    if (!game_read(play + g_upd.local, &entity) || entity != cap.local) return;  // another entity's values by now
    update_done();
    uint32_t cur[kValueKinds] = {}, max[kValueKinds] = {};
    bool vitals = hud;
    if (hud)
        for (uint32_t k = KUI_HEALTH; k <= KUI_MANA; ++k)
            vitals &= game_read(play + g_upd.cur[k], &cur[k]) && game_read(play + g_upd.max[k], &max[k]);
    if (want_stage) publish_stage(play, cap.local, c, cur, max, vitals);

    ChainStamp st;
    st.session = c.session;
    st.step = c.step;
    KilnUiItemView base{};
    base.size = sizeof base;
    base.local_player = cap.local;
    KilnUiScreen screen{};
    if (env().screen && env().screen(&screen)) {
        base.screen = screen;
        st.frame = screen.frame;  // held and HUD run between frames: the last completed one
    }
    base.session = st.session;
    base.frame = st.frame;

    if (want_held && cap.held_play == play && cap.held_pide != 0 && cap.held_fuel) {
        uint32_t fuel = 0, fuel_max = 0;
        if (game_read(play + g_upd.cur[KUI_HELD], &fuel) && game_read(play + g_upd.max[KUI_HELD], &fuel_max) &&
            fuel_max != 0) {
            uint8_t raw[kViewBytes];
            if (held_view(play, cap.local, cap.held_pide, raw)) {
                KilnUiItemView v = base;
                fill_view(raw, g_upd.view, 0, &v);
                v.units_per_charge = units_per_charge(play);
                run_pair(KUI_HELD, play, v, fuel, fuel_max, st);
            } else if (!g_held_missing_logged.exchange(true)) {
                logf(KILN_LOG_INFO,
                     "ui.values: the held stack's view was not found in the local inventory; its "
                     "providers pass (logged once)");
            }
        }
    }
    if (want_hud && vitals)
        for (uint32_t k = KUI_HEALTH; k <= KUI_MANA; ++k)
            if (max[k] != 0 && any(k)) run_pair(k, play, base, cur[k], max[k], st);
}

void hk_update(void* iterator) {
    ClientNow c{};
    if (!env().client || !env().client(&c)) c = ClientNow{};
    const Capture outer = t_capture;  // a nested update gets its own capture and gives the outer one back
    t_capture = Capture{};
    t_capture.active = true;
    t_capture.local = c.local_player;
    g_update_next(iterator);
    const Capture mine = t_capture;
    t_capture = outer;
    after_update(mine, c);
}

void KILN_CALL on_play_capture(KilnRegisters* r, void*) {
    Capture& cap = t_capture;
    if (!cap.active || !cap.local) return;
    uint32_t entity = 0;
    if (!game_read(r->rsi + g_upd.local, &entity) || entity != cap.local) return;
    if (!cap.play)
        cap.play = r->rsi;
    else if (cap.play != r->rsi)
        cap.ambiguous = true;
}

void KILN_CALL on_held_capture(KilnRegisters* r, void*) {
    Capture& cap = t_capture;
    if (!cap.active || !cap.local) return;
    uint32_t entity = 0;
    if (!game_read(r->rsi + g_upd.local, &entity) || entity != cap.local) return;
    cap.held_play = r->rsi;
    cap.held_pide = static_cast<uint32_t>(r->rdi);
    cap.held_fuel = r->rbx != 0;
}

bool install_base() {
    if (g_base.load(std::memory_order_acquire)) return true;
    uintptr_t update = 0, capture = 0;
    if (!sig_constant("ui/play.localEntity", &g_upd.local) || g_upd.local < 0 ||
        !sig_symbol("ui/PlayerUiUpdate", &update) || !sig_symbol("ui/PlayerUiPlayCapture", &capture)) {
        logf(KILN_LOG_ERROR,
             "ui.values: ui/PlayerUiUpdate, ui/PlayerUiPlayCapture or ui/play.localEntity did not "
             "resolve");
        return false;
    }
    // the capture first: the raw link is what makes it record anything
    KilnResult rc = env().hook_mid(capture, "ui.values/PlayerUiPlayCapture", &on_play_capture);
    if (rc == KILN_OK)
        rc = env().hook_raw(update, "ui.values/PlayerUiUpdate", reinterpret_cast<void*>(&hk_update),
                            reinterpret_cast<void**>(&g_update_next));
    if (rc != KILN_OK || !g_update_next) {
        logf(KILN_LOG_ERROR, "ui.values: hooking the player-UI update failed (%d)", rc);
        return false;
    }
    g_base.store(true, std::memory_order_release);
    return true;
}

bool install_update(uint32_t list) {
    if (list == KUI_HELD) {
        uintptr_t at = 0;
        if (!sig_constant("ui/play.heldFuel", &g_upd.cur[KUI_HELD]) ||
            !sig_constant("ui/play.heldFuelMax", &g_upd.max[KUI_HELD]) || !load_models() ||
            !sig_symbol("ui/HeldItemCapture", &at)) {
            logf(KILN_LOG_ERROR, "ui.values: the held item's symbols or constants did not resolve");
            return false;
        }
        load_charge_layout();
        if (!install_base()) return false;
        const KilnResult rc = env().hook_mid(at, "ui.values/HeldItemCapture", &on_held_capture);
        if (rc != KILN_OK) {
            logf(KILN_LOG_ERROR, "ui.values: hooking ui/HeldItemCapture failed (%d)", rc);
            return false;
        }
        g_held.store(true, std::memory_order_release);
        return true;
    }
    if (list >= KUI_HEALTH && list <= KUI_MANA) {
        if (g_hud.load(std::memory_order_acquire)) return true;  // one install for the three HUD kinds
        static const char* const kPairs[][2] = {{"ui/play.health", "ui/play.healthMax"},
                                                {"ui/play.stamina", "ui/play.staminaMax"},
                                                {"ui/play.mana", "ui/play.manaMax"}};
        for (uint32_t k = KUI_HEALTH; k <= KUI_MANA; ++k)
            if (!sig_constant(kPairs[k - KUI_HEALTH][0], &g_upd.cur[k]) ||
                !sig_constant(kPairs[k - KUI_HEALTH][1], &g_upd.max[k])) {
                logf(KILN_LOG_ERROR, "ui.values: the HUD pairs' constants did not resolve");
                return false;
            }
        if (!install_base()) return false;
        g_hud.store(true, std::memory_order_release);
        return true;
    }
    return false;
}

struct UpdateInstaller {
    UpdateInstaller() {
        for (uint32_t k = KUI_HELD; k <= KUI_MANA; ++k) set_installer(k, &install_update);
    }
} g_update_installer;

}  // namespace

KilnResult stage_subscribe(KilnOwner owner, StageFn fn, void* user, KilnHandle* out) {
    if (!fn || !out) return KILN_ERR_INVALID_ARGUMENT;
    *out = 0;
    if (!env().client_side || !env().client_side()) return KILN_ERR_UNAVAILABLE;
    if (!ensure_installed(KUI_HEALTH)) return KILN_ERR_UNAVAILABLE;  // the update hook comes with ui.values.hud
    load_models();  // rows when the inventory layout resolved; vitals only otherwise
    std::lock_guard lk(g_stage.mu);
    const KilnHandle h = kStageTag | g_stage.next++;
    g_stage.list.push_back(StageSub{h, owner, fn, user});
    g_stage.publish();
    *out = h;
    return KILN_OK;
}

KilnResult stage_unsubscribe(KilnHandle h) {
    std::lock_guard lk(g_stage.mu);
    const auto it =
        std::find_if(g_stage.list.begin(), g_stage.list.end(), [&](const StageSub& s) { return s.handle == h; });
    if (it == g_stage.list.end()) return KILN_ERR_NOT_FOUND;
    g_stage.list.erase(it);
    g_stage.publish();
    return KILN_OK;
}

}  // namespace kiln::uivalues
