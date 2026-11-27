// SPDX-License-Identifier: MIT
// kiln.content registry stage: archive-writer attribution from kiln-eml's change journal (archive_journal.h).
// Reads the documented records (schema/sfi/eml-journal/startup-journal.schema.json, format 1, and eml-journal-2.schema.json,
// format 2); no kiln-eml or ShroudForge code.
#include "archive_journal.h"

#include <algorithm>
#include <cctype>
#include <fstream>
#include <set>

#include "../../../core/src/services/boot_gamedata.h"

namespace kc::registry {

namespace {

std::string lower(std::string s) {
    for (char& c : s) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return s;
}

// A resource key's GUID: 8-4-4-4-12 hex digits, as the game prints it. false: not one.
bool guid_of(const json& key, std::string* out) {
    if (!key.is_object() || !key.contains("guid") || !key["guid"].is_string()) return false;
    std::string g = lower(key["guid"].get<std::string>());
    if (g.size() != 36) return false;
    for (size_t i = 0; i < g.size(); ++i) {
        const bool dash = i == 8 || i == 13 || i == 18 || i == 23;
        if (dash ? g[i] != '-' : !std::isxdigit(static_cast<unsigned char>(g[i]))) return false;
    }
    *out = std::move(g);
    return true;
}

// A format-2 member path as the schema writes it: member names ([A-Za-z_][A-Za-z0-9_]*) joined by '.', each followed by
// any number of [<index>] or [], at most 256 bytes; "" is the whole resource.
bool member_path(const std::string& p) {
    if (p.size() > 256) return false;
    size_t i = 0;
    while (i < p.size()) {
        if (i > 0) {
            if (p[i] != '.') return false;
            ++i;
        }
        const size_t start = i;
        while (i < p.size() && (std::isalnum(static_cast<unsigned char>(p[i])) || p[i] == '_')) ++i;
        if (i == start || std::isdigit(static_cast<unsigned char>(p[start]))) return false;
        while (i < p.size() && p[i] == '[') {
            const size_t close = p.find(']', i);
            if (close == std::string::npos) return false;
            for (size_t k = i + 1; k < close; ++k)
                if (!std::isdigit(static_cast<unsigned char>(p[k]))) return false;
            i = close + 1;
        }
    }
    return true;
}

// One package (or the unattributed bucket): its written and created resources, each GUID once; format 2: each written
// resource's member paths (1 to kArchiveJournalPaths, as the schema writes them). false + *why: malformed.
bool changes_of(const json& c, ArchiveJournal::Package* p, int format, std::string* why) {
    if (!c.is_object()) return *why = "a package is not an object", false;
    if (c.contains("truncated") && c["truncated"].is_boolean()) p->truncated = c["truncated"].get<bool>();
    if (c.contains("contents") && c["contents"].is_array()) p->contents = c["contents"].size();
    const std::string who = p->id.empty() ? "the unattributed bucket" : p->id;
    std::set<std::string> seen;
    for (const char* list : {"changed", "created"}) {
        if (!c.contains(list) || !c[list].is_array()) return *why = std::string("a package has no \"") + list + "\" list", false;
        const bool changed = std::string(list) == "changed";
        auto& out = changed ? p->changed : p->created;
        for (const json& k : c[list]) {
            std::string g;
            if (!guid_of(k, &g)) return *why = std::string("a \"") + list + "\" row of " + who + " has no resource GUID", false;
            if (changed && format == 2) {
                if (!k.contains("paths") || !k["paths"].is_array() || k["paths"].empty() || k["paths"].size() > kArchiveJournalPaths)
                    return *why = "a \"changed\" row of " + who + " has no list of 1 to " + std::to_string(kArchiveJournalPaths) + " member paths", false;
                std::vector<std::string> paths;
                for (const json& x : k["paths"]) {
                    if (!x.is_string() || !member_path(x.get<std::string>())) return *why = "a member path of " + who + " is not one the format writes", false;
                    paths.push_back(x.get<std::string>());
                }
                // one GUID with other type hashes or parts (resources Kiln does not tell apart by GUID): their paths
                // together, so what any of them names is named
                auto& all = p->paths[g];
                all.insert(all.end(), paths.begin(), paths.end());
            }
            if (seen.insert(std::string(list) + g).second) out.push_back(std::move(g));
        }
    }
    for (auto& [g, paths] : p->paths) {
        std::sort(paths.begin(), paths.end());
        paths.erase(std::unique(paths.begin(), paths.end()), paths.end());
        if (!paths.empty() && paths.front().empty()) paths = {std::string()};
    }
    return true;
}

// A ShroudForge identifier, as the format names a package: 1 to 80 of A-Z a-z 0-9 . _ -
bool sf_id(const std::string& id) {
    if (id.empty() || id.size() > 80) return false;
    for (const char c : id)
        if (!std::isalnum(static_cast<unsigned char>(c)) && c != '.' && c != '_' && c != '-') return false;
    return true;
}

// Whose record the text names in messages.
std::string journal_name(const std::string& writer) {
    return writer == "shroudforge" ? "hosted ShroudForge's change journal" : "kiln-eml's change journal";
}

ArchiveJournal unreadable(const std::string& why, const std::string& writer) {
    ArchiveJournal j;
    j.writer = writer;
    j.state = "unreadable";
    j.text = journal_name(writer) + " is not one Kiln reads (format " + (writer == "shroudforge" ? "2" : "1 or 2") + ": " + why +
             "): no archive-writer package is named in this start";
    return j;
}

// One line a person reads (a notApplied reason): no control character, at most 512 bytes.
bool one_line(const std::string& s) {
    if (s.empty() || s.size() > 512) return false;
    return std::none_of(s.begin(), s.end(), [](char c) { return static_cast<unsigned char>(c) < 0x20 || c == 0x7f; });
}

// A plan digest as the format writes it: null (empty) or 64 lower-case hex digits. false: neither.
bool digest_of(const json& p, const char* key, std::string* out) {
    out->clear();
    if (!p.contains(key) || p[key].is_null()) return true;
    if (!p[key].is_string()) return false;
    const std::string d = p[key].get<std::string>();
    if (d.size() != 64 || d.find_first_not_of("0123456789abcdef") != std::string::npos) return false;
    *out = d;
    return true;
}

// A file of the game folder read whole and closed at once, only when it is a regular file that is no link (nor any part named
// in `parts`), at most kArchiveJournalMax. nullopt + *why (begun with `what`) otherwise.
std::optional<std::string> read_regular(const std::filesystem::path& file, const std::vector<std::pair<std::filesystem::path, const char*>>& parts,
                                        const ArchiveLinkTest& is_link, const std::string& what, std::string* why) {
    namespace fs = std::filesystem;
    std::error_code ec;
    for (const auto& [p, name] : parts)
        if (fs::is_symlink(fs::symlink_status(p, ec)) || (is_link && is_link(p)))
            return *why = what + " is not read: " + name + " is a link, which may lead out of the game folder", std::nullopt;
    if (!fs::is_regular_file(fs::symlink_status(file, ec))) return *why = what + " is no regular file: not read", std::nullopt;
    const uintmax_t size = fs::file_size(file, ec);
    if (ec) return *why = what + " could not be read", std::nullopt;
    if (size > kArchiveJournalMax) return *why = what + " is larger than 16 MiB: not read", std::nullopt;
    std::ifstream in(file, std::ios::binary);
    std::string text;
    text.resize(static_cast<size_t>(size));
    if (!in || !in.read(text.data(), static_cast<std::streamsize>(size)) || in.peek() != std::char_traits<char>::eof())
        return *why = what + " could not be read whole (it changed while it was read)", std::nullopt;
    return text;
}

}  // namespace

std::optional<std::string> read_archive_journal(const std::filesystem::path& kiln, const std::filesystem::path& stem, const ArchiveLinkTest& is_link,
                                                std::string* why) {
    namespace fs = std::filesystem;
    const fs::path file = kiln / "eml" / "ownership" / (stem.native() + fs::path(".change-journal.json").native());
    std::error_code ec;
    if (!fs::exists(fs::symlink_status(file, ec)))
        return *why = "kiln-eml wrote no change journal for this start (kiln/eml/ownership): its packages are not named", std::nullopt;
    return read_regular(file, {{kiln, "the kiln folder"}, {kiln / "eml", "kiln/eml"}, {kiln / "eml" / "ownership", "kiln/eml/ownership"}, {file, "the journal"}},
                        is_link, "kiln-eml's change journal", why);
}

std::optional<std::string> read_hosted_journal(const std::filesystem::path& game, const std::filesystem::path& stem, const ArchiveLinkTest& is_link,
                                               std::string* why) {
    namespace fs = std::filesystem;
    const fs::path file = game / (stem.native() + fs::path(".kfc.change-journal.json").native());
    std::error_code ec;
    if (!fs::exists(fs::symlink_status(file, ec)))
        return *why = "hosted ShroudForge wrote no change journal of its pass (" + file.filename().string() +
                      "): its packages are not named (a ShroudForge older than this Kiln's, or the player's own installed one, keeps none)",
               std::nullopt;
    return read_regular(file, {{game, "the game folder"}, {file, "the journal"}}, is_link, "hosted ShroudForge's change journal", why);
}

bool hosted_generation(const std::filesystem::path& game, const std::filesystem::path& stem, const ArchiveLinkTest& is_link, std::string* hex,
                       std::string* why) {
    namespace fs = std::filesystem;
    const fs::path kfc = game / (stem.native() + fs::path(".kfc").native());
    std::error_code ec;
    if (fs::is_symlink(fs::symlink_status(kfc, ec)) || (is_link && is_link(kfc)) || fs::is_symlink(fs::symlink_status(game, ec)) || (is_link && is_link(game)))
        return *why = "the game's archive header (" + kfc.filename().string() + ") or the game folder is a link: no hosted journal is used", false;
    if (!fs::is_regular_file(fs::symlink_status(kfc, ec))) return *why = "the game's archive header (" + kfc.filename().string() + ") is not there", false;
    const uintmax_t size = fs::file_size(kfc, ec);
    if (ec || size > kHostedKfcMax) return *why = "the game's archive header (" + kfc.filename().string() + ") cannot be hashed", false;
    *hex = kiln::boot_gd::file_sha256(kfc);
    if (hex->size() != 64) return *why = "the game's archive header (" + kfc.filename().string() + ") could not be read", false;
    return true;
}

namespace {
// parse_archive_journal over a parsed document; `any`: whatever generation it names is taken as the live one (peek).
ArchiveJournal parse_doc(const json& doc, const std::string& given_generation, const std::string& writer, bool any) {
    auto unreadable = [&](const std::string& why) { return ::kc::registry::unreadable(why, writer); };
    if (doc.is_discarded() || !doc.is_object()) return unreadable("not JSON");
    if (!doc.contains("format") || (doc["format"] != 1 && doc["format"] != 2)) return unreadable("format is not 1 or 2");
    const int format = doc["format"].get<int>();
    if (!doc.contains("generation") || !doc["generation"].is_string()) return unreadable("no generation");
    const std::string live_generation = any ? doc["generation"].get<std::string>() : given_generation;
    ArchiveJournal j;
    j.writer = writer;
    j.generation = lower(doc["generation"].get<std::string>());
    // whose record it is: kiln-eml's (the member absent, as 0.2.0-dev.31.18 writes format 2) or the hosted ShroudForge's; a
    // record in the other's place is no journal of this start's writer
    const std::string says = doc.contains("writer") && doc["writer"].is_string() ? doc["writer"].get<std::string>() : (doc.contains("writer") ? "?" : "kiln-eml");
    if (says != "kiln-eml" && says != "shroudforge") return unreadable("its writer is not kiln-eml or shroudforge");
    if (says != writer) return unreadable("it says " + says + " wrote it, in " + (writer == "shroudforge" ? "hosted ShroudForge's" : "kiln-eml's") + " place");
    const std::string generation = lower(doc["generation"].get<std::string>());
    if (live_generation.empty() || generation != lower(live_generation)) {
        j.state = "stale";
        j.text = journal_name(writer) + " names another generation than the one this start reads (" + generation.substr(0, 12) +
                 " is not the live " + (live_generation.empty() ? std::string("one") : lower(live_generation).substr(0, 12)) +
                 "): left by an earlier start, it names no package in this one";
        return j;
    }
    if (doc.contains("dialect") && doc["dialect"].is_string()) j.dialect = doc["dialect"].get<std::string>();
    // format 2: EML's pass (its packages EML mods) or a ShroudForge pass (kiln mode, or the hosted one); format 1 is kiln-eml's
    // ShroudForge pass only
    if (format == 2 && j.dialect != "eml" && j.dialect != "shroudforge") return unreadable("a format-2 journal of another dialect than eml or shroudforge");
    if (writer == "shroudforge" && (format != 2 || j.dialect != "shroudforge")) return unreadable("a hosted journal that is not format 2 of the ShroudForge dialect");
    j.format = format;
    if (doc.contains("truncated") && doc["truncated"].is_boolean()) j.truncated = doc["truncated"].get<bool>();
    if (!doc.contains("packages") || !doc["packages"].is_array()) return unreadable("no packages");
    std::string why;
    std::set<std::string> ids;
    for (const json& p : doc["packages"]) {
        ArchiveJournal::Package pkg;
        if (!p.is_object() || !p.contains("id") || !p["id"].is_string() || p["id"].get<std::string>().empty()) return unreadable("a package has no id");
        pkg.id = p["id"].get<std::string>();
        // the name players and kiln.log read: only as the format allows it, never a line break or a disguised name
        if (!sf_id(pkg.id)) return unreadable("a package id is not a ShroudForge identifier");
        if (!ids.insert(pkg.id).second) return unreadable("the package " + pkg.id + " is listed twice");
        if (p.contains("version") && p["version"].is_string()) pkg.version = p["version"].get<std::string>();
        if (!p.contains("kind") || (p["kind"] != "shroudforge" && p["kind"] != "eml")) return unreadable("the package " + pkg.id + " has no kind shroudforge or eml");
        if (format == 2 && j.dialect == "eml" && p["kind"] != "eml") return unreadable("the package " + pkg.id + " of an EML pass is not kind eml");
        pkg.kind = p["kind"].get<std::string>();
        if (!digest_of(p, "contentSha256", &pkg.content)) return unreadable("the package " + pkg.id + " has a content digest that is not one");
        if (!changes_of(p, &pkg, format, &why)) return unreadable(why);
        j.packages.push_back(std::move(pkg));
    }
    if (doc.contains("unattributed") && !changes_of(doc["unattributed"], &j.unattributed, format, &why)) return unreadable(why);
    j.unattributed.kind = format == 2 && j.dialect == "eml" ? "eml" : "shroudforge";
    // the packages whose Lua the committed run did not apply (format 2, optional): one line each, never a package that ran
    if (doc.contains("notApplied")) {
        if (format != 2 || !doc["notApplied"].is_array()) return unreadable("notApplied is not a list of a format-2 journal");
        std::set<std::string> missed;
        for (const json& n : doc["notApplied"]) {
            ArchiveJournal::NotApplied row;
            if (!n.is_object() || !n.contains("id") || !n["id"].is_string() || !sf_id(n["id"].get<std::string>())) return unreadable("a package not applied has no ShroudForge identifier");
            row.id = n["id"].get<std::string>();
            if (ids.count(row.id) || !missed.insert(row.id).second) return unreadable("the package " + row.id + " is listed as not applied and as run, or twice");
            if (!n.contains("kind") || (n["kind"] != "shroudforge" && n["kind"] != "eml") || (j.dialect == "eml" && n["kind"] != "eml"))
                return unreadable("the package " + row.id + " not applied has no kind of its dialect");
            row.kind = n["kind"].get<std::string>();
            if (n.contains("version") && n["version"].is_string()) row.version = n["version"].get<std::string>();
            if (!digest_of(n, "contentSha256", &row.content)) return unreadable("the package " + row.id + " not applied has a content digest that is not one");
            if (!n.contains("reason") || !n["reason"].is_string() || !one_line(n["reason"].get<std::string>()))
                return unreadable("the package " + row.id + " not applied has no one-line reason");
            row.reason = n["reason"].get<std::string>();
            j.not_applied.push_back(std::move(row));
        }
    }
    j.state = "read";
    size_t rows = 0;
    for (const auto& p : j.packages) rows += p.changed.size() + p.created.size();
    const bool eml = format == 2 && j.dialect == "eml";
    j.text = journal_name(writer) + " names " + std::to_string(j.packages.size()) +
             (eml ? (j.packages.size() == 1 ? " EML mod" : " EML mods") : (j.packages.size() == 1 ? " package" : " packages")) +
             " of the startup pass (" + std::to_string(rows) + " resources " + (format == 2 && !eml ? "changed" : "written") + " or created" +
             (format == 2 ? (eml ? ", with the members written" : ", with the members changed") : "") + ")" +
             (j.not_applied.empty() ? "" : "; " + std::to_string(j.not_applied.size()) + (j.not_applied.size() == 1 ? " package's" : " packages'") + " Lua was not applied") +
             (j.truncated ? "; it is truncated, so a package past its bounds is not named everywhere it wrote" : "");
    return j;
}

}  // namespace

ArchiveJournal parse_archive_journal(const std::string& text, const std::string& live_generation, const std::string& writer) {
    return parse_doc(json::parse(text, nullptr, false), live_generation, writer, false);
}

ArchiveJournal peek_archive_journal(const std::string& text, const std::string& writer) {
    return parse_doc(json::parse(text, nullptr, false), std::string(), writer, true);
}

bool eml_state_generation(const std::string& state_json, std::string* hex, std::string* why) {
    const json doc = json::parse(state_json, nullptr, false);
    if (doc.is_discarded() || !doc.is_object()) return *why = "kiln-eml's state of this start is not readable: no change journal is used", false;
    const std::string phase = doc.contains("phase") && doc["phase"].is_string() ? doc["phase"].get<std::string>() : "";
    if (phase == "aside") return *why = "kiln-eml stood aside in this start (another loader wrote the game's data): no change journal names its packages", false;
    if (phase == "idle") return *why = "kiln-eml ran no EML mod in this start: it keeps no change journal", false;
    if (phase != "done") return *why = "kiln-eml's EML pass did not end well in this start (" + (phase.empty() ? std::string("no phase") : phase) + "): no change journal is used", false;
    if (!doc.contains("generation") || !doc["generation"].is_string())
        return *why = "kiln-eml reports no live generation in this start (it published nothing, or it is older than its change journal): its mods are not named", false;
    const std::string g = lower(doc["generation"].get<std::string>());
    if (g.size() != 64 || g.find_first_not_of("0123456789abcdef") != std::string::npos)
        return *why = "kiln-eml's live generation is not a SHA-256: no change journal is used", false;
    *hex = g;
    return true;
}

std::set<std::string> archive_fields_of(const std::map<std::string, std::vector<std::string>>& rows, const std::vector<std::string>& paths) {
    // a and b are one member or one holds the other, at a member boundary ('.' or '[')
    auto overlap = [](const std::string& a, const std::string& b) {
        const std::string& s = a.size() <= b.size() ? a : b;
        const std::string& l = a.size() <= b.size() ? b : a;
        return l.compare(0, s.size(), s) == 0 && (l.size() == s.size() || l[s.size()] == '.' || l[s.size()] == '[');
    };
    std::set<std::string> out;
    for (const auto& [field, members] : rows)
        for (const std::string& p : paths)
            if (p.empty() || std::any_of(members.begin(), members.end(), [&](const std::string& m) { return overlap(p, m); })) {
                out.insert(field);
                break;
            }
    return out;
}

size_t name_archive_writers(const ArchiveJournal& journal, const std::map<std::string, std::string>& ids, const std::set<std::string>& whole,
                            BaseModel* base, const ArchiveFields* fields, std::map<std::string, std::vector<std::string>>* unmatched) {
    if (journal.state != "read" || !base) return 0;
    // GUID -> the entries that are that resource (one per registry it is an entry of); an element of a shared resource is
    // not one (its GUID member is no resource key)
    std::map<std::string, std::vector<std::pair<std::string, std::string>>> by_guid;
    for (const auto& [key, guid] : ids) {
        const size_t bar = key.find('|');
        if (bar == std::string::npos || guid.empty() || !whole.count(key.substr(0, bar))) continue;
        by_guid[lower(guid)].emplace_back(key.substr(0, bar), key.substr(bar + 1));
    }
    // (registry, id) -> the packages in run order, and for each one named on some fields only, those fields
    struct Named {
        std::vector<std::string> order;
        std::map<std::string, std::set<std::string>> some;  // who -> fields (absent: the whole entry)
    };
    std::map<std::pair<std::string, std::string>, Named> names;
    auto name = [&](const std::pair<std::string, std::string>& entry, const std::string& who, const std::set<std::string>* only) {
        Named& n = names[entry];
        const bool fresh = std::find(n.order.begin(), n.order.end(), who) == n.order.end();
        if (fresh) n.order.push_back(who);
        if (!only) n.some.erase(who);  // the whole entry
        else if (fresh) n.some[who] = *only;
        else if (const auto it = n.some.find(who); it != n.some.end()) it->second.insert(only->begin(), only->end());
    };
    const std::map<std::string, std::vector<std::string>> none;
    auto rows_of = [&](const std::string& reg) -> const std::map<std::string, std::vector<std::string>>& {
        if (!fields) return none;
        const auto it = fields->rows.find(reg);
        return it == fields->rows.end() ? none : it->second;
    };
    // An entry named from format-2 paths: whole when a path is "", else on the fields they name (none: not named).
    auto name_paths = [&](const std::pair<std::string, std::string>& entry, const std::string& who, const std::vector<std::string>& paths) {
        if (std::find(paths.begin(), paths.end(), std::string()) != paths.end()) return name(entry, who, nullptr);
        const std::set<std::string> f = archive_fields_of(rows_of(entry.first), paths);
        if (!f.empty()) name(entry, who, &f);
    };
    auto note = [&](const ArchiveJournal::Package& p) {
        const std::string who = archive_writer_who(p.kind, p.id, journal.writer);
        std::vector<std::string>* lines = unmatched && !p.id.empty() ? &(*unmatched)[who] : nullptr;
        auto say = [&](const std::string& line) {
            if (lines && std::find(lines->begin(), lines->end(), line) == lines->end() && lines->size() < 64) lines->push_back(line);
        };
        for (const std::string& g : p.created) {
            say("created a resource (" + g + "): a created entry is not replayed");
            if (const auto it = by_guid.find(g); it != by_guid.end())
                for (const auto& entry : it->second) name(entry, who, nullptr);
        }
        if (p.contents) say("created " + std::to_string(p.contents) + " content(s): assets are not replayed");
        // a member path that names Kiln fields only by holding their members ("damageSetup" over power's
        // "damageSetup.dmgMod") changed more than those fields: what else it holds no field proves (review-j3)
        auto wider = [&](const std::string& reg, const std::string& place, const std::string& path) {
            const auto& rows = rows_of(reg);
            const std::set<std::string> named = archive_fields_of(rows, {path});
            if (named.empty()) return say(place + ": " + path + " is no Kiln field");
            for (const auto& [field, members] : rows)
                for (const std::string& m : members)
                    if (path.compare(0, m.size(), m) == 0 && (path.size() == m.size() || path[m.size()] == '.' || path[m.size()] == '['))
                        return;  // a field's own member, or a place inside one
            std::string list;
            for (const std::string& f : named) list += (list.empty() ? "" : ", ") + f;
            say(place + ": " + path + " holds more than its Kiln field(s) " + list + " (the rest is no Kiln field)");
        };
        for (const std::string& g : p.changed) {
            const auto pit = p.paths.find(g);
            bool known = false;  // the resource is some registry's entry, or holds some array registry's elements
            if (const auto it = by_guid.find(g); it != by_guid.end())
                for (const auto& entry : it->second) {
                    known = true;
                    if (pit == p.paths.end() || !fields) name(entry, who, nullptr);
                    else {
                        name_paths(entry, who, pit->second);
                        for (const std::string& path : pit->second)
                            if (!path.empty()) wider(entry.first, entry.first + "[" + entry.second + "]", path);
                    }
                }
            // an array registry's elements: only from paths, which say which element ("<array>[<i>]...")
            if (pit == p.paths.end() || !fields || !fields->elements) {
                if (!known) say("a resource that is no registry entry (" + g + ")");
                continue;
            }
            for (const auto& [reg, array] : fields->array) {
                const std::map<int32_t, std::string>* elements = fields->elements(reg, g);
                if (!elements || elements->empty()) continue;
                known = true;
                for (const std::string& path : pit->second) {
                    const bool under = path.compare(0, array.size() + 1, array + "[") == 0;
                    const bool holds = path.empty() || (array.compare(0, path.size(), path) == 0 &&
                                                        (array.size() == path.size() || array[path.size()] == '.' || array[path.size()] == '['));
                    if (!under && !holds) say("a member of the " + reg + " resource that is no Kiln field (" + path + ")");
                    if (!under) continue;
                    const size_t close = path.find(']', array.size());
                    // the list's membership: an element added (also named by its index, whole) or removed (named nowhere):
                    // what a replay of field values cannot do (review-j3)
                    if (close == array.size() + 1) {
                        say("the " + reg + " list's membership changed (an element added or removed): not replayed");
                        continue;
                    }
                    const std::string rest = close == std::string::npos ? std::string() : path.substr(close + 1);
                    if (rest.size() > 1 && rest[0] == '.')
                        wider(reg, reg + " element " + path.substr(array.size(), close - array.size() + 1), rest.substr(1));
                }
                std::map<int32_t, std::vector<std::string>> at;  // element -> its paths from the element's root
                for (const std::string& path : pit->second) {
                    // the whole resource, or the list itself (or a member holding it) put in place: every element, whole
                    const bool holds_list = array.compare(0, path.size(), path) == 0 &&
                                            (array.size() == path.size() || array[path.size()] == '.' || array[path.size()] == '[');
                    if (path.empty() || holds_list) {
                        for (const auto& [i, id] : *elements) at[i] = {std::string()};
                        continue;
                    }
                    if (path.compare(0, array.size() + 1, array + "[") != 0) continue;  // another member, or the list itself
                    const size_t close = path.find(']', array.size());
                    if (close == array.size() + 1) continue;  // "<array>[]": its members changed, no element named
                    const std::string digits = path.substr(array.size() + 1, close - array.size() - 1);
                    if (digits.size() > 9) continue;
                    const int32_t i = std::stoi(digits);
                    const std::string rest = path.substr(close + 1);
                    auto& v = at[i];
                    v.push_back(rest.empty() || rest[0] != '.' ? std::string() : rest.substr(1));
                }
                for (const auto& [i, paths] : at)
                    if (const auto e = elements->find(i); e != elements->end()) name_paths({reg, e->second}, who, paths);
            }
            if (!known) say("a resource that is no registry entry (" + g + ")");
        }
    };
    note(journal.unattributed);
    for (const auto& p : journal.packages) note(p);
    size_t named = 0;
    for (auto& [reg, entries] : base->registries)
        for (BaseModel::Entry& e : entries) {
            const auto it = names.find({reg, e.id});
            if (it == names.end()) continue;
            e.archive = it->second.order;
            e.archive_fields = it->second.some;
            ++named;
        }
    return named;
}

}  // namespace kc::registry
