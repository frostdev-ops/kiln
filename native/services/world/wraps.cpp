// SPDX-License-Identifier: MIT
// kiln.world v4, program wraps: Kiln's code before, after or instead of the update of one of the game's programs, per
// slice, in the program's own slot, thread and scheduling place (chapter 06a; section 9 is normative).
//
// Mechanism: ProgramRunner reads the update of a program from its table entry (entry+0x28) for every slice. A wrap
// swaps that pointer for one of kiln.world's trampolines (a static pool per world kind with a table: 896 each), in
// the world's own Pre, when no slice of that world runs. The trampoline runs the program's stage chain around the
// update it saved. The last stage gone, the pointer goes back by compare-exchange; when another tool swapped it in the
// meantime, the trampoline stays in its chain, forwarding, and its slot is never reused.
//
// The slice path (dispatch, run, call) takes no lock and allocates nothing. Every exit of a trampoline is balanced
// (in_flight: up, then down after the update returned, also when it only forwards). Chains are immutable; a new one
// is published in Pre, the old one is freed once the slot's in-flight count reads 0 after the publish (the slice
// path increments before it loads the chain, both sequentially consistent, so a slice either holds the new chain or
// was counted). A stage that faults (SEH) or overruns its budget 10 times in a row is disabled at once, on its own.
// A stage is entered only through call()'s gate: count up (calls_in), then check `gone`; an unwrap, a release or a
// revoked owner sets `gone` first and then reads calls_in (both sequentially consistent), so once it reads 0 no call
// of that stage runs or can start, also while its world steps; taking it out of the chain waits for the next Pre.
#include <algorithm>
#include <array>
#include <chrono>
#include <cstdio>
#include <deque>
#include <map>
#include <memory>
#include <unordered_map>
#include <utility>

#include "services/kiln_caps.h"
#include "wraps.h"

