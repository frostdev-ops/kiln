// SPDX-License-Identifier: MIT
// See states.h. The reference is tools/animcook/anicook/playerstates.py; the JSON reader is CPython's json scanner
// (Modules/_json.c, the C scanner json.loads uses) rule for rule, so a file is refused for the same reason at the same
// place.
#include "states.h"

#include <algorithm>
#include <charconv>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <set>

#include "blob.h"

namespace kpa {

namespace {

void sha1(const uint8_t* data, size_t len, uint8_t out[20]) {
    uint32_t h[5] = {0x67452301u, 0xEFCDAB89u, 0x98BADCFEu, 0x10325476u, 0xC3D2E1F0u};
    std::vector<uint8_t> m(data, data + len);
    m.push_back(0x80);
    while (m.size() % 64 != 56) m.push_back(0);
    const uint64_t bits = static_cast<uint64_t>(len) * 8;
    for (int i = 7; i >= 0; --i) m.push_back(static_cast<uint8_t>(bits >> (i * 8)));
    auto rol = [](uint32_t x, int n) { return x << n | x >> (32 - n); };
    for (size_t o = 0; o < m.size(); o += 64) {
        uint32_t w[80];
        for (int t = 0; t < 16; ++t)
            w[t] = uint32_t{m[o + t * 4]} << 24 | uint32_t{m[o + t * 4 + 1]} << 16 | uint32_t{m[o + t * 4 + 2]} << 8 | uint32_t{m[o + t * 4 + 3]};
        for (int t = 16; t < 80; ++t) w[t] = rol(w[t - 3] ^ w[t - 8] ^ w[t - 14] ^ w[t - 16], 1);
        uint32_t a = h[0], b = h[1], c = h[2], d = h[3], e = h[4];
        for (int t = 0; t < 80; ++t) {
            uint32_t f, k;
            if (t < 20) f = (b & c) | (~b & d), k = 0x5A827999u;
            else if (t < 40) f = b ^ c ^ d, k = 0x6ED9EBA1u;
            else if (t < 60) f = (b & c) | (b & d) | (c & d), k = 0x8F1BBCDCu;
            else f = b ^ c ^ d, k = 0xCA62C1D6u;
            const uint32_t x = rol(a, 5) + f + e + k + w[t];
            e = d, d = c, c = rol(b, 30), b = a, a = x;
        }
        h[0] += a, h[1] += b, h[2] += c, h[3] += d, h[4] += e;
    }
    for (int i = 0; i < 5; ++i)
        for (int j = 0; j < 4; ++j) out[i * 4 + j] = static_cast<uint8_t>(h[i] >> (24 - j * 8));
}

// GUID_RE: ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$ (Python's $ also matches before
// a final newline).
bool guid_form(std::string_view s) {
    if (s.size() == 37 && s.back() == '\n') s.remove_suffix(1);
    if (s.size() != 36) return false;
    for (size_t i = 0; i < 36; ++i) {
        const char c = s[i];
        if (i == 8 || i == 13 || i == 18 || i == 23) {
            if (c != '-') return false;
        } else if (!std::isxdigit(static_cast<unsigned char>(c))) {
            return false;
        }
    }
    return true;
}

std::string ascii_lower(std::string s) {
    for (char& c : s)
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    return s;
}

std::string join(const std::vector<std::string>& v, const char* sep) {
    std::string out;
    for (size_t i = 0; i < v.size(); ++i) out += (i ? sep : "") + v[i];
    return out;
}

// sorted({k for k in keys if keys.count(k) > 1}): Python sorts str by code point, which is UTF-8 byte order.
std::vector<std::string> repeated(const std::vector<std::string>& keys) {
    std::set<std::string> seen, twice;
    for (const std::string& k : keys)
        if (!seen.insert(k).second) twice.insert(k);
    return {twice.begin(), twice.end()};
}
std::vector<std::string> unknown(const std::vector<std::string>& keys, std::initializer_list<const char*> known) {
    std::set<std::string> out;
    for (const std::string& k : keys)
        if (std::none_of(known.begin(), known.end(), [&](const char* x) { return k == x; })) out.insert(k);
    return {out.begin(), out.end()};
}

}  // namespace

std::string derive(const std::string& mod, const std::string& kind, const std::string& name) {
    static constexpr uint8_t ns[16] = {0x6f, 0x6d, 0x2a, 0x44, 0x53, 0xc1, 0x5c, 0x52, 0x9d, 0x51, 0x0a, 0x3c, 0x6e, 0x1f, 0x2b, 0x77};
    const std::string text = mod + ":" + kind + ":" + name;
    std::vector<uint8_t> all(ns, ns + 16);
    all.insert(all.end(), text.begin(), text.end());
    uint8_t h[20];
    sha1(all.data(), all.size(), h);
    h[6] = static_cast<uint8_t>((h[6] & 0x0f) | 0x50);
    h[8] = static_cast<uint8_t>((h[8] & 0x3f) | 0x80);
    char s[37];
    std::snprintf(s, sizeof s, "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x", h[0], h[1], h[2], h[3], h[4], h[5], h[6], h[7], h[8], h[9],
                  h[10], h[11], h[12], h[13], h[14], h[15]);
    return s;
}

uint32_t fnv_guid_text(std::string_view guid) {
    Guid g;
    return Guid::parse(guid, &g) ? fnv_guid(g) : 0;
}

std::string clip_guid(const std::string& mod, const std::string& clip) { return guid_form(clip) ? ascii_lower(clip) : derive(mod, "clip", clip); }

std::optional<std::string> manifest_reason(const std::string& mod, const std::optional<std::string>& side, const std::optional<std::string>& clients) {
    if (side && *side == "both" && clients && *clients == "mod") return std::nullopt;
    return "mod " + mod + ": player states have to be on the server and on every player's game: set \"side\": \"both\" and \"clients\": \"mod\" in kiln.mod.json";
}

void State::refuse(const std::string& why) {
    if (status && *status == "refused") return;
    status = "refused";
    reason = "mod " + mod + ", state " + name + ": " + why;
}

nlohmann::ordered_json State::entry() const {
    auto opt = [](const std::optional<std::string>& s) { return s ? nlohmann::ordered_json(*s) : nlohmann::ordered_json(nullptr); };
    nlohmann::ordered_json e;
    e["mod"] = mod;
    e["name"] = name;
    e["guid"] = guid;
    e["id"] = hex8(id);
    e["kind"] = opt(kind);
    e["like"] = opt(like);
    e["machine"] = opt(machine);
    e["status"] = opt(status);
    e["reason"] = opt(reason);
    return e;
}

// ---- JSON as CPython's scanner reads it ----

const Json* Json::find(std::string_view key) const {
    for (size_t i = keys.size(); i-- > 0;)
        if (keys[i] == key) return &items[i];
    return nullptr;
}

namespace {

struct StopIter {
    size_t pos;
};

constexpr int kMaxDepth = 512;  // CPython raises RecursionError somewhere deeper; a fault here would end the game

class Parser {
public:
    Parser(std::vector<uint32_t> s, const std::string& where, bool strict, const std::string& prefix)
        : s_(std::move(s)), n_(s_.size()), where_(where), strict_(strict), prefix_(prefix) {}

