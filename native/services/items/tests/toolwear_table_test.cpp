// SPDX-License-Identifier: MIT
// kiln.toolwear's table (toolwear_table.cpp) with no game: the owners' rule layers, what reaches the hooks' rule table,
// the generation, the refusals, the change records and the status. The hooks' side (set_rule, drain, status) and the
// item catalog are made up here; the table code is the product's.
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <map>
#include <vector>

#include "../toolwear.h"
#include "kiln_api.h"
#include "services/kiln_items.h"
#include "services/kiln_toolwear.h"

static int g_fail = 0;
#define CHECK(c) do { if (!(c)) { std::printf("FAIL %s:%d %s\n", __FILE__, __LINE__, #c); g_fail++; } } while (0)

// ---- the made-up other side ------------------------------------------------------------------------------------------
namespace fake {
std::map<uint32_t, int32_t> rules;      // what the hooks would read
bool hooks_on = true, tick = true, catalog = true, full = false;
uintptr_t world = 0x1000;
std::vector<kitems::toolwear::Change> queue;
const KilnToolWearApi* table = nullptr;
constexpr uint32_t kPickaxe = 0x2525bb44u, kAxe = 0x1111aaaau, kTorch = 0x70707070u, kSword = 0x5555u, kRake = 0x6666u;
}  // namespace fake

namespace kitems {
KilnResult items_item_info(uint32_t id, KilnItemInfo* out) {
    if (!fake::catalog) return KILN_ERR_UNAVAILABLE;
    static const char* names[] = {"Pickaxe", "Axe", "Torch", "Sword", "Rake"};
    KilnItemInfo i{};
    i.size = sizeof i;
    i.id = id;
    int n;
    switch (id) {
        case fake::kPickaxe: i.category = 6, i.item_flags = 0x82, i.gear = 1, n = 0; break;  // the game's catalog: a build tool with an item entity
        case fake::kAxe: i.category = 3, i.item_flags = 0x2, i.gear = 1, n = 1; break;
        case fake::kTorch: i.category = 3, i.item_flags = 0, i.gear = 0, n = 2; break;    // a tool without an item entity
        case fake::kSword: i.category = 2, i.item_flags = 0x2, i.gear = 1, n = 3; break;  // an item entity, not a tool
        case fake::kRake: i.category = 6, i.item_flags = 0, i.gear = 0, n = 4; break;     // a build tool
        default: return KILN_ERR_NOT_FOUND;
    }
    i.name = KilnStr{names[n], std::strlen(names[n])};
    *out = i;
    return KILN_OK;
}
}  // namespace kitems

namespace kitems::toolwear {
bool set_rule(uint32_t item, int32_t cost) {
    if (cost < 0) {
        fake::rules.erase(item);
        return true;
    }
    if (fake::full && !fake::rules.count(item)) return false;
    fake::rules[item] = cost;
    return true;
}
int32_t rule(uint32_t item) { return fake::rules.count(item) ? fake::rules[item] : -1; }
size_t drain(Change* out, size_t max) {
    size_t n = 0;
    while (n < max && !fake::queue.empty()) {
        out[n++] = fake::queue.front();
        fake::queue.erase(fake::queue.begin());
    }
    return n;
}
Status status() {
    Status s;
    s.on = fake::hooks_on;
    s.dropped = 3;
    s.threads = 2;
    s.hit_seen = 11;
    s.hit_applied = 5;
    s.spawn_seen = 9;
    s.spawn_applied = 4;
    if (!s.on) std::snprintf(s.why, sizeof s.why, "this game build has no approved description");
    return s;
}
}  // namespace kitems::toolwear

static bool KILN_CALL on_tick_thread() { return fake::tick; }
static uintptr_t KILN_CALL server_world() { return fake::world; }
static KilnResult KILN_CALL session(KilnSessionInfo* out) {
    out->sessions[KILN_WORLD_SERVER] = 4;
    return KILN_OK;
}
static const void* KILN_CALL service(KilnStr, uint32_t) { return nullptr; }
static KilnResult KILN_CALL register_service(KilnOwner, KilnStr name, const void* t) {
    if (name.len == std::strlen(KILN_TOOLWEAR_SERVICE) && !std::memcmp(name.ptr, KILN_TOOLWEAR_SERVICE, name.len))
        fake::table = static_cast<const KilnToolWearApi*>(t);
    return KILN_OK;
}

