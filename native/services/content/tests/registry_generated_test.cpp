// SPDX-License-Identifier: MIT
// kiln.content registry stage over the GENERATED schema (registry/schema.g.h through schema_ir.h), not a hand-filled
// descriptor: the IR reader, composition with its reference check, and registry.json for a realistic set of edits on the
// R1 registries: items and recipes with references (a mod's added item in an added recipe, an input whose item another mod
// removes), loot rarities as flags, the balance table's fixed-length lists edited per element, an optional reference
// cleared; and the edits the generated schema must refuse (a whole fixed-length list, a list op on it, clearing a reference
// that is not optional, a field that does not exist, a value outside the generated range).
#include <cstdio>
#include <set>
#include <string>

#include "../registry/compose.h"
#include "../registry/report.h"
#include "../registry/schema_ir.h"

using namespace kc::registry;

static int g_fail = 0, g_pass = 0;
#define CHECK(c)                                                                  \
    do {                                                                          \
        if (c) ++g_pass;                                                          \
        else { ++g_fail; std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #c); } \
    } while (0)

static Value stack(const char* item, int64_t count) {  // a recipe input: {category, count, item}, keys sorted
    Value v;
    v.kind = Value::Struct;
    v.keys = {"category", "count", "item"};
    v.list = {Value{}, Value::integer(count), Value::text(item)};
    return v;
}
static Value out_stack(const char* item, int64_t count) {  // an output: {count, item}
    Value v;
    v.kind = Value::Struct;
    v.keys = {"count", "item"};
    v.list = {Value::integer(count), Value::text(item)};
    return v;
}
static Value ints(std::initializer_list<int64_t> xs) {
    std::vector<Value> v;
    for (int64_t x : xs) v.push_back(Value::integer(x));
    return Value::of_list(v);
}

static BaseModel base() {
    BaseModel b;
    for (const char* id : {"game:wood", "game:flint", "game:plank", "game:sword"}) {
        BaseModel::Entry e;
        e.id = id;
        e.fields["maxStack"] = Value::integer(10);
        e.fields["perks"] = Value::of_list({});
        b.registries["items"].push_back(e);
    }
    BaseModel::Entry arrow, plank;
    arrow.id = "game:arrow";
    arrow.fields["inputs"] = Value::of_list({stack("game:flint", 1), stack("game:plank", 1)});
    arrow.fields["outputs"] = Value::of_list({out_stack("game:sword", 5)});
    plank.id = "game:plank_recipe";
    plank.fields["inputs"] = Value::of_list({stack("game:wood", 2)});
    plank.fields["outputs"] = Value::of_list({out_stack("game:plank", 4)});
    b.registries["recipes"] = {arrow, plank};
    BaseModel::Entry loot;
    loot.id = "game:wood";
    loot.fields["rarities"] = Value::of_list({Value::text("common")});
    b.registries["loot"] = {loot};
    BaseModel::Entry bal;
    bal.id = "game:balance";
    bal.fields["xpPerLevel"] = ints({1000, 2000, 3000, 4000, 5000});
    bal.fields["altarsPerFlameLevel"] = ints({1, 1, 2, 2, 3, 3});
    bal.fields["flameLevelMax"] = Value::integer(5);
    b.registries["balance"] = {bal};
    b.registries["drops"] = {}; // this synthetic copied model read an empty drop registry; it did not omit the safety read
    return b;
}

static Batch batch(const Schema& s, const std::string& mod, const std::string& stage, const std::string& edits, std::vector<Refusal>* refused) {
    Batch b;
    std::string err;
    if (!parse_batch(R"({"ir": 1, "mod": ")" + mod + R"(", "stage": ")" + stage + R"(", "source": "content/)" + mod + R"(.json", "edits": )" + edits + "}", s, &b, refused, &err))
        std::printf("batch refused whole: %s\n", err.c_str());
    return b;
}

static const FieldOutcome* field(const Result& r, const char* reg, const char* id, const char* f) {
    const auto it = r.fields.find({reg, id, f});
    return it == r.fields.end() ? nullptr : &it->second;
}

static bool has_ref(const Result& r, const std::vector<EntryRef>& v, const char* reg, const char* id) {
    for (const EntryRef& e : v)
        if (e.registry == reg && e.id == id) return true;
    (void)r;
    return false;
}

static bool refused_with(const std::vector<Refusal>& v, uint32_t n, const std::string& text) {
    for (const Refusal& x : v)
        if (x.n == n && x.reason.find(text) != std::string::npos) return true;
    return false;
}

int main() {
    const Schema s = generated_schema();
    // ---- the glue: the generated rows as the IR's descriptor
    CHECK(s.format == 1 && s.registries.size() == std::size(gen::kRegistries) && s.registries.size() >= 10);
    size_t specs = 0;
    for (const auto& [n, r] : s.registries) {
        specs += r.fields.size();
        // the shipped schema versions: items 0.1.1 since 31.x (block and comfort apply now), balance 0.1.1 since dev.32
        // (altarsPerFlameLevel up to 255), creatures 0.1.1 since dev.32's taming fields, the others 0.1.0; dev.32's skills,
        // stations, buffs and abilities take no raw game paths (raw: refused), and limits are Kiln's hooks (no raw game path)
        CHECK(r.version == (n == "items" || n == "balance" || n == "creatures" ? "0.1.1" : "0.1.0") &&
              r.raw == (n != "skills" && n != "stations" && n != "buffs" && n != "abilities" && n != "limits"));
    }
    CHECK(specs == std::size(gen::kFields));  // every generated row, leaves of list elements kept
    const RegistrySpec& rec = *s.registry("recipes");
    const FieldSpec* inputs = rec.field("inputs");
    CHECK(inputs && inputs->type == FieldType::List && inputs->element == FieldType::Struct && inputs->element_fields.size() == 3);
    CHECK(inputs && inputs->element_field("item") && inputs->element_field("item")->ref == "items" && inputs->element_field("item")->optional);
    CHECK(inputs && inputs->element_field("category") && inputs->element_field("category")->ref == "inputCategories");
    CHECK(rec.field("outputs[3].item") && rec.field("outputs[3].item")->ref == "items" && !rec.field("outputs[3].item")->optional);
    CHECK(s.registry("items")->field("perks")->ref == "perks" && s.registry("items")->field("perks")->element == FieldType::Id);
    CHECK(s.registry("loot")->field("rarities")->type == FieldType::Flags && s.registry("loot")->field("rarities")->enum_values.size() == 5);
    const FieldSpec* xp = s.registry("balance")->field("xpPerLevel");
    CHECK(xp && xp->type == FieldType::List && xp->ops == FieldOps::Indexed && xp->fixed_length == 5);
    CHECK(s.registry("balance")->field("altarsPerFlameLevel")->ops == FieldOps::Indexed && s.registry("balance")->field("flameLevelMax")->read_only);
    CHECK(s.registry("drops")->field("entries")->element == FieldType::Struct && s.registry("drops")->field("entries[0].count.min"));

    // ---- dev.32: buffs and abilities (gamedata registries: scalar fields, no raw paths, no entry add/remove)
    {
        const RegistrySpec* buffs = s.registry("buffs");
        const RegistrySpec* abil = s.registry("abilities");
        CHECK(buffs && abil && !buffs->raw && !abil->raw);
        const FieldSpec* dur = buffs ? buffs->field("duration") : nullptr;
        CHECK(dur && dur->type == FieldType::Float && dur->applies == Applies::Now && dur->min == 0 && dur->max == 86400 && dur->ops == FieldOps::Number);
        const FieldSpec* st = buffs ? buffs->field("stacking") : nullptr;
        CHECK(st && st->type == FieldType::Enum && st->enum_values == std::vector<std::string>({"keep", "refresh", "replace", "replaceAtEnd"}));
        CHECK(buffs && buffs->field("kind") && buffs->field("kind")->read_only && buffs->field("saved") && buffs->field("saved")->read_only);
        for (const char* t : {"timing.release", "timing.hit", "timing.cost", "timing.buff"}) {
            const FieldSpec* f = abil ? abil->field(t) : nullptr;
            CHECK(f && f->type == FieldType::Float && f->applies == Applies::Now && f->min == 0 && f->max == 600);
        }
        BaseModel b = base();
        BaseModel::Entry poison, fireball;
        poison.id = "game:buff_5f425faf";
        poison.fields["duration"] = Value::number(10), poison.fields["stacking"] = Value::text("refresh");
        b.registries["buffs"] = {poison};
        fireball.id = "game:attack_player_staff_fixedanim_fireball_fast";
        fireball.fields["timing.release"] = Value::number(0.65), fireball.fields["timing.cost"] = Value::number(0.65);
        b.registries["abilities"] = {fireball};
        std::vector<Refusal> rf;
        ModInput m;
        m.id = "spells";
        m.batches.push_back(batch(s, "spells", "alter", R"([
            {"registry": "buffs", "select": {"id": "game:buff_5f425faf"}, "field": "duration", "op": {"mul": 2}},
            {"registry": "buffs", "select": {"id": "game:buff_5f425faf"}, "field": "stacking", "op": "replace"},
            {"registry": "abilities", "select": {"id": "game:attack_player_staff_fixedanim_fireball_fast"}, "field": "timing.release", "op": 0.45},
            {"registry": "abilities", "select": {"id": "game:attack_player_staff_fixedanim_fireball_fast"}, "field": "timing.cost", "op": {"add": -0.2}}
        ])", &rf));
        CHECK(rf.empty());
        const Result r = compose(s, b, {}, {m});
        const FieldOutcome* d = field(r, "buffs", "game:buff_5f425faf", "duration");
        const FieldOutcome* k = field(r, "buffs", "game:buff_5f425faf", "stacking");
        const FieldOutcome* rel = field(r, "abilities", "game:attack_player_staff_fixedanim_fireball_fast", "timing.release");
        const FieldOutcome* cost = field(r, "abilities", "game:attack_player_staff_fixedanim_fireball_fast", "timing.cost");
        CHECK(d && d->result.as_double() == 20 && k && k->result.t == "replace");
        CHECK(rel && rel->result.as_double() == 0.45 && cost && cost->result.as_double() > 0.449 && cost->result.as_double() < 0.451);
        // refused: a value the enum lacks, a read-only field, a duration past the range, a raw path, an entry add
        std::vector<Refusal> bad;
        batch(s, "bad", "alter", R"([
            {"registry": "buffs", "select": {"id": "game:buff_5f425faf"}, "field": "stacking", "op": "stack"},
            {"registry": "buffs", "select": {"id": "game:buff_5f425faf"}, "field": "kind", "op": "food"},
            {"registry": "buffs", "select": {"id": "game:buff_5f425faf"}, "field": "duration", "op": 100000},
            {"registry": "abilities", "select": {"id": "game:attack_player_staff_fixedanim_fireball_fast"}, "field": "timing.release", "op": 601},
            {"registry": "buffs", "select": {"id": "game:buff_5f425faf"}, "raw": {"defaultLifeTime": 5}}
        ])", &bad);
        CHECK(bad.size() == 5);
    }

    // ---- what the generated schema refuses
    {
        std::vector<Refusal> rf;
        batch(s, "bad", "alter", R"([
            {"registry": "balance", "select": {"id": "game:balance"}, "field": "xpPerLevel", "op": [1, 2, 3, 4, 5]},
            {"registry": "balance", "select": {"id": "game:balance"}, "field": "xpPerLevel", "list": {"append": [6000]}},
            {"registry": "balance", "select": {"id": "game:balance"}, "field": "xpPerLevel[5]", "op": 1},
            {"registry": "recipes", "select": {"id": "game:arrow"}, "field": "outputs[0].item", "op": null},
            {"registry": "items", "select": {"id": "game:wood"}, "field": "maxStackSize", "op": 5},
            {"registry": "items", "select": {"id": "game:wood"}, "field": "maxStack", "op": 70000},
            {"registry": "loot", "select": {"id": "game:wood"}, "field": "rarities", "op": ["mythic"]},
            {"registry": "balance", "select": {"id": "game:balance"}, "field": "flameLevelMax", "op": 30}
        ])", &rf);
        for (const Refusal& x : rf) std::printf("  refused #%u: %s\n", x.n, x.reason.c_str());
        CHECK(rf.size() == 8);
        CHECK(refused_with(rf, 0, "fixed length") && refused_with(rf, 1, "fixed length") && refused_with(rf, 2, "5"));
        CHECK(refused_with(rf, 3, "outputs[0].item"));
    }

    // ---- a realistic set, composed
    std::vector<Refusal> r1, r2, r3;
    ModInput a, b, c;
    a.id = "newstuff", c.id = "tuner", b.id = "remover";
    a.batches.push_back(batch(s, "newstuff", "add", R"([
        {"registry": "items", "entry": "add", "id": "newstuff:crate", "like": "game:wood", "fields": {"maxStack": 50}},
        {"registry": "recipes", "entry": "add", "id": "newstuff:crate_recipe",
         "fields": {"inputs": [{"item": "game:wood", "count": 3}], "outputs": [{"item": "newstuff:crate", "count": 1}]}}
    ])", &r1));
    c.batches.push_back(batch(s, "tuner", "alter", R"([
        {"registry": "items", "select": {"id": "game:*"}, "field": "maxStack", "op": {"mul": 4}},
        {"registry": "items", "select": {"id": "game:sword"}, "field": "perks", "list": {"append": ["game:perk_fire"]}},
        {"registry": "loot", "select": {"id": "game:wood"}, "field": "rarities", "op": ["rare", "epic"]},
        {"registry": "balance", "select": {"id": "game:balance"}, "field": "xpPerLevel[2]", "op": 1500},
        {"registry": "balance", "select": {"id": "game:balance"}, "field": "altarsPerFlameLevel[3]", "op": {"add": 1}},
        {"registry": "recipes", "select": {"id": "game:plank_recipe"}, "field": "inputs[0].item", "op": null},
        {"registry": "recipes", "select": {"id": "game:plank_recipe"}, "field": "inputs[0].category", "op": "game:logs"}
    ])", &r2));
    b.batches.push_back(batch(s, "remover", "alter", R"([{"registry": "items", "entry": "remove", "select": {"id": "game:flint"}}])", &r3));
    for (const auto* v : {&r1, &r2, &r3})
        for (const Refusal& x : *v) std::printf("  unexpected refusal %s #%u: %s\n", x.mod.c_str(), x.n, x.reason.c_str());
    CHECK(r1.empty() && r2.empty() && r3.empty());
    const Result r = compose(s, base(), {}, {a, c, b});
    for (const Refusal& x : r.refused) std::printf("  refused in the stage %s #%u: %s\n", x.mod.c_str(), x.n, x.reason.c_str());
    CHECK(r.refused.empty());
    const FieldOutcome* ms = field(r, "items", "game:wood", "maxStack");
    CHECK(ms && ms->result.i == 40);
    CHECK(has_ref(r, r.added, "items", "newstuff:crate") && has_ref(r, r.added, "recipes", "newstuff:crate_recipe"));
    const FieldOutcome* crate = field(r, "items", "newstuff:crate", "maxStack");
    CHECK(crate && crate->result.i == 50);  // a mod's added entry is not a game:* entry
    CHECK(has_ref(r, r.removed, "items", "game:flint") && has_ref(r, r.removed, "recipes", "game:arrow"));  // its input is gone
    CHECK(!has_ref(r, r.removed, "recipes", "game:plank_recipe"));
    const FieldOutcome* perks = field(r, "items", "game:sword", "perks");
    CHECK(perks && perks->result.list.size() == 1 && perks->result.list[0].t == "game:perk_fire");  // perks: not checked here
    const FieldOutcome* rar = field(r, "loot", "game:wood", "rarities");
    CHECK(rar && rar->result.list.size() == 2 && rar->result.list[0].t == "rare" && rar->result.list[1].t == "epic");
    const FieldOutcome* xpo = field(r, "balance", "game:balance", "xpPerLevel");
    CHECK(xpo && xpo->result.list.size() == 5 && xpo->result.list[2].i == 1500 && xpo->result.list[3].i == 4000);
    const FieldOutcome* alt = field(r, "balance", "game:balance", "altarsPerFlameLevel");
    CHECK(alt && alt->result.list.size() == 6 && alt->result.list[3].i == 3);
    const FieldOutcome* pin = field(r, "recipes", "game:plank_recipe", "inputs");
    CHECK(pin && pin->result.list.size() == 1);
    if (pin && pin->result.list.size() == 1) {
        const Value& in0 = pin->result.list[0];
        CHECK(in0.leaf("item") && in0.leaf("item")->kind == Value::None);   // cleared
        CHECK(in0.leaf("category") && in0.leaf("category")->t == "game:logs");
        CHECK(in0.leaf("count") && in0.leaf("count")->i == 2);
    }

    // ---- the second review's cases over the generated schema (dev31/registry-b round 2): ops, fixed lengths, read-only
    // leaves given on a new element, references enforced through the generated "ref"s, the game-entry rule
    {
        BaseModel bb = base();
        BaseModel::Entry table;
        table.id = "game:table1";
        Value w;
        w.kind = Value::Struct, w.keys = {"chance", "count.max", "item", "weight"};
        w.list = {Value::number(0.5), Value::integer(1), Value::text("game:wood"), Value::number(1)};
        Value f2 = w;
        f2.list[2] = Value::text("game:flint");
        table.fields["entries"] = Value::of_list({w, f2});
        table.drop_groups = {{0, 2}};
        bb.registries["drops"] = {table};
        std::vector<Refusal> rf;
        const Batch d = batch(s, "dropper", "alter", R"([
            {"registry": "drops", "select": {"id": "game:table1"}, "field": "entries", "list": {"append": [{"item": "game:plank", "chance": 0.25}]}},
            {"registry": "drops", "select": {"id": "game:table1"}, "field": "entries[0].item", "op": "game:plank"},
            {"registry": "drops", "select": {"id": "game:table1"}, "field": "entries[1].chance", "op": {"mul": 2}}
        ])", &rf);
        for (const Refusal& x : rf) std::printf("  refused #%u: %s\n", x.n, x.reason.c_str());
        CHECK(d.edits.size() == 2 && rf.size() == 1 && refused_with(rf, 1, "read-only"));  // the item is given on append, never edited after
        ModInput dm;
        dm.id = "dropper", dm.batches = {d};
        ModInput rm;
        rm.id = "remover";
        std::vector<Refusal> rr;
        rm.batches = {batch(s, "remover", "alter", R"([{"registry": "items", "entry": "remove", "select": {"id": "game:wood"}}])", &rr)};
        const Result dr = compose(s, bb, {}, {dm, rm});
        const FieldOutcome* en = field(dr, "drops", "game:table1", "entries");
        CHECK(en && en->result.list.size() == 3 && en->result.list[0].leaf("item")->t == "game:wood" && en->result.list[2].leaf("item")->t == "game:plank");
        CHECK(en && en->result.list[1].leaf("chance")->d == 1.0);
        CHECK(refused_with(dr.refused, 0, "grouping") && !has_ref(dr, dr.removed, "items", "game:wood") && !has_ref(dr, dr.removed, "recipes", "game:plank_recipe")); // structural append invalidated copied group spans: refuse removal whole
        auto fixed = dm; fixed.batches.front().edits.erase(fixed.batches.front().edits.begin()); // only the existing-slot chance edit
        const auto safe = compose(s, bb, {}, {fixed, rm});
        const auto* inert = field(safe, "drops", "game:table1", "entries");
        CHECK(inert && inert->result.list.size() == 2 && inert->result.list[0].leaf("item")->t == "game:wood" && inert->result.list[1].leaf("item")->t == "game:flint");
        CHECK(inert && inert->result.list[0].leaf("chance")->as_double() == 0 && inert->result.list[0].leaf("weight")->as_double() == 0 && inert->result.list[1].leaf("chance")->as_double() == 1);
        CHECK(!has_ref(safe, safe.removed, "drops", "game:table1") && has_ref(safe, safe.removed, "recipes", "game:plank_recipe")); // retained physical slot, dependent recipe hidden
        // a mod's recipe whose output item was refused before the stage is refused, with the chain
        Refusal pre;
        pre.mod = "newstuff", pre.stage = Stage::Add, pre.entry = "items|newstuff:lantern", pre.reason = "Unmapped: items.power is not mapped for game build 1287369 yet";
        ModInput nm;
        nm.id = "newstuff";
        std::vector<Refusal> nr;
        nm.batches = {batch(s, "newstuff", "add", R"([{"registry": "recipes", "entry": "add", "id": "newstuff:lantern_recipe",
                                                         "fields": {"inputs": [{"item": "game:wood", "count": 1}], "outputs": [{"item": "newstuff:lantern", "count": 1}]}}])", &nr)};
        nm.refused = {pre};
        const Result lr = compose(s, base(), {}, {nm});
        bool chained = false;
        for (const Refusal& x : lr.refused) chained = chained || x.reason.find("recipes newstuff:lantern_recipe refused: outputs[0].item newstuff:lantern refused: Unmapped") != std::string::npos;
        CHECK(nr.empty() && chained && !has_ref(lr, lr.added, "recipes", "newstuff:lantern_recipe"));
        // code edits are held to the generated ops too: a list op on a fixed-length list
        ModInput code;
        code.id = "coder";
        code.code = [](Stage st, const View&, Batch* out) {
            if (st != Stage::Alter) return;
            Edit e;
            e.registry = "balance", e.select.id = "game:balance", e.field = "xpPerLevel", e.list = ListOp::Clear;
            out->edits.push_back(e);
        };
        const Result cr = compose(s, base(), {}, {code});
        CHECK(cr.refused.size() == 1 && cr.refused[0].reason.find("fixed length") != std::string::npos && !field(cr, "balance", "game:balance", "xpPerLevel"));
        // every list stays within its generated maxItems: perks hold at most 5
        std::vector<Refusal> pr;
        ModInput pm;
        pm.id = "perky";
        pm.batches = {batch(s, "perky", "alter", R"([{"registry": "items", "select": {"id": "game:sword"}, "field": "perks", "list": {"append": ["game:p1", "game:p2", "game:p3", "game:p4", "game:p5", "game:p6"]}}])", &pr)};
        const Result pres = compose(s, base(), {}, {pm});
        CHECK(pr.size() + pres.refused.size() == 1 && !field(pres, "items", "game:sword", "perks"));
    }

    // ---- registry.json over the generated schema
    ReportMeta meta;
    meta.pid = 1, meta.written = "2026-11-20T16:00:00Z", meta.kiln = "0.2.0-dev.31", meta.generation = 1;
    meta.regmap = {"server", "f0d824be7fcd48ebfe2458ff5c28da26060c2074e3ec1fcac587e5a1d508aeb2", "regmap/server/f0d824be7fcd48ebfe2458ff5c28da26060c2074e3ec1fcac587e5a1d508aeb2.json", true};
    meta.order = {{"newstuff", "1.0.0", ""}, {"tuner", "1.0.0", ""}, {"remover", "1.0.0", ""}};
    const json doc = report_json(s, r, meta);
    CHECK(doc["schema"]["registrySchema"] == 1 && doc["schema"]["registries"]["items"] == "0.1.1" && doc["schema"]["registries"].size() == s.registries.size() &&
          doc["schema"]["registries"]["creatures"] == "0.1.1" && doc["schema"]["registries"]["randomLoot"] == "0.1.0" &&  // creatures: TAME's fields
          doc["schema"]["registries"]["lootRules"] == "0.1.0");
    bool saw_ms = false, saw_xp = false;
    for (const json& f : doc["fields"]) {
        saw_ms = saw_ms || (f["registry"] == "items" && f["id"] == "game:wood" && f["field"] == "maxStack" && f["applies"] == "worldLoad");
        saw_xp = saw_xp || (f["registry"] == "balance" && f["field"] == "xpPerLevel" && f["applies"] == "restart");
    }
    CHECK(saw_ms && saw_xp);
    const auto log = report_log(r);
    CHECK(!log.empty());

    // ---- an element's value set over an old boot patch's value: "overridden" (D1), not "composedOn"
    {
        std::vector<Refusal> rf;
        OldWrite ow;
        ow.kind = WriterKind::OldPatch, ow.mod = "oldxp", ow.who = "oldxp/xp";
        ow.field = {"balance", "game:balance", "xpPerLevel"};
        ow.op.set = true, ow.op.value = ints({1500, 2000, 3000, 4000, 5000});
        ModInput m;
        m.id = "newxp";
        m.batches.push_back(batch(s, "newxp", "alter", R"([{"registry": "balance", "select": {"id": "game:balance"}, "field": "xpPerLevel[0]", "op": 2222}])", &rf));
        const Result x = compose(s, base(), {ow}, {m});
        const FieldOutcome* fo = field(x, "balance", "game:balance", "xpPerLevel");
        CHECK(fo && fo->result.list.size() == 5 && fo->result.list[0].i == 2222);
        size_t over = 0, on = 0;
        for (const Line& l : x.lines) over += l.kind == LineKind::Overridden, on += l.kind == LineKind::ComposedOn;
        CHECK(over == 1 && on == 0);
    }
    std::printf("registry-generated-test: %d passed, %d failed\n", g_pass, g_fail);
    return g_fail ? 1 : 0;
}
