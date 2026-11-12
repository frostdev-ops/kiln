// SPDX-License-Identifier: MIT
// typedump: writes the layout of the game's ECS component and event types, read from an exe file (no game running),
// as sigdb/types/<side>/<sha256>.json (dump schema 2, docs/kiln/typegen.md section 2).
//
//   typedump --side <server|client> --out <dir> <exe>
//
// Structure only: type and field names, kinds, sizes, alignments, offsets, hashes and enum values of the inline
// closure of the two ECS lists. The type descriptors also hold a flags byte and a pointer to each type's default
// value; this tool never reads either, and writes no address, RVA, image base or table index. The exe is opened
// with a plain read-only stream; the one file written is the output named above.
#include "../../../native/services/types/scan.h"

#include <cctype>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <map>
#include <set>
#include <sstream>
#include <stdexcept>

using namespace ktypes;

namespace {

[[noreturn]] void fail(const std::string& why) { throw std::runtime_error(why); }

// ---- SHA-256 (FIPS 180-4), for the output name ----
struct Sha256 {
    uint32_t h[8] = {0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19};
    static uint32_t rotr(uint32_t x, int n) { return (x >> n) | (x << (32 - n)); }
    void block(const uint8_t* p) {
        static constexpr uint32_t k[64] = {
            0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, 0xd807aa98,
            0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786,
            0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da, 0x983e5152, 0xa831c66d, 0xb00327c8,
            0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13,
            0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819,
            0xd6990624, 0xf40e3585, 0x106aa070, 0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a,
            0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7,
            0xc67178f2};
        uint32_t w[64];
        for (int i = 0; i < 16; ++i)
            w[i] = uint32_t(p[4 * i]) << 24 | uint32_t(p[4 * i + 1]) << 16 | uint32_t(p[4 * i + 2]) << 8 | p[4 * i + 3];
        for (int i = 16; i < 64; ++i) {
            const uint32_t s0 = rotr(w[i - 15], 7) ^ rotr(w[i - 15], 18) ^ (w[i - 15] >> 3);
            const uint32_t s1 = rotr(w[i - 2], 17) ^ rotr(w[i - 2], 19) ^ (w[i - 2] >> 10);
            w[i] = w[i - 16] + s0 + w[i - 7] + s1;
        }
        uint32_t a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
        for (int i = 0; i < 64; ++i) {
            const uint32_t t1 = hh + (rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25)) + ((e & f) ^ (~e & g)) + k[i] + w[i];
            const uint32_t t2 = (rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22)) + ((a & b) ^ (a & c) ^ (b & c));
            hh = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
        }
        h[0] += a; h[1] += b; h[2] += c; h[3] += d; h[4] += e; h[5] += f; h[6] += g; h[7] += hh;
    }
    static std::string hex(const std::vector<uint8_t>& data) {
        Sha256 s;
        const uint64_t n = data.size();
        uint64_t i = 0;
        for (; i + 64 <= n; i += 64) s.block(data.data() + i);
        uint8_t tail[128] = {};
        const size_t rest = static_cast<size_t>(n - i);
        if (rest) std::memcpy(tail, data.data() + i, rest);
        tail[rest] = 0x80;
        const size_t len = rest + 9 <= 64 ? 64 : 128;
        for (int j = 0; j < 8; ++j) tail[len - 1 - j] = uint8_t((n * 8) >> (8 * j));
        s.block(tail);
        if (len == 128) s.block(tail + 64);
        std::string out;
        char buf[9];
        for (uint32_t v : s.h) { std::snprintf(buf, sizeof buf, "%08x", v); out += buf; }
        return out;
    }
};

// ---- PE mapping (as map_pe_bytes in native/services/types/types_test.cpp): sections laid out at their RVAs ----
struct Mapped {
    std::vector<uint8_t> mem;
    Image im;
};

