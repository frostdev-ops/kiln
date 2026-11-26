// SPDX-License-Identifier: MIT
// kiln.content registry stage: the stage runner (registry/stage.cpp, slice S11) and the boot back-end's bridge
// (registry/boot_bridge.cpp, slice S10) on the host, with ASan/UBSan, over a fake tree view of the game's data and an
// in-memory data folder: a mod's data section in load order, refusals of fields this build does not map (loud, in
// registry.json and the log), an old boot patch below a registry edit of the same field (the registry wins, reported), a
// game recipe removed with the item it needs, registry.json and its phases, the id table and the plan written to the data
// folder, a second start from the cache giving the same registry.json, Kiln.Host's order, a native stage callback's edits,
// the registry's writes on a resource's bytes after the old documents.
#include <algorithm>
#include <cstdio>
#include <cstring>
#include <stdexcept>

#include "services/kiln_caps.h"
#include "../registry/boot_hooks.h"
#include "../registry/schema_ir.h"
#include "../registry/stage.h"

namespace kc {
std::string guid_text(const uint8_t g[16]);
bool guid_parse(const std::string& s, uint8_t out[16]);
namespace rbtest {
void balance_type();  // keen::BalancingTable {playerLevelCap u32 +0, skillPointsPerLevel u32 +4}, 8 bytes
}
}  // namespace kc

namespace {
using namespace kc::registry;
int checks = 0;
void check(bool ok, const std::string& why) {
    if (!ok) throw std::runtime_error(why);
    ++checks;
}
std::string g(int n) {
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
const char* kBalance = "82706b40-61b1-4b8f-8b23-dcec6971bda1";
const std::string kExe = "f0d824be7fcd48ebfe2458ff5c28da26060c2074e3ec1fcac587e5a1d508aeb2";
const std::string kClientExe = "5d9ef2f140b8ed49e30dbee655841b89d08baec20f799a1817e171a18a850c77";

void registry_caps(const std::string& dir) {
    struct Row {
        uint32_t state, reason;
        std::string detail;
    };
    using Rows = std::map<std::string, Row>;
    constexpr std::string_view name_chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._/-";
    auto run = [&](const std::string& side, const std::string& exe,
                   const std::function<void(map::RegMap&)>& adjust = {}) {
        Rows rows;
        stage::Env e;
        e.side = side;
        e.exe_sha256 = exe;
        unsigned loads = 0;
        e.regmap = [&](map::RegMap* rm, std::vector<std::string>* errs) {
            ++loads;
            const bool ok = map::load_dir(dir, side, exe, rm, errs);
            if (adjust) adjust(*rm);
            return ok;
        };
        e.report_cap = [&](const std::string& name, uint32_t state, uint32_t reason, const std::string& detail) {
            check(loads == 1, "reports use the loaded regmap");
            check(!name.empty() && name.size() <= KILN_CAP_NAME_MAX && name.find_first_not_of(name_chars) == std::string::npos,
                  "valid kiln.caps name: " + name);
            check(rows.size() < KILN_CAP_OWNER_MAX, "fits kiln.caps owner limit");
            check(rows.emplace(name, Row{state, reason, detail}).second, "row reported once: " + name);
            return KILN_OK;
        };
        stage::Runner runner(std::move(e));
        // Schema queries can load the regmap before resources-ready.
        if (side == "server" && exe == kExe) runner.schema_json();
        runner.run({});
        runner.run({});
        check(rows.size() == std::size(gen::kRegistries) + std::size(gen::kFields), "one capability per registry and field");
        return rows;
    };
    const auto server = run("server", kExe);
    for (const auto& r : gen::kRegistries)
        check(server.at(std::string("registry.") + r.name).state == KILN_CAP_AVAILABLE, "verified registry: " + std::string(r.name));
    for (const auto& f : gen::kFields) {
        std::string path = f.path;
        if (const auto pos = path.find("[]"); pos != std::string::npos) path.erase(pos, 2);
        const auto& row = server.at(std::string("registry.") + f.registry + "." + path);
        const char* applies = f.applies == gen::Applies::Now ? "now" : f.applies == gen::Applies::WorldLoad ? "worldLoad" : "restart";
        check(row.detail.find(std::string("applies: ") + applies) != std::string::npos, "schema applies in " + path + " detail");
    }
    auto expect = [&](const Rows& rows, const char* name, uint32_t reason, const char* detail) {
        const Row& row = rows.at(name);
        check(row.state == (reason == KILN_CAP_REASON_NONE ? KILN_CAP_AVAILABLE : KILN_CAP_UNAVAILABLE) &&
                  row.reason == reason && row.detail.find(detail) != std::string::npos,
              std::string(name) + " reports " + detail + ": " + row.detail);
    };
    expect(server, "registry.items.power", KILN_CAP_REASON_NONE, "applies: now");
    expect(server, "registry.items.maxStack", KILN_CAP_REASON_NONE, "applies: worldLoad");
    expect(server, "registry.balance.levelCap", KILN_CAP_REASON_NONE, "applies: restart");
    expect(server, "registry.recipes.inputs.count", KILN_CAP_REASON_NONE, "applies: now");
    expect(server, "registry.items.fuel.burnTime", KILN_CAP_REASON_SYMBOL_MISSING, "unverified");
    expect(server, "registry.recipes.unlock", KILN_CAP_REASON_WRONG_SIDE, "client");
    // dev.32: the skills and stations registries' rows, and recipes.station now that it names a stations id
    expect(server, "registry.skills", KILN_CAP_REASON_NONE, "verified");
    expect(server, "registry.skills.cost", KILN_CAP_REASON_NONE, "applies: now");
    expect(server, "registry.skills.unlocks", KILN_CAP_REASON_NONE, "applies: now");
    expect(server, "registry.stations", KILN_CAP_REASON_NONE, "verified");
    expect(server, "registry.stations.npc", KILN_CAP_REASON_NONE, "applies: restart");
    expect(server, "registry.recipes.station", KILN_CAP_REASON_NONE, "applies: restart");

    const auto client = run("client", kClientExe);
    expect(client, "registry.recipes.unlock", KILN_CAP_REASON_NONE, "applies: now");
    const auto missing_client_unlock = run("client", kClientExe, [](map::RegMap& rm) {
        rm.build.rows.erase("recipes.unlock");
    });
    expect(missing_client_unlock, "registry.recipes.unlock", KILN_CAP_REASON_SYMBOL_MISSING, "unmapped");

    const auto unknown = run("server", std::string(64, '0'));
    expect(unknown, "registry.items", KILN_CAP_REASON_POLICY, "unverified");
    expect(unknown, "registry.items.power", KILN_CAP_REASON_POLICY, "unverified");
    const auto unverified_build = run("server", kExe, [](map::RegMap& rm) { rm.build.verified = false; });
    expect(unverified_build, "registry.items.power", KILN_CAP_REASON_POLICY, "unverified");
    // review3 S4: a record proven on another regmap/common enables nothing and is not called a verified one
    const auto other_mapping = run("server", kExe, [](map::RegMap& rm) { rm.build.regmap_digest = std::string(64, 'b'); });
    expect(other_mapping, "registry.items.power", KILN_CAP_REASON_POLICY, "unverified regmap record");
    expect(other_mapping, "registry.items", KILN_CAP_REASON_POLICY, "unverified regmap record");
    const auto incomplete = run("server", kExe, [](map::RegMap& rm) {
        rm.build.rows["items.power"] = map::BuildRow::Changed;
        rm.registries.erase("balance");
    });
    expect(incomplete, "registry.items.power", KILN_CAP_REASON_SYMBOL_MISSING, "unmapped");
    expect(incomplete, "registry.items", KILN_CAP_REASON_NONE, "verified");
    expect(incomplete, "registry.balance", KILN_CAP_REASON_SYMBOL_MISSING, "unmapped");
    expect(incomplete, "registry.balance.levelCap", KILN_CAP_REASON_SYMBOL_MISSING, "unmapped");
}

struct TreeView final : kiln::boot_view::BaseView {
    std::map<std::string, json> values;
    std::map<std::string, std::vector<std::string>> types;
    std::set<std::string> served;
    bool released = false;
    const char* source() const override { return "live"; }
    bool ready(std::string*) override { return true; }
    std::string digest() override {
        std::string d;
        for (const std::string& s : served) d += s + values[s].dump();
        return kiln::boot_gd::hex_of(kiln::boot_gd::sha256(d));
    }
    KilnResult tree(std::string_view q, size_t cap, size_t, uint64_t, std::string* out, size_t* req, size_t*, std::string* why) override {
        const std::string guid(q.substr(9, 36));
        const auto it = values.find(guid);
        if (it == values.end()) return *why = "none", KILN_ERR_NOT_FOUND;
        const std::string text = json{{"value", it->second}}.dump();
        if (text.size() + 1 > cap) return *req = text.size() + 1, KILN_ERR_BUFFER_TOO_SMALL;
        served.insert(guid);
        *out = text;
        return KILN_OK;
    }
    KilnResult bytes(const KilnAssetKey&, size_t, std::string*, std::string* why) override { return *why = "trees", KILN_ERR_UNAVAILABLE; }
    void release() override { released = true; }
    void add(const std::string& type, const std::string& guid, json v) {
        types[type].push_back(guid);
        values[guid] = std::move(v);
    }
};

TreeView world() {
    TreeView v;
    auto item = [&](int n, const std::string& name, int stack) {
        v.add("keen::ItemInfo", g(n), {{"itemId", 1000 + n}, {"objectId", g(n)}, {"debugName", name}, {"maxStackSize", stack}, {"rarity", "Common"},
                                       {"damageSetup", {{"dmgMod", 0.0}, {"speedMod", 1.0}, {"isSet", false}}}, {"itemLevelRange", {{"minLevel", 1}, {"maxLevel", 1}}}});
    };
    item(1, "Material_T1_Wood_Logs", 10);
    item(2, "Plank", 20);
    json in = {{"itemStack", {{"item", 1001}, {"itemRef", g(1)}, {"count", 3}}}, {"inputItemCategory", {{"category", 0}, {"categoryRef", "none"}, {"count", 0}}}};
    v.add("keen::RecipeRegistryResource", g(100),
          {{"recipes", json::array({{{"debugName", "Craft_Planks"}, {"recipeGuid", g(101)}, {"recipeId", 5001}, {"input", json::array({in})},
                                     {"output", json::array({{{"item", 1002}, {"itemRef", g(2)}, {"count", 1}}})}, {"craftingDuration", 2.0}}})},
           {"inputCategories", json::array()}});
    v.add("keen::LootableItemsResource", g(200), {{"items", json::array({{{"itemId", 1002}, {"stackSizeMin", 1}, {"stackSizeMax", 2}}})}});
    v.add("keen::ecs::DefaultInventoryResource", g(300), {{"rootGroup", {{"stacks", json::array({{{"item", 1002}, {"itemRef", g(2)}, {"countMin", 1}, {"countMax", 1}, {"spawnProbability", 1.0}, {"weight", 1.0}}})}}}});
    v.add("keen::ecs::TemplateResource", g(400), {{"name", "Chest_A"}, {"components", json::array({{{"$type", "DefaultInventory"}, {"defaultInventory", g(300)}}})}});
    v.add("keen::BalancingTable", kBalance, {{"playerLevelCap", 50}, {"skillPointsPerLevel", 2}, {"genericWeaponSeed", 10.0}, {"powerCreepWeapon", 0.15},
                                             {"armorSetup", {{"offset", 23.0}, {"gradient", 15.0}}}, {"durabilityStart", 100.0},
                                             {"buildzoneSizesPerAltarLevel", json::array({{{"x", 40.0}, {"y", 40.0}, {"z", 40.0}},
                                                                                          {{"x", 60.0}, {"y", 60.0}, {"z", 60.0}},
                                                                                          {{"x", 80.0}, {"y", 80.0}, {"z", 80.0}}})}});
    return v;
}

struct Files {
    std::map<std::string, std::string> data;
    std::string report;
    std::vector<std::string> log;
    std::map<std::string, int64_t> times;  // a data file's last write (a clock of this fake: later writes are larger)
    int64_t clock = 1000;
    std::vector<std::string> removed;     // data files the stage removed, in order
};

stage::Env env_of(TreeView* view, Files* files, const std::string& regmap_dir, std::vector<stage::Env::DataFile> (*data)(const Schema&)) {
    stage::Env e;
    e.side = "server", e.exe_sha256 = kExe, e.kiln_version = "0.2.0-test", e.pid = 7;
    e.now = [] { return std::string("2026-11-21T00:00:00Z"); };
    e.source = [view](std::string*) {
        return model::tree_source(*view, [view](const std::string& type, std::vector<KilnAssetKey>* out, std::string*) {
            for (const std::string& gu : view->types[type]) out->push_back(key_of(gu, type));
            return true;
        });
    };
    e.regmap = [regmap_dir](map::RegMap* out, std::vector<std::string>* errs) { return map::load_dir(regmap_dir, "server", kExe, out, errs); };
    e.data = data;
    e.folders = [] { return std::vector<std::pair<std::string, std::string>>{{"oldmod", "1.0.0"}, {"bigmod", "2.0.0"}, {"late", "1.0.0"}}; };
    e.write_report = [files](const std::string& t) { return files->report = t, true; };
    e.write_data = [files](const std::string& rel, const std::string& t) { return files->data[rel] = t, files->times[rel] = ++files->clock, true; };
    e.read_data = [files](const std::string& rel) -> std::optional<std::string> {
        const auto it = files->data.find(rel);
        if (it == files->data.end()) return std::nullopt;
        return it->second;
    };
    e.list_data = [files](const std::string& dir) {
        std::vector<stage::Env::DataEntry> out;
        const std::string prefix = dir + "/";
        for (const auto& [rel, text] : files->data)
            if (rel.rfind(prefix, 0) == 0 && rel.find('/', prefix.size()) == std::string::npos)
                out.push_back({rel.substr(prefix.size()), files->times.count(rel) ? files->times.at(rel) : 0});
        return out;
    };
    e.remove_data = [files](const std::string& rel) {
        files->removed.push_back(rel);
        files->times.erase(rel);
        return files->data.erase(rel) == 1;
    };
    e.log = [files](int level, const std::string& l) { files->log.push_back(std::to_string(level) + " " + l); };
    return e;
}

std::vector<stage::Env::DataFile> sections(const Schema& schema) {
    const std::string big = R"({"registry": {"alter": [
        {"registry": "items", "select": {"id": "game:plank"}, "field": "maxStack", "op": {"mul": 4}},
        {"registry": "items", "select": {"id": "game:plank"}, "field": "fuel.burnTime", "op": 30},
        {"registry": "balance", "select": {"id": "game:balance"}, "field": "levelCap", "op": 60},
        {"registry": "items", "entry": "remove", "select": {"id": "game:material_t1_wood_logs"}}]}})";
    const std::string late = R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:plank"}, "field": "maxStack", "op": {"mul": 2}}]}})";
    auto out = stage::read_sections("bigmod", "2.0.0", {{"content/b.json", big}, {"content/a.json", "{\"items\": {}}"}}, schema);
    auto l = stage::read_sections("late", "1.0.0", {{"content/x.json", late}}, schema);
    out.insert(out.end(), l.begin(), l.end());
    return out;
}

