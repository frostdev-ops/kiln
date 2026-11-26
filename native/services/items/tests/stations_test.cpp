// SPDX-License-Identifier: MIT
// kiln.stations host test: request checks, the fill's arithmetic against the game's own (AutoTransferRecipe,
// docs/re/stations.md §3.1), the base id rule, the stop-after-craft rule, the state diff, the event ring, the unlock
// verdict, and the ABI's sizes (the managed mirror in Kiln.Stations depends on them).
#include "../stations_logic.h"

#include <cstddef>
#include <cstdio>
#include <cstring>
#include <string>

using namespace kitems::stations;

namespace {
int checks = 0, failed = 0;
#define CHECK(x) do { ++checks; if (!(x)) { ++failed; std::printf("FAIL %d %s\n", __LINE__, #x); } } while (0)

KilnStationRequest req(uint32_t action) {
    KilnStationRequest r{};
    r.size = sizeof r;
    r.action = action;
    r.station = 1318;
    if (action == KILN_STATION_FILL) r.crafts = 5;
    return r;
}

KilnEntity* named_one() {
    static KilnEntity e[1] = {4000};
    return e;
}

// The game's AutoTransferItem (S 0x198bf0 / C 0x3f7950, docs/re/stations.md section 10.9) on plain numbers: the player's
// bag, then each list-A source in order, take what is still missing; short -> 4; then all of it into the station (no
// room -> 2). One call per layer, as Kiln's item fill makes them, each moving exactly what it is asked.
int game_item_fill(uint64_t* source, uint32_t amount, uint64_t room, uint64_t* station) {
    if (*source < amount) return 4;
    if (*station + amount > room) return 2;
    *source -= amount;
    *station += amount;
    return 0;
}

// The game's AutoTransferRecipe, as read from the code (docs/re/stations.md §3.1), on plain numbers: returns 0 and
// fills `moved` per input, or 4 when the stock is short, 2 when the station cannot hold it (water: its capacity). The
// water input (kWaterItem) counts in "loaded" as the game counts it. Used to check the planner never asks for what the
// game refuses.
int game_fill(const std::vector<InputNow>& in, uint32_t n, std::vector<uint32_t>* moved) {
    const uint32_t loaded = crafts_loaded(in);
    moved->assign(in.size(), 0);
    for (size_t k = 0; k < in.size(); ++k) {
        const uint32_t need = game_need(in[k], n, loaded);
        if (need > in[k].stock) return 4;
        if (in[k].room != UINT64_MAX && in[k].have + need > in[k].room) return 2;
        (*moved)[k] = need;
    }
    return 0;
}
}  // namespace

