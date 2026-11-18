// SPDX-License-Identifier: MIT
// Runs the real looks (style.cpp) on a headless Dear ImGui context under ASan/UBSan, with a fake kiln.types and kiln.assets
// (synthetic types and colours, no game data): the game's colours read through reflection offsets (and the neutral
// stand-in before), the player's look (game, translucent, ember, a theme; opacity, scale, clamps), themes (roles, single
// colours, spacing, rounding, background; refusals; push/pop and the end of the frame; a freed theme), look_info inside and
// outside a frame, immediate drawing's alpha, mod fonts (refusals, the budget, joining and leaving the atlas), and the pure
// rules of look_palette.h and font_budget.h; overlay images' opaque bounds and trimmed, fitted and part-of-image draws.
#include <cassert>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <string>
#include <thread>
#include <vector>

#include "../style.cpp"
#include "draw_slots.h"

#undef NDEBUG
namespace kui {
const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
static thread_local bool t_frame = false;
bool in_frame() { return t_frame; }
uint64_t now_ms() {
    return static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count());
}
static std::vector<std::string> g_logs;
static std::mutex g_log_mu;
void logf(int, const char* fmt, ...) {
    char b[512];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(b, sizeof b, fmt, ap);
    va_end(ap);
    std::lock_guard lk(g_log_mu);
    g_logs.emplace_back(b);
}
KilnHandle next_handle() {
    static std::atomic<KilnHandle> h{100};
    return h++;
}
KilnResult game_screen(KilnUiScreen*) { return KILN_ERR_UNAVAILABLE; }  // no game: the game's language is not known
}  // namespace kui

namespace look = kui::look;
namespace fonts = kui::fonts;

static int g_fail = 0, g_checks = 0;
#define CHECK(cond)                                                     \
    do {                                                                \
        ++g_checks;                                                     \
        if (!(cond)) {                                                  \
            std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond); \
            ++g_fail;                                                   \
        }                                                               \
    } while (0)

static bool near(float a, float b, float eps = 0.004f) { return std::fabs(a - b) <= eps; }
static bool same(const ImVec4& v, uint32_t rgba) {
    return near(v.x, look::ch(rgba, 24) / 255.f) && near(v.y, look::ch(rgba, 16) / 255.f) && near(v.z, look::ch(rgba, 8) / 255.f);
}

// ---- a fake reflection: a few synthetic structs ----
struct FakeField {
    const char* name;
    const struct FakeType* type;
    uint64_t offset;
};
struct FakeType {
    const char* name;
    uint32_t size;
    uint32_t hash;
    uint8_t kind;
    const FakeType* inner;
    std::vector<FakeField> fields;
};
static FakeType tColor{"keen::Color", 4, 1, KILN_TYPE_U32, nullptr, {}};
static FakeType tFloat{"keen::float32", 4, 2, KILN_TYPE_F32, nullptr, {}};
static FakeType tUiColor{"keen::UiColor", 8, 3, KILN_TYPE_STRUCT, nullptr, {{"value", &tColor, 0}, {"intensity", &tFloat, 4}}};
static FakeType tMenu{"keen::FbUiMenu", 0x60, 4, KILN_TYPE_STRUCT, nullptr,
                      {{"menuTiles", &tFloat, 0x0},
                       {"tabLabelHighlightColor", &tUiColor, 0x10},
                       {"tabLabelIdleColor", &tUiColor, 0x18},
                       {"tileBackgroundColor", &tUiColor, 0x20},
                       {"basicWidgetColor", &tUiColor, 0x28},
                       {"tabHighlightColor", &tUiColor, 0x30},
                       {"tileHighlightOutlineColor", &tUiColor, 0x38},
                       {"textTabRowBgHighlightColor", &tUiColor, 0x40}}};
// no checkmarkGreen: the stand-in's OK stays
static FakeType tGlobals{"keen::FbUiGlobalColors", 0x20, 5, KILN_TYPE_STRUCT, nullptr,
                         {{"attentionColor", &tUiColor, 0x0}, {"warningColor", &tUiColor, 0x8}}};
static FakeType tUiBundle{"keen::UiBundle", 0x10, 6, KILN_TYPE_STRUCT, nullptr, {{"characterStyles", &tFloat, 0}}};
static const uint32_t kBundleHash = look::fnv1a("keen::FbUiBundle");
static FakeType tBundle{"keen::FbUiBundle", 0x400, kBundleHash, KILN_TYPE_STRUCT, &tUiBundle,
                        {{"logos", &tFloat, 0x10}, {"menu", &tMenu, 0x100}, {"globalColors", &tGlobals, 0x300}}};

