// SPDX-License-Identifier: MIT
// kiln.creatures: field paths inside template components through the game's own reflection (TypeByHash; the same
// type records kiln.content reads: docs/re/content.md). A path is resolved on the running build, so private copies
// never depend on offsets compiled into Kiln. Typed values are parsed from / formatted as text.
#include <charconv>
#include <cmath>
#include <cstdio>

#include "internal.h"

namespace kcr {

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

template <class T>
bool rd(uintptr_t a, T& v) {
    return seh_read(reinterpret_cast<const void*>(a), &v, sizeof v);
}

std::string read_text(uintptr_t p, uint64_t n) {
    if (!p || n == 0 || n > 512) return {};
    std::string s(static_cast<size_t>(n), '\0');
    if (!seh_read(reinterpret_cast<const void*>(p), s.data(), s.size())) return {};
    return s;
}

bool load(uintptr_t p, Rec* r) {
    if (!p) return false;
    uintptr_t qn = 0;
    uint64_t ql = 0;
    uint32_t kind = 0;
    if (!rd(p + L.type_qname, qn) || !rd(p + L.type_qname + 8, ql) || !rd(p + L.type_size, r->size) ||
        !rd(p + L.type_count, r->count) || !rd(p + L.type_kind, kind) || !rd(p + L.type_inner, r->inner) ||
        !rd(p + L.type_fields, r->fields) || !rd(p + L.type_enums, r->enums))
        return false;
    r->p = p;
    r->kind = kind & 0xFF;
    r->name = read_text(qn, ql);
    return !r->name.empty() && r->size < (1u << 24) && r->count < 100000;
}

bool resolve_typedef(uintptr_t p, Rec* out) {
    for (int i = 0; i < 8; ++i) {
        if (!load(p, out)) return false;
        if (out->kind != K_TYPEDEF) return true;
        p = out->inner;
    }
    return false;
}

bool is_int_kind(uint32_t k) { return k >= K_U8 && k <= K_S64; }
bool is_signed_kind(uint32_t k) { return k == K_S8 || k == K_S16 || k == K_S32 || k == K_S64; }

// Classifies a (typedef-resolved) record as a scalar field; false for aggregates.
bool scalar_of(const Rec& r, const std::string& display, FieldRef* f) {
    f->type_name = display;
    switch (r.kind) {
        case K_BOOL: f->kind = VKind::Bool; f->bytes = static_cast<uint8_t>(r.size); return r.size == 1;
        case K_F32: case K_F64:
            f->kind = VKind::Float;
            f->bytes = static_cast<uint8_t>(r.size);
            f->f64 = r.size == 8;
            return true;
        case K_ENUM: {
            f->kind = VKind::Enum;
            f->bytes = static_cast<uint8_t>(r.size);
            f->enum_type = r.p;
            Rec under;
            if (r.inner && resolve_typedef(r.inner, &under)) f->is_signed = is_signed_kind(under.kind);
            return f->bytes == 1 || f->bytes == 2 || f->bytes == 4 || f->bytes == 8;
        }
        case K_REF: case K_GUID:
            f->kind = VKind::Guid;
            f->bytes = 16;
            return r.size == 16;
        default: break;
    }
    if (is_int_kind(r.kind)) {
        f->kind = VKind::Int;
        f->bytes = static_cast<uint8_t>(r.size);
        f->is_signed = is_signed_kind(r.kind);
        return f->bytes == 1 || f->bytes == 2 || f->bytes == 4 || f->bytes == 8;
    }
    if (r.kind >= K_BITMASK8 && r.kind <= K_BITMASK64) {
        f->kind = VKind::Flags;
        f->bytes = static_cast<uint8_t>(r.size);
        Rec en;
        if (r.inner && resolve_typedef(r.inner, &en) && en.kind == K_ENUM) f->enum_type = en.p;
        return f->bytes == 1 || f->bytes == 2 || f->bytes == 4 || f->bytes == 8;
    }
    if (r.kind == K_STRUCT && r.name == "keen::Time" && r.size == 8) {
        f->kind = VKind::Time;
        f->bytes = 8;
        f->is_signed = true;
        return true;
    }
    return false;
}

// A struct of exactly one scalar field (keen::HashKey32 {u32 value}) reads as that scalar.
bool single_scalar(const Rec& r, FieldRef* f, uint32_t* extra) {
    if (r.kind != K_STRUCT || r.count != 1 || !r.fields || r.name == "keen::Time") return false;
    uintptr_t tp = 0;
    uint64_t off = 0;
    if (!rd(r.fields + L.field_type, tp) || !rd(r.fields + L.field_offset, off)) return false;
    Rec in;
    if (!resolve_typedef(tp, &in)) return false;
    if (!(in.kind >= K_BOOL && in.kind <= K_F64)) return false;
    if (!scalar_of(in, r.name, f)) return false;
    *extra = static_cast<uint32_t>(off);
    return true;
}

bool field_named(const Rec& r, std::string_view name, uintptr_t* type, uint32_t* off) {
    for (uint32_t i = 0; i < r.count; ++i) {
        const uintptr_t fr = r.fields + static_cast<uintptr_t>(i) * L.field_stride;
        uintptr_t np = 0, tp = 0;
        uint64_t nl = 0, o = 0;
        if (!rd(fr, np) || !rd(fr + 8, nl) || !rd(fr + L.field_type, tp) || !rd(fr + L.field_offset, o)) return false;
        const std::string n = read_text(np, nl);
        if (n.size() == name.size() && _strnicmp(n.data(), name.data(), n.size()) == 0) {
            *type = tp;
            *off = static_cast<uint32_t>(o);
            return true;
        }
    }
    // a struct's `inner` is its base, at offset 0
    Rec base;
    if (r.kind == K_STRUCT && r.inner && load(r.inner, &base) && base.kind == K_STRUCT) return field_named(base, name, type, off);
    return false;
}

std::string lower(std::string_view s) {
    std::string o(s);
    for (char& c : o) c = static_cast<char>(c >= 'A' && c <= 'Z' ? c + 32 : c);
    return o;
}

std::string trim(std::string_view s) {
    size_t a = 0, b = s.size();
    while (a < b && (s[a] == ' ' || s[a] == '\t')) ++a;
    while (b > a && (s[b - 1] == ' ' || s[b - 1] == '\t')) --b;
    return std::string(s.substr(a, b - a));
}

bool parse_int(std::string_view s, int64_t* out) {
    std::string t = trim(s);
    if (t.empty()) return false;
    bool neg = false;
    size_t i = 0;
    if (t[0] == '-' || t[0] == '+') neg = t[0] == '-', i = 1;
    int base = 10;
    if (t.size() > i + 2 && t[i] == '0' && (t[i + 1] == 'x' || t[i + 1] == 'X')) base = 16, i += 2;
    uint64_t v = 0;
    auto r = std::from_chars(t.data() + i, t.data() + t.size(), v, base);
    if (r.ec != std::errc() || r.ptr != t.data() + t.size()) return false;
    *out = neg ? -static_cast<int64_t>(v) : static_cast<int64_t>(v);
    return true;
}

bool parse_double(std::string_view s, double* out) {
    std::string t = trim(s);
    if (t.empty()) return false;
    char* end = nullptr;
    *out = std::strtod(t.c_str(), &end);
    return end && *end == 0 && std::isfinite(*out);
}

void store_int(uint8_t* dst, uint8_t bytes, int64_t v) {
    switch (bytes) {
        case 1: { const uint8_t x = static_cast<uint8_t>(v); std::memcpy(dst, &x, 1); break; }
        case 2: { const uint16_t x = static_cast<uint16_t>(v); std::memcpy(dst, &x, 2); break; }
        case 4: { const uint32_t x = static_cast<uint32_t>(v); std::memcpy(dst, &x, 4); break; }
        default: std::memcpy(dst, &v, 8); break;
    }
}

int64_t load_int(const uint8_t* src, uint8_t bytes, bool sgn) {
    switch (bytes) {
        case 1: { uint8_t x; std::memcpy(&x, src, 1); return sgn ? static_cast<int8_t>(x) : x; }
        case 2: { uint16_t x; std::memcpy(&x, src, 2); return sgn ? static_cast<int16_t>(x) : x; }
        case 4: { uint32_t x; std::memcpy(&x, src, 4); return sgn ? static_cast<int32_t>(x) : x; }
        default: { int64_t x; std::memcpy(&x, src, 8); return x; }
    }
}

}  // namespace

