// SPDX-License-Identifier: MIT
// The overlap report (compat.h): pure functions over a hook map document, chain entries and ShroudForge's status text.
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
    o["late"] = row->late;
    return o;
}

// late: a chain entry that loads early (moving it after Kiln changes who hooks first); rule: not a chain entry;
// none: nothing a player can do.
const char* fix_for(const ChainRow* row) {
    if (!row) return "rule";
    if (row->late || !row->on()) return "none";
    return "late";
}

std::string hex_text(const ojson& v) { return v.is_string() ? v.get<std::string>() : std::string(); }

std::string module_at(const ojson& f) {
    const std::string module = f.value("module", std::string());
    if (module.empty()) {
        const std::string address = hex_text(f.value("address", ojson()));
        return address.empty() ? "an unnamed module" : "an unnamed module (jump target " + address + ")";
    }
    return module + "+" + hex_text(f.value("offset", ojson()));
}

}  // namespace

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
    const std::string mode = doc.value("mode", std::string());
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
        if (f.is_object() && !f.value("own", false)) add_unique(suspects, f.value("module", std::string()));
    for (const auto& t : targets)
        for (const char* key : {"foreignBefore", "foreignOver"})
            if (t.contains(key) && t[key].is_object() && !t[key].value("own", false))
                add_unique(suspects, t[key].value("module", std::string()));

    for (const auto& t : targets) {
        if (!t.is_object() || !t.contains("links") || !t["links"].is_array()) continue;
        std::vector<std::string> owners;
        for (const auto& l : t["links"]) add_unique(owners, normalize_owner(l.value("owner", std::string())));
        if (owners.empty()) continue;  // nothing Kiln-hosted is left on it
        const std::string label = t.value("label", std::string("?"));
        for (const char* kind : {"foreign-before", "foreign-over"}) {
            const bool before = std::string(kind) == "foreign-before";
            const char* key = before ? "foreignBefore" : "foreignOver";
            if (!t.contains(key) || !t[key].is_object() || t[key].value("own", false)) continue;
            const ojson& f = t[key];
            const std::string module = f.value("module", std::string());
            const ChainRow* row = module.empty() ? nullptr : find_row(chain, module);
            ojson o;
            o["symbol"] = t.contains("symbol") && t["symbol"].is_string() ? t["symbol"] : ojson(nullptr);
            o["rva"] = t.value("rva", ojson(nullptr));
            o["kind"] = kind;
            ojson fo;
            fo["module"] = module;
            fo["offset"] = f.value("offset", ojson(nullptr));
            if (f.contains("address")) fo["address"] = f["address"];
            if (f.value("inPlace", false)) fo["inPlace"] = true;
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
                else if (row && row->late)
                    msg += module + " is already a late entry; it hooks early on its own.";
                else
                    msg += (module.empty() ? "Kiln cannot tell which module that is" : module + " is not a chain entry, so Kiln cannot move it") +
                           "; add a rule to compat-rules.json or switch one side off.";
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
            else if (std::string(fix) == "rule") msg += "Add a rule to compat-rules.json or switch the other module off.";
            else if (!module.empty()) msg += module + " already loads late.";
            else msg += "Kiln cannot tell which module it was.";
        }
        o["message"] = msg;
        out.push_back(std::move(o));
    }
    return out;
}

}  // namespace kiln::compat