std::vector<boothook::OldDoc> old_patch() {
    boothook::OldDoc d;
    d.mod = "oldmod", d.key = "cap";
    d.res = key_of(kBalance, "keen::BalancingTable");
    d.type = "keen::BalancingTable";
    d.sets = {{"playerLevelCap", 55}, {"skillPointsPerLevel", {{"mul", 2}}}};
    return {d};
}

void boot_sync(const std::string& dir) {
    // One start of mods a (levelCap) and b (b_field, "" = no mod b) with the join's registry part it publishes; null: none.
    // b_side: "" = both; "order:<side>" = Kiln.Host's set_order names it; "file:<side>" = only its kiln.mod.json (the data file) does.
    struct Opt {
        int cap = 60, points = 3;
        bool reverse = false, needs = true;
        std::string b_side, b_field = "skillPointsPerLevel";
        std::string refused;          // the boot write this game refuses: "levelCap", "skillPointsPerLevel", "*" (both)
        bool cap_unmapped = false;    // this game build's record does not map balance.levelCap
        bool code_fault = false;      // a side-both mod's native stage callback faults
        bool no_base = false;         // the stage cannot read the game's base
        std::string unwritten;        // a boot field no write outcome reaches (the bridge never saw it)
    };
    auto run = [&](const Opt& o) -> json {
        TreeView view = world();
        Files files;
        auto e = env_of(&view, &files, dir, nullptr);
        e.data = [=](const Schema& schema) {
            auto a = stage::read_sections("a", "1", {{"content/a.json", json{{"registry", {{"alter", json::array({
                {{"registry", "balance"}, {"select", "game:balance"}, {"field", "levelCap"}, {"op", o.cap}}
            })}}}}.dump()}}, schema);
            if (o.b_field.empty()) return a;
            auto b = stage::read_sections("b", "1", {{"content/b.json", json{{"registry", {{"alter", json::array({
                {{"registry", "balance"}, {"select", "game:balance"}, {"field", o.b_field}, {"op", o.points}}
            })}}}}.dump()}}, schema);
            if (o.b_side.rfind("file:", 0) == 0)
                for (auto& f : b) f.side = o.b_side.substr(5);
            a.insert(a.end(), b.begin(), b.end());
            return a;
        };
        auto mapping = e.regmap;
        e.regmap = [=](map::RegMap* rm, std::vector<std::string>* errors) {
            bool ok = mapping(rm, errors);
            rm->registries.at("balance").rows.at("skillPointsPerLevel").client_needs = o.needs;
            if (o.cap_unmapped) rm->build.rows["balance.levelCap"] = map::BuildRow::Changed;
            return ok;
        };
        if (o.no_base) e.source = [](std::string* why) -> std::unique_ptr<model::Source> { *why = "test: no base"; return nullptr; };
        stage::Runner r(std::move(e));
        const std::string bj = o.b_side.rfind("order:", 0) == 0 ? R"({"mod":"b","side":")" + o.b_side.substr(6) + R"("})" : R"({"mod":"b"})";
        if (o.b_field.empty()) r.set_order(R"([{"mod":"a"}])");
        else r.set_order(o.reverse ? "[" + bj + R"(,{"mod":"a"}])" : R"([{"mod":"a"},)" + bj + "]");
        uint64_t h = 0;
        if (o.code_fault) r.subscribe_stage(9, "c", [](Stage s, const View&) { return s != Stage::Alter; }, &h);
        auto boot = r.run({});
        std::vector<stage::BootOutcome> outcomes;
        const bool b_last = !o.reverse;  // the later writer of levelCap when both write it
        for (const auto& resource : boot)
            for (const auto& f : resource.fields) {
                const bool cap = f.field.field == "levelCap";
                const int value = cap && o.b_field == "levelCap" ? (b_last ? o.points : o.cap) : cap ? o.cap : o.points;
                const bool refused = o.refused == "*" || o.refused == f.field.field;
                if (o.unwritten == f.field.field) continue;
                outcomes.push_back({f.field, !refused, Value::integer(value), "test refusal"});
            }
        r.finish(outcomes);
        auto report = json::parse(files.report);
        return report.contains("bootSync") ? report["bootSync"] : json();
    };
    auto with = [](std::function<void(Opt&)> f) { Opt o; f(o); return o; };
    const auto base = run({});
    check(base.is_object() && base["values"].is_string() && base["values"].get<std::string>().size() == 64, "full resolved values hash in registry.json");
    check(base == run({}), "deterministic across starts");
    check(base["values"] != run(with([](Opt& o) { o.cap = 61; }))["values"], "resolved boot value changes hash");
    auto reverse = run(with([](Opt& o) { o.reverse = true; }));
    check(base["values"] == reverse["values"] && base["order"] != reverse["order"], "same values, other registry order detected");
    check(run(with([](Opt& o) { o.needs = false; })) == run(with([](Opt& o) { o.needs = false; o.points = 9; })), "server-only values and writers excluded");
    // a side server/client mod runs on one side only (kiln.boot leaves such mods out too): the fields it writes take no part
    check(base["values"] != run(with([](Opt& o) { o.points = 9; }))["values"], "a side-both mod's client-needed value takes part");
    for (const char* side : {"order:server", "order:client", "file:server"})
        check(run(with([&](Opt& o) { o.b_side = side; })) == run(with([&](Opt& o) { o.b_side = side; o.points = 9; })),
              std::string("a one-side mod's own field takes no part (") + side + ")");
    check(run(with([](Opt& o) { o.b_side = "order:both"; })) == base, "side both in the order is the default");
    // The part is one hash over both sides' fields, so a field only one side leaves out makes every join differ. A server whose side
    // server mod b writes nothing a shared mod edits equals a client that never runs b...
    const auto shared_only = run(with([](Opt& o) { o.b_field = ""; }));
    check(shared_only.is_object() && run(with([](Opt& o) { o.b_side = "order:server"; })) == shared_only,
          "a server with a one-side mod on its own field sends what a client without that mod sends");
    // ...but one whose one-side mod also writes a field a shared mod edits has no fair counterpart: no part, never a difference
    for (const char* side : {"order:server", "order:client", "file:server"})
        check(run(with([&](Opt& o) { o.b_side = side; o.b_field = "levelCap"; o.points = 70; })).is_null(),
              std::string("a one-side mod on a shared mod's client-held field: no registry part (") + side + ")");
    check(run(with([](Opt& o) { o.b_side = "order:server"; o.b_field = "levelCap"; o.points = 70; o.reverse = true; })).is_null(),
          "the same with the one-side mod first");
    // a shared mod's client-held edit this side could not apply (the other side may have): no part either
    check(run(with([](Opt& o) { o.refused = "levelCap"; })).is_null(), "a client-held boot write this game refused: no registry part");
    check(run(with([](Opt& o) { o.refused = "*"; })).is_null(), "every boot write refused: no registry part");
    check(run(with([](Opt& o) { o.cap_unmapped = true; })).is_null(), "a client-held edit this build's mapping refused: no registry part");
    check(run(with([](Opt& o) { o.code_fault = true; })).is_null(), "a shared mod's stage callback faulted (its edits unknown): no registry part");
    check(run(with([](Opt& o) { o.no_base = true; })).is_null(), "a stage that could not read the base: no registry part");
    check(run(with([](Opt& o) { o.unwritten = "levelCap"; })).is_null(), "a shared mod's client-held boot field no write reached: no registry part");
    check(run(with([](Opt& o) { o.needs = false; o.unwritten = "skillPointsPerLevel"; })) == run(with([](Opt& o) { o.needs = false; })),
          "a server-only boot field no write reached does not stop the part");
    check(run(with([](Opt& o) { o.needs = false; o.refused = "skillPointsPerLevel"; })) == run(with([](Opt& o) { o.needs = false; })),
          "a refused server-only write does not stop the part");
    check(run(with([](Opt& o) { o.b_side = "order:server"; o.refused = "skillPointsPerLevel"; })) == shared_only,
          "a one-side mod's refused write does not stop the part");
}

