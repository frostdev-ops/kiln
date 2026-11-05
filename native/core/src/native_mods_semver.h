// SPDX-License-Identifier: MIT
// Semantic versions and version ranges for native.json ("kiln", "incompatible"): a C++ port of
// managed/Kiln.Host/SemVer.cs (SemVersion + VersionRange), same grammar and results. Parity: schema/fixtures/semver
// (native/spikes/proxyhost/semvertest.cpp runs ranges.json, parse.json and order.json against this file).
// Header-only so the test exe can include it without the core.
#pragma once
#include <climits>
#include <string>
#include <string_view>
#include <vector>

namespace kiln::semver {

struct Version {
    int major = 0, minor = 0, patch = 0;
    std::vector<std::string> pre;
    std::string build;

    Version release() const { return Version{major, minor, patch, {}, {}}; }
    Version lowest() const { return Version{major, minor, patch, {"0"}, {}}; }  // X.Y.Z-0: below its pre-releases
    std::string str() const {
        std::string s = std::to_string(major) + "." + std::to_string(minor) + "." + std::to_string(patch);
        for (size_t i = 0; i < pre.size(); ++i) s += (i == 0 ? "-" : ".") + pre[i];
        if (!build.empty()) s += "+" + build;
        return s;
    }
};

namespace detail {

inline bool digit(char c) { return c >= '0' && c <= '9'; }
inline bool all_digits(std::string_view s) {
    for (char c : s)
        if (!digit(c)) return false;
    return true;
}
inline bool identifier(std::string_view s) {
    if (s.empty()) return false;
    for (char c : s)
        if (!(digit(c) || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '-')) return false;
    return true;
}
inline std::string_view trim(std::string_view s) {
    auto ws = [](char c) { return c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f' || c == '\v'; };
    while (!s.empty() && ws(s.front())) s.remove_prefix(1);
    while (!s.empty() && ws(s.back())) s.remove_suffix(1);
    return s;
}
inline std::vector<std::string_view> split(std::string_view s, std::string_view sep) {
    std::vector<std::string_view> out;
    size_t start = 0;
    for (;;) {
        const size_t at = s.find(sep, start);
        if (at == std::string_view::npos) {
            out.push_back(s.substr(start));
            return out;
        }
        out.push_back(s.substr(start, at - start));
        start = at + sep.size();
    }
}
// int.TryParse(s, NumberStyles.None): digits only, fits an int.
inline bool to_int(std::string_view s, int* out) {
    if (s.empty() || !all_digits(s)) return false;
    long long v = 0;
    for (char c : s) {
        v = v * 10 + (c - '0');
        if (v > INT_MAX) return false;
    }
    *out = static_cast<int>(v);
    return true;
}

}  // namespace detail

// 1, 1.2, 1.2.3, 1.x, 1.2.* with optional -pre and +build; count = numeric parts before the first wildcard.
inline bool parse_partial(std::string_view text, int parts[3], int* count, std::vector<std::string>* pre,
                          std::string* build, bool* wildcard) {
    using namespace detail;
    parts[0] = parts[1] = parts[2] = 0;
    *count = 0;
    pre->clear();
    build->clear();
    *wildcard = false;
    std::string_view s = trim(text);
    if (s.empty()) return false;
    if (s[0] == 'v' || s[0] == 'V') s.remove_prefix(1);
    const size_t plus = s.find('+');
    if (plus != std::string_view::npos) {
        const std::string_view b = s.substr(plus + 1);
        if (b.empty()) return false;
        for (auto part : split(b, "."))
            if (!identifier(part)) return false;
        *build = std::string(b);
        s = s.substr(0, plus);
    }
    const size_t dash = s.find('-');
    if (dash != std::string_view::npos) {
        for (auto part : split(s.substr(dash + 1), ".")) {
            if (!identifier(part)) return false;
            if (part.size() > 1 && part[0] == '0' && all_digits(part)) return false;  // no leading zeros
            pre->emplace_back(part);
        }
        s = s.substr(0, dash);
    }
    const auto nums = split(s, ".");
    if (nums.empty() || nums.size() > 3) return false;
    for (size_t i = 0; i < nums.size(); ++i) {
        const std::string_view n = nums[i];
        if (n == "x" || n == "X" || n == "*") {
            *wildcard = true;
            continue;
        }
        if (*wildcard) return false;  // 1.x.3
        if (n.empty() || !all_digits(n) || (n.size() > 1 && n[0] == '0')) return false;
        if (!to_int(n, &parts[i])) return false;
        *count = static_cast<int>(i) + 1;
    }
    if (*wildcard && !pre->empty()) return false;
    return true;
}

inline bool parse(std::string_view text, Version* out) {
    int parts[3];
    int count = 0;
    bool wildcard = false;
    Version v;
    if (!parse_partial(text, parts, &count, &v.pre, &v.build, &wildcard) || count == 0 || wildcard) return false;
    v.major = parts[0];
    v.minor = count > 1 ? parts[1] : 0;
    v.patch = count > 2 ? parts[2] : 0;
    *out = std::move(v);
    return true;
}

// semver.org precedence; build metadata is ignored.
inline int compare(const Version& a, const Version& b) {
    auto cmp = [](long long x, long long y) { return x < y ? -1 : x > y ? 1 : 0; };
    int c = cmp(a.major, b.major);
    if (c == 0) c = cmp(a.minor, b.minor);
    if (c == 0) c = cmp(a.patch, b.patch);
    if (c != 0) return c;
    if (a.pre.empty() || b.pre.empty()) return cmp(static_cast<long long>(b.pre.size()), static_cast<long long>(a.pre.size()));
    for (size_t i = 0; i < a.pre.size() && i < b.pre.size(); ++i) {
        int ai = 0, bi = 0;
        const bool an = detail::to_int(a.pre[i], &ai), bn = detail::to_int(b.pre[i], &bi);
        c = an && bn ? cmp(ai, bi) : an ? -1 : bn ? 1 : (a.pre[i] < b.pre[i] ? -1 : a.pre[i] > b.pre[i] ? 1 : 0);
        if (c != 0) return c;
    }
    return cmp(static_cast<long long>(a.pre.size()), static_cast<long long>(b.pre.size()));
}

class Range {
public:
    // The grammar of docs/kiln/modding/manifest.md ("Versions and ranges").
    static bool parse(std::string_view text, Range* out, std::string* error) {
        Range r;
        error->clear();
        const std::string_view s = detail::trim(text);
        r.text_ = std::string(s.empty() ? std::string_view("*") : s);
        if (s.empty() || s == "*") {
            r.sets_.push_back({});
            *out = std::move(r);
            return true;
        }
        for (auto alt : detail::split(s, "||")) {
            const std::string_view part = detail::trim(alt);
            if (part.empty()) {
                *error = "version range '" + std::string(s) + "': empty alternative";
                return false;
            }
            if (part[0] == '[' || part[0] == '(') {
                if (!intervals(part, &r.sets_, error)) return false;
                continue;
            }
            std::vector<Cmp> set;
            if (!comparators(part, &set, error)) {
                *error = "version range '" + std::string(s) + "': " + *error;
                return false;
            }
            r.sets_.push_back(std::move(set));
        }
        *out = std::move(r);
        return true;
    }