std::string component_qname(std::string_view c) {
    return c.find("::") != std::string_view::npos ? std::string(c) : "keen::ecs::" + std::string(c);
}

bool type_info(std::string_view qname, TypeInfo* out, std::string* err) {
    if (!F.reflection) {
        *err = "the game's reflection is unavailable on this build";
        return false;
    }
    uintptr_t t = 0;
    if (!seh_type_by_hash(fnv1a(qname), &t) || !t) {
        *err = std::string(qname) + ": not in the game's reflection";
        return false;
    }
    Rec r;
    if (!load(t, &r) || r.name != qname || r.kind != K_STRUCT) {
        *err = std::string(qname) + ": reflection record looks wrong";
        return false;
    }
    uint32_t h = 0;
    if (!rd(t + L.type_hash, h) || h != fnv1a(qname)) {
        *err = std::string(qname) + ": type hash mismatch";
        return false;
    }
    out->qname = std::string(qname);
    out->size = r.size;
    out->record = t;
    return true;
}

bool resolve_path(const TypeInfo& t, std::string_view path, FieldRef* out, std::string* err) {
    Rec cur;
    if (!load(t.record, &cur)) {
        *err = "reflection record unreadable";
        return false;
    }
    uint32_t base = 0;
    std::string display = cur.name;
    size_t i = 0;
    while (true) {
        // one name
        size_t j = i;
        while (j < path.size() && path[j] != '.' && path[j] != '[') ++j;
        const std::string_view name = path.substr(i, j - i);
        if (name.empty()) {
            *err = "empty field name in '" + std::string(path) + "'";
            return false;
        }
        if (cur.kind != K_STRUCT || (!cur.fields && !cur.inner)) {
            *err = "'" + std::string(path.substr(0, i)) + "' is not a struct";
            return false;
        }
        uintptr_t ft = 0;
        uint32_t fo = 0;
        if (!field_named(cur, name, &ft, &fo)) {
            *err = cur.name + " has no field '" + std::string(name) + "'";
            return false;
        }
        base += fo;
        Rec fr;
        if (!load(ft, &fr)) {
            *err = "field type unreadable";
            return false;
        }
        display = fr.name;
        if (fr.kind == K_TYPEDEF && !resolve_typedef(fr.inner, &fr)) {
            *err = "typedef unreadable";
            return false;
        }
        // indexes
        while (j < path.size() && path[j] == '[') {
            const size_t k = path.find(']', j);
            if (k == std::string_view::npos) {
                *err = "missing ']'";
                return false;
            }
            int64_t idx = 0;
            if (!parse_int(path.substr(j + 1, k - j - 1), &idx) || idx < 0) {
                *err = "bad index in '" + std::string(path) + "'";
                return false;
            }
            if (fr.kind != K_ARRAY || !fr.inner) {
                *err = "'" + std::string(name) + "' is not a fixed array (" + fr.name + ")";
                return false;
            }
            if (static_cast<uint64_t>(idx) >= fr.count) {
                *err = "index " + std::to_string(idx) + " out of range (" + std::to_string(fr.count) + ")";
                return false;
            }
            Rec el;
            if (!load(fr.inner, &el)) return false;
            display = el.name;
            if (el.kind == K_TYPEDEF && !resolve_typedef(el.inner, &el)) return false;
            const uint32_t esize = el.size ? el.size : fr.size / fr.count;
            base += static_cast<uint32_t>(idx) * esize;
            fr = el;
            j = k + 1;
        }
        if (j >= path.size()) {
            FieldRef f;
            uint32_t extra = 0;
            if (scalar_of(fr, display, &f)) {
                f.offset = base;
            } else if (single_scalar(fr, &f, &extra)) {
                f.offset = base + extra;
            } else {
                *err = "'" + std::string(path) + "' is " + fr.name + ", not a value (give a field inside it)";
                return false;
            }
            if (f.offset + f.bytes > t.size) {
                *err = "field outside the component";
                return false;
            }
            *out = f;
            return true;
        }
        if (path[j] != '.') {
            *err = "bad path '" + std::string(path) + "'";
            return false;
        }
        cur = fr;
        i = j + 1;
    }
}