void one_start(const std::string& dir) {
    TreeView view = world();
    Files files;
    stage::Runner r(env_of(&view, &files, dir, &sections));
    std::function<void(const Result&, bool)> live_report;
    const Result* live_result = nullptr;
    r.set_live([&](const stage::LiveHandoff& h) { live_report = h.report, live_result = h.result; });
    check(r.phase() == stage::Phase::None, "phase none before the stage");
    const auto docs = old_patch();
    const auto boot = r.run([&](model::Model& m) { return boothook::old_writes(docs, m); });
    check(boot.size() == 1 && boot[0].type == "keen::BalancingTable" && boot[0].fields.size() == 1 && boot[0].fields[0].field.field == "levelCap",
          "the boot back-end gets the balance table's levelCap (restart) only");
    check(view.released, "the base view is released once the base is decoded");
    r.finish({{boot[0].fields[0].field, true, Value::integer(60), ""}});
    check(r.phase() == stage::Phase::Start && r.generation() == 1, "phase start, generation 1");
    const json rep = json::parse(files.report);
    check(json::parse(r.schema_json())["bootSync"] == rep["bootSync"], "small native schema query carries the fingerprint without report-size limits");
    check(json::parse(r.schema_json())["registries"]["items"]["fields"]["maxStack"]["joinedClient"] == "worldLoad", "native schema exposes the joined-client marker");
    check(rep["phase"] == "start" && rep["orderSource"] == "folders" && rep["regmap"]["verified"] == true, "registry.json: phase, order source, the verified record");
    check(rep["order"].size() == 2 && rep["order"][0]["mod"] == "bigmod" && rep["order"][1]["mod"] == "late", "the order: the mod folders' (late after bigmod)");
    const std::string text = rep.dump();  // compact: the file itself is indented
    check(text.find("\"field\":\"maxStack\"") != std::string::npos && text.find("\"result\":160") != std::string::npos, "maxStack: x4 then x2 stack (20 -> 160)");
    check(text.find("fuel.burnTime") != std::string::npos && text.find("Unproven") != std::string::npos, "an unproven field's edit is refused with its reason: " + text.substr(0, 400));
    bool loud = false;
    for (const std::string& l : files.log) loud = loud || (l.rfind("1 ", 0) == 0 && l.find("fuel.burnTime") != std::string::npos);
    check(loud, "and said in the log as a warning");
    // review finding 1 (ruling 2026-11-21): entry add and remove have no back-end in this release: refused per edit, loud, and
    // nothing is removed with them (a game recipe needing the item stays)
    check(text.find("entry add and remove have no back-end in this release") != std::string::npos, "the entry remove is refused with its reason");
    check(rep["removed"].empty() && text.find("recipes game:craft_planks removed") == std::string::npos, "nothing is removed: " + rep["removed"].dump());
    bool warned = false;
    for (const std::string& l : files.log) warned = warned || (l.rfind("1 ", 0) == 0 && l.find("no back-end in this release") != std::string::npos);
    check(warned, "the refused entry edit is a warning in kiln.log");
    bool no_adds = false;
    for (const auto& [k, v] : files.data)
        if (k.rfind("plans/", 0) == 0) no_adds = json::parse(v)["adds"].empty();
    check(no_adds, "the plan adds no entry");
    check(text.find("\"oldPatch\"") != std::string::npos && text.find("oldmod/cap") != std::string::npos, "the old boot patch is a writer of levelCap below the registry");
    bool over = false;
    for (const auto& c : rep["conflicts"]) over = over || (c["kind"] == "overridden" && c["winner"] == "bigmod" && c["field"].get<std::string>().find("levelCap") != std::string::npos);
    check(over, "the registry's set over the old patch wins, reported as overridden: " + rep["conflicts"].dump());
    check(text.find("\"result\":60") != std::string::npos, "levelCap 60 in the report");
    check(files.data.count("ids-server-" + kExe + ".json") && json::parse(files.data["ids-server-" + kExe + ".json"])["registries"]["items"][g(2)] == "game:plank",
          "the id table in the data folder");
    bool plan = false;
    for (const auto& [k, v] : files.data) plan = plan || k.rfind("plans/", 0) == 0;
    check(plan && rep["plan"].is_object(), "the plan is written and named in registry.json");
    std::string out;
    check(r.read(2, "balance", "game:balance", "levelCap", &out) == KILN_OK && out == "60", "read resolved");
    check(r.read(0, "items", "game:plank", "maxStack", &out) == KILN_OK && out == "20", "read base");
    check(r.select(2, "items", "\"game:*\"", &out) == KILN_OK && out == "[\"game:material_t1_wood_logs\",\"game:plank\"]", "select resolved: the item whose removal was refused is still there: " + out);
    uint32_t a = 0, b = 0;
    std::string rp;
    check(r.submit(1, "x", R"({"ir":1,"mod":"x","stage":"alter","edits":[]})", &a, &b, &rp) == KILN_ERR_UNAVAILABLE, "no submit after the stage");
    check(live_report != nullptr, "the live back-end got its handoff with a report callback");
    const std::string start_report = files.report;
    live_report(*live_result, true);
    check(json::parse(files.report)["bootSync"] == rep["bootSync"], "runtime report preserves the immutable boot fingerprint");
    check(r.phase() == stage::Phase::WorldLoad && r.generation() == 2 && json::parse(files.report)["phase"] == "worldLoad",
          "the live back-end's report: phase worldLoad, generation 2");

    // a second start: the cache gives the same base and the same registry.json
    TreeView view2 = world();
    Files files2;
    files2.data = files.data;
    stage::Runner r2(env_of(&view2, &files2, dir, &sections));
    const auto boot2 = r2.run([&](model::Model& m) { return boothook::old_writes(docs, m); });
    r2.finish({{boot2.at(0).fields.at(0).field, true, Value::integer(60), ""}});
    bool hit = false;
    for (const std::string& l : files2.log) hit = hit || l.find("the cache holds") != std::string::npos;
    check(hit, "the second start takes its base from the cache");
    check(files2.report == start_report, "and writes the same registry.json");
}

void host_order_and_code(const std::string& dir) {
    TreeView view = world();
    Files files;
    stage::Runner r(env_of(&view, &files, dir, &sections));
    check(r.set_order(R"([{"mod":"late","version":"1.0.0"},{"mod":"bigmod","version":"2.0.0"}])") == KILN_OK, "set_order");
    check(r.set_order("[{\"mod\":\"a\"},{\"mod\":\"a\"}]") == KILN_ERR_INVALID_ARGUMENT, "a mod twice is refused");
    uint64_t h = 0;
    std::string seen;
    check(r.subscribe_stage(9, "codemod", [&](Stage s, const View& v) {
              if (s != Stage::Alter) return true;
              std::string out;
              r.read(1, "items", "game:plank", "maxStack", &out);
              seen = out;
              uint32_t a = 0, b = 0;
              std::string rep;
              r.submit(9, "codemod", R"({"ir":1,"mod":"codemod","stage":"alter","edits":[{"registry":"items","select":{"id":"game:plank"},"field":"maxStack","op":{"add":1}}]})", &a, &b, &rep);
              (void)v;
              return true;
          }, &h) == KILN_OK,
          "a native stage callback");
    uint32_t a = 0, b = 0;
    std::string rep;
    check(r.submit(9, "codemod", R"({"ir":1,"mod":"codemod","stage":"final","edits":[{"registry":"items","select":{"id":"game:plank"},"field":"maxStack","op":{"add":100}}]})", &a, &b, &rep) == KILN_OK && a == 1,
          "a batch submitted before the stage is queued");
    check(r.submit(9, "codemod", R"({"ir":1,"mod":"other","stage":"alter","edits":[]})", &a, &b, &rep) == KILN_ERR_INVALID_ARGUMENT, "a batch naming another mod's id is refused");
    r.run({});
    r.finish({});
    const json rep2 = json::parse(files.report);
    check(rep2["orderSource"] == "kiln.host" && rep2["order"][0]["mod"] == "late" && rep2["order"][1]["mod"] == "bigmod" && rep2["order"][2]["mod"] == "codemod",
          "Kiln.Host's order; a mod it does not name runs after: " + rep2["order"].dump());
    check(seen == "20", "the callback's stage view in alter: the previous stage's resolved view (add: the base 20), never the alters running: " + seen);
    check(rep2.dump().find("\"result\":261") != std::string::npos, "its alter (+1) and its queued final (+100) compose on them: 261");
}

std::vector<stage::Env::DataFile> no_sections(const Schema&) { return {}; }

void id_table_without_content(const std::string& dir) {
    TreeView view = world();
    Files files;
    stage::Runner r(env_of(&view, &files, dir, &no_sections));
    r.run({});
    r.finish({});
    const std::string rel = "ids-server-" + kExe + ".json";
    check(files.data.count(rel) && json::parse(files.data[rel])["registries"]["items"][g(2)] == "game:plank" && !view.served.empty(),
          "no registry content, no id table for this build yet: the id table is written");
    check(json::parse(files.report)["fields"].empty(), "and registry.json holds nothing");
    TreeView view2 = world();
    Files files2;
    files2.data = files.data;
    // proof-2 N7: the id table (1.9 MB on a real build) is only looked up, never read whole, to learn that it exists
    stage::Env e2 = env_of(&view2, &files2, dir, &no_sections);
    size_t id_reads = 0, id_lookups = 0;
    auto read = e2.read_data;
    e2.read_data = [read, rel, &id_reads](const std::string& x) { if (x == rel) ++id_reads; return read(x); };
    e2.data_exists = [&files2, rel, &id_lookups](const std::string& x) { if (x == rel) ++id_lookups; return files2.data.count(x) != 0; };
    stage::Runner r2(std::move(e2));
    r2.run({});
    r2.finish({});
    check(view2.served.empty(), "the next start with no registry content reads nothing");
    check(id_lookups == 1 && id_reads == 0, "the id table's existence is one lookup, nothing read: " + std::to_string(id_lookups) + " lookups, " +
                                                std::to_string(id_reads) + " reads");
    // without data_exists (an older host env) the read stays the fallback
    TreeView view3 = world();
    Files files3;
    files3.data = files.data;
    stage::Runner r3(env_of(&view3, &files3, dir, &no_sections));
    r3.run({});
    r3.finish({});
    check(view3.served.empty(), "without data_exists the read decides, as before");
}

void bridge_write() {
    kc::rbtest::balance_type();
    std::vector<uint8_t> bytes(8, 0);
    const uint32_t cap = 55, sp = 4;
    std::memcpy(bytes.data(), &cap, 4);
    std::memcpy(bytes.data() + 4, &sp, 4);
    const Schema schema = generated_schema();
    map::RegistryMap reg;
    reg.registry = "balance";
    map::Row row;
    row.field = "levelCap", row.paths = {"playerLevelCap"}, row.conv = "int", row.backend = map::Backend::Boot;
    reg.rows["levelCap"] = row;
    stage::BootResource rb;
    rb.key = key_of(kBalance, "keen::BalancingTable");
    rb.type = "keen::BalancingTable";
    ChainRow add;
    add.kind = WriterKind::Registry, add.mod = "bigmod";
    add.op.add = 5;
    rb.fields.push_back({{"balance", "game:balance", "levelCap"}, &reg, &reg.rows["levelCap"], *schema.registry("balance")->field("levelCap"), {add}});
    std::vector<stage::BootOutcome> out;
    std::vector<std::string> notes;
    const size_t n = boothook::write(rb.key, rb.type, &bytes, rb, &out, &notes);
    uint32_t now = 0;
    std::memcpy(&now, bytes.data(), 4);
    check(n == 1 && now == 60 && out.size() == 1 && out[0].ok && out[0].written == Value::integer(60),
          "the registry's +5 over what the old documents left (55) is written: 60");
    row.paths = {"noSuchMember"};
    reg.rows["levelCap"] = row;
    out.clear();
    check(boothook::write(rb.key, rb.type, &bytes, rb, &out, &notes) == 0 && out.size() == 1 && !out[0].ok && !out[0].why.empty(), "a member that is not there: not written, with why: " + (out.empty() ? "" : out[0].why));
}
// ---- registry-rollback: the plan keeps its edits and values, the last good plan is "previous", a rollback starts on it ----
std::string g_stacks = R"({"registry": {"alter": [
    {"registry": "items", "select": {"id": "game:plank"}, "field": "maxStack", "op": {"mul": 4}},
    {"registry": "balance", "select": {"id": "game:balance"}, "field": "levelCap", "op": 60}]}})";
std::vector<stage::Env::DataFile> stacks(const Schema& schema) { return stage::read_sections("bigmod", "2.0.0", {{"content/b.json", g_stacks}}, schema); }
const char* kGood = R"({"registry": {"alter": [
    {"registry": "items", "select": {"id": "game:plank"}, "field": "maxStack", "op": {"mul": 4}},
    {"registry": "balance", "select": {"id": "game:balance"}, "field": "levelCap", "op": 60}]}})";
