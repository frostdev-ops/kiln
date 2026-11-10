// SPDX-License-Identifier: MIT
// Compatibility rules (compat_rules.h).
#include "compat_rules.h"

#include <windows.h>

#include <algorithm>
#include <cstring>
#include <set>

#include "nlohmann/json.hpp"

namespace kiln::compat {
namespace {

using nlohmann::json;

constexpr size_t kMaxRules = 512;      // as the launcher's reader (crates/kiln-manage/src/compat.rs MAX_RULES)
constexpr size_t kMaxText = 1024 * 1024;
constexpr size_t kMaxMatcher = 260;
constexpr size_t kMaxMessage = 2000;
constexpr size_t kMaxWhen = 8;          // as MAX_WHEN: bounds the work of one evaluation
constexpr size_t kMaxGlob = 64;         // a module pattern with wildcards: at most this long (MAX_GLOB) ...
constexpr size_t kMaxStars = 4;         // ... and with at most this many `*` (MAX_STARS)

// Characters (Unicode code points) of UTF-8 text, as JSON Schema's maxLength and the launcher count them.
size_t chars_of(std::string_view s) {
    size_t n = 0;
    for (unsigned char c : s)
        if ((c & 0xC0) != 0x80) ++n;
    return n;
}

std::wstring wide(std::string_view s) {
    if (s.empty()) return {};
    const int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
    if (n <= 0) return std::wstring(s.begin(), s.end());  // not UTF-8: the bytes as they are
    std::wstring w(static_cast<size_t>(n), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), w.data(), n);
    return w;
}

std::wstring folded(std::string_view s) {
    std::wstring w = wide(s);
    if (!w.empty()) CharLowerBuffW(w.data(), static_cast<DWORD>(w.size()));
    return w;
}

// 1-128 characters: letters, digits, . _ - :
bool valid_id(std::string_view s) {
    if (s.empty() || s.size() > 128) return false;
    for (char c : s) {
        const bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' ||
                        c == '-' || c == ':';
        if (!ok) return false;
    }
    return true;
}

bool parse_matcher(const json& j, Matcher* out, std::string* why) {
    if (!j.is_object() || j.size() != 1) {
        *why = "a matcher is an object with exactly one key (module, engine, mod or symbol)";
        return false;
    }
    const auto it = j.begin();
    const std::string& key = it.key();
    if (key != "module" && key != "engine" && key != "mod" && key != "symbol") {
        *why = "unknown matcher \"" + key + "\"";
        return false;
    }
    if (!it.value().is_string()) {
        *why = "\"" + key + "\" must be a string";
        return false;
    }
    const std::string v = it.value().get<std::string>();
    const size_t chars = chars_of(v);
    if (chars == 0 || chars > kMaxMatcher) {
        *why = "a matcher's text must be 1-260 characters";
        return false;
    }
    if (key == "module" && v.find_first_of("*?") != std::string::npos &&
        (chars > kMaxGlob || static_cast<size_t>(std::count(v.begin(), v.end(), '*')) > kMaxStars)) {
        *why = "a module pattern with wildcards has at most 64 characters and 4 *";
        return false;
    }
    if (key == "module") out->kind = Matcher::Kind::Module;
    else if (key == "engine") out->kind = Matcher::Kind::Engine;
    else if (key == "symbol") out->kind = Matcher::Kind::Symbol;
    else {
        if (v.find(':') == std::string::npos) {
            *why = "mod matcher \"" + v + "\" must be <engine>:<mod id>";
            return false;
        }
        out->kind = Matcher::Kind::Mod;
    }
    out->value = v;
    return true;
}

// One rule object; false with `why` when it does not validate (the launcher's reader refuses the same rules: unknown
// keys included).
bool parse_rule(const json& j, const char* source, Rule* r, std::string* why) {
    if (!j.is_object()) {
        *why = "not an object";
        return false;
    }
    for (const auto& [key, v] : j.items()) {
        (void)v;
        if (key != "id" && key != "when" && key != "action" && key != "subject" && key != "message") {
            *why = "unknown key \"" + key + "\"";
            return false;
        }
    }
    if (!j.contains("id") || !j["id"].is_string() || !valid_id(j["id"].get<std::string>())) {
        *why = "\"id\" must be 1-128 characters: letters, digits, . _ - :";
        return false;
    }
    r->id = j["id"].get<std::string>();
    r->source = source;
    if (j.contains("when")) {
        if (!j["when"].is_array()) {
            *why = "\"when\" must be a list of matchers";
            return false;
        }
        if (j["when"].size() > kMaxWhen) {
            *why = "more than 8 conditions in \"when\"";
            return false;
        }
        for (const auto& m : j["when"]) {
            Matcher mt;
            if (!parse_matcher(m, &mt, why)) return false;
            r->when.push_back(std::move(mt));
        }
    }
    if (!j.contains("action") || !j["action"].is_string()) {
        *why = "\"action\" must be warn, late or disable";
        return false;
    }
    r->action = j["action"].get<std::string>();
    if (r->action != "warn" && r->action != "late" && r->action != "disable") {
        *why = "\"action\" must be warn, late or disable";
        return false;
    }
    if (j.contains("message")) {
        if (!j["message"].is_string()) {
            *why = "\"message\" must be a string";
            return false;
        }
        r->message = j["message"].get<std::string>();
        if (chars_of(r->message) > kMaxMessage) {
            *why = "\"message\" is longer than 2000 characters";
            return false;
        }
    }
    if (j.contains("subject")) {
        if (!parse_matcher(j["subject"], &r->subject, why)) return false;
        r->has_subject = true;
    }
    if (r->action != "warn" && !r->has_subject) {
        *why = "action " + r->action + " needs a \"subject\"";
        return false;
    }
    return true;
}

// mod matchers: `native:x`, `plugin:x` and `kiln:x` name the same mod; the engine word ignores case, the id does not
std::string canon_mod(std::string_view s) {
    const size_t colon = s.find(':');
    std::string engine(colon == std::string_view::npos ? std::string_view() : s.substr(0, colon));
    const std::string id(colon == std::string_view::npos ? s : s.substr(colon + 1));
    for (char& c : engine)
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    if (engine == "native" || engine == "plugin") engine = "kiln";
    return engine + ":" + id;
}

bool equal_ascii_nocase(std::string_view a, std::string_view b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i) {
        char x = a[i], y = b[i];
        if (x >= 'A' && x <= 'Z') x = static_cast<char>(x - 'A' + 'a');
        if (y >= 'A' && y <= 'Z') y = static_cast<char>(y - 'A' + 'a');
        if (x != y) return false;
    }
    return true;
}

}  // namespace

