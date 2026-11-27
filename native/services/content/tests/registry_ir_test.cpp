// SPDX-License-Identifier: MIT
// Host test of the edit IR's shapes the generated schema needs (registry/ir.cpp, registry/compose.cpp): lists whose elements
// have fields of their own (recipes.inputs {item, count}), element paths ("inputs[2].count", "inputs[*].count",
// "altarsPerFlameLevel[2]") that edit inside the list's own chain, flags (a set of the schema's names), field() reading
// "[3]" and "[*]" as "[]", and the data pack section {"add" | "alter" | "final": [edits]} lowered to IR batches.
#include <cstdio>
#include <string>

#include "../registry/compose.h"

using namespace kc::registry;

static int g_fail = 0, g_pass = 0;
#define CHECK(cond, what)                                                       \
    do {                                                                        \
        if (cond) ++g_pass;                                                     \
        else {                                                                  \
            ++g_fail;                                                           \
            std::printf("FAIL %s:%d %s\n", __FILE__, __LINE__, what);          \
        }                                                                       \
    } while (0)

static FieldSpec spec(const char* name, FieldType t, Applies a = Applies::Now) {
    FieldSpec f;
    f.name = name, f.type = t, f.applies = a;
    return f;
}

static Schema schema() {
    Schema s;
    RegistrySpec recipes;
    recipes.name = "recipes";
    FieldSpec inputs = spec("inputs", FieldType::List);
    inputs.max_items = 8;
    FieldSpec item = spec("inputs[].item", FieldType::Id);
    FieldSpec count = spec("inputs[].count", FieldType::Int);
    count.min = 1, count.max = 999;
    FieldSpec cat = spec("inputs[].category", FieldType::Int);
    cat.read_only = true;
    FieldSpec dur = spec("duration", FieldType::Float);
    dur.min = 0, dur.max = 3600;
    for (const FieldSpec& f : {inputs, item, count, cat, dur}) recipes.fields[f.name] = f;
    s.registries["recipes"] = recipes;
    RegistrySpec items;
    items.name = "items";
    FieldSpec tags = spec("tags", FieldType::Flags);
    tags.enum_values = {"storage", "fuel", "quest"};
    FieldSpec perks = spec("perks", FieldType::List);
    perks.element = FieldType::Id, perks.max_items = 3;
    for (const FieldSpec& f : {tags, perks}) items.fields[f.name] = f;
    s.registries["items"] = items;
    RegistrySpec bal;
    bal.name = "balance", bal.add = bal.remove = false;
    FieldSpec altars = spec("altarsPerFlameLevel", FieldType::List, Applies::Restart);
    altars.element = FieldType::Int, altars.min = 0, altars.max = 64;
    bal.fields[altars.name] = altars;
    s.registries["balance"] = bal;
    // drops.entries: an element whose count is a struct of its own (count.min, count.max) and whose item is given when it is made
    RegistrySpec drops;
    drops.name = "drops", drops.add = false;
    FieldSpec entries = spec("entries", FieldType::List);
    FieldSpec ditem = spec("entries[].item", FieldType::Id);
    ditem.read_only = true;
    FieldSpec cmin = spec("entries[].count.min", FieldType::Int), cmax = spec("entries[].count.max", FieldType::Int);
    cmin.min = cmax.min = 0, cmin.max = cmax.max = 1000;
    FieldSpec chance = spec("entries[].chance", FieldType::Float);
    chance.min = 0, chance.max = 1;
    for (const FieldSpec& f : {entries, ditem, cmin, cmax, chance}) drops.fields[f.name] = f;
    s.registries["drops"] = drops;
    link_elements(&s);
    return s;
}

static Value input(const char* item, int64_t count) {
    Value v;
    v.kind = Value::Struct;
    v.keys = {"count", "item"};
    v.list = {Value::integer(count), Value::text(item)};
    return v;
}

static BaseModel base() {
    BaseModel b;
    BaseModel::Entry r;
    r.id = "game:arrow";
    r.fields["inputs"] = Value::of_list({input("game:wood", 2), input("game:flint", 3)});
    r.fields["duration"] = Value::number(1.0);
    b.registries["recipes"] = {r};
    BaseModel::Entry it;
    it.id = "game:chest";
    it.fields["tags"] = Value::of_list({Value::text("storage")});
    it.fields["perks"] = Value::of_list({});
    b.registries["items"] = {it};
    BaseModel::Entry bal;
    bal.id = "game:balance";
    bal.fields["altarsPerFlameLevel"] = Value::of_list({Value::integer(1), Value::integer(2), Value::integer(3)});
    b.registries["balance"] = {bal};
    return b;
}

