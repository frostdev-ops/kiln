// SPDX-License-Identifier: MIT
// Reuse the real store/live backend fixture; this executable adds transaction/entry-point cases, not another mock backend.
#define main registry_live_baseline_main
#include "registry_live_test.cpp"
#undef main
#include "services/kiln_registry_live.h"
#include <chrono>
#include "../registry/stage.h"

namespace {
std::string doc(const std::string& mod, const std::string& edits) {
    return "{\"ir\":1,\"mod\":\"" + mod + "\",\"stage\":\"alter\",\"edits\":" + edits + "}";
}
uint64_t change(KilnOwner owner, const std::string& mod, const std::string& edits, uint32_t point = 4) {
    uint64_t h = 0; RegistryLiveReply reply;
    check(registry_live_begin(owner, mod, point, &h) == KILN_OK, "begin");
    check(registry_live_submit(owner, h, doc(mod, edits), &reply, nullptr) == KILN_OK, "submit");
    return h;
}
const std::string wood = R"("registry":"items","select":"game:material_t1_wood_logs","field":"maxStack",)";
const std::string stone = R"("registry":"items","select":"game:material_stone","field":"maxStack",)";
const std::string sword = R"("registry":"items","select":"game:sword_t2","field":"maxStack",)";
void fresh_live() { world(); g_files.clear(); stage({}); registry_live_world_begin(); apply_registry_files({}); }
bool report_names(const std::string& mod, const std::string& needle) {
    return std::any_of(g_last_report.refused.begin(), g_last_report.refused.end(), [&](const registry::Refusal& r) {
        return r.mod == mod && r.reason.find(needle) != std::string::npos;
    });
}

// review4 NM1 (R1): the committing candidate's own write fails; other owners' retained changes stay applied and retained.
void candidate_write_failure_keeps_other_owners() {
    fresh_live(); RegistryLiveReply reply;
    const auto a = change(71, "a", "[{" + wood + R"("op":50}])");
    registry_live_commit(71, a, &reply);
    const auto b = change(72, "b", "[{" + stone + R"("op":70}])");
    registry_live_commit(72, b, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && at<uint32_t>(WOOD_AT) == 50 && at<uint32_t>(STONE_AT) == 70, "a and b applied");
    const auto c = change(73, "c", "[{" + sword + R"("op":9}])");
    g_write_limits = {0}; // only c's slot is dirty in this flush, and its write fails
    registry_live_commit(73, c, &reply);
    check(reply.outcome == RegistryLiveOutcome::Refused && reply.accepted == 0, "the candidate's own failed write refuses only the candidate");
    check(reply.reason.find("0x") == std::string::npos && reply.reason.find("items[game:sword_t2].maxStack") != std::string::npos,
          "the candidate's reason names the Kiln place, never a process address");
    check(registry_live_status().active == 2 && at<uint32_t>(WOOD_AT) == 50 && at<uint32_t>(STONE_AT) == 70 && at<uint32_t>(SWORD_AT) == 0,
          "other owners' retained changes survive the candidate's own failed write");
    registry_live_publish();
    check(!report_names("a", "not applied") && !report_names("b", "not applied") && report_names("c", "not committed"),
          "only the candidate is reported refused");
    check(registry_live_revert(71, a, &reply) == KILN_OK && registry_live_revert(72, b, &reply) == KILN_OK,
          "the retained handles stay valid");
}
// review4 NM1 (R2): a pack reapplication's unrelated stage-edit write fails; a live change on another field survives.
void unrelated_application_write_failure_keeps_live() {
    world();
    g_files = {{"p/content/p.json", R"({"registry":{"alter":[{"registry":"items","select":"game:material_stone","field":"maxStack","op":70}]}})"}};
    const auto files = files_of({{"p", "p/content/p.json"}});
    stage(files); registry_live_world_begin(); apply_registry_files(files);
    check(at<uint32_t>(STONE_AT) == 70, "pack applied");
    RegistryLiveReply reply;
    const auto a = change(71, "a", "[{" + wood + R"("op":50}])", 1);
    registry_live_commit(71, a, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && at<uint32_t>(WOOD_AT) == 50, "live a applied");
    g_files["p/content/p.json"] = R"({"registry":{"alter":[{"registry":"items","select":"game:material_stone","field":"maxStack","op":80}]}})";
    g_write_limits = {0}; // the pack's stone slot is the only write of this flush, and it fails
    apply_registry_files(files);
    check(registry_live_status().active == 1 && at<uint32_t>(WOOD_AT) == 50,
          "an unrelated pack slot's failed write does not remove another mod's live change");
    check(!report_names("a", "not applied") && report_names("p", "items[game:material_stone].maxStack: the game memory write failed"),
          "the failed write is reported for the pack edit that owns the slot, not for the live change");
}
// review4 NM1: rows of retained changes rejected during a commit survive the commit's own rollback pass.
void rejected_rows_survive_the_rollback_pass() {
    fresh_live(); RegistryLiveReply reply;
    const auto a = change(71, "a", R"([{"registry":"items","select":"game:sword_t2","field":"manaCost","op":20}])");
    registry_live_commit(71, a, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied, "a applied");
    reg(KILN_CONTENT_ITEMS).by_id.erase(SWORD); // a no longer binds in this world
    const auto c = change(73, "c", "[{" + stone + R"("op":70}])");
    g_write_limits = {0}; // c's write fails
    registry_live_commit(73, c, &reply);
    check(reply.outcome == RegistryLiveOutcome::Refused && at<uint32_t>(STONE_AT) == 50, "the candidate is refused and rolled back");
    registry_live_publish();
    check(report_names("a", "the entry is not in this world") && report_names("c", "not committed"),
          "the rejected retained change's row reaches registry.json together with the candidate's");
}
// review4 NS2 (R3): a process (MOD_LOAD) change rejected "in this world" is tried again in every later world.
void rejected_process_change_returns_next_world() {
    fresh_live(); RegistryLiveReply reply;
    const auto h = change(71, "a", "[{" + wood + R"("op":75},{"registry":"items","select":"game:sword_t2","field":"manaCost","op":20}])", 1);
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && at<uint32_t>(WOOD_AT) == 75, "world 1 applies the process change");
    registry_live_world_stop(); std::string error; store_reset(true, &error); world();
    g_write_limits = {0}; // world 2 cannot write the change's slots
    registry_live_world_begin(); apply_registry_files({});
    check(at<uint32_t>(WOOD_AT) == 10 && registry_live_status().active == 0, "world 2 rejects it whole");
    check(reported("not applied in this world"), "world 2 reports the rejection by change");
    registry_live_world_stop(); store_reset(true, &error); world(); // world 3 writes normally
    registry_live_world_begin(); apply_registry_files({});
    check(registry_live_status().active == 1 && at<uint32_t>(WOOD_AT) == 75,
          "a process change refused only 'in this world' applies again in a later world");
    check(registry_live_revert(71, h, &reply) == KILN_OK && at<uint32_t>(WOOD_AT) == 10, "and its handle still reverts it");
}
// review4 NS2 (R4): another writer changes ONE slot of a retained two-field change; an unrelated owner's commit keeps the
// change's untouched field (ruling 2026-11-22 5:22 PM: only the drifted slot stays with that writer for the world).
void foreign_slot_keeps_the_rest_of_a_retained_change() {
    fresh_live(); RegistryLiveReply reply;
    const auto a = change(71, "a", "[{" + wood + R"("op":50},{)" + stone + R"("op":70}])", 1);
    registry_live_commit(71, a, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && at<uint32_t>(WOOD_AT) == 50 && at<uint32_t>(STONE_AT) == 70, "a applied");
    mem<uint32_t>(WOOD_AT, 33); // another writer takes the wood slot
    const auto b = change(72, "b", "[{" + sword + R"("op":9}])");
    registry_live_commit(72, b, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied, "the unrelated commit applies");
    check(at<uint32_t>(WOOD_AT) == 33, "the foreign wood bytes are preserved");
    check(at<uint32_t>(STONE_AT) == 70 && registry_live_status().active == 2,
          "a's untouched stone edit stays; only the drifted slot is left to the other writer");
    apply_registry_files({}); // a later application in the same world (a pack reload) keeps it too
    check(at<uint32_t>(WOOD_AT) == 33 && at<uint32_t>(STONE_AT) == 70 && registry_live_status().active == 2,
          "a later application in the same world keeps the change's other field");
    check(reported("was written by another writer") && !reported("not applied in this world"),
          "the drifted slot is reported, the change is not rejected");
    registry_live_world_stop(); std::string error; store_reset(true, &error); world();
    registry_live_world_begin(); apply_registry_files({});
    check(at<uint32_t>(WOOD_AT) == 50 && at<uint32_t>(STONE_AT) == 70, "the next world applies the whole process change again");
}
// review4 NS5: a runtime pass reads the live value without the reading mod's own registry rows (stage edits and live
// changes); layers below (packs) and above (code) stay, and a slot another writer took reads that writer's bytes.
void pass_read_excludes_own_layers() {
    world();
    g_files = {{"a/content/a.json", R"({"registry":{"alter":[{"registry":"items","select":"game:material_t1_wood_logs","field":"maxStack","op":{"add":1}}]}})"}};
    const auto files = files_of({{"a", "a/content/a.json"}});
    stage(files); registry_live_world_begin(); apply_registry_files(files);
    put_pack(WOOD_AT, 20); std::string err; store_flush(&err);
    RegistryLiveReply reply;
    const auto a = change(71, "a", "[{" + wood + R"("op":{"mul":2}}])", 1); registry_live_commit(71, a, &reply);
    const auto b = change(72, "b", "[{" + wood + R"("op":{"add":5}}])", 1); registry_live_commit(72, b, &reply);
    check(at<uint32_t>(WOOD_AT) == 47, "pack 20, a's stage +1, a's x2, b's +5");
    std::string value;
    auto read = [&](const std::string& mod) {
        check(registry_live_read("items", "game:material_t1_wood_logs", "maxStack", &value, mod) == KILN_OK, "pass read " + mod);
        return std::stoi(value);
    };
    check(read("") == 47 && read("c") == 47, "a plain read and another mod's pass read see the live value");
    check(read("a") == 25, "a's pass read leaves out a's stage edit and a's live change, not the pack or b's change");
    check(read("b") == 42, "b's pass read leaves out only b's change");
    check(at<uint32_t>(WOOD_AT) == 47, "a pass read writes nothing");
    uint8_t bytes[4]; uint32_t code = 333; std::memcpy(bytes, &code, 4);
    store_put(CODE, next_code_seq(), KILN_CONTENT_ITEMS, WOOD, 0, -1, A(WOOD_AT), 4, bytes, "code"); store_flush(&err);
    check(read("a") == 333, "code above the registry still wins in a pass read");
    store_drop_owner(CODE); store_flush(&err);
    registry_live_world_stop();
    check(read("a") == 15 && read("b") == 22, "before a world, the composed value is read without the mod's rows (base 10)");
    registry_live_world_begin(); apply_registry_files(files);
    mem<uint32_t>(WOOD_AT, 777); apply_registry_files(files); // another writer took the slot
    check(read("a") == 777, "a slot another writer took reads that writer's bytes");
}
// review2 S2: a worldLoad field written into a running world by a later point is applied and named: the save was read.
void world_load_field_mid_world_is_named() {
    fresh_live(); RegistryLiveReply reply;
    const auto h = change(71, "a", "[{" + wood + R"("op":50}])", 4);
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && at<uint32_t>(WOOD_AT) == 50, "the worldLoad field is still written");
    check(reply.reason.find("items.maxStack applies at world load") != std::string::npos && reply.reason.find("next world load") != std::string::npos,
          "the result says it takes full effect at the next world load");
    registry_live_revert(71, h, &reply);
    const auto now = change(72, "b", R"([{"registry":"items","select":"game:sword_t2","field":"manaCost","op":12}])", 4);
    registry_live_commit(72, now, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && reply.reason.empty(), "a field that applies now is not flagged");
    registry_live_revert(72, now, &reply);
    const auto w = change(73, "c", "[{" + wood + R"("op":60}])", 2);
    registry_live_commit(73, w, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && reply.reason.empty(), "the WorldLoad point's own world-load field is not flagged");
    registry_live_revert(73, w, &reply);
}
// review2 S5: a drop-table change before the world has loaded the table is PendingResource, then applies on the table.
void drop_table_change_is_pending_resource() {
    fresh_live(); RegistryLiveReply reply;
    const auto h = change(71, "a", R"([{"registry":"drops","select":"game:dt_wolf","field":"entries[*].count.max","op":{"mul":2}}])");
    check(registry_live_commit(71, h, &reply) == KILN_OK && reply.outcome == RegistryLiveOutcome::PendingResource && reply.accepted == 1,
          "a change to a drop table the world has not loaded waits for it");
    check(at<uint32_t>(0x31018) == 3, "nothing is written before the table loads");
    add_drop_table();
    registry_live_table(g_regs[KILN_CONTENT_DROPS].entries[0]);
    std::string err; store_flush(&err);
    check(at<uint32_t>(0x31018) == 6 && at<uint32_t>(0x31058) == 6, "the table's stacks take the change once it loads");
    check(registry_live_revert(71, h, &reply) == KILN_OK && reply.outcome == RegistryLiveOutcome::Reverted && at<uint32_t>(0x31018) == 3,
          "revert restores the stacks");
}
// registry-proof (31.x): a drop table another change (or a GameStart item removal's disabled slots) waits for does not make an
// unrelated change pending: the outcome is the change's own (an item's maxStack written now is Applied, Ok in C#).
void unrelated_pending_table_keeps_a_change_applied() {
    fresh_live(); RegistryLiveReply reply;
    const auto pending = change(71, "a", R"([{"registry":"drops","select":"game:dt_wolf","field":"entries[*].count.max","op":{"mul":2}}])");
    check(registry_live_commit(71, pending, &reply) == KILN_OK && reply.outcome == RegistryLiveOutcome::PendingResource, "the drop-table change waits for its table");
    const auto h = change(72, "b", "[{" + wood + R"("op":75}])");
    check(registry_live_commit(72, h, &reply) == KILN_OK && reply.outcome == RegistryLiveOutcome::Applied && at<uint32_t>(WOOD_AT) == 75,
          "an item change written now is Applied while another change's table is pending");
    const auto mixed = change(73, "c", "[{" + stone + R"("op":70},{"registry":"drops","select":"game:dt_wolf","field":"entries[*].weight","op":{"mul":2}}])");
    check(registry_live_commit(73, mixed, &reply) == KILN_OK && reply.outcome == RegistryLiveOutcome::PendingResource,
          "a change with a field of its own on the pending table still waits for it");
    registry_live_revert(73, mixed, &reply); registry_live_revert(72, h, &reply); registry_live_revert(71, pending, &reply);
}
// review of registry-loot (dev.32): a drop table the world has not loaded yet reads the value it takes when it loads (its base
// and the writers' rows; a pass read leaves the reading mod's own rows out), never UNAVAILABLE. In the lab, Loot Rarity
// Leveler's WorldLoad pass reads every drop list to derive the chances it writes: the game loads drop tables as it needs
// them, so the first unloaded one threw and the whole pass (its lootRules and randomLoot edits too) was discarded.
void unloaded_drop_table_reads_the_value_it_loads_with() {
    fresh_live(); RegistryLiveReply reply; std::string value;
    auto read = [&](const std::string& field, const std::string& mod) {
        value.clear();
        const KilnResult rc = registry_live_read("drops", "game:dt_wolf", field, &value, mod);
        return rc == KILN_OK ? value : "rc " + std::to_string(static_cast<int>(rc));
    };
    check(read("entries[0].count.max", "") == "3", "a plain read of a drop table the world has not loaded gives its base: " + value);
    check(read("entries[*].chance", "a") == "[0.5,0.5]", "a pass read of it too: " + value);
    const auto h = change(71, "a", R"([{"registry":"drops","select":"game:dt_wolf","field":"entries[*].count.max","op":{"mul":2}}])");
    check(registry_live_commit(71, h, &reply) == KILN_OK && reply.outcome == RegistryLiveOutcome::PendingResource, "a's change waits for the table");
    check(read("entries[0].count.max", "") == "6", "a plain read includes the waiting change (what the table takes when it loads): " + value);
    check(read("entries[0].count.max", "a") == "3", "a's pass read leaves a's own waiting change out: " + value);
    check(read("entries[1].count.max", "b") == "6", "another mod's pass read sees a's change: " + value);
    check(at<uint32_t>(0x31018) == 3, "a read writes nothing");
    add_drop_table();
    registry_live_table(g_regs[KILN_CONTENT_DROPS].entries[0]);
    std::string err; store_flush(&err);
    check(at<uint32_t>(0x31018) == 6, "the table takes the change once it loads");
    check(read("entries[0].count.max", "") == "6" && read("entries[0].count.max", "a") == "3", "once loaded, the same values from its bytes: " + value);
    check(read("entries[0].count.max", "") == "6" && read("entries[0].count.max", "zzz") == "6", "and for any other reader");
    registry_live_revert(71, h, &reply);
    check(at<uint32_t>(0x31018) == 3, "revert restores the stacks");
    check(registry_live_read("drops", "game:dt_nobody", "entries[0].count.max", &value, "") == KILN_ERR_NOT_FOUND,
          "an id the game does not have is still NOT_FOUND");
}
// review2 S5: a formula transaction (power) writes its input, and its revert leaves another writer's bytes alone.
void formula_transaction_with_foreign_write() {
    fresh_live(); RegistryLiveReply reply;
    const float before = at<float>(SWORD_AT + 8);
    const auto h = change(71, "a", R"([{"registry":"items","select":"game:sword_t2","field":"power","op":{"mul":2}}])");
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && at<float>(SWORD_AT + 8) != before, "power x2 writes damageSetup.dmgMod through the formula");
    std::string value;
    check(registry_live_read("items", "game:sword_t2", "power", &value) == KILN_OK, "the formula reads back");
    mem<float>(SWORD_AT + 8, 7.0f); // another writer
    registry_live_revert(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Skipped && at<float>(SWORD_AT + 8) == 7.0f, "the formula's revert preserves the foreign bytes");
}
// review2 S5: a world-bound draft begun in one world cannot be committed into the next; a MOD_LOAD draft is no world's
// (review4 nit: begin no longer binds it to the world it was begun in).
void draft_cannot_cross_worlds() {
    fresh_live(); RegistryLiveReply reply;
    const auto h = change(71, "a", "[{" + wood + R"("op":50}])", 4);
    const auto process = change(72, "b", "[{" + wood + R"("op":60}])", 1);
    registry_live_world_stop();
    check(registry_live_commit(71, h, &reply) == KILN_ERR_NOT_FOUND, "the ended world's manual draft expired with it");
    check(registry_live_commit(72, process, &reply) == KILN_OK && reply.outcome == RegistryLiveOutcome::PendingWorld,
          "a MOD_LOAD draft begun in a world commits after it ended and waits for the next world");
    std::string error; store_reset(true, &error); world(); registry_live_world_begin(); apply_registry_files({});
    check(at<uint32_t>(WOOD_AT) == 60, "the next world applies it");
    registry_live_revert(72, process, &reply);
}
// review2 S5: a MOD_LOAD change reverted before any world is removed from the composition only.
void process_change_reverts_without_a_world() {
    world(); g_files.clear(); stage({}); RegistryLiveReply reply;
    const auto h = change(71, "a", "[{" + wood + R"("op":50}])", 1);
    check(registry_live_commit(71, h, &reply) == KILN_OK && reply.outcome == RegistryLiveOutcome::PendingWorld, "pending before a world");
    std::string value;
    check(registry_live_read("items", "game:material_t1_wood_logs", "maxStack", &value) == KILN_OK && value == "50", "the pre-world read includes it");
    check(registry_live_revert(71, h, &reply) == KILN_OK && reply.outcome == RegistryLiveOutcome::Reverted, "revert without a world");
    check(registry_live_read("items", "game:material_t1_wood_logs", "maxStack", &value) == KILN_OK && value == "10", "the composition drops it");
    registry_live_world_begin(); apply_registry_files({});
    check(at<uint32_t>(WOOD_AT) == 10 && registry_live_status().active == 0, "the next world never applies it");
}
// review2 N4: a world's end drops its live rows from registry.json at the next pump.
void world_stop_reports_without_its_live_rows() {
    fresh_live(); RegistryLiveReply reply;
    const auto h = change(71, "a", "[{" + wood + R"("op":70}])", 2);
    registry_live_commit(71, h, &reply); registry_live_publish();
    auto has_live = [] { return registry::report_json(registry_schema(), g_last_report, {}).dump().find("live:") != std::string::npos; };
    check(has_live(), "the world's change is in registry.json");
    registry_live_world_stop(); registry_live_publish();
    check(!has_live(), "after the world ends, registry.json no longer shows its live rows");
}
// review2 N6: a live read of an id the world does not have is NOT_FOUND, as before a world.
void live_read_of_a_missing_entry_is_not_found() {
    fresh_live(); std::string value;
    check(registry_live_read("items", "game:nothing_here", "maxStack", &value) == KILN_ERR_NOT_FOUND, "an unknown id is NOT_FOUND");
    reg(KILN_CONTENT_ITEMS).by_id.erase(SWORD);
    check(registry_live_read("items", "game:sword_t2", "maxStack", &value) == KILN_ERR_NOT_FOUND, "an entry missing in this world is NOT_FOUND");
    registry_live_world_stop();
    check(registry_live_read("items", "game:nothing_here", "maxStack", &value) == KILN_ERR_NOT_FOUND, "as before a world");
}
// review4 NM1: two retained changes, one slot each, the write of one of them fails at a reapplication: only that one is charged.
void multi_change_write_failure_is_attributed_per_change() {
    fresh_live(); RegistryLiveReply reply;
    const auto a = change(71, "a", "[{" + wood + R"("op":{"mul":3}}])", 1);
    registry_live_commit(71, a, &reply);
    const auto b = change(72, "b", "[{" + stone + R"("op":70}])", 1);
    registry_live_commit(72, b, &reply);
    check(at<uint32_t>(WOOD_AT) == 30 && at<uint32_t>(STONE_AT) == 70, "both applied");
    put_pack(WOOD_AT, 20); // a relative change over a new pack value: only a's slot is written again
    g_write_limits = {0};
    apply_registry_files({});
    check(at<uint32_t>(STONE_AT) == 70, "b's slot is untouched");
    check(!report_names("b", "not applied") && report_names("a", "the game memory write of items[game:material_t1_wood_logs].maxStack failed"),
          "the failed write is charged to the change that owns the slot only");
}

