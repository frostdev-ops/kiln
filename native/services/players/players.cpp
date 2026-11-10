// SPDX-License-Identifier: MIT
// kiln.players: who is on the server (native/core/abi/services/kiln_players.h; evidence docs/re/players.md).
//
//  - A hook on the engine's RegisterSingleton (runs only while worlds are set up) captures the server world and the
//    "ServerPlayers" (the game server's 16 player records) and "BasicPlayerInfos" singletons. The first scan checks
//    them against the world's own singleton map and the GameServer's world pointer; any mismatch turns the records
//    off (log-only mode), never a guess.
//  - Every server-world step (Pre) the 16 records are copied under an SEH guard, diffed against the previous step
//    (joined / left events), merged with the game-log tracker (SteamID64), positioned with GetEntityTransform and
//    published as a snapshot that any thread can read.
//  - Without the records (unknown build), the log tracker alone is the player list.
#include <windows.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <string_view>
#include <vector>

#include "kiln_api.h"
#include "log_tracker.h"
#include "playerstate.h"
#include "progression.h"
#include "kiln_hook.hpp"
#include "services/kiln_players.h"

namespace {

using kiln::players::LogEvent;
using kiln::players::LogSlot;
using kiln::players::LogTracker;

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;

constexpr std::string_view kServerPlayers = "ServerPlayers";
constexpr std::string_view kBasicPlayerInfos = "BasicPlayerInfos";
constexpr uint32_t kRing = 64;
constexpr size_t kRecCopy = 0x60;  // the record prefix we read (fields up to +0x50)

// ---------------------------------------------------------------------------------------------- layout (sigdb) ---
struct Layout {
    // ServerPlayers records
    int64_t server_records = -1, server_world = -1;
    int64_t rec_stride = 0, rec_count = 0, rec_state = -1, rec_peer = -1, rec_group = -1, rec_perms = -1,
            rec_entity = -1, rec_name = -1, rec_account = -1;
    // BasicPlayerInfos
    int64_t info_stride = 0, info_max = 0, info_admin = -1, info_handle = -1;
    // the world's singleton map
    int64_t map_bits = -1, map_cap = -1, map_keys = -1, map_values = -1, singletons = -1, sg_stride = 0,
            sg_object = -1, sg_hash = -1;
    // core
    int64_t world_storage = -1, storage_kind = -1;
    bool records = false, admin = false, map = false;
} L;

uintptr_t g_get_transform = 0;  // core symbol GetEntityTransform: WorldTransform*(world, entity)

std::atomic<uintptr_t> g_world{0}, g_server_players{0}, g_basic_infos{0}, g_world_infos{0};
kiln::RawHook g_reg_hook;

// Set once (kiln_plugin_main, then the first scan), read from any thread.
std::atomic<bool> g_records_ok{false};  // records usable (validated at the first scan)
std::atomic<bool> g_admin_ok{false};
bool g_validated = false;  // tick thread only
std::atomic<bool> g_map_ok{false};
std::atomic<bool> g_positions_ok{false};
std::atomic<bool> g_log_ok{false};

// Lab only: KILN_PLAYERS_SIMULATE=<file of game-log lines> ignores the records and replays the file into the live log
// tracker, one line per step (a login line takes its permission list along), so the whole event path (tracker, scan
// diff, ring, subscribers, Kiln.Players' per-mod pump) runs without a client. Logged loudly; never set on a real
// server.
bool g_sim = false;
std::vector<std::string> g_sim_lines;
size_t g_sim_pos = 0;

// ---------------------------------------------------------------------------------------------- helpers ----------
void logf(int level, const char* fmt, ...) {
    if (!g_api) return;
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    if (n > static_cast<int>(sizeof buf) - 1) n = sizeof buf - 1;
    g_api->log(g_owner, level, KilnStr{buf, static_cast<size_t>(n)});
}

bool constant(const char* name, int64_t* out) {
    return g_api->constant(KilnStr{name, std::strlen(name)}, out) == KILN_OK;
}

// SEH-guarded reads of game memory (no C++ objects with destructors in these functions, as __try requires).
bool guarded_copy(void* dst, uintptr_t src, size_t n) {
    if (!src) return false;
    __try {
        std::memcpy(dst, reinterpret_cast<const void*>(src), n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

using GetTransformFn = const int64_t*(__fastcall*)(uintptr_t world, uint32_t entity);
bool guarded_position(uintptr_t fn, uintptr_t world, uint32_t entity, int64_t out[3]) {
    __try {
        const int64_t* t = reinterpret_cast<GetTransformFn>(fn)(world, entity);
        if (!t) return false;
        out[0] = t[0];
        out[1] = t[1];
        out[2] = t[2];
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool guarded_event(KilnPlayerEventFn fn, const KilnPlayerEvent* e, void* user, unsigned long* code) {
    __try {
        fn(e, user);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

template <class T>
T rd(const uint8_t* p, int64_t off) {
    T v;
    std::memcpy(&v, p + off, sizeof v);
    return v;
}

uint32_t fnv1a(std::string_view s) {
    uint32_t h = 0x811c9dc5u;
    for (unsigned char c : s) h = (h ^ c) * 0x1000193u;
    return h;
}

// The world's singleton map (open addressing, FNV-1a of the name -> u16 index into the singleton array), as
// RegisterSingleton builds it. 0 when not found. Game memory: tick thread or before the first tick.
uintptr_t map_lookup(uintptr_t world, std::string_view name) {
    if (!L.map || !world) return 0;
    uint64_t cap = 0;
    uintptr_t bits = 0, keys = 0, values = 0, array = 0;
    if (!guarded_copy(&cap, world + L.map_cap, 8) || !guarded_copy(&bits, world + L.map_bits, 8) ||
        !guarded_copy(&keys, world + L.map_keys, 8) || !guarded_copy(&values, world + L.map_values, 8) ||
        !guarded_copy(&array, world + L.singletons, 8))
        return 0;
    if (cap == 0 || cap > (1u << 16) || (cap & (cap - 1)) != 0) return 0;
    const uint32_t h = fnv1a(name);
    uint64_t i = h & (cap - 1);
    for (uint64_t n = 0; n < cap; ++n) {
        uint64_t word = 0;
        if (!guarded_copy(&word, bits + (i >> 6) * 8, 8)) return 0;
        if (!((word >> (i & 63)) & 1)) return 0;
        uint32_t key = 0;
        if (!guarded_copy(&key, keys + i * 4, 4)) return 0;
        if (key == h) {
            uint16_t index = 0;
            uint32_t entry_hash = 0;
            uintptr_t object = 0;
            if (!guarded_copy(&index, values + i * 2, 2)) return 0;
            const uintptr_t e = array + static_cast<uintptr_t>(index) * static_cast<uintptr_t>(L.sg_stride);
            if (!guarded_copy(&entry_hash, e + L.sg_hash, 4) || entry_hash != h) return 0;
            if (!guarded_copy(&object, e + L.sg_object, 8)) return 0;
            return object;
        }
        i = i + 1 < cap ? i + 1 : 0;
    }
    return 0;
}

// ---------------------------------------------------------------------------------------------- RegisterSingleton -
struct GameStr {
    const char* ptr;
    uint64_t len;
};

// A game client runs a client and a prediction world next to the server world it hosts: their singletons
// (BasicPlayerInfos is registered in the client world too) must not replace the server world's.
bool server_kind(uintptr_t world) {
    if (!world || L.world_storage < 0 || L.storage_kind < 0) return true;  // layout unknown: as before
    uint8_t kind = 0xFF;
    return guarded_copy(&kind, world + static_cast<uintptr_t>(L.world_storage + L.storage_kind), 1) && kind == 0;
}

void capture(uintptr_t world, const GameStr* name, uintptr_t object) {
    if (!server_kind(world)) return;
    GameStr s{};
    if (!guarded_copy(&s, reinterpret_cast<uintptr_t>(name), sizeof s) || !s.ptr || s.len == 0 || s.len > 64) return;
    char text[65] = {};
    if (!guarded_copy(text, reinterpret_cast<uintptr_t>(s.ptr), static_cast<size_t>(s.len))) return;
    const std::string_view n(text, static_cast<size_t>(s.len));
    if (n == kServerPlayers) {
        g_server_players.store(object);
        g_world.store(world);
    } else if (n == kBasicPlayerInfos) {
        g_basic_infos.store(object);
        g_world_infos.store(world);
    }
}

uintptr_t __fastcall hk_register_singleton(uintptr_t world, const GameStr* name, uintptr_t object, uintptr_t options) {
    const uintptr_t r = g_reg_hook.call<uintptr_t>(world, name, object, options);
    if (r & 0xFF) capture(world, name, object);
    return r;
}

// ---------------------------------------------------------------------------------------------- state ------------
struct SlotState {
    bool in_game = false;
    uint32_t handle = 0;
    std::string name;
    uint64_t joined_step = 0;
    KilnPlayer last{};  // as last seen in game
};

std::mutex g_snap_mutex;  // guards the snapshot, the event ring and the status counters below
std::vector<KilnPlayer> g_snapshot;  // every used slot (any state), slot order
uint32_t g_slots = 0, g_in_game = 0, g_connected = 0;
bool g_layout_ok = true;
uint64_t g_scans = 0;
std::array<KilnPlayerEvent, kRing> g_ring{};
uint64_t g_seq = 0;
std::atomic<uint64_t> g_seq_pub{0};

std::array<SlotState, LogTracker::kSlots> g_slot_state;  // tick thread only

std::mutex g_log_mutex;  // guards the tracker and its pending events (log thread writes, tick thread reads)
LogTracker g_tracker;
std::vector<LogEvent> g_log_events;  // the tracker's own events: unused (the scan diffs the tracker's state)
std::atomic<uint64_t> g_log_lines{0}, g_log_multiline{0};
bool g_multiline_noted = false;

struct Sub {
    KilnHandle handle;
    KilnOwner owner;
    KilnPlayerEventFn fn;
    void* user;
};
std::mutex g_sub_mutex;
std::vector<Sub> g_subs;
uint64_t g_next_handle = 1;

double to_m(int64_t v) { return static_cast<double>(v) / 4294967296.0; }

void set_name(KilnPlayer& p, std::string_view n) {
    const size_t len = std::min<size_t>(n.size(), KILN_PLAYERS_NAME_MAX - 1);
    std::memcpy(p.name, n.data(), len);
    p.name[len] = 0;
    p.name_len = static_cast<uint32_t>(len);
}

std::string_view name_of(const KilnPlayer& p) { return std::string_view(p.name, p.name_len); }

KilnPlayer from_log(uint32_t slot, const LogSlot& s) {
    KilnPlayer p{};
    p.size = sizeof p;
    p.slot = slot;
    p.entity = slot + 1;
    p.state = s.in_game ? KILN_PLAYER_IN_GAME : (s.leaving ? KILN_PLAYER_LEAVING : KILN_PLAYER_JOINING);
    p.flags = KILN_PLAYER_FROM_LOG;
    if (s.steam_id) {
        p.steam_id = s.steam_id;
        p.flags |= KILN_PLAYER_HAS_STEAM_ID;
    }
    if (s.has_perms) {
        p.permissions = s.perms;
        p.flags |= KILN_PLAYER_HAS_PERMISSIONS;
    }
    set_name(p, s.name);
    return p;
}

// Steam id for a record: the log slot with the same index and name, else any slot with that name.
uint64_t steam_for(uint32_t slot, std::string_view name, const std::array<LogSlot, LogTracker::kSlots>& log) {
    if (slot < log.size() && log[slot].used && log[slot].steam_id && (log[slot].name.empty() || log[slot].name == name))
        return log[slot].steam_id;
    if (!name.empty())
        for (const LogSlot& s : log)
            if (s.used && s.steam_id && s.name == name) return s.steam_id;
    return 0;
}

// One record -> KilnPlayer. false: the slot is free (or inconsistent: *bad = true).
bool read_record(uint32_t i, uintptr_t base, KilnPlayer& p, uint32_t* rec_entity, bool* bad) {
    uint8_t rec[kRecCopy];
    if (!guarded_copy(rec, base + static_cast<uintptr_t>(i) * static_cast<uintptr_t>(L.rec_stride), sizeof rec)) {
        *bad = true;
        return false;
    }
    const uint32_t handle = rd<uint32_t>(rec, 0);
    const uint8_t state = rd<uint8_t>(rec, L.rec_state);
    if (handle == 0) {
        if (state != 0) *bad = true;
        return false;
    }
    if ((handle & 0x3F) != i || state > 6) {
        *bad = true;
        return false;
    }
    p = KilnPlayer{};
    p.size = sizeof p;
    p.slot = i;
    p.handle = handle;
    *rec_entity = rd<uint32_t>(rec, L.rec_entity);
    p.entity = *rec_entity ? *rec_entity : i + 1;
    p.permissions = rd<uint8_t>(rec, L.rec_perms);
    p.user_group = rd<uint32_t>(rec, L.rec_group);
    p.account_id = rd<uint64_t>(rec, L.rec_account);
    p.flags = KILN_PLAYER_FROM_RECORDS | KILN_PLAYER_HAS_PERMISSIONS;
    if (state == 3) p.state = *rec_entity ? KILN_PLAYER_IN_GAME : KILN_PLAYER_JOINING;
    else if (state >= 4) p.state = KILN_PLAYER_LEAVING;
    else p.state = KILN_PLAYER_JOINING;
    const uintptr_t name_ptr = rd<uint64_t>(rec, L.rec_name);
    const uint64_t name_len = rd<uint64_t>(rec, L.rec_name + 8);
    if (name_ptr && name_len > 0 && name_len < 4096) {
        char text[KILN_PLAYERS_NAME_MAX] = {};
        const size_t n = std::min<size_t>(static_cast<size_t>(name_len), KILN_PLAYERS_NAME_MAX - 1);
        if (guarded_copy(text, name_ptr, n)) set_name(p, std::string_view(text, n));
    }
    return true;
}

void read_admin(KilnPlayer& p, uintptr_t infos) {
    if (!g_admin_ok || !infos || p.entity == 0 || p.entity - 1 > static_cast<uint32_t>(L.info_max) ||
        p.entity > g_slots)
        return;  // the object holds one entry per slot; the game's own check (entity-1 <= 0x3FE) is looser
    uint8_t entry[0xA0];
    if (L.info_stride != sizeof entry ||
        !guarded_copy(entry, infos + static_cast<uintptr_t>(p.entity - 1) * sizeof entry, sizeof entry))
        return;
    if (rd<uint32_t>(entry, 0) != p.entity) return;  // as IsAdmin: the entry must name the entity
    p.is_admin = rd<uint8_t>(entry, L.info_admin) & 1;
    p.flags |= KILN_PLAYER_HAS_ADMIN;
}

void read_position(KilnPlayer& p, uintptr_t world) {
    if (!g_positions_ok || !world || p.state != KILN_PLAYER_IN_GAME) return;
    int64_t fx[3];
    if (!guarded_position(g_get_transform, world, p.entity, fx)) return;
    p.position = KilnVec3{to_m(fx[0]), to_m(fx[1]), to_m(fx[2])};
    p.flags |= KILN_PLAYER_HAS_POSITION;
}

// ---------------------------------------------------------------------------------------------- validation -------
void validate_once() {
    if (g_validated) return;
    g_validated = true;
    const uintptr_t world = g_world.load(), sp = g_server_players.load(), bi = g_basic_infos.load();
    // the server world: kind byte 0
    bool server_world = false;
    if (world && L.world_storage >= 0 && L.storage_kind >= 0) {
        uint8_t kind = 0xFF;
        server_world = guarded_copy(&kind, world + L.world_storage + L.storage_kind, 1) && kind == 0;
    }
    const uintptr_t map_sp = map_lookup(world, kServerPlayers);
    const uintptr_t map_bi = map_lookup(g_world_infos.load(), kBasicPlayerInfos);
    g_map_ok = L.map && sp && map_sp == sp && (!bi || map_bi == bi);
    bool gs_ok = false;
    if (sp && L.server_records >= 0 && L.server_world >= 0) {
        uintptr_t gs_world = 0;
        gs_ok = guarded_copy(&gs_world, sp - L.server_records + L.server_world, 8) && gs_world == world;
    }
    g_records_ok = L.records && sp && world && server_world && (g_map_ok || !L.map) && gs_ok && !g_sim;
    g_admin_ok = L.admin && bi && g_world_infos.load() == world && (map_bi == bi || !L.map);
    g_positions_ok = g_get_transform && world && server_world;
    g_slots = (g_records_ok || (g_sim && L.records)) ? static_cast<uint32_t>(L.rec_count) : 16u;
    if (g_sim) {
        g_log_ok = true;
        logf(KILN_LOG_WARN, "players: SIMULATION: records ignored, replaying %zu log lines (KILN_PLAYERS_SIMULATE)",
             g_sim_lines.size());
    }
    // Evidence: which bytes of the first record and BasicPlayerInfos entry are set on an empty server.
    auto dump = [](const char* what, uintptr_t at, size_t n) {
        std::vector<uint8_t> b(n);
        if (!at || !guarded_copy(b.data(), at, n)) return;
        std::string out;
        char tmp[48];
        for (size_t i = 0; i < n; i += 8) {
            uint64_t q = 0;
            std::memcpy(&q, b.data() + i, std::min<size_t>(8, n - i));
            if (!q) continue;
            std::snprintf(tmp, sizeof tmp, " +0x%zx=%llx", i, static_cast<unsigned long long>(q));
            out += tmp;
        }
        logf(KILN_LOG_INFO, "players: %s %p non-zero qwords:%s", what, reinterpret_cast<void*>(at),
             out.empty() ? " none" : out.c_str());
    };
    if (sp && L.rec_stride > 0) dump("record[0]", sp, static_cast<size_t>(L.rec_stride));
    if (bi) dump("BasicPlayerInfos[0]", bi, 0xA0);
    logf(KILN_LOG_INFO,
         "players: world %p (server world: %s), ServerPlayers %p (map %p: %s), BasicPlayerInfos %p (map %p: %s), "
         "GameServer+0x%llx = world: %s",
         reinterpret_cast<void*>(world), server_world ? "yes" : "NO", reinterpret_cast<void*>(sp),
         reinterpret_cast<void*>(map_sp), map_sp == sp && sp ? "match" : "MISMATCH", reinterpret_cast<void*>(bi),
         reinterpret_cast<void*>(map_bi), map_bi == bi && bi ? "match" : "MISMATCH",
         static_cast<unsigned long long>(L.server_world), gs_ok ? "yes" : "NO");
    logf(g_records_ok ? KILN_LOG_INFO : KILN_LOG_WARN,
         "players: source %s; %u slots x 0x%llx (state +0x%llx, name +0x%llx, entity +0x%llx, account +0x%llx), "
         "admin bit %s, positions %s, log mirror %s",
         g_records_ok ? "records" : (g_log_ok ? "game log (records unavailable)" : "NONE"), g_slots,
         static_cast<unsigned long long>(L.rec_stride), static_cast<unsigned long long>(L.rec_state),
         static_cast<unsigned long long>(L.rec_name), static_cast<unsigned long long>(L.rec_entity),
         static_cast<unsigned long long>(L.rec_account), g_admin_ok ? "on" : "off", g_positions_ok ? "on" : "off",
         g_log_ok ? "on" : "off");
}

// ---------------------------------------------------------------------------------------------- the scan ---------
void push_event(uint32_t kind, uint64_t step, const KilnPlayer& p, std::vector<KilnPlayerEvent>& fired) {
    KilnPlayerEvent e{};
    e.seq = ++g_seq;
    e.kind = kind;
    e.step = step;
    e.player = p;
    g_ring[e.seq % kRing] = e;
    fired.push_back(e);
}

void sim_step() {
    // Start after 50 steps, so mods loaded at the first step have subscribed.
    if (!g_sim || g_sim_pos >= g_sim_lines.size() || g_scans < 50) return;
    std::string msg = g_sim_lines[g_sim_pos++];
    if (msg.find("logged in with Permissions:") != std::string::npos)
        while (g_sim_pos < g_sim_lines.size()) {
            const std::string_view n = kiln::players::strip_file_prefix(g_sim_lines[g_sim_pos]);
            if (!(n.rfind("\t - ", 0) == 0 || n.empty())) break;
            msg += "\n";
            msg += n;
            ++g_sim_pos;
            if (n.empty()) break;
        }
    std::lock_guard lock(g_log_mutex);
    g_tracker.feed(msg, g_log_events);
    g_log_events.clear();
}

LARGE_INTEGER g_qpf{};
double g_scan_us_sum = 0, g_scan_us_max = 0;
uint64_t g_scan_timed = 0;
uint64_t g_last_step = 0;  // tick thread

void deliver(const std::vector<KilnPlayerEvent>& fired);

void KILN_CALL on_pre(const KilnTickInfo* tick, void*) {
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    validate_once();
    sim_step();
    const uint64_t step = tick ? tick->step : 0;
    g_last_step = step;
    const uintptr_t world = g_world.load();

    std::array<LogSlot, LogTracker::kSlots> log{};
    if (g_log_ok) {
        std::lock_guard lock(g_log_mutex);
        g_tracker.flush(g_log_events);
        g_log_events.clear();
        for (uint32_t i = 0; i < LogTracker::kSlots; ++i) log[i] = g_tracker.slot(i);
    }
    if (!g_multiline_noted && g_log_multiline.load() > 0) {
        g_multiline_noted = true;
        logf(KILN_LOG_INFO, "players: the log mirror delivers multi-line messages (%llu so far)",
             static_cast<unsigned long long>(g_log_multiline.load()));
    }

    std::vector<KilnPlayer> now;
    bool layout_ok = true;
    if (g_records_ok) {
        const uintptr_t base = g_server_players.load(), infos = g_basic_infos.load();
        for (uint32_t i = 0; i < g_slots; ++i) {
            KilnPlayer p;
            uint32_t rec_entity = 0;
            bool bad = false;
            if (!read_record(i, base, p, &rec_entity, &bad)) {
                layout_ok = layout_ok && !bad;
                continue;
            }
            if (const uint64_t sid = steam_for(i, name_of(p), log)) {
                p.steam_id = sid;
                p.flags |= KILN_PLAYER_HAS_STEAM_ID;
            }
            read_admin(p, infos);
            read_position(p, world);
            now.push_back(p);
        }
    } else if (g_log_ok) {
        for (uint32_t i = 0; i < LogTracker::kSlots; ++i) {
            if (!log[i].used) continue;
            KilnPlayer p = from_log(i, log[i]);
            read_admin(p, g_basic_infos.load());
            read_position(p, world);
            now.push_back(p);
        }
    }

    // Diff against the previous scan.
    std::vector<KilnPlayerEvent> fired;
    std::array<const KilnPlayer*, LogTracker::kSlots> cur{};
    for (const KilnPlayer& p : now)
        if (p.slot < cur.size()) cur[p.slot] = &p;
    {
        std::lock_guard lock(g_snap_mutex);
        for (uint32_t i = 0; i < LogTracker::kSlots; ++i) {
            SlotState& s = g_slot_state[i];
            const KilnPlayer* p = cur[i];
            const bool in_game = p && p->state == KILN_PLAYER_IN_GAME;
            // Same connection: same handle (slot + generation) and, when both are readable, the same name.
            const bool same = in_game && s.in_game && p->handle == s.handle &&
                              (p->name_len == 0 || s.name.empty() || name_of(*p) == s.name);
            if (s.in_game && !same) {
                push_event(KILN_PLAYER_LEFT, step, s.last, fired);
                s.in_game = false;
            }
            if (same && s.name.empty() && p->name_len) s.name.assign(name_of(*p));
            if (in_game && !s.in_game) {
                s.in_game = true;
                s.handle = p->handle;
                s.name.assign(name_of(*p));
                s.joined_step = step;
                KilnPlayer j = *p;
                j.joined_step = step;
                push_event(KILN_PLAYER_JOINED, step, j, fired);
            }
        }
        uint32_t in = 0;
        for (KilnPlayer& p : now) {
            if (p.slot < g_slot_state.size() && g_slot_state[p.slot].in_game) {
                p.joined_step = g_slot_state[p.slot].joined_step;
                g_slot_state[p.slot].last = p;
            }
            if (p.state == KILN_PLAYER_IN_GAME) ++in;
        }
        g_snapshot.swap(now);
        g_in_game = in;
        g_connected = static_cast<uint32_t>(g_snapshot.size());
        g_layout_ok = layout_ok;
        ++g_scans;
        g_seq_pub.store(g_seq);
    }
    {
        LARGE_INTEGER t1;
        QueryPerformanceCounter(&t1);
        if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
        const double us = static_cast<double>(t1.QuadPart - t0.QuadPart) * 1e6 / static_cast<double>(g_qpf.QuadPart);
        g_scan_us_sum += us;
        g_scan_us_max = std::max(g_scan_us_max, us);
        if (++g_scan_timed == 1000)
            logf(KILN_LOG_INFO, "players: scan cost over 1000 steps: avg %.1f us, max %.1f us (%u in game, %u connected)",
                 g_scan_us_sum / 1000.0, g_scan_us_max, g_in_game, g_connected);
    }

    deliver(fired);
    kiln::progression::pre(step);
}

void deliver(const std::vector<KilnPlayerEvent>& fired) {
    if (fired.empty()) return;
    for (const KilnPlayerEvent& e : fired)
        logf(KILN_LOG_INFO, "players: %s slot %u entity %u '%s' (steam %llu, admin %u, perms 0x%x) [event %llu]",
             e.kind == KILN_PLAYER_JOINED ? "joined" : "left", e.player.slot, e.player.entity, e.player.name,
             static_cast<unsigned long long>(e.player.steam_id), e.player.is_admin, e.player.permissions,
             static_cast<unsigned long long>(e.seq));
    std::vector<Sub> subs;
    {
        std::lock_guard lock(g_sub_mutex);
        subs = g_subs;
    }
    for (const Sub& s : subs) {
        for (const KilnPlayerEvent& e : fired) {
            unsigned long code = 0;
            if (!guarded_event(s.fn, &e, s.user, &code)) {
                logf(KILN_LOG_ERROR, "players: event subscriber faulted (0x%08lx); removed", code);
                std::lock_guard lock(g_sub_mutex);
                std::erase_if(g_subs, [&](const Sub& x) { return x.handle == s.handle; });
                break;
            }
        }
    }
}

// ---------------------------------------------------------------------------------------------- world sessions ---
// A game client hosts one server world after another in one process (back to the main menu, host again). The
// server world's STOPPING (tick thread, world still intact) ends the session: everyone still in game leaves, and the
// captured singletons and validation go. The next world's setup registers its singletons again (captured above) and
// its first scan validates them. A dedicated server has one world for its whole run: nothing here runs there.
bool end_session(const char* why, bool keep_captures) {
    std::vector<KilnPlayerEvent> fired;
    {
        std::lock_guard lock(g_snap_mutex);
        for (SlotState& s : g_slot_state) {
            if (s.in_game) push_event(KILN_PLAYER_LEFT, g_last_step, s.last, fired);
            s = SlotState{};
        }
        g_snapshot.clear();
        g_in_game = g_connected = 0;
        g_seq_pub.store(g_seq);
    }
    {
        std::lock_guard lock(g_log_mutex);
        g_tracker = LogTracker{};
        g_log_events.clear();
    }
    if (!keep_captures) {  // at a new session's start the captures are already the new world's (its setup ran)
        g_world.store(0);
        g_server_players.store(0);
        g_basic_infos.store(0);
        g_world_infos.store(0);
    }
    g_records_ok = g_admin_ok = g_map_ok = g_positions_ok = false;
    g_validated = false;
    logf(KILN_LOG_INFO, "players: server world session ended (%s): %zu player(s) left with it", why, fired.size());
    deliver(fired);
    return true;
}

bool g_session_open = false;  // tick thread: a server world ran since the last end_session

void KILN_CALL on_world(const KilnWorldEvent* ev, void*) {
    if (ev->kind != KILN_WORLD_SERVER) return;
    if (ev->event == KILN_WORLD_STOPPING) {
        end_session("the game is closing the world", false);
        kiln::progression::session_end();
        g_session_open = false;
    } else if (ev->event == KILN_WORLD_STARTED) {
        // Without the world-destroy hook a new session arrives without a STOPPING: close the old one here.
        if (ev->session > 1 && g_session_open) {
            end_session("a new server world started", true);
            kiln::progression::session_end();
        }
        g_session_open = true;
        if (ev->session > 1) g_validated = false;  // validate the new world's singletons at its first scan
    }
}

void KILN_CALL on_log(int32_t, KilnStr text, void*) {
    g_log_lines.fetch_add(1, std::memory_order_relaxed);
    if (!text.ptr || text.len == 0) return;
    const std::string_view t(text.ptr, text.len);
    if (t.find('\n') != std::string_view::npos) g_log_multiline.fetch_add(1, std::memory_order_relaxed);
    std::lock_guard lock(g_log_mutex);
    g_tracker.feed(t, g_log_events);
    g_log_events.clear();
}

// ---------------------------------------------------------------------------------------------- the table --------
bool on_tick_thread() { return g_api && g_api->on_tick_thread(); }

KilnResult KILN_CALL t_status(KilnPlayersStatus* out) {
    if (!out || out->size < sizeof(KilnPlayersStatus)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_snap_mutex);
    KilnPlayersStatus s{};
    s.size = sizeof s;
    s.source = g_records_ok ? KILN_PLAYERS_SOURCE_RECORDS : (g_log_ok ? KILN_PLAYERS_SOURCE_LOG : KILN_PLAYERS_SOURCE_NONE);
    s.records = g_records_ok.load();
    s.admin = g_admin_ok.load();
    s.positions = g_positions_ok.load();
    s.log = g_log_ok.load();
    s.map_ok = g_map_ok.load();
    s.simulated = g_sim;
    s.layout_ok = g_layout_ok;
    s.slots = g_slots;
    s.in_game = g_in_game;
    s.connected = g_connected;
    s.last_seq = g_seq;
    s.scans = g_scans;
    s.log_lines = g_log_lines.load();
    {
        std::lock_guard l2(g_log_mutex);
        s.log_matched = g_tracker.matched();
    }
    s.world = g_world.load();
    s.server_players = g_server_players.load();
    s.basic_infos = g_basic_infos.load();
    *out = s;
    return KILN_OK;
}

KilnResult KILN_CALL t_list(uint32_t flags, KilnPlayer* out, uint32_t capacity, uint32_t* out_count) {
    if (!out && capacity) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_snap_mutex);
    uint32_t n = 0;
    for (const KilnPlayer& p : g_snapshot) {
        if (!(flags & KILN_PLAYERS_LIST_ALL) && p.state != KILN_PLAYER_IN_GAME) continue;
        if (n < capacity) out[n] = p;
        ++n;
    }
    if (out_count) *out_count = n;
    if (!g_records_ok && !g_log_ok) return KILN_ERR_UNAVAILABLE;
    return n > capacity ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

template <class Pred>
KilnResult find(KilnPlayer* out, Pred pred) {
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_snap_mutex);
    const KilnPlayer* best = nullptr;
    for (const KilnPlayer& p : g_snapshot)
        if (pred(p) && (!best || (p.state == KILN_PLAYER_IN_GAME && best->state != KILN_PLAYER_IN_GAME))) best = &p;
    if (!best) return (!g_records_ok && !g_log_ok) ? KILN_ERR_UNAVAILABLE : KILN_ERR_NOT_FOUND;
    *out = *best;
    return KILN_OK;
}

KilnResult KILN_CALL t_get(KilnEntity entity, KilnPlayer* out) {
    return find(out, [&](const KilnPlayer& p) { return entity != 0 && p.entity == entity; });
}

bool ieq(std::string_view a, std::string_view b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i) {
        char x = a[i], y = b[i];
        if (x >= 'A' && x <= 'Z') x = static_cast<char>(x - 'A' + 'a');
        if (y >= 'A' && y <= 'Z') y = static_cast<char>(y - 'A' + 'a');
        if (x != y) return false;
    }
    return true;
}

KilnResult KILN_CALL t_find_name(KilnStr name, KilnPlayer* out) {
    if (!name.ptr || name.len == 0) return KILN_ERR_INVALID_ARGUMENT;
    const std::string_view n(name.ptr, name.len);
    const KilnResult r = find(out, [&](const KilnPlayer& p) { return name_of(p) == n; });
    if (r != KILN_ERR_NOT_FOUND) return r;
    return find(out, [&](const KilnPlayer& p) { return ieq(name_of(p), n); });
}

KilnResult KILN_CALL t_find_steam(uint64_t steam_id, KilnPlayer* out) {
    return find(out, [&](const KilnPlayer& p) { return steam_id != 0 && p.steam_id == steam_id; });
}

KilnResult KILN_CALL t_position(KilnEntity entity, KilnVec3* out) {
    if (!out || entity == 0) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_positions_ok) return KILN_ERR_UNAVAILABLE;
    KilnPlayer p;
    if (const KilnResult r = t_get(entity, &p); r != KILN_OK) return r;
    if (p.state != KILN_PLAYER_IN_GAME) return KILN_ERR_NOT_FOUND;
    if (on_tick_thread()) {
        int64_t fx[3];
        if (!guarded_position(g_get_transform, g_world.load(), entity, fx)) return KILN_ERR_NOT_FOUND;
        *out = KilnVec3{to_m(fx[0]), to_m(fx[1]), to_m(fx[2])};
        return KILN_OK;
    }
    if (!(p.flags & KILN_PLAYER_HAS_POSITION)) return KILN_ERR_NOT_FOUND;
    *out = p.position;
    return KILN_OK;
}

KilnResult KILN_CALL t_events(uint64_t after_seq, KilnPlayerEvent* out, uint32_t capacity, uint32_t* out_count) {
    if (!out && capacity) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_snap_mutex);
    uint64_t from = after_seq + 1;
    const uint64_t oldest = g_seq >= kRing ? g_seq - kRing + 1 : 1;
    if (from < oldest) from = oldest;
    uint32_t n = 0;
    for (uint64_t s = from; s <= g_seq && n < capacity; ++s) out[n++] = g_ring[s % kRing];
    if (out_count) *out_count = n;
    return KILN_OK;
}

uint64_t KILN_CALL t_last_seq() { return g_seq_pub.load(); }

KilnResult KILN_CALL t_subscribe(KilnOwner owner, KilnPlayerEventFn fn, void* user, KilnHandle* out) {
    if (!fn) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_sub_mutex);
    const KilnHandle h = g_next_handle++;
    g_subs.push_back({h, owner, fn, user});
    if (out) *out = h;
    return KILN_OK;
}

KilnResult KILN_CALL t_unsubscribe(KilnHandle handle) {
    std::lock_guard lock(g_sub_mutex);
    const size_t before = g_subs.size();
    std::erase_if(g_subs, [&](const Sub& s) { return s.handle == handle; });
    return g_subs.size() == before ? KILN_ERR_NOT_FOUND : KILN_OK;
}

KilnResult KILN_CALL t_raw_slot(uint32_t slot, KilnPlayerSlotRaw* out) {
    if (!out || out->size < sizeof(KilnPlayerSlotRaw)) return KILN_ERR_INVALID_ARGUMENT;
    if (!on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!g_records_ok) return KILN_ERR_UNAVAILABLE;
    if (slot >= g_slots) return KILN_ERR_INVALID_ARGUMENT;
    uint8_t rec[kRecCopy];
    if (!guarded_copy(rec, g_server_players.load() + static_cast<uintptr_t>(slot) * L.rec_stride, sizeof rec))
        return KILN_ERR_INTERNAL;
    KilnPlayerSlotRaw r{};
    r.size = sizeof r;
    r.handle = rd<uint32_t>(rec, 0);
    r.peer = rd<uint32_t>(rec, L.rec_peer);
    r.state = rd<uint8_t>(rec, L.rec_state);
    r.user_group = rd<uint32_t>(rec, L.rec_group);
    r.permissions = rd<uint8_t>(rec, L.rec_perms);
    r.entity = rd<uint32_t>(rec, L.rec_entity);
    r.name_len = static_cast<uint32_t>(rd<uint64_t>(rec, L.rec_name + 8));
    r.account_id = rd<uint64_t>(rec, L.rec_account);
    if (g_admin_ok && L.info_stride == 0xA0) {
        uint8_t e[0xA0];
        if (guarded_copy(e, g_basic_infos.load() + static_cast<uintptr_t>(slot) * sizeof e, sizeof e)) {
            r.info_entity = rd<uint32_t>(e, 0);
            r.info_handle = rd<uint32_t>(e, L.info_handle);
            r.info_admin = rd<uint8_t>(e, L.info_admin);
            for (uint8_t b : e) r.info_nonzero += b != 0;
        }
    }
    *out = r;
    return KILN_OK;
}

KilnResult KILN_CALL t_replay_log(const KilnStr* lines, uint32_t count, KilnPlayer* out, uint32_t capacity,
                                  uint32_t* out_count, KilnPlayerEvent* events, uint32_t events_capacity,
                                  uint32_t* out_events) {
    if ((!lines && count) || (!out && capacity) || (!events && events_capacity)) return KILN_ERR_INVALID_ARGUMENT;
    LogTracker t;
    std::vector<LogEvent> ev;
    for (uint32_t i = 0; i < count; ++i)
        if (lines[i].ptr) t.feed(std::string_view(lines[i].ptr, lines[i].len), ev);
    t.flush(ev);
    uint32_t n = 0;
    for (uint32_t i = 0; i < LogTracker::kSlots; ++i) {
        if (!t.slot(i).used) continue;
        if (n < capacity) out[n] = from_log(i, t.slot(i));
        ++n;
    }
    uint32_t m = 0;
    for (const LogEvent& e : ev) {
        if (m < events_capacity) {
            KilnPlayerEvent k{};
            k.seq = m + 1;
            k.kind = static_cast<uint32_t>(e.kind);
            k.player = from_log(e.slot, e.player);
            k.player.state = e.kind == KILN_PLAYER_JOINED ? KILN_PLAYER_IN_GAME : KILN_PLAYER_LEAVING;
            events[m] = k;
        }
        ++m;
    }
    if (out_count) *out_count = n;
    if (out_events) *out_events = m;
    return (n > capacity || m > events_capacity) ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

KilnResult KILN_CALL t_probe_position(KilnEntity entity, KilnVec3* out) {
    if (!out || entity == 0) return KILN_ERR_INVALID_ARGUMENT;
    if (!on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!g_positions_ok) return KILN_ERR_UNAVAILABLE;
    int64_t fx[3];
    if (!guarded_position(g_get_transform, g_world.load(), entity, fx)) return KILN_ERR_NOT_FOUND;
    *out = KilnVec3{to_m(fx[0]), to_m(fx[1]), to_m(fx[2])};
    return KILN_OK;
}

const KilnPlayersApi kTable = {
    {sizeof(KilnPlayersApi), KILN_PLAYERS_VERSION},
    &t_status, &t_list, &t_get, &t_find_name, &t_find_steam, &t_position, &t_events, &t_last_seq,
    &t_subscribe, &t_unsubscribe, &t_raw_slot, &t_replay_log, &t_probe_position,
};

// ---------------------------------------------------------------------------------------------- setup ------------
void load_layout() {
    auto c = [](const char* n, int64_t& v) { return constant(n, &v); };
    bool rec = g_api->feature(KilnStr{"players.records", 15});
    rec = c("players/server.records", L.server_records) && c("players/server.world", L.server_world) &&
          c("players/rec.stride", L.rec_stride) && c("players/rec.count", L.rec_count) &&
          c("players/rec.state", L.rec_state) && c("players/rec.peer", L.rec_peer) &&
          c("players/rec.group", L.rec_group) && c("players/rec.permissions", L.rec_perms) &&
          c("players/rec.entity", L.rec_entity) && c("players/rec.name", L.rec_name) &&
          c("players/rec.account", L.rec_account) && rec;
    // Sanity: the fields we read must lie inside our copy and the record; slots fit the 6-bit handle field.
    rec = rec && L.rec_stride >= static_cast<int64_t>(kRecCopy) && L.rec_count > 0 && L.rec_count <= 64 &&
          L.rec_state < static_cast<int64_t>(kRecCopy) && L.rec_peer + 4 <= static_cast<int64_t>(kRecCopy) &&
          L.rec_group + 4 <= static_cast<int64_t>(kRecCopy) && L.rec_perms < static_cast<int64_t>(kRecCopy) &&
          L.rec_entity + 4 <= static_cast<int64_t>(kRecCopy) && L.rec_name + 16 <= static_cast<int64_t>(kRecCopy) &&
          L.rec_account + 8 <= static_cast<int64_t>(kRecCopy);
    L.records = rec;
    L.admin = g_api->feature(KilnStr{"players.admin", 13}) && c("players/info.stride", L.info_stride) &&
              c("players/info.max_index", L.info_max) && c("players/info.admin", L.info_admin) &&
              c("players/info.handle", L.info_handle) && L.info_stride == 0xA0 && L.info_admin < 0xA0 &&
              L.info_handle + 4 <= 0xA0;
    L.map = g_api->feature(KilnStr{"players.singleton_map", 21}) && c("players/world.singleton_map.bits", L.map_bits) &&
            c("players/world.singleton_map.capacity", L.map_cap) && c("players/world.singleton_map.keys", L.map_keys) &&
            c("players/world.singleton_map.values", L.map_values) && c("players/world.singletons", L.singletons) &&
            c("players/singleton.stride", L.sg_stride) && c("players/singleton.object", L.sg_object) &&
            c("players/singleton.hash", L.sg_hash);
    c("world.storage", L.world_storage);
    c("storage.kind", L.storage_kind);
    uintptr_t t = 0;
    if (g_api->symbol(KilnStr{"GetEntityTransform", 18}, &t) == KILN_OK) g_get_transform = t;
}

void KILN_CALL on_ready(void*) {
    // kiln.world is not needed for the player list (positions come from the core's GetEntityTransform); kiln.progression
    // registers its program with it.
    kiln::progression::ready();
    kiln::playerstate::ready();
}

}  // namespace

bool kiln::players::in_game(KilnEntity entity) {
    if (entity == 0) return false;
    std::lock_guard lock(g_snap_mutex);
    for (const KilnPlayer& p : g_snapshot)
        if (p.entity == entity && p.state == KILN_PLAYER_IN_GAME) return true;
    return false;
}

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = KilnStr{KILN_PLAYERS_SERVICE, sizeof(KILN_PLAYERS_SERVICE) - 1};
    info->version = KilnStr{"1", 1};
    info->on_ready = &on_ready;

    load_layout();
    wchar_t sim[MAX_PATH] = {};
    if (const DWORD n = GetEnvironmentVariableW(L"KILN_PLAYERS_SIMULATE", sim, MAX_PATH); n > 0 && n < MAX_PATH) {
        if (std::FILE* f = _wfopen(sim, L"rb")) {
            std::string all;
            char buf[4096];
            for (size_t k; (k = std::fread(buf, 1, sizeof buf, f)) > 0;) all.append(buf, k);
            std::fclose(f);
            size_t a = 0;
            while (a < all.size()) {
                size_t b = all.find('\n', a);
                if (b == std::string::npos) b = all.size();
                std::string l = all.substr(a, b - a);
                if (!l.empty() && l.back() == '\r') l.pop_back();
                g_sim_lines.push_back(std::move(l));
                a = b + 1;
            }
            g_sim = true;
        } else {
            logf(KILN_LOG_WARN, "players: the file named by KILN_PLAYERS_SIMULATE cannot be opened; ignored");
        }
    }
    uintptr_t reg = 0;
    if (api->symbol(KilnStr{"players/RegisterSingleton", 25}, &reg) == KILN_OK) {
        g_reg_hook.install(g_api, g_owner, reg, reinterpret_cast<void*>(&hk_register_singleton), "players/RegisterSingleton");
        if (!g_reg_hook) logf(KILN_LOG_WARN, "players: hooking RegisterSingleton at %p failed", reinterpret_cast<void*>(reg));
    }
    if (!g_reg_hook) {
        L.records = false;
        L.admin = false;
    }
    KilnHandle h = 0;
    g_log_ok = api->subscribe_log(owner, &on_log, nullptr, &h) == KILN_OK;
    api->subscribe_world(owner, &on_world, nullptr, &h);
    if (api->subscribe_tick(owner, KILN_TICK_PRE, &on_pre, nullptr, &h) != KILN_OK) {
        logf(KILN_LOG_WARN, "players: no tick on this build; service unavailable");
        return KILN_ERR_UNAVAILABLE;
    }
    if (!L.records && !g_log_ok && !g_sim) {
        logf(KILN_LOG_WARN, "players: neither the player records nor the game log work on this build; unavailable");
        return KILN_ERR_UNAVAILABLE;
    }
    logf(KILN_LOG_INFO, "players: layout %s, admin bit %s, singleton map %s, GetEntityTransform %s, log mirror %s",
         L.records ? "resolved" : "unresolved", L.admin ? "resolved" : "unresolved", L.map ? "resolved" : "unresolved",
         g_get_transform ? "resolved" : "unresolved", g_log_ok ? "subscribed" : "unavailable");
    kiln::progression::main(api, owner);  // kiln.progression (docs/kiln/services/progression.md)
    kiln::playerstate::main(api, owner);  // kiln.playerstate (docs/kiln/services/player-state.md)
    return api->register_service(owner, KilnStr{KILN_PLAYERS_SERVICE, sizeof(KILN_PLAYERS_SERVICE) - 1}, &kTable);
}
