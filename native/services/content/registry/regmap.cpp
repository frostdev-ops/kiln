// SPDX-License-Identifier: MIT
// kiln.content registry stage: the regmap's reader, the routing rule and the entries' Kiln ids (regmap.h).
#include "regmap.h"

#include <algorithm>
#include <cctype>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <set>
#include <sstream>

#include "../../../core/src/services/boot_gamedata.h"

#include "element_num.h"
#include "schema.g.h"

namespace kc::registry::map {

namespace {

std::string lower(std::string s) {
    for (char& c : s) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return s;
}

bool read_of(const std::string& s, Read* r) {
    if (s == "now") return *r = Read::Now, true;
    if (s == "worldLoad") return *r = Read::WorldLoad, true;
    if (s == "restart") return *r = Read::Restart, true;
    if (s == "unknown") return *r = Read::Unknown, true;
    return false;
}

const char* read_name(Read r) {
    return r == Read::Now ? "now" : r == Read::WorldLoad ? "worldLoad" : r == Read::Restart ? "restart" : "unknown";
}

bool is_conv(const std::string& c) {
    return std::any_of(std::begin(kConvs), std::end(kConvs), [&](const char* k) { return c == k; });
}

const gen::Field* schema_field(const std::string& registry, const std::string& field) {
    for (const gen::Field& f : gen::kFields)
        if (registry == f.registry && field == f.path) return &f;
    return nullptr;
}

bool is_hex_guid(const std::string& g) {
    if (g.size() != 36) return false;
    for (size_t i = 0; i < g.size(); ++i) {
        const bool dash = i == 8 || i == 13 || i == 18 || i == 23;
        if (dash ? g[i] != '-' : !std::isxdigit(static_cast<unsigned char>(g[i])) || std::isupper(static_cast<unsigned char>(g[i]))) return false;
    }
    return true;
}

bool id_char(char c) { return (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.'; }

std::string str(const json& j, const char* k) { return j.contains(k) && j[k].is_string() ? j[k].get<std::string>() : std::string(); }

}  // namespace

bool is_kiln_id(const std::string& id) {
    if (id.empty() || id.size() > 256) return false;
    const auto colon = id.find(':');
    if (colon == std::string::npos || colon == 0 || !(std::islower(static_cast<unsigned char>(id[0])) || std::isdigit(static_cast<unsigned char>(id[0]))))
        return false;
    for (size_t i = 0; i < colon; ++i)
        if (!id_char(id[i])) return false;
    size_t end = id.size();
    const auto tilde = id.find('~', colon + 1);
    if (tilde != std::string::npos) {
        const size_t n = id.size() - tilde - 1;
        if (n < 8 || n > 32) return false;
        for (size_t i = tilde + 1; i < id.size(); ++i)
            if (!std::isxdigit(static_cast<unsigned char>(id[i])) || std::isupper(static_cast<unsigned char>(id[i]))) return false;
        end = tilde;
    }
    if (end == colon + 1) return false;
    for (size_t i = colon + 1; i < end; ++i)
        if (!id_char(id[i])) return false;
    return true;
}

bool Aliases::is_removed(const std::string& registry, const std::string& id, std::string* build) const {
    const auto it = removed.find(registry + "|" + id);
    if (it == removed.end()) return false;
    if (build) *build = it->second;
    return true;
}

bool load_common(const json& doc, RegistryMap* out, std::vector<std::string>* errs) {
    *out = RegistryMap{};
    if (!doc.is_object() || doc.value("regmap", 0) != 1) return errs->push_back("a regmap file has \"regmap\": 1"), false;
    out->registry = str(doc, "registry");
    if (out->registry.empty()) return errs->push_back("a regmap file names its \"registry\""), false;
    const std::string& r = out->registry;
    const json e = doc.value("entries", json::object());
    static const char* const ekeys[] = {"kind", "resourceType", "array", "resourceGuid", "name", "guid", "id", "unnamed", "nameFrom", "key", "keyMember", "withComponent",
                                        "sources"};
    for (const auto& [k, v] : e.items())
        if (std::none_of(std::begin(ekeys), std::end(ekeys), [&](const char* x) { return k == x; })) errs->push_back(r + ": entries: unknown member \"" + k + "\"");
    const std::string kind = str(e, "kind");
    out->entries.kind = kind == "array"    ? EntryKind::Array
                        : kind == "single" ? EntryKind::Single
                        : kind == "hooks"  ? EntryKind::Hooks
                        : kind == "arrays" ? EntryKind::Arrays
                                           : EntryKind::Resource;
    if (kind != "resource" && kind != "array" && kind != "single" && kind != "hooks" && kind != "arrays")
        errs->push_back(r + ": entries.kind is resource, array, single, hooks or arrays");
    out->entries.resource_type = str(e, "resourceType");
    const bool hooks = out->entries.kind == EntryKind::Hooks;
    if (out->entries.kind == EntryKind::Arrays) {
        // the sources say where the entries are; the kind has no root type of its own
        if (!out->entries.resource_type.empty()) errs->push_back(r + ": entries of kind arrays name their types in sources, not resourceType");
        const json sources = e.value("sources", json::array());
        if (!sources.is_array() || sources.empty()) errs->push_back(r + ": entries.sources lists the game sources of kind arrays");
        else
            for (const json& sj : sources) {
                static const char* const skeys[] = {"resourceType", "array", "id", "unnamed", "usedBy"};
                EntrySource s;
                s.kind = EntryKind::Array;
                if (!sj.is_object()) { errs->push_back(r + ": entries.sources holds objects"); continue; }
                for (const auto& [k, v] : sj.items())
                    if (std::none_of(std::begin(skeys), std::end(skeys), [&](const char* x) { return k == x; }))
                        errs->push_back(r + ": entries.sources: unknown member \"" + k + "\"");
                s.resource_type = str(sj, "resourceType"), s.array = str(sj, "array"), s.id = str(sj, "id"), s.unnamed = str(sj, "unnamed");
                if (s.resource_type.empty() || s.array.empty()) errs->push_back(r + ": each entries.sources item names resourceType and array");
                // an element without an id member is named by what uses it (usedBy), never by its index: an index moves
                // when a game update inserts an element, and an edit naming it would then reach another table
                if (sj.contains("usedBy")) {
                    const json& u = sj["usedBy"];
                    static const char* const ukeys[] = {"array", "index", "id"};
                    if (!u.is_object()) errs->push_back(r + ": entries.sources[].usedBy is an object");
                    else
                        for (const auto& [k, v] : u.items())
                            if (std::none_of(std::begin(ukeys), std::end(ukeys), [&](const char* x) { return k == x; }))
                                errs->push_back(r + ": entries.sources[].usedBy: unknown member \"" + k + "\"");
                    if (u.is_object()) s.used_by_array = str(u, "array"), s.used_by_index = str(u, "index"), s.used_by_id = str(u, "id");
                    if (s.used_by_array.empty() || s.used_by_index.empty() || s.used_by_id.empty())
                        errs->push_back(r + ": entries.sources[].usedBy names the array that uses the elements, its index member and its id member");
                }
                if (s.id.empty() == s.used_by_array.empty())
                    errs->push_back(r + ": each entries.sources item names its elements by their id member or by usedBy (one of them)");
                if (s.unnamed.find("%08x") == std::string::npos || s.unnamed.find("%u") != std::string::npos)
                    errs->push_back(r + ": a source's unnamed pattern takes its key (\"%08x\": the id, or usedBy's id) and may take its resource's GUID "
                                        "(\"%.8s\"); an index is no stable name");
                out->entries.sources.push_back(std::move(s));
            }
    } else {
        if (e.contains("sources")) errs->push_back(r + ": entries.sources is for kind arrays");
        if (hooks && !out->entries.resource_type.empty()) errs->push_back(r + ": entries of kind hooks have no resourceType (their values are Kiln's hooks)");
        if (hooks && out->entries.unnamed.empty() && str(e, "unnamed").empty()) errs->push_back(r + ": entries of kind hooks name their one entry (\"unnamed\")");
        if (!hooks && out->entries.resource_type.empty()) errs->push_back(r + ": entries.resourceType names the game's root type");
    }
    out->entries.array = str(e, "array"), out->entries.resource_guid = str(e, "resourceGuid");
    out->entries.name = str(e, "name"), out->entries.guid = str(e, "guid"), out->entries.id = str(e, "id");
    out->entries.unnamed = str(e, "unnamed"), out->entries.name_from = str(e, "nameFrom"), out->entries.key = str(e, "key");
    out->entries.key_member = str(e, "keyMember");
    if (!out->entries.key_member.empty() && out->entries.key != "items") errs->push_back(r + ": entries.keyMember goes with \"key\": \"items\"");
    if (out->entries.kind == EntryKind::Array && out->entries.array.empty()) errs->push_back(r + ": entries.array names the member that holds the entries");
    if (e.contains("withComponent")) {  // template roots only: the entries are the templates holding one of these components
        const json& wc = e["withComponent"];
        if (!wc.is_array() || wc.empty() || out->entries.kind != EntryKind::Resource) errs->push_back(r + ": entries.withComponent is a list of component types (resource entries)");
        else
            for (const json& c : wc) {
                if (c.is_string() && !c.get<std::string>().empty()) out->entries.with_component.push_back(c.get<std::string>());
                else errs->push_back(r + ": entries.withComponent names component types");
            }
    }
    if (out->entries.kind == EntryKind::Single && !is_hex_guid(out->entries.resource_guid)) errs->push_back(r + ": entries.resourceGuid is the one resource's GUID");
    static const char* const rkeys[] = {"paths", "conv", "values", "formula", "config", "read", "backend", "clientNeeds", "state", "reason", "evidence", "resize"};
    const json rows = doc.value("rows", json::object());
    for (const auto& [field, j] : rows.items()) {
        const std::string q = r + "." + field;
        if (!j.is_object()) { errs->push_back(q + ": a row is an object"); continue; }
        bool ok = true;
        for (const auto& [k, v] : j.items())
            if (std::none_of(std::begin(rkeys), std::end(rkeys), [&](const char* x) { return k == x; }))
                errs->push_back(q + ": unknown member \"" + k + "\""), ok = false;
        if (!schema_field(r, field)) errs->push_back(q + ": the schema has no such field"), ok = false;
        Row row;
        row.field = field;
        const std::string state = j.value("state", std::string("mapped"));
        row.state = state == "unproven" ? RowState::Unproven : state == "unmapped" ? RowState::Unmapped : RowState::Mapped;
        if (state != "mapped" && state != "unproven" && state != "unmapped") errs->push_back(q + ": state is mapped, unproven or unmapped"), ok = false;
        row.reason = str(j, "reason"), row.evidence = str(j, "evidence");
        if (row.state != RowState::Mapped && row.reason.empty()) errs->push_back(q + ": an " + state + " row says why (\"reason\")"), ok = false;
        if (row.state == RowState::Unmapped) {
            if (ok) out->rows[field] = row;
            continue;
        }
        if (!j.contains("paths") || !j["paths"].is_array() || j["paths"].empty()) errs->push_back(q + ": paths names the game's members"), ok = false;
        else
            for (const json& p : j["paths"]) {
                if (!p.is_string() || p.get<std::string>().empty()) { errs->push_back(q + ": a path is a member name"), ok = false; continue; }
                row.paths.push_back(p.get<std::string>());
            }
        row.conv = str(j, "conv");
        if (!is_conv(row.conv)) errs->push_back(q + ": conv \"" + row.conv + "\" is not one of regmap/README.md's conversions"), ok = false;
        if (!read_of(str(j, "read"), &row.read)) errs->push_back(q + ": read is now, worldLoad, restart or unknown"), ok = false;
        const std::string be = str(j, "backend");
        row.backend = be == "live" ? Backend::Live : be == "boot" ? Backend::Boot : be == "hook" ? Backend::Hook : Backend::None;
        if (be != "live" && be != "boot" && be != "none" && be != "hook") errs->push_back(q + ": backend is live, boot, hook or none"), ok = false;
        // a hooks registry's values are written by Kiln's hooks only; a resource's never are
        if ((row.backend == Backend::Hook) != (hooks && be != "none")) errs->push_back(q + ": the hook back-end serves the rows of a hooks registry, and only it does"), ok = false;
        if (j.contains("clientNeeds") && !j["clientNeeds"].is_boolean()) errs->push_back(q + ": clientNeeds is true or false"), ok = false;
        row.client_needs = j.value("clientNeeds", false);
        if (j.contains("resize") && !j["resize"].is_boolean()) errs->push_back(q + ": resize is true or false"), ok = false;
        row.resize = j.contains("resize") && j["resize"].is_boolean() && j["resize"].get<bool>();
        if (row.resize && (row.conv != "list" || row.backend != Backend::Boot))
            errs->push_back(q + ": resize is for a list row of the boot back-end"), ok = false;
        if (j.contains("values")) {
            if (!j["values"].is_object()) errs->push_back(q + ": values maps Kiln names to the game's"), ok = false;
            else
                for (const auto& [k, v] : j["values"].items()) row.values[k] = v.is_string() ? v.get<std::string>() : std::string();
        }
        if ((row.conv == "enum" || row.conv == "flags" || row.conv == "optionalEnum") && row.values.empty())
            errs->push_back(q + ": an " + row.conv + " row maps its Kiln names (\"values\")"), ok = false;
        row.formula = str(j, "formula"), row.config = str(j, "config");
        if (row.conv == "formula" && row.formula.empty()) errs->push_back(q + ": a formula row names its formula"), ok = false;
        if (row.conv == "impactConfig" && row.config.empty()) errs->push_back(q + ": an impactConfig row names its config"), ok = false;
        // one named bit of a bitmask, a bool (creatures.tameable); read-only until Kiln has a writer of one bit
        if (row.conv == "flag" && (row.values.size() != 1 || row.paths.size() != 1 || row.backend != Backend::None))
            errs->push_back(q + ": a flag row is read-only and maps one Kiln name to one bit of its bitmask (\"values\")"), ok = false;
        if (row.conv == "component" && (row.values.size() != row.paths.size() || row.backend != Backend::None))
            errs->push_back(q + ": a component row is read-only and maps one Kiln name to each of its component types (\"values\")"), ok = false;
        if (ok) out->rows[field] = std::move(row);
    }
    return true;
}

bool load_record(const json& doc, BuildRecord* out, std::vector<std::string>* errs) {
    *out = BuildRecord{};
    const size_t before = errs->size();
    if (!doc.is_object() || doc.value("regmap", 0) != 1) return errs->push_back("a regmap record has \"regmap\": 1"), false;
    out->side = str(doc, "side");
    if (out->side != "server" && out->side != "client") errs->push_back("record: side is server or client");
    out->exe_sha256 = str(doc, "exeSha256");
    if (out->exe_sha256.size() != 64 || !std::all_of(out->exe_sha256.begin(), out->exe_sha256.end(), [](char c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'); }))
        errs->push_back("record: exeSha256 is the exe's SHA-256 in 64 lower-case hex digits");
    out->game_build = str(doc, "gameBuild"), out->steam_build = str(doc, "steamBuild"), out->proof = str(doc, "proof");
    out->regmap_digest = str(doc, "regmapDigest");
    if (doc.contains("regmapDigest") && (out->regmap_digest.size() != 64 ||
                                         !std::all_of(out->regmap_digest.begin(), out->regmap_digest.end(), [](char c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'); })))
        errs->push_back("record: regmapDigest is the sha256 of the regmap/common it was proven on, 64 lower-case hex digits");
    if (!doc.contains("verified") || !doc["verified"].is_boolean()) errs->push_back("record: verified is true or false");
    else out->verified = doc["verified"].get<bool>();
    if (doc.contains("markedLocally") && !doc["markedLocally"].is_boolean()) errs->push_back("record: markedLocally is true or false");
    else out->marked_locally = doc.value("markedLocally", false);
    const json layouts = doc.value("layouts", json::object());
    for (const auto& [k, v] : layouts.items()) {
        const std::string s = v.is_string() ? v.get<std::string>() : std::string();
        char* end = nullptr;
        const unsigned long h = s.rfind("0x", 0) == 0 ? std::strtoul(s.c_str() + 2, &end, 16) : 0;
        if (!end || *end || s.size() != 10) errs->push_back("record: layouts." + k + " is a hash \"0x<8 hex>\"");
        else out->layouts[k] = static_cast<uint32_t>(h);
    }
    const json rows = doc.value("rows", json::object());
    for (const auto& [k, v] : rows.items()) {
        const std::string s = v.is_string() ? v.get<std::string>() : std::string();
        if (s == "ok") out->rows[k] = BuildRow::Ok;
        else if (s == "changed") out->rows[k] = BuildRow::Changed;
        else if (s == "unmapped") out->rows[k] = BuildRow::Unmapped;
        else if (s == "unproven") out->rows[k] = BuildRow::Unproven;
        else errs->push_back("record: rows." + k + " is ok, changed, unproven or unmapped");
    }
    const json reasons = doc.value("reasons", json::object());
    if (!reasons.is_object()) errs->push_back("record: reasons is an object of \"<registry>.<field>\": text");
    else
        for (const auto& [k, v] : reasons.items()) {
            if (v.is_string()) {
                std::string reason = v.get<std::string>();
                if (reason.size() > 400) {
                    size_t end = 400;
                    while (end && (static_cast<unsigned char>(reason[end]) & 0xc0) == 0x80) --end;
                    reason.resize(end); // never split a UTF-8 code point in the refusal/state JSON
                }
                out->reasons[k] = std::move(reason);
            } else errs->push_back("record: reasons." + k + " is text");
        }
    const json a = doc.value("aliases", json::object());
    for (const char* list : {"renames", "suffixes"}) {
        const json items = a.value(list, json::array());
        for (const json& x : items) {
            const std::string reg = str(x, "registry"), guid = str(x, "guid"), id = str(x, "id");
            if (reg.empty() || !is_hex_guid(guid) || !is_kiln_id(id) || id.rfind("game:", 0) != 0)
                errs->push_back(std::string("record: aliases.") + list + " rows are {registry, guid (lower case), id (game:...)}");
            else out->aliases.pinned[reg + "|" + guid] = id;
        }
    }
    const json removals = a.value("removals", json::array());
    for (const json& x : removals) {
        const std::string reg = str(x, "registry"), id = str(x, "id"), b = str(x, "removedIn");
        if (reg.empty() || !is_kiln_id(id) || b.empty()) errs->push_back("record: aliases.removals rows are {registry, id, removedIn}");
        else out->aliases.removed[reg + "|" + id] = b;
    }
    return errs->size() == before;
}

namespace {
bool read_json(const std::filesystem::path& p, json* out, std::vector<std::string>* errs) {
    std::ifstream f(p, std::ios::binary);
    if (!f) return errs->push_back(p.filename().string() + ": cannot be read"), false;
    std::stringstream ss;
    ss << f.rdbuf();
    *out = json::parse(ss.str(), nullptr, false);
    if (out->is_discarded()) return errs->push_back(p.filename().string() + ": not JSON"), false;
    return true;
}
}  // namespace

bool load_dir(const std::string& dir, const std::string& side, const std::string& exe_sha256, RegMap* out, std::vector<std::string>* errs) {
    *out = RegMap{};
    out->side = side, out->exe_sha256 = exe_sha256;
    namespace fs = std::filesystem;
    std::error_code ec;
    // dir is UTF-8 (kiln.content's paths): a path made from UTF-8 code units, so a game folder with letters outside the
    // ANSI code page opens on Windows too (a narrow fs::path reads the bytes in that code page there)
    const fs::path root(std::u8string(dir.begin(), dir.end()));
    std::vector<fs::path> files;
    for (const auto& e : fs::directory_iterator(root / "common", ec))
        if (e.is_regular_file() && e.path().extension() == ".json") files.push_back(e.path());
    if (ec) return errs->push_back(dir + "/common: " + ec.message()), false;
    std::sort(files.begin(), files.end());
    // the deployed mapping's digest (kilnctl regproof's regmapDigest): which mapping a record's proof holds for
    std::string digest_text;
    for (const fs::path& p : files) {
        std::ifstream f(p, std::ios::binary);
        std::stringstream ss;
        ss << f.rdbuf();
        const std::u8string name = p.filename().u8string();
        digest_text += std::string(name.begin(), name.end()) + "\n" + kiln::boot_gd::hex_of(kiln::boot_gd::sha256(ss.str())) + "\n";
    }
    out->common_digest = kiln::boot_gd::hex_of(kiln::boot_gd::sha256(digest_text));
    for (const fs::path& p : files) {
        const std::u8string file_name = p.filename().u8string();
        const std::string file(file_name.begin(), file_name.end());
        json doc;
        if (!read_json(p, &doc, errs)) {
            out->stray_common.push_back(file);
            continue;
        }
        RegistryMap m;
        if (!load_common(doc, &m, errs)) {
            out->stray_common.push_back(file);
            continue;
        }
        if (file != m.registry + ".json") out->stray_common.push_back(file);
        for (const std::string& broken : check_routing(m)) {
            errs->push_back(broken);
            const auto colon = broken.find(": ");
            const std::string field = broken.substr(m.registry.size() + 1, colon - m.registry.size() - 1);
            const auto it = m.rows.find(field);
            if (it != m.rows.end() && it->second.state == RowState::Mapped) {
                it->second.state = RowState::Unproven;
                it->second.reason = "Kiln's mapping breaks its own routing rule (" + broken.substr(colon + 2) + ")";
            }
        }
        if (p.stem().string() != m.registry) errs->push_back(p.filename().string() + ": the file of registry \"" + m.registry + "\" is " + m.registry + ".json");
        out->registries[m.registry] = std::move(m);
    }
    const fs::path rec = root / side / (exe_sha256 + ".json");
    if (fs::exists(rec, ec)) {
        json doc;
        if (read_json(rec, &doc, errs) && load_record(doc, &out->build, errs)) {
            out->has_build = true;
            if (out->build.side != side || out->build.exe_sha256 != exe_sha256) {
                errs->push_back(rec.filename().string() + ": the record is of another side or exe");
                out->has_build = false;
            }
        }
    }
    return true;
}

Mapping RegMap::mapping(const std::string& registry, const std::string& field, std::string* reason) const {
    static const char* const kNotApplied = "; this edit was not applied";
    const std::string q = registry + "." + field;
    const std::string build_name = has_build && !build.game_build.empty() ? "game build " + build.game_build
                                                                          : "this game build (" + side + " exe " + exe_sha256.substr(0, 8) + ")";
    const auto reg = registries.find(registry);
    const Row* row = nullptr;
    if (reg != registries.end()) {
        const auto it = reg->second.rows.find(field);
        if (it != reg->second.rows.end()) row = &it->second;
    }
    if (!row || row->state == RowState::Unmapped) {
        *reason = "Unmapped: " + q + " is not mapped for " + build_name + " yet (a Kiln update is needed" +
                  (row && !row->reason.empty() ? ": " + row->reason : std::string()) + ")" + kNotApplied;
        return Mapping::Unmapped;
    }
    if (!has_build || !build.verified) {
        *reason = "Unproven: " + q + " is not proven for " + build_name + " (no verified regmap record for it yet; a Kiln update is needed)" + kNotApplied;
        return Mapping::Unproven;
    }
    if (build.regmap_digest != common_digest) {
        // the record's proof holds for the mapping it was proven on, not for an edited one (review2 S1); the advice fits
        // why they differ (review3 S4)
        std::string why = "regmap/common differs from the one this build's record was proven on";
        if (!stray_common.empty()) {
            why += ", because it holds files Kiln did not install: ";
            for (size_t i = 0; i < stray_common.size() && i < 3; ++i) why += (i ? ", " : "") + stray_common[i];
            if (stray_common.size() > 3) why += " and " + std::to_string(stray_common.size() - 3) + " more";
            why += "; remove them, or repair or reinstall Kiln";
        } else if (build.marked_locally) {
            why = "regmap/common differs from the one this server's own record was proven on; run kilnctl regproof for this server again";
        } else {
            why += "; a Kiln update is needed";
        }
        *reason = "Unproven: " + q + " is not proven for " + build_name + " (" + why + ")" + kNotApplied;
        return Mapping::Unproven;
    }
    const auto b = build.rows.find(q);
    if (b != build.rows.end() && b->second == BuildRow::Unproven) {
        const auto r = build.reasons.find(q);
        *reason = "Unproven: " + q + " is not proven for " + build_name + " (" +
                  (r != build.reasons.end() && !r->second.empty() ? r->second : std::string("no lab evidence on this build")) + ")" + kNotApplied;
        return Mapping::Unproven;
    }
    if (b == build.rows.end() || b->second != BuildRow::Ok) {
        *reason = "Unmapped: " + q + " is not mapped for " + build_name + " yet (a Kiln update is needed)" + kNotApplied;
        return Mapping::Unmapped;
    }
    if (row->state == RowState::Unproven) {
        *reason = "Unproven: " + q + " is not proven for " + build_name + " (" + row->reason + ")" + kNotApplied;
        return Mapping::Unproven;
    }
    reason->clear();
    return Mapping::Mapped;
}

std::vector<std::string> check_routing(const RegistryMap& m) {
    std::vector<std::string> out;
    for (const auto& [field, row] : m.rows) {
        const std::string q = m.registry + "." + field;
        const gen::Field* f = schema_field(m.registry, field);
        if (!f) { out.push_back(q + ": the schema has no such field"); continue; }
        if (row.state == RowState::Unmapped) continue;
        const int applies = f->applies == gen::Applies::Now ? 0 : f->applies == gen::Applies::WorldLoad ? 1 : 2;
        if (row.read == Read::Unknown) {
            if (row.state != RowState::Unproven) out.push_back(q + ": the game's read time is not traced: the row is unproven until it is");
            if (applies == 0) out.push_back(q + ": the game's read time is not traced: the schema ships the cautious applies (worldLoad or restart), not now");
        } else {
            const int read = row.read == Read::Now ? 0 : row.read == Read::WorldLoad ? 1 : 2;
            if (read > applies)
                out.push_back(q + ": the schema promises it applies " + (applies == 0 ? "now" : "at world load") + " but the game reads it at " +
                              read_name(row.read) + " only");
        }
        if (f->read_only) {
            if (row.backend != Backend::None) out.push_back(q + ": a read-only field has no back-end (backend none)");
            continue;
        }
        if (row.backend == Backend::Boot && applies != 2) out.push_back(q + ": a boot back-end write acts only after a restart, the schema says otherwise");
        if (row.backend == Backend::Hook && applies != 2) out.push_back(q + ": a hook takes its value at start, the schema says otherwise");
        if (row.backend == Backend::Live && applies == 2) out.push_back(q + ": a restart field goes through the boot back-end (before the game reads it)");
        if (row.backend == Backend::None) out.push_back(q + ": a writable field needs a back-end (live or boot)");
    }
    return out;
}

uint32_t element_num(const uint8_t guid[16], uint32_t key) { return kc::registry::element_num(guid, key); }

std::string element_name(const EntrySource& source, uint32_t key, const std::string& guid_text) {
    std::string out;
    const std::string& p = source.unnamed;
    for (size_t i = 0; i < p.size(); ++i) {
        char buf[16];
        if (p.compare(i, 4, "%08x") == 0) {
            std::snprintf(buf, sizeof buf, "%08x", static_cast<unsigned>(key));
            out += buf, i += 3;
        } else if (p.compare(i, 4, "%.8s") == 0) {
            out += lower(guid_text).substr(0, 8), i += 3;
        } else {
            out += p[i];
        }
    }
    return out;
}

std::vector<std::string> assign_ids(const std::string& registry, const EntrySource& src, const Aliases& aliases,
                                    const std::vector<GameEntry>& entries, const std::map<uint32_t, std::string>* by_item_id,
                                    std::vector<std::string>* notes, const std::map<std::string, std::string>* by_key_guid) {
    std::vector<std::string> ids(entries.size());
    std::set<std::string> taken;
    const std::string prefix = registry + "|";
    for (const auto& [key, build] : aliases.removed)  // a removed entry's id is never given again
        if (key.compare(0, prefix.size(), prefix) == 0) taken.insert(key.substr(prefix.size()));
    // pinned ids hold first, whatever the order
    for (size_t i = 0; i < entries.size(); ++i) {
        const auto it = aliases.pinned.find(prefix + lower(entries[i].guid));
        if (it != aliases.pinned.end()) ids[i] = it->second, taken.insert(it->second);
    }
    const auto suffixed = [&](const std::string& base, std::string hex) {
        std::string id = base;
        if (hex.empty()) hex = "00000000";
        for (size_t n = 8; taken.count(id) && n <= std::max<size_t>(hex.size(), 8) + 4; n += 4)
            id = base + "~" + hex.substr(0, std::min(n, hex.size()));
        return id;
    };
    for (size_t i = 0; i < entries.size(); ++i) {
        if (!ids[i].empty()) continue;
        const GameEntry& e = entries[i];
        std::string hex;
        for (char c : lower(e.guid))
            if (c != '-') hex += c;
        if (hex.empty()) {
            char buf[16];
            std::snprintf(buf, sizeof buf, "%08x", static_cast<unsigned>(e.id));
            hex = buf;
        }
        if (!src.key.empty()) {  // the keyed registry's id: by GUID (creatures: their template), else by 32-bit id (loot: its item)
            const std::string* keyed = nullptr;
            if (by_key_guid && !e.guid.empty())
                if (const auto it = by_key_guid->find(lower(e.guid)); it != by_key_guid->end()) keyed = &it->second;
            const uint32_t item = src.key_member.empty() ? e.id : e.key;  // loot: its own id; stations: the item that builds it
            if (!keyed && by_item_id && (src.key_member.empty() || item))   // no item: the name rules
                if (const auto it = by_item_id->find(item); it != by_item_id->end()) keyed = &it->second;
            if (keyed) {
                std::string id = *keyed;
                if (taken.count(id)) {
                    if (notes) notes->push_back(registry + ": a second entry for " + id + " (" + hex + ") gets a suffix");
                    id = suffixed(*keyed, hex);
                }
                taken.insert(id);
                ids[i] = id;
                continue;
            }
        }
        std::string raw;
        if (!src.name_from.empty()) {
            std::vector<std::string> by;
            for (const std::string& t : e.named_by) by.push_back(lower(t));
            std::sort(by.begin(), by.end());
            if (!by.empty()) raw = by.front();
        } else {
            raw = lower(e.name);
        }
        std::string name;
        for (char c : raw) name += id_char(c) ? c : '_';
        if (name != raw && notes) notes->push_back(registry + ": \"" + raw + "\" is outside the id grammar: " + name + " (" + hex + ", pinned by the record)");
        if (name.empty()) {
            char buf[64] = {};
            if (src.unnamed.find("%08x") != std::string::npos) std::snprintf(buf, sizeof buf, src.unnamed.c_str(), static_cast<unsigned>(e.id));
            else if (src.unnamed.find("%.8s") != std::string::npos) std::snprintf(buf, sizeof buf, src.unnamed.c_str(), hex.c_str());
            else if (!src.unnamed.empty() && src.unnamed.find('%') == std::string::npos) std::snprintf(buf, sizeof buf, "%s", src.unnamed.c_str());
            else std::snprintf(buf, sizeof buf, "entry_%08x", static_cast<unsigned>(e.id));
            name = buf;
        }
        std::string id = suffixed("game:" + name, hex);
        if (!is_kiln_id(id)) {
            if (notes) notes->push_back(registry + ": \"" + id + "\" is not a Kiln id: game:entry_" + hex.substr(0, 8) + " instead");
            id = suffixed("game:entry_" + hex.substr(0, 8), hex);
        }
        taken.insert(id);
        ids[i] = id;
    }
    return ids;
}

}  // namespace kc::registry::map
