// SPDX-License-Identifier: MIT
// kiln.content registry stage, host test: the registry line review's findings on the stage runner (out/registry-review,
// R1-R5, moved here). Harness as registry_stage_test.cpp. registry_stage_review_test.sh builds and runs it.
#include <cstdio>
#include <cstring>
#include <set>
#include <stdexcept>
#include <thread>

#include "../registry/boot_hooks.h"
#include "../registry/schema_ir.h"
#include "../registry/stage.h"
#include "../registry/stage_owner.h"

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
                                             {"armorSetup", {{"offset", 23.0}, {"gradient", 15.0}}}});
    return v;
}

struct Files {
    std::map<std::string, std::string> data;
    std::string report;
    std::vector<std::string> log;
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
    e.write_data = [files](const std::string& rel, const std::string& t) { return files->data[rel] = t, true; };
    e.read_data = [files](const std::string& rel) -> std::optional<std::string> {
        const auto it = files->data.find(rel);
        if (it == files->data.end()) return std::nullopt;
        return it->second;
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

std::vector<stage::Env::DataFile> no_sections(const Schema&) { return {}; }
std::vector<stage::Env::DataFile> items_only(const Schema& schema) {
    return stage::read_sections("late", "1.0.0", {{"content/x.json", R"({"registry": {"alter": [{"registry": "items", "select": {"id": "game:plank"}, "field": "maxStack", "op": {"mul": 2}}]}})"}}, schema);
}

// R1: a mod folder with a data section that Kiln.Host's load order does not name (Kiln.Host did not load it: wrong side,
// a missing dependency, an incompatible version) still has its registry edits applied. Data packs skip such a mod
// (content.cpp apply_packs -> order_packs); the stage runs it last instead.
void skipped_mod_data_applied(const std::string& dir) {
    TreeView view = world();
    Files files;
    stage::Runner r(env_of(&view, &files, dir, &sections));
    check(r.set_order(R"([{"mod":"bigmod","version":"2.0.0"}])") == KILN_OK, "set_order (Kiln.Host loaded bigmod only)");
    r.run({});
    r.finish({});
    const json rep = json::parse(files.report);
    bool late = false;
    for (const auto& o : rep["order"]) late = late || o["mod"] == "late";
    check(!late, "a mod Kiln.Host did not load is not in the stage: order = " + rep["order"].dump());
    check(rep.dump().find("\"result\":80") != std::string::npos, "maxStack is bigmod's x4 only (80), not x4 then late's x2 (160)");
    check(rep.dump().find("Kiln.Host did not load the mod on this side") != std::string::npos, "the skipped mod's file is named with the reason");
}

std::vector<stage::Env::DataFile> client_side(const Schema& schema) {
    auto out = items_only(schema);
    for (auto& f : out) f.side = "client";
    return out;
}
// R1b: without Kiln.Host's order (the folders'), a data mod for the other side is skipped and named
void other_side_mod_skipped(const std::string& dir) {
    TreeView view = world();
    Files files;
    stage::Runner r(env_of(&view, &files, dir, &client_side));
    r.run({});
    r.finish({});
    const json rep = json::parse(files.report);
    check(rep.dump().find("the mod is for the client side, this is the server") != std::string::npos, "the client mod is skipped with its reason");
    check(rep.dump().find("\"result\":40") == std::string::npos, "its maxStack edit is not applied");
}

std::vector<stage::Env::DataFile> server_side(const Schema& schema) {
    auto out = sections(schema);
    for (auto& f : out) f.side = "server";
    return out;
}
// R1c (recheck N1): on a game client Kiln.Host's registry order includes side "server" mods (RegistryHost's order resolves
// with hosted server mods on by default), because a hosted world's server runs in the client process. With Kiln.Host's
// order the stage decides by membership in that order only; the side check is for the folders' order (no .NET).
void hosting_client_keeps_ordered_server_mods(const std::string& dir) {
    TreeView view = world();
    Files files;
    stage::Env env = env_of(&view, &files, dir, &server_side);
    env.side = "client";
    stage::Runner r(std::move(env));
    check(r.set_order(R"([{"mod":"bigmod","version":"2.0.0"},{"mod":"late","version":"1.0.0"}])") == KILN_OK, "set_order: Kiln.Host's order on a client with hosted server mods");
    r.run({});
    r.finish({});
    const json rep = json::parse(files.report);
    check(rep.dump().find("\"result\":160") != std::string::npos,
          "the server mods Kiln.Host ordered apply on the client (maxStack x4 then x2 = 160); refused: " + rep["refused"].dump().substr(0, 400));
}

// R2: the id table of a build is written once, when it is missing; a later start with registry content (cache miss)
// overwrites it with only the registries that start decoded.
void id_table_overwritten(const std::string& dir) {
    TreeView view = world();
    Files files;
    { stage::Runner r(env_of(&view, &files, dir, &no_sections)); r.run({}); r.finish({}); }
    const std::string rel = "ids-server-" + kExe + ".json";
    const json first = json::parse(files.data.at(rel));
    check(first["registries"].contains("recipes") && first["registries"].contains("items"), "the first start writes the full table: " + first["registries"].dump().substr(0, 200));
    TreeView view2 = world();
    Files files2;
    files2.data = files.data;
    { stage::Runner r(env_of(&view2, &files2, dir, &items_only)); r.run({}); r.finish({}); }
    const json second = json::parse(files2.data.at(rel));
    std::string regs;
    for (auto it = second["registries"].begin(); it != second["registries"].end(); ++it) regs += it.key() + " ";
    check(second["registries"].contains("recipes"), "a later start keeps the full id table (registries now: " + regs + ")");
}

// The per-build id table is complete whatever starts first (ruling 2026-11-21 19:11: written once per build when missing,
// also without registry content): since the lazy start (5abb8783) the unused-start table left out the reference tables
// that only a decode of references listed (perks, input categories), and so did a start whose content needs no reference.
void id_table_lists_reference_tables(const std::string& dir) {
    auto with_refs = [] {
        TreeView v = world();
        v.add("keen::Perk", g(900), {{"debugName", "Perk_Strong"}});
        json& reg = v.values[g(100)];
        reg["inputCategories"] = json::array({{{"debugName", "Wood"}, {"inputCategoryId", 77}}});
        // dev.32: the stations (keyed by GUID) and the skill nodes (no GUID: "#<node id>") are schema registries too
        v.add("keen::WorkshopRegistryResource", g(500),
              {{"workshops", json::array({{{"isNpc", false}, {"workshopGuid", g(501)}, {"workshopId", 0x4da88f73}, {"item", 0}, {"itemRef", "none"}}})}});
        v.add("keen::SkillTreeResource", g(600), {{"nodes", json::array({{{"id", 0x4475e8dd}, {"costs", 3}, {"maxLevel", 0}}})}});
        return v;
    };
    const std::string rel = "ids-server-" + kExe + ".json";
    for (auto data : {&no_sections, &items_only}) {
        TreeView view = with_refs();
        Files files;
        { stage::Runner r(env_of(&view, &files, dir, data)); r.run({}); r.finish({}); }
        const json t = json::parse(files.data.at(rel));
        const std::string what = data == &no_sections ? "an unused start" : "a start whose content needs no reference";
        check(t["registries"].contains("perks") && t["registries"]["perks"].contains(g(900)),
              what + " lists the perks in the build's id table: " + t["registries"].dump().substr(0, 300));
        check(t["registries"].contains("inputCategories") && t["registries"]["inputCategories"].size() == 1,
              what + " lists the input categories in the build's id table: " + t["registries"].dump().substr(0, 300));
        check(t["registries"].contains("items") && t["registries"].contains("drops"), what + " keeps every schema registry");
        check(t["registries"].contains("stations") && t["registries"]["stations"].value(g(501), "") == "game:station_4da88f73" &&
                  t["registries"].contains("skills") && t["registries"]["skills"].value("#" + std::to_string(0x4475e8ddu), "") == "game:skill_4475e8dd",
              what + " lists the stations and the skill nodes: " + t["registries"].value("skills", json::object()).dump() + " " +
                  t["registries"].value("stations", json::object()).dump());
    }
}

// R3: a submit whose owner Kiln cannot bind (t_submit passes bound_mod(owner) == "" for a revoked or unknown owner, or an
// owner whose id is no mod id) is accepted under whatever mod the batch names.
void submit_unbound_owner(const std::string& dir) {
    TreeView view = world();
    Files files;
    stage::Runner r(env_of(&view, &files, dir, &no_sections));
    uint32_t a = 0, b = 0;
    std::string rep;
    const KilnResult rc = r.submit(77, "", R"({"ir":1,"mod":"bigmod","stage":"alter","edits":[{"registry":"items","select":{"id":"game:plank"},"field":"maxStack","op":1}]})", &a, &b, &rep);
    check(rc != KILN_OK, "a batch from an owner with no bound mod is refused (rc " + std::to_string(rc) + ", accepted " + std::to_string(a) + ")");
}

// R4: kiln_registry.h: subscribe_ready's fn "runs at START ... and at each WORLDLOAD". Only START is delivered.
void worldload_ready_event(const std::string& dir) {
    TreeView view = world();
    Files files;
    stage::Runner r(env_of(&view, &files, dir, &sections));
    std::function<void(const Result&, bool)> live_report;
    const Result* live_result = nullptr;
    r.set_live([&](const stage::LiveHandoff& h) { live_report = h.report, live_result = h.result; });
    std::vector<stage::Phase> seen;
    uint64_t h = 0;
    check(r.subscribe_ready(5, [&](stage::Phase p, uint64_t) { seen.push_back(p); return true; }, &h) == KILN_OK, "subscribe_ready");
    r.run({});
    r.finish({});
    live_report(*live_result, true);  // the live back-end applied the live fields at a world load
    check(r.phase() == stage::Phase::WorldLoad, "phase worldLoad");
    check(seen.size() == 2 && seen[1] == stage::Phase::WorldLoad, "the ready callback saw START and WORLDLOAD (saw " + std::to_string(seen.size()) + " event(s))");
    const auto generation = r.generation();
    Result late = *live_result;
    late.fields.at({"items", "game:plank", "maxStack"}).result = Value::integer(42);
    live_report(late, false);
    std::string value;
    check(r.read(2, "items", "game:plank", "maxStack", &value) == KILN_OK && value == "42", "a late refresh updates the resolved view");
    check(r.generation() == generation && seen.size() == 2, "a late refresh keeps the generation and sends no ready event");
}
Files warm_id_table(const std::string& dir) {
    TreeView view = world(); Files files;
    stage::Runner runner(env_of(&view, &files, dir, &no_sections));
    runner.run({}); runner.finish({});
    return files;
}
void unused_live_base_is_lazy(const std::string& dir) {
    TreeView view = world(); Files files = warm_id_table(dir);
    auto env = env_of(&view, &files, dir, &no_sections);
    env.prepare_live = true;
    auto source = env.source; int opens = 0;
    env.source = [&](std::string* why) { ++opens; return source(why); };
    stage::Runner runner(std::move(env)); stage::LiveHandoff live;
    runner.set_live([&](const stage::LiveHandoff& handoff) { live = handoff; });
    runner.run({}); runner.finish({});
    check(opens == 0 && live.base->registries.empty() && live.decoded->empty(),
          "without registry users and with the build's id table, startup neither opens nor fully decodes the archive");
    check(bool(live.prepare), "an unused registry still supports explicit first-use preparation");
    stage::LiveData a, b; std::string whyA, whyB; bool okA = false, okB = false;
    std::thread one([&] { okA = live.prepare(&a, &whyA); });
    std::thread two([&] { okB = live.prepare(&b, &whyB); });
    one.join(); two.join();
    check(okA && okB && opens == 1 && !a.ids.empty() && a.ids == b.ids,
          "concurrent first-use requests share one serialized archive preparation");
    check(a.decoded.count("items.maxStack") && a.base.registries.at("items").size() == 2,
          "the lazy copied model includes stable fields and ids for native/manual users");
    check(!a.base.raw, "the detached stable live snapshot retains no raw resolver into a destroyed model");
}
// review4 NS1: when Kiln.Host names a mod whose code uses the registry API, the stage decodes the stable base at
// resources-ready (before kiln.content writes packs into resident resource pages), even with no stage edits.
void registry_users_decode_at_the_stage(const std::string& dir) {
    TreeView view = world(); Files files = warm_id_table(dir);
    auto env = env_of(&view, &files, dir, &no_sections);
    env.prepare_live = true;
    auto source = env.source; int opens = 0;
    env.source = [&](std::string* why) { ++opens; return source(why); };
    stage::Runner runner(std::move(env)); stage::LiveHandoff live;
    check(runner.set_order(R"([{"mod":"plain","version":"1.0.0"},{"mod":"author","version":"1.0.0","registry":true}])") == KILN_OK,
          "the order may flag registry users");
    runner.set_live([&](const stage::LiveHandoff& handoff) { live = handoff; });
    runner.run({}); runner.finish({});
    check(opens == 1 && !live.base->registries.empty() && live.decoded->count("items.maxStack"),
          "a declared registry user makes the stage decode the full stable base at resources-ready");
    check(!live.prepare, "no lazy first-use decode remains to read bytes Kiln wrote later");
    std::string value;
    check(runner.read(0, "items", "game:material_t1_wood_logs", "maxStack", &value) == KILN_OK && value == "10" && opens == 1,
          "reads use the stage's base without opening the archive again");
}
void full_live_base_uses_cache(const std::string& dir) {
    TreeView view = world(); Files files;
    auto env = env_of(&view, &files, dir, &items_only); env.prepare_live = true;
    { stage::Runner runner(std::move(env)); runner.run({}); runner.finish({}); }
    bool cached = false;
    for (const auto& [path, text] : files.data) cached |= path.rfind("cache/", 0) == 0;
    check(cached, "a full stable-field model is cached, not decoded on every registry start");
    TreeView again = world(); Files second; second.data = files.data;
    auto next = env_of(&again, &second, dir, &items_only); next.prepare_live = true;
    { stage::Runner runner(std::move(next)); runner.run({}); runner.finish({}); }
    bool hit = false;
    for (const auto& line : second.log) hit |= line.find("the cache holds") != std::string::npos;
    check(hit, "a second full decode with matching resources/listings uses the cache");
    TreeView changed = world(); changed.values[g(1)]["maxStackSize"] = 41;
    Files third; third.data = files.data;
    auto last = env_of(&changed, &third, dir, &items_only); last.prepare_live = true;
    stage::Runner runner(std::move(last)); runner.run({}); runner.finish({});
    std::string value;
    check(runner.read(0, "items", "game:material_t1_wood_logs", "maxStack", &value) == KILN_OK && value == "41",
          "changed resource bytes invalidate the full-model cache");
}
void entry_backend_admission_and_inherited_reads(const std::string& dir) {
    TreeView view = world(); Files files;
    auto env = env_of(&view, &files, dir, &no_sections);
    env.entry_support = [](const auto&, bool, auto*) { return true; };
    env.data = [](const Schema& schema) { return stage::read_sections("late", "1", {{"content/entries.json", R"({"registry":{"add":[{"registry":"items","entry":"add","id":"late:plank","like":"game:plank"}],"alter":[{"registry":"recipes","entry":"remove","select":"game:craft_planks"},{"registry":"loot","entry":"remove","select":"game:plank"}]}})"}}, schema); };
    stage::Runner runner(std::move(env)); runner.run({}); runner.finish({});
    const auto report = json::parse(files.report);
    check(report["added"].size() == 1 && report["removed"].size() == 2 && report["refused"].empty(), "the installed entry backend admits supported GameStart operations");
    std::string value;
    check(runner.read(2, "items", "late:plank", "maxStack", &value) == KILN_OK && value == "20", "stage resolved reads expose inherited fields of a new stable id");
}
void cache_without_entry_metadata_is_rejected(const std::string& dir) {
    TreeView first = world(); Files files;
    auto env = env_of(&first, &files, dir, &items_only); env.prepare_live = true;
    { stage::Runner runner(std::move(env)); runner.run({}); runner.finish({}); }
    for (auto& [path, text] : files.data) if (path.starts_with("cache/")) {
        auto cached = json::parse(text); cached["base"].erase("entryMetadata"); text = cached.dump();
    }
    TreeView second = world(); Files again; again.data = files.data;
    auto next = env_of(&second, &again, dir, &items_only); next.prepare_live = true;
    { stage::Runner runner(std::move(next)); runner.run({}); runner.finish({}); }
    bool hit = false;
    for (const auto& line : again.log) hit |= line.find("the cache holds") != std::string::npos;
    check(!hit, "a cached base without entry/group metadata is never reused");
}
void lazy_base_waits_for_live_handoff(const std::string& dir) {
    TreeView view = world(); Files files = warm_id_table(dir);
    auto env = env_of(&view, &files, dir, &no_sections); env.prepare_live = true;
    stage::Runner runner(std::move(env)); KilnResult during = KILN_OK;
    runner.set_live([&](const stage::LiveHandoff& handoff) {
        std::string value;
        std::thread reader([&] { during = runner.read(0, "items", "game:material_t1_wood_logs", "maxStack", &value); });
        reader.join();
        check(during == KILN_ERR_UNAVAILABLE && handoff.base->registries.empty(),
              "a polling reader cannot mutate the base while the live installer copies its handoff");
    });
    runner.run({}); runner.finish({});
    std::string value;
    check(runner.read(0, "items", "game:material_t1_wood_logs", "maxStack", &value) == KILN_OK && value == "10",
          "a reader can prepare the base after installation completes");
}
void full_live_cache_keeps_boot_targets(const std::string& dir) {
    TreeView first = world(); Files files;
    auto env = env_of(&first, &files, dir, &sections); env.prepare_live = true;
    { stage::Runner runner(std::move(env)); auto boot = runner.run({});
      check(!boot.empty(), "a fresh full decode locates the restart-field resource"); runner.finish({}); }
    TreeView second = world(); Files again; again.data = files.data;
    auto next = env_of(&second, &again, dir, &sections); next.prepare_live = true;
    stage::Runner runner(std::move(next)); auto boot = runner.run({});
    check(!boot.empty() && !boot[0].fields.empty() && boot[0].fields[0].field.registry == "balance",
          "a full-model cache hit still locates restart-field resources for the boot writer");
    runner.finish({});
}
void lazy_base_keeps_pre_patch_resource_bytes(const std::string& dir) {
    TreeView view = world(); Files files = warm_id_table(dir);
    auto env = env_of(&view, &files, dir, &no_sections); env.prepare_live = true;
    env.write_data = {}; // this fixture mutates the view's backing after the frozen copy; do not persist its synthetic digest
    stage::Runner runner(std::move(env)); stage::LiveHandoff live;
    runner.set_live([&](const stage::LiveHandoff& handoff) { live = handoff; });
    auto calls = std::make_shared<int>(0);
    stage::Runner::OldWritesFn old = [calls](model::Model&) {
        ++*calls;
        OldWrite write; write.kind = WriterKind::OldPatch; write.mod = "old"; write.who = "old/stack";
        write.field = {"items", "game:material_t1_wood_logs", "maxStack"}; write.op.mul = 2;
        return std::vector<OldWrite>{write};
    };
    runner.run(old, {{key_of(g(1), "keen::ItemInfo"), "keen::ItemInfo"}}); runner.finish({});
    check(*calls == 0 && live.base->registries.empty(), "unused startup freezes only old writers' resources without a full model decode");
    view.values[g(1)]["maxStackSize"] = 20; // the legacy boot writer now patches its resource
    stage::LiveData data; std::string why;
    check(live.prepare && live.prepare(&data, &why), "a first use can prepare the kept pre-patch source");
    check(*calls == 1 && data.result.fields.at({"items", "game:material_t1_wood_logs", "maxStack"}).result == Value::integer(20),
          "the legacy multiplier is composed and attributed once, not twice over its already-patched resource");
    std::string value;
    check(runner.read(0, "items", "game:material_t1_wood_logs", "maxStack", &value) == KILN_OK && value == "10",
          "the lazy base still holds the pre-patch value");
    check(runner.read(2, "items", "game:material_t1_wood_logs", "maxStack", &value) == KILN_OK && value == "20",
          "the resolved lazy view includes the named legacy writer");
}
// A source that says which of its kind are still held (not released, not destroyed) and what each read.
struct HeldSources {
    std::set<model::Source*> held;
};
class Recording : public model::Source {
   public:
    Recording(std::unique_ptr<model::Source> in, std::shared_ptr<HeldSources> held) : in_(std::move(in)), held_(std::move(held)) { held_->held.insert(this); }
    ~Recording() override { held_->held.erase(this); }
    const char* name() const override { return in_->name(); }
    bool list(const std::string& type, std::vector<KilnAssetKey>* out, std::string* why) override { return in_->list(type, out, why); }
    bool count(const model::Place& p, size_t* n, std::string* why) override { return in_->count(p, n, why); }
    bool member(const model::Place& p, const std::string& path, json* out, std::string* why) override { return in_->member(p, path, out, why); }
    std::string digest() override { return in_->digest(); }
    std::vector<std::pair<KilnAssetKey, std::string>> keys_read() const override { return in_->keys_read(); }
    bool touch(const KilnAssetKey& key, const std::string& type, std::string* why) override { return in_->touch(key, type, why); }
    void release() override { in_->release(); held_->held.erase(this); }

   private:
    std::unique_ptr<model::Source> in_;
    std::shared_ptr<HeldSources> held_;
};
// review4 nit: the first start of a new game build with a frozen boot writer writes the id ledger, which reads every
// resource; the source kept for the first-use decode holds only the frozen writers' resources, not the ledger's reads.
void ledger_reads_are_not_kept_for_first_use(const std::string& dir) {
    TreeView view = world(); Files files;  // no id table for this build yet
    auto env = env_of(&view, &files, dir, &no_sections); env.prepare_live = true;
    env.write_data = [&files](const std::string& rel, const std::string& t) {  // the ledger, not the synthetic cache digest
        if (rel.rfind("ids-", 0) == 0) files.data[rel] = t;
        return true;
    };
    auto held = std::make_shared<HeldSources>();
    auto source = env.source;
    env.source = [source, held](std::string* why) -> std::unique_ptr<model::Source> {
        auto s = source(why);
        return s ? std::make_unique<Recording>(std::move(s), held) : nullptr;
    };
    stage::Runner runner(std::move(env)); stage::LiveHandoff live;
    runner.set_live([&](const stage::LiveHandoff& handoff) { live = handoff; });
    stage::Runner::OldWritesFn old = [](model::Model&) {
        OldWrite write; write.kind = WriterKind::OldPatch; write.mod = "old"; write.who = "old/stack";
        write.field = {"items", "game:material_t1_wood_logs", "maxStack"}; write.op.mul = 2;
        return std::vector<OldWrite>{write};
    };
    const KilnAssetKey frozen = key_of(g(1), "keen::ItemInfo");
    runner.run(old, {{frozen, "keen::ItemInfo"}}); runner.finish({});
    check(files.data.count("ids-server-" + kExe + ".json"), "the first start of the build writes the id ledger");
    size_t reads = 0;
    for (model::Source* s : held->held) reads += s->keys_read().size();
    check(held->held.size() == 1 && reads == 1, "the source kept for first use holds the frozen resource only: " + std::to_string(held->held.size()) +
                                                     " sources, " + std::to_string(reads) + " resources");
    view.values[g(1)]["maxStackSize"] = 20;  // the legacy boot writer patches its resource
    stage::LiveData data; std::string why;
    check(live.prepare && live.prepare(&data, &why) && data.result.fields.at({"items", "game:material_t1_wood_logs", "maxStack"}).result == Value::integer(20),
          "the first use still composes over the frozen pre-patch bytes (10 x2 = 20, not 40): " + why);
}
// review4 nit (NS1 edge): a registry user's stage that cannot read the base cannot freeze the old boot writers' resources
// either; the first use then refuses, with that reason, rather than decode their patched bytes as the game's.
void flagged_stage_without_a_base_refuses_first_use(const std::string& dir) {
    TreeView view = world(); Files files = warm_id_table(dir);
    auto env = env_of(&view, &files, dir, &no_sections); env.prepare_live = true;
    env.write_data = {};
    auto source = env.source; int opens = 0;
    env.source = [&opens, source](std::string* why) -> std::unique_ptr<model::Source> {
        if (++opens == 1) { *why = "the archive view is not open"; return nullptr; }
        return source(why);
    };
    stage::Runner runner(std::move(env)); stage::LiveHandoff live;
    runner.set_order(R"([{"mod":"author","version":"1.0.0","registry":true}])");
    runner.set_live([&](const stage::LiveHandoff& handoff) { live = handoff; });
    auto calls = std::make_shared<int>(0);
    stage::Runner::OldWritesFn old = [calls](model::Model&) {
        ++*calls;
        OldWrite write; write.kind = WriterKind::OldPatch; write.mod = "old"; write.who = "old/stack";
        write.field = {"items", "game:material_t1_wood_logs", "maxStack"}; write.op.mul = 2;
        return std::vector<OldWrite>{write};
    };
    runner.run(old, {{key_of(g(1), "keen::ItemInfo"), "keen::ItemInfo"}}); runner.finish({});
    view.values[g(1)]["maxStackSize"] = 20;  // the legacy boot writer patched its resource
    stage::LiveData data; std::string why;
    const bool prepared = live.prepare && live.prepare(&data, &why);
    check(!prepared && why.find("could not be read at the stage") != std::string::npos,
          "the first use refuses with the reason instead of reading the patched bytes: " +
              (prepared ? "prepared, maxStack " + data.result.fields.at({"items", "game:material_t1_wood_logs", "maxStack"}).result.to_json().dump() : why));
}
void transaction_snapshot_is_not_worldload(const std::string& dir) {
    TreeView view = world();
    Files files;
    stage::Runner r(env_of(&view, &files, dir, &sections));
    stage::LiveHandoff live;
    r.set_live([&](const stage::LiveHandoff& h) { live = h; });
    std::vector<stage::Phase> seen;
    uint64_t handle = 0;
    r.subscribe_ready(5, [&](stage::Phase phase, uint64_t) { seen.push_back(phase); return true; }, &handle);
    r.run({}); r.finish({});
    check(bool(live.changed), "the live handoff has a transaction snapshot publisher");
    auto generation = r.generation();
    live.changed(*live.result, false);
    check(r.generation() == generation + 1 && r.phase() == stage::Phase::Start && seen.size() == 1,
          "a pre-world transaction advances the snapshot generation without WORLDLOAD");
    check(json::parse(files.report)["phase"] == "start", "the pre-world report does not claim a world loaded");
    live.report(*live.result, true);
    generation = r.generation();
    live.changed(*live.result, true);
    check(r.generation() == generation + 1 && seen.size() == 2,
          "an in-world transaction advances the generation without another ready event");
    check(json::parse(files.report)["phase"] == "worldLoad", "an in-world transaction preserves world report metadata");
}

// R6: the live handoff keeps what a hot reload must not lose (review finding 6): the refusals made outside compose, the restart
// fields as the boot back-end wrote them, and the stage's mapping check
void handoff_keeps_outside_refusals(const std::string& dir) {
    TreeView view = world();
    Files files;
    stage::Runner r(env_of(&view, &files, dir, &sections));
    stage::LiveHandoff got;
    r.set_live([&](const stage::LiveHandoff& h) { got = h; });
    const auto boot = r.run({});
    r.finish({{boot.at(0).fields.at(0).field, true, Value::integer(60), ""}});
    check(got.pre_refused && !got.pre_refused->empty(), "the handoff names the refusals made outside compose");
    bool unproven = false;
    for (const Refusal& x : *got.pre_refused) unproven = unproven || x.reason.find("Unproven") != std::string::npos;
    check(unproven, "among them the unproven field's");
    check(got.boot_fields && got.boot_fields->count({"balance", "game:balance", "levelCap"}) &&
              got.boot_fields->at({"balance", "game:balance", "levelCap"}).result == Value::integer(60), "the handoff has levelCap as the boot back-end wrote it");
    check(static_cast<bool>(got.check), "and the mapping check");
    Batch b;
    std::vector<Refusal> rf;
    std::string err;
    parse_batch(std::string(R"({"ir":1,"mod":"bigmod","stage":"alter","edits":[{"registry":"items","select":{"id":"game:plank"},"field":"fuel.burnTime","op":30}]})"), generated_schema(), &b, &rf, &err);
    got.check(&b, &rf);
    check(b.edits.empty() && !rf.empty(), "the check refuses a reloaded unproven edit");
}

// R5: the base cache's check touches only the resources the first decode read and compares their digest; the type
// listings (one resource per item, template, drop table) are in neither. A resource added to a listed type with the same
// exe, Kiln and regmap (another archive writer's new item) is a cache hit, and the stage's base, ids and globs miss it.
void cache_misses_added_resource(const std::string& dir) {
    TreeView view = world();
    Files files;
    { stage::Runner r(env_of(&view, &files, dir, &items_only)); r.run({}); r.finish({}); }
    bool cached = false;
    for (const auto& [k, v] : files.data) cached = cached || k.rfind("cache/", 0) == 0;
    check(cached, "the first start wrote the base cache");
    TreeView view2 = world();
    view2.add("keen::ItemInfo", g(3), {{"itemId", 1003}, {"objectId", g(3)}, {"debugName", "Hammer"}, {"maxStackSize", 5}, {"rarity", "Common"},
                                       {"damageSetup", {{"dmgMod", 0.0}, {"speedMod", 1.0}, {"isSet", false}}}, {"itemLevelRange", {{"minLevel", 1}, {"maxLevel", 1}}}});
    Files files2;
    files2.data = files.data;
    stage::Runner r2(env_of(&view2, &files2, dir, &items_only));
    r2.run({});
    r2.finish({});
    bool hit = false;
    for (const std::string& l : files2.log) hit = hit || l.find("the cache holds") != std::string::npos;
    std::string out;
    r2.select(0, "items", "\"game:*\"", &out);
    check(out.find("game:hammer") != std::string::npos, std::string("a new item resource is in the base (cache ") + (hit ? "hit" : "miss") + "; base items: " + out + ")");
}
// P2 lifecycle: revocation removes only that owner's queued work, even without a stage subscription.
void revoked_queue(const std::string& dir) {
    TreeView view = world();
    Files files;
    stage::Runner r(env_of(&view, &files, dir, &no_sections));
    const std::string gone = R"({"ir":1,"mod":"bigmod","stage":"alter","source":"gone-queue","edits":[
        {"registry":"items","select":"game:plank","field":"maxStack","op":99},
        {"registry":"items","select":"game:plank","field":"notAField","op":1}]})";
    const std::string kept = R"({"ir":1,"mod":"bigmod","stage":"alter","source":"kept-queue","edits":[
        {"registry":"items","select":"game:plank","field":"maxStack","op":{"mul":2}}]})";
    check(r.submit(77, "bigmod", gone, nullptr, nullptr, nullptr) == KILN_OK, "queue gone owner");
    check(r.submit(78, "bigmod", kept, nullptr, nullptr, nullptr) == KILN_OK, "queue live owner of the same mod");
    r.owner_gone(77);
    r.owner_gone(77);  // idempotent
    size_t warnings = 0;
    for (const auto& line : files.log)
        if (line.find("bigmod") != std::string::npos && line.find("owner 77") != std::string::npos &&
            line.find("1 queued batch") != std::string::npos && line.find("2 edit") != std::string::npos) ++warnings;
    check(warnings == 1, "one immediate warning names the mod, owner and dropped counts, even after repeated revoke");
    r.run({}); r.finish({});
    std::string value;
    const KilnResult rc = r.read(2, "items", "game:plank", "maxStack", &value);
    check(rc == KILN_OK && value == "40", "revocation drops the dead owner's queue, not the live owner's (got " + value + ")");
    check(r.counts().refused == 1 && files.report.find("queued batch") != std::string::npos,
          "discarding accepted queued work leaves a revocation diagnostic in the report");
    check(files.report.find("notAField") == std::string::npos, "the dead owner's original parse refusals go with its queued batch");
}

