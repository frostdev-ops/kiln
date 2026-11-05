// SPDX-License-Identifier: MIT
// kiln.content: the game's own reflection. TypeByHash(fnv1a(qualified name)) returns a type record (layout in
// docs/re/settings.md and docs/re/content.md): name, size, kind, fields {name, type, offset}, enum values. A struct
// is flattened into dotted scalar paths, so every registry's fields come from the running build, not from Kiln.
#include <cmath>
#include <cstdio>
#include <cstring>

#include "content.h"

namespace kc {

namespace {

enum : uint32_t {
    K_BOOL = 1, K_U8, K_S8, K_U16, K_S16, K_U32, K_S32, K_U64, K_S64, K_F32, K_F64,
    K_ENUM = 0x0C, K_BITMASK8 = 0x0D, K_BITMASK64 = 0x10, K_TYPEDEF = 0x11, K_STRUCT = 0x12, K_ARRAY = 0x13,
    K_BLOBARRAY = 0x18, K_BLOBSTRING = 0x19, K_REF = 0x1C, K_GUID = 0x1D,
};

struct Rec {
    uintptr_t p = 0;
    std::string name;
    uint32_t size = 0, count = 0, kind = 0;
    uintptr_t inner = 0, fields = 0, enums = 0;
};

bool load(uintptr_t p, Rec* r) {
    if (!p) return false;
    uintptr_t qn = 0;
    uint64_t qlen = 0;
    uint32_t kind = 0;
    if (!rdv(p + E.type_qname, qn) || !rdv(p + E.type_qname + 8, qlen) || !rdv(p + E.type_size, r->size) ||
        !rdv(p + E.type_count, r->count) || !rdv(p + E.type_kind, kind) || !rdv(p + E.type_inner, r->inner) ||
        !rdv(p + E.type_fields, r->fields) || !rdv(p + E.type_enums, r->enums))
        return false;
    r->p = p;
    r->kind = kind & 0xFF;
    r->name = read_text(qn, qlen);
    return !r->name.empty() && r->size < (1u << 24) && r->count < 100000;
}

std::shared_ptr<EnumInfo> enums_of(const Rec& r) {
    auto en = std::make_shared<EnumInfo>();
    en->type = r.name;
    if (!r.enums || r.count > 4096) return en;
    for (uint32_t i = 0; i < r.count; ++i) {
        const uintptr_t e = r.enums + static_cast<uintptr_t>(i) * E.enum_stride;
        uintptr_t np = 0;
        uint64_t nl = 0, v = 0;
        if (!rdv(e, np) || !rdv(e + 8, nl) || !rdv(e + E.enum_value, v)) break;
        en->values.emplace_back(read_text(np, nl), static_cast<int64_t>(v));
    }
    return en;
}

struct Flattener {
    Schema* s;
    const std::vector<std::string>* readonly;
    std::string err;
    int budget = 8000;

    bool is_readonly(const std::string& path) const {
        for (const std::string& r : *readonly) {
            if (path == r) return true;
            if (path.size() > r.size() && path.compare(0, r.size(), r) == 0 && (path[r.size()] == '.' || path[r.size()] == '['))
                return true;
        }
        return false;
    }

    void add(Field f) {
        if (--budget < 0) return;
        f.writable = f.writable && !is_readonly(f.path);
        s->fields.push_back(std::move(f));
    }

    // Resolves typedefs; returns the final record (display name kept by the caller).
    bool resolve(uintptr_t p, Rec* out) {
        for (int i = 0; i < 8; ++i) {
            if (!load(p, out)) return false;
            if (out->kind != K_TYPEDEF) return true;
            p = out->inner;
        }
        return false;
    }

    // A struct of exactly one scalar field (keen::HashKey32 {u32 value}) is shown as that scalar.
    bool single_scalar(const Rec& r, Rec* inner, uint64_t* off) {
        if (r.kind != K_STRUCT || r.count != 1 || !r.fields) return false;
        uintptr_t tp = 0;
        if (!rdv(r.fields + E.field_type, tp) || !rdv(r.fields + E.field_offset, *off)) return false;
        if (!resolve(tp, inner)) return false;
        return inner->kind >= K_BOOL && inner->kind <= K_F64;
    }

