// SPDX-License-Identifier: MIT
// ECS program registration (EMF docs/engine_parts/re_custom_programs.md §2, §5-§7, re-verified on svn 1285601):
// kiln.world hooks the game's RegisterAllPrograms (the last program-registration call of the server world setup),
// calls the original, then registers every accepted Kiln program with the game's own RegisterProgram. Names are
// resolved by the engine later, at finalize, by hash; kiln.world validates every name against the world's maps first,
// because one unknown component or event aborts the engine's access resolution for that program half-way.
#include <algorithm>
#include <cstdio>
#include <chrono>
#include <array>
#include <cstdio>
#include <utility>
#include <set>
#include <shared_mutex>
#include <thread>

#include "kiln_hook.hpp"
#include "layout.h"
#include "world_internal.h"
#include "wraps.h"
#include "services/kiln_caps.h"
#include "services/kiln_types.h"

namespace kw {
namespace {

// Kiln's own quota (chapter 17.4; 32 until 0.2.0-dev.29): programs registered through kiln.world in this process, its own
// services' and compat loaders' and the mods' together; one typed trampoline each. Not the game's limit: the world's
// program table holds 896 (world/programs.max, read live), and every registration checks its live count first.
constexpr int kMaxPrograms = 64;
// Kept for Kiln's own services while mods' boot declarations reserve: the services that register later than the
// declarations (in their on_ready, or at the first world: the tracked-write program) still find room.
constexpr int kBuiltinHeadroom = 8;
constexpr uint32_t kMaxIterBytes = 4096;
constexpr uint32_t kAllowedFlags = KILN_PROGRAM_SYSTEM | KILN_PROGRAM_INIT | KILN_PROGRAM_HOST_ONLY |
                                   KILN_PROGRAM_EXCLUSIVE | KILN_PROGRAM_CHUNKED | KILN_PROGRAM_PINNED |
                                   KILN_PROGRAM_BARRIER;
constexpr uint32_t kEngineSkipped = 0x20;  // entry flag set at finalize: a required singleton is missing
constexpr uint32_t kClientFlag = 0x1;      // descriptor flag: a client-world program (the client world runs only these)
constexpr uint32_t kEngineBit = 0x40;      // an engine bit the prediction storage's rule names (chapter 14 section 1)

// A program's place in the client world's table (chapter 14 section 9, since 0.2.0-dev.29): as a client-world program
// (side[KILN_WORLD_CLIENT]: flag 0x1, the client world runs it) or as a prediction program (side[KILN_WORLD_PREDICTION]:
// no 0x1, 0x10 or 0x40, so the client world skips it and the prediction storage runs it), with that registration's own
// descriptor, state, counters and callback. The server world keeps Program's own fields, unchanged. A slice reaches a
// side's callback only through its gate (calls_in up, then `open` read, both sequentially consistent): once a closer
// that cleared `open` reads calls_in 0, no call of the side runs or can start, also while the client world steps on
// another thread (a host unloading a mod from its server thread).
struct Side {
    bool enabled = false;
    EngineDesc desc{};  // what that table's entry points to (never freed: the engine keeps it)
    std::atomic<int> state{KILN_PROGRAM_PENDING};
    std::atomic<uintptr_t> entry{0};
    std::atomic<uint32_t> engine_id{0};
    std::atomic<const std::string*> reason{nullptr};  // interned or static: valid for the life of the process
    std::atomic<uint64_t> updates{0}, entities{0};
    std::atomic<bool> fault_logged{false};
    std::atomic<KilnOwner> owner{0};
    std::atomic<KilnProgramEachFn> each{nullptr};
    std::atomic<KilnProgramUpdateFn> update{nullptr};
    std::atomic<void*> user{nullptr};
    std::atomic<bool> open{false};       // a callback is bound and calls may start
    std::atomic<uint32_t> calls_in{0};   // calls past the gate and not yet returned
    std::atomic<uint64_t> session{0};    // the client world registration (g_table_session) it is in; 0: none. A call
                                         // runs only while it equals the current one, and writes the side's state only
                                         // while it still does (a call of a stopped world never touches the next one's)
    uint64_t generation = 0;             // g_mutex: bumped by every bind (a binding handle carries it)
    bool closing = false;                // g_mutex: unbound while a call ran; forgotten once calls_in reads 0
};

struct Program : ProgramLayout {  // name, lists, singleton masks, group counts: layout.h
    uint32_t worlds = kWorldBitServer;  // the worlds it is for (kWorldBit*); fixed before it is published
    Side side[KILN_WORLD_KINDS];        // [KILN_WORLD_CLIENT] and [KILN_WORLD_PREDICTION]; [0] unused: the fields below
    KilnOwner owner = 0;
    std::shared_mutex callback_mutex;
    uint32_t flags = 0;
    KilnProgramEachFn each = nullptr;
    KilnProgramUpdateFn update = nullptr;
    void* user = nullptr;
    bool internal = false;
    ProgramPrunedFn pruned = nullptr;  // set: unknown other-write names are dropped, not fatal (the write program)
    std::string reserved_mod, reserved_key, reservation; // immutable, captured before engine normalization/pruning
    EngineDesc desc{};
    int slot = -1;  // trampoline index
    std::atomic<int> state{KILN_PROGRAM_PENDING};
    std::atomic<uint64_t> updates{0}, entities{0};
    std::atomic<uint32_t> engine_id{0};
    std::atomic<uintptr_t> entry{0};
    std::atomic<bool> fault_logged{false};
};

std::mutex g_mutex;
std::vector<Program*> g_programs;  // never freed: the engine keeps pointers into each descriptor
Program* g_by_slot[kMaxPrograms] = {};
kiln::RawHook g_hook;
std::atomic<bool> g_registered_once{false};
std::atomic<uintptr_t> g_finalize_checked{0};
thread_local ProgramCtx* t_context = nullptr;
thread_local uint64_t* t_iterator = nullptr;
thread_local Program* t_prog = nullptr;  // the Kiln program whose update runs on this thread

// ---- the client world's table (chapter 14): nothing of it runs while g_side_programs is 0 ----
std::atomic<bool> g_client_on{false};       // world.programs.client is available (programs_client_init)
std::atomic<uint32_t> g_side_programs{0};   // programs with a client or prediction side
std::atomic<bool> g_table_stopping{false};  // the client world stops: no call of a side starts
std::atomic<bool> g_table_live{false};      // a client world registered Kiln's programs and has not stopped
std::atomic<bool> g_skip_check{false};      // the client world's first Pre looks for programs the engine skips
std::atomic<uint32_t> g_closing{0};         // sides unbound while a call ran
std::atomic<uint32_t> g_table_stop_wait_ms{2000};
std::atomic<uint64_t> g_table_session{0};   // bumped by every client world's registration
// The process latch (package 14 review, P1): set once, never cleared. After a call of a stopped client world was still
// running when the bounded wait ended, or after the engine's RegisterProgram (or Kiln's raw reads around it) faulted or
// left a table that does not add up, no client-world program registers again in this process and the row says why.
std::atomic<bool> g_latched{false};
std::atomic<const std::string*> g_latch_reason{nullptr};
std::atomic<bool> g_latch_unreported{false};  // the row is reported after the locks are released
thread_local uint32_t t_side_kind = 0;         // the client-table side whose call runs on this thread (0: none)
thread_local uint64_t t_side_session = 0;      // ... and the client world registration (session) that call started in
// The storage each client-table side runs in, for the registration that runs now (package 14 closing review): the
// client world's own (storage_of(world) at its RegisterAllPrograms) and the prediction storage's (the first prediction
// slice of the session: it steps serially on the main thread, which is also where a client world registers, so no
// prediction slice of an earlier world can come after a registration). A slice whose job names another storage belongs
// to another world (a job of a stopped client world that entered late) and is refused.
std::atomic<uintptr_t> g_table_storage[KILN_WORLD_KINDS] = {};
std::atomic<uint64_t> g_other_world_slices{0};  // slices refused because their storage was another world's
std::atomic<uint64_t> g_late_slices{0};         // slices that stopped mid-way because their world began to stop
uint32_t g_main_tid = 0;                    // the game's main thread: kiln_plugin_main runs there
std::set<std::string> g_reasons;            // g_mutex: interned reasons (a small set of texts)
const std::string kReasonWaiting = "waiting for a client world";
const std::string kReasonNextWorld = "registered from the next client world";
const std::string kReasonFaulted = "its callback faulted in this world (kiln.log says where); bind it again to run it";
const std::string kReasonLayout = "the engine's iterator layout differs from kiln.world's (kiln.log has the counts)";
const std::string kReasonSkipped = "the engine skips it in this world (a required singleton is missing)";

void internal_update(ProgramCtx* ctx);
void dispatch_side(Program* p, uint32_t kind, ProgramCtx* ctx, const uint8_t* storage);
void report_latch();  // the latch's row, once, after the locks are released (defined with the client table's members)

void fault(Program* p, const char* what, unsigned long code) {
    p->state.store(KILN_PROGRAM_FAULTED);
    if (!p->fault_logged.exchange(true))
        KW_E("program %s: %s (0x%08lx); the program is disabled", p->name.c_str(), what, code);
}

// The engine's partial-iteration check (slice runner, after the update returns): ctx.index must be 0 or the slice's
// entity count. After a fault mid-iteration we finish the slice on paper so the engine does not abort.
// Every access guarded: after a client world began to stop its job may be gone (the engine's context itself is the slice
// runner's, but a late slice's job is not).
void repair(ProgramCtx* ctx) {
    uint32_t index = 0;
    if (!seh_copy(&index, &ctx->index, sizeof index) || index == 0) return;
    uint64_t count = 0;
    if (!seh_read_u64(reinterpret_cast<uintptr_t>(ctx->job) + static_cast<uintptr_t>(L.job_entity_count), &count)) return;
    const uint32_t done = static_cast<uint32_t>(count);
    seh_copy(&ctx->index, &done, sizeof done);
}

// The engine's CurrentEntity under the guard (a client-table slice: its world may be stopping under it).
struct EntityStep {
    ProgramCtx* ctx;
    uint32_t entity;
};
void entity_step(void* raw) {
    auto& st = *static_cast<EntityStep*>(raw);
    fn.current_entity(st.ctx, &st.entity);
}
bool guarded_entity(ProgramCtx* ctx, uint32_t* out, unsigned long* code) {
    EntityStep st{ctx, 0};
    if (!seh_call(&entity_step, &st, code)) return false;
    *out = st.entity;
    return true;
}

// Counts the engine computed for this program's iterator must equal ours, or our slots would be wrong.
bool counts_match(Program* p, ProgramCtx* ctx) {
    const uint8_t* j = ctx->job;
    const uint32_t t = j[L.job_tpl_count];
    const uint32_t r = *reinterpret_cast<const uint16_t*>(j + L.job_read_count);
    const uint32_t w = *reinterpret_cast<const uint16_t*>(j + L.job_write_count);
    const auto* singletons = *reinterpret_cast<const uint8_t* const*>(j + L.job_singletons);
    const uint32_t s = singletons ? singletons[L.singletons_count] : 0;
    const uint32_t ot = j[L.job_other_tpl_count];
    const uint32_t o = j[L.job_other_count];
    const uint64_t ei = *reinterpret_cast<const uint64_t*>(j + L.job_events_in_count);
    const uint32_t eo = *reinterpret_cast<const uint32_t*>(j + L.job_events_out_count);
    if (t == p->T && r == p->R && w == p->W && s == p->S && ot == p->OT && o == p->O && ei == p->EI && eo == p->EO)
        return true;
    p->state.store(KILN_PROGRAM_FAULTED);
    if (!p->fault_logged.exchange(true))
        KW_E("program %s: the engine's iterator layout differs from kiln.world's (engine T%u R%u W%u S%u OT%u O%u "
             "EI%llu EO%u, kiln T%u R%u W%u S%u OT%u O%u EI%u EO%u); disabled",
             p->name.c_str(), t, r, w, s, ot, o, static_cast<unsigned long long>(ei), eo, p->T, p->R, p->W, p->S,
             p->OT, p->O, p->EI, p->EO);
    return false;
}

void run_each(Program* p, ProgramCtx* ctx) {
    alignas(16) uint64_t it[kMaxIterBytes / 8 + 2];
    auto* kctx = reinterpret_cast<KilnProgramCtx*>(ctx);
    unsigned long code = 0;
    if (!seh_iter_begin(ctx, it, p->iter_size, &code)) {
        fault(p, "iter_begin faulted", code);
        return;
    }
    t_iterator = it;
    if (p->flags & KILN_PROGRAM_SYSTEM) {
        if (!seh_each(p->each, kctx, it, 0, p->user, &code)) fault(p, "update faulted", code);
        return;
    }
    bool faulted = false;
    for (;;) {
        bool more = false;
        if (!seh_iter_next(ctx, it, p->iter_size, &more, &code)) {
            fault(p, "iter_next faulted", code);
            repair(ctx);
            return;
        }
        if (!more) break;
        p->entities.fetch_add(1, std::memory_order_relaxed);
        if (faulted) continue;
        uint32_t e = 0;
        fn.current_entity(ctx, &e);
        if (!seh_each(p->each, kctx, it, e, p->user, &code)) {
            faulted = true;
            fault(p, "update faulted", code);
        }
    }
}

// The storage a slice runs in (J+0) and its kind (storage + storage.kind: its u8 kind). The job is the running slice's.
const uint8_t* slice_storage(const ProgramCtx* ctx) {
    const uint8_t* storage = nullptr;
    std::memcpy(&storage, ctx->job, sizeof storage);
    return storage;
}
uint32_t slice_kind(const uint8_t* storage) { return storage ? storage[L.storage_kind] : 0xFFu; }

void dispatch(int i, void* raw) {
    Program* p = g_by_slot[i];
    auto* ctx = static_cast<ProgramCtx*>(raw);
    if (!p) return;
    if (p->worlds != kWorldBitServer) {  // in the client world's table too (chapter 14): its slices go by storage kind
        const uint8_t* storage = slice_storage(ctx);
        const uint32_t kind = slice_kind(storage);
        if (kind != KILN_WORLD_SERVER) return dispatch_side(p, kind, ctx, storage);
        if (!(p->worlds & kWorldBitServer)) return;  // never: a server table has no entry of it
    }
    std::shared_lock callback_lock(p->callback_mutex);
    if (p->state.load(std::memory_order_relaxed) != KILN_PROGRAM_ACTIVE) return;  // iterate none: allowed
    p->updates.fetch_add(1, std::memory_order_relaxed);
    if (!counts_match(p, ctx)) return;
    t_in_program = true;
    t_prog = p;
    t_context = ctx;
    if (p->internal) {
        internal_update(ctx);
    } else if (p->each) {
        run_each(p, ctx);
    } else {
        unsigned long code = 0;
        if (!seh_update(p->update, reinterpret_cast<KilnProgramCtx*>(ctx), p->user, &code)) {
            fault(p, "update faulted", code);
            repair(ctx);
        }
    }
    t_context = nullptr;
    t_iterator = nullptr;
    t_prog = nullptr;
    t_in_program = false;
}

const char* world_name(uint32_t kind) {
    return kind == KILN_WORLD_CLIENT ? "client world" : kind == KILN_WORLD_PREDICTION ? "prediction storage" : "server world";
}

// A call's fault: only while the side is still in the registration the call ran in (a call of a stopped client world
// that returns late must not mark the next world's registration).
void fault_side(Program* p, Side& s, uint32_t kind, const char* what, unsigned long code, uint64_t session) {
    if (s.session.load(std::memory_order_seq_cst) != session) return;
    s.state.store(KILN_PROGRAM_FAULTED);
    s.reason.store(&kReasonFaulted);
    if (!s.fault_logged.exchange(true))
        KW_E("program %s (%s): %s (0x%08lx); the program is disabled in this world", p->name.c_str(), world_name(kind), what, code);
}

// The counts the engine computed for this slice's iterator against the program's layout (the same in every world: it is
// laid out by name, and each world's registration checked that its type order agrees).
bool side_counts_match(Program* p, Side& s, uint32_t kind, ProgramCtx* ctx, uint64_t session) {
    const JobCounts c = job_counts(ctx->job);
    if (same_counts(*p, c)) return true;
    if (s.session.load(std::memory_order_seq_cst) != session) return false;
    s.state.store(KILN_PROGRAM_FAULTED);
    s.reason.store(&kReasonLayout);
    if (!s.fault_logged.exchange(true))
        KW_E("program %s (%s): the engine's iterator layout differs from kiln.world's (engine T%u R%u W%u S%u OT%u O%u EI%u "
             "EO%u, kiln T%u R%u W%u S%u OT%u O%u EI%u EO%u); disabled in this world",
             p->name.c_str(), world_name(kind), c.T, c.R, c.W, c.S, c.OT, c.O, c.EI, c.EO, p->T, p->R, p->W, p->S, p->OT,
             p->O, p->EI, p->EO);
    return false;
}

// Whether a client-table call's world is gone: the client world began to stop, or the side left the registration the
// call started in. Checked before every entity of a slice (the gate is passed once per slice) and by every helper a
// callback can call: from then on nothing of that world is touched. A safety net: with the core's drain
// (world.stop_drain.client, since 0.2.0-dev.29) no slice of a client world runs when its STOPPING comes (StepPrograms
// waits for the world's queue before it returns, the world is destroyed on the thread that steps it, and the core runs
// WaitWorldJobs before STOPPING); it is for a core without that row (a bounded wait only) and for a drain that did not
// hold.
bool side_gone(const Side& s, uint64_t session) {
    return g_table_stopping.load(std::memory_order_seq_cst) || s.session.load(std::memory_order_seq_cst) != session;
}
// The same for the call that runs on this thread (false outside a client-table call).
bool call_gone() {
    return t_side_kind != 0 && t_prog && side_gone(t_prog->side[t_side_kind], t_side_session);
}

// A slice that stops mid-way because its world began to stop: finished on paper, counted, logged the first time and each
// time the count doubles.
void late_slice(Program* p, uint32_t kind, ProgramCtx* ctx) {
    repair(ctx);
    const uint64_t n = g_late_slices.fetch_add(1, std::memory_order_relaxed) + 1;
    if ((n & (n - 1)) == 0)
        KW_W("program %s (%s): its world began to stop while a slice of it ran; the slice stopped before its next entity "
             "(%llu so far)", p->name.c_str(), world_name(kind), static_cast<unsigned long long>(n));
}

// run_each for a side: its own callback, counters and fault state (the server world's are Program's). The world is checked
// again before each entity: once it began to stop (or the registration changed) the slice calls nothing more, neither
// the engine's iteration nor the callback, and finishes on paper.
void run_each_side(Program* p, Side& s, uint32_t kind, KilnProgramEachFn each, void* user, ProgramCtx* ctx, uint64_t session) {
    alignas(16) uint64_t it[kMaxIterBytes / 8 + 2];
    auto* kctx = reinterpret_cast<KilnProgramCtx*>(ctx);
    unsigned long code = 0;
    if (!seh_iter_begin(ctx, it, p->iter_size, &code)) {
        fault_side(p, s, kind, "iter_begin faulted", code, session);
        return;
    }
    t_iterator = it;
    if (p->flags & KILN_PROGRAM_SYSTEM) {
        if (!seh_each(each, kctx, it, 0, user, &code)) fault_side(p, s, kind, "update faulted", code, session);
        return;
    }
    bool faulted = false;
    for (;;) {
        if (side_gone(s, session)) return late_slice(p, kind, ctx);
        bool more = false;
        if (!seh_iter_next(ctx, it, p->iter_size, &more, &code)) {
            fault_side(p, s, kind, "iter_next faulted", code, session);
            repair(ctx);
            return;
        }
        if (!more) break;
        s.entities.fetch_add(1, std::memory_order_relaxed);
        if (faulted) continue;
        uint32_t e = 0;
        if (!guarded_entity(ctx, &e, &code)) {
            fault_side(p, s, kind, "current_entity faulted", code, session);
            repair(ctx);
            return;
        }
        if (!seh_each(each, kctx, it, e, user, &code)) {
            faulted = true;
            fault_side(p, s, kind, "update faulted", code, session);
        }
    }
}

// A slice of the client world's table: kind 2 (the client world) for a client side, kind 1 (the prediction storage)
// for a prediction side. Anything else iterates none (allowed: the engine accepts an index of 0): the prediction storage
// never runs a client program (0x1) and the client world never runs a prediction one, so those never come.
void dispatch_side(Program* p, uint32_t kind, ProgramCtx* ctx, const uint8_t* storage) {
    if (kind != KILN_WORLD_CLIENT && kind != KILN_WORLD_PREDICTION) return;
    Side& s = p->side[kind];
    if (!s.enabled) return;
    s.calls_in.fetch_add(1, std::memory_order_seq_cst);
    const uint64_t session = s.session.load(std::memory_order_seq_cst);
    if (!s.open.load(std::memory_order_seq_cst) || g_table_stopping.load(std::memory_order_seq_cst) || session == 0 ||
        session != g_table_session.load(std::memory_order_seq_cst) || s.state.load(std::memory_order_acquire) != KILN_PROGRAM_ACTIVE) {
        s.calls_in.fetch_sub(1, std::memory_order_release);
        return;
    }
    // The slice must belong to the world that registration was for: its job's storage against the session's (item 2 of
    // the closing review: a job of a stopped client world entering after the next one registered carries the old world's
    // storage). The prediction storage's is learnt from its first slice of the session. A safety net, as side_gone:
    // with the core's drain no job of a stopped world can enter; this is for a core without it and a drain that failed.
    const auto at = reinterpret_cast<uintptr_t>(storage);
    uintptr_t want = g_table_storage[kind].load(std::memory_order_seq_cst);
    if (!want && kind == KILN_WORLD_PREDICTION && g_table_storage[kind].compare_exchange_strong(want, at)) want = at;
    if (at != want) {
        s.calls_in.fetch_sub(1, std::memory_order_release);
        const uint64_t n = g_other_world_slices.fetch_add(1, std::memory_order_relaxed) + 1;
        if ((n & (n - 1)) == 0)
            KW_W("program %s (%s): a slice of another world (storage 0x%llx, this registration's 0x%llx) was refused (%llu so far)",
                 p->name.c_str(), world_name(kind), static_cast<unsigned long long>(at), static_cast<unsigned long long>(want),
                 static_cast<unsigned long long>(n));
        return;
    }
    const KilnProgramEachFn each = s.each.load(std::memory_order_acquire);
    const KilnProgramUpdateFn update = s.update.load(std::memory_order_acquire);
    void* const user = s.user.load(std::memory_order_acquire);
    s.updates.fetch_add(1, std::memory_order_relaxed);
    if ((each || update) && side_counts_match(p, s, kind, ctx, session)) {
        t_in_program = true;
        t_prog = p;
        t_context = ctx;
        t_side_kind = kind;
        t_side_session = session;
        if (each) {
            run_each_side(p, s, kind, each, user, ctx, session);
        } else {
            unsigned long code = 0;
            if (!seh_update(update, reinterpret_cast<KilnProgramCtx*>(ctx), user, &code)) {
                fault_side(p, s, kind, "update faulted", code, session);
                repair(ctx);
            }
        }
        t_side_kind = 0;
        t_side_session = 0;
        t_context = nullptr;
        t_iterator = nullptr;
        t_prog = nullptr;
        t_in_program = false;
    }
    s.calls_in.fetch_sub(1, std::memory_order_release);
}

template <int I>
void trampoline(void* ctx) {
    dispatch(I, ctx);
}
template <int... I>
constexpr std::array<void (*)(void*), sizeof...(I)> make_trampolines(std::integer_sequence<int, I...>) {
    return {&trampoline<I>...};
}
constexpr auto kTrampolines = make_trampolines(std::make_integer_sequence<int, kMaxPrograms>{});

// ---- names ----

bool valid_name(std::string_view n, bool allow_scope) {
    if (n.empty() || n.size() > 120) return false;
    for (char c : n) {
        const bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' ||
                        (allow_scope && c == ':');
        if (!ok) return false;
    }
    return allow_scope || n.find("::") == std::string_view::npos;
}

Program* by_handle(KilnHandle h) {
    std::lock_guard lock(g_mutex);
    if (h == 0 || h > g_programs.size()) return nullptr;
    return g_programs[h - 1];
}

// ---- validation and registration (server thread, inside the world setup) ----

// Drops the other-write names the world has no component type for, so one wrong name in one mod's list does not cost
// every mod the program. The engine has not seen the descriptor's lists yet (it resolves names at finalize).
void prune_unknown(Program& p, uintptr_t world) {
    const uintptr_t map = map_base(registry_of(world), M_DYN);
    if (!map) return;
    List& l = p.lists[L_OWRITE];
    std::vector<std::string> keep;
    for (const std::string& n : l.names) {
        if (map_find_u16(map, fnv1a(n, fnv1a("keen::ecs::")), nullptr)) {
            keep.push_back(n);
            continue;
        }
        KW_W("program %s: the world has no component '%s'; registered without it", p.name.c_str(), n.c_str());
        p.pruned(n);
    }
    if (keep.size() == l.names.size()) return;
    l.names = std::move(keep);
    layout(p, [&](int list, size_t i) { return qualified(p.lists[list].names[i]); });
    l.refs.clear();
    for (auto& s : l.names) l.refs.push_back({s.data(), s.size()});
    p.desc.other_write = NameListE{l.refs.data(), l.refs.size()};
}

void register_into(uintptr_t world) {
    std::lock_guard lock(g_mutex);
    const auto count_at = reinterpret_cast<const uint16_t*>(world + L.programs_count);
    for (Program* p : g_programs) {
        const int st = p->state.load();
        if (st == KILN_PROGRAM_REFUSED || st == KILN_PROGRAM_FAULTED) continue;
        if (!(p->worlds & kWorldBitServer)) continue;  // a client or prediction program only (chapter 14)
        if (p->pruned) prune_unknown(*p, world);
        std::string why;
        // A program in the client world's table too: validated on a copy, because on a host its client slices may read
        // its layout while this world sets up (validate() lays it out again by type index for the comparison).
        ProgramLayout copy;
        if (p->worlds != kWorldBitServer) copy = static_cast<const ProgramLayout&>(*p);
        if (!validate(p->worlds != kWorldBitServer ? copy : *p, world, why)) {
            p->state.store(KILN_PROGRAM_REFUSED);
            KW_E("program %s refused: %s", p->name.c_str(), why.c_str());
            continue;
        }
        const uint16_t before = *count_at;
        if (before >= L.programs_max) {
            p->state.store(KILN_PROGRAM_REFUSED);
            KW_E("program %s refused: the world's program table is full (%u/%lld)", p->name.c_str(), before,
                 static_cast<long long>(L.programs_max));
            continue;
        }
        bool ok = false;
        unsigned long code = 0;
        if (!seh_register_program(fn.register_program, world, &p->desc, &ok, &code)) {
            p->state.store(KILN_PROGRAM_REFUSED);
            KW_E("program %s: RegisterProgram faulted (0x%08lx)", p->name.c_str(), code);
            continue;
        }
        const uint16_t after = *count_at;
        const uintptr_t entry = world + L.programs_table + static_cast<uintptr_t>(after - 1) * L.programs_stride;
        if (!ok || after != before + 1 || *reinterpret_cast<const EngineDesc* const*>(entry) != &p->desc) {
            p->state.store(KILN_PROGRAM_REFUSED);
            KW_E("program %s: the engine refused it (duplicate name?) (ok=%d, count %u -> %u)", p->name.c_str(), ok,
                 before, after);
            continue;
        }
        p->entry.store(entry);
        p->engine_id.store(*reinterpret_cast<const uint16_t*>(entry + L.entry_id));
        p->state.store(KILN_PROGRAM_ACTIVE);
        KW_I("program %s registered as #%u (T%u R%u W%u S%u OT%u O%u EI%u EO%u, iterator %u bytes, flags 0x%x)",
             p->name.c_str(), p->engine_id.load(), p->T, p->R, p->W, p->S, p->OT, p->O, p->EI, p->EO, p->iter_size,
             p->flags);
    }
    KW_I("world 0x%llx: %u Kiln programs; the program table holds %u of %lld",
         static_cast<unsigned long long>(world), static_cast<unsigned>(g_programs.size()), *count_at,
         static_cast<long long>(L.programs_max));
    // the slot ledger: who holds Kiln's slots, in slot order (a service's or loader's program by its name, a mod's
    // declarations by its mod id and how many)
    std::string ledger;
    uint32_t mods = 0, services = 0;
    for (size_t i = 0; i < g_programs.size(); ++i) {
        const Program* p = g_programs[i];
        const bool mod = !p->reserved_mod.empty();
        (mod ? mods : services) += 1;
        if (mod && i > 0 && g_programs[i - 1]->reserved_mod == p->reserved_mod) continue;
        uint32_t n = 1;
        while (mod && i + n < g_programs.size() && g_programs[i + n]->reserved_mod == p->reserved_mod) ++n;
        if (ledger.size() < 1500) {
            ledger += ledger.empty() ? "" : ", ";
            ledger += mod ? p->reserved_mod + " x" + std::to_string(n) : p->name;
        }
    }
    KW_I("kiln.world program slots: %u of %d taken, %u by Kiln's services and loaders, %u by mods' declarations: %s",
         static_cast<unsigned>(g_programs.size()), kMaxPrograms, services, mods, ledger.c_str());
}

const std::string* intern_reason(const std::string& text) {  // g_mutex held
    return &*g_reasons.insert(text).first;
}

void refuse_side(Program* p, Side& s, uint32_t kind, const std::string& why) {  // g_mutex held
    s.state.store(KILN_PROGRAM_REFUSED);
    s.reason.store(intern_reason(why));
    KW_E("program %s refused for the %s: %s", p->name.c_str(), world_name(kind), why.c_str());
}

// Sets the process latch once (g_mutex held): no client-world program registers again in this process. The row is
// reported by the caller after the locks are released (report_latch).
void latch_client(const std::string& why) {
    if (g_latched.exchange(true)) return;
    const std::string* reason = intern_reason("client-world programs are off until the game restarts: " + why);
    g_latch_reason.store(reason);
    g_latch_unreported.store(true);
    KW_E("%s", reason->c_str());
}

// After the latch, one truth for every side (g_mutex held): refused with the latch's reason, out of every registration
// (session 0: no call starts), also the ones registered before the latch in the same client world. program_world_state,
// EcsProgramRefusedEvent and TryBind all read it.
void latch_all_sides() {
    const std::string* reason = g_latch_reason.load();
    for (Program* p : g_programs)
        for (uint32_t kind = 1; kind < KILN_WORLD_KINDS; ++kind) {
            Side& s = p->side[kind];
            if (!s.enabled) continue;
            s.session.store(0, std::memory_order_seq_cst);
            s.state.store(KILN_PROGRAM_REFUSED);
            s.reason.store(reason);
        }
}

// One side's registration into a client world, all of it under the structured-exception guard: the validation against
// that world's maps, the table's live count before, the engine's RegisterProgram (its own guard says when it faulted)
// and the count, entry and id read after. Raw reads of game memory never run outside it.
struct RegisterStep {
    enum Result { REGISTERED, REFUSED, FULL, ENGINE_REFUSED, MISMATCH, ENGINE_FAULT };
    Program* p = nullptr;
    Side* s = nullptr;
    uintptr_t world = 0;
    Result result = REFUSED;
    std::string why;
    uint16_t before = 0, after = 0, id = 0;
    uintptr_t entry = 0;
    bool ok = false;
    unsigned long code = 0;
};
void register_step(void* raw) {
    auto& r = *static_cast<RegisterStep*>(raw);
    const auto count_at = reinterpret_cast<const uint16_t*>(r.world + L.programs_count);
    ProgramLayout copy = static_cast<const ProgramLayout&>(*r.p);
    if (!validate(copy, r.world, r.why)) {
        r.result = RegisterStep::REFUSED;
        return;
    }
    r.before = *count_at;
    if (r.before >= L.programs_max) {
        r.result = RegisterStep::FULL;
        return;
    }
    if (!seh_register_program(fn.register_program, r.world, &r.s->desc, &r.ok, &r.code)) {
        r.result = RegisterStep::ENGINE_FAULT;
        return;
    }
    r.after = *count_at;
    if (!r.ok && r.after == r.before) {  // the engine said no and left its table as it was (a duplicate name)
        r.result = RegisterStep::ENGINE_REFUSED;
        return;
    }
    if (!r.ok || r.after != r.before + 1) {
        r.result = RegisterStep::MISMATCH;
        return;
    }
    r.entry = r.world + L.programs_table + static_cast<uintptr_t>(r.after - 1) * L.programs_stride;
    if (*reinterpret_cast<const EngineDesc* const*>(r.entry) != &r.s->desc) {
        r.result = RegisterStep::MISMATCH;
        return;
    }
    r.id = *reinterpret_cast<const uint16_t*>(r.entry + L.entry_id);
    r.result = RegisterStep::REGISTERED;
}

// The client world's RegisterAllPrograms (the main thread, inside its setup; each client world anew, hosted or joined):
// every client and prediction program into its table after the game's own, each validated against THIS world's maps
// (on a copy of its layout: on a host a "both" program's server slices may read it meanwhile), with the table's live
// count checked before and after each registration as for the server world (the engine's 896). A fault in it (the
// engine's or a raw read's), or a table that does not add up after it, sets the process latch: the programs registered
// so far are left as they are (the engine's table is never undone), nothing more is registered in this process.
void register_into_client(uintptr_t world) {
    std::lock_guard lock(g_mutex);
    const uint64_t session = g_table_session.fetch_add(1, std::memory_order_seq_cst) + 1;
    // this registration's storages, before any side joins it (a slice of another world is refused by its storage)
    g_table_storage[KILN_WORLD_CLIENT].store(storage_of(world), std::memory_order_seq_cst);
    g_table_storage[KILN_WORLD_PREDICTION].store(0, std::memory_order_seq_cst);
    uint32_t registered = 0, refused = 0;
    for (Program* p : g_programs) {
        for (const uint32_t kind : {uint32_t{KILN_WORLD_CLIENT}, uint32_t{KILN_WORLD_PREDICTION}}) {
            Side& s = p->side[kind];
            if (!s.enabled) continue;
            s.session.store(0, std::memory_order_seq_cst);
            s.entry.store(0);
            s.engine_id.store(0);
            if (s.calls_in.load(std::memory_order_seq_cst) != 0 && !g_latched.load())
                latch_client("a call of a Kiln program of the last client world was still running when this one registered");
            if (g_latched.load()) {  // nothing more after the latch (latch_all_sides below says why, for every side)
                ++refused;
                continue;
            }
            const bool faulted = s.state.load() == KILN_PROGRAM_FAULTED;  // stays so until a new bind
            if (!faulted) s.reason.store(nullptr);
            RegisterStep step;
            step.p = p;
            step.s = &s;
            step.world = world;
            unsigned long code = 0;
            if (!seh_call(&register_step, &step, &code)) {
                char text[96];
                std::snprintf(text, sizeof text, "reading the client world's maps or program table faulted (0x%08lx)", code);
                refuse_side(p, s, kind, text);
                latch_client(text);
                ++refused;
                continue;
            }
            switch (step.result) {
                case RegisterStep::REFUSED: refuse_side(p, s, kind, step.why); ++refused; continue;
                case RegisterStep::FULL:
                    refuse_side(p, s, kind, "the client world's program table is full (" + std::to_string(step.before) + "/" +
                                                std::to_string(L.programs_max) + ")");
                    ++refused;
                    continue;
                case RegisterStep::ENGINE_REFUSED:
                    refuse_side(p, s, kind, "the engine refused it (a duplicate name?) (count " + std::to_string(step.before) + ")");
                    ++refused;
                    continue;
                case RegisterStep::ENGINE_FAULT: {
                    char text[96];
                    std::snprintf(text, sizeof text, "the engine's RegisterProgram faulted (0x%08lx)", step.code);
                    refuse_side(p, s, kind, text);
                    latch_client(std::string(text) + " for " + p->name);
                    ++refused;
                    continue;
                }
                case RegisterStep::MISMATCH: {
                    const std::string text = "the client world's program table does not add up after RegisterProgram for " + p->name +
                                             " (ok=" + std::to_string(step.ok) + ", count " + std::to_string(step.before) + " -> " +
                                             std::to_string(step.after) + ")";
                    refuse_side(p, s, kind, text);
                    latch_client(text);
                    ++refused;
                    continue;
                }
                case RegisterStep::REGISTERED: break;
            }
            s.entry.store(step.entry);
            s.engine_id.store(step.id);
            s.session.store(session, std::memory_order_seq_cst);
            if (!faulted) s.state.store(KILN_PROGRAM_ACTIVE);
            ++registered;
            KW_I("program %s registered into the %s as #%u (T%u R%u W%u S%u OT%u O%u EI%u EO%u, flags 0x%x)%s",
                 p->name.c_str(), kind == KILN_WORLD_CLIENT ? "client world" : "client world's table for the prediction storage",
                 step.id, p->T, p->R, p->W, p->S, p->OT, p->O, p->EI, p->EO, s.desc.flags,
                 faulted ? "; it faulted in an earlier client world and stays off until it is bound again" : "");
        }
    }
    if (g_latched.load()) {
        // a latch in this registration also takes the sides registered before it: they never run in this world
        // (their entries stay in the engine's table, inert: the trampoline's gate refuses them) and say the latch
        latch_all_sides();
        registered = 0;
    }
    g_skip_check.store(true);
    g_table_live.store(!g_latched.load());
    if (!g_latched.load()) g_table_stopping.store(false, std::memory_order_seq_cst);
    uint16_t count = 0;
    seh_copy(&count, reinterpret_cast<const void*>(world + L.programs_count), 2);
    KW_I("client world 0x%llx: %u Kiln program(s) registered into its table, %u refused; the table holds %u of %lld",
         static_cast<unsigned long long>(world), registered, refused, count, static_cast<long long>(L.programs_max));
}

void on_world(uintptr_t world) {
    // The storage's kind byte, under the guard (this runs for every player, with or without Kiln programs): when it
    // cannot be read nothing is registered into that world and kiln.log says so.
    uint8_t kind = 0;
    if (!seh_copy(&kind, reinterpret_cast<const void*>(storage_of(world) + L.storage_kind), 1)) {
        KW_E("RegisterAllPrograms for world 0x%llx: its storage's kind could not be read; no Kiln programs registered there",
             static_cast<unsigned long long>(world));
        return;
    }
    if (kind != 0) {  // the client world of a client: only while a mod declares a program for it (chapter 14)
        if (kind == KILN_WORLD_CLIENT && g_side_programs.load() && g_client_on.load()) {
            uint8_t finalizing = 0, may = 0;
            if (!seh_copy(&finalizing, reinterpret_cast<const void*>(world), 1) ||
                (L.may_finalize >= 0 && !seh_copy(&may, reinterpret_cast<const void*>(world + L.may_finalize), 1)) || finalizing || may) {
                KW_E("client world 0x%llx is already finalizing its programs (or could not be read); no Kiln programs registered",
                     static_cast<unsigned long long>(world));
                return;
            }
            register_into_client(world);
            report_latch();
            return;
        }
        KW_I("RegisterAllPrograms for a world of kind %u: not the server world; no Kiln programs there", kind);
        return;
    }
    uint8_t finalizing = 0, may = 0;
    if (!seh_copy(&finalizing, reinterpret_cast<const void*>(world), 1) ||
        (L.may_finalize >= 0 && !seh_copy(&may, reinterpret_cast<const void*>(world + L.may_finalize), 1)) || finalizing || may) {
        KW_E("server world 0x%llx is already finalizing its programs (or could not be read); no Kiln programs registered",
             static_cast<unsigned long long>(world));
        return;
    }
    writes_world_changing();     // the previous world's requests and catalog end before g_world names this one
    g_world.store(world);
    writes_before_world(world);  // may still hand over the write program: before registration closes
    g_registered_once.store(true);
    register_into(world);
    writes_after_world();
}

void __fastcall hk_register_all(uintptr_t world) {
    g_hook.call<void>(world);
    try {
        on_world(world);
    } catch (const std::exception& e) {
        KW_E("program registration failed: %s", e.what());
    } catch (...) {
        KW_E("program registration failed");
    }
}

void internal_update(ProgramCtx* ctx) {
    const uintptr_t storage = *reinterpret_cast<const uintptr_t*>(ctx->job);
    const uintptr_t world = storage - static_cast<uintptr_t>(L.world_storage);
    if (world != g_world.load(std::memory_order_relaxed)) {
        static std::atomic<bool> warned{false};
        if (!warned.exchange(true))
            KW_W("own program runs in world 0x%llx, expected 0x%llx; following it", static_cast<unsigned long long>(world),
                 static_cast<unsigned long long>(g_world.load()));
        g_world.store(world);
    }
    g_internal_runs.fetch_add(1, std::memory_order_release);
    teleport_in_program(reinterpret_cast<KilnProgramCtx*>(ctx));
}

}  // namespace

// The event types of a world's event map, by name, into kiln.log (chapter 14 question 4: which event types a client
// world has, for on_event and events_in there). Only while a mod declares a client-table program: the client world's at
// its first Pre, a host's server world's at its first live step, each on its own thread. Names come from kiln.types
// (by hash); a hash it does not know is written as #<hash>.
namespace {
// The event map's hashes, read under the guard (raw reads of the world's registry and map on the main thread of every
// player with a client program): a fault leaves `faulted` set and the list as far as it got.
struct EventMapStep {
    uintptr_t world = 0, map = 0;
    std::vector<uint32_t> hashes;
};
void event_map_step(void* raw) {
    auto& st = *static_cast<EventMapStep*>(raw);
    st.map = map_base(registry_of(st.world), M_EVENT);
    if (st.map) map_each_u16(st.map, [&](uint32_t hash, uint16_t) { st.hashes.push_back(hash); });
}
}  // namespace

void dump_event_map(const char* what, uintptr_t world) {
    EventMapStep step;
    step.world = world;
    unsigned long code = 0;
    if (world && !seh_call(&event_map_step, &step, &code)) {
        // fallback: nothing is listed for that world (question 4's dump is a diagnostic; the programs do not depend on it)
        KW_W("%s 0x%llx: reading its event map faulted (0x%08lx); its event types are not listed", what,
             static_cast<unsigned long long>(world), code);
        return;
    }
    if (!step.map) {
        KW_I("%s 0x%llx: its event map cannot be read on this build (world.names.events)", what, static_cast<unsigned long long>(world));
        return;
    }
    const KilnTypesApi* types = program_types();
    std::vector<std::string> names;
    for (const uint32_t hash : step.hashes) {
        KilnTypeInfo info{};
        info.size = sizeof info;
        const KilnType* t = types && types->type_by_hash ? types->type_by_hash(hash) : nullptr;
        if (t && types->type_info(t, &info) == KILN_OK && info.name.len) {
            names.emplace_back(info.name.ptr, info.name.len);
        } else {
            char b[16];
            std::snprintf(b, sizeof b, "#%08x", hash);
            names.emplace_back(b);
        }
    }
    std::sort(names.begin(), names.end());
    std::vector<std::string> lines(1);
    for (const std::string& n : names) {
        if (lines.back().size() + n.size() > 800) lines.emplace_back();
        lines.back() += (lines.back().empty() ? "" : ", ") + n;
    }
    for (size_t i = 0; i < lines.size(); ++i)
        KW_I("%s 0x%llx event map (question 4): %u event types [%u/%u]: %s", what, static_cast<unsigned long long>(world),
             static_cast<unsigned>(names.size()), static_cast<unsigned>(i + 1), static_cast<unsigned>(lines.size()), lines[i].c_str());
}

// Whether a Kiln client or prediction program's call runs on this thread (foreign.cpp refuses its writes there).
bool in_client_program() { return t_side_kind != 0; }

bool programs_init() {
    if (!F.programs) return false;
    g_hook.install(g_api, g_owner, fn.register_all, reinterpret_cast<void*>(&hk_register_all), "world/RegisterAllPrograms");
    if (!g_hook) {
        KW_E("hooking RegisterAllPrograms at %p failed", reinterpret_cast<void*>(fn.register_all));
        return false;
    }
    return true;
}

// First Pre after the world finalized: report programs the engine skips (missing required singletons).
void programs_after_first_step(uintptr_t world) {
    if (g_finalize_checked.exchange(world) == world) return;
    std::lock_guard lock(g_mutex);
    for (Program* p : g_programs) {
        const uintptr_t entry = p->entry.load();
        if (p->state.load() != KILN_PROGRAM_ACTIVE || !entry) continue;
        const uint32_t flags = *reinterpret_cast<const uint32_t*>(entry + L.entry_flags);
        if (flags & kEngineSkipped) {
            p->state.store(KILN_PROGRAM_SKIPPED);
            KW_E("program %s: the engine skips it (a required singleton is missing; engine flags 0x%x)",
                 p->name.c_str(), flags);
        }
    }
    if (g_side_programs.load()) dump_event_map("server world", world);  // a host with client programs (chapter 14 Q4)
}

namespace {
// register_program for the worlds in `worlds` (kWorldBit*): the server world (as ever), the client world, the
// prediction storage. One Program, one slot and one trampoline whatever the worlds; a descriptor per table side.
KilnResult register_program_in(KilnOwner owner, const KilnProgramDesc* d, KilnHandle* out, bool internal, uint32_t worlds) {
    if (out) *out = 0;
    if (!F.programs) return KILN_ERR_UNAVAILABLE;
    if (!d || d->size < sizeof(KilnProgramDesc)) return KILN_ERR_INVALID_ARGUMENT;
    const std::string name(sv(d->name));
    auto refuse = [&](const char* why) {
        KW_E("program '%s' refused: %s", name.c_str(), why);
        return KILN_ERR_INVALID_ARGUMENT;
    };
    if ((worlds & kWorldBitServer) && g_registered_once.load()) {
        KW_E("program '%s' refused: the server world has registered its programs already (register from "
             "kiln_plugin_main or on_ready)", name.c_str());
        return KILN_ERR_UNAVAILABLE;
    }
    if (!valid_name(name, false) || name.size() > 63) return refuse("name must be 1-63 characters [A-Za-z0-9_]");
    if (d->flags & ~kAllowedFlags) return refuse("unsupported flags");
    if (!internal && (!d->each) == (!d->update)) return refuse("set exactly one of each / update");

    auto* p = new Program();  // never freed (the engine keeps &p->desc)
    p->owner = owner;
    p->name = name;
    p->flags = d->flags;
    p->each = d->each;
    p->update = d->update;
    p->user = d->user;
    p->internal = internal;
    p->sread_opt = d->singletons_read_optional;
    p->swrite_opt = d->singletons_write_optional;
    const KilnNames* src[L_COUNT] = {&d->tpl, &d->tpl_optional, &d->read, &d->read_optional, &d->write,
                                     &d->write_optional, &d->singletons_read, &d->singletons_write, &d->other_tpl,
                                     &d->other_read, &d->other_write, &d->with, &d->without, &d->any_of,
                                     &d->events_in, &d->events_out};
    for (int id = 0; id < L_COUNT; ++id) {
        const KilnNames& n = *src[id];
        if (n.count && !n.items) {
            delete p;
            return refuse("a list has a count but no items");
        }
        if (n.count > 160) {
            delete p;
            return refuse("a list is too long");
        }
        List& l = p->lists[id];
        l.names.reserve(n.count);
        for (uint32_t i = 0; i < n.count; ++i) {
            std::string s(sv(n.items[i]));
            if (!valid_name(s, kListMap[id] == M_SINGLETON)) {
                delete p;
                KW_E("program '%s' refused: bad name '%s' in %s", name.c_str(), s.c_str(), kListName[id]);
                return KILN_ERR_INVALID_ARGUMENT;
            }
            l.names.push_back(std::move(s));
        }
    }
    // Duplicates inside a slot group would give two slots for one bit.
    const std::vector<std::vector<int>> groups = {{L_TPL, L_TPL_OPT}, {L_READ, L_READ_OPT, L_WRITE, L_WRITE_OPT},
                                                  {L_SREAD}, {L_SWRITE}, {L_OTPL}, {L_OREAD}, {L_OWRITE},
                                                  {L_EIN}, {L_EOUT}, {L_WITH, L_WITHOUT, L_ANY}};
    for (const auto& g : groups) {
        std::vector<std::string> all;
        for (int id : g) all.insert(all.end(), p->lists[id].names.begin(), p->lists[id].names.end());
        std::sort(all.begin(), all.end());
        if (std::adjacent_find(all.begin(), all.end()) != all.end()) {
            delete p;
            return refuse("a name appears twice in one access group (read/write lists, a singleton list, ...)");
        }
    }
    const size_t tpl = p->lists[L_TPL].names.size() + p->lists[L_TPL_OPT].names.size();
    const size_t rw = p->lists[L_READ].names.size() + p->lists[L_READ_OPT].names.size() +
                      p->lists[L_WRITE].names.size() + p->lists[L_WRITE_OPT].names.size();
    const size_t singles = p->lists[L_SREAD].names.size() + p->lists[L_SWRITE].names.size();
    const size_t filters = p->lists[L_WITH].names.size() + p->lists[L_ANY].names.size();
    if (tpl > 8 || rw > 42 || p->lists[L_SREAD].names.size() > 64 || p->lists[L_SWRITE].names.size() > 64 ||
        p->lists[L_OTPL].names.size() > 9 ||
        p->lists[L_OREAD].names.size() + p->lists[L_OWRITE].names.size() > 92 ||
        p->lists[L_EIN].names.size() + p->lists[L_EOUT].names.size() > 24) {
        delete p;
        return refuse("over an engine limit (tpl 8, read+write 42, singletons 64 per list, other_tpl 9, other 92, "
                      "events 24)");
    }
    (void)singles;
    const bool system = d->flags & KILN_PROGRAM_SYSTEM;
    if (system && (tpl || rw || filters || p->lists[L_WITHOUT].names.size())) {
        delete p;
        return refuse("a SYSTEM program iterates no entities: no tpl/read/write/with/without/any_of lists");
    }
    if (!system && !tpl && !rw && !filters) {
        delete p;
        return refuse("an entity query without required components matches every entity; add a list or use SYSTEM");
    }
    layout(*p, [&](int list, size_t i) { return qualified(p->lists[list].names[i]); });
    if (p->iter_size > kMaxIterBytes) {
        delete p;
        return refuse("iterator over 4096 bytes");
    }
    for (auto& l : p->lists) {
        l.refs.clear();
        for (auto& s : l.names) l.refs.push_back({s.data(), s.size()});
    }
    auto list = [&](int id) { return NameListE{p->lists[id].refs.data(), p->lists[id].refs.size()}; };
    EngineDesc& e = p->desc;
    e.name = {p->name.data(), p->name.size()};
    e.tpl = list(L_TPL);
    e.tpl_opt = list(L_TPL_OPT);
    e.read = list(L_READ);
    e.read_opt = list(L_READ_OPT);
    e.write = list(L_WRITE);
    e.write_opt = list(L_WRITE_OPT);
    e.sys_read = list(L_SREAD);
    e.sys_read_opt = p->sread_opt;
    e.sys_write = list(L_SWRITE);
    e.sys_write_opt = p->swrite_opt;
    e.other_tpl = list(L_OTPL);
    e.other_read = list(L_OREAD);
    e.other_write = list(L_OWRITE);
    e.with = list(L_WITH);
    e.without = list(L_WITHOUT);
    e.any_of = list(L_ANY);
    e.events_in = list(L_EIN);
    e.events_out = list(L_EOUT);
    e.flags = p->flags;
    e.interval = 1;  // 0 divides by zero in the stage scheduler

    std::lock_guard lock(g_mutex);
    if (g_programs.size() >= kMaxPrograms) {
        delete p;
        KW_E("program '%s' refused: kiln.world holds %d programs at most (all of Kiln's services and mods together)", name.c_str(),
             kMaxPrograms);
        return KILN_ERR_UNAVAILABLE;
    }
    for (Program* q : g_programs)
        if (q->name == name) {
            delete p;
            KW_E("program '%s' refused: the name is taken", name.c_str());
            return KILN_ERR_INVALID_ARGUMENT;
        }
    p->slot = static_cast<int>(g_programs.size());
    e.update = kTrampolines[p->slot];
    // The client world's table gets a descriptor of its own (the engine keeps a pointer to it): the client flag 0x1 for a
    // client-world program, none of 0x1, 0x10 and 0x40 for a prediction one; HOST_ONLY, which only a server program can
    // mean, is dropped (register_program_world refuses it; a "both" reservation keeps it on its server side).
    p->worlds = worlds;
    for (const uint32_t kind : {uint32_t{KILN_WORLD_CLIENT}, uint32_t{KILN_WORLD_PREDICTION}}) {
        if (!(worlds & (1u << kind))) continue;
        Side& side = p->side[kind];
        side.enabled = true;
        side.desc = e;
        side.desc.flags = kind == KILN_WORLD_CLIENT ? (p->flags & ~uint32_t{KILN_PROGRAM_HOST_ONLY}) | kClientFlag
                                                    : p->flags & ~(kClientFlag | uint32_t{KILN_PROGRAM_HOST_ONLY} | kEngineBit);
    }
    if (worlds != kWorldBitServer) g_side_programs.fetch_add(1);
    g_by_slot[p->slot] = p;
    g_programs.push_back(p);
    if (out) *out = static_cast<KilnHandle>(g_programs.size());
    KW_I("program %s accepted (%s, flags 0x%x, iterator %u bytes%s)", name.c_str(),
         internal ? "internal" : (p->each ? "each" : "raw"), p->flags, p->iter_size,
         worlds == kWorldBitServer ? ""
         : worlds == kWorldBitClient ? ", client world"
         : worlds == kWorldBitPrediction ? ", prediction storage"
         : worlds == (kWorldBitServer | kWorldBitClient) ? ", server and client worlds" : ", several worlds");
    return KILN_OK;
}
}  // namespace

KilnResult register_program(KilnOwner owner, const KilnProgramDesc* d, KilnHandle* out, bool internal) {
    return register_program_in(owner, d, out, internal, kWorldBitServer);
}

int32_t program_slot(KilnHandle h, int32_t kind, KilnStr name) {
    Program* p = by_handle(h);
    if (!p) return -1;
    const std::string_view n = sv(name);
    int ids[2] = {-1, -1};
    switch (kind) {
        case KILN_SLOT_TPL: ids[0] = L_TPL, ids[1] = L_TPL_OPT; break;
        case KILN_SLOT_READ: ids[0] = L_READ, ids[1] = L_READ_OPT; break;
        case KILN_SLOT_WRITE: ids[0] = L_WRITE, ids[1] = L_WRITE_OPT; break;
        case KILN_SLOT_SINGLETON: ids[0] = L_SREAD, ids[1] = L_SWRITE; break;
        case KILN_SLOT_OTHER_TPL: ids[0] = L_OTPL; break;
        case KILN_SLOT_OTHER_READ: ids[0] = L_OREAD; break;
        case KILN_SLOT_OTHER_WRITE: ids[0] = L_OWRITE; break;
        case KILN_SLOT_EVENT_IN: ids[0] = L_EIN; break;
        case KILN_SLOT_EVENT_OUT: ids[0] = L_EOUT; break;
        default: return -1;
    }
    std::lock_guard lock(g_mutex);
    for (int id : ids) {
        if (id < 0) continue;
        const List& l = p->lists[id];
        for (size_t i = 0; i < l.names.size(); ++i)
            if (l.names[i] == n) return l.slot[i];
    }
    return -1;
}

KilnResult program_info(KilnHandle h, KilnWorldProgramInfo* out) {
    Program* p = by_handle(h);
    if (!p) return KILN_ERR_NOT_FOUND;
    if (!out || out->size < sizeof(KilnWorldProgramInfo)) return KILN_ERR_INVALID_ARGUMENT;
    out->state = static_cast<uint32_t>(p->state.load());
    out->engine_id = p->engine_id.load();
    out->iter_size = p->iter_size;
    out->updates = p->updates.load();
    out->entities = p->entities.load();
    return KILN_OK;
}

void program_set_prune(KilnHandle h, ProgramPrunedFn removed) {
    if (Program* p = by_handle(h)) p->pruned = removed;
}

uint32_t programs_registered() {
    std::lock_guard lock(g_mutex);
    return static_cast<uint32_t>(g_programs.size());
}

uint32_t program_quota() { return kMaxPrograms; }

// The slot ledger for kiln.world.budget (any thread): a slot is in active use while its program is registered or waits
// for a world with a callback that can run (a declaration counts once a mod bound it); a refused, faulted or skipped
// program, and a declaration no loaded mod bound, still holds its slot (the engine keeps every descriptor).
ProgramLedger program_ledger() {
    std::lock_guard lock(g_mutex);
    ProgramLedger out;
    out.taken = static_cast<uint32_t>(g_programs.size());
    for (const Program* p : g_programs) {
        out.descs.push_back(reinterpret_cast<uintptr_t>(&p->desc));
        const int st = p->state.load();
        const bool bound = p->reserved_mod.empty() || p->owner != g_owner;
        bool in_use = (p->worlds & kWorldBitServer) && (st == KILN_PROGRAM_ACTIVE || st == KILN_PROGRAM_PENDING) && bound &&
                      (p->internal || p->each || p->update);
        // its client-table descriptors are Kiln's entries in a client world's table; a bound side in use holds the slot too
        for (const Side& side : p->side) {
            if (!side.enabled) continue;
            out.descs.push_back(reinterpret_cast<uintptr_t>(&side.desc));
            const int ss = side.state.load();
            in_use = in_use || ((ss == KILN_PROGRAM_ACTIVE || ss == KILN_PROGRAM_PENDING) && side.open.load());
        }
        if (in_use) ++out.active;
    }
    std::sort(out.descs.begin(), out.descs.end());
    return out;
}

uint32_t engine_program_count(uintptr_t world) {
    if (!world || L.programs_count < 0) return 0;
    return *reinterpret_cast<const uint16_t*>(world + L.programs_count);
}

// ---- helpers inside an update ----

// Inside a client-table call every helper below refuses once that call's world is gone (call_gone: the client world
// began to stop, or the registration changed): nothing more of a world being destroyed is touched.
KilnResult iter_begin(KilnProgramCtx* c, uint64_t* iter, uint32_t size) {
    auto* ctx = reinterpret_cast<ProgramCtx*>(c);
    if (!ctx || !iter || !t_in_program) return KILN_ERR_WRONG_THREAD;
    if (call_gone()) return KILN_ERR_UNAVAILABLE;
    // The engine ignores the size: the buffer must hold the whole iterator.
    if (t_prog && size < t_prog->iter_size) return KILN_ERR_BUFFER_TOO_SMALL;
    if (ctx != t_context) return KILN_ERR_INVALID_ARGUMENT;
    if (!fn.iter_begin) return KILN_ERR_UNAVAILABLE;
    fn.iter_begin(ctx, iter, size);
    t_iterator = iter;
    return KILN_OK;
}

bool iter_next(KilnProgramCtx* c, uint64_t* iter, uint32_t size) {
    auto* ctx = reinterpret_cast<ProgramCtx*>(c);
    if (!ctx || ctx != t_context || !t_in_program || !t_prog || iter != t_iterator ||
        !iter || size < t_prog->iter_size || !fn.iter_next || call_gone()) return false;
    if (t_side_kind == 0) return fn.iter_next(ctx, iter, size);
    bool more = false;
    unsigned long code = 0;
    return seh_iter_next(ctx, iter, size, &more, &code) && more;
}

KilnEntity current_entity(KilnProgramCtx* c) {
    auto* ctx = reinterpret_cast<ProgramCtx*>(c);
    if (!ctx || ctx != t_context || !t_in_program || !fn.current_entity || call_gone()) return 0;
    uint32_t e = 0;
    if (t_side_kind == 0) {
        fn.current_entity(ctx, &e);
        return e;
    }
    unsigned long code = 0;
    return guarded_entity(ctx, &e, &code) ? e : 0;
}

void* command_buffer(KilnProgramCtx* c) {
    auto* ctx = reinterpret_cast<ProgramCtx*>(c);
    if (!ctx || ctx != t_context || !t_in_program || !F.queue || L.job_cmdbuf < 0 || call_gone()) return nullptr;
    void* cb = nullptr;
    return seh_copy(&cb, ctx->job + L.job_cmdbuf, sizeof cb) ? cb : nullptr;
}

std::atomic<bool> g_queue_broken{false};

void* queue_push(KilnProgramCtx* c, uint8_t kind, uint32_t size, KilnEntity entity) {
    void* cb = command_buffer(c);
    if (!cb || size < 16 || size > 0x8000 || kind == 0x11) return nullptr;  // 0x11 is fatal in the queue
    if (g_queue_broken.load(std::memory_order_relaxed)) return nullptr;
    void* r = nullptr;
    if (!seh_queue_push(cb, static_cast<uint8_t*>(cb) + L.cmdbuf_queue, kind, size, entity, &r)) {
        if (!g_queue_broken.exchange(true))
            KW_E("QueuePush faulted (kind %u, %u bytes, entity %u); command-buffer pushes are off from now on",
                 kind, size, entity);
        return nullptr;
    }
    return r;
}

KilnResult queue_add(KilnProgramCtx* c, KilnEntity entity, uint32_t hash, const void* data, uint32_t size) {
    if (!entity || !hash || !data || size == 0 || size > 4096) return KILN_ERR_INVALID_ARGUMENT;
    if (!command_buffer(c)) return t_in_program ? KILN_ERR_UNAVAILABLE : KILN_ERR_WRONG_THREAD;
    const uint32_t total = static_cast<uint32_t>(L.rec_data) + size;
    auto* r = static_cast<uint8_t*>(queue_push(c, 3, total, entity));
    if (!r) return KILN_ERR_BUFFER_TOO_SMALL;  // full: retry next step
    std::memcpy(r + L.rec_hashes, &hash, 4);
    const uint16_t sz = static_cast<uint16_t>(size);
    std::memcpy(r + L.rec_sizes, &sz, 2);
    std::memcpy(r + L.rec_data, data, size);
    return KILN_OK;
}

KilnResult queue_remove(KilnProgramCtx* c, KilnEntity entity, uint32_t hash) {
    if (!entity || !hash) return KILN_ERR_INVALID_ARGUMENT;
    if (!command_buffer(c)) return t_in_program ? KILN_ERR_UNAVAILABLE : KILN_ERR_WRONG_THREAD;
    auto* r = static_cast<uint8_t*>(queue_push(c, 4, static_cast<uint32_t>(L.tp_remove_size), entity));
    if (!r) return KILN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(r + L.rec_hashes, &hash, 4);
    return KILN_OK;
}

void mark_written(KilnProgramCtx* c, const uint64_t* w) {
    // Not in a client world or the prediction storage (since 0.2.0-dev.29): MarkWritten runs the same two tests as the
    // other-entity mark (replication while a peer is connected, the save for an entity the save tracks: docs/re/world.md)
    // and nothing in the notes says it skips a client storage, while world.programs.client promises that nothing a client
    // program writes is saved or sent. A no-op there.
    if (t_side_kind != 0) return;
    if (!c || reinterpret_cast<ProgramCtx*>(c) != t_context || !w || !t_in_program || !t_prog ||
        !t_iterator || !F.write || !fn.mark_written) return;
    bool declared = false;
    for (int id : {L_WRITE, L_WRITE_OPT})
        for (int32_t slot : t_prog->lists[id].slot)
            if (slot >= 0 && w == t_iterator + slot) { declared = true; break; }
    if (!declared || !w[5]) return;
    fn.mark_written(reinterpret_cast<void*>(w[5]), reinterpret_cast<void*>(w[1]), reinterpret_cast<void*>(w[2]),
                    reinterpret_cast<void*>(w[3]), static_cast<uint16_t>(w[4]), static_cast<uint8_t>(w[4] >> 16));
}

const void* read_other(KilnProgramCtx* c, uint64_t accessor, KilnEntity entity, uint32_t* out_size) {
    if (out_size) *out_size = 0;
    auto* ctx = reinterpret_cast<ProgramCtx*>(c);
    if (!ctx || ctx != t_context || !t_in_program || !t_prog || !t_iterator || !F.read_other ||
        !fn.read_other || L.job_storage < 0 || !accessor || !entity || call_gone()) return nullptr;
    bool declared = false;
    for (int32_t slot : t_prog->lists[L_OREAD].slot)
        if (slot >= 0 && t_iterator[slot] == accessor) { declared = true; break; }
    if (!declared) return nullptr;
    Slice s{};
    if (t_side_kind == 0) {
        void* storage = *reinterpret_cast<void* const*>(ctx->job + L.job_storage);
        fn.read_other(&s, storage, reinterpret_cast<void*>(accessor), entity);
    } else {  // a client-table call: the job and the engine's read under the guard
        struct Step {
            ProgramCtx* ctx;
            uint64_t accessor;
            uint32_t entity;
            Slice out;
        } st{ctx, accessor, entity, {}};
        unsigned long code = 0;
        if (!seh_call(
                [](void* raw) {
                    auto& r = *static_cast<Step*>(raw);
                    void* storage = *reinterpret_cast<void* const*>(r.ctx->job + L.job_storage);
                    fn.read_other(&r.out, storage, reinterpret_cast<void*>(r.accessor), r.entity);
                },
                &st, &code))
            return nullptr;
        s = st.out;
    }
    if (s.size > UINT32_MAX) return nullptr;
    if (out_size) *out_size = static_cast<uint32_t>(s.size);
    return s.ptr;
}

void* write_other(KilnProgramCtx* c, uint64_t accessor, KilnEntity entity, uint32_t* out_size) {
    if (out_size) *out_size = 0;
    // Not in a client world or the prediction storage (0.2.0-dev.29). Not for the save: per docs/re/world.md WriteOther
    // marks a block only in the server world (storage+8 == 0), so in a client storage it would not mark. It is refused
    // because the other-entity path (the job's tracker and write flag of a client job, a block of another entity of a
    // world the server replicates into) has not been read in a client run: a client program changes its own declared
    // write slots only, until a client run checks the rest.
    if (t_side_kind != 0) return nullptr;
    auto* ctx = reinterpret_cast<ProgramCtx*>(c);
    if (!ctx || ctx != t_context || !t_prog || !t_iterator || !F.write_other || !accessor || !entity) return nullptr;
    bool declared = false;
    for (int32_t slot : t_prog->lists[L_OWRITE].slot)
        if (slot >= 0 && t_iterator[slot] == accessor) { declared = true; break; }
    if (!declared) return nullptr;
    Slice s{};
    void* storage = *reinterpret_cast<void* const*>(ctx->job + L.job_storage);
    fn.write_other(&s, storage, fn.job_tracker(ctx->job), accessor, entity, fn.job_write_flag(ctx));
    if (s.size > UINT32_MAX) return nullptr;
    if (out_size) *out_size = static_cast<uint32_t>(s.size);
    return const_cast<void*>(s.ptr);
}

KilnResult unregister_program(KilnHandle h) {
    if (t_in_program || (g_registered_once.load() && !g_api->on_tick_thread())) return KILN_ERR_WRONG_THREAD;
    Program* p = by_handle(h);
    if (!p || p->internal) return KILN_ERR_NOT_FOUND;
    std::unique_lock lock(p->callback_mutex);
    if (!p->reserved_mod.empty()) {
        p->owner = g_owner;
        p->each = [](KilnProgramCtx*, uint64_t*, KilnEntity, void*) {};
        if (p->state.load() == KILN_PROGRAM_FAULTED) {
            p->state.store(p->entry.load() ? KILN_PROGRAM_ACTIVE : KILN_PROGRAM_PENDING);
            p->fault_logged = false;
        }
    } else {
        p->state.store(KILN_PROGRAM_FAULTED);
        p->each = nullptr;
    }
    p->update = nullptr;
    p->user = nullptr;
    return KILN_OK;
}

void KILN_CALL program_noop(void*) {}
// The core API has no direct owner-liveness query: a post to a revoked owner fails (and one to a live owner queues a
// no-op task). Probe at most once per second, not per program per tick; the list buffer is reused (tick thread only).
void programs_reap_owners() {
    static std::chrono::steady_clock::time_point next{};
    const auto now = std::chrono::steady_clock::now();
    if (now < next) return;
    next = now + std::chrono::seconds(1);
    static std::vector<Program*> snapshot;
    snapshot.clear();
    { std::lock_guard lock(g_mutex); snapshot.assign(g_programs.begin(), g_programs.end()); }
    for (Program* p : snapshot) {
        if (p->internal || p->owner == g_owner) continue;
        if (g_api->post(p->owner, &program_noop, nullptr) == KILN_ERR_REVOKED)
            unregister_program(static_cast<KilnHandle>(p->slot + 1));
    }
}

// The boot declaration of a reservation as kiln.world.budget's program_reservation answers it: copied from the
// declaration, not the live descriptor (which the world's validation may later prune). A declaration that is not the
// server world's alone ends with its "world" (since 0.2.0-dev.29, chapter 14: part of the reservation's identity, so a
// declaration rebuilt for another world under the same key is a different one); a server one's text is as it was.
std::string reservation_text(const KilnProgramDesc* desc, uint32_t interval, uint32_t worlds) {
    const KilnNames* lists[] = {&desc->tpl, &desc->tpl_optional, &desc->read, &desc->read_optional, &desc->write,
        &desc->write_optional, &desc->singletons_read, &desc->singletons_write, &desc->other_tpl, &desc->other_read,
        &desc->other_write, &desc->with, &desc->without, &desc->any_of, &desc->events_in, &desc->events_out};
    const char* names[] = {"tpl", "tplOptional", "read", "readOptional", "write", "writeOptional", "singletonsRead",
        "singletonsWrite", "otherTpl", "otherRead", "otherWrite", "with", "without", "anyOf", "eventsIn", "eventsOut"};
    std::string snapshot;
    for (size_t i=0; i<16; ++i) {
        snapshot += names[i]; snapshot += '=';
        for (uint32_t j=0; j<lists[i]->count; ++j) {
            if (j) snapshot += ',';
            snapshot += sv(lists[i]->items[j]);
        }
        snapshot += '\n';
    }
    char tail[64];
    std::snprintf(tail, sizeof tail, "flags=%08x\ninterval=%u\n", desc->flags, interval);
    snapshot += tail;
    if (worlds != kWorldBitServer)
        snapshot += worlds == kWorldBitClient ? "world=client\n" : worlds == kWorldBitPrediction ? "world=prediction\n"
                    : worlds == (kWorldBitServer | kWorldBitClient) ? "world=both\n" : "world=other\n";
    return snapshot;
}

KilnResult reserve_program(const std::string& mod, const std::string& key, const KilnProgramDesc* desc) {
    if (programs_registered() + kBuiltinHeadroom >= static_cast<uint32_t>(kMaxPrograms)) {
        KW_E("managed program %s/%s refused: the last %d of kiln.world's %d program slots stay for Kiln's own services", mod.c_str(),
             key.c_str(), kBuiltinHeadroom, kMaxPrograms);
        return KILN_ERR_UNAVAILABLE;
    }
    KilnHandle handle = 0;
    const auto result = register_program(g_owner, desc, &handle, false);
    if (result != KILN_OK) return result;
    Program* p = by_handle(handle);
    std::string snapshot = reservation_text(desc, p->desc.interval, kWorldBitServer);
    std::lock_guard lock(g_mutex);
    p->reservation = std::move(snapshot);
    p->reserved_mod = mod;
    p->reserved_key = key;
    return KILN_OK;
}
KilnResult KILN_CALL program_reservation(KilnStr mod, KilnStr key, char* text, uint32_t capacity, uint32_t* out_size) {
    if (!out_size) return KILN_ERR_INVALID_ARGUMENT;
    *out_size = 0;
    if (!mod.ptr || !key.ptr) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mutex);
    for (const Program* p : g_programs) {
        if (p->reserved_mod != sv(mod) || p->reserved_key != sv(key)) continue;
        *out_size = static_cast<uint32_t>(p->reservation.size());
        if (text) {
            if (capacity < *out_size) return KILN_ERR_BUFFER_TOO_SMALL;
            std::memcpy(text, p->reservation.data(), *out_size);
        }
        return KILN_OK;
    }
    return KILN_ERR_NOT_FOUND;
}
KilnResult KILN_CALL bind_program(KilnOwner owner, KilnStr mod, KilnStr key, KilnProgramEachFn each, void* user, KilnHandle* out) {
    if (out) *out = 0;
    if (!F.programs) return KILN_ERR_UNAVAILABLE;
    if (t_in_program || !g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!owner || !each || !out) return KILN_ERR_INVALID_ARGUMENT;
    if (g_api->post(owner, &program_noop, nullptr) == KILN_ERR_REVOKED) return KILN_ERR_REVOKED;
    std::lock_guard lock(g_mutex);
    for (Program* p : g_programs) {
        if (p->reserved_mod != sv(mod) || p->reserved_key != sv(key)) continue;
        if (!(p->worlds & kWorldBitServer)) return KILN_ERR_NOT_FOUND;  // a client-world reservation (bind_program_world)
        std::unique_lock callback_lock(p->callback_mutex);
        if (p->owner != g_owner) return KILN_ERR_INVALID_ARGUMENT;
        if (p->state.load() != KILN_PROGRAM_ACTIVE && p->state.load() != KILN_PROGRAM_PENDING) return KILN_ERR_UNAVAILABLE;
        p->owner = owner; p->each = each; p->user = user;
        *out = static_cast<KilnHandle>(p->slot + 1);
        return KILN_OK;
    }
    return KILN_ERR_NOT_FOUND;
}
uint32_t KILN_CALL program_capabilities() {
    return (F.programs && F.write_other ? KILN_WORLD_CAP_WRITE_OTHER : 0) |
        (F.programs && F.events ? KILN_WORLD_CAP_MULTI_EVENTS : 0) |
        (F.programs ? KILN_WORLD_CAP_BIND_PROGRAM : 0) | writes_capability() | ecs_capability();
}

