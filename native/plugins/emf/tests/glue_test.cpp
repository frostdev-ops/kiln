// SPDX-License-Identifier: MIT
// Exercise the production adapters against fake Kiln services, including
// callback reentrancy.
#include "emf_internal.h"
#include "services/kiln_caps.h"
#include "services/kiln_render.h"
#include "v26_model.h"
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <map>
#include <set>
#include <stdexcept>
#include <thread>

static int failures = 0, checks = 0;
#define CHECK(x)                                                                                                       \
    do {                                                                                                               \
        ++checks;                                                                                                      \
        if (!(x)) {                                                                                                    \
            ++failures;                                                                                                \
            std::printf("FAIL %d: %s\n", __LINE__, #x);                                                                \
        }                                                                                                              \
    } while (0)
namespace kemf {
const KilnApi *g_api;
KilnOwner g_owner = 1;
uint32_t g_side = KILN_SIDE_CLIENT;
Services S;
Paths P;
thread_local Context t_ctx;
static Mod mod, other_mod;
static Mod *active_mod = &mod;
Mod *caller_mod(const void *) { return active_mod; }
Mod *mod_of_address(const void *) { return active_mod; }
void logf(int, const char *, ...) {}
std::vector<std::string> mod_lines;  // every mod_logf line, for the window log checks
void mod_logf(const Mod *, int, const char *fmt, ...) {
    char buf[4096];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    mod_lines.emplace_back(buf);
    if (std::getenv("KEMF_TEST_VERBOSE"))
        std::printf("  log %s\n", buf);
}
// A fake portrait worker: `drawing_key` is being drawn until the test finishes it and runs the registered callback.
std::string drawing_key;
void (*when_drawn)() = nullptr;
bool portraits_wait(const std::string &k) { return k != drawing_key; }
bool portraits_drawing(const std::string &k) { return k == drawing_key; }
void portraits_when_drawn(void (*fn)()) { when_drawn = fn; }
int unsupported_calls = 0, refusals = 0;
std::set<std::string> unsupported_fns;  // every function that reported itself not supported
static void say(const char *what, const char *fn, const char *why) {
    if (std::getenv("KEMF_TEST_VERBOSE"))
        std::printf("  %s %s: %s\n", what, fn ? fn : "", why ? why : "");
}
void unsupported(const Mod *, const char *fn, const char *why) {
    ++unsupported_calls;
    if (fn) unsupported_fns.insert(fn);
    say("unsupported", fn, why);
}
void refuse(const Mod *, const char *fn, const char *why, bool) {
    ++refusals;
    say("refused", fn, why);
}
std::vector<std::function<void()>> posted;  // tasks for the next tick (post_tick)
bool post_tick(std::function<void()> f) {
    posted.push_back(std::move(f));
    return true;
}
void account(Mod *, uint64_t) {}
uint64_t qpc_ns() { return 0; }
bool guarded(Mod *, const char *, ThunkFn f, void *p) {
    f(p);
    return true;
}
bool seh_copy(void *d, const void *s, size_t n) {
    if (!s || !d)
        return false;
    std::memcpy(d, s, n);
    return true;
}
bool seh_read_u32(uintptr_t p, uint32_t *v) { return seh_copy(v, reinterpret_cast<void *>(p), 4); }
bool seh_read_u64(uintptr_t p, uint64_t *v) { return seh_copy(v, reinterpret_cast<void *>(p), 8); }
bool seh_read_u16(uintptr_t p, uint16_t *v) { return seh_copy(v, reinterpret_cast<void *>(p), 2); }
bool seh_call(ThunkFn f, void *p, unsigned long *, uintptr_t *) {
    f(p);
    return true;
}
std::wstring to_wide(std::string_view s) { return {s.begin(), s.end()}; }
std::string to_utf8(const std::wstring &s) { return {s.begin(), s.end()}; }
std::string lower(std::string_view v) {
    std::string s(v);
    for (auto &c : s)
        c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return s;
}
std::string path_utf8(const std::filesystem::path &p) { return p.string(); }
bool read_file(const std::filesystem::path &p, std::string *out) {
    std::ifstream f(p, std::ios::binary);
    out->assign(std::istreambuf_iterator<char>(f), {});
    return bool(f);
}
void ui_refresh_demand() {}
void keep_adapter_mapped() {}
uint32_t spawn_for(Mod *, EmfWorld *, const uint8_t *, double, double, double, const char *) { return 5000; }
uint32_t fnv1a(std::string_view s) {
    uint32_t h = 2166136261u;
    for (auto c : s)
        h = (h ^ static_cast<uint8_t>(c)) * 16777619u;
    return h;
}
uint32_t type_size(uint32_t hash) { return hash==fnv1a("keen::ecs::EventB")?12:24; }
std::string format(const char *, ...) { return {}; }
} // namespace kemf
using namespace kemf;
static uint32_t session_id = 1;
static uintptr_t worlds[3] = {0x1000, 0, 0x2000};
static bool tick_thread = true;
uint32_t kemf::server_session() { return session_id; }
static EmfApi table{};
static KilnUiKeyFn key_fn;
static KilnUiFrameFn frame_fn;
static uint32_t captured = 0;
static int closed = 0, clicks = 0, right_clicks = 0;
static EmfUiEvent last{};
static std::string last_key, last_window;  // the event's strings are valid during the callback only
static bool reenter = false, stop_on_click = false, close_frame = true;
static void ui_cb(EmfWorld *, const EmfUiEvent *e, void *) {
    if (e->kind == EMF_UI_CLOSED) {
        ++closed;
        last = *e;
    } else if (e->kind == EMF_UI_RIGHT_CLICK) {
        ++right_clicks;
        last = *e;
        last_key = e->key;
        last_window = e->window;
    } else {
        ++clicks;
        if (stop_on_click) {
            stop_on_click = false;
            windows_world_stopping(EMF_WORLD_SERVER);
        }
    }
    if (reenter) {
        reenter = false;
        table.window_close("test.win");
    }
}
static EmfWindowDef window_def() {
    EmfWindowDef d{};
    d.size = sizeof d;
    d.id = "test.win";
    d.cb = ui_cb;
    d.widget_size = sizeof(EmfUiWidget);
    return d;
}
static bool button_clicked = true, right_click_now = false;
static bool wheel_open_now = false;
static int wheel_open_calls = 0;
static KilnResult wheel_rc = KILN_OK;
static KilnUiWheelFn wheel_fn;
static void *wheel_user;
static KilnHandle wheel_closed = 0;
static std::vector<std::string> wheel_items;
static std::string wheel_title;
static uint32_t wheel_hold = 0, wheel_count = 0;
static int size_calls = 0, constraint_calls = 0;
static float min_w, min_h, max_w, max_h, size_w, size_h;
static uint32_t window_flags, size_cond, pos_cond;
static std::vector<std::string> window_calls;
static void init_ui(KilnUiApi &ui) {
    ui.header.size = sizeof ui;
    ui.header.version = KILN_UI_VERSION;
    ui.subscribe_frame = [](KilnOwner, KilnUiFrameFn f, void *, KilnHandle *h) -> KilnResult {
        frame_fn = f;
        *h = 1;
        return KILN_OK;
    };
    ui.subscribe_key = [](KilnOwner, KilnUiKeyFn f, void *, KilnHandle *h) -> KilnResult {
        key_fn = f;
        *h = 2;
        return KILN_OK;
    };
    ui.set_next_window_pos = [](float, float, uint32_t c, float, float) { pos_cond = c; window_calls.push_back("pos"); };
    ui.set_next_window_size = [](float w, float h, uint32_t c) { ++size_calls; size_w = w; size_h = h; size_cond = c; window_calls.push_back("size"); };
    ui.set_next_window_size_constraints = [](float a, float b, float c, float d) {
        ++constraint_calls; min_w = a; min_h = b; max_w = c; max_h = d; window_calls.push_back("constraints");
    };
    ui.begin = [](KilnStr, bool *open, uint32_t flags) {
        window_flags = flags; window_calls.push_back("begin");
        *open = !close_frame;
        return !close_frame;
    };
    ui.push_id = [](KilnStr) {};
    ui.pop_id = []() {};
    ui.button = [](KilnStr, float, float) { return button_clicked; };
    ui.text_wrapped = [](KilnStr) {};
    ui.is_item_clicked = [](uint32_t b) { return b == KILN_UI_MOUSE_RIGHT && right_click_now; };
    ui.wheel_open = [](KilnOwner, const KilnUiWheel *d, KilnUiWheelFn fn, void *user, KilnHandle *out) -> KilnResult {
        ++wheel_open_calls;
        if (wheel_rc != KILN_OK)
            return wheel_rc;
        if (wheel_open_now)
            return KILN_ERR_UNAVAILABLE;
        wheel_items.clear();
        for (uint32_t i = 0; i < d->count; ++i)
            wheel_items.emplace_back(d->items[i].ptr, d->items[i].len);
        wheel_title.assign(d->title.ptr, d->title.len);
        wheel_hold = d->hold_key;
        wheel_count = d->count;
        wheel_fn = fn;
        wheel_user = user;
        wheel_open_now = true;
        *out = 99;
        return KILN_OK;
    };
    ui.wheel_close = [](KilnHandle h) -> KilnResult {
        wheel_closed = h;
        if (!wheel_open_now)
            return KILN_ERR_NOT_FOUND;
        wheel_open_now = false;
        return KILN_OK;
    };
    ui.wheel_active = []() { return wheel_open_now; };
    ui.end = []() {};
    ui.unsubscribe = [](KilnHandle) -> KilnResult { return KILN_OK; };
    ui.set_capture = [](uint32_t c) { captured = c; };
}
static void test_window_sizing(KilnUiApi &ui) {
    auto d = window_def();
    d.width = 20;
    CHECK(table.window_open(&d));
    KilnUiFrame fr{};
    fr.width = 1920; fr.height = 1080; fr.dpi_scale = 1.5f;
    const bool saved_close = close_frame;
    const int saved_closed = closed;
    close_frame = false;
    for (int i = 0; i < 2; ++i) {
        // The second frame represents content arriving after opening.
        if (i) {
            EmfUiWidget row{}; row.type = EMF_UI_TEXT; row.text = "creature";
            d.widgets = &row; d.count = 1;
            CHECK(table.window_update(&d));
        }
        window_calls.clear(); size_calls = constraint_calls = 0;
        frame_fn(&fr, nullptr);
        CHECK(constraint_calls == 1 && size_calls == 0);
        CHECK(min_w == 540 && max_w == 540 && min_h == 0 && max_h == 918);
        CHECK(window_flags == (KILN_UI_WINDOW_NO_COLLAPSE | KILN_UI_WINDOW_ALWAYS_AUTO_RESIZE));
        CHECK(pos_cond == KILN_UI_COND_APPEARING);
        CHECK((window_calls == std::vector<std::string>{"pos", "constraints", "begin"}));
    }
    d.widgets = nullptr; d.count = 0;
    CHECK(table.window_update(&d));
    const auto full = ui.header.size;
    // a table that ends before the constraints slot, and one that ends a byte short of covering it (kiln.ui has grown
    // past that slot since, so sizeof(KilnUiApi) - 1 no longer is that case)
    for (auto short_size : {offsetof(KilnUiApi, set_next_window_size_constraints),
                            offsetof(KilnUiApi, set_next_window_size_constraints) + sizeof(ui.set_next_window_size_constraints) - 1}) {
        ui.header.size = short_size;
        window_calls.clear(); size_calls = constraint_calls = 0;
        frame_fn(&fr, nullptr);
        CHECK(constraint_calls == 0 && size_calls == 1);
        CHECK(size_w == 540 && size_h == 0 && size_cond == KILN_UI_COND_APPEARING);
        CHECK(window_flags == KILN_UI_WINDOW_NO_COLLAPSE);
        CHECK((window_calls == std::vector<std::string>{"pos", "size", "begin"}));
    }
    ui.header.size = full;
    auto constraints = ui.set_next_window_size_constraints;
    ui.set_next_window_size_constraints = nullptr;
    size_calls = constraint_calls = 0;
    frame_fn(&fr, nullptr);
    CHECK(size_calls == 1 && constraint_calls == 0 && window_flags == KILN_UI_WINDOW_NO_COLLAPSE);
    ui.set_next_window_size_constraints = constraints;
    CHECK(table.window_close(d.id));
    windows_after_step(reinterpret_cast<EmfWorld *>(worlds[0]), EMF_WORLD_SERVER);
    closed = saved_closed; close_frame = saved_close;
}
// kiln.log lines about mod windows (support reports): the open, real changes only (bounded), each click delivered and
// the close.
static std::string row_hit;  // the row label the fake selectable reports clicked
static size_t lines_with(const std::string &needle, size_t from = 0) {
    size_t n = 0;
    for (size_t i = from; i < mod_lines.size(); ++i)
        n += mod_lines[i].find(needle) != std::string::npos;
    return n;
}
static void test_window_log(KilnUiApi &ui) {
    ui.selectable = [](KilnStr s, bool) { return !row_hit.empty() && std::string(s.ptr, s.len) == row_hit; };
    ui.separator_text = [](KilnStr) {};
    auto *server = reinterpret_cast<EmfWorld *>(worlds[0]);
    const bool saved_close = close_frame, saved_button = button_clicked;
    close_frame = false;
    button_clicked = false;
    const int saved_clicks = clicks, saved_closed = closed;
    const size_t start = mod_lines.size();
    EmfUiWidget w[3]{};
    w[0].type = EMF_UI_HEADING;
    w[0].text = "Survivors";
    w[1].type = EMF_UI_ROW;
    w[1].key = "pick.7";
    w[1].text = "Hunter (4 m)";
    w[2].type = EMF_UI_BUTTON;
    w[2].key = "send";
    w[2].text = "Send";
    auto d = window_def();
    d.title = "Expeditions";
    d.widgets = w;
    d.count = 3;
    CHECK(table.window_open(&d));
    CHECK(mod_lines.size() == start + 1);
    CHECK(lines_with("window test.win opened: \"Expeditions\", 3 widgets, events on the server world; clickable: "
                     "pick.7 \"Hunter (4 m)\", send \"Send\"; text: \"Survivors\"", start) == 1);
    // the same content again (a mod refreshing every tick): no line
    for (int i = 0; i < 50; ++i)
        CHECK(table.window_update(&d));
    KilnUiFrame fr{};
    fr.dpi_scale = 1;
    for (int i = 0; i < 5; ++i)
        frame_fn(&fr, nullptr);
    CHECK(mod_lines.size() == start + 1);
    // a real change: one line
    w[1].text = "Hunter (2 m)";
    CHECK(table.window_update(&d));
    CHECK(table.window_update(&d));
    CHECK(mod_lines.size() == start + 2 && lines_with("window test.win changed: ", start) == 1 &&
          lines_with("pick.7 \"Hunter (2 m)\"", start) == 1);
    // a click on the row and on the button: one line each when the mod gets it (after the world's step)
    row_hit = "Hunter (2 m)";
    frame_fn(&fr, nullptr);
    row_hit.clear();
    button_clicked = true;
    frame_fn(&fr, nullptr);
    button_clicked = false;
    CHECK(lines_with(": click on", start) == 0);  // not delivered yet
    windows_after_step(server, EMF_WORLD_SERVER);
    CHECK(clicks == saved_clicks + 2);
    CHECK(lines_with("window test.win: click on pick.7 \"Hunter (2 m)\" delivered", start) == 1);
    CHECK(lines_with("window test.win: click on send \"Send\" delivered", start) == 1);
    // a mod changing its window every tick: bounded, then one line saying so
    const size_t before = mod_lines.size();
    for (int i = 0; i < 200; ++i) {
        const std::string t = "Hunter (" + std::to_string(i) + " m)";
        w[1].text = t.c_str();
        CHECK(table.window_update(&d));
    }
    const size_t spent = mod_lines.size() - before;
    CHECK(spent >= 10 && spent <= 40);
    CHECK(lines_with("further changes of this window are not logged", before) == 1);
    // closed by the player (its X): one line
    close_frame = true;
    frame_fn(&fr, nullptr);
    windows_after_step(server, EMF_WORLD_SERVER);
    CHECK(closed == saved_closed + 1);
    CHECK(lines_with("window test.win closed by the player", start) == 1);
    // reopened: the change budget starts again
    close_frame = false;
    w[1].text = "Hunter (1 m)";
    CHECK(table.window_open(&d));
    CHECK(lines_with("window test.win opened: ", start) == 2);
    w[1].text = "Hunter (0 m)";
    CHECK(table.window_update(&d));
    CHECK(mod_lines.back().find("window test.win changed: ") == 0);
    CHECK(table.window_close(d.id));
    windows_after_step(server, EMF_WORLD_SERVER);
    CHECK(lines_with("window test.win closed by the mod", start) == 1);
    // a mod opening and closing every tick: bounded per mod, then one line saying so
    const size_t cycle = mod_lines.size();
    for (int i = 0; i < 2000; ++i) {
        CHECK(table.window_open(&d));
        CHECK(table.window_close(d.id));
        windows_after_step(server, EMF_WORLD_SERVER);
    }
    CHECK(mod_lines.size() - cycle <= 400);
    CHECK(lines_with("further window lines of this mod are not logged", cycle) == 1);
    windows_world_started(EMF_WORLD_SERVER);  // the next session logs again
    CHECK(table.window_open(&d));
    CHECK(mod_lines.back().find("window test.win opened: ") == 0);
    CHECK(table.window_close(d.id));
    windows_after_step(server, EMF_WORLD_SERVER);
    clicks = saved_clicks;
    closed = saved_closed;
    close_frame = saved_close;
    button_clicked = saved_button;
    ui.selectable = nullptr;
    ui.separator_text = nullptr;
}
static std::vector<KilnProgressionRequest> submitted;
static bool curve_available = true;
static KilnResult submit_result = KILN_OK;
static uint32_t needed = 100;
static void test_level(KilnProgressionApi &prog) {
    prog.xp_for_level = [](uint32_t, uint32_t *n, uint32_t *c) -> KilnResult {
        *n = needed;
        *c = 45;
        return curve_available ? KILN_OK : KILN_ERR_UNAVAILABLE;
    };
    prog.submit = [](KilnOwner, const KilnProgressionRequest *q, uint64_t *) {
        if (submit_result == KILN_OK)
            submitted.push_back(*q);
        return submit_result;
    };
    CHECK(table.player_xp_required(reinterpret_cast<EmfWorld *>(worlds[0]), 1) == 100);
    ++session_id;
    curve_available = false;
    CHECK(table.player_level_cap(reinterpret_cast<EmfWorld *>(worlds[0])) == 0);
    curve_available = true;
    CHECK(table.player_level_set(reinterpret_cast<EmfWorld *>(worlds[0]), 2, 5, 99));
    KilnProgressionResult r{};
    r.status = KILN_OK;
    r.entity = 2;
    r.after.experience_needed = 100;
    r.after.level = 5;
    ++session_id;
    const auto first = submitted.front();
    first.callback(&r, first.user);
    CHECK(submitted.size() == 1); // a successful old result must not write XP
                                  // into a new world's entity 2
    submitted.clear();
    CHECK(table.player_level_set(reinterpret_cast<EmfWorld *>(worlds[0]), 2, 5, 99));
    r.after.level = 6;
    const auto second = submitted.front();
    second.callback(&r, second.user);
    CHECK(submitted.size() == 1);
    submitted.clear();
    CHECK(table.player_level_set(reinterpret_cast<EmfWorld *>(worlds[0]), 2, 5, 99));
    CHECK(table.player_level_set(reinterpret_cast<EmfWorld *>(worlds[0]), 2, 5, 0));
    r.after.level = 5;
    auto old = submitted[0], fresh = submitted[1];
    old.callback(&r, old.user);
    fresh.callback(&r, fresh.user);
    CHECK(submitted.size() == 2);
    submitted.clear();
    CHECK(table.player_level_set(reinterpret_cast<EmfWorld *>(worlds[0]), 2, 5, 500));
    auto valid = submitted.front();
    valid.callback(&r, valid.user);
    CHECK(submitted.size() == 2 && submitted.back().op == KILN_PROG_OP_SET_EXPERIENCE && submitted.back().value == 99);
    tick_thread = false;
    int32_t out = 123;
    CHECK(!level_attribute_get(&mod, reinterpret_cast<EmfWorld *>(worlds[0]), 2, EMF_ATTR_LEVEL, &out) && out == 123);
    tick_thread = true; // an intervening level write must not receive the old level's XP
}
static int live_images = 0, decoded = 0, free_images = 0;
static void test_images(KilnUiApi &ui) {
    ui.image_load = [](KilnOwner, const void *, size_t) -> KilnHandle {
        ++decoded;
        if (decoded == 2)
            return 0;
        ++live_images;
        return 10 + decoded;
    };
    ui.image_free = [](KilnHandle) {
        --live_images;
        ++free_images;
    };
    const auto dir = P.emf / "frames";
    std::filesystem::create_directories(dir);
    const unsigned char png[24] = {0x89, 'P', 'N', 'G', 13, 10, 26, 10, 0, 0, 0, 13,
                                   'I',  'H', 'D', 'R', 0,  0,  0,  1,  0, 0, 0, 1};
    for (auto name : {"a.png", "b.png"}) {
        std::ofstream f(dir / name, std::ios::binary);
        f.write(reinterpret_cast<const char *>(png), sizeof png);
    }
    const auto path = dir.string();
    CHECK(table.image_load(path.c_str()) == 0);
    CHECK(live_images == 0 && free_images == 1);
    CHECK(table.image_load(path.c_str()) != 0);
    CHECK(live_images == 2);
    const auto big = P.emf / "large.png";
    {
        std::ofstream f(big, std::ios::binary);
        f.write(reinterpret_cast<const char *>(png), sizeof png);
        f.seekp(kemf::v26::kMaxImageBytes);
        f.put(0);
    }
    const int before = decoded;
    CHECK(table.image_load(big.string().c_str()) == 0 && decoded == before && live_images == 2);
    const std::string too_long(32768, 'a');
    CHECK(table.image_load(too_long.c_str()) == 0 && table.image_load(nullptr) == 0);
}
// image_load("portrait:<key>") from a tick/render thread while the worker draws: a handle at once, frames when drawn.
static void test_pending_portrait() {
    const auto dir = P.emf / "cache" / "portraits" / "wolf";
    std::filesystem::create_directories(dir);
    const unsigned char png[24] = {0x89, 'P', 'N', 'G', 13, 10, 26, 10, 0, 0, 0, 13,
                                   'I',  'H', 'D', 'R', 0,  0,  0,  1,  0, 0, 0, 1};
    for (auto name : {"00.png", "01.png"}) {
        std::ofstream f(dir / name, std::ios::binary);
        f.write(reinterpret_cast<const char *>(png), sizeof png);
    }
    drawing_key = "wolf";
    const int before = decoded, live = live_images;
    const uint32_t handle = table.image_load("portrait:wolf");
    CHECK(handle != 0 && decoded == before && when_drawn); // a handle, nothing decoded yet
    CHECK(table.image_load("portrait:wolf") == handle);   // asked again: the same handle
    drawing_key.clear();
    when_drawn(); // the worker finished drawing
    CHECK(decoded == before + 2 && live_images == live + 2);
    CHECK(table.image_load("portrait:wolf") == handle);
    when_drawn(); // claimed once: a second run decodes nothing
    CHECK(decoded == before + 2);
    // A picture that failed to draw: the handle draws nothing and a later call tries again.
    drawing_key = "boar";
    const uint32_t boar = table.image_load("portrait:boar");
    CHECK(boar != 0 && boar != handle);
    drawing_key.clear();
    when_drawn();
    CHECK(decoded == before + 2);
    CHECK(table.image_load("portrait:boar") == 0); // no folder: refused now, not a stale handle
}
static std::map<std::string, KilnProgramDesc> programs;
static std::map<std::pair<uint32_t, uint32_t>, std::vector<uint8_t>> components;
static int accepts = 0;
static int program_first = 0, program_second = 0;
static bool passed_other_write = false;
static uint8_t written_component[24]{};
static void test_program(void* ctx,void*) {
    CHECK(table.read_events(ctx,"EventA",24,[](const void* data,void*) { CHECK(*static_cast<const uint32_t*>(data)==11); ++program_first; },nullptr)==1);
    CHECK(table.read_events(ctx,"EventB",12,[](const void* data,void*) { CHECK(*static_cast<const uint32_t*>(data)==22); ++program_second; },nullptr)==1);
    auto* data=static_cast<uint8_t*>(table.program_write_other(ctx,"Health",5000,24));
    CHECK(data==written_component);
    if (data) *data=91;
    CHECK(table.program_write_other(ctx,"undeclared",5000,24)==nullptr);
    CHECK(table.program_write_other(ctx,"Health",5000,25)==nullptr);
}
// ---- the client world (0.2.0-dev.29): register_program(EMF_WORLD_CLIENT), _def and on_event through kiln.world's
// register_program_world, one relay per event type per world kind, delivery after that world's step ----
static std::map<std::string, uint32_t> program_worlds;  // the kind each program was registered for
static std::vector<KilnHandle> unbound, unregistered;
static bool client_row_available = true;
static int client_events = 0, client_program_runs = 0;
static std::thread::id client_event_thread;
static void client_program(void*, void*) { ++client_program_runs; }
static void client_event_cb(EmfWorld *, const void *, uint32_t, void *) {
    ++client_events;
    client_event_thread = std::this_thread::get_id();
}
static const KilnCapsApi *caps_table() {
    static KilnCapsApi caps{};
    caps.header.size = sizeof caps;
    caps.find = [](KilnStr name, KilnCapRow *row) -> KilnResult {
        if (std::string_view(name.ptr, name.len) != "world.programs.client") return KILN_ERR_NOT_FOUND;
        row->state = client_row_available ? KILN_CAP_AVAILABLE : KILN_CAP_UNAVAILABLE;
        return KILN_OK;
    };
    return &caps;
}
static void accepted_cb(EmfWorld *, uint32_t player, uint32_t entity, void *) {
    CHECK(player == 2 && entity == 5000);
    ++accepts;
}
static void test_components(KilnWorldApi &world) {
    world.header.size=sizeof world;
    world.write_other=[](KilnProgramCtx*,uint64_t accessor,uint32_t entity,uint32_t* size)->void* { CHECK(accessor==0xfeed && entity==5000); *size=24; return written_component; };
    world.component_hash = [](KilnStr s) { return fnv1a(std::string_view(s.ptr, s.len)); };
    world.component = [](uint32_t e, uint32_t h, uint32_t *size) -> const void * {
        auto it = components.find({e, h});
        if (it == components.end())
            return nullptr;
        *size = static_cast<uint32_t>(it->second.size());
        return it->second.data();
    };
    world.exists = [](uint32_t e) { return e == 5000; };
    world.position = [](uint32_t e, KilnVec3 *) -> KilnResult { return e == 5000 ? KILN_OK : KILN_ERR_NOT_FOUND; };
    world.register_program = [](KilnOwner, const KilnProgramDesc *d, KilnHandle *h) -> KilnResult {
        if (std::string(d->name.ptr,d->name.len)=="two_events")
            passed_other_write=d->other_write.count==1 && std::string(d->other_write.items[0].ptr,d->other_write.items[0].len)=="Health";
        programs[std::string(d->name.ptr, d->name.len)] = *d;
        program_worlds[std::string(d->name.ptr, d->name.len)] = KILN_WORLD_SERVER;
        *h = programs.size();
        return KILN_OK;
    };
    world.register_program_world = [](KilnOwner, uint32_t kind, const KilnProgramDesc *d, KilnHandle *h) -> KilnResult {
        if (kind != KILN_WORLD_CLIENT || (d->flags & KILN_PROGRAM_HOST_ONLY)) return KILN_ERR_INVALID_ARGUMENT;  // as kiln.world
        programs[std::string(d->name.ptr, d->name.len)] = *d;
        program_worlds[std::string(d->name.ptr, d->name.len)] = kind;
        *h = 1000 + programs.size();
        return KILN_OK;
    };
    world.unbind_program_world = [](KilnHandle h) -> KilnResult { unbound.push_back(h); return KILN_OK; };
    world.unregister_program = [](KilnHandle h) -> KilnResult { unregistered.push_back(h); return KILN_OK; };
    world.program_slot = [](KilnHandle,int32_t kind,KilnStr name)->int32_t {
        const std::string key(name.ptr,name.len);
        if (kind==KILN_SLOT_OTHER_WRITE) return key=="Health"?2:-1;
        return key=="EventB"?1:0;
    };
    world.queue_add = [](KilnProgramCtx *, uint32_t e, uint32_t h, const void *data, uint32_t n) -> KilnResult {
        auto p = static_cast<const uint8_t *>(data);
        components[{e, h}] = {p, p + n};
        return KILN_OK;
    };
    world.queue_remove = [](KilnProgramCtx *, uint32_t e, uint32_t h) -> KilnResult {
        components.erase({e, h});
        return KILN_OK;
    };
    const char* inputs[]={"EventB","EventA",nullptr};
    const char* writes[]={"Health",nullptr};
    EmfProgramDef definition{}; definition.size=sizeof definition; definition.name="two_events";
    definition.world_kind=EMF_WORLD_SERVER; definition.fn=&test_program; definition.events_in=inputs; definition.other_write=writes;
    CHECK(table.register_program_def(&definition));
    // the client world: a dedicated server refuses at the call as before (false, an "unsupported" note); so does a
    // client whose world.programs.client is off; a client with it registers through register_program_world
    const auto api_service = const_cast<KilnApi *>(g_api)->service;
    const_cast<KilnApi *>(g_api)->service = [](KilnStr n, uint32_t) -> const void * {
        return std::string_view(n.ptr, n.len) == KILN_CAPS_SERVICE ? caps_table() : nullptr;
    };
    const int unsupported_before = unsupported_calls;
    g_side = KILN_SIDE_SERVER;
    CHECK(!table.register_program("cprog", EMF_WORLD_CLIENT, &client_program, nullptr));
    CHECK(!table.on_event(EMF_WORLD_CLIENT, "ClientEvent", 24, &client_event_cb, nullptr));
    g_side = KILN_SIDE_CLIENT;
    client_row_available = false;
    CHECK(!table.register_program("cprog", EMF_WORLD_CLIENT, &client_program, nullptr));
    client_row_available = true;
    CHECK(!table.register_program("pprog", 1 /* the prediction world */, &client_program, nullptr));
    CHECK(unsupported_calls == unsupported_before + 4);
    CHECK(table.register_program("cprog", EMF_WORLD_CLIENT, &client_program, nullptr));
    EmfProgramDef cdef{}; cdef.size=sizeof cdef; cdef.name="cprog_def"; cdef.world_kind=EMF_WORLD_CLIENT; cdef.fn=&client_program;
    const char* cin[]={"ClientEvent",nullptr}; cdef.events_in=cin;
    CHECK(table.register_program_def(&cdef));
    CHECK(table.on_event(EMF_WORLD_CLIENT, "ClientEvent", 24, &client_event_cb, nullptr));
    CHECK(table.on_event(EMF_WORLD_SERVER, "ClientEvent", 24, &client_event_cb, nullptr));  // the same name in the server world
    components_on_ready();
    events_on_ready();
    CHECK(passed_other_write);
    CHECK(program_worlds.at("two_events") == KILN_WORLD_SERVER && program_worlds.at("cprog") == KILN_WORLD_CLIENT &&
          program_worlds.at("cprog_def") == KILN_WORLD_CLIENT);
    CHECK(programs.at("cprog").flags == KILN_PROGRAM_SYSTEM && programs.at("two_events").flags == (KILN_PROGRAM_SYSTEM | KILN_PROGRAM_HOST_ONLY));
    CHECK(programs.at("cprog_def").events_in.count == 1);
    // one relay per event type per world kind
    CHECK(program_worlds.at("emf_evc_ClientEvent") == KILN_WORLD_CLIENT && program_worlds.at("emf_ev_ClientEvent") == KILN_WORLD_SERVER);
    CHECK(programs.at("emf_evc_ClientEvent").flags == KILN_PROGRAM_SYSTEM);
    programs.at("cprog").each(nullptr, nullptr, 0, programs.at("cprog").user);
    CHECK(client_program_runs == 1);
    const_cast<KilnApi *>(g_api)->service = api_service;
    struct ProgramQueue { uint16_t first=1; uint8_t pad[6]{}; uintptr_t blocks=0; uint32_t count=1; } qa,qb;
    alignas(8) uint8_t page_a[256]{},page_b[256]{};
    uint32_t one=11,two=22; std::memcpy(page_a+8,&one,4); std::memcpy(page_b+8,&two,4);
    qa.blocks=reinterpret_cast<uintptr_t>(page_a); qb.blocks=reinterpret_cast<uintptr_t>(page_b);
    uint64_t program_iter[]={reinterpret_cast<uintptr_t>(&qa),reinterpret_cast<uintptr_t>(&qb),0xfeed};
    auto& program=programs.at("two_events");
    program.each(reinterpret_cast<KilnProgramCtx*>(0x1234),program_iter,0,program.user);
    CHECK(program_first==1 && program_second==1 && written_component[0]==91);
    CHECK(table.program_write_other(reinterpret_cast<void*>(0x1234),"Health",5000,24)==nullptr);
    CHECK(table.read_events(reinterpret_cast<void*>(0x1234),"EventA",24,[](const void*,void*){},nullptr)==0);
    auto w = reinterpret_cast<EmfWorld *>(worlds[0]);
    EmfOffer o{};
    o.size = sizeof o;
    CHECK(!table.offer(w, 5000, &o, accepted_cb,
                       nullptr)); // tick thread alone is not a world callback
    ContextScope scope(&mod, w, true);
    CHECK(table.offer(w, 5000, &o, accepted_cb, nullptr));
    auto step = [&] {
        auto &p = programs.at("emf_components");
        p.each(nullptr, nullptr, 0, p.user);
        components_after_step(w, EMF_WORLD_SERVER);
    };
    step();
    step();
    step();
    CHECK(table.offer_active(w, 5000) && !table.offer_active(nullptr, 5000));
    // An actual queue consumed by api_events' production event_each, then
    // delivered by events_after_step.
    alignas(8) uint8_t block[256]{};
    uint32_t guest = 2, host = 5000;
    std::memcpy(block + 16, &guest, 4);
    std::memcpy(block + 20, &host, 4);
    struct Queue {
        uint16_t first = 1;
        uint8_t pad[6]{};
        uintptr_t blocks = 0;
        uint32_t count = 1;
    } queue;
    queue.blocks = reinterpret_cast<uintptr_t>(block);
    uint64_t iter = reinterpret_cast<uintptr_t>(&queue);
    auto &event = programs.at("emf_ev_InteractionAcceptedEvent");
    event.each(nullptr, &iter, 0, event.user);
    events_after_step(w, EMF_WORLD_SERVER);
    CHECK(accepts == 1);
    event.each(nullptr, &iter, 0, event.user);
    events_world_stopping();
    events_after_step(w, EMF_WORLD_SERVER);
    CHECK(accepts == 1); // copied old-world accepts are discarded
    {
        // The client world's relay: copies made in its step go to its registrations after its step, on that thread, and
        // never to the server world's; the server world's never to the client's.
        alignas(8) uint8_t cblock[256]{};
        Queue cq;
        cq.blocks = reinterpret_cast<uintptr_t>(cblock);
        uint64_t citer = reinterpret_cast<uintptr_t>(&cq);
        auto &relay = programs.at("emf_evc_ClientEvent");
        relay.each(nullptr, &citer, 0, relay.user);
        events_after_step(w, EMF_WORLD_SERVER);
        CHECK(client_events == 0);
        auto cw = reinterpret_cast<EmfWorld *>(worlds[2]);
        std::thread([&] { events_after_step(cw, EMF_WORLD_CLIENT); }).join();
        CHECK(client_events == 1 && client_event_thread != std::this_thread::get_id());  // on the thread that ran the step
        relay.each(nullptr, &citer, 0, relay.user);
        events_world_stopping(EMF_WORLD_CLIENT);  // the client world stops: its copies are never delivered to the next
        events_after_step(cw, EMF_WORLD_CLIENT);
        CHECK(client_events == 1);
        auto &srelay = programs.at("emf_ev_ClientEvent");
        srelay.each(nullptr, &citer, 0, srelay.user);
        events_after_step(cw, EMF_WORLD_CLIENT);
        CHECK(client_events == 1);
        events_after_step(w, EMF_WORLD_SERVER);
        CHECK(client_events == 2);
    }
    CHECK(table.offer_remove(w, 5000));
    step();
    step();
    step();
    CHECK(!table.offer_active(w, 5000) && components.empty());
    CHECK(table.offer(w, 5000, &o, accepted_cb, nullptr));
    components_world_stopping();
    CHECK(!table.offer_active(w, 5000));
    CHECK(!table.player_level_set(w,2,5,0));
    components_world_started();
    // shutdown: a client-world program's handle goes to unbind_program_world, a server one's to unregister_program
    events_shutdown();
    CHECK(unbound.size() == 3 && unregistered.size() >= 3);
    for (KilnHandle h : unbound) CHECK(h > 1000);
    for (KilnHandle h : unregistered) CHECK(h < 1000);
}

// ---- v18 attributes on kiln.combat v2 ----
static uint32_t combat_id = 0;
static int32_t combat_delta = 0;
static int combat_get_calls = 0, combat_add_calls = 0;
static KilnResult combat_get_rc = KILN_OK, combat_add_rc = KILN_OK;
static int32_t combat_value = 55;
static bool combat_no_max = false;  // the pair has no readable maximum
static KilnCombatApi combat_full{};
static void test_attributes() {
    auto *w = reinterpret_cast<EmfWorld *>(worlds[0]);
    int32_t out = -1;
    S.combat = nullptr;
    int before = unsupported_calls;
    CHECK(!table.attribute_get(w, 7, EMF_ATTR_STAMINA, &out) && out == -1 && unsupported_calls == before + 1);
    CHECK(!table.attribute_add(w, 7, EMF_ATTR_STAMINA, 5) && unsupported_calls == before + 2);

    // a kiln.combat table that ends before the v2 functions: "not supported", never a call through a missing slot
    static KilnCombatApi v1{};
    v1.header.size = offsetof(KilnCombatApi, attribute_ids);
    S.combat = &v1;
    CHECK(!table.attribute_get(w, 7, EMF_ATTR_STAMINA, &out) && !table.attribute_add(w, 7, EMF_ATTR_MANA, 1));
    CHECK(unsupported_calls == before + 4);

    combat_full.header.size = sizeof combat_full;
    combat_full.header.version = KILN_COMBAT_VERSION;
    combat_full.attribute_get = [](KilnEntity, uint32_t id, KilnAttributeValue *v) -> KilnResult {
        ++combat_get_calls;
        combat_id = id;
        if (combat_get_rc == KILN_OK) {
            v->value = combat_value;
            v->is_current = 1;
            v->has_max = combat_no_max ? 0 : 1;
        }
        return combat_get_rc;
    };
    combat_full.attribute_add = [](KilnEntity, uint32_t id, int32_t delta, KilnAttributeValue *) -> KilnResult {
        ++combat_add_calls;
        combat_id = id;
        combat_delta = delta;
        // as kiln.combat: only a pair's current value can be added to
        if (id == EMF_ATTR_MAX_HEALTH || id == EMF_ATTR_MAX_STAMINA || id == EMF_ATTR_MAX_MANA)
            return KILN_ERR_INVALID_ARGUMENT;
        return combat_add_rc;
    };
    S.combat = &combat_full;

    // every id goes through unchanged, and the value comes back
    for (uint32_t id : {EMF_ATTR_HEALTH, EMF_ATTR_STAMINA, EMF_ATTR_MANA, EMF_ATTR_MAX_HEALTH, EMF_ATTR_MAX_STAMINA, EMF_ATTR_MAX_MANA, 0x12345678u}) {
        out = -1;
        combat_id = 0;
        CHECK(table.attribute_get(w, 7, id, &out) && out == 55 && combat_id == id);
    }
    // EMF's plain false: no such entity or component (no refusal line), a bad id and an off feature are logged
    const int refused = refusals;
    combat_get_rc = KILN_ERR_NOT_FOUND;
    out = -1;
    CHECK(!table.attribute_get(w, 7, EMF_ATTR_MANA, &out) && out == -1 && refusals == refused);
    combat_get_rc = KILN_ERR_INVALID_ARGUMENT;
    CHECK(!table.attribute_get(w, 7, 0xDEADBEEFu, &out) && out == -1 && refusals == refused + 1);
    combat_get_rc = KILN_ERR_UNAVAILABLE;
    const int unsup = unsupported_calls;
    CHECK(!table.attribute_get(w, 7, EMF_ATTR_STAMINA, &out) && unsupported_calls == unsup + 1);
    combat_get_rc = KILN_OK;
    // the server world's thread only, a server world only, an out pointer
    const int calls = combat_get_calls;
    tick_thread = false;
    CHECK(!table.attribute_get(w, 7, EMF_ATTR_STAMINA, &out) && combat_get_calls == calls);
    tick_thread = true;
    CHECK(!table.attribute_get(reinterpret_cast<EmfWorld *>(worlds[2]), 7, EMF_ATTR_STAMINA, &out) && combat_get_calls == calls);
    CHECK(!table.attribute_get(w, 7, EMF_ATTR_STAMINA, nullptr) && combat_get_calls == calls);

    // attribute_add on the world's thread runs at once, with the id and the delta as given
    posted.clear();
    combat_add_calls = 0;
    CHECK(table.attribute_add(w, 7, EMF_ATTR_STAMINA, -12) && combat_add_calls == 1 && combat_id == EMF_ATTR_STAMINA && combat_delta == -12);
    CHECK(table.attribute_add(w, 7, EMF_ATTR_MANA, 30) && combat_add_calls == 2 && combat_id == EMF_ATTR_MANA && combat_delta == 30);
    CHECK(table.attribute_add(w, 7, EMF_ATTR_HEALTH, 0) && combat_add_calls == 3 && combat_delta == 0);
    CHECK(posted.empty());
    // maximums are refused (kiln.combat's INVALID_ARGUMENT), with a log line
    const int r0 = refusals;
    CHECK(!table.attribute_add(w, 7, EMF_ATTR_MAX_STAMINA, 10) && refusals == r0 + 1);
    CHECK(!table.attribute_add(w, 7, EMF_ATTR_MAX_HEALTH, 10) && !table.attribute_add(w, 7, EMF_ATTR_MAX_MANA, 10) && refusals == r0 + 3);
    combat_add_rc = KILN_ERR_NOT_FOUND;
    CHECK(!table.attribute_add(w, 99, EMF_ATTR_STAMINA, 1) && refusals == r0 + 4);
    combat_add_rc = KILN_ERR_UNAVAILABLE;
    const int u0 = unsupported_calls;
    CHECK(!table.attribute_add(w, 7, EMF_ATTR_STAMINA, 1) && unsupported_calls == u0 + 1);
    combat_add_rc = KILN_OK;
    // EMF: a heal of an attribute without a readable maximum is refused; damage to it goes through
    combat_no_max = true;
    const int r_nomax = refusals, a_nomax = combat_add_calls;
    CHECK(!table.attribute_add(w, 7, EMF_ATTR_STAMINA, 5) && refusals == r_nomax + 1 && combat_add_calls == a_nomax);
    CHECK(table.attribute_add(w, 7, EMF_ATTR_STAMINA, -5) && combat_add_calls == a_nomax + 1);
    combat_no_max = false;
    // any other thread: queued (true), run by the next tick, a later refusal is logged
    tick_thread = false;
    combat_add_calls = 0;
    CHECK(table.attribute_add(w, 7, EMF_ATTR_STAMINA, -5) && combat_add_calls == 0 && posted.size() == 1);
    CHECK(table.attribute_add(w, 7, EMF_ATTR_MAX_STAMINA, -5) && posted.size() == 2);  // queued: it is refused when it runs
    tick_thread = true;
    const int r1 = refusals;
    for (auto &f : posted)
        f();
    posted.clear();
    CHECK(combat_add_calls == 2 && refusals == r1 + 1);
    // a server world only
    CHECK(!table.attribute_add(reinterpret_cast<EmfWorld *>(worlds[2]), 7, EMF_ATTR_STAMINA, 1) && combat_add_calls == 2);
    CHECK(!table.attribute_add(nullptr, 7, EMF_ATTR_STAMINA, 1) && combat_add_calls == 2);
    // XP and level stay on kiln.progression: kiln.combat is not asked
    const int gets = combat_get_calls, adds = combat_add_calls;
    submitted.clear();
    CHECK(table.attribute_add(w, 2, EMF_ATTR_XP_PENDING, 50) && submitted.size() == 1 && submitted[0].op == KILN_PROG_OP_ADD_EXPERIENCE);
    CHECK(!table.attribute_add(w, 2, EMF_ATTR_LEVEL, 1) && !table.attribute_add(w, 2, EMF_ATTR_XP, 1) && !table.attribute_add(w, 2, EMF_ATTR_XP_PENDING, -5));
    out = -1;
    const_cast<KilnProgressionApi *>(S.progression)->get = [](KilnEntity, KilnProgression *p) -> KilnResult {
        p->flags = KILN_PROG_HAS_LEVEL;
        p->level = 12;
        return KILN_OK;
    };
    CHECK(table.attribute_get(w, 2, EMF_ATTR_LEVEL, &out) && out == 12);
    CHECK(combat_get_calls == gets && combat_add_calls == adds);
    submitted.clear();
}

// ---- v10 wheel, and right clicks on windows ----
static int wheel_cb_calls = 0, wheel_last_choice = -99;
static EmfWorld *wheel_last_world = nullptr;
static void *wheel_last_user = nullptr;
static void wheel_cb(EmfWorld *w, int choice, void *user) {
    ++wheel_cb_calls;
    wheel_last_choice = choice;
    wheel_last_world = w;
    wheel_last_user = user;
}
// v28-v40: every slot filled; a function Kiln does not carry yet gives EMF's documented failure, clears its out buffer,
// says why where the request has room for it, refuses a request that is too small, and never calls a callback.
static int v40_callbacks = 0;
static std::atomic<bool> render_ok{true};
static std::atomic<int> render_lookups{0};
static KilnRenderApi fake_render{};
// A kiln-emf without kiln.render (a dedicated server, an older core): its own process, because the lookup happens once.
static int test_v40_no_render(KilnApi &api) {
    api.service = [](KilnStr, uint32_t) -> const void * {
        ++render_lookups;
        return nullptr;
    };
    EmfApi t{};
    fill_v40(&t);
    float px = -1, py = -1;
    const int before = unsupported_calls;
    CHECK(!t.world_to_screen(1, 2, 3, &px, &py) && px == -1 && py == -1);
    CHECK(!t.world_to_screen(1, 2, 3, nullptr, nullptr));
    CHECK(unsupported_calls == before + 2 && render_lookups == 1);
    std::printf("glue_test (no kiln.render): %d checks, %d failures\n", checks, failures);
    return failures ? 1 : 0;
}
static void test_v40(KilnApi &api) {
    fake_render.header.size = sizeof fake_render;
    fake_render.header.version = KILN_RENDER_VERSION;
    fake_render.world_to_screen = [](const double w[3], float, float, float out[3]) -> bool {
        out[0] = static_cast<float>(w[0]) + 0.5f;
        out[1] = static_cast<float>(w[2]) + 0.25f;
        out[2] = 3.f;
        return render_ok.load();
    };
    api.service = [](KilnStr n, uint32_t) -> const void * {
        ++render_lookups;
        std::this_thread::sleep_for(std::chrono::milliseconds(50));  // widen the window of a racing first call
        return std::string(n.ptr, n.len) == KILN_RENDER_SERVICE ? &fake_render : nullptr;
    };
    EmfApi t{};
    fill_v40(&t);
    {
        // world_to_screen is an any-thread function: first calls that race see one lookup and all get the answer
        std::atomic<int> good{0};
        std::vector<std::thread> threads;
        for (int i = 0; i < 8; ++i)
            threads.emplace_back([&] {
                float px = -1, py = -1;
                if (t.world_to_screen(10, 20, 30, &px, &py) && px == 10.5f && py == 30.25f) ++good;
            });
        for (auto &th : threads) th.join();
        CHECK(good == 8 && render_lookups == 1);
    }
    // the two chat functions come from fill_game (api_game.cpp, not built into this test)
    const size_t first = offsetof(EmfApi, on_player) / sizeof(void *), n = sizeof(EmfApi) / sizeof(void *);
    const auto *slots = reinterpret_cast<void *const *>(&t);
    int empty = 0;
    for (size_t i = first; i < n; ++i)
        if (!slots[i] && i != offsetof(EmfApi, chat_tell_ex) / sizeof(void *) &&
            i != offsetof(EmfApi, chat_broadcast_ex) / sizeof(void *))
            ++empty;
    CHECK(n - first == 52 && empty == 0);  // v28-v40: 45; v43: 1, v44: 3, v46: 3
    CHECK(!t.chat_tell_ex && !t.chat_broadcast_ex && !t.net_peer);

    auto *w = reinterpret_cast<EmfWorld *>(worlds[0]);
    const int before = unsupported_calls, refused_before = refusals;
    char buf[16];
    auto dirty = [&] { std::memset(buf, 'x', sizeof buf); };
    CHECK(!t.on_player([](EmfWorld *, const EmfPlayerEvent *, void *) { ++v40_callbacks; }, nullptr));
    dirty();
    CHECK(t.item_name(w, 1, 0, buf, sizeof buf) == 0 && buf[0] == 0);
    CHECK(t.item_name(w, 1, 0, nullptr, 0) == 0);
    dirty();
    CHECK(t.game_language(buf, sizeof buf) == 0 && buf[0] == 0);
    CHECK(t.game_language(nullptr, 0) == 0);
    dirty();
    CHECK(!t.player_character_name(w, 1, buf, sizeof buf) && buf[0] == 0);
    CHECK(!t.on_hit([](EmfWorld *, const EmfHit *, void *) { ++v40_callbacks; }, nullptr));
    CHECK(!t.on_hit_before([](EmfWorld *, EmfHit *, void *) { ++v40_callbacks; }, nullptr));
    EmfHitRule rule{};
    rule.size = sizeof rule;
    CHECK(t.hit_rule_add(&rule) == 0 && !t.hit_rule_remove(1));
    EmfBuffType bt{};
    EmfBuff b{};
    CHECK(t.buff_types(w, &bt, 1) == 0 && !t.buff_type(w, 1, &bt) && t.buffs_of(w, 1, &b, 1) == 0);
    dirty();
    CHECK(!t.buff_text(1, 0, buf, sizeof buf) && buf[0] == 0);
    auto buff_cb = [](EmfWorld *, const EmfBuffEvent *, void *) { ++v40_callbacks; };
    CHECK(!t.buff_apply(w, 1, 2, 0, 0, 0, buff_cb, nullptr) && !t.buff_remove(w, 1, 2, buff_cb, nullptr) &&
          !t.on_buff(buff_cb, nullptr));
    EmfMarker mk{};
    mk.size = sizeof mk;
    mk.id = "m";
    EmfMapMarkerType mt{};
    CHECK(!t.marker_set(&mk) && !t.marker_remove("m") && t.marker_clear() == 0 && t.marker_types(&mt, 1) == 0);
    CHECK(t.marker_send(0, &mk, 1) == 0 && t.marker_unsend(0, "m") == 0);
    double x = 7, z = 9;
    CHECK(!t.map_cursor(&x, &z) && x == 7 && z == 9);
    CHECK(!t.on_map_click([](const EmfMapClick *, void *) { ++v40_callbacks; }, nullptr));
    CHECK(!t.map_action("a", "A", 0, [](EmfWorld *, const EmfMapDone *, void *) { ++v40_callbacks; }, nullptr));
    CHECK(!t.ground_at(w, 1, 2, [](EmfWorld *, const EmfGround *, void *) { ++v40_callbacks; }, nullptr));
    EmfPlayerView view{};
    view.size = sizeof view;
    float explored = 4;
    EmfGameMarker gm{};
    EmfCanvas canvas{};
    canvas.size = sizeof canvas;
    EmfCanvasCmd cmd{};
    CHECK(!t.player_view(&view) && !t.fog_at(1, 2, &explored) && explored == 4 && t.map_markers(&gm, 1) == 0);
    CHECK(!t.canvas_set("c", &canvas) && !t.canvas_draw("c", &cmd, 1));
    EmfPathQuery q{};
    q.size = sizeof q;
    CHECK(t.path_find(w, &q, [](EmfWorld *, uint32_t, const EmfPathResult *, const EmfPathPoint *, void *) { ++v40_callbacks; },
                      nullptr) == 0 &&
          !t.path_cancel(1));
    CHECK(t.door_state(w, 1) == EMF_DOOR_NONE && !t.door_set(w, 1, 1, 0) && !t.door_lock(w, 1, 1));
    CHECK(!t.enemy_order_cancel(1) && t.enemy_target(w, 1) == 0 && !t.enemy_move_cancel(1) && t.enemy_moving(w, 1) == 0);
    CHECK(v40_callbacks == 0);
    CHECK(refusals == refused_before);

    // the three requests with a `why`: refused with the reason, and a request that is too small is refused untouched
    EmfRemoveBuilding rb{};
    rb.size = sizeof rb;
    rb.entity = 5;
    rb.refunded = 77;
    CHECK(!t.remove_building(w, &rb) && rb.refunded == 0 && std::string(rb.why) == "not available on Kiln yet");
    EmfEnemyOrder eo{};
    eo.size = sizeof eo;
    CHECK(t.enemy_order(w, &eo) == 0 && std::string(eo.why) == "not available on Kiln yet");
    EmfMoveOrder mo{};
    mo.size = sizeof mo;
    CHECK(t.enemy_move(w, &mo, [](EmfWorld *, uint32_t, uint32_t, void *) { ++v40_callbacks; }, nullptr) == 0 &&
          std::string(mo.why) == "not available on Kiln yet");
    // v41 (EMF 0.6.10): a mod built with 0.6.11's header sends 136 bytes with stuck_seconds and EMF_MOVE_PEACEFUL_ALL;
    // one built with 0.6.8's sends 128. Both get the same refusal with its reason, and no callback.
    CHECK(sizeof mo == 136);
    mo = {};
    mo.size = sizeof mo;
    mo.flags = EMF_MOVE_PEACEFUL | EMF_MOVE_PEACEFUL_ALL;
    mo.stuck_seconds = 30;
    CHECK(t.enemy_move(w, &mo, [](EmfWorld *, uint32_t, uint32_t, void *) { ++v40_callbacks; }, nullptr) == 0 &&
          std::string(mo.why) == "not available on Kiln yet");
    mo = {};
    mo.size = 128;
    CHECK(t.enemy_move(w, &mo, nullptr, nullptr) == 0 && std::string(mo.why) == "not available on Kiln yet");
    const int counted = unsupported_calls - before;
    CHECK(counted >= 42);  // every function that is not carried reported itself at least once
    rb = {};
    rb.size = sizeof rb - 1;
    rb.refunded = 77;
    CHECK(!t.remove_building(w, &rb) && rb.refunded == 77 && rb.why[0] == 0);
    CHECK(!t.remove_building(w, nullptr));
    eo = {};
    eo.size = 8;
    CHECK(t.enemy_order(w, &eo) == 0 && eo.why[0] == 0 && t.enemy_order(w, nullptr) == 0);
    mo = {};
    mo.size = 8;
    CHECK(t.enemy_move(w, &mo, nullptr, nullptr) == 0 && mo.why[0] == 0 && t.enemy_move(w, nullptr, nullptr, nullptr) == 0);
    CHECK(refusals == refused_before + 6 && unsupported_calls - before == counted && v40_callbacks == 0);

    // world_to_screen: kiln.render's answer; a point behind the camera (false) leaves px, py alone
    float px = -1, py = -1;
    CHECK(t.world_to_screen(10, 20, 30, &px, &py) && px == 10.5f && py == 30.25f);
    CHECK(t.world_to_screen(10, 20, 30, nullptr, nullptr));
    render_ok = false;
    px = py = -1;
    CHECK(!t.world_to_screen(10, 20, 30, &px, &py) && px == -1 && py == -1);
    render_ok = true;
    CHECK(unsupported_calls - before == counted);
}

// v43-v46 (EMF 0.6.13 and 0.6.14): seven functions Kiln does not carry yet, by the rules of v28-v40's. v45 added no
// function (EmfBlockInfo.world, EMF_BREAK_WORLD); v46 also gave EmfMoveOrder hold_seconds (pad2's place) and the flag
// EMF_MOVE_HOLD, which change nothing in the answer.
static int v46_callbacks = 0;
static void test_v46() {
    EmfApi t{};
    fill_v40(&t);
    CHECK(t.player_permissions && t.break_blocks && t.block_at && t.find_wall && t.villager_move && t.villager_move_cancel &&
          t.villager_moving);
    auto *w = reinterpret_cast<EmfWorld *>(worlds[0]);
    const int before = unsupported_calls, refused_before = refusals;
    auto break_cb = [](EmfWorld *, uint32_t, const EmfBreakResult *, void *) { ++v46_callbacks; };
    auto move_cb = [](EmfWorld *, uint32_t, uint32_t, void *) { ++v46_callbacks; };
    const std::string not_yet = "not available on Kiln yet";

    // v43: false and *perms = 0, as for an entity that is no player
    uint32_t perms = 0x3F;
    CHECK(!t.player_permissions(w, 7, &perms) && perms == 0);
    CHECK(!t.player_permissions(w, 7, nullptr));

    // v44: a break is refused with its reason and no callback; block_at and find_wall answer false and leave *out as
    // it was (EMF writes it only on success): a mod built with 0.6.14's SDK keeps world = -1, "not known"
    EmfBreakBlocks bb{};
    bb.size = sizeof bb;
    bb.flags = EMF_BREAK_INSTANT | EMF_BREAK_DROPS;
    bb.x = 1, bb.y = 2, bb.z = 3, bb.radius = 2, bb.height = 1;
    // EMF's own text, which mods compare (Night Raids 1.2.1: no wall search, raiders go for doors; GitHub #43)
    CHECK(t.break_blocks(w, &bb, break_cb, nullptr) == 0 && bb.why == std::string("not available on this game version"));
    // v45: the flag for the world's own blocks gets the same answer
    bb = {};
    bb.size = sizeof bb;
    bb.flags = EMF_BREAK_WORLD;
    CHECK(t.break_blocks(w, &bb, nullptr, nullptr) == 0 && bb.why == std::string("not available on this game version"));
    EmfBlockInfo info{};
    info.size = sizeof info;
    info.kind = 77;
    info.world = -1;
    const float from[3] = {0, 0, 0}, to[3] = {10, 0, 0};
    CHECK(!t.block_at(w, 1, 2, 3, &info) && info.size == sizeof info && info.kind == 77 && info.world == -1);
    CHECK(!t.find_wall(w, from, to, &info) && info.size == sizeof info && info.kind == 77 && info.world == -1);
    info.size = 40;  // a v44 mod's EmfBlockInfo, before `world`
    CHECK(!t.block_at(w, 1, 2, 3, &info) && !t.find_wall(w, from, to, &info) && info.size == 40 && info.kind == 77);
    CHECK(!t.block_at(w, 1, 2, 3, nullptr) && !t.find_wall(w, nullptr, nullptr, nullptr));

    // v46: a villager move gets enemy_move's refusal, with hold_seconds or EMF_MOVE_HOLD too, and no callback
    EmfPathPoint point{1, 2, 3, 0};
    EmfMoveOrder mo{};
    mo.size = sizeof mo;
    mo.enemy = 9;
    mo.count = 1;
    mo.points = &point;
    CHECK(t.villager_move(w, &mo, move_cb, nullptr) == 0 && mo.why == not_yet);
    mo.why[0] = 0;
    mo.flags = EMF_MOVE_HOLD;
    CHECK(t.villager_move(w, &mo, move_cb, nullptr) == 0 && mo.why == not_yet);
    mo.why[0] = 0;
    mo.flags = 0;
    mo.hold_seconds = 60;
    mo.stuck_seconds = 20;
    CHECK(t.villager_move(w, &mo, move_cb, nullptr) == 0 && mo.why == not_yet);
    mo = {};
    mo.size = 128;  // a mod built with EMF 0.6.8's header
    CHECK(t.villager_move(w, &mo, nullptr, nullptr) == 0 && mo.why == not_yet);
    CHECK(!t.villager_move_cancel(1) && t.villager_moving(w, 9) == 0);
    // enemy_move with EMF_MOVE_HOLD (EMF: "EMF_MOVE_HOLD is for villager_move"): refused as every order on Kiln
    mo = {};
    mo.size = sizeof mo;
    mo.flags = EMF_MOVE_HOLD;
    CHECK(t.enemy_move(w, &mo, move_cb, nullptr) == 0 && mo.why == not_yet);
    CHECK(v46_callbacks == 0 && refusals == refused_before);
    // each of the seven reported itself (one kiln.log line per mod and function, state/emf.json "unsupported")
    for (const char *fn : {"player_permissions", "break_blocks", "block_at", "find_wall", "villager_move",
                           "villager_move_cancel", "villager_moving"})
        CHECK(unsupported_fns.count(fn) == 1);
    const int counted = unsupported_calls - before;
    CHECK(counted >= 7);

    // a request whose size is below EMF's minimum is refused untouched (no `why`), as EMF refuses it
    bb = {};
    bb.size = sizeof bb - 1;
    CHECK(t.break_blocks(w, &bb, break_cb, nullptr) == 0 && bb.why[0] == 0);
    CHECK(t.break_blocks(w, nullptr, break_cb, nullptr) == 0);
    mo = {};
    mo.size = 127;
    mo.flags = EMF_MOVE_HOLD;
    CHECK(t.villager_move(w, &mo, move_cb, nullptr) == 0 && mo.why[0] == 0);
    CHECK(t.villager_move(w, nullptr, move_cb, nullptr) == 0);
    CHECK(refusals == refused_before + 4 && unsupported_calls - before == counted && v46_callbacks == 0);
}

static void test_wheel_and_right_clicks(KilnUiApi &ui) {
    auto *server = reinterpret_cast<EmfWorld *>(worlds[0]);
    int token = 0;
    const char *const items[] = {"Wolf", "Bear (level 3)", "Fox"};
    EmfWheel def{};
    def.size = sizeof def;
    def.title = "Pets";
    def.items = items;
    def.count = 3;
    def.hold_key = 0x10;
    def.world_kind = EMF_WORLD_SERVER;
    def.cb = wheel_cb;
    def.user = &token;
    windows_world_started(EMF_WORLD_SERVER);
    wheel_open_now = false;
    wheel_open_calls = 0;

    // opens: kiln.ui gets the copied definition
    CHECK(table.wheel_open(&def));
    CHECK(wheel_open_calls == 1 && wheel_count == 3 && wheel_title == "Pets" && wheel_hold == 0x10 && wheel_items[1] == "Bear (level 3)");
    // one at a time (EMF: another wheel is open), and no window meanwhile (EMF's window_open rule)
    int refused = refusals;
    CHECK(!table.wheel_open(&def) && wheel_open_calls == 1 && refusals == refused + 1);
    auto win = window_def();
    CHECK(!table.window_open(&win) && refusals == refused + 2);
    // the pick reaches the callback after the next step of its world, on that world's thread, once
    wheel_fn(1, wheel_user);
    CHECK(wheel_cb_calls == 0);
    wheel_open_now = false;  // kiln.ui closed it before calling back
    windows_after_step(server, EMF_WORLD_SERVER);
    CHECK(wheel_cb_calls == 1 && wheel_last_choice == 1 && wheel_last_world == server && wheel_last_user == &token);
    windows_after_step(server, EMF_WORLD_SERVER);
    CHECK(wheel_cb_calls == 1);
    // a cancel is -1
    CHECK(table.wheel_open(&def));
    wheel_fn(-1, wheel_user);
    wheel_open_now = false;
    windows_after_step(server, EMF_WORLD_CLIENT);  // the wrong kind of world: not yet
    CHECK(wheel_cb_calls == 1);
    windows_after_step(server, EMF_WORLD_SERVER);
    CHECK(wheel_cb_calls == 2 && wheel_last_choice == -1);
    // a stale callback of a wheel that is not the open one is ignored
    CHECK(table.wheel_open(&def));
    void *old_user = wheel_user;
    wheel_fn(0, old_user);
    wheel_open_now = false;
    CHECK(table.wheel_open(&def));
    wheel_fn(2, old_user);  // the first wheel's serial again
    windows_after_step(server, EMF_WORLD_SERVER);
    CHECK(wheel_cb_calls == 3 && wheel_last_choice == 0);
    wheel_fn(1, wheel_user);
    wheel_open_now = false;
    windows_after_step(server, EMF_WORLD_SERVER);
    CHECK(wheel_cb_calls == 4 && wheel_last_choice == 1);

    // the world ends while it is open: closed in kiln.ui, no callback; a pick not yet delivered is dropped too
    CHECK(table.wheel_open(&def));
    wheel_closed = 0;
    windows_world_stopping(EMF_WORLD_SERVER);
    CHECK(wheel_closed == 99 && !wheel_open_now);
    wheel_fn(1, wheel_user);  // a late callback from the render thread
    windows_after_step(server, EMF_WORLD_SERVER);
    CHECK(wheel_cb_calls == 4);
    windows_world_started(EMF_WORLD_SERVER);
    CHECK(table.wheel_open(&def));
    wheel_fn(1, wheel_user);
    wheel_open_now = false;
    windows_world_stopping(EMF_WORLD_SERVER);
    windows_world_started(EMF_WORLD_SERVER);
    windows_after_step(server, EMF_WORLD_SERVER);
    CHECK(wheel_cb_calls == 4);

    // refusals: kiln.ui's (another wheel of another mod, the menu), a bad definition, no world of that kind
    refused = refusals;
    wheel_open_now = true;  // someone else's wheel
    CHECK(!table.wheel_open(&def) && refusals == refused + 1);
    wheel_open_now = false;
    wheel_rc = KILN_ERR_UNAVAILABLE;  // the Kiln menu is open / no window yet
    CHECK(!table.wheel_open(&def) && refusals == refused + 2);
    wheel_rc = KILN_ERR_INVALID_ARGUMENT;
    CHECK(!table.wheel_open(&def) && refusals == refused + 3);
    wheel_rc = KILN_OK;
    CHECK(table.wheel_open(&def));  // the failures left nothing behind
    wheel_fn(-1, wheel_user);
    wheel_open_now = false;
    windows_after_step(server, EMF_WORLD_SERVER);
    const int calls = wheel_open_calls;
    EmfWheel bad = def;
    bad.count = 0;
    CHECK(!table.wheel_open(&bad));
    bad = def;
    bad.hold_key = 1;  // VK_LBUTTON
    CHECK(!table.wheel_open(&bad));
    bad = def;
    bad.hold_key = 0xA0;  // VK_LSHIFT, not VK_SHIFT
    CHECK(!table.wheel_open(&bad));
    bad = def;
    bad.cb = nullptr;
    CHECK(!table.wheel_open(&bad) && !table.wheel_open(nullptr));
    CHECK(wheel_open_calls == calls);  // refused before kiln.ui was asked
    worlds[0] = 0;  // no server world: its callback would never come
    CHECK(!table.wheel_open(&def) && wheel_open_calls == calls);
    bad = def;
    bad.world_kind = EMF_WORLD_CLIENT;
    CHECK(table.wheel_open(&bad));  // a client world runs
    wheel_fn(0, wheel_user);
    wheel_open_now = false;
    worlds[0] = 0x1000;
    windows_after_step(reinterpret_cast<EmfWorld *>(worlds[2]), EMF_WORLD_CLIENT);
    CHECK(wheel_last_choice == 0 && wheel_last_world == reinterpret_cast<EmfWorld *>(worlds[2]));

    // a dedicated server has no overlay: EMF's failure value, kiln.ui never asked (even for a valid definition)
    const int calls2 = wheel_open_calls;
    refused = refusals;
    g_side = KILN_SIDE_SERVER;
    CHECK(!table.wheel_open(&def) && wheel_open_calls == calls2 && refusals == refused + 1);
    CHECK(!table.wheel_open(&bad) && wheel_open_calls == calls2);
    g_side = KILN_SIDE_CLIENT;
    // no kiln.ui at all, and a kiln.ui without the wheel (an older core): false, "not supported"
    const KilnUiApi *saved = S.ui;
    S.ui = nullptr;
    CHECK(!table.wheel_open(&def));
    S.ui = saved;
    const uint32_t full = ui.header.size;
    const int unsup = unsupported_calls;
    ui.header.size = offsetof(KilnUiApi, is_item_clicked);
    CHECK(!table.wheel_open(&def) && unsupported_calls == unsup + 1 && wheel_open_calls == calls2);
    ui.header.size = full;

    // right clicks: a widget with a key reports EMF_UI_RIGHT_CLICK next to its left click
    wheel_open_now = false;
    close_frame = false;
    button_clicked = false;
    right_click_now = true;
    EmfUiWidget widgets[3]{};
    widgets[0].type = EMF_UI_BUTTON;
    widgets[0].key = "slot:1";
    widgets[1].type = EMF_UI_TEXT;  // a text takes no clicks
    widgets[1].key = "text";
    widgets[2].type = EMF_UI_BUTTON;
    widgets[2].key = "off";
    widgets[2].flags = EMF_UI_DISABLED;
    auto rw = window_def();
    rw.widgets = widgets;
    rw.count = 3;
    CHECK(table.window_open(&rw));
    KilnUiFrame fr{};
    fr.dpi_scale = 1;
    const int rc0 = right_clicks, cc0 = clicks;
    frame_fn(&fr, nullptr);
    windows_after_step(server, EMF_WORLD_SERVER);
    CHECK(right_clicks == rc0 + 1 && clicks == cc0 && last.kind == EMF_UI_RIGHT_CLICK && last_key == "slot:1" && last_window == "test.win");
    // not while the pointer is not right-clicking; a left click is still a click
    right_click_now = false;
    button_clicked = true;
    frame_fn(&fr, nullptr);
    windows_after_step(server, EMF_WORLD_SERVER);
    CHECK(right_clicks == rc0 + 1 && clicks == cc0 + 1);
    // a kiln.ui without is_item_clicked reports no right clicks (and is never called)
    right_click_now = true;
    button_clicked = false;
    ui.header.size = offsetof(KilnUiApi, is_item_clicked);
    frame_fn(&fr, nullptr);
    windows_after_step(server, EMF_WORLD_SERVER);
    CHECK(right_clicks == rc0 + 1);
    ui.header.size = full;
    // the CLOSED event is still the last
    CHECK(table.window_close("test.win"));
    windows_after_step(server, EMF_WORLD_SERVER);
    CHECK(last.kind == EMF_UI_CLOSED);
    right_click_now = false;
    button_clicked = true;
}

int main(int argc, char **argv) {
    if (argc != 2 && argc != 3)
        return 2;
    std::setvbuf(stdout, nullptr, _IONBF, 0); // a crash must not eat the FAIL lines
    P.emf = argv[1];
    static KilnApi api{};
    api.world = [](uint32_t k) { return worlds[k]; };
    api.on_tick_thread = []() { return tick_thread; };
    api.constant = [](KilnStr n, int64_t *v) -> KilnResult {
        const std::string s(n.ptr, n.len);
        *v = s == "combat/evq.first"         ? 0
             : s == "combat/evq.blocks"      ? 8
             : s == "combat/evq.count"       ? 16
             : s == "combat/evq.next"        ? 0
             : s == "combat/evq.block_shift" ? 8
                                             : -1;
        return *v < 0 ? KILN_ERR_NOT_FOUND : KILN_OK;
    };
    api.symbol = [](KilnStr, uintptr_t *) -> KilnResult { return KILN_ERR_NOT_FOUND; };
    api.on_world_thread = [](uint32_t) { return tick_thread; };
    api.session = [](KilnSessionInfo *s) -> KilnResult {
        for (int i = 0; i < 3; ++i) {
            s->sessions[i] = session_id;
            s->worlds[i] = worlds[i];
        }
        return KILN_OK;
    };
    g_api = &api;
    if (argc == 3 && std::string(argv[2]) == "no-render")
        return test_v40_no_render(api);
    static KilnUiApi ui{};
    init_ui(ui);
    S.ui = &ui;
    static KilnProgressionApi prog{};
    S.progression = &prog;
    fill_windows(&table);
    fill_attributes(&table);
    fill_level(&table);
    fill_components(&table);
    fill_events(&table);
    static KilnWorldApi world{};
    S.world = &world;
    test_components(world);
    test_level(prog);
    test_window_sizing(ui);
    test_window_log(ui);
    auto d = window_def();
    CHECK(table.window_open(&d));
    active_mod = &other_mod;
    CHECK(!table.window_update(&d) && !table.window_close(d.id) && !table.window_open(&d));
    active_mod = &mod;
    // Closing from a menu/render callback must run outside the window mutex:
    // callback reenters the API.
    worlds[0] = 0;
    reenter = true;
    KilnUiFrame fr{};
    fr.dpi_scale = 1;
    frame_fn(&fr, nullptr);
    CHECK(closed == 1 && captured == 0 && last.flags == EMF_UI_NO_WORLD);
    worlds[0] = 0x1000;
    CHECK(table.window_open(&d));
    // Missing world: lifecycle close is tested via the same entry point the
    // plugin calls.
    windows_world_stopping(EMF_WORLD_SERVER);
    CHECK(closed == 2 && last.reason == EMF_UI_BY_CORE && last.flags == EMF_UI_NO_WORLD && captured == 0);
    // World change must deliver exactly one close, never retain an old pending
    // BY_MOD close.
    windows_world_started(EMF_WORLD_SERVER);
    CHECK(table.window_open(&d));
    CHECK(table.window_close(d.id));
    windows_world_stopping(EMF_WORLD_SERVER);
    CHECK(closed == 3 && last.flags == EMF_UI_NO_WORLD);
    windows_after_step(reinterpret_cast<EmfWorld *>(worlds[0]), EMF_WORLD_SERVER);
    CHECK(closed == 3);
    windows_world_started(EMF_WORLD_SERVER);
    EmfUiWidget button{};
    button.type = EMF_UI_BUTTON;
    button.key = "click";
    d.widgets = &button;
    d.count = 1;
    close_frame = false;
    CHECK(table.window_open(&d));
    frame_fn(&fr, nullptr);
    frame_fn(&fr, nullptr);
    stop_on_click = true;
    windows_after_step(reinterpret_cast<EmfWorld *>(worlds[0]), EMF_WORLD_SERVER);
    CHECK(clicks == 1 && closed == 4); // CLOSED frees user: the second extracted
                                       // CLICK must be suppressed
    test_images(ui);
    test_pending_portrait();
    test_attributes();
    test_wheel_and_right_clicks(ui);
    test_v40(api);
    test_v46();
    windows_shutdown();
    CHECK(live_images == 0);
    std::printf("glue_test: %d checks, %d failures\n", checks, failures);
    return failures ? 1 : 0;
}
