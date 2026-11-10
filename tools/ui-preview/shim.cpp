// SPDX-License-Identifier: MIT
// kiln.ui without the game, for the UI preview (tools/ui-preview/README.md): a Linux shared library that exports the same
// KilnUiApi table the managed Kiln.Ui calls, built from the same drawing slots as the game's (native/services/ui/
// draw_slots.h) and the same looks and font loading (style.cpp). Frames run on a headless Dear ImGui context; a small
// software rasteriser turns the draw lists into RGBA pixels (the caller writes the PNG). No GPU, no Wine, no game.
//
// The game's UI font: pass a TTF path to kiln_preview_open (preview.sh extracts the game's own at run time with
// tools/kfc, never into the tree); kiln.assets is faked so style.cpp's real loader picks it up. Without one, the
// game look uses the base font (a system TTF when found, else ImGui's own).
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <string>
#include <thread>
#include <vector>

#include "draw_slots.h"
#include "imgui.h"
#include "imgui_internal.h"
#include "kiln_api.h"
#include "services/kiln_assets.h"
#include "services/kiln_ui.h"
#include "ui.h"

namespace kui {
const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
static thread_local bool t_frame = false;
bool in_frame() { return t_frame; }
void logf(int, const char* fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    std::fprintf(stderr, "[shim] ");
    std::vfprintf(stderr, fmt, ap);
    std::fputc('\n', stderr);
    va_end(ap);
}
KilnHandle next_handle() {
    static std::atomic<KilnHandle> h{1};
    return h++;
}
}  // namespace kui

