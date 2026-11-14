// SPDX-License-Identifier: MIT
// kiln.ui.values: tooltip lines for Kiln's details window (chapter 05a §4.4, §5.1, §5.2, §5.5; capability
// ui.values.tooltip_lines, held until check 05a-c4-viewer). Nothing is drawn into the game's tooltip: session 6 showed
// that native lines at the detail view's content merge point neither wrap nor reserve space.
//
//   raw link on ui/ItemDetail (void(detail, u8))   copies the 0x48-byte ItemView prefix of ItemDetail's own record (its
//                                                  first argument) onto this thread's stack of details, calls on
//                                                  once, drops the copy on return (a nested detail gets its own);
//   mid ui/DetailLinesAppend                       the content merge point: once per detail occurrence, the lines
//                                                  providers run over the innermost copy.
// Each provider writes through its own writer (append only; at most the room left of 16 entries per occurrence, all
// providers together) and its lines are committed only when it returns KILN_OK with no failed append: a refused or
// faulting provider drops only its own lines. The result becomes the last completed detail snapshot (identity,
// session, frame, capture time, at most 16 text ids: no game pointer). The details window freezes one snapshot while
// open (open_tooltip_details) until refresh (same presentation identity only) or close.
#include <cstring>
#include <memory>
#include <mutex>

#include "ui_values_internal.h"