namespace kw {
namespace {

constexpr uint32_t kOverrunsToDisable = 10;
constexpr size_t kFinishedKept = 1024;   // removed and refused wraps kept for game_wrap_info
constexpr uint32_t kCoolingPres = 2;     // Pres with nothing in flight before a restored slot is used again
std::atomic<uint64_t> g_budget_ns{1000000};  // 1 ms per stage per slice

thread_local bool t_in_slice = false;

struct Stage;
struct Chain {  // immutable once published
    std::vector<Stage*> before, after;  // in call order
    Stage* replace = nullptr;
    const std::string* program = nullptr;
    int32_t index = -1;
};

struct Stage {
    KilnHandle handle = 0;
    KilnOwner owner = 0;
    uint32_t kind = 0;
    int pool = 0;
    const std::string* program = nullptr;
    int32_t stage = 0, priority = 0;
    uint64_t seq = 0;
    KilnWrapStageFn fn = nullptr;
    void* user = nullptr;
    std::atomic<uint32_t> state{KILN_WRAP_PENDING};
    std::atomic<uint32_t> reason{KILN_WRAP_OK};
    std::atomic<uint32_t> disabled{KILN_WRAP_OK};  // set on the slice path: KILN_WRAP_FAULT or _BUDGET
    std::atomic<bool> gone{false};                 // unwrapped, released or its owner revoked: no call starts
    std::atomic<uint32_t> calls_in{0};             // calls past the gate and not yet returned
    std::atomic<uint32_t> fault_code{0};
    std::atomic<int32_t> index{-1};
    // g_mu
    bool remove = false;          // unwrap queued
    uint32_t remove_reason = KILN_WRAP_UNWRAPPED;
    uint32_t refs = 0;            // chains (published, or retired and not yet freed) that hold it
    bool logged = false;
    char detail[128]{};
    // stats (slice path, relaxed)
    std::atomic<uint64_t> calls{0}, entities{0}, faults{0}, overruns{0}, ns{0};
    std::atomic<uint32_t> consecutive{0};
    std::atomic<uint32_t> tids[16]{};
};

enum SlotState : uint8_t { S_FREE, S_LIVE, S_COOLING, S_RETAINED };

struct Slot {
    std::atomic<UpdateFn> orig{nullptr};
    std::atomic<uintptr_t> entry{0};
    std::atomic<const Chain*> chain{nullptr};
    std::atomic<uint32_t> in_flight{0};
    std::atomic<uint64_t> slices{0}, entities{0}, forwarded{0}, faults{0};
    // g_mu
    SlotState state = S_FREE;
    uint32_t cooling = 0;
    const std::string* program = nullptr;
};

struct Pool {
    uint32_t kind = 0;
    Slot slots[kWrapSlots];
};
Pool g_pools[kWrapPools];

// Per pool and program (g_mu): the slot it holds in the current world, and what earlier slots of this world counted.
struct Prog {
    int slot = -1;
    uint64_t slices = 0, entities = 0, forwarded = 0, faults = 0;
};

struct Retired {
    int pool, slot;
    const Chain* chain;
};

std::mutex g_mu;
std::unordered_map<KilnHandle, std::unique_ptr<Stage>> g_stages;
std::deque<KilnHandle> g_finished;
std::map<std::pair<int, const std::string*>, Prog> g_progs;
std::vector<Retired> g_retired;
KilnHandle g_next = 1;
uint64_t g_seq = 0;
bool g_ok = false;                    // the layout wraps need resolved
std::atomic<uint32_t> g_external{0};  // kiln.types' external runner, as last seen
uint32_t g_external_pre = 0;          // as the last Pre saw it (g_mu): what the capability row says
const KilnCapsApi* g_wrap_caps = nullptr;
std::atomic<uint32_t> g_active{0}, g_pending{0};
std::chrono::steady_clock::time_point g_next_reap{};

// ---- the trampolines ----

void dispatch(int pool, int slot, void* ctx);
template <int P, int I>
void tramp(void* ctx) {
    dispatch(P, I, ctx);
}
template <int P, int... I>
constexpr std::array<UpdateFn, sizeof...(I)> make_tramps(std::integer_sequence<int, I...>) {
    return {&tramp<P, I>...};
}
const std::array<UpdateFn, kWrapSlots> kTramps[kWrapPools] = {
    make_tramps<0>(std::make_integer_sequence<int, kWrapSlots>{}),
    make_tramps<1>(std::make_integer_sequence<int, kWrapSlots>{}),
};
struct TrampAt {
    uintptr_t address;
    int pool, slot;
};
std::vector<TrampAt> g_by_address;  // sorted; written once at boot (wraps_init), read lock-free after

bool tramp_of(uintptr_t address, int* pool, int* slot) {
    const auto it = std::lower_bound(g_by_address.begin(), g_by_address.end(), address,
                                     [](const TrampAt& t, uintptr_t a) { return t.address < a; });
    if (it == g_by_address.end() || it->address != address) return false;
    *pool = it->pool;
    *slot = it->slot;
    return true;
}

// ---- the slice path ----

struct Frame {
    UpdateFn orig;
    ProgramCtx* ctx;
    uint32_t count;
    int32_t stage;
    bool original_called;
    uint64_t original_ns;  // the game's update inside game_slice_run_original: not charged to the Replace
};
thread_local Frame* t_frame = nullptr;

uint64_t now_ns() {
    return static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(
                                     std::chrono::steady_clock::now().time_since_epoch()).count());
}

void add_tid(Stage& st, uint32_t tid) {
    for (auto& t : st.tids) {
        uint32_t cur = t.load(std::memory_order_relaxed);
        if (cur == tid) return;
        if (cur == 0 && t.compare_exchange_strong(cur, tid, std::memory_order_relaxed)) return;
        if (cur == tid) return;
    }
}

enum CallResult { CALL_RAN, CALL_FAULTED, CALL_SKIPPED };

// One stage, through its gate. CALL_FAULTED: it faulted and is disabled from now on; CALL_SKIPPED: it is gone.
CallResult call(Slot& s, Stage& st, KilnGameSlice& slice, Frame& f, int32_t stage) {
    st.calls_in.fetch_add(1, std::memory_order_seq_cst);
    if (st.gone.load(std::memory_order_seq_cst)) {
        st.calls_in.fetch_sub(1, std::memory_order_release);
        return CALL_SKIPPED;
    }
    slice.stage = stage;
    f.stage = stage;
    Frame* const outer_frame = t_frame;
    const bool outer = t_in_slice;
    t_frame = &f;
    t_in_slice = true;
    unsigned long code = 0;
    const uint64_t original_before = f.original_ns;
    const uint64_t t0 = now_ns();
    const bool ok = seh_wrap_stage(st.fn, &slice, st.user, &code);
    const uint64_t total = now_ns() - t0, original = f.original_ns - original_before;
    const uint64_t dt = total > original ? total - original : 0;  // the stage's own time
    t_frame = outer_frame;
    t_in_slice = outer;
    st.calls_in.fetch_sub(1, std::memory_order_release);
    st.calls.fetch_add(1, std::memory_order_relaxed);
    st.entities.fetch_add(slice.count, std::memory_order_relaxed);
    st.ns.fetch_add(dt, std::memory_order_relaxed);
    add_tid(st, current_thread_id());
    if (!ok) {
        st.faults.fetch_add(1, std::memory_order_relaxed);
        s.faults.fetch_add(1, std::memory_order_relaxed);
        st.fault_code.store(static_cast<uint32_t>(code), std::memory_order_relaxed);
        st.disabled.store(KILN_WRAP_FAULT, std::memory_order_release);
        return CALL_FAULTED;
    }
    if (dt > g_budget_ns.load(std::memory_order_relaxed)) {
        st.overruns.fetch_add(1, std::memory_order_relaxed);
        if (st.consecutive.fetch_add(1, std::memory_order_relaxed) + 1 >= kOverrunsToDisable)
            st.disabled.store(KILN_WRAP_BUDGET, std::memory_order_release);
    } else {
        st.consecutive.store(0, std::memory_order_relaxed);
    }
    return CALL_RAN;
}

bool live(const Stage* st) {
    return !st->disabled.load(std::memory_order_acquire) && !st->gone.load(std::memory_order_relaxed);
}

// The engine aborts on an index between 0 and the slice's count; a stage that faulted mid-iteration leaves one.
void finish_index(ProgramCtx* ctx, uint32_t count) {
    if (ctx->index != 0 && ctx->index < count) ctx->index = count;
}

void run(int pool, Slot& s, const Chain& c, ProgramCtx* ctx, UpdateFn orig) {
    const uint32_t kind = g_pools[pool].kind;
    uint64_t count64 = 0;
    std::memcpy(&count64, ctx->job + L.job_entity_count, 8);
    const uint32_t count = count64 > UINT32_MAX ? UINT32_MAX : static_cast<uint32_t>(count64);
    s.slices.fetch_add(1, std::memory_order_relaxed);
    s.entities.fetch_add(count, std::memory_order_relaxed);
    Frame f{orig, ctx, count, -1, false, 0};
    KilnGameSlice slice{};
    slice.size = sizeof slice;
    slice.world_kind = kind;
    slice.program_ctx = reinterpret_cast<KilnProgramCtx*>(ctx);
    slice.count = count;
    slice.thread_is_world = current_thread_id() == game_programs_world_tid(kind);
    if (count) std::memcpy(&slice.entity_ids, ctx->job + L.job_entity_list, sizeof(void*));
    slice.entity_stride = 16;
    slice.program_index = c.index;
    slice.step = game_programs_step(kind);
    slice.program = kstr(*c.program);
    slice.internal = &f;

    for (Stage* st : c.before) {
        if (!live(st)) continue;
        call(s, *st, slice, f, KILN_WRAP_BEFORE);
        ctx->index = 0;  // the update starts from the slice's first entity, also after a Before that faulted mid-way
    }
    if (c.replace && live(c.replace)) {
        const CallResult r = call(s, *c.replace, slice, f, KILN_WRAP_REPLACE);
        if (r == CALL_RAN) {
            finish_index(ctx, count);
        } else if (f.original_called) {
            ctx->index = count;  // the update ran; it never runs twice in a slice
        } else {
            ctx->index = 0;      // the Replace is off (a fault) or gone: this slice is the game's again
            orig(ctx);
        }
    } else {
        orig(ctx);
    }
    for (Stage* st : c.after) {
        if (!live(st)) continue;
        if (call(s, *st, slice, f, KILN_WRAP_AFTER) == CALL_FAULTED) finish_index(ctx, count);
    }
    finish_index(ctx, count);
}

bool kind_is(const ProgramCtx* ctx, uint32_t kind) {
    const uint8_t* storage = nullptr;
    std::memcpy(&storage, ctx->job, sizeof storage);  // J+0: the slice's storage
    return storage && storage[L.storage_kind] == kind;
}

void dispatch(int pool, int slot, void* raw) {
    Slot& s = g_pools[pool].slots[slot];
    s.in_flight.fetch_add(1, std::memory_order_seq_cst);
    const UpdateFn orig = s.orig.load(std::memory_order_acquire);
    const Chain* c = s.chain.load(std::memory_order_seq_cst);
    auto* ctx = static_cast<ProgramCtx*>(raw);
    if (!c || !kind_is(ctx, g_pools[pool].kind)) {
        // no stage left, a slot kept for a foreign swap on top, or another storage (the prediction world runs the
        // client world's table): the game's update, untouched
        s.forwarded.fetch_add(1, std::memory_order_relaxed);
        if (orig) orig(raw);
    } else if (orig) {
        run(pool, s, *c, ctx, orig);
    }
    s.in_flight.fetch_sub(1, std::memory_order_release);
}

// ---- registry (g_mu) ----

const char* stage_name(int32_t stage) {
    return stage == KILN_WRAP_BEFORE ? "Before" : stage == KILN_WRAP_REPLACE ? "Replace" : "After";
}

const char* reason_text(uint32_t r) {
    switch (r) {
        case KILN_WRAP_FAULT: return "a fault";
        case KILN_WRAP_BUDGET: return "its budget (10 slices in a row over it)";
        case KILN_WRAP_NATIVE_SET: return "native SET loaded";
        case KILN_WRAP_FOREIGN: return "another tool swapped the program's update";
        case KILN_WRAP_NO_PROGRAM: return "no such program in this world";
        case KILN_WRAP_NO_UPDATE: return "the program has no update in this world";
        case KILN_WRAP_POOL_FULL: return "every trampoline of this world kind is taken";
        case KILN_WRAP_UNWRAPPED: return "unwrapped";
        case KILN_WRAP_OWNER_GONE: return "its owner was revoked";
        case KILN_WRAP_REPLACE_TAKEN: return "another wrap replaces the program";
        default: return "";
    }
}

// kiln.types' answer to "does another runtime own the program runner?" (native SET names programs by their update
// pointer, which a wrap changes).
const KilnTypesApi* g_types = nullptr;
std::once_flag g_types_once;
uint32_t external_runner() {
    std::call_once(g_types_once, [] {
        g_types = program_types();
        if (g_types && g_types->header.size < offsetof(KilnTypesApi, external_runner) + sizeof(void*)) g_types = nullptr;
    });
    const uint32_t v = g_types && g_types->external_runner ? g_types->external_runner() : 0;
    g_external.store(v, std::memory_order_relaxed);
    return v;
}

void report_row(const char* name, uint32_t state, uint32_t reason, const char* text) {
    if (g_wrap_caps && g_wrap_caps->report)
        g_wrap_caps->report(g_owner, KilnStr{name, std::strlen(name)}, state, reason, KilnStr{text, std::strlen(text)});
}

void count_states() {
    uint32_t active = 0, pending = 0;
    for (const auto& [h, st] : g_stages) {
        const uint32_t s = st->state.load();
        active += s == KILN_WRAP_ACTIVE;
        pending += s == KILN_WRAP_PENDING;
    }
    g_active.store(active);
    g_pending.store(pending);
}

void finish(Stage& st, uint32_t state, uint32_t reason) {
    st.state.store(state);
    st.reason.store(reason);
    st.index.store(-1);
    g_finished.push_back(st.handle);
}

// Frees finished stages no chain holds once more than kFinishedKept are kept.
void trim_finished() {
    size_t guard = g_finished.size();
    while (g_finished.size() > kFinishedKept && guard--) {
        const KilnHandle h = g_finished.front();
        g_finished.pop_front();
        const auto it = g_stages.find(h);
        if (it == g_stages.end()) continue;
        if (it->second->refs) {
            g_finished.push_back(h);  // still in a chain that waits to be freed
            continue;
        }
        g_stages.erase(it);
    }
}

void free_chain(const Chain* c) {
    for (Stage* st : c->before) --st->refs;
    for (Stage* st : c->after) --st->refs;
    if (c->replace) --c->replace->refs;
    delete c;
}

// Publishes `next` (may be null) in the slot; the old chain is freed now if nothing is in flight, else later.
void publish(int pool, int slot, const Chain* next) {
    Slot& s = g_pools[pool].slots[slot];
    const Chain* old = s.chain.exchange(next, std::memory_order_seq_cst);
    if (!old) return;
    if (s.in_flight.load(std::memory_order_seq_cst) == 0) free_chain(old);
    else g_retired.push_back({pool, slot, old});
}

void reap_retired() {
    for (size_t i = 0; i < g_retired.size();) {
        const Retired r = g_retired[i];
        if (g_pools[r.pool].slots[r.slot].in_flight.load(std::memory_order_seq_cst) == 0) {
            free_chain(r.chain);
            g_retired[i] = g_retired.back();
            g_retired.pop_back();
        } else {
            ++i;
        }
    }
}

Prog& prog(int pool, const std::string* name) { return g_progs[{pool, name}]; }

// The slot's counters go to the program's totals of this world.
void bank(int pool, int slot) {
    Slot& s = g_pools[pool].slots[slot];
    if (!s.program) return;
    Prog& p = prog(pool, s.program);
    p.slices += s.slices.exchange(0);
    p.entities += s.entities.exchange(0);
    p.forwarded += s.forwarded.exchange(0);
    p.faults += s.faults.exchange(0);
    if (p.slot == slot) p.slot = -1;
}

int alloc_slot(int pool) {
    for (int i = 0; i < kWrapSlots; ++i) {
        Slot& s = g_pools[pool].slots[i];
        if (s.state == S_FREE && s.in_flight.load() == 0) return i;
    }
    return -1;
}

// Puts the game's update back. False when another tool's pointer sits in the entry: the slot then forwards for good.
bool restore(int pool, int slot, bool touch_entry) {
    Slot& s = g_pools[pool].slots[slot];
    bool back = false;
    if (touch_entry) {
        uintptr_t seen = 0;
        const auto tr = reinterpret_cast<uintptr_t>(kTramps[pool][slot]);
        const auto orig = reinterpret_cast<uintptr_t>(s.orig.load());
        const uintptr_t at = s.entry.load() + kEntryUpdateAt;
        back = seh_cas_ptr(at, tr, orig, &seen) && seen == tr;
        if (!back)
            KW_W("program %s: its update is no longer kiln.world's trampoline (0x%llx); left in place, forwarding, its "
                 "trampoline is never used again", s.program ? s.program->c_str() : "?", static_cast<unsigned long long>(seen));
    }
    publish(pool, slot, nullptr);
    bank(pool, slot);
    s.state = back ? S_COOLING : S_RETAINED;
    s.cooling = 0;
    return back;
}

Chain* build_chain(int pool, const std::string* name, int32_t index) {
    std::vector<Stage*> befores, afters;
    Stage* replace = nullptr;
    for (auto& [h, up] : g_stages) {
        Stage* st = up.get();
        if (st->pool != pool || st->program != name || st->state.load() != KILN_WRAP_ACTIVE || st->remove) continue;
        if (st->disabled.load()) continue;
        if (st->stage == KILN_WRAP_BEFORE) befores.push_back(st);
        else if (st->stage == KILN_WRAP_AFTER) afters.push_back(st);
        else if (!replace || st->seq < replace->seq) replace = st;
    }
    if (befores.empty() && afters.empty() && !replace) return nullptr;
    // Befores: high priority first, then registration order; Afters: the exact reverse (an onion).
    auto first = [](const Stage* a, const Stage* b) { return a->priority != b->priority ? a->priority > b->priority : a->seq < b->seq; };
    std::sort(befores.begin(), befores.end(), first);
    std::sort(afters.begin(), afters.end(), [&](const Stage* a, const Stage* b) { return first(b, a); });
    auto* c = new Chain{std::move(befores), std::move(afters), replace, name, index};
    for (Stage* st : c->before) ++st->refs;
    for (Stage* st : c->after) ++st->refs;
    if (c->replace) ++c->replace->refs;
    return c;
}

void refuse(Stage& st, uint32_t reason, const char* detail) {
    std::snprintf(st.detail, sizeof st.detail, "%s", detail);
    KW_W("wrap %llu (%s of %s, world %u, owner %llu) refused: %s", static_cast<unsigned long long>(st.handle),
         stage_name(st.stage), st.program->c_str(), st.kind, static_cast<unsigned long long>(st.owner), detail);
    finish(st, KILN_WRAP_REFUSED, reason);
}

std::string module_of(uintptr_t address);  // below (Windows: the DLL that holds an address)

// A program's chain after this Pre's changes: installs the trampoline for its first stage, restores the update after
// its last. Stages are ACTIVE (or not) already.
void settle(int pool, const std::string* name) {
    Prog& p = prog(pool, name);
    ProgramRef ref;
    const bool here = game_program_lookup(g_pools[pool].kind, *name, &ref);
    Chain* c = here ? build_chain(pool, name, ref.index) : nullptr;
    if (p.slot >= 0) {
        if (c) publish(pool, p.slot, c);
        else restore(pool, p.slot, true);
        return;
    }
    if (!c) return;
    // first stage: the entry must hold the program's own update, never another tool's pointer
    uintptr_t cur = 0, own = 0;
    seh_copy(&cur, reinterpret_cast<const void*>(ref.entry + kEntryUpdateAt), sizeof cur);
    if (ref.desc) seh_copy(&own, reinterpret_cast<const void*>(ref.desc + offsetof(EngineDesc, update)), sizeof own);
    std::string why;
    uint32_t reason = KILN_WRAP_OK;
    int slot = -1;
    if (!cur || cur != own) {
        reason = KILN_WRAP_FOREIGN;
        why = "the program's update was swapped by another tool (" + module_of(cur) + "); kiln.world never stacks on one";
    } else if ((slot = alloc_slot(pool)) < 0) {
        reason = KILN_WRAP_POOL_FULL;
        why = reason_text(reason);
    }
    if (reason == KILN_WRAP_OK) {
        Slot& s = g_pools[pool].slots[slot];
        s.orig.store(reinterpret_cast<UpdateFn>(cur));
        s.entry.store(ref.entry);
        s.program = name;
        s.slices = s.entities = s.forwarded = s.faults = 0;
        s.chain.store(c, std::memory_order_seq_cst);  // the chain before the pointer: a slice that finds the
        uintptr_t seen = 0;                             // trampoline finds its chain
        if (seh_cas_ptr(ref.entry + kEntryUpdateAt, cur, reinterpret_cast<uintptr_t>(kTramps[pool][slot]), &seen) && seen == cur) {
            s.state = S_LIVE;
            p.slot = slot;
            return;
        }
        s.chain.store(nullptr);
        s.program = nullptr;
        reason = KILN_WRAP_FOREIGN;
        why = "the program's update changed while kiln.world swapped it (" + module_of(seen) + ")";
    }
    // refused: every stage that was to start the chain
    for (Stage* st : c->before) --st->refs;
    for (Stage* st : c->after) --st->refs;
    if (c->replace) --c->replace->refs;
    for (Stage* st : c->before) refuse(*st, reason, why.c_str());
    for (Stage* st : c->after) refuse(*st, reason, why.c_str());
    if (c->replace) refuse(*c->replace, reason, why.c_str());
    delete c;
}

void log_disabled(Stage& st) {
    if (st.logged) return;
    st.logged = true;
    const uint32_t d = st.disabled.load();
    if (d == KILN_WRAP_FAULT)
        std::snprintf(st.detail, sizeof st.detail, "faulted (0x%08x); this stage is off", st.fault_code.load());
    else
        std::snprintf(st.detail, sizeof st.detail, "over its budget of %llu us in 10 slices in a row; this stage is off",
                      static_cast<unsigned long long>(g_budget_ns.load() / 1000));
    KW_E("wrap %llu (%s of %s, world %u, owner %llu): %s", static_cast<unsigned long long>(st.handle), stage_name(st.stage),
         st.program->c_str(), st.kind, static_cast<unsigned long long>(st.owner), st.detail);
}

// The core has no owner-liveness query: a post to a revoked owner fails. Probed at most once a second, so a revoked
// owner's stages can run up to a second longer. To be replaced by kiln.hooks' subscribe_owner_retiring (03-camera S1,
// row hooks.owner_cleanup) once that is on integration: then the stages go before the owner's code does.
bool owner_revoked(KilnOwner owner) { return g_api->post(owner, [](void*) {}, nullptr) == KILN_ERR_REVOKED; }

// Takes a stage out (g_mu): no call of it starts from here on; the next Pre removes it from its chain.
void retire_stage(Stage& st, uint32_t reason) {
    if (!st.remove) st.remove = true, st.remove_reason = reason;
    st.gone.store(true, std::memory_order_seq_cst);
}

// Every stage of a pool back to PENDING (re-applied by name in the next world) or finished; slots restored (the world
// is still there) or kept (it may be gone).
void drop_pool(int pool, bool touch_entries) {
    for (int i = 0; i < kWrapSlots; ++i) {
        Slot& s = g_pools[pool].slots[i];
        if (s.state == S_LIVE) restore(pool, i, touch_entries);
    }
    for (auto& [h, st] : g_stages) {
        if (st->pool != pool || st->state.load() != KILN_WRAP_ACTIVE) continue;
        if (st->remove) finish(*st, KILN_WRAP_REMOVED, st->remove_reason);
        else st->state.store(KILN_WRAP_PENDING), st->index.store(-1);
    }
    for (auto it = g_progs.begin(); it != g_progs.end();)
        it = it->first.first == pool ? g_progs.erase(it) : std::next(it);
    reap_retired();
    count_states();
}

}  // namespace

