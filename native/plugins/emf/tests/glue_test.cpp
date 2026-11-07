// SPDX-License-Identifier: MIT
// Exercise the production adapters against fake Kiln services, including
// callback reentrancy.
#include "emf_internal.h"
#include "v26_model.h"
#include <algorithm>
#include <cstdio>
#include <fstream>
#include <map>
#include <stdexcept>

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
void mod_logf(const Mod *, int, const char *, ...) {}
void unsupported(const Mod *, const char *, const char *) {}
void refuse(const Mod *, const char *, const char *, bool) {}
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
uint32_t spawn_for(Mod *, EmfWorld *, const uint8_t *, double, double, double, const char *) { return 5000; }
uint32_t fnv1a(std::string_view s) {
    uint32_t h = 2166136261u;
    for (auto c : s)
        h = (h ^ static_cast<uint8_t>(c)) * 16777619u;
    return h;
}
uint32_t type_size(uint32_t) { return 24; }
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
static int closed = 0, clicks = 0;
static EmfUiEvent last{};
static bool reenter = false, stop_on_click = false, close_frame = true;
static void ui_cb(EmfWorld *, const EmfUiEvent *e, void *) {
    if (e->kind == EMF_UI_CLOSED) {
        ++closed;
        last = *e;
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
static void init_ui(KilnUiApi &ui) {
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
    ui.set_next_window_pos = [](float, float, uint32_t, float, float) {};
    ui.set_next_window_size = [](float, float, uint32_t) {};
    ui.begin = [](KilnStr, bool *open, uint32_t) {
        *open = !close_frame;
        return !close_frame;
    };
    ui.push_id = [](KilnStr) {};
    ui.pop_id = []() {};
    ui.button = [](KilnStr, float, float) { return true; };
    ui.end = []() {};
    ui.unsubscribe = [](KilnHandle) -> KilnResult { return KILN_OK; };
    ui.set_capture = [](uint32_t c) { captured = c; };
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
static std::map<std::string, KilnProgramDesc> programs;
static std::map<std::pair<uint32_t, uint32_t>, std::vector<uint8_t>> components;
static int accepts = 0;
static void accepted_cb(EmfWorld *, uint32_t player, uint32_t entity, void *) {
    CHECK(player == 2 && entity == 5000);
    ++accepts;
}
static void test_components(KilnWorldApi &world) {
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
        programs[std::string(d->name.ptr, d->name.len)] = *d;
        *h = programs.size();
        return KILN_OK;
    };
    world.program_slot = [](KilnHandle, int32_t, KilnStr) -> int32_t { return 0; };
    world.queue_add = [](KilnProgramCtx *, uint32_t e, uint32_t h, const void *data, uint32_t n) -> KilnResult {
        auto p = static_cast<const uint8_t *>(data);
        components[{e, h}] = {p, p + n};
        return KILN_OK;
    };
    world.queue_remove = [](KilnProgramCtx *, uint32_t e, uint32_t h) -> KilnResult {
        components.erase({e, h});
        return KILN_OK;
    };
    components_on_ready();
    events_on_ready();
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
}

int main(int argc, char **argv) {
    if (argc != 2)
        return 2;
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
    static KilnUiApi ui{};
    init_ui(ui);
    S.ui = &ui;
    static KilnProgressionApi prog{};
    S.progression = &prog;
    fill_windows(&table);
    fill_level(&table);
    fill_components(&table);
    static KilnWorldApi world{};
    S.world = &world;
    test_components(world);
    test_level(prog);
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
    windows_shutdown();
    CHECK(live_images == 0);
    std::printf("glue_test: %d checks, %d failures\n", checks, failures);
    return failures ? 1 : 0;
}
