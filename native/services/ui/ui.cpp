// SPDX-License-Identifier: MIT
// kiln.ui: service entry, subscribers, the ImGui frame and the drawing table (native/core/abi/services/kiln_ui.h).
#include <intrin.h>
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <optional>
#include <mutex>
#include <memory>
#include <string>
#include <vector>
#include <unordered_map>

#include "backends/imgui_impl_win32.h"
#include "imgui.h"
#include "key_names.h"
#include "safetyhook.hpp"
#include "services/kiln_progress.h"
#include "services/kiln_caps.h"
#include "services/kiln_render.h"
#include "image_budget.h"
#include "ui.h"
#include "load_hook.h"
#include "slider.h"
#include "overlay_guard_runtime.h"
#include "ini_file.h"
#include "draw_slots.h"
#include "ui_text.h"

namespace kui {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
std::atomic<uint32_t> g_demand{0}, g_interactive{0};
std::atomic<bool> g_menu_open{false};
std::atomic<uint32_t> g_menu_vk{VK_INSERT}, g_menu_mods{0};
std::atomic<HWND> g_hwnd{nullptr};
std::atomic<bool> g_text_input{false}, g_want_mouse{false};
std::atomic<uint32_t> g_hover_panels{0};
namespace {
std::atomic<KilnHandle> g_handles{1};
}
KilnHandle next_handle() { return g_handles.fetch_add(1); }
thread_local CallbackLifetime* t_callback = nullptr;

namespace {
std::mutex g_err_mu;
char g_error[128] = "";
uint64_t g_frame_no = 0;
uint64_t g_last_frame_ms = 0;
float g_style_scale = 0;
std::string g_ini_path;
// imgui.ini, read and written on IniFile's thread (ini_file.h); ImGui gets and gives it in memory. The write replaces the
// file through a temporary one, nothing forced to the disk (window places: a lost last move is no loss).
kui::IniFile g_ini(
    [](const std::string& path) -> std::optional<std::string> {
        std::ifstream f(std::filesystem::path(std::u8string(path.begin(), path.end())), std::ios::binary);
        if (!f) return std::nullopt;
        std::string text((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
        if (f.bad()) return std::nullopt;
        return text;
    },
    [](const std::string& path, const std::string& text) {
        const std::filesystem::path file(std::u8string(path.begin(), path.end()));
        auto temp = file;
        temp += L".tmp";
        {
            std::ofstream f(temp, std::ios::binary | std::ios::trunc);
            if (!f) return false;
            f.write(text.data(), static_cast<std::streamsize>(text.size()));
            if (!f) return false;
        }
        return MoveFileExW(temp.c_str(), file.c_str(), MOVEFILE_REPLACE_EXISTING) != 0;
    });
bool g_imgui = false;
thread_local bool t_in_frame = false;

struct FrameSub : CallbackLifetime {
    KilnHandle handle;
    KilnOwner owner;
    KilnUiFrameFn fn;
    void* user;
};
std::mutex g_sub_mu;
std::vector<std::shared_ptr<FrameSub>> g_frame_subs;  // key subscribers: keys.cpp
// Keep canceled calls discoverable while another thread drains them (including self-unsubscribe during cleanup).
std::unordered_map<KilnHandle, std::shared_ptr<CallbackLifetime>> g_retiring_subs;

// After every library load that gave a module (load_hook.h), on the loading thread; several threads at once, and the
// thread may hold the loader lock. Neither call waits for another thread: each lets one thread work and the others pass.
void after_library_load() {
    // every load: vulkan-1.dll present and our hooks not in its code (first load, or a reload) -> (re)install
    if (overlay_drawing_on() && GetModuleHandleW(L"vulkan-1.dll")) vk_install();
    if (overlay_drawing_on()) input_hook_xinput();
    graphics_after_load();  // NVIDIA's DLSS core, when a DLSS setting or the readout was on at start (one load otherwise)
}

// Calls a native frame callback under a fault guard (the game's render thread must survive a bad subscriber).
bool guarded_frame(KilnUiFrameFn fn, const KilnUiFrame* f, void* user) {
    __try {
        fn(f, user);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void apply_style(float scale) {
    style_apply_base(scale);  // the looks at this scale (style.cpp); windows that push nothing get the player's
    g_style_scale = scale;
}

void fallback_menu() {
    const ImVec2 ds = ImGui::GetIO().DisplaySize;
    ImGui::SetNextWindowPos(ImVec2(ds.x * 0.5f, ds.y * 0.3f), ImGuiCond_Always, ImVec2(0.5f, 0.5f));
    ImGui::SetNextWindowSize(ImVec2(420.0f * g_style_scale, 0.0f), ImGuiCond_Always);  // height fits the text
    bool open = true;
    if (ImGui::Begin("Kiln###kiln.menu.native", &open, ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoCollapse)) {
        KilnStr v = g_api->version();
        const std::string version(v.ptr ? v.ptr : "", v.ptr ? v.len : 0);
        ImGui::TextUnformatted(text::fill(text::get("native.fallback.title"), {{"version", version}}).c_str());
        ImGui::Separator();
        ImGui::TextWrapped("%s", text::get("native.fallback.body").c_str());
        char key[32];
        key_name(g_menu_vk.load(), key, sizeof key);  // key names stay as keybinds.json writes them (key_names.h)
        ImGui::TextDisabled("%s", text::fill(text::get("native.fallback.close"), {{"key", key}}).c_str());
    }
    ImGui::End();
    if (!open) g_menu_open = false;
}

}  // namespace

uint64_t now_ms() { return GetTickCount64(); }

void logf(int level, const char* fmt, ...) {
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    const int n = std::vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    if (n > 0) g_api->log(g_owner, level, KilnStr{buf, static_cast<size_t>(std::min(n, static_cast<int>(sizeof(buf) - 1)))});
}

void set_error(const char* fmt, ...) {
    char buf[128];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    {
        std::lock_guard<std::mutex> lk(g_err_mu);
        std::snprintf(g_error, sizeof g_error, "%s", buf);
    }
    logf(KILN_LOG_ERROR, "ui: %s", buf);
}

bool imgui_ready() { return g_imgui; }
bool in_frame() { return t_in_frame; }

bool imgui_create(HWND hwnd, float dpi) {
    if (g_imgui) return true;
    IMGUI_CHECKVERSION();
    ImGui::CreateContext();
    ImGuiIO& io = ImGui::GetIO();
    // imgui.ini from memory (g_ini read it on its thread since the plugin started); saved through g_ini (build_frame)
    io.IniFilename = nullptr;
    if (const auto ini = g_ini.text(); ini && !ini->empty()) ImGui::LoadIniSettingsFromMemory(ini->data(), ini->size());
    io.ConfigFlags |= ImGuiConfigFlags_NavEnableKeyboard | ImGuiConfigFlags_NavEnableGamepad | ImGuiConfigFlags_NoMouseCursorChange;
    io.MouseDrawCursor = false;
    // By default ImGui opens a "conflicting ID" popup for programmers over the game when two items share an id, in Kiln's
    // own pages or in a mod's window. Players cannot act on it: off (ImGui's advice for non-programmer builds).
    io.ConfigDebugHighlightIdConflicts = false;
    if (!ImGui_ImplWin32_Init(hwnd)) {
        set_error("ImGui Win32 backend failed");
        ImGui::DestroyContext();
        return false;
    }
    // A readable TTF when the system has one (Windows: Segoe UI; Proton: Arial); ImGui's own font otherwise.
    char windir[MAX_PATH] = "C:\\Windows";
    GetWindowsDirectoryA(windir, MAX_PATH);
    bool font = false;
    for (const char* f : {"segoeui.ttf", "arial.ttf", "tahoma.ttf"}) {
        const std::string p = std::string(windir) + "\\Fonts\\" + f;
        if (GetFileAttributesA(p.c_str()) != INVALID_FILE_ATTRIBUTES && io.Fonts->AddFontFromFileTTF(p.c_str(), 16.0f)) {
            logf(KILN_LOG_INFO, "ui: font %s", p.c_str());
            font = true;
            break;
        }
    }
    if (!font) io.Fonts->AddFontDefault();
    apply_style(dpi);
    g_imgui = true;
    logf(KILN_LOG_INFO, "ui: ImGui %s context ready (window %p, scale %.2f)", IMGUI_VERSION, reinterpret_cast<void*>(hwnd), dpi);
    return true;
}

void imgui_destroy() {
    if (!g_imgui) return;
    size_t ini_size = 0;  // what ImGui's own shutdown saved while it had the file name
    if (const char* ini = ImGui::SaveIniSettingsToMemory(&ini_size)) g_ini.save(std::string(ini, ini_size));
    ImGui_ImplWin32_Shutdown();
    ImGui::DestroyContext();
    g_imgui = false;
}

bool build_frame(float width, float height, float dpi) {
    input_capture_changed();
    input_replay();
    ImGui_ImplWin32_NewFrame();
    ImGuiIO& io = ImGui::GetIO();
    io.DisplaySize = ImVec2(width, height);
    dpi *= user_scale();  // the player's scale (kiln.json ui.scale, set_look) on top of the display's
    if (dpi != g_style_scale) apply_style(dpi);
    const uint64_t t = now_ms();
    const float dt = g_last_frame_ms ? std::clamp((t - g_last_frame_ms) / 1000.0f, 0.0001f, 0.25f) : 1.0f / 60.0f;
    g_last_frame_ms = t;
    io.DeltaTime = dt;
    style_frame_begin();  // the game's font and new images join the atlas / texture list before the frame
    ImGui::NewFrame();
    if (io.WantSaveIniSettings) {  // a window moved or resized a few seconds ago: imgui.ini is written on g_ini's thread
        size_t ini_size = 0;
        if (const char* ini = ImGui::SaveIniSettingsToMemory(&ini_size)) g_ini.save(std::string(ini, ini_size));
        io.WantSaveIniSettings = false;
    }
    footer_overlay();  // Kiln's mark next to its line in the main menu's footer, behind every window (menu_footer.cpp)
    graphics_overlay();  // the graphics readout in the top right corner, when the player turned it on (graphics.cpp)
    KilnUiFrame f{};
    f.size = sizeof f;
    f.width = width;
    f.height = height;
    // the style's scale (apply_style above) and what Ui.Scale gives every mod: one value. A page that grows with the
    // display on top of it bounds its own scale (ModManagerHost.PageScale), not this one.
    f.dpi_scale = dpi;
    f.delta_seconds = dt;
    f.menu_open = g_menu_open.load() ? 1 : 0;
    f.capturing = capturing() ? 1 : 0;
    f.frame = ++g_frame_no;
    std::vector<std::shared_ptr<FrameSub>> subs;
    {
        std::lock_guard<std::mutex> lk(g_sub_mu);
        subs = g_frame_subs;
    }
    t_in_frame = true;
    for (const auto& s : subs) {
        std::lock_guard call(s->gate);
        if (!s->active.load(std::memory_order_acquire)) continue;
        CallbackLifetime::Invocation invocation(s.get());
        auto* outer = t_callback;
        t_callback = s.get();
        const bool ok = guarded_frame(s->fn, &f, s->user);
        t_callback = outer;
        if (!ok) {
            s->active.store(false, std::memory_order_release);
            set_error("a frame callback faulted; removed");
            std::lock_guard<std::mutex> lk(g_sub_mu);
            std::erase_if(g_frame_subs, [&](const auto& x) { return x == s; });
        }
    }
    if (wheel_active()) wheel_frame(width, height, dpi);  // on top of the windows
    if (f.menu_open && subs.empty()) fallback_menu();
    kui_draw::v3_frame_end();  // font sizes, colours and spacings a callback left pushed
    style_frame_end();
    t_in_frame = false;
    ImGui::Render();
    g_text_input = io.WantTextInput;
    g_want_mouse = io.WantCaptureMouse;
    return true;
}

bool dispatch_key(const KilnUiKey& key, bool mouse) {
    // the menu key first (works without the managed host); in the main menu it opens or leaves the game page. A
    // punctuation key is the key at its place on any layout (key_names.h), as for Kiln.Ui's bindings
    if (key.down && !key.repeat && !key.text_input && keys::matches(g_menu_vk.load(), key.vk, key.scan) &&
        key.mods == g_menu_mods.load()) {
        if (overlay_drawing_on() && page_menu_key()) return true;
        g_menu_open = !g_menu_open.load();
        logf(KILN_LOG_DEBUG, "ui: Kiln menu %s", g_menu_open ? "open" : "closed");
        return true;
    }
    // Escape (and gamepad B, which input.cpp turns into Escape) on the game page: back to the game's main menu. In a
    // text field it goes to ImGui first (it leaves the field).
    if (key.down && key.vk == VK_ESCAPE && !key.text_input && page_escape()) return true;
    if (key.down && key.vk == VK_ESCAPE && g_menu_open.load() && !key.text_input) {
        g_menu_open = false;
        return true;
    }
    if (wheel_active() && wheel_on_key(key)) return true;  // Esc cancels the wheel; its hold key's release is noted
    return keys_dispatch(key, mouse);  // subscribe_key gets keyboard keys only; mouse presses need KILN_UI_KEYS_MOUSE
}

}  // namespace kui

// =====================================================================================================================
// Table

namespace {
using namespace kui;
using namespace kui_draw;

KilnResult KILN_CALL t_status(KilnUiStatus* out) {
    if (!out || out->size < sizeof(KilnUiStatus)) return KILN_ERR_INVALID_ARGUMENT;
    std::memset(out, 0, sizeof *out);
    out->size = sizeof *out;
    if (overlay_drawing_on()) vk_status(out);
    out->window = input_attached() ? 1 : 0;
    out->menu_open = g_menu_open.load() || page_active() ? 1 : 0;  // the Kiln menu shows, in either host
    out->capturing = capturing() ? 1 : 0;
    {
        std::lock_guard<std::mutex> lk(g_sub_mu);
        out->subscribers = static_cast<uint32_t>(g_frame_subs.size() + keys_count());
    }
    std::lock_guard<std::mutex> lk(g_err_mu);
    std::snprintf(out->error, sizeof out->error, "%s", overlay_drawing_on() ? g_error : g_overlay_reason.c_str());
    return KILN_OK;
}

KilnResult KILN_CALL t_subscribe_frame(KilnOwner owner, KilnUiFrameFn fn, void* user, KilnHandle* out) {
    if (!fn) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard<std::mutex> lk(g_sub_mu);
    const KilnHandle h = next_handle();
    auto sub = std::make_shared<FrameSub>();
    sub->handle = h;
    sub->owner = owner;
    sub->fn = fn;
    sub->user = user;
    g_frame_subs.push_back(std::move(sub));
    if (out) *out = h;
    return KILN_OK;
}

KilnResult KILN_CALL t_subscribe_key(KilnOwner owner, KilnUiKeyFn fn, void* user, KilnHandle* out) {
    return keys_subscribe(owner, fn, user, 0, out);  // keyboard keys only, as before 0.2.0-dev.27
}

KilnResult KILN_CALL t_unsubscribe(KilnHandle h) {
    std::shared_ptr<CallbackLifetime> removed;
    {
        std::lock_guard<std::mutex> lk(g_sub_mu);
        const auto erase = [&](const auto& s) {
            if (s->handle != h) return false;
            s->active.store(false, std::memory_order_release);
            removed = s;
            g_retiring_subs[h] = s;
            return true;
        };
        std::erase_if(g_frame_subs, erase);
        if (!removed) {
            const auto it = g_retiring_subs.find(h);
            if (it != g_retiring_subs.end()) removed = it->second;
        }
    }
    // Never wait under g_sub_mu: the callback may itself subscribe/unsubscribe.
    if (removed) {
        // Two callbacks canceling one another must not deadlock their threads. Retry from the cleanup thread.
        auto drain = removed->drain(t_callback != nullptr);
        if (!drain.owns_lock()) return KILN_ERR_WRONG_THREAD;
        std::lock_guard lk(g_sub_mu);
        g_retiring_subs.erase(h);
        return KILN_OK;
    }
    if (const KilnResult r = keys_unsubscribe(h); r != KILN_ERR_NOT_FOUND) return r;  // key callbacks (keys.cpp)
    if (const KilnResult r = input_feed_unsubscribe(h); r != KILN_ERR_NOT_FOUND) return r;  // window input (v5)
    if (game_unsubscribe(h)) return KILN_OK;  // menu entries, screen and UI-tick subscriptions
    // Frame callbacks run under Vulkan's mutex. Look up local handles first to avoid recursively locking it.
    if (t_in_frame) return KILN_ERR_WRONG_THREAD;
    if (vk_unsubscribe(h)) return KILN_OK;
    return KILN_ERR_NOT_FOUND;
}

// set_demand / set_capture take a count per calling native plugin, and the overlay uses their sum: kiln-emf (its HUD and
// window) and Kiln.Ui each send only their own count, so one of them sending 0 no longer ends the other's capture or
// drawing. The key is the module of the return address when it is a Kiln plugin (exports kiln_plugin_main); everything
// else (managed code, JIT-compiled or ReadyToRun, the runtime's stubs) shares one key, so a managed caller whose code moves
// (tiered compilation) keeps one count. The module of a return address is looked up once.
struct PerCaller {
    std::mutex mu;
    std::vector<std::pair<HMODULE, uint32_t>> counts;
    std::vector<std::pair<void*, HMODULE>> seen;  // return address -> module (call sites are few)
};
PerCaller g_demand_by, g_capture_by;

void per_caller(PerCaller& pc, std::atomic<uint32_t>& published, void* ret, uint32_t n) {
    std::lock_guard lk(pc.mu);
    HMODULE m = nullptr;
    auto at = std::find_if(pc.seen.begin(), pc.seen.end(), [&](const auto& x) { return x.first == ret; });
    if (at != pc.seen.end()) {
        m = at->second;
    } else {
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                                static_cast<LPCWSTR>(ret), &m) ||
            !GetProcAddress(m, "kiln_plugin_main"))
            m = nullptr;
        if (pc.seen.size() < 64) pc.seen.emplace_back(ret, m);
    }
    auto it = std::find_if(pc.counts.begin(), pc.counts.end(), [&](const auto& x) { return x.first == m; });
    if (it == pc.counts.end()) {
        if (n) pc.counts.emplace_back(m, n);
    } else if (n) {
        it->second = n;
    } else {
        pc.counts.erase(it);
    }
    uint64_t sum = 0;
    for (const auto& c : pc.counts) sum += c.second;
    // Publish before releasing the count lock: another caller must not publish its newer sum before ours.
    published.store(static_cast<uint32_t>(std::min<uint64_t>(sum, UINT32_MAX)));
}

void KILN_CALL t_set_demand(uint32_t n) { if (!overlay_drawing_on()) return; per_caller(g_demand_by, g_demand, _ReturnAddress(), n); }
void KILN_CALL t_set_capture(uint32_t n) { if (!overlay_drawing_on()) return; per_caller(g_capture_by, g_interactive, _ReturnAddress(), n); }
void KILN_CALL t_set_menu(int32_t open) {
    // the game page counts as the Kiln menu: closing (or toggling) the menu leaves it; opening keeps it
    if (page_active()) {
        if (open <= 0) page_leave();
        return;
    }
    g_menu_open = open < 0 ? !g_menu_open.load() : open != 0;
}
void KILN_CALL t_set_menu_key(uint32_t vk, uint32_t mods) {
    if (vk == 0 || vk > 255) return;
    g_menu_vk = vk;
    g_menu_mods = mods & (KILN_UI_MOD_CTRL | KILN_UI_MOD_SHIFT | KILN_UI_MOD_ALT);
}
void KILN_CALL t_key_name(uint32_t vk, char* buf, size_t cap) { key_name(vk, buf, cap); }

uint32_t KILN_CALL t_take_pressed_key(uint32_t* mods) { return input_take_pressed(mods); }

// ---- later additions ----
KilnResult KILN_CALL t_screen(KilnUiScreen* out) { return game_screen(out); }
KilnResult KILN_CALL t_subscribe_screen(KilnOwner o, KilnUiScreenFn fn, void* u, KilnHandle* out) { return game_subscribe_screen(o, fn, u, out); }
KilnResult KILN_CALL t_subscribe_ui_tick(KilnOwner o, KilnUiTickFn fn, void* u, KilnHandle* out) { return game_subscribe_tick(o, fn, u, out); }
KilnResult KILN_CALL t_menu_add(KilnOwner o, uint32_t menu, int32_t order, KilnStr text, KilnUiMenuFn fn, void* u, KilnHandle* out) {
    return game_menu_add(o, menu, order, text, fn, u, out);
}
KilnResult KILN_CALL t_menu_set(KilnHandle e, KilnStr text, uint32_t flags) { return game_menu_set(e, text, flags); }
KilnHandle KILN_CALL t_image_load(KilnOwner o, const void* png, size_t len) { if (!overlay_drawing_on()) return 0; return image_load(o, png, len); }
uint32_t KILN_CALL t_screen_target() { return game_screen_target(); }
int32_t KILN_CALL t_image_state(KilnHandle h) { return image_state(h); }
KilnHandle KILN_CALL t_image_load_rgba(KilnOwner o, const uint8_t* rgba, uint32_t w, uint32_t h) {
    if (!overlay_drawing_on()) return 0;
    return image_load_rgba(o, rgba, w, h);
}
bool KILN_CALL t_image_size(KilnHandle h, uint32_t* w, uint32_t* hh) { return image_size(h, w, hh); }
void KILN_CALL t_image_free(KilnHandle h) { image_free(h); }
// part of an image, trimmed images (0.2.0-dev.29.16)
bool KILN_CALL t_image_ex(KilnStr id, KilnHandle h, const KilnUiImageDraw* d) {
    if (!in_frame() || !d || d->size < sizeof(KilnUiImageDraw)) return false;
    const std::string label = id.ptr && id.len ? std::string(id.ptr, id.len) : std::string();
    return image_draw_ex(label.empty() ? nullptr : label.c_str(), h, d->flags, d->width, d->height, d->u0, d->v0, d->u1, d->v1);
}
bool KILN_CALL t_image_opaque_rect(KilnHandle h, uint32_t* x, uint32_t* y, uint32_t* w, uint32_t* hh) { return image_opaque_rect(h, x, y, w, hh); }
void KILN_CALL t_set_hover_capture(uint32_t n) { if (overlay_drawing_on()) g_hover_panels = n; }
KilnResult KILN_CALL t_wheel_open(KilnOwner o, const KilnUiWheel* def, KilnUiWheelFn fn, void* user, KilnHandle* out) {
    return wheel_open(o, def, fn, user, out);
}
KilnResult KILN_CALL t_wheel_close(KilnHandle h) { return wheel_close(h); }
bool KILN_CALL t_wheel_active() { return wheel_active(); }
KilnResult KILN_CALL t_subscribe_pre_overlay(KilnOwner, KilnUiPreOverlayFn fn, KilnUiSwapchainGoneFn gone, void* user, KilnHandle* out) {
    if (!overlay_drawing_on()) return KILN_ERR_UNAVAILABLE;
    return vk_subscribe_pre_overlay(fn, gone, user, out);
}

// v4: the main-menu page and fonts
KilnResult KILN_CALL t_menu_page(KilnUiMenuPage* out) { return page_state(out); }
void KILN_CALL t_menu_page_drawn() { page_drawn(); }
void KILN_CALL t_menu_page_leave() { page_leave(); }
void KILN_CALL t_menu_page_host(uint32_t on) {
    page_host(on);
    logf(KILN_LOG_INFO, "ui: a host %s the main-menu page", on ? "draws" : "no longer draws");
}

// v5: the window's input records (input.cpp)
KilnResult KILN_CALL t_subscribe_window_input(KilnOwner o, KilnUiWindowInputFn fn, void* u, KilnHandle* out) {
    return input_feed_subscribe(o, fn, u, out);
}

// v6: mods' entries in the game's menus (game.cpp, menu_rows.h)
void KILN_CALL t_menu_shown(uint32_t mode) { game_menu_shown(mode); }
uint32_t KILN_CALL t_menu_entries(KilnUiMenuEntry* out, uint32_t capacity) { return game_menu_entries(out, capacity); }
KilnResult KILN_CALL t_menu_activate(KilnHandle entry) { return game_menu_activate(entry); }
uint32_t KILN_CALL t_menu_scroll() { return scroll_status(); }

// v7: key callbacks that take mouse presses too (keys.cpp)
KilnResult KILN_CALL t_subscribe_key_ex(KilnOwner o, KilnUiKeyFn fn, void* u, uint32_t flags, KilnHandle* out) {
    return keys_subscribe(o, fn, u, flags, out);
}

// Kiln's line in the main menu's footer (menu_footer.cpp)
void KILN_CALL t_menu_footer(int32_t mods, uint32_t flags) { footer_set(mods, flags); }

// Looks, themes and fonts (style.cpp, 0.2.0-dev.29.16). Mods never set the player's look: set_look from a native plugin
// (a module that exports kiln_plugin_main; per_caller below tells them apart the same way) is refused. Kiln.Ui's managed
// call comes from no such module.
bool caller_is_plugin(void* ret) {
    HMODULE m = nullptr;
    return GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                              static_cast<LPCWSTR>(ret), &m) &&
           m && GetProcAddress(m, "kiln_plugin_main") != nullptr;
}
KilnResult KILN_CALL t_theme_create(KilnOwner o, const KilnUiThemeDef* def, KilnHandle* out) { return theme_create(o, def, out); }
KilnResult KILN_CALL t_theme_free(KilnHandle h) { return theme_free(h); }
KilnResult KILN_CALL t_set_look(const KilnUiLook* look) {
    if (caller_is_plugin(_ReturnAddress())) {
        static std::atomic<bool> said{false};
        if (!said.exchange(true)) logf(KILN_LOG_WARN, "ui: a native plugin called set_look: refused (the player chooses Kiln's look)");
        return KILN_ERR_UNAVAILABLE;
    }
    return look_set(look);
}
KilnResult KILN_CALL t_look_info(KilnUiLookInfo* out) { return look_info(out); }

// Kiln's own graphics tools (graphics.cpp, 0.2.0-dev.29.18). Like set_look, graphics_set is Kiln.Ui's call only: the player
// chooses them. graphics_status is anyone's.
KilnResult KILN_CALL t_graphics_set(const KilnUiGraphics* g) {
    if (caller_is_plugin(_ReturnAddress())) {
        static std::atomic<bool> said{false};
        if (!said.exchange(true)) logf(KILN_LOG_WARN, "ui: a native plugin called graphics_set: refused (the player chooses Kiln's graphics settings)");
        return KILN_ERR_UNAVAILABLE;
    }
    return graphics_set(g);
}
KilnResult KILN_CALL t_graphics_status(KilnUiGraphicsStatus* out) { return graphics_status(out); }

// Kiln's own texts in the player's language (ui_text.h). Like set_look, Kiln.Ui's call only: the player chooses the language.
KilnResult KILN_CALL t_set_texts(const KilnUiTexts* t) {
    if (caller_is_plugin(_ReturnAddress())) {
        static std::atomic<bool> said{false};
        if (!said.exchange(true)) logf(KILN_LOG_WARN, "ui: a native plugin called set_texts: refused (the player chooses Kiln's language)");
        return KILN_ERR_UNAVAILABLE;
    }
    if (!t || t->size < sizeof(KilnUiTexts) || t->count > 4096 || (t->count && (!t->keys || !t->texts)))
        return KILN_ERR_INVALID_ARGUMENT;
    std::vector<std::pair<std::string, std::string>> pairs;
    pairs.reserve(t->count);
    for (uint32_t i = 0; i < t->count; ++i) {
        const KilnStr k = t->keys[i], v = t->texts[i];
        pairs.emplace_back(std::string(k.ptr ? k.ptr : "", k.ptr ? k.len : 0), std::string(v.ptr ? v.ptr : "", v.ptr ? v.len : 0));
    }
    const std::string language(t->language.ptr ? t->language.ptr : "", t->language.ptr ? t->language.len : 0);
    if (!text::table().set(language, pairs)) return KILN_OK;
    logf(KILN_LOG_INFO, "ui: Kiln's texts: %s (%u native texts)", language.empty() ? "the game's language" : language.c_str(), t->count);
    game_texts_changed();
    footer_texts_changed();
    return KILN_OK;
}
KilnHandle KILN_CALL t_font_load(KilnOwner o, const void* data, size_t len) {
    if (!overlay_drawing_on()) return 0;
    return font_load(o, data, len);
}
void KILN_CALL t_font_free(KilnHandle h) { font_free(h); }

// kiln.ui.tick (for Kiln.Host)
KilnResult KILN_CALL tt_subscribe(KilnOwner o, KilnUiFrameTickFn fn, void* u, KilnHandle* out) { return game_subscribe_frame_tick(o, fn, u, out); }
KilnResult KILN_CALL tt_unsubscribe(KilnHandle h) { return game_unsubscribe(h) ? KILN_OK : KILN_ERR_NOT_FOUND; }
bool KILN_CALL tt_in_menu() { return game_in_menu(); }
const KilnUiTickApi kTickTable = {{sizeof(KilnUiTickApi), KILN_UI_TICK_VERSION}, &tt_subscribe, &tt_unsubscribe, &tt_in_menu};

// Kiln.Ui's managed table mirrors these (UiNative.Table2, WheelDef; managed/tests/Kiln.Ui.Tests/WheelTests.cs checks the same numbers)
static_assert(offsetof(KilnUiApi, is_item_clicked) == offsetof(KilnUiApi, set_hover_capture) + 4 * sizeof(void*),
              "kiln.ui v2 slots follow add_swapchain_usage");
static_assert(offsetof(KilnUiApi, push_font_size) == offsetof(KilnUiApi, wheel_active) + sizeof(void*),
              "kiln.ui v3 slots follow wheel_active");
static_assert(offsetof(KilnUiApi, menu_page) == offsetof(KilnUiApi, set_scroll_here_y) + sizeof(void*),
              "kiln.ui v4 slots follow set_scroll_here_y");
static_assert(offsetof(KilnUiApi, subscribe_window_input) == offsetof(KilnUiApi, pop_font) + sizeof(void*),
              "kiln.ui v5 appends subscribe_window_input after pop_font");
static_assert(offsetof(KilnUiApi, menu_shown) == offsetof(KilnUiApi, subscribe_window_input) + sizeof(void*) &&
                  offsetof(KilnUiApi, menu_scroll) == offsetof(KilnUiApi, menu_shown) + 3 * sizeof(void*),
              "kiln.ui v6 appends menu_shown, menu_entries, menu_activate, menu_scroll after subscribe_window_input");
static_assert(offsetof(KilnUiApi, subscribe_key_ex) == offsetof(KilnUiApi, menu_scroll) + sizeof(void*),
              "kiln.ui v7 appends subscribe_key_ex after menu_scroll (UiNative.Table7)");
static_assert(offsetof(KilnUiApi, set_next_window_size_constraints) == offsetof(KilnUiApi, subscribe_key_ex) + sizeof(void*),
              "kiln.ui appends size constraints after subscribe_key_ex");
static_assert(offsetof(KilnUiApi, menu_footer) == offsetof(KilnUiApi, set_next_window_size_constraints) + sizeof(void*),
              "kiln.ui appends menu_footer after the size constraints (UiNative.TableFooter)");
static_assert(offsetof(KilnUiApi, image_load_rgba) == offsetof(KilnUiApi, menu_footer) + sizeof(void*) &&
                  offsetof(KilnUiApi, screen_target) == offsetof(KilnUiApi, image_load_rgba) + sizeof(void*) &&
                  offsetof(KilnUiApi, image_state) == offsetof(KilnUiApi, screen_target) + sizeof(void*),
              "kiln.ui appends image_load_rgba, screen_target and image_state after menu_footer (UiNative.TableImageRgba, TableScreenTarget, TableImageState)");
static_assert(offsetof(KilnUiApi, theme_create) == 928 && offsetof(KilnUiApi, theme_free) == 936 &&
                  offsetof(KilnUiApi, push_theme) == 944 && offsetof(KilnUiApi, set_look) == 952 &&
                  offsetof(KilnUiApi, look_info) == 960 && offsetof(KilnUiApi, font_load) == 968 &&
                  offsetof(KilnUiApi, font_free) == 976 && offsetof(KilnUiApi, push_font_handle) == 984 &&
                  offsetof(KilnUiApi, image_ex) == 992,
              "kiln.ui appends the looks after image_state (0.2.0-dev.29.16; UiNative.TableLooks)");
static_assert(offsetof(KilnUiApi, image_ex) == offsetof(KilnUiApi, push_font_handle) + sizeof(void*) &&
                  offsetof(KilnUiApi, image_opaque_rect) == 1000,
              "kiln.ui appends image_ex and image_opaque_rect after the looks (0.2.0-dev.29.16; UiNative.TableImageEx)");
static_assert(sizeof(KilnUiImageDraw) == 32 && offsetof(KilnUiImageDraw, width) == 8 && offsetof(KilnUiImageDraw, u0) == 16,
              "KilnUiImageDraw layout (UiNative.ImageDraw)");
static_assert(offsetof(KilnUiApi, set_texts) == 1008,
              "kiln.ui appends set_texts after image_opaque_rect (0.2.0-dev.29.16; UiNative.TableTexts)");
static_assert(offsetof(KilnUiApi, graphics_set) == 1016 && offsetof(KilnUiApi, graphics_status) == 1024 && sizeof(KilnUiApi) == 1032,
              "kiln.ui appends graphics_set and graphics_status after set_texts (0.2.0-dev.29.18; UiNative.TableGraphics)");
static_assert(sizeof(KilnUiGraphics) == 48 && offsetof(KilnUiGraphics, custom_scale) == 16 && offsetof(KilnUiGraphics, mode_scale) == 20 &&
                  offsetof(KilnUiGraphics, readout) == 36,
              "KilnUiGraphics layout (UiNative.Graphics)");
static_assert(sizeof(KilnUiGraphicsStatus) == 72 && offsetof(KilnUiGraphicsStatus, rendered_fps) == 20 &&
                  offsetof(KilnUiGraphicsStatus, output_width) == 32 && offsetof(KilnUiGraphicsStatus, dlss_quality) == 40 &&
                  offsetof(KilnUiGraphicsStatus, render_width) == 48 && offsetof(KilnUiGraphicsStatus, dlss_output_width) == 56,
              "KilnUiGraphicsStatus layout (UiNative.GraphicsStatus)");
static_assert(sizeof(KilnUiTexts) == 40 && offsetof(KilnUiTexts, language) == 8 && offsetof(KilnUiTexts, keys) == 24 &&
                  offsetof(KilnUiTexts, texts) == 32,
              "KilnUiTexts layout (UiNative.Texts)");
static_assert(sizeof(KilnUiThemeDef) == 360 && offsetof(KilnUiThemeDef, color_mask) == 16 && offsetof(KilnUiThemeDef, roles) == 24 &&
                  offsetof(KilnUiThemeDef, colors) == 88 && offsetof(KilnUiThemeDef, vars) == 216 &&
                  offsetof(KilnUiThemeDef, background) == 344 && offsetof(KilnUiThemeDef, font) == 352,
              "KilnUiThemeDef layout (UiNative.ThemeDef)");
static_assert(sizeof(KilnUiLook) == 32 && offsetof(KilnUiLook, theme) == 8 && offsetof(KilnUiLook, background) == 16,
              "KilnUiLook layout (UiNative.Look)");
static_assert(sizeof(KilnUiLookInfo) == 104 && offsetof(KilnUiLookInfo, theme) == 8 && offsetof(KilnUiLookInfo, game_colors) == 28 &&
                  offsetof(KilnUiLookInfo, roles) == 32 && offsetof(KilnUiLookInfo, role_count) == 96,
              "KilnUiLookInfo layout (UiNative.LookInfo)");
static_assert(int{KILN_UI_IMAGE_UNKNOWN} == int{img::kUnknown} && int{KILN_UI_IMAGE_WAITING} == int{img::kWaiting} &&
                  int{KILN_UI_IMAGE_READY} == int{img::kReady} && int{KILN_UI_IMAGE_FAILED} == int{img::kFailed},
              "image states (image_budget.h)");
static_assert(sizeof(KilnUiMenuEntry) == 40 && offsetof(KilnUiMenuEntry, entry) == 8 && offsetof(KilnUiMenuEntry, text) == 16 &&
                  offsetof(KilnUiMenuEntry, drawn) == 36,
              "KilnUiMenuEntry layout (UiNative.MenuEntryInfo)");
static_assert(sizeof(KilnUiWindowInput) == 48 && offsetof(KilnUiWindowInput, wparam) == 32 &&
                  offsetof(KilnUiWindowInput, lparam) == 40,
              "KilnUiWindowInput layout");
static_assert(sizeof(KilnUiMenuPage) == 104 && offsetof(KilnUiMenuPage, ui_frame) == 16 &&
              offsetof(KilnUiMenuPage, canvas_width) == 40 && offsetof(KilnUiMenuPage, x0) == 84,
              "KilnUiMenuPage layout (UiNative.MenuPage)");
static_assert(sizeof(KilnUiCursor) == 60 && sizeof(KilnUiItem) == 28, "KilnUiCursor / KilnUiItem layout (UiNative.Cursor / Item)");
static_assert(sizeof(KilnUiWheel) == 40 && offsetof(KilnUiWheel, hold_key) == 36, "KilnUiWheel layout");

const KilnUiApi kTable = {
    {sizeof(KilnUiApi), KILN_UI_VERSION},
    &t_status, &t_subscribe_frame, &t_subscribe_key, &t_unsubscribe, &t_set_demand, &t_set_capture, &t_set_menu,
    &t_set_menu_key, &t_key_name,
    &t_begin, &t_end, &t_text, &t_text_colored, &t_text_wrapped, &t_text_disabled, &t_button, &t_small_button,
    &t_checkbox, &t_slider_float, &t_slider_int, &t_input_text, &t_collapsing_header, &t_tree_node, &t_tree_pop,
    &t_selectable, &t_separator, &t_separator_text, &t_same_line, &t_spacing, &t_push_id, &t_pop_id,
    &t_set_next_window_pos, &t_set_next_window_size, &t_set_next_window_bg_alpha, &t_progress_bar, &t_begin_table,
    &t_table_setup_column, &t_table_headers_row, &t_table_next_row, &t_table_next_column, &t_end_table,
    &t_begin_tab_bar, &t_begin_tab_item, &t_end_tab_item, &t_end_tab_bar, &t_begin_child, &t_end_child,
    &t_is_item_hovered, &t_set_tooltip, &t_draw_text, &t_draw_rect, &t_text_size, &t_take_pressed_key,
    // later additions
    &t_screen, &t_subscribe_screen, &t_subscribe_ui_tick, &t_menu_add, &t_menu_set, &t_push_style, &t_pop_style,
    &t_image_load, &t_image_size, &t_image, &t_image_button, &t_image_free, &t_set_next_window_focus, &t_combo,
    &t_input_multiline, &t_dummy, &t_window_rect, &t_set_hover_capture,
    &t_subscribe_pre_overlay, &vk_set_pre_overlay_demand, &vk_add_swapchain_usage,
    // v2
    &t_is_item_clicked, &t_wheel_open, &t_wheel_close, &t_wheel_active,
    // v3
    &t_push_font_size, &t_pop_font_size, &t_begin_child_ex, &t_set_next_item_width, &t_cursor, &t_set_cursor_pos,
    &t_item, &t_selectable_ex, &t_set_keyboard_focus_here, &t_set_item_default_focus, &t_draw_rect_in, &t_draw_text_in,
    &t_text_size_ex, &t_push_color, &t_pop_color, &t_push_var, &t_pop_var, &t_key_pressed, &t_key_mods,
    &t_set_scroll_here_y,
    // v4
    &t_menu_page, &t_menu_page_drawn, &t_menu_page_leave, &t_menu_page_host, &t_push_font, &t_pop_font,
    // v5
    &t_subscribe_window_input,
    // v6
    &t_menu_shown, &t_menu_entries, &t_menu_activate, &t_menu_scroll,
    // v7
    &t_subscribe_key_ex,
    &t_set_next_window_size_constraints,
    &t_menu_footer,
    &t_image_load_rgba,  // 0.2.0-dev.29.15
    &t_screen_target,    // 0.2.0-dev.29.15
    &t_image_state,      // 0.2.0-dev.29.15
    // looks, themes and fonts (0.2.0-dev.29.16)
    &t_theme_create, &t_theme_free, &t_push_theme, &t_set_look, &t_look_info, &t_font_load, &t_font_free, &t_push_font_handle,
    // part of an image, trimmed images (0.2.0-dev.29.16)
    &t_image_ex, &t_image_opaque_rect,
    &t_set_texts,  // Kiln's own texts (0.2.0-dev.29.16)
    &t_graphics_set, &t_graphics_status,  // Kiln's own graphics tools (0.2.0-dev.29.18)
};

std::string api_path(int kind) {
    char b[1024];
    size_t n = 0;
    if (g_api->path(kind, b, sizeof b, &n) != KILN_OK) return {};
    return std::string(b, n);
}

}  // namespace

namespace kui {
void start_drawn(const char* what) {
    static std::atomic<bool> said{false};
    if (said.load(std::memory_order_relaxed) || said.exchange(true) || !g_api) return;
    const auto* t = static_cast<const KilnProgressApi*>(g_api->service(KilnStr{KILN_PROGRESS_SERVICE, sizeof(KILN_PROGRESS_SERVICE) - 1}, 1));
    if (!t || t->header.size < offsetof(KilnProgressApi, mark) + sizeof(void*) || !t->mark) return;  // a core older than dev.27
    const KilnResult rc = t->mark(KILN_PROGRESS_MARK_MENU);
    logf(KILN_LOG_INFO, "ui: %s drew: told the start's progress (rc %d)", what, static_cast<int>(rc));
}
}  // namespace kui

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < offsetof(KilnApi, side)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = KilnStr{KILN_UI_SERVICE, sizeof(KILN_UI_SERVICE) - 1};
    info->version = KilnStr{"1", 1};
    uint32_t side = 0;
    if (api->size >= offsetof(KilnApi, side) + sizeof(void*)) {
        side = api->side();
    } else {
        KilnBuildInfo bi{};
        bi.size = sizeof bi;
        if (api->build_info(&bi) == KILN_OK) side = bi.side;
    }
    if (side != KILN_SIDE_CLIENT) return KILN_ERR_UNAVAILABLE;  // no window on a dedicated server
    char env[8];
    if (GetEnvironmentVariableA("KILN_UI_DISABLE", env, sizeof env) > 0 && env[0] == '1') {
        logf(KILN_LOG_INFO, "ui: disabled by KILN_UI_DISABLE=1");
        return KILN_ERR_UNAVAILABLE;
    }
    overlay_guard_start();
    {
        // Kiln's own graphics tools: kiln.json "graphics" and what they step aside for (graphics.cpp)
        bool optiscaler = false, streamline = false;
        overlay_guard_tools(&optiscaler, &streamline);
        graphics_start(optiscaler, streamline);
    }
    try {
        const std::string data = api_path(KILN_PATH_DATA);
        if (!data.empty()) {
            std::filesystem::path p = std::filesystem::path(std::u8string(data.begin(), data.end())) / "kiln.ui";
            std::filesystem::create_directories(p);
            const auto s = (p / "imgui.ini").u8string();
            g_ini_path.assign(s.begin(), s.end());
            g_ini.start(g_ini_path);  // read now, on its thread: the first frame finds it in memory
        }
    } catch (...) {
    }
    // the game's own UI (screens, menu entries, crafting): independent of the overlay's Vulkan hooks
    const bool game = game_install();
    render_install();  // kiln.render's camera (screen drawing works without it)
    info->on_ready = [](void*) { game_ready(); };
    // the process exits with code 0 (kiln_api.h on_unload: under Wine, and on Windows where kiln.json turns it on): the
    // scrolling pause menu's crash guard knows the game did not end in its calls (menu_scroll.cpp), and the overlay's
    // crash guard that the overlay's start did not end it (overlay_guard.cpp); chat history's last file write is done
    // (chat_input_probe.cpp)
    if (info->size >= offsetof(KilnPluginInfo, on_unload) + sizeof(info->on_unload))
        info->on_unload = [](void*) { scroll_exit(); overlay_guard_exit(); chat_input_exit(); };
    // the cursor import slots are patched once vulkan-1.dll loads (vk_install), long after the exe's own start-up code
    if (!overlay_drawing_on()) {
        // No Vulkan load observer, exports, objects or pre-overlay callbacks in this mode.
    } else if (GetModuleHandleW(L"vulkan-1.dll")) {
        vk_install();
        // NVIDIA's DLSS core loads after vulkan-1.dll: its hooks need the load hook all the same
        if (graphics_wants_load_hook() && !load_hook::install(&after_library_load))
            logf(KILN_LOG_WARN, "graphics: hooking LoadLibraryExW failed: Kiln's DLSS settings are off");
        graphics_after_load();
    } else {
        if (!load_hook::install(&after_library_load)) {
            logf(KILN_LOG_ERROR, "ui: hooking LoadLibraryExW failed: no overlay");
            return KILN_ERR_UNAVAILABLE;
        }
        logf(KILN_LOG_INFO, "ui: waiting for the game to load vulkan-1.dll (LoadLibraryExW hooked)");
    }
    if (game) api->register_service(owner, KilnStr{KILN_UI_TICK_SERVICE, sizeof(KILN_UI_TICK_SERVICE) - 1}, &kTickTable);
    api->register_service(owner, KilnStr{KILN_RENDER_SERVICE, sizeof(KILN_RENDER_SERVICE) - 1}, render_table());
    const auto result = api->register_service(owner, KilnStr{KILN_UI_SERVICE, sizeof(KILN_UI_SERVICE) - 1}, &kTable);
    if (!overlay_drawing_on()) {
        const auto* caps = static_cast<const KilnCapsApi*>(api->service(KilnStr{KILN_CAPS_SERVICE, sizeof(KILN_CAPS_SERVICE)-1}, 1));
        if (caps && caps->report) {
            for (const char* name : {"ui.windows", "ui.draw", "ui.menu_page", "render.draw", "kiln.screen"})
                caps->report(owner, KilnStr{name, std::strlen(name)}, KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_POLICY,
                             KilnStr{g_overlay_reason.data(), g_overlay_reason.size()});
        }
    }
    return result;
}