// ---- service ----

bool game_in_slice() { return t_in_slice; }

void wraps_init() {
    g_by_address.clear();
    for (int p = 0; p < kWrapPools; ++p)
        for (int i = 0; i < kWrapSlots; ++i) g_by_address.push_back({reinterpret_cast<uintptr_t>(kTramps[p][i]), p, i});
    std::sort(g_by_address.begin(), g_by_address.end(), [](const TrampAt& a, const TrampAt& b) { return a.address < b.address; });
    g_pools[0].kind = KILN_WORLD_SERVER;
    g_pools[1].kind = KILN_WORLD_CLIENT;
    g_ok = L.programs_table >= 0 && L.programs_count >= 0 && L.programs_stride > 0 && L.storage_kind >= 0 &&
           L.job_entity_count >= 0 && L.job_entity_list >= 0 && L.ctx_index == offsetof(ProgramCtx, index) &&
           g_api->feature(kstr("world.programs"));
    g_wrap_caps = static_cast<const KilnCapsApi*>(g_api->service(kstr(KILN_CAPS_SERVICE), 1));
    if (g_ok)
        report_row("systems.wraps", KILN_CAP_AVAILABLE, KILN_CAP_REASON_NONE, "Before, After and Replace on the game's programs");
    else
        report_row("systems.wraps", KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_SYMBOL_MISSING,
               "the program table or job layout did not resolve on this build (world.programs)");
    const bool client = g_api->size >= offsetof(KilnApi, session) + sizeof(void*) && g_api->side() == KILN_SIDE_CLIENT;
    if (!client)
        report_row("systems.wraps.client", KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_WRONG_SIDE, "a client world runs only in the game client");
    else if (g_ok)
        report_row("systems.wraps.client", KILN_CAP_AVAILABLE, KILN_CAP_REASON_NONE, "wraps on the client world's programs");
    else
        report_row("systems.wraps.client", KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_SYMBOL_MISSING,
               "the program table or job layout did not resolve on this build (world.programs)");
}