void revoked_only_queue(const std::string& dir) {
    TreeView view = world(); Files files;
    stage::Runner r(env_of(&view, &files, dir, &no_sections));
    check(r.submit(77, "bigmod", R"({"ir":1,"mod":"bigmod","stage":"alter","edits":[{"registry":"items","select":"game:plank","field":"maxStack","op":99}]})",
                   nullptr, nullptr, nullptr) == KILN_OK, "queue the only input");
    r.owner_gone(77); r.run({}); r.finish({});
    check(r.counts().edits == 0 && r.counts().refused == 1 && files.report.find("queued batch") != std::string::npos,
          "a completely emptied queue still reports why its accepted input disappeared");
}

void revoked_registration(const std::string& dir) {
    TreeView view = world(); Files files;
    stage::Runner r(env_of(&view, &files, dir, &no_sections));
    r.owner_gone(77);
    uint64_t h = 0;
    check(r.subscribe_stage(77, "bigmod", [](Stage, const View&) { return true; }, &h) == KILN_ERR_REVOKED, "a revoked stage subscriber is refused");
    check(r.subscribe_ready(77, [](stage::Phase, uint64_t) { return true; }, &h) == KILN_ERR_REVOKED, "a revoked ready subscriber is refused");
    uint32_t a = 99;
    check(r.submit(77, "bigmod", R"({"ir":1,"mod":"bigmod","stage":"alter","edits":[]})", &a, nullptr, nullptr) == KILN_ERR_REVOKED && a == 0,
          "a revoked submit returns REVOKED and takes no edits");
}

