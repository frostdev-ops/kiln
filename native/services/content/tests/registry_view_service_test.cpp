// SPDX-License-Identifier: MIT
// kiln.content registry views: the service behind kiln.registry.view (registry/view_service.h), on the host over a fake game:
// two resource types with entries held as JSON, the registries' resolved view faked. Not before the stage; the catalog with
// every registry (a fixture registry too) and every resource type; reads of advanced fields in Kiln form, per entry, an id
// field naming another view's entry; refusals per entry in Kiln words (no such field, a kind the view cannot read, a member
// this entry lacks), each logged once; a resource added later; stable views reading through the registry; selectors.
#include <cstdio>
#include <cstring>
#include <string>

#include "../registry/schema_ir.h"
#include "../registry/view_service.h"

using namespace kc::registry;
using views::json;
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
P leaf(TypeKind k) {
    auto t = std::make_shared<TypeDesc>();
    t->kind = k;
    return t;
}
P st(std::vector<std::pair<std::string, P>> fields) {
    auto t = std::make_shared<TypeDesc>();
    t->kind = TypeKind::Struct, t->fields = std::move(fields);
    return t;
}
P list(P el) {
    auto t = std::make_shared<TypeDesc>();
    t->kind = TypeKind::List, t->element = std::move(el);
    return t;
}
P ref(const std::string& target) {
    auto t = std::make_shared<TypeDesc>();
    t->kind = TypeKind::Ref, t->target = target;
    return t;
}
P en(std::vector<std::string> v) {
    auto t = std::make_shared<TypeDesc>();
    t->kind = TypeKind::Enum, t->values = std::move(v);
    return t;
}
TypeDesc root(const std::string& name, uint32_t hash, std::vector<std::pair<std::string, P>> fields) {
    TypeDesc t = *st(std::move(fields));
    t.name = name, t.hash = hash;
    return t;
}

constexpr uint32_t kQuest = 0x1111, kItem = 0x2222;
KilnAssetKey key(uint8_t n, uint32_t type) {
    KilnAssetKey k{};
    k.guid[15] = n;
    k.type = type;
    return k;
}
std::string guid(uint8_t n) {
    char b[40];
    std::snprintf(b, sizeof b, "00000000-0000-0000-0000-0000000000%02x", n);
    return b;
}

struct Game {
    bool ready = true;
    std::map<uint32_t, std::vector<std::pair<KilnAssetKey, json>>> res;
    int member_calls = 0;
    std::vector<std::string> log;
    std::map<std::string, std::string> registry;  // "<registry>|<id>|<field>" -> value JSON
};

