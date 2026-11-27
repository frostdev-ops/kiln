// SPDX-License-Identifier: MIT
// Host test of registry/saves.cpp (registry-rollback, package K): every class of edits that change what existing saves hold,
// with its decision (refuse, guard, warn, report), the fields a save holds nothing of, the value a chain's registry rows start
// from, and the notes of a result.
#include <cstdio>
#include <string>

#include "../registry/saves.h"

using namespace kc::registry;
namespace sv = kc::registry::saves;

static int g_fail = 0, g_pass = 0;
#define CHECK(cond, what)                                              \
    do {                                                               \
        if (cond) ++g_pass;                                            \
        else {                                                         \
            ++g_fail;                                                  \
            std::printf("FAIL %s:%d %s\n", __FILE__, __LINE__, what); \
        }                                                              \
    } while (0)

static Value I(int64_t v) { return Value::integer(v); }
static Value F(double v) { return Value::number(v); }
static Value L(std::vector<Value> v) { return Value::of_list(std::move(v)); }
static Value T(const char* t) { return Value::text(t); }
static Value xyz(double x, double y, double z) {
    Value v;
    v.kind = Value::Struct, v.keys = {"x", "y", "z"}, v.list = {F(x), F(y), F(z)};
    return v;
}
static bool is(const std::optional<sv::Verdict>& v, const char* cls, sv::Decision d) { return v && v->cls == cls && v->decision == d; }

