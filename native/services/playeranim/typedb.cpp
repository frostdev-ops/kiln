// SPDX-License-Identifier: MIT
// See typedb.h.
#include "typedb.h"

#include <algorithm>
#include <charconv>

#include "../types/scan.h"
#include "blob.h"
#include "nlohmann/json.hpp"

namespace kpa {

const char* kind_name(uint32_t kind) {
    switch (kind) {
        case K_NONE: return "none";
        case K_BOOL: return "bool";
        case K_U8: return "u8";
        case K_S8: return "s8";
        case K_U16: return "u16";
        case K_S16: return "s16";
        case K_U32: return "u32";
        case K_S32: return "s32";
        case K_U64: return "u64";
        case K_S64: return "s64";
        case K_F32: return "f32";
        case K_F64: return "f64";
        case K_ENUM: return "enum";
        case K_BM8: return "bitmask8";
        case K_BM16: return "bitmask16";
        case K_BM32: return "bitmask32";
        case K_BM64: return "bitmask64";
        case K_TYPEDEF: return "typedef";
        case K_STRUCT: return "struct";
        case K_STATIC_ARRAY: return "static_array";
        case K_DS_ARRAY: return "ds_array";
        case K_DS_STRING: return "ds_string";
        case K_DS_OPTIONAL: return "ds_optional";
        case K_DS_VARIANT: return "ds_variant";
        case K_BLOB_ARRAY: return "blob_array";
        case K_BLOB_STRING: return "blob_string";
        case K_BLOB_OPTIONAL: return "blob_optional";
        case K_BLOB_VARIANT: return "blob_variant";
        case K_OBJECT_REF: return "object_reference";
        case K_GUID: return "guid";
        default: return nullptr;
    }
}

const Type* TypeDb::by_name(std::string_view name) const {
    const auto it = by_name_.find(std::string(name));
    return it == by_name_.end() ? nullptr : it->second;
}

const Type* TypeDb::get(uint32_t hash) const {
    const auto it = by_hash_.find(hash);
    return it == by_hash_.end() ? nullptr : it->second;
}

const Type* TypeDb::get(std::string_view key) const {
    if (key.size() > 2 && key[0] == '0' && (key[1] == 'x' || key[1] == 'X')) {
        uint64_t h = 0;
        const auto r = std::from_chars(key.data() + 2, key.data() + key.size(), h, 16);
        if (r.ec != std::errc() || r.ptr != key.data() + key.size() || h > UINT32_MAX) return nullptr;
        return get(static_cast<uint32_t>(h));
    }
    return by_name(key);
}

const Type* TypeDb::resolve(const Type* t) const {
    for (int i = 0; i < 16; ++i) {
        if (t == nullptr || t->kind != K_TYPEDEF) return t;
        t = t->has_inner ? by_name(t->inner) : nullptr;
    }
    return t;
}

const std::vector<const Field*>& TypeDb::all_fields(const Type* t) const {
    return t && t->kind == K_STRUCT ? t->all : none_;
}

void TypeDb::add(Type t) {
    types_.push_back(std::move(t));
    const Type* p = &types_.back();
    by_name_[p->name] = p;
    by_hash_[p->hash] = p;
}

void TypeDb::finish() {
    for (Type& t : types_) {
        t.inner_t = t.has_inner && !t.inner.empty() ? get(t.inner) : nullptr;
        for (Field& f : t.fields) f.t = f.has_type ? get(f.type) : nullptr;
    }
    // all_fields: the chain of struct bases (refl.py follows by_name while the kind is struct). A chain longer than 256 is
    // a cycle in a broken dump: cut there (refl.py would not return).
    for (Type& t : types_) {
        if (t.kind != K_STRUCT) continue;
        std::vector<const Type*> chain;
        for (const Type* cur = &t; cur && cur->kind == K_STRUCT && chain.size() < 256;
             cur = cur->has_inner && !cur->inner.empty() ? by_name(cur->inner) : nullptr)
            chain.push_back(cur);
        for (auto it = chain.rbegin(); it != chain.rend(); ++it)
            for (const Field& f : (*it)->fields) t.all.push_back(&f);
    }
    finished_ = true;
}

bool TypeDb::from_json(std::string_view text, TypeDb* out, std::string* why) {
    *out = TypeDb();
    nlohmann::json doc = nlohmann::json::parse(text.begin(), text.end(), nullptr, false);
    if (doc.is_discarded() || !doc.is_object() || !doc.contains("types") || !doc["types"].is_array()) {
        *why = "not a type dump: an object with \"types\"";
        return false;
    }
    auto u64 = [](const nlohmann::json& j, const char* k) -> uint64_t {
        const auto it = j.find(k);
        return it != j.end() && it->is_number_unsigned() ? it->get<uint64_t>() : it != j.end() && it->is_number_integer() ? static_cast<uint64_t>(it->get<int64_t>()) : 0;
    };
    for (const nlohmann::json& j : doc["types"]) {
        if (!j.is_object() || !j.contains("name") || !j["name"].is_string()) {
            *why = "a type without a name";
            return false;
        }
        Type t;
        t.name = j["name"].get<std::string>();
        t.hash = static_cast<uint32_t>(u64(j, "hash"));
        t.size = static_cast<uint32_t>(u64(j, "size"));
        t.align = static_cast<uint32_t>(u64(j, "align"));
        t.kind = static_cast<uint32_t>(u64(j, "kind"));
        t.count = static_cast<uint32_t>(u64(j, "count"));
        if (j.contains("inner") && j["inner"].is_string()) t.has_inner = true, t.inner = j["inner"].get<std::string>();
        if (j.contains("fields") && j["fields"].is_array()) {
            t.has_fields = true;
            for (const nlohmann::json& f : j["fields"]) {
                Field fd;
                if (f.contains("name") && f["name"].is_string()) fd.name = f["name"].get<std::string>();
                if (f.contains("type") && f["type"].is_string()) fd.has_type = true, fd.type = f["type"].get<std::string>();
                fd.offset = u64(f, "offset");
                t.fields.push_back(std::move(fd));
            }
        }
        if (j.contains("values") && j["values"].is_array()) {
            t.has_values = true;
            for (const nlohmann::json& v : j["values"]) {
                EnumValue ev;
                if (v.contains("name") && v["name"].is_string()) ev.name = v["name"].get<std::string>();
                ev.value = u64(v, "value");
                t.values.push_back(std::move(ev));
            }
        }
        out->add(std::move(t));
    }
    out->finish();
    return true;
}

namespace {

constexpr uint64_t kInner = 0x38, kSize = 0x40, kAlign = 0x44, kCount = 0x48, kKind = 0x4C, kHash = 0x50, kFields = 0x58, kEnums = 0x60;
constexpr uint64_t kFieldStride = 0x30, kEnumStride = 0x28;

// pe.text_at: the bytes [ptr, ptr + len) cut at the image's end, decoded with replacement; "" when the pointer is outside
// or the length is not 1..4096.
std::string text_at(const ktypes::Image& im, uint64_t ptr, uint64_t len) {
    if (len == 0 || len > 4096 || !im.has(ptr, 1)) return {};
    uint64_t n = len;
    while (n > 0 && !im.has(ptr, n)) --n;  // the slice stops at the image's end
    return utf8_replace(im.at(ptr), static_cast<size_t>(n));
}

}  // namespace

bool TypeDb::from_image(const ktypes::Image& im, TypeDb* out, std::string* why) {
    *out = TypeDb();
    const ktypes::TypeTable tt = ktypes::find_type_table(im);
    if (!tt.error.empty() || tt.table == 0) {
        *why = "the game's type table: " + (tt.error.empty() ? std::string("not found") : tt.error);
        return false;
    }
    // Every entry of the table, and every type record their inner and field types reach (refl.extract names a
    // referenced record only when it is a type record itself).
    std::unordered_map<uint64_t, std::string> names;
    std::vector<uint64_t> todo;
    auto visit = [&](uint64_t va) {
        if (va == 0 || names.count(va) || !ktypes::is_type(im, va)) return;
        names.emplace(va, std::string(ktypes::str_at(im, va + ktypes::td::qname)));
        todo.push_back(va);
    };
    for (uint32_t i = 0; i < tt.count; ++i) visit(im.q(tt.table + 8ull * i));
    while (!todo.empty()) {
        const uint64_t va = todo.back();
        todo.pop_back();
        visit(im.q(va + kInner));
        const uint32_t kind = im.d(va + kKind) & 0xFF, count = im.d(va + kCount);
        const uint64_t fp = im.q(va + kFields);
        if (kind == K_STRUCT && fp && count < 4096)
            for (uint32_t i = 0; i < count; ++i) visit(im.q(fp + uint64_t{i} * kFieldStride + 0x10));
    }
    // refl.extract sorts the records by name (in address order within a name), so the last of a name wins.
    std::vector<std::pair<std::string_view, uint64_t>> order;
    order.reserve(names.size());
    for (const auto& [va, n] : names) order.emplace_back(n, va);
    std::sort(order.begin(), order.end());
    auto ref = [&](uint64_t va, std::string* name) {
        const auto it = va ? names.find(va) : names.end();
        if (it == names.end()) return false;
        *name = it->second;
        return true;
    };
    for (const auto& [name, va] : order) {
        Type t;
        t.name = std::string(name);
        t.hash = im.d(va + kHash);
        t.size = im.d(va + kSize);
        t.align = im.w(va + kAlign);
        t.kind = im.d(va + kKind) & 0xFF;
        t.count = im.d(va + kCount);
        t.has_inner = ref(im.q(va + kInner), &t.inner);
        const uint64_t fp = im.q(va + kFields);
        if (t.kind == K_STRUCT && fp && t.count < 4096 && im.has(fp, uint64_t{t.count} * kFieldStride)) {
            t.has_fields = true;
            for (uint32_t i = 0; i < t.count; ++i) {
                const uint64_t e = fp + uint64_t{i} * kFieldStride;
                Field f;
                f.name = text_at(im, im.q(e), im.q(e + 8));
                f.has_type = ref(im.q(e + 0x10), &f.type);
                f.offset = im.q(e + 0x18);
                t.fields.push_back(std::move(f));
            }
        }
        const uint64_t ep = im.q(va + kEnums);
        if (t.kind == K_ENUM && ep && t.count < 65536 && im.has(ep, uint64_t{t.count} * kEnumStride)) {
            t.has_values = true;
            for (uint32_t i = 0; i < t.count; ++i) {
                const uint64_t e = ep + uint64_t{i} * kEnumStride;
                t.values.push_back({text_at(im, im.q(e), im.q(e + 8)), im.q(e + 0x10)});
            }
        }
        out->add(std::move(t));
    }
    out->finish();
    return true;
}

}  // namespace kpa