const char* matcher_key(Matcher::Kind kind) {
    switch (kind) {
        case Matcher::Kind::Engine: return "engine";
        case Matcher::Kind::Mod: return "mod";
        case Matcher::Kind::Symbol: return "symbol";
        default: return "module";
    }
}

std::string matcher_text(const Matcher& m) { return std::string(matcher_key(m.kind)) + " " + m.value; }

ParseResult parse_rules(std::string_view text, const char* source) {
    ParseResult out;
    if (text.size() >= 3 && std::memcmp(text.data(), "\xEF\xBB\xBF", 3) == 0) text.remove_prefix(3);
    if (text.size() > kMaxText) {
        out.error = "larger than 1 MiB";
        return out;
    }
    json doc;
    try {
        doc = json::parse(text.begin(), text.end(), nullptr, true, false);
    } catch (const std::exception& e) {
        out.error = std::string("not valid JSON: ") + e.what();
        return out;
    }
    if (!doc.is_object()) {
        out.error = "the file must hold one { ... } object";
        return out;
    }
    if (!doc.contains("format") || !doc["format"].is_number_integer() || doc["format"].get<int64_t>() != 1) {
        out.error = "\"format\" must be 1";
        return out;
    }
    if (doc.contains("rules") && !doc["rules"].is_array()) {
        out.error = "\"rules\" must be a list";
        return out;
    }
    if (!doc.contains("rules")) {  // no rules is a valid file
        out.ok = true;
        return out;
    }
    std::set<std::string> seen;
    size_t n = 0;
    for (const auto& j : doc["rules"]) {
        ++n;
        std::string why;
        Rule r;
        if (n > kMaxRules) {
            ++out.dropped;
            out.problems.push_back("rule #" + std::to_string(n) + ": more than 512 rules; dropped");
            continue;
        }
        if (!parse_rule(j, source, &r, &why)) {
            ++out.dropped;
            out.problems.push_back("rule #" + std::to_string(n) + (r.id.empty() ? "" : " (" + r.id + ")") + ": " + why + "; dropped");
            continue;
        }
        if (!seen.insert(r.id).second) {
            ++out.dropped;
            out.problems.push_back("rule #" + std::to_string(n) + ": id '" + r.id + "' is used twice in this file; the first one stays");
            continue;
        }
        out.rules.push_back(std::move(r));
    }
    out.ok = true;
    return out;
}

