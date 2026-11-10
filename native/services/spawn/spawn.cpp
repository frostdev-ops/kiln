// SPDX-License-Identifier: MIT
// kiln.spawn: create/destroy server-world entities from templates and track what Kiln spawned per world.
// Engine evidence: docs/re/spawn.md. Usage: docs/kiln/services/spawn.md. Table: native/core/abi/services/kiln_spawn.h.
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cctype>
#include <chrono>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <ctime>
#include <fstream>
#include <map>
#include <set>
#include <sstream>

#include "catalog.h"
#include "game.h"
#include "kiln_api.h"
#include "nlohmann/json.hpp"
#include "services/kiln_saves.h"
#include "services/kiln_spawn.h"
#include "tracker.h"

using namespace kspawn;

// The managed mirror (managed/services/Kiln.Spawn/Spawn.cs) relies on these sizes.
static_assert(sizeof(KilnSpawnRequest) == 120 && sizeof(KilnSpawnComponent) == 24 &&
              offsetof(KilnSpawnRequest, components) == KILN_SPAWN_REQUEST_V1_SIZE && sizeof(KilnTemplateInfo) == 224 && sizeof(KilnTrackedEntity) == 352 &&
              sizeof(KilnEntitySnapshot) == 96 && sizeof(KilnSpawnCleanup) == 16 && sizeof(KilnGuid) == 16);

