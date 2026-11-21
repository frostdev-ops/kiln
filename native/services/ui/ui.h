// SPDX-License-Identifier: MIT
// kiln.ui internals shared by ui.cpp (service, frame, drawing table), vk.cpp (Vulkan interception + rendering) and
// input.cpp (game window input). See native/core/abi/services/kiln_ui.h and docs/re/client-render.md.
#pragma once
#include <windows.h>

#include <atomic>
#include <cstdint>
#include <mutex>

#include "kiln_api.h"
#include "overlay_mode.h"
#include "services/kiln_ui.h"

namespace kui {

// Snapshots hold this state, not a bare callback. Unsubscribe cancels queued calls and waits for an in-flight call
// before the plugin may unload. A callback may unsubscribe itself (the gate is recursive).
struct CallbackLifetime {
    // A recursive gate alone cannot distinguish self-unsubscribe from a nested callback canceling an outer one.
    // Keep the invocation stack so the latter cannot claim that the still-running outer call has drained.
    struct Invocation {
        CallbackLifetime* state;
        Invocation* outer;
        inline static thread_local Invocation* current = nullptr;
        explicit Invocation(CallbackLifetime* s) : state(s), outer(current) { current = this; }
        ~Invocation() { current = outer; }
        Invocation(const Invocation&) = delete;
        Invocation& operator=(const Invocation&) = delete;
    };
    std::recursive_mutex gate;
    std::atomic<bool> active{true};
    std::unique_lock<std::recursive_mutex> drain(bool nonblocking = false) {
        std::unique_lock lock(gate, std::defer_lock);
        if (nonblocking && Invocation::current) {
            for (auto* i = Invocation::current->outer; i; i = i->outer)
                if (i->state == this) return lock;
        }
        if (nonblocking && !lock.try_lock()) return lock;
        if (!nonblocking) lock.lock();
        return lock;
    }
};

// The kiln.ui callback running on this thread (null outside one): unsubscribe does not wait from inside a callback.
extern thread_local CallbackLifetime* t_callback;

extern const KilnApi* g_api;
extern KilnOwner g_owner;
void logf(int level, const char* fmt, ...);
// progress.json (kiln.progress v2 `mark`): the main menu, or a start's first world frame, drew. Once per process; any
// later call returns at an atomic test. `what` names it for the log line.
void start_drawn(const char* what);
uint64_t now_ms();

// ---- state (any thread) ----
extern std::atomic<uint32_t> g_demand;       // drawable layers registered by Kiln.Ui (HUD, windows, toasts)
extern std::atomic<uint32_t> g_interactive;  // interactive windows open
extern std::atomic<bool> g_menu_open;
extern std::atomic<uint32_t> g_menu_vk, g_menu_mods;
extern std::atomic<HWND> g_hwnd;             // the game window (from the swapchain's surface)
extern std::atomic<bool> g_text_input;       // ImGui wants text input (last frame)
extern std::atomic<bool> g_want_mouse;       // ImGui wants the mouse (last frame)
extern std::atomic<bool> g_wheel_active;     // a radial wheel is open (wheel.cpp)
extern std::atomic<bool> g_page_capture;     // the Kiln page in the game's main menu is opening or shown (menu_page.cpp)
inline bool wheel_active() { return g_wheel_active.load(std::memory_order_relaxed); }
// The overlay's drawing turned off during this session (vk.cpp: a failed Vulkan submit, a fault, a present queue Kiln
// cannot draw with; ui.cpp overlay_turned_off). Set once, never cleared: from then on nothing of Kiln's captures the
// game's input or asks to draw, and the Mods entry shows the reason as with the drawing off from the start.
extern std::atomic<bool> g_overlay_failed;
inline bool overlay_failed() { return g_overlay_failed.load(std::memory_order_acquire); }
// The overlay draws in this session: its drawing was on at the start and has not turned off since.
inline bool overlay_draws() { return overlay_drawing_on() && !overlay_failed(); }
// Kiln's menu fell back to the game's own Mods rows in this session (since 0.2.0-dev.30.2): it was asked for in the
// overlay and no frame of the overlay was drawn and presented in about a second (overlay_guard.cpp overlay_menu_ask;
// another layer on the game's presents, say). The Mods entry then works as with the drawing off: its caption in the game's
// own menu, g_menu_open its state. Cleared by the next ask once a frame of the overlay was presented since, and when the
// drawing turns off (that state says it all then).
extern std::atomic<bool> g_menu_fell_back;
// Kiln's menu opens in the overlay: the overlay draws and its menu did not fall back.
inline bool menu_in_overlay() { return overlay_draws() && !g_menu_fell_back.load(std::memory_order_relaxed); }
// Kiln's menu is open in the overlay (g_menu_open), not as the Mods entry's caption after the fallback.
inline bool menu_open_in_overlay() {
    return g_menu_open.load(std::memory_order_relaxed) && !g_menu_fell_back.load(std::memory_order_relaxed);
}
// vk.cpp, once, on the present call after the overlay's drawing turned off, outside the overlay's locks: the one log
// line (at `level`: an error for a failure, a warning for a present Kiln cannot draw in), kiln.ui's status, the Mods
// caption, the capability rows and the crash guard (ui.cpp). `reason` is short (the Mods caption and Ui.Status.Error
// carry it), `detail` goes into kiln.log only.
void overlay_turned_off(int level, const char* reason, const char* detail);
const std::string& overlay_off_reason();  // the reason (after overlay_failed() said true), else ""
// overlay_turned_off, with g_overlay_failed set: what the last drawn frame left set lets go of the game's input. ImGui's
// text field flag is not built again (key bindings would take every press for typing: KilnUiKey.text_input), and
// Kiln's menu, no longer shown, is closed (Escape would close it instead of reaching the game; a Keys page that was
// waiting for a key would take the next press). The menu key opens the Mods entry's caption, as with drawing off.
inline void release_input_after_turn_off() {
    g_text_input.store(false);
    g_menu_open.store(false);
    g_menu_fell_back.store(false);
}
inline bool capturing() {
    return overlay_draws() && (menu_open_in_overlay() || g_page_capture.load(std::memory_order_relaxed) ||
           g_interactive.load(std::memory_order_relaxed) > 0 || wheel_active());
}
// Something to draw this frame. The native fallback menu counts too.
extern std::atomic<uint32_t> g_render_demand;  // kiln.render acquire_frames handles (render.cpp)
extern std::atomic<uint32_t> g_footer_demand;  // Kiln's mark next to its line in the main menu's footer (menu_footer.cpp)
extern std::atomic<uint32_t> g_graphics_demand;  // the graphics readout (graphics.cpp)
extern std::atomic<uint32_t> g_chat_completion_demand; // focused native slash-command suggestions (issue #44)
inline bool want_draw() {
    return overlay_draws() && (capturing() || g_demand.load(std::memory_order_relaxed) > 0 || g_render_demand.load(std::memory_order_relaxed) > 0 ||
                                    g_footer_demand.load(std::memory_order_relaxed) > 0 || g_graphics_demand.load(std::memory_order_relaxed) > 0 ||
                                    g_chat_completion_demand.load(std::memory_order_relaxed) > 0);
}
bool in_frame();  // the calling thread is inside the overlay frame's callbacks (drawing calls work)

void set_error(const char* fmt, ...);
KilnHandle next_handle();                    // one handle space for every kiln.ui subscription (any thread)
extern std::atomic<uint32_t> g_hover_panels; // Kiln panels that take the mouse only while it is over them

// ---- chat_input_probe.cpp (GitHub issue 44: Up/Down history in the game's chat field) ----
bool chat_input_probe_install();  // game_install: hooks the chat field when chat history (or a diagnostic) is on
void chat_input_frame_end(uintptr_t sys10);  // game's UI thread, focus and native rectangle conversion
void chat_input_exit();           // on_unload: writes the history file still pending, stops its writer thread
KilnResult chat_completion_io(KilnUiChatCompletion* io);  // internal managed snapshot/selection/replacement, NOT game call
KilnResult chat_search_io(KilnUiChatSearch* io); // internal Ctrl+R search snapshot/mouse selection, NOT a game call

// The window procedure's one hook point for the game's chat field (input.cpp calls it on the window thread, before
// Kiln's own key handling). Set by chat_input_probe_install while chat history or its diagnostic runs; null otherwise,
// and in the input.cpp host harnesses, which do not link chat_input_probe.cpp.
struct ChatInputSink {
    bool (*key)(uint32_t vk, uint32_t mods, bool down, bool captured);  // a key transition; true keeps a press from the game
    bool (*text)(uint32_t ch);                                           // WM_CHAR / WM_UNICHAR; true drops a consumed Tab's character
    void (*ime)(bool composing);                                         // an IME composition started (true) or ended
    void (*blur)();                                                      // the window lost focus / the game was deactivated
};
void input_set_chat_sink(const ChatInputSink* sink);  // input.cpp; the sink must live for the process

// ---- input.cpp ----
void input_install_iat();       // SetCursorPos / ClipCursor slots of the game exe (plugin main)
void input_hook_xinput();      // after XInput loads: idle pad for the game while Kiln has the input
void input_poll_window();       // drawing off: discover this process's game window without Vulkan
void input_attach(HWND hwnd);   // subclasses the window (idempotent; re-subclasses when the game replaced its WndProc)
bool input_attached();
void input_replay();            // render thread, before ImGui::NewFrame: feeds queued window messages to ImGui
void input_capture_changed();   // render thread: capture started/stopped (cursor clip, ImGui mouse cursor)
uint32_t input_take_pressed(uint32_t* mods);
void key_name(uint32_t vk, char* out, size_t cap);
// Runs the menu key, the wheel and the key subscribers; true = consumed. `mouse`: a mouse press (mouse_press.h), which
// only subscribers with KILN_UI_KEYS_MOUSE get (keys.cpp).
bool dispatch_key(const KilnUiKey& key, bool mouse = false);
bool input_key_held(uint32_t vk);         // the key is down now, as far as the window's messages have told (any thread)
// subscribe_window_input (kiln_ui.h v5): the window's input records for native subscribers. Any thread.
KilnResult input_feed_subscribe(KilnOwner owner, KilnUiWindowInputFn fn, void* user, KilnHandle* out);
KilnResult input_feed_unsubscribe(KilnHandle h);  // KILN_ERR_NOT_FOUND: not a window-input subscription
// subscribe_pointer_press (0.2.0-dev.30): left/right presses over the game's UI for native subscribers. Any thread.
KilnResult input_pointer_subscribe(KilnOwner owner, KilnUiPointerFn fn, void* user, KilnHandle* out);
KilnResult input_pointer_unsubscribe(KilnHandle h);  // KILN_ERR_NOT_FOUND: not a pointer subscription

// ---- keys.cpp (the key subscribers: subscribe_key, v7 subscribe_key_ex) ----
KilnResult keys_subscribe(KilnOwner owner, KilnUiKeyFn fn, void* user, uint32_t flags, KilnHandle* out);  // KILN_UI_KEYS_*
KilnResult keys_unsubscribe(KilnHandle h);  // KILN_ERR_NOT_FOUND: not a key subscription
bool keys_dispatch(const KilnUiKey& key, bool mouse);  // to the subscribers that get it; true = one of them took it
bool keys_mouse_wanted();                  // a subscriber gets mouse presses (any thread)
size_t keys_count();

// ---- ui.cpp ----
// Builds one ImGui frame (NewFrame, frame callbacks, the fallback menu, Render). Render thread. False = nothing drawn.
bool build_frame(float width, float height, float dpi_scale);
bool imgui_ready();
bool imgui_create(HWND hwnd, float dpi_scale);  // context + Win32 backend + fonts (render thread)
void imgui_destroy();

// ---- wheel.cpp (the radial wheel: native/services/ui/wheel_math.h has its rules) ----
KilnResult wheel_open(KilnOwner owner, const KilnUiWheel* def, KilnUiWheelFn fn, void* user, KilnHandle* out);
KilnResult wheel_close(KilnHandle handle);
void wheel_frame(float width, float height, float dpi);  // render thread, inside the ImGui frame, when wheel_active()
bool wheel_on_key(const KilnUiKey& key);                 // window thread: Esc and the hold key; true = consumed
void wheel_cancel_undrawable();                          // render thread: the overlay is off; cancel an open wheel (fn(-1))

// ---- game.cpp (the game's own UI: screens, menu entries, crafting; docs/re/game-ui.md) ----
bool game_install();   // plugin main: resolve ui.json, hook the game's UI frame and menu-entry renderers
void game_ready();     // on_ready: kiln.craftmenu
bool game_screens_on();
bool game_menu_on(uint32_t menu);
bool game_in_menu();   // no world steps (boot, main menu, loading)
KilnResult game_screen(KilnUiScreen* out);
uint32_t game_screen_target();  // a chest's or factory's entity (play.target) of the last UI frame, else 0
KilnResult game_subscribe_screen(KilnOwner owner, KilnUiScreenFn fn, void* user, KilnHandle* out);
KilnResult game_subscribe_tick(KilnOwner owner, KilnUiTickFn fn, void* user, KilnHandle* out);
KilnResult game_subscribe_frame_tick(KilnOwner owner, KilnUiFrameTickFn fn, void* user, KilnHandle* out);
KilnResult game_menu_add(KilnOwner owner, uint32_t menu, int32_t order, KilnStr text, KilnUiMenuFn fn, void* user, KilnHandle* out);
KilnResult game_menu_set(KilnHandle entry, KilnStr text, uint32_t flags);
void game_menu_shown(uint32_t mode);                                     // KILN_UI_MENU_SHOW_* (menu_rows.h)
uint32_t game_menu_entries(KilnUiMenuEntry* out, uint32_t capacity);   // mods' entries (not Kiln's Mods entry)
KilnResult game_menu_activate(KilnHandle entry);                         // as if chosen in its menu
uint32_t game_menu_mode();                                                 // KILN_UI_MENU_SHOW_* now
size_t game_pause_mods_rows();                                             // visible mods' entries of the pause menu

// ---- menu_scroll.cpp (the pause menu in the game's own scroll view, an experiment: menu_scroll.h) ----
bool scroll_install(bool system_menu_hooked);  // game_install, after the SystemMenuEntry hook
uint32_t scroll_status();                      // KILN_UI_SCROLL_*
bool scroll_on();                              // the pause menu can scroll in this process now
void scroll_entry_next();                      // UI thread, before each call of the game's entry renderer: the top probe
void scroll_entry_drawn(bool chosen);          // UI thread, after each call of the game's entry renderer (`chosen`: it
                                               // returned true): the end probe, Quit chosen
void scroll_rows_drawn(uint32_t n);            // UI thread: Kiln drew n rows of its own in the pause menu (Settings' call)
void scroll_after_frame(uint64_t frame);       // UI thread, after each UI frame: the frame count, the crash guard
void scroll_exit();                            // on_unload (the process exits with code 0): the crash guard's marker goes
bool game_unsubscribe(KilnHandle h);
void game_status_line(char* out, size_t cap);
uint64_t game_frames();                        // UI frames finished so far (any thread)

// ---- menu_footer.cpp (Kiln's line in the main menu's footer: feature ui.menu_footer, menu_footer.h) ----
bool footer_install();                        // game_install: resolve, copy the footer function, hook it
void footer_set(int32_t mods, uint32_t flags);  // any thread: the mods' count (< 0 unknown) and KILN_UI_FOOTER_* (Kiln.Ui)
void footer_after_frame(uintptr_t sys10);     // UI thread, after each UI frame: the canvas metrics for the mark
void footer_overlay();                        // render thread, inside the ImGui frame: the mark
void footer_texts_changed();                  // any thread: Kiln's texts changed (ui_text.h): the line in the new language

// ---- Kiln's own texts (ui_text.h; set_texts) ----
void game_texts_changed();                    // any thread: the Mods entry and the drawing-off caption in the new language
void game_overlay_turned_off();               // any thread, once: the Mods entry shows why the overlay's drawing went off
// overlay_guard.cpp (a pool thread), when Kiln's menu fell back (g_menu_fell_back): the Mods entry's caption says so, and
// the main menu's page, when it was the one asked for, is left for the game's main menu, where the caption shows.
void game_menu_fell_back();

// ---- menu_page.cpp (the Kiln menu as a page of the game's main menu: feature ui.menu_page) ----
bool page_install(bool main_menu_hooked);  // game_install, after the MainMenuEntry hook: resolve, hook Credits
bool page_usable();                        // the feature, its hook and a host (menu_page_host(1)), not given up
bool page_active();                        // opening or shown (any thread)
bool page_open_from_entry(void* entry_state);  // UI thread, MainMenuEntry hook: Kiln's MODS entry activated; true = opened
void page_main_menu_drawn(void* entry_state);  // UI thread, MainMenuEntry hook at the Settings call site, every frame
void page_after_frame(uintptr_t sys10, uint64_t frame);  // UI thread, after each UI frame
bool page_menu_key();                      // window thread: the menu key; true = handled (leave, or the page will open)
bool page_escape();                        // window thread: Escape / gamepad B; true = consumed (leave)
void page_leave();                         // any thread
void page_drawn();                         // render thread: a host drew the page this overlay frame
void page_host(uint32_t on);
KilnResult page_state(KilnUiMenuPage* out);
void page_status_line(char* out, size_t cap);
bool style_push_font(uint32_t font);       // style.cpp: KILN_UI_FONT_*; false = not loaded (nothing pushed)

// ---- style.cpp (the looks: Kiln's, the game's, the player's, mods' themes; the game's font and colours, mod fonts, images) ----
void style_apply_base(float scale);          // the looks at this scale; windows that push nothing get the player's
void style_push(uint32_t style);             // inside a frame: KILN_UI_STYLE_*
bool theme_push(KilnHandle theme);           // inside a frame; false: no such theme (nothing pushed); style_pop pops it
void style_pop();
size_t style_depth();                        // looks pushed now (render thread)
void style_frame_begin();                    // render thread, before NewFrame: fonts, colours, looks and textures
void style_frame_end();                      // render thread, after the frame: unbalanced pushes
float style_scale();
float user_scale();                          // the player's scale (set_look), any thread
bool style_push_font_handle(KilnHandle font);  // inside a frame: a mod font; false = not in the atlas (nothing pushed)
KilnResult theme_create(KilnOwner owner, const KilnUiThemeDef* def, KilnHandle* out);  // any thread
KilnResult theme_free(KilnHandle theme);                                              // any thread
KilnResult look_set(const KilnUiLook* look);                                           // any thread (the caller is checked in ui.cpp)
KilnResult look_info(KilnUiLookInfo* out);                                             // any thread
KilnHandle font_load(KilnOwner owner, const void* data, size_t len);                   // any thread; 0 refused
void font_free(KilnHandle font);                                                       // any thread
KilnHandle image_load(KilnOwner owner, const void* png, size_t len);
KilnHandle image_load_rgba(KilnOwner owner, const uint8_t* rgba, uint32_t w, uint32_t h);  // w*h*4 bytes, copied
int32_t image_state(KilnHandle h);  // image_budget.h: unknown, waiting, ready, failed
bool image_size(KilnHandle h, uint32_t* w, uint32_t* hgt);
void image_draw(KilnHandle h, float w, float hgt);
bool image_button(const char* id, KilnHandle h, float w, float hgt);
// image_ex: KILN_UI_IMAGE_DRAW_* flags, uv all 0 = whole; id null = an image (false), else a button. Inside a frame.
bool image_draw_ex(const char* id, KilnHandle h, uint32_t flags, float w, float hgt, float u0, float v0, float u1, float v1);
bool image_opaque_rect(KilnHandle h, uint32_t* x, uint32_t* y, uint32_t* w, uint32_t* hgt);  // any thread
void image_free(KilnHandle h);
}  // namespace kui
struct ImTextureRef;
namespace kui {
bool image_texture(KilnHandle h, ImTextureRef* out);  // render thread

// ---- render.cpp (kiln.render: drawing for mods, the game's camera) ----
bool render_install();                 // plugin main: the camera hook (feature render.camera)
const void* render_table();            // the KilnRenderApi

// ---- graphics.cpp (Kiln's own graphics tools: the frame cap, DLSS settings, the readout; docs/kiln/graphics.md) ----
void graphics_start(bool optiscaler, bool streamline);  // plugin main, after overlay_guard_start: kiln.json, what steps aside
bool graphics_wants_load_hook();  // DLSS is to be hooked when NVIDIA's DLSS core loads (the LoadLibraryExW hook must be in)
void graphics_after_load();       // after every library load (load_hook.h; may hold the loader lock): NVIDIA's DLSS core
void graphics_present();          // render thread, right after the game's frame went to vkQueuePresentKHR (no lock held):
                                  // the frame counts, and the frame cap's wait for the game's next slot
extern std::atomic<bool> g_graphics_present;  // graphics_present has work: the cap or the readout is on, or was just
                                              // turned off (its counts are cleared once)
// vk.cpp's present hook, after the game's present: one relaxed load while the frame cap and the readout are off.
inline void graphics_after_present() {
    if (g_graphics_present.load(std::memory_order_relaxed)) graphics_present();
}
void graphics_overlay();          // render thread, inside the ImGui frame: the readout
KilnResult graphics_set(const KilnUiGraphics* settings);  // any thread (Kiln.Ui; ui.cpp refuses native plugins)
KilnResult graphics_status(KilnUiGraphicsStatus* out);     // any thread

// ---- upscale.cpp (phase B): declared in upscale.h, which needs the Vulkan headers ----

// ---- vk.cpp ----
bool vk_install();  // called once vulkan-1.dll is loaded (from the LoadLibraryExW hook)
void vk_extent(uint32_t* width, uint32_t* height);  // the game window's swapchain (any thread; 0 before one exists)
void vk_status(KilnUiStatus* out);
uint64_t vk_presents();  // frames the game presented through Kiln's present hook (the overlay crash guard)
bool vk_drawing();       // a present call is making the overlay's frame right now (vk.cpp draw(), past its checks)
KilnResult vk_subscribe_pre_overlay(KilnUiPreOverlayFn fn, KilnUiSwapchainGoneFn gone, void* user, KilnHandle* out);
bool vk_unsubscribe(KilnHandle handle);
void vk_set_pre_overlay_demand(uint32_t count);
void vk_add_swapchain_usage(uint32_t usage);

}  // namespace kui
