// SPDX-License-Identifier: MIT
// kiln.content registry stage: the generated schema (schema.g.h, tools/registrygen/registrygen.py) as the edit IR's
// descriptor (ir.h's Schema). One FieldSpec per generated row: a scalar leaf ("maxStack", "damage.fire"), a list ("perks",
// "inputs"), a leaf of a list's elements ("inputs[].count"); then link_elements() joins the leaves to their list (element
// Struct). Kinds map as: int -> Int, float and time (seconds) -> Float, bool -> Bool, enum -> Enum, flags -> Flags,
// ref -> Id (its registry in FieldSpec::ref, so references are checked), list -> List (its element's kind; Struct for
// elements with fields). Each field's ops (none, set, number, list, indexed), the schema's optional refs, the lengths of
// indexed lists (fixed, or another field's value plus an offset), each registry's version and raw flag, the id migrations of
// its breaking steps (kRenames) and the schema format are carried over.
#pragma once
#include <string>
#include <vector>

#include "ir.h"
#include "schema.g.h"

namespace kc::registry {

namespace schema_detail {

inline std::vector<std::string> split_names(const char* s) {
    std::vector<std::string> out;
    std::string cur;
    for (const char* p = s; *p; ++p) {
        if (*p == '|') out.push_back(cur), cur.clear();
        else cur += *p;
    }
    if (!cur.empty()) out.push_back(cur);
    return out;
}

inline FieldType type_of(gen::Kind k) {
    switch (k) {
        case gen::Kind::Int: return FieldType::Int;
        case gen::Kind::Float:
        case gen::Kind::Time: return FieldType::Float;
        case gen::Kind::Bool: return FieldType::Bool;
        case gen::Kind::Enum: return FieldType::Enum;
        case gen::Kind::Flags: return FieldType::Flags;
        case gen::Kind::Ref: return FieldType::Id;
        case gen::Kind::List: return FieldType::List;
        case gen::Kind::Struct: return FieldType::Struct;
    }
    return FieldType::Int;
}

inline FieldOps ops_of(gen::Ops o) {
    switch (o) {
        case gen::Ops::None: return FieldOps::None;
        case gen::Ops::Set: return FieldOps::Set;
        case gen::Ops::Number: return FieldOps::Number;
        case gen::Ops::List: return FieldOps::List;
        case gen::Ops::Indexed: return FieldOps::Indexed;
    }
    return FieldOps::Any;
}

inline Applies applies_of(gen::Applies a) {
    return a == gen::Applies::Now ? Applies::Now : a == gen::Applies::WorldLoad ? Applies::WorldLoad : Applies::Restart;
}

}  // namespace schema_detail

inline Schema generated_schema() {
    using namespace schema_detail;
    Schema s;
    s.format = static_cast<int>(gen::kRegistrySchema);
    for (const gen::Registry& r : gen::kRegistries) {
        RegistrySpec& reg = s.registries[r.name];
        reg.name = r.name;
        reg.version = r.version;
        reg.add = r.add;
        reg.remove = r.remove[0] != 0;
        reg.raw = r.raw;
        reg.edits_per_call = r.edits_per_call;
        for (size_t i = r.first; i < r.first + r.count; ++i) {
            const gen::Field& f = gen::kFields[i];
            FieldSpec x;
            x.name = f.path;
            x.type = type_of(f.kind);
            x.element = type_of(f.element);
            x.read_only = f.read_only;
            x.applies = applies_of(f.applies);
            if (f.has_min) x.min = f.min;
            if (f.has_max) x.max = f.max;
            x.max_items = f.max_items;
            x.enum_values = split_names(f.values);
            x.ref = f.ref;
            x.optional = f.optional;
            x.ops = ops_of(f.ops);  // the schema's ops: which edits the field takes (ir.h ops_allow)
            x.fixed_length = f.fixed_length;
            x.length_field = f.length_field;
            x.length_offset = f.length_offset;
            if (f.kind == gen::Kind::Flags) x.max_items = x.enum_values.size();
            reg.fields[x.name] = std::move(x);
        }
    }
    // the id migrations of the breaking steps, oldest first per registry (one Migration per step)
    for (const gen::Rename& r : gen::kRenames) {
        if (!r.registry) break;
        const auto reg = s.registries.find(r.registry);
        if (reg == s.registries.end()) continue;
        auto& steps = reg->second.migrations;
        if (steps.empty() || steps.back().from != r.from || steps.back().to != r.to) steps.push_back({r.from, r.to, {}, {}});
        (r.field ? steps.back().fields : steps.back().ids)[r.old_name] = r.new_name;
    }
    link_elements(&s);
    return s;
}

}  // namespace kc::registry
