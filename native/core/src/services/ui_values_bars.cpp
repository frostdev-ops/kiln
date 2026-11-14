// SPDX-License-Identifier: MIT
// kiln.ui.values: the item detail bar and the inventory slot bar (chapter 05a §2 DetailBarDraw / SlotBarDraw, §4.2,
// §5.1). Two mid hooks right before the game draws a temporary Bar descriptor (rcx):
//   detail  rdi = the thumbnail descriptor; *(rdi + detail.view) = ItemDetail's own copied record, whose first 0x48
//           bytes have ItemView layout (read-only, identity optional, all-zero but the item when the game built it for
//           an item id alone: a synthetic view);
//   slot    rbx = the slot's ItemView.
// Per hit: copy the view prefix and the Bar's fraction / current / max / label, run the kind's providers over that
// copy, and write the accumulated result back into the temporary descriptor once, only the fields that changed. A
// provider never sees a game pointer, and nothing is written before every edit was validated. The game's own gates
// (which items get a bar, a slot's fuel 0/0 drawn full, the detail skipping water 0/0) are untouched: no provider that
// passes through changes a byte.
//
// Units (§2 "Provider units"): detail current/max are what the game prints (water: charges); slot current/max are raw
// fuel units; the view's fuel / fuel_max are raw fuel units with units_per_charge, the detail view's divisor (read
// through the frame's play data while the UI frame runs; 1 when the registry has none, as the game does).
#include <cstring>

#include "ui_values_internal.h"