const KilnTypesApi* program_types() {
    const auto* types = static_cast<const KilnTypesApi*>(g_api->service(kstr(KILN_TYPES_SERVICE),1));
    return types && types->header.size >= offsetof(KilnTypesApi,system_count) ? types : nullptr;
}
bool event_info(const KilnTypesApi* types, KilnStr name, KilnTypeInfo& info) {
    if (!types) return false;
    const auto* type = types->type_by_name(name);
    info.size = sizeof info;
    return type && types->type_info(type,&info) == KILN_OK && info.type_size && info.type_size < 0x800;
}
KilnResult KILN_CALL program_events(KilnProgramCtx* c, KilnStr name, KilnProgramEventFn callback, void* user) {
    if (!t_prog || reinterpret_cast<ProgramCtx*>(c) != t_context || !t_iterator) return KILN_ERR_WRONG_THREAD;
    if (call_gone()) return KILN_ERR_UNAVAILABLE;
    if (!callback) return KILN_ERR_INVALID_ARGUMENT;
    const int32_t slot = program_slot(static_cast<KilnHandle>(t_prog->slot+1),KILN_SLOT_EVENT_IN,name);
    if (slot < 0) return KILN_ERR_INVALID_ARGUMENT;
    KilnTypeInfo info{};
    if (!event_info(program_types(),name,info)) return KILN_ERR_UNAVAILABLE;
    auto constant = [](const char* key) { int64_t value = -1; g_api->constant(kstr(key),&value); return value; };
    const auto first = constant("combat/evq.first"), blocks = constant("combat/evq.blocks"), count = constant("combat/evq.count"),
        next = constant("combat/evq.next"), shift = constant("combat/evq.block_shift");
    if (first < 0 || blocks < 0 || count < 0 || next < 0 || shift < 5 || shift > 20) return KILN_ERR_UNAVAILABLE;
    const auto queue = t_iterator[slot];
    uint32_t length = 0; uint16_t page = 0; uintptr_t pool = 0;
    if (!queue || !seh_copy(&length,reinterpret_cast<void*>(queue+count),4) || length > 65536 ||
        !seh_copy(&page,reinterpret_cast<void*>(queue+first),2) || !seh_copy(&pool,reinterpret_cast<void*>(queue+blocks),8)) return KILN_ERR_INTERNAL;
    const uint64_t page_size = uint64_t{1} << shift;
    if (info.type_size > page_size-8 || uint64_t(length)*info.type_size > 16*1024*1024) return KILN_ERR_INTERNAL;
    std::vector<uint8_t> copies(static_cast<size_t>(length)*info.type_size);
    uint64_t offset = 0;
    uintptr_t block = page && pool ? pool + uint64_t(page-1)*page_size : 0;
    std::set<uint16_t> visited;
    if (page) visited.insert(page);
    for (uint32_t i = 0; i < length; ++i) {
        if (offset+info.type_size > page_size-8) {
            uint16_t following = 0;
            if (!block || !seh_copy(&following,reinterpret_cast<void*>(block+next),2) || !following || !visited.insert(following).second) return KILN_ERR_INTERNAL;
            block = pool + uint64_t(following-1)*page_size; offset = 0;
        }
        if (!block || !seh_copy(copies.data()+size_t(i)*info.type_size,reinterpret_cast<void*>(block+8+offset),info.type_size)) return KILN_ERR_INTERNAL;
        offset += info.type_size;
    }
    for (uint32_t i = 0; i < length; ++i) callback(copies.data()+size_t(i)*info.type_size,info.type_size,user);
    return KILN_OK;
}
KilnResult KILN_CALL program_emit(KilnProgramCtx* c, KilnStr name, const void* data, uint32_t size) {
    if (!t_prog || reinterpret_cast<ProgramCtx*>(c) != t_context || !t_iterator) return KILN_ERR_WRONG_THREAD;
    if (call_gone()) return KILN_ERR_UNAVAILABLE;
    if (!data || !size || size >= 256) return KILN_ERR_INVALID_ARGUMENT;
    const int32_t slot = program_slot(static_cast<KilnHandle>(t_prog->slot+1),KILN_SLOT_EVENT_OUT,name);
    if (slot < 0) return KILN_ERR_INVALID_ARGUMENT;
    const auto* types = program_types();
    KilnTypeInfo info{};
    if (!event_info(types,name,info) || !fn.emit_event || !fn.job_event_buffer || !fn.job_program_id) return KILN_ERR_UNAVAILABLE;
    if (size != info.type_size) return KILN_ERR_INVALID_ARGUMENT;
    uint8_t copy[256];
    if (!seh_copy(copy,data,size)) return KILN_ERR_INVALID_ARGUMENT;
    bool stamp = false;
    for (unsigned depth = 0; depth < 16; ++depth) {
        if (sv(info.name) == "GameEvent") { stamp = true; break; }
        if (!info.inner || types->type_info(info.inner,&info) != KILN_OK) break;
    }
    if (stamp && (!fn.program_time || size < 8)) return KILN_ERR_UNAVAILABLE;
    auto* ctx = reinterpret_cast<ProgramCtx*>(c);
    void* out = nullptr;
    if (t_side_kind == 0) {
        void* buffer = fn.job_event_buffer(ctx->job);
        out = buffer ? fn.emit_event(buffer,component_hash(sv(name)),size,fn.job_program_id(ctx->job)) : nullptr;
    } else {  // a client-table call: the job's event buffer, the engine's emit, the copy and the stamp under the guard
        struct Step {
            ProgramCtx* ctx;
            uint32_t hash, size;
            const uint8_t* copy;
            bool stamp;
            void* out;
        } st{ctx, component_hash(sv(name)), size, copy, stamp, nullptr};
        unsigned long code = 0;
        if (!seh_call(
                [](void* raw) {
                    auto& r = *static_cast<Step*>(raw);
                    void* buffer = fn.job_event_buffer(r.ctx->job);
                    r.out = buffer ? fn.emit_event(buffer, r.hash, r.size, fn.job_program_id(r.ctx->job)) : nullptr;
                    if (!r.out) return;
                    std::memcpy(r.out, r.copy, r.size);
                    if (r.stamp) fn.program_time(r.ctx, static_cast<int64_t*>(r.out));
                },
                &st, &code))
            return KILN_ERR_INTERNAL;
        return st.out ? KILN_OK : KILN_ERR_BUFFER_TOO_SMALL;
    }
    if (!out) return KILN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(out,copy,size);
    if (stamp) fn.program_time(ctx,static_cast<int64_t*>(out));
    return KILN_OK;
}