    bool satisfies(const Version& v) const {
        for (const auto& set : sets_) {
            bool all = true;
            for (const auto& c : set) all = all && c.test(v);
            if (all) return true;
        }
        return false;
    }
    bool any() const {
        for (const auto& set : sets_)
            if (set.empty()) return true;
        return false;
    }
    const std::string& text() const { return text_; }

private:
    enum class Op { Eq, Gt, Ge, Lt, Le };
    struct Cmp {
        Op op;
        Version v;
        bool test(const Version& x) const {
            const int c = compare(x, v);
            switch (op) {
                case Op::Eq: return c == 0;
                case Op::Gt: return c > 0;
                case Op::Ge: return c >= 0;
                case Op::Lt: return c < 0;
                default: return c <= 0;
            }
        }
    };
    struct Partial {
        int major = 0, minor = 0, patch = 0, count = 0;
        std::vector<std::string> pre;
        bool wildcard = false;
        Version floor() const { return Version{major, minor, patch, pre, {}}; }
        Version next() const {
            if (count == 1) return Version{major + 1, 0, 0, {}, {}};
            if (count == 2) return Version{major, minor + 1, 0, {}, {}};
            return Version{major, minor, patch + 1, {}, {}};
        }
    };

    std::vector<std::vector<Cmp>> sets_;  // OR of ANDs; an empty set = any
    std::string text_;

