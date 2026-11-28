// SPDX-License-Identifier: MIT
// Runs the real loading of the game's other fonts (style.cpp, font_group.h) on a headless Dear ImGui context under
// ASan/UBSan, with a fake kiln.assets serving a made-up keen::UiFontResource (the shape of client 26430625's: a default
// group with Cyrillic, Thai and large CJK fallbacks, and zh-CN, ja-JP, ko-KR, zh-TW, Cyrillic groups) and a system TTF for
// every font file (padded to the size the resource gives; no game data):
//  - the default group's small fonts after its Latin source, for every language, before a CJK group's; a Cyrillic
//    language is READY with them, FAILED when they lack Cyrillic (logged);
//  - Kiln's language (set_texts) picks the CJK group; else the game's language; Latin needs none;
//  - look_info's glyph state (loading, ready, failed) and its language;
//  - the group's fonts merged into the overlay's default font and into the game's UI font and bold, also when those arrive
//    after the group; another language's group replaces them; back to Latin removes them;
//  - a group whose file cannot be read: failed, logged, the atlas as it was;
//  - the fields' places from the game's reflection (a fake kiln.types with another layout).
// With KILN_TEST_CJK_FONT set to a real CJK TTF/OTF (an extracted game font, never in the tree), it also checks that a Chinese
// glyph is drawn from it.
#include <cassert>
#include <chrono>
#include <cstdio>
#include <cstdlib>
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
static std::atomic<uint32_t> g_game_language{0};
KilnResult game_screen(KilnUiScreen* out) {
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    std::memset(out, 0, sizeof *out);
    out->size = sizeof *out;
    out->language = g_game_language.load();
    return KILN_OK;
}
}  // namespace kui

namespace fg = kui::fontgroup;

static int g_fail = 0, g_checks = 0;
#define CHECK(cond)                                                     \
    do {                                                                \
        ++g_checks;                                                     \
        if (!(cond)) {                                                  \
            std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond); \
            ++g_fail;                                                   \
        }                                                               \
    } while (0)

static bool logged(const char* part) {
    std::lock_guard lk(kui::g_log_mu);
    for (const auto& l : kui::g_logs)
        if (l.find(part) != std::string::npos) return true;
    return false;
}

// ---- a made-up keen::UiFontResource (font_group_test.cpp writes the same shape) ----
static void put32(std::vector<uint8_t>& b, size_t at, uint32_t v) { std::memcpy(&b[at], &v, 4); }
static void put_array(std::vector<uint8_t>& b, size_t field, size_t base, uint32_t n) {
    put32(b, field, static_cast<uint32_t>(static_cast<int32_t>(base - field)));
    put32(b, field + 4, n);
}
struct FakeSet {
    uint32_t font;
    uint8_t variant;
    fg::Range range;
};
struct FakeGroup {
    uint64_t mask;
    bool is_default;
    std::vector<FakeSet> sets;
};
static std::vector<uint8_t> resource(const fg::Layout& l, uint32_t font_bytes) {
    const uint64_t bit = 1;
    const std::vector<FakeGroup> groups = {
        {(bit << 1) | (bit << 3) | (bit << 20), true,
         {{4, 2, {0, 0x2e7f}}, {0, 0, {0, 0x2e7f}}, {24, 1, {0, 0x2e7f}}, {13, 0, {0x2e80, 0xabff}}, {26, 1, {0x400, 0x4ff}},
          {10, 0, {0x400, 0x4ff}}, {12, 0, {0xac00, 0xffff}}, {6, 0, {0xe00, 0xe7f}}}},
        {bit << 20, false, {{18, 2, {0, 0xabff}}, {13, 0, {0, 0xabff}}, {5, 1, {0, 0xabff}}, {12, 0, {0xac00, 0xffff}}}},
        {bit << 11, false, {{3, 0, {0, 0xabff}}, {12, 0, {0xac00, 0xffff}}}},
        {bit << 13, false, {{12, 0, {0, 0xffff}}}},
        {bit << 21, false, {{21, 0, {0, 0xabff}}, {12, 0, {0xac00, 0xffff}}}},
        {(bit << 9) | (bit << 34), false, {{10, 0, {0, 0xabff}}, {12, 0, {0xac00, 0xffff}}, {13, 0, {0x500, 0xabff}}}},
    };
    std::vector<uint8_t> b(8192, 0);
    size_t at = 64;
    const size_t fonts = at;
    for (uint32_t i = 0; i < 28; ++i) {
        // the CJK files (12, 13) are large, as the game's: the default group's fonts leave them to the CJK groups
        put32(b, fonts + i * l.font_size, i == 12 || i == 13 ? (3u << 20) : font_bytes);
        put32(b, fonts + i * l.font_size + 4, 1000 + i);  // hash0: the font's index
    }
    put_array(b, l.res_fonts, fonts, 28);
    at += 28 * l.font_size;
    const size_t gbase = at;
    put_array(b, l.res_groups, gbase, static_cast<uint32_t>(groups.size()));
    at += groups.size() * l.group_size;
    for (size_t g = 0; g < groups.size(); ++g) {
        const size_t ga = gbase + g * l.group_size;
        std::memcpy(&b[ga + l.group_mask], &groups[g].mask, 8);
        b[ga + l.group_default] = groups[g].is_default ? 1 : 0;
        const size_t sbase = at;
        put_array(b, ga + l.group_sets, sbase, static_cast<uint32_t>(groups[g].sets.size()));
        at += groups[g].sets.size() * l.set_size;
        for (size_t s = 0; s < groups[g].sets.size(); ++s) {
            const size_t sa = sbase + s * l.set_size;
            put32(b, sa + l.set_font, groups[g].sets[s].font);
            b[sa + l.set_variant] = groups[g].sets[s].variant;
            put_array(b, sa + l.set_ranges, at, 1);
            put32(b, at + l.range_first, groups[g].sets[s].range.first);
            put32(b, at + l.range_last, groups[g].sets[s].range.last);
            at += l.range_size;
        }
    }
    b.resize(at);
    return b;
}

