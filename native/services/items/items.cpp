// SPDX-License-Identifier: MIT
// kiln.items: give / take items through the game's inventory transaction, read inventories, the item catalog.
//
// The transaction needs a ProgramContext and other-entity accessors, so the service runs it inside its own ECS system
// program "kiln_items" (other-write Inventory, InventorySetup, ...; the same access the game's inventory_actions and
// EMF's emf_inventory declare). The program is registered through kiln.world when that service exists (it owns the
// game's program registration); without it the service registers the program itself (a hook on RegisterAllPrograms).
// Requests queue on any thread, run in the next sim step inside the program, and their callbacks run on the tick
// thread in the following Pre. See native/core/abi/services/kiln_items.h and docs/re/items.md.
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cctype>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <deque>
#include <filesystem>
#include <fstream>
#include <cmath>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include "engine.h"
#include "completed_results.h"
#include "moves.h"
#include "names.h"
#include "pide.h"
#include "kiln_api.h"
#include "safetyhook.hpp"
#include "services/kiln_items.h"
#include "services/kiln_world.h"
#include "xfer.h"

using namespace kitems;

// The managed mirror (managed/services/Kiln.Items/Native.cs) depends on these layouts.
static_assert(sizeof(KilnItemsRequest) == 40 && offsetof(KilnItemsRequest, slot) == 32);
static_assert(sizeof(KilnItemsChange) == 24);
static_assert(sizeof(KilnItemsResult) == 592 && offsetof(KilnItemsResult, changes) == 48 &&
              offsetof(KilnItemsResult, message) == 432);
static_assert(sizeof(KilnItemsSlot) == 32 && offsetof(KilnItemsSlot, state) == 26);
static_assert(sizeof(KilnItemsInventory) == 184 && offsetof(KilnItemsInventory, categories) == 152);
static_assert(sizeof(KilnItemInfo) == 64 && offsetof(KilnItemInfo, name) == 48);
static_assert(sizeof(KilnItemsStatus) == 200 && offsetof(KilnItemsStatus, detail) == 40);
static_assert(sizeof(KilnItemsFuelRequest) == 24 && sizeof(KilnItemsFuelChange) == 32);
static_assert(sizeof(KilnItemsFuelResult) == 720 && offsetof(KilnItemsFuelResult, changes) == 48 &&
              offsetof(KilnItemsFuelResult, message) == 560);

static_assert(sizeof(KilnItemsMoveRequest) == 40 && offsetof(KilnItemsMoveRequest, slot) == 24);
static_assert(sizeof(KilnItemsMoveChange) == 32);
static_assert(sizeof(KilnItemsMoveResult) == 736 && offsetof(KilnItemsMoveResult, changes) == 64 &&
              offsetof(KilnItemsMoveResult, message) == 576);
static_assert(sizeof(KilnItemsStatus2) == 216 && offsetof(KilnItemsStatus2, transfers_seen) == 24 &&
              offsetof(KilnItemsStatus2, detail) == 56);
static_assert(sizeof(KilnItemsTransferEvent) == 32 && sizeof(KilnItemsTransferRule) == 32 &&
              sizeof(KilnItemsTransferReport) == 48);