    Json document() {
        if (n_ && s_[0] == 0xFEFF) fail("Unexpected UTF-8 BOM (decode using utf-8-sig)", 0);
        size_t idx = ws(0), end = 0;
        Json v;
        try {
            v = value(idx, &end, 0);
        } catch (const StopIter& e) {
            fail("Expecting value", e.pos);
        }
        end = ws(end);
        if (end != n_) fail("Extra data", end);
        return v;
    }

private:
    [[noreturn]] void fail(const char* msg, size_t pos) {
        size_t line = 1, last_nl = SIZE_MAX;
        for (size_t i = 0; i < pos && i < n_; ++i)
            if (s_[i] == '\n') ++line, last_nl = i;
        const size_t col = last_nl == SIZE_MAX ? pos + 1 : pos - last_nl;
        throw DefinitionError(where_ + ": " + prefix_ + msg + ": line " + std::to_string(line) + " column " + std::to_string(col) + " (char " + std::to_string(pos) +
                              ")");
    }
    size_t ws(size_t i) const {
        while (i < n_ && (s_[i] == ' ' || s_[i] == '\t' || s_[i] == '\n' || s_[i] == '\r')) ++i;
        return i;
    }
    bool at(size_t i, const char* lit) const {
        for (size_t k = 0; lit[k]; ++k)
            if (i + k >= n_ || s_[i + k] != static_cast<unsigned char>(lit[k])) return false;
        return true;
    }
    static bool digit(uint32_t c) { return c >= '0' && c <= '9'; }