// ======================================================================= the client world's table (chapter 14) =====

namespace {

bool on_client_thread() {
    return g_api->on_world_thread(KILN_WORLD_CLIENT) || (g_main_tid && current_thread_id() == g_main_tid);
}

// A binding handle: (generation << 12) | (kind << 8) | (slot + 1). A program handle is slot + 1 (kind 0, generation 0).
KilnHandle binding_handle(int slot, uint32_t kind, uint64_t generation) {
    return (generation << 12) | (static_cast<uint64_t>(kind) << 8) | static_cast<uint64_t>(slot + 1);
}

Program* program_at(KilnHandle h) {  // g_mutex held
    const uint64_t slot = (h & 0xFFu);
    return slot >= 1 && slot <= g_programs.size() ? g_programs[slot - 1] : nullptr;
}

void forget_binding(Side& s) {  // g_mutex held; no call of it runs any more
    s.each.store(nullptr);
    s.update.store(nullptr);
    s.user.store(nullptr);
    s.owner.store(0);
    if (s.closing) {
        s.closing = false;
        g_closing.fetch_sub(1);
    }
}

// Closes a side's gate and waits for its running calls at most `wait`: true when none runs any more (forgotten).
bool close_side(Side& s, std::chrono::microseconds wait) {  // g_mutex held
    s.open.store(false, std::memory_order_seq_cst);
    const auto until = std::chrono::steady_clock::now() + wait;
    while (s.calls_in.load(std::memory_order_seq_cst) != 0) {
        if (std::chrono::steady_clock::now() >= until) {
            if (!s.closing) {
                s.closing = true;
                g_closing.fetch_add(1);
            }
            return false;
        }
        std::this_thread::yield();
    }
    forget_binding(s);
    return true;
}

void report_client_row(uint32_t state, uint32_t reason, const char* text) {
    const auto* caps = static_cast<const KilnCapsApi*>(g_api->service(kstr(KILN_CAPS_SERVICE), 1));
    if (caps && caps->header.size >= offsetof(KilnCapsApi, report) + sizeof(void*) && caps->report)
        caps->report(g_owner, kstr("world.programs.client"), state, reason, KilnStr{text, std::strlen(text)});
}

void report_latch() {
    if (!g_latch_unreported.exchange(false)) return;
    const std::string* why = g_latch_reason.load();
    report_client_row(KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_FAULTED, why ? why->c_str() : "client-world programs are off until the game restarts");
}

constexpr const char* kRowMenu = "Kiln programs in the client world and the prediction storage: waiting for a world";
constexpr const char* kRowWorld = "Kiln programs in the client world and the prediction storage: a client world runs";
// The row's text with the core's guarantee at a client world's stop (world_stop_drain's words).
std::string row_text(const char* base, const StopDrain& drain) { return std::string(base) + "; at a world stop " + drain.text; }

}  // namespace