namespace kiln::uivalues {
namespace {

constexpr uint32_t kMaxLines = 16;
constexpr uint32_t kWriterMagic = 0x55564c57u;  // "UVLW"

struct LinesLayout {
    ViewLayout view;
} g_ll;
using DetailFn = void (*)(void*, uint8_t);
DetailFn g_detail_next = nullptr;

struct DetailCopy {
    uint8_t raw[kViewBytes] = {};
    bool ok = false, collected = false;
};
thread_local DetailCopy* t_detail = nullptr;

struct WriterState {
    uint32_t magic = kWriterMagic;
    const Provider* provider = nullptr;
    uint32_t count = 0;
    KilnUiTextId staged[kMaxLines] = {};
};

struct Snapshot {
    KilnUiItemView view{};
    uint64_t session = 0, frame = 0, capture_ms = 0;
    uint32_t count = 0;
    KilnUiTextId lines[kMaxLines] = {};
};
struct Details {
    std::mutex mu;
    std::shared_ptr<const Snapshot> latest, open;
    uint64_t providers_seen = 0;  // the lines registry's size when the window opened (a change closes it)
};
Details& details() {
    static Details* d = new Details;  // never destroyed: the UI thread may collect while the process ends
    return *d;
}

bool same_identity(const KilnUiItemView& a, const KilnUiItemView& b) {
    if ((a.flags & KUI_SYNTHETIC) || (b.flags & KUI_SYNTHETIC))
        return (a.flags & KUI_SYNTHETIC) && (b.flags & KUI_SYNTHETIC) && a.item == b.item && a.session == b.session;
    return a.item == b.item && a.inventory_page == b.inventory_page && a.slot_index == b.slot_index &&
           a.client_entity == b.client_entity && a.session == b.session;
}

KilnResult KILN_CALL writer_append(KilnUiLineWriter* w, KilnUiTextId id) {
    if (!w || w->size < sizeof(KilnUiLineWriter)) return KILN_ERR_INVALID_ARGUMENT;
    auto* st = static_cast<WriterState*>(w->reserved);
    if (!st || st->magic != kWriterMagic || !st->provider) return KILN_ERR_INVALID_ARGUMENT;
    if (w->status != KUI_REASON_NONE)
        return w->status == KUI_TEXT_BUDGET ? KILN_ERR_BUFFER_TOO_SMALL : KILN_ERR_INVALID_ARGUMENT;
    std::string_view probe;
    if (id != 0 && (!text::held_by(id, st->provider->mod) || !text::bytes(id, &probe))) {
        w->status = KUI_INVALID_EDIT;  // a foreign or unknown token
        return KILN_ERR_INVALID_ARGUMENT;
    }
    if (st->count >= w->capacity) {
        w->status = KUI_TEXT_BUDGET;  // 16 entries per detail occurrence, all providers together
        return KILN_ERR_BUFFER_TOO_SMALL;
    }
    st->staged[st->count] = id;
    w->lines[st->count] = KilnUiLine{sizeof(KilnUiLine), 0, id};
    w->count = ++st->count;
    return KILN_OK;
}

void collect(const DetailCopy& c) {
    KilnUiItemView v{};
    v.size = sizeof v;
    fill_view(c.raw, g_ll.view, KUI_DETAIL_COPY, &v);
    if (v.item == 0) return;
    ChainStamp st;
    ClientNow now{};
    if (env().client && env().client(&now)) {
        v.local_player = now.local_player;
        st.session = now.session;
        st.step = now.step;
    }
    KilnUiScreen screen{};
    if (env().screen && env().screen(&screen)) {
        v.screen = screen;
        st.frame = screen.frame + 1;  // collected while the frame after the last completed one is built
    }
    v.session = st.session;
    v.frame = st.frame;
    auto snap = std::make_shared<Snapshot>();
    snap->view = v;
    snap->session = st.session;
    snap->frame = st.frame;
    snap->capture_ms = env().tick_ms ? env().tick_ms() : 0;
    const auto list = providers(kListLines);
    for (const auto& sp : *list) {
        Provider& p = *sp;
        if (!p.live.load(std::memory_order_acquire)) continue;
        if (p.item != 0 && p.item != v.item) continue;
        if (snap->count >= kMaxLines) break;
        WriterState ws;
        ws.provider = &p;
        KilnUiLineWriter w{};
        w.size = sizeof w;
        w.capacity = kMaxLines - snap->count;
        w.status = KUI_REASON_NONE;
        w.reserved = &ws;
        w.append = &writer_append;
        CallState cs;
        cs.magic = kCallMagic;
        cs.provider = &p;
        KilnUiProviderContext ctx{};
        ctx.size = sizeof ctx;
        ctx.kind = kListLines;
        ctx.session = st.session;
        ctx.frame = st.frame;
        ctx.client_step = st.step;
        ctx.owner = p.owner;
        ctx.text = text_ops();
        ctx.reserved = &cs;
        KilnUiItemView view = v;
        KilnResult rc = KILN_ERR_INTERNAL;
        bool called;
        {
            CallScope scope(&cs);
            called = env().call_lines &&
                     env().call_lines(reinterpret_cast<KilnUiLinesFn>(p.fn), &ctx, &view, &w, p.user, &rc);
        }
        if (!called || rc == KILN_ERR_INTERNAL) {
            fault(p, called ? "returned an internal error" : "faulted");
            continue;
        }
        if (w.status == KUI_INVALID_EDIT || w.count != ws.count) {  // a foreign token, or the writer edited directly
            fault(p, "wrote an invalid line");
            continue;
        }
        if (rc != KILN_OK || w.status != KUI_REASON_NONE || cs.latched) {
            refused(p, rc != KILN_OK ? KUI_REJECTED : (w.status != KUI_REASON_NONE ? w.status : cs.latched));
            continue;  // only this provider's lines go
        }
        std::memcpy(snap->lines + snap->count, ws.staged, ws.count * sizeof(KilnUiTextId));
        snap->count += ws.count;
        succeeded(p);
    }
    Details& d = details();
    std::lock_guard lk(d.mu);
    if (d.open && d.open->session != snap->session) d.open.reset();  // a world/session change closes the window
    d.latest = std::move(snap);
}

void hk_item_detail(void* detail, uint8_t flag) {
    DetailCopy c;
    DetailCopy* outer = t_detail;
    c.ok = any(kListLines) && env().read(c.raw, reinterpret_cast<uintptr_t>(detail), sizeof c.raw);
    t_detail = &c;
    g_detail_next(detail, flag);
    t_detail = outer;
}

void KILN_CALL on_lines_point(KilnRegisters*, void*) {
    DetailCopy* c = t_detail;
    if (!c || !c->ok || c->collected) return;
    c->collected = true;  // once per detail occurrence
    collect(*c);
}

bool install_lines(uint32_t list) {
    if (list != kListLines) return false;
    uintptr_t detail = 0, point = 0;
    if (!load_view_layout(&g_ll.view) || !sig_symbol("ui/ItemDetail", &detail) ||
        !sig_symbol("ui/DetailLinesAppend", &point)) {
        logf(KILN_LOG_ERROR, "ui.values: ui/ItemDetail, ui/DetailLinesAppend or the view constants did not resolve");
        return false;
    }
    KilnResult rc = env().hook_mid(point, "ui.values/DetailLinesAppend", &on_lines_point);
    if (rc == KILN_OK)
        rc = env().hook_raw(detail, "ui.values/ItemDetail", 0, reinterpret_cast<void*>(&hk_item_detail),
                            reinterpret_cast<void**>(&g_detail_next));
    if (rc != KILN_OK || !g_detail_next) {
        logf(KILN_LOG_ERROR, "ui.values: hooking the item detail view failed (%d)", rc);
        return false;
    }
    return true;
}

struct LinesInstaller {
    LinesInstaller() { set_installer(kListLines, &install_lines); }
} g_lines_installer;

KilnResult check_caller(KilnOwner owner) {
    if (!env().client_side || !env().client_side()) return KILN_ERR_UNAVAILABLE;
    if (!env().owner_alive(owner)) return KILN_ERR_REVOKED;
    if (!env().row_available || !env().row_available(row_of(kListLines))) return KILN_ERR_UNAVAILABLE;
    return KILN_OK;
}

void open_result(KilnUiTooltipOpen* out, const Snapshot* s, uint32_t reason) {
    const uint32_t size = out->size;
    *out = KilnUiTooltipOpen{};
    out->size = size;
    out->ok = s ? 1 : 0;
    out->reason = s ? KUI_REASON_NONE : reason;
    if (s) {
        out->session = s->session;
        out->source_frame = s->frame;
    }
}

uint64_t lines_registered() { return providers(kListLines)->size(); }

}  // namespace

KilnResult tooltip_open(KilnOwner owner, KilnUiTooltipOpen* out) {
    if (const KilnResult rc = check_caller(owner)) return rc;
    ClientNow now{};
    if (env().client) env().client(&now);
    Details& d = details();
    std::lock_guard lk(d.mu);
    if (!d.latest || d.latest->session != now.session) {
        open_result(out, nullptr, KUI_NO_TOOLTIP);
        return KILN_OK;
    }
    if (!d.open) {  // opening again keeps the frozen snapshot (idempotent)
        d.open = d.latest;
        d.providers_seen = lines_registered();
    }
    open_result(out, d.open.get(), KUI_NO_TOOLTIP);
    return KILN_OK;
}

KilnResult tooltip_close(KilnOwner owner) {
    if (!env().client_side || !env().client_side()) return KILN_ERR_UNAVAILABLE;
    if (!env().owner_alive(owner)) return KILN_ERR_REVOKED;
    Details& d = details();
    std::lock_guard lk(d.mu);
    d.open.reset();
    return KILN_OK;
}

KilnResult tooltip_refresh(KilnOwner owner, KilnUiTooltipOpen* out) {
    if (const KilnResult rc = check_caller(owner)) return rc;
    Details& d = details();
    std::lock_guard lk(d.mu);
    if (!d.open) {
        open_result(out, nullptr, KUI_NO_TOOLTIP);
        return KILN_OK;
    }
    if (!d.latest || !same_identity(d.latest->view, d.open->view)) {
        open_result(out, nullptr, KUI_STALE_FRAME);
        return KILN_OK;
    }
    d.open = d.latest;
    open_result(out, d.open.get(), KUI_NO_TOOLTIP);
    return KILN_OK;
}

KilnResult tooltip_details(KilnOwner owner, uint32_t which, KilnUiTooltipDetails* out) {
    if (which > 1) return KILN_ERR_INVALID_ARGUMENT;
    if (const KilnResult rc = check_caller(owner)) return rc;
    ClientNow now{};
    if (env().client) env().client(&now);
    std::shared_ptr<const Snapshot> s;
    bool open = false;
    {
        Details& d = details();
        std::lock_guard lk(d.mu);
        // a world/session change or a provider registry change closes the window
        if (d.open && (d.open->session != now.session || d.providers_seen != lines_registered())) d.open.reset();
        open = d.open != nullptr;
        s = which == 0 ? d.open : d.latest;
        if (s && s->session != now.session) s.reset();
    }
    const uint32_t size = out->size;
    *out = KilnUiTooltipDetails{};
    out->size = size;
    out->open = open ? 1 : 0;
    if (!s) {
        out->reason = KUI_NO_TOOLTIP;
        return KILN_OK;
    }
    out->ok = 1;
    out->session = s->session;
    out->source_frame = s->frame;
    out->capture_ms = s->capture_ms;
    out->item = s->view.item;
    out->inventory_page = s->view.inventory_page;
    out->slot_index = s->view.slot_index;
    out->client_entity = s->view.client_entity;
    out->view_flags = s->view.flags;
    out->count = s->count;
    for (uint32_t i = 0; i < s->count; ++i) {
        std::string_view b;
        if (text::bytes(s->lines[i], &b)) out->lines[i] = KilnStr{b.data(), b.size()};
    }
    return KILN_OK;
}

void lines_reset_for_tests() {
    Details& d = details();
    std::lock_guard lk(d.mu);
    d.latest.reset();
    d.open.reset();
    d.providers_seen = 0;
    g_ll = {};
    g_detail_next = nullptr;
}

}  // namespace kiln::uivalues