    Json constant(const char* name, size_t idx, size_t len, size_t* next) {
        if (strict_) throw DefinitionError(where_ + ": " + name + " is not JSON");
        Json v;
        v.t = Json::Float;
        v.f = name[0] == 'N' ? std::nan("") : name[0] == '-' ? -HUGE_VAL : HUGE_VAL;
        *next = idx + len;
        return v;
    }

    Json value(size_t idx, size_t* next, int depth) {
        if (idx >= n_) throw StopIter{idx};
        if (depth > kMaxDepth) fail("too deeply nested", idx);
        const uint32_t c = s_[idx];
        const size_t len = n_;
        Json v;
        switch (c) {
            case '"':
                v.t = Json::Str;
                v.s = string(idx + 1, next);
                return v;
            case '{': return object(idx + 1, next, depth + 1);
            case '[': return array(idx + 1, next, depth + 1);
            case 'n':
                if (idx + 3 < len && at(idx, "null")) return *next = idx + 4, v;
                break;
            case 't':
                if (idx + 3 < len && at(idx, "true")) return v.t = Json::True, *next = idx + 4, v;
                break;
            case 'f':
                if (idx + 4 < len && at(idx, "false")) return v.t = Json::False, *next = idx + 5, v;
                break;
            case 'N':
                if (idx + 2 < len && at(idx, "NaN")) return constant("NaN", idx, 3, next);
                break;
            case 'I':
                if (idx + 7 < len && at(idx, "Infinity")) return constant("Infinity", idx, 8, next);
                break;
            case '-':
                if (idx + 8 < len && at(idx, "-Infinity")) return constant("-Infinity", idx, 9, next);
                break;
            default: break;
        }
        return number(idx, next);
    }