bool field_layout(const TypeInfo& t, std::string_view name, uint32_t* offset, uint32_t* size) {
    Rec cur;
    if (!load(t.record, &cur)) return false;
    uint32_t base = 0;
    size_t i = 0;
    while (true) {
        const size_t j = name.find('.', i);
        uintptr_t ft = 0;
        uint32_t fo = 0;
        if (!field_named(cur, name.substr(i, j == std::string_view::npos ? j : j - i), &ft, &fo)) return false;
        base += fo;
        Rec fr;
        if (!load(ft, &fr)) return false;
        if (j == std::string_view::npos) {
            *offset = base;
            *size = fr.size;
            return true;
        }
        if (fr.kind == K_TYPEDEF && !resolve_typedef(fr.inner, &fr)) return false;
        if (fr.kind != K_STRUCT) return false;
        cur = fr;
        i = j + 1;
    }
}

bool enum_value(uintptr_t enum_type, std::string_view name, int64_t* out) {
    Rec r;
    if (!enum_type || !load(enum_type, &r) || !r.enums) return false;
    for (uint32_t i = 0; i < r.count && i < 4096; ++i) {
        const uintptr_t e = r.enums + static_cast<uintptr_t>(i) * L.enum_stride;
        uintptr_t np = 0;
        uint64_t nl = 0, v = 0;
        if (!rd(e, np) || !rd(e + 8, nl) || !rd(e + L.enum_value, v)) return false;
        const std::string n = read_text(np, nl);
        if (n.size() == name.size() && _strnicmp(n.data(), name.data(), n.size()) == 0) {
            *out = static_cast<int64_t>(v);
            return true;
        }
    }
    return false;
}

