// SPDX-License-Identifier: MIT
// Kiln's base rules (baserules.hpp) in the real collector and apply of gamedata_bootpatch.cpp, with fake boot and assets
// tables and a fake keen::BalancingTable shaped as the game's (altarsPerFlameLevel: BlobArray<u8>, 21 levels;
// buildzoneSizesPerAltarLevel: BlobArray<float3>, 6 altar levels). No game or Wine.
#include "../gamedata_bootpatch.cpp"
#include <cmath>
#include <cstdio>
#include <stdexcept>
#include <unistd.h>
namespace kc {
const KilnApi* g_api = nullptr;
KilnOwner g_owner = 7;
void logf(int, const char*, ...) {}
std::vector<std::filesystem::path> g_mod_dirs;
std::vector<std::filesystem::path> mod_folder_list() { return g_mod_dirs; }
// the balancing table's GUID is "table", any other text a GUID of its own
bool guid_parse(const std::string& s, uint8_t out[16]) {
    if (s == "bad") return false;
    std::memset(out, s == "table" ? 1 : static_cast<int>(2 + s.size() % 200), 16);
    return true;
}
std::string guid_text(const uint8_t* g) { return g[0] == 1 ? "table" : "g" + std::to_string(g[0]); }
uint32_t fnv1a(const void* p, size_t n) {
    uint32_t h = 2166136261u;
    for (size_t i = 0; i < n; ++i) h = (h ^ static_cast<const uint8_t*>(p)[i]) * 16777619u;
    return h;
}
namespace gd {
TType u8t, f32t, table_t;
const TType* TType::resolved() const { return this; }
const TType* TType::inner() const { return nullptr; }
const TField* TType::field(const std::string&) const { return nullptr; }
const TType* TField::type() const { return nullptr; }
const TType* type_named(const std::string& name) { return name == "keen::BalancingTable" ? &table_t : nullptr; }  // no keen::ItemInfo: types unchecked
bool leaf_of(const TType* t, Field* f) {
    if (t == &u8t) return f->bytes = 1, f->vt = KILN_CV_INT, true;
    if (t == &f32t) return f->bytes = 4, f->vt = KILN_CV_FLOAT, true;
    return false;
}
gjson leaf_json(const Field& f, const uint8_t* b, bool) {
    if (f.vt == KILN_CV_INT) return *b;
    float v;
    std::memcpy(&v, b, 4);
    return v;
}
}  // namespace gd
}  // namespace kc
// The table: 21 altar counts (flame levels 0..20), then 6 sizes of 3 floats (altar levels 0..5).
struct Table {
    uint8_t altars[21];
    uint8_t pad[3];
    float sizes[6][3];
};
static_assert(offsetof(Table, sizes) == 24);
namespace kc::gd {
// "altarsPerFlameLevel[*]" / "[i]", "buildzoneSizesPerAltarLevel[*].x" / "[i].y", "value" (the first altar count),
// "missing...": no such member
bool resolve(Root, Node root, const std::string& path, std::vector<Resolved>* out, std::string* err, bool*) {
    auto list = [&](const std::string& name, size_t n, size_t stride, size_t at, const TType* t, std::string rest) {
        if (path.rfind(name + "[", 0) != 0) return false;
        const size_t close = path.find(']');
        const std::string ix = path.substr(name.size() + 1, close - name.size() - 1);
        if (path.substr(close + 1) != rest && !(rest == "*axis" && path.size() == close + 3 && path[close + 1] == '.')) return false;
        size_t axis = 0;
        if (rest == "*axis") axis = static_cast<size_t>(path[close + 2] - 'x');
        for (size_t i = 0; i < n; ++i) {
            if (ix != "*" && std::to_string(i) != ix) continue;
            Resolved r{};
            r.node = {t, root.addr + at + i * stride + axis * 4};
            r.path = name + "[" + std::to_string(i) + "]";
            out->push_back(r);
        }
        return true;
    };
    if (list("altarsPerFlameLevel", 21, 1, 0, &u8t, "") || list("buildzoneSizesPerAltarLevel", 6, 12, 24, &f32t, "*axis")) return true;
    if (path == "value") {
        Resolved r{};
        r.node = {&u8t, root.addr};
        r.path = path;
        out->push_back(r);
        return true;
    }
    return *err = "no member " + path, false;
}
// plain values and {"mul","add","min","max","raiseOnly","lowerOnly"} on the original, as the real parse_leaf combines them
bool parse_leaf(const Field& f, const gjson& v, const uint8_t* original, uint8_t* out, bool* reset, std::string* err) {
    *reset = false;
    double was = f.vt == KILN_CV_INT ? *original : [&] { float x; std::memcpy(&x, original, 4); return static_cast<double>(x); }();
    double x = was;
    if (v.is_number()) x = v.get<double>();
    else if (v.is_object()) {
        if (v.contains("mul")) x *= v["mul"].get<double>();
        if (v.contains("add")) x += v["add"].get<double>();
        if (v.contains("min")) x = std::max(x, v["min"].get<double>());
        if (v.contains("max")) x = std::min(x, v["max"].get<double>());
        if (v.value("raiseOnly", false)) x = std::max(x, was);
        if (v.value("lowerOnly", false)) x = std::min(x, was);
    } else return *err = "not a number", false;
    if (f.vt == KILN_CV_INT) *out = static_cast<uint8_t>(std::llround(x));
    else { const float y = static_cast<float>(x); std::memcpy(out, &y, 4); }
    return true;
}
bool boot_remove_components(const TType*, const std::vector<std::string>&, std::vector<uint8_t>*, size_t* n, std::string*) { return *n = 0, true; }
bool boot_add_components(const TType*, const std::vector<std::string>&, std::vector<uint8_t>*, size_t* n, std::string*) { return *n = 0, true; }
bool boot_copy_components(const TType*, const std::vector<CopySource>&, std::vector<uint8_t>*, size_t* n, std::string*, size_t) { return *n = 0, true; }
bool boot_clear(Root, const TType*, const std::string&, std::vector<uint8_t>*, size_t* n, std::string*) { return *n = 0, true; }
bool boot_replace(Root, const TType*, const std::string&, const std::vector<gjson>&, std::vector<uint8_t>*, size_t* n, std::string*) { return *n = 0, true; }
bool boot_append(Root, const TType*, const std::string&, const std::vector<gjson>&, bool, std::vector<uint8_t>*, size_t* n, std::string*, size_t*) {
    return *n = 0, true;
}
}  // namespace kc::gd