const char* game_wraps_off_reason() {
    if (!g_ok) return "wraps: the program table or job layout did not resolve on this build";
    switch (g_external.load()) {
        case 1: return "wraps: native SET is loaded (it names programs by their update pointer)";
        case 2: return "wraps: the loaded modules could not be read, so native SET may be loaded";
        default: return "";
    }
}

void wraps_pre(uint32_t kind, uintptr_t) {
    const int pool = wrap_pool(kind);
    if (pool < 0 || !g_ok) return;
    const uint32_t ext = external_runner();
    // Revoked owners (at most once a second): probed outside g_mu (post() is the core's), retired under it.
    std::vector<KilnOwner> owners, revoked;
    {
        std::lock_guard lock(g_mu);
        const auto now = std::chrono::steady_clock::now();
        if (now >= g_next_reap) {
            g_next_reap = now + std::chrono::seconds(1);
            for (auto& [h, st] : g_stages)
                if ((st->state.load() == KILN_WRAP_PENDING || st->state.load() == KILN_WRAP_ACTIVE) && !st->remove &&
                    std::find(owners.begin(), owners.end(), st->owner) == owners.end())
                    owners.push_back(st->owner);
        }
    }
    for (KilnOwner o : owners)
        if (owner_revoked(o)) revoked.push_back(o);
    // The capability row is reported after g_mu is released: kiln.caps calls its subscribers on this thread.
    struct Row {
        bool set = false;
        uint32_t state = 0, reason = 0;
        const char* text = "";
    } row;
    {
    std::lock_guard lock(g_mu);
    const uint32_t ext_before = g_external_pre;
    g_external_pre = ext;
    if (ext && ext != ext_before) {
        KW_W("%s: every wrap is removed", game_wraps_off_reason());
        row = {true, KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_POLICY, game_wraps_off_reason()};
    } else if (!ext && ext_before) {
        KW_I("wraps: native SET is not loaded after all; new wraps are taken again (removed ones are not restored)");
        row = {true, KILN_CAP_AVAILABLE, KILN_CAP_REASON_NONE, "Before, After and Replace on the game's programs"};
    }
    for (KilnOwner o : revoked)
        for (auto& [h, st] : g_stages)
            if (st->owner == o && (st->state.load() == KILN_WRAP_PENDING || st->state.load() == KILN_WRAP_ACTIVE))
                retire_stage(*st, KILN_WRAP_OWNER_GONE);
    std::vector<const std::string*> touched;
    auto touch = [&](const std::string* n) {
        if (std::find(touched.begin(), touched.end(), n) == touched.end()) touched.push_back(n);
    };
    std::vector<Stage*> order;
    for (auto& [h, st] : g_stages)
        if (st->pool == pool) order.push_back(st.get());
    std::sort(order.begin(), order.end(), [](const Stage* a, const Stage* b) { return a->seq < b->seq; });
    for (Stage* st : order) {
        const uint32_t state = st->state.load();
        if (state != KILN_WRAP_PENDING && state != KILN_WRAP_ACTIVE) continue;
        if (ext && !st->remove) retire_stage(*st, KILN_WRAP_NATIVE_SET);
        if (state == KILN_WRAP_PENDING) {
            if (st->remove) {
                finish(*st, KILN_WRAP_REMOVED, st->remove_reason);
                continue;
            }
            ProgramRef ref;
            if (!game_program_lookup(kind, *st->program, &ref)) {
                if (game_programs_has_directory(kind)) refuse(*st, KILN_WRAP_NO_PROGRAM, "no such program in this world");
                continue;  // no directory yet: it waits for the world
            }
            if (ref.no_update) {
                refuse(*st, KILN_WRAP_NO_UPDATE, "the program has no update in this world");
                continue;
            }
            st->state.store(KILN_WRAP_ACTIVE);
            st->index.store(ref.index);
            touch(st->program);
        } else if (st->remove) {
            touch(st->program);
            finish(*st, KILN_WRAP_REMOVED, st->remove_reason);  // out of the chain settle() publishes below
        } else if (st->disabled.load() && !st->logged) {
            log_disabled(*st);
            touch(st->program);
        }
    }
    for (const std::string* name : touched) settle(pool, name);
    reap_retired();
    // Restored slots cool for kCoolingPres Pres with nothing in flight before they are used again.
    for (int i = 0; i < kWrapSlots; ++i) {
        Slot& s = g_pools[pool].slots[i];
        if (s.state != S_COOLING) continue;
        if (s.in_flight.load() != 0) s.cooling = 0;
        else if (++s.cooling >= kCoolingPres) s.state = S_FREE, s.program = nullptr, s.entry = 0;
    }
    trim_finished();
    count_states();
    }
    if (row.set) report_row("systems.wraps", row.state, row.reason, row.text);
}

