// SPDX-License-Identifier: MIT
// kiln.creature_control: the layout guard (types_guard.h). Resolves every path of kGuardPaths through kiln.types.
#include "types_guard.h"

#include <cstdio>
#include <cstring>
#include <string_view>

namespace kcc {

namespace {

KilnStr ks(std::string_view s) { return KilnStr{s.data(), s.size()}; }
std::string_view sv(KilnStr s) { return s.ptr ? std::string_view(s.ptr, s.len) : std::string_view(); }

bool info(const KilnTypesApi* t, const KilnType* type, KilnTypeInfo* out) {
    *out = KilnTypeInfo{};
    out->size = sizeof *out;
    return type && t->type_info(type, out) == KILN_OK;
}

// A field of `type` or of its base classes (a struct's inner), offset from `type`'s start. A typedef is looked
// through to what it names.
bool field(const KilnTypesApi* t, const KilnType* type, std::string_view name, const KilnType** ftype, uint64_t* off) {
    for (int depth = 0; type && depth < 16; ++depth) {
        KilnTypeInfo ti{};
        if (!info(t, type, &ti)) return false;
        if (ti.kind == KILN_TYPE_TYPEDEF) {
            type = ti.inner;
            continue;
        }
        for (uint32_t i = 0; i < ti.field_count; ++i) {
            KilnTypeField f{};
            if (t->type_field(type, i, &f) == KILN_OK && sv(f.name) == name) {
                *ftype = f.type;
                *off = f.offset;  // base classes start at 0 of the derived struct
                return true;
            }
        }
        if (ti.kind != KILN_TYPE_STRUCT) return false;
        type = ti.inner;  // the base class
    }
    return false;
}

// Resolves one table entry; *found is what the game has (offset, size or value). False: it does not resolve.
bool resolve(const KilnTypesApi* t, const GuardPath& p, uint64_t* found, uint32_t* found_size, std::string* what) {
    *found = 0;
    *found_size = 0;
    const KilnType* type = t->type_by_name(ks(p.type));
    if (!type) return *what = "no such type", false;
    KilnTypeInfo ti{};
    if (!info(t, type, &ti)) return *what = "type_info failed", false;
    std::string_view path = p.path;
    if (path.empty()) {
        *found = ti.type_size;
        return true;
    }
    if (path.substr(0, 2) == "::") {
        const std::string_view name = path.substr(2);
        for (uint32_t i = 0; i < ti.field_count; ++i) {
            KilnStr n{};
            int64_t v = 0;
            if (t->type_enum(type, i, &n, &v) == KILN_OK && sv(n) == name) {
                *found = static_cast<uint64_t>(v);
                return true;
            }
        }
        return *what = "no such value", false;
    }
    uint64_t off = 0;
    while (!path.empty()) {
        const size_t dot = path.find('.');
        const std::string_view seg = path.substr(0, dot);
        const KilnType* ft = nullptr;
        uint64_t fo = 0;
        if (!field(t, type, seg, &ft, &fo)) return *what = "no field " + std::string(seg), false;
        off += fo;
        type = ft;
        path = dot == std::string_view::npos ? std::string_view() : path.substr(dot + 1);
    }
    KilnTypeInfo leaf{};
    if (!info(t, type, &leaf)) return *what = "leaf type_info failed", false;
    *found = off;
    *found_size = leaf.type_size;
    return true;
}

}  // namespace

GuardResult run_guard(const KilnTypesApi* types, Layout* out) {
    GuardResult r;
    r.total = static_cast<uint32_t>(sizeof kGuardPaths / sizeof kGuardPaths[0]);
    bool broker = true, footprint = true;
    const bool usable = types && types->header.size >= sizeof(KilnTypesApi) && types->type_by_name && types->type_info &&
                        types->type_field && types->type_enum;
    for (const GuardPath& p : kGuardPaths) {
        uint64_t found = 0;
        uint32_t size = 0;
        std::string what;
        const bool resolved = usable && resolve(types, p, &found, &size, &what);
        const bool ok = resolved && found == p.expect && size == p.size;
        char item[224];
        const char* sep = p.path[0] == ':' || p.path[0] == 0 ? "" : ".";
        if (ok) {
            std::snprintf(item, sizeof item, "%s%s%s ok", p.type, sep, p.path[0] ? p.path : " size");
            ++r.matched;
        } else if (!resolved) {
            std::snprintf(item, sizeof item, "%s%s%s MISSING (%s)", p.type, sep, p.path[0] ? p.path : " size",
                          usable ? what.c_str() : "kiln.types is not loaded");
        } else {
            std::snprintf(item, sizeof item, "%s%s%s MISMATCH (0x%llx/%u, expected 0x%x/%u)", p.type, sep,
                          p.path[0] ? p.path : " size", static_cast<unsigned long long>(found), size, p.expect, p.size);
        }
        if (!r.line.empty()) r.line += "; ";
        r.line += item;
        if (!ok) {
            if (p.use == GuardUse::Broker) {
                if (broker) r.why = item;
                broker = false;
            } else {
                footprint = false;
            }
        }
    }
    r.broker_ok = broker;
    r.footprint_ok = footprint;
    if (out) *out = Layout{};  // the table's values: used only when broker_ok
    return r;
}

}  // namespace kcc
