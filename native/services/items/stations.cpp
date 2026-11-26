// SPDX-License-Identifier: MIT
// kiln.stations (since 0.2.0-dev.29): the production stations of a base and the game's own station actions, for code.
// ABI native/core/abi/services/kiln_stations.h, evidence docs/re/stations.md, logic without game memory in
// stations_logic.h (host test tests/stations_test.cpp).
//
//  - Reads (tick thread, Pre or Post): the game's PlayerBasesSystemServer (its base records, each with the
//    crafting-stock list and the water-source list the game rebuilds every server frame), BaseAtPosition (the game's own
//    lookup), FactoryStation / Crafting / BaseIdComponent through kiln.world, inventories through kiln.items' reads,
//    recipes and ingredient categories from the game's RecipeRegistry, the game's own ResolveRecipeInputs.
//  - Actions: kiln_items_stations, a system program registered through kiln.world with player_crafting's own access
//    (minus PlayerInput / ServerConsumedPlayerInput, plus Crafting). FILL calls the game's AutoTransferRecipe (the code of
//    the "fill from chests" button) on an inventory transaction set up as the game's handler sets it up, with the
//    station's base stock and no player; STOP / RESUME / SELECT write FactoryStation as the game's player_crafting does
//    (the program's tracked write) after ServerFactoryInfo is told. Every game call and game read is guarded.
//  - Events: while a watch is on, Pre reads every known station's FactoryStation and diffs it; a scan of the world's
//    entities every 32 steps finds stations placed and removed and the bases' changes.
//  - Station outputs as crafting stock (GitHub #68, 0.2.0-dev.29.17): a FILL also takes, last, from the Outputs of
//    opted-in stations of the same base. Inside the program's one transaction Kiln first asks the game's AutoTransferRecipe
//    for each input's shortfall with a source list of only the stations that lend that item (count 1, crafts = the
//    shortfall: the game moves exactly that), then for the rest with the base's own stock as always. A FILL that names
//    ordinary chests does the same for those chests in between: player and Magic chests first, then the chests, then
//    the station outputs. docs/re/stations.md §8 and §9.
#include "stations.h"
#include "dyes.h"
#include "storage_guard.h"

#include <windows.h>
#include <intrin.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstddef>
#include <cstdio>
#include <cstring>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <tuple>
#include <unordered_map>
#include <vector>

#include "../quests/kiln_knowledge.h"
#include "engine.h"
#include "equip.h"
#include "equip_logic.h"
#include "kiln_call_site.hpp"
#include "kiln_hook.hpp"
#include "kiln_mid_frames.hpp"
#include "../../core/src/hooks_protect_internal.h"
#include "instances.h"
#include "sets.h"
#include "stations_logic.h"
#include "services/kiln_caps.h"
#include "services/kiln_gameplay.h"
#include "services/kiln_players.h"
#include "services/kiln_rules.h"
#include "services/kiln_stations.h"
#include "xfer.h"
#include "../world/transform_layout.h"

namespace kitems {
namespace {

using namespace stations;

#define LOGI(...) items_log(KILN_LOG_INFO, __VA_ARGS__)
#define LOGW(...) items_log(KILN_LOG_WARN, __VA_ARGS__)
#define LOGE(...) items_log(KILN_LOG_ERROR, __VA_ARGS__)

constexpr const char* kProgram = "kiln_items_stations";
constexpr uint32_t kMaxPending = 1024;
constexpr uint32_t kPerStep = 16;
constexpr uint64_t kTimeoutSteps = 250;      // ~10 s: a request the program did not run is answered DROPPED
constexpr uint64_t kScanEvery = 32;          // steps between scans of the world's stations and altars
constexpr uint32_t kMaxStations = 4096;
constexpr uint32_t kMaxAltars = 64;
constexpr uint64_t kAfterCraftMaxSteps = 24 * 60 * 60;  // ~1 h of steps: a stop waiting for a cycle's end gives up
constexpr uint32_t kRecipeVecInline = KILN_STATIONS_MAX_INPUTS;

template <typename T>
T rd(const void* p, int64_t off) {
    T v;
    std::memcpy(&v, static_cast<const uint8_t*>(p) + off, sizeof v);
    return v;
}
template <typename T>
void wr(void* p, int64_t off, T v) {
    std::memcpy(static_cast<uint8_t*>(p) + off, &v, sizeof v);
}

uint32_t fnv(const char* s) {
    uint32_t h = 0x811c9dc5u;
    for (; *s; ++s) h = (h ^ static_cast<uint8_t>(*s)) * 0x01000193u;
    return h;
}
uint32_t comp_hash(const char* name) {
    char b[96];
    std::snprintf(b, sizeof b, "keen::ecs::%s", name);
    return fnv(b);
}
KilnStr str(const char* s) { return KilnStr{s, std::strlen(s)}; }

// ---- the game's functions and layouts (sigdb server.d/stations.json) ----
struct BaseHit {  // BaseAtPosition's result: {u8 error (0 found, 0x1f none); ...; u8 index at +8}
    uint8_t error;
    uint8_t pad[7];
    uint8_t index;
    uint8_t pad2[7];
};
using BaseAtFn = BaseHit*(__fastcall*)(BaseHit* out, void* bases, const float* position);
// int AutoTransferRecipe(tx, const BaseStock* {A.ptr, A.count, B.ptr, B.count}, const {stacks*, count}* inputs,
//                        u32 station, u16 crafts, u32 waterCharges, u32 player)
using FillFn = int32_t(__fastcall*)(void* tx, const void* stock, const void* inputs, uint32_t station, uint16_t crafts,
                                    uint32_t water_charges, uint32_t player);
// AutoTransferItem(tx, {u32* ids, count}* stock, item, station, u16 amount, player): the station screen's per-ingredient
// transfer (S 0x198bf0, C 0x3f7950; docs/re/stations.md section 10.9). 0 moved, 4 short, 2 no room.
using ItemFillFn = int32_t(__fastcall*)(void* tx, const void* stock, uint32_t item, uint32_t station, uint16_t amount,
                                        uint32_t player);
// ResolveRecipeInputs(recipe, DynamicArray<RecipeItemStack 24 B>* out, const RecipeInputCategorySelections*, RecipeRegistry*)
using ResolveFn = void(__fastcall*)(const void* recipe, void* out, const void* selections, void* recipes);
using InvalidateFn = void(__fastcall*)(void* factory_info, uint32_t station);
using SpeedFn = float(__fastcall*)(void* balancing);
using ProgramTimeFn = int64_t*(__fastcall*)(void* ctx, int64_t* out);
using RecipeFindFn = void*(__fastcall*)(uint32_t id, void* recipes);
using GetTemplateFn = void* (*)(uintptr_t world, uint32_t hash, uint32_t entity);
// InventoryPay's base-stock call (docs/re/stations.md §10): err byte, then the 4-qword stock
// {items*, count, water*, count}. arg3/bases are the tx's copied setup slots [0xD] and [0xE].
struct StockErr { uint8_t error; uint8_t pad[3]; uint32_t value; };
struct StockLists { uint32_t* items; uint64_t nitems; uint32_t* water; uint64_t nwater; };
using BaseStockFn = void(__fastcall*)(StockErr* err, StockLists* stock, uint64_t arg3, uint64_t bases, void* pos, uint32_t player);
// gems/PayMaterial: removeItems of one item, no preferred slot. out.left is still missing when error is 0.
struct PayOut { uint8_t error; uint8_t pad[3]; uint32_t zero; uint32_t left; };
using PayFn = PayOut*(__fastcall*)(PayOut* out, void* tx, uint32_t entity, uint32_t item, uint32_t count);

struct Game {
    BaseAtFn base_at = nullptr;
    FillFn fill = nullptr;
    ItemFillFn fill_item = nullptr;  // GitHub #132 (0.2.0-dev.31.16)
    ResolveFn resolve = nullptr;
    InvalidateFn invalidate = nullptr;
    SpeedFn speed = nullptr;
    ProgramTimeFn program_time = nullptr;
    RecipeFindFn recipe_find = nullptr;
    GetTemplateFn get_template = nullptr;  // InventoryCraftingStock when the deposit override is unset; missing: unknown stays allowed
    BaseStockFn base_stock = nullptr;
    PayFn pay = nullptr;
    uintptr_t recipe_paid = 0;  // rules/RecipePaid: InventoryPay's return inside RecipeApply
    int64_t bases_count = 0, stride = 0, aabb_y = 0, zone_mask_rel = 0, zones = 0, stock_items = 0, stock_items_count = 0,
            stock_water = 0, stock_water_count = 0;
    int64_t cat_bits = 0, cat_cap = 0, cat_keys = 0, cat_values = 0, cat_count = 0;
    int64_t fs_next = 0, fs_next_sel = 0, fs_stopped = 0, fs_use_all = 0, fs_start = 0, fs_pause = 0, fs_missing = 0,
            fs_running = 0, fs_running_sel = 0, fs_size = 0;
    int64_t recipe_duration = 0, recipe_water = 0;
    bool registered_ok = false;
    bool read = false, fill_ok = false, control = false, events = false, outputs_ok = false, craft_ok = false;
    bool fill_items_ok = false;  // stations.fill_items: the per-ingredient transfer (KILN_STATION_FILL_ITEM)
};
// The station a CraftRecipe call is paying for, set by kiln.gameplay's craft hook for the duration of that call
// (same thread). 0: the hook is not in this call.
thread_local uint32_t t_craft_station = 0;
// How far the payment of the craft running on this thread got (GitHub #132 follow-up): opened by kiln.gameplay's craft hook
// (kiln_stations_set_craft_context), noted by craft_pay_impl, written as one kiln.log line at the craft's end
// (kiln_stations_craft_done).
thread_local CraftTrace t_trace;
void trace(uint8_t stage, uint64_t from = 0, const char* why = nullptr) {
    if (!t_trace.active) return;
    t_trace.stage = stage;
    t_trace.from = from;
    t_trace.why = why;
}
// What a Kiln client may count, from the server's last snapshot (bag + Magic chests + opted-in outputs).
std::mutex g_craft_mu;
std::unordered_map<uint32_t, uint32_t> g_craft_have;  // g_craft_mu
std::atomic<bool> g_craft_on{false};
Game G;

// FactoryStation (reflection, docs/re/stations.md §1.1) and the other components read
constexpr int64_t kFsState = 0x00;
constexpr int64_t kCraftingWorkshop = 0x10;   // Crafting.workshopId
constexpr int64_t kRecipeId = 0x10, kRecipeWorkshop = 0x24, kRecipeInputs = 0x28, kRecipeOutputs = 0x30,
                  kRecipeKnowledge = 0x50;     // RecipeInfo
constexpr uint32_t kInputStride = 48, kOutputStride = 24;
constexpr int64_t kInputItem = 0x10, kInputCount = 0x14, kInputCategory = 0x28, kInputCategoryCount = 0x2c;
constexpr uint32_t kSetupBases = 0xD8, kSetupPosition = 0xE0, kSetupNetworkFuel = 0x68;  // the setup block (items.md §3)

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
const KilnWorldApi* g_w = nullptr;
const KilnPlayersApi* g_players = nullptr;
const KilnKnowledgeApi* g_know = nullptr;
const KilnGameplayApi* g_gameplay = nullptr;

uint32_t H_FACTORY = 0, H_CRAFTING = 0, H_BASE_ID = 0, H_TRANSFORM = 0, H_NETWORK_FUEL = 0, H_SETUP = 0, H_STOCK = 0;

std::mutex g_mu;  // everything below that is not atomic; never held while calling the game or a callback
char g_detail[160] = "not resolved";
std::atomic<bool> g_off{false};

// ---- program ----
KilnHandle g_prog = 0;
std::atomic<int> g_state{KILN_ITEMS_PROGRAM_NONE};
bool g_slots = false;
int g_s_sing[7] = {-1, -1, -1, -1, -1, -1, -1};  // Balancing, Items, Attributes, Bases, Recipes, Knowledge, FactoryInfo
int g_s_acc[A_COUNT] = {};
int g_s_factory = -1, g_s_netfuel = -1, g_s_transform = -1, g_s_crafting = -1, g_s_deposit = -1;
int g_s_ev[2] = {-1, -1};  // PickupEvent, InventoryFull
uint8_t* g_tx = nullptr;
std::atomic<int64_t> g_now{0};          // the world's program time of the last step (ns)
std::atomic<uint32_t> g_speed_bits{0};  // FactorySpeed's factor of the last step (1 / factoryProductionSpeedFactor)

// ---- requests (Req: stations_logic.h) ----
struct Callback {
    KilnStationDoneFn fn;
    void* user;
};
std::vector<Req> g_incoming;  // submitted, SELECT not checked yet (g_mu)
std::vector<Req> g_ready;     // the program takes them (g_mu)
std::vector<Req> g_after;     // STOP after craft, waiting for the cycle's end (program thread only, under g_mu)
std::vector<KilnStationResult> g_done;  // g_mu
std::unordered_map<uint64_t, Callback> g_callbacks;  // g_mu
std::atomic<uint32_t> g_pending_n{0};
std::atomic<uint32_t> g_done_total{0};
// Kiln's last STOP / RESUME / SELECT write per station: whose, when, and what it set (a state event names the owner only
// for exactly that change) (g_mu)
struct Wrote {
    KilnOwner owner = 0;
    uint64_t step = 0;
    bool sets_stopped = false, sets_selected = false;
    uint8_t stopped = 0;
    uint32_t selected = 0;
};
std::unordered_map<KilnEntity, Wrote> g_wrote;

// ---- station outputs as crafting stock: the opted-in stations (g_mu), this session's ----
constexpr uint32_t kMaxSources = 4096;
CraftSourceRegistry g_registered;
std::vector<KilnEntity> g_sources;  // sorted, unique

// ---- watch ----
std::atomic<int> g_watchers{0};
EventRing g_ring;  // g_mu
struct Known {
    Snap snap;
    bool fresh;
};
std::unordered_map<KilnEntity, Known> g_known;  // tick thread
uint64_t g_scan_step = 0;
bool g_scanned = false;
std::atomic<bool> g_scanned_any{false};  // status(): the first scan is done (any thread)
std::atomic<uint32_t> g_station_count{0};  // status(): stations at the last scan (any thread)
std::vector<KilnEntity> g_stations;  // the last scan (tick thread)
uint64_t g_stations_step = 0;
struct Altar {
    KilnEntity e;
    KilnBaseId id;
    int32_t base;
};
std::vector<Altar> g_altars;  // tick thread
uint64_t g_altars_step = 0;
uint32_t g_bases_sig = 0;      // a fingerprint of the base records, for BASES events
uint32_t g_bases_version = 0;  // raised when it changes

void set_detail(const char* s) {
    std::lock_guard lock(g_mu);
    std::snprintf(g_detail, sizeof g_detail, "%s", s);
}

void lower(const char* feature, uint32_t reason, const char* why) {
    set_detail(why);
    const auto* caps = static_cast<const KilnCapsApi*>(g_api ? g_api->service(str(KILN_CAPS_SERVICE), KILN_CAPS_VERSION) : nullptr);
    if (caps && caps->report) caps->report(g_owner, str(feature), KILN_CAP_UNAVAILABLE, reason, str(why));
}

void switch_off(const char* why) {
    if (!g_off.exchange(true)) {
        LOGE("stations: actions off until the server, or the hosting player's game, restarts: %s", why);
        lower("stations.fill", KILN_CAP_REASON_FAULTED, why);
        lower("stations.control", KILN_CAP_REASON_FAULTED, why);
    }
}

bool guard(void (*fn)(void*), void* p, unsigned long* code) {
    __try {
        fn(p);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool here() {
    if (!g_api) return false;
    KilnSessionInfo si{};
    si.size = sizeof si;
    const KilnResult rc = g_api->session(&si);
    return equip_here(rc, si.state) == EquipHere::Yes && items_server_world() != 0;
}

void* singleton(const char* name) {
    const uintptr_t w = items_server_world();
    return w ? reinterpret_cast<void*>(g_api->singleton(w, str(name))) : nullptr;
}

// ================================================================================ bases (tick thread) ====

struct RawBase {
    uint64_t a_ptr, a_n, b_ptr, b_n;
    float min[3], max[3];
    uint32_t zone_mask;
};
struct RawBases {
    void* sys = nullptr;
    uint32_t count = 0;
    RawBase r[KILN_STATIONS_MAX_BASES];
    bool ok = false;
};

void read_bases_impl(void* p) {
    auto* o = static_cast<RawBases*>(p);
    const auto* s = static_cast<const uint8_t*>(o->sys);
    o->count = rd<uint32_t>(s, G.bases_count);
    if (o->count > KILN_STATIONS_MAX_BASES) return;
    for (uint32_t i = 0; i < o->count; ++i) {
        const int64_t off = static_cast<int64_t>(i) * G.stride;
        RawBase& b = o->r[i];
        b.a_ptr = rd<uint64_t>(s, G.stock_items + off);
        b.a_n = rd<uint64_t>(s, G.stock_items_count + off);
        b.b_ptr = rd<uint64_t>(s, G.stock_water + off);
        b.b_n = rd<uint64_t>(s, G.stock_water_count + off);
        for (int k = 0; k < 3; ++k) {
            b.min[k] = rd<float>(s, G.aabb_y - 4 + off + 4 * k);
            b.max[k] = rd<float>(s, G.aabb_y - 4 + 0x10 + off + 4 * k);
        }
        b.zone_mask = rd<uint32_t>(s, G.aabb_y + G.zone_mask_rel + off);
        if (b.a_n > kMaxStations || b.b_n > kMaxStations || (b.a_n && !b.a_ptr) || (b.b_n && !b.b_ptr)) return;
    }
    o->ok = true;
}

bool read_bases(RawBases* out) {
    out->sys = singleton("PlayerBasesSystemServer");
    if (!out->sys || !G.read) return false;
    unsigned long code = 0;
    return guard(&read_bases_impl, out, &code) && out->ok;
}

struct HitCall {
    void* sys;
    float pos[3];
    BaseHit hit;
};
void base_at_impl(void* p) {
    auto* h = static_cast<HitCall*>(p);
    G.base_at(&h->hit, h->sys, h->pos);
}
// The base (record index) a position is in, by the game's own lookup; -1: none.
int32_t base_index_at(void* sys, const KilnVec3& pos) {
    if (!sys || !G.base_at) return -1;
    HitCall h{sys, {static_cast<float>(pos.x), static_cast<float>(pos.y), static_cast<float>(pos.z)}, {}};
    h.hit.error = 0xff;
    unsigned long code = 0;
    if (!guard(&base_at_impl, &h, &code)) return -1;
    return h.hit.error == 0 && h.hit.index < KILN_STATIONS_MAX_BASES ? h.hit.index : -1;
}

bool position_of(KilnEntity e, KilnVec3* out) {
    return g_w && g_w->position(e, out) == KILN_OK;
}

void scan_altars(uint64_t step, void* sys) {
    if (step != 0 && g_altars_step != 0 && step - g_altars_step < kScanEvery && step >= g_altars_step) return;
    g_altars_step = step ? step : 1;
    g_altars.clear();
    if (!g_w) return;
    KilnEntity ids[kMaxAltars];
    uint32_t n = 0;
    if (g_w->find(H_BASE_ID, ids, kMaxAltars, &n) != KILN_OK && n == 0) return;
    n = std::min(n, kMaxAltars);
    for (uint32_t i = 0; i < n; ++i) {
        uint32_t size = 0;
        const auto* c = static_cast<const uint8_t*>(g_w->component(ids[i], H_BASE_ID, &size));
        if (!c || size < 16) continue;
        Altar a{};
        a.e = ids[i];
        std::memcpy(a.id.guid, c, 16);
        KilnVec3 p{};
        a.base = position_of(ids[i], &p) ? base_index_at(sys, p) : -1;
        g_altars.push_back(a);
    }
}

void fill_base(const RawBases& rb, uint32_t i, KilnStationBase* out) {
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    out->index = i;
    const RawBase& b = rb.r[i];
    KilnBaseId ids[kMaxAltars];
    uint32_t n = 0;
    for (const Altar& a : g_altars) {
        if (a.base != static_cast<int32_t>(i) || n >= kMaxAltars) continue;
        ids[n++] = a.id;
        if (out->altar_count < KILN_STATIONS_MAX_ALTARS) {
            out->altars[out->altar_count] = a.e;
            out->altar_ids[out->altar_count] = a.id;
        }
        ++out->altar_count;
    }
    out->altar_count = std::min<uint32_t>(out->altar_count, KILN_STATIONS_MAX_ALTARS);
    out->id = smallest_id(ids, n);
    out->storage_count = static_cast<uint32_t>(b.a_n);
    out->water_count = static_cast<uint32_t>(b.b_n);
    for (uint32_t m = b.zone_mask; m; m &= m - 1) ++out->zone_count;
    out->version = g_bases_version;
    out->min = KilnVec3{b.min[0], b.min[1], b.min[2]};
    out->max = KilnVec3{b.max[0], b.max[1], b.max[2]};
    uint32_t stations = 0;
    for (KilnEntity e : g_stations) {
        auto k = g_known.find(e);
        if (k != g_known.end() && k->second.snap.base == static_cast<int32_t>(i)) ++stations;
    }
    out->station_count = stations;
}

struct ListCopy {
    uint64_t ptr, n;
    KilnEntity* out;
    uint32_t cap, written;
};
void copy_list_impl(void* p) {
    auto* c = static_cast<ListCopy*>(p);
    const auto* ids = reinterpret_cast<const uint32_t*>(c->ptr);
    for (uint64_t i = 0; i < c->n && c->written < c->cap; ++i) c->out[c->written++] = ids[i];
}
// The game's list of a base (A: stock containers, B: water sources) copied out.
bool base_list(const RawBases& rb, uint32_t i, bool water, std::vector<KilnEntity>* out) {
    const RawBase& b = rb.r[i];
    const uint64_t n = water ? b.b_n : b.a_n;
    out->assign(static_cast<size_t>(n), 0);
    ListCopy c{water ? b.b_ptr : b.a_ptr, n, out->data(), static_cast<uint32_t>(n), 0};
    unsigned long code = 0;
    if (n && !guard(&copy_list_impl, &c, &code)) return false;
    out->resize(c.written);
    return true;
}

// kiln.items.inventory_kinds (GitHub issue #138): the game's own lists, no template names
bool base_stock_all(std::vector<KilnEntity>* out) {
    out->clear();
    RawBases rb{};
    if (!read_bases(&rb)) return false;
    std::vector<KilnEntity> one;
    for (uint32_t i = 0; i < rb.count; ++i) {
        if (!base_list(rb, i, false, &one)) return false;
        out->insert(out->end(), one.begin(), one.end());
    }
    std::sort(out->begin(), out->end());
    out->erase(std::unique(out->begin(), out->end()), out->end());
    return true;
}

// ================================================================================ recipes (tick thread) ====

void* recipes_registry() { return singleton("g38_items::RecipeRegistry"); }

struct RecipeCall {
    uint32_t id;
    void* registry;
    KilnStationRecipe* out;
    bool found;
};
void recipe_impl(void* p) {
    auto* c = static_cast<RecipeCall*>(p);
    const auto* r = static_cast<const uint8_t*>(G.recipe_find(c->id, c->registry));
    if (!r) return;
    c->found = true;
    KilnStationRecipe& o = *c->out;
    o.id = rd<uint32_t>(r, kRecipeId);
    o.workshop = rd<uint32_t>(r, kRecipeWorkshop);
    o.duration_ns = rd<int64_t>(r, G.recipe_duration);
    o.water_charges = rd<uint32_t>(r, G.recipe_water);
    o.knowledge_id = rd<uint32_t>(r, kRecipeKnowledge);
    o.knowledge_value = rd<uint32_t>(r, kRecipeKnowledge + 4);
    o.knowledge_compare = r[kRecipeKnowledge + 8];
    o.knowledge_type = r[kRecipeKnowledge + 9];
    o.knowledge_player_only = r[kRecipeKnowledge + 10];
    const int32_t in_rel = rd<int32_t>(r, kRecipeInputs);
    const uint32_t in_n = rd<uint32_t>(r, kRecipeInputs + 4);
    o.input_count = std::min(in_n, KILN_STATIONS_MAX_INPUTS);
    const uint8_t* in = r + kRecipeInputs + in_rel;
    for (uint32_t i = 0; i < o.input_count && in_rel; ++i) {
        const uint8_t* e = in + i * kInputStride;
        o.inputs[i].item = rd<uint32_t>(e, kInputItem);
        o.inputs[i].count = rd<uint32_t>(e, kInputCount);
        o.inputs[i].category = rd<uint32_t>(e, kInputCategory);
        o.inputs[i].category_count = rd<uint32_t>(e, kInputCategoryCount);
    }
    const int32_t out_rel = rd<int32_t>(r, kRecipeOutputs);
    const uint32_t out_n = rd<uint32_t>(r, kRecipeOutputs + 4);
    o.output_count = std::min(out_n, KILN_STATIONS_MAX_OUTPUTS);
    const uint8_t* outs = r + kRecipeOutputs + out_rel;
    for (uint32_t i = 0; i < o.output_count && out_rel; ++i) {
        o.outputs[i].item = rd<uint32_t>(outs + i * kOutputStride, kInputItem);
        o.outputs[i].count = rd<uint32_t>(outs + i * kOutputStride, kInputCount);
    }
}

KilnResult recipe_info(uint32_t id, KilnStationRecipe* out) {
    void* reg = recipes_registry();
    if (!reg || !G.recipe_find) return KILN_ERR_UNAVAILABLE;
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    RecipeCall c{id, reg, out, false};
    unsigned long code = 0;
    if (!guard(&recipe_impl, &c, &code)) return KILN_ERR_INTERNAL;
    return c.found ? KILN_OK : KILN_ERR_NOT_FOUND;
}

// The game's resolution of a recipe's inputs (category choices applied, counts scaled), into plain stacks.
struct ResolveVec {  // the game's DynamicArray as AutoTransferAction sets it up: {ptr, count, capacity, grow fn, alloc}
    uint64_t ptr, count, capacity, grow, alloc;
    uint8_t inline_[kRecipeVecInline * 24];
};
struct ResolveCall {
    const void* recipe;
    const KilnStationChoice* choices;
    void* registry;
    ResolveVec vec;
    KilnStationStack out[KILN_STATIONS_MAX_INPUTS];
    uint32_t n;
    bool too_many;
};
void resolve_impl(void* p) {
    auto* c = static_cast<ResolveCall*>(p);
    if (rd<uint32_t>(c->recipe, kRecipeInputs + 4) > kRecipeVecInline) {  // no grow function given: never overflow
        c->too_many = true;
        return;
    }
    c->vec.ptr = reinterpret_cast<uint64_t>(c->vec.inline_);
    c->vec.count = 0;
    c->vec.capacity = kRecipeVecInline;
    c->vec.grow = 0;
    c->vec.alloc = 0;
    alignas(8) uint8_t sel[32] = {};
    if (c->choices) std::memcpy(sel, c->choices, 32);
    G.resolve(c->recipe, &c->vec, sel, c->registry);
    c->n = static_cast<uint32_t>(std::min<uint64_t>(c->vec.count, kRecipeVecInline));
    for (uint32_t i = 0; i < c->n; ++i) {
        c->out[i].item = rd<uint32_t>(c->vec.inline_, i * 24 + 0x10);
        c->out[i].count = rd<uint32_t>(c->vec.inline_, i * 24 + 0x14);
    }
}
struct FindCall {
    uint32_t id;
    void* registry;
    const void* recipe;
};
void find_impl(void* p) {
    auto* f = static_cast<FindCall*>(p);
    f->recipe = G.recipe_find(f->id, f->registry);
}
const void* find_recipe(uint32_t id, void* reg) {
    FindCall f{id, reg, nullptr};
    unsigned long code = 0;
    if (!G.recipe_find || !reg || !guard(&find_impl, &f, &code)) return nullptr;
    return f.recipe;
}
// Tick thread or inside the program (the function reads the registry only).
bool resolve_inputs(const void* recipe, const KilnStationChoice* choices, void* reg, ResolveCall* c) {
    c->recipe = recipe;
    c->choices = choices;
    c->registry = reg;
    c->n = 0;
    c->too_many = false;
    unsigned long code = 0;
    return guard(&resolve_impl, c, &code) && !c->too_many;
}

// ================================================================================ stations (tick thread) ====

struct FsBytes {
    uint8_t b[0x68];
};
bool factory_of(KilnEntity e, FsBytes* out) {
    if (!g_w) return false;
    uint32_t size = 0;
    const void* p = g_w->component(e, H_FACTORY, &size);
    if (!p || size < static_cast<uint32_t>(G.fs_size)) return false;
    std::memcpy(out->b, p, sizeof out->b);
    return true;
}
bool is_factory(KilnEntity e) {
    FsBytes fs{};
    return H_FACTORY != 0 && factory_of(e, &fs);
}
uint32_t workshop_of(KilnEntity e) {
    uint32_t size = 0;
    const void* p = g_w ? g_w->component(e, H_CRAFTING, &size) : nullptr;
    return p && size >= kCraftingWorkshop + 4 ? rd<uint32_t>(p, kCraftingWorkshop) : 0;
}

Snap snap_of(const FsBytes& fs, uint32_t workshop, int32_t base, const KilnVec3& pos) {
    Snap s{};
    s.state = fs.b[kFsState];
    s.stopped = fs.b[G.fs_stopped];
    s.selected = rd<uint32_t>(fs.b, G.fs_next);
    s.running = rd<uint32_t>(fs.b, G.fs_running);
    s.workshop = workshop;
    s.base = base;
    s.pos = pos;
    return s;
}

// Counts per item in an entity's inventories (tick thread, kiln.items' read).
void count_items(KilnEntity e, std::unordered_map<uint32_t, uint64_t>* into) {
    static thread_local std::vector<KilnItemsSlot> slots;
    slots.resize(8 * KILN_ITEMS_MAX_INVENTORIES);
    ReadOut ro{};
    ro.info.size = sizeof ro.info;
    ro.slots = slots.data();
    ro.capacity = static_cast<uint32_t>(slots.size());
    unsigned long code = 0;
    if (!read_inventory(items_server_world(), singleton("g38_items::ItemRegistry"), e, &ro, &code) || !ro.found) return;
    for (uint32_t i = 0; i < ro.written; ++i)
        if (slots[i].item && slots[i].count) (*into)[slots[i].item] += slots[i].count;
}

// A water container's water (Fuel units); false when it holds none or has no Fuel. Tick thread, or inside the program
// (which declares Fuel: no other program writes it meanwhile).
bool water_of(KilnEntity e, void* attributes, int32_t* cur, int32_t* max) {
    *cur = *max = 0;
    bool found = false;
    unsigned long code = 0;
    return attributes && read_fuel(items_server_world(), attributes, e, cur, max, &found, &code) && found;
}
uint64_t base_water(const RawBases& rb, int32_t base, void* attributes) {
    std::vector<KilnEntity> list;
    uint64_t sum = 0;
    if (base < 0 || !base_list(rb, static_cast<uint32_t>(base), true, &list)) return 0;
    for (KilnEntity c : list) {
        int32_t cur = 0, max = 0;
        if (water_of(c, attributes, &cur, &max) && cur > 0) sum += static_cast<uint64_t>(cur);
    }
    return sum;
}
// The id of base `index` from the altars of the last scan (zero: none).
KilnBaseId base_id_of(int32_t index) {
    KilnBaseId ids[kMaxAltars];
    uint32_t n = 0;
    for (const Altar& a : g_altars)
        if (a.base == index && n < kMaxAltars) ids[n++] = a.id;
    return smallest_id(ids, n);
}

int64_t scaled(int64_t dur_ns) {
    uint32_t bits = g_speed_bits.load(std::memory_order_relaxed);
    float f;
    std::memcpy(&f, &bits, 4);
    if (!(f > 0.0f)) f = 1.0f;
    // as factory_station: double(ns) * 1e-9 -> float, times the factor, -> double * 1e9 -> int
    const float sec = static_cast<float>(static_cast<double>(dur_ns) * 1e-9);
    return static_cast<int64_t>(static_cast<double>(sec * f) * 1e9);
}

// ---- station outputs as crafting stock (GitHub #68) ----
// A station's recipes as the classification needs them (selected and running); defined after the category reads.
bool source_recipes(const FsBytes& fs, void* reg, SourceRecipes* out);

std::vector<KilnEntity> sources_copy() {
    std::lock_guard lock(g_mu);
    return g_sources;
}

// What opted-in stations of base `base` lend of each of `items` for a fill of `target`: per item the count and the
// stations, in entity order. `factory`, `base_of` and `count` are the caller's reads (the tick thread's, or the program's).
struct Lent {
    std::unordered_map<uint32_t, uint64_t> count;
    std::unordered_map<uint32_t, std::vector<uint32_t>> by;
};
template <class Factory, class BaseOf, class Count>
void lenders(KilnEntity target, int32_t base, const std::vector<uint32_t>& items, void* reg, Factory factory, BaseOf base_of,
             Count count, Lent* out) {
    std::vector<SourceCandidate> cands;
    for (KilnEntity e : sources_copy()) {
        SourceCandidate c{e, -1, false};
        FsBytes fs{};
        if (e != target && factory(e, &fs)) {
            c.station = true;
            c.base = base_of(e);
        }
        cands.push_back(c);
    }
    for (uint32_t e : eligible_sources(cands, target, base)) {
        FsBytes fs{};
        SourceRecipes sr;
        if (!factory(e, &fs) || !source_recipes(fs, reg, &sr)) continue;
        std::unordered_map<uint32_t, uint64_t> inv;
        bool counted = false;
        for (uint32_t item : items) {
            if (!lends_item(sr, item)) continue;
            if (!counted) {
                count(e, &inv);
                counted = true;
            }
            const uint64_t n = inv[item];
            if (!n) continue;
            out->count[item] += n;
            out->by[item].push_back(e);
        }
    }
}

// ---- crafting payment (docs/re/stations.md §10) ----
// InventoryPay builds one stock list and walks it for every cost line, so a station cannot be added the way a fill
// adds one (a station in that list would pay every line, queued inputs included). For the recipe function's call only,
// on the server world's own transaction, when the game's own sources are short and opted-in outputs cover every line's
// shortfall, Kiln pays the whole recipe itself: the owner and the Magic chests first (in the game's order), then each
// line's lending stations, through the game's PayMaterial, inside the transaction RecipeApply already opened. Anything
// else is the game's own payment, a shortfall the outputs cannot cover included. The decision and the loop are
// stations_logic.h's craft_order / craft_lenders / craft_pay (host test).
//
// Where it runs: inside the game's craft program (CraftRecipe -> RecipeApply -> InventoryPay), on the tick thread of a
// dedicated server, possibly on a game worker thread in a hosted game. So nothing here goes through kiln.world (tick
// thread only) or the core's singleton lookup: the bases system is the transaction's own (tx+0x68, compared with the
// server's, which the tick thread caches), inventories are counted through the transaction's own accessors
// (access_from_tx: the view PayMaterial itself takes from), and a lender's FactoryStation and place are SEH-guarded
// GetComponent reads. Every program that writes FactoryStation (the game's factory_station and player_crafting, Kiln's
// kiln_items_stations) also writes Inventory, which the craft program writes too, so none runs while it does.

// The game's call that ends at ret as the exe file has it calls target. Another module may have redirected the live call
// (Building Tools 0.34.0 rewrites the blueprint program's payment call, and for its magic chests of all bases the build
// cursor's stock count); kiln.hooks' read_original gives the file's bytes, else the live ones are read.
bool call_matches(uintptr_t ret, uintptr_t target);
bool call_matches_original(uintptr_t ret, uintptr_t target) {
    const auto* h = kiln::hooks_v2(g_api);
    uint8_t call[5] = {};
    if (ret >= 5 && target && h && h->header.size >= offsetof(KilnHooksApi, read_original) + sizeof(h->read_original) &&
        h->read_original && h->read_original(ret - 5, call, sizeof call) == KILN_OK)
        return kiln::call_target(call, ret) == target;
    return call_matches(ret, target);
}
bool call_matches(uintptr_t ret, uintptr_t target) {
    __try {
        if (!ret || !target) return false;
        const auto* p = reinterpret_cast<const uint8_t*>(ret);
        int32_t rel = 0;
        std::memcpy(&rel, p - 4, 4);
        return p[-5] == 0xe8 && static_cast<uintptr_t>(static_cast<intptr_t>(ret) + rel) == target;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

struct OnePay { void* tx; uint32_t entity, item, count; PayOut out; };
void one_pay_impl(void* p) {
    auto* j = static_cast<OnePay*>(p);
    j->out = {};
    if (PayOut* r = G.pay(&j->out, j->tx, j->entity, j->item, j->count); r && r != &j->out) j->out = *r;
}
// False: the call faulted (the transaction may already hold a take). *refused: the game's error byte (0: none).
bool pay_one(void* tx, uint32_t entity, uint32_t item, uint32_t* left, uint8_t* refused = nullptr) {
    if (refused) *refused = 0;
    if (!*left || !entity) return true;
    OnePay j{tx, entity, item, *left, {}};
    unsigned long code = 0;
    if (!guard(&one_pay_impl, &j, &code)) return false;
    // The game ignores the out value when the error byte is set and tries the next source (InventoryPay).
    if (j.out.error == 0) *left = j.out.left;
    else if (refused) *refused = j.out.error;
    return true;
}

// The words of the game's transaction the payment reads (docs/re/stations.md §10.4): BaseStockForPlayer's two bases-system
// words (+0x68, used when set, else +0x70: the server's craft transaction leaves +0x68 zero), the position (+0x80,
// 12 bytes) and the stock mode (+0xb1).
struct TxHead {
    uint8_t* tx;
    uint64_t bases, arg4;
    float pos[3];
    uint8_t mode;
};
void tx_head_impl(void* p) {
    auto* h = static_cast<TxHead*>(p);
    std::memcpy(&h->bases, h->tx + 0x68, 8);
    std::memcpy(&h->arg4, h->tx + 0x70, 8);
    std::memcpy(h->pos, h->tx + 0x80, 12);
    h->mode = h->tx[0xb1];
}
// The transaction's first 0x20 words (a diagnostic line when the server-world test refuses one).
struct TxWords {
    const uint8_t* tx;
    uint64_t w[0x20];
};
void tx_words_impl(void* p) {
    auto* t = static_cast<TxWords*>(p);
    std::memcpy(t->w, t->tx, sizeof t->w);
}
struct StockJob {
    const TxHead* head;
    uint32_t player;
    StockErr err;
    StockLists stock;
};
void stock_job_impl(void* p) {
    auto* j = static_cast<StockJob*>(p);
    alignas(8) uint8_t pos[16] = {};
    std::memcpy(pos, j->head->pos, 12);
    G.base_stock(&j->err, &j->stock, j->head->bases, j->head->arg4, pos, j->player);
}
// One base record's stock list in the bases system `sys`: the arrays BaseStockForPlayer returns after its permission
// test (S 0x208251). Only the lab's stand-in reads it (KILN_STATIONS_CRAFT_LAB, below).
struct ListAt {
    const uint8_t* sys;
    int32_t index;
    uint64_t ptr, n;
};
void list_at_impl(void* p) {
    auto* l = static_cast<ListAt*>(p);
    const int64_t off = static_cast<int64_t>(l->index) * G.stride;
    l->ptr = rd<uint64_t>(l->sys, G.stock_items + off);
    l->n = rd<uint64_t>(l->sys, G.stock_items_count + off);
}
struct ListCopyJob {
    const uint32_t* src;
    uint32_t* dst;
    uint64_t n;
};
void list_copy_impl(void* p) {
    auto* c = static_cast<ListCopyJob*>(p);
    if (c->n) std::memcpy(c->dst, c->src, c->n * 4);
}
// The cost lines (24 bytes each: item +0x10, count +0x14) and InventoryPay's fifth argument (when set, every line's item).
struct RawCosts {
    const uint8_t* costs;
    const uint8_t* extra;
    uint32_t n;
    uint32_t item[32], count[32];
    uint32_t forced;
    uint64_t lines = 0;  // how many the list holds (more than 32: n stays 0, the game's payment)
};
void costs_impl(void* p) {
    auto* c = static_cast<RawCosts*>(p);
    struct {
        const uint8_t* ptr;
        uint64_t n;
    } s{};
    std::memcpy(&s, c->costs, sizeof s);
    c->lines = s.n;
    if (!s.ptr || s.n == 0 || s.n > 32) return;
    for (uint64_t i = 0; i < s.n; ++i) {
        std::memcpy(&c->item[i], s.ptr + i * 24 + 0x10, 4);
        std::memcpy(&c->count[i], s.ptr + i * 24 + 0x14, 4);
    }
    if (c->extra) std::memcpy(&c->forced, c->extra, 4);
    c->n = static_cast<uint32_t>(s.n);
}
// An entity's component through the game's GetComponent, guarded: a plain read, as read_inventory makes, usable off the
// tick thread (kiln.world's reads are not).
struct CompRead {
    uintptr_t world;
    uint32_t hash, entity, want;
    uint8_t buf[0x68];
    bool ok;
};
void comp_read_impl(void* p) {
    auto* c = static_cast<CompRead*>(p);
    PtrSize ps{};
    E.get_component(&ps, c->world, c->hash, c->entity);
    if (!ps.ptr || ps.size < c->want) return;
    std::memcpy(c->buf, ps.ptr, c->want);
    c->ok = true;
}
bool comp_read(uintptr_t world, uint32_t hash, uint32_t entity, uint32_t want, uint8_t* out) {
    if (!world || !E.get_component || !entity || want > sizeof(CompRead::buf)) return false;
    CompRead c{world, hash, entity, want, {}, false};
    unsigned long code = 0;
    if (!guard(&comp_read_impl, &c, &code) || !c.ok) return false;
    std::memcpy(out, c.buf, want);
    return true;
}
// Counts per item in an entity's inventories through the accessors of `a` (defined with the program's reads).
void count_pages(const Access& a, KilnEntity e, std::unordered_map<uint32_t, uint64_t>* into);
// A player character (its SlotSelection has an action bar): kiln.items' guarded read through the transaction's accessors.
bool is_character(const Access& a, KilnEntity e) {
    PlayerGuard g;
    player_guard(a, e, &g);
    return g.is_player;
}

// BasicPlayerInfos is the server-wide per-player right, not a per-base ownership check (docs/re/stations.md §10.1).
// These offsets are pinned by crafting.sources in sigdb; unknown builds never enable this path.
struct SourcePlayerJob { void* bases; uint32_t player, session; bool ok; };
void source_player_impl(void* p) {
    auto& j=*static_cast<SourcePlayerJob*>(p);
    if(!j.bases || !j.player || j.player>kMaxPlayerEntity) return;
    const auto* infos=rd<const uint8_t*>(j.bases,0x10);
    if(!infos)return;
    const auto* record=infos+static_cast<size_t>(j.player-1)*0xa0;
    if(rd<uint32_t>(record,0)!=j.player)return;
    j.session=rd<uint32_t>(record,4); // players/InfoHandle: same id, different login must not inherit links
    j.ok=true;
}
bool source_player(void* bases,uint32_t player,uint32_t* session) {
    SourcePlayerJob job{bases,player,0,false}; unsigned long fault=0;
    if(!G.registered_ok || !guard(&source_player_impl,&job,&fault) || !job.ok)return false;
    *session=job.session;return true;
}

// Linked sources must be ordinary storage: PayMaterial cannot distinguish a production station's queued inputs
// from its finished outputs. Query the component directly (not kiln.world), including on the payment worker thread.
struct SourceContainerJob { uint32_t container; bool ok; };
void source_container_impl(void* p) {
    auto& j=*static_cast<SourceContainerJob*>(p);
    const uintptr_t world=items_server_world();
    if(!world || !E.get_component || !H_FACTORY)return;
    PtrSize factory{};
    E.get_component(&factory,world,H_FACTORY,j.container);
    if(factory.ptr)return;
    bool container=false;unsigned long fault=0;
    j.ok=inventory_matches(world,j.container,KILN_ITEMS_FIND_CONTAINERS,&container,&fault) && container;
}
bool source_container(uint32_t container) {
    SourceContainerJob job{container,false};unsigned long fault=0;
    return guard(&source_container_impl,&job,&fault) && job.ok;
}

struct SourceAccessJob { void* bases; uint32_t player, container; bool ok; };
void source_access_impl(void* p) {
    auto& j = *static_cast<SourceAccessJob*>(p);
    if (!j.bases || !j.player || j.player > kMaxPlayerEntity || j.container <= kMaxPlayerEntity) return;
    const auto* infos = rd<const uint8_t*>(j.bases, 0x10);
    if (!infos) return;
    const auto* record = infos + static_cast<size_t>(j.player - 1) * 0xa0;
    if (rd<uint32_t>(record,0) != j.player || !(record[0x9d] & 2)) return;
    j.ok = source_identity_ok(j.player,j.container,false,source_container(j.container));
}
bool source_access(void* bases, uint32_t player, uint32_t container) {
    SourceAccessJob job{bases,player,container,false};
    unsigned long fault = 0;
    return G.registered_ok && guard(&source_access_impl,&job,&fault) && job.ok;
}

// What the craft payment did, for kiln.log and the lab (kiln_stations_craft_stats, in this order).
struct CraftStats {
    std::atomic<uint32_t> calls{0};         // InventoryPay called from RecipeApply (its return address is RecipePaid)
    std::atomic<uint32_t> idle{0};          // nothing opted in: the game's payment, untouched
    std::atomic<uint32_t> other_world{0};   // not the server world's transaction (a hosting client's station-screen dry run)
    std::atomic<uint32_t> mode0{0};         // stock mode 0, no base stock (a factory's own craft)
    std::atomic<uint32_t> no_access{0};     // BaseStockForPlayer refused: no CanAccessInventories (0x16), no base (0x1f)
    std::atomic<uint32_t> considered{0};    // counted: the payer's base, its chests and its opted-in stations were read
    std::atomic<uint32_t> covered{0};       // the bag and the chests cover it: the game pays
    std::atomic<uint32_t> short_{0};        // this base's opted-in outputs cannot cover the shortfall: the game refuses it
    std::atomic<uint32_t> paid{0};          // Kiln paid it, station outputs last
    std::atomic<uint32_t> from_outputs{0};  // items those payments took from station outputs
    std::atomic<uint32_t> rollback{0};      // a take faulted, or a line stayed short after a take: 4, the craft rolled back
    std::atomic<uint32_t> skipped{0};       // the game's for another reason (unreadable, an item twice, a stock list too long)
    std::atomic<uint32_t> lab{0};           // KILN_STATIONS_CRAFT_LAB's stand-in stock list was used
    std::atomic<uint32_t> respec{0};        // GitHub #150: calls of the respec payer (respec/RespecPaid), whatever came of them
};
CraftStats CS;
constexpr uint32_t kCraftStatCount = 14;
bool g_craft_lab = false;  // KILN_STATIONS_CRAFT_LAB=1 (stations_resolve): labs only, never a real server
// The server world's bases system and recipe registry, cached by the tick thread (Pre) for the payment.
std::atomic<void*> g_srv_bases{nullptr}, g_srv_recipes{nullptr};
// The client world's recipe registry, cached on the client world's thread (its Pre) for the crafting menu's count.
std::atomic<void*> g_client_recipes{nullptr};

// At most `budget` lines of one kind, then one every 10 s with how many were held back.
struct LogLimit {
    std::atomic<int> budget;
    std::atomic<int64_t> last{0};
    std::atomic<uint32_t> held{0};
    explicit LogLimit(int b) : budget(b) {}
    // 0: drop. Otherwise 1 + how many were held back since the last line.
    uint32_t take() {
        int b = budget.load();
        while (b > 0)
            if (budget.compare_exchange_weak(b, b - 1)) return 1;
        const int64_t now = static_cast<int64_t>(GetTickCount64());
        int64_t l = last.load();
        if (now - l >= 10000 && last.compare_exchange_strong(l, now)) return 1 + held.exchange(0);
        held.fetch_add(1);
        return 0;
    }
};
LogLimit g_log_left{6}, g_log_paid{20}, g_log_refused{12}, g_log_raised{12}, g_log_stock{8};
// Kept apart (GitHub #132 follow-up): on a hosting client the client world's own dry runs (RecipeApply for every recipe a
// crafting menu counts) reach the payment too; they used up the lines that would say why the server's craft was left to
// the game. g_log_tx: the server world's transaction refused; g_log_craft: one line per player craft (craft_done_impl).
LogLimit g_log_dry{4}, g_log_tx{8}, g_log_craft{24};

// GitHub #132 (0.2.0-dev.31.12): a block placement's payment from the player's building links (build section below).
// t_build_pay: set by the RawHook on ApplyMaterials for the blueprint program's own call, read by craft_pay_impl.
struct BuildSites {
    uintptr_t cursor_ret = 0, stock_fn = 0, apply = 0, blueprint_ret = 0, paid = 0, pay_fn = 0;
};
BuildSites g_build;
thread_local bool t_build_pay = false;
LogLimit g_log_build_pay{16};
std::atomic<bool> g_build_dirty{true};  // a registration changed: the server's count of building links is read again

// GitHub #150 (0.2.0-dev.31.18): the skill tree's paid reset and a node's unlearn from the player's respec links (respec
// section below). Both pay through the respec payer's one InventoryPay call (respec/RespecPaid, its return address), a
// stock-mode-2 transaction (the bag, then the Magic chests of the player's base) with one cost line of the respec item.
struct RespecSites {
    uintptr_t payer = 0, paid = 0, pay_fn = 0;                        // the server half (both executables)
    uintptr_t node_ret = 0, altar_ret = 0, header_ret = 0, avail_fn = 0;  // a client's screens
    uint32_t item = 0;                                               // respec/item, the payer's own constant (Runes)
    int64_t ui_item = 0, ui_cost = 0;                                // G38UiPlayData's respec item and cost (a client)
};
RespecSites g_respec;
bool g_respec_feat = false;                 // crafting.respec_sources resolved (stations_resolve)
std::atomic<bool> g_respec_off{false};      // a stand-down: a foreign patch, a failed hook, a fault, no payment hook
std::atomic<bool> g_respec_ready{false};    // the server half's code check passed (respec_server_pre): links may pay
LogLimit g_log_respec_pay{24}, g_log_respec_world{2};

// Labs only (KILN_STATIONS_CRAFT_LAB=1, as for the craft lab): a lab has no logged-in player, so a spawned player character
// (an id outside the player range, never a real player) may have respec links of its own, set through the private
// kiln_stations_lab_respec_link below. They pay only its respec (the respec branch), with the container checks of a real
// link and no player record (the character has none). Never on a real server: the switch is off without the variable,
// which kiln-items reads once at its start, before any mod runs. Review of #150: only a dedicated server's process (never
// a client's, so never a hosted world), at most kMaxRespecLab links in all, each named in kiln.log.
std::mutex g_respec_lab_mu;
std::vector<std::pair<uint32_t, uint32_t>> g_respec_lab;  // (character, container), g_respec_lab_mu
constexpr size_t kMaxRespecLab = 16;
bool respec_lab_on() { return g_craft_lab && g_api && g_api->side() == KILN_SIDE_SERVER; }
std::vector<CraftSource> respec_lab_sources(uint32_t character) {
    std::vector<CraftSource> out;
    std::lock_guard lock(g_respec_lab_mu);
    for (const auto& [who, container] : g_respec_lab)
        if (who == character) {
            CraftSource s;
            s.player = who, s.container = container, s.respec = true;
            out.push_back(std::move(s));
        }
    return out;
}
bool respec_lab_any() {
    std::lock_guard lock(g_respec_lab_mu);
    return !g_respec_lab.empty();
}

// The respec branch of the payment adapter (any thread: the game's player_skilltree, a hosting client's game thread):
// false, with a kiln.log line, while the server half may not pay from links (its code check has not run yet, or it stood
// down). The game then pays as it always does: the bag and the Magic chests of the player's base, or refuses.
bool respec_may_pay(uint32_t owner) {
    const char* const why = g_respec_off.load(std::memory_order_relaxed)
                                ? "respec from linked sources stood down on this build (an earlier respec: line says why)"
                            : !g_respec_ready.load(std::memory_order_acquire)
                                ? "the server half's code check has not run yet (it runs on the next server tick)"
                                : nullptr;
    if (!why) return true;
    if (const uint32_t n = g_log_respec_pay.take())
        LOGI("respec: player %u's skill respec left to the game: %s (the bag and the Magic chests pay)%s", owner, why,
             n > 1 ? " (more held back)" : "");
    return false;
}

// What one payment adapter call is (craft_pay_impl): a recipe's craft, a block placement (GitHub #132), a respec (#150).
enum PayKind : int { kPayCraft = 0, kPayBuild = 1, kPayRespec = 2 };
const char* pay_noun(int kind) { return kind == kPayRespec ? "skill respec" : kind == kPayBuild ? "block placement" : "craft"; }
LogLimit& pay_log(int kind, LogLimit& craft) { return kind == kPayRespec ? g_log_respec_pay : kind == kPayBuild ? g_log_build_pay : craft; }

void left_to_game(const char* why, unsigned mode, unsigned err, uint32_t owner, int kind = kPayCraft) {
    trace(kTraceLeft, 0, why);
    if (const uint32_t n = pay_log(kind, g_log_left).take())
        LOGI("%s: %s payment left to the game (%s; mode %u, stock error %u, payer %u)%s", kind == kPayRespec ? "respec" : "stations",
             pay_noun(kind), why, mode, err, owner, n > 1 ? " (more held back)" : "");
}

// 1: this call was paid here (*code is 0 or 4). 0: the game's InventoryPay should run.
int craft_pay_impl(void* tx, void* costs, uint32_t owner, uint32_t multiplier, void* extra, uintptr_t ret, uint32_t* code) {
    if (!G.craft_ok || g_off.load() || !tx || !costs || !code || !multiplier) return 0;
    // ret is rules/RecipePaid, the return address: call_matches(ret, pay) checked at resolve that the instruction there
    // calls InventoryPay; comparing ret with RecipePaid is the runtime test. The storage upgrade and the alternate payer
    // (RecipePayAlt) return elsewhere and stay the game's.
    // GitHub #132: a block placement (ApplyMaterials' take, scoped to the blueprint program by hk_build_apply) pays from the
    // player's building links only, never from station outputs; otherwise the same adapter as a craft's.
    const bool build = g_build.paid && ret == g_build.paid && t_build_pay;
    // GitHub #150: the respec payer's call (the skill tree's paid reset or a node's unlearn) pays from the player's respec
    // links only, never from station outputs, after the bag and the Magic chests (the game's stock mode 2)
    const bool respec = !build && g_respec.paid && ret == g_respec.paid;
    if (ret != G.recipe_paid && !build && !respec) {
        trace(kTraceOtherPayer);
        return 0;
    }
    const int kind = respec ? kPayRespec : build ? kPayBuild : kPayCraft;
    if (respec) CS.respec.fetch_add(1, std::memory_order_relaxed);
    const bool linked_only = kind != kPayCraft;  // one purpose's links: no station outputs, the transaction's reads only
    const char* const what = respec ? "a skill respec" : build ? "a block placement" : "a craft";
    const char* const tag = respec ? "respec" : "stations";  // kiln.log's prefix: respec lines read as the locks: lines do
    CS.calls.fetch_add(1, std::memory_order_relaxed);
    auto registered = G.registered_ok ? craft_sources_copy(owner) : std::vector<CraftSource>{};
    if (linked_only)
        registered.erase(std::remove_if(registered.begin(), registered.end(),
                                        [&](const CraftSource& s) { return respec ? !s.respec : !s.building; }),
                         registered.end());
    // labs only: a spawned character's lab links (respec_lab_sources); a real player's links are the registry's
    const bool lab_payer = respec && owner > kMaxPlayerEntity && respec_lab_on();
    if (lab_payer) registered = respec_lab_sources(owner);
    if (respec && !registered.empty() && !respec_may_pay(owner)) return 0;  // a stand-down: the game's (bag and Magic chests)
    {
        std::lock_guard lock(g_mu);
        if ((linked_only || g_sources.empty()) && registered.empty()) {
            CS.idle.fetch_add(1, std::memory_order_relaxed);
            // links of this payer that craft_sources_copy left out: another login's, or an unreadable player record
            trace(t_trace.active && G.registered_ok && !g_registered.copy(owner).empty() ? kTraceDropped : kTraceIdle);
            return 0;
        }
    }
    // The server world's own transaction only. A hosting client's station screen runs RecipeApply as a rolled-back dry
    // run on a client-world transaction (C 0x2be03a): its bases word is not the server's, and access_from_tx refuses a
    // storage that is not the server world's.
    void* const srv_bases = g_srv_bases.load(std::memory_order_acquire);
    TxHead head{static_cast<uint8_t*>(tx), 0, 0, {}, 0};
    unsigned long fault = 0;
    if (!guard(&tx_head_impl, &head, &fault)) {
        CS.skipped.fetch_add(1, std::memory_order_relaxed);
        left_to_game("the transaction could not be read", 0, 0, owner, kind);
        return 0;
    }
    if (head.mode == 0 && registered.empty()) {  // a factory never has player-bound registrations
        CS.mode0.fetch_add(1, std::memory_order_relaxed);
        trace(kTraceLeft, 0, "stock mode 0 (the payer alone) and nothing linked to the payer");
        return 0;
    }
    Access a{};
    const uint64_t used = head.bases ? head.bases : head.arg4;  // the bases system BaseStockForPlayer reads (S 0x208281)
    const bool same_bases = srv_bases && (used == reinterpret_cast<uint64_t>(srv_bases) ||
        (head.mode == 0 && !used && !registered.empty()));
    // GitHub #148: the blueprint program's transaction has no Fuel accessor (player_building_blueprints leaves its setup's
    // +0x70 zero, S 0xc4a57 / C 0x2d8ab7, which InventoryTxInit copies to the transaction's Fuel word; docs/re/stations.md
    // section 13), so the full check refused every placement and the game paid from the bag and the base alone. A
    // placement's payment reads only InventorySetup and Inventory (count_pages, setup_flags) and takes through the game's
    // PayMaterial: it is checked by those two, as a lock's payment is.
    // GitHub #150: the respec payer's transaction is set up the same way (no Fuel accessor either, S 0xb9230)
    const bool accessors = same_bases && (linked_only ? access_from_tx_reads(tx, &a) : access_from_tx(tx, &a));
    if (respec && !same_bases) {
        // GitHub #150: a hosting client's prediction world runs the same payer on its own transaction; only the server
        // world's payment counts (the server's state then reaches the prediction)
        CS.other_world.fetch_add(1, std::memory_order_relaxed);
        trace(kTraceOtherWorld);
        if (const uint32_t n = g_log_respec_world.take())
            LOGI("respec: a skill respec in another world (a hosting client's prediction world) is the game's own; the server "
                 "world's payment counts the respec links (payer %u)%s", owner, n > 1 ? " (more held back)" : "");
        return 0;
    }
    if (!same_bases || !accessors) {
        CS.other_world.fetch_add(1, std::memory_order_relaxed);
        // Another world's transaction (a hosting client's own dry runs) and the server world's transaction whose accessors
        // did not check out are told apart, each with its own lines: the first are many and expected.
        trace(same_bases ? kTraceTxRefused : kTraceOtherWorld);
        if (same_bases) {
            if (const uint32_t n = g_log_tx.take())
                LOGW("%s: the server world's %s transaction did not check out (its accessors): its payment is the "
                     "game's, crafting sources not asked (mode %u, payer %u)%s", tag, pay_noun(kind), head.mode, owner,
                     n > 1 ? " (more held back)" : "");
        } else if (const uint32_t n = g_log_dry.take()) {
            TxWords w{static_cast<const uint8_t*>(tx), {}};
            guard(&tx_words_impl, &w, &fault);
            LOGI("%s: %s payment left to the game (not the server world's transaction: bases words %p / %p, the "
                 "server's %p, accessors %s; words 0:%p 1:%p 3:%p 4:%p 5:%p 7:%p 12:%p 14:%p 1d:%p; mode %u, payer %u)%s",
                 tag, pay_noun(kind), reinterpret_cast<void*>(head.bases), reinterpret_cast<void*>(head.arg4), srv_bases,
                 same_bases ? "refused" : "not read",
                 reinterpret_cast<void*>(w.w[0]), reinterpret_cast<void*>(w.w[1]), reinterpret_cast<void*>(w.w[3]),
                 reinterpret_cast<void*>(w.w[4]), reinterpret_cast<void*>(w.w[5]), reinterpret_cast<void*>(w.w[7]),
                 reinterpret_cast<void*>(w.w[0x12]), reinterpret_cast<void*>(w.w[0x14]), reinterpret_cast<void*>(w.w[0x1d]),
                 head.mode, owner, n > 1 ? " (more held back)" : "");
        }
        return 0;
    }
    StockJob stock{&head, owner, {}, {}};
    if (head.mode != 0 && !guard(&stock_job_impl, &stock, &fault)) {
        CS.skipped.fetch_add(1, std::memory_order_relaxed);
        left_to_game("the base stock call faulted", head.mode, 0, owner, kind);
        return 0;
    }
    const int32_t base = base_index_at(srv_bases, KilnVec3{head.pos[0], head.pos[1], head.pos[2]});
    uint64_t list_ptr = reinterpret_cast<uint64_t>(stock.stock.items), list_n = stock.stock.nitems;
    if (stock.err.error) {
        // The game's own permission and base test (0x16: no CanAccessInventories, 0x1f: no base). A lab has no logged-in
        // player: with KILN_STATIONS_CRAFT_LAB=1 a spawned player character (never an id of the player range, so never a
        // real player) gets the base's list as the game hands it to a player with the right.
        bool lab = false;
        if (g_craft_lab && stock.err.error == 0x16 && owner > kMaxPlayerEntity && base >= 0 && is_character(a, owner)) {
            ListAt l{static_cast<const uint8_t*>(srv_bases), base, 0, 0};
            if (guard(&list_at_impl, &l, &fault) && (l.ptr || !l.n)) {
                list_ptr = l.ptr, list_n = l.n, lab = true;
                CS.lab.fetch_add(1, std::memory_order_relaxed);
            }
        }
        if (!lab && registered.empty()) {
            CS.no_access.fetch_add(1, std::memory_order_relaxed);
            left_to_game(stock.err.error == 0x16 ? "the payer may not access the base's inventories" : "the payer stands in no base",
                         head.mode, stock.err.error, owner, kind);
            return 0;
        }
    }
    auto skip = [&](const char* why) {
        CS.skipped.fetch_add(1, std::memory_order_relaxed);
        left_to_game(why, head.mode, stock.err.error, owner, kind);
        return 0;
    };
    if (base < 0 && registered.empty()) return skip("the transaction's position is in no base");
    // A refused base list is empty in the game too. Registered sources need their independent permission check.
    if (head.mode == 0 || (stock.err.error && !(g_craft_lab && owner > kMaxPlayerEntity))) list_ptr = list_n = 0;
    RawCosts rc{static_cast<const uint8_t*>(costs), static_cast<const uint8_t*>(extra), 0, {}, {}, 0};
    // review of #148: Building Tools' area actions charge up to 64 merged lines; more than 32 stay the game's (bag and base)
    if (!guard(&costs_impl, &rc, &fault) || rc.n == 0)
        return skip(rc.lines > 32 ? "it names more than 32 cost lines, which Kiln does not pay" : "the cost lines could not be read");
    std::vector<CraftCost> lines;
    std::vector<uint32_t> items;
    for (uint32_t i = 0; i < rc.n; ++i) {
        const uint32_t item = extra ? rc.forced : rc.item[i];
        if (!item || !rc.count[i]) continue;
        if (std::find(items.begin(), items.end(), item) != items.end()) return skip("the recipe names one item twice");
        items.push_back(item);
        lines.push_back(CraftCost{item, static_cast<uint32_t>(std::min<uint64_t>(static_cast<uint64_t>(rc.count[i]) * multiplier, 0xffffffffu)), 0, 0});
    }
    if (lines.empty()) {
        trace(kTraceLeft, 0, "the recipe's cost lines name nothing to pay");
        return 0;
    }
    if (list_n > kMaxCraftStock || (list_n && !list_ptr)) return skip("the base's stock list is longer than Kiln counts");
    std::vector<uint32_t> list(static_cast<size_t>(list_n));
    ListCopyJob lc{reinterpret_cast<const uint32_t*>(list_ptr), list.data(), list_n};
    if (!guard(&list_copy_impl, &lc, &fault)) return skip("the base's stock list could not be read");
    std::vector<uint32_t> order;
    if (!craft_order(head.mode, owner, list.data(), list.size(), &order)) return skip("an unknown stock mode");

    // counts through the transaction's accessors (the view PayMaterial takes from), each entity read once
    std::unordered_map<uint32_t, std::unordered_map<uint32_t, uint64_t>> held;
    auto have = [&](uint32_t e, uint32_t item) -> uint64_t {
        auto it = held.find(e);
        if (it == held.end()) {
            std::unordered_map<uint32_t, uint64_t> m;
            count_pages(a, e, &m);
            it = held.emplace(e, std::move(m)).first;
        }
        const auto n = it->second.find(item);
        return n == it->second.end() ? 0 : n->second;
    };
    // the opted-in stations as they are now: still a station (its FactoryStation, its recipes), and where
    const uint32_t exclude = t_craft_station ? t_craft_station : (owner > kMaxPlayerEntity ? owner : 0u);
    const uintptr_t world = items_server_world();
    void* const reg = g_srv_recipes.load(std::memory_order_acquire);
    std::vector<CraftLender> cands;
    for (KilnEntity e : (head.mode != 0 && !stock.err.error && base >= 0 && !linked_only ? sources_copy() : std::vector<KilnEntity>{})) {
        CraftLender l;
        l.entity = e;
        FsBytes fs{};
        uint8_t tr[kw::kTransformMinSize];
        KilnTransform t{};
        if (e != exclude && reg && comp_read(world, H_FACTORY, e, static_cast<uint32_t>(G.fs_size), fs.b) &&
            source_recipes(fs, reg, &l.recipes)) {
            l.station = true;
            if (comp_read(world, H_TRANSFORM, e, kw::kTransformMinSize, tr) && kw::transform_decode(tr, kw::kTransformMinSize, &t))
                l.base = base_index_at(srv_bases, t.position);
        }
        cands.push_back(std::move(l));
    }
    CraftLent lent;
    craft_lenders(cands, exclude, base, order, items, have, &lent);
    merge_craft_sources(registered, owner, exclude, order, items,
        [&](uint32_t player, uint32_t container) {
            if (lab_payer ? !(container > kMaxPlayerEntity && is_character(a, player) && !storage_contains(container) && source_container(container))
                          : !source_access(srv_bases,player,container)) return false;
            uint8_t flags = 0;
            PageSet pages{}; bool initialized = false;
            return setup_flags(a,container,&flags) && !(flags & 3) && read_pages(a,container,&pages,&initialized) && initialized;
        }, have, &lent);
    CS.considered.fetch_add(1, std::memory_order_relaxed);

    uint64_t from = 0;
    uint32_t why = 0;
    // What the game's own sources did, for the lines below (GitHub #132: a craft paid from linked sources while the
    // player said the bag held everything; these say what the bag was counted to hold and what its takes paid).
    uint64_t game_paid = 0, payer_paid = 0;
    uint32_t refusals = 0;
    uint8_t last_refusal = 0;
    const int verdict = craft_pay(&lines, order, lent, have,
                                  [&](uint32_t e, uint32_t item, uint32_t* left) {
                                      const uint32_t before = *left;
                                      uint8_t refused = 0;
                                      if (!pay_one(tx, e, item, left, &refused)) return false;
                                      if (refused) ++refusals, last_refusal = refused;
                                      if (std::find(order.begin(), order.end(), e) != order.end()) {
                                          game_paid += before - *left;
                                          if (e == owner) payer_paid += before - *left;
                                      }
                                      return true;
                                  }, &from, &why);
    uint64_t game_counted = 0, payer_held = 0;
    for (const CraftCost& c : lines) game_counted += c.game, payer_held += have(owner, c.item);
    if (t_trace.active) {
        t_trace.counted = true;
        t_trace.game_counted = game_counted, t_trace.game_paid = game_paid;
        t_trace.payer_held = payer_held, t_trace.payer_paid = payer_paid;
        t_trace.pay_refusals = refusals, t_trace.last_refusal = last_refusal;
    }
    if (verdict == kCraftGame) {
        (why == kCraftWhyCovered ? CS.covered : CS.short_).fetch_add(1, std::memory_order_relaxed);
        if (why == kCraftWhyShort) {
            uint64_t short_by = 0;
            for (const CraftCost& c : lines)
                if (c.need > c.game + c.outputs) short_by += c.need - c.game - c.outputs;
            trace(kTraceShort, short_by);
            if (const uint32_t n = pay_log(kind, g_log_refused).take()) {
                if (respec)  // the game then refuses it: the unlearn puts the node back, the reset does not happen
                    LOGI("respec: %s left to the game: the bag, the Magic chests and the respec links cannot cover it (short by "
                         "%llu of item 0x%08x; payer %u)%s", what, static_cast<unsigned long long>(short_by), lines[0].item, owner,
                         n > 1 ? " (more held back)" : "");
                else
                    LOGI("stations: %s left to the game: linked sources and base %d's opted-in outputs cannot cover it (short by "
                         "%llu; payer %u)%s", what, base, static_cast<unsigned long long>(short_by), owner, n > 1 ? " (more held back)" : "");
            }
        } else {
            trace(kTraceCovered);
        }
        return 0;
    }
    if (verdict == kCraftPaid) {
        CS.paid.fetch_add(1, std::memory_order_relaxed);
        CS.from_outputs.fetch_add(static_cast<uint32_t>(std::min<uint64_t>(from, 0xffffffffu)), std::memory_order_relaxed);
        trace(kTracePaid, from);
        if (const uint32_t n = pay_log(kind, g_log_paid).take())
            LOGI("%s: %s paid %llu from additional crafting sources (base %d, payer %u; the bag and Magic chests "
                 "counted %llu, paid %llu; the payer's bag held %llu, paid %llu; %u take(s) refused, last error 0x%02x)%s",
                 tag, what, static_cast<unsigned long long>(from), base, owner, static_cast<unsigned long long>(game_counted),
                 static_cast<unsigned long long>(game_paid), static_cast<unsigned long long>(payer_held),
                 static_cast<unsigned long long>(payer_paid), refusals, static_cast<unsigned>(last_refusal),
                 n > 1 ? " (more held back)" : "");
        *code = 0;
        return 1;
    }
    CS.rollback.fetch_add(1, std::memory_order_relaxed);
    const char* const rolled = why == kCraftWhyFault ? "a take faulted"
                               : why == kCraftWhyGamePaidLess ? "the bag and the Magic chests paid less than they were counted to hold"
                                                              : "a line stayed short";
    trace(kTraceRollback, from, rolled);
    LOGW("%s: %s paid from additional sources rolled back (%s; base %d, payer %u; counted %llu, paid %llu; "
         "%u take(s) refused, last error 0x%02x): nothing moved", tag, what, rolled, base, owner,
         static_cast<unsigned long long>(game_counted), static_cast<unsigned long long>(game_paid), refusals,
         static_cast<unsigned>(last_refusal));
    *code = 4;  // a take may already be queued: CraftRecipe rolls the transaction back
    return 1;
}

// The misnamed CraftableAmount cache returns a STATUS (1 ready, 4 refused), not a count. Never raise it from a display
// snapshot: RecipeApply also uses 4 for non-material eligibility failures. Keep the old private bridge inert for older
// kiln-rules builds. The scoped dry run below lets the game's full RecipeApply compute its own status instead.
int craft_adjust_impl(uint32_t, uint32_t, uint32_t, const void*, uint32_t*) { return 0; }

// Only player_ui's type-1 RecipeApply call is a preview. The hosted server uses the same RecipeApply/InventoryPay
// functions but not this call site. The caller ALWAYS rolls this transaction back, including after success.
// That preview's descriptor byte tx+0xb0 is 0x40 (| 0x08 at a workshop) on client 26430625, so RecipeApply pays it with
// RecipePayAlt, not InventoryPay (GitHub #132, docs/re/stations.md §10.5; since 0.2.0-dev.31.12 observed here too).
kiln::RawHook h_preview_apply, h_preview_take, h_preview_fetch, h_preview_alt;
uintptr_t g_preview_apply = 0, g_preview_ret = 0, g_preview_pay = 0, g_preview_paid = 0, g_preview_take = 0;
uintptr_t g_preview_remove = 0, g_preview_fetch = 0, g_preview_fetch_ret = 0, g_preview_preferred_ret = 0;
uintptr_t g_preview_stock = 0, g_preview_rollback = 0;
// RecipePayAlt, its call's return in RecipeApply (RecipePaid + 7), its two PayMaterial return sites (mode 0: the owner's
// whole line; other modes: the remainder after the client stock table) and that table's getter (bases + 0x1610).
uintptr_t g_preview_alt = 0, g_preview_alt_ret = 0, g_preview_alt_take_ret = 0, g_preview_alt_stock_take_ret = 0;
uintptr_t g_preview_alt_table = 0;
bool g_preview_feat = false;
std::atomic<bool> g_preview_failed{false}, g_preview_hooked{false};
LogLimit g_log_preview{12}, g_log_preview_entry{12}, g_log_preview_payment{16}, g_log_preview_native{8};
struct PreviewCall {
    void* tx = nullptr;
    uint32_t owner = 0, amount = 0, recipe = 0;
    RawCosts costs{};
    bool saw_take = false;
    const char* take_error = nullptr;
    // From the descriptor: the payer RecipeApply calls (its return address) and, for RecipePayAlt, where every take of
    // that payer returns (0 = not checked: InventoryPay also pays from bases' stock through other sites).
    uintptr_t pay_ret = 0, take_ret = 0;
    uint8_t route = 0;  // kPreviewPayRoute or kPreviewAltRoute once the descriptor was read
};
thread_local PreviewCall t_preview;

// Which payer the preview's descriptor makes RecipeApply call (C 0x3f8fe3: tx+0xb0 bit 0x40 calls RecipePayAlt, else
// InventoryPay). Each route is checked only on the code it runs through (GitHub #132): a foreign patch inside InventoryPay
// (Building Tools 0.34.0 at 0x3f8aee) or BaseStockForPlayer (Unified Tweaks Live at 0x46fb65, Cross-Base Storage) is not
// on the RecipePayAlt route the pinned client's preview takes, so it no longer stands the whole preview down.
enum : uint8_t { kPreviewInstall = 0, kPreviewPayRoute = 1, kPreviewAltRoute = 2 };
const char* preview_route_name(uint8_t route) {
    return route == kPreviewPayRoute ? "InventoryPay" : route == kPreviewAltRoute ? "RecipePayAlt" : "install";
}

// Foreign (e.g. CBS), unreadable and unknown code stays vanilla. RawHook may join Kiln's existing physical detour,
// never patch over a foreign one. Recheck at entry as another loader can install later.
struct PreviewCodeRefusal {
    const char* name = nullptr;
    uintptr_t at = 0;
    size_t size = 0;
    uint32_t state = KILN_CODE_UNKNOWN;
    KilnResult rc = KILN_ERR_UNAVAILABLE;
};
bool preview_code_ok(PreviewCodeRefusal* why, uint8_t route) {
    const auto* h = kiln::hooks_v2(g_api);
    if (!h || h->header.size < offsetof(KilnHooksApi, code_state) + sizeof(h->code_state) || !h->code_state) {
        if (why) why->name = "kiln.hooks' code-state check";
        return false;
    }
    struct Dep { uintptr_t at; size_t size; const char* name; uint8_t route; };
    // route 0: every preview; kPreviewPayRoute / kPreviewAltRoute: that payer's own code. Installing checks the routes the
    // pinned client uses (common + RecipePayAlt); InventoryPay's route is checked on a call that takes it.
    for (const Dep& d : {Dep{g_preview_apply, 0x275, "RecipeApply", 0}, Dep{g_preview_take, 0x2b, "PayMaterial", 0},
                         Dep{g_preview_remove, 0x2dd, "InventoryRemoveItems", 0}, Dep{g_preview_fetch, 0x10c, "InventoryPageFetch", 0},
                         Dep{g_preview_rollback, 0x508, "InventoryRollback", 0},
                         Dep{g_preview_ret ? g_preview_ret - 5 : 0, 19, "the crafting menu's RecipeApply call", 0},
                         Dep{g_preview_pay, 0x286, "InventoryPay", kPreviewPayRoute},
                         Dep{g_preview_stock, 0x174, "BaseStockForPlayer", kPreviewPayRoute},
                         Dep{g_preview_alt, 0x1f4, "RecipePayAlt", kPreviewAltRoute},
                         Dep{g_preview_alt_table, 8, "the client stock table read", kPreviewAltRoute}}) {
        if (d.route && d.route != (route == kPreviewInstall ? kPreviewAltRoute : route)) continue;
        uint32_t state = KILN_CODE_UNKNOWN;
        uint64_t generation = 0;
        const KilnResult rc = d.at ? h->code_state(d.at, d.size, &state, &generation) : KILN_ERR_INVALID_ARGUMENT;
        if (rc != KILN_OK || (state != KILN_CODE_ORIGINAL && state != KILN_CODE_MANAGED)) {
            if (why) *why = {d.name, d.at, d.size, state, rc};
            return false;
        }
    }
    return true;
}
// Only on a rate-limited refusal, after the decision. A decoded destination is evidence, not permission to chain.
void preview_code_note(const PreviewCodeRefusal& failure, char* note, size_t n) {
    if (!failure.name || !n) return;
    char detail[384]{};
    const char* state = failure.rc != KILN_OK ? "unavailable" : failure.state == KILN_CODE_FOREIGN ? "FOREIGN" : "UNKNOWN";
    if (failure.rc == KILN_OK && failure.state == KILN_CODE_FOREIGN) {
        const auto* hooks = static_cast<const KilnHooksInternalApi*>(g_api->service(str(KILN_HOOKS_INTERNAL_SERVICE),
                                                                                    KILN_HOOKS_INTERNAL_VERSION));
        if (hooks && hooks->header.size >= offsetof(KilnHooksInternalApi, describe_code_conflict) + sizeof(hooks->describe_code_conflict) &&
            hooks->describe_code_conflict)
            hooks->describe_code_conflict(failure.at, failure.size, detail, sizeof detail);
    }
    std::snprintf(note, n, ": %s code state %s (result %d)%s", failure.name, state, static_cast<int>(failure.rc), detail);
}

struct PreviewDiagnostic {
    void* tx;
    const char* entry = "entry-arguments";
    const char* payment = "no-matching-payment";
    PreviewCodeRefusal code;
    uint32_t recipe = 0, game_payment = 0, item = 0;
    uint64_t need = 0, have = 0;
    unsigned long fault = 0;
    bool saw_payment = false;
    uint8_t route = 0, flags = 0xff, mode = 0xff;  // the descriptor as read (0xff: not read)
};
thread_local PreviewDiagnostic* t_preview_diag = nullptr;

void log_preview_refusal(const PreviewDiagnostic& diag, int32_t amount, uint32_t result) {
    // Separate budgets: a menu can preview many native-only refusals before the player reaches the affected recipe.
    auto& limit = diag.entry ? g_log_preview_entry : diag.saw_payment && diag.payment ? g_log_preview_payment : g_log_preview_native;
    if (!limit.take()) return;
    char note[512]{}, payment[32]{};
    preview_code_note(diag.code, note, sizeof note);
    if (diag.saw_payment) std::snprintf(payment, sizeof payment, "%u", diag.game_payment);
    else std::snprintf(payment, sizeof payment, "not-observed");
    LOGI("stations: linked craft preview left recipe 0x%08x x%d to native result %u: %s (payment %s via %s, descriptor "
         "0x%02x mode %u, item 0x%08x, need %llu, counted %llu, fault 0x%lx)%s", diag.recipe, amount, result,
         diag.entry ? diag.entry : diag.payment ? diag.payment : "native-check-after-supplement", payment,
         diag.route ? preview_route_name(diag.route) : "no payer", static_cast<unsigned>(diag.flags), static_cast<unsigned>(diag.mode),
         diag.item, static_cast<unsigned long long>(diag.need), static_cast<unsigned long long>(diag.have), diag.fault, note);
}

// InventoryRemoveItems can skip failed page fetches and still answer {error=0,left=remaining}. Observe its actual
// participating fetches inside each native PayMaterial call, including base-stock payers, without another simulation.
using PreviewFetchFn = void*(__fastcall*)(void* out, void* tx, uint32_t page);
struct PreviewFetchRead { void* tx; uint32_t calls = 0; const char* error = nullptr; };
thread_local PreviewFetchRead* t_preview_fetch = nullptr;
void* __fastcall hk_preview_fetch(void* out, void* tx, uint32_t page) {
    const uintptr_t ret = h_preview_fetch.caller();
    PreviewFetchRead* const read = t_preview_fetch && t_preview_fetch->tx == tx && t_preview.tx == tx ? t_preview_fetch : nullptr;
    void* const result = h_preview_fetch.original<PreviewFetchFn>()(out, tx, page);
    if (read) {
        if (read->calls == UINT32_MAX) read->error = "page-observation-overflow";
        else ++read->calls;
        uint8_t error = 0xff;
        __try {
            // The native fetch always returns its out pointer; success carries a nonnull inventory view at +8.
            if (result && result == out && rd<void*>(result, 8)) error = rd<uint8_t>(result, 0);
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
        if (!read->error) {
            if (error != 0) read->error = "native-page-read-error";
            else if (ret != g_preview_fetch_ret && ret != g_preview_preferred_ret) read->error = "unrecognized-page-caller";
        }
    }
    return result;
}
PayOut* preview_native_take(PayOut* out, void* tx, uint32_t entity, uint32_t item, uint32_t count, PreviewFetchRead* read) {
    struct Scope { PreviewFetchRead* saved = t_preview_fetch; ~Scope() { t_preview_fetch = saved; } } scope;
    t_preview_fetch = t_preview.tx == tx && tx ? read : nullptr;
    return h_preview_take.original<PayFn>()(out, tx, entity, item, count);
}
// Preserve every native result. An unobserved/incomplete fetch or any outer error prevents complementing code 4. Inside
// RecipePayAlt every take must return to the one site its mode uses (the owner's take: the table is never taken from).
PayOut* __fastcall hk_preview_take(PayOut* out, void* tx, uint32_t entity, uint32_t item, uint32_t count) {
    const uintptr_t ret = h_preview_take.caller();  // before the original
    PreviewFetchRead read{tx};
    PayOut* const result = preview_native_take(out, tx, entity, item, count, &read);
    if (t_preview.tx == tx && tx) {
        t_preview.saw_take = true;
        if (!t_preview.take_error && t_preview.take_ret && ret != t_preview.take_ret)
            t_preview.take_error = "unrecognized-take-caller";
        uint8_t error = 0xff;
        __try { if (result && result == out && result->left <= count) error = result->error; }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
        if (!t_preview.take_error) {
            if (error != 0) t_preview.take_error = "native-material-take-error";
            else if (!read.calls) t_preview.take_error = "no-native-page-read";
            else t_preview.take_error = read.error;
        }
    }
    return result;
}
struct PreviewRead {
    PreviewCall call;
    const void* recipe;
    const void* inputs;
    const char* stage = "transaction-layout";
    bool ok = false;
    uint8_t flags = 0xff, mode = 0xff;
};
void preview_read_impl(void* arg) {
    auto& j = *static_cast<PreviewRead*>(arg);
    auto* tx = static_cast<uint8_t*>(j.call.tx);
    if (!E.tx_ok) return;  // no client layout reads when the transaction feature did not qualify
    j.stage = "transaction-mode-or-flags";
    // 0x08 = inputs only (a workshop); 0x40 = RecipePayAlt pays; any other bit (0x10 skips payment) stays native.
    // Mode tx+0xb1: 0 = the owner alone, 2 = the bases' stock too (RecipePayAlt: the client stock table first).
    j.flags = tx[0xb0];
    j.mode = tx[0xb1];
    const auto flags = static_cast<uint8_t>(tx[0xb0] & ~0x08u);
    if ((flags != 0 && flags != 0x40) || (tx[0xb1] != 0 && tx[0xb1] != 2)) return;
    if (flags == 0x40) {
        if (!g_preview_alt_ret || !g_preview_alt_take_ret || !g_preview_alt_stock_take_ret) return;
        j.call.pay_ret = g_preview_alt_ret;
        j.call.take_ret = tx[0xb1] ? g_preview_alt_stock_take_ret : g_preview_alt_take_ret;
        j.call.route = kPreviewAltRoute;
    } else {
        j.call.pay_ret = g_preview_paid;
        j.call.route = kPreviewPayRoute;
    }
    j.stage = "client-recipe-identity";
    j.call.recipe = rd<uint32_t>(j.recipe, kRecipeId);
    void* const reg = g_client_recipes.load(std::memory_order_acquire);
    if (!j.call.recipe || !reg || find_recipe(j.call.recipe, reg) != j.recipe) return;
    j.stage = "client-storage";
    const auto ctx = rd<void*>(tx, 0);
    const auto job = ctx ? rd<void*>(ctx, 0) : nullptr;
    Access a{};
    a.storage = job && E.job_storage ? E.job_storage(job) : nullptr;
    if (!a.storage || rd<uint8_t>(a.storage, 8) != 2 || !E.component_map_find || !E.read_other) return;
    void* const registry = rd<void*>(a.storage, 0);
    if (!registry) return;
    j.stage = "client-inventory-accessors";
    // Read-only client access, deliberately NOT access_from_tx (which must remain server-only for real payment).
    for (auto [which, offset] : {std::pair<Acc, int>{A_SETUP, 0x18}, {A_INVENTORY, 0x20}}) {
        a.acc[which] = rd<uint64_t>(tx, offset);
        const uint32_t hash = comp_hash(which == A_SETUP ? "InventorySetup" : "Inventory");
        const auto index = E.component_map_find(static_cast<uint8_t*>(registry) + E.registry_components, &hash);
        if (!index || !a.acc[which] || rd<uint16_t>(reinterpret_cast<void*>(a.acc[which]), 0) != *index) return;
    }
    PageSet pages{};
    bool initialized = false;
    j.stage = "owner-inventory-pages";
    if (!read_pages(a, j.call.owner, &pages, &initialized) || !initialized || !pages.n || pages.n > KILN_ITEMS_MAX_INVENTORIES) return;
    j.stage = "owner-inventory-view";
    for (uint32_t i = 0; i < pages.n; ++i) {
        PtrSize ps{};
        E.read_other(&ps, a.storage, a.acc[A_INVENTORY], pages.p[i].entity);
        if (!ps.ptr || ps.size < kInventorySize) return;
    }
    j.stage = "preview-cost-lines";
    j.call.costs.costs = static_cast<const uint8_t*>(j.inputs);
    costs_impl(&j.call.costs);
    if (!j.call.costs.n || !E.item_find) return;
    j.stage = "preview-item-identity";
    void* const items = rd<void*>(tx, 8);
    if (!items) return;
    for (uint32_t i = 0; i < j.call.costs.n; ++i)
        if (!j.call.costs.item[i] || !j.call.costs.count[i] || !E.item_find(j.call.costs.item[i], items)) return;
    j.ok = true;
}

uint32_t __fastcall hk_preview_apply(void* tx, const void* recipe, const void* inputs, uint64_t props,
                                    const void* station, const uint32_t* workshop, uint32_t owner, int32_t amount) {
    const uintptr_t ret = h_preview_apply.caller();
    struct Scope {
        PreviewCall saved = t_preview;
        PreviewDiagnostic* diagnostic = t_preview_diag;
        ~Scope() { t_preview = saved; t_preview_diag = diagnostic; }
    } scope;
    t_preview = {};
    t_preview_diag = nullptr;
    PreviewDiagnostic diag{tx};
    const bool scoped = !g_off.load() && ret == g_preview_ret && g_craft_on.load(std::memory_order_acquire);
    if (scoped) {
        t_preview_diag = &diag;
        if (tx && recipe && inputs && owner && owner <= kMaxPlayerEntity && amount > 0 && amount <= 1000) {
            // The descriptor first (a guarded read): it names the payer, and only that payer's code is checked.
            PreviewRead j{{tx, owner, static_cast<uint32_t>(amount)}, recipe, inputs};
            const bool readable = guard(&preview_read_impl, &j, &diag.fault);
            diag.recipe = j.call.recipe;
            diag.route = j.call.route, diag.flags = j.flags, diag.mode = j.mode;
            diag.entry = readable && j.ok ? nullptr : j.stage;
            if (readable && j.ok) {
                if (!preview_code_ok(&diag.code, j.call.route)) diag.entry = "entry-code";
                else t_preview = j.call;
            }
        }
    }
    // All eligibility, props, water, output capacity and recipe logic stays in the game's function.
    const uint32_t result = h_preview_apply.original<RecipeApplyFn>()(tx, recipe, inputs, props, station, workshop, owner, amount);
    if (scoped && result) log_preview_refusal(diag, amount, result);
    return result;
}

// Called AFTER the original InventoryPay, through its already existing kiln.rules hook, or after the original
// RecipePayAlt (hk_preview_alt). Only the payer the descriptor names, returning to RecipeApply, counts. Only its
// material-shortfall code can be complemented. No stock is taken here: the UI caller rolls back. The server's real payment still checks
// live authorizations/inventories and performs every take, bag first, with the existing no-overdraw rollback.
uint32_t craft_preview_impl(void* tx, void* costs, uint32_t owner, uint32_t amount, void* extra, uintptr_t ret, uint32_t game) {
    PreviewDiagnostic* const diag = t_preview_diag && tx && t_preview_diag->tx == tx ? t_preview_diag : nullptr;
    if (diag) { diag->saw_payment = true; diag->game_payment = game; }
    const auto refuse = [diag, game](const char* reason) {
        if (diag) diag->payment = reason;
        return game;
    };
    if (game != 4) return refuse("native-payment-result");
    if (!tx || t_preview.tx != tx || t_preview.owner != owner || t_preview.amount != amount || extra)
        return refuse("payment-scope");
    if (!t_preview.pay_ret || ret != t_preview.pay_ret) return refuse("payment-caller");
    if (!t_preview.saw_take) return refuse("no-native-material-take");
    if (t_preview.take_error) return refuse(t_preview.take_error);
    if (!g_craft_on.load(std::memory_order_acquire) || g_off.load()) return refuse("snapshot-off");
    if (!preview_code_ok(diag ? &diag->code : nullptr, t_preview.route)) return refuse("payment-code");
    RawCosts rc{static_cast<const uint8_t*>(costs), nullptr, 0, {}, {}, 0};
    unsigned long fault = 0;
    if (!guard(&costs_impl, &rc, &fault) || !rc.n || rc.n != t_preview.costs.n) {
        if (diag) diag->fault = fault;
        return refuse("payment-cost-lines");
    }
    {
        std::lock_guard lock(g_craft_mu);
        if (!g_craft_on.load(std::memory_order_relaxed)) return refuse("snapshot-off");
        for (uint32_t i = 0; i < rc.n; ++i) {
            if (!rc.item[i] || !rc.count[i] || rc.item[i] != t_preview.costs.item[i] || rc.count[i] != t_preview.costs.count[i])
                return refuse("payment-cost-mismatch");
            for (uint32_t k = 0; k < i; ++k) if (rc.item[k] == rc.item[i]) return refuse("duplicate-cost-item");
            const uint64_t need = static_cast<uint64_t>(rc.count[i]) * amount;
            const auto it = g_craft_have.find(rc.item[i]);
            if (need > UINT32_MAX || it == g_craft_have.end() || it->second < need) {
                if (diag) { diag->item = rc.item[i]; diag->need = need; diag->have = it == g_craft_have.end() ? 0 : it->second; }
                return refuse(need > UINT32_MAX ? "cost-overflow" : "snapshot-insufficient");
            }
        }
    }
    if (diag) diag->payment = nullptr;
    if (const uint32_t n = g_log_preview.take())
        LOGI("stations: client craft preview of recipe 0x%08x x%u: material shortfall covered by the server stock; "
             "native eligibility and output checks continue, real payment still required%s", t_preview.recipe, amount,
             n > 1 ? " (more held back)" : "");
    return 0;
}
// RecipePayAlt (tx, costs, owner, multiplier, extra) -> 0 or 4: the same arguments as InventoryPay. kiln.rules' free
// crafting may already have a link here; RawHook joins it. Outside the exact preview scope the native result is returned.
using AltPayFn = uint32_t(__fastcall*)(void* tx, void* costs, uint32_t owner, uint32_t multiplier, void* extra);
uint32_t __fastcall hk_preview_alt(void* tx, void* costs, uint32_t owner, uint32_t multiplier, void* extra) {
    const uintptr_t ret = h_preview_alt.caller();  // before the original
    const uint32_t game = h_preview_alt.original<AltPayFn>()(tx, costs, owner, multiplier, extra);
    if (!tx || (t_preview.tx != tx && !(t_preview_diag && t_preview_diag->tx == tx))) return game;
    return craft_preview_impl(tx, costs, owner, multiplier, extra, ret, game);
}
void preview_hook_on_demand() {
    if (!g_preview_feat || g_preview_failed || g_preview_hooked.load() || !g_craft_on.load()) return;
    if (PreviewCodeRefusal why; !preview_code_ok(&why, kPreviewInstall)) {
        g_preview_failed = true;  // retried in the next client world (client_world, KILN_WORLD_STOPPING)
        char note[512]{};
        preview_code_note(why, note, sizeof note);
        LOGW("stations: linked craft preview stands down%s (the crafting menus' RecipeApply/RecipePayAlt route; existing hooks "
             "kept; the game's own craft status is used; checked again in the next world)", note);
        return;
    }
    if (!h_preview_fetch && !h_preview_fetch.install(g_api, g_owner, g_preview_fetch, reinterpret_cast<void*>(&hk_preview_fetch),
                                                    "stations/client.CraftPreviewFetch")) {
        g_preview_failed = true;
        LOGW("stations: linked craft preview stands down: native page reads cannot be observed (%s)",
             h_preview_fetch.error() ? h_preview_fetch.error() : "hook refused");
        return;
    }
    if (!h_preview_take && !h_preview_take.install(g_api, g_owner, g_preview_take, reinterpret_cast<void*>(&hk_preview_take), "stations/client.CraftPreviewTake")) {
        g_preview_failed = true;
        LOGW("stations: linked craft preview stands down: native material errors cannot be observed (%s)",
             h_preview_take.error() ? h_preview_take.error() : "hook refused");
        return;
    }
    if (!h_preview_alt && !h_preview_alt.install(g_api, g_owner, g_preview_alt, reinterpret_cast<void*>(&hk_preview_alt),
                                                "stations/client.CraftPreviewAltPay")) {
        g_preview_failed = true;
        LOGW("stations: linked craft preview stands down: the crafting menu's payer cannot be observed (%s)",
             h_preview_alt.error() ? h_preview_alt.error() : "hook refused");
        return;
    }
    if (!h_preview_apply.install(g_api, g_owner, g_preview_apply, reinterpret_cast<void*>(&hk_preview_apply), "stations/client.CraftPreview")) {
        g_preview_failed = true;
        LOGW("stations: linked craft preview stands down: %s", h_preview_apply.error() ? h_preview_apply.error() : "hook refused");
        return;
    }
    g_preview_hooked.store(true);
    LOGI("stations: linked crafting's client dry-run material check hooked; native status and real payment remain authoritative");
}

// ---- the station screen's fill buttons on a Kiln client (GitHub #68, docs/re/stations.md §10.3) ----
// The game's UI action query (stations/client.UiActionStatus, called on the game's UI thread): u32 (gameUi*, u32 station,
// u32 recipe, u32 item, u16 amount, u32 type, const RecipeInputCategorySelections*). The station screen enables its
// "+1" and "+10" fill when the type-3 query answers 1. Hooked on demand: the client world's Pre installs it the first
// time a Kiln server's craft-stock snapshot is on (never with a vanilla server, never while no opted-in station of the
// player's base holds outputs). Without an answer for the exact station, recipe and choices it returns the game's status.
kiln::RawHook h_ui_status;
std::atomic<bool> g_fill_hooked{false};
bool g_fill_failed = false;  // the client world's thread only
bool g_fill_feat = false;    // stations.fill_ui with its symbol (stations_resolve)
uintptr_t g_ui_status = 0;
std::mutex g_fill_mu;
std::vector<KilnStationFillAnswer> g_fill_answers;  // g_fill_mu
std::vector<KilnStationFillKey> g_fill_asks;        // g_fill_mu
std::atomic<uint32_t> g_fill_raised{0}, g_fill_faults{0};
LogLimit g_log_fill{12};

void fill_clear() {
    std::lock_guard lock(g_fill_mu);
    g_fill_answers.clear();
    g_fill_asks.clear();
}

bool copy_choices(const void* sel, KilnStationChoice* out) {
    __try {
        std::memcpy(out, sel, sizeof(KilnStationChoice) * 4);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

uint32_t __fastcall hk_ui_status(void* ui, uint32_t station, uint32_t recipe, uint32_t item, uint32_t amount, uint32_t type,
                                 const void* sel) {
    const uint32_t game = h_ui_status.call<uint32_t>(ui, station, recipe, item, amount, type, sel);
    if (type != kUiQueryFill || game != kUiMissing || !g_craft_on.load(std::memory_order_acquire)) return game;
    KilnStationChoice c[4] = {};
    if (sel && !copy_choices(sel, c)) {
        g_fill_faults.fetch_add(1, std::memory_order_relaxed);
        return game;
    }
    const KilnStationFillKey k = fill_key(station, recipe, c);
    const uint32_t want = amount & 0xffffu;  // the amount is a u16 (a stack argument)
    bool answered = false;
    uint32_t out = game;
    {
        std::lock_guard lock(g_fill_mu);
        out = fill_ui_status(game, type, want, k, g_fill_answers, &answered);
        if (!answered) note_fill_ask(&g_fill_asks, k, KILN_STATIONS_MAX_FILL_KEYS);
    }
    if (out != game) {
        g_fill_raised.fetch_add(1, std::memory_order_relaxed);
        // once per station, recipe and amount, in a few lines: the real-client check reads them
        static std::mutex mu;
        static std::unordered_map<uint64_t, uint32_t> said;
        const uint64_t key = (static_cast<uint64_t>(station) << 32) | recipe;
        bool fresh = false;
        {
            std::lock_guard lock(mu);
            if (said.size() > 512) said.clear();
            auto [it, inserted] = said.emplace(key, want);
            fresh = inserted || it->second != want;
            it->second = want;
        }
        if (fresh)
            if (const uint32_t n = g_log_fill.take())
                LOGI("stations: the station screen's fill of %u craft(s) of recipe 0x%08x at station %u is offered: the server's "
                     "fill covers it with station outputs%s", want, recipe, station, n > 1 ? " (more held back)" : "");
    }
    return out;
}

// The ingredient rows' "available" count (stations/client.UiAvailableCount, the game's UI thread): u32 (u32 item,
// inventory view*, stock map*). Hooked on demand with the fill query. At the production station's row widget (two calls)
// and, since GitHub #132, at the crafting menu's own two (its ingredient list, its row's options): the greater of the
// game's count and the snapshot's. The list adds the game's count of the view's fixed slots right after its call
// (stations/client.UiFixedSlotsCount, u32 (u32 item, inventory view*)), which the snapshot already holds, so there Kiln
// asks that count first and leaves it out. docs/re/stations.md §10.3.
kiln::RawHook h_avail;
std::atomic<bool> g_avail_hooked{false};
bool g_avail_failed = false;  // the client world's thread only
bool g_avail_feat = false;    // stations.stock_ui with its symbols (stations_resolve)
uintptr_t g_avail_fn = 0;
AvailSites g_avail_sites;     // set once by avail_resolve, before the hook can go in
using FixedSlotsFn = uint32_t(__fastcall*)(uint32_t item, const void* view);
FixedSlotsFn g_avail_fixed = nullptr;
std::atomic<uint32_t> g_avail_raised{0}, g_avail_faults{0};
LogLimit g_log_avail{12};

// The game's own count of the view's fixed slots, as the list asks it next (the same item and view). False: it faulted.
bool fixed_slots(uint32_t item, const void* view, uint32_t* out) {
    __try {
        *out = g_avail_fixed(item, view);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

uint32_t __fastcall hk_avail(uint32_t item, const void* view, const void* stock) {
    const uintptr_t ret = h_avail.caller();  // the game's call site (read before the original runs)
    const uint32_t game = h_avail.call<uint32_t>(item, view, stock);
    const uint32_t site = stock_ui_site(ret, g_avail_sites);
    if (site == kAvailOther || !g_craft_on.load(std::memory_order_acquire)) return game;
    bool named = false;
    uint64_t have = 0;
    {
        std::lock_guard lock(g_craft_mu);
        if (const auto it = g_craft_have.find(item); it != g_craft_have.end()) named = true, have = it->second;
    }
    uint32_t fixed = 0;
    if (stock_ui_needs_fixed(site, named, have, game) && (!g_avail_fixed || !fixed_slots(item, view, &fixed))) {
        g_avail_faults.fetch_add(1, std::memory_order_relaxed);
        return game;
    }
    const uint32_t out = stock_ui_site_count(game, site, named, have, fixed);
    if (out != game) {
        g_avail_raised.fetch_add(1, std::memory_order_relaxed);
        static std::mutex mu;
        static std::unordered_map<uint32_t, uint64_t> said;
        const uint64_t key = (static_cast<uint64_t>(game) << 32) | out;
        bool fresh = false;
        {
            std::lock_guard lock(mu);
            if (said.size() > 512) said.clear();
            auto [it, inserted] = said.emplace(item, key);
            fresh = inserted || it->second != key;
            it->second = key;
        }
        if (fresh)
            if (const uint32_t n = g_log_avail.take())
                LOGI("stations: an ingredient's available count for item 0x%08x shown %u -> %u (%s) with the server's crafting stock%s",
                     item, game, out, site == kAvailFactoryRow ? "station screen" : site == kAvailMenuList ? "crafting menu" : "crafting menu option",
                     n > 1 ? " (more held back)" : "");
    }
    return out;
}

// The client world's Pre: the hook, the first time the snapshot is on.
void avail_hook_on_demand() {
    if (!g_avail_feat || g_avail_failed || g_avail_hooked.load(std::memory_order_acquire) || !g_craft_on.load(std::memory_order_acquire))
        return;
    if (!h_avail.install(g_api, g_owner, g_avail_fn, reinterpret_cast<void*>(&hk_avail), "stations/client.UiAvailableCount")) {
        g_avail_failed = true;
        LOGE("stations: hooking the ingredient rows' available count failed: %s", h_avail.error() ? h_avail.error() : "?");
        lower("stations.stock_ui", KILN_CAP_REASON_FAULTED, "hooking the ingredient rows' available count failed (see kiln.log)");
        return;
    }
    g_avail_hooked.store(true, std::memory_order_release);
    LOGI("stations: the ingredient rows' available count %p hooked (the station screen and the crafting menu count what the "
         "server's snapshot names)", reinterpret_cast<void*>(g_avail_fn));
}

// The client world's Pre: the hook, the first time the snapshot is on.
void fill_hook_on_demand() {
    if (!g_fill_feat || g_fill_failed || g_fill_hooked.load(std::memory_order_acquire) || !g_craft_on.load(std::memory_order_acquire))
        return;
    if (!h_ui_status.install(g_api, g_owner, g_ui_status, reinterpret_cast<void*>(&hk_ui_status), "stations/client.UiActionStatus")) {
        g_fill_failed = true;
        LOGE("stations: hooking the station screen's fill query failed: %s", h_ui_status.error() ? h_ui_status.error() : "?");
        lower("stations.fill_ui", KILN_CAP_REASON_FAULTED, "hooking the station screen's fill query failed (see kiln.log)");
        return;
    }
    g_fill_hooked.store(true, std::memory_order_release);
    LOGI("stations: the station screen's fill query %p hooked (a fill only station outputs cover is offered when the server says so)",
         reinterpret_cast<void*>(g_ui_status));
}

uint32_t craft_stats(uint32_t* out, uint32_t n) {
    const std::atomic<uint32_t>* f[kCraftStatCount] = {&CS.calls, &CS.idle, &CS.other_world, &CS.mode0, &CS.no_access,
                                                       &CS.considered, &CS.covered, &CS.short_, &CS.paid, &CS.from_outputs,
                                                       &CS.rollback, &CS.skipped, &CS.lab, &CS.respec};
    for (uint32_t i = 0; out && i < n && i < kCraftStatCount; ++i) out[i] = f[i]->load(std::memory_order_relaxed);
    return kCraftStatCount;
}

// ---- the plan (tick thread; the program repeats it with its own reads) ----
struct PlanOut {
    int32_t status = KILN_STATION_DONE;
    uint32_t recipe = 0;
    int32_t base = -1;
    std::vector<InputNow> in;  // the resolved inputs, then the water (kWaterItem) when the recipe needs some
    uint32_t loaded = 0, affordable = 0, wanted = 0, planned = 0;
    Limit limit;
    uint32_t water_per = 0;
    uint64_t water_have = 0, water_stock = 0;
    uint8_t use_all = 0;
    char why[128] = "";
};

void say(PlanOut* p, int32_t status, const char* fmt, uint32_t a = 0, uint32_t b = 0) {
    p->status = status;
    std::snprintf(p->why, sizeof p->why, fmt, a, b);
}

// A category choice fits a recipe when the category is one of its inputs' and the index is one of the category's options.
struct CatCall;
bool category_options(void* reg, uint32_t id, uint32_t* n);
bool choices_fit(const void* recipe, const KilnStationChoice* choices, void* reg, char* why, size_t cap) {
    const auto* r = static_cast<const uint8_t*>(recipe);
    const int32_t rel = rd<int32_t>(r, kRecipeInputs);
    const uint32_t n = std::min(rd<uint32_t>(r, kRecipeInputs + 4), KILN_STATIONS_MAX_INPUTS);
    for (int k = 0; k < 4; ++k) {
        const KilnStationChoice& c = choices[k];
        if (!c.category) continue;
        bool in_recipe = false;
        for (uint32_t i = 0; i < n && rel && !in_recipe; ++i)
            in_recipe = rd<uint32_t>(r + kRecipeInputs + rel + i * kInputStride, kInputCategory) == c.category;
        if (!in_recipe)
            return std::snprintf(why, cap, "category 0x%08x is not one of recipe 0x%08x's inputs", c.category, rd<uint32_t>(r, kRecipeId)), false;
        uint32_t options = 0;
        if (!category_options(reg, c.category, &options) || c.index >= options)
            return std::snprintf(why, cap, "category 0x%08x has no option %u (%u options)", c.category, c.index, options), false;
    }
    return true;
}

// Station, recipe and choices of a FILL (shared by plan() and the program): fills recipe, choices, the recipe pointer.
bool fill_recipe(const KilnStationRequest& r, const FsBytes& fs, uint32_t workshop, void* reg, const void** recipe,
                 KilnStationChoice* choices, PlanOut* p) {
    p->recipe = r.recipe ? r.recipe : rd<uint32_t>(fs.b, G.fs_next);
    if (!p->recipe) return say(p, KILN_STATION_NO_RECIPE, "the station has no recipe selected"), false;
    *recipe = find_recipe(p->recipe, reg);
    if (!*recipe) return say(p, KILN_STATION_NO_RECIPE, "recipe 0x%08x is not in the recipe registry", p->recipe), false;
    const uint32_t rw = rd<uint32_t>(*recipe, kRecipeWorkshop);
    if (rw != workshop)
        return say(p, KILN_STATION_WRONG_WORKSHOP, "recipe 0x%08x is a recipe of workshop 0x%08x", p->recipe, rw), false;
    std::memset(choices, 0, sizeof(KilnStationChoice) * 4);
    if (any_choice(r.choices)) {
        if (!choices_fit(*recipe, r.choices, reg, p->why, sizeof p->why)) return p->status = KILN_STATION_REFUSED, false;
        std::memcpy(choices, r.choices, sizeof r.choices);
    } else if (p->recipe == rd<uint32_t>(fs.b, G.fs_next)) {
        std::memcpy(choices, fs.b + G.fs_next_sel, 32);
    }
    p->use_all = fs.b[G.fs_use_all];
    return true;
}

uint64_t keep_of(const KilnStationStack* keep, uint32_t n, uint32_t item) {
    uint64_t k = 0;
    for (uint32_t i = 0; i < n; ++i)
        if (keep[i].item == item) k += keep[i].count;
    return k;
}

// The arithmetic once `in` holds the resolved inputs with have and stock (and the water input).
void finish_plan(const KilnStationRequest& r, PlanOut* p) {
    merge_inputs(&p->in);
    const PlanMath m = plan_math(r.flags, r.crafts, p->in);
    p->loaded = m.loaded;
    p->affordable = m.affordable;
    p->wanted = m.wanted;
    p->planned = m.planned;
    p->limit = m.limit;
    p->status = m.status;
    if (m.status == KILN_STATION_UNCHANGED) return say(p, KILN_STATION_UNCHANGED, "the station already holds %u complete craft(s)", p->loaded);
    if (m.planned >= m.wanted) return;
    const int32_t st = m.status;
    switch (m.limit.why) {
    case KILN_STATION_LIMIT_WATER:
        return say(p, st, "water (the base's water sources, or the station's water capacity) covers %u more craft(s)", m.affordable);
    case KILN_STATION_LIMIT_RESERVE:
        return say(p, st, "the reserve of item 0x%08x leaves %u more craft(s)", m.limit.item, m.affordable);
    case KILN_STATION_LIMIT_STOCK:
        return say(p, st, "the base's stock of item 0x%08x covers %u more craft(s)", m.limit.item, m.affordable);
    default:
        return say(p, st, "%u of %u craft(s) covered", m.planned, m.wanted);
    }
}

// The inputs of a FILL with their counts: the station's, the base stock's (after the reserves), the player's bag.
void plan_inputs(const KilnStationRequest& r, const KilnStationStack* keep, const ResolveCall& rc,
                 std::unordered_map<uint32_t, uint64_t>& have, std::unordered_map<uint32_t, uint64_t>& stock,
                 std::unordered_map<uint32_t, uint64_t>& bag, const std::unordered_map<uint32_t, uint64_t>* chests,
                 const Lent* lent, PlanOut* p, const LinkUse* links = nullptr) {
    for (uint32_t i = 0; i < rc.n; ++i) {
        InputNow x{};
        x.item = rc.out[i].item;
        x.count = rc.out[i].count;
        x.have = have[x.item];
        const uint64_t k = keep_of(keep, r.keep_count, x.item);
        x.stock = after_keep(stock[x.item], k) + bag[x.item];  // the game takes from the player first; no reserve there
        x.kept = std::min(stock[x.item], k);
        if (chests) {  // after the Magic chests, before station outputs; reserves do not apply
            const auto it = chests->find(x.item);
            x.chests = it == chests->end() ? 0 : it->second;
            x.stock += x.chests;
        }
        if (lent) {  // last: the opted-in stations' Outputs (no reserve there either)
            const auto it = lent->count.find(x.item);
            x.outputs = it == lent->count.end() ? 0 : it->second;
            x.stock += x.outputs;
        }
        if (links) {  // the player's fill-opted linked sources, around the outputs (no reserve)
            x.linked = links->before_of(x.item);
            x.linked_after = links->after_of(x.item);
            x.stock += x.linked + x.linked_after;
        }
        p->in.push_back(x);
    }
}

// Named ordinary chests, checked and counted. `fact` reads one entity; `count_one` adds its stacks. A refusal sets why.
struct ChestUse {
    bool refused = false;
    char why[128] = "";
    std::vector<uint32_t> ids;
    std::unordered_map<uint32_t, uint64_t> count;
};
template <class Fact, class CountOne>
ChestUse chest_sources(const KilnEntity* ids, uint32_t n, KilnEntity target, int32_t base, const uint32_t* items, uint32_t nitems,
                       Fact fact, CountOne count_one) {
    ChestUse u;
    if (!n || !use_named_chests(items, nitems)) return u;  // none named, or a repeated input: ignore the chests, do not refuse
    std::vector<NamedChest> named;
    named.reserve(n);
    for (uint32_t i = 0; i < n; ++i) {
        const ChestFacts f = fact(ids[i]);
        named.push_back(NamedChest{ids[i], f.base, kind_of(f, ids[i], target, base)});
    }
    uint32_t bad = 0;
    ChestKind kind = ChestKind::Ordinary;
    if (!check_named_chests(named, &bad, &kind)) {
        u.refused = true;
        std::snprintf(u.why, sizeof u.why, chest_kind_why(kind), bad);
        return u;
    }
    u.ids = eligible_chests(named, target, base);
    for (uint32_t e : u.ids) count_one(e, &u.count);
    return u;
}

bool magic_listed(const std::vector<KilnEntity>& list, KilnEntity e) {
    return std::find(list.begin(), list.end(), e) != list.end();
}

// Whether a FILL may use station outputs: the capability, the request, and resolved inputs without a repeated item.
bool outputs_allowed(const KilnStationRequest& r, const ResolveCall& rc) {
    if (!G.outputs_ok || (r.flags & KILN_STATION_FILL_NO_OUTPUTS)) return false;
    uint32_t items[KILN_STATIONS_MAX_INPUTS];
    for (uint32_t i = 0; i < rc.n; ++i) items[i] = rc.out[i].item;
    if (inputs_repeat(items, rc.n)) return false;
    std::lock_guard lock(g_mu);
    return !g_sources.empty();
}
// Whether a FILL takes from its player's fill-opted linked sources (GitHub #132): the request, a player, the registry.
bool fill_links_wanted(const KilnStationRequest& r) {
    return (r.flags & KILN_STATION_FILL_LINKED) && r.player && r.player <= kMaxPlayerEntity && G.registered_ok && !g_off.load();
}
std::vector<uint32_t> input_items(const ResolveCall& rc) {
    std::vector<uint32_t> v;
    for (uint32_t i = 0; i < rc.n; ++i) v.push_back(rc.out[i].item);
    return v;
}

void plan_tick(const KilnStationRequest& r, const KilnEntity* chest_ids, uint32_t chest_n, PlanOut* p) {
    FsBytes fs{};
    if (!factory_of(r.station, &fs)) return say(p, KILN_STATION_NOT_A_STATION, "entity %u has no FactoryStation", r.station);
    void* reg = recipes_registry();
    const void* recipe = nullptr;
    KilnStationChoice choices[4];
    if (!fill_recipe(r, fs, workshop_of(r.station), reg, &recipe, choices, p)) return;
    KilnVec3 pos{};
    RawBases rb{};
    if (!position_of(r.station, &pos) || !read_bases(&rb)) return say(p, KILN_STATION_NO_BASE, "the bases cannot be read");
    p->base = base_index_at(rb.sys, pos);
    if (p->base < 0) return say(p, KILN_STATION_NO_BASE, "station %u stands in no base", r.station);
    ResolveCall rc{};
    if (!resolve_inputs(recipe, choices, reg, &rc))
        return say(p, KILN_STATION_REFUSED, "the inputs of recipe 0x%08x cannot be resolved", p->recipe);
    std::unordered_map<uint32_t, uint64_t> have, stock, bag;
    count_items(r.station, &have);
    std::vector<KilnEntity> list;
    if (base_list(rb, static_cast<uint32_t>(p->base), false, &list))
        for (KilnEntity c : list) count_items(c, &stock);
    if (r.player) count_items(r.player, &bag);
    Lent lent;
    const bool outs = outputs_allowed(r, rc);
    if (outs)
        lenders(r.station, p->base, input_items(rc), reg, [](KilnEntity e, FsBytes* f) { return factory_of(e, f); },
                [&](KilnEntity e) {
                    KilnVec3 sp{};
                    return position_of(e, &sp) ? base_index_at(rb.sys, sp) : -1;
                },
                [](KilnEntity e, std::unordered_map<uint32_t, uint64_t>* m) { count_items(e, m); }, &lent);
    const auto items = input_items(rc);
    const ChestUse chests = chest_sources(
        chest_ids, chest_n, r.station, p->base, items.data(), static_cast<uint32_t>(items.size()),
        [&](KilnEntity e) {
            ChestFacts f;
            KilnVec3 sp{};
            f.present = position_of(e, &sp);
            if (f.present) f.base = base_index_at(rb.sys, sp);
            FsBytes got{};
            f.station = factory_of(e, &got);
            f.magic = magic_listed(list, e);
            uint32_t size = 0;
            const auto* setup = g_w ? static_cast<const uint8_t*>(g_w->component(e, H_SETUP, &size)) : nullptr;
            f.inventory = setup && size > static_cast<uint32_t>(E.setup_initialized) && setup[E.setup_initialized] != 0;
            // a player's or an NPC's inventory is no storage chest; a setup too short to tell is not one either
            f.person = f.inventory && (size <= kSetupFlags || setup_is_person(setup[kSetupFlags]));
            return f;
        },
        [](KilnEntity e, std::unordered_map<uint32_t, uint64_t>* m) { count_items(e, m); });
    if (chests.refused) {
        p->status = KILN_STATION_REFUSED;
        std::snprintf(p->why, sizeof p->why, "%s", chests.why);
        return;
    }
    // GitHub #132: the player's fill-opted linked sources, in any base (each checked as the payment checks a link)
    LinkUse links;
    const bool linked = fill_links_wanted(r);
    if (linked) {
        std::unordered_set<uint32_t> skip{r.station};
        skip.insert(list.begin(), list.end());
        skip.insert(chests.ids.begin(), chests.ids.end());
        for (const auto& [item, by] : lent.by) skip.insert(by.begin(), by.end());
        std::unordered_map<uint32_t, std::unordered_map<uint32_t, uint64_t>> held;
        links = links_for(craft_sources_copy(r.player), &CraftSource::fills, r.player, items, skip,
                          [&](uint32_t pl, uint32_t c) { return source_access(rb.sys, pl, c); },
                          [&](uint32_t c, uint32_t item) -> uint64_t {
                              auto it = held.find(c);
                              if (it == held.end()) {
                                  it = held.emplace(c, std::unordered_map<uint32_t, uint64_t>{}).first;
                                  count_items(c, &it->second);
                              }
                              const auto n = it->second.find(item);
                              return n == it->second.end() ? 0 : n->second;
                          });
    }
    plan_inputs(r, r.keep, rc, have, stock, bag, chest_n ? &chests.count : nullptr, outs ? &lent : nullptr, p, linked ? &links : nullptr);
    void* attributes = singleton("impact::AttributeRegistry");
    int32_t cur = 0, max = 0;
    water_of(r.station, attributes, &cur, &max);
    InputNow w{};
    if (water_input(rd<uint32_t>(recipe, G.recipe_water), (r.flags & KILN_STATION_NO_WATER) != 0, static_cast<uint64_t>(std::max(cur, 0)),
                    static_cast<uint64_t>(std::max(max, 0)), base_water(rb, p->base, attributes), &w)) {
        p->water_per = w.count;
        p->water_have = w.have;
        p->water_stock = w.stock;
        p->in.push_back(w);
    }
    finish_plan(r, p);
}

// GitHub #132 (0.2.0-dev.31.16): the plan of an item fill (KILN_STATION_FILL_ITEM): the station's pile, the bag and the
// Magic chests of the station's base (the game's own call), the player's fill-opted links; one input {item, 1}.
void plan_item_tick(const KilnStationRequest& r, PlanOut* p) {
    FsBytes fs{};
    if (!factory_of(r.station, &fs)) return say(p, KILN_STATION_NOT_A_STATION, "entity %u has no FactoryStation", r.station);
    KilnVec3 pos{};
    RawBases rb{};
    if (!position_of(r.station, &pos) || !read_bases(&rb)) return say(p, KILN_STATION_NO_BASE, "the bases cannot be read");
    p->base = base_index_at(rb.sys, pos);
    if (p->base < 0) return say(p, KILN_STATION_NO_BASE, "station %u stands in no base", r.station);
    std::unordered_map<uint32_t, uint64_t> have, stock, bag;
    count_items(r.station, &have);
    std::vector<KilnEntity> list;
    if (base_list(rb, static_cast<uint32_t>(p->base), false, &list))
        for (KilnEntity c : list) count_items(c, &stock);
    if (r.player) count_items(r.player, &bag);
    LinkUse links;
    const bool linked = fill_links_wanted(r);
    if (linked) {
        std::unordered_set<uint32_t> skip{r.station};
        skip.insert(list.begin(), list.end());
        links = links_for(craft_sources_copy(r.player), &CraftSource::fills, r.player, {r.item}, skip,
                          [&](uint32_t pl, uint32_t c) { return source_access(rb.sys, pl, c); },
                          [&](uint32_t c, uint32_t item) -> uint64_t {
                              std::unordered_map<uint32_t, uint64_t> held;
                              count_items(c, &held);
                              const auto n = held.find(item);
                              return n == held.end() ? 0 : n->second;
                          });
    }
    const InputNow x = item_fill_input(r.item, have[r.item], stock[r.item] + bag[r.item], links.before_of(r.item), links.after_of(r.item));
    const ItemFill f = item_fill_plan(r.flags, r.crafts, x);
    p->in.push_back(x);
    p->loaded = f.math.loaded;
    p->affordable = f.math.affordable;
    p->wanted = f.math.wanted;
    p->planned = f.math.planned;
    p->limit = f.math.limit;
    p->status = f.math.status;
    if (p->status != KILN_STATION_DONE)
        std::snprintf(p->why, sizeof p->why, "the bag, the base's Magic chests%s hold %u of item 0x%08x", linked ? " and the fill-opted links" : "",
                      p->affordable, r.item);
}

// ================================================================================ the program ====

const char* const kSingRead[5] = {"BalancingRegistry", "g38_items::ItemRegistry", "impact::AttributeRegistry",
                                  "PlayerBasesSystemServer", "g38_items::RecipeRegistry"};
const char* const kSingWrite[2] = {"KnowledgeTriggerQueue", "ServerFactoryInfo"};
const char* const kOtherRead[2] = {"CurrentTransform", "Crafting"};
const char* const kEvents[2] = {"PickupEvent", "InventoryFull"};

bool resolve_slots() {
    if (g_slots) return true;
    for (int i = 0; i < 5; ++i) g_s_sing[i] = g_w->program_slot(g_prog, KILN_SLOT_SINGLETON, str(kSingRead[i]));
    for (int i = 0; i < 2; ++i) g_s_sing[5 + i] = g_w->program_slot(g_prog, KILN_SLOT_SINGLETON, str(kSingWrite[i]));
    for (int i = 0; i < A_COUNT; ++i) g_s_acc[i] = g_w->program_slot(g_prog, KILN_SLOT_OTHER_WRITE, str(kAccNames[i]));
    g_s_factory = g_w->program_slot(g_prog, KILN_SLOT_OTHER_WRITE, str("FactoryStation"));
    g_s_netfuel = g_w->program_slot(g_prog, KILN_SLOT_OTHER_WRITE, str("NetworkFuel"));
    g_s_transform = g_w->program_slot(g_prog, KILN_SLOT_OTHER_READ, str(kOtherRead[0]));
    g_s_crafting = g_w->program_slot(g_prog, KILN_SLOT_OTHER_READ, str(kOtherRead[1]));
    g_s_deposit = g_w->program_slot(g_prog, KILN_SLOT_OTHER_READ, str("DynamicInventoryCraftingStock"));
    for (int i = 0; i < 2; ++i) g_s_ev[i] = g_w->program_slot(g_prog, KILN_SLOT_EVENT_OUT, str(kEvents[i]));
    auto good = [](int s) { return s >= 1 && s < 96; };
    bool ok = good(g_s_factory) && good(g_s_netfuel) && good(g_s_transform) && good(g_s_crafting);
    for (int s : g_s_sing) ok = ok && good(s);
    for (int s : g_s_acc) ok = ok && good(s);
    for (int s : g_s_ev) ok = ok && good(s);
    return g_slots = ok;
}

// The program's own view of one update: kiln.items' Access plus the station accessors and singletons.
struct Run {
    KilnProgramCtx* ctx;
    uint64_t* it;
    Access a;
    uint64_t acc_factory, acc_netfuel, acc_transform, acc_crafting, acc_deposit;
    void* bases;
    void* recipes;
    void* factory_info;
};

struct ExtraCheck {
    const Run* run;
    bool ok;
    char why[160];
};
void extra_check_impl(void* p) {
    auto* x = static_cast<ExtraCheck*>(p);
    const Run& r = *x->run;
    void* registry = *static_cast<void**>(r.a.storage);
    const std::pair<uint64_t, uint32_t> want[4] = {{r.acc_factory, H_FACTORY}, {r.acc_netfuel, H_NETWORK_FUEL},
                                                    {r.acc_transform, H_TRANSFORM}, {r.acc_crafting, H_CRAFTING}};
    for (const auto& [acc, h] : want) {
        const uint16_t* idx = E.component_map_find(static_cast<uint8_t*>(registry) + E.registry_components, &h);
        if (!acc || !idx || *reinterpret_cast<const uint16_t*>(acc) != *idx) {
            std::snprintf(x->why, sizeof x->why, "an accessor of kiln_items_stations is not the component it names (hash %08x)", h);
            return;
        }
    }
    if (!r.bases || !r.recipes || !r.factory_info) {
        std::snprintf(x->why, sizeof x->why, "a singleton slot of kiln_items_stations is empty (bases, recipes, factory info)");
        return;
    }
    x->ok = true;
}

bool verify(KilnProgramCtx* ctx, uint64_t* it, Run* run, char* why) {
    const int sing[4] = {g_s_sing[0], g_s_sing[1], g_s_sing[2], g_s_sing[5]};  // Balancing, Items, Attributes, Knowledge
    bool ok = false;
    unsigned long code = 0;
    if (!verify_access(it, sing, g_s_acc, g_s_ev, ctx, &run->a, &ok, why, &code) || !ok) return false;
    run->ctx = ctx;
    run->it = it;
    run->acc_factory = it[g_s_factory];
    run->acc_netfuel = it[g_s_netfuel];
    run->acc_transform = it[g_s_transform];
    run->acc_crafting = it[g_s_crafting];
    run->acc_deposit = g_s_deposit >= 1 && g_s_deposit < 96 ? it[g_s_deposit] : 0;
    run->bases = reinterpret_cast<void*>(it[g_s_sing[3]]);
    run->recipes = reinterpret_cast<void*>(it[g_s_sing[4]]);
    run->factory_info = reinterpret_cast<void*>(it[g_s_sing[6]]);
    run->a.recipes = run->recipes;
    ExtraCheck x{run, false, ""};
    if (!guard(&extra_check_impl, &x, &code) || !x.ok) {
        std::snprintf(why, 160, "%s", x.why[0] ? x.why : "checking kiln_items_stations' accessors faulted");
        return false;
    }
    return true;
}

const uint8_t* other(const Run& r, uint64_t acc, KilnEntity e, uint64_t min) {
    PtrSize ps{};
    E.read_other(&ps, r.a.storage, acc, e);
    return ps.ptr && ps.size >= min ? static_cast<const uint8_t*>(ps.ptr) : nullptr;
}

// Counts per item in an entity's inventories, inside the program (the program's accessors).
void count_pages(const Access& a, KilnEntity e, std::unordered_map<uint32_t, uint64_t>* into) {
    PageSet ps;
    bool init = false;
    if (!read_pages(a, e, &ps, &init)) return;
    for (uint32_t p = 0; p < ps.n; ++p)
        for (const Stack& s : ps.p[p].s)
            if (s.item && s.count) (*into)[s.item] += s.count;
}

void finish(KilnStationResult* res, int32_t status, const char* fmt, uint32_t a = 0, uint32_t b = 0) {
    res->status = status;
    std::snprintf(res->message, sizeof res->message, fmt, a, b);
}

void finish_text(KilnStationResult* res, int32_t status, const char* text) {
    res->status = status;
    std::snprintf(res->message, sizeof res->message, "%s", text);
}

KilnStationResult result_base(const Req& q) { return result_for(q); }

void noted_write(KilnEntity station, KilnOwner owner, const uint8_t* fs_after, uint32_t action) {
    Wrote w;
    w.owner = owner;
    w.step = current_step();
    w.sets_stopped = action == KILN_STATION_STOP || action == KILN_STATION_RESUME || action == KILN_STATION_SELECT;
    w.sets_selected = action == KILN_STATION_SELECT;
    w.stopped = fs_after[G.fs_stopped];
    w.selected = rd<uint32_t>(fs_after, G.fs_next);
    std::lock_guard lock(g_mu);
    g_wrote[station] = w;
}

// ---- FILL inside the program ----
struct FillCall {
    const Run* run;
    void* tx;
    const uint64_t* stock;
    const void* inputs;  // {ptr, count}
    uint32_t station, crafts, water, player;
    int32_t rc;
};
void fill_call_impl(void* p) {
    auto* f = static_cast<FillCall*>(p);
    f->rc = G.fill(f->tx, f->stock, f->inputs, f->station, static_cast<uint16_t>(f->crafts), f->water, f->player);
}

void setup_for_fill(const Run& r, KilnEntity player, const float pos[3], uint8_t* s) {
    std::memset(s, 0, kSetupBlock);
    const Access& a = r.a;
    auto put = [s](uint32_t off, const void* v) { std::memcpy(s + off, &v, 8); };
    auto put_acc = [s](uint32_t off, uint64_t v) { std::memcpy(s + off, &v, 8); };
    put(0x08, a.ctx);
    put(0x10, a.items);
    put(0x18, a.balancing);
    put(0x20, a.attributes);
    put_acc(0x28, a.acc[A_SETUP]);
    put_acc(0x30, a.acc[A_INVENTORY]);
    put_acc(0x38, a.acc[A_SLOTSEL]);
    put_acc(0x40, a.acc[A_ITEMSTATE]);
    put_acc(0x48, a.acc[A_OWNER]);
    put_acc(0x58, a.acc[A_EMPTY]);
    put_acc(kSetupNetworkFuel, r.acc_netfuel);
    put_acc(0x70, a.acc[A_FUEL]);
    put(0xA0, a.ev_pickup);
    put(0xA8, a.ev_full);
    put(0xC0, player ? a.knowledge : nullptr);
    std::memcpy(s + E.setup_operator, &player, 4);  // the game's handler: the player; for a fill of Kiln's alone: 0
    put(kSetupBases, r.bases);
    std::memcpy(s + kSetupPosition, pos, 12);
}

void ledger(const uint8_t* tx, KilnStationResult* res) {
    const uint8_t* ops = rd<const uint8_t*>(tx, E.tx_ops);
    const uint64_t n = rd<uint64_t>(tx, E.tx_ops + 8);
    for (uint64_t i = 0; i < n && ops && res->change_count < KILN_STATIONS_MAX_CHANGES; ++i) {
        const uint8_t* op = ops + i * kOpStride;
        KilnItemsMoveChange& c = res->changes[res->change_count++];
        const uint64_t slot = rd<uint64_t>(op, 0);
        if (op[0x26] == 0) c.to_slot = slot;
        else c.from_slot = slot;
        c.item = rd<uint32_t>(op, 8);
        c.amount = rd<uint16_t>(op, 0x24);
        const uint32_t pide = rd<uint32_t>(op, 0x20);
        c.pide = pide == 0xFFFFFFFFu ? 0 : pide;
    }
}

void run_fill(const Run& run, const Req& q, KilnStationResult* res) {
    const KilnStationRequest& r = q.r;
    if (!G.fill_ok) return finish(res, KILN_STATION_UNAVAILABLE, "stations.fill is off on this build");
    const uint8_t* fsp = other(run, run.acc_factory, r.station, static_cast<uint64_t>(G.fs_size));
    if (!fsp) return finish(res, KILN_STATION_NOT_A_STATION, "entity %u has no FactoryStation", r.station);
    FsBytes fs{};
    std::memcpy(fs.b, fsp, sizeof fs.b);
    res->state_before = res->state_after = fs.b[kFsState];
    res->stopped_before = res->stopped_after = fs.b[G.fs_stopped];
    const uint8_t* cr = other(run, run.acc_crafting, r.station, kCraftingWorkshop + 4);
    const uint32_t workshop = cr ? rd<uint32_t>(cr, kCraftingWorkshop) : 0;
    PlanOut p;
    const void* recipe = nullptr;
    KilnStationChoice choices[4];
    if (!fill_recipe(r, fs, workshop, run.recipes, &recipe, choices, &p)) return finish_text(res, p.status, p.why);
    res->recipe = p.recipe;
    const uint8_t* tr = other(run, run.acc_transform, r.station, 24);
    if (!tr) return finish(res, KILN_STATION_NOT_A_STATION, "station %u has no position", r.station);
    float pos[3];
    for (int k = 0; k < 3; ++k) pos[k] = static_cast<float>(static_cast<double>(rd<int64_t>(tr, 8 * k)) * (1.0 / 4294967296.0));
    const int32_t base = base_index_at(run.bases, KilnVec3{pos[0], pos[1], pos[2]});
    if (base < 0) return finish(res, KILN_STATION_NO_BASE, "station %u stands in no base", r.station);
    // the base's stock, as BaseStockForPlayer hands it to the game's handler: {A.ptr, A.count, B.ptr, B.count}
    uint64_t stock[4] = {};
    {
        const auto* s = static_cast<const uint8_t*>(run.bases);
        const int64_t off = static_cast<int64_t>(base) * G.stride;
        stock[0] = rd<uint64_t>(s, G.stock_items + off);
        stock[1] = rd<uint64_t>(s, G.stock_items_count + off);
        stock[2] = rd<uint64_t>(s, G.stock_water + off);
        stock[3] = rd<uint64_t>(s, G.stock_water_count + off);
    }
    ResolveCall rc{};
    if (!resolve_inputs(recipe, choices, run.recipes, &rc))
        return finish(res, KILN_STATION_REFUSED, "the inputs of recipe 0x%08x cannot be resolved", p.recipe);
    std::unordered_map<uint32_t, uint64_t> have, stocked, bag;
    count_pages(run.a, r.station, &have);
    for (uint64_t i = 0; i < stock[1] && stock[0]; ++i) count_pages(run.a, reinterpret_cast<const uint32_t*>(stock[0])[i], &stocked);
    if (r.player) count_pages(run.a, r.player, &bag);
    // the opted-in stations' Outputs, revalidated here at the transaction: this step's inventories and recipes
    Lent lent;
    const bool outs = outputs_allowed(r, rc);
    if (outs)
        lenders(r.station, base, input_items(rc), run.recipes,
                [&](KilnEntity e, FsBytes* f) {
                    const uint8_t* fp = other(run, run.acc_factory, e, static_cast<uint64_t>(G.fs_size));
                    if (!fp) return false;
                    std::memcpy(f->b, fp, sizeof f->b);
                    return true;
                },
                [&](KilnEntity e) {
                    const uint8_t* t = other(run, run.acc_transform, e, 24);
                    if (!t) return -1;
                    KilnVec3 sp{};
                    sp.x = static_cast<double>(rd<int64_t>(t, 0)) * (1.0 / 4294967296.0);
                    sp.y = static_cast<double>(rd<int64_t>(t, 8)) * (1.0 / 4294967296.0);
                    sp.z = static_cast<double>(rd<int64_t>(t, 16)) * (1.0 / 4294967296.0);
                    return base_index_at(run.bases, sp);
                },
                [&](KilnEntity e, std::unordered_map<uint32_t, uint64_t>* m) { count_pages(run.a, e, m); }, &lent);
    std::vector<KilnEntity> magic;
    for (uint64_t i = 0; i < stock[1] && stock[0]; ++i) magic.push_back(reinterpret_cast<const uint32_t*>(stock[0])[i]);
    const auto items = input_items(rc);
    const ChestUse named = chest_sources(
        q.chests, q.chest_n, r.station, base, items.data(), static_cast<uint32_t>(items.size()),
        [&](KilnEntity e) {
            ChestFacts f;
            const uint8_t* t = other(run, run.acc_transform, e, 24);
            f.present = t != nullptr;
            if (t) {
                KilnVec3 sp{};
                sp.x = static_cast<double>(rd<int64_t>(t, 0)) * (1.0 / 4294967296.0);
                sp.y = static_cast<double>(rd<int64_t>(t, 8)) * (1.0 / 4294967296.0);
                sp.z = static_cast<double>(rd<int64_t>(t, 16)) * (1.0 / 4294967296.0);
                f.base = base_index_at(run.bases, sp);
            }
            f.station = other(run, run.acc_factory, e, static_cast<uint64_t>(G.fs_size)) != nullptr;
            f.magic = magic_listed(magic, e);
            PageSet pages;
            bool init = false;
            f.inventory = read_pages(run.a, e, &pages, &init) && init;
            uint8_t flags = 0;
            f.person = f.inventory && (!setup_flags(run.a, e, &flags) || setup_is_person(flags));
            return f;
        },
        [&](KilnEntity e, std::unordered_map<uint32_t, uint64_t>* m) { count_pages(run.a, e, m); });
    if (named.refused) return finish_text(res, KILN_STATION_REFUSED, named.why);
    // GitHub #132: the player's fill-opted linked sources, revalidated at the transaction through its own accessors: the
    // player's CanAccessInventories and login (source_access), an initialized inventory that is no person's
    LinkUse links;
    const bool linked = fill_links_wanted(r);
    if (linked) {
        std::unordered_set<uint32_t> skip{r.station};
        skip.insert(magic.begin(), magic.end());
        skip.insert(named.ids.begin(), named.ids.end());
        for (const auto& [item, by] : lent.by) skip.insert(by.begin(), by.end());
        std::unordered_map<uint32_t, std::unordered_map<uint32_t, uint64_t>> held;
        links = links_for(craft_sources_copy(r.player), &CraftSource::fills, r.player, items, skip,
                          [&](uint32_t pl, uint32_t c) {
                              if (!source_access(run.bases, pl, c)) return false;
                              PageSet pages{};
                              bool init = false;
                              uint8_t flags = 0;
                              return read_pages(run.a, c, &pages, &init) && init && setup_flags(run.a, c, &flags) && !setup_is_person(flags);
                          },
                          [&](uint32_t c, uint32_t item) -> uint64_t {
                              auto it = held.find(c);
                              if (it == held.end()) {
                                  it = held.emplace(c, std::unordered_map<uint32_t, uint64_t>{}).first;
                                  count_pages(run.a, c, &it->second);
                              }
                              const auto n = it->second.find(item);
                              return n == it->second.end() ? 0 : n->second;
                          });
    }
    plan_inputs(r, q.keep, rc, have, stocked, bag, q.chest_n ? &named.count : nullptr, outs ? &lent : nullptr, &p, linked ? &links : nullptr);
    const uint32_t charges = rd<uint32_t>(recipe, G.recipe_water);
    {
        int32_t cur = 0, max = 0;
        water_of(r.station, run.a.attributes, &cur, &max);
        uint64_t water_stock = 0;
        for (uint64_t i = 0; i < stock[3] && stock[2]; ++i) {
            int32_t c = 0, m = 0;
            if (water_of(reinterpret_cast<const uint32_t*>(stock[2])[i], run.a.attributes, &c, &m) && c > 0) water_stock += static_cast<uint64_t>(c);
        }
        InputNow w{};
        if (water_input(charges, (r.flags & KILN_STATION_NO_WATER) != 0, static_cast<uint64_t>(std::max(cur, 0)),
                        static_cast<uint64_t>(std::max(max, 0)), water_stock, &w))
            p.in.push_back(w);
    }
    finish_plan(r, &p);
    res->crafts_before = res->crafts_after = p.loaded;
    res->crafts_requested = p.wanted;
    auto set_limit = [&](const Limit& l) {
        res->limited_by = l.why;
        res->short_item = l.why == KILN_STATION_LIMIT_STOCK || l.why == KILN_STATION_LIMIT_RESERVE ? l.item : 0;
    };
    if (p.status != KILN_STATION_DONE && p.status != KILN_STATION_PARTIAL) {
        if (p.status != KILN_STATION_UNCHANGED) set_limit(p.limit);
        return finish_text(res, p.status, p.why);
    }
    // the game's inputs as AutoTransferRecipe takes them: {RecipeItemStack 24 B*, count}
    const uint64_t inputs[2] = {rc.vec.ptr, rc.vec.count};
    const uint32_t water = (r.flags & KILN_STATION_NO_WATER) ? 0u : charges;
    const bool dry = (r.flags & KILN_STATION_DRY_RUN) != 0;
    alignas(16) uint8_t setup[kSetupBlock];
    setup_for_fill(run, r.player, pos, setup);
    char fault[128] = "";
    uint64_t from_outputs = 0, from_chests = 0, from_linked = 0;
    std::vector<uint32_t> lenders_used;  // the stations a committed fill took from (their screens refresh too)
    const bool layers = outs || q.chest_n != 0 || (linked && !links.empty());
    const Attempts at = run_attempts(r.flags, p.planned, [&](uint32_t n) -> int32_t {
        // ordinary chests, linked sources and station outputs pay only what the player and the Magic chests cannot (none:
        // the plain fill, exactly as before)
        FillShares sh;
        for (auto* v : {&sh.chests, &sh.linked, &sh.outputs, &sh.linked_after}) v->assign(p.in.size(), 0);
        if (layers && !pay_shares(p.in, n, p.loaded, &sh)) return 4;
        const std::vector<uint32_t>& chest_sh = sh.chests;
        const std::vector<uint32_t>& out_sh = sh.outputs;
        const bool take_chests = q.chest_n && any_share(chest_sh);
        const bool lend = outs && any_share(out_sh);
        const bool take_linked = linked && (any_share(sh.linked) || any_share(sh.linked_after));
        E.tx_ctor(g_tx);
        E.tx_init(g_tx, setup);
        unsigned long code = 0;
        auto call = [&](const uint64_t* st, const void* ins, uint32_t crafts, uint32_t w, KilnEntity pl) -> int32_t {
            FillCall f{&run, g_tx, st, ins, r.station, crafts, w, pl, -1};
            // the game's function owns nothing of ours: a fault leaves the transaction to roll back
            if (!guard(&fill_call_impl, &f, &code)) {
                abandon_tx(g_tx);
                std::snprintf(fault, sizeof fault, "the game faulted (0x%08lx) in its fill of station %u", code, r.station);
                return kAttemptFault;
            }
            return f.rc;
        };
        // one resolved stack with count 1, so the game's "loaded" is what the station holds of it and it moves exactly
        // `share` of it. `one` lives for the call.
        auto take_exact = [&](size_t k, uint32_t share, const uint64_t* from) -> int32_t {
            uint32_t j = 0;
            while (j < rc.n && rc.out[j].item != p.in[k].item) ++j;
            if (j >= rc.n || !from || !from[1]) return 4;
            alignas(8) uint8_t one[24];
            std::memcpy(one, rc.vec.inline_ + static_cast<size_t>(j) * 24, 24);
            wr<uint32_t>(one, 0x14, 1u);
            const uint64_t slice[2] = {reinterpret_cast<uint64_t>(one), 1};
            return call(from, slice, share, 0, 0);
        };
        int32_t rc0 = 0;
        uint32_t n_game = n;
        const bool extra = take_chests || lend || take_linked;
        // one input's linked share from that item's links, in their order
        auto take_linked_share = [&](const std::vector<uint32_t>& shares, const std::unordered_map<uint32_t, std::vector<uint32_t>>& by) {
            for (size_t k = 0; k < p.in.size() && rc0 == 0; ++k) {
                if (k >= shares.size() || !shares[k]) continue;
                const auto it = by.find(p.in[k].item);
                if (it == by.end() || it->second.empty()) {
                    rc0 = 4;
                    break;
                }
                const uint64_t from[4] = {reinterpret_cast<uint64_t>(it->second.data()), it->second.size(), 0, 0};
                rc0 = take_exact(k, shares[k], from);
            }
        };
        if (extra) {
            if (take_chests) {
                const uint64_t from[4] = {reinterpret_cast<uint64_t>(named.ids.data()), named.ids.size(), 0, 0};
                for (size_t k = 0; k < p.in.size() && rc0 == 0; ++k)
                    if (chest_sh[k]) rc0 = take_exact(k, chest_sh[k], from);
            }
            if (rc0 == 0 && take_linked) take_linked_share(sh.linked, links.before);
            if (rc0 == 0 && lend) {
                for (size_t k = 0; k < p.in.size() && rc0 == 0; ++k) {
                    if (!out_sh[k]) continue;
                    const auto by = lent.by.find(p.in[k].item);
                    if (by == lent.by.end() || by->second.empty()) {
                        rc0 = 4;
                        break;
                    }
                    const uint64_t from[4] = {reinterpret_cast<uint64_t>(by->second.data()), by->second.size(), 0, 0};
                    rc0 = take_exact(k, out_sh[k], from);
                }
            }
            if (rc0 == 0 && take_linked) take_linked_share(sh.linked_after, links.after);
            n_game = crafts_after_shares(p.in, sh.all(), n, p.loaded);
        }
        if (rc0 == 0 && (!extra || n_game > 0)) rc0 = call(stock, inputs, n_game, water, r.player);
        if (rc0 == kAttemptFault) return kAttemptFault;
        if (rc0 == 0) {
            res->change_count = 0;
            ledger(g_tx, res);
            from_chests = take_chests ? shares_total(chest_sh) : 0;
            from_outputs = lend ? shares_total(out_sh) : 0;
            from_linked = take_linked ? shares_total(sh.linked_all()) : 0;
            lenders_used.clear();
            for (size_t k = 0; lend && k < p.in.size(); ++k)
                if (out_sh[k])
                    if (const auto by = lent.by.find(p.in[k].item); by != lent.by.end())
                        lenders_used.insert(lenders_used.end(), by->second.begin(), by->second.end());
            if (dry) E.rollback(g_tx);
            else E.commit(g_tx);
            E.free_arrays(g_tx);
            return 0;
        }
        E.rollback(g_tx);
        E.free_arrays(g_tx);
        return rc0;
    });
    res->from_outputs = static_cast<uint32_t>(std::min<uint64_t>(from_outputs, UINT32_MAX));
    res->from_chests = static_cast<uint32_t>(std::min<uint64_t>(from_chests, UINT32_MAX));
    res->from_linked = static_cast<uint32_t>(std::min<uint64_t>(from_linked, UINT32_MAX));
    if (fault[0]) {
        switch_off(fault);
        return finish_text(res, KILN_STATION_UNAVAILABLE, fault);
    }
    res->game_error = at.ok ? 0 : at.code;
    const int32_t status = fill_status(at, p.wanted);
    if (at.ok) {
        res->crafts_added = at.n;
        res->crafts_after = p.loaded + at.n;
        if (!dry && G.invalidate && run.factory_info) {
            G.invalidate(run.factory_info, r.station);
            std::sort(lenders_used.begin(), lenders_used.end());
            lenders_used.erase(std::unique(lenders_used.begin(), lenders_used.end()), lenders_used.end());
            for (uint32_t e : lenders_used) G.invalidate(run.factory_info, e);
        }
        if (status == KILN_STATION_DONE) return void(res->status = KILN_STATION_DONE);
        // fewer than wanted: what the plan saw, or the station's room when the game refused more
        if (at.n < p.planned) {
            set_limit(Limit{0, KILN_STATION_LIMIT_ROOM});
            return finish(res, KILN_STATION_PARTIAL, "%u of %u craft(s): the station had no room for more", at.n, p.wanted);
        }
        set_limit(p.limit);
        return finish_text(res, KILN_STATION_PARTIAL, p.why);
    }
    if (status == KILN_STATION_NO_ROOM) {
        set_limit(Limit{0, KILN_STATION_LIMIT_ROOM});
        return finish(res, KILN_STATION_NO_ROOM, "the station has no room for the inputs (the game's code 2)");
    }
    if (status == KILN_STATION_NOT_ENOUGH) {
        set_limit(p.limit.why ? p.limit : Limit{0, KILN_STATION_LIMIT_STOCK});
        return finish(res, KILN_STATION_NOT_ENOUGH, "the base's stock is short (the game's code 4)");
    }
    finish(res, KILN_STATION_REFUSED, "the game's fill refused it (code %u)", static_cast<uint32_t>(at.code));
}

// ---- GitHub #132 (0.2.0-dev.31.16): the per-ingredient transfer (KILN_STATION_FILL_ITEM, docs/re/stations.md §10.9) ----
// The game's own AutoTransferItem, as the press runs it (player_crafting's AutomaticTransfer with an item), once per
// layer inside one transaction: each link group's share from that group's containers with player 0, then the rest from
// the station's base list A with the player (the bag first). Any code rolls everything back. One kiln.log line per
// outcome (g_log_item).
struct ItemFillCall {
    void* tx;
    const uint64_t* stock;  // {u32* ids, count, ...}
    uint32_t item, station, amount, player;
    int32_t rc;
};
void item_fill_call_impl(void* p) {
    auto* f = static_cast<ItemFillCall*>(p);
    f->rc = G.fill_item(f->tx, f->stock, f->item, f->station, static_cast<uint16_t>(f->amount), f->player);
}
LogLimit g_log_item{16};

void run_item_fill(const Run& run, const Req& q, KilnStationResult* res) {
    const KilnStationRequest& r = q.r;
    if (!G.fill_items_ok) return finish(res, KILN_STATION_UNAVAILABLE, "stations.fill_items is off on this build");
    const uint8_t* fsp = other(run, run.acc_factory, r.station, static_cast<uint64_t>(G.fs_size));
    if (!fsp) return finish(res, KILN_STATION_NOT_A_STATION, "entity %u has no FactoryStation", r.station);
    res->state_before = res->state_after = fsp[kFsState];
    res->stopped_before = res->stopped_after = fsp[G.fs_stopped];
    res->recipe = 0;
    const uint8_t* tr = other(run, run.acc_transform, r.station, 24);
    if (!tr) return finish(res, KILN_STATION_NOT_A_STATION, "station %u has no position", r.station);
    float pos[3];
    for (int k = 0; k < 3; ++k) pos[k] = static_cast<float>(static_cast<double>(rd<int64_t>(tr, 8 * k)) * (1.0 / 4294967296.0));
    const int32_t base = base_index_at(run.bases, KilnVec3{pos[0], pos[1], pos[2]});
    if (base < 0) return finish(res, KILN_STATION_NO_BASE, "station %u stands in no base", r.station);
    uint64_t stock[4] = {};  // the base's list A, as BaseStockForPlayer hands it to the game's handler
    {
        const auto* sb = static_cast<const uint8_t*>(run.bases);
        const int64_t off = static_cast<int64_t>(base) * G.stride;
        stock[0] = rd<uint64_t>(sb, G.stock_items + off);
        stock[1] = rd<uint64_t>(sb, G.stock_items_count + off);
    }
    std::unordered_map<uint32_t, uint64_t> have, stocked, bag;
    count_pages(run.a, r.station, &have);
    std::vector<KilnEntity> magic;
    for (uint64_t i = 0; i < stock[1] && stock[0]; ++i) {
        magic.push_back(reinterpret_cast<const uint32_t*>(stock[0])[i]);
        count_pages(run.a, magic.back(), &stocked);
    }
    if (r.player) count_pages(run.a, r.player, &bag);
    // the player's fill-opted links, revalidated here as the recipe fill revalidates them (run_fill)
    LinkUse links;
    const bool linked = fill_links_wanted(r);
    if (linked) {
        std::unordered_set<uint32_t> skip{r.station};
        skip.insert(magic.begin(), magic.end());
        links = links_for(craft_sources_copy(r.player), &CraftSource::fills, r.player, {r.item}, skip,
                          [&](uint32_t pl, uint32_t c) {
                              if (!source_access(run.bases, pl, c)) return false;
                              PageSet pages{};
                              bool init = false;
                              uint8_t flags = 0;
                              return read_pages(run.a, c, &pages, &init) && init && setup_flags(run.a, c, &flags) && !setup_is_person(flags);
                          },
                          [&](uint32_t c, uint32_t item) -> uint64_t {
                              std::unordered_map<uint32_t, uint64_t> held;
                              count_pages(run.a, c, &held);
                              const auto n = held.find(item);
                              return n == held.end() ? 0 : n->second;
                          });
    }
    const uint64_t before_n = have[r.item];
    const ItemFill f = item_fill_plan(r.flags, r.crafts, item_fill_input(r.item, before_n, stocked[r.item] + bag[r.item],
                                                                         links.before_of(r.item), links.after_of(r.item)));
    res->crafts_requested = r.crafts;
    res->crafts_before = res->crafts_after = static_cast<uint32_t>(std::min<uint64_t>(before_n, UINT32_MAX));
    const auto say_line = [&](const char* text) {
        if (const uint32_t n = g_log_item.take()) LOGI("%s%s", text, n > 1 ? " (more held back)" : "");
    };
    if (f.math.status != KILN_STATION_DONE) {
        res->limited_by = KILN_STATION_LIMIT_STOCK;
        res->short_item = r.item;
        say_line(item_fill_text(f, r.item, r.crafts, r.station, r.player).c_str());
        char why[128];
        std::snprintf(why, sizeof why, "the bag, the base's Magic chests%s hold %u of item 0x%08x", linked ? " and the fill-opted links" : "",
                      f.math.affordable, r.item);
        return finish_text(res, KILN_STATION_NOT_ENOUGH, why);
    }
    const bool dry = (r.flags & KILN_STATION_DRY_RUN) != 0;
    alignas(16) uint8_t setup[kSetupBlock];
    setup_for_fill(run, r.player, pos, setup);
    char fault[128] = "";
    unsigned long code = 0;
    E.tx_ctor(g_tx);
    E.tx_init(g_tx, setup);
    auto call = [&](const uint64_t* from, uint32_t amount, KilnEntity pl) -> int32_t {
        ItemFillCall c{g_tx, from, r.item, r.station, amount, pl, -1};
        // the game's function owns nothing of ours: a fault leaves the transaction to roll back
        if (!guard(&item_fill_call_impl, &c, &code)) {
            abandon_tx(g_tx);
            std::snprintf(fault, sizeof fault, "the game faulted (0x%08lx) in its item transfer into station %u", code, r.station);
            return kAttemptFault;
        }
        return c.rc;
    };
    int32_t rc0 = 0;
    const auto take_links = [&](uint32_t share, const std::unordered_map<uint32_t, std::vector<uint32_t>>& by) {
        if (!share || rc0 != 0) return;
        const auto it = by.find(r.item);
        if (it == by.end() || it->second.empty()) {
            rc0 = 4;
            return;
        }
        const uint64_t from[4] = {reinterpret_cast<uint64_t>(it->second.data()), it->second.size(), 0, 0};
        rc0 = call(from, share, 0);
    };
    take_links(f.linked, links.before);
    take_links(f.linked_after, links.after);
    if (rc0 == 0 && f.game) rc0 = call(stock, f.game, r.player);
    if (rc0 == kAttemptFault) {
        switch_off(fault);
        return finish_text(res, KILN_STATION_UNAVAILABLE, fault);
    }
    if (rc0 != 0) {
        E.rollback(g_tx);
        E.free_arrays(g_tx);
        res->game_error = rc0;
        char line[320];
        std::snprintf(line, sizeof line, "stations: player %u's transfer of %u x item 0x%08x into station %u: the game refused it (code %d): nothing moved",
                      r.player, r.crafts, r.item, r.station, rc0);
        say_line(line);
        if (rc0 == 2) {
            res->limited_by = KILN_STATION_LIMIT_ROOM;
            return finish(res, KILN_STATION_NO_ROOM, "the station has no room for %u of item 0x%08x (the game's code 2)", r.crafts, r.item);
        }
        if (rc0 == 4) {
            res->limited_by = KILN_STATION_LIMIT_STOCK;
            res->short_item = r.item;
            return finish(res, KILN_STATION_NOT_ENOUGH, "a source paid less than it was counted to hold (the game's code 4): nothing moved");
        }
        return finish(res, KILN_STATION_REFUSED, "the game's item transfer refused it (code %u)", static_cast<uint32_t>(rc0));
    }
    res->change_count = 0;
    ledger(g_tx, res);
    if (dry) E.rollback(g_tx);
    else E.commit(g_tx);
    E.free_arrays(g_tx);
    res->from_linked = f.linked + f.linked_after;
    res->crafts_added = f.math.planned;
    res->crafts_after = static_cast<uint32_t>(std::min<uint64_t>(before_n + f.math.planned, UINT32_MAX));
    if (!dry && G.invalidate && run.factory_info) G.invalidate(run.factory_info, r.station);
    if (!dry) say_line(item_fill_text(f, r.item, r.crafts, r.station, r.player).c_str());
    res->status = KILN_STATION_DONE;
}

// ---- STOP / RESUME / SELECT inside the program ----
uint8_t* write_factory(const Run& run, KilnEntity e) {
    uint32_t size = 0;
    void* p = g_w->write_other(run.ctx, run.acc_factory, e, &size);
    return p && size >= static_cast<uint32_t>(G.fs_size) ? static_cast<uint8_t*>(p) : nullptr;
}

void run_control(const Run& run, Req& q, KilnStationResult* res, bool* keep_waiting) {
    const KilnStationRequest& r = q.r;
    *keep_waiting = false;
    if (!G.control) return finish(res, KILN_STATION_UNAVAILABLE, "stations.control is off on this build");
    const uint8_t* now = other(run, run.acc_factory, r.station, static_cast<uint64_t>(G.fs_size));
    if (!now) return finish(res, KILN_STATION_NOT_A_STATION, "entity %u has no FactoryStation", r.station);
    res->state_before = res->state_after = now[kFsState];
    res->stopped_before = res->stopped_after = now[G.fs_stopped];
    if (r.action == KILN_STATION_STOP) {
        if (r.flags & KILN_STATION_AFTER_CRAFT) {
            const int64_t start = rd<int64_t>(now, G.fs_start);
            const uint32_t selected = rd<uint32_t>(now, G.fs_next);
            if (!q.waiting) {
                if (now[G.fs_stopped]) return finish(res, KILN_STATION_UNCHANGED, "the station is stopped already");
                q.waiting = true;
                q.start0 = start;
                q.selected0 = selected;
                q.waiting_since = current_step();
            }
            StopSeen seen;
            seen.present = true;
            seen.state = now[kFsState];
            seen.stopped = now[G.fs_stopped];
            seen.running = rd<uint32_t>(now, G.fs_running);
            seen.selected = selected;
            seen.start = start;
            seen.now = g_now.load(std::memory_order_relaxed);
            switch (waiting_stop_step(&q, seen, current_step() - q.waiting_since, kAfterCraftMaxSteps)) {
            case StopWait::Wait:
                *keep_waiting = true;
                return;
            case StopWait::TimedOut:
                return finish(res, KILN_STATION_TIMED_OUT, "the running cycle did not end within about an hour of steps: nothing written");
            case StopWait::Canceled:
                return finish(res, KILN_STATION_CANCELED, "the station's recipe was changed to 0x%08x while the stop waited: nothing written",
                              selected);
            case StopWait::Unchanged:
                return finish(res, KILN_STATION_UNCHANGED, "the station was stopped while the stop waited");
            case StopWait::Gone:
                return finish(res, KILN_STATION_NOT_A_STATION, "entity %u has no FactoryStation", r.station);
            case StopWait::Write:
                break;
            }
        } else if (now[G.fs_stopped]) {
            return finish(res, KILN_STATION_UNCHANGED, "the station is stopped already");
        }
    }
    if (r.action == KILN_STATION_RESUME && !now[G.fs_stopped]) return finish(res, KILN_STATION_UNCHANGED, "the station is not stopped");
    const bool own_choices = r.action == KILN_STATION_SELECT && any_choice(r.choices);
    if (r.action == KILN_STATION_SELECT) {
        if (r.recipe) {
            const void* recipe = find_recipe(r.recipe, run.recipes);
            if (!recipe) return finish(res, KILN_STATION_NO_RECIPE, "recipe 0x%08x is not in the recipe registry", r.recipe);
            const uint8_t* cr = other(run, run.acc_crafting, r.station, kCraftingWorkshop + 4);
            const uint32_t ws = cr ? rd<uint32_t>(cr, kCraftingWorkshop) : 0;
            const uint32_t rw = rd<uint32_t>(recipe, kRecipeWorkshop);
            if (rw != ws) return finish(res, KILN_STATION_WRONG_WORKSHOP, "recipe 0x%08x is a recipe of workshop 0x%08x", r.recipe, rw);
            char why[128];
            if (own_choices && !choices_fit(recipe, r.choices, run.recipes, why, sizeof why)) return finish_text(res, KILN_STATION_REFUSED, why);
        } else if (own_choices) {
            return finish(res, KILN_STATION_REFUSED, "category choices without a recipe");
        }
        // all-zero choices keep the station's own
        const bool same = rd<uint32_t>(now, G.fs_next) == r.recipe && (!own_choices || std::memcmp(now + G.fs_next_sel, r.choices, 32) == 0) &&
                          ((r.flags & KILN_STATION_KEEP_STOPPED) || !now[G.fs_stopped]);
        if (same) return finish(res, KILN_STATION_UNCHANGED, "the station has this recipe selected already");
    }
    if (G.invalidate && run.factory_info) G.invalidate(run.factory_info, r.station);  // as player_crafting, before the write
    uint8_t* w = write_factory(run, r.station);
    if (!w) return finish(res, KILN_STATION_NOT_A_STATION, "entity %u has no FactoryStation to write", r.station);
    switch (r.action) {
    case KILN_STATION_STOP: w[G.fs_stopped] = 1; break;
    case KILN_STATION_RESUME: w[G.fs_stopped] = 0; break;
    case KILN_STATION_SELECT:
        wr<uint32_t>(w, G.fs_next, r.recipe);
        if (own_choices) std::memcpy(w + G.fs_next_sel, r.choices, 32);
        if (!(r.flags & KILN_STATION_KEEP_STOPPED)) w[G.fs_stopped] = 0;
        break;
    default: break;
    }
    res->state_after = w[kFsState];
    res->stopped_after = w[G.fs_stopped];
    noted_write(r.station, q.owner, w, r.action);
    res->status = KILN_STATION_DONE;
}

// ItemInfo.maxStackSize. Unknown (no registry, a fault) is 1, as Items.Get missing is for FindRoomFor.
struct MaxCall {
    void* items;
    uint32_t item;
    uint32_t max;
};
void max_call_impl(void* p) {
    auto* c = static_cast<MaxCall*>(p);
    c->max = 1;
    if (!E.item_find || !c->items || E.item_max_stack <= 0) return;
    const auto* info = static_cast<const uint8_t*>(E.item_find(c->item, c->items));
    if (!info) return;
    const uint16_t m = rd<uint16_t>(info, E.item_max_stack);
    if (m) c->max = m;
}
uint32_t max_stack_of(void* items, uint32_t item) {
    MaxCall c{items, item, 1};
    unsigned long code = 0;
    if (!guard(&max_call_impl, &c, &code)) return 1;
    return c.max;
}

struct TplCall {
    uintptr_t world;
    uint32_t hash;
    uint32_t entity;
    const uint8_t* bytes;
};
void tpl_call_impl(void* p) {
    auto* c = static_cast<TplCall*>(p);
    c->bytes = nullptr;
    if (!G.get_template || !c->world || !c->entity) return;
    c->bytes = static_cast<const uint8_t*>(G.get_template(c->world, c->hash, c->entity));
}

// The template's canReceiveAreaDeposit when it can be read. A fault or a missing reader leaves the bit unknown.
bool template_deposit(KilnEntity e, bool* on) {
    TplCall c{items_server_world(), H_STOCK, e, nullptr};
    unsigned long code = 0;
    if (!guard(&tpl_call_impl, &c, &code) || !c.bytes) return false;
    *on = c.bytes[0] != 0;
    return true;
}

bool chest_takes_deposits(const Run& run, KilnEntity e) {
    DepositRead d;
    const uint8_t* dep = other(run, run.acc_deposit, e, 2);
    if (dep) {
        d.component = true;
        d.override_set = dep[0] != 0;
        d.override_on = dep[1] != 0;
    }
    if (!(d.component && d.override_set)) d.template_known = template_deposit(e, &d.template_on);
    return takes_deposits(d);
}

// A chest that already holds `item` with room, and whether it has a free slot. FindRoomFor's two questions.
ChestOffer chest_offer(const Run& run, KilnEntity e, uint32_t item, uint32_t max_stack) {
    ChestOffer o;
    o.entity = e;
    PageSet pages;
    bool init = false;
    if (!read_pages(run.a, e, &pages, &init) || !init) return o;
    for (uint32_t p = 0; p < pages.n; ++p) {
        for (const Stack& s : pages.p[p].s) {
            if (is_empty(s)) o.free_slot = true;
            else if (s.item == item && s.count < max_stack) o.holds_with_room = true;
        }
    }
    return o;
}

void copy_move_changes(KilnStationResult* res, const KilnItemsMoveResult& mv) {
    for (uint32_t i = 0; i < mv.change_count && res->change_count < KILN_STATIONS_MAX_CHANGES; ++i)
        res->changes[res->change_count++] = mv.changes[i];
}

// Finished stacks into an inventory, or into the base's Magic chests that take deposits. Each stack is kiln.items' move
// (run_move): the same transfer Items.Move uses, one transaction per move, noted for component watchers and counted as
// kiln.items' own moves. A stack counts as moved only when the move is DONE. A partial move keeps the slot and tries a
// later chest. A fault switches the service's actions off, as a faulted fill does (kiln_stations.h KilnStationCollect).
void run_collect(const Run& run, const Req& q, KilnStationResult* res) {
    res->action = KILN_STATION_COLLECT;
    if (!E.slot_transfer || !E.move_ok) return finish(res, KILN_STATION_UNAVAILABLE, "items.move is off on this build");
    const uint8_t* fsp = other(run, run.acc_factory, q.r.station, static_cast<uint64_t>(G.fs_size));
    if (!fsp) return finish(res, KILN_STATION_NOT_A_STATION, "entity %u has no FactoryStation", q.r.station);
    FsBytes fs{};
    std::memcpy(fs.b, fsp, sizeof fs.b);
    res->state_before = res->state_after = fs.b[kFsState];
    res->stopped_before = res->stopped_after = fs.b[G.fs_stopped];
    res->recipe = rd<uint32_t>(fs.b, G.fs_next);
    SourceRecipes roles;
    const bool known = source_recipes(fs, run.recipes, &roles);
    PageSet pages;
    bool init = false;
    if (!read_pages(run.a, q.r.station, &pages, &init) || !init)
        return finish(res, KILN_STATION_NOT_A_STATION, "station %u has no inventory", q.r.station);
    struct Piece {
        uint64_t slot;
        uint32_t item;
    };
    std::vector<Piece> pieces;
    const bool want_out = (q.collect_flags & KILN_STATION_COLLECT_OUTPUTS) != 0;
    const bool want_left = (q.collect_flags & KILN_STATION_COLLECT_LEFTOVERS) != 0;
    for (uint32_t p = 0; p < pages.n; ++p) {
        for (const Stack& s : pages.p[p].s) {
            if (is_empty(s)) continue;
            if (q.collect_item && s.item != q.collect_item) continue;
            const SlotRole role = slot_role(roles, s.item, known);
            if ((role == SlotRole::Output && want_out) || (role == SlotRole::Leftover && want_left)) pieces.push_back({s.slot, s.item});
        }
    }
    if (pieces.empty()) return finish(res, KILN_STATION_UNCHANGED, "nothing to collect");

    std::vector<KilnEntity> chests;
    if (q.collect_flags & KILN_STATION_COLLECT_TO_BASE) {
        const uint8_t* tr = other(run, run.acc_transform, q.r.station, 24);
        if (!tr) return finish(res, KILN_STATION_NOT_A_STATION, "station %u has no position", q.r.station);
        KilnVec3 pos{};
        pos.x = static_cast<double>(rd<int64_t>(tr, 0)) * (1.0 / 4294967296.0);
        pos.y = static_cast<double>(rd<int64_t>(tr, 8)) * (1.0 / 4294967296.0);
        pos.z = static_cast<double>(rd<int64_t>(tr, 16)) * (1.0 / 4294967296.0);
        const int32_t base = base_index_at(run.bases, pos);
        if (base < 0) return finish_text(res, collect_without_base(), "station stands in no base");
        // A missing accessor is not "deposit into every stock entity".
        if (!run.acc_deposit)
            return finish(res, KILN_STATION_UNAVAILABLE, "DynamicInventoryCraftingStock is not readable on this program");
        const auto* sys = static_cast<const uint8_t*>(run.bases);
        const int64_t off = static_cast<int64_t>(base) * G.stride;
        const uint64_t ptr = rd<uint64_t>(sys, G.stock_items + off);
        const uint64_t n = rd<uint64_t>(sys, G.stock_items_count + off);
        for (uint64_t i = 0; i < n && ptr; ++i) {
            const KilnEntity e = reinterpret_cast<const uint32_t*>(ptr)[i];
            if (!chest_takes_deposits(run, e)) continue;
            chests.push_back(e);
        }
    } else {
        chests.push_back(q.collect_to);
    }

    uint32_t finished = 0;
    bool any_moved = false;
    char last[128] = "no container with room";
    const bool to_base = (q.collect_flags & KILN_STATION_COLLECT_TO_BASE) != 0;
    for (const Piece& piece : pieces) {
        std::vector<KilnEntity> order;
        if (to_base) {
            const uint32_t maxs = max_stack_of(run.a.items, piece.item);
            std::vector<ChestOffer> offers;
            offers.reserve(chests.size());
            for (KilnEntity c : chests) offers.push_back(chest_offer(run, c, piece.item, maxs));
            order = collect_chest_order(offers);
        } else {
            order = chests;
        }
        bool done = false;
        bool piece_any = false;
        for (KilnEntity dest : order) {
            KilnItemsMoveRequest mr{};
            mr.size = sizeof mr;
            mr.kind = KILN_MOVE_STACK;
            mr.from = q.r.station;
            mr.to = dest;
            mr.slot = piece.slot;
            MoveJob job{mr};
            KilnItemsMoveResult mv{};
            mv.size = sizeof mv;
            unsigned long code = 0;
            NoteLease note;  // a component watch sees kiln.items as the writer, as for Items.Move
            if (note_wanted()) {
                const KilnEntity holders[2] = {q.r.station, dest};
                note.begin(run.a, holders, 2);
            }
            if (!run_move(run.a, job, g_tx, &mv, &code)) {
                abandon_tx(g_tx);
                char why[160];
                std::snprintf(why, sizeof why, "the game's inventory transaction faulted (0x%08lx) on a collect from %u", code,
                              q.r.station);
                switch_off(why);
                const bool before = finished > 0 || any_moved || piece_any;
                const int32_t st = collect_fault_status(before);
                if (st == KILN_STATION_PARTIAL)
                    std::snprintf(why, sizeof why, "earlier stacks moved, then the inventory transaction faulted (0x%08lx)", code);
                return finish_text(res, st, why);
            }
            if (note) note.end(run.a);
            items_count_move((mv.status == KILN_ITEMS_DONE || mv.status == KILN_ITEMS_PARTIAL) && mv.done > 0);
            if (mv.status == KILN_ITEMS_DONE) {
                copy_move_changes(res, mv);
                done = true;
                piece_any = true;
                break;
            }
            if (mv.status == KILN_ITEMS_PARTIAL && mv.done > 0) {
                copy_move_changes(res, mv);
                piece_any = true;  // the slot still holds the rest: try a later chest
                continue;
            }
            if (mv.message[0]) std::snprintf(last, sizeof last, "%s", mv.message);
        }
        if (done) ++finished;
        else if (piece_any) any_moved = true;
    }
    const int32_t status = collect_status(static_cast<uint32_t>(pieces.size()), finished, any_moved);
    if (status == KILN_STATION_DONE) {
        res->status = KILN_STATION_DONE;
        return;
    }
    finish_text(res, status, last);
}

struct ActCall {
    const Run* run;
    Req* q;
    KilnStationResult* res;
    bool keep_waiting;
};
void act_impl(void* p) {
    auto* a = static_cast<ActCall*>(p);
    if (a->q->r.action == KILN_STATION_FILL && (a->q->r.flags & KILN_STATION_FILL_ITEM)) run_item_fill(*a->run, *a->q, a->res);
    else if (a->q->r.action == KILN_STATION_FILL) run_fill(*a->run, *a->q, a->res);
    else if (a->q->r.action == KILN_STATION_COLLECT) run_collect(*a->run, *a->q, a->res);
    else run_control(*a->run, *a->q, a->res, &a->keep_waiting);
}

struct TickCall {
    KilnProgramCtx* ctx;
    void* balancing;
    int64_t now;
    float speed;
};
void tick_impl(void* p) {
    auto* t = static_cast<TickCall*>(p);
    int64_t v = 0;
    if (G.program_time) t->now = *G.program_time(t->ctx, &v);
    if (G.speed && t->balancing) t->speed = G.speed(t->balancing);
}

const char* action_name(uint32_t a) {
    switch (a) {
    case KILN_STATION_FILL: return "fill";
    case KILN_STATION_STOP: return "stop";
    case KILN_STATION_RESUME: return "resume";
    case KILN_STATION_SELECT: return "select";
    case KILN_STATION_COLLECT: return "collect";
    default: return "?";
    }
}

void KILN_CALL each(KilnProgramCtx* ctx, uint64_t* it, KilnEntity, void*) {
    if (g_off.load()) return;
    if (!resolve_slots()) {
        switch_off("kiln.world did not report kiln_items_stations' iterator slots");
        return;
    }
    {  // the world's time and the speed factor, every step (station_read's progress)
        TickCall t{ctx, reinterpret_cast<void*>(it[g_s_sing[0]]), 0, 0.0f};
        unsigned long code = 0;
        if (guard(&tick_impl, &t, &code)) {
            g_now.store(t.now, std::memory_order_relaxed);
            uint32_t bits;
            std::memcpy(&bits, &t.speed, 4);
            g_speed_bits.store(bits, std::memory_order_relaxed);
        }
    }
    if (g_pending_n.load(std::memory_order_acquire) == 0) return;
    std::vector<Req> batch, after;
    {
        std::lock_guard lock(g_mu);
        const size_t take = std::min<size_t>(g_ready.size(), kPerStep);
        batch.assign(g_ready.begin(), g_ready.begin() + static_cast<ptrdiff_t>(take));
        g_ready.erase(g_ready.begin(), g_ready.begin() + static_cast<ptrdiff_t>(take));
        after = g_after;  // the stops waiting for a cycle's end, every step (g_after keeps them listed: stop_pending)
    }
    if (batch.empty() && after.empty()) return;
    Run run{};
    char why[160] = "";
    const bool ok = verify(ctx, it, &run, why);
    if (!ok) {
        LOGE("stations: kiln_items_stations' iterator: %s", why[0] ? why : "?");
        switch_off(why[0] ? why : "checking kiln_items_stations' iterator faulted");
    } else if (g_state.exchange(KILN_ITEMS_PROGRAM_RUNNING) != KILN_ITEMS_PROGRAM_RUNNING) {
        LOGI("program %s runs: accessors verified", kProgram);
    }
    std::vector<KilnStationResult> done;
    auto act = [&](Req& q, KilnStationResult* res) -> bool {
        if (!ok) {
            finish(res, KILN_STATION_UNAVAILABLE, "kiln_items_stations is off (see kiln.log)");
            return false;
        }
        ActCall a{&run, &q, res, false};
        unsigned long code = 0;
        if (!guard(&act_impl, &a, &code)) {
            abandon_tx(g_tx);
            char b[128];
            std::snprintf(b, sizeof b, "the game faulted (0x%08lx) in an action on station %u", code, q.r.station);
            finish_text(res, KILN_STATION_UNAVAILABLE, b);
            switch_off(b);
            return false;
        }
        return a.keep_waiting;
    };
    auto answer = [&](const Req&, const KilnStationResult& r) {
        KilnStationResult x = r;
        x.step = current_step();
        done.push_back(x);
    };
    std::vector<Req> still = run_batch(batch, std::move(after), act, answer);
    std::lock_guard lock(g_mu);
    for (auto& r : done) g_done.push_back(r);
    g_after = still;
    g_pending_n.store(static_cast<uint32_t>(g_ready.size() + g_after.size() + g_incoming.size()), std::memory_order_release);
}

// ================================================================================ Pre ====

void poll_program() {
    if (!g_w || g_state.load() != KILN_ITEMS_PROGRAM_PENDING) return;
    KilnWorldProgramInfo pi{};
    pi.size = sizeof pi;
    if (g_w->program_info(g_prog, &pi) != KILN_OK) return;
    if (pi.state == KILN_PROGRAM_ACTIVE) {
        int expected = KILN_ITEMS_PROGRAM_PENDING;
        g_state.compare_exchange_strong(expected, KILN_ITEMS_PROGRAM_ACTIVE);
    } else if (pi.state == KILN_PROGRAM_REFUSED || pi.state == KILN_PROGRAM_FAULTED || pi.state == KILN_PROGRAM_SKIPPED) {
        g_state.store(KILN_ITEMS_PROGRAM_NONE);
        char b[160];
        std::snprintf(b, sizeof b, "kiln.world reports %s as state %u (see kiln.log)", kProgram, pi.state);
        lower("stations.fill", KILN_CAP_REASON_FAULTED, b);
        lower("stations.control", KILN_CAP_REASON_FAULTED, b);
        LOGE("stations: %s", b);
    }
}

// SELECT's unlock check: the world (shared progression) and every player in game, by the game's evaluator.
bool unlocked(uint32_t recipe_id, char* why, size_t cap) {
    KilnStationRecipe rec{};
    rec.size = sizeof rec;
    if (recipe_info(recipe_id, &rec) != KILN_OK) return std::snprintf(why, cap, "recipe 0x%08x is not in the recipe registry", recipe_id), false;
    if (rec.knowledge_id == 0) return true;
    if (!g_know) {
        std::snprintf(why, cap, "kiln.knowledge is not loaded: Kiln cannot tell whether anyone has unlocked recipe 0x%08x "
                                "(ask with ALLOW_LOCKED to select it anyway)", recipe_id);
        return false;
    }
    KilnKnowledgeQuery q{rec.knowledge_id, rec.knowledge_value, rec.knowledge_compare, rec.knowledge_type, rec.knowledge_player_only, 0};
    uint8_t v = 0;
    const KilnResult wrc = g_know->query(0, &q, KILN_KNOW_MODE_SHARED, &v);
    const int world = wrc == KILN_OK ? (v ? 1 : 0) : -1;
    std::vector<int> players;
    KilnPlayer ps[KILN_PLAYERS_MAX_SLOTS];
    uint32_t n = 0;
    if (g_players) {
        for (auto& p : ps) p.size = sizeof p;
        g_players->list(0, ps, KILN_PLAYERS_MAX_SLOTS, &n);
        for (uint32_t i = 0; i < std::min<uint32_t>(n, KILN_PLAYERS_MAX_SLOTS); ++i) {
            if (ps[i].state != KILN_PLAYER_IN_GAME) continue;
            uint8_t pv = 0;
            const KilnResult prc = g_know->query(ps[i].entity, &q, KILN_KNOW_MODE_PLAYER, &pv);
            players.push_back(prc == KILN_OK ? (pv ? 1 : 0) : -1);
        }
    }
    switch (unlock_verdict(false, world, players)) {
    case Unlock::Yes: return true;
    case Unlock::No:
        std::snprintf(why, cap, "no one on this server has unlocked recipe 0x%08x (the world and %zu player(s) in game checked)",
                      recipe_id, players.size());
        return false;
    case Unlock::Unknown:
    default:
        std::snprintf(why, cap, "recipe 0x%08x's unlock is a player's and no player is in game to check it (ALLOW_LOCKED selects it anyway)",
                      recipe_id);
        return false;
    }
}

bool guarded_done(KilnStationDoneFn fn, const KilnStationResult* r, void* user) {
    __try {
        fn(r, user);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void deliver() {
    std::vector<KilnStationResult> done;
    std::vector<std::pair<KilnStationResult, Callback>> calls;
    {
        std::lock_guard lock(g_mu);
        done.swap(g_done);
        for (const KilnStationResult& r : done) {
            auto i = g_callbacks.find(r.ticket);
            if (i == g_callbacks.end()) continue;
            calls.emplace_back(r, i->second);
            g_callbacks.erase(i);
        }
    }
    g_done_total.fetch_add(static_cast<uint32_t>(done.size()));
    for (const KilnStationResult& r : done)
        LOGI("stations: ticket %llu: %s station %u recipe 0x%08x: status %d, crafts %u -> %u (+%u), game code %d, %u change(s)%s%s",
             static_cast<unsigned long long>(r.ticket), action_name(r.action), r.station, r.recipe, r.status, r.crafts_before,
             r.crafts_after, r.crafts_added, r.game_error, r.change_count, r.message[0] ? ": " : "", r.message);
    for (auto& [r, cb] : calls)
        if (!guarded_done(cb.fn, &r, cb.user)) LOGE("stations: a result callback faulted (ticket %llu)", static_cast<unsigned long long>(r.ticket));
}

uint32_t bases_fingerprint(const RawBases& rb) {
    uint32_t h = 0x811c9dc5u ^ rb.count;
    for (uint32_t i = 0; i < rb.count; ++i) {
        const auto* p = reinterpret_cast<const uint8_t*>(rb.r[i].min);
        for (int k = 0; k < 24; ++k) h = (h ^ p[k]) * 0x01000193u;
        h = (h ^ rb.r[i].zone_mask) * 0x01000193u;
    }
    return h;
}

void push_event(const KilnStationEvent& ev) {
    std::lock_guard lock(g_mu);
    g_ring.push(ev);
}

// The stations of the world (a scan of the entity map through kiln.world), at most every kScanEvery steps.
void scan_stations(uint64_t step, void* sys, bool events) {
    g_stations_step = step ? step : 1;
    std::vector<KilnEntity> ids(kMaxStations);
    uint32_t n = 0;
    if (!g_w || (g_w->find(H_FACTORY, ids.data(), kMaxStations, &n) != KILN_OK && n == 0)) n = 0;
    ids.resize(std::min(n, kMaxStations));
    for (auto& [e, k] : g_known) k.fresh = false;
    for (KilnEntity e : ids) {
        FsBytes fs{};
        if (!factory_of(e, &fs)) continue;
        KilnVec3 pos{};
        position_of(e, &pos);
        const Snap s = snap_of(fs, workshop_of(e), base_index_at(sys, pos), pos);
        auto it = g_known.find(e);
        if (it == g_known.end()) {
            g_known.emplace(e, Known{s, true});
            if (events && g_scanned) {
                KilnStationEvent ev = state_event(e, s, s);
                ev.kind = KILN_STATION_EV_PLACED;
                ev.step = step;
                ev.base = base_id_of(s.base);
                push_event(ev);
            }
        } else {
            it->second.fresh = true;
            it->second.snap.base = s.base;
            it->second.snap.pos = s.pos;
            it->second.snap.workshop = s.workshop;
        }
    }
    for (auto it = g_known.begin(); it != g_known.end();) {
        if (!it->second.fresh) {
            if (events && g_scanned) {
                KilnStationEvent ev = state_event(it->first, it->second.snap, it->second.snap);
                ev.kind = KILN_STATION_EV_REMOVED;
                ev.step = step;
                ev.base = base_id_of(it->second.snap.base);
                push_event(ev);
            }
            it = g_known.erase(it);
        } else {
            ++it;
        }
    }
    g_stations = ids;
    g_scanned = true;
    g_station_count.store(static_cast<uint32_t>(ids.size()), std::memory_order_relaxed);
    g_scanned_any.store(true, std::memory_order_relaxed);
}

void watch_pre(uint64_t step) {
    const bool watching = g_watchers.load() > 0;
    RawBases rb{};
    const bool have_bases = read_bases(&rb);
    void* sys = have_bases ? rb.sys : nullptr;
    bool rebuilt = false;
    if (have_bases) {
        const uint32_t sig = bases_fingerprint(rb);
        if (sig != g_bases_sig) {
            rebuilt = true;
            const bool first = g_bases_sig == 0;
            g_bases_sig = sig;
            ++g_bases_version;
            g_altars_step = 0;  // rescan the altars now
            if (!first && watching) {
                KilnStationEvent ev{};
                ev.size = sizeof ev;
                ev.kind = KILN_STATION_EV_BASES;
                ev.base_index = static_cast<int32_t>(rb.count);
                ev.step = step;
                push_event(ev);
            }
        }
    }
    if (!watching) return;
    if (scan_due(g_scanned, step, g_stations_step, kScanEvery, rebuilt)) {  // a rebuild moves the stations' base indices
        scan_altars(step, sys);
        scan_stations(step, sys, true);
    }
    for (auto& [e, k] : g_known) {
        FsBytes fs{};
        if (!factory_of(e, &fs)) continue;  // gone: the next scan reports it
        Snap s = snap_of(fs, k.snap.workshop, k.snap.base, k.snap.pos);
        if (s == k.snap) continue;
        KilnStationEvent ev = state_event(e, k.snap, s);
        ev.base = base_id_of(s.base);
        {
            // the owner of a Kiln write that made exactly this change (its stop flag or its selection), in the step before
            std::lock_guard lock(g_mu);
            auto w = g_wrote.find(e);
            if (w != g_wrote.end() && step <= w->second.step + 2) {
                const Wrote& x = w->second;
                const bool flag = x.sets_stopped && s.stopped != k.snap.stopped && s.stopped == x.stopped;
                const bool sel = x.sets_selected && s.selected != k.snap.selected && s.selected == x.selected;
                if (flag || sel) {
                    ev.by = x.owner;
                    g_wrote.erase(w);  // once
                }
            }
        }
        ev.step = step;
        push_event(ev);
        k.snap = s;
    }
}

// ================================================================================ the table ====

bool tick() { return g_api && g_api->on_tick_thread(); }

KilnResult read_gate() {
    if (!tick()) return KILN_ERR_WRONG_THREAD;
    if (!G.read || !here()) return KILN_ERR_UNAVAILABLE;
    return KILN_OK;
}

KilnResult KILN_CALL t_status(KilnStationsStatus* out) {
    if (!out || out->size < sizeof(KilnStationsStatus)) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    const int state = g_state.load();
    const bool live = here();
    const bool prog = state != KILN_ITEMS_PROGRAM_NONE && state != KILN_ITEMS_PROGRAM_BROKEN && !g_off.load();
    const StatusBits on = status_bits(G.read, G.fill_ok, G.control, G.events, live, prog);
    out->read = on.read ? 1 : 0;
    out->fill = on.fill ? 1 : 0;
    out->control = on.control ? 1 : 0;
    out->events = on.events ? 1 : 0;
    out->program = static_cast<uint8_t>(state);
    out->watching = g_watchers.load() > 0 ? 1 : 0;
    out->knowledge = g_know ? 1 : 0;
    out->stations = g_station_count.load(std::memory_order_relaxed);
    out->scanned = g_scanned_any.load(std::memory_order_relaxed) ? 1 : 0;
    out->pending = g_pending_n.load();
    out->done = g_done_total.load();
    std::lock_guard lock(g_mu);
    if (!live) std::snprintf(out->detail, sizeof out->detail, "no server world here (a joined client, or no world yet)");
    else if (!(out->read && out->fill && out->control)) std::snprintf(out->detail, sizeof out->detail, "%s", g_detail);
    return KILN_OK;
}

KilnResult KILN_CALL t_base_count(uint32_t* out) {
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    *out = 0;
    if (KilnResult g = read_gate(); g != KILN_OK) return g;
    RawBases rb{};
    if (!read_bases(&rb)) return KILN_ERR_UNAVAILABLE;
    *out = rb.count;
    return KILN_OK;
}

KilnResult base_fill(uint32_t index, KilnStationBase* out, const RawBases& rb) {
    if (index >= rb.count) return KILN_ERR_NOT_FOUND;
    scan_altars(current_step(), rb.sys);
    if (!g_scanned) scan_stations(current_step(), rb.sys, false);
    fill_base(rb, index, out);
    return KILN_OK;
}

KilnResult KILN_CALL t_base_at(uint32_t index, KilnStationBase* out) {
    if (!out || out->size < sizeof(KilnStationBase)) return KILN_ERR_INVALID_ARGUMENT;
    if (KilnResult g = read_gate(); g != KILN_OK) return g;
    RawBases rb{};
    if (!read_bases(&rb)) return KILN_ERR_UNAVAILABLE;
    return base_fill(index, out, rb);
}

KilnResult KILN_CALL t_base_at_position(const KilnVec3* p, KilnStationBase* out) {
    if (!p || !out || out->size < sizeof(KilnStationBase)) return KILN_ERR_INVALID_ARGUMENT;
    if (KilnResult g = read_gate(); g != KILN_OK) return g;
    RawBases rb{};
    if (!read_bases(&rb)) return KILN_ERR_UNAVAILABLE;
    const int32_t i = base_index_at(rb.sys, *p);
    if (i < 0) return KILN_ERR_NOT_FOUND;
    return base_fill(static_cast<uint32_t>(i), out, rb);
}

KilnResult KILN_CALL t_base_of(KilnEntity e, KilnStationBase* out) {
    if (!e || !out || out->size < sizeof(KilnStationBase)) return KILN_ERR_INVALID_ARGUMENT;
    if (KilnResult g = read_gate(); g != KILN_OK) return g;
    KilnVec3 p{};
    if (!position_of(e, &p)) return KILN_ERR_NOT_FOUND;
    return t_base_at_position(&p, out);
}

KilnResult list_out(const std::vector<KilnEntity>& v, KilnEntity* out, uint32_t capacity, uint32_t* out_count) {
    *out_count = static_cast<uint32_t>(v.size());
    const uint32_t n = std::min<uint32_t>(capacity, *out_count);
    if (n && out) std::memcpy(out, v.data(), n * sizeof(KilnEntity));
    return *out_count > capacity ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

KilnResult base_list_out(uint32_t index, bool water, KilnEntity* out, uint32_t capacity, uint32_t* out_count) {
    if (!out_count || (capacity && !out)) return KILN_ERR_INVALID_ARGUMENT;
    *out_count = 0;
    if (KilnResult g = read_gate(); g != KILN_OK) return g;
    RawBases rb{};
    if (!read_bases(&rb)) return KILN_ERR_UNAVAILABLE;
    if (index >= rb.count) return KILN_ERR_NOT_FOUND;
    std::vector<KilnEntity> v;
    if (!base_list(rb, index, water, &v)) return KILN_ERR_INTERNAL;
    return list_out(v, out, capacity, out_count);
}
KilnResult KILN_CALL t_base_storage(uint32_t i, KilnEntity* out, uint32_t cap, uint32_t* n) { return base_list_out(i, false, out, cap, n); }
KilnResult KILN_CALL t_base_water(uint32_t i, KilnEntity* out, uint32_t cap, uint32_t* n) { return base_list_out(i, true, out, cap, n); }

KilnResult KILN_CALL t_base_stock(uint32_t index, KilnStationStack* out, uint32_t capacity, uint32_t* out_count) {
    if (!out_count || (capacity && !out)) return KILN_ERR_INVALID_ARGUMENT;
    *out_count = 0;
    if (KilnResult g = read_gate(); g != KILN_OK) return g;
    RawBases rb{};
    if (!read_bases(&rb)) return KILN_ERR_UNAVAILABLE;
    if (index >= rb.count) return KILN_ERR_NOT_FOUND;
    std::vector<KilnEntity> v;
    if (!base_list(rb, index, false, &v)) return KILN_ERR_INTERNAL;
    std::unordered_map<uint32_t, uint64_t> sum;
    for (KilnEntity c : v) count_items(c, &sum);
    std::vector<KilnStationStack> rows;
    for (auto& [item, count] : sum) rows.push_back({item, static_cast<uint32_t>(std::min<uint64_t>(count, UINT32_MAX))});
    std::sort(rows.begin(), rows.end(), [](const KilnStationStack& a, const KilnStationStack& b) { return a.item < b.item; });
    *out_count = static_cast<uint32_t>(rows.size());
    const uint32_t n = std::min<uint32_t>(capacity, *out_count);
    if (n) std::memcpy(out, rows.data(), n * sizeof(KilnStationStack));
    return *out_count > capacity ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

KilnResult KILN_CALL t_stations(int32_t base, KilnEntity* out, uint32_t capacity, uint32_t* out_count) {
    if (!out_count || (capacity && !out)) return KILN_ERR_INVALID_ARGUMENT;
    *out_count = 0;
    if (KilnResult g = read_gate(); g != KILN_OK) return g;
    const uint64_t step = current_step();
    RawBases rb{};
    read_bases(&rb);
    if (!g_scanned || step - g_stations_step >= kScanEvery) scan_stations(step, rb.sys, g_watchers.load() > 0);
    if (base < 0) return list_out(g_stations, out, capacity, out_count);
    std::vector<KilnEntity> v;
    for (KilnEntity e : g_stations) {
        auto k = g_known.find(e);
        if (k != g_known.end() && k->second.snap.base == base) v.push_back(e);
    }
    return list_out(v, out, capacity, out_count);
}

KilnResult KILN_CALL t_station_read(KilnEntity e, KilnStation* out) {
    if (!e || !out || out->size < sizeof(KilnStation)) return KILN_ERR_INVALID_ARGUMENT;
    if (KilnResult g = read_gate(); g != KILN_OK) return g;
    FsBytes fs{};
    if (!factory_of(e, &fs)) return KILN_ERR_NOT_FOUND;
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    out->entity = e;
    out->workshop = workshop_of(e);
    position_of(e, &out->position);
    RawBases rb{};
    if (read_bases(&rb)) {
        out->base_index = base_index_at(rb.sys, out->position);
        if (out->base_index >= 0) {
            scan_altars(current_step(), rb.sys);
            KilnStationBase b{};
            b.size = sizeof b;
            fill_base(rb, static_cast<uint32_t>(out->base_index), &b);
            out->base = b.id;
        }
    } else {
        out->base_index = -1;
    }
    out->state = fs.b[kFsState];
    out->stopped_by_player = fs.b[G.fs_stopped];
    out->use_all_options = fs.b[G.fs_use_all];
    out->needs = fs.b[G.fs_missing];
    out->running_recipe = rd<uint32_t>(fs.b, G.fs_running);
    out->selected_recipe = rd<uint32_t>(fs.b, G.fs_next);
    std::memcpy(out->running_choices, fs.b + G.fs_running_sel, 32);
    std::memcpy(out->selected_choices, fs.b + G.fs_next_sel, 32);
    out->recipe_start_ns = rd<int64_t>(fs.b, G.fs_start);
    out->pause_ns = rd<int64_t>(fs.b, G.fs_pause);
    out->now_ns = g_now.load(std::memory_order_relaxed);
    void* attributes = singleton("impact::AttributeRegistry");
    int32_t cur = 0, max = 0;
    if (water_of(e, attributes, &cur, &max)) {
        out->water = cur;
        out->water_max = max;
    }
    void* reg = recipes_registry();
    // loaded crafts: of the selected recipe, its water counted as the game counts it
    if (const void* recipe = out->selected_recipe ? find_recipe(out->selected_recipe, reg) : nullptr) {
        ResolveCall rc{};
        if (resolve_inputs(recipe, reinterpret_cast<const KilnStationChoice*>(fs.b + G.fs_next_sel), reg, &rc)) {
            std::unordered_map<uint32_t, uint64_t> have;
            count_items(e, &have);
            std::vector<InputNow> in;
            for (uint32_t i = 0; i < rc.n; ++i) in.push_back({rc.out[i].item, rc.out[i].count, have[rc.out[i].item], 0});
            merge_inputs(&in);
            InputNow w{};
            if (water_input(rd<uint32_t>(recipe, G.recipe_water), false, static_cast<uint64_t>(std::max(cur, 0)),
                            static_cast<uint64_t>(std::max(max, 0)), 0, &w))
                in.push_back(w);
            out->crafts_loaded = crafts_loaded(in);
        }
    }
    // the duration and progress of the cycle in progress (its own recipe), else the selected recipe's duration
    const uint32_t shown = out->running_recipe ? out->running_recipe : out->selected_recipe;
    if (const void* recipe = shown ? find_recipe(shown, reg) : nullptr) out->duration_ns = scaled(rd<int64_t>(recipe, G.recipe_duration));
    if (cycle_in_progress(out->state, out->running_recipe) && out->duration_ns > 0) {
        double f = 0.0;
        if (out->state == KILN_STATION_STATE_PAUSED) f = static_cast<double>(out->pause_ns) / static_cast<double>(out->duration_ns);
        else if (out->now_ns > 0 && out->recipe_start_ns > 0)
            f = static_cast<double>(out->now_ns - out->recipe_start_ns) / static_cast<double>(out->duration_ns);
        out->progress = static_cast<float>(std::clamp(f, 0.0, 1.0));
    }
    {
        std::lock_guard lock(g_mu);
        auto waits = [&](const std::vector<Req>& l) {
            return std::any_of(l.begin(), l.end(), [&](const Req& q) {
                return q.r.station == e && q.r.action == KILN_STATION_STOP && (q.r.flags & KILN_STATION_AFTER_CRAFT);
            });
        };
        if (waits(g_after) || waits(g_ready) || waits(g_incoming)) out->flags |= KILN_STATION_FLAG_STOP_PENDING;
    }
    return KILN_OK;
}

KilnResult KILN_CALL t_recipe(uint32_t id, KilnStationRecipe* out) {
    if (!id || !out || out->size < sizeof(KilnStationRecipe)) return KILN_ERR_INVALID_ARGUMENT;
    if (KilnResult g = read_gate(); g != KILN_OK) return g;
    return recipe_info(id, out);
}

KilnResult KILN_CALL t_recipe_inputs(uint32_t id, const KilnStationChoice* choices, KilnStationStack* out, uint32_t capacity,
                                     uint32_t* out_count) {
    if (!id || !out_count || (capacity && !out)) return KILN_ERR_INVALID_ARGUMENT;
    *out_count = 0;
    if (KilnResult g = read_gate(); g != KILN_OK) return g;
    void* reg = recipes_registry();
    const void* recipe = find_recipe(id, reg);
    if (!recipe) return KILN_ERR_NOT_FOUND;
    static const KilnStationChoice none[4] = {};
    ResolveCall rc{};
    if (!resolve_inputs(recipe, choices ? choices : none, reg, &rc)) return KILN_ERR_INTERNAL;
    *out_count = rc.n;
    const uint32_t n = std::min(capacity, rc.n);
    if (n) std::memcpy(out, rc.out, n * sizeof(KilnStationStack));
    return rc.n > capacity ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

struct CatCall {
    void* reg;
    uint32_t want_id;   // category(): this id
    uint32_t want_at;   // category_at(): the index-th in map order
    bool by_index;
    uint32_t found_id;
    KilnItemId* items;
    float* scaling;
    uint32_t cap, n;
    bool found;
};
void cat_impl(void* p) {
    auto* c = static_cast<CatCall*>(p);
    const auto* r = static_cast<const uint8_t*>(c->reg);
    const auto* bits = rd<const uint64_t*>(r, G.cat_bits);
    const uint64_t cap = rd<uint64_t>(r, G.cat_cap);
    const auto* keys = rd<const uint32_t*>(r, G.cat_keys);
    const auto* values = rd<const uint8_t* const*>(r, G.cat_values);
    if (!bits || !keys || !values || cap > (1u << 20)) return;
    uint32_t seen = 0;
    for (uint64_t i = 0; i < cap; ++i) {
        if (!((bits[i >> 6] >> (i & 63)) & 1)) continue;
        const uint32_t id = keys[i];
        const bool hit = c->by_index ? seen++ == c->want_at : id == c->want_id;
        if (!hit) continue;
        c->found = true;
        c->found_id = id;
        const uint8_t* cat = values[i];
        if (!cat || c->by_index) return;
        const int32_t rel = rd<int32_t>(cat, 4);
        const uint32_t n = rd<uint32_t>(cat, 8);
        const uint8_t* it = cat + 4 + rel;
        c->n = n;
        for (uint32_t k = 0; k < n && k < c->cap && rel; ++k) {
            c->items[k] = rd<uint32_t>(it, 8 * k);
            if (c->scaling) c->scaling[k] = rd<float>(it, 8 * k + 4);
        }
        return;
    }
}

bool category_options(void* reg, uint32_t id, uint32_t* n) {
    CatCall c{reg, id, 0, false, 0, nullptr, nullptr, 0, 0, false};
    unsigned long code = 0;
    if (!reg || !guard(&cat_impl, &c, &code) || !c.found) return false;
    *n = c.n;
    return true;
}

// A station's recipes as the classification needs them (selected and running), from the registry `reg`. Tick thread, or
// inside the program (the registry is only read).
bool source_recipes(const FsBytes& fs, void* reg, SourceRecipes* out) {
    out->outputs.clear();
    out->inputs.clear();
    const uint32_t ids[2] = {rd<uint32_t>(fs.b, G.fs_next), rd<uint32_t>(fs.b, G.fs_running)};
    bool any = false;
    for (int k = 0; k < 2; ++k) {
        if (!ids[k] || (k == 1 && ids[1] == ids[0])) continue;
        static thread_local KilnStationRecipe rec;
        std::memset(&rec, 0, sizeof rec);
        rec.size = sizeof rec;
        RecipeCall c{ids[k], reg, &rec, false};
        unsigned long code = 0;
        if (!reg || !G.recipe_find || !guard(&recipe_impl, &c, &code)) return false;  // unreadable: lends nothing
        if (!c.found) continue;
        any = true;
        for (uint32_t i = 0; i < rec.output_count; ++i) out->outputs.push_back(rec.outputs[i].item);
        for (uint32_t i = 0; i < rec.input_count; ++i) {
            if (rec.inputs[i].item) out->inputs.push_back(rec.inputs[i].item);
            if (!rec.inputs[i].category) continue;
            KilnItemId items[64];
            CatCall cc{reg, rec.inputs[i].category, 0, false, 0, items, nullptr, 64, 0, false};
            if (!guard(&cat_impl, &cc, &code) || !cc.found || cc.n > 64) return false;  // a category it cannot list: lends nothing
            for (uint32_t o = 0; o < cc.n; ++o) out->inputs.push_back(items[o]);
        }
    }
    return any;
}

// Finished output stacks still in a station (Bases.StockWithStations: StationInfo.Outputs). No recipe: nothing.
void add_output_stacks(KilnEntity e, std::unordered_map<uint32_t, uint64_t>* into) {
    FsBytes fs{};
    if (!factory_of(e, &fs)) return;
    SourceRecipes roles;
    if (!source_recipes(fs, recipes_registry(), &roles)) return;
    static thread_local std::vector<KilnItemsSlot> slots;
    slots.resize(8 * KILN_ITEMS_MAX_INVENTORIES);
    ReadOut ro{};
    ro.info.size = sizeof ro.info;
    ro.slots = slots.data();
    ro.capacity = static_cast<uint32_t>(slots.size());
    unsigned long code = 0;
    if (!read_inventory(items_server_world(), singleton("g38_items::ItemRegistry"), e, &ro, &code) || !ro.found) return;
    for (uint32_t i = 0; i < ro.written; ++i) {
        if (!slots[i].item || !slots[i].count) continue;
        if (slot_role(roles, slots[i].item, true) != SlotRole::Output) continue;
        (*into)[slots[i].item] += slots[i].count;
    }
}

KilnResult KILN_CALL t_base_stock_with_stations(uint32_t index, KilnStationStack* out, uint32_t capacity, uint32_t* out_count) {
    if (!out_count || (capacity && !out)) return KILN_ERR_INVALID_ARGUMENT;
    *out_count = 0;
    if (KilnResult g = read_gate(); g != KILN_OK) return g;
    RawBases rb{};
    if (!read_bases(&rb)) return KILN_ERR_UNAVAILABLE;
    if (index >= rb.count) return KILN_ERR_NOT_FOUND;
    const uint64_t step = current_step();
    if (!g_scanned || step - g_stations_step >= kScanEvery) scan_stations(step, rb.sys, g_watchers.load() > 0);
    std::vector<KilnEntity> v;
    if (!base_list(rb, index, false, &v)) return KILN_ERR_INTERNAL;
    std::unordered_map<uint32_t, uint64_t> sum;
    for (KilnEntity c : v) count_items(c, &sum);
    for (KilnEntity e : g_stations) {
        auto k = g_known.find(e);
        if (k != g_known.end() && k->second.snap.base == static_cast<int32_t>(index)) add_output_stacks(e, &sum);
    }
    std::vector<KilnStationStack> rows;
    for (auto& [item, count] : sum) rows.push_back({item, static_cast<uint32_t>(std::min<uint64_t>(count, UINT32_MAX))});
    std::sort(rows.begin(), rows.end(), [](const KilnStationStack& a, const KilnStationStack& b) { return a.item < b.item; });
    *out_count = static_cast<uint32_t>(rows.size());
    const uint32_t n = std::min<uint32_t>(capacity, *out_count);
    if (n) std::memcpy(out, rows.data(), n * sizeof(KilnStationStack));
    return *out_count > capacity ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

KilnResult KILN_CALL t_category(uint32_t id, KilnItemId* items, float* scaling, uint32_t capacity, uint32_t* out_count) {
    if (!id || !out_count || (capacity && !items)) return KILN_ERR_INVALID_ARGUMENT;
    *out_count = 0;
    if (KilnResult g = read_gate(); g != KILN_OK) return g;
    CatCall c{recipes_registry(), id, 0, false, 0, items, scaling, capacity, 0, false};
    unsigned long code = 0;
    if (!c.reg || !guard(&cat_impl, &c, &code)) return KILN_ERR_UNAVAILABLE;
    if (!c.found) return KILN_ERR_NOT_FOUND;
    *out_count = c.n;
    return c.n > capacity ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

KilnResult KILN_CALL t_category_at(uint32_t index, uint32_t* out_id) {
    if (!out_id) return KILN_ERR_INVALID_ARGUMENT;
    *out_id = 0;
    if (KilnResult g = read_gate(); g != KILN_OK) return g;
    CatCall c{recipes_registry(), 0, index, true, 0, nullptr, nullptr, 0, 0, false};
    unsigned long code = 0;
    if (!c.reg || !guard(&cat_impl, &c, &code)) return KILN_ERR_UNAVAILABLE;
    if (!c.found) return KILN_ERR_NOT_FOUND;
    *out_id = c.found_id;
    return KILN_OK;
}

KilnResult KILN_CALL t_workshop_recipes(uint32_t workshop, uint32_t* out, uint32_t capacity, uint32_t* out_count) {
    if (!out_count || (capacity && !out)) return KILN_ERR_INVALID_ARGUMENT;
    *out_count = 0;
    if (KilnResult g = read_gate(); g != KILN_OK) return g;
    if (!g_gameplay || !g_gameplay->workshop_recipes) return KILN_ERR_UNAVAILABLE;
    return g_gameplay->workshop_recipes(workshop, out, capacity, out_count);
}

KilnResult KILN_CALL t_plan(const KilnStationRequest* r, KilnStationPlan* out) {
    // a caller built before 0.2.0-dev.29.17 passes the plan without `outputs`: it gets everything up to message
    constexpr uint32_t kPlanV1 = static_cast<uint32_t>(offsetof(KilnStationPlan, outputs));
    constexpr uint32_t kPlanChests = static_cast<uint32_t>(offsetof(KilnStationPlan, chests));
    constexpr uint32_t kPlanLinked = static_cast<uint32_t>(offsetof(KilnStationPlan, linked));  // a caller before 31.12
    if (!r || !out || out->size < kPlanV1) return KILN_ERR_INVALID_ARGUMENT;
    if (r->action != KILN_STATION_FILL || !request_valid(*r)) return KILN_ERR_INVALID_ARGUMENT;
    if ((r->flags & KILN_STATION_FILL_ITEM) && (!G.fill_items_ok || g_off.load())) return KILN_ERR_UNAVAILABLE;
    if (KilnResult g = read_gate(); g != KILN_OK) return g;
    const uint32_t size = out->size;
    const bool with_outputs = size >= kPlanChests;
    const bool with_chests = size >= kPlanLinked;
    const bool with_linked = size >= sizeof(KilnStationPlan);
    const uint32_t clear = with_linked ? static_cast<uint32_t>(sizeof(KilnStationPlan)) : with_chests ? kPlanLinked
                           : with_outputs ? kPlanChests : kPlanV1;
    std::memset(out, 0, clear);
    out->size = size;
    out->station = r->station;
    PlanOut p;
    const bool named = r->size >= sizeof(KilnStationRequest);
    if (r->flags & KILN_STATION_FILL_ITEM) plan_item_tick(*r, &p);
    else plan_tick(*r, named ? r->chests : nullptr, named ? r->chest_count : 0, &p);
    out->status = p.status;
    out->recipe = p.recipe;
    out->base_index = p.base;
    out->crafts_loaded = p.loaded;
    out->crafts_affordable = p.affordable;
    out->crafts_planned = p.status == KILN_STATION_DONE || p.status == KILN_STATION_PARTIAL ? p.planned : 0;
    out->limited_by = p.limit.why;
    out->short_item = p.limit.why == KILN_STATION_LIMIT_STOCK || p.limit.why == KILN_STATION_LIMIT_RESERVE ? p.limit.item : 0;
    out->use_all_options = p.use_all;
    out->water_per_craft = p.water_per;
    out->water_have = static_cast<uint32_t>(std::min<uint64_t>(p.water_have, UINT32_MAX));
    out->water_stock = static_cast<uint32_t>(std::min<uint64_t>(p.water_stock, UINT32_MAX));
    for (const InputNow& x : p.in) {
        if (x.item == kWaterItem || out->input_count >= KILN_STATIONS_MAX_INPUTS) continue;  // water: its own fields
        const uint32_t i = out->input_count++;
        out->inputs[i] = {x.item, x.count};
        out->have[i] = static_cast<uint32_t>(std::min<uint64_t>(x.have, UINT32_MAX));
        uint64_t rest = x.stock;
        const uint64_t out_n = std::min(rest, x.outputs);
        rest -= out_n;
        const uint64_t linked_n = std::min(rest, x.linked + x.linked_after);
        rest -= linked_n;
        const uint64_t chest_n = std::min(rest, x.chests);
        rest -= chest_n;
        out->stock[i] = static_cast<uint32_t>(std::min<uint64_t>(rest, UINT32_MAX));
        if (with_outputs) out->outputs[i] = static_cast<uint32_t>(std::min<uint64_t>(out_n, UINT32_MAX));
        if (with_chests) out->chests[i] = static_cast<uint32_t>(std::min<uint64_t>(chest_n, UINT32_MAX));
        if (with_linked) out->linked[i] = static_cast<uint32_t>(std::min<uint64_t>(linked_n, UINT32_MAX));
    }
    std::snprintf(out->message, sizeof out->message, "%s", p.why);
    return KILN_OK;
}

KilnResult KILN_CALL t_submit(KilnOwner owner, const KilnStationRequest* r, KilnStationDoneFn done, void* user, uint64_t* out_ticket) {
    if (out_ticket) *out_ticket = 0;
    if (!r || !request_valid(*r)) return KILN_ERR_INVALID_ARGUMENT;
    static std::atomic<uint32_t> refusals{0};
    auto refuse = [&](const char* why) {
        if (refusals.fetch_add(1) < 5) LOGW("stations: an action was refused: %s", why);
        return KILN_ERR_UNAVAILABLE;
    };
    const bool fill = r->action == KILN_STATION_FILL;
    if (g_off.load() || (fill ? !G.fill_ok : !G.control)) return refuse(fill ? "stations.fill is off" : "stations.control is off");
    if (fill && (r->flags & KILN_STATION_FILL_ITEM) && !G.fill_items_ok) return refuse("stations.fill_items is off");
    const int state = g_state.load();
    if (!g_w || !g_tx || state == KILN_ITEMS_PROGRAM_NONE || state == KILN_ITEMS_PROGRAM_BROKEN)
        return refuse("the program kiln_items_stations is not registered or broke");
    if (!here()) return refuse("no server world here");
    Req q{};
    q.ticket = items_next_ticket();
    q.owner = owner;
    // an older caller passes KILN_STATION_REQUEST_V1 bytes: do not read past what it owns
    std::memcpy(&q.r, r, std::min(r->size, static_cast<uint32_t>(sizeof q.r)));
    q.r.size = sizeof(KilnStationRequest);
    q.r.keep = nullptr;
    q.r.chests = nullptr;
    q.r.chest_count = 0;
    q.r.chest_reserved = 0;
    for (uint32_t i = 0; i < r->keep_count; ++i) q.keep[i] = r->keep[i];
    if (r->size >= sizeof(KilnStationRequest)) {
        q.chest_n = r->chest_count;
        for (uint32_t i = 0; i < q.chest_n; ++i) q.chests[i] = r->chests[i];
    }
    q.submitted = current_step();
    {
        std::lock_guard lock(g_mu);
        if (g_incoming.size() + g_ready.size() + g_after.size() >= kMaxPending) return KILN_ERR_BUFFER_TOO_SMALL;
        g_incoming.push_back(q);
        if (done) g_callbacks.emplace(q.ticket, Callback{done, user});
        g_pending_n.store(static_cast<uint32_t>(g_ready.size() + g_after.size() + g_incoming.size()), std::memory_order_release);
    }
    if (out_ticket) *out_ticket = q.ticket;
    return KILN_OK;
}

KilnResult KILN_CALL t_set_output_source(KilnEntity station, uint32_t on) {
    if (!station || on > 1) return KILN_ERR_INVALID_ARGUMENT;
    if (!G.outputs_ok || g_off.load()) return KILN_ERR_UNAVAILABLE;
    std::lock_guard lock(g_mu);
    const auto it = std::lower_bound(g_sources.begin(), g_sources.end(), station);
    const bool there = it != g_sources.end() && *it == station;
    if (on && !there) {
        if (g_sources.size() >= kMaxSources) return KILN_ERR_BUFFER_TOO_SMALL;
        g_sources.insert(it, station);
    } else if (!on && there) {
        g_sources.erase(it);
    }
    return KILN_OK;
}

KilnResult KILN_CALL t_output_sources(KilnEntity* out, uint32_t capacity, uint32_t* out_count) {
    if (!out_count || (capacity && !out)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    const uint32_t n = static_cast<uint32_t>(g_sources.size());
    *out_count = n;
    for (uint32_t i = 0; i < n && i < capacity; ++i) out[i] = g_sources[i];
    return n > capacity ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

uint32_t KILN_CALL t_max_chests() { return KILN_STATIONS_MAX_CHESTS; }

KilnResult KILN_CALL t_collect(KilnOwner owner, const KilnStationCollect* c, KilnStationDoneFn done, void* user, uint64_t* out_ticket) {
    if (out_ticket) *out_ticket = 0;
    if (!c || !collect_valid(*c)) return KILN_ERR_INVALID_ARGUMENT;
    if (g_off.load() || !G.read || !E.move_ok) return KILN_ERR_UNAVAILABLE;
    const int state = g_state.load();
    if (!g_w || !g_tx || state == KILN_ITEMS_PROGRAM_NONE || state == KILN_ITEMS_PROGRAM_BROKEN) return KILN_ERR_UNAVAILABLE;
    if (!here()) return KILN_ERR_UNAVAILABLE;
    Req q{};
    q.ticket = items_next_ticket();
    q.owner = owner;
    q.r.action = KILN_STATION_COLLECT;
    q.r.station = c->station;
    q.r.flags = c->flags;
    q.collect_to = c->to;
    q.collect_item = c->item;
    q.collect_flags = c->flags;
    q.submitted = current_step();
    {
        std::lock_guard lock(g_mu);
        if (g_incoming.size() + g_ready.size() + g_after.size() >= kMaxPending) return KILN_ERR_BUFFER_TOO_SMALL;
        g_incoming.push_back(q);
        if (done) g_callbacks.emplace(q.ticket, Callback{done, user});
        g_pending_n.store(static_cast<uint32_t>(g_ready.size() + g_after.size() + g_incoming.size()), std::memory_order_release);
    }
    if (out_ticket) *out_ticket = q.ticket;
    return KILN_OK;
}

// The client's snapshot of what it may count. Any thread. Not consulted by the server's payment. A row with count 0 names
// its item with none (since 0.2.0-dev.30.8): a production station's screen names every item so (GitHub #132), which raises
// no count there and keeps the snapshot on for the fill buttons' answers.
KilnResult KILN_CALL t_set_craft_stock(const KilnStationStack* rows, uint32_t count) {
    if (count > KILN_STATIONS_MAX_CRAFT_STOCK || (count && !rows)) return KILN_ERR_INVALID_ARGUMENT;
    size_t kept = 0;
    bool was = false;
    {
        std::lock_guard lock(g_craft_mu);
        was = g_craft_on.load(std::memory_order_relaxed);
        g_craft_have.clear();
        for (uint32_t i = 0; i < count; ++i)
            if (rows[i].item) g_craft_have[rows[i].item] = rows[i].count;
        kept = g_craft_have.size();
        g_craft_on.store(kept != 0, std::memory_order_release);
    }
    if (!kept) fill_clear();  // the fill buttons' answers belong to the same open menu
    if (kept || was)
        if (const uint32_t n = g_log_stock.take())
            LOGI(kept ? "stations: crafting stock from the server: %u item(s) the crafting menu may count%s"
                      : "stations: crafting stock cleared: the crafting menu shows the game's own counts%s",
                 static_cast<unsigned>(kept), n > 1 ? " (more held back)" : "");
    return KILN_OK;
}

// The server's answers for the station screen's fills (a Kiln client). Any thread.
KilnResult KILN_CALL t_set_fill_answers(const KilnStationFillAnswer* rows, uint32_t count) {
    if (count > KILN_STATIONS_MAX_FILL_KEYS || (count && !rows)) return KILN_ERR_INVALID_ARGUMENT;
    std::vector<KilnStationFillAnswer> v;
    for (uint32_t i = 0; i < count; ++i) {
        if (!fill_answer_ok(rows[i])) return KILN_ERR_INVALID_ARGUMENT;
        v.push_back(rows[i]);
    }
    std::lock_guard lock(g_fill_mu);
    drop_answered(&g_fill_asks, v);
    g_fill_answers = std::move(v);
    return KILN_OK;
}

// The station screen's refused fills with no answer yet (a Kiln client). Any thread.
KilnResult KILN_CALL t_fill_asks(KilnStationFillKey* out, uint32_t capacity, uint32_t* out_count) {
    if (!out_count || (capacity && !out)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_fill_mu);
    const uint32_t n = static_cast<uint32_t>(g_fill_asks.size());
    *out_count = n;
    for (uint32_t i = 0; i < n && i < capacity; ++i) out[i] = g_fill_asks[i];
    return n > capacity ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

// ---- the items a Kiln client needs counted (GitHub #68): an index of every recipe's inputs ----
// Built from the server world's RecipeRegistry (its recipe map: content/recipes.*), again when the registry or its
// count changes (a content mod's recipes). Tick thread.
struct RecipeIndex {
    void* reg = nullptr;
    uint64_t count = 0;
    std::vector<std::vector<uint32_t>> inputs;  // per recipe: its input items and every option of a category input
};
RecipeIndex g_rindex;
int64_t g_rmap_cap = -1, g_rmap_bits = -1, g_rmap_values = -1, g_rmap_count = -1;

struct RecipeMapCopy {
    const uint8_t* reg;
    uint64_t cap, count;
    const uint8_t** out;
    uint32_t room, n;
};
void recipe_map_impl(void* p) {
    auto* c = static_cast<RecipeMapCopy*>(p);
    c->cap = rd<uint64_t>(c->reg, g_rmap_cap);
    c->count = rd<uint32_t>(c->reg, g_rmap_count);
    const auto* bits = rd<const uint64_t*>(c->reg, g_rmap_bits);
    const auto* values = rd<const uint8_t* const*>(c->reg, g_rmap_values);
    if (!c->out || !bits || !values || !c->cap || (c->cap & (c->cap - 1)) || c->cap > (1u << 20)) return;
    for (uint64_t i = 0; i < c->cap && c->n < c->room; ++i)
        if ((bits[i >> 6] >> (i & 63)) & 1)
            if (const uint8_t* r = values[i]) c->out[c->n++] = r;
}
struct RecipeInputsRead {
    const uint8_t* recipe;
    uint32_t n;
    uint32_t item[KILN_STATIONS_MAX_INPUTS], category[KILN_STATIONS_MAX_INPUTS];
};
void recipe_inputs_impl(void* p) {
    auto* c = static_cast<RecipeInputsRead*>(p);
    const int32_t rel = rd<int32_t>(c->recipe, kRecipeInputs);
    const uint32_t n = std::min(rd<uint32_t>(c->recipe, kRecipeInputs + 4), KILN_STATIONS_MAX_INPUTS);
    if (!rel) return;
    const uint8_t* in = c->recipe + kRecipeInputs + rel;
    for (uint32_t i = 0; i < n; ++i) {
        c->item[i] = rd<uint32_t>(in + i * kInputStride, kInputItem);
        c->category[i] = rd<uint32_t>(in + i * kInputStride, kInputCategory);
    }
    c->n = n;
}
bool recipe_index(void* reg) {
    if (!reg || g_rmap_cap < 0) return false;
    unsigned long code = 0;
    RecipeMapCopy probe{static_cast<const uint8_t*>(reg), 0, 0, nullptr, 0, 0};
    if (!guard(&recipe_map_impl, &probe, &code)) return false;
    if (g_rindex.reg == reg && g_rindex.count == probe.count && !g_rindex.inputs.empty()) return true;
    std::vector<const uint8_t*> recipes(static_cast<size_t>(std::min<uint64_t>(probe.count + 64, 1u << 16)));
    RecipeMapCopy c{static_cast<const uint8_t*>(reg), 0, 0, recipes.data(), static_cast<uint32_t>(recipes.size()), 0};
    if (!guard(&recipe_map_impl, &c, &code) || c.n == 0) return false;
    std::unordered_map<uint32_t, std::vector<uint32_t>> options;  // category -> its items (read once)
    std::vector<std::vector<uint32_t>> all;
    all.reserve(c.n);
    for (uint32_t r = 0; r < c.n; ++r) {
        RecipeInputsRead in{recipes[r], 0, {}, {}};
        if (!guard(&recipe_inputs_impl, &in, &code)) continue;
        std::vector<uint32_t> v;
        for (uint32_t i = 0; i < in.n; ++i) {
            if (in.item[i]) v.push_back(in.item[i]);
            if (!in.category[i]) continue;
            auto it = options.find(in.category[i]);
            if (it == options.end()) {
                KilnItemId opt[64];
                CatCall cc{reg, in.category[i], 0, false, 0, opt, nullptr, 64, 0, false};
                std::vector<uint32_t> o;
                if (guard(&cat_impl, &cc, &code) && cc.found)
                    for (uint32_t k = 0; k < cc.n && k < 64; ++k) o.push_back(opt[k]);
                it = options.emplace(in.category[i], std::move(o)).first;
            }
            v.insert(v.end(), it->second.begin(), it->second.end());
        }
        if (!v.empty()) all.push_back(std::move(v));
    }
    g_rindex.reg = reg;
    g_rindex.count = probe.count;
    g_rindex.inputs = std::move(all);
    return true;
}

KilnResult KILN_CALL t_craft_items(const KilnItemId* items, uint32_t count, KilnItemId* out, uint32_t capacity, uint32_t* out_count) {
    if (!out_count || (count && !items) || (capacity && !out)) return KILN_ERR_INVALID_ARGUMENT;
    *out_count = 0;
    if (KilnResult g = read_gate(); g != KILN_OK) return g;
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!recipe_index(recipes_registry())) return KILN_ERR_UNAVAILABLE;
    const std::vector<uint32_t> lent(items, items + count);
    const std::vector<uint32_t> want = craft_stock_items(g_rindex.inputs, lent);
    *out_count = static_cast<uint32_t>(want.size());
    for (uint32_t i = 0; i < want.size() && i < capacity; ++i) out[i] = want[i];
    return want.size() > capacity ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

KilnResult KILN_CALL t_cancel(uint64_t ticket) {
    std::lock_guard lock(g_mu);
    return g_callbacks.erase(ticket) ? KILN_OK : KILN_ERR_NOT_FOUND;
}

KilnResult KILN_CALL t_watch(uint32_t on) {
    if (on) {
        if (!G.events || !G.read) return KILN_ERR_UNAVAILABLE;
        g_watchers.fetch_add(1);
    } else {
        int v = g_watchers.load();
        while (v > 0 && !g_watchers.compare_exchange_weak(v, v - 1)) {
        }
    }
    return KILN_OK;
}

KilnResult KILN_CALL t_events(uint64_t after, KilnStationEvent* out, uint32_t capacity, uint32_t* out_count) {
    if (!out_count || (capacity && !out)) return KILN_ERR_INVALID_ARGUMENT;
    *out_count = 0;
    if (!tick()) return KILN_ERR_WRONG_THREAD;
    std::lock_guard lock(g_mu);
    bool more = false;
    *out_count = g_ring.read(after, out, capacity, &more);
    return more ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

uint64_t KILN_CALL t_last_seq() {
    std::lock_guard lock(g_mu);
    return g_ring.last();
}

// ---- GitHub #147: an InteractionLock's required item from the player's linked sources (docs/re/locks.md) ----
// Two places in the game decide a lock with a required item (sigdb server.d/lock-sources.json):
//  - the press: player_control_action_sequence's lock check counts the item in the presser's bag (InventoryItemCount)
//    and, when it is short of requiredItemAmount, plays the lock's failed attempt instead of sending the press. It runs
//    in the server world and, on a client, in the client and prediction worlds; a hosting client's exe runs one copy
//    of the code for all three. A mid hook at the count's return (locks/LockCountRet) adds, when the bag alone is short:
//    in the server world the server's own count of the player's lock-opted links (g_lock_cache, read on the tick thread
//    each 30 steps); in a client world the server's snapshot (set_lock_stock). The locked prompt (player_ui, client
//    only, locks/client.UiLockCountRet) adds the snapshot the same way, and so does the prompt's requirement line
//    ("Required: Lockpick", the HUD, locks/client.LockRowCount): the HUD counts the bag again itself and colours the
//    line by that count alone (red when short), whatever player_ui decided (GitHub #147 follow-up, 0.2.0-dev.31.15).
//  - the payment: interaction_attempt pays the item with PayMaterial(tx, guest, item, amount) inside its own
//    transaction, then commits (Consume) or rolls back (a presence test). A mid hook after that call (locks/LockPaid)
//    pays what the bag left from the opted-in links, in their order, inside the same transaction, after a recount of
//    each through the transaction's own accessors; anything short leaves the game's answer (left > 0), and the game
//    declines and rolls the whole transaction back: all or nothing, as crafting's payment.
// Hooks go in on demand: the server's when a link with Locks exists, the client's when a Kiln server's snapshot is on.
constexpr uint64_t kLockRefreshSteps = 30;
struct LockSites {
    uintptr_t count_ret = 0, paid = 0, ui_ret = 0, count_fn = 0, pay_fn = 0, row = 0;
};
LockSites g_lock;
using CurrentEntityFn = uint32_t* (*)(void* ctx, uint32_t* out);
CurrentEntityFn g_lock_current = nullptr;
bool g_lock_feat = false;          // items.lock_sources resolved with its pinned layout (stations_resolve)
std::atomic<bool> g_lock_off{false};  // a stand-down: a foreign patch, a failed hook, a fault
// The prompt's requirement line (locks/client.LockRowCount) is display only, and a hosting client's server half shares
// g_lock_feat and g_lock_off: so the line is not part of items.lock_sources. Its site missing, another module's patch on
// it, its hook not going in or faulting leave only the line to the game (logged); nothing else stands down, no row lowered.
bool g_lock_row_feat = false;             // locks/client.LockRowCount resolved (stations_resolve, a client)
std::atomic<bool> g_lock_row_off{false};  // the line's own stand-down
kiln::MidHook2 h_lock_eval, h_lock_paid, h_lock_ui, h_lock_row;
std::atomic<bool> g_lock_eval_on{false}, g_lock_paid_on{false}, g_lock_ui_on{false}, g_lock_row_on{false};
// A hosting client puts the hooks in from two threads: its server world's Pre (the tick thread) and its client world's
// Pre (the main thread). Both may want h_lock_eval; the installs and their failure flags go under one lock.
std::mutex g_lock_install_mu;
// A hook that faulted on a game worker thread: the next Pre lowers items.lock_sources (faulted), on the tick thread.
std::atomic<bool> g_lock_fault_report{false};
LogLimit g_log_lock_check{16}, g_log_lock_pay{24}, g_log_lock_stand{8}, g_log_lock_snap{8}, g_log_lock_prompt{12};
// The requirement line's last logged state (item << 8 | LockState): the HUD runs every frame, the line logs per change.
std::atomic<uint64_t> g_lock_prompt_last{0};

// The server's own counts of each player's lock-opted links (tick thread writes, any thread reads). `linked`: the player
// has links of this login; `opted`: one of them has Locks.
struct LockCached {
    uint32_t session = 0;
    bool linked = false, opted = false;
    std::unordered_map<uint32_t, uint64_t> counts;
};
std::mutex g_lock_mu;
std::unordered_map<uint32_t, LockCached> g_lock_cache;  // g_lock_mu
std::atomic<bool> g_lock_dirty{true};
uint64_t g_lock_step = 0;

// A Kiln client's snapshot from the server (g_lock_snap_mu): on, the rows, the items asked about.
std::mutex g_lock_snap_mu;
std::atomic<bool> g_lock_on{false};
std::unordered_map<uint32_t, uint32_t> g_lock_have;  // g_lock_snap_mu
std::vector<uint32_t> g_lock_asks;                   // g_lock_snap_mu, oldest first

// What this player's lock-opted links hold, through kiln.items' reads (tick thread). The payment recounts at use.
void lock_counts(void* bases, uint32_t player, const std::vector<CraftSource>& sources, std::unordered_map<uint32_t, uint64_t>* out) {
    std::unordered_set<uint32_t> seen{player};
    for (bool after : {false, true})
        for (const auto& s : sources) {
            if (!s.locks || s.after_outputs != after || seen.count(s.container) || !source_access(bases, player, s.container)) continue;
            seen.insert(s.container);
            std::unordered_map<uint32_t, uint64_t> held;
            count_items(s.container, &held);
            // A container is counted once per item even when several of the player's links name it with other filters.
            for (const auto& [item, n] : held) {
                bool allowed = false;
                for (const auto& t : sources) allowed = allowed || (t.locks && t.container == s.container && t.allows(item));
                if (allowed && n) (*out)[item] += n;
            }
        }
}

// GitHub #132: what one purpose's links (fills, building) hold, every container once per item, the filters applied, no
// bag and no container in `skip`; sorted by item. Tick thread (kiln.world reads), as the payment's own checks.
std::vector<KilnStationStack> link_rows(void* bases, uint32_t player, const std::vector<CraftSource>& sources, bool CraftSource::*opt,
                                        const std::unordered_set<uint32_t>& skip = {}) {
    std::unordered_map<uint32_t, uint64_t> sum;
    std::unordered_set<uint32_t> seen(skip.begin(), skip.end());
    seen.insert(player);
    for (const auto& s : sources) {
        if (!(s.*opt) || seen.count(s.container) || !source_access(bases, player, s.container)) continue;
        seen.insert(s.container);
        std::unordered_map<uint32_t, uint64_t> held;
        count_items(s.container, &held);
        for (const auto& [item, n] : held) {
            bool allowed = false;
            for (const auto& t : sources) allowed = allowed || ((t.*opt) && t.container == s.container && t.allows(item));
            if (allowed && n) sum[item] += n;
        }
    }
    std::vector<KilnStationStack> rows;
    for (const auto& [item, count] : sum) rows.push_back({item, static_cast<uint32_t>(std::min<uint64_t>(count, UINT32_MAX))});
    std::sort(rows.begin(), rows.end(), [](const auto& a, const auto& b) { return a.item < b.item; });
    return rows;
}

// GitHub #132 (review of 31.12): the Magic chests of the base the player stands in. The build cursor adds a building link
// to the game's own count of the base's stock (the server's StockCount counts the base at the cursor, a client's the
// published map of the player's base), and the placement pays the base list first and leaves it out of the links
// (merge_craft_sources skips the payment's order), so a link to one of these chests must not count again: a player who
// links every chest (Streamlined Logistics) saw the cursor and the build menu count a base chest twice, and the placement
// then came up short. Tick thread (kiln.world reads). Empty when the player stands in no base or nothing could be read.
std::unordered_set<uint32_t> player_base_chests(uint32_t player) {
    std::unordered_set<uint32_t> out;
    RawBases rb{};
    KilnVec3 position{};
    if (!position_of(player, &position) || !read_bases(&rb)) return out;
    const int32_t base = base_index_at(rb.sys, position);
    std::vector<KilnEntity> chests;
    if (base >= 0 && static_cast<uint32_t>(base) < rb.count && base_list(rb, static_cast<uint32_t>(base), false, &chests))
        out.insert(chests.begin(), chests.end());
    return out;
}

// Tick thread, the server world's Pre: the cache, every 30 steps and when a registration changed.
void lock_cache_refresh(uint64_t step) {
    if (!g_lock_dirty.exchange(false) && step < g_lock_step + kLockRefreshSteps && step >= g_lock_step) return;
    g_lock_step = step;
    void* const bases = g_srv_bases.load(std::memory_order_acquire);
    std::unordered_map<uint32_t, LockCached> next;
    for (uint32_t player : g_registered.players()) {
        LockCached c;
        if (!source_player(bases, player, &c.session)) continue;
        const auto sources = craft_sources_copy(player);
        c.linked = !sources.empty();
        c.opted = std::any_of(sources.begin(), sources.end(), [](const CraftSource& s) { return s.locks; });
        if (c.opted) lock_counts(bases, player, sources, &c.counts);
        if (c.linked) next.emplace(player, std::move(c));
    }
    std::lock_guard lock(g_lock_mu);
    g_lock_cache = std::move(next);
}

// The server world's count for a player's lock check (any thread): false when the player has no link of this login.
bool lock_cached(uint32_t player, uint32_t item, uint64_t* held, bool* opted) {
    std::lock_guard lock(g_lock_mu);
    const auto it = g_lock_cache.find(player);
    if (it == g_lock_cache.end()) return false;
    *opted = it->second.opted;
    const auto n = it->second.counts.find(item);
    *held = n == it->second.counts.end() ? 0 : n->second;
    return true;
}

// The client's snapshot for a lock check (any thread). False: no snapshot. *row: the server named the item. An item
// with no row is noted for the client to ask about (at most KILN_STATIONS_MAX_LOCK_ASKS, oldest kept).
bool lock_snapshot(uint32_t item, uint64_t* held, bool* row) {
    std::lock_guard lock(g_lock_snap_mu);
    if (!g_lock_on.load(std::memory_order_relaxed)) return false;
    const auto it = g_lock_have.find(item);
    *row = it != g_lock_have.end();
    *held = *row ? it->second : 0;
    if (!*row && item && g_lock_asks.size() < KILN_STATIONS_MAX_LOCK_ASKS &&
        std::find(g_lock_asks.begin(), g_lock_asks.end(), item) == g_lock_asks.end())
        g_lock_asks.push_back(item);
    return true;
}

// The world a game program's context belongs to (ctx qword 0 = the job; the job's storage kind byte +8): 0 the server
// world, 1 prediction, 2 client; 0xff unreadable.
uint8_t ctx_world(void* ctx) {
    __try {
        if (!ctx || !E.job_storage) return 0xff;
        void* const job = *static_cast<void**>(ctx);
        if (!job) return 0xff;
        const void* const storage = E.job_storage(job);
        return storage ? rd<uint8_t>(storage, 8) : 0xff;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0xff;
    }
}
uint32_t ctx_entity(void* ctx) {
    __try {
        uint32_t out = 0;
        const uint32_t* p = g_lock_current && ctx ? g_lock_current(ctx, &out) : nullptr;
        return p ? *p : 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

// What one lock check decided (lock_decide): not asked (the bag covers it, no item, a stand-down, nothing linked), the
// links counted, or why not.
enum LockState : uint8_t { kLockUnasked = 0, kLockCounted, kLockNoSnapshot, kLockNoRow, kLockShortCount, kLockNotOpted };
struct LockDecision {
    uint32_t counted = 0;
    uint64_t linked = 0;
    LockState state = kLockUnasked;
    const char* why = nullptr;  // a refusal's text
};
LockDecision lock_decide(uint8_t world, uint32_t player, uint32_t item, uint32_t bag, uint32_t need) {
    LockDecision d;
    d.counted = bag;
    if (!item || !need || bag >= need || g_lock_off.load(std::memory_order_relaxed)) return d;
    if (world == 0) {
        bool opted = false;
        if (!lock_cached(player, item, &d.linked, &opted)) return d;  // nothing linked: the game's lock, no line
        if (!opted) d.state = kLockNotOpted, d.why = lock_why_text(kLockNotOptedIn);
        else if (bag + d.linked < need) d.state = kLockShortCount, d.why = "short count";
    } else {
        bool row = false;
        if (!lock_snapshot(item, &d.linked, &row))
            d.state = kLockNoSnapshot, d.why = "no snapshot from the server (a vanilla or older server, or no link opted in with Locks)";
        else if (!row)
            d.state = kLockNoRow, d.why = "no snapshot row for the item yet (the client asks the server for it)";
        else if (bag + d.linked < need)
            d.state = kLockShortCount, d.why = "short count";
    }
    if (!d.why) {
        d.state = kLockCounted;
        d.counted = lock_counted(bag, d.linked, need);
    }
    return d;
}

// One lock check (the press, or the locked prompt): the game counted `bag` of `item`, the lock needs `need`. Returns the
// count the game should see. `world`: the context's world (the prompt: client). Logs a press's decision when links or a
// snapshot were asked.
uint32_t lock_check(uint8_t world, uint32_t player, uint32_t item, uint32_t bag, uint32_t need, bool press) {
    const LockDecision d = lock_decide(world, player, item, bag, need);
    if (d.state == kLockUnasked) return d.counted;
    const uint64_t linked = d.linked;
    const char* const why = d.why;
    const uint32_t counted = d.counted;
    if (press)
        if (const uint32_t n = g_log_lock_check.take()) {
            const char* where = world == 0 ? "server" : world == 2 ? "client" : world == 1 ? "prediction" : "unknown";
            if (why)
                LOGI("locks: the %s world's lock check of item 0x%08x (player %u) refused: %s (bag %u, linked %llu, needs %u)%s",
                     where, item, player, why, bag, static_cast<unsigned long long>(linked), need, n > 1 ? " (more held back)" : "");
            else
                LOGI("locks: the %s world's lock check of item 0x%08x (player %u) counts the linked sources: bag %u + linked %llu, "
                     "needs %u%s", where, item, player, bag, static_cast<unsigned long long>(linked), need, n > 1 ? " (more held back)" : "");
        }
    return counted;
}

// locks/LockCountRet: rbp-0x68 the count's {u8 error; u32; u32 count}, r14 the InteractionLock (item +0x10, amount u16
// +0x16), [rsp+0x58] the program context (all pinned by the pattern).
void lock_eval_impl(KilnRegisters& r) {
    auto* out = reinterpret_cast<uint8_t*>(r.rbp - 0x68);
    if (out[0] != 0) return;  // the game's own refusal (no initialized inventory): left to it
    const auto* lock = reinterpret_cast<const uint8_t*>(r.r14);
    const uint32_t item = rd<uint32_t>(lock, 0x10), need = rd<uint16_t>(lock, 0x16), bag = rd<uint32_t>(out, 8);
    if (bag >= need) return;
    void* const ctx = rd<void*>(reinterpret_cast<const void*>(r.rsp), 0x58);
    const uint8_t world = ctx_world(ctx);
    if (world == 0xff) return;
    const uint32_t player = world == 0 ? ctx_entity(ctx) : 0;
    if (world == 0 && (!player || player > kMaxPlayerEntity)) return;
    const uint32_t counted = lock_check(world, player, item, bag, need, true);
    if (counted != bag) wr<uint32_t>(out, 8, counted);
}
// A fault in a lock hook (a game worker thread): the feature stands down at once, its capability row on the next Pre.
void lock_fault(const char* what, unsigned long code) {
    if (g_lock_off.exchange(true)) return;
    LOGE("locks: %s faulted (0x%08lx): locks from linked sources stand down", what, code);
    g_lock_fault_report.store(true);
}
// Tick thread (either world's Pre): the capability row of a hook fault, once.
void lock_fault_lower() {
    if (g_lock_fault_report.exchange(false))
        lower("items.lock_sources", KILN_CAP_REASON_FAULTED, "a lock hook faulted (see kiln.log): the game's own lock checks and payments are used");
}
void hk_lock_eval(KilnRegisters& r) {
    __try {
        lock_eval_impl(r);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        lock_fault("the lock check's hook", GetExceptionCode());
    }
}
// locks/client.UiLockCountRet: rbp+0x620 the count's result, rdi the InteractionLock (pinned by the pattern).
void hk_lock_ui(KilnRegisters& r) {
    __try {
        auto* out = reinterpret_cast<uint8_t*>(r.rbp + 0x620);
        if (out[0] != 0) return;
        const auto* lock = reinterpret_cast<const uint8_t*>(r.rdi);
        const uint32_t item = rd<uint32_t>(lock, 0x10), need = rd<uint16_t>(lock, 0x16), bag = rd<uint32_t>(out, 8);
        if (bag >= need) return;
        const uint32_t counted = lock_check(2, 0, item, bag, need, false);
        if (counted != bag) wr<uint32_t>(out, 8, counted);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        lock_fault("the locked prompt's hook", GetExceptionCode());
    }
}
// locks/client.LockRowCount (GitHub #147 follow-up): the HUD's requirement line of the looked-at lock. r8d is the HUD's own
// count of the item in the local player's bag (the UI's inventory mirror), r14 the prompt record (player_ui's +0x2bc0:
// item +0xd8, amount u16 +0xdc), all pinned by the pattern. The line is met (not red) when the count reaches max(amount,
// 1); the count is shown too, so it is raised to bag + linked as the press and the locked prompt count. Logs the line's
// state once per change (the HUD runs every frame), so a player's log shows whether the line counted the links and why not.
void lock_row_impl(KilnRegisters& r) {
    const auto* rec = reinterpret_cast<const uint8_t*>(r.r14);
    const uint32_t item = rd<uint32_t>(rec, 0xd8), amount = rd<uint16_t>(rec, 0xdc);
    const uint32_t need = amount > 1 ? amount : 1, bag = static_cast<uint32_t>(r.r8);
    const LockDecision d = lock_decide(2, 0, item, bag, need);
    if (d.state == kLockUnasked) return;
    if (d.counted != bag) r.r8 = d.counted;  // a 32-bit register write: the upper half is zero
    const uint64_t key = (uint64_t{item} << 8) | d.state;
    if (g_lock_prompt_last.exchange(key, std::memory_order_relaxed) == key) return;
    if (const uint32_t n = g_log_lock_prompt.take()) {
        if (d.state == kLockCounted)
            LOGI("locks: the lock prompt's requirement line counts the linked sources: item 0x%08x, bag %u + linked %llu, needs %u "
                 "(shown met)%s", item, bag, static_cast<unsigned long long>(d.linked), need, n > 1 ? " (more held back)" : "");
        else
            LOGI("locks: the lock prompt's requirement line counts the bag only: item 0x%08x, %s (bag %u, linked %llu, needs %u)%s",
                 item, d.why, bag, static_cast<unsigned long long>(d.linked), need, n > 1 ? " (more held back)" : "");
    }
}
// A fault in the requirement line's hook: the line alone keeps the game's count from then on (g_lock_row_off).
void lock_row_fault(unsigned long code) {
    if (g_lock_row_off.exchange(true)) return;
    LOGE("locks: the lock prompt's requirement line hook faulted (0x%08lx): the line keeps the game's count (the bag only); the lock "
         "checks, the locked prompt and the payment still count linked sources", code);
}
void hk_lock_row(KilnRegisters& r) {
    if (g_lock_row_off.load(std::memory_order_relaxed)) return;
    __try {
        lock_row_impl(r);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        lock_row_fault(GetExceptionCode());
    }
}

// The rest of a lock's payment from the guest's opted-in links, inside the game's transaction (any thread: the game's
// interaction_attempt). `left`: what the bag's PayMaterial left. True: every unit was taken (the caller sets left 0).
bool lock_pay_impl(void* tx, uint32_t guest, uint32_t item, uint32_t need, uint32_t left, bool consume) {
    auto stand = [&](const char* why) {
        if (const uint32_t n = g_log_lock_pay.take())
            LOGI("locks: player %u's lock of item 0x%08x x%u left to the game: %s (the bag paid %u)%s", guest, item, need, why,
                 need - left, n > 1 ? " (more held back)" : "");
        return false;
    };
    if (!guest || guest > kMaxPlayerEntity || !item || !left || left > need) return false;
    if (g_lock_off.load(std::memory_order_relaxed) || !G.registered_ok || !G.pay) return stand("locks from linked sources are off here");
    void* const bases = g_srv_bases.load(std::memory_order_acquire);
    Access a{};
    if (!bases || !access_from_tx_reads(tx, &a)) return stand("not the server world's transaction (its accessors did not check out)");
    const auto sources = craft_sources_copy(guest);
    if (sources.empty()) {
        if (!g_registered.copy(guest).empty()) return stand("the player's links belong to an earlier login");
        return false;  // nothing linked: the game's lock, no line
    }
    std::unordered_map<uint32_t, std::unordered_map<uint32_t, uint64_t>> views;
    auto have = [&](uint32_t e, uint32_t it) -> uint64_t {
        auto v = views.find(e);
        if (v == views.end()) {
            std::unordered_map<uint32_t, uint64_t> m;
            count_pages(a, e, &m);
            v = views.emplace(e, std::move(m)).first;
        }
        const auto n = v->second.find(it);
        return n == v->second.end() ? 0 : n->second;
    };
    auto access = [&](uint32_t player, uint32_t container) {
        if (storage_contains(container) || !source_access(bases, player, container)) return false;
        uint8_t flags = 0;
        PageSet pages{};
        bool initialized = false;
        return setup_flags(a, container, &flags) && !(flags & 3) && read_pages(a, container, &pages, &initialized) && initialized;
    };
    const auto held = lock_sources_for(sources, guest, item, guest, access, have);
    uint64_t total = 0;
    for (const auto& h : held) total += h.count;
    if (const uint32_t why = lock_why(sources, guest, item, total, left); why != kLockNone) {
        if (why == kLockNoLinks) return false;
        uint64_t cached = 0;
        bool opted = false;
        if (why == kLockShort && lock_cached(guest, item, &cached, &opted) && cached >= left) {
            char text[160];
            std::snprintf(text, sizeof text, "a stack the server no longer sees (the links were counted to hold %llu, hold %llu now; "
                          "needs %u more)", static_cast<unsigned long long>(cached), static_cast<unsigned long long>(total), left);
            return stand(text);
        }
        char text[160];
        std::snprintf(text, sizeof text, "%s (the links hold %llu, needs %u more)", lock_why_text(why),
                      static_cast<unsigned long long>(total), left);
        return stand(text);
    }
    std::vector<LockHeld> takes;
    if (!plan_lock_take(held, left, &takes)) return stand("short count");
    uint64_t paid = 0;
    for (const auto& t : takes) {
        uint32_t rem = static_cast<uint32_t>(t.count);
        uint8_t refused = 0;
        const bool ran = pay_one(tx, t.container, item, &rem, &refused);
        if (!ran || refused || rem) {
            LOGW("locks: player %u's lock of item 0x%08x x%u rolled back: container %u's take %s (refused 0x%02x, %u short): nothing moved",
                 guest, item, need, t.container, !ran ? "faulted" : refused ? "was refused" : "came short", refused, rem);
            return false;  // left > 0: the game declines and rolls the transaction back, the bag's take included
        }
        paid += t.count;
    }
    g_lock_dirty.store(true, std::memory_order_relaxed);
    g_build_dirty.store(true, std::memory_order_relaxed);
    if (const uint32_t n = g_log_lock_pay.take())
        LOGI("locks: player %u's lock of item 0x%08x x%u: the bag paid %u, %llu from %zu linked source(s)%s%s", guest, item, need,
             need - left, static_cast<unsigned long long>(paid), takes.size(),
             consume ? "" : " (no Consume: a presence test, the game rolls it back)", n > 1 ? " (more held back)" : "");
    return true;
}
// locks/LockPaid: [rsp+0x58] PayMaterial's {u8 error; u32; u32 left}, rbp+0x230 the transaction, r14 the event's guest,
// rsi the InteractionLock (item +0x10, amount +0x16, mask +0x18 bit 0 Consume): pinned by the pattern.
void lock_paid_impl(KilnRegisters& r) {
    auto* out = reinterpret_cast<uint8_t*>(r.rsp + 0x58);
    const uint32_t left = rd<uint32_t>(out, 8);
    if (!left) return;  // the bag paid it: the game's own lock
    const auto* lock = reinterpret_cast<const uint8_t*>(r.rsi);
    const uint32_t item = rd<uint32_t>(lock, 0x10), need = rd<uint16_t>(lock, 0x16);
    const bool consume = (lock[0x18] & 1) != 0;
    const uint32_t guest = rd<uint32_t>(reinterpret_cast<const void*>(r.r14), 0);
    if (out[0] != 0) {
        if (!craft_sources_copy(guest).empty())
            if (const uint32_t n = g_log_lock_pay.take())
                LOGI("locks: player %u's lock of item 0x%08x x%u left to the game: the bag's payment answered error 0x%02x%s", guest,
                     item, need, out[0], n > 1 ? " (more held back)" : "");
        return;
    }
    if (lock_pay_impl(reinterpret_cast<void*>(r.rbp + 0x230), guest, item, need, left, consume)) wr<uint32_t>(out, 8, 0);
}
void hk_lock_paid(KilnRegisters& r) {
    __try {
        lock_paid_impl(r);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        lock_fault("the lock payment's hook", GetExceptionCode());
    }
}

// The client's snapshot (a Kiln client, any thread).
KilnResult KILN_CALL t_set_lock_stock(uint32_t on, const KilnStationStack* rows, uint32_t count) {
    if (on > 1 || (on && (count > KILN_STATIONS_MAX_LOCK_STOCK || (count && !rows)))) return KILN_ERR_INVALID_ARGUMENT;
    for (uint32_t i = 0; on && i < count; ++i)
        if (!rows[i].item) return KILN_ERR_INVALID_ARGUMENT;
    bool was = false;
    size_t kept = 0;
    {
        std::lock_guard lock(g_lock_snap_mu);
        was = g_lock_on.load(std::memory_order_relaxed);
        g_lock_have.clear();
        if (on)
            for (uint32_t i = 0; i < count; ++i) g_lock_have[rows[i].item] = rows[i].count;
        else
            g_lock_asks.clear();
        g_lock_asks.erase(std::remove_if(g_lock_asks.begin(), g_lock_asks.end(), [](uint32_t i) { return g_lock_have.count(i) != 0; }),
                          g_lock_asks.end());
        kept = g_lock_have.size();
        g_lock_on.store(on != 0, std::memory_order_release);
    }
    if (on != static_cast<uint32_t>(was))
        if (const uint32_t n = g_log_lock_snap.take())
            LOGI(on ? "locks: the server counts lock items from linked sources: %u item row(s)%s"
                    : "locks: lock snapshot cleared: the game's own lock checks again%s",
                 static_cast<unsigned>(kept), n > 1 ? " (more held back)" : "");
    return KILN_OK;
}
KilnResult KILN_CALL t_lock_asks(KilnItemId* out, uint32_t capacity, uint32_t* out_count) {
    if (!out_count || (capacity && !out)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_lock_snap_mu);
    const uint32_t n = static_cast<uint32_t>(g_lock_asks.size());
    *out_count = n;
    for (uint32_t i = 0; i < n && i < capacity; ++i) out[i] = g_lock_asks[i];
    return n > capacity ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

// ---- GitHub #150 (0.2.0-dev.31.18): the skill tree's respec from the player's respec links (docs/re/respec.md) ----
// The game charges its respec item (Runes, respec/item) in two places, both through one payer (respec/Payer, S 0xb9230):
// a node's unlearn (its cost; a refusal puts the node back) and the skill tree's reset with its pay flag set (the points
// spent). The payer opens a stock-mode-2 transaction (the bag, then the Magic chests of the base the player stands in) and
// pays one cost line with rules/InventoryPay, whose return is respec/RespecPaid. kiln.rules' payment hook hands that call
// to craft_pay_impl (the respec branch above): the game pays alone when the bag and the chests cover it; else Kiln pays the
// line itself in the same transaction, the bag and the chests first, then the links opted in with Respec, all or nothing.
// The join check's reset passes pay 0 and never reaches the payer: it stays free. A Kiln client's two screens that decide
// whether a respec is sent count the item with the game's UI count (stations/client.UiAvailableCount) and offer nothing
// when it is short: the respec tree's unlearn (respec/client.NodeCountRet, against the node's cost) and the Flame Altar's
// reskill dialog (respec/client.AltarCountRet, against the points spent). Mid hooks at those returns add the server's
// respec snapshot (set_respec_stock), as the lock checks do; the respec tree's Rune row (respec/client.HeaderCountRet) is
// display only and degrades alone.
// A Kiln client's snapshot from the server (g_respec_snap_mu): on, the rows.
std::mutex g_respec_snap_mu;
std::atomic<bool> g_respec_on{false};
std::unordered_map<uint32_t, uint32_t> g_respec_have;  // g_respec_snap_mu
bool g_respec_row_feat = false;                         // respec/client.HeaderCountRet resolved (a client; display only)
std::atomic<bool> g_respec_row_off{false};             // the Rune row's own stand-down
kiln::MidHook2 h_respec_node, h_respec_altar, h_respec_row;
std::atomic<bool> g_respec_node_on{false}, g_respec_altar_on{false}, g_respec_row_on{false};
std::atomic<bool> g_respec_fault_report{false};  // a hook faulted: the next Pre lowers crafting.respec_sources (faulted)
LogLimit g_log_respec_snap{8}, g_log_respec_check{24};
std::atomic<uint64_t> g_respec_last[3]{};        // per client site: the last logged (item, state), the UI runs every frame

// The client's snapshot (any thread). False: no snapshot (a vanilla or older server, no respec link). *row: the item named.
bool respec_snapshot(uint32_t item, uint64_t* held, bool* row) {
    std::lock_guard lock(g_respec_snap_mu);
    if (!g_respec_on.load(std::memory_order_relaxed)) return false;
    const auto it = g_respec_have.find(item);
    *row = it != g_respec_have.end();
    *held = *row ? it->second : 0;
    return true;
}

// One client respec check (the game's UI thread): the game counted `game` of `item` (the bag mirror and the published
// Magic chests); `need` is what the screen compares it with (0: the display row). Returns the count the screen should
// see: the game's plus the snapshot's row whenever the snapshot names the item, so the screen shows what the server will
// count; the screen's own compare decides. Logs the site's state once per change.
enum RespecSite : uint32_t { kRespecNode = 0, kRespecAltar = 1, kRespecRow = 2 };
enum RespecState : uint8_t { kRespecCounted = 1, kRespecShort, kRespecNoRow };
uint32_t respec_check(uint32_t site, uint32_t item, uint32_t game, uint32_t need) {
    if (!item || g_respec_off.load(std::memory_order_relaxed)) return game;
    uint64_t linked = 0;
    bool row = false;
    if (!respec_snapshot(item, &linked, &row)) return game;  // no snapshot (cleared): the game's own count
    RespecState state = kRespecCounted;
    uint32_t counted = game;
    if (!row) state = kRespecNoRow;
    else counted = static_cast<uint32_t>(std::min<uint64_t>(uint64_t{game} + linked, 0xffffffffull));
    if (need && game >= need) return counted;  // the game's count covers it: nothing to say
    if (state == kRespecCounted && need && counted < need) state = kRespecShort;
    if (site > kRespecRow) return counted;
    const uint64_t key = (uint64_t{item} << 8) | state;
    if (g_respec_last[site].exchange(key, std::memory_order_relaxed) == key) return counted;
    if (const uint32_t n = g_log_respec_check.take()) {
        const char* const what = site == kRespecNode ? "the skill tree's unlearn check" : site == kRespecAltar ? "the Flame Altar's reskill check"
                                                                                                         : "the respec tree's item row";
        if (state == kRespecCounted || state == kRespecShort)
            LOGI("respec: %s counts the linked sources: item 0x%08x, the game's count %u + linked %llu%s%u%s%s", what, item, game,
                 static_cast<unsigned long long>(linked), need ? ", needs " : " (shown", need ? need : counted,
                 !need ? ")" : state == kRespecShort ? " (short: not offered)" : " (offered)", n > 1 ? " (more held back)" : "");
        else
            LOGI("respec: %s counts the game's count only: item 0x%08x, %s (count %u%s%u)%s", what, item,
                 "no snapshot row for the item (the server sends its own respec item's row only)", game, need ? ", needs " : "", need, n > 1 ? " (more held back)" : "");
    }
    return counted;
}
// A fault in the unlearn or reskill check's hook (the game's UI thread): the feature stands down at once, its row on the
// next Pre (respec_fault_lower), as a lock hook's does.
void respec_fault(const char* what, unsigned long code) {
    if (g_respec_off.exchange(true)) return;
    LOGE("respec: %s faulted (0x%08lx): the skill respec from linked sources stands down (the game's own counts and payment)",
         what, code);
    g_respec_fault_report.store(true);
}
void respec_fault_lower() {
    if (g_respec_fault_report.exchange(false))
        lower("crafting.respec_sources", KILN_CAP_REASON_FAULTED,
              "a respec hook faulted (see kiln.log): the game's own respec counts and payment are used");
}
// respec/client.NodeCountRet: eax the game's count, r14d the node's cost (a u16), r12 G38UiPlayData (all pinned).
void respec_node_impl(KilnRegisters& r) {
    const uint32_t item = rd<uint32_t>(reinterpret_cast<const void*>(r.r12), static_cast<size_t>(g_respec.ui_item));
    const uint32_t game = static_cast<uint32_t>(r.rax), need = static_cast<uint32_t>(r.r14 & 0xffff);
    const uint32_t counted = respec_check(kRespecNode, item, game, need);
    if (counted != game) r.rax = counted;  // a 32-bit register: the upper half is zero
}
void hk_respec_node(KilnRegisters& r) {
    __try {
        if (!g_respec_off.load(std::memory_order_relaxed)) respec_node_impl(r);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        respec_fault("the skill tree's unlearn check hook", GetExceptionCode());
    }
}
// respec/client.AltarCountRet: eax the game's count, rdi G38UiPlayData (its respec cost next, compared with eax).
void respec_altar_impl(KilnRegisters& r) {
    const auto* ui = reinterpret_cast<const uint8_t*>(r.rdi);
    const uint32_t item = rd<uint32_t>(ui, static_cast<size_t>(g_respec.ui_item)), need = rd<uint32_t>(ui, static_cast<size_t>(g_respec.ui_cost));
    const uint32_t game = static_cast<uint32_t>(r.rax);
    const uint32_t counted = respec_check(kRespecAltar, item, game, need);
    if (counted != game) r.rax = counted;
}
void hk_respec_altar(KilnRegisters& r) {
    __try {
        if (!g_respec_off.load(std::memory_order_relaxed)) respec_altar_impl(r);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        respec_fault("the Flame Altar's reskill check hook", GetExceptionCode());
    }
}
// respec/client.HeaderCountRet: eax the count the respec tree's item row shows, rbx G38UiPlayData. Display only: a fault
// leaves only the row to the game.
void hk_respec_row(KilnRegisters& r) {
    if (g_respec_row_off.load(std::memory_order_relaxed) || g_respec_off.load(std::memory_order_relaxed)) return;
    __try {
        const uint32_t item = rd<uint32_t>(reinterpret_cast<const void*>(r.rbx), static_cast<size_t>(g_respec.ui_item));
        const uint32_t game = static_cast<uint32_t>(r.rax), counted = respec_check(kRespecRow, item, game, 0);
        if (counted != game) r.rax = counted;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        if (!g_respec_row_off.exchange(true))
            LOGE("respec: the respec tree's item row hook faulted (0x%08lx): the row keeps the game's count; the unlearn and "
                 "reskill checks and the payment still count linked sources", GetExceptionCode());
    }
}

// The client's snapshot (a Kiln client, any thread).
KilnResult KILN_CALL t_set_respec_stock(uint32_t on, const KilnStationStack* rows, uint32_t count) {
    if (on > 1 || (on && (count > KILN_STATIONS_MAX_RESPEC_STOCK || (count && !rows)))) return KILN_ERR_INVALID_ARGUMENT;
    for (uint32_t i = 0; on && i < count; ++i)
        if (!rows[i].item) return KILN_ERR_INVALID_ARGUMENT;
    bool was = false;
    size_t kept = 0;
    {
        std::lock_guard lock(g_respec_snap_mu);
        was = g_respec_on.load(std::memory_order_relaxed);
        g_respec_have.clear();
        if (on)
            for (uint32_t i = 0; i < count; ++i) g_respec_have[rows[i].item] = rows[i].count;
        kept = g_respec_have.size();
        g_respec_on.store(on != 0, std::memory_order_release);
    }
    if (on != static_cast<uint32_t>(was)) {
        for (auto& last : g_respec_last) last.store(0, std::memory_order_relaxed);
        if (const uint32_t n = g_log_respec_snap.take())
            LOGI(on ? "respec: the server counts the respec item from linked sources: %u item row(s)%s"
                    : "respec: respec snapshot cleared: the game's own respec counts again%s",
                 static_cast<unsigned>(kept), n > 1 ? " (more held back)" : "");
    }
    return KILN_OK;
}

KilnResult KILN_CALL t_add_source(KilnOwner owner, const KilnCraftSourceRequest* r, uint64_t* out) {
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    *out = 0;
    if (!r || r->size < KILN_CRAFT_SOURCE_REQUEST_V1 || r->order > 1 || r->filter > 1 || r->item_count > 65536 ||
        (r->item_count && !r->items)) return KILN_ERR_INVALID_ARGUMENT;
    // Since 0.2.0-dev.31.12 (GitHub #147): flags, read only when the request's size covers them.
    const bool has_flags = r->size >= sizeof *r;
    const uint32_t flags = has_flags ? r->flags : 0u;
    constexpr uint32_t kKnownFlags = uint32_t{KILN_CRAFT_SOURCE_LOCKS} | uint32_t{KILN_CRAFT_SOURCE_FILLS} | uint32_t{KILN_CRAFT_SOURCE_BUILDING} |
                                     uint32_t{KILN_CRAFT_SOURCE_RESPEC};  // 31.12; RESPEC since 31.18 (GitHub #150)
    if ((flags & ~kKnownFlags) || (has_flags && r->reserved)) return KILN_ERR_INVALID_ARGUMENT;
    if (KilnResult g = read_gate(); g != KILN_OK) return g;
    if (!G.registered_ok || g_off.load()) return KILN_ERR_UNAVAILABLE;
    if (!source_identity_ok(r->player,r->container,false,source_container(r->container))) return KILN_ERR_INVALID_ARGUMENT;
    std::vector<uint32_t> items;
    if (r->item_count) items.assign(r->items,r->items + r->item_count);
    if (std::find(items.begin(),items.end(),0u) != items.end()) return KILN_ERR_INVALID_ARGUMENT;
    uint32_t session=0;
    // Registration already holds the tick-thread gate: do not depend on our first Pre having cached this world yet.
    if(!source_player(singleton("PlayerBasesSystemServer"),r->player,&session))return KILN_ERR_INVALID_ARGUMENT;
    *out = g_registered.add(owner,r->player,r->container,r->order != 0,r->filter == 0,std::move(items),session,flags);
    g_lock_dirty.store(true, std::memory_order_relaxed);
    g_build_dirty.store(true, std::memory_order_relaxed);
    return *out ? KILN_OK : KILN_ERR_BUFFER_TOO_SMALL;
}
KilnResult KILN_CALL t_remove_source(KilnOwner owner, uint64_t handle) {
    g_lock_dirty.store(true, std::memory_order_relaxed);
    g_build_dirty.store(true, std::memory_order_relaxed);
    return g_registered.remove(owner,handle) ? KILN_OK : KILN_ERR_NOT_FOUND;
}
void KILN_CALL t_cancel_sources(KilnOwner owner) {
    g_registered.cancel_owner(owner);
    g_lock_dirty.store(true, std::memory_order_relaxed);
    g_build_dirty.store(true, std::memory_order_relaxed);
}

KilnResult KILN_CALL t_source_stock(KilnEntity player, uint32_t flags, KilnStationStack* out, uint32_t capacity, uint32_t* out_count) {
    constexpr uint32_t kPurpose = uint32_t{KILN_CRAFT_SOURCE_STOCK_LOCKS} | uint32_t{KILN_CRAFT_SOURCE_STOCK_FILLS} |
                                  uint32_t{KILN_CRAFT_SOURCE_STOCK_BUILDING} | uint32_t{KILN_CRAFT_SOURCE_STOCK_RESPEC};
    if (!out_count || (capacity && !out) || !player || player > kMaxPlayerEntity ||
        (flags & ~(static_cast<uint32_t>(KILN_CRAFT_SOURCE_STOCK_ALL) | kPurpose)) ||
        __builtin_popcount(flags & kPurpose) > 1) return KILN_ERR_INVALID_ARGUMENT;
    *out_count = 0;
    if (KilnResult g = read_gate(); g != KILN_OK) return g;
    if (!G.registered_ok || g_off.load()) return KILN_ERR_UNAVAILABLE;
    if (flags & KILN_CRAFT_SOURCE_STOCK_RESPEC) {  // GitHub #150: the respec item's row from the respec links alone
        if (!g_respec_feat) return KILN_ERR_UNAVAILABLE;
        void* const bases = singleton("PlayerBasesSystemServer");
        if (!bases) return KILN_ERR_UNAVAILABLE;
        // without the Magic chests of the player's base: the payment counts them with the bag, before any link
        uint32_t held = 0;
        for (const KilnStationStack& row : link_rows(bases, player, craft_sources_copy(player), &CraftSource::respec, player_base_chests(player)))
            if (row.item == g_respec.item) held = row.count;
        *out_count = 1;
        if (capacity < 1) return KILN_ERR_BUFFER_TOO_SMALL;
        out[0] = {g_respec.item, held};
        return KILN_OK;
    }
    if (flags & (KILN_CRAFT_SOURCE_STOCK_FILLS | KILN_CRAFT_SOURCE_STOCK_BUILDING)) {  // GitHub #132: one purpose's links alone
        void* const bases = singleton("PlayerBasesSystemServer");
        if (!bases) return KILN_ERR_UNAVAILABLE;
        const bool building = (flags & KILN_CRAFT_SOURCE_STOCK_BUILDING) != 0;
        // both leave out the Magic chests of the player's base: the bag and they pay first and are counted apart (FILLS
        // since 0.2.0-dev.31.16, for the station screen's per-ingredient rows)
        const auto rows = link_rows(bases, player, craft_sources_copy(player), building ? &CraftSource::building : &CraftSource::fills,
                                    player_base_chests(player));
        *out_count = static_cast<uint32_t>(rows.size());
        if (capacity < rows.size()) return KILN_ERR_BUFFER_TOO_SMALL;
        if (!rows.empty()) std::memcpy(out, rows.data(), rows.size() * sizeof rows[0]);
        return KILN_OK;
    }
    if (flags & KILN_CRAFT_SOURCE_STOCK_LOCKS) {  // GitHub #147: the lock-opted links alone (no base, no bag)
        void* const bases = singleton("PlayerBasesSystemServer");
        if (!bases) return KILN_ERR_UNAVAILABLE;
        std::unordered_map<uint32_t, uint64_t> sum;
        lock_counts(bases, player, craft_sources_copy(player), &sum);
        std::vector<KilnStationStack> rows;
        for (const auto& [item, count] : sum) if (count) rows.push_back({item, static_cast<uint32_t>(std::min<uint64_t>(count, UINT32_MAX))});
        std::sort(rows.begin(), rows.end(), [](const auto& a, const auto& b) { return a.item < b.item; });
        *out_count = static_cast<uint32_t>(rows.size());
        if (capacity < rows.size()) return KILN_ERR_BUFFER_TOO_SMALL;
        if (!rows.empty()) std::memcpy(out, rows.data(), rows.size() * sizeof rows[0]);
        return KILN_OK;
    }
    RawBases rb{};
    if(flags & KILN_CRAFT_SOURCE_STOCK_ALL) {rb.sys=singleton("PlayerBasesSystemServer");if(!rb.sys)return KILN_ERR_UNAVAILABLE;}
    else if (!read_bases(&rb)) return KILN_ERR_UNAVAILABLE;
    KilnVec3 position{};
    const int32_t base = !(flags & KILN_CRAFT_SOURCE_STOCK_ALL) && position_of(player,&position) ? base_index_at(rb.sys,position) : -1;
    std::vector<KilnEntity> normal{player};
    if (base >= 0) {
        std::vector<KilnEntity> chests;
        if (!base_list(rb,base,false,&chests)) return KILN_ERR_UNAVAILABLE;
        normal.insert(normal.end(),chests.begin(),chests.end());
    }
    std::unordered_map<uint32_t,std::unordered_set<uint32_t>> seen;
    std::unordered_map<uint32_t,uint64_t> sum;
    const auto stations = sources_copy();
    void* const reg = recipes_registry();
    for (const auto& source : craft_sources_copy(player)) {
        const auto e = source.container;
        if (std::find(normal.begin(),normal.end(),e) != normal.end() || !source_access(rb.sys,player,e)) continue;
        // The normal snapshot already includes this station's outputs. A registration can additionally allow its
        // inputs/leftovers, but no output is counted twice. The payment makes the same per-item union.
        SourceRecipes recipes;
        bool output_source = false;
        if (base >= 0 && std::binary_search(stations.begin(),stations.end(),e)) {
            FsBytes fs{}; KilnVec3 p{};
            output_source = reg && factory_of(e,&fs) && position_of(e,&p) && base_index_at(rb.sys,p) == base && source_recipes(fs,reg,&recipes);
        }
        std::unordered_map<uint32_t,uint64_t> counts;
        count_items(e,&counts);
        for (const auto& [item,count] : counts)
            if (source.allows(item) && !(output_source && lends_item(recipes,item)) && seen[e].insert(item).second) sum[item] += count;
    }
    std::vector<KilnStationStack> rows;
    for (const auto& [item,count] : sum) if(count) rows.push_back({item,static_cast<uint32_t>(std::min<uint64_t>(count,UINT32_MAX))});
    std::sort(rows.begin(),rows.end(),[](const auto& a,const auto& b) {return a.item < b.item;});
    *out_count = static_cast<uint32_t>(rows.size());
    if (capacity < rows.size()) return KILN_ERR_BUFFER_TOO_SMALL;
    if (!rows.empty()) std::memcpy(out,rows.data(),rows.size()*sizeof rows[0]);
    return KILN_OK;
}

// ---- GitHub #132 (0.2.0-dev.31.12): block placements from the player's building links (docs/re/stations.md §13) ----
// The build cursor's material check (S 0x99750 / C 0x28ab60, rules/CursorFlags' bit 12) counts the bag (ebx) plus the
// base's stock at the cursor (build/StockCount -> eax) and compares them with the blocks needed (ebp). A mid hook at that
// call's return (build/CursorStockRet) adds the player's building links when the two fall short: in the server world from
// the server's own count of the links (g_build_cache, the tick thread's), in a Kiln client's own world from the server's
// snapshot (set_build_stock). The placement itself pays through craft_pay_impl (ApplyMaterials -> InventoryPay, scoped by
// hk_build_apply to the blueprint program's call), bag and base first, all or nothing. Never props, undo, dismantling,
// terraforming or impact natives (their checks and payments stay the game's).
bool g_build_feat = false;  // crafting.build_sources resolved (stations_resolve)
std::atomic<bool> g_build_off{false}, g_build_cursor_on{false}, g_build_apply_on{false};
std::mutex g_build_install_mu;
kiln::MidHook2 h_build_cursor;
kiln::RawHook h_build_apply;
std::mutex g_build_mu;  // the server's count of each player's building links (tick thread writes)
std::unordered_map<uint32_t, std::unordered_map<uint32_t, uint64_t>> g_build_cache;
uint64_t g_build_step = 0;
std::mutex g_build_snap_mu;  // a Kiln client's snapshot from its server
std::unordered_map<uint32_t, uint64_t> g_build_have;
std::atomic<bool> g_build_on{false};
LogLimit g_log_build{16}, g_log_build_snap{4};
// A fault in a building hook (a game worker or UI thread): its half stands down at once, its capability row on the next
// Pre (build_fault_lower, as a lock hook's: lock_fault_lower). The cursor's: crafting.build_sources; the menu's rows:
// crafting.build_ui.
std::atomic<bool> g_build_fault_report{false}, g_build_rows_fault_report{false};
std::atomic<uint64_t> g_build_last_line{0};  // the cursor runs every frame: one line per change of (world, item, need, verdict)

void build_cache_refresh(uint64_t step) {
    if (!g_build_dirty.exchange(false) && step < g_build_step + 30 && step >= g_build_step) return;
    g_build_step = step;
    void* const bases = g_srv_bases.load(std::memory_order_acquire);
    std::unordered_map<uint32_t, std::unordered_map<uint32_t, uint64_t>> next;
    for (uint32_t player : g_registered.players()) {
        const auto sources = craft_sources_copy(player);  // this login's links
        if (std::none_of(sources.begin(), sources.end(), [](const CraftSource& s) { return s.building; })) continue;
        auto& counts = next[player];
        for (const KilnStationStack& row : link_rows(bases, player, sources, &CraftSource::building, player_base_chests(player)))
            counts[row.item] = row.count;
    }
    std::lock_guard lock(g_build_mu);
    g_build_cache = std::move(next);
}
// The server world's count for a placement (any thread): false when the player has no building link of this login.
bool build_cached(uint32_t player, uint32_t item, uint64_t* held) {
    std::lock_guard lock(g_build_mu);
    const auto it = g_build_cache.find(player);
    if (it == g_build_cache.end()) return false;
    const auto n = it->second.find(item);
    *held = n == it->second.end() ? 0 : n->second;
    return true;
}
// A Kiln client's snapshot (any thread): false when its server sent none (a vanilla or older server, no building link).
bool build_snapshot(uint32_t item, uint64_t* held) {
    std::lock_guard lock(g_build_snap_mu);
    if (!g_build_on.load(std::memory_order_relaxed)) return false;
    const auto it = g_build_have.find(item);
    *held = it == g_build_have.end() ? 0 : it->second;
    return true;
}

// The count the cursor's check should see: the game's (`base` + `bag`) when it covers `need` or nothing is linked, else
// the base's count raised by what the links hold (capped so base + bag stays a u32). Logged per change.
uint32_t build_check(uint8_t world, uint32_t player, uint32_t item, uint32_t base, uint32_t bag, uint32_t need, bool linked_known,
                     uint64_t linked) {
    const uint64_t have = static_cast<uint64_t>(base) + bag;
    if (!need || have >= need || !linked_known) return base;
    const bool covered = have + linked >= need;
    const uint64_t key = (static_cast<uint64_t>(world) << 56) ^ (static_cast<uint64_t>(item) << 24) ^ (static_cast<uint64_t>(need) << 1) ^
                         (covered ? 1u : 0u);
    if (g_build_last_line.exchange(key) != key)
        if (const uint32_t n = g_log_build.take()) {
            const char* where = world == 0 ? "server" : world == 2 ? "client" : "other";
            LOGI("build: the %s world's material check of item 0x%08x (player %u) %s: bag %u + base %u + linked %llu, needs %u%s",
                 where, item, player, covered ? "counts the building links" : "refused: short count", bag, base,
                 static_cast<unsigned long long>(linked), need, n > 1 ? " (more held back)" : "");
        }
    if (!covered) return base;  // the game's own verdict (missing materials) and its count
    return static_cast<uint32_t>(std::min<uint64_t>(static_cast<uint64_t>(base) + linked, 0xffffffffull - bag));
}

// build/CursorStockRet: eax the base's stock count, ebx the bag's, ebp the blocks needed, rsi the cursor query (the
// material item at [[rsi+0x68]]), rdi the cursor context (its first qword the program context); pinned by the pattern.
void build_cursor_impl(KilnRegisters& r) {
    const uint32_t base = static_cast<uint32_t>(r.rax), bag = static_cast<uint32_t>(r.rbx), need = static_cast<uint32_t>(r.rbp);
    if (!need || static_cast<uint64_t>(base) + bag >= need || !r.rsi || !r.rdi) return;
    const auto* material = rd<const uint32_t*>(reinterpret_cast<const void*>(r.rsi), 0x68);
    const uint32_t item = material ? *material : 0;
    if (!item) return;
    void* const ctx = rd<void*>(reinterpret_cast<const void*>(r.rdi), 0);
    const uint8_t world = ctx_world(ctx);
    uint32_t player = 0;
    uint64_t linked = 0;
    bool known = false;
    if (world == 0) {
        player = ctx_entity(ctx);
        if (!player || player > kMaxPlayerEntity) return;
        known = build_cached(player, item, &linked);
    } else if (world == 2) {
        known = build_snapshot(item, &linked);
    } else {
        return;  // a prediction world's check stays the game's
    }
    const uint32_t counted = build_check(world, player, item, base, bag, need, known, linked);
    if (counted != base) r.rax = counted;
}
void hk_build_cursor(KilnRegisters& r) {
    __try {
        if (!g_build_off.load(std::memory_order_relaxed)) build_cursor_impl(r);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        if (!g_build_off.exchange(true)) {
            LOGE("build: the build cursor's material hook faulted (0x%08lx): block placements from linked sources stand down", GetExceptionCode());
            g_build_fault_report.store(true);
        }
    }
}

// rules/ApplyMaterials(cost context, list, take): only the blueprint program's own take (build/BlueprintApplyRet) may pay
// from building links; any other caller (props, undo, dismantling, terraforming, impact natives) and a give stay the game's.
using BuildApplyFn = bool(__fastcall*)(void* ctx, void* list, uint8_t take);
// Building Tools (0.34.0) redirects the blueprint program's call to its own code, which then calls ApplyMaterials (once
// for the charge; its Replace refunds are gives): the caller is then in its DLL, and the game's call is found behind it
// (kiln_call_site.hpp), so a placement it routes pays from the links as the game's own does. A take that cannot be
// traced stays the game's payment (bag and base), with one kiln.log line per module.
std::mutex g_build_foreign_mu;
std::vector<std::string> g_build_foreign;
void note_foreign_take(uintptr_t ret, uintptr_t site, bool by_frame = false) {
    char module[128];
    kiln::module_name(ret, module, sizeof module);
    const bool blueprint = site && site == g_build.blueprint_ret;
    {
        std::lock_guard lock(g_build_foreign_mu);
        const std::string key = std::string(module) + (blueprint ? (by_frame ? "|f" : "|b") : site ? "|g" : "|-");
        if (g_build_foreign.size() > 64 || std::find(g_build_foreign.begin(), g_build_foreign.end(), key) != g_build_foreign.end()) return;
        g_build_foreign.push_back(key);
    }
    if (blueprint)
        LOGI("build: %s makes the blueprint program's payments (its code calls ApplyMaterials for the game's call%s): block "
             "placements through it pay from linked sources too", module,
             by_frame ? ", found by the program's own frame" : "");
    else if (!site)
        LOGW("build: a materials take from %s could not be traced to the game's call behind it: it pays from the bag and the "
             "base only (the game's payment)", module);
}
// GitHub #148: Building Tools 0.34.0's placements still paid from the bag and the base alone in a hosted game: the walk of
// the stack through its code did not reach the game's call (kiln.log, Fuzen777). The blueprint program passes
// ApplyMaterials its own cost context, ctx = its rsp + 0x50 (lea rcx,[rsp+0x50], pinned by build/BlueprintApplyRet's
// pattern), so the return address its call pushed lies at ctx - 0x58. A module that makes the call for it passes that ctx
// on (Building Tools' pay_hook does). So when that slot, on this thread's stack above this hook's frame, still holds
// build/BlueprintApplyRet, the ctx is mode 0 (the server world's transaction) and that transaction lies in the same frame
// (rbp + 0x520, ctx + 0x5d0), the take is the blueprint program's call in progress, whoever made it. `here`: the hook's
// own return slot. Any fault or doubt: 0.
uintptr_t blueprint_frame_site(const void* ctx, uintptr_t here) {
    const auto c = reinterpret_cast<uintptr_t>(ctx);
    if (!c || !g_build.blueprint_ret || c < 0x58 || c - 0x58 <= here) return 0;
    ULONG_PTR lo = 0, hi = 0;
    GetCurrentThreadStackLimits(&lo, &hi);
    const uintptr_t slot = c - 0x58;
    if (slot < lo || c + 0x10 > hi) return 0;
    __try {
        if (*reinterpret_cast<const uintptr_t*>(slot) != g_build.blueprint_ret || *reinterpret_cast<const uint8_t*>(c) != 0) return 0;
        const uintptr_t tx = *reinterpret_cast<const uintptr_t*>(c + 8);
        return tx > c && tx < hi && tx - c < 0x8000 ? g_build.blueprint_ret : 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}
uintptr_t build_payment_site(uintptr_t ret, const void* ctx, uintptr_t here) {
    const auto* h = kiln::hooks_v2(g_api);
    const bool readable = h && h->header.size >= offsetof(KilnHooksApi, read_original) + sizeof(h->read_original) && h->read_original;
    uintptr_t site = kiln::game_call_of(ret, g_build.apply, [&](uintptr_t at, void* out, size_t n) {
        return readable && h->read_original(at, out, n) == KILN_OK;
    });
    bool by_frame = false;
    if (!site && ret && ret != g_build.blueprint_ret) by_frame = (site = blueprint_frame_site(ctx, here)) != 0;
    if (site != ret) note_foreign_take(ret, site, by_frame);
    return site;
}
// never inlined: `here` must be this function's own return slot (blueprint_frame_site looks above it)
__declspec(noinline) bool __fastcall hk_build_apply(void* ctx, void* list, uint8_t take) {
    const uintptr_t ret = h_build_apply.caller();  // before the original
    const auto here = reinterpret_cast<uintptr_t>(_AddressOfReturnAddress());
    struct Scope {
        bool saved = t_build_pay;
        ~Scope() { t_build_pay = saved; }
    } scope;
    const bool candidate = take != 0 && g_build.blueprint_ret && !g_build_off.load(std::memory_order_relaxed);
    const uintptr_t site = candidate && ret != g_build.blueprint_ret ? build_payment_site(ret, ctx, here) : ret;
    t_build_pay = candidate && site == g_build.blueprint_ret;
    return h_build_apply.original<BuildApplyFn>()(ctx, list, take);
}

// True when the only bytes of [at, at + size) that are not the game's or Kiln's are the rel32 of the game's call that ends at
// ret: another module redirected that call to its own code (Building Tools 0.34.0 does so with the blueprint program's
// payment call and the build cursor's stock count). The cursor's hook reads the call's result whoever computed it; the
// payment's scope finds the game's call behind the module's code (build_payment_site).
bool only_call_redirected(const KilnHooksApi* h, uintptr_t at, size_t size, uintptr_t ret) {
    if (!ret || !at || ret < at + 5 || ret > at + size) return false;
    const auto ok = [&](uintptr_t a, size_t n) {
        if (!n) return true;
        uint32_t s = KILN_CODE_UNKNOWN;
        uint64_t g = 0;
        return h->code_state(a, n, &s, &g) == KILN_OK && (s == KILN_CODE_ORIGINAL || s == KILN_CODE_MANAGED);
    };
    return ok(at, ret - 4 - at) && ok(ret, at + size - ret);  // the E8 itself is the file's, its rel32 is not
}
std::mutex g_build_redirect_mu;
std::vector<std::string> g_build_redirects;
// Once per call site and module: which module the game's call now enters.
void note_call_redirect_to(const char* what, uintptr_t ret, uintptr_t to) {
    char module[128] = "another module";
    if (to) kiln::module_name(to, module, sizeof module);
    {
        std::lock_guard lock(g_build_redirect_mu);
        const std::string key = std::string(what) + "|" + module;
        if (std::find(g_build_redirects.begin(), g_build_redirects.end(), key) != g_build_redirects.end()) return;
        g_build_redirects.push_back(key);
    }
    const auto base = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    LOGI("build: %s (rva 0x%llx) is redirected to %s's code: the building hooks follow the game's call through it", what,
         static_cast<unsigned long long>(ret - 5 - base), module);
}
uintptr_t live_call_target(uintptr_t ret) {
    __try {
        return kiln::call_target(reinterpret_cast<const uint8_t*>(ret - 5), ret);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}
// Foreign code on what the building hooks read or rely on: the cursor's stock-count call and the count function (e.g.
// Cross-Base Storage's build check), ApplyMaterials' entry and take, the blueprint program's call. Named once in kiln.log.
// A redirect of the cursor's or the blueprint program's call alone is followed, not stood down for (only_call_redirected).
bool build_code_ok(bool cursor, bool payment, PreviewCodeRefusal* why) {
    const auto* h = kiln::hooks_v2(g_api);
    if (!h || h->header.size < offsetof(KilnHooksApi, code_state) + sizeof(h->code_state) || !h->code_state) {
        why->name = "kiln.hooks' code-state check";
        return false;
    }
    // call: the return address of the game's call in the range whose rel32 another module may redirect
    struct Dep { uintptr_t at; size_t size; const char* name; bool want; uintptr_t call; };
    for (const Dep& d : {Dep{g_build.cursor_ret ? g_build.cursor_ret - 98 : 0, 98 + 6, "the build cursor's stock-count call", cursor, g_build.cursor_ret},
                         Dep{g_build.stock_fn, 0x40, "the base stock count (build check)", cursor, 0},
                         Dep{g_build.apply, 0x40, "ApplyMaterials", payment, 0},
                         Dep{g_build.paid ? g_build.paid - 17 : 0, 17 + 13, "ApplyMaterials' inventory payment call", payment, 0},
                         Dep{g_build.blueprint_ret ? g_build.blueprint_ret - 17 : 0, 17 + 4, "the blueprint program's payment call", payment,
                             g_build.blueprint_ret}}) {
        if (!d.want) continue;
        uint32_t state = KILN_CODE_UNKNOWN;
        uint64_t generation = 0;
        const KilnResult rc = d.at ? h->code_state(d.at, d.size, &state, &generation) : KILN_ERR_INVALID_ARGUMENT;
        if (rc == KILN_OK && state == KILN_CODE_FOREIGN && only_call_redirected(h, d.at, d.size, d.call)) {
            note_call_redirect_to(d.name, d.call, live_call_target(d.call));
            continue;
        }
        if (rc != KILN_OK || (state != KILN_CODE_ORIGINAL && state != KILN_CODE_MANAGED)) {
            *why = {d.name, d.at, d.size, state, rc};
            return false;
        }
    }
    return true;
}
void build_stand_down(const char* what, const PreviewCodeRefusal& why) {
    char note[512]{};
    preview_code_note(why, note, sizeof note);
    LOGW("build: %s stands down%s (the game's own material check and payment are used)", what, note);
    lower("crafting.build_sources", KILN_CAP_REASON_FAULTED, "another module patched the code a building hook reads (see kiln.log)");
}
// ---- the build menu's material counts (crafting.build_ui, a Kiln client; docs/re/stations.md §13) ----
// The material selector's gatherer (build/client.MaterialRows, C 0x40e5d0) fills seven groups of kRowGroupStride bytes at
// its selector state, one per material pool kind: each group's rows {ptr +0, count +8, capacity +0x10, grow +0x18},
// 24-byte rows with the item at +0 and the aggregate stock (u64) at +0x10, and its sort mode byte at +0x638 (the
// catalogue's tabs: 0 Backpack & Magic Chests, 1 Last Used, 2 by amount, 3 All Materials / item sets, 4 Favorites). A group
// it rebuilds has every row's stock zeroed; then, through the row add (build/client.MaterialRowAdd: add to the item's row,
// or append one), the bag's stacks and, per pool material, the base's published stock (build/client.StockRowsRet, the
// return of that add). Only Last Used and Favorites get a zero row per listed material first, and in those two modes the
// add never appends. So a material only the links hold has no row in the other tabs, the hammer's list or under the
// cursor (GitHub #148: 31.12 to 31.14 raised existing rows after the gatherer, and those were only Last Used and
// Favorites). Since #148 kiln-items adds the links where the game adds the base's stock: a mid hook at
// build/client.StockRowsRet (the gatherer's per-material step after the base-stock add, every path of the loop) calls the
// game's own row add with the server's building snapshot for that material, once per group and gatherer call. The row add
// then appends a row where the base's stock would (never in Last Used and Favorites), the amount sort that follows sees
// the sum, a group the call does not rebuild is not touched (the loop skips it), and the next rebuild zeroes it again:
// nothing to take back. build/client.MaterialRowsBody / Done (after the prologue, at the epilogue) open and close the
// call: its snapshot and what it added.
constexpr uint32_t kRowGroups = 7;
constexpr size_t kRowGroupStride = 0x648, kRowStride = 24, kRowStock = 0x10;
constexpr uint64_t kRowMax = 4096;
// build/client.MaterialRowAdd(group, {item, count, 0}, container key, slot, pool row): the game's add (C 0x40c010)
using RowAddFn = void(__fastcall*)(void* group, const uint32_t* record, uint32_t key, uint64_t slot, uint8_t pool_row);
using RowSnapshot = std::shared_ptr<const std::unordered_map<uint32_t, uint64_t>>;
RowSnapshot g_build_rows_snap;  // under g_build_snap_mu: set_build_stock's rows, shared by the calls that read them
bool g_build_ui_feat = false;   // crafting.build_ui resolved (stations_resolve, a client)
std::atomic<bool> g_build_rows_on{false}, g_build_rows_off{false};
kiln::MidHook2 h_build_rows_body, h_build_rows_done, h_build_rows_add;
uintptr_t g_build_rows_fn = 0, g_build_rows_body = 0, g_build_rows_done = 0, g_build_row_add = 0, g_build_stock_rows_ret = 0,
          g_build_row_stock = 0, g_build_no_container = 0;
std::atomic<uint64_t> g_build_rows_added{0};  // rows the links raised or added (the first one is logged)

// The gatherer's two halves, mid hooks inside it since 0.2.0-dev.31.13 (until then a detour on its entry, which hid the
// function from Building Tools 0.34.0: it finds it by its first 35 bytes, 4 s after it loads, for its full material list
// and partial area actions; XHL). build/client.MaterialRowsBody (entry + 0x23, after the prologue: rcx the selector state)
// opens the call with the snapshot it adds; build/client.MaterialRowsDone (+ 0xa8a, the epilogue on every path: rsi the
// state) closes it. The function's rsp is the same at both and at build/client.StockRowsRet and pairs them
// (kiln::MidFrames); kiln-tools' material picker has its halves on the same two sites.
struct RowsCall {
    explicit RowsCall(uint8_t* s) : state(s) {
        std::lock_guard lock(g_build_snap_mu);
        if (g_build_on.load(std::memory_order_relaxed) && g_build_rows_snap && !g_build_rows_snap->empty()) linked = g_build_rows_snap;
    }
    uint8_t* state;
    RowSnapshot linked;           // null: this call shows the game's counts
    std::vector<uint64_t> added;  // (group << 32) | item: the links are added once per group
};
thread_local kiln::MidFrames<RowsCall> t_build_rows;

// One pool material of the gatherer's stock loop: its links into its group's row through the game's own row add.
struct RowAddJob {
    RowsCall* call;
    const uint8_t* pool;  // r15: the material pool (its materials: i32 offset +8 from +8, u32 count +0xc)
    uint64_t index;       // r14: this material's index in it
    uint32_t group;       // r12b: the pool kind's group
    bool added;
};
void build_rows_add_impl(void* p) {
    auto& j = *static_cast<RowAddJob*>(p);
    const int32_t off = rd<int32_t>(j.pool, 8);
    if (!off || j.index >= rd<uint32_t>(j.pool, 0xc)) return;
    const uint32_t item = rd<uint32_t>(j.pool + 8 + off, static_cast<size_t>(j.index) * 4);
    if (!item) return;
    const auto it = j.call->linked->find(item);
    if (it == j.call->linked->end() || !it->second) return;
    const uint64_t key = (static_cast<uint64_t>(j.group) << 32) | item;
    if (std::find(j.call->added.begin(), j.call->added.end(), key) != j.call->added.end()) return;
    j.call->added.push_back(key);
    uint8_t* const group = j.call->state + static_cast<size_t>(j.group) * kRowGroupStride;
    const auto* rows = rd<const uint8_t*>(group, 0);
    const uint64_t n = rd<uint64_t>(group, 8), capacity = rd<uint64_t>(group, 0x10);
    if (n > kRowMax || (n && !rows)) return;
    bool has_row = false;
    for (uint64_t i = 0; i < n && !has_row; ++i) has_row = rd<uint32_t>(rows, i * kRowStride) == item;
    // the game's add appends with its grow callback when the rows are full, and stops the game without one
    if (!has_row && n + 1 > capacity && !rd<void*>(group, 0x18)) return;
    const uint32_t record[3] = {item, static_cast<uint32_t>(std::min<uint64_t>(it->second, UINT32_MAX)), 0};
    reinterpret_cast<RowAddFn>(g_build_row_add)(group, record, rd<uint32_t>(reinterpret_cast<const void*>(g_build_no_container), 0), 0, 0);
    j.added = true;
}
void build_rows_enter(uint8_t* state, uintptr_t frame) {
    t_build_rows.enter(frame, state);  // no room or no memory: this call shows the game's counts
}
void build_rows_add(uint8_t* state, uintptr_t frame, const uint8_t* pool, uint64_t index, uint32_t group) {
    if (g_build_rows_off.load(std::memory_order_relaxed) || g_build_off.load(std::memory_order_relaxed)) return;
    RowsCall* const call = t_build_rows.leave(frame);  // this call's own (never popped here: its last half pops it)
    if (!call || call->state != state || !state || !call->linked || !pool || group >= kRowGroups) return;
    RowAddJob j{call, pool, index, group, false};
    unsigned long code = 0;
    if (!guard(&build_rows_add_impl, &j, &code)) {
        call->linked.reset();
        if (!g_build_rows_off.exchange(true)) {
            LOGE("build: the build menu's material rows could not be read (0x%08lx): its counts are the game's again", code);
            g_build_rows_fault_report.store(true);
        }
        return;
    }
    if (j.added && g_build_rows_added.fetch_add(1, std::memory_order_relaxed) == 0)
        LOGI("build: the build menu lists a material from the building links (group %u)", group);
}
void build_rows_leave(uintptr_t frame) {
    if (t_build_rows.leave(frame)) t_build_rows.pop();  // its first half did not run (the hooks went in while it was inside)
}
void hk_build_rows_body(KilnRegisters& r) { build_rows_enter(reinterpret_cast<uint8_t*>(r.rcx), r.rsp); }
// build/client.StockRowsRet: rsi the state, r15 the pool, r14 the material's index, r12b its group (pinned by the patterns)
void hk_build_rows_add(KilnRegisters& r) {
    build_rows_add(reinterpret_cast<uint8_t*>(r.rsi), r.rsp, reinterpret_cast<const uint8_t*>(r.r15), r.r14,
                   static_cast<uint32_t>(r.r12 & 0xff));
}
void hk_build_rows_done(KilnRegisters& r) { build_rows_leave(r.rsp); }
// Once the cursor hook is in: the menu's counts too, unless another module changed the gatherer or the row add.
void build_rows_install() {
    if (!g_build_ui_feat || g_build_rows_on.load() || g_build_rows_off.load()) return;
    const auto* h = kiln::hooks_v2(g_api);
    PreviewCodeRefusal why;
    if (!h || h->header.size < offsetof(KilnHooksApi, code_state) + sizeof(h->code_state) || !h->code_state) {
        why.name = "kiln.hooks' code-state check";
    } else {
        struct Dep { uintptr_t at; size_t size; const char* name; };
        for (const Dep& d : {Dep{g_build_rows_fn, 0x40, "the build menu's material gatherer"},
                             Dep{g_build_rows_done, 8, "the gatherer's epilogue"},
                             Dep{g_build_stock_rows_ret ? g_build_stock_rows_ret - 58 : 0, 58 + 7, "its base-stock record and next material"},
                             Dep{g_build_row_add, 0x134, "the material row add"}}) {
            uint32_t state = KILN_CODE_UNKNOWN;
            uint64_t generation = 0;
            const KilnResult rc = d.at ? h->code_state(d.at, d.size, &state, &generation) : KILN_ERR_INVALID_ARGUMENT;
            if (rc != KILN_OK || (state != KILN_CODE_ORIGINAL && state != KILN_CODE_MANAGED)) {
                why = {d.name, d.at, d.size, state, rc};
                break;
            }
        }
    }
    if (why.name) {
        g_build_rows_off.store(true);
        char note[512]{};
        preview_code_note(why, note, sizeof note);
        LOGW("build: the build menu's counts of linked sources stand down%s (its counts are the game's; the cursor and the "
             "placement still count the links)", note);
        lower("crafting.build_ui", KILN_CAP_REASON_FAULTED, "another module patched the build menu's gatherer (see kiln.log)");
        return;
    }
    // the last half first, the first half last: a call already inside the gatherer finds no first half and keeps the
    // game's counts (its stock step finds no open call)
    const bool done = h_build_rows_done.install(g_api, g_owner, g_build_rows_done, &hk_build_rows_done, "build/client.MaterialRowsDone");
    const bool add = done && h_build_rows_add.install(g_api, g_owner, g_build_stock_rows_ret, &hk_build_rows_add, "build/client.StockRowsRet");
    if (!add || !h_build_rows_body.install(g_api, g_owner, g_build_rows_body, &hk_build_rows_body, "build/client.MaterialRowsBody")) {
        const char* const err = !done ? h_build_rows_done.error() : !add ? h_build_rows_add.error() : h_build_rows_body.error();
        h_build_rows_add.revoke();
        h_build_rows_done.revoke();
        g_build_rows_off.store(true);
        LOGW("build: the build menu's gatherer (build/client.MaterialRowsBody, build/client.StockRowsRet, "
             "build/client.MaterialRowsDone) could not be hooked (%s): the build menu keeps the game's counts", err ? err : "refused");
        lower("crafting.build_ui", KILN_CAP_REASON_FAULTED, "the build menu's hook could not be installed (see kiln.log)");
        return;
    }
    g_build_rows_on.store(true);
    LOGI("build: the build menu's material counts include the building links (the server's snapshot): every tab, the hammer's "
         "list and the cursor");
}

bool build_install_cursor() {
    if (g_build_cursor_on.load()) return true;
    PreviewCodeRefusal why;
    if (!build_code_ok(true, false, &why)) {
        g_build_off.store(true);
        build_stand_down("the build cursor's count of linked sources", why);
        return false;
    }
    if (!h_build_cursor.install(g_api, g_owner, g_build.cursor_ret, &hk_build_cursor, "build/CursorStockRet")) {
        g_build_off.store(true);
        LOGW("build: build/CursorStockRet could not be hooked (%s): block placements from linked sources stand down",
             h_build_cursor.error() ? h_build_cursor.error() : "refused");
        lower("crafting.build_sources", KILN_CAP_REASON_FAULTED, "a building hook could not be installed (see kiln.log)");
        return false;
    }
    g_build_cursor_on.store(true);
    return true;
}
// Either world's Pre: the capability row of a building hook's fault, once.
void build_fault_lower() {
    if (g_build_fault_report.exchange(false))
        lower("crafting.build_sources", KILN_CAP_REASON_FAULTED,
              "a building hook faulted (see kiln.log): the game's own material check and payment are used");
    if (g_build_rows_fault_report.exchange(false))
        lower("crafting.build_ui", KILN_CAP_REASON_FAULTED, "the build menu's material rows could not be read (see kiln.log)");
}
// The server world's Pre (tick thread): the cache, then both hooks once a link with Building exists.
void build_server_pre(uint64_t step) {
    build_fault_lower();
    if (!g_build_feat || !G.registered_ok || !G.craft_ok || g_build_off.load()) return;
    if (!g_build_apply_on.load() && !g_registered.any_building()) return;
    build_cache_refresh(step);
    if (g_build_apply_on.load() && g_build_cursor_on.load()) return;
    std::lock_guard install(g_build_install_mu);
    static bool failed = false;
    if (failed || g_build_off.load()) return;
    PreviewCodeRefusal why;
    if (!build_code_ok(false, true, &why)) {
        failed = true;
        g_build_off.store(true);
        build_stand_down("the server's block placement payment from linked sources", why);
        return;
    }
    if (!g_build_apply_on.load()) {
        if (!h_build_apply.install(g_api, g_owner, g_build.apply, reinterpret_cast<void*>(&hk_build_apply), "build/ApplyMaterials")) {
            failed = true;
            g_build_off.store(true);
            LOGW("build: rules/ApplyMaterials could not be hooked (%s): block placements from linked sources stand down",
                 h_build_apply.error() ? h_build_apply.error() : "refused");
            lower("crafting.build_sources", KILN_CAP_REASON_FAULTED, "a building hook could not be installed (see kiln.log)");
            return;
        }
        g_build_apply_on.store(true);
    }
    if (!build_install_cursor()) {
        failed = true;
        return;
    }
    LOGI("build: a link opted in with Building: the server's material check and blueprint payment count linked sources (bag "
         "and base first)");
}
// The client world's Pre (a client process): the cursor hook once a Kiln server's building snapshot is on.
void build_client_pre() {
    build_fault_lower();
    if (!g_build_feat || g_build_off.load() || !g_build_on.load(std::memory_order_acquire)) return;
    if (g_build_cursor_on.load()) {  // a hosting client's server Pre may have put the cursor's hook in first
        if (!g_build_rows_on.load() && !g_build_rows_off.load()) {
            std::lock_guard install(g_build_install_mu);
            build_rows_install();
        }
        return;
    }
    std::lock_guard install(g_build_install_mu);
    static bool failed = false;
    if (failed || g_build_off.load()) return;
    if (!build_install_cursor()) {
        failed = true;
        return;
    }
    LOGI("build: the server's building snapshot is on: this client's build cursor counts the bag, the base and linked sources");
    build_rows_install();  // the build menu's counts (crafting.build_ui), once
}
KilnResult KILN_CALL t_set_build_stock(uint32_t on, const KilnStationStack* rows, uint32_t count) {
    if (on > 1 || (on && (count > KILN_STATIONS_MAX_CRAFT_STOCK || (count && !rows)))) return KILN_ERR_INVALID_ARGUMENT;
    for (uint32_t i = 0; on && i < count; ++i)
        if (!rows[i].item) return KILN_ERR_INVALID_ARGUMENT;
    bool was = false;
    size_t kept = 0;
    {
        auto next = std::make_shared<std::unordered_map<uint32_t, uint64_t>>();
        for (uint32_t i = 0; on && i < count; ++i) (*next)[rows[i].item] = rows[i].count;
        std::lock_guard lock(g_build_snap_mu);
        was = g_build_on.load(std::memory_order_relaxed);
        g_build_have = *next;
        kept = g_build_have.size();
        g_build_rows_snap = on ? RowSnapshot(std::move(next)) : RowSnapshot();
        g_build_on.store(on != 0, std::memory_order_release);
    }
    if (on != static_cast<uint32_t>(was))
        if (const uint32_t n = g_log_build_snap.take())
            LOGI(on ? "build: the server counts building materials from linked sources: %u item row(s)%s"
                    : "build: building snapshot cleared: the game's own material checks again%s",
                 static_cast<unsigned>(kept), n > 1 ? " (more held back)" : "");
    return KILN_OK;
}

// GitHub #132 (0.2.0-dev.31.16): the FILL flags this build takes (the managed side asks before it names FILL_ITEM).
uint32_t KILN_CALL t_fill_flags() {
    if (!G.fill_ok || g_off.load()) return 0;
    uint32_t f = KILN_STATION_FILL_ADD | KILN_STATION_FILL_EXACT | KILN_STATION_DRY_RUN | KILN_STATION_NO_WATER | KILN_STATION_FILL_NO_OUTPUTS;
    if (G.registered_ok) f |= KILN_STATION_FILL_LINKED;
    if (G.fill_items_ok) f |= KILN_STATION_FILL_ITEM;
    return f;
}

const KilnStationsApi kTable = {
    {sizeof(KilnStationsApi), KILN_STATIONS_VERSION},
    &t_status, &t_base_count, &t_base_at, &t_base_of, &t_base_at_position, &t_base_storage, &t_base_water, &t_base_stock,
    &t_stations, &t_station_read, &t_recipe, &t_recipe_inputs, &t_category, &t_category_at, &t_workshop_recipes, &t_plan,
    &t_submit, &t_cancel, &t_watch, &t_events, &t_last_seq, &t_set_output_source, &t_output_sources, &t_max_chests,
    &t_collect, &t_base_stock_with_stations, &t_set_craft_stock, &t_craft_items, &t_set_fill_answers, &t_fill_asks, &t_add_source, &t_remove_source, &t_cancel_sources, &t_source_stock, &dyes_set_stock,
    &t_set_lock_stock, &t_lock_asks,
    &t_set_build_stock,  // GitHub #132 (0.2.0-dev.31.12), after #147's members
    &t_fill_flags,       // GitHub #132 (0.2.0-dev.31.16)
    &t_set_respec_stock, // GitHub #150 (0.2.0-dev.31.18)
};

bool sym(const char* name, uintptr_t* out) {
    *out = 0;
    return g_api->symbol(str(name), out) == KILN_OK && *out;
}

template <typename F>
bool sym(const char* name, F* out) {
    uintptr_t a = 0;
    if (g_api->symbol(str(name), &a) != KILN_OK || !a) return false;
    *out = reinterpret_cast<F>(a);
    return true;
}
bool cst(const char* name, int64_t* out) { return g_api->constant(str(name), out) == KILN_OK; }
bool feat(const char* name) { return g_api->feature(str(name)); }

// stations_resolve, on a client with stations.stock_ui: the count, its four call sites and the list's fixed-slot count.
// Each one is required; without them the feature is lowered and nothing is hooked.
bool avail_resolve() {
    AvailSites s;
    uintptr_t fixed = 0;
    g_avail_feat = sym("stations/client.UiAvailableCount", &g_avail_fn) && sym("stations/client.AvailableRowRet1", &s.row1) &&
                   sym("stations/client.AvailableRowRet2", &s.row2) && sym("stations/client.AvailableListRet", &s.list) &&
                   sym("stations/client.AvailableOptionRet", &s.option) && sym("stations/client.UiFixedSlotsCount", &fixed) &&
                   g_avail_fn && s.row1 && s.row2 && s.list && s.option && fixed;
    if (!g_avail_feat) {
        lower("stations.stock_ui", KILN_CAP_REASON_SYMBOL_MISSING,
              "stations/client.UiAvailableCount, its call sites or the crafting menu's fixed-slot count did not resolve");
        return false;
    }
    g_avail_sites = s;
    g_avail_fixed = reinterpret_cast<FixedSlotsFn>(fixed);
    return true;
}

// kiln.rules' hooks that carry the craft half (its payment hook) or the client half (its crafting-count hook): checked
// once, at the first Pre of a world, after kiln.rules armed them at that world's start. Without them the row says why.
bool rules_hook(uint32_t bit, std::string* why, uint32_t* reason) {
    const auto* rules = static_cast<const KilnRulesApi*>(g_api->service(str(KILN_RULES_SERVICE), 1));
    KilnRulesStatus st{};
    st.size = sizeof st;
    if (!rules || !rules->status) {
        *reason = KILN_CAP_REASON_SERVICE_MISSING;
        *why = "kiln.rules is not here (kiln-rules.dll): its hook carries this";
        return false;
    }
    if (rules->status(&st) != KILN_OK) {
        *reason = KILN_CAP_REASON_FAULTED;
        *why = "kiln.rules did not answer its status";
        return false;
    }
    if (st.hooks & bit) return true;
    st.detail[sizeof st.detail - 1] = 0;
    const bool creative = std::strstr(st.detail, "Creative Mode runs") != nullptr;
    *reason = creative ? KILN_CAP_REASON_POLICY : KILN_CAP_REASON_FAULTED;
    *why = std::string(creative ? "Creative Mode runs, so kiln.rules' hook stands aside" : "kiln.rules' hook is not installed") +
           " (" + st.detail + ")";
    return false;
}

// ---- GitHub #147: the lock hooks' symbols, their code check and their installation on demand ----
// stations_resolve: items.lock_sources with its pinned layout (the patterns pin rbp-0x68, [rsp+0x58], rbp+0x620,
// rbp+0x230; the captured InteractionLock offsets must be the ones the handlers read).
void lock_resolve() {
    g_lock_row_feat = false;
    if (!feat("items.lock_sources")) return;
    int64_t item = -1, amount = -1, mask = -1;
    const bool layout = cst("locks/lock.item", &item) && cst("locks/lock.amount", &amount) && cst("locks/lock.mask", &mask) &&
                        item == 0x10 && amount == 0x16 && mask == 0x18;
    g_lock_feat = layout && sym("locks/LockCountRet", &g_lock.count_ret) && sym("locks/InventoryItemCount", &g_lock.count_fn) &&
                  sym("locks/LockPaid", &g_lock.paid) && sym("gems/PayMaterial", &g_lock.pay_fn) &&
                  sym("world/CurrentEntityId", &g_lock_current) && call_matches(g_lock.count_ret, g_lock.count_fn) &&
                  call_matches(g_lock.paid, g_lock.pay_fn) && E.job_storage && E.component_map_find;
    if (g_lock_feat && g_api->side() == KILN_SIDE_CLIENT) {
        g_lock_feat = sym("locks/client.UiLockCountRet", &g_lock.ui_ret) && call_matches(g_lock.ui_ret, g_lock.count_fn);
        // the requirement line: display only, not part of the feature (g_lock_row_feat)
        g_lock_row_feat = g_lock_feat && sym("locks/client.LockRowCount", &g_lock.row);
        if (g_lock_feat && !g_lock_row_feat)
            LOGW("locks: the lock prompt's requirement line keeps the game's count (the bag only): locks/client.LockRowCount did not "
                 "resolve on this build; the lock checks, the locked prompt and the payment still count linked sources");
    }
    if (!G.pay) sym("gems/PayMaterial", &G.pay);
    if (!g_lock_feat) {
        LOGW("locks: locks from linked sources are unavailable: a symbol or the InteractionLock layout of items.lock_sources did not check out");
        lower("items.lock_sources", KILN_CAP_REASON_SYMBOL_MISSING,
              "locks/LockCountRet, locks/LockPaid, locks/InventoryItemCount (and on a client locks/client.UiLockCountRet) or the "
              "InteractionLock offsets did not check out on this build");
    }
}

// GitHub #132: the building hooks' symbols (sigdb server.d/build-sources.json), each call they rely on checked. The build
// cursor's stock-count call and the blueprint program's payment call as the exe file has them (call_matches_original):
// Building Tools (0.34.0) redirects their live rel32 4 s after it loads, which can be before this runs; build_code_ok then
// follows that redirect when the hooks go in, as it does for one made later.
void build_resolve() {
    if (!feat("crafting.build_sources")) return;
    if (!g_lock_current) sym("world/CurrentEntityId", &g_lock_current);
    g_build_feat = sym("build/CursorStockRet", &g_build.cursor_ret) && sym("build/StockCount", &g_build.stock_fn) &&
                   sym("rules/ApplyMaterials", &g_build.apply) && sym("build/BlueprintApplyRet", &g_build.blueprint_ret) &&
                   sym("build/ApplyMaterialsPaid", &g_build.paid) && sym("rules/InventoryPay", &g_build.pay_fn) &&
                   call_matches_original(g_build.cursor_ret, g_build.stock_fn) &&
                   call_matches_original(g_build.blueprint_ret, g_build.apply) && call_matches(g_build.paid, g_build.pay_fn) &&
                   g_lock_current && E.job_storage;
    if (g_build_feat && g_api->side() == KILN_SIDE_CLIENT && feat("crafting.build_ui")) {  // the build menu's counts
        g_build_ui_feat = sym("build/client.MaterialRows", &g_build_rows_fn) && sym("build/client.MaterialRowsBody", &g_build_rows_body) &&
                          sym("build/client.MaterialRowsDone", &g_build_rows_done) && g_build_rows_body > g_build_rows_fn &&
                          g_build_rows_body - g_build_rows_fn < 0x40 && g_build_rows_done > g_build_rows_body &&
                          g_build_rows_done - g_build_rows_fn < 0xb3c && sym("build/client.StockRowsRet", &g_build_stock_rows_ret) &&
                          sym("build/client.MaterialRowAdd", &g_build_row_add) && sym("build/client.MaterialRowStock", &g_build_row_stock) &&
                          call_matches(g_build_stock_rows_ret, g_build_row_add) && g_build_row_stock > g_build_row_add &&
                          g_build_row_stock < g_build_row_add + 0x134 && g_build_stock_rows_ret > g_build_rows_body &&
                          g_build_stock_rows_ret < g_build_rows_done && sym("build/client.NoContainerKey", &g_build_no_container);
        if (!g_build_ui_feat)
            lower("crafting.build_ui", KILN_CAP_REASON_SYMBOL_MISSING, "the build menu's gatherer or row layout did not check out");
    }
    if (!g_build_feat) {
        g_build = {};
        LOGW("build: block placements from linked sources are unavailable: a symbol of crafting.build_sources did not check out");
        lower("crafting.build_sources", KILN_CAP_REASON_SYMBOL_MISSING,
              "build/CursorStockRet, build/StockCount, build/BlueprintApplyRet, build/ApplyMaterialsPaid or rules/ApplyMaterials "
              "did not check out on this build");
    }
}

// Foreign code (another module's hook or patch) on what a lock hook reads or patches: the call sites and the two
// functions' entries. Named in kiln.log; the hooks then stay out.
struct LockCodeRefusal {
    const char* name = nullptr;
    uintptr_t at = 0;
    size_t size = 0;
    uint32_t state = KILN_CODE_UNKNOWN;
    KilnResult rc = KILN_ERR_UNAVAILABLE;
};
bool lock_dep_ok(uintptr_t at, size_t size, const char* name, LockCodeRefusal* why) {
    const auto* h = kiln::hooks_v2(g_api);
    if (!h || h->header.size < offsetof(KilnHooksApi, code_state) + sizeof(h->code_state) || !h->code_state) {
        why->name = "kiln.hooks' code-state check";
        return false;
    }
    uint32_t state = KILN_CODE_UNKNOWN;
    uint64_t generation = 0;
    const KilnResult rc = at ? h->code_state(at, size, &state, &generation) : KILN_ERR_INVALID_ARGUMENT;
    if (rc != KILN_OK || (state != KILN_CODE_ORIGINAL && state != KILN_CODE_MANAGED)) {
        *why = {name, at, size, state, rc};
        return false;
    }
    return true;
}
bool lock_code_ok(bool client_sites, bool server_sites, LockCodeRefusal* why) {
    struct Dep { uintptr_t at; size_t size; const char* name; bool want; };
    for (const Dep& d : {Dep{g_lock.count_ret ? g_lock.count_ret - 0x32 : 0, 0x48, "the lock check's item count call", true},
                         Dep{g_lock.count_fn, 0x40, "InventoryItemCount", true},
                         Dep{g_lock.ui_ret ? g_lock.ui_ret - 0x49 : 0, 0x6a, "the locked prompt's item count call", client_sites},
                         Dep{g_lock.paid ? g_lock.paid - 0x20 : 0, 0x3c, "interaction_attempt's lock payment call", server_sites},
                         Dep{g_lock.pay_fn, 0x2b, "PayMaterial", server_sites}}) {
        if (d.want && !lock_dep_ok(d.at, d.size, d.name, why)) return false;
    }
    return true;
}
void lock_stand_down(const char* what, const LockCodeRefusal& why) {
    PreviewCodeRefusal p{why.name, why.at, why.size, why.state, why.rc};
    char note[512]{};
    preview_code_note(p, note, sizeof note);
    LOGW("locks: %s stands down%s (the game's own lock checks and payments are used)", what, note);
    lower("items.lock_sources", KILN_CAP_REASON_FAULTED, "another module patched the code a lock hook reads (see kiln.log)");
}
bool lock_install(kiln::MidHook2& hook, std::atomic<bool>& on, uintptr_t at, void (*fn)(KilnRegisters&), const char* label) {
    if (on.load()) return true;
    if (!hook.install(g_api, g_owner, at, fn, label)) {
        if (!g_lock_off.exchange(true))
            LOGW("locks: %s could not be hooked (%s): locks from linked sources stand down", label, hook.error() ? hook.error() : "refused");
        lower("items.lock_sources", KILN_CAP_REASON_FAULTED, "a lock hook could not be installed (see kiln.log)");
        return false;
    }
    on.store(true);
    return true;
}

// The requirement line's hook (a client's Pre, under g_lock_install_mu), after the press and the locked prompt went in. On
// its own: its code check, a failed install (and later a fault, lock_row_fault) leave only the line to the game. False:
// not in.
bool lock_row_install() {
    if (g_lock_row_on.load()) return true;
    if (!g_lock_row_feat || g_lock_row_off.load()) return false;
    LockCodeRefusal why;
    if (!lock_dep_ok(g_lock.row - 0x44, 0x6f, "the lock prompt's requirement line count", &why)) {
        g_lock_row_off.store(true);
        PreviewCodeRefusal p{why.name, why.at, why.size, why.state, why.rc};
        char note[512]{};
        preview_code_note(p, note, sizeof note);
        LOGW("locks: the lock prompt's requirement line stands down%s (the line keeps the game's count, the bag only; the lock "
             "checks, the locked prompt and the payment still count linked sources)", note);
        return false;
    }
    if (!h_lock_row.install(g_api, g_owner, g_lock.row, &hk_lock_row, "locks/client.LockRowCount")) {
        g_lock_row_off.store(true);
        LOGW("locks: locks/client.LockRowCount could not be hooked (%s): the lock prompt's requirement line keeps the game's count "
             "(the bag only); the lock checks, the locked prompt and the payment still count linked sources",
             h_lock_row.error() ? h_lock_row.error() : "refused");
        return false;
    }
    g_lock_row_on.store(true);
    return true;
}

// ---- GitHub #150: the respec symbols, their code check and the client hooks on demand ----
// stations_resolve: crafting.respec_sources. The payer's payment call must call rules/InventoryPay (the hook that carries
// the payment) and the client's count returns must follow calls to stations/client.UiAvailableCount; the respec tree's
// item row is display only (g_respec_row_feat), never part of the feature.
void respec_resolve() {
    g_respec_row_feat = false;
    if (!feat("crafting.respec_sources")) return;
    RespecSites s;
    int64_t item = 0;
    bool ok = cst("respec/item", &item) && item > 0 && item <= 0xffffffffll && sym("respec/Payer", &s.payer) &&
              sym("respec/RespecPaid", &s.paid) && sym("rules/InventoryPay", &s.pay_fn) && call_matches(s.paid, s.pay_fn) &&
              s.paid > s.payer && s.paid - s.payer < 0x21a;
    if (ok && g_api->side() == KILN_SIDE_CLIENT) {
        ok = sym("respec/client.NodeCountRet", &s.node_ret) && sym("respec/client.AltarCountRet", &s.altar_ret) &&
             sym("stations/client.UiAvailableCount", &s.avail_fn) && cst("respec/client.ui.item", &s.ui_item) &&
             cst("respec/client.ui.cost", &s.ui_cost) && s.ui_item > 0 && s.ui_cost == s.ui_item + 4 &&
             call_matches(s.node_ret, s.avail_fn) && call_matches(s.altar_ret, s.avail_fn);
        g_respec_row_feat = ok && sym("respec/client.HeaderCountRet", &s.header_ret) && call_matches(s.header_ret, s.avail_fn);
        if (ok && !g_respec_row_feat)
            LOGW("respec: the respec tree's item row keeps the game's count: respec/client.HeaderCountRet did not resolve on this build "
                 "(the unlearn and reskill checks and the payment still count linked sources)");
    }
    s.item = static_cast<uint32_t>(item);
    g_respec_feat = ok;
    if (ok) {
        g_respec = s;
        return;
    }
    g_respec = {};
    LOGW("respec: the skill respec from linked sources is unavailable: a symbol of crafting.respec_sources did not check out");
    lower("crafting.respec_sources", KILN_CAP_REASON_SYMBOL_MISSING,
          "respec/Payer, respec/RespecPaid (a call to rules/InventoryPay), respec/item (and on a client the unlearn and reskill "
          "checks' UiAvailableCount returns) did not check out on this build");
}

// Foreign code on what the respec path relies on: the payer's entry (its cost test and stock mode) and its payment call
// (on a server), the client checks' count calls. Named in kiln.log; the feature then stands down.
bool respec_code_ok(bool client_sites, bool server_sites, LockCodeRefusal* why) {
    struct Dep { uintptr_t at; size_t size; const char* name; bool want; };
    for (const Dep& d : {Dep{g_respec.payer, 0x3b, "the respec payer's entry", server_sites},
                         Dep{g_respec.paid ? g_respec.paid - 0x4a : 0, 0x4a + 0x18, "the respec payer's payment call", server_sites},
                         Dep{g_respec.node_ret ? g_respec.node_ret - 0x39 : 0, 0x39 + 0x14, "the skill tree's unlearn check count", client_sites},
                         Dep{g_respec.altar_ret ? g_respec.altar_ret - 0x3c : 0, 0x3c + 0xf, "the Flame Altar's reskill check count", client_sites}}) {
        if (d.want && !lock_dep_ok(d.at, d.size, d.name, why)) return false;
    }
    return true;
}
void respec_stand_down(const char* what, const LockCodeRefusal& why) {
    PreviewCodeRefusal p{why.name, why.at, why.size, why.state, why.rc};
    char note[512]{};
    preview_code_note(p, note, sizeof note);
    LOGW("respec: %s stands down%s (the game's own respec counts and payment are used)", what, note);
    lower("crafting.respec_sources", KILN_CAP_REASON_FAULTED, "another module patched the code the respec path reads (see kiln.log)");
}

// The server world's Pre (tick thread): the payment may use links once a link with Respec exists and the code checked out.
// Nothing is hooked here: the payment is kiln.rules' InventoryPay hook (craft_pay_impl's respec branch).
void respec_server_pre() {
    respec_fault_lower();
    if (!g_respec_feat || !G.registered_ok || g_respec_off.load() || g_respec_ready.load()) return;
    if (!g_registered.any_respec() && !(respec_lab_on() && respec_lab_any())) return;
    LockCodeRefusal why;
    if (!respec_code_ok(false, true, &why)) {
        g_respec_off.store(true);
        respec_stand_down("the skill respec payment from linked sources", why);
        return;
    }
    g_respec_ready.store(true, std::memory_order_release);
    LOGI("respec: a link opted in with Respec: the skill tree's paid reset and unlearn pay the respec item 0x%08x from linked "
         "sources after the bag and the Magic chests (the join check's reset stays free)", g_respec.item);
}

// The client world's Pre (a client process): the unlearn and reskill checks' hooks once a Kiln server's respec snapshot is
// on; the item row's hook on its own (display only).
void respec_client_pre() {
    respec_fault_lower();
    if (!g_respec_feat || g_respec_off.load() || !g_respec_on.load(std::memory_order_acquire)) return;
    if (g_respec_node_on.load() && g_respec_altar_on.load() &&
        (g_respec_row_on.load() || !g_respec_row_feat || g_respec_row_off.load())) return;
    static bool failed = false;
    if (failed) return;
    LockCodeRefusal why;
    if (!respec_code_ok(true, false, &why)) {
        failed = true;
        g_respec_off.store(true);
        respec_stand_down("the client's respec checks from linked sources", why);
        return;
    }
    auto install = [&](kiln::MidHook2& hook, std::atomic<bool>& on, uintptr_t at, void (*fn)(KilnRegisters&), const char* label) {
        if (on.load()) return true;
        if (!hook.install(g_api, g_owner, at, fn, label)) {
            if (!g_respec_off.exchange(true))
                LOGW("respec: %s could not be hooked (%s): the skill respec from linked sources stands down", label,
                     hook.error() ? hook.error() : "refused");
            lower("crafting.respec_sources", KILN_CAP_REASON_FAULTED, "a respec hook could not be installed (see kiln.log)");
            return false;
        }
        on.store(true);
        return true;
    };
    if (!install(h_respec_node, g_respec_node_on, g_respec.node_ret, &hk_respec_node, "respec/client.NodeCountRet") ||
        !install(h_respec_altar, g_respec_altar_on, g_respec.altar_ret, &hk_respec_altar, "respec/client.AltarCountRet")) {
        failed = true;
        return;
    }
    bool row = false;
    if (g_respec_row_feat && !g_respec_row_off.load()) {
        if (!lock_dep_ok(g_respec.header_ret - 0x3d, 0x3d + 7, "the respec tree's item row count", &why) ||
            !h_respec_row.install(g_api, g_owner, g_respec.header_ret, &hk_respec_row, "respec/client.HeaderCountRet")) {
            g_respec_row_off.store(true);
            LOGW("respec: the respec tree's item row keeps the game's count (its code or hook did not check out); the unlearn and "
                 "reskill checks still count linked sources");
        } else {
            g_respec_row_on.store(true);
            row = true;
        }
    }
    LOGI("respec: the server's respec snapshot is on: this client's unlearn and reskill checks count the game's count plus linked "
         "sources%s", row ? " (and the respec tree's item row)" : "");
}

// The server world's Pre (tick thread): the cache, and the server's hooks once a link with Locks exists.
void lock_server_pre(uint64_t step) {
    lock_fault_lower();
    if (!g_lock_feat || !G.registered_ok || g_lock_off.load()) return;
    if (!g_lock_paid_on.load() && !g_registered.any_locks()) return;
    lock_cache_refresh(step);
    if (g_lock_paid_on.load() && g_lock_eval_on.load()) return;
    std::lock_guard install(g_lock_install_mu);
    if (g_lock_off.load() || (g_lock_paid_on.load() && g_lock_eval_on.load())) return;
    static bool failed = false;
    if (failed) return;
    LockCodeRefusal why;
    if (!lock_code_ok(false, true, &why)) {
        failed = true;
        g_lock_off.store(true);
        lock_stand_down("the server's lock check and payment from linked sources", why);
        return;
    }
    if (!lock_install(h_lock_eval, g_lock_eval_on, g_lock.count_ret, &hk_lock_eval, "locks/LockCountRet") ||
        !lock_install(h_lock_paid, g_lock_paid_on, g_lock.paid, &hk_lock_paid, "locks/LockPaid")) {
        failed = true;
        return;
    }
    LOGI("locks: a link opted in with Locks: the server's lock check and lock payment count linked sources (bag first)");
}

// The client world's Pre (a client process): the client's hooks once a Kiln server's lock snapshot is on.
void lock_client_pre() {
    lock_fault_lower();
    if (!g_lock_feat || g_lock_off.load() || !g_lock_on.load(std::memory_order_acquire)) return;
    // the requirement line is settled when its hook is in or it stays the game's (lock_row_install)
    const auto done = [] {
        return g_lock_eval_on.load() && g_lock_ui_on.load() && (g_lock_row_on.load() || !g_lock_row_feat || g_lock_row_off.load());
    };
    if (done()) return;
    std::lock_guard install(g_lock_install_mu);
    if (g_lock_off.load() || done()) return;
    static bool failed = false;
    if (failed) return;
    LockCodeRefusal why;
    if (!lock_code_ok(true, false, &why)) {
        failed = true;
        g_lock_off.store(true);
        lock_stand_down("the client's lock check and locked prompt from linked sources", why);
        return;
    }
    if (!lock_install(h_lock_eval, g_lock_eval_on, g_lock.count_ret, &hk_lock_eval, "locks/LockCountRet") ||
        !lock_install(h_lock_ui, g_lock_ui_on, g_lock.ui_ret, &hk_lock_ui, "locks/client.UiLockCountRet")) {
        failed = true;
        return;
    }
    const bool row = lock_row_install();
    LOGI("locks: the server's lock snapshot is on: this client's lock checks count the bag plus linked sources (the press, the "
         "locked prompt%s)", row ? " and its requirement line" : "; the requirement line keeps the game's count");
}

// Tick thread, every Pre: what the payment reads off the tick thread, the rows' check, the craft hook's demand.
void craft_pre(uint64_t step) {
    if (!G.craft_ok) return;
    g_srv_bases.store(singleton("PlayerBasesSystemServer"), std::memory_order_release);
    g_srv_recipes.store(recipes_registry(), std::memory_order_release);
    lock_server_pre(step);
    build_server_pre(step);  // GitHub #132
    respec_server_pre();     // GitHub #150
    static bool checked = false;
    if (!checked) {
        checked = true;
        std::string why;
        uint32_t reason = 0;
        if (!rules_hook(KILN_RULES_HOOK_PAYMENT, &why, &reason)) {
            LOGW("stations: station outputs in a craft are off: %s", why.c_str());
            lower("stations.craft_sources", reason, why.c_str());
            lower("crafting.sources", reason, why.c_str());
            if (g_lock_feat) lower("items.lock_sources", reason, why.c_str());  // the links locks use are crafting.sources'
            if (g_build_feat) {  // GitHub #132: a placement pays through the same hook; the cursor must not count links then
                g_build_off.store(true);
                lower("crafting.build_sources", reason, why.c_str());
            }
            if (g_respec_feat) {  // GitHub #150: the respec payer's payment goes through the same hook
                g_respec_off.store(true);
                lower("crafting.respec_sources", reason, why.c_str());
            }
            G.craft_ok = G.registered_ok = false;
            return;
        }
    }
    // The station being crafted at is never a source: kiln.gameplay's CraftRecipe hook names it (the craft context).
    // That hook group is installed on demand: ask for it while a station is opted in.
    static bool asked = false;
    if (!asked && g_gameplay && g_gameplay->header.size >= offsetof(KilnGameplayApi, request_hooks) + sizeof(void*) &&
        g_gameplay->request_hooks) {
        bool any = false;
        {
            std::lock_guard lock(g_mu);
            any = !g_sources.empty();
        }
        if (any || !g_registered.empty()) {
            asked = true;
            const KilnResult rc = g_gameplay->request_hooks(g_owner, KILN_GAMEPLAY_HOOK_CRAFT);
            LOGI("stations: a station is opted in: kiln.gameplay's craft hook asked for (the station crafted at is never a "
                 "source; %d)", static_cast<int>(rc));
        }
    }
}

// The client world's Pre (a client process): the recipe registry the crafting menu's count reads, and the client half's
// row once kiln.rules armed its hooks.
void KILN_CALL client_pre(const KilnTickInfo*, void*) {
    const uintptr_t w = g_api->world(KILN_WORLD_CLIENT);
    static uintptr_t seen = 0;
    static uint32_t frames = 0;
    if (w != seen || !g_client_recipes.load(std::memory_order_relaxed) || (++frames & 255) == 0) {
        if (w != seen) {
            seen = w;
            {
                std::lock_guard lock(g_craft_mu);  // a new world: the last one's snapshot names nothing
                g_craft_have.clear();
                g_craft_on.store(false, std::memory_order_release);
            }
            fill_clear();
        }
        g_client_recipes.store(w ? reinterpret_cast<void*>(g_api->singleton(w, str("g38_items::RecipeRegistry"))) : nullptr,
                               std::memory_order_release);
    }
    preview_hook_on_demand();
    fill_hook_on_demand();
    avail_hook_on_demand();
    lock_client_pre();
    build_client_pre();  // GitHub #132
    respec_client_pre();  // GitHub #150
    static bool checked = false;
    if (!checked && w && feat("stations.craft_ui")) {
        checked = true;
        std::string why;
        uint32_t reason = 0;
        if (!rules_hook(KILN_RULES_HOOK_PAYMENT, &why, &reason)) {
            LOGW("stations: the linked crafting preview is unavailable here: %s", why.c_str());
            lower("stations.craft_ui", reason, why.c_str());
            lower("crafting.source_ui", reason, why.c_str());
        } else if (!G.resolve || !G.recipe_find) {
            LOGW("stations: the linked crafting preview is unavailable here: the recipe reads did not resolve");
            lower("stations.craft_ui", KILN_CAP_REASON_SYMBOL_MISSING, "stations/ResolveRecipeInputs or content/RecipeFind did not resolve");
            lower("crafting.source_ui", KILN_CAP_REASON_SYMBOL_MISSING, "stations/ResolveRecipeInputs or content/RecipeFind did not resolve");
        } else {
            LOGI("stations: linked crafting uses the native client dry-run status and per-ingredient server stock (client world %s)",
                 g_client_recipes.load() ? "recipes read" : "recipes not read yet");
        }
    }
}
void KILN_CALL client_world(const KilnWorldEvent* ev, void*) {
    if (ev->kind != KILN_WORLD_CLIENT || ev->event != KILN_WORLD_STOPPING) return;
    t_set_lock_stock(0, nullptr, 0);  // the lock snapshot named this session's lock items
    g_lock_prompt_last.store(0, std::memory_order_relaxed);  // the next world's requirement line logs its state again
    t_set_build_stock(0, nullptr, 0);  // and the building snapshot this session's materials (GitHub #132)
    t_set_respec_stock(0, nullptr, 0);  // and the respec snapshot (GitHub #150)
    g_client_recipes.store(nullptr, std::memory_order_release);
    // A stand-down is checked again in the next world (GitHub #132: it was decided once per session); hooks already in stay.
    if (!g_preview_hooked.load()) g_preview_failed.store(false);
    {
        std::lock_guard lock(g_craft_mu);
        g_craft_have.clear();
        g_craft_on.store(false, std::memory_order_release);
    }
    fill_clear();
}

}  // namespace

bool stations_base_stock(std::vector<KilnEntity>* out) { return base_stock_all(out); }
bool stations_is_factory(KilnEntity e) { return is_factory(e); }

// kiln.stations.client (client_stations.cpp, 0.2.0-dev.31.13): a recipe of `registry` (the client world's
// g38_items::RecipeRegistry), read through the game's RecipeFind with the layout kiln.stations reads every recipe with.
// Read-only; on that world's thread.
bool stations_recipe_facts(void* registry, uint32_t id, int64_t duration_at, int64_t* duration_ns,
                           std::vector<uint32_t>* outputs, std::vector<uint32_t>* inputs) {
    outputs->clear();
    inputs->clear();
    const auto* recipe = static_cast<const uint8_t*>(find_recipe(id, registry));
    if (!recipe || duration_at <= 0) return false;
    KilnStationRecipe r{};
    r.size = sizeof r;
    RecipeCall c{id, registry, &r, false};
    unsigned long code = 0;
    struct DurationRead {
        const uint8_t* at;
        int64_t* out;
        static void run(void* p) {
            auto* d = static_cast<DurationRead*>(p);
            std::memcpy(d->out, d->at, sizeof *d->out);
        }
    } dur{recipe + duration_at, duration_ns};
    if (!guard(&recipe_impl, &c, &code) || !c.found || !guard(&DurationRead::run, &dur, &code)) return false;
    for (uint32_t i = 0; i < r.output_count; ++i) outputs->push_back(r.outputs[i].item);
    for (uint32_t i = 0; i < r.input_count; ++i) {
        if (r.inputs[i].item) inputs->push_back(r.inputs[i].item);
        if (!r.inputs[i].category || G.cat_bits <= 0) continue;
        KilnItemId items[64] = {};
        float scaling[64] = {};
        CatCall k{registry, r.inputs[i].category, 0, false, 0, items, scaling, 64, 0, false};
        if (guard(&cat_impl, &k, &code) && k.found)
            for (uint32_t j = 0; j < std::min<uint32_t>(k.n, 64); ++j) inputs->push_back(items[j]);
    }
    return true;
}

void stations_resolve() {
    g_api = items_api();
    g_owner = items_owner();
    H_FACTORY = comp_hash("FactoryStation");
    H_STOCK = comp_hash("InventoryCraftingStock");
    sym("GetTemplateComponent", &G.get_template);
    H_SETUP = comp_hash("InventorySetup");
    H_CRAFTING = comp_hash("Crafting");
    H_BASE_ID = comp_hash("BaseIdComponent");
    H_TRANSFORM = comp_hash("CurrentTransform");
    H_NETWORK_FUEL = comp_hash("NetworkFuel");
    int64_t fs_size = 0, fs_state = -1, fs_running = -1, fs_running_sel = -1, fs_pause = -1, fs_missing = -1;
    const bool layout = cst("stations/factory.size", &fs_size) && cst("stations/factory.state", &fs_state) &&
                        cst("stations/factory.running_recipe", &fs_running) && cst("stations/factory.running_selections", &fs_running_sel) &&
                        cst("stations/factory.pause_duration", &fs_pause) && cst("stations/factory.missing", &fs_missing) &&
                        cst("stations/factory.recipe_start", &G.fs_start) && fs_size == 0x68 && fs_state == kFsState;
    G.fs_size = fs_size;
    G.fs_running = fs_running;
    G.fs_running_sel = fs_running_sel;
    G.fs_pause = fs_pause;
    G.fs_missing = fs_missing;
    G.read = feat("stations.read") && layout && E.read_ok && sym("stations/BaseAtPosition", &G.base_at) &&
             sym("stations/ResolveRecipeInputs", &G.resolve) && sym("content/RecipeFind", &G.recipe_find) &&
             sym("stations/FactorySpeed", &G.speed) && sym("gameplay/ProgramTime", &G.program_time) &&
             cst("stations/bases.count", &G.bases_count) && cst("stations/bases.stride", &G.stride) &&
             cst("stations/bases.aabb_y", &G.aabb_y) && cst("stations/bases.zone_mask_rel", &G.zone_mask_rel) &&
             cst("stations/bases.zones", &G.zones) && cst("stations/stock.items", &G.stock_items) &&
             cst("stations/stock.items_count", &G.stock_items_count) && cst("stations/stock.water", &G.stock_water) &&
             cst("stations/stock.water_count", &G.stock_water_count) && cst("stations/categories.bits", &G.cat_bits) &&
             cst("stations/categories.cap", &G.cat_cap) && cst("stations/categories.keys", &G.cat_keys) &&
             cst("stations/categories.values", &G.cat_values) && cst("stations/categories.count", &G.cat_count) &&
             cst("stations/recipe.duration", &G.recipe_duration) && cst("stations/recipe.water_charges", &G.recipe_water) &&
             cst("stations/factory.next_recipe", &G.fs_next) && cst("stations/factory.next_selections", &G.fs_next_sel) &&
             cst("stations/factory.stopped_by_player", &G.fs_stopped) && cst("stations/factory.use_all", &G.fs_use_all) &&
             G.stride == 0x130 && G.stock_items_count == G.stock_items + 8 && G.stock_water_count == G.stock_water + 8 &&
             G.cat_cap == G.cat_bits + 8 && G.fs_next + 4 == G.fs_next_sel && G.fs_stopped == G.fs_use_all + 1 &&
             G.fs_stopped < G.fs_size && G.fs_missing < G.fs_size;
    // A joined client has no server world, so stations.read stays off, but its crafting menu still resolves recipes.
    // The count hook needs these two even then (docs/re/stations.md §10.2). Missing symbols leave the game's count.
    if (!G.resolve) sym("stations/ResolveRecipeInputs", &G.resolve);
    if (!G.recipe_find) sym("content/RecipeFind", &G.recipe_find);
    // The station screen's fill buttons (GitHub #68, a Kiln client; docs/re/stations.md §10.3). Hooked on demand.
    if (g_api->side() == KILN_SIDE_CLIENT && feat("stations.fill_ui")) {
        g_fill_feat = sym("stations/client.UiActionStatus", &g_ui_status) && g_ui_status;
        if (!g_fill_feat)
            lower("stations.fill_ui", KILN_CAP_REASON_SYMBOL_MISSING, "stations/client.UiActionStatus did not resolve");
    }
    // Exact client-only type-1 dry run, with its unconditional rollback. A joined client need not have a server world.
    if (g_api->side() == KILN_SIDE_CLIENT && feat("stations.craft_ui")) {
        uintptr_t alt_read = 0;
        g_preview_feat = sym("items/RecipeApply", &g_preview_apply) && sym("stations/client.CraftPreviewReturn", &g_preview_ret) &&
            sym("rules/InventoryPay", &g_preview_pay) && sym("rules/RecipePaid", &g_preview_paid) && sym("gems/PayMaterial", &g_preview_take) &&
            sym("items/InventoryRemoveItems", &g_preview_remove) && sym("items/InventoryPageFetch", &g_preview_fetch) &&
            sym("stations/MaterialPageFetchRet", &g_preview_fetch_ret) && sym("stations/MaterialPreferredPageFetchRet", &g_preview_preferred_ret) &&
            sym("stations/BaseStockForPlayer", &g_preview_stock) && sym("items/InventoryRollback", &g_preview_rollback) &&
            call_matches(g_preview_fetch_ret, g_preview_fetch) && call_matches(g_preview_preferred_ret, g_preview_fetch) &&
            call_matches(g_preview_ret, g_preview_apply) && call_matches(g_preview_paid, g_preview_pay) &&
            sym("rules/RecipePayAlt", &g_preview_alt) && sym("stations/client.CraftAltTakeRet", &g_preview_alt_take_ret) &&
            sym("stations/client.CraftAltStockTakeRet", &g_preview_alt_stock_take_ret) &&
            sym("stations/client.CraftStockTable", &g_preview_alt_table) && sym("stations/client.CraftAltStockRead", &alt_read) &&
            call_matches(alt_read, g_preview_alt_table) &&
            call_matches(g_preview_paid + 7, g_preview_alt) && call_matches(g_preview_alt_take_ret, g_preview_take) &&
            call_matches(g_preview_alt_stock_take_ret, g_preview_take) && G.recipe_find && E.tx_ok;
        if (g_preview_feat) g_preview_alt_ret = g_preview_paid + 7;
        if (!g_preview_feat) LOGW("stations: linked craft preview unavailable: the scoped dry run did not resolve; native status kept");
    }
    // The ingredient rows' "available" count (the same snapshot). Hooked on demand.
    if (g_api->side() == KILN_SIDE_CLIENT && feat("stations.stock_ui")) avail_resolve();
    lock_resolve();  // GitHub #147, after G.pay may have resolved
    build_resolve();  // GitHub #132
    respec_resolve();  // GitHub #150
    G.fill_ok = G.read && feat("stations.fill") && E.tx_ok && sym("stations/AutoTransferRecipe", &G.fill) &&
                sym("stations/FactoryInfoInvalidate", &G.invalidate) && E.setup_operator > 0 &&
                E.setup_operator + 4 <= static_cast<int64_t>(kSetupBlock);
    G.control = G.read && feat("stations.control") && (G.invalidate || sym("stations/FactoryInfoInvalidate", &G.invalidate));
    // GitHub #132 (0.2.0-dev.31.16): the per-ingredient transfer's own function, pinned by sigdb at the press's call
    // (@stations/AutoTransferAction.item: tx, list A, item, station, u16 amount, player)
    G.fill_items_ok = G.fill_ok && feat("stations.fill_items") && sym("stations/AutoTransferItem", &G.fill_item);
    if (!G.fill_items_ok && G.fill_ok && feat("stations.fill_items"))
        lower("stations.fill_items", KILN_CAP_REASON_SYMBOL_MISSING, "the game's item transfer did not resolve on this build");
    {  // the per-item take of AutoTransferRecipe and its 24-byte input stacks (item +0x10, count +0x14), checked on this build
        int64_t stack_item = 0, stack_size = 0;
        G.outputs_ok = G.fill_ok && feat("stations.output_sources") && cst("stations/stack.item", &stack_item) &&
                       cst("stations/stack.size", &stack_size) && stack_item == 0x10 && stack_size == 24;
    }
    uintptr_t pay = 0, paid = 0;
    G.craft_ok = G.outputs_ok && feat("stations.craft_sources") && sym("gems/PayMaterial", &G.pay) &&
                 sym("stations/BaseStockForPlayer", &G.base_stock) && sym("rules/InventoryPay", &pay) &&
                 sym("rules/RecipePaid", &paid) && call_matches(paid, pay) && E.get_component && E.job_storage &&
                 E.component_map_find;
    if (G.craft_ok) G.recipe_paid = paid;
    else if (feat("stations.craft_sources"))
        lower("stations.craft_sources", KILN_CAP_REASON_SYMBOL_MISSING,
              "the recipe payment or its per-item take did not resolve on this build");
    int64_t infos = 0, stride = 0, perms = 0, handle = 0;
    G.registered_ok = G.craft_ok && feat("crafting.sources") && cst("crafting/bases.players",&infos) && infos == 0x10 &&
        cst("crafting/player.stride",&stride) && stride == 0xa0 && cst("crafting/player.permissions",&perms) && perms == 0x9d && cst("players/info.handle",&handle) && handle == 4;
    if (!G.registered_ok && feat("crafting.sources"))
        lower("crafting.sources",KILN_CAP_REASON_SYMBOL_MISSING,"the server recipe payment, inventory access or player permission layout is unavailable");
    if (!G.registered_ok && g_lock_feat && g_api->side() == KILN_SIDE_SERVER)
        lower("items.lock_sources",KILN_CAP_REASON_SYMBOL_MISSING,"crafting.sources (the links a lock uses) is unavailable on this build");
    if (!G.registered_ok && g_respec_feat && g_api->side() == KILN_SIDE_SERVER)  // GitHub #150: the same links pay a respec
        lower("crafting.respec_sources",KILN_CAP_REASON_SYMBOL_MISSING,"crafting.sources (the links a respec uses) is unavailable on this build");
    // the recipe map, for the items a Kiln client is sent (craft_items); absolute offsets in the RecipeRegistry
    if (!(cst("content/recipes.cap", &g_rmap_cap) && cst("content/recipes.bits", &g_rmap_bits) &&
          cst("content/recipes.values", &g_rmap_values) && cst("content/recipes.count", &g_rmap_count)))
        g_rmap_cap = g_rmap_bits = g_rmap_values = g_rmap_count = -1;
    {
        char lab[8] = {};
        g_craft_lab = GetEnvironmentVariableA("KILN_STATIONS_CRAFT_LAB", lab, sizeof lab) > 0 && lab[0] == '1';
        if (g_craft_lab)
            LOGW("stations: KILN_STATIONS_CRAFT_LAB is set: a spawned player character's craft counts the base's Magic "
                 "chests without the game's inventory right%s (labs only). Never set this on a real server",
                 g_api->side() == KILN_SIDE_SERVER ? ", and it may have respec links of its own (GitHub #150)" : "");
    }
    G.events = G.read && feat("stations.events");
    if (!G.read) {
        const char* why = !feat("stations.read") ? "stations.read did not resolve on this game build"
                                                 : "a symbol or layout constant of kiln.stations does not check out";
        set_detail(why);
        if (feat("stations.read")) lower("stations.read", KILN_CAP_REASON_SYMBOL_MISSING, why);
    }
    LOGI("stations: reads %s, fill %s, control %s, events %s, output sources %s, craft sources %s, item fills %s (need kiln.world for the program)",
         G.read ? "on" : "OFF", G.fill_ok ? "on" : "OFF", G.control ? "on" : "OFF", G.events ? "on" : "OFF",
         G.outputs_ok ? "on" : "OFF", G.craft_ok ? "on" : "OFF", G.fill_items_ok ? "on" : "OFF");
}

void stations_register(const KilnWorldApi* w) {
    g_gameplay = static_cast<const KilnGameplayApi*>(items_api()->service(str(KILN_GAMEPLAY_SERVICE), 1));
    g_know = static_cast<const KilnKnowledgeApi*>(items_api()->service(str(KILN_KNOWLEDGE_SERVICE), 1));
    if (g_know && g_know->header.size < offsetof(KilnKnowledgeApi, query) + sizeof(void*)) g_know = nullptr;
    g_players = static_cast<const KilnPlayersApi*>(items_api()->service(str(KILN_PLAYERS_SERVICE), 1));
    if (g_players && g_players->header.size < sizeof(KilnPlayersApi)) g_players = nullptr;
    if (g_api->side() == KILN_SIDE_CLIENT) {  // the crafting menu's count (GitHub #68): the client world's recipes
        KilnHandle h = 0;
        if (g_api->subscribe_world_tick(items_owner(), KILN_WORLD_CLIENT, KILN_TICK_PRE, &client_pre, nullptr, &h) != KILN_OK ||
            g_api->subscribe_world(items_owner(), &client_world, nullptr, &h) != KILN_OK)
            lower("stations.craft_ui", KILN_CAP_REASON_FAULTED, "no tick of the client world for the crafting menu's count");
    }
    if (!G.read) return;
    if (!w || w->header.size < sizeof(KilnWorldApi)) {
        lower("stations.read", KILN_CAP_REASON_SERVICE_MISSING, "kiln.stations needs kiln.world (entity reads and its program)");
        G.read = G.fill_ok = G.control = G.events = G.fill_items_ok = false;
        return;
    }
    g_w = w;
    if (!G.fill_ok && !G.control) return;
    g_tx = static_cast<uint8_t*>(VirtualAlloc(nullptr, static_cast<SIZE_T>(E.tx_size), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
    if (!g_tx) {
        set_detail("no memory for the stations' transaction");
        G.fill_ok = G.control = G.fill_items_ok = false;
        return;
    }
    std::vector<KilnStr> sr, sw, orr, ow, eo;
    for (const char* n : kSingRead) sr.push_back(str(n));
    for (const char* n : kSingWrite) sw.push_back(str(n));
    for (const char* n : kOtherRead) orr.push_back(str(n));
    orr.push_back(str("DynamicInventoryCraftingStock"));
    for (int i = 0; i < A_COUNT; ++i) ow.push_back(str(kAccNames[i]));
    ow.push_back(str("FactoryStation"));
    ow.push_back(str("NetworkFuel"));
    for (const char* n : kEvents) eo.push_back(str(n));
    KilnProgramDesc d{};
    d.size = sizeof d;
    d.flags = KILN_PROGRAM_SYSTEM | KILN_PROGRAM_HOST_ONLY;
    d.name = str(kProgram);
    d.each = &each;
    d.singletons_read = KilnNames{sr.data(), static_cast<uint32_t>(sr.size()), 0};
    d.singletons_write = KilnNames{sw.data(), static_cast<uint32_t>(sw.size()), 0};
    d.singletons_write_optional = 1;  // KnowledgeTriggerQueue (a player's fill only)
    d.other_read = KilnNames{orr.data(), static_cast<uint32_t>(orr.size()), 0};
    d.other_write = KilnNames{ow.data(), static_cast<uint32_t>(ow.size()), 0};
    d.events_out = KilnNames{eo.data(), static_cast<uint32_t>(eo.size()), 0};
    if (w->register_program(items_owner(), &d, &g_prog) != KILN_OK) {
        lower("stations.fill", KILN_CAP_REASON_FAULTED, "kiln.world refused the program kiln_items_stations (see kiln.log)");
        lower("stations.control", KILN_CAP_REASON_FAULTED, "kiln.world refused the program kiln_items_stations (see kiln.log)");
        G.fill_ok = G.control = G.fill_items_ok = false;
        return;
    }
    g_state.store(KILN_ITEMS_PROGRAM_PENDING);
    set_detail("waiting for the server world (kiln.world)");
    LOGI("program %s registered through kiln.world (station actions; knowledge %s, players %s, gameplay %s)", kProgram,
         g_know ? "here" : "not here", g_players ? "here" : "not here", g_gameplay ? "here" : "not here");
}

void stations_pre(uint64_t step) {
    if (!G.read) return;
    craft_pre(step);
    poll_program();
    // SELECT's unlock check (tick thread), then the program may take them
    std::vector<Req> incoming;
    {
        std::lock_guard lock(g_mu);
        incoming.swap(g_incoming);
    }
    std::vector<Req> ready;
    std::vector<KilnStationResult> refused;
    for (Req& q : incoming) {
        if (q.r.action == KILN_STATION_SELECT && q.r.recipe && !(q.r.flags & KILN_STATION_ALLOW_LOCKED)) {
            char why[128] = "";
            if (!unlocked(q.r.recipe, why, sizeof why)) {
                KilnStationResult res = result_base(q);
                finish_text(&res, KILN_STATION_LOCKED, why);
                res.step = step;
                refused.push_back(res);
                continue;
            }
        }
        ready.push_back(q);
    }
    {
        std::lock_guard lock(g_mu);
        for (auto& q : ready) g_ready.push_back(q);
        for (auto& r : refused) g_done.push_back(r);
        const int state = g_state.load();
        const bool off = g_off.load() || state == KILN_ITEMS_PROGRAM_NONE;
        for (auto i = g_ready.begin(); i != g_ready.end();) {
            const bool stale = step > i->submitted + kTimeoutSteps;
            if (off || stale) {
                KilnStationResult res = result_base(*i);
                finish(&res, off ? KILN_STATION_UNAVAILABLE : KILN_STATION_DROPPED,
                       off ? "kiln.stations' actions went off" : "kiln_items_stations did not run it within 10 s");
                res.step = step;
                g_done.push_back(res);
                i = g_ready.erase(i);
                continue;
            }
            ++i;
        }
        if (off) {  // the program no longer runs: the stops waiting for a cycle's end are answered too
            for (const Req& q : take_if(&g_after, [](const Req&) { return true; })) {
                KilnStationResult res = result_base(q);
                finish(&res, KILN_STATION_UNAVAILABLE, "kiln.stations' actions went off while the stop waited: nothing written");
                res.step = step;
                g_done.push_back(res);
            }
        }
        g_pending_n.store(static_cast<uint32_t>(g_ready.size() + g_after.size() + g_incoming.size()), std::memory_order_release);
    }
    deliver();
    if (G.events && items_server_world()) watch_pre(step);
}

void stations_world_closed() {
    {
        std::lock_guard lock(g_mu);
        for (auto* list : {&g_incoming, &g_ready, &g_after}) {
            for (const Req& q : *list) {
                KilnStationResult res = result_base(q);
                finish(&res, KILN_STATION_DROPPED, "the server world closed");
                g_done.push_back(res);
            }
            list->clear();
        }
        g_pending_n.store(0, std::memory_order_release);
        g_wrote.clear();
        g_ring.clear();
        g_sources.clear();  // entity ids of a closed world name nothing
    }
    g_registered.clear();
    {
        std::lock_guard lock(g_lock_mu);  // GitHub #147: the lock checks' counts named this world's entities
        g_lock_cache.clear();
    }
    {
        std::lock_guard lock(g_build_mu);  // GitHub #132: entity ids of a closed world name nothing
        g_build_cache.clear();
    }
    g_lock_dirty.store(true);
    g_build_dirty.store(true);
    g_srv_bases.store(nullptr, std::memory_order_release);
    g_srv_recipes.store(nullptr, std::memory_order_release);
    g_rindex = RecipeIndex{};
    {
        std::lock_guard lock(g_craft_mu);  // a hosting player's own snapshot came from this world
        g_craft_have.clear();
        g_craft_on.store(false, std::memory_order_release);
    }
    fill_clear();
    g_known.clear();
    g_stations.clear();
    g_altars.clear();
    g_scanned = false;
    g_scanned_any.store(false);
    g_station_count.store(0);
    g_stations_step = g_altars_step = 0;
    g_bases_sig = 0;
    g_now.store(0);
    int expected = KILN_ITEMS_PROGRAM_RUNNING;
    g_state.compare_exchange_strong(expected, KILN_ITEMS_PROGRAM_ACTIVE);
}

// kiln.gameplay's craft hook, at the end of a craft (this thread): one bounded line for a player's craft while some crafting
// source could pay it, saying how far its payment got (GitHub #132 follow-up). A craft that never reached the hook (the
// client never sent it, or the server dropped the event before CraftRecipe) leaves no line.
void craft_done_impl(uint32_t op, uint32_t station, uint32_t recipe, int32_t rc) {
    const CraftTrace t = t_trace;
    t_trace = CraftTrace{};
    if (!t.active || !G.craft_ok) return;
    bool sources = false;
    {
        std::lock_guard lock(g_mu);
        sources = !g_sources.empty();
    }
    if (!sources && G.registered_ok) sources = !g_registered.copy(op).empty();
    if (!craft_trace_wanted(op, station, sources)) return;
    if (const uint32_t n = g_log_craft.take())
        LOGI("stations: %s%s", craft_trace_text(t, op, recipe, station, rc).c_str(), n > 1 ? " (more held back)" : "");
}

std::vector<CraftSource> craft_sources_copy(uint32_t player) {
    uint32_t session=0;
    if(!source_player(g_srv_bases.load(std::memory_order_acquire),player,&session))return {};
    auto sources=g_registered.copy(player);
    sources.erase(std::remove_if(sources.begin(),sources.end(),[&](const CraftSource& source) {return source.player_session!=session||storage_contains(source.container);}),sources.end());
    return sources;
}
bool craft_source_access(void* bases, uint32_t player, uint32_t container) { return !storage_contains(container)&&source_access(bases,player,container); }
bool craft_source_access(uint32_t player, uint32_t container) { return !storage_contains(container)&&source_access(g_srv_bases.load(std::memory_order_acquire),player,container); }
void craft_sources_cancel_owner(KilnOwner owner) { g_registered.cancel_owner(owner); }

KilnResult stations_publish(const KilnApi* api, KilnOwner owner) {
    return api->register_service(owner, str(KILN_STATIONS_SERVICE), &kTable);
}

}  // namespace kitems

// Labs only (GitHub #150): a respec link of a spawned player character, as Crafting.AddSource's Respec gives a player.
// Answers 0 unless this is a dedicated server started with KILN_STATIONS_CRAFT_LAB=1 (never a client's process, so never
// a hosted world), the character's id is outside the player range and the container is an entity, and while fewer than
// 16 lab links exist (one already there answers 1 again); container 0 removes the character's lab links. Each new link is
// a kiln.log warning. Not an ABI (as kiln_stations_craft_stats).
extern "C" KILN_EXPORT int kiln_stations_lab_respec_link(uint32_t character, uint32_t container) {
    if (!kitems::respec_lab_on() || character <= kitems::kMaxPlayerEntity || (container && container <= kitems::kMaxPlayerEntity)) return 0;
    {
        std::lock_guard lock(kitems::g_respec_lab_mu);
        auto& v = kitems::g_respec_lab;
        if (!container) {
            v.erase(std::remove_if(v.begin(), v.end(), [&](const auto& l) { return l.first == character; }), v.end());
            return 1;
        }
        if (std::find(v.begin(), v.end(), std::make_pair(character, container)) != v.end()) return 1;
        if (v.size() >= kitems::kMaxRespecLab) return 0;
        v.emplace_back(character, container);
    }
    kitems::items_log(KILN_LOG_WARN, "respec: lab respec link: spawned character %u's skill respec may pay from container %u "
                      "(KILN_STATIONS_CRAFT_LAB, labs only)", character, container);
    return 1;
}

// kiln.rules calls this from its inventory-payment hook. 1: *code is the payment's result. 0: run the game's function.
extern "C" KILN_EXPORT int kiln_stations_craft_pay(void* tx, void* costs, uint32_t owner, uint32_t multiplier, void* extra,
                                                   uintptr_t ret, uint32_t* code) {
    return kitems::craft_pay_impl(tx, costs, owner, multiplier, extra, ret, code);
}

// kiln.rules' already installed InventoryPay hook, after the native material payment. Private, not a service ABI.
extern "C" KILN_EXPORT uint32_t kiln_stations_craft_preview(void* tx, void* costs, uint32_t owner, uint32_t amount,
                                                         void* extra, uintptr_t ret, uint32_t game) {
    return kitems::craft_preview_impl(tx, costs, owner, amount, extra, ret, game);
}

// kiln.gameplay's craft hook: the station being crafted at, for the duration of CraftRecipe (this thread).
extern "C" KILN_EXPORT void kiln_stations_set_craft_context(uint32_t station, int active) {
    kitems::t_craft_station = active ? station : 0;
    if (active) {
        kitems::t_trace = kitems::stations::CraftTrace{};
        kitems::t_trace.active = true;
    }
}

// kiln.gameplay's craft hook, after the game's CraftRecipe returned (this thread): the operator, the station, the recipe
// id and the game's code. Not an ABI (as kiln_stations_set_craft_context); an older kiln-gameplay.dll does not call it.
extern "C" KILN_EXPORT void kiln_stations_craft_done(uint32_t op, uint32_t station, uint32_t recipe, int32_t rc) {
    kitems::craft_done_impl(op, station, recipe, rc);
}

// kiln.rules' craft-count hook on a Kiln client (the game's UI thread). 1: *out is the snapshot's craft count; `game` is
// the game's own count (kiln.log names a count the snapshot raised).
extern "C" KILN_EXPORT int kiln_stations_craft_adjust(uint32_t recipe, uint32_t cap, uint32_t game, const void* selections,
                                                     uint32_t* out) {
    return kitems::craft_adjust_impl(recipe, cap, game, selections, out);
}

// The craft payment's counters (labs, diagnostics; not an ABI): calls, idle, other_world, mode0, no_access, considered,
// covered, short, paid, from_outputs, rollback, skipped, lab, respec (GitHub #150), in that order. Writes up to n; returns how many there are.
extern "C" KILN_EXPORT uint32_t kiln_stations_craft_stats(uint32_t* out, uint32_t n) {
    return kitems::craft_stats(out, n);
}