void ready_snapshot_revoke(const std::string& dir) {
    TreeView view = world(); Files files;
    stage::Runner r(env_of(&view, &files, dir, &no_sections));
    uint64_t first = 0, second = 0; int calls = 0;
    r.subscribe_ready(1, [&](stage::Phase, uint64_t) { r.owner_gone(2); return true; }, &first);
    r.subscribe_ready(2, [&](stage::Phase, uint64_t) { ++calls; return true; }, &second);
    r.run({}); r.finish({});
    check(calls == 0, "a ready dispatch snapshot must not call an owner revoked by an earlier callback");
    check(r.unsubscribe(second) == KILN_ERR_NOT_FOUND, "revocation removes the ready subscription");
}

void ready_snapshot_unsubscribe(const std::string& dir) {
    TreeView view = world(); Files files;
    stage::Runner r(env_of(&view, &files, dir, &no_sections));
    uint64_t first = 0, second = 0; int calls = 0;
    r.subscribe_ready(1, [&](stage::Phase, uint64_t) { r.unsubscribe(second); return true; }, &first);
    r.subscribe_ready(2, [&](stage::Phase, uint64_t) { ++calls; return true; }, &second);
    r.run({}); r.finish({});
    check(calls == 0, "a ready snapshot rechecks unsubscribe before dispatch");
}

