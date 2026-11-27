// SPDX-License-Identifier: MIT
// kiln.content, host test (dev.32): the registry's live back-end over kiln.gamedata's entries, the buffs and abilities
// registries, on the one gamedata-section writer (registry_live.cpp bind_section, bind_section_scalar, bind_event_time) through
// its gd_* seams (content.h), on the real store, IR and composition and the repository's regmap rows (every build row ok:
// regmap_fixture.sh). registry_live_test.cpp's GdSectionFake stands in for gamedata_packs.cpp: a made-up buff record and an
// ability's events in the test arena.
//   - buffs.duration (a time) and buffs.stacking (an enum): write, read back, revert; a relative edit; a pack below the registry;
//     another writer after Kiln's layer (compare-before-write keeps its bytes); read-only fields refused;
//   - abilities.timing.release: the first spawn and every spawn at the same moment move together, a later spawn stays; a
//     relative edit; revert; kiln.gamedata is told after each flush (gd_section_written);
//   - an ability without the moment refuses that entry alone at an entry point, the whole change in a Manual transaction;
//   - without kiln.gamedata's entries in this world the registries refuse, named.
#define main registry_live_baseline_main
#include "registry_live_test.cpp"
#undef main
#include "services/kiln_registry_live.h"

namespace {
constexpr uint32_t BUFF = 0xcdbe9c80u, CAST = 0xabc0001u, TURN = 0xabc0002u;
constexpr size_t BUFF_AT = 0x50000, CAST_AT = 0x51000, TURN_AT = 0x52000;
constexpr uint32_t GD_BUFFS = 16 + 4, GD_SEQUENCES = 16 + 3;
const uint8_t G5[16] = {5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5}, G6[16] = {6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6};
// the cast's events (each a keen::Time at its address): the first spawn at 0.65 s with a payment at the same moment (another kind),
// a second spawn at 0.65 s (moves with the first), a third at 0.9 s (stays)
constexpr size_t SPAWN0 = CAST_AT + 0x00, PAY0 = CAST_AT + 0x20, SPAWN1 = CAST_AT + 0x40, SPAWN2 = CAST_AT + 0x60, TURN_SPAWN = TURN_AT;
int& g_written = g_gd_written;  // gd_section_written calls (registry_live_test.cpp's fake)
bool g_hooks = true;            // kiln.gamedata has the buff and the abilities in this world
bool g_no_replace_at_end = false;  // a game build whose buffs have no ReplaceAtEnd

std::shared_ptr<EnumInfo> apply_types() {
    auto en = std::make_shared<EnumInfo>();
    en->values = {{"Invalid", 0}, {"ResetTime", 1}, {"ReplaceAtEnd", 2}, {"Replace", 3}};
    if (g_no_replace_at_end) std::erase_if(en->values, [](const auto& v) { return v.first == "ReplaceAtEnd"; });
    return en;
}
std::shared_ptr<EnumInfo> slot_types() {
    auto en = std::make_shared<EnumInfo>();
    en->values = {{"Food", 0}, {"Buff", 1}, {"Debuff", 2}, {"Rested", 3}, {"TerrainDebuff", 4}};
    return en;
}
Field time_field() {
    Field f;
    f.bytes = 8, f.vt = KILN_CV_TIME, f.is_signed = true, f.writable = true, f.type_name = "keen::Time";
    return f;
}
GdValueSlot slot(uintptr_t addr, Field f, uint32_t kind, uint32_t id, int32_t field) {
    GdValueSlot s;
    s.addr = addr, s.f = std::move(f), s.kind = kind, s.id = id, s.field = field;
    return s;
}

void gd_sections();
void gd_world() {
    world();
    g_written = 0;
    gd_sections();
    mem<uint8_t>(BUFF_AT + 4, 3), mem<uint8_t>(BUFF_AT + 5, 1), mem<int64_t>(BUFF_AT + 8, 10'000'000'000ll), mem<uint8_t>(BUFF_AT + 0x4a, 1);
    for (size_t at : {SPAWN0, PAY0, SPAWN1}) mem<int64_t>(at, 650'000'000ll);
    mem<int64_t>(SPAWN2, 900'000'000ll);
    mem<int64_t>(TURN_SPAWN, 200'000'000ll);
}

// kiln.gamedata's buffs and sequences sections in this world, as gamedata_packs.cpp gives them to the one writer (the
// gamedata-section fake of registry_live_test.cpp): the buff by its 32-bit id, the abilities by their GUID, the cast's moments
void gd_sections() {
    GdSectionFake& B = g_gd_sections[KILN_GD_BUFFS];
    GdSectionFake& S = g_gd_sections[KILN_GD_SEQUENCES];
    B = GdSectionFake{}, S = GdSectionFake{};
    if (!g_hooks) return;  // no such entries in this world
    Entry buff;
    buff.id = BUFF, buff.addr = A(BUFF_AT), buff.name = "buff_cdbe9c80";
    B.entries["buff:cdbe9c80"] = buff;
    B.slots[{BUFF, "defaultLifeTime"}] = slot(A(BUFF_AT + 8), time_field(), GD_BUFFS, BUFF, 1);
    Field apply;
    apply.bytes = 1, apply.vt = KILN_CV_INT, apply.enum_kind = KILN_CONTENT_ENUM, apply.en = apply_types(), apply.writable = true;
    B.slots[{BUFF, "applyType"}] = slot(A(BUFF_AT + 5), apply, GD_BUFFS, BUFF, 2);
    Field kind;
    kind.bytes = 1, kind.vt = KILN_CV_INT, kind.enum_kind = KILN_CONTENT_ENUM, kind.en = slot_types(), kind.writable = true;
    B.slots[{BUFF, "slot"}] = slot(A(BUFF_AT + 4), kind, GD_BUFFS, BUFF, 3);
    Entry cast, turn;
    cast.id = CAST, cast.addr = A(CAST_AT), cast.name = "Fire_Cast";
    turn.id = TURN, turn.addr = A(TURN_AT), turn.name = "Turn";
    S.entries[guid_text(G5)] = cast, S.entries[guid_text(G6)] = turn;
    S.events[{CAST, "keen::actor::SpawnEntityEvent"}] = {slot(A(SPAWN0), time_field(), GD_SEQUENCES, CAST, 4),
                                                         slot(A(SPAWN1), time_field(), GD_SEQUENCES, CAST, 5)};
    S.events[{TURN, "keen::actor::SpawnEntityEvent"}] = {slot(A(TURN_SPAWN), time_field(), GD_SEQUENCES, TURN, 4)};
    S.events[{CAST, "keen::actor::PayUsageCost"}] = {slot(A(PAY0), time_field(), GD_SEQUENCES, CAST, 6)};
}

void install_hooks(RegistryLive& in) {
    g_no_replace_at_end = false;
    in.ids["abilities|game:fire_cast"] = guid_text(G5), in.ids["abilities|game:turn"] = guid_text(G6);
    in.nums["buffs|game:buff_cdbe9c80"] = BUFF;
    for (const char* f : {"buffs.duration", "buffs.stacking", "buffs.kind", "abilities.timing.release", "abilities.timing.cost", "abilities.timing.hit"})
        in.decoded.insert(f);
}

registry::BaseModel gd_base() {
    registry::BaseModel b = base();
    registry::BaseModel::Entry buff, cast, turn;
    buff.id = "game:buff_cdbe9c80";
    buff.fields["duration"] = registry::Value::number(10), buff.fields["stacking"] = registry::Value::text("refresh");
    buff.fields["kind"] = registry::Value::text("rested"), buff.fields["saved"] = registry::Value::boolean(true);
    b.registries["buffs"].push_back(buff);
    cast.id = "game:fire_cast";
    cast.fields["timing.release"] = registry::Value::number(0.65), cast.fields["timing.cost"] = registry::Value::number(0.65);
    b.registries["abilities"].push_back(cast);
    turn.id = "game:turn";
    turn.fields["timing.release"] = registry::Value::number(0.2);
    b.registries["abilities"].push_back(turn);
    return b;
}

// stage() of registry_live_test.cpp with the gamedata hooks and entries
void gd_stage(const std::vector<PackFile>& files) {
    const registry::Schema& sc = registry_schema();
    std::vector<registry::ModInput> mods;
    for (const RegistrySection& s : registry_sections(files, sc)) {
        auto it = std::find_if(mods.begin(), mods.end(), [&](const registry::ModInput& m) { return m.id == s.mod; });
        if (it == mods.end()) mods.push_back({}), it = mods.end() - 1, it->id = s.mod;
        for (auto& b : s.batches) it->batches.push_back(b);
        for (auto& r : s.refused) it->refused.push_back(r);
    }
    RegistryLive in;
    in.schema = std::make_shared<const registry::Schema>(sc);
    in.on_world_thread = [](uint32_t) { return true; };
    in.regmap = g_regmap;
    in.base = gd_base();
    in.mods = mods;
    in.result = registry::compose(sc, in.base, {}, mods);
    in.report = [](const registry::Result& r, bool) { g_last_report = r, ++g_reports; };
    install_hooks(in);
    registry_live_install(std::move(in));
}
void gd_fresh(const std::vector<PackFile>& files = {}) {
    gd_world();
    gd_stage(files);
    registry_live_world_begin();
    apply_registry_files(files);
}

std::string doc_of(const std::string& mod, const std::string& edits) { return "{\"ir\":1,\"mod\":\"" + mod + "\",\"stage\":\"alter\",\"edits\":" + edits + "}"; }
uint64_t begin_change(KilnOwner owner, const std::string& mod, const std::string& edits, uint32_t point = 4) {
    uint64_t h = 0;
    RegistryLiveReply reply;
    check(registry_live_begin(owner, mod, point, &h) == KILN_OK, "begin");
    check(registry_live_submit(owner, h, doc_of(mod, edits), &reply, nullptr) == KILN_OK, "submit");
    return h;
}
std::string read(const std::string& reg, const std::string& id, const std::string& field) {
    std::string v;
    return registry_live_read(reg, id, field, &v) == KILN_OK ? v : "ERR";
}
const std::string buff = R"("registry":"buffs","select":"game:buff_cdbe9c80",)";
const std::string cast = R"("registry":"abilities","select":"game:fire_cast",)";

void buff_duration_read_write_revert() {
    gd_fresh();
    RegistryLiveReply reply;
    check(read("buffs", "game:buff_cdbe9c80", "duration") == "10.0", "buffs.duration reads the record's time in seconds: " + read("buffs", "game:buff_cdbe9c80", "duration"));
    const int before = g_written;
    const auto h = begin_change(71, "a", "[{" + buff + R"("field":"duration","op":{"mul":2}}])");
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && at<int64_t>(BUFF_AT + 8) == 20'000'000'000ll, "a relative edit writes 20 s in the game's nanoseconds");
    check(g_written > before, "kiln.gamedata is told after the flush (its buff mirror follows)");
    check(read("buffs", "game:buff_cdbe9c80", "duration") == "20.0", "read back through the registry");
    check(at<uint8_t>(BUFF_AT + 5) == 1 && at<uint8_t>(BUFF_AT + 4) == 3, "the record's other members are untouched");
    registry_live_revert(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Reverted && at<int64_t>(BUFF_AT + 8) == 10'000'000'000ll, "revert puts the game's time back");
}

void buff_stacking_enum() {
    gd_fresh();
    RegistryLiveReply reply;
    check(read("buffs", "game:buff_cdbe9c80", "stacking") == "\"refresh\"", "stacking reads ResetTime as refresh");
    const auto h = begin_change(71, "a", "[{" + buff + R"("field":"stacking","op":"replace"}])");
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && at<uint8_t>(BUFF_AT + 5) == 3, "replace is the game's Replace (3)");
    const auto k = begin_change(72, "b", "[{" + buff + R"("field":"stacking","op":"keep"}])");
    registry_live_commit(72, k, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && at<uint8_t>(BUFF_AT + 5) == 0, "keep is the game's Invalid (0); the later change wins");
    registry_live_revert(72, k, &reply);
    check(at<uint8_t>(BUFF_AT + 5) == 3, "reverting the later change restores the earlier one");
    registry_live_revert(71, h, &reply);
    check(at<uint8_t>(BUFF_AT + 5) == 1, "and the game's");
    uint64_t bad = 0;
    RegistryLiveReply r2;
    check(registry_live_begin(73, "c", 4, &bad) == KILN_OK, "begin");
    registry_live_submit(73, bad, doc_of("c", "[{" + buff + R"("field":"kind","op":"food"}])"), &r2, nullptr);
    check(r2.accepted == 0 && at<uint8_t>(BUFF_AT + 4) == 3, "buffs.kind is read-only: refused, nothing written");
    registry_live_revert(73, bad, &r2);
}

void buff_pack_below_and_foreign_writer() {
    gd_fresh();
    RegistryLiveReply reply;
    // kiln.gamedata's own pack layer (an older path) on the same slot sets 12 s; the registry's relative edit composes over it
    uint8_t b[8];
    const int64_t twelve = 12'000'000'000ll;
    std::memcpy(b, &twelve, 8);
    set_owner_label(PACK, "pack oldbuffs");
    store_put(PACK, kPackSeq, GD_BUFFS, BUFF, 1, -1, A(BUFF_AT + 8), 8, b, "oldbuffs: content/a.json");
    std::string ferr;
    store_flush(&ferr);
    check(at<int64_t>(BUFF_AT + 8) == twelve, "the pack's 12 s");
    const auto h = begin_change(71, "a", "[{" + buff + R"("field":"duration","op":{"add":3}}])");
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && at<int64_t>(BUFF_AT + 8) == 15'000'000'000ll, "registry +3 s over the pack's 12 s");
    mem<int64_t>(BUFF_AT + 8, 99'000'000'000ll);  // another writer after Kiln's layer
    registry_live_revert(71, h, &reply);
    check(at<int64_t>(BUFF_AT + 8) == 99'000'000'000ll, "compare-before-write: a revert keeps another writer's bytes");
}

void ability_release_moves_the_moment() {
    gd_fresh();
    RegistryLiveReply reply;
    check(read("abilities", "game:fire_cast", "timing.release") == "0.65", "timing.release reads the first spawn's time: " + read("abilities", "game:fire_cast", "timing.release"));
    const int before = g_written;
    const auto h = begin_change(71, "a", "[{" + cast + R"("field":"timing.release","op":0.45}])");
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && at<int64_t>(SPAWN0) == 450'000'000ll && at<int64_t>(SPAWN1) == 450'000'000ll,
          "the first spawn and the spawn at the same moment move to 0.45 s");
    check(at<int64_t>(SPAWN2) == 900'000'000ll && at<int64_t>(PAY0) == 650'000'000ll, "a later spawn and an event of another kind stay");
    check(g_written > before, "kiln.gamedata is told after the flush (it keeps the events sorted)");
    check(read("abilities", "game:fire_cast", "timing.release") == "0.45", "read back");
    const auto m = begin_change(72, "b", "[{" + cast + R"("field":"timing.release","op":{"mul":2}}])");
    registry_live_commit(72, m, &reply);
    check(at<int64_t>(SPAWN0) == 900'000'000ll && at<int64_t>(SPAWN1) == 900'000'000ll, "a later relative edit composes on 0.45 for the whole moment");
    registry_live_revert(72, m, &reply);
    registry_live_revert(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Reverted && at<int64_t>(SPAWN0) == 650'000'000ll && at<int64_t>(SPAWN1) == 650'000'000ll,
          "revert puts both back");
    const auto c = begin_change(73, "c", "[{" + cast + R"("field":"timing.cost","op":0.5}])");
    registry_live_commit(73, c, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && at<int64_t>(PAY0) == 500'000'000ll && at<int64_t>(SPAWN0) == 650'000'000ll,
          "timing.cost moves the payment alone");
    registry_live_revert(73, c, &reply);
}

void ability_without_the_moment_refuses_that_entry() {
    gd_fresh();
    RegistryLiveReply reply;
    const std::string all = R"([{"registry":"abilities","select":"game:*","field":"timing.cost","op":0.3}])";
    const auto manual = begin_change(70, "m", all, 4);
    registry_live_commit(70, manual, &reply);
    check(reply.outcome == RegistryLiveOutcome::Refused && at<int64_t>(PAY0) == 650'000'000ll, "a Manual transaction stays all-or-nothing");
    const auto h = begin_change(71, "a", all, 2);
    check(registry_live_commit(71, h, &reply) == KILN_OK && reply.outcome == RegistryLiveOutcome::Applied && reply.accepted == 1 && reply.refused == 1,
          "an entry point applies the ability that has the moment and refuses the other");
    check(at<int64_t>(PAY0) == 300'000'000ll, "the cast's payment moved");
    check(reply.reason.find("no such moment") != std::string::npos, "the refusal says why in Kiln words: " + reply.reason);
    registry_live_revert(71, h, &reply);
}

// registry trial merge: on the one gamedata-section writer an ability without an event of the kind has no value of the field
// (only some abilities have one), as a creature without a component: a read of it in a world is null, as its base reads it, never
// an unreadable field that fails a mod's whole pass; a read of one that has it still reads its time
void ability_without_the_moment_reads_null() {
    gd_fresh();
    std::string v;
    check(registry_live_read("abilities", "game:turn", "timing.cost", &v) == KILN_OK && v == "null", "the turn has no payment: null (" + v + ")");
    check(read("abilities", "game:fire_cast", "timing.cost") == "0.65", "the cast's payment reads its time");
}

void data_section_binds_at_world_load() {
    g_files = {{"a/content/spells.json", R"({"registry":{"alter":[
        {"registry":"abilities","select":"game:fire_cast","field":"timing.release","op":0.45},
        {"registry":"buffs","select":"game:buff_cdbe9c80","field":"duration","op":30}]}})"}};
    const auto files = files_of({{"a", "a/content/spells.json"}});
    gd_fresh(files);
    check(at<int64_t>(SPAWN0) == 450'000'000ll && at<int64_t>(SPAWN1) == 450'000'000ll && at<int64_t>(BUFF_AT + 8) == 30'000'000'000ll,
          "a data pack's registry section binds both registries at world load");
    g_files["a/content/spells.json"] = "{}";
    apply_registry_files(files);
    check(at<int64_t>(SPAWN0) == 650'000'000ll && at<int64_t>(BUFF_AT + 8) == 10'000'000'000ll, "a reload without the section leaves nothing behind");
    g_files.clear();
}

// registry-followups 1 over dev.32's registries: kiln.gamedata's own code layer (gamedata_api.cpp: "<label>: buffs buff_<id>.<path>",
// "<label>: sequences <name>.<path>") on a buff's or an ability's slot is named in Kiln words, never the game's entry and member.
void gamedata_code_writers_in_kiln_words() {
    g_files = {{"m/content/a.json", R"({"registry":{"alter":[
        {"registry":"buffs","select":"game:buff_cdbe9c80","field":"duration","op":{"mul":2}},
        {"registry":"abilities","select":"game:fire_cast","field":"timing.release","op":0.45}]}})"}};
    const auto files = files_of({{"m", "m/content/a.json"}});
    gd_fresh(files);
    set_owner_label(45, "mod spelltweaks");
    const int64_t nine = 9'000'000'000ll, half = 500'000'000ll;
    uint8_t b[8];
    std::memcpy(b, &nine, 8);
    check(store_put(45, next_code_seq(), GD_BUFFS, BUFF, 1, -1, A(BUFF_AT + 8), 8, b, "mod spelltweaks: buffs buff_cdbe9c80.defaultLifeTime"), "code layer on the buff");
    std::memcpy(b, &half, 8);
    check(store_put(45, next_code_seq(), GD_SEQUENCES, CAST, 4, -1, A(SPAWN0), 8, b, "mod spelltweaks: sequences Fire_Cast.events[SpawnEntityEvent#0].time"),
          "code layer on the cast");
    store_flush(nullptr);
    apply_registry_files(files);
    for (const auto& [ref, place] : {std::pair{registry::FieldRef{"buffs", "game:buff_cdbe9c80", "duration"}, "spelltweaks: buffs[game:buff_cdbe9c80].duration"},
                                     std::pair{registry::FieldRef{"abilities", "game:fire_cast", "timing.release"}, "spelltweaks: abilities[game:fire_cast].timing.release"}}) {
        const auto row = json_field(ref);
        registry::json code;
        for (const auto& w : row["writers"])
            if (w["kind"] == "code") code = w;
        check(!code.is_null() && code["who"] == place && code["mod"] == "spelltweaks", "the mod and the Kiln place: " + row["writers"].dump());
        check(row.dump().find("defaultLifeTime") == std::string::npos && row.dump().find("SpawnEntityEvent") == std::string::npos &&
                  row.dump().find("Fire_Cast") == std::string::npos,
              "no game entry or member name in the row: " + row.dump());
    }
    g_files.clear();
}

