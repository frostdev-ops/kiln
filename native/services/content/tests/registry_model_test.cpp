// SPDX-License-Identifier: MIT
// kiln.content registry stage: the model (registry/model.cpp, slice S5) on the host, with ASan/UBSan. Over a fake tree view
// (decoded values, as the boot snapshot or a kiln-backend start serve them): entries and D3 ids of every R1 registry (a clash
// suffix, an unnamed entry, loot keyed by items, drop tables named by the templates that use them, the single balance table),
// values through the repository's regmap rows (int, enum, formula with the balance constants, ref, list of structs,
// stackCount, time, unlock, inventoryWalk, list), what the stage needs (needs_of), the base for compose (a game recipe whose
// input a mod removed is removed with it, reported), the boot back-end's writes (encode), the id table, release(), and the
// GUID of a mod's entry. Then the live bytes (kiln.assets' bytes through a base view, decoded through the game's reflection
// with gd::resolve / gd::node_json) on hand-made types.
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <stdexcept>

#include "../registry/model.h"
#include "../registry/schema_ir.h"

namespace kc {  // registry_model_fakes.cpp (content.h cannot share a unit with the registry's headers)
std::string guid_text(const uint8_t g[16]);
bool guid_parse(const std::string& s, uint8_t out[16]);
namespace rbtest {
void byte_blob_type(uintptr_t unreadable);
void wrapped_id_type();
void item_type();  // keen::ItemInfo {itemId u32 +0, objectId Guid +4, maxStackSize u16 +0x14, debugName BlobString +0x18}, 40 bytes
}
}  // namespace kc