void wraps_world_event(const KilnWorldEvent* event) {
    const int pool = event ? wrap_pool(event->kind) : -1;
    if (pool < 0) return;
    std::lock_guard lock(g_mu);
    bool live_slots = false;
    for (int i = 0; i < kWrapSlots && !live_slots; ++i) live_slots = g_pools[pool].slots[i].state == S_LIVE;
    if (event->event == KILN_WORLD_STOPPING) {
        if (live_slots) KW_I("world %u stops: its wraps are taken out and wait for the next world", event->kind);
        drop_pool(pool, true);  // the world is still intact: the entries go back
    } else if (event->event == KILN_WORLD_STARTED && live_slots) {
        // a new world without a stop of the last (no world.lifecycle): its table may be gone, so nothing is written
        KW_W("world %u: a new world without a stop of the last; its trampolines are kept, the wraps re-applied", event->kind);
        drop_pool(pool, false);
    }
}

uintptr_t wraps_resolve(uintptr_t entry, uintptr_t current) {
    int pool = 0, slot = 0;
    if (!tramp_of(current, &pool, &slot)) return current;
    const Slot& s = g_pools[pool].slots[slot];
    return s.entry.load(std::memory_order_acquire) == entry ? reinterpret_cast<uintptr_t>(s.orig.load()) : current;
}