void stage_self_revoke(const std::string& dir, Stage turn) {
    TreeView view = world(); Files files;
    stage::Runner r(env_of(&view, &files, dir, &no_sections));
    uint64_t h = 0; int calls = 0;
    r.subscribe_stage(77, "bigmod", [&](Stage s, const View&) {
        if (s != turn) return true;
        ++calls;
        const std::string ir = std::string(R"({"ir":1,"mod":"bigmod","stage":")") + stage_name(s) + R"(","edits":[
            {"registry":"items","select":"game:plank","field":"maxStack","op":99}]})";
        check(r.submit(77, "bigmod", ir, nullptr, nullptr, nullptr) == KILN_OK, "in-flight submit before revocation");
        r.owner_gone(77);
        return true;
    }, &h);
    r.run({}); r.finish({});
    std::string value;
    const KilnResult rc = r.read(2, "items", "game:plank", "maxStack", &value);
    check(rc == KILN_OK && value == "20", "self-revoke drops the uncommitted callback turn (got " + value + ")");
    check(calls == 1, "no later stage calls the revoked owner");
    check(files.report.find("revoked") != std::string::npos, "the dropped turn is reported");
}

void ready_fault_removed(const std::string& dir) {
    TreeView view = world(); Files files;
    stage::Runner r(env_of(&view, &files, dir, &sections));
    std::function<void(const Result&, bool)> publish;
    Result result;
    r.set_live([&](const stage::LiveHandoff& h) { publish = h.report; result = *h.result; });
    int calls = 0; uint64_t h = 0;
    r.subscribe_ready(77, [&](stage::Phase, uint64_t) { ++calls; return false; }, &h);
    r.run({}); r.finish({});
    publish(result, true);
    check(calls == 1, "a faulted ready callback is removed, not called at world load");
    check(r.unsubscribe(h) == KILN_ERR_NOT_FOUND, "the faulted ready handle is gone");
}
KilnOwnerRetiringFn retiring_fn = nullptr;
void* retiring_user = nullptr;
KilnResult KILN_CALL fake_retiring(KilnOwner observer, KilnOwnerRetiringFn fn, void* user, KilnHandle* out) {
    check(observer == 42, "the observer belongs to the content service");
    retiring_fn = fn; retiring_user = user; *out = 7;
    return KILN_OK;
}
KilnResult KILN_CALL refuse_retiring(KilnOwner, KilnOwnerRetiringFn, void*, KilnHandle*) { return KILN_ERR_REVOKED; }