int main() {
    // stackCut: a lower max stack would make the game delete the part of a saved stack above it; Kiln guards it
    const auto stack = sv::judge("items", "maxStack", I(250), I(100));
    CHECK(is(stack, "stackCut", sv::Decision::Guard) && stack->text.find("250 -> 100") != std::string::npos &&
              stack->unguarded.find("this edit was not applied") != std::string::npos, "a lower max stack: guard (refused without the guard)");
    CHECK(!sv::judge("items", "maxStack", I(250), I(1000)), "a higher max stack changes nothing a save holds");
    // levelRange: a narrower range clamps saved copies at load and the save keeps it: refused
    CHECK(is(sv::judge("items", "levels.max", I(55), I(30)), "levelRange", sv::Decision::Refuse), "levels.max lowered: refused");
    CHECK(is(sv::judge("items", "levels.min", I(1), I(5)), "levelRange", sv::Decision::Refuse), "levels.min raised: refused");
    CHECK(!sv::judge("items", "levels.max", I(30), I(55)) && !sv::judge("items", "levels.min", I(5), I(1)), "a wider range: applied");
    // perkSlots: an existing slot changed or emptied: warn; a perk into an empty slot: nothing
    const Value perks = L({T("game:perk_a"), T("game:perk_b")});
    CHECK(is(sv::judge("items", "perks", perks, L({T("game:perk_a"), T("game:perk_c")})), "perkSlots", sv::Decision::Warn), "slot 1 replaced: warn");
    CHECK(is(sv::judge("items", "perks", perks, L({T("game:perk_a")})), "perkSlots", sv::Decision::Warn), "slot 1 removed: warn");
    CHECK(!sv::judge("items", "perks", perks, L({T("game:perk_a"), T("game:perk_b"), T("game:perk_c")})), "a perk appended to a free slot: nothing");
    // durability: copies keep their share; nothing lost: report
    CHECK(is(sv::judge("balance", "durability.start", F(100), F(50)), "durability", sv::Decision::Report), "durability.start: report");
    CHECK(is(sv::judge("balance", "rarityDurability.epic", F(1.5), F(2)), "durability", sv::Decision::Report), "rarityDurability: report");
    // dev.32 creatures: a lower highest level can cut the level of a saved creature (a villager, a farm animal) as it loads: warn;
    // every other creature, arsenal and perk field is read at use or copied into new creatures only
    CHECK(is(sv::judge("creatures", "levelMax", I(105), I(50)), "creatureLevel", sv::Decision::Warn), "creatures.levelMax lowered: warn");
    CHECK(!sv::judge("creatures", "levelMax", I(105), I(200)) && !sv::judge("creatures", "level", I(7), I(12)) &&
              !sv::judge("creatures", "faction", Value::text("vukah"), Value::text("playerAlly")) && !sv::judge("perks", "value", I(140), I(150)) &&
              !sv::judge("arsenals", "attacks", Value::of_list({}), Value::of_list({I(1)})),
          "a higher highest level, a level, a faction, a perk's value, an arsenal change nothing a save holds");
    // progressionCap: level cap or skill points lowered: warn
    CHECK(is(sv::judge("balance", "levelCap", I(50), I(40)), "progressionCap", sv::Decision::Warn), "levelCap lowered: warn");
    CHECK(!sv::judge("balance", "levelCap", I(50), I(60)), "levelCap raised: nothing");
    CHECK(is(sv::judge("balance", "skillPointsPerLevel", I(2), I(1)), "progressionCap", sv::Decision::Warn), "skill points lowered: warn");
    // experienceCurve: any change: warn
    CHECK(is(sv::judge("balance", "xpPerLevel", L({I(100), I(200)}), L({I(100), I(300)})), "experienceCurve", sv::Decision::Warn), "xpPerLevel: warn");
    CHECK(is(sv::judge("balance", "xpCurve.end", I(9000), I(8000)), "experienceCurve", sv::Decision::Warn), "xpCurve: warn");
    // baseLimits: a per-level limit lowered: warn
    CHECK(is(sv::judge("balance", "altarsPerFlameLevel", L({I(1), I(2), I(3)}), L({I(1), I(1), I(3)})), "baseLimits", sv::Decision::Warn), "fewer altars at a level: warn");
    CHECK(!sv::judge("balance", "waterSourcesPerFlameLevel", L({I(1), I(2)}), L({I(1), I(4)})), "more water sources: nothing");
    const Value zones = L({xyz(40, 40, 40), xyz(60, 60, 60), xyz(80, 80, 80)});
    CHECK(is(sv::judge("balance", "buildzoneSizesPerAltarLevel", zones, L({xyz(40, 40, 40), xyz(50, 60, 60), xyz(80, 80, 80)})), "baseLimits", sv::Decision::Warn),
          "a smaller build zone at a level: warn");
    // listShape: the build-zone list shorter than the altar levels a save can hold: refused
    const auto shorter = sv::judge("balance", "buildzoneSizesPerAltarLevel", zones, L({xyz(40, 40, 40), xyz(60, 60, 60)}));
    CHECK(is(shorter, "listShape", sv::Decision::Refuse) && shorter->text.find("2 altar level(s) from 3") != std::string::npos, "a shorter build-zone list: refused");
    CHECK(!sv::judge("balance", "buildzoneSizesPerAltarLevel", zones, L({xyz(40, 40, 40), xyz(60, 60, 60), xyz(80, 80, 80), xyz(99, 99, 99)})), "a longer one: nothing");
    // no save state: read at use, or for new copies only
    for (const auto& [reg, field] : std::vector<std::pair<const char*, const char*>>{
             {"recipes", "duration"}, {"recipes", "inputs"}, {"loot", "stack.max"}, {"drops", "entries"}, {"items", "power"}, {"items", "rarity"},
             {"items", "socket"}, {"items", "manaCost"}, {"balance", "gear.seed"}, {"balance", "socketChance.rare"}, {"balance", "fogResistance.base"}})
        CHECK(!sv::judge(reg, field, I(10), I(1)), (std::string(reg) + "." + field + ": a save holds nothing it changes").c_str());
    CHECK(!sv::judge("items", "maxStack", I(10), I(10)), "an unchanged value: nothing");

    // before_registry: the value after the older writers (a pack's 300), not the base's
    FieldOutcome fo;
    fo.base = I(250);
    ChainRow pack;
    pack.kind = WriterKind::OldPack, pack.after = I(300);
    ChainRow reg;
    reg.kind = WriterKind::Registry, reg.mod = "m", reg.after = I(100);
    fo.chain = {pack, reg};
    fo.result = I(100);
    CHECK(sv::before_registry(fo) == I(300), "the registry rows start from the older pack's value");
    // notes: guard, warn and report rows; never a refused class, never a field without registry rows
    Result r;
    r.fields[{"items", "game:logs", "maxStack"}] = fo;
    FieldOutcome lv = fo;
    lv.base = I(55), lv.chain = {reg}, lv.result = I(30);
    r.fields[{"items", "game:sword", "levels.max"}] = lv;
    FieldOutcome only_pack;
    only_pack.base = I(50), only_pack.chain = {pack}, only_pack.result = I(300);
    r.fields[{"balance", "game:balance", "levelCap"}] = only_pack;
    const auto notes = sv::notes(r);
    CHECK(notes.size() == 1 && notes[0].cls == "stackCut" && notes[0].decision == "guard" && notes[0].field.id == "game:logs",
          "one note: the stack cut (the refused level range is a refusal, the pack-only field no registry edit)");
    const json j = sv::notes_json(notes);
    CHECK(j.size() == 1 && j[0]["field"] == "items[game:logs].maxStack" && j[0]["class"] == "stackCut" && j[0]["decision"] == "guard",
          "registry.json rows name the field, class and decision");

    std::printf("registry-saves-test: %d passed, %d failed\n", g_pass, g_fail);
    return g_fail ? 1 : 0;
}