    static bool partial(std::string_view text, Partial* p) {
        int parts[3];
        std::string build;
        if (!parse_partial(text, parts, &p->count, &p->pre, &build, &p->wildcard)) return false;
        p->major = parts[0], p->minor = parts[1], p->patch = parts[2];
        return true;
    }

    static bool comparators(std::string_view part, std::vector<Cmp>* set, std::string* error) {
        std::string spaced(part);
        for (char& c : spaced)
            if (c == ',') c = ' ';
        std::vector<std::string> tokens;
        for (auto t : detail::split(spaced, " "))
            if (!t.empty()) tokens.emplace_back(t);
        for (size_t i = 0; i + 1 < tokens.size(); ++i) {  // ">= 1.0" -> ">=1.0"
            const std::string& t = tokens[i];
            if (t == ">=" || t == "<=" || t == ">" || t == "<" || t == "=" || t == "^" || t == "~") {
                tokens[i] += tokens[i + 1];
                tokens.erase(tokens.begin() + static_cast<long>(i) + 1);
            }
        }
        for (size_t i = 0; i < tokens.size(); ++i) {
            if (i + 2 < tokens.size() && tokens[i + 1] == "-") {  // hyphen range
                Partial lo, hi;
                if (!partial(tokens[i], &lo) || !partial(tokens[i + 2], &hi)) {
                    *error = "bad hyphen range '" + tokens[i] + " - " + tokens[i + 2] + "'";
                    return false;
                }
                set->push_back({Op::Ge, lo.floor()});
                set->push_back(hi.count == 3 ? Cmp{Op::Le, hi.floor()} : Cmp{Op::Lt, hi.next().lowest()});
                i += 2;
                continue;
            }
            if (!one(tokens[i], set, error)) return false;
        }
        return true;
    }