// ---- fake kiln.assets ----
static std::vector<uint8_t> g_latin;    // a system TTF: every font file of the made-up resource
static std::vector<uint8_t> g_cjk;      // KILN_TEST_CJK_FONT, served as font 13 when set
static std::vector<uint8_t> g_res;
static std::atomic<bool> g_font_res_listed{false};
static std::atomic<uint32_t> g_missing{21};  // the font no blob_read finds (zh-TW's own)
constexpr uint32_t kFontRes = kui::look::fnv1a("keen::UiFontResource");
static std::mutex g_res_mu;

KilnResult KILN_CALL as_list(uint32_t type, KilnAssetKey* out, size_t cap, size_t* n) {
    const bool have = type == kFontRes && g_font_res_listed.load();
    if (n) *n = have ? 1 : 0;
    if (have && out && cap) {
        std::memset(out, 0, sizeof *out);
        out->type = type;
    }
    return KILN_OK;
}
KilnResult KILN_CALL as_read(const KilnAssetKey* key, void* buf, size_t cap, size_t* len) {
    if (!key || key->type != kFontRes) return KILN_ERR_NOT_FOUND;
    std::lock_guard lk(g_res_mu);
    if (len) *len = g_res.size();
    if (buf && cap >= g_res.size()) std::memcpy(buf, g_res.data(), g_res.size());
    return KILN_OK;
}
KilnResult KILN_CALL as_blob(const KilnContentHash* h, void* buf, size_t cap, size_t* len) {
    if (!h || h->hash0 < 1000 || h->hash0 - 1000 == g_missing.load()) return KILN_ERR_NOT_FOUND;
    const std::vector<uint8_t>& f = h->hash0 == 1013 && !g_cjk.empty() ? g_cjk : g_latin;
    if (h->size < f.size()) return KILN_ERR_NOT_FOUND;  // a larger one: the TTF, then zeros (a TTF reads its tables by offset)
    if (len) *len = h->size;
    if (buf && cap >= h->size) {
        std::memcpy(buf, f.data(), f.size());
        std::memset(static_cast<uint8_t*>(buf) + f.size(), 0, h->size - f.size());
    }
    return KILN_OK;
}