views::ServiceEnv env(Game& g, std::optional<Schema> schema = std::nullopt) {
    views::ServiceEnv e;
    e.ready = [&g](std::string* why) { return g.ready || (*why = "the game's resources are not loaded yet", false); };
    e.resource_types = [](std::string*) { return std::vector<std::pair<std::string, uint32_t>>{{"keen::QuestResource", kQuest}, {"keen::ItemInfo", kItem}}; };
    e.describe = [](const std::string& type) -> std::optional<TypeDesc> {
        if (type == "keen::QuestResource")
            return root(type, kQuest,
                        {{"debugName", leaf(TypeKind::Text)}, {"xpReward", leaf(TypeKind::Int)}, {"kind", en({"Main", "Side_Quest"})},
                         {"rewardItems", list(st({{"item", ref("keen::ItemInfo")}, {"count", leaf(TypeKind::Int)}}))},
                         {"events", list(leaf(TypeKind::Variant))}, {"bonus", leaf(TypeKind::Float)}});
        if (type == "keen::ItemInfo") return root(type, kItem, {{"debugName", leaf(TypeKind::Text)}, {"maxStackSize", leaf(TypeKind::Int)}});
        return std::nullopt;
    };
    e.list = [&g](uint32_t type, std::vector<KilnAssetKey>* out) {
        out->clear();
        for (const auto& [k, _] : g.res[type]) out->push_back(k);
        return true;
    };
    e.member = [&g](const KilnAssetKey& k, const std::string&, const std::string& path, json* out, std::string* why) {
        ++g.member_calls;
        for (const auto& [kk, doc] : g.res[k.type]) {
            if (std::memcmp(kk.guid, k.guid, 16) != 0) continue;
            const json* cur = &doc;
            std::string rest = path;
            while (!rest.empty()) {
                const size_t dot = rest.find('.');
                std::string seg = rest.substr(0, dot);
                rest = dot == std::string::npos ? "" : rest.substr(dot + 1);
                size_t idx = SIZE_MAX;
                if (const size_t b = seg.find('['); b != std::string::npos) idx = std::stoul(seg.substr(b + 1)), seg = seg.substr(0, b);
                if (!cur->is_object() || !cur->contains(seg)) return *why = seg + ": nothing there", false;
                cur = &(*cur)[seg];
                if (idx != SIZE_MAX) {
                    if (!cur->is_array() || idx >= cur->size()) return *why = "the list has " + std::to_string(cur->size()) + " entries", false;
                    cur = &(*cur)[idx];
                }
            }
            *out = *cur;
            return true;
        }
        return *why = "no such resource", false;
    };
    e.regmap = [](map::RegMap* rm) {
        std::vector<std::string> errs;
        map::load_common(json::parse(R"({"regmap": 1, "registry": "items",
            "entries": {"kind": "resource", "resourceType": "keen::ItemInfo", "name": "debugName", "guid": "objectId", "id": "itemId"},
            "rows": {"maxStack": {"paths": ["maxStackSize"], "conv": "int", "read": "worldLoad", "backend": "live"}}})"),
                         &rm->registries["items"], &errs);
        return true;
    };
    e.aliases = [](views::Aliases* a, std::vector<std::string>*) { *a = {}; return true; };
    e.schema = std::move(schema);
    e.registry_read = [&g](const std::string& r, const std::string& id, const std::string& f, std::string* out) -> KilnResult {
        const auto it = g.registry.find(r + "|" + id + "|" + f);
        if (it == g.registry.end()) return KILN_ERR_NOT_FOUND;
        *out = it->second;
        return KILN_OK;
    };
    e.registry_select = [](const std::string& r, const std::string& sel, std::string* out) -> KilnResult {
        if (r != "items") return KILN_ERR_UNAVAILABLE;
        *out = sel == "\"*\"" ? R"(["game:wood","game:stone"])" : R"(["game:wood"])";
        return KILN_OK;
    };
    e.registry_mapping = [](const std::string& q, std::string* state, std::string* reason) {
        if (q == "items.fuel.burnTime") return *state = "unproven", *reason = "no game code reads it", true;
        return *state = "mapped", reason->clear(), true;
    };
    e.log = [&g](int, const std::string& line) { g.log.push_back(line); };
    return e;
}

void quests(Game& g) {
    g.res[kQuest] = {
        {key(1, kQuest), json::parse(R"({"debugName": "Quest_Main", "xpReward": 100, "kind": "Main", "bonus": 1.5,
             "rewardItems": [{"item": "00000000-0000-0000-0000-000000000020", "count": 2}], "events": []})")},
        {key(2, kQuest), json::parse(R"({"debugName": "Quest_Side", "xpReward": 40, "kind": "Side_Quest",
             "rewardItems": [], "events": []})")},  // no "bonus": this entry lacks the member
        {key(3, kQuest), json::parse(R"({"debugName": "Quest_Odd", "xpReward": 7, "kind": "Daily", "bonus": 0, "rewardItems": [], "events": []})")}};
    g.res[kItem] = {{key(10, kItem), json::parse(R"({"debugName": "Wood", "maxStackSize": 20})")},
                    {key(11, kItem), json::parse(R"({"debugName": "Stone", "maxStackSize": 50})")}};
}

