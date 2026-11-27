// SPDX-License-Identifier: MIT
// kiln.content registry views: the game's reflection as views.h's plain types (view_service.h describe_type), over gamedata's
// type graph (gamedata_types.cpp). Leaves are what gd::leaf_of calls leaves, so a view reads a member exactly as the walker
// (and every data pack path) does: a struct of one scalar is that scalar, keen::Time is seconds, an enum is its value's name.
#include "view_service.h"

#include "../gamedata.h"

namespace kc::registry::views {

namespace {

constexpr int kDescribeDepth = kMaxDepth + 2;  // deeper than derive() walks, so derive names the cut-off

std::shared_ptr<const TypeDesc> desc(const gd::TType* decl, int depth) {
    auto d = std::make_shared<TypeDesc>();
    const gd::TType* t = decl ? decl->resolved() : nullptr;
    if (!t) return d;  // Other
    d->name = t->name;
    d->hash = t->hash;
    kc::Field f;
    if (t->kind != gd::K_BLOBVAR && gd::leaf_of(decl, &f)) {
        switch (f.vt) {
            case KILN_CV_BOOL: d->kind = TypeKind::Bool; break;
            case KILN_CV_FLOAT: d->kind = TypeKind::Float; break;
            case KILN_CV_TIME: d->kind = TypeKind::Time; break;
            case KILN_CV_GUID:
                d->kind = f.ref ? TypeKind::Ref : TypeKind::Guid;
                if (f.ref && t->kind == gd::K_REF && t->inner()) d->target = t->inner()->resolved() ? t->inner()->resolved()->name : t->inner()->name;
                break;
            case KILN_CV_INT:
                d->kind = f.enum_kind == KILN_CONTENT_ENUM ? TypeKind::Enum : f.enum_kind == KILN_CONTENT_FLAGS ? TypeKind::Flags : TypeKind::Int;
                if (f.en && d->kind != TypeKind::Int)
                    for (const auto& [n, _] : f.en->values) d->values.push_back(n);
                break;
            default: break;
        }
        return d;
    }
    switch (t->kind) {
        case gd::K_BLOBSTRING: d->kind = TypeKind::Text; return d;
        case gd::K_BLOBVAR: d->kind = TypeKind::Variant; return d;
        case gd::K_BLOBOPT: d->kind = TypeKind::Optional; return d;
        case gd::K_ARRAY:
        case gd::K_BLOBARRAY:
            d->kind = TypeKind::List;
            d->fixed = t->kind == gd::K_ARRAY ? t->count : 0;
            if (depth < kDescribeDepth) d->element = desc(t->inner(), depth + 1);
            return d;
        case gd::K_STRUCT:
            if (t->name == "keen::impact::Configurations") {
                d->kind = TypeKind::Configs;
                return d;
            }
            d->kind = TypeKind::Struct;
            if (depth < kDescribeDepth)
                for (const gd::TField& m : t->fields) d->fields.emplace_back(m.name, desc(m.type(), depth + 1));
            return d;
        default: return d;
    }
}

}  // namespace

std::optional<TypeDesc> describe_type(const std::string& qualified) {
    const gd::TType* t = gd::type_named(qualified);
    const gd::TType* r = t ? t->resolved() : nullptr;
    if (!r || r->kind != gd::K_STRUCT) return std::nullopt;
    TypeDesc out = *desc(r, 0);
    out.name = qualified;
    return out;
}

}  // namespace kc::registry::views
