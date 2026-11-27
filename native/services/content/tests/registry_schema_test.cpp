// SPDX-License-Identifier: MIT
// kiln.content registry stage: the generated schema (registry/schema.g.h, tools/registrygen) as the edit IR's descriptor
// (registry/schema_ir.h -> ir.h's Schema), on the host. Every row of the generated table becomes a FieldSpec with its type,
// range, values and applies; IR documents written against the R1 registries parse or are refused as the schema says.
#include <cstdio>
#include <set>
#include <string>

#include "../registry/ir.h"
#include "../registry/schema_ir.h"

using namespace kc::registry;

static int g_fail = 0, g_pass = 0;
#define CHECK(c)                                                                  \
    do {                                                                          \
        if (c) ++g_pass;                                                          \
        else { ++g_fail; std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #c); } \
    } while (0)

static std::vector<Refusal> parse(const Schema& s, const char* doc, Batch* b) {
    std::vector<Refusal> refused;
    std::string err;
    const bool ok = parse_batch(std::string(doc), s, b, &refused, &err);
    if (!ok) std::printf("document refused: %s\n", err.c_str());
    CHECK(ok);
    return refused;
}

int main() {
    const Schema s = generated_schema();
    // ---- the five R1 registries and dev.32's creatures, arsenals, perks, skills, stations, buffs and abilities, every row (later
    // waves add theirs)
    CHECK(s.registries.size() == std::size(gen::kRegistries));
    for (const char* r : {"items", "recipes", "loot", "drops", "balance", "creatures", "arsenals", "perks", "skills", "stations", "buffs", "abilities"})
        CHECK(s.registry(r) != nullptr);
    // pets and taming read a creature's kind (read-only) and its template id; an arsenal's attacks are a list of structs
    const FieldSpec* kind = s.registry("creatures")->field("kind");
    CHECK(kind && kind->read_only && kind->type == FieldType::Enum && kind->enum_values.size() == 4);
    const FieldSpec* arsenal = s.registry("creatures")->field("arsenal");
    CHECK(arsenal && arsenal->read_only && arsenal->ref == "arsenals");
    const FieldSpec* attacks = s.registry("arsenals")->field("attacks");
    CHECK(attacks && attacks->type == FieldType::List && attacks->element == FieldType::Struct && attacks->applies == Applies::Now);
    const FieldSpec* value = s.registry("perks")->field("value");
    CHECK(value && value->type == FieldType::Float && value->min && *value->min == -100000 && value->applies == Applies::Now);
    CHECK(s.registry("perks")->field("damage.ice")->applies == Applies::WorldLoad);
    size_t rows = 0;
    for (const auto& g : gen::kRegistries) rows += g.count;
    CHECK(rows == std::size(gen::kFields));
    size_t specs = 0;
    for (const auto& [n, r] : s.registries) specs += r.fields.size();
    CHECK(specs == std::size(gen::kFields));
    // ---- types, ranges, applies
    const FieldSpec* ms = s.registry("items")->field("maxStack");
    CHECK(ms && ms->type == FieldType::Int && ms->min == 1.0 && ms->max == 65535.0 && ms->applies == Applies::WorldLoad && !ms->read_only);
    const FieldSpec* rar = s.registry("items")->field("rarity");
    CHECK(rar && rar->type == FieldType::Enum && rar->enum_values == std::vector<std::string>({"common", "uncommon", "rare", "epic", "legendary"}));
    const FieldSpec* perks = s.registry("items")->field("perks");
    CHECK(perks && perks->type == FieldType::List && perks->element == FieldType::Id && perks->max_items == 5 && perks->applies == Applies::Now);
    const FieldSpec* hd = s.registry("items")->field("hasDamage");
    CHECK(hd && hd->read_only);
    const FieldSpec* burn = s.registry("items")->field("fuel.burnTime");
    CHECK(burn && burn->type == FieldType::Float && burn->applies == Applies::WorldLoad);
    const FieldSpec* cap = s.registry("balance")->field("levelCap");
    CHECK(cap && cap->applies == Applies::Restart);
    const FieldSpec* alt = s.registry("balance")->field("altarsPerFlameLevel");
    CHECK(alt && alt->type == FieldType::List && alt->element == FieldType::Int && alt->max && *alt->max == 16.0 && alt->ops == FieldOps::Indexed);
    const FieldSpec* rarities = s.registry("loot")->field("rarities");
    CHECK(rarities && rarities->type == FieldType::Flags && rarities->max_items == 5);
    const FieldSpec* cnt = s.registry("recipes")->field("inputs[].count");
    CHECK(cnt && cnt->type == FieldType::Int && cnt->min == 1.0);
    CHECK(!s.registry("items")->add == false && s.registry("drops")->add == false && s.registry("drops")->remove == false);
    CHECK(s.registry("loot")->remove && !s.registry("loot")->add);
    // ---- lists whose elements have fields: Struct elements with their leaves (link_elements)
    for (const char* q : {"recipes.inputs", "recipes.outputs", "drops.entries", "balance.buildzoneSizesPerAltarLevel"}) {
        const std::string qs = q;
        const auto dot = qs.find('.');
        const FieldSpec* l = s.registry(qs.substr(0, dot))->field(qs.substr(dot + 1));
        CHECK(l && l->type == FieldType::List && l->element == FieldType::Struct && !l->element_fields.empty());
    }
    // ---- IR against the generated schema
    {
        Batch b;
        auto refused = parse(s, R"({"ir": 1, "mod": "bigstacks", "stage": "alter", "source": "content/a.json", "edits": [
            {"registry": "items", "select": {"id": "game:material_*"}, "field": "maxStack", "op": {"mul": 4, "max": 9999}},
            {"registry": "items", "select": {"id": "game:weapon_x"}, "field": "rarity", "op": "epic"},
            {"registry": "items", "select": {"id": "game:weapon_x"}, "field": "damage.fire", "op": {"add": 0.25}},
            {"registry": "items", "select": {"id": "game:weapon_x"}, "field": "perks", "list": {"append": ["game:perk_fire"]}},
            {"registry": "balance", "select": {"id": "game:balance"}, "field": "levelCap", "op": 60},
            {"registry": "loot", "select": {"id": "game:material_t1_wood_logs"}, "field": "stack.max", "op": {"mul": 2}},
            {"registry": "items", "select": {"id": "game:weapon_x"}, "field": "maxStackSize", "op": 5},
            {"registry": "items", "select": {"id": "game:weapon_x"}, "field": "hasDamage", "op": true},
            {"registry": "items", "select": {"id": "game:weapon_x"}, "field": "maxStack", "op": 70000},
            {"registry": "items", "select": {"id": "game:weapon_x"}, "field": "rarity", "op": "mythic"},
            {"registry": "balance", "select": {"id": "game:balance"}, "field": "altarsPerFlameLevel", "list": {"replace": [1, 2, 17]}},
            {"registry": "drops", "entry": "remove", "select": {"id": "game:x"}}
        ]})", &b);
        CHECK(b.edits.size() == 6);
        CHECK(refused.size() == 6);
        std::set<uint32_t> ns;
        for (const Refusal& r : refused) ns.insert(r.n);
        CHECK(ns == std::set<uint32_t>({6, 7, 8, 9, 10, 11}));
    }
    {
        Batch b;
        auto refused = parse(s, R"({"ir": 1, "mod": "newstuff", "stage": "add", "edits": [
            {"registry": "items", "entry": "add", "id": "newstuff:crate", "like": "game:material_t1_wood_logs", "fields": {"maxStack": 500, "rarity": "rare"}},
            {"registry": "loot", "entry": "add", "id": "newstuff:crate"}
        ]})", &b);
        CHECK(b.edits.size() == 1 && refused.size() == 1);
    }
    std::printf("registry-schema-test: %d passed, %d failed\n", g_pass, g_fail);
    return g_fail ? 1 : 0;
}
