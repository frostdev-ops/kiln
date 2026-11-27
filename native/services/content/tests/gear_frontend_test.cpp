// SPDX-License-Identifier: MIT
// A data pack's "gearRules" as a registry front-end, on the host, with ASan/UBSan: generated gearRules, each lowered as the
// boot lowers it (gear.cpp lower_rules) and written into a balancing table both ways:
//   - as its resource patch: every value set the way gamedata_bootpatch.cpp's build sets it (gd::resolve, gd::parse_leaf on
//     the bytes as they are);
//   - by the registry: the document's rows as the boot bridge makes them (boot_bridge.cpp old_writes, front rows), the boot
//     resources the stage hands over for them (stage.cpp), and the boot back-end's write (boothook::write) through Kiln's
//     mapping (regmap/common/balance.json).
// Every document the registry takes gives the same bytes as its resource patch; the ones it does not take are named with
// their reason. Then the report (registry.json frontEnds, the writer's frontEnd mark) and the composition around a front-end
// (a registry edit of the same field, an exclusive one) as with the resource patch alone.
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <memory>
#include <random>
#include <stdexcept>

#include "../gamedata.h"
#include "../gear.h"
#include "../registry/boot_hooks.h"
#include "../registry/formulas.h"
#include "../registry/schema_ir.h"
#include "../registry/stage.h"

namespace kc {
std::string guid_text(const uint8_t g[16]);
bool guid_parse(const std::string& s, uint8_t out[16]);
namespace rbtest {
size_t gear_balance_type();  // keen::BalancingTable with the gear rules' members (registry_model_fakes.cpp)
}
}  // namespace kc

