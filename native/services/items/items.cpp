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
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include "engine.h"
#include "kiln_api.h"
#include "safetyhook.hpp"
#include "services/kiln_items.h"
#include "services/kiln_world.h"

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
};

std::mutex g_mu;  // guards the three below; never held while calling the game or a callback
std::deque<Request> g_pending;
std::vector<KilnItemsResult> g_done;
std::unordered_map<uint64_t, Callback> g_callbacks;

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

// One update of the program: verify once, then run up to kPerUpdate queued transactions.
void program_body(void* ctx, const uint64_t* it) {
    g_updates.fetch_add(1, std::memory_order_relaxed);
    g_last_update_step.store(g_step.load(std::memory_order_relaxed), std::memory_order_relaxed);
    if (g_program.load() == KILN_ITEMS_PROGRAM_BROKEN) return;
    if (g_verified) {  // idle steps cost one lock: nothing to check or run
        std::lock_guard lock(g_mu);
        if (g_pending.empty()) return;
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

void deliver() {
    std::vector<KilnItemsResult> done;
    std::vector<Callback> cbs;
    {
        std::lock_guard lock(g_mu);
        if (g_done.empty()) return;
        done.swap(g_done);
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
    }
    g_verified = false;
    g_world.store(0);
    g_registry.store(nullptr);
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
    poll_world_program();
    expire();
    deliver();
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
                                     KILN_ITEMS_NOTIFY | KILN_ITEMS_NO_MERGE | KILN_ITEMS_DRY_RUN | KILN_ITEMS_RECIPE);
    if (r->flags & ~allowed) return KILN_ERR_INVALID_ARGUMENT;
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

KilnResult KILN_CALL t_cancel(uint64_t ticket) {
    std::lock_guard lock(g_mu);
    return g_callbacks.erase(ticket) ? KILN_OK : KILN_ERR_NOT_FOUND;
}

void KILN_CALL t_cancel_owner(KilnOwner owner) {
    std::lock_guard lock(g_mu);
    for (auto it = g_callbacks.begin(); it != g_callbacks.end();)
        it = it->second.owner == owner ? g_callbacks.erase(it) : std::next(it);
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

const KilnItemsApi kTable = {
    {sizeof(KilnItemsApi), KILN_ITEMS_VERSION},
    &t_give, &t_take, &t_cancel, &t_cancel_owner, &t_read, &t_find,
    &t_item_count, &t_item_at, &t_item_info, &t_find_item, &t_id_of_guid, &t_status,
};

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
    if (E.tx_ok) {
        g_tx = static_cast<uint8_t*>(VirtualAlloc(nullptr, static_cast<SIZE_T>(E.tx_size), MEM_COMMIT | MEM_RESERVE,
                                                  PAGE_READWRITE));
        if (!g_tx) E.tx_ok = false;
    }
    LOGI("resolved: transaction %s (tx %lld bytes), read %s, catalog %s, scan %s, own program registration %s, recipe path %s",
         E.tx_ok ? "on" : "OFF", static_cast<long long>(E.tx_size), E.read_ok ? "on" : "OFF",
         E.catalog_ok ? "on" : "OFF", E.scan_ok ? "on" : "OFF", E.program_ok ? "possible" : "OFF",
         E.recipe_apply ? "on" : "OFF");
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

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = KilnStr{kName, std::strlen(kName)};
    info->version = KilnStr{"1", 1};
    info->on_ready = &on_ready;
    resolve();
    KilnHandle h = 0;
    if (!cst("storage.kind", &g_storage_kind)) g_storage_kind = -1;
    api->subscribe_world(owner, &on_world, nullptr, &h);
    if (KilnResult rc = api->subscribe_tick(owner, KILN_TICK_PRE, &on_pre, nullptr, &h); rc != KILN_OK) return rc;
    // Published even when give/take are off on this build: their functions then return KILN_ERR_UNAVAILABLE, and
    // status() says why.
    return api->register_service(owner, KilnStr{KILN_ITEMS_SERVICE, std::strlen(KILN_ITEMS_SERVICE)}, &kTable);
}
