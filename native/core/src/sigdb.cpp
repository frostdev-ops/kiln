// SPDX-License-Identifier: MIT
#include "sigdb.h"

#include <algorithm>
#include <cstring>
#include <fstream>
#include <map>
#include <mutex>
#include <optional>
#include <set>

#include "image.h"
#include "log.h"
#include "nlohmann/json.hpp"

namespace kiln::sigdb {
namespace {

using json = nlohmann::ordered_json;

struct FindSpec {
    std::string text;
    Pattern pattern;
    int64_t offset = 0;
    std::string scope_symbol;
    size_t scope_size = 0;
};

struct SymbolDef {
    std::string name;
    bool probe = false;  // "@name": only captures, every strategy evaluated
    std::vector<FindSpec> find;
};

struct ConstDef {
    std::optional<int64_t> fixed;
    int min_votes = 1;
};

struct FeatureDef {
    std::string name;
    std::vector<std::string> symbols, constants;
};

struct Resolved {
    bool ok = false;
    int64_t value = 0;  // address (symbols, absolute) or constant
    std::string source;
    std::string error;
};

struct Vote {
    int64_t value;
    std::string source;
};

int hexval(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

std::string hex(int64_t v) {
    char b[32];
    std::snprintf(b, sizeof b, "0x%llx", static_cast<unsigned long long>(v));
    return b;
}

// One self-contained set of definitions resolved against the running image. The core is one Resolver; each mod
// fragment is another, with the core as its parent so a fragment strategy can be scoped to a core symbol.
class Resolver {
public:
    explicit Resolver(const Resolver* parent = nullptr) : parent_(parent) {}

    std::string load_error;  // set by load(); non-empty = the file is unusable as a whole

    // Reads one definitions file into this resolver. require_prefix (a fragment's "<id>/") rejects any name that is
    // not in that namespace. Appends, so the core can merge server.json + server.d/*.json.
    bool load(const std::filesystem::path& file, const std::string& require_prefix) {
        std::ifstream in(file);
        if (!in) {
            load_error = "cannot open " + to_utf8(file.wstring());
            return false;
        }
        json doc;
        try {
            doc = json::parse(in, nullptr, true, true);
        } catch (const std::exception& e) {
            load_error = std::string("parse error: ") + e.what();
            return false;
        }
        if (doc.value("format", 0) != 1) {
            load_error = "unsupported format";
            return false;
        }
        auto check_prefix = [&](const std::string& name) -> bool {
            if (require_prefix.empty()) return true;
            const std::string bare = (!name.empty() && name[0] == '@') ? name.substr(1) : name;
            if (bare.rfind(require_prefix, 0) == 0) return true;
            load_error = "name '" + name + "' must start with '" + require_prefix + "'";
            return false;
        };
        for (auto& [name, sym] : doc["symbols"].items()) {
            if (!check_prefix(name)) return false;
            SymbolDef def;
            def.name = name;
            def.probe = !name.empty() && name[0] == '@';
            for (auto& f : sym["find"]) {
                FindSpec spec;
                spec.text = f.value("pattern", "");
                spec.offset = f.value("offset", 0);
                if (f.contains("scope")) {
                    spec.scope_symbol = f["scope"].value("symbol", "");
                    spec.scope_size = f["scope"].value("size", 0);
                }
                std::string perr;
                if (!parse_pattern(spec.text, spec.pattern, perr)) {
                    load_error = name + ": " + perr;
                    return false;
                }
                def.find.push_back(std::move(spec));
            }
            if (!defs_.emplace(name, std::move(def)).second) {
                load_error = "symbol " + name + " is defined twice";
                return false;
            }
        }
        if (doc.contains("constants")) {
            for (auto& [name, c] : doc["constants"].items()) {
                if (!check_prefix(name)) return false;
                ConstDef def;
                if (c.contains("value")) def.fixed = c["value"].get<int64_t>();
                def.min_votes = c.value("min_votes", 1);
                if (!const_defs_.emplace(name, def).second) {
                    load_error = "constant " + name + " is defined twice";
                    return false;
                }
            }
        }
        if (doc.contains("features")) {
            for (auto& [name, f] : doc["features"].items()) {
                if (!check_prefix(name)) return false;
                FeatureDef def;
                def.name = name;
                if (f.contains("symbols")) def.symbols = f["symbols"].get<std::vector<std::string>>();
                if (f.contains("constants")) def.constants = f["constants"].get<std::vector<std::string>>();
                features_.push_back(std::move(def));
            }
        }
        return true;
    }

    void resolve() {
        const double t0 = now_ms();
        symbols_.clear();
        constants_.clear();
        feature_ok_.clear();
        captures_.clear();
        matched_.clear();
        state_.clear();

        // Fixed point: an unscoped strategy may capture (via a rel32 hole) the very symbol another strategy is
        // scoped to, so we resolve, promote rel32-captured symbols, and repeat until nothing new resolves. This is
        // the "resolve rel32-captured symbols before scoped searches" fix: a scope symbol reached only through a
        // rel32 capture is available by the time a scoped strategy needs it.
        bool progress = true;
        for (size_t guard = 0; progress && guard <= defs_.size() + 4; ++guard) {
            progress = false;
            for (auto& [name, def] : defs_)
                if (try_resolve(name, def)) progress = true;
            if (promote_symbol_votes()) progress = true;
        }

        // Final pass: record errors for anything still unresolved, and finalize constants.
        for (auto& [name, def] : defs_) {
            if (state_[name] == 2) continue;
            Resolved& r = symbols_[name];
            if (r.error.empty()) r.error = "unresolved";
        }
        promote_symbol_votes();  // one last promotion in case the loop hit the guard
        finalize_constants();

        for (const FeatureDef& f : features_) {
            bool ok = true;
            for (auto& s : f.symbols) ok = ok && symbols_.count(s) && symbols_[s].ok;
            for (auto& c : f.constants) ok = ok && constants_.count(c) && constants_[c].ok;
            feature_ok_[f.name] = ok;
        }
        ms_ = now_ms() - t0;
    }

    bool symbol(std::string_view name, uintptr_t* out) const {
        if (!name.empty() && name[0] == '@') return false;
        auto it = symbols_.find(std::string(name));
        if (it == symbols_.end() || !it->second.ok) return false;
        if (out) *out = static_cast<uintptr_t>(it->second.value);
        return true;
    }
    bool constant(std::string_view name, int64_t* out) const {
        auto it = constants_.find(std::string(name));
        if (it == constants_.end() || !it->second.ok) return false;
        if (out) *out = it->second.value;
        return true;
    }
    bool feature(std::string_view name) const {
        auto it = feature_ok_.find(std::string(name));
        return it != feature_ok_.end() && it->second;
    }
    std::vector<std::string> feature_names() const {
        std::vector<std::string> v;
        for (auto& f : features_) v.push_back(f.name);
        return v;
    }
    std::vector<std::string> failed_features() const {
        std::vector<std::string> v;
        for (auto& [n, ok] : feature_ok_)
            if (!ok) v.push_back(n);
        return v;
    }
    double ms() const { return ms_; }

    void summarize(FragmentSummary& s) const {
        s.ms = ms_;
        for (auto& [n, r] : symbols_) {
            if (!n.empty() && n[0] == '@') continue;
            ++s.symbols;
            if (r.ok) ++s.symbols_ok;
            else s.problems.push_back(n + ": " + (r.error.empty() ? "unresolved" : r.error));
        }
        for (auto& [n, r] : constants_) {
            ++s.constants;
            if (r.ok) ++s.constants_ok;
            else s.problems.push_back(n + ": " + (r.error.empty() ? "unresolved" : r.error));
        }
        for (auto& [n, ok] : feature_ok_) {
            ++s.features;
            if (ok) ++s.features_ok;
        }
    }

    void write_report(const std::filesystem::path& file) const {
        json doc;
        doc["kiln"] = std::string(kVersion);
        doc["exe_sha256"] = image().sha256;
        doc["codeview"] = image().codeview;
        doc["resolve_ms"] = ms_;
        for (auto& [n, r] : symbols_) {
            json j;
            j["ok"] = r.ok;
            if (r.ok && n[0] != '@') j["rva"] = hex(r.value - static_cast<int64_t>(image().base));
            if (!r.source.empty()) j["source"] = r.source;
            if (!r.error.empty()) j["error"] = r.error;
            doc["symbols"][n] = j;
        }
        for (auto& [n, r] : constants_) {
            json j;
            j["ok"] = r.ok;
            if (r.ok) j["value"] = hex(r.value);
            if (!r.source.empty()) j["source"] = r.source;
            if (!r.error.empty()) j["error"] = r.error;
            doc["constants"][n] = j;
        }
        for (auto& [n, ok] : feature_ok_) doc["features"][n] = ok;
        std::ofstream out(file, std::ios::trunc);
        out << doc.dump(2) << '\n';
    }

    void log_unresolved() const {
        for (auto& [n, r] : symbols_)
            if (!r.ok && n[0] != '@') KLOG_W("sigdb: symbol %s unresolved: %s", n.c_str(), r.error.c_str());
        for (auto& [n, r] : constants_)
            if (!r.ok) KLOG_W("sigdb: constant %s unresolved: %s", n.c_str(), r.error.c_str());
    }

    size_t symbol_count_ok() const {
        size_t n = 0;
        for (auto& [k, r] : symbols_) if (r.ok) ++n;
        return n;
    }
    size_t symbol_count() const { return symbols_.size(); }
    size_t const_count_ok() const {
        size_t n = 0;
        for (auto& [k, r] : constants_) if (r.ok) ++n;
        return n;
    }
    size_t const_count() const { return constants_.size(); }

private:
    const Resolver* parent_;
    std::map<std::string, SymbolDef> defs_;
    std::map<std::string, ConstDef> const_defs_;
    std::vector<FeatureDef> features_;
    std::map<std::string, Resolved> symbols_;
    std::map<std::string, Resolved> constants_;
    std::map<std::string, bool> feature_ok_;
    // Captures keyed by strategy tag ("name.find[i]"), overwritten on re-evaluation so a strategy never votes twice.
    struct Capture {
        std::string name;
        bool is_symbol;  // rel32 hole: the absolute target is a symbol address
        int64_t value;
    };
    std::map<std::string, std::vector<Capture>> captures_;
    std::set<std::string> matched_;  // strategy tags already matched (not re-scanned)
    std::map<std::string, int> state_;  // 0 unseen/retry, 2 done (resolved or final-failed)
    double ms_ = 0;

    bool address_of(const std::string& name, int64_t* out) const {
        auto it = symbols_.find(name);
        if (it != symbols_.end() && it->second.ok) {
            *out = it->second.value;
            return true;
        }
        return parent_ && parent_->address_of(name, out);
    }

    // Returns true when `name` became resolved on this call (new progress).
    bool try_resolve(const std::string& name, const SymbolDef& def) {
        if (state_[name] == 2) return false;
        Resolved& r = symbols_[name];
        const bool before = r.ok;
        r.error.clear();
        bool pending_scope = false;  // a strategy is waiting on an unresolved scope symbol
        size_t unique = 0;

        for (size_t i = 0; i < def.find.size(); ++i) {
            const FindSpec& f = def.find[i];
            const std::string tag = name + ".find[" + std::to_string(i) + "]";
            const uint8_t* begin = image().text;
            const uint8_t* end = begin + image().text_size;
            if (!f.scope_symbol.empty()) {
                int64_t base = 0;
                if (!address_of(f.scope_symbol, &base)) {
                    pending_scope = true;
                    r.error += (r.error.empty() ? "" : "; ") + tag + ": scope symbol " + f.scope_symbol +
                               " unresolved";
                    continue;
                }
                begin = reinterpret_cast<const uint8_t*>(base);
                end = std::min(end, begin + f.scope_size);
            }
            if (matched_.count(tag)) {  // already matched in a previous iteration
                ++unique;
                if (!def.probe) {
                    // non-probe resolved earlier would be state 2; reaching here means probe context
                }
                continue;
            }
            const auto hits = scan(f.pattern, begin, end, 2);
            if (hits.size() != 1) {
                r.error += (r.error.empty() ? "" : "; ") + tag + ": " + std::to_string(hits.size()) +
                           (hits.size() > 1 ? "+ matches" : " matches");
                continue;
            }
            matched_.insert(tag);
            record_captures(f.pattern, hits[0], tag);
            ++unique;
            if (!def.probe) {
                r.ok = true;
                r.value = static_cast<int64_t>(reinterpret_cast<uintptr_t>(hits[0]) + f.offset);
                r.source = tag;
                r.error.clear();
                state_[name] = 2;
                return !before;
            }
        }

        if (def.probe) {
            if (unique == def.find.size()) {
                r.ok = true;
                r.source = std::to_string(unique) + "/" + std::to_string(def.find.size()) + " strategies matched";
                r.error.clear();
                state_[name] = 2;
                return !before;
            }
            // not all matched: keep retrying if any strategy is only waiting on a scope symbol
            if (!pending_scope) {
                r.source = std::to_string(unique) + "/" + std::to_string(def.find.size()) + " strategies matched";
                state_[name] = 2;  // scopes known, still incomplete: a real failure
            }
            return false;
        }

        // non-probe, nothing matched: give up only when no strategy is blocked on a scope symbol
        if (!pending_scope) state_[name] = 2;
        return false;
    }

    void record_captures(const Pattern& p, const uint8_t* match, const std::string& tag) {
        std::vector<Capture>& caps = captures_[tag];
        caps.clear();
        for (const Hole& h : p.holes) {
            int64_t v = 0;
            hole_value(p, match, h.name, &v);
            caps.push_back({h.name, h.kind == Hole::Rel32, v});
        }
    }

    // Build symbol (rel32) votes from all matched strategies and promote agreeing ones. Returns true if any symbol
    // became newly resolved.
    bool promote_symbol_votes() {
        std::map<std::string, std::vector<Vote>> votes;
        for (auto& [tag, caps] : captures_)
            for (const Capture& c : caps)
                if (c.is_symbol) votes[c.name].push_back({c.value, tag});
        bool progress = false;
        for (auto& [name, vs] : votes) {
            Resolved& r = symbols_[name];
            const bool agree = std::all_of(vs.begin(), vs.end(), [&](const Vote& v) { return v.value == vs[0].value; });
            if (!agree) {
                r.ok = false;
                r.error = "captures disagree";
                continue;
            }
            if (r.ok) {
                if (r.value != vs[0].value) {
                    r.ok = false;
                    r.error = "pattern and captures disagree (" + hex(r.value - image().base) + " vs " +
                              hex(vs[0].value - image().base) + ")";
                }
                continue;
            }
            r.ok = true;
            r.value = vs[0].value;
            r.source = "captured by " + vs[0].source;
            r.error.clear();
            if (state_[name] != 2) {
                state_[name] = 2;
                progress = true;
            }
        }
        return progress;
    }

    void finalize_constants() {
        std::map<std::string, std::vector<Vote>> votes;
        for (auto& [tag, caps] : captures_)
            for (const Capture& c : caps)
                if (!c.is_symbol) votes[c.name].push_back({c.value, tag});
        std::map<std::string, ConstDef> all = const_defs_;
        for (auto& [name, vs] : votes) all.try_emplace(name);
        for (auto& [name, def] : all) {
            Resolved r;
            auto& vs = votes[name];
            const bool agree = std::all_of(vs.begin(), vs.end(), [&](const Vote& v) { return v.value == vs[0].value; });
            if (!agree) {
                r.error = "captures disagree:";
                for (const Vote& v : vs) r.error += " " + hex(v.value) + "@" + v.source;
            } else if (def.fixed) {
                if (!vs.empty() && vs[0].value != *def.fixed) {
                    r.error = "fixed value " + hex(*def.fixed) + " but captured " + hex(vs[0].value);
                } else {
                    r.ok = true;
                    r.value = *def.fixed;
                    r.source = vs.empty() ? "fixed" : "fixed, confirmed by " + std::to_string(vs.size()) + " captures";
                }
            } else if (vs.empty() || static_cast<int>(vs.size()) < def.min_votes) {
                r.error = std::to_string(vs.size()) + " captures, need " + std::to_string(def.min_votes);
            } else {
                r.ok = true;
                r.value = vs[0].value;
                r.source = std::to_string(vs.size()) + " captures agree";
            }
            constants_[name] = r;
        }
    }
};

// ---- global state ----
Resolver g_core;
bool g_core_loaded = false;
double g_ms = 0;

std::mutex g_frag_mutex;
std::map<std::string, std::unique_ptr<Resolver>> g_fragments;  // id -> resolver (core as parent)

std::string prefix_of(std::string_view name) {
    const size_t slash = name.find('/');
    return slash == std::string_view::npos ? std::string() : std::string(name.substr(0, slash));
}

}  // namespace

bool parse_pattern(std::string_view text, Pattern& out, std::string& error) {
    out = Pattern{};
    size_t i = 0;
    while (i < text.size()) {
        while (i < text.size() && (text[i] == ' ' || text[i] == '\t')) ++i;
        if (i >= text.size()) break;
        if (text[i] == '{') {
            const size_t close = text.find('}', i);
            if (close == std::string_view::npos) {
                error = "unterminated hole";
                return false;
            }
            const std::string_view body = text.substr(i + 1, close - i - 1);
            const size_t colon = body.rfind(':');
            if (colon == std::string_view::npos || colon == 0) {
                error = "hole needs name:type";
                return false;
            }
            Hole h;
            h.name = std::string(body.substr(0, colon));
            const std::string_view kind = body.substr(colon + 1);
            if (kind == "d8") h.kind = Hole::U8, h.width = 1;
            else if (kind == "d16") h.kind = Hole::U16, h.width = 2;
            else if (kind == "d32") h.kind = Hole::U32, h.width = 4;
            else if (kind == "s32") h.kind = Hole::S32, h.width = 4;
            else if (kind == "rel32") h.kind = Hole::Rel32, h.width = 4;
            else {
                error = "unknown hole type '" + std::string(kind) + "'";
                return false;
            }
            h.offset = out.bytes.size();
            out.bytes.insert(out.bytes.end(), h.width, -1);
            out.holes.push_back(std::move(h));
            i = close + 1;
            continue;
        }
        if (text[i] == '?') {
            out.bytes.push_back(-1);
            i += (i + 1 < text.size() && text[i + 1] == '?') ? 2 : 1;
            continue;
        }
        const int hi = hexval(text[i]);
        const int lo = i + 1 < text.size() ? hexval(text[i + 1]) : -1;
        if (hi < 0 || lo < 0) {
            error = "bad byte at column " + std::to_string(i);
            return false;
        }
        out.bytes.push_back(hi * 16 + lo);
        i += 2;
    }
    // Anchor: the longest run of literal bytes.
    size_t best = 0, best_off = 0, run = 0;
    for (size_t k = 0; k <= out.bytes.size(); ++k) {
        if (k < out.bytes.size() && out.bytes[k] >= 0) {
            ++run;
            continue;
        }
        if (run > best) best = run, best_off = k - run;
        run = 0;
    }
    if (best == 0) {
        error = "pattern has no literal bytes";
        return false;
    }
    out.anchor_offset = best_off;
    out.anchor_length = best;
    return true;
}

std::vector<const uint8_t*> scan(const Pattern& p, const uint8_t* begin, const uint8_t* end, size_t limit) {
    std::vector<const uint8_t*> hits;
    const size_t n = p.bytes.size();
    if (!begin || end <= begin || static_cast<size_t>(end - begin) < n) return hits;
    std::vector<uint8_t> anchor(p.anchor_length);
    for (size_t k = 0; k < p.anchor_length; ++k) anchor[k] = static_cast<uint8_t>(p.bytes[p.anchor_offset + k]);
    const std::boyer_moore_horspool_searcher searcher(anchor.begin(), anchor.end());
    const uint8_t* search_begin = begin + p.anchor_offset;
    const uint8_t* search_end = end - n + p.anchor_offset + p.anchor_length;
    while (search_begin < search_end) {
        const auto [first, last] = searcher(search_begin, search_end);
        if (first == search_end) break;
        const uint8_t* start = first - p.anchor_offset;
        bool ok = true;
        for (size_t k = 0; k < n; ++k) {
            if (p.bytes[k] >= 0 && start[k] != static_cast<uint8_t>(p.bytes[k])) {
                ok = false;
                break;
            }
        }
        if (ok) {
            hits.push_back(start);
            if (hits.size() >= limit) break;
        }
        search_begin = first + 1;
    }
    return hits;
}

bool hole_value(const Pattern& p, const uint8_t* match, std::string_view name, int64_t* out) {
    for (const Hole& h : p.holes) {
        if (h.name != name) continue;
        const uint8_t* at = match + h.offset;
        int64_t v = 0;
        switch (h.kind) {
            case Hole::U8: v = at[0]; break;
            case Hole::U16: { uint16_t x; std::memcpy(&x, at, 2); v = x; } break;
            case Hole::U32: { uint32_t x; std::memcpy(&x, at, 4); v = x; } break;
            case Hole::S32: { int32_t x; std::memcpy(&x, at, 4); v = x; } break;
            case Hole::Rel32: {
                int32_t x;
                std::memcpy(&x, at, 4);
                v = static_cast<int64_t>(reinterpret_cast<uintptr_t>(at + 4) + x);
            } break;
        }
        if (out) *out = v;
        return true;
    }
    return false;
}

bool load_and_resolve(const std::filesystem::path& definitions) {
    if (!g_core.load(definitions, "")) {
        KLOG_E("sigdb: %s", g_core.load_error.c_str());
        return false;
    }
    // Per-service definition files: <dir>/<stem>.d/*.json, merged in name order. A duplicate name is an error in
    // that file only; the rest still loads.
    std::vector<std::filesystem::path> extra;
    std::error_code ec;
    const auto dir = definitions.parent_path() / (definitions.stem().wstring() + L".d");
    for (auto& e : std::filesystem::directory_iterator(dir, ec))
        if (e.path().extension() == L".json") extra.push_back(e.path());
    std::sort(extra.begin(), extra.end());
    for (const auto& f : extra)
        if (!g_core.load(f, ""))
            KLOG_E("sigdb: %s: %s", to_utf8(f.filename().wstring()).c_str(), g_core.load_error.c_str());

    g_core.resolve();
    g_core_loaded = true;
    g_ms = g_core.ms();
    g_core.log_unresolved();
    KLOG_I("sigdb: %zu/%zu symbols, %zu/%zu constants resolved in %.1f ms", g_core.symbol_count_ok(),
           g_core.symbol_count(), g_core.const_count_ok(), g_core.const_count(), g_ms);
    return true;
}

bool symbol(std::string_view name, uintptr_t* out) { return g_core.symbol(name, out); }
bool constant(std::string_view name, int64_t* out) { return g_core.constant(name, out); }
bool feature(std::string_view name) { return g_core.feature(name); }
std::vector<std::string> features() { return g_core.feature_names(); }
std::vector<std::string> failed_features() { return g_core.failed_features(); }
double resolve_ms() { return g_ms; }
void write_report(const std::filesystem::path& file) { g_core.write_report(file); }

bool load_fragment(std::string_view id, const std::filesystem::path& file, const std::filesystem::path& report,
                   FragmentSummary* summary, std::string* error) {
    const std::string prefix = std::string(id) + "/";
    auto res = std::make_unique<Resolver>(&g_core);
    if (!res->load(file, prefix)) {
        if (error) *error = res->load_error;
        return false;
    }
    res->resolve();
    if (!report.empty()) res->write_report(report);
    if (summary) res->summarize(*summary);
    std::lock_guard lock(g_frag_mutex);
    g_fragments[std::string(id)] = std::move(res);
    return true;
}

bool unload_fragment(std::string_view id) {
    std::lock_guard lock(g_frag_mutex);
    return g_fragments.erase(std::string(id)) != 0;
}

bool any_symbol(std::string_view name, uintptr_t* out) {
    if (g_core.symbol(name, out)) return true;
    const std::string pfx = prefix_of(name);
    if (pfx.empty()) return false;
    std::lock_guard lock(g_frag_mutex);
    auto it = g_fragments.find(pfx);
    return it != g_fragments.end() && it->second->symbol(name, out);
}

bool any_constant(std::string_view name, int64_t* out) {
    if (g_core.constant(name, out)) return true;
    const std::string pfx = prefix_of(name);
    if (pfx.empty()) return false;
    std::lock_guard lock(g_frag_mutex);
    auto it = g_fragments.find(pfx);
    return it != g_fragments.end() && it->second->constant(name, out);
}

bool any_feature(std::string_view name) {
    if (g_core.feature(name)) return true;
    const std::string pfx = prefix_of(name);
    if (pfx.empty()) return false;
    std::lock_guard lock(g_frag_mutex);
    auto it = g_fragments.find(pfx);
    return it != g_fragments.end() && it->second->feature(name);
}

}  // namespace kiln::sigdb