namespace kiln::uivalues {
namespace {

struct BarLayout {
    int64_t fraction = 0, current = 0, maximum = 0, label = 0;
};
struct Charges {  // UiApp -> data -> state -> play -> balancing registry -> resource +waterChargeAmount
    uintptr_t ui_app = 0;
    int64_t data = 0, play = 0, balancing = 0, root = 0, amount = 0;
    bool ok = false;
};
struct Layout {
    bool loaded = false;  // view and bar: shared by both kinds, loaded by the first installer (installers are serial)
    ViewLayout view;
    BarLayout bar;
    int64_t detail_view = 0;
    Charges charges;
} g_bars;
bool g_charges_tried = false;  // installers run one at a time (ensure_installed holds its lock)

bool load_bar(BarLayout* b) {
    const bool ok = sig_constant("ui/bar.fraction", &b->fraction) && sig_constant("ui/bar.current", &b->current) &&
                    sig_constant("ui/bar.max", &b->maximum) && sig_constant("ui/bar.label", &b->label);
    // the descriptor is at least 0x50 bytes (the detail one ends at +0x4f): every field must sit inside it
    return ok && b->fraction >= 0 && b->fraction + 4 <= 0x50 && b->current >= 0 && b->current + 4 <= 0x50 &&
           b->maximum >= 0 && b->maximum + 4 <= 0x50 && b->label >= 0 && b->label + 24 <= 0x50;
}

void load_charges(Charges* c) {
    c->ok = sig_symbol("ui/UiApp", &c->ui_app) && sig_constant("ui/ui.data", &c->data) &&
            sig_constant("ui/state.play", &c->play) && sig_constant("ui/play.balancing", &c->balancing) &&
            sig_constant("ui/balancing.rootData", &c->root) &&
            sig_constant("ui/balancing.waterChargeAmount", &c->amount);
}

// The detail view's charge divisor, read through the UI frame's play data (only valid while a UI frame runs).
uint32_t units_per_charge_in_frame() {
    const Charges& c = g_bars.charges;
    uintptr_t app = 0, data = 0, state = 0, play = 0;
    if (!c.ok || !game_read(c.ui_app, &app) || !game_read(app + c.data, &data) || !game_read(data, &state) ||
        !game_read(state + c.play, &play))
        return 1;
    return units_per_charge(play);
}

// The bar as the game filled it: the input of the chain, and what is written back when nothing changed.
struct GameBar {
    float fraction = 0;
    uint32_t current = 0, maximum = 0;
    text::GameText label{};
};
bool read_bar(uintptr_t bar, bool with_label, GameBar* g) {
    return game_read(bar + g_bars.bar.fraction, &g->fraction) && game_read(bar + g_bars.bar.current, &g->current) &&
           game_read(bar + g_bars.bar.maximum, &g->maximum) &&
           (!with_label || game_read(bar + g_bars.bar.label, &g->label));
}

std::atomic<bool> g_bar_write_failed{false};

// Writes what the chain changed. Every edit was validated before; a field that did not change is not written.
void commit(uintptr_t bar, uint32_t kind, const GameBar& g, const KilnUiBarValue& out) {
    bool ok = true;
    if (std::memcmp(&out.fraction, &g.fraction, sizeof out.fraction) != 0)
        ok &= env().write(bar + g_bars.bar.fraction, &out.fraction, sizeof out.fraction);
    if (out.current != g.current) ok &= env().write(bar + g_bars.bar.current, &out.current, sizeof out.current);
    if (out.maximum != g.maximum) ok &= env().write(bar + g_bars.bar.maximum, &out.maximum, sizeof out.maximum);
    if (kind == KUI_DETAIL && out.label_is_override) {
        text::GameText t{};
        if (text::game_text(out.label_id, &t)) ok &= env().write(bar + g_bars.bar.label, &t, sizeof t);
    }
    if (!ok && !g_bar_write_failed.exchange(true))
        logf(KILN_LOG_ERROR, "ui.values: writing a %s bar descriptor failed", kind == KUI_DETAIL ? "detail" : "slot");
}

void stamp_now(KilnUiItemView* v, ChainStamp* st) {
    ClientNow c{};
    if (env().client && env().client(&c)) {
        v->local_player = c.local_player;
        st->session = c.session;
        st->step = c.step;
    }
    KilnUiScreen s{};
    if (env().screen && env().screen(&s)) {
        v->screen = s;
        st->frame = s.frame + 1;  // the screen is the last completed frame; the bar is drawn in the one after it
    }
    v->session = st->session;
    v->frame = st->frame;
}

void run_bar(uint32_t kind, uintptr_t bar, uintptr_t view_addr, uint32_t base_flags) {
    uint8_t raw[kViewBytes];
    GameBar g;
    if (!env().read(raw, view_addr, sizeof raw) || !read_bar(bar, kind == KUI_DETAIL, &g)) return;
    KilnUiItemView v{};
    v.size = sizeof v;
    fill_view(raw, g_bars.view, base_flags, &v);
    if (v.item == 0) return;  // the game draws no bar for an empty view; nothing to offer a provider
    v.units_per_charge = units_per_charge_in_frame();
    ChainStamp st;
    stamp_now(&v, &st);
    KilnUiBarValue in{};
    in.size = sizeof in;
    in.kind = kind;
    in.fraction = g.fraction;
    in.current = g.current;
    in.maximum = g.maximum;
    const KilnUiBarValue out = run_chain(kind, v, in, st);
    commit(bar, kind, g, out);
    publish_value(kind, v, out);
}

void KILN_CALL on_detail(KilnRegisters* r, void*) {
    if (!any(KUI_DETAIL)) return;
    uintptr_t record = 0;
    if (!game_read(r->rdi + g_bars.detail_view, &record)) return;
    run_bar(KUI_DETAIL, r->rcx, record, KUI_DETAIL_COPY);
}

void KILN_CALL on_slot(KilnRegisters* r, void*) {
    if (!any(KUI_SLOT)) return;
    run_bar(KUI_SLOT, r->rcx, r->rbx, 0);
}

bool install_bars(uint32_t list) {
    if (!g_bars.loaded) {
        if (!load_view_layout(&g_bars.view) || !load_bar(&g_bars.bar)) {
            logf(KILN_LOG_ERROR, "ui.values: the item view or bar layout constants did not resolve");
            return false;
        }
        g_bars.loaded = true;
    }
    load_charge_layout();
    uintptr_t at = 0;
    if (list == KUI_DETAIL) {
        if (!sig_constant("ui/detail.view", &g_bars.detail_view) || g_bars.detail_view < 0 ||
            !sig_symbol("ui/DetailBarDraw", &at)) {
            logf(KILN_LOG_ERROR, "ui.values: ui/DetailBarDraw or ui/detail.view did not resolve");
            return false;
        }
        if (!g_bars.charges.ok) logf(KILN_LOG_WARN, "ui.values: the charge divisor is not readable; views say 1");
        const KilnResult rc = env().hook_mid(at, "ui.values/DetailBarDraw", &on_detail);
        if (rc != KILN_OK) logf(KILN_LOG_ERROR, "ui.values: hooking ui/DetailBarDraw failed (%d)", rc);
        return rc == KILN_OK;
    }
    if (list == KUI_SLOT) {
        if (!sig_symbol("ui/SlotBarDraw", &at)) {
            logf(KILN_LOG_ERROR, "ui.values: ui/SlotBarDraw did not resolve");
            return false;
        }
        const KilnResult rc = env().hook_mid(at, "ui.values/SlotBarDraw", &on_slot);
        if (rc != KILN_OK) logf(KILN_LOG_ERROR, "ui.values: hooking ui/SlotBarDraw failed (%d)", rc);
        return rc == KILN_OK;
    }
    return false;
}

struct BarsInstaller {
    BarsInstaller() {
        set_installer(KUI_DETAIL, &install_bars);
        set_installer(KUI_SLOT, &install_bars);
    }
} g_bars_installer;

}  // namespace

// ---- the item view, shared with the held item (ui_values_update.cpp) ----
bool load_view_layout(ViewLayout* v) {
    struct Field {
        const char* name;
        int64_t* at;
        int64_t width;
    } fields[] = {{"ui/view.inventoryPage", &v->page, 4},
                  {"ui/view.slotIndex", &v->index, 1},
                  {"ui/view.item", &v->item, 4},
                  {"ui/view.count", &v->count, 4},
                  {"ui/view.pide", &v->pide, 4},
                  {"ui/view.durability", &v->durability, 4},
                  {"ui/view.durabilityMax", &v->durability_max, 4},
                  {"ui/view.fuel", &v->fuel, 2},
                  {"ui/view.fuelMax", &v->fuel_max, 2},
                  {"ui/view.level", &v->level, 4},
                  {"ui/view.rarity", &v->rarity, 1}};
    int64_t stride = 0;
    if (!sig_constant("ui/view.stride", &stride) || stride != static_cast<int64_t>(kViewBytes)) return false;
    for (const Field& f : fields)
        if (!sig_constant(f.name, f.at) || *f.at < 0 || *f.at + f.width > static_cast<int64_t>(kViewBytes))
            return false;
    return true;
}

void load_charge_layout() {
    if (g_charges_tried) return;
    g_charges_tried = true;
    load_charges(&g_bars.charges);
}

void fill_view(const uint8_t* raw, const ViewLayout& vl, uint32_t base_flags, KilnUiItemView* v) {
    auto u32 = [&](int64_t at) {
        uint32_t x;
        std::memcpy(&x, raw + at, 4);
        return x;
    };
    auto u16 = [&](int64_t at) {
        uint16_t x;
        std::memcpy(&x, raw + at, 2);
        return x;
    };
    v->item = u32(vl.item);
    v->durability = u32(vl.durability);
    v->durability_max = u32(vl.durability_max);
    v->fuel = u16(vl.fuel);  // the view's u16 network values, widened
    v->fuel_max = u16(vl.fuel_max);
    v->level = u32(vl.level);
    v->rarity = raw[vl.rarity];
    const uint32_t page = u32(vl.page), pide = u32(vl.pide), count = u32(vl.count);
    const uint8_t index = raw[vl.index];
    v->flags = base_flags;
    if ((base_flags & KUI_DETAIL_COPY) && page == 0 && index == 0 && count == 0 && pide == 0) {
        v->flags |= KUI_SYNTHETIC;  // built for an item id alone: no slot, count or entity, never an instance
        return;
    }
    if (page != 0) {
        v->flags |= KUI_INVENTORY_SLOT;
        v->inventory_page = page;
        v->slot_index = index;
    }
    v->count = count;
    if (pide != 0) {
        v->flags |= KUI_CLIENT_ENTITY;
        v->client_entity = pide;
    }
    // server_entity stays 0 (KUI_SERVER_ENTITY clear): v1 proves no item's replicated provenance natively (§4.6)
}

uint32_t units_per_charge(uintptr_t play) {
    const Charges& c = g_bars.charges;
    uintptr_t registry = 0, resource = 0;
    uint32_t units = 1;
    if (!c.ok || !play || !game_read(play + c.balancing, &registry) || !registry) return 1;
    if (!game_read(registry + c.root, &resource)) return 1;
    if (resource && !game_read(resource + c.amount, &units)) return 1;  // no resource: the game's fallback is 1
    return units ? units : 1;
}

}  // namespace kiln::uivalues