// Boot (kiln_plugin_main, the main thread): what world.programs.client needs, and its row. Off on a dedicated server
// (wrong side), without the RegisterAllPrograms hook or the program table's layout (world.programs), without the name
// maps (a name could not be checked against the client world), without world.lifecycle (a client world's stop would not
// be seen, so its programs could not be closed), or when the client world's tick or the world events cannot be had.
void programs_client_init(bool client_tick, bool world_events) {
    g_main_tid = current_thread_id();
    const bool client = g_api->size >= offsetof(KilnApi, session) + sizeof(void*) && g_api->side() == KILN_SIDE_CLIENT;
    const char* why = nullptr;
    uint32_t reason = KILN_CAP_REASON_SYMBOL_MISSING;
    if (!client) why = "a client world runs only in the game client", reason = KILN_CAP_REASON_WRONG_SIDE;
    else if (!F.programs || !g_hook || L.programs_count < 0 || L.programs_max <= 0 || L.programs_table < 0 || L.storage_kind < 0)
        why = "kiln.world's program registration did not resolve or hook on this build (world.programs)";
    else if (!F.names)
        why = "the world's name maps did not resolve on this build (world.names): a client world's names could not be checked";
    else if (!g_api->feature(kstr("world.lifecycle")))
        why = "client-world programs need world.lifecycle to close them when a client world stops";
    else if (!client_tick || !world_events)
        why = "the client world's tick or the world events could not be subscribed", reason = KILN_CAP_REASON_FAULTED;
    g_client_on.store(why == nullptr);
    if (why) {
        report_client_row(KILN_CAP_UNAVAILABLE, reason, why);
        if (client) KW_W("client-world programs are off: %s", why);
        return;
    }
    report_client_row(KILN_CAP_AVAILABLE, KILN_CAP_REASON_NONE, kRowMenu);
    if (!F.events) KW_W("client-world programs: event names cannot be checked on this build; a program with events is refused");
}

