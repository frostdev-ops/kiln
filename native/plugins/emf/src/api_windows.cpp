// SPDX-License-Identifier: MIT
// The EmfApi table, v24 mod windows (window_open, window_update, window_close) and v26 pictures (image_load), drawn
// with kiln.ui (Kiln's ImGui overlay) in the game client.
//  - One window at a time, as in EMF. While it is open kiln-emf takes the mouse and keyboard (kiln.ui capture); Esc,
//    its close_key or its X close it (CLOSED, BY_PLAYER). Clicks are queued on the render thread and reach the mod's
//    callback after the next step of the world the window named, on that world's thread, as EMF delivers them.
//  - A dedicated server has no overlay: window_open is refused and image_load answers 0 there (EMF's behaviour).
//  - Right clicks on a widget with a key (button, row, slot, picture) are EMF_UI_RIGHT_CLICK events, like the left clicks
//    (kiln.ui v2's is_item_clicked); a kiln.ui without it reports none.
//  - Differences: EMF's console look and its debug-bridge commands are not reproduced (the widgets are plain ImGui
//    widgets).
//  - window_open is refused while a wheel is open (api_wheel.cpp), as EMF documents.
//  - Pictures: PNG files (or a folder of PNG frames) decoded by kiln.ui's image_load; EMF's limits (512 x 512, 64
//    frames, 128 pictures, 64 MB) and path rules (the mod's folder, absolute, "portrait:<key>").
#include <intrin.h>

#include <algorithm>
#include <cmath>
#include <deque>
#include <fstream>
#include <map>
#include <optional>
#include <set>

#include "emf_internal.h"
#include "v26_model.h"