namespace {
using namespace kc;
using nlohmann::json;
int checks = 0, put_count = 0;
const uint8_t kGameAltars[21] = {1, 2, 4, 6, 8, 10, 11, 12, 13, 14, 15, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16};
const float kGameSizes[6] = {0, 40, 80, 120, 160, 160};
Table game{}, served{};
bool have_served = false, list_works = true;
const void* active = nullptr;
struct Fault { std::string mod, kind, key, reason; };
std::vector<Fault> faults;
std::string row_name, row_text;
KilnBootApiV1 boot{};
KilnAssetsApi asset{};
KilnCapsApi caps{};
KilnApi api{};
KilnBootIssueV1 issue{};
void check(bool ok, const std::string& why) {
    if (!ok) throw std::runtime_error(why + " [" + issue.mod_id + "/" + issue.kind + "/" + issue.key + ": " + issue.reason + "]");
    ++checks;
}
std::string str(KilnStr s) { return std::string(s.ptr, s.len); }
bool has(const std::string& s, const std::string& part) { return s.find(part) != std::string::npos; }
const void* KILN_CALL service(KilnStr name, uint32_t) {
    if (str(name) == KILN_BOOT_SERVICE) return &boot;
    if (str(name) == KILN_ASSETS_SERVICE) return &asset;
    if (str(name) == KILN_CAPS_SERVICE) return &caps;
    return nullptr;
}
void reset_game() {
    std::memcpy(game.altars, kGameAltars, 21);
    for (int l = 0; l < 6; ++l) for (int a = 0; a < 3; ++a) game.sizes[l][a] = kGameSizes[l];
    have_served = false;
    served = {};
}
// the envelope: every mod named is a participant
json envelope(std::initializer_list<std::tuple<const char*, std::string, json>> docs) {
    json list = json::array(), owners = json::array();
    std::set<std::string> seen;
    for (const auto& [owner, key, doc] : docs) {
        list.push_back({{"owner", owner}, {"key", key}, {"document", doc}});
        if (seen.insert(owner).second) owners.push_back({{"id", owner}});
    }
    return {{"owners", owners}, {"collectors", {{"patches", list}}}};
}
KilnBootPreparedV1 prepare(const json& ir) {
    const std::string text = ir.dump();
    KilnBootPrepareInputV1 in{sizeof(in), 1, 1, {text.data(), text.size()}};
    KilnBootPreparedV1 out{};
    out.size = sizeof(out);
    issue = {};
    issue.size = sizeof(issue);
    check(prepare_boot(nullptr, &in, &out, &issue) == KILN_OK, "collector result");
    return out;
}
bool accepted(const json& ir) {
    auto p = prepare(ir);
    if (p.ok) p.destroy(p.plan);
    return p.ok;
}
// prepare and apply; the served table is in "served" (or the game's when nothing was put)
void run(const json& ir) {
    faults.clear();
    put_count = 0;
    reset_game();
    auto p = prepare(ir);
    if (!p.ok) throw std::runtime_error(std::string("the plan is refused: ") + issue.mod_id + "/" + issue.kind + "/" + issue.key + ": " + issue.reason);
    active = p.plan;
    apply(nullptr);
    active = nullptr;
    p.destroy(p.plan);
    if (!have_served) served = game;
}
// more-flame-altars 0.4.0's boot patch (MoreFlameAltarsMod.Patch) with Altars 4, ExtraAltars 2 and BuildArea 150: its exact shape
json more_flame_altars() {
    json set = {{"altarsPerFlameLevel[*]", {{"add", 2}, {"min", 4}, {"max", 16}, {"raiseOnly", true}}}};
    for (const char* axis : {"x", "y", "z"}) set[std::string("buildzoneSizesPerAltarLevel[*].") + axis] = {{"mul", 1.5}, {"raiseOnly", true}};
    return {{"part", 0}, {"resource", "table"}, {"type", "keen::BalancingTable"}, {"set", set}};
}
int mfa_altars(int g) { return std::max(g, std::min(16, std::max(4, g + 2))); }
double mfa_size(double g) { return std::max(g, g * 1.5); }
// two mods' base rules: "roomy" (at least 8 altars; altar level 1 at 60 m, level 4 at 250 m) and "wide" (2 more altars than
// the game, build areas at 120%)
json roomy() { return {{"minimumAltars", 8}, {"sizes", {{{"altarLevel", 1}, {"size", {60, 60, 60}}}, {{"altarLevel", 4}, {"size", {250, 250, 250}}}}}}; }
json wide() { return {{"extraAltars", 5}, {"buildAreaPercent", 120}}; }
int roomy_altars(int g) { return std::max(g, std::min(16, std::max(8, g))); }
int wide_altars(int g) { return std::max(g, std::min(16, g + 5)); }
double roomy_size(int level, double g) { return level == 1 ? std::max(g, 60.0) : level == 4 ? std::max(g, 250.0) : g; }
double wide_size(double g) { return std::max(g, std::min(256.0, g * 1.2)); }

void parse_rules() {
    auto bad = [](json doc, const std::string& part) {
        baserules::Rule r;
        std::string err;
        return !baserules::parse(doc, &r, &err) && has(err, part);
    };
    baserules::Rule r;
    std::string err;
    check(baserules::parse(roomy(), &r, &err) && r.minimum == 8 && r.sizes.size() == 2 && r.sizes[1].level == 4 && r.sizes[1].xyz[2] == 250, "roomy parses");
    check(baserules::parse(json::object(), &r, &err), "an empty rule changes nothing and is fine");
    check(baserules::parse({{"format", 1}, {"minimumAltars", nullptr}, {"extraAltars", 0}, {"buildAreaPercent", nullptr}, {"sizes", nullptr}}, &r, &err),
          "null is a member left out");
    check(bad({{"minimumAltars", 17}}, "at most 16"), "17 altars is refused");
    check(bad({{"minimumAltars", 0}}, "1 to 16"), "0 altars is refused");
    check(bad({{"minimumAltars", 2.5}}, "1 to 16"), "a fraction of an altar is refused");
    check(bad({{"extraAltars", 16}}, "0 to 15"), "16 extra altars is refused");
    check(bad({{"extraAltars", -1}}, "0 to 15"), "fewer altars is refused");
    check(bad({{"buildAreaPercent", 99}}, "100 to 200"), "99% is refused");
    check(bad({{"buildAreaPercent", 201}}, "100 to 200"), "201% is refused");
    check(bad({{"sizes", {{{"altarLevel", 0}, {"size", {40, 40, 40}}}}}}, "1 to 5"), "altar level 0 is refused");
    check(bad({{"sizes", {{{"altarLevel", 6}, {"size", {40, 40, 40}}}}}}, "1 to 5"), "altar level 6 is refused");
    check(bad({{"sizes", {{{"altarLevel", 1}, {"size", {0.25, 40, 40}}}}}}, "0.5 to 256"), "0.25 m is refused");
    check(bad({{"sizes", {{{"altarLevel", 1}, {"size", {40, 256.5, 40}}}}}}, "0.5 to 256"), "256.5 m is refused");
    check(bad({{"sizes", {{{"altarLevel", 1}, {"size", {40, 40, 100.25}}}}}}, "multiple of 0.5"), "100.25 m is refused");
    check(bad({{"sizes", {{{"altarLevel", 1}, {"size", {40, 40}}}}}}, "[x, y, z]"), "two axes are refused");
    check(bad({{"sizes", {{{"altarLevel", 1}, {"size", {40, 40, 40}}}, {{"altarLevel", 1}, {"size", {50, 50, 50}}}}}}, "twice"), "one altar level twice is refused");
    check(bad({{"sizes", {{{"altarLevel", 1}, {"size", {40, 40, 40}}, {"sizee", 1}}}}}, "unknown member \"sizee\""), "an unknown size member is refused");
    check(bad({{"minimumAltar", 4}}, "unknown member \"minimumAltar\""), "an unknown member is refused");
    check(bad(json::array(), "an object"), "a rule is an object");
}

void compose_rules() {
    baserules::Rule a, b;
    std::string err;
    check(baserules::parse(roomy(), &a, &err) && baserules::parse(wide(), &b, &err), "both parse");
    const baserules::Composed c = baserules::compose({a, b});
    for (int lv = 0; lv < 21; ++lv) {
        const int g = kGameAltars[lv];
        check(baserules::altars(g, c) == std::max(roomy_altars(g), wide_altars(g)), "the count is the max of the rules at flame level " + std::to_string(lv));
    }
    check(baserules::altars(16, c) == 16 && baserules::altars(20, c) == 20, "never above 16 by a rule, never below the game's");
    for (int lv = 0; lv < 6; ++lv)
        check(baserules::size(kGameSizes[lv], lv, 0, c) == std::max(roomy_size(lv, kGameSizes[lv]), wide_size(kGameSizes[lv])),
              "the size is the max of the rules at altar level " + std::to_string(lv));
    baserules::Rule big;
    check(baserules::parse({{"buildAreaPercent", 200}}, &big, &err), "200%");
    check(baserules::size(160, 4, 1, baserules::compose({big})) == 256, "a percentage is held at 256 m");
    check(baserules::size(300, 4, 1, baserules::compose({big})) == 300, "a game's value above 256 m stays");
}

// more-flame-altars' own patch and two mods' base rules: no refusal in either order, the result the max of all three.
void with_more_flame_altars() {
    const json mfa = more_flame_altars();
    for (int order = 0; order < 3; ++order) {
        const json ir = order == 0 ? envelope({{"more-flame-altars", "altars", mfa}, {"rules-a", "baseRules@roomy", roomy()}, {"rules-b", "baseRules@wide", wide()}})
                      : order == 1 ? envelope({{"rules-a", "baseRules@roomy", roomy()}, {"rules-b", "baseRules@wide", wide()}, {"more-flame-altars", "altars", mfa}})
                                   : envelope({{"rules-a", "baseRules@roomy", roomy()}, {"more-flame-altars", "altars", mfa}, {"rules-b", "baseRules@wide", wide()}});
        check(accepted(ir), "more-flame-altars and two base rules are not refused (order " + std::to_string(order) + ")");
        run(ir);
        check(faults.empty(), "no fault (order " + std::to_string(order) + ")");
        check(put_count == 1, "one served table (order " + std::to_string(order) + ")");
        for (int lv = 0; lv < 21; ++lv) {
            const int g = kGameAltars[lv];
            const int want = std::max({mfa_altars(g), roomy_altars(g), wide_altars(g)});
            check(served.altars[lv] == want, "flame level " + std::to_string(lv) + ": " + std::to_string(served.altars[lv]) + ", want " + std::to_string(want));
        }
        for (int lv = 0; lv < 6; ++lv)
            for (int ax = 0; ax < 3; ++ax) {
                const double g = kGameSizes[lv];
                const double want = std::max({mfa_size(g), roomy_size(lv, g), wide_size(g)});
                check(served.sizes[lv][ax] == static_cast<float>(want),
                      "altar level " + std::to_string(lv) + " axis " + std::to_string(ax) + ": " + std::to_string(served.sizes[lv][ax]) + ", want " + std::to_string(want));
            }
        check(served.altars[0] == 8 && served.altars[1] == 8 && served.altars[2] == 9 && served.altars[11] == 16 && served.sizes[1][0] == 60 &&
                  served.sizes[4][2] == 250 && served.sizes[5][1] == 240,
              "spot values");
        check(game.altars[2] == 4 && game.sizes[4][0] == 160, "the game's own bytes stay as they were");
    }
    // more-flame-altars alone is what it was before base rules existed
    run(envelope({{"more-flame-altars", "altars", mfa}}));
    check(faults.empty() && served.altars[0] == 4 && served.altars[2] == 6 && served.sizes[4][0] == 240, "more-flame-altars alone");
}

// Base rules without any resource patch of the table: Kiln's change alone is served.
void rules_alone() {
    run(envelope({{"rules-a", "baseRules@roomy", roomy()}, {"rules-b", "baseRules@wide", wide()}}));
    check(faults.empty() && put_count == 1, "the table is served with Kiln's change only");
    check(served.altars[0] == 8 && served.altars[3] == 11 && served.altars[20] == 16 && served.sizes[1][1] == 60 && served.sizes[2][1] == 96 &&
              served.sizes[4][1] == 250 && served.sizes[5][1] == 192,
          "the composed rule");
    // a rule that changes nothing: nothing is served
    run(envelope({{"rules-a", "baseRules@none", json::object()}}));
    check(faults.empty() && put_count == 0, "a rule that changes nothing serves nothing");
    // a mod that lowers the count with a patch of its own: the rule still holds at least its own result
    run(envelope({{"lower", "few", {{"resource", "table"}, {"type", "keen::BalancingTable"}, {"set", {{"altarsPerFlameLevel[*]", {{"max", 1}, {"lowerOnly", true}}}}}}},
                  {"rules-a", "baseRules@roomy", {{"minimumAltars", 3}}}}));
    check(faults.empty() && served.altars[0] == 3 && served.altars[5] == 10 && served.altars[11] == 16, "a lowering patch and a rule: the rule's result is the floor");
    // two base rules of one mod compose as two mods' do
    run(envelope({{"rules-a", "baseRules@roomy", roomy()}, {"rules-a", "baseRules@wide", wide()}}));
    check(faults.empty() && served.altars[3] == 11 && served.sizes[4][0] == 250, "one mod's two rules compose");
    // the table cannot be found: every rule is faulted, naming its mod, kind and name; nothing is served
    list_works = false;
    run(envelope({{"rules-a", "baseRules@roomy", roomy()}}));
    list_works = true;
    check(faults.size() == 1 && faults[0].mod == "rules-a" && faults[0].kind == "baseRules" && faults[0].key == "roomy" && put_count == 0,
          "no table: the rule is faulted");
}

// A bad rule refuses its mod in prepare, naming the kind and the rule; another mod's patch of the same fields is no conflict.
void refusals() {
    check(!accepted(envelope({{"rules-a", "baseRules@many", {{"minimumAltars", 17}}}})) && std::string(issue.mod_id) == "rules-a" &&
              std::string(issue.kind) == "baseRules" && std::string(issue.key) == "many" && has(issue.reason, "at most 16"),
          "17 altars refuses the mod, naming the rule");
    // two resource patches of one field still conflict (the old rule), base rules never do
    check(!accepted(envelope({{"more-flame-altars", "altars", more_flame_altars()},
                              {"other", "altars", {{"resource", "table"}, {"type", "keen::BalancingTable"}, {"set", {{"altarsPerFlameLevel[*]", {{"min", 10}}}}}}}})) &&
              std::string(issue.mod_id) == "other",
          "two patches of one field: the later is refused, as before");
}

// The read side: kiln.base_rules' state, the game's own table and the one served, and the rules that applied.
void state() {
    auto read = [] {
        char buf[16384];
        size_t n = 0;
        check(base_state(buf, sizeof buf, &n) == KILN_OK, "state answers");
        return json::parse(std::string(buf, n));
    };
    run(envelope({{"more-flame-altars", "altars", more_flame_altars()}, {"rules-a", "baseRules@roomy", roomy()}, {"rules-b", "baseRules@wide", wide()}}));
    have_served = true;  // kiln.assets serves the patched table from now on
    json s = read();
    check(s["table"] == true && s["original"]["altars"].size() == 21 && s["original"]["altars"][2] == 4 && s["effective"]["altars"][2] == 9,
          "original and effective altars");
    check(s["original"]["sizes"].size() == 6 && s["original"]["sizes"][4][0] == 160 && s["effective"]["sizes"][4][0] == 250 && s["effective"]["sizes"][5][2] == 240,
          "original and effective sizes");
    check(s["applied"] == true && s["rules"].size() == 2 && s["rules"][0]["mod"] == "rules-a" && s["rules"][0]["name"] == "roomy" && s["rules"][0]["applied"] == true &&
              s["rules"][1]["mod"] == "rules-b" && s["composed"]["minimumAltars"] == 8 && s["composed"]["extraAltars"] == 5 && s["composed"]["buildAreaPercent"] == 120,
          "the rules, applied, and the composed rule");
    check(s["patchedBy"].size() == 1 && s["patchedBy"][0] == "more-flame-altars", "the mods whose own patches changed the table");
}

// Ruling D1 (2026-11-21): the base rules lie below the registry stage. A registry edit on altarsPerFlameLevel[1] wins over a
// rule that raised it, the rule enters the registry stage as a named writer beneath it, and kiln.base_rules says the
// registry changed the rule's value.
void with_registry() {
    namespace bh = kc::registry::boothook;
    namespace rs = kc::registry::stage;
    std::vector<bh::OldDoc> below;
    std::vector<uint8_t> at_write;  // the copy as the registry's write sees it
    bh::Hooks h;
    h.run = [&](const std::vector<bh::OldDoc>& d) {
        below = d;
        std::vector<rs::BootResource> out;
        for (const auto& o : d)
            if (o.type == "keen::BalancingTable") {
                rs::BootResource b;
                b.key = o.res, b.type = o.type;
                rs::BootField f;
                f.field.field = "altarsPerFlameLevel";
                b.fields.push_back(f);
                out.push_back(b);
                break;
            }
        return out;
    };
    h.write = [&](const KilnAssetKey&, const std::string&, std::vector<uint8_t>* copy, const rs::BootResource& rb, std::vector<rs::BootOutcome>* outcomes,
                  std::vector<std::string>*) -> size_t {
        at_write = *copy;
        (*copy)[1] = 2;  // the registry sets altarsPerFlameLevel[1] = 2
        outcomes->push_back({rb.fields[0].field, true, {}, ""});
        return 1;
    };
    h.finish = [](const std::vector<rs::BootOutcome>&) {};
    bh::set(&h);
    run(envelope({{"rules-a", "baseRules@roomy", roomy()}}));
    bh::set(nullptr);
    check(faults.empty() && put_count == 1, "one copy served: the rules and the registry's write in it");
    bool named = false;
    for (const auto& o : below)
        if (o.mod == "rules-a" && o.key == "baseRules/roomy")
            for (const auto& [p, v] : o.sets) named = named || (p == "altarsPerFlameLevel[1]" && v == json{{"min", 8}, {"raiseOnly", true}});
    check(named, "the base rule enters the registry stage as an earlier writer of altarsPerFlameLevel[1] (min 8, raise only)");
    check(at_write.size() >= sizeof(Table) && at_write[1] == 8, "the registry writes over the rule's result (8 at flame level 1)");
    check(served.altars[1] == 2 && served.altars[0] == 8 && served.altars[2] == 8, "the registry's edit wins at level 1; the rule holds elsewhere");
    have_served = true;
    char buf[16384];
    size_t n = 0;
    check(base_state(buf, sizeof buf, &n) == KILN_OK, "state answers");
    const json s = json::parse(std::string(buf, n));
    check(s["effective"]["altars"][1] == 2 && s["registryChanged"].size() == 1 && s["registryChanged"][0]["field"] == "altarsPerFlameLevel[1]" &&
              s["registryChanged"][0]["afterRules"] == 8 && s["registryChanged"][0]["effective"] == 2 && s["registryChanged"][0]["rules"][0] == "rules-a:roomy",
          "kiln.base_rules: the effective value after the registry, and that a registry edit changed the rule's value: " + s.dump());
}

// A data pack's gearRules as a registry front-end (registry/boot_hooks.h): the stage hands over a gearRules document whose
// values its boot back-end writes itself; the apply builds the table both ways (the documents as before, then the registry's
// own rows; without that document, then the registry's rows with its front rows) and serves the front-end's copy only when the
// two are the same bytes, else the old copy, as before. What it did goes to finish, per document.
void front_ends() {
    namespace bh = kc::registry::boothook;
    namespace rs = kc::registry::stage;
    namespace rg = kc::registry;
    const std::string doc = "gearmod/gearRules@gear.json";
    float want = 70;              // what the registry's write puts at altar level 2's y (the document's value: 70)
    bool registry_row = false;    // the stage also hands over a registry edit of altarsPerFlameLevel[1] (2)
    std::vector<float> seen;      // altar level 2's y in each copy the registry's write was given
    std::vector<size_t> rows_seen;
    std::vector<rs::BootOutcome> outcomes;
    std::vector<rs::FrontEnd> fronts;
    bh::Hooks h;
    h.run = [&](const std::vector<bh::OldDoc>& d) {
        std::vector<rs::BootResource> out;
        for (const auto& o : d) {
            if (o.mod + "/" + o.key != doc) continue;
            rs::BootResource b;
            b.key = o.res, b.type = o.type;
            rs::BootField f;
            f.field = {"balance", "game:balance", "buildzoneSizesPerAltarLevel[2].y"};
            rg::ChainRow c;
            c.kind = rg::WriterKind::OldPatch, c.mod = o.mod, c.who = doc, c.front = true;
            f.rows.push_back(c);
            b.fields.push_back(f);
            if (registry_row) {
                rs::BootField r;
                r.field = {"balance", "game:balance", "altarsPerFlameLevel"};
                rg::ChainRow e;
                e.kind = rg::WriterKind::Registry, e.mod = "bigmod";
                r.rows.push_back(e);
                b.fields.push_back(r);
            }
            b.front = {doc};
            out.push_back(b);
        }
        return out;
    };
    h.write = [&](const KilnAssetKey&, const std::string&, std::vector<uint8_t>* copy, const rs::BootResource& rb, std::vector<rs::BootOutcome>* out,
                  std::vector<std::string>*) -> size_t {
        Table t;
        std::memcpy(&t, copy->data(), sizeof t);
        seen.push_back(t.sizes[2][1]);
        rows_seen.push_back(rb.fields.size());
        size_t n = 0;  // values changed: as the registry's write, which leaves a value the copy already holds as it is
        for (const auto& f : rb.fields) {
            const bool front = std::any_of(f.rows.begin(), f.rows.end(), [](const rg::ChainRow& c) { return c.front; });
            if (front && t.sizes[2][1] != want) t.sizes[2][1] = want, ++n;
            else if (!front && t.altars[1] != 2) t.altars[1] = 2, ++n;
            out->push_back({f.field, true, {}, ""});
        }
        std::memcpy(copy->data(), &t, sizeof t);
        return n;
    };
    h.finish = [&](const std::vector<rs::BootOutcome>& o) { outcomes = o; };
    h.fronts = [&](const std::vector<rs::FrontEnd>& f) { fronts = f; };
    bh::set(&h);
    const json patch = {{"resource", "table"}, {"type", "keen::BalancingTable"}, {"set", {{"buildzoneSizesPerAltarLevel[2].y", 70}}}};
    auto go = [&](const json& ir) {
        seen.clear(), rows_seen.clear(), outcomes.clear(), fronts.clear();
        run(ir);
    };
    // the same bytes: the registry's copy is served (built without the document: altar level 2 at the game's 80 when the
    // registry writes), finish hears "registry"
    go(envelope({{"gearmod", "gearRules@gear.json", patch}}));
    check(faults.empty() && put_count == 1 && served.sizes[2][1] == 70 && served.sizes[2][0] == 80, "the table holds the document's value");
    check(seen.size() == 1 && seen[0] == 80 && rows_seen[0] == 1, "the registry's write was given the copy built without the document (80), with its front row");
    check(fronts.size() == 1 && fronts[0].mod == "gearmod" && fronts[0].key == "gearRules@gear.json" && fronts[0].state == "registry" && fronts[0].why.empty(),
          "finish: the registry wrote it");
    check(outcomes.size() == 1 && outcomes[0].ok, "the front field's outcome is the registry's");
    // different bytes: the old copy is served, the document as a resource patch, finish hears why
    want = 71;
    go(envelope({{"gearmod", "gearRules@gear.json", patch}}));
    check(faults.empty() && put_count == 1 && served.sizes[2][1] == 70, "the resource patch's value is served (70, not the registry's 71)");
    check(fronts.size() == 1 && fronts[0].state == "resourcePatch" && has(fronts[0].why, "the registry's bytes differ from its resource patch's (byte 54 of the resource)"),
          "finish: kept, with where the bytes differ: " + (fronts.empty() ? std::string() : fronts[0].why));
    check(outcomes.empty(), "no outcome of the front field (the registry did not write it)");
    want = 70;
    // a registry edit beside it: the old way writes the registry's row over the document, the front-end way both rows; same bytes
    registry_row = true;
    go(envelope({{"gearmod", "gearRules@gear.json", patch}}));
    check(faults.empty() && served.sizes[2][1] == 70 && served.altars[1] == 2, "the document and the registry's edit, in one copy");
    check(seen.size() == 2 && rows_seen[0] == 1 && rows_seen[1] == 2, "the old way's write had the registry's row alone, the front-end's both");
    check(fronts.size() == 1 && fronts[0].state == "registry" && outcomes.size() == 2, "the front-end's copy and outcomes");
    // a base rule on the same member: the old way's rule floors the document's 70 at the game's 80 (it runs after the
    // documents), the registry writes after the rule: the bytes differ, so the resource patch is applied, as before
    go(envelope({{"gearmod", "gearRules@gear.json", patch}, {"rules-a", "baseRules@roomy", roomy()}}));
    check(faults.empty() && served.sizes[2][1] == 80 && served.altars[1] == 2 && served.altars[0] == 8, "the old copy: the rule's floor, the registry's edit");
    check(fronts.size() == 1 && fronts[0].state == "resourcePatch" && has(fronts[0].why, "differ"), "finish: kept, the bytes differ: " + (fronts.empty() ? std::string() : fronts[0].why));
    check(outcomes.size() == 1 && outcomes[0].field.field == "altarsPerFlameLevel", "the old way's outcome: the registry's own row only");
    registry_row = false;
    // a document of the value the table already holds: the old way's patch counts it (a copy is served, kiln.base_rules names
    // the mod), the registry's write changes nothing; the front-end's copy is served all the same, as the old way's was
    want = 80;
    go(envelope({{"gearmod", "gearRules@gear.json", {{"resource", "table"}, {"type", "keen::BalancingTable"}, {"set", {{"buildzoneSizesPerAltarLevel[2].y", 80}}}}}}));
    check(faults.empty() && put_count == 1 && served.sizes[2][1] == 80 && fronts.size() == 1 && fronts[0].state == "registry",
          "the game's own value: one copy served, the registry's, as the resource patch's was (puts " + std::to_string(put_count) + ")");
    {
        char buf[16384];
        size_t n = 0;
        check(base_state(buf, sizeof buf, &n) == KILN_OK, "state answers");
        const json st = json::parse(std::string(buf, n));
        check(st["patchedBy"] == json::array({"gearmod"}), "kiln.base_rules names the mod whose patch was served, as before: " + st["patchedBy"].dump());
    }
    want = 70;
    // a document the old way cannot apply faults as before; the registry does not write it either
    go(envelope({{"gearmod", "gearRules@gear.json", {{"resource", "table"}, {"type", "keen::BalancingTable"}, {"set", {{"missingMember", 1}}}}}}));
    check(faults.size() == 1 && faults[0].mod == "gearmod" && faults[0].key == "gearRules@gear.json", "the fault, as before");
    check(fronts.size() == 1 && fronts[0].state == "resourcePatch" && has(fronts[0].why, "its resource patch was not applied"), "finish: kept, it was not applied");
    // another mod's ordinary patch is never a front-end
    go(envelope({{"other", "sizes", patch}}));
    check(faults.empty() && fronts.empty() && seen.empty() && served.sizes[2][1] == 70, "an ordinary resource patch: no front-end, as before");
    bh::set(nullptr);
}

// A blocks file's "baseRules" section: read by the collector as a staged rule is; a name both have stays the file's.
void rule_files() {
    namespace fs = std::filesystem;
    const char* base = std::getenv("KILN_OUT");
    const fs::path root = fs::path(base ? base : fs::temp_directory_path().string()) / "content-tests" / ("base-mods-" + std::to_string(::getpid()));
    fs::remove_all(root);
    fs::create_directories(root / "basemod" / "blocks");
    auto put = [](const fs::path& p, const std::string& s) { std::ofstream(p, std::ios::binary) << s; };
    put(root / "basemod" / "kiln.mod.json", R"({"id": "basemod", "boot": true})");
    put(root / "basemod" / "blocks" / "bases.json", "{ // comments and trailing commas, as in every blocks file\n \"format\": 1, \"baseRules\": {\"roomy\": {\"minimumAltars\": 6,},},}");
    g_mod_dirs = {root / "basemod"};
    std::vector<kiln::boot::Doc> rules;
    auto patches = file_docs(nullptr, &rules);
    check(patches.empty() && rules.size() == 1 && rules[0].owner == "basemod" && rules[0].key == "baseRules@roomy" && rules[0].document["minimumAltars"] == 6,
          "the file's rule, keyed as a staged one");
    run(envelope({{"basemod", "baseRules@roomy", {{"minimumAltars", 2}}}}));
    check(faults.empty() && served.altars[0] == 6, "the file's rule wins over the staged one of the same name");
    g_mod_dirs.clear();
    fs::remove_all(root);
}
}  // namespace

