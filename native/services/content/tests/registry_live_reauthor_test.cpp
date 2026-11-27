// SPDX-License-Identifier: MIT
// Registry.Reauthor off the world tick (dev.32 LOOT-2): a re-authored pass replaces the layers its point made before in one
// turn of the live back-end (one composition, one write of the bytes that change), and the turn a pass prepared off the world
// thread lands on it in a few milliseconds. The real store and binders over the made-up memory of registry_live_test.cpp and
// the loot tables of registry_live_transactions_test.cpp; no game process.
#include <thread>
#define REGISTRY_LIVE_TRANSACTIONS_NO_MAIN
#include "registry_live_transactions_test.cpp"

namespace {

uint64_t draft(KilnOwner owner, const std::string& mod, const std::string& edits) {
    return change(owner, mod, edits, KILN_REGISTRY_LIVE_WORLD_LOAD);
}
std::vector<RegistryLiveReply> replace(const std::vector<std::pair<KilnOwner, uint64_t>>& commit,
                                       const std::vector<std::pair<KilnOwner, uint64_t>>& replaced, KilnResult* rc = nullptr) {
    RegistryLiveTurn turn;
    turn.commit = commit, turn.replaced = replaced;
    std::vector<RegistryLiveReply> replies;
    const KilnResult r = registry_live_replace(turn, &replies);
    if (rc) *rc = r;
    else check(r == KILN_OK, "replace returns KILN_OK (" + std::to_string(r) + ")");
    return replies;
}
const std::string level_set = "{" + world_table + R"("field":"level.mode","op":"set"},)";
std::string level(int value) { return "{" + world_table + R"("field":"level.value","op":)" + std::to_string(value) + "}"; }
std::string uncommon(int value) { return "{" + chest_table + R"("field":"rarity.uncommon.weight","op":)" + std::to_string(value) + "}"; }

// One turn: the new pass's layers in and the earlier pass's out, composed once and written in one batch; a field only the
// earlier pass edited goes back to the game's value in the same batch, and no moment shows the field without a value of the mod.
void a_replace_is_one_turn_and_one_write() {
    random_loot_world(); RegistryLiveReply reply;
    const uint64_t h1 = draft(71, "leveler", "[" + level_set + level(30) + "," + uncommon(50) + "]");
    registry_live_commit(71, h1, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && at<float>(WORLD_AT + 4) == 30.0f && at<float>(TABLE_AT + 0x24) == 50.0f,
          "the earlier pass applied: " + reply.reason);
    const uint64_t h2 = draft(71, "leveler", "[" + level_set + level(40) + "]");
    std::vector<float> levels;  // the level after each single write (what a reader between two writes would see)
    g_after_each_write = [&] { levels.push_back(at<float>(WORLD_AT + 4)); };
    const size_t batches = g_write_batches;
    const auto replies = replace({{71, h2}}, {{71, h1}});
    g_after_each_write = {};
    check(replies.size() == 2 && replies[0].outcome == RegistryLiveOutcome::Applied && replies[0].accepted == 2 &&
              replies[1].outcome == RegistryLiveOutcome::Reverted,
          "the new pass applies and the earlier one is reverted: " + replies[0].reason + " / " + replies[1].reason);
    check(at<float>(WORLD_AT + 4) == 40.0f && at<uint8_t>(WORLD_AT) == 0 && at<float>(TABLE_AT + 0x24) == 40.26f,
          "the new value, and the chest table's weight only the earlier pass edited back to the game's");
    check(g_write_batches - batches == 1, "one write batch for the turn (was a commit's and a revert's): " + std::to_string(g_write_batches - batches));
    check(levels.size() == 2 && std::none_of(levels.begin(), levels.end(), [](float v) { return v != 30.0f && v != 40.0f; }),
          "two writes (the level and the weight; the unchanged mode is not written), and the level is never without the mod's value");
    check(registry_live_revert(71, h1, &reply) == KILN_ERR_NOT_FOUND && registry_live_status().active == 1,
          "the earlier change is gone, the new one retained");
    registry_live_publish();
    const auto field = g_last_report.fields.find({"randomLoot", "game:world_509feadb_0", "level.value"});
    check(field != g_last_report.fields.end() &&
              std::count_if(field->second.chain.begin(), field->second.chain.end(), [](const registry::ChainRow& r) { return r.mod == "leveler"; }) == 1 &&
              std::any_of(field->second.chain.begin(), field->second.chain.end(),
                          [&](const registry::ChainRow& r) { return r.source.rfind("live:" + std::to_string(h2) + ":", 0) == 0; }),
          "registry.json names the new change once, not the earlier one");
    check(registry_live_revert(71, h2, &reply) == KILN_OK && at<float>(WORLD_AT + 4) == 0 && at<uint8_t>(WORLD_AT) == 3, "and it reverts");
}
// The new pass is the newest layer: it wins over another mod's change committed after the earlier pass (later wins), and that
// change stays.
void a_replace_lands_after_every_other_change() {
    random_loot_world(); RegistryLiveReply reply;
    const uint64_t h1 = draft(71, "leveler", "[" + level_set + level(30) + "]");
    registry_live_commit(71, h1, &reply);
    const uint64_t other = draft(72, "other", "[" + level_set + level(35) + "]");
    registry_live_commit(72, other, &reply);
    check(at<float>(WORLD_AT + 4) == 35.0f, "the other mod's later change wins over the earlier pass");
    const uint64_t h2 = draft(71, "leveler", "[" + level_set + level(40) + "]");
    const auto replies = replace({{71, h2}}, {{71, h1}});
    check(replies[0].outcome == RegistryLiveOutcome::Applied && at<float>(WORLD_AT + 4) == 40.0f, "the re-authored pass is newest and wins");
    check(registry_live_revert(71, h2, &reply) == KILN_OK && at<float>(WORLD_AT + 4) == 35.0f, "the other mod's change is still there below it");
    registry_live_revert(72, other, &reply);
}
// A pass's phases are a turn's candidates in order (its Alter, then its Final): the later one wins where both edit.
void a_turn_commits_its_candidates_in_order() {
    random_loot_world(); RegistryLiveReply reply;
    const uint64_t h1 = draft(71, "leveler", "[" + level_set + level(30) + "]");
    registry_live_commit(71, h1, &reply);
    const uint64_t alter = draft(71, "leveler", "[" + level_set + level(40) + "," + uncommon(45) + "]");
    const uint64_t final = draft(71, "leveler", "[" + level(41) + "]");
    const auto replies = replace({{71, alter}, {71, final}}, {{71, h1}});
    check(replies.size() == 3 && replies[0].outcome == RegistryLiveOutcome::Applied && replies[1].outcome == RegistryLiveOutcome::Applied &&
              replies[2].outcome == RegistryLiveOutcome::Reverted,
          "both phases apply and the earlier layer is reverted");
    check(at<float>(WORLD_AT + 4) == 41.0f && at<float>(TABLE_AT + 0x24) == 45.0f, "the Final candidate is committed after the Alter one");
    registry_live_revert(71, final, &reply), registry_live_revert(71, alter, &reply);
    check(at<float>(WORLD_AT + 4) == 0 && at<float>(TABLE_AT + 0x24) == 40.26f, "and both revert");
}
// A pass that makes no edits (the mod switched off) is a turn with nothing to commit: the earlier layers come out at once.
void a_turn_with_nothing_to_commit_takes_the_earlier_layers_out() {
    random_loot_world(); RegistryLiveReply reply;
    const uint64_t h1 = draft(71, "leveler", "[" + level_set + level(30) + "," + uncommon(50) + "]");
    registry_live_commit(71, h1, &reply);
    const size_t batches = g_write_batches;
    const auto replies = replace({}, {{71, h1}});
    check(replies.size() == 1 && replies[0].outcome == RegistryLiveOutcome::Reverted && at<float>(WORLD_AT + 4) == 0 &&
              at<uint8_t>(WORLD_AT) == 3 && at<float>(TABLE_AT + 0x24) == 40.26f && g_write_batches - batches == 1,
          "the game's values again, in one batch");
    check(registry_live_status().active == 0, "nothing retained");
}
// A candidate whose own write fails rolls the whole turn back: the earlier layers stay as they were (a pass that cannot land
// keeps them), its handle stays valid, and both replies say why.
void a_failed_turn_keeps_the_earlier_layers() {
    random_loot_world(); RegistryLiveReply reply;
    const uint64_t h1 = draft(71, "leveler", "[" + level_set + level(30) + "," + uncommon(50) + "]");
    registry_live_commit(71, h1, &reply);
    const uint64_t h2 = draft(71, "leveler", "[" + level_set + level(40) + "]");
    g_fail_addrs = {A(WORLD_AT + 4)};  // the level's write does not land
    const auto replies = replace({{71, h2}}, {{71, h1}});
    g_fail_addrs.clear();
    check(replies[0].outcome == RegistryLiveOutcome::Refused && replies[0].reason.find("not committed") != std::string::npos &&
              replies[0].reason.find("randomLoot[game:world_509feadb_0].level.value") != std::string::npos,
          "the candidate is not committed, named in Kiln words: " + replies[0].reason);
    check(replies[1].outcome == RegistryLiveOutcome::Refused && replies[1].reason.find("kept") != std::string::npos,
          "the earlier change is kept: " + replies[1].reason);
    check(at<float>(WORLD_AT + 4) == 30.0f && at<float>(TABLE_AT + 0x24) == 50.0f && registry_live_status().active == 1,
          "the earlier pass's values stay, the weight too");
    check(registry_live_revert(71, h1, &reply) == KILN_OK && at<float>(WORLD_AT + 4) == 0 && at<float>(TABLE_AT + 0x24) == 40.26f,
          "its handle still reverts it");
}
// A candidate refused whole at submit (over its registry's cap) applies nothing, as a lone commit of it would; the turn still
// takes the earlier layers out (the pass's other calls apply, and the refusal is in the replies and registry.json).
void an_over_cap_candidate_is_refused_and_the_turn_goes_on() {
    random_loot_world(); RegistryLiveReply reply;
    const uint64_t h1 = draft(71, "leveler", "[" + level_set + level(30) + "]");
    registry_live_commit(71, h1, &reply);
    uint64_t over = 0;
    check(registry_live_begin(71, "leveler", KILN_REGISTRY_LIVE_WORLD_LOAD, &over) == KILN_OK, "begin");
    registry_live_submit(71, over, doc("leveler", list_of(many(world_table, "level.value", 16385))), &reply, nullptr);
    const uint64_t final = draft(71, "leveler", "[" + uncommon(45) + "]");
    const auto replies = replace({{71, over}, {71, final}}, {{71, h1}});
    check(replies[0].outcome == RegistryLiveOutcome::Refused && replies[0].reason.find("over this registry's cap of 16384") != std::string::npos,
          "the over-cap call is refused whole: " + replies[0].reason);
    check(replies[1].outcome == RegistryLiveOutcome::Applied && replies[2].outcome == RegistryLiveOutcome::Reverted &&
              at<float>(WORLD_AT + 4) == 0 && at<float>(TABLE_AT + 0x24) == 45.0f,
          "the pass's other call applies and the earlier layer is out");
    registry_live_publish();
    check(report_names("leveler", "over this registry's cap of 16384"), "registry.json keeps the refused call's row through the turn's write");
    registry_live_revert(71, final, &reply);
}
// The arguments: drafts to commit, retained changes to replace, each its owner's and once; the world thread only.
void a_turn_checks_its_handles() {
    random_loot_world(); RegistryLiveReply reply;
    const uint64_t h1 = draft(71, "leveler", "[" + level_set + level(30) + "]");
    registry_live_commit(71, h1, &reply);
    const uint64_t d = draft(71, "leveler", "[" + level_set + level(40) + "]");
    KilnResult rc = KILN_OK;
    replace({{71, h1}}, {}, &rc);
    check(rc == KILN_ERR_UNAVAILABLE, "a retained change is not a draft to commit");
    replace({{71, d}}, {{71, d}}, &rc);
    check(rc == KILN_ERR_UNAVAILABLE, "a draft has nothing in place to replace");
    replace({{72, d}}, {{71, h1}}, &rc);
    check(rc == KILN_ERR_INVALID_ARGUMENT, "another owner's draft");
    replace({{71, d}}, {{71, h1}, {71, h1}}, &rc);
    check(rc == KILN_ERR_INVALID_ARGUMENT, "a handle twice");
    replace({}, {}, &rc);
    check(rc == KILN_ERR_INVALID_ARGUMENT, "an empty turn");
    check(at<float>(WORLD_AT + 4) == 30.0f && registry_live_status().active == 1 && registry_live_status().drafts == 1,
          "a refused turn changes nothing");
    const auto tick = std::this_thread::get_id();
    g.in->on_world_thread = [tick](uint32_t) { return std::this_thread::get_id() == tick; };
    std::thread other([&] { replace({{71, d}}, {{71, h1}}, &rc); });
    other.join();
    g.in->on_world_thread = [](uint32_t) { return true; };
    check(rc == KILN_ERR_WRONG_THREAD && at<float>(WORLD_AT + 4) == 30.0f, "another thread is refused under the lock");
    registry_live_revert(71, d, &reply), registry_live_revert(71, h1, &reply);
}


// ---- a turn prepared off the world thread ----
std::string ir_of(const std::string& mod, const std::string& edits) { return doc(mod, edits); }
struct Landed {
    KilnResult rc = KILN_OK;
    uint32_t stale = 0;
    std::vector<RegistryLiveReply> replies;
    std::vector<uint64_t> handles;
};
uint64_t prepare(const std::vector<RegistryLiveCall>& calls, const std::vector<std::pair<KilnOwner, uint64_t>>& replaced, uint64_t epoch = 0,
                 KilnResult* rc = nullptr, uint32_t* stale = nullptr) {
    uint64_t ticket = 0; uint32_t s = 0;
    const KilnResult r = registry_live_prepare(calls, replaced, epoch ? epoch : registry_live_epoch(), &ticket, &s);
    if (rc) *rc = r;
    else check(r == KILN_OK, "prepare returns KILN_OK (" + std::to_string(r) + ", stale " + std::to_string(s) + ")");
    if (stale) *stale = s;
    return ticket;
}
Landed install(uint64_t ticket, uint32_t flags = 0) {
    Landed l;
    l.rc = registry_live_install_turn(ticket, flags, &l.replies, &l.handles, &l.stale);
    return l;
}
RegistryLiveCall call(KilnOwner owner, const std::string& mod, const std::string& edits) { return {owner, mod, KILN_REGISTRY_LIVE_WORLD_LOAD, ir_of(mod, edits)}; }
std::string loot_bytes() {  // the made-up loot tables' memory, as one comparable string
    return std::string(reinterpret_cast<const char*>(A(RULES_AT)), 0x20) + std::string(reinterpret_cast<const char*>(A(TABLE_AT)), 0x140);
}
std::string normalized_report() {  // registry.json's rows with the live handles' numbers taken out (two runs number them apart)
    std::string text;
    for (const auto& [ref, fo] : g_last_report.fields) {
        text += ref.text() + "=" + fo.result.text_of() + " winner " + fo.winner + ":";
        for (const auto& row : fo.chain) text += " [" + row.mod + "|" + std::to_string(static_cast<int>(row.kind)) + "|" + row.after.text_of() + "]";
        text += "\n";
    }
    for (const auto& r : g_last_report.refused) text += "refused " + r.mod + ": " + r.reason + "\n";
    for (const auto& l : g_last_report.lines) text += "line " + l.text + "\n";
    std::string out;
    for (size_t i = 0; i < text.size(); ++i) {
        if (text.compare(i, 5, "live:") == 0 || text.compare(i, 12, "live change ") == 0) {
            const size_t start = text[i + 4] == ':' ? i + 5 : i + 12;
            size_t j = start;
            while (j < text.size() && std::isdigit(static_cast<unsigned char>(text[j]))) ++j;
            out += text.substr(i, start - i) + "#";
            i = j - 1;
            continue;
        }
        out += text[i];
    }
    return out;
}

// The same turn two ways: registry_live_replace on the world thread, and prepared off it then landed. The game's bytes, the
// composition and registry.json are the same; the prepared one writes in one batch and builds its report off the tick.
void a_prepared_turn_lands_as_the_turn_does() {
    const std::string earlier = "[" + level_set + level(30) + "," + uncommon(50) + R"(,{"registry":"lootRules","select":"game:loot","field":"rarityWeights.epic","op":20}])";
    const std::string next = "[" + level_set + level(40) + R"(,{"registry":"lootRules","select":"game:loot","field":"rarityWeights.epic","op":25},)" +
                             R"({"registry":"randomLoot","select":"game:world_509feadb_1","field":"level.value","op":7}])";  // _1: not in this world
    random_loot_world(); RegistryLiveReply reply;
    uint64_t h1 = draft(71, "leveler", earlier);
    registry_live_commit(71, h1, &reply);
    const uint64_t other = draft(72, "other", R"([{"registry":"randomLoot","select":"game:labels_80943338","field":"rarity.rare.weight","op":3}])");
    registry_live_commit(72, other, &reply);
    const auto classic = replace({{71, draft(71, "leveler", next)}}, {{71, h1}});
    registry_live_publish();
    const std::string bytes = loot_bytes(), rows = normalized_report();
    random_loot_world();
    h1 = draft(71, "leveler", earlier);
    registry_live_commit(71, h1, &reply);
    registry_live_commit(72, draft(72, "other", R"([{"registry":"randomLoot","select":"game:labels_80943338","field":"rarity.rare.weight","op":3}])"), &reply);
    registry_live_publish();  // the commits' reports
    const size_t batches = g_write_batches;
    const int reports = g_reports;
    const Landed l = install(prepare({call(71, "leveler", next)}, {{71, h1}}));
    check(l.rc == KILN_OK && l.stale == 0 && l.replies.size() == 2 && l.handles.size() == 1 && l.handles[0] != 0, "the prepared turn lands");
    check(l.replies[0].outcome == classic[0].outcome && l.replies[0].accepted == classic[0].accepted && l.replies[0].refused == classic[0].refused &&
              l.replies[1].outcome == RegistryLiveOutcome::Reverted,
          "with the turn's replies: " + std::to_string(static_cast<int>(l.replies[0].outcome)) + " " + std::to_string(l.replies[0].accepted) + "/" +
              std::to_string(l.replies[0].refused) + " vs " + std::to_string(classic[0].accepted) + "/" + std::to_string(classic[0].refused) + ": " + l.replies[0].reason);
    check(g_write_batches - batches == 1, "one write batch");
    check(loot_bytes() == bytes && at<float>(WORLD_AT + 4) == 40.0f && at<float>(RULES_AT + 12) == 25.0f && at<float>(TABLE_AT + 0x24) == 40.26f,
          "the game's bytes are the turn's");
    registry_live_publish();
    check(g_reports == reports, "registry.json is not built on the world tick when the turn lands");
    check(registry_live_build_report(), "the report is built off the tick");
    registry_live_publish();
    check(g_reports == reports + 1 && normalized_report() == rows, "and is the turn's report:\n" + normalized_report() + "\n---\n" + rows);
    check(registry_live_revert(71, h1, &reply) == KILN_ERR_NOT_FOUND && registry_live_revert(71, l.handles[0], &reply) == KILN_OK &&
              at<float>(WORLD_AT + 4) == 0 && at<float>(RULES_AT + 12) == 9.71f,
          "the earlier change is gone, the new one reverts");
}
// A pass's calls in order (Alter, then Final), a pass that switches off (nothing to commit), and a pass refused whole at
// submit (over a cap) land as registry_live_replace's turn does.
void a_prepared_turn_keeps_the_turn_rules() {
    random_loot_world(); RegistryLiveReply reply;
    const uint64_t h1 = draft(71, "leveler", "[" + level_set + level(30) + "]");
    registry_live_commit(71, h1, &reply);
    Landed l = install(prepare({call(71, "leveler", "[" + level_set + level(40) + "," + uncommon(45) + "]"), call(71, "leveler", "[" + level(41) + "]")}, {{71, h1}}));
    check(l.rc == KILN_OK && l.replies[0].outcome == RegistryLiveOutcome::Applied && l.replies[1].outcome == RegistryLiveOutcome::Applied &&
              at<float>(WORLD_AT + 4) == 41.0f && at<float>(TABLE_AT + 0x24) == 45.0f,
          "the Final call lands after the Alter one");
    l = install(prepare({}, {{71, l.handles[0]}, {71, l.handles[1]}}));
    check(l.rc == KILN_OK && l.replies.size() == 2 && l.replies[0].outcome == RegistryLiveOutcome::Reverted && at<float>(WORLD_AT + 4) == 0 &&
              at<uint8_t>(WORLD_AT) == 3 && at<float>(TABLE_AT + 0x24) == 40.26f && registry_live_status().active == 0,
          "a pass that makes no edits takes the earlier layers out");
    l = install(prepare({call(71, "leveler", list_of(many(world_table, "level.value", 16385))), call(71, "leveler", "[" + uncommon(46) + "]")}, {}));
    check(l.rc == KILN_OK && l.replies[0].outcome == RegistryLiveOutcome::Refused && l.handles[0] == 0 &&
              l.replies[0].reason.find("over this registry's cap") != std::string::npos && l.replies[1].outcome == RegistryLiveOutcome::Applied &&
              at<float>(TABLE_AT + 0x24) == 46.0f,
          "an over-cap call is refused whole, the pass's other call lands: " + l.replies[0].reason);
    registry_live_build_report(); registry_live_publish();
    check(report_names("leveler", "over this registry's cap of 16384"), "and registry.json has its row");
    registry_live_revert(71, l.handles[1], &reply);
}
// The registry moved after the pass read it: the preparation is not written (the pass runs again). Only the bindings moved:
// prepared again, or landed the classic way when asked.
void a_stale_preparation_is_never_written() {
    random_loot_world(); RegistryLiveReply reply;
    const uint64_t h1 = draft(71, "leveler", "[" + level_set + level(30) + "]");
    registry_live_commit(71, h1, &reply);
    const uint64_t epoch = registry_live_epoch();
    uint64_t ticket = prepare({call(71, "leveler", "[" + level_set + level(40) + "]")}, {{71, h1}}, epoch);
    const uint64_t other = draft(72, "other", "[" + level_set + level(35) + "]");
    registry_live_commit(72, other, &reply);  // another mod's commit: what the pass read moved
    Landed l = install(ticket);
    check(l.rc == KILN_ERR_UNAVAILABLE && l.stale == 1 && at<float>(WORLD_AT + 4) == 35.0f && registry_live_status().active == 2,
          "a preparation over reads the registry has moved past is not written");
    check(install(ticket).rc == KILN_ERR_NOT_FOUND, "and is used up");
    KilnResult rc = KILN_OK; uint32_t stale = 0;
    prepare({call(71, "leveler", "[" + level_set + level(40) + "]")}, {{71, h1}}, epoch, &rc, &stale);
    check(rc == KILN_ERR_UNAVAILABLE && stale == 1, "a preparation from an old epoch is refused at once");
    ticket = prepare({call(71, "leveler", "[" + level_set + level(40) + "]")}, {{71, h1}});
    { std::lock_guard lock(store_mutex()); put_pack(0x7F000, 1); }  // a layer of an older path: the values below the registry moved
    l = install(ticket);
    check(l.rc == KILN_ERR_UNAVAILABLE && l.stale == 1 && at<float>(WORLD_AT + 4) == 35.0f, "a write below the registry makes it stale too");
    l = install(prepare({call(71, "leveler", "[" + level_set + level(40) + "]")}, {{71, h1}}));
    check(l.rc == KILN_OK && at<float>(WORLD_AT + 4) == 40.0f, "prepared again, it lands");
    registry_live_revert(72, other, &reply), registry_live_revert(71, l.handles[0], &reply);
}
void a_preparation_stale_only_in_its_bindings_lands_on_request() {
    fresh_live(); RegistryLiveReply reply;
    const auto pending = change(71, "a", R"([{"registry":"drops","select":"game:dt_wolf","field":"entries[*].count.max","op":{"mul":2}}])");
    registry_live_commit(71, pending, &reply);
    const uint64_t h1 = draft(72, "b", "[{" + wood + R"("op":50}])");
    registry_live_commit(72, h1, &reply);
    uint64_t ticket = prepare({call(72, "b", "[{" + wood + R"("op":60}])")}, {{72, h1}});
    add_drop_table();
    registry_live_table(g_regs[KILN_CONTENT_DROPS].entries[0]);  // the game loads a drop table: bindings move, compositions do not
    Landed l = install(ticket);
    check(l.rc == KILN_ERR_UNAVAILABLE && l.stale == 2 && at<uint32_t>(WOOD_AT) == 50, "a preparation whose bindings moved is not written as prepared");
    ticket = prepare({call(72, "b", "[{" + wood + R"("op":60}])")}, {{72, h1}});
    registry_live_table(g_regs[KILN_CONTENT_DROPS].entries[0]);
    l = install(ticket, kRegistryInstallAnyway);
    check(l.rc == KILN_OK && l.replies[0].outcome == RegistryLiveOutcome::Applied && l.replies[1].outcome == RegistryLiveOutcome::Reverted &&
              at<uint32_t>(WOOD_AT) == 60 && at<uint32_t>(0x31018) == 6,
          "asked to, it lands the classic way, composed and written on the world thread");
    registry_live_revert(72, l.handles[0], &reply), registry_live_revert(71, pending, &reply);
}
// review of LOOT-2: a drop table the preparation found unloaded, loaded by the game before the turn lands, while nothing live
// waited for it (the pass is the first to edit it: off to on). Its load moved no epoch, so the plan's fields would wait for a load
// that has happened and never be written in this world. The turn is stale in its bindings: prepared again, it binds the table.
void a_drop_table_loaded_while_a_turn_is_prepared_is_not_left_waiting() {
    fresh_live(); RegistryLiveReply reply;
    const std::string doubled = R"([{"registry":"drops","select":"game:dt_wolf","field":"entries[*].count.max","op":{"mul":2}}])";
    uint64_t ticket = prepare({call(72, "b", doubled)}, {});
    add_drop_table();
    registry_live_table(g_regs[KILN_CONTENT_DROPS].entries[0]);  // the game loads it: no live field waits for it
    Landed l = install(ticket);
    check(l.rc == KILN_ERR_UNAVAILABLE && l.stale == 2 && at<uint32_t>(0x31018) == 3 && registry_live_status().active == 0,
          "a preparation that left a now loaded table waiting is not written as prepared (rc " + std::to_string(l.rc) + ", stale " +
              std::to_string(l.stale) + ", outcome " + (l.replies.empty() ? std::string("-") : std::to_string(static_cast<int>(l.replies[0].outcome))) + ")");
    l = install(prepare({call(72, "b", doubled)}, {}));
    check(l.rc == KILN_OK && l.replies[0].outcome == RegistryLiveOutcome::Applied && at<uint32_t>(0x31018) == 6 && at<uint32_t>(0x31058) == 6,
          "prepared again, it binds the loaded table and lands: " + std::to_string(static_cast<int>(l.rc)));
    ticket = prepare({call(72, "b", R"([{"registry":"drops","select":"game:dt_wolf","field":"entries[*].count.max","op":{"mul":3}}])")}, {{72, l.handles[0]}});
    const Landed anyway = install(ticket, kRegistryInstallAnyway);
    check(anyway.rc == KILN_OK && anyway.replies[0].outcome == RegistryLiveOutcome::Applied && at<uint32_t>(0x31018) == 9,
          "and a bound table re-authored lands as usual");
    registry_live_revert(72, anyway.handles[0], &reply);
    check(at<uint32_t>(0x31018) == 3, "reverted to the game's value");
}
// The same with the landing asked to go ahead (the third preparation, or the tick's): the classic way binds the table.
void a_drop_table_loaded_while_a_turn_is_prepared_lands_bound_on_request() {
    fresh_live(); RegistryLiveReply reply;
    const uint64_t ticket = prepare({call(72, "b", R"([{"registry":"drops","select":"game:dt_wolf","field":"entries[*].count.max","op":{"mul":2}}])")}, {});
    add_drop_table();
    registry_live_table(g_regs[KILN_CONTENT_DROPS].entries[0]);
    const Landed l = install(ticket, kRegistryInstallAnyway);
    check(l.rc == KILN_OK && l.replies[0].outcome == RegistryLiveOutcome::Applied && at<uint32_t>(0x31018) == 6,
          "landed bound, not waiting for the table: outcome " + std::to_string(static_cast<int>(l.replies.empty() ? 99 : static_cast<int>(l.replies[0].outcome))));
    registry_live_revert(72, l.handles[0], &reply);
}
// A turn the preparation does not cover (a formula field: its evaluation reads the entry's other values, which the turn itself
// may change) lands the classic way: composed and written on the world thread, with the turn's rules.
void a_turn_the_preparation_does_not_cover_lands_the_classic_way() {
    fresh_live(); RegistryLiveReply reply;
    const float before = at<float>(SWORD_AT + 8);
    const uint64_t h1 = draft(71, "a", R"([{"registry":"items","select":"game:sword_t2","field":"power","op":{"mul":2}}])");
    registry_live_commit(71, h1, &reply);
    const float doubled = at<float>(SWORD_AT + 8);
    check(doubled != before, "power x2 written through the formula");
    g_log_lines.clear();
    const Landed l = install(prepare({call(71, "a", R"([{"registry":"items","select":"game:sword_t2","field":"power","op":{"mul":3}}])")}, {{71, h1}}));
    check(l.rc == KILN_OK && l.replies[0].outcome == RegistryLiveOutcome::Applied && l.replies[1].outcome == RegistryLiveOutcome::Reverted &&
              at<float>(SWORD_AT + 8) != doubled && at<float>(SWORD_AT + 8) != before,
          "the turn lands: power x3 over the game's, the x2 gone");
    check(std::any_of(g_log_lines.begin(), g_log_lines.end(), [](const std::string& line) {
              return line.find("lands the classic way: a field's evaluation reads other fields' values") != std::string::npos; }),
          "kiln.log says why it landed the classic way");
    check(registry_live_revert(71, l.handles[0], &reply) == KILN_OK && at<float>(SWORD_AT + 8) == before, "and reverts to the game's value");
}
// The preparation and the pass's reads on another thread; the landing only on the world's.
void a_turn_is_prepared_off_the_world_thread() {
    random_loot_world(); RegistryLiveReply reply;
    const uint64_t h1 = draft(71, "leveler", "[" + level_set + level(30) + "]");
    registry_live_commit(71, h1, &reply);
    const auto tick = std::this_thread::get_id();
    g.in->on_world_thread = [tick](uint32_t) { return std::this_thread::get_id() == tick; };
    const uint64_t world = registry_live_status().world;
    uint64_t ticket = 0; KilnResult prepared = KILN_ERR_UNAVAILABLE, read = KILN_ERR_UNAVAILABLE, plain = KILN_OK, landed = KILN_OK;
    std::string value;
    std::thread worker([&] {
        read = registry_live_read("randomLoot", "game:world_509feadb_0", "level.value", &value, "leveler", world);
        std::string ignored;
        plain = registry_live_read("randomLoot", "game:world_509feadb_0", "level.value", &ignored, "leveler");
        uint32_t stale = 0;
        prepared = registry_live_prepare({call(71, "leveler", "[" + level_set + level(40) + "]")}, {{71, h1}}, registry_live_epoch(), &ticket, &stale);
        std::vector<RegistryLiveReply> r; std::vector<uint64_t> h;
        landed = registry_live_install_turn(ticket, 0, &r, &h, &stale);
    });
    worker.join();
    check(read == KILN_OK && registry::json::parse(value) == 0 && plain == KILN_ERR_WRONG_THREAD,
          "a pass reads below its own layers off the world thread in its world: " + value);
    check(prepared == KILN_OK && landed == KILN_ERR_WRONG_THREAD && at<float>(WORLD_AT + 4) == 30.0f,
          "the turn is prepared there and never landed there");
    const Landed l = install(ticket);
    check(l.rc == KILN_OK && at<float>(WORLD_AT + 4) == 40.0f, "the world thread lands it");
    std::string other;
    check(registry_live_read("randomLoot", "game:world_509feadb_0", "level.value", &other, "leveler", world + 1) == KILN_ERR_UNAVAILABLE,
          "a read for another world is refused");
    g.in->on_world_thread = [](uint32_t) { return true; };
    registry_live_revert(71, l.handles[0], &reply);
}
// A call whose write does not land: the classic way takes over, rolls the turn back and keeps the earlier layers.
void a_prepared_turn_that_cannot_write_keeps_the_earlier_layers() {
    random_loot_world(); RegistryLiveReply reply;
    const uint64_t h1 = draft(71, "leveler", "[" + level_set + level(30) + "," + uncommon(50) + "]");
    registry_live_commit(71, h1, &reply);
    const uint64_t ticket = prepare({call(71, "leveler", "[" + level_set + level(40) + "]")}, {{71, h1}});
    g_fail_addrs = {A(WORLD_AT + 4)};
    const Landed l = install(ticket);
    g_fail_addrs.clear();
    check(l.rc == KILN_OK && l.replies[0].outcome == RegistryLiveOutcome::Refused && l.handles[0] == 0 &&
              l.replies[0].reason.find("randomLoot[game:world_509feadb_0].level.value") != std::string::npos &&
              l.replies[1].outcome == RegistryLiveOutcome::Refused,
          "the call is not committed, the earlier change kept: " + l.replies[0].reason + " / " + l.replies[1].reason);
    check(at<float>(WORLD_AT + 4) == 30.0f && at<float>(TABLE_AT + 0x24) == 50.0f && registry_live_status().active == 1,
          "the earlier pass's values stay");
    check(registry_live_revert(71, h1, &reply) == KILN_OK && at<float>(WORLD_AT + 4) == 0, "and its handle reverts them");
}
// A landed turn whose report no worker built: the world tick builds it after kReportOwedMs.
void an_owed_report_is_built_by_the_tick_in_the_end() {
    static uint64_t now = 1'000'000;
    g_registry_now_ms = [] { return now; };
    random_loot_world(); RegistryLiveReply reply;
    const uint64_t h1 = draft(71, "leveler", "[" + level_set + level(30) + "]");
    registry_live_commit(71, h1, &reply);
    registry_live_publish();
    const int reports = g_reports;
    const Landed l = install(prepare({call(71, "leveler", "[" + level_set + level(40) + "]")}, {{71, h1}}));
    registry_live_publish();
    now += kReportOwedMs - 1;
    registry_live_publish();
    check(g_reports == reports, "not before kReportOwedMs");
    now += 1;
    registry_live_publish();
    check(g_reports == reports + 1 && std::any_of(g_last_report.fields.begin(), g_last_report.fields.end(), [&](const auto& kv) {
              return std::any_of(kv.second.chain.begin(), kv.second.chain.end(), [&](const registry::ChainRow& r) {
                  return r.source.rfind("live:" + std::to_string(l.handles[0]) + ":", 0) == 0; }); }),
          "then the tick builds it, with the landed change");
    check(registry_live_build_report(), "nothing is owed any more");
    g_registry_now_ms = &steady_ms;
    registry_live_revert(71, l.handles[0], &reply);
}

// The cost (owner: "under about 16 ms on the tick"): every field of every table of the real data's size (797 tables x 16
// fields = 12,752 edits), re-authored. Before dev.32 the world thread composed and wrote it twice (the new pass over the
// earlier layer, then the earlier layer taken back): 297 ms and 218 ms at -O2 on the host. Now the turn is prepared off it and
// the world thread only lands it. KILN_REAUTHOR_BUDGET_MS: registry_live_reauthor_cost_test.sh builds this at -O2 without
// sanitizers and holds the landing to 16 ms; the sanitizer build only checks it stays bounded.
#ifndef KILN_REAUTHOR_BUDGET_MS
#define KILN_REAUTHOR_BUDGET_MS 400
#endif
double ms_since(std::chrono::steady_clock::time_point t) { return std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t).count(); }
void a_full_reauthor_lands_within_the_tick_budget() {
    random_loot_world();
    constexpr size_t kTables = 797, kAt = 0xA0000, kStride = 0x40;
    Registry& T = g_regs[KILN_CONTENT_RANDOM_LOOT];
    RegistryLive installed = *g.in;
    const registry::BaseModel::Entry model = installed.base.registries.at("randomLoot").back();
    static const char* const fields[] = {"level.value", "levelOffset.value", "levelOffsetRange.min", "levelOffsetRange.max"};
    static const char* const rar[5] = {"common", "uncommon", "rare", "epic", "legendary"};
    auto pass = [&](int value) {
        std::string edits;
        for (size_t i = 0; i < kTables; ++i) {
            const std::string id = "game:world_cost_" + std::to_string(i);
            const std::string sel = R"("registry":"randomLoot","select":")" + id + R"(",)";
            edits += "{" + sel + R"("field":"level.mode","op":"set"},{)" + sel + R"("field":"levelOffset.mode","op":"add"},)";
            for (const char* f : fields) edits += "{" + sel + R"("field":")" + f + R"(","op":)" + std::to_string(value) + "},";
            for (const char* r : rar)
                edits += "{" + sel + R"("field":"rarity.)" + r + R"(.mode","op":"multiply"},{)" + sel + R"("field":"rarity.)" + r + R"(.weight","op":)" +
                         std::to_string(value - 1) + "},";
        }
        return list_of(edits);
    };
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
    }
    registry_live_install(std::move(installed)); registry_live_world_begin(); apply_registry_files({});
    const size_t last = kAt + (kTables - 1) * kStride;
    struct Step { const char* name; double prepare, land, report; };
    std::vector<Step> steps;
    auto reauthor = [&](const char* name, std::vector<RegistryLiveCall> calls, std::vector<std::pair<KilnOwner, uint64_t>> replaced) {
        Step step{name, 0, 0, 0};
        uint64_t ticket = 0;
        KilnResult prepared = KILN_ERR_UNAVAILABLE;
        std::thread worker([&] {  // the pass's worker: prepared off the world thread
            const auto t = std::chrono::steady_clock::now();
            uint32_t stale = 0;
            prepared = registry_live_prepare(calls, replaced, registry_live_epoch(), &ticket, &stale);
            step.prepare = ms_since(t);
        });
        worker.join();
        check(prepared == KILN_OK, std::string(name) + ": prepared (" + std::to_string(prepared) + ")");
        const auto t = std::chrono::steady_clock::now();
        Landed l = install(ticket);
        step.land = ms_since(t);
        check(l.rc == KILN_OK, std::string(name) + ": landed");
        bool built = false;
        std::thread reporter([&] {
            const auto r = std::chrono::steady_clock::now();
            built = registry_live_build_report();
            step.report = ms_since(r);
        });
        reporter.join();
        check(built, std::string(name) + ": its report built off the tick");
        registry_live_publish();
        steps.push_back(step);
        return l;
    };
    Landed on = reauthor("off to on", {call(71, "leveler", pass(3))}, {});
    check(on.replies[0].outcome == RegistryLiveOutcome::Applied && on.replies[0].accepted == kTables * 16 && at<uint8_t>(last + 0x08) == 1 &&
              at<float>(last + 0x14) == 3.0f && at<float>(last + 0x1C) == 2.0f,
          "every field of every table written: " + on.replies[0].reason);
    Landed again = reauthor("replace", {call(71, "leveler", pass(4))}, {{71, on.handles[0]}});
    check(again.replies[0].outcome == RegistryLiveOutcome::Applied && again.replies[1].outcome == RegistryLiveOutcome::Reverted &&
              at<float>(last + 0x14) == 4.0f && at<float>(last + 0x1C) == 3.0f,
          "the new pass replaces the earlier one");
    Landed off = reauthor("on to off", {}, {{71, again.handles[0]}});
    check(off.replies[0].outcome == RegistryLiveOutcome::Reverted && at<uint8_t>(last + 0x08) == 3 && at<float>(last + 0x14) == 0,
          "and the earlier layers come out");
    for (const Step& s : steps) {
        std::printf("randomLoot re-author of %zu edits, %s: prepared off the world thread in %.2f ms, landed on it in %.2f ms, report built off it in %.2f ms\n",
                    kTables * 16, s.name, s.prepare, s.land, s.report);
        check(s.land < KILN_REAUTHOR_BUDGET_MS, std::string(s.name) + ": landed in " + std::to_string(s.land) + " ms, over the " +
                                                     std::to_string(KILN_REAUTHOR_BUDGET_MS) + " ms budget");
    }
}
}  // namespace

