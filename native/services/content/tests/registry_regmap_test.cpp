// SPDX-License-Identifier: MIT
// kiln.content registry stage: the regmap (registry/regmap.h), on the host. How each Kiln field maps onto the game per game
// build: the common rows (paths, conversion, read time, back-end), the per-build record (verified, row states, alias
// deltas), the routing rule against the schema's "applies", the mapping state an edit gets, and the entries' Kiln ids
// (stable names, clashes, unnamed entries, drop tables named by their templates, pinned renames and removals).
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <sstream>
#include <string>

#include "../../../core/src/services/boot_gamedata.h"
#include "../registry/regmap.h"
#include "../registry/schema.g.h"

using namespace kc::registry;
using map::Mapping;

static int g_fail = 0, g_pass = 0;
#define CHECK(c)                                                                  \
    do {                                                                          \
        if (c) ++g_pass;                                                          \
        else { ++g_fail; std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #c); } \
    } while (0)

static bool has(const std::vector<std::string>& v, const std::string& part) {
    for (const std::string& s : v)
        if (s.find(part) != std::string::npos) return true;
    return false;
}

static map::RegistryMap common(const char* text, std::vector<std::string>* errs) {
    map::RegistryMap m;
    map::load_common(map::json::parse(text), &m, errs);
    return m;
}

int main(int argc, char** argv) {
    // ---- a common file: entries and rows
    {
        std::vector<std::string> errs;
        auto m = common(R"({"regmap": 1, "registry": "items",
            "entries": {"kind": "resource", "resourceType": "keen::ItemInfo", "name": "debugName", "guid": "objectId", "id": "itemId", "unnamed": "item_%08x"},
            "rows": {
              "maxStack": {"paths": ["maxStackSize"], "conv": "int", "read": "worldLoad", "backend": "live", "clientNeeds": true},
              "rarity": {"paths": ["rarity"], "conv": "enum", "values": {"common": "Common", "uncommon": "Uncommon", "rare": "Rare", "epic": "Epic", "legendary": "Legendary"}, "read": "now", "backend": "live"},
              "fuel.burnTime": {"paths": ["fuelSetup.burnTime"], "conv": "time", "read": "unknown", "backend": "live", "state": "unproven", "reason": "the read time is not traced"}
            }})", &errs);
        CHECK(errs.empty());
        CHECK(m.registry == "items" && m.entries.kind == map::EntryKind::Resource && m.entries.resource_type == "keen::ItemInfo");
        CHECK(m.rows.size() == 3 && m.rows.at("maxStack").paths == std::vector<std::string>({"maxStackSize"}));
        CHECK(m.rows.at("maxStack").read == map::Read::WorldLoad && m.rows.at("maxStack").backend == map::Backend::Live && m.rows.at("maxStack").client_needs);
        CHECK(m.rows.at("rarity").values.at("epic") == "Epic");
        CHECK(m.rows.at("fuel.burnTime").state == map::RowState::Unproven);
        // the routing rule against the generated schema: none of these breaks it (an untraced read needs the cautious applies:
        // fuel.burnTime's worldLoad; items.comfort applies now since its reader was traced, 31.x)
        CHECK(map::check_routing(m).empty());
    }
    // ---- malformed rows are refused with the field named
    {
        std::vector<std::string> errs;
        common(R"({"regmap": 1, "registry": "items", "entries": {"kind": "resource", "resourceType": "keen::ItemInfo"},
            "rows": {"maxStack": {"paths": [], "conv": "int", "read": "now", "backend": "live"},
                     "rarity": {"paths": ["rarity"], "conv": "teleport", "read": "now", "backend": "live"},
                     "rolled": {"paths": ["generateRarity"], "conv": "bool", "read": "sometimes", "backend": "live"},
                     "nonsense": {"paths": ["x"], "conv": "int", "read": "now", "backend": "live"},
                     "socket": {"paths": ["gemSlotOverride.isSet"], "conv": "bool", "read": "now", "backend": "live", "offset": 1944}}})", &errs);
        CHECK(has(errs, "items.maxStack") && has(errs, "items.rarity") && has(errs, "items.rolled"));
        CHECK(has(errs, "items.nonsense") && has(errs, "items.socket"));
    }
    // ---- routing: what the schema promises (applies) and what the game does (read, back-end) must agree
    {
        std::vector<std::string> errs;
        auto m = common(R"({"regmap": 1, "registry": "items", "entries": {"kind": "resource", "resourceType": "keen::ItemInfo"},
            "rows": {"maxStack": {"paths": ["maxStackSize"], "conv": "int", "read": "restart", "backend": "live"},
                     "rarity": {"paths": ["rarity"], "conv": "enum", "values": {"common": "Common"}, "read": "now", "backend": "boot"},
                     "fuel.burnTime": {"paths": ["fuelSetup.burnTime"], "conv": "time", "read": "unknown", "backend": "live"},
                     "rolled": {"paths": ["generateRarity"], "conv": "bool", "read": "now", "backend": "live"}}})", &errs);
        CHECK(errs.empty());
        const auto r = map::check_routing(m);
        CHECK(has(r, "items.maxStack") && has(r, "restart"));   // promised worldLoad, the game reads it at start only
        CHECK(has(r, "items.rarity") && has(r, "boot"));         // a boot write cannot act "now"
        CHECK(has(r, "items.fuel.burnTime") && has(r, "unproven"));
        CHECK(!has(r, "items.rolled"));
        auto m3 = common(R"({"regmap": 1, "registry": "items", "entries": {"kind": "resource", "resourceType": "keen::ItemInfo"},
            "rows": {"rolled": {"paths": ["generateRarity"], "conv": "bool", "read": "unknown", "backend": "live", "state": "unproven", "reason": "x"}}})", &errs);
        CHECK(has(map::check_routing(m3), "cautious applies"));   // items.rolled applies now: an untraced read time cannot promise that
        auto m2 = common(R"({"regmap": 1, "registry": "balance", "entries": {"kind": "single", "resourceType": "keen::BalancingTable", "resourceGuid": "82706b40-61b1-4b8f-8b23-dcec6971bda1"},
            "rows": {"levelCap": {"paths": ["playerLevelCap"], "conv": "int", "read": "restart", "backend": "live"}}})", &errs);
        CHECK(has(map::check_routing(m2), "balance.levelCap"));  // restart fields go through the boot back-end
    }
    // ---- a per-build record
    {
        std::vector<std::string> errs;
        map::BuildRecord b;
        map::load_record(map::json::parse(R"({"regmap": 1, "side": "server", "exeSha256": "f0d824be7fcd48ebfe2458ff5c28da26060c2074e3ec1fcac587e5a1d508aeb2",
            "gameBuild": "1287369", "steamBuild": "26430253", "verified": true, "proof": "test",
            "layouts": {"keen::ItemInfo": "0x07009d37"},
            "rows": {"items.maxStack": "ok", "items.rarity": "changed"},
            "aliases": {"renames": [{"registry": "items", "guid": "00000000-0000-0000-0000-0000000000a1", "id": "game:old_name", "since": "1287369"}],
                        "removals": [{"registry": "items", "id": "game:gone", "removedIn": "1287369"}],
                        "suffixes": [{"registry": "items", "guid": "00000000-0000-0000-0000-0000000000b2", "id": "game:twin~00000000"}]}})"), &b, &errs);
        CHECK(errs.empty());
        CHECK(b.verified && b.side == "server" && b.game_build == "1287369" && b.layouts.at("keen::ItemInfo") == 0x07009d37u);
        CHECK(b.aliases.pinned.size() == 2 && b.aliases.removed.at("items|game:gone") == "1287369");
        map::BuildRecord bad;
        map::load_record(map::json::parse(R"({"regmap": 1, "side": "moon", "exeSha256": "xyz", "verified": "yes", "rows": {"items.maxStack": "fine"}})"), &bad, &errs);
        CHECK(has(errs, "side") && has(errs, "exeSha256") && has(errs, "verified") && has(errs, "items.maxStack"));
        // kilnctl regproof: a row that only lacks lab evidence is "unproven", with its reason (kept off; not "changed")
        std::vector<std::string> e2;
        map::BuildRecord u;
        CHECK(map::load_record(map::json::parse(R"({"regmap": 1, "side": "server", "exeSha256": "f0d824be7fcd48ebfe2458ff5c28da26060c2074e3ec1fcac587e5a1d508aeb2",
            "verified": true, "rows": {"items.maxStack": "ok", "items.comfort": "unproven"},
            "reasons": {"items.comfort": "no lab round trip of it on this build yet"}})"), &u, &e2));
        CHECK(e2.empty() && u.rows.at("items.comfort") == map::BuildRow::Unproven &&
              u.reasons.at("items.comfort") == "no lab round trip of it on this build yet");
        std::vector<std::string> e3;
        map::BuildRecord r3;
        map::load_record(map::json::parse(R"({"regmap": 1, "side": "server", "exeSha256": "f0d824be7fcd48ebfe2458ff5c28da26060c2074e3ec1fcac587e5a1d508aeb2",
            "verified": true, "rows": {}, "reasons": {"items.comfort": 3}})"), &r3, &e3);
        CHECK(has(e3, "reasons.items.comfort"));
        // The 400-byte display limit must not split a UTF-8 code point (registry state JSON uses strict UTF-8).
        const std::string long_reason = std::string(399, 'a') + "é" + "more";
        auto doc = map::json{{"regmap", 1}, {"side", "server"}, {"exeSha256", std::string(64, 'a')},
                             {"verified", true}, {"rows", {{"items.comfort", "unproven"}}}, {"reasons", {{"items.comfort", long_reason}}}};
        map::BuildRecord utf8;
        std::vector<std::string> eu;
        CHECK(map::load_record(doc, &utf8, &eu));
        CHECK(utf8.reasons.at("items.comfort") == std::string(399, 'a'));
        try { (void)map::json(utf8.reasons.at("items.comfort")).dump(); CHECK(true); }
        catch (const map::json::exception&) { CHECK(false); }

    }
    // ---- the mapping state an edit gets on this build
    {
        std::vector<std::string> errs;
        map::RegMap rm;
        rm.registries["items"] = common(R"({"regmap": 1, "registry": "items", "entries": {"kind": "resource", "resourceType": "keen::ItemInfo"},
            "rows": {"maxStack": {"paths": ["maxStackSize"], "conv": "int", "read": "worldLoad", "backend": "live"},
                     "rarity": {"paths": ["rarity"], "conv": "enum", "values": {"common": "Common"}, "read": "now", "backend": "live"},
                     "comfort": {"paths": ["comfortSetup.comfortAmount"], "conv": "int", "read": "unknown", "backend": "live", "state": "unproven", "reason": "the read time is not traced"},
                     "manaCost": {"state": "unmapped", "reason": "not mapped yet"}}})", &errs);
        CHECK(errs.empty());
        std::string why;
        // no record for this exe: nothing is proven
        rm.side = "server", rm.exe_sha256 = "f0d824be7fcd48ebfe2458ff5c28da26060c2074e3ec1fcac587e5a1d508aeb2";
        CHECK(rm.mapping("items", "maxStack", &why) == Mapping::Unproven &&
              why == "Unproven: items.maxStack is not proven for this game build (server exe f0d824be) (no verified regmap record for it yet; "
                     "a Kiln update is needed); this edit was not applied");
        rm.build.verified = true, rm.build.game_build = "1287369", rm.has_build = true;
        rm.build.rows = {{"items.maxStack", map::BuildRow::Ok}, {"items.rarity", map::BuildRow::Changed}, {"items.comfort", map::BuildRow::Ok}};
        CHECK(rm.mapping("items", "maxStack", &why) == Mapping::Mapped);
        CHECK(rm.mapping("items", "rarity", &why) == Mapping::Unmapped &&
              why == "Unmapped: items.rarity is not mapped for game build 1287369 yet (a Kiln update is needed); this edit was not applied");
        CHECK(rm.mapping("items", "comfort", &why) == Mapping::Unproven &&
              why == "Unproven: items.comfort is not proven for game build 1287369 (the read time is not traced); this edit was not applied");
        CHECK(rm.mapping("items", "manaCost", &why) == Mapping::Unmapped &&
              why == "Unmapped: items.manaCost is not mapped for game build 1287369 yet (a Kiln update is needed: not mapped yet); this edit was not applied");
        CHECK(rm.mapping("items", "power", &why) == Mapping::Unmapped);        // no row at all
        CHECK(rm.mapping("items", "levels.min", &why) == Mapping::Unmapped);
        CHECK(rm.mapping("nowhere", "x", &why) == Mapping::Unmapped);
        // unproven on this build (kilnctl regproof): kept off with the record's reason, never "a Kiln update is needed"
        rm.build.rows["items.maxStack"] = map::BuildRow::Unproven;
        rm.build.reasons["items.maxStack"] = "no lab round trip of it on this build yet";
        CHECK(rm.mapping("items", "maxStack", &why) == Mapping::Unproven &&
              why == "Unproven: items.maxStack is not proven for game build 1287369 (no lab round trip of it on this build yet); this edit was not applied");
        rm.build.reasons.clear();
        CHECK(rm.mapping("items", "maxStack", &why) == Mapping::Unproven && why.find("(no lab evidence on this build)") != std::string::npos);
        rm.build.rows["items.maxStack"] = map::BuildRow::Ok;
        rm.build.verified = false;
        CHECK(rm.mapping("items", "maxStack", &why) == Mapping::Unproven);
    }
    // ---- ids: the cases native and tools/registrygen/regmap_ids.py must both give (registry_ids_cases.json)
    if (argc > 2) {
        std::ifstream f(argv[2]);
        std::stringstream ss;
        ss << f.rdbuf();
        const auto doc = map::json::parse(ss.str(), nullptr, false);
        CHECK(!doc.is_discarded() && doc["cases"].size() >= 8);
        for (const auto& c : doc["cases"]) {
            map::EntrySource src;
            const auto& s = c["src"];
            src.unnamed = s.value("unnamed", ""), src.name_from = s.value("nameFrom", ""), src.key = s.value("key", "");
            map::Aliases al;
            const std::string reg = c["registry"];
            const map::json pinned = c["aliases"].value("pinned", map::json::object()), removed = c["aliases"].value("removed", map::json::object());
            for (const auto& [g, id] : pinned.items()) al.pinned[reg + "|" + g] = id;
            for (const auto& [id, b] : removed.items()) al.removed[reg + "|" + id] = b;
            std::vector<map::GameEntry> es;
            for (const auto& e : c["entries"]) {
                map::GameEntry x{e["guid"], e["name"], e["id"].get<uint32_t>(), {}};
                const map::json named = e.value("namedBy", map::json::array());
                for (const auto& t : named) x.named_by.push_back(t);
                es.push_back(x);
            }
            std::map<uint32_t, std::string> by;
            const map::json by_item = c.value("byItemId", map::json::object());
            for (const auto& [k, v] : by_item.items()) by[static_cast<uint32_t>(std::stoul(k))] = v;
            std::map<std::string, std::string> by_guid;  // a registry keyed by another by GUID (creatures: their template)
            const map::json by_key = c.value("byKeyGuid", map::json::object());
            for (const auto& [k, v] : by_key.items()) by_guid[k] = v;
            std::vector<std::string> notes;
            const auto ids = map::assign_ids(reg, src, al, es, c.contains("byItemId") ? &by : nullptr, &notes, c.contains("byKeyGuid") ? &by_guid : nullptr);
            const auto want = c["expect"].get<std::vector<std::string>>();
            if (ids != want) {
                std::printf("  case \"%s\":", c["name"].get<std::string>().c_str());
                for (const auto& i : ids) std::printf(" %s", i.c_str());
                std::printf("\n");
            }
            CHECK(ids == want);
            CHECK(notes.size() == c.value("notes", 0u));
            for (const auto& i : ids) CHECK(map::is_kiln_id(i));
        }
        std::printf("ids: %zu cases\n", doc["cases"].size());
    }
    // ---- the id grammar
    for (const char* ok : {"game:x", "game:_hidden", "mymod:crate.v2", "game:twin~1a2b3c4d", "game:x~0123456789abcdef0123456789abcdef"})
        CHECK(map::is_kiln_id(ok));
    for (const char* bad : {"", "game:", ":x", "Game:x", "game:X", "game:a b", "game:x~1a2b", "game:x~zzzzzzzz", "_mod:x", "a:b:c", "game:x~1a2b3c4d~1a2b3c4d"})
        CHECK(!map::is_kiln_id(bad));
    // ---- a row that breaks the routing rule loads as unproven (load_dir)
    if (argc > 3) {
        namespace fs = std::filesystem;
        const fs::path d = fs::path(argv[3]) / "regmap-broken";
        fs::remove_all(d);
        fs::create_directories(d / "common");
        fs::create_directories(d / "server");
        const std::string items = R"({"regmap": 1, "registry": "items", "entries": {"kind": "resource", "resourceType": "keen::ItemInfo"},
            "rows": {"maxStack": {"paths": ["maxStackSize"], "conv": "int", "read": "restart", "backend": "live"},
                     "rolled": {"paths": ["generateRarity"], "conv": "bool", "read": "now", "backend": "live"}}})";
        std::ofstream(d / "common" / "items.json") << items;
        // kilnctl regproof's regmapDigest of this common folder: sha256 over "<file name>\n<sha256 of its bytes>\n"
        namespace gd = kiln::boot_gd;
        const std::string digest = gd::hex_of(gd::sha256("items.json\n" + gd::hex_of(gd::sha256(items)) + "\n"));
        const std::string sha(64, 'a');
        auto record = [&](const std::string& digest_member) {
            std::ofstream(d / "server" / (sha + ".json")) << R"({"regmap": 1, "side": "server", "exeSha256": ")" + sha +
                R"(", "gameBuild": "1", "verified": true, )" + digest_member + R"("rows": {"items.maxStack": "ok", "items.rolled": "ok"}})";
        };
        record(R"("regmapDigest": ")" + digest + R"(", )");
        map::RegMap rm;
        std::vector<std::string> errs;
        CHECK(map::load_dir(d.string(), "server", sha, &rm, &errs));
        CHECK(has(errs, "items.maxStack"));
        std::string why;
        CHECK(rm.mapping("items", "maxStack", &why) == Mapping::Unproven && why.find("routing rule") != std::string::npos);
        CHECK(rm.mapping("items", "rolled", &why) == Mapping::Mapped);
        // review2 S1: a record proves the mapping it was proven on. Another digest, none, or a common file edited after the
        // proof: no row is enabled, refused as Unproven naming the reason
        CHECK(rm.common_digest == digest && rm.build.regmap_digest == digest);
        for (const std::string& member : {R"("regmapDigest": ")" + std::string(64, 'b') + R"(", )", std::string()}) {
            record(member);
            map::RegMap other;
            errs.clear();
            CHECK(map::load_dir(d.string(), "server", sha, &other, &errs) && other.has_build && other.build.verified);
            CHECK(other.mapping("items", "rolled", &why) == Mapping::Unproven && why.find("differs from the one this build's record was proven on") != std::string::npos);
            // review3 S4: a record of another mapping is not a verified one (status surfaces and capabilities)
            CHECK(!other.verified() && why.find("a Kiln update is needed") != std::string::npos);
        }
        CHECK(rm.verified());
        record(R"("regmapDigest": ")" + digest + R"(", )");
        std::ofstream(d / "common" / "items.json") << items << "\n";
        map::RegMap edited;
        errs.clear();
        CHECK(map::load_dir(d.string(), "server", sha, &edited, &errs) && edited.common_digest != digest);
        CHECK(edited.mapping("items", "rolled", &why) == Mapping::Unproven && why.find("differs from the one this build's record was proven on") != std::string::npos);
        CHECK(!edited.verified() && why.find("a Kiln update is needed") != std::string::npos && why.find("((") == std::string::npos);
        // review3 S4: the advice fits the case. This server's own record (kilnctl regproof --mark) after common changed: prove
        // it again; a file in common that is not a registry's own (a copy left beside it): remove it, or repair Kiln
        std::ofstream(d / "server" / (sha + ".json")) << R"({"regmap": 1, "side": "server", "exeSha256": ")" + sha +
            R"(", "gameBuild": "1", "verified": true, "markedLocally": true, "regmapDigest": ")" + digest + R"(", "rows": {"items.rolled": "ok"}})";
        map::RegMap local;
        errs.clear();
        CHECK(map::load_dir(d.string(), "server", sha, &local, &errs) && local.build.marked_locally && !has(errs, "markedLocally"));
        CHECK(local.mapping("items", "rolled", &why) == Mapping::Unproven && why.find("kilnctl regproof") != std::string::npos &&
              why.find("Kiln update") == std::string::npos);
        std::ofstream(d / "common" / "items.json") << items;
        std::ofstream(d / "common" / "items - Copy.json") << items;
        map::RegMap stray;
        errs.clear();
        CHECK(map::load_dir(d.string(), "server", sha, &stray, &errs) && stray.stray_common.size() == 1);
        CHECK(stray.mapping("items", "rolled", &why) == Mapping::Unproven && why.find("items - Copy.json") != std::string::npos &&
              why.find("repair or reinstall Kiln") != std::string::npos && why.find("kilnctl") == std::string::npos);
        fs::remove(d / "common" / "items - Copy.json");
        record(R"("regmapDigest": "not hex", )");
        map::RegMap bad;
        errs.clear();
        map::load_dir(d.string(), "server", sha, &bad, &errs);
        CHECK(has(errs, "regmapDigest"));
        fs::remove_all(d);
    }
    // ---- the repository's regmap: every file loads, every row routes as the schema says, the records are whole
    if (argc > 1) {
        map::RegMap rm;
        std::vector<std::string> errs;
        for (const char* side : {"server", "client"}) {
            const std::string sha = std::string(side) == "server" ? "f0d824be7fcd48ebfe2458ff5c28da26060c2074e3ec1fcac587e5a1d508aeb2"
                                                                  : "5d9ef2f140b8ed49e30dbee655841b89d08baec20f799a1817e171a18a850c77";
            map::RegMap one;
            const bool ok = map::load_dir(argv[1], side, sha, &one, &errs);
            CHECK(ok);
            for (const std::string& e : errs) std::printf("  regmap: %s\n", e.c_str());
            CHECK(errs.empty());
            CHECK(one.has_build && one.build.verified && one.build.side == side);
            // review2 S1: every committed verified record is bound to this tree's regmap/common (a common edit needs a new proof)
            if (one.build.regmap_digest != one.common_digest)
                std::printf("  regmap %s: the record's regmapDigest %s is not regmap/common's %s\n", side, one.build.regmap_digest.c_str(), one.common_digest.c_str());
            CHECK(one.build.regmap_digest == one.common_digest);
            CHECK(one.registries.size() == 5);
            for (const auto& [name, reg] : one.registries) {
                const auto r = map::check_routing(reg);
                for (const std::string& e : r) std::printf("  routing: %s\n", e.c_str());
                CHECK(r.empty());
            }
            // S6: every R1 field is mapped on this build, or explicitly unproven with its reason; none unmapped
            int mapped = 0, unproven = 0, unmapped = 0;
            for (const gen::Field& f : gen::kFields) {
                std::string why;
                const Mapping m = one.mapping(f.registry, f.path, &why);
                (m == Mapping::Mapped ? mapped : m == Mapping::Unproven ? unproven : unmapped)++;
                if (m == Mapping::Unmapped) std::printf("  unmapped on %s: %s\n", side, why.c_str());
            }
            std::printf("regmap %s: %d mapped, %d unproven, %d unmapped\n", side, mapped, unproven, unmapped);
            CHECK(unmapped == 0 && mapped + unproven == static_cast<int>(std::size(gen::kFields)));
            // review2 N9: the shipped behaviour of the records, not only their counts. A proven row applies; a row the game's
            // read time is not traced for stays off with that reason; the client record mirrors the server proof row for row
            // (owner ruling 2026-11-23: no client proof of its own), and keeps the server's reasons.
            std::string why;
            CHECK(one.mapping("items", "maxStack", &why) == Mapping::Mapped);
            // registry-proof (31.x): the drop rows (lab drop tables), the loot rows (the roll's disassembly), block and comfort (their readers
            // found) are proven; two rows stay off with a proven reason, refused cleanly
            for (const auto& [reg, field] : {std::pair{"items", "block"}, {"items", "comfort"}, {"loot", "rarities"}, {"loot", "fixedRarity"},
                                             {"loot", "stack.scalable"}, {"loot", "stack.maxScaled"}, {"drops", "entries"}, {"drops", "entries[].item"},
                                             {"drops", "entries[].count.min"}, {"drops", "entries[].count.max"}, {"drops", "entries[].chance"},
                                             {"drops", "entries[].weight"}}) {
                const bool ok = one.mapping(reg, field, &why) == Mapping::Mapped;
                if (!ok) std::printf("  %s: %s.%s: %s\n", side, reg, field, why.c_str());
                CHECK(ok);
            }
            CHECK(one.mapping("items", "fuel.burnTime", &why) == Mapping::Unproven && why.find("no game code reads") != std::string::npos);
            CHECK(one.mapping("recipes", "station", &why) == Mapping::Unproven && why.find("no stations registry") != std::string::npos);
            CHECK(mapped == static_cast<int>(std::size(gen::kFields)) - 2 && unproven == 2);
            if (std::string(side) == "client") {
                // review3 N4: row for row only where the client's layout of the registry's root type is the server's (a
                // later client layout may differ legitimately: those rows are unproven); never ok where the server is not
                int same_layout = 0;
                for (const auto& [q, state] : one.build.rows) {
                    const auto reg = one.registries.find(q.substr(0, q.find('.')));
                    CHECK(reg != one.registries.end());
                    if (reg == one.registries.end()) continue;
                    const std::string& root = reg->second.entries.resource_type;
                    const auto cl = one.build.layouts.find(root), sl = rm.build.layouts.find(root);
                    const auto server_row = rm.build.rows.find(q);
                    CHECK(state != map::BuildRow::Ok || (server_row != rm.build.rows.end() && server_row->second == map::BuildRow::Ok));
                    if (cl == one.build.layouts.end() || sl == rm.build.layouts.end() || cl->second != sl->second) continue;
                    ++same_layout;
                    CHECK(server_row != rm.build.rows.end() && server_row->second == state);
                    const auto cr = one.build.reasons.find(q), sr = rm.build.reasons.find(q);
                    CHECK((cr == one.build.reasons.end()) == (sr == rm.build.reasons.end()) && (cr == one.build.reasons.end() || cr->second == sr->second));
                }
                std::printf("regmap client: %d of %zu rows compared row for row (same layout as the server's)\n", same_layout, one.build.rows.size());
                CHECK(one.build.game_build == rm.build.game_build && one.build.regmap_digest == rm.build.regmap_digest);
            }
            rm = std::move(one);
        }
        std::printf("regmap: %zu registries loaded from %s\n", rm.registries.size(), argv[1]);
    }
    std::printf("registry-regmap-test: %d passed, %d failed\n", g_pass, g_fail);
    return g_fail ? 1 : 0;
}
