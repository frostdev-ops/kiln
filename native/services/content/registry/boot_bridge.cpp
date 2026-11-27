// SPDX-License-Identifier: MIT
// kiln.content registry stage: the boot back-end's bridge (boot_hooks.h) between gamedata_bootpatch.cpp's resources-ready
// apply and the stage: the old documents as the stage's old-path rows, and the registry's writes on a copy.
#include "boot_hooks.h"

#include <algorithm>
#include <cstring>
#include <map>
#include <set>

#include "../gamedata.h"
#include "model.h"

namespace kc::registry::boothook {

namespace {
std::string folded(std::string s) {
    for (char& c : s)
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    return s;
}
}  // namespace

// The old documents' writes on places the registries map, as the stage's old-path rows (they lie below every registry
// edit). Only what a Kiln field's row covers: a scalar member (int, float, bool, time, enum), an element of a list of
// scalars ([i] or [*]), and a list's clear, replace or append.
// fronts: the documents ("<mod>/<key>") the registry's boot back-end may write itself (front-ends, compose.h OldWrite::front).
// Such a document's rows are front rows when every value it sets is one scalar field this build maps and has proven, as a
// boot field, by an op that means the same both ways; else none of them is, and its entry mark says why (front_why).
std::vector<OldWrite> old_writes(const std::vector<OldDoc>& docs, model::Model& m, const std::set<std::string>* fronts) {
    std::vector<OldWrite> out;
    const map::RegMap& rm = m.regmap();
    const Schema& schema = m.schema();
    std::map<FieldRef, Value> lists;  // a list's value as the old documents leave it so far
    for (const OldDoc& d : docs) {
        const std::string doc_id = d.mod + "/" + d.key;
        const bool candidate = fronts && fronts->count(doc_id);
        const size_t first = out.size();
        std::string front_why;  // the first reason this candidate is not a front-end
        auto not_front = [&](const std::string& why) {
            if (front_why.empty()) front_why = why;
        };
        if (candidate && !d.lists.empty()) not_front("it changes a list, which the registry's front-end does not write");
        bool matched_registry = false;
        for (const auto& [regname, rmap] : rm.registries) {
            if (rmap.entries.resource_type != d.type) continue;
            std::string why;
            const std::vector<model::Entry>* es = m.entries(regname, &why);
            if (!es) continue;
            const RegistrySpec* rs = schema.registry(regname);
            if (!rs) continue;
            matched_registry = true;
            const bool array = rmap.entries.kind == map::EntryKind::Array;
            {
                // (path as written) -> (entry index, the path below the entry)
                auto locate = [&](const std::string& path, size_t* idx, std::string* rest) {
                    std::string p = folded(path);
                    if (!array) {
                        for (size_t i = 0; i < es->size(); ++i)
                            if (std::memcmp(&(*es)[i].place.key, &d.res, sizeof d.res) == 0) return *idx = i, *rest = p, true;
                        return false;
                    }
                    const std::string head = folded(rmap.entries.array) + "[";
                    if (p.rfind(head, 0) != 0) return false;
                    const size_t close = p.find(']');
                    if (close == std::string::npos || close + 1 >= p.size() || p[close + 1] != '.') return false;
                    const std::string n = p.substr(head.size(), close - head.size());
                    if (n.empty() || n.size() > 9 || !std::all_of(n.begin(), n.end(), [](char c) { return c >= '0' && c <= '9'; })) return false;
                    const size_t el = std::stoul(n);
                    // the element of this document's resource: another resource of the type has an element N too
                    for (size_t i = 0; i < es->size(); ++i)
                        if ((*es)[i].place.element == static_cast<int32_t>(el) && std::memcmp(&(*es)[i].place.key, &d.res, sizeof d.res) == 0)
                            return *idx = i, *rest = p.substr(close + 2), true;
                    return false;
                };
                auto value_for = [&](const map::Row& row, const FieldSpec& spec, FieldType type, const nlohmann::json& v, Value* x) {
                    if (row.conv == "enum") {
                        if (!v.is_string()) return false;
                        for (const auto& [k, g] : row.values)
                            if (folded(g) == folded(v.get<std::string>())) return *x = Value::text(k), true;
                        return false;
                    }
                    FieldSpec f = spec;
                    f.type = type;
                    std::string err;
                    return value_of(json::parse(v.dump()), f, false, x, &err);
                };
                auto op_for = [&](const map::Row& row, const FieldSpec& spec, FieldType type, const nlohmann::json& v, Op* op) {
                    if (!v.is_object()) return op->set = true, value_for(row, spec, type, v, &op->value);
                    if (v.contains("set")) return op->set = true, value_for(row, spec, type, v["set"], &op->value);
                    if (v.contains("mul")) op->mul = v["mul"].get<double>();
                    if (v.contains("add")) op->add = v["add"].get<double>();
                    if (v.contains("min")) op->min = v["min"].get<double>();
                    if (v.contains("max")) op->max = v["max"].get<double>();
                    op->raise_only = v.value("raiseOnly", false);
                    op->lower_only = v.value("lowerOnly", false);
                    return true;
                };
                auto push = [&](const FieldRef& ref, Op op, ListOp lop, std::vector<Value> vals) {
                    OldWrite w;
                    w.kind = WriterKind::OldPatch;
                    w.mod = d.mod;
                    w.who = d.mod + "/" + d.key;
                    w.field = ref;
                    w.op = std::move(op);
                    w.list = lop;
                    w.list_values = std::move(vals);
                    out.push_back(std::move(w));
                };
                // the entries this document writes, whatever their paths map to (an entry mark, kind OldPatch with no field):
                // the live back-end's drift probe (drift.h) takes a change there for Kiln's own patch, never for another
                // loader's, since many paths (formula inputs, lists of structs, "[*]" over a registry's elements) have no
                // row of their own. An element "<array>[N]..." marks that element; any other path marks every entry of the
                // resource.
                std::set<size_t> marked;
                auto mark = [&](const std::string& path) {
                    size_t el = 0;
                    std::string below;
                    if (array && locate(path, &el, &below)) return (void)marked.insert(el);
                    for (size_t k = 0; k < es->size(); ++k)
                        if (std::memcmp(&(*es)[k].place.key, &d.res, sizeof d.res) == 0) marked.insert(k);
                };
                for (const auto& [spath, svalue] : d.sets) mark(spath);
                for (const OldDoc::List& o : d.lists) mark(o.path);
                for (const size_t k : marked) {
                    OldWrite w;
                    w.kind = WriterKind::OldPatch;
                    w.mod = d.mod;
                    w.who = d.mod + "/" + d.key;
                    w.field = {regname, (*es)[k].id, ""};
                    out.push_back(std::move(w));
                }
                for (const auto& [spath, svalue] : d.sets) {
                    size_t i = 0;
                    std::string rest;
                    if (!locate(spath, &i, &rest)) {
                        if (candidate) not_front("a value it sets has no registry field on this game build");
                        continue;
                    }
                    size_t scalars = 0;  // the scalar fields this value is (a front-end's must be exactly one)
                    for (const auto& [fname, row] : rmap.rows) {
                        if (row.paths.empty() || fname.find("[]") != std::string::npos) continue;
                        const FieldSpec* f = rs->field(fname);
                        if (!f) continue;
                        const std::string p0 = folded(row.paths[0]);
                        const FieldRef ref{regname, (*es)[i].id, fname};
                        if ((row.conv == "int" || row.conv == "float" || row.conv == "bool" || row.conv == "time" || row.conv == "enum") && p0 == rest) {
                            Op op;
                            if (!op_for(row, *f, f->type, svalue, &op)) {
                                if (candidate) not_front(regname + "." + fname + ": its value is not one the registry reads the same way");
                                continue;
                            }
                            ++scalars;
                            if (candidate) {
                                std::string reason;
                                if (rm.mapping(regname, fname, &reason) != map::Mapping::Mapped)
                                    not_front(regname + "." + fname + " is not proven on this game build" + (reason.empty() ? "" : " (" + reason + ")"));
                                else if (row.backend != map::Backend::Boot || f->applies != Applies::Restart || f->read_only)
                                    not_front(regname + "." + fname + " is not a field the registry writes at the game's start");
                                else if (op.min && op.max && *op.min > *op.max)
                                    // a resource patch clamps as std::clamp does, the registry min then max: they differ only here
                                    not_front(regname + "." + fname + ": an operation whose min is above its max");
                            }
                            push(ref, std::move(op), ListOp::None, {});
                        } else if (row.conv == "list" && f->element != FieldType::Struct && rest.rfind(p0 + "[", 0) == 0 && rest.back() == ']') {
                            if (candidate) not_front(regname + "." + fname + ": it sets an element of a list, which the registry's front-end does not write");
                            const std::string ix = rest.substr(p0.size() + 1, rest.size() - p0.size() - 2);
                            auto cur = lists.find(ref);
                            if (cur == lists.end()) {
                                auto base = m.value(regname, i, fname, &why);
                                if (!base || base->kind != Value::List) continue;
                                cur = lists.emplace(ref, *base).first;
                            }
                            Op op;
                            FieldSpec el = *f;
                            el.type = f->element;
                            if (!op_for(row, *f, f->element, svalue, &op)) continue;
                            Value next = cur->second;
                            for (size_t k = 0; k < next.list.size(); ++k) {
                                if (ix != "*" && ix != std::to_string(k)) continue;
                                next.list[k] = apply_op(op, next.list[k], el);
                            }
                            cur->second = next;
                            Op whole;
                            whole.set = true;
                            whole.value = next;
                            push(ref, std::move(whole), ListOp::None, {});
                        }
                    }
                    if (candidate && scalars != 1) not_front(scalars ? "a value it sets is more than one registry field" : "a value it sets has no registry field on this game build");
                }
                for (const OldDoc::List& o : d.lists) {
                    size_t i = 0;
                    std::string rest;
                    if (!locate(o.path, &i, &rest)) continue;
                    for (const auto& [fname, row] : rmap.rows) {
                        const FieldSpec* f = rs->field(fname);
                        if (!f || row.conv != "list" || row.paths.empty() || folded(row.paths[0]) != rest || f->element == FieldType::Struct) continue;
                        std::vector<Value> vals;
                        for (const auto& v : o.values) {
                            Value x;
                            if (value_for(row, *f, f->element, v, &x)) vals.push_back(std::move(x));
                        }
                        lists.erase(FieldRef{regname, (*es)[i].id, fname});
                        push({regname, (*es)[i].id, fname}, Op{}, o.op == 'x' ? ListOp::Clear : o.op == 'r' ? ListOp::Replace : ListOp::Append, std::move(vals));
                    }
                }
            }
        }
        if (!candidate) continue;
        if (!matched_registry) not_front("no registry maps the resource it patches on this game build");
        bool any_field = false, any_mark = false;
        for (size_t k = first; k < out.size(); ++k) (out[k].field.field.empty() ? any_mark : any_field) = true;
        if (!any_field) not_front("it sets no value a registry field holds");
        if (front_why.empty()) {
            for (size_t k = first; k < out.size(); ++k)
                if (!out[k].field.field.empty()) out[k].front = true;
            continue;
        }
        // the verdict rides on the document's entry mark (the stage reads it for the report); a document no registry entry
        // holds gets a mark of its own, with no entry
        if (!any_mark) {
            OldWrite w;
            w.kind = WriterKind::OldPatch;
            w.mod = d.mod;
            w.who = doc_id;
            out.push_back(std::move(w));
        }
        for (size_t k = first; k < out.size(); ++k)
            if (out[k].field.field.empty()) out[k].front_why = front_why;
    }
    return out;
}

// The registry's boot writes on one copy, after the old documents: each field's registry rows evaluated over the value the
// copy holds now, written where it differs. *outcomes: what each field became (or why it was not written).
size_t write(const KilnAssetKey& key, const std::string& type_name, std::vector<uint8_t>* copy, const stage::BootResource& rb,
             std::vector<stage::BootOutcome>* outcomes, std::vector<std::string>* notes) {
    const gd::TType* type = gd::type_named(type_name);
    if (!type || !type->resolved()) {
        for (const auto& f : rb.fields) outcomes->push_back({f.field, false, {}, "this game build has no type " + type_name});
        return 0;
    }
    size_t written = 0;
    const gd::Root root_kind = type_name == "keen::ecs::TemplateResource" ? gd::Root::Template : gd::Root::Plain;
    for (const stage::BootField& f : rb.fields) {
        stage::BootOutcome o;
        o.field = f.field;
        model::MemberFn get = [&](const std::string& path, json* out, std::string* why) {
            return model::bytes_member(copy->data(), copy->size(), type_name, path, out, why);
        };
        std::string why;
        const auto below = model::decode(*f.reg, *f.row, f.spec, get, nullptr, {}, &why);
        if (!below) {
            o.why = "the value in the resource cannot be read (" + why + ")";
            outcomes->push_back(std::move(o));
            continue;
        }
        const Value v = evaluate(f.rows, *below, f.spec);
        std::vector<std::pair<std::string, json>> writes;
        if (!model::encode(*f.reg, *f.row, f.spec, v, *below, &writes, &why)) {
            o.why = why;
            outcomes->push_back(std::move(o));
            continue;
        }
        // every write is computed before any is made: a field is written whole or not at all
        const uintptr_t lo = reinterpret_cast<uintptr_t>(copy->data()), hi = lo + copy->size();
        std::vector<std::pair<uintptr_t, std::vector<uint8_t>>> bytes;
        for (const auto& [path, value] : writes) {
            std::vector<gd::Resolved> found;
            std::string err;
            if (!gd::resolve(root_kind, gd::Node{type->resolved(), lo}, path, &found, &err) || found.size() != 1) {
                why = "\"" + path + "\": " + (err.empty() ? "no such member" : err);
                break;
            }
            Field leaf;
            const gd::Resolved& r = found[0];
            if (!r.node.t || !gd::leaf_of(r.node.t, &leaf) || leaf.ref || leaf.vt == KILN_CV_GUID || r.node.addr < lo || r.node.addr + leaf.bytes > hi) {
                why = "\"" + path + "\" is not a scalar member of the resource";
                break;
            }
            uint8_t b[16] = {};
            bool reset = false;
            if (!gd::parse_leaf(leaf, gjson::parse(value.dump()), reinterpret_cast<const uint8_t*>(r.node.addr), b, &reset, &err)) {
                why = "\"" + path + "\": " + err;
                break;
            }
            bytes.emplace_back(r.node.addr, std::vector<uint8_t>(b, b + leaf.bytes));
        }
        if (bytes.size() != writes.size()) {
            o.why = why;
            outcomes->push_back(std::move(o));
            continue;
        }
        for (const auto& [addr, b] : bytes) std::memcpy(reinterpret_cast<void*>(addr), b.data(), b.size());
        written += bytes.size();
        o.ok = true;
        o.written = v;
        if (!bytes.empty())
            notes->push_back("registry: " + type_name + " " + guid_text(key.guid) + ": " + f.field.text() + " = " + v.text_of() + " (" +
                             std::to_string(bytes.size()) + " value(s), after the resource patches)");
        outcomes->push_back(std::move(o));
    }
    return written;
}


}  // namespace kc::registry::boothook
