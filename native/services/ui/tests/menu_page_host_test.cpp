// SPDX-License-Identifier: MIT
// The main-menu page (menu_page.cpp) against stand-ins for the game, under ASan/UBSan: a fake PreGameGoTo that writes the
// page as the game does, a fake Credits draw function, a fake UI system block with the canvas metrics, and kiln.hooks'
// test RawHook (caller() is what the test says). Checks: the feature missing changes nothing; the MODS entry calls GoTo
// once with the PreGame block and the Credits page; the hook stays, leaves once, and passes other callers through; the
// watchdog; the game leaving by itself (an invite); the menu key; the published rectangle and the error popup.
#define KILN_UI_PAGE_TESTS 1
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <map>
#include <set>
#include <string>
#include <vector>

#include "../menu_page.cpp"

namespace kui {
const KilnApi* g_api = nullptr;
KilnOwner g_owner = 7;
std::atomic<bool> g_menu_open{false};
static uint64_t g_now = 100000;
uint64_t now_ms() { return g_now; }
static std::vector<std::string> g_log;
void logf(int, const char* fmt, ...) {
    char b[512];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(b, sizeof b, fmt, ap);
    va_end(ap);
    g_log.emplace_back(b);
}
void page_reset_for_tests();
}  // namespace kui

static int g_fail = 0, g_checks = 0;
#define CHECK(cond)                                                     \
    do {                                                                \
        ++g_checks;                                                     \
        if (!(cond)) {                                                  \
            std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond); \
            ++g_fail;                                                   \
        }                                                               \
    } while (0)

// ---- the fake game ----
alignas(16) static uint8_t g_pregame[0x250];  // the PreGame block: page at +0x24c, the main-menu state at +8
alignas(16) static uint8_t g_system[0x800];   // the UI system: GameUiFrame gets system + 0x10
static int32_t& page_field() { return *reinterpret_cast<int32_t*>(g_pregame + 0x24c); }
static int g_goto_calls = 0, g_credits_calls = 0, g_sound_calls = 0;
static uintptr_t g_goto_block = 0;
static int32_t g_goto_page = -1;
static bool g_goto_throws = false, g_credits_result = true;
static void fake_goto(uintptr_t pregame, int32_t page) {
    ++g_goto_calls;
    if (g_goto_throws) throw 1;  // a fault (the stub's __try is try)
    g_goto_block = pregame;
    g_goto_page = page;
    *reinterpret_cast<int32_t*>(pregame + 0x24c) = page;
}
static bool fake_credits() {
    ++g_credits_calls;
    return g_credits_result;
}
// UiPlaySound(ctx, &sound) and the UiApp global it reads: ctx -> config (+0xc8) -> sound (+0x124f0)
alignas(16) static uint8_t g_config[0x12600];
alignas(16) static uint8_t g_ctx[0x100];
static uintptr_t g_ui_app = 0;  // the global: holds the context pointer
static uint64_t g_sound_seen = 0;
static void fake_play_sound(uintptr_t ctx, const void* sound) {
    ++g_sound_calls;
    if (ctx == reinterpret_cast<uintptr_t>(g_ctx)) std::memcpy(&g_sound_seen, sound, 8);
}
static constexpr uintptr_t kCreditsRet = 0x140c6aac5, kEndCreditsRet = 0x140bd1578;

// ---- the fake KilnApi (sigdb) ----
static std::set<std::string> g_features;
static std::map<std::string, uintptr_t> g_symbols;
static std::map<std::string, int64_t> g_constants;
static std::string str(KilnStr s) { return std::string(s.ptr, s.len); }
static bool KILN_CALL api_feature(KilnStr n) { return g_features.count(str(n)) > 0; }
static KilnResult KILN_CALL api_symbol(KilnStr n, uintptr_t* out) {
    auto it = g_symbols.find(str(n));
    if (it == g_symbols.end()) return KILN_ERR_NOT_FOUND;
    *out = it->second;
    return KILN_OK;
}
static KilnResult KILN_CALL api_constant(KilnStr n, int64_t* out) {
    auto it = g_constants.find(str(n));
    if (it == g_constants.end()) return KILN_ERR_NOT_FOUND;
    *out = it->second;
    return KILN_OK;
}

