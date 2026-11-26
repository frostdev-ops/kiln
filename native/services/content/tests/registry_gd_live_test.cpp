// SPDX-License-Identifier: MIT
// kiln.content, host test: dev.32's registries over kiln.gamedata's sections (creatures, arsenals, perks) in the live back-end
// (registry_live.cpp bind_section and its binders), over the real store, IR and composition, on a made-up gamedata world
// (registry_live_test.cpp's GdSectionFake: entries by GUID, value slots by path, components an entry lacks, a perk's one value).
// Each live field: write, read, revert, a relative op over the value below, another writer, a per-entry refusal in Kiln
// words; a perk's whole-number value refuses a fraction (not_exact) and its tooltip row follows; an arsenal keeps its attacks.
// registry_gd_live_test.sh builds and runs it.
#define main registry_live_baseline_main
#include "registry_live_test.cpp"
#undef main
#include "services/kiln_gamedata.h"

namespace {
using namespace kc;

constexpr uint32_t VUKAH = 0xC1, WOLF = 0xC2, ARS = 0xA1, PERK_INT = 0xB1, PERK_FLOAT = 0xB2, PERK_ARMOR = 0xB3;
const uint8_t C1[16] = {0xc1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1}, C2[16] = {0xc2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2},
              A1[16] = {0xa1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3}, B1[16] = {0xb1, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4},
              B2[16] = {0xb2, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5}, B3[16] = {0xb3, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6};
constexpr size_t VUKAH_AT = 0x50000, WOLF_AT = 0x50100, ARS_AT = 0x51000, PERK_INT_AT = 0x52000, PERK_FLOAT_AT = 0x52100, PERK_ARMOR_AT = 0x52200;
const char* const kLeaves[] = {"selectionChance", "threat", "minRange", "maxRange", "maxVerticalRange"};

Entry gd_entry(uint32_t id, const char* name, size_t at, const uint8_t g[16]) {
    Entry e;
    e.id = id, e.name = name, e.addr = A(at);
    std::memcpy(e.guid, g, 16);
    return e;
}
void gd_slot_at(uint32_t section, uint32_t id, const std::string& path, size_t at, uint8_t bytes, uint8_t vt, std::shared_ptr<EnumInfo> en = nullptr,
                size_t tip = 0) {
    GdValueSlot s;
    s.addr = A(at), s.kind = 16 + section, s.id = id, s.field = static_cast<int32_t>(g_gd_sections[section].slots.size());
    s.f.path = path, s.f.offset = 0, s.f.bytes = bytes, s.f.vt = vt, s.f.writable = true, s.f.is_signed = vt == KILN_CV_INT;
    if (en) s.f.enum_kind = KILN_CONTENT_ENUM, s.f.en = std::move(en);
    if (tip) s.tip = A(tip), s.tip_format = 0;
    g_gd_sections[section].slots[{id, path}] = s;
}

// the made-up gamedata world, beside registry_live_test's items
void gd_world() {
    world();
    g_gd_sections.clear();
    auto factions = std::make_shared<EnumInfo>();
    factions->values = {{"Environment", 0}, {"Vukah", 3}, {"Wildlife", 5}, {"PlayerAlly", 11}};
    GdSectionFake& T = g_gd_sections[KILN_GD_TEMPLATES];
    T.entries[guid_text(C1)] = gd_entry(VUKAH, "Enemy_Vukah_Melee", VUKAH_AT, C1);
    T.entries[guid_text(C2)] = gd_entry(WOLF, "Animal_Wildlife_Wolf", WOLF_AT, C2);
    gd_slot_at(KILN_GD_TEMPLATES, VUKAH, "Level.dataStorage[0]", VUKAH_AT + 0x0, 4, KILN_CV_INT);
    gd_slot_at(KILN_GD_TEMPLATES, VUKAH, "Faction.faction", VUKAH_AT + 0x4, 1, KILN_CV_INT, factions);
    gd_slot_at(KILN_GD_TEMPLATES, VUKAH, "Enemy01BehaviorState.combatCommitmentSetup.view.range", VUKAH_AT + 0x8, 4, KILN_CV_FLOAT);
    gd_slot_at(KILN_GD_TEMPLATES, VUKAH, "DamageSusceptibility.dataStorage[2]", VUKAH_AT + 0xC, 4, KILN_CV_FLOAT);
    T.absent.insert({VUKAH, "ScaleComponent.uniformScale"});  // the Vukah has no size of its own
    gd_slot_at(KILN_GD_TEMPLATES, WOLF, "Level.dataStorage[0]", WOLF_AT + 0x0, 4, KILN_CV_INT);
    gd_slot_at(KILN_GD_TEMPLATES, WOLF, "ScaleComponent.uniformScale", WOLF_AT + 0x4, 4, KILN_CV_FLOAT);
    mem<uint32_t>(VUKAH_AT, 7), mem<uint8_t>(VUKAH_AT + 4, 3), mem<float>(VUKAH_AT + 8, 18.0f), mem<float>(VUKAH_AT + 0xC, 1.5f);
    mem<uint32_t>(WOLF_AT, 3), mem<float>(WOLF_AT + 4, 1.5f);
    GdSectionFake& R = g_gd_sections[KILN_GD_ARSENALS];
    R.entries[guid_text(A1)] = gd_entry(ARS, "", ARS_AT, A1);
    R.lists[{ARS, "attacks"}] = 2;
    for (int i = 0; i < 2; ++i)
        for (int k = 0; k < 5; ++k)
            gd_slot_at(KILN_GD_ARSENALS, ARS, "attacks[" + std::to_string(i) + "].description." + kLeaves[k], ARS_AT + 0x20 * i + 4 * k, 4, KILN_CV_FLOAT);
    mem<float>(ARS_AT + 0x00, 1.0f), mem<float>(ARS_AT + 0x04, 9.0f), mem<float>(ARS_AT + 0x0C, 2.5f);  // attack 0
    mem<float>(ARS_AT + 0x20, 0.5f), mem<float>(ARS_AT + 0x24, 3.0f), mem<float>(ARS_AT + 0x2C, 8.0f);  // attack 1
    GdSectionFake& P = g_gd_sections[KILN_GD_PERKS];
    P.entries[guid_text(B1)] = gd_entry(PERK_INT, "Perk_Max_Adder_Health_T7", PERK_INT_AT, B1);
    P.entries[guid_text(B2)] = gd_entry(PERK_FLOAT, "Perk_Mana_Cost_Mod_10", PERK_FLOAT_AT, B2);
    P.entries[guid_text(B3)] = gd_entry(PERK_ARMOR, "Perk_Armor_Fog", PERK_ARMOR_AT, B3);
    P.shown[PERK_INT] = "impactValues.0x0a0c87e7", P.shown[PERK_FLOAT] = "impactValues.0x0a0c87e7", P.shown[PERK_ARMOR] = "none";
    gd_slot_at(KILN_GD_PERKS, PERK_INT, "impactValues.0x0a0c87e7", PERK_INT_AT, 4, KILN_CV_INT, nullptr, PERK_INT_AT + 4);
    gd_slot_at(KILN_GD_PERKS, PERK_FLOAT, "impactValues.0x0a0c87e7", PERK_FLOAT_AT, 4, KILN_CV_FLOAT, nullptr, PERK_FLOAT_AT + 4);
    gd_slot_at(KILN_GD_PERKS, PERK_ARMOR, "perkArmorSetup.isSet", PERK_ARMOR_AT, 1, KILN_CV_BOOL);
    gd_slot_at(KILN_GD_PERKS, PERK_ARMOR, "perkArmorSetup.distribution.fog", PERK_ARMOR_AT + 4, 4, KILN_CV_FLOAT);
    mem<int32_t>(PERK_INT_AT, 140), mem<int32_t>(PERK_INT_AT + 4, 140);
    mem<float>(PERK_FLOAT_AT, -0.1f), mem<int32_t>(PERK_FLOAT_AT + 4, -10);
    mem<uint8_t>(PERK_ARMOR_AT, 1), mem<float>(PERK_ARMOR_AT + 4, 0.03f);
}

registry::Value vstruct(std::vector<std::pair<std::string, registry::Value>> kv) {
    registry::Value x;
    x.kind = registry::Value::Struct;
    std::sort(kv.begin(), kv.end(), [](auto& a, auto& b) { return a.first < b.first; });
    for (auto& [k, v] : kv) x.keys.push_back(k), x.list.push_back(v);
    return x;
}
registry::BaseModel gd_base() {
    using registry::Value;
    registry::BaseModel b = base();
    registry::BaseModel::Entry v;
    v.id = "game:enemy_vukah_melee";
    v.fields["level"] = Value::integer(7), v.fields["faction"] = Value::text("vukah"), v.fields["sight.range"] = Value::number(18);
    v.fields["resist.cut"] = Value::number(1.5), v.fields["scale"] = Value{}, v.fields["kind"] = Value::text("enemy");
    v.traits.insert("enemy");
    registry::BaseModel::Entry w;
    w.id = "game:animal_wildlife_wolf";
    w.fields["level"] = Value::integer(3), w.fields["scale"] = Value::number(1.5), w.fields["kind"] = Value::text("wildlife");
    w.traits.insert("wildlife");
    b.registries["creatures"] = {v, w};
    registry::BaseModel::Entry a;
    a.id = "game:enemy_vukah_melee";
    a.fields["attacks"] = Value::of_list({vstruct({{"chance", Value::number(1)}, {"threat", Value::number(9)}, {"minRange", Value::number(0)},
                                                   {"maxRange", Value::number(2.5)}, {"maxHeight", Value::number(0)}}),
                                          vstruct({{"chance", Value::number(0.5)}, {"threat", Value::number(3)}, {"minRange", Value::number(0)},
                                                   {"maxRange", Value::number(8)}, {"maxHeight", Value::number(0)}})});
    b.registries["arsenals"] = {a};
    registry::BaseModel::Entry p1, p2, p3;
    p1.id = "game:perk_max_adder_health_t7", p1.fields["value"] = Value::number(140);
    p2.id = "game:perk_mana_cost_mod_10", p2.fields["value"] = Value::number(-0.1);
    p3.id = "game:perk_armor_fog", p3.fields["value"] = Value{}, p3.fields["armor.enabled"] = Value::boolean(true), p3.fields["armor.shroud"] = Value::number(0.03);
    b.registries["perks"] = {p1, p2, p3};
    return b;
}

bool g_vukah_by_num = false;  // the id table names the Vukah by its 32-bit id only (an element entry without a GUID, a skill node's way)

// registry_live_test's stage() with the gamedata registries' entries and ids
void stage_gd(const std::vector<PackFile>& files) {
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
    in.ids = {{"items|game:material_t1_wood_logs", guid_text(G1)}, {"creatures|game:enemy_vukah_melee", guid_text(C1)},
              {"creatures|game:animal_wildlife_wolf", guid_text(C2)}, {"arsenals|game:enemy_vukah_melee", guid_text(A1)},
              {"perks|game:perk_max_adder_health_t7", guid_text(B1)}, {"perks|game:perk_mana_cost_mod_10", guid_text(B2)},
              {"perks|game:perk_armor_fog", guid_text(B3)}};
    in.nums = {{"items|game:material_t1_wood_logs", WOOD}};
    if (g_vukah_by_num) in.ids["creatures|game:enemy_vukah_melee"] = "", in.nums["creatures|game:enemy_vukah_melee"] = VUKAH;
    for (const char* f : {"creatures.level", "creatures.faction", "creatures.scale", "creatures.sight.range", "creatures.resist.cut", "arsenals.attacks",
                          "perks.value", "perks.armor.enabled", "perks.armor.shroud"})
        in.decoded.insert(f);
    in.report = [](const registry::Result& r, bool) {
        g_last_report = r;
        ++g_reports;
    };
    registry_live_install(std::move(in));
}
std::vector<PackFile> pack(const std::string& edits) {
    g_files = {{"m/content/m.json", R"({"registry": {"alter": )" + edits + "}}"}};
    return files_of({{"m", "m/content/m.json"}});
}
void apply_gd(const std::string& edits) {
    const auto files = pack(edits);
    stage_gd(files);
    registry_live_world_begin();
    apply_registry_files(files);
}
std::string read(const std::string& reg, const std::string& id, const std::string& field) {
    std::string out;
    return registry_live_read(reg, id, field, &out) == KILN_OK ? out : "ERR";
}
std::string refusals() {
    std::string out;
    for (const registry::Refusal& r : g_last_report.refused) out += "\n  [" + r.registry + "|" + r.id + "|" + r.field + "] " + r.reason;
    return out;
}
bool no_game_names() {
    for (const registry::Refusal& r : g_last_report.refused)
        for (const char* game : {"Level", "dataStorage", "ScaleComponent", "Faction.faction", "impactValues", "selectionChance", "perkArmorSetup", "0x0a0c87e7"})
            if (r.reason.find(game) != std::string::npos) return false;
    return true;
}

// creatures: scalars and an enum on a template's components, write, read, relative over the value below, revert
void creature_fields_write_read_revert() {
    gd_world();
    apply_gd(R"([{"registry": "creatures", "select": "game:enemy_vukah_melee", "field": "level", "op": 12},
                 {"registry": "creatures", "select": "game:enemy_vukah_melee", "field": "faction", "op": "playerAlly"},
                 {"registry": "creatures", "select": "game:enemy_vukah_melee", "field": "sight.range", "op": {"mul": 2}},
                 {"registry": "creatures", "select": "game:animal_wildlife_wolf", "field": "scale", "op": 3}])");
    check(at<uint32_t>(VUKAH_AT) == 12, "creatures.level written to the template's level");
    check(at<uint8_t>(VUKAH_AT + 4) == 11, "creatures.faction: the Kiln name as the game's enum value");
    check(at<float>(VUKAH_AT + 8) == 36.0f, "creatures.sight.range: a relative op over the value below");
    check(at<float>(WOLF_AT + 4) == 3.0f, "creatures.scale where the creature has one");
    check(read("creatures", "game:enemy_vukah_melee", "level") == "12" && read("creatures", "game:enemy_vukah_melee", "faction") == "\"playerAlly\"",
          "read back through the schema: " + read("creatures", "game:enemy_vukah_melee", "faction"));
    check(g_last_report.refused.empty(), "nothing refused");
    apply_registry_files(pack("[]"));  // the section goes: nothing of it stays
    check(at<uint32_t>(VUKAH_AT) == 7 && at<uint8_t>(VUKAH_AT + 4) == 3 && at<float>(VUKAH_AT + 8) == 18.0f && at<float>(WOLF_AT + 4) == 1.5f,
          "every creature field back to the game's value");
}

