// SPDX-License-Identifier: MIT
// kiln.ui: looks for Kiln's ImGui windows, the game's UI font and colours, mods' themes and fonts, and PNG images.
//  - KILN_UI_STYLE_KILN: Kiln's ember theme (branding/palette.json).
//  - KILN_UI_STYLE_GAME: the game's look: a palette made from the game's own menu colours (keen::FbUiBundle: the tab
//    labels, the tile, the widget ground, the tab highlight, a chosen tile's outline and row, attention, warning, the
//    check mark; look_palette.h) and its own UI font. Neither is shipped: kiln.ui reads the bundle's colours (offsets from
//    the game's reflection, kiln.types; bytes from kiln.assets) and the game's keen::UiFontResource (the default language
//    group's regular TTF, a content blob) at run time, on a worker, and gives the bytes to ImGui. Until they are read, and
//    where they cannot be, the game look is a neutral dark stand-in (look::neutral) and the system font. The same
//    resource's other fonts give the overlay's fonts what their Latin source lacks (font_group.h): the default group's
//    Cyrillic and Thai for every language, a Chinese, Japanese or Korean language's group while it is in use.
//  - KILN_UI_STYLE_PLAYER: the player's look (set_look: game, translucent, ember or a mod's theme, with an opacity and a
//    scale). Windows that push nothing get its colours too (their font stays the overlay's default).
//  - Themes (theme_create / push_theme): a base look's palette with a mod's roles, single colours, spacing, rounding,
//    background opacity and font. Fonts (font_load): mod TTF/OTF fonts in ImGui's font atlas (font_budget.h).
//  - Images: PNG -> RGBA texture registered with ImGui (ImGui::RegisterUserTexture; the Vulkan backend uploads it at the
//    next frame).
#include <windows.h>

#include <atomic>
#include <cmath>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

#include "imgui.h"
#include "imgui_internal.h"
#include "font_budget.h"
#include "font_group.h"
#include "text_glyphs.h"
#include "image_budget.h"
#include "image_rect.h"
#include "look_palette.h"
#include "services/kiln_assets.h"
#include "services/kiln_types.h"
#include "ui.h"
#include "ui_text.h"

#define STB_IMAGE_STATIC
#define STB_IMAGE_IMPLEMENTATION
#define STBI_ONLY_PNG
#define STBI_NO_STDIO
#if defined(__clang__)
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wunused-function"
#endif
#include "stb/stb_image.h"
#if defined(__clang__)
#pragma clang diagnostic pop
#endif