void game_program_wrap_stats(uint32_t kind, const std::string* name, KilnGameProgramInfo* out) {
    const int pool = wrap_pool(kind);
    if (pool < 0) return;
    std::lock_guard lock(g_mu);
    const auto it = g_progs.find({pool, name});
    uint32_t wraps = 0;
    for (const auto& [h, st] : g_stages)
        if (st->pool == pool && st->program == name && st->state.load() == KILN_WRAP_ACTIVE) ++wraps;
    out->wraps = wraps;
    if (it == g_progs.end()) return;
    const Prog& p = it->second;
    uint64_t slices = p.slices, entities = p.entities, forwarded = p.forwarded, faults = p.faults;
    if (p.slot >= 0) {
        const Slot& s = g_pools[pool].slots[p.slot];
        slices += s.slices.load(), entities += s.entities.load(), forwarded += s.forwarded.load(), faults += s.faults.load();
    }
    out->slices = slices;
    out->entities = entities;
    out->forwarded = forwarded;
    out->faults = static_cast<uint32_t>(std::min<uint64_t>(faults, UINT32_MAX));
}

void game_wraps_status(KilnGameProgramsStatus* out) {
    out->wraps_active = g_active.load();
    out->wraps_pending = g_pending.load();
}

// ---- the table ----