const KilnType* KILN_CALL ty_by_name(KilnStr n) {
    const std::string s(n.ptr, n.len);
    for (const FakeType* t : {&tBundle, &tMenu, &tGlobals, &tUiColor, &tColor, &tUiBundle})
        if (s == t->name) return reinterpret_cast<const KilnType*>(t);
    return nullptr;
}
KilnResult KILN_CALL ty_info(const KilnType* type, KilnTypeInfo* out) {
    const auto* t = reinterpret_cast<const FakeType*>(type);
    if (!t || !out) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    out->name = KilnStr{t->name, std::strlen(t->name)};
    out->qualified_name = out->name;
    out->type_size = t->size;
    out->kind = t->kind;
    out->hash = t->hash;
    out->field_count = static_cast<uint32_t>(t->fields.size());
    out->inner = reinterpret_cast<const KilnType*>(t->inner);
    return KILN_OK;
}
KilnResult KILN_CALL ty_field(const KilnType* type, uint32_t i, KilnTypeField* out) {
    const auto* t = reinterpret_cast<const FakeType*>(type);
    if (!t || !out || i >= t->fields.size()) return KILN_ERR_NOT_FOUND;
    out->name = KilnStr{t->fields[i].name, std::strlen(t->fields[i].name)};
    out->type = reinterpret_cast<const KilnType*>(t->fields[i].type);
    out->offset = t->fields[i].offset;
    return KILN_OK;
}

// ---- a fake kiln.assets: one bundle resource with synthetic colours (keen::Color: A B G R from the high byte) ----
static std::vector<uint8_t> g_bundle(0x500, 0);
static uint32_t keen_of(uint32_t rgba) {
    return static_cast<uint32_t>(look::ch(rgba, 0)) << 24 | look::ch(rgba, 8) << 16 | look::ch(rgba, 16) << 8 | look::ch(rgba, 24);
}
static void put_color(size_t at, uint32_t rgba) {
    const uint32_t v = keen_of(rgba);
    std::memcpy(&g_bundle[at], &v, 4);
}
static std::atomic<int> g_lists{0};
KilnResult KILN_CALL as_list(uint32_t type, KilnAssetKey* out, size_t cap, size_t* n) {
    // the first ask comes before the game listed its UI bundle (a frame drawn early): kiln.ui asks again 5 s later
    const bool b = type == kBundleHash && g_lists.fetch_add(1) > 0;
    if (n) *n = b ? 1 : 0;
    if (b && out && cap) std::memset(out, 0, sizeof *out);
    return KILN_OK;
}
KilnResult KILN_CALL as_read(const KilnAssetKey*, void* buf, size_t cap, size_t* len) {
    if (len) *len = g_bundle.size();
    if (buf && cap >= g_bundle.size()) std::memcpy(buf, g_bundle.data(), g_bundle.size());
    return KILN_OK;
}

static KilnTypesApi g_types{};
static KilnAssetsApi g_assets{};
static KilnApi g_kapi{};
const void* KILN_CALL k_service(KilnStr name, uint32_t) {
    const std::string s(name.ptr, name.len);
    if (s == KILN_TYPES_SERVICE) return &g_types;
    if (s == KILN_ASSETS_SERVICE) return &g_assets;
    return nullptr;
}