const char* kChanged = R"({"registry": {"alter": [
    {"registry": "items", "select": {"id": "game:plank"}, "field": "maxStack", "op": {"mul": 8}},
    {"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "maxStack", "op": 30},
    {"registry": "balance", "select": {"id": "game:balance"}, "field": "levelCap", "op": 60}]}})";

struct PlanStart {
    json report;
    std::vector<std::string> log;
    std::string rollback;  // the live handoff's rolled-back plan
    std::function<void(const Result&, bool)> world;
    Result result;
    bool callback_ran = false;
};
// One start over files; set_order with the given versions; a world load reported with (or without) a refusal.
// The stack guard (stage::Env::stack_guard): available unless a test says not, counting its calls.
bool g_guard_available = true;
int g_guard_calls = 0;
PlanStart plan_start(const std::string& dir, Files* files, TreeView* view, const std::string& order = R"([{"mod":"bigmod","version":"2.0.0"}])",
                     bool world_refusal = false, bool with_callback = false) {
    PlanStart out;
    stage::Env env = env_of(view, files, dir, &stacks);
    env.stack_guard = [](std::string* why) {
        ++g_guard_calls;
        if (!g_guard_available) *why = "not on this build";
        return g_guard_available;
    };
    stage::Runner r(std::move(env));
    if (!order.empty()) check(r.set_order(order) == KILN_OK, "set_order");
    uint64_t h = 0;
    if (with_callback)
        r.subscribe_stage(5, "codemod", [&](Stage, const View&) { out.callback_ran = true; return true; }, &h);
    const Result* live_result = nullptr;
    r.set_live([&](const stage::LiveHandoff& hd) { out.world = hd.report, live_result = hd.result, out.rollback = hd.rollback; });
    files->log.clear();
    const auto boot = r.run({});
    std::vector<stage::BootOutcome> outcomes;
    for (const auto& b : boot)
        for (const auto& f : b.fields) outcomes.push_back({f.field, true, f.rows.empty() ? Value::integer(60) : f.rows.back().after, ""});  // as composed
    r.finish(outcomes);
    out.report = json::parse(files->report);
    if (out.world && live_result) {
        Result world = *live_result;
        if (world_refusal) world.refused.push_back({"bigmod", "content/b.json", Stage::Alter, 9, "a live refusal in this world", ""});
        out.world(world, true);
        out.result = world;
    }
    out.log = files->log;
    return out;
}
std::string plan_hash(PlanStart s) { return s.report["plan"].is_object() && s.report["plan"]["hash"].is_string() ? s.report["plan"]["hash"].get<std::string>() : ""; }
json field_row(json rep, const std::string& id, const std::string& field) {
    for (const auto& f : rep["fields"])
        if (f.value("id", "") == id && f.value("field", "") == field) return f;
    return json::object();  // result: null
}
bool logged(const std::vector<std::string>& log, const std::string& what) {
    for (const std::string& l : log)
        if (l.find(what) != std::string::npos) return true;
    return false;
}

void plans_link_the_last_good_plan(const std::string& dir) {
    Files files;
    TreeView v1 = world();
    g_stacks = kGood;
    PlanStart a = plan_start(dir, &files, &v1);
    const std::string ha = plan_hash(a);
    check(!ha.empty() && files.data.count("plans/" + ha + ".json"), "the first start writes its plan");
    json pa = json::parse(files.data["plans/" + ha + ".json"]);
    check(pa["format"] == 2 && pa["inputs"].is_array() && pa["inputs"].size() == 1 && pa["inputs"][0]["mod"] == "bigmod" &&
              pa["inputs"][0]["version"] == "2.0.0" && pa["inputs"][0]["batches"].size() == 1 && pa["inputs"][0]["batches"][0]["ir"] == 1,
          "the plan keeps the lowered edits of each mod (format 2): " + pa.dump().substr(0, 400));
    check(pa["values"]["items[game:plank].maxStack"] == 80 && pa["values"]["balance[game:balance].levelCap"] == 60,
          "the plan keeps the resolved values, restart fields as written: " + pa["values"].dump());
    check(a.report["plan"]["previous"].is_null(), "no earlier good plan: previous is null");
    check(files.data.count("plans/index.json") && json::parse(files.data["plans/index.json"]).value("good", json::object()).value("plan", "") == ha,
          "a world started on it without refusals: it is the last good plan");
    // the same mods again: the same plan; previous stays empty (there is no other good plan)
    TreeView v2 = world();
    PlanStart a2 = plan_start(dir, &files, &v2);
    check(plan_hash(a2) == ha && a2.report["plan"]["previous"].is_null(), "the same edits give the same plan, and it is not its own previous");
    // changed settings: a new plan, linked to the last good one
    g_stacks = kChanged;
    TreeView v3 = world();
    PlanStart b = plan_start(dir, &files, &v3);
    const std::string hb = plan_hash(b);
    check(!hb.empty() && hb != ha && b.report["plan"]["previous"] == ha, "a changed plan names the last good plan as previous: " + b.report["plan"].dump());
    json idx = json::parse(files.data["plans/index.json"]);
    check(idx["good"]["plan"] == hb && idx["good"]["previous"] == ha && idx["world"]["plan"] == hb, "index: the new good plan and the one before it: " + idx.dump());
    // the same changed plan again: previous is still the one before it
    TreeView v4 = world();
    PlanStart b2 = plan_start(dir, &files, &v4);
    check(plan_hash(b2) == hb && b2.report["plan"]["previous"] == ha, "restarting on the good plan keeps the earlier one as previous");
    // a world with a refusal is not good: previous does not move to it
    g_stacks = R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:plank"}, "field": "maxStack", "op": 21}]}})";
    TreeView v5 = world();
    PlanStart c = plan_start(dir, &files, &v5, R"([{"mod":"bigmod","version":"2.0.0"}])", true);
    json idx2 = json::parse(files.data["plans/index.json"]);
    check(c.report["plan"]["previous"] == hb && idx2["good"]["plan"] == hb && idx2["world"]["plan"] == plan_hash(c),
          "a world with a refusal is not a good plan (the last good one stays): " + idx2.dump());
    // a refused edit at the stage also keeps the plan from being good (fuel.burnTime: no game code reads it on this build, so it stays unproven)
    g_stacks = R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:plank"}, "field": "fuel.burnTime", "op": 0.5}]}})";
    TreeView v6 = world();
    plan_start(dir, &files, &v6);
    check(json::parse(files.data["plans/index.json"]).value("good", json::object()).value("plan", "") == hb, "a start with a refused edit is not a good plan");
}

void rollback_starts_on_the_previous_plan(const std::string& dir) {
    Files files;
    g_stacks = kGood;
    TreeView v1 = world();
    const std::string ha = plan_hash(plan_start(dir, &files, &v1));
    g_stacks = kChanged;
    TreeView v2 = world();
    PlanStart b = plan_start(dir, &files, &v2);
    const std::string hb = plan_hash(b);
    check(field_row(b.report, "game:plank", "maxStack")["result"] == 160, "the changed plan: plank 20 x8 = 160");
    files.data["rollback.json"] = json{{"format", 1}, {"plan", ha}, {"from", hb}, {"requested", "2026-11-25T20:00:00Z"}, {"by", "test"}}.dump();
    TreeView v3 = world();
    PlanStart r = plan_start(dir, &files, &v3, R"([{"mod":"bigmod","version":"2.0.0"},{"mod":"codemod","version":"1.0.0"}])", false, true);
    check(r.report["rollback"].is_object() && r.report["rollback"]["state"] == "applied" && r.report["rollback"]["plan"] == ha,
          "registry.json: the rollback to the previous plan is applied: " + r.report["rollback"].dump());
    check(field_row(r.report, "game:plank", "maxStack")["result"] == 80, "the start runs on the previous plan's values: plank 80");
    check(field_row(r.report, "game:material_t1_wood_logs", "maxStack")["result"].is_null(), "wood logs: the changed plan's edit is not applied");
    check(plan_hash(r) == ha && !files.data.count("plans/" + std::string(64, '0') + ".json"), "registry.json names the plan it started on");
    json back = r.report["rollback"]["changedBack"];
    bool plank = false, logs = false, cap = false;
    for (const auto& row : back) {
        plank = plank || (row["field"] == "items[game:plank].maxStack" && row["from"] == 160 && row["to"] == 80);
        logs = logs || (row["field"] == "items[game:material_t1_wood_logs].maxStack" && row["from"] == 30 && row["to"] == 10);
        cap = cap || row["field"] == "balance[game:balance].levelCap";
    }
    check(plank && logs && !cap, "it says exactly what changed back (plank 160 -> 80, logs 30 -> 10; levelCap was 60 in both): " + back.dump());
    check(logged(r.log, "rolled back to plan " + ha.substr(0, 12)) && logged(r.log, "items[game:plank].maxStack 160 -> 80"), "and says so in kiln.log");
    bool guarded = false;
    for (const auto& n : r.report["saves"]) guarded = guarded || (n["field"] == "items[game:plank].maxStack" && n["decision"] == "guard");
    check(guarded, "rolling the largest stack back down (160 -> 80) keeps the stacks the save holds whole: the stack guard is on: " + r.report["saves"].dump());
    check(!r.callback_ran, "no mod's stage callback runs on a rolled-back start (the plan holds the edits)");
    check(r.rollback == ha, "the live back-end is told the start runs on a rolled-back plan");
    bool left = false;
    for (const auto& m : r.report["rollback"]["leftOut"]) left = left || m == "codemod";
    check(left, "a mod with registry code that the plan does not hold is named as left out: " + r.report["rollback"].dump());
    check(files.data.count("rollback.json"), "the request stays until it is cleared: the next start runs on the same plan");
    TreeView v4 = world();
    PlanStart r2 = plan_start(dir, &files, &v4);
    check(r2.report["rollback"]["state"] == "applied" && field_row(r2.report, "game:plank", "maxStack")["result"] == 80, "the next start is rolled back too");
    files.data.erase("rollback.json");
    TreeView v5 = world();
    PlanStart n = plan_start(dir, &files, &v5);
    check(n.report["rollback"].is_null() && field_row(n.report, "game:plank", "maxStack")["result"] == 160 && plan_hash(n) == hb,
          "cleared: the mods' current edits again");
}

