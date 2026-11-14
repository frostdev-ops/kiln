// SPDX-License-Identifier: MIT
// systems-test (native part): the lab test of kiln.world's game-program surface (v4, chapter 06a) through its C table,
// as a native consumer sees it. A plugin for a lab server only: copy kiln-systems-test.dll to <lab>/kiln/plugins/ and
// create <lab>/kiln/control/systems.parallel (the lab parallel switch). Every line it writes starts with
// "systems-test:"; the last one is "systems-test: done, N PASS, M FAIL". No player is needed: the switch forces the
// server world's parallel path (players mask bit 15) for bounded windows.
//
// Schedule (steps from the first Pre, at world step 100 or later, where the world has its directory and has loaded
// at least 10 entities with Health):
//   10        find: the directory (817 game programs, 197 of them client programs with no update in the server world)
//   20-840    the switch: kiln.types system hooks count the threads of a few entity programs' slices; serial, then
//             the switch on for 300 steps (the parallel window), then serial again
//   0         an After wrap on the INIT program init_spawn_time, made at the world's first Pre with a directory (it runs
//             while the world creates its entities)
//   900-1260  wraps, serial: Before/After on fuel_network, Replace on fuel_setup (a kiln.types hook on its update counts
//             the game's runs), a stage that faults, a second Replace; then every wrap goes and the table holds the
//             game's own updates again
//   1300-3712 wraps with the switch on (2,600 steps): Before/After on 4 entity programs (threads, index, counters), then
//             the stress: 4 wraps on entity programs made and removed every 50 steps for 2,000 steps
//   3720-4030 kiln.types under wraps: the profiler still names a wrapped program (wrap_original), and a kiln.types
//             system hook on a wrapped program's update still runs once per slice
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

// ---- S2: wraps ----
std::atomic<uint64_t> g_bad_before{0}, g_bad_after{0};

// What one wrap stage saw: slices, entities, threads, the world thread or not.
struct Seen {
    std::atomic<uint64_t> slices{0}, entities{0}, worker{0};
    Threads threads;
    KilnHandle handle = 0;
    void reset() {
        slices = 0, entities = 0, worker = 0;
        threads.reset();
    }
};
uint32_t ctx_index(const KilnGameSlice* s) { return reinterpret_cast<const uint32_t*>(s->program_ctx)[2]; }
void KILN_CALL st_before(KilnGameSlice* s, void* user) {
    auto* seen = static_cast<Seen*>(user);
    seen->slices.fetch_add(1, std::memory_order_relaxed);
    seen->entities.fetch_add(s->count, std::memory_order_relaxed);
    if (!s->thread_is_world) seen->worker.fetch_add(1, std::memory_order_relaxed);
    seen->threads.add(GetCurrentThreadId());
    if (ctx_index(s) != 0) g_bad_before.fetch_add(1);
}
void KILN_CALL st_after(KilnGameSlice* s, void* user) {
    auto* seen = static_cast<Seen*>(user);
    seen->slices.fetch_add(1, std::memory_order_relaxed);
    seen->entities.fetch_add(s->count, std::memory_order_relaxed);
    if (!s->thread_is_world) seen->worker.fetch_add(1, std::memory_order_relaxed);
    seen->threads.add(GetCurrentThreadId());
    const uint32_t i = ctx_index(s);
    if (i != 0 && i < s->count) g_bad_after.fetch_add(1);
}
void KILN_CALL st_replace(KilnGameSlice* s, void* user) {
    auto* seen = static_cast<Seen*>(user);
    seen->slices.fetch_add(1, std::memory_order_relaxed);
    seen->entities.fetch_add(s->count, std::memory_order_relaxed);
    // the slice is Kiln's: nothing runs, the index stays 0 (the runner accepts it)
}
void KILN_CALL st_fault(KilnGameSlice*, void* user) {
    static_cast<Seen*>(user)->slices.fetch_add(1);
    *reinterpret_cast<volatile int*>(static_cast<uintptr_t>(0x10)) = 1;  // a native fault, on purpose
}
std::atomic<uint64_t> g_hook_runs{0};
void KILN_CALL hook_count(void*, void*) { g_hook_runs.fetch_add(1, std::memory_order_relaxed); }