void owner_notification_bridge(const std::string& dir) {
    TreeView view = world(); Files files;
    stage::Runner r(env_of(&view, &files, dir, &no_sections));
    KilnHooksApi hooks{};
    check(stage::bind_owner_cleanup(nullptr, 42, &r) == KILN_ERR_UNAVAILABLE, "no lifecycle service fails closed");
    hooks.header.size = offsetof(KilnHooksApi, subscribe_owner_retiring);
    hooks.subscribe_owner_retiring = &fake_retiring;
    check(stage::bind_owner_cleanup(&hooks, 42, &r) == KILN_ERR_UNAVAILABLE, "an older table is never called past its size");
    hooks.header.size = sizeof hooks;
    hooks.subscribe_owner_retiring = &refuse_retiring;
    check(stage::bind_owner_cleanup(&hooks, 42, &r) == KILN_ERR_REVOKED, "lifecycle registration refusal is propagated");
    hooks.subscribe_owner_retiring = &fake_retiring;
    check(stage::bind_owner_cleanup(&hooks, 42, &r) == KILN_OK && retiring_fn && retiring_user == &r, "the real binding keeps the runner as its user");
    uint64_t h = 0; int calls = 0;
    r.subscribe_stage(77, "bigmod", [&](Stage, const View&) { ++calls; return true; }, &h);
    r.subscribe_ready(77, [&](stage::Phase, uint64_t) { ++calls; return true; }, &h);
    r.submit(77, "bigmod", R"({"ir":1,"mod":"bigmod","stage":"alter","edits":[{"registry":"items","select":"game:plank","field":"maxStack","op":99}]})", nullptr, nullptr, nullptr);
    retiring_fn(77, retiring_user);  // the callback the core receives, not a direct call to owner_gone
    r.run({}); r.finish({});
    check(calls == 0 && r.counts().edits == 0, "the core notification drops queued work and both callback kinds");
}