void packs_code_and_out_of_order_revert() {
    fresh_live(); put_pack(WOOD_AT, 20); std::string err; store_flush(&err);
    RegistryLiveReply reply;
    auto a = change(71, "a", "[{" + wood + R"("op":{"mul":3}}])");
    registry_live_commit(71, a, &reply);
    check(at<uint32_t>(WOOD_AT) == 60, "relative live edit composes on old pack");
    auto b = change(72, "b", "[{" + wood + R"("op":{"mul":2}}])");
    registry_live_commit(72, b, &reply);
    check(at<uint32_t>(WOOD_AT) == 120, "later transaction composes");
    registry_live_revert(71, a, &reply);
    check(at<uint32_t>(WOOD_AT) == 40, "reverting an earlier handle keeps the later multiplier");
    uint8_t bytes[4]; uint32_t code = 333; std::memcpy(bytes, &code, 4);
    store_put(CODE, next_code_seq(), KILN_CONTENT_ITEMS, WOOD, 0, -1, A(WOOD_AT), 4, bytes, "code"); store_flush(&err);
    registry_live_revert(72, b, &reply);
    check(at<uint32_t>(WOOD_AT) == 333, "revert never rolls back another code owner");
    store_drop_owner(CODE); store_flush(&err);
    check(at<uint32_t>(WOOD_AT) == 20, "old pack remains under every reverted transaction");
}
void unrelated_pack_binding_failure_isolated() {
    world();
    g_files = {{"p/content/p.json", R"({"registry":{"alter":[{"registry":"items","select":"game:material_t1_wood_logs","field":"manaCost","op":2}]}})"}};
    const auto files = files_of({{"p", "p/content/p.json"}});
    stage(files); registry_live_world_begin(); apply_registry_files(files);
    RegistryLiveReply reply;
    const auto h = change(71, "a", "[{" + wood + R"("op":{"mul":3}}])");
    check(registry_live_commit(71, h, &reply) == KILN_OK, "independent commit call succeeds");
    check(reply.outcome == RegistryLiveOutcome::Applied && reply.accepted == 1 && at<uint32_t>(WOOD_AT) == 30,
          "an unrelated pack's unbindable edit does not refuse this live change");
    check(reported("the item has no such configuration"), "the unrelated binding refusal remains visible in the report");
}
void unrelated_live_binding_failure_isolated() {
    fresh_live(); RegistryLiveReply reply;
    const auto a = change(71, "a", R"([{"registry":"items","select":"game:sword_t2","field":"manaCost","op":20}])");
    registry_live_commit(71, a, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied, "first live change initially binds");
    reg(KILN_CONTENT_ITEMS).by_id.erase(SWORD); // the earlier change's entry no longer binds in this world
    const auto b = change(72, "b", "[{" + wood + R"("op":{"mul":3}}])");
    check(registry_live_commit(72, b, &reply) == KILN_OK, "second independent commit call succeeds");
    check(reply.outcome == RegistryLiveOutcome::Applied && reply.accepted == 1 && at<uint32_t>(WOOD_AT) == 30,
          "an earlier live change's unbindable edit does not refuse another owner");
    registry_live_publish();
    check(reported("the entry is not in this world"), "the earlier change's binding refusal remains visible");
}
// review4 NS6 (ruling Nov 23): an entry-point change refuses per entry, like GameStart: the unbindable entry alone.
void pending_binding_failure_drops_only_that_entry() {
    world(); g_files.clear(); stage({});
    RegistryLiveReply reply;
    const auto h = change(71, "a", "[{" + wood + R"("op":75},{"registry":"items","select":"game:material_t1_wood_logs","field":"manaCost","op":2}])", 1);
    check(registry_live_commit(71, h, &reply) == KILN_OK && reply.outcome == RegistryLiveOutcome::PendingWorld && reply.accepted == 2,
          "the complete pre-world change waits for live binding");
    registry_live_world_begin(); apply_registry_files({});
    check(at<uint32_t>(WOOD_AT) == 75 && registry_live_status().active == 1, "the bindable entry applies");
    check(reported("items[game:material_t1_wood_logs].manaCost: the item has no such configuration") && !reported("not applied in this world"),
          "only the unbindable entry is refused, named");
    apply_registry_files({});
    check(at<uint32_t>(WOOD_AT) == 75 && reported("the item has no such configuration"), "a later application in the world keeps both");
    check(registry_live_revert(71, h, &reply) == KILN_OK && reply.outcome == RegistryLiveOutcome::Reverted && at<uint32_t>(WOOD_AT) == 10,
          "the handle reverts what applied");
}
void carried_binding_failure_drops_only_that_entry() {
    fresh_live(); RegistryLiveReply reply;
    const auto h = change(71, "a", "[{" + wood + R"("op":75},{"registry":"items","select":"game:sword_t2","field":"manaCost","op":20}])", 1);
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && at<uint32_t>(WOOD_AT) == 75, "the process change binds in its first world");
    registry_live_world_stop(); std::string error; store_reset(true, &error); world();
    reg(KILN_CONTENT_ITEMS).by_id.erase(SWORD);
    registry_live_world_begin(); apply_registry_files({});
    check(at<uint32_t>(WOOD_AT) == 75 && registry_live_status().active == 1, "the next world applies the entries it has");
    check(reported("items[game:sword_t2].manaCost: the entry is not in this world"), "the missing entry is refused, named");
    registry_live_world_stop(); store_reset(true, &error); world();
    registry_live_world_begin(); apply_registry_files({});
    check(at<uint32_t>(WOOD_AT) == 75 && at<float>(SWORD_AT + 0x40) == 20.0f, "a world that has the entry again applies it again");
}
void reapplied_binding_failure_is_atomic() {
    fresh_live(); RegistryLiveReply reply;
    const auto h = change(71, "a", "[{" + wood + R"("op":75},{"registry":"items","select":"game:sword_t2","field":"manaCost","op":20}])", 4);
    registry_live_commit(71, h, &reply);
    check(at<uint32_t>(WOOD_AT) == 75, "the manual transaction is initially fully applied");
    reg(KILN_CONTENT_ITEMS).by_id.erase(SWORD);
    apply_registry_files({});
    check(at<uint32_t>(WOOD_AT) == 10 && registry_live_status().active == 0,
          "a pack application cannot leave half a now-unbindable retained change");
    check(reported("not applied in this world"), "the reapplication refusal remains visible");
}
void pending_evaluation_failure_is_atomic() {
    world(); g_files.clear(); stage({}); RegistryLiveReply reply;
    const auto h = change(71, "a", "[{" + wood + R"("op":75},{"registry":"items","select":"game:sword_t2","field":"manaCost","op":{"mul":1000}}])", 1);
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::PendingWorld && reply.accepted == 2, "archive values admit the pending relative change");
    mem<float>(SWORD_AT + 0x40, 200.0f); mem<int32_t>(SWORD_AT + 0x44, 200);
    registry_live_world_begin(); apply_registry_files({});
    check(at<uint32_t>(WOOD_AT) == 75 && at<float>(SWORD_AT + 0x40) == 200.0f,
          "a runtime evaluation failure refuses that entry before any write; the rest of the entry-point change applies");
    check(registry_live_status().active == 1 && reported("items[game:sword_t2].manaCost: the result") && !reported("not applied in this world"),
          "the failed evaluation is reported for its entry only");
}
void pending_write_failure_is_rolled_back() {
    world(); g_files.clear(); stage({}); RegistryLiveReply reply;
    const auto h = change(71, "a", "[{" + wood + R"("op":75},{"registry":"items","select":"game:sword_t2","field":"maxStack","op":9}])", 1);
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::PendingWorld, "the two-field change waits for a world");
    g_write_limits = {1}; // one field really writes, then the second write fails; rollback is allowed to write normally
    registry_live_world_begin(); apply_registry_files({});
    check(at<uint32_t>(WOOD_AT) == 10 && at<uint32_t>(SWORD_AT) == 0,
          "a partial pending write failure restores the fields that already wrote");
    check(registry_live_status().active == 0 && reported("not applied in this world"), "a failed pending write is deactivated and reported");
}
void commit_does_not_reenter_ready() {
    fresh_live();
    struct Probe { int depth = 0, max_depth = 0, reports = 0, applications = 0; bool reverting = false; uint64_t previous = 0; };
    auto probe = std::make_shared<Probe>();
    g.in->report = [probe](const registry::Result& result, bool application) {
        g_last_report = result; ++probe->reports;
        if (!application) return;
        ++probe->applications;
        if (probe->reverting || probe->depth >= 3) return; // bounded linear reproduction, never a recursive tree
        ++probe->depth; probe->max_depth = std::max(probe->max_depth, probe->depth);
        RegistryLiveReply reply;
        if (probe->previous) {
            probe->reverting = true; registry_live_revert(81, probe->previous, &reply); probe->reverting = false;
        }
        probe->previous = change(81, "reapply", "[{" + wood + R"("op":{"add":1}}])");
        registry_live_commit(81, probe->previous, &reply);
        --probe->depth;
    };
    RegistryLiveReply reply;
    probe->previous = change(81, "reapply", "[{" + wood + R"("op":{"add":1}}])");
    registry_live_commit(81, probe->previous, &reply);
    check(probe->reports == 0 && probe->max_depth == 0, "commit returns before publishing, with no ready-callback recursion");
    registry_live_publish();
    check(probe->reports == 1 && probe->applications == 0, "the next pump publishes a transaction snapshot, not WORLDLOAD");
}
void revert_defers_its_snapshot() {
    fresh_live(); RegistryLiveReply reply;
    const auto h = change(71, "a", "[{" + wood + R"("op":50}])");
    registry_live_commit(71, h, &reply); registry_live_publish();
    auto reports = std::make_shared<int>(0);
    g.in->report = [reports](const registry::Result& result, bool application) { g_last_report = result; if (!application) ++*reports; else *reports += 100; };
    registry_live_revert(71, h, &reply);
    check(*reports == 0 && at<uint32_t>(WOOD_AT) == 10, "revert writes its result but never dispatches a report callback inline");
    registry_live_publish();
    check(*reports == 1, "revert's next-pump snapshot is not a world-load notification");
}
void nested_transactions_are_refused() {
    fresh_live(); RegistryLiveReply reply;
    const auto outer = change(71, "a", "[{" + wood + R"("op":50}])");
    const auto nested = change(72, "b", "[{" + wood + R"("op":80}])");
    KilnResult commit_rc = KILN_OK, revert_rc = KILN_OK;
    g_before_write = [&] {
        RegistryLiveReply inner;
        commit_rc = registry_live_commit(72, nested, &inner);
        revert_rc = registry_live_revert(71, outer, &inner);
    };
    registry_live_commit(71, outer, &reply);
    check(commit_rc == KILN_ERR_UNAVAILABLE && revert_rc == KILN_ERR_UNAVAILABLE,
          "a write callback cannot reenter commit or revert while another transaction is in flight");
    check(reply.outcome == RegistryLiveOutcome::Applied && at<uint32_t>(WOOD_AT) == 50,
          "refusing nested transactions preserves the outer write and its handle");
    registry_live_revert(72, nested, &reply); registry_live_revert(71, outer, &reply);
}
void wrong_world_thread_is_refused_under_lock() {
    fresh_live(); RegistryLiveReply reply;
    const auto active = change(71, "a", "[{" + wood + R"("op":50}])");
    registry_live_commit(71, active, &reply); registry_live_publish();
    const auto draft = change(72, "b", "[{" + wood + R"("op":80}])");
    auto thread = std::make_shared<bool>(false);
    g.in->on_world_thread = [thread](uint32_t kind) { return kind == KILN_WORLD_SERVER && *thread; };
    const auto generation = store_generation();
    const auto commit_rc = registry_live_commit(72, draft, &reply);
    const auto revert_rc = registry_live_revert(71, active, &reply);
    std::string value;
    const auto read_rc = registry_live_read("items", "game:material_t1_wood_logs", "maxStack", &value);
    check(commit_rc == KILN_ERR_WRONG_THREAD && revert_rc == KILN_ERR_WRONG_THREAD && read_rc == KILN_ERR_WRONG_THREAD,
          "the backend checks the active world's thread under its store lock for commit, revert and read");
    check(store_generation() == generation && at<uint32_t>(WOOD_AT) == 50 && registry_live_status().active == 1,
          "off-thread requests neither write nor consume handles");
    registry_live_owner_gone(71);
    registry_live_publish();
    check(store_generation() == generation && at<uint32_t>(WOOD_AT) == 50 && registry_live_status().active == 1,
          "an off-thread pump leaves owner cleanup queued for the world thread");
    *thread = true;
    registry_live_publish();
    check(at<uint32_t>(WOOD_AT) == 10 && registry_live_status().active == 0,
          "the next world-thread pump removes the retired owner's complete change");
    check(registry_live_status().world_kind == KILN_WORLD_SERVER, "status copies the kind paired with its world token");
    registry_live_revert(72, draft, &reply);
}
void stopped_world_uses_only_copied_data() {
    fresh_live(); RegistryLiveReply reply;
    const auto process = change(71, "a", "[{" + wood + R"("op":50}])", 1);
    registry_live_commit(71, process, &reply);
    registry_live_world_stop();
    g.in->on_world_thread = [](uint32_t) { return false; };
    const auto generation = store_generation();
    const auto pending = change(72, "b", "[{" + wood + R"("op":80}])", 1);
    check(registry_live_commit(72, pending, &reply) == KILN_OK && reply.outcome == RegistryLiveOutcome::PendingWorld,
          "after the stop fence a caller may commit copied process inputs without touching the old world");
    check(store_generation() == generation && at<uint32_t>(WOOD_AT) == 50,
          "a stopped-world process commit does not write the old world's bytes");
    check(registry_live_status().world == 0 && registry_live_status().world_kind == KILN_WORLD_KINDS,
          "the stopped token and kind are cleared together");
}
void final_then_later_alter_obeys_commit_order() {
    fresh_live(); RegistryLiveReply reply;
    uint64_t a = 0;
    registry_live_begin(71, "a", 4, &a);
    std::string final = doc("a", "[{" + wood + R"("op":70}])");
    final.replace(final.find("alter"), 5, "final");
    registry_live_submit(71, a, final, &reply, nullptr);
    registry_live_commit(71, a, &reply);
    const auto b = change(72, "b", "[{" + wood + R"("op":80}])");
    registry_live_commit(72, b, &reply);
    check(at<uint32_t>(WOOD_AT) == 80, "commit order wins even when an earlier transaction was Final and the later one Alter");
    registry_live_revert(72, b, &reply);
    check(at<uint32_t>(WOOD_AT) == 70, "reverting the later Alter restores the earlier Final");
}
void prepared_turn_still_checks_references() {
    fresh_live(); RegistryLiveReply reply;
    const auto invalid = change(71, "a", R"([{"registry":"recipes","select":"game:plank_recipe","field":"inputs","list":{"replace":[{"item":"missing:ingredient","count":3}]}}])");
    check(registry_live_commit(71, invalid, &reply) == KILN_OK && reply.outcome == RegistryLiveOutcome::Refused && reply.accepted == 0 && reply.reason.find("missing:ingredient") != std::string::npos,
          "a field-only continuation still refuses a new missing reference");
    const auto valid = change(72, "b", R"([{"registry":"recipes","select":"game:plank_recipe","field":"inputs","list":{"replace":[{"item":"game:material_t1_wood_logs","count":3}]}}])");
    check(registry_live_commit(72, valid, &reply) == KILN_OK && reply.outcome == RegistryLiveOutcome::Applied,
          "a valid reference-list transaction remains writable");
    std::string value;
    check(registry_live_read("recipes", "game:plank_recipe", "inputs", &value) == KILN_OK && registry::json::parse(value)[0]["count"] == 3,
          "the reference-list transaction is visible through the mapped reader");
    registry_live_revert(72, valid, &reply);
    check(registry_live_read("recipes", "game:plank_recipe", "inputs", &value) == KILN_OK && registry::json::parse(value)[0]["count"] == 2,
          "reverting the reference-list transaction restores the original list");
}
void retained_changes_have_bounded_commit_cost() {
    fresh_live();
    RegistryLive installed = *g.in;
    for (size_t i = 0; i < 20000; ++i) {
        registry::BaseModel::Entry entry;
        entry.id = "game:cost_" + std::to_string(i);
        entry.fields = {{"maxStack", registry::Value::integer(10)}, {"manaCost", registry::Value::number(10)},
            {"staminaCost", registry::Value::number(10)}, {"power", registry::Value::integer(10)},
            {"rarity", registry::Value::text("common")}, {"perks", registry::Value::of_list({})}};
        installed.base.registries["items"].push_back(std::move(entry));
    }
    registry_live_install(std::move(installed)); registry_live_world_begin(); apply_registry_files({});
    // Seed admitted copied changes directly, without charging or waiting for 63 earlier commits in this bounded-cost test.
    // The final public commit still recomposes and binds all 64 retained transactions through the real backend/store.
    for (unsigned i = 0; i < 63; ++i) {
        const auto h = change(1000 + i, "cost-" + std::to_string(i), "[{" + wood + R"("op":{"add":1}}])");
        g.changes.at(h).order = ++g.next_order;
    }
    const auto h = change(2000, "last", "[{" + wood + R"("op":{"add":1}}])");
    RegistryLiveReply reply;
    const auto start = std::chrono::steady_clock::now();
    const auto rc = registry_live_commit(2000, h, &reply);
    const double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
    std::printf("64 retained changes / 20000 extra entries: %.2f ms commit\n", ms);
    check(rc == KILN_OK && reply.outcome == RegistryLiveOutcome::Applied && at<uint32_t>(WOOD_AT) == 74,
          "the bounded-cost commit preserves all 64 ordered relative edits");
    check(ms < 1000, "64 retained scalar changes over 20000 entries commit within the 1000 ms sanitizer-host budget");
}
registry::stage::LiveData lazy_alias_data() {
    registry::stage::LiveData data;
    auto entry = base().registries.at("items").front(); entry.id = "game:wood_alias";
    data.base.registries["items"].push_back(std::move(entry));
    data.ids = {{"items|game:wood_alias", guid_text(G1)}};
    data.nums = {{"items|game:wood_alias", WOOD}};
    data.decoded = {"items.maxStack"};
    return data;
}
void begin_prepares_copied_base_once() {
    fresh_live(); auto calls = std::make_shared<int>(0), locked = std::make_shared<int>(0);
    // review4 NS1: the first-use decode reads the archive for seconds; it never holds the store lock the world thread needs
    g.in->prepare = [calls, locked](registry::stage::LiveData* out, std::string*) {
        ++*calls; *locked += tl_depth != 0; *out = lazy_alias_data(); return true;
    };
    registry_live_status();
    check(*calls == 0, "status does not decode an unused registry");
    const auto h = change(71, "a", R"([{"registry":"items","select":"game:wood_alias","field":"maxStack","op":{"mul":3}}])");
    check(*calls == 1, "the first native begin prepares the copied stable base once");
    RegistryLiveReply reply;
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && at<uint32_t>(WOOD_AT) == 30,
          "lazy preparation refreshes composition lookup and mapped id indexes before a commit");
    std::string value;
    check(registry_live_read("items", "game:wood_alias", "maxStack", &value) == KILN_OK && value == "30" && *calls == 1,
          "reads reuse the first prepared model without another decode");
    check(*locked == 0, "the first-use preparation ran outside the store lock");
}
void read_can_be_first_registry_use() {
    fresh_live(); auto calls = std::make_shared<int>(0);
    g.in->prepare = [calls](registry::stage::LiveData* out, std::string*) {
        check(tl_depth == 0, "a read's first-use preparation runs outside the store lock");
        ++*calls; *out = lazy_alias_data(); return true;
    };
    const auto generation = store_generation(); std::string value;
    check(registry_live_read("items", "game:wood_alias", "maxStack", &value) == KILN_OK && value == "10" && *calls == 1,
          "a mapped read can prepare the copied model without beginning a transaction");
    check(store_generation() == generation && registry_live_status().drafts == 0,
          "read-first preparation creates no store writes or transaction handles");
}
void unavailable_preparation_remains_retryable() {
    fresh_live(); auto ready = std::make_shared<bool>(false);
    g.in->prepare = [ready](registry::stage::LiveData* out, std::string* why) {
        if (!*ready) { *why = "synthetic archive not ready"; return false; }
        *out = lazy_alias_data(); return true;
    };
    uint64_t h = 0;
    check(registry_live_begin(71, "a", 1, &h) == KILN_ERR_UNAVAILABLE && h == 0 && registry_live_status().drafts == 0,
          "failed lazy preparation admits no draft and reports unavailable");
    *ready = true;
    check(registry_live_begin(71, "a", 1, &h) == KILN_OK && h != 0 && !g.in->prepare,
          "a later preparation can succeed and release its loader closure");
    RegistryLiveReply reply; registry_live_revert(71, h, &reply);
}
size_t impact_at(const std::string& field) { return SWORD_AT + (field == "manaCost" ? 0x40 : 0x48); }
void use_integer_impact(const std::string& field) {
    g_integer_impacts.insert(field);
    mem<int32_t>(impact_at(field), 10); mem<int32_t>(impact_at(field) + 4, 10);
}
std::string cost_edit(const std::string& field, const std::string& value) {
    return "{\"registry\":\"items\",\"select\":\"game:sword_t2\",\"field\":\"" + field + "\",\"op\":" + value + "}";
}
void fractional_integer_cost_is_refused(const std::string& field) {
    for (const std::string value : {"67.5", "2.5"}) {
        fresh_live(); use_integer_impact(field); RegistryLiveReply reply;
        const auto h = change(71, "a", "[{" + wood + "\"op\":75}," + cost_edit(field, value) + "]");
        registry_live_commit(71, h, &reply);
        check(reply.outcome == RegistryLiveOutcome::Refused && reply.accepted == 0 && reply.reason.rfind("not_exact: ", 0) == 0,
              field + ": fractional integer-storage refusal has the stable not_exact token, never Applied");
        check(reply.reason.find("items." + field + " of game:sword_t2 is a whole number on this build; " + value + " was not written") != std::string::npos,
              "the refusal names the stable field, item and unrounded value");
        check(at<int32_t>(impact_at(field)) == 10 && at<uint32_t>(WOOD_AT) == 10,
              "neither the fractional integer value nor another field in its transaction is written");
    }
}
void whole_and_float_costs_still_work() {
    for (const std::string field : {"manaCost", "staminaCost"}) {
        fresh_live(); use_integer_impact(field); RegistryLiveReply reply;
        auto h = change(71, "a", "[" + cost_edit(field, "67") + "]"); registry_live_commit(71, h, &reply);
        check(reply.outcome == RegistryLiveOutcome::Applied && at<int32_t>(impact_at(field)) == 67, "whole cost values remain writable to integer storage");
        registry_live_revert(71, h, &reply);
        check(at<int32_t>(impact_at(field)) == 10, "whole integer cost values revert exactly");
        for (const std::string value : {"67.5", "2.5"}) {
            fresh_live();
            h = change(71, "a", "[" + cost_edit(field, value) + "]"); registry_live_commit(71, h, &reply);
            check(reply.outcome == RegistryLiveOutcome::Applied && at<float>(impact_at(field)) == std::stof(value),
                  "float-config entries keep fractional values");
            registry_live_revert(71, h, &reply);
            check(at<float>(impact_at(field)) == 10.0f, "float-config fractions revert exactly");
        }
    }
}
void staged_fractional_integer_cost_is_refused() {
    for (const std::string field : {"manaCost", "staminaCost"}) {
        world(); g_files.clear(); use_integer_impact(field);
        g_files["a/content/cost.json"] = "{\"registry\":{\"alter\":[" + cost_edit(field, "2.5") + "]}}";
        const auto files = files_of({{"a", "a/content/cost.json"}});
        stage(files); registry_live_world_begin(); apply_registry_files(files);
        check(at<int32_t>(impact_at(field)) == 10, "a start-time edit cannot round a fractional cost at world-load binding");
        bool exact = false;
        for (const auto& refusal : g_last_report.refused) exact |= refusal.reason.rfind("not_exact: ", 0) == 0;
        check(exact, "the start-time/world-load refusal also carries the stable reason token");
    }
}
void pending_fractional_integer_cost_is_refused() {
    world(); g_files.clear(); use_integer_impact("manaCost"); stage({}); RegistryLiveReply reply;
    const auto h = change(71, "a", "[" + cost_edit("manaCost", "67.5") + "]", 1);
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::PendingWorld, "a copied cost waits for its actual per-entry storage type");
    registry_live_world_begin(); apply_registry_files({});
    check(at<int32_t>(impact_at("manaCost")) == 10, "world-load binding refuses the inexact entry; nothing is rounded");
    bool exact = false;
    for (const auto& refusal : g_last_report.refused) exact |= refusal.reason.rfind("not_exact: ", 0) == 0;
    check(exact, "a pending change's rejection preserves the stable reason token");
}
// review4 NS6: an inexact whole-number cost at an entry point refuses that entry only, with the not_exact token.
void entry_point_inexact_cost_drops_only_that_entry() {
    for (uint32_t point = 1; point <= 3; ++point) {
        fresh_live(); use_integer_impact("manaCost"); RegistryLiveReply reply;
        const auto h = change(71, "a", "[{" + wood + "\"op\":75}," + cost_edit("manaCost", "2.5") + "]", point);
        check(registry_live_commit(71, h, &reply) == KILN_OK && reply.outcome == RegistryLiveOutcome::Applied, "the pass applies");
        check(reply.accepted == 2 && reply.refused == 1 && reply.reason.rfind("not_exact: ", 0) == 0,
              "refused > 0 with the stable not_exact token for that entry");
        check(at<uint32_t>(WOOD_AT) == 75 && at<int32_t>(impact_at("manaCost")) == 10, "the other entry applies, the cost is never rounded");
        registry_live_publish();
        bool exact = false;
        for (const auto& r : g_last_report.refused) exact |= r.reason.rfind("not_exact: ", 0) == 0 && r.id == "game:sword_t2";
        check(exact, "registry.json names the refused entry with the token");
        registry_live_revert(71, h, &reply);
        check(at<uint32_t>(WOOD_AT) == 10, "revert removes what applied");
    }
}
// review4 NS6: a selector over every item at an entry point refuses the entries that cannot take the edit, one by one;
// a MANUAL transaction with the same edit is refused whole.
void entry_point_selector_refuses_per_entry() {
    fresh_live(); use_integer_impact("manaCost"); RegistryLiveReply reply;
    const std::string all = R"([{"registry":"items","select":"*","field":"manaCost","op":{"mul":1.5}}])";
    const auto manual = change(70, "m", all, 4);
    registry_live_commit(70, manual, &reply);
    check(reply.outcome == RegistryLiveOutcome::Refused && reply.accepted == 0 && at<int32_t>(impact_at("manaCost")) == 10,
          "a manual transaction stays all-or-nothing");
    const auto h = change(71, "a", all, 2);
    check(registry_live_commit(71, h, &reply) == KILN_OK && reply.outcome == RegistryLiveOutcome::Applied && reply.accepted == 1 && reply.refused == 2,
          "an entry point applies the entries that can take it and counts each refused entry");
    check(at<int32_t>(impact_at("manaCost")) == 15, "the sword's whole-number cost 10 x 1.5 = 15 applies");
    registry_live_revert(71, h, &reply);
}
// review2 S3: an exclusive conflict on one selected entry refuses that entry at an entry point; MANUAL refuses the edit.
void selector_conflict_refuses_one_entry_at_entry_points() {
    for (uint32_t point : {2u, 4u}) {
        fresh_live(); RegistryLiveReply reply;
        const auto a = change(70, "a", "[{" + wood + R"("op":50,"exclusive":true}])");
        registry_live_commit(70, a, &reply);
        const auto b = change(72, "b", R"([{"registry":"items","select":"game:material_*","field":"maxStack","op":70}])", point);
        registry_live_commit(72, b, &reply);
        if (point == 4) {
            check(reply.outcome == RegistryLiveOutcome::Refused && reply.accepted == 0 && reply.refused == 1,
                  "a manual wildcard edit with one exclusive conflict is refused as one edit");
            check(at<uint32_t>(WOOD_AT) == 50 && at<uint32_t>(STONE_AT) == 50, "nothing of it applies");
        } else {
            check(reply.outcome == RegistryLiveOutcome::Applied && reply.accepted == 1 && reply.refused == 1 &&
                  reply.reason.find("Conflict (exclusive)") != std::string::npos,
                  "an entry point refuses the conflicting entry alone, with its reason");
            check(at<uint32_t>(WOOD_AT) == 50 && at<uint32_t>(STONE_AT) == 70, "the other selected entry applies");
        }
        registry_live_revert(70, a, &reply);
        check(at<uint32_t>(WOOD_AT) == 10 && at<uint32_t>(STONE_AT) == (point == 4 ? 50u : 70u),
              "a refused entry never activates later when its conflicting owner leaves");
    }
}
void flattened_socket_read_write_revert() {
    fresh_live(); RegistryLiveReply reply; std::string value;
    mem<uint8_t>(WOOD_AT + 0x97, 0x5a); mem<uint8_t>(WOOD_AT + 0x99, 0xa5);
    check(registry_live_read("items", "game:material_t1_wood_logs", "socket", &value) == KILN_OK && value == "false",
          "socket reads the scalar that reflection exposes for its single-field struct");
    const auto h = change(71, "a", R"([{"registry":"items","select":"game:material_t1_wood_logs","field":"socket","op":true}])");
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && at<uint8_t>(WOOD_AT + 0x98) == 1,
          "socket writes the flattened Boolean, not a missing nested member");
    check(at<uint8_t>(WOOD_AT + 0x97) == 0x5a && at<uint8_t>(WOOD_AT + 0x99) == 0xa5,
          "socket writes only its one-byte slot");
    check(registry_live_read("items", "game:material_t1_wood_logs", "socket", &value) == KILN_OK && value == "true", "the mapped socket read observes the write");
    registry_live_revert(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Reverted && at<uint8_t>(WOOD_AT + 0x98) == 0, "socket reverts its own Boolean layer");
}
void staged_flattened_socket_binds() {
    world(); g_files = {{"a/content/socket.json", R"({"registry":{"alter":[{"registry":"items","select":"game:material_t1_wood_logs","field":"socket","op":true}]}})"}};
    const auto files = files_of({{"a", "a/content/socket.json"}});
    stage(files); registry_live_world_begin(); apply_registry_files(files);
    check(at<uint8_t>(WOOD_AT + 0x98) == 1, "start-time socket edits bind the flattened field at world load too");
}
void flattened_socket_foreign_revert_is_skipped() {
    fresh_live(); RegistryLiveReply reply;
    const auto h = change(71, "a", R"([{"registry":"items","select":"game:material_t1_wood_logs","field":"socket","op":true}])");
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied, "the socket change applies before outside drift");
    mem<uint8_t>(WOOD_AT + 0x98, 2);
    registry_live_revert(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Skipped && at<uint8_t>(WOOD_AT + 0x98) == 2,
          "a socket revert preserves another writer's bytes");
}
void unlock_read_has_no_write_side_effect() {
    fresh_live(); std::string value;
    const auto generation = store_generation(); const auto owners = store_owners();
    check(registry_live_read("recipes", "game:plank_recipe", "unlock", &value) == KILN_OK && registry::json::parse(value) == "game",
          "unlock reads the existing nonzero knowledge requirement through the live table");
    check(store_generation() == generation && store_owners() == owners,
          "unlock reads create neither store layers nor writes");
    mem<uint32_t>(PLANK_AT + 0x50, 0);
    check(registry_live_read("recipes", "game:plank_recipe", "unlock", &value) == KILN_OK && registry::json::parse(value) == "game",
          "unlock checks the whole query, not only its first word");
    mem<uint32_t>(PLANK_AT + 0x58, 0);
    check(registry_live_read("recipes", "game:plank_recipe", "unlock", &value) == KILN_OK && registry::json::parse(value) == "always",
          "an entirely empty query reads as always");
}
void unlock_write_read_revert() {
    fresh_live(); RegistryLiveReply reply; std::string value;
    const auto h = change(71, "a", R"([{"registry":"recipes","select":"game:plank_recipe","field":"unlock","op":"always"}])");
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && at<uint32_t>(PLANK_AT + 0x50) == 0, "unlock writes already work");
    check(registry_live_read("recipes", "game:plank_recipe", "unlock", &value) == KILN_OK && registry::json::parse(value) == "always",
          "unlock reads observe the cleared knowledge requirement");
    registry_live_revert(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Reverted && at<uint32_t>(PLANK_AT + 0x50) == 0x12345678 && at<uint32_t>(PLANK_AT + 0x58) == 7,
          "unlock revert restores the original query bytes");
    check(registry_live_read("recipes", "game:plank_recipe", "unlock", &value) == KILN_OK && registry::json::parse(value) == "game",
          "unlock reads observe the restored query");
}
void unlock_foreign_revert_is_skipped() {
    fresh_live(); RegistryLiveReply reply; std::string value;
    const auto h = change(71, "a", R"([{"registry":"recipes","select":"game:plank_recipe","field":"unlock","op":"always"}])");
    registry_live_commit(71, h, &reply);
    mem<uint32_t>(PLANK_AT + 0x50, 123);
    check(registry_live_read("recipes", "game:plank_recipe", "unlock", &value) == KILN_OK && registry::json::parse(value) == "game",
          "unlock reads actual outside bytes, not its cached intended value");
    registry_live_revert(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Skipped && at<uint32_t>(PLANK_AT + 0x50) == 123,
          "unlock revert preserves another writer's whole query slot");
}
void live_raw_refusals_keep_place_metadata() {
    fresh_live();
    // The stage already checked Advanced raw edits. Exercise their world binder, not a public live-submit bypass.
    auto outcome = [](const std::string& path) {
        registry::FieldOutcome fo;
        fo.raw = path;
        fo.raw_spec = std::make_shared<registry::FieldSpec>(*registry_schema().registry("items")->field("maxStack"));
        registry::ChainRow row;
        row.mod = row.who = "advanced-mod"; row.source = "content/raw.json"; row.n = 9; row.raw = path;
        row.op.set = true; row.op.value = registry::Value::integer(20);
        fo.chain.push_back(row);
        return fo;
    };
    bind({"items", "game:material_t1_wood_logs", "raw:Missing.Member"}, outcome("Missing.Member"));
    reg(KILN_CONTENT_ITEMS).schema.fields[0].writable = false;
    bind({"items", "game:material_t1_wood_logs", "raw:maxStackSize"}, outcome("maxStackSize"));
    check(g.refused.size() == 2, "both missing and nonwritable raw members produce live refusals");
    for (const auto& refusal : g.refused) {
        check(refusal.registry == "items" && refusal.id == "game:material_t1_wood_logs" && !refusal.field.empty() && refusal.raw,
              "live raw binding refusals retain their place and explicit Advanced-tier marker");
        check(refusal.mod == "advanced-mod" && refusal.source == "content/raw.json" && refusal.n == 9,
              "raw refusal retains its author and source edit");
    }
    registry::Result result; result.refused = g.refused;
    const auto report = registry::report_json(registry_schema(), result, {});
    for (const auto& row : report.at("refused"))
        check(row.value("raw", false) && row.at("registry") == "items" && row.at("id") == "game:material_t1_wood_logs",
              "the serialized live refusal lets a launcher hide all raw member details");
}
void raw_refusal_marker_does_not_depend_on_place_text() {
    registry::Result result; registry::Refusal refusal;
    refusal.mod = "advanced-mod"; refusal.raw = true;
    refusal.reason = "the member Missing.Member is not writable";
    result.refused.push_back(refusal);
    const auto report = registry::report_json(registry_schema(), result, {});
    check(report.at("refused").at(0).value("raw", false),
          "an explicit raw refusal stays raw even when its place is unavailable");
}
void raw_evaluation_refusal_is_marked_but_unknown_kiln_field_is_not() {
    fresh_live();
    registry::FieldOutcome fo;
    fo.raw = "maxStackSize";
    auto spec = std::make_shared<registry::FieldSpec>(*registry_schema().registry("items")->field("maxStack"));
    spec->max = 15; fo.raw_spec = spec;
    registry::ChainRow row; row.mod = row.who = "advanced-mod";
    row.source = "content/raw.json"; row.op.set = true; row.op.value = registry::Value::integer(20);
    fo.chain.push_back(row);
    bind({"items", "game:material_t1_wood_logs", "raw:maxStackSize"}, fo);
    uint8_t bytes[4] = {};
    check(store_effective(A(WOOD_AT), 4, bytes) && bytes[0] == 10, "an out-of-range raw edit leaves the value below it during evaluation");
    check(!g.refused.empty() && g.refused.back().raw && g.refused.back().field == "maxStackSize",
          "evaluation refusals also carry raw place metadata");
    bind({"items", "game:material_t1_wood_logs", "maxStak"}, fo);
    check(!g.refused.back().raw && g.refused.back().field == "maxStak" && g.refused.back().reason.find("not a field of the schema") != std::string::npos,
          "an unknown Kiln field keeps its ordinary diagnostic rather than becoming Advanced");
}
void claims_and_binding_failure() {
    fresh_live(); RegistryLiveReply reply;
    auto a = change(71, "a", "[{" + wood + R"("op":50,"exclusive":true}])"); registry_live_commit(71, a, &reply);
    auto b = change(72, "b", "[{" + wood + R"("op":70}])"); registry_live_commit(72, b, &reply);
    check(reply.outcome == RegistryLiveOutcome::Refused && at<uint32_t>(WOOD_AT) == 50, "exclusive claims survive between entry points");
    auto partial = change(74, "partial", "[{" + wood + R"("op":70},{"registry":"loot","select":"game:material_t1_wood_logs","field":"stack.max","op":7}])");
    registry_live_commit(74, partial, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && reply.accepted == 1 && reply.refused == 1, "only the nonconflicting edit is accepted");
    registry_live_revert(71, a, &reply);
    check(at<uint32_t>(WOOD_AT) == 10, "a refused edit never activates later when its conflicting owner leaves");
    registry_live_revert(74, partial, &reply);
    auto c = change(73, "c", "[{" + wood + R"("op":75},{"registry":"items","select":"game:material_t1_wood_logs","field":"manaCost","op":2}])");
    registry_live_commit(73, c, &reply);
    check(reply.outcome == RegistryLiveOutcome::Refused && at<uint32_t>(WOOD_AT) == 10, "binding failure rolls the candidate's other fields back");
}
void mapped_reads_have_no_write_side_effect() {
    fresh_live(); put_pack(WOOD_AT, 20); std::string err; store_flush(&err);
    const auto before = store_generation(); const auto owners = store_owners(); std::string value;
    check(registry_live_read("items", "game:material_t1_wood_logs", "maxStack", &value) == KILN_OK && value == "20", "read actual old-pack value, not cached archive value");
    check(registry_live_read("recipes", "game:plank_recipe", "inputs", &value) == KILN_OK && registry::json::parse(value)[0]["count"] == 2, "read mapped recipe list");
    check(registry_live_read("items", "game:sword_t2", "manaCost", &value) == KILN_OK && registry::json::parse(value) == 10, "read mapped cost");
    check(registry_live_read("recipes", "game:plank_recipe", "inputs[0].count", &value) == KILN_OK && value == "2", "read one mapped list leaf");
    check(registry_live_read("recipes", "game:plank_recipe", "inputs[].count", &value) == KILN_OK && value == "[2]", "read a schema list-leaf column");
    check(registry_live_read("recipes", "game:plank_recipe", "inputs[99999999999999999999999999999999].count", &value) == KILN_ERR_INVALID_ARGUMENT, "index parsing is bounded before overflow");
    check(store_generation() == before && store_owners() == owners, "reads create no layers or writes");
}
void world_scope_and_admission() {
    fresh_live(); RegistryLiveReply reply;
    auto process = change(71, "a", "[{" + wood + R"("op":{"mul":3}}])", 1); registry_live_commit(71, process, &reply);
    auto scoped = change(72, "b", "[{" + wood + R"("op":70}])", 2); registry_live_commit(72, scoped, &reply);
    check(at<uint32_t>(WOOD_AT) == 70, "world-local entry point wins");
    const auto token = registry_live_status().world;
    registry_live_world_stop(); std::string err; store_reset(true, &err); world(); registry_live_world_begin(); apply_registry_files({});
    check(registry_live_status().world != token && at<uint32_t>(WOOD_AT) == 30, "new world expires only world-local changes");
    check(registry_live_revert(72, scoped, &reply) == KILN_ERR_NOT_FOUND, "old world handle never aliases a new world");
    uint64_t h = 0;
    check(registry_live_begin(0, "a", 1, &h) == KILN_ERR_INVALID_ARGUMENT, "owner zero refused");
    check(registry_live_begin(71, "a", 99, &h) == KILN_ERR_INVALID_ARGUMENT, "unknown entry point refused");
    registry_live_begin(71, "a", 4, &h);
    check(registry_live_submit(71, h, doc("other", "[]"), &reply, nullptr) == KILN_ERR_INVALID_ARGUMENT, "batch cannot impersonate another mod");
    registry_live_revert(71, h, &reply); registry_live_revert(71, process, &reply);
}
void repeated_submit_numbers_are_distinct() {
    fresh_live(); RegistryLiveReply reply;
    auto h = change(71, "a", "[{" + wood + R"("op":30}])");
    registry_live_submit(71, h, doc("a", R"([{"registry":"items","select":"game:missing","field":"maxStack","op":70}])"), &reply, nullptr);
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && reply.accepted == 1 && reply.refused == 1 && at<uint32_t>(WOOD_AT) == 30,
          "a refused edit numbered zero in a second submit cannot erase the first submit's zero");
    registry_live_revert(71, h, &reply);
}
void restart_is_refused_at_every_live_point() {
    for (uint32_t point = 1; point <= 4; ++point) {
        fresh_live(); RegistryLiveReply reply;
        const auto h = change(71, "a", R"([{"registry":"balance","select":"game:balance","field":"levelCap","op":60}])", point);
        check(registry_live_commit(71, h, &reply) == KILN_OK && reply.outcome == RegistryLiveOutcome::RestartRequired && reply.accepted == 0,
              "each later entry point refuses restart-only edits with RestartRequired");
    }
}
void refused_edits_share_change_budget() {
    fresh_live(); RegistryLiveReply reply; uint64_t h = 0;
    check(registry_live_begin(71, "a", 4, &h) == KILN_OK, "begin bounded change");
    std::string edits = "[";
    for (int i = 0; i < 4096; ++i) {
        if (i) edits += ',';
        edits += R"({"registry":"balance","select":"game:balance","field":"levelCap","op":60})";
    }
    edits += ']';
    check(registry_live_submit(71, h, doc("a", edits), &reply, nullptr) == KILN_OK && reply.refused == 4096,
          "refused edits are retained in the change's budget");
    // owner 2026-11-26: the budget is each registry's own (balance's 4,096): the next balance edit is past it and its batch is
    // refused whole, an items edit still fits items' own
    check(registry_live_submit(71, h, doc("a", R"([{"registry":"balance","select":"game:balance","field":"levelCap","op":61}])"), &reply, nullptr) == KILN_OK &&
              reply.accepted == 0 && reply.refused == 1,
          "the 4096-edit limit of balance includes its prior refused edits");
    check(registry_live_submit(71, h, doc("a", "[{" + wood + R"("op":75}])"), &reply, nullptr) == KILN_OK && reply.accepted == 1,
          "another registry's edit has its own limit");
    registry_live_revert(71, h, &reply);
}
void restart_is_per_edit_and_begin_is_bounded() {
    fresh_live(); RegistryLiveReply reply;
    auto h = change(71, "a", "[{" + wood + R"("op":75},{"registry":"balance","select":"game:balance","field":"levelCap","op":60}])", 3);
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && reply.refused == 1 && at<uint32_t>(WOOD_AT) == 75, "restart-only refusal does not silently write or discard mapped live edits");
    registry_live_publish();
    check(reported("RestartRequired"), "restart requirement is in registry.json");
    registry_live_revert(71, h, &reply);
    // review2 S4: per-owner caps below the process-wide ones, so one mod cannot starve the others
    std::vector<std::pair<KilnOwner, uint64_t>> drafts;
    for (int i = 0; i < 16; ++i) { uint64_t n = 0; check(registry_live_begin(71, "a", 4, &n) == KILN_OK, "draft slot"); drafts.push_back({71, n}); }
    check(registry_live_begin(71, "a", 4, &h) == KILN_ERR_UNAVAILABLE, "one owner's 16-draft cap is enforced");
    for (KilnOwner owner = 72; owner < 75; ++owner)
        for (int i = 0; i < 16; ++i) { uint64_t n = 0; check(registry_live_begin(owner, "b", 4, &n) == KILN_OK, "another owner still has draft slots"); drafts.push_back({owner, n}); }
    check(registry_live_begin(75, "c", 4, &h) == KILN_ERR_UNAVAILABLE, "the process-wide 64-draft bound is enforced");
    for (auto [owner, n] : drafts) registry_live_revert(owner, n, &reply);
}
// review2 S4: one owner's retained changes are capped (256) below the process-wide 1,024.
void retained_changes_are_capped_per_owner() {
    fresh_live(); RegistryLiveReply reply;
    for (unsigned i = 0; i < 256; ++i) {
        const auto h = change(71, "a", "[{" + wood + R"("op":{"add":1}}])");
        g.changes.at(h).order = ++g.next_order; // admitted without a world write each: the cap, not the store, is under test
    }
    uint64_t h = 0;
    check(registry_live_begin(71, "a", 4, &h) == KILN_ERR_UNAVAILABLE, "an owner with 256 retained changes gets no new draft");
    check(registry_live_begin(72, "b", 4, &h) == KILN_OK, "another owner is not starved");
    registry_live_revert(72, h, &reply);
}
// ---- flags (loot.rarities) and optionalEnum (loot.fixedRarity): the live back-end writes them like the other conventions ----
// The back-end suite tests what a mapped row does; regmap/common may still mark a row unproven until a lab proves its read
// time, so these cases treat both loot rows as mapped (the committed mapping's state is registry_regmap_test's).
void map_loot_rarity_rows() {
    for (const char* f : {"rarities", "fixedRarity"}) g_regmap->registries.at("loot").rows.at(f).state = registry::map::RowState::Mapped;
}
void fresh_loot() { world(); map_loot_rarity_rows(); g_files.clear(); stage({}); registry_live_world_begin(); apply_registry_files({}); }
const std::string loot_wood = R"("registry":"loot","select":"game:material_t1_wood_logs",)";
constexpr size_t MASK_AT = 0x40008, SWITCH_AT = 0x40009, FIXED_AT = 0x4000A;
std::string loot_read(const char* field) {
    std::string v;
    return registry_live_read("loot", "game:material_t1_wood_logs", field, &v) == KILN_OK ? registry::json::parse(v).dump() : "<refused>";
}
void loot_flags_read_write_revert() {
    fresh_loot(); RegistryLiveReply reply;
    check(loot_read("rarities") == R"(["common","uncommon","rare"])", "rarities reads the game's bitmask as Kiln names in the schema's order: " + loot_read("rarities"));
    const auto h = change(71, "a", "[{" + loot_wood + R"("field":"rarities","op":["legendary","epic"]}])");
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && at<uint8_t>(MASK_AT) == 0x18, "a flags set writes the game's bits (epic | legendary)");
    check(loot_read("rarities") == R"(["epic","legendary"])", "the write reads back through the schema");
    check(at<uint8_t>(SWITCH_AT) == 0 && at<uint8_t>(FIXED_AT) == 2, "a flags write touches only its own byte");
    registry_live_revert(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Reverted && at<uint8_t>(MASK_AT) == 0x07, "the revert puts the game's bits back");
}
void loot_flags_keep_bits_kiln_has_no_name_for() {
    fresh_loot(); RegistryLiveReply reply;
    mem<uint8_t>(MASK_AT, 0x87);  // bit 7: no ItemRarity value, no Kiln name
    const auto h = change(71, "a", "[{" + loot_wood + R"("field":"rarities","op":["common"]}])");
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && at<uint8_t>(MASK_AT) == 0x81, "a flags write changes only the bits Kiln names");
    registry_live_revert(71, h, &reply);
    check(at<uint8_t>(MASK_AT) == 0x87, "and reverts to every original bit");
}
void loot_flags_value_missing_on_the_build_is_refused() {
    fresh_loot(); RegistryLiveReply reply;
    auto& L = g_regs[KILN_CONTENT_LOOT];
    auto fewer = std::make_shared<EnumInfo>();
    fewer->values = {{"Common", 0}, {"Uncommon", 1}, {"Rare", 2}, {"Epic", 3}};  // a build without Legendary
    L.schema.fields[L.schema.by_path.at("rarities")].en = fewer;
    const auto h = change(71, "a", "[{" + wood + R"("op":75},{)" + loot_wood + R"("field":"rarities","op":["legendary"]}])");
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Refused && reply.reason.find("\"legendary\" is not a value of loot.rarities on this game build") != std::string::npos,
          "a name the build's enum lacks is refused, named: " + reply.reason);
    check(at<uint8_t>(MASK_AT) == 0x07 && at<uint32_t>(WOOD_AT) == 10, "nothing of the manual transaction is written");
}
void loot_flags_foreign_revert_is_skipped() {
    fresh_loot(); RegistryLiveReply reply;
    const auto h = change(71, "a", "[{" + loot_wood + R"("field":"rarities","op":["epic"]}])");
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && at<uint8_t>(MASK_AT) == 0x08, "the flags change applies");
    mem<uint8_t>(MASK_AT, 0x10);  // another writer
    registry_live_revert(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Skipped && at<uint8_t>(MASK_AT) == 0x10, "a flags revert keeps another writer's byte (compare before write)");
}
// 0.2.0-dev.32, Loot Rarity Leveler: lootRules (the world-wide rarity weights) and randomLoot (each random loot table's
// level rules and rarity weights) are plain floats and enums of resident tables, written by the generic scalar binder
constexpr size_t RULES_AT = 0x44000, TABLE_AT = 0x44100, WORLD_AT = 0x44200;
constexpr uint32_t RULES = 0x51, TABLE = 0x80943338u, WORLD_TABLE = 0x61;
void random_loot_world() {
    world();
    // the back-end, not the proof: the fixture's build record has every row of the two registries ok
    static const registry::Schema schema = registry::generated_schema();
    for (const char* r : {"lootRules", "randomLoot"})
        for (const auto& [path, spec] : schema.registry(r)->fields)
            g_regmap->build.rows[std::string(r) + "." + path] = registry::map::BuildRow::Ok;
    Registry& R = g_regs[KILN_CONTENT_LOOT_RULES];
    R.kind = KILN_CONTENT_LOOT_RULES, R.label = "loot rules", R.available = true;
    for (int i = 0; i < 5; ++i) add_field(R.schema, fld(("globalRarityDropRates[" + std::to_string(i) + "]").c_str(), 4 * i, 4, KILN_CV_FLOAT));
    add_entry(R, RULES, "loot", RULES_AT, G1);
    const float vanilla[5] = {40.59f, 27.35f, 17.06f, 9.71f, 5.29f};
    for (int i = 0; i < 5; ++i) mem<float>(RULES_AT + 4 * i, vanilla[i]);
    Registry& T = g_regs[KILN_CONTENT_RANDOM_LOOT];
    T.kind = KILN_CONTENT_RANDOM_LOOT, T.label = "random loot tables", T.available = true;
    auto mode = std::make_shared<EnumInfo>();
    mode->values = {{"Set", 0}, {"Add", 1}, {"Multiply", 2}, {"None", 3}};
    auto mode_at = [&](const std::string& path, uint32_t off) { Field m = fld(path.c_str(), off, 1, KILN_CV_INT); m.enum_kind = KILN_CONTENT_ENUM; m.en = mode; add_field(T.schema, m); };
    mode_at("modifier.level.mode", 0x00), add_field(T.schema, fld("modifier.level.value", 0x04, 4, KILN_CV_FLOAT));
    mode_at("modifier.levelOffset.mode", 0x08), add_field(T.schema, fld("modifier.levelOffset.value", 0x0C, 4, KILN_CV_FLOAT));
    add_field(T.schema, fld("modifier.levelOffsetRangeMin", 0x10, 4, KILN_CV_FLOAT));
    add_field(T.schema, fld("modifier.levelOffsetRangeMax", 0x14, 4, KILN_CV_FLOAT));
    for (int i = 0; i < 5; ++i) {
        mode_at("modifier.rarities[" + std::to_string(i) + "].mode", 0x18 + 8 * i);
        add_field(T.schema, fld(("modifier.rarities[" + std::to_string(i) + "].value").c_str(), 0x1C + 8 * i, 4, KILN_CV_FLOAT));
    }
    add_entry(T, TABLE, "labels_80943338", TABLE_AT, G2);
    add_entry(T, WORLD_TABLE, "world_509feadb_0", WORLD_AT, G3);
    for (size_t at : {TABLE_AT, WORLD_AT}) {
        mem<uint8_t>(at + 0x00, 3), mem<uint8_t>(at + 0x08, 3);  // level and offset: None
        for (int i = 0; i < 5; ++i) mem<uint8_t>(at + 0x18 + 8 * i, 3);
    }
    const float wood[5] = {59.74f, 40.26f, 0, 0, 0};
    for (int i = 0; i < 5; ++i) mem<uint8_t>(TABLE_AT + 0x18 + 8 * i, 0), mem<float>(TABLE_AT + 0x1C + 8 * i, wood[i]);  // all five Set
    g_stage_extra = [vanilla, wood](RegistryLive& in) {
        static const registry::Schema schema = registry::generated_schema();
        static const char* const rar[5] = {"common", "uncommon", "rare", "epic", "legendary"};
        registry::BaseModel::Entry r;
        r.id = "game:loot";
        for (int i = 0; i < 5; ++i) r.fields[std::string("rarityWeights.") + rar[i]] = registry::Value::number(vanilla[i]);
        in.base.registries["lootRules"].push_back(r);
        for (const char* id : {"game:labels_80943338", "game:world_509feadb_0", "game:world_509feadb_1"}) {  // _1: in the base, not in this world
            registry::BaseModel::Entry t;
            t.id = id;
            const bool chest = std::string(id) == "game:labels_80943338";
            t.fields["level.mode"] = registry::Value::text("none"), t.fields["level.value"] = registry::Value::number(0);
            t.fields["levelOffset.mode"] = registry::Value::text("none"), t.fields["levelOffset.value"] = registry::Value::number(0);
            t.fields["levelOffsetRange.min"] = registry::Value::number(0), t.fields["levelOffsetRange.max"] = registry::Value::number(0);
            for (int i = 0; i < 5; ++i) {
                t.fields[std::string("rarity.") + rar[i] + ".mode"] = registry::Value::text(chest ? "set" : "none");
                t.fields[std::string("rarity.") + rar[i] + ".weight"] = registry::Value::number(chest ? wood[i] : 0);
            }
            in.base.registries["randomLoot"].push_back(t);
        }
        for (const char* r : {"lootRules", "randomLoot"})
            for (const auto& [path, spec] : schema.registry(r)->fields) in.decoded.insert(std::string(r) + "." + path);
        in.ids["lootRules|game:loot"] = guid_text(G1), in.nums["lootRules|game:loot"] = RULES;
        in.ids["randomLoot|game:labels_80943338"] = "", in.nums["randomLoot|game:labels_80943338"] = TABLE;
        in.ids["randomLoot|game:world_509feadb_0"] = "", in.nums["randomLoot|game:world_509feadb_0"] = WORLD_TABLE;
        in.ids["randomLoot|game:world_509feadb_1"] = "", in.nums["randomLoot|game:world_509feadb_1"] = 0x62;  // listed, not in this world
    };
    g_files.clear(); stage({}); registry_live_world_begin(); apply_registry_files({});
}
std::string rl_read(const char* registry, const char* id, const char* field) {
    std::string v;
    return registry_live_read(registry, id, field, &v) == KILN_OK ? registry::json::parse(v).dump() : "<refused>";
}
const std::string chest_table = R"("registry":"randomLoot","select":"game:labels_80943338",)";
const std::string world_table = R"("registry":"randomLoot","select":"game:world_509feadb_0",)";
const std::string loot_rules = R"("registry":"lootRules","select":"game:loot",)";
void random_loot_read_write_revert() {
    random_loot_world(); RegistryLiveReply reply;
    check(rl_read("randomLoot", "game:labels_80943338", "rarity.uncommon.mode") == R"("set")" &&
              std::stod(rl_read("randomLoot", "game:labels_80943338", "rarity.uncommon.weight")) == static_cast<double>(40.26f),
          "a table reads its mode as a Kiln name and its weight: " + rl_read("randomLoot", "game:labels_80943338", "rarity.uncommon.weight"));
    // the plan of a Loot Rarity Leveler pass: a chest table's weights, a table's level rules, the world-wide weights
    const auto h = change(71, "leveler", "[{" + chest_table + R"("field":"rarity.uncommon.weight","op":80.52},{)" + chest_table +
                                              R"("field":"rarity.legendary.weight","op":124.5926},{)" + world_table + R"("field":"level.mode","op":"set"},{)" +
                                              world_table + R"("field":"level.value","op":25},{)" + world_table + R"("field":"levelOffset.mode","op":"add"},{)" +
                                              world_table + R"("field":"levelOffset.value","op":5},{)" + world_table + R"("field":"levelOffsetRange.max","op":3},{)" +
                                              loot_rules + R"("field":"rarityWeights.legendary","op":{"mul":16}}])");
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && reply.accepted == 8, "the pass applies: " + reply.reason);
    check(at<float>(TABLE_AT + 0x1C + 8 * 1) == 80.52f && at<float>(TABLE_AT + 0x1C + 8 * 4) == 124.5926f, "the chest table's weights are the f32 values written");
    check(at<uint8_t>(TABLE_AT + 0x18 + 8 * 1) == 0, "its mode stays Set");
    check(at<uint8_t>(WORLD_AT + 0x00) == 0 && at<float>(WORLD_AT + 0x04) == 25.0f && at<uint8_t>(WORLD_AT + 0x08) == 1 && at<float>(WORLD_AT + 0x0C) == 5.0f &&
              at<float>(WORLD_AT + 0x14) == 3.0f,
          "a world table's level Set 25, offset Add 5, range max 3: the game's mode numbers");
    check(at<float>(RULES_AT + 16) == 5.29f * 16, "the world-wide legendary weight x16 over the game's");
    check(rl_read("randomLoot", "game:world_509feadb_0", "levelOffset.mode") == R"("add")", "the write reads back as a Kiln name");
    registry_live_revert(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Reverted && at<float>(TABLE_AT + 0x1C + 8 * 1) == 40.26f && at<uint8_t>(WORLD_AT) == 3 &&
              at<float>(WORLD_AT + 0x04) == 0 && at<float>(RULES_AT + 16) == 5.29f,
          "the revert puts the game's values back");
}
void random_loot_foreign_and_refusals() {
    random_loot_world(); RegistryLiveReply reply;
    auto h = change(71, "leveler", "[{" + world_table + R"("field":"level.value","op":40}])");
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && at<float>(WORLD_AT + 4) == 40.0f, "applied");
    mem<float>(WORLD_AT + 4, 7.0f);  // another writer (the original mod patching the same memory)
    registry_live_revert(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Skipped && at<float>(WORLD_AT + 4) == 7.0f, "a revert keeps another writer's value (compare before write)");
    random_loot_world();
    // a table listed in the id table but not in this world, and a value outside the schema's range: refused, nothing written
    h = change(71, "leveler", R"([{"registry":"randomLoot","select":"game:world_509feadb_1","field":"level.value","op":25},{)" + world_table +
                                  R"("field":"level.value","op":30}])");
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Refused && reply.reason.find("the entry is not in this world") != std::string::npos && at<float>(WORLD_AT + 4) == 0,
          "a Manual change with a table not in this world is refused whole: " + reply.reason);
    h = change(72, "leveler", "[{" + chest_table + R"("field":"rarity.rare.weight","op":-1}])");
    registry_live_commit(72, h, &reply);
    check(reply.outcome != RegistryLiveOutcome::Applied && at<float>(TABLE_AT + 0x1C + 16) == 0, "a negative weight is outside the schema's range");
    // an entry point refuses the missing table alone and applies the other
    h = change(73, "leveler", R"([{"registry":"randomLoot","select":"game:world_509feadb_1","field":"level.value","op":25},{)" + world_table +
                                  R"("field":"level.value","op":30}])", KILN_REGISTRY_LIVE_WORLD_LOAD);
    registry_live_commit(73, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && reply.refused == 1 && at<float>(WORLD_AT + 4) == 30.0f,
          "a WorldLoad pass refuses per entry: the table in this world applies");
}
// owner 2026-11-26 ("make it a registry"): each registry's schema declares how many edits one call takes (editsPerCall;
// randomLoot 16,384 for every field of its ~800 tables, lootRules and the shipped five 4,096). A call over a registry's cap is
// refused whole, loudly (a row per registry it names, kiln.log), never cut and never thrown; the other calls apply.
std::string many(const std::string& sel, const char* field, int n, int mod = 100) {
    std::string out;
    for (int i = 0; i < n; ++i) out += "{" + sel + R"("field":")" + field + R"(","op":)" + std::to_string(i % mod) + "},";
    return out;
}
std::string list_of(std::string edits) { edits.back() = ']'; return "[" + edits; }
void edit_caps_are_per_registry() {
    static const registry::Schema schema = registry::generated_schema();
    check(schema.registry("randomLoot")->edits_per_call == 16384 && schema.registry("lootRules")->edits_per_call == 4096 &&
              schema.registry("items")->edits_per_call == 4096 && schema.registry("drops")->edits_per_call == 4096,
          "the generated schema carries each registry's cap");
    random_loot_world(); RegistryLiveReply reply;
    // under: a Loot Rarity Leveler sized call (4,000 table edits, more than the 4,096 one call took in all before, with 400
    // world-wide weights) is taken whole in one WorldLoad call
    auto h = change(71, "leveler", list_of(many(world_table, "level.value", 6000) + many(loot_rules, "rarityWeights.common", 4096, 500)),
                    KILN_REGISTRY_LIVE_WORLD_LOAD);
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && reply.accepted == 6000 + 4096 && reply.refused == 0 && at<float>(WORLD_AT + 4) == 99.0f &&
              at<float>(RULES_AT) == 95.0f,
          "6,000 randomLoot and 4,096 lootRules edits in one call: each registry within its own cap: " + reply.reason);
    registry_live_revert(71, h, &reply);
    // over: one edit past randomLoot's cap refuses the whole call, the lootRules edits in it too; nothing is written
    uint64_t hh = 0;
    check(registry_live_begin(72, "leveler", KILN_REGISTRY_LIVE_WORLD_LOAD, &hh) == KILN_OK, "begin");
    std::string report;
    check(registry_live_submit(72, hh, doc("leveler", list_of(many(world_table, "level.value", 16385) + many(loot_rules, "rarityWeights.common", 3))), &reply,
                               &report) == KILN_OK && reply.accepted == 0 && reply.refused == 2,
          "submit takes the call and refuses it whole, a row per registry: " + report);
    check(report.find("randomLoot: 16385 edits in one call, over this registry's cap of 16384 edits per call") != std::string::npos &&
              report.find("lootRules: refused with its call, which has more edits of randomLoot than the cap allows") != std::string::npos,
          "the rows name the registry over its cap, the count and the cap: " + report);
    registry_live_commit(72, hh, &reply);
    check(reply.outcome == RegistryLiveOutcome::Refused && reply.reason.find("over this registry's cap of 16384") != std::string::npos &&
              at<float>(WORLD_AT + 4) == 0 && at<float>(RULES_AT) == 40.59f,
          "the commit is refused with the reason and nothing of the call is written: " + reply.reason);
    // a registry without a declared cap keeps 4,096: 4,096 lootRules edits are taken, 4,097 refused
    h = change(73, "leveler", list_of(many(loot_rules, "rarityWeights.common", 4097, 7)));
    registry_live_commit(73, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Refused && reply.reason.find("lootRules: 4097 edits in one call, over this registry's cap of 4096") != std::string::npos &&
              at<float>(RULES_AT) == 40.59f,
          "4,097 lootRules edits: refused whole: " + reply.reason);
    // the cap counts a change's earlier batches: the batch that goes past it is refused, the earlier one stays
    check(registry_live_begin(74, "leveler", KILN_REGISTRY_LIVE_WORLD_LOAD, &hh) == KILN_OK, "begin");
    check(registry_live_submit(74, hh, doc("leveler", list_of(many(world_table, "level.value", 10000, 50))), &reply, nullptr) == KILN_OK && reply.accepted == 10000,
          "a first batch of 10,000");
    check(registry_live_submit(74, hh, doc("leveler", list_of(many(world_table, "levelOffset.value", 6385, 60))), &reply, nullptr) == KILN_OK &&
              reply.accepted == 0 && reply.refused == 1,
          "a second batch that would make 16,385: refused");
    registry_live_commit(74, hh, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && at<float>(WORLD_AT + 4) == 49.0f && at<float>(WORLD_AT + 0x0C) == 0,
          "the first batch applies, nothing of the refused one: " + reply.reason);
    registry_live_revert(74, hh, &reply);
}
// The cap's cost (owner: check memory and the world thread's time before raising it): every field of every table of the real
// data's size (797 tables x 16 fields = 12,752 edits) in one WorldLoad call, submitted and committed through the real back-end.
void a_full_random_loot_call_has_bounded_cost() {
    random_loot_world();
    constexpr size_t kTables = 797, kAt = 0xA0000, kStride = 0x40;
    Registry& T = g_regs[KILN_CONTENT_RANDOM_LOOT];
    RegistryLive installed = *g.in;
    const registry::BaseModel::Entry model = installed.base.registries.at("randomLoot").back();
    std::string edits;
    static const char* const fields[] = {"level.value", "levelOffset.value", "levelOffsetRange.min", "levelOffsetRange.max"};
    static const char* const rar[5] = {"common", "uncommon", "rare", "epic", "legendary"};
    for (size_t i = 0; i < kTables; ++i) {
        const std::string name = "world_cost_" + std::to_string(i), id = "game:" + name;
        const size_t at = kAt + i * kStride;
        add_entry(T, static_cast<uint32_t>(0x10000 + i), name.c_str(), at, G3);
        mem<uint8_t>(at + 0x00, 3), mem<uint8_t>(at + 0x08, 3);
        for (int r = 0; r < 5; ++r) mem<uint8_t>(at + 0x18 + 8 * r, 3);
        auto entry = model;
        entry.id = id;
        installed.base.registries["randomLoot"].push_back(std::move(entry));
        installed.ids["randomLoot|" + id] = "", installed.nums["randomLoot|" + id] = static_cast<uint32_t>(0x10000 + i);
        const std::string sel = R"("registry":"randomLoot","select":")" + id + R"(",)";
        edits += "{" + sel + R"("field":"level.mode","op":"set"},{)" + sel + R"("field":"levelOffset.mode","op":"add"},)";
        for (const char* f : fields) edits += "{" + sel + R"("field":")" + f + R"(","op":3},)";
        for (const char* r : rar)
            edits += "{" + sel + R"("field":"rarity.)" + r + R"(.mode","op":"multiply"},{)" + sel + R"("field":"rarity.)" + r + R"(.weight","op":2},)";
    }
    registry_live_install(std::move(installed)); registry_live_world_begin(); apply_registry_files({});
    const std::string ir = doc("leveler", list_of(edits));
    RegistryLiveReply reply; uint64_t h = 0;
    const auto start = std::chrono::steady_clock::now();
    check(registry_live_begin(71, "leveler", KILN_REGISTRY_LIVE_WORLD_LOAD, &h) == KILN_OK, "begin");
    check(registry_live_submit(71, h, ir, &reply, nullptr) == KILN_OK && reply.accepted == kTables * 16, "12,752 edits taken in one call");
    const auto submitted = std::chrono::steady_clock::now();
    registry_live_commit(71, h, &reply);
    const auto end = std::chrono::steady_clock::now();
    const double sub = std::chrono::duration<double, std::milli>(submitted - start).count(), com = std::chrono::duration<double, std::milli>(end - submitted).count();
    std::printf("randomLoot: %zu edits (%zu tables x 16 fields, %zu KiB of IR) in one call: submit %.2f ms, commit %.2f ms\n", kTables * 16, kTables, ir.size() / 1024,
                sub, com);
    check(reply.outcome == RegistryLiveOutcome::Applied && reply.accepted == kTables * 16 && at<uint8_t>(kAt + 0x08 + 796 * kStride) == 1 &&
              at<float>(kAt + 0x14 + 796 * kStride) == 3.0f,
          "every field of every table written: " + reply.reason);
    check(sub + com < 2000, "a full randomLoot call stays within the 2,000 ms sanitizer-host budget");
    registry_live_revert(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Reverted && at<uint8_t>(kAt + 0x08 + 796 * kStride) == 3, "and reverts");
}
// review-registry-proof nit: a read-only bitmask is refused as read-only, not as "is not a bitmask"
void loot_flags_read_only_is_refused_as_read_only() {
    fresh_loot(); RegistryLiveReply reply;
    auto& L = g_regs[KILN_CONTENT_LOOT];
    Field& mask = L.schema.fields[L.schema.by_path.at("rarities")];
    mask.writable = false;
    auto h = change(71, "a", "[{" + loot_wood + R"("field":"rarities","op":["epic"]}])");
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Refused && reply.reason.find("is read-only on this game build") != std::string::npos &&
              reply.reason.find("is not a bitmask") == std::string::npos,
          "a read-only bitmask says read-only: " + reply.reason);
    check(at<uint8_t>(MASK_AT) == 0x07, "nothing is written");
    fresh_loot();
    Field& plain = g_regs[KILN_CONTENT_LOOT].schema.fields[g_regs[KILN_CONTENT_LOOT].schema.by_path.at("rarities")];
    plain.enum_kind = KILN_CONTENT_ENUM;  // a build where the member is a plain enum
    h = change(71, "a", "[{" + loot_wood + R"("field":"rarities","op":["epic"]}])");
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Refused && reply.reason.find("is not a bitmask on this game build") != std::string::npos,
          "a member that is no bitmask says so: " + reply.reason);
}
void loot_fixed_rarity_read_only_is_refused_as_read_only() {
    fresh_loot(); RegistryLiveReply reply;
    auto& L = g_regs[KILN_CONTENT_LOOT];
    L.schema.fields[L.schema.by_path.at("uiRarity")].writable = false;
    const auto h = change(71, "a", "[{" + loot_wood + R"("field":"fixedRarity","op":"epic"}])");
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Refused && reply.reason.find("read-only on this game build") != std::string::npos &&
              reply.reason.find("are not a switch and an enum") == std::string::npos,
          "a read-only switch or enum says read-only: " + reply.reason);
}
// registry-fixes item 5 (registry-hygiene "before release"): live refusals named the game's members ("the game member
// maxStackSize is not writable on this game build"). Mods and players see Kiln names, never the game's members (owner's
// principle): a refusal names the Kiln field and entry; kiln.log keeps the regmap path at debug level for maintainers.
bool names_no_member(const std::string& reason, std::initializer_list<const char*> members) {
    if (reason.find("game member") != std::string::npos) return false;
    for (const char* m : members)
        if (reason.find(m) != std::string::npos) return false;
    return true;
}
bool debug_names(const std::string& member) {
    return std::any_of(g_log_lines.begin(), g_log_lines.end(), [&](const std::string& l) {
        return l.rfind(std::to_string(KILN_LOG_DEBUG) + " ", 0) == 0 && l.find(member) != std::string::npos;
    });
}
void live_refusals_name_kiln_fields_never_game_members() {
    RegistryLiveReply reply;
    auto refused = [&](const std::string& what, std::initializer_list<const char*> members, const std::string& place, const std::string& says) {
        check(reply.outcome == RegistryLiveOutcome::Refused && reply.reason.find(place) != std::string::npos &&
                  reply.reason.find(says) != std::string::npos && names_no_member(reply.reason, members),
              what + ": the refusal names the Kiln field, never the game's member: " + reply.reason);
    };
    // a scalar the build does not let Kiln write
    fresh_live(); g_log_lines.clear();
    g_regs[KILN_CONTENT_ITEMS].schema.fields[g_regs[KILN_CONTENT_ITEMS].schema.by_path.at("maxStackSize")].writable = false;
    registry_live_commit(71, change(71, "a", "[{" + wood + R"("op":50}])"), &reply);
    refused("not writable", {"maxStackSize"}, "items[game:material_t1_wood_logs].maxStack", "is not writable on this game build");
    check(debug_names("maxStackSize"), "not writable: kiln.log names the regmap path at debug level, for maintainers");
    // a scalar the build does not have
    fresh_live();
    {
        auto& sc = g_regs[KILN_CONTENT_ITEMS].schema;
        sc.by_path.erase("maxStackSize"), sc.by_lower.erase("maxstacksize");
    }
    registry_live_commit(71, change(71, "a", "[{" + wood + R"("op":50}])"), &reply);
    refused("missing", {"maxStackSize"}, "items[game:material_t1_wood_logs].maxStack", "is missing on this game build");
    // a value its storage cannot hold
    fresh_live();
    {
        Field& f = g_regs[KILN_CONTENT_ITEMS].schema.fields[g_regs[KILN_CONTENT_ITEMS].schema.by_path.at("maxStackSize")];
        f.bytes = 1;  // a build that keeps max stacks in one byte
    }
    registry_live_commit(71, change(71, "a", "[{" + wood + R"("op":300}])"), &reply);
    refused("does not fit", {"maxStackSize"}, "items[game:material_t1_wood_logs].maxStack", "does not fit");
    // a formula whose input the build does not have
    fresh_live();
    {
        auto& sc = g_regs[KILN_CONTENT_ITEMS].schema;
        sc.by_path.erase("damageSetup.speedMod"), sc.by_lower.erase("damagesetup.speedmod");
    }
    registry_live_commit(71, change(71, "a", R"([{"registry":"items","select":"game:sword_t2","field":"power","op":{"mul":2}}])"), &reply);
    refused("formula input", {"damageSetup", "speedMod"}, "items[game:sword_t2].power", "is missing on this game build");
    // the optional enum: missing, not a switch and an enum, read-only
    fresh_loot();
    {
        auto& sc = g_regs[KILN_CONTENT_LOOT].schema;
        sc.by_path.erase("uiRarity"), sc.by_lower.erase("uirarity");
    }
    registry_live_commit(71, change(71, "a", "[{" + loot_wood + R"("field":"fixedRarity","op":"epic"}])"), &reply);
    refused("optional enum missing", {"uiRarity", "useUiRarity"}, "loot[game:material_t1_wood_logs].fixedRarity", "is missing on this game build");
    fresh_loot();
    g_regs[KILN_CONTENT_LOOT].schema.fields[g_regs[KILN_CONTENT_LOOT].schema.by_path.at("useUiRarity")].vt = KILN_CV_INT;
    registry_live_commit(71, change(71, "a", "[{" + loot_wood + R"("field":"fixedRarity","op":"epic"}])"), &reply);
    refused("optional enum shape", {"uiRarity", "useUiRarity"}, "loot[game:material_t1_wood_logs].fixedRarity", "are not a switch and an enum on this game build");
    fresh_loot();
    g_regs[KILN_CONTENT_LOOT].schema.fields[g_regs[KILN_CONTENT_LOOT].schema.by_path.at("useUiRarity")].writable = false;
    registry_live_commit(71, change(71, "a", "[{" + loot_wood + R"("field":"fixedRarity","op":"epic"}])"), &reply);
    refused("optional enum read-only", {"uiRarity", "useUiRarity"}, "loot[game:material_t1_wood_logs].fixedRarity", "is read-only on this game build");
    // review-registry-fixes: the perk slots the build does not have (refList: references and ids in step)
    fresh_live(); g_log_lines.clear();
    {
        auto& sc = g_regs[KILN_CONTENT_ITEMS].schema;
        sc.by_path.erase("perkIds[0]"), sc.by_lower.erase("perkids[0]");
    }
    registry_live_commit(71, change(71, "a", R"([{"registry":"items","select":"game:material_t1_wood_logs","field":"perks","list":{"append":["game:perk_b"]}}])"), &reply);
    refused("perk slots", {"perkReferences", "perkIds"}, "items[game:material_t1_wood_logs].perks", "have no perk slots Kiln can write");
    check(debug_names("perkIds"), "perk slots: kiln.log names the game's members at debug level, for maintainers");
}
void loot_fixed_rarity_read_write_revert() {
    fresh_loot(); RegistryLiveReply reply;
    check(loot_read("fixedRarity") == R"("none")", "the switch off reads as none (the enum below it is the game's own)");
    const auto a = change(71, "a", "[{" + loot_wood + R"("field":"fixedRarity","op":"epic"}])");
    registry_live_commit(71, a, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && at<uint8_t>(SWITCH_AT) == 1 && at<uint8_t>(FIXED_AT) == 3, "a name turns the switch on and writes the enum");
    check(loot_read("fixedRarity") == R"("epic")" && at<uint8_t>(MASK_AT) == 0x07, "it reads back; the mask is untouched");
    const auto b = change(72, "b", "[{" + loot_wood + R"("field":"fixedRarity","op":"none"}])");
    registry_live_commit(72, b, &reply);
    // live changes compose into one registry layer per slot over the bytes below the registry: none keeps the game's own enum
    check(reply.outcome == RegistryLiveOutcome::Applied && at<uint8_t>(SWITCH_AT) == 0 && at<uint8_t>(FIXED_AT) == 2,
          "a later none turns the switch off and leaves the game's own enum below it");
    check(loot_read("fixedRarity") == R"("none")", "and reads none");
    registry_live_revert(72, b, &reply);
    check(at<uint8_t>(SWITCH_AT) == 1 && at<uint8_t>(FIXED_AT) == 3 && loot_read("fixedRarity") == R"("epic")", "reverting the later change restores the earlier one");
    registry_live_revert(71, a, &reply);
    check(reply.outcome == RegistryLiveOutcome::Reverted && at<uint8_t>(SWITCH_AT) == 0 && at<uint8_t>(FIXED_AT) == 2, "both slots back to the game's bytes");
}
void loot_fixed_rarity_none_keeps_the_games_enum() {
    fresh_loot(); RegistryLiveReply reply;
    mem<uint8_t>(SWITCH_AT, 1), mem<uint8_t>(FIXED_AT, 4);
    check(loot_read("fixedRarity") == R"("legendary")", "a game entry with a fixed rarity reads its name");
    const auto h = change(71, "a", "[{" + loot_wood + R"("field":"fixedRarity","op":"none"}])");
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && at<uint8_t>(SWITCH_AT) == 0 && at<uint8_t>(FIXED_AT) == 4, "none turns the switch off only");
    registry_live_revert(71, h, &reply);
    check(at<uint8_t>(SWITCH_AT) == 1 && at<uint8_t>(FIXED_AT) == 4, "the revert turns it on again");
}
void loot_fixed_rarity_value_missing_on_the_build_is_refused() {
    fresh_loot(); RegistryLiveReply reply;
    auto& L = g_regs[KILN_CONTENT_LOOT];
    auto fewer = std::make_shared<EnumInfo>();
    fewer->values = {{"Common", 0}, {"Uncommon", 1}, {"Rare", 2}, {"Epic", 3}};
    L.schema.fields[L.schema.by_path.at("uiRarity")].en = fewer;
    const auto h = change(71, "a", "[{" + loot_wood + R"("field":"fixedRarity","op":"legendary"}])");
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Refused && reply.reason.find("\"legendary\" is not a value of loot.fixedRarity on this game build") != std::string::npos,
          "a name the build's enum lacks is refused, named: " + reply.reason);
    check(at<uint8_t>(SWITCH_AT) == 0 && at<uint8_t>(FIXED_AT) == 2, "neither slot is written");
}
void loot_fixed_rarity_foreign_revert_is_skipped() {
    fresh_loot(); RegistryLiveReply reply;
    const auto h = change(71, "a", "[{" + loot_wood + R"("field":"fixedRarity","op":"rare"}])");
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && at<uint8_t>(SWITCH_AT) == 1 && at<uint8_t>(FIXED_AT) == 2, "the change applies");
    mem<uint8_t>(FIXED_AT, 1);  // another writer changes the enum
    registry_live_revert(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Skipped && at<uint8_t>(FIXED_AT) == 1, "the revert keeps another writer's enum byte");
}
void loot_fixed_rarity_write_failure_rolls_back() {
    fresh_loot(); RegistryLiveReply reply;
    const auto h = change(71, "a", "[{" + loot_wood + R"("field":"fixedRarity","op":"epic"}])");
    g_fail_addrs = {A(FIXED_AT)};  // the enum's write fails, whatever order the slots are written in
    registry_live_commit(71, h, &reply);
    g_fail_addrs.clear();
    check(reply.outcome == RegistryLiveOutcome::Refused && at<uint8_t>(SWITCH_AT) == 0 && at<uint8_t>(FIXED_AT) == 2,
          "a half-written optional enum is rolled back whole: " + reply.reason);
}
// review registry-proof: a roll reads useUiRarity and then uiRarity on a game thread while Kiln writes them (the real
// write_batch goes in address order: the switch +5 before the enum +6). Turning a fixed rarity on lands the enum before the
// switch, turning it off the switch before the enum: after no single write is the switch on over an enum it was not set with.
void loot_fixed_rarity_switch_never_on_over_a_stale_enum() {
    fresh_loot(); RegistryLiveReply reply;
    std::string bad;
    int allowed = 3;  // the enum the switch may be on over while it is on (-1: the switch must stay off)
    g_after_each_write = [&] {
        if (at<uint8_t>(SWITCH_AT) && at<uint8_t>(FIXED_AT) != allowed) bad += " [on over " + std::to_string(at<uint8_t>(FIXED_AT)) + "]";
    };
    const auto a = change(71, "a", "[{" + loot_wood + R"("field":"fixedRarity","op":"epic"}])");
    registry_live_commit(71, a, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && at<uint8_t>(SWITCH_AT) == 1 && at<uint8_t>(FIXED_AT) == 3 && bad.empty(),
          "turning a fixed rarity on writes the enum before the switch:" + bad);
    const auto b = change(72, "b", "[{" + loot_wood + R"("field":"fixedRarity","op":"none"}])");
    registry_live_commit(72, b, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && at<uint8_t>(SWITCH_AT) == 0 && at<uint8_t>(FIXED_AT) == 2 && bad.empty(),
          "turning it off writes the switch before the enum goes back to the game's:" + bad);
    registry_live_revert(72, b, &reply);
    check(at<uint8_t>(SWITCH_AT) == 1 && at<uint8_t>(FIXED_AT) == 3 && bad.empty(), "a revert that turns it on again writes the enum first:" + bad);
    registry_live_revert(71, a, &reply);
    check(at<uint8_t>(SWITCH_AT) == 0 && at<uint8_t>(FIXED_AT) == 2 && bad.empty(), "the last revert turns the switch off first:" + bad);
    allowed = -1;
    const auto c = change(73, "c", "[{" + loot_wood + R"("field":"fixedRarity","op":"legendary"}])");
    g_fail_addrs = {A(FIXED_AT)};  // the enum's write fails
    registry_live_commit(73, c, &reply);
    g_fail_addrs.clear();
    check(reply.outcome == RegistryLiveOutcome::Refused && at<uint8_t>(SWITCH_AT) == 0 && at<uint8_t>(FIXED_AT) == 2 && bad.empty(),
          "a failed enum write never turns the switch on over the old enum:" + bad);
    allowed = 4;
    const auto d = change(74, "d", "[{" + loot_wood + R"("field":"fixedRarity","op":"legendary"}])");
    g_fail_addrs = {A(SWITCH_AT)};  // the switch's write fails after the enum landed
    registry_live_commit(74, d, &reply);
    g_fail_addrs.clear();
    check(reply.outcome == RegistryLiveOutcome::Refused && at<uint8_t>(SWITCH_AT) == 0 && at<uint8_t>(FIXED_AT) == 2 && bad.empty(),
          "a failed switch write leaves the switch off and the enum is rolled back:" + bad);
    g_after_each_write = {};
}
void staged_loot_flags_and_fixed_rarity_bind() {
    world(); map_loot_rarity_rows();
    g_files = {{"a/content/loot.json", R"({"registry":{"alter":[{"registry":"loot","select":"game:material_t1_wood_logs","field":"rarities","op":["rare"]},)"
                                       R"({"registry":"loot","select":"game:material_t1_wood_logs","field":"fixedRarity","op":"legendary"}]}})"}};
    const auto files = files_of({{"a", "a/content/loot.json"}});
    stage(files); registry_live_world_begin(); apply_registry_files(files);
    check(at<uint8_t>(MASK_AT) == 0x04 && at<uint8_t>(SWITCH_AT) == 1 && at<uint8_t>(FIXED_AT) == 4, "start-time flags and optional enum edits bind at world load");
    check(!reported("does not write"), "nothing is refused for its convention");
}
}
int main() {
    g_registry_stack_guard = &fake_guard;  // the host has no game code to patch: the guard as on a build that has it
    std::vector<std::string> errs; g_regmap = std::make_shared<registry::map::RegMap>();
    if (!registry::map::load_dir(KILN_REGMAP_DIR, "server", "f0d824be7fcd48ebfe2458ff5c28da26060c2074e3ec1fcac587e5a1d508aeb2", g_regmap.get(), &errs)) return 2;
    int failed = 0;
    const std::pair<const char*, void(*)()> tests[] = {
        {"packs_code_and_out_of_order_revert", packs_code_and_out_of_order_revert},
        {"unrelated_pack_binding_failure_isolated", unrelated_pack_binding_failure_isolated},
        {"unrelated_live_binding_failure_isolated", unrelated_live_binding_failure_isolated},
        {"pending_binding_failure_drops_only_that_entry", pending_binding_failure_drops_only_that_entry},
        {"carried_binding_failure_drops_only_that_entry", carried_binding_failure_drops_only_that_entry},
        {"reapplied_binding_failure_is_atomic", reapplied_binding_failure_is_atomic},
        {"pending_evaluation_failure_is_atomic", pending_evaluation_failure_is_atomic},
        {"pending_write_failure_is_rolled_back", pending_write_failure_is_rolled_back},
        {"commit_does_not_reenter_ready", commit_does_not_reenter_ready},
        {"revert_defers_its_snapshot", revert_defers_its_snapshot},
        {"nested_transactions_are_refused", nested_transactions_are_refused},
        {"wrong_world_thread_is_refused_under_lock", wrong_world_thread_is_refused_under_lock},
        {"stopped_world_uses_only_copied_data", stopped_world_uses_only_copied_data},
        {"final_then_later_alter_obeys_commit_order", final_then_later_alter_obeys_commit_order},
        {"prepared_turn_still_checks_references", prepared_turn_still_checks_references},
        {"retained_changes_have_bounded_commit_cost", retained_changes_have_bounded_commit_cost},
        {"begin_prepares_copied_base_once", begin_prepares_copied_base_once},
        {"read_can_be_first_registry_use", read_can_be_first_registry_use},
        {"unavailable_preparation_remains_retryable", unavailable_preparation_remains_retryable},
        {"fractional_integer_mana_refused", [] { fractional_integer_cost_is_refused("manaCost"); }},
        {"fractional_integer_stamina_refused", [] { fractional_integer_cost_is_refused("staminaCost"); }},
        {"whole_and_float_costs_still_work", whole_and_float_costs_still_work},
        {"staged_fractional_integer_cost_is_refused", staged_fractional_integer_cost_is_refused},
        {"pending_fractional_integer_cost_is_refused", pending_fractional_integer_cost_is_refused},
        {"flattened_socket_read_write_revert", flattened_socket_read_write_revert},
        {"staged_flattened_socket_binds", staged_flattened_socket_binds},
        {"flattened_socket_foreign_revert_is_skipped", flattened_socket_foreign_revert_is_skipped},
        {"unlock_read_has_no_write_side_effect", unlock_read_has_no_write_side_effect},
        {"unlock_write_read_revert", unlock_write_read_revert},
        {"unlock_foreign_revert_is_skipped", unlock_foreign_revert_is_skipped},
        {"live_raw_refusals_keep_place_metadata", live_raw_refusals_keep_place_metadata},
        {"raw_refusal_marker_does_not_depend_on_place_text", raw_refusal_marker_does_not_depend_on_place_text},
        {"raw_evaluation_refusal_is_marked_but_unknown_kiln_field_is_not", raw_evaluation_refusal_is_marked_but_unknown_kiln_field_is_not},
        {"claims_and_binding_failure", claims_and_binding_failure},
        {"mapped_reads_have_no_write_side_effect", mapped_reads_have_no_write_side_effect},
        {"world_scope_and_admission", world_scope_and_admission},
        {"repeated_submit_numbers_are_distinct", repeated_submit_numbers_are_distinct},
        {"restart_is_refused_at_every_live_point", restart_is_refused_at_every_live_point},
        {"refused_edits_share_change_budget", refused_edits_share_change_budget},
        {"restart_is_per_edit_and_begin_is_bounded", restart_is_per_edit_and_begin_is_bounded},
        {"candidate_write_failure_keeps_other_owners", candidate_write_failure_keeps_other_owners},
        {"unrelated_application_write_failure_keeps_live", unrelated_application_write_failure_keeps_live},
        {"rejected_rows_survive_the_rollback_pass", rejected_rows_survive_the_rollback_pass},
        {"multi_change_write_failure_is_attributed_per_change", multi_change_write_failure_is_attributed_per_change},
        {"rejected_process_change_returns_next_world", rejected_process_change_returns_next_world},
        {"foreign_slot_keeps_the_rest_of_a_retained_change", foreign_slot_keeps_the_rest_of_a_retained_change},
        {"pass_read_excludes_own_layers", pass_read_excludes_own_layers},

        {"live_read_of_a_missing_entry_is_not_found", live_read_of_a_missing_entry_is_not_found},
        {"world_stop_reports_without_its_live_rows", world_stop_reports_without_its_live_rows},

        {"drop_table_change_is_pending_resource", drop_table_change_is_pending_resource},
        {"unrelated_pending_table_keeps_a_change_applied", unrelated_pending_table_keeps_a_change_applied},
        {"unloaded_drop_table_reads_the_value_it_loads_with", unloaded_drop_table_reads_the_value_it_loads_with},
        {"formula_transaction_with_foreign_write", formula_transaction_with_foreign_write},
        {"draft_cannot_cross_worlds", draft_cannot_cross_worlds},
        {"process_change_reverts_without_a_world", process_change_reverts_without_a_world},
        {"retained_changes_are_capped_per_owner", retained_changes_are_capped_per_owner},

        {"world_load_field_mid_world_is_named", world_load_field_mid_world_is_named},
        {"entry_point_inexact_cost_drops_only_that_entry", entry_point_inexact_cost_drops_only_that_entry},
        {"entry_point_selector_refuses_per_entry", entry_point_selector_refuses_per_entry},
        {"selector_conflict_refuses_one_entry_at_entry_points", selector_conflict_refuses_one_entry_at_entry_points},
        {"loot_flags_read_write_revert", loot_flags_read_write_revert},
        {"random_loot_read_write_revert", random_loot_read_write_revert},
        {"random_loot_foreign_and_refusals", random_loot_foreign_and_refusals},
        {"edit_caps_are_per_registry", edit_caps_are_per_registry},
        {"a_full_random_loot_call_has_bounded_cost", a_full_random_loot_call_has_bounded_cost},
        {"loot_flags_keep_bits_kiln_has_no_name_for", loot_flags_keep_bits_kiln_has_no_name_for},
        {"loot_flags_value_missing_on_the_build_is_refused", loot_flags_value_missing_on_the_build_is_refused},
        {"loot_flags_foreign_revert_is_skipped", loot_flags_foreign_revert_is_skipped},
        {"loot_flags_read_only_is_refused_as_read_only", loot_flags_read_only_is_refused_as_read_only},
        {"loot_fixed_rarity_read_only_is_refused_as_read_only", loot_fixed_rarity_read_only_is_refused_as_read_only},
        {"live_refusals_name_kiln_fields_never_game_members", live_refusals_name_kiln_fields_never_game_members},
        {"loot_fixed_rarity_read_write_revert", loot_fixed_rarity_read_write_revert},
        {"loot_fixed_rarity_none_keeps_the_games_enum", loot_fixed_rarity_none_keeps_the_games_enum},
        {"loot_fixed_rarity_value_missing_on_the_build_is_refused", loot_fixed_rarity_value_missing_on_the_build_is_refused},
        {"loot_fixed_rarity_foreign_revert_is_skipped", loot_fixed_rarity_foreign_revert_is_skipped},
        {"loot_fixed_rarity_write_failure_rolls_back", loot_fixed_rarity_write_failure_rolls_back},
        {"loot_fixed_rarity_switch_never_on_over_a_stale_enum", loot_fixed_rarity_switch_never_on_over_a_stale_enum},
        {"staged_loot_flags_and_fixed_rarity_bind", staged_loot_flags_and_fixed_rarity_bind}};
    for (auto [name, fn] : tests) try { fn(); std::printf("ok %s\n", name); } catch (const std::exception& e) { ++failed; std::printf("FAIL %s: %s\n", name, e.what()); }
    std::printf("registry live transactions: %d checks, %d failed groups\n", checks, failed);
    return failed ? 1 : 0;
}