    void emit(uintptr_t tp, uint32_t off, const std::string& path, int depth, bool top) {
        Rec r;
        if (!load(tp, &r)) return;
        const std::string display = r.name;
        if (r.kind == K_TYPEDEF && !resolve(r.inner, &r)) return;
        Field f;
        f.path = path;
        f.type_name = display;
        f.offset = off;
        switch (r.kind) {
            case K_BOOL: case K_U8: case K_S8: case K_U16: case K_S16: case K_U32: case K_S32: case K_U64: case K_S64:
                f.bytes = static_cast<uint8_t>(r.size);
                f.vt = r.kind == K_BOOL ? KILN_CV_BOOL : KILN_CV_INT;
                f.is_signed = r.kind == K_S8 || r.kind == K_S16 || r.kind == K_S32 || r.kind == K_S64;
                if (f.bytes == 1 || f.bytes == 2 || f.bytes == 4 || f.bytes == 8) add(std::move(f));
                return;
            case K_F32: case K_F64:
                f.bytes = static_cast<uint8_t>(r.size);
                f.vt = KILN_CV_FLOAT;
                f.f64 = r.size == 8;
                if (f.bytes == 4 || f.bytes == 8) add(std::move(f));
                return;
            case K_ENUM: {
                f.bytes = static_cast<uint8_t>(r.size);
                f.vt = KILN_CV_INT;
                f.enum_kind = KILN_CONTENT_ENUM;
                f.en = enums_of(r);
                Rec under;
                if (r.inner && resolve(r.inner, &under)) f.is_signed = under.kind == K_S8 || under.kind == K_S16 || under.kind == K_S32 || under.kind == K_S64;
                if (f.bytes == 1 || f.bytes == 2 || f.bytes == 4 || f.bytes == 8) add(std::move(f));
                return;
            }
            case K_TYPEDEF:
                return;
            case K_STRUCT: {
                if (r.name == "keen::Time" && r.size == 8) {
                    f.bytes = 8;
                    f.vt = KILN_CV_TIME;
                    f.is_signed = true;
                    add(std::move(f));
                    return;
                }
                Rec in;
                uint64_t ioff = 0;
                if (single_scalar(r, &in, &ioff)) {
                    f.bytes = static_cast<uint8_t>(in.size);
                    f.offset = off + static_cast<uint32_t>(ioff);
                    f.vt = in.kind == K_BOOL ? KILN_CV_BOOL : (in.kind >= K_F32 ? KILN_CV_FLOAT : KILN_CV_INT);
                    f.f64 = in.kind == K_F64;
                    f.is_signed = in.kind == K_S8 || in.kind == K_S16 || in.kind == K_S32 || in.kind == K_S64;
                    add(std::move(f));
                    return;
                }
                if (depth >= 6 || !r.fields) return;
                walk(r, off, path + ".", depth + 1);
                return;
            }
            case K_ARRAY: {
                Rec el;
                if (!r.inner || !load(r.inner, &el) || r.count == 0 || r.count > 64) return;
                Rec elr = el;
                if (el.kind == K_TYPEDEF && !resolve(el.inner, &elr)) return;
                const uint32_t esize = elr.size ? elr.size : r.size / r.count;
                for (uint32_t i = 0; i < r.count; ++i)
                    emit(r.inner, off + i * esize, path + "[" + std::to_string(i) + "]", depth + 1, false);
                return;
            }
            case K_REF: case K_GUID:
                if (r.size != 16) return;
                f.bytes = 16;
                f.vt = KILN_CV_GUID;
                f.writable = false;  // values never; references through refs.cpp (new items, or game entries with opt-in)
                f.ref = r.kind == K_REF || display != "keen::Guid";
                add(std::move(f));
                return;
            default:
                if (r.kind >= K_BITMASK8 && r.kind <= K_BITMASK64) {
                    f.bytes = static_cast<uint8_t>(r.size);
                    f.vt = KILN_CV_INT;
                    f.enum_kind = KILN_CONTENT_FLAGS;
                    Rec en;
                    if (r.inner && load(r.inner, &en) && en.kind == K_ENUM) f.en = enums_of(en);
                    if (f.bytes == 1 || f.bytes == 2 || f.bytes == 4 || f.bytes == 8) add(std::move(f));
                    return;
                }
                if (r.kind == K_BLOBARRAY || r.kind == K_BLOBSTRING) s->special[path] = off;  // nested too: "equipment.visualModels"
                return;
        }
    }

