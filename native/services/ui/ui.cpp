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
#include <mutex>
#include <memory>
#include <string>
#include <vector>
#include <unordered_map>

#include "backends/imgui_impl_win32.h"
#include "imgui.h"
#include "safetyhook.hpp"
#include "services/kiln_render.h"
#include "ui.h"
#include "slider.h"
#include "draw_slots.h"

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

namespace {
std::mutex g_err_mu;
char g_error[128] = "";
uint64_t g_frame_no = 0;
uint64_t g_last_frame_ms = 0;
float g_style_scale = 0;
std::string g_ini_path;
bool g_imgui = false;
thread_local bool t_in_frame = false;
thread_local CallbackLifetime* t_callback = nullptr;

struct FrameSub : CallbackLifetime {
    KilnHandle handle;
    KilnOwner owner;
    KilnUiFrameFn fn;
    void* user;
};
struct KeySub : CallbackLifetime {
    KilnHandle handle;
    KilnOwner owner;
    KilnUiKeyFn fn;
    void* user;
};
std::mutex g_sub_mu;
std::vector<std::shared_ptr<FrameSub>> g_frame_subs;
std::vector<std::shared_ptr<KeySub>> g_key_subs;
// Keep canceled calls discoverable while another thread drains them (including self-unsubscribe during cleanup).
std::unordered_map<KilnHandle, std::shared_ptr<CallbackLifetime>> g_retiring_subs;

SafetyHookInline g_load_library;
using LoadLibraryExWFn = HMODULE(WINAPI*)(LPCWSTR, HANDLE, DWORD);

HMODULE WINAPI hk_load_library_ex_w(LPCWSTR name, HANDLE file, DWORD flags) {
    HMODULE m = g_load_library.call<HMODULE>(name, file, flags);
    // every load: vulkan-1.dll present and our hooks not in its code (first load, or a reload) -> (re)install
    if (m && GetModuleHandleW(L"vulkan-1.dll")) vk_install();
    if (m) input_hook_xinput();
    return m;
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

bool guarded_key(KilnUiKeyFn fn, const KilnUiKey* k, void* user, bool* consumed) {
    __try {
        *consumed = fn(k, user);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void apply_style(float scale) {
    style_apply_base(scale);  // Kiln's ember look (style.cpp); game-style windows push KILN_UI_STYLE_GAME
    g_style_scale = scale;
}

void fallback_menu() {
    const ImVec2 ds = ImGui::GetIO().DisplaySize;
    ImGui::SetNextWindowPos(ImVec2(ds.x * 0.5f, ds.y * 0.3f), ImGuiCond_Always, ImVec2(0.5f, 0.5f));
    ImGui::SetNextWindowSize(ImVec2(420.0f * g_style_scale, 0.0f), ImGuiCond_Always);  // height fits the text
    bool open = true;
    if (ImGui::Begin("Kiln###kiln.menu.native", &open, ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoCollapse)) {
        KilnStr v = g_api->version();
        ImGui::Text("Kiln %.*s", static_cast<int>(v.len), v.ptr);
        ImGui::Separator();
        ImGui::TextWrapped("Kiln's managed host (Kiln.Ui) is not running in this session, so the mod list and mod screens are "
                           "unavailable here. Check kiln/logs/kiln.log.");
        char key[32];
        key_name(g_menu_vk.load(), key, sizeof key);
        ImGui::TextDisabled("Press %s to close.", key);
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
    io.IniFilename = g_ini_path.empty() ? nullptr : g_ini_path.c_str();
    io.ConfigFlags |= ImGuiConfigFlags_NavEnableKeyboard | ImGuiConfigFlags_NavEnableGamepad | ImGuiConfigFlags_NoMouseCursorChange;
    io.MouseDrawCursor = false;
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
    if (dpi != g_style_scale) apply_style(dpi);
    const uint64_t t = now_ms();
    const float dt = g_last_frame_ms ? std::clamp((t - g_last_frame_ms) / 1000.0f, 0.0001f, 0.25f) : 1.0f / 60.0f;
    g_last_frame_ms = t;
    io.DeltaTime = dt;
    style_frame_begin();  // the game's font and new images join the atlas / texture list before the frame
    ImGui::NewFrame();
    KilnUiFrame f{};
    f.size = sizeof f;
    f.width = width;
    f.height = height;
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

bool dispatch_key(const KilnUiKey& key) {
    // the menu key first (works without the managed host)
    if (key.down && !key.repeat && !key.text_input && key.vk == g_menu_vk.load() && key.mods == g_menu_mods.load()) {
        g_menu_open = !g_menu_open.load();
        logf(KILN_LOG_DEBUG, "ui: Kiln menu %s", g_menu_open ? "open" : "closed");
        return true;
    }
    if (key.down && key.vk == VK_ESCAPE && g_menu_open.load() && !key.text_input) {
        g_menu_open = false;
        return true;
    }
    if (wheel_active() && wheel_on_key(key)) return true;  // Esc cancels the wheel; its hold key's release is noted
    std::vector<std::shared_ptr<KeySub>> subs;
    {
        std::lock_guard<std::mutex> lk(g_sub_mu);
        subs = g_key_subs;
    }
    bool consumed = false;
    for (const auto& s : subs) {
        std::lock_guard call(s->gate);
        if (!s->active.load(std::memory_order_acquire)) continue;
        bool c = false;
        auto* outer = t_callback;
        t_callback = s.get();
        const bool ok = guarded_key(s->fn, &key, s->user, &c);
        t_callback = outer;
        if (!ok) {
            s->active.store(false, std::memory_order_release);
            set_error("a key callback faulted; removed");
            std::lock_guard<std::mutex> lk(g_sub_mu);
            std::erase_if(g_key_subs, [&](const auto& x) { return x == s; });
            continue;
        }
        consumed |= c;
    }
    return consumed;
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
    vk_status(out);
    out->window = input_attached() ? 1 : 0;
    out->menu_open = g_menu_open.load() ? 1 : 0;
    out->capturing = capturing() ? 1 : 0;
    {
        std::lock_guard<std::mutex> lk(g_sub_mu);
        out->subscribers = static_cast<uint32_t>(g_frame_subs.size() + g_key_subs.size());
    }
    std::lock_guard<std::mutex> lk(g_err_mu);
    std::snprintf(out->error, sizeof out->error, "%s", g_error);
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
    if (!fn) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard<std::mutex> lk(g_sub_mu);
    const KilnHandle h = next_handle();
    auto sub = std::make_shared<KeySub>();
    sub->handle = h;
    sub->owner = owner;
    sub->fn = fn;
    sub->user = user;
    g_key_subs.push_back(std::move(sub));
    if (out) *out = h;
    return KILN_OK;
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
        std::erase_if(g_key_subs, erase);
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

void KILN_CALL t_set_demand(uint32_t n) { per_caller(g_demand_by, g_demand, _ReturnAddress(), n); }
void KILN_CALL t_set_capture(uint32_t n) { per_caller(g_capture_by, g_interactive, _ReturnAddress(), n); }
void KILN_CALL t_set_menu(int32_t open) { g_menu_open = open < 0 ? !g_menu_open.load() : open != 0; }
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
KilnHandle KILN_CALL t_image_load(KilnOwner o, const void* png, size_t len) { return image_load(o, png, len); }
bool KILN_CALL t_image_size(KilnHandle h, uint32_t* w, uint32_t* hh) { return image_size(h, w, hh); }
void KILN_CALL t_image_free(KilnHandle h) { image_free(h); }
void KILN_CALL t_set_hover_capture(uint32_t n) { g_hover_panels = n; }
KilnResult KILN_CALL t_wheel_open(KilnOwner o, const KilnUiWheel* def, KilnUiWheelFn fn, void* user, KilnHandle* out) {
    return wheel_open(o, def, fn, user, out);
}
KilnResult KILN_CALL t_wheel_close(KilnHandle h) { return wheel_close(h); }
bool KILN_CALL t_wheel_active() { return wheel_active(); }
KilnResult KILN_CALL t_subscribe_pre_overlay(KilnOwner, KilnUiPreOverlayFn fn, KilnUiSwapchainGoneFn gone, void* user, KilnHandle* out) {
    return vk_subscribe_pre_overlay(fn, gone, user, out);
}

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
};

std::string api_path(int kind) {
    char b[1024];
    size_t n = 0;
    if (g_api->path(kind, b, sizeof b, &n) != KILN_OK) return {};
    return std::string(b, n);
}

}  // namespace

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
    try {
        const std::string data = api_path(KILN_PATH_DATA);
        if (!data.empty()) {
            std::filesystem::path p = std::filesystem::path(std::u8string(data.begin(), data.end())) / "kiln.ui";
            std::filesystem::create_directories(p);
            const auto s = (p / "imgui.ini").u8string();
            g_ini_path.assign(s.begin(), s.end());
        }
    } catch (...) {
    }
    // the game's own UI (screens, menu entries, crafting): independent of the overlay's Vulkan hooks
    const bool game = game_install();
    render_install();  // kiln.render's camera (screen drawing works without it)
    info->on_ready = [](void*) { game_ready(); };
    // the cursor import slots are patched once vulkan-1.dll loads (vk_install), long after the exe's own start-up code
    if (GetModuleHandleW(L"vulkan-1.dll")) {
        vk_install();
    } else {
        void* target = reinterpret_cast<void*>(GetProcAddress(GetModuleHandleW(L"kernelbase.dll"), "LoadLibraryExW"));
        if (!target) target = reinterpret_cast<void*>(GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryExW"));
        g_load_library = safetyhook::create_inline(target, reinterpret_cast<void*>(&hk_load_library_ex_w));
        if (!g_load_library) {
            logf(KILN_LOG_ERROR, "ui: hooking LoadLibraryExW failed: no overlay");
            return KILN_ERR_UNAVAILABLE;
        }
        logf(KILN_LOG_INFO, "ui: waiting for the game to load vulkan-1.dll (LoadLibraryExW hooked)");
    }
    if (game) api->register_service(owner, KilnStr{KILN_UI_TICK_SERVICE, sizeof(KILN_UI_TICK_SERVICE) - 1}, &kTickTable);
    api->register_service(owner, KilnStr{KILN_RENDER_SERVICE, sizeof(KILN_RENDER_SERVICE) - 1}, render_table());
    return api->register_service(owner, KilnStr{KILN_UI_SERVICE, sizeof(KILN_UI_SERVICE) - 1}, &kTable);
}
