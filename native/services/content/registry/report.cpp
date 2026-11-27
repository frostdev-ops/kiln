// SPDX-License-Identifier: MIT
// kiln.content registry stage: registry.json (report.h).
#include "report.h"

#include <algorithm>
#include <set>

namespace kc::registry {

namespace {

const char* applies_name(Applies a) { return a == Applies::Now ? "now" : a == Applies::WorldLoad ? "worldLoad" : "restart"; }

json list_json(const std::vector<Value>& list, size_t cap) {
    json values = json::array();
    for (size_t i = 0; i < std::min(list.size(), cap); ++i) values.push_back(list[i].to_json());
    return values;
}

void write_value(json& row, const char* key, const Value& value, const char* omitted_key, size_t cap) {
    if (value.kind == Value::List) {
        row[key] = list_json(value.list, cap);
        if (value.list.size() > cap) row[omitted_key] = value.list.size() - cap;
    } else {
        row[key] = value.to_json();
    }
}

json writer_json(const ChainRow& r, size_t list_cap) {
    json w = {{"kind", writer_kind_name(r.kind)}};
    if (!r.mod.empty()) w["mod"] = r.mod;
    if (r.who != r.mod) w["who"] = r.who;
    if (r.kind == WriterKind::Registry) {
        w["stage"] = stage_name(r.stage);
        if (!r.source.empty()) w["source"] = r.source;
        w["n"] = r.n;
        if (r.exclusive) w["exclusive"] = true;
    }
    if (!r.raw.empty()) w["raw"] = r.raw;
    if (r.element.on)
        w["element"] = r.element.list + "[" + (r.element.all ? std::string("*") : std::to_string(r.element.index)) + "]" +
                       (r.element.leaf.empty() ? "" : "." + r.element.leaf);
    if (r.kind == WriterKind::ArchiveWriter) {
        // no op: kiln-eml's journal names the entry's resource (or, format 2, this field), not what the package did to the
        // field (archive_journal.h)
        w["named"] = r.archive_field ? "field" : "entry";
    } else if (r.list == ListOp::None) {
        if (r.op.set) write_value(w, "op", r.op.value, "opEntriesLeftOut", list_cap);
        else w["op"] = r.op.to_json();
    } else if (r.list == ListOp::Clear) {
        w["list"] = {{"clear", true}};
    } else {
        const char* op = r.list == ListOp::Append ? "append" : r.list == ListOp::Remove ? "remove" : "replace";
        w["list"] = {{op, list_json(r.list_values, list_cap)}};
        if (r.list_values.size() > list_cap) w["listEntriesLeftOut"] = r.list_values.size() - list_cap;
    }
    write_value(w, "value", r.after, "valueEntriesLeftOut", list_cap);
    // the check's own rows too are Kiln's: certain; a package from the journal: named; the drift probe: best effort
    w["certainty"] = r.kind == WriterKind::ArchiveWriter ? "named" : r.kind == WriterKind::Drift ? "bestEffort" : "certain";
    return w;
}

// What a field row and a conflict line call a place: the Kiln field, or the raw path as written.
std::string field_name(const FieldRef& ref, const FieldOutcome* fo) {
    if (!is_raw_field(ref.field)) return ref.field;
    return fo && !fo->raw.empty() ? fo->raw : ref.field.substr(4);
}

}  // namespace

json report_json(const Schema& schema, const Result& r, const ReportMeta& meta, const ReportCaps& caps) {
    json doc = {{"format", 1}, {"pid", meta.pid}, {"written", meta.written}};
    if (!meta.kiln.empty()) doc["kiln"] = meta.kiln;
    doc["generation"] = meta.generation;
    doc["phase"] = meta.world_load ? "worldLoad" : "start";
    json versions = json::object();
    for (const auto& [name, reg] : schema.registries) versions[name] = reg.version;
    doc["schema"] = {{"registrySchema", schema.format}, {"registries", versions}};
    doc["regmap"] = {{"side", meta.regmap.side}, {"exeSha256", meta.regmap.exe_sha256}, {"verified", meta.regmap.verified}};
    doc["regmap"]["record"] = meta.regmap.record.empty() ? json(nullptr) : json(meta.regmap.record);
    doc["base"] = {{"origin", meta.base.origin}, {"writer", meta.base.writer}};
    if (!meta.base.token.empty()) doc["base"]["token"] = meta.base.token;
    if (meta.base.archive.is_object()) doc["base"]["archive"] = meta.base.archive;
    {
        const auto& jr = meta.base.journal;
        json journal = {{"state", jr.state}, {"packages", jr.packages}, {"entries", jr.entries}, {"truncated", jr.truncated}};
        if (!jr.text.empty()) journal["text"] = jr.text;
        doc["base"]["journal"] = std::move(journal);
    }
    json order = json::array();
    for (const ReportMeta::Order& o : meta.order) {
        json row = {{"mod", o.mod}, {"version", o.version}};
        if (!o.edits_hash.empty()) row["editsHash"] = o.edits_hash;
        order.push_back(std::move(row));
    }
    doc["order"] = std::move(order);
    doc["orderSource"] = meta.order_source;
    if (!meta.boot_sync.is_null()) doc["bootSync"] = meta.boot_sync;
    if (meta.plan.hash.empty()) {
        doc["plan"] = nullptr;
    } else {
        doc["plan"] = {{"hash", meta.plan.hash}, {"file", meta.plan.file}};
        doc["plan"]["previous"] = meta.plan.previous.empty() ? json(nullptr) : json(meta.plan.previous);
    }
    doc["rollback"] = meta.rollback.is_object() ? meta.rollback : json(nullptr);
    if (meta.saves.is_array() && !meta.saves.empty()) doc["saves"] = meta.saves;
    if (meta.migrated.is_array() && !meta.migrated.empty()) doc["migrated"] = meta.migrated;

    std::set<EntryRef> added(r.added.begin(), r.added.end());
    size_t writers_cut = 0;
    json fields = json::array();
    for (const auto& [ref, fo] : r.fields) {
        if (fields.size() >= caps.fields) break;
        const RegistrySpec* reg = schema.registry(ref.registry);
        const FieldSpec* spec = is_raw_field(ref.field) ? fo.raw_spec.get() : (reg ? reg->field(ref.field) : nullptr);
        json row = {{"registry", ref.registry}, {"id", ref.id}, {"field", field_name(ref, &fo)}};
        if (is_raw_field(ref.field)) row["raw"] = true;
        row["applies"] = applies_name(spec ? spec->applies : Applies::Restart);
        const std::string origin = added.count({ref.registry, ref.id}) ? "added" : meta.origin ? meta.origin(ref) : "archive";
        row["base"] = {{"origin", origin}};
        write_value(row["base"], "value", fo.base, "entriesLeftOut", caps.list_entries);
        json writers = json::array();
        const size_t skip = fo.chain.size() > caps.writers ? fo.chain.size() - caps.writers : 0;  // the last ones decide the result
        for (size_t k = skip; k < fo.chain.size(); ++k) writers.push_back(writer_json(fo.chain[k], caps.list_entries));
        row["writers"] = std::move(writers);
        if (skip) row["writersLeftOut"] = skip;
        writers_cut += skip;
        write_value(row, "result", fo.result, "resultEntriesLeftOut", caps.list_entries);
        if (!fo.formula_inputs.empty()) row["formulaInputs"] = fo.formula_inputs;
        row["winner"] = fo.winner.empty() ? json(nullptr) : json(fo.winner);
        fields.push_back(std::move(row));
    }
    doc["fields"] = std::move(fields);

    json conflicts = json::array();
    std::vector<const Line*> lines;
    lines.reserve(r.lines.size() + meta.hook_lines.size());
    for (const Line& l : r.lines) lines.push_back(&l);
    for (const Line& l : meta.hook_lines) lines.push_back(&l);
    for (const Line* lp : lines) {
        const Line& l = *lp;
        if (conflicts.size() >= caps.conflicts) break;
        const auto fo = r.fields.find(l.field);
        json row = {{"field", l.field.registry + "[" + l.field.id + "]." + field_name(l.field, fo == r.fields.end() ? nullptr : &fo->second)},
                    {"registry", l.field.registry},
                    {"id", l.field.id},
                    {"kind", line_kind_name(l.kind)}};
        row["winner"] = l.winner.empty() ? json(nullptr) : json(l.winner);
        row["loser"] = l.loser.empty() ? json(nullptr) : json(l.loser);
        row["certainty"] = certainty_name(l.certainty);
        row["text"] = l.text;
        if (is_raw_field(l.field.field)) row["raw"] = true;
        conflicts.push_back(std::move(row));
    }
    doc["conflicts"] = std::move(conflicts);

    json refused = json::array();
    for (const Refusal& x : r.refused) {
        if (refused.size() >= caps.refused) break;
        json row = {{"mod", x.mod}, {"source", x.source}, {"stage", stage_name(x.stage)}, {"n", x.n}, {"reason", x.reason}};
        if (!x.registry.empty()) row["registry"] = x.registry;
        if (!x.id.empty()) row["id"] = x.id;
        if (!x.field.empty()) row["field"] = x.field;
        if (x.raw) row["raw"] = true;
        refused.push_back(std::move(row));
    }
    doc["refused"] = std::move(refused);

    auto entries = [&](const std::vector<EntryRef>& list, bool adding) {
        json a = json::array();
        for (const EntryRef& e : list) {
            if (a.size() >= caps.entries) break;
            json row = {{"registry", e.registry}, {"id", e.id},
                {"state", meta.world_load ? (adding ? "registered" : e.registry == "loot" ? "disabled" : "hidden") : "pendingWorld"}};
            if (adding) {
                if (const auto d = r.definitions.find(e); d != r.definitions.end()) {
                    row["mod"] = d->second.mod; row["source"] = d->second.source; row["n"] = d->second.n;
                    row["like"] = d->second.like; row["base"] = d->second.base;
                }
            } else if (const auto removal = r.removals.find(e); removal != r.removals.end()) {
                row["mod"] = removal->second.mod; row["source"] = removal->second.source;
                row["n"] = removal->second.n; row["reason"] = removal->second.reason;
            }
            a.push_back(std::move(row));
        }
        return a;
    };
    doc["added"] = entries(r.added, true);
    doc["removed"] = entries(r.removed, false);

    doc["counts"] = {{"fields", r.fields.size()}, {"conflicts", lines.size()}, {"refused", r.refused.size()}, {"added", r.added.size()}, {"removed", r.removed.size()}};
    // refused rows that wait for a restart (the stable token RESTART_REQUIRED_TOKEN first): what a reload, a live edit or a
    // runtime pass left for the next start. Counted over every row, also those the refused cap leaves out.
    doc["counts"]["restartRequired"] = std::count_if(r.refused.begin(), r.refused.end(), [](const Refusal& x) { return restart_required(x); });
    doc["truncated"] = {{"fields", r.fields.size() - doc["fields"].size()},
                        {"conflicts", lines.size() - doc["conflicts"].size()},
                        {"refused", r.refused.size() - doc["refused"].size()},
                        {"added", r.added.size() - doc["added"].size()},
                        {"removed", r.removed.size() - doc["removed"].size()},
                        {"writers", writers_cut}};
    return doc;
}

std::vector<std::string> report_log(const Result& r, const ReportCaps& caps) {
    std::vector<std::string> out;
    const size_t all = r.lines.size() + r.refused.size();
    if (all == 0) return out;
    // refusals first: an edit that did not apply is what an author needs to see, even behind many conflicts
    for (const Refusal& x : r.refused) {
        if (out.size() >= caps.log) break;
        out.push_back("registry: refused " + x.mod + " " + (x.source.empty() ? std::string("(no source)") : x.source) + " #" + std::to_string(x.n) + " (" +
                      stage_name(x.stage) + "): " + x.reason);
    }
    for (const Line& l : r.lines) {
        if (out.size() >= caps.log) break;
        out.push_back("registry: " + std::string(line_kind_name(l.kind)) + ": " + l.text);
    }
    const size_t left = all - out.size();
    std::string total = "registry: " + std::to_string(r.lines.size()) + (r.lines.size() == 1 ? " conflict, " : " conflicts, ") + std::to_string(r.refused.size()) +
                        (r.refused.size() == 1 ? " refused edit" : " refused edits");
    total += left ? "; " + std::to_string(left) + " not in kiln.log: state/registry.json has them all" : "; state/registry.json has them all";
    out.push_back(std::move(total));
    return out;
}

}  // namespace kc::registry
