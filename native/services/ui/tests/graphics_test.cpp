// SPDX-License-Identifier: MIT
// Host test of Kiln's graphics tools' plain logic: the frame cap's pacing (frame_pacer.h), kiln.json "graphics" and the
// DLSS render size (graphics_settings.h), when the tools step aside (graphics_aside.h) and NGX's parameter slots
// (ngx_param.h, against a fake parameter object).
#include <cassert>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <map>
#include <string>

#include "../frame_pacer.h"
#include "../graphics_aside.h"
#include "../graphics_settings.h"
#include "../ngx_param.h"

using namespace kui;
using namespace kui::gfx;

static int g_checks = 0;
#define CHECK(...)                                                              \
    do {                                                                        \
        ++g_checks;                                                             \
        if (!(__VA_ARGS__)) {                                                           \
            std::fprintf(stderr, "%s:%d: CHECK(%s) failed\n", __FILE__, __LINE__, #__VA_ARGS__); \
            std::exit(1);                                                       \
        }                                                                       \
    } while (0)

static constexpr int64_t kFreq = 10'000'000;  // QueryPerformanceFrequency on Windows 10+ and Wine

static void pacer_holds_the_rate() {
    FramePacer p;
    CHECK(!p.on());
    CHECK(p.plan(123) == 123);  // off: at once
    p.set(60, kFreq);
    CHECK(p.on() && p.interval() == 166667);
    bool resynced = false;
    // the first frame goes at once and starts the schedule
    int64_t now = 1'000'000;
    CHECK(p.plan(now, &resynced) == now && resynced);
    // a game far faster than the cap: every frame waits for its tick, exactly one interval apart
    int64_t last = now;
    for (int i = 0; i < 600; ++i) {
        now = last + 20'000;  // ready 2 ms after the last went
        const int64_t go = p.plan(now, &resynced);
        CHECK(!resynced);
        CHECK(go - last == 166667);
        last = go;
    }
    // 600 frames at 60 fps: 10 seconds, to the tick
    CHECK(last - 1'000'000 == 600 * 166667);
}

static void pacer_never_holds_a_slower_game() {
    FramePacer p;
    p.set(60, kFreq);
    bool resynced = false;
    int64_t now = 0;
    p.plan(now += 1, &resynced);
    // the game's own limit at 50 fps (20 ms): no frame is ever held
    for (int i = 0; i < 300; ++i) {
        now += 200'000;
        CHECK(p.plan(now, &resynced) == now);
    }
    // V-Sync at 60 Hz with a little jitter around the cap's own rate: held at most by the jitter, no burst after a late one
    p.set(0, kFreq);
    p.set(60, kFreq);
    now = 50'000'000;
    int64_t go = p.plan(now);
    int64_t worst_hold = 0;
    for (int i = 0; i < 300; ++i) {
        const int64_t jitter = (i % 3 == 0) ? 3'000 : (i % 3 == 1 ? -2'000 : 0);
        now = go + 166667 + jitter;
        const int64_t g = p.plan(now);
        worst_hold = std::max(worst_hold, g - now);
        CHECK(g >= now);
        go = g;
    }
    CHECK(worst_hold <= 3'000);
}

static void pacer_resyncs_after_a_hitch() {
    FramePacer p;
    p.set(100, kFreq);  // 10 ms
    bool resynced = false;
    int64_t now = 5'000'000;
    p.plan(now);
    // a 250 ms hitch (a world save, a shader compile): the next frames do not rush to catch up
    now += 2'500'000;
    CHECK(p.plan(now, &resynced) == now && resynced);
    const int64_t a = p.plan(now + 1'000, &resynced);
    CHECK(!resynced && a == now + 100'000);
    // late by less than half an interval: kept on the schedule (the next one waits the rest)
    const int64_t b = p.plan(a + 100'000 + 30'000, &resynced);
    CHECK(!resynced && b == a + 130'000);
    const int64_t c = p.plan(b + 1'000, &resynced);
    CHECK(!resynced && c == a + 200'000);
    // late by more than half an interval: a new schedule from that frame
    const int64_t d = p.plan(c + 100'000 + 60'000, &resynced);
    CHECK(resynced && d == c + 160'000);
}

static void pacer_settings() {
    FramePacer p;
    p.set(144, kFreq);
    CHECK(p.interval() == 69444);
    p.plan(1);
    p.set(144, kFreq);  // the same rate keeps the schedule
    bool resynced = true;
    p.plan(2, &resynced);
    CHECK(!resynced);
    p.set(30, kFreq);  // another rate starts a new one
    p.plan(3, &resynced);
    CHECK(resynced);
    p.set(0, kFreq);
    CHECK(!p.on() && p.plan(77) == 77);
    CHECK(clamp_fps(0) == 0 && clamp_fps(-5) == 0 && clamp_fps(1) == 20 && clamp_fps(19) == 20 && clamp_fps(20) == 20);
    CHECK(clamp_fps(237) == 237 && clamp_fps(500) == 500 && clamp_fps(501) == 500 && clamp_fps(100000) == 500);
}

// Edges: the highest cap, a cap changed while frames run, and any mix of frame times: a frame is never held longer than
// one interval, never earlier than its slot, and late ones never bring a burst.
static void pacer_edges() {
    FramePacer p;
    p.set(kMaxFps, kFreq);
    CHECK(p.interval() == 20'000);  // 2 ms
    int64_t go = p.plan(1'000);
    for (int i = 0; i < 100; ++i) {
        const int64_t g = p.plan(go + 1'000);  // a game at 10000 fps: every frame waits for its 2 ms slot
        CHECK(g - go == 20'000);
        go = g;
    }
    // a cap changed while running: the next frame goes at once and starts the new schedule
    p.set(60, kFreq);
    bool resynced = false;
    CHECK(p.plan(go + 1'000, &resynced) == go + 1'000 && resynced);
    CHECK(p.plan(go + 2'000, &resynced) == go + 1'000 + 166'667 && !resynced);
    // random frame times between 0.1 ms and 3 intervals at a 144 fps cap
    p.set(144, kFreq);
    uint64_t seed = 12345;
    auto rnd = [&](int64_t n) { seed = seed * 6364136223846793005ull + 1442695040888963407ull; return static_cast<int64_t>((seed >> 33) % static_cast<uint64_t>(n)); };
    int64_t now = 9'000'000, last_go = 0, slot = 0;
    for (int i = 0; i < 20'000; ++i) {
        const int64_t g = p.plan(now, &resynced);
        CHECK(g >= now);                     // never before the frame is there
        CHECK(g - now <= p.interval());      // never held longer than one interval
        if (last_go && !resynced) CHECK(g - last_go >= p.interval() - p.interval() / 2);  // no burst after a late frame
        if (resynced) slot = g;
        else CHECK((g - slot) % p.interval() == 0 || g == now);  // on the schedule, or late (then at once)
        last_go = g;
        now = g + 1'000 + rnd(3 * p.interval());
    }
}

static void wait_plans() {
    const int64_t ms = kFreq / 1000;
    auto w = wait_plan(0, ms);
    CHECK(!w.spin && !w.timer);
    w = wait_plan(-5, ms);
    CHECK(!w.spin && !w.timer);
    w = wait_plan(ms / 2, ms);  // shorter than the timer's lateness: spin only
    CHECK(w.spin && !w.timer);
    w = wait_plan(16 * ms, ms);  // the timer sleeps all but the margin, the spin does the rest
    CHECK(w.spin && w.timer == 15 * ms);
}

static void timer_margin() {
    const int64_t ms = kFreq / 1000;
    TimerMargin m;
    CHECK(m.margin(kFreq) == 2 * ms);  // until a window is full
    for (int i = 0; i < 63; ++i) m.observe(ms / 10);
    CHECK(m.margin(kFreq) == 2 * ms);
    m.observe(-ms);  // early counts as on time
    CHECK(m.margin(kFreq) == ms / 10 + ms / 4);  // a timer that wakes on time: little spin
    // one late wake keeps the margin up for this window and the next
    m.observe(3 * ms);
    CHECK(m.margin(kFreq) == 3 * ms + ms / 4);
    for (int i = 0; i < 63; ++i) m.observe(0);
    CHECK(m.margin(kFreq) == 3 * ms + ms / 4);
    for (int i = 0; i < 64; ++i) m.observe(0);
    CHECK(m.margin(kFreq) == ms / 4);
    // never past 4 ms
    for (int i = 0; i < 64; ++i) m.observe(50 * ms);
    CHECK(m.margin(kFreq) == 4 * ms);
}

static void stats() {
    FrameStats s;
    CHECK(s.rendered_fps() == 0 && s.output_fps() == 0);
    int64_t now = 0;
    for (int i = 0; i <= 60; ++i) s.frame(now += 166667, kFreq);  // a second at 60 fps
    CHECK(std::fabs(s.rendered_fps() - 60.0f) < 0.6f);
    CHECK(std::fabs(s.output_fps() - s.rendered_fps()) < 0.01f);
    CHECK(std::fabs(s.frame_ms() - 16.67f) < 0.05f);
    CHECK(std::fabs(s.max_ms() - 16.67f) < 0.05f);
    // with frame generation (a later phase) the output counts every present, the rendered count only the game's
    FrameStats g;
    now = 0;
    for (int i = 0; i <= 60; ++i) {
        g.frame(now += 83333, kFreq, true);
        g.frame(now += 83333, kFreq, false);
    }
    CHECK(std::fabs(g.rendered_fps() - 60.0f) < 1.0f && std::fabs(g.output_fps() - 120.0f) < 2.0f);
    CHECK(std::fabs(g.frame_ms() - 16.67f) < 0.1f);
    // one long frame shows in the max, not as a frame rate of zero
    FrameStats h;
    now = 0;
    for (int i = 0; i < 20; ++i) h.frame(now += 100'000, kFreq);
    h.frame(now += 4'000'000, kFreq);  // 400 ms: the window (half a second) closes on it
    CHECK(h.max_ms() >= 399.0f && h.rendered_fps() > 0);
    h.reset();
    CHECK(h.rendered_fps() == 0);
}

static nlohmann::json J(const char* text) { return nlohmann::json::parse(text, nullptr, true, true); }

static void settings_parse() {
    std::vector<std::string> w;
    Settings s = parse_settings(J("{}"), &w);
    CHECK(s == Settings{} && w.empty());
    CHECK(!s.wants_ngx() && !s.dlss_set());
    s = parse_settings(J(R"({"ui": {"look": "kiln"}, "graphics": null})"), &w);
    CHECK(s == Settings{} && w.empty());
    s = parse_settings(J(R"(// Kiln's JSONC
        {"graphics": {
          "fpsCap": 117,            // a comment
          "dlssPreset": "k",
          "dlssScale": "perMode",
          "dlssRenderScale": 0.8,
          "dlssQualityScale": 0.75, "dlssBalancedScale": 2, "dlssPerformanceScale": 0.1, "dlssUltraPerformanceScale": "x",
          "readout": true
        }})"), &w);
    CHECK(s.fps_cap == 117 && s.dlss_preset == kPresetK && s.dlss_scale == DlssScale::per_mode);
    CHECK(std::fabs(s.custom_scale - 0.8f) < 1e-6f);
    CHECK(std::fabs(s.mode_scale[0] - 0.75f) < 1e-6f && s.mode_scale[1] == 1.0f && s.mode_scale[2] == kMinScale);
    CHECK(s.mode_scale[3] == kDlssScales[3]);  // not a number: the default, with a warning
    CHECK(s.readout && s.dlss_set() && s.wants_ngx());
    CHECK(w.size() == 1 && w[0].find("dlssUltraPerformanceScale") != std::string::npos);
    w.clear();
    s = parse_settings(J(R"({"graphics": {"fpsCap": 5, "dlssPreset": "A", "dlssScale": "half", "readout": 1}})"), &w);
    CHECK(s.fps_cap == 20 && s.dlss_preset == kPresetDefault && s.dlss_scale == DlssScale::game && !s.readout);
    CHECK(w.size() == 4);
    w.clear();
    s = parse_settings(J(R"({"graphics": {"fpsCap": 0, "dlssPreset": "default", "dlssScale": "custom"}})"), &w);
    CHECK(s.fps_cap == 0 && s.dlss_preset == 0 && s.dlss_scale == DlssScale::custom && w.empty() && s.dlss_set());
    s = parse_settings(J(R"({"graphics": {"fpsCap": 9000}})"), &w);
    CHECK(s.fps_cap == 500 && w.size() == 1);
    w.clear();
    s = parse_settings(J(R"({"graphics": "fast"})"), &w);
    CHECK(s == Settings{} && w.size() == 1);
    // only the readout: Kiln's hooks on DLSS (they only read), no DLSS setting
    s = parse_settings(J(R"({"graphics": {"readout": true}})"));
    CHECK(s.wants_ngx() && !s.dlss_set());
}

static void presets() {
    CHECK(preset_name(0) == "default" && preset_name(kPresetJ) == "J" && preset_name(kPresetM) == "M" && preset_name(5) == "E");
    CHECK(preset_name(99) == "99");
    CHECK(preset_from_name("J") == kPresetJ && preset_from_name("l") == kPresetL && preset_from_name("Default") == kPresetDefault);
    CHECK(!preset_from_name("E") && !preset_from_name("F") && !preset_from_name("") && !preset_from_name("KK"));
    Settings s;
    s.dlss_preset = 7;  // not one Kiln offers
    s.fps_cap = 3;
    s.custom_scale = NAN;
    s.mode_scale[2] = 5;
    s.dlss_scale = static_cast<DlssScale>(9);
    s = clamped(s);
    CHECK(s.dlss_preset == 0 && s.fps_cap == 20 && s.custom_scale == 0.67f && s.mode_scale[2] == 1.0f && s.dlss_scale == DlssScale::game);
}

static void render_sizes() {
    Settings s;
    const Size out{2560, 1440}, dlss_q{1707, 960}, min{853, 480}, max{2560, 1440};
    // the game's mode: DLSS's own
    CHECK(!render_size(s, kQuality, out, dlss_q, min, max));
    // custom: one scale for every mode but DLAA
    s.dlss_scale = DlssScale::custom;
    s.custom_scale = 0.75f;
    CHECK(render_size(s, kQuality, out, dlss_q, min, max) == Size{1920, 1080});
    CHECK(render_size(s, kUltraPerformance, out, Size{853, 480}, min, max) == Size{1920, 1080});
    CHECK(render_size(s, kUltraQuality, out, Size{1970, 1108}, min, max) == Size{1920, 1080});
    CHECK(!render_size(s, kDlaa, out, out, min, max));
    // below DLSS's smallest: its smallest
    s.custom_scale = 0.33f;
    CHECK(render_size(s, kPerformance, out, Size{1280, 720}, min, max) == Size{853, 480});
    // per mode: the defaults are DLSS's own (no change), a moved one is used, Ultra Quality and DLAA keep DLSS's
    s.dlss_scale = DlssScale::per_mode;
    CHECK(!render_size(s, kQuality, out, dlss_q, min, max));
    CHECK(!render_size(s, kBalanced, out, Size{1485, 835}, min, max));
    CHECK(!render_size(s, kPerformance, out, Size{1280, 720}, min, max));
    CHECK(!render_size(s, kUltraPerformance, out, Size{853, 480}, min, max));
    s.mode_scale[2] = 0.6f;
    CHECK(render_size(s, kPerformance, out, Size{1280, 720}, min, max) == Size{1536, 864});
    CHECK(!render_size(s, kUltraQuality, out, Size{1970, 1108}, min, max));
    CHECK(!render_size(s, kDlaa, out, out, min, max));
    // no range from DLSS: only the output bounds it; no output: nothing
    s.dlss_scale = DlssScale::custom;
    s.custom_scale = 1.0f;
    CHECK(render_size(s, kQuality, out, dlss_q, Size{}, Size{}) == out);
    CHECK(!render_size(s, kQuality, Size{}, dlss_q, min, max));
    // whether DLSS's range limited the scale (the Graphics page says so)
    s.custom_scale = 0.33f;
    auto pick = pick_render_size(s, kPerformance, out, Size{1280, 720}, min, max);
    CHECK(pick.limited && pick.set == Size{853, 480} && pick.wanted == Size{845, 475});
    pick = pick_render_size(s, kUltraPerformance, out, Size{853, 480}, min, max);
    CHECK(pick.limited && !pick.set);  // limited to DLSS's own size: nothing set, still said
    s.custom_scale = 0.75f;
    pick = pick_render_size(s, kQuality, out, dlss_q, min, max);
    CHECK(!pick.limited && pick.set == Size{1920, 1080});
    pick = pick_render_size(s, kDlaa, out, out, min, max);
    CHECK(!pick.limited && !pick.set && pick.wanted == Size{});
    // a narrow range for a mode: the scale does little there
    pick = pick_render_size(s, kQuality, out, dlss_q, Size{1600, 900}, Size{1800, 1013});
    CHECK(pick.limited && pick.set == Size{1800, 1013});
    CHECK(mode_index(kQuality) == 0 && mode_index(kUltraPerformance) == 3 && mode_index(kDlaa) == -1 && mode_index(kUltraQuality) == -1);
    CHECK(std::string(mode_name(kBalanced)) == "Balanced" && std::string(mode_name(42)) == "?");
}

static void aside(const char* tmp_root) {
    namespace fs = std::filesystem;
    const fs::path game = fs::path(tmp_root) / "graphics-aside-game";
    fs::remove_all(game);
    fs::create_directories(game);
    auto touch = [&](const fs::path& rel) {
        fs::create_directories((game / rel).parent_path());
        std::ofstream(game / rel) << "x";
    };
    // the game's own files: nothing
    touch("enshrouded.exe");
    touch("nvngx_dlss.dll");
    AsideFiles a = detect_aside_files(game, false, false);
    CHECK(a.bits == 0 && a.files.empty());
    CHECK(detect_aside_files(fs::path(), true, false).bits == kAsideOptiScaler);
    // what the overlay guard found counts
    a = detect_aside_files(game, true, true);
    CHECK(a.bits == (kAsideOptiScaler | kAsideStreamline) && a.files.empty());
    touch("Streamline/sl.interposer.dll");
    touch("EnshroudedVKFG.ini");
    a = detect_aside_files(game, false, false);
    CHECK(a.bits == (kAsideStreamline | kAsideVkfg));
    CHECK(a.files.size() == 2 && a.files[0] == "Streamline/sl.interposer.dll" && a.files[1] == "EnshroudedVKFG.ini");
    CHECK((a.bits & kAsideCap) == a.bits);
    fs::remove_all(game / "Streamline");
    fs::remove(game / "EnshroudedVKFG.ini");
    touch("dlsstweaks.ini");
    touch("nvngx.dll");
    touch("_nvngx.dll");
    a = detect_aside_files(game, false, false);
    CHECK(a.bits == (kAsideDlssTweaks | kAsideNvngx) && a.files.size() == 3);
    CHECK((a.bits & kAsideCap) == 0);  // the frame cap stays for these: they only touch DLSS
    CHECK((a.bits & kAsideDlss) == a.bits);
    CHECK(aside_names(kAsideOptiScaler | kAsideDlssTweaks) == "OptiScaler, DLSSTweaks");
    CHECK(aside_names(0).empty());
    fs::remove_all(game);
    // a hook somebody else put on a function's entry
    uint8_t plain[16] = {0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48, 0x83, 0xEC, 0x20};
    CHECK(!patched_entry(plain));
    uint8_t e9[16] = {0xE9, 1, 2, 3, 4};
    uint8_t ff25[16] = {0xFF, 0x25, 0, 0, 0, 0};
    uint8_t movjmp[16] = {0x48, 0xB8, 1, 2, 3, 4, 5, 6, 7, 8, 0xFF, 0xE0};
    uint8_t r11[16] = {0x49, 0xBB, 1, 2, 3, 4, 5, 6, 7, 8, 0x41, 0xFF, 0xE3};
    CHECK(patched_entry(e9) && patched_entry(ff25) && patched_entry(movjmp) && patched_entry(r11));
}

// A stand-in for NGX's parameter object: each slot records which one ran.
struct FakeParams;
static std::string g_last;
static std::map<std::string, uint64_t> g_values;
struct FakeParams {
    void* const* vt;
};
static void f_set_ptr(void*, const char* n, void* v) { g_last = "SetVoidPointer"; g_values[n] = reinterpret_cast<uintptr_t>(v); }
static void f_set_i(void*, const char* n, int32_t v) { g_last = "SetI"; g_values[n] = static_cast<uint64_t>(static_cast<int64_t>(v)); }
static void f_set_ui(void*, const char* n, uint32_t v) { g_last = "SetUI"; g_values[n] = v; }
static ngx::Result f_get_ptr(const void*, const char* n, void** out) {
    g_last = "GetVoidPointer";
    auto it = g_values.find(n);
    if (it == g_values.end()) return ngx::kFail;
    *out = reinterpret_cast<void*>(static_cast<uintptr_t>(it->second));
    return 1;
}
static ngx::Result f_get_i(const void*, const char* n, int32_t* out) {
    g_last = "GetI";
    auto it = g_values.find(n);
    if (it == g_values.end()) return ngx::kFail;
    *out = static_cast<int32_t>(it->second);
    return 1;
}
static ngx::Result f_get_ui(const void*, const char* n, uint32_t* out) {
    g_last = "GetUI";
    auto it = g_values.find(n);
    if (it == g_values.end()) return static_cast<ngx::Result>(0xBAD00005u);
    *out = static_cast<uint32_t>(it->second);
    return 1;
}
static void f_trap() { std::fprintf(stderr, "a slot Kiln never calls was called\n"); std::exit(1); }

static void ngx_slots() {
    void* vt[17];
    for (auto& s : vt) s = reinterpret_cast<void*>(&f_trap);
    vt[ngx::SetVoidPointer] = reinterpret_cast<void*>(&f_set_ptr);
    vt[ngx::SetI] = reinterpret_cast<void*>(&f_set_i);
    vt[ngx::SetUI] = reinterpret_cast<void*>(&f_set_ui);
    vt[ngx::GetVoidPointer] = reinterpret_cast<void*>(&f_get_ptr);
    vt[ngx::GetI] = reinterpret_cast<void*>(&f_get_i);
    vt[ngx::GetUI] = reinterpret_cast<void*>(&f_get_ui);
    FakeParams p{vt};
    // the slots the game's own helpers use (docs/re/client-upscale.md): SetUI +0x20, GetVoidPointer +0x40, GetUI +0x60
    CHECK(ngx::SetUI * 8 == 0x20 && ngx::SetI * 8 == 0x18 && ngx::SetF * 8 == 0x30 && ngx::SetULL * 8 == 0x38);
    CHECK(ngx::GetVoidPointer * 8 == 0x40 && ngx::GetI * 8 == 0x58 && ngx::GetUI * 8 == 0x60 && ngx::GetF * 8 == 0x70);
    ngx::set_ui(&p, ngx::kOutWidth, 1920);
    CHECK(g_last == "SetUI" && g_values[ngx::kOutWidth] == 1920);
    ngx::set_i(&p, ngx::kPerfQualityValue, 2);
    CHECK(g_last == "SetI");
    int x = 0;
    ngx::set_ptr(&p, ngx::kOptimalSettingsCallback, &x);
    CHECK(g_last == "SetVoidPointer");
    void* got = nullptr;
    CHECK(ngx::ok(ngx::get_ptr(&p, ngx::kOptimalSettingsCallback, &got)) && got == &x && g_last == "GetVoidPointer");
    uint32_t w = 0;
    CHECK(ngx::ok(ngx::get_ui(&p, ngx::kOutWidth, &w)) && w == 1920 && g_last == "GetUI");
    int32_t q = -1;
    CHECK(ngx::ok(ngx::get_i(&p, ngx::kPerfQualityValue, &q)) && q == 2 && g_last == "GetI");
    CHECK(!ngx::ok(ngx::get_ui(&p, "missing", &w)));
    CHECK(ngx::ok(1) && !ngx::ok(ngx::kFail) && !ngx::ok(static_cast<ngx::Result>(0xBAD00007u)));
    CHECK(std::string(ngx::preset_hint_for(2)) == "DLSS.Hint.Render.Preset.Quality" && !ngx::preset_hint_for(5));
}

int main(int argc, char** argv) {
    pacer_holds_the_rate();
    pacer_never_holds_a_slower_game();
    pacer_resyncs_after_a_hitch();
    pacer_settings();
    pacer_edges();
    wait_plans();
    timer_margin();
    stats();
    settings_parse();
    presets();
    render_sizes();
    aside(argc > 1 ? argv[1] : ".");
    ngx_slots();
    std::printf("graphics_test: %d checks passed\n", g_checks);
    return 0;
}