int32_t prog(const char* name) { return W->game_program_find(KILN_WORLD_SERVER, ks(name)); }
KilnHandle wrap(const char* program, int32_t stage, KilnWrapStageFn fn, void* user, KilnResult* rc = nullptr) {
    KilnHandle h = 0;
    const KilnResult r = W->game_program_wrap(g_owner, KILN_WORLD_SERVER, prog(program), stage, 0, fn, user, &h);
    if (rc) *rc = r;
    if (r != KILN_OK && !rc) say("note: wrap of %s refused (%d)", program, r);
    return h;
}
KilnGameWrapInfo winfo(KilnHandle h) {
    KilnGameWrapInfo i{};
    i.size = sizeof i;
    W->game_wrap_info(h, &i);
    return i;
}
KilnGameProgramInfo pinfo(const char* name) {
    KilnGameProgramInfo i{};
    i.size = sizeof i;
    W->game_program_info(KILN_WORLD_SERVER, prog(name), &i);
    return i;
}

// Every entry of the server world's table holds its descriptor's update (read from the table itself).
void restore_row(const char* when, int wrapped = 0) {
    int64_t table = -1, stride = -1, count_at = -1;
    g_api->constant(ks("world/programs.table"), &table);
    g_api->constant(ks("world/programs.stride"), &stride);
    g_api->constant(ks("world/programs.count"), &count_at);
    const uintptr_t world = g_api->world(KILN_WORLD_SERVER);
    if (table < 0 || stride <= 0 || count_at < 0 || !world) {
        row(false, "restore (%s): the table's constants are not here", when);
        return;
    }
    const uint16_t n = *reinterpret_cast<const uint16_t*>(world + count_at);
    int same = 0, differ = 0, ours = 0;
    for (uint16_t i = 0; i < n; ++i) {
        const uintptr_t e = world + table + i * stride;
        const auto* desc = *reinterpret_cast<const uint8_t* const*>(e);
        const uintptr_t update = *reinterpret_cast<const uintptr_t*>(e + 0x28);
        const uintptr_t own = desc ? *reinterpret_cast<const uintptr_t*>(desc + 0x10) : 0;
        (update == own ? same : differ)++;
        if (W->wrap_original(e)) ++ours;
    }
    row(differ == wrapped && ours == wrapped, "restore (%s): entry+0x28 equals the descriptor's update for %d of %u programs, "
        "%d differ, %d kiln.world trampolines (%d expected)", when, same, n, differ, ours, wrapped);
}

struct {
    Seen fn_before, fn_after, replace, init, fault, fault_after;
    KilnHandle hook = 0;
    uint64_t hook_during = 0, hook_after = 0;
    Seen forced[4][2];
    const char* forced_names[4] = {"is_actor_in_shape", "despawn_link", "impact_prepare", "move_colliders"};
    std::vector<KilnHandle> stress;
    Seen stress_seen[4][2];
    uint64_t stress_slices = 0, stress_cycles = 0, stress_refused = 0;
    uint64_t stress_faults = 0;
} g_w;