namespace kemf {

namespace {

struct WindowLifetime { std::atomic<bool> ended{false}; };

struct Pending {
    EmfUiCallback cb;
    void* user;
    Mod* mod;
    std::string window, key;
    uint32_t kind, reason;
    uint8_t world_kind;
    std::shared_ptr<WindowLifetime> lifetime;
};

std::mutex g_win_mutex;
std::optional<v26::WindowRequest> g_win;  // the open window
Mod* g_win_mod = nullptr;
std::mutex g_ui_mutex;
std::atomic<bool> g_shutdown{false};
bool g_stopping[KILN_WORLD_KINDS]{};
std::deque<Pending> g_events;
std::shared_ptr<WindowLifetime> g_win_lifetime;
std::vector<Pending> g_targets; // includes windows whose events are in an in-flight delivery batch
KilnHandle g_frame = 0, g_key = 0;
std::atomic<bool> g_open{false};

struct Image {
    std::vector<KilnHandle> frames;
    uint32_t w = 0, h = 0;
    std::string pending;  // a portrait key still being drawn: no frames yet, loaded when the drawing finishes
};
std::mutex g_img_mutex;
std::vector<Image> g_images;           // handle = index + 1
std::map<std::string, uint32_t> g_image_keys;
std::set<std::string> g_image_refused;  // logged once per path
uint64_t g_image_bytes = 0;

void deliver(Pending& p, EmfWorld* w, bool no_world) {
    if (!p.cb || !p.lifetime) return;
    if (p.kind == EMF_UI_CLOSED) {
        if (p.lifetime->ended.exchange(true)) return;
    } else if (p.lifetime->ended.load()) return;
    EmfUiEvent e{};
    e.size = sizeof e;
    e.kind = p.kind;
    e.window = p.window.c_str();
    e.key = p.key.c_str();
    e.from_key = "";
    e.reason = p.reason;
    e.flags = no_world ? EMF_UI_NO_WORLD : 0u;
    struct Call {
        EmfUiCallback cb;
        EmfWorld* w;
        const EmfUiEvent* e;
        void* user;
    } c{p.cb, w, &e, p.user};
    ContextScope scope(p.mod, w, w != nullptr);
    guarded(p.mod, "a window callback",
            [](void* q) {
                auto* x = static_cast<Call*>(q);
                x->cb(x->w, x->e, x->user);
            },
            &c);
}

// Queues an event of the open (or just closed) window `r`. A CLOSED without a world of its kind is delivered now.
void post_event_locked(const v26::WindowRequest& r, Mod* m, uint32_t kind, const std::string& key, uint32_t reason,
                       std::vector<Pending>* now) {
    Pending p{r.cb, r.user, m, r.id, key, kind, reason, r.world_kind, g_win_lifetime};
    if (kind != EMF_UI_CLOSED && g_events.size() >= 256) return;
    if (kind == EMF_UI_CLOSED && !g_api->world(r.world_kind)) now->push_back(std::move(p));
    else g_events.push_back(std::move(p));
}

void run_now(std::vector<Pending>& now) {
    for (auto& p : now) deliver(p, nullptr, true);
}

void set_open(bool on) {
    g_open = on;
    if (!S.ui) return;
    S.ui->set_capture(on ? 1u : 0u);
    ui_refresh_demand();
}

// Closes the open window (lock held); its CLOSED goes into `now` or the queue.
void close_locked(uint32_t reason, std::vector<Pending>* now) {
    if (!g_win) return;
    post_event_locked(*g_win, g_win_mod, EMF_UI_CLOSED, "", reason, now);
    g_win.reset();
    g_win_mod = nullptr;
}

const Image* image_of(uint32_t handle) {
    return handle && handle <= g_images.size() ? &g_images[handle - 1] : nullptr;
}

void draw_picture(uint32_t icon, uint32_t fps, float w, float h, uint64_t frame_no) {
    std::lock_guard lock(g_img_mutex);
    const Image* im = image_of(icon);
    if (!im || im->frames.empty()) {
        S.ui->dummy(w, h);
        return;
    }
    size_t f = 0;
    if (fps && im->frames.size() > 1) f = static_cast<size_t>(frame_no * fps / 60) % im->frames.size();
    S.ui->image(im->frames[f], w, h);
}

void KILN_CALL on_frame(const KilnUiFrame* fr, void*) {
    std::vector<Pending> now;
    std::unique_lock lock(g_win_mutex);
    if (!g_win) return;
    v26::WindowRequest& win = *g_win;
    const float dpi = fr->dpi_scale > 0 ? fr->dpi_scale : 1.0f;
    const float line = 18.0f * dpi;
    const float width = win.width * line;
    float x = (fr->width - width) * 0.5f;
    if (win.place == EMF_UI_PLACE_LEFT) x = fr->width * 0.08f;
    if (win.place == EMF_UI_PLACE_RIGHT) x = fr->width * 0.58f;
    S.ui->set_next_window_pos(x, fr->height * 0.15f, KILN_UI_COND_APPEARING, 0, 0);
    S.ui->set_next_window_size(width, 0, KILN_UI_COND_APPEARING);
    bool open = true;
    const bool right_clicks = ui_has_item_clicked();  // kiln.ui v2
    const std::string title = (win.title.empty() ? win.id : win.title) + "###emf." + win.id;
    if (S.ui->begin(kstr(title), &open, KILN_UI_WINDOW_NO_COLLAPSE)) {
        for (size_t i = 0; i < win.widgets.size(); ++i) {
            const v26::WindowWidget& wd = win.widgets[i];
            if (i && (wd.flags & EMF_UI_SAME_LINE)) S.ui->same_line(0, -1);
            S.ui->push_id(kstr(std::to_string(i)));
            const bool off = wd.flags & EMF_UI_DISABLED;
            bool clicked = false;
            const std::string label = wd.text.empty() ? " " : wd.text;
            switch (wd.type) {
                case EMF_UI_TEXT:
                    if (wd.rgba) S.ui->text_colored(wd.rgba, kstr(wd.text));
                    else if (off || (wd.flags & EMF_UI_DIM)) S.ui->text_disabled(kstr(wd.text));
                    else S.ui->text_wrapped(kstr(wd.text));
                    break;
                case EMF_UI_HEADING: S.ui->separator_text(kstr(wd.text)); break;
                case EMF_UI_BUTTON:
                    clicked = (wd.flags & EMF_UI_SELECTED) ? S.ui->selectable(kstr(label), true) : S.ui->button(kstr(label), 0, 0);
                    break;
                case EMF_UI_ROW: {
                    std::string row = label;
                    if (wd.icon) {
                        draw_picture(wd.icon, wd.fps, line, line, fr->frame);
                        S.ui->same_line(0, -1);
                    }
                    clicked = S.ui->selectable(kstr(row), (wd.flags & EMF_UI_SELECTED) != 0);
                    break;
                }
                case EMF_UI_SLOT: {
                    std::string s = (wd.flags & EMF_UI_LOCKED) ? "[locked]" : (wd.flags & EMF_UI_EMPTY) ? "" : label;
                    if (wd.count) s += " x" + std::to_string(wd.count);
                    const float side = line * 3.2f;
                    std::lock_guard il(g_img_mutex);
                    const Image* im = image_of(wd.icon);
                    if (im && !im->frames.empty()) {
                        const size_t f = wd.fps && im->frames.size() > 1 ? static_cast<size_t>(fr->frame * wd.fps / 60) % im->frames.size() : 0;
                        clicked = S.ui->image_button(kstr("slot"), im->frames[f], side, side);
                    } else {
                        clicked = S.ui->button(kstr(s.empty() ? " " : s), side, side);
                    }
                    break;
                }
                case EMF_UI_BAR:
                    S.ui->progress_bar(static_cast<float>(wd.value) / 1000.0f, -1, 0, kstr(wd.text));
                    break;
                case EMF_UI_SEPARATOR: S.ui->separator(); break;
                case EMF_UI_IMAGE: {
                    float h = (wd.height > 0 ? wd.height : 6.0f) * line, w = h;
                    {
                        std::lock_guard il(g_img_mutex);
                        if (const Image* im = image_of(wd.icon); im && im->h)
                            w = std::min(width - 2 * line, h * static_cast<float>(im->w) / static_cast<float>(im->h));
                    }
                    if (wd.key.empty() || off) {
                        draw_picture(wd.icon, wd.fps, w, h, fr->frame);
                    } else {
                        std::lock_guard il(g_img_mutex);
                        const Image* im = image_of(wd.icon);
                        if (im && !im->frames.empty()) {
                            const size_t f = wd.fps && im->frames.size() > 1 ? static_cast<size_t>(fr->frame * wd.fps / 60) % im->frames.size() : 0;
                            clicked = S.ui->image_button(kstr("pic"), im->frames[f], w, h);
                        } else {
                            S.ui->dummy(w, h);
                        }
                    }
                    break;
                }
                default: break;
            }
            // before the tooltip: it is asked of the widget drawn last
            const bool right = right_clicks && !off && !wd.key.empty() && v26::widget_takes_clicks(wd.type) &&
                               S.ui->is_item_clicked(KILN_UI_MOUSE_RIGHT);
            if (!wd.tooltip.empty() && S.ui->is_item_hovered()) S.ui->set_tooltip(kstr(wd.tooltip));
            if (clicked && !off && !wd.key.empty() && v26::widget_takes_clicks(wd.type))
                post_event_locked(win, g_win_mod, EMF_UI_CLICK, wd.key, 0, &now);
            if (right) post_event_locked(win, g_win_mod, EMF_UI_RIGHT_CLICK, wd.key, 0, &now);
            S.ui->pop_id();
        }
    }
    S.ui->end();
    if (!open) {
        close_locked(EMF_UI_BY_PLAYER, &now);
        set_open(false);
    }
    lock.unlock();
    run_now(now);
}

bool KILN_CALL on_key(const KilnUiKey* k, void*) {
    if (!g_open || !k || !k->down || k->repeat || k->text_input) return false;
    std::vector<Pending> now;
    {
        std::lock_guard lock(g_win_mutex);
        if (!g_win) return false;
        if (k->vk != 0x1B && (!g_win->close_vk || k->vk != g_win->close_vk)) return false;
        close_locked(EMF_UI_BY_PLAYER, &now);
        set_open(false);
    }
    run_now(now);
    return true;
}

bool ensure_ui() {
    std::lock_guard lock(g_ui_mutex);
    if (!S.ui || g_shutdown) return false;
    if (!g_frame && S.ui->subscribe_frame(g_owner, &on_frame, nullptr, &g_frame) != KILN_OK) g_frame = 0;
    if (!g_key && S.ui->subscribe_key(g_owner, &on_key, nullptr, &g_key) != KILN_OK) g_key = 0;
    return g_frame != 0 && g_key != 0;
}

const char* no_overlay() {
    return g_side == KILN_SIDE_SERVER ? "a dedicated server has no overlay (keep chat commands for players)"
                                      : "the in-game overlay is off (kiln.ui is not available)";
}

bool api_window_open(const EmfWindowDef* def) {
    Mod* m = caller_mod(_ReturnAddress());
    v26::WindowRequest r;
    r.mod = m ? m->id : "?";
    std::string why;
    if (!v26::copy_window_def(def, false, &r, &why)) return refuse(m, "window_open", why.c_str()), false;
    if (g_side == KILN_SIDE_SERVER || !S.ui || !ensure_ui()) return refuse(m, "window_open", no_overlay(), true), false;
    if (wheel_is_open()) return refuse(m, "window_open", "a wheel is open", true), false;
    if (!g_api->world(r.world_kind))
        return refuse(m, "window_open",
                      r.world_kind == EMF_WORLD_SERVER
                          ? "no server world runs (in someone else's game pass EMF_WORLD_CLIENT): its events would never come"
                          : "no client world runs: its events would never come"),
               false;
    std::vector<Pending> now;
    {
        std::lock_guard lock(g_win_mutex);
        std::erase_if(g_targets, [](const Pending& p) { return p.lifetime->ended.load(); });
        if (g_targets.size() >= 256) return refuse(m, "window_open", "too many windows awaiting CLOSED"), false;
        if (g_win && (g_win->id != r.id || g_win_mod != m))
            return refuse(m, "window_open", ("another window is open (" + g_win->id + ")").c_str()), false;
        if (g_win) close_locked(EMF_UI_BY_MOD, &now);
        if (g_shutdown || g_stopping[r.world_kind]) return false;
        g_win = std::move(r);
        g_win_mod = m;
        g_win_lifetime = std::make_shared<WindowLifetime>();
        g_targets.push_back({g_win->cb, g_win->user, m, g_win->id, "", EMF_UI_CLOSED, EMF_UI_BY_CORE, g_win->world_kind, g_win_lifetime});
        set_open(true);
    }
    run_now(now);
    return true;
}

bool api_window_update(const EmfWindowDef* def) {
    Mod* m = caller_mod(_ReturnAddress());
    v26::WindowRequest r;
    std::string why;
    if (!v26::copy_window_def(def, true, &r, &why)) return refuse(m, "window_update", why.c_str()), false;
    std::lock_guard lock(g_win_mutex);
    if (!g_win || g_win->id != r.id || g_win_mod != m) return false;  // not open (the player closed it): no log, never reopened
    r.cb = g_win->cb;
    r.user = g_win->user;
    r.mod = g_win->mod;
    for (auto& target : g_targets)
        if (target.lifetime == g_win_lifetime) target.world_kind = r.world_kind;
    g_win = std::move(r);
    return true;
}

bool api_window_close(const char* id) {
    if (!id || strnlen(id, v26::kMaxWindowIdLen + 1) > v26::kMaxWindowIdLen) return false;
    Mod* m = caller_mod(_ReturnAddress());
    std::vector<Pending> now;
    {
        std::lock_guard lock(g_win_mutex);
        if (!g_win || g_win->id != id || g_win_mod != m) return false;
        close_locked(EMF_UI_BY_MOD, &now);
        set_open(false);
    }
    run_now(now);
    return true;
}

// ============================================================================================= v26 ===========
std::filesystem::path mod_folder(const void* return_address) {
    HMODULE h = nullptr;
    if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           static_cast<LPCWSTR>(return_address), &h)) {
        wchar_t p[MAX_PATH] = L"";
        if (GetModuleFileNameW(h, p, MAX_PATH)) return std::filesystem::path(p).parent_path();
    }
    return P.emf / L"mods";
}