namespace {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
const KilnWorldApi* g_world_api = nullptr;  // kiln.world, when it is there and registration went through it
KilnHandle g_world_program = 0;

constexpr const char* kName = "kiln.items";
constexpr const char* kProgramName = "kiln_items";
constexpr uint32_t kMaxPending = 1024;
constexpr uint32_t kPerUpdate = 16;       // transactions per sim step
constexpr uint64_t kTimeoutSteps = 250;   // ~10 s at 24.5 Hz
constexpr uint32_t kMaxCount = 100000;

void logf(int level, const char* fmt, ...) {
    char b[768];
    va_list ap;
    va_start(ap, fmt);
    const int n = std::vsnprintf(b, sizeof b, fmt, ap);
    va_end(ap);
    if (g_api && n > 0) g_api->log(g_owner, level, KilnStr{b, static_cast<size_t>(std::min(n, int(sizeof b) - 1))});
}
#define LOGI(...) logf(KILN_LOG_INFO, __VA_ARGS__)
#define LOGW(...) logf(KILN_LOG_WARN, __VA_ARGS__)
#define LOGE(...) logf(KILN_LOG_ERROR, __VA_ARGS__)

// ============================================================================================= state ===========

void selftest_pre(uint64_t step);
void selftest_init();

std::atomic<int> g_program{KILN_ITEMS_PROGRAM_NONE};
std::atomic<uintptr_t> g_world{0};       // the server world (from the program's job: storage - world.storage)
std::atomic<void*> g_registry{nullptr};  // g38_items::ItemRegistry of that world
std::atomic<uint64_t> g_updates{0};
std::atomic<uint64_t> g_step{0};
std::atomic<uint64_t> g_last_update_step{0};
std::atomic<uint64_t> g_commits{0}, g_rollbacks{0};
std::atomic<uint64_t> g_next_ticket{1};
std::mutex g_detail_mu;
char g_detail[160] = "starting";

void set_detail(const char* s) {
    std::lock_guard lock(g_detail_mu);
    std::snprintf(g_detail, sizeof g_detail, "%s", s);
}

void broken(const char* why) {
    if (g_program.exchange(KILN_ITEMS_PROGRAM_BROKEN) != KILN_ITEMS_PROGRAM_BROKEN)
        LOGE("give/take are off until the server restarts: %s", why);
    set_detail(why);
}

struct Request {
    uint64_t ticket;
    KilnOwner owner;
    Job job;
    uint64_t submitted;  // step
};
struct Callback {
    KilnItemsDoneFn fn;
    void* user;
    KilnOwner owner;
    KilnItemsFuelDoneFn fuel_fn = nullptr;  // fuel requests
    KilnItemsMoveDoneFn move_fn = nullptr;  // move requests
};
struct MoveRequest {
    uint64_t ticket;
    KilnOwner owner;
    MoveJob job;
    uint64_t submitted;
};
struct FuelRequest {
    uint64_t ticket;
    KilnOwner owner;
    FuelJob job;
    uint64_t submitted;
};
struct Keep {
    uint64_t handle;
    KilnOwner owner;
    FuelJob job;
    KilnItemsFuelKeepFn on_refill = nullptr;
    void* user = nullptr;
    double rate = 0;   // ADD: units per second of game time
    double acc = 0;    // ADD: units owed, carried between steps
    std::vector<KilnItemsFuelChange> changed;  // since the last Pre (per container: first before, last after)
};

constexpr uint32_t kFuelPerUpdate = 64;  // fuel requests per sim step (cheap: no transaction)
constexpr uint32_t kMovesPerUpdate = 8;  // moves per sim step (each is a transaction)
constexpr uint32_t kMaxKeeps = 256;

std::mutex g_mu;  // guards the containers below; never held while calling the game or a callback
std::deque<Request> g_pending;
kitems::CompletedResults<KilnItemsResult> g_done;
std::unordered_map<uint64_t, Callback> g_callbacks;
std::deque<FuelRequest> g_fuel_pending;
std::vector<KilnItemsFuelResult> g_fuel_done;
std::deque<MoveRequest> g_move_pending;
std::vector<KilnItemsMoveResult> g_move_done;
std::atomic<uint64_t> g_moves_done{0};
std::vector<Keep> g_keeps;
std::atomic<uint64_t> g_fuel_writes{0};   // containers changed by requests and keeps
std::atomic<uint32_t> g_keep_count{0};
std::atomic<uint32_t> g_step_dt_bits{0};    // float seconds of game time the current step covers (Pre)

uint8_t* g_tx = nullptr;  // one InventoryTx (the program is serial: it writes other entities' inventories)

// ============================================================================================= program =========

// The engine's ProgramDesc (0x188 bytes; docs/re/items.md "Program"). Used only without kiln.world.
struct NameRef {
    const char* s;
    uint64_t n;
};
struct NameList {
    const NameRef* p;
    uint64_t n;
};
struct ProgramDesc {
    NameRef name;
    void* update;
    NameList tpl, tpl_opt, read, read_opt, write, write_opt, add, remove;
    NameList sys_read;
    uint64_t sys_read_opt;
    NameList sys_write;
    uint64_t sys_write_opt, sys_write_parallel;
    NameList other_tpl, other_read, other_write, other_write_unser, with, without, any_of, all_write, all_read;
    NameList events_in, events_out;
    uint32_t flags, interval;
};
static_assert(sizeof(ProgramDesc) == 0x188);
static_assert(offsetof(ProgramDesc, sys_read) == 0x98 && offsetof(ProgramDesc, sys_write) == 0xB0);
static_assert(offsetof(ProgramDesc, other_write) == 0xF0 && offsetof(ProgramDesc, events_out) == 0x170);
static_assert(offsetof(ProgramDesc, flags) == 0x180 && offsetof(ProgramDesc, interval) == 0x184);

#define NR(s) NameRef{s, sizeof(s) - 1}
const NameRef kSysRead[] = {NR("BalancingRegistry"), NR("g38_items::ItemRegistry"), NR("impact::AttributeRegistry")};
const NameRef kSysWrite[] = {NR("KnowledgeTriggerQueue")};
// Engine order (ascending "keen::ecs::<name>"); the service still matches accessors by type index.
const NameRef kOtherWrite[] = {NR("Fuel"),      NR("Inventory"),         NR("InventoryEmptyState"), NR("InventorySetup"),
                               NR("ItemState"), NR("OwnerRelationship"), NR("SlotSelection")};
const NameRef kEventsOut[] = {NR("InventoryFull"), NR("PickupEvent")};
ProgramDesc g_desc{};

// Iterator slots of the four singletons (sys-read order, then sys-write), the 7 accessors and the 2 event senders.
int g_sing_slots[4] = {1, 2, 3, 4};
int g_acc_slots[A_COUNT] = {5, 6, 7, 8, 9, 10, 11};
int g_ev_slots[2] = {12, 13};
bool g_slots_ready = false;

Access g_access;       // verified accessors (stable across updates)
bool g_verified = false;

SafetyHookInline g_register_hook;

void KILN_CALL world_update(KilnProgramCtx* ctx, void* user);
void __fastcall own_update(void* ctx);

bool guarded_iter_begin(void* ctx, uint64_t* it, uint32_t size, unsigned long* code) {
    __try {
        E.iter_begin(ctx, it, size);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool resolve_world_slots() {
    if (g_slots_ready) return true;
    const char* sing[4] = {"BalancingRegistry", "g38_items::ItemRegistry", "impact::AttributeRegistry",
                           "KnowledgeTriggerQueue"};
    for (int i = 0; i < 4; ++i)
        g_sing_slots[i] = g_world_api->program_slot(g_world_program, KILN_SLOT_SINGLETON,
                                                    KilnStr{sing[i], std::strlen(sing[i])});
    for (int i = 0; i < A_COUNT; ++i)
        g_acc_slots[i] = g_world_api->program_slot(g_world_program, KILN_SLOT_OTHER_WRITE,
                                                   KilnStr{kOtherWrite[i].s, kOtherWrite[i].n});
    for (int i = 0; i < 2; ++i)
        g_ev_slots[i] = g_world_api->program_slot(g_world_program, KILN_SLOT_EVENT_OUT,
                                                  KilnStr{kEventsOut[i].s, kEventsOut[i].n});
    for (int s : g_sing_slots) if (s < 1 || s >= 64) return false;
    for (int s : g_acc_slots) if (s < 1 || s >= 64) return false;
    for (int s : g_ev_slots) if (s < 1 || s >= 64) return false;
    g_slots_ready = true;
    return true;
}

// Fuel requests (up to kFuelPerUpdate) and every keep-full entry. They run before the give/take batch of the same
// update; the program is appended after the game's programs that write Fuel (impact_simulation, actor_interaction),
// so a keep-full write lands after this step's pours and fills.
void run_fuel_jobs(const Access& a) {
    if (!E.fuel_ok) return;
    FuelRequest batch[kFuelPerUpdate];
    uint32_t n = 0;
    std::vector<Keep> keeps;
    {
        std::lock_guard lock(g_mu);
        while (n < kFuelPerUpdate && !g_fuel_pending.empty()) {
            batch[n++] = g_fuel_pending.front();
            g_fuel_pending.pop_front();
        }
        if (!g_keeps.empty()) keeps = g_keeps;
    }
    if (n == 0 && keeps.empty()) return;
    const uint64_t step = g_step.load();
    std::vector<KilnItemsFuelResult> results(n);
    for (uint32_t i = 0; i < n; ++i) {
        KilnItemsFuelResult& res = results[i];
        std::memset(&res, 0, sizeof res);
        res.size = sizeof res;
        res.ticket = batch[i].ticket;
        res.entity = batch[i].job.r.entity;
        res.item = batch[i].job.r.item;
        res.flags = batch[i].job.r.flags;
        res.steps = static_cast<uint32_t>(step - batch[i].submitted);
        if (g_program.load() == KILN_ITEMS_PROGRAM_BROKEN) {
            res.status = KILN_ITEMS_UNAVAILABLE;
            std::snprintf(res.message, sizeof res.message, "kiln.items' program was switched off (see kiln.log)");
            continue;
        }
        unsigned long code = 0;
        if (!run_fuel(a, batch[i].job, &res, &code)) {
            res.status = KILN_ITEMS_UNAVAILABLE;
            res.change_count = 0;
            std::snprintf(res.message, sizeof res.message, "the Fuel write faulted (0x%08lx)", code);
            char b[160];
            std::snprintf(b, sizeof b, "a Fuel write faulted (0x%08lx) on entity %u", code, res.entity);
            broken(b);
            continue;
        }
        if (!(res.flags & KILN_ITEMS_FUEL_DRY_RUN)) g_fuel_writes.fetch_add(res.changed, std::memory_order_relaxed);
    }
    float dt;
    {
        const uint32_t bits = g_step_dt_bits.load(std::memory_order_relaxed);
        std::memcpy(&dt, &bits, 4);
    }
    struct Done {
        uint64_t handle;
        double acc;
        KilnItemsFuelResult res;
    };
    std::vector<Done> kept;
    for (Keep& k : keeps) {
        if (g_program.load() == KILN_ITEMS_PROGRAM_BROKEN) break;
        FuelJob job = k.job;
        double acc = k.acc;
        if (job.r.flags & KILN_ITEMS_FUEL_ADD) {
            acc += k.rate * dt;
            const double whole = std::floor(acc);
            if (whole < 1.0) {
                kept.push_back(Done{k.handle, acc, {}});
                continue;
            }
            job.r.units = whole > 1.0e9 ? 1000000000 : static_cast<int32_t>(whole);
            acc -= whole;
        }
        KilnItemsFuelResult res{};
        unsigned long code = 0;
        if (!run_fuel(a, job, &res, &code)) {
            char b[160];
            std::snprintf(b, sizeof b, "a keep-full Fuel write faulted (0x%08lx) on entity %u", code, k.job.r.entity);
            broken(b);
            break;
        }
        if (res.changed == 0 && (job.r.flags & KILN_ITEMS_FUEL_ADD)) acc = 0;  // all at the level: owe nothing
        g_fuel_writes.fetch_add(res.changed, std::memory_order_relaxed);
        kept.push_back(Done{k.handle, acc, res});
    }
    std::lock_guard lock(g_mu);
    for (auto& r : results) g_fuel_done.push_back(r);
    for (const Done& d : kept) {
        for (Keep& k : g_keeps) {
            if (k.handle != d.handle) continue;
            k.acc = d.acc;
            if (!k.on_refill) break;
            for (uint32_t i = 0; i < d.res.change_count; ++i) {
                const KilnItemsFuelChange& c = d.res.changes[i];
                bool merged = false;
                for (auto& e : k.changed)
                    if (e.container == c.container) {
                        e.after = c.after;
                        e.max = c.max;
                        merged = true;
                    }
                if (!merged && k.changed.size() < 64) k.changed.push_back(c);
            }
            break;
        }
    }
}

// Moves (up to kMovesPerUpdate): each is a transaction of its own (the game's slot transfer, or its quick stack / move
// all), committed here, so the item entities move with their stacks inside the game's own commit.
void run_move_jobs(const Access& a) {
    if (!E.move_ok) return;
    MoveRequest batch[kMovesPerUpdate];
    uint32_t n = 0;
    {
        std::lock_guard lock(g_mu);
        while (n < kMovesPerUpdate && !g_move_pending.empty()) {
            batch[n++] = g_move_pending.front();
            g_move_pending.pop_front();
        }
    }
    if (n == 0) return;
    const uint64_t step = g_step.load();
    std::vector<KilnItemsMoveResult> results(n);
    for (uint32_t i = 0; i < n; ++i) {
        KilnItemsMoveResult& res = results[i];
        std::memset(&res, 0, sizeof res);
        res.size = sizeof res;
        res.ticket = batch[i].ticket;
        res.from = batch[i].job.r.from;
        res.to = batch[i].job.r.to;
        res.kind = batch[i].job.r.kind;
        res.flags = batch[i].job.r.flags;
        res.steps = static_cast<uint32_t>(step - batch[i].submitted);
        if (g_program.load() == KILN_ITEMS_PROGRAM_BROKEN) {
            res.status = KILN_ITEMS_UNAVAILABLE;
            std::snprintf(res.message, sizeof res.message, "kiln.items' program was switched off (see kiln.log)");
            continue;
        }
        unsigned long code = 0;
        if (!run_move(a, batch[i].job, g_tx, &res, &code)) {
            abandon_tx(g_tx);
            res.status = KILN_ITEMS_UNAVAILABLE;
            res.done = 0;
            res.change_count = 0;
            std::snprintf(res.message, sizeof res.message, "the game's inventory transaction faulted (0x%08lx)", code);
            char b[160];
            std::snprintf(b, sizeof b, "the game's inventory transaction faulted (0x%08lx) on a move of kind %u from %u to %u",
                          code, res.kind, res.from, res.to);
            broken(b);
            continue;
        }
        const bool moved = (res.status == KILN_ITEMS_DONE || res.status == KILN_ITEMS_PARTIAL) &&
                           !(res.flags & KILN_ITEMS_MOVE_DRY_RUN);
        if (moved) {
            g_commits.fetch_add(1);
            g_moves_done.fetch_add(1);
        } else {
            g_rollbacks.fetch_add(1);
        }
    }
    std::lock_guard lock(g_mu);
    for (auto& r : results) g_move_done.push_back(r);
}

// One update of the program: verify once, then run up to kPerUpdate queued transactions.
void program_body(void* ctx, const uint64_t* it) {
    g_updates.fetch_add(1, std::memory_order_relaxed);
    g_last_update_step.store(g_step.load(std::memory_order_relaxed), std::memory_order_relaxed);
    if (g_program.load() == KILN_ITEMS_PROGRAM_BROKEN) return;
    if (g_verified) {  // idle steps cost one lock: nothing to check or run
        std::lock_guard lock(g_mu);
        if (g_pending.empty() && g_fuel_pending.empty() && g_keeps.empty() && g_move_pending.empty()) return;
    }
    unsigned long code = 0;
    Access a{};
    bool ok = false;
    char why[160] = "";
    if (!verify_access(it, g_sing_slots, g_acc_slots, g_ev_slots, ctx, &a, &ok, why, &code)) {
        char b[160];
        std::snprintf(b, sizeof b, "checking the program's iterator faulted (0x%08lx)", code);
        return broken(b);
    }
    if (!ok) return broken(why);
    if (!g_verified) {
        g_verified = true;
        g_access = a;
        g_world.store(reinterpret_cast<uintptr_t>(a.storage) - static_cast<uintptr_t>(E.world_storage));
        g_registry.store(a.items);
        g_program.store(KILN_ITEMS_PROGRAM_RUNNING);
        set_detail("running");
        LOGI("program %s runs: accessors verified against the world's component map (Fuel %u, Inventory %u, "
             "InventoryEmptyState %u, InventorySetup %u, ItemState %u, OwnerRelationship %u, SlotSelection %u), "
             "item registry %p, knowledge queue %s",
             kProgramName, *reinterpret_cast<uint16_t*>(a.acc[A_FUEL]), *reinterpret_cast<uint16_t*>(a.acc[A_INVENTORY]),
             *reinterpret_cast<uint16_t*>(a.acc[A_EMPTY]), *reinterpret_cast<uint16_t*>(a.acc[A_SETUP]),
             *reinterpret_cast<uint16_t*>(a.acc[A_ITEMSTATE]), *reinterpret_cast<uint16_t*>(a.acc[A_OWNER]),
             *reinterpret_cast<uint16_t*>(a.acc[A_SLOTSEL]), a.items, a.knowledge ? "yes" : "missing");
    } else {
        for (int i = 0; i < A_COUNT; ++i)
            if (a.acc[i] != g_access.acc[i] || a.items != g_access.items) return broken("the program's accessors changed");
    }

    run_fuel_jobs(a);
    run_move_jobs(a);

    Request batch[kPerUpdate];
    uint32_t n = 0;
    {
        std::lock_guard lock(g_mu);
        while (n < kPerUpdate && !g_pending.empty()) {
            batch[n++] = g_pending.front();
            g_pending.pop_front();
        }
    }
    if (n == 0) return;
    KilnItemsResult results[kPerUpdate];
    const uint64_t step = g_step.load();
    for (uint32_t i = 0; i < n; ++i) {
        KilnItemsResult& res = results[i];
        std::memset(&res, 0, sizeof res);
        res.size = sizeof res;
        res.ticket = batch[i].ticket;
        res.entity = batch[i].job.r.entity;
        res.item = batch[i].job.r.item;
        res.requested = batch[i].job.r.count;
        res.flags = batch[i].job.r.flags;
        res.steps = static_cast<uint32_t>(step - batch[i].submitted);
        if (g_program.load() == KILN_ITEMS_PROGRAM_BROKEN) {
            res.status = KILN_ITEMS_UNAVAILABLE;
            std::snprintf(res.message, sizeof res.message, "give/take were switched off (see kiln.log)");
            continue;
        }
        code = 0;
        if (batch[i].job.r.flags & KILN_ITEMS_RECIPE)
            a.recipes = reinterpret_cast<void*>(g_api->singleton(g_api->server_world(), KilnStr{"g38_items::RecipeRegistry", 25}));
        if (!run_job(a, batch[i].job, g_tx, &res, &code)) {
            abandon_tx(g_tx);
            res.status = KILN_ITEMS_UNAVAILABLE;
            res.done = 0;
            res.change_count = 0;
            std::snprintf(res.message, sizeof res.message, "the game's inventory transaction faulted (0x%08lx)", code);
            char b[160];
            std::snprintf(b, sizeof b, "the game's inventory transaction faulted (0x%08lx) on %s of item 0x%08x for %u",
                          code, batch[i].job.take ? "take" : "give", res.item, res.entity);
            broken(b);
            continue;
        }
        const bool committed = (res.status == KILN_ITEMS_DONE || res.status == KILN_ITEMS_PARTIAL) &&
                               !(res.flags & KILN_ITEMS_DRY_RUN);
        (committed ? g_commits : g_rollbacks).fetch_add(1);
    }
    std::lock_guard lock(g_mu);
    for (uint32_t i = 0; i < n; ++i) g_done.push_back(results[i]);
}

bool guarded_body(void* ctx, const uint64_t* it, unsigned long* code) {
    __try {
        program_body(ctx, it);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void update_common(void* ctx, uint64_t* it) {
    unsigned long code = 0;
    if (!guarded_body(ctx, it, &code)) {
        char b[160];
        std::snprintf(b, sizeof b, "the program update faulted (0x%08lx)", code);
        broken(b);
    }
}

// kiln.world raw update (once per step: a SYSTEM program).
void KILN_CALL world_update(KilnProgramCtx* ctx, void*) {
    if (g_program.load() == KILN_ITEMS_PROGRAM_BROKEN && g_verified) return;
    if (!resolve_world_slots()) return broken("kiln.world did not report the program's iterator slots");
    alignas(16) uint64_t it[64] = {};
    if (g_world_api->iter_begin(ctx, it, sizeof it) != KILN_OK) return broken("kiln.world iter_begin refused");
    update_common(ctx, it);
}

// Engine update, own registration (iterator: [ctx][4 singletons][7 accessors][2 event senders]).
void __fastcall own_update(void* ctx) {
    if (g_program.load() == KILN_ITEMS_PROGRAM_BROKEN && g_verified) return;
    alignas(16) uint64_t it[64] = {};
    unsigned long code = 0;
    if (!guarded_iter_begin(ctx, it, sizeof it, &code)) {
        char b[160];
        std::snprintf(b, sizeof b, "IterBegin faulted (0x%08lx)", code);
        return broken(b);
    }
    update_common(ctx, it);
}

int64_t g_storage_kind = -1;  // storage.kind (core constant); -1 = unknown

// RegisterAllPrograms runs for every world a game client creates (client and prediction worlds too): kiln_items is a
// server-world program.
bool server_world_kind(uintptr_t world) {
    if (g_storage_kind < 0 || !world) return true;
    return *reinterpret_cast<const uint8_t*>(world + static_cast<uintptr_t>(E.world_storage + g_storage_kind)) == 0;
}

void __fastcall hk_register_all(uintptr_t world) {
    g_register_hook.call<void>(world);
    if (!server_world_kind(world)) {
        LOGI("RegisterAllPrograms for a world of kind %u: not the server world; no kiln_items program there",
             *reinterpret_cast<const uint8_t*>(world + static_cast<uintptr_t>(E.world_storage + g_storage_kind)));
        return;
    }
    int outcome = 0;
    char why[160] = "";
    unsigned long code = 0;
    if (!register_program(world, &g_desc, &outcome, why, &code)) {
        LOGE("registering program %s faulted (0x%08lx)", kProgramName, code);
        g_program.store(KILN_ITEMS_PROGRAM_NONE);
        set_detail("registering the program faulted");
        return;
    }
    if (!outcome) {
        LOGE("program %s not registered: %s", kProgramName, why);
        g_program.store(KILN_ITEMS_PROGRAM_NONE);
        set_detail(why);
        return;
    }
    g_program.store(KILN_ITEMS_PROGRAM_ACTIVE);
    set_detail("program registered, not run yet");
    LOGI("program %s registered into the server world (own registration)", kProgramName);
}

bool register_own() {
    if (!E.program_ok) return false;
    g_desc.name = NameRef{kProgramName, std::strlen(kProgramName)};
    g_desc.update = reinterpret_cast<void*>(&own_update);
    g_desc.sys_read = NameList{kSysRead, 3};
    g_desc.sys_write = NameList{kSysWrite, 1};
    g_desc.sys_write_opt = 1;  // KnowledgeTriggerQueue may be missing (slot NULL), as for inventory_actions
    g_desc.other_write = NameList{kOtherWrite, A_COUNT};
    g_desc.events_out = NameList{kEventsOut, 2};
    g_desc.flags = 0x4 | 0x10;  // system program (no entity query), not in a client's prediction world
    g_desc.interval = 1;
    g_register_hook = safetyhook::create_inline(reinterpret_cast<void*>(E.register_all),
                                                reinterpret_cast<void*>(&hk_register_all));
    if (!g_register_hook) {
        LOGE("hooking RegisterAllPrograms failed");
        return false;
    }
    g_program.store(KILN_ITEMS_PROGRAM_PENDING);
    set_detail("waiting for the server world");
    LOGI("program %s: own registration (kiln.world not used), hooked RegisterAllPrograms", kProgramName);
    return true;
}

bool register_with_world(const KilnWorldApi* w) {
    std::vector<KilnStr> sr, sw, ow, eo;
    for (auto& n : kSysRead) sr.push_back(KilnStr{n.s, n.n});
    for (auto& n : kSysWrite) sw.push_back(KilnStr{n.s, n.n});
    for (auto& n : kOtherWrite) ow.push_back(KilnStr{n.s, n.n});
    for (auto& n : kEventsOut) eo.push_back(KilnStr{n.s, n.n});
    KilnProgramDesc d{};
    d.size = sizeof d;
    d.flags = KILN_PROGRAM_SYSTEM | KILN_PROGRAM_HOST_ONLY;
    d.name = KilnStr{kProgramName, std::strlen(kProgramName)};
    d.update = &world_update;
    d.singletons_read = KilnNames{sr.data(), static_cast<uint32_t>(sr.size()), 0};
    d.singletons_write = KilnNames{sw.data(), static_cast<uint32_t>(sw.size()), 0};
    d.singletons_write_optional = 1;
    d.other_write = KilnNames{ow.data(), static_cast<uint32_t>(ow.size()), 0};
    d.events_out = KilnNames{eo.data(), static_cast<uint32_t>(eo.size()), 0};
    KilnHandle h = 0;
    const KilnResult rc = w->register_program(g_owner, &d, &h);  // kiln.world copies the descriptor
    if (rc != KILN_OK) {
        LOGW("kiln.world refused program %s (%d)", kProgramName, rc);
        return false;
    }
    g_world_api = w;
    g_world_program = h;
    g_program.store(KILN_ITEMS_PROGRAM_PENDING);
    set_detail("waiting for the server world (kiln.world)");
    LOGI("program %s registered through kiln.world", kProgramName);
    return true;
}

// ============================================================================================= catalog =========

struct Item {
    KilnItemInfo info;
    std::string name;
};
std::vector<Item> g_items;                          // sorted by name; immutable once published
std::unordered_map<uint32_t, uint32_t> g_by_id;     // id -> index
std::unordered_map<std::string, uint32_t> g_by_name, g_by_lower;
std::atomic<bool> g_catalog{false};
uint32_t g_reg_seen = 0, g_reg_stable = 0;

std::string lower(std::string_view s) {
    std::string r(s);
    for (char& c : r) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return r;
}

void build_catalog() {
    void* reg = g_registry.load();
    if (!reg || g_catalog.load()) return;
    unsigned long code = 0;
    uint32_t count = 0;
    if (!registry_count(reg, &count, &code)) {
        LOGE("reading the item registry faulted (0x%08lx); no catalog", code);
        g_registry.store(nullptr);
        return;
    }
    if (count == 0 || count != g_reg_seen) {
        g_reg_seen = count;
        g_reg_stable = 0;
        return;
    }
    if (++g_reg_stable < 6) return;  // unchanged for 6 checks (~2 s): the registry has loaded
    std::vector<RawItem> raw(count + 64);
    uint32_t n = 0;
    if (!copy_registry(reg, raw.data(), static_cast<uint32_t>(raw.size()), &n, &code)) {
        LOGE("copying the item registry faulted (0x%08lx); no catalog", code);
        g_registry.store(nullptr);
        return;
    }
    uint32_t named = 0, guid_ok = 0;
    std::vector<Item> items;
    items.reserve(n);
    for (uint32_t i = 0; i < n; ++i) {
        const RawItem& r = raw[i];
        Item it{};
        it.info.size = sizeof(KilnItemInfo);
        it.info.id = r.id;
        std::memcpy(it.info.guid, r.guid, 16);
        it.info.max_stack = r.max_stack;
        it.info.category = r.category;
        it.info.rarity = r.rarity;
        it.info.item_flags = r.item_flags;
        it.info.level_min = r.level_min;
        it.info.level_max = r.level_max;
        it.info.equip_slot = r.equip_slot;
        it.info.gear = r.gear;
        it.name.assign(r.name, r.name_len);
        named += r.name_len > 0;
        guid_ok += r.guid_id == r.id;
        items.push_back(std::move(it));
    }
    std::sort(items.begin(), items.end(), [](const Item& a, const Item& b) {
        return a.name != b.name ? a.name < b.name : a.info.id < b.info.id;
    });
    g_items = std::move(items);
    for (uint32_t i = 0; i < g_items.size(); ++i) {
        Item& it = g_items[i];
        it.info.name = KilnStr{it.name.data(), it.name.size()};
        g_by_id.emplace(it.info.id, i);
        if (!it.name.empty()) {
            g_by_name.emplace(it.name, i);
            g_by_lower.emplace(lower(it.name), i);
        }
    }
    g_catalog.store(true, std::memory_order_release);
    LOGI("catalog: %zu items from the item registry (%u with a debug name; GUID hash = item id for %u)", g_items.size(),
         named, guid_ok);
    if (guid_ok + 8 < g_items.size()) LOGW("catalog: %zu items whose id is not FNV-1a of their GUID", g_items.size() - guid_ok);
}

// ============================================================================================= tick ============

bool guarded_callback(KilnItemsDoneFn fn, const KilnItemsResult* r, void* user, unsigned long* code) {
    __try {
        fn(r, user);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool guarded_fuel_callback(KilnItemsFuelDoneFn fn, const KilnItemsFuelResult* r, void* user, unsigned long* code) {
    __try {
        fn(r, user);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool guarded_keep_callback(KilnItemsFuelKeepFn fn, uint64_t h, const KilnItemsFuelChange* c, uint32_t n, void* user,
                           unsigned long* code) {
    __try {
        fn(h, c, n, user);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void deliver_refills() {
    struct Out {
        uint64_t handle;
        KilnItemsFuelKeepFn fn;
        void* user;
        std::vector<KilnItemsFuelChange> changes;
    };
    std::vector<Out> outs;
    {
        std::lock_guard lock(g_mu);
        for (Keep& k : g_keeps)
            if (k.on_refill && !k.changed.empty()) {
                outs.push_back(Out{k.handle, k.on_refill, k.user, std::move(k.changed)});
                k.changed.clear();
            }
    }
    for (const Out& o : outs) {
        unsigned long code = 0;
        if (!guarded_keep_callback(o.fn, o.handle, o.changes.data(), static_cast<uint32_t>(o.changes.size()), o.user, &code))
            LOGE("a refill callback (keep %llu) faulted (0x%08lx); ignored", static_cast<unsigned long long>(o.handle), code);
    }
}

void deliver_fuel() {
    deliver_refills();
    std::vector<KilnItemsFuelResult> done;
    std::vector<Callback> cbs;
    {
        std::lock_guard lock(g_mu);
        if (g_fuel_done.empty()) return;
        done.swap(g_fuel_done);
        for (const KilnItemsFuelResult& r : done) {
            auto it = g_callbacks.find(r.ticket);
            if (it == g_callbacks.end()) cbs.push_back(Callback{});
            else {
                cbs.push_back(it->second);
                g_callbacks.erase(it);
            }
        }
    }
    for (size_t i = 0; i < done.size(); ++i) {
        const KilnItemsFuelResult& r = done[i];
        LOGI("fuel ticket %llu%s: entity %u item 0x%08x flags 0x%x -> status %d, %u matched, %u changed, %u step(s)%s%s",
             static_cast<unsigned long long>(r.ticket), (r.flags & KILN_ITEMS_FUEL_DRY_RUN) ? " (dry run)" : "",
             r.entity, r.item, r.flags, r.status, r.matched, r.changed, r.steps, r.message[0] ? ": " : "", r.message);
        unsigned long code = 0;
        if (cbs[i].fuel_fn && !guarded_fuel_callback(cbs[i].fuel_fn, &r, cbs[i].user, &code))
            LOGE("a Fuel result callback (owner %llu) faulted (0x%08lx); ignored",
                 static_cast<unsigned long long>(cbs[i].owner), code);
    }
}

bool guarded_move_callback(KilnItemsMoveDoneFn fn, const KilnItemsMoveResult* r, void* user, unsigned long* code) {
    __try {
        fn(r, user);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void deliver_moves() {
    std::vector<KilnItemsMoveResult> done;
    std::vector<Callback> cbs;
    {
        std::lock_guard lock(g_mu);
        if (g_move_done.empty()) return;
        done.swap(g_move_done);
        for (const KilnItemsMoveResult& r : done) {
            auto it = g_callbacks.find(r.ticket);
            if (it == g_callbacks.end()) cbs.push_back(Callback{});
            else {
                cbs.push_back(it->second);
                g_callbacks.erase(it);
            }
        }
    }
    for (size_t i = 0; i < done.size(); ++i) {
        const KilnItemsMoveResult& r = done[i];
        LOGI("move ticket %llu%s: kind %u, entity %u -> %u, status %d, done %u of %u, %u stack(s), %u change(s), %u step(s)%s%s",
             static_cast<unsigned long long>(r.ticket), (r.flags & KILN_ITEMS_MOVE_DRY_RUN) ? " (dry run)" : "", r.kind, r.from,
             r.to, r.status, r.done, r.requested, r.stacks, r.change_count, r.steps, r.message[0] ? ": " : "", r.message);
        unsigned long code = 0;
        if (cbs[i].move_fn && !guarded_move_callback(cbs[i].move_fn, &r, cbs[i].user, &code))
            LOGE("a move result callback (owner %llu) faulted (0x%08lx); ignored",
                 static_cast<unsigned long long>(cbs[i].owner), code);
    }
}

void deliver() {
    deliver_fuel();
    deliver_moves();
    const std::vector<KilnItemsResult>* batch = nullptr;
    std::vector<Callback> cbs;
    {
        std::lock_guard lock(g_mu);
        if (g_done.empty() || g_done.delivering()) return;
        batch = &g_done.begin_delivery();
        const auto& done = *batch;
        cbs.reserve(done.size());
        for (const KilnItemsResult& r : done) {
            auto it = g_callbacks.find(r.ticket);
            if (it == g_callbacks.end()) cbs.push_back(Callback{nullptr, nullptr, 0});
            else {
                cbs.push_back(it->second);
                g_callbacks.erase(it);
            }
        }
    }
    const auto& done = *batch;
    for (size_t i = 0; i < done.size(); ++i) {
        const KilnItemsResult& r = done[i];
        LOGI("ticket %llu%s: item 0x%08x x%u, entity %u -> status %d, done %u, %u slot(s), %u step(s)%s%s",
             static_cast<unsigned long long>(r.ticket), (r.flags & KILN_ITEMS_DRY_RUN) ? " (dry run)" : "", r.item,
             r.requested, r.entity, r.status, r.done, r.change_count, r.steps, r.message[0] ? ": " : "", r.message);
        unsigned long code = 0;
        if (cbs[i].fn && !guarded_callback(cbs[i].fn, &r, cbs[i].user, &code))
            LOGE("a result callback (owner %llu) faulted (0x%08lx); ignored", static_cast<unsigned long long>(cbs[i].owner),
                 code);
    }
    std::lock_guard lock(g_mu);
    g_done.end_delivery();
}

KilnItemsFuelResult fuel_failure(const FuelRequest& q, int32_t status, const char* why, uint64_t step) {
    KilnItemsFuelResult r{};
    r.size = sizeof r;
    r.ticket = q.ticket;
    r.entity = q.job.r.entity;
    r.item = q.job.r.item;
    r.flags = q.job.r.flags;
    r.steps = static_cast<uint32_t>(step - q.submitted);
    r.status = status;
    std::snprintf(r.message, sizeof r.message, "%s", why);
    return r;
}

KilnItemsMoveResult move_failure(const MoveRequest& q, int32_t status, const char* why, uint64_t step) {
    KilnItemsMoveResult r{};
    r.size = sizeof r;
    r.ticket = q.ticket;
    r.from = q.job.r.from;
    r.to = q.job.r.to;
    r.kind = q.job.r.kind;
    r.flags = q.job.r.flags;
    r.steps = static_cast<uint32_t>(step - q.submitted);
    r.status = status;
    std::snprintf(r.message, sizeof r.message, "%s", why);
    return r;
}

void expire() {
    const int state = g_program.load();
    const uint64_t step = g_step.load();
    std::lock_guard lock(g_mu);
    while (!g_pending.empty()) {
        const Request& q = g_pending.front();
        const bool broken_now = state == KILN_ITEMS_PROGRAM_BROKEN || state == KILN_ITEMS_PROGRAM_NONE;
        const bool stale = step > q.submitted + kTimeoutSteps;
        if (!broken_now && !stale) break;
        KilnItemsResult r{};
        r.size = sizeof r;
        r.ticket = q.ticket;
        r.entity = q.job.r.entity;
        r.item = q.job.r.item;
        r.requested = q.job.r.count;
        r.flags = q.job.r.flags;
        r.steps = static_cast<uint32_t>(step - q.submitted);
        r.status = broken_now ? KILN_ITEMS_UNAVAILABLE : KILN_ITEMS_DROPPED;
        std::snprintf(r.message, sizeof r.message, "%s",
                      broken_now ? "give/take are off (see kiln.log)" : "the kiln_items program did not run for 10 s");
        g_done.push_back(r);
        g_pending.pop_front();
    }
    while (!g_move_pending.empty()) {
        const MoveRequest& q = g_move_pending.front();
        const bool broken_now = state == KILN_ITEMS_PROGRAM_BROKEN || state == KILN_ITEMS_PROGRAM_NONE;
        const bool stale = step > q.submitted + kTimeoutSteps;
        if (!broken_now && !stale) break;
        g_move_done.push_back(move_failure(q, broken_now ? KILN_ITEMS_UNAVAILABLE : KILN_ITEMS_DROPPED,
                                           broken_now ? "kiln.items' program is off (see kiln.log)"
                                                      : "the kiln_items program did not run for 10 s",
                                           step));
        g_move_pending.pop_front();
    }
    while (!g_fuel_pending.empty()) {
        const FuelRequest& q = g_fuel_pending.front();
        const bool broken_now = state == KILN_ITEMS_PROGRAM_BROKEN || state == KILN_ITEMS_PROGRAM_NONE;
        const bool stale = step > q.submitted + kTimeoutSteps;
        if (!broken_now && !stale) break;
        g_fuel_done.push_back(fuel_failure(q, broken_now ? KILN_ITEMS_UNAVAILABLE : KILN_ITEMS_DROPPED,
                                           broken_now ? "kiln.items' program is off (see kiln.log)"
                                                      : "the kiln_items program did not run for 10 s",
                                           step));
        g_fuel_pending.pop_front();
    }
}

void poll_world_program() {
    if (!g_world_api || g_program.load() != KILN_ITEMS_PROGRAM_PENDING) return;
    KilnProgramInfo pi{};
    pi.size = sizeof pi;
    if (g_world_api->program_info(g_world_program, &pi) != KILN_OK) return;
    if (pi.state == KILN_PROGRAM_ACTIVE) {
        g_program.store(KILN_ITEMS_PROGRAM_ACTIVE);
        set_detail("program registered (kiln.world), not run yet");
    } else if (pi.state == KILN_PROGRAM_REFUSED || pi.state == KILN_PROGRAM_FAULTED || pi.state == KILN_PROGRAM_SKIPPED) {
        g_program.store(KILN_ITEMS_PROGRAM_NONE);
        char b[160];
        std::snprintf(b, sizeof b, "kiln.world reports the program as state %u (see kiln.log)", pi.state);
        set_detail(b);
        LOGE("%s", b);
    }
}

// ---- world sessions ----
// The game closes the server world (a client leaving its hosted game). Requests queued for it end as dropped; the
// next server world's program run verifies its accessors and registry again (kiln.world, or the RegisterAllPrograms
// hook, registers kiln_items into each new server world).
void drop_world(const char* why) {
    {
        std::lock_guard lock(g_mu);
        const uint64_t step = g_step.load();
        while (!g_pending.empty()) {
            const Request& q = g_pending.front();
            KilnItemsResult r{};
            r.size = sizeof r;
            r.ticket = q.ticket;
            r.entity = q.job.r.entity;
            r.item = q.job.r.item;
            r.requested = q.job.r.count;
            r.flags = q.job.r.flags;
            r.steps = static_cast<uint32_t>(step - q.submitted);
            r.status = KILN_ITEMS_DROPPED;
            std::snprintf(r.message, sizeof r.message, "the server world closed");
            g_done.push_back(r);
            g_pending.pop_front();
        }
        while (!g_fuel_pending.empty()) {
            g_fuel_done.push_back(fuel_failure(g_fuel_pending.front(), KILN_ITEMS_DROPPED, "the server world closed", step));
            g_fuel_pending.pop_front();
        }
        while (!g_move_pending.empty()) {
            g_move_done.push_back(move_failure(g_move_pending.front(), KILN_ITEMS_DROPPED, "the server world closed", step));
            g_move_pending.pop_front();
        }
        if (!g_keeps.empty()) LOGI("server world closed: %zu keep-full entr%s ended", g_keeps.size(), g_keeps.size() == 1 ? "y" : "ies");
        g_keeps.clear();
        g_keep_count.store(0);
    }
    g_verified = false;
    g_world.store(0);
    g_registry.store(nullptr);
    names_world_closed();
    int expected = KILN_ITEMS_PROGRAM_RUNNING;
    if (g_program.compare_exchange_strong(expected, KILN_ITEMS_PROGRAM_ACTIVE))
        set_detail("program registered, not run yet in this world");
    LOGI("server world closed (%s): kiln_items waits for the next server world", why);
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

void KILN_CALL on_pre(const KilnTickInfo* tick, void*) {
    g_step.store(tick->step);
    {
        float dt = tick->clock_delta > 0 && tick->clock_delta < 60 ? tick->clock_delta / 60.0f : 0.0f;  // the 60 Hz clock
        uint32_t bits;
        std::memcpy(&bits, &dt, 4);
        g_step_dt_bits.store(bits, std::memory_order_relaxed);
    }
    xfer_set_step(tick->step);
    poll_world_program();
    expire();
    deliver();
    xfer_deliver();
    selftest_pre(tick->step);
    if (!g_catalog.load(std::memory_order_relaxed) && (tick->step & 7) == 0) build_catalog();
}

// ============================================================================================= table ===========

KilnResult submit(bool take, KilnOwner owner, const KilnItemsRequest* r, KilnItemsDoneFn done, void* user,
                  uint64_t* out_ticket) {
    if (!r || r->size < sizeof(KilnItemsRequest) || r->entity == 0 || r->item == 0 || r->count == 0 ||
        r->count > kMaxCount)
        return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t allowed = take ? (KILN_ITEMS_EXACT_SLOT | KILN_ITEMS_DRY_RUN)
                                  : (KILN_ITEMS_LEVEL | KILN_ITEMS_RARITY | KILN_ITEMS_ALL_OR_NOTHING |
                                     KILN_ITEMS_NOTIFY | KILN_ITEMS_NO_MERGE | KILN_ITEMS_DRY_RUN | KILN_ITEMS_RECIPE |
                                     KILN_ITEMS_UNLOCK | KILN_ITEMS_GEM_SLOT);
    if (r->flags & ~allowed) return KILN_ERR_INVALID_ARGUMENT;
    // gear options at creation (pide.cpp): not with a recipe; `unlock` only with UNLOCK (1..5, 255 = all)
    if (!pide_request_valid(r->flags, r->unlock)) return KILN_ERR_INVALID_ARGUMENT;
    if ((r->flags & (KILN_ITEMS_UNLOCK | KILN_ITEMS_GEM_SLOT)) && !pide_available()) return KILN_ERR_UNAVAILABLE;
    if ((r->flags & KILN_ITEMS_RECIPE) && ((r->flags & (KILN_ITEMS_LEVEL | KILN_ITEMS_RARITY)) || r->slot))
        return KILN_ERR_INVALID_ARGUMENT;
    if ((r->flags & KILN_ITEMS_RARITY) && r->rarity > 4) return KILN_ERR_INVALID_ARGUMENT;
    if ((r->flags & KILN_ITEMS_LEVEL) && (r->level == 0 || r->level > 255)) return KILN_ERR_INVALID_ARGUMENT;
    if (r->slot && ((r->slot >> 40) != 0 || KILN_SLOT_INDEX(r->slot) > 7 || KILN_SLOT_INVENTORY(r->slot) == 0))
        return KILN_ERR_INVALID_ARGUMENT;
    if ((r->flags & KILN_ITEMS_EXACT_SLOT) && !r->slot) return KILN_ERR_INVALID_ARGUMENT;
    if (!E.tx_ok || !g_tx) return KILN_ERR_UNAVAILABLE;
    const int state = g_program.load();
    if (state == KILN_ITEMS_PROGRAM_NONE || state == KILN_ITEMS_PROGRAM_BROKEN) return KILN_ERR_UNAVAILABLE;
    Request q{};
    q.ticket = g_next_ticket.fetch_add(1);
    q.owner = owner;
    q.job.take = take;
    q.job.r = *r;
    q.job.r.size = sizeof(KilnItemsRequest);
    q.submitted = g_step.load();
    {
        std::lock_guard lock(g_mu);
        if (g_pending.size() >= kMaxPending) return KILN_ERR_UNAVAILABLE;
        g_pending.push_back(q);
        if (done) g_callbacks.emplace(q.ticket, Callback{done, user, owner});
    }
    if (out_ticket) *out_ticket = q.ticket;
    return KILN_OK;
}

KilnResult KILN_CALL t_give(KilnOwner owner, const KilnItemsRequest* r, KilnItemsDoneFn done, void* user,
                            uint64_t* out) {
    return submit(false, owner, r, done, user, out);
}
KilnResult KILN_CALL t_take(KilnOwner owner, const KilnItemsRequest* r, KilnItemsDoneFn done, void* user,
                            uint64_t* out) {
    return submit(true, owner, r, done, user, out);
}

bool fuel_flags_ok(uint32_t f, bool keep) {
    const uint32_t mode = f & (KILN_ITEMS_FUEL_FILL | KILN_ITEMS_FUEL_ADD);
    if (f & ~(KILN_ITEMS_FUEL_FILL | KILN_ITEMS_FUEL_ADD | KILN_ITEMS_FUEL_INVENTORIES | KILN_ITEMS_FUEL_DRY_RUN))
        return false;
    if (mode == (KILN_ITEMS_FUEL_FILL | KILN_ITEMS_FUEL_ADD)) return false;
    if (keep && (mode == 0 || (f & KILN_ITEMS_FUEL_DRY_RUN))) return false;  // a keep fills or adds
    return true;
}

bool fuel_usable() {
    const int state = g_program.load();
    return E.fuel_ok && state != KILN_ITEMS_PROGRAM_NONE && state != KILN_ITEMS_PROGRAM_BROKEN;
}

KilnResult KILN_CALL t_fuel(KilnOwner owner, const KilnItemsFuelRequest* r, KilnItemsFuelDoneFn done, void* user,
                            uint64_t* out_ticket) {
    if (!r || r->size < sizeof(KilnItemsFuelRequest) || r->entity == 0 || r->limit < 0 ||
        !fuel_flags_ok(r->flags, false))
        return KILN_ERR_INVALID_ARGUMENT;
    if (!fuel_usable()) return KILN_ERR_UNAVAILABLE;
    FuelRequest q{};
    q.ticket = g_next_ticket.fetch_add(1);
    q.owner = owner;
    q.job.r = *r;
    q.job.r.size = sizeof(KilnItemsFuelRequest);
    q.submitted = g_step.load();
    {
        std::lock_guard lock(g_mu);
        if (g_fuel_pending.size() >= kMaxPending) return KILN_ERR_UNAVAILABLE;
        g_fuel_pending.push_back(q);
        if (done) {
            Callback cb{};
            cb.user = user;
            cb.owner = owner;
            cb.fuel_fn = done;
            g_callbacks.emplace(q.ticket, cb);
        }
    }
    if (out_ticket) *out_ticket = q.ticket;
    return KILN_OK;
}

KilnResult KILN_CALL t_fuel_read(KilnEntity entity, int32_t* current, int32_t* max) {
    if (entity == 0) return KILN_ERR_INVALID_ARGUMENT;
    if (!E.fuel_ok || !E.read_ok) return KILN_ERR_UNAVAILABLE;
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    const uintptr_t world = g_world.load();
    if (!world) return KILN_ERR_UNAVAILABLE;
    void* attributes = reinterpret_cast<void*>(g_api->singleton(world, KilnStr{"impact::AttributeRegistry", 25}));
    if (!attributes) return KILN_ERR_UNAVAILABLE;
    int32_t cur = 0, mx = 0;
    bool found = false;
    unsigned long code = 0;
    if (!read_fuel(world, attributes, entity, &cur, &mx, &found, &code)) {
        LOGE("reading entity %u's Fuel faulted (0x%08lx)", entity, code);
        return KILN_ERR_INTERNAL;
    }
    if (!found) return KILN_ERR_NOT_FOUND;
    if (current) *current = cur;
    if (max) *max = mx;
    return KILN_OK;
}

KilnResult KILN_CALL t_fuel_keep(KilnOwner owner, const KilnItemsFuelRequest* r, KilnItemsFuelKeepFn on_refill,
                                 void* user, uint64_t* out_handle) {
    if (!r || !out_handle || r->size < sizeof(KilnItemsFuelRequest) || r->entity == 0 || r->limit < 0 ||
        !fuel_flags_ok(r->flags, true) || ((r->flags & KILN_ITEMS_FUEL_ADD) && r->units <= 0))
        return KILN_ERR_INVALID_ARGUMENT;
    if (!fuel_usable()) return KILN_ERR_UNAVAILABLE;
    Keep k{};
    k.handle = g_next_ticket.fetch_add(1);
    k.owner = owner;
    k.job.r = *r;
    k.job.r.size = sizeof(KilnItemsFuelRequest);
    k.on_refill = on_refill;
    k.user = user;
    k.rate = (r->flags & KILN_ITEMS_FUEL_ADD) ? r->units : 0.0;
    {
        std::lock_guard lock(g_mu);
        if (g_keeps.size() >= kMaxKeeps) return KILN_ERR_UNAVAILABLE;
        g_keeps.push_back(k);
        g_keep_count.store(static_cast<uint32_t>(g_keeps.size()));
    }
    *out_handle = k.handle;
    LOGI("refill %llu: entity %u%s item 0x%08x, %s%s (owner %llu)", static_cast<unsigned long long>(k.handle), r->entity,
         (r->flags & KILN_ITEMS_FUEL_INVENTORIES) ? " (its inventories)" : "", r->item,
         (r->flags & KILN_ITEMS_FUEL_ADD) ? "units per second " : "keep full",
         (r->flags & KILN_ITEMS_FUEL_ADD) ? std::to_string(r->units).c_str() : "",
         static_cast<unsigned long long>(owner));
    return KILN_OK;
}

KilnResult KILN_CALL t_fuel_keep_end(uint64_t handle) {
    std::lock_guard lock(g_mu);
    for (auto it = g_keeps.begin(); it != g_keeps.end(); ++it) {
        if (it->handle != handle) continue;
        g_keeps.erase(it);
        g_keep_count.store(static_cast<uint32_t>(g_keeps.size()));
        LOGI("refill %llu ended", static_cast<unsigned long long>(handle));
        return KILN_OK;
    }
    return KILN_ERR_NOT_FOUND;
}

KilnResult KILN_CALL t_move(KilnOwner owner, const KilnItemsMoveRequest* r, KilnItemsMoveDoneFn done, void* user,
                            uint64_t* out_ticket) {
    constexpr uint32_t kAllowed = KILN_ITEMS_MOVE_DRY_RUN | KILN_ITEMS_MOVE_EQUIPPED | KILN_ITEMS_MOVE_ACTIONBAR |
                                  KILN_ITEMS_MOVE_SILENT;
    if (!r || r->size < sizeof(KilnItemsMoveRequest) || r->from == 0 || r->to == 0 || r->from == r->to ||
        r->kind > KILN_MOVE_ALL || (r->flags & ~kAllowed))
        return KILN_ERR_INVALID_ARGUMENT;
    if (r->kind == KILN_MOVE_STACK) {
        if (r->slot == 0 || (r->slot >> 40) != 0 || KILN_SLOT_INDEX(r->slot) > 7 || KILN_SLOT_INVENTORY(r->slot) == 0 ||
            r->count > kMaxCount)
            return KILN_ERR_INVALID_ARGUMENT;
        if (r->to_slot && ((r->to_slot >> 40) != 0 || KILN_SLOT_INDEX(r->to_slot) > 7 || KILN_SLOT_INVENTORY(r->to_slot) == 0))
            return KILN_ERR_INVALID_ARGUMENT;
    } else if (r->slot != 0 || r->to_slot != 0 || r->count != 0) {
        return KILN_ERR_INVALID_ARGUMENT;
    }
    if (r->kind != KILN_MOVE_STACK && (r->flags & KILN_ITEMS_MOVE_EQUIPPED)) return KILN_ERR_INVALID_ARGUMENT;
    if (!E.move_ok || !E.tx_ok || !g_tx) return KILN_ERR_UNAVAILABLE;
    const int state = g_program.load();
    if (state == KILN_ITEMS_PROGRAM_NONE || state == KILN_ITEMS_PROGRAM_BROKEN) return KILN_ERR_UNAVAILABLE;
    MoveRequest q{};
    q.ticket = g_next_ticket.fetch_add(1);
    q.owner = owner;
    q.job.r = *r;
    q.job.r.size = sizeof(KilnItemsMoveRequest);
    q.submitted = g_step.load();
    {
        std::lock_guard lock(g_mu);
        if (g_move_pending.size() >= kMaxPending) return KILN_ERR_UNAVAILABLE;
        g_move_pending.push_back(q);
        if (done) {
            Callback cb{};
            cb.user = user;
            cb.owner = owner;
            cb.move_fn = done;
            g_callbacks.emplace(q.ticket, cb);
        }
    }
    if (out_ticket) *out_ticket = q.ticket;
    return KILN_OK;
}

KilnResult KILN_CALL t_container_name(KilnEntity entity, char* buf, uint32_t capacity, uint32_t* out_len) {
    if (out_len) *out_len = 0;
    if (entity == 0 || (capacity && !buf)) return KILN_ERR_INVALID_ARGUMENT;
    if (!E.names_ok || !E.read_ok) return KILN_ERR_UNAVAILABLE;
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    const uintptr_t world = g_api->server_world();
    if (!world) return KILN_ERR_UNAVAILABLE;
    return name_of(world, entity, buf, capacity, out_len);
}

KilnResult KILN_CALL t_transfer_rule(KilnOwner owner, const KilnItemsTransferRule* r, uint64_t* out) {
    return xfer_add_rule(owner, r, out);
}
KilnResult KILN_CALL t_transfer_listen(KilnOwner owner, KilnItemsTransferFn fn, void* user, uint64_t* out) {
    return xfer_add_handler(owner, fn, user, out);
}
KilnResult KILN_CALL t_transfer_report(KilnOwner owner, KilnItemsTransferReportFn fn, void* user, uint64_t* out) {
    return xfer_add_reporter(owner, fn, user, out);
}
KilnResult KILN_CALL t_transfer_end(uint64_t handle) { return xfer_end(handle); }

KilnResult KILN_CALL t_status2(KilnItemsStatus2* out) {
    if (!out || out->size < sizeof(KilnItemsStatus2)) return KILN_ERR_INVALID_ARGUMENT;
    std::memset(out, 0, sizeof *out);
    out->size = sizeof *out;
    out->moves = E.move_ok && g_program.load() == KILN_ITEMS_PROGRAM_RUNNING;
    out->names = E.names_ok && E.read_ok;
    xfer_status(out);
    out->moves_done = g_moves_done.load();
    out->names_found = names_found();
    if (!out->detail[0]) {
        const char* why = !E.move_ok ? "items.move is off on this build" : !E.names_ok ? "items.names is off on this build"
                          : !E.transfer_ok ? "items.transfer is off on this build" : "";
        std::snprintf(out->detail, sizeof out->detail, "%s", why);
    }
    return KILN_OK;
}

KilnResult KILN_CALL t_cancel(uint64_t ticket) {
    std::lock_guard lock(g_mu);
    return g_callbacks.erase(ticket) ? KILN_OK : KILN_ERR_NOT_FOUND;
}

void KILN_CALL t_cancel_owner(KilnOwner owner) {
    {
        std::lock_guard lock(g_mu);
        for (auto it = g_callbacks.begin(); it != g_callbacks.end();)
            it = it->second.owner == owner ? g_callbacks.erase(it) : std::next(it);
        const size_t before = g_keeps.size();
        g_keeps.erase(std::remove_if(g_keeps.begin(), g_keeps.end(), [owner](const Keep& k) { return k.owner == owner; }),
                      g_keeps.end());
        g_keep_count.store(static_cast<uint32_t>(g_keeps.size()));
        if (before != g_keeps.size()) LOGI("owner %llu: %zu keep-full entr%s ended", static_cast<unsigned long long>(owner),
                                           before - g_keeps.size(), before - g_keeps.size() == 1 ? "y" : "ies");
    }
    // Draining a running transfer callback must not hold the request mutex: that callback may queue a request.
    xfer_end_owner(owner);
}

KilnResult KILN_CALL t_read(KilnEntity entity, KilnItemsInventory* info, KilnItemsSlot* slots, uint32_t capacity,
                            uint32_t* out_count) {
    if (out_count) *out_count = 0;
    if (entity == 0 || (capacity && !slots) || (info && info->size < sizeof(KilnItemsInventory)))
        return KILN_ERR_INVALID_ARGUMENT;
    if (!E.read_ok) return KILN_ERR_UNAVAILABLE;
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    const uintptr_t world = g_world.load();
    if (!world) return KILN_ERR_UNAVAILABLE;
    ReadOut ro{};
    ro.info.size = sizeof ro.info;
    ro.slots = slots;
    ro.capacity = capacity;
    unsigned long code = 0;
    if (!read_inventory(world, g_registry.load(), entity, &ro, &code)) {
        LOGE("reading entity %u's inventory faulted (0x%08lx)", entity, code);
        return KILN_ERR_INTERNAL;
    }
    if (!ro.found) return KILN_ERR_NOT_FOUND;
    if (info) *info = ro.info;
    if (out_count) *out_count = ro.written;
    return ro.written < ro.info.slot_total ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

KilnResult KILN_CALL t_find(uint32_t flags, KilnEntity* out, uint32_t capacity, uint32_t* out_count) {
    if (out_count) *out_count = 0;
    if ((capacity && !out) || (flags & ~0xFu)) return KILN_ERR_INVALID_ARGUMENT;
    if (!E.read_ok) return KILN_ERR_UNAVAILABLE;
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    const uintptr_t world = g_world.load();
    if (!world) return KILN_ERR_UNAVAILABLE;
    unsigned long code = 0;
    uint32_t total = 0;
    if (g_world_api && g_world_api->header.size >= offsetof(KilnWorldApi, find) + sizeof(void*)) {
        std::vector<KilnEntity> all(1u << 16);
        uint32_t n = 0;
        KilnResult rc = g_world_api->find(kHashInventorySetup, all.data(), static_cast<uint32_t>(all.size()), &n);
        if (rc == KILN_ERR_BUFFER_TOO_SMALL) {
            all.resize(n + 1024);
            rc = g_world_api->find(kHashInventorySetup, all.data(), static_cast<uint32_t>(all.size()), &n);
        }
        if (rc == KILN_OK) {
            for (uint32_t i = 0; i < n; ++i) {
                bool m = false;
                if (!inventory_matches(world, all[i], flags, &m, &code)) return KILN_ERR_INTERNAL;
                if (!m) continue;
                if (total < capacity) out[total] = all[i];
                ++total;
            }
            if (out_count) *out_count = total;
            return total > capacity ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
        }
    }
    if (!E.scan_ok) return KILN_ERR_UNAVAILABLE;
    if (!scan_inventories(world, flags, out, capacity, &total, &code)) {
        LOGE("scanning the world's entities faulted (0x%08lx)", code);
        return KILN_ERR_INTERNAL;
    }
    if (out_count) *out_count = total;
    return total > capacity ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

uint32_t KILN_CALL t_item_count() {
    return g_catalog.load(std::memory_order_acquire) ? static_cast<uint32_t>(g_items.size()) : 0;
}

KilnResult copy_info(const Item& it, KilnItemInfo* out) {
    if (!out || out->size < sizeof(KilnItemInfo)) return KILN_ERR_INVALID_ARGUMENT;
    *out = it.info;
    return KILN_OK;
}

KilnResult KILN_CALL t_item_at(uint32_t index, KilnItemInfo* out) {
    if (!g_catalog.load(std::memory_order_acquire)) return KILN_ERR_UNAVAILABLE;
    if (index >= g_items.size()) return KILN_ERR_NOT_FOUND;
    return copy_info(g_items[index], out);
}

KilnResult KILN_CALL t_item_info(KilnItemId id, KilnItemInfo* out) {
    if (!g_catalog.load(std::memory_order_acquire)) return KILN_ERR_UNAVAILABLE;
    auto it = g_by_id.find(id);
    return it == g_by_id.end() ? KILN_ERR_NOT_FOUND : copy_info(g_items[it->second], out);
}

KilnItemId KILN_CALL t_find_item(KilnStr name) {
    if (!g_catalog.load(std::memory_order_acquire) || !name.ptr || !name.len) return 0;
    const std::string s(name.ptr, name.len);
    if (auto it = g_by_name.find(s); it != g_by_name.end()) return g_items[it->second].info.id;
    if (auto it = g_by_lower.find(lower(s)); it != g_by_lower.end()) return g_items[it->second].info.id;
    return 0;
}

KilnItemId KILN_CALL t_id_of_guid(const uint8_t guid[16]) {
    if (!guid) return 0;
    bool zero = true;
    uint32_t h = 0x811c9dc5u;
    for (int i = 0; i < 16; ++i) {
        zero = zero && guid[i] == 0;
        h = (h ^ guid[i]) * 0x01000193u;
    }
    return zero ? 0 : h;
}

KilnResult KILN_CALL t_status(KilnItemsStatus* out) {
    if (!out || out->size < sizeof(KilnItemsStatus)) return KILN_ERR_INVALID_ARGUMENT;
    std::memset(out, 0, sizeof *out);
    out->size = sizeof *out;
    out->program = static_cast<uint8_t>(g_program.load());
    out->transactions = E.tx_ok && out->program == KILN_ITEMS_PROGRAM_RUNNING;
    out->read = E.read_ok && g_world.load() != 0;
    out->catalog = g_catalog.load();
    out->items = t_item_count();
    out->transactions_committed = g_commits.load();
    out->transactions_rolled_back = g_rollbacks.load();
    out->program_updates = g_updates.load();
    {
        std::lock_guard lock(g_mu);
        out->pending = static_cast<uint32_t>(g_pending.size());
    }
    std::lock_guard lock(g_detail_mu);
    std::snprintf(out->detail, sizeof out->detail, "%s", E.tx_ok ? g_detail : "the inventory transaction is not available on this game build");
    return KILN_OK;
}

KilnResult KILN_CALL t_peek_result(uint64_t ticket, KilnItemsResult* out) {
    if (!ticket || !out || out->size < sizeof(KilnItemsResult)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    return g_done.peek(ticket, out) ? KILN_OK : KILN_ERR_NOT_FOUND;
}

const KilnItemsApi kTable = {
    {sizeof(KilnItemsApi), KILN_ITEMS_VERSION},
    &t_give, &t_take, &t_cancel, &t_cancel_owner, &t_read, &t_find,
    &t_item_count, &t_item_at, &t_item_info, &t_find_item, &t_id_of_guid, &t_status,
    &t_fuel, &t_fuel_read, &t_fuel_keep, &t_fuel_keep_end,
    &t_move, &t_container_name, &t_transfer_rule, &t_transfer_listen, &t_transfer_report, &t_transfer_end, &t_status2,
    &t_peek_result,
};

// ============================================================================================= self-test =======
// Labs only: enabled when <kiln data>/kiln.items/selftest exists at boot. Results: "items selftest: PASS|FAIL ..." lines.
//  - names a container through the game's own rename path (set_string + AddComponent, what the server does for a client's
//    Rename), so a lab world without a named chest has one, and the next autosave carries the name into the save;
//  - when the managed test writes <data>/kiln.items/selftest-run (chest A, chest B, slots), runs a native transfer handler
//    through the game's own quick stack / move all (see selftest_handler below).
namespace {
bool g_st_enabled = false;
int g_st_phase = 0;
uint64_t g_st_wait_until = 0;
std::filesystem::path g_st_dir;
KilnEntity g_st_named = 0;
}  // namespace

void selftest_init() {
    char buf[1024];
    size_t len = 0;
    if (g_api->path(KILN_PATH_DATA, buf, sizeof buf, &len) != KILN_OK) return;
    std::error_code ec;
    const std::u8string dir(reinterpret_cast<const char8_t*>(buf), len);
    g_st_dir = std::filesystem::path(dir) / "kiln.items";
    if (!std::filesystem::exists(g_st_dir / "selftest", ec)) return;
    g_st_enabled = true;
    LOGI("items selftest enabled (%s)", reinterpret_cast<const char*>(g_st_dir.u8string().c_str()));
}

void selftest_name_chest() {
    if (!E.names_ok || !E.set_string || !E.add_component) {
        LOGI("items selftest: SKIP naming (no names / set_string / AddComponent on this build)");
        g_st_phase = 99;
        return;
    }
    const uintptr_t world = g_api->server_world();
    KilnEntity ids[256];
    uint32_t n = 0;
    t_find(KILN_ITEMS_FIND_CONTAINERS | KILN_ITEMS_FIND_NONEMPTY, ids, 256, &n);
    n = std::min<uint32_t>(n, 256);
    char name[160];
    for (uint32_t i = 0; i < n; ++i) {  // a chest that already carries a name (from the save): nothing to do
        uint32_t len = 0;
        if (name_of(world, ids[i], name, sizeof name, &len) == KILN_OK) {
            g_st_named = ids[i];
            LOGI("items selftest: PASS a container from the save is already named: entity %u is \"%s\"", ids[i], name);
            g_st_phase = 99;
            return;
        }
    }
    KilnEntity pick = 0;
    for (uint32_t i = 0; i < n; ++i) pick = (pick == 0 || ids[i] < pick) ? ids[i] : pick;
    if (pick == 0) {
        LOGI("items selftest: FAIL naming: no non-empty container in the world");
        g_st_phase = 99;
        return;
    }
    char msg[200];
    if (!name_set_for_lab(world, pick, "Kiln Lab Ores", msg, sizeof msg)) {
        LOGI("items selftest: FAIL naming entity %u: %s", pick, msg);
        g_st_phase = 99;
        return;
    }
    g_st_named = pick;
    LOGI("items selftest: %s", msg);
    g_st_phase = 2;  // read it back next step
}

// ---- the native handler scenario (docs/kiln/modding/inventory.md "Native handlers") ----
// Written by the managed test when it is done: <data>/kiln.items/selftest-run holds "A B" (two containers). A handler is
// registered with transfer_listen, then the game's own move all runs from A to B through kiln.items' move: the handler
// keeps one stack, moves another itself, and (second pass) cancels. The inventories before and after say what happened;
// the ledger puts everything back. Result lines: "items selftest: PASS|FAIL handler ..."; the file selftest-run.done.
constexpr KilnOwner kScOwner = 0x5e1f7e57;  // the self-test's own owner id
struct Scenario {
    int stage = 0;
    KilnEntity a = 0, b = 0;
    KilnSlotId keep = 0, pre = 0;
    uint32_t keep_item = 0, keep_count = 0, pre_item = 0, pre_count = 0;
    std::vector<KilnItemsSlot> a0, b0;
    bool cancel = false;
    uint64_t handle = 0;
    uint32_t calls = 0, ev_kind = 0, read_n = 0, pre_moved = 0, name_len = 0;
    KilnEntity ev_from = 0, ev_to = 0;
    std::vector<KilnItemsMoveChange> ledger;
    size_t restoring = 0;
    uint32_t done = 0, canceled = 0;
    int status = -1;
    int failures = 0;
    std::string report;
} g_sc;

std::vector<KilnItemsSlot> read_all(KilnEntity e) {
    std::vector<KilnItemsSlot> v(256);
    KilnItemsInventory info{};
    info.size = sizeof info;
    uint32_t n = 0;
    t_read(e, &info, v.data(), static_cast<uint32_t>(v.size()), &n);
    v.resize(n);
    return v;
}

uint32_t held(const std::vector<KilnItemsSlot>& v) {
    uint32_t n = 0;
    for (const auto& s : v) n += (s.item && s.count) ? s.count : 0;
    return n;
}

bool same_slots(const std::vector<KilnItemsSlot>& x, const std::vector<KilnItemsSlot>& y) {
    if (x.size() != y.size()) return false;
    for (size_t i = 0; i < x.size(); ++i)
        if (x[i].slot != y[i].slot || x[i].item != y[i].item || x[i].count != y[i].count) return false;
    return true;
}

void sc_line(bool ok, const char* fmt, ...) {
    char b[512];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(b, sizeof b, fmt, ap);
    va_end(ap);
    g_sc.failures += !ok;
    LOGI("items selftest: %s %s", ok ? "PASS" : "FAIL", b);
    g_sc.report += std::string(ok ? "PASS " : "FAIL ") + b + "\n";
}

void KILN_CALL sc_handler(const KilnItemsTransferEvent* ev, const KilnItemsTransferHost* host, void*) {
    if (ev->from != g_sc.a || ev->to != g_sc.b) return;
    ++g_sc.calls;
    g_sc.ev_kind = ev->kind;
    g_sc.ev_from = ev->from;
    g_sc.ev_to = ev->to;
    KilnItemsSlot slots[256];
    g_sc.read_n = host->read(ev->ctx, ev->from, slots, 256);
    char name[16];
    g_sc.name_len = host->name(ev->ctx, ev->from, name, sizeof name);
    if (!g_sc.cancel) host->keep(ev->ctx, g_sc.keep);
    g_sc.pre_moved = host->move(ev->ctx, g_sc.pre, ev->to, 0);
    if (g_sc.cancel) host->cancel(ev->ctx);
}

void sc_finish();
void sc_submit_move_all();
void sc_restore_next();

void KILN_CALL sc_restore_done(const KilnItemsMoveResult* r, void*) {
    if (r->status != KILN_ITEMS_DONE) sc_line(false, "handler: restoring a stack failed: status %d %s", r->status, r->message);
    sc_restore_next();
}

void sc_restore_next() {
    if (g_sc.restoring < g_sc.ledger.size()) {
        const KilnItemsMoveChange& c = g_sc.ledger[g_sc.restoring++];
        KilnItemsMoveRequest q{};
        q.size = sizeof q;
        q.kind = KILN_MOVE_STACK;
        q.from = g_sc.b;
        q.to = g_sc.a;
        q.slot = c.to_slot;
        q.count = c.amount;
        q.to_slot = c.from_slot;
        if (t_move(kScOwner, &q, &sc_restore_done, nullptr, nullptr) != KILN_OK) sc_restore_next();
        return;
    }
    auto a1 = read_all(g_sc.a), b1 = read_all(g_sc.b);
    sc_line(same_slots(a1, g_sc.a0) && same_slots(b1, g_sc.b0), "handler: pass %d restored by its ledger, both containers slot for slot as before (%zu piece(s))",
            g_sc.stage, g_sc.ledger.size());
    if (g_sc.stage == 1) {
        g_sc.stage = 2;
        g_sc.cancel = true;
        sc_submit_move_all();
    } else {
        sc_finish();
    }
}

void KILN_CALL sc_move_done(const KilnItemsMoveResult* r, void*) {
    auto a1 = read_all(g_sc.a), b1 = read_all(g_sc.b);
    g_sc.done = r->done;
    g_sc.canceled = r->canceled;
    g_sc.status = r->status;
    g_sc.ledger.assign(r->changes, r->changes + r->change_count);
    const uint32_t a0n = held(g_sc.a0), a1n = held(a1), b0n = held(g_sc.b0), b1n = held(b1);
    if (!g_sc.cancel) {
        const KilnItemsSlot* kept = nullptr;
        for (const auto& s : a1) if (s.slot == g_sc.keep) kept = &s;
        sc_line(g_sc.calls == 1 && g_sc.ev_kind == KILN_TRANSFER_MOVE_ALL && g_sc.ev_from == g_sc.a && g_sc.ev_to == g_sc.b,
                "handler: the game's move all ran the handler once: kind %u, %u -> %u, it read %u slots, name length %u", g_sc.ev_kind,
                g_sc.ev_from, g_sc.ev_to, g_sc.read_n, g_sc.name_len);
        sc_line(kept && kept->item == g_sc.keep_item && kept->count == g_sc.keep_count && a1n == g_sc.keep_count,
                "handler: keep: slot %u:%u still holds its %u x 0x%08x, everything else left %u (%u items)", KILN_SLOT_INVENTORY(g_sc.keep),
                KILN_SLOT_INDEX(g_sc.keep), g_sc.keep_count, g_sc.keep_item, g_sc.a, a0n - a1n);
        sc_line(g_sc.pre_moved == g_sc.pre_count && b1n == b0n + (a0n - g_sc.keep_count) && r->status == KILN_ITEMS_DONE && r->done == a0n - g_sc.keep_count,
                "handler: it moved a stack itself first (%u of 0x%08x), the game moved the rest: %u items in all, B %u -> %u", g_sc.pre_moved,
                g_sc.pre_item, r->done, b0n, b1n);
    } else {
        sc_line(g_sc.calls == 1 && r->canceled == 1 && a1n == a0n - g_sc.pre_count && g_sc.pre_moved == g_sc.pre_count && r->done == g_sc.pre_count,
                "handler: cancel: the game's move all did not run; only the handler's own move happened (%u items, canceled %u, A %u -> %u)", r->done,
                r->canceled, a0n, a1n);
    }
    g_sc.restoring = 0;
    sc_restore_next();
}

void sc_submit_move_all() {
    g_sc.calls = 0;
    g_sc.pre_moved = 0;
    g_sc.ledger.clear();
    KilnItemsMoveRequest q{};
    q.size = sizeof q;
    q.kind = KILN_MOVE_ALL;
    q.from = g_sc.a;
    q.to = g_sc.b;
    if (t_move(kScOwner, &q, &sc_move_done, nullptr, nullptr) != KILN_OK) {
        sc_line(false, "handler: the move all was refused");
        sc_finish();
    }
}

void sc_finish() {
    t_transfer_end(g_sc.handle);
    g_sc.stage = 3;
    LOGI("items selftest: handler scenario done, %d failure(s)", g_sc.failures);
    std::error_code ec;
    std::filesystem::remove(g_st_dir / "selftest-run", ec);
    if (FILE* f = std::fopen((g_st_dir / "selftest-run.done").string().c_str(), "wb")) {
        std::fprintf(f, "%s%s\n", g_sc.report.c_str(), g_sc.failures ? "RESULT fail" : "RESULT ok");
        std::fclose(f);
    }
}

void sc_start() {
    std::ifstream in(g_st_dir / "selftest-run");
    unsigned a = 0, b = 0;
    if (!(in >> a >> b) || a == 0 || b == 0) return;
    g_sc = Scenario{};
    g_sc.a = a;
    g_sc.b = b;
    g_sc.a0 = read_all(a);
    g_sc.b0 = read_all(b);
    // two stacks of different items in A: the first is kept, the second the handler moves itself
    for (const auto& s : g_sc.a0) {
        if (!s.item || !s.count || s.category != 4) continue;
        if (!g_sc.keep) { g_sc.keep = s.slot; g_sc.keep_item = s.item; g_sc.keep_count = s.count; }
        else if (s.item != g_sc.keep_item && !g_sc.pre) { g_sc.pre = s.slot; g_sc.pre_item = s.item; g_sc.pre_count = s.count; }
    }
    if (!g_sc.keep || !g_sc.pre) {
        sc_line(false, "handler: container %u needs two stacks of different items", a);
        sc_finish();
        return;
    }
    if (t_transfer_listen(kScOwner, &sc_handler, nullptr, &g_sc.handle) != KILN_OK) {
        sc_line(false, "handler: transfer_listen refused: %s", "see kiln.log");
        sc_finish();
        return;
    }
    g_sc.stage = 1;
    LOGI("items selftest: handler scenario on %u -> %u: keep %u:%u (0x%08x x%u), pre-move %u:%u (0x%08x x%u)", a, b,
         KILN_SLOT_INVENTORY(g_sc.keep), KILN_SLOT_INDEX(g_sc.keep), g_sc.keep_item, g_sc.keep_count, KILN_SLOT_INVENTORY(g_sc.pre),
         KILN_SLOT_INDEX(g_sc.pre), g_sc.pre_item, g_sc.pre_count);
    sc_submit_move_all();
}

void selftest_pre(uint64_t step) {
    if (!g_st_enabled) return;
    if (g_st_phase == 99 && g_sc.stage == 0 && (step & 15) == 0) {
        std::error_code ec;
        if (std::filesystem::exists(g_st_dir / "selftest-run", ec)) sc_start();
        return;
    }
    if (g_st_phase >= 99) return;
    if (g_st_phase == 0) {
        if (g_program.load() != KILN_ITEMS_PROGRAM_RUNNING || !g_catalog.load() || !g_api->server_world()) return;
        g_st_wait_until = step + 50;
        g_st_phase = 1;
        return;
    }
    if (step < g_st_wait_until) return;
    if (g_st_phase == 1) return selftest_name_chest();
    if (g_st_phase == 2) {
        char name[160];
        uint32_t len = 0;
        const KilnResult rc = name_of(g_api->server_world(), g_st_named, name, sizeof name, &len);
        if (rc == KILN_OK && std::strcmp(name, "Kiln Lab Ores") == 0)
            LOGI("items selftest: PASS container_name read back: entity %u is \"%s\"", g_st_named, name);
        else
            LOGI("items selftest: FAIL container_name read back: entity %u -> result %d, \"%s\"", g_st_named, rc, rc == KILN_OK ? name : "");
        g_st_phase = 99;
    }
}

// ============================================================================================= boot ============

template <typename F>
bool sym(const char* name, F* out) {
    uintptr_t a = 0;
    if (g_api->symbol(KilnStr{name, std::strlen(name)}, &a) != KILN_OK || !a) return false;
    *out = reinterpret_cast<F>(a);
    return true;
}
bool cst(const char* name, int64_t* out) {
    return g_api->constant(KilnStr{name, std::strlen(name)}, out) == KILN_OK;
}
bool feat(const char* name) { return g_api->feature(KilnStr{name, std::strlen(name)}); }

void resolve() {
    bool ok = feat("items.transaction") && sym("items/InventoryTxCtor", &E.tx_ctor) &&
              sym("items/InventoryTxInit", &E.tx_init) && sym("items/InventoryAddItems", &E.add_items) &&
              sym("items/InventoryRemoveItems", &E.remove_items) && sym("items/InventoryCommit", &E.commit) &&
              sym("items/InventoryRollback", &E.rollback) && sym("items/InventoryFreeArrays", &E.free_arrays) &&
              sym("items/ReadOther", &E.read_other) && sym("items/JobStorage", &E.job_storage) &&
              sym("items/ItemRegistryFind", &E.item_find);
    int64_t result = 0;
    ok = ok && cst("items/tx.result", &result) && cst("items/tx.ops", &E.tx_ops) &&
         cst("items/tx.operator", &E.tx_operator) && cst("items/setup.operator", &E.setup_operator) &&
         cst("items/item.flags", &E.item_flags) && cst("items/item.max_stack", &E.item_max_stack) &&
         cst("items/setup_c.initialized", &E.setup_initialized) && cst("world.storage", &E.world_storage);
    // Plausibility: the tx ends with the result-flags byte; the setup block's operator lands at the tx's operator.
    ok = ok && result > 0x7000 && result < 0x10000 && E.setup_operator < static_cast<int64_t>(kSetupBlock) &&
         E.tx_ops < 0x200;
    E.tx_size = (result + 8 + 15) & ~15ll;
    sym("items/GuidToItemId", &E.guid_to_id);
    if (feat("items.recipe") && !(sym("items/RecipeApply", &E.recipe_apply) && sym("content/RecipeFind", &E.recipe_find)))
        E.recipe_apply = nullptr;
    // Shared by the transaction checks, reading, the scan and the catalog.
    const bool common = sym("items/ComponentMapFind", &E.component_map_find) && sym("GetComponent", &E.get_component) &&
                        cst("items/registry.components", &E.registry_components) &&
                        cst("items/registry.items", &E.registry_items) && cst("items/hmap.capacity", &E.hmap_capacity) &&
                        cst("items/hmap.count", &E.hmap_count) && cst("items/hmap.bits", &E.hmap_bits) &&
                        cst("items/hmap.keys", &E.hmap_keys) && cst("items/hmap.values", &E.hmap_values) &&
                        cst("world.storage", &E.world_storage);
    E.tx_ok = ok && common;
    E.read_ok = feat("items.read") && common && E.item_find && cst("items/setup_c.initialized", &E.setup_initialized) &&
                cst("items/item.flags", &E.item_flags);
    E.catalog_ok = feat("items.catalog") && common && cst("items/item.max_stack", &E.item_max_stack);
    E.scan_ok = feat("items.scan") && E.read_ok && cst("items/storage.entities", &E.storage_entities);
    E.program_ok = feat("items.program") && common && sym("items/RegisterProgram", &E.register_program) &&
                   sym("items/RegisterAllPrograms", &E.register_all) && sym("items/IterBegin", &E.iter_begin) &&
                   cst("items/world.program_count", &E.program_count);
    const bool pide = E.tx_ok && pide_install(g_api);
    LOGI("gear options at creation (KILN_ITEMS_UNLOCK, KILN_ITEMS_GEM_SLOT): %s", pide ? "on" : "OFF (items.pide_options)");
    // v3 (docs/re/items.md sections 13 and 14)
    {
        const bool qs = sym("items/QuickStack", &E.quick_stack) && sym("items/MoveAll", &E.move_all);
        const bool mv = sym("items/SlotTransfer", &E.slot_transfer);
        uintptr_t area = 0;
        sym("items/AreaDeposit", &area);
        E.area_deposit = area;
        E.move_ok = E.tx_ok && feat("items.move") && qs && mv;
        E.transfer_ok = E.tx_ok && feat("items.transfer") && qs && area != 0;
        uintptr_t str = 0, add = 0;
        sym("items/SetString", &str);
        sym("items/AddComponent", &add);
        E.set_string = str;
        E.add_component = add;
        int64_t s_cap = 0, s_keys = 0, s_vals = 0, s_cnt = 0;
        E.names_ok = E.read_ok && feat("items.names") && cst("items/strings.bitmap", &E.strings_bitmap) && str != 0 &&
                     cst("items/strings.capacity", &s_cap) && cst("items/strings.keys", &s_keys) &&
                     cst("items/strings.values", &s_vals) && cst("items/strings.count", &s_cnt);
        // the string table's map must have the item registry's layout, relative to its bitmap pointer
        if (E.names_ok &&
            !(s_cap - E.strings_bitmap == E.hmap_capacity - E.hmap_bits && s_keys - E.strings_bitmap == E.hmap_keys - E.hmap_bits &&
              s_vals - E.strings_bitmap == E.hmap_values - E.hmap_bits && s_cnt - E.strings_bitmap == E.hmap_count - E.hmap_bits)) {
            E.names_ok = false;
            LOGW("container names off: the string table's map layout differs from the item registry's");
        }
        // a mod's move is only ever as good as what the game's functions return: refuse a transaction layout whose result
        // bytes do not fit the 12-byte results the code reads
        gate_features();
    }
    E.fuel_ok = E.tx_ok && feat("items.fuel") && sym("items/WriteOther", &E.write_other) &&
                sym("items/JobTracker", &E.job_tracker) && sym("items/JobWriteFlag", &E.job_write_flag) &&
                sym("items/AttrFind", &E.attr_find) && sym("items/AttrChanged", &E.attr_changed);
    if (E.tx_ok) {
        g_tx = static_cast<uint8_t*>(VirtualAlloc(nullptr, static_cast<SIZE_T>(E.tx_size), MEM_COMMIT | MEM_RESERVE,
                                                  PAGE_READWRITE));
        if (!g_tx) E.tx_ok = false;
    }
    LOGI("resolved: transaction %s (tx %lld bytes), read %s, catalog %s, scan %s, own program registration %s, recipe path %s, "
         "tracked Fuel writes %s, moves %s, transfer hooks %s, container names %s",
         E.tx_ok ? "on" : "OFF", static_cast<long long>(E.tx_size), E.read_ok ? "on" : "OFF",
         E.catalog_ok ? "on" : "OFF", E.scan_ok ? "on" : "OFF", E.program_ok ? "possible" : "OFF",
         E.recipe_apply ? "on" : "OFF", E.fuel_ok ? "on" : "OFF", E.move_ok ? "on" : "OFF", E.transfer_ok ? "possible" : "OFF",
         E.names_ok ? "on" : "OFF");
    xfer_init();  // after every symbol above: a lab switch may install the hooks right now
}

void KILN_CALL on_ready(void*) {
    if (!E.tx_ok) {
        set_detail("the inventory transaction is not available on this game build");
        return;
    }
    char mode[16] = "auto";
    GetEnvironmentVariableA("KILN_ITEMS_PROGRAM", mode, sizeof mode);  // auto | own | world
    const auto* w = static_cast<const KilnWorldApi*>(g_api->service(KilnStr{KILN_WORLD_SERVICE, std::strlen(KILN_WORLD_SERVICE)}, 1));
    const bool world_ok = w && w->header.size >= sizeof(KilnWorldApi);
    if (std::strcmp(mode, "own") != 0 && world_ok) {
        if (register_with_world(w)) return;
        if (std::strcmp(mode, "world") == 0) {
            set_detail("kiln.world refused the program");
            return;
        }
        LOGW("falling back to own program registration");
    } else if (std::strcmp(mode, "world") == 0) {
        LOGE("KILN_ITEMS_PROGRAM=world but kiln.world is not available");
        set_detail("kiln.world is not available");
        return;
    }
    if (!register_own()) {
        set_detail("the program cannot be registered on this build");
        LOGE("give/take unavailable: the program cannot be registered on this build");
    }
}

}  // namespace

namespace kitems {
void items_log(int level, const char* fmt, ...) {
    char b[768];
    va_list ap;
    va_start(ap, fmt);
    const int n = std::vsnprintf(b, sizeof b, fmt, ap);
    va_end(ap);
    if (g_api && n > 0) g_api->log(g_owner, level, KilnStr{b, static_cast<size_t>(std::min(n, int(sizeof b) - 1))});
}
const KilnApi* items_api() { return g_api; }
}  // namespace kitems

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = KilnStr{kName, std::strlen(kName)};
    info->version = KilnStr{"3", 1};
    info->on_ready = &on_ready;
    resolve();
    selftest_init();
    KilnHandle h = 0;
    if (!cst("storage.kind", &g_storage_kind)) g_storage_kind = -1;
    api->subscribe_world(owner, &on_world, nullptr, &h);
    if (KilnResult rc = api->subscribe_tick(owner, KILN_TICK_PRE, &on_pre, nullptr, &h); rc != KILN_OK) return rc;
    // Published even when give/take are off on this build: their functions then return KILN_ERR_UNAVAILABLE, and
    // status() says why.
    return api->register_service(owner, KilnStr{KILN_ITEMS_SERVICE, std::strlen(KILN_ITEMS_SERVICE)}, &kTable);
}