namespace {
using namespace kc::registry;
int checks = 0;
void check(bool ok, const std::string& why) {
    if (!ok) throw std::runtime_error(why);
    ++checks;
}
bool has(const std::string& s, const std::string& part) { return s.find(part) != std::string::npos; }
// A member or element of a report (null when it is missing): never nlohmann's assertion on a const document.
const json& at(const json& j, const char* k) {
    static const json none;
    if (!j.is_object()) return none;
    const auto it = j.find(k);
    return it == j.end() ? none : *it;
}
const json& nth(const json& j, size_t i) {
    static const json none;
    return j.is_array() && i < j.size() ? j[i] : none;
}
std::string text(const json& j) { return j.is_string() ? j.get<std::string>() : j.dump(); }
const char* kBalance = "82706b40-61b1-4b8f-8b23-dcec6971bda1";
const std::string kExe = "f0d824be7fcd48ebfe2458ff5c28da26060c2074e3ec1fcac587e5a1d508aeb2";
const char* kRarities[] = {"common", "uncommon", "rare", "epic", "legendary"};
const std::string kDoc = "gearmod/gearRules@gear.json";

KilnAssetKey key_of(const std::string& guid, const std::string& type) {
    KilnAssetKey k{};
    kc::guid_parse(guid, k.guid);
    k.type = kiln::boot_gd::fnv1a32(type);
    return k;
}

struct TreeView final : kiln::boot_view::BaseView {
    std::map<std::string, json> values;
    std::map<std::string, std::vector<std::string>> types;
    const char* source() const override { return "live"; }
    bool ready(std::string*) override { return true; }
    std::string digest() override { return kiln::boot_gd::hex_of(kiln::boot_gd::sha256(values.begin()->second.dump())); }
    KilnResult tree(std::string_view q, size_t cap, size_t, uint64_t, std::string* out, size_t* req, size_t*, std::string* why) override {
        const std::string guid(q.substr(9, 36));
        const auto it = values.find(guid);
        if (it == values.end()) return *why = "none", KILN_ERR_NOT_FOUND;
        const std::string text = json{{"value", it->second}}.dump();
        if (text.size() + 1 > cap) return *req = text.size() + 1, KILN_ERR_BUFFER_TOO_SMALL;
        *out = text;
        return KILN_OK;
    }
    KilnResult bytes(const KilnAssetKey&, size_t, std::string*, std::string* why) override { return *why = "trees", KILN_ERR_UNAVAILABLE; }
    void release() override {}
};

// The game's values (the balancing table the builds Kiln measures ship: content.md "Gear rules")
struct Game {
    float durability[5] = {0.9f, 0.95f, 1.0f, 1.05f, 1.1f};
    float socket[5] = {0.0f, 0.05f, 0.25f, 0.5f, 1.0f};
    float start = 145, growth = 2, creep = 0.15f, weapons = 1, armor = 1, rings = 1, advanced = 8;
    uint32_t seed = 8, advanced_from = 4, level_cap = 50;
};
std::vector<uint8_t> bytes_of(const Game& g, size_t size) {
    std::vector<uint8_t> b(size, 0);
    auto f = [&](size_t at, float v) { std::memcpy(b.data() + at, &v, 4); };
    auto u = [&](size_t at, uint32_t v) { std::memcpy(b.data() + at, &v, 4); };
    for (int i = 0; i < 5; ++i) f(4 * i, g.durability[i]), f(20 + 4 * i, g.socket[i]);
    f(40, g.start), f(44, g.growth), u(48, g.seed), f(52, g.creep), f(56, g.weapons), f(60, g.armor), f(64, g.rings), f(68, g.advanced);
    u(72, g.advanced_from), u(76, g.level_cap);
    return b;
}
json tree_of(const Game& g) {
    json d = json::object(), s = json::object();
    for (int i = 0; i < 5; ++i) d[kRarities[i]] = g.durability[i], s[kRarities[i]] = g.socket[i];
    return {{"rarityDurabilityMod", d}, {"gemSlotProbability", s}, {"durabilityStart", g.start}, {"durabilityGrowth", g.growth},
            {"perkUpgradeCostSeed", g.seed}, {"perkCostCreep", g.creep}, {"perkCostFactorWeaponsShield", g.weapons},
            {"perkCostFactorArmor", g.armor}, {"perkCostFactorRings", g.rings}, {"advancedPerkCostNominator", g.advanced},
            {"advancedPerkCostStartLevel", g.advanced_from}, {"playerLevelCap", g.level_cap},
            {"genericWeaponSeed", 10.0}, {"powerCreepWeapon", 0.15}, {"armorSetup", {{"offset", 23.0}, {"gradient", 15.0}}}};
}

struct Files {
    std::map<std::string, std::string> data;
    std::string report;
    std::vector<std::string> log;
};
stage::Env env_of(TreeView* view, Files* files, const std::string& regmap_dir) {
    stage::Env e;
    e.side = "server", e.exe_sha256 = kExe, e.kiln_version = "0.2.0-test", e.pid = 7;
    e.now = [] { return std::string("2026-11-26T00:00:00Z"); };
    e.source = [view](std::string*) {
        return model::tree_source(*view, [view](const std::string& type, std::vector<KilnAssetKey>* out, std::string*) {
            for (const std::string& gu : view->types[type]) out->push_back(key_of(gu, type));
            return true;
        });
    };
    e.regmap = [regmap_dir](map::RegMap* out, std::vector<std::string>* errs) { return map::load_dir(regmap_dir, "server", kExe, out, errs); };
    e.folders = [] { return std::vector<std::pair<std::string, std::string>>{{"gearmod", "1.0.0"}, {"bigmod", "2.0.0"}}; };
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

boothook::OldDoc doc_of(const json& patch, const std::string& mod = "gearmod", const std::string& key = "gearRules@gear.json") {
    boothook::OldDoc d;
    d.mod = mod, d.key = key;
    d.res = key_of(kBalance, "keen::BalancingTable");
    d.type = "keen::BalancingTable";
    for (auto& [path, value] : patch["set"].items()) d.sets.emplace_back(path, value);
    return d;
}

// The resource patch's way: every value set on the bytes as they are (gamedata_bootpatch.cpp build's sets).
std::vector<uint8_t> patched(std::vector<uint8_t> b, const boothook::OldDoc& d) {
    const kc::gd::TType* t = kc::gd::type_named("keen::BalancingTable");
    const uintptr_t lo = reinterpret_cast<uintptr_t>(b.data());
    for (const auto& [path, value] : d.sets) {
        std::vector<kc::gd::Resolved> found;
        std::string err;
        check(kc::gd::resolve(kc::gd::Root::Plain, kc::gd::Node{t, lo}, path, &found, &err) && found.size() == 1, "the patch's member " + path + ": " + err);
        kc::Field f;
        check(kc::gd::leaf_of(found[0].node.t, &f), path + " is a scalar");
        uint8_t out[16] = {};
        bool reset = false;
        check(kc::gd::parse_leaf(f, kc::gjson::parse(value.dump()), reinterpret_cast<const uint8_t*>(found[0].node.addr), out, &reset, &err), path + ": " + err);
        std::memcpy(reinterpret_cast<void*>(found[0].node.addr), out, f.bytes);
    }
    return b;
}

// One start of the stage with these documents below it (and a mod's registry section, when given), the front-end candidate
// kDoc unless fronts is off. *boot: what the stage hands the boot back-end; *fronts: its front-end verdicts before the apply.
// The start keeps its runner (and the view and files it reads): a boot resource's fields point into the runner's mapping, as
// they do in the process, where the stage's runner lives as long as the process.
struct Start {
    std::vector<stage::BootResource> boot;
    std::vector<stage::FrontEnd> fronts;
    json report;
    std::vector<std::string> log;
    std::unique_ptr<TreeView> view;
    std::unique_ptr<Files> files;
    std::unique_ptr<stage::Runner> runner;
};
Start start(const std::string& dir, const Game& g, const std::vector<boothook::OldDoc>& docs, bool fronts_on, const std::string& section = "",
            std::function<void(stage::Runner&, const std::vector<stage::BootResource>&)> finish = {},
            std::function<void(map::RegMap&)> adjust = {}) {
    Start out;
    out.view = std::make_unique<TreeView>();
    out.files = std::make_unique<Files>();
    TreeView& view = *out.view;
    Files& files = *out.files;
    view.types["keen::BalancingTable"].push_back(kBalance);
    view.values[kBalance] = tree_of(g);
    stage::Env e = env_of(&view, &files, dir);
    if (!section.empty())
        e.data = [section](const Schema& schema) { return stage::read_sections("bigmod", "2.0.0", {{"content/b.json", section}}, schema); };
    if (adjust) {
        auto load = e.regmap;
        e.regmap = [load, adjust](map::RegMap* rm, std::vector<std::string>* errs) {
            const bool ok = load(rm, errs);
            adjust(*rm);
            return ok;
        };
    }
    out.runner = std::make_unique<stage::Runner>(std::move(e));
    stage::Runner& r = *out.runner;
    std::vector<std::pair<std::string, std::string>> candidates;
    std::set<std::string> ids;
    if (fronts_on)
        for (const auto& d : docs)
            if (boothook::front_end_key(d.key)) candidates.emplace_back(d.mod + "/" + d.key, d.type), ids.insert(d.mod + "/" + d.key);
    out.boot = r.run([&](model::Model& m) { return boothook::old_writes(docs, m, &ids); }, {}, candidates);
    out.fronts = r.front_ends();
    if (finish) finish(r, out.boot);
    else r.finish({});
    out.report = files.report.empty() ? json() : json::parse(files.report);
    out.log = files.log;
    return out;
}

// ---- generated gearRules ----
struct Gen {
    std::mt19937 rng{20261126};
    double uni(double a, double b) { return std::uniform_real_distribution<double>(a, b)(rng); }
    int pick(int n) { return std::uniform_int_distribution<int>(0, n - 1)(rng); }
    bool coin(double p = 0.5) { return uni(0, 1) < p; }
    // a value of [lo, hi] (whole: a whole number), or an op on the game's value; bad_clamp: may give min above max
    json value(double lo, double hi, bool whole, bool bad_clamp) {
        const int k = pick(10);
        if (k < 4) return whole ? json(static_cast<int64_t>(std::llround(uni(lo, hi)))) : json(uni(lo, hi));
        if (k == 4) return json{{"set", whole ? json(static_cast<int64_t>(std::llround(uni(lo, hi)))) : json(uni(lo, hi))}};
        json op = json::object();
        if (coin(0.7)) op["mul"] = coin(0.3) ? json(static_cast<int64_t>(pick(4))) : json(uni(0, 4));
        if (coin(0.4)) op["add"] = whole && coin(0.5) ? json(static_cast<int64_t>(pick(21) - 10)) : json(uni(-(hi - lo) / 10, (hi - lo) / 10));
        if (coin(0.3)) op["min"] = uni(lo, hi);
        if (coin(0.3)) op["max"] = uni(lo, hi);
        if (op.contains("min") && op.contains("max") && op["min"].get<double>() > op["max"].get<double>() && !bad_clamp) std::swap(op["min"], op["max"]);
        if (coin(0.2)) op["raiseOnly"] = true;
        else if (coin(0.2)) op["lowerOnly"] = true;
        if (op.empty()) op["mul"] = 1.5;
        return op;
    }
    json rarities(double lo, double hi, bool bad) {
        if (coin()) {
            json l = json::array();
            const int n = 1 + pick(5);
            for (int i = 0; i < n; ++i) l.push_back(coin(0.2) ? json(nullptr) : value(lo, hi, false, bad));
            return l;
        }
        json o = json::object();
        const int n = 1 + pick(3);
        for (int i = 0; i < n; ++i) o[coin(0.2) ? (coin() ? "Common" : "EPIC") : kRarities[pick(5)]] = value(lo, hi, false, bad);  // any case
        return o;
    }
    json rules(bool bad) {
        json r = json::object();
        while (r.empty()) {
            if (coin(0.5)) r["rarityDurability"] = rarities(0.01, 100, bad);
            if (coin(0.5)) r["socketChance"] = rarities(0, 1, bad);
            if (coin(0.4)) {
                json d = json::object();
                if (coin(0.7)) d["start"] = value(1, 100000, false, bad);
                if (coin(0.7)) d["growth"] = value(0, 100000, false, bad);
                if (!d.empty()) r["durability"] = d;
            }
            if (coin(0.4)) {
                json u = json::object();
                if (coin(0.5)) u["seed"] = value(0, 100000, true, bad);
                if (coin(0.4)) u["creep"] = value(0, 100, false, bad);
                for (const char* k : {"weapons", "armor", "rings"})
                    if (coin(0.3)) u[k] = value(0, 100, false, bad);
                if (coin(0.3)) u["advanced"] = value(0, 100000, false, bad);
                if (coin(0.3)) u["advancedFrom"] = value(0, 5, true, bad);
                if (!u.empty()) r["unlockCost"] = u;
            }
        }
        return r;
    }
};

// Every generated gearRules: the registry takes it, and its bytes are the resource patch's.
void both_ways(const std::string& dir, size_t table) {
    Gen gen;
    const Game g;
    const std::vector<uint8_t> base = bytes_of(g, table);
    size_t same = 0, lowered = 0, kept_clamp = 0, unchanged = 0;
    for (int n = 0; n < 1500; ++n) {
        const bool bad = n % 10 == 9;  // some with an op whose min is above its max: the registry does not take those
        const json rules = gen.rules(bad);
        std::vector<std::string> errors;
        const auto patch = kc::gear::lower_rules(kc::gear::json::parse(rules.dump()), &errors);
        if (!errors.empty()) continue;  // the boot leaves such a file out before any of this (bootpatch_test)
        ++lowered;
        const boothook::OldDoc d = doc_of(json::parse(patch.dump()));
        const std::vector<uint8_t> old_way = patched(base, d);
        const Start s = start(dir, g, {d}, true);
        check(s.fronts.size() == 1, "one candidate");
        const std::string what = "gearRules " + rules.dump();
        bool clamp = false;
        for (const auto& [path, value] : d.sets)
            clamp = clamp || (value.is_object() && value.contains("min") && value.contains("max") && value["min"].get<double>() > value["max"].get<double>());
        if (clamp) {
            check(s.fronts[0].state == "resourcePatch" && has(s.fronts[0].why, "an operation whose min is above its max") && s.boot.empty(),
                  "an op whose min is above its max stays a resource patch, named: " + what + ": " + s.fronts[0].why);
            ++kept_clamp;
            continue;
        }
        check(s.fronts[0].state == "pending" && s.fronts[0].fields == d.sets.size(), "the registry takes it, every value one field: " + what + " " + s.fronts[0].why);
        check(s.boot.size() == 1 && s.boot[0].front == std::vector<std::string>{kDoc} && s.boot[0].fields.size() == d.sets.size(),
              "one resource, one boot field per value, the document named: " + what);
        for (const auto& f : s.boot[0].fields)
            check(f.rows.size() == 1 && f.rows[0].front && f.rows[0].kind == WriterKind::OldPatch && f.rows[0].who == kDoc, "its rows are its own front rows: " + f.field.text());
        std::vector<uint8_t> registry_way = base;
        std::vector<stage::BootOutcome> out;
        std::vector<std::string> notes;
        // one outcome per value, every one ok; a value the table already holds (a raiseOnly below the game's number) is not
        // written again (compare-before-write), so the writes can be fewer than the values: the bytes below decide
        const size_t written = boothook::write(s.boot[0].key, s.boot[0].type, &registry_way, s.boot[0], &out, &notes);
        check(out.size() == d.sets.size() && written <= d.sets.size() && std::all_of(out.begin(), out.end(), [](const auto& o) { return o.ok; }),
              "every value's outcome ok: " + what);
        unchanged += d.sets.size() - written;
        size_t at = 0;
        while (at < base.size() && old_way[at] == registry_way[at]) ++at;
        check(at == base.size(), "the same bytes both ways: " + what + " (first difference at byte " + std::to_string(at) + ")");
        ++same;
    }
    check(lowered > 1000 && same + kept_clamp == lowered && kept_clamp > 5, "most generated rules lower and every one is one of the two");
    std::printf("gear-frontend: %zu generated gearRules: %zu byte-identical through the registry (%zu values already held, not rewritten), %zu kept as "
                "resource patches (min above max)\n",
                lowered, same, unchanged, kept_clamp);
}

// The cases each key of gearRules has, at the edges of their ranges, one at a time.
void each_key(const std::string& dir, size_t table) {
    const Game g;
    const std::vector<uint8_t> base = bytes_of(g, table);
    const char* cases[] = {
        R"({"rarityDurability": [0.01, null, 1, 1.25, 100]})", R"({"rarityDurability": {"Legendary": {"mul": 2, "max": 2}}})",
        R"({"socketChance": [0, 0, 0, 0, 1]})",              R"({"socketChance": {"epic": {"add": 0.25, "max": 1}}})",
        R"({"durability": {"start": 1, "growth": 0}})",      R"({"durability": {"start": 100000, "growth": {"mul": 2.5}}})",
        R"({"unlockCost": {"seed": 0, "advancedFrom": 5}})", R"({"unlockCost": {"seed": {"mul": 1.5}}})",
        R"({"unlockCost": {"seed": {"mul": 1.3125}}})",      R"({"unlockCost": {"seed": {"add": 0.5}, "advancedFrom": {"add": -0.5}}})",
        R"({"unlockCost": {"creep": 100, "weapons": 0.5, "armor": 2, "rings": 0.25, "advanced": 100000}})",
        R"({"unlockCost": {"seed": {"set": 12}, "creep": {"raiseOnly": true, "mul": 0.5}, "rings": {"lowerOnly": true, "mul": 3}}})",
    };
    for (const char* text : cases) {
        std::vector<std::string> errors;
        const auto patch = kc::gear::lower_rules(kc::gear::json::parse(text), &errors);
        check(errors.empty(), std::string("lowers: ") + text);
        const boothook::OldDoc d = doc_of(json::parse(patch.dump()));
        const Start s = start(dir, g, {d}, true);
        check(s.fronts.size() == 1 && s.fronts[0].state == "pending" && s.boot.size() == 1, std::string("taken: ") + text + " " + (s.fronts.empty() ? "" : s.fronts[0].why));
        std::vector<uint8_t> registry_way = base;
        std::vector<stage::BootOutcome> out;
        std::vector<std::string> notes;
        boothook::write(s.boot[0].key, s.boot[0].type, &registry_way, s.boot[0], &out, &notes);
        check(registry_way == patched(base, d), std::string("the same bytes both ways: ") + text);
    }
    // every gearRules key names the registry field Kiln's mapping holds for the member it patches (gear.h kRuleFields)
    std::vector<std::string> errors;
    const auto all = kc::gear::lower_rules(kc::gear::json::parse(R"({"rarityDurability": [1, 1, 1, 1, 1], "socketChance": [0, 0, 0, 0, 0],
        "durability": {"start": 1, "growth": 1}, "unlockCost": {"seed": 1, "creep": 1, "weapons": 1, "armor": 1, "rings": 1, "advanced": 1, "advancedFrom": 1}})"), &errors);
    const Start s = start(dir, g, {doc_of(json::parse(all.dump()))}, true);
    std::set<std::string> fields;
    for (const auto& f : s.boot.at(0).fields) fields.insert(f.field.field);
    const std::set<std::string> want = {"rarityDurability.common", "rarityDurability.uncommon", "rarityDurability.rare", "rarityDurability.epic",
                                        "rarityDurability.legendary", "socketChance.common", "socketChance.uncommon", "socketChance.rare",
                                        "socketChance.epic", "socketChance.legendary", "durability.start", "durability.growth", "unlockCost.seed",
                                        "unlockCost.creep", "unlockCost.weapons", "unlockCost.armor", "unlockCost.rings", "unlockCost.advanced",
                                        "unlockCost.advancedFrom"};
    check(fields == want, "every gearRules key is the balance field of the same name (19 fields)");
}

// What the stage does not take, and why (Kiln words).
void not_taken(const std::string& dir) {
    const Game g;
    auto one = [&](const json& sets, const std::string& part, std::function<void(map::RegMap&)> adjust = {}) {
        const Start s = start(dir, g, {doc_of({{"set", sets}})}, true, "", {}, adjust);
        check(s.fronts.size() == 1 && s.fronts[0].state == "resourcePatch" && has(s.fronts[0].why, part) && s.boot.empty(),
              "kept (" + part + "): " + (s.fronts.empty() ? std::string("none") : s.fronts[0].why));
        const json fe = s.report.value("frontEnds", json::array());
        check(fe.size() == 1 && fe[0]["state"] == "resourcePatch" && has(fe[0].value("reason", ""), part), "registry.json frontEnds says so: " + fe.dump());
        for (const std::string& l : s.log)
            if (has(l, "gearRules@")) check(!has(l, "rarityDurabilityMod") && !has(l, "durabilityStart") && !has(l, "playerLevelCap"), "no game member in the log: " + l);
    };
    one({{"durabilityStart", 200}, {"noSuchMember", 1}}, "a value it sets has no registry field on this game build");
    one({{"durabilityStart", 200}}, "balance.durability.start is not proven on this game build",
        [](map::RegMap& rm) { rm.build.rows["balance.durability.start"] = map::BuildRow::Changed; });
    one({{"durabilityStart", {{"min", 300}, {"max", 200}}}}, "balance.durability.start: an operation whose min is above its max");
    // the switch off (front-ends.off, a lab's before): no candidate, no frontEnds, the resource patch as before
    const Start off = start(dir, g, {doc_of({{"set", {{"durabilityStart", 200}}}})}, false);
    check(off.fronts.empty() && off.boot.empty() && !off.report.contains("frontEnds"), "no candidates: nothing for the boot back-end, no frontEnds");
    // a document of another kind (an OnBoot resource patch) is never a candidate
    const Start other = start(dir, g, {doc_of({{"set", {{"durabilityStart", 200}}}}, "gearmod", "cap")}, true);
    check(other.fronts.empty() && other.boot.empty(), "an ordinary resource patch is not a front-end");
}

// The report after the apply: taken (frontEnd on its writer row, the fields with the base and the result) or kept.
void report(const std::string& dir) {
    const Game g;
    const boothook::OldDoc d = doc_of({{"set", {{"durabilityStart", 200}, {"gemSlotProbability.epic", 0.75}}}});
    auto finish_as = [](const std::string& state, const std::string& why) {
        return [state, why](stage::Runner& r, const std::vector<stage::BootResource>& boot) {
            std::vector<stage::BootOutcome> out;
            if (state == "registry")
                for (const auto& f : boot.at(0).fields)
                    out.push_back({f.field, true, f.field.field == "durability.start" ? Value::number(200) : Value::number(0.75f), ""});
            r.finish(out, {{"gearmod", "gearRules@gear.json", state, why, 0}});
        };
    };
    const Start taken = start(dir, g, {d}, true, "", finish_as("registry", ""));
    const json fe = at(taken.report, "frontEnds");
    check(fe.size() == 1 && fe[0] == json{{"mod", "gearmod"}, {"key", "gearRules@gear.json"}, {"state", "registry"}, {"fields", 2}}, "registry.json frontEnds: " + fe.dump());
    bool start_row = false;
    for (const auto& f : at(taken.report, "fields"))
        if (at(f, "registry") == "balance" && at(f, "field") == "durability.start") {
            const json& w = nth(at(f, "writers"), 0);
            start_row = at(at(f, "base"), "value") == 145 && at(f, "result") == 200 && at(f, "writers").size() == 1 && at(w, "kind") == "oldPatch" &&
                        at(w, "who") == kDoc && at(w, "frontEnd") == true;
        }
    check(start_row, "the field's row: base 145, result 200, its writer the document, marked frontEnd: " + at(taken.report, "fields").dump());
    bool said = false;
    for (const std::string& l : taken.log) said = said || has(l, "gearRules@gear.json is written by the registry's boot back-end (2 field(s)");
    check(said, "kiln.log says the registry wrote it");
    // the same start with the front-end off: the join's registry part is the same (front rows are no registry edits)
    const Start off = start(dir, g, {d}, false);
    check(!at(taken.report, "bootSync").is_null() && at(taken.report, "bootSync") == at(off.report, "bootSync"), "bootSync is the one a start without the front-end sends: front-end rows are no registry edits");
    // the apply kept the resource patch (the bytes differed): no frontEnd mark, the reason in frontEnds
    const Start kept = start(dir, g, {d}, true, "", finish_as("resourcePatch", "the registry's bytes differ from its resource patch's (byte 40 of the resource)"));
    const json& kfe = nth(at(kept.report, "frontEnds"), 0);
    check(at(kfe, "state") == "resourcePatch" && has(text(at(kfe, "reason")), "byte 40"), "kept, with the apply's reason: " + kfe.dump());
    for (const auto& f : at(kept.report, "fields"))
        for (const auto& w : at(f, "writers")) check(!w.contains("frontEnd"), "a kept document's rows are not marked: " + f.dump());
    // an apply that never named it (its resource could not be read): kept
    const Start silent = start(dir, g, {d}, true, "", [](stage::Runner& r, const std::vector<stage::BootResource>&) { r.finish({}); });
    const json& sfe = nth(at(silent.report, "frontEnds"), 0);
    check(at(sfe, "state") == "resourcePatch" && has(text(at(sfe, "reason")), "did not hand it to the registry"), "not named by the apply: kept: " + sfe.dump());
}

// The boot back-end's write of what the stage handed over, into these bytes (the copy the apply builds: without the front-end
// documents when there are any, else with every document), then finish with its outcomes and each front-end document as the
// registry's: what the apply does when both ways give the same bytes.
std::function<void(stage::Runner&, const std::vector<stage::BootResource>&)> written_finish(std::vector<uint8_t> bytes) {
    return [bytes](stage::Runner& r, const std::vector<stage::BootResource>& boot) {
        std::vector<uint8_t> copy = bytes;
        std::vector<stage::BootOutcome> out;
        std::vector<std::string> notes;
        std::vector<stage::FrontEnd> fronts;
        for (const auto& rb : boot) {
            boothook::write(rb.key, rb.type, &copy, rb, &out, &notes);
            for (const std::string& id : rb.front) fronts.push_back({id.substr(0, id.find('/')), id.substr(id.find('/') + 1), "registry", "", 0});
        }
        r.finish(out, fronts);
    };
}

// The composition around a front-end is the resource patch's: a registry edit of the same field composes on it and wins as
// over the resource patch alone, an exclusive one conflicts as with it; only the front rows are marked and carried.
void composition(const std::string& dir, size_t table) {
    const Game g;
    const std::vector<uint8_t> base = bytes_of(g, table);
    const boothook::OldDoc d = doc_of({{"set", {{"durabilityStart", 200}}}});
    auto strip = [](json j) {
        if (j.is_object() && j.contains("fields"))
            for (auto& f : j["fields"])
                if (f.contains("writers"))
                    for (auto& w : f["writers"]) w.erase("frontEnd");
        return j;
    };
    const std::string mul = R"({"registry": {"alter": [{"registry": "balance", "select": {"id": "game:balance"}, "field": "durability.start", "op": {"mul": 2}}]}})";
    // the apply's two copies: the front-end's built without the document, the old way's with its resource patch
    const Start on = start(dir, g, {d}, true, mul, written_finish(base)), off = start(dir, g, {d}, false, mul, written_finish(patched(base, d)));
    check(on.boot.size() == 1 && on.boot[0].fields.size() == 1 && on.boot[0].fields[0].rows.size() == 2 && on.boot[0].fields[0].rows[0].front &&
              on.boot[0].fields[0].rows[1].kind == WriterKind::Registry && on.boot[0].front == std::vector<std::string>{kDoc},
          "the field's boot rows: the front row, then the registry edit");
    check(off.boot.size() == 1 && off.boot[0].fields[0].rows.size() == 1 && off.boot[0].front.empty(), "without the front-end: the registry edit alone, as before");
    check(!at(on.report, "fields").empty() && at(strip(on.report), "fields") == at(strip(off.report), "fields") &&
              at(on.report, "conflicts") == at(off.report, "conflicts"),
          "the same rows and conflict lines as over the resource patch: " + at(on.report, "conflicts").dump());
    check(!at(on.report, "bootSync").is_null() && at(on.report, "bootSync") == at(off.report, "bootSync"),
          "and the same join part: " + at(on.report, "bootSync").dump() + " " + at(off.report, "bootSync").dump());
    bool doubled = false;
    for (const auto& f : at(on.report, "fields"))
        if (at(f, "field") == "durability.start") doubled = at(f, "result") == 400 && at(nth(at(f, "writers"), 0), "frontEnd") == true;
    check(doubled, "the registry's mul composes on the document's 200: 400, the document's row marked: " + at(on.report, "fields").dump());
    const std::string excl = R"({"registry": {"alter": [{"registry": "balance", "select": {"id": "game:balance"}, "field": "durability.start", "op": 300, "exclusive": true}]}})";
    const Start eon = start(dir, g, {d}, true, excl), eoff = start(dir, g, {d}, false, excl);
    check(at(eon.report, "refused") == at(eoff.report, "refused") && at(eon.report, "conflicts") == at(eoff.report, "conflicts"),
          "an exclusive edit meets the front-end as it meets the resource patch: " + at(eon.report, "refused").dump() + " " + at(eoff.report, "refused").dump());
    // a front-end document and another mod's resource patch of another member: only the front-end's rows are carried
    const boothook::OldDoc cap = doc_of({{"set", {{"playerLevelCap", 60}}}}, "capmod", "cap");
    const Start mixed = start(dir, g, {cap, d}, true);
    check(mixed.boot.size() == 1 && mixed.boot[0].fields.size() == 1 && mixed.boot[0].fields[0].field.field == "durability.start", "the other document stays the resource patch's");
}
// The gear section's vocabulary is the items registry's (registry BRIEF P5, "gear lowered to item fields"): each gear key writes
// the member Kiln's mapping (regmap/common/items.json) holds for the items field of the same meaning, and the power and armour it
// solves are the registry formulas' (one solver, the same constants). Where one registry edit per gear key would not give the
// same numbers, the case is named here (docs/kiln/services/content.md "Gear values and the items registry").
void gear_vocabulary(const std::string& dir) {
    map::RegMap rm;
    std::vector<std::string> errs;
    check(map::load_dir(dir, "server", kExe, &rm, &errs), "the regmap loads");
    const auto& rows = rm.registries.at("items").rows;
    using M = std::map<std::string, double>;
    auto reader = [](const M& m) {
        return [m](const std::string& p) -> std::optional<double> {
            const auto it = m.find(p);
            if (it == m.end()) return std::nullopt;
            return it->second;
        };
    };
    const M sword = {{"itemLevelRange.minLevel", 5}, {"itemLevelRange.maxLevel", 5}, {"damageSetup.isSet", 1}, {"damageSetup.dmgMod", 1},
                     {"damageSetup.speedMod", 1.25}, {"damageSetup.distribution.physicalCutDamage", 1}, {"armorSetup.isSet", 0}, {"blockSetup.quality", 0.5}};
    const M chest = {{"itemLevelRange.minLevel", 10}, {"itemLevelRange.maxLevel", 10}, {"damageSetup.isSet", 0}, {"armorSetup.isSet", 1},
                     {"armorSetup.distribution.physical", 0.18}, {"armorSetup.distribution.fog", 0.1}};
    struct Key {
        const char* rule;
        bool armour;
        std::vector<std::string> fields;
    };
    const Key keys[] = {
        {R"({"power": 30})", false, {"power"}},
        {R"({"speed": 1.5})", false, {"speed"}},
        {R"({"damage": {"cut": 0.5, "pierce": 0.1, "blunt": 0.1, "fire": 0.1, "ice": 0.1, "shroud": 0.05, "lightning": 0.05, "poison": 0, "healing": 0.2}})", false,
         {"damage.cut", "damage.pierce", "damage.blunt", "damage.fire", "damage.ice", "damage.shroud", "damage.lightning", "damage.poison", "damage.healing"}},
        {R"({"damage": {"wood": 20, "stone": 10, "metal": 5, "explosion": 1}})", false, {"damage.wood", "damage.stone", "damage.metal", "damage.explosion"}},
        {R"({"armor": {"physical": 40, "magical": 1, "cut": 1, "pierce": 1, "blunt": 1, "fire": 1, "ice": 1, "shroud": 5, "lightning": 1}})", true,
         {"armor.physical", "armor.magical", "armor.cut", "armor.pierce", "armor.blunt", "armor.fire", "armor.ice", "armor.shroud", "armor.lightning"}},
        {R"({"block": 0.75})", false, {"block"}},
        {R"({"rarity": "epic"})", false, {"rarity"}},
        {R"({"levels": [3, 9]})", false, {"levels.min", "levels.max"}},
        {R"({"rolled": false})", false, {"rolled"}},
        {R"({"socket": true})", false, {"socket"}},
        {R"({"maxStack": 5})", false, {"maxStack"}},
    };
    for (const Key& k : keys) {
        const kc::gear::Expansion x = kc::gear::expand(kc::gear::json::parse(k.rule), reader(k.armour ? chest : sword));
        check(x.errors.empty(), std::string("expands: ") + k.rule);
        std::set<std::string> got, want;
        for (auto& [path, v] : x.fields.items()) got.insert(path);
        for (const std::string& f : k.fields) {
            const auto& row = rows.at(f);
            std::string p = row.paths.at(0);
            // a struct with one bool is that bool at its parent's path in the store (pitfall P2): items.socket's flag
            if (row.conv == "bool" && p.size() > 6 && p.ends_with(".isSet") && f == "socket") p.resize(p.size() - 6);
            want.insert(p);
        }
        std::string g, w;
        for (const auto& p : got) g += p + " ";
        for (const auto& p : want) w += p + " ";
        check(got == want, std::string("gear ") + k.rule + " writes the members of items " + w + "(it writes " + g + ")");
    }
    {
        const kc::gear::Expansion x = kc::gear::expand(kc::gear::json::parse(R"({"staminaCost": 12})"), reader(sword));
        const auto& row = rows.at("staminaCost");
        check(x.fields.contains(row.paths.at(0) + "." + row.config) && row.conv == "impactConfig", "staminaCost is the item's StaminaCost config, as items.staminaCost maps it");
    }
    // power and armour: what the gear section solves is the registry formula's write at the same level and speed, and reads back
    const kc::gear::Constants game;
    size_t solved = 0;
    for (int level = 1; level <= 55; ++level)
        for (double speed : {0.8, 1.0, 1.25, 1.5}) {  // speeds a float holds exactly (the game's member is a float)
            M w = sword;
            w["itemLevelRange.minLevel"] = w["itemLevelRange.maxLevel"] = level;
            w["damageSetup.speedMod"] = speed;
            for (int power = 0; power <= 400; power += 7) {
                const kc::gear::Expansion x = kc::gear::expand(kc::gear::json{{"power", power}}, reader(w));
                double reg = 0, back = 0;
                check(formulas::write("gear.power", power, {0, speed, static_cast<double>(level)}, game, &reg) &&
                          x.fields["damageSetup.dmgMod"].get<double>() == reg,
                      "power " + std::to_string(power) + " at level " + std::to_string(level) + ": the gear section's dmgMod is items.power's");
                check(formulas::read("gear.power", {static_cast<float>(reg), speed, static_cast<double>(level)}, game, &back) && back == power, "and reads back");
                ++solved;
            }
            M a = chest;
            a["itemLevelRange.minLevel"] = a["itemLevelRange.maxLevel"] = level;
            for (int armor = 0; armor <= 300; armor += 11) {
                const kc::gear::Expansion x = kc::gear::expand(kc::gear::json{{"armor", {{"physical", armor}}}}, reader(a));
                double reg = 0;
                check(formulas::write("gear.armor", armor, {0, static_cast<double>(level)}, game, &reg) &&
                          x.fields["armorSetup.distribution.physical"].get<double>() == reg,
                      "armour " + std::to_string(armor) + " at level " + std::to_string(level) + ": the gear section's share is items.armor.physical's");
                ++solved;
            }
        }
    // Where one registry edit per gear key is not the same game value (why the gear section is not switched to the registry):
    // 1. speed and power in one rule: the gear section solves the power at the rule's number, the registry at the speed the
    //    game holds (a float); for a speed a float cannot hold exactly (1.1), about a third of the dmgMods differ by a bit
    size_t speed_differs = 0, speed_cases = 0;
    for (int level = 1; level <= 55; ++level) {
        M w = sword;
        w["itemLevelRange.minLevel"] = w["itemLevelRange.maxLevel"] = level;
        for (int power = 0; power <= 399; power += 3) {
            const kc::gear::Expansion x = kc::gear::expand(kc::gear::json{{"speed", 1.1}, {"power", power}}, reader(w));
            double reg = 0;
            check(formulas::write("gear.power", power, {0, static_cast<double>(static_cast<float>(1.1)), static_cast<double>(level)}, game, &reg), "solves");
            ++speed_cases;
            speed_differs += static_cast<float>(x.fields["damageSetup.dmgMod"].get<double>()) != static_cast<float>(reg);
        }
    }
    check(speed_differs > 0, "speed 1.1 with power: the gear section and the registry solve at different speeds (" + std::to_string(speed_differs) + " of " +
                                 std::to_string(speed_cases) + " differ)");
    // 2. a relative power (or armour) with levels in one rule: the gear section takes the game's tooltip number at the item's own
    //    level, the registry the number at the new level
    {
        const kc::gear::Expansion x = kc::gear::expand(kc::gear::json::parse(R"({"levels": [10, 10], "power": {"mul": 1.5}})"), reader(sword));
        const int gear_power = kc::gear::power_of(x.fields["damageSetup.dmgMod"].get<double>(), 1.25, 10);
        const int registry_power = static_cast<int>(std::llround(1.5 * kc::gear::power_of(1, 1.25, 10)));
        check(gear_power == static_cast<int>(std::llround(1.5 * kc::gear::power_of(1, 1.25, 5))) && gear_power != registry_power,
              "levels with a relative power: the gear section's base is the power at level 5 (" + std::to_string(gear_power) + "), the registry's at level 10 (" +
                  std::to_string(registry_power) + ")");
    }
    std::printf("gear-frontend: the gear section's 13 keys are items registry fields (member paths and formulas, %zu solved values equal)\n", solved);
}
}  // namespace

int main(int argc, char** argv) {
    try {
        if (argc < 2) throw std::runtime_error("usage: gear-frontend-test <regmap dir>");
        std::setvbuf(stdout, nullptr, _IONBF, 0);  // progress lines survive an abort
        const std::string dir = argv[1];
        const size_t table = kc::rbtest::gear_balance_type();
        gear_vocabulary(dir);
        each_key(dir, table);
        both_ways(dir, table);
        not_taken(dir);
        std::printf("gear-frontend: what the registry does not take is named (%d checks so far)\n", checks);
        report(dir);
        std::printf("gear-frontend: registry.json frontEnds and the frontEnd mark (%d checks so far)\n", checks);
        composition(dir, table);
    } catch (const std::exception& e) {
        std::fprintf(stderr, "FAIL %s\n", e.what());
        return 1;
    }
    std::printf("gear-frontend: %d checks, 0 failures\n", checks);
    return 0;
}
