// SPDX-License-Identifier: MIT
// kiln.markers' surfaces (chapter 04 §4.5, §4.6; plan S5, S6): at each client Post the client's rows (local and received)
// are admitted per surface (select.h), their visibility events are made (visibility.h) and the admitted map rows are
// appended to the game's map list (map_list.h), and written again after every client SimStep (issue 159). Rows:
// markers.map and markers.map_text (here: a NativeTooltip row's text in its map entry, issue 160), markers.compass,
// markers.world and markers.nameplates (the drawn surfaces).
//
// Threads: everything here runs on the client world's thread (its Post, its lifecycle, the link after its SimStep), except
// the frame callback of the drawn surfaces, which only reads an immutable snapshot.
#include <windows.h>

#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <array>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <tuple>
#include <vector>

#include "kiln_hook.hpp"
#include "services/kiln_render.h"
#include "services/kiln_ui.h"

#include "compass.h"
#include "glue.h"
#include "map_list.h"
#include "select.h"
#include "visibility.h"

namespace kmk {

namespace {

constexpr char kMap[] = "markers.map";
constexpr char kCompass[] = "markers.compass";
constexpr char kWorld[] = "markers.world";
constexpr char kNameplates[] = "markers.nameplates";
constexpr char kMapText[] = "markers.map_text";

KilnStr str(const char* s) { return KilnStr{s, std::strlen(s)}; }

// ---- the native map ---------------------------------------------------------------------------------------------------

MapLayout g_layout;
bool g_map_guards = false;        // markers.client_map resolved and its layout is the measured one
std::string g_map_static_off;     // why not (the row's text)
uint32_t g_map_static_reason = KILN_CAP_REASON_SYMBOL_MISSING;
bool g_map_latched = false;       // a guard failed at run time: off until the game restarts
std::string g_map_latch_reason;
int g_map_reported = -1;          // the row's last reported state (KILN_CAP_*), -1 none
std::string g_map_reported_text;
uint64_t g_ui_epoch = 0;          // the client world epoch g_ui belongs to
uintptr_t g_ui = 0;               // that world's G38UiPlayData
Appended g_appended;
MapKeep g_keep;                   // the rows the last client Post wrote, written again after each client SimStep
// the tooltip text (issue 160): markers.map_tooltip_text resolved and the entry's text view is the measured one
bool g_text_guards = false;
std::string g_text_off = "needs the native map list's guards (markers.client_map)";
uint32_t g_text_reason = KILN_CAP_REASON_SYMBOL_MISSING;
int g_text_reported = -1;         // markers.map_text's last reported state, -1 none
std::string g_text_reported_text;

bool constant(const char* name, int64_t* out) { return g.api->constant && g.api->constant(str(name), out) == KILN_OK; }

void map_setup() {
    if (!g.api->feature || !g.api->feature(str("markers.client_map"))) {
        g_map_static_off = "the native map list's guards did not resolve on this game build (markers.client_map)";
        return;
    }
    if (!g.api->feature(str("singletons")) || !g.api->singleton) {
        g_map_static_off = "the client world's UI data cannot be found on this build (feature singletons)";
        return;
    }
    int64_t count = 0, cap_off = 0, cap = 0, size = 0, pos = 0, z = 0, type = 0, vis = 0;
    if (!constant("markers/main.count", &count) || !constant("markers/main.capacity_offset", &cap_off) || !constant("markers/list.capacity", &cap) ||
        !constant("markers/entry.size", &size) || !constant("markers/entry.position", &pos) || !constant("markers/entry.z", &z) ||
        !constant("markers/entry.type", &type) || !constant("markers/entry.visibility", &vis)) {
        g_map_static_off = "a constant of the native map list is missing (markers.client_map)";
        return;
    }
    g_layout.header = count - 8;
    g_layout.capacity = static_cast<uint64_t>(cap);
    g_layout.entry_size = static_cast<uint32_t>(size);
    g_layout.position = static_cast<uint32_t>(pos);
    g_layout.z = static_cast<uint32_t>(z);
    g_layout.type = static_cast<uint32_t>(type);
    g_layout.visibility = static_cast<uint32_t>(vis);
    // the measured header: data, count, capacity in a row; the entry as chapter 04 §2.3 traced it
    if (cap_off != count + 8 || !g_layout.valid() || z != pos + 16) {
        g_map_static_off = "the native map list's layout differs from the measured one (markers.client_map)";
        g_map_static_reason = KILN_CAP_REASON_FAULTED;
        return;
    }
    g_map_guards = true;
}

// ---- the tooltip text (GitHub issue 160) ------------------------------------------------------------------------------
// MapLabel.NativeTooltip: a row's text goes into its map entry's text view (+0x50, +0x58, +0x60), which the game's name
// resolver (client 0xd3d350) takes before anything else; the crosshair window's tooltip starts from that name. Kiln calls
// and hooks nothing for it: the feature's symbols are guards, and the view holds the record's bytes in kiln.markers'
// arena, never moved or freed while the process lives (docs/re/map.md, "The tooltip's name"). Without the feature
// markers.map_text stays off, Set and Send refuse NativeTooltip as before, and kiln.log says so once.
void text_setup() {
    if (!g_map_guards) return;  // markers.map is off for good: the map row says why, and this row needs it
    int64_t label = 0, len = 0, hash = 0;
    if (!g.api->feature(str("markers.map_tooltip_text")) || !constant("markers/entry.label", &label) ||
        !constant("markers/entry.label_len", &len) || !constant("markers/entry.label_hash", &hash)) {
        g_text_off = "the game's map tooltip cannot be found on this build (markers.map_tooltip_text)";
        logf(KILN_LOG_WARN, "markers: the game's map tooltip cannot be found on this build (markers.map_tooltip_text): "
                            "MapLabel.NativeTooltip is refused (markers.map_text off), map icons keep the game's own names");
        return;
    }
    MapLayout l = g_layout;
    const bool in_entry = label > 0 && hash > 0 && label < g_layout.entry_size && hash < g_layout.entry_size;
    if (in_entry) {
        l.label = static_cast<uint32_t>(label);
        l.label_hash = static_cast<uint32_t>(hash);
    }
    if (!in_entry || len != label + 8 || !l.has_label()) {
        g_text_off = "the map entry's text view differs from the measured one (markers.map_tooltip_text)";
        g_text_reason = KILN_CAP_REASON_FAULTED;
        logf(KILN_LOG_WARN, "markers: %s: MapLabel.NativeTooltip is refused (markers.map_text off)", g_text_off.c_str());
        return;
    }
    g_layout = l;
    g_text_guards = true;
}

// markers.map_text now: on exactly when markers.map is on and the guards above hold (map_list.h map_text_row).
void update_text_row(bool map_on) {
    const TextRow t = map_text_row(map_on, g_text_guards, g_text_reason, g_text_off.c_str());
    g.sw->map_text.store(t.state == KILN_CAP_AVAILABLE);
    if (static_cast<int>(t.state) == g_text_reported && g_text_reported_text == t.text) return;
    g_text_reported = static_cast<int>(t.state);
    g_text_reported_text = t.text;
    report(kMapText, t.state, t.reason, t.text);
}

// markers.map now: the guards, the run-time latch and the icon catalog (warming: available, Set answers
// CatalogNotReady; published and verified for this build: available; otherwise off with the catalog's reason).
void update_map_row() {
    int state = KILN_CAP_AVAILABLE;
    uint32_t reason = KILN_CAP_REASON_NONE;
    std::string text;
    if (!g_map_guards) {
        state = KILN_CAP_UNAVAILABLE, reason = g_map_static_reason, text = g_map_static_off;
    } else if (g_map_latched) {
        state = KILN_CAP_UNAVAILABLE, reason = KILN_CAP_REASON_FAULTED, text = g_map_latch_reason;
    } else if (g.catalog->get() && (!g.catalog->map_verified() || g.catalog->changed())) {
        state = KILN_CAP_UNAVAILABLE, reason = KILN_CAP_REASON_POLICY, text = g.catalog->map_reason();
    }
    // the text needs the map: it goes off before the map does and on after it
    const bool map_on = state == KILN_CAP_AVAILABLE;
    if (!map_on) update_text_row(false);
    g.sw->map.store(map_on);
    if (map_on) update_text_row(true);
    if (state == g_map_reported && text == g_map_reported_text) return;
    g_map_reported = state;
    g_map_reported_text = text;
    report(kMap, static_cast<uint32_t>(state), reason, text.c_str());
}

void latch_map(const char* why) {
    if (g_map_latched) return;
    g_map_latched = true;
    g_map_latch_reason = why;
    logf(KILN_LOG_WARN, "markers: %s: native map icons are off until the game restarts", why);
    update_map_row();
}

struct AppendCall {
    uint8_t* ui;
    const MapRow* rows;
    uint32_t n;
    uint32_t written;
    ListState state;
};

// The game's memory under a fault guard (no C++ object with a destructor in this frame).
bool guarded_read(uint8_t* ui, uint64_t* count, uint64_t* room, ListState* state) {
    __try {
        *state = read_list(ui, g_layout, count, room);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool guarded_append(AppendCall* c) {
    __try {
        c->written = append_rows(c->ui, g_layout, c->rows, c->n, &g_appended, &c->state);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// The client world's G38UiPlayData, looked up again at every use (review N1): a pointer kept for the whole world could
// outlive the object if the game ever reallocated it, and the list guards would then run on freed memory. When the
// epoch or the object changes, our own rows of the last step are no longer ours to recognise.
uint8_t* ui_data(uint64_t epoch) {
    const uintptr_t w = g.api->world ? g.api->world(KILN_WORLD_CLIENT) : 0;
    const uintptr_t ui = w ? g.api->singleton(w, str("G38UiPlayData")) : 0;
    if (g_ui_epoch != epoch || g_ui != ui) {
        g_ui_epoch = epoch;
        g_ui = ui;
        g_appended = Appended{};
    }
    return reinterpret_cast<uint8_t*>(g_ui);
}

// ---- the camera and the viewport (kiln.render, kiln.ui) ---------------------------------------------------------------

const KilnRenderApi* g_render = nullptr;
const KilnUiApi* g_uiapi = nullptr;
bool g_ui_status = false;  // kiln.ui's status() is in its table

void read_view(View* v) {
    if (g_render) {
        KilnRenderCamera c{};
        c.size = sizeof c;
        double fwd[3];
        if (g_render->camera(&c) == KILN_OK && c.valid && forward_of(c.rotation, fwd) && std::isfinite(c.position[0]) &&
            std::isfinite(c.position[1]) && std::isfinite(c.position[2])) {
            v->camera = true;
            for (int i = 0; i < 3; ++i) v->cam[i] = c.position[i], v->fwd[i] = fwd[i];
            v->near_plane = c.near_plane > 0 && std::isfinite(c.near_plane) ? c.near_plane : 0.1;
        }
    }
    if (g_uiapi && g_ui_status) {
        KilnUiStatus s{};
        s.size = sizeof s;
        if (g_uiapi->status(&s) == KILN_OK) v->viewport = s.width > 0 && s.height > 0;
    }
}

// ---- admission and visibility -----------------------------------------------------------------------------------------

Visibility g_vis;
std::vector<ClientRow> g_rows;
std::vector<Candidate> g_cand;
std::vector<Admitted> g_adm;
std::vector<MapRow> g_maprows;

void candidates(const std::vector<ClientRow>& rows, std::vector<Candidate>* out) {
    out->resize(rows.size());
    const double now_ms = static_cast<double>(
        std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count());
    for (size_t i = 0; i < rows.size(); ++i) {
        const ClientRow& r = rows[i];
        Candidate& c = (*out)[i];
        c = Candidate{};
        c.requested = r.rec.surfaces;
        c.source_rank = r.source == KILN_MARKER_LOCAL ? 0 : 1;
        c.priority = r.rec.priority;
        c.ns = r.ns;
        c.key = r.key;
        c.publisher_generation = r.publisher_generation;
        c.height = r.rec.height;
        c.max_distance = r.rec.max_distance;
        c.x = r.rec.x, c.y = r.rec.y, c.z = r.rec.z;
        c.source_state = r.source_reason;
        if (r.source != KILN_MARKER_LOCAL && r.followed) {
            // a received followed row: from its previous pose to its latest over 100 ms after the latest arrived (§4.4 POSE)
            const double from[3] = {r.pose_prev.x, r.pose_prev.y, r.pose_prev.z}, to[3] = {r.pose_last.x, r.pose_last.y, r.pose_last.z};
            double p[3];
            interpolate(from, to, now_ms - static_cast<double>(r.pose_last_ms), p);
            c.x = p[0], c.y = p[1], c.z = p[2];
        }
        if (r.source == KILN_MARKER_LOCAL && r.rec.follow) {
            KilnVec3 p{};
            uint32_t why = KILN_MARKER_ANCHOR_ENDED;
            if (anchors_follow(r.rec.follow, &p, &why)) {
                c.source_state = why;
                if (why == KILN_MARKER_READY) c.x = p.x, c.y = p.y, c.z = p.z;
            } else {
                c.source_state = KILN_MARKER_ANCHOR_ENDED;
            }
        }
    }
}

void post_map(uint64_t epoch, const std::vector<uint32_t>& order) {
    if (!g.sw->map.load() || (order.empty() && !g_appended.n)) return;
    uint8_t* ui = ui_data(epoch);
    if (!ui) return;
    g_maprows.clear();
    const bool text_on = g.sw->map_text.load();  // issue 160: a NativeTooltip row's text (the arena's bytes) in its entry
    for (uint32_t i : order) {
        const Record& rec = g_rows[i].rec;
        g_maprows.push_back(map_row(g_cand[i].x, g_cand[i].y, g_cand[i].z, rec.icon, rec.label_mode, rec.text, text_on));
    }
    AppendCall c{ui, g_maprows.data(), static_cast<uint32_t>(g_maprows.size()), 0, ListState::Ok};
    if (!guarded_append(&c)) return latch_map("writing the native map list faulted");
    if (c.state != ListState::Ok) return latch_map("the native map list's header failed its guards (data pointer, capacity or count)");
    g_keep.set(epoch, g_maprows.data(), static_cast<uint32_t>(g_maprows.size()));
}

// ---- the end of every client step (GitHub issue 159) ------------------------------------------------------------------
// The game clears and refills the map list at every client SimStep, once per frame on the main thread: the client world
// update (client 0xa1fae0) calls SimStep with ticked 0 on frames without a new step, and SimStep runs StepPrograms either
// way (clear_map_markers_for_ui, then replicate_map_markers_for_ui). The core's Post runs only at the ticked SimSteps
// (60 Hz), so above 60 fps the map drew the frames in between from a list without Kiln's rows. A link around SimStep
// writes the last Post's rows again once every client SimStep has returned: StepPrograms has waited for the step's jobs
// (replicate's chunks among them) by then, and the UI frame that reads the list runs outside SimStep on the same thread.
// docs/re/map.md, "The list in a frame". Feature markers.client_map_steps; without it the rows are written at the Post
// only, as before, and kiln.log says so once.

kiln::RawHook g_step_hook;
int64_t g_world_storage = 0;  // world -> its storage, SimStep's first argument
int64_t g_storage_kind = 0;   // storage -> its kind byte (KILN_WORLD_*)

// The kind byte of the storage SimStep was called with (the game's live argument), or 0xff if it cannot be read.
uint8_t storage_kind(uintptr_t storage) {
    __try {
        return *reinterpret_cast<const uint8_t*>(storage + static_cast<uintptr_t>(g_storage_kind));
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0xff;
    }
}

struct RewriteCall {
    uint8_t* ui;
    uint64_t epoch;
    uint32_t written;
    ListState state;
};

bool guarded_rewrite(RewriteCall* c) {
    __try {
        c->written = rewrite_kept(c->ui, g_layout, g_keep, c->epoch, &g_appended, &c->state);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// After a SimStep returned. Only the client world's, on its own thread, touches the kept rows (the server world of a
// hosted game steps on its GameServer thread; the prediction world's kind is 1). A ticked step without our Post drops them
// (MapKeep::end_step): they never outlive the Post that would move or remove them.
void step_end(uintptr_t storage, bool ticked) {
    if (storage_kind(storage) != KILN_WORLD_CLIENT || !g.api->on_world_thread(KILN_WORLD_CLIENT)) return;
    if (!g_keep.end_step(ticked) || !g.sw->map.load()) return;
    const uintptr_t w = g.api->world(KILN_WORLD_CLIENT);
    if (!w || storage != w + static_cast<uintptr_t>(g_world_storage)) return;
    uint64_t epoch = 0;
    g.store->world_epoch(KILN_WORLD_CLIENT, &epoch);
    if (!epoch || epoch != g_keep.epoch) return;
    uint8_t* ui = ui_data(epoch);
    if (!ui) return;
    RewriteCall c{ui, epoch, 0, ListState::Ok};
    if (!guarded_rewrite(&c)) return latch_map("writing the native map list faulted");
    if (c.state != ListState::Ok) return latch_map("the native map list's header failed its guards (data pointer, capacity or count)");
}

// SimStep(Storage*, bool ticked): the rest of the chain (the core's Pre, the game's step, the core's Post), then step_end.
// ticked is the bool's byte, as the core's link reads it.
uintptr_t hk_sim_step(uintptr_t storage, uintptr_t ticked, uintptr_t r8, uintptr_t r9) {
    const uintptr_t result = g_step_hook.call<uintptr_t>(storage, ticked, r8, r9);
    try {
        step_end(storage, (ticked & 0xff) != 0);
    } catch (...) {  // never into the game; a fault in the list itself latches the map (step_end)
    }
    return result;
}

// surfaces_ready, once the map list's guards hold and the client Post is ours (no Post, no rows to keep): the link before
// the core's own SimStep link, so it runs after the Post.
void steps_setup() {
    if (!g_map_guards || !g.client_post) return;
    uintptr_t sim = 0;
    if (!g.api->feature(str("markers.client_map_steps")) || g.api->symbol(str("SimStep"), &sim) != KILN_OK || !sim ||
        !constant("world.storage", &g_world_storage) || g_world_storage <= 0 || !constant("storage.kind", &g_storage_kind) ||
        g_storage_kind < 0) {
        logf(KILN_LOG_WARN, "markers: the end of each client step cannot be found on this build (markers.client_map_steps): map "
                            "icons are written at the client's 60 Hz steps only and can flicker at a higher frame rate");
        return;
    }
    if (!g_step_hook.install(g.api, g.owner, sim, reinterpret_cast<void*>(&hk_sim_step), "markers/SimStep", 0, "kiln")) {
        logf(KILN_LOG_WARN, "markers: the link after each client step could not be installed (%s): map icons are written at the "
                            "client's 60 Hz steps only and can flicker at a higher frame rate", g_step_hook.error());
        return;
    }
    logf(KILN_LOG_INFO, "markers: map icons are written again after every client step (markers.client_map_steps)");
}

// ---- the drawn surfaces: world labels and the compass strip (S6) -------------------------------------------------------
// One process-lifetime frame callback reads an immutable snapshot the client Post publishes; it makes no game read and
// calls no author code. Frames are leased from kiln.render only while the snapshot has something to draw.

struct Drawable {
    double from[3] = {0, 0, 0}, to[3] = {0, 0, 0};  // smoothed over 100 ms from the drawn position to the newest
    float height = 0;
    uint32_t rgba = 0xffffffffu;
    std::string text;
};
struct FrameSnap {
    std::chrono::steady_clock::time_point at;
    std::vector<Drawable> world, compass;  // best first
};

std::mutex g_frame_mu;  // guards the pointer only; the snapshot itself is immutable
std::shared_ptr<const FrameSnap> g_frame;
bool g_drawn_ok = false;     // kiln.render's camera, kiln.ui's frame callback and screen state
std::string g_drawn_off = "not set up";
uint32_t g_drawn_reason = KILN_CAP_REASON_SERVICE_MISSING;
bool g_screen_ok = false;
KilnHandle g_lease = 0;      // kiln.render acquire_frames while there is something to draw
int g_nameplates_reported = -1;
using RowKey = std::tuple<uint32_t, uint64_t, std::string, std::string>;
struct Segment {
    double from[3], to[3];
};
std::map<RowKey, Segment> g_drawn_at;  // each drawn row's segment in the last snapshot

void publish_frame(std::shared_ptr<const FrameSnap> s) {
    std::lock_guard l(g_frame_mu);
    g_frame = std::move(s);
}

std::shared_ptr<const FrameSnap> frame_snapshot() {
    std::lock_guard l(g_frame_mu);
    return g_frame;
}

void set_lease(bool want) {
    if (!g_render) return;
    if (want && !g_lease) {
        KilnHandle h = 0;
        if (g_render->acquire_frames(g.owner, &h) == KILN_OK) g_lease = h;
    } else if (!want && g_lease) {
        g_render->release(g_lease);
        g_lease = 0;
    }
}

void diamond(float x, float y, float r, uint32_t rgba) {
    const float xy[8] = {x, y - r, x + r, y, x, y + r, x - r, y};
    g_render->polyline(xy, 4, rgba, 1.0f, 1, 1);
    g_render->polyline(xy, 4, 0x000000c0u, 1.0f, 1, 0);
}

// Inside kiln.ui's frame: only on the play screen (in game or typing in chat), the Kiln menu closed, a fresh camera.
void KILN_CALL on_frame(const KilnUiFrame* f, void*) {
    if (!f || f->menu_open || !g_render || !g_screen_ok) return;
    const auto s = frame_snapshot();
    if (!s || (s->world.empty() && s->compass.empty())) return;
    KilnUiScreen sc{};
    sc.size = sizeof sc;
    if (g_uiapi->screen(&sc) != KILN_OK || !sc.available || sc.ui_state != KILN_UI_STATE_PLAY ||
        (sc.hud_screen != KILN_UI_HUD_INGAME && sc.hud_screen != KILN_UI_HUD_TEXT_CHAT))
        return;
    KilnRenderCamera c{};
    c.size = sizeof c;
    double fwd[3];
    if (g_render->camera(&c) != KILN_OK || !c.valid || !forward_of(c.rotation, fwd) || !(f->width > 0) || !(f->height > 0)) return;
    static double s_heading = 0;  // the frame callback's own: the last valid heading (initially north)
    s_heading = heading_of(fwd, s_heading);
    const float scale = f->dpi_scale > 0 && std::isfinite(f->dpi_scale) ? f->dpi_scale : 1.0f;
    const double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - s->at).count();
    char line[192];
    for (const Drawable& d : s->world) {
        double p[3];
        interpolate(d.from, d.to, ms, p);
        p[1] += d.height;
        float out[3];
        if (!g_render->world_to_screen(p, f->width, f->height, out)) continue;  // behind the near plane
        diamond(out[0], out[1], 5 * scale, d.rgba);
        if (!d.text.empty())
            g_render->text(out[0], out[1] - 24 * scale, d.rgba, 14 * scale, KILN_RENDER_TEXT_CENTER_X | KILN_RENDER_TEXT_OUTLINE,
                           KilnStr{d.text.data(), d.text.size()});
    }
    if (!s->compass.empty()) {
        const Strip st = strip_of(f->width, scale);
        g_render->line(st.cx - st.width / 2, st.y, st.cx + st.width / 2, st.y, 0xffffff60u, 1.0f * scale);
        for (const Drawable& d : s->compass) {
            double p[3];
            interpolate(d.from, d.to, ms, p);
            const float x = strip_x(st, s_heading, bearing_to(c.position, p[0], p[2]));
            diamond(x, st.y, 8 * scale, d.rgba);
            const double dx = p[0] - c.position[0], dy = p[1] - c.position[1], dz = p[2] - c.position[2];
            const int n = std::snprintf(line, sizeof line, "%.*s %.0fm", static_cast<int>(d.text.size() > 128 ? 128 : d.text.size()),
                                        d.text.data(), std::sqrt(dx * dx + dy * dy + dz * dz));
            if (n > 0)
                g_render->text(x, st.y + 12 * scale, d.rgba, 12 * scale, KILN_RENDER_TEXT_CENTER_X | KILN_RENDER_TEXT_OUTLINE,
                               KilnStr{line, static_cast<size_t>(n < static_cast<int>(sizeof line) ? n : sizeof line - 1)});
        }
    }
}

void drawn_setup() {
    if (!g_render) {
        g_drawn_off = "needs kiln.render";
        return;
    }
    if (!g.api->feature(str("render.camera"))) {
        g_drawn_off = "needs kiln.render's camera (render.camera) on this build";
        g_drawn_reason = KILN_CAP_REASON_SYMBOL_MISSING;
        return;
    }
    const bool frames = g_uiapi && g_uiapi->header.size >= offsetof(KilnUiApi, subscribe_frame) + sizeof(g_uiapi->subscribe_frame) && g_uiapi->subscribe_frame;
    g_screen_ok = g_uiapi && g_uiapi->header.size >= offsetof(KilnUiApi, screen) + sizeof(g_uiapi->screen) && g_uiapi->screen &&
                  g.api->feature(str("ui.screens"));
    if (!frames || !g_screen_ok) {
        g_drawn_off = !frames ? "needs kiln.ui's frame callback" : "needs kiln.ui's screen state (ui.screens): labels are drawn only on the play screen";
        g_drawn_reason = !frames ? KILN_CAP_REASON_SERVICE_MISSING : KILN_CAP_REASON_SYMBOL_MISSING;
        return;
    }
    KilnHandle h = 0;
    if (g_uiapi->subscribe_frame(g.owner, &on_frame, nullptr, &h) != KILN_OK) {
        g_drawn_off = "kiln.ui refused the frame callback";
        g_drawn_reason = KILN_CAP_REASON_FAULTED;
        return;
    }
    g_drawn_ok = true;
}

void update_nameplates_row() {
    const bool on = g_drawn_ok && g.sw->anchors_client.load();
    if (static_cast<int>(on) == g_nameplates_reported) return;
    g_nameplates_reported = on;
    if (on)
        report(kNameplates, KILN_CAP_AVAILABLE, KILN_CAP_REASON_NONE, "");
    else
        report(kNameplates, KILN_CAP_UNAVAILABLE, g_drawn_ok ? KILN_CAP_REASON_SERVICE_MISSING : g_drawn_reason,
               g_drawn_ok ? "needs markers.world and markers.anchors.client (following an entity of the client world)" : g_drawn_off.c_str());
}

void post_frame(const std::vector<uint32_t>& world, const std::vector<uint32_t>& compass) {
    if (!g_drawn_ok) return;
    const auto prev = frame_snapshot();
    const auto now = std::chrono::steady_clock::now();
    const double ms = prev ? std::chrono::duration<double, std::milli>(now - prev->at).count() : 0;
    std::map<RowKey, Segment> drawn;
    auto make = [&](uint32_t i) {
        const ClientRow& r = g_rows[i];
        Drawable d;
        d.to[0] = g_cand[i].x, d.to[1] = g_cand[i].y, d.to[2] = g_cand[i].z;
        RowKey key{r.source, r.publisher_generation, r.ns, r.key};
        // a row drawn before starts where it is drawn right now, so a moving one goes on smoothly; a new one at its point
        if (auto it = g_drawn_at.find(key); it != g_drawn_at.end())
            interpolate(it->second.from, it->second.to, ms, d.from);
        else
            for (int k = 0; k < 3; ++k) d.from[k] = d.to[k];
        d.height = r.rec.height;
        d.rgba = r.rec.rgba;
        d.text.assign(r.rec.text.data(), r.rec.text.size());
        Segment seg;
        for (int k = 0; k < 3; ++k) seg.from[k] = d.from[k], seg.to[k] = d.to[k];
        drawn[key] = seg;
        return d;
    };
    auto s = std::make_shared<FrameSnap>();
    s->at = now;
    for (uint32_t i : world) s->world.push_back(make(i));
    for (uint32_t i : compass) s->compass.push_back(make(i));
    g_drawn_at.swap(drawn);
    const bool any = !s->world.empty() || !s->compass.empty();
    publish_frame(std::move(s));
    set_lease(any);
}

}  // namespace

void surfaces_ready() {
    if (g.side != KILN_SIDE_CLIENT) {
        static const char kSide[] = "drawn and native map surfaces are a game client's; a server sends markers (markers.send)";
        for (const char* name : {kMap, kCompass, kWorld, kNameplates}) report(name, KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_WRONG_SIDE, kSide);
        return;
    }
    g_render = static_cast<const KilnRenderApi*>(g.api->service(str(KILN_RENDER_SERVICE), KILN_RENDER_VERSION));
    if (g_render && g_render->header.size < sizeof(KilnRenderApi)) g_render = nullptr;
    g_uiapi = static_cast<const KilnUiApi*>(g.api->service(str(KILN_UI_SERVICE), KILN_UI_VERSION));
    g_ui_status = g_uiapi && g_uiapi->header.size >= offsetof(KilnUiApi, status) + sizeof(g_uiapi->status) && g_uiapi->status;
    map_setup();
    text_setup();
    update_map_row();
    steps_setup();
    drawn_setup();
    for (const char* name : {kCompass, kWorld}) {
        if (g_drawn_ok)
            report(name, KILN_CAP_AVAILABLE, KILN_CAP_REASON_NONE, "");
        else
            report(name, KILN_CAP_UNAVAILABLE, g_drawn_reason, g_drawn_off.c_str());
    }
    g.sw->compass.store(g_drawn_ok);
    g.sw->world.store(g_drawn_ok);
    update_nameplates_row();
}

void surfaces_world(const KilnWorldEvent& e, std::vector<Note>* notes) {
    if (g.side != KILN_SIDE_CLIENT || e.kind != KILN_WORLD_CLIENT) return;
    if (e.event == KILN_WORLD_STOPPING) {
        uint64_t epoch = 0;
        g.store->world_epoch(KILN_WORLD_CLIENT, &epoch);
        g_vis.clear(epoch, notes);
        g_keep.clear();
        g_appended = Appended{};
        g_ui = 0;
        g_ui_epoch = 0;
        // the frame callback draws nothing from here; the lease goes
        publish_frame(nullptr);
        g_drawn_at.clear();
        set_lease(false);
    }
}

void surfaces_client_post(std::vector<Note>* notes) {
    if (g.side != KILN_SIDE_CLIENT) return;
    g_keep.begin_post();  // post_map keeps what it writes at this Post
    uint64_t epoch = 0;
    g.store->world_epoch(KILN_WORLD_CLIENT, &epoch);
    update_map_row();
    update_nameplates_row();
    g.store->snapshot_client(&g_rows);
    if (g_rows.empty() && !g_vis.size() && !g_appended.n) {
        if (g_lease) {
            publish_frame(nullptr);
            g_drawn_at.clear();
            set_lease(false);
        }
        return;
    }
    View v;
    v.world_active = epoch != 0;
    v.cap[kSurfMap] = g.sw->map.load();
    v.cap[kSurfCompass] = g.sw->compass.load();
    v.cap[kSurfWorld] = g.sw->world.load();
    read_view(&v);
    if (v.cap[kSurfMap] && epoch) {
        if (uint8_t* ui = ui_data(epoch)) {
            uint64_t count = 0, room = 0;
            ListState st = ListState::Ok;
            if (!guarded_read(ui, &count, &room, &st)) {
                latch_map("reading the native map list faulted");
            } else if (st != ListState::Ok) {
                latch_map("the native map list's header failed its guards (data pointer, capacity or count)");
            } else {
                // our own rows of the last step, when the game did not clear them, are room again
                if (g_appended.n && count == g_appended.base + g_appended.n) room += g_appended.n;
                v.map_room = static_cast<uint32_t>(room);
            }
            v.cap[kSurfMap] = g.sw->map.load();
        }
    }
    candidates(g_rows, &g_cand);
    std::vector<uint32_t> order[3];
    admit(v, g_cand, &g_adm, order);
    g_vis.step(epoch, g_rows, g_adm, notes);
    post_map(epoch, order[kSurfMap]);
    post_frame(order[kSurfWorld], order[kSurfCompass]);
}

}  // namespace kmk