// ---- S3: kiln.types under wraps ----
struct {
    Seen setup, net_before;
    KilnHandle hook = 0;
    uint64_t calls0 = 0;
} g_id;
uint64_t profiled_calls(const char* name) {
    const int32_t i = T ? T->system_find(ks(name)) : -1;
    KilnSystemInfo si{};
    si.size = sizeof si;
    return i >= 0 && T->system_info(static_cast<uint32_t>(i), &si) == KILN_OK ? si.calls : 0;
}
void identity_phase(uint64_t n) {
    auto& d = g_id;
    switch (n) {
        case 3720: {
            const bool types_ok = T && T->header.size >= offsetof(KilnTypesApi, external_runner) + sizeof(void*);
            row(types_ok && T->external_runner() == 0, "kiln.types: external_runner %u (no native SET here)",
                types_ok ? T->external_runner() : 99u);
            if (!T) break;
            T->profile(true);
            d.setup.handle = wrap("fuel_setup", KILN_WRAP_BEFORE, &st_before, &d.setup);
            d.net_before.handle = wrap("fuel_network", KILN_WRAP_BEFORE, &st_before, &d.net_before);
            const int32_t i = T->system_find(ks("fuel_network"));
            if (i < 0 || T->system_hook(g_owner, static_cast<uint32_t>(i), &hook_count, nullptr, nullptr, &d.hook) != KILN_OK)
                d.hook = 0;
            break;
        }
        case 3722:  // both wraps live since the last Pre: count from here
            d.setup.reset();
            d.net_before.reset();
            g_hook_runs = 0;
            d.calls0 = profiled_calls("fuel_setup");
            break;
        case 4020: {
            if (!T) break;
            const uint64_t calls = profiled_calls("fuel_setup") - d.calls0, slices = d.setup.slices.load();
            row(slices > 0 && calls == slices && winfo(d.setup.handle).state == KILN_WRAP_ACTIVE,
                "identity: kiln.types' profiler names the wrapped fuel_setup: %llu calls, %llu wrapped slices",
                static_cast<unsigned long long>(calls), static_cast<unsigned long long>(slices));
            const uint64_t hooked = g_hook_runs.load(), wrapped = d.net_before.slices.load();
            row(d.hook && wrapped > 0 && hooked == wrapped,
                "GameSystems.Hook callbacks still run under a wrap: fuel_network's kiln.types hook ran %llu times, its "
                "wrap's Before %llu", static_cast<unsigned long long>(hooked), static_cast<unsigned long long>(wrapped));
            W->game_program_unwrap(d.setup.handle);
            W->game_program_unwrap(d.net_before.handle);
            if (d.hook) T->system_unhook(d.hook), d.hook = 0;
            T->profile(false);
            break;
        }
        case 4022: restore_row("the end"); break;
        case 4030:
            g_done = true;
            say("done, %d PASS, %d FAIL", g_pass, g_fail);
            break;
        default: break;
    }
}