    // _match_number_unicode
    Json number(size_t start, size_t* next) {
        const size_t end_idx = n_ - 1;
        size_t idx = start;
        bool is_float = false;
        if (s_[idx] == '-') {
            ++idx;
            if (idx > end_idx) throw StopIter{start};
        }
        if (s_[idx] >= '1' && s_[idx] <= '9') {
            ++idx;
            while (idx <= end_idx && digit(s_[idx])) ++idx;
        } else if (s_[idx] == '0') {
            ++idx;
        } else {
            throw StopIter{start};
        }
        if (idx < end_idx && s_[idx] == '.' && digit(s_[idx + 1])) {
            is_float = true;
            idx += 2;
            while (idx <= end_idx && digit(s_[idx])) ++idx;
        }
        if (idx < end_idx && (s_[idx] == 'e' || s_[idx] == 'E')) {
            const size_t e_start = idx;
            ++idx;
            if (idx < end_idx && (s_[idx] == '-' || s_[idx] == '+')) ++idx;
            while (idx <= end_idx && digit(s_[idx])) ++idx;
            if (digit(s_[idx - 1])) is_float = true;
            else idx = e_start;
        }
        std::string text;
        for (size_t i = start; i < idx; ++i) text.push_back(static_cast<char>(s_[i]));
        *next = idx;
        Json v;
        v.t = is_float ? Json::Float : Json::Int;
        double d = 0;
        const auto r = std::from_chars(text.data(), text.data() + text.size(), d);
        if (r.ec == std::errc::result_out_of_range) {
            // float() gives +-inf on overflow and +-0.0 on underflow; an int too large for a double raises OverflowError
            // when converted. Which one: the decimal exponent of the first significant digit.
            const bool neg = text[0] == '-';
            const size_t e = text.find_first_of("eE");
            const std::string mant = text.substr(neg ? 1 : 0, (e == std::string::npos ? text.size() : e) - (neg ? 1 : 0));
            long long ex = 0;
            if (e != std::string::npos) {
                const char* p = text.data() + e + 1;
                const bool eneg = *p == '-';
                if (*p == '-' || *p == '+') ++p;
                for (; p < text.data() + text.size(); ++p) ex = std::min<long long>(ex * 10 + (*p - '0'), 1000000000LL);
                if (eneg) ex = -ex;
            }
            const size_t dot = mant.find('.');
            const long long int_digits = static_cast<long long>(dot == std::string::npos ? mant.size() : dot);
            long long lead = 0;
            for (char ch : mant) {
                if (ch == '.') continue;
                if (ch != '0') break;
                ++lead;
            }
            const bool overflow = int_digits - 1 - lead + ex >= 0;
            d = overflow ? HUGE_VAL : 0.0;
            if (neg) d = -d;
            if (!is_float) v.big = overflow;
        }
        v.f = d;
        if (!is_float) v.s = text == "-0" ? "0" : text;
        return v;
    }

    // scanstring_unicode, strict: a control character in a string is an error
    std::string string(size_t end, size_t* next) {
        const size_t begin = end - 1, len = n_;
        std::string out;
        while (true) {
            uint32_t c = 0;
            size_t nx = end;
            for (; nx < len; ++nx) {
                c = s_[nx];
                if (c == '"' || c == '\\') break;
                if (c <= 0x1f) fail("Invalid control character at", nx);
            }
            if (nx >= len || !(c == '"' || c == '\\')) fail("Unterminated string starting at", begin);
            for (size_t i = end; i < nx; ++i) append_utf8(&out, s_[i]);
            ++nx;
            if (c == '"') {
                *next = nx;
                return out;
            }
            if (nx == len) fail("Unterminated string starting at", begin);
            c = s_[nx];
            if (c != 'u') {
                end = nx + 1;
                switch (c) {
                    case '"':
                    case '\\':
                    case '/': break;
                    case 'b': c = '\b'; break;
                    case 'f': c = '\f'; break;
                    case 'n': c = '\n'; break;
                    case 'r': c = '\r'; break;
                    case 't': c = '\t'; break;
                    default: c = 0;
                }
                if (c == 0) fail("Invalid \\escape", end - 2);
            } else {
                c = 0;
                ++nx;
                end = nx + 4;
                if (end >= len) fail("Invalid \\uXXXX escape", nx - 1);
                for (; nx < end; ++nx) {
                    const int h = hex(s_[nx]);
                    if (h < 0) fail("Invalid \\uXXXX escape", end - 5);
                    c = c << 4 | static_cast<uint32_t>(h);
                }
                // a surrogate pair
                if (c >= 0xD800 && c <= 0xDBFF && end + 6 < len && s_[nx] == '\\' && s_[nx + 1] == 'u') {
                    nx += 2;
                    uint32_t c2 = 0;
                    end += 6;
                    for (; nx < end; ++nx) {
                        const int h = hex(s_[nx]);
                        if (h < 0) fail("Invalid \\uXXXX escape", end - 5);
                        c2 = c2 << 4 | static_cast<uint32_t>(h);
                    }
                    if (c2 >= 0xDC00 && c2 <= 0xDFFF) c = 0x10000 + ((c - 0xD800) << 10) + (c2 - 0xDC00);
                    else end -= 6;
                }
            }
            append_utf8(&out, c);
        }
    }
    static int hex(uint32_t c) {
        return c >= '0' && c <= '9' ? static_cast<int>(c - '0') : c >= 'a' && c <= 'f' ? static_cast<int>(c - 'a' + 10) : c >= 'A' && c <= 'F' ? static_cast<int>(c - 'A' + 10) : -1;
    }