namespace {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
bool g_ok = false;  // init succeeded: the table's functions work

constexpr const char* kPickupName = "Base_LootPickup_Generic_Gravity";
constexpr const char* kPickupGuid = "a56d2777-daf1-4164-90d4-b7b12749c723";  // RVA 0xbe50f0, used by spawnPickupItem

// ---- logging ----
void logv(int level, const char* fmt, va_list ap) {
    char buf[1024];
    int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    if (n < 0) return;
    g_api->log(g_owner, level, KilnStr{buf, static_cast<size_t>(std::min(n, static_cast<int>(sizeof buf) - 1))});
}
void logi(const char* fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    logv(KILN_LOG_INFO, fmt, ap);
    va_end(ap);
}
void logw(const char* fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    logv(KILN_LOG_WARN, fmt, ap);
    va_end(ap);
}

// C++ exceptions must not cross the C boundary: every table entry and tick callback runs through this.
template <class F>
KilnResult guarded(const char* what, F&& f) {
    try {
        return f();
    } catch (const std::exception& e) {
        logw("kiln.spawn: %s failed: %s", what, e.what());
    } catch (...) {
        logw("kiln.spawn: %s failed", what);
    }
    return KILN_ERR_INTERNAL;
}

std::string_view sv(KilnStr s) { return s.ptr ? std::string_view(s.ptr, s.len) : std::string_view(); }
std::string lower(std::string_view s) {
    std::string r(s);
    for (char& c : r) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return r;
}
std::string clip(std::string_view s, size_t n) { return std::string(s.substr(0, n)); }
void copy_c(char* dst, size_t cap, const std::string& s) {
    const size_t n = std::min(s.size(), cap - 1);
    std::memcpy(dst, s.data(), n);
    dst[n] = 0;
}

bool on_tick() { return g_api->on_tick_thread(); }

// The server steps its world while it is still loading the save ("[game_server] Switching state from Load to
// LoadEcsScene", then "... to Run"); entities from the save exist only after that, and the loader creates entities
// itself meanwhile. The game log line sets g_world_running (and triggers an immediate re-find of tracked records).
std::atomic<bool> g_world_running{false}, g_run_pending{false}, g_have_log{false};
std::atomic<uint64_t> g_world_steps{0};
uint32_t world_state() {
    if (!game::world_ready()) return KILN_SPAWN_WORLD_NONE;
    if (g_world_running.load()) return KILN_SPAWN_WORLD_RUNNING;
    // Without the game-log mirror (or if this build words the line differently): assume running after 90 s / 4 min
    // of steps (24.5 per second). The save loads in about 11 s on the lab server.
    const uint64_t steps = g_world_steps.load();
    return steps > (g_have_log.load() ? 5900u : 2200u) ? KILN_SPAWN_WORLD_RUNNING : KILN_SPAWN_WORLD_LOADING;
}
double now_s() {
    using namespace std::chrono;
    return duration<double>(steady_clock::now().time_since_epoch()).count();
}

bool finite_pos(const KilnVec3& p) {
    return std::isfinite(p.x) && std::isfinite(p.y) && std::isfinite(p.z) && std::fabs(p.x) < 1e6 &&
           std::fabs(p.y) < 1e6 && std::fabs(p.z) < 1e6;
}

void to_m(const WorldTransform& t, double out[3]) {
    for (int i = 0; i < 3; ++i) out[i] = fixed_to_m(t.pos[i]);
}
double dist2(const double a[3], const double b[3]) {
    const double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
    return dx * dx + dy * dy + dz * dz;
}

// ---- world key + sidecar ----
std::string path_of(int kind) {
    char buf[1024];
    size_t n = 0;
    if (g_api->path(kind, buf, sizeof buf, &n) != KILN_OK) return {};
    return std::string(buf, n);
}

std::filesystem::path u8p(const std::string& s) {
    return std::filesystem::path(std::u8string(reinterpret_cast<const char8_t*>(s.data()), s.size()));
}

std::string u8s(const std::filesystem::path& p) {
    const std::u8string s = p.u8string();
    return std::string(reinterpret_cast<const char*>(s.data()), s.size());
}

// World GUID from the newest world*.ksd in the server's save directory: "SV2C" header, GUID at +0x0C (16 bytes,
// stable for a world; +0x1C is the save revision). See docs/re/spawn.md.
bool world_guid(std::string& key, std::string& save_file) {
    const std::filesystem::path game = u8p(path_of(KILN_PATH_GAME));
    std::filesystem::path dir = game / "savegame";
    try {
        std::ifstream cf(game / "enshrouded_server.json", std::ios::binary);
        if (cf) {
            std::stringstream ss;
            ss << cf.rdbuf();
            const auto cfg = nlohmann::json::parse(ss.str(), nullptr, false);
            if (cfg.is_object() && cfg.contains("saveDirectory") && cfg["saveDirectory"].is_string()) {
                std::filesystem::path d = u8p(cfg["saveDirectory"].get<std::string>());
                dir = d.is_absolute() ? d : game / d;
            }
        }
    } catch (...) {
    }
    std::error_code ec;
    std::filesystem::path best;
    std::filesystem::file_time_type best_t{};
    for (auto& e : std::filesystem::directory_iterator(dir, ec)) {
        const std::wstring n = e.path().filename().wstring();
        if (n.rfind(L"world", 0) != 0 || e.path().extension() != L".ksd") continue;
        const auto t = e.last_write_time(ec);
        if (best.empty() || t > best_t) best = e.path(), best_t = t;
    }
    if (best.empty()) return false;
    std::ifstream f(best, std::ios::binary);
    uint8_t h[0x20]{};
    if (!f.read(reinterpret_cast<char*>(h), sizeof h)) return false;
    if (std::memcmp(h, "SV2C", 4) != 0) return false;
    Guid g;
    std::memcpy(g.b, h + 0x0C, 16);
    if (g.zero()) return false;
    key = g.text();
    save_file = u8s(best.lexically_normal());
    return true;
}

// The running world's GUID from kiln.saves (the game's memory, else the world file it loaded). A game client hosts
// worlds from Steam cloud or local slots, not from a server save folder: this is how it gets its world key.
bool saves_world_guid(std::string& key, std::string& save_file) {
    static const KilnSavesApi* t = nullptr;
    if (!t) t = static_cast<const KilnSavesApi*>(g_api->service(KilnStr{KILN_SAVES_SERVICE, sizeof(KILN_SAVES_SERVICE) - 1}, 1));
    if (!t) return false;
    KilnSavesStatus st{};
    st.size = sizeof st;
    if (t->status(&st) != KILN_OK || !st.has_guid) return false;
    Guid g;
    std::memcpy(g.b, st.world_guid, 16);
    if (g.zero()) return false;
    key = g.text();
    char buf[1024];
    size_t n = 0;
    save_file = t->path(KILN_SAVES_PATH_WORLD_FILE, buf, sizeof buf, &n) == KILN_OK ? std::string(buf, n) : "";
    return true;
}

bool g_client = false;

bool world_identity(std::string& key, std::string& save) {
    if (!g_client && world_guid(key, save)) return true;  // dedicated server: its save folder, as before
    return saves_world_guid(key, save);
}

void open_tracker() {
    std::string key, save;
    if (!world_identity(key, save)) {
        key = "unknown-world";
        save = "";
        // expected for a new world (and a client's hosted world before its first save): the key follows at that save
        logi("kiln.spawn: no world save found yet; tracking under '%s' (the key changes once the world is saved)",
             key.c_str());
    }
    const std::filesystem::path file = u8p(path_of(KILN_PATH_DATA)) / "spawn" / (key + ".json");
    tracker::open(file, key, save);
    logi("kiln.spawn: world %s, sidecar %s (%zu tracked records)", key.c_str(), u8s(file).c_str(), tracker::count());
}

// ---- templates ----
double g_templates_at = 0;

size_t load_templates() {
    std::vector<TemplateRow> rows;
    if (!game::templates(rows) || rows.empty()) return 0;
    const size_t n = catalog::merge_engine(rows);
    g_templates_at = now_s();
    return n;
}

// Templates registered at run time (kiln.gamedata clones) reach the engine's list after the catalog last read it. Reading it
// costs some milliseconds, so a name that is nothing doesn't read it every time.
void refresh_templates() {
    if (on_tick() && game::world_ready() && now_s() - g_templates_at > 5) load_templates();
}

// ---- spawn core ----
struct Resolved {
    Guid guid;
    std::string name;
    uint32_t kind = 0;
};

KilnResult resolve_template(const KilnGuid* gp, KilnStr name, uint32_t flags, Resolved* out) {
    catalog::Entry e;
    if (gp) {
        std::memcpy(out->guid.b, gp->b, 16);
        if (catalog::get(out->guid, &e)) {
            out->name = e.name;
            out->kind = e.kind;
        }
        if (catalog::engine_loaded()) {
            // templates registered at run time (kiln.gamedata clones) reach the engine's map later: re-read it
            if (!catalog::engine_has(out->guid)) {
                refresh_templates();
                if (catalog::get(out->guid, &e)) {
                    out->name = e.name;
                    out->kind = e.kind;
                }
            }
            if (!catalog::engine_has(out->guid)) return KILN_ERR_NOT_FOUND;
        } else if (out->name.empty() && !(flags & KILN_SPAWN_ALLOW_UNKNOWN)) {
            return KILN_ERR_NOT_FOUND;
        }
        return KILN_OK;
    }
    if (!catalog::find(sv(name), &e)) {
        // Names the shipped catalog lacks may be loaded by the engine since the last read.
        refresh_templates();
        if (!catalog::find(sv(name), &e)) return KILN_ERR_NOT_FOUND;
    }
    if (catalog::engine_loaded() && !catalog::engine_has(e.guid)) return KILN_ERR_NOT_FOUND;
    out->guid = e.guid;
    out->name = e.name;
    out->kind = e.kind;
    return KILN_OK;
}

// SpawnTime marker: ECS now (a multiple of the 16.67 ms tick period) plus 1..997 ns. Harmless to game logic and never
// produced by the game itself: within a session it proves an id still names the entity Kiln spawned. The save does
// not keep SpawnTime (docs/re/spawn.md), so after a restart records are matched by template and position instead.
int64_t spawn_marker(uint64_t serial) { return game::ecs_now() + 1 + static_cast<int64_t>(serial % 997); }

KilnResult do_spawn(const Resolved& t, const KilnVec3& p, float yaw_deg, int32_t level, uint32_t flags,
                    std::string_view tag, std::string_view origin, const PickupItem* pickup, KilnEntity* out,
                    const KilnSpawnComponent* extra = nullptr, uint32_t extra_count = 0) {
    if (out) *out = 0;
    if (!g_ok) return KILN_ERR_UNAVAILABLE;
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    if (world_state() != KILN_SPAWN_WORLD_RUNNING) return KILN_ERR_UNAVAILABLE;
    if (!finite_pos(p) || !std::isfinite(yaw_deg) || level < 0 || level > 1000) return KILN_ERR_INVALID_ARGUMENT;
    if (extra_count > KILN_SPAWN_MAX_COMPONENTS || (extra_count && !extra)) return KILN_ERR_INVALID_ARGUMENT;
    for (uint32_t i = 0; i < extra_count; ++i) {
        if (!extra[i].hash || !extra[i].data || extra[i].size == 0 || extra[i].size > (1u << 20))
            return KILN_ERR_INVALID_ARGUMENT;
        if (extra[i].hash == kSpawnTime && !(flags & KILN_SPAWN_NO_TRACK)) return KILN_ERR_INVALID_ARGUMENT;
        for (uint32_t j = 0; j < i; ++j)
            if (extra[j].hash == extra[i].hash) return KILN_ERR_INVALID_ARGUMENT;  // one value per component
    }
    if (!tracker::opened()) open_tracker();

    WorldTransform xf{};
    xf.pos[0] = m_to_fixed(p.x);
    xf.pos[1] = m_to_fixed(p.y);
    xf.pos[2] = m_to_fixed(p.z);
    const double half = static_cast<double>(yaw_deg) * 3.14159265358979323846 / 360.0;
    xf.rot[0] = 0;
    xf.rot[1] = static_cast<float>(std::sin(half));
    xf.rot[2] = 0;
    xf.rot[3] = static_cast<float>(std::cos(half));
    xf.scale[0] = xf.scale[1] = xf.scale[2] = 1.0f;

    const bool track = !(flags & KILN_SPAWN_NO_TRACK);
    const uint64_t serial = track ? tracker::next_serial() : 0;
    int64_t spawn_time = track ? spawn_marker(serial) : game::ecs_now();
    uint32_t lvl = static_cast<uint32_t>(level);
    uint8_t dns = 0;

    SpawnComponent comps[6 + KILN_SPAWN_MAX_COMPONENTS];
    size_t n = 0;
    comps[n++] = {kCurrentTransform, 0, &xf, sizeof xf};
    comps[n++] = {kSpawnTime, 0, &spawn_time, sizeof spawn_time};
    if (level > 0) comps[n++] = {kOverrideLevel, 0, &lvl, sizeof lvl};
    if (flags & KILN_SPAWN_TRANSIENT) comps[n++] = {kDoNotSave, 0, &dns, sizeof dns};
    if (pickup) comps[n++] = {kPickupItem, 0, pickup, sizeof *pickup};
    // Extra components (SpawnOptions.Components): one of Kiln's own hashes replaces Kiln's value, the rest append.
    for (uint32_t i = 0; i < extra_count; ++i) {
        SpawnComponent c{extra[i].hash, 0, extra[i].data, extra[i].size};
        size_t k = 0;
        while (k < n && comps[k].hash != c.hash) ++k;
        comps[k] = c;
        if (k == n) ++n;
    }

    unsigned long fault = 0;
    const uint32_t id = game::create(t.guid, comps, n, &fault);
    if (fault) {
        logw("kiln.spawn: spawning %s faulted (0x%08lx); refused", t.name.c_str(), fault);
        return KILN_ERR_INTERNAL;
    }
    if (!id) {
        logw("kiln.spawn: the engine refused to spawn %s (%s); see the game's log line above", t.name.c_str(),
             t.guid.text().c_str());
        return KILN_ERR_INTERNAL;
    }
    if (track) {
        tracker::Record r;
        r.serial = serial;
        r.entity = id;
        r.tmpl = t.guid;
        r.name = t.name;
        r.spawn_time = spawn_time;
        r.pos[0] = r.spawn_pos[0] = p.x;
        r.pos[1] = r.spawn_pos[1] = p.y;
        r.pos[2] = r.spawn_pos[2] = p.z;
        r.spawned_unix = static_cast<int64_t>(std::time(nullptr));
        r.flags = flags;
        r.tag = clip(tag, 63);
        r.origin = clip(origin, 63);
        r.live = true;
        tracker::add(std::move(r));
    }
    logi("kiln.spawn: spawned %s as entity %u at (%.1f, %.1f, %.1f)%s%s%s", t.name.empty() ? t.guid.text().c_str() : t.name.c_str(),
         id, p.x, p.y, p.z, track ? "" : " (untracked)", (flags & KILN_SPAWN_TRANSIENT) ? " (transient)" : "",
         extra_count ? " (+components)" : "");
    if (out) *out = id;
    return KILN_OK;
}

// ---- refresh: keep tracked records bound to live entities ----
double g_last_refresh = 0, g_last_full_scan = 0, g_last_pos_save = 0, g_session_start = 0;
bool g_pos_dirty = false;

bool spawn_time_of(uint32_t id, int64_t* out) {
    void* data = nullptr;
    uint64_t size = 0;
    if (!game::component(id, kSpawnTime, &data, &size) || size != 8 || !data) return false;
    __try {
        *out = *static_cast<const int64_t*>(data);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// Template flags from the catalog (KILN_TEMPLATE_FLAG_*), 0 if unknown.
uint32_t template_flags(const Guid& g) {
    catalog::Entry e;
    return catalog::get(g, &e) ? e.flags : 0;
}

// Rules (docs/kiln/services/spawn.md, "Tracking"):
//  - a live record whose entity disappears during the session is dropped: the game removed it (killed, collected,
//    despawned, culled) and the entity map is authoritative for the running world;
//  - after a restart records are re-found among entities of the same template that no other record holds: static
//    templates (props, pickups, other) at the exact saved position (0.3 m), creatures and villagers near the last
//    known position (8 m, nearest);
//  - records that cannot come back (TRANSIENT, or a template carrying DoNotSave) are dropped once the world runs;
//  - other missing records are kept (cleanup drops them); a record missing for 3 whole sessions is dropped.
// A candidate carrying a SpawnTime that is not this record's marker is not ours (the game's own value, or another
// record's marker); entities loaded from the save have no SpawnTime.
bool foreign_marker(uint32_t id, const tracker::Record& r) {
    int64_t st = 0;
    return spawn_time_of(id, &st) && st != r.spawn_time;
}

uint32_t refresh_impl(bool full_scan) {
    std::vector<tracker::Record> recs = tracker::snapshot();
    bool dirty = false;
    bool any_missing = false;
    std::set<uint32_t> held;
    std::vector<tracker::Record> kept;
    kept.reserve(recs.size());
    for (tracker::Record& r : recs) {
        if (!r.live) {
            any_missing = true;
            kept.push_back(std::move(r));
            continue;
        }
        // Existence through the engine's own lookup (GetEntityTransform); template and marker as identity checks.
        WorldTransform xf{};
        bool ok = game::transform(r.entity, &xf);
        if (ok && game::can_list_entities()) {
            Guid g;
            ok = game::entity_template(r.entity, &g) && g == r.tmpl;
        }
        int64_t st = 0;
        if (ok && r.spawn_time && spawn_time_of(r.entity, &st)) ok = st == r.spawn_time;
        if (!ok) {
            logi("kiln.spawn: tracked #%llu (%s, entity %u) is gone (removed by the game); record dropped",
                 static_cast<unsigned long long>(r.serial), r.name.c_str(), r.entity);
            dirty = true;
            continue;
        }
        double p[3];
        to_m(xf, p);
        if (dist2(p, r.pos) > 0.25) {
            std::memcpy(r.pos, p, sizeof p);
            g_pos_dirty = true;
        }
        held.insert(r.entity);
        kept.push_back(std::move(r));
    }
    recs = std::move(kept);
    const bool running = g_world_running.load() || world_state() == KILN_SPAWN_WORLD_RUNNING;
    if (any_missing && full_scan && running) {
        g_last_full_scan = now_s();
        std::vector<EntityRow> rows;
        if (game::entities(rows)) {
            std::map<Guid, std::vector<uint32_t>> by_tmpl;
            std::set<Guid> wanted;
            for (const tracker::Record& r : recs)
                if (!r.live) wanted.insert(r.tmpl);
            for (const EntityRow& e : rows)
                if (e.has_tmpl && wanted.count(e.tmpl) && !held.count(e.id) && e.id > 1023)
                    by_tmpl[e.tmpl].push_back(e.id);
            std::vector<tracker::Record> next;
            next.reserve(recs.size());
            for (tracker::Record& r : recs) {
                if (r.live) {
                    next.push_back(std::move(r));
                    continue;
                }
                uint32_t match = 0;
                const char* how = "";
                auto it = by_tmpl.find(r.tmpl);
                if (it != by_tmpl.end()) {
                    for (uint32_t id : it->second) {  // an entity still carrying our marker (same session)
                        int64_t st = 0;
                        if (r.spawn_time && !held.count(id) && spawn_time_of(id, &st) && st == r.spawn_time) {
                            match = id;
                            how = "spawn-time marker";
                            break;
                        }
                    }
                    if (!match) {
                        const uint32_t kind = catalog::kind_of(r.name);
                        const bool moves = kind == KILN_TEMPLATE_CREATURE || kind == KILN_TEMPLATE_NPC;
                        const double radius = moves ? 8.0 : 0.3;
                        double best = radius * radius;
                        for (uint32_t id : it->second) {
                            WorldTransform xf{};
                            if (held.count(id) || !game::transform(id, &xf) || foreign_marker(id, r)) continue;
                            double p[3];
                            to_m(xf, p);
                            const double d = dist2(p, r.pos);
                            if (d <= best) best = d, match = id, how = moves ? "template + nearest position" : "template + exact position";
                        }
                    }
                }
                if (match) {
                    r.entity = match;
                    r.live = true;
                    r.missed_sessions = 0;
                    held.insert(match);
                    WorldTransform xf{};
                    if (game::transform(match, &xf)) to_m(xf, r.pos);
                    dirty = true;
                    logi("kiln.spawn: re-found tracked #%llu (%s) as entity %u by %s",
                         static_cast<unsigned long long>(r.serial), r.name.c_str(), match, how);
                    next.push_back(std::move(r));
                    continue;
                }
                const uint32_t tf = template_flags(r.tmpl);
                if ((r.flags & KILN_SPAWN_TRANSIENT) || (tf & KILN_TEMPLATE_FLAG_DO_NOT_SAVE)) {
                    logi("kiln.spawn: tracked #%llu (%s) is not in the world: %s; record dropped",
                         static_cast<unsigned long long>(r.serial), r.name.c_str(),
                         (r.flags & KILN_SPAWN_TRANSIENT) ? "spawned transient (DoNotSave)" : "its template is never saved (DoNotSave)");
                    dirty = true;
                    continue;
                }
                if (!r.counted && now_s() - g_session_start > 600) {
                    r.counted = true;
                    ++r.missed_sessions;
                    dirty = true;
                    if (r.missed_sessions >= 3) {
                        logi("kiln.spawn: tracked #%llu (%s) was not found in 3 sessions; record dropped",
                             static_cast<unsigned long long>(r.serial), r.name.c_str());
                        continue;
                    }
                }
                next.push_back(std::move(r));
            }
            recs = std::move(next);
        }
    }
    uint32_t live = 0;
    for (const tracker::Record& r : recs) live += r.live ? 1 : 0;
    const bool pos_save = g_pos_dirty && now_s() - g_last_pos_save > 10;
    if (pos_save) {
        g_last_pos_save = now_s();
        g_pos_dirty = false;
    }
    tracker::replace(std::move(recs), dirty || pos_save);
    return live;
}

// ---- Pre maintenance ----

void KILN_CALL on_game_log(int32_t, KilnStr text, void*) {
    if (sv(text).find("Switching state from LoadEcsScene to Run") != std::string_view::npos) {
        g_world_running.store(true);
        g_run_pending.store(true);
    }
}

uint64_t g_steps = 0;
bool g_started = false;
double g_next_templates = 0;
int g_template_tries = 0;

void pre_impl() {
    ++g_steps;
    g_world_steps.store(g_steps);
    if (!game::world_ready()) return;
    const double t = now_s();
    if (!g_started) {
        g_started = true;
        g_session_start = t;
        open_tracker();
        std::vector<EntityRow> rows;
        const bool ent = game::entities(rows);
        size_t with_tmpl = 0;
        for (const EntityRow& e : rows) with_tmpl += e.has_tmpl ? 1 : 0;
        logi("kiln.spawn: entity map %s (%zu entities, %zu with a template; %s)", ent ? "readable" : "UNREADABLE",
             rows.size(), with_tmpl, game::entity_map_stats().c_str());
    }
    if (g_steps == 250 || g_steps == 2500) {
        const double t0 = now_s();
        std::vector<EntityRow> rows;
        game::entities(rows);
        logi("kiln.spawn: entity map at step %llu: %s; full scan %.2f ms", static_cast<unsigned long long>(g_steps),
             game::entity_map_stats().c_str(), (now_s() - t0) * 1000.0);
    }
    if (g_template_tries < 30 && t >= g_next_templates && !catalog::engine_loaded()) {
        ++g_template_tries;
        g_next_templates = t + 2;
        const size_t n = load_templates();
        if (n) logi("kiln.spawn: %zu engine templates loaded; catalog %zu entries", n, catalog::count());
        else if (g_template_tries == 30) logw("kiln.spawn: the engine's template list stayed unreadable; names come from the shipped catalog only");
    }
    if (g_run_pending.exchange(false)) {
        const uint32_t live = refresh_impl(true);
        logi("kiln.spawn: world is running (%s); %u of %zu tracked records live", game::entity_map_stats().c_str(), live,
             tracker::count());
        g_last_refresh = t;
        g_session_start = t;  // re-find early and often from here on
    }
    if (!g_world_running.load() && world_state() == KILN_SPAWN_WORLD_RUNNING) {
        g_world_running.store(true);
        g_run_pending.store(true);
        logw("kiln.spawn: no \"LoadEcsScene to Run\" line seen; assuming the world runs");
    }
    if (g_steps % 750 == 0 && tracker::world_key() == "unknown-world") {  // the world got its first save meanwhile?
        std::string key, save;
        if (world_identity(key, save)) {
            const std::filesystem::path file = u8p(path_of(KILN_PATH_DATA)) / "spawn" / (key + ".json");
            tracker::rekey(file, key, save);
            logi("kiln.spawn: world saved for the first time: sidecar is now %s", u8s(file).c_str());
        }
    }
    if (t - g_last_refresh >= 5) {
        g_last_refresh = t;
        const bool early = t - g_session_start < 600;
        const bool scan = early ? t - g_last_full_scan >= 10 : t - g_last_full_scan >= 120;
        refresh_impl(scan);
    }
}

void KILN_CALL on_pre(const KilnTickInfo*, void*) {
    guarded("tick", [] { return pre_impl(), KILN_OK; });
}

// ---- world sessions ----
// A game client hosts one world after another. When the game closes the server world the tracker is saved; the next
// server world opens its own sidecar (by its world GUID) at its first step and re-finds its records from there.
void drop_world(const char* why) {
    if (tracker::opened()) tracker::save_now();
    g_started = false;
    g_world_running.store(false);
    g_run_pending.store(false);
    g_session_start = 0;
    logi("kiln.spawn: server world closed (%s); sidecar %s saved", why, tracker::world_key().c_str());
}

bool g_closed = false;  // tick thread

void KILN_CALL on_world(const KilnWorldEvent* ev, void*) {
    if (ev->kind != KILN_WORLD_SERVER) return;
    if (ev->event == KILN_WORLD_STOPPING) {
        drop_world("the game is closing it");
        g_closed = true;
    } else if (ev->event == KILN_WORLD_STARTED) {
        if (ev->session > 1 && !g_closed) drop_world("a new server world started");
        g_closed = false;
    }
}

// ---- table ----
KilnResult i_spawn(const KilnSpawnRequest* req, KilnEntity* out) {
    if (out) *out = 0;
    if (!req || req->size < KILN_SPAWN_REQUEST_V1_SIZE) return KILN_ERR_INVALID_ARGUMENT;
    const bool has_extra = req->size >= offsetof(KilnSpawnRequest, reserved) + sizeof(req->reserved);
    const KilnSpawnComponent* extra = has_extra ? req->components : nullptr;
    const uint32_t extra_count = has_extra ? req->component_count : 0;
    if (!g_ok) return KILN_ERR_UNAVAILABLE;
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    Guid g;
    std::memcpy(g.b, req->template_guid.b, 16);
    Resolved t;
    const KilnResult rc = resolve_template(g.zero() ? nullptr : &req->template_guid, req->template_name, req->flags, &t);
    if (rc != KILN_OK) {
        const std::string what = g.zero() ? std::string(sv(req->template_name)) : g.text();
        logw("kiln.spawn: unknown template '%s'", what.c_str());
        return rc;
    }
    return do_spawn(t, req->position, req->yaw_degrees, req->level, req->flags, sv(req->tag), sv(req->origin), nullptr,
                    out, extra, extra_count);
}

KilnResult i_spawn_item(uint32_t item_id, uint32_t amount, KilnVec3 position, int32_t level, uint32_t flags,
                                  KilnStr tag, KilnStr origin, KilnEntity* out) {
    if (out) *out = 0;
    if (!g_ok) return KILN_ERR_UNAVAILABLE;
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    if (item_id == 0 || amount == 0 || amount > 65535) return KILN_ERR_INVALID_ARGUMENT;
    Resolved t;
    catalog::Entry e;
    if (catalog::find(kPickupName, &e)) {
        t.guid = e.guid;
        t.name = e.name;
    } else {
        Guid::parse(kPickupGuid, &t.guid);
        t.name = kPickupName;
    }
    if (catalog::engine_loaded() && !catalog::engine_has(t.guid)) {
        logw("kiln.spawn: the generic pickup template is not loaded on this build");
        return KILN_ERR_UNAVAILABLE;
    }
    t.kind = KILN_TEMPLATE_PICKUP;
    PickupItem pi{};
    pi.item_id = item_id;
    pi.level = static_cast<uint32_t>(std::max(level, 0));
    pi.amount = static_cast<uint16_t>(amount);
    std::string tg(sv(tag));
    if (tg.empty()) tg = "item";
    return do_spawn(t, position, 0.0f, 0, flags, tg, sv(origin), &pi, out);
}

KilnResult i_destroy(KilnEntity entity) {
    if (!g_ok) return KILN_ERR_UNAVAILABLE;
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    if (entity >= 1 && entity <= 1023) return KILN_ERR_INVALID_ARGUMENT;
    if (!game::can_destroy()) return KILN_ERR_UNAVAILABLE;
    unsigned long fault = 0;
    const bool ok = game::destroy(entity, &fault);
    if (fault) {
        logw("kiln.spawn: destroying entity %u faulted (0x%08lx)", entity, fault);
        return KILN_ERR_INTERNAL;
    }
    if (!ok) return KILN_ERR_NOT_FOUND;
    const bool tracked = tracker::remove_entity(entity);
    logi("kiln.spawn: destroyed entity %u%s", entity, tracked ? " (tracked record dropped)" : "");
    return KILN_OK;
}

KilnResult i_entity_info(KilnEntity entity, KilnEntitySnapshot* out) {
    if (!out || out->size < sizeof(KilnEntitySnapshot)) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_ok) return KILN_ERR_UNAVAILABLE;
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    WorldTransform xf{};
    Guid g;
    uint32_t parent = 0;
    const bool has_t = game::entity_template(entity, &g, &parent);
    out->parent = parent;
    const bool has_x = game::transform(entity, &xf);
    if (!has_t && !has_x) return KILN_ERR_NOT_FOUND;
    if (has_t) {
        out->has_template = 1;
        std::memcpy(out->template_guid.b, g.b, 16);
    }
    if (has_x) {
        out->position = {fixed_to_m(xf.pos[0]), fixed_to_m(xf.pos[1]), fixed_to_m(xf.pos[2])};
        std::memcpy(out->rotation, xf.rot, sizeof out->rotation);
        out->yaw_degrees = static_cast<float>(2.0 * std::atan2(xf.rot[1], xf.rot[3]) * 180.0 / 3.14159265358979323846);
    }
    tracker::Record r;
    if (tracker::find_live(entity, &r)) {
        out->tracked = 1;
        out->serial = r.serial;
    }
    int64_t st = 0;
    if (spawn_time_of(entity, &st)) {
        out->spawn_time = st;
        out->has_spawn_time = 1;
    }
    return KILN_OK;
}

KilnResult i_find(const KilnGuid* guid, KilnStr name_filter, KilnVec3 center, double radius, KilnEntity* out,
                            size_t capacity, size_t* count) {
    if (count) *count = 0;
    if (!g_ok) return KILN_ERR_UNAVAILABLE;
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    if (!game::can_list_entities()) return KILN_ERR_UNAVAILABLE;
    if (radius > 0 && !finite_pos(center)) return KILN_ERR_INVALID_ARGUMENT;
    Guid want;
    const bool by_guid = guid && (std::memcpy(want.b, guid->b, 16), !want.zero());
    std::set<Guid> names;
    const std::string filter = lower(sv(name_filter));
    if (!by_guid && !filter.empty()) {
        if (!catalog::engine_loaded()) load_templates();
        for (const Guid& g : catalog::match_names(filter)) names.insert(g);
    }
    std::vector<EntityRow> rows;
    if (!game::entities(rows)) return KILN_ERR_UNAVAILABLE;
    std::vector<std::pair<double, uint32_t>> hits;
    const double c[3] = {center.x, center.y, center.z};
    for (const EntityRow& e : rows) {
        if (by_guid ? !(e.has_tmpl && e.tmpl == want) : (!filter.empty() && !(e.has_tmpl && names.count(e.tmpl))))
            continue;
        double d = 0;
        if (radius > 0) {
            WorldTransform xf{};
            if (!game::transform(e.id, &xf)) continue;
            double p[3];
            to_m(xf, p);
            d = dist2(p, c);
            if (d > radius * radius) continue;
        }
        hits.emplace_back(d, e.id);
    }
    std::sort(hits.begin(), hits.end());
    if (count) *count = hits.size();
    for (size_t i = 0; i < hits.size() && i < capacity && out; ++i) out[i] = hits[i].second;
    return KILN_OK;
}

void fill_template(const catalog::Entry& e, KilnTemplateInfo* out) {
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    out->kind = e.kind;
    std::memcpy(out->guid.b, e.guid.b, 16);
    copy_c(out->name, sizeof out->name, e.name);
    copy_c(out->display, sizeof out->display, e.display);
    out->source = e.source;
    out->flags = e.flags;
}

KilnResult KILN_CALL t_template_find(KilnStr name, KilnTemplateInfo* out) {
    if (!out || out->size < sizeof(KilnTemplateInfo)) return KILN_ERR_INVALID_ARGUMENT;
    catalog::Entry e;
    if (!catalog::find(sv(name), &e)) {
        refresh_templates();
        if (!catalog::find(sv(name), &e)) return KILN_ERR_NOT_FOUND;
    }
    fill_template(e, out);
    return KILN_OK;
}

KilnResult KILN_CALL t_template_get(const KilnGuid* guid, KilnTemplateInfo* out) {
    if (!guid || !out || out->size < sizeof(KilnTemplateInfo)) return KILN_ERR_INVALID_ARGUMENT;
    Guid g;
    std::memcpy(g.b, guid->b, 16);
    catalog::Entry e;
    if (!catalog::get(g, &e)) {
        refresh_templates();
        if (!catalog::get(g, &e)) return KILN_ERR_NOT_FOUND;
    }
    fill_template(e, out);
    return KILN_OK;
}

size_t KILN_CALL t_template_count() { return catalog::count(); }

KilnResult KILN_CALL t_template_at(size_t index, KilnTemplateInfo* out) {
    if (!out || out->size < sizeof(KilnTemplateInfo)) return KILN_ERR_INVALID_ARGUMENT;
    catalog::Entry e;
    if (!catalog::at(index, &e)) return KILN_ERR_NOT_FOUND;
    fill_template(e, out);
    return KILN_OK;
}

size_t KILN_CALL t_tracked_count() { return tracker::count(); }

KilnResult KILN_CALL t_tracked_at(size_t index, KilnTrackedEntity* out) {
    if (!out || out->size < sizeof(KilnTrackedEntity)) return KILN_ERR_INVALID_ARGUMENT;
    tracker::Record r;
    if (!tracker::at(index, &r)) return KILN_ERR_NOT_FOUND;
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    out->entity = r.live ? r.entity : 0;
    out->serial = r.serial;
    std::memcpy(out->template_guid.b, r.tmpl.b, 16);
    out->position = {r.pos[0], r.pos[1], r.pos[2]};
    out->spawn_position = {r.spawn_pos[0], r.spawn_pos[1], r.spawn_pos[2]};
    out->spawned_unix = r.spawned_unix;
    out->state = r.live ? KILN_TRACKED_LIVE : KILN_TRACKED_MISSING;
    out->flags = r.flags;
    copy_c(out->tag, sizeof out->tag, r.tag);
    copy_c(out->origin, sizeof out->origin, r.origin);
    copy_c(out->name, sizeof out->name, r.name);
    return KILN_OK;
}

KilnResult i_cleanup(KilnStr tag, KilnStr origin, KilnSpawnCleanup* out) {
    if (out && out->size < sizeof(KilnSpawnCleanup)) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_ok) return KILN_ERR_UNAVAILABLE;
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    if (!game::world_ready()) return KILN_ERR_UNAVAILABLE;
    if (!tracker::opened()) open_tracker();
    refresh_impl(true);
    const std::string want_tag(sv(tag)), want_origin(sv(origin));
    std::vector<tracker::Record> recs = tracker::snapshot(), keep;
    uint32_t destroyed = 0, missing = 0, kept = 0;
    for (tracker::Record& r : recs) {
        const bool match = (want_tag.empty() || r.tag == want_tag) && (want_origin.empty() || r.origin == want_origin);
        if (!match) {
            ++kept;
            keep.push_back(std::move(r));
            continue;
        }
        if (!r.live) {
            ++missing;
            continue;
        }
        unsigned long fault = 0;
        if (game::destroy(r.entity, &fault)) {
            ++destroyed;
        } else if (fault) {
            logw("kiln.spawn: cleanup: destroying entity %u faulted (0x%08lx); record kept", r.entity, fault);
            ++kept;
            keep.push_back(std::move(r));
        } else {
            ++missing;
        }
    }
    tracker::replace(std::move(keep), true);
    logi("kiln.spawn: cleanup (tag '%s', origin '%s'): %u destroyed, %u missing dropped, %u kept", want_tag.c_str(),
         want_origin.c_str(), destroyed, missing, kept);
    if (out) {
        out->destroyed = destroyed;
        out->missing = missing;
        out->kept = kept;
    }
    return KILN_OK;
}

KilnResult i_refresh(uint32_t* live) {
    if (live) *live = 0;
    if (!g_ok) return KILN_ERR_UNAVAILABLE;
    if (!on_tick()) return KILN_ERR_WRONG_THREAD;
    if (!game::world_ready()) return KILN_ERR_UNAVAILABLE;
    if (!tracker::opened()) open_tracker();
    const uint32_t n = refresh_impl(true);
    if (live) *live = n;
    return KILN_OK;
}

KilnResult KILN_CALL t_world_key(char* buffer, size_t capacity, size_t* out_len) {
    const std::string k = tracker::world_key();
    if (out_len) *out_len = k.size();
    if (k.empty()) return KILN_ERR_UNAVAILABLE;
    if (!buffer || capacity < k.size() + 1) return KILN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(buffer, k.data(), k.size());
    buffer[k.size()] = 0;
    return KILN_OK;
}

KilnResult KILN_CALL t_spawn(const KilnSpawnRequest* req, KilnEntity* out) {
    return guarded("spawn", [&] { return i_spawn(req, out); });
}
KilnResult KILN_CALL t_spawn_item(uint32_t item_id, uint32_t amount, KilnVec3 position, int32_t level, uint32_t flags,
                                  KilnStr tag, KilnStr origin, KilnEntity* out) {
    return guarded("spawn_item", [&] { return i_spawn_item(item_id, amount, position, level, flags, tag, origin, out); });
}
KilnResult KILN_CALL t_destroy(KilnEntity entity) {
    return guarded("destroy", [&] { return i_destroy(entity); });
}
KilnResult KILN_CALL t_entity_info(KilnEntity entity, KilnEntitySnapshot* out) {
    return guarded("entity_info", [&] { return i_entity_info(entity, out); });
}
KilnResult KILN_CALL t_find(const KilnGuid* guid, KilnStr name_filter, KilnVec3 center, double radius, KilnEntity* out,
                            size_t capacity, size_t* count) {
    return guarded("find", [&] { return i_find(guid, name_filter, center, radius, out, capacity, count); });
}
KilnResult KILN_CALL t_cleanup(KilnStr tag, KilnStr origin, KilnSpawnCleanup* out) {
    return guarded("cleanup", [&] { return i_cleanup(tag, origin, out); });
}
KilnResult KILN_CALL t_refresh(uint32_t* live) {
    return guarded("refresh", [&] { return i_refresh(live); });
}

uint32_t KILN_CALL t_world_state() { return world_state(); }
uint32_t KILN_CALL t_capabilities() { return KILN_SPAWN_CAP_COMPONENTS; }

const KilnSpawnApi kTable = {
    {sizeof(KilnSpawnApi), KILN_SPAWN_VERSION},
    &t_spawn,
    &t_spawn_item,
    &t_destroy,
    &t_entity_info,
    &t_find,
    &t_template_find,
    &t_template_get,
    &t_template_count,
    &t_template_at,
    &t_tracked_count,
    &t_tracked_at,
    &t_cleanup,
    &t_refresh,
    &t_world_key,
    &t_world_state,
    &t_capabilities,
};

}  // namespace

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    // Needs the table up to register_service; server_world (added later) is used when present.
    if (api->abi_version != KILN_ABI_VERSION || api->size < offsetof(KilnApi, register_service) + sizeof(void*))
        return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = KilnStr{KILN_SPAWN_SERVICE, sizeof(KILN_SPAWN_SERVICE) - 1};
    info->version = KilnStr{"1", 1};
    catalog::init();
    if (!api->feature(KilnStr{"spawn.create", 12})) {
        logw("kiln.spawn: unavailable on this build (sigdb feature spawn.create is off)");
        return KILN_ERR_UNAVAILABLE;
    }
    std::string why;
    try {
        if (!game::init(api, owner, why)) {
            logw("kiln.spawn: unavailable: %s", why.c_str());
            return KILN_ERR_UNAVAILABLE;
        }
    } catch (...) {
        logw("kiln.spawn: unavailable: init threw");
        return KILN_ERR_UNAVAILABLE;
    }
    KilnHandle h = 0;
    const bool has_worlds = api->size >= offsetof(KilnApi, session) + sizeof(void*);
    g_client = has_worlds && api->side() == KILN_SIDE_CLIENT;
    if (has_worlds) api->subscribe_world(owner, &on_world, nullptr, &h);
    if (KilnResult rc = api->subscribe_tick(owner, KILN_TICK_PRE, &on_pre, nullptr, &h); rc != KILN_OK) return rc;
    if (api->subscribe_log(owner, &on_game_log, nullptr, &h) == KILN_OK) g_have_log.store(true);
    else
        logw("kiln.spawn: no game-log mirror; tracked records are re-found by the periodic scan only");
    g_ok = true;
    logi("kiln.spawn: ready (world from %s; destroy %s, engine templates %s, entity scan %s; %zu shipped catalog entries)",
         game::world_from_core() ? "the core" : "own SimStep hook", game::can_destroy() ? "on" : "off",
         game::can_list_templates() ? "on" : "off", game::can_list_entities() ? "on" : "off", catalog::count());
    return api->register_service(owner, KilnStr{KILN_SPAWN_SERVICE, sizeof(KILN_SPAWN_SERVICE) - 1}, &kTable);
}
