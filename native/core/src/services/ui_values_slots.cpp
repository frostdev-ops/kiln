// SPDX-License-Identifier: MIT
// kiln.ui.values: the inventory slots the game drew in each completed UI frame (chapter 05a §2 "Slot geometry and
// hover", §5.3; capability ui.slots.inspect, held until check 05a-c5).
//
//   raw link on ui/GameUiFrame at priority +100   outside kiln.ui's own link (0): begins this thread's frame collector,
//                                                 calls the rest of the chain once (kiln.ui publishes its screen in
//                                                 there), then publishes the frame;
//   raw link on ui/ItemSlot                       void* item_slot(void* out32, void* descriptor, const void* options,
//                                                 void* context): pushes a slot collector (a nested call gets its own
//                                                 and gives the outer one back), calls on, then copies the 0x20-byte
//                                                 result (outer xywh, 16 state bytes) and the descriptor's view;
//   mid ui/SlotGeometry                           rax -> the inner bounds {x,y,w,h}, r15 = the descriptor: fills the
//                                                 innermost collector of that descriptor.
// A frame holds at most 4096 slot draws; more publish an unavailable frame (KUI_FRAME_BUDGET), never a partial list.
// The last two completed frames are kept as immutable snapshots for copy_slots; subscribe_slots observers get each one
// on the UI thread after the frame. Only values are copied: no descriptor, view or result pointer leaves a callback.
//
// The holder (ruling 9, for 13d-shroudlink's slot marks): at the frame's first slot, while the game's UiApp frame
// context is valid, the play data's inventory models give each page's holder (the model's owner) and the page's
// position among that holder's pages. Kiln gives every holder a lifetime serial: a new one when it first shows in a
// client session or shows again after a frame in which the models were read without it.
#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstring>
#include <memory>
#include <mutex>
#include <unordered_map>
#include <vector>

#include "ui_values_internal.h"

