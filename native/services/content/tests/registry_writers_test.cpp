// SPDX-License-Identifier: MIT
// Host test of the registry's archive-writer attribution (registry/archive_journal.cpp, commitment 7 / D1): kiln-eml's
// startup change journal read against the start's live generation (the schema's own valid fixture, a stale one, broken
// ones), its packages named on the entries whose resources they wrote, the named rows below every Kiln writer in the
// chain (certainty "named"), the conflict line that says the package wrote the entry before Kiln and Kiln's edit wins, and
// registry.json's base.journal. argv[1]: the repository root (for the schema fixture); argv[2] (optional): where to write
// the document it builds, for a schema check against schema/state-registry.schema.json.
#include <chrono>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <functional>
#include <set>
#include <sstream>
#include <string>

#include <unistd.h>

#include "../registry/archive_base.h"
#include "../registry/archive_journal.h"
#include "../registry/drift.h"
#include "../registry/report.h"

using namespace kc::registry;

static int g_fail = 0, g_pass = 0;
#define CHECK(cond, what)                                                       \
    do {                                                                        \
        if (cond) ++g_pass;                                                     \
        else {                                                                  \
            ++g_fail;                                                           \
            std::printf("FAIL %s:%d %s\n", __FILE__, __LINE__, std::string(what).c_str()); \
        }                                                                       \
    } while (0)

static const std::string kGen = std::string(64, 'a');
static const std::string kStoneGuid = "01234567-89ab-cdef-0123-456789abcdef";  // the fixture's resource alpha, bravo and delta write
static const std::string kCreated = "5d2f8a90-1c3b-4e7f-9a12-6b4c8d0e2f13";     // alpha's first creation
static const std::string kStone = "game:material_t1_stone", kLogs = "game:material_t1_wood_logs", kNew = "game:alpha_item";

static Schema schema() {
    Schema s;
    RegistrySpec items;
    items.name = "items", items.version = "1.0.0";
    FieldSpec ms;
    ms.name = "maxStack", ms.type = FieldType::Int, ms.min = 1, ms.max = 65535, ms.applies = Applies::WorldLoad;
    FieldSpec perks;
    perks.name = "perks", perks.type = FieldType::List, perks.element = FieldType::Id, perks.max_items = 5, perks.applies = Applies::Now;
    FieldSpec up;
    up.name = "upgradesTo", up.type = FieldType::Id, up.ref = "items", up.applies = Applies::Now;
    items.fields[ms.name] = ms;
    items.fields[perks.name] = perks;
    items.fields[up.name] = up;
    s.registries["items"] = items;
    return s;
}

static BaseModel base() {
    BaseModel b;
    auto item = [](const std::string& id, int64_t stack) {
        BaseModel::Entry e;
        e.id = id;
        e.fields["maxStack"] = Value::integer(stack);
        e.fields["perks"] = Value::of_list({});
        e.fields["upgradesTo"] = Value::text("game:material_t1_wood_logs");
        return e;
    };
    b.registries["items"] = {item(kStone, 25), item(kLogs, 10), item(kNew, 3)};
    return b;
}

static const std::map<std::string, std::string> kIds = {
    {"items|" + kStone, kStoneGuid}, {"items|" + kLogs, "aaaaaaaa-0000-4000-8000-000000000001"}, {"items|" + kNew, kCreated}};

static Batch batch(const Schema& s, const std::string& mod, const std::string& stage, const std::string& edits) {
    Batch b;
    std::vector<Refusal> r;
    std::string err;
    if (!parse_batch(std::string(R"({"ir": 1, "mod": ")") + mod + R"(", "stage": ")" + stage + R"(", "source": "content/)" + mod +
                         R"(.json", "edits": )" + edits + "}",
                     s, &b, &r, &err))
        std::printf("batch: %s\n", err.c_str());
    return b;
}
static ModInput mod(const std::string& id, std::vector<Batch> batches) {
    ModInput m;
    m.id = id;
    m.batches = std::move(batches);
    return m;
}

static std::string fixture(const std::string& root) {
    std::ifstream f(root + "/schema/sfi/eml-journal/fixtures/valid.json");
    std::stringstream ss;
    ss << f.rdbuf();
    const json all = json::parse(ss.str(), nullptr, false);
    if (all.is_discarded() || !all.contains("startup-journal")) return "{}";
    return all["startup-journal"]["mixed_retry"].dump();
}

