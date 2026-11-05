// SPDX-License-Identifier: MIT
#include "game.h"

#include <cstring>

#include "services/kiln_settings.h"

namespace kset {

// Each guarded function holds no C++ objects with destructors, as __try requires.
bool read(uintptr_t address, void* out, size_t size) {
    if (!address || address > UINTPTR_MAX - size) return false;
    __try {
        std::memcpy(out, reinterpret_cast<const void*>(address), size);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool write(uintptr_t address, const void* in, size_t size) {
    if (!address || address > UINTPTR_MAX - size) return false;
    __try {
        std::memcpy(reinterpret_cast<void*>(address), in, size);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

static uintptr_t call_guarded(TypeByHashFn fn, uint32_t hash) {
    __try {
        return fn(hash);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

uintptr_t type_by_hash(TypeByHashFn fn, uint32_t hash) { return fn ? call_guarded(fn, hash) : 0; }

std::string text(uintptr_t ptr, uint64_t len) {
    char buf[257];
    if (!ptr || len == 0 || len > 256 || !read(ptr, buf, static_cast<size_t>(len))) return {};
    return std::string(buf, static_cast<size_t>(len));
}

uint32_t fnv1a(std::string_view s) {
    uint32_t h = 0x811c9dc5u;
    for (unsigned char c : s) h = (h ^ c) * 0x1000193u;
    return h;
}

namespace {

template <class T>
bool at(uintptr_t base, int64_t off, T& out) {
    return off >= 0 && rd(base + static_cast<uintptr_t>(off), out);
}

std::string qname(const Layout& L, uintptr_t type) {
    uintptr_t p = 0;
    uint64_t n = 0;
    if (!at(type, L.type_qname, p) || !at(type, L.type_qname + 8, n)) return {};
    return text(p, n);
}

uint32_t kind_of(const Layout& L, uintptr_t type) {
    uint32_t k = 0;
    return at(type, L.type_kind, k) ? k : 0;
}

uint32_t size_of(const Layout& L, uintptr_t type) {
    uint32_t s = 0;
    return at(type, L.type_size, s) ? s : 0;
}

// Follows typedefs (keen::float -> keen::float32).
uintptr_t resolve(const Layout& L, uintptr_t type) {
    for (int i = 0; i < 8 && type && kind_of(L, type) == KIND_TYPEDEF; ++i) {
        uintptr_t inner = 0;
        if (!at(type, L.type_inner, inner)) return 0;
        type = inner;
    }
    return type;
}

bool is_int_kind(uint32_t k) { return k >= KIND_U8 && k <= KIND_S64; }
bool signed_kind(uint32_t k) { return k == KIND_S8 || k == KIND_S16 || k == KIND_S32 || k == KIND_S64; }

}  // namespace

bool reflect(const Layout& L, uintptr_t type, std::string_view expect_qname, Reflected& out, std::string& error) {
    out = Reflected{};
    if (!type) return error = "type not registered", false;
    const std::string q = qname(L, type);
    if (q != expect_qname) return error = "type name is '" + q + "'", false;
    uint32_t hash = 0;
    if (!at(type, L.type_hash, hash) || hash != fnv1a(expect_qname)) return error = "type hash mismatch", false;
    if (kind_of(L, type) != KIND_STRUCT) return error = "not a struct", false;
    out.size = size_of(L, type);
    uint32_t count = 0;
    uintptr_t fields = 0;
    if (out.size < 8 || out.size > 4096) return error = "implausible size " + std::to_string(out.size), false;
    if (!at(type, L.type_count, count) || count == 0 || count > 256 || !at(type, L.type_fields, fields) || !fields)
        return error = "no field table", false;
    for (uint32_t i = 0; i < count; ++i) {
        const uintptr_t rec = fields + static_cast<uintptr_t>(i) * static_cast<uintptr_t>(L.field_stride);
        uintptr_t name_ptr = 0, ftype = 0;
        uint64_t name_len = 0, offset = 0;
        if (!rd(rec, name_ptr) || !rd(rec + 8, name_len) || !at(rec, L.field_type, ftype) ||
            !at(rec, L.field_offset, offset))
            return error = "field " + std::to_string(i) + " unreadable", false;
        Field f;
        f.name = text(name_ptr, name_len);
        if (f.name.empty() || offset >= out.size) return error = "field " + std::to_string(i) + " invalid", false;
        f.offset = static_cast<uint32_t>(offset);
        f.type_name = qname(L, ftype);
        const uintptr_t t = resolve(L, ftype);
        const uint32_t k = kind_of(L, t);
        const uint32_t sz = size_of(L, t);
        if (k == KIND_BOOL && sz == 1) {
            f.kind = KILN_SETTING_BOOL, f.width = 1;
        } else if (k == KIND_F32 && sz == 4) {
            f.kind = KILN_SETTING_FLOAT, f.width = 4;
        } else if (is_int_kind(k) && (sz == 1 || sz == 2 || sz == 4 || sz == 8)) {
            f.kind = KILN_SETTING_INT, f.width = sz, f.is_signed = signed_kind(k);
        } else if (k == KIND_ENUM) {
            uintptr_t under = 0;
            const uintptr_t u = at(t, L.type_inner, under) ? resolve(L, under) : 0;
            const uint32_t uk = kind_of(L, u), us = size_of(L, u);
            uint32_t n = 0;
            uintptr_t values = 0;
            if (is_int_kind(uk) && (us == 1 || us == 2 || us == 4) && at(t, L.type_count, n) && n > 0 && n <= 64 &&
                at(t, L.type_enum_values, values) && values) {
                f.kind = KILN_SETTING_ENUM, f.width = us, f.is_signed = signed_kind(uk);
                for (uint32_t j = 0; j < n; ++j) {
                    const uintptr_t e = values + static_cast<uintptr_t>(j) * static_cast<uintptr_t>(L.enum_stride);
                    uintptr_t np = 0;
                    uint64_t nl = 0, v = 0;
                    if (!rd(e, np) || !rd(e + 8, nl) || !at(e, L.enum_value, v)) {
                        f.kind = 0;
                        break;
                    }
                    f.options.push_back({text(np, nl), static_cast<int64_t>(v)});
                }
            }
        } else if (k == KIND_STRUCT && sz == 8 && qname(L, t) == "keen::Time") {
            f.kind = KILN_SETTING_DURATION, f.width = 8, f.is_signed = true;  // keen::Time { sint64 value } (ns)
        }
        if (f.kind && f.offset + f.width > out.size) return error = "field " + f.name + " out of bounds", false;
        out.fields.push_back(std::move(f));
    }
    return true;
}

}  // namespace kset
