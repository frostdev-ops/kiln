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

size_t align16(size_t n) { return (n + 15) & ~size_t(15); }

// A type whose bytes can move as they are: no member of it holds an offset to data of its own (a list, a text, an optional,
// a variant); references and GUIDs are plain bytes.
bool movable(const gd::TType* t, int depth = 0) {
    const gd::TType* r = t ? t->resolved() : nullptr;
    if (!r || depth > 32) return false;
    switch (r->kind) {
        case gd::K_BLOBARRAY: case gd::K_BLOBSTRING: case gd::K_BLOBOPT: case gd::K_BLOBVAR: return false;
        case gd::K_STRUCT:
            for (const gd::TField& f : r->fields)
                if (!movable(f.type(), depth + 1)) return false;
            return true;
        case gd::K_ARRAY: return movable(r->inner(), depth + 1);
        default: return true;
    }
}

// A list whose length changes (its row says "resize"): the new list is placed behind the copy's bytes, 16-aligned, as a
// resource patch's clear, append and replace place theirs (gamedata_bootstruct.cpp), and the list's header points at it
// (an empty list: no data). An element of the new value that equals one of the list below (the first such not taken yet)
// keeps that element's bytes, so the members Kiln has no field for stay the game's; any other element starts as zeros and
// gets its mapped leaves. Elements that hold data of their own cannot be moved like that: refused, never written blind.
// Everything is computed before the copy changes: the list is written whole or not at all. *written: the elements placed.
bool resize_list(const std::string& type_name, gd::Root root_kind, const map::RegistryMap& reg, const map::Row& row, const FieldSpec& f,
                 const Value& below, const Value& v, std::vector<uint8_t>* copy, size_t* written, std::string* why) {
    *written = 0;
    const gd::TType* type = gd::type_named(type_name);
    if (!type || !type->resolved()) return *why = "this game build has no type " + type_name, false;
    if (v.kind != Value::List || below.kind != Value::List) return *why = "not a list", false;
    const uintptr_t lo = reinterpret_cast<uintptr_t>(copy->data()), hi = lo + copy->size();
    std::vector<gd::Resolved> found;
    std::string err;
    if (!gd::resolve(root_kind, gd::Node{type->resolved(), lo}, row.paths.at(0), &found, &err) || found.size() != 1 || !found[0].node.t)
        return *why = "\"" + row.paths[0] + "\": " + (err.empty() ? "no such member" : err), false;
    const gd::TType* list = found[0].node.t->resolved();
    if (!list || list->kind != gd::K_BLOBARRAY) return *why = "\"" + row.paths[0] + "\" is not a scalar member of the resource", false;
    if (found[0].node.addr < lo || found[0].node.addr + 8 > hi) return *why = "\"" + row.paths[0] + "\" lies outside the resource's own bytes", false;
    const gd::TType* el = list->inner() ? list->inner()->resolved() : nullptr;
    if (!el || !el->size) return *why = f.name + ": the size of its elements is not known on this game build", false;
    if (!movable(el)) return *why = f.name + "'s elements hold data of their own: the boot back-end writes them in place only, its length cannot change", false;
    const size_t where = found[0].node.addr - lo, es = el->size;
    int32_t rel = 0;
    uint32_t count = 0;
    std::memcpy(&rel, copy->data() + where, 4);
    std::memcpy(&count, copy->data() + where + 4, 4);
    const int64_t old_at = static_cast<int64_t>(where) + rel;
    if (count && (!rel || old_at < 0 || static_cast<uint64_t>(old_at) + uint64_t{count} * es > copy->size()))
        return *why = f.name + ": the list in the resource is not readable", false;
    if (count != below.list.size()) return *why = f.name + ": the resource holds " + std::to_string(count) + " element(s), the value read holds " +
                                                  std::to_string(below.list.size()), false;
    if (v.list.size() > 1000000) return *why = f.name + ": the list would be too long", false;
    std::vector<uint8_t> out(v.list.size() * es, 0);
    std::vector<char> taken(count, 0);
    const Field none{};
    for (size_t i = 0; i < v.list.size(); ++i) {
        uint8_t* at = out.data() + i * es;
        size_t j = 0;
        while (j < count && (taken[j] || !(below.list[j] == v.list[i]))) ++j;
        if (j < count) {
            taken[j] = 1;
            std::memcpy(at, copy->data() + old_at + j * es, es);
            continue;
        }
        // a new element: its mapped leaves on zeros
        auto put = [&](const gd::Node& n, const FieldSpec& spec, const map::Row& r, const Value& x) {
            Field leaf = none;
            if (!n.t || !gd::leaf_of(n.t, &leaf) || leaf.ref || leaf.vt == KILN_CV_GUID || n.addr < reinterpret_cast<uintptr_t>(at) ||
                n.addr + leaf.bytes > reinterpret_cast<uintptr_t>(at) + es)
                return *why = f.name + ": an element's member is not a scalar member of the resource", false;
            json j;
            if (!model::encode_leaf(spec, r, x, &j, why)) return false;
            uint8_t b[16] = {};
            bool reset = false;
            if (!gd::parse_leaf(leaf, gjson::parse(j.dump()), reinterpret_cast<const uint8_t*>(n.addr), b, &reset, &err))
                return *why = f.name + "[" + std::to_string(i) + "]: " + err, false;
            std::memcpy(reinterpret_cast<void*>(n.addr), b, leaf.bytes);
            return true;
        };
        if (f.element != FieldType::Struct) {
            map::Row er = row;
            er.conv = f.element == FieldType::Float ? "float" : f.element == FieldType::Bool ? "bool" : "int";
            FieldSpec espec = f;
            espec.type = f.element;
            if (!put(gd::Node{el, reinterpret_cast<uintptr_t>(at)}, espec, er, v.list[i])) return false;
            continue;
        }
        const Value& now = v.list[i];
        for (size_t k = 0; k < now.keys.size(); ++k) {
            if (now.list[k].kind == Value::None) continue;  // not given: the game's zero
            const auto lr = reg.rows.find(f.name + "[]." + now.keys[k]);
            const FieldSpec* leaf = f.element_field(now.keys[k]);
            if (lr == reg.rows.end() || !leaf || lr->second.paths.empty()) return *why = f.name + "[]." + now.keys[k] + " has no row", false;
            std::vector<gd::Resolved> rs;
            if (!gd::resolve(gd::Root::Plain, gd::Node{el, reinterpret_cast<uintptr_t>(at)}, lr->second.paths[0], &rs, &err) || rs.size() != 1)
                return *why = "\"" + lr->second.paths[0] + "\": " + (err.empty() ? "no such member" : err), false;
            if (!put(rs[0].node, *leaf, lr->second, now.list[k])) return false;
        }
    }
    // the copy changes only now (it may move: offsets, never pointers, from here on)
    const size_t list_at = align16(copy->size());
    if (!out.empty()) {
        copy->resize(align16(list_at + out.size()), 0);
        std::memcpy(copy->data() + list_at, out.data(), out.size());
    }
    const int32_t new_rel = out.empty() ? 0 : static_cast<int32_t>(static_cast<int64_t>(list_at) - static_cast<int64_t>(where));
    const uint32_t new_count = static_cast<uint32_t>(v.list.size());
    std::memcpy(copy->data() + where, &new_rel, 4);
    std::memcpy(copy->data() + where + 4, &new_count, 4);
    *written = v.list.size() + 1;  // the elements and the list's header
    return true;
}

}  // namespace