void core_dead_before_notification(const std::string& dir) {
    TreeView view = world(); Files files;
    bool alive = true;
    auto env = env_of(&view, &files, dir, &no_sections);
    env.owner_alive = [&](uint64_t) { return alive; };
    stage::Runner r(std::move(env));
    uint64_t h = 0; int calls = 0;
    r.subscribe_stage(77, "bigmod", [&](Stage, const View&) { ++calls; return true; }, &h);
    r.subscribe_ready(77, [&](stage::Phase, uint64_t) { ++calls; return true; }, &h);
    r.submit(77, "bigmod", R"({"ir":1,"mod":"bigmod","stage":"alter","edits":[{"registry":"items","select":"game:plank","field":"maxStack","op":99}]})", nullptr, nullptr, nullptr);
    alive = false;  // core marked dead; another retiring observer can still be running before ours
    check(r.subscribe_ready(77, [](stage::Phase, uint64_t) { return true; }, &h) == KILN_ERR_REVOKED, "core liveness is checked on admission");
    r.run({}); r.finish({});
    check(calls == 0 && r.counts().edits == 0, "dead owners are not dispatched or drained while notification is pending");
    check(files.report.find("queued batch") != std::string::npos, "core-dead queued work is diagnosed even before retirement notification");
}

void worldload_snapshot_revoke(const std::string& dir) {
    TreeView view = world(); Files files;
    stage::Runner r(env_of(&view, &files, dir, &sections));
    Result result; std::function<void(const Result&, bool)> publish;
    r.set_live([&](const stage::LiveHandoff& h) { result = *h.result; publish = h.report; });
    uint64_t h = 0; int calls = 0;
    r.subscribe_ready(1, [&](stage::Phase p, uint64_t) { if (p == stage::Phase::WorldLoad) r.owner_gone(2); return true; }, &h);
    r.subscribe_ready(2, [&](stage::Phase, uint64_t) { ++calls; return true; }, &h);
    r.run({}); r.finish({});
    check(calls == 1, "both owners see START while alive");
    publish(result, true);
    check(calls == 1, "WORLDLOAD rechecks owner revocation in its dispatch snapshot too");
    r.owner_gone(1);
    publish(result, true);  // revocation does not discard already committed stage edits
    check(r.counts().edits != 0, "committed registry edits remain after subscriber revocation");
}
// Callback lifetime/replay: cancelled turns retain neither edits nor executable author state.
void callback_cancellation_is_not_replayed(const std::string& dir, int mode) {
    TreeView view = world(); Files files;
    stage::Runner r(env_of(&view, &files, dir, &no_sections));
    uint64_t handle = 0; int calls = 0;
    r.subscribe_stage(77, "bigmod", [&](Stage s, const View&) {
        ++calls;
        if (s != Stage::Alter) return true;
        check(r.submit(77, "bigmod", R"({"ir":1,"mod":"bigmod","stage":"alter","edits":[{"registry":"items","select":"game:plank","field":"maxStack","op":{"add":5}}]})",
                       nullptr, nullptr, nullptr) == KILN_OK, "submit a valid turn before cancellation");
        if (mode == 0) return false;
        if (mode == 1) r.owner_gone(77);
        else { std::thread retiring([&] { r.owner_gone(77); }); retiring.join(); }
        return true;
    }, &handle);
    std::vector<ModInput> kept; BaseModel base;
    r.set_live([&](const stage::LiveHandoff& h) { kept = *h.inputs; base = *h.base; });
    r.run({}); r.finish({});
    std::string value;
    check(r.read(2, "items", "game:plank", "maxStack", &value) == KILN_OK && value == "20", "cancelled turn leaves the start value untouched");
    check(calls == 2 && r.unsubscribe(handle) == KILN_ERR_NOT_FOUND, "fault/revoke removes the later final turn");
    const auto replay = compose(generated_schema(), base, {}, kept);
    const auto field = replay.fields.find({"items", "game:plank", "maxStack"});
    check(field == replay.fields.end() || field->second.result == Value::integer(20), "faulted or revoked turns are absent from live replay");
    for (const auto& mod : kept) {
        check(!mod.code, "the handoff keeps no function into the author");
        for (const auto& batch : mod.batches) check(batch.edits.empty(), "no cancelled callback edit was recorded");
    }
}

