// SPDX-License-Identifier: MIT
// kiln.ui: the game's own UI (docs/re/game-ui.md, sigdb/symbols/client.d/ui.json).
//  - GameUiFrame: the game's UI update, once per UI frame on the UI thread (also in the main menu). After it Kiln reads
//    the UI state (keen::UiState, keen::HudScreenType, the interaction entity, the crafting menu's selection, the
//    text language), delivers menu-entry activations, screen changes and UI ticks.
//  - MainMenuEntry / SystemMenuEntry: the game's menu-entry renderers (immediate mode: the call draws the entry, takes
//    part in focus and gamepad/keyboard navigation and returns true on activation). Called from the menu builders'
//    Settings entry (identified by the call's return address), Kiln first draws its own entries through the same
//    renderer. Technique credit: ShroudForge 1.7.6 (MIT, modules/game-runtime/native/
//    main-menu/manage_native_main_menu.cpp), which put a "ShroudForge" entry before the main menu's Settings entry
//    the same way (there by patching the two call sites; here by one inline hook on the renderer).
//  - CraftingMenu: the crafting menu builder, every frame while a crafting menu is open: station entity and the
//    selected recipe.
#include <windows.h>
#include <intrin.h>

#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include "kiln_hook.hpp"
#include "ui.h"

namespace kui {
namespace {

// ---- layout (sigdb) ----
struct Layout {
    uintptr_t ui_app = 0;           // ui/UiApp: the UI frame context while a UI frame runs, else stale/null
    int64_t ui_data = 0;            // ctx + ui.data -> GameUiData*
    int64_t sys_data = 0;           // GameUiFrame's first argument + sys.data -> GameUiData*
    int64_t state_ui = 0;           // *(GameUiData) = state; + state.ui_state: u8 keen::UiState
    int64_t state_play = 0;         // state + state.play -> play data
    int64_t play_hud = 0;           // play + play.hud_screen: u8 keen::HudScreenType
    int64_t play_interaction = 0;   // play + play.interaction: u32 entity
    int64_t data_loca = 0;          // GameUiData + data.loca_pool -> LocaPool*
    int64_t loca_language = 0;      // LocaPool + content/loca.language: u32 keen::LanguageId
    int64_t craft_recipe = 0;       // GameUiData + data.craft_recipe: u32 (the player's own crafting menu)
    int64_t station_recipe = 0;     // GameUiData + data.station_recipe -> u32* (a station's)
    uintptr_t main_ret_a = 0, main_ret_b = 0, sys_ret = 0;
    bool screens = false, main_menu = false, system_menu = false, crafting = false, language = false;
} L;

// ---- the game's text and menu-entry arguments ----
struct GameText {  // the UI's string view: UTF-8, byte length, FNV-1a 64 of the bytes (sign-extended, like the game)
    const char* p;
    uint64_t n;
    uint64_t fnv;
};
static_assert(sizeof(GameText) == 24);
struct MainDesc {  // MainMenuEntry's descriptor: text + 8 option bytes (copied from the entry Kiln draws before)
    GameText text;
    uint8_t options[8];
};
static_assert(sizeof(MainDesc) == 32);

uint64_t fnv64(const char* p, size_t n) {
    uint64_t h = 0xcbf29ce484222325ull;
    for (size_t i = 0; i < n; ++i) {
        h ^= static_cast<uint64_t>(static_cast<int64_t>(static_cast<int8_t>(p[i])));
        h *= 0x100000001b3ull;
    }
    return h;
}

// Labels live for the process (the UI may keep a pointer to a drawn string until it renders): interned, never freed.
std::mutex g_text_mu;
std::deque<std::string> g_text_arena;
std::unordered_map<std::string, const std::string*> g_text_index;
const std::string* intern(KilnStr s) {
    std::string v(s.ptr ? s.ptr : "", s.ptr ? s.len : 0);
    std::lock_guard lk(g_text_mu);
    auto it = g_text_index.find(v);
    if (it != g_text_index.end()) return it->second;
    g_text_arena.push_back(v);
    const std::string* p = &g_text_arena.back();
    g_text_index.emplace(v, p);
    return p;
}

struct Entry {
    KilnHandle handle;
    KilnOwner owner;
    uint32_t menu;
    int32_t order;
    uint64_t seq;
    KilnUiMenuFn fn;
    void* user;
    const std::string* text;  // interned
    uint64_t fnv;
    uint32_t flags;
};
std::mutex g_mu;  // entries, subscribers, pending
std::vector<Entry> g_entries;
uint64_t g_seq = 0;
std::atomic<uint64_t> g_entries_version{0};

struct ScreenSub {
    KilnHandle handle;
    KilnOwner owner;
    KilnUiScreenFn fn;
    void* user;
};
struct TickSub {
    KilnHandle handle;
    KilnOwner owner;
    KilnUiTickFn fn;
    void* user;
};
struct FrameTickSub {
    KilnHandle handle;
    KilnOwner owner;
    KilnUiFrameTickFn fn;
    void* user;
};
std::vector<ScreenSub> g_screen_subs;
std::vector<TickSub> g_tick_subs;
std::vector<FrameTickSub> g_ftick_subs;
std::vector<KilnHandle> g_pending;  // activated entries, delivered after the frame
std::atomic<uint64_t> g_draws{0}, g_clicks{0}, g_frames{0};

// last frame's screen (any thread reads a copy)
std::mutex g_screen_mu;
KilnUiScreen g_screen{};
KilnUiScreen g_prev{};
bool g_have_prev = false;

// crafting menu this frame (UI thread only)
struct Craft {
    uint64_t frame = 0;
    uint8_t kind = 0;
    uint32_t station = 0;
    uint32_t recipe = 0;
} g_craft;
std::atomic<uint32_t> g_workshop{0};
std::atomic<uint32_t> g_workshop_station{0xFFFFFFFFu};  // the station the workshop was seen with
thread_local bool t_in_builder = false;                 // inside the crafting menu builder (its list lookups)
thread_local uint32_t t_builder_station = 0;
uint64_t g_frame_no = 0;
DWORD g_ui_thread = 0;

kiln::RawHook g_frame_hook, g_main_hook, g_sys_hook, g_craft_hook;
using MainEntryFn = bool(__fastcall*)(void*, const MainDesc*);
using SysEntryFn = bool(__fastcall*)(const GameText*, uint8_t, uint8_t);

// ---- guarded reads (no C++ objects in these frames: __try) ----
template <typename T>
bool rd(uintptr_t a, T& out) {
    if (a < 0x10000) return false;
    __try {
        out = *reinterpret_cast<const T*>(a);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

struct Raw {
    uint8_t ui_state, hud;
    uint32_t interaction, language;
};

bool read_state(uintptr_t sys10, Raw* r) {
    r->ui_state = r->hud = 0;
    r->interaction = r->language = 0;
    uintptr_t data = 0, state = 0, play = 0;
    if (!rd(sys10 + L.sys_data, data) || !data) return false;
    if (!rd(data, state) || !state) return true;  // no state yet
    rd(state + L.state_ui, r->ui_state);
    if (rd(state + L.state_play, play) && play && r->ui_state == KILN_UI_STATE_PLAY) {
        rd(play + L.play_hud, r->hud);
        rd(play + L.play_interaction, r->interaction);
    }
    if (L.language) {
        uintptr_t pool = 0;
        if (rd(data + L.data_loca, pool) && pool) rd(pool + L.loca_language, r->language);
    }
    return true;
}

// The crafting menu's selected recipe, right after its builder ran (the UI context is still current).
uint32_t read_craft_recipe(uint32_t station) {
    uintptr_t ctx = 0, data = 0;
    if (!rd(L.ui_app, ctx) || !ctx || !rd(ctx + L.ui_data, data) || !data) return 0;
    uint32_t id = 0;
    if (!station) {
        rd(data + L.craft_recipe, id);
    } else {
        uintptr_t slot = 0;
        if (rd(data + L.station_recipe, slot) && slot) rd(slot, id);
    }
    return id;
}

// ---- guarded subscriber calls ----
bool call_entry(KilnUiMenuFn fn, KilnHandle h, void* user) {
    __try {
        fn(h, user);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool call_screen(KilnUiScreenFn fn, const KilnUiScreen* a, const KilnUiScreen* b, void* user) {
    __try {
        fn(a, b, user);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool call_tick(KilnUiTickFn fn, const KilnUiScreen* a, void* user) {
    __try {
        fn(a, user);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool call_ftick(KilnUiFrameTickFn fn, uint64_t frame, void* user) {
    __try {
        fn(frame, user);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool same_screen(const KilnUiScreen& a, const KilnUiScreen& b) {
    return a.ui_state == b.ui_state && a.hud_screen == b.hud_screen && a.crafting == b.crafting &&
           a.interaction == b.interaction && a.station == b.station && a.workshop == b.workshop && a.recipe == b.recipe &&
           a.language == b.language;
}

const char* state_name(uint8_t s) {
    static const char* names[] = {"None", "Preload", "PreGame", "Load", "LoreIntro", "LoreOutro", "Credits", "Play"};
    return s < 8 ? names[s] : "?";
}

// The entries of one menu, in order, visible only (snapshot under the lock).
std::vector<Entry> entries_of(uint32_t menu) {
    std::vector<Entry> out;
    std::lock_guard lk(g_mu);
    for (const Entry& e : g_entries)
        if (e.menu == menu && !(e.flags & KILN_UI_ENTRY_HIDDEN) && e.text && !e.text->empty()) out.push_back(e);
    std::stable_sort(out.begin(), out.end(), [](const Entry& a, const Entry& b) {
        return a.order != b.order ? a.order < b.order : a.seq < b.seq;
    });
    return out;
}

void activated(KilnHandle h) {
    g_clicks.fetch_add(1, std::memory_order_relaxed);
    std::lock_guard lk(g_mu);
    if (g_pending.size() < 64) g_pending.push_back(h);
}

// ---- hooks ----
bool __fastcall hk_main_entry(void* state, const MainDesc* d) {
    const uintptr_t r = g_main_hook.caller();  // the game's call site, read before the original
    if (d && (r == L.main_ret_a || r == L.main_ret_b)) {
        for (const Entry& e : entries_of(KILN_UI_MENU_MAIN)) {
            MainDesc c = *d;  // the Settings entry's option bytes: same style, same behaviour
            c.text = GameText{e.text->c_str(), e.text->size(), e.fnv};
            g_draws.fetch_add(1, std::memory_order_relaxed);
            if (g_main_hook.call<bool>(state, &c)) activated(e.handle);
        }
    }
    return g_main_hook.call<bool>(state, d);
}

bool __fastcall hk_sys_entry(const GameText* t, uint8_t p2, uint8_t attention) {
    const uintptr_t ret = g_sys_hook.caller();  // the game's call site, read before the original
    if (t && ret == L.sys_ret) {
        for (const Entry& e : entries_of(KILN_UI_MENU_PAUSE)) {
            const GameText c{e.text->c_str(), e.text->size(), e.fnv};
            g_draws.fetch_add(1, std::memory_order_relaxed);
            if (g_sys_hook.call<bool>(&c, p2, static_cast<uint8_t>((e.flags & KILN_UI_ENTRY_ATTENTION) ? 1 : 0)))
                activated(e.handle);
        }
    }
    return g_sys_hook.call<bool>(t, p2, attention);
}

void* __fastcall hk_craft(uint64_t packed, int32_t select) {
    const uint32_t station = static_cast<uint32_t>(packed >> 32);
    t_in_builder = true;
    t_builder_station = station;
    void* r = g_craft_hook.call<void*>(packed, select);
    t_in_builder = false;
    g_craft.frame = g_frame_no + 1;  // the frame being built (g_frame_no counts finished frames)
    g_craft.kind = ((packed >> 8) & 0xFF) == 1 ? KILN_UI_CRAFTING_JOURNAL : KILN_UI_CRAFTING_MENU;
    g_craft.station = station;
    g_craft.recipe = read_craft_recipe(station);
    return r;
}

void after_frame(uintptr_t sys10) {
    ++g_frame_no;
    g_frames.store(g_frame_no, std::memory_order_relaxed);
    g_ui_thread = GetCurrentThreadId();
    Raw raw{};
    const bool ok = read_state(sys10, &raw);
    KilnUiScreen s{};
    s.size = sizeof s;
    s.available = ok ? 1 : 0;
    s.ui_state = raw.ui_state;
    s.hud_screen = raw.ui_state == KILN_UI_STATE_PLAY ? raw.hud : 0;
    s.interaction = raw.interaction;
    s.language = raw.language;
    s.ui_thread = g_ui_thread;
    s.frame = g_frame_no;
    // the builder runs every frame while a crafting menu shows; a frame or two without it (a tab transition) is not a close
    if (g_craft.frame && g_craft.frame + 3 > g_frame_no) {
        s.crafting = g_craft.kind;
        s.station = g_craft.station;  // the builder's station argument is the station's workshop id [rt]
        s.recipe = g_craft.recipe;
        s.workshop = g_craft.station;
        if (g_workshop_station.load(std::memory_order_relaxed) == g_craft.station) s.workshop = g_workshop.load(std::memory_order_relaxed);
    }
    KilnUiScreen before{};
    bool changed = false;
    {
        std::lock_guard lk(g_screen_mu);
        before = g_screen;
        changed = !g_have_prev || !same_screen(before, s);
        g_prev = before;
        g_screen = s;
        g_have_prev = true;
    }
    if (changed) {
        logf(KILN_LOG_DEBUG, "ui: game screen %s hud %u interaction %u crafting %u station %u workshop %08x recipe %08x language %u (frame %llu, thread %lu)",
             state_name(s.ui_state), s.hud_screen, s.interaction, s.crafting, s.station, s.workshop, s.recipe, s.language,
             static_cast<unsigned long long>(s.frame), static_cast<unsigned long>(s.ui_thread));
        if (before.ui_state != s.ui_state || before.hud_screen != s.hud_screen)
            logf(KILN_LOG_INFO, "ui: game screen %s/%u -> %s/%u", state_name(before.ui_state), before.hud_screen, state_name(s.ui_state),
                 s.hud_screen);
    }
    // activations of Kiln's menu entries (drawn during this frame)
    std::vector<KilnHandle> pending;
    std::vector<Entry> entries;
    std::vector<ScreenSub> ssubs;
    std::vector<TickSub> tsubs;
    std::vector<FrameTickSub> fsubs;
    {
        std::lock_guard lk(g_mu);
        pending.swap(g_pending);
        if (!pending.empty()) entries = g_entries;
        if (changed) ssubs = g_screen_subs;
        tsubs = g_tick_subs;
        fsubs = g_ftick_subs;
    }
    for (KilnHandle h : pending) {
        for (const Entry& e : entries) {
            if (e.handle != h || !e.fn) continue;
            logf(KILN_LOG_INFO, "ui: menu entry '%s' activated", e.text ? e.text->c_str() : "");
            if (!call_entry(e.fn, h, e.user)) set_error("a menu entry callback faulted");
        }
    }
    if (changed)
        for (const ScreenSub& x : ssubs)
            if (!call_screen(x.fn, &s, &before, x.user)) {
                set_error("a screen callback faulted; removed");
                std::lock_guard lk(g_mu);
                std::erase_if(g_screen_subs, [&](const ScreenSub& y) { return y.handle == x.handle; });
            }
    for (const TickSub& x : tsubs)
        if (!call_tick(x.fn, &s, x.user)) {
            set_error("a UI tick callback faulted; removed");
            std::lock_guard lk(g_mu);
            std::erase_if(g_tick_subs, [&](const TickSub& y) { return y.handle == x.handle; });
        }
    for (const FrameTickSub& x : fsubs)
        if (!call_ftick(x.fn, s.frame, x.user)) {
            set_error("a UI frame tick callback faulted; removed");
            std::lock_guard lk(g_mu);
            std::erase_if(g_ftick_subs, [&](const FrameTickSub& y) { return y.handle == x.handle; });
        }
}

void __fastcall hk_frame(void* sys10, void* p2) {
    g_frame_hook.call<void>(sys10, p2);
    after_frame(reinterpret_cast<uintptr_t>(sys10));
}

// ---- kiln.craftmenu: the workshop of the crafting menu being built ----
void KILN_CALL on_craftmenu(const KilnCraftMenuInfo* info, void*) {
    if (!info) return;
    if (!t_in_builder) return;  // another menu's lookup (the journal's recipe overview)
    g_workshop.store(info->workshop, std::memory_order_relaxed);
    g_workshop_station.store(t_builder_station, std::memory_order_relaxed);
}

// Kiln's own "Mods" entry in both menus: opens the Kiln menu (works without the managed host).
KilnHandle g_builtin[2] = {0, 0};
void KILN_CALL on_builtin(KilnHandle, void*) {
    g_menu_open = true;
    logf(KILN_LOG_DEBUG, "ui: Kiln menu opened from the game's menu");
}
KilnResult game_menu_add_impl(KilnOwner owner, uint32_t menu, int32_t order, KilnStr text, KilnUiMenuFn fn, void* user, KilnHandle* out);
void add_builtin_entries() {
    static const char kMods[] = "Mods";
    if (L.main_menu) game_menu_add_impl(g_owner, KILN_UI_MENU_MAIN, 1000, KilnStr{kMods, 4}, &on_builtin, nullptr, &g_builtin[0]);
    if (L.system_menu) game_menu_add_impl(g_owner, KILN_UI_MENU_PAUSE, 1000, KilnStr{kMods, 4}, &on_builtin, nullptr, &g_builtin[1]);
}

bool sym(const char* n, uintptr_t* out) {
    return g_api->symbol(KilnStr{n, std::strlen(n)}, out) == KILN_OK && *out;
}
bool cst(const char* n, int64_t* out) { return g_api->constant(KilnStr{n, std::strlen(n)}, out) == KILN_OK; }
bool feat(const char* n) { return g_api->feature(KilnStr{n, std::strlen(n)}); }

}  // namespace

bool game_install() {
    uintptr_t frame_fn = 0, main_fn = 0, sys_fn = 0, craft_fn = 0;
    L.screens = feat("ui.screens") && sym("ui/GameUiFrame", &frame_fn) && cst("ui/sys.data", &L.sys_data) &&
                cst("ui/state.ui_state", &L.state_ui) && cst("ui/state.play", &L.state_play) &&
                cst("ui/play.hud_screen", &L.play_hud) && cst("ui/play.interaction", &L.play_interaction);
    if (!L.screens) {
        logf(KILN_LOG_WARN, "ui: game screens unavailable on this build (feature ui.screens off): no menu entries, no screen events");
        return false;
    }
    L.language = feat("ui.language") && cst("ui/data.loca_pool", &L.data_loca) && cst("content/loca.language", &L.loca_language);
    L.main_menu = feat("ui.main_menu") && sym("ui/MainMenuEntry", &main_fn) && sym("ui/MainMenuSettingsA", &L.main_ret_a) &&
                  sym("ui/MainMenuSettingsB", &L.main_ret_b);
    L.system_menu = feat("ui.system_menu") && sym("ui/SystemMenuEntry", &sys_fn) && sym("ui/SystemMenuSettings", &L.sys_ret);
    L.crafting = feat("ui.crafting") && sym("ui/CraftingMenu", &craft_fn) && sym("ui/UiApp", &L.ui_app) &&
                 cst("ui/ui.data", &L.ui_data) && cst("ui/data.craft_recipe", &L.craft_recipe) &&
                 cst("ui/data.station_recipe", &L.station_recipe);
    g_frame_hook.install(g_api, g_owner, frame_fn, reinterpret_cast<void*>(&hk_frame), "ui/GameUiFrame");
    if (!g_frame_hook) {
        logf(KILN_LOG_ERROR, "ui: hooking the game's UI frame failed: no game-UI integration");
        L.screens = L.main_menu = L.system_menu = L.crafting = false;
        return false;
    }
    if (L.main_menu) {
        g_main_hook.install(g_api, g_owner, main_fn, reinterpret_cast<void*>(&hk_main_entry), "ui/MainMenuEntry");
        L.main_menu = static_cast<bool>(g_main_hook);
    }
    if (L.system_menu) {
        g_sys_hook.install(g_api, g_owner, sys_fn, reinterpret_cast<void*>(&hk_sys_entry), "ui/SystemMenuEntry");
        L.system_menu = static_cast<bool>(g_sys_hook);
    }
    if (L.crafting) {
        g_craft_hook.install(g_api, g_owner, craft_fn, reinterpret_cast<void*>(&hk_craft), "ui/CraftingMenu");
        L.crafting = static_cast<bool>(g_craft_hook);
    }
    add_builtin_entries();
    logf(KILN_LOG_INFO, "ui: game UI hooked: screens on, main menu %s, pause menu %s, crafting %s, language %s",
         L.main_menu ? "on" : "off", L.system_menu ? "on" : "off", L.crafting ? "on" : "off", L.language ? "on" : "off");
    return true;
}

void game_ready() {
    // kiln.craftmenu (kiln.content) tells which workshop the crafting menu builds
    if (const auto* cm = static_cast<const KilnCraftMenuApi*>(g_api->service(KilnStr{KILN_CRAFTMENU_SERVICE, sizeof(KILN_CRAFTMENU_SERVICE) - 1}, 1))) {
        KilnHandle h = 0;
        if (cm->subscribe(g_owner, &on_craftmenu, nullptr, &h) == KILN_OK) logf(KILN_LOG_INFO, "ui: crafting menu workshops from kiln.craftmenu");
    }
}

bool game_screens_on() { return L.screens; }
bool game_menu_on(uint32_t menu) { return menu == KILN_UI_MENU_MAIN ? L.main_menu : menu == KILN_UI_MENU_PAUSE ? L.system_menu : false; }

bool game_in_menu() {
    if (!L.screens) return false;
    return g_api->world(KILN_WORLD_SERVER) == 0 && g_api->world(KILN_WORLD_CLIENT) == 0;
}

KilnResult game_screen(KilnUiScreen* out) {
    if (!out || out->size < sizeof(KilnUiScreen)) return KILN_ERR_INVALID_ARGUMENT;
    if (!L.screens) return KILN_ERR_UNAVAILABLE;
    std::lock_guard lk(g_screen_mu);
    *out = g_screen;
    out->size = sizeof(KilnUiScreen);
    return KILN_OK;
}

KilnResult game_subscribe_screen(KilnOwner owner, KilnUiScreenFn fn, void* user, KilnHandle* out) {
    if (!fn) return KILN_ERR_INVALID_ARGUMENT;
    if (!L.screens) return KILN_ERR_UNAVAILABLE;
    std::lock_guard lk(g_mu);
    const KilnHandle h = next_handle();
    g_screen_subs.push_back({h, owner, fn, user});
    if (out) *out = h;
    return KILN_OK;
}

KilnResult game_subscribe_tick(KilnOwner owner, KilnUiTickFn fn, void* user, KilnHandle* out) {
    if (!fn) return KILN_ERR_INVALID_ARGUMENT;
    if (!L.screens) return KILN_ERR_UNAVAILABLE;
    std::lock_guard lk(g_mu);
    const KilnHandle h = next_handle();
    g_tick_subs.push_back({h, owner, fn, user});
    if (out) *out = h;
    return KILN_OK;
}

KilnResult game_subscribe_frame_tick(KilnOwner owner, KilnUiFrameTickFn fn, void* user, KilnHandle* out) {
    if (!fn) return KILN_ERR_INVALID_ARGUMENT;
    if (!L.screens) return KILN_ERR_UNAVAILABLE;
    std::lock_guard lk(g_mu);
    const KilnHandle h = next_handle();
    g_ftick_subs.push_back({h, owner, fn, user});
    if (out) *out = h;
    return KILN_OK;
}

namespace {
KilnResult game_menu_add_impl(KilnOwner owner, uint32_t menu, int32_t order, KilnStr text, KilnUiMenuFn fn, void* user, KilnHandle* out) {
    if (menu != KILN_UI_MENU_MAIN && menu != KILN_UI_MENU_PAUSE) return KILN_ERR_INVALID_ARGUMENT;
    if (!game_menu_on(menu)) return KILN_ERR_UNAVAILABLE;
    const std::string* t = intern(text);
    std::lock_guard lk(g_mu);
    if (g_entries.size() >= 64) return KILN_ERR_UNAVAILABLE;
    const KilnHandle h = next_handle();
    g_entries.push_back(Entry{h, owner, menu, order, ++g_seq, fn, user, t, fnv64(t->data(), t->size()), 0});
    g_entries_version.fetch_add(1);
    if (out) *out = h;
    logf(KILN_LOG_INFO, "ui: %s menu entry '%s' added (order %d)", menu == KILN_UI_MENU_MAIN ? "main" : "pause", t->c_str(), order);
    return KILN_OK;
}
}  // namespace

KilnResult game_menu_add(KilnOwner owner, uint32_t menu, int32_t order, KilnStr text, KilnUiMenuFn fn, void* user, KilnHandle* out) {
    return game_menu_add_impl(owner, menu, order, text, fn, user, out);
}

KilnResult game_menu_set(KilnHandle entry, KilnStr text, uint32_t flags) {
    if (entry == 0) {  // Kiln's own "Mods" entries (both menus)
        KilnResult r = KILN_ERR_NOT_FOUND;
        for (KilnHandle b : g_builtin)
            if (b && game_menu_set(b, text, flags) == KILN_OK) r = KILN_OK;
        return r;
    }
    const std::string* t = text.ptr ? intern(text) : nullptr;
    std::lock_guard lk(g_mu);
    for (Entry& e : g_entries)
        if (e.handle == entry) {
            if (t) {
                e.text = t;
                e.fnv = fnv64(t->data(), t->size());
            }
            e.flags = flags & (KILN_UI_ENTRY_HIDDEN | KILN_UI_ENTRY_ATTENTION);
            g_entries_version.fetch_add(1);
            return KILN_OK;
        }
    return KILN_ERR_NOT_FOUND;
}

bool game_unsubscribe(KilnHandle h) {
    std::lock_guard lk(g_mu);
    const size_t n = g_entries.size() + g_screen_subs.size() + g_tick_subs.size() + g_ftick_subs.size();
    std::erase_if(g_entries, [&](const Entry& e) { return e.handle == h; });
    std::erase_if(g_screen_subs, [&](const ScreenSub& e) { return e.handle == h; });
    std::erase_if(g_tick_subs, [&](const TickSub& e) { return e.handle == h; });
    std::erase_if(g_ftick_subs, [&](const FrameTickSub& e) { return e.handle == h; });
    return g_entries.size() + g_screen_subs.size() + g_tick_subs.size() + g_ftick_subs.size() < n;
}

void game_status_line(char* out, size_t cap) {
    std::snprintf(out, cap, "screens %s, main menu %s, pause menu %s, crafting %s; frames %llu, entries drawn %llu, activated %llu",
                  L.screens ? "on" : "off", L.main_menu ? "on" : "off", L.system_menu ? "on" : "off", L.crafting ? "on" : "off",
                  static_cast<unsigned long long>(g_frames.load()), static_cast<unsigned long long>(g_draws.load()),
                  static_cast<unsigned long long>(g_clicks.load()));
}

}  // namespace kui