bool programs_client_on() { return g_client_on.load(); }

KilnResult reserve_program_world(const std::string& mod, const std::string& key, const KilnProgramDesc* desc, uint32_t worlds, uint32_t declared_worlds) {
    if (!declared_worlds) declared_worlds = worlds;
    if (worlds == kWorldBitServer) {
        const auto result = reserve_program(mod, key, desc);
        if (result == KILN_OK && declared_worlds != worlds) {
            std::lock_guard lock(g_mutex);
            for (Program* p : g_programs)
                if (p->reserved_mod == mod && p->reserved_key == key)
                    p->reservation = reservation_text(desc, 1, declared_worlds);
        }
        return result;
    }
    if (!worlds || (worlds & ~(kWorldBitServer | kWorldBitClient | kWorldBitPrediction))) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_client_on.load()) return KILN_ERR_UNAVAILABLE;
    if (programs_registered() + kBuiltinHeadroom >= static_cast<uint32_t>(kMaxPrograms)) {
        KW_E("managed program %s/%s refused: the last %d of kiln.world's %d program slots stay for Kiln's own services", mod.c_str(),
             key.c_str(), kBuiltinHeadroom, kMaxPrograms);
        return KILN_ERR_UNAVAILABLE;
    }
    KilnHandle handle = 0;
    const auto result = register_program_in(g_owner, desc, &handle, false, worlds);
    if (result != KILN_OK) return result;
    std::string snapshot = reservation_text(desc, 1, declared_worlds);  // the interval every Kiln program gets
    std::lock_guard lock(g_mutex);
    Program* p = g_programs[handle - 1];
    p->reservation = std::move(snapshot);
    p->reserved_mod = mod;
    p->reserved_key = key;
    return KILN_OK;
}

