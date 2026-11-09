// SPDX-License-Identifier: MIT
// kiln.render (native/core/abi/services/kiln_render.h): drawing for mods on the overlay's background list (under Kiln's
// own windows), and the camera the game renders with, read where the client writes the local player's
// ClientPlayerInput.camera (sigdb client.d/render.json, docs/re/client-render.md "The camera").
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstring>
#include <mutex>
#include <vector>

#include "imgui.h"
#include "render_math.h"
#include "kiln_hook.hpp"
#include "safetyhook.hpp"  // safetyhook::Context: the camera hook's register view
#include "services/kiln_render.h"
#include "ui.h"

namespace kui {
std::atomic<uint32_t> g_render_demand{0};
}

namespace {
using namespace kui;

// ---- camera ----
struct CamLayout {
    int64_t cam_obj = 0;      // client game -> its camera object pointer
    int64_t cpi_camera = 0;   // ClientPlayerInput -> ClientCamera (WorldPosition 3 x int64 32.32)
    int64_t rotation = 0;     // ClientPlayerInput -> quaternion
    int64_t distance = 0, fov_y = 0, aspect = 0, near_plane = 0, far_plane = 0;
} L;

struct CamData {
    int64_t pos[3];
    float rot[4];
    float distance, fov_y, aspect, near_plane, far_plane;
};

// Copy data and its metadata under the same lock. Readers never wait on the render thread.
std::mutex g_cam_mu;
CamData g_cam{};
std::atomic<uint64_t> g_cam_frames{0}, g_cam_ms{0};
std::atomic<uint64_t> g_draws{0};
std::atomic<bool> g_cam_on{false};
kiln::MidHook2 g_cam_hook;
std::atomic<bool> g_cam_logged{false};
constexpr double kFixed = 1.0 / 4294967296.0;  // WorldPosition: metres in 32.32 fixed point
constexpr uint64_t kCamFreshMs = 500;

bool read_cam(const uint8_t* cpi, CamData* out) {
    __try {
        std::memcpy(out->pos, cpi + L.cpi_camera, sizeof out->pos);
        std::memcpy(out->rot, cpi + L.rotation, sizeof out->rot);
        std::memcpy(&out->distance, cpi + L.distance, 4);
        std::memcpy(&out->fov_y, cpi + L.fov_y, 4);
        std::memcpy(&out->aspect, cpi + L.aspect, 4);
        std::memcpy(&out->near_plane, cpi + L.near_plane, 4);
        std::memcpy(&out->far_plane, cpi + L.far_plane, 4);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool cam_obj_present(uintptr_t game) {
    __try {
        return *reinterpret_cast<const void* const*>(game + L.cam_obj) != nullptr;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool finite_cam(const CamData& d) {
    for (float f : d.rot)
        if (!std::isfinite(f)) return false;
    const float len = d.rot[0] * d.rot[0] + d.rot[1] * d.rot[1] + d.rot[2] * d.rot[2] + d.rot[3] * d.rot[3];
    const float fov = krender::fov_radians(d.fov_y);
    return len > 0.5f && len < 2.0f && std::isfinite(d.fov_y) && d.fov_y > 0 &&
           fov > 0 && fov < 3.14159265f && std::isfinite(d.distance);
}

// Main thread, after the client wrote ClientPlayerInput.camera: rbp = the client game, r14 = the local player's
// ClientPlayerInput. Read only.
void hk_camera(safetyhook::Context& c) {
    if (!c.rbp || !c.r14 || !cam_obj_present(c.rbp)) return;  // no camera object: nothing was written this frame
    CamData d;
    if (!read_cam(reinterpret_cast<const uint8_t*>(c.r14), &d) || !finite_cam(d)) return;
    const float norm = std::sqrt(d.rot[0] * d.rot[0] + d.rot[1] * d.rot[1] + d.rot[2] * d.rot[2] + d.rot[3] * d.rot[3]);
    for (float& r : d.rot) r /= norm;
    {
        std::lock_guard lk(g_cam_mu);
        g_cam = d;
        g_cam_frames.fetch_add(1, std::memory_order_relaxed);
        g_cam_ms.store(GetTickCount64(), std::memory_order_relaxed);
    }
    if (!g_cam_logged.exchange(true))
        logf(KILN_LOG_INFO, "render: first camera at (%.1f, %.1f, %.1f) m, rotation (%.3f, %.3f, %.3f, %.3f), fov %g, aspect %g, "
             "near %g, far %g, distance %g", d.pos[0] * kFixed, d.pos[1] * kFixed, d.pos[2] * kFixed, d.rot[0], d.rot[1],
             d.rot[2], d.rot[3], d.fov_y, d.aspect, d.near_plane, d.far_plane, d.distance);
}

bool snapshot(CamData* out, uint64_t* frames, uint64_t* age) {
    if (!g_cam_on || g_cam_frames.load(std::memory_order_relaxed) == 0) return false;
    std::unique_lock lk(g_cam_mu, std::try_to_lock);
    if (!lk.owns_lock()) return false;
    *out = g_cam;
    *frames = g_cam_frames.load(std::memory_order_relaxed);
    const uint64_t now = GetTickCount64(), written = g_cam_ms.load(std::memory_order_relaxed);
    *age = now >= written ? now - written : 0;
    return true;
}

bool math_camera(krender::Camera* m, uint64_t* age_out = nullptr) {
    CamData d;
    uint64_t frames = 0, age = 0;
    if (!snapshot(&d, &frames, &age)) return false;
    if (age_out) *age_out = age;
    for (int i = 0; i < 3; ++i) m->pos[i] = static_cast<double>(d.pos[i]) * kFixed;
    std::memcpy(m->rot, d.rot, sizeof m->rot);
    m->fov_y = krender::fov_radians(d.fov_y);
    m->near_plane = std::isfinite(d.near_plane) && d.near_plane > 0.01f ? d.near_plane : 0.05f;
    return age <= kCamFreshMs;
}

// ---- drawing ----
ImU32 col(uint32_t rgba) {
    return IM_COL32((rgba >> 24) & 0xFF, (rgba >> 16) & 0xFF, (rgba >> 8) & 0xFF, rgba & 0xFF);
}
ImDrawList* dl() {
    if (!in_frame()) return nullptr;
    g_draws.fetch_add(1, std::memory_order_relaxed);
    return ImGui::GetBackgroundDrawList();
}
bool sane(float v) { return std::isfinite(v) && v > -1e6f && v < 1e6f; }
bool sane2(float x, float y) { return sane(x) && sane(y); }

struct Holder {
    KilnHandle handle;
    KilnOwner owner;
};
std::mutex g_mu;
std::vector<Holder> g_holders;

KilnResult KILN_CALL t_status(KilnRenderStatus* out) {
    if (!out || out->size < sizeof(KilnRenderStatus)) return KILN_ERR_INVALID_ARGUMENT;
    std::memset(out, 0, sizeof *out);
    out->size = sizeof *out;
    out->camera = g_cam_on ? 1 : 0;
    CamData d;
    uint64_t frames = 0, age = 0;
    out->camera_valid = snapshot(&d, &frames, &age) && age <= kCamFreshMs ? 1 : 0;
    out->camera_frames = g_cam_frames.load(std::memory_order_relaxed);
    out->draws = g_draws.load(std::memory_order_relaxed);
    std::lock_guard lk(g_mu);
    out->frame_holders = static_cast<uint32_t>(g_holders.size());
    return KILN_OK;
}

KilnResult KILN_CALL t_acquire(KilnOwner owner, KilnHandle* out) {
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lk(g_mu);
    const KilnHandle h = next_handle();
    g_holders.push_back({h, owner});
    g_render_demand.store(static_cast<uint32_t>(g_holders.size()), std::memory_order_relaxed);
    if (out) *out = h;
    return KILN_OK;
}

KilnResult KILN_CALL t_release(KilnHandle h) {
    std::lock_guard lk(g_mu);
    const size_t n = g_holders.size();
    std::erase_if(g_holders, [&](const Holder& x) { return x.handle == h; });
    g_render_demand.store(static_cast<uint32_t>(g_holders.size()), std::memory_order_relaxed);
    return g_holders.size() < n ? KILN_OK : KILN_ERR_NOT_FOUND;
}

KilnResult KILN_CALL t_camera(KilnRenderCamera* out) {
    if (!out || out->size < sizeof(KilnRenderCamera)) return KILN_ERR_INVALID_ARGUMENT;
    std::memset(out, 0, sizeof *out);
    out->size = sizeof *out;
    out->available = g_cam_on ? 1 : 0;
    if (!g_cam_on) return KILN_ERR_UNAVAILABLE;
    CamData d;
    uint64_t frames = 0, age = 0;
    if (!snapshot(&d, &frames, &age)) return KILN_OK;  // hooked, nothing written yet (menu)
    for (int i = 0; i < 3; ++i) out->position[i] = static_cast<double>(d.pos[i]) * kFixed;
    std::memcpy(out->rotation, d.rot, sizeof out->rotation);
    out->fov_y = krender::fov_radians(d.fov_y);
    out->aspect = d.aspect;
    out->near_plane = d.near_plane;
    out->far_plane = d.far_plane;
    out->distance = d.distance;
    out->frame = frames;
    out->age_ms = static_cast<uint32_t>(std::min<uint64_t>(age, 0xFFFFFFFFu));
    out->valid = age <= kCamFreshMs ? 1 : 0;
    return KILN_OK;
}

bool KILN_CALL t_world_to_screen(const double world[3], float w, float h, float out[3]) {
    if (!world || !out) return false;
    out[0] = out[1] = out[2] = 0;
    krender::Camera m;
    if (!math_camera(&m)) return false;
    if (w <= 0 || h <= 0) {
        KilnUiStatus s{};
        s.size = sizeof s;
        vk_status(&s);
        w = static_cast<float>(s.width);
        h = static_cast<float>(s.height);
        if (w <= 0 || h <= 0) return false;
    }
    return krender::project(m, world, w, h, out);
}

void KILN_CALL t_line(float x0, float y0, float x1, float y1, uint32_t rgba, float th) {
    if (!sane2(x0, y0) || !sane2(x1, y1) || !sane(th)) return;
    if (ImDrawList* d = dl()) d->AddLine(ImVec2(x0, y0), ImVec2(x1, y1), col(rgba), th > 0 ? th : 1.0f);
}

void KILN_CALL t_rect(float x0, float y0, float x1, float y1, uint32_t rgba, float rounding, float th, int32_t filled) {
    if (!sane2(x0, y0) || !sane2(x1, y1) || !sane(rounding) || !sane(th)) return;
    ImDrawList* d = dl();
    if (!d) return;
    if (filled) d->AddRectFilled(ImVec2(x0, y0), ImVec2(x1, y1), col(rgba), rounding);
    else d->AddRect(ImVec2(x0, y0), ImVec2(x1, y1), col(rgba), rounding, 0, th > 0 ? th : 1.0f);
}

void KILN_CALL t_circle(float x, float y, float r, uint32_t rgba, int32_t seg, float th, int32_t filled) {
    if (!sane2(x, y) || !sane(r) || r <= 0 || !sane(th)) return;
    ImDrawList* d = dl();
    if (!d) return;
    seg = std::clamp(seg, 0, 256);
    if (filled) d->AddCircleFilled(ImVec2(x, y), r, col(rgba), seg);
    else d->AddCircle(ImVec2(x, y), r, col(rgba), seg, th > 0 ? th : 1.0f);
}

void KILN_CALL t_triangle(float x0, float y0, float x1, float y1, float x2, float y2, uint32_t rgba, float th, int32_t filled) {
    if (!sane2(x0, y0) || !sane2(x1, y1) || !sane2(x2, y2) || !sane(th)) return;
    ImDrawList* d = dl();
    if (!d) return;
    if (filled) d->AddTriangleFilled(ImVec2(x0, y0), ImVec2(x1, y1), ImVec2(x2, y2), col(rgba));
    else d->AddTriangle(ImVec2(x0, y0), ImVec2(x1, y1), ImVec2(x2, y2), col(rgba), th > 0 ? th : 1.0f);
}

void KILN_CALL t_polyline(const float* xy, uint32_t count, uint32_t rgba, float th, int32_t closed, int32_t filled) {
    if (!in_frame() || !xy || count < 2 || count > 4096 || !sane(th)) return;
    thread_local std::vector<ImVec2> pts;
    pts.clear();
    for (uint32_t i = 0; i < count; ++i) {
        if (!sane2(xy[2 * i], xy[2 * i + 1])) return;
        pts.emplace_back(xy[2 * i], xy[2 * i + 1]);
    }
    ImDrawList* d = dl();
    if (!d) return;
    if (filled && count >= 3) d->AddConvexPolyFilled(pts.data(), static_cast<int>(count), col(rgba));
    else d->AddPolyline(pts.data(), static_cast<int>(count), col(rgba), th > 0 ? th : 1.0f, closed ? ImDrawFlags_Closed : 0);
}

void KILN_CALL t_text(float x, float y, uint32_t rgba, float size, uint32_t flags, KilnStr s) {
    if (!sane2(x, y) || !s.ptr || !s.len) return;
    ImDrawList* d = dl();
    if (!d) return;
    ImFont* font = ImGui::GetFont();
    const float fs = size > 0 ? std::min(size, 512.0f) : ImGui::GetFontSize();
    if (flags & (KILN_RENDER_TEXT_CENTER_X | KILN_RENDER_TEXT_CENTER_Y | KILN_RENDER_TEXT_RIGHT)) {
        const ImVec2 sz = font->CalcTextSizeA(fs, FLT_MAX, 0.0f, s.ptr, s.ptr + s.len);
        if (flags & KILN_RENDER_TEXT_RIGHT) x -= sz.x;
        else if (flags & KILN_RENDER_TEXT_CENTER_X) x -= sz.x * 0.5f;
        if (flags & KILN_RENDER_TEXT_CENTER_Y) y -= sz.y * 0.5f;
    }
    x = std::floor(x);
    y = std::floor(y);
    const ImU32 shade = IM_COL32(0, 0, 0, (rgba & 0xFF) * 3 / 4);
    if (flags & KILN_RENDER_TEXT_OUTLINE) {
        for (const ImVec2 o : {ImVec2(-1, 0), ImVec2(1, 0), ImVec2(0, -1), ImVec2(0, 1)})
            d->AddText(font, fs, ImVec2(x + o.x, y + o.y), shade, s.ptr, s.ptr + s.len);
    } else if (flags & KILN_RENDER_TEXT_SHADOW) {
        d->AddText(font, fs, ImVec2(x + 1, y + 1), shade, s.ptr, s.ptr + s.len);
    }
    d->AddText(font, fs, ImVec2(x, y), col(rgba), s.ptr, s.ptr + s.len);
}

void KILN_CALL t_text_size(KilnStr s, float size, float* w, float* h) {
    if (w) *w = 0;
    if (h) *h = 0;
    if (!in_frame() || !s.ptr) return;
    const float fs = size > 0 ? std::min(size, 512.0f) : ImGui::GetFontSize();
    const ImVec2 v = ImGui::GetFont()->CalcTextSizeA(fs, FLT_MAX, 0.0f, s.ptr, s.ptr + s.len);
    if (w) *w = v.x;
    if (h) *h = v.y;
}

void KILN_CALL t_image(KilnHandle img, float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1, uint32_t tint) {
    if (!sane2(x0, y0) || !sane2(x1, y1) || !sane2(u0, v0) || !sane2(u1, v1)) return;
    if (!in_frame()) return;
    ImTextureRef tex;
    if (!image_texture(img, &tex)) return;  // not uploaded yet (the frame after image_load) or freed
    if (ImDrawList* d = dl()) d->AddImage(tex, ImVec2(x0, y0), ImVec2(x1, y1), ImVec2(u0, v0), ImVec2(u1, v1), col(tint));
}

const KilnRenderApi kTable = {
    {sizeof(KilnRenderApi), KILN_RENDER_VERSION},
    &t_status, &t_acquire, &t_release, &t_camera, &t_world_to_screen,
    &t_line, &t_rect, &t_circle, &t_triangle, &t_polyline, &t_text, &t_text_size, &t_image,
};

bool sym(const char* n, uintptr_t* out) { return g_api->symbol(KilnStr{n, std::strlen(n)}, out) == KILN_OK && *out; }
bool cst(const char* n, int64_t* out) { return g_api->constant(KilnStr{n, std::strlen(n)}, out) == KILN_OK; }
bool feat(const char* n) { return g_api->feature(KilnStr{n, std::strlen(n)}); }

}  // namespace

namespace kui {

bool render_install() {
    uintptr_t site = 0;
    const bool ok = feat("render.camera") && sym("render/client.CameraWritten", &site) &&
                    cst("render/client.cam.obj", &L.cam_obj) && cst("render/client.cpi.camera", &L.cpi_camera) &&
                    cst("render/client.cpi.rotation", &L.rotation) && cst("render/client.cpi.distance", &L.distance) &&
                    cst("render/client.cpi.fov_y", &L.fov_y) && cst("render/client.cpi.aspect", &L.aspect) &&
                    cst("render/client.cpi.near", &L.near_plane) && cst("render/client.cpi.far", &L.far_plane);
    if (!ok) {
        logf(KILN_LOG_WARN, "render: the game's camera is not found on this build (feature render.camera off): screen drawing works, "
             "world positions do not");
        return false;
    }
    g_cam_hook.install(g_api, g_owner, site, &hk_camera, "render/client.CameraWritten");
    g_cam_on = static_cast<bool>(g_cam_hook);
    if (g_cam_on) logf(KILN_LOG_INFO, "render: camera hooked (read only)");
    else logf(KILN_LOG_ERROR, "render: hooking the camera failed: world positions do not work");
    return g_cam_on;
}

const void* render_table() { return &kTable; }

}  // namespace kui
