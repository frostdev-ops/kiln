// SPDX-License-Identifier: MIT
// The overlap report and the rule outcomes (compat.h): pure functions over a hook map document, chain entries,
// ShroudForge's status text and the facts of one start.
#include <algorithm>
#include <cctype>

#include "compat.h"

namespace kiln::compat {
namespace {

std::string lower(std::string s) {
    for (char& c : s) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return s;
}

bool same_name(const std::string& a, const std::string& b) { return !a.empty() && lower(a) == lower(b); }

std::string join(const std::vector<std::string>& v, const char* sep = ", ") {
    std::string out;
    for (const auto& s : v) out += (out.empty() ? "" : sep) + s;
    return out;
}

void add_unique(std::vector<std::string>& v, const std::string& s) {
    if (!s.empty() && std::find(v.begin(), v.end(), s) == v.end()) v.push_back(s);
}

// The chain entry named `module`; an entry that is on wins over one that is not.
const ChainRow* find_row(const std::vector<ChainRow>& rows, const std::string& module) {
    const ChainRow* found = nullptr;
    for (const ChainRow& r : rows) {
        if (!same_name(r.file, module)) continue;
        if (r.on()) return &r;
        if (!found) found = &r;
    }
    return found;
}

ojson chain_entry_json(const ChainRow* row) {
    if (!row) return nullptr;
    ojson o;
    o["path"] = row->path;
    o["source"] = row->source;
    o["status"] = row->status == "late" ? "skipped" : row->status;  // "late" = the core has not loaded it (yet)
    o["late"] = row->late && !row->late_ignored;
    if (row->late_ignored) o["lateIgnored"] = true;  // named in [late], but another loader had started it first
    return o;
}

// Another loader starts this chain entry (or it is that loader), so moving it behind Kiln is not in Kiln's hands.
bool not_ours_to_move(const ChainRow* row) { return row && (row->loader || row->started_by_loader || row->late_ignored); }

// late: a chain entry that loads early (moving it after Kiln changes who hooks first); rule: not a chain entry, or
// another loader starts it; none: nothing a player can do.
const char* fix_for(const ChainRow* row) {
    if (!row) return "rule";
    if ((row->late && !row->late_ignored) || !row->on()) return "none";
    if (not_ours_to_move(row)) return "rule";
    return "late";
}

// Why [late] cannot be the fix for this module (a sentence ending in a full stop).
std::string why_not_late(const ChainRow* row, const std::string& module) {
    if (!row) return module.empty() ? "Kiln cannot tell which module that is." : module + " is not a chain entry, so Kiln cannot move it.";
    if (row->loader) return module + " is EMF's loader: it starts EMF itself, so it always loads first.";
    if (row->late_ignored) return module + " is named in [late], but another loader started it first, so [late] had no effect.";
    if (row->started_by_loader) return module + " is started by EMF's loader (dinput8-chain.ini), so [late] cannot move it.";
    if (row->late) return module + " already loads late.";
    return module + " is not loaded by Kiln's chain (" + row->status + ").";
}

std::string hex_text(const ojson& v) { return v.is_string() ? v.get<std::string>() : std::string(); }

std::string module_at(const ojson& f) {
    const std::string module = json_text(f, "module");
    if (module.empty()) {
        const std::string address = hex_text(f.value("address", ojson()));
        return address.empty() ? "an unnamed module" : "an unnamed module (jump target " + address + ")";
    }
    return module + "+" + hex_text(f.value("offset", ojson()));
}

}  // namespace

std::string json_text(const ojson& obj, const char* key) {
    if (!obj.is_object()) return {};
    const auto it = obj.find(key);
    return it != obj.end() && it->is_string() ? it->get<std::string>() : std::string();
}

bool json_flag(const ojson& obj, const char* key) {
    if (!obj.is_object()) return false;
    const auto it = obj.find(key);
    return it != obj.end() && it->is_boolean() && it->get<bool>();
}

std::string overlap_key(const ojson& o) {
    const ojson none = ojson::object();
    const ojson& foreign = o.is_object() && o.contains("foreign") && o["foreign"].is_object() ? o["foreign"] : none;
    const bool symbol = o.is_object() && o.contains("symbol") && o["symbol"].is_string();
    return json_text(o, "kind") + "|" + (symbol ? json_text(o, "symbol") : json_text(o, "hook")) + "|" +
           json_text(foreign, "module") + "|" + json_text(foreign, "offset");
}

std::string normalize_owner(std::string_view owner) {
    std::string s(owner);
    for (const char* prefix : {"service:", "plugin:"}) {
        const std::string p(prefix);
        if (s.compare(0, p.size(), p) == 0) {
            s.erase(0, p.size());
            break;
        }
    }
    if (s.compare(0, 5, "kiln-") == 0 && s.size() > 5) s.erase(0, 5);
    if (s.size() > 4 && lower(s.substr(s.size() - 4)) == ".dll") s.erase(s.size() - 4);
    return s;
}

bool sf_running(std::string_view text) {
    const ojson doc = ojson::parse(text.begin(), text.end(), nullptr, false);
    if (!doc.is_object()) return false;
    const std::string mode = json_text(doc, "mode");
    return mode == "hosted" || mode == "self-hosted";
}

std::vector<Refused> parse_sf_status(std::string_view text) {
    std::vector<Refused> out;
    const ojson doc = ojson::parse(text.begin(), text.end(), nullptr, false);
    if (!doc.is_object() || !doc.contains("native") || !doc["native"].is_string()) return out;
    const std::string native = doc["native"].get<std::string>();
    const std::string key = "game_thread=unavailable(";
    const size_t at = native.find(key);
    if (at == std::string::npos) return out;
    const size_t start = at + key.size();
    const size_t end = native.find(')', start);
    if (end == std::string::npos || end == start) return out;
    const std::string what = native.substr(start, end - start);
    Refused r;
    const size_t colon = what.find(':');
    if (colon == std::string::npos) {
        r.hook = "game-thread";
        r.reason = what;
    } else {
        r.hook = what.substr(0, colon);
        r.reason = what.substr(colon + 1);
    }
    out.push_back(std::move(r));
    return out;
}

ojson compute_overlaps(const ojson& map, const std::vector<ChainRow>& chain, const std::vector<Refused>& refused) {
    ojson out = ojson::array();
    const ojson no_array = ojson::array();
    const ojson& targets = map.contains("targets") && map["targets"].is_array() ? map["targets"] : no_array;
    const ojson& foreign = map.contains("foreign") && map["foreign"].is_array() ? map["foreign"] : no_array;

    // Modules that patched game code (own = Kiln's)
    std::vector<std::string> suspects;
    for (const auto& f : foreign)
        if (f.is_object() && !json_flag(f, "own")) add_unique(suspects, json_text(f, "module"));
    for (const auto& t : targets)
        for (const char* key : {"foreignBefore", "foreignOver"})
            if (t.is_object() && t.contains(key) && t[key].is_object() && !json_flag(t[key], "own"))
                add_unique(suspects, json_text(t[key], "module"));

    for (const auto& t : targets) {
        if (!t.is_object() || !t.contains("links") || !t["links"].is_array()) continue;
        std::vector<std::string> owners;
        for (const auto& l : t["links"]) add_unique(owners, normalize_owner(json_text(l, "owner")));
        if (owners.empty()) continue;  // nothing Kiln-hosted is left on it
        std::string label = json_text(t, "label");
        if (label.empty()) label = "?";
        for (const char* kind : {"foreign-before", "foreign-over"}) {
            const bool before = std::string(kind) == "foreign-before";
            const char* key = before ? "foreignBefore" : "foreignOver";
            if (!t.contains(key) || !t[key].is_object() || json_flag(t[key], "own")) continue;
            const ojson& f = t[key];
            const std::string module = json_text(f, "module");
            const ChainRow* row = module.empty() ? nullptr : find_row(chain, module);
            ojson o;
            o["symbol"] = t.contains("symbol") && t["symbol"].is_string() ? t["symbol"] : ojson(nullptr);
            o["rva"] = t.contains("rva") && t["rva"].is_string() ? t["rva"] : ojson(nullptr);
            o["kind"] = kind;
            ojson fo;
            fo["module"] = module;
            fo["offset"] = f.contains("offset") && f["offset"].is_string() ? f["offset"] : ojson(nullptr);
            if (f.contains("address")) fo["address"] = f["address"];
            if (json_flag(f, "inPlace")) fo["inPlace"] = true;
            o["foreign"] = fo;
            o["owners"] = owners;
            const char* fix = before ? fix_for(row) : "none";
            o["fix"] = fix;
            o["chainEntry"] = chain_entry_json(row);
            std::string msg;
            const std::string who = join(owners);
            if (before) {
                msg = module_at(f) + " hooked " + label + " before " + who + "; Kiln chained over it. ";
                if (std::string(fix) == "late")
                    msg += "Load " + module + " late (kiln/chain.ini [late], or the launcher's \"Load after Kiln and ShroudForge\"): "
                           "Kiln then hooks first and " + module + " hooks over it.";
                else if (row && row->late && !row->late_ignored)
                    msg += module + " is already a late entry; it hooks early on its own.";
                else
                    msg += why_not_late(row, module) + " Add a rule to compat-rules.json or switch one side off.";
            } else {
                msg = module_at(f) + " hooked over Kiln's " + label + " (" + who + "). Kiln's detour stays armed behind their jump; "
                      "nothing to fix unless a feature misbehaves.";
            }
            o["message"] = msg;
            out.push_back(std::move(o));
        }
    }

    for (const Refused& r : refused) {
        const bool foreign_cause = r.reason == "original-bytes-mismatch" || r.reason == "safe-code-write-rejected";
        // the only module that patched game code is the likely culprit
        const std::string module = foreign_cause && suspects.size() == 1 ? suspects[0] : std::string();
        const ChainRow* row = module.empty() ? nullptr : find_row(chain, module);
        ojson o;
        o["symbol"] = nullptr;
        o["rva"] = nullptr;
        o["kind"] = "refused";
        ojson fo;
        fo["module"] = module.empty() ? ojson(nullptr) : ojson(module);
        fo["offset"] = nullptr;
        o["foreign"] = fo;
        o["owners"] = ojson::array({"shroudforge"});
        o["hook"] = r.hook;
        o["reason"] = r.reason;
        o["suspects"] = suspects;
        const char* fix = foreign_cause && row ? fix_for(row) : (foreign_cause && !suspects.empty() ? "rule" : "none");
        o["fix"] = fix;
        o["chainEntry"] = chain_entry_json(row);
        std::string msg = "ShroudForge refused its " + r.hook + " hook (" + r.reason + "): ";
        if (!foreign_cause) {
            msg += "this is not another module's doing that Kiln can see.";
        } else {
            msg += "another module patched the game code first";
            if (!module.empty()) msg += ", probably " + module;
            else if (!suspects.empty()) msg += " (modules that patched game code: " + join(suspects) + ")";
            msg += ". ";
            if (std::string(fix) == "late") msg += "Load " + module + " late (kiln/chain.ini [late], or the launcher's \"Load after Kiln and ShroudForge\").";
            else if (std::string(fix) == "rule")
                msg += (not_ours_to_move(row) ? why_not_late(row, module) + " " : std::string()) +
                       "Add a rule to compat-rules.json or switch the other module off.";
            else if (!module.empty()) msg += why_not_late(row, module);
            else msg += "Kiln cannot tell which module it was.";
        }
        o["message"] = msg;
        out.push_back(std::move(o));
    }
    return out;
}

namespace {

// The chain rows a module matcher names, and those of them that are on.
bool subject_rows(const Rule& r, const std::vector<ChainRow>& rows, std::vector<const ChainRow*>* on) {
    bool any = false;
    for (const ChainRow& row : rows) {
        if (!glob_match(r.subject.value, row.file)) continue;
        any = true;
        if (row.on()) on->push_back(&row);
    }
    return any;
}

}  // namespace

Outcome outcome_of(const Rule& r, const std::vector<ChainRow>& rows, const Facts& facts, const RuleContext& ctx) {
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
        auto any_of = [&](bool ChainRow::*flag) {
            return std::any_of(on.begin(), on.end(), [&](const ChainRow* c) { return c->*flag; });
        };
        if (on.empty()) {
            o.applied = false;
            o.note = "no chain entry named " + r.subject.value + " in this start; nothing to move";
        } else if (any_of(&ChainRow::loader)) {
            // the proxy loads EMF's loader normally whatever chain.ini says: it holds the start of the game
            o.applied = false;
            o.note = r.subject.value + " is EMF's loader: it starts EMF itself, so it always loads first and cannot be moved late";
        } else if (any_of(&ChainRow::late_ignored)) {
            o.applied = false;
            o.note = r.subject.value + " is named in chain.ini [late], but another loader started it first, so it did not load late";
        } else if (std::all_of(on.begin(), on.end(), [](const ChainRow* c) { return c->late; })) {
            o.note = r.subject.value + " already loads late";
        } else if (any_of(&ChainRow::started_by_loader)) {
            o.applied = false;
            o.note = r.subject.value + " is started by EMF's loader (dinput8-chain.ini), so chain.ini [late] cannot move it";
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
        const auto early = std::find_if(ctx.early.begin(), ctx.early.end(), [&](const auto& e) { return e.first == r.id; });
        if (early != ctx.early.end()) {
            if (ctx.proxy_loaded.count(r.id)) {
                // the profile entry is off, but the proxy started the DLL in DllMain, before this rule could be read
                o.applied = false;
                o.note = "rule " + r.id + " asks " + r.subject.value + " to be disabled, but its DLL was loaded by the proxy before "
                         "kiln-core started; apply it in the launcher (activate the profile again)";
            } else {
                o.note = early->second;
            }
        } else if (present(r.subject, facts) && ctx.rule_off.count(r.subject.value) == 0) {
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

}  // namespace kiln::compat
