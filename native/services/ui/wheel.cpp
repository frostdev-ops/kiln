// SPDX-License-Identifier: MIT
// kiln.ui: the radial wheel (wheel_open / wheel_close / wheel_active in native/core/abi/services/kiln_ui.h).
//  - One wheel at a time. While it is open it counts as a capture (capturing() in ui.h): the game gets no mouse or key
//    presses (releases still reach it), the cursor is free, and the pointer steers the highlight.
//  - The rules (pages, hit test, hold key, tap, cancel) are wheel_math.h's Model, tested on the host. This file feeds it
//    the pointer, the clicks and the mouse wheel from ImGui, and Esc and the hold key's release from the window thread
//    (wheel_on_key), and draws the result.
//  - It is drawn in the middle of the screen on a full-screen ImGui window with no background that sits on top of the other
//    windows, so nothing below it gets the pointer. When no wheel is open none of this runs: build_frame asks one atomic.
//  - The callback runs on the render thread after the wheel was taken out, so it may open another wheel. When the overlay
//    turns off (a Vulkan error or fault) an open wheel is cancelled at the next present (wheel_cancel_undrawable): it could
//    never end otherwise, and the game would keep its input captured.
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include "imgui.h"
#include "ui.h"
#include "wheel_math.h"

namespace kui {

std::atomic<bool> g_wheel_active{false};

namespace {

struct Wheel {
    KilnHandle handle = 0;
    KilnOwner owner = 0;
    std::string title;
    std::vector<std::string> items;
    uint32_t hold_key = 0;
    KilnUiWheelFn fn = nullptr;
    void* user = nullptr;
    uint64_t opened_ms = 0;
    kwheel::Model model{0, false, 0, 0};
    // from the window thread
    std::atomic<bool> ready{false};      // armed: the frame may step the model
    std::atomic<bool> escape{false};
    std::atomic<bool> released{false};
    std::atomic<uint64_t> release_ms{0};
    uint32_t frames = 0;                 // frames drawn (render thread)
};

std::mutex g_mu;  // g_wheel
std::shared_ptr<Wheel> g_wheel;
// Held by the render thread while it runs the callback, so that wheel_close can wait for a callback in flight.
std::recursive_mutex g_gate;

bool guarded_wheel_fn(KilnUiWheelFn fn, int32_t choice, void* user) {
    __try {
        fn(choice, user);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

float measure(const char* b, const char* e, float size) { return ImGui::GetFont()->CalcTextSizeA(size, FLT_MAX, 0.0f, b, e).x; }

ImU32 tint(ImVec4 c, float alpha) {
    c.w *= alpha;
    return ImGui::ColorConvertFloat4ToU32(c);
}

// One ring segment between two clock angles.
void ring_segment(ImDrawList* dl, ImVec2 c, float r0, float r1, float a0, float a1, ImU32 fill, ImU32 line, float thickness) {
    const float p0 = a0 - kwheel::kPi * 0.5f, p1 = a1 - kwheel::kPi * 0.5f;
    dl->PathArcTo(c, r1, p0, p1);
    dl->PathArcTo(c, r0, p1, p0);
    dl->PathFillConcave(fill);
    dl->PathArcTo(c, r1, p0, p1);
    dl->PathArcTo(c, r0, p1, p0);
    dl->PathStroke(line, ImDrawFlags_Closed, thickness);
}

void text_shadowed(ImDrawList* dl, ImVec2 p, ImU32 col, float size, const std::string& s) {
    ImFont* f = ImGui::GetFont();
    dl->AddText(f, size, ImVec2(p.x + 1, p.y + 1), IM_COL32(0, 0, 0, 170), s.data(), s.data() + s.size());
    dl->AddText(f, size, p, col, s.data(), s.data() + s.size());
}

// A line of text centred on x, top at y; returns its height.
float text_centered(ImDrawList* dl, float x, float y, ImU32 col, float size, const std::string& s) {
    if (s.empty()) return 0;
    const float w = measure(s.data(), s.data() + s.size(), size);
    text_shadowed(dl, ImVec2(x - w * 0.5f, y), col, size, s);
    return size;
}

void draw_wheel(Wheel& w, float width, float height, float dpi, int highlight) {
    ImDrawList* dl = ImGui::GetWindowDrawList();
    const ImGuiStyle& st = ImGui::GetStyle();
    const float size = ImGui::GetFontSize();
    const float small = size * 0.85f;
    const ImVec2 c(width * 0.5f, height * 0.5f);
    const kwheel::Rings rg = kwheel::rings(width, height, dpi);
    const int n = w.model.page_items();
    const int first = w.model.entry_of(0);
    const float span = kwheel::sector_span(n);
    const float mid_r = (rg.inner + rg.outer) * 0.5f + (rg.outer - rg.inner) * 0.08f;
    const ImU32 base = tint(st.Colors[ImGuiCol_PopupBg], 0.88f);
    const ImU32 lit = tint(st.Colors[ImGuiCol_HeaderHovered], 1.0f);
    const ImU32 line = tint(st.Colors[ImGuiCol_Border], 1.0f);
    const ImU32 text = ImGui::ColorConvertFloat4ToU32(st.Colors[ImGuiCol_Text]);
    const ImU32 dim = ImGui::ColorConvertFloat4ToU32(st.Colors[ImGuiCol_TextDisabled]);
    const float gap = std::max(3.0f * dpi / rg.outer, 0.012f);  // a thin gap between the sectors

    dl->AddRectFilled(ImVec2(0, 0), ImVec2(width, height), IM_COL32(0, 0, 0, 70));  // dims the screen a little
    for (int s = 0; s < n; ++s) {
        const float a = kwheel::sector_center(s, n);
        const bool hot = first + s == highlight;
        ring_segment(dl, c, rg.inner, rg.outer, a - span * 0.5f + gap * 0.5f, a + span * 0.5f - gap * 0.5f, hot ? lit : base, line,
                     hot ? 2.0f * dpi : 1.0f * dpi);
    }
    // the labels: name, and the details on a second line
    for (int s = 0; s < n; ++s) {
        const float a = kwheel::sector_center(s, n);
        float ox, oy;
        kwheel::polar(a, mid_r, &ox, &oy);
        const float chord = 2.0f * mid_r * std::sin(std::min(span, kwheel::kPi) * 0.5f) * 0.88f;
        const float max_w = std::clamp(chord, 80.0f * dpi, 200.0f * dpi);
        std::string_view name, details;
        kwheel::split_label(w.items[static_cast<size_t>(first + s)], &name, &details);
        auto fit = [&](std::string_view t, float px) {
            return kwheel::ellipsize(t, max_w, [&](std::string_view v) { return measure(v.data(), v.data() + v.size(), px); });
        };
        const std::string n1 = fit(name, size), n2 = details.empty() ? std::string() : fit(details, small);
        const float h = size + (n2.empty() ? 0.0f : small + 2.0f * dpi);
        float y = c.y + oy - h * 0.5f;
        const bool hot = first + s == highlight;
        y += text_centered(dl, c.x + ox, y, hot ? IM_COL32(255, 255, 255, 255) : text, size, n1);
        if (!n2.empty()) text_centered(dl, c.x + ox, y + 2.0f * dpi, hot ? IM_COL32(235, 235, 235, 255) : dim, small, n2);
    }
    // the middle: the title, the page
    dl->AddCircleFilled(c, rg.inner - 3.0f * dpi, tint(st.Colors[ImGuiCol_PopupBg], 0.78f), 48);
    dl->AddCircle(c, rg.inner - 3.0f * dpi, line, 48, 1.0f * dpi);
    const int pages = w.model.pages();
    const float block = (w.title.empty() ? 0.0f : size) + (pages > 1 ? small + 2.0f * dpi : 0.0f);
    float y = c.y - block * 0.5f;
    if (!w.title.empty()) {
        const std::string t = kwheel::ellipsize(w.title, rg.inner * 1.7f, [&](std::string_view v) { return measure(v.data(), v.data() + v.size(), size); });
        y += text_centered(dl, c.x, y, text, size, t);
    }
    if (pages > 1) text_centered(dl, c.x, y + 2.0f * dpi, dim, small, std::to_string(w.model.page() + 1) + " / " + std::to_string(pages));
    // under the ring: the highlighted entry in full, and what the keys do
    float by = c.y + rg.outer + 12.0f * dpi;
    if (highlight >= 0) {
        std::string_view name, details;
        kwheel::split_label(w.items[static_cast<size_t>(highlight)], &name, &details);
        by += text_centered(dl, c.x, by, IM_COL32(255, 255, 255, 255), size, std::string(name));
        if (!details.empty()) by += text_centered(dl, c.x, by + 2.0f * dpi, dim, small, std::string(details)) + 2.0f * dpi;
    }
    std::string hint;
    if (w.model.hold_mode()) {
        char key[32];
        key_name(w.hold_key, key, sizeof key);
        hint = std::string("Release ") + key + " to choose (in the middle: cancel). Right click or Esc: cancel";
    } else {
        hint = "Click to choose. Right click or Esc: cancel";
    }
    if (pages > 1) hint += ". Mouse wheel: page";
    text_centered(dl, c.x, by + 6.0f * dpi, dim, small, hint);
}

// Ends `w` with `choice` and calls its fn, unless wheel_close took it out first (then fn must not run: exactly once). The
// gate is taken before the wheel is taken out, so a wheel_close that finds nothing waits until fn has returned. Only the
// fn call runs under the gate (it is SEH-guarded), never the ImGui drawing: a fault there cannot leave the gate locked.
void finish(const std::shared_ptr<Wheel>& w, int choice) {
    std::lock_guard<std::recursive_mutex> gate(g_gate);
    {
        std::lock_guard<std::mutex> lk(g_mu);
        if (g_wheel != w) return;  // closed (wheel_close) in the meantime: no callback
        g_wheel.reset();
        g_wheel_active.store(false, std::memory_order_relaxed);
    }
    logf(KILN_LOG_DEBUG, "ui: wheel %s", choice >= 0 ? ("picked entry " + std::to_string(choice)).c_str() : "cancelled");
    if (!guarded_wheel_fn(w->fn, choice, w->user)) set_error("a wheel callback faulted");
}

}  // namespace

void wheel_frame(float width, float height, float dpi) {
    std::shared_ptr<Wheel> w;
    {
        std::lock_guard<std::mutex> lk(g_mu);
        w = g_wheel;
    }
    if (!w || !w->ready.load(std::memory_order_acquire)) return;
    ImGuiIO& io = ImGui::GetIO();
    const kwheel::Rings rg = kwheel::rings(width, height, dpi);
    w->model.set_dead_radius(rg.inner);

    // a full-screen window with no background on top of the others: it takes the pointer from everything below
    ImGui::SetNextWindowPos(ImVec2(0, 0), ImGuiCond_Always);
    ImGui::SetNextWindowSize(ImVec2(width, height), ImGuiCond_Always);
    ImGui::SetNextWindowFocus();
    ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, ImVec2(0, 0));
    ImGui::PushStyleVar(ImGuiStyleVar_WindowBorderSize, 0.0f);
    constexpr ImGuiWindowFlags kFlags = ImGuiWindowFlags_NoDecoration | ImGuiWindowFlags_NoBackground | ImGuiWindowFlags_NoMove |
                                        ImGuiWindowFlags_NoSavedSettings | ImGuiWindowFlags_NoNav | ImGuiWindowFlags_NoScrollWithMouse;
    const bool visible = ImGui::Begin("##kiln.wheel", nullptr, kFlags);

    kwheel::Inputs in;
    const bool pointer = ImGui::IsMousePosValid(&io.MousePos);
    if (pointer) {
        in.dx = io.MousePos.x - width * 0.5f;
        in.dy = io.MousePos.y - height * 0.5f;
    } else {
        in.dx = in.dy = NAN;  // no highlight
    }
    const bool first_frame = w->frames == 0;
    in.left_click = !first_frame && ImGui::IsMouseClicked(ImGuiMouseButton_Left, false);  // not the click that opened it
    in.right_click = !first_frame && ImGui::IsMouseClicked(ImGuiMouseButton_Right, false);
    in.escape = w->escape.exchange(false);
    in.turn = io.MouseWheel < 0 ? 1 : io.MouseWheel > 0 ? -1 : 0;
    if (w->released.exchange(false)) {
        in.hold_released = true;
        in.release_ms = w->release_ms.load();
    }
    const kwheel::Step step = w->model.step(in);
    if (visible) draw_wheel(*w, width, height, dpi, step.highlight);
    ++w->frames;
    ImGui::End();
    ImGui::PopStyleVar(2);
    if (step.kind == kwheel::Step::Pick) finish(w, step.choice);
    else if (step.kind == kwheel::Step::Cancel) finish(w, -1);
}

void wheel_cancel_undrawable() {
    std::shared_ptr<Wheel> w;
    {
        std::lock_guard<std::mutex> lk(g_mu);
        w = g_wheel;
    }
    if (!w) return;
    logf(KILN_LOG_WARN, "ui: the overlay is off: the open wheel is cancelled (the game gets its input back)");
    finish(w, -1);
}

bool wheel_on_key(const KilnUiKey& k) {
    std::shared_ptr<Wheel> w;
    {
        std::lock_guard<std::mutex> lk(g_mu);
        w = g_wheel;
    }
    if (!w) return false;
    if (k.down) {
        if (k.vk == VK_ESCAPE && !k.repeat) {
            w->escape.store(true);
            return true;
        }
        return false;
    }
    if (w->hold_key && k.vk == w->hold_key) {
        w->release_ms.store(now_ms());
        w->released.store(true);
    }
    return false;  // releases always reach the game
}

KilnResult wheel_open(KilnOwner owner, const KilnUiWheel* def, KilnUiWheelFn fn, void* user, KilnHandle* out) {
    if (!def || !fn || def->size < sizeof(KilnUiWheel) || def->flags != 0) return KILN_ERR_INVALID_ARGUMENT;
    if (def->count < 1 || def->count > static_cast<uint32_t>(kwheel::kMaxItems) || !def->items) return KILN_ERR_INVALID_ARGUMENT;
    if (!kwheel::valid_hold_key(def->hold_key)) return KILN_ERR_INVALID_ARGUMENT;
    if ((def->title.len && !def->title.ptr)) return KILN_ERR_INVALID_ARGUMENT;
    auto w = std::make_shared<Wheel>();
    for (uint32_t i = 0; i < def->count; ++i) {
        const KilnStr s = def->items[i];
        if (s.len && !s.ptr) return KILN_ERR_INVALID_ARGUMENT;
        w->items.push_back(kwheel::clean_text(std::string_view(s.ptr ? s.ptr : "", s.ptr ? s.len : 0), kwheel::kMaxLabelBytes));
    }
    w->title = kwheel::clean_text(std::string_view(def->title.ptr ? def->title.ptr : "", def->title.ptr ? def->title.len : 0), kwheel::kMaxTitleBytes);
    w->handle = next_handle();
    w->owner = owner;
    w->hold_key = def->hold_key;
    w->fn = fn;
    w->user = user;
    w->opened_ms = now_ms();
    if (g_menu_open.load()) return KILN_ERR_UNAVAILABLE;  // the Kiln menu has the input
    KilnUiStatus status{};
    vk_status(&status);
    if (!input_attached() || !status.swapchain) return KILN_ERR_UNAVAILABLE;  // nothing could draw it
    w->model = kwheel::Model(static_cast<int>(def->count), false, w->opened_ms, kwheel::rings(static_cast<float>(status.width), static_cast<float>(status.height), status.dpi_scale).inner);
    {
        std::lock_guard<std::mutex> lk(g_mu);
        if (g_wheel) return KILN_ERR_UNAVAILABLE;  // one wheel at a time
        g_wheel = w;
        g_wheel_active.store(true, std::memory_order_relaxed);
    }
    // The hold key counts only when it is down as the wheel opens. It is checked after the wheel can see key events: a
    // release in between is either seen here (the key is up) or delivered to the wheel (and acted on once it is ready).
    if (w->hold_key && input_key_held(w->hold_key)) w->model.set_hold(true);
    w->ready.store(true, std::memory_order_release);
    logf(KILN_LOG_DEBUG, "ui: wheel opened (%u entries%s)", def->count, w->model.hold_mode() ? ", hold key down" : "");
    if (out) *out = w->handle;
    return KILN_OK;
}

KilnResult wheel_close(KilnHandle handle) {
    bool found = false;
    {
        std::lock_guard<std::mutex> lk(g_mu);
        if (g_wheel && g_wheel->handle == handle) {
            g_wheel.reset();
            g_wheel_active.store(false, std::memory_order_relaxed);
            found = true;
        }
    }
    // a callback may be running on the render thread: wait for it (a call from the callback itself passes: the gate is recursive)
    std::lock_guard<std::recursive_mutex> gate(g_gate);
    return found ? KILN_OK : KILN_ERR_NOT_FOUND;
}

}  // namespace kui