// ---- frames ----
static void textures(ImDrawData* dd) {
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
template <class F>
static void frame(F inside) {
    kui::style_frame_begin();
    ImGui::NewFrame();
    kui::t_frame = true;
    ImGui::Begin("test");  // a window drawn in the player's look
    ImGui::Text("look");
    ImGui::End();
    inside();  // between windows, as Kiln.Ui pushes looks: what a callback leaves on is taken off below
    kui_draw::v3_frame_end();
    kui::style_frame_end();
    kui::t_frame = false;
    ImGui::Render();
    textures(ImGui::GetDrawData());
}
static void frame() {
    frame([] {});
}
static KilnUiLookInfo info() {
    KilnUiLookInfo i{};
    i.size = sizeof i;
    CHECK(kui::look_info(&i) == KILN_OK);
    return i;
}
static KilnResult set(uint32_t lk, float bg = 0.55f, float op = 1.0f, float sc = 1.0f, KilnHandle th = 0) {
    KilnUiLook l{};
    l.size = sizeof l;
    l.look = lk;
    l.theme = th;
    l.background = bg;
    l.opacity = op;
    l.scale = sc;
    return kui::look_set(&l);
}
static KilnUiThemeDef theme_def(uint32_t base) {
    KilnUiThemeDef d{};
    d.size = sizeof d;
    d.base = base;
    d.background = -1;
    d.rounding = -1;
    return d;
}

// synthetic colours (not the game's)
constexpr uint32_t kLabel = 0xfafafaffu, kIdle = 0xa0b0c080u, kTile = 0x40506090u, kWidget = 0x10203040u, kTab = 0xc0a050ffu,
                   kOutline = 0xf0d060ffu, kRow = 0x806020ffu, kAttention = 0xffe000ffu, kWarning = 0xf06060ffu;

static void pure() {
    // look_palette.h
    CHECK(look::fnv1a("keen::FbUiBundle") == 0xfef918b8u);  // the hash kiln.assets lists the bundle under
    CHECK(look::from_keen(0xA9463236u) == 0x363246A9u);
    CHECK(look::mix(0x00000000u, 0xffffffffu, 0.5f) == 0x80808080u);
    CHECK(look::scaled(0x80808080u, 0.5f) == 0x40404080u);
    uint32_t c[look::kGameColors] = {kLabel, kIdle, kTile, kWidget, kTab, kOutline, kRow, kAttention, kWarning, 0};
    bool used = true;
    look::Palette p = look::game(c, (1u << look::kRequiredGameColors) - 2, &used);  // the label missing: the stand-in
    CHECK(!used && p[KILN_UI_ROLE_TEXT] == look::neutral()[KILN_UI_ROLE_TEXT]);
    p = look::game(c, (1u << look::kGameColors) - 1 - (1u << look::kOk), &used);  // every one but the check mark
    CHECK(used);
    CHECK(p[KILN_UI_ROLE_PANEL_BG] == kTile);
    CHECK(p[KILN_UI_ROLE_TEXT_MUTED] == kIdle);
    CHECK(p[KILN_UI_ROLE_ACCENT] == kOutline);
    CHECK(p[KILN_UI_ROLE_BORDER] == kTab);
    CHECK(p[KILN_UI_ROLE_HEADER] == kRow);
    CHECK(p[KILN_UI_ROLE_ATTENTION] == kAttention);
    CHECK(p[KILN_UI_ROLE_OK] == look::neutral()[KILN_UI_ROLE_OK]);
    CHECK(look::ch(p[KILN_UI_ROLE_WINDOW_BG], 0) == 230);  // the game look's 0.90
    uint32_t roles[16] = {};
    roles[KILN_UI_ROLE_ACCENT] = 0x11223300u;
    const look::Palette o = look::over(look::ember(), roles, 1u << KILN_UI_ROLE_ACCENT);
    CHECK(o[KILN_UI_ROLE_ACCENT] == 0x112233ffu);  // the theme's R, G, B; the base's alpha
    CHECK(o[KILN_UI_ROLE_TEXT] == look::ember()[KILN_UI_ROLE_TEXT]);
    CHECK(look::clamp_opacity(0.0f) == 0.3f && look::clamp_scale(9.0f) == 2.0f && look::clamp_background(NAN) == 0.55f);
    // font_budget.h
    const uint8_t ttf[16] = {0, 1, 0, 0}, otf[16] = {'O', 'T', 'T', 'O'}, ttc[16] = {'t', 't', 'c', 'f'}, png[16] = {0x89, 'P', 'N', 'G'};
    CHECK(fonts::is_font(ttf, 16) && fonts::is_font(otf, 16) && !fonts::is_font(ttc, 16) && !fonts::is_font(png, 16));
    CHECK(!fonts::is_font(ttf, 8) && !fonts::is_font(nullptr, 100));
    CHECK(fonts::admit(ttf, 16, 0, 0) == fonts::kOk);
    CHECK(fonts::admit(png, 16, 0, 0) == fonts::kNotAFont);
    CHECK(fonts::admit(ttf, fonts::kMaxFontBytes + 1, 0, 0) == fonts::kTooLarge);
    CHECK(fonts::admit(ttf, 16, fonts::kMaxFonts, 0) == fonts::kTooMany);
    CHECK(fonts::admit(ttf, fonts::kMaxFontBytes, 2, fonts::kMaxTotalBytes - fonts::kMaxFontBytes + 1) == fonts::kTooMuch);
}

// ---- images: opaque bounds found at load, trimmed / fitted / part-of-image draws (image_ex, 0.2.0-dev.29.16) ----
struct Quad {
    ImVec2 p0, p1, uv0, uv1;
};
// The last image drawn into the current window: the quad AddImage / ImageButton just wrote (top left, bottom right).
static Quad last_quad() {
    const ImDrawList* dl = ImGui::GetWindowDrawList();
    const int n = dl->VtxBuffer.Size;
    if (n < 4) return {};
    const ImDrawVert& a = dl->VtxBuffer[n - 4];
    const ImDrawVert& c = dl->VtxBuffer[n - 2];
    return {a.pos, c.pos, a.uv, c.uv};
}
static std::vector<uint8_t> icon_px(int w, int h, int x0, int y0, int x1, int y1) {
    std::vector<uint8_t> px(static_cast<size_t>(w) * h * 4, 0);
    for (int y = y0; y < y1; ++y)
        for (int x = x0; x < x1; ++x) {
            uint8_t* p = &px[(static_cast<size_t>(y) * w + x) * 4];
            p[0] = p[1] = p[2] = p[3] = 255;
        }
    return px;
}
static void images() {
    // a 64 x 64 icon whose art is 32 x 48 at (16, 8); one with no art at all; a PNG-less 37 x 23 opaque one
    const auto art = icon_px(64, 64, 16, 8, 48, 56);
    const KilnHandle icon = kui::image_load_rgba(3, art.data(), 64, 64);
    const auto none = icon_px(20, 10, 0, 0, 0, 0);
    const KilnHandle clear = kui::image_load_rgba(3, none.data(), 20, 10);
    const auto all = icon_px(37, 23, 0, 0, 37, 23);
    const KilnHandle solid = kui::image_load_rgba(3, all.data(), 37, 23);
    CHECK(icon && clear && solid);
    uint32_t x = 9, y = 9, w = 9, h = 9;
    CHECK(kui::image_opaque_rect(icon, &x, &y, &w, &h) && x == 16 && y == 8 && w == 32 && h == 48);  // before upload: any thread
    CHECK(kui::image_opaque_rect(clear, &x, &y, &w, &h) && x == 0 && y == 0 && w == 20 && h == 10);  // nothing opaque: whole
    CHECK(kui::image_opaque_rect(solid, &x, &y, &w, &h) && x == 0 && y == 0 && w == 37 && h == 23);
    CHECK(kui::image_opaque_rect(icon, nullptr, nullptr, nullptr, nullptr));
    CHECK(!kui::image_opaque_rect(0xdead, &x, &y, &w, &h));

    // not uploaded yet: the box is taken, nothing drawn
    frame([&] {
        ImGui::SetNextWindowPos(ImVec2(0, 0));
        ImGui::SetNextWindowSize(ImVec2(600, 700));  // every draw below in view (a clipped one adds no quad)
        ImGui::Begin("images");
        const int before = ImGui::GetWindowDrawList()->VtxBuffer.Size;
        const float y0 = ImGui::GetCursorPosY();
        CHECK(!kui::image_draw_ex(nullptr, icon, KILN_UI_IMAGE_DRAW_TRIM | KILN_UI_IMAGE_DRAW_FIT, 40, 40, 0, 0, 0, 0));
        CHECK(ImGui::GetWindowDrawList()->VtxBuffer.Size == before);
        CHECK(near(ImGui::GetItemRectSize().x, 40) && near(ImGui::GetItemRectSize().y, 40));
        CHECK(ImGui::GetCursorPosY() > y0 + 39);
        ImGui::End();
    });
    frame();  // uploaded at the end of the first frame (the fake backend), ready from here
    CHECK(kui::image_state(icon) == kui::img::kReady);
    frame([&] {
        ImGui::SetNextWindowPos(ImVec2(0, 0));
        ImGui::SetNextWindowSize(ImVec2(600, 700));  // every draw below in view (a clipped one adds no quad)
        ImGui::Begin("images");
        // trimmed and fitted: the art's 32 x 48 into 40 x 40 is 26.67 x 40, centred; only the art's UVs are sampled
        ImVec2 at = ImGui::GetCursorScreenPos();
        CHECK(!kui::image_draw_ex(nullptr, icon, KILN_UI_IMAGE_DRAW_TRIM | KILN_UI_IMAGE_DRAW_FIT, 40, 40, 0.3f, 0.3f, 0.4f, 0.4f));
        Quad q = last_quad();
        CHECK(near(q.uv0.x, 0.25f) && near(q.uv0.y, 0.125f) && near(q.uv1.x, 0.75f) && near(q.uv1.y, 0.875f));
        CHECK(near(q.p0.x - at.x, 20 / 3.f, 0.01f) && near(q.p0.y - at.y, 0) && near(q.p1.x - q.p0.x, 80 / 3.f, 0.01f) &&
              near(q.p1.y - q.p0.y, 40));
        CHECK(near(ImGui::GetItemRectSize().x, 40) && near(ImGui::GetItemRectSize().y, 40));  // the whole box is the item
        // trimmed, not fitted: stretched over the box
        at = ImGui::GetCursorScreenPos();
        kui::image_draw_ex(nullptr, icon, KILN_UI_IMAGE_DRAW_TRIM, 40, 40, 0, 0, 0, 0);
        q = last_quad();
        CHECK(near(q.p0.x, at.x) && near(q.p1.x - q.p0.x, 40) && near(q.p1.y - q.p0.y, 40) && near(q.uv0.x, 0.25f));
        // trimmed without a size: the art's own pixels
        kui::image_draw_ex(nullptr, icon, KILN_UI_IMAGE_DRAW_TRIM, 0, 0, 0, 0, 0, 0);
        CHECK(near(ImGui::GetItemRectSize().x, 32) && near(ImGui::GetItemRectSize().y, 48));
        // no flags, no UVs: the whole image over the box, as image does
        kui::image_draw_ex(nullptr, icon, 0, 40, 40, 0, 0, 0, 0);
        q = last_quad();
        CHECK(near(q.uv0.x, 0) && near(q.uv0.y, 0) && near(q.uv1.x, 1) && near(q.uv1.y, 1) && near(q.p1.x - q.p0.x, 40));
        // a source rectangle: the top-left quarter, at its own size (32 x 32)
        kui::image_draw_ex(nullptr, icon, 0, 0, 0, 0, 0, 0.5f, 0.5f);
        q = last_quad();
        CHECK(near(q.uv1.x, 0.5f) && near(q.uv1.y, 0.5f) && near(q.p1.x - q.p0.x, 32) && near(q.p1.y - q.p0.y, 32));
        // flipped
        kui::image_draw_ex(nullptr, icon, 0, 16, 16, 1, 0, 0, 1);
        q = last_quad();
        CHECK(near(q.uv0.x, 1) && near(q.uv1.x, 0) && near(q.p1.x - q.p0.x, 16));
        // nothing opaque: trimming draws it whole
        kui::image_draw_ex(nullptr, clear, KILN_UI_IMAGE_DRAW_TRIM | KILN_UI_IMAGE_DRAW_FIT, 40, 40, 0, 0, 0, 0);
        q = last_quad();
        CHECK(near(q.uv0.x, 0) && near(q.uv1.x, 1) && near(q.uv1.y, 1) && near(q.p1.x - q.p0.x, 40) && near(q.p1.y - q.p0.y, 20));
        // a button: the image fitted inside the frame of a 40 x 40 image button
        const ImVec2 pad = ImGui::GetStyle().FramePadding;
        at = ImGui::GetCursorScreenPos();
        CHECK(!kui::image_draw_ex("##trimmed", icon, KILN_UI_IMAGE_DRAW_TRIM | KILN_UI_IMAGE_DRAW_FIT, 40, 40, 0, 0, 0, 0));
        q = last_quad();
        CHECK(near(ImGui::GetItemRectSize().x, 40 + 2 * pad.x) && near(ImGui::GetItemRectSize().y, 40 + 2 * pad.y));
        CHECK(near(q.uv0.x, 0.25f) && near(q.p0.x - at.x, pad.x + 20 / 3.f, 0.01f) && near(q.p1.y - q.p0.y, 40));
        CHECK(near(ImGui::GetStyle().FramePadding.x, pad.x));  // popped
        // no such image: nothing at all
        const int before = ImGui::GetWindowDrawList()->VtxBuffer.Size;
        CHECK(!kui::image_draw_ex(nullptr, 0xdead, KILN_UI_IMAGE_DRAW_TRIM, 40, 40, 0, 0, 0, 0));
        CHECK(!kui::image_draw_ex("##gone", 0xdead, 0, 40, 40, 0, 0, 0, 0));
        CHECK(ImGui::GetWindowDrawList()->VtxBuffer.Size == before);
        ImGui::End();
    });
    kui::image_free(icon);
    kui::image_free(clear);
    kui::image_free(solid);
    CHECK(!kui::image_opaque_rect(icon, &x, &y, &w, &h));
    frame();
    frame();
}

int main() {
    pure();

    put_color(0x100 + 0x10, kLabel);
    put_color(0x100 + 0x18, kIdle);
    put_color(0x100 + 0x20, kTile);
    put_color(0x100 + 0x28, kWidget);
    put_color(0x100 + 0x30, kTab);
    put_color(0x100 + 0x38, kOutline);
    put_color(0x100 + 0x40, kRow);
    put_color(0x300 + 0x0, kAttention);
    put_color(0x300 + 0x8, kWarning);
    g_types.header = {sizeof g_types, KILN_TYPES_VERSION};
    g_types.type_by_name = &ty_by_name;
    g_types.type_info = &ty_info;
    g_types.type_field = &ty_field;
    g_assets.header = {sizeof g_assets, KILN_ASSETS_VERSION};
    g_assets.resource_list = &as_list;
    g_assets.resource_read = &as_read;
    g_kapi.size = sizeof g_kapi;
    g_kapi.service = &k_service;
    kui::g_api = &g_kapi;

    IMGUI_CHECKVERSION();
    ImGui::CreateContext();
    ImGuiIO& io = ImGui::GetIO();
    io.IniFilename = nullptr;
    io.BackendFlags |= ImGuiBackendFlags_RendererHasTextures;
    io.DisplaySize = ImVec2(1280, 720);
    io.DeltaTime = 1.0f / 60;
    io.Fonts->AddFontDefault();
    kui::style_apply_base(1.0f);

    // before the read: the neutral stand-in, never ember
    KilnUiLookInfo i = info();
    CHECK(i.look == KILN_UI_LOOK_GAME && i.role_count == KILN_UI_ROLE_COUNT);
    CHECK(i.roles[KILN_UI_ROLE_ACCENT] == look::neutral()[KILN_UI_ROLE_ACCENT]);
    CHECK(same(ImGui::GetStyle().Colors[ImGuiCol_CheckMark], look::neutral()[KILN_UI_ROLE_ACCENT]));
    // the first frame asks for the game's data; the bundle is not listed yet, so the stand-in stays and kiln.ui asks again
    // 5 s later; then the colours arrive (the font does not exist here: logged, system font)
    for (int n = 0; n < 20; ++n) {
        frame();
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
    }
    CHECK(!info().game_colors && g_lists.load() == 1);
    for (int n = 0; n < 2000 && !info().game_colors; ++n) {
        frame();
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
    }
    CHECK(g_lists.load() == 2);  // asked twice in all
    i = info();
    CHECK(i.game_colors == 1 && i.game_font == 0);
    CHECK(i.roles[KILN_UI_ROLE_ACCENT] == kOutline);
    CHECK(i.roles[KILN_UI_ROLE_PANEL_BG] == kTile);
    CHECK(i.roles[KILN_UI_ROLE_TEXT] == look::with_alpha(look::scaled(kLabel, 0.933f, 0.918f, 0.886f), 0xFF));
    CHECK(i.roles[KILN_UI_ROLE_WARNING] == kWarning);
    CHECK(i.roles[KILN_UI_ROLE_OK] == look::neutral()[KILN_UI_ROLE_OK]);
    frame([] {
        // windows that push nothing: the player's look (the game's)
        CHECK(same(ImGui::GetStyle().Colors[ImGuiCol_CheckMark], kOutline));
        CHECK(same(ImGui::GetStyle().Colors[ImGuiCol_Border], kTab));
    });

    // ember
    CHECK(set(KILN_UI_LOOK_KILN) == KILN_OK);
    frame([] {
        CHECK(same(ImGui::GetStyle().Colors[ImGuiCol_WindowBg], 0x0f0d0cffu));
        CHECK(near(ImGui::GetStyle().WindowRounding, 8.0f));
        kui::style_push(KILN_UI_STYLE_GAME);  // a window that asks for the game's look still gets it
        CHECK(same(ImGui::GetStyle().Colors[ImGuiCol_CheckMark], kOutline));
        KilnUiLookInfo in{};
        in.size = sizeof in;
        kui::look_info(&in);
        CHECK(in.roles[KILN_UI_ROLE_ACCENT] == kOutline && in.look == KILN_UI_LOOK_KILN);  // the pushed look's palette
        kui::style_pop();
        CHECK(same(ImGui::GetStyle().Colors[ImGuiCol_WindowBg], 0x0f0d0cffu));
    });
    CHECK(info().roles[KILN_UI_ROLE_ACCENT] == look::ember()[KILN_UI_ROLE_ACCENT]);

    // translucent: the windows at the background opacity, pop-ups readable
    CHECK(set(KILN_UI_LOOK_TRANSLUCENT, 0.3f) == KILN_OK);
    frame([] {
        CHECK(near(ImGui::GetStyle().Colors[ImGuiCol_WindowBg].w, 0.3f));
        CHECK(ImGui::GetStyle().Colors[ImGuiCol_PopupBg].w >= look::kMinPopupAlpha - 0.001f);
        CHECK(ImGui::GetStyle().Colors[ImGuiCol_ChildBg].w < 0.45f);
        kui::style_push(KILN_UI_STYLE_PLAYER);
        CHECK(near(ImGui::GetStyle().Colors[ImGuiCol_WindowBg].w, 0.3f));
        kui::style_pop();
    });
    CHECK(look::ch(info().roles[KILN_UI_ROLE_WINDOW_BG], 0) == 77);

    // opacity, scale and their clamps
    CHECK(set(KILN_UI_LOOK_GAME, 0.55f, 0.5f, 1.25f) == KILN_OK);
    CHECK(near(kui::user_scale(), 1.25f));
    frame([] {
        CHECK(near(ImGui::GetStyle().Alpha, 0.5f));
        CHECK(kui_draw::col(0xffffff80u) >> IM_COL32_A_SHIFT == 64);  // immediate drawing follows the opacity
    });
    CHECK(set(KILN_UI_LOOK_GAME, 0.55f, 0.0f, 9.0f) == KILN_OK);
    i = info();
    CHECK(near(i.opacity, 0.3f) && near(i.scale, 2.0f));
    CHECK(set(77) == KILN_OK && info().look == KILN_UI_LOOK_GAME);  // an unknown look: the game's
    KilnUiLook small{};
    small.size = 8;
    CHECK(kui::look_set(&small) == KILN_ERR_INVALID_ARGUMENT && kui::look_set(nullptr) == KILN_ERR_INVALID_ARGUMENT);
    CHECK(set(KILN_UI_LOOK_GAME) == KILN_OK);

    // themes: refusals
    KilnHandle h = 0;
    KilnUiThemeDef bad = theme_def(7);
    CHECK(kui::theme_create(1, &bad, &h) == KILN_ERR_INVALID_ARGUMENT);
    bad = theme_def(KILN_UI_STYLE_GAME);
    bad.rounding = NAN;
    CHECK(kui::theme_create(1, &bad, &h) == KILN_ERR_INVALID_ARGUMENT);
    bad = theme_def(KILN_UI_STYLE_GAME);
    bad.size = 16;
    CHECK(kui::theme_create(1, &bad, &h) == KILN_ERR_INVALID_ARGUMENT);
    // a game-based theme with an accent, a title colour, a spacing, rounding and a background
    KilnUiThemeDef d = theme_def(KILN_UI_STYLE_GAME);
    d.role_mask = 1u << KILN_UI_ROLE_ACCENT;
    d.roles[KILN_UI_ROLE_ACCENT] = 0x7fd4ff00u;
    d.color_mask = 1ull << KILN_UI_COLOR_TITLE_BG;
    d.colors[KILN_UI_COLOR_TITLE_BG] = 0x102030ffu;
    d.var_mask = 1u << KILN_UI_VAR_ITEM_SPACING;
    d.vars[KILN_UI_VAR_ITEM_SPACING][0] = 5;
    d.vars[KILN_UI_VAR_ITEM_SPACING][1] = 4;
    d.rounding = 6;
    d.background = 0.7f;
    KilnHandle frost = 0;
    CHECK(kui::theme_create(1, &d, &frost) == KILN_OK && frost != 0);
    CHECK(set(KILN_UI_LOOK_GAME, 0.55f, 1.0f, 1.5f) == KILN_OK);
    kui::style_apply_base(1.5f);  // ui.cpp does this when the scale changes
    frame([&] {
        const ImVec4 before = ImGui::GetStyle().Colors[ImGuiCol_CheckMark];
        CHECK(kui_draw::t_push_theme(frost));
        CHECK(kui::style_depth() == 1);
        const ImGuiStyle& st = ImGui::GetStyle();
        CHECK(same(st.Colors[ImGuiCol_CheckMark], 0x7fd4ffffu));
        CHECK(same(st.Colors[ImGuiCol_TitleBg], 0x102030ffu));
        CHECK(near(st.ItemSpacing.x, 7.5f) && near(st.ItemSpacing.y, 6.0f));
        CHECK(near(st.WindowRounding, 9.0f) && near(st.FrameRounding, 9.0f));
        CHECK(near(st.Colors[ImGuiCol_WindowBg].w, 0.7f));
        CHECK(same(st.Colors[ImGuiCol_Border], kTab));  // roles it does not set stay the game's
        KilnUiLookInfo in{};
        in.size = sizeof in;
        kui::look_info(&in);
        CHECK(in.roles[KILN_UI_ROLE_ACCENT] == 0x7fd4ffffu);
        kui::style_pop();
        CHECK(kui::style_depth() == 0);
        CHECK(near(ImGui::GetStyle().Colors[ImGuiCol_CheckMark].x, before.x));
        CHECK(!kui_draw::t_push_theme(123456));  // no such theme: nothing pushed
        CHECK(kui::style_depth() == 0);
        kui_draw::t_push_theme(frost);  // left on: the frame's end takes it off
    });
    CHECK(kui::style_depth() == 0);
    CHECK(!kui_draw::t_push_theme(frost));  // outside a frame: nothing
    // an ember-based theme
    KilnUiThemeDef e = theme_def(KILN_UI_STYLE_KILN);
    e.role_mask = 1u << KILN_UI_ROLE_ACCENT;
    e.roles[KILN_UI_ROLE_ACCENT] = 0x30ff6000u;
    KilnHandle ember_green = 0;
    CHECK(kui::theme_create(1, &e, &ember_green) == KILN_OK);
    frame([&] {
        CHECK(kui::theme_push(ember_green));
        CHECK(same(ImGui::GetStyle().Colors[ImGuiCol_CheckMark], 0x30ff60ffu));
        CHECK(same(ImGui::GetStyle().Colors[ImGuiCol_WindowBg], 0x0f0d0cffu));
        kui::style_pop();
    });
    // the player picks the theme; it goes when freed
    CHECK(set(KILN_UI_LOOK_THEME, 0.55f, 1.0f, 1.0f, frost) == KILN_OK);
    frame([] { CHECK(same(ImGui::GetStyle().Colors[ImGuiCol_CheckMark], 0x7fd4ffffu)); });
    i = info();
    CHECK(i.look == KILN_UI_LOOK_THEME && i.theme == frost && i.roles[KILN_UI_ROLE_ACCENT] == 0x7fd4ffffu);
    CHECK(kui::theme_free(frost) == KILN_OK && kui::theme_free(frost) == KILN_ERR_NOT_FOUND);
    frame([] { CHECK(same(ImGui::GetStyle().Colors[ImGuiCol_CheckMark], kOutline)); });
    i = info();
    CHECK(i.look == KILN_UI_LOOK_GAME && i.theme == 0);
    CHECK(kui::theme_free(ember_green) == KILN_OK);

    // mod fonts
    const uint8_t png[64] = {0x89, 'P', 'N', 'G'};
    CHECK(kui::font_load(1, png, sizeof png) == 0);
    std::vector<uint8_t> ttf;
    for (const char* path : {"/usr/share/fonts/TTF/DejaVuSans.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
                             "/usr/share/fonts/noto/NotoSans-Regular.ttf"}) {
        std::ifstream f(path, std::ios::binary);
        if (f) {
            ttf.assign(std::istreambuf_iterator<char>(f), {});
            break;
        }
    }
    if (ttf.size() > 1024 && ttf.size() < fonts::kMaxTotalBytes / fonts::kMaxFonts) {
        std::vector<KilnHandle> hs;
        for (size_t k = 0; k < fonts::kMaxFonts; ++k) hs.push_back(kui::font_load(2, ttf.data(), ttf.size()));
        CHECK(std::all_of(hs.begin(), hs.end(), [](KilnHandle x) { return x != 0; }));
        CHECK(kui::font_load(2, ttf.data(), ttf.size()) == 0);  // the ninth: refused
        CHECK(!kui::style_push_font_handle(hs[0]));  // not in the atlas before the next frame
        frame([&] {
            CHECK(kui_draw::t_push_font_handle(hs[0]));
            CHECK(ImGui::GetFont() != ImGui::GetIO().Fonts->Fonts[0]);
            kui_draw::t_pop_font();
            CHECK(kui_draw::t_push_font_handle(hs[1]));  // left on: v3_frame_end takes it off
        });
        // a theme with a font: its windows' text
        KilnUiThemeDef fd = theme_def(KILN_UI_STYLE_KILN);
        fd.font = hs[2];
        KilnHandle ft = 0;
        CHECK(kui::theme_create(2, &fd, &ft) == KILN_OK);
        frame([&] {
            CHECK(kui::theme_push(ft));
            CHECK(ImGui::GetFont() == kui::mod_font(hs[2]));
            CHECK(kui::style_push_font(KILN_UI_FONT_DEFAULT));  // the current look's font
            CHECK(ImGui::GetFont() == kui::mod_font(hs[2]));
            ImGui::PopFont();
            kui::style_pop();
        });
        for (KilnHandle x : hs) kui::font_free(x);
        frame();
        frame([&] { CHECK(!kui_draw::t_push_font_handle(hs[0])); });
        CHECK(kui::font_load(2, ttf.data(), ttf.size()) != 0);  // room again
        kui::theme_free(ft);
    } else {
        std::printf("style_host_test: no system TTF found: the atlas part of the font checks is skipped\n");
    }

    images();

    ImGui::DestroyContext();
    std::printf("style_host_test: %d checks, %d failed\n", g_checks, g_fail);
    return g_fail ? 1 : 0;
}