    static bool one(const std::string& token, std::vector<Cmp>* set, std::string* error) {
        std::string op;
        if (token.rfind(">=", 0) == 0 || token.rfind("<=", 0) == 0) op = token.substr(0, 2);
        else if (!token.empty() && (token[0] == '>' || token[0] == '<' || token[0] == '=' || token[0] == '^' ||
                                    token[0] == '~'))
            op = token.substr(0, 1);
        std::string rest = token.substr(op.size());
        if (op == "~" && !rest.empty() && rest[0] == '>') rest = rest.substr(1);  // ~> (Ruby style) = ~
        Partial p;
        if (!partial(rest, &p)) {
            *error = "'" + token + "' is not a version or comparator";
            return false;
        }
        if (p.count == 0) {  // * / x
            if (op == "<" || op == ">") set->push_back({Op::Lt, Version{}.lowest()});  // matches nothing
            return true;
        }
        if (op.empty() && p.wildcard) op = "=";  // 1.2.x: any 1.2.z, not caret
        if (op.empty() || op == "^") {
            set->push_back({Op::Ge, p.floor()});
            Version upper;
            if (p.major > 0 || p.count == 1) upper = Version{p.major + 1, 0, 0, {}, {}};
            else if (p.minor > 0 || p.count == 2) upper = Version{0, p.minor + 1, 0, {}, {}};
            else upper = Version{0, 0, p.patch + 1, {}, {}};
            set->push_back({Op::Lt, upper.lowest()});
        } else if (op == "~") {
            set->push_back({Op::Ge, p.floor()});
            const Version upper = p.count == 1 ? Version{p.major + 1, 0, 0, {}, {}} : Version{p.major, p.minor + 1, 0, {}, {}};
            set->push_back({Op::Lt, upper.lowest()});
        } else if (op == "=") {
            if (p.count == 3) {
                set->push_back({Op::Eq, p.floor()});
            } else {
                set->push_back({Op::Ge, p.floor()});
                set->push_back({Op::Lt, p.next().lowest()});
            }
        } else if (op == ">=") {
            set->push_back({Op::Ge, p.floor()});
        } else if (op == ">") {
            set->push_back(p.count == 3 ? Cmp{Op::Gt, p.floor()} : Cmp{Op::Ge, p.next().lowest()});
        } else if (op == "<") {
            const Version f = p.floor();
            set->push_back({Op::Lt, f.pre.empty() ? f.lowest() : f});
        } else {  // <=
            set->push_back(p.count == 3 ? Cmp{Op::Le, p.floor()} : Cmp{Op::Lt, p.next().lowest()});
        }
        return true;
    }

    static bool intervals(std::string_view text, std::vector<std::vector<Cmp>>* sets, std::string* error) {
        const std::string t(text);
        size_t i = 0;
        size_t added = 0;
        while (i < t.size()) {
            while (i < t.size() && (t[i] == ',' || t[i] == ' ' || t[i] == '\t' || t[i] == '\r' || t[i] == '\n')) ++i;
            if (i >= t.size()) break;
            if (t[i] != '[' && t[i] != '(') {
                *error = "version range '" + t + "': expected '[' or '(' at " + std::to_string(i);
                return false;
            }
            const size_t close = t.find_first_of("])", i);
            if (close == std::string::npos) {
                *error = "version range '" + t + "': unclosed interval";
                return false;
            }
            const bool lower_inclusive = t[i] == '[', upper_inclusive = t[close] == ']';
            const std::string body = t.substr(i + 1, close - i - 1);
            const size_t comma = body.find(',');
            std::vector<Cmp> set;
            if (comma == std::string::npos) {
                Version exact;
                if (!lower_inclusive || !upper_inclusive || !semver::parse(body, &exact)) {
                    *error = "version range '" + t + "': '[" + body + "]' must be an exact version in square brackets";
                    return false;
                }
                set.push_back({Op::Eq, exact});
            } else {
                const std::string_view lo = detail::trim(std::string_view(body).substr(0, comma));
                const std::string_view hi = detail::trim(std::string_view(body).substr(comma + 1));
                if (!lo.empty()) {
                    Version l;
                    if (!semver::parse(lo, &l)) {
                        *error = "version range '" + t + "': bad lower bound '" + std::string(lo) + "'";
                        return false;
                    }
                    set.push_back({lower_inclusive ? Op::Ge : Op::Gt, l});
                }
                if (!hi.empty()) {
                    Version h;
                    if (!semver::parse(hi, &h)) {
                        *error = "version range '" + t + "': bad upper bound '" + std::string(hi) + "'";
                        return false;
                    }
                    set.push_back(upper_inclusive ? Cmp{Op::Le, h} : Cmp{Op::Lt, h.pre.empty() ? h.lowest() : h});
                }
            }
            sets->push_back(std::move(set));
            ++added;
            i = close + 1;
        }
        if (added == 0) {
            *error = "version range '" + t + "': no interval";
            return false;
        }
        return true;
    }
};

}  // namespace kiln::semver