namespace {
using namespace kui_draw;

// ---- the fake kiln.assets: one keen::UiFontResource whose font 0 is the TTF given ----
std::vector<uint8_t> g_ttf;
std::vector<uint8_t> g_font_res;  // fonts {off 16, count 1}, groups {0, 0}, then the record (a KilnContentHash)

KilnResult KILN_CALL a_resource_list(uint32_t, KilnAssetKey* out, size_t cap, size_t* n) {
    if (n) *n = g_ttf.empty() ? 0 : 1;
    if (!g_ttf.empty() && out && cap) std::memset(out, 0, sizeof *out);
    return KILN_OK;
}
KilnResult KILN_CALL a_resource_read(const KilnAssetKey*, void* buf, size_t cap, size_t* len) {
    if (g_ttf.empty()) return KILN_ERR_NOT_FOUND;
    if (len) *len = g_font_res.size();
    if (buf && cap >= g_font_res.size()) std::memcpy(buf, g_font_res.data(), g_font_res.size());
    return KILN_OK;
}
KilnResult KILN_CALL a_blob_read(const KilnContentHash* h, void* buf, size_t cap, size_t* len) {
    if (g_ttf.empty() || !h || h->size != g_ttf.size()) return KILN_ERR_NOT_FOUND;
    if (len) *len = g_ttf.size();
    if (buf && cap >= g_ttf.size()) std::memcpy(buf, g_ttf.data(), g_ttf.size());
    return KILN_OK;
}
KilnAssetsApi g_assets{};
KilnApi g_kapi{};

const void* KILN_CALL k_service(KilnStr name, uint32_t) {
    if (std::string_view(name.ptr, name.len) == KILN_ASSETS_SERVICE) return &g_assets;
    return nullptr;
}

// ---- frame subscribers ----
struct Sub {
    KilnHandle h;
    KilnUiFrameFn fn;
    void* user;
};
std::vector<Sub> g_subs;
float g_w = 1920, g_h = 1080, g_scale = 1;
uint64_t g_frame = 0;
std::vector<uint8_t> g_pixels;  // RGBA, g_w x g_h

KilnResult KILN_CALL p_status(KilnUiStatus* out) {
    if (!out || out->size < sizeof(KilnUiStatus)) return KILN_ERR_INVALID_ARGUMENT;
    std::memset(out, 0, sizeof *out);
    out->size = sizeof *out;
    out->hooked = out->device = out->swapchain = out->window = 1;
    out->menu_open = out->capturing = 1;
    out->width = static_cast<uint32_t>(g_w);
    out->height = static_cast<uint32_t>(g_h);
    out->dpi_scale = g_scale;
    out->subscribers = static_cast<uint32_t>(g_subs.size());
    out->presents = out->frames_drawn = g_frame;
    out->frame_ms = 0.8;
    return KILN_OK;
}
KilnResult KILN_CALL p_subscribe_frame(KilnOwner, KilnUiFrameFn fn, void* user, KilnHandle* out) {
    if (!fn) return KILN_ERR_INVALID_ARGUMENT;
    const KilnHandle h = kui::next_handle();
    g_subs.push_back({h, fn, user});
    if (out) *out = h;
    return KILN_OK;
}
KilnResult KILN_CALL p_subscribe_key(KilnOwner, KilnUiKeyFn, void*, KilnHandle* out) {
    if (out) *out = kui::next_handle();
    return KILN_OK;
}
KilnResult KILN_CALL p_unsubscribe(KilnHandle h) {
    std::erase_if(g_subs, [&](const Sub& s) { return s.h == h; });
    return KILN_OK;
}
void KILN_CALL p_u32(uint32_t) {}
void KILN_CALL p_i32(int32_t) {}
void KILN_CALL p_u32u32(uint32_t, uint32_t) {}
void KILN_CALL p_key_name(uint32_t vk, char* buf, size_t cap) {
    if (!buf || !cap) return;
    if (vk == 0x2D) std::snprintf(buf, cap, "Insert");
    else if (vk == 0x20) std::snprintf(buf, cap, "Space");
    else if (vk >= 0x70 && vk <= 0x87) std::snprintf(buf, cap, "F%u", vk - 0x6F);
    else if ((vk >= 'A' && vk <= 'Z') || (vk >= '0' && vk <= '9')) std::snprintf(buf, cap, "%c", static_cast<char>(vk));
    else std::snprintf(buf, cap, "Key%u", vk);
}
uint32_t KILN_CALL p_take_pressed(uint32_t* mods) {
    if (mods) *mods = 0;
    return 0;
}
KilnResult KILN_CALL p_screen(KilnUiScreen*) { return KILN_ERR_UNAVAILABLE; }
KilnResult KILN_CALL p_sub_screen(KilnOwner, KilnUiScreenFn, void*, KilnHandle*) { return KILN_ERR_UNAVAILABLE; }
KilnResult KILN_CALL p_sub_tick(KilnOwner, KilnUiTickFn, void*, KilnHandle*) { return KILN_ERR_UNAVAILABLE; }
KilnResult KILN_CALL p_menu_add(KilnOwner, uint32_t, int32_t, KilnStr, KilnUiMenuFn, void*, KilnHandle*) { return KILN_ERR_UNAVAILABLE; }
KilnResult KILN_CALL p_menu_set(KilnHandle, KilnStr, uint32_t) { return KILN_ERR_UNAVAILABLE; }
KilnHandle KILN_CALL p_image_load(KilnOwner o, const void* png, size_t len) { return kui::image_load(o, png, len); }
bool KILN_CALL p_image_size(KilnHandle h, uint32_t* w, uint32_t* hh) { return kui::image_size(h, w, hh); }
void KILN_CALL p_image_free(KilnHandle h) { kui::image_free(h); }
KilnResult KILN_CALL p_pre(KilnOwner, KilnUiPreOverlayFn, KilnUiSwapchainGoneFn, void*, KilnHandle*) { return KILN_ERR_UNAVAILABLE; }
KilnResult KILN_CALL p_wheel_open(KilnOwner, const KilnUiWheel*, KilnUiWheelFn, void*, KilnHandle*) { return KILN_ERR_UNAVAILABLE; }
KilnResult KILN_CALL p_wheel_close(KilnHandle) { return KILN_ERR_NOT_FOUND; }
bool KILN_CALL p_wheel_active() { return false; }

KilnUiApi make_table() {
    KilnUiApi t{};
    t.header = {sizeof(KilnUiApi), KILN_UI_VERSION};
    t.status = &p_status;
    t.subscribe_frame = &p_subscribe_frame;
    t.subscribe_key = &p_subscribe_key;
    t.unsubscribe = &p_unsubscribe;
    t.set_demand = &p_u32;
    t.set_capture = &p_u32;
    t.set_menu = &p_i32;
    t.set_menu_key = &p_u32u32;
    t.key_name = &p_key_name;
    t.begin = &t_begin;
    t.end = &t_end;
    t.text = &t_text;
    t.text_colored = &t_text_colored;
    t.text_wrapped = &t_text_wrapped;
    t.text_disabled = &t_text_disabled;
    t.button = &t_button;
    t.small_button = &t_small_button;
    t.checkbox = &t_checkbox;
    t.slider_float = &t_slider_float;
    t.slider_int = &t_slider_int;
    t.input_text = &t_input_text;
    t.collapsing_header = &t_collapsing_header;
    t.tree_node = &t_tree_node;
    t.tree_pop = &t_tree_pop;
    t.selectable = &t_selectable;
    t.separator = &t_separator;
    t.separator_text = &t_separator_text;
    t.same_line = &t_same_line;
    t.spacing = &t_spacing;
    t.push_id = &t_push_id;
    t.pop_id = &t_pop_id;
    t.set_next_window_pos = &t_set_next_window_pos;
    t.set_next_window_size = &t_set_next_window_size;
    t.set_next_window_bg_alpha = &t_set_next_window_bg_alpha;
    t.progress_bar = &t_progress_bar;
    t.begin_table = &t_begin_table;
    t.table_setup_column = &t_table_setup_column;
    t.table_headers_row = &t_table_headers_row;
    t.table_next_row = &t_table_next_row;
    t.table_next_column = &t_table_next_column;
    t.end_table = &t_end_table;
    t.begin_tab_bar = &t_begin_tab_bar;
    t.begin_tab_item = &t_begin_tab_item;
    t.end_tab_item = &t_end_tab_item;
    t.end_tab_bar = &t_end_tab_bar;
    t.begin_child = &t_begin_child;
    t.end_child = &t_end_child;
    t.is_item_hovered = &t_is_item_hovered;
    t.set_tooltip = &t_set_tooltip;
    t.draw_text = &t_draw_text;
    t.draw_rect = &t_draw_rect;
    t.text_size = &t_text_size;
    t.take_pressed_key = &p_take_pressed;
    t.screen = &p_screen;
    t.subscribe_screen = &p_sub_screen;
    t.subscribe_ui_tick = &p_sub_tick;
    t.menu_add = &p_menu_add;
    t.menu_set = &p_menu_set;
    t.push_style = &t_push_style;
    t.pop_style = &t_pop_style;
    t.image_load = &p_image_load;
    t.image_size = &p_image_size;
    t.image = &t_image;
    t.image_button = &t_image_button;
    t.image_free = &p_image_free;
    t.set_next_window_focus = &t_set_next_window_focus;
    t.combo = &t_combo;
    t.input_multiline = &t_input_multiline;
    t.dummy = &t_dummy;
    t.window_rect = &t_window_rect;
    t.set_hover_capture = &p_u32;
    t.subscribe_pre_overlay = &p_pre;
    t.set_pre_overlay_demand = &p_u32;
    t.add_swapchain_usage = &p_u32;
    t.is_item_clicked = &t_is_item_clicked;
    t.wheel_open = &p_wheel_open;
    t.wheel_close = &p_wheel_close;
    t.wheel_active = &p_wheel_active;
    t.push_font_size = &t_push_font_size;
    t.pop_font_size = &t_pop_font_size;
    t.begin_child_ex = &t_begin_child_ex;
    t.set_next_item_width = &t_set_next_item_width;
    t.cursor = &t_cursor;
    t.set_cursor_pos = &t_set_cursor_pos;
    t.item = &t_item;
    t.selectable_ex = &t_selectable_ex;
    t.set_keyboard_focus_here = &t_set_keyboard_focus_here;
    t.set_item_default_focus = &t_set_item_default_focus;
    t.draw_rect_in = &t_draw_rect_in;
    t.draw_text_in = &t_draw_text_in;
    t.text_size_ex = &t_text_size_ex;
    t.push_color = &t_push_color;
    t.pop_color = &t_pop_color;
    t.push_var = &t_push_var;
    t.pop_var = &t_pop_var;
    t.key_pressed = &t_key_pressed;
    t.key_mods = &t_key_mods;
    t.set_scroll_here_y = &t_set_scroll_here_y;
    return t;
}
KilnUiApi g_table;

// ---- the software rasteriser ----
struct Tex {
    const uint8_t* px;
    int w, h, bpp;
};

Tex tex_of(ImTextureID id) {
    auto* t = reinterpret_cast<ImTextureData*>(static_cast<uintptr_t>(id));
    if (!t || !t->Pixels) return {nullptr, 0, 0, 0};
    return {static_cast<const uint8_t*>(t->Pixels), t->Width, t->Height, t->BytesPerPixel};
}

inline void sample(const Tex& t, float u, float v, float out[4]) {
    if (!t.px) {
        out[0] = out[1] = out[2] = out[3] = 1;
        return;
    }
    // bilinear, texel centres at +0.5
    const float x = u * t.w - 0.5f, y = v * t.h - 0.5f;
    const int x0 = static_cast<int>(std::floor(x)), y0 = static_cast<int>(std::floor(y));
    const float fx = x - x0, fy = y - y0;
    float acc[4] = {0, 0, 0, 0};
    for (int j = 0; j < 2; ++j)
        for (int i = 0; i < 2; ++i) {
            const int xx = std::clamp(x0 + i, 0, t.w - 1), yy = std::clamp(y0 + j, 0, t.h - 1);
            const float wgt = (i ? fx : 1 - fx) * (j ? fy : 1 - fy);
            const uint8_t* p = t.px + (static_cast<size_t>(yy) * t.w + xx) * t.bpp;
            if (t.bpp == 4)
                for (int k = 0; k < 4; ++k) acc[k] += wgt * p[k] / 255.f;
            else {
                acc[0] += wgt;
                acc[1] += wgt;
                acc[2] += wgt;
                acc[3] += wgt * p[0] / 255.f;
            }
        }
    for (int k = 0; k < 4; ++k) out[k] = acc[k];
}

void raster_tri(const ImDrawVert& a, const ImDrawVert& b, const ImDrawVert& c, const ImVec4& clip, const Tex& tex) {
    const int W = static_cast<int>(g_w), H = static_cast<int>(g_h);
    float area = (b.pos.x - a.pos.x) * (c.pos.y - a.pos.y) - (b.pos.y - a.pos.y) * (c.pos.x - a.pos.x);
    if (std::fabs(area) < 1e-8f) return;
    int x0 = static_cast<int>(std::floor(std::max({std::min({a.pos.x, b.pos.x, c.pos.x}), clip.x, 0.f})));
    int y0 = static_cast<int>(std::floor(std::max({std::min({a.pos.y, b.pos.y, c.pos.y}), clip.y, 0.f})));
    int x1 = static_cast<int>(std::ceil(std::min({std::max({a.pos.x, b.pos.x, c.pos.x}), clip.z, static_cast<float>(W)})));
    int y1 = static_cast<int>(std::ceil(std::min({std::max({a.pos.y, b.pos.y, c.pos.y}), clip.w, static_cast<float>(H)})));
    if (x0 >= x1 || y0 >= y1) return;
    const float inv = 1.0f / area;
    auto unpack = [](ImU32 col, float o[4]) {
        o[0] = ((col >> IM_COL32_R_SHIFT) & 0xFF) / 255.f;
        o[1] = ((col >> IM_COL32_G_SHIFT) & 0xFF) / 255.f;
        o[2] = ((col >> IM_COL32_B_SHIFT) & 0xFF) / 255.f;
        o[3] = ((col >> IM_COL32_A_SHIFT) & 0xFF) / 255.f;
    };
    float ca[4], cb[4], cc[4];
    unpack(a.col, ca);
    unpack(b.col, cb);
    unpack(c.col, cc);
    const bool flat = a.col == b.col && b.col == c.col;
    // a solid quad half (the white pixel UV everywhere): no texture lookups
    const bool same_uv = a.uv.x == b.uv.x && b.uv.x == c.uv.x && a.uv.y == b.uv.y && b.uv.y == c.uv.y;
    float solid[4] = {1, 1, 1, 1};
    if (same_uv) sample(tex, a.uv.x, a.uv.y, solid);
    for (int y = y0; y < y1; ++y) {
        const float py = y + 0.5f;
        for (int x = x0; x < x1; ++x) {
            const float px = x + 0.5f;
            const float w0 = ((b.pos.x - px) * (c.pos.y - py) - (b.pos.y - py) * (c.pos.x - px)) * inv;
            const float w1 = ((c.pos.x - px) * (a.pos.y - py) - (c.pos.y - py) * (a.pos.x - px)) * inv;
            const float w2 = 1.0f - w0 - w1;
            if (w0 < 0 || w1 < 0 || w2 < 0) continue;
            float col[4];
            if (flat) std::memcpy(col, ca, sizeof col);
            else
                for (int k = 0; k < 4; ++k) col[k] = w0 * ca[k] + w1 * cb[k] + w2 * cc[k];
            float t[4];
            if (same_uv) std::memcpy(t, solid, sizeof t);
            else sample(tex, w0 * a.uv.x + w1 * b.uv.x + w2 * c.uv.x, w0 * a.uv.y + w1 * b.uv.y + w2 * c.uv.y, t);
            const float sa = col[3] * t[3];
            if (sa <= 0.0005f) continue;
            uint8_t* d = &g_pixels[(static_cast<size_t>(y) * W + x) * 4];
            for (int k = 0; k < 3; ++k) {
                const float s = col[k] * t[k];
                d[k] = static_cast<uint8_t>(std::clamp((s * sa + (d[k] / 255.f) * (1 - sa)) * 255.f + 0.5f, 0.f, 255.f));
            }
            d[3] = 255;
        }
    }
}

// A stand-in for the game's picture behind the menu: dusk sky, a warm horizon, a few soft shapes.
void paint_backdrop() {
    const int W = static_cast<int>(g_w), H = static_cast<int>(g_h);
    for (int y = 0; y < H; ++y) {
        const float t = static_cast<float>(y) / H;
        for (int x = 0; x < W; ++x) {
            const float u = static_cast<float>(x) / W;
            float r = 38 + 90 * t + 30 * u, g = 52 + 60 * t, b = 84 + 10 * t - 30 * u;
            const float hx = u - 0.68f, hy = t - 0.62f;
            const float glow = std::exp(-(hx * hx * 6 + hy * hy * 40));
            r += 140 * glow;
            g += 90 * glow;
            b += 30 * glow;
            if (t > 0.72f + 0.05f * std::sin(u * 9.0f)) {
                r *= 0.35f;
                g *= 0.4f;
                b *= 0.45f;
            }
            uint8_t* d = &g_pixels[(static_cast<size_t>(y) * W + x) * 4];
            d[0] = static_cast<uint8_t>(std::clamp(r, 0.f, 255.f));
            d[1] = static_cast<uint8_t>(std::clamp(g, 0.f, 255.f));
            d[2] = static_cast<uint8_t>(std::clamp(b, 0.f, 255.f));
            d[3] = 255;
        }
    }
}

void update_textures(ImDrawData* dd) {
    if (!dd->Textures) return;
    for (ImTextureData* t : *dd->Textures) {
        if (t->Status == ImTextureStatus_WantCreate || t->Status == ImTextureStatus_WantUpdates) {
            t->SetTexID(static_cast<ImTextureID>(reinterpret_cast<uintptr_t>(t)));
            t->SetStatus(ImTextureStatus_OK);
        } else if (t->Status == ImTextureStatus_WantDestroy && t->UnusedFrames > 0) {
            t->SetTexID(ImTextureID_Invalid);
            t->SetStatus(ImTextureStatus_Destroyed);
        }
    }
}

void rasterise(ImDrawData* dd) {
    paint_backdrop();
    for (ImDrawList* dl : dd->CmdLists) {
        const ImDrawVert* vb = dl->VtxBuffer.Data;
        const ImDrawIdx* ib = dl->IdxBuffer.Data;
        for (const ImDrawCmd& cmd : dl->CmdBuffer) {
            if (cmd.UserCallback) continue;
            const Tex tex = tex_of(cmd.GetTexID());
            for (unsigned i = 0; i + 2 < cmd.ElemCount; i += 3) {
                const ImDrawIdx* idx = ib + cmd.IdxOffset + i;
                raster_tri(vb[cmd.VtxOffset + idx[0]], vb[cmd.VtxOffset + idx[1]], vb[cmd.VtxOffset + idx[2]], cmd.ClipRect, tex);
            }
        }
    }
}

bool read_file(const char* path, std::vector<uint8_t>& out) {
    std::ifstream f(path, std::ios::binary);
    if (!f) return false;
    out.assign(std::istreambuf_iterator<char>(f), {});
    return !out.empty();
}

}  // namespace

