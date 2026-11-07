// SPDX-License-Identifier: MIT
// kiln.ui: looks for Kiln's ImGui windows, the game's UI font and PNG images.
//  - KILN_UI_STYLE_KILN: Kiln's ember theme (branding/palette.json).
//  - KILN_UI_STYLE_GAME: the game's look: its menu colours (keen::FbUiBundle: menu.tileBackgroundColor, tab highlight,
//    highlight outline, text row highlight, label colours; read from the game's data, docs/re/game-ui.md "Fonts and
//    colours") and its own UI font. The font is never shipped: kiln.ui reads the game's keen::UiFontResource through
//    kiln.assets at run time (the default language group's regular TTF, a content blob) and gives the bytes to ImGui.
//  - Images: PNG -> RGBA texture registered with ImGui (ImGui::RegisterUserTexture; the Vulkan backend uploads it at the
//    next frame).
#include <windows.h>

#include <atomic>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

#include "imgui.h"
#include "imgui_internal.h"
#include "services/kiln_assets.h"
#include "ui.h"

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

float g_scale = 1.0f;
ImGuiStyle g_kiln_style, g_game_style;
bool g_styles = false;

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

void make_game(ImGuiStyle& st, float scale) {
    st = ImGuiStyle();
    ImGui::StyleColorsDark(&st);
    // keen::FbUiBundle menu colours (26404966): tileBackgroundColor #363246 a 169, tabHighlightColor #caac54,
    // tileHighlightOutlineColor #f5c558, textTabRowBgHighlightColor #896828, tabLabelIdleColor #c0cdd5 a 171,
    // basicWidgetColor #131313 a 90, attention #ffe06b, warning #f26e6e. Square-ish frames like the game's tiles.
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
    ImVec4* c = st.Colors;
    c[ImGuiCol_Text] = hex(0xeeeae2);
    c[ImGuiCol_TextDisabled] = hex(0xc0cdd5, 0.67f);
    c[ImGuiCol_WindowBg] = hex(0x1a1822, 0.90f);
    c[ImGuiCol_ChildBg] = hex(0x363246, 0.45f);
    c[ImGuiCol_PopupBg] = hex(0x1a1822, 0.96f);
    c[ImGuiCol_Border] = hex(0xcaac54, 0.45f);
    c[ImGuiCol_FrameBg] = hex(0x131313, 0.55f);
    c[ImGuiCol_FrameBgHovered] = hex(0x363246, 0.80f);
    c[ImGuiCol_FrameBgActive] = hex(0x896828, 0.70f);
    c[ImGuiCol_TitleBg] = hex(0x1a1822, 0.95f);
    c[ImGuiCol_TitleBgActive] = hex(0x26222f, 0.98f);
    c[ImGuiCol_TitleBgCollapsed] = hex(0x1a1822, 0.80f);
    c[ImGuiCol_ScrollbarBg] = hex(0x131313, 0.35f);
    c[ImGuiCol_ScrollbarGrab] = hex(0xc0cdd5, 0.35f);
    c[ImGuiCol_ScrollbarGrabHovered] = hex(0xcaac54, 0.75f);
    c[ImGuiCol_ScrollbarGrabActive] = hex(0xf5c558);
    c[ImGuiCol_CheckMark] = hex(0xf5c558);
    c[ImGuiCol_SliderGrab] = hex(0xcaac54);
    c[ImGuiCol_SliderGrabActive] = hex(0xf5c558);
    c[ImGuiCol_Button] = hex(0x363246, 0.85f);
    c[ImGuiCol_ButtonHovered] = hex(0x896828, 0.90f);
    c[ImGuiCol_ButtonActive] = hex(0xcaac54);
    c[ImGuiCol_Header] = hex(0x896828, 0.55f);
    c[ImGuiCol_HeaderHovered] = hex(0x896828, 0.85f);
    c[ImGuiCol_HeaderActive] = hex(0xcaac54, 0.90f);
    c[ImGuiCol_Separator] = hex(0xcaac54, 0.35f);
    c[ImGuiCol_SeparatorHovered] = hex(0xcaac54, 0.75f);
    c[ImGuiCol_SeparatorActive] = hex(0xf5c558);
    c[ImGuiCol_ResizeGrip] = hex(0xcaac54, 0.25f);
    c[ImGuiCol_ResizeGripHovered] = hex(0xcaac54, 0.6f);
    c[ImGuiCol_ResizeGripActive] = hex(0xf5c558);
    c[ImGuiCol_Tab] = hex(0x131313, 0.60f);
    c[ImGuiCol_TabHovered] = hex(0x896828, 0.85f);
    c[ImGuiCol_TabSelected] = hex(0x363246, 0.95f);
    c[ImGuiCol_TabSelectedOverline] = hex(0xcaac54);
    c[ImGuiCol_TabDimmed] = hex(0x131313, 0.5f);
    c[ImGuiCol_TabDimmedSelected] = hex(0x363246, 0.7f);
    c[ImGuiCol_PlotHistogram] = hex(0xcaac54);
    c[ImGuiCol_PlotHistogramHovered] = hex(0xf5c558);
    c[ImGuiCol_TableHeaderBg] = hex(0x363246, 0.8f);
    c[ImGuiCol_TableBorderStrong] = hex(0xcaac54, 0.35f);
    c[ImGuiCol_TableBorderLight] = hex(0xc0cdd5, 0.15f);
    c[ImGuiCol_TableRowBgAlt] = hex(0xffffff, 0.03f);
    c[ImGuiCol_TextSelectedBg] = hex(0xcaac54, 0.35f);
    c[ImGuiCol_NavCursor] = hex(0xf5c558);
    st.ScaleAllSizes(scale);
    st.FontScaleDpi = scale;
}

