// SPDX-License-Identifier: MIT
// kiln.ui without the game, for the UI preview (tools/ui-preview/README.md): a Linux shared library that exports the same
// KilnUiApi table the managed Kiln.Ui calls, built from the same drawing slots as the game's (native/services/ui/
// draw_slots.h) and the same looks and font loading (style.cpp). Frames run on a headless Dear ImGui context; a small
// software rasteriser turns the draw lists into RGBA pixels (the caller writes the PNG). No GPU, no Wine, no game.
//
// The game's UI font: pass a TTF path to kiln_preview_open (preview.sh extracts the game's own at run time with
// tools/kfc, never into the tree); kiln.assets is faked so style.cpp's real loader picks it up. Without one, the
// game look uses the base font (a system TTF when found, else ImGui's own).
// The game's UI colours: kiln_preview_game_colors(bundle, layout) before kiln_preview_open hands style.cpp's real reader
// the game's keen::FbUiBundle bytes and the reflection layout of the fields it reads (game_colors.py writes both from the
// local game at run time, outside the tree); kiln.types is faked from that layout. Without them the game look is its
// neutral stand-in, as on a build where the read fails.
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <memory>
#include <string>
#include <thread>
#include <vector>

#include "draw_slots.h"
#include "imgui.h"
#include "imgui_internal.h"
#include "kiln_api.h"
#include "look_palette.h"
#include "services/kiln_assets.h"
#include "services/kiln_types.h"
#include "services/kiln_ui.h"
#include "ui.h"
#include "ui_text.h"

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
uint64_t now_ms() {
    return static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count());
}
// the game's text language (keen::LanguageId) for style.cpp's glyph choice: kiln_preview_game_language
std::atomic<uint32_t> g_preview_language{0};
KilnResult game_screen(KilnUiScreen* out) {
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    std::memset(out, 0, sizeof *out);
    out->size = sizeof *out;
    out->language = g_preview_language.load();
    return KILN_OK;
}
}  // namespace kui