// review of dev.32 (P45): a stacking value this build's buffs lack is refused in Kiln words: the field, never the game's value
void buff_stacking_value_missing_names_the_kiln_field() {
    gd_fresh();
    g_no_replace_at_end = true;  // a build whose buffs have no ReplaceAtEnd: the slot's enum as kiln.gamedata gives it then
    g_gd_sections[KILN_GD_BUFFS].slots[{BUFF, "applyType"}].f.en = apply_types();
    RegistryLiveReply reply;
    const auto h = begin_change(71, "a", "[{" + buff + R"("field":"stacking","op":"replaceAtEnd"}])");
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Refused && at<uint8_t>(BUFF_AT + 5) == 1, "refused, nothing written: " + reply.reason);
    check(reply.reason.find("\"replaceAtEnd\" is not a value of buffs.stacking on this game build") != std::string::npos,
          "the refusal names the Kiln field: " + reply.reason);
    check(reply.reason.find("ReplaceAtEnd") == std::string::npos, "never the game's value: " + reply.reason);
}

// review of dev.32 (P17, P44): every event of a moment is the field's slot. A write of the second spawn that fails takes the
// whole change back (never a moment moved in part reported applied), named by the Kiln field; another writer on it after Kiln's
// layer is named by the Kiln field too.
void ability_moment_is_written_whole() {
    gd_fresh();
    RegistryLiveReply reply;
    const auto h = begin_change(71, "a", "[{" + cast + R"("field":"timing.release","op":0.45}])");
    g_fail_addrs = {A(SPAWN1)};
    registry_live_commit(71, h, &reply);
    g_fail_addrs.clear();
    check(reply.outcome == RegistryLiveOutcome::Refused && at<int64_t>(SPAWN0) == 650'000'000ll && at<int64_t>(SPAWN1) == 650'000'000ll,
          "a moment written in part is rolled back whole: " + reply.reason + " (" + std::to_string(at<int64_t>(SPAWN0)) + ")");
    check(reply.reason.find("abilities[game:fire_cast].timing.release") != std::string::npos, "the failure names the Kiln field: " + reply.reason);
    g_files = {{"a/content/spells.json", R"({"registry":{"alter":[{"registry":"abilities","select":"game:fire_cast","field":"timing.release","op":0.45}]}})"}};
    const auto files = files_of({{"a", "a/content/spells.json"}});
    gd_fresh(files);
    check(at<int64_t>(SPAWN1) == 450'000'000ll, "the pack moves the moment");
    mem<int64_t>(SPAWN1, 777'000'000ll);  // another writer on the second spawn
    apply_registry_files(files);           // a pack reload
    check(at<int64_t>(SPAWN1) == 777'000'000ll, "the other writer's value stays");
    check(reported("abilities[game:fire_cast].timing.release was written by another writer after Kiln's layer"), "the skipped row names the Kiln field");
    const auto c = begin_change(72, "b", "[{" + cast + R"("field":"timing.release","op":0.4}])");
    registry_live_commit(72, c, &reply);
    check(reply.outcome == RegistryLiveOutcome::Skipped, "a change over the moment another writer holds part of is skipped, never applied in part: " +
                                                             std::string(reply.outcome == RegistryLiveOutcome::Applied ? "Applied" : "other"));
    g_files.clear();
}

