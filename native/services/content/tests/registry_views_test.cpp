// SPDX-License-Identifier: MIT
// kiln.content registry views (registry/views.h, dev.32 J1), on the host: the Kiln names of views and fields derived from
// hand-made reflection, the alias table (a renamed member or type keeps its Kiln name, a removed member is refused, never
// reused), the registry rows that cover a member, the catalog (every registry of the schema as a stable view, a fixture
// registry included, every resource type as an advanced one, clashes), values in Kiln form, ids and selectors, and that the
// JSON the native table gives names no game type or member.
#include <cstdio>
#include <string>

#include "../registry/schema_ir.h"
#include "../registry/views.h"

using namespace kc::registry;
using views::FieldKind;
using views::TypeDesc;
using views::TypeKind;

static int g_fail = 0, g_pass = 0;
#define CHECK(c)                                                                  \
    do {                                                                          \
        if (c) ++g_pass;                                                          \
        else { ++g_fail; std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #c); } \
    } while (0)

namespace {
using P = std::shared_ptr<const TypeDesc>;
P leaf(TypeKind k, const std::string& name = "") {
    auto t = std::make_shared<TypeDesc>();
    t->kind = k, t->name = name;
    return t;
}
P en(std::vector<std::string> values, TypeKind k = TypeKind::Enum) {
    auto t = std::make_shared<TypeDesc>();
    t->kind = k, t->name = "keen::QuestKind", t->values = std::move(values);
    return t;
}
P ref(const std::string& target) {
    auto t = std::make_shared<TypeDesc>();
    t->kind = TypeKind::Ref, t->name = "keen::ObjectReference<" + target + ">", t->target = target;
    return t;
}
P list(P element, uint32_t fixed = 0) {
    auto t = std::make_shared<TypeDesc>();
    t->kind = TypeKind::List, t->name = "keen::BlobArray<x>", t->element = std::move(element), t->fixed = fixed;
    return t;
}
P st(const std::string& name, std::vector<std::pair<std::string, P>> fields) {
    auto t = std::make_shared<TypeDesc>();
    t->kind = TypeKind::Struct, t->name = name, t->fields = std::move(fields);
    return t;
}
TypeDesc root(const std::string& name, std::vector<std::pair<std::string, P>> fields, uint32_t hash = 0x1234abcd) {
    TypeDesc t = *st(name, std::move(fields));
    t.hash = hash;
    return t;
}

// A quest-like resource type with every kind a member can have.
TypeDesc quest() {
    auto reward = st("keen::QuestReward", {{"item", ref("keen::ItemInfo")}, {"count", leaf(TypeKind::Int)}});
    return root("keen::QuestResource",
                {{"debugName", leaf(TypeKind::Text)},
                 {"objectId", leaf(TypeKind::Guid)},
                 {"xpReward", leaf(TypeKind::Int)},
                 {"Duration", leaf(TypeKind::Time)},
                 {"m_weight", leaf(TypeKind::Float)},
                 {"repeatable", leaf(TypeKind::Bool)},
                 {"kind", en({"Main", "Side_Quest", "Daily"})},
                 {"tags", en({"Combat", "Gather"}, TypeKind::Flags)},
                 {"rewardItems", list(reward)},
                 {"stageXp", list(leaf(TypeKind::Int), 4)},
                 {"setup", st("keen::QuestSetup", {{"radius", leaf(TypeKind::Float)}, {"giver", ref("keen::NpcDialogResource")}})},
                 {"events", list(leaf(TypeKind::Variant))},
                 {"impactValues", leaf(TypeKind::Configs)},
                 {"maybe", leaf(TypeKind::Optional)}});
}

const views::ViewField* field(const views::View& v, const std::string& p) { return v.field(p); }
// Null-safe reads, so a red run counts failures instead of stopping at the first missing field.
const views::ViewField kNone{};
const views::ViewField& at(const views::View* v, const std::string& p) {
    const views::ViewField* f = v ? v->field(p) : nullptr;
    return f ? *f : kNone;
}
const views::View kNoView{};
const views::View& view(const views::Catalog& c, const std::string& name) {
    const views::View* v = c.find(name);
    return v ? *v : kNoView;
}
std::string type_of(const views::Catalog& c, const std::string& type) {
    const auto it = c.by_type.find(type);
    return it == c.by_type.end() ? "" : it->second;
}

bool no_game_words(const std::string& text) {
    return text.find("keen::") == std::string::npos && text.find("QuestResource") == std::string::npos &&
           text.find("m_weight") == std::string::npos && text.find("Side_Quest") == std::string::npos;
}

map::RegMap items_regmap() {
    map::RegMap rm;
    std::vector<std::string> errs;
    map::load_common(map::json::parse(R"({"regmap": 1, "registry": "items",
        "entries": {"kind": "resource", "resourceType": "keen::ItemInfo", "name": "debugName", "guid": "objectId", "id": "itemId"},
        "rows": {
          "maxStack": {"paths": ["maxStackSize"], "conv": "int", "read": "worldLoad", "backend": "live"},
          "power": {"paths": ["damageSetup.dmgMod", "levels.min"], "conv": "formula", "formula": "gear.power", "read": "now", "backend": "live"}}})"),
                     &rm.registries["items"], &errs);
    map::load_common(map::json::parse(R"({"regmap": 1, "registry": "recipes",
        "entries": {"kind": "array", "resourceType": "keen::RecipeRegistryResource", "array": "recipes", "name": "debugName", "guid": "guid", "id": "recipeId"},
        "rows": {"duration": {"paths": ["craftingDuration"], "conv": "time", "read": "now", "backend": "live"}}})"),
                     &rm.registries["recipes"], &errs);
    return rm;
}
}  // namespace