bool has(const std::vector<std::string>& v, const std::string& part) {
    for (const auto& s : v)
        if (s.find(part) != std::string::npos) return true;
    return false;
}
}  // namespace

int main() {
    // ---- not before the stage
    {
        Game g;
        g.ready = false;
        quests(g);
        views::Service s(env(g));
        std::string out;
        CHECK(s.views(&out) == KILN_ERR_UNAVAILABLE && out.find("not loaded yet") != std::string::npos);
        CHECK(s.read("quest", "game:quest_main", "xpReward", &out) == KILN_ERR_UNAVAILABLE);
        g.ready = true;
        CHECK(s.views(&out) == KILN_OK);  // and it builds once they are
    }

    Game g;
    quests(g);
    Schema schema = generated_schema();
    RegistrySpec creatures;  // a later lane's registry, from its schema alone
    creatures.name = "creatures";
    FieldSpec hp;
    hp.name = "health.perPlayer", hp.type = FieldType::Float;
    creatures.fields[hp.name] = hp;
    schema.registries["creatures"] = creatures;
    views::Service s(env(g, schema));
    std::string out;

    // ---- the catalog
    CHECK(s.views(&out) == KILN_OK);
    {
        const json j = json::parse(out);
        std::set<std::string> names;
        for (const json& v : j["views"]) names.insert(v["name"].get<std::string>());
        for (const char* n : {"items", "recipes", "loot", "drops", "balance", "creatures", "quest", "itemInfo"}) CHECK(names.count(n));
        CHECK(out.find("keen::") == std::string::npos && out.find("QuestResource") == std::string::npos);
    }
    CHECK(s.fields("quest", &out) == KILN_OK && out.find("\"xpReward\"") != std::string::npos && out.find("\"sideQuest\"") != std::string::npos);
    CHECK(s.fields("itemInfo", &out) == KILN_OK && out.find("\"coveredBy\":\"items.maxStack\"") != std::string::npos);
    CHECK(s.fields("items", &out) == KILN_OK && out.find("\"maxStack\"") != std::string::npos);
    CHECK(s.fields("items", &out) == KILN_OK && out.find("no game code reads it") != std::string::npos);  // a field's mapping on this build
    CHECK(s.fields("creatures", &out) == KILN_OK && out.find("health.perPlayer") != std::string::npos);
    CHECK(s.fields("nope", &out) == KILN_ERR_NOT_FOUND && out.find("no view nope") != std::string::npos);

    // ---- advanced reads
    CHECK(s.read("quest", "game:quest_main", "xpReward", &out) == KILN_OK && out == "100");
    CHECK(s.read("quest", "game:quest_side", "kind", &out) == KILN_OK && out == "\"sideQuest\"");
    // no view entry has the item's GUID (yet): the GUID text stays
    CHECK(s.read("quest", "game:quest_main", "rewardItems", &out) == KILN_OK &&
          json::parse(out, nullptr, false) == json::parse(R"([{"item": "00000000-0000-0000-0000-000000000020", "count": 2}])"));
    // the item exists as an itemInfo entry when its GUID matches one: the id names it
    g.res[kItem].push_back({key(0x20, kItem), json::parse(R"({"debugName": "Rope", "maxStackSize": 10})")});
    CHECK(s.read("quest", "game:quest_main", "rewardItems[0].item", &out) == KILN_OK && out == "\"game:rope\"");
    CHECK(s.read("quest", "game:quest_main", "rewardItems[0].count", &out) == KILN_OK && out == "2");
    CHECK(s.read("itemInfo", "game:wood", "maxStackSize", &out) == KILN_OK && out == "20");
    // refusals per entry, in Kiln words, logged once
    CHECK(s.read("quest", "game:quest_side", "bonus", &out) == KILN_ERR_UNAVAILABLE && out.find("quest[game:quest_side].bonus") != std::string::npos);
    CHECK(s.read("quest", "game:quest_main", "bonus", &out) == KILN_OK && out == "1.5");  // the other entries still read
    CHECK(s.read("quest", "game:quest_odd", "kind", &out) == KILN_ERR_UNAVAILABLE && out.find("no name for") != std::string::npos &&
          out.find("Daily") == std::string::npos);
    CHECK(s.read("quest", "game:quest_main", "events", &out) == KILN_ERR_UNAVAILABLE && out.find("several kinds") != std::string::npos);
    CHECK(s.read("quest", "game:quest_main", "nope", &out) == KILN_ERR_NOT_FOUND && out.find("quest has no field nope") != std::string::npos);
    CHECK(s.read("quest", "game:quest_nope", "xpReward", &out) == KILN_ERR_NOT_FOUND && out.find("no entry game:quest_nope") != std::string::npos);
    CHECK(s.read("quest", "game:quest_main", "rewardItems[5].count", &out) == KILN_ERR_UNAVAILABLE);
    const size_t logged = g.log.size();
    CHECK(has(g.log, "quest.bonus") && has(g.log, "quest.events"));
    s.read("quest", "game:quest_main", "events", &out);
    CHECK(g.log.size() == logged);  // the same refusal is logged once
    for (const auto& l : g.log) CHECK(l.find("keen::") == std::string::npos && l.find("Daily") == std::string::npos);
    // one entry, every field: refusals listed per field, the rest read
    CHECK(s.entry("quest", "game:quest_side", &out) == KILN_OK);
    {
        const json j = json::parse(out);
        CHECK(j["id"] == "game:quest_side" && j["fields"]["xpReward"] == 40 && j["fields"]["kind"] == "sideQuest");
        CHECK(j["refused"].contains("bonus") && j["refused"].contains("events") && !j["fields"].contains("bonus"));
        CHECK(out.find("keen::") == std::string::npos);
    }

    // ---- selectors, a resource added later
    CHECK(s.select("quest", "*", &out) == KILN_OK && json::parse(out) == json::parse(R"(["game:quest_main","game:quest_side","game:quest_odd"])"));
    CHECK(s.select("quest", "game:quest_s*", &out) == KILN_OK && json::parse(out) == json::parse(R"(["game:quest_side"])"));
    CHECK(s.select("quest", "Quest_Odd", &out) == KILN_OK && json::parse(out) == json::parse(R"(["game:quest_odd"])"));
    g.res[kQuest].push_back({key(4, kQuest), json::parse(R"({"debugName": "Quest_New", "xpReward": 1, "kind": "Main", "rewardItems": [], "events": []})")});
    CHECK(s.select("quest", "*", &out) == KILN_OK && json::parse(out).size() == 4);
    CHECK(s.read("quest", "game:quest_new", "xpReward", &out) == KILN_OK && out == "1");
    const int calls = g.member_calls;
    s.read("quest", "game:quest_new", "xpReward", &out);
    CHECK(g.member_calls == calls + 1);  // names are not read again while the count holds

    // ---- stable views read through the registry
    g.registry["items|game:wood|maxStack"] = "500";
    CHECK(s.read("items", "game:wood", "maxStack", &out) == KILN_OK && out == "500");
    CHECK(s.read("items", "game:wood", "fuel.burnTime", &out) == KILN_ERR_UNAVAILABLE && out.find("no game code reads it") != std::string::npos);
    CHECK(s.read("items", "game:wood", "nope", &out) == KILN_ERR_NOT_FOUND && out.find("items has no field nope") != std::string::npos);
    CHECK(s.select("items", "*", &out) == KILN_OK && json::parse(out).size() == 2);
    CHECK(s.entry("items", "game:wood", &out) == KILN_OK && json::parse(out)["fields"]["maxStack"] == 500);

    std::printf("registry-view-service-test: %d passed, %d failed\n", g_pass, g_fail);
    return g_fail ? 1 : 0;
}