KilnResult KILN_CALL register_program_world(KilnOwner owner, uint32_t kind, const KilnProgramDesc* d, KilnHandle* out) {
    if (out) *out = 0;
    if (kind == KILN_WORLD_SERVER) {
        if (t_in_program || game_in_slice()) return KILN_ERR_WRONG_THREAD;
        return register_program(owner, d, out, false);
    }
    if (kind != KILN_WORLD_CLIENT && kind != KILN_WORLD_PREDICTION) return KILN_ERR_INVALID_ARGUMENT;
    if (t_in_program || game_in_slice()) return KILN_ERR_WRONG_THREAD;
    if (!F.programs || !g_client_on.load() || g_latched.load()) return KILN_ERR_UNAVAILABLE;
    if (!d || d->size < sizeof(KilnProgramDesc) || !owner) return KILN_ERR_INVALID_ARGUMENT;
    if (d->flags & KILN_PROGRAM_HOST_ONLY) {
        KW_E("program '%.*s' refused: HOST_ONLY is for server programs (a %s program never runs in the server world)",
             static_cast<int>(d->name.len), d->name.ptr ? d->name.ptr : "", world_name(kind));
        return KILN_ERR_INVALID_ARGUMENT;
    }
    KilnHandle h = 0;
    const KilnResult rc = register_program_in(owner, d, &h, false, 1u << kind);
    if (rc != KILN_OK) return rc;
    {
        std::lock_guard lock(g_mutex);
        Side& s = g_programs[h - 1]->side[kind];
        s.owner.store(owner);
        s.each.store(d->each);
        s.update.store(d->update);
        s.user.store(d->user);
        s.generation = 1;
        if (g_table_live.load()) s.reason.store(&kReasonNextWorld);
        s.open.store(true, std::memory_order_seq_cst);
    }
    if (out) *out = h;
    return KILN_OK;
}