std::string enum_name(uintptr_t enum_type, int64_t v) {
    Rec r;
    if (!enum_type || !load(enum_type, &r) || !r.enums) return {};
    for (uint32_t i = 0; i < r.count && i < 4096; ++i) {
        const uintptr_t e = r.enums + static_cast<uintptr_t>(i) * L.enum_stride;
        uintptr_t np = 0;
        uint64_t nl = 0, ev = 0;
        if (!rd(e, np) || !rd(e + 8, nl) || !rd(e + L.enum_value, ev)) return {};
        if (static_cast<int64_t>(ev) == v) return read_text(np, nl);
    }
    return {};
}

namespace {
// Flags: the enum values are bit indexes when every value < bits; else masks (both forms appear in reflection).
uint64_t flag_bit(uintptr_t enum_type, std::string_view name, uint8_t bytes, bool* ok) {
    int64_t v = 0;
    *ok = enum_value(enum_type, name, &v);
    if (!*ok) {
        *ok = parse_int(name, &v);
        return *ok ? static_cast<uint64_t>(v) : 0;
    }
    return v >= 0 && v < bytes * 8 ? (1ull << v) : static_cast<uint64_t>(v);
}
}  // namespace

bool parse_value(const FieldRef& f, std::string_view text, uint8_t* dst, std::string* err) {
    const std::string t = trim(text);
    switch (f.kind) {
        case VKind::Bool: {
            const std::string l = lower(t);
            if (l == "true" || l == "1" || l == "yes" || l == "on") dst[0] = 1;
            else if (l == "false" || l == "0" || l == "no" || l == "off") dst[0] = 0;
            else return *err = "expected true or false", false;
            return true;
        }
        case VKind::Int: {
            int64_t v = 0;
            double d = 0;
            if (!parse_int(t, &v)) {
                if (!parse_double(t, &d) || d != std::floor(d)) return *err = "expected an integer", false;
                v = static_cast<int64_t>(d);
            }
            store_int(dst, f.bytes, v);
            return true;
        }
        case VKind::Float: {
            double d = 0;
            if (!parse_double(t, &d)) return *err = "expected a number", false;
            if (f.f64) std::memcpy(dst, &d, 8);
            else {
                const float x = static_cast<float>(d);
                std::memcpy(dst, &x, 4);
            }
            return true;
        }
        case VKind::Enum: {
            int64_t v = 0;
            if (!enum_value(f.enum_type, t, &v) && !parse_int(t, &v))
                return *err = "unknown " + f.type_name + " value '" + t + "'", false;
            store_int(dst, f.bytes, v);
            return true;
        }
        case VKind::Flags: {
            uint64_t cur = static_cast<uint64_t>(load_int(dst, f.bytes, false));
            int64_t whole = 0;
            if (parse_int(t, &whole)) {
                store_int(dst, f.bytes, whole);
                return true;
            }
            const bool relative = !t.empty() && (t[0] == '+' || t[0] == '-');
            uint64_t v = relative ? cur : 0;
            size_t i = 0;
            while (i < t.size()) {
                while (i < t.size() && (t[i] == ' ' || t[i] == ',' || t[i] == '|')) ++i;
                if (i >= t.size()) break;
                char op = '=';
                if (t[i] == '+' || t[i] == '-') op = t[i++];
                size_t j = i;
                while (j < t.size() && t[j] != ' ' && t[j] != ',' && t[j] != '|' && t[j] != '+' && t[j] != '-') ++j;
                bool ok = false;
                const uint64_t b = flag_bit(f.enum_type, t.substr(i, j - i), f.bytes, &ok);
                if (!ok) return *err = "unknown flag '" + t.substr(i, j - i) + "' of " + f.type_name, false;
                if (op == '-') v &= ~b;
                else v |= b;
                i = j;
            }
            store_int(dst, f.bytes, static_cast<int64_t>(v));
            return true;
        }
        case VKind::Time: {
            std::string n = t;
            double scale = 1e9;
            if (n.size() > 2 && lower(n.substr(n.size() - 2)) == "ms") scale = 1e6, n.resize(n.size() - 2);
            else if (n.size() > 1 && (n.back() == 's' || n.back() == 'S')) n.pop_back();
            double d = 0;
            if (!parse_double(n, &d)) return *err = "expected seconds", false;
            const int64_t ns = static_cast<int64_t>(std::llround(d * scale));
            std::memcpy(dst, &ns, 8);
            return true;
        }
        case VKind::Guid: {
            uint8_t g[16];
            if (!parse_guid(t, g)) return *err = "expected a GUID", false;
            std::memcpy(dst, g, 16);
            return true;
        }
        default: return *err = "unsupported field type", false;
    }
}