void stage_cross_removal(const std::string& dir, bool revoke) {
    TreeView view = world(); Files files;
    stage::Runner r(env_of(&view, &files, dir, &no_sections));
    uint64_t first = 0, second = 0; std::vector<Stage> calls;
    r.subscribe_stage(1, "oldmod", [&](Stage s, const View&) {
        if (s == Stage::Alter) {
            if (revoke) r.owner_gone(2);
            else check(r.unsubscribe(second) == KILN_OK, "an earlier stage callback unsubscribes a later one");
        }
        return true;
    }, &first);
    r.subscribe_stage(2, "bigmod", [&](Stage s, const View&) {
        calls.push_back(s);
        if (s == Stage::Alter)
            r.submit(2, "bigmod", R"({"ir":1,"mod":"bigmod","stage":"alter","edits":[{"registry":"items","select":"game:plank","field":"maxStack","op":99}]})", nullptr, nullptr, nullptr);
        return true;
    }, &second);
    r.run({}); r.finish({});
    check(calls == std::vector<Stage>{Stage::Add}, "a removed later subscriber gets neither alter nor final");
    std::string value;
    check(r.read(2, "items", "game:plank", "maxStack", &value) == KILN_OK && value == "20", "the skipped stage subscriber cannot write");
}

void stage_self_unsubscribe(const std::string& dir) {
    TreeView view = world(); Files files;
    stage::Runner r(env_of(&view, &files, dir, &no_sections));
    uint64_t handle = 0; std::vector<Stage> calls;
    r.subscribe_stage(77, "bigmod", [&](Stage s, const View&) {
        calls.push_back(s);
        if (s == Stage::Alter) {
            check(r.submit(77, "bigmod", R"({"ir":1,"mod":"bigmod","stage":"alter","edits":[{"registry":"items","select":"game:plank","field":"maxStack","op":{"add":5}}]})", nullptr, nullptr, nullptr) == KILN_OK, "self-unsubscribing turn submits");
            check(r.unsubscribe(handle) == KILN_OK, "a callback can unsubscribe itself");
        }
        return true;
    }, &handle);
    std::vector<ModInput> kept; BaseModel base;
    r.set_live([&](const stage::LiveHandoff& h) { kept = *h.inputs; base = *h.base; });
    r.run({}); r.finish({});
    check(calls == std::vector<Stage>{Stage::Add, Stage::Alter}, "self-unsubscribe removes final but does not revoke the in-flight turn");
    std::string value;
    check(r.read(2, "items", "game:plank", "maxStack", &value) == KILN_OK && value == "25", "the successful in-flight turn stays");
    const auto replay = compose(generated_schema(), base, {}, kept);
    check(replay.fields.at({"items", "game:plank", "maxStack"}).result == Value::integer(25), "self-unsubscribe retains one successful replay, not zero or two");
}

void core_dead_admission_all(const std::string& dir) {
    TreeView view = world(); Files files;
    auto env = env_of(&view, &files, dir, &no_sections);
    env.owner_alive = [](uint64_t) { return false; };
    stage::Runner r(std::move(env));
    uint64_t h = 0; uint32_t accepted = 99;
    check(r.subscribe_stage(77, "bigmod", [](Stage, const View&) { return true; }, &h) == KILN_ERR_REVOKED, "stage admission checks the core before notification");
    check(r.subscribe_ready(77, [](stage::Phase, uint64_t) { return true; }, &h) == KILN_ERR_REVOKED, "ready admission checks the core before notification");
    check(r.submit(77, "bigmod", R"({"ir":1,"mod":"bigmod","stage":"alter","edits":[]})", &accepted, nullptr, nullptr) == KILN_ERR_REVOKED && accepted == 0,
          "submit admission checks the core before notification");
}

void cancelled_turn_keeps_other_owner_refusal(const std::string& dir) {
    for (bool revoke : {false, true}) {
        TreeView view = world(); Files files;
        stage::Runner r(env_of(&view, &files, dir, &no_sections));
        uint64_t handle = 0;
        r.subscribe_stage(77, "bigmod", [&](Stage s, const View&) {
            if (s != Stage::Alter) return true;
            check(r.submit(78, "bigmod", R"({"ir":1,"mod":"bigmod","stage":"alter","source":"other-owner","edits":[{"registry":"items","select":"game:plank","field":"notAField","op":99}]})", nullptr, nullptr, nullptr) == KILN_OK,
                  "a different live owner of the same mod submits a refusal");
            if (revoke) r.owner_gone(77);
            return revoke;
        }, &handle);
        r.run({}); r.finish({});
        check(files.report.find("other-owner") != std::string::npos && files.report.find("notAField") != std::string::npos,
              "fault and revoke both preserve the other live owner's refusal diagnostics");
    }
}