KilnResult KILN_CALL game_program_wrap(KilnOwner owner, uint32_t kind, int32_t index, int32_t stage, int32_t priority,
                                       KilnWrapStageFn fn, void* user, KilnHandle* out) {
    if (out) *out = 0;
    const int pool = wrap_pool(kind);
    if (!out || !fn || !owner || stage < KILN_WRAP_BEFORE || stage > KILN_WRAP_AFTER || pool < 0)
        return KILN_ERR_INVALID_ARGUMENT;
    if (t_in_slice || t_in_program) return KILN_ERR_WRONG_THREAD;
    if (!g_ok) return KILN_ERR_UNAVAILABLE;
    if (external_runner()) {
        KW_W("a wrap was refused: %s", game_wraps_off_reason());
        return KILN_ERR_UNAVAILABLE;
    }
    if (kind == KILN_WORLD_CLIENT) {
        const KilnCapsApi* caps = g_wrap_caps;
        if (!caps || kiln_caps_held(caps, kstr("systems.wraps.client"))) return KILN_ERR_UNAVAILABLE;
        KilnCapRow row{};
        row.size = sizeof row;
        if (caps->find(kstr("systems.wraps.client"), &row) != KILN_OK || row.state != KILN_CAP_AVAILABLE)
            return KILN_ERR_UNAVAILABLE;
    }
    ProgramRef ref;
    if (!game_program_at(kind, index, &ref)) return KILN_ERR_NOT_FOUND;
    if (ref.no_update) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    auto st = std::make_unique<Stage>();
    st->handle = g_next++;
    st->owner = owner;
    st->kind = kind;
    st->pool = pool;
    st->program = ref.name;
    st->stage = stage;
    st->priority = priority;
    st->seq = ++g_seq;
    st->fn = fn;
    st->user = user;
    // One Replace per program: a second one is a refusal (a handle in state REFUSED, reason REPLACE_TAKEN), not an
    // argument error, so a caller can tell the mod that holds it.
    if (stage == KILN_WRAP_REPLACE)
        for (const auto& [h, other] : g_stages) {
            const uint32_t s = other->state.load();
            if (other->pool == pool && other->program == ref.name && other->stage == KILN_WRAP_REPLACE && !other->remove &&
                (s == KILN_WRAP_PENDING || s == KILN_WRAP_ACTIVE)) {
                std::snprintf(st->detail, sizeof st->detail, "owner %llu replaces %s already (wrap %llu)",
                              static_cast<unsigned long long>(other->owner), ref.name->c_str(),
                              static_cast<unsigned long long>(h));
                KW_W("a second Replace of %s (world %u) was refused: owner %llu asked (wrap %llu), owner %llu replaces it "
                     "(wrap %llu)", ref.name->c_str(), kind, static_cast<unsigned long long>(owner),
                     static_cast<unsigned long long>(st->handle), static_cast<unsigned long long>(other->owner),
                     static_cast<unsigned long long>(h));
                finish(*st, KILN_WRAP_REFUSED, KILN_WRAP_REPLACE_TAKEN);
                break;
            }
        }
    *out = st->handle;
    g_stages.emplace(st->handle, std::move(st));
    trim_finished();
    count_states();
    return KILN_OK;
}