int main() {
    g_registry_stack_guard = &fake_guard;  // the host has no game code to patch: the guard as on a build that has it
    std::vector<std::string> errs; g_regmap = std::make_shared<registry::map::RegMap>();
    if (!registry::map::load_dir(KILN_REGMAP_DIR, "server", "f0d824be7fcd48ebfe2458ff5c28da26060c2074e3ec1fcac587e5a1d508aeb2", g_regmap.get(), &errs)) return 2;
    int failed = 0;
    const std::pair<const char*, void (*)()> tests[] = {
        {"a_replace_is_one_turn_and_one_write", a_replace_is_one_turn_and_one_write},
        {"a_replace_lands_after_every_other_change", a_replace_lands_after_every_other_change},
        {"a_turn_commits_its_candidates_in_order", a_turn_commits_its_candidates_in_order},
        {"a_turn_with_nothing_to_commit_takes_the_earlier_layers_out", a_turn_with_nothing_to_commit_takes_the_earlier_layers_out},
        {"a_failed_turn_keeps_the_earlier_layers", a_failed_turn_keeps_the_earlier_layers},
        {"an_over_cap_candidate_is_refused_and_the_turn_goes_on", an_over_cap_candidate_is_refused_and_the_turn_goes_on},
        {"a_turn_checks_its_handles", a_turn_checks_its_handles},
        {"a_prepared_turn_lands_as_the_turn_does", a_prepared_turn_lands_as_the_turn_does},
        {"a_prepared_turn_keeps_the_turn_rules", a_prepared_turn_keeps_the_turn_rules},
        {"a_stale_preparation_is_never_written", a_stale_preparation_is_never_written},
        {"a_preparation_stale_only_in_its_bindings_lands_on_request", a_preparation_stale_only_in_its_bindings_lands_on_request},
        {"a_drop_table_loaded_while_a_turn_is_prepared_is_not_left_waiting", a_drop_table_loaded_while_a_turn_is_prepared_is_not_left_waiting},
        {"a_drop_table_loaded_while_a_turn_is_prepared_lands_bound_on_request", a_drop_table_loaded_while_a_turn_is_prepared_lands_bound_on_request},
        {"a_turn_the_preparation_does_not_cover_lands_the_classic_way", a_turn_the_preparation_does_not_cover_lands_the_classic_way},
        {"a_turn_is_prepared_off_the_world_thread", a_turn_is_prepared_off_the_world_thread},
        {"a_prepared_turn_that_cannot_write_keeps_the_earlier_layers", a_prepared_turn_that_cannot_write_keeps_the_earlier_layers},
        {"an_owed_report_is_built_by_the_tick_in_the_end", an_owed_report_is_built_by_the_tick_in_the_end},
        {"a_full_reauthor_lands_within_the_tick_budget", a_full_reauthor_lands_within_the_tick_budget},
    };
    if (const char* only = std::getenv("KILN_TEST_ONLY")) {  // the -O2 cost build runs its budget case alone
        for (auto [name, fn] : tests)
            if (std::string(name) == only) try { fn(); std::printf("ok %s\n", name); } catch (const std::exception& e) { ++failed; std::printf("FAIL %s: %s\n", name, e.what()); }
        std::printf("registry live reauthor (%s): %d checks, %d failed groups\n", only, checks, failed);
        return failed ? 1 : 0;
    }
    for (auto [name, fn] : tests) try { fn(); std::printf("ok %s\n", name); } catch (const std::exception& e) { ++failed; std::printf("FAIL %s: %s\n", name, e.what()); }
    std::printf("registry live reauthor: %d checks, %d failed groups\n", checks, failed);
    return failed ? 1 : 0;
}