// Decodes a PNG file, or a folder of PNG frames, into `im` (empty on failure; `why` says why). `budget` is how many
// bytes of decoded pixels are left. Touches no shared state but kiln.ui, so it can run without g_img_mutex.
bool decode_image(const std::filesystem::path& file, uint64_t budget, Image* out, uint64_t* out_bytes, std::string* why) {
    auto refused = [&](const std::string& w) { *why = w; return false; };
    std::vector<std::filesystem::path> files;
    std::error_code ec;
    if (std::filesystem::is_directory(file, ec)) {
        std::filesystem::directory_iterator it(file, ec), end;
        while (!ec && it != end) {
            if (lower(to_utf8(it->path().extension().wstring())) == ".png") files.push_back(it->path());
            if (files.size() > v26::kMaxImageFrames) return refused("more than 64 frames");
            it.increment(ec);
        }
        if (ec) return refused("the folder could not be read");
        std::sort(files.begin(), files.end());
        if (files.empty()) return refused("the folder has no .png files");
        if (files.size() > v26::kMaxImageFrames) return refused("more than 64 frames");
    } else if (std::filesystem::is_regular_file(file, ec)) {
        files.push_back(file);
    } else {
        return refused("no such file or folder");
    }
    Image im;
    // Roll back every decoded frame if a later frame is refused (including allocation/I/O exceptions).
    struct Rollback {
        Image& im;
        ~Rollback() { for (auto h : im.frames) S.ui->image_free(h); }
    } rollback{im};
    im.frames.reserve(files.size());
    uint64_t bytes = 0;
    for (const auto& f : files) {
        std::string data;
        uint32_t w = 0, h = 0;
        std::ifstream input(f, std::ios::binary);
        char chunk[8192];
        while (input && data.size() <= v26::kMaxImageBytes) {
            input.read(chunk, sizeof chunk);
            data.append(chunk, static_cast<size_t>(input.gcount()));
        }
        if (data.size() > v26::kMaxImageBytes) return refused("PNG file exceeds 64 MB");
        if (!input.eof() || !v26::png_size(reinterpret_cast<const uint8_t*>(data.data()), data.size(), &w, &h))
            return refused(path_utf8(f.filename()) + " is not a PNG");
        if (w > v26::kMaxImageSide || h > v26::kMaxImageSide) return refused("larger than 512 x 512 pixels");
        if (im.w && (w != im.w || h != im.h)) return refused("its frames are not all the same size");
        im.w = w;
        im.h = h;
        bytes += uint64_t{w} * h * 4;
        if (bytes > budget) return refused("the 64 MB for pictures are used up");
        const KilnHandle hnd = S.ui->image_load(g_owner, data.data(), data.size());
        if (!hnd) return refused("it could not be decoded");
        im.frames.push_back(hnd);
    }
    out->frames = std::move(im.frames);
    out->w = im.w;
    out->h = im.h;
    im.frames.clear();
    *out_bytes = bytes;
    return true;
}