bool map_pe_bytes(const std::vector<uint8_t>& raw, Mapped& m) {
    if (raw.size() < 0x40) return false;
    const Range file{0, raw.size()};
    auto u16 = [&](size_t o) { uint16_t v = 0; if (file.has(o, 2)) std::memcpy(&v, raw.data() + o, 2); return v; };
    auto u32 = [&](size_t o) { uint32_t v = 0; if (file.has(o, 4)) std::memcpy(&v, raw.data() + o, 4); return v; };
    auto u64 = [&](size_t o) { uint64_t v = 0; if (file.has(o, 8)) std::memcpy(&v, raw.data() + o, 8); return v; };
    const size_t pe = u32(0x3c);
    if (u16(0) != 0x5a4d || !file.has(pe, 24) || u32(pe) != 0x4550 || u16(pe + 4) != 0x8664) return false;
    const size_t nsec = u16(pe + 6), opt = pe + 24, optsz = u16(pe + 20);
    if (!nsec || nsec > 96 || optsz < 0xf0 || !file.has(opt, optsz) || u16(opt) != 0x20b ||
        !file.has(opt + optsz, nsec * 40)) return false;
    const uint64_t base = u64(opt + 24);
    const uint32_t image_size = u32(opt + 56);
    if (image_size < 0x1000 || image_size > 1024 * 1024 * 1024 || base > UINT64_MAX - image_size) return false;
    m.mem.assign(image_size, 0);
    m.im.base = base;
    m.im.all = {base, base + image_size};
    for (size_t i = 0; i < nsec; ++i) {
        const size_t s = opt + optsz + 40 * i;
        char name[9] = {};
        std::memcpy(name, raw.data() + s, 8);
        const uint32_t vsize = u32(s + 8), va = u32(s + 12), rsize = u32(s + 16), rptr = u32(s + 20);
        const uint32_t length = vsize ? vsize : rsize, n = std::min(length, rsize);
        if (!length || va > image_size || length > image_size - va || !file.has(rptr, rsize)) return false;
        if (n) std::memcpy(m.mem.data() + va, raw.data() + rptr, n);
        const Range r{base + va, base + va + length};
        if (!std::strcmp(name, ".text")) m.im.text = r;
        if (!std::strcmp(name, ".rdata")) m.im.rdata = r;
    }
    m.im.mem = m.mem.data();
    return m.im.valid();
}

std::vector<uint8_t> read_file(const std::string& path) {
    std::ifstream f(path, std::ios::binary | std::ios::in);  // read-only, no lock, no write
    if (!f) fail("cannot open " + path);
    f.seekg(0, std::ios::end);
    const auto size = f.tellg();
    if (size < 0 || size > 1024ll * 1024 * 1024) fail("unreadable or too large: " + path);
    std::vector<uint8_t> raw(static_cast<size_t>(size));
    f.seekg(0);
    if (!f.read(reinterpret_cast<char*>(raw.data()), static_cast<std::streamsize>(raw.size()))) fail("cannot read " + path);
    return raw;
}

// ---- the closure ----
struct Field {
    std::string name, type;
    uint64_t offset;
};
struct Value {
    std::string name;
    uint64_t value;
};
struct Type {
    std::string name, qname, inner;
    uint32_t hash = 0, size = 0;
    uint16_t align = 0;
    uint8_t kind = 0;
    bool has_fields = false, has_values = false;
    std::vector<Field> fields;
    std::vector<Value> values;
};

// Names are written as they are; the tool refuses anything but the characters C++ type and member names use here.
bool name_ok(std::string_view s, bool type_name) {
    if (s.empty() || s.size() > (type_name ? 128u : 64u)) return false;
    if (!(std::isalpha(static_cast<unsigned char>(s[0])) || s[0] == '_')) return false;
    for (char c : s) {
        const bool ident = std::isalnum(static_cast<unsigned char>(c)) || c == '_';
        if (!ident && !(type_name && (c == ':' || c == '<' || c == '>' || c == ','))) return false;
    }
    return true;
}

struct Dumper {
    const Image& im;
    std::unordered_set<uint64_t> table;            // every descriptor of the type table
    std::map<uint64_t, Type> by_va;                // the closure, by descriptor
    std::map<std::string, uint64_t> by_qname;      // qualified name -> descriptor, must be one to one
    std::vector<uint64_t> todo;

    std::string qname_of(uint64_t t) const { return std::string(str_at(im, t + td::qname)); }

    std::string where(uint64_t t) const {
        const std::string q = is_type(im, t) ? qname_of(t) : "";
        return q.empty() ? "a descriptor" : "\"" + q + "\"";
    }

    // A type named from inside the closure: a descriptor of the type table. Returns its qualified name.
    std::string want(uint64_t t, const std::string& from) {
        if (!is_type(im, t)) fail(from + " points at something that is not a type descriptor");
        if (!table.count(t)) fail(from + " points at " + where(t) + ", which is not in the type table");
        const std::string q = qname_of(t);
        auto [it, fresh] = by_qname.emplace(q, t);
        if (!fresh && it->second != t) fail("two descriptors share the qualified name \"" + q + "\"");
        if (!by_va.count(t)) {
            by_va[t];
            todo.push_back(t);
        }
        return q;
    }