// a creature without the component a field lives in: an absolute value is refused for that entry alone, in Kiln words, the
// others apply; a relative op has no number to work on there, so composition refuses that edit whole (an edit is all or
// nothing: select the creatures that have one)
void creature_without_the_component() {
    gd_world();
    apply_gd(R"([{"registry": "creatures", "select": "game:*", "field": "scale", "op": 2}])");
    check(at<float>(WOLF_AT + 4) == 2.0f, "the wolf, which has a size, takes the edit" + refusals());
    check(reported("creatures[game:enemy_vukah_melee].scale: this creature has no value of this field (only some creatures have one)"),
          "the Vukah, which has none, is refused alone, named in Kiln words" + refusals());
    check(no_game_names(), "no game member in a refusal");
    apply_gd(R"([{"registry": "creatures", "select": "game:*", "field": "scale", "op": {"mul": 2}}])");
    check(at<float>(WOLF_AT + 4) == 1.5f && reported("scale has no number to change here"), "a relative op over a creature without one: the edit is refused" + refusals());
}

// another writer changed a creature's slot after Kiln's layer: kept, reported with the Kiln place
void creature_another_writer() {
    gd_world();
    const auto files = pack(R"([{"registry": "creatures", "select": "game:enemy_vukah_melee", "field": "level", "op": 20}])");
    stage_gd(files);
    registry_live_world_begin();
    apply_registry_files(files);
    check(at<uint32_t>(VUKAH_AT) == 20, "applied");
    mem<uint32_t>(VUKAH_AT, 55);  // a ShroudForge run-time write in kiln mode
    apply_registry_files(files);
    check(at<uint32_t>(VUKAH_AT) == 55, "the other writer's value stays");
    check(reported("creatures[game:enemy_vukah_melee].level was written by another writer after Kiln's layer"), "the row names the Kiln place");
    check(no_game_names(), "no game member in a refusal");
}