void callback_batches_survive_handoff(const std::string& dir) {
    TreeView view = world(); Files files;
    stage::Runner r(env_of(&view, &files, dir, &no_sections));
    uint64_t h = 0; int calls = 0;
    r.subscribe_stage(77, "bigmod", [&](Stage s, const View&) {
        if (s == Stage::Alter) {
            ++calls;
            check(r.submit(77, "bigmod", R"({"ir":1,"mod":"bigmod","stage":"alter","edits":[{"registry":"items","select":"game:plank","field":"maxStack","op":{"add":5}}]})", nullptr, nullptr, nullptr) == KILN_OK, "callback submit");
        }
        return true;
    }, &h);
    std::vector<ModInput> kept; BaseModel base;
    r.set_live([&](const stage::LiveHandoff& x) { kept = *x.inputs; base = *x.base; });
    r.run({}); r.finish({});
    std::string value;
    check(calls == 1 && r.read(2, "items", "game:plank", "maxStack", &value) == KILN_OK && value == "25", "start applies the relative callback once");
    size_t edits = 0;
    for (const auto& mod : kept) {
        check(!mod.code, "the successful handoff retains no author callback");
        for (const auto& batch : mod.batches) edits += batch.edits.size();
    }
    check(edits == 1, "exactly one successful callback edit is recorded");
    for (int repeat = 0; repeat < 2; ++repeat) {
        const Result replay = compose(generated_schema(), base, {}, kept);
        const auto it = replay.fields.find({"items", "game:plank", "maxStack"});
        check(it != replay.fields.end() && it->second.result == Value::integer(25), "replay applies the relative callback once without accumulating it");
    }
}
}  // namespace

int main(int argc, char** argv) {
    const std::string dir = argc > 1 ? argv[1] : "regmap";
    int failed = 0;
    const std::pair<const char*, std::function<void()>> groups[] = {
        {"R1 skipped_mod_data_applied", [&] { skipped_mod_data_applied(dir); }},
        {"R1b other_side_mod_skipped", [&] { other_side_mod_skipped(dir); }},
        {"R1c hosting_client_keeps_ordered_server_mods", [&] { hosting_client_keeps_ordered_server_mods(dir); }},
        {"R2 id_table_overwritten", [&] { id_table_overwritten(dir); }},
        {"R2b id_table_lists_reference_tables", [&] { id_table_lists_reference_tables(dir); }},
        {"R3 submit_unbound_owner", [&] { submit_unbound_owner(dir); }},
        {"R4 worldload_ready_event", [&] { worldload_ready_event(dir); }},
        {"P2 transaction_snapshot_is_not_worldload", [&] { transaction_snapshot_is_not_worldload(dir); }},
        {"P2 unused_live_base_is_lazy", [&] { unused_live_base_is_lazy(dir); }},
        {"NS1 registry_users_decode_at_the_stage", [&] { registry_users_decode_at_the_stage(dir); }},
        {"P2 full_live_base_uses_cache", [&] { full_live_base_uses_cache(dir); }},
        {"P2 cache_without_entry_metadata_is_rejected", [&] { cache_without_entry_metadata_is_rejected(dir); }},
        {"P2 entry_backend_admission_and_inherited_reads", [&] { entry_backend_admission_and_inherited_reads(dir); }},
        {"P2 full_live_cache_keeps_boot_targets", [&] { full_live_cache_keeps_boot_targets(dir); }},
        {"P2 lazy_base_waits_for_live_handoff", [&] { lazy_base_waits_for_live_handoff(dir); }},
        {"P2 lazy_base_keeps_pre_patch_resource_bytes", [&] { lazy_base_keeps_pre_patch_resource_bytes(dir); }},
        {"nit ledger_reads_are_not_kept_for_first_use", [&] { ledger_reads_are_not_kept_for_first_use(dir); }},
        {"nit flagged_stage_without_a_base_refuses_first_use", [&] { flagged_stage_without_a_base_refuses_first_use(dir); }},
        {"R6 handoff_keeps_outside_refusals", [&] { handoff_keeps_outside_refusals(dir); }},
        {"R5 cache_misses_added_resource", [&] { cache_misses_added_resource(dir); }},
        {"P2 revoked_queue", [&] { revoked_queue(dir); }},
        {"P2 revoked_only_queue", [&] { revoked_only_queue(dir); }},
        {"P2 revoked_registration", [&] { revoked_registration(dir); }},
        {"P2 ready_snapshot_revoke", [&] { ready_snapshot_revoke(dir); }},
        {"P2 ready_snapshot_unsubscribe", [&] { ready_snapshot_unsubscribe(dir); }},
        {"P2 stage_self_revoke", [&] { stage_self_revoke(dir, Stage::Add); }},
        {"P2 stage_self_revoke_valid_edit", [&] { stage_self_revoke(dir, Stage::Alter); }},
        {"P2 ready_fault_removed", [&] { ready_fault_removed(dir); }},
        {"P2 owner_notification_bridge", [&] { owner_notification_bridge(dir); }},
        {"P2 core_dead_before_notification", [&] { core_dead_before_notification(dir); }},
        {"P2 worldload_snapshot_revoke", [&] { worldload_snapshot_revoke(dir); }},
        {"P2 callback_batches_survive_handoff", [&] { callback_batches_survive_handoff(dir); }},
        {"P2 faulted_callback_not_replayed", [&] { callback_cancellation_is_not_replayed(dir, 0); }},
        {"P2 revoked_callback_not_replayed", [&] { callback_cancellation_is_not_replayed(dir, 1); }},
        {"P2 concurrent_revoke_not_replayed", [&] { callback_cancellation_is_not_replayed(dir, 2); }},
        {"P2 stage_cross_unsubscribe", [&] { stage_cross_removal(dir, false); }},
        {"P2 stage_cross_revoke", [&] { stage_cross_removal(dir, true); }},
        {"P2 stage_self_unsubscribe", [&] { stage_self_unsubscribe(dir); }},
        {"P2 core_dead_admission_all", [&] { core_dead_admission_all(dir); }},
        {"P2 cancelled_turn_keeps_other_owner_refusal", [&] { cancelled_turn_keeps_other_owner_refusal(dir); }}};
    for (const auto& [name, fn] : groups) {
        try {
            fn();
            std::printf("registry-stage-review: PASS %s\n", name);
        } catch (const std::exception& e) {
            ++failed;
            std::printf("registry-stage-review: FAIL %s: %s\n", name, e.what());
        }
    }
    std::printf("registry-stage-review: %d check(s), %d failed group(s)\n", checks, failed);
    return failed ? 1 : 0;
}