    Json object(size_t idx, size_t* next, int depth) {
        Json v;
        v.t = Json::Obj;
        const size_t end_idx = n_ - 1;  // n_ > 0 here: '{' was read
        idx = ws(idx);
        if (idx > end_idx || s_[idx] != '}') {
            while (true) {
                if (idx > end_idx || s_[idx] != '"') fail("Expecting property name enclosed in double quotes", idx);
                size_t nx = 0;
                std::string key = string(idx + 1, &nx);
                idx = ws(nx);
                if (idx > end_idx || s_[idx] != ':') fail("Expecting ':' delimiter", idx);
                idx = ws(idx + 1);
                Json val = value(idx, &nx, depth);
                v.keys.push_back(std::move(key));
                v.items.push_back(std::move(val));
                idx = ws(nx);
                if (idx <= end_idx && s_[idx] == '}') break;
                if (idx > end_idx || s_[idx] != ',') fail("Expecting ',' delimiter", idx);
                const size_t comma = idx;
                idx = ws(idx + 1);
                if (idx <= end_idx && s_[idx] == '}') fail("Illegal trailing comma before end of object", comma);
            }
        }
        *next = idx + 1;
        return v;
    }

    Json array(size_t idx, size_t* next, int depth) {
        Json v;
        v.t = Json::Arr;
        const size_t end_idx = n_ - 1;
        idx = ws(idx);
        if (idx > end_idx || s_[idx] != ']') {
            while (true) {
                size_t nx = 0;
                v.items.push_back(value(idx, &nx, depth));
                idx = ws(nx);
                if (idx <= end_idx && s_[idx] == ']') break;
                if (idx > end_idx || s_[idx] != ',') fail("Expecting ',' delimiter", idx);
                const size_t comma = idx;
                idx = ws(idx + 1);
                if (idx <= end_idx && s_[idx] == ']') fail("Illegal trailing comma before end of array", comma);
            }
        }
        *next = idx + 1;
        return v;
    }