int main(int argc, char** argv) {
    const std::string root = argc > 1 ? argv[1] : "../../../..";
    const std::string text = fixture(root);
    const Schema s = schema();

    // ---- the reader ----
    const ArchiveJournal j = parse_archive_journal(text, kGen);
    CHECK(j.state == "read" && j.dialect == "shroudforge" && !j.truncated, "the schema's valid fixture reads against its generation: " + j.state + " " + j.text);
    CHECK(j.packages.size() == 4 && j.packages[0].id == "alpha" && j.packages[1].kind == "eml" && j.packages[3].id == "delta",
          "its packages in run order, with their kind");
    CHECK(j.packages.size() == 4 && j.packages[0].changed.size() == 1 && j.packages[0].changed[0] == kStoneGuid &&
              j.packages[0].created.size() == 2 && j.packages[0].created[0] == kCreated,
          "a package's written and created resources, GUIDs lower case");
    const ArchiveJournal stale = parse_archive_journal(text, std::string(64, 'c'));
    CHECK(stale.state == "stale" && stale.packages.empty() && stale.text.find("another generation") != std::string::npos,
          "a journal of another generation names nobody: " + stale.text);
    CHECK(parse_archive_journal("{", kGen).state == "unreadable", "not JSON: unreadable");
    json other = json::parse(text);
    other["format"] = 2;
    CHECK(parse_archive_journal(other.dump(), kGen).state == "unreadable", "format 1's rows as format 2 (no member paths): unreadable");
    other["format"] = 3;
    CHECK(parse_archive_journal(other.dump(), kGen).state == "unreadable", "another format: unreadable");
    json broken = json::parse(text);
    broken["packages"][0]["changed"][0]["guid"] = 7;
    const ArchiveJournal b = parse_archive_journal(broken.dump(), kGen);
    CHECK(b.state == "unreadable" && b.packages.empty(), "a malformed package: unreadable, nothing named: " + b.text);
    // review: a package is named only as the format names one (a ShroudForge identifier, kind shroudforge or eml, once):
    // anything else is a corrupt journal, which names nobody (a line break in a name would forge kiln.log lines)
    for (const auto& [what, edit] : std::vector<std::pair<std::string, std::function<void(json&)>>>{
             {"an id with a line break", [](json& d) { d["packages"][0]["id"] = "alpha\nregistry: forged line"; }},
             {"an id with a space", [](json& d) { d["packages"][0]["id"] = "two words"; }},
             {"an id of 81 characters", [](json& d) { d["packages"][0]["id"] = std::string(81, 'a'); }},
             {"a kind that is not shroudforge or eml", [](json& d) { d["packages"][0]["kind"] = "dll"; }},
             {"no kind", [](json& d) { d["packages"][0].erase("kind"); }},
             {"one id twice", [](json& d) { d["packages"][1]["id"] = d["packages"][0]["id"]; }}}) {
        json bad = json::parse(text);
        edit(bad);
        const ArchiveJournal x = parse_archive_journal(bad.dump(), kGen);
        CHECK(x.state == "unreadable" && x.packages.empty(), what + ": unreadable, nothing named: " + x.state + " " + x.text);
    }
    json big = json::parse(text);
    big["truncated"] = true;
    big["packages"][1]["truncated"] = true;
    const ArchiveJournal t = parse_archive_journal(big.dump(), kGen);
    CHECK(t.state == "read" && t.truncated && t.packages[1].truncated, "a truncated journal still names what it lists, and says it is truncated");
    CHECK(archive_writer_who("shroudforge", "alpha") == "shroudforge:alpha" && archive_writer_who("eml", "bravo") == "eml:bravo",
          "who: <kind>:<package>");
    CHECK(archive_writer_label("shroudforge:alpha") == "alpha (ShroudForge package)" && archive_writer_label("eml:bravo") == "bravo (EML package)",
          "a package's label in messages");

    // ---- the contract with kiln-eml's writer (review): the record kiln-eml's own code makes of a fixed pass
    // (gpl/kiln-eml/kiln-eml/tests/journal_contract.rs fails unless this fixture is byte for byte that record) is read and
    // named here, so a format change on either side fails a test. Its GUIDs are the game's key bytes' text as Kiln's id
    // table spells them (game.cpp guid_text: keen::Guid's Windows layout, printed the usual way). ----
    {
        std::ifstream wf(root + "/schema/sfi/eml-journal/fixtures/kiln-eml-writer.json");
        std::stringstream ws;
        ws << wf.rdbuf();
        const std::string written = ws.str();
        std::string live;
        for (int i = 0; i < 32; ++i) live += "ab";
        const ArchiveJournal w = parse_archive_journal(written, live);
        const std::string stone = "1d622bd9-8170-41b1-822e-d3984b386d95", logs = "aaaaaaaa-0000-4000-8000-000000000001",
                          made = "5d2f8a90-1c3b-4e7f-9a12-6b4c8d0e2f13", own = "0e0e0e0e-1111-4222-8333-444444444444",
                          table = "82706b40-61b1-4b8f-8b23-dcec6971bda1";
        CHECK(!written.empty() && w.state == "read" && w.dialect == "shroudforge" && !w.truncated, "kiln-eml's own record reads: " + w.state + " " + w.text);
        CHECK(w.packages.size() == 3 && w.packages[0].id == "alpha" && w.packages[0].kind == "shroudforge" && w.packages[0].version == "1.2.0" &&
                  w.packages[1].id == "bravo" && w.packages[1].kind == "eml" && w.packages[2].id == "charlie" && w.packages[2].version.empty(),
              "its packages in run order, with kind and version");
        CHECK(w.packages.size() == 3 && w.packages[0].changed == std::vector<std::string>({table, stone}) &&
                  w.packages[0].created == std::vector<std::string>({made, own}) && w.packages[1].changed == std::vector<std::string>({made, stone}) &&
                  w.packages[2].changed.empty() && w.unattributed.changed == std::vector<std::string>({logs}),
              "what each package wrote and created, by the GUID text Kiln's id table holds");
        CHECK(parse_archive_journal(written, std::string(64, 'c')).state == "stale", "the same record names nobody in another generation");
        // named on the entries of the base whose resources they are
        BaseModel wb;
        auto entry = [](const std::string& id) {
            BaseModel::Entry e;
            e.id = id;
            e.fields["maxStack"] = Value::integer(10);
            return e;
        };
        wb.registries["items"] = {entry("game:stone"), entry("game:logs"), entry("alpha:made"), entry("game:own")};
        wb.registries["balance"] = {entry("game:balance")};
        wb.registries["recipes"] = {entry("game:craft_stone")};
        const std::map<std::string, std::string> wids = {{"items|game:stone", stone},   {"items|game:logs", logs},        {"items|alpha:made", made},
                                                         {"items|game:own", own},       {"balance|game:balance", table}, {"recipes|game:craft_stone", stone}};
        CHECK(name_archive_writers(w, wids, {"items", "balance"}, &wb) == 5, "five entries named");
        CHECK(wb.registries["items"][0].archive == std::vector<std::string>({"shroudforge:alpha", "eml:bravo"}) &&
                  wb.registries["items"][1].archive == std::vector<std::string>({"kiln-eml:unattributed"}) &&
                  wb.registries["items"][2].archive == std::vector<std::string>({"shroudforge:alpha", "eml:bravo"}) &&
                  wb.registries["items"][3].archive == std::vector<std::string>({"shroudforge:alpha"}) &&
                  wb.registries["balance"][0].archive == std::vector<std::string>({"shroudforge:alpha"}) && wb.registries["recipes"][0].archive.empty(),
              "each entry names the packages that wrote or created its resource, in run order; never a recipe");
    }
    // ---- the file (review: only from the game folder itself, bounded, never through a link) ----
    {
        namespace fs = std::filesystem;
        const fs::path tmp = fs::temp_directory_path() / ("kiln-writers-test-" + std::to_string(::getpid()));
        fs::remove_all(tmp);
        const fs::path kiln = tmp / "game" / "kiln", own = kiln / "eml" / "ownership";
        fs::create_directories(own);
        const std::string name = "enshrouded_server.change-journal.json";
        std::string why;
        CHECK(!read_archive_journal(kiln, "enshrouded_server", {}, &why) && why.find("wrote no change journal") != std::string::npos, "no file: none, with why: " + why);
        std::ofstream(own / name, std::ios::binary) << text;
        const auto got = read_archive_journal(kiln, "enshrouded_server", {}, &why);
        CHECK(got && *got == text, "the journal beside the receipt is read whole");
        // a junction or another reparse point (Windows), as the caller's link test says
        why.clear();
        CHECK(!read_archive_journal(kiln, "enshrouded_server", [&](const fs::path& p) { return p == own; }, &why) && why.find("is a link") != std::string::npos,
              "a reparse point on the way is not followed: " + why);
        // a symbolic link out of the game folder: the ownership folder, or the file itself
        const fs::path outside = tmp / "elsewhere";
        fs::create_directories(outside);
        std::ofstream(outside / name, std::ios::binary) << text;
        const fs::path kiln2 = tmp / "game2" / "kiln";
        fs::create_directories(kiln2 / "eml");
        fs::create_directory_symlink(outside, kiln2 / "eml" / "ownership");
        why.clear();
        CHECK(!read_archive_journal(kiln2, "enshrouded_server", {}, &why) && why.find("kiln/eml/ownership is a link") != std::string::npos,
              "a linked ownership folder is not followed: " + why);
        const fs::path kiln3 = tmp / "game3" / "kiln";
        fs::create_directories(kiln3 / "eml" / "ownership");
        fs::create_symlink(outside / name, kiln3 / "eml" / "ownership" / name);
        why.clear();
        CHECK(!read_archive_journal(kiln3, "enshrouded_server", {}, &why) && why.find("the journal is a link") != std::string::npos, "a linked file is not followed: " + why);
        // larger than kiln-eml's own bound: not read (nothing is parsed)
        fs::resize_file(own / name, kArchiveJournalMax + 1);
        why.clear();
        CHECK(!read_archive_journal(kiln, "enshrouded_server", {}, &why) && why.find("larger than 16 MiB") != std::string::npos, "an oversized file is not read: " + why);
        fs::remove(own / name);
        fs::create_directory(own / name);
        why.clear();
        CHECK(!read_archive_journal(kiln, "enshrouded_server", {}, &why) && why.find("no regular file") != std::string::npos, "a folder in its place is not read: " + why);
        fs::remove_all(tmp);
    }

    // ---- naming entries ----
    BaseModel bm = base();
    const std::set<std::string> whole = {"items"};  // the registries whose entries are each a whole resource
    const size_t named = name_archive_writers(j, kIds, whole, &bm);
    CHECK(named == 2, "two entries are named (the written one and the created one): " + std::to_string(named));
    const auto& stone = bm.registries["items"][0].archive;
    CHECK(stone == std::vector<std::string>({"shroudforge:alpha", "eml:bravo", "shroudforge:delta"}),
          "every package that wrote the stone, in run order");
    CHECK(bm.registries["items"][1].archive.empty(), "an entry no package wrote is not named");
    CHECK(bm.registries["items"][2].archive == std::vector<std::string>({"shroudforge:alpha", "shroudforge:delta"}),
          "the package that created an entry is named on it, and a later one that wrote it");
    BaseModel none = base();
    CHECK(name_archive_writers(stale, kIds, whole, &none) == 0 && none.registries["items"][0].archive.empty(), "a stale journal names nothing");
    // review: an element of a shared resource (a recipe) is never named, even when its GUID member is a resource the journal lists
    BaseModel shared = base();
    BaseModel::Entry recipe;
    recipe.id = "game:craft_stone";
    recipe.fields["duration"] = Value::number(2);
    shared.registries["recipes"] = {recipe};
    std::map<std::string, std::string> ids = kIds;
    ids["recipes|game:craft_stone"] = kStoneGuid;
    CHECK(name_archive_writers(j, ids, whole, &shared) == 2 && shared.registries["recipes"][0].archive.empty() && !shared.registries["items"][0].archive.empty(),
          "a registry whose entries share a resource is not named from the journal's resource keys");

    // ---- the chain and the conflict line ----
    const Result r = compose(s, bm, {},
                             {mod("bigstacks", {batch(s, "bigstacks", "alter", R"([{"registry": "items", "select": {"id": "game:material_t1_*"}, "field": "maxStack", "op": {"mul": 4}},
                                                                                     {"registry": "items", "select": {"id": "game:alpha_item"}, "field": "maxStack", "op": 50}])")})});
    const FieldOutcome& fo = r.fields.at({"items", kStone, "maxStack"});
    CHECK(fo.chain.size() == 4 && fo.chain[0].kind == WriterKind::ArchiveWriter && fo.chain[0].who == "shroudforge:alpha" &&
              fo.chain[2].who == "shroudforge:delta" && fo.chain[3].kind == WriterKind::Registry,
          "the packages lie below the registry edit, in run order");
    CHECK(fo.chain.size() == 4 && fo.chain[0].after.i == 25 && fo.chain[0].op.set && fo.chain[0].mod.empty(),
          "an archive row's value is the base (archive bytes are the base, D1)");
    CHECK(fo.result.i == 100 && fo.winner == "bigstacks", "a relative registry op composes on the archive's value and wins");
    CHECK(r.fields.at({"items", kLogs, "maxStack"}).chain.size() == 1, "an entry no package wrote has no archive row");
    const Line* line = nullptr;
    for (const Line& l : r.lines)
        if (l.field.id == kStone) line = &l;
    CHECK(line && line->kind == LineKind::ComposedOn && line->certainty == Certainty::Named && line->loser == "shroudforge:delta" && line->winner == "bigstacks",
          "one composedOn line, named, against the last package");
    CHECK(line && line->text.find("delta (ShroudForge package)") != std::string::npos && line->text.find("before Kiln") != std::string::npos &&
              line->text.find("bigstacks's edit wins (D1)") != std::string::npos,
          "the line says the package wrote it before Kiln and the registry edit wins: " + (line ? line->text : std::string()));
    const Line* created = nullptr;
    for (const Line& l : r.lines)
        if (l.field.id == kNew) created = &l;
    CHECK(created && created->kind == LineKind::Overridden && created->certainty == Certainty::Named && created->loser == "shroudforge:delta",
          "an absolute edit over a created entry: overridden, named");
    // exclusive and yield are not about the archive (its value is the base)
    const Result y = compose(s, bm, {}, {mod("y", {batch(s, "y", "alter", R"([{"registry": "items", "select": {"id": "game:material_t1_stone"}, "field": "maxStack", "op": 9, "yield": true}])")})});
    CHECK(y.fields.at({"items", kStone, "maxStack"}).result.i == 9, "a yield edit still applies over the archive's packages (they are the base)");
    const Result ex = compose(s, bm, {}, {mod("x", {batch(s, "x", "alter", R"([{"registry": "items", "select": {"id": "game:material_t1_stone"}, "field": "maxStack", "op": 9, "exclusive": true}])")})});
    CHECK(ex.refused.empty() && ex.fields.at({"items", kStone, "maxStack"}).result.i == 9, "an exclusive edit is never refused because of the archive's packages");
    // an old Kiln path on a named entry: the packages first, the old path above them
    OldWrite pack;
    pack.kind = WriterKind::OldPack, pack.mod = "oldstacks", pack.who = "oldstacks: content/a.json", pack.field = {"items", kStone, "maxStack"};
    pack.op.set = true, pack.op.value = Value::integer(30);
    const Result o = compose(s, bm, {pack}, {});
    const auto& oc = o.fields.at({"items", kStone, "maxStack"}).chain;
    CHECK(oc.size() == 4 && oc[2].kind == WriterKind::ArchiveWriter && oc[3].kind == WriterKind::OldPack && oc[3].after.i == 30,
          "an old pack lies above the archive's packages");
    // review: a package is never a field's winner (per entry, not per field): an edit the reference check takes out leaves
    // the field to the base, not to the last package, and a field only packages are left on is not reported as touched
    const Result dropped = compose(s, bm, {}, {mod("ghost", {batch(s, "ghost", "alter", R"([{"registry": "items", "select": {"id": "game:material_t1_stone"}, "field": "upgradesTo", "op": "zz:ghost"}])")})});
    const auto up = dropped.fields.find({"items", kStone, "upgradesTo"});
    CHECK(up == dropped.fields.end() || (up->second.winner.empty() && up->second.winner.rfind("shroudforge:", 0) != 0),
          "a dropped edit's field is not won by a package: " + (up == dropped.fields.end() ? std::string("gone") : up->second.winner));
    CHECK(up == dropped.fields.end(), "a field only archive rows are left on is not a touched field");
    bool refused_ghost = false;
    for (const Refusal& x : dropped.refused) refused_ghost = refused_ghost || x.mod == "ghost";
    CHECK(refused_ghost, "the edit naming no entry is refused");
    // a later entry point keeps the named rows of the start and names new places too
    const Result later = compose_after(s, bm, r, {mod("late", {batch(s, "late", "alter", R"([{"registry": "items", "select": {"id": "game:alpha_item"}, "field": "perks", "list": {"append": ["game:perk_x"]}}])")})});
    const auto& lp = later.fields.at({"items", kNew, "perks"}).chain;
    CHECK(lp.size() == 3 && lp[0].kind == WriterKind::ArchiveWriter && lp[2].kind == WriterKind::Registry, "a later pass names the package on a field it touches first");

    // ---- registry.json ----
    ReportMeta meta;
    meta.written = "2026-11-25T20:00:00Z", meta.generation = 1, meta.base.writer = "kiln-eml";
    meta.regmap = {"server", std::string(64, 'f'), "", false};  // a document the schema check accepts
    meta.base.journal = {j.state, j.text, j.packages.size(), named, j.truncated, "kiln-eml"};
    meta.base.archive = {{"source", "kiln-eml"}, {"phase", "done"}, {"patched", 3}, {"cached", false}, {"restored", false}, {"mods", 2},
                         {"receipt", "kiln/eml/ownership/enshrouded_server.json"}, {"text", "kiln-eml's EML pass patched 3 resources of the archive this start"}};
    const json doc = report_json(s, r, meta);
    CHECK(doc["base"]["journal"]["state"] == "read" && doc["base"]["journal"]["packages"] == 4 && doc["base"]["journal"]["entries"] == 2 &&
              doc["base"]["journal"]["truncated"] == false,
          "base.journal: read, its packages and the entries it named: " + doc["base"].dump());
    const json* row = nullptr;
    for (const json& f : doc["fields"])
        if (f["id"] == kStone) row = &f;
    CHECK(row && (*row)["writers"][0]["kind"] == "archiveWriter" && (*row)["writers"][0]["who"] == "shroudforge:alpha" &&
              (*row)["writers"][0]["certainty"] == "named" && (*row)["writers"][0]["value"] == 25 && !(*row)["writers"][0].contains("mod"),
          "the field row names the package, certainty named");
    // review: the journal names the entry, not the field: an archive row claims no operation on the field (no op, no list)
    CHECK(row && !(*row)["writers"][0].contains("op") && !(*row)["writers"][0].contains("list"),
          "an archive row has no op: what the package did to this field is not known: " + (row ? (*row)["writers"].dump() : std::string()));
    bool named_line = false;
    for (const json& c : doc["conflicts"]) named_line = named_line || (c["loser"] == "shroudforge:delta" && c["certainty"] == "named");
    CHECK(named_line, "the conflict line is named");
    ReportMeta absent;
    absent.written = meta.written, absent.base.writer = "shroudforge-hosted";
    absent.base.journal.text = "hosted ShroudForge keeps no change journal";
    const json doc2 = report_json(s, compose(s, base(), {}, {}), absent);
    CHECK(doc2["base"]["journal"]["state"] == "none" && doc2["base"]["journal"]["text"] == "hosted ShroudForge keeps no change journal",
          "without a journal base.journal says so: " + doc2["base"].dump());
    if (argc > 2) std::ofstream(argv[2]) << doc.dump(2);

    // ---- the EML dialect (format 2): kiln-eml's own record of a fixed EML pass (gpl/kiln-eml/kiln-eml/tests/
    // journal_contract.rs `the_eml_pass_makes_the_record_kilns_reader_is_tested_on` fails unless this fixture is byte for
    // byte that record): named per field on items and balance, per element on recipes and loot ----
    {
        std::ifstream ef(root + "/schema/sfi/eml-journal/fixtures/kiln-eml-writer-eml.json");
        std::stringstream es;
        es << ef.rdbuf();
        const std::string written = es.str();
        std::string live;
        for (int i = 0; i < 32; ++i) live += "ef";
        const std::string stone = "1d622bd9-8170-41b1-822e-d3984b386d95", logs = "aaaaaaaa-0000-4000-8000-000000000001",
                          made = "5d2f8a90-1c3b-4e7f-9a12-6b4c8d0e2f13", table = "82706b40-61b1-4b8f-8b23-dcec6971bda1",
                          recipes = "3c0a5e77-2f41-4b6a-9e10-5d8c7b6a5f40", loot = "6e9f0a12-7c3d-4b85-a1f6-0d2e4c6b8a91";
        const ArchiveJournal e = parse_archive_journal(written, live);
        CHECK(!written.empty() && e.state == "read" && e.format == 2 && e.dialect == "eml" && !e.truncated, "the EML pass's record reads: " + e.state + " " + e.text);
        CHECK(e.packages.size() == 3 && e.packages[0].id == "alpha" && e.packages[0].kind == "eml" && e.packages[1].id == "bravo" &&
                  e.packages[2].id == "charlie" && e.packages[2].version.empty(),
              "its mods in run order (the one whose id cannot be named is not a package)");
        CHECK(e.packages.size() == 3 && e.packages[0].paths.at(stone) == std::vector<std::string>({"maxStackSize"}) &&
                  e.packages[1].paths.at(recipes) == std::vector<std::string>({"recipes[2]", "recipes[]"}) &&
                  e.unattributed.paths.at(logs) == std::vector<std::string>({""}),
              "each written resource with its member paths");
        CHECK(parse_archive_journal(written, std::string(64, 'c')).state == "stale", "another generation: stale, names nobody");
        CHECK(e.text.find("EML mods") != std::string::npos, "base.journal says EML mods: " + e.text);
        // the bad ones: format 2 is the EML dialect's and its paths are the schema's
        const json good = json::parse(written);
        for (const auto& [what, edit] : std::vector<std::pair<std::string, std::function<void(json&)>>>{
                 {"format 3", [](json& d) { d["format"] = 3; }},
                 {"format 2 of a dialect that is neither eml nor shroudforge", [](json& d) { d["dialect"] = "emf"; }},
                 {"a ShroudForge package in an EML pass", [](json& d) { d["packages"][0]["kind"] = "shroudforge"; }},
                 {"a hosted writer's record in kiln-eml's place", [](json& d) { d["writer"] = "shroudforge"; }},
                 {"a writer that is neither", [](json& d) { d["writer"] = "emf"; }},
                 {"a changed row without paths", [](json& d) { d["packages"][0]["changed"][0].erase("paths"); }},
                 {"no paths at all", [](json& d) { d["packages"][0]["changed"][0]["paths"] = json::array(); }},
                 {"a path with a space", [](json& d) { d["packages"][0]["changed"][0]["paths"] = {"max Stack"}; }},
                 {"a path with a line break", [](json& d) { d["packages"][0]["changed"][0]["paths"] = {"a\nregistry: forged"}; }},
                 {"a negative element", [](json& d) { d["packages"][0]["changed"][0]["paths"] = {"recipes[-1]"}; }},
                 {"a path of 257 bytes", [](json& d) { d["packages"][0]["changed"][0]["paths"] = {std::string(257, 'a')}; }}}) {
            json bad = good;
            edit(bad);
            const ArchiveJournal x = parse_archive_journal(bad.dump(), live);
            CHECK(x.state == "unreadable" && x.packages.empty(), what + ": unreadable, nothing named: " + x.state + " " + x.text);
        }
        // the base: items (whole resources), the balance table, three recipes and a loot entry (elements of shared resources)
        BaseModel eb;
        auto entry = [](const std::string& id, std::initializer_list<std::pair<const char*, Value>> f) {
            BaseModel::Entry x;
            x.id = id;
            for (const auto& [k, v] : f) x.fields[k] = v;
            return x;
        };
        eb.registries["items"] = {entry("game:stone", {{"maxStack", Value::integer(10)}, {"power", Value::number(31)}, {"perks", Value::of_list({})}}),
                                  entry("game:logs", {{"maxStack", Value::integer(10)}}), entry("bravo:made", {{"maxStack", Value::integer(1)}})};
        eb.registries["balance"] = {entry("game:balance", {{"levelCap", Value::integer(25)}, {"skillPointsPerLevel", Value::integer(1)}})};
        eb.registries["recipes"] = {entry("game:craft_a", {{"duration", Value::number(1)}}), entry("game:craft_b", {{"duration", Value::number(2)}}),
                                    entry("game:craft_c", {{"duration", Value::number(3)}})};
        eb.registries["loot"] = {entry("game:stone", {{"stack.max", Value::integer(5)}, {"stack.min", Value::integer(1)}})};
        const std::map<std::string, std::string> eids = {{"items|game:stone", stone}, {"items|game:logs", logs}, {"items|bravo:made", made},
                                                         {"balance|game:balance", table}, {"recipes|game:craft_b", "99999999-0000-4000-8000-000000000000"}};
        ArchiveFields af;
        af.rows["items"] = {{"maxStack", {"maxStackSize"}}, {"power", {"damageSetup.dmgMod", "damageSetup.speedMod", "itemLevelRange.minLevel"}},
                            {"perks", {"perkReferences", "perkIds"}}};
        af.rows["balance"] = {{"levelCap", {"playerLevelCap"}}, {"skillPointsPerLevel", {"skillPointsPerLevel"}}};
        af.rows["recipes"] = {{"duration", {"craftingDuration"}}, {"inputs", {"input"}}};
        af.rows["loot"] = {{"stack.max", {"stackSizeMax"}}, {"stack.min", {"stackSizeMin"}}};
        af.array = {{"recipes", "recipes"}, {"loot", "items"}};
        const std::map<int32_t, std::string> recipe_elements = {{0, "game:craft_a"}, {1, "game:craft_b"}, {2, "game:craft_c"}}, loot_elements = {{0, "game:stone"}};
        af.elements = [&](const std::string& reg, const std::string& guid) -> const std::map<int32_t, std::string>* {
            if (reg == "recipes" && guid == recipes) return &recipe_elements;
            if (reg == "loot" && guid == loot) return &loot_elements;
            return nullptr;
        };
        CHECK(name_archive_writers(e, eids, {"items", "balance"}, &eb, &af) == 7, "seven entries named (stone, logs, made, balance, two recipes, a loot entry)");
        const auto& st = eb.registries["items"][0];
        CHECK(st.archive == std::vector<std::string>({"eml:alpha", "eml:bravo"}) &&
                  st.archive_fields.at("eml:alpha") == std::set<std::string>({"maxStack"}) && st.archive_fields.at("eml:bravo") == std::set<std::string>({"power"}),
              "the stone: alpha on its stack size, bravo on its power (a formula input)");
        CHECK(eb.registries["items"][1].archive == std::vector<std::string>({"kiln-eml:unattributed"}) && eb.registries["items"][1].archive_fields.empty(),
              "the logs replaced whole by a mod whose id cannot be named: unattributed, the whole entry");
        CHECK(eb.registries["items"][2].archive == std::vector<std::string>({"eml:bravo"}) && eb.registries["items"][2].archive_fields.empty(), "a creation: its creator, whole");
        CHECK(eb.registries["balance"][0].archive_fields.at("eml:alpha") == std::set<std::string>({"levelCap"}), "the balance table: the level cap only");
        CHECK(eb.registries["recipes"][0].archive.empty(), "a recipe nobody wrote (only the list's members changed) is not named");
        CHECK(eb.registries["recipes"][1].archive == std::vector<std::string>({"eml:alpha"}) && eb.registries["recipes"][1].archive_fields.at("eml:alpha") == std::set<std::string>({"duration"}),
              "recipe 1: alpha on its duration, by its element (never by the element's own GUID member)");
        CHECK(eb.registries["recipes"][2].archive == std::vector<std::string>({"eml:bravo"}) && eb.registries["recipes"][2].archive_fields.empty(),
              "recipe 2 put in place whole by bravo: the whole entry");
        CHECK(eb.registries["loot"][0].archive_fields.at("eml:bravo") == std::set<std::string>({"stack.max"}), "a loot entry by its element: its stack size");
        CHECK(archive_fields_of(af.rows["items"], {"damageSetup"}) == std::set<std::string>({"power"}) &&
                  archive_fields_of(af.rows["items"], {"perkIds[]"}) == std::set<std::string>({"perks"}) &&
                  archive_fields_of(af.rows["items"], {"maxStackSizeX"}).empty() && archive_fields_of(af.rows["items"], {""}).size() == 3,
              "a path names the fields it holds or lies in, at member boundaries only; \"\" names every field");
        // dev.32 creatures: a template's fields live in its component list, which a journal names by place, not by the
        // component type the rows start at: any write in the list names every field (never a miss), its name none
        std::map<std::string, std::vector<std::string>> creature_rows;
        for (const auto& [f, paths] : std::map<std::string, std::vector<std::string>>{{"level", {"Level.dataStorage[0]"}}, {"scale", {"ScaleComponent.uniformScale"}}})
            creature_rows[f] = archive_paths_of("keen::ecs::TemplateResource", paths);
        CHECK(archive_fields_of(creature_rows, {"components[12].value.dataStorage[0]"}) == std::set<std::string>({"level", "scale"}) &&
                  archive_fields_of(creature_rows, {"components"}).size() == 2 && archive_fields_of(creature_rows, {"name"}).empty() &&
                  archive_paths_of("keen::ItemInfo", {"maxStackSize"}) == std::vector<std::string>({"maxStackSize"}),
              "a write in a template's component list names every creature field; other resources keep their rows' paths");
        // review registry-r2a: the journal cannot tell which of a template's components a write was in, so such a write names
        // the creature whole ("wrote this entry", registry.json "named": "entry"), never field by field ("wrote this field"
        // would claim the package wrote the creature's level when it only changed its model); a write outside the component
        // list (its name) names nothing
        {
            const std::string vukah = "d5d05217-52dc-467a-9192-26b4f661d8d9", wolf = "478381c5-585f-4264-b373-dbb1ea596eca";
            ArchiveJournal cj;
            cj.state = "read", cj.format = 2, cj.dialect = "eml";
            ArchiveJournal::Package looks;
            looks.id = "looks", looks.kind = "eml", looks.changed = {vukah, wolf};
            looks.paths[vukah] = {"components[3].value.model"};
            looks.paths[wolf] = {"name"};
            cj.packages = {looks};
            BaseModel cbm;
            cbm.registries["creatures"] = {entry("game:enemy_vukah_melee", {{"level", Value::integer(7)}, {"scale", Value::number(1)}}),
                                           entry("game:enemy_wildbeast_wolf_earlygame", {{"level", Value::integer(3)}})};
            ArchiveFields caf;
            caf.rows["creatures"] = creature_rows;
            CHECK(name_archive_writers(cj, {{"creatures|game:enemy_vukah_melee", vukah}, {"creatures|game:enemy_wildbeast_wolf_earlygame", wolf}},
                                       {"creatures"}, &cbm, &caf) == 1,
                  "one creature named: the one whose component list the package wrote");
            const BaseModel::Entry& v = cbm.registries["creatures"][0];
            CHECK(v.archive == std::vector<std::string>({"eml:looks"}) && v.archive_fields.empty(),
                  "the creature is named whole, not on every field: " + std::to_string(v.archive_fields.size()) + " per-field names");
            CHECK(cbm.registries["creatures"][1].archive.empty(), "a write of the template's name names no creature");
        }
        // dev.32 abilities: a regmap member with a [<Type>#<k>] selector (which the journal's paths never carry: they give an
        // element's index) is named by a written path at any index there, so a package that wrote an event's time is never
        // missed (it can over-name, as the journal may)
        const std::map<std::string, std::vector<std::string>> ab = {
            {"timing.release", {"subSequences[0].events[keen::actor::SpawnEntityEvent#0].time"}},
            {"timing.cost", {"subSequences[0].events[keen::actor::PayUsageCost#0].time"}}};
        CHECK(archive_fields_of(ab, {"subSequences[0].events[11].time"}) == std::set<std::string>({"timing.release", "timing.cost"}) &&
                  archive_fields_of(ab, {"subSequences[0].events"}).size() == 2 && archive_fields_of(ab, {"subSequences[0].events[]"}).size() == 2 &&
                  archive_fields_of(ab, {"subSequences[0].length"}).empty() && archive_fields_of(ab, {"subSequences[1].events[3].time"}).empty() &&
                  archive_fields_of(ab, {"subSequences[0].events[11].duration"}).empty() && archive_fields_of(ab, {"subSequences"}).size() == 2,
              "a variant selector names any element at its place, never another member or part");
        // the same journal without the regmap's fields: per entry as before, never an element
        BaseModel plain = eb;
        for (auto& [reg, es2] : plain.registries)
            for (auto& x : es2) x.archive.clear(), x.archive_fields.clear();
        CHECK(name_archive_writers(e, eids, {"items", "balance"}, &plain) == 4 && plain.registries["items"][0].archive_fields.empty() &&
                  plain.registries["recipes"][1].archive.empty(),
              "without the fields: per entry on whole resources, no element");
        // composition: a row only on the fields a mod is named on, and the line says the field
        Schema es3;
        RegistrySpec items;
        items.name = "items", items.version = "1.0.0";
        FieldSpec ms;
        ms.name = "maxStack", ms.type = FieldType::Int, ms.min = 1, ms.max = 65535, ms.applies = Applies::WorldLoad;
        FieldSpec pw;
        pw.name = "power", pw.type = FieldType::Float, pw.min = 0, pw.max = 100000, pw.applies = Applies::Now;
        items.fields[ms.name] = ms;
        items.fields[pw.name] = pw;
        RegistrySpec recipes_spec;
        recipes_spec.name = "recipes", recipes_spec.version = "1.0.0";
        FieldSpec du;
        du.name = "duration", du.type = FieldType::Float, du.min = 0, du.max = 100000, du.applies = Applies::Now;
        recipes_spec.fields[du.name] = du;
        es3.registries["items"] = items;
        es3.registries["recipes"] = recipes_spec;
        BaseModel cb;
        cb.registries["items"] = eb.registries["items"];
        cb.registries["recipes"] = eb.registries["recipes"];
        const Result cr = compose(es3, cb, {},
                                  {mod("big", {batch(es3, "big", "alter", R"([{"registry": "items", "select": {"id": "game:stone"}, "field": "maxStack", "op": 100},
                                                                               {"registry": "items", "select": {"id": "game:stone"}, "field": "power", "op": {"mul": 2}},
                                                                               {"registry": "items", "select": {"id": "game:logs"}, "field": "maxStack", "op": 50},
                                                                               {"registry": "recipes", "select": {"id": "game:craft_*"}, "field": "duration", "op": 9}])")})});
        const auto& msc = cr.fields.at({"items", "game:stone", "maxStack"}).chain;
        CHECK(msc.size() == 2 && msc[0].kind == WriterKind::ArchiveWriter && msc[0].who == "eml:alpha" && msc[0].archive_field && msc[1].kind == WriterKind::Registry,
              "the stone's stack size: alpha's row (by field), not bravo's");
        const auto& pwc = cr.fields.at({"items", "game:stone", "power"}).chain;
        CHECK(pwc.size() == 2 && pwc[0].who == "eml:bravo" && pwc[0].archive_field, "its power: bravo's row");
        const auto& lgc = cr.fields.at({"items", "game:logs", "maxStack"}).chain;
        CHECK(lgc.size() == 2 && lgc[0].who == "kiln-eml:unattributed" && !lgc[0].archive_field, "the logs: the whole entry's row");
        CHECK(cr.fields.at({"recipes", "game:craft_a", "duration"}).chain.size() == 1 && cr.fields.at({"recipes", "game:craft_b", "duration"}).chain.size() == 2 &&
                  cr.fields.at({"recipes", "game:craft_c", "duration"}).chain[0].who == "eml:bravo",
              "recipes: rows on the elements their mods wrote only");
        const Line* fl = nullptr;
        const Line* wl = nullptr;
        for (const Line& l : cr.lines) {
            if (l.field.id == "game:stone" && l.field.field == "maxStack") fl = &l;
            if (l.field.id == "game:logs") wl = &l;
        }
        CHECK(fl && fl->certainty == Certainty::Named && fl->text.find("alpha (EML package) wrote this field in the game's archive before Kiln; big's edit wins (D1)") != std::string::npos,
              "the line says the mod wrote this field: " + (fl ? fl->text : std::string()));
        CHECK(wl && wl->text.find("wrote this entry") != std::string::npos, "a whole-entry name still says the entry: " + (wl ? wl->text : std::string()));
        ReportMeta em;
        em.written = "2026-11-26T05:00:00Z", em.generation = 1, em.base.writer = "kiln-eml";
        em.regmap = {"server", std::string(64, 'f'), "", false};
        const json ed = report_json(es3, cr, em);
        std::string named_field, named_entry;
        for (const json& f : ed["fields"]) {
            if (f["id"] == "game:stone" && f["field"] == "maxStack") named_field = f["writers"][0].value("named", "");
            if (f["id"] == "game:logs") named_entry = f["writers"][0].value("named", "");
        }
        CHECK(named_field == "field" && named_entry == "entry", "registry.json says what the journal names the package on: " + named_field + "/" + named_entry);
        if (argc > 3) std::ofstream(argv[3]) << ed.dump(2);
        // ---- review: a list replaced whole, a path per element past 64, a GUID listed twice, the unreadable text ----
        {
            auto fresh = [&] {
                BaseModel b = eb;
                for (auto& [reg, es2] : b.registries)
                    for (auto& x : es2) x.archive.clear(), x.archive_fields.clear();
                return b;
            };
            auto bravo_recipes = [&](const json& paths) {
                json d = good;
                for (json& c : d["packages"][1]["changed"])
                    if (c["guid"] == recipes) c["paths"] = paths;
                return d;
            };
            // the recipe list replaced whole ("recipes": a field of the resource's root holding every element)
            BaseModel b1 = fresh();
            const ArchiveJournal j1 = parse_archive_journal(bravo_recipes({"recipes"}).dump(), live);
            name_archive_writers(j1, eids, {"items", "balance"}, &b1, &af);
            CHECK(j1.state == "read" && b1.registries["recipes"][0].archive == std::vector<std::string>({"eml:bravo"}) &&
                      b1.registries["recipes"][0].archive_fields.empty() && b1.registries["recipes"][2].archive == std::vector<std::string>({"eml:bravo"}),
                  "the recipe list replaced whole names its mod on every recipe, whole: " + j1.state + " " + j1.text);
            // a recipe mod that wrote one field of a hundred recipes: a path each, named on that field of each
            json many = json::array();
            for (int i = 0; i < 100; ++i) many.push_back("recipes[" + std::to_string(i) + "].craftingDuration");
            BaseModel b2 = fresh();
            const ArchiveJournal j2 = parse_archive_journal(bravo_recipes(many).dump(), live);
            name_archive_writers(j2, eids, {"items", "balance"}, &b2, &af);
            CHECK(j2.state == "read" && b2.registries["recipes"][0].archive_fields.count("eml:bravo") &&
                      b2.registries["recipes"][0].archive_fields.at("eml:bravo") == std::set<std::string>({"duration"}) &&
                      b2.registries["recipes"][2].archive_fields.at("eml:bravo") == std::set<std::string>({"duration"}),
                  "a hundred recipe paths read, each recipe named on its duration only: " + j2.state + " " + j2.text);
            json over = json::array();
            for (int i = 0; i <= 16384; ++i) over.push_back("recipes[" + std::to_string(i) + "]");
            CHECK(parse_archive_journal(bravo_recipes(over).dump(), live).state == "unreadable", "more paths than the format writes: unreadable");
            // one GUID listed twice (another part): its paths together, so an element is still named by them
            json twice = bravo_recipes({"recipes[2]"});
            json row = {{"guid", recipes}, {"typeHash", "00000001"}, {"part", 1}, {"createdBy", nullptr}, {"paths", {"recipes[0].craftingDuration"}}};
            twice["packages"][1]["changed"].push_back(row);
            BaseModel b3 = fresh();
            const ArchiveJournal j3 = parse_archive_journal(twice.dump(), live);
            name_archive_writers(j3, eids, {"items", "balance"}, &b3, &af);
            CHECK(j3.state == "read" && b3.registries["recipes"][0].archive_fields.count("eml:bravo") &&
                      b3.registries["recipes"][0].archive_fields.at("eml:bravo") == std::set<std::string>({"duration"}) &&
                      b3.registries["recipes"][2].archive == std::vector<std::string>({"eml:bravo"}) && b3.registries["recipes"][2].archive_fields.empty(),
                  "a GUID listed twice: the elements its paths name are named: " + j3.state + " " + j3.text);
            // a format-2 journal that is malformed is not called a format-1 one
            json bad = good;
            bad["packages"][0]["changed"][0]["paths"] = json::array();
            const ArchiveJournal j4 = parse_archive_journal(bad.dump(), live);
            CHECK(j4.state == "unreadable" && j4.text.find("format-1") == std::string::npos && j4.text.find("format 1 or 2") != std::string::npos,
                  "the unreadable text says what Kiln reads: " + j4.text);
        }
    }
    // ---- the live generation of an EML start: kiln-eml's state of this start (eml.json "generation") ----
    {
        std::string hex, why;
        const std::string g(64, 'a');
        CHECK(eml_state_generation(R"({"format":1,"phase":"done","cached":true,"generation":")" + g + R"("})", &hex, &why) && hex == g,
              "done with a generation: that one");
        CHECK(!eml_state_generation(R"({"phase":"aside","aside":"ShroudForge"})", &hex, &why) && why.find("stood aside") != std::string::npos, "aside: " + why);
        CHECK(!eml_state_generation(R"({"phase":"idle"})", &hex, &why) && why.find("no EML mod") != std::string::npos, "idle: " + why);
        CHECK(!eml_state_generation(R"({"phase":"failed","error":"x"})", &hex, &why) && why.find("did not end well") != std::string::npos, "failed: " + why);
        CHECK(!eml_state_generation(R"({"phase":"done","patched":0})", &hex, &why) && why.find("no live generation") != std::string::npos,
              "done, no generation (an older kiln-eml): " + why);
        CHECK(!eml_state_generation(R"({"phase":"done","generation":"xyz"})", &hex, &why), "a generation that is no SHA-256");
        CHECK(!eml_state_generation("{", &hex, &why), "not JSON");
    }

    // ---- the drift probe's suspects (drift.h) ----
    const json map = json::parse(R"({"foreign": [{"symbol": "a", "rva": "0x10", "module": "xhl-core.dll", "offset": "0x1", "own": false},
                                                 {"symbol": "b", "rva": "0x20", "module": "kiln-content.dll", "offset": "0x2", "own": true},
                                                 {"symbol": "c", "rva": "0x30", "module": "xhl-core.dll", "offset": "0x3", "own": false},
                                                 {"symbol": "d", "rva": "0x40", "module": "", "offset": "0x4", "own": false}]})");
    CHECK(drift_suspects(map, false) == std::vector<std::string>({"xhl-core.dll", "code outside any module"}),
          "suspects: other modules that patched game code, each once, never Kiln's own");
    CHECK(drift_suspects(map, true).front() == "ShroudForge (hosted)", "hosted ShroudForge is a suspect when it runs");
    CHECK(drift_suspects(json(), false).empty() && drift_suspects(json::parse("{\"foreign\": 3}"), false).empty(), "no hook map: no suspect");
    json many = {{"foreign", json::array()}};
    for (int i = 0; i < 12; ++i) many["foreign"].push_back({{"module", "m" + std::to_string(i) + ".dll"}, {"own", false}});
    const auto capped = drift_suspects(many, false);
    CHECK(capped.size() == 9 && capped.back() == "+4 more", "at most 8 suspects, then how many more");
    CHECK(drift_who({}) == "outside Kiln (best effort; no other loader's hook seen)" &&
              drift_who({"xhl-core.dll", "emf.dll"}) == "outside Kiln (best effort; suspects: xhl-core.dll, emf.dll)",
          "a drift row's writer says best effort and names the suspects");

    // ---- who published the archive (archive_base.h; registry-followups 2) ----
    // The compat rehearsal's starts, by the state kiln-eml gave (kiln/state/eml.json): before, base.writer said "none" on all
    // of them but kiln mode, also where kiln-eml's EML pass or hosted ShroudForge had patched the archive.
    {
        const std::string stem = "enshrouded_server";
        std::set<std::string> files;
        ArchiveBaseInputs in;
        in.stem = stem;
        in.exists = [&files](const std::string& rel) { return files.count(rel) != 0; };
        auto with = [&](const char* backend, const char* state) {
            in.backend = backend;
            in.eml = state != nullptr;
            in.eml_state = state ? json::parse(state) : json();
            return archive_base(in);
        };
        // kiln-eml's own EML pass (Free Skill Unlocks, Unlimited Flame Altars: 3 patched), hosted ShroudForge with no package
        files = {"kiln/eml/ownership/enshrouded_server.json"};
        ArchiveBase b = with("hosted", R"({"format": 1, "phase": "done", "file": "enshrouded_server", "patched": 3, "cached": false, "restored": false,
                                           "aside": null, "mods": [{"id": "free-skill-unlocks", "state": "loaded"}, {"id": "xhl-unlimited-flame-altars", "state": "loaded"}]})");
        CHECK(b.writer == "kiln-eml", "kiln-eml's EML pass patched the archive: the base is kiln-eml's, not as shipped (" + b.writer + ")");
        CHECK(b.archive.value("source", "") == "kiln-eml" && b.archive.value("phase", "") == "done" && b.archive.value("patched", -1) == 3 &&
                  b.archive.value("cached", true) == false && b.archive.value("receipt", "") == "kiln/eml/ownership/enshrouded_server.json" &&
                  b.archive.value("mods", -1) == 2 && !b.archive.value("text", "").empty(),
              "base.archive: what kiln-eml did and the receipt it rests on: " + b.archive.dump());
        // nothing changed since the last run: the archive keeps kiln-eml's patches
        b = with("hosted", R"({"phase": "done", "file": "enshrouded_server", "patched": 0, "cached": true, "restored": false, "mods": [{"id": "a", "state": "loaded"}]})");
        CHECK(b.writer == "kiln-eml" && b.archive.value("cached", false) == true, "a cached run is still kiln-eml's: " + b.archive.dump());
        // no receipt (an older kiln-eml's output): named all the same, receipt null
        files.clear();
        b = with("hosted", R"({"phase": "done", "file": "enshrouded_server", "patched": 1, "cached": false, "restored": false, "mods": []})");
        CHECK(b.writer == "kiln-eml" && b.archive.contains("receipt") && b.archive["receipt"].is_null(), "no receipt: null: " + b.archive.dump());
        // hosted ShroudForge reserved the writer (SF Crafting Essentials, Unlimited Flame Altars): kiln-eml stood aside
        files = {"enshrouded_server.kfc.prepared.json"};
        const char* aside = R"({"phase": "aside", "file": null, "patched": 0, "cached": false, "restored": false,
                                "aside": "ShroudForge owns startup asset patching and runs EML packages in its mods folder", "mods": []})";
        b = with("hosted", aside);
        CHECK(b.writer == "shroudforge-hosted", "hosted ShroudForge published the archive (" + b.writer + ")");
        CHECK(b.archive.value("source", "") == "kiln-eml" && b.archive.value("phase", "") == "aside" &&
                  b.archive.value("record", "") == "enshrouded_server.kfc.prepared.json" && b.archive.value("receipt", "x") == "x",
              "it rests on ShroudForge's record of its pass (named, never read): " + b.archive.dump());
        CHECK(b.archive.value("text", "").find("ShroudForge") != std::string::npos, "the sentence names ShroudForge: " + b.archive.dump());
        CHECK(with("installed", aside).writer == "shroudforge-installed", "the player's own ShroudForge");
        files.clear();
        b = with("hosted", aside);
        CHECK(b.writer == "shroudforge-hosted" && b.archive.contains("record") && b.archive["record"].is_null(), "no record yet: null: " + b.archive.dump());
        // EML's own proxy ran instead of kiln-eml: another loader published it
        b = with("hosted", R"({"phase": "aside", "file": null, "patched": 0, "cached": false, "restored": false,
                               "aside": "EML's own dbghelp.dll is loaded and ran instead", "loaders": ["dbghelp.dll"], "mods": []})");
        CHECK(b.writer == "external", "another loader's proxy wrote the archive (" + b.writer + ")");
        CHECK(with("none", R"({"phase": "aside", "aside": "someone", "mods": []})").writer == "external", "aside without a ShroudForge backend: external");
        // the DLL engines set and the repo's labs: no EML mod, the game files untouched
        b = with("hosted", R"({"phase": "idle", "file": "enshrouded_server", "patched": 0, "cached": false, "restored": false, "aside": null, "mods": []})");
        CHECK(b.writer == "none" && b.archive.value("phase", "") == "idle", "nothing patched it: as shipped: " + b.archive.dump());
        b = with("hosted", R"({"phase": "idle", "file": "enshrouded_server", "patched": 0, "cached": false, "restored": true, "mods": []})");
        CHECK(b.writer == "none" && b.archive.value("restored", false) == true, "kiln-eml put the game's own data back: " + b.archive.dump());
        b = with("hosted", R"({"phase": "idle", "file": "enshrouded_server", "staleBackup": true, "mods": []})");
        CHECK(b.writer == "none", "a backup of an earlier build: the data is the new build's own");
        b = with("hosted", R"({"phase": "idle", "file": "enshrouded_server", "restored": false, "mods": [],
                               "reason": "The game data differs from its vanilla backup, and Kiln cannot prove it is its own EML output (x), so it was left as it is."})");
        CHECK(b.writer == "external", "the data differs from the backup and is not kiln-eml's: not as shipped (" + b.writer + ")");
        b = with("hosted", R"({"phase": "idle", "file": "enshrouded_server", "restored": false, "mods": [],
                               "reason": "Extra archive containers retained: enshrouded_server_010.dat. They may belong to another loader"})");
        CHECK(b.writer == "none", "retained containers alone say nothing about the archive's bytes");
        b = with("hosted", R"({"phase": "failed", "file": "enshrouded_server", "restored": true, "error": "x", "mods": []})");
        CHECK(b.writer == "none", "a failed run that put the game's data back");
        b = with("hosted", R"({"phase": "failed", "file": "enshrouded_server", "restored": false, "error": "x", "mods": []})");
        CHECK(b.writer == "external", "a failed run that left the data as it was: not known to be the game's own");
        // kiln mode: Kiln's interpreter through kiln-eml (its journal names the packages), as before
        b = with("kiln", R"({"phase": "done", "file": "enshrouded_server", "patched": 4, "mods": []})");
        CHECK(b.writer == "kiln-eml", "kiln mode: kiln-eml");
        CHECK(with("kiln", nullptr).writer == "kiln-eml", "kiln mode without kiln-eml's state: kiln-eml all the same (as before)");
        // review: kiln mode's sentence follows kiln-eml's phase (startup_seam.rs settle_entries: done = committed or cached, idle =
        // unchanged or restored, failed = refused or failed, and run_kiln's start with no pass): it said "published the archive"
        // on every one. The writer stays kiln-eml, as before.
        CHECK(b.archive.value("text", "").find("published the archive") != std::string::npos, "a done pass published it: " + b.archive.dump());
        b = with("kiln", R"({"phase": "idle", "file": "enshrouded_server", "patched": 0, "restored": true, "mods": []})");
        CHECK(b.writer == "kiln-eml" && b.archive.value("text", "").find("published the archive") == std::string::npos &&
                  b.archive.value("text", "").find("vanilla backup") != std::string::npos,
              "kiln mode put the game's own data back: " + b.archive.dump());
        b = with("kiln", R"({"phase": "idle", "file": "enshrouded_server", "patched": 0, "restored": false, "mods": []})");
        CHECK(b.writer == "kiln-eml" && b.archive.value("text", "").find("published nothing") != std::string::npos,
              "kiln mode published nothing this start: " + b.archive.dump());
        b = with("kiln", R"({"phase": "failed", "file": "enshrouded_server", "restored": false, "error": "x", "mods": []})");
        CHECK(b.writer == "kiln-eml" && b.archive.value("text", "").find("failed") != std::string::npos &&
                  b.archive.value("text", "").find("published the archive") == std::string::npos,
              "kiln mode's pass failed: " + b.archive.dump());
        CHECK(with("kiln", nullptr).archive.value("text", "").find("published the archive") == std::string::npos,
              "kiln mode without kiln-eml's state: what was published is not known: " + with("kiln", nullptr).archive.dump());
        // no kiln-eml: Kiln cannot tell; "none", as before, and the sentence says so
        b = with("hosted", nullptr);
        CHECK(b.writer == "none" && b.archive.value("source", "") == "none" && !b.archive.value("text", "").empty(),
              "no kiln-eml: none, said: " + b.archive.dump());
        // registry.json carries it
        ReportMeta am;
        am.written = meta.written;
        am.base.writer = "kiln-eml";
        am.base.archive = with("hosted", R"({"phase": "done", "file": "enshrouded_server", "patched": 3, "cached": false, "restored": false, "mods": []})").archive;
        const json d3 = report_json(s, compose(s, base(), {}, {}), am);
        CHECK(d3["base"]["writer"] == "kiln-eml" && d3["base"].contains("archive") && d3["base"]["archive"].value("patched", -1) == 3,
              "registry.json base.archive: " + d3["base"].dump());
        ReportMeta none;
        none.written = meta.written;
        CHECK(!report_json(s, compose(s, base(), {}, {}), none)["base"].contains("archive"), "no archive object when nothing was said");
    }

    // ---- J3 (0.2.0-dev.32): format 2 of the ShroudForge dialect, kiln-eml's kiln-mode record (schema fixture
    // kiln_mode_shroudforge) and Kiln's hosted ShroudForge's own (fixture hosted): read, named per field and per element,
    // its notApplied packages; a record only from its own writer's place ----
    {
        std::ifstream vf(root + "/schema/sfi/eml-journal/fixtures/valid.json");
        std::stringstream vs;
        vs << vf.rdbuf();
        const json all = json::parse(vs.str(), nullptr, false);
        const json kiln = all.is_object() ? all["eml-journal-2"]["kiln_mode_shroudforge"] : json();
        const json hosted = all.is_object() ? all["eml-journal-2"]["hosted"] : json();
        const std::string gen = std::string(32 * 2, '0');
        std::string g7;
        for (int i = 0; i < 32; ++i) g7 += "7c";
        const ArchiveJournal k = parse_archive_journal(kiln.dump(), g7);
        CHECK(k.state == "read" && k.format == 2 && k.dialect == "shroudforge" && k.writer == "kiln-eml" && !k.truncated,
              "kiln mode's format 2 of the ShroudForge dialect reads: " + k.state + " " + k.text);
        std::string c1;
        for (int i = 0; i < 32; ++i) c1 += "1d";
        CHECK(k.packages.size() == 3 && k.packages[0].id == "betterstacks" && k.packages[0].kind == "shroudforge" && k.packages[0].version == "1.2.0" &&
                  k.packages[0].content == c1 && k.packages[1].id == "more-recipes" && k.packages[1].kind == "eml",
              "its ShroudForge and EML packages in run order, with their content digests");
        CHECK(k.packages.size() == 3 && k.packages[0].paths.at(kStoneGuid) == std::vector<std::string>({"maxStackSize"}),
              "a ShroudForge package's changed members");
        CHECK(k.not_applied.size() == 1 && k.not_applied[0].id == "broken-pack" && k.not_applied[0].kind == "shroudforge" && k.not_applied[0].version == "0.9.0" &&
                  k.not_applied[0].content == c1 && k.not_applied[0].reason.find("damageSetup") != std::string::npos,
              "the package whose Lua was not applied, with why");
        CHECK(k.text.find("3 packages") != std::string::npos && k.text.find("members changed") != std::string::npos && k.text.find("1 package's Lua was not applied") != std::string::npos,
              "base.journal's sentence: " + k.text);
        CHECK(parse_archive_journal(kiln.dump(), g7, "shroudforge").state == "unreadable", "kiln-eml's record in the hosted place: unreadable");
        // the hosted record
        const ArchiveJournal h = parse_archive_journal(hosted.dump(), g7, "shroudforge");
        CHECK(h.state == "read" && h.writer == "shroudforge" && h.dialect == "shroudforge" && h.packages.size() == 2 && h.packages[0].content.empty() &&
                  h.not_applied.empty(),
              "the hosted record reads in its own place: " + h.state + " " + h.text);
        CHECK(h.text.find("hosted ShroudForge's change journal") == 0, "and says whose it is: " + h.text);
        const ArchiveJournal hk = parse_archive_journal(hosted.dump(), g7);
        CHECK(hk.state == "unreadable" && hk.text.find("in kiln-eml's place") != std::string::npos, "a hosted record in kiln-eml's place: unreadable: " + hk.text);
        const ArchiveJournal hs = parse_archive_journal(hosted.dump(), gen, "shroudforge");
        CHECK(hs.state == "stale" && hs.text.find("hosted ShroudForge's change journal names another generation") == 0, "another <stem>.kfc: stale: " + hs.text);
        json h1 = hosted;
        h1["format"] = 1;
        CHECK(parse_archive_journal(h1.dump(), g7, "shroudforge").state == "unreadable", "a hosted record of format 1: unreadable");
        json he = hosted;
        he["dialect"] = "eml";
        for (auto& p : he["packages"]) p["kind"] = "eml";
        CHECK(parse_archive_journal(he.dump(), g7, "shroudforge").state == "unreadable", "a hosted record of the EML dialect: unreadable");
        CHECK(archive_writer_who("shroudforge", "", "shroudforge") == "shroudforge-hosted:unattributed" &&
                  archive_writer_label("shroudforge-hosted:unattributed").find("hosted ShroudForge") != std::string::npos,
              "the hosted record's unattributed writes are named apart from any package");
        // notApplied rows as the format writes them, or nobody is named
        for (const auto& [what, edit] : std::vector<std::pair<std::string, std::function<void(json&)>>>{
                 {"a reason with a line break", [](json& d) { d["notApplied"][0]["reason"] = "one\nregistry: forged"; }},
                 {"an empty reason", [](json& d) { d["notApplied"][0]["reason"] = ""; }},
                 {"a reason of 513 bytes", [](json& d) { d["notApplied"][0]["reason"] = std::string(513, 'a'); }},
                 {"no reason", [](json& d) { d["notApplied"][0].erase("reason"); }},
                 {"an id with a space", [](json& d) { d["notApplied"][0]["id"] = "two words"; }},
                 {"a package that ran", [](json& d) { d["notApplied"][0]["id"] = "betterstacks"; }},
                 {"one package twice", [](json& d) { d["notApplied"].push_back(d["notApplied"][0]); }},
                 {"a kind that is none", [](json& d) { d["notApplied"][0]["kind"] = "dll"; }},
                 {"a digest that is none", [](json& d) { d["notApplied"][0]["contentSha256"] = "x"; }},
                 {"a package's digest that is none", [](json& d) { d["packages"][0]["contentSha256"] = 7; }},
                 {"notApplied that is no list", [](json& d) { d["notApplied"] = "broken-pack"; }}}) {
            json bad = kiln;
            edit(bad);
            const ArchiveJournal x = parse_archive_journal(bad.dump(), g7);
            CHECK(x.state == "unreadable" && x.packages.empty() && x.not_applied.empty(), "notApplied: " + what + ": unreadable: " + x.state + " " + x.text);
        }
        // named per field (items) and per element (recipes): betterstacks on the stone's stack size only; more-recipes on the
        // recipe it appended (recipes[2]); slow-craft on the duration of the first recipe (recipes[0] in this base)
        json kk = kiln;
        kk["packages"][2]["changed"][0]["paths"] = {"recipes[0].craftingDuration"};
        kk["packages"][1]["changed"][0]["paths"] = {"recipes[2]", "recipes[]"};
        const ArchiveJournal kn = parse_archive_journal(kk.dump(), g7);
        BaseModel kb;
        auto entry = [](const std::string& id, std::initializer_list<std::pair<const char*, Value>> f) {
            BaseModel::Entry x;
            x.id = id;
            for (const auto& [n, v] : f) x.fields[n] = v;
            return x;
        };
        const std::string recipes = "7a1d3e55-0b2c-4d6e-8f10-23456789abcd";
        kb.registries["items"] = {entry(kStone, {{"maxStack", Value::integer(100)}, {"power", Value::number(3)}}),
                                  entry(kLogs, {{"maxStack", Value::integer(10)}})};
        kb.registries["recipes"] = {entry("game:craft_a", {{"duration", Value::number(9)}, {"inputs", Value::of_list({})}}),
                                    entry("game:craft_b", {{"duration", Value::number(2)}}), entry("more-recipes:1", {{"duration", Value::number(1)}})};
        ArchiveFields kf;
        kf.rows["items"] = {{"maxStack", {"maxStackSize"}}, {"power", {"damageSetup.dmgMod"}}};
        kf.rows["recipes"] = {{"duration", {"craftingDuration"}}, {"inputs", {"input"}}};
        kf.array = {{"recipes", "recipes"}};
        const std::map<int32_t, std::string> els = {{0, "game:craft_a"}, {1, "game:craft_b"}, {2, "more-recipes:1"}};
        kf.elements = [&](const std::string& reg, const std::string& guid) -> const std::map<int32_t, std::string>* {
            return reg == "recipes" && guid == recipes ? &els : nullptr;
        };
        CHECK(name_archive_writers(kn, {{"items|" + kStone, kStoneGuid}, {"items|" + kLogs, "aaaaaaaa-0000-4000-8000-000000000001"}}, {"items"}, &kb, &kf) == 3,
              "three entries named: the stone, the slowed recipe, the appended one");
        CHECK(kb.registries["items"][0].archive == std::vector<std::string>({"shroudforge:betterstacks"}) &&
                  kb.registries["items"][0].archive_fields.at("shroudforge:betterstacks") == std::set<std::string>({"maxStack"}) &&
                  kb.registries["items"][1].archive.empty(),
              "a ShroudForge package is named on the field it changed, not the whole item");
        CHECK(kb.registries["recipes"][0].archive == std::vector<std::string>({"shroudforge:slow-craft"}) &&
                  kb.registries["recipes"][0].archive_fields.at("shroudforge:slow-craft") == std::set<std::string>({"duration"}) &&
                  kb.registries["recipes"][1].archive.empty() && kb.registries["recipes"][2].archive == std::vector<std::string>({"eml:more-recipes"}) &&
                  !kb.registries["recipes"][2].archive_fields.count("eml:more-recipes"),
              "and a recipe by its element: the changed duration, the appended recipe whole, never another recipe");
        // the chain and the line say the ShroudForge package by name, per field
        const Result kr = compose(s, kb, {}, {mod("bigstacks", {batch(s, "bigstacks", "alter", R"([{"registry": "items", "select": {"id": "game:material_t1_stone"}, "field": "maxStack", "op": {"mul": 2}}])")})});
        const FieldOutcome& kfo = kr.fields.at({"items", kStone, "maxStack"});
        CHECK(kfo.chain.size() == 2 && kfo.chain[0].kind == WriterKind::ArchiveWriter && kfo.chain[0].who == "shroudforge:betterstacks" && kfo.chain[0].archive_field &&
                  kfo.result.i == 200,
              "the ShroudForge package lies below the registry edit, named on the field, and the edit composes on its value");
        // the files: the hosted record beside <stem>.kfc, bound to that file's SHA-256, never through a link
        namespace fs = std::filesystem;
        const fs::path tmp = fs::temp_directory_path() / ("kiln-writers-hosted-" + std::to_string(::getpid()));
        fs::remove_all(tmp);
        const fs::path game = tmp / "game";
        fs::create_directories(game);
        std::string why, hex;
        CHECK(!read_hosted_journal(game, "enshrouded", {}, &why) && why.find("wrote no change journal") != std::string::npos, "no hosted record: none, said: " + why);
        CHECK(!hosted_generation(game, "enshrouded", {}, &hex, &why) && why.find("not there") != std::string::npos, "no <stem>.kfc: no generation: " + why);
        std::ofstream(game / "enshrouded.kfc", std::ios::binary) << "abc";
        CHECK(hosted_generation(game, "enshrouded", {}, &hex, &why) && hex == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
              "the generation is the SHA-256 of the live <stem>.kfc: " + hex);
        std::ofstream(game / "enshrouded.kfc.change-journal.json", std::ios::binary) << hosted.dump();
        const auto ht = read_hosted_journal(game, "enshrouded", {}, &why);
        CHECK(ht && json::parse(*ht) == hosted, "the hosted record beside <stem>.kfc is read whole");
        why.clear();
        CHECK(!read_hosted_journal(game, "enshrouded", [&](const fs::path& p) { return p == game; }, &why) && why.find("the game folder is a link") != std::string::npos,
              "a game folder behind a reparse point is not followed: " + why);
        fs::create_directories(tmp / "elsewhere");
        std::ofstream(tmp / "elsewhere" / "j.json", std::ios::binary) << hosted.dump();
        fs::remove(game / "enshrouded.kfc.change-journal.json");
        fs::create_symlink(tmp / "elsewhere" / "j.json", game / "enshrouded.kfc.change-journal.json");
        why.clear();
        CHECK(!read_hosted_journal(game, "enshrouded", {}, &why) && why.find("the journal is a link") != std::string::npos, "a linked hosted record is not followed: " + why);
        // review-j3: what proving a hosted journal costs (the hash of a <stem>.kfc as big as the game's, about 9 MiB); effect
        // replay's unchanged start does without it (Env::archive_journal_text)
        {
            std::ofstream big(game / "enshrouded.kfc", std::ios::binary | std::ios::trunc);
            const std::string mib(1 << 20, 'k');
            for (int i = 0; i < 9; ++i) big << mib;
        }
        const auto h0 = std::chrono::steady_clock::now();
        CHECK(hosted_generation(game, "enshrouded", {}, &hex, &why) && hex.size() == 64, "a 9 MiB <stem>.kfc is hashed: " + why);
        std::printf("hosted journal proof: SHA-256 of a 9 MiB <stem>.kfc %.1f ms (ASan build)\n",
                    std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - h0).count());
        fs::remove(game / "enshrouded.kfc");
        fs::create_symlink(tmp / "elsewhere" / "j.json", game / "enshrouded.kfc");
        why.clear();
        CHECK(!hosted_generation(game, "enshrouded", {}, &hex, &why) && why.find("is a link") != std::string::npos, "a linked <stem>.kfc gives no generation: " + why);
        fs::remove_all(tmp);
        // the contract with kiln-eml's writer (gpl/kiln-eml/kiln-eml/tests/journal_contract.rs
        // `the_shroudforge_dialect_with_paths_makes_the_record_kilns_reader_is_tested_on` fails unless this fixture is byte for
        // byte its record of a fixed kiln-mode pass): read, named per field and per element, its notApplied package
        {
            std::ifstream sf(root + "/schema/sfi/eml-journal/fixtures/kiln-eml-writer-sf2.json");
            std::stringstream ss;
            ss << sf.rdbuf();
            const std::string written = ss.str();
            std::string live;
            for (int i = 0; i < 32; ++i) live += "7c";
            const ArchiveJournal w = parse_archive_journal(written, live);
            CHECK(!written.empty() && w.state == "read" && w.format == 2 && w.dialect == "shroudforge" && w.writer == "kiln-eml",
                  "kiln-eml's format-2 ShroudForge-dialect record reads: " + w.state + " " + w.text);
            CHECK(w.packages.size() == 3 && w.packages[1].kind == "eml" && w.not_applied.size() == 1 && w.not_applied[0].id == "broken" &&
                      w.not_applied[0].reason.find("boom") != std::string::npos && w.not_applied[0].reason.find('\n') == std::string::npos,
                  "its packages and the one not applied, its reason one line");
            const std::string stone = "1d622bd9-8170-41b1-822e-d3984b386d95", made = "5d2f8a90-1c3b-4e7f-9a12-6b4c8d0e2f13",
                              recipes = "3c0a5e77-2f41-4b6a-9e10-5d8c7b6a5f40";
            BaseModel wb;
            auto e1 = [](const std::string& id, const char* f, Value v) {
                BaseModel::Entry x;
                x.id = id;
                x.fields[f] = v;
                return x;
            };
            wb.registries["items"] = {e1("game:stone", "maxStack", Value::integer(100)), e1("alpha:1", "maxStack", Value::integer(1))};
            wb.registries["recipes"] = {e1("game:a", "duration", Value::number(1)), e1("game:b", "duration", Value::number(4)), e1("bravo:new", "duration", Value::number(2))};
            ArchiveFields wf;
            wf.rows["items"] = {{"maxStack", {"maxStackSize"}}};
            wf.rows["recipes"] = {{"duration", {"craftingDuration"}}};
            wf.array = {{"recipes", "recipes"}};
            const std::map<int32_t, std::string> els = {{0, "game:a"}, {1, "game:b"}, {2, "bravo:new"}};
            wf.elements = [&](const std::string& reg, const std::string& guid) -> const std::map<int32_t, std::string>* {
                return reg == "recipes" && guid == recipes ? &els : nullptr;
            };
            std::map<std::string, std::vector<std::string>> unmatched;
            CHECK(name_archive_writers(w, {{"items|game:stone", stone}, {"items|alpha:1", made}}, {"items"}, &wb, &wf, &unmatched) == 4,
                  "four entries named: the stone, alpha's creation, the slowed recipe and the appended one");
            CHECK(wb.registries["items"][0].archive == std::vector<std::string>({"shroudforge:alpha"}) &&
                      wb.registries["items"][0].archive_fields.at("shroudforge:alpha") == std::set<std::string>({"maxStack"}),
                  "alpha on the stone's stack size; bravo, which wrote it back as it was, nowhere on it");
            CHECK(wb.registries["recipes"][0].archive.empty() && wb.registries["recipes"][1].archive == std::vector<std::string>({"shroudforge:alpha"}) &&
                      wb.registries["recipes"][2].archive == std::vector<std::string>({"eml:bravo"}),
                  "the recipes by element: alpha's slowed one, bravo's appended one");
            CHECK(unmatched["shroudforge:alpha"].size() == 1 && unmatched["shroudforge:alpha"][0].find("created a resource") == 0 &&
                      unmatched["eml:bravo"] == std::vector<std::string>({"the recipes list's membership changed (an element added or removed): not replayed"}),
                  "the replay residue: alpha's creation; bravo's appended recipe (the list's membership; the element itself is named whole): " +
                      json(unmatched).dump());
        }
        // registry.json says whose record was looked at
        ReportMeta hm;
        hm.written = "2026-11-26T20:00:00Z", hm.base.writer = "shroudforge-hosted";
        hm.base.journal = {h.state, h.text, h.packages.size(), 0, false, "shroudforge"};
        const json hd = report_json(s, compose(s, base(), {}, {}), hm);
        CHECK(hd["base"]["journal"]["writer"] == "shroudforge" && hd["base"]["journal"]["state"] == "read", "base.journal.writer: " + hd["base"].dump());
        if (argc > 3) std::ofstream(std::string(argv[3]) + ".hosted.json") << hd.dump(2);
    }

    // ---- review-j3: effect replay's residue (replay.h: a package is replayed whole or not at all, and only where a proven
    // Kiln field holds what it changed). A list's membership changed (an element removed: "recipes[]" alone, nothing named
    // by index) and a member path wider than the Kiln fields under it ("damageSetup" holds power's "damageSetup.dmgMod" and
    // members no field covers) are residue; a path a field's member holds ("input[1].itemStack.count" under inputs' "input")
    // and a field's own member are not ----
    {
        std::string g7;
        for (int i = 0; i < 32; ++i) g7 += "7c";
        const std::string stone = "11111111-0000-4000-8000-000000000001", logs = "11111111-0000-4000-8000-000000000002",
                          recipes = "11111111-0000-4000-8000-0000000000aa";
        auto pkg = [](const std::string& id, const std::string& guid, std::vector<std::string> paths) {
            return json{{"id", id}, {"version", "1.0.0"}, {"kind", "shroudforge"}, {"contentSha256", nullptr}, {"settingsSha256", nullptr}, {"truncated", false},
                        {"changed", json::array({{{"guid", guid}, {"typeHash", "8b4f2d1e"}, {"part", 0}, {"createdBy", nullptr}, {"paths", paths}}})},
                        {"created", json::array()}, {"contents", json::array()}};
        };
        const json doc = {{"format", 2}, {"stem", "enshrouded_server"}, {"generation", g7}, {"receiptSha256", g7}, {"dialect", "shroudforge"},
                          {"attempt", "first"}, {"truncated", false},
                          {"packages", json::array({pkg("remover", recipes, {"recipes[]"}), pkg("structy", stone, {"damageSetup"}),
                                                    pkg("exact", logs, {"maxStackSize"}), pkg("deep", recipes, {"recipes[0].input[1].itemStack.count"})})},
                          {"unattributed", {{"truncated", false}, {"changed", json::array()}, {"created", json::array()}, {"contents", json::array()}}},
                          {"writer", "kiln-eml"}};
        const ArchiveJournal rj = parse_archive_journal(doc.dump(), g7);
        CHECK(rj.state == "read" && rj.packages.size() == 4, "the residue journal reads: " + rj.state + " " + rj.text);
        BaseModel rb;
        auto e1 = [](const std::string& id, std::initializer_list<std::pair<const char*, Value>> f) {
            BaseModel::Entry x;
            x.id = id;
            for (const auto& [n, v] : f) x.fields[n] = v;
            return x;
        };
        rb.registries["items"] = {e1("game:stone", {{"maxStack", Value::integer(100)}, {"power", Value::number(3)}}), e1("game:logs", {{"maxStack", Value::integer(10)}})};
        rb.registries["recipes"] = {e1("game:a", {{"duration", Value::number(1)}, {"inputs", Value::of_list({})}})};
        ArchiveFields rf;
        rf.rows["items"] = {{"maxStack", {"maxStackSize"}}, {"power", {"damageSetup.dmgMod"}}};
        rf.rows["recipes"] = {{"duration", {"craftingDuration"}}, {"inputs", {"input"}}};
        rf.array = {{"recipes", "recipes"}};
        const std::map<int32_t, std::string> rels = {{0, "game:a"}};
        rf.elements = [&](const std::string& reg, const std::string& guid) -> const std::map<int32_t, std::string>* {
            return reg == "recipes" && guid == recipes ? &rels : nullptr;
        };
        std::map<std::string, std::vector<std::string>> un;
        name_archive_writers(rj, {{"items|game:stone", stone}, {"items|game:logs", logs}}, {"items"}, &rb, &rf, &un);
        auto has = [&](const std::string& who, const std::string& part) {
            for (const std::string& l : un[who])
                if (l.find(part) != std::string::npos) return true;
            return false;
        };
        CHECK(has("shroudforge:remover", "membership"), "a recipe removed (recipes[] alone) is residue, not a replay without it: " + json(un["shroudforge:remover"]).dump());
        CHECK(has("shroudforge:structy", "damageSetup") && has("shroudforge:structy", "power"),
              "a member path wider than its Kiln fields (damageSetup over power's damageSetup.dmgMod) is residue: " + json(un["shroudforge:structy"]).dump());
        CHECK(un["shroudforge:exact"].empty() && un["shroudforge:deep"].empty(),
              "a field's own member, and a path inside a field's member, are no residue: " + json(un).dump());
    }

    std::printf("registry writers: %d passed, %d failed\n", g_pass, g_fail);
    return g_fail ? 1 : 0;
}