static Batch batch(const Schema& s, const std::string& mod, const std::string& stage, const std::string& edits, std::vector<Refusal>* refused = nullptr) {
    Batch b;
    std::vector<Refusal> r;
    std::string err;
    if (!parse_batch(R"({"ir": 1, "mod": ")" + mod + R"(", "stage": ")" + stage + R"(", "source": "t", "edits": )" + edits + "}", s, &b, &r, &err))
        std::printf("batch: %s\n", err.c_str());
    if (refused) *refused = r;
    else
        for (const Refusal& x : r) std::printf("unexpected refusal %s #%u: %s\n", x.mod.c_str(), x.n, x.reason.c_str());
    return b;
}

static ModInput mod(const std::string& id, std::vector<Batch> batches) {
    ModInput m;
    m.id = id;
    m.batches = std::move(batches);
    return m;
}

static Value result(const Result& r, const std::string& reg, const std::string& id, const std::string& field) {
    const auto it = r.fields.find({reg, id, field});
    return it == r.fields.end() ? Value{} : it->second.result;
}

static std::string counts(const Value& list) {
    std::string out;
    for (const Value& e : list.list)
        if (const Value* c = e.leaf("count")) out += (out.empty() ? "" : ",") + std::to_string(c->i);
    return out;
}

static std::string names(const Value& list) {
    std::string out;
    for (const Value& e : list.list) out += (out.empty() ? "" : ",") + e.t;
    return out;
}

static std::string alter(const std::string& reg, const std::string& id, const std::string& field, const std::string& opkey, const std::string& op) {
    return R"([{"registry": ")" + reg + R"(", "select": {"id": ")" + id + R"("}, "field": ")" + field + R"(", ")" + opkey + R"(": )" + op + "}]";
}