namespace kui {
namespace {

ImVec4 hex(uint32_t rgb, float a = 1.0f) {
    return ImVec4(((rgb >> 16) & 0xFF) / 255.f, ((rgb >> 8) & 0xFF) / 255.f, (rgb & 0xFF) / 255.f, a);
}
// 0xRRGGBBAA -> ImGui; v4a: its R, G, B at alpha a
ImVec4 v4(uint32_t c) { return ImVec4(look::ch(c, 24) / 255.f, look::ch(c, 16) / 255.f, look::ch(c, 8) / 255.f, look::ch(c, 0) / 255.f); }
ImVec4 v4a(uint32_t c, float a) { return ImVec4(look::ch(c, 24) / 255.f, look::ch(c, 16) / 255.f, look::ch(c, 8) / 255.f, a); }

float g_scale = 1.0f;

// ---- the game's colours (read on a worker; look_palette.h) ----
std::atomic<int> g_colors_state{0};  // 0 not asked (or to try again), 1 loading, 2 read (to apply), 3 applied, 4 the stand-in for good
look::Palette g_read_pal;            // the worker's result (published by state 2)
std::string g_colors_note;

// ---- the player's look and the game palette in use (any thread, under g_look_mu) ----
struct LookReq {
    uint32_t look = KILN_UI_LOOK_GAME;
    KilnHandle theme = 0;
    float background = 0.55f, opacity = 1.0f, scale = 1.0f;
};
std::mutex g_look_mu;
LookReq g_look;
look::Palette g_game_pal = look::neutral();
std::atomic<uint64_t> g_gen{1};          // bumped whenever a built look may be stale
std::atomic<float> g_user_scale{1.0f};
void stale() { g_gen.fetch_add(1, std::memory_order_acq_rel); }

// ---- themes (created on any thread, built on the render thread) ----
struct Theme {
    KilnHandle handle = 0;
    KilnOwner owner = 0;
    KilnUiThemeDef def{};
    // render thread
    ImGuiStyle st;
    look::Palette pal;
    ImFont* font = nullptr;
    uint64_t gen = 0;
};
std::mutex g_theme_mu;
std::unordered_map<KilnHandle, std::shared_ptr<Theme>> g_themes;

std::shared_ptr<Theme> theme_of(KilnHandle h) {
    std::lock_guard lk(g_theme_mu);
    auto it = g_themes.find(h);
    return it == g_themes.end() ? nullptr : it->second;
}

// ---- mod fonts (font_budget.h) ----
struct ModFont {
    KilnHandle handle = 0;
    KilnOwner owner = 0;
    std::vector<uint8_t> bytes;  // kept alive for the atlas (FontDataOwnedByAtlas = false)
    ImFont* font = nullptr;      // render thread
    bool added = false;          // render thread: AddFont was tried
    bool dead = false;
};
std::mutex g_font_mu;
std::vector<std::shared_ptr<ModFont>> g_mod_fonts;  // live, and freed ones until the atlas dropped them

ImFont* mod_font(KilnHandle h) {
    if (!h) return nullptr;
    std::lock_guard lk(g_font_mu);
    for (const auto& f : g_mod_fonts)
        if (f->handle == h && !f->dead) return f->font;
    return nullptr;
}

// ---- the game's font ----
std::atomic<int> g_font_state{0};  // 0 not asked (or to try again), 1 loading, 2 bytes ready, 3 added, 4 failed
std::vector<uint8_t> g_font_bytes;  // kept alive for the atlas (FontDataOwnedByAtlas = false)
std::vector<uint8_t> g_bold_bytes;  // the same group's bold face (empty when it has none)
ImFont* g_game_font = nullptr;
ImFont* g_game_bold = nullptr;
std::string g_font_note;

// ---- the game's font of a Chinese, Japanese or Korean language group (font_group.h) ----
// Read on a worker when Kiln's language or the game's is one of them, merged on the render thread into the overlay's fonts
// (its default, the game's UI font and bold) after their Latin source; removed again when another group or none is wanted.
struct GroupFont {
    uint32_t index = 0;               // in the resource's fonts
    std::vector<uint8_t> bytes;       // kept alive for the atlas (FontDataOwnedByAtlas = false)
    std::vector<ImWchar> exclude;     // ImGui's GlyphExcludeRanges (empty: none)
};
std::mutex g_group_mu;                               // the worker's result
std::vector<std::shared_ptr<GroupFont>> g_group_read;
uint32_t g_group_read_lang = 0;
std::string g_group_read_note;
std::atomic<int> g_group_state{0};       // 0 nothing wanted (or to try again), 1 loading, 2 read (to merge), 3 merged, 4 failed
std::atomic<uint32_t> g_group_lang{0};   // the keen::LanguageId the state is about (0 none)
std::atomic<bool> g_group_busy{false};
int g_group_tries = 0;                   // render thread: reads of this language so far
std::atomic<uint64_t> g_group_retry_at{0};  // a resource not listed yet: asked again then
bool g_group_said = false;               // render thread: the failure was logged
std::vector<std::shared_ptr<GroupFont>> g_group_fonts;  // render thread: merged
std::vector<ImFont*> g_group_into;                      // render thread: the fonts they are merged into
uint64_t g_group_started = 0;

// ---- the game's default group's fonts after its Latin source (font_group.h pick_default): Cyrillic, Thai ----
// Read on a worker at the first overlay frame, for every look and language, merged on the render thread into the overlay's
// fonts (the same three) after their Latin source and before a CJK group's; kept for the process.
std::mutex g_extra_mu;                                  // the worker's result
std::vector<std::shared_ptr<GroupFont>> g_extra_read;
std::string g_extra_note;
bool g_extra_read_cyrillic = false;
std::atomic<int> g_extra_state{0};       // 0 not asked (or to try again), 1 loading, 2 read (to merge), 3 merged, 4 failed
std::atomic<bool> g_extra_busy{false};
std::atomic<bool> g_extra_cyrillic{false};  // the fonts merged cover Cyrillic (U+0410..U+044F)
int g_extra_tries = 0;                   // render thread: reads so far
std::atomic<uint64_t> g_extra_retry_at{0};
bool g_extra_said = false;               // render thread: the failure was logged
std::vector<std::shared_ptr<GroupFont>> g_extra_fonts;  // render thread: merged
std::vector<ImFont*> g_extra_into;                      // render thread: the fonts they are merged into
std::atomic<uint32_t> g_glyph_need{0};   // fontgroup::needed() as of this frame (look_info's glyphs)

// ---- built looks (render thread) ----
struct Built {
    ImGuiStyle st;
    look::Palette pal;
    ImFont* font = nullptr;  // null: the overlay's default font
};
Built g_kiln, g_game, g_player;
uint64_t g_built = 0;
bool g_styles = false;

// The game look's shape: square-ish frames like the game's tiles.
void game_shape(ImGuiStyle& st) {
    st.WindowRounding = 2.0f;
    st.ChildRounding = 2.0f;
    st.FrameRounding = 2.0f;
    st.PopupRounding = 2.0f;
    st.GrabRounding = 2.0f;
    st.TabRounding = 0.0f;
    st.WindowBorderSize = 1.0f;
    st.FrameBorderSize = 1.0f;
    st.WindowPadding = ImVec2(14, 12);
    st.FramePadding = ImVec2(10, 6);
    st.ItemSpacing = ImVec2(10, 8);
    st.WindowTitleAlign = ImVec2(0.5f, 0.5f);
}

// The game recipe: ImGui's colours from a palette's roles (the roles in `mask`; KILN_UI_ROLE_* bits).
void apply_roles(ImGuiStyle& st, const look::Palette& p, uint32_t mask) {
    auto has = [&](int r) { return (mask & (1u << r)) != 0; };
    const uint32_t T = p[KILN_UI_ROLE_TEXT], M = p[KILN_UI_ROLE_TEXT_MUTED], W = p[KILN_UI_ROLE_WINDOW_BG],
                   P = p[KILN_UI_ROLE_PANEL_BG], F = p[KILN_UI_ROLE_FRAME_BG], B = p[KILN_UI_ROLE_BORDER],
                   A = p[KILN_UI_ROLE_ACCENT], U = p[KILN_UI_ROLE_BUTTON], H = p[KILN_UI_ROLE_HEADER];
    const float wa = look::ch(W, 0) / 255.0f;
    ImVec4* c = st.Colors;
    if (has(KILN_UI_ROLE_TEXT)) c[ImGuiCol_Text] = v4a(T, 1.0f);
    if (has(KILN_UI_ROLE_TEXT_MUTED)) {
        c[ImGuiCol_TextDisabled] = v4a(M, std::max(0.5f, look::ch(M, 0) / 255.0f));
        c[ImGuiCol_ScrollbarGrab] = v4a(M, 0.35f);
        c[ImGuiCol_TableBorderLight] = v4a(M, 0.15f);
    }
    if (has(KILN_UI_ROLE_WINDOW_BG)) {
        c[ImGuiCol_WindowBg] = v4a(W, wa);
        c[ImGuiCol_PopupBg] = v4a(W, std::max(look::kMinPopupAlpha, std::min(1.0f, wa + 0.06f)));
        c[ImGuiCol_TitleBg] = v4a(W, std::min(1.0f, wa + 0.05f));
        c[ImGuiCol_TitleBgCollapsed] = v4a(W, wa * 0.9f);
        c[ImGuiCol_MenuBarBg] = v4a(W, std::min(1.0f, wa + 0.05f));
    }
    if (has(KILN_UI_ROLE_WINDOW_BG) || has(KILN_UI_ROLE_PANEL_BG)) c[ImGuiCol_TitleBgActive] = v4a(look::mix(W, P, 0.4f), std::min(1.0f, wa + 0.08f));
    if (has(KILN_UI_ROLE_PANEL_BG)) {
        c[ImGuiCol_ChildBg] = v4a(P, 0.45f * wa / look::kGameWindowAlpha);
        c[ImGuiCol_FrameBgHovered] = v4a(P, 0.80f);
        c[ImGuiCol_TabSelected] = v4a(P, 0.95f);
        c[ImGuiCol_TabDimmedSelected] = v4a(P, 0.70f);
        c[ImGuiCol_TableHeaderBg] = v4a(P, 0.80f);
    }
    if (has(KILN_UI_ROLE_FRAME_BG)) {
        c[ImGuiCol_FrameBg] = v4a(F, 0.55f);
        c[ImGuiCol_ScrollbarBg] = v4a(F, 0.35f);
        c[ImGuiCol_Tab] = v4a(F, 0.60f);
        c[ImGuiCol_TabDimmed] = v4a(F, 0.50f);
    }
    if (has(KILN_UI_ROLE_BORDER)) {
        c[ImGuiCol_Border] = v4a(B, 0.45f);
        c[ImGuiCol_Separator] = v4a(B, 0.35f);
        c[ImGuiCol_SeparatorHovered] = v4a(B, 0.75f);
        c[ImGuiCol_ScrollbarGrabHovered] = v4a(B, 0.75f);
        c[ImGuiCol_ResizeGrip] = v4a(B, 0.25f);
        c[ImGuiCol_ResizeGripHovered] = v4a(B, 0.60f);
        c[ImGuiCol_TableBorderStrong] = v4a(B, 0.35f);
        c[ImGuiCol_PlotHistogram] = v4a(B, 1.0f);
    }
    if (has(KILN_UI_ROLE_ACCENT)) {
        c[ImGuiCol_CheckMark] = v4a(A, 1.0f);
        c[ImGuiCol_SliderGrab] = v4a(A, 0.85f);
        c[ImGuiCol_SliderGrabActive] = v4a(A, 1.0f);
        c[ImGuiCol_ScrollbarGrabActive] = v4a(A, 1.0f);
        c[ImGuiCol_SeparatorActive] = v4a(A, 1.0f);
        c[ImGuiCol_ResizeGripActive] = v4a(A, 1.0f);
        c[ImGuiCol_TabSelectedOverline] = v4a(A, 1.0f);
        c[ImGuiCol_PlotHistogramHovered] = v4a(A, 1.0f);
        c[ImGuiCol_TextSelectedBg] = v4a(A, 0.35f);
        c[ImGuiCol_NavCursor] = v4a(A, 1.0f);
        c[ImGuiCol_DragDropTarget] = v4a(A, 0.90f);
        c[ImGuiCol_ButtonActive] = v4a(A, 0.85f);
        c[ImGuiCol_HeaderActive] = v4a(A, 0.80f);
    }
    if (has(KILN_UI_ROLE_BUTTON)) c[ImGuiCol_Button] = v4a(U, 0.85f);
    if (has(KILN_UI_ROLE_HEADER)) {
        c[ImGuiCol_ButtonHovered] = v4a(H, 0.90f);
        c[ImGuiCol_Header] = v4a(H, 0.55f);
        c[ImGuiCol_HeaderHovered] = v4a(H, 0.85f);
        c[ImGuiCol_FrameBgActive] = v4a(H, 0.70f);
        c[ImGuiCol_TabHovered] = v4a(H, 0.85f);
    }
    c[ImGuiCol_TableRowBgAlt] = hex(0xffffff, 0.03f);
}

constexpr uint32_t kAllRoles = (1u << look::kRoles) - 1;

void make_game(ImGuiStyle& st, const look::Palette& p, float scale) {
    st = ImGuiStyle();
    ImGui::StyleColorsDark(&st);
    game_shape(st);
    apply_roles(st, p, kAllRoles);
    st.ScaleAllSizes(scale);
    st.FontScaleDpi = scale;
}

void make_kiln(ImGuiStyle& st, float scale) {
    st = ImGuiStyle();
    ImGui::StyleColorsDark(&st);
    // branding/palette.json: charcoal bg-0..3, text-0..2, ember-300..700, gold-400
    st.WindowRounding = 8.0f;
    st.ChildRounding = 6.0f;
    st.FrameRounding = 4.0f;
    st.PopupRounding = 6.0f;
    st.GrabRounding = 4.0f;
    st.TabRounding = 4.0f;
    st.WindowBorderSize = 1.0f;
    st.FramePadding = ImVec2(8, 4);
    st.ItemSpacing = ImVec2(8, 6);
    ImVec4* c = st.Colors;
    c[ImGuiCol_Text] = hex(0xf4ece6);
    c[ImGuiCol_TextDisabled] = hex(0x94857a);
    c[ImGuiCol_WindowBg] = hex(0x0f0d0c, 0.94f);
    c[ImGuiCol_ChildBg] = hex(0x171413, 0.60f);
    c[ImGuiCol_PopupBg] = hex(0x171413, 0.97f);
    c[ImGuiCol_Border] = hex(0x3a312c);
    c[ImGuiCol_FrameBg] = hex(0x1f1b19);
    c[ImGuiCol_FrameBgHovered] = hex(0x2a2421);
    c[ImGuiCol_FrameBgActive] = hex(0x4d413a);
    c[ImGuiCol_TitleBg] = hex(0x171413);
    c[ImGuiCol_TitleBgActive] = hex(0x2a2421);
    c[ImGuiCol_TitleBgCollapsed] = hex(0x0f0d0c, 0.8f);
    c[ImGuiCol_MenuBarBg] = hex(0x171413);
    c[ImGuiCol_ScrollbarBg] = hex(0x0f0d0c, 0.6f);
    c[ImGuiCol_ScrollbarGrab] = hex(0x4d413a);
    c[ImGuiCol_ScrollbarGrabHovered] = hex(0xb83f05);
    c[ImGuiCol_ScrollbarGrabActive] = hex(0xe5530a);
    c[ImGuiCol_CheckMark] = hex(0xff8540);
    c[ImGuiCol_SliderGrab] = hex(0xff6a1a);
    c[ImGuiCol_SliderGrabActive] = hex(0xffa56b);
    c[ImGuiCol_Button] = hex(0xb83f05, 0.85f);
    c[ImGuiCol_ButtonHovered] = hex(0xe5530a);
    c[ImGuiCol_ButtonActive] = hex(0xff6a1a);
    c[ImGuiCol_Header] = hex(0xb83f05, 0.55f);
    c[ImGuiCol_HeaderHovered] = hex(0xe5530a, 0.80f);
    c[ImGuiCol_HeaderActive] = hex(0xff6a1a);
    c[ImGuiCol_Separator] = hex(0x3a312c);
    c[ImGuiCol_SeparatorHovered] = hex(0xe5530a);
    c[ImGuiCol_SeparatorActive] = hex(0xff6a1a);
    c[ImGuiCol_ResizeGrip] = hex(0xb83f05, 0.4f);
    c[ImGuiCol_ResizeGripHovered] = hex(0xe5530a, 0.7f);
    c[ImGuiCol_ResizeGripActive] = hex(0xff6a1a);
    c[ImGuiCol_Tab] = hex(0x1f1b19);
    c[ImGuiCol_TabHovered] = hex(0xe5530a, 0.8f);
    c[ImGuiCol_TabSelected] = hex(0xb83f05);
    c[ImGuiCol_TabSelectedOverline] = hex(0xf9c74f);
    c[ImGuiCol_TabDimmed] = hex(0x171413);
    c[ImGuiCol_TabDimmedSelected] = hex(0x2a2421);
    c[ImGuiCol_PlotHistogram] = hex(0xff6a1a);
    c[ImGuiCol_PlotHistogramHovered] = hex(0xf9c74f);
    c[ImGuiCol_TableHeaderBg] = hex(0x1f1b19);
    c[ImGuiCol_TableBorderStrong] = hex(0x3a312c);
    c[ImGuiCol_TableBorderLight] = hex(0x2a2421);
    c[ImGuiCol_TableRowBgAlt] = hex(0xffffff, 0.03f);
    c[ImGuiCol_TextSelectedBg] = hex(0xff6a1a, 0.35f);
    c[ImGuiCol_NavCursor] = hex(0xf9c74f);
    st.ScaleAllSizes(scale);
    st.FontScaleDpi = scale;
}

// The windows' backgrounds at opacity `bg` (from the look's own `from`): windows and title bars, panels, menu and
// scroll bars, table headers in proportion; pop-ups stay readable.
void set_background(ImGuiStyle& st, look::Palette& pal, float bg, float from) {
    if (!(from > 0.0f)) from = 1.0f;
    const float k = bg / from;
    ImVec4* c = st.Colors;
    auto scale = [&](ImGuiCol i) { c[i].w = std::clamp(c[i].w * k, 0.0f, 1.0f); };
    c[ImGuiCol_WindowBg].w = bg;
    for (ImGuiCol i : {ImGuiCol_ChildBg, ImGuiCol_TitleBg, ImGuiCol_TitleBgActive, ImGuiCol_TitleBgCollapsed, ImGuiCol_MenuBarBg,
                       ImGuiCol_ScrollbarBg, ImGuiCol_TableHeaderBg})
        scale(i);
    c[ImGuiCol_PopupBg].w = std::max(look::kMinPopupAlpha, std::min(1.0f, c[ImGuiCol_PopupBg].w * k));
    pal[KILN_UI_ROLE_WINDOW_BG] = look::with_alpha_f(pal[KILN_UI_ROLE_WINDOW_BG], bg);
    pal[KILN_UI_ROLE_PANEL_BG] = look::with_alpha_f(pal[KILN_UI_ROLE_PANEL_BG], std::clamp(look::ch(pal[KILN_UI_ROLE_PANEL_BG], 0) / 255.0f * k, 0.0f, 1.0f));
}

// KILN_UI_COLOR_* -> ImGuiCol (kiln_ui.h; draw_slots_v3.inl's push_color uses the same map).
int color_slot(uint32_t slot) {
    static const ImGuiCol map[KILN_UI_COLOR_COUNT] = {
        ImGuiCol_Text,          ImGuiCol_TextDisabled,   ImGuiCol_WindowBg,      ImGuiCol_ChildBg,        ImGuiCol_Border,
        ImGuiCol_FrameBg,       ImGuiCol_FrameBgHovered, ImGuiCol_FrameBgActive, ImGuiCol_Button,         ImGuiCol_ButtonHovered,
        ImGuiCol_ButtonActive,  ImGuiCol_Header,         ImGuiCol_HeaderHovered, ImGuiCol_HeaderActive,   ImGuiCol_Separator,
        ImGuiCol_ScrollbarBg,   ImGuiCol_ScrollbarGrab,  ImGuiCol_NavCursor,     ImGuiCol_CheckMark,      ImGuiCol_SliderGrab,
        ImGuiCol_TitleBg,       ImGuiCol_TitleBgActive,  ImGuiCol_PopupBg,       ImGuiCol_Tab,            ImGuiCol_TabHovered,
        ImGuiCol_TabSelected,   ImGuiCol_TableHeaderBg,  ImGuiCol_TextSelectedBg,
    };
    return slot < KILN_UI_COLOR_COUNT ? map[slot] : -1;
}

void set_var(ImGuiStyle& st, uint32_t v, float x, float y, float scale) {
    switch (v) {
        case KILN_UI_VAR_WINDOW_PADDING: st.WindowPadding = ImVec2(x * scale, y * scale); break;
        case KILN_UI_VAR_FRAME_PADDING: st.FramePadding = ImVec2(x * scale, y * scale); break;
        case KILN_UI_VAR_ITEM_SPACING: st.ItemSpacing = ImVec2(x * scale, y * scale); break;
        case KILN_UI_VAR_ITEM_INNER_SPACING: st.ItemInnerSpacing = ImVec2(x * scale, y * scale); break;
        case KILN_UI_VAR_FRAME_ROUNDING: st.FrameRounding = x * scale; break;
        case KILN_UI_VAR_FRAME_BORDER_SIZE: st.FrameBorderSize = x * scale; break;
        case KILN_UI_VAR_CHILD_ROUNDING: st.ChildRounding = x * scale; break;
        case KILN_UI_VAR_CHILD_BORDER_SIZE: st.ChildBorderSize = x * scale; break;
        case KILN_UI_VAR_SELECTABLE_TEXT_ALIGN: st.SelectableTextAlign = ImVec2(std::clamp(x, 0.f, 1.f), std::clamp(y, 0.f, 1.f)); break;
        case KILN_UI_VAR_BUTTON_TEXT_ALIGN: st.ButtonTextAlign = ImVec2(std::clamp(x, 0.f, 1.f), std::clamp(y, 0.f, 1.f)); break;
        case KILN_UI_VAR_SCROLLBAR_SIZE: st.ScrollbarSize = x * scale; break;
        case KILN_UI_VAR_WINDOW_BORDER_SIZE: st.WindowBorderSize = x * scale; break;
        default: break;
    }
}

ImFont* game_font() { return g_game_font; }

// A theme on the current palettes and scale (render thread).
void build_theme(Theme& t, const look::Palette& game_pal, float opacity) {
    const KilnUiThemeDef& d = t.def;
    const bool kiln_base = d.base == KILN_UI_STYLE_KILN;
    const look::Palette base = kiln_base ? look::ember() : game_pal;
    t.pal = look::over(base, d.roles, d.role_mask);
    if (kiln_base) {
        make_kiln(t.st, g_scale);
        apply_roles(t.st, t.pal, d.role_mask & kAllRoles);
    } else {
        make_game(t.st, t.pal, g_scale);
    }
    for (uint32_t s = 0; s < KILN_UI_COLOR_COUNT && s < 64; ++s)
        if (d.color_mask & (1ull << s))
            if (const int i = color_slot(s); i >= 0) t.st.Colors[i] = v4(d.colors[s]);
    for (uint32_t v = 0; v < KILN_UI_VAR_COUNT && v < 16; ++v)
        if (d.var_mask & (1u << v)) set_var(t.st, v, d.vars[v][0], d.vars[v][1], g_scale);
    if (d.rounding >= 0.0f) {
        const float r = d.rounding * g_scale;
        t.st.WindowRounding = t.st.ChildRounding = t.st.FrameRounding = t.st.PopupRounding = t.st.GrabRounding = t.st.TabRounding = r;
        t.st.ScrollbarRounding = r;
    }
    if (d.background >= 0.0f)
        set_background(t.st, t.pal, std::clamp(d.background, 0.0f, 1.0f), look::ch(base[KILN_UI_ROLE_WINDOW_BG], 0) / 255.0f);
    t.st.Alpha = opacity;
    t.font = d.font ? mod_font(d.font) : nullptr;
    if (!t.font && !kiln_base) t.font = game_font();
}

// The player's look and the game palette, copied under the lock.
LookReq look_req(look::Palette* game_pal) {
    std::lock_guard lk(g_look_mu);
    if (game_pal) *game_pal = g_game_pal;
    return g_look;
}

// Rebuilds the base looks and the player's (render thread, outside a frame) when something changed.
void rebuild_if_stale() {
    const uint64_t gen = g_gen.load(std::memory_order_acquire);
    if (g_styles && g_built == gen) return;
    look::Palette gp;
    const LookReq req = look_req(&gp);
    make_kiln(g_kiln.st, g_scale);
    g_kiln.st.Alpha = req.opacity;
    g_kiln.pal = look::ember();
    g_kiln.font = nullptr;
    make_game(g_game.st, gp, g_scale);
    g_game.st.Alpha = req.opacity;
    g_game.pal = gp;
    g_game.font = game_font();
    std::shared_ptr<Theme> th = req.look == KILN_UI_LOOK_THEME ? theme_of(req.theme) : nullptr;
    if (th) {
        build_theme(*th, gp, req.opacity);
        th->gen = gen;
        g_player.st = th->st;
        g_player.pal = th->pal;
        g_player.font = th->font;
    } else if (req.look == KILN_UI_LOOK_KILN) {
        g_player = g_kiln;
    } else {
        g_player = g_game;
        if (req.look == KILN_UI_LOOK_TRANSLUCENT) set_background(g_player.st, g_player.pal, req.background, look::kGameWindowAlpha);
    }
    g_built = gen;
    g_styles = true;
}

// ---- style stack (render thread, inside a frame) ----
struct Pushed {
    ImGuiStyle saved;
    bool font;
    ImFont* look_font;  // the pushed look's font (null: the overlay's default)
    look::Palette pal;
};
std::vector<Pushed> g_stack;

struct BlobArray {
    int32_t off;
    uint32_t count;
};

const KilnAssetsApi* assets_api() {
    return g_api ? static_cast<const KilnAssetsApi*>(g_api->service(KilnStr{KILN_ASSETS_SERVICE, sizeof(KILN_ASSETS_SERVICE) - 1}, 1)) : nullptr;
}

// ---- the game's colours: offsets from kiln.types, bytes from kiln.assets ----

// The offset of `path` ("menu.tileBackgroundColor.value") below `type`, walking own fields, then base classes.
bool field_offset(const KilnTypesApi* tp, const KilnType* type, const char* path, uint64_t* out) {
    uint64_t at = 0;
    const KilnType* t = type;
    std::string rest(path);
    while (t) {
        const size_t dot = rest.find('.');
        const std::string name = rest.substr(0, dot);
        bool found = false;
        for (const KilnType* cur = t; cur && !found;) {
            KilnTypeInfo info{};
            info.size = sizeof info;
            if (tp->type_info(cur, &info) != KILN_OK) return false;
            for (uint32_t i = 0; i < info.field_count && i < 4096; ++i) {
                KilnTypeField f{};
                if (tp->type_field(cur, i, &f) != KILN_OK) continue;
                if (f.name.len == name.size() && std::memcmp(f.name.ptr, name.data(), name.size()) == 0) {
                    at += f.offset;
                    t = f.type;
                    found = true;
                    break;
                }
            }
            cur = found ? nullptr : (info.kind == KILN_TYPE_STRUCT ? info.inner : nullptr);  // a base class sits at offset 0
        }
        if (!found) return false;
        if (dot == std::string::npos) {
            *out = at;
            return true;
        }
        rest = rest.substr(dot + 1);
    }
    return false;
}

// The game's colours and font are read on one worker, at the first frame in a game-based look (or the first window that
// asks for the game's look or font). A resource the game has not listed yet (a frame drawn before its UI data loaded) is
// asked again 5 seconds later, at most kDataTries times in all; any other failure is final.
constexpr int kDataTries = 4;
std::atomic<bool> g_data_busy{false};
std::atomic<int> g_data_tries{0};
std::atomic<uint64_t> g_data_retry_at{0};
bool data_retry_left() { return g_data_tries.load() < kDataTries; }

void load_game_colors() {
    const uint64_t started = now_ms();
    auto fail = [](const std::string& why) {
        g_colors_note = why;
        g_colors_state = 4;
        logf(KILN_LOG_WARN, "ui: the game's UI colours are not available (%s): the game look uses its neutral stand-in", why.c_str());
    };
    auto not_yet = [&](const std::string& why) {
        if (!data_retry_left()) return fail(why);
        g_colors_note = why;
        g_colors_state = 0;  // asked again in 5 seconds
    };
    const auto* tp = static_cast<const KilnTypesApi*>(g_api->service(KilnStr{KILN_TYPES_SERVICE, sizeof(KILN_TYPES_SERVICE) - 1}, 1));
    if (!tp || tp->header.size < offsetof(KilnTypesApi, type_field) + sizeof(void*) || !tp->type_by_name || !tp->type_info || !tp->type_field)
        return fail("kiln.types unavailable");
    const auto* a = assets_api();
    if (!a) return fail("kiln.assets unavailable");
    static const char kBundle[] = "keen::FbUiBundle";
    const KilnType* bundle = tp->type_by_name(KilnStr{kBundle, sizeof(kBundle) - 1});
    if (!bundle) return fail("the game's reflection has no keen::FbUiBundle");
    KilnTypeInfo bi{};
    bi.size = sizeof bi;
    if (tp->type_info(bundle, &bi) != KILN_OK) return fail("keen::FbUiBundle unreadable");
    uint32_t colors[look::kGameColors] = {};
    uint32_t have = 0;
    uint64_t offs[look::kGameColors] = {};
    for (int i = 0; i < look::kGameColors; ++i) {
        const std::string path = std::string(look::kGameFields[i].path) + ".value";
        if (field_offset(tp, bundle, path.c_str(), &offs[i]) && offs[i] + 4 <= bi.type_size) have |= 1u << i;
    }
    if ((have & ((1u << look::kRequiredGameColors) - 1)) != (1u << look::kRequiredGameColors) - 1)
        return fail("keen::FbUiBundle lacks one of the menu colours");
    KilnAssetKey keys[4]{};
    size_t n = 0;
    const uint32_t hash = bi.hash ? bi.hash : look::fnv1a(kBundle);
    if (a->resource_list(hash, keys, 4, &n) != KILN_OK || n == 0) return not_yet("no keen::FbUiBundle resource");
    size_t len = 0;
    a->resource_read(&keys[0], nullptr, 0, &len);
    if (len < bi.type_size || len > (16u << 20)) return fail("keen::FbUiBundle unreadable");
    std::vector<uint8_t> res(len);
    if (a->resource_read(&keys[0], res.data(), res.size(), &len) != KILN_OK || len < bi.type_size) return fail("keen::FbUiBundle unreadable");
    for (int i = 0; i < look::kGameColors; ++i) {
        if (!(have & (1u << i))) continue;
        uint32_t abgr = 0;
        std::memcpy(&abgr, res.data() + offs[i], 4);
        colors[i] = look::from_keen(abgr);
    }
    bool used = false;
    g_read_pal = look::game(colors, have, &used);
    char b[160];
    std::snprintf(b, sizeof b, "%d of %d colours; read in %llu ms", __builtin_popcount(have), look::kGameColors,
                  static_cast<unsigned long long>(now_ms() - started));
    g_colors_note = b;
    g_colors_state = used ? 2 : 4;
}

// The default language group's regular font of the game's keen::UiFontResource (docs/re/game-ui.md). On its own thread,
// started by the first frame in a game-based look: it reads two blobs of about 85 KB from the game's containers. The
// log line says how long that took (24.6 s on a hard disk before blob_read read only the blob's bytes).
void load_game_font() {
    const uint64_t started = now_ms();
    const auto* a = assets_api();
    auto fail = [](const char* why) {
        g_font_note = why;
        g_font_state = 4;
        logf(KILN_LOG_WARN, "ui: the game's UI font is not available (%s): game-style windows use the system font", why);
    };
    auto not_yet = [&](const char* why) {
        if (!data_retry_left()) return fail(why);
        g_font_note = why;
        g_font_state = 0;  // asked again in 5 seconds
    };
    if (!a) return fail("kiln.assets unavailable");
    constexpr uint32_t kUiFontResource = look::fnv1a("keen::UiFontResource");
    KilnAssetKey keys[8]{};
    size_t n = 0;
    if (a->resource_list(kUiFontResource, keys, 8, &n) != KILN_OK || n == 0) return not_yet("no keen::UiFontResource");
    size_t len = 0;
    a->resource_read(&keys[0], nullptr, 0, &len);
    if (len < 16 || len > (1u << 20)) return fail("UiFontResource unreadable");
    std::vector<uint8_t> res(len);
    if (a->resource_read(&keys[0], res.data(), res.size(), &len) != KILN_OK) return fail("UiFontResource unreadable");
    auto arr = [&](size_t at) -> BlobArray {
        BlobArray b{};
        if (at + 8 <= res.size()) std::memcpy(&b, res.data() + at, 8);
        return b;
    };
    const BlobArray fonts = arr(0), groups = arr(8);
    if (!fonts.count || fonts.count > 64) return fail("no fonts in UiFontResource");
    // the default group (isDefaultLanguageGroup +0x14), else the first; its first glyph set without variants, and its
    // first bold one (keen::UiFontVariantMask: bit 0 Bold, bit 1 Italic; 26430625: fonts 0 and 24)
    uint32_t font_index = 0, bold_index = UINT32_MAX;
    const size_t gbase = 8 + static_cast<size_t>(groups.off);
    size_t pick = SIZE_MAX;
    for (uint32_t i = 0; i < groups.count && i < 64; ++i) {
        const size_t g = gbase + i * 24ull;
        if (g + 24 > res.size()) break;
        if (pick == SIZE_MAX) pick = g;
        if (res[g + 0x14]) {
            pick = g;
            break;
        }
    }
    if (pick != SIZE_MAX) {
        const BlobArray sets = arr(pick + 8);
        bool regular = false;
        const size_t sbase = pick + 8 + static_cast<size_t>(sets.off);
        for (uint32_t i = 0; i < sets.count && i < 64; ++i) {
            const size_t s = sbase + i * 20ull;
            if (s + 20 > res.size()) break;
            if (res[s + 0x10] == 0 && !regular) {  // fontVariantMask: none (regular)
                std::memcpy(&font_index, res.data() + s, 4);
                regular = true;
            }
            if (res[s + 0x10] == 1 && bold_index == UINT32_MAX) std::memcpy(&bold_index, res.data() + s, 4);  // bold only
            if (regular && bold_index != UINT32_MAX) break;
        }
    }
    if (font_index >= fonts.count) font_index = 0;
    // one font's TTF content blob; empty when out of range or unreadable
    auto ttf_of = [&](uint32_t index) -> std::vector<uint8_t> {
        const size_t f = 0 + static_cast<size_t>(fonts.off) + index * 32ull;
        if (index >= fonts.count || f + 16 > res.size()) return {};
        KilnContentHash h{};
        std::memcpy(&h, res.data() + f, sizeof h);
        if (h.size < 1024 || h.size > (32u << 20)) return {};
        std::vector<uint8_t> ttf(h.size);
        size_t got = 0;
        if (a->blob_read(&h, ttf.data(), ttf.size(), &got) != KILN_OK || got != h.size) return {};
        return ttf;
    };
    std::vector<uint8_t> ttf = ttf_of(font_index);
    if (ttf.empty()) return fail("font blob unreadable");
    if (bold_index != UINT32_MAX && bold_index != font_index) g_bold_bytes = ttf_of(bold_index);
    g_font_bytes.swap(ttf);
    char b[128];
    std::snprintf(b, sizeof b, "font %u of %u, %zu bytes; bold %s; read in %llu ms", font_index, fonts.count, g_font_bytes.size(),
                  g_bold_bytes.empty() ? "none" : std::to_string(bold_index).c_str(),
                  static_cast<unsigned long long>(now_ms() - started));
    g_font_note = b;
    g_font_state = 2;
}

// ---- the game's font of a CJK language group ----

// Where the fields of the font resource are: the game's reflection (kiln.types) when it has them, else client 26430625's
// layout (font_group.h, checked against that build's data offline).
fontgroup::Layout group_layout(std::string* note) {
    fontgroup::Layout l;
    const auto* tp = static_cast<const KilnTypesApi*>(g_api->service(KilnStr{KILN_TYPES_SERVICE, sizeof(KILN_TYPES_SERVICE) - 1}, 1));
    if (!tp || tp->header.size < offsetof(KilnTypesApi, type_field) + sizeof(void*) || !tp->type_by_name || !tp->type_info || !tp->type_field) {
        *note = "layout of 26430625 (no kiln.types)";
        return l;
    }
    auto type = [&](const char* name, uint32_t* size) -> const KilnType* {
        const KilnType* t = tp->type_by_name(KilnStr{name, std::strlen(name)});
        KilnTypeInfo i{};
        i.size = sizeof i;
        if (!t || tp->type_info(t, &i) != KILN_OK || i.type_size == 0 || i.type_size > 4096) return nullptr;
        if (size) *size = static_cast<uint32_t>(i.type_size);
        return t;
    };
    auto at = [&](const KilnType* t, const char* field, uint32_t* out) {
        uint64_t o = 0;
        if (!t || !field_offset(tp, t, field, &o) || o > 4096) return false;
        *out = static_cast<uint32_t>(o);
        return true;
    };
    fontgroup::Layout r;
    const KilnType* res = type("keen::UiFontResource", nullptr);
    const KilnType* group = type("keen::UiFontLanguageGroupResource", &r.group_size);
    const KilnType* set = type("keen::UiGlyphSetResource", &r.set_size);
    const KilnType* range = type("keen::UiFontRange", &r.range_size);
    const bool ok = type("keen::TtfFontResource", &r.font_size) && at(res, "fonts", &r.res_fonts) &&
                    at(res, "languageGroups", &r.res_groups) && at(group, "groupLanguages", &r.group_mask) &&
                    at(group, "glyphSets", &r.group_sets) && at(group, "isDefaultLanguageGroup", &r.group_default) &&
                    at(set, "fontIndex", &r.set_font) && at(set, "ranges", &r.set_ranges) && at(set, "fontVariantMask", &r.set_variant) &&
                    at(range, "firstRune", &r.range_first) && at(range, "lastRune", &r.range_last) &&
                    r.group_mask + 8 <= r.group_size && r.group_default < r.group_size && r.set_variant < r.set_size &&
                    r.set_font + 4 <= r.set_size && r.range_last + 4 <= r.range_size && r.font_size >= 16;
    if (!ok) {
        *note = "layout of 26430625 (the reflection lacks a field)";
        return l;
    }
    *note = "layout from the game's reflection";
    return r;
}

// The game's keen::UiFontResource's bytes (kiln.assets): 2 read, 0 not listed yet, 4 unreadable; `why` when not read.
int read_font_resource(const KilnAssetsApi* a, std::vector<uint8_t>& res, std::string& why) {
    if (!a) return why = "kiln.assets unavailable", 4;
    constexpr uint32_t kUiFontResource = look::fnv1a("keen::UiFontResource");
    KilnAssetKey keys[8]{};
    size_t n = 0;
    if (a->resource_list(kUiFontResource, keys, 8, &n) != KILN_OK || n == 0) return why = "no keen::UiFontResource yet", 0;
    size_t len = 0;
    a->resource_read(&keys[0], nullptr, 0, &len);
    if (len < 16 || len > (1u << 20)) return why = "UiFontResource unreadable", 4;
    res.assign(len, 0);
    if (a->resource_read(&keys[0], res.data(), res.size(), &len) != KILN_OK) return why = "UiFontResource unreadable", 4;
    return 2;
}

// The content hashes of a pick's fonts (their sizes first), or false when the fonts are out of the resource.
bool font_hashes(const std::vector<uint8_t>& res, const fontgroup::Layout& l, const fontgroup::Pick& p, std::vector<KilnContentHash>& hashes) {
    size_t fbase = 0;
    uint32_t fcount = 0;
    if (!fontgroup::blob_array(res.data(), res.size(), l.res_fonts, l.font_size, &fbase, &fcount)) return false;
    for (const fontgroup::Set& set : p.sets) {
        KilnContentHash h{};
        std::memcpy(&h, res.data() + fbase + static_cast<size_t>(set.font) * l.font_size, sizeof h);
        hashes.push_back(h);
    }
    return true;
}

// One font of a set read through kiln.assets (blob_read), with its exclusions; null when unreadable or no font.
std::shared_ptr<GroupFont> read_group_font(const KilnAssetsApi* a, const fontgroup::Set& set, const KilnContentHash& h) {
    auto f = std::make_shared<GroupFont>();
    f->index = set.font;
    f->bytes.resize(h.size);
    size_t read = 0;
    if (a->blob_read(&h, f->bytes.data(), f->bytes.size(), &read) != KILN_OK || read != f->bytes.size() ||
        !fonts::is_font(f->bytes.data(), f->bytes.size()))
        return nullptr;
    for (uint16_t c : fontgroup::exclude_ranges(set.ranges)) f->exclude.push_back(static_cast<ImWchar>(c));
    return f;
}

// The worker: the group of `lang` (a keen::LanguageId) from the game's keen::UiFontResource, its regular fonts' blobs.
void load_group_fonts(uint32_t lang) {
    const uint64_t started = now_ms();
    std::vector<std::shared_ptr<GroupFont>> got;
    auto done = [&](int state, const std::string& note) {
        {
            std::lock_guard lk(g_group_mu);
            g_group_read = std::move(got);
            g_group_read_lang = lang;
            g_group_read_note = note;
        }
        if (state == 0) g_group_retry_at = now_ms() + 5000;
        g_group_state = state;
    };
    const auto* a = assets_api();
    std::vector<uint8_t> res;
    std::string why;
    if (const int st = read_font_resource(a, res, why); st != 2) return done(st, why);
    std::string layout_note;
    const fontgroup::Layout l = group_layout(&layout_note);
    const fontgroup::Pick p = fontgroup::pick(res.data(), res.size(), lang, l);
    if (p.sets.empty()) return done(4, p.why + " (" + layout_note + ")");
    // the blobs' sizes (each font's content hash), then the budget
    std::vector<KilnContentHash> hashes;
    if (!font_hashes(res, l, p, hashes)) return done(4, "fonts out of the resource");
    std::vector<size_t> sizes;
    for (const KilnContentHash& h : hashes) sizes.push_back(h.size);
    const size_t keep = fonts::admit_group(sizes.data(), sizes.size());
    if (keep == 0) return done(4, "its first font is over the budget (font_budget.h)");
    std::string which;
    size_t total = 0;
    for (size_t i = 0; i < keep; ++i) {
        auto f = read_group_font(a, p.sets[i], hashes[i]);
        if (!f) return done(4, "font " + std::to_string(p.sets[i].font) + " of the group is unreadable");
        total += f->bytes.size();
        which += (which.empty() ? "" : ", ") + std::to_string(f->index);
        got.push_back(std::move(f));
    }
    char b[200];
    std::snprintf(b, sizeof b, "group %d, fonts %s of %u, %.1f MB%s; %s; read in %llu ms", p.group, which.c_str(), p.font_count,
                  total / 1048576.0, keep < p.sets.size() ? " (the rest over the budget)" : "", layout_note.c_str(),
                  static_cast<unsigned long long>(now_ms() - started));
    done(2, b);
}

// The worker: the default group's fonts after its Latin source (pick_default), the small ones (admit_default).
void load_default_fonts() {
    const uint64_t started = now_ms();
    std::vector<std::shared_ptr<GroupFont>> got;
    bool cyrillic = false;
    auto done = [&](int state, const std::string& note) {
        {
            std::lock_guard lk(g_extra_mu);
            g_extra_read = std::move(got);
            g_extra_note = note;
            g_extra_read_cyrillic = cyrillic;
        }
        if (state == 0) g_extra_retry_at = now_ms() + 5000;
        g_extra_state = state;
    };
    const auto* a = assets_api();
    std::vector<uint8_t> res;
    std::string why;
    if (const int st = read_font_resource(a, res, why); st != 2) return done(st, why);
    std::string layout_note;
    const fontgroup::Layout l = group_layout(&layout_note);
    const fontgroup::Pick p = fontgroup::pick_default(res.data(), res.size(), l);
    if (p.sets.empty()) return done(4, p.why + " (" + layout_note + ")");
    std::vector<KilnContentHash> hashes;
    if (!font_hashes(res, l, p, hashes)) return done(4, "fonts out of the resource");
    std::vector<size_t> sizes;
    for (const KilnContentHash& h : hashes) sizes.push_back(h.size);
    std::unique_ptr<bool[]> keep(new bool[sizes.size() + 1]());
    fonts::admit_default(sizes.data(), sizes.size(), keep.get());
    std::string which, left, unreadable;
    std::vector<fontgroup::Set> kept;
    size_t total = 0;
    for (size_t i = 0; i < p.sets.size(); ++i) {
        const std::string index = std::to_string(p.sets[i].font);
        if (!keep[i]) {
            left += (left.empty() ? "" : ", ") + index;
            continue;
        }
        auto f = read_group_font(a, p.sets[i], hashes[i]);
        if (!f) {
            unreadable += (unreadable.empty() ? "" : ", ") + index;
            continue;
        }
        total += f->bytes.size();
        which += (which.empty() ? "" : ", ") + index;
        kept.push_back(p.sets[i]);
        got.push_back(std::move(f));
    }
    cyrillic = fontgroup::covers(kept, 0x410, 0x44F);
    std::string note = "group " + std::to_string(p.group) + ", fonts " + (which.empty() ? "none" : which) + " of " +
                       std::to_string(p.font_count);
    char b[64];
    std::snprintf(b, sizeof b, ", %.1f MB", total / 1048576.0);
    note += b;
    note += cyrillic ? "; Cyrillic" : "; no Cyrillic";
    if (!left.empty()) note += "; fonts " + left + " left out (over 2 MiB: a Chinese, Japanese or Korean language's own group brings them)";
    if (!unreadable.empty()) note += "; fonts " + unreadable + " unreadable";
    std::snprintf(b, sizeof b, "; read in %llu ms", static_cast<unsigned long long>(now_ms() - started));
    note += "; " + layout_note + b;
    if (got.empty()) return done(4, note);
    done(2, note);
}

// Takes a layer's fonts out of the atlas (render thread, outside a frame): their sources leave every font they were
// merged into, whose glyphs are made again as they are drawn. A CJK group's when another or none is wanted.
void unmerge(std::vector<std::shared_ptr<GroupFont>>& layer, std::vector<ImFont*>& into) {
    if (layer.empty()) return;
    ImFontAtlas* atlas = ImGui::GetIO().Fonts;
    for (ImFont* f : into) ImFontAtlasFontDiscardBakes(atlas, f, 0);  // their bakes hold loader data per source
    for (int i = atlas->Sources.Size - 1; i >= 0; --i) {
        ImFontConfig& src = atlas->Sources[i];
        bool ours = false;
        for (const auto& g : layer) ours = ours || src.FontData == g->bytes.data();
        if (!ours) continue;
        const ImFontLoader* loader = src.FontLoader ? src.FontLoader : atlas->FontLoader;
        if (loader && loader->FontSrcDestroy) loader->FontSrcDestroy(atlas, &src);
        ImFontAtlasFontDestroySourceData(atlas, &src);
        atlas->Sources.erase(&atlas->Sources[i]);
    }
    ImFontAtlasBuildUpdatePointers(atlas);
    into.clear();
    layer.clear();
}
void unmerge_group() { unmerge(g_group_fonts, g_group_into); }

// Merges a layer's fonts (the default group's, or a CJK group's) into a font of the overlay that does not have them yet
// (render thread, outside a frame).
void merge_into(const std::vector<std::shared_ptr<GroupFont>>& layer, std::vector<ImFont*>& into, ImFont* target, const char* what) {
    if (!target || layer.empty()) return;
    for (ImFont* f : into)
        if (f == target) return;
    ImFontAtlas* atlas = ImGui::GetIO().Fonts;
    for (const auto& g : layer) {
        ImFontConfig cfg;
        cfg.MergeMode = true;
        cfg.DstFont = target;
        cfg.FontDataOwnedByAtlas = false;
        cfg.GlyphExcludeRanges = g->exclude.empty() ? nullptr : g->exclude.data();
        std::snprintf(cfg.Name, sizeof cfg.Name, "game font %u (%s)", g->index, what);
        atlas->AddFontFromMemoryTTF(g->bytes.data(), static_cast<int>(g->bytes.size()), 0.0f, &cfg);  // 0: the target's size
    }
    into.push_back(target);
}
void merge_group_into(ImFont* target) { merge_into(g_group_fonts, g_group_into, target, "language group"); }

// The overlay's fonts that get the game's other fonts: its default, the game's UI font and bold.
template <class F>
void each_target(F&& f) {
    ImGuiIO& io = ImGui::GetIO();
    if (io.Fonts->Fonts.Size > 0) f(io.Fonts->Fonts[0]);
    f(g_game_font);
    f(g_game_bold);
}

// Each frame (render thread, before NewFrame, before update_group_fonts): the default group's fonts read once (asked again
// while the game has not listed the resource), merged as they arrive and into fonts that arrive later. True when the atlas
// changed.
bool update_default_fonts() {
    const int st = g_extra_state.load();
    if (st == 0 && !g_extra_busy.load() && g_extra_tries < kDataTries && now_ms() >= g_extra_retry_at) {
        ++g_extra_tries;
        g_extra_busy = true;
        g_extra_state = 1;
        std::thread([] {
            load_default_fonts();
            g_extra_busy = false;
        }).detach();
        return false;
    }
    if (st == 0 && g_extra_tries >= kDataTries && !g_extra_busy.load()) {  // never listed: give up
        g_extra_state = 4;
        g_extra_said = true;
        logf(KILN_LOG_WARN, "ui: the game's default font group is not available (the game did not list its UI fonts): Kiln's "
                            "texts in a Cyrillic language stay English, other Cyrillic text in game-style windows shows as ?");
        return false;
    }
    if (st == 2) {
        std::string note;
        bool cyr = false;
        {
            std::lock_guard lk(g_extra_mu);
            g_extra_fonts = std::move(g_extra_read);
            g_extra_read.clear();
            note = g_extra_note;
            cyr = g_extra_read_cyrillic;
        }
        g_extra_cyrillic = cyr;
        g_extra_state = 3;
        logf(KILN_LOG_INFO, "ui: the game's default font group joined the overlay's font atlas (%s)", note.c_str());
        if (!cyr)
            logf(KILN_LOG_WARN, "ui: the game's default font group has no Cyrillic font Kiln can use: Kiln's texts in a Cyrillic "
                                "language stay English");
    }
    if (st == 4 && !g_extra_said) {
        std::string note;
        {
            std::lock_guard lk(g_extra_mu);
            note = g_extra_note;
        }
        g_extra_said = true;
        logf(KILN_LOG_WARN, "ui: the game's default font group is not available (%s): Kiln's texts in a Cyrillic language stay "
                            "English, other Cyrillic text in game-style windows shows as ?", note.c_str());
    }
    if (g_extra_state.load() != 3) return false;
    const size_t before = g_extra_into.size();
    each_target([](ImFont* f) { merge_into(g_extra_fonts, g_extra_into, f, "default group"); });
    return g_extra_into.size() != before;
}

// The CJK language whose group the overlay needs now (render thread): Kiln's (set_texts) when it is CJK, else the game's,
// else zh-CN's once a drawn text needed CJK glyphs (text_glyphs.h); and the language look_info reports the glyphs of
// (fontgroup::needed: Kiln's and the game's only) into g_glyph_need.
uint32_t group_wanted() {
    uint32_t game = 0;
    KilnUiScreen sc{};
    sc.size = sizeof sc;
    if (game_screen(&sc) == KILN_OK) game = sc.language;
    const std::string kiln(text::table().language());
    g_glyph_need = fontgroup::needed(kiln, game);
    const bool text = g_text_cjk.load(std::memory_order_relaxed);
    const uint32_t want = fontgroup::wanted(kiln, game, text);
    static bool said = false;
    if (text && !said && want != fontgroup::wanted(kiln, game)) {
        said = true;
        logf(KILN_LOG_INFO, "ui: a text in Chinese, Japanese or Korean is drawn (a mod's) while neither Kiln's language nor the "
                            "game's is one: the game's Chinese font group joins the overlay's font atlas for it");
    }
    return want;
}

// Each frame (render thread, before NewFrame): start the read of a newly wanted group, merge one that was read, merge into
// a font that arrived later (the game's), drop a group no longer wanted. True when the atlas changed.
bool update_group_fonts() {
    const uint32_t want = group_wanted();
    bool changed = false;
    if (want != g_group_lang.load()) {
        if (g_group_busy.load()) return false;  // the read in flight finishes first
        if (!g_group_fonts.empty()) {
            unmerge_group();
            changed = true;
            logf(KILN_LOG_INFO, "ui: the game's font of language %u left the overlay's font atlas", g_group_lang.load());
        }
        g_group_lang = want;
        g_group_state = 0;
        g_group_tries = 0;
        g_group_retry_at = 0;
        g_group_said = false;
    }
    if (!want) return changed;
    int st = g_group_state.load();
    if (st == 0 && !g_group_busy.load() && g_group_tries < kDataTries && now_ms() >= g_group_retry_at) {
        ++g_group_tries;
        g_group_busy = true;
        g_group_state = 1;
        g_group_started = now_ms();
        std::thread([want] {
            load_group_fonts(want);
            g_group_busy = false;
        }).detach();
        return changed;
    }
    if (st == 0 && g_group_tries >= kDataTries && !g_group_busy.load()) {  // never listed: give up
        g_group_state = 4;
        g_group_said = true;
        logf(KILN_LOG_WARN, "ui: the game's font of language %u is not available (the game did not list its UI fonts): Kiln's "
                            "texts stay English, other text in that language shows as boxes", want);
        return changed;
    }
    if (st == 2) {
        std::string note;
        uint32_t lang = 0;
        {
            std::lock_guard lk(g_group_mu);
            g_group_fonts = std::move(g_group_read);
            g_group_read.clear();
            lang = g_group_read_lang;
            note = g_group_read_note;
        }
        if (lang != want) {  // read for another language meanwhile: read again
            g_group_fonts.clear();
            g_group_state = 0;
            return changed;
        }
        g_group_state = 3;
        logf(KILN_LOG_INFO, "ui: the game's font of language %u joined the overlay's font atlas (%s)", want, note.c_str());
    }
    if (st == 4 && !g_group_said) {
        std::string note;
        {
            std::lock_guard lk(g_group_mu);
            note = g_group_read_note;
        }
        g_group_said = true;
        logf(KILN_LOG_WARN, "ui: the game's font of language %u is not available (%s): Kiln's texts stay English, other text "
                            "in that language shows as boxes", want, note.c_str());
    }
    // after the default group's fonts in each font (merged first, update_default_fonts), unless those failed
    const int ex = g_extra_state.load();
    if (g_group_state.load() == 3 && (ex == 3 || ex == 4)) {
        const size_t before = g_group_into.size();
        each_target([](ImFont* f) { merge_group_into(f); });
        changed = changed || g_group_into.size() != before;
    }
    return changed;
}

// The game's colours, then its font, on one worker (the first game-based look or game font asked for; again while a
// resource was not listed yet, see kDataTries).
void ensure_game_data() {
    const bool colors = g_colors_state.load() == 0, font = g_font_state.load() == 0;
    if (!colors && !font) return;
    if (g_data_busy.load() || !data_retry_left() || now_ms() < g_data_retry_at.load()) return;
    if (g_data_busy.exchange(true)) return;
    g_data_tries.fetch_add(1);
    if (colors) g_colors_state = 1;
    if (font) g_font_state = 1;
    std::thread([colors, font] {
        if (colors) load_game_colors();
        if (font) load_game_font();
        g_data_retry_at = now_ms() + 5000;
        g_data_busy = false;
    }).detach();
}
void ensure_game_font() { ensure_game_data(); }

bool game_based(const LookReq& r) {
    if (r.look == KILN_UI_LOOK_KILN) return false;
    if (r.look == KILN_UI_LOOK_THEME) {
        auto t = theme_of(r.theme);
        return !t || t->def.base != KILN_UI_STYLE_KILN;
    }
    return true;
}

// Mod fonts in and out of the atlas (render thread, before NewFrame); true when one changed.
bool update_mod_fonts() {
    ImGuiIO& io = ImGui::GetIO();
    bool changed = false;
    std::lock_guard lk(g_font_mu);
    for (auto it = g_mod_fonts.begin(); it != g_mod_fonts.end();) {
        ModFont& f = **it;
        if (f.dead) {
            if (f.font) io.Fonts->RemoveFont(f.font);
            f.font = nullptr;
            it = g_mod_fonts.erase(it);
            changed = true;
            continue;
        }
        if (!f.added) {
            f.added = true;
            ImFontConfig cfg;
            cfg.FontDataOwnedByAtlas = false;
            std::snprintf(cfg.Name, sizeof cfg.Name, "mod font %llu", static_cast<unsigned long long>(f.handle));
            f.font = io.Fonts->AddFontFromMemoryTTF(f.bytes.data(), static_cast<int>(f.bytes.size()), 17.0f, &cfg);
            logf(f.font ? KILN_LOG_INFO : KILN_LOG_WARN, "ui: a mod font (%zu bytes) %s", f.bytes.size(),
                 f.font ? "joined the font atlas" : "was rejected by ImGui: its text keeps the current font");
            changed = true;
        }
        ++it;
    }
    return changed;
}

void push_look(const ImGuiStyle& st, ImFont* font, const look::Palette& pal) {
    Pushed p{ImGui::GetStyle(), false, font, pal};
    ImGui::GetStyle() = st;
    ImFont* f = font ? font : ImGui::GetIO().FontDefault;
    if (!f && ImGui::GetIO().Fonts->Fonts.Size > 0) f = ImGui::GetIO().Fonts->Fonts[0];
    if (f) {
        ImGui::PushFont(f, 0.0f);
        p.font = true;
    }
    g_stack.push_back(p);
}

// ---- images ----
struct Image {
    KilnHandle handle;
    KilnOwner owner;
    ImTextureData* tex;  // created on the render thread
    std::vector<uint8_t> rgba;
    int w, h;
    bool registered;
    bool dead;
    bool failed = false;  // uploaded without a descriptor set (image_budget.h): never drawn
    img::Rect opaque{};   // its opaque bounds, found when loaded (image_rect.h): trimmed draws, image_opaque_rect
};
std::mutex g_img_mu;
std::unordered_map<KilnHandle, Image*> g_images;
std::vector<Image*> g_img_dead;

}  // namespace

float style_scale() { return g_scale; }
float user_scale() { return g_user_scale.load(std::memory_order_relaxed); }

void style_apply_base(float scale) {
    g_scale = scale;
    stale();
    rebuild_if_stale();
    ImGui::GetStyle() = g_player.st;
}

void style_push(uint32_t style) {
    if (!g_styles) return;
    if (style == KILN_UI_STYLE_GAME) {
        ensure_game_data();
        push_look(g_game.st, g_game.font, g_game.pal);
    } else if (style == KILN_UI_STYLE_PLAYER) {
        push_look(g_player.st, g_player.font, g_player.pal);
    } else {
        push_look(g_kiln.st, g_kiln.font, g_kiln.pal);
    }
}

bool theme_push(KilnHandle h) {
    if (!g_styles) return false;
    std::shared_ptr<Theme> t = theme_of(h);
    if (!t) return false;
    const uint64_t gen = g_gen.load(std::memory_order_acquire);
    if (t->gen != gen) {
        look::Palette gp;
        const LookReq req = look_req(&gp);
        if (t->def.base != KILN_UI_STYLE_KILN) ensure_game_data();
        build_theme(*t, gp, req.opacity);
        t->gen = gen;
    }
    push_look(t->st, t->font, t->pal);
    return true;
}

bool style_push_font(uint32_t font) {
    ImFont* f = nullptr;
    if (font == KILN_UI_FONT_DEFAULT) {
        ImGuiIO& io = ImGui::GetIO();
        f = !g_stack.empty() && g_stack.back().look_font ? g_stack.back().look_font
            : io.FontDefault ? io.FontDefault : (io.Fonts->Fonts.Size > 0 ? io.Fonts->Fonts[0] : nullptr);
    } else if (font == KILN_UI_FONT_GAME || font == KILN_UI_FONT_GAME_BOLD) {
        ensure_game_font();
        f = font == KILN_UI_FONT_GAME ? g_game_font : g_game_bold;
    }
    if (!f) return false;
    ImGui::PushFont(f, 0.0f);  // the current size
    return true;
}

bool style_push_font_handle(KilnHandle h) {
    ImFont* f = mod_font(h);
    if (!f) return false;
    ImGui::PushFont(f, 0.0f);
    return true;
}

void style_pop() {
    if (g_stack.empty()) return;
    Pushed p = g_stack.back();
    g_stack.pop_back();
    if (p.font) ImGui::PopFont();
    ImGui::GetStyle() = p.saved;
}

size_t style_depth() { return g_stack.size(); }

void style_frame_end() {
    while (!g_stack.empty()) style_pop();  // a callback that forgot pop_style
}

// ---- looks, themes and fonts: the table's slots (any thread unless said) ----

KilnResult theme_create(KilnOwner owner, const KilnUiThemeDef* def, KilnHandle* out) {
    if (!def || def->size < sizeof(KilnUiThemeDef)) return KILN_ERR_INVALID_ARGUMENT;
    if (def->base != KILN_UI_STYLE_GAME && def->base != KILN_UI_STYLE_KILN) return KILN_ERR_INVALID_ARGUMENT;
    if (!std::isfinite(def->background) || !std::isfinite(def->rounding)) return KILN_ERR_INVALID_ARGUMENT;
    for (const auto& v : def->vars)
        if (!std::isfinite(v[0]) || !std::isfinite(v[1])) return KILN_ERR_INVALID_ARGUMENT;
    auto t = std::make_shared<Theme>();
    t->handle = next_handle();
    t->owner = owner;
    std::memcpy(&t->def, def, sizeof(KilnUiThemeDef));
    t->def.size = sizeof(KilnUiThemeDef);
    t->def.rounding = std::min(t->def.rounding, 64.0f);
    {
        std::lock_guard lk(g_theme_mu);
        g_themes[t->handle] = t;
    }
    stale();  // the player's look may name it
    if (out) *out = t->handle;
    return KILN_OK;
}

KilnResult theme_free(KilnHandle h) {
    {
        std::lock_guard lk(g_theme_mu);
        if (!g_themes.erase(h)) return KILN_ERR_NOT_FOUND;
    }
    stale();
    return KILN_OK;
}

KilnResult look_set(const KilnUiLook* in) {
    if (!in || in->size < sizeof(KilnUiLook)) return KILN_ERR_INVALID_ARGUMENT;
    LookReq r;
    r.look = in->look <= static_cast<uint32_t>(KILN_UI_LOOK_THEME) ? in->look : static_cast<uint32_t>(KILN_UI_LOOK_GAME);
    r.theme = r.look == KILN_UI_LOOK_THEME ? in->theme : 0;
    r.background = look::clamp_background(in->background);
    r.opacity = look::clamp_opacity(in->opacity);
    r.scale = look::clamp_scale(in->scale);
    {
        std::lock_guard lk(g_look_mu);
        g_look = r;
    }
    g_user_scale.store(r.scale, std::memory_order_relaxed);
    stale();
    return KILN_OK;
}

// The palette of a look request (any thread).
look::Palette palette_of(const LookReq& r, const look::Palette& gp, uint32_t* look_out, KilnHandle* theme_out) {
    *look_out = r.look;
    *theme_out = r.theme;
    if (r.look == KILN_UI_LOOK_KILN) return look::ember();
    if (r.look == KILN_UI_LOOK_THEME) {
        if (auto t = theme_of(r.theme)) {
            const look::Palette base = t->def.base == KILN_UI_STYLE_KILN ? look::ember() : gp;
            look::Palette p = look::over(base, t->def.roles, t->def.role_mask);
            if (t->def.background >= 0.0f) p[KILN_UI_ROLE_WINDOW_BG] = look::with_alpha_f(p[KILN_UI_ROLE_WINDOW_BG], std::clamp(t->def.background, 0.0f, 1.0f));
            return p;
        }
        *look_out = KILN_UI_LOOK_GAME;
        *theme_out = 0;
    }
    look::Palette p = gp;
    if (r.look == KILN_UI_LOOK_TRANSLUCENT) {
        ImGuiStyle st;
        set_background(st, p, r.background, look::kGameWindowAlpha);
    }
    return p;
}

KilnResult look_info(KilnUiLookInfo* out) {
    if (!out || out->size < sizeof(KilnUiLookInfo)) return KILN_ERR_INVALID_ARGUMENT;
    look::Palette gp;
    const LookReq r = look_req(&gp);
    uint32_t lk = 0;
    KilnHandle th = 0;
    look::Palette p = palette_of(r, gp, &lk, &th);
    if (in_frame() && g_styles) p = g_stack.empty() ? g_player.pal : g_stack.back().pal;  // the look pushed now
    std::memset(out, 0, sizeof *out);
    out->size = sizeof *out;
    out->look = lk;
    out->theme = th;
    out->background = r.background;
    out->opacity = r.opacity;
    out->scale = r.scale;
    out->game_colors = g_colors_state.load() == 3 ? 1 : 0;
    out->game_font = g_font_state.load() == 3 ? 1 : 0;
    // the glyphs of the language Kiln's texts (else the game's) need: a CJK one is its group's, a Cyrillic one the default
    // group's fonts (READY only when they cover Cyrillic)
    const uint32_t need = g_glyph_need.load();
    const int gs = fontgroup::cjk(need) ? (g_group_lang.load() == need ? g_group_state.load() : 1) : g_extra_state.load();
    const bool have = fontgroup::cjk(need) || g_extra_cyrillic.load();
    out->glyphs = static_cast<uint8_t>(need == 0 ? KILN_UI_GLYPHS_LATIN
                                       : gs == 3 ? (have ? KILN_UI_GLYPHS_READY : KILN_UI_GLYPHS_FAILED)
                                       : gs == 4 ? KILN_UI_GLYPHS_FAILED
                                                 : KILN_UI_GLYPHS_LOADING);
    out->glyph_language = static_cast<uint8_t>(need);
    for (int i = 0; i < look::kRoles; ++i) out->roles[i] = p[i];
    out->role_count = look::kRoles;
    return KILN_OK;
}

KilnHandle font_load(KilnOwner owner, const void* data, size_t len) {
    std::lock_guard lk(g_font_mu);
    size_t bytes = 0;
    for (const auto& f : g_mod_fonts) bytes += f->bytes.size();
    const auto r = fonts::admit(data, len, g_mod_fonts.size(), bytes);
    if (r != fonts::kOk) {
        logf(KILN_LOG_WARN, "ui: a mod font (%zu bytes) was refused: %s", len, fonts::refusal_text(r));
        return 0;
    }
    auto f = std::make_shared<ModFont>();
    f->handle = next_handle();
    f->owner = owner;
    f->bytes.assign(static_cast<const uint8_t*>(data), static_cast<const uint8_t*>(data) + len);
    g_mod_fonts.push_back(f);
    return f->handle;
}

void font_free(KilnHandle h) {
    std::lock_guard lk(g_font_mu);
    for (auto& f : g_mod_fonts)
        if (f->handle == h) f->dead = true;
}

void style_frame_begin() {
    ImGuiIO& io = ImGui::GetIO();
    look::Palette unused;
    const LookReq req = look_req(&unused);
    if (game_based(req)) ensure_game_data();
    if (g_colors_state.load() == 2) {
        {
            std::lock_guard lk(g_look_mu);
            g_game_pal = g_read_pal;
        }
        g_colors_state = 3;
        stale();
        logf(KILN_LOG_INFO, "ui: the game's UI colours loaded (%s)", g_colors_note.c_str());
    }
    if (g_font_state.load() == 2) {
        ImFontConfig cfg;
        cfg.FontDataOwnedByAtlas = false;
        std::snprintf(cfg.Name, sizeof cfg.Name, "Enshrouded UI (game data)");
        g_game_font = io.Fonts->AddFontFromMemoryTTF(g_font_bytes.data(), static_cast<int>(g_font_bytes.size()), 17.0f, &cfg);
        if (g_game_font && !g_bold_bytes.empty()) {
            ImFontConfig bold;
            bold.FontDataOwnedByAtlas = false;
            std::snprintf(bold.Name, sizeof bold.Name, "Enshrouded UI bold (game data)");
            g_game_bold = io.Fonts->AddFontFromMemoryTTF(g_bold_bytes.data(), static_cast<int>(g_bold_bytes.size()), 17.0f, &bold);
        }
        g_font_state = g_game_font ? 3 : 4;
        stale();
        logf(g_game_font ? KILN_LOG_INFO : KILN_LOG_WARN, "ui: the game's UI font %s (%s)", g_game_font ? "loaded" : "rejected by ImGui",
             g_font_note.c_str());
    }
    if (update_mod_fonts()) stale();
    if (update_default_fonts()) stale();
    if (update_group_fonts()) stale();
    rebuild_if_stale();
    ImGui::GetStyle() = g_player.st;  // windows that push nothing: the player's look
    // images: textures for new ones (at most img::kUploadsPerFrame a frame: the backend submits and waits for each), a
    // texture the backend uploaded without a descriptor set marked failed (never drawn), freed ones dropped
    std::lock_guard lk(g_img_mu);
    img::upload_some(g_images.begin(), g_images.end(), [](const auto& p) { return !p.second->registered && !p.second->dead; }, [](auto& p) {
        Image* img = p.second;
        img->tex = IM_NEW(ImTextureData)();
        img->tex->Create(ImTextureFormat_RGBA32, img->w, img->h);
        std::memcpy(img->tex->GetPixels(), img->rgba.data(), img->rgba.size());
        img->tex->UseColors = true;
        img->tex->SetStatus(ImTextureStatus_WantCreate);
        ImGui::RegisterUserTexture(img->tex);
        img->registered = true;
        img->rgba.clear();
        img->rgba.shrink_to_fit();
    });
    static bool s_failed_logged = false;
    for (auto& [h, img] : g_images)
        if (img->tex && !img->failed && img->tex->Status == ImTextureStatus_OK && img->tex->TexID == ImTextureID_Invalid) {
            img->failed = true;
            if (!s_failed_logged) {
                s_failed_logged = true;
                logf(KILN_LOG_WARN, "ui: an overlay image got no descriptor set from the Vulkan pool (%u sets): it is not drawn "
                                    "(image state failed; its owner frees it)", img::kDescriptorPoolSize);
            }
        }
    // freed images: the backend destroys a texture marked WantDestroy at its next update and marks it Destroyed;
    // then it leaves ImGui's list and its memory goes
    std::vector<Image*> keep;
    for (Image* img : g_img_dead) {
        if (!img->tex) {
            delete img;
            continue;
        }
        if (img->tex->Status == ImTextureStatus_Destroyed) {
            ImGui::UnregisterUserTexture(img->tex);
            IM_DELETE(img->tex);
            delete img;
            continue;
        }
        if (img->tex->Status != ImTextureStatus_WantDestroy) {
            img->tex->DestroyPixels();  // else SetStatus(Destroyed) would ask for it to be created again
            img->tex->SetStatus(ImTextureStatus_WantDestroy);
        }
        keep.push_back(img);
    }
    g_img_dead.swap(keep);
}

// Admission: every image takes a descriptor set once uploaded; past img::kMaxImages (live and freed-not-yet-destroyed) a
// load is refused (0), logged once, so the pool never runs out (image_budget.h).
static bool image_room_locked() {
    if (img::can_add(g_images.size(), g_img_dead.size())) return true;
    static bool logged = false;
    if (!logged) {
        logged = true;
        logf(KILN_LOG_WARN, "ui: %u overlay images exist: a new one is refused until some are freed", img::kMaxImages);
    }
    return false;
}

KilnHandle image_load(KilnOwner owner, const void* png, size_t len) {
    if (!png || len < 8 || len > (64u << 20)) return 0;
    {
        std::lock_guard lk(g_img_mu);
        if (!image_room_locked()) return 0;
    }
    int w = 0, h = 0, n = 0;
    stbi_uc* px = stbi_load_from_memory(static_cast<const stbi_uc*>(png), static_cast<int>(len), &w, &h, &n, 4);
    if (!px) return 0;
    if (w <= 0 || h <= 0 || w > 4096 || h > 4096) {
        stbi_image_free(px);
        return 0;
    }
    auto* img = new Image{next_handle(), owner, nullptr, {}, w, h, false, false};
    img->rgba.assign(px, px + static_cast<size_t>(w) * h * 4);
    stbi_image_free(px);
    img->opaque = img::opaque_bounds(img->rgba.data(), w, h);
    std::lock_guard lk(g_img_mu);
    if (!image_room_locked()) {
        delete img;
        return 0;
    }
    g_images[img->handle] = img;
    return img->handle;
}

// The same image from pixels (kiln.assets' texture_rgba: the game's item icons, Kiln.Ui's Ui.ItemImage).
KilnHandle image_load_rgba(KilnOwner owner, const uint8_t* rgba, uint32_t w, uint32_t h) {
    if (!rgba || w == 0 || h == 0 || w > 4096 || h > 4096) return 0;
    auto* img = new Image{next_handle(), owner, nullptr, {}, static_cast<int>(w), static_cast<int>(h), false, false};
    img->rgba.assign(rgba, rgba + static_cast<size_t>(w) * h * 4);
    img->opaque = img::opaque_bounds(img->rgba.data(), img->w, img->h);  // once, on the loading thread
    std::lock_guard lk(g_img_mu);
    if (!image_room_locked()) {
        delete img;
        return 0;
    }
    g_images[img->handle] = img;
    return img->handle;
}

namespace {
img::State state_locked(const Image* i) {
    const bool created = i->tex != nullptr;
    return img::state_of(i->failed, created, created && i->tex->Status == ImTextureStatus_OK,
                         created && i->tex->TexID != ImTextureID_Invalid);
}

// The texture to draw for h: only an uploaded one with its descriptor set (else ImGui would bind a null set).
ImTextureData* drawable(KilnHandle h, float* iw, float* ih, img::Rect* opaque = nullptr) {
    std::lock_guard lk(g_img_mu);
    auto it = g_images.find(h);
    if (it == g_images.end()) return nullptr;
    *iw = static_cast<float>(it->second->w);
    *ih = static_cast<float>(it->second->h);
    if (opaque) *opaque = it->second->opaque;
    return state_locked(it->second) == img::kReady ? it->second->tex : nullptr;
}
}  // namespace

int32_t image_state(KilnHandle h) {
    std::lock_guard lk(g_img_mu);
    auto it = g_images.find(h);
    return it == g_images.end() ? img::kUnknown : state_locked(it->second);
}

bool image_size(KilnHandle h, uint32_t* w, uint32_t* hgt) {
    std::lock_guard lk(g_img_mu);
    auto it = g_images.find(h);
    if (it == g_images.end()) return false;
    if (w) *w = static_cast<uint32_t>(it->second->w);
    if (hgt) *hgt = static_cast<uint32_t>(it->second->h);
    return true;
}

void image_draw(KilnHandle h, float w, float hgt) {
    float iw = 0, ih = 0;
    ImTextureData* tex = drawable(h, &iw, &ih);
    if (iw <= 0) return;  // no such image
    const ImVec2 size(w > 0 ? w : iw, hgt > 0 ? hgt : ih);
    if (!tex) {
        ImGui::Dummy(size);  // not uploaded yet, or without a descriptor set: its place, nothing drawn
        return;
    }
    ImGui::Image(tex->GetTexRef(), size);
}

bool image_button(const char* id, KilnHandle h, float w, float hgt) {
    float iw = 0, ih = 0;
    ImTextureData* tex = drawable(h, &iw, &ih);
    if (iw <= 0) return false;
    const ImVec2 size(w > 0 ? w : iw, hgt > 0 ? hgt : ih);
    if (!tex) return ImGui::InvisibleButton(id, size);  // the button's place and clicks, nothing drawn
    return ImGui::ImageButton(id, tex->GetTexRef(), size);
}

// image_ex (0.2.0-dev.29.16): part of an image (uv), or its opaque bounds (TRIM), optionally fitted in the box keeping its
// aspect ratio (FIT); id: a button. The box is taken whatever is drawn; inside a frame.
bool image_draw_ex(const char* id, KilnHandle h, uint32_t flags, float w, float hgt, float u0, float v0, float u1, float v1) {
    float iw = 0, ih = 0;
    img::Rect opaque;
    ImTextureData* tex = drawable(h, &iw, &ih, &opaque);
    if (iw <= 0) return false;  // no such image
    img::Uv uv;
    if (flags & KILN_UI_IMAGE_DRAW_TRIM) uv = img::uv_of(opaque, static_cast<int>(iw), static_cast<int>(ih));
    else if (u0 != 0 || v0 != 0 || u1 != 0 || v1 != 0) uv = {u0, v0, u1, v1};
    const float sw = std::fabs(uv.u1 - uv.u0) * iw, sh = std::fabs(uv.v1 - uv.v0) * ih;  // the source in pixels
    const ImVec2 box(w > 0 ? w : sw, hgt > 0 ? hgt : sh);
    const img::Placed at = (flags & KILN_UI_IMAGE_DRAW_FIT) ? img::fit(box.x, box.y, sw, sh) : img::Placed{0, 0, box.x, box.y};
    if (id) {
        const ImVec2 pad = ImGui::GetStyle().FramePadding;
        if (!tex) return ImGui::InvisibleButton(id, ImVec2(box.x + 2 * pad.x, box.y + 2 * pad.y));  // its place and clicks
        // image_button's frame around the box; the image fitted inside it (the extra space joins the padding)
        ImGui::PushStyleVar(ImGuiStyleVar_FramePadding, ImVec2(pad.x + at.x, pad.y + at.y));
        const bool clicked = ImGui::ImageButton(id, tex->GetTexRef(), ImVec2(at.w, at.h), ImVec2(uv.u0, uv.v0), ImVec2(uv.u1, uv.v1));
        ImGui::PopStyleVar();
        return clicked;
    }
    const ImVec2 p = ImGui::GetCursorScreenPos();
    ImGui::Dummy(box);  // the box is the item (hover, tooltips, layout), whatever part of it the image covers
    if (tex && ImGui::IsItemVisible())
        ImGui::GetWindowDrawList()->AddImage(tex->GetTexRef(), ImVec2(p.x + at.x, p.y + at.y), ImVec2(p.x + at.x + at.w, p.y + at.y + at.h),
                                             ImVec2(uv.u0, uv.v0), ImVec2(uv.u1, uv.v1), ImGui::GetColorU32(IM_COL32_WHITE));
    return false;
}

bool image_opaque_rect(KilnHandle h, uint32_t* x, uint32_t* y, uint32_t* w, uint32_t* hgt) {
    std::lock_guard lk(g_img_mu);
    auto it = g_images.find(h);
    if (it == g_images.end()) return false;
    const img::Rect& r = it->second->opaque;
    if (x) *x = static_cast<uint32_t>(r.x0);
    if (y) *y = static_cast<uint32_t>(r.y0);
    if (w) *w = static_cast<uint32_t>(r.w());
    if (hgt) *hgt = static_cast<uint32_t>(r.h());
    return true;
}

void image_free(KilnHandle h) {
    std::lock_guard lk(g_img_mu);
    auto it = g_images.find(h);
    if (it == g_images.end()) return;
    it->second->dead = true;
    g_img_dead.push_back(it->second);
    g_images.erase(it);
}

// For kiln.render (render.cpp): the texture of an image once it exists (render thread, inside a frame).
bool image_texture(KilnHandle h, ImTextureRef* out) {
    float iw = 0, ih = 0;
    ImTextureData* tex = drawable(h, &iw, &ih);
    if (!tex) return false;
    *out = tex->GetTexRef();
    return true;
}

}  // namespace kui
