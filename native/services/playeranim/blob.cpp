// SPDX-License-Identifier: MIT
// See blob.h. The rules are tools/kfc/kfc/blob.py and blobwrite.py; where the Python reads past a buffer the way Python
// slices do (a negative offset counts from the end, a slice stops at the end), this code does the same, so that a
// graph the Python decodes into garbage is garbage here too and fails the same round-trip check.
#include "blob.h"

#include <charconv>
#include <cmath>
#include <cstdlib>
#include <cstdio>
#include <cstring>

namespace kpa {

// ---- shared rules ----

uint32_t fnv1a(const void* data, size_t n) {
    uint32_t h = 0x811c9dc5u;
    const auto* p = static_cast<const uint8_t*>(data);
    for (size_t i = 0; i < n; ++i) h = (h ^ p[i]) * 0x01000193u;
    return h;
}

std::array<uint8_t, 32> sha256(const void* data, size_t n) {
    static constexpr uint32_t k[64] = {
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74,
        0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da, 0x983e5152, 0xa831c66d,
        0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e,
        0x92722c85, 0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070, 0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5,
        0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2};
    uint32_t h[8] = {0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19};
    const auto* p = static_cast<const uint8_t*>(data);
    auto ror = [](uint32_t x, int r) { return x >> r | x << (32 - r); };
    auto block = [&](const uint8_t* b) {
        uint32_t w[64];
        for (int t = 0; t < 16; ++t) w[t] = uint32_t{b[4 * t]} << 24 | uint32_t{b[4 * t + 1]} << 16 | uint32_t{b[4 * t + 2]} << 8 | uint32_t{b[4 * t + 3]};
        for (int t = 16; t < 64; ++t) {
            const uint32_t s0 = ror(w[t - 15], 7) ^ ror(w[t - 15], 18) ^ (w[t - 15] >> 3), s1 = ror(w[t - 2], 17) ^ ror(w[t - 2], 19) ^ (w[t - 2] >> 10);
            w[t] = w[t - 16] + s0 + w[t - 7] + s1;
        }
        uint32_t a = h[0], bb = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
        for (int t = 0; t < 64; ++t) {
            const uint32_t t1 = hh + (ror(e, 6) ^ ror(e, 11) ^ ror(e, 25)) + ((e & f) ^ (~e & g)) + k[t] + w[t];
            const uint32_t t2 = (ror(a, 2) ^ ror(a, 13) ^ ror(a, 22)) + ((a & bb) ^ (a & c) ^ (bb & c));
            hh = g, g = f, f = e, e = d + t1, d = c, c = bb, bb = a, a = t1 + t2;
        }
        h[0] += a, h[1] += bb, h[2] += c, h[3] += d, h[4] += e, h[5] += f, h[6] += g, h[7] += hh;
    };
    size_t i = 0;
    for (; i + 64 <= n; i += 64) block(p + i);
    uint8_t tail[128] = {};
    const size_t rest = n - i;
    if (rest) std::memcpy(tail, p + i, rest);
    tail[rest] = 0x80;
    const size_t len = rest + 9 <= 64 ? 64 : 128;
    const uint64_t bits = static_cast<uint64_t>(n) * 8;
    for (int b = 0; b < 8; ++b) tail[len - 1 - b] = static_cast<uint8_t>(bits >> (8 * b));
    block(tail);
    if (len == 128) block(tail + 64);
    std::array<uint8_t, 32> out{};
    for (int t = 0; t < 8; ++t)
        for (int b = 0; b < 4; ++b) out[4 * t + b] = static_cast<uint8_t>(h[t] >> (24 - 8 * b));
    return out;
}

bool Guid::zero() const {
    for (uint8_t x : b)
        if (x) return false;
    return true;
}

namespace {
constexpr int kGuidOrder[16] = {3, 2, 1, 0, 5, 4, 7, 6, 8, 9, 10, 11, 12, 13, 14, 15};
int hexval(char c) {
    return c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
}
}  // namespace

std::string Guid::text() const {
    char s[37];
    std::snprintf(s, sizeof s, "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x", b[3], b[2], b[1], b[0], b[5], b[4], b[7], b[6], b[8],
                  b[9], b[10], b[11], b[12], b[13], b[14], b[15]);
    return s;
}

bool Guid::parse(std::string_view s, Guid* out) {
    if (s.size() != 36 || s[8] != '-' || s[13] != '-' || s[18] != '-' || s[23] != '-') return false;
    Guid g;
    size_t at = 0;
    for (int i = 0; i < 16; ++i) {
        if (s[at] == '-') ++at;
        const int hi = hexval(s[at]), lo = hexval(s[at + 1]);
        if (hi < 0 || lo < 0) return false;
        g.b[kGuidOrder[i]] = static_cast<uint8_t>(hi << 4 | lo);
        at += 2;
    }
    *out = g;
    return true;
}

void append_utf8(std::string* out, uint32_t cp) {
    if (cp < 0x80) {
        out->push_back(static_cast<char>(cp));
    } else if (cp < 0x800) {
        out->push_back(static_cast<char>(0xC0 | cp >> 6));
        out->push_back(static_cast<char>(0x80 | (cp & 0x3F)));
    } else if (cp < 0x10000) {
        out->push_back(static_cast<char>(0xE0 | cp >> 12));
        out->push_back(static_cast<char>(0x80 | (cp >> 6 & 0x3F)));
        out->push_back(static_cast<char>(0x80 | (cp & 0x3F)));
    } else {
        out->push_back(static_cast<char>(0xF0 | cp >> 18));
        out->push_back(static_cast<char>(0x80 | (cp >> 12 & 0x3F)));
        out->push_back(static_cast<char>(0x80 | (cp >> 6 & 0x3F)));
        out->push_back(static_cast<char>(0x80 | (cp & 0x3F)));
    }
}

std::string utf8_replace(const uint8_t* p, size_t n) {
    std::string out;
    out.reserve(n);
    size_t i = 0;
    while (i < n) {
        const uint8_t c = p[i];
        if (c < 0x80) {
            out.push_back(static_cast<char>(c));
            ++i;
            continue;
        }
        int need = 0;
        uint8_t lo = 0x80, hi = 0xBF;
        if (c >= 0xC2 && c <= 0xDF) need = 1;
        else if (c == 0xE0) need = 2, lo = 0xA0;
        else if ((c >= 0xE1 && c <= 0xEC) || c == 0xEE || c == 0xEF) need = 2;
        else if (c == 0xED) need = 2, hi = 0x9F;
        else if (c == 0xF0) need = 3, lo = 0x90;
        else if (c >= 0xF1 && c <= 0xF3) need = 3;
        else if (c == 0xF4) need = 3, hi = 0x8F;
        if (need == 0) {  // 0x80..0xC1, 0xF5..0xFF: no sequence starts here
            out += "\xEF\xBF\xBD";
            ++i;
            continue;
        }
        size_t j = i + 1;
        bool ok = true;
        for (int k = 0; k < need; ++k, ++j) {
            const uint8_t l = k == 0 ? lo : 0x80, h = k == 0 ? hi : 0xBF;
            if (j >= n || p[j] < l || p[j] > h) {
                ok = false;
                break;
            }
        }
        if (ok) out.append(reinterpret_cast<const char*>(p + i), j - i);
        else out += "\xEF\xBF\xBD";  // the maximal subpart [i, j) is one replacement; p[j] starts afresh
        i = j;
    }
    return out;
}

std::vector<uint32_t> code_points(std::string_view s) {
    std::vector<uint32_t> out;
    out.reserve(s.size());
    const auto* p = reinterpret_cast<const uint8_t*>(s.data());
    const size_t n = s.size();
    for (size_t i = 0; i < n;) {
        const uint8_t c = p[i];
        int len = c < 0x80 ? 1 : (c >> 5) == 6 ? 2 : (c >> 4) == 14 ? 3 : (c >> 3) == 30 ? 4 : 0;
        bool ok = len > 0 && i + len <= n;
        for (int k = 1; ok && k < len; ++k) ok = (p[i + k] & 0xC0) == 0x80;
        if (!ok) {
            out.push_back(0xFFFD);
            ++i;
            continue;
        }
        uint32_t cp = len == 1 ? c : len == 2 ? c & 0x1F : len == 3 ? c & 0x0F : c & 0x07;
        for (int k = 1; k < len; ++k) cp = cp << 6 | (p[i + k] & 0x3F);
        out.push_back(cp);
        i += len;
    }
    return out;
}

namespace {

// Py_UNICODE_ISPRINTABLE for code points from 0x80: false for Cc, Cf, Cs, Co, Zl, Zp, Zs (but space) and Cn. The
// categories that are not "unassigned" are listed in full; of the unassigned code points (which change with Python's
// Unicode version) only the noncharacters and the empty planes are. See the report: a repr of other unassigned code
// points keeps them where Python escapes them.
bool printable(uint32_t c) {
    static constexpr uint32_t kNot[][2] = {
        {0x80, 0xA0},       {0xAD, 0xAD},       {0x600, 0x605},     {0x61C, 0x61C},     {0x6DD, 0x6DD},     {0x70F, 0x70F},
        {0x890, 0x891},     {0x8E2, 0x8E2},     {0x1680, 0x1680},   {0x180E, 0x180E},   {0x2000, 0x200F},   {0x2028, 0x202F},
        {0x205F, 0x2064},   {0x2066, 0x206F},   {0x3000, 0x3000},   {0xD800, 0xF8FF},   {0xFDD0, 0xFDEF},   {0xFEFF, 0xFEFF},
        {0xFFF9, 0xFFFB},   {0x110BD, 0x110BD}, {0x110CD, 0x110CD}, {0x13430, 0x1343F}, {0x1BCA0, 0x1BCA3}, {0x1D173, 0x1D17A},
        {0x40000, 0xDFFFF}, {0xE0000, 0xE00FF}, {0xE01F0, 0x10FFFF},
    };
    if ((c & 0xFFFE) == 0xFFFE) return false;  // U+xFFFE, U+xFFFF of every plane
    for (const auto& r : kNot)
        if (c >= r[0] && c <= r[1]) return false;
    return true;
}

}  // namespace

std::string py_repr(std::string_view s) {
    const std::vector<uint32_t> cps = code_points(s);
    bool single = false, dbl = false;
    for (uint32_t c : cps) single |= c == '\'', dbl |= c == '"';
    const char q = single && !dbl ? '"' : '\'';
    std::string out(1, q);
    char buf[16];
    for (uint32_t c : cps) {
        if (c == static_cast<uint32_t>(q) || c == '\\') out.push_back('\\'), out.push_back(static_cast<char>(c));
        else if (c == '\t') out += "\\t";
        else if (c == '\n') out += "\\n";
        else if (c == '\r') out += "\\r";
        else if (c < ' ' || c == 0x7F) std::snprintf(buf, sizeof buf, "\\x%02x", c), out += buf;
        else if (c < 0x7F) out.push_back(static_cast<char>(c));
        else if (printable(c)) append_utf8(&out, c);
        else if (c <= 0xFF) std::snprintf(buf, sizeof buf, "\\x%02x", c), out += buf;
        else if (c <= 0xFFFF) std::snprintf(buf, sizeof buf, "\\u%04x", c), out += buf;
        else std::snprintf(buf, sizeof buf, "\\U%08x", c), out += buf;
    }
    out.push_back(q);
    return out;
}

bool to_f32(double x, float* out) {
    // (float)x rounds to nearest, ties to even: from 2^128 - 2^103 on a finite value rounds to infinity, which
    // CPython's PyFloat_Pack4 refuses. Below that the conversion is defined.
    if (std::isfinite(x) && std::fabs(x) >= 0x1.ffffffp127) return false;
    *out = static_cast<float>(x);
    return true;
}

// CPython's float_repr_style "short": the exponent has two digits or more.
std::string py_float_repr(double x) {
    if (std::isnan(x)) return "nan";
    if (std::isinf(x)) return x < 0 ? "-inf" : "inf";
    char buf[64];
    const auto r = std::to_chars(buf, buf + sizeof buf, x, std::chars_format::scientific);
    std::string sci(buf, r.ptr);
    std::string sign;
    if (sci[0] == '-') sign = "-", sci.erase(0, 1);
    const size_t e = sci.find('e');
    std::string digits;
    for (size_t i = 0; i < e; ++i)
        if (sci[i] != '.') digits.push_back(sci[i]);
    const int exp10 = std::atoi(sci.c_str() + e + 1);
    int decpt = exp10 + 1;
    const bool use_exp = decpt <= -4 || decpt > 16;
    if (use_exp) decpt = 1;
    const int nd = static_cast<int>(digits.size());
    std::string out;
    if (decpt <= 0) out = "0." + std::string(static_cast<size_t>(-decpt), '0') + digits;
    else if (decpt >= nd) out = digits + std::string(static_cast<size_t>(decpt - nd), '0') + (use_exp ? "" : ".0");
    else out = digits.substr(0, static_cast<size_t>(decpt)) + "." + digits.substr(static_cast<size_t>(decpt));
    if (use_exp) {
        char eb[16];
        std::snprintf(eb, sizeof eb, "e%c%02d", exp10 < 0 ? '-' : '+', std::abs(exp10));
        out += eb;
    }
    return sign + out;
}

std::string hex8(uint64_t v) {
    char b[24];
    std::snprintf(b, sizeof b, "0x%08llx", static_cast<unsigned long long>(v));
    return b;
}

// ---- the value tree ----

Value Value::boolean(bool b) {
    Value v;
    v.k = VK::Bool;
    v.u = b ? 1 : 0;
    return v;
}
Value Value::integer(uint64_t x) {
    Value v;
    v.k = VK::Int;
    v.u = x;
    return v;
}
Value Value::real(double x) {
    Value v;
    v.k = VK::Float;
    v.f = x;
    return v;
}
Value Value::guid(const Guid& g) {
    Value v;
    v.k = VK::Guid;
    v.g = g;
    return v;
}
Value Value::text(std::string x) {
    Value v;
    v.k = VK::Str;
    v.s = std::move(x);
    return v;
}
Value Value::list(std::vector<Value> x) {
    Value v;
    v.k = VK::List;
    v.items = std::move(x);
    return v;
}
Value Value::strukt() {
    Value v;
    v.k = VK::Struct;
    return v;
}
Value Value::hk(uint64_t x) {
    Value v = strukt();
    v.set("value", integer(x));
    return v;
}

const Value* Value::get(std::string_view key) const {
    if (k != VK::Struct) return nullptr;
    const std::vector<std::string_view>& n = names();
    for (size_t i = 0; i < n.size(); ++i)
        if (n[i] == key) return &items[i];
    return nullptr;
}
Value* Value::get(std::string_view key) { return const_cast<Value*>(static_cast<const Value*>(this)->get(key)); }

const Value& Value::at(std::string_view key) const {
    if (k != VK::Struct)
        throw PyError("TypeError", k == VK::None ? "'NoneType' object is not subscriptable" : "a value that is not a dict has no member " + py_repr(key));
    const Value* v = get(key);
    if (!v) throw PyError("KeyError", py_repr(key));
    return *v;
}
Value& Value::at(std::string_view key) { return const_cast<Value&>(static_cast<const Value*>(this)->at(key)); }

void Value::set(std::string_view key, Value v) {
    if (Value* m = get(key)) {
        *m = std::move(v);
        return;
    }
    if (shared_names) own_names = *shared_names, shared_names = nullptr;
    own_names.push_back(key);
    items.push_back(std::move(v));
}

bool Value::erase(std::string_view key) {
    const std::vector<std::string_view>& n = names();
    for (size_t i = 0; i < n.size(); ++i)
        if (n[i] == key) {
            if (shared_names) own_names = *shared_names, shared_names = nullptr;
            own_names.erase(own_names.begin() + static_cast<ptrdiff_t>(i));
            items.erase(items.begin() + static_cast<ptrdiff_t>(i));
            return true;
        }
    return false;
}

uint64_t Value::id() const {
    const Value& v = at("value");
    if (v.k == VK::Int || v.k == VK::Bool) return v.u;
    if (v.k == VK::Enum && v.name < 0) return v.u;
    throw PyError("TypeError", "an id that is not an integer");
}

std::string_view Value::enum_name() const {
    if (k != VK::Enum || name < 0 || !t || static_cast<size_t>(name) >= t->values.size()) return {};
    return t->values[static_cast<size_t>(name)].name;
}

namespace {

uint64_t mask_of(uint64_t size) { return size >= 8 ? ~uint64_t{0} : (uint64_t{1} << (8 * size)) - 1; }

bool is_num(const Value& v) {
    return v.k == VK::Bool || v.k == VK::Int || v.k == VK::Float || (v.k == VK::Enum && v.name < 0) ||
           (v.k == VK::Bits && !(v.t && v.t->inner_t));
}
bool num_is_float(const Value& v) { return v.k == VK::Float; }
bool num_signed(const Value& v) { return (v.k == VK::Int || v.k == VK::Enum) && v.sign; }

bool int_eq(uint64_t a, bool as, uint64_t b, bool bs) {
    if (as == bs) return a == b;
    const uint64_t s = as ? a : b, u = as ? b : a;  // s is signed, u unsigned
    return static_cast<int64_t>(s) >= 0 && s == u;
}
bool int_float_eq(uint64_t i, bool sgn, double f) {
    if (!std::isfinite(f) || f != std::floor(f)) return false;
    if (sgn && static_cast<int64_t>(i) < 0) return f >= -9223372036854775808.0 && static_cast<int64_t>(f) == static_cast<int64_t>(i);
    return f >= 0 && f < 18446744073709551616.0 && static_cast<uint64_t>(f) == i;
}
bool num_eq(const Value& a, const Value& b) {
    if (num_is_float(a) && num_is_float(b)) return a.f == b.f;
    if (num_is_float(a)) return int_float_eq(b.u, num_signed(b), a.f);
    if (num_is_float(b)) return int_float_eq(a.u, num_signed(a), b.f);
    return int_eq(a.u, num_signed(a), b.u, num_signed(b));
}

// A bitmask in the Python's tree: the names (or bit numbers) of its set bits, when its flag enum is known.
struct Token {
    bool named;
    std::string_view name;
    uint32_t bit;
    bool operator==(const Token& o) const { return named == o.named && (named ? name == o.name : bit == o.bit); }
};
std::vector<Token> bit_tokens(const Value& v) {
    std::vector<Token> out;
    const Type* en = v.t ? v.t->inner_t : nullptr;
    const uint64_t bits = v.t ? std::min<uint64_t>(64, 8ull * v.t->size) : 64;
    for (uint32_t i = 0; i < bits; ++i) {
        if (!(v.u >> i & 1)) continue;
        // names = {e.value: e.name}: the last entry of a value wins
        const EnumValue* hit = nullptr;
        if (en)
            for (const EnumValue& e : en->values)
                if (e.value == i) hit = &e;
        out.push_back(hit ? Token{true, hit->name, 0} : Token{false, {}, i});
    }
    return out;
}

}  // namespace

bool equal(const Value& a, const Value& b) {
    if (is_num(a) && is_num(b)) return num_eq(a, b);
    // a str in the Python: Str, a GUID's text, a named enum
    auto as_text = [](const Value& v, std::string* out) {
        if (v.k == VK::Str) return *out = v.s, true;
        if (v.k == VK::Guid) return *out = v.g.text(), true;
        if (v.k == VK::Enum && v.name >= 0) return *out = std::string(v.enum_name()), true;
        return false;
    };
    if (a.k != b.k) {
        std::string x, y;
        return as_text(a, &x) && as_text(b, &y) && x == y;
    }
    switch (a.k) {
        case VK::None: return true;
        case VK::Enum: return a.enum_name() == b.enum_name();  // both named (numbers are handled above)
        case VK::Bits: return bit_tokens(a) == bit_tokens(b);
        case VK::Str:
        case VK::Hex: return a.s == b.s;
        case VK::Raw: return a.s == b.s && a.u == b.u;
        case VK::Guid: return a.g == b.g;
        case VK::Unknown: return a.u == b.u && a.size == b.size;
        case VK::Variant: return a.s == b.s && a.items.size() == b.items.size() && (a.items.empty() || equal(a.items[0], b.items[0]));
        case VK::List:
            if (a.items.size() != b.items.size()) return false;
            for (size_t i = 0; i < a.items.size(); ++i)
                if (!equal(a.items[i], b.items[i])) return false;
            return true;
        case VK::Struct: {
            if (a.items.size() != b.items.size()) return false;
            if (a.shared_names && a.shared_names == b.shared_names) {  // the same keys in the same order
                for (size_t i = 0; i < a.items.size(); ++i)
                    if (!equal(a.items[i], b.items[i])) return false;
                return true;
            }
            const std::vector<std::string_view>&an = a.names(), &bn = b.names();
            for (size_t i = 0; i < a.items.size(); ++i) {
                const Value* other = i < bn.size() && bn[i] == an[i] ? &b.items[i] : b.get(an[i]);
                if (!other || !equal(a.items[i], *other)) return false;
            }
            return true;
        }
        default: return false;
    }
}

// ---- reading, with Python's rules for offsets ----

namespace {

std::string sz(int64_t v) { return std::to_string(v); }

// struct.unpack_from(fmt, b, o) with a format of `size` bytes: the start it reads from, or struct.error as CPython words it.
size_t unpack_at(size_t n, int64_t o, int64_t size) {
    const int64_t len = static_cast<int64_t>(n);
    if (o < 0) {
        if (o + size > 0) throw PyError("error", "not enough data to unpack " + sz(size) + " bytes at offset " + sz(o));
        if (o + len < 0) throw PyError("error", "offset " + sz(o) + " out of range for " + sz(len) + "-byte buffer");
        o += len;
    }
    if (len - o < size)
        throw PyError("error", "unpack_from requires a buffer of at least " + sz(size + o) + " bytes for unpacking " + sz(size) + " bytes at offset " + sz(o) +
                                   " (actual buffer size is " + sz(len) + ")");
    return static_cast<size_t>(o);
}
template <class T>
T rd(const uint8_t* b, size_t n, int64_t o) {
    T v;
    std::memcpy(&v, b + unpack_at(n, o, sizeof(T)), sizeof v);
    return v;
}
// struct.unpack_from("<iI") / ("<IiI"): the whole header in one read, so a short buffer is reported as the Python reports it.
struct Head {
    uint32_t a = 0, b = 0, c = 0;
};
Head head(const uint8_t* b, size_t n, int64_t o, int64_t size) {
    Head h;
    const size_t at = unpack_at(n, o, size);
    std::memcpy(&h.a, b + at, 4);
    if (size >= 8) std::memcpy(&h.b, b + at + 4, 4);
    if (size >= 12) std::memcpy(&h.c, b + at + 8, 4);
    return h;
}

// b[start:stop] as Python slices it: [lo, hi).
void py_slice(size_t n, int64_t start, int64_t stop, size_t* lo, size_t* hi) {
    const int64_t len = static_cast<int64_t>(n);
    auto norm = [&](int64_t i) { return i < 0 ? std::max<int64_t>(0, i + len) : std::min(i, len); };
    const int64_t a = norm(start), z = norm(stop);
    *lo = static_cast<size_t>(a);
    *hi = static_cast<size_t>(std::max(a, z));
}

std::string to_hex(const uint8_t* p, size_t n) {
    static const char* d = "0123456789abcdef";
    std::string s(n * 2, '0');
    for (size_t i = 0; i < n; ++i) s[2 * i] = d[p[i] >> 4], s[2 * i + 1] = d[p[i] & 15];
    return s;
}

std::string hexx(int64_t v) {  // Python's f"{v:#x}"
    char b[32];
    if (v < 0) std::snprintf(b, sizeof b, "-0x%llx", static_cast<unsigned long long>(-(v + 1)) + 1);
    else std::snprintf(b, sizeof b, "0x%llx", static_cast<unsigned long long>(v));
    return b;
}

bool is_signed(uint32_t k) { return k == K_S8 || k == K_S16 || k == K_S32 || k == K_S64; }
uint64_t read_uint(const uint8_t* b, size_t n, int64_t o, uint32_t size) {
    switch (size) {
        case 1: return rd<uint8_t>(b, n, o);
        case 2: return rd<uint16_t>(b, n, o);
        case 4: return rd<uint32_t>(b, n, o);
        default: return rd<uint64_t>(b, n, o);
    }
}
uint64_t sign_extend(uint64_t v, uint32_t size) {
    if (size >= 8) return v;
    const uint64_t top = uint64_t{1} << (8 * size - 1);
    return (v ^ top) - top;
}

const char* kNone = "'NoneType' object is not subscriptable";

}  // namespace

Value Decoder::decode(const Type* t, const uint8_t* b, size_t n, uint64_t off, const std::string& type_name) const {
    if (!t) throw PyError("DecodeError", "unknown type " + type_name);
    if (off > static_cast<uint64_t>(INT64_MAX)) throw PyError("error", "offset out of range");
    return value(t, b, n, static_cast<int64_t>(off), 0);
}

Value Decoder::value(const Type* t, const uint8_t* b, size_t n, int64_t o, int depth) const {
    for (unsigned chain = 0; t && t->kind == K_TYPEDEF && t->inner_t; ++chain) {
        if (chain == 16) throw PyError("DecodeError", "typedef chain exceeds 16 steps at " + t->name);
        t = t->inner_t;
    }
    if (!t) throw PyError("TypeError", kNone);
    if (depth > 64) throw PyError("DecodeError", "nesting too deep");
    const uint32_t k = t->kind;
    Value v;
    switch (k) {
        case K_BOOL: return Value::boolean(rd<uint8_t>(b, n, o) != 0);
        case K_U8: return Value::integer(rd<uint8_t>(b, n, o));
        case K_U16: return Value::integer(rd<uint16_t>(b, n, o));
        case K_U32: return Value::integer(rd<uint32_t>(b, n, o));
        case K_U64: return Value::integer(rd<uint64_t>(b, n, o));
        case K_S8:
        case K_S16:
        case K_S32:
        case K_S64: {
            const uint32_t size = k == K_S8 ? 1 : k == K_S16 ? 2 : k == K_S32 ? 4 : 8;
            v = Value::integer(sign_extend(read_uint(b, n, o, size), size));
            v.sign = true;
            return v;
        }
        case K_F32: return Value::real(static_cast<double>(rd<float>(b, n, o)));
        case K_F64: return Value::real(rd<double>(b, n, o));
        case K_TYPEDEF: {  // unresolved target: b[o:o + size].hex()
            size_t lo, hi;
            py_slice(n, o, o + t->size, &lo, &hi);
            return Value::text(to_hex(b + lo, hi - lo));
        }
        case K_ENUM: {
            const Type* under = t->has_inner && !t->inner.empty() ? db_.resolve(t->inner_t) : nullptr;
            const bool sgn = under && is_signed(under->kind);
            if (t->size != 1 && t->size != 2 && t->size != 4 && t->size != 8)
                throw PyError("TypeError", "Struct() argument 1 must be a str or bytes object, not None");
            uint64_t x = read_uint(b, n, o, t->size);
            if (sgn) x = sign_extend(x, t->size);
            v.k = VK::Enum;
            v.t = t;
            v.u = x;
            v.sign = sgn;
            const uint64_t m = x & mask_of(t->size);
            for (size_t i = 0; i < t->values.size(); ++i)
                if (t->values[i].value == m) {
                    v.name = static_cast<int32_t>(i);
                    break;
                }
            return v;
        }
        case K_BM8:
        case K_BM16:
        case K_BM32:
        case K_BM64:
            if (t->size != 1 && t->size != 2 && t->size != 4 && t->size != 8) throw PyError("KeyError", std::to_string(t->size));
            v.k = VK::Bits;
            v.t = t;
            v.u = read_uint(b, n, o, t->size);
            return v;
        case K_STRUCT: {
            // a dict filled field by field: a repeated name keeps its first place and its last value
            v = Value::strukt();
            const auto& fs = db_.all_fields(t);
            v.shared_names = &t->names;
            v.items.resize(t->names.size());
            for (size_t i = 0; i < fs.size(); ++i) {
                const Field* f = fs[i];
                if (!f->t) {
                    v.items[t->slot[i]] = Value::none();
                    continue;
                }
                if (f->offset > static_cast<uint64_t>(INT64_MAX - std::max<int64_t>(o, 0))) throw PyError("error", "a field offset out of range");
                v.items[t->slot[i]] = value(f->t, b, n, o + static_cast<int64_t>(f->offset), depth + 1);
            }
            return v;
        }
        case K_STATIC_ARRAY: {
            const Type* el = t->inner_t;
            const uint64_t cnt = t->count;
            const uint64_t es = el && el->size ? el->size : (cnt ? t->size / cnt : 0);
            v = Value::list();
            v.items.reserve(cnt);
            for (uint64_t i = 0; i < cnt; ++i) v.items.push_back(value(el, b, n, o + static_cast<int64_t>(i * es), depth + 1));
            return v;
        }
        case K_OBJECT_REF:
        case K_GUID: {
            size_t lo, hi;
            py_slice(n, o, o + 16, &lo, &hi);
            if (hi - lo != 16) throw PyError("ValueError", "bytes_le is not a 16-char string");
            v.k = VK::Guid;
            std::memcpy(v.g.b.data(), b + lo, 16);
            return v;
        }
        case K_BLOB_ARRAY: {
            const Head hd = head(b, n, o, 8);
            const int32_t rel = static_cast<int32_t>(hd.a);
            const uint32_t cnt = hd.b;
            if (cnt == 0) return Value::list();
            if (cnt > max_array_) throw PyError("DecodeError", t->name + ": " + std::to_string(cnt) + " elements");
            const Type* el = t->inner_t;
            if (!el) throw PyError("TypeError", kNone);
            const int64_t es = el->size, base = o + rel;
            if (base < 0 || base + static_cast<int64_t>(cnt) * es > static_cast<int64_t>(n))
                throw PyError("DecodeError", t->name + " at " + hexx(o) + ": [" + hexx(base) + ", +" + std::to_string(cnt) + "x" + std::to_string(es) +
                                                 ") outside the blob (" + std::to_string(n) + ")");
            if (el->kind == K_U8 && cnt > 64) {
                v.k = VK::Hex;
                v.s.assign(reinterpret_cast<const char*>(b + base), cnt);
                return v;
            }
            v = Value::list();
            v.items.reserve(cnt);
            for (uint32_t i = 0; i < cnt; ++i) v.items.push_back(value(el, b, n, base + static_cast<int64_t>(i) * es, depth + 1));
            return v;
        }
        case K_BLOB_STRING: {
            const Head hd = head(b, n, o, 8);
            const int32_t rel = static_cast<int32_t>(hd.a);
            const uint32_t cnt = hd.b;
            if (cnt == 0) return Value::text("");
            size_t lo, hi;
            py_slice(n, o + rel, o + rel + cnt, &lo, &hi);
            return Value::text(utf8_replace(b + lo, hi - lo));
        }
        case K_BLOB_OPTIONAL: {
            const int32_t rel = rd<int32_t>(b, n, o);
            if (rel == 0) return Value::none();
            return value(t->inner_t, b, n, o + rel, depth + 1);
        }
        case K_BLOB_VARIANT: {
            const Head hd = head(b, n, o, 12);
            const uint32_t h = hd.a;
            const int32_t rel = static_cast<int32_t>(hd.b);
            const uint32_t size = hd.c;
            if (h == 0) return Value::none();
            const Type* vt = db_.get(h);
            if (!vt) {
                v.k = VK::Unknown;
                v.u = h;
                v.size = size;
                return v;
            }
            v.k = VK::Variant;
            v.s = vt->name;
            v.items.push_back(value(vt, b, n, o + 4 + rel, depth + 1));
            return v;
        }
        default: {  // ds_* kinds and unknowns: raw bytes
            size_t lo, hi;
            py_slice(n, o, o + t->size, &lo, &hi);
            v.k = VK::Raw;
            v.u = k;
            v.s.assign(reinterpret_cast<const char*>(b + lo), hi - lo);
            return v;
        }
    }
}

// ---- writing ----

namespace {

// struct.pack_into(fmt, buf, o, ...) of `size` bytes: CPython's words when it does not fit.
void pack_at(const Bytes& b, uint64_t o, uint64_t size) {
    if (o + size <= b.size()) return;
    const int64_t len = static_cast<int64_t>(b.size()), off = static_cast<int64_t>(o);
    if (off > len) throw PyError("error", "offset " + sz(off) + " out of range for " + sz(len) + "-byte buffer");
    throw PyError("error", "pack_into requires a buffer of at least " + sz(off + static_cast<int64_t>(size)) + " bytes for packing " +
                               sz(static_cast<int64_t>(size)) + " bytes at offset " + sz(off) + " (actual buffer size is " + sz(len) + ")");
}
template <class T>
void put(Bytes& b, uint64_t o, T v) {
    pack_at(b, o, sizeof v);
    std::memcpy(b.data() + o, &v, sizeof v);
}
void put_uint(Bytes& b, uint64_t o, uint64_t v, uint32_t size) {
    switch (size) {
        case 1: put<uint8_t>(b, o, static_cast<uint8_t>(v)); break;
        case 2: put<uint16_t>(b, o, static_cast<uint16_t>(v)); break;
        case 4: put<uint32_t>(b, o, static_cast<uint32_t>(v)); break;
        default: put<uint64_t>(b, o, v); break;
    }
}

// An integer for a struct.pack integer format, with its range check.
uint64_t as_int(const Value& v, uint32_t size, bool sgn) {
    uint64_t x;
    bool neg = false;
    if (v.k == VK::Bool || v.k == VK::Int || (v.k == VK::Enum && v.name < 0) || (v.k == VK::Bits && !(v.t && v.t->inner_t)))
        x = v.u, neg = num_signed(v) && static_cast<int64_t>(v.u) < 0;
    else
        throw PyError("error", "required argument is not an integer");
    const bool fits = sgn ? (size >= 8 || (neg ? static_cast<int64_t>(x) >= -(int64_t{1} << (8 * size - 1)) : x < (uint64_t{1} << (8 * size - 1))))
                          : (!neg && (size >= 8 || x <= mask_of(size)));
    if (!fits) throw PyError("error", "argument out of range");
    return x;
}
double as_float(const Value& v) {
    if (v.k == VK::Float) return v.f;
    if (v.k == VK::Bool || v.k == VK::Int) return num_signed(v) ? static_cast<double>(static_cast<int64_t>(v.u)) : static_cast<double>(v.u);
    throw PyError("error", "required argument is not a float");
}
bool truthy(const Value& v) {  // Python's bool(v)
    switch (v.k) {
        case VK::None: return false;
        case VK::Bool:
        case VK::Int:
        case VK::Bits: return v.u != 0;  // a list of set bits is empty exactly when the number is 0
        case VK::Enum: return v.name >= 0 ? !v.enum_name().empty() : v.u != 0;
        case VK::Float: return v.f != 0;
        case VK::Str:
        case VK::Hex: return !v.s.empty();
        case VK::List:
        case VK::Struct: return !v.items.empty();
        default: return true;
    }
}

}  // namespace

uint64_t Encoder::reserve(Bytes& b, uint64_t size, uint64_t align) const {
    const uint64_t a = std::max<uint64_t>(1, align);
    const uint64_t o = (b.size() + a - 1) / a * a;
    b.resize(o + size, 0);
    return o;
}

Bytes Encoder::encode(const Type* t, const Value& v, const std::string& type_name) const {
    if (!t) throw PyError("EncodeError", "unknown type " + type_name);
    Bytes b(t->size, 0);
    write(b, t, v, 0);
    return b;
}

void Encoder::write(Bytes& b, const Type* t, const Value& v, uint64_t o) const {
    for (unsigned chain = 0; t && t->kind == K_TYPEDEF; ++chain) {
        if (chain == 16) throw PyError("EncodeError", "typedef chain exceeds 16 steps at " + t->name);
        t = t->inner_t;
    }
    if (!t) throw PyError("TypeError", kNone);
    const uint32_t k = t->kind;
    switch (k) {
        case K_BOOL: put<uint8_t>(b, o, truthy(v) ? 1 : 0); return;
        case K_U8: put_uint(b, o, as_int(v, 1, false), 1); return;
        case K_U16: put_uint(b, o, as_int(v, 2, false), 2); return;
        case K_U32: put_uint(b, o, as_int(v, 4, false), 4); return;
        case K_U64: put_uint(b, o, as_int(v, 8, false), 8); return;
        case K_S8: put_uint(b, o, as_int(v, 1, true), 1); return;
        case K_S16: put_uint(b, o, as_int(v, 2, true), 2); return;
        case K_S32: put_uint(b, o, as_int(v, 4, true), 4); return;
        case K_S64: put_uint(b, o, as_int(v, 8, true), 8); return;
        case K_F32: {
            float x;
            if (!to_f32(as_float(v), &x)) throw PyError("OverflowError", "float too large to pack with f format");
            put<float>(b, o, x);
            return;
        }
        case K_F64: put<double>(b, o, as_float(v)); return;
        case K_ENUM: {
            uint64_t x;
            if (v.k == VK::Enum && v.name >= 0) {  // a name: m = {e.name: e.value}, the last of a name wins
                const std::string_view nm = v.enum_name();
                const EnumValue* hit = nullptr;
                for (const EnumValue& e : t->values)
                    if (e.name == nm) hit = &e;
                if (!hit) throw PyError("EncodeError", t->name + ": unknown value " + py_repr(nm));
                x = hit->value;
            } else if (v.k == VK::Str) {  // a name written as text
                const EnumValue* hit = nullptr;
                for (const EnumValue& e : t->values)
                    if (e.name == v.s) hit = &e;
                if (!hit) throw PyError("EncodeError", t->name + ": unknown value " + py_repr(v.s));
                x = hit->value;
            } else {
                x = as_int(v, 8, num_signed(v));
            }
            if (t->size != 1 && t->size != 2 && t->size != 4 && t->size != 8) throw PyError("KeyError", std::to_string(t->size));
            put_uint(b, o, x & mask_of(t->size), t->size);
            return;
        }
        case K_BM8:
        case K_BM16:
        case K_BM32:
        case K_BM64: {
            uint64_t bits = 0;
            if (v.k == VK::Bits && v.t && v.t->inner_t) {  // a list of names or bit numbers
                const Type* en = t->inner_t;
                for (const Token& tok : bit_tokens(v)) {
                    uint64_t bit = tok.bit;
                    if (tok.named) {
                        const EnumValue* hit = nullptr;  // names = {e.name: e.value}, the last of a name wins
                        if (en)
                            for (const EnumValue& e : en->values)
                                if (e.name == tok.name) hit = &e;
                        if (!hit) throw PyError("KeyError", py_repr(tok.name));
                        bit = hit->value;
                    }
                    if (bit >= 64) throw PyError("error", "argument out of range");
                    bits |= uint64_t{1} << bit;
                }
            } else {
                bits = as_int(v, 8, false);
            }
            if (t->size != 1 && t->size != 2 && t->size != 4 && t->size != 8) throw PyError("KeyError", std::to_string(t->size));
            if (t->size < 8 && bits > mask_of(t->size)) throw PyError("error", "argument out of range");
            put_uint(b, o, bits, t->size);
            return;
        }
        case K_STRUCT: {
            if (v.k != VK::Struct) throw PyError("EncodeError", t->name + ": expected a dict");
            for (const Field* f : db_.all_fields(t)) {
                const Value* m = v.get(f->name);
                if (!m) continue;  // a missing field stays zero
                write(b, f->t, *m, o + f->offset);
            }
            return;
        }
        case K_STATIC_ARRAY: {
            const Type* el = t->inner_t;
            if (!el) throw PyError("TypeError", kNone);
            if (v.k != VK::List) throw PyError("TypeError", "a static array that is not a list");
            const uint64_t cnt = t->count;
            const uint64_t es = el->size ? el->size : t->size / std::max<uint64_t>(1, cnt);
            for (uint64_t i = 0; i < cnt && i < v.items.size(); ++i) write(b, el, v.items[i], o + i * es);
            return;
        }
        case K_OBJECT_REF:
        case K_GUID: {
            Guid g;
            if (v.k == VK::Guid) g = v.g;
            else if (v.k == VK::Str && Guid::parse(v.s, &g)) {
            } else
                throw PyError("ValueError", "badly formed hexadecimal UUID string");
            pack_at(b, o, 16);
            std::memcpy(b.data() + o, g.b.data(), 16);
            return;
        }
        case K_BLOB_ARRAY: {
            const Type* el = t->inner_t;
            if (v.k == VK::Hex) {
                const uint64_t cnt = v.s.size();
                if (cnt == 0) {
                    put<int32_t>(b, o, 0), put<uint32_t>(b, o + 4, 0);
                    return;
                }
                if (!el) throw PyError("TypeError", kNone);
                const uint64_t at = reserve(b, cnt, el->align);
                std::memcpy(b.data() + at, v.s.data(), cnt);
                put<int32_t>(b, o, static_cast<int32_t>(at - o)), put<uint32_t>(b, o + 4, static_cast<uint32_t>(cnt));
                return;
            }
            if (v.k != VK::List) throw PyError("TypeError", "a blob array that is not a list");
            const uint64_t cnt = v.items.size();
            if (cnt == 0) {
                put<int32_t>(b, o, 0), put<uint32_t>(b, o + 4, 0);
                return;
            }
            if (!el) throw PyError("TypeError", kNone);
            const uint64_t es = el->size;
            const uint64_t at = reserve(b, cnt * es, el->align);
            put<int32_t>(b, o, static_cast<int32_t>(at - o)), put<uint32_t>(b, o + 4, static_cast<uint32_t>(cnt));
            for (uint64_t i = 0; i < cnt; ++i) write(b, el, v.items[i], at + i * es);
            return;
        }
        case K_BLOB_STRING: {
            if (v.k != VK::Str) throw PyError("TypeError", "a string that is not text");
            if (v.s.empty()) {
                put<int32_t>(b, o, 0), put<uint32_t>(b, o + 4, 0);
                return;
            }
            const uint64_t at = reserve(b, v.s.size(), 1);
            std::memcpy(b.data() + at, v.s.data(), v.s.size());
            put<int32_t>(b, o, static_cast<int32_t>(at - o)), put<uint32_t>(b, o + 4, static_cast<uint32_t>(v.s.size()));
            return;
        }
        case K_BLOB_OPTIONAL: {
            if (v.k == VK::None) {
                put<int32_t>(b, o, 0);
                return;
            }
            const Type* el = t->inner_t;
            if (!el) throw PyError("TypeError", kNone);
            const uint64_t at = reserve(b, el->size, el->align);
            put<int32_t>(b, o, static_cast<int32_t>(at - o));
            write(b, el, v, at);
            return;
        }
        case K_BLOB_VARIANT: {
            if (v.k == VK::None) {
                put<uint32_t>(b, o, 0), put<int32_t>(b, o + 4, 0), put<uint32_t>(b, o + 8, 0);
                return;
            }
            std::string tname;
            if (v.k == VK::Variant) tname = v.s;
            else if (v.k == VK::Unknown) tname = hex8(v.u);
            else throw PyError("TypeError", "a variant that is not a dict");
            const Type* vt = db_.get(tname);
            if (!vt) throw PyError("EncodeError", "variant type " + tname + " unknown");
            if (v.k == VK::Unknown || v.items.empty()) throw PyError("KeyError", "'value'");
            const uint64_t at = reserve(b, vt->size, vt->align);
            // the size field is the variant struct's own size, not counting its out-of-line data
            put<uint32_t>(b, o, vt->hash), put<int32_t>(b, o + 4, static_cast<int32_t>(at - (o + 4))), put<uint32_t>(b, o + 8, vt->size);
            write(b, vt, v.items[0], at);
            return;
        }
        default:
            if (v.k == VK::Raw) {  // b[o:o + len] = raw, a bytearray slice assignment
                const uint64_t lo = std::min<uint64_t>(o, b.size()), hi = std::min<uint64_t>(o + v.s.size(), b.size());
                b.erase(b.begin() + static_cast<ptrdiff_t>(lo), b.begin() + static_cast<ptrdiff_t>(hi));
                b.insert(b.begin() + static_cast<ptrdiff_t>(lo), v.s.begin(), v.s.end());
                return;
            }
            throw PyError("EncodeError", t->name + ": kind " + (kind_name(k) ? std::string(kind_name(k)) : std::to_string(k)) + " not encodable");
    }
}

}  // namespace kpa