// a Manual change on a creature: begin, submit, commit, read, revert (all or nothing)
void creature_manual_change() {
    gd_world();
    apply_gd("[]");
    uint64_t h = 0;
    RegistryLiveReply reply;
    check(registry_live_begin(77, "change", 4, &h) == KILN_OK && h, "begin");
    const std::string ir = R"({"ir":1,"mod":"change","stage":"alter","edits":[{"registry":"creatures","select":"game:enemy_vukah_melee","field":"resist.cut","op":0.25}]})";
    check(registry_live_submit(77, h, ir, &reply, nullptr) == KILN_OK, "submit");
    check(registry_live_commit(77, h, &reply) == KILN_OK && reply.outcome == RegistryLiveOutcome::Applied, "commit: " + reply.reason);
    check(at<float>(VUKAH_AT + 0xC) == 0.25f && read("creatures", "game:enemy_vukah_melee", "resist.cut") == "0.25", "written and read back");
    check(registry_live_revert(77, h, &reply) == KILN_OK && at<float>(VUKAH_AT + 0xC) == 1.5f, "revert restores the game's value");
    // Manual is all or nothing: a creature without the component refuses the whole change
    h = 0;
    registry_live_begin(77, "change", 4, &h);
    const std::string both = R"({"ir":1,"mod":"change","stage":"alter","edits":[{"registry":"creatures","select":"game:animal_wildlife_wolf","field":"level","op":9},
        {"registry":"creatures","select":"game:enemy_vukah_melee","field":"scale","op":2}]})";
    registry_live_submit(77, h, both, &reply, nullptr);
    registry_live_commit(77, h, &reply);
    check(reply.outcome == RegistryLiveOutcome::Refused && at<uint32_t>(WOLF_AT) == 3, "a refused entry refuses the Manual change whole: " + reply.reason);
}

