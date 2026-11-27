// SPDX-License-Identifier: MIT
// kiln.content registry stage: which archive-writer package wrote which game entry before Kiln (commitment 7, D1).
//
// kiln-eml keeps a change journal of its startup pass beside its ownership receipt:
// <game>/kiln/eml/ownership/<stem>.change-journal.json, in one of two formats:
//  - format 1 (schema/sfi/eml-journal/startup-journal.schema.json): a kiln-mode ShroudForge-dialect pass. Per package of
//    the committed attempt, in run order (ShroudForge packages and EML packages the pass ran), the archive resources its
//    entrypoint wrote and the ones it created, by resource key (GUID, type hash, part);
//  - format 2 (eml-journal-2.schema.json): the same per package, and on each written resource the member paths the package
//    wrote there, at their published places ("maxStackSize", "recipes[37].craftingDuration", "input[]" for a list's
//    members, "" for the whole resource). Dialect "eml": EML's own pass (an EML start outside kiln mode, 0.2.0-dev.31.18;
//    a kiln-mode plan of EML mods only, next release); dialect "shroudforge": a kiln-mode ShroudForge-dialect pass (next
//    release; a package is named where its entrypoint changed a member, compared with what the resource held before it).
// Kiln's hosted ShroudForge (the bundled fork) keeps the same record of its own pass, format 2 of dialect "shroudforge" with
// "writer": "shroudforge", beside its record of the pass: <game>/<stem>.kfc.change-journal.json, bound to the SHA-256 of
// the <stem>.kfc it published (read_hosted_journal, hosted_generation). Either record may list the packages whose Lua the
// committed run did not apply ("notApplied": the failed package and the ones left out with it; effect replay reads it).
// Never a value. This reader takes that record (Kiln's own code over the documented file formats; nothing of kiln-eml's
// source) and names each package on the registry entries whose resource it lists, so the report says "<package> (EML
// package) wrote this before Kiln; Kiln's edit wins (D1)" instead of nothing.
//
// What it can and cannot say:
//  - format 1 is per resource: a package is named on every field of an entry whose resource it wrote, whatever member
//    it changed. Format 2 is per field where its paths name one: a package is named on the Kiln fields whose regmap
//    member paths a written path holds or lies in (a path "" or a resource named whole is every field). "Wrote" means
//    written, not changed (writing a field's current value names it too). The journal can over-name a writer, never miss
//    one, within its bounds;
//  - format 1 names only registries whose entries are each one whole resource (regmap entries kind resource or single:
//    items, drops, balance). An entry of a registry whose entries are the elements of a shared resource (kind array:
//    recipes, loot) needs to know which element a package wrote: format 2's paths say it ("recipes[37]...", the element's
//    index in the published resource), format 1 does not, so from a format-1 journal no recipe or loot entry is named (an
//    element's own GUID member, recipeGuid, is no resource key: matching it could name a package that never wrote it).
//    The list itself put in place ("recipes": the whole list replaced) names every element; its membership written
//    ("recipes[]": an element inserted, removed or the list cleared) names no other element;
//  - a journal is used only when its generation is the start's live generation: in a kiln-mode start the startup
//    outcome's archiveSha256, in an EML start the receipt kiln-eml reports live in its state (eml.json "generation"). A
//    writer that ignores the receipt (hosted ShroudForge, another loader) can leave one behind that names another
//    generation. Such a journal is "stale" and names nobody; an old reader refuses format 2 whole (it names nobody);
//  - the player's own installed ShroudForge and other loaders keep none: their pregame values are in the base and the
//    report says no journal named them (Journal::state); a hosted record left beside an installed ShroudForge's output
//    names its packages only while the <stem>.kfc it is bound to is the live one.
// Plain data apart from read_archive_journal (one file): no game memory, no lock.
#pragma once
#include <cstdint>
#include <filesystem>
#include <functional>
#include <map>
#include <optional>
#include <set>
#include <string>
#include <vector>

#include "compose.h"