int main() {
    try {
        using namespace kc::gd;
        u8t.name = "uint8", u8t.kind = K_U8, u8t.size = 1;
        f32t.name = "float32", f32t.kind = K_F32, f32t.size = 4;
        table_t.name = "keen::BalancingTable", table_t.brief = "BalancingTable", table_t.kind = K_STRUCT, table_t.size = sizeof(Table);
        boot.header.size = sizeof(boot);
        boot.active_plan = [](KilnStr, uint64_t*) -> const void* { return active; };
        boot.activation_fault = [](KilnOwner, KilnStr mod, KilnStr kind, KilnStr key, KilnStr reason) -> KilnResult {
            faults.push_back({str(mod), str(kind), str(key), str(reason)});
            return KILN_OK;
        };
        asset.header.size = sizeof(asset);
        asset.when_resources_ready = [](void(KILN_CALL*)(void*), void*) -> KilnResult { return KILN_OK; };
        asset.resource_data = [](const KilnAssetKey* k, const void** data, size_t* len) -> KilnResult {
            if (k->guid[0] != 1) return KILN_ERR_NOT_FOUND;
            *data = have_served ? &served : &game;
            *len = sizeof(Table);
            return KILN_OK;
        };
        asset.resource_list = [](uint32_t type, KilnAssetKey* out, size_t cap, size_t* n) -> KilnResult {
            *n = 0;
            if (!list_works || type != fnv1a(std::string("keen::BalancingTable"))) return KILN_OK;
            *n = 1;
            if (cap >= 1) {
                std::memset(out[0].guid, 1, 16);
                out[0].type = type;
                out[0].part = 0;
            }
            return KILN_OK;
        };
        asset.resource_put = [](KilnOwner, const KilnAssetKey* k, const void* data, size_t len, uint32_t) -> KilnResult {
            if (k->guid[0] != 1 || len < sizeof(Table)) return KILN_ERR_INVALID_ARGUMENT;
            std::memcpy(&served, data, sizeof(Table));
            have_served = true;
            ++put_count;
            return KILN_OK;
        };
        caps.report = [](KilnOwner, KilnStr name, uint32_t, uint32_t, KilnStr text) -> KilnResult {
            row_name = str(name);
            row_text = str(text);
            return KILN_OK;
        };
        api.service = &service;
        g_api = &api;
        parse_rules();
        compose_rules();
        with_more_flame_altars();
        rules_alone();
        refusals();
        state();
        with_registry();
        front_ends();
        rule_files();
    } catch (const std::exception& e) {
        std::printf("baserules-test: FAIL %s (after %d checks)\n", e.what(), checks);
        return 1;
    }
    std::printf("baserules-test: %d checks passed\n", checks);
}