// ---- fake kiln.types (step 6: another layout, as another build's reflection gives it) ----
struct FakeField {
    const char* name;
    uint64_t offset;
};
struct FakeType {
    const char* name;
    uint32_t size;
    std::vector<FakeField> fields;
};
static const fg::Layout kOther = [] {
    fg::Layout o;
    o.res_fonts = 0x10, o.res_groups = 0x0, o.font_size = 40;
    o.group_size = 32, o.group_mask = 0x8, o.group_sets = 0x10, o.group_default = 0x1c;
    o.set_size = 24, o.set_font = 0x4, o.set_ranges = 0x8, o.set_variant = 0x14;
    o.range_size = 8, o.range_first = 0x4, o.range_last = 0x0;
    return o;
}();
static FakeType tRes{"keen::UiFontResource", 24, {{"fonts", kOther.res_fonts}, {"languageGroups", kOther.res_groups}}};
static FakeType tTtf{"keen::TtfFontResource", kOther.font_size, {}};
static FakeType tGroup{"keen::UiFontLanguageGroupResource", kOther.group_size,
                       {{"groupLanguages", kOther.group_mask}, {"glyphSets", kOther.group_sets}, {"isDefaultLanguageGroup", kOther.group_default}}};
static FakeType tSet{"keen::UiGlyphSetResource", kOther.set_size,
                     {{"fontIndex", kOther.set_font}, {"ranges", kOther.set_ranges}, {"fontVariantMask", kOther.set_variant}}};
static FakeType tRange{"keen::UiFontRange", kOther.range_size, {{"firstRune", kOther.range_first}, {"lastRune", kOther.range_last}}};
const KilnType* KILN_CALL ty_by_name(KilnStr n) {
    const std::string s(n.ptr, n.len);
    for (FakeType* t : {&tRes, &tTtf, &tGroup, &tSet, &tRange})
        if (s == t->name) return reinterpret_cast<const KilnType*>(t);
    return nullptr;
}
KilnResult KILN_CALL ty_info(const KilnType* type, KilnTypeInfo* out) {
    const auto* t = reinterpret_cast<const FakeType*>(type);
    if (!t || !out) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    out->name = out->qualified_name = KilnStr{t->name, std::strlen(t->name)};
    out->type_size = t->size;
    out->kind = KILN_TYPE_STRUCT;
    out->field_count = static_cast<uint32_t>(t->fields.size());
    return KILN_OK;
}
KilnResult KILN_CALL ty_field(const KilnType* type, uint32_t i, KilnTypeField* out) {
    const auto* t = reinterpret_cast<const FakeType*>(type);
    if (!t || !out || i >= t->fields.size()) return KILN_ERR_NOT_FOUND;
    out->name = KilnStr{t->fields[i].name, std::strlen(t->fields[i].name)};
    out->type = reinterpret_cast<const KilnType*>(&tTtf);  // any type: the walk stops at the field
    out->offset = t->fields[i].offset;
    return KILN_OK;
}