void rollback_refused_whole(const std::string& dir) {
    Files files;
    g_stacks = kGood;
    TreeView v1 = world();
    const std::string ha = plan_hash(plan_start(dir, &files, &v1));
    g_stacks = kChanged;
    TreeView v2 = world();
    const std::string hb = plan_hash(plan_start(dir, &files, &v2));
    auto ask = [&](const std::string& plan) { files.data["rollback.json"] = json{{"format", 1}, {"plan", plan}, {"from", hb}}.dump(); };
    auto refused = [&](PlanStart s, const std::string& why) {
        check(s.report["rollback"].is_object() && s.report["rollback"]["state"] == "refused" &&
                  s.report["rollback"]["reason"].get<std::string>().find(why) != std::string::npos,
              "the rollback is refused (" + why + "): " + s.report["rollback"].dump());
        check(field_row(s.report, "game:plank", "maxStack")["result"] == 160 && field_row(s.report, "game:material_t1_wood_logs", "maxStack")["result"] == 30,
              "and the start runs on the mods' current edits, nothing of the plan half-applied");
        check(logged(s.log, "rollback") && s.rollback.empty(), "said in kiln.log; the live back-end is not rolled back");
    };
    ask(ha);
    TreeView v3 = world();
    refused(plan_start(dir, &files, &v3, R"([{"mod":"bigmod","version":"2.1.0"}])"), "bigmod changed (2.0.0, now 2.1.0)");
    TreeView v4 = world();
    PlanStart gone = plan_start(dir, &files, &v4, R"([{"mod":"othermod","version":"1.0.0"}])");
    check(gone.report["rollback"]["state"] == "refused" && gone.report["rollback"]["reason"].get<std::string>().find("bigmod is gone") != std::string::npos &&
              field_row(gone.report, "game:plank", "maxStack")["result"].is_null() && gone.rollback.empty(),
          "a plan whose mod is gone is refused, nothing of it applied (Kiln.Host did not load bigmod, so neither do its current edits): " +
              gone.report["rollback"].dump());
    ask(std::string(64, 'a'));
    TreeView v5 = world();
    refused(plan_start(dir, &files, &v5), "not in kiln/data/content/registry/plans");
    ask(ha);
    const std::string keep = files.data["plans/" + ha + ".json"];
    json edited = json::parse(keep);
    edited["values"]["items[game:plank].maxStack"] = 9999;
    files.data["plans/" + ha + ".json"] = edited.dump();
    TreeView v6 = world();
    refused(plan_start(dir, &files, &v6), "does not match its name");
    files.data["plans/" + ha + ".json"] = keep;
    files.data["rollback.json"] = "{not json";
    TreeView v7 = world();
    refused(plan_start(dir, &files, &v7), "cannot be read");
    // review: members of another type than Kiln writes (a hand edit) refuse the rollback, never throw into the boot
    files.data["rollback.json"] = json{{"format", "1"}, {"plan", ha}}.dump();
    TreeView v7b = world();
    refused(plan_start(dir, &files, &v7b), "cannot be read");
    files.data["rollback.json"] = json{{"format", 1}, {"plan", ha}, {"requested", 5}}.dump();
    TreeView v7c = world();
    refused(plan_start(dir, &files, &v7c), "cannot be read");
    files.data["plans/" + ha + ".json"] = json{{"format", "2"}, {"inputs", json::array()}}.dump();
    files.data["rollback.json"] = json{{"format", 1}, {"plan", ha}}.dump();
    TreeView v7d = world();
    refused(plan_start(dir, &files, &v7d), "cannot be read");
    files.data["plans/" + ha + ".json"] = keep;
    files.data["rollback.json"] = "{not json";  // as before these cases: the next start (plan C below) is no rollback
    // a plan edit that no longer applies on this base (the entry is gone): refused whole, never half-applied
    g_stacks = R"({"registry": {"alter": [
        {"registry": "items", "select": {"id": "game:plank"}, "field": "maxStack", "op": 70},
        {"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "maxStack", "op": 40}]}})";
    TreeView v8 = world();
    const std::string hc = plan_hash(plan_start(dir, &files, &v8));
    g_stacks = kChanged;
    ask(hc);
    TreeView v9 = world();
    v9.types["keen::ItemInfo"].erase(v9.types["keen::ItemInfo"].begin());  // the game build without wood logs
    PlanStart s9 = plan_start(dir, &files, &v9);
    check(s9.report["rollback"]["state"] == "refused" && s9.report["rollback"]["reason"].get<std::string>().find("material_t1_wood_logs") != std::string::npos &&
              field_row(s9.report, "game:plank", "maxStack")["result"] == 160,
          "a plan whose edit is refused on this base is refused whole: plank keeps the current 160, not the plan's 70: " + s9.report["rollback"].dump());
    // the plan rolled back to lowers a max stack and the stack guard is not available here: refused whole
    ask(ha);
    g_guard_available = false;
    TreeView v11 = world();
    refused(plan_start(dir, &files, &v11), "stack guard is not available here");
    g_guard_available = true;
    // a format-1 plan (an older Kiln's) holds no edits to start on
    json old = json::parse(keep);
    old["format"] = 1;
    old.erase("inputs");
    old.erase("previous");
    const std::string ho = kiln::boot_gd::hex_of(kiln::boot_gd::sha256(old.dump()));
    files.data["plans/" + ho + ".json"] = old.dump();
    ask(ho);
    TreeView v10 = world();
    refused(plan_start(dir, &files, &v10), "older Kiln");
}
// ---- registry-rollback, package K: field edits that change what existing saves hold (registry/saves.h) ----
void save_checks_of_restart_fields(const std::string& dir) {
    Files files;
    g_stacks = R"({"registry": {"alter": [
        {"registry": "balance", "select": {"id": "game:balance"}, "field": "levelCap", "op": 40},
        {"registry": "balance", "select": {"id": "game:balance"}, "field": "durability.start", "op": 50},
        {"registry": "balance", "select": {"id": "game:balance"}, "field": "buildzoneSizesPerAltarLevel",
         "list": {"replace": [{"x": 40, "y": 40, "z": 40}, {"x": 60, "y": 60, "z": 60}]}}]}})";
    TreeView v = world();
    stage::Runner r(env_of(&v, &files, dir, &stacks));
    const auto boot = r.run({});
    bool cap = false, dur = false, zones = false;
    for (const auto& b : boot)
        for (const auto& f : b.fields) {
            cap = cap || f.field.field == "levelCap";
            dur = dur || f.field.field == "durability.start";
            zones = zones || f.field.field == "buildzoneSizesPerAltarLevel";
        }
    check(cap && dur && !zones, "the boot back-end writes levelCap and durability.start, never the refused shorter build-zone list");
    std::vector<stage::BootOutcome> outcomes;
    for (const auto& b : boot)
        for (const auto& f : b.fields) outcomes.push_back({f.field, true, f.field.field == "levelCap" ? Value::integer(40) : Value::number(50), ""});
    r.finish(outcomes);
    json rep = json::parse(files.report);
    bool refused_zones = false;
    for (const auto& x : rep["refused"])
        refused_zones = refused_zones || (x.value("field", "") == "buildzoneSizesPerAltarLevel" &&
                                          x.value("reason", "").find("shortens the build-zone sizes to 2 altar level(s) from 3") != std::string::npos);
    check(refused_zones, "a shorter build-zone list is refused, with why: " + rep["refused"].dump().substr(0, 600));
    std::map<std::string, std::string> notes;
    for (const auto& n : rep["saves"]) notes[n["field"].get<std::string>()] = n["class"].get<std::string>() + "/" + n["decision"].get<std::string>();
    check(notes["balance[game:balance].levelCap"] == "progressionCap/warn" && notes["balance[game:balance].durability.start"] == "durability/report" && notes.size() == 2,
          "registry.json saves: levelCap lowered warns, durability reports: " + rep["saves"].dump());
    check(logged(files.log, "1 registry: saves: balance[game:balance].levelCap lowers the level cap 50 -> 40") &&
              logged(files.log, "0 registry: saves: balance[game:balance].durability.start"),
          "kiln.log: the warning and the line");
}

void stack_guard_against_the_last_world(const std::string& dir) {
    Files files;
    g_guard_calls = 0;
    g_stacks = kGood;  // plank 20 x4 = 80; a world runs on it
    TreeView v1 = world();
    plan_start(dir, &files, &v1);
    check(g_guard_calls == 0, "raising needs no guard at the stage");
    g_stacks = R"({"registry": {"alter": [{"registry": "balance", "select": {"id": "game:balance"}, "field": "levelCap", "op": 60}]}})";
    TreeView v2 = world();
    PlanStart s = plan_start(dir, &files, &v2);  // the mod's stack edit is gone: plank is the game's 20 again
    std::string row;
    for (const auto& n : s.report["saves"])
        if (n["field"] == "items[game:plank].maxStack") row = n["decision"].get<std::string>() + ": " + n["text"].get<std::string>();
    check(g_guard_calls > 0 && row.rfind("guard: a world already started with the largest stack 80, this start gives 20:", 0) == 0,
          "a start that gives an item a lower max stack than a world already started with turns the stack guard on: " + row);
    check(json::parse(files.data["plans/index.json"])["stacks"]["items[game:plank].maxStack"] == 80,
          "plans/index.json keeps the highest max stack a world started with (never lowered by the later world on 20)");
    g_guard_available = false;
    TreeView v3 = world();
    s = plan_start(dir, &files, &v3);
    row.clear();
    for (const auto& n : s.report["saves"])
        if (n["field"] == "items[game:plank].maxStack") row = n["decision"].get<std::string>() + ": " + n["text"].get<std::string>();
    g_guard_available = true;
    check(row.rfind("warn: ", 0) == 0 && row.find("not available here") != std::string::npos && logged(s.log, "1 registry: saves: items[game:plank].maxStack"),
          "two starts later, and without the guard, still a warning (that save can still hold 80-stacks; nothing can be refused for an edit that is gone): " + row);
}

// review of registry-rollback: the high-water mark (plans/index.json "stacks"). A later world on a lower max stack never
// lowers it, and a start with no registry content at all after a raise still asks the stack guard (the base is read for
// that alone).
void stack_high_water_mark(const std::string& dir) {
    Files files;
    auto plank = [](int v) {
        return R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:plank"}, "field": "maxStack", "op": )" + std::to_string(v) + "}]}}";
    };
    g_stacks = plank(80);
    TreeView v1 = world();
    plan_start(dir, &files, &v1);
    g_stacks = plank(40);
    TreeView v2 = world();
    plan_start(dir, &files, &v2);
    check(json::parse(files.data["plans/index.json"])["stacks"]["items[game:plank].maxStack"] == 80,
          "a later world on 40 does not lower the mark (80): " + json::parse(files.data["plans/index.json"]).dump());
    g_stacks = plank(50);
    g_guard_calls = 0;
    TreeView v3 = world();
    PlanStart s = plan_start(dir, &files, &v3);
    std::string row;
    for (const auto& n : s.report.value("saves", json::array()))
        if (n["field"] == "items[game:plank].maxStack") row = n["decision"].get<std::string>() + ": " + n["text"].get<std::string>();
    check(g_guard_calls > 0 && row.rfind("guard: a world already started with the largest stack 80, this start gives 50:", 0) == 0,
          "50 is below the 80 a world started with (not the last world's 40): the guard is asked: " + row);
    // no registry content at all: the base is still read for the mark, and the guard asked
    TreeView v4 = world();
    stage::Env env = env_of(&v4, &files, dir, &no_sections);
    g_guard_calls = 0;
    env.stack_guard = [](std::string*) { ++g_guard_calls; return true; };
    stage::Runner r(std::move(env));
    r.run({});
    r.finish({});
    const json rep = json::parse(files.report);
    bool noted = false;
    for (const auto& n : rep.value("saves", json::array())) noted = noted || (n["field"] == "items[game:plank].maxStack" && n["decision"] == "guard");
    check(g_guard_calls > 0 && noted, "a start without registry content after a raise asks the guard: " + rep.value("saves", json::array()).dump());
}

// ---- review of registry-rollback ----
const char* rb_row(const json& rep) { return rep["rollback"].is_object() && rep["rollback"]["state"].is_string() ? "" : "no rollback row"; }
std::string reason_of(const PlanStart& s) {
    return s.report["rollback"].is_object() && s.report["rollback"]["reason"].is_string() ? s.report["rollback"]["reason"].get<std::string>() : std::string();
}

// changedBack lists a value that only the plan rolled back to edits (the plan rolled away from left it at the game's value)
void rollback_changed_back_names_every_value(const std::string& dir) {
    Files files;
    g_stacks = kGood;  // plank x4 = 80, levelCap 60
    TreeView v1 = world();
    const std::string ha = plan_hash(plan_start(dir, &files, &v1));
    g_stacks = R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:plank"}, "field": "maxStack", "op": {"mul": 8}}]}})";
    TreeView v2 = world();
    const std::string hb = plan_hash(plan_start(dir, &files, &v2));
    files.data["rollback.json"] = json{{"format", 1}, {"plan", ha}, {"from", hb}}.dump();
    TreeView v3 = world();
    PlanStart r = plan_start(dir, &files, &v3);
    check(r.report["rollback"].is_object() && r.report["rollback"]["state"] == "applied", std::string(rb_row(r.report)) + r.report["rollback"].dump());
    bool plank = false, cap = false;
    for (const auto& row : r.report["rollback"]["changedBack"]) {
        plank = plank || (row["field"] == "items[game:plank].maxStack" && row["from"] == 160 && row["to"] == 80);
        cap = cap || (row["field"] == "balance[game:balance].levelCap" && row["from"] == 50 && row["to"] == 60);
    }
    check(plank && cap && r.report["rollback"]["changedBackTotal"] == 2,
          "changedBack: plank 160 -> 80 and levelCap 50 (the game's, plan B has no edit of it) -> 60: " + r.report["rollback"].dump());
    check(logged(r.log, "registry: rollback: balance[game:balance].levelCap 50 -> 60"), "and kiln.log names it");
}