namespace kiln::uivalues {
namespace {

constexpr uint32_t kSlotsBudget = 4096;
constexpr size_t kSlotResultBytes = 0x20;
constexpr size_t kRecordMin = offsetof(KilnUiVisibleSlot, holder);  // the chapter's record (88 bytes)

struct SlotsLayout {
    ViewLayout view;
    int64_t state = 0;  // the result's state bytes (ui/slot.hoverCandidate)
    int64_t frame_arg = 0, canvas_w = 0, canvas_h = 0, viewport = 0, ppu = 0;
    // the holders, through the frame's play data (optional: no holder when one is missing)
    uintptr_t ui_app = 0;
    int64_t data = 0, play = 0, models = 0, models_count = 0, owner = 0, stride = 0, views = 0, views_count = 0;
    bool holders = false;
};
SlotsLayout g_sl;
using ItemSlotFn = void* (*)(void*, void*, const void*, void*);
using FrameFn = void (*)(void*, void*);
ItemSlotFn g_item_slot_next = nullptr;
FrameFn g_frame_next = nullptr;

// ---- the thread's collectors ----
struct SlotCollector {
    uintptr_t descriptor = 0;
    float inner[4] = {};
    bool inner_seen = false;
};
thread_local SlotCollector* t_slot = nullptr;

struct PageInfo {
    uint32_t holder = 0, ordinal = 0;
};
struct FrameCollector {
    std::vector<KilnUiVisibleSlot>* slots = nullptr;  // reused buffer of the UI thread
    bool overflow = false;
    bool holders_read = false;
    bool holders_valid = false;
    std::unordered_map<uint32_t, PageInfo>* pages = nullptr;
};
thread_local FrameCollector* t_frame = nullptr;

struct Frame {
    KilnUiSlotsFrame meta{};
    std::vector<KilnUiVisibleSlot> slots;
};
struct Ring {
    std::mutex mu;
    std::shared_ptr<const Frame> latest, previous;
    // holders' lifetime serials
    std::unordered_map<uint32_t, std::pair<uint64_t, uint64_t>> holders;  // holder -> (serial, models read index)
    uint64_t holders_session = 0, reads = 0, next_serial = 1;
};
Ring& ring() {
    static Ring* r = new Ring;  // never destroyed: the UI thread may publish while the process ends
    return *r;
}

bool rect_ok(const float* r) {
    for (int i = 0; i < 4; ++i)
        if (!std::isfinite(r[i])) return false;
    return r[2] > 0 && r[3] > 0;
}

// The pages of the frame's play data: holder and ordinal of each page entity.
bool read_holders(std::unordered_map<uint32_t, PageInfo>& pages) {
    const SlotsLayout& L = g_sl;
    if (!L.holders) return false;
    uintptr_t app = 0, data = 0, state = 0, play = 0, models = 0;
    uint64_t count = 0;
    if (!game_read(L.ui_app, &app) || !game_read(app + L.data, &data) || !game_read(data, &state) ||
        !game_read(state + L.play, &play) || !game_read(play + L.models, &models) ||
        !game_read(play + L.models_count, &count) || count > 64 || (count && !models))
        return false;
    uint8_t view[kViewBytes];
    std::unordered_map<uint32_t, uint32_t> ordinals;
    for (uint64_t m = 0; m < count; ++m) {
        const uintptr_t model = models + m * static_cast<uint64_t>(L.stride);
        uint32_t owner = 0;
        uintptr_t views = 0;
        uint64_t n = 0;
        if (!game_read(model + L.owner, &owner) || !game_read(model + L.views, &views) ||
            !game_read(model + L.views_count, &n) || n > 4096 || (n && !views))
            return false;
        uint32_t& ordinal = ordinals[owner];
        for (uint64_t i = 0; i < n; ++i) {
            if (!env().read(view, views + i * kViewBytes, kViewBytes)) return false;
            uint32_t page;
            std::memcpy(&page, view + L.view.page, 4);
            if (owner && page && pages.try_emplace(page, PageInfo{owner, ordinal}).second) ++ordinal;
        }
    }
    return true;
}

void record(FrameCollector& f, const SlotCollector& c, uintptr_t out) {
    if (f.overflow) return;
    if (f.slots->size() >= kSlotsBudget) {
        f.overflow = true;
        return;
    }
    KilnUiVisibleSlot s{};
    s.size = sizeof s;
    s.occurrence = static_cast<uint32_t>(f.slots->size());
    float outer[4];
    if (game_read(out, &outer)) {
        if (rect_ok(outer)) {
            s.available |= KUI_OUTER_RECT;
            s.outer_ui = KilnUiRect{outer[0], outer[1], outer[2], outer[3]};
        }
    }
    if (env().read(s.native_state, out + static_cast<uintptr_t>(g_sl.state), sizeof s.native_state))
        s.available |= KUI_NATIVE_STATE;
    if (c.inner_seen && rect_ok(c.inner)) {
        s.available |= KUI_INNER_RECT;
        s.inner_ui = KilnUiRect{c.inner[0], c.inner[1], c.inner[2], c.inner[3]};
    }
    uintptr_t view = 0;
    uint8_t raw[kViewBytes];
    if (game_read(c.descriptor, &view) && env().read(raw, view, sizeof raw)) {
        KilnUiItemView v{};
        fill_view(raw, g_sl.view, 0, &v);
        uint32_t page;
        std::memcpy(&page, raw + g_sl.view.page, 4);
        s.inventory_page = page;
        s.slot_index = raw[g_sl.view.index];
        s.item = v.item;
        s.count = v.count;
        s.client_entity = v.client_entity;
        s.view_flags = v.flags;
        if (!f.holders_read) {
            f.holders_read = true;
            f.holders_valid = read_holders(*f.pages);  // UiApp exists only while the frame runs
            if (!f.holders_valid) f.pages->clear();  // never publish a partial holder map
        }
        const auto it = f.pages->find(page);
        if (page && it != f.pages->end()) {
            s.holder = it->second.holder;
            s.page_ordinal = it->second.ordinal;
            s.holder_flags = KUI_HOLDER_KNOWN | KUI_HOLDER_ORDINAL;
        }
    }
    f.slots->push_back(s);
}

void* hk_item_slot(void* out, void* descriptor, const void* options, void* context) {
    SlotCollector c;
    c.descriptor = reinterpret_cast<uintptr_t>(descriptor);
    SlotCollector* outer = t_slot;
    t_slot = &c;
    void* result = g_item_slot_next(out, descriptor, options, context);
    t_slot = outer;
    if (FrameCollector* f = t_frame) record(*f, c, reinterpret_cast<uintptr_t>(out));
    return result;
}

void KILN_CALL on_geometry(KilnRegisters* r, void*) {
    SlotCollector* c = t_slot;
    if (!c || c->descriptor != r->r15) return;
    c->inner_seen = game_read(r->rax, &c->inner);
}

void publish(FrameCollector& f, uintptr_t sys10) {
    auto frame = std::make_shared<Frame>();
    KilnUiSlotsFrame& m = frame->meta;
    m.size = sizeof m;
    ClientNow now{};
    if (env().client) env().client(&now);
    m.local_player = now.local_player;
    m.session = now.session;
    KilnUiScreen screen{};
    if (env().screen && env().screen(&screen)) {
        m.screen = screen;  // kiln.ui published this frame's screen inside the chain
        m.frame = screen.frame;
    }
    m.capture_ms = env().tick_ms ? env().tick_ms() : 0;
    const uintptr_t sys = sys10 - static_cast<uintptr_t>(g_sl.frame_arg);
    float vp[4] = {};
    if (sys10 && game_read(sys + g_sl.canvas_w, &m.canvas_width) && game_read(sys + g_sl.canvas_h, &m.canvas_height) &&
        game_read(sys + g_sl.ppu, &m.pixels_per_unit) && game_read(sys + g_sl.viewport, &vp)) {
        m.viewport_px = KilnUiRect{vp[0], vp[1], vp[2], vp[3]};
        auto in = [](float v, float lo, float hi) { return std::isfinite(v) && v >= lo && v <= hi; };
        if (in(m.pixels_per_unit, 0.05f, 50.0f) && in(m.canvas_width, 100.0f, 100000.0f) &&
            in(m.canvas_height, 100.0f, 100000.0f) && in(vp[2], 1.0f, 65536.0f) && in(vp[3], 1.0f, 65536.0f) &&
            in(vp[0], -65536.0f, 65536.0f) && in(vp[1], -65536.0f, 65536.0f))
            m.transform_flags = KUI_VIEWPORT;  // KUI_PIXEL_MAPPING_VERIFIED waits for check 05a-c5
    }
    if (f.overflow) {
        m.ok = 0;
        m.reason = KUI_FRAME_BUDGET;
    } else {
        m.ok = 1;
        frame->slots = *f.slots;
        m.count = static_cast<uint32_t>(frame->slots.size());
    }
    {
        Ring& r = ring();
        std::lock_guard lk(r.mu);
        // holders' lifetime serials
        if (r.holders_session != now.session) {
            r.holders.clear();
            r.holders_session = now.session;
        }
        if (f.holders_read && !f.holders_valid) r.holders.clear();
        if (f.holders_valid) {
            const uint64_t read = ++r.reads;
            for (const auto& [page, info] : *f.pages) {  // every holder of this frame's models, drawn or not
                auto [it, fresh] = r.holders.try_emplace(info.holder, std::make_pair(0ull, 0ull));
                if (fresh || it->second.second + 1 < read) it->second.first = r.next_serial++;  // new, or back again
                it->second.second = read;
            }
            std::erase_if(r.holders, [&](const auto& kv) { return kv.second.second != read; });
            for (auto& s : frame->slots) {
                if (!(s.holder_flags & KUI_HOLDER_KNOWN)) continue;
                s.holder_serial = r.holders[s.holder].first;
                if (now.local_player && s.holder == now.local_player) s.holder_flags |= KUI_HOLDER_LOCAL_PLAYER;
            }
        }
        for (auto& s : frame->slots)
            if (m.transform_flags & KUI_VIEWPORT) s.available |= KUI_VIEWPORT;
        r.previous = std::move(r.latest);
        r.latest = frame;
    }
    // subscribers, on this (the UI) thread after the frame
    const auto subs = providers(kListSlots);
    for (const auto& sp : *subs) {
        Provider& p = *sp;
        if (!provider_live(p)) continue;
        if (env().call_slots &&
            env().call_slots(reinterpret_cast<KilnUiSlotsFn>(p.fn), &m, frame->slots.data(), m.count, p.user))
            succeeded(p);
        else
            fault(p, "faulted");
    }
}

void hk_frame(void* sys10, void* p2) {
    thread_local std::vector<KilnUiVisibleSlot>* t_buffer = nullptr;  // never freed: no TLS destructor in kiln-core
    thread_local std::unordered_map<uint32_t, PageInfo>* t_pages = nullptr;
    if (!t_buffer) {
        t_buffer = new std::vector<KilnUiVisibleSlot>;
        t_buffer->reserve(256);
        t_pages = new std::unordered_map<uint32_t, PageInfo>;
    }
    FrameCollector* outer = t_frame;
    if (outer) {  // a frame inside a frame (not seen): the inner one is not collected separately
        g_frame_next(sys10, p2);
        return;
    }
    t_buffer->clear();
    t_pages->clear();
    FrameCollector f;
    f.slots = t_buffer;
    f.pages = t_pages;
    t_frame = &f;
    g_frame_next(sys10, p2);
    t_frame = nullptr;
    publish(f, reinterpret_cast<uintptr_t>(sys10));
}

bool install_slots(uint32_t list) {
    if (list != kListSlots) return false;
    if (!env().row_available || !env().row_available("ui.screens")) {
        logf(KILN_LOG_ERROR, "ui.values: ui.screens (kiln.ui's frame backend) is not available: no slot inspection");
        return false;
    }
    SlotsLayout& L = g_sl;
    int64_t result = 0;
    uintptr_t item_slot = 0, geometry = 0, frame = 0;
    if (!load_view_layout(&L.view) || !sig_constant("ui/slot.resultBytes", &result) ||
        result != static_cast<int64_t>(kSlotResultBytes) || !sig_constant("ui/slot.hoverCandidate", &L.state) ||
        L.state < 0 || L.state + 16 > result || !sig_constant("ui/sys.frame_arg", &L.frame_arg) ||
        !sig_constant("ui/sys.canvas_w", &L.canvas_w) || !sig_constant("ui/sys.canvas_h", &L.canvas_h) ||
        !sig_constant("ui/sys.viewport", &L.viewport) || !sig_constant("ui/sys.px_per_unit", &L.ppu) ||
        !sig_symbol("ui/ItemSlot", &item_slot) || !sig_symbol("ui/SlotGeometry", &geometry) ||
        !sig_symbol("ui/GameUiFrame", &frame)) {
        logf(KILN_LOG_ERROR, "ui.values: the slot symbols or constants did not resolve");
        return false;
    }
    L.holders = sig_symbol("ui/UiApp", &L.ui_app) && sig_constant("ui/ui.data", &L.data) &&
                sig_constant("ui/state.play", &L.play) && sig_constant("ui/play.models", &L.models) &&
                sig_constant("ui/play.modelsCount", &L.models_count) && sig_constant("ui/model.owner", &L.owner) &&
                sig_constant("ui/model.stride", &L.stride) && sig_constant("ui/model.views", &L.views) &&
                sig_constant("ui/model.viewsCount", &L.views_count) && L.stride > 0;
    if (!L.holders) logf(KILN_LOG_WARN, "ui.values: the inventory models are not readable: slots carry no holder");
    // the geometry mid first, then the slot wrapper, then the frame: each one only records into the one after it
    KilnResult rc = env().hook_mid(geometry, "ui.values/SlotGeometry", &on_geometry);
    if (rc == KILN_OK)
        rc = env().hook_raw(item_slot, "ui.values/ItemSlot", 0, reinterpret_cast<void*>(&hk_item_slot),
                            reinterpret_cast<void**>(&g_item_slot_next));
    if (rc == KILN_OK)
        rc = env().hook_raw(frame, "ui.values/GameUiFrame", 100, reinterpret_cast<void*>(&hk_frame),
                            reinterpret_cast<void**>(&g_frame_next));
    if (rc != KILN_OK || !g_item_slot_next || !g_frame_next) {
        logf(KILN_LOG_ERROR, "ui.values: hooking the slot sites failed (%d)", rc);
        return false;
    }
    return true;
}

struct SlotsInstaller {
    SlotsInstaller() { set_installer(kListSlots, &install_slots); }
} g_slots_installer;

}  // namespace

KilnResult slots_copy(KilnOwner owner, uint64_t expected_session, uint64_t expected_frame, KilnUiSlotsFrame* frame,
                      KilnUiVisibleSlot* slots, uint32_t capacity, uint32_t* required) {
    const uint32_t stride = capacity ? slots[0].size : static_cast<uint32_t>(sizeof(KilnUiVisibleSlot));
    if (capacity && stride < kRecordMin) return KILN_ERR_INVALID_ARGUMENT;
    if (!env().client_side || !env().client_side()) return KILN_ERR_UNAVAILABLE;
    if (!env().owner_alive(owner)) return KILN_ERR_REVOKED;
    if (!ensure_installed(kListSlots)) return KILN_ERR_UNAVAILABLE;
    *required = 0;
    ClientNow now{};
    const bool have_client = env().client && env().client(&now);
    std::shared_ptr<const Frame> pick;
    {
        Ring& r = ring();
        std::lock_guard lk(r.mu);
        if (!r.latest) {
            const uint32_t size = frame->size;
            *frame = KilnUiSlotsFrame{};
            frame->size = size;
            frame->reason = KUI_NO_FRAME;
            return KILN_OK;
        }
        if (expected_session == 0 && expected_frame == 0) {
            pick = r.latest;
        } else {
            for (const auto& f : {r.latest, r.previous})
                if (f && f->meta.session == expected_session && f->meta.frame == expected_frame) pick = f;
        }
    }
    if (!have_client || (pick && pick->meta.session != now.session)) pick.reset();
    const uint32_t size = frame->size;
    if (!pick) {
        *frame = KilnUiSlotsFrame{};
        frame->size = size;
        frame->reason = KUI_STALE_FRAME;
        return KILN_OK;
    }
    *frame = pick->meta;  // the whole metadata, also when the records do not fit
    frame->size = size;
    *required = pick->meta.count;
    if (!pick->meta.ok) return KILN_OK;
    if (capacity < pick->meta.count) return KILN_ERR_BUFFER_TOO_SMALL;
    const size_t n = std::min<size_t>(stride, sizeof(KilnUiVisibleSlot));
    auto* out = reinterpret_cast<uint8_t*>(slots);
    for (uint32_t i = 0; i < pick->meta.count; ++i) {
        KilnUiVisibleSlot s = pick->slots[i];
        s.size = static_cast<uint32_t>(n);
        std::memcpy(out + static_cast<size_t>(i) * stride, &s, n);
    }
    return KILN_OK;
}

void slots_reset_for_tests() {
    Ring& r = ring();
    std::lock_guard lk(r.mu);
    r.latest.reset();
    r.previous.reset();
    r.holders.clear();
    r.holders_session = r.reads = 0;
    r.next_serial = 1;
    g_sl = {};
    g_item_slot_next = nullptr;
    g_frame_next = nullptr;
}

}  // namespace kiln::uivalues