static KilnStr S(const char* s) { return KilnStr{s, std::strlen(s)}; }

static KilnToolWearState state(uint32_t item) {
    KilnToolWearState s{};
    s.size = sizeof s;
    CHECK(fake::table->status(item, &s) == KILN_OK);
    return s;
}

int main() {
    static KilnApi api{};
    api.size = sizeof api;
    api.on_tick_thread = &on_tick_thread;
    api.server_world = &server_world;
    api.session = &session;
    api.service = &service;
    api.register_service = &register_service;
    CHECK(kitems::toolwear::publish(&api, 1) && fake::table);
    const KilnToolWearApi* t = fake::table;
    CHECK(t->header.size == sizeof(KilnToolWearApi) && t->header.version == KILN_TOOLWEAR_VERSION);
    const KilnOwner A = 10, B = 20;
    KilnHandle a = 0, b = 0, a2 = 0, x = 0;

    // a supported tool without a rule
    auto s = state(fake::kPickaxe);
    CHECK(s.ok == 1 && s.has_rule == 0 && s.pending == 0 && s.generation == 0 && s.dropped_events == 3 && s.owner_mod_id[0] == 0 && s.reason[0] == 0);

    // one owner's rule: in effect, in the hooks' table, one generation
    CHECK(t->define(A, S("mod-a"), fake::kPickaxe, 7, &a) == KILN_OK && a != 0 && fake::rules[fake::kPickaxe] == 7);
    s = state(fake::kPickaxe);
    CHECK(s.has_rule == 1 && s.per_debit == 7 && s.generation == 1 && !std::strcmp(s.owner_mod_id, "mod-a"));
    // the same owner, the same cost: the same handle, nothing changes
    CHECK(t->define(A, S("mod-a"), fake::kPickaxe, 7, &a2) == KILN_OK && a2 == a && state(fake::kPickaxe).generation == 1);
    // the same owner, another cost: its rule replaced in place
    CHECK(t->define(A, S("mod-a"), fake::kPickaxe, 9, &a2) == KILN_OK && a2 == a && fake::rules[fake::kPickaxe] == 9 && state(fake::kPickaxe).generation == 2);
    // another owner: the latest defined is in effect
    CHECK(t->define(B, S("mod-b"), fake::kPickaxe, 2, &b) == KILN_OK && b != a && fake::rules[fake::kPickaxe] == 2);
    s = state(fake::kPickaxe);
    CHECK(s.per_debit == 2 && s.generation == 3 && !std::strcmp(s.owner_mod_id, "mod-b"));
    // only the owner removes its rule
    CHECK(t->remove(A, b) == KILN_TOOLWEAR_NOT_OWNER && fake::rules[fake::kPickaxe] == 2 && state(fake::kPickaxe).generation == 3);
    CHECK(t->remove(B, 9999) == KILN_ERR_NOT_FOUND && t->remove(B, 0) == KILN_ERR_INVALID_ARGUMENT);
    // removing the winner reveals the rule before it
    CHECK(t->remove(B, b) == KILN_OK && fake::rules[fake::kPickaxe] == 9);
    s = state(fake::kPickaxe);
    CHECK(s.per_debit == 9 && s.generation == 4 && !std::strcmp(s.owner_mod_id, "mod-a"));
    // an equal cost under another owner is still a change of whose rule it is
    CHECK(t->define(B, S("mod-b"), fake::kPickaxe, 9, &b) == KILN_OK && fake::rules[fake::kPickaxe] == 9);
    s = state(fake::kPickaxe);
    CHECK(s.generation == 5 && !std::strcmp(s.owner_mod_id, "mod-b"));
    // removing a rule that is not in effect changes nothing
    CHECK(t->remove(A, a) == KILN_OK && fake::rules[fake::kPickaxe] == 9 && state(fake::kPickaxe).generation == 5);
    // the last rule gone: the game's own debit
    CHECK(t->remove(B, b) == KILN_OK && !fake::rules.count(fake::kPickaxe));
    s = state(fake::kPickaxe);
    CHECK(s.has_rule == 0 && s.generation == 6 && s.owner_mod_id[0] == 0);
    // cost 0 is a rule (the tool does not wear), not "no rule"
    CHECK(t->define(A, S("mod-a"), fake::kAxe, 0, &a) == KILN_OK && fake::rules.count(fake::kAxe) && fake::rules[fake::kAxe] == 0 && state(fake::kAxe).has_rule == 1);

    // refusals: nothing reaches the hooks' table
    const auto before = fake::rules;
    CHECK(t->define(A, S("mod-a"), fake::kTorch, 7, &x) == KILN_TOOLWEAR_UNSUPPORTED_TOOL && x == 0);
    CHECK(t->define(A, S("mod-a"), fake::kSword, 7, &x) == KILN_TOOLWEAR_UNSUPPORTED_TOOL);
    CHECK(t->define(A, S("mod-a"), fake::kRake, 7, &x) == KILN_TOOLWEAR_UNSUPPORTED_TOOL);
    CHECK(t->define(A, S("mod-a"), 0x999, 7, &x) == KILN_ERR_NOT_FOUND);
    CHECK(t->define(A, S("mod-a"), fake::kPickaxe, 32768, &x) == KILN_ERR_INVALID_ARGUMENT);
    CHECK(t->define(A, S("mod-a"), 0, 7, &x) == KILN_ERR_INVALID_ARGUMENT && t->define(A, S(""), fake::kPickaxe, 7, &x) == KILN_ERR_INVALID_ARGUMENT);
    CHECK(t->define(A, S("mod-a"), fake::kPickaxe, 7, nullptr) == KILN_ERR_INVALID_ARGUMENT);
    char long_id[200];
    std::memset(long_id, 'x', sizeof long_id);
    CHECK(t->define(A, KilnStr{long_id, 128}, fake::kPickaxe, 7, &x) == KILN_ERR_INVALID_ARGUMENT);
    CHECK(t->define(A, S("m\xc3\xb6""d"), fake::kPickaxe, 7, &x) == KILN_ERR_INVALID_ARGUMENT);
    fake::world = 0;
    CHECK(t->define(A, S("mod-a"), fake::kPickaxe, 7, &x) == KILN_TOOLWEAR_NO_AUTHORITATIVE_WORLD);
    fake::world = 0x1000;
    fake::catalog = false;
    CHECK(t->define(A, S("mod-a"), fake::kPickaxe, 7, &x) == KILN_TOOLWEAR_PENDING && state(fake::kPickaxe).pending == 1 && state(fake::kPickaxe).ok == 0);
    fake::catalog = true;
    fake::tick = false;
    CHECK(t->define(A, S("mod-a"), fake::kPickaxe, 7, &x) == KILN_ERR_WRONG_THREAD && t->remove(A, a) == KILN_ERR_WRONG_THREAD);
    uint32_t n = 0;
    KilnToolWearChange rec[4];
    CHECK(t->changes(rec, 4, sizeof rec[0], &n) == KILN_ERR_WRONG_THREAD);
    CHECK(state(fake::kAxe).has_rule == 1);  // status is any thread
    fake::tick = true;
    fake::hooks_on = false;
    CHECK(t->define(A, S("mod-a"), fake::kPickaxe, 7, &x) == KILN_ERR_UNAVAILABLE);
    s = state(fake::kPickaxe);
    CHECK(s.ok == 0 && std::strstr(s.reason, "approved") != nullptr);
    fake::hooks_on = true;
    fake::full = true;
    const uint64_t gen = state(0).generation;
    CHECK(t->define(A, S("mod-a"), fake::kPickaxe, 7, &x) == KILN_TOOLWEAR_TOO_MANY_RULES && x == 0 && state(fake::kPickaxe).has_rule == 0 && state(0).generation == gen);
    fake::full = false;
    CHECK(fake::rules == before);
    s = state(fake::kTorch);
    CHECK(s.ok == 0 && std::strstr(s.reason, "UnsupportedTool") && std::strstr(s.reason, "Torch"));
    // a reader with a shorter state gets its part, a too short one is refused
    KilnToolWearState small{};
    small.size = static_cast<uint32_t>(offsetof(KilnToolWearState, owner_mod_id));
    CHECK(fake::table->status(fake::kAxe, &small) == KILN_OK && small.has_rule == 1 && small.owner_mod_id[0] == 0);
    small.size = 8;
    CHECK(fake::table->status(fake::kAxe, &small) == KILN_ERR_INVALID_ARGUMENT);
    // the appended lab counters: a full reader gets them, a reader of the first form (360 bytes) gets nothing past it
    KilnToolWearState full{};
    full.size = sizeof full;
    CHECK(fake::table->status(0, &full) == KILN_OK && full.size == 400 && full.hook_threads == 2 && full.hit_seen == 11 && full.hit_applied == 5 &&
          full.spawn_seen == 9 && full.spawn_applied == 4);
    KilnToolWearState first{};
    std::memset(&first, 0xAB, sizeof first);
    first.size = 360;
    CHECK(fake::table->status(0, &first) == KILN_OK && first.size == 360 && first.hook_threads == 0xABABABABu && first.spawn_applied == 0xABABABABABABABABull);

    // change records: values copied, the world's session, the stride honoured
    kitems::toolwear::Change c{};
    c.source = kitems::toolwear::kSourceProjectileSpawn;
    c.step = 77;
    c.actor = 2022;
    c.item_entity = 2032;
    c.item_id = fake::kPickaxe;
    c.stamp = 500;
    c.before = 180;
    c.after = 173;
    c.maximum = 180;
    c.original_delta = -1;
    c.requested_delta = -7;
    c.sequence_id = 0xabcd;
    c.call_index = 3;
    c.trigger_time = 99;
    c.trigger_type = 1;
    fake::queue = {c, c, c};
    fake::queue[1].before = 173, fake::queue[1].after = 166;
    fake::queue[2].before = 166, fake::queue[2].after = 159;
    uint8_t wide[3 * 96] = {};
    CHECK(t->changes(reinterpret_cast<KilnToolWearChange*>(wide), 2, 96, &n) == KILN_OK && n == 2);
    KilnToolWearChange r0, r1;
    std::memcpy(&r0, wide, sizeof r0);
    std::memcpy(&r1, wide + 96, sizeof r1);
    CHECK(r0.size == sizeof r0 && r0.source == KILN_TOOLWEAR_PROJECTILE_SPAWN && r0.world_session == 4 && r0.step == 77 && r0.actor == 2022 &&
          r0.item_pide == 2032 && r0.item_id == fake::kPickaxe && r0.instance_stamp == 500 && r0.before == 180 && r0.after == 173 && r0.maximum == 180 &&
          r0.original_delta == -1 && r0.requested_delta == -7 && r0.sequence_id == 0xabcd && r0.call_index == 3 && r0.trigger_time == 99 && r0.trigger_type == 1);
    CHECK(r1.before == 173 && r1.after == 166);
    CHECK(t->changes(rec, 4, sizeof rec[0], &n) == KILN_OK && n == 1 && rec[0].before == 166 && rec[0].after == 159);  // the third was kept for the next call
    CHECK(t->changes(rec, 4, sizeof rec[0], &n) == KILN_OK && n == 0);
    // an older reader's shorter record: its part only, nothing written past its stride
    fake::queue = {c};
    uint8_t narrow[64];
    std::memset(narrow, 0xEE, sizeof narrow);
    const uint32_t short_stride = static_cast<uint32_t>(offsetof(KilnToolWearChange, sequence_id));
    CHECK(t->changes(reinterpret_cast<KilnToolWearChange*>(narrow), 1, short_stride, &n) == KILN_OK && n == 1);
    uint32_t wrote = 0;
    std::memcpy(&wrote, narrow, 4);
    CHECK(wrote == short_stride && narrow[short_stride] == 0xEE);
    CHECK(t->changes(rec, 1, 8, &n) == KILN_ERR_INVALID_ARGUMENT && t->changes(nullptr, 1, sizeof rec[0], &n) == KILN_ERR_INVALID_ARGUMENT &&
          t->changes(rec, 1, sizeof rec[0], nullptr) == KILN_ERR_INVALID_ARGUMENT);

    std::printf("items-toolwear-table-test: %s\n", g_fail ? "FAILED" : "ok");
    return g_fail ? 1 : 0;
}