// review-int (the registry trial merge x LOOT-2): a re-authored pass's turn, prepared off the world tick and landed in one flush
// (registry_live_prepare, registry_live_install_turn), tells kiln.gamedata after that flush as every other write of the live
// back-end does (gd_section_written: an ability's events sorted again, the buff mirror following the game's records, the
// modified list for the content sync). The landing flushed without it: a Registry.Reauthor pass that moved an ability's moment
// left the events list out of the game's order, and one that changed a buff left Kiln's copy of the buffs behind.
void a_reauthored_turn_tells_gamedata_after_its_flush() {
    gd_fresh();
    RegistryLiveReply reply;
    const auto h = begin_change(71, "a", "[{" + cast + R"("field":"timing.release","op":0.45},{)" + buff + R"("field":"duration","op":20}])", 2);
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Applied && at<int64_t>(SPAWN0) == 450'000'000ll && at<int64_t>(BUFF_AT + 8) == 20'000'000'000ll,
          "the pass's first turn: the moment at 0.45 s, the buff 20 s");
    const int before = g_written;
    uint64_t ticket = 0;
    uint32_t stale = 0;
    const std::string next = "[{" + cast + R"("field":"timing.release","op":0.3},{)" + buff + R"("field":"duration","op":25}])";
    check(registry_live_prepare({{71, "a", KILN_REGISTRY_LIVE_WORLD_LOAD, doc_of("a", next)}}, {{71, h}}, registry_live_epoch(), &ticket, &stale) == KILN_OK && stale == 0,
          "the re-authored pass's turn is prepared");
    std::vector<RegistryLiveReply> replies;
    std::vector<uint64_t> handles;
    check(registry_live_install_turn(ticket, 0, &replies, &handles, &stale) == KILN_OK && stale == 0 && replies.size() == 2 &&
              replies[0].outcome == RegistryLiveOutcome::Applied && replies[1].outcome == RegistryLiveOutcome::Reverted,
          "and lands in one step");
    check(at<int64_t>(SPAWN0) == 300'000'000ll && at<int64_t>(SPAWN1) == 300'000'000ll && at<int64_t>(SPAWN2) == 900'000'000ll &&
              at<int64_t>(BUFF_AT + 8) == 25'000'000'000ll,
          "the moment moved to 0.3 s, the buff 25 s");
    check(g_written > before, "kiln.gamedata is told after the landed turn's flush (" + std::to_string(g_written - before) + " times)");
    registry_live_revert(71, handles.empty() ? 0 : handles[0], &reply);
    check(at<int64_t>(SPAWN0) == 650'000'000ll && at<int64_t>(BUFF_AT + 8) == 10'000'000'000ll, "its revert puts the game's values back");
}