// A rollback is judged against the values of the plan it rolls away from (a world ran on them, so its saves can hold them):
// a narrower level range or a shorter build-zone list than that plan's refuses the plan whole; a wider one is applied.
void rollback_judged_against_the_plan_rolled_away_from(const std::string& dir) {
    Files files;
    auto levels = [](int max) {
        return R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:plank"}, "field": "levels.max", "op": )" + std::to_string(max) + "}]}}";
    };
    g_stacks = levels(10);
    TreeView v1 = world();
    const std::string ha = plan_hash(plan_start(dir, &files, &v1));
    g_stacks = levels(20);
    TreeView v2 = world();
    const std::string hb = plan_hash(plan_start(dir, &files, &v2));
    check(!ha.empty() && !hb.empty() && ha != hb, "two plans");
    files.data["rollback.json"] = json{{"format", 1}, {"plan", ha}, {"from", hb}}.dump();
    TreeView v3 = world();
    PlanStart s = plan_start(dir, &files, &v3);
    check(s.report["rollback"].is_object() && s.report["rollback"]["state"] == "refused" &&
              reason_of(s).find("items[game:plank].levels.max narrows the level range (levels.max 20 -> 10)") != std::string::npos &&
              reason_of(s).find(hb.substr(0, 12)) != std::string::npos,
          "rolling levels.max back from 20 to 10 would clamp copies a world on plan B saved: refused whole, naming that plan: " + s.report["rollback"].dump());
    check(field_row(s.report, "game:plank", "levels.max")["result"] == 20 && s.rollback.empty(), "the mods' current edits run (20), not the plan's 10");
    // the other way round widens the range: applied
    g_stacks = levels(10);
    files.data["rollback.json"] = json{{"format", 1}, {"plan", hb}, {"from", ha}}.dump();
    TreeView v4 = world();
    PlanStart w = plan_start(dir, &files, &v4);
    check(w.report["rollback"]["state"] == "applied" && field_row(w.report, "game:plank", "levels.max")["result"] == 20,
          "rolling back to a wider range (10 -> 20) is applied: " + w.report["rollback"].dump());
    // without "from": judged against the plan the last world ran on (plans/index.json "world")
    files.data["rollback.json"] = json{{"format", 1}, {"plan", ha}}.dump();
    TreeView v5 = world();
    PlanStart nf = plan_start(dir, &files, &v5);
    check(nf.report["rollback"]["state"] == "refused" && reason_of(nf).find("levels.max 20 -> 10") != std::string::npos,
          "no from: the last world's plan (B, 20) is the reference: " + nf.report["rollback"].dump());
    // the build-zone list: plan D gives a fourth altar level, plan C none (the game's three)
    files.data.erase("rollback.json");
    g_stacks = R"({"registry": {"alter": [{"registry": "balance", "select": {"id": "game:balance"}, "field": "levelCap", "op": 60}]}})";
    TreeView v6 = world();
    const std::string hc = plan_hash(plan_start(dir, &files, &v6));
    g_stacks = R"({"registry": {"alter": [{"registry": "balance", "select": {"id": "game:balance"}, "field": "levelCap", "op": 60},
        {"registry": "balance", "select": {"id": "game:balance"}, "field": "buildzoneSizesPerAltarLevel", "list": {"append": [{"x": 99, "y": 99, "z": 99}]}}]}})";
    TreeView v7 = world();
    PlanStart d = plan_start(dir, &files, &v7);
    const std::string hd = plan_hash(d);
    check(d.report["refused"].empty() && hd != hc, "plan D (four build-zone sizes) starts cleanly: " + d.report["refused"].dump().substr(0, 400));
    files.data["rollback.json"] = json{{"format", 1}, {"plan", hc}, {"from", hd}}.dump();
    TreeView v8 = world();
    PlanStart z = plan_start(dir, &files, &v8);
    check(z.report["rollback"]["state"] == "refused" && reason_of(z).find("shortens the build-zone sizes to 3 altar level(s) from 4") != std::string::npos,
          "rolling back to three build-zone sizes would leave a saved level-4 altar without one: refused whole: " + z.report["rollback"].dump());
    // a warning class against that plan is reported (levelCap 70 -> 60; the game's 50 alone would say nothing)
    files.data.erase("rollback.json");
    auto cap = [](int c) {
        return R"({"registry": {"alter": [{"registry": "balance", "select": {"id": "game:balance"}, "field": "levelCap", "op": )" + std::to_string(c) + "}]}}";
    };
    g_stacks = cap(60);
    TreeView v9 = world();
    const std::string hf = plan_hash(plan_start(dir, &files, &v9));
    g_stacks = cap(70);
    TreeView v10 = world();
    const std::string he = plan_hash(plan_start(dir, &files, &v10));
    files.data["rollback.json"] = json{{"format", 1}, {"plan", hf}, {"from", he}}.dump();
    TreeView v11 = world();
    PlanStart c = plan_start(dir, &files, &v11);
    std::string note;
    for (const auto& n : c.report.value("saves", json::array()))
        if (n["field"] == "balance[game:balance].levelCap") note = n["class"].get<std::string>() + "/" + n["decision"].get<std::string>() + ": " + n["text"].get<std::string>();
    check(c.report["rollback"]["state"] == "applied" && note.rfind("progressionCap/warn: lowers the level cap 70 -> 60", 0) == 0 && note.find("against plan " + he.substr(0, 12)) != std::string::npos,
          "registry.json saves: the rollback lowers the level cap against the plan it rolls away from: " + note);
}

// A start (not a rollback) whose level range is narrower than the last world's: applied with a warning (refusing it would
// leave the game's range, narrower still)
void a_start_below_the_last_worlds_range_warns(const std::string& dir) {
    Files files;
    auto levels = [](int max) {
        return R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:plank"}, "field": "levels.max", "op": )" + std::to_string(max) + "}]}}";
    };
    g_stacks = levels(20);
    TreeView v1 = world();
    const std::string ha = plan_hash(plan_start(dir, &files, &v1));
    g_stacks = levels(15);
    TreeView v2 = world();
    PlanStart s = plan_start(dir, &files, &v2);
    std::string note;
    for (const auto& n : s.report.value("saves", json::array()))
        if (n["field"] == "items[game:plank].levels.max") note = n["class"].get<std::string>() + "/" + n["decision"].get<std::string>() + ": " + n["text"].get<std::string>();
    check(field_row(s.report, "game:plank", "levels.max")["result"] == 15 && s.report["refused"].empty(), "15 is applied (the game's 1 would be narrower still)");
    check(note.rfind("levelRange/warn: narrows the level range (levels.max 20 -> 15)", 0) == 0 && note.find("against plan " + ha.substr(0, 12)) != std::string::npos &&
              note.find("not applied") == std::string::npos,
          "registry.json saves warns against the plan the last world ran on: " + note);
    check(logged(s.log, "1 registry: saves: items[game:plank].levels.max narrows the level range"), "a kiln.log warning");
}

// A plan whose max stack is lower than the value under it needs the stack guard at the stage, not per entry at the world's load
// (here the index was deleted, so no highest stack is known, and the request names no plan rolled away from).
void rollback_plan_lowering_a_stack_asks_the_guard_at_the_stage(const std::string& dir) {
    Files files;
    g_stacks = R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:plank"}, "field": "maxStack", "op": 10}]}})";  // 20 -> 10
    TreeView v1 = world();
    const std::string ha = plan_hash(plan_start(dir, &files, &v1));
    g_stacks = kGood;
    TreeView v2 = world();
    plan_start(dir, &files, &v2);
    files.data.erase("plans/index.json");
    files.data["rollback.json"] = json{{"format", 1}, {"plan", ha}}.dump();
    g_guard_available = false;
    TreeView v3 = world();
    PlanStart s = plan_start(dir, &files, &v3);
    g_guard_available = true;
    check(s.report["rollback"].is_object() && s.report["rollback"]["state"] == "refused" && reason_of(s).find("stack guard is not available") != std::string::npos,
          "the plan lowers plank's max stack 20 -> 10 and the guard is not available: refused whole at the stage: " + s.report["rollback"].dump());
    check(field_row(s.report, "game:plank", "maxStack")["result"] == 80 && s.rollback.empty(), "the mods' current edits run (80)");
    TreeView v4 = world();
    g_guard_calls = 0;
    PlanStart ok = plan_start(dir, &files, &v4);
    check(ok.report["rollback"]["state"] == "applied" && g_guard_calls > 0, "with the guard: applied, the guard asked at the stage: " + ok.report["rollback"].dump());
}

// plans/index.json that cannot be read: kiln.log says so, the file is kept beside it, and since the highest stacks a world
// started with are unknown the stack guard goes on; a hand-edited index naming no plan hash is never passed on as previous.
void an_unreadable_index_is_kept_and_reported(const std::string& dir) {
    Files files;
    g_stacks = kGood;
    TreeView v1 = world();
    const std::string ha = plan_hash(plan_start(dir, &files, &v1));
    const std::string cut = "{\"format\": 1, \"good\": {\"plan\": \"" + ha + "\"}, \"stacks\": {\"items[game:plank].maxStack\": 8";  // a write cut short
    files.data["plans/index.json"] = cut;
    g_stacks = R"({"registry": {"alter": [{"registry": "balance", "select": {"id": "game:balance"}, "field": "levelCap", "op": 60}]}})";
    g_guard_calls = 0;
    TreeView v2 = world();
    PlanStart s = plan_start(dir, &files, &v2);
    check(logged(s.log, "1 registry: kiln/data/content/registry/plans/index.json cannot be read"), "a warning in kiln.log");
    check(files.data.count("plans/index.unreadable.json") && files.data["plans/index.unreadable.json"] == cut, "the unreadable index is kept beside it");
    check(g_guard_calls > 0, "the highest max stacks a world started with are unknown: the stack guard is asked");
    files.data["plans/index.json"] = json{{"format", 1}, {"good", {{"plan", "../../savegame/x"}, {"previous", 7}}}, {"stacks", {{"items[game:plank].maxStack", "big"}}}}.dump();
    TreeView v3 = world();
    PlanStart t = plan_start(dir, &files, &v3);
    check(t.report["plan"]["previous"].is_null(), "an index entry that is no plan hash is not a previous plan: " + t.report["plan"].dump());
}

// The schemas' id migrations inside a GameStart callback's batch are named in registry.json "migrated" like a data section's
// (a synthetic items 0.0.9 -> 0.1.0 step renaming stackSize to maxStack: test fixture only)
void migrated_names_of_a_callback_turn(const std::string& dir) {
    Files files;
    TreeView view = world();
    stage::Env env = env_of(&view, &files, dir, &no_sections);
    Schema m = generated_schema();
    m.registries["items"].migrations = {{"0.0.9", "0.1.0", {}, {{"stackSize", "maxStack"}}}};
    env.schema = m;
    stage::Runner r(std::move(env));
    check(r.set_order(R"([{"mod":"codemod","version":"1.0.0"}])") == KILN_OK, "set_order");
    uint64_t h = 0;
    check(r.subscribe_stage(9, "codemod", [&](Stage s, const View&) {
              if (s != Stage::Alter) return true;
              uint32_t a = 0, b = 0;
              std::string rep;
              r.submit(9, "codemod", R"({"ir":1,"mod":"codemod","stage":"alter","edits":[{"n":7,"registry":"items","select":{"id":"game:plank"},"field":"stackSize","op":30}]})", &a, &b, &rep);
              return true;
          }, &h) == KILN_OK, "a native stage callback");
    r.run({});
    r.finish({});
    const json rep = json::parse(files.report);
    bool named = false;
    for (const auto& x : rep.value("migrated", json::array()))
        named = named || (x.value("mod", "") == "codemod" && x.value("note", "").find("#0: items.stackSize -> items.maxStack") != std::string::npos);
    check(field_row(rep, "game:plank", "maxStack")["result"] == 30, "the old name is read as maxStack (30)");
    check(named, "registry.json migrated names the turn's rename, numbered as the turn's edit: " + rep.value("migrated", json::array()).dump());
}