std::filesystem::path portrait_folder(const std::string& key) { return P.emf / L"cache" / L"portraits" / to_wide(key); }

// Loads the frames of every handle handed out while its portrait was being drawn. Runs on the portrait worker when the
// drawing finishes (and on a loading thread that lost the race with it). Each pending handle is claimed once.
void resolve_portraits() {
    std::vector<std::pair<uint32_t, std::string>> todo;
    uint64_t budget = 0;
    {
        std::lock_guard lock(g_img_mutex);
        if (g_shutdown) return;
        for (size_t i = 0; i < g_images.size(); ++i)
            if (!g_images[i].pending.empty() && !portraits_drawing(g_images[i].pending))
                todo.emplace_back(static_cast<uint32_t>(i + 1), std::exchange(g_images[i].pending, {}));
        budget = v26::kMaxImageBytes - std::min<uint64_t>(g_image_bytes, v26::kMaxImageBytes);
    }
    for (const auto& [handle, key] : todo) {
        Image im;
        uint64_t bytes = 0;
        std::string why;
        const bool ok = decode_image(portrait_folder(key), budget, &im, &bytes, &why);
        std::lock_guard lock(g_img_mutex);
        if (ok && !g_shutdown && handle <= g_images.size() && g_image_bytes + bytes <= v26::kMaxImageBytes) {
            g_image_bytes += bytes;
            budget -= std::min(budget, bytes);
            g_images[handle - 1].frames = std::move(im.frames);
            g_images[handle - 1].w = im.w;
            g_images[handle - 1].h = im.h;
            continue;
        }
        for (auto h : im.frames) S.ui->image_free(h);
        if (g_shutdown) return;
        if (ok) why = "the 64 MB for pictures are used up";
        // The handle stays valid and draws nothing. A later image_load of the key tries again with a new handle.
        g_image_keys.erase("portrait:" + key);
        if (g_image_refused.insert("portrait:" + key).second)
            logf(KILN_LOG_WARN, "EMF: image_load('portrait:%s') refused after drawing: %s", key.c_str(), why.c_str());
    }
}