    void walk(const Rec& r, uint32_t base, const std::string& prefix, int depth) {
        for (uint32_t i = 0; i < r.count && budget > 0; ++i) {
            const uintptr_t fr = r.fields + static_cast<uintptr_t>(i) * E.field_stride;
            uintptr_t np = 0, tp = 0;
            uint64_t nl = 0, off = 0;
            if (!rdv(fr, np) || !rdv(fr + 8, nl) || !rdv(fr + E.field_type, tp) || !rdv(fr + E.field_offset, off)) {
                err = "unreadable field record";
                return;
            }
            const std::string name = read_text(np, nl);
            if (name.empty() || off >= (1u << 20)) continue;
            const bool top = depth == 0;
            if (top) {  // remember every top-level field's offset (BlobArrays, references, ...)
                s->special.emplace(name, base + static_cast<uint32_t>(off));
            }
            emit(tp, base + static_cast<uint32_t>(off), prefix + name, depth, top);
        }
    }
};

uintptr_t type_of(const char* qname) { return type_by_hash(fnv1a(qname, std::strlen(qname))); }

}  // namespace

const Field* Schema::find(const std::string& path) const {
    if (auto it = by_path.find(path); it != by_path.end()) return &fields[it->second];
    if (auto it = by_lower.find(lower(path)); it != by_lower.end()) return &fields[it->second];
    return nullptr;
}

bool build_schema(const char* qname, const std::vector<std::string>& readonly, Schema* out, std::string* err) {
    const uintptr_t t = type_of(qname);
    Rec r;
    if (!t || !load(t, &r)) {
        *err = std::string(qname) + ": not in the game's reflection";
        return false;
    }
    if (r.name != qname || r.kind != K_STRUCT) {
        *err = std::string(qname) + ": reflection record looks wrong (" + r.name + ", kind " + std::to_string(r.kind) + ")";
        return false;
    }
    uint32_t h = 0;
    if (!rdv(t + E.type_hash, h) || h != fnv1a(qname, std::strlen(qname))) {
        *err = std::string(qname) + ": type hash mismatch";
        return false;
    }
    out->type = qname;
    out->size = r.size;
    out->fields.clear();
    Flattener fl{out, &readonly};
    fl.walk(r, 0, "", 0);
    if (!fl.err.empty()) {
        *err = std::string(qname) + ": " + fl.err;
        return false;
    }
    for (uint32_t i = 0; i < out->fields.size(); ++i) {
        const Field& f = out->fields[i];
        if (f.offset + f.bytes > out->size) {
            *err = std::string(qname) + ": field " + f.path + " outside the struct";
            return false;
        }
        out->by_path.emplace(f.path, i);
        out->by_lower.emplace(lower(f.path), i);
    }
    return !out->fields.empty();
}

int64_t field_offset(const char* qname, const char* field) {
    Rec r;
    if (!load(type_of(qname), &r) || r.kind != K_STRUCT) return -1;
    for (uint32_t i = 0; i < r.count; ++i) {
        const uintptr_t fr = r.fields + static_cast<uintptr_t>(i) * E.field_stride;
        uintptr_t np = 0;
        uint64_t nl = 0, off = 0;
        if (!rdv(fr, np) || !rdv(fr + 8, nl) || !rdv(fr + E.field_offset, off)) return -1;
        if (read_text(np, nl) == field) return static_cast<int64_t>(off);
    }
    return -1;
}

uint32_t type_size(const char* qname) {
    Rec r;
    return load(type_of(qname), &r) ? r.size : 0;
}

bool enum_values(const char* qname, std::vector<std::pair<std::string, int64_t>>* out) {
    out->clear();
    Rec r;
    if (!load(type_of(qname), &r)) return false;
    for (int i = 0; i < 8 && r.kind == K_TYPEDEF; ++i)
        if (!load(r.inner, &r)) return false;
    if (r.kind != K_ENUM) return false;
    *out = enums_of(r)->values;
    return true;
}

// Every BlobArray / BlobString inside the inline part of a struct (nested structs and static arrays included): the
// fields whose i32 self-relative offset must be rebased when the struct is copied to another address (new content).
// Refuses kinds it does not know (another blob kind would point into the old place unnoticed).
namespace {
bool collect_relocs(const Rec& r, uint32_t base, int depth, std::vector<uint32_t>* out, std::string* err) {
    if (depth > 12) return true;
    for (uint32_t i = 0; i < r.count; ++i) {
        const uintptr_t fr = r.fields + static_cast<uintptr_t>(i) * E.field_stride;
        uintptr_t np = 0, tp = 0;
        uint64_t nl = 0, off = 0;
        if (!rdv(fr, np) || !rdv(fr + 8, nl) || !rdv(fr + E.field_type, tp) || !rdv(fr + E.field_offset, off)) {
            *err = r.name + ": unreadable field record";
            return false;
        }
        Rec t;
        if (!load(tp, &t)) continue;
        for (int k = 0; k < 8 && t.kind == K_TYPEDEF; ++k)
            if (!load(t.inner, &t)) break;
        const uint32_t o = base + static_cast<uint32_t>(off);
        if (t.kind == K_BLOBARRAY || t.kind == K_BLOBSTRING) {
            out->push_back(o);
        } else if (t.kind == K_STRUCT) {
            if (!collect_relocs(t, o, depth + 1, out, err)) return false;
        } else if (t.kind == K_ARRAY) {
            Rec el;
            if (!t.inner || !load(t.inner, &el)) continue;
            for (int k = 0; k < 8 && el.kind == K_TYPEDEF; ++k)
                if (!load(el.inner, &el)) break;
            const uint32_t n = t.count, es = el.size ? el.size : (n ? t.size / n : 0);
            for (uint32_t j = 0; j < n && j < 4096; ++j) {
                if (el.kind == K_STRUCT) {
                    if (!collect_relocs(el, o + j * es, depth + 1, out, err)) return false;
                } else if (el.kind == K_BLOBARRAY || el.kind == K_BLOBSTRING) {
                    out->push_back(o + j * es);
                } else if (el.kind > K_BITMASK64 && el.kind != K_REF && el.kind != K_GUID) {
                    *err = r.name + ": array element kind " + std::to_string(el.kind) + " not understood";
                    return false;
                }
            }
        } else if (t.kind > K_BITMASK64 && t.kind != K_REF && t.kind != K_GUID) {
            *err = r.name + "." + read_text(np, nl) + ": kind " + std::to_string(t.kind) + " not understood";
            return false;
        }
    }
    return true;
}
}  // namespace

bool self_relative_fields(const char* qname, std::vector<uint32_t>* out, std::string* err) {
    out->clear();
    Rec r;
    if (!load(type_of(qname), &r) || r.kind != K_STRUCT) {
        *err = std::string(qname) + ": not in the game's reflection";
        return false;
    }
    return collect_relocs(r, 0, 0, out, err);
}

// ============================================================================================= values ===========

KilnContentValue decode(const Field& f, const uint8_t* b) {
    KilnContentValue v{};
    v.type = f.vt;
    switch (f.vt) {
        case KILN_CV_BOOL:
            v.i = b[0] != 0;
            v.f = static_cast<double>(v.i);
            break;
        case KILN_CV_INT: {
            uint64_t u = 0;
            std::memcpy(&u, b, f.bytes);
            if (f.is_signed && f.bytes < 8) {
                const uint64_t sign = 1ull << (f.bytes * 8 - 1);
                if (u & sign) u |= ~((sign << 1) - 1);
            }
            v.i = static_cast<int64_t>(u);
            v.f = f.is_signed || f.bytes < 8 ? static_cast<double>(v.i) : static_cast<double>(u);
            break;
        }
        case KILN_CV_FLOAT:
            if (f.f64) std::memcpy(&v.f, b, 8);
            else {
                float x;
                std::memcpy(&x, b, 4);
                v.f = x;
            }
            v.i = static_cast<int64_t>(v.f);
            break;
        case KILN_CV_TIME:
            std::memcpy(&v.i, b, 8);
            v.f = static_cast<double>(v.i) / 1e9;
            break;
        case KILN_CV_GUID:
            std::memcpy(v.guid, b, 16);
            break;
        default:
            break;
    }
    return v;
}

double as_double(const KilnContentValue& v) {
    switch (v.type) {
        case KILN_CV_BOOL: case KILN_CV_INT: return static_cast<double>(v.i);
        case KILN_CV_TIME: return v.f != 0 ? v.f : static_cast<double>(v.i) / 1e9;
        default: return v.f;
    }
}

bool encode(const Field& f, const KilnContentValue& v, uint8_t* b, std::string* why) {
    auto fail = [&](const std::string& s) {
        if (why) *why = s;
        return false;
    };
    if (!f.writable) return fail(f.path + " is read-only");
    // The incoming value as a number (INT keeps exact integers).
    const bool integral_in = v.type == KILN_CV_INT || v.type == KILN_CV_BOOL;
    double d = v.type == KILN_CV_TIME ? (v.f != 0 ? v.f : static_cast<double>(v.i) / 1e9)
                                      : (integral_in ? static_cast<double>(v.i) : v.f);
    if (!std::isfinite(d)) return fail("not a finite number");
    switch (f.vt) {
        case KILN_CV_BOOL:
            b[0] = d != 0 ? 1 : 0;
            return true;
        case KILN_CV_INT: {
            if (integral_in && f.bytes == 8) {
                std::memcpy(b, &v.i, 8);
                return true;
            }
            const double r = std::nearbyint(d);
            const int bits = f.bytes * 8;
            double lo, hi;
            if (f.is_signed) {
                hi = std::ldexp(1.0, bits - 1) - 1;
                lo = -std::ldexp(1.0, bits - 1);
            } else {
                hi = std::ldexp(1.0, bits) - 1;
                lo = 0;
            }
            if (r < lo || r > hi) {
                char m[160];
                std::snprintf(m, sizeof m, "%g does not fit %s (%s%d-bit: %.0f..%.0f)", d, f.path.c_str(),
                              f.is_signed ? "signed " : "", bits, lo, hi);
                return fail(m);
            }
            const int64_t iv = static_cast<int64_t>(r);
            const uint64_t u = f.is_signed ? static_cast<uint64_t>(iv) : static_cast<uint64_t>(r);
            std::memcpy(b, &u, f.bytes);
            return true;
        }
        case KILN_CV_FLOAT:
            if (f.f64) std::memcpy(b, &d, 8);
            else {
                const float x = static_cast<float>(d);
                std::memcpy(b, &x, 4);
            }
            return true;
        case KILN_CV_TIME: {
            if (d < 0 || d > 1e9) return fail("time out of range (0..1e9 s)");
            const int64_t ns = v.type == KILN_CV_TIME && v.f == 0 ? v.i : static_cast<int64_t>(std::llround(d * 1e9));
            std::memcpy(b, &ns, 8);
            return true;
        }
        default:
            return fail(f.path + " cannot be written");
    }
}

std::string show(const Field& f, const KilnContentValue& v) {
    char b[96];
    switch (f.vt) {
        case KILN_CV_BOOL: return v.i ? "true" : "false";
        case KILN_CV_INT:
            if (f.enum_kind == KILN_CONTENT_ENUM && f.en)
                for (auto& [n, x] : f.en->values)
                    if (x == v.i) return n;
            if (f.enum_kind == KILN_CONTENT_FLAGS) {
                std::snprintf(b, sizeof b, "0x%llx", static_cast<unsigned long long>(v.i));
                return b;
            }
            if (!f.is_signed && f.bytes == 8) std::snprintf(b, sizeof b, "%llu", static_cast<unsigned long long>(v.i));
            else std::snprintf(b, sizeof b, "%lld", static_cast<long long>(v.i));
            return b;
        case KILN_CV_FLOAT: std::snprintf(b, sizeof b, "%g", v.f); return b;
        case KILN_CV_TIME: std::snprintf(b, sizeof b, "%gs", v.f); return b;
        case KILN_CV_GUID: return guid_text(v.guid);
        default: return "?";
    }
}

}  // namespace kc
