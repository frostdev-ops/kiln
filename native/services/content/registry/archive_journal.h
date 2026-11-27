// SPDX-License-Identifier: MIT
// kiln.content registry stage: which archive-writer package wrote which game entry before Kiln (commitment 7, D1).
//
// kiln-eml keeps a change journal of its startup pass beside its ownership receipt:
// <game>/kiln/eml/ownership/<stem>.change-journal.json, in one of two formats:
//  - format 1 (schema/sfi/eml-journal/startup-journal.schema.json): a kiln-mode ShroudForge-dialect pass. Per package of
//    the committed attempt, in run order (ShroudForge packages and EML packages the pass ran), the archive resources its
//    entrypoint wrote and the ones it created, by resource key (GUID, type hash, part);
//  - format 2 (eml-journal-2.schema.json, 0.2.0-dev.31.18): EML's own pass (an EML start outside kiln mode). The
//    same per mod of the run, and on each written resource the member paths the mod wrote there, at their published
//    places ("maxStackSize", "recipes[37].craftingDuration", "input[]" for a list's members, "" for the whole resource).
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
//  - hosted or installed ShroudForge and other loaders keep none: their pregame values are in the base and the report
//    says no journal named them (Journal::state).
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
    std::string dialect;  // "shroudforge" (format 1) | "eml" (format 2)
    int format = 0;       // 1 or 2 once read
    bool truncated = false;
    struct Package {
        std::string id, version, kind;  // kind "shroudforge" | "eml"
        std::vector<std::string> changed, created;  // resource GUIDs, lower case, as the game prints them
        // format 2: the member paths written in each changed resource (GUID -> paths, sorted; "" the whole resource; a
        // GUID listed with several type hashes or parts: all their paths). A GUID without paths (format 1) is named whole.
        std::map<std::string, std::vector<std::string>> paths;
        bool truncated = false;
    };
    std::vector<Package> packages;  // in run order
    Package unattributed;           // id empty: writes made while no entrypoint ran
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

// The journal's text against the generation this start's archive writer published (lower-case hex, 64 digits). Never
// throws: a malformed file is state "unreadable" with the reason. A package must be named as the format says (a ShroudForge
// identifier, kind "shroudforge" or "eml", each id once; format 2: dialect "eml", kind "eml", every changed resource with
// 1 to kArchiveJournalPaths member paths as the schema writes them): a journal that names one otherwise names nobody.
ArchiveJournal parse_archive_journal(const std::string& text, const std::string& live_generation);

// The live generation of an EML start (outside kiln mode) from kiln-eml's state of this process (kiln.eml state_json, the
// text of kiln/state/eml.json): its "generation" when the phase is "done", lower-case hex. false + *why otherwise (kiln-eml
// stood aside, ran no mod, failed, or is older than its journal).
bool eml_state_generation(const std::string& state_json, std::string* hex, std::string* why);

// What the report calls a package: "shroudforge:<id>" / "eml:<id>"; "kiln-eml:unattributed" for the unattributed bucket.
inline std::string archive_writer_who(const std::string& kind, const std::string& id) {
    return id.empty() ? std::string("kiln-eml:unattributed") : (kind == "eml" ? "eml:" : "shroudforge:") + id;
}
// For messages: "<id> (ShroudForge package)", "<id> (EML package)", "a startup write of no package Kiln can name (kiln-eml)".
inline std::string archive_writer_label(const std::string& who) {
    if (who == "kiln-eml:unattributed") return "a startup write of no package Kiln can name (kiln-eml)";
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

// The member paths a registry field is named by in a format-2 journal: its regmap row's paths, except on a template (creatures):
// its fields live in the component list, which a journal names by its published place ("components[12].value..."), not by the
// component's type the rows start at, so any write in the list names the creature ("components": the entry, never a miss).
// A journal write in it names the template entry whole, not its fields one by one (name_archive_writers).
inline constexpr const char* kTemplateComponents = "components";
std::vector<std::string> archive_paths_of(const std::string& resource_type, const std::vector<std::string>& row_paths);

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
// Returns how many entries were named.
size_t name_archive_writers(const ArchiveJournal& journal, const std::map<std::string, std::string>& ids, const std::set<std::string>& whole,
                            BaseModel* base, const ArchiveFields* fields = nullptr);

}  // namespace kc::registry