namespace {
using namespace kui_draw;

// ---- the fake kiln.assets: one keen::UiFontResource whose font 0 is the TTF given (and font 1 its bold face, when given)
std::vector<uint8_t> g_ttf, g_bold_ttf;
std::string g_bold_path;
std::vector<uint8_t> g_font_res;  // fonts, then (with a bold face) one default language group with a regular and a bold set
// kiln_preview_font_resource: the game's own keen::UiFontResource and a folder of its font files named by content hash
// ("<size>-<h0>-<h1>-<h2>.bin", all four in hex: the preview's own names, not kiln.assets' text, whose size is decimal;
// game_fonts.py writes them from the local game, never into the tree): style.cpp reads the default group's fonts (Latin,
// bold, Cyrillic, Thai) and a Chinese, Japanese or Korean group's from them
std::vector<uint8_t> g_real_res;
std::string g_blob_dir;

// ---- the game's UI bundle and the layout of its colour fields (kiln_preview_game_colors) ----
std::vector<uint8_t> g_bundle;
struct FakeType;
struct FakeField {
    std::string name;
    const FakeType* type;
    uint64_t offset;
};
struct FakeType {
    std::string name;
    uint32_t size = 0, hash = 0;
    uint8_t kind = 0;
    std::vector<FakeField> fields;
};
std::vector<std::unique_ptr<FakeType>> g_fake_types;
FakeType* fake_type(const std::string& name) {
    for (auto& t : g_fake_types)
        if (t->name == name) return t.get();
    g_fake_types.push_back(std::make_unique<FakeType>());
    g_fake_types.back()->name = name;
    return g_fake_types.back().get();
}
const KilnType* KILN_CALL ty_by_name(KilnStr n) {
    const std::string s(n.ptr, n.len);
    for (auto& t : g_fake_types)
        if (t->name == s && t->size) return reinterpret_cast<const KilnType*>(t.get());
    return nullptr;
}
KilnResult KILN_CALL ty_info(const KilnType* type, KilnTypeInfo* out) {
    const auto* t = reinterpret_cast<const FakeType*>(type);
    if (!t || !out) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    out->name = out->qualified_name = KilnStr{t->name.data(), t->name.size()};
    out->type_size = t->size;
    out->kind = t->kind;
    out->hash = t->hash;
    out->field_count = static_cast<uint32_t>(t->fields.size());
    return KILN_OK;
}
KilnResult KILN_CALL ty_field(const KilnType* type, uint32_t i, KilnTypeField* out) {
    const auto* t = reinterpret_cast<const FakeType*>(type);
    if (!t || !out || i >= t->fields.size()) return KILN_ERR_NOT_FOUND;
    out->name = KilnStr{t->fields[i].name.data(), t->fields[i].name.size()};
    out->type = reinterpret_cast<const KilnType*>(t->fields[i].type);
    out->offset = t->fields[i].offset;
    return KILN_OK;
}
KilnTypesApi g_types{};

constexpr uint32_t kFontRes = kui::look::fnv1a("keen::UiFontResource");
constexpr uint32_t kBundleRes = kui::look::fnv1a("keen::FbUiBundle");
KilnResult KILN_CALL a_resource_list(uint32_t type, KilnAssetKey* out, size_t cap, size_t* n) {
    const bool have = (type == kFontRes && (!g_ttf.empty() || !g_real_res.empty())) || (type == kBundleRes && !g_bundle.empty());
    if (n) *n = have ? 1 : 0;
    if (have && out && cap) {
        std::memset(out, 0, sizeof *out);
        out->type = type;
    }
    return KILN_OK;
}
KilnResult KILN_CALL a_resource_read(const KilnAssetKey* key, void* buf, size_t cap, size_t* len) {
    const std::vector<uint8_t>* res = !key ? nullptr : key->type == kFontRes && !g_real_res.empty() ? &g_real_res
                                     : key->type == kFontRes && !g_ttf.empty() ? &g_font_res
                                     : key->type == kBundleRes && !g_bundle.empty() ? &g_bundle : nullptr;
    if (!res) return KILN_ERR_NOT_FOUND;
    if (len) *len = res->size();
    if (buf && cap >= res->size()) std::memcpy(buf, res->data(), res->size());
    return KILN_OK;
}
KilnResult KILN_CALL a_blob_read(const KilnContentHash* h, void* buf, size_t cap, size_t* len) {
    if (h && !g_blob_dir.empty()) {
        char name[96];
        std::snprintf(name, sizeof name, "/%x-%x-%x-%x.bin", h->size, h->hash0, h->hash1, h->hash2);
        std::ifstream f(g_blob_dir + name, std::ios::binary);
        if (!f) return KILN_ERR_NOT_FOUND;
        std::vector<uint8_t> b((std::istreambuf_iterator<char>(f)), {});
        if (b.size() != h->size) return KILN_ERR_NOT_FOUND;
        if (len) *len = b.size();
        if (buf && cap >= b.size()) std::memcpy(buf, b.data(), b.size());
        return KILN_OK;
    }
    if (g_ttf.empty() || !h) return KILN_ERR_NOT_FOUND;
    const std::vector<uint8_t>& ttf = h->hash0 == 2 ? g_bold_ttf : g_ttf;  // hash0: 1 regular, 2 bold
    if (ttf.empty() || h->size != ttf.size()) return KILN_ERR_NOT_FOUND;
    if (len) *len = ttf.size();
    if (buf && cap >= ttf.size()) std::memcpy(buf, ttf.data(), ttf.size());
    return KILN_OK;
}
KilnAssetsApi g_assets{};
KilnApi g_kapi{};

const void* KILN_CALL k_service(KilnStr name, uint32_t) {
    if (std::string_view(name.ptr, name.len) == KILN_ASSETS_SERVICE) return &g_assets;
    if (std::string_view(name.ptr, name.len) == KILN_TYPES_SERVICE && !g_fake_types.empty()) return &g_types;
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

// ---- the main-menu page (v4): kiln_preview_page() sets it; the canvas is what the game's UI system computes for the
// window size (1920x1080 UI units at 1 pixel per unit, grown along the longer side; 0xa4fb40) ----
bool g_page = false;
float g_applied_scale = 1;  // the dpi the looks were built for (the shim's scale times the player's, set_look)
int g_backdrop = 1;  // the game page's stand-in: 1 the dark close-up, 2 a bright, busy, high-contrast picture
uint64_t g_ui_frame = 0;
int g_page_drawn = 0, g_page_leaves = 0;
KilnResult KILN_CALL p_menu_page(KilnUiMenuPage* out) {
    if (!out || out->size < sizeof(KilnUiMenuPage)) return KILN_ERR_INVALID_ARGUMENT;
    std::memset(out, 0, sizeof *out);
    out->size = sizeof *out;
    out->available = 1;
    out->ui_frame = g_ui_frame;
    out->pad_left = out->pad_right = 80;
    out->pad_top = out->pad_bottom = 50;
    if (!g_page) return KILN_OK;
    out->state = KILN_UI_PAGE_SHOWN;
    out->opened_by = KILN_UI_PAGE_BY_ENTRY;
    out->shown_frame = g_ui_frame;
    const float ppu = std::min(g_w / 1920.0f, g_h / 1080.0f);
    out->metrics = 1;
    out->pixels_per_unit = ppu;
    out->canvas_width = g_w / ppu;
    out->canvas_height = g_h / ppu;
    out->viewport_width = g_w;
    out->viewport_height = g_h;
    out->x0 = 80 * ppu;
    out->y0 = 50 * ppu;
    out->x1 = (out->canvas_width - 80) * ppu;
    out->y1 = (out->canvas_height - 50) * ppu;
    return KILN_OK;
}
void KILN_CALL p_menu_page_drawn() { ++g_page_drawn; }
void KILN_CALL p_menu_page_leave() { ++g_page_leaves; }
void KILN_CALL p_menu_page_host(uint32_t) {}

// set_texts (0.2.0-dev.29.16): Kiln's own native texts and the language, as ui.cpp keeps them (no game menus here)
KilnResult KILN_CALL t_set_texts(const KilnUiTexts* t) {
    if (!t || t->size < sizeof(KilnUiTexts) || (t->count && (!t->keys || !t->texts))) return KILN_ERR_INVALID_ARGUMENT;
    std::vector<std::pair<std::string, std::string>> pairs;
    for (uint32_t i = 0; i < t->count; ++i)
        pairs.emplace_back(std::string(t->keys[i].ptr, t->keys[i].len), std::string(t->texts[i].ptr, t->texts[i].len));
    kui::text::table().set(std::string_view(t->language.ptr ? t->language.ptr : "", t->language.ptr ? t->language.len : 0), pairs);
    return KILN_OK;
}

// Kiln's graphics tools (0.2.0-dev.29.18): the settings kept, and a sample of what the game would say (a 2560x1440 game
// at DLSS Quality, the cap holding it) so the Graphics page shows a typical status.
KilnUiGraphics g_graphics{sizeof(KilnUiGraphics)};
KilnResult KILN_CALL t_graphics_set(const KilnUiGraphics* g) {
    if (!g || g->size < sizeof(KilnUiGraphics)) return KILN_ERR_INVALID_ARGUMENT;
    g_graphics = *g;
    return KILN_OK;
}
KilnResult KILN_CALL t_graphics_status(KilnUiGraphicsStatus* out) {
    if (!out || out->size < KILN_UI_GRAPHICS_STATUS_SIZE_V1) return KILN_ERR_INVALID_ARGUMENT;
    KilnUiGraphicsStatus st{};
    st.size = sizeof st;
    st.fps_cap = g_graphics.fps_cap;
    st.flags = KILN_UI_GFX_DLSS_HOOKED | KILN_UI_GFX_DLSS_ACTIVE | KILN_UI_GFX_COUNTING | (st.fps_cap ? KILN_UI_GFX_CAP_ACTIVE : 0u);
    st.rendered_fps = st.output_fps = st.fps_cap ? static_cast<float>(st.fps_cap) - 0.2f : 87.4f;
    st.frame_ms = 1000.0f / st.rendered_fps;
    st.output_width = 2560;
    st.output_height = 1440;
    st.dlss_quality = 2;
    st.dlss_preset = g_graphics.dlss_preset;
    if (st.dlss_preset) st.flags |= KILN_UI_GFX_DLSS_PRESET;
    const float k = g_graphics.dlss_scale == KILN_UI_DLSS_SCALE_CUSTOM ? g_graphics.custom_scale
                  : g_graphics.dlss_scale == KILN_UI_DLSS_SCALE_PER_MODE ? g_graphics.mode_scale[0] : 0.667f;
    st.render_width = static_cast<uint32_t>(2560 * k + 0.5f);
    st.render_height = static_cast<uint32_t>(1440 * k + 0.5f);
    st.dlss_output_width = 2560;
    st.dlss_output_height = 1440;
    // (phase B) the upscaler behind the game's FSR 3: AMD FSR 4 runs; Intel XeSS's files are not downloaded
    st.upscaler = g_graphics.upscaler;
    st.amd_upscaler = g_graphics.amd_upscaler;
    if (g_graphics.upscaler != KILN_UI_UPSCALER_GAME) {  // the game upscales with FSR 3 then, not DLSS
        st.flags &= ~(KILN_UI_GFX_DLSS_ACTIVE | KILN_UI_GFX_DLSS_PRESET);
        st.dlss_quality = -1;
    }
    if (g_graphics.upscaler == KILN_UI_UPSCALER_AMD) {
        const bool pinned = g_graphics.amd_upscaler == KILN_UI_AMD_UPSCALER_FSR3;
        st.upscale_running = pinned ? KILN_UI_UPSCALE_RUN_AMD_FSR3 : KILN_UI_UPSCALE_RUN_AMD_FSR4;
        st.upscale_flags = KILN_UI_UPSCALE_HOOKED | KILN_UI_UPSCALE_FSR_SEEN | KILN_UI_UPSCALE_FILES_OK | KILN_UI_UPSCALE_DEVICE_OK |
                           (pinned ? KILN_UI_UPSCALE_AMD_PINNED : 0u);
        st.upscale_version = pinned ? (3u << 22) | (1u << 12) | 6u : (4u << 22) | (1u << 12) | 2u;
    } else if (g_graphics.upscaler == KILN_UI_UPSCALER_XESS) {
        st.upscale_running = KILN_UI_UPSCALE_RUN_GAME_FSR3;
        st.upscale_reason = KILN_UI_UPSCALE_WHY_FILES_MISSING;
        st.upscale_flags = KILN_UI_UPSCALE_HOOKED | KILN_UI_UPSCALE_FSR_SEEN;
        std::snprintf(st.upscale_detail, sizeof st.upscale_detail, "kiln/vendor/intel/3.0.2/libxess.dll is not there");
    }
    if (st.upscale_flags & KILN_UI_UPSCALE_FSR_SEEN) {
        st.upscale_render_width = 1707;
        st.upscale_render_height = 960;
        st.upscale_output_width = 2560;
        st.upscale_output_height = 1440;
    }
    const uint32_t size = out->size;
    std::memcpy(out, &st, std::min<size_t>(size, sizeof st));
    out->size = size;
    return KILN_OK;
}

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
    t.menu_page = &p_menu_page;
    t.menu_page_drawn = &p_menu_page_drawn;
    t.menu_page_leave = &p_menu_page_leave;
    t.menu_page_host = &p_menu_page_host;
    t.push_font = &t_push_font;
    t.pop_font = &t_pop_font;
    // the looks tail (0.2.0-dev.29.16): style.cpp's own functions; set_look without ui.cpp's caller check
    t.theme_create = &kui::theme_create;
    t.theme_free = &kui::theme_free;
    t.push_theme = &t_push_theme;
    t.set_look = &kui::look_set;
    t.look_info = &kui::look_info;
    t.font_load = &kui::font_load;
    t.font_free = &kui::font_free;
    t.push_font_handle = &t_push_font_handle;
    t.set_texts = &t_set_texts;
    t.graphics_set = &t_graphics_set;
    t.graphics_status = &t_graphics_status;
    t.pointer = &t_pointer;  // the pointer and drag and drop (0.2.0-dev.30.3)
    t.drag_source = &t_drag_source;
    t.drop_target = &t_drop_target;
    t.window_state = &t_window_state;  // window docking (issue 149)
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

// A stand-in for the game's close-up shot behind its own pages (the camera near the character: a dark, warm, blurred
// scene) with the vignette the game draws over every PreGame page (black at the edges).
void paint_page_backdrop() {
    const int W = static_cast<int>(g_w), H = static_cast<int>(g_h);
    for (int y = 0; y < H; ++y) {
        const float t = static_cast<float>(y) / H;
        for (int x = 0; x < W; ++x) {
            const float u = static_cast<float>(x) / W;
            float r = 52 + 40 * t, g = 46 + 22 * t, b = 50 + 6 * t;
            const float fx = u - 0.62f, fy = t - 0.45f;  // a figure lit from the side
            const float body = std::exp(-(fx * fx * 18 + fy * fy * 5));
            r += 120 * body;
            g += 80 * body;
            b += 50 * body;
            const float lx = u - 0.2f, ly = t - 0.3f;  // a lamp far behind
            const float lamp = std::exp(-(lx * lx * 40 + ly * ly * 60));
            r += 90 * lamp;
            g += 60 * lamp;
            b += 20 * lamp;
            const float ex = std::max(std::fabs(u - 0.5f) * 2, std::fabs(t - 0.5f) * 2);  // vignette
            const float v = 1 - 0.45f * std::clamp((ex - 0.45f) / 0.55f, 0.f, 1.f);
            uint8_t* d = &g_pixels[(static_cast<size_t>(y) * W + x) * 4];
            d[0] = static_cast<uint8_t>(std::clamp(r * v, 0.f, 255.f));
            d[1] = static_cast<uint8_t>(std::clamp(g * v, 0.f, 255.f));
            d[2] = static_cast<uint8_t>(std::clamp(b * v, 0.f, 255.f));
            d[3] = 255;
        }
    }
}

// The worst case for readability: a bright sky, white and near-black stripes and blocks, sharp edges (snow, sunlit
// stone, dark trees), with the game's vignette on top.
void paint_bright_backdrop() {
    const int W = static_cast<int>(g_w), H = static_cast<int>(g_h);
    for (int y = 0; y < H; ++y) {
        const float t = static_cast<float>(y) / H;
        for (int x = 0; x < W; ++x) {
            const float u = static_cast<float>(x) / W * (g_w / g_h) * (9.0f / 16.0f);
            float r = 236 - 30 * t, g = 238 - 20 * t, b = 246 - 10 * t;  // white-blue sky
            if (std::sin(u * 40.0f + t * 9.0f) > 0.55f) r = g = b = 252;   // glare streaks
            const int cx = static_cast<int>(u * 14), cy = static_cast<int>(t * 9);
            if (((cx * 7 + cy * 3) % 5) == 0) { r = 30; g = 34; b = 28; }  // dark blocks (trees)
            if (t > 0.78f + 0.04f * std::sin(u * 21.0f)) { r = 250; g = 247; b = 240; }  // snow
            if (((cx + cy) % 4) == 1) { r = std::min(255.f, r + 10); g = std::min(255.f, g + 5); b = 120; }  // yellow light
            const float ex = std::max(std::fabs(static_cast<float>(x) / W - 0.5f) * 2, std::fabs(t - 0.5f) * 2);
            const float v = 1 - 0.45f * std::clamp((ex - 0.45f) / 0.55f, 0.f, 1.f);
            uint8_t* d = &g_pixels[(static_cast<size_t>(y) * W + x) * 4];
            d[0] = static_cast<uint8_t>(std::clamp(r * v, 0.f, 255.f));
            d[1] = static_cast<uint8_t>(std::clamp(g * v, 0.f, 255.f));
            d[2] = static_cast<uint8_t>(std::clamp(b * v, 0.f, 255.f));
            d[3] = 255;
        }
    }
}

// A stand-in for the game's picture behind the menu: dusk sky, a warm horizon, a few soft shapes.
void paint_backdrop() {
    if (g_page) return g_backdrop == 2 ? paint_bright_backdrop() : paint_page_backdrop();
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
        const bool bold = !g_bold_path.empty() && read_file(g_bold_path.c_str(), g_bold_ttf);
        // fonts {off 16, count}; with a bold face, groups {off, 1}: one default group whose glyph sets are font 0 regular
        // (variant mask 0) and font 1 bold (mask 1), as the game's resource has them
        g_font_res.assign(bold ? 144 : 48, 0);
        auto put = [](size_t at, uint32_t v) { std::memcpy(&g_font_res[at], &v, 4); };
        put(0, 16);
        put(4, bold ? 2 : 1);
        KilnContentHash h{static_cast<uint32_t>(g_ttf.size()), 1, 2, 3};
        std::memcpy(&g_font_res[16], &h, sizeof h);
        if (bold) {
            KilnContentHash hb{static_cast<uint32_t>(g_bold_ttf.size()), 2, 2, 3};
            std::memcpy(&g_font_res[48], &hb, sizeof hb);
            put(8, 80 - 8);          // groups: the group record at 80
            put(12, 1);
            put(80 + 8, 104 - 88);   // its glyph sets at 104
            put(80 + 12, 2);
            g_font_res[80 + 0x14] = 1;  // the default language group
            put(104, 0);             // set 0: font 0, mask 0
            put(124, 1);             // set 1: font 1, mask 1 (bold)
            g_font_res[124 + 0x10] = 1;
        }
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
    g_applied_scale = g_scale;
    g_table = make_table();
    return &g_table;
}

// The game's UI bundle (keen::FbUiBundle bytes) and the layout of the fields style.cpp reads, both from the local game
// (game_colors.py). Layout lines: "type <name> <size> <hash> <kind>" and "field <owner> <name> <offset> <type>". Call
// before kiln_preview_open. 0: read; else a file is missing or empty.
__attribute__((visibility("default"))) int kiln_preview_game_colors(const char* bundle, const char* layout) {
    if (!bundle || !layout || !read_file(bundle, g_bundle)) return 1;
    std::ifstream f(layout);
    std::string line;
    while (std::getline(f, line)) {
        char a[256], b[256], c[256];
        unsigned long long n1 = 0, n2 = 0;
        unsigned kind = 0;
        if (std::sscanf(line.c_str(), "type %255s %llu %llx %u", a, &n1, &n2, &kind) == 4) {
            FakeType* t = fake_type(a);
            t->size = static_cast<uint32_t>(n1);
            t->hash = static_cast<uint32_t>(n2);
            t->kind = static_cast<uint8_t>(kind);
        } else if (std::sscanf(line.c_str(), "field %255s %255s %llx %255s", a, b, &n1, c) == 4) {
            fake_type(a)->fields.push_back({b, fake_type(c), n1});
        }
    }
    if (g_fake_types.empty()) {
        g_bundle.clear();
        return 1;
    }
    g_types.header = {sizeof(KilnTypesApi), KILN_TYPES_VERSION};
    g_types.type_by_name = &ty_by_name;
    g_types.type_info = &ty_info;
    g_types.type_field = &ty_field;
    return 0;
}

// The game's own keen::UiFontResource and the folder of its font files (game_fonts.py), in place of the one-font resource made
// from --font. Call before kiln_preview_open. 0: read.
__attribute__((visibility("default"))) int kiln_preview_font_resource(const char* resource, const char* blob_dir) {
    if (!resource || !blob_dir || !read_file(resource, g_real_res)) return 1;
    g_blob_dir = blob_dir;
    return 0;
}

// The game's text language (keen::LanguageId: 20 zh-CN, 11 ja-JP, 13 ko-KR, 21 zh-TW, 0 unknown).
__attribute__((visibility("default"))) void kiln_preview_game_language(uint32_t id) { kui::g_preview_language = id; }

// The font atlas now: its texture's size, the glyphs made so far (every size of every font), the fonts' sources.
__attribute__((visibility("default"))) void kiln_preview_atlas(int* width, int* height, int* glyphs, int* sources) {
    ImFontAtlas* a = ImGui::GetIO().Fonts;
    *width = a->TexData ? a->TexData->Width : 0;
    *height = a->TexData ? a->TexData->Height : 0;
    int n = 0;
    if (a->Builder)
        for (int i = 0; i < a->Builder->BakedPool.Size; ++i)
            if (!a->Builder->BakedPool[i].WantDestroy) n += a->Builder->BakedPool[i].Glyphs.Size;
    *glyphs = n;
    *sources = a->Sources.Size;
}

// The game's bold UI face (call before kiln_preview_open; the game's resource has it as a second glyph set).
__attribute__((visibility("default"))) void kiln_preview_bold_font(const char* path) { g_bold_path = path ? path : ""; }

// The Kiln page inside the game's main menu: 0 not shown, 1 shown over the dark close-up stand-in, 2 shown over a
// bright, high-contrast stand-in.
__attribute__((visibility("default"))) void kiln_preview_page(int mode) {
    g_page = mode != 0;
    g_backdrop = mode == 2 ? 2 : 1;
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
    ++g_ui_frame;
    const float dpi = g_scale * kui::user_scale();  // as ui.cpp's build_frame: the player's scale on top
    if (dpi != g_applied_scale) {
        kui::style_apply_base(dpi);
        g_applied_scale = dpi;
    }
    kui::style_frame_begin();
    ImGui::NewFrame();
    KilnUiFrame f{};
    f.size = sizeof f;
    f.width = g_w;
    f.height = g_h;
    f.dpi_scale = dpi;
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

// Typed text for the next frame (UTF-8), as the game's window messages would feed it.
__attribute__((visibility("default"))) void kiln_preview_chars(const char* utf8) { ImGui::GetIO().AddInputCharactersUTF8(utf8); }

// The mouse cursor shape the last frame asked for (ImGuiMouseCursor: 1 = a text field is under the mouse).
__attribute__((visibility("default"))) int kiln_preview_mouse_cursor() { return ImGui::GetMouseCursor(); }

// The active text field as ImGui holds it: the size it was told the caller's buffer has (BufCapacity, given every frame),
// the size of its own copy of the text (TextA, made when the field became active) and the text's length. 0: none active.
__attribute__((visibility("default"))) int kiln_preview_text_state(int* capacity, int* size, int* length) {
    ImGuiContext& g = *GImGui;
    const ImGuiInputTextState& st = g.InputTextState;
    if (st.ID == 0 || g.ActiveId != st.ID) return 0;
    if (capacity) *capacity = st.BufCapacity;
    if (size) *size = st.TextA.Size;
    if (length) *length = st.TextLen;
    return 1;
}

// The name of the front-most top-level window drawn last frame (z-order checks: Kiln.Ui.Preview --check-z).
__attribute__((visibility("default"))) int kiln_preview_front_window(char* buf, int cap) {
    ImGuiContext& g = *GImGui;
    for (int i = g.Windows.Size - 1; i >= 0; --i) {
        const ImGuiWindow* w = g.Windows[i];
        if (w->ParentWindow || !w->WasActive || w->Hidden || (w->Flags & ImGuiWindowFlags_Tooltip)) continue;
        if (buf && cap > 0) std::snprintf(buf, static_cast<size_t>(cap), "%s", w->Name);
        return 1;
    }
    return 0;
}

__attribute__((visibility("default"))) void kiln_preview_close() {
    if (ImGui::GetCurrentContext()) ImGui::DestroyContext();
    g_subs.clear();
}

}  // extern "C"