std::string format_value(const FieldRef& f, const uint8_t* src) {
    char b[64];
    switch (f.kind) {
        case VKind::Bool: return src[0] ? "true" : "false";
        case VKind::Int: return std::to_string(load_int(src, f.bytes, f.is_signed));
        case VKind::Float: {
            double d = 0;
            if (f.f64) std::memcpy(&d, src, 8);
            else {
                float x;
                std::memcpy(&x, src, 4);
                d = x;
            }
            std::snprintf(b, sizeof b, "%.9g", d);
            return b;
        }
        case VKind::Enum: {
            const int64_t v = load_int(src, f.bytes, f.is_signed);
            std::string n = enum_name(f.enum_type, v);
            return n.empty() ? std::to_string(v) : n;
        }
        case VKind::Flags: {
            const uint64_t v = static_cast<uint64_t>(load_int(src, f.bytes, false));
            std::string out;
            for (int bit = 0; bit < f.bytes * 8; ++bit) {
                if (!((v >> bit) & 1)) continue;
                std::string n = enum_name(f.enum_type, bit);
                if (n.empty()) n = "0x" + [&] { char h[24]; std::snprintf(h, sizeof h, "%llx", 1ull << bit); return std::string(h); }();
                if (!out.empty()) out += "|";
                out += n;
            }
            return out.empty() ? "0" : out;
        }
        case VKind::Time: {
            int64_t ns = 0;
            std::memcpy(&ns, src, 8);
            std::snprintf(b, sizeof b, "%.9g", static_cast<double>(ns) / 1e9);
            return b;
        }
        case VKind::Guid: return guid_text(src);
        default: return "?";
    }
}