KilnResult KILN_CALL game_program_unwrap(KilnHandle h) {
    if (t_in_slice) return KILN_ERR_WRONG_THREAD;
    std::lock_guard lock(g_mu);
    const auto it = g_stages.find(h);
    if (it == g_stages.end()) return KILN_ERR_NOT_FOUND;
    Stage& st = *it->second;
    const uint32_t s = st.state.load();
    if (s == KILN_WRAP_PENDING) {
        finish(st, KILN_WRAP_REMOVED, KILN_WRAP_UNWRAPPED);
        count_states();
    } else if (s == KILN_WRAP_ACTIVE) {
        retire_stage(st, KILN_WRAP_UNWRAPPED);  // no call starts from here on
    }
    return KILN_OK;
}

KilnResult KILN_CALL game_wrap_info(KilnHandle h, KilnGameWrapInfo* out) {
    if (!out || out->size < sizeof(KilnGameWrapInfo)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    const auto it = g_stages.find(h);
    if (it == g_stages.end()) return KILN_ERR_NOT_FOUND;
    const Stage& st = *it->second;
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    out->state = st.state.load();
    out->disabled = st.disabled.load();
    out->reason = st.reason.load();
    out->world_kind = st.kind;
    out->stage = st.stage;
    out->priority = st.priority;
    out->program_index = st.index.load();
    out->program = kstr(*st.program);
    out->calls = st.calls.load();
    out->entities = st.entities.load();
    out->faults = st.faults.load();
    out->overruns = st.overruns.load();
    out->total_ns = st.ns.load();
    for (const auto& t : st.tids) out->threads += t.load() != 0;
    out->fault_code = st.fault_code.load();
    out->remove_queued = st.remove && st.state.load() == KILN_WRAP_ACTIVE;
    out->gone = st.gone.load();
    out->running = st.calls_in.load(std::memory_order_seq_cst);
    std::snprintf(out->detail, sizeof out->detail, "%s",
                  st.detail[0] ? st.detail : (out->reason ? reason_text(out->reason) : ""));
    return KILN_OK;
}

KilnResult KILN_CALL game_wraps_release(KilnOwner owner, uint32_t* out_pending) {
    if (out_pending) *out_pending = 0;
    if (!owner) return KILN_ERR_INVALID_ARGUMENT;
    if (t_in_slice) return KILN_ERR_WRONG_THREAD;
    std::lock_guard lock(g_mu);
    std::vector<Stage*> mine;
    for (auto& [h, up] : g_stages) {
        Stage& st = *up;
        if (st.owner != owner) continue;
        const uint32_t s = st.state.load();
        if (s == KILN_WRAP_PENDING) finish(st, KILN_WRAP_REMOVED, KILN_WRAP_UNWRAPPED);
        else if (s == KILN_WRAP_ACTIVE) retire_stage(st, KILN_WRAP_UNWRAPPED);
        mine.push_back(&st);
    }
    // After every `gone` is set: a call past a gate before that is still counted in calls_in, any later one is not
    // let in. A world counts while one of its slices may be inside one of the owner's stages.
    bool running[kWrapPools] = {};
    for (Stage* st : mine)
        if (st->calls_in.load(std::memory_order_seq_cst)) running[st->pool] = true;
    uint32_t pending = 0;
    for (bool r : running) pending += r;
    count_states();
    if (out_pending) *out_pending = pending;
    return KILN_OK;
}

KilnResult KILN_CALL game_slice_run_original(KilnGameSlice* slice) {
    if (!slice || slice->size < sizeof(KilnGameSlice)) return KILN_ERR_INVALID_ARGUMENT;
    Frame* f = t_frame;
    if (!t_in_slice || !f || slice->internal != f) return KILN_ERR_WRONG_THREAD;
    if (f->stage != KILN_WRAP_REPLACE || f->original_called) return KILN_ERR_INVALID_ARGUMENT;
    f->original_called = true;
    f->ctx->index = 0;
    const uint64_t t0 = now_ns();
    f->orig(f->ctx);
    f->original_ns += now_ns() - t0;  // the game's own time: call() does not charge it to the Replace's budget
    return KILN_OK;
}

uint64_t KILN_CALL wrap_original(uintptr_t entry) {
    if (!entry) return 0;
    uintptr_t cur = 0;
    if (!seh_copy(&cur, reinterpret_cast<const void*>(entry + kEntryUpdateAt), sizeof cur)) return 0;
    const uintptr_t orig = wraps_resolve(entry, cur);
    return orig == cur ? 0 : orig;
}

// ---- platform ----
namespace {
std::string module_of(uintptr_t address) {
    char buf[64];
    std::snprintf(buf, sizeof buf, "0x%llx", static_cast<unsigned long long>(address));
    std::string out = buf;
    const std::string mod = module_name(address);
    if (!mod.empty()) out += " in " + mod;
    return out;
}
}  // namespace

// ---- host tests ----
void wraps_test_budget_ns(uint64_t ns) { g_budget_ns.store(ns); }
uint32_t wraps_test_in_flight(int pool) {
    uint32_t n = 0;
    for (const Slot& s : g_pools[pool].slots) n += s.in_flight.load();
    return n;
}

}  // namespace kw