// perks.value: the perk's one value (a whole-number or a fractional config), its tooltip row following
void perk_value_and_tooltip() {
    gd_world();
    apply_gd(R"([{"registry": "perks", "select": "game:perk_max_adder_health_t7", "field": "value", "op": 150},
                 {"registry": "perks", "select": "game:perk_mana_cost_mod_10", "field": "value", "op": {"mul": 2}}])");
    check(at<int32_t>(PERK_INT_AT) == 150 && at<int32_t>(PERK_INT_AT + 4) == 150, "a whole-number value and its tooltip row");
    check(std::fabs(at<float>(PERK_FLOAT_AT) + 0.2f) < 1e-6f && at<int32_t>(PERK_FLOAT_AT + 4) == 0, "a fractional value; the row follows its format");
    check(std::stod(read("perks", "game:perk_max_adder_health_t7", "value")) == 150, "read back: " + read("perks", "game:perk_max_adder_health_t7", "value"));
    apply_gd(R"([{"registry": "perks", "select": "game:perk_max_adder_health_t7", "field": "value", "op": 150.5},
                 {"registry": "perks", "select": "game:perk_armor_fog", "field": "value", "op": 2}])");
    check(at<int32_t>(PERK_INT_AT) == 140 && at<int32_t>(PERK_INT_AT + 4) == 140, "a fraction on a whole-number value: not written, never rounded");
    check(reported("not_exact: perks.value of game:perk_max_adder_health_t7 is a whole number on this build"), "refused with not_exact");
    check(reported("perks[game:perk_armor_fog].value: this perk has no value"), "a perk without a value is refused alone");
    check(no_game_names(), "no game member in a refusal");
    apply_registry_files(pack("[]"));
    check(at<int32_t>(PERK_INT_AT) == 140 && std::fabs(at<float>(PERK_FLOAT_AT) + 0.1f) < 1e-6f && at<int32_t>(PERK_FLOAT_AT + 4) == -10, "reverted with its rows");
}