// ---- style stack (render thread, inside a frame) ----
struct Pushed {
    ImGuiStyle saved;
    bool font;
};
std::vector<Pushed> g_stack;

// ---- the game's font ----
std::atomic<int> g_font_state{0};  // 0 not asked, 1 loading, 2 bytes ready, 3 added, 4 failed
std::vector<uint8_t> g_font_bytes;  // kept alive for the atlas (FontDataOwnedByAtlas = false)
ImFont* g_game_font = nullptr;
std::string g_font_note;

struct BlobArray {
    int32_t off;
    uint32_t count;
};

// The default language group's regular font of the game's keen::UiFontResource (docs/re/game-ui.md).
void load_game_font() {
    const auto* a = static_cast<const KilnAssetsApi*>(g_api->service(KilnStr{KILN_ASSETS_SERVICE, sizeof(KILN_ASSETS_SERVICE) - 1}, 1));
    auto fail = [](const char* why) {
        g_font_note = why;
        g_font_state = 4;
        logf(KILN_LOG_WARN, "ui: the game's UI font is not available (%s): game-style windows use the system font", why);
    };
    if (!a) return fail("kiln.assets unavailable");
    constexpr uint32_t kUiFontResource = 0x878aa5adu;  // FNV-1a-32 of "keen::UiFontResource"
    KilnAssetKey keys[8]{};
    size_t n = 0;
    if (a->resource_list(kUiFontResource, keys, 8, &n) != KILN_OK || n == 0) return fail("no keen::UiFontResource");
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
    // the default group (isDefaultLanguageGroup +0x14), else the first; its first glyph set without variants
    uint32_t font_index = 0;
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
        const size_t sbase = pick + 8 + static_cast<size_t>(sets.off);
        for (uint32_t i = 0; i < sets.count && i < 64; ++i) {
            const size_t s = sbase + i * 20ull;
            if (s + 20 > res.size()) break;
            if (res[s + 0x10] == 0) {  // fontVariantMask: none (regular)
                std::memcpy(&font_index, res.data() + s, 4);
                break;
            }
        }
    }
    if (font_index >= fonts.count) font_index = 0;
    const size_t f = 0 + static_cast<size_t>(fonts.off) + font_index * 32ull;
    if (f + 16 > res.size()) return fail("font record out of range");
    KilnContentHash h{};
    std::memcpy(&h, res.data() + f, sizeof h);
    if (h.size < 1024 || h.size > (32u << 20)) return fail("font blob size implausible");
    std::vector<uint8_t> ttf(h.size);
    size_t got = 0;
    if (a->blob_read(&h, ttf.data(), ttf.size(), &got) != KILN_OK || got != h.size) return fail("font blob unreadable");
    g_font_bytes.swap(ttf);
    char b[96];
    std::snprintf(b, sizeof b, "font %u of %u, %u bytes", font_index, fonts.count, h.size);
    g_font_note = b;
    g_font_state = 2;
}

