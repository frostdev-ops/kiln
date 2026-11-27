// SPDX-License-Identifier: MIT
// kiln.content registry stage: archive-writer attribution from kiln-eml's change journal (archive_journal.h).
// Reads the documented records (schema/sfi/eml-journal/startup-journal.schema.json, format 1, and eml-journal-2.schema.json,
// format 2); no kiln-eml code.
#include "archive_journal.h"

#include <algorithm>
#include <cctype>
#include <fstream>
#include <set>

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

ArchiveJournal unreadable(const std::string& why) {
    ArchiveJournal j;
    j.state = "unreadable";
    j.text = "kiln-eml's change journal is not one Kiln reads (format 1 or 2: " + why + "): no archive-writer package is named in this start";
    return j;
}

}  // namespace

std::optional<std::string> read_archive_journal(const std::filesystem::path& kiln, const std::filesystem::path& stem, const ArchiveLinkTest& is_link,
                                                std::string* why) {
    namespace fs = std::filesystem;
    const fs::path file = kiln / "eml" / "ownership" / (stem.native() + fs::path(".change-journal.json").native());
    std::error_code ec;
    if (!fs::exists(fs::symlink_status(file, ec)))
        return *why = "kiln-eml wrote no change journal for this start (kiln/eml/ownership): its packages are not named", std::nullopt;
    const std::pair<fs::path, const char*> parts[] = {{kiln, "the kiln folder"}, {kiln / "eml", "kiln/eml"}, {kiln / "eml" / "ownership", "kiln/eml/ownership"}, {file, "the journal"}};
    for (const auto& [p, name] : parts)
        if (fs::is_symlink(fs::symlink_status(p, ec)) || (is_link && is_link(p)))
            return *why = std::string("kiln-eml's change journal is not read: ") + name + " is a link, which may lead out of the game folder", std::nullopt;
    if (!fs::is_regular_file(fs::symlink_status(file, ec))) return *why = "kiln-eml's change journal is no regular file: not read", std::nullopt;
    const uintmax_t size = fs::file_size(file, ec);
    if (ec) return *why = "kiln-eml's change journal could not be read", std::nullopt;
    if (size > kArchiveJournalMax) return *why = "kiln-eml's change journal is larger than 16 MiB: not read", std::nullopt;
    std::ifstream in(file, std::ios::binary);
    std::string text;
    text.resize(static_cast<size_t>(size));
    if (!in || !in.read(text.data(), static_cast<std::streamsize>(size)) || in.peek() != std::char_traits<char>::eof())
        return *why = "kiln-eml's change journal could not be read whole (it changed while it was read)", std::nullopt;
    return text;
}

ArchiveJournal parse_archive_journal(const std::string& text, const std::string& live_generation) {
    const json doc = json::parse(text, nullptr, false);
    if (doc.is_discarded() || !doc.is_object()) return unreadable("not JSON");
    if (!doc.contains("format") || (doc["format"] != 1 && doc["format"] != 2)) return unreadable("format is not 1 or 2");
    const int format = doc["format"].get<int>();
    if (!doc.contains("generation") || !doc["generation"].is_string()) return unreadable("no generation");
    ArchiveJournal j;
    const std::string generation = lower(doc["generation"].get<std::string>());
    if (live_generation.empty() || generation != lower(live_generation)) {
        j.state = "stale";
        j.text = "kiln-eml's change journal names another generation than the one this start reads (" + generation.substr(0, 12) +
                 " is not the live " + (live_generation.empty() ? std::string("one") : lower(live_generation).substr(0, 12)) +
                 "): left by an earlier start, it names no package in this one";
        return j;
    }
    if (doc.contains("dialect") && doc["dialect"].is_string()) j.dialect = doc["dialect"].get<std::string>();
    // format 2 is the EML dialect's (its packages EML mods): anything else is no journal Kiln knows
    if (format == 2 && j.dialect != "eml") return unreadable("a format-2 journal of another dialect than eml");
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
        if (format == 2 && p["kind"] != "eml") return unreadable("the package " + pkg.id + " of an EML pass is not kind eml");
        pkg.kind = p["kind"].get<std::string>();
        if (!changes_of(p, &pkg, format, &why)) return unreadable(why);
        j.packages.push_back(std::move(pkg));
    }
    if (doc.contains("unattributed") && !changes_of(doc["unattributed"], &j.unattributed, format, &why)) return unreadable(why);
    if (format == 2) j.unattributed.kind = "eml";
    j.state = "read";
    size_t rows = 0;
    for (const auto& p : j.packages) rows += p.changed.size() + p.created.size();
    j.text = "kiln-eml's change journal names " + std::to_string(j.packages.size()) +
             (format == 2 ? (j.packages.size() == 1 ? " EML mod" : " EML mods") : (j.packages.size() == 1 ? " package" : " packages")) +
             " of the startup pass (" + std::to_string(rows) + " resources written or created" + (format == 2 ? ", with the members written" : "") + ")" +
             (j.truncated ? "; it is truncated, so a package past its bounds is not named everywhere it wrote" : "");
    return j;
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
                            BaseModel* base, const ArchiveFields* fields) {
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
        const std::string who = archive_writer_who(p.kind, p.id);
        for (const std::string& g : p.created)
            if (const auto it = by_guid.find(g); it != by_guid.end())
                for (const auto& entry : it->second) name(entry, who, nullptr);
        for (const std::string& g : p.changed) {
            const auto pit = p.paths.find(g);
            if (const auto it = by_guid.find(g); it != by_guid.end())
                for (const auto& entry : it->second) {
                    if (pit == p.paths.end() || !fields) {
                        // a part of a shared resource: a key alone does not say the package wrote that part
                        if (!fields || !fields->partial.count(entry.first)) name(entry, who, nullptr);
                    } else {
                        name_paths(entry, who, pit->second);
                    }
                }
            // an array registry's elements: only from paths, which say which element ("<array>[<i>]...")
            if (pit == p.paths.end() || !fields || !fields->elements) continue;
            for (const auto& [reg, array] : fields->array) {
                const std::map<int32_t, std::string>* elements = fields->elements(reg, g);
                if (!elements || elements->empty()) continue;
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