void perk_armor() {
    gd_world();
    apply_gd(R"([{"registry": "perks", "select": "game:perk_armor_fog", "field": "armor.shroud", "op": {"mul": 2}},
                 {"registry": "perks", "select": "game:perk_armor_fog", "field": "armor.enabled", "op": false}])");
    check(std::fabs(at<float>(PERK_ARMOR_AT + 4) - 0.06f) < 1e-6f && at<uint8_t>(PERK_ARMOR_AT) == 0, "a perk's armour share and its switch");
    apply_registry_files(pack("[]"));
    check(at<uint8_t>(PERK_ARMOR_AT) == 1 && std::fabs(at<float>(PERK_ARMOR_AT + 4) - 0.03f) < 1e-6f, "reverted");
}

// the perk damage bonus is unproven (its read time is not traced): refused with the reason, nothing written
void perk_damage_unproven() {
    gd_world();
    apply_gd(R"([{"registry": "perks", "select": "game:perk_armor_fog", "field": "damage.ice", "op": 0.5}])");
    check(reported("Unproven: perks.damage.ice") && reported("when the game reads a perk's damage bonus is not traced yet"), "refused, with its reason");
}

// arsenals.attacks: an element's leaf by its index; the list keeps its attacks
void arsenal_attacks() {
    gd_world();
    apply_gd(R"([{"registry": "arsenals", "select": "game:enemy_vukah_melee", "field": "attacks[1].chance", "op": 0.25},
                 {"registry": "arsenals", "select": "game:enemy_vukah_melee", "field": "attacks[*].maxRange", "op": {"mul": 2}}])");
    check(at<float>(ARS_AT + 0x20) == 0.25f && at<float>(ARS_AT + 0x00) == 1.0f, "attacks[1].chance alone");
    check(at<float>(ARS_AT + 0x0C) == 5.0f && at<float>(ARS_AT + 0x2C) == 16.0f, "attacks[*].maxRange on every attack");
    const std::string list = read("arsenals", "game:enemy_vukah_melee", "attacks");
    check(list.find("\"chance\":0.25") != std::string::npos && list.find("\"maxRange\":16") != std::string::npos, "read back: " + list);
    check(read("arsenals", "game:enemy_vukah_melee", "attacks[1].chance") == "0.25", "an element's leaf read: " + read("arsenals", "game:enemy_vukah_melee", "attacks[1].chance"));
    apply_gd(R"([{"registry": "arsenals", "select": "game:enemy_vukah_melee", "field": "attacks",
                   "list": {"append": [{"chance": 1, "threat": 1, "minRange": 0, "maxRange": 1, "maxHeight": 0}]}}])");
    check(at<float>(ARS_AT + 0x20) == 0.5f && at<float>(ARS_AT + 0x2C) == 8.0f, "an attack appended: nothing written");
    check(reported("an arsenal keeps its attacks (only their values change)"), "refused: an arsenal keeps its attacks" + refusals());
    apply_registry_files(pack("[]"));
    check(at<float>(ARS_AT + 0x0C) == 2.5f, "reverted");
}

