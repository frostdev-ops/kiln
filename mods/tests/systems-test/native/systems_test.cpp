// SPDX-License-Identifier: MIT
// systems-test (native part): the lab test of kiln.world's game-program surface (v4, chapter 06a) through its C table,
// as a native consumer sees it. A plugin for a lab server only: copy kiln-systems-test.dll to <lab>/kiln/plugins/ and
// create <lab>/kiln/control/systems.parallel (the lab parallel switch). Every line it writes starts with
// "systems-test:"; the last one is "systems-test: done, N PASS, M FAIL". No player is needed: the switch forces the
// server world's parallel path (players mask bit 15) for bounded windows.
//
// Schedule (steps from the first Pre that has the server world's directory):
//   10    find: the directory (817 game programs, 197 of them client programs with no update in the server world)
//   20    kiln.types system hooks on a few entity programs (they count the threads their slices ran on)
//   20-   serial window, then the switch on for 300 steps (the parallel window), then serial again
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "kiln_api.h"
#include "services/kiln_types.h"
#include "services/kiln_world.h"

namespace {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
const KilnWorldApi* W = nullptr;
const KilnTypesApi* T = nullptr;
int g_pass = 0, g_fail = 0;
uint64_t g_start = 0;  // the step of the first Pre with a directory
bool g_done = false;
DWORD g_tick_tid = 0;

KilnStr ks(const char* s) { return KilnStr{s, std::strlen(s)}; }

void say(const char* fmt, ...) {
    char b[1600];
    int n = std::snprintf(b, sizeof b, "systems-test: ");
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(b + n, sizeof b - n, fmt, ap);
    va_end(ap);
    g_api->log(g_owner, KILN_LOG_INFO, KilnStr{b, std::strlen(b)});
}

void row(bool ok, const char* fmt, ...) {
    char b[1400];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(b, sizeof b, fmt, ap);
    va_end(ap);
    (ok ? g_pass : g_fail)++;
    say("%s %s", ok ? "PASS" : "FAIL", b);
}

KilnGameProgramsStatus status() {
    KilnGameProgramsStatus s{};
    s.size = sizeof s;
    W->game_programs_status(&s);
    return s;
}

// ---- threads seen by a slice callback (kiln.types system hooks; later the wraps' own stages) ----
struct Threads {
    std::atomic<uint32_t> tids[16]{};
    std::atomic<uint64_t> calls{0};
    void add(uint32_t tid) {
        calls.fetch_add(1, std::memory_order_relaxed);
        for (auto& t : tids) {
            uint32_t cur = t.load(std::memory_order_relaxed);
            if (cur == tid) return;
            if (cur == 0 && t.compare_exchange_strong(cur, tid)) return;
            if (cur == tid) return;
        }
    }
    int count() const {
        int n = 0;
        for (auto& t : tids) n += t.load() != 0;
        return n;
    }
    bool off(uint32_t tick) const {
        for (auto& t : tids)
            if (t.load() && t.load() != tick) return true;
        return false;
    }
    void reset() {
        for (auto& t : tids) t = 0;
        calls = 0;
    }
};

struct Hooked {
    const char* name;
    KilnHandle handle = 0;
    Threads threads;
};
Hooked g_hooked[] = {{"is_actor_in_shape"}, {"despawn_link"}, {"update_selected_targets_transforms"},
                     {"selected_target_flags"}, {"move_colliders"}, {"server_slot_attachment"},
                     {"environment_sensor"}, {"impact_prepare"}, {"network_level_attribute"},
                     {"base_comfort"}, {"fuel_setup"}, {"fuel_network"}};

void KILN_CALL hook_before(void*, void* user) { static_cast<Hooked*>(user)->threads.add(GetCurrentThreadId()); }

// One window's census over the hooked programs: how many ran, how many ran off the tick thread, distinct threads.
struct Census {
    int ran = 0, off = 0, threads = 0;
    uint64_t calls = 0;
};
Census census(bool reset) {
    Census c;
    std::vector<uint32_t> all;
    for (auto& h : g_hooked) {
        if (!h.handle || !h.threads.calls.load()) continue;
        ++c.ran;
        c.calls += h.threads.calls.load();
        if (h.threads.off(g_tick_tid)) ++c.off;
        for (auto& t : h.threads.tids)
            if (const uint32_t id = t.load(); id && std::find(all.begin(), all.end(), id) == all.end()) all.push_back(id);
        if (reset) h.threads.reset();
    }
    c.threads = static_cast<int>(all.size());
    return c;
}

// ---- S1: the directory ----
void find_rows() {
    const auto s = status();
    int round_trip = 0, bad = 0, game = 0, no_update = 0, rva_bad = 0;
    bool cet = false;
    for (int32_t i = 0; i < static_cast<int32_t>(s.programs[KILN_WORLD_SERVER]); ++i) {
        KilnGameProgramInfo info{};
        info.size = sizeof info;
        if (W->game_program_info(KILN_WORLD_SERVER, i, &info) != KILN_OK) {
            ++bad;
            continue;
        }
        if (W->game_program_find(KILN_WORLD_SERVER, info.name) == i) ++round_trip;
        if (info.flags & KILN_GAME_PROGRAM_GAME) {
            ++game;
            if (info.flags & KILN_GAME_PROGRAM_NO_UPDATE) ++no_update;
            else if (!info.update_rva) ++rva_bad;  // a game update lives in the game's image
        }
        if (std::string(info.name.ptr, info.name.len) == "client_event_tracking")
            cet = (info.flags & KILN_GAME_PROGRAM_NO_UPDATE) && (info.desc_flags & 1);
    }
    row(s.directory[KILN_WORLD_SERVER] && game == 817 && no_update == 197 && s.game_programs[0] == 817 &&
            s.no_update[0] == 197,
        "find: %d game programs, %d client-only (status %u/%u; %u programs in the table)", game, no_update,
        s.game_programs[0], s.no_update[0], s.programs[0]);
    row(round_trip == static_cast<int>(s.programs[0]) && bad == 0 && rva_bad == 0,
        "find: every name of the table finds its own index (%d of %u), every game update has an rva", round_trip,
        s.programs[0]);
    row(cet, "find: client_event_tracking (the tail-jump registration) is a client program with no update here");
    row(W->game_program_find(KILN_WORLD_SERVER, ks("no_such_program")) == -1 &&
            W->game_program_find(KILN_WORLD_PREDICTION, ks("fuel_setup")) == -1 &&
            W->game_program_find(KILN_WORLD_CLIENT, ks("fuel_setup")) == -1,
        "find: unknown names and the world kinds a dedicated server has no table for answer -1");
}

void hook_programs() {
    int ok = 0;
    for (auto& h : g_hooked) {
        const int32_t i = T->system_find(ks(h.name));
        if (i < 0 || T->system_hook(g_owner, static_cast<uint32_t>(i), &hook_before, nullptr, &h, &h.handle) != KILN_OK) {
            say("note: no kiln.types hook on %s", h.name);
            h.handle = 0;
            continue;
        }
        ++ok;
    }
    say("kiln.types hooks on %d of %zu entity programs (they count the threads their slices ran on)", ok,
        sizeof g_hooked / sizeof g_hooked[0]);
}

// The parallel switch window (S1): serial, forced, serial.
struct {
    Census serial, forced, after;
    uint16_t mask_before = 0xFFFF, mask_forced = 0xFFFF, mask_after = 0xFFFF;
    bool forced_flag = false;
} g_switch;

void KILN_CALL on_pre(const KilnTickInfo* tick, void*) {
    if (g_done || !W) return;
    g_tick_tid = GetCurrentThreadId();
    if (!g_start) {
        if (!status().directory[KILN_WORLD_SERVER]) return;
        g_start = tick->step;
        say("start at step %llu", static_cast<unsigned long long>(g_start));
    }
    const uint64_t n = tick->step - g_start;
    switch (n) {
        case 10: find_rows(); break;
        case 20:
            if (T) hook_programs();
            census(true);
            break;
        case 260:  // serial window: 240 steps
            g_switch.serial = census(true);
            g_switch.mask_before = status().players_mask;
            row(W->lab_parallel(1, 0) == KILN_ERR_INVALID_ARGUMENT && W->lab_parallel(1, 3001) == KILN_ERR_INVALID_ARGUMENT,
                "parallel switch refuses windows outside 1..3000 steps");
            row(W->lab_parallel(1, 300) == KILN_OK, "parallel switch: on for 300 steps");
            break;
        case 262: {
            const auto s = status();
            g_switch.mask_forced = s.players_mask;
            g_switch.forced_flag = s.parallel_forced;
            census(true);  // the step that read the mask before it was set
            break;
        }
        case 560: g_switch.forced = census(true); break;  // 298 steps with the bit set
        case 600: {
            const auto s = status();
            g_switch.mask_after = s.players_mask;
            census(true);
            row(!s.parallel_forced && s.players_mask == g_switch.mask_before,
                "parallel switch lets go by itself after its 300 steps: mask 0x%04x", s.players_mask);
            break;
        }
        case 840: {
            g_switch.after = census(true);
            const auto& w = g_switch;
            row(w.mask_before == 0 && w.mask_forced == 0x8000 && w.forced_flag && w.forced.off >= 1 && w.forced.threads > 1 &&
                    w.serial.off == 0 && w.after.off == 0 && w.serial.ran > 0,
                "parallel switch: mask 0x%04x -> 0x%04x, %d programs off the tick thread (%d of the %d hooked ran, on %d "
                "threads); serial before %d off of %d on %d thread(s), after %d off of %d",
                w.mask_before, w.mask_forced, w.forced.off, w.forced.ran, static_cast<int>(sizeof g_hooked / sizeof g_hooked[0]),
                w.forced.threads, w.serial.off, w.serial.ran, w.serial.threads, w.after.off, w.after.ran);
            for (auto& h : g_hooked)
                if (h.handle) T->system_unhook(h.handle), h.handle = 0;
            break;
        }
        case 850:
            g_done = true;
            say("done, %d PASS, %d FAIL", g_pass, g_fail);
            break;
        default: break;
    }
}

}  // namespace

extern "C" __declspec(dllexport) KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (!api || api->abi_version != KILN_ABI_VERSION) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = ks("systems-test");
    info->version = ks("0.1.0");
    if (info->size >= offsetof(KilnPluginInfo, on_ready) + sizeof(info->on_ready))
        info->on_ready = [](void*) {
            W = static_cast<const KilnWorldApi*>(g_api->service(ks(KILN_WORLD_SERVICE), 4));
            T = static_cast<const KilnTypesApi*>(g_api->service(ks(KILN_TYPES_SERVICE), 1));
            if (!W || W->header.size < offsetof(KilnWorldApi, lab_parallel) + sizeof(void*)) {
                W = nullptr;
                say("FAIL kiln.world v4 is not here");
                say("done, 0 PASS, 1 FAIL");
                return;
            }
            if (!T || T->header.size < offsetof(KilnTypesApi, system_unhook) + sizeof(void*)) T = nullptr;
            say("kiln.world v4 found (table %u bytes); kiln.types %s", W->header.size, T ? "found" : "missing");
        };
    KilnHandle h = 0;
    api->subscribe_tick(owner, KILN_TICK_PRE, &on_pre, nullptr, &h);
    return KILN_OK;
}
