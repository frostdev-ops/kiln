// SPDX-License-Identifier: MIT
// kiln.content gamedata: the game's own reflection (TypeByHash records, layout in docs/re/content.md section 2) as a
// type graph. Unlike refl.cpp's flattener it keeps every kind (blob arrays, variants, optionals, strings), follows a
// struct's base chain (record +0x38 of a struct = its base: keen::impact::FloatImpactConfig -> ImpactConfig) and
// resolves field types lazily, so any resource can be walked in memory. Records are static data of the exe: the
// graph is built once per type and never freed.
#include <mutex>

#include "gamedata.h"

namespace kc::gd {

namespace {

std::recursive_mutex g_mu;
std::unordered_map<uintptr_t, std::unique_ptr<TType>> g_by_rec;
std::unordered_map<uint32_t, const TType*> g_by_hash;

std::string brief_of(const std::string& q) {
    // "keen::ecs::LifeTime" -> "LifeTime"; templates keep their arguments: "keen::BlobArray<keen::X>" -> "BlobArray<keen::X>"
    const size_t lt = q.find('<');
    const size_t end = lt == std::string::npos ? q.size() : lt;
    const size_t c = q.rfind("::", end);
    return c == std::string::npos ? q : q.substr(c + 2);
}

std::shared_ptr<EnumInfo> enum_of(uintptr_t rec, const std::string& name, uint32_t count) {
    auto en = std::make_shared<EnumInfo>();
    en->type = name;
    uintptr_t values = 0;
    if (!rdv(rec + E.type_enums, values) || !values || count > 4096) return en;
    for (uint32_t i = 0; i < count; ++i) {
        const uintptr_t e = values + static_cast<uintptr_t>(i) * E.enum_stride;
        uintptr_t np = 0;
        uint64_t nl = 0, v = 0;
        if (!rdv(e, np) || !rdv(e + 8, nl) || !rdv(e + E.enum_value, v)) break;
        en->values.emplace_back(read_text(np, nl), static_cast<int64_t>(v));
    }
    return en;
}

TType* load(uintptr_t rec);

// Own fields of one struct record.
bool own_fields(uintptr_t rec, uint32_t count, std::vector<TField>* out) {
    uintptr_t fields = 0;
    if (!rdv(rec + E.type_fields, fields) || (!fields && count)) return false;
    for (uint32_t i = 0; i < count && i < 4096; ++i) {
        const uintptr_t fr = fields + static_cast<uintptr_t>(i) * E.field_stride;
        uintptr_t np = 0, tp = 0;
        uint64_t nl = 0, off = 0;
        if (!rdv(fr, np) || !rdv(fr + 8, nl) || !rdv(fr + E.field_type, tp) || !rdv(fr + E.field_offset, off)) return false;
        TField f;
        f.name = read_text(np, nl);
        f.type_rec = tp;
        f.offset = static_cast<uint32_t>(off);
        if (!f.name.empty() && off < (1u << 24)) out->push_back(std::move(f));
    }
    return true;
}

TType* load(uintptr_t rec) {
    if (!rec) return nullptr;
    std::lock_guard lock(g_mu);
    if (auto it = g_by_rec.find(rec); it != g_by_rec.end()) return it->second.get();
    auto t = std::make_unique<TType>();
    uintptr_t qn = 0;
    uint64_t qlen = 0;
    uint32_t kind = 0;
    if (!rdv(rec + E.type_qname, qn) || !rdv(rec + E.type_qname + 8, qlen) || !rdv(rec + E.type_size, t->size) ||
        !rdv(rec + E.type_count, t->count) || !rdv(rec + E.type_kind, kind) || !rdv(rec + E.type_inner, t->inner_rec) ||
        !rdv(rec + E.type_hash, t->hash))
        return nullptr;
    t->rec = rec;
    t->kind = kind & 0xFF;
    t->name = read_text(qn, qlen);
    if (t->name.empty() || t->size >= (1u << 24) || t->count >= 100000) return nullptr;
    t->brief = brief_of(t->name);
    TType* raw = t.get();
    g_by_rec.emplace(rec, std::move(t));  // before recursing: a struct may reach itself through a blob array
    g_by_hash.emplace(raw->hash, raw);
    if (raw->kind == K_ENUM) raw->en = enum_of(rec, raw->name, raw->count);
    if (raw->kind >= K_BM8 && raw->kind <= K_BM64 && raw->inner_rec) {
        const TType* fe = load(raw->inner_rec);
        if (fe && fe->kind == K_ENUM) raw->en = fe->en;
    }
    if (raw->kind == K_STRUCT) {
        // base chain first (bases are structs whose fields sit at their own offsets inside the derived one)
        std::vector<uintptr_t> chain;
        for (uintptr_t b = raw->inner_rec; b && chain.size() < 16; ) {
            uint32_t bk = 0, bc = 0;
            uintptr_t bi = 0;
            if (!rdv(b + E.type_kind, bk) || (bk & 0xFF) != K_STRUCT || !rdv(b + E.type_count, bc) || !rdv(b + E.type_inner, bi))
                break;
            chain.push_back(b);
            b = bi;
        }
        for (auto it = chain.rbegin(); it != chain.rend(); ++it) {
            uint32_t bc = 0;
            rdv(*it + E.type_count, bc);
            own_fields(*it, bc, &raw->fields);
        }
        own_fields(rec, raw->count, &raw->fields);
    }
    return raw;
}

}  // namespace

const TType* TField::type() const { return load(type_rec); }

const TType* TType::inner() const { return load(inner_rec); }

const TType* TType::resolved() const {
    const TType* t = this;
    for (int i = 0; i < 12 && t && t->kind == K_TYPEDEF; ++i) t = t->inner();
    return t && t->kind != K_TYPEDEF ? t : nullptr;
}

const TField* TType::field(const std::string& n) const {
    for (const TField& f : fields)
        if (f.name == n) return &f;
    const std::string l = lower(n);
    for (const TField& f : fields)
        if (lower(f.name) == l) return &f;
    return nullptr;
}

const TType* type_at(uintptr_t rec) { return load(rec); }

const TType* type_hash(uint32_t hash) {
    {
        std::lock_guard lock(g_mu);
        if (auto it = g_by_hash.find(hash); it != g_by_hash.end()) return it->second;
    }
    const uintptr_t rec = type_by_hash(hash);
    const TType* t = load(rec);
    return t && t->hash == hash ? t : nullptr;
}

const TType* type_named(const std::string& qname) { return type_hash(fnv1a(qname)); }

}  // namespace kc::gd