    std::vector<uint32_t> s_;
    size_t n_;
    const std::string& where_;
    bool strict_;
    const std::string& prefix_;
};

}  // namespace

Json parse_json(std::string_view text, const std::string& where, bool strict_constants, const std::string& prefix) {
    return Parser(code_points(text), where, strict_constants, prefix).document();
}

std::string py_repr(const Json& v) {
    switch (v.t) {
        case Json::Null: return "None";
        case Json::True: return "True";
        case Json::False: return "False";
        case Json::Int: return v.s;
        case Json::Float: return py_float_repr(v.f);
        case Json::Str: return py_repr(std::string_view(v.s));
        case Json::Arr: {
            std::string out = "[";
            for (size_t i = 0; i < v.items.size(); ++i) out += (i ? ", " : "") + py_repr(v.items[i]);
            return out + "]";
        }
        case Json::Obj: {
            std::string out = "[";
            for (size_t i = 0; i < v.items.size(); ++i) out += (i ? ", (" : "(") + py_repr(std::string_view(v.keys[i])) + ", " + py_repr(v.items[i]) + ")";
            return out + "]";
        }
    }
    return "None";
}

// ---- the definitions ----

namespace {

bool is_number(const Json& v) { return v.t == Json::Int || v.t == Json::Float; }

// A Python str with a lone surrogate (a \ud800 escape) cannot be encoded as UTF-8: the reference stops with
// UnicodeEncodeError where it derives a GUID from it (a state name, a clip name, the mod id). Here the file is refused.
bool lone_surrogate(std::string_view s) {
    for (uint32_t c : code_points(s))
        if (c >= 0xD800 && c <= 0xDFFF) return true;
    return false;
}

// Fills `st` from its object; the problems found, in the Python's order.
std::vector<std::string> check_state(State& st, const Json& value) {
    std::vector<std::string> problems;
    const size_t len = code_points(st.name).size();
    if (len < 1 || len > kNameMax || st.name.find('/') != std::string::npos)
        problems.push_back("a state name has 1 to " + std::to_string(kNameMax) + " characters and no '/'");
    if (value.t != Json::Obj) {
        problems.push_back("a state is an object: {\"clip\": ..., \"like\": ...}");
        return problems;
    }
    const std::vector<std::string> twice = repeated(value.keys);
    if (!twice.empty()) problems.push_back("key " + join(twice, ", ") + " given twice");
    const std::vector<std::string> unk = unknown(value.keys, {"clip", "like", "blendIn", "loop", "speed"});
    if (!unk.empty()) problems.push_back("unknown keys " + join(unk, ", ") + " (known: clip, like, blendIn, loop, speed)");
    const Json* clip = value.find("clip");
    const Json* like = value.find("like");
    if (!clip || clip->t != Json::Str || clip->s.empty()) {
        problems.push_back("clip: a clip name of this mod's cook or a GUID");
    } else {
        if (lone_surrogate(clip->s))
            throw DefinitionError("mod " + st.mod + ": state " + st.name + ": clip " + py_repr(std::string_view(clip->s)) + " is not Unicode text (a lone surrogate)");
        st.clip_ref = clip->s;
        st.clip = clip_guid(st.mod, clip->s);
    }
    if (!like || like->t != Json::Str || like->s.empty()) {
        problems.push_back("like: a state of the player's graph, State or Machine/State");
    } else {
        st.like = like->s;
        size_t parts = 1;
        bool empty_part = false;
        size_t from = 0;
        for (size_t i = 0; i <= like->s.size(); ++i)
            if (i == like->s.size() || like->s[i] == '/') {
                empty_part |= i == from;
                if (i < like->s.size()) ++parts;
                from = i + 1;
            }
        if (parts > 2 || empty_part) problems.push_back("like " + py_repr(std::string_view(like->s)) + ": State or Machine/State");
    }
    if (const Json* v = value.find("blendIn")) {
        if (!is_number(*v) || !(v->f >= 0 && v->f <= kBlendMax)) {
            problems.push_back("blendIn: seconds from 0 to 5");
        } else {
            float x = 0;
            to_f32(v->f, &x);  // 0..5: always fits
            st.blend_in = static_cast<double>(x);
        }
    }
    if (const Json* v = value.find("loop")) {
        if (v->t != Json::True && v->t != Json::False) problems.push_back("loop: true or false");
        else st.loop = v->t == Json::True;
    }
    if (const Json* v = value.find("speed")) {
        // _number(v) and v > 0, then 0 < f32(v) < inf; float() of a huge int and a finite value past float32's
        // range raise OverflowError, which refuses it
        bool ok = is_number(*v) && v->f > 0;
        float x = 0;
        if (ok) ok = !v->big && to_f32(v->f, &x) && x > 0 && std::isfinite(x);
        if (!ok) problems.push_back("speed: a number above 0 (a float32)");
        else st.speed = static_cast<double>(x);
    }
    return problems;
}

}  // namespace

ModStates parse(std::string_view text, const std::string& mod, const std::optional<std::string>& side, const std::optional<std::string>& clients,
                const std::string& source, const IdHooks* hooks) {
    const std::string where = "mod " + mod + ": " + source;
    const Json doc = parse_json(text, where, true, "not strict JSON: ");
    if (doc.t != Json::Obj) throw DefinitionError(where + ": an object with \"format\" and \"states\"");
    const std::vector<std::string> twice = repeated(doc.keys);
    if (!twice.empty()) throw DefinitionError(where + ": key " + join(twice, ", ") + " given twice");
    const std::vector<std::string> unk = unknown(doc.keys, {"format", "states"});
    if (!unk.empty()) throw DefinitionError(where + ": unknown keys " + join(unk, ", ") + " (known: format, states)");
    const Json* format = doc.find("format");
    if (!format || format->t != Json::Int || format->s != std::to_string(kFormat))
        throw DefinitionError(where + ": format " + (format ? py_repr(*format) : std::string("None")) + ", expected " + std::to_string(kFormat));
    const Json* states = doc.find("states");
    if (!states || states->t != Json::Obj) throw DefinitionError(where + ": \"states\": an object of states by name");

    ModStates out;
    out.mod = mod;
    out.side = side;
    out.clients = clients;
    out.source = source;
    std::set<std::string> seen;
    for (size_t i = 0; i < states->keys.size(); ++i) {
        const std::string& name = states->keys[i];
        if (!seen.insert(name).second) continue;
        if (lone_surrogate(name) || lone_surrogate(mod))
            throw DefinitionError(where + ": state name " + py_repr(std::string_view(name)) + " of mod " + py_repr(std::string_view(mod)) +
                                  " is not Unicode text (a lone surrogate)");
        State st;
        st.mod = mod;
        st.name = name;
        st.guid = derive(mod, "state", name);
        st.id = hooks && hooks->state_id ? hooks->state_id(st.guid) : fnv_guid_text(st.guid);
        if (std::count(states->keys.begin(), states->keys.end(), name) > 1) {
            st.refuse("defined twice in \"states\"");
        } else {
            const std::vector<std::string> problems = check_state(st, states->items[i]);
            if (!problems.empty()) st.refuse(join(problems, "; "));
        }
        out.states.push_back(std::move(st));
    }
    // ids alike within the file (groups in order of first appearance)
    std::vector<std::pair<uint32_t, std::vector<size_t>>> by_id;
    for (size_t i = 0; i < out.states.size(); ++i) {
        auto it = std::find_if(by_id.begin(), by_id.end(), [&](const auto& g) { return g.first == out.states[i].id; });
        if (it == by_id.end()) by_id.push_back({out.states[i].id, {i}});
        else it->second.push_back(i);
    }
    for (const auto& [id, same] : by_id) {
        if (same.size() < 2) continue;
        for (size_t i : same) {
            std::vector<std::string> others;
            for (size_t o : same)
                if (o != i) others.push_back(out.states[o].name);
            out.states[i].refuse("its state id " + hex8(id) + " is the id of state " + join(others, ", ") + " too: rename one");
        }
    }
    if (const std::optional<std::string> why = manifest_reason(mod, side, clients))
        for (State& st : out.states) {
            st.status = "refused";
            st.reason = *why + (st.reason ? " (also " + *st.reason + ")" : std::string());
        }
    return out;
}

ModStates load_bytes(std::string_view bytes, const std::string& mod, const std::optional<std::string>& side, const std::optional<std::string>& clients,
                     const std::string& file_name, const IdHooks* hooks) {
    // raw.decode("utf-8"): the first bad sequence, in CPython's words
    const auto* p = reinterpret_cast<const uint8_t*>(bytes.data());
    const size_t n = bytes.size();
    for (size_t i = 0; i < n;) {
        const uint8_t c = p[i];
        if (c < 0x80) {
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
        size_t j = i + 1;
        const char* why = nullptr;
        if (need == 0) {
            why = "invalid start byte";
        } else {
            for (int k = 0; k < need; ++k, ++j) {
                if (j >= n) {
                    why = "unexpected end of data";
                    break;
                }
                const uint8_t l = k == 0 ? lo : 0x80, h = k == 0 ? hi : 0xBF;
                if (p[j] < l || p[j] > h) {
                    why = "invalid continuation byte";
                    break;
                }
            }
        }
        if (why) {
            char msg[160];
            if (j - i == 1) std::snprintf(msg, sizeof msg, "'utf-8' codec can't decode byte 0x%02x in position %zu: %s", c, i, why);
            else std::snprintf(msg, sizeof msg, "'utf-8' codec can't decode bytes in position %zu-%zu: %s", i, j - 1, why);
            throw DefinitionError("mod " + mod + ": " + file_name + ": not UTF-8: " + msg);
        }
        i = j;
    }
    if (n >= 3 && p[0] == 0xEF && p[1] == 0xBB && p[2] == 0xBF)
        throw DefinitionError("mod " + mod + ": " + file_name + ": starts with a byte order mark (strict JSON has none)");
    return parse(bytes, mod, side, clients, file_name, hooks);
}

// ---- kiln.mod.json ----

namespace {

// Python's \s in a str pattern: str.isspace().
bool py_space(uint32_t c) {
    return (c >= 0x09 && c <= 0x0D) || (c >= 0x1C && c <= 0x20) || c == 0x85 || c == 0xA0 || c == 0x1680 || (c >= 0x2000 && c <= 0x200A) || c == 0x2028 ||
           c == 0x2029 || c == 0x202F || c == 0x205F || c == 0x3000;
}

std::string to_text(const std::vector<uint32_t>& cps, size_t lo, size_t hi) {
    std::string out;
    for (size_t i = lo; i < hi; ++i) append_utf8(&out, cps[i]);
    return out;
}

}  // namespace

ManifestFields read_manifest_text(std::string_view text, const std::string& where) {
    // re.sub(r"^\s*//.*$", "", text, flags=re.M): a line whose first non-space characters are // goes, with the
    // white space before it (which may span lines)
    const std::vector<uint32_t> in = code_points(text);
    std::vector<uint32_t> a;
    a.reserve(in.size());
    for (size_t i = 0; i < in.size();) {
        if (i == 0 || in[i - 1] == '\n') {
            size_t q = i;
            while (q < in.size() && py_space(in[q])) ++q;
            if (q + 1 < in.size() && in[q] == '/' && in[q + 1] == '/') {
                size_t e = q;
                while (e < in.size() && in[e] != '\n') ++e;
                i = e;  // the match ends before the newline
                continue;
            }
        }
        a.push_back(in[i++]);
    }
    // re.sub(r",(\s*[}\]])", r"\1", ...): a comma before a closing bracket goes
    std::vector<uint32_t> b;
    b.reserve(a.size());
    for (size_t i = 0; i < a.size();) {
        if (a[i] == ',') {
            size_t q = i + 1;
            while (q < a.size() && py_space(a[q])) ++q;
            if (q < a.size() && (a[q] == '}' || a[q] == ']')) {
                b.insert(b.end(), a.begin() + static_cast<ptrdiff_t>(i + 1), a.begin() + static_cast<ptrdiff_t>(q + 1));
                i = q + 1;
                continue;
            }
        }
        b.push_back(a[i++]);
    }
    const Json doc = parse_json(to_text(b, 0, b.size()), where, false, "");
    if (doc.t != Json::Obj) throw DefinitionError(where + ": not an object");
    ManifestFields out;
    auto str = [&](const char* k) -> std::optional<std::string> {
        const Json* v = doc.find(k);
        return v && v->t == Json::Str ? std::optional<std::string>(v->s) : std::nullopt;
    };
    out.id = str("id");
    out.side = str("side");
    out.clients = str("clients");
    if (!out.id || out.id->empty()) throw DefinitionError(where + ": no \"id\"");
    return out;
}

}  // namespace kpa