static void sigdb_26430625() {
    g_features = {"ui.menu_page", "ui.menu_page_sound", "ui.error_popup"};
    g_symbols = {{"ui/PreGameGoTo", reinterpret_cast<uintptr_t>(&fake_goto)},
                 {"ui/PreGameCredits", reinterpret_cast<uintptr_t>(&fake_credits)},
                 {"ui/PreGameCreditsRet", kCreditsRet},
                 {"ui/UiApp", reinterpret_cast<uintptr_t>(&g_ui_app)},
                 {"ui/UiPlaySound", reinterpret_cast<uintptr_t>(&fake_play_sound)}};
    g_constants = {{"ui/pregame.page", 0x24c},   {"ui/pregame.page_credits", 6}, {"ui/pregame.page_main_menu", 3},
                   {"ui/pregame.main_menu", 8},  {"ui/sys.frame_arg", 0x10},     {"ui/sys.canvas_w", 0x30},
                   {"ui/sys.canvas_h", 0x34},    {"ui/sys.viewport", 0x38},      {"ui/sys.px_per_unit", 0x48},
                   {"ui/ui.config", 0xc8},       {"ui/cfg.sound_entry_cancel", 0x124f0},
                   {"ui/sys.popups", 0x120},     {"ui/popups.error", 0x550}};
}

static void set_metrics(float cw, float ch, float vx, float vy, float vw, float vh, float ppu) {
    auto put = [](size_t at, float v) { std::memcpy(g_system + at, &v, 4); };
    put(0x30, cw), put(0x34, ch), put(0x38, vx), put(0x3c, vy), put(0x40, vw), put(0x44, vh), put(0x48, ppu);
}
static uintptr_t sys10() { return reinterpret_cast<uintptr_t>(g_system) + 0x10; }
static void* entry_state() { return g_pregame + 8; }

// One UI frame of the PreGame page switch as the game runs it: the page's draw function (the Credits hook when the page
// is Credits), then "if false: GoTo(MainMenu)", then the after-frame.
static uint64_t g_frame = 0;
static bool g_credits_hook_ran = false, g_credits_hook_result = false;
static void ui_frame(bool main_menu_draws_entries = true) {
    g_credits_hook_ran = false;
    if (page_field() == 6) {
        kiln::test::caller = kCreditsRet;
        g_credits_hook_ran = true;
        g_credits_hook_result = kui::hk_credits();
        if (!g_credits_hook_result) fake_goto(reinterpret_cast<uintptr_t>(g_pregame), 3);
    } else if (page_field() == 3 && main_menu_draws_entries) {
        kui::page_main_menu_drawn(entry_state());
    }
    kui::page_after_frame(sys10(), ++g_frame);
    kui::g_now += 16;
}

static void fresh(bool host = true) {
    kui::page_reset_for_tests();
    kui::g_menu_open = false;
    std::memset(g_pregame, 0, sizeof g_pregame);
    page_field() = 3;
    g_goto_calls = g_credits_calls = g_sound_calls = 0;
    g_goto_block = 0;
    g_goto_page = -1;
    g_goto_throws = false;
    g_credits_result = true;
    g_frame = 0;
    set_metrics(1920, 1080, 0, 0, 1920, 1080, 1);
    g_system[0x10 + 0x120 + 0x550] = 0;
    CHECK(kui::page_install(true));
    if (host) kui::page_host(1);
    ui_frame();
}

static KilnUiMenuPage state() {
    KilnUiMenuPage p{};
    p.size = sizeof p;
    kui::page_state(&p);
    return p;
}