// A start whose stage had no base to read applied nothing: a world on it does not make it the last good plan
void a_start_without_a_base_is_not_a_good_plan(const std::string& dir) {
    Files files;
    g_stacks = kGood;
    TreeView v1 = world();
    const std::string ha = plan_hash(plan_start(dir, &files, &v1));
    g_stacks = kChanged;
    TreeView v2 = world();
    stage::Env env = env_of(&v2, &files, dir, &stacks);
    env.source = [](std::string* why) -> std::unique_ptr<model::Source> { *why = "the view is not ready"; return nullptr; };
    stage::Runner r(std::move(env));
    check(r.set_order(R"([{"mod":"bigmod","version":"2.0.0"}])") == KILN_OK, "set_order");
    std::function<void(const Result&, bool)> world_report;
    const Result* live_result = nullptr;
    r.set_live([&](const stage::LiveHandoff& h) { world_report = h.report, live_result = h.result; });
    r.run({});
    r.finish({});
    check(bool(world_report) && live_result, "the live back-end is handed over");
    world_report(*live_result, true);
    const json idx = json::parse(files.data["plans/index.json"]);
    check(idx["good"]["plan"] == ha, "the last good plan stays A: " + idx.dump());
}
// registry-fixes item 6 (review-registry-rollback Q5): plans were never pruned, so the plans folder grew by one file per
// changed mod set without bound. At the end of a successful start Kiln keeps the last good plan, its previous, the plan the
// last world ran on, this start's plan, any plan a pending rollback.json names (and its "from"), and the newest 10 others,
// and removes the other plans (only <sha256>.json files: the index, a kept unreadable index, temp files stay). A start that
// failed (no base), whose rollback was refused (its error path) or whose index could not be read removes nothing.
std::string junk_hash(int i) {
    char b[65];
    std::snprintf(b, sizeof b, "%064x", 0xabc000 + i);
    return b;
}
void plan_files(Files& files, int n, int64_t from_time) {
    for (int i = 0; i < n; ++i) {
        const std::string rel = "plans/" + junk_hash(i) + ".json";
        files.data[rel] = json{{"format", 2}, {"values", json::object()}, {"inputs", json::array()}}.dump();
        files.times[rel] = from_time + i;  // junk 0 is the oldest
    }
}
size_t plan_count(const Files& files) {
    size_t n = 0;
    for (const auto& [rel, t] : files.data)
        if (rel.rfind("plans/", 0) == 0 && rel.size() == 6 + 64 + 5 && rel.compare(rel.size() - 5, 5, ".json") == 0) ++n;
    return n;
}
bool has_plan(const Files& files, const std::string& hash) { return files.data.count("plans/" + hash + ".json") != 0; }

void plans_are_pruned_at_the_end_of_a_successful_start(const std::string& dir) {
    Files files;
    plan_files(files, 15, 1);  // 15 plans of earlier starts, junk 0 the oldest
    // the index names three of them: the last good plan (junk 0, the oldest), its previous (junk 1), the last world's (junk 2)
    files.data["plans/index.json"] = json{{"format", 1}, {"good", {{"plan", junk_hash(0)}, {"previous", junk_hash(1)}, {"at", "x"}}},
                                          {"world", {{"plan", junk_hash(2)}, {"at", "x"}}}}.dump();
    files.data["plans/index.unreadable.json"] = "{cut";
    files.data["plans/" + junk_hash(99) + ".json.tmp"] = "{";
    files.data["plans/readme.txt"] = "a person's note";
    g_stacks = kGood;
    TreeView v1 = world();
    const PlanStart a = plan_start(dir, &files, &v1);
    const std::string ha = plan_hash(a);
    check(!ha.empty() && has_plan(files, ha), "the start writes its plan");
    check(has_plan(files, junk_hash(0)) && has_plan(files, junk_hash(1)) && has_plan(files, junk_hash(2)),
          "the last good plan, its previous and the last world's plan stay");
    bool newest = true;
    for (int i = 5; i < 15; ++i) newest = newest && has_plan(files, junk_hash(i));
    check(newest, "the newest 10 other plans stay");
    check(!has_plan(files, junk_hash(3)) && !has_plan(files, junk_hash(4)), "the older others are removed");
    check(plan_count(files) == 1 + 3 + 10, "14 plans are left: " + std::to_string(plan_count(files)));
    check(files.data.count("plans/index.json") && files.data.count("plans/index.unreadable.json") &&
              files.data.count("plans/" + junk_hash(99) + ".json.tmp") && files.data.count("plans/readme.txt"),
          "files that are no plan stay (the index, the kept unreadable one, a temp file, a note)");
    check(logged(a.log, "registry: plans: removed 2 older plan(s)"), "kiln.log says how many it removed");

    // a pending rollback.json names a plan (and the plan it rolls away from): both stay. It is written after this start read the
    // requests (as kilnctl registry rollback does while a game runs), so this start runs on the mods' edits and prunes.
    plan_files(files, 15, 0);  // the junk again, all older than ha now
    for (int i = 0; i < 15; ++i) files.times["plans/" + junk_hash(i) + ".json"] = i;  // junk 0 the oldest
    g_stacks = kChanged;
    TreeView v2 = world();
    stage::Env env = env_of(&v2, &files, dir, &stacks);
    auto read = env.read_data;
    int rollback_reads = 0;
    env.read_data = [&, read](const std::string& rel) -> std::optional<std::string> {
        if (rel == "rollback.json" && ++rollback_reads > 1)  // the pruning's read, after the stage's
            return json{{"format", 1}, {"plan", junk_hash(3)}, {"from", junk_hash(4)}, {"requested", "x"}, {"by", "test"}}.dump();
        return read(rel);
    };
    {
        stage::Runner r(std::move(env));
        check(r.set_order(R"([{"mod":"bigmod","version":"2.0.0"}])") == KILN_OK, "set_order");
        r.set_live([&](const stage::LiveHandoff&) {});
        r.run({});
        r.finish({});
    }
    check(rollback_reads >= 2, "the pruning reads the rollback request again at the end of the start");
    check(has_plan(files, junk_hash(3)) && has_plan(files, junk_hash(4)), "the plans a pending rollback.json names stay");
    check(has_plan(files, ha), "the start's previous (the last good plan) stays");

    // error paths: a start whose rollback was refused, a start with an unreadable index, a start without a base remove nothing
    plan_files(files, 15, 0);
    files.data["rollback.json"] = json{{"format", 1}, {"plan", std::string(64, 'e')}, {"requested", "x"}, {"by", "test"}}.dump();  // no such plan
    size_t before = plan_count(files);
    files.removed.clear();
    TreeView v3 = world();
    const PlanStart refused = plan_start(dir, &files, &v3);
    check(refused.report["rollback"].value("state", "") == "refused", "the rollback is refused (its plan is not there)");
    check(files.removed.empty() && plan_count(files) >= before, "a start whose rollback was refused removes no plan");
    files.data.erase("rollback.json");

    files.data["plans/index.json"] = "{\"format\": 1, \"good\"";  // cut short
    before = plan_count(files);
    TreeView v4 = world();
    plan_start(dir, &files, &v4);
    check(files.removed.empty() && plan_count(files) >= before, "a start whose index could not be read removes no plan");

    files.data["plans/index.json"] = json{{"format", 1}}.dump();
    TreeView v5 = world();
    stage::Env nobase = env_of(&v5, &files, dir, &stacks);
    nobase.source = [](std::string* why) -> std::unique_ptr<model::Source> { *why = "the view is not ready"; return nullptr; };
    {
        stage::Runner r(std::move(nobase));
        check(r.set_order(R"([{"mod":"bigmod","version":"2.0.0"}])") == KILN_OK, "set_order");
        r.set_live([&](const stage::LiveHandoff&) {});
        r.run({});
        r.finish({});
    }
    check(files.removed.empty(), "a start without a base removes no plan");

    // the rule itself: keep set, newest others by time, only plan names
    const std::vector<stage::Env::DataEntry> listing = {{junk_hash(1) + ".json", 5}, {junk_hash(2) + ".json", 9}, {junk_hash(3) + ".json", 1},
                                                        {"index.json", 0}, {junk_hash(4) + ".json.tmp", 0}, {junk_hash(5) + ".json", 7}};
    const auto gone = stage::plans_to_prune(listing, {junk_hash(1)}, 1);
    check(gone == std::vector<std::string>{junk_hash(5) + ".json", junk_hash(3) + ".json"},
          "plans_to_prune keeps the named plans and the newest others, and names only plan files (oldest last)");
}

}  // namespace

// review3 S4: registry.json does not call a record verified when it was proven on another regmap/common (every row is off)
void other_mapping_report(const std::string& dir) {
    TreeView view = world();
    Files files;
    stage::Env e = env_of(&view, &files, dir, [](const Schema&) { return std::vector<stage::Env::DataFile>{}; });
    e.regmap = [dir](map::RegMap* out, std::vector<std::string>* errs) {
        const bool ok = map::load_dir(dir, "server", kExe, out, errs);
        out->build.regmap_digest = std::string(64, 'b');
        return ok;
    };
    stage::Runner r(std::move(e));
    r.run([](model::Model& m) { return boothook::old_writes({}, m); });
    r.finish({});
    const json rep = json::parse(files.report);
    check(rep["regmap"]["verified"] == false && rep["regmap"]["record"].is_string(), "registry.json: a record of another mapping is not verified");
}

// kiln-eml's change journal of this start names the package that wrote the plank before Kiln (archive_journal.h): its row
// lies below the registry edits, named, and the conflict line says the registry edit wins (D1); a journal of another
// generation names nobody and registry.json says why; without one, base.journal says this start has none.
void archive_journal_writers(const std::string& dir) {
    auto journal = [](const std::string& generation) {
        return json{{"format", 1}, {"stem", "enshrouded_server"}, {"generation", generation}, {"receiptSha256", std::string(64, 'b')},
                    {"dialect", "shroudforge"}, {"attempt", "first"}, {"truncated", false},
                    {"packages", json::array({{{"id", "betterstacks"}, {"version", "1.0.0"}, {"kind", "shroudforge"},
                                               {"contentSha256", std::string(64, '1')}, {"settingsSha256", std::string(64, '2')}, {"truncated", false},
                                               {"changed", json::array({{{"guid", g(2)}, {"typeHash", "8c3dbe3d"}, {"part", 0}, {"createdBy", nullptr}}})},
                                               {"created", json::array()}, {"contents", json::array()}}})},
                    {"unattributed", {{"truncated", false}, {"changed", json::array()}, {"created", json::array()}, {"contents", json::array()}}}}
            .dump();
    };
    const std::string live = std::string(64, 'a');
    for (const std::string& written : {live, std::string(64, 'c'), std::string()}) {
        TreeView view = world();
        Files files;
        stage::Env e = env_of(&view, &files, dir, &sections);
        e.writer = "kiln-eml";
        if (!written.empty())
            e.archive_journal = [&](std::string* generation, std::string*) -> std::optional<std::string> { return *generation = live, journal(written); };
        stage::Runner r(std::move(e));
        const auto docs = old_patch();
        const auto boot = r.run([&](model::Model& m) { return boothook::old_writes(docs, m); });
        r.finish({{boot.at(0).fields.at(0).field, true, Value::integer(60), ""}});
        const json rep = json::parse(files.report);
        const json* plank = nullptr;
        for (const json& f : rep["fields"])
            if (f["id"] == "game:plank" && f["field"] == "maxStack") plank = &f;
        check(plank != nullptr, "the plank's maxStack row");
        bool line = false;
        for (const json& c : rep["conflicts"])
            line = line || (c["loser"] == "shroudforge:betterstacks" && c["certainty"] == "named" &&
                            c["text"].get<std::string>().find("betterstacks (ShroudForge package) wrote this entry") != std::string::npos);
        if (written == live) {
            check(rep["base"]["journal"]["state"] == "read" && rep["base"]["journal"]["entries"] == 1, "base.journal: read, one entry named: " + rep["base"].dump());
            check((*plank)["writers"][0]["kind"] == "archiveWriter" && (*plank)["writers"][0]["who"] == "shroudforge:betterstacks" &&
                      (*plank)["writers"][0]["certainty"] == "named" && (*plank)["writers"][0]["value"] == 20 && (*plank)["result"] == 160,
                  "the package is the plank's first writer, named, at the base value; the registry's x4 x2 still give 160: " + plank->dump());
            check(line, "a conflict line names the package and says the registry edit wins: " + rep["conflicts"].dump());
            bool other = false;
            for (const json& f : rep["fields"])
                for (const json& w : f["writers"]) other = other || (f["id"] != "game:plank" && w["kind"] == "archiveWriter");
            check(!other, "no other entry names the package");
        } else {
            check((*plank)["writers"][0]["kind"] == "registry" && !line, "no package is named");
            const std::string state = rep["base"]["journal"]["state"];
            check(written.empty() ? state == "none" : state == "stale", "base.journal says why nothing is named: " + rep["base"].dump());
            bool warned = false;
            for (const std::string& l : files.log) warned = warned || (l.rfind("1 ", 0) == 0 && l.find("another generation") != std::string::npos);
            check(written.empty() || warned, "a stale journal is a warning in kiln.log");
        }
    }
}