extern "C" {

// Creates the context. font: the game's UI TTF (or null), base_font: the non-game font (null = a system TTF or ImGui's).
__attribute__((visibility("default"))) const KilnUiApi* kiln_preview_open(int width, int height, float scale, const char* font,
                                                                          const char* base_font) {
    g_w = static_cast<float>(width);
    g_h = static_cast<float>(height);
    g_scale = scale > 0 ? scale : 1;
    g_pixels.assign(static_cast<size_t>(width) * height * 4, 0);
    g_assets.header = {sizeof(KilnAssetsApi), KILN_ASSETS_VERSION};
    g_assets.resource_list = &a_resource_list;
    g_assets.resource_read = &a_resource_read;
    g_assets.blob_read = &a_blob_read;
    g_kapi.size = sizeof g_kapi;
    g_kapi.abi_version = KILN_ABI_VERSION;
    g_kapi.service = &k_service;
    kui::g_api = &g_kapi;
    if (font && *font && read_file(font, g_ttf)) {
        g_font_res.assign(48, 0);
        const int32_t off = 16;
        const uint32_t count = 1;
        std::memcpy(&g_font_res[0], &off, 4);
        std::memcpy(&g_font_res[4], &count, 4);
        KilnContentHash h{static_cast<uint32_t>(g_ttf.size()), 1, 2, 3};
        std::memcpy(&g_font_res[16], &h, sizeof h);
    }
    IMGUI_CHECKVERSION();
    ImGui::CreateContext();
    ImGuiIO& io = ImGui::GetIO();
    io.IniFilename = nullptr;
    io.ConfigFlags |= ImGuiConfigFlags_NavEnableKeyboard | ImGuiConfigFlags_NavEnableGamepad;
    io.BackendFlags |= ImGuiBackendFlags_RendererHasTextures;
    io.Fonts->TexDesiredFormat = ImTextureFormat_RGBA32;
    io.DisplaySize = ImVec2(g_w, g_h);
    bool base = false;
    for (const char* p : {base_font, "/usr/share/fonts/TTF/DejaVuSans.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
                          "/usr/share/fonts/noto/NotoSans-Regular.ttf", "/usr/share/fonts/TTF/Arial.TTF"}) {
        if (p && *p && std::ifstream(p) && io.Fonts->AddFontFromFileTTF(p, 16.0f)) {
            base = true;
            break;
        }
    }
    if (!base) io.Fonts->AddFontDefault();
    kui::style_apply_base(g_scale);
    g_table = make_table();
    return &g_table;
}

// Feeds input before the next frame: a key (ImGuiKey value) down/up, or the mouse (x, y, left button).
__attribute__((visibility("default"))) void kiln_preview_key(int key, int down) {
    ImGui::GetIO().AddKeyEvent(static_cast<ImGuiKey>(key), down != 0);
}
__attribute__((visibility("default"))) void kiln_preview_mouse(float x, float y, int left) {
    ImGuiIO& io = ImGui::GetIO();
    io.AddMousePosEvent(x, y);
    io.AddMouseButtonEvent(0, left != 0);
}

// Runs one overlay frame (the frame callbacks, as ui.cpp's build_frame does) and rasterises it into the pixel buffer.
__attribute__((visibility("default"))) const uint8_t* kiln_preview_frame(int menu_open) {
    ImGuiIO& io = ImGui::GetIO();
    io.DisplaySize = ImVec2(g_w, g_h);
    io.DeltaTime = 1.0f / 60.0f;
    kui::style_frame_begin();
    ImGui::NewFrame();
    KilnUiFrame f{};
    f.size = sizeof f;
    f.width = g_w;
    f.height = g_h;
    f.dpi_scale = g_scale;
    f.delta_seconds = io.DeltaTime;
    f.menu_open = menu_open ? 1 : 0;
    f.capturing = 1;
    f.frame = ++g_frame;
    kui::t_frame = true;
    const auto subs = g_subs;
    for (const auto& s : subs) s.fn(&f, s.user);
    v3_frame_end();
    kui::style_frame_end();
    kui::t_frame = false;
    ImGui::Render();
    ImDrawData* dd = ImGui::GetDrawData();
    update_textures(dd);
    rasterise(dd);
    return g_pixels.data();
}

__attribute__((visibility("default"))) void kiln_preview_close() {
    if (ImGui::GetCurrentContext()) ImGui::DestroyContext();
    g_subs.clear();
}

}  // extern "C"