namespace kc::registry {

struct ArchiveJournal {
    // read: this start's journal, bound to its live generation; none: this start has none (the text says why); stale: one
    // for another generation; unreadable: the file is not a journal of format 1 or 2 (the text says what is wrong).
    std::string state = "none";
    std::string text;
    std::string dialect;  // "shroudforge" (format 1, or format 2 of a kiln-mode or hosted SF pass) | "eml" (format 2)
    int format = 0;       // 1 or 2 once read
    std::string writer = "kiln-eml";  // who wrote it: "kiln-eml" | "shroudforge" (Kiln's hosted ShroudForge)
    std::string generation;           // the generation it names (lower-case hex; empty: not read that far)
    bool truncated = false;
    struct Package {
        std::string id, version, kind;  // kind "shroudforge" | "eml"
        std::string content;            // its contentSha256 (lower-case hex; empty: null, no plan froze it)
        std::vector<std::string> changed, created;  // resource GUIDs, lower case, as the game prints them
        // format 2: the member paths written in each changed resource (GUID -> paths, sorted; "" the whole resource; a
        // GUID listed with several type hashes or parts: all their paths). A GUID without paths (format 1) is named whole.
        std::map<std::string, std::vector<std::string>> paths;
        size_t contents = 0;  // the contents it created (assets: never a registry entry)
        bool truncated = false;
    };
    std::vector<Package> packages;  // in run order
    Package unattributed;           // id empty: writes made while no entrypoint ran
    // format 2 "notApplied": the plan's packages whose pregame Lua the committed run did not apply, in the plan's order
    struct NotApplied {
        std::string id, version, kind, content, reason;  // content: contentSha256 (empty: null); reason: one line
    };
    std::vector<NotApplied> not_applied;
};

// The largest journal read: kiln-eml reads its own at most this big (StartupJournal::read), and its bounds keep one far
// below (32,768 rows in all; format 2: at most 8 MiB of member paths).
constexpr uintmax_t kArchiveJournalMax = 16u << 20;
// Format 2: the most member paths one changed resource lists (kiln-eml RESOURCE_PATHS: a path per element of the
// largest shared resource, 2,542 recipes, several times over; more and it is listed whole).
constexpr size_t kArchiveJournalPaths = 16384;

// The journal's text, read only from the game folder itself as kiln-eml writes it (kiln-eml refuses a linked archive or
// state path): <kiln>/eml/ownership/<stem>.change-journal.json, where kiln is <game>/kiln. nullopt + *why when there is
// none, when the kiln folder, eml, ownership or the file is a link (is_link: a symbolic link, a junction or any other
// reparse point), when it is no regular file, larger than kArchiveJournalMax, or unreadable. Read and closed at once (an
// open handle can make the next kiln-mode start fail to remove it).
using ArchiveLinkTest = std::function<bool(const std::filesystem::path&)>;
std::optional<std::string> read_archive_journal(const std::filesystem::path& kiln, const std::filesystem::path& stem, const ArchiveLinkTest& is_link,
                                                std::string* why);

// Kiln's hosted ShroudForge's journal of its own pass: <game>/<stem>.kfc.change-journal.json, read only from the game folder
// itself (neither the file nor the game folder a link: is_link as above), at most kArchiveJournalMax, read and closed at once.
// nullopt + *why when there is none or it cannot be read so.
std::optional<std::string> read_hosted_journal(const std::filesystem::path& game, const std::filesystem::path& stem, const ArchiveLinkTest& is_link,
                                               std::string* why);
// The generation a hosted journal is bound to: the SHA-256 (lower-case hex) of the live <game>/<stem>.kfc, the archive's
// header the game reads (about 9 MB; the hosted ShroudForge's record of its pass keeps the same digest). false + *why: the
// file is not there, is a link, is larger than kHostedKfcMax or cannot be read.
constexpr uintmax_t kHostedKfcMax = 256u << 20;
bool hosted_generation(const std::filesystem::path& game, const std::filesystem::path& stem, const ArchiveLinkTest& is_link, std::string* hex,
                       std::string* why);

// The journal's text against the generation this start's archive writer published (lower-case hex, 64 digits). Never
// throws: a malformed file is state "unreadable" with the reason. A package must be named as the format says (a ShroudForge
// identifier, kind "shroudforge" or "eml", each id once; format 2: dialect "eml" with kind "eml" only, or "shroudforge",
// every changed resource with 1 to kArchiveJournalPaths member paths as the schema writes them; a "notApplied" row a
// ShroudForge identifier, a kind, one line of reason): a journal that names one otherwise names nobody. `writer`: whose
// place the text was read from ("kiln-eml", or "shroudforge" for the hosted record): a record whose "writer" is another's
// (or a hosted one of another dialect or format than 2 of dialect "shroudforge") is unreadable.
ArchiveJournal parse_archive_journal(const std::string& text, const std::string& live_generation, const std::string& writer = "kiln-eml");
// The same, taking whatever generation the record names as the live one (state "read" for a well-formed record): what it
// lists, without the proof that it is this start's. Effect replay (replay.h) uses it only to see that nothing changed since
// its last record, which needs no hash of the archive; everything it records or replays comes from parse_archive_journal.
ArchiveJournal peek_archive_journal(const std::string& text, const std::string& writer = "kiln-eml");

// The live generation of an EML start (outside kiln mode) from kiln-eml's state of this process (kiln.eml state_json, the
// text of kiln/state/eml.json): its "generation" when the phase is "done", lower-case hex. false + *why otherwise (kiln-eml
// stood aside, ran no mod, failed, or is older than its journal).
bool eml_state_generation(const std::string& state_json, std::string* hex, std::string* why);

// What the report calls a package: "shroudforge:<id>" / "eml:<id>"; "kiln-eml:unattributed" (or, in a hosted record,
// "shroudforge-hosted:unattributed") for the unattributed bucket.
inline std::string archive_writer_who(const std::string& kind, const std::string& id, const std::string& writer = "kiln-eml") {
    if (id.empty()) return writer == "shroudforge" ? std::string("shroudforge-hosted:unattributed") : std::string("kiln-eml:unattributed");
    return (kind == "eml" ? "eml:" : "shroudforge:") + id;
}
// For messages: "<id> (ShroudForge package)", "<id> (EML package)", "a startup write of no package Kiln can name (kiln-eml)".
inline std::string archive_writer_label(const std::string& who) {
    if (who == "kiln-eml:unattributed") return "a startup write of no package Kiln can name (kiln-eml)";
    if (who == "shroudforge-hosted:unattributed") return "a startup write of no package Kiln can name (hosted ShroudForge)";
    if (who.rfind("eml:", 0) == 0) return who.substr(4) + " (EML package)";
    if (who.rfind("shroudforge:", 0) == 0) return who.substr(12) + " (ShroudForge package)";
    return who;
}

// What naming per field and per element needs (format 2's paths), from the regmap.
struct ArchiveFields {
    // registry -> Kiln field -> the game's member paths from the entry's root (regmap rows; only the fields a FieldRef
    // names: a list's element rows belong to the list's own row)
    std::map<std::string, std::map<std::string, std::vector<std::string>>> rows;
    // registry -> the member that holds its entries (regmap entries kind array: recipes "recipes", loot "items")
    std::map<std::string, std::string> array;
    // An array registry's entries in the resource with this GUID (lower case): element index -> Kiln id. null: not known
    // (no element of it is named).
    std::function<const std::map<int32_t, std::string>*(const std::string& registry, const std::string& guid)> elements;
};

// The Kiln fields of `rows` that a written member path names: the path holds a field's member path, or lies inside one
// ("damageSetup" names damage.cut and power; "input[2].itemStack.count" names inputs). "" names every field.
std::set<std::string> archive_fields_of(const std::map<std::string, std::vector<std::string>>& rows, const std::vector<std::string>& paths);

// Names the journal's packages on the base's game entries, in run order (BaseModel::Entry::archive):
//  - an entry of a registry in `whole` (its entries are each one whole resource, so the id table's GUID is the resource's)
//    whose GUID (ids: "<registry>|<kiln id>" -> GUID text, the model's id table) a package created, or wrote without paths
//    (format 1), gets the package on the whole entry; written with paths (format 2), on the fields they name
//    (BaseModel::Entry::archive_fields; a path naming no Kiln field names nothing);
//  - with `fields` and format-2 paths, an entry of an array registry gets the package when a path names its element
//    ("<array>[<i>]": the whole element; "<array>[<i>].<member>": the fields that names; "" or "<array>" itself: every
//    element);
//  - any other entry is never named.
// Returns how many entries were named. `unmatched` (effect replay, replay.h): per package (who), one line for each part of
// its record that names no Kiln field: a resource that is no registry entry, a member path no field holds, a created
// resource, created contents.
size_t name_archive_writers(const ArchiveJournal& journal, const std::map<std::string, std::string>& ids, const std::set<std::string>& whole,
                            BaseModel* base, const ArchiveFields* fields = nullptr,
                            std::map<std::string, std::vector<std::string>>* unmatched = nullptr);

}  // namespace kc::registry