namespace {
void json_str(std::string& o, std::string_view s) {
    o += '"';
    for (char c : s) {
        if (c == '"' || c == '\\') o += '\\';
        o += c;
    }
    o += '"';
}

struct Lister {
    std::string* out;
    int budget = 6000;
    void walk(const Rec& r, uint32_t base, const std::string& prefix, int depth) {
        for (uint32_t i = 0; i < r.count && budget > 0; ++i) {
            const uintptr_t fr = r.fields + static_cast<uintptr_t>(i) * L.field_stride;
            uintptr_t np = 0, tp = 0;
            uint64_t nl = 0, off = 0;
            if (!rd(fr, np) || !rd(fr + 8, nl) || !rd(fr + L.field_type, tp) || !rd(fr + L.field_offset, off)) return;
            const std::string name = read_text(np, nl);
            if (name.empty()) continue;
            emit(tp, base + static_cast<uint32_t>(off), prefix + name, depth);
        }
    }
    void emit(uintptr_t tp, uint32_t off, const std::string& path, int depth) {
        Rec r;
        if (!load(tp, &r)) return;
        const std::string display = r.name;
        if (r.kind == K_TYPEDEF && !resolve_typedef(r.inner, &r)) return;
        FieldRef f;
        uint32_t extra = 0;
        if (scalar_of(r, display, &f) || single_scalar(r, &f, &extra)) {
            if (--budget < 0) return;
            if (out->size() > 1) *out += ',';
            *out += "{\"path\":";
            json_str(*out, path);
            *out += ",\"type\":";
            json_str(*out, display);
            *out += ",\"offset\":" + std::to_string(off + extra) + ",\"bytes\":" + std::to_string(f.bytes) + "}";
            return;
        }
        if (r.kind == K_STRUCT && r.fields && depth < 6) {
            walk(r, off, path + ".", depth + 1);
        } else if (r.kind == K_ARRAY && r.inner && r.count > 0 && r.count <= 64) {
            Rec el;
            if (!load(r.inner, &el)) return;
            Rec elr = el;
            if (el.kind == K_TYPEDEF && !resolve_typedef(el.inner, &elr)) return;
            const uint32_t es = elr.size ? elr.size : r.size / r.count;
            for (uint32_t k = 0; k < r.count; ++k) emit(r.inner, off + k * es, path + "[" + std::to_string(k) + "]", depth + 1);
        }
    }
};
}  // namespace

std::string fields_json(const TypeInfo& t) {
    std::string o = "[";
    Rec r;
    if (load(t.record, &r)) {
        Lister l{&o};
        l.walk(r, 0, "", 0);
    }
    o += "]";
    return o;
}

}  // namespace kcr