int main() {
    const Schema s = schema();
    const BaseModel b = base();

    // ---- field(): an element's index reads as [] ----
    {
        CHECK(generic_path("inputs[3].count") == "inputs[].count" && generic_path("inputs[*].count") == "inputs[].count" && generic_path("a[1][2]") == "a[][]",
              "generic_path");
        const RegistrySpec& r = *s.registry("recipes");
        CHECK(r.field("inputs[2].count") && r.field("inputs[2].count")->name == "inputs[].count" && r.field("inputs[*].item"), "field() finds a leaf by an indexed path");
        CHECK(r.field("inputs")->element == FieldType::Struct && r.field("inputs")->element_fields.size() == 3 && r.field("inputs")->element_field("count"),
              "link_elements: the list's element has its leaves");
        CHECK(!r.field("nothing[1].count"), "field(): no such list");
    }

    // ---- lists of elements with fields ----
    {
        std::vector<Refusal> rf;
        Batch x = batch(s, "a", "alter", R"([
            {"registry": "recipes", "select": {"id": "game:arrow"}, "field": "inputs", "list": {"append": [{"item": "game:stone", "count": 4}]}},
            {"registry": "recipes", "select": {"id": "game:arrow"}, "field": "inputs", "list": {"append": [{"item": "game:stone", "colour": 4}]}},
            {"registry": "recipes", "select": {"id": "game:arrow"}, "field": "inputs", "list": {"append": [{"item": "game:stone", "count": 0}]}},
            {"registry": "recipes", "select": {"id": "game:arrow"}, "field": "inputs", "list": {"append": [{"category": 3}]}},
            {"registry": "recipes", "select": {"id": "game:arrow"}, "field": "inputs", "list": {"append": [5]}}
        ])", &rf);
        CHECK(x.edits.size() == 2 && rf.size() == 3, "an element is {leaf: value}: unknown leaves, out-of-range values and plain values refused; a read-only leaf is given in a new element");
        const Result r = compose(s, b, {}, {mod("a", {x})});
        const Value v = result(r, "recipes", "game:arrow", "inputs");
        CHECK(v.list.size() == 4 && counts(v) == "2,3,4" && v.list[2].leaf("item")->t == "game:stone" && v.list[3].leaf("category") && v.list[3].leaf("category")->i == 3,
              "appended elements with fields (a read-only leaf set when the element is made)");
    }

    // ---- element paths: inside the list's own chain ----
    {
        const Result one = compose(s, b, {}, {mod("a", {batch(s, "a", "alter", alter("recipes", "game:arrow", "inputs[1].count", "op", R"({"mul": 2})"))})});
        CHECK(counts(result(one, "recipes", "game:arrow", "inputs")) == "2,6" && one.refused.empty(), "inputs[1].count x2");
        CHECK(one.fields.count({"recipes", "game:arrow", "inputs"}) && one.fields.size() == 1, "an element edit is a row of the list's chain");
        const Result all = compose(s, b, {}, {mod("a", {batch(s, "a", "alter", R"([{"registry": "recipes", "select": {"id": "game:arrow"}, "field": "inputs", "list": {"append": [{"item": "game:stone", "count": 4}]}}])")}),
                                              mod("b", {batch(s, "b", "alter", alter("recipes", "game:arrow", "inputs[*].count", "op", R"({"add": 1})"))})});
        CHECK(counts(result(all, "recipes", "game:arrow", "inputs")) == "3,4,5" && all.lines.size() == 1 && all.lines[0].kind == LineKind::Composed,
              "inputs[*].count +1 after another mod's append: every element, composed and reported");
        const Result far = compose(s, b, {}, {mod("a", {batch(s, "a", "alter", alter("recipes", "game:arrow", "inputs[5].count", "op", "7"))})});
        CHECK(far.refused.size() == 1 && far.refused[0].reason.find("no element 5") != std::string::npos, "an element that is not there refuses the edit");
        const Result high = compose(s, b, {}, {mod("a", {batch(s, "a", "alter", alter("recipes", "game:arrow", "inputs[*].count", "op", R"({"mul": 400})"))})});
        CHECK(high.refused.size() == 1 && high.fields.empty(), "a leaf's range holds for every element (all or nothing)");
        const Result whole = compose(s, b, {}, {mod("a", {batch(s, "a", "alter", alter("recipes", "game:arrow", "inputs[0]", "op", R"({"set": {"item": "game:iron", "count": 1}})"))})});
        const Value w = result(whole, "recipes", "game:arrow", "inputs");
        CHECK(w.list.size() == 2 && w.list[0].leaf("item")->t == "game:iron" && counts(w) == "1,3", "a whole element set with {set: {...}}");
        const Result alt = compose(s, b, {}, {mod("a", {batch(s, "a", "alter", alter("balance", "game:balance", "altarsPerFlameLevel[2]", "op", R"({"add": 10})"))})});
        const Value av = result(alt, "balance", "game:balance", "altarsPerFlameLevel");
        CHECK(av.list.size() == 3 && av.list[2].i == 13, "a plain list's element: a relative change with the list's range");
        std::vector<Refusal> rf;
        batch(s, "a", "alter", R"([
            {"registry": "recipes", "select": {"id": "game:arrow"}, "field": "inputs[1].count", "list": {"clear": true}},
            {"registry": "recipes", "select": {"id": "game:arrow"}, "field": "inputs[1].colour", "op": 1},
            {"registry": "recipes", "select": {"id": "game:arrow"}, "field": "inputs[1].category", "op": 1},
            {"registry": "balance", "select": {"id": "game:balance"}, "field": "altarsPerFlameLevel[1].count", "op": 1},
            {"registry": "recipes", "select": {"id": "game:arrow"}, "field": "inputs[0]", "op": {"item": "game:iron"}},
            {"registry": "recipes", "select": {"id": "game:arrow"}, "field": "inputs[x].count", "op": 1},
            {"registry": "recipes", "select": {"id": "game:arrow"}, "field": "duration[1]", "op": 1},
            {"registry": "balance", "select": {"id": "game:balance"}, "field": "altarsPerFlameLevel[1]", "op": 99}
        ])", &rf);
        CHECK(rf.size() == 8, "element paths: a list op, an unknown or read-only leaf, a leaf of a plain list, a plain object, a bad index, a non-list, a value out of range: refused");
        std::vector<Refusal> rf2;
        Batch norm = batch(s, "a", "alter", alter("recipes", "game:arrow", "inputs[02].count", "op", "5"), &rf2);
        CHECK(norm.edits.size() == 1 && norm.edits[0].field == "inputs[2].count" && norm.edits[0].element.index == 2, "an element path is kept with a plain index");
    }

    // ---- flags: a set of the schema's names ----
    {
        const Result set = compose(s, b, {}, {mod("a", {batch(s, "a", "alter", alter("items", "game:chest", "tags", "op", R"(["Quest", "storage"])"))})});
        CHECK(names(result(set, "items", "game:chest", "tags")) == "storage,quest", "flags set: names in any case, held in the schema's order");
        std::vector<Refusal> rf;
        batch(s, "a", "alter", alter("items", "game:chest", "tags", "op", R"(["fuel", "FUEL"])") + "", &rf);
        CHECK(rf.size() == 1 && rf[0].reason.find("twice") != std::string::npos, "flags: a name twice is refused");
        std::vector<Refusal> rf2;
        batch(s, "a", "alter", alter("items", "game:chest", "tags", "op", R"({"mul": 2})"), &rf2);
        CHECK(rf2.size() == 1, "flags take no relative op");
        const Result un = compose(s, b, {}, {mod("a", {batch(s, "a", "alter", alter("items", "game:chest", "tags", "list", R"({"append": ["quest", "storage"]})"))}),
                                             mod("b", {batch(s, "b", "alter", alter("items", "game:chest", "tags", "list", R"({"append": ["fuel"]})"))}),
                                             mod("c", {batch(s, "c", "alter", alter("items", "game:chest", "tags", "list", R"({"remove": ["storage"]})"))})});
        CHECK(names(result(un, "items", "game:chest", "tags")) == "fuel,quest", "flags: appends are a union (each name once), remove takes out");
        const Result cl = compose(s, b, {}, {mod("a", {batch(s, "a", "alter", alter("items", "game:chest", "tags", "list", R"("clear")"))})});
        CHECK(result(cl, "items", "game:chest", "tags").list.empty(), "\"list\": \"clear\" (the section's short form)");
    }

    // ---- a plain list's whole value through op ----
    {
        const Result r = compose(s, b, {}, {mod("a", {batch(s, "a", "alter", alter("items", "game:chest", "perks", "op", R"(["game:p1", "game:p2"])"))})});
        CHECK(names(result(r, "items", "game:chest", "perks")) == "game:p1,game:p2", "op with a list sets a list field whole");
        std::vector<Refusal> rf;
        batch(s, "a", "alter", alter("items", "game:chest", "perks", "op", R"(["game:p1", "game:p2", "game:p3", "game:p4"])"), &rf);
        CHECK(rf.size() == 1, "...within its size");
    }

    // ---- the data pack section {"add" | "alter" | "final": [edits]} ----
    {
        const json section = json::parse(R"({
            "final": [{"registry": "recipes", "select": {"id": "game:arrow"}, "field": "duration", "op": {"mul": 0.5}}],
            "add": [{"registry": "recipes", "entry": "add", "id": "pack:bolt", "like": "game:arrow"}],
            "alter": [{"registry": "recipes", "select": {"id": "pack:bolt"}, "field": "inputs[0].count", "op": 9},
                      {"registry": "recipes", "select": {"id": "game:arrow"}, "field": "nope", "op": 1}]
        })");
        std::vector<Batch> out;
        std::vector<Refusal> rf;
        std::string err;
        const bool ok = lower_section(section, "pack", "1.0.0", "content/bolts.json", s, &out, &rf, &err);
        CHECK(ok && out.size() == 3 && out[0].stage == Stage::Add && out[1].stage == Stage::Alter && out[2].stage == Stage::Final, "one batch per stage, in stage order");
        CHECK(ok && out[1].mod == "pack" && out[1].source == "content/bolts.json" && out[1].mod_version == "1.0.0" && out[1].edits.size() == 1 && rf.size() == 1,
              "the batches carry mod, source and version; a bad edit is refused alone");
        if (ok) {
            const Result r = compose(s, b, {}, {mod("pack", out)});
            CHECK(r.added.size() == 1 && counts(result(r, "recipes", "pack:bolt", "inputs")) == "9,3" && result(r, "recipes", "game:arrow", "duration").d == 0.5,
                  "the lowered section composes: add, then alter of the new entry's element, then final");
        }
        std::vector<Batch> o2;
        CHECK(!lower_section(json::parse(R"({"later": []})"), "pack", "", "c", s, &o2, &rf, &err) && err.find("later") != std::string::npos, "an unknown stage refuses the section");
        CHECK(!lower_section(json::parse(R"({"alter": {}})"), "pack", "", "c", s, &o2, &rf, &err), "a stage is a list");
        CHECK(!lower_section(json::parse(R"({"alter": []})"), "game", "", "c", s, &o2, &rf, &err), "a section of a reserved mod id is refused");
        CHECK(lower_section(json::object(), "pack", "", "c", s, &o2, &rf, &err) && o2.empty(), "an empty section is no batch");
    }

    // ---- batch_json: the plan keeps a start's edits as IR that parse_batch reads back the same (registry-rollback) ----
    {
        const std::string alters = R"([
            {"n": 40, "registry": "recipes", "select": {"id": "game:arr*", "where": {"duration": ">=1"}, "has": ["t1"], "hasNot": ["old"]},
             "field": "duration", "op": {"mul": 0.5, "add": 1, "min": 0, "max": 60, "raiseOnly": true}, "exclusive": true},
            {"n": 70, "registry": "recipes", "select": {"id": "game:arrow"}, "field": "duration", "op": 2.5, "yield": true},
            {"registry": "recipes", "select": {"id": "game:arrow"}, "field": "inputs", "list": {"append": [{"item": "game:stone", "count": 4}]}},
            {"registry": "recipes", "select": {"id": "game:arrow"}, "field": "inputs", "list": {"remove": [{"item": "game:wood"}]}},
            {"registry": "recipes", "select": {"id": "game:arrow"}, "field": "inputs", "list": {"replace": []}},
            {"registry": "recipes", "select": {"id": "game:arrow"}, "field": "inputs", "list": "clear"},
            {"registry": "recipes", "select": {"id": "game:arrow"}, "field": "inputs[1].count", "op": {"mul": 2}},
            {"registry": "recipes", "select": {"id": "game:arrow"}, "field": "inputs[*].item", "op": "game:iron"},
            {"registry": "recipes", "select": {"id": "game:arrow"}, "field": "inputs[0]", "op": {"set": {"item": "game:iron", "count": 1}}},
            {"registry": "items", "select": {"id": "game:chest"}, "field": "tags", "op": ["quest", "fuel"]},
            {"registry": "items", "select": {"id": "game:chest"}, "field": "tags", "list": {"append": ["storage"]}},
            {"registry": "items", "select": {"id": "game:chest"}, "field": "perks[1]", "op": "game:perk_a"},
            {"registry": "items", "entry": "remove", "select": {"id": "game:chest"}},
            {"registry": "balance", "select": {"id": "game:balance"}, "field": "altarsPerFlameLevel[2]", "op": {"add": 10}}])";
        const std::string adds = R"([{"n": 3, "registry": "recipes", "entry": "add", "id": "a:bolt", "like": "game:arrow",
                                      "fields": {"duration": 3, "inputs": [{"item": "game:stone", "count": 2}]}}])";
        Batch alter_b = batch(s, "a", "alter", alters), add_b = batch(s, "a", "add", adds);
        alter_b.mod_version = "1.2.3";
        CHECK(alter_b.edits.size() == 14 && add_b.edits.size() == 1, "the fixture parses whole");
        for (const Batch* b0 : {&alter_b, &add_b}) {
            const json doc = batch_json(*b0);
            Batch back;
            std::vector<Refusal> rf;
            std::string err;
            const bool ok = parse_batch(doc.dump(), s, &back, &rf, &err);
            CHECK(ok && rf.empty() && back.edits.size() == b0->edits.size(), ("batch_json reads back whole: " + err + " " + doc.dump().substr(0, 300)).c_str());
            CHECK(ok && back.mod == b0->mod && back.mod_version == b0->mod_version && back.stage == b0->stage && back.source == b0->source,
                  "mod, version, stage and source kept");
            CHECK(ok && batch_json(back) == doc, "a second trip writes the same document");
            for (size_t i = 0; ok && i < back.edits.size() && i < b0->edits.size(); ++i) {
                const Edit &x = b0->edits[i], &y = back.edits[i];
                CHECK(x.n == y.n && x.registry == y.registry && x.field == y.field && x.select.id == y.select.id && x.select.where == y.select.where &&
                          x.select.has == y.select.has && x.select.has_not == y.select.has_not && x.op.set == y.op.set && x.op.value == y.op.value &&
                          x.op.mul == y.op.mul && x.op.add == y.op.add && x.op.min == y.op.min && x.op.max == y.op.max && x.op.raise_only == y.op.raise_only &&
                          x.list == y.list && x.list_values == y.list_values && x.entry == y.entry && x.add_id == y.add_id && x.like == y.like &&
                          x.add_fields == y.add_fields && x.exclusive == y.exclusive && x.yield == y.yield && x.element.on == y.element.on,
                      ("edit " + std::to_string(i) + " is the same after the trip").c_str());
            }
        }
        const Result r1 = compose(s, b, {}, {mod("a", {alter_b})});
        Batch again;
        std::vector<Refusal> rf;
        std::string err;
        parse_batch(batch_json(alter_b).dump(), s, &again, &rf, &err);
        const Result r2 = compose(s, b, {}, {mod("a", {again})});
        CHECK(r1.fields.size() == r2.fields.size() && r1.refused.size() == r2.refused.size(), "the read-back batch composes the same");
        for (const auto& [ref, fo] : r1.fields) {
            const auto it = r2.fields.find(ref);
            CHECK(it != r2.fields.end() && it->second.result == fo.result, ("the same result for " + ref.text()).c_str());
        }
    }

    // ---- id migrations at run time (registry-rollback): a synthetic schema change, test fixture only (no real bump) ----
    // recipes 0.1.0 -> 0.2.0 renamed "time" to "craftTime", "ingredients" to "inputs", "ingredients[].amount" to "inputs[].count",
    // the entry game:old_arrow to game:arrow; 0.2.0 -> 0.3.0 renamed "craftTime" (0.2's interim name) on, and items 0.1.0 -> 0.2.0
    // renamed the entry game:stone_old to game:stone (a reference value a recipe edit names).
    {
        Schema m = s;
        RegistrySpec& rec = m.registries["recipes"];
        rec.version = "0.3.0";
        for (FieldSpec& leaf : rec.fields["inputs"].element_fields)
            if (leaf.name == "item") leaf.ref = "items";  // a reference leaf (the test schema leaves it unchecked elsewhere)
        rec.fields["inputs[].item"].ref = "items";
        rec.migrations = {{"0.1.0", "0.2.0", {{"game:old_arrow", "game:arrow"}}, {{"time", "craftTime"}, {"ingredients", "inputs"}, {"ingredients[].amount", "inputs[].count"}}},
                          {"0.2.0", "0.3.0", {}, {{"craftTime", "duration"}, {"duration", "nowhere"}}}};
        m.registries["items"].migrations = {{"0.1.0", "0.2.0", {{"game:stone_old", "game:stone"}}, {}}};
        const std::string old_edits = R"([
            {"registry": "recipes", "select": {"id": "game:old_arrow"}, "field": "time", "op": {"mul": 2}},
            {"registry": "recipes", "select": "game:old_arrow", "field": "ingredients[1].amount", "op": 5},
            {"registry": "recipes", "select": {"id": "game:arrow"}, "field": "ingredients", "list": {"append": [{"item": "game:stone_old", "count": 4}]}},
            {"registry": "recipes", "select": {"id": "game:old_*"}, "field": "craftTime", "op": {"add": 1}}])";
        Batch old_b;
        std::vector<Refusal> rf;
        std::string err;
        const bool ok = parse_batch(R"({"ir": 1, "mod": "a", "stage": "alter", "source": "content/old.json", "edits": )" + old_edits + "}", m, &old_b, &rf, &err);
        CHECK(ok && rf.empty() && old_b.edits.size() == 4, "an edit written for recipes 0.1.0 is read whole by the 0.3.0 schema");
        if (ok && old_b.edits.size() == 4) {
            CHECK(old_b.edits[0].field == "duration" && old_b.edits[0].select.id == "game:arrow", "time -> craftTime -> duration through both steps; the id too");
            CHECK(old_b.edits[1].field == "inputs[1].count" && old_b.edits[1].select.id == "game:arrow", "an element path: list and leaf renamed, the index kept");
            CHECK(old_b.edits[2].field == "inputs" && old_b.edits[2].list_values.size() == 1 && old_b.edits[2].list_values[0].leaf("item") &&
                      old_b.edits[2].list_values[0].leaf("item")->t == "game:stone", "a reference value is renamed by its own registry's step (items)");
            CHECK(old_b.edits[3].field == "duration" && old_b.edits[3].select.id == "game:old_*", "an edit written for 0.2.0 starts at its own step; a glob is never renamed");
        }
        bool said = false;
        for (const std::string& n : old_b.migrated) said = said || n.find("recipes.time -> recipes.duration (recipes schema 0.1.0 -> 0.3.0)") != std::string::npos;
        CHECK(said && old_b.migrated.size() >= 6, "each rename is noted for kiln.log and registry.json");
        // a field the running schema has is never renamed (old names are never reused, so a step naming one is a mistake)
        Batch cur;
        parse_batch(R"({"ir": 1, "mod": "a", "stage": "alter", "edits": )" + alter("recipes", "game:arrow", "duration", "op", "4") + "}", m, &cur, &rf, &err);
        CHECK(cur.edits.size() == 1 && cur.edits[0].field == "duration" && cur.migrated.empty(), "a current name stays as it is");
        // the migrated batch composes exactly as the same edits written with the 0.3.0 names
        Batch new_b;
        parse_batch(std::string(R"({"ir": 1, "mod": "a", "stage": "alter", "edits": [
            {"registry": "recipes", "select": {"id": "game:arrow"}, "field": "duration", "op": {"mul": 2}},
            {"registry": "recipes", "select": "game:arrow", "field": "inputs[1].count", "op": 5},
            {"registry": "recipes", "select": {"id": "game:arrow"}, "field": "inputs", "list": {"append": [{"item": "game:stone", "count": 4}]}}]})"), m, &new_b, &rf, &err);
        if (!old_b.edits.empty()) old_b.edits.pop_back();  // the glob edit has no counterpart below
        const Result a = compose(m, b, {}, {mod("a", {old_b})}), c = compose(m, b, {}, {mod("a", {new_b})});
        CHECK(!a.fields.empty() && a.fields.size() == c.fields.size() && a.refused.size() == c.refused.size(), "the same fields and refusals");
        for (const auto& [ref, fo] : a.fields) {
            const auto it = c.fields.find(ref);
            CHECK(it != c.fields.end() && it->second.result == fo.result, ("migrated and current edits give the same " + ref.text()).c_str());
        }
        // a data pack's section goes through the same reader
        std::vector<Batch> out;
        CHECK(lower_section(json::parse(R"({"alter": [{"registry": "recipes", "select": {"id": "game:old_arrow"}, "field": "time", "op": 3}]})"),
                            "pack", "1.0.0", "content/p.json", m, &out, &rf, &err) && out.size() == 1 && out[0].edits.size() == 1 &&
                  out[0].edits[0].field == "duration" && !out[0].migrated.empty(),
              "a data pack's registry section written for 0.1.0 is migrated too");
        // review: the rest of an edit's names go through the same steps
        {
            Schema mm = m;
            FieldSpec station = spec("station", FieldType::Id);
            station.ref = "items";
            mm.registries["recipes"].fields["station"] = station;
            Batch more;
            std::vector<Refusal> rf3;
            const bool ok3 = parse_batch(std::string(R"({"ir": 1, "mod": "a", "stage": "alter", "edits": [
                {"registry": "recipes", "select": "game:arrow", "field": "ingredients", "list": {"append": [{"item": "game:stone_old", "amount": 4}]}},
                {"registry": "recipes", "select": {"id": "game:*", "where": {"time": ">=1"}}, "field": "time", "op": {"mul": 2}},
                {"registry": "recipes", "select": "game:arrow", "field": "station", "op": {"set": "game:stone_old"}},
                {"registry": "recipes", "select": "game:arrow", "field": "inputs[0]", "op": {"set": {"item": "game:stone_old", "amount": 2}}}]})"),
                                         mm, &more, &rf3, &err);
            std::string why3;
            for (const Refusal& x : rf3) why3 += " #" + std::to_string(x.n) + " " + x.reason;
            CHECK(ok3 && rf3.empty() && more.edits.size() == 4, ("an element's leaf, a where field and a set reference are renamed too:" + why3).c_str());
            if (ok3 && more.edits.size() == 4) {
                const Value* count = more.edits[0].list_values.empty() ? nullptr : more.edits[0].list_values[0].leaf("count");
                CHECK(more.edits[0].field == "inputs" && count && count->i == 4 && more.edits[0].list_values[0].leaf("item")->t == "game:stone",
                      "an appended element written with 0.1.0's leaf name (amount) is read as inputs[].count");
                CHECK(more.edits[1].select.where.is_object() && more.edits[1].select.where.contains("duration") && !more.edits[1].select.where.contains("time"),
                      "a where condition's field (time) is read as duration");
                CHECK(more.edits[2].op.set && more.edits[2].op.value.t == "game:stone", "a reference set with {\"set\": id} is renamed like a plain one");
                const Value& el = more.edits[3].op.value;
                CHECK(el.leaf("count") && el.leaf("count")->i == 2 && el.leaf("item") && el.leaf("item")->t == "game:stone", "a whole element set with old names");
            }
            // a mod that names one field twice, by its old and its new name: refused (which value it meant is unknown)
            Batch twice;
            std::vector<Refusal> rf4;
            parse_batch(std::string(R"({"ir": 1, "mod": "a", "stage": "add", "edits": [
                {"registry": "recipes", "entry": "add", "id": "a:bolt", "like": "game:old_arrow", "fields": {"time": 3, "duration": 4}}]})"), mm, &twice, &rf4, &err);
            CHECK(twice.edits.empty() && rf4.size() == 1 && rf4[0].reason.find("names recipes.duration twice") != std::string::npos,
                  ("an add naming duration and its old name time is refused, not silently one of them: " + (rf4.empty() ? std::string("none") : rf4[0].reason)).c_str());
            Batch twice2;
            std::vector<Refusal> rf5;
            parse_batch(std::string(R"({"ir": 1, "mod": "a", "stage": "alter", "edits": [
                {"registry": "recipes", "select": "game:arrow", "field": "inputs", "list": {"append": [{"item": "game:stone", "amount": 4, "count": 5}]}}]})"),
                        mm, &twice2, &rf5, &err);
            CHECK(twice2.edits.empty() && rf5.size() == 1 && rf5[0].reason.find("twice") != std::string::npos,
                  ("an element naming count and its old name amount is refused: " + (rf5.empty() ? std::string("none") : rf5[0].reason)).c_str());
        }
        // without migrations nothing is renamed: the unknown name is refused as before
        Batch plain;
        std::vector<Refusal> rf2;
        parse_batch(R"({"ir": 1, "mod": "a", "stage": "alter", "edits": )" + alter("recipes", "game:arrow", "time", "op", "4") + "}", s, &plain, &rf2, &err);
        CHECK(plain.edits.empty() && rf2.size() == 1 && rf2[0].reason.find("no field \"time\"") != std::string::npos, "no migration: the old name is refused, with why");
    }

    // ---- api-shape (dev.32): an appended element's struct leaves nested, as the C# types and the docs show them ----
    {
        std::vector<Refusal> rf;
        const Batch nb = batch(s, "a", "alter", R"([
            {"registry": "drops", "select": "game:dt", "field": "entries", "list": {"append": [{"item": "game:x", "count": {"min": 1, "max": 3}, "chance": 0.5}]}},
            {"registry": "drops", "select": "game:dt", "field": "entries", "list": {"append": [{"item": "game:x", "count.min": 1, "count.max": 3, "chance": 0.5}]}},
            {"registry": "drops", "select": "game:dt", "field": "entries", "list": {"remove": [{"item": "game:x", "count": {"max": 3}}]}},
            {"registry": "drops", "select": "game:dt", "field": "entries[0]", "op": {"set": {"item": "game:y", "count": {"min": 2}}}}])", &rf);
        CHECK(rf.empty() && nb.edits.size() == 4, ("nested leaves parse in append, remove and a whole element's set: " + (rf.empty() ? std::string("ok") : rf[0].reason)).c_str());
        if (rf.empty() && nb.edits.size() == 4) {
            const Value& nested = nb.edits[0].list_values.at(0);
            const Value& dotted = nb.edits[1].list_values.at(0);
            CHECK(nested == dotted && nested.keys == std::vector<std::string>({"chance", "count.max", "count.min", "item"}),
                  "a nested element is the dotted one: the same leaves, keyed count.min / count.max, sorted");
            CHECK(nb.edits[2].list_values.at(0).leaf("count.max") && nb.edits[2].list_values.at(0).leaf("count.max")->i == 3, "a removal names a nested leaf");
            CHECK(nb.edits[3].op.value.leaf("count.min") && nb.edits[3].op.value.leaf("count.min")->i == 2, "a whole element's set takes nested leaves");
        }
        auto refused = [&](const char* edits, const char* why) {
            std::vector<Refusal> r;
            const Batch x = batch(s, "a", "alter", edits, &r);
            const bool ok = x.edits.empty() && r.size() == 1 && r[0].reason.find(why) != std::string::npos;
            if (!ok) std::printf("  refusal: %s\n", r.empty() ? "(none)" : r[0].reason.c_str());
            return ok;
        };
        CHECK(refused(R"([{"registry": "drops", "select": "game:dt", "field": "entries", "list": {"append": [{"item": "game:x", "count": {"min": 1}, "count.min": 2}]}}])", "twice"),
              "one leaf given nested and dotted is refused (which one it meant is unknown)");
        CHECK(refused(R"([{"registry": "drops", "select": "game:dt", "field": "entries", "list": {"append": [{"item": "game:x", "count": {"least": 1}}]}}])", "no field \"count.least\""),
              "a nested name the element does not have is refused by its dotted name");
        CHECK(refused(R"([{"registry": "drops", "select": "game:dt", "field": "entries", "list": {"append": [{"item": "game:x", "count": 2}]}}])", "no field \"count\""),
              "a struct given as a plain value is refused");
        CHECK(refused(R"([{"registry": "drops", "select": "game:dt", "field": "entries", "list": {"append": [{"item": "game:x", "count": {"min": 2000}}]}}])", "count.min: 2000"),
              "a nested leaf keeps its range");
        CHECK(refused(R"([{"registry": "recipes", "select": "game:arrow", "field": "inputs", "list": {"append": [{"item": {"id": "game:x"}, "count": 1}]}}])", "item: an id"),
              "an object where a leaf (no struct) belongs is that leaf's wrong value");
    }

    std::printf("registry-ir-test: %d passed, %d failed\n", g_pass, g_fail);
    return g_fail ? 1 : 0;
}