void wraps_phase(uint64_t n) {
    auto& w = g_w;
    switch (n) {
        case 900: {
            const auto s0 = status();
            g_bad_before = g_bad_after = 0;
            w.fn_before.handle = wrap("fuel_network", KILN_WRAP_BEFORE, &st_before, &w.fn_before);
            w.fn_after.handle = wrap("fuel_network", KILN_WRAP_AFTER, &st_after, &w.fn_after);
            w.replace.handle = wrap("fuel_setup", KILN_WRAP_REPLACE, &st_replace, &w.replace);
            KilnResult rc = 0;
            KilnHandle second = 0;
            const KilnResult r2 = W->game_program_wrap(g_owner + 1000, KILN_WORLD_SERVER, prog("fuel_setup"),
                                                       KILN_WRAP_REPLACE, 0, &st_replace, nullptr, &second);
            const auto si = winfo(second);
            row(r2 == KILN_OK && second && si.state == KILN_WRAP_REFUSED && si.reason == KILN_WRAP_REPLACE_TAKEN,
                "a second Replace of fuel_setup is refused: state %u, reason %u, \"%s\"", si.state, si.reason, si.detail);
            (void)rc;
            w.fault.handle = wrap("move_colliders", KILN_WRAP_BEFORE, &st_fault, &w.fault);
            w.fault_after.handle = wrap("move_colliders", KILN_WRAP_AFTER, &st_after, &w.fault_after);
            if (T) {
                const int32_t i = T->system_find(ks("fuel_setup"));
                if (i < 0 || T->system_hook(g_owner, static_cast<uint32_t>(i), &hook_count, nullptr, nullptr, &w.hook) != KILN_OK)
                    w.hook = 0;
            }
            say("wraps made (pending %u, active %u before the Pre)", s0.wraps_pending, s0.wraps_active);
            break;
        }
        case 901: {
            const auto s = status();
            row(winfo(w.fn_before.handle).state == KILN_WRAP_ACTIVE && winfo(w.replace.handle).state == KILN_WRAP_ACTIVE &&
                    winfo(w.init.handle).state == KILN_WRAP_ACTIVE && s.wraps_active == 6 && s.wraps_pending == 0,
                "wraps active after one Pre: %u active, %u pending", s.wraps_active, s.wraps_pending);
            g_hook_runs = 0;
            break;
        }
        case 1200: {
            w.hook_during = g_hook_runs.load();
            const uint64_t b = w.fn_before.slices, a = w.fn_after.slices;
            row(b > 0 && a == b && g_bad_before == 0 && g_bad_after == 0 && pinfo("fuel_network").slices == b,
                "wrap before/after fuel_network: %llu slices (%llu entities), before == after == slices, 0 bad index",
                static_cast<unsigned long long>(b), static_cast<unsigned long long>(w.fn_before.entities.load()));
            row(w.hook && w.replace.slices > 0 && w.hook_during == 0 && pinfo("fuel_setup").slices == w.replace.slices,
                "replace fuel_setup: original ran %llu times in 300 steps (%llu slices replaced), server stepped on",
                static_cast<unsigned long long>(w.hook_during), static_cast<unsigned long long>(w.replace.slices.load()));
            const auto fi = winfo(w.fault.handle);
            row(fi.state == KILN_WRAP_ACTIVE && fi.disabled == KILN_WRAP_FAULT && fi.faults == 1 && w.fault.slices == 1 &&
                    fi.fault_code == 0xC0000005u && w.fault_after.slices > 1 && g_bad_after == 0,
                "a native fault in a stage disables only that stage: faults %llu (0x%08x), the After on the same program ran "
                "%llu slices since", static_cast<unsigned long long>(fi.faults), fi.fault_code,
                static_cast<unsigned long long>(w.fault_after.slices.load()));
            break;
        }
        case 1201:
            for (Seen* s : {&w.fn_before, &w.fn_after, &w.replace, &w.fault, &w.fault_after})
                W->game_program_unwrap(s->handle);
            break;
        case 1202: restore_row("fuel_setup, fuel_network and move_colliders unwrapped; the INIT wrap stays", 1); break;
        case 1250: {
            w.hook_after = g_hook_runs.load() - w.hook_during;
            const KilnGameProgramInfo ii = pinfo("init_spawn_time");
            row((ii.desc_flags & 8) && w.init.slices > 0, "INIT wrap: init_spawn_time (flags 0x%x) After ran %llu slices",
                ii.desc_flags, static_cast<unsigned long long>(w.init.slices.load()));
            row(w.hook_after > 0, "fuel_setup's own update runs again once its Replace is gone (%llu runs in 48 steps)",
                static_cast<unsigned long long>(w.hook_after));
            W->game_program_unwrap(w.init.handle);
            if (w.hook) T->system_unhook(w.hook), w.hook = 0;
            break;
        }
        case 1252: restore_row("every wrap gone"); break;
        case 1300:
            row(W->lab_parallel(1, 2600) == KILN_OK, "parallel switch on for 2,600 steps (threads and stress)");
            g_bad_before = g_bad_after = 0;
            for (int i = 0; i < 4; ++i) {
                w.forced[i][0].handle = wrap(w.forced_names[i], KILN_WRAP_BEFORE, &st_before, &w.forced[i][0]);
                w.forced[i][1].handle = wrap(w.forced_names[i], KILN_WRAP_AFTER, &st_after, &w.forced[i][1]);
            }
            break;
        case 1302:
            for (auto& p : w.forced) p[0].reset(), p[1].reset();
            break;
        case 1700: {
            int off = 0, threads = 0, consistent = 0;
            uint64_t slices = 0, workers = 0;
            std::vector<uint32_t> all;
            for (auto& p : w.forced) {
                slices += p[0].slices;
                workers += p[0].worker;
                if (p[0].threads.off(g_tick_tid)) ++off;
                if (p[0].slices == p[1].slices) ++consistent;
                for (auto& t : p[0].threads.tids)
                    if (const uint32_t id = t.load(); id && std::find(all.begin(), all.end(), id) == all.end()) all.push_back(id);
            }
            threads = static_cast<int>(all.size());
            row(status().parallel_forced && threads > 1 && off >= 1 && workers > 0 && consistent == 4 && g_bad_before == 0 &&
                    g_bad_after == 0,
                "threads (forced): wrapped slices ran on %d threads (%d of 4 programs off the tick thread, %llu of %llu "
                "slices on workers), 0 bad index, Before == After on %d of 4 programs", threads, off,
                static_cast<unsigned long long>(workers), static_cast<unsigned long long>(slices), consistent);
            for (auto& p : w.forced) W->game_program_unwrap(p[0].handle), W->game_program_unwrap(p[1].handle);
            g_bad_before = g_bad_after = 0;
            break;
        }
        default:
            if (n > 1700 && n <= 3700) {  // the stress: 4 wraps made and removed every 50 steps
                if ((n - 1701) % 50 == 0) {
                    if (w.stress.empty()) {
                        for (int i = 0; i < 4; ++i)
                            for (int k = 0; k < 2; ++k) {
                                KilnResult rc = 0;
                                const KilnHandle h = wrap(w.forced_names[i], k ? KILN_WRAP_AFTER : KILN_WRAP_BEFORE,
                                                          k ? &st_after : &st_before, &w.stress_seen[i][k], &rc);
                                if (rc == KILN_OK) w.stress.push_back(h);
                                else ++w.stress_refused;
                            }
                    } else {
                        for (KilnHandle h : w.stress) {
                            const auto i = winfo(h);
                            w.stress_faults += i.faults;
                            W->game_program_unwrap(h);
                        }
                        w.stress.clear();
                        ++w.stress_cycles;
                    }
                }
            } else if (n == 3710) {
                uint64_t b = 0, a = 0;
                bool equal = true;
                for (auto& p : w.stress_seen) {
                    b += p[0].slices, a += p[1].slices;
                    equal &= p[0].slices == p[1].slices;
                }
                const auto s = status();
                row(equal && b > 0 && w.stress_faults == 0 && w.stress_refused == 0 && g_bad_before == 0 && g_bad_after == 0 &&
                        s.wraps_active == 0 && s.wraps_pending == 0 && s.parallel_forced,
                    "stress (forced): 4 programs wrapped and unwrapped every 50 steps for 2,000 steps (%llu cycles): %llu "
                    "slices, Before == After on each, 0 faults, 0 bad index, 0 wraps left", static_cast<unsigned long long>(w.stress_cycles),
                    static_cast<unsigned long long>(b));
                W->lab_parallel(0, 0);
            } else if (n == 3712) {
                restore_row("after the stress");
            } else if (n >= 3720) {
                identity_phase(n);
            }
            break;
    }
}