KilnResult KILN_CALL program_world_state(KilnHandle h, uint32_t kind, int32_t* state, KilnStr* reason) {
    if (reason) *reason = KilnStr{"", 0};
    if (!state || kind >= KILN_WORLD_KINDS) return KILN_ERR_INVALID_ARGUMENT;
    *state = KILN_PROGRAM_PENDING;
    if (h == 0) {  // the client table's own state: REFUSED with the latch's reason once latched (a caller whose bind got
                   // KILN_ERR_UNAVAILABLE tells the latch from "the last binding's calls are still finishing")
        if (kind == KILN_WORLD_SERVER) return KILN_ERR_NOT_FOUND;
        if (g_latched.load()) {
            *state = KILN_PROGRAM_REFUSED;
            if (reason)
                if (const std::string* why = g_latch_reason.load()) *reason = kstr(*why);
        } else {
            *state = g_table_live.load() ? KILN_PROGRAM_ACTIVE : KILN_PROGRAM_PENDING;
        }
        return KILN_OK;
    }
    Program* p = nullptr;
    {
        std::lock_guard lock(g_mutex);  // only to find the program: its states are atomics
        p = program_at(h);
    }
    if (!p || !(p->worlds & (1u << kind))) return KILN_ERR_NOT_FOUND;
    if (kind == KILN_WORLD_SERVER) {
        *state = p->state.load();
        return KILN_OK;
    }
    const Side& s = p->side[kind];
    *state = s.state.load();
    const std::string* why = s.reason.load();
    if (!why && *state == KILN_PROGRAM_PENDING) why = g_table_live.load() ? &kReasonNextWorld : &kReasonWaiting;
    if (reason && why) *reason = kstr(*why);
    return KILN_OK;
}

