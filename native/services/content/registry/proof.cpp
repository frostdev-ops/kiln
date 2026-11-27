// SPDX-License-Identifier: MIT
// kiln.content registry stage: the lab's registry proof producer, its native half (proof.h).
#include "proof.h"

#include <algorithm>
#include <cmath>

#include "../../../core/src/services/boot_gamedata.h"
#include "boot_hooks.h"
#include "limits.h"

namespace kc::registry::proof {

namespace {

std::string sha_of(const std::string& s) { return kiln::boot_gd::hex_of(kiln::boot_gd::sha256(s)); }
std::string sha_of(const std::vector<uint8_t>& b) { return sha_of(std::string(b.begin(), b.end())); }

bool mapped(const map::Row& r) { return r.state != map::RowState::Unmapped && !r.paths.empty(); }

// A raw member value as the record's contract has it (regmap/README.md "Raw values"): a time member's nanoseconds (the
// reflection reads a keen::Time as seconds), everything else as read.
json raw_of(const map::Row& row, json m) {
    if (row.conv == "time" && m.is_number()) return static_cast<int64_t>(std::llround(m.get<double>() * 1e9));
    return m;
}

// "inputs[].count" -> ("inputs", "count"); a field of the entry -> ("", "")
std::pair<std::string, std::string> element_of(const std::string& field) {
    const size_t at = field.find("[].");
    if (at == std::string::npos) return {};
    return {field.substr(0, at), field.substr(at + 3)};
}

// The read-only rows (back-end none) of a registry without a boot row, from the base: read through the schema (a list field's
// first element) from the first of up to 64 entries that reads a value, raw by the base's members (a structured value is not
// one member: the SHA-256 of the entry's resource, as the live suite gives it). An entry whose raw view cannot be read is passed
// over; no entry that reads: no sample (kilnctl: unproven, never an error).
void read_only(model::Model& m, model::Source& src, const Env& e, const map::RegistryMap& reg, const RegistrySpec& rs, json* samples) {
    std::string why;
    const std::vector<model::Entry>* es = m.entries(reg.registry, &why);
    if (!es) return;
    for (const auto& [f, row] : reg.rows) {
        if (!mapped(row) || row.backend != map::Backend::None || !rs.field(f)) continue;
        const auto [list, leaf] = element_of(f);
        const std::string field = list.empty() ? f : list;
        const auto wit = reg.rows.find(field);
        if (wit == reg.rows.end() || wit->second.paths.empty()) continue;
        static const char* const kCompared[] = {"int", "float", "bool", "time", "enum", "flags", "optionalEnum", "flag"};
        const bool compared = std::find(std::begin(kCompared), std::end(kCompared), row.conv) != std::end(kCompared);
        const bool prefer_true = rs.field(f)->type == FieldType::Bool;
        bool sampled = false;
        for (int pass = 0; pass < (prefer_true ? 2 : 1) && !sampled; ++pass) {
            for (size_t i = 0; i < es->size() && i < 64; ++i) {
                std::optional<Value> v = m.value(reg.registry, i, field, &why);
                if (v && !list.empty()) {
                    const Value* l = v->kind == Value::List && !v->list.empty() ? v->list[0].leaf(leaf) : nullptr;
                    v = l ? std::optional<Value>(*l) : std::nullopt;
                }
                if (!v) continue;
                json read = v->to_json();
                if (read.is_null() || (read.is_array() && read.empty())) continue;
                if (prefer_true && read.is_boolean() && read.get<bool>() != (pass == 0)) continue;
                const model::Entry& en = (*es)[i];
                std::vector<uint8_t> bytes;
                const std::string sha = e.bytes && e.bytes(en.place.key, &bytes, &why) ? sha_of(bytes) : std::string();
                json raw = json::array();
                bool all = true;
                for (const std::string& p : row.paths) {
                    json mv;
                    std::string w;
                    if (!compared) {
                        all = all && !sha.empty();
                        raw.push_back("resource:" + sha);
                    }
                    else if (src.member(en.place, list.empty() ? p : wit->second.paths[0] + "[0]." + p, &mv, &w) && !mv.is_null()) raw.push_back(raw_of(row, mv));
                    else all = false;
                }
                if (!all) continue;
                json s = {{"field", reg.registry + "." + f}, {"entry", en.id}, {"element", list.empty() ? json(nullptr) : json(0)}, {"read", read},
                          {"rawBefore", raw}};
                if (!sha.empty()) s["bytesBefore"] = sha;
                samples->push_back(std::move(s));
                sampled = true;
                break;
            }
        }
    }
}

json roundtrip_doc(const Env& e, const std::string& generation, const std::string& digest, json samples) {
    return {{"format", 1}, {"kind", "registry-roundtrip"}, {"side", e.side}, {"exeSha256", e.exe_sha256}, {"generation", generation},
            {"regmapDigest", digest}, {"samples", std::move(samples)}};
}

}  // namespace

bool requested(const Env& e) {
    if (e.exists) return e.exists(kRequest);
    return e.read_data && e.read_data(kRequest).has_value();
}

std::string regmap_digest(std::vector<std::pair<std::string, std::string>> files) {
    std::sort(files.begin(), files.end());
    std::string text;
    for (const auto& [name, bytes] : files) text += name + "\n" + sha_of(bytes) + "\n";
    return sha_of(text);
}

std::optional<Value> probe_for(const FieldSpec& spec, const Value& v) {
    auto moved = [&](double x, bool whole) -> std::optional<Value> {
        const double up = whole ? x + 1 : (x == 0 ? 1.0 : x * 1.25), down = whole ? x - 1 : x * 0.75;
        auto fits = [&](double y) { return std::isfinite(y) && (!whole || (y >= -0x1p63 && y < 0x1p63 && std::trunc(y) == y)) && y != x && (!spec.min || y >= *spec.min) && (!spec.max || y <= *spec.max); };
        const double y = fits(up) ? up : fits(down) ? down : NAN;
        if (std::isnan(y)) return std::nullopt;
        return whole ? Value::integer(static_cast<int64_t>(y)) : Value::number(y);
    };
    switch (spec.type) {
        case FieldType::Int: return v.numeric() ? moved(v.as_double(), true) : std::nullopt;
        case FieldType::Float: return v.numeric() ? moved(v.as_double(), false) : std::nullopt;
        case FieldType::Bool: return v.kind == Value::Bool ? std::optional<Value>(Value::boolean(!v.b)) : std::nullopt;
        case FieldType::Enum:
            for (const std::string& n : spec.enum_values)
                if (n != v.t) return Value::text(n);
            return std::nullopt;
        case FieldType::List: {
            if (v.kind != Value::List || v.list.empty()) return std::nullopt;
            Value out = v;
            Value& first = out.list[0];
            if (spec.element == FieldType::Struct) {
                for (size_t k = 0; k < first.keys.size(); ++k) {
                    const FieldSpec* leaf = spec.element_field(first.keys[k]);
                    if (!leaf || !first.list[k].numeric()) continue;
                    if (auto p = probe_for(*leaf, first.list[k])) {
                        first.list[k] = *p;
                        return out;
                    }
                }
                return std::nullopt;
            }
            FieldSpec el = spec;  // a list's min/max are its numbers'
            el.type = spec.element;
            auto p = probe_for(el, first);
            if (!p) return std::nullopt;
            first = *p;
            return out;
        }
        default: return std::nullopt;
    }
}

bool disagreement(const std::string& why) {
    return why.find("is not a scalar member") != std::string::npos || why.find("no such member") != std::string::npos ||
           why.find("has no member") != std::string::npos;
}

json boot_sample(const map::RegistryMap& reg, const RegistrySpec& rs, const std::string& field, const std::string& id, const KilnAssetKey& key,
                 const std::string& type, const std::vector<uint8_t>& base) {
    json s = {{"field", reg.registry + "." + field}, {"entry", id}, {"element", nullptr}};
    auto missing = [&](const std::string&) -> json { return nullptr; };
    const auto rit = reg.rows.find(field);
    if (rit == reg.rows.end() || !mapped(rit->second)) return missing("the field has no mapped row");
    const map::Row& row = rit->second;
    const auto [list, leaf] = element_of(field);
    const std::string written = list.empty() ? field : list;  // the field the boot back-end writes (a list for its elements)
    const auto wit = reg.rows.find(written);
    const FieldSpec* wspec = rs.field(written);
    const FieldSpec* spec = rs.field(field);
    if (wit == reg.rows.end() || wit->second.paths.empty() || !wspec || !spec) return missing("the schema or the regmap has no " + written);
    const map::Row& wrow = wit->second;
    if (!list.empty()) s["element"] = 0;
    auto whole = [&](const std::vector<uint8_t>& b, std::string* why) {
        model::MemberFn get = [&](const std::string& path, json* out, std::string* w) { return model::bytes_member(b.data(), b.size(), type, path, out, w); };
        return model::decode(reg, wrow, *wspec, get, {}, {}, why);
    };
    auto value = [&](const std::optional<Value>& w, std::string* why) -> std::optional<Value> {
        if (!w || list.empty()) return w;
        if (w->kind != Value::List || w->list.empty()) return *why = "the list is empty", std::nullopt;
        const Value* l = w->list[0].leaf(leaf);
        if (!l) return *why = "its first element has no " + leaf, std::nullopt;
        return *l;
    };
    auto raw = [&](const std::vector<uint8_t>& b) {
        json a = json::array();
        for (const std::string& p : row.paths) {
            const std::string path = list.empty() ? p : wrow.paths.at(0) + "[0]." + p;
            json m;
            std::string w;
            a.push_back(model::bytes_member(b.data(), b.size(), type, path, &m, &w) ? raw_of(row, std::move(m)) : json(nullptr));
        }
        return a;
    };
    std::string why;
    const std::optional<Value> wbefore = whole(base, &why);
    const std::optional<Value> before = value(wbefore, &why);
    if (!before) return missing("read through the schema: " + why);
    s["read"] = before->to_json();
    s["rawBefore"] = raw(base);
    s["bytesBefore"] = sha_of(base);
    if (row.backend != map::Backend::Boot || spec->read_only) return s;  // a read-only field: read, never written
    // the probe, as the whole written field (a list keeps its other elements)
    std::optional<Value> probe = probe_for(*spec, *before);
    if (!probe) return missing("no probe value for this kind of field");
    Value wprobe = *probe;
    if (!list.empty()) {
        wprobe = *wbefore;
        Value& first = wprobe.list[0];
        for (size_t k = 0; k < first.keys.size(); ++k)
            if (first.keys[k] == leaf) first.list[k] = *probe;
    }
    s["probe"] = probe->to_json();
    ChainRow set;
    set.kind = WriterKind::Registry;
    set.mod = set.who = "registry-proof";
    set.source = "lab proof";
    set.op.set = true;
    set.op.value = wprobe;
    stage::BootResource rb;
    rb.key = key, rb.type = type;
    rb.fields.push_back({FieldRef{reg.registry, id, written}, &reg, &wrow, *wspec, {set}});
    std::vector<uint8_t> copy = base;
    std::vector<stage::BootOutcome> out;
    std::vector<std::string> notes;
    boothook::write(key, type, &copy, rb, &out, &notes);
    if (out.size() != 1 || !out[0].ok) {
        if (copy == base && out.size() == 1 && disagreement(out[0].why)) {
            s["error"] = "the boot back-end refused the probe: " + out[0].why;  // the mapping disagrees with the build
            return s;
        }
        if (copy == base) return missing("the boot back-end refused this entry's probe");
        s["error"] = "the boot back-end changed bytes but did not finish the probe";
        return s;
    }
    const std::optional<Value> after = value(whole(copy, &why), &why);
    s["readBack"] = after ? after->to_json() : json(nullptr);
    s["rawAfter"] = raw(copy);
    // A fresh generation composed without the probe: a no-op on untouched base bytes, NOT a live revert.
    // This only demonstrates that composing no rows preserves the base; the successful write/read above is the proof.
    std::vector<uint8_t> again = base;
    rb.fields[0].rows.clear();
    out.clear();
    boothook::write(key, type, &again, rb, &out, &notes);
    s["reverted"] = out.size() == 1 && out[0].ok;
    s["bytesAfter"] = sha_of(again);
    return s;
}

// A hooks registry's row (limits.h, entries kind hooks, back-end hook): its value is in no resource, so the sample is the game's
// own code: `read` the game's value (from its code), `probe` the value the producer gives Kiln's hook for a moment (no registry
// layer), `readBack` and `rawAfter` what the game's function answers then (called: the gift getter as it is, the altar limit over
// a scratch table whose levels hold 255, so the cap decides), `rawBefore` and the revert the same before and after the probe; the
// "bytes" are the answer's digest. A hook value this build does not have gives no sample (unproven, never an error).
json hook_sample(const map::RegistryMap& reg, const RegistrySpec& rs, const std::string& field, const std::string& id, std::string* why) {
    const auto rit = reg.rows.find(field);
    const FieldSpec* spec = rs.field(field);
    if (rit == reg.rows.end() || !mapped(rit->second) || !spec) return *why = "the field has no mapped row", nullptr;
    const std::string name = rit->second.paths[0];
    json base;
    if (!limits::base_value(name, &base, why)) return nullptr;
    const Value before = spec->type == FieldType::Int ? Value::integer(base.get<int64_t>()) : Value::number(base.get<double>());
    auto answer = [&]() -> std::optional<double> { return limits::game_answer(name, 255, why); };
    auto as_value = [&](double v) { return spec->type == FieldType::Int ? Value::integer(static_cast<int64_t>(std::llround(v))).to_json() : json(v); };
    const auto rb = answer();
    if (!rb) return nullptr;
    json s = {{"field", reg.registry + "." + field}, {"entry", id}, {"element", nullptr}, {"read", before.to_json()}, {"rawBefore", json::array({as_value(*rb)})},
              {"bytesBefore", sha_of("answer " + std::to_string(*rb))}};
    if (spec->read_only) return s;
    const std::optional<Value> probe = probe_for(*spec, before);
    if (!probe) return *why = "no probe value for this kind of field", nullptr;
    s["probe"] = probe->to_json();
    if (!limits::probe_set(name, probe->as_double(), why)) return nullptr;  // the hook could not go in: no evidence
    const auto ra = answer();
    limits::probe_clear(name);
    const auto rr = answer();
    s["readBack"] = ra ? as_value(*ra) : json(nullptr);
    s["rawAfter"] = json::array({ra ? as_value(*ra) : json(nullptr)});
    s["reverted"] = rr.has_value() && *rr == *rb;
    s["bytesAfter"] = sha_of("answer " + (rr ? std::to_string(*rr) : std::string("none")));
    return s;
}

std::vector<std::string> run(const Env& e) {
    std::vector<std::string> written;
    if (!requested(e)) return written;
    auto log = [&](int level, const std::string& line) {
        if (e.log) e.log(level, "registry proof: " + line);
    };
    map::RegMap rm;
    std::vector<std::string> errs;
    if (!e.schema || !e.regmap || !e.regmap(&rm, &errs)) {
        log(2, "the regmap cannot be read; nothing written");
        return written;
    }
    std::string why;
    std::unique_ptr<model::Source> src = e.source ? e.source(&why) : nullptr;
    if (!src) {
        log(2, "no base to read (" + why + "); nothing written");
        return written;
    }
    const std::string digest = regmap_digest(e.common_files ? e.common_files() : std::vector<std::pair<std::string, std::string>>{});
    model::Model m(*src, rm, *e.schema);
    auto put = [&](const std::string& rel, const json& doc) {
        if (e.write_data && e.write_data(rel, doc.dump())) written.push_back(rel);
        else log(2, "could not write " + rel);
    };
    // the boot generation, in memory
    json samples = json::array();
    for (const auto& [name, reg] : rm.registries) {
        const RegistrySpec* rs = e.schema->registry(name);
        const bool boot = std::any_of(reg.rows.begin(), reg.rows.end(), [](const auto& r) { return mapped(r.second) && r.second.backend == map::Backend::Boot; });
        if (!rs) continue;
        if (reg.entries.kind == map::EntryKind::Hooks) {
            const std::vector<model::Entry>* es = m.entries(name, &why);
            const std::string id = es && !es->empty() ? (*es)[0].id : "game:" + reg.entries.unnamed;
            for (const auto& [f, row] : reg.rows) {
                if (!mapped(row)) continue;
                std::string w;
                json sample = hook_sample(reg, *rs, f, id, &w);
                if (sample.is_null()) log(1, name + "." + f + ": unproven (" + w + ")");
                else samples.push_back(std::move(sample));
            }
            continue;
        }
        if (!boot) {
            read_only(m, *src, e, reg, *rs, &samples);
            continue;
        }
        auto each = [&](auto fn) {
            for (const auto& [f, row] : reg.rows)
                if (mapped(row) && (row.backend == map::Backend::Boot || row.backend == map::Backend::None)) fn(f);
        };
        const std::vector<model::Entry>* es = m.entries(name, &why);
        std::vector<uint8_t> base;
        std::string err;
        if (!es || es->empty()) err = "no entry to sample (" + why + ")";
        else if (reg.entries.kind == map::EntryKind::Array || reg.entries.kind == map::EntryKind::Arrays) err = "boot fields of array entries are not sampled";
        else if (!e.bytes || !e.bytes((*es)[0].place.key, &base, &why)) err = "the entry's resource cannot be read (" + why + ")";
        if (!err.empty()) {
            log(1, name + ": unproven (" + err + ")");
            continue;
        }
        const model::Entry& en = (*es)[0];
        each([&](const std::string& f) {
            json sample = boot_sample(reg, *rs, f, en.id, en.place.key, en.place.type, base);
            if (sample.is_null()) log(1, name + "." + f + ": unproven (no readable value or representable probe for this entry)");
            else samples.push_back(std::move(sample));
        });
    }
    put("proof/roundtrip-" + e.side + "-boot.json", roundtrip_doc(e, "boot", digest, std::move(samples)));
    // every entry of every registry through the schema, from the base
    json regs = json::object();
    for (const auto& [name, reg] : rm.registries) {
        if (!e.schema->registry(name)) continue;
        const std::vector<model::Entry>* es = m.entries(name, &why);
        if (!es) {
            log(1, name + ": its entries cannot be read (" + why + ")");
            continue;
        }
        json entries = json::object();
        for (size_t i = 0; i < es->size(); ++i) {
            json fields = json::object();
            for (const auto& [f, row] : reg.rows) {
                if (!mapped(row) || f.find("[]") != std::string::npos) continue;  // a list's elements are in its value
                std::string w;
                if (auto v = m.value(name, i, f, &w)) fields[f] = v->to_json();
            }
            entries[(*es)[i].id] = std::move(fields);
        }
        regs[name] = std::move(entries);
    }
    put("proof/values-" + e.side + ".json",
        json{{"format", 1}, {"kind", "registry-values"}, {"side", e.side}, {"exeSha256", e.exe_sha256},
             {"gameBuild", rm.has_build ? rm.build.game_build : std::string()}, {"regmapDigest", digest},
             {"base", {{"origin", src->name()}, {"writer", e.writer}}}, {"registries", std::move(regs)}});
    m.release();
    log(0, "wrote " + std::to_string(written.size()) + " file(s) under content/registry/proof (lab only)");
    return written;
}

}  // namespace kc::registry::proof
