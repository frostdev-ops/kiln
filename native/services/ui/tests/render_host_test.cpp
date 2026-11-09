// SPDX-License-Identifier: MIT
// Exercise the actual render service with host ImGui and OS/hook stand-ins, under ASan/UBSan.
#include <cassert>
#include <future>
#include <thread>
#include <limits>
#include "../render.cpp"
#include "../slider.h"

namespace kui {
const KilnApi* g_api = nullptr;
static thread_local bool drawing = false;
bool in_frame() { return drawing; }
KilnHandle next_handle() { static std::atomic<KilnHandle> h{1}; return h++; }
void logf(int, const char*, ...) {}
void vk_status(KilnUiStatus* out) { out->width = 1920; out->height = 1080; }
bool image_texture(KilnHandle, ImTextureRef*) { return false; }
}

int main() {
    // Unknown build: no hook is installed, screen APIs remain registered.
    KilnApi api{};
    api.feature = [](KilnStr) -> bool { return false; };
    kui::g_api = &api;
    assert(!kui::render_install());
    KilnRenderCamera camera{};
    camera.size = sizeof camera;
    assert(t_camera(&camera) == KILN_ERR_UNAVAILABLE && !camera.available && !camera.valid);
    assert(t_acquire(1, nullptr) == KILN_ERR_INVALID_ARGUMENT);
    KilnHandle handle = 0;
    assert(t_acquire(1, &handle) == KILN_OK && kui::g_render_demand == 1);
    assert(t_release(handle) == KILN_OK && kui::g_render_demand == 0);
    assert(t_release(handle) == KILN_ERR_NOT_FOUND);

    // Copy and metadata stay coherent while a main-thread writer and multiple readers contend.
    g_cam_on = true;
    std::atomic<bool> done{false};
    std::thread writer([&] {
        for (int i = 1; i <= 20000; ++i) {
            std::lock_guard lk(g_cam_mu);
            g_cam.pos[0] = i; g_cam.pos[1] = i * 2; g_cam.pos[2] = i * 3;
            g_cam_frames = i;
            g_cam_ms = GetTickCount64();
        }
        done = true;
    });
    auto reader = [&] {
        do {
            CamData d{}; uint64_t frame = 0, age = 0;
            if (snapshot(&d, &frame, &age)) {
                assert(d.pos[0] == static_cast<int64_t>(frame));
                assert(d.pos[1] == d.pos[0] * 2 && d.pos[2] == d.pos[0] * 3);
                assert(age < 60000);  // no unsigned underflow
            }
        } while (!done);
    };
    std::thread r1(reader), r2(reader);
    writer.join(); r1.join(); r2.join();
    // A contended read returns immediately instead of stalling the render thread.
    std::unique_lock held(g_cam_mu);
    auto read = std::async(std::launch::async, [] {
        CamData d{}; uint64_t f, a; return snapshot(&d, &f, &a);
    });
    assert(read.wait_for(std::chrono::seconds(2)) == std::future_status::ready);
    assert(!read.get());
    held.unlock();

    ImGui::CreateContext();
    auto& io = ImGui::GetIO();
    io.IniFilename = nullptr;
    io.DisplaySize = ImVec2(1920, 1080);
    io.Fonts->AddFontDefault();
    io.Fonts->Build();
    ImGui::NewFrame();
    const float inf = std::numeric_limits<float>::infinity();
    const float nan = std::numeric_limits<float>::quiet_NaN();
    t_line(0, 0, 10, 10, ~0u, 1);  // out of callback: no-op
    kui::drawing = true;
    auto* d = ImGui::GetBackgroundDrawList();
    const int before = d->VtxBuffer.Size;
    t_line(0, 0, 10, 10, ~0u, inf);
    t_rect(0, 0, 10, 10, ~0u, nan, 1, 1);
    t_circle(10, 10, 4, ~0u, 0, inf, 0);
    t_triangle(0, 0, 10, 0, 5, 10, ~0u, inf, 0);
    float pts[] = {0, 0, 5, 5, 10, 0};
    t_polyline(pts, 3, ~0u, nan, 0, 0);
    assert(d->VtxBuffer.Size == before);
    t_polyline(pts, 3, ~0u, 2, 1, 0);
    t_text(10, 10, ~0u, 16, KILN_RENDER_TEXT_OUTLINE, {"hello", 5});
    assert(d->VtxBuffer.Size > before);
    for (const auto& v : d->VtxBuffer) assert(std::isfinite(v.pos.x) && std::isfinite(v.pos.y));
    kui::drawing = false;
    ImGui::Render();

    // Real mouse input must select values inside a tiny float range. ImGui's default %.3f rounds this to 0.
    constexpr float tiny_min = 1e-5f, tiny_max = 2e-5f;
    float tiny = tiny_min;
    ImVec2 tiny_start{}, tiny_end{};
    io.ConfigInputTrickleEventQueue = false;
    auto slider_frame = [&](ImVec2 mouse, bool down) {
        io.AddMousePosEvent(mouse.x, mouse.y);
        io.AddMouseButtonEvent(0, down);
        ImGui::NewFrame();
        ImGui::SetNextWindowPos(ImVec2(20, 20));
        ImGui::SetNextWindowSize(ImVec2(400, 100));
        ImGui::Begin("slider input test", nullptr, ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoMove | ImGuiWindowFlags_NoSavedSettings);
        ImGui::SetNextItemWidth(240);
        const bool changed = kui::slider_float("##tiny", &tiny, tiny_min, tiny_max);
        tiny_start = ImGui::GetItemRectMin();
        tiny_end = ImGui::GetItemRectMax();
        ImGui::End();
        ImGui::Render();
        return changed;
    };
    slider_frame(ImVec2(-100, -100), false);
    const ImVec2 middle((tiny_start.x + tiny_end.x) * 0.5f, (tiny_start.y + tiny_end.y) * 0.5f);
    slider_frame(middle, false); // hover the actual widget before pressing
    assert(slider_frame(middle, true));
    assert(std::isfinite(tiny) && tiny > tiny_min && tiny < tiny_max);
    const float midpoint = tiny;
    const ImVec2 three_quarters(tiny_start.x + (tiny_end.x - tiny_start.x) * 0.75f, middle.y);
    assert(slider_frame(three_quarters, true));
    assert(std::isfinite(tiny) && tiny > midpoint && tiny < tiny_max);
    const float selected = tiny;
    slider_frame(three_quarters, false);
    assert(tiny == selected);
    std::printf("float_slider: actual mouse selected %.9g in [%.9g, %.9g], release kept it\n", tiny, tiny_min, tiny_max);
    ImGui::DestroyContext();

    // A snapshot cannot call a canceled callback, and unsubscribe waits for an in-flight call.
    auto state = std::make_shared<kui::CallbackLifetime>();
    std::promise<void> entered, finish;
    auto release = finish.get_future();
    std::thread callback([&] {
        std::lock_guard call(state->gate);
        assert(state->active);
        entered.set_value();
        release.wait();
    });
    entered.get_future().wait();
    assert(!state->drain(true).owns_lock()); // cross-callback cancellation must not block
    auto unsubscribe = std::async(std::launch::async, [&] {
        state->active.store(false, std::memory_order_release);
        auto drained = state->drain();
        assert(drained.owns_lock());
    });
    assert(unsubscribe.wait_for(std::chrono::milliseconds(10)) == std::future_status::timeout);
    finish.set_value();
    callback.join(); unsubscribe.get();
    { std::lock_guard call(state->gate); assert(!state->active); assert(state->drain(true).owns_lock()); } // self-unsubscribe is reentrant
    std::puts("render_host: all checks passed");
}