// The old documents' writes on places the registries map, as the stage's old-path rows (they lie below every registry
// edit). Only what a Kiln field's row covers: a scalar member (int, float, bool, time, enum), an element of a list of
// scalars ([i] or [*]), and a list's clear, replace or append.
std::vector<OldWrite> old_writes(const std::vector<OldDoc>& docs, model::Model& m) {
    std::vector<OldWrite> out;
    const map::RegMap& rm = m.regmap();
    const Schema& schema = m.schema();
    std::map<FieldRef, Value> lists;  // a list's value as the old documents leave it so far
    for (const OldDoc& d : docs) {
        for (const auto& [regname, rmap] : rm.registries) {
            // a kind arrays registry (randomLoot) has no restart field: the old documents are below its live rows, nothing to read back
            if (rmap.entries.kind == map::EntryKind::Arrays || rmap.entries.resource_type != d.type) continue;
            std::string why;
            const std::vector<model::Entry>* es = m.entries(regname, &why);
            if (!es) continue;
            const RegistrySpec* rs = schema.registry(regname);
            if (!rs) continue;
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
                    if (!locate(spath, &i, &rest)) continue;
                    for (const auto& [fname, row] : rmap.rows) {
                        if (row.paths.empty() || fname.find("[]") != std::string::npos) continue;
                        const FieldSpec* f = rs->field(fname);
                        if (!f) continue;
                        const std::string p0 = folded(row.paths[0]);
                        const FieldRef ref{regname, (*es)[i].id, fname};
                        if ((row.conv == "int" || row.conv == "float" || row.conv == "bool" || row.conv == "time" || row.conv == "enum") && p0 == rest) {
                            Op op;
                            if (op_for(row, *f, f->type, svalue, &op)) push(ref, std::move(op), ListOp::None, {});
                        } else if (row.conv == "list" && f->element != FieldType::Struct && rest.rfind(p0 + "[", 0) == 0 && rest.back() == ']') {
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
                }
                for (const OldDoc::List& o : d.lists) {
                    size_t i = 0;
                    std::string rest;
                    if (!locate(o.path, &i, &rest)) continue;
                    for (const auto& [fname, row] : rmap.rows) {
                        const FieldSpec* f = rs->field(fname);
                        // a list of structs takes only an old document's clear (a resource patch replaces and appends lists of plain
                        // values only); EMBER's "build anywhere" clears the world scene's lists that way
                        if (!f || row.conv != "list" || row.paths.empty() || folded(row.paths[0]) != rest || (f->element == FieldType::Struct && o.op != 'x'))
                            continue;
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
        if (f.row->conv == "list" && f.row->resize && v.kind == Value::List && below->kind == Value::List && v.list.size() != below->list.size()) {
            size_t n = 0;
            if (!resize_list(type_name, root_kind, *f.reg, *f.row, f.spec, *below, v, copy, &n, &why)) {
                o.why = why;
                outcomes->push_back(std::move(o));
                continue;
            }
            written += n;
            o.ok = true;
            o.written = v;
            notes->push_back("registry: " + type_name + " " + guid_text(key.guid) + ": " + f.field.text() + " = " + v.text_of() + " (" +
                             std::to_string(v.list.size()) + " element(s) in a list of another length, after the resource patches)");
            outcomes->push_back(std::move(o));
            continue;
        }
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