static KilnTypesApi g_types{};
static KilnAssetsApi g_assets{};
static KilnApi g_kapi{};
static std::atomic<bool> g_types_on{false};
const void* KILN_CALL k_service(KilnStr name, uint32_t) {
    const std::string s(name.ptr, name.len);
    if (s == KILN_TYPES_SERVICE && g_types_on.load()) return &g_types;
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
static const char* g_text = "Mods 模组 モッド 모드";
static void frame() {
    kui::style_frame_begin();
    ImGui::NewFrame();
    kui::t_frame = true;
    ImGui::Begin("test");
    ImGui::TextUnformatted(g_text);
    if (ImFont* f = kui::game_font()) {
        ImGui::PushFont(f, 0.0f);
        ImGui::TextUnformatted(g_text);
        ImGui::PopFont();
    }
    ImGui::End();
    kui_draw::v3_frame_end();
    kui::style_frame_end();
    kui::t_frame = false;
    ImGui::Render();
    textures(ImGui::GetDrawData());
}
static KilnUiLookInfo info() {
    KilnUiLookInfo i{};
    i.size = sizeof i;
    CHECK(kui::look_info(&i) == KILN_OK);
    return i;
}
// frames until look_info's glyph state is `state` for `language` (or 10 s)
static bool until(uint8_t state, uint8_t language) {
    for (int n = 0; n < 2000; ++n) {
        frame();
        const KilnUiLookInfo i = info();
        if (i.glyphs == state && i.glyph_language == language) return true;
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
    }
    const KilnUiLookInfo i = info();
    std::printf("  glyphs %u language %u, wanted %u %u\n", i.glyphs, i.glyph_language, state, language);
    return false;
}
// font 13 as the real CJK file (KILN_TEST_CJK_FONT): its size in the resource
static std::vector<uint8_t> with_cjk(std::vector<uint8_t> res, const fg::Layout& l) {
    if (g_cjk.empty()) return res;
    size_t fbase = 0;
    uint32_t n = 0;
    fg::blob_array(res.data(), res.size(), l.res_fonts, l.font_size, &fbase, &n);
    put32(res, fbase + 13 * l.font_size, static_cast<uint32_t>(g_cjk.size()));
    return res;
}
static int merged(ImFont* f) { return f ? f->Sources.Size - 1 : -1; }  // sources after the font's own
static void set_kiln(const char* language) { kui::text::table().set(language, {}); }

int main() {
    for (const char* path : {"/usr/share/fonts/TTF/DejaVuSans.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
                             "/usr/share/fonts/noto/NotoSans-Regular.ttf", "/usr/share/fonts/liberation/LiberationSans-Regular.ttf"}) {
        std::ifstream f(path, std::ios::binary);
        if (f) {
            g_latin.assign(std::istreambuf_iterator<char>(f), {});
            break;
        }
    }
    if (g_latin.size() < 1024) {
        std::printf("group_host_test: no system TTF found: skipped\n");
        return 0;
    }
    if (const char* cjk = std::getenv("KILN_TEST_CJK_FONT")) {
        std::ifstream f(cjk, std::ios::binary);
        if (f) g_cjk.assign(std::istreambuf_iterator<char>(f), {});
    }
    g_res = with_cjk(resource(fg::Layout{}, static_cast<uint32_t>(g_latin.size())), fg::Layout{});
    g_types.header = {sizeof g_types, KILN_TYPES_VERSION};
    g_types.type_by_name = &ty_by_name;
    g_types.type_info = &ty_info;
    g_types.type_field = &ty_field;
    g_assets.header = {sizeof g_assets, KILN_ASSETS_VERSION};
    g_assets.resource_list = &as_list;
    g_assets.resource_read = &as_read;
    g_assets.blob_read = &as_blob;
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
    ImFont* base = io.Fonts->Fonts[0];
    kui::style_apply_base(1.0f);

    // the game lists its UI fonts; Kiln's ember look first, so the game's UI font is not read until the game's look (step 2)
    g_font_res_listed = true;
    KilnUiLook ember{};
    ember.size = sizeof ember;
    ember.look = KILN_UI_LOOK_KILN;
    ember.opacity = ember.scale = 1.0f;
    ember.background = 0.55f;
    CHECK(kui::look_set(&ember) == KILN_OK);

    // 0. the default group's fonts load at the first frame, whatever the language and look; here its Cyrillic file (font 10)
    //    cannot be read: Thai alone joins, a Cyrillic language's glyphs fail (logged once), Latin needs none
    g_missing = 10;
    set_kiln("en-us");
    for (int n = 0; n < 2000 && kui::g_extra_state.load() != 3; ++n) {
        frame();
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
    }
    frame();
    CHECK(kui::g_extra_state.load() == 3 && merged(base) == 1);
    CHECK(info().glyphs == KILN_UI_GLYPHS_LATIN && info().glyph_language == 0);
    CHECK(logged("the game's default font group joined the overlay's font atlas (group 0, fonts 6 of 28"));
    CHECK(logged("fonts 13, 12 left out (over 2 MiB"));
    CHECK(logged("fonts 10 unreadable"));
    CHECK(logged("has no Cyrillic font Kiln can use"));
    set_kiln("ru");
    CHECK(until(KILN_UI_GLYPHS_FAILED, fg::kRuRu));
    CHECK(kui::g_group_lang.load() == 0);  // Cyrillic needs no group of its own
    // the same read with font 10 there (as a new process would): Cyrillic from it, after the overlay's own font
    kui::unmerge(kui::g_extra_fonts, kui::g_extra_into);
    kui::g_extra_state = 0;
    kui::g_extra_tries = 0;
    kui::g_extra_retry_at = 0;
    g_missing = 21;
    CHECK(until(KILN_UI_GLYPHS_READY, fg::kRuRu));
    frame();
    CHECK(merged(base) == 2 && base->IsGlyphInFont(0x416));  // Ж: ImGui's own font has none, font 10 (a system TTF) has
    CHECK(std::strstr(base->Sources[1]->Name, "game font 10 (default group)") && std::strstr(base->Sources[2]->Name, "game font 6"));
    CHECK(logged("fonts 10, 6 of 28, ") && logged("; Cyrillic; fonts 13, 12 left out"));
    // Kiln in English, the game in Ukrainian: the game's language is reported (mods' texts follow it)
    set_kiln("en-us");
    kui::g_game_language = fg::kUkUa;
    CHECK(until(KILN_UI_GLYPHS_READY, fg::kUkUa));
    kui::g_game_language = 0;

    // 1. Latin: no group to load; the default group's fonts stay
    set_kiln("en-us");
    for (int n = 0; n < 5; ++n) frame();
    CHECK(info().glyphs == KILN_UI_GLYPHS_LATIN && info().glyph_language == 0);
    CHECK(merged(base) == 2);

    // 2. Kiln forced Chinese: the zh-CN group's regular fonts (13, then 12), merged into the default font after the default
    //    group's; the game's UI font and its bold, read when the player picks the game's look, get both as they arrive
    set_kiln("zh-cn");
    frame();
    CHECK(info().glyphs == KILN_UI_GLYPHS_LOADING && info().glyph_language == fg::kZhCn);
    CHECK(until(KILN_UI_GLYPHS_READY, fg::kZhCn));
    CHECK(kui::game_font() == nullptr);
    ember.look = KILN_UI_LOOK_GAME;
    CHECK(kui::look_set(&ember) == KILN_OK);
    CHECK(merged(base) == 4);
    CHECK(std::strstr(base->Sources[2]->Name, "(default group)") && std::strstr(base->Sources[3]->Name, "game font 13 (language group)"));
    CHECK(logged("joined the overlay's font atlas (group 1, fonts 13, 12 of 28"));
    CHECK(logged("layout of 26430625 (no kiln.types)"));
    for (int n = 0; n < 1500 && !kui::game_font(); ++n) {  // the game's own font (default group: fonts 0 and 24)
        frame();
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
    }
    frame();
    CHECK(kui::game_font() != nullptr && merged(kui::game_font()) == 4);  // merged into it as it arrived
    CHECK(std::strstr(kui::game_font()->Sources[1]->Name, "game font 10 (default group)"));
    CHECK(merged(kui::g_game_bold) == 4);
    if (!g_cjk.empty()) {  // a real CJK file as font 13: the overlay's fonts have 模 (U+6A21) now, the Latin ones never did
        CHECK(base->IsGlyphInFont(0x6A21) && kui::game_font()->IsGlyphInFont(0x6A21));
        std::printf("group_host_test: the real CJK font gives 模 (%zu bytes)\n", g_cjk.size());
    }

    // 3. Japanese: zh-CN's fonts leave, ja-JP's (3, 12) come; the default group's stay
    set_kiln("ja-jp");
    CHECK(until(KILN_UI_GLYPHS_READY, fg::kJaJp));
    CHECK(merged(base) == 4 && merged(kui::game_font()) == 4);
    CHECK(logged("the game's font of language 20 left the overlay's font atlas"));

    // 4. Kiln English in a Korean game: the game's language picks (mods' texts are Korean): font 12 alone
    set_kiln("en-us");
    kui::g_game_language = fg::kKoKr;
    CHECK(until(KILN_UI_GLYPHS_READY, fg::kKoKr));
    CHECK(merged(base) == 3);

    // 5. a group whose own file cannot be read (zh-TW's font 21): failed, logged, nothing of it merged
    set_kiln("zh-tw");
    CHECK(until(KILN_UI_GLYPHS_FAILED, fg::kZhTw));
    frame();  // the frame after the read says it once
    CHECK(merged(base) == 2);
    CHECK(logged("font 21 of the group is unreadable"));
    CHECK(logged("Kiln's texts stay English"));

    // 5b. Kiln in Russian in a Chinese game: Russian's glyphs reported, the Chinese group still loads for mods' texts
    set_kiln("ru-ru");
    kui::g_game_language = fg::kZhCn;
    CHECK(until(KILN_UI_GLYPHS_READY, fg::kRuRu));
    for (int n = 0; n < 2000 && kui::g_group_state.load() != 3; ++n) {
        frame();
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
    }
    frame();
    CHECK(kui::g_group_lang.load() == fg::kZhCn && merged(base) == 4);

    // 6. back to Latin: the atlas as it was before the groups
    set_kiln("de-de");
    kui::g_game_language = 3;
    CHECK(until(KILN_UI_GLYPHS_LATIN, 0));
    CHECK(merged(base) == 2 && merged(kui::game_font()) == 2);

    // 7. the fields' places from the game's reflection: another layout read right
    {
        std::lock_guard lk(g_res_mu);
        g_res = with_cjk(resource(kOther, static_cast<uint32_t>(g_latin.size())), kOther);
    }
    g_types_on = true;
    g_missing = 99;
    set_kiln("zh-cn");
    CHECK(until(KILN_UI_GLYPHS_READY, fg::kZhCn));
    CHECK(merged(base) == 4);
    CHECK(logged("layout from the game's reflection"));
    set_kiln("en");
    CHECK(until(KILN_UI_GLYPHS_LATIN, 0));

    // 8. Kiln and the game in a Latin language, and a mod's Chinese label drawn through kiln.ui's slots (HTR, 2026-11-27:
    //    Nexus 347's ShroudForge settings drew as ????): zh-CN's group joins the atlas for it; look_info stays Latin (Kiln's
    //    texts are English). Latin text through the same slots asks for nothing.
    auto slot_frame = [](const char* text) {
        kui::style_frame_begin();
        ImGui::NewFrame();
        kui::t_frame = true;
        ImGui::Begin("slots");
        bool on = false;
        kui_draw::t_checkbox(KilnStr{text, std::strlen(text)}, &on);
        kui_draw::t_text_wrapped(KilnStr{text, std::strlen(text)});
        ImGui::End();
        kui_draw::v3_frame_end();
        kui::style_frame_end();
        kui::t_frame = false;
        ImGui::Render();
        textures(ImGui::GetDrawData());
    };
    for (int n = 0; n < 5; ++n) slot_frame("Sulfur / Archaic Essence 4");
    CHECK(kui::g_group_lang.load() == 0 && merged(base) == 2);
    slot_frame("\xe7\xa1\xab\xe7\xa3\xba | \xe7\xa1\xab\xe7\xa3\xba / Sulfur");  // 硫磺 | 硫磺 / Sulfur
    for (int n = 0; n < 2000 && !(kui::g_group_lang.load() == fg::kZhCn && kui::g_group_state.load() == 3); ++n) {
        frame();
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
    }
    frame();
    CHECK(kui::g_group_lang.load() == fg::kZhCn && kui::g_group_state.load() == 3 && merged(base) == 4);
    CHECK(info().glyphs == KILN_UI_GLYPHS_LATIN && info().glyph_language == 0);
    CHECK(logged("a text in Chinese, Japanese or Korean is drawn"));
    // Kiln's language picks its own group still (Japanese here), and back to Latin the Chinese group stays for the text
    set_kiln("ja");
    CHECK(until(KILN_UI_GLYPHS_READY, fg::kJaJp));
    set_kiln("en");
    for (int n = 0; n < 2000 && !(kui::g_group_lang.load() == fg::kZhCn && kui::g_group_state.load() == 3); ++n) {
        frame();
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
    }
    CHECK(kui::g_group_lang.load() == fg::kZhCn && merged(base) == 4);

    ImGui::DestroyContext();
    std::printf("group_host_test: %d checks, %d failed\n", g_checks, g_fail);
    return g_fail ? 1 : 0;
}
