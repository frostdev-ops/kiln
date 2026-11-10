// SPDX-License-Identifier: MIT
// Compatibility rules and the overlap report (core/src/compat_rules.cpp, compat_overlaps.cpp) under Wine, no game:
//   compat_test.exe <schema/fixtures dir>
// The rule cases are schema/fixtures/compat-rules/cases.json, the file the launcher's Rust reader is tested with too.
#include <windows.h>

#include <cstdio>
#include <fstream>
#include <sstream>
#include <string>

#include "compat.h"
#include "compat_rules.h"
#include "nlohmann/json.hpp"

namespace {

using json = nlohmann::ordered_json;
using namespace kiln::compat;

int g_checks = 0, g_failed = 0;

void check(bool ok, const std::string& what) {
    ++g_checks;
    if (!ok) {
        ++g_failed;
        std::printf("FAIL %s\n", what.c_str());
    }
}

std::string slurp(const std::string& path) {
    std::ifstream in(path, std::ios::binary);
    std::stringstream ss;
    ss << in.rdbuf();
    return ss.str();
}

Rule one_rule(const json& j, const char* source = "test") {
    json doc = {{"format", 1}, {"rules", json::array({j})}};
    ParseResult r = parse_rules(doc.dump(), source);
    return r.rules.empty() ? Rule{} : r.rules[0];
}

std::vector<std::string> strings(const json& j) {
    std::vector<std::string> out;
    if (j.is_array())
        for (const auto& s : j) out.push_back(s.get<std::string>());
    return out;
}

void rule_cases(const std::string& dir) {
    const json cases = json::parse(slurp(dir + "/compat-rules/cases.json"));
    for (const auto& c : cases["glob"])
        check(glob_match(c["pattern"].get<std::string>(), c["name"].get<std::string>()) == c["match"].get<bool>(),
              "glob " + c["pattern"].get<std::string>() + " vs " + c["name"].get<std::string>());
    for (const auto& c : cases["parse"]) {
        const std::string name = "parse: " + c["name"].get<std::string>();
        const std::string text = c["input"].is_string() ? c["input"].get<std::string>() : c["input"].dump();
        const ParseResult r = parse_rules(text, "test");
        check(r.ok == c["ok"].get<bool>(), name + " (ok)");
        std::vector<std::string> ids;
        for (const Rule& rule : r.rules) ids.push_back(rule.id);
        check(ids == strings(c["rules"]), name + " (kept ids)");
        check(r.dropped == c["dropped"].get<int>(), name + " (dropped " + std::to_string(r.dropped) + ")");
    }
    for (const auto& c : cases["merge"]) {
        const std::string name = "merge: " + c["name"].get<std::string>();
        auto load = [](const json& list) {
            std::vector<Rule> out;
            if (list.is_array())
                for (const auto& j : list) out.push_back(one_rule(j));
            return out;
        };
        std::vector<std::string> replaced;
        const auto merged = merge_rules(load(c["shipped"]), load(c["user"]), &replaced);
        std::vector<std::string> ids;
        for (const Rule& r : merged) ids.push_back(r.id);
        check(ids == strings(c["ids"]), name + " (ids)");
        check(replaced == strings(c["replaced"]), name + " (replaced)");
        if (c.contains("messages"))
            for (const auto& [id, message] : c["messages"].items())
                for (const Rule& r : merged)
                    if (r.id == id) check(r.message == message.get<std::string>(), name + " (message of " + id + ")");
    }
    for (const auto& c : cases["evaluate"]) {
        const std::string name = "evaluate: " + c["name"].get<std::string>();
        std::vector<Rule> rules;
        for (const auto& j : c["rules"]) rules.push_back(one_rule(j));
        Facts f;
        f.modules = strings(c["facts"]["modules"]);
        f.engines = strings(c["facts"]["engines"]);
        f.mods = strings(c["facts"]["mods"]);
        f.symbols = strings(c["facts"]["symbols"]);
        std::vector<std::string> fired;
        for (const Rule* r : evaluate(rules, f)) fired.push_back(r->id);
        check(fired == strings(c["fires"]), name);
    }
}

// ---- overlaps ----

ChainRow row(const char* file, const char* status, bool late = false, const char* source = "chain") {
    ChainRow r;
    r.path = std::string("Z:\\game\\kiln\\chain\\") + file;
    r.file = file;
    r.source = source;
    r.status = status;
    r.late = late;
    return r;
}

json target(const char* rva, const char* symbol, std::vector<const char*> owners, json before = nullptr, json over = nullptr) {
    json links = json::array();
    for (const char* o : owners) links.push_back({{"owner", o}, {"kind", "raw"}, {"priority", 0}, {"label", "x"}});
    return {{"rva", rva}, {"symbol", symbol ? json(symbol) : json(nullptr)}, {"label", symbol ? symbol : "rva"}, {"links", links},
            {"foreignBefore", before}, {"foreignOver", over}};
}

json dest(const char* module, const char* offset, bool own = false) {
    return {{"module", module}, {"offset", offset}, {"address", "0x7ff000001000"}, {"own", own}};
}

void overlap_cases() {
    check(normalize_owner("service:kiln-chat.dll") == "chat", "owner service:kiln-chat.dll");
    check(normalize_owner("plugin:kiln-shroudforge.dll") == "shroudforge", "owner plugin:kiln-shroudforge.dll");
    check(normalize_owner("emf:coolmod") == "emf:coolmod", "owner emf:coolmod");
    check(normalize_owner("native:fps-unlock") == "native:fps-unlock", "owner native:fps-unlock");
    check(normalize_owner("kiln") == "kiln", "owner kiln");
    check(normalize_owner("kiln-") == "kiln-", "owner kiln-");

    // a) foreign-before from an early chain entry: fix late
    json map = {{"targets", json::array({target("0x1000", "chat/ChatLogAdd", {"service:kiln-chat.dll", "plugin:kiln-emf.dll", "service:kiln-chat.dll"},
                                                dest("xhl-core.dll", "0x2a0"))})},
                {"foreign", json::array()}};
    std::vector<ChainRow> chain = {row("xhl-core.dll", "loaded")};
    json o = compute_overlaps(map, chain, {});
    check(o.size() == 1, "foreign-before: one overlap");
    if (o.size() == 1) {
        check(o[0]["kind"] == "foreign-before" && o[0]["symbol"] == "chat/ChatLogAdd" && o[0]["rva"] == "0x1000", "foreign-before: kind, symbol, rva");
        check(o[0]["foreign"]["module"] == "xhl-core.dll" && o[0]["foreign"]["offset"] == "0x2a0", "foreign-before: the foreign side");
        check(strings(o[0]["owners"]) == std::vector<std::string>({"chat", "emf"}), "foreign-before: owners are short and unique");
        check(o[0]["fix"] == "late" && o[0]["chainEntry"]["status"] == "loaded" && o[0]["chainEntry"]["late"] == false, "foreign-before: fix late, chain entry");
        const std::string msg = o[0]["message"].get<std::string>();
        check(msg.find("xhl-core.dll+0x2a0") != std::string::npos && msg.find("chat, emf") != std::string::npos &&
              msg.find("[late]") != std::string::npos, "foreign-before: the message names both sides and the fix: " + msg);
    }
    // the same module already late: nothing to move
    o = compute_overlaps(map, {row("XHL-Core.dll", "loaded", true)}, {});
    check(o.size() == 1 && o[0]["fix"] == "none" && o[0]["chainEntry"]["late"] == true, "foreign-before from a late entry: fix none");
    // not a chain entry: a rule
    o = compute_overlaps(map, {row("other.dll", "loaded")}, {});
    check(o.size() == 1 && o[0]["fix"] == "rule" && o[0]["chainEntry"].is_null(), "foreign-before from a module the chain does not know: fix rule");
    // b) foreign-over, and both on one target; Kiln's own module is no overlap
    map = {{"targets", json::array({target("0x2000", "gameplay/InventoryTransfer", {"service:kiln-items.dll", "emf:coolmod"},
                                           dest("kiln-emf.dll", "0x10", true), dest("xhl-core.dll", "0x4410")),
                                    target("0x3000", nullptr, {"native:fps"}, dest("kiln-emf.dll", "0x20", true)),
                                    target("0x4000", "time/Tick", {}, dest("xhl-core.dll", "0x1"))})},
           {"foreign", json::array()}};
    o = compute_overlaps(map, chain, {});
    check(o.size() == 1, "foreign-over: own modules and link-less targets make no overlap (" + std::to_string(o.size()) + ")");
    if (o.size() == 1) {
        check(o[0]["kind"] == "foreign-over" && o[0]["fix"] == "none", "foreign-over: kind, fix none");
        check(strings(o[0]["owners"]) == std::vector<std::string>({"items", "emf:coolmod"}), "foreign-over: owners");
    }
    map["targets"].push_back(target("0x5000", "a/B", {"service:kiln-a.dll"}, dest("one.dll", "0x1"), dest("two.dll", "0x2")));
    o = compute_overlaps(map, chain, {});
    check(o.size() == 3 && o[1]["kind"] == "foreign-before" && o[2]["kind"] == "foreign-over", "before and over on one target are two overlaps");
    // an unnamed jump target
    map = {{"targets", json::array({target("0x6000", "a/B", {"service:kiln-a.dll"}, {{"module", ""}, {"offset", "0x0"}, {"address", "0x1b2c3d4e0000"}, {"own", false}})})}};
    o = compute_overlaps(map, chain, {});
    check(o.size() == 1 && o[0]["fix"] == "rule" && o[0]["message"].get<std::string>().find("unnamed") != std::string::npos, "an unnamed foreign jump: fix rule");
    // no overlaps in a quiet map; a map without sections
    check(compute_overlaps(json::object(), chain, {}).empty(), "an empty document has no overlaps");
    check(compute_overlaps({{"targets", json::array({target("0x1", "a/B", {"service:kiln-a.dll"})})}}, chain, {}).empty(), "a clean target has none");

    // c) refused (ShroudForge)
    const char* sf = R"j({"mode":"hosted","runtime":"running","native":"types=3/3{reflected_ecs_types=3} game_thread=unavailable(game-thread:original-bytes-mismatch) entity_hooks{}"})j";
    const auto refused = parse_sf_status(sf);
    check(refused.size() == 1 && refused[0].hook == "game-thread" && refused[0].reason == "original-bytes-mismatch", "SF status: game_thread=unavailable(hook:reason)");
    check(sf_running(sf), "SF status: hosted counts as running");
    check(parse_sf_status(R"j({"mode":"hosted","native":"game_thread=ready(thread=5)"})j").empty(), "SF status: ready is no refusal");
    check(parse_sf_status(R"j({"mode":"hosted","native":"game_thread=unavailable"})j").empty(), "SF status: bare unavailable is no refusal");
    check(parse_sf_status("not json").empty() && !sf_running("not json") && !sf_running(R"j({"mode":"off"})j"), "SF status: garbage and off");
    const auto plain = parse_sf_status(R"j({"mode":"hosted","native":"game_thread=unavailable(signature-not-unique-or-missing)"})j");
    check(plain.size() == 1 && plain[0].hook == "game-thread" && plain[0].reason == "signature-not-unique-or-missing", "SF status: a reason without a hook name");

    map = {{"targets", json::array()},
           {"foreign", json::array({{{"symbol", "a/B"}, {"rva", "0x1"}, {"module", "xhl-core.dll"}, {"offset", "0x5"}, {"own", false}},
                                    {{"symbol", "c/D"}, {"rva", "0x2"}, {"module", "xhl-core.dll"}, {"offset", "0x9"}, {"own", false}},
                                    {{"symbol", "e/F"}, {"rva", "0x3"}, {"module", "kiln-emf.dll"}, {"offset", "0x9"}, {"own", true}}})}};
    o = compute_overlaps(map, chain, refused);
    check(o.size() == 1 && o[0]["kind"] == "refused", "refused: one overlap");
    if (o.size() == 1) {
        check(o[0]["symbol"].is_null() && o[0]["rva"].is_null() && strings(o[0]["owners"]) == std::vector<std::string>({"shroudforge"}), "refused: no symbol, owner shroudforge");
        check(o[0]["hook"] == "game-thread" && o[0]["reason"] == "original-bytes-mismatch", "refused: hook and reason");
        check(o[0]["foreign"]["module"] == "xhl-core.dll" && o[0]["fix"] == "late" && o[0]["chainEntry"]["status"] == "loaded", "refused: the only patching module is the suspect, fix late");
        check(strings(o[0]["suspects"]) == std::vector<std::string>({"xhl-core.dll"}), "refused: suspects");
    }
    map["foreign"].push_back({{"symbol", "g/H"}, {"rva", "0x4"}, {"module", "reshade64.dll"}, {"offset", "0x1"}, {"own", false}});
    o = compute_overlaps(map, chain, refused);
    check(o.size() == 1 && o[0]["foreign"]["module"].is_null() && o[0]["fix"] == "rule" && o[0]["suspects"].size() == 2, "refused with two suspects: module unknown, fix rule");
    o = compute_overlaps(json::object(), chain, refused);
    check(o.size() == 1 && o[0]["fix"] == "none" && o[0]["suspects"].empty() && o[0]["foreign"]["module"].is_null() &&
          o[0]["message"].get<std::string>().find("cannot tell") != std::string::npos, "refused with no suspect: fix none");
    o = compute_overlaps(json::object(), chain, plain);
    check(o.size() == 1 && o[0]["fix"] == "none", "refused for another reason: fix none");
    // deterministic: same input, same output
    check(compute_overlaps(map, chain, refused) == compute_overlaps(map, chain, refused), "compute_overlaps is deterministic");
}

}  // namespace

int main(int argc, char** argv) {
    const std::string dir = argc > 1 ? argv[1] : "schema/fixtures";
    rule_cases(dir);
    overlap_cases();
    std::printf("compat_test: %d checks, %d failed\n", g_checks, g_failed);
    return g_failed ? 1 : 0;
}
