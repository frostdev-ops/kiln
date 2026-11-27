// SPDX-License-Identifier: MIT
// kiln.content registry stage: effect replay of archive-writer packages (replay.h).
#include "replay.h"

#include <algorithm>
#include <cctype>
#include <tuple>

#include "../../../core/src/services/boot_gamedata.h"

namespace kc::registry::replay {

namespace {

std::string lower(std::string s) {
    for (char& c : s) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return s;
}

bool who_ok(const std::string& who) {
    const size_t colon = who.find(':');
    if (colon == std::string::npos) return false;
    const std::string kind = who.substr(0, colon), id = who.substr(colon + 1);
    if (kind != "shroudforge" && kind != "eml") return false;
    if (id.empty() || id.size() > 80) return false;
    return std::all_of(id.begin(), id.end(), [](char c) { return std::isalnum(static_cast<unsigned char>(c)) || c == '.' || c == '_' || c == '-'; });
}

std::string str(const json& j, const char* k) { return j.contains(k) && j[k].is_string() ? j[k].get<std::string>() : std::string(); }

}  // namespace

OptIn parse_opt_in(const std::string& text) {
    OptIn out;
    const json doc = json::parse(text, nullptr, false);
    if (doc.is_discarded() || !doc.is_object() || doc.value("format", 0) != kFormatOptIn || !doc.contains("packages") || !doc["packages"].is_object()) {
        out.error = std::string(kOptInFile) + " is not {\"format\": 1, \"packages\": {\"shroudforge:<id>\": true, ...}}: no package is opted in to effect replay";
        return out;
    }
    for (auto it = doc["packages"].begin(); it != doc["packages"].end(); ++it) {
        if (!who_ok(it.key()) || !it.value().is_boolean()) {
            out.packages.clear();
            out.error = std::string(kOptInFile) + ": \"" + it.key().substr(0, 100) +
                        "\" is not a package (shroudforge:<id> or eml:<id>) set to true or false: no package is opted in to effect replay";
            return out;
        }
        out.packages[it.key()] = it.value().get<bool>();
    }
    return out;
}

std::string mod_id_of(const std::string& who) {
    const size_t colon = who.find(':');
    const std::string kind = colon == std::string::npos ? std::string() : who.substr(0, colon);
    std::string id = "replay." + std::string(kind == "eml" ? "eml." : "sf.") + lower(colon == std::string::npos ? who : who.substr(colon + 1));
    if (id.size() > 64) id = id.substr(0, 55) + "." + kiln::boot_gd::hex_of(kiln::boot_gd::sha256(who)).substr(0, 8);
    return id;
}

std::string file_of(const std::string& who) { return "replay/" + mod_id_of(who) + ".json"; }

json description_json(const Description& d) {
    json fields = json::array();
    for (const Description::Field& f : d.fields) fields.push_back({{"registry", f.registry}, {"id", f.id}, {"field", f.field}, {"value", f.value}});
    return {{"format", kFormat},
            {"who", d.who},
            {"id", d.id},
            {"kind", d.kind},
            {"version", d.version.empty() ? json(nullptr) : json(d.version)},
            {"contentSha256", d.content.empty() ? json(nullptr) : json(d.content)},
            {"side", d.side},
            {"exe", d.exe},
            {"kiln", d.kiln},
            {"recorded", d.recorded},
            {"writer", d.writer},
            {"fields", std::move(fields)},
            {"residue", d.residue}};
}

bool parse_description(const std::string& text, Description* out, std::string* why) {
    const json doc = json::parse(text, nullptr, false);
    if (doc.is_discarded() || !doc.is_object() || doc.value("format", 0) != kFormat) return *why = "the recorded effect is not one this Kiln reads", false;
    Description d;
    d.who = str(doc, "who"), d.id = str(doc, "id"), d.kind = str(doc, "kind"), d.version = str(doc, "version"), d.content = str(doc, "contentSha256");
    d.side = str(doc, "side"), d.exe = str(doc, "exe"), d.kiln = str(doc, "kiln"), d.recorded = str(doc, "recorded"), d.writer = str(doc, "writer");
    if (!who_ok(d.who) || d.exe.empty() || d.side.empty()) return *why = "the recorded effect names no package, game build or side", false;
    if (!doc.contains("fields") || !doc["fields"].is_array() || !doc.contains("residue") || !doc["residue"].is_array())
        return *why = "the recorded effect has no fields or residue list", false;
    for (const json& f : doc["fields"]) {
        if (!f.is_object() || str(f, "registry").empty() || str(f, "id").empty() || str(f, "field").empty() || !f.contains("value"))
            return *why = "a recorded field is not {registry, id, field, value}", false;
        d.fields.push_back({str(f, "registry"), str(f, "id"), str(f, "field"), f["value"]});
    }
    for (const json& r : doc["residue"]) {
        if (!r.is_string()) return *why = "a residue line is not text", false;
        d.residue.push_back(r.get<std::string>());
    }
    *out = std::move(d);
    return true;
}

Description describe(const ArchiveJournal::Package& p, const std::string& who, const BaseModel& base, const std::vector<std::string>& unmatched,
                     bool truncated) {
    Description d;
    d.who = who, d.id = p.id, d.kind = p.kind, d.version = p.version, d.content = p.content;
    d.residue = unmatched;
    if (truncated) d.residue.push_back("the change journal left some of this package's record out (it is truncated)");
    for (const auto& [reg, entries] : base.registries)
        for (const BaseModel::Entry& e : entries) {
            if (std::find(e.archive.begin(), e.archive.end(), who) == e.archive.end()) continue;
            const auto own = e.archive_fields.find(who);
            if (own == e.archive_fields.end()) {
                d.residue.push_back(reg + "[" + e.id + "]: named on the whole entry (its record does not say which fields)");
                continue;
            }
            for (const std::string& field : own->second) {
                std::vector<std::string> others;
                for (const std::string& o : e.archive) {
                    if (o == who) continue;
                    const auto of = e.archive_fields.find(o);
                    if (of == e.archive_fields.end() || of->second.count(field)) others.push_back(archive_writer_label(o));
                }
                const std::string place = reg + "[" + e.id + "]." + field;
                if (!others.empty()) {
                    std::string list;
                    for (const std::string& o : others) list += (list.empty() ? "" : ", ") + o;
                    d.residue.push_back(place + ": also changed by " + list + " (its value is not this package's alone)");
                    continue;
                }
                const auto v = e.fields.find(field);
                if (v == e.fields.end() || v->second.kind == Value::None) {
                    d.residue.push_back(place + ": its value was not read");
                    continue;
                }
                d.fields.push_back({reg, e.id, field, v->second.to_json()});
            }
        }
    std::sort(d.fields.begin(), d.fields.end(), [](const Description::Field& a, const Description::Field& b) {
        return std::tie(a.registry, a.id, a.field) < std::tie(b.registry, b.id, b.field);
    });
    return d;
}

bool replay_batch(const Description& d, const Schema& schema, Batch* out, std::string* why) {
    json edits = json::array();
    for (const Description::Field& f : d.fields)
        edits.push_back({{"registry", f.registry}, {"select", {{"id", f.id}}}, {"field", f.field}, {"op", f.value}});
    const json doc = {{"ir", 1}, {"mod", mod_id_of(d.who)}, {"stage", "alter"}, {"source", file_of(d.who)}, {"edits", std::move(edits)}};
    Batch b;
    std::vector<Refusal> refused;
    std::string err;
    if (!parse_batch(doc, schema, &b, &refused, &err)) return *why = err, false;
    if (!refused.empty()) {
        const Refusal& r = refused.front();
        *why = r.reason + (refused.size() > 1 ? " (and " + std::to_string(refused.size() - 1) + " more)" : std::string());
        return false;
    }
    for (Edit& e : b.edits) e.stand_in = true;
    *out = std::move(b);
    return true;
}

json outcomes_json(const std::vector<Outcome>& rows) {
    json out = json::array();
    for (const Outcome& o : rows) {
        json r = {{"who", o.who}, {"state", o.state}, {"text", o.text}, {"fields", o.fields}};
        if (!o.recorded_on.empty()) r["recordedOn"] = o.recorded_on;
        out.push_back(std::move(r));
    }
    return out;
}

}  // namespace kc::registry::replay