namespace {
using namespace kc::registry;
using json = nlohmann::json;
int checks = 0;
void check(bool ok, const std::string& why) {
    if (!ok) throw std::runtime_error(why);
    ++checks;
}

std::string g(int n) {  // a test GUID
    char b[40];
    std::snprintf(b, sizeof b, "%08x-0000-4000-8000-%012x", n, n);
    return b;
}
KilnAssetKey key_of(const std::string& guid, const std::string& type) {
    KilnAssetKey k{};
    kc::guid_parse(guid, k.guid);
    k.type = kiln::boot_gd::fnv1a32(type);
    return k;
}

// A view of decoded trees: query "resource:<guid>/<hash>/<part>" -> {"value": ...}.
struct TreeView final : kiln::boot_view::BaseView {
    std::map<std::string, json> values;  // guid -> value
    std::map<std::string, std::vector<std::string>> types;  // type -> guids, archive order
    int reads = 0;
    bool released = false;
    const char* source() const override { return "snapshot"; }
    bool ready(std::string*) override { return true; }
    std::string digest() override { return released ? "" : "d" + std::to_string(reads); }
    KilnResult tree(std::string_view q, size_t cap, size_t, uint64_t, std::string* out, size_t* req, size_t*, std::string* why) override {
        const std::string s(q);
        const std::string guid = s.substr(9, 36);
        const auto it = values.find(guid);
        if (it == values.end()) return *why = "none", KILN_ERR_NOT_FOUND;
        const std::string text = json{{"resourceId", guid}, {"value", it->second}}.dump();
        if (text.size() + 1 > cap) return *req = text.size() + 1, KILN_ERR_BUFFER_TOO_SMALL;
        ++reads;
        *out = text;
        return KILN_OK;
    }
    KilnResult bytes(const KilnAssetKey&, size_t, std::string*, std::string* why) override { return *why = "trees only", KILN_ERR_UNAVAILABLE; }
    void release() override { released = true; }
    void add(const std::string& type, const std::string& guid, json v) {
        types[type].push_back(guid);
        values[guid] = std::move(v);
    }
};

const char* kBalance = "82706b40-61b1-4b8f-8b23-dcec6971bda1";

TreeView world() {
    TreeView v;
    auto item = [&](int n, const std::string& name, int stack, const std::string& rarity, double dmg) {
        json j = {{"itemId", 1000 + n}, {"objectId", g(n)}, {"maxStackSize", stack}, {"rarity", rarity}, {"generateRarity", false},
                  {"itemLevelRange", {{"minLevel", 1}, {"maxLevel", 5}}}, {"damageSetup", {{"dmgMod", dmg}, {"speedMod", 1.0}, {"isSet", dmg > 0}}},
                  {"armorSetup", {{"isSet", false}}}, {"perkReferences", json::array({g(900), "none"})}, {"perkIds", json::array({0, 0})}};
        if (!name.empty()) j["debugName"] = name;
        v.add("keen::ItemInfo", g(n), j);
    };
    item(1, "Material_T1_Wood_Logs", 10, "Common", 0);
    item(2, "Sword_T1", 1, "Rare", 1.0);
    item(3, "material_t1_wood_logs", 20, "Common", 0);  // the same name in another case: a clash
    item(4, "", 5, "Epic", 0);                          // no debug name
    v.add("keen::Perk", g(900), {{"debugName", "Perk_Strong"}});
    auto input_item = [](int item, int count) {
        return json{{"itemStack", {{"item", 1000 + item}, {"itemRef", g(item)}, {"count", count}}},
                    {"inputItemCategory", {{"category", 0}, {"categoryRef", "none"}, {"count", 0}}}};
    };
    json recipes = json::array();
    recipes.push_back({{"debugName", "Craft_Planks"}, {"recipeGuid", g(101)}, {"recipeId", 5001}, {"workshopId", 0x4da88f73},
                       {"workshopGuid", "00000000-0000-0000-0000-000000000000"},  // the game names a recipe's station by its id only
                       {"input", json::array({input_item(1, 3)})}, {"output", json::array({{{"item", 1002}, {"itemRef", g(2)}, {"count", 1}}})},
                       {"craftingDuration", 2.5}, {"knowledgeRequirement", {{"knowledgeOrQueryId", 0}, {"compareValue", 0}}}});
    recipes.push_back({{"debugName", "Craft_Stew"}, {"recipeGuid", g(102)}, {"recipeId", 5002}, {"workshopId", 0},
                       {"workshopGuid", "00000000-0000-0000-0000-000000000000"},  // the player's own crafting: no station
                       {"input", json::array({{{"itemStack", {{"item", 0}, {"itemRef", "none"}, {"count", 0}}},
                                               {"inputItemCategory", {{"category", 77}, {"categoryRef", "none"}, {"count", 4}}}}})},
                       {"output", json::array({{{"item", 1004}, {"itemRef", g(4)}, {"count", 2}}})},
                       {"craftingDuration", 10.0}, {"knowledgeRequirement", {{"knowledgeOrQueryId", 99}, {"compareValue", 1}}}});
    v.add("keen::RecipeRegistryResource", g(100), {{"recipes", recipes}, {"inputCategories", json::array({{{"inputCategoryId", 77}}})}});
    // dev.32: the stations (named by the item that builds them; none: station_<id>) and the skill tree's nodes (no names, no GUIDs)
    v.add("keen::WorkshopRegistryResource", g(500),
          {{"workshops", json::array({{{"isNpc", false}, {"workshopGuid", g(501)}, {"workshopId", 0x4da88f73}, {"item", 1002}, {"itemRef", g(2)}},
                                      {{"isNpc", false}, {"workshopGuid", g(502)}, {"workshopId", 0xb326e042}, {"item", 0}, {"itemRef", "none"}},
                                      {{"isNpc", true}, {"workshopGuid", g(503)}, {"workshopId", 0xc86873f9}, {"item", 1004}, {"itemRef", g(4)}}})}});
    v.add("keen::SkillTreeResource", g(600),
          {{"nodes", json::array({{{"id", 0x4475e8dd}, {"costs", 3}, {"maxLevel", 0}, {"type", "ActiveSkill"}, {"classAlignment", "Neutral"},
                                   {"activeSkillUnlock", 1002}, {"activeSkillUnlockRef", g(2)}},
                                  {{"id", 0x8efcc472}, {"costs", 0}, {"maxLevel", 3}, {"type", "Root"}, {"classAlignment", "Mage"},
                                   {"activeSkillUnlock", 0}, {"activeSkillUnlockRef", "none"}}})}});
    v.add("keen::LootableItemsResource", g(200),
          {{"items", json::array({{{"itemId", 1001}, {"stackSizeMin", 1}, {"stackSizeMax", 4}}, {{"itemId", 1002}, {"stackSizeMin", 1}, {"stackSizeMax", 1}}})}});
    v.add("keen::ecs::DefaultInventoryResource", g(300),
          {{"rootGroup", {{"stacks", json::array({{{"item", 1001}, {"itemRef", g(1)}, {"countMin", 1}, {"countMax", 2}, {"spawnProbability", 0.5}, {"weight", 1.0}}})},
                          {"groups", json::array({{{"stacks", json::array({{{"item", 1002}, {"itemRef", g(2)}, {"countMin", 1}, {"countMax", 1}, {"spawnProbability", 1.0}, {"weight", 3.0}}})}}})}}}});
    v.add("keen::ecs::DefaultInventoryResource", g(301), {{"rootGroup", {{"stacks", json::array()}}}});  // no template uses it
    v.add("keen::ecs::TemplateResource", g(400), {{"name", "LootContainer_Enemy_A"}, {"components", json::array({{{"$type", "DefaultInventory"}, {"defaultInventory", g(300)}}})}});
    v.add("keen::ecs::TemplateResource", g(401), {{"name", "Chest_B"}, {"components", json::array({{{"$type", "Health"}}})}});
    v.add("keen::BalancingTable", kBalance,
          {{"experienceNeedPerLevel", json::array({1000, 2000, 3000, 4000, 5000})}, {"playerLevelCap", 50}, {"genericWeaponSeed", 10.0}, {"powerCreepWeapon", 0.15},
           {"armorSetup", {{"offset", 23.0}, {"gradient", 15.0}}}, {"flameLevelMax", 2}, {"altarsPerFlameLevel", json::array({1, 2, 3})}});
    return v;
}

struct Fixture {
    TreeView view = world();
    map::RegMap rm;
    Schema schema = generated_schema();
    std::unique_ptr<model::Source> src;
    std::unique_ptr<model::Model> m;
    explicit Fixture(const std::string& regmap_dir) {
        std::vector<std::string> errs;
        map::load_dir(regmap_dir, "server", std::string(64, '0'), &rm, &errs);
        src = model::tree_source(view, [this](const std::string& type, std::vector<KilnAssetKey>* out, std::string*) {
            for (const std::string& guid : view.types[type]) out->push_back(key_of(guid, type));
            return true;
        });
        m = std::make_unique<model::Model>(*src, rm, schema);
    }
    Value val(const std::string& reg, const std::string& id, const std::string& field) {
        std::string why;
        const auto* es = m->entries(reg, &why);
        check(es != nullptr, "entries of " + reg + ": " + why);
        for (size_t i = 0; i < es->size(); ++i)
            if ((*es)[i].id == id) {
                auto v = m->value(reg, i, field, &why);
                check(v.has_value(), reg + "." + field + " of " + id + ": " + why);
                return *v;
            }
        throw std::runtime_error("no entry " + id + " in " + reg);
    }
};

std::string ids_of(model::Model& m, const std::string& reg) {
    std::string why, out;
    const auto* es = m.entries(reg, &why);
    if (!es) return "ERR " + why;
    for (const auto& e : *es) out += (out.empty() ? "" : ",") + e.id;
    return out;
}

void entries_and_ids(const std::string& dir) {
    Fixture f(dir);
    check(ids_of(*f.m, "items") == "game:material_t1_wood_logs,game:sword_t1,game:material_t1_wood_logs~00000003,game:item_000003ec",
          "items: names in lower case, the later of two equal names gets ~<guid8>, an unnamed item item_<id>: " + ids_of(*f.m, "items"));
    check(ids_of(*f.m, "recipes") == "game:craft_planks,game:craft_stew", "recipes from the one resource's array: " + ids_of(*f.m, "recipes"));
    check(ids_of(*f.m, "loot") == "game:material_t1_wood_logs,game:sword_t1", "loot keyed by the item's 32-bit id: " + ids_of(*f.m, "loot"));
    check(ids_of(*f.m, "drops") == "game:lootcontainer_enemy_a,game:drops_0000012d", "drop tables named after the template that uses them, else by GUID: " + ids_of(*f.m, "drops"));
    check(ids_of(*f.m, "balance") == "game:balance", "the one balance table: " + ids_of(*f.m, "balance"));
    check(ids_of(*f.m, "stations") == "game:sword_t1,game:station_b326e042,game:item_000003ec",
          "stations take the id of the item that builds them, else station_<id>: " + ids_of(*f.m, "stations"));
    check(ids_of(*f.m, "skills") == "game:skill_4475e8dd,game:skill_8efcc472", "skill nodes by their node id: " + ids_of(*f.m, "skills"));
    const auto* station = f.m->find("stations", "game:sword_t1");
    check(station && station->num == 0x4da88f73 && station->guid == g(501), "a station's number is its own 32-bit id (what recipes name), not its item's");
    const auto* e = f.m->find("items", "GAME:SWORD_T1");
    check(e && e->num == 1002 && e->guid == g(2) && e->name == "Sword_T1", "find: case-insensitive, with GUID, number and name");
}

void values(const std::string& dir) {
    Fixture f(dir);
    check(f.val("items", "game:material_t1_wood_logs", "maxStack") == Value::integer(10), "int");
    check(f.val("items", "game:sword_t1", "rarity") == Value::text("rare"), "enum: the game's name as Kiln's");
    check(f.val("items", "game:sword_t1", "power") == Value::integer(11), "formula gear.power with the balance constants (seed 10, creep 0.15 at level 1: 11)");
    check(f.val("items", "game:sword_t1", "perks").text_of() == "[\"game:perk_strong\"]", "refList: perk GUIDs as Kiln ids, empty slots left out: " + f.val("items", "game:sword_t1", "perks").text_of());
    const Value in = f.val("recipes", "game:craft_planks", "inputs");
    check(in.text_of() == "[{\"category\":null,\"count\":3,\"item\":\"game:material_t1_wood_logs\"}]", "list of structs, ref by GUID, stackCount of the item stack: " + in.text_of());
    const Value cat = f.val("recipes", "game:craft_stew", "inputs");
    check(cat.text_of() == "[{\"category\":\"game:category_0000004d\",\"count\":4,\"item\":null}]", "an input category: its Kiln id and its own count: " + cat.text_of());
    check(f.val("recipes", "game:craft_planks", "duration") == Value::number(2.5), "time in seconds");
    check(f.val("recipes", "game:craft_planks", "unlock") == Value::text("always") && f.val("recipes", "game:craft_stew", "unlock") == Value::text("game"), "unlock");
    check(f.val("loot", "game:sword_t1", "stack.max") == Value::integer(1), "loot value");
    const Value walk = f.val("drops", "game:lootcontainer_enemy_a", "entries");
    check(walk.list.size() == 2 && walk.list[0].leaf("item")->t == "game:material_t1_wood_logs" && walk.list[1].leaf("weight")->d == 3.0 &&
              walk.list[0].leaf("chance")->d == 0.5,
          "inventoryWalk: the root's stacks, then each child group's: " + walk.text_of());
    check(f.val("balance", "game:balance", "xpPerLevel").text_of() == "[1000,2000,3000,4000,5000]", "a list of ints");
    check(f.val("balance", "game:balance", "levelCap") == Value::integer(50), "balance int");
    // dev.32: a recipe's station by the station's 32-bit id (the recipe's station GUID is empty); none: null
    check(f.val("recipes", "game:craft_planks", "station") == Value::text("game:sword_t1"), "recipes.station names the stations id");
    check(f.val("recipes", "game:craft_stew", "station").kind == Value::None, "a recipe without a station: none");
    check(f.val("stations", "game:sword_t1", "item") == Value::text("game:sword_t1") && f.val("stations", "game:station_b326e042", "item").kind == Value::None,
          "stations.item: the items id, none without one");
    check(f.val("stations", "game:item_000003ec", "npc") == Value::boolean(true) && f.val("stations", "game:sword_t1", "npc") == Value::boolean(false), "stations.npc");
    check(f.val("skills", "game:skill_4475e8dd", "cost") == Value::integer(3) && f.val("skills", "game:skill_8efcc472", "maxLevel") == Value::integer(3), "skills ints");
    check(f.val("skills", "game:skill_4475e8dd", "kind") == Value::text("active") && f.val("skills", "game:skill_8efcc472", "class") == Value::text("mage"),
          "skills enums: the game's names as Kiln's");
    check(f.val("skills", "game:skill_4475e8dd", "unlocks") == Value::text("game:sword_t1") && f.val("skills", "game:skill_8efcc472", "unlocks").kind == Value::None,
          "skills.unlocks: the ability item's id, none without one");
    std::string why;
    const auto* items = f.m->entries("items", &why);
    check(!f.m->value("items", 0, "noSuchField", &why) && why.find("not in the schema") != std::string::npos, "an unknown field: " + why);
    (void)items;
}

void needs_and_compose(const std::string& dir) {
    Fixture f(dir);
    // Keep a viable stack in the affected group: removing its only stack is now refused, not guessed safe.
    auto& root = f.view.values[g(300)]["rootGroup"];
    root["stacks"].push_back(root["groups"][0]["stacks"][0]);
    std::vector<ModInput> mods(1);
    mods[0].id = "testmod";
    Batch b;
    std::vector<Refusal> refused;
    std::string err;
    const json doc = {{"ir", 1}, {"mod", "testmod"}, {"stage", "alter"}, {"source", "content/a.json"},
                      {"edits", json::array({{{"registry", "items"}, {"entry", "remove"}, {"select", {{"id", "game:material_t1_wood_logs"}}}},
                                             {{"registry", "items"}, {"select", {{"id", "game:sword_t1"}}}, {"field", "maxStack"}, {"op", {{"mul", 4}}}}})}};
    check(parse_batch(doc, f.schema, &b, &refused, &err) && refused.empty(), "the batch parses: " + err);
    mods[0].batches.push_back(b);
    const model::Need need = model::needs_of(f.schema, mods);
    check(need.fields.at("items").count("maxStack") && need.fields.at("recipes").count("inputs") && need.fields.at("recipes").count("outputs") &&
              need.fields.at("drops").count("entries"),
          "needs: the edited field, and every reference field once an entry is removed");
    check(!need.fields.at("items").count("rarity"), "needs: nothing else of items");
    BaseModel base;
    f.m->fill(need, &base);
    check(base.registries.at("recipes").size() == 2 && base.registries.at("recipes")[0].fields.count("inputs"), "the base holds what was needed");
    check(!base.registries.at("items")[0].fields.count("rarity"), "and not more");
    const auto decoded = f.m->decoded();
    check(decoded.count("items.maxStack") && !decoded.count("items.rarity"), "decoded lazily");
    f.m->release();
    check(f.view.released, "release() lets the view go");
    std::string why;
    check(!f.m->value("items", 0, "rarity", &why), "after release nothing more is read: " + why);
    check(f.m->value("items", 1, "maxStack", &why).has_value(), "what was decoded stays");
    const Result r = compose(f.schema, base, {}, mods);
    bool recipe_gone = false, line = false;
    for (const EntryRef& x : r.removed) recipe_gone = recipe_gone || (x.registry == "recipes" && x.id == "game:craft_planks");
    for (const Line& l : r.lines) line = line || l.text.find("recipes game:craft_planks removed: inputs[0].item game:material_t1_wood_logs") != std::string::npos;
    check(recipe_gone && line, "a game recipe whose input the mod removed is removed with it, reported");
    const auto fo = r.fields.find(FieldRef{"items", "game:sword_t1", "maxStack"});
    check(fo != r.fields.end() && fo->second.result == Value::integer(4), "the edit composed over the decoded base (1 x 4)");
    bool drop_gone = false;
    for (const EntryRef& x : r.removed) drop_gone = drop_gone || (x.registry == "drops" && x.id == "game:lootcontainer_enemy_a");
    const auto drops = r.fields.find(FieldRef{"drops", "game:lootcontainer_enemy_a", "entries"});
    check(base.registries.at("drops")[0].drop_groups == std::vector<std::pair<size_t, size_t>>{{0, 2}, {2, 1}},
          "the copied model retains group spans independently of flat author-facing values");
    check(!drop_gone && drops != r.fields.end() && drops->second.result.list.size() == 3 &&
          drops->second.result.list[0].leaf("chance")->as_double() == 0 && drops->second.result.list[0].leaf("weight")->as_double() == 0 &&
          drops->second.result.list[1].leaf("item")->t == "game:sword_t1",
          "the drop table and its indices stay; only the removed item's stack is disabled");
}

void encode_writes(const std::string& dir) {
    Fixture f(dir);
    const auto& reg = f.rm.registries.at("balance");
    const FieldSpec* xp = f.schema.registry("balance")->field("xpPerLevel");
    const Value below = f.val("balance", "game:balance", "xpPerLevel");
    Value now = below;
    now.list[2] = Value::integer(3500);
    std::vector<std::pair<std::string, json>> w;
    std::string why;
    check(model::encode(reg, reg.rows.at("xpPerLevel"), *xp, now, below, &w, &why) && w.size() == 1 && w[0].first == "experienceNeedPerLevel[2]" && w[0].second == 3500,
          "an element of an indexed list: one write: " + why);
    now.list.push_back(Value::integer(1));
    w.clear();
    check(!model::encode(reg, reg.rows.at("xpPerLevel"), *xp, now, below, &w, &why) && why.find("cannot change") != std::string::npos, "a length change is refused: " + why);
    w.clear();
    check(model::encode(reg, reg.rows.at("levelCap"), *f.schema.registry("balance")->field("levelCap"), Value::integer(60), Value::integer(50), &w, &why) &&
              w.size() == 1 && w[0].first == "playerLevelCap" && w[0].second == 60,
          "an int field");
    w.clear();
    check(model::encode(reg, reg.rows.at("levelCap"), *f.schema.registry("balance")->field("levelCap"), Value::integer(50), Value::integer(50), &w, &why) && w.empty(),
          "nothing written where nothing changed");
}

void id_table(const std::string& dir) {
    Fixture f(dir);
    std::string why;
    f.m->entries("loot", &why);
    std::map<std::string, std::string> ids;
    std::map<std::string, uint32_t> nums;
    f.m->id_table(&ids, &nums);
    check(ids.at("items|game:sword_t1") == g(2) && nums.at("items|game:sword_t1") == 1002, "items: GUID and item id");
    check(ids.at("loot|game:sword_t1").empty() && nums.at("loot|game:sword_t1") == 1002, "loot: keyed by the item id");
    const json t = f.m->id_table_json();
    check(t["registries"]["items"][g(1)] == "game:material_t1_wood_logs" && t["decoder"] == model::kDecoderVersion, "the file's shape: " + t.dump().substr(0, 200));
    check(model::added_guid("items", "mymod:lantern") == "a0255f78-9e7f-5439-be52-9f7523f1f236", "a mod entry's GUID: UUIDv5 (Kiln.Blocks' namespace, registry:<registry>:<id>)");
}

// ---- the live bytes, through the game's reflection ----
struct BytesView final : kiln::boot_view::BaseView {
    std::map<std::string, std::string> res;  // guid -> bytes
    bool released = false;
    const char* source() const override { return "live"; }
    bool ready(std::string*) override { return true; }
    std::string digest() override { return released ? "" : "live-digest"; }
    KilnResult tree(std::string_view, size_t, size_t, uint64_t, std::string*, size_t*, size_t*, std::string* why) override { return *why = "bytes only", KILN_ERR_UNAVAILABLE; }
    KilnResult bytes(const KilnAssetKey& k, size_t, std::string* out, std::string* why) override {
        if (released) return *why = "released", KILN_ERR_UNAVAILABLE;
        const auto it = res.find(kc::guid_text(k.guid));
        if (it == res.end()) return *why = "none", KILN_ERR_NOT_FOUND;
        *out = it->second;
        return KILN_OK;
    }
    void release() override { released = true; }
};

void live_bytes(const std::string& dir) {
    kc::rbtest::item_type();
    BytesView view;
    std::vector<KilnAssetKey> keys;
    auto make = [&](int n, uint16_t stack, const std::string& name) {
        std::string b(40 + name.size(), '\0');
        const uint32_t id = 1000 + n;
        std::memcpy(&b[0], &id, 4);
        kc::guid_parse(g(n), reinterpret_cast<uint8_t*>(&b[4]));
        std::memcpy(&b[0x14], &stack, 2);
        const int32_t rel = 40 - 0x18;
        const uint32_t len = static_cast<uint32_t>(name.size());
        std::memcpy(&b[0x18], &rel, 4);
        std::memcpy(&b[0x1c], &len, 4);
        std::memcpy(&b[40], name.data(), name.size());
        view.res[g(n)] = b;
        keys.push_back(key_of(g(n), "keen::ItemInfo"));
    };
    make(1, 99, "Material_T1_Wood_Logs");
    make(2, 1, "Sword_T1");
    map::RegMap rm;
    std::vector<std::string> errs;
    map::load_dir(dir, "server", std::string(64, '0'), &rm, &errs);
    const Schema schema = generated_schema();
    auto src = model::live_source(view, [&](uint32_t type, std::vector<KilnAssetKey>* out) {
        if (type != kiln::boot_gd::fnv1a32("keen::ItemInfo")) return true;
        *out = keys;
        return true;
    });
    model::Model m(*src, rm, schema);
    check(ids_of(m, "items") == "game:material_t1_wood_logs,game:sword_t1", "live: ids from the bytes' debug names: " + ids_of(m, "items"));
    std::string why;
    auto v = m.value("items", 0, "maxStack", &why);
    check(v && *v == Value::integer(99), "live: a u16 member through the reflection: " + why);
    check(m.find("items", "game:sword_t1")->guid == g(2), "live: the GUID member");
    check(m.digest() == "live-digest", "the base view's digest");
    m.release();
    check(view.released && m.digest() == "live-digest", "released: the view let go; the digest is kept");
}
void byte_array_bounds(const std::string&) {
    std::vector<uint8_t> bytes(65560, 7);
    const int32_t rel = 16;
    uint32_t count = 21;
    std::memcpy(bytes.data(), &rel, 4);
    std::memcpy(bytes.data() + 4, &count, 4);
    kc::rbtest::byte_blob_type(0);
    json out;
    std::string why;
    check(model::bytes_member(bytes.data(), bytes.size(), "test::ByteBlob", "values", &out, &why) && out.size() == 21, "a bounded byte blob reads");
    check(model::bytes_member(bytes.data(), 24, "test::ByteBlob", "values", &out, &why) && out.is_null(), "a byte blob outside the resource is not read");
    count = 65537;
    std::memcpy(bytes.data() + 4, &count, 4);
    check(model::bytes_member(bytes.data(), bytes.size(), "test::ByteBlob", "values", &out, &why) && out.is_null(), "a long byte array is refused, not truncated");
    count = 21;
    std::memcpy(bytes.data() + 4, &count, 4);
    kc::rbtest::byte_blob_type(reinterpret_cast<uintptr_t>(bytes.data()) + 16);
    check(model::bytes_member(bytes.data(), bytes.size(), "test::ByteBlob", "values", &out, &why) && out.is_null(), "a failed guarded byte read is null");
    kc::rbtest::byte_blob_type(0);
    check(model::bytes_member(bytes.data(), bytes.size(), "test::ByteBlob", "", &out, &why) && out["values"].size() == 21, "a nested byte array also reads as numbers");
}
void single_scalar_structs(const std::string&) {
    const uint32_t raw[3] = {4242, 7, 9};
    kc::rbtest::wrapped_id_type();
    json out;
    std::string why;
    check(model::bytes_member(raw, sizeof raw, "test::Wrapped", "id", &out, &why) && out.is_number() && out.get<uint32_t>() == 4242,
          "a one-scalar struct id reads as its number, not {\"value\": n} (got " + out.dump() + ")");
    check(model::bytes_member(raw, sizeof raw, "test::Wrapped", "pair", &out, &why) && out.is_object() && out["a"] == 7 && out["b"] == 9,
          "a struct of two members still reads as an object");
    check(model::bytes_member(raw, sizeof raw, "test::Wrapped", "", &out, &why) && out["id"] == 4242,
          "nested in the root too");
}
}  // namespace