    void read(uint64_t t) {
        Type& y = by_va[t];
        y.qname = qname_of(t);
        y.name = std::string(str_at(im, t + td::name));
        const std::string at = "\"" + y.qname + "\"";
        if (!name_ok(y.qname, true)) fail("qualified name " + at + " is not a type name");
        if (!name_ok(y.name, true)) fail(at + ": its name is not a type name");
        y.hash = im.d(t + td::hash);      // is_type has checked it equals FNV-1a of the qualified name
        y.size = im.d(t + td::size);
        y.align = im.w(t + td::align);
        y.kind = im.b(t + td::kind);
        if (y.kind < 1 || y.kind > 29) fail(at + ": kind " + std::to_string(y.kind) + " is unknown");
        if (y.align == 0 || (y.align & (y.align - 1)) || y.align > 64) fail(at + ": alignment is not a power of two up to 64");
        if (y.size > (1u << 20)) fail(at + ": size is implausible");
        const uint32_t count = im.d(t + td::count);
        const uint64_t inner = im.q(t + td::inner);
        if (y.kind >= 12 && y.kind <= 19) {
            if (inner) y.inner = want(inner, at + " inner");
            else if (y.kind != 18) fail(at + ": kind " + std::to_string(y.kind) + " has no inner type");
        }
        if (y.kind == 18) {
            y.has_fields = true;
            for (uint32_t i = 0; i < count; ++i) {
                const uint64_t e = field_entry(im, t, i);
                if (!e) fail(at + ": field " + std::to_string(i) + " fails validation");
                Field f;
                f.name = std::string(str_at(im, e));
                if (!name_ok(f.name, false)) fail(at + ": field " + std::to_string(i) + " has no plain name");
                f.offset = im.q(e + td::field_offset);
                f.type = want(im.q(e + td::field_type), at + "." + f.name);
                y.fields.push_back(std::move(f));
            }
        }
        if (y.kind == 12 || (y.kind >= 13 && y.kind <= 16 && count)) {
            y.has_values = true;
            for (uint32_t i = 0; i < count; ++i) {
                const uint64_t e = enum_entry(im, t, i);
                if (!e) fail(at + ": value " + std::to_string(i) + " fails validation");
                Value v;
                v.name = std::string(str_at(im, e));
                if (!name_ok(v.name, false)) fail(at + ": value " + std::to_string(i) + " has no plain name");
                v.value = im.q(e + td::enum_value);
                y.values.push_back(std::move(v));
            }
        }
        // Kinds 1..11 and 20..29 are leaves: nothing behind them is followed or written.
    }

    std::vector<std::string> list(uint64_t va, uint32_t n, const char* what) {
        std::vector<std::string> out;
        for (uint32_t i = 0; i < n; ++i) out.push_back(want(im.q(va + 8ull * i), std::string(what) + " list"));
        std::sort(out.begin(), out.end());
        if (std::adjacent_find(out.begin(), out.end()) != out.end()) fail(std::string("the ") + what + " list names a type twice");
        return out;
    }
};

// ---- JSON, laid out as Python's json.dumps(indent=1) plus a final newline ----
std::string q(const std::string& s) { return "\"" + s + "\""; }  // names are checked by name_ok: nothing to escape

