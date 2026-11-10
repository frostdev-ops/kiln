// SPDX-License-Identifier: MIT
// kiln.ui internals shared by ui.cpp (service, frame, drawing table), vk.cpp (Vulkan interception + rendering) and
// input.cpp (game window input). See native/core/abi/services/kiln_ui.h and docs/re/client-render.md.
#pragma once
#include <windows.h>

#include <atomic>
#include <cstdint>
#include <mutex>

#include "kiln_api.h"
#include "services/kiln_ui.h"

namespace kui {

// Snapshots hold this state, not a bare callback. Unsubscribe cancels queued calls and waits for an in-flight call
// before the plugin may unload. A callback may unsubscribe itself (the gate is recursive).
struct CallbackLifetime {
    std::recursive_mutex gate;
    std::atomic<bool> active{true};
    std::unique_lock<std::recursive_mutex> drain(bool nonblocking = false) {
        std::unique_lock lock(gate, std::defer_lock);
        if (nonblocking && !lock.try_lock()) return lock;
        if (!nonblocking) lock.lock();
        return lock;
    }
};

extern const KilnApi* g_api;
extern KilnOwner g_owner;
void logf(int level, const char* fmt, ...);
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
inline bool capturing() {
    return g_menu_open.load(std::memory_order_relaxed) || g_page_capture.load(std::memory_order_relaxed) ||
           g_interactive.load(std::memory_order_relaxed) > 0 || wheel_active();
}
// Something to draw this frame. The native fallback menu counts too.
extern std::atomic<uint32_t> g_render_demand;  // kiln.render acquire_frames handles (render.cpp)
inline bool want_draw() {
    return capturing() || g_demand.load(std::memory_order_relaxed) > 0 || g_render_demand.load(std::memory_order_relaxed) > 0;
}
bool in_frame();  // the calling thread is inside the overlay frame's callbacks (drawing calls work)

void set_error(const char* fmt, ...);
KilnHandle next_handle();                    // one handle space for every kiln.ui subscription (any thread)
extern std::atomic<uint32_t> g_hover_panels; // Kiln panels that take the mouse only while it is over them

// ---- input.cpp ----
void input_install_iat();       // SetCursorPos / ClipCursor slots of the game exe (plugin main)
void input_hook_xinput();      // after XInput loads: idle pad for the game while Kiln has the input
void input_attach(HWND hwnd);   // subclasses the window (idempotent; re-subclasses when the game replaced its WndProc)
bool input_attached();
void input_replay();            // render thread, before ImGui::NewFrame: feeds queued window messages to ImGui
void input_capture_changed();   // render thread: capture started/stopped (cursor clip, ImGui mouse cursor)
uint32_t input_take_pressed(uint32_t* mods);
void key_name(uint32_t vk, char* out, size_t cap);
bool dispatch_key(const KilnUiKey& key);  // runs the menu key, the wheel and key subscribers; true = consumed
bool input_key_held(uint32_t vk);         // the key is down now, as far as the window's messages have told (any thread)

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
KilnResult game_subscribe_screen(KilnOwner owner, KilnUiScreenFn fn, void* user, KilnHandle* out);
KilnResult game_subscribe_tick(KilnOwner owner, KilnUiTickFn fn, void* user, KilnHandle* out);
KilnResult game_subscribe_frame_tick(KilnOwner owner, KilnUiFrameTickFn fn, void* user, KilnHandle* out);
KilnResult game_menu_add(KilnOwner owner, uint32_t menu, int32_t order, KilnStr text, KilnUiMenuFn fn, void* user, KilnHandle* out);
KilnResult game_menu_set(KilnHandle entry, KilnStr text, uint32_t flags);
bool game_unsubscribe(KilnHandle h);
void game_status_line(char* out, size_t cap);

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

// ---- style.cpp (Kiln's and the game's look, the game font, images) ----
void style_apply_base(float scale);          // the default (Kiln ember) style, scaled
void style_push(uint32_t style);             // inside a frame
void style_pop();
void style_frame_begin();                    // render thread, before NewFrame: fonts and textures to add
void style_frame_end();                      // render thread, after the frame: unbalanced pushes
float style_scale();
KilnHandle image_load(KilnOwner owner, const void* png, size_t len);
bool image_size(KilnHandle h, uint32_t* w, uint32_t* hgt);
void image_draw(KilnHandle h, float w, float hgt);
bool image_button(const char* id, KilnHandle h, float w, float hgt);
void image_free(KilnHandle h);
}  // namespace kui
struct ImTextureRef;
namespace kui {
bool image_texture(KilnHandle h, ImTextureRef* out);  // render thread

// ---- render.cpp (kiln.render: drawing for mods, the game's camera) ----
bool render_install();                 // plugin main: the camera hook (feature render.camera)
const void* render_table();            // the KilnRenderApi

// ---- vk.cpp ----
bool vk_install();  // called once vulkan-1.dll is loaded (from the LoadLibraryExW hook)
void vk_status(KilnUiStatus* out);
KilnResult vk_subscribe_pre_overlay(KilnUiPreOverlayFn fn, KilnUiSwapchainGoneFn gone, void* user, KilnHandle* out);
bool vk_unsubscribe(KilnHandle handle);
void vk_set_pre_overlay_demand(uint32_t count);
void vk_add_swapchain_usage(uint32_t usage);

}  // namespace kui