// (R2b's "without the hooks": on the one writer kiln.gamedata is always linked; what it can lack is this world's entries)
void without_gamedata_entries_refused() {
    g_hooks = false;
    gd_fresh();
    g_hooks = true;
    RegistryLiveReply reply;
    const auto h = begin_change(71, "a", "[{" + buff + R"("field":"duration","op":5}])");
    registry_live_commit(71, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Refused && reply.reason.find("buffs[game:buff_cdbe9c80].duration: the entry is not in this world") != std::string::npos &&
              at<int64_t>(BUFF_AT + 8) == 10'000'000'000ll,
          "without kiln.gamedata's buff in this world the registry refuses, named: " + reply.reason);
}
}  // namespace

int main() {
    std::vector<std::string> errs;
    g_regmap = std::make_shared<registry::map::RegMap>();
    if (!registry::map::load_dir(KILN_REGMAP_DIR, "server", "f0d824be7fcd48ebfe2458ff5c28da26060c2074e3ec1fcac587e5a1d508aeb2", g_regmap.get(), &errs)) return 2;
    int failed = 0;
    const std::pair<const char*, void (*)()> tests[] = {
        {"buff_duration_read_write_revert", buff_duration_read_write_revert},
        {"buff_stacking_enum", buff_stacking_enum},
        {"buff_pack_below_and_foreign_writer", buff_pack_below_and_foreign_writer},
        {"ability_release_moves_the_moment", ability_release_moves_the_moment},
        {"ability_without_the_moment_refuses_that_entry", ability_without_the_moment_refuses_that_entry},
        {"ability_without_the_moment_reads_null", ability_without_the_moment_reads_null},
        {"data_section_binds_at_world_load", data_section_binds_at_world_load},
        {"gamedata_code_writers_in_kiln_words", gamedata_code_writers_in_kiln_words},
        {"without_gamedata_entries_refused", without_gamedata_entries_refused},
        {"buff_stacking_value_missing_names_the_kiln_field", buff_stacking_value_missing_names_the_kiln_field},
        {"ability_moment_is_written_whole", ability_moment_is_written_whole},
        {"a_reauthored_turn_tells_gamedata_after_its_flush", a_reauthored_turn_tells_gamedata_after_its_flush},
    };
    for (const auto& [name, fn] : tests) {
        try {
            fn();
        } catch (const std::exception& e) {
            ++failed;
            std::printf("registry-live-gamedata-test: FAIL %s: %s\n", name, e.what());
        }
    }
    std::printf("registry-live-gamedata-test: %d checks, %d failed\n", checks, failed);
    return failed ? 1 : 0;
}