// Review (registry-writers): the journal lists resources by their key, so only a registry whose entries are each a whole
// resource (items, drops, balance) can be named from it. A recipe is an element of the shared RecipeRegistryResource: its
// recipeGuid is a member, not a resource key, so a package that wrote a resource with that GUID never wrote the recipe,
// and a package that wrote the recipe list cannot be pinned on one recipe. Neither names the recipe; base.journal says so.
std::vector<stage::Env::DataFile> recipe_sections(const Schema& schema) {
    const std::string fast = R"({"registry": {"alter": [{"registry": "recipes", "select": {"id": "game:craft_planks"}, "field": "duration", "op": 1}]}})";
    return stage::read_sections("bigmod", "2.0.0", {{"content/r.json", fast}}, schema);
}
void archive_journal_shared_resources(const std::string& dir) {
    const std::string live = std::string(64, 'a');
    // "other" wrote a resource whose GUID is the recipe's recipeGuid (g(101)); "lists" wrote the recipe list itself (g(100))
    const std::string journal =
        json{{"format", 1}, {"stem", "enshrouded_server"}, {"generation", live}, {"receiptSha256", std::string(64, 'b')}, {"dialect", "shroudforge"},
             {"attempt", "first"}, {"truncated", false},
             {"packages", json::array({{{"id", "other"}, {"version", "1.0.0"}, {"kind", "shroudforge"}, {"contentSha256", std::string(64, '1')},
                                        {"settingsSha256", std::string(64, '2')}, {"truncated", false},
                                        {"changed", json::array({{{"guid", g(101)}, {"typeHash", "01234567"}, {"part", 0}, {"createdBy", nullptr}}})},
                                        {"created", json::array()}, {"contents", json::array()}},
                                       {{"id", "lists"}, {"version", "1.0.0"}, {"kind", "shroudforge"}, {"contentSha256", std::string(64, '3')},
                                        {"settingsSha256", std::string(64, '4')}, {"truncated", false},
                                        {"changed", json::array({{{"guid", g(100)}, {"typeHash", "89abcdef"}, {"part", 0}, {"createdBy", nullptr}}})},
                                        {"created", json::array()}, {"contents", json::array()}}})},
             {"unattributed", {{"truncated", false}, {"changed", json::array()}, {"created", json::array()}, {"contents", json::array()}}}}
            .dump();
    TreeView view = world();
    Files files;
    stage::Env e = env_of(&view, &files, dir, &recipe_sections);
    e.writer = "kiln-eml";
    e.archive_journal = [&](std::string* generation, std::string*) -> std::optional<std::string> { return *generation = live, journal; };
    stage::Runner r(std::move(e));
    r.run({});
    r.finish({});
    const json rep = json::parse(files.report);
    const json* duration = nullptr;
    for (const json& f : rep["fields"])
        if (f["id"] == "game:craft_planks" && f["field"] == "duration") duration = &f;
    check(duration != nullptr, "the recipe's duration row: " + rep["fields"].dump());
    for (const json& w : (*duration)["writers"]) check(w["kind"] != "archiveWriter", "no package is named on a recipe: " + duration->dump());
    for (const json& c : rep["conflicts"]) check(c["certainty"] != "named", "no named conflict line on a recipe: " + c.dump());
    check(rep["base"]["journal"]["state"] == "read" && rep["base"]["journal"]["entries"] == 0, "the journal is read and names no entry: " + rep["base"].dump());
    check(rep["base"]["journal"]["text"].get<std::string>().find("recipes") != std::string::npos,
          "base.journal says recipes cannot be named from it: " + rep["base"].dump());
}

// Review (registry-writers): old_writes marks every entry an old boot document writes (an OldPatch row with no field),
// also where no Kiln field's row covers the path ("[*]" over the recipes, a formula's input): the live drift probe takes a
// change there for Kiln's own patch. Composition skips the marks; an entry no document writes has none.
void old_writes_marks(const std::string& dir) {
    TreeView view = world();
    Files files;
    stage::Runner r(env_of(&view, &files, dir, &recipe_sections));
    boothook::OldDoc faster;
    faster.mod = "oldmod", faster.key = "faster";
    faster.res = key_of(g(100), "keen::RecipeRegistryResource"), faster.type = "keen::RecipeRegistryResource";
    faster.sets = {{"recipes[*].craftingDuration", {{"mul", 0.5}}}};
    boothook::OldDoc hits;
    hits.mod = "oldmod", hits.key = "hits";
    hits.res = key_of(g(2), "keen::ItemInfo"), hits.type = "keen::ItemInfo";
    hits.sets = {{"damageSetup.dmgMod", 2.0}};
    std::vector<OldWrite> got;
    r.run([&](model::Model& m) { return got = boothook::old_writes({faster, hits}, m); });
    r.finish({});
    auto marked = [&](const std::string& reg, const std::string& id) {
        return std::any_of(got.begin(), got.end(), [&](const OldWrite& w) {
            return w.kind == WriterKind::OldPatch && w.field.registry == reg && w.field.id == id && w.field.field.empty() && w.who.rfind("oldmod/", 0) == 0;
        });
    };
    check(marked("recipes", "game:craft_planks"), "a \"[*]\" document over the recipes marks the recipe");
    check(marked("items", "game:plank"), "a formula input's path marks the item");
    check(!marked("items", "game:material_t1_wood_logs"), "an item no document writes is not marked");
    const json rep = json::parse(files.report);
    for (const json& f : rep["fields"])
        for (const json& w : f["writers"]) check(!(w["kind"] == "oldPatch" && f["field"] == ""), "a mark is never a chain row: " + f.dump());
    check(!rep["fields"].empty() && rep["refused"].empty(), "the recipe edit still composes: " + rep.dump().substr(0, 600));
}

// Review (registry-writers): an old document's element path ("recipes[0]...") names an element of ITS resource. With two
// resources of the registry's type (two recipe lists; the model reads the first one's elements as the registry's entries),
// element 0 of the second list is not the first list's recipe 0: no row of that document lies on game:craft_planks.
void old_writes_element_of_its_resource(const std::string& dir) {
    TreeView view = world();
    json in = {{"itemStack", {{"item", 1001}, {"itemRef", g(1)}, {"count", 1}}}, {"inputItemCategory", {{"category", 0}, {"categoryRef", "none"}, {"count", 0}}}};
    view.add("keen::RecipeRegistryResource", g(110),
             {{"recipes", json::array({{{"debugName", "Craft_Logs"}, {"recipeGuid", g(111)}, {"recipeId", 5002}, {"input", json::array({in})},
                                        {"output", json::array({{{"item", 1001}, {"itemRef", g(1)}, {"count", 1}}})}, {"craftingDuration", 4.0}}})},
              {"inputCategories", json::array()}});
    Files files;
    stage::Runner r(env_of(&view, &files, dir, &recipe_sections));
    boothook::OldDoc slow;
    slow.mod = "oldmod", slow.key = "slow";
    slow.res = key_of(g(110), "keen::RecipeRegistryResource"), slow.type = "keen::RecipeRegistryResource";
    slow.sets = {{"recipes[0].craftingDuration", 9.0}};
    std::vector<OldWrite> got;
    r.run([&](model::Model& m) { return got = boothook::old_writes({slow}, m); });
    r.finish({});
    bool logs = false, planks = false;
    for (const OldWrite& w : got)
        if (w.field.field == "duration") {
            if (w.field.id == "game:craft_logs") logs = true;
            if (w.field.id == "game:craft_planks") planks = true;
        }
    check(!planks, "the second list's recipe 0 is not the first list's: no row on game:craft_planks");
    check(!logs, "the second list's recipes are no registry entries (the first list is read)");
    for (const OldWrite& w : got) check(w.field.id != "game:craft_planks", "nothing of the document is on the other list's recipe: " + w.field.text());
    const json rep = json::parse(files.report);
    for (const json& f : rep["fields"])
        for (const json& w : f["writers"]) check(w["kind"] != "oldPatch", "no old patch row in registry.json: " + f.dump());
}

int main(int argc, char** argv) {
    const std::string dir = argc > 1 ? argv[1] : "regmap";
    int failed = 0;
    const std::pair<const char*, std::function<void()>> groups[] = {
        {"boot_sync", [&] { boot_sync(dir); }}, {"one_start", [&] { one_start(dir); }}, {"host_order_and_code", [&] { host_order_and_code(dir); }}, {"id_table_without_content", [&] { id_table_without_content(dir); }}, {"bridge_write", [] { bridge_write(); }},
        {"registry_caps", [&] { registry_caps(dir); }}, {"other_mapping_report", [&] { other_mapping_report(dir); }},
        {"plans_link_the_last_good_plan", [&] { plans_link_the_last_good_plan(dir); }},
        {"rollback_starts_on_the_previous_plan", [&] { rollback_starts_on_the_previous_plan(dir); }},
        {"rollback_refused_whole", [&] { rollback_refused_whole(dir); }},
        {"save_checks_of_restart_fields", [&] { save_checks_of_restart_fields(dir); }},
        {"stack_guard_against_the_last_world", [&] { stack_guard_against_the_last_world(dir); }},
        {"stack_high_water_mark", [&] { stack_high_water_mark(dir); }},
        {"rollback_changed_back_names_every_value", [&] { rollback_changed_back_names_every_value(dir); }},
        {"rollback_judged_against_the_plan_rolled_away_from", [&] { rollback_judged_against_the_plan_rolled_away_from(dir); }},
        {"a_start_below_the_last_worlds_range_warns", [&] { a_start_below_the_last_worlds_range_warns(dir); }},
        {"rollback_plan_lowering_a_stack_asks_the_guard_at_the_stage", [&] { rollback_plan_lowering_a_stack_asks_the_guard_at_the_stage(dir); }},
        {"an_unreadable_index_is_kept_and_reported", [&] { an_unreadable_index_is_kept_and_reported(dir); }},
        {"migrated_names_of_a_callback_turn", [&] { migrated_names_of_a_callback_turn(dir); }},
        {"a_start_without_a_base_is_not_a_good_plan", [&] { a_start_without_a_base_is_not_a_good_plan(dir); }},
        {"plans_are_pruned_at_the_end_of_a_successful_start", [&] { plans_are_pruned_at_the_end_of_a_successful_start(dir); }},
        {"archive_journal_writers", [&] { archive_journal_writers(dir); }},
        {"archive_journal_shared_resources", [&] { archive_journal_shared_resources(dir); }},
        {"old_writes_marks", [&] { old_writes_marks(dir); }},
        {"old_writes_element_of_its_resource", [&] { old_writes_element_of_its_resource(dir); }}};
    for (const auto& [name, fn] : groups) {
        try {
            fn();
        } catch (const std::exception& e) {
            ++failed;
            std::printf("registry-stage-test: FAIL %s: %s (after %d checks)\n", name, e.what(), checks);
        }
    }
    if (failed) {
        std::printf("registry-stage-test: %d group(s) failed, %d checks passed\n", failed, checks);
        return 1;
    }
    std::printf("registry-stage-test: %d checks passed\n", checks);
}
