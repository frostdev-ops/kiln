// SPDX-License-Identifier: MIT
// Opt-in self-test of the program API (labs only): enabled when <kiln>/data/kiln.world/selftest exists at boot.
// Registers three programs through the public table paths and checks, inside their updates, that every iterator slot
// kiln.world computed points at the same component GetComponent returns; then exercises queue_add / queue_remove on
// a prop (the hidden, do_not_save Teleport component, which the engine ignores on entities without TeleportCount).
// Results: "world selftest: PASS|FAIL ..." lines in kiln.log.
#include <filesystem>

#include "world_internal.h"

namespace kw {
namespace {

constexpr uint32_t kCT = 0x027ac564u, kLT = 0x4b6abcf9u, kTC = 0xbffe34efu, kTeleport = 0x23384d40u;

bool g_enabled = false;
KilnHandle g_each = 0, g_raw = 0, g_sys = 0;
int32_t s_ct = -1, s_lt = -1, s_tc = -1, s_npc = -1, s_raw_ct = -1, s_raw_tc = -1, s_sys_grid = -1, s_sys_other = -1;
uint32_t g_npc_hash = 0;
std::atomic<uint64_t> each_ok{0}, each_bad{0}, raw_ok{0}, raw_bad{0}, sys_ok{0}, sys_bad{0}, sys_calls{0};
std::atomic<uint32_t> g_sample_entity{0};
std::atomic<int> g_queue_phase{0};  // 0 idle, 1 add requested, 2 added (Pre checks), 3 remove requested, 4 removed
std::atomic<uint32_t> g_prop{0};
bool g_reported = false;
uint64_t g_step0 = 0;
std::atomic<int> g_watchdog{0};  // 0 not started, 1 waiting, 2 PASS, 3 FAIL
uint64_t g_watchdog_step = 0;

void KILN_CALL watchdog_cb(const KilnTeleportResult* r, void*) {
    const bool ok = r->status == KILN_TELEPORT_FAILED && (r->flags & KILN_TELEPORT_TIMED_OUT) &&
                    !component(r->entity, kTeleport, nullptr);
    KW_I("world selftest: %s watchdog (forced stuck teleport of prop %u: status %u, flags 0x%x, %u steps, Teleport "
         "present now: %d; reason: %.*s)",
         ok ? "PASS" : "FAIL", r->entity, r->status, r->flags, r->steps, component(r->entity, kTeleport, nullptr) != nullptr,
         static_cast<int>(r->reason.len), r->reason.ptr);
    g_watchdog.store(ok ? 2 : 3);
}

bool same(const void* a, const void* b) { return a == b; }

void KILN_CALL each_fn(KilnProgramCtx*, uint64_t* it, KilnEntity e, void*) {
    if (each_ok + each_bad > 20000) return;
    uint32_t sz = 0;
    bool ok = same(reinterpret_cast<const void*>(it[s_ct]), component(e, kCT, &sz)) &&
              same(reinterpret_cast<const void*>(it[s_lt]), component(e, kLT, &sz)) &&
              same(reinterpret_cast<const void*>(it[s_tc]), component(e, kTC, &sz)) &&
              same(reinterpret_cast<const void*>(it[s_npc]), component(e, g_npc_hash, &sz));
    (ok ? each_ok : each_bad).fetch_add(1);
    if (it[s_tc]) g_sample_entity.store(e, std::memory_order_relaxed);
}

void KILN_CALL raw_fn(KilnProgramCtx* ctx, void*) {
    alignas(16) uint64_t it[64];
    if (iter_begin(ctx, it, sizeof it) != KILN_OK) return;
    while (iter_next(ctx, it, sizeof it)) {  // all entities, always
        if (raw_ok + raw_bad > 20000) continue;
        const KilnEntity e = current_entity(ctx);
        uint32_t sz = 0;
        const bool ok = e && same(reinterpret_cast<const void*>(it[s_raw_ct]), component(e, kCT, &sz)) &&
                        same(reinterpret_cast<const void*>(it[s_raw_tc]), component(e, kTC, &sz));
        (ok ? raw_ok : raw_bad).fetch_add(1);
    }
}

void KILN_CALL sys_fn(KilnProgramCtx* ctx, uint64_t* it, KilnEntity e, void*) {
    sys_calls.fetch_add(1);
    if (e != 0 || current_entity(ctx) != 0) sys_bad.fetch_add(1);
    if (sys_ok + sys_bad < 200) {
        const bool grid = it[s_sys_grid] != 0;
        const uint32_t sample = g_sample_entity.load(std::memory_order_relaxed);
        bool other = true;
        if (sample) {
            uint32_t a = 0, b = 0;
            const void* via = read_other(ctx, it[s_sys_other], sample, &a);
            other = via && via == component(sample, kCT, &b) && a == b;
        }
        (grid && other ? sys_ok : sys_bad).fetch_add(1);
    }
    // queue_add / queue_remove on a prop.
    const int phase = g_queue_phase.load();
    const uint32_t prop = g_prop.load();
    if (phase == 1 && prop) {
        uint8_t payload[80] = {};
        uint32_t sz = 0;
        if (const void* ct = component(prop, kCT, &sz); ct && sz >= 56) std::memcpy(payload, ct, 56);
        payload[0x48] = 1;  // applied
        if (queue_add(ctx, prop, kTeleport, payload, 80) == KILN_OK) g_queue_phase.store(2);
    } else if (phase == 3 && prop) {
        if (queue_remove(ctx, prop, kTeleport) == KILN_OK) g_queue_phase.store(4);
    }
}

KilnNames names(const KilnStr* s, uint32_t n) { return KilnNames{s, n, 0}; }

}  // namespace

void selftest_init() {
    char buf[1024];
    size_t len = 0;
    if (g_api->path(KILN_PATH_DATA, buf, sizeof buf, &len) != KILN_OK) return;
    std::error_code ec;
    const std::u8string dir(reinterpret_cast<const char8_t*>(buf), len);
    const auto marker = std::filesystem::path(dir) / "kiln.world" / "selftest";
    if (!std::filesystem::exists(marker, ec)) return;
    g_enabled = true;
    g_npc_hash = component_hash("NpcCommon");
    KW_I("world selftest enabled");

    static const KilnStr read_each[] = {kstr("LastTransform"), kstr("CurrentTransform")};  // unsorted on purpose
    static const KilnStr read_each_opt[] = {kstr("TeleportCount"), kstr("NpcCommon")};
    KilnProgramDesc d{};
    d.size = sizeof d;
    d.flags = KILN_PROGRAM_HOST_ONLY;
    d.name = kstr("kiln_world_selftest_each");
    d.each = &each_fn;
    d.read = names(read_each, 2);
    d.read_optional = names(read_each_opt, 2);
    if (register_program(g_owner, &d, &g_each, false) == KILN_OK) {
        s_ct = program_slot(g_each, KILN_SLOT_READ, kstr("CurrentTransform"));
        s_lt = program_slot(g_each, KILN_SLOT_READ, kstr("LastTransform"));
        s_tc = program_slot(g_each, KILN_SLOT_READ, kstr("TeleportCount"));
        s_npc = program_slot(g_each, KILN_SLOT_READ, kstr("NpcCommon"));
        KW_I("world selftest: each slots CurrentTransform=%d LastTransform=%d NpcCommon=%d TeleportCount=%d", s_ct,
             s_lt, s_npc, s_tc);
    }

    static const KilnStr read_raw[] = {kstr("TeleportCount"), kstr("CurrentTransform")};
    KilnProgramDesc r{};
    r.size = sizeof r;
    r.flags = KILN_PROGRAM_HOST_ONLY | KILN_PROGRAM_CHUNKED;
    r.name = kstr("kiln_world_selftest_raw");
    r.update = &raw_fn;
    r.read = names(read_raw, 2);
    if (register_program(g_owner, &r, &g_raw, false) == KILN_OK) {
        s_raw_ct = program_slot(g_raw, KILN_SLOT_READ, kstr("CurrentTransform"));
        s_raw_tc = program_slot(g_raw, KILN_SLOT_READ, kstr("TeleportCount"));
    }

    static const KilnStr singletons[] = {kstr("ecs::PathGridSystem")};
    static const KilnStr other[] = {kstr("CurrentTransform")};
    KilnProgramDesc s{};
    s.size = sizeof s;
    s.flags = KILN_PROGRAM_SYSTEM | KILN_PROGRAM_HOST_ONLY;
    s.name = kstr("kiln_world_selftest_system");
    s.each = &sys_fn;
    s.singletons_read = names(singletons, 1);
    s.other_read = names(other, 1);
    if (register_program(g_owner, &s, &g_sys, false) == KILN_OK) {
        s_sys_grid = program_slot(g_sys, KILN_SLOT_SINGLETON, kstr("ecs::PathGridSystem"));
        s_sys_other = program_slot(g_sys, KILN_SLOT_OTHER_READ, kstr("CurrentTransform"));
    }

    // Refusals that must not reach the engine.
    static const KilnStr bogus[] = {kstr("NoSuchComponentKiln")};
    KilnProgramDesc b{};
    b.size = sizeof b;
    b.flags = KILN_PROGRAM_HOST_ONLY;
    b.name = kstr("kiln_world_selftest_bogus");
    b.each = &each_fn;
    b.read = names(bogus, 1);
    KilnHandle hb = 0;
    const KilnResult rb = register_program(g_owner, &b, &hb, false);  // accepted now, refused at world registration
    KilnProgramDesc all{};
    all.size = sizeof all;
    all.name = kstr("kiln_world_selftest_everything");
    all.each = &each_fn;
    KilnHandle ha = 0;
    const KilnResult ra = register_program(g_owner, &all, &ha, false);  // no list, not SYSTEM: refused at once
    KW_I("world selftest: bogus-name program accepted for checking (%d), list-less program refused at once (%d)", rb,
         ra);
}

void selftest_pre(const KilnTickInfo* tick) {
    if (!g_enabled || g_reported || !g_world.load()) return;
    if (!g_step0 && !g_world_live.load()) return;  // count from the first step in which programs run
    if (!g_step0) g_step0 = tick->step;
    const uint64_t steps = tick->step - g_step0;
    // queue test driver (tick thread: find a prop, then check the component appears and disappears)
    const int phase = g_queue_phase.load();
    if (steps == 40 && phase == 0) {
        static KilnEntity buf[65536];
        uint32_t n = 0;
        find(kCT, buf, 65536, &n);
        for (uint32_t i = 0; i < n && i < 65536; ++i)
            if (!component(buf[i], kTC, nullptr) && !component(buf[i], kLT, nullptr) &&
                !component(buf[i], kTeleport, nullptr) && buf[i] > 1023) {
                g_prop.store(buf[i]);
                break;
            }
        KW_I("world selftest: queue test on prop %u", g_prop.load());
        g_queue_phase.store(g_prop.load() ? 1 : 9);
    } else if (phase == 2) {
        const bool has = component(g_prop.load(), kTeleport, nullptr) != nullptr;
        KW_I("world selftest: %s queue_add (prop %u has Teleport after the step: %d)", has ? "PASS" : "FAIL",
             g_prop.load(), has);
        g_queue_phase.store(has ? 3 : 9);
    } else if (phase == 4) {
        const bool has = component(g_prop.load(), kTeleport, nullptr) != nullptr;
        KW_I("world selftest: %s queue_remove (prop %u has Teleport after the step: %d)", !has ? "PASS" : "FAIL",
             g_prop.load(), has);
        g_queue_phase.store(9);
    }
    if (steps < 120 || g_queue_phase.load() != 9) return;
    if (g_watchdog.load() == 0 && g_prop.load()) {
        const KilnResult rc = teleport_force_stuck(g_prop.load(), &watchdog_cb, nullptr);
        KW_I("world selftest: watchdog test started on prop %u (%d); expect a TIMED_OUT result in ~10 s",
             g_prop.load(), rc);
        g_watchdog.store(rc == KILN_OK ? 1 : 3);
        g_watchdog_step = tick->step;
    }
    if (g_watchdog.load() == 1 && tick->step - g_watchdog_step == 30) {
        const bool has = component(g_prop.load(), kTeleport, nullptr) != nullptr;
        KW_I("world selftest: %s forced Teleport is on the prop while it waits (%d)", has ? "PASS" : "FAIL", has);
    }
    if (g_watchdog.load() == 1) return;
    g_reported = true;
    auto info = [](KilnHandle h) {
        KilnProgramInfo i{};
        i.size = sizeof i;
        program_info(h, &i);
        return i;
    };
    const KilnProgramInfo ie = info(g_each), ir = info(g_raw), is = info(g_sys);
    KW_I("world selftest: %s each-mode slots (%llu entities match GetComponent, %llu differ; program #%u state %u, "
         "%llu updates, %llu entities)",
         each_ok && !each_bad ? "PASS" : "FAIL", static_cast<unsigned long long>(each_ok.load()),
         static_cast<unsigned long long>(each_bad.load()), ie.engine_id, ie.state,
         static_cast<unsigned long long>(ie.updates), static_cast<unsigned long long>(ie.entities));
    KW_I("world selftest: %s raw-mode iter_begin/iter_next/current_entity (%llu match, %llu differ; program #%u state %u, "
         "%llu updates)",
         raw_ok && !raw_bad ? "PASS" : "FAIL", static_cast<unsigned long long>(raw_ok.load()),
         static_cast<unsigned long long>(raw_bad.load()), ir.engine_id, ir.state,
         static_cast<unsigned long long>(ir.updates));
    KW_I("world selftest: %s system program singleton + read_other (%llu ok, %llu bad, %llu calls over %llu steps; "
         "program #%u state %u)",
         sys_ok && !sys_bad ? "PASS" : "FAIL", static_cast<unsigned long long>(sys_ok.load()),
         static_cast<unsigned long long>(sys_bad.load()), static_cast<unsigned long long>(sys_calls.load()),
         static_cast<unsigned long long>(steps), is.engine_id, is.state);
}

}  // namespace kw