int main() {
    KilnApi api{};
    api.feature = &api_feature;
    api.symbol = &api_symbol;
    api.constant = &api_constant;
    kui::g_api = &api;
    // UiApp -> ctx -> config: the cancel sound's first 8 bytes are a marker
    *reinterpret_cast<uintptr_t*>(g_ctx + 0xc8) = reinterpret_cast<uintptr_t>(g_config);
    g_ui_app = reinterpret_cast<uintptr_t>(g_ctx);
    const uint64_t marker = 0x5eed5eedull;
    std::memcpy(g_config + 0x124f0, &marker, 8);

    // ---- feature missing (another build): no hook, the MODS entry and the keys keep today's behaviour ----
    g_features.clear();
    g_symbols.clear();
    g_constants.clear();
    kui::page_reset_for_tests();
    const int installs = kiln::test::installs;
    CHECK(!kui::page_install(true));
    CHECK(kiln::test::installs == installs);
    kui::page_host(1);
    CHECK(!kui::page_usable());
    CHECK(!kui::page_open_from_entry(entry_state()));
    CHECK(!kui::page_menu_key());
    CHECK(!kui::page_escape());
    CHECK(!kui::g_page_capture.load());
    {
        KilnUiMenuPage p{};
        p.size = sizeof p;
        CHECK(kui::page_state(&p) == KILN_ERR_UNAVAILABLE && !p.available && p.state == KILN_UI_PAGE_HIDDEN);
        p.size = 8;
        CHECK(kui::page_state(&p) == KILN_ERR_INVALID_ARGUMENT);
    }
    // one symbol missing is the same
    sigdb_26430625();
    g_symbols.erase("ui/PreGameCreditsRet");
    CHECK(!kui::page_install(true));
    sigdb_26430625();
    CHECK(!kui::page_install(false));  // no main-menu entry hook: nothing to open the page with
    // the hook refused by kiln.hooks
    kiln::test::fail_target = reinterpret_cast<uintptr_t>(&fake_credits);
    CHECK(!kui::page_install(true));
    kiln::test::fail_target = 0;
    // implausible constants
    g_constants["ui/pregame.page_credits"] = 3;
    CHECK(!kui::page_install(true));
    sigdb_26430625();

    // ---- no host yet (an older Kiln.Ui): the MODS entry opens the stand-alone menu ----
    fresh(false);
    CHECK(!kui::page_usable());
    CHECK(!kui::page_open_from_entry(entry_state()) && g_goto_calls == 0);
    kui::page_host(1);
    CHECK(kui::page_usable());

    // ---- open from the MODS entry ----
    fresh();
    kui::g_menu_open = true;  // a stand-alone menu somehow open: one host at a time
    CHECK(kui::page_open_from_entry(entry_state()));
    CHECK(g_goto_calls == 1 && g_goto_page == 6 && g_goto_block == reinterpret_cast<uintptr_t>(g_pregame));
    CHECK(!kui::g_menu_open.load());
    CHECK(kui::page_active() && kui::g_page_capture.load());
    CHECK(state().state == KILN_UI_PAGE_OPENING && state().opened_by == KILN_UI_PAGE_BY_ENTRY);
    CHECK(!kui::page_open_from_entry(entry_state()) && g_goto_calls == 1);  // a second activation does nothing
    kui::page_after_frame(sys10(), ++g_frame);  // the rest of the frame
    // next frame: the page switch calls Credits; the hook stays and the game's Credits does not run
    ui_frame();
    CHECK(g_credits_hook_ran && g_credits_hook_result && g_credits_calls == 0);
    {
        const KilnUiMenuPage p = state();
        CHECK(p.state == KILN_UI_PAGE_SHOWN && p.shown_frame == g_frame && p.available);
        CHECK(p.metrics && p.pixels_per_unit == 1 && p.canvas_width == 1920);
        CHECK(p.x0 == 80 && p.y0 == 50 && p.x1 == 1840 && p.y1 == 1030);
        CHECK(p.pad_left == 80 && p.pad_top == 50 && !p.error_popup);
    }
    for (int i = 0; i < 20; ++i) {
        kui::page_drawn();
        ui_frame();
        CHECK(g_credits_hook_result && g_credits_calls == 0);
    }
    // other callers pass through while the page shows: the end-game credits, a mod calling it, no caller at all
    kiln::test::caller = kEndCreditsRet;
    g_credits_result = false;
    CHECK(!kui::hk_credits() && g_credits_calls == 1);
    g_credits_result = true;
    kiln::test::caller = 0;
    CHECK(kui::hk_credits() && g_credits_calls == 2);
    // the window grows to 3440x1440: the rectangle follows (canvas 2580x1080 units at 1.333 px/unit)
    set_metrics(2580, 1080, 0, 0, 3440, 1440, 1440.0f / 1080.0f);
    ui_frame();
    {
        const KilnUiMenuPage p = state();
        CHECK(p.metrics && p.x0 > 106 && p.x0 < 107 && p.y0 > 66 && p.y0 < 67 && p.x1 > 3333 && p.x1 < 3334 && p.y1 > 1373 && p.y1 < 1374);
    }
    // garbage metrics are not published as metrics
    set_metrics(0, 0, 0, 0, 0, 0, 0);
    ui_frame();
    CHECK(!state().metrics && state().state == KILN_UI_PAGE_SHOWN);
    set_metrics(1920, 1080, 0, 0, 1920, 1080, 1);
    // an error popup
    g_system[0x10 + 0x120 + 0x550] = 1;
    ui_frame();
    CHECK(state().error_popup == 1);
    CHECK(!kui::g_page_capture.load() && kui::page_active());  // the game gets the input for its popup
    CHECK(!kui::page_escape());                                 // and Escape
    for (int i = 0; i < 100; ++i) ui_frame();                   // no host draws meanwhile: no watchdog
    CHECK(kui::page_active() && kui::page_usable());
    g_system[0x10 + 0x120 + 0x550] = 0;
    ui_frame();
    CHECK(state().error_popup == 0 && kui::g_page_capture.load());

    // ---- Escape: leave once; the game goes back itself, with its back sound ----
    CHECK(kui::page_escape());
    CHECK(kui::page_active());  // until the game's next page switch
    g_sound_seen = 0;
    ui_frame();
    CHECK(g_credits_hook_ran && !g_credits_hook_result && g_credits_calls == 2);
    CHECK(g_sound_calls == 1 && g_sound_seen == marker);
    CHECK(page_field() == 3 && !kui::page_active() && !kui::g_page_capture.load());
    CHECK(state().state == KILN_UI_PAGE_HIDDEN);
    CHECK(!kui::page_escape());  // Escape is the game's again
    ui_frame();
    // the real Credits entry afterwards: the game's Credits runs
    fake_goto(reinterpret_cast<uintptr_t>(g_pregame), 6);
    ui_frame();
    CHECK(g_credits_hook_ran && g_credits_calls == 3 && g_credits_hook_result);
    g_credits_result = false;  // the player backs out of the real credits
    ui_frame();
    CHECK(g_credits_calls == 4 && page_field() == 3);
    g_credits_result = true;
    // and MODS again
    CHECK(kui::page_open_from_entry(entry_state()) && g_goto_calls == 5);
    ui_frame();
    ui_frame();
    CHECK(state().state == KILN_UI_PAGE_SHOWN && g_credits_calls == 4);
    // the menu key on the page leaves too; a leave from any thread (the page's Back control)
    CHECK(kui::page_menu_key());
    ui_frame();
    CHECK(!kui::page_active() && page_field() == 3);

    // ---- the current page is not the main menu: nothing happens ----
    fresh();
    page_field() = 5;
    CHECK(!kui::page_open_from_entry(entry_state()) && g_goto_calls == 0 && !kui::page_active());

    // ---- the game's GoTo faults: give up, the stand-alone menu opens from now on ----
    fresh();
    g_goto_throws = true;
    CHECK(!kui::page_open_from_entry(entry_state()) && !kui::page_active() && !kui::page_usable());
    g_goto_throws = false;

    // ---- watchdog: no host drew the page ----
    fresh();
    CHECK(kui::page_open_from_entry(entry_state()));
    kui::page_after_frame(sys10(), ++g_frame);
    ui_frame();
    CHECK(state().state == KILN_UI_PAGE_SHOWN);
    int frames = 0;
    while (kui::page_active() && frames < 200) {
        ui_frame();
        ++frames;
    }
    CHECK(frames > 30 && frames < 100);  // 30 UI frames and a second without page_drawn
    CHECK(!kui::page_active() && page_field() == 3);
    CHECK(!kui::page_usable());          // given up for the session
    CHECK(!kui::page_open_from_entry(entry_state()));
    CHECK(!state().available);

    // ---- the game changes the page by itself (an invite): the hook stops being called ----
    fresh();
    CHECK(kui::page_open_from_entry(entry_state()));
    kui::page_after_frame(sys10(), ++g_frame);
    ui_frame();
    CHECK(state().state == KILN_UI_PAGE_SHOWN);
    page_field() = 3;  // the invite path writes the page directly (0xc6a894), no GoTo
    const int gotos = g_goto_calls;
    ui_frame(false);
    ui_frame(false);
    CHECK(kui::page_active());  // a frame or two without the hook is not yet a close
    ui_frame(false);
    CHECK(!kui::page_active() && !kui::g_page_capture.load() && g_goto_calls == gotos);
    // a leave asked for after that changes nothing
    kui::page_leave();
    ui_frame();
    CHECK(g_goto_calls == gotos && !kui::page_active());

    // ---- the page never appears after GoTo (the game moved on the same frame) ----
    fresh();
    CHECK(kui::page_open_from_entry(entry_state()));
    page_field() = 3;
    for (int i = 0; i < 3; ++i) ui_frame(false);
    CHECK(kui::page_active());
    ui_frame(false);
    CHECK(!kui::page_active());

    // ---- a leave before the page appeared: the first hook call leaves ----
    fresh();
    CHECK(kui::page_open_from_entry(entry_state()));
    kui::page_leave();
    kui::page_after_frame(sys10(), ++g_frame);
    ui_frame();
    CHECK(g_credits_hook_ran && !g_credits_hook_result && page_field() == 3 && !kui::page_active());

    // ---- the menu key in the main menu ----
    fresh();
    CHECK(kui::page_menu_key());  // the main menu drew its entries last frame
    CHECK(!kui::page_active());   // it opens inside the next main-menu draw
    ui_frame();
    CHECK(kui::page_active() && g_goto_calls == 1 && state().opened_by == KILN_UI_PAGE_BY_KEY);
    ui_frame();
    CHECK(state().state == KILN_UI_PAGE_SHOWN);
    CHECK(kui::page_menu_key());  // again: leave
    ui_frame();
    CHECK(!kui::page_active());
    // the main menu's entries are not drawn (another page, character selection): the key opens the stand-alone menu
    fresh();
    page_field() = 5;
    ui_frame();
    ui_frame();
    CHECK(!kui::page_menu_key() && g_goto_calls == 0);
    // a request the main menu never takes falls back to the stand-alone menu
    fresh();
    CHECK(kui::page_menu_key());
    for (int i = 0; i < 3; ++i) ui_frame(false);
    CHECK(!kui::g_menu_open.load());
    ui_frame(false);
    CHECK(kui::g_menu_open.load() && !kui::page_active() && g_goto_calls == 0);
    // the stand-alone menu open: the key closes it (ui.cpp toggles), not the page
    fresh();
    kui::g_menu_open = true;
    CHECK(!kui::page_menu_key());
    // the host going away leaves the page
    fresh();
    CHECK(kui::page_open_from_entry(entry_state()));
    ui_frame();
    ui_frame();
    kui::page_host(0);
    ui_frame();
    CHECK(!kui::page_active() && !kui::page_usable());

    char line[256];
    kui::page_status_line(line, sizeof line);
    CHECK(std::strstr(line, "main-menu page on") != nullptr);

    std::printf("menu_page: %d checks, %d failed\n", g_checks, g_fail);
    return g_fail ? 1 : 0;
}