std::vector<Rule> merge_rules(std::vector<Rule> shipped, const std::vector<Rule>& user, std::vector<std::string>* replaced) {
    for (const Rule& u : user) {
        auto it = std::find_if(shipped.begin(), shipped.end(), [&](const Rule& r) { return r.id == u.id; });
        if (it != shipped.end()) {
            *it = u;
            if (replaced) replaced->push_back(u.id);
        } else {
            shipped.push_back(u);
        }
    }
    return shipped;
}

bool glob_match(std::string_view pattern, std::string_view name) {
    // no file name is longer, and rules are checked to be short: bounds the work of a match (as the launcher's glob)
    if (chars_of(pattern) > kMaxMatcher || chars_of(name) > kMaxMatcher) return false;
    const std::wstring p = folded(pattern), n = folded(name);
    size_t pi = 0, ni = 0, star = std::wstring::npos, mark = 0;
    while (ni < n.size()) {
        if (pi < p.size() && p[pi] == L'*') {
            star = pi++;
            mark = ni;
        } else if (pi < p.size() && (p[pi] == L'?' || p[pi] == n[ni])) {
            ++pi;
            ++ni;
        } else if (star != std::wstring::npos) {
            pi = star + 1;
            ni = ++mark;
        } else {
            return false;
        }
    }
    while (pi < p.size() && p[pi] == L'*') ++pi;
    return pi == p.size();
}

bool present(const Matcher& m, const Facts& f) {
    switch (m.kind) {
        case Matcher::Kind::Module:
            return std::any_of(f.modules.begin(), f.modules.end(), [&](const std::string& s) { return glob_match(m.value, s); });
        case Matcher::Kind::Engine:
            return std::any_of(f.engines.begin(), f.engines.end(), [&](const std::string& s) { return equal_ascii_nocase(s, m.value); });
        case Matcher::Kind::Mod: {
            const std::string want = canon_mod(m.value);
            return std::any_of(f.mods.begin(), f.mods.end(), [&](const std::string& s) { return canon_mod(s) == want; });
        }
        case Matcher::Kind::Symbol: return std::find(f.symbols.begin(), f.symbols.end(), m.value) != f.symbols.end();
    }
    return false;
}

std::vector<const Rule*> evaluate(const std::vector<Rule>& rules, const Facts& facts) {
    std::vector<const Rule*> out;
    for (const Rule& r : rules)
        if (std::all_of(r.when.begin(), r.when.end(), [&](const Matcher& m) { return present(m, facts); })) out.push_back(&r);
    return out;
}

}  // namespace kiln::compat