void ensure_game_font() {
    int expected = 0;
    if (g_font_state.compare_exchange_strong(expected, 1)) std::thread([] { load_game_font(); }).detach();
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
};
std::mutex g_img_mu;
std::unordered_map<KilnHandle, Image*> g_images;
std::vector<Image*> g_img_dead;

}  // namespace

float style_scale() { return g_scale; }

void style_apply_base(float scale) {
    g_scale = scale;
    make_kiln(g_kiln_style, scale);
    make_game(g_game_style, scale);
    g_styles = true;
    ImGui::GetStyle() = g_kiln_style;
}

void style_push(uint32_t style) {
    if (!g_styles) return;
    Pushed p{ImGui::GetStyle(), false};
    ImGui::GetStyle() = style == KILN_UI_STYLE_GAME ? g_game_style : g_kiln_style;
    if (style == KILN_UI_STYLE_GAME) {
        ensure_game_font();
        if (g_game_font) {
            ImGui::PushFont(g_game_font, 0.0f);
            p.font = true;
        }
    }
    g_stack.push_back(p);
}

void style_pop() {
    if (g_stack.empty()) return;
    Pushed p = g_stack.back();
    g_stack.pop_back();
    if (p.font) ImGui::PopFont();
    ImGui::GetStyle() = p.saved;
}

void style_frame_end() {
    while (!g_stack.empty()) style_pop();  // a callback that forgot pop_style
}

void style_frame_begin() {
    ImGuiIO& io = ImGui::GetIO();
    if (g_font_state.load() == 2) {
        ImFontConfig cfg;
        cfg.FontDataOwnedByAtlas = false;
        std::snprintf(cfg.Name, sizeof cfg.Name, "Enshrouded UI (game data)");
        g_game_font = io.Fonts->AddFontFromMemoryTTF(g_font_bytes.data(), static_cast<int>(g_font_bytes.size()), 17.0f, &cfg);
        g_font_state = g_game_font ? 3 : 4;
        logf(g_game_font ? KILN_LOG_INFO : KILN_LOG_WARN, "ui: the game's UI font %s (%s)", g_game_font ? "loaded" : "rejected by ImGui",
             g_font_note.c_str());
    }
    // images: create textures for new ones, drop freed ones
    std::lock_guard lk(g_img_mu);
    for (auto& [h, img] : g_images) {
        if (img->registered || img->dead) continue;
        img->tex = IM_NEW(ImTextureData)();
        img->tex->Create(ImTextureFormat_RGBA32, img->w, img->h);
        std::memcpy(img->tex->GetPixels(), img->rgba.data(), img->rgba.size());
        img->tex->UseColors = true;
        img->tex->SetStatus(ImTextureStatus_WantCreate);
        ImGui::RegisterUserTexture(img->tex);
        img->registered = true;
        img->rgba.clear();
        img->rgba.shrink_to_fit();
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

KilnHandle image_load(KilnOwner owner, const void* png, size_t len) {
    if (!png || len < 8 || len > (64u << 20)) return 0;
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
    std::lock_guard lk(g_img_mu);
    g_images[img->handle] = img;
    return img->handle;
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
    ImTextureData* tex = nullptr;
    float iw = 0, ih = 0;
    {
        std::lock_guard lk(g_img_mu);
        auto it = g_images.find(h);
        if (it == g_images.end() || !it->second->tex) return;
        tex = it->second->tex;
        iw = static_cast<float>(it->second->w);
        ih = static_cast<float>(it->second->h);
    }
    ImGui::Image(tex->GetTexRef(), ImVec2(w > 0 ? w : iw, hgt > 0 ? hgt : ih));
}

bool image_button(const char* id, KilnHandle h, float w, float hgt) {
    ImTextureData* tex = nullptr;
    float iw = 0, ih = 0;
    {
        std::lock_guard lk(g_img_mu);
        auto it = g_images.find(h);
        if (it == g_images.end() || !it->second->tex) return false;
        tex = it->second->tex;
        iw = static_cast<float>(it->second->w);
        ih = static_cast<float>(it->second->h);
    }
    return ImGui::ImageButton(id, tex->GetTexRef(), ImVec2(w > 0 ? w : iw, hgt > 0 ? hgt : ih));
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
    std::lock_guard lk(g_img_mu);
    auto it = g_images.find(h);
    if (it == g_images.end() || !it->second->tex) return false;
    *out = it->second->tex->GetTexRef();
    return true;
}

}  // namespace kui