void KILN_CALL on_pre(const KilnTickInfo* tick, void*) {
    if (g_done || !W) return;
    g_tick_tid = GetCurrentThreadId();
    if (!g_w.init.handle && status().directory[KILN_WORLD_SERVER]) {
        // INIT programs run for archetype groups that gain entities: wrap one while the world loads its entities
        g_w.init.handle = wrap("init_spawn_time", KILN_WRAP_AFTER, &st_after, &g_w.init);
        say("INIT wrap on init_spawn_time made at world step %llu", static_cast<unsigned long long>(tick->step));
    }
    if (!g_start) {
        // The world's entities load a while after its first step (no player); the managed part uses the switch at
        // world steps 60-64. Start after both.
        if (tick->step < 100 || tick->step % 25 != 0 || !status().directory[KILN_WORLD_SERVER]) return;
        uint32_t health = 0;
        W->find(W->component_hash(ks("Health")), nullptr, 0, &health);
        if (health < 10) return;
        g_start = tick->step;
        say("start at step %llu (%u entities with Health)", static_cast<unsigned long long>(g_start), health);
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
        default:
            if (n >= 850) wraps_phase(n);
            break;
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
            if (!W || W->header.size < offsetof(KilnWorldApi, wrap_original) + sizeof(void*)) {
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