int main() {
    // ---- names
    CHECK(views::kiln_name("maxStackSize") == "maxStackSize");
    CHECK(views::kiln_name("ManaCost") == "manaCost");
    CHECK(views::kiln_name("m_weight") == "weight");
    CHECK(views::kiln_name("Side_Quest") == "sideQuest");
    CHECK(views::kiln_name("2dSize") == "n2dSize");
    CHECK(views::kiln_name("__") == "");
    CHECK(views::view_name("keen::NpcDialogResource") == "npcDialog");
    CHECK(views::view_name("keen::ecs::TemplateResource") == "template");
    CHECK(views::view_name("keen::ecs::TemplateResource", true) == "ecsTemplate");
    CHECK(views::view_name("keen::ItemInfo") == "itemInfo");
    CHECK(views::view_name("keen::Resource") == "resource");

    // ---- one advanced view
    const views::Aliases none;
    {
        const views::View v = views::derive(quest(), "quest", none, nullptr);
        CHECK(!v.stable && v.name == "quest" && v.name_member == "debugName");
        const auto* xp = field(v, "xpReward");
        CHECK(xp && xp->kind == FieldKind::Int && xp->read_only && xp->game == "xpReward");
        const auto* dur = field(v, "duration");
        CHECK(dur && dur->kind == FieldKind::Time && dur->game == "Duration");
        const auto* w = field(v, "weight");
        CHECK(w && w->kind == FieldKind::Float && w->game == "m_weight");
        const auto* kind = field(v, "kind");
        CHECK(kind && kind->kind == FieldKind::Enum && kind->values == (std::vector<std::string>{"main", "sideQuest", "daily"}));
        const auto* tags = field(v, "tags");
        CHECK(tags && tags->kind == FieldKind::Flags && tags->values.size() == 2 && tags->values[1] == "gather");
        const auto* rewards = field(v, "rewardItems");
        CHECK(rewards && rewards->kind == FieldKind::List && rewards->element == FieldKind::Struct);
        const auto* count = field(v, "rewardItems[].count");
        CHECK(count && count->kind == FieldKind::Int && count->game == "rewardItems[].count");
        CHECK(field(v, "rewardItems[3].count") == count);  // an index reads as the list's row
        const auto* item = field(v, "rewardItems[].item");
        CHECK(item && item->kind == FieldKind::Id && item->ref_type == "keen::ItemInfo" && item->ref.empty());  // the catalog names its view
        const auto* stage = field(v, "stageXp");
        CHECK(stage && stage->kind == FieldKind::List && stage->element == FieldKind::Int);
        CHECK(field(v, "setup.radius") && field(v, "setup.radius")->kind == FieldKind::Float);
        CHECK(!field(v, "setup"));  // a struct is not a field: its leaves are
        const auto* ev = field(v, "events");
        CHECK(ev && ev->kind == FieldKind::List && ev->element == FieldKind::Unread && !ev->reason.empty());
        const auto* cfg = field(v, "impactValues");
        CHECK(cfg && cfg->kind == FieldKind::Unread && cfg->reason.find("configuration") != std::string::npos);
        const auto* opt = field(v, "maybe");
        CHECK(opt && opt->kind == FieldKind::Unread && !opt->reason.empty());
        CHECK(field(v, "debugName") && field(v, "debugName")->kind == FieldKind::Text);
        CHECK(!field(v, "nope"));
        // game paths with indices
        const views::ViewField* f = nullptr;
        std::string why;
        auto gp = views::game_path(v, "rewardItems[2].count", &f, &why);
        CHECK(gp && *gp == "rewardItems[2].count" && f == count);
        gp = views::game_path(v, "weight", &f, &why);
        CHECK(gp && *gp == "m_weight");
        CHECK(!views::game_path(v, "rewardItems[x].count", &f, &why) && !why.empty());
        CHECK(!views::game_path(v, "nope", &f, &why) && why.find("quest has no field nope") != std::string::npos);
        // fields JSON: Kiln names only
        const std::string text = views::fields_json(v).dump();
        CHECK(no_game_words(text));
        CHECK(text.find("\"weight\"") != std::string::npos && text.find("\"sideQuest\"") != std::string::npos);
    }

    // ---- a member deeper than the limit, a struct reaching itself, the field cap
    {
        P deep = leaf(TypeKind::Int);
        for (int i = 0; i < views::kMaxDepth + 2; ++i) deep = st("keen::Deep", {{"inner", deep}});
        const views::View v = views::derive(root("keen::DeepResource", {{"d", deep}}), "deep", none, nullptr);
        bool unread_deep = false;
        for (const auto& f : v.fields) unread_deep |= f.kind == FieldKind::Unread && f.reason.find("deep") != std::string::npos;
        CHECK(unread_deep);
        std::vector<std::pair<std::string, P>> many;
        for (size_t i = 0; i < views::kMaxFields + 10; ++i) many.emplace_back("f" + std::to_string(i), leaf(TypeKind::Int));
        const views::View big = views::derive(root("keen::BigResource", many), "big", none, nullptr);
        CHECK(big.fields.size() == views::kMaxFields && big.truncated);
    }

    // ---- two members with one Kiln name: the first keeps it, the later one gets a number
    {
        const views::View v = views::derive(root("keen::TwinResource", {{"Speed", leaf(TypeKind::Float)}, {"speed", leaf(TypeKind::Int)}}), "twin", none, nullptr);
        CHECK(field(v, "speed") && field(v, "speed")->game == "Speed");
        CHECK(field(v, "speed2") && field(v, "speed2")->game == "speed");
    }

    // ---- the alias table
    {
        views::Aliases a;
        std::vector<std::string> errs;
        CHECK(views::load_aliases(views::json::parse(R"({"views": 1,
            "types": {"quest": "keen::QuestDefinitionResource"},
            "fields": {"quest": {"xpReward": "experienceReward", "rewardItems[].count": "rewardItems[].amount"}},
            "removed": {"quest": {"repeatable": "1300000"}},
            "ids": {"pinned": {"quest|00000000-0000-0000-0000-000000000001": "game:old_quest"}, "removed": {"quest|game:gone": "1300000"}}})"),
                                  &a, &errs));
        CHECK(errs.empty());
        // the game renamed xpReward to experienceReward and count to amount; repeatable is gone
        auto reward = st("keen::QuestReward", {{"amount", leaf(TypeKind::Int)}});
        const TypeDesc t = root("keen::QuestDefinitionResource",
                                {{"debugName", leaf(TypeKind::Text)}, {"experienceReward", leaf(TypeKind::Int)}, {"rewardItems", list(reward)}});
        const views::View v = views::derive(t, "quest", a, nullptr);
        CHECK(field(v, "xpReward") && field(v, "xpReward")->game == "experienceReward");
        CHECK(!field(v, "experienceReward"));  // one member, one Kiln name: the pinned one
        CHECK(field(v, "rewardItems[].count") && field(v, "rewardItems[].count")->game == "rewardItems[].amount");
        CHECK(!field(v, "rewardItems[].amount"));
        const auto* gone = field(v, "repeatable");
        CHECK(gone && gone->removed && gone->reason.find("1300000") != std::string::npos);
        // a pinned field whose member this build does not have is refused, not dropped
        views::Aliases b = a;
        b.fields["quest"]["bonus"] = "bonusMember";
        const views::View w = views::derive(t, "quest", b, nullptr);
        const auto* bonus = field(w, "bonus");
        CHECK(bonus && bonus->kind == FieldKind::Unread && !bonus->reason.empty());
        // a renamed type keeps its view's name
        const Schema schema = generated_schema();
        const views::Catalog c = views::catalog(schema, {t}, a, nullptr);
        CHECK(view(c, "quest").type == "keen::QuestDefinitionResource");
        CHECK(!c.find("questDefinition"));
        // ids: a pin keeps the old id, a removed id is never given again
        const auto ids = views::ids(view(c, "quest"), a,
                                    {{"00000000-0000-0000-0000-000000000001", "Quest_Renamed", 0, {}},
                                     {"00000000-0000-0000-0000-000000000002", "Gone", 0, {}},
                                     {"00000000-0000-0000-0000-000000000003", "", 0, {}}},
                                    nullptr);
        CHECK(ids.size() == 3 && ids[0] == "game:old_quest");
        CHECK(ids[1] != "game:gone");
        CHECK(ids[2] == "game:quest_00000000");
        CHECK(!views::load_aliases(views::json::parse(R"({"views": 2})"), &b, &errs));
        errs.clear();
        CHECK(!views::load_aliases(views::json::parse(R"({"views": 1, "fields": {"quest": {"Bad Name": "x"}}})"), &b, &errs) || !errs.empty());
    }

    // ---- registry rows that cover a member: one to one, or through a formula; array entries
    {
        const map::RegMap rm = items_regmap();
        const TypeDesc item = root("keen::ItemInfo", {{"debugName", leaf(TypeKind::Text)}, {"maxStackSize", leaf(TypeKind::Int)},
                                                      {"damageSetup", st("keen::DamageSetup", {{"dmgMod", leaf(TypeKind::Float)}})},
                                                      {"weight", leaf(TypeKind::Float)}});
        const views::View v = views::derive(item, "itemInfo", none, &rm);
        CHECK(at(&v, "maxStackSize").covered_by == "items.maxStack" && at(&v, "maxStackSize").exact);
        CHECK(at(&v, "damageSetup.dmgMod").covered_by == "items.power" && !at(&v, "damageSetup.dmgMod").exact);
        CHECK(at(&v, "weight").path == "weight" && at(&v, "weight").covered_by.empty());
        const TypeDesc recipes = root("keen::RecipeRegistryResource",
                                      {{"recipes", list(st("keen::Recipe", {{"craftingDuration", leaf(TypeKind::Time)}, {"debugName", leaf(TypeKind::Text)}}))}});
        const views::View r = views::derive(recipes, "recipeRegistry", none, &rm);
        CHECK(at(&r, "recipes[].craftingDuration").covered_by == "recipes.duration" && at(&r, "recipes[].craftingDuration").exact);
    }

    // ---- the catalog: every registry of the schema, a fixture registry with no code of its own, clashes
    {
        Schema schema = generated_schema();
        CHECK(schema.registries.count("items") && schema.registries.count("balance"));
        RegistrySpec creatures;  // a registry of a later lane, as its schema would give it
        creatures.name = "creatures";
        FieldSpec health;
        health.name = "health.perPlayer", health.type = FieldType::Float, health.applies = Applies::Now;
        creatures.fields[health.name] = health;
        schema.registries["creatures"] = creatures;
        const TypeDesc item = root("keen::ItemInfo", {{"maxStackSize", leaf(TypeKind::Int)}}, 1);
        const TypeDesc drops = root("keen::Drops", {{"x", leaf(TypeKind::Int)}}, 2);           // "drops" is a registry's name
        const TypeDesc q1 = root("keen::QuestResource", {{"x", leaf(TypeKind::Int)}}, 3);
        const TypeDesc q2 = root("keen::story::QuestResource", {{"y", leaf(TypeKind::Int)}}, 4);  // the same short name
        const TypeDesc q3 = root("keen::story::Quest", {{"z", leaf(TypeKind::Int)}}, 0xabcdef01);  // and its namespaced form too
        const views::Catalog c = views::catalog(schema, {q3, item, drops, q2, q1}, none, nullptr);
        for (const char* r : {"items", "recipes", "loot", "drops", "balance", "creatures"}) {
            CHECK(view(c, r).stable);
        }
        CHECK(at(c.find("creatures"), "health.perPlayer").kind == FieldKind::Float);
        CHECK(at(c.find("items"), "maxStack").path == "maxStack" && !at(c.find("items"), "maxStack").read_only);
        CHECK(c.find("itemInfo") && !view(c, "itemInfo").stable);
        CHECK(view(c, "drops").stable && c.find("keen") == nullptr);
        // keen::Drops cannot be "drops" (the registry's) and has no namespace of its own, so it gets a hash suffix; types in name
        // order: keen::QuestResource keeps "quest", keen::story::Quest takes its namespaced form "storyQuest", and
        // keen::story::QuestResource finds both taken
        CHECK(type_of(c, "keen::Drops") == "drops_00000002" && c.find("drops_00000002") && !view(c, "drops_00000002").stable);
        CHECK(type_of(c, "keen::QuestResource") == "quest");
        CHECK(type_of(c, "keen::story::Quest") == "storyQuest");
        CHECK(type_of(c, "keen::story::QuestResource") == "quest_00000004");
        CHECK(type_of(c, "keen::ItemInfo") == "itemInfo");
        // an id field's view, by its target type
        const TypeDesc giver = root("keen::GiftResource", {{"item", ref("keen::ItemInfo")}, {"other", ref("keen::NotAResource")}}, 5);
        const views::Catalog d = views::catalog(schema, {giver, item}, none, nullptr);
        CHECK(at(d.find("gift"), "item").ref == "itemInfo");
        CHECK(at(d.find("gift"), "other").path == "other" && at(d.find("gift"), "other").ref.empty());
        // the JSON of the catalog
        const std::string text = views::views_json(c, {{"quest", 3}}).dump();
        CHECK(no_game_words(text));
        CHECK(text.find("\"creatures\"") != std::string::npos && text.find("\"stable\"") != std::string::npos &&
              text.find("\"advanced\"") != std::string::npos);
    }

    // ---- values in Kiln form
    {
        const views::Catalog c = views::catalog(generated_schema(), {quest(), root("keen::ItemInfo", {{"maxStackSize", leaf(TypeKind::Int)}}, 9)}, none, nullptr);
        const views::View& v = view(c, "quest");
        CHECK(at(&v, "rewardItems[].item").ref == "itemInfo");
        std::string why;
        const views::RefFn refs = [](const std::string& view, const std::string& guid) -> std::optional<std::string> {
            if (view == "itemInfo" && guid == "00000000-0000-0000-0000-00000000000a") return std::string("game:wood");
            return std::nullopt;
        };
        auto val = views::kiln_value(v, at(&v, "kind"), views::json("Side_Quest"), refs, &why);
        CHECK(val && *val == "sideQuest");
        CHECK(!views::kiln_value(v, at(&v, "kind"), views::json("Nope"), refs, &why) && why.find("quest.kind") != std::string::npos);
        CHECK(no_game_words(why) && why.find("Nope") == std::string::npos);
        val = views::kiln_value(v, at(&v, "tags"), views::json::parse(R"(["Gather"])"), refs, &why);
        CHECK(val && *val == views::json::parse(R"(["gather"])"));
        val = views::kiln_value(v, at(&v, "tags"), views::json::parse(R"(["Gather", 64])"), refs, &why);
        CHECK(!val);  // a bit Kiln has no name for
        val = views::kiln_value(v, at(&v, "rewardItems"),
                                views::json::parse(R"([{"item": "00000000-0000-0000-0000-00000000000a", "count": 2}, {"item": "none", "count": 1}])"), refs, &why);
        CHECK(val && *val == views::json::parse(R"([{"item": "game:wood", "count": 2}, {"item": null, "count": 1}])"));
        val = views::kiln_value(v, at(&v, "rewardItems[].item"), views::json("00000000-0000-0000-0000-0000000000ff"), refs, &why);
        CHECK(val && *val == "00000000-0000-0000-0000-0000000000ff");  // no view entry for it: the GUID text stays
        val = views::kiln_value(v, at(&v, "duration"), views::json(2.5), refs, &why);
        CHECK(val && *val == 2.5);
        val = views::kiln_value(v, at(&v, "xpReward"), views::json("text"), refs, &why);
        CHECK(!val && !why.empty());
        CHECK(!views::kiln_value(v, at(&v, "events"), views::json::array(), refs, &why) && !why.empty());
        val = views::kiln_value(v, at(&v, "stageXp"), views::json::parse("[1, 2, 3, 4]"), refs, &why);
        CHECK(val && val->size() == 4);
    }

    // ---- selectors
    CHECK(views::selects("game:quest_main", "game:quest_main", "Quest_Main"));
    CHECK(views::selects("game:quest_*", "game:quest_main", "Quest_Main"));
    CHECK(views::selects("*", "game:quest_main", ""));
    CHECK(views::selects("Quest_Main", "game:quest_main", "Quest_Main"));
    CHECK(views::selects("quest_main", "game:quest_main", "Quest_Main"));
    CHECK(!views::selects("game:other", "game:quest_main", "Quest_Main"));
    CHECK(!views::selects("", "game:quest_main", "Quest_Main"));

    std::printf("registry-views-test: %d passed, %d failed\n", g_pass, g_fail);
    return g_fail ? 1 : 0;
}