uint32_t api_image_load(const char* path) {
    Mod* m = caller_mod(_ReturnAddress());
    if (!path || !*path) return 0;
    const size_t length = strnlen(path, 32768);
    if (length == 32768) return refuse(m, "image_load", "path is too long", true), 0;
    const std::string p(path, length);
    std::filesystem::path file;
    std::string key, drawing;
    const std::string kPortrait = "portrait:";
    if (p.rfind(kPortrait, 0) == 0) {
        const std::string k = p.substr(kPortrait.size());
        if (!v26::portrait_key_ok(k)) return refuse(m, "image_load", "a portrait key is 1-64 of A-Z a-z 0-9 _ . -", true), 0;
        // first start after a change: the picture may still be drawing on the portrait worker. Only the thread that is
        // loading the mods waits for it; a tick or render thread gets a handle at once that draws nothing until the
        // worker has finished the picture (resolve_portraits).
        if (!portraits_wait(k)) drawing = k;
        file = portrait_folder(k);
        key = p;
    } else {
        const std::filesystem::path w(to_wide(p));
        file = w.is_absolute() || (p.size() > 1 && (p[0] == '\\' || p[0] == '/')) ? w : mod_folder(_ReturnAddress()) / w;
        key = lower(path_utf8(file));
    }
    std::unique_lock lock(g_img_mutex);
    if (g_shutdown) return 0;
    if (auto it = g_image_keys.find(key); it != g_image_keys.end()) return it->second;
    auto refused = [&](const std::string& why) -> uint32_t {
        if (g_image_refused.insert(key).second) mod_logf(m, KILN_LOG_WARN, "image_load('%s') refused: %s", p.c_str(), why.c_str());
        return 0;
    };
    if (g_side == KILN_SIDE_SERVER || !S.ui) return refused(no_overlay());
    if (g_images.size() >= v26::kMaxImages) return refused("128 pictures are loaded already");
    if (!drawing.empty()) {
        Image im;
        im.pending = drawing;
        g_images.push_back(std::move(im));
        const uint32_t handle = static_cast<uint32_t>(g_images.size());
        g_image_keys[key] = handle;
        lock.unlock();
        portraits_when_drawn(&resolve_portraits);
        if (!portraits_drawing(drawing)) resolve_portraits();  // the worker finished before it saw this handle
        return handle;
    }
    Image im;
    uint64_t bytes = 0;
    std::string why;
    if (!decode_image(file, v26::kMaxImageBytes - std::min<uint64_t>(g_image_bytes, v26::kMaxImageBytes), &im, &bytes, &why))
        return refused(why);
    g_image_bytes += bytes;
    g_images.push_back(std::move(im));
    const uint32_t handle = static_cast<uint32_t>(g_images.size());
    g_image_keys[key] = handle;
    return handle;
}

}  // namespace