// a read-only field (creatures.kind, creatures.arsenal) takes no edit; an unknown creature is refused per entry
void creature_read_only_and_unknown() {
    gd_world();
    const auto files = pack(R"([{"registry": "creatures", "select": "game:enemy_vukah_melee", "field": "kind", "op": "animal"},
                                {"registry": "creatures", "select": "game:enemy_vukah_melee", "field": "arsenal", "op": "game:enemy_vukah_melee"},
                                {"registry": "creatures", "select": "game:no_such_creature", "field": "level", "op": 3}])");
    bool kind = false, arsenal = false;
    for (const RegistrySection& sec : registry_sections(files, registry_schema()))
        for (const registry::Refusal& r : sec.refused) {
            kind = kind || (r.field == "kind" && r.reason.find("read-only") != std::string::npos);
            arsenal = arsenal || (r.field == "arsenal" && r.reason.find("read-only") != std::string::npos);
        }
    check(kind && arsenal, "kind and arsenal are read-only");
    stage_gd(files);
    registry_live_world_begin();
    apply_registry_files(files);
    check(at<uint32_t>(VUKAH_AT) == 7, "nothing written");
    check(reported("no entry game:no_such_creature in creatures"), "an id that names no creature is refused" + refusals());
}
// registry-followups 1 for the gamedata registries: a code writer through kiln.gamedata's set on a creature's slot is named in
// Kiln words (the mod and creatures[<id>].<field>), never the template's or the member's name
void creature_code_writer_in_kiln_words() {
    gd_world();
    const auto files = pack(R"([{"registry": "creatures", "select": "game:enemy_vukah_melee", "field": "level", "op": 12}])");
    stage_gd(files);
    registry_live_world_begin();
    apply_registry_files(files);
    set_owner_label(46, "mod creaturetweaks");
    const uint32_t v = 30;
    uint8_t b[4];
    std::memcpy(b, &v, 4);
    const GdValueSlot& s = g_gd_sections[KILN_GD_TEMPLATES].slots[{VUKAH, "Level.dataStorage[0]"}];
    // as gamedata_api.cpp t_set puts it on a template's value
    check(store_put(46, next_code_seq(), s.kind, VUKAH, s.field, -1, s.addr, 4, b, "mod creaturetweaks: templates Enemy_Vukah_Melee.Level.dataStorage[0]"),
          "kiln.gamedata's code layer");
    store_flush(nullptr);
    apply_registry_files(files);
    check(at<uint32_t>(VUKAH_AT) == 30, "code wins over the registry");
    const auto row = json_field({"creatures", "game:enemy_vukah_melee", "level"});
    registry::json code;
    for (const auto& w : row["writers"])
        if (w["kind"] == "code") code = w;
    check(!code.is_null() && code["who"] == "creaturetweaks: creatures[game:enemy_vukah_melee].level" && code["mod"] == "creaturetweaks",
          "the mod and the Kiln place: " + row["writers"].dump());
    check(row.dump().find("Enemy_Vukah_Melee") == std::string::npos && row.dump().find("dataStorage") == std::string::npos,
          "no game template or member name in the row: " + row.dump());
}
// an entry the id table names by its 32-bit id only (no GUID: an element entry such as a skill node) binds by that id
void entry_by_its_number() {
    gd_world();
    g_vukah_by_num = true;
    apply_gd(R"([{"registry": "creatures", "select": "game:enemy_vukah_melee", "field": "level", "op": 21}])");
    g_vukah_by_num = false;
    check(at<uint32_t>(VUKAH_AT) == 21 && g_last_report.refused.empty(), "bound by its 32-bit id" + refusals());
}
}  // namespace

