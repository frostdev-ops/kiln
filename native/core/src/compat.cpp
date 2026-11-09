// SPDX-License-Identifier: MIT
// Overlap report and compatibility rules inside kiln-core (compat.h).
#include "compat.h"

#include <algorithm>
#include <cctype>
#include <mutex>
#include <set>

#include "api.h"
#include "common.h"
#include "log.h"
#include "native_mods.h"
#include "profile.h"
#include "services/hooks_internal.h"

#include <psapi.h>  // after windows.h (common.h)

namespace kiln::compat {
namespace {

namespace fs = std::filesystem;

struct FileInfo {
    std::string path, source, error;
    size_t rules = 0;
};

std::mutex g_mu;
std::vector<Rule> g_rules;                         // shipped, then the player's
std::vector<FileInfo> g_files;                     // the rule files that exist
std::vector<std::pair<std::string, std::string>> g_early;  // rule id -> what apply_early() did
std::set<std::string> g_rule_off;                  // "<engine>:<id>" switched off by a rule: still part of this start for the rules
std::set<std::string> g_logged;                    // lines logged once
std::set<std::string> g_prev_late;                 // `late` rules the last session's hooks.json reports as applied
bool g_inited = false;

const char* const kProxyStatus[] = {"loaded", "failed", "missing", "refused", "duplicate", "skipped", "late"};

std::string file_name_of(const std::string& path) {
    const size_t at = path.find_last_of("\\/");
    return at == std::string::npos ? path : path.substr(at + 1);
}

// The proxy's chain entries, a late one with the status the late phase gave it.
std::vector<ChainRow> chain_rows() {
    std::vector<ChainRow> rows;
    const KilnBootInfo& bi = boot_info();
    const auto late = native_mods::late_rows();
    for (uint32_t i = 0; i < bi.chain_count; ++i) {
        const KilnChainEntry& e = bi.chain[i];
        ChainRow r;
        r.path = std::string(sv(e.path));
        r.file = file_name_of(r.path);
        r.source = e.source == KILN_CHAIN_SOURCE_EMF ? "emf" : e.source == KILN_CHAIN_SOURCE_PROFILE ? "profile" : "chain";
        r.status = e.status < 7 ? kProxyStatus[e.status] : "failed";
        if (e.status == KILN_CHAIN_LATE) {
            r.late = true;
            for (const auto& l : late)
                if (l.chain_index == i && l.status != "pending") r.status = l.status;
        }
        rows.push_back(std::move(r));
    }
    return rows;
}

std::vector<std::string> loaded_module_names() {
    std::vector<std::string> out;
    HMODULE mods[2048];
    DWORD needed = 0;
    if (!K32EnumProcessModules(GetCurrentProcess(), mods, sizeof mods, &needed)) return out;
    const size_t n = std::min<size_t>(needed / sizeof(HMODULE), 2048);
    for (size_t i = 0; i < n; ++i) {
        wchar_t path[2048];
        const DWORD len = GetModuleFileNameW(mods[i], path, 2048);
        if (len == 0 || len >= 2048) continue;
        out.push_back(file_name_of(to_utf8(std::wstring(path, len))));
    }
    return out;
}

void add_unique(std::vector<std::string>& v, const std::string& s) {
    if (!s.empty() && std::find(v.begin(), v.end(), s) == v.end()) v.push_back(s);
}

bool ieq(const std::string& a, const char* b) { return glob_match(b, a); }  // no wildcard in b: a case-insensitive compare

// What is present now (compat_rules.h Facts).
Facts gather_facts(const std::vector<ChainRow>& rows, const std::vector<std::string>& symbols, bool sf_hosted) {
    Facts f;
    f.modules = loaded_module_names();
    for (const ChainRow& r : rows)
        if (r.on()) add_unique(f.modules, r.file);
    f.symbols = symbols;
    f.engines.push_back("kiln");
    if (sf_hosted) add_unique(f.engines, "shroudforge");
    for (const std::string& m : f.modules) {  // a few loaders every player knows by name
        if (ieq(m, "shroudforge-runtime.dll")) add_unique(f.engines, "shroudforge");
        if (ieq(m, "emf_core.dll")) add_unique(f.engines, "emf");
        if (ieq(m, "enshwick.dll")) add_unique(f.engines, "enshwick");
    }
    const auto& prof = profile::state();
    if (prof.mode == KILN_PROFILE_ACTIVE || prof.mode == KILN_PROFILE_ONCE) {
        for (uint32_t k = 0; k < KILN_PROFILE_KINDS; ++k) {
            for (const auto& e : prof.mods[k]) {
                if (!e.enabled || e.id.empty()) continue;
                const std::string engine = k == KILN_PROFILE_KIND_EMF ? "emf" : "kiln";
                add_unique(f.mods, engine + ":" + e.id);
                if (k == KILN_PROFILE_KIND_EMF) add_unique(f.engines, "emf");
            }
        }
        for (uint32_t i = 0; i < profile::kEngineKindCount; ++i) {
            for (const auto& e : prof.engine_mods[i]) {
                if (!e.enabled) continue;
                add_unique(f.mods, std::string(profile::kEngineKinds[i]) + ":" + e.id);
                add_unique(f.engines, profile::kEngineKinds[i]);
            }
        }
    }
    for (const std::string& m : g_rule_off) add_unique(f.mods, m);
    return f;
}

std::string read_state_file(const wchar_t* name) {
    std::string text;
    read_file(paths().state / name, &text);
    return text;
}

bool log_once(const std::string& key) { return g_logged.insert(key).second; }

std::string overlap_key(const ojson& o) {
    return o.value("kind", std::string()) + "|" + (o.contains("symbol") && o["symbol"].is_string() ? o["symbol"].get<std::string>() : o.value("hook", std::string())) +
           "|" + o["foreign"].value("module", std::string()) + "|" + (o["foreign"].contains("offset") && o["foreign"]["offset"].is_string() ? o["foreign"]["offset"].get<std::string>() : std::string());
}

struct Outcome {
    bool applied = true;
    std::string note;
};

bool subject_rows(const Rule& r, const std::vector<ChainRow>& rows, std::vector<const ChainRow*>* on) {
    bool any = false;
    for (const ChainRow& row : rows) {
        if (!glob_match(r.subject.value, row.file)) continue;
        any = true;
        if (row.on()) on->push_back(&row);
    }
    return any;
}

Outcome outcome_of(const Rule& r, const std::vector<ChainRow>& rows, const Facts& facts) {
    Outcome o;
    if (r.action == "warn") return o;
    const std::string what = matcher_text(r.subject);
    if (r.action == "late") {
        if (r.subject.kind != Matcher::Kind::Module) {
            o.applied = false;
            o.note = "only a chain entry (a module) can load late, not " + what + "; nothing was changed";
            return o;
        }
        std::vector<const ChainRow*> on;
        subject_rows(r, rows, &on);
        if (on.empty()) {
            o.applied = false;
            o.note = "no chain entry named " + r.subject.value + " in this start; nothing to move";
        } else if (std::all_of(on.begin(), on.end(), [](const ChainRow* c) { return c->late; })) {
            o.note = r.subject.value + " already loads late";
        } else {
            o.applied = false;
            o.note = "rule " + r.id + " asks " + r.subject.value + " to load late; apply it in the launcher";
        }
        return o;
    }
    // disable
    if (r.subject.kind == Matcher::Kind::Module) {
        std::vector<const ChainRow*> on;
        subject_rows(r, rows, &on);
        const bool loaded = !on.empty() || (present(r.subject, facts));
        if (loaded) {
            o.applied = false;
            o.note = "rule " + r.id + " asks " + r.subject.value + " to be disabled; apply it in the launcher";
        } else {
            o.note = r.subject.value + " is not loaded in this start";
        }
    } else if (r.subject.kind == Matcher::Kind::Mod) {
        const auto early = std::find_if(g_early.begin(), g_early.end(), [&](const auto& e) { return e.first == r.id; });
        if (early != g_early.end()) {
            o.note = early->second;
        } else if (present(r.subject, facts) && g_rule_off.count(r.subject.value) == 0) {
            o.applied = false;
            o.note = "rule " + r.id + " asks " + r.subject.value + " to be disabled, but it is not in the profile's lists "
                     "(a drop-in folder?); apply it in the launcher";
        } else {
            o.note = r.subject.value + " is not in this start";
        }
    } else {
        if (present(r.subject, facts)) {
            o.applied = false;
            o.note = "rule " + r.id + " asks " + what + " to be disabled; apply it in the launcher";
        } else {
            o.note = what + " is not in this start";
        }
    }
    return o;
}

ojson matcher_json(const Matcher& m) {
    ojson o;
    o[matcher_key(m.kind)] = m.value;
    return o;
}

// A `late` rule that was applied last session stays in force while its subject still loads late, whether or not its
// `when` still holds: a rule on {"symbol": ...} that moved the DLL late may have removed the overlap it fired on, and
// would otherwise switch itself off and put the DLL back to the proxy at the next start (the overlap would return).
bool sticky_late(const Rule& r, const std::vector<ChainRow>& rows) {
    if (r.action != "late" || !g_prev_late.count(r.id) || r.subject.kind != Matcher::Kind::Module) return false;
    std::vector<const ChainRow*> on;
    subject_rows(r, rows, &on);
    return !on.empty() && std::all_of(on.begin(), on.end(), [](const ChainRow* c) { return c->late; });
}

// The "rules" section: the rules that fire in this start, with what became of them. Logs each once.
ojson rules_report(const std::vector<ChainRow>& rows, const Facts& facts) {
    ojson out = ojson::array();
    std::set<const Rule*> fired;
    for (const Rule* r : evaluate(g_rules, facts)) fired.insert(r);
    for (const Rule& r : g_rules) {
        const bool early = std::any_of(g_early.begin(), g_early.end(), [&](const auto& e) { return e.first == r.id; });
        if (!fired.count(&r) && !early && !sticky_late(r, rows)) continue;
        const Outcome oc = outcome_of(r, rows, facts);
        ojson o;
        o["id"] = r.id;
        o["action"] = r.action;
        o["applied"] = oc.applied;
        o["message"] = r.message;
        o["subject"] = r.has_subject ? matcher_json(r.subject) : ojson(nullptr);
        o["source"] = r.source;
        if (!oc.note.empty()) o["note"] = oc.note;
        out.push_back(std::move(o));
        if (r.action == "warn") {
            if (log_once("rule|" + r.id)) KLOG_W("compat: rule %s: %s", r.id.c_str(), r.message.c_str());
        } else if (!oc.applied) {
            if (log_once("rule|" + r.id + "|" + oc.note)) KLOG_W("compat: %s (%s)", oc.note.c_str(), r.message.c_str());
        }
    }
    return out;
}

ojson files_json() {
    ojson out = ojson::array();
    for (const FileInfo& f : g_files) {
        ojson o;
        o["path"] = f.path;
        o["source"] = f.source;
        o["rules"] = f.rules;
        o["error"] = f.error.empty() ? ojson(nullptr) : ojson(f.error);
        out.push_back(std::move(o));
    }
    return out;
}

// Adds "pid", "overlaps", "rules" and "ruleFiles" to the hook map (called by the map writer, registry lock not held).
void fill(ojson& doc) {
    std::lock_guard lock(g_mu);
    doc["pid"] = GetCurrentProcessId();
    const auto rows = chain_rows();
    const std::string sf = read_state_file(L"shroudforge.json");
    const ojson overlaps = compute_overlaps(doc, rows, parse_sf_status(sf));
    std::vector<std::string> symbols;
    for (const auto& o : overlaps) {
        if (o.contains("symbol") && o["symbol"].is_string()) add_unique(symbols, o["symbol"].get<std::string>());
        if (log_once("overlap|" + overlap_key(o))) KLOG_W("compat: overlap: %s", o.value("message", std::string()).c_str());
    }
    doc["overlaps"] = overlaps;
    const Facts facts = gather_facts(rows, symbols, sf_running(sf));
    doc["rules"] = rules_report(rows, facts);
    doc["ruleFiles"] = files_json();
}

void map_extension(void* json, const char*) {
    if (json) fill(*static_cast<ojson*>(json));
}

void load_file(const fs::path& file, const char* source, std::vector<Rule>* rules) {
    std::error_code ec;
    if (!fs::exists(file, ec)) return;
    FileInfo info;
    info.path = u8(file);
    info.source = source;
    std::string text;
    if (!read_file(file, &text)) {
        info.error = "cannot be read";
        KLOG_W("compat: %s cannot be read; its rules are ignored", info.path.c_str());
    } else {
        ParseResult r = parse_rules(text, source);
        if (!r.ok) {
            info.error = r.error;
            KLOG_W("compat: %s: %s; its rules are ignored", info.path.c_str(), r.error.c_str());
        } else {
            for (const std::string& p : r.problems) {
                KLOG_W("compat: %s: %s", info.path.c_str(), p.c_str());
                info.error += (info.error.empty() ? "" : "; ") + p;
            }
            info.rules = r.rules.size();
            *rules = std::move(r.rules);
        }
    }
    g_files.push_back(std::move(info));
}

void CALLBACK settle_timer(PVOID, BOOLEAN) { hooks::mark_map_dirty(); }

}  // namespace

void init() {
    std::lock_guard lock(g_mu);
    if (g_inited) return;
    g_inited = true;
    // The last session's report, before this one overwrites it (the first write is at boot end).
    {
        std::string text;
        if (read_file(paths().state / L"hooks.json", &text)) {
            const ojson prev = ojson::parse(text, nullptr, false);
            if (prev.is_object() && prev.contains("rules") && prev["rules"].is_array())
                for (const auto& r : prev["rules"])
                    if (r.is_object() && r.value("action", std::string()) == "late" && r.value("applied", false) && r.contains("id") && r["id"].is_string())
                        g_prev_late.insert(r["id"].get<std::string>());
        }
    }
    std::vector<Rule> shipped, user;
    load_file(paths().kiln / L"compat-rules.json", "shipped", &shipped);
    load_file(paths().config / L"compat-rules.json", "user", &user);
    std::vector<std::string> replaced;
    g_rules = merge_rules(std::move(shipped), user, &replaced);
    for (const std::string& id : replaced) KLOG_I("compat: the player's rule '%s' replaces the shipped one", id.c_str());
    if (!g_rules.empty()) KLOG_I("compat: %zu compatibility rule(s)", g_rules.size());
    hooks::add_map_extension(&map_extension);  // before hooks::boot_complete() writes the first hooks.json
}

void apply_early() {
    std::lock_guard lock(g_mu);
    if (g_rules.empty()) return;
    const auto rows = chain_rows();
    const Facts facts = gather_facts(rows, {}, false);
    for (const Rule* r : evaluate(g_rules, facts)) {
        if (r->action != "disable" || r->subject.kind != Matcher::Kind::Mod) continue;
        const std::string& ref = r->subject.value;
        const size_t colon = ref.find(':');
        std::string engine = ref.substr(0, colon), id = ref.substr(colon + 1);
        for (char& c : engine) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
        const std::string why = "switched off by compat rule '" + r->id + "': " + r->message;
        bool done = false;
        if (engine == "kiln" || engine == "native" || engine == "plugin") {
            for (const char* kind : {"kiln", "native", "plugin"}) done |= profile::disable_entry(kind, id, why);
        } else {
            done = profile::disable_entry(engine, id, why);
        }
        if (!done) continue;
        g_rule_off.insert(ref);
        g_early.emplace_back(r->id, "switched off in the profile before the plugins read it");
        KLOG_W("compat: rule %s switched %s off: %s", r->id.c_str(), ref.c_str(), r->message.c_str());
    }
}

void boot_done() {
    const bool hooked = hooks::chain::ready();
    if (!hooked) {  // safe idle: no hook map, so no hooks.json; the warnings still go to kiln.log
        ojson doc;
        doc["targets"] = ojson::array();
        doc["foreign"] = ojson::array();
        fill(doc);
        return;
    }
    // ShroudForge reports its hooks a little after boot: write the map again once it had time to
    for (const auto& p : plugin_records()) {
        if (p.file.find("shroudforge") == std::string::npos || std::string_view(p.status) != "loaded") continue;
        HANDLE t = nullptr;
        for (DWORD ms : {12000u, 45000u})
            CreateTimerQueueTimer(&t, nullptr, &settle_timer, nullptr, ms, 0, WT_EXECUTEONLYONCE);
        break;
    }
}

}  // namespace kiln::compat