bool windows_want_frame() { return g_open.load(); }

void windows_after_step(EmfWorld* w, uint8_t kind) {
    std::vector<Pending> due;
    {
        std::lock_guard lock(g_win_mutex);
        for (auto it = g_events.begin(); it != g_events.end();) {
            if (it->world_kind == kind) {
                due.push_back(std::move(*it));
                it = g_events.erase(it);
            } else {
                ++it;
            }
        }
    }
    for (auto& p : due) deliver(p, w, false);
    wheel_after_step(w, kind);  // v10: wheel results
}

// Called before Kiln clears the dying world's pointer: never deliver its clicks into a later session.
void windows_world_stopping(uint8_t kind) {
    wheel_world_stopping(kind);
    std::vector<Pending> due;
    {
        std::lock_guard lock(g_win_mutex);
        g_stopping[kind] = true;
        for (auto it = g_events.begin(); it != g_events.end();) {
            if (it->world_kind != kind) { ++it; continue; }
            it = g_events.erase(it);
        }
        for (auto it = g_targets.begin(); it != g_targets.end();) {
            if (it->world_kind != kind) { ++it; continue; }
            due.push_back(std::move(*it));
            it = g_targets.erase(it);
        }
        if (g_win && g_win->world_kind == kind) {
            g_win.reset();
            g_win_mod = nullptr;
            set_open(false);
        }
    }
    run_now(due); // user code may reenter the API
}

void windows_world_started(uint8_t kind) {
    windows_world_stopping(kind); // also discard pending events when an older core missed a stop (and a wheel's)
    {
        std::lock_guard lock(g_win_mutex);
        g_stopping[kind] = false;
    }
    wheel_world_started(kind);
}

void windows_shutdown() {
    g_shutdown = true;
    wheel_shutdown();
    windows_world_stopping(EMF_WORLD_SERVER);
    windows_world_stopping(EMF_WORLD_CLIENT);
    if (S.ui) {
        std::lock_guard ui_lock(g_ui_mutex);
        if (g_frame) S.ui->unsubscribe(g_frame);
        if (g_key) S.ui->unsubscribe(g_key);
        g_frame = g_key = 0;
        std::lock_guard image_lock(g_img_mutex);
        for (const auto& im : g_images) for (auto h : im.frames) S.ui->image_free(h);
        g_images.clear();
        g_image_keys.clear();
        g_image_refused.clear();
        g_image_bytes = 0;
    }
}

void fill_windows(EmfApi* t) {
    t->window_open = &api_window_open;
    t->window_update = &api_window_update;
    t->window_close = &api_window_close;
    t->image_load = &api_image_load;
    fill_wheel(t);
}

}  // namespace kemf