int main() {
    std::vector<std::string> errs;
    g_registry_stack_guard = &fake_guard;
    g_regmap = std::make_shared<registry::map::RegMap>();
    if (!registry::map::load_dir(KILN_REGMAP_DIR, "server", "f0d824be7fcd48ebfe2458ff5c28da26060c2074e3ec1fcac587e5a1d508aeb2", g_regmap.get(), &errs) || !g_regmap->has_build) {
        std::printf("FAIL cannot load the regmap at %s\n", KILN_REGMAP_DIR);
        return 1;
    }
    const std::pair<const char*, void (*)()> tests[] = {
        {"creature_fields_write_read_revert", creature_fields_write_read_revert},
        {"creature_without_the_component", creature_without_the_component},
        {"creature_another_writer", creature_another_writer},
        {"creature_manual_change", creature_manual_change},
        {"perk_value_and_tooltip", perk_value_and_tooltip},
        {"perk_armor", perk_armor},
        {"perk_damage_unproven", perk_damage_unproven},
        {"arsenal_attacks", arsenal_attacks},
        {"creature_read_only_and_unknown", creature_read_only_and_unknown},
        {"creature_code_writer_in_kiln_words", creature_code_writer_in_kiln_words},
        {"entry_by_its_number", entry_by_its_number},
    };
    int failed = 0;
    for (auto& [name, fn] : tests) {
        try {
            fn();
            std::printf("ok   %s\n", name);
        } catch (const std::exception& e) {
            ++failed;
            std::printf("FAIL %s: %s\n", name, e.what());
        }
    }
    std::printf("registry-gd-live-test: %d checks, %d failed\n", checks, failed);
    return failed ? 1 : 0;
}