// dev.32: a selector's traits over skills (active: the node unlocks an ability) and a where on recipes.station (a stations id)
// pick the entries a data pack means; composed over the decoded base.
void skills_and_stations_selectors(const std::string& dir) {
    Fixture f(dir);
    std::vector<ModInput> mods(1);
    mods[0].id = "testmod";
    Batch b;
    std::vector<Refusal> refused;
    std::string err;
    const json doc = {{"ir", 1}, {"mod", "testmod"}, {"stage", "alter"}, {"source", "content/a.json"},
                      {"edits", json::array({{{"registry", "skills"}, {"select", {{"id", "*"}, {"has", json::array({"active"})}}}, {"field", "cost"}, {"op", 0}},
                                             {{"registry", "recipes"}, {"select", {{"id", "*"}, {"where", {{"station", "game:sword_t1"}}}}},
                                              {"field", "duration"}, {"op", {{"mul", 0.5}}}}})}};
    check(parse_batch(doc, f.schema, &b, &refused, &err) && refused.empty(), "the batch parses: " + err);
    mods[0].batches.push_back(b);
    const model::Need need = model::needs_of(f.schema, mods);
    check(need.traits.count("skills") && need.fields.at("recipes").count("station"), "needs: the skills traits, and the station the where reads");
    BaseModel base;
    f.m->fill(need, &base);
    const Result r = compose(f.schema, base, {}, mods);
    const auto active = r.fields.find(FieldRef{"skills", "game:skill_4475e8dd", "cost"});
    check(active != r.fields.end() && active->second.result == Value::integer(0), "the active node (it unlocks an ability) costs 0");
    check(!r.fields.count(FieldRef{"skills", "game:skill_8efcc472", "cost"}), "the root is not active: untouched");
    const auto planks = r.fields.find(FieldRef{"recipes", "game:craft_planks", "duration"});
    check(planks != r.fields.end() && planks->second.result == Value::number(1.25), "the station's recipe: 2.5 s x 0.5");
    check(!r.fields.count(FieldRef{"recipes", "game:craft_stew", "duration"}), "a recipe of no station: untouched");
}

int main(int argc, char** argv) {
    const std::string dir = argc > 1 ? argv[1] : "regmap";
    int failed = 0;
    const std::pair<const char*, void (*)(const std::string&)> groups[] = {
        {"byte_array_bounds", byte_array_bounds}, {"single_scalar_structs", single_scalar_structs}, {"entries_and_ids", entries_and_ids}, {"values", values}, {"needs_and_compose", needs_and_compose},
        {"encode_writes", encode_writes},     {"id_table", id_table}, {"live_bytes", live_bytes}, {"skills_and_stations_selectors", skills_and_stations_selectors}};
    for (const auto& [name, fn] : groups) {
        try {
            fn(dir);
        } catch (const std::exception& e) {
            ++failed;
            std::printf("registry-model-test: FAIL %s: %s (after %d checks)\n", name, e.what(), checks);
        }
    }
    if (failed) {
        std::printf("registry-model-test: %d group(s) failed, %d checks passed\n", failed, checks);
        return 1;
    }
    std::printf("registry-model-test: %d checks passed\n", checks);
}