int main() {
    // ---- ABI sizes (the managed mirror) ----
    static_assert(sizeof(KilnBaseId) == 16 && sizeof(KilnStationChoice) == 8 && sizeof(KilnStationStack) == 8);
    static_assert(sizeof(KilnStationBase) == 432 && offsetof(KilnStationBase, altar_ids) == 92 && offsetof(KilnStationBase, min) == 368);
    static_assert(sizeof(KilnStation) == 200 && offsetof(KilnStation, position) == 32 && offsetof(KilnStation, state) == 56 &&
                  offsetof(KilnStation, crafts_loaded) == 132 && offsetof(KilnStation, recipe_start_ns) == 136 &&
                  offsetof(KilnStation, progress) == 168 && offsetof(KilnStation, flags) == 180);
    static_assert(sizeof(KilnStationInput) == 16 && sizeof(KilnStationRecipe) == 624 && offsetof(KilnStationRecipe, inputs) == 48);
    static_assert(sizeof(KilnStationRequest) == 104 && offsetof(KilnStationRequest, choices) == 24 &&
                  offsetof(KilnStationRequest, keep) == 64 && offsetof(KilnStationRequest, chest_count) == KILN_STATION_REQUEST_V1 &&
                  offsetof(KilnStationRequest, chests) == 96);
    static_assert(sizeof(KilnItemsMoveChange) == 32);
    // since 0.2.0-dev.31.12 (GitHub #132): from_linked appended to the result, linked[] to the plan
    static_assert(sizeof(KilnStationResult) == 1248 && offsetof(KilnStationResult, from_linked) == 1240 && offsetof(KilnStationResult, limited_by) == 52 &&
                  offsetof(KilnStationResult, short_item) == 56 && offsetof(KilnStationResult, ticket) == 64 &&
                  offsetof(KilnStationResult, changes) == 80 && offsetof(KilnStationResult, message) == 1104 &&
                  offsetof(KilnStationResult, from_chests) == 1232);
    static_assert(sizeof(KilnStationPlan) == 1080 && offsetof(KilnStationPlan, linked) == 952 && offsetof(KilnStationPlan, outputs) == 696 && offsetof(KilnStationPlan, chests) == 824 &&
                  offsetof(KilnStationPlan, short_item) == 36 && offsetof(KilnStationPlan, water_per_craft) == 44 &&
                  offsetof(KilnStationPlan, inputs) == 56);
    static_assert(sizeof(KilnStationEvent) == 112 && offsetof(KilnStationEvent, by) == 64 && offsetof(KilnStationEvent, position) == 72 &&
                  offsetof(KilnStationEvent, base) == 96);
    static_assert(sizeof(KilnStationsStatus) == 188);
    static_assert(sizeof(KilnStationsApi) == 8 + 40 * 8 && offsetof(KilnStationsApi, set_output_source) == 8 + 21 * 8 &&
                  offsetof(KilnStationsApi, max_chests) == 8 + 23 * 8 && offsetof(KilnStationsApi, collect) == 8 + 24 * 8 &&
                  offsetof(KilnStationsApi, base_stock_with_stations) == 8 + 25 * 8 &&
                  offsetof(KilnStationsApi, set_craft_stock) == 8 + 26 * 8 && offsetof(KilnStationsApi, craft_items) == 8 + 27 * 8 &&
                  offsetof(KilnStationsApi, set_fill_answers) == 8 + 28 * 8 && offsetof(KilnStationsApi, fill_asks) == 8 + 29 * 8);
    static_assert(sizeof(KilnCraftSourceRequest) == 40 && offsetof(KilnCraftSourceRequest, flags) == KILN_CRAFT_SOURCE_REQUEST_V1 &&
                  offsetof(KilnStationsApi, add_source) == 248 &&
                  offsetof(KilnStationsApi, remove_source) == 256 && offsetof(KilnStationsApi, cancel_sources) == 264 &&
                  offsetof(KilnStationsApi, source_stock) == 272 && offsetof(KilnStationsApi, set_dye_stock) == 280);
    // GitHub #147 (0.2.0-dev.31.12): the lock members after set_dye_stock; a 32-byte request is the older caller's
    static_assert(offsetof(KilnStationsApi, set_lock_stock) == 288 && offsetof(KilnStationsApi, lock_asks) == 296 &&
                  KILN_CRAFT_SOURCE_REQUEST_V1 == 32 && KILN_STATIONS_MAX_LOCK_STOCK == 256 && KILN_STATIONS_MAX_LOCK_ASKS == 16);
    static_assert(offsetof(KilnStationsApi, set_build_stock) == 304);  // after #147's members
    // GitHub #132 (0.2.0-dev.31.16): fill_flags after set_build_stock; the request's item where reserved[0] was
    static_assert(offsetof(KilnStationsApi, fill_flags) == 312);
    // GitHub #150 (0.2.0-dev.31.18): set_respec_stock after fill_flags; RESPEC the next link flag and stock flag
    static_assert(offsetof(KilnStationsApi, set_respec_stock) == 320 && sizeof(KilnStationsApi) == 328 &&
                  KILN_CRAFT_SOURCE_RESPEC == 8 && KILN_CRAFT_SOURCE_STOCK_RESPEC == 16 && KILN_STATIONS_MAX_RESPEC_STOCK == 16);
    static_assert(offsetof(KilnStationRequest, item) == 72 && offsetof(KilnStationRequest, reserved) == 76 && KILN_STATION_FILL_ITEM == 0x200);
    static_assert(KILN_CRAFT_SOURCE_FILLS == 2 && KILN_CRAFT_SOURCE_BUILDING == 4 && KILN_CRAFT_SOURCE_STOCK_FILLS == 4 &&
                  KILN_CRAFT_SOURCE_STOCK_BUILDING == 8 && KILN_STATION_FILL_LINKED == 0x100);
    static_assert(KILN_STATIONS_MAX_CRAFT_STOCK == 1024 && KILN_STATIONS_MAX_FILL_KEYS == 16);
    static_assert(sizeof(KilnStationCollect) == 24 && offsetof(KilnStationCollect, flags) == 16);
    // a collect's flags travel in KilnStationResult.flags: they share no bit with a request flag (DRY_RUN above all)
    static_assert(((KILN_STATION_COLLECT_OUTPUTS | KILN_STATION_COLLECT_LEFTOVERS | KILN_STATION_COLLECT_TO_BASE) &
                   (KILN_STATION_FILL_ADD | KILN_STATION_FILL_EXACT | KILN_STATION_DRY_RUN | KILN_STATION_NO_WATER |
                    KILN_STATION_AFTER_CRAFT | KILN_STATION_KEEP_STOPPED | KILN_STATION_ALLOW_LOCKED |
                    KILN_STATION_FILL_NO_OUTPUTS | KILN_STATION_FILL_LINKED | KILN_STATION_FILL_ITEM)) == 0);
    static_assert(KILN_STATION_COLLECT_OUTPUTS == 0x10000u && KILN_STATION_COLLECT_LEFTOVERS == 0x20000u &&
                  KILN_STATION_COLLECT_TO_BASE == 0x40000u);
    static_assert(offsetof(KilnStationResult, from_outputs) == 60);

    // ---- requests ----
    CHECK(request_valid(req(KILN_STATION_FILL)));
    CHECK(request_valid(req(KILN_STATION_STOP)));
    CHECK(request_valid(req(KILN_STATION_RESUME)));
    auto sel = req(KILN_STATION_SELECT);
    sel.recipe = 0x5568cc2b;
    CHECK(request_valid(sel));
    sel.recipe = 0;  // clears the selection
    CHECK(request_valid(sel));
    auto r = req(KILN_STATION_FILL);
    r.crafts = 0;
    CHECK(!request_valid(r));
    r.crafts = 1001;
    CHECK(!request_valid(r));
    r = req(KILN_STATION_FILL);
    r.flags = KILN_STATION_AFTER_CRAFT;  // a stop flag on a fill
    CHECK(!request_valid(r));
    r = req(KILN_STATION_FILL);
    r.keep_count = 2;  // no list
    CHECK(!request_valid(r));
    r = req(KILN_STATION_FILL);
    r.choices[0].category = 0xee5ae93f;  // choices without a recipe
    CHECK(!request_valid(r));
    r.recipe = 0x5568cc2b;
    CHECK(request_valid(r));
    r.choices[0].reserved[1] = 1;
    CHECK(!request_valid(r));
    r = req(KILN_STATION_STOP);
    r.flags = KILN_STATION_AFTER_CRAFT;
    CHECK(request_valid(r));
    r.crafts = 3;
    CHECK(!request_valid(r));
    r = req(KILN_STATION_RESUME);
    r.flags = KILN_STATION_ALLOW_LOCKED;
    CHECK(!request_valid(r));
    // GitHub #132 (0.2.0-dev.31.12): a fill from linked sources names their player; no other action takes the flag
    r = req(KILN_STATION_FILL);
    r.flags = KILN_STATION_FILL_LINKED;
    CHECK(!request_valid(r));
    r.player = 1;
    CHECK(request_valid(r));
    r.player = 1024;
    CHECK(!request_valid(r));
    for (uint32_t action : {KILN_STATION_STOP, KILN_STATION_RESUME, KILN_STATION_SELECT}) {
        r = req(action);
        r.flags = KILN_STATION_FILL_LINKED;
        CHECK(!request_valid(r));
    }
    // GitHub #132 (0.2.0-dev.31.16): the station screen's per-ingredient transfer: one item, an amount in items, no recipe
    r = req(KILN_STATION_FILL);
    r.flags = KILN_STATION_FILL_ITEM;
    r.item = 0x2b0cf1d5;
    r.player = 1;
    CHECK(request_valid(r));
    r.crafts = 9999;  // items, not crafts: more than a fill's 1000
    CHECK(request_valid(r));
    r.crafts = 0xFFFF;
    CHECK(request_valid(r));
    r.crafts = 0x10000;  // the game's amount is a u16
    CHECK(!request_valid(r));
    r.crafts = 0;
    CHECK(!request_valid(r));
    r.crafts = 5;
    r.flags = KILN_STATION_FILL_ITEM | KILN_STATION_FILL_LINKED | KILN_STATION_DRY_RUN | KILN_STATION_FILL_ADD |
              KILN_STATION_FILL_EXACT | KILN_STATION_NO_WATER | KILN_STATION_FILL_NO_OUTPUTS;
    CHECK(request_valid(r));
    r.player = 0;  // linked sources are a player's
    CHECK(!request_valid(r));
    r.flags = KILN_STATION_FILL_ITEM;  // Kiln's own item fill without a player: the Magic chests alone
    CHECK(request_valid(r));
    r.player = 2550;  // never another entity
    CHECK(!request_valid(r));
    r.player = 1;
    r.item = 0;  // no item
    CHECK(!request_valid(r));
    r.item = 0x2b0cf1d5;
    r.recipe = 0x5568cc2b;  // an item fill names no recipe
    CHECK(!request_valid(r));
    r.recipe = 0;
    r.choices[0].category = 0xee5ae93f;
    CHECK(!request_valid(r));
    r.choices[0].category = 0;
    KilnStationStack keep1{0x2b0cf1d5, 3};
    r.keep_count = 1;
    r.keep = &keep1;  // reserves are the recipe fill's
    CHECK(!request_valid(r));
    r.keep_count = 0;
    r.keep = nullptr;
    r.chest_count = 1;
    r.chests = named_one();
    CHECK(!request_valid(r));  // named chests too
    r.chest_count = 0;
    r.chests = nullptr;
    CHECK(request_valid(r));
    r.reserved[0] = 1;
    CHECK(!request_valid(r));
    r.reserved[0] = 0;
    r.flags = KILN_STATION_FILL_LINKED;  // an item named on a recipe fill: refused, never read as one
    CHECK(!request_valid(r));
    for (uint32_t action : {KILN_STATION_STOP, KILN_STATION_RESUME, KILN_STATION_SELECT}) {
        r = req(action);
        r.flags = KILN_STATION_FILL_ITEM;
        CHECK(!request_valid(r));
        r.flags = 0;
        r.item = 7;
        CHECK(!request_valid(r));
    }
    r = req(KILN_STATION_FILL);
    r.station = 0;
    CHECK(!request_valid(r));
    r = req(KILN_STATION_FILL);
    r.reserved[2] = 1;
    CHECK(!request_valid(r));
    r = req(7);
    CHECK(!request_valid(r));
    // FILL's player: a player entity (1..1023) or 0; never another entity (a chest's id would make the game take from it)
    r = req(KILN_STATION_FILL);
    r.player = 2550;
    CHECK(!request_valid(r));
    r.player = 1;
    CHECK(request_valid(r));
    r.player = 1023;
    CHECK(request_valid(r));
    // ordinary chests: a full request may name them; a request of the older size names none and is not read past
    KilnEntity named[2] = {4000, 4001};
    r = req(KILN_STATION_FILL);
    r.chest_count = 2;
    r.chests = named;
    CHECK(request_valid(r));
    r.chests = nullptr;
    CHECK(!request_valid(r));
    r.chests = named;
    named[0] = 3;  // a player
    CHECK(!request_valid(r));
    named[0] = r.station;
    CHECK(!request_valid(r));
    named[0] = 4000;
    r.chest_count = KILN_STATIONS_MAX_CHESTS + 1;
    CHECK(!request_valid(r));
    r.chest_count = 2;
    r.chest_reserved = 1;
    CHECK(!request_valid(r));
    r.chest_reserved = 0;
    r.size = 96;  // between the older request and this one
    CHECK(!request_valid(r));
    r.size = KILN_STATION_REQUEST_V1;
    CHECK(request_valid(r));  // the chest fields are not part of this request
    r = req(KILN_STATION_STOP);
    r.chest_count = 1;
    r.chests = named;
    CHECK(!request_valid(r));

    // ---- the fill's arithmetic: the charcoal kiln (12 of category wood, 3 of another per craft) ----
    std::vector<InputNow> in{{0x2b0cf1d5, 12, 30, 100}, {0x07e67b5b, 3, 4, 20}};
    CHECK(crafts_loaded(in) == 1);                   // 30/12 = 2, 4/3 = 1
    CHECK(game_need(in[0], 4, 1) == 5 * 12 - 30);     // (n + loaded) * count - have
    CHECK(game_need(in[1], 4, 1) == 5 * 3 - 4);
    CHECK(game_need(in[0], 0, 1) == 0);
    const uint32_t aff = crafts_affordable(in, 1);    // wood (30+100)/12 = 10 -> 9 more; other (4+20)/3 = 8 -> 7 more
    CHECK(aff == 7);
    std::vector<uint32_t> moved;
    CHECK(game_fill(in, aff, &moved) == 0);           // the game accepts what the planner offers
    CHECK(game_fill(in, aff + 1, &moved) == 4);       // and refuses one more
    CHECK(crafts_wanted(0, 5, 1) == 4);               // UpTo 5 with 1 loaded
    CHECK(crafts_wanted(0, 1, 3) == 0);               // already more than asked
    CHECK(crafts_wanted(KILN_STATION_FILL_ADD, 5, 1) == 5);
    CHECK(crafts_first_try(0, 20, 7) == 7);
    CHECK(crafts_first_try(KILN_STATION_FILL_EXACT, 20, 7) == 0);   // EXACT and the stock (after reserves) short: no call
    CHECK(crafts_first_try(KILN_STATION_FILL_EXACT, 7, 7) == 7);
    CHECK(crafts_next_try(0, 7, 1) == 3);
    CHECK(crafts_next_try(0, 1, 1) == 0);
    CHECK(crafts_next_try(KILN_STATION_FILL_EXACT, 7, 1) == 0);
    CHECK(crafts_next_try(0, 64, kMaxTries) == 0);
    // reserves
    CHECK(after_keep(100, 40) == 60);
    CHECK(after_keep(10, 40) == 0);
    std::vector<InputNow> kept{{1, 12, 30, after_keep(100, 90)}, {2, 3, 4, 20}};  // 10 wood left to take
    CHECK(crafts_affordable(kept, crafts_loaded(kept)) == 2);  // (30 + 10) / 12 = 3 -> 2 more
    // an input with count 0 (the data has none; the game moves nothing for it and loaded is 0)
    std::vector<InputNow> zero{{1, 0, 5, 5}, {2, 3, 0, 9}};
    CHECK(crafts_loaded(zero) == 0);
    CHECK(crafts_affordable(zero, 0) == 3);
    // the game's u16 clamp: never plan more than 0xFFFF of one input per call
    std::vector<InputNow> big{{1, 1, 0, 1000000}};
    CHECK(crafts_affordable(big, 0) == kGameMaxCount);
    CHECK(game_need(big[0], 70000, 0) == kGameMaxCount);
    CHECK(crafts_loaded({}) == 0 && crafts_affordable({}, 0) == 0);

    // ---- stop after the running craft ----
    CHECK(after_craft_due(KILN_STATION_STATE_NO_RECIPE, 0, 5, 5));
    CHECK(!after_craft_due(KILN_STATION_STATE_RUNNING, 0x5568cc2b, 5, 5));   // the same cycle runs
    CHECK(after_craft_due(KILN_STATION_STATE_RUNNING, 0x5568cc2b, 5, 9));    // a new cycle started: the old one ended
    CHECK(!after_craft_due(KILN_STATION_STATE_OUTPUT_FULL, 0x5568cc2b, 5, 5)); // outputs waiting: a stop now refunds the inputs
    CHECK(!after_craft_due(KILN_STATION_STATE_PAUSED, 0x5568cc2b, 5, 5));      // paused mid-cycle: a stop now loses it
    CHECK(after_craft_due(KILN_STATION_STATE_MISSING_MATERIALS, 0x5568cc2b, 5, 5));

    // ---- water counts in "loaded" (the game's count), unless the request pours none ----
    {
        std::vector<InputNow> w{{0x2b0cf1d5, 12, 60, 100}};  // items for 5 crafts in the station
        InputNow water{};
        CHECK(water_input(1, false, 0, 256, 1000, &water) && water.item == kWaterItem && water.count == 64);
        w.push_back(water);
        CHECK(crafts_loaded(w) == 0);  // no water: no complete craft
        PlanMath m = plan_math(0, 5, w);  // up to 5: the water for them (the station holds 4 crafts' worth)
        CHECK(m.loaded == 0 && m.wanted == 5 && m.planned == 4 && m.status == KILN_STATION_PARTIAL);
        CHECK(m.limit.why == KILN_STATION_LIMIT_WATER);
        std::vector<uint32_t> moved;
        CHECK(game_fill(w, m.planned, &moved) == 0 && moved[0] == 0 && moved[1] == 4 * 64);
        CHECK(game_fill(w, m.planned + 1, &moved) == 2);
        w[1].have = 64;  // one craft's water
        PlanMath add = plan_math(KILN_STATION_FILL_ADD, 1, w);
        CHECK(add.loaded == 1 && add.planned == 1 && add.loaded + add.planned == 2);  // not 6
        CHECK(!water_input(1, true, 0, 256, 1000, &water));  // NO_WATER: items alone
        CHECK(!water_input(0, false, 0, 256, 1000, &water)); // a recipe without water
        std::vector<InputNow> dry{{0x2b0cf1d5, 12, 60, 100}};
        CHECK(plan_math(0, 5, dry).status == KILN_STATION_UNCHANGED);
        w[1].stock = 0;
        w[1].have = 0;
        PlanMath none = plan_math(0, 1, w);
        CHECK(none.planned == 0 && none.status == KILN_STATION_NOT_ENOUGH && none.limit.why == KILN_STATION_LIMIT_WATER);
    }
    // ---- the player's bag, reserves, EXACT ----
    {
        std::vector<InputNow> bag{{1, 12, 0, after_keep(10, 10) + 24, 10}};  // the base's 10 all reserved, 24 in the bag
        CHECK(plan_math(0, 5, bag).planned == 2);
        std::vector<InputNow> res{{1, 12, 0, after_keep(30, 20), 20}};        // 10 may be taken: not one craft
        PlanMath e = plan_math(KILN_STATION_FILL_EXACT, 1, res);
        CHECK(e.planned == 0 && e.status == KILN_STATION_NOT_ENOUGH && e.limit.why == KILN_STATION_LIMIT_RESERVE && e.limit.item == 1);
        std::vector<InputNow> ok{{1, 12, 0, after_keep(50, 20), 20}};         // 30 may be taken: 2 crafts
        CHECK(plan_math(KILN_STATION_FILL_EXACT, 2, ok).planned == 2);
        CHECK(plan_math(KILN_STATION_FILL_EXACT, 3, ok).planned == 0);
        std::vector<InputNow> short_{{1, 12, 0, 5}, {2, 3, 0, 100}};
        PlanMath s = plan_math(0, 1, short_);
        CHECK(s.status == KILN_STATION_NOT_ENOUGH && s.limit.why == KILN_STATION_LIMIT_STOCK && s.limit.item == 1);
    }
    // ---- one item named twice is one input ----
    {
        std::vector<InputNow> dup{{1, 12, 30, 0}, {1, 3, 30, 0}, {2, 1, 9, 0}};
        merge_inputs(&dup);
        CHECK(dup.size() == 2 && dup[0].count == 15 && crafts_loaded(dup) == 2);
    }
    // ---- the game's function, tried with less after a refusal ----
    {
        int calls = 0;
        auto room3 = [&](uint32_t n) { ++calls; return n > 3 ? 2 : 0; };  // the station takes 3 crafts' inputs
        Attempts a = run_attempts(0, 13, room3);
        CHECK(a.ok && a.n == 3 && a.tries == 3 && calls == 3 && fill_status(a, 13) == KILN_STATION_PARTIAL);
        Attempts x = run_attempts(KILN_STATION_FILL_EXACT, 13, room3);  // all or nothing: no smaller try
        CHECK(!x.ok && x.tries == 1 && fill_status(x, 13) == KILN_STATION_NO_ROOM);
        Attempts never = run_attempts(0, 64, [](uint32_t) { return 4; });
        CHECK(!never.ok && never.tries == kMaxTries && fill_status(never, 64) == KILN_STATION_NOT_ENOUGH);
        Attempts fault = run_attempts(0, 64, [](uint32_t) { return kAttemptFault; });
        CHECK(!fault.ok && fault.tries == 1);
        CHECK(fill_status(run_attempts(0, 5, [](uint32_t) { return 0; }), 5) == KILN_STATION_DONE);
        CHECK(fill_status(run_attempts(0, 5, [](uint32_t) { return 5; }), 5) == KILN_STATION_REFUSED);
        CHECK(!run_attempts(0, 0, room3).ok);  // nothing planned: no call
    }
    // ---- a waiting stop, step by step ----
    {
        StopSeen s{};
        s.present = true;
        s.state = KILN_STATION_STATE_RUNNING;
        s.running = 0x5568cc2b;
        s.selected = 0x5568cc2b;
        s.start = 100;
        s.now = 50'000'000'000;
        CHECK(waiting_stop(s, 0x5568cc2b, 100, 10, 1000) == StopWait::Wait);      // the same cycle runs
        CHECK(waiting_stop(s, 0x5568cc2b, 100, 1001, 1000) == StopWait::TimedOut); // too long: nothing written
        StopSeen p = s;
        p.state = KILN_STATION_STATE_PAUSED;
        CHECK(waiting_stop(p, 0x5568cc2b, 100, 10, 1000) == StopWait::Wait);
        p.state = KILN_STATION_STATE_OUTPUT_FULL;
        CHECK(waiting_stop(p, 0x5568cc2b, 100, 10, 1000) == StopWait::Wait);
        StopSeen resumed = s;
        resumed.start = s.now - 30'000'000'000;  // resumed from a pause: start = now - what it had run
        CHECK(waiting_stop(resumed, 0x5568cc2b, 100, 10, 1000) == StopWait::Wait);
        StopSeen fresh = s;
        fresh.start = s.now - 40'000'000;  // a new cycle, one step old
        CHECK(waiting_stop(fresh, 0x5568cc2b, 100, 10, 1000) == StopWait::Write);
        StopSeen between = s;
        between.state = KILN_STATION_STATE_NO_RECIPE;  // the step between two cycles
        CHECK(waiting_stop(between, 0x5568cc2b, 100, 10, 1000) == StopWait::Write);
        StopSeen stopped = s;
        stopped.stopped = 1;
        CHECK(waiting_stop(stopped, 0x5568cc2b, 100, 10, 1000) == StopWait::Unchanged);
        StopSeen other = s;
        other.selected = 0x1234;
        CHECK(waiting_stop(other, 0x5568cc2b, 100, 10, 1000) == StopWait::Canceled);
        StopSeen gone{};
        CHECK(waiting_stop(gone, 0x5568cc2b, 100, 10, 1000) == StopWait::Gone);
        auto stop = req(KILN_STATION_STOP);
        CHECK(withdraws_waiting(stop));
        stop.flags = KILN_STATION_AFTER_CRAFT;
        CHECK(!withdraws_waiting(stop));
        CHECK(withdraws_waiting(req(KILN_STATION_RESUME)) && withdraws_waiting(req(KILN_STATION_SELECT)));
        CHECK(!withdraws_waiting(req(KILN_STATION_FILL)));
    }
    // ---- the queue: withdraw a station's waiting stops, flush a list ----
    {
        std::vector<Req> after(3);
        after[0].r.station = 10;
        after[1].r.station = 11;
        after[2].r.station = 10;
        auto w = take_if(&after, [](const Req& q) { return q.r.station == 10; });
        CHECK(w.size() == 2 && after.size() == 1 && after[0].r.station == 11);
        auto all = take_if(&after, [](const Req&) { return true; });
        CHECK(all.size() == 1 && after.empty());
    }

    // ---- one step's requests: withdrawals in order, after the action passed ----
    {
        // a fake game: station 10 runs a cycle (a deferred stop waits), station 11's recipe is not this workshop's
        auto act = [](Req& q, KilnStationResult* res) {
            if (is_deferred_stop(q.r)) return true;  // the cycle runs: wait
            if (q.r.action == KILN_STATION_SELECT && q.r.recipe == 0xbad) {
                res->status = KILN_STATION_WRONG_WORKSHOP;
                std::snprintf(res->message, sizeof res->message, "recipe 0x00000bad is a recipe of another workshop");
                return false;
            }
            if (q.r.action == KILN_STATION_RESUME) {  // the station is not stopped
                res->status = KILN_STATION_UNCHANGED;
                std::snprintf(res->message, sizeof res->message, "the station is not stopped");
            }
            return false;
        };
        std::vector<std::pair<uint64_t, KilnStationResult>> out;
        auto done = [&](const Req& q, const KilnStationResult& r) { out.emplace_back(q.ticket, r); };
        auto mk = [](uint64_t ticket, uint32_t action, uint32_t flags = 0, uint32_t recipe = 0) {
            Req q;
            q.ticket = ticket;
            q.r = req(action);
            q.r.station = 10;
            q.r.flags = flags;
            q.r.recipe = recipe;
            q.r.crafts = 0;
            return q;
        };
        auto status_of = [&](uint64_t ticket) {
            for (auto& [t, r] : out)
                if (t == ticket) return r.status;
            return -1;
        };
        // a deferred stop and a resume in the same step: the resume withdraws it
        auto w = run_batch({mk(1, KILN_STATION_STOP, KILN_STATION_AFTER_CRAFT), mk(2, KILN_STATION_RESUME)}, {}, act, done);
        CHECK(w.empty() && status_of(1) == KILN_STATION_CANCELED);
        // two deferred stops in one step: the second finds the first
        out.clear();
        w = run_batch({mk(3, KILN_STATION_STOP, KILN_STATION_AFTER_CRAFT), mk(4, KILN_STATION_STOP, KILN_STATION_AFTER_CRAFT)}, {}, act, done);
        CHECK(w.size() == 1 && w[0].ticket == 3 && status_of(4) == KILN_STATION_UNCHANGED);
        // a select that fails its checks leaves the waiting stop alone
        out.clear();
        std::vector<Req> waiting{mk(5, KILN_STATION_STOP, KILN_STATION_AFTER_CRAFT)};
        w = run_batch({mk(6, KILN_STATION_SELECT, 0, 0xbad)}, waiting, act, done);
        CHECK(w.size() == 1 && w[0].ticket == 5 && status_of(6) == KILN_STATION_WRONG_WORKSHOP && status_of(5) == -1);
        // a resume answering UNCHANGED still withdraws it, and its message says so
        out.clear();
        w = run_batch({mk(7, KILN_STATION_RESUME)}, waiting, act, done);
        CHECK(w.empty() && status_of(5) == KILN_STATION_CANCELED && status_of(7) == KILN_STATION_UNCHANGED);
        bool noted = false;
        for (auto& [t, r] : out)
            if (t == 7) noted = std::strstr(r.message, "not stopped") && std::strstr(r.message, "withdrew");
        CHECK(noted);
        // another station's waiting stop is not touched
        out.clear();
        std::vector<Req> other{mk(8, KILN_STATION_STOP, KILN_STATION_AFTER_CRAFT)};
        other[0].r.station = 11;
        w = run_batch({mk(9, KILN_STATION_RESUME)}, other, act, done);
        CHECK(w.size() == 1 && w[0].ticket == 8);
    }
    // ---- the watch scans at once when the bases were rebuilt ----
    CHECK(scan_due(false, 5, 0, 32, false));
    CHECK(!scan_due(true, 40, 20, 32, false));
    CHECK(scan_due(true, 40, 20, 32, true));
    CHECK(scan_due(true, 60, 20, 32, false));
    // ---- a pause and a resume early in a cycle is the same cycle ----
    {
        Req q;
        q.selected0 = 0x5568cc2b;
        q.start0 = 1'000'000'000;
        StopSeen s{};
        s.present = true;
        s.running = s.selected = 0x5568cc2b;
        s.state = KILN_STATION_STATE_PAUSED;
        s.start = q.start0;
        s.now = 2'000'000'000;  // paused one second in
        CHECK(waiting_stop_step(&q, s, 10, 1000) == StopWait::Wait);
        s.state = KILN_STATION_STATE_RUNNING;
        s.now = 9'000'000'000;
        s.start = s.now - 1'000'000'000;  // resumed: start = now - the second it had run
        CHECK(waiting_stop_step(&q, s, 11, 1000) == StopWait::Wait);
        CHECK(waiting_stop_step(&q, s, 12, 1000) == StopWait::Wait);  // and the steps after
        StopSeen next = s;
        next.state = KILN_STATION_STATE_NO_RECIPE;  // it ended
        CHECK(waiting_stop_step(&q, next, 13, 1000) == StopWait::Write);
    }

    // ---- a base's id ----
    KilnBaseId ids[3]{};
    ids[0].guid[0] = 0x80;
    ids[1].guid[0] = 0x10;
    ids[1].guid[15] = 7;
    // ids[2] all zero: skipped
    KilnBaseId m = smallest_id(ids, 3);
    CHECK(m.guid[0] == 0x10 && m.guid[15] == 7);
    KilnBaseId none = smallest_id(ids + 2, 1);
    CHECK(none.guid[0] == 0 && none.guid[15] == 0);

    // ---- the state diff and the ring ----
    Snap a{}, b{};
    a.state = KILN_STATION_STATE_RUNNING;
    a.running = a.selected = 0x5568cc2b;
    b = a;
    CHECK(a == b);
    b.stopped = 1;
    b.state = KILN_STATION_STATE_STOPPED;
    CHECK(!(a == b));
    b.pos.x = 3800.0;
    KilnStationEvent ev = state_event(1318, a, b);
    CHECK(ev.kind == KILN_STATION_EV_STATE && ev.stopped_before == 0 && ev.stopped_after == 1 && ev.state_after == 3 &&
          ev.position.x == 3800.0);
    EventRing ring;
    KilnStationEvent out[4];
    bool more = false;
    CHECK(ring.read(0, out, 4, &more) == 0 && !more);
    for (int i = 0; i < 6; ++i) ring.push(ev);
    CHECK(ring.last() == 6);
    CHECK(ring.read(0, out, 4, &more) == 4 && more && out[0].seq == 1 && out[3].seq == 4);
    CHECK(ring.read(4, out, 4, &more) == 2 && !more && out[1].seq == 6);
    CHECK(ring.read(6, out, 4, &more) == 0);
    for (int i = 0; i < 2000; ++i) ring.push(ev);  // the ring keeps the last 1024
    CHECK(ring.last() == 2006);
    CHECK(ring.read(0, out, 1, &more) == 1 && out[0].seq == 2006 - 1024 + 1 && more);
    ring.clear();
    CHECK(ring.read(0, out, 4, &more) == 0 && ring.last() == 2006);
    ring.push(ev);
    CHECK(ring.read(2006, out, 4, &more) == 1 && out[0].seq == 2007);

    // ---- SELECT's unlock verdict ----
    CHECK(unlock_verdict(true, -1, {}) == Unlock::Yes);           // no requirement
    CHECK(unlock_verdict(false, 1, {}) == Unlock::Yes);           // the world has it (shared progression)
    CHECK(unlock_verdict(false, 0, {0, 1}) == Unlock::Yes);       // one player in game has it
    CHECK(unlock_verdict(false, 0, {0, 0}) == Unlock::No);
    CHECK(unlock_verdict(false, -1, {0}) == Unlock::No);
    CHECK(unlock_verdict(false, -1, {}) == Unlock::Unknown);      // a player query and no player in game
    CHECK(unlock_verdict(false, -1, {-1, -1}) == Unlock::Unknown);

    // ---- status: the events are on before a world runs (a mod subscribes in OnLoad) ----
    {
        const StatusBits menu = status_bits(true, true, true, true, false, false);  // no world yet, no program yet
        CHECK(!menu.read && !menu.fill && !menu.control);
        CHECK(menu.events);
        const StatusBits world = status_bits(true, true, true, true, true, true);
        CHECK(world.read && world.fill && world.control && world.events);
        const StatusBits broken = status_bits(true, true, true, true, true, false);  // the program broke
        CHECK(broken.read && !broken.fill && !broken.control && broken.events);
        CHECK(!status_bits(false, true, true, true, true, true).events);              // no reads, no watch
        CHECK(!status_bits(true, true, true, false, true, true).events);
    }

    // ---- station outputs as crafting stock (GitHub #68) ----
    {
        // the request flag: FILL only
        KilnStationRequest r = req(KILN_STATION_FILL);
        r.flags = KILN_STATION_FILL_NO_OUTPUTS;
        CHECK(request_valid(r));
        KilnStationRequest s2 = req(KILN_STATION_STOP);
        s2.flags = KILN_STATION_FILL_NO_OUTPUTS;
        CHECK(!request_valid(s2));
        r.flags = 0x0100;  // unknown
        CHECK(!request_valid(r));

        // classification: a charcoal kiln (logs -> charcoal) lends charcoal only; never its logs (an input), never a
        // leftover (dirt: neither), never an output that is also an input (seed -> seed + fibre: the seed)
        const uint32_t LOGS = 11, CHARCOAL = 12, DIRT = 13, SEED = 14, FIBRE = 15, ORE = 16, OAK = 17, PINE = 18;
        SourceRecipes kiln{{CHARCOAL}, {LOGS}};
        CHECK(lends_item(kiln, CHARCOAL));
        CHECK(!lends_item(kiln, LOGS));
        CHECK(!lends_item(kiln, DIRT));
        CHECK(!lends_item(kiln, 0));
        SourceRecipes bed{{SEED, FIBRE}, {SEED}};
        CHECK(!lends_item(bed, SEED));
        CHECK(lends_item(bed, FIBRE));
        // a category input: every option counts as an input (an "any wood" kiln never lends oak or pine)
        SourceRecipes anywood{{CHARCOAL, OAK}, {OAK, PINE}};
        CHECK(!lends_item(anywood, OAK) && !lends_item(anywood, PINE) && lends_item(anywood, CHARCOAL));
        // the running recipe counts too: selected makes charcoal, running still makes logs from ... (both recipes' sets)
        SourceRecipes both{{CHARCOAL, LOGS}, {LOGS, ORE}};
        CHECK(!lends_item(both, LOGS) && !lends_item(both, ORE) && lends_item(both, CHARCOAL));
        SourceRecipes none{};
        CHECK(!lends_item(none, CHARCOAL));

        // eligibility: opted in, still a station, the same base, not the target; entity order, once each
        const std::vector<SourceCandidate> cands = {{40, 0, true}, {30, 0, true}, {50, 1, true}, {60, 0, false},
                                                    {20, -1, true}, {10, 0, true}, {30, 0, true}};
        const std::vector<uint32_t> el = eligible_sources(cands, 10, 0);
        CHECK(el.size() == 2 && el[0] == 30 && el[1] == 40);  // 50: another base; 60: no station; 20: no base; 10: target
        CHECK(eligible_sources(cands, 10, -1).empty());
        CHECK(eligible_sources(cands, 99, 1).size() == 1);

        const uint32_t rep[3] = {LOGS, CHARCOAL, LOGS}, norep[2] = {ORE, CHARCOAL};
        CHECK(inputs_repeat(rep, 3) && !inputs_repeat(norep, 2) && !inputs_repeat(norep, 0));
        CHECK(!use_named_chests(rep, 3) && use_named_chests(norep, 2));
        // chests were named, but the resolved inputs repeat an item: they are ignored and the plain fill runs
        std::vector<InputNow> repeated(1);
        repeated[0].item = CHARCOAL;
        repeated[0].count = 1;
        repeated[0].stock = 4;  // Magic chests and the bag only; the named chests are not added
        repeated[0].chests = 0;
        std::vector<uint32_t> rep_chests, rep_outs;
        CHECK(pay_layers(repeated, 4, 0, &rep_chests, &rep_outs) && rep_chests[0] == 0 && !any_share(rep_chests));
        CHECK(plan_math(KILN_STATION_FILL_ADD, 4, repeated).status == KILN_STATION_DONE);

        // bisdat's case: a forge (ore 2 + charcoal 3 a craft) holds nothing; the chests have 10 ore and 4 charcoal,
        // an opted-in kiln lends 20 charcoal. A fill of 3 crafts: chests pay 4 charcoal first, the kiln the other 5.
        std::vector<InputNow> in(2);
        in[0].item = ORE, in[0].count = 2, in[0].stock = 10;
        in[1].item = CHARCOAL, in[1].count = 3, in[1].stock = 4 + 20, in[1].outputs = 20;
        PlanMath m = plan_math(KILN_STATION_FILL_ADD, 3, in);
        CHECK(m.status == KILN_STATION_DONE && m.planned == 3 && m.affordable == 5);  // ore covers 5, charcoal 8
        std::vector<uint32_t> shares;
        CHECK(output_shares(in, 3, m.loaded, &shares) && shares[0] == 0 && shares[1] == 5 && shares_total(shares) == 5);
        CHECK(any_share(shares));
        // after the share is in the forge (5 charcoal), the game's own call for the rest is still 3 crafts on top of 0
        CHECK(crafts_after_shares(in, shares, 3, m.loaded) == 3);

        // the two game calls together put exactly what a plain fill of n puts, the chests paying first
        auto both_calls = [&](std::vector<InputNow> v, uint32_t n, std::vector<uint64_t>* first_paid, std::vector<uint64_t>* outs_paid) {
            const uint32_t loaded = crafts_loaded(v);
            std::vector<uint32_t> sh;
            if (!output_shares(v, n, loaded, &sh)) return -1;
            first_paid->assign(v.size(), 0);
            outs_paid->assign(v.size(), 0);
            // call 1 per share: one stack with count 1, crafts = share -> the game moves exactly the share
            for (size_t k = 0; k < v.size(); ++k) {
                if (!sh[k]) continue;
                InputNow one = v[k];
                one.count = 1;
                one.stock = v[k].outputs;
                std::vector<uint32_t> mv;
                if (game_fill({one}, sh[k], &mv) != 0 || mv[0] != sh[k]) return -2;
                v[k].have += mv[0];
                (*outs_paid)[k] = mv[0];
            }
            // call 2: the base's own sources only, n' = n + loaded - loaded'
            std::vector<InputNow> rest = v;
            for (auto& x : rest) x.stock -= std::min(x.stock, x.outputs);
            const uint32_t n2 = crafts_after_shares(v, std::vector<uint32_t>(v.size(), 0), n, loaded);
            std::vector<uint32_t> mv;
            if (n2 > 0) {
                if (game_fill(rest, n2, &mv) != 0) return -3;
                for (size_t k = 0; k < v.size(); ++k) {
                    v[k].have += mv[k];
                    (*first_paid)[k] = mv[k];
                }
            }
            // every input ends where a plain fill of n would put it
            for (const auto& x : v)
                if (x.count && x.have < static_cast<uint64_t>(n + loaded) * x.count) return -4;
            return static_cast<int>(crafts_loaded(v));
        };
        std::vector<uint64_t> fp, op;
        CHECK(both_calls(in, 3, &fp, &op) == 3 && fp[0] == 6 && fp[1] == 4 && op[0] == 0 && op[1] == 5);
        // outputs taken last: chests that cover it all leave the kiln untouched
        in[1].stock = 30 + 20;
        CHECK(output_shares(in, 3, 0, &shares) && !any_share(shares));
        CHECK(both_calls(in, 3, &fp, &op) == 3 && fp[1] == 9 && op[1] == 0);
        // a forge that already holds a partial load: have counts, so only the top-up is paid
        in[0].have = 3, in[1].have = 4, in[1].stock = 2 + 20;  // loaded = min(3/2, 4/3) = 1
        CHECK(crafts_loaded(in) == 1);
        CHECK(output_shares(in, 2, 1, &shares) && shares[1] == 3);  // need 3*3-4 = 5, chests 2, kiln 3
        CHECK(crafts_after_shares(in, shares, 2, 1) == 2);          // charcoal 7 -> loaded' 1 still (ore): 2 more
        CHECK(both_calls(in, 2, &fp, &op) == 3 && op[1] == 3 && fp[1] == 2 && fp[0] == 3);
        // the share lifts the bottleneck: then the game's own call asks for fewer crafts on top of a higher loaded
        std::vector<InputNow> one(1);
        one[0].item = CHARCOAL, one[0].count = 3, one[0].have = 2, one[0].stock = 0 + 20, one[0].outputs = 20;
        CHECK(output_shares(one, 2, 0, &shares) && shares[0] == 4);
        CHECK(crafts_after_shares(one, shares, 2, 0) == 0);  // the outputs alone reach the target: no second call
        CHECK(both_calls(one, 2, &fp, &op) == 2 && op[0] == 4);
        // not enough: the outputs do not cover the rest -> no share at all (the fill tries fewer or nothing moves)
        std::vector<InputNow> shortv(1);
        shortv[0].item = CHARCOAL, shortv[0].count = 3, shortv[0].stock = 2 + 4, shortv[0].outputs = 4;
        m = plan_math(KILN_STATION_FILL_ADD | KILN_STATION_FILL_EXACT, 3, shortv);
        CHECK(m.status == KILN_STATION_NOT_ENOUGH && m.planned == 0 && m.limit.why == KILN_STATION_LIMIT_STOCK);
        CHECK(!output_shares(shortv, 3, 0, &shares));
        m = plan_math(KILN_STATION_FILL_ADD, 3, shortv);
        CHECK(m.status == KILN_STATION_PARTIAL && m.planned == 2);
        // water never comes from outputs: the game's own call pours it
        std::vector<InputNow> wet(2);
        wet[0].item = CHARCOAL, wet[0].count = 1, wet[0].stock = 5, wet[0].outputs = 5;
        CHECK(water_input(1, false, 0, 640, 640, &wet[1]));
        CHECK(output_shares(wet, 2, 0, &shares) && shares[0] == 2 && shares[1] == 0);
        CHECK(crafts_after_shares(wet, shares, 2, 0) == 2);  // the water still holds loaded at 0: the game's call pours 2 crafts' water
        // reserves: the chests' stock already excludes the reserve, so outputs pay before the reserve is touched
        std::vector<InputNow> kept(1);
        kept[0].item = CHARCOAL, kept[0].count = 1, kept[0].kept = 10, kept[0].stock = 2 + 8, kept[0].outputs = 8;
        CHECK(output_shares(kept, 6, 0, &shares) && shares[0] == 4);

        // ordinary chests pay after the Magic chests and before station outputs
        ChestFacts plain;
        plain.present = true;
        plain.inventory = true;
        plain.base = 0;
        CHECK(kind_of(plain, 4000, 10, 0) == ChestKind::Ordinary);
        CHECK(kind_of(plain, 10, 10, 0) == ChestKind::IsTarget);
        CHECK(kind_of(plain, 4, 10, 0) == ChestKind::Player);
        ChestFacts magic = plain;
        magic.magic = true;
        CHECK(kind_of(magic, 4000, 10, 0) == ChestKind::Magic);
        ChestFacts away = plain;
        away.base = 1;
        CHECK(kind_of(away, 4000, 10, 0) == ChestKind::OtherBase);
        ChestFacts bare = plain;
        bare.inventory = false;
        CHECK(kind_of(bare, 4000, 10, 0) == ChestKind::NoInventory);
        // a villager or a player's inventory in the base is not a storage chest (InventorySetup flags bit 0 / bit 1)
        ChestFacts villager = plain;
        villager.person = setup_is_person(0x02);
        CHECK(villager.person && kind_of(villager, 4000, 10, 0) == ChestKind::Person);
        ChestFacts body = plain;
        body.person = setup_is_person(0x01);
        CHECK(kind_of(body, 4000, 10, 0) == ChestKind::Person);
        CHECK(!setup_is_person(0x00) && !setup_is_person(0x04));
        CHECK(std::strstr(chest_kind_why(ChestKind::Person), "NPC") != nullptr);
        const std::vector<NamedChest> boxes = {{4002, 0, ChestKind::Ordinary},
                                                {4000, 0, ChestKind::Ordinary},
                                                {4000, 0, ChestKind::Ordinary},
                                                {4005, 1, ChestKind::OtherBase}};
        uint32_t bad = 0;
        ChestKind why = ChestKind::Ordinary;
        CHECK(!check_named_chests(boxes, &bad, &why) && bad == 4005 && why == ChestKind::OtherBase);
        const std::vector<NamedChest> okboxes = {{4002, 0, ChestKind::Ordinary}, {4000, 0, ChestKind::Ordinary}, {4000, 0, ChestKind::Ordinary}};
        CHECK(check_named_chests(okboxes, &bad, &why));
        const std::vector<uint32_t> got = eligible_chests(okboxes, 10, 0);
        CHECK(got.size() == 2 && got[0] == 4002 && got[1] == 4000);  // the request's order, a repeat once

        // 3 crafts need 9 charcoal. Magic chests hold 2, an ordinary chest 3, a kiln lends 20.
        std::vector<InputNow> layered(1);
        layered[0].item = CHARCOAL;
        layered[0].count = 3;
        layered[0].stock = 2 + 3 + 20;
        layered[0].chests = 3;
        layered[0].outputs = 20;
        std::vector<uint32_t> csh, osh;
        CHECK(pay_layers(layered, 3, 0, &csh, &osh) && csh[0] == 3 && osh[0] == 4);
        CHECK(output_shares(layered, 3, 0, &shares) && shares[0] == 4);
        CHECK(crafts_after_shares(layered, share_sum(csh, osh), 3, 0) == 1);  // 7 in: loaded' is 2, one more craft takes the 2 from the Magic chests
        // the Magic chests cover it: neither later source is touched
        layered[0].stock = 9 + 3 + 20;
        CHECK(pay_layers(layered, 3, 0, &csh, &osh) && !any_share(csh) && !any_share(osh));
        // chests cover the rest and the kiln is not owed anything
        layered[0].stock = 4 + 5;
        layered[0].chests = 5;
        layered[0].outputs = 0;
        CHECK(pay_layers(layered, 3, 0, &csh, &osh) && csh[0] == 5 && osh[0] == 0);
        // short even with the chest: nothing is taken
        layered[0].stock = 4 + 2;
        layered[0].chests = 2;
        CHECK(!pay_layers(layered, 3, 0, &csh, &osh));
        // water is never taken from a chest
        std::vector<InputNow> wetchest(2);
        wetchest[0].item = CHARCOAL;
        wetchest[0].count = 1;
        wetchest[0].stock = 0 + 2;
        wetchest[0].chests = 2;
        CHECK(water_input(1, false, 0, 640, 640, &wetchest[1]));
        CHECK(pay_layers(wetchest, 2, 0, &csh, &osh) && csh[0] == 2 && csh[1] == 0 && osh[0] == 0);
        CHECK(crafts_after_shares(wetchest, share_sum(csh, osh), 2, 0) == 2);

        // collect: the same split as the managed SlotRoles, and the same result counts
        SourceRecipes kiln_roles{{CHARCOAL}, {LOGS}};
        CHECK(slot_role(kiln_roles, CHARCOAL, true) == SlotRole::Output);
        CHECK(slot_role(kiln_roles, LOGS, true) == SlotRole::None);
        CHECK(slot_role(kiln_roles, DIRT, true) == SlotRole::Leftover);
        CHECK(slot_role(kiln_roles, DIRT, false) == SlotRole::None);  // no recipe: leftovers are not named
        CHECK(collect_status(0, 0) == KILN_STATION_UNCHANGED);
        CHECK(collect_status(3, 0) == KILN_STATION_NO_ROOM);
        CHECK(collect_status(3, 2) == KILN_STATION_PARTIAL);
        CHECK(collect_status(3, 3) == KILN_STATION_DONE);
        // a stack of 64: the first chest takes 10, a later chest takes the rest → Done only when nothing remains
        const uint32_t both_chests[2] = {10, 54};
        StackCollect moved = collect_stack(64, both_chests, 2);
        CHECK(moved.finished && moved.any && moved.left == 0);
        CHECK(collect_status(1, moved.finished ? 1u : 0u, moved.any && !moved.finished) == KILN_STATION_DONE);
        const uint32_t one_chest[2] = {10, 0};
        moved = collect_stack(64, one_chest, 2);
        CHECK(!moved.finished && moved.any && moved.left == 54);
        CHECK(collect_status(1, 0, moved.any) == KILN_STATION_PARTIAL);
        CHECK(collect_status(1, 0, false) == KILN_STATION_NO_ROOM);
        // no base: the managed CollectToBase answered NoRoom
        CHECK(collect_without_base() == KILN_STATION_NO_ROOM);
        // override unset: the template deposit bit. Unknown stays allowed. A set override wins.
        DepositRead dep;
        CHECK(takes_deposits(dep));
        dep.template_known = true;
        dep.template_on = false;
        CHECK(!takes_deposits(dep));
        dep.template_on = true;
        CHECK(takes_deposits(dep));
        dep.component = true;
        dep.override_set = true;
        dep.override_on = false;
        CHECK(!takes_deposits(dep));
        // a partial stack is preferred to an empty slot in another chest; a chest with neither is not used
        const std::vector<ChestOffer> offers = {{7, false, true}, {9, true, true}, {8, false, false}};
        const std::vector<uint32_t> ord = collect_chest_order(offers);
        CHECK(ord.size() == 2 && ord[0] == 9 && ord[1] == 7);
        KilnStationCollect col{};
        col.size = sizeof col;
        col.station = 1318;
        col.flags = KILN_STATION_COLLECT_OUTPUTS;
        col.to = 4000;
        CHECK(collect_valid(col));
        col.flags |= KILN_STATION_COLLECT_TO_BASE;
        CHECK(!collect_valid(col));  // to base: no container named
        col.to = 0;
        CHECK(collect_valid(col));
        col.flags = KILN_STATION_COLLECT_TO_BASE;
        CHECK(!collect_valid(col));  // outputs or leftovers is required
        col.to = 1318;
        col.flags = KILN_STATION_COLLECT_OUTPUTS;
        CHECK(!collect_valid(col));  // not into itself
        col.to = 5;
        CHECK(collect_valid(col));   // a player's bag, as Stations.Collect(station, player) through Items.Move
        col.to = 4000;
        col.flags = KILN_STATION_COLLECT_OUTPUTS | 0x4u;  // the old TO_BASE bit is no collect flag
        CHECK(!collect_valid(col));
        col.flags = KILN_STATION_COLLECT_LEFTOVERS;
        col.reserved = 1;
        CHECK(!collect_valid(col));
        col.reserved = 0;
        col.size = sizeof col - 4;
        CHECK(!collect_valid(col));
        // a fault after earlier stacks moved is PARTIAL (they stay moved); before anything moved, UNAVAILABLE
        CHECK(collect_fault_status(true) == KILN_STATION_PARTIAL && collect_fault_status(false) == KILN_STATION_UNAVAILABLE);
        // crafting payment: outputs cover only the shortfall, and a line they cannot cover takes nothing
        std::vector<CraftCost> craft = {{0xaaa, 4, 2, 4}, {0xbbb, 2, 2, 9}};
        std::vector<uint32_t> took;
        CHECK(craft_output_takes(craft, &took) && took[0] == 2 && took[1] == 0);
        craft = {{0xaaa, 4, 1, 2}};  // short by 3, outputs hold 2
        CHECK(!craft_output_takes(craft, &took) && took[0] == 0);
        craft = {{0xaaa, 4, 4, 9}};  // the game's sources already cover it
        CHECK(craft_output_takes(craft, &took) && took[0] == 0);
        craft = {{0xaaa, 0, 0, 5}};
        CHECK(craft_output_takes(craft, &took) && took[0] == 0);
    }

    // ---- a craft's payment (craft_pay_impl's logic; docs/re/stations.md §10) ----
    {
        constexpr uint32_t COAL = 0xc0a1, LOGS = 0x1095, SCRAP = 0x5c4a, LEFT = 0x1eff, BOTH = 0xb074;
        constexpr uint32_t P = 5, C1 = 2001, C2 = 2002, K1 = 3001, K2 = 3002, K3 = 3003, W = 3004;
        // who pays before any station, in the game's order
        std::vector<uint32_t> order;
        const uint32_t list[3] = {C1, P, C2};
        CHECK(craft_order(2, P, list, 3, &order) && order == std::vector<uint32_t>({P, C1, C2}));  // owner first, once
        CHECK(craft_order(1, P, list, 3, &order) && order == std::vector<uint32_t>({C1, P, C2}));  // list first
        const uint32_t chests[2] = {C1, C2};
        CHECK(craft_order(1, P, chests, 2, &order) && order == std::vector<uint32_t>({C1, C2, P}));
        CHECK(craft_order(0, P, chests, 2, &order) && order == std::vector<uint32_t>({P}));  // mode 0: owner only
        CHECK(!craft_order(3, P, chests, 2, &order));
        CHECK(!craft_order(2, P, nullptr, 2, &order));
        std::vector<uint32_t> big(kMaxCraftStock + 1, C1);
        CHECK(!craft_order(2, P, big.data(), big.size(), &order));  // longer than the cap: the game pays
        CHECK(craft_order(2, P, nullptr, 0, &order) && order == std::vector<uint32_t>({P}));

        // the world: what each entity holds, and the stations' recipes
        std::unordered_map<uint32_t, std::unordered_map<uint32_t, uint64_t>> inv;
        auto reset = [&] {
            inv.clear();
            inv[P][COAL] = 3, inv[P][SCRAP] = 2;
            inv[C1][COAL] = 4;
            inv[K1][COAL] = 8, inv[K1][LOGS] = 6, inv[K1][LEFT] = 9;  // finished coal, queued logs, a Leftover
            inv[K2][COAL] = 50;                                          // the same kiln in another base
            inv[K3][BOTH] = 7, inv[K3][COAL] = 0;                        // an output that is also an input
            inv[W][COAL] = 40;                                           // the station being crafted at
        };
        reset();
        auto count = [&](uint32_t e, uint32_t item) -> uint64_t {
            const auto it = inv.find(e);
            if (it == inv.end()) return 0;
            const auto n = it->second.find(item);
            return n == it->second.end() ? 0 : n->second;
        };
        SourceRecipes kiln_r{{COAL}, {LOGS}};        // logs -> charcoal
        SourceRecipes both_r{{BOTH, COAL}, {BOTH}};  // an output that is also an input stays; its coal would lend
        SourceRecipes bench_r{{COAL}, {LOGS}};
        std::vector<CraftLender> lenders = {{K1, 0, true, kiln_r}, {K2, 1, true, kiln_r}, {K3, 0, true, both_r},
                                            {W, 0, true, bench_r}, {C2, 0, false, {}}};
        CraftLent lent;
        craft_order(2, P, chests, 2, &order);
        craft_lenders(lenders, W, 0, order, {COAL, LOGS, LEFT, BOTH, SCRAP}, count, &lent);
        CHECK(lent.count[COAL] == 8 && lent.by[COAL] == std::vector<uint32_t>({K1}));  // not K2 (other base), not W, not K3 (0)
        CHECK(lent.count.find(LOGS) == lent.count.end());  // queued inputs never lend
        CHECK(lent.count.find(LEFT) == lent.count.end());  // nor Leftovers
        CHECK(lent.count.find(BOTH) == lent.count.end());  // nor an output that is also an input
        CHECK(lent.count.find(SCRAP) == lent.count.end());
        CraftLent lent_in_order;  // a station the game's own sources already list is not lent twice
        const std::vector<uint32_t> order_with_k1 = {P, K1};
        craft_lenders(lenders, W, 0, order_with_k1, {COAL}, count, &lent_in_order);
        CHECK(lent_in_order.count.empty());
        CraftLent lent_no_exclude;  // without a known station crafted at, W lends like any opted-in station
        craft_lenders(lenders, 0, 0, order, {COAL}, count, &lent_no_exclude);
        CHECK(lent_no_exclude.count[COAL] == 48 && lent_no_exclude.by[COAL] == std::vector<uint32_t>({K1, W}));

        // pay: a fake PayMaterial over `inv`, with a log of every take and an optional fault
        struct Take {
            uint32_t e, item, n;
        };
        std::vector<Take> log;
        uint32_t fault_at = 0;
        std::unordered_map<uint32_t, bool> empty_pay;  // pay finds nothing there, whatever have() said
        auto pay = [&](uint32_t e, uint32_t item, uint32_t* left) {
            if (e == fault_at) return false;
            const uint64_t has = empty_pay[e] ? 0 : count(e, item);
            const uint32_t n = static_cast<uint32_t>(std::min<uint64_t>(has, *left));
            if (n) {
                inv[e][item] -= n;
                log.push_back({e, item, n});
            }
            *left -= n;
            return true;
        };
        uint64_t from = 0;
        uint32_t why = 0;
        // the bag, then the chest, then the kiln's outputs for the rest only; logs and the Leftover stay
        std::vector<CraftCost> lines = {{COAL, 10, 0, 0}, {SCRAP, 2, 0, 0}};
        CHECK(craft_pay(&lines, order, lent, count, pay, &from, &why) == kCraftPaid && why == kCraftWhyNone && from == 3);
        CHECK(log.size() == 4 && log[0].e == P && log[0].n == 3 && log[1].e == C1 && log[1].n == 4 && log[2].e == K1 &&
              log[2].item == COAL && log[2].n == 3 && log[3].e == P && log[3].item == SCRAP && log[3].n == 2);
        CHECK(inv[K1][COAL] == 5 && inv[K1][LOGS] == 6 && inv[K1][LEFT] == 9 && inv[K2][COAL] == 50 && inv[W][COAL] == 40);
        CHECK(lines[0].game == 7 && lines[0].outputs == 8);

        // not enough: 7 from the bag and the chest, 8 from outputs, 20 asked -> nothing taken, the game's own payment
        reset();
        log.clear();
        lines = {{COAL, 20, 0, 0}};
        CHECK(craft_pay(&lines, order, lent, count, pay, &from, &why) == kCraftGame && why == kCraftWhyShort && log.empty());
        // all or nothing over every line: one line short of the outputs keeps the other's outputs too
        lines = {{COAL, 10, 0, 0}, {SCRAP, 5, 0, 0}};
        CHECK(craft_pay(&lines, order, lent, count, pay, &from, &why) == kCraftGame && why == kCraftWhyShort && log.empty());
        // the bag and the chests cover it: the game pays, Kiln takes nothing
        lines = {{COAL, 6, 0, 0}, {SCRAP, 2, 0, 0}};
        CHECK(craft_pay(&lines, order, lent, count, pay, &from, &why) == kCraftGame && why == kCraftWhyCovered && log.empty());
        // another base is never used: only base 1's kiln could cover it
        CraftLent other;
        craft_lenders(lenders, W, 1, order, {COAL}, count, &other);
        CHECK(other.count[COAL] == 50 && other.by[COAL] == std::vector<uint32_t>({K2}));
        CraftLent none;
        craft_lenders(lenders, W, 2, order, {COAL}, count, &none);  // a base with no opted-in station
        lines = {{COAL, 10, 0, 0}};
        CHECK(craft_pay(&lines, order, none, count, pay, &from, &why) == kCraftGame && why == kCraftWhyShort && log.empty());
        // a fault after a take: InventoryPay's 4, the craft's rollback undoes the bag's take
        fault_at = K1;
        lines = {{COAL, 10, 0, 0}};
        CHECK(craft_pay(&lines, order, lent, count, pay, &from, &why) == kCraftRollback && why == kCraftWhyFault);
        fault_at = 0;
        // the counts did not hold (the kiln holds less than counted): the outputs pay what they hold and the line stays
        // short -> 4, never a partial craft
        reset();
        log.clear();
        inv[K1][COAL] = 1;
        lines = {{COAL, 10, 0, 0}};
        CHECK(craft_pay(&lines, order, lent, count, pay, &from, &why) == kCraftRollback && why == kCraftWhyRemainder);
        // GitHub #132: the bag pays less than it was counted to hold (3 counted, nothing taken): the kiln must not pay
        // more than the counted shortfall (3) though it holds 8 -> 4 before any lender is asked
        reset();
        log.clear();
        empty_pay[P] = true;
        lines = {{COAL, 10, 0, 0}};
        CHECK(craft_pay(&lines, order, lent, count, pay, &from, &why) == kCraftRollback && why == kCraftWhyGamePaidLess);
        CHECK(from == 0 && inv[K1][COAL] == 8 && std::none_of(log.begin(), log.end(), [&](const Take& t) { return t.e == K1; }));
        empty_pay.clear();
        // and with every count holding, the lender pays exactly the shortfall, not what it holds
        reset();
        log.clear();
        lines = {{COAL, 9, 0, 0}};
        CHECK(craft_pay(&lines, order, lent, count, pay, &from, &why) == kCraftPaid && from == 2 && inv[K1][COAL] == 6);
        // mode 1: the chests before the owner
        reset();
        log.clear();
        craft_order(1, P, chests, 2, &order);
        lines = {{COAL, 10, 0, 0}};
        CHECK(craft_pay(&lines, order, lent, count, pay, &from, &why) == kCraftPaid && from == 3 && log.size() == 3 &&
              log[0].e == C1 && log[1].e == P && log[2].e == K1);

        // what a Kiln client is sent: the inputs of the recipes that take a lent item, and the lent items
        const std::vector<std::vector<uint32_t>> recipes = {{COAL, SCRAP}, {LOGS}, {BOTH, LEFT}, {0x777, COAL}};
        std::vector<uint32_t> want = {COAL, SCRAP, 0x777};
        std::sort(want.begin(), want.end());
        CHECK(craft_stock_items(recipes, {COAL}) == want);
        CHECK(craft_stock_items(recipes, {}).empty());
        CHECK(craft_stock_items(recipes, {0x999}) == std::vector<uint32_t>({0x999}));  // lent, used by no recipe
    }

    // ---- the station screen's fill buttons on a Kiln client (GitHub #68, docs/re/stations.md §10.3) ----
    {
        static_assert(sizeof(KilnStationFillKey) == 40 && offsetof(KilnStationFillKey, choices) == 8);
        static_assert(sizeof(KilnStationFillAnswer) == 48 && offsetof(KilnStationFillAnswer, crafts) == 40);
        constexpr uint32_t FORGE = 1032, KILN2 = 1040, SHEETS = 0x3acc518b, OTHER = 0x1234;
        KilnStationChoice sel[4] = {};
        sel[1].category = 0x77;
        sel[1].index = 2;
        sel[1].reserved[0] = 0xcd;  // the game's padding byte is not part of the key
        const KilnStationFillKey k = fill_key(FORGE, SHEETS, sel);
        CHECK(k.choices[1].category == 0x77 && k.choices[1].index == 2 && k.choices[1].reserved[0] == 0);
        CHECK(same_fill(k, fill_key(FORGE, SHEETS, sel)));
        KilnStationChoice sel2[4] = {};
        std::memcpy(sel2, sel, sizeof sel);
        sel2[1].index = 3;
        CHECK(!same_fill(k, fill_key(FORGE, SHEETS, sel2)));  // another option of the category
        CHECK(!same_fill(k, fill_key(KILN2, SHEETS, sel)) && !same_fill(k, fill_key(FORGE, OTHER, sel)));
        CHECK(same_fill(fill_key(FORGE, SHEETS, nullptr), fill_key(FORGE, SHEETS, std::vector<KilnStationChoice>(4).data())));

        std::vector<KilnStationFillAnswer> answers;
        bool answered = true;
        // no answer: the game's status, and the screen's query is noted as unanswered
        CHECK(fill_ui_status(kUiMissing, kUiQueryFill, 1, k, answers, &answered) == kUiMissing && !answered);
        answers.push_back(KilnStationFillAnswer{k, 10, 0});
        // the server's fill with station outputs covers 10 more crafts: "+1" and "+10" can fill, "+11" cannot
        CHECK(fill_ui_status(kUiMissing, kUiQueryFill, 1, k, answers, &answered) == kUiOk && answered);
        CHECK(fill_ui_status(kUiMissing, kUiQueryFill, 10, k, answers, &answered) == kUiOk);
        CHECK(fill_ui_status(kUiMissing, kUiQueryFill, 11, k, answers, &answered) == kUiMissing && answered);
        CHECK(fill_ui_status(kUiMissing, kUiQueryFill, 0, k, answers, &answered) == kUiMissing);
        // only "missing materials" is raised: not run yet, no room, no inventory and ok stay the game's
        CHECK(fill_ui_status(0, kUiQueryFill, 1, k, answers, &answered) == 0);
        CHECK(fill_ui_status(2, kUiQueryFill, 1, k, answers, &answered) == 2);
        CHECK(fill_ui_status(5, kUiQueryFill, 1, k, answers, &answered) == 5);
        CHECK(fill_ui_status(kUiOk, kUiQueryFill, 1, k, answers, &answered) == kUiOk);
        // other queries (the item fill 4, the craft 1, ...) are never touched
        CHECK(fill_ui_status(kUiMissing, 4, 1, k, answers, &answered) == kUiMissing && !answered);
        CHECK(fill_ui_status(kUiMissing, 1, 1, k, answers, &answered) == kUiMissing);
        // an answer for another station, recipe or choice does not count
        CHECK(fill_ui_status(kUiMissing, kUiQueryFill, 1, fill_key(KILN2, SHEETS, sel), answers, &answered) == kUiMissing && !answered);
        CHECK(fill_ui_status(kUiMissing, kUiQueryFill, 1, fill_key(FORGE, SHEETS, sel2), answers, &answered) == kUiMissing && !answered);
        // an answer of 0 crafts: answered, still the game's refusal
        answers[0].crafts = 0;
        CHECK(fill_ui_status(kUiMissing, kUiQueryFill, 1, k, answers, &answered) == kUiMissing && answered);

        // the rows set_fill_answers takes
        CHECK(fill_answer_ok(KilnStationFillAnswer{k, 3, 0}));
        CHECK(!fill_answer_ok(KilnStationFillAnswer{k, 3, 1}));
        CHECK(!fill_answer_ok(KilnStationFillAnswer{fill_key(0, SHEETS, nullptr), 3, 0}));
        CHECK(!fill_answer_ok(KilnStationFillAnswer{fill_key(FORGE, 0, nullptr), 3, 0}));
        KilnStationFillAnswer dirty{k, 3, 0};
        dirty.key.choices[2].reserved[1] = 1;
        CHECK(!fill_answer_ok(dirty));

        // the asks: each refused key once, oldest first, at most the cap; an answer takes its key off
        std::vector<KilnStationFillKey> asks;
        CHECK(note_fill_ask(&asks, k, 3) && !note_fill_ask(&asks, k, 3) && asks.size() == 1);
        CHECK(note_fill_ask(&asks, fill_key(KILN2, SHEETS, nullptr), 3) && note_fill_ask(&asks, fill_key(FORGE, OTHER, nullptr), 3));
        CHECK(note_fill_ask(&asks, fill_key(FORGE, SHEETS, sel2), 3) && asks.size() == 3 && same_fill(asks[0], fill_key(KILN2, SHEETS, nullptr)));
        CHECK(!note_fill_ask(&asks, k, 0) || asks.size() <= 3);
        drop_answered(&asks, {KilnStationFillAnswer{fill_key(FORGE, OTHER, nullptr), 0, 0}});
        CHECK(asks.size() == 2 && same_fill(asks[0], fill_key(KILN2, SHEETS, nullptr)) && same_fill(asks[1], fill_key(FORGE, SHEETS, sel2)));
    }

    // ---- the ingredient rows' "available" count on a Kiln client (GitHub #68, docs/re/stations.md §10.3) ----
    {
        constexpr uintptr_t ROW1 = 0x140bb19b8, ROW2 = 0x140bb280a, OTHER = 0x140c7eeb4;
        // the row's two calls: the greater of the game's count and the snapshot's
        CHECK(stock_ui_count(0, ROW1, ROW1, ROW2, true, 20) == 20);
        CHECK(stock_ui_count(3, ROW2, ROW1, ROW2, true, 20) == 20);
        CHECK(stock_ui_count(25, ROW1, ROW1, ROW2, true, 20) == 25);  // never below the game's own count
        // any other caller (the build menu, a hint, ...), an item the snapshot does not name, no row symbols: the game's
        CHECK(stock_ui_count(0, OTHER, ROW1, ROW2, true, 20) == 0);
        CHECK(stock_ui_count(4, ROW1, ROW1, ROW2, false, 0) == 4);
        CHECK(stock_ui_count(4, 0, 0, 0, true, 20) == 4);
        CHECK(stock_ui_count(4, ROW1, 0, 0, true, 20) == 4);
        CHECK(stock_ui_count(0, ROW1, ROW1, ROW2, true, 0x1'0000'0005ull) == 0xffffffffu);
    }

    {  // GitHub #132: the crafting menu counts at its own two sites (its list and its row's options), not the factory row's
        constexpr uintptr_t ROW1 = 0x140bb19b8, ROW2 = 0x140bb280a, LIST = 0x140bf5095, OPTION = 0x140bf352c,
                            OTHER = 0x140c7eeb4;
        const AvailSites s{ROW1, ROW2, LIST, OPTION};
        CHECK(stock_ui_site(ROW1, s) == kAvailFactoryRow && stock_ui_site(ROW2, s) == kAvailFactoryRow);
        CHECK(stock_ui_site(LIST, s) == kAvailMenuList);
        CHECK(stock_ui_site(OPTION, s) == kAvailMenuOption);
        CHECK(stock_ui_site(OTHER, s) == kAvailOther);
        CHECK(stock_ui_site(0, s) == kAvailOther);
        CHECK(stock_ui_site(LIST, AvailSites{ROW1, ROW2, 0, 0}) == kAvailOther);  // a site that did not resolve never matches
        CHECK(stock_ui_site(0, AvailSites{}) == kAvailOther);
        // the list adds the game's count of the view's fixed slots after this call; the snapshot already holds those
        // stacks, so the list's call answers max(game, have - fixed) and the row shows max(game + fixed, have)
        CHECK(stock_ui_site_count(5, kAvailMenuList, true, 20, 3) == 17);   // the row: 17 + 3 = the snapshot's 20
        CHECK(stock_ui_site_count(18, kAvailMenuList, true, 20, 3) == 18);  // the game's 18 + 3 is more: the game's
        CHECK(stock_ui_site_count(0, kAvailMenuList, true, 2, 3) == 0);     // a snapshot below the fixed slots: the game's
        CHECK(stock_ui_site_count(0, kAvailMenuList, true, 20, 0) == 20);
        CHECK(stock_ui_site_count(5, kAvailMenuList, false, 20, 0) == 5);   // an item the snapshot does not name
        CHECK(stock_ui_needs_fixed(kAvailMenuList, true, 20, 5));
        CHECK(!stock_ui_needs_fixed(kAvailMenuList, true, 5, 5));            // the game's count wins anyway
        CHECK(!stock_ui_needs_fixed(kAvailMenuList, false, 20, 5));
        CHECK(!stock_ui_needs_fixed(kAvailMenuOption, true, 20, 5));         // only the list adds the fixed slots
        CHECK(!stock_ui_needs_fixed(kAvailFactoryRow, true, 20, 5));
        // the row's options and the factory rows: the greater of the game's count and the snapshot's
        CHECK(stock_ui_site_count(5, kAvailMenuOption, true, 20, 0) == 20);
        CHECK(stock_ui_site_count(5, kAvailFactoryRow, true, 20, 0) == 20);
        CHECK(stock_ui_site_count(25, kAvailMenuOption, true, 20, 0) == 25);
        CHECK(stock_ui_site_count(5, kAvailMenuOption, false, 20, 0) == 5);
        CHECK(stock_ui_site_count(5, kAvailOther, true, 20, 0) == 5);
        CHECK(stock_ui_site_count(0, kAvailMenuOption, true, 0x1'0000'0005ull, 0) == 0xffffffffu);
        CHECK(stock_ui_site_count(0, kAvailMenuList, true, 0x1'0000'0005ull, 4) == 0xffffffffu);
    }

    // ---- GitHub #132 follow-up: one kiln.log line per player craft that crafting sources could matter to ----
    {
        CHECK(craft_trace_wanted(1, 3000, true));
        CHECK(!craft_trace_wanted(1, 3000, false));     // no linked source and no opted-in station: the game's craft, no line
        CHECK(!craft_trace_wanted(3000, 3000, true));   // a factory's own cycle (operator == station)
        CHECK(!craft_trace_wanted(0, 3000, true));
        CHECK(!craft_trace_wanted(1024, 3000, true));   // not a player
        CraftTrace t;
        std::string s = craft_trace_text(t, 1, 0x77, 3000, 4);
        CHECK(s.find("player 1's craft of recipe 0x00000077 at station 3000") != std::string::npos);
        CHECK(s.find("refused without reaching the recipe payment") != std::string::npos && s.find("game code 4") != std::string::npos);
        CHECK(s.find("no crafting source was asked") != std::string::npos);
        t.stage = kTracePaid;
        t.from = 12;
        s = craft_trace_text(t, 1, 0x77, 3000, 0);
        CHECK(s.find("paid 12 item(s) from linked sources or opted-in station outputs") != std::string::npos && s.find("made") != std::string::npos);
        t = CraftTrace{};
        t.stage = kTraceShort;
        t.from = 5;
        s = craft_trace_text(t, 1, 0x77, 0, 4);
        CHECK(s.find("short by 5") != std::string::npos && s.find("by hand") != std::string::npos);
        t.stage = kTraceDropped;
        CHECK(craft_trace_text(t, 1, 0x77, 3000, 4).find("earlier login") != std::string::npos);
        t.stage = kTraceTxRefused;
        CHECK(craft_trace_text(t, 1, 0x77, 3000, 4).find("transaction") != std::string::npos);
        t.stage = kTraceOtherPayer;
        CHECK(craft_trace_text(t, 1, 0x77, 3000, 0).find("another payer") != std::string::npos);
        t.stage = kTraceLeft;
        t.why = "the cost lines could not be read";
        CHECK(craft_trace_text(t, 1, 0x77, 3000, 4).find("left to the game: the cost lines could not be read") != std::string::npos);
        t.stage = kTraceCovered;
        t.why = nullptr;
        CHECK(craft_trace_text(t, 1, 0x77, 3000, 0).find("covered") != std::string::npos);
        // GitHub #132: what the game's own sources were counted to hold and what they paid, and the game's refusals
        t = CraftTrace{};
        t.stage = kTracePaid;
        t.from = 5;
        t.counted = true;
        t.game_counted = 3, t.game_paid = 0, t.payer_held = 3, t.payer_paid = 0, t.pay_refusals = 1, t.last_refusal = 0x0b;
        s = craft_trace_text(t, 1, 0x3ef55875, 4417, 0);
        CHECK(s.find("paid 5 item(s) from linked sources or opted-in station outputs (the bag and the base's Magic chests: counted 3, "
                     "paid 0; the payer's bag: held 3, paid 0; the game refused 1 take(s), last error 0x0b); made") != std::string::npos);
        t.stage = kTraceCovered;
        t.pay_refusals = 0;
        CHECK(craft_trace_text(t, 1, 0x77, 3000, 0).find("own payment (the bag and the base's Magic chests: counted 3; the payer's bag: held 3)") !=
              std::string::npos);
        t.stage = kTraceRollback;
        t.why = "the bag and the Magic chests paid less than they were counted to hold";
        CHECK(craft_trace_text(t, 1, 0x77, 3000, 4).find("paid less than they were counted to hold: the craft was rolled back") != std::string::npos);
        t = CraftTrace{};
        t.stage = kTraceCovered;
        CHECK(craft_trace_text(t, 1, 0x77, 3000, 0).find("(the bag") == std::string::npos);  // not counted: nothing added
        t.stage = 200;  // never a stage: still one bounded line
        CHECK(!craft_trace_text(t, 1, 0x77, 3000, 0).empty());
    }

    // ---- GitHub #132 (0.2.0-dev.31.16): the per-ingredient transfer as a one-input fill of count 1 ----
    {
        // 7 in the station; the bag and the Magic chests hold 3, the BeforeStationOutputs links 10, the After links 4
        const InputNow x = item_fill_input(0x2b0cf1d5, 7, 3, 10, 4);
        CHECK(x.item == 0x2b0cf1d5 && x.count == 1 && x.have == 7 && x.stock == 17 && x.linked == 10 && x.linked_after == 4);
        ItemFill f = item_fill_plan(KILN_STATION_FILL_ITEM, 2, x);  // the bag and the chests cover it: no link pays
        CHECK(f.math.status == KILN_STATION_DONE && f.math.planned == 2 && f.game == 2 && f.linked == 0 && f.linked_after == 0);
        f = item_fill_plan(KILN_STATION_FILL_ITEM | KILN_STATION_FILL_LINKED, 12, x);  // 3 game, 9 from the Before links
        CHECK(f.math.status == KILN_STATION_DONE && f.math.wanted == 12 && f.game == 3 && f.linked == 9 && f.linked_after == 0);
        f = item_fill_plan(KILN_STATION_FILL_ITEM | KILN_STATION_FILL_LINKED, 16, x);  // the After links pay last
        CHECK(f.math.status == KILN_STATION_DONE && f.game == 3 && f.linked == 10 && f.linked_after == 3);
        CHECK(f.game + f.linked + f.linked_after == 16);
        f = item_fill_plan(KILN_STATION_FILL_ITEM | KILN_STATION_FILL_LINKED, 18, x);  // one short: all or nothing
        CHECK(f.math.status == KILN_STATION_NOT_ENOUGH && f.math.planned == 0 && f.math.limit.item == 0x2b0cf1d5 &&
              f.math.limit.why == KILN_STATION_LIMIT_STOCK && f.game == 0 && f.linked == 0 && f.linked_after == 0);
        CHECK(f.math.affordable == 17);
        // never a partial move, whatever the request's ADD / EXACT bits
        f = item_fill_plan(KILN_STATION_FILL_ITEM | KILN_STATION_FILL_LINKED | KILN_STATION_FILL_ADD, 18, x);
        CHECK(f.math.status == KILN_STATION_NOT_ENOUGH);
        // the loaded count is the station's pile (count 1): never "already loaded"
        const InputNow full = item_fill_input(0x2b0cf1d5, 900, 3, 0, 0);
        f = item_fill_plan(KILN_STATION_FILL_ITEM, 3, full);
        CHECK(f.math.status == KILN_STATION_DONE && f.math.loaded == 900 && f.game == 3);
        // each layer as the game's own call takes it: links first (player 0), then the game's call (bag, then chests)
        uint64_t before = 10, after = 4, game = 3, station = 7;
        f = item_fill_plan(KILN_STATION_FILL_ITEM | KILN_STATION_FILL_LINKED, 16, x);
        CHECK(game_item_fill(&before, f.linked, UINT64_MAX, &station) == 0 && game_item_fill(&after, f.linked_after, UINT64_MAX, &station) == 0 &&
              game_item_fill(&game, f.game, UINT64_MAX, &station) == 0 && station == 7 + 16 && before == 0 && after == 1 && game == 0);
        // a bag or chest that pays less than counted: the game's call refuses (4) and the whole transaction rolls back
        uint64_t less = 2;
        CHECK(game_item_fill(&less, f.game, UINT64_MAX, &station) == 4);
        // the game's u16: an item fill never asks more than 0xFFFF
        const InputNow big = item_fill_input(0x2b0cf1d5, 0, 70000, 0, 0);
        f = item_fill_plan(KILN_STATION_FILL_ITEM, 0xFFFF, big);
        CHECK(f.math.status == KILN_STATION_DONE && f.game == 0xFFFF);
        // without FILL_LINKED the links' counts never pay (a caller that did not ask)
        f = item_fill_plan(KILN_STATION_FILL_ITEM, 12, x);
        CHECK(f.math.status == KILN_STATION_NOT_ENOUGH && f.linked == 0 && f.game == 0);
        // the line for a decision: what paid, or why nothing moved
        CHECK(item_fill_text(f, 0x2b0cf1d5, 12, 3300, 5).find("player 5's transfer of 12 x item 0x2b0cf1d5 into station 3300") != std::string::npos);
        CHECK(item_fill_text(f, 0x2b0cf1d5, 12, 3300, 5).find("short") != std::string::npos);
        f = item_fill_plan(KILN_STATION_FILL_ITEM | KILN_STATION_FILL_LINKED, 16, x);
        CHECK(item_fill_text(f, 0x2b0cf1d5, 16, 3300, 5).find("3 from the bag and the base's Magic chests, 13 from linked sources") !=
              std::string::npos);
    }

    std::printf("stations: %d checks, %d failures\n", checks, failed);
    return failed ? 1 : 0;
}