KilnResult KILN_CALL bind_program_world(KilnOwner owner, KilnStr mod, KilnStr key, uint32_t kind, KilnProgramEachFn each,
                                        KilnProgramUpdateFn update, void* user, KilnHandle* out) {
    if (out) *out = 0;
    if (kind == KILN_WORLD_SERVER) {
        if (update || game_in_slice()) return update ? KILN_ERR_INVALID_ARGUMENT : KILN_ERR_WRONG_THREAD;
        return bind_program(owner, mod, key, each, user, out);
    }
    if (kind != KILN_WORLD_CLIENT && kind != KILN_WORLD_PREDICTION) return KILN_ERR_INVALID_ARGUMENT;
    if (!F.programs || !g_client_on.load() || g_latched.load()) return KILN_ERR_UNAVAILABLE;
    if (t_in_program || game_in_slice() || !on_client_thread()) return KILN_ERR_WRONG_THREAD;
    if (!owner || !out || (!each) == (!update)) return KILN_ERR_INVALID_ARGUMENT;
    if (g_api->post_world(owner, KILN_WORLD_CLIENT, &program_noop, nullptr) == KILN_ERR_REVOKED) return KILN_ERR_REVOKED;
    std::lock_guard lock(g_mutex);
    for (Program* p : g_programs) {
        if (p->reserved_mod.empty() || p->reserved_mod != sv(mod) || p->reserved_key != sv(key)) continue;
        Side& s = p->side[kind];
        if (!s.enabled) return KILN_ERR_NOT_FOUND;
        if (s.open.load()) return KILN_ERR_INVALID_ARGUMENT;  // another binding holds it
        if (s.closing) {
            if (s.calls_in.load(std::memory_order_seq_cst) != 0) return KILN_ERR_UNAVAILABLE;  // the last calls still finish
            forget_binding(s);
        }
        if (s.state.load() == KILN_PROGRAM_FAULTED) {
            s.state.store(s.entry.load() ? KILN_PROGRAM_ACTIVE : KILN_PROGRAM_PENDING);
            s.fault_logged.store(false);
            s.reason.store(nullptr);
        }
        s.owner.store(owner);
        s.each.store(each);
        s.update.store(update);
        s.user.store(user);
        ++s.generation;
        s.open.store(true, std::memory_order_seq_cst);
        *out = binding_handle(p->slot, kind, s.generation);
        return KILN_OK;
    }
    return KILN_ERR_NOT_FOUND;
}

KilnResult KILN_CALL unbind_program_world(KilnHandle h) {
    if (t_in_program || game_in_slice()) return KILN_ERR_WRONG_THREAD;
    const uint32_t kind = static_cast<uint32_t>(h >> 8) & 0xFu;
    const uint64_t generation = h >> 12;
    std::unique_lock lock(g_mutex);
    Program* p = program_at(h);
    if (!p) return KILN_ERR_NOT_FOUND;
    uint32_t k = kind;
    if (kind == KILN_WORLD_SERVER) {
        if (generation != 0) return KILN_ERR_NOT_FOUND;
        if (p->worlds & kWorldBitServer) {  // a server program's handle: unregister_program (tick thread)
            lock.unlock();
            return unregister_program(h);
        }
        k = (p->worlds & kWorldBitClient) ? KILN_WORLD_CLIENT : KILN_WORLD_PREDICTION;  // register_program_world's handle
    }
    if (k != KILN_WORLD_CLIENT && k != KILN_WORLD_PREDICTION) return KILN_ERR_NOT_FOUND;
    Side& s = p->side[k];
    if (!s.enabled) return KILN_ERR_NOT_FOUND;
    const uint64_t want = kind == KILN_WORLD_SERVER ? s.generation : generation;
    if (want > s.generation || want == 0) return KILN_ERR_NOT_FOUND;
    if (want < s.generation) return KILN_OK;  // an earlier binding: it had finished before the next one was made
    if (!s.open.load() && !s.closing) return KILN_OK;
    return close_side(s, std::chrono::microseconds(2000)) ? KILN_OK : KILN_ERR_WRONG_THREAD;
}

// The client world's Pre (main thread). Nothing for a player whose mods declare no client program (one atomic load).
void programs_client_pre(const KilnTickInfo*) {
    if (!g_side_programs.load(std::memory_order_relaxed)) return;
    if (g_skip_check.exchange(false)) {  // the first Pre after the world finalized: programs the engine skips
        dump_event_map("client world", g_api->world(KILN_WORLD_CLIENT));
        std::lock_guard lock(g_mutex);
        for (Program* p : g_programs)
            for (uint32_t kind = 1; kind < KILN_WORLD_KINDS; ++kind) {
                Side& s = p->side[kind];
                const uintptr_t entry = s.entry.load();
                if (!s.enabled || !entry || s.state.load() != KILN_PROGRAM_ACTIVE) continue;
                uint32_t flags = 0;
                if (!seh_copy(&flags, reinterpret_cast<const void*>(entry + L.entry_flags), 4) || !(flags & kEngineSkipped)) continue;
                s.state.store(KILN_PROGRAM_SKIPPED);
                s.reason.store(&kReasonSkipped);
                KW_E("program %s: the engine skips it in the %s (a required singleton is missing; engine flags 0x%x)",
                     p->name.c_str(), world_name(kind), flags);
            }
    }
    if (g_closing.load(std::memory_order_relaxed)) {  // unbound while a call ran: forgotten once it has finished
        std::lock_guard lock(g_mutex);
        for (Program* p : g_programs)
            for (Side& s : p->side)
                if (s.closing && s.calls_in.load(std::memory_order_seq_cst) == 0) forget_binding(s);
    }
    // Revoked owners, at most once a second (a post to a revoked owner fails; a live owner's no-op runs before the next
    // Pre). Probed outside g_mutex.
    static std::chrono::steady_clock::time_point next{};
    const auto now = std::chrono::steady_clock::now();
    if (now < next) return;
    next = now + std::chrono::seconds(1);
    std::vector<std::pair<Side*, KilnOwner>> open;
    {
        std::lock_guard lock(g_mutex);
        for (Program* p : g_programs)
            for (Side& s : p->side)
                if (s.enabled && s.open.load() && s.owner.load() && s.owner.load() != g_owner) open.emplace_back(&s, s.owner.load());
    }
    std::vector<KilnOwner> revoked, live;
    for (const auto& [side, owner] : open) {
        if (std::find(revoked.begin(), revoked.end(), owner) != revoked.end() || std::find(live.begin(), live.end(), owner) != live.end())
            continue;
        (g_api->post_world(owner, KILN_WORLD_CLIENT, &program_noop, nullptr) == KILN_ERR_REVOKED ? revoked : live).push_back(owner);
    }
    if (revoked.empty()) return;
    std::lock_guard lock(g_mutex);
    for (const auto& [side, owner] : open)
        if (std::find(revoked.begin(), revoked.end(), owner) != revoked.end() && side->owner.load() == owner)
            close_side(*side, std::chrono::microseconds(0));
}

// A world starts or stops. The client world's stop (on the destroying thread, the world intact): no call of a client-
// table program starts, and a slice already inside stops before its next entity (run_each_side) and every helper refuses
// (call_gone); the running callbacks are waited for, at most g_table_stop_wait_ms; each registration goes back to PENDING
// for the next client world (a FAULTED one stays so until it is bound again); bindings stay. When a callback is still
// inside after the wait, the process latch is set and every side is refused with its reason, its session cleared (that
// late call can no longer write its state); nothing registers again and the row says so. Since 0.2.0-dev.29 the core runs
// the game's WaitWorldJobs on the client world before this event (world.stop_drain.client, world_stop_drain): then no
// call can be inside here, and one that is (the drain did not hold) is an error that latches as a timeout does. Without
// that row the per-entity check, the bounded wait and the latch are the only guard. The row says whether a world runs
// and which guarantee the stop has.
void programs_world_event(const KilnWorldEvent* e) {
    if (!e || e->kind != KILN_WORLD_CLIENT || !g_client_on.load()) return;
    if (e->event == KILN_WORLD_STARTED) {
        if (!g_latched.load())
            report_client_row(KILN_CAP_AVAILABLE, KILN_CAP_REASON_NONE,
                              row_text(kRowWorld, world_stop_drain(KILN_WORLD_CLIENT, g_table_stop_wait_ms.load())).c_str());
        return;
    }
    if (e->event != KILN_WORLD_STOPPING) return;
    // What the core guarantees about the client world's jobs at this STOPPING (06a, world.stop_drain.client): asked first,
    // without kiln.world's locks (kiln.caps may call its subscribers on this thread).
    const StopDrain drain = world_stop_drain(KILN_WORLD_CLIENT, g_table_stop_wait_ms.load());
    if (g_side_programs.load()) {
        g_table_stopping.store(true, std::memory_order_seq_cst);
        g_table_live.store(false);
        std::vector<Program*> snapshot;
        {
            std::lock_guard lock(g_mutex);
            snapshot = g_programs;
        }
        auto running = [&] {
            uint32_t n = 0;
            for (Program* p : snapshot)
                for (const Side& s : p->side) n += s.calls_in.load(std::memory_order_seq_cst);
            return n;
        };
        const auto t0 = std::chrono::steady_clock::now();
        const uint32_t first = running();
        // With the engine's drain no call can be inside now: one that is means the drain did not hold. It is an error, and
        // it latches as a timeout does (below), also when the call leaves within the wait. Without the drain the bounded
        // wait is the only guard (as before 0.2.0-dev.29's core drain).
        const bool drain_broken = first != 0 && drain.engine;
        if (drain_broken)
            KW_E("client world stops: %u call(s) of Kiln programs were inside although %s", first, drain.text.c_str());
        uint32_t left = first;
        while (left && std::chrono::steady_clock::now() < t0 + std::chrono::milliseconds(g_table_stop_wait_ms.load())) {
            std::this_thread::yield();
            left = running();
        }
        const auto waited = std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now() - t0).count();
        uint32_t closed = 0, stuck = 0;
        {
            std::lock_guard lock(g_mutex);
            // A count the gate raised for a call that is leaving at once (it saw the stop) lasts nanoseconds: let those go
            // before deciding (1 ms), so that only a call really inside counts as stuck.
            const auto settle = std::chrono::steady_clock::now() + std::chrono::milliseconds(1);
            auto inside = [&] {
                uint32_t n = 0;
                for (Program* p : g_programs)
                    for (const Side& s : p->side) n += s.enabled ? s.calls_in.load(std::memory_order_seq_cst) : 0;
                return n;
            };
            while (inside() && std::chrono::steady_clock::now() < settle) std::this_thread::yield();
            for (Program* p : g_programs)
                for (uint32_t kind = 1; kind < KILN_WORLD_KINDS; ++kind) {
                    Side& s = p->side[kind];
                    if (!s.enabled) continue;
                    ++closed;
                    s.session.store(0, std::memory_order_seq_cst);  // a call still inside can no longer write this side
                    if (s.calls_in.load(std::memory_order_seq_cst) != 0) {
                        ++stuck;
                        KW_E("program %s: a call was still running in the %s when it stopped", p->name.c_str(), world_name(kind));
                    }
                }
            if (stuck) {
                KW_E("client world stops: %u call(s) of Kiln programs still running after %lld us; their bindings are kept", stuck,
                     static_cast<long long>(waited));
                latch_client("a call of a Kiln program was still running " + std::to_string(waited) +
                             " us after the client world began to stop (it may read the destroyed world; " + drain.text + ")");
            } else if (drain_broken) {
                latch_client("a call of a Kiln program was inside the client world at its stop although " + drain.text);
            }
            if (g_latched.load()) {
                latch_all_sides();  // every side, the stuck ones too: refused with the latch's reason
            } else {
                for (Program* p : g_programs)
                    for (uint32_t kind = 1; kind < KILN_WORLD_KINDS; ++kind) {
                        Side& s = p->side[kind];
                        if (!s.enabled) continue;
                        s.entry.store(0);
                        s.engine_id.store(0);
                        if (s.state.load() != KILN_PROGRAM_FAULTED) {
                            s.state.store(KILN_PROGRAM_PENDING);
                            s.reason.store(nullptr);
                        }
                    }
            }
            for (Program* p : g_programs)
                for (Side& s : p->side)
                    if (s.closing && s.calls_in.load(std::memory_order_seq_cst) == 0) forget_binding(s);
        }
        KW_I("client world stops: %u Kiln client-table registration(s) closed for the next client world (%u call(s) running at the "
             "stop, %u after %lld us; %s%s)", closed, first, stuck, static_cast<long long>(waited), drain.text.c_str(),
             g_latched.load() ? "; client-world programs are off until the game restarts" : "");
    }
    if (g_latched.load()) report_latch();
    else report_client_row(KILN_CAP_AVAILABLE, KILN_CAP_REASON_NONE, row_text(kRowMenu, drain).c_str());
}

}  // namespace kw