std::string render(const std::string& side, const std::string& sha, const std::vector<std::string>& comps,
                   const std::vector<std::string>& events, const std::vector<const Type*>& types) {
    std::ostringstream o;
    auto strings = [&](const std::vector<std::string>& v) {
        if (v.empty()) { o << "[]"; return; }
        o << "[\n";
        for (size_t i = 0; i < v.size(); ++i) o << "  " << q(v[i]) << (i + 1 < v.size() ? ",\n" : "\n");
        o << " ]";
    };
    o << "{\n \"schema\": 2,\n \"side\": " << q(side) << ",\n \"exe_sha256\": " << q(sha) << ",\n \"components\": ";
    strings(comps);
    o << ",\n \"events\": ";
    strings(events);
    o << ",\n \"types\": ";
    if (types.empty()) o << "[]";
    else {
        o << "[\n";
        for (size_t i = 0; i < types.size(); ++i) {
            const Type& t = *types[i];
            char hash[11];
            std::snprintf(hash, sizeof hash, "0x%08X", t.hash);
            o << "  {\n   \"name\": " << q(t.name) << ",\n   \"qualifiedName\": " << q(t.qname) << ",\n   \"hash\": "
              << q(hash) << ",\n   \"kind\": " << unsigned(t.kind) << ",\n   \"size\": " << t.size << ",\n   \"align\": "
              << t.align;
            if (!t.inner.empty()) o << ",\n   \"inner\": " << q(t.inner);
            if (t.has_fields) {
                o << ",\n   \"fields\": ";
                if (t.fields.empty()) o << "[]";
                else {
                    o << "[\n";
                    for (size_t j = 0; j < t.fields.size(); ++j) {
                        const Field& f = t.fields[j];
                        o << "    {\n     \"name\": " << q(f.name) << ",\n     \"offset\": " << f.offset
                          << ",\n     \"type\": " << q(f.type) << "\n    }" << (j + 1 < t.fields.size() ? ",\n" : "\n");
                    }
                    o << "   ]";
                }
            }
            if (t.has_values) {
                o << ",\n   \"values\": ";
                if (t.values.empty()) o << "[]";
                else {
                    o << "[\n";
                    for (size_t j = 0; j < t.values.size(); ++j) {
                        const Value& v = t.values[j];
                        o << "    {\n     \"name\": " << q(v.name) << ",\n     \"value\": " << v.value << "\n    }"
                          << (j + 1 < t.values.size() ? ",\n" : "\n");
                    }
                    o << "   ]";
                }
            }
            o << "\n  }" << (i + 1 < types.size() ? ",\n" : "\n");
        }
        o << " ]";
    }
    o << "\n}\n";
    return o.str();
}

int usage() {
    std::fprintf(stderr, "usage: typedump --side <server|client> --out <dir> <exe>\n");
    return 2;
}

}  // namespace

int main(int argc, char** argv) {
    std::string side, out, exe;
    for (int i = 1; i < argc; ++i) {
        const std::string a = argv[i];
        if (a == "--side" && i + 1 < argc) side = argv[++i];
        else if (a == "--out" && i + 1 < argc) out = argv[++i];
        else if (!a.empty() && a[0] != '-' && exe.empty()) exe = a;
        else return usage();
    }
    if ((side != "server" && side != "client") || out.empty() || exe.empty()) return usage();
    try {
        const std::vector<uint8_t> raw = read_file(exe);
        const std::string sha = Sha256::hex(raw);
        Mapped m;
        if (!map_pe_bytes(raw, m)) fail("not a mappable x64 PE image: " + exe);
        const TypeTable tt = find_type_table(m.im);
        if (!tt.error.empty()) fail("type table: " + tt.error);
        if (!tt.components || !tt.events) fail("type table: the ecs.component or ecs.event list is missing");
        Dumper d{m.im, {}, {}, {}, {}};
        for (uint32_t i = 0; i < tt.count; ++i) d.table.insert(m.im.q(tt.table + 8ull * i));
        const auto comps = d.list(tt.components, tt.component_count, "ecs.component");
        const auto events = d.list(tt.events, tt.event_count, "ecs.event");
        while (!d.todo.empty()) {
            const uint64_t t = d.todo.back();
            d.todo.pop_back();
            d.read(t);
        }
        std::vector<const Type*> types;
        for (const auto& [qn, va] : d.by_qname) types.push_back(&d.by_va.at(va));  // std::map: sorted by name
        const std::string json = render(side, sha, comps, events, types);

        const std::filesystem::path dir = std::filesystem::path(out) / side, file = dir / (sha + ".json");
        std::filesystem::create_directories(dir);
        const std::filesystem::path tmp = dir / (sha + ".json.tmp");
        {
            std::ofstream f(tmp, std::ios::binary | std::ios::trunc);
            if (!f || !f.write(json.data(), static_cast<std::streamsize>(json.size()))) fail("cannot write " + tmp.string());
        }
        std::filesystem::rename(tmp, file);
        size_t nf = 0, nv = 0;
        for (const Type* t : types) nf += t->fields.size(), nv += t->values.size();
        std::printf("%s\n", file.string().c_str());
        std::fprintf(stderr, "typedump: %zu components, %zu events, %zu types, %zu fields, %zu values, %zu bytes\n",
                     comps.size(), events.size(), types.size(), nf, nv, json.size());
        return 0;
    } catch (const std::exception& e) {
        std::fprintf(stderr, "typedump: %s\n", e.what());
        return 1;
    }
}
