// SPDX-License-Identifier: MIT
// From matenm/emf-enshrouded v0.6.8, src/core/mods_check.h.
// Copyright (c) 2026 EMF contributors. See licenses/EMF-LICENSE.txt.
#pragma once
// The mod check on join: what an EMF game and an EMF server tell each other about their mods (the core channel
// emf/mods), and what is said when they differ. Pure.
//   The list: one entry per line, "kind id version". kind: c = an EML content mod (it changes the game's data), b = a mod
//   whose mod.json says "side": "both", s = "server", l = "client", u = no side, e = EMF itself ("e emf 0.6.3"). At most kMaxModEntries entries and
//   kMaxModListBytes bytes; a line that is not an entry is skipped, the first of a repeated id counts.
//   Compared: the server's c and b entries by id and version (a player's game without one, or with another version,
//   "misses" it); the player's own c and b entries the server lacks are "extra" (harmless for the world: no note).
#include <algorithm>
#include <cstdint>
#include <optional>
#include <set>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "mod_registry.h"
#include "mods_policy.h"  // valid_list_id

namespace emf::core {

constexpr size_t kMaxModEntries = 512;
constexpr size_t kMaxModListBytes = 16384;
constexpr size_t kMaxModVersionBytes = 32;
constexpr size_t kNoteMaxMods = 3;  // the player's note names at most this many (3 rows on screen), then "and N more"

struct ModEntry {
    char kind = 'u';
    std::string id, version;
};

struct ModDiff {
    std::vector<std::pair<ModEntry, std::string>> missing;  // the server's entry, the player's version ("" = none)
    std::vector<ModEntry> extra;                            // the player's c/b entries the server does not have
};

inline bool mod_kind_compared(char k) { return k == 'c' || k == 'b'; }

inline bool valid_list_version(std::string_view v) {
    if (v.empty() || v.size() > kMaxModVersionBytes) return false;
    for (unsigned char c : v)
        if (c <= ' ' || c >= 0x7F) return false;
    return true;
}

inline std::string encode_mods(const std::vector<ModEntry>& mods) {
    std::string out;
    for (const auto& m : mods) {
        out += m.kind;
        out += ' ';
        out += m.id;
        out += ' ';
        out += m.version;
        out += '\n';
    }
    return out;
}

inline std::optional<std::vector<ModEntry>> decode_mods(std::string_view text, std::string* why) {
    if (text.size() > kMaxModListBytes) {
        if (why) *why = "a mod list over 16384 bytes";
        return std::nullopt;
    }
    std::vector<ModEntry> out;
    std::set<std::string> seen;
    size_t lines = 0;
    while (!text.empty()) {
        const size_t nl = text.find('\n');
        std::string_view line = text.substr(0, nl);
        text = nl == std::string_view::npos ? std::string_view() : text.substr(nl + 1);
        if (!line.empty() && line.back() == '\r') line.remove_suffix(1);
        if (line.empty()) continue;
        if (++lines > kMaxModEntries) {
            if (why) *why = "a mod list of more than 512 mods";
            return std::nullopt;
        }
        // "k id version": exactly three fields
        if (line.size() < 5 || line[1] != ' ') continue;
        const char kind = line[0];
        if (kind != 'c' && kind != 'b' && kind != 's' && kind != 'l' && kind != 'u' && kind != 'e') continue;
        const std::string_view rest = line.substr(2);
        const size_t sp = rest.find(' ');
        if (sp == std::string_view::npos) continue;
        const std::string_view id = rest.substr(0, sp), version = rest.substr(sp + 1);
        if (!valid_list_id(id) || !valid_list_version(version)) continue;
        if (!seen.insert(std::string(id)).second) continue;
        out.push_back({kind, std::string(id), std::string(version)});
    }
    return out;
}

inline ModDiff compare_mods(const std::vector<ModEntry>& server, const std::vector<ModEntry>& player) {
    const auto find = [](const std::vector<ModEntry>& list, const std::string& id) -> const ModEntry* {
        for (const auto& m : list)
            if (m.id == id) return &m;
        return nullptr;
    };
    ModDiff d;
    for (char kind : {'c', 'b'})  // content mods first
        for (const auto& s : server) {
            if (s.kind != kind) continue;
            const ModEntry* p = find(player, s.id);
            if (!p) d.missing.push_back({s, ""});
            else if (p->version != s.version) d.missing.push_back({s, p->version});
        }
    for (const auto& p : player)
        if (mod_kind_compared(p.kind) && !find(server, p.id)) d.extra.push_back(p);
    return d;
}

// This game's list: the EML content mods its data was built with (id, version), then every running mod (loaded or a
// script) by its mod.json side. Entries whose id or version cannot go in the list are left out.
inline std::vector<ModEntry> this_games_mods(const std::vector<ModRecord>& mods,
                                             const std::vector<std::pair<std::string, std::string>>& content) {
    std::vector<ModEntry> out;
    std::set<std::string> seen;
    for (const auto& [id, version] : content)
        if (valid_list_id(id) && valid_list_version(version) && seen.insert(id).second) out.push_back({'c', id, version});
    for (const auto& r : mods) {
        if (r.status != ModStatus::Loaded && r.status != ModStatus::Script) continue;
        if (!valid_list_id(r.id) || !valid_list_version(r.version) || !seen.insert(r.id).second) continue;
        const char kind = r.side == "both" ? 'b' : r.side == "server" ? 's' : r.side == "client" ? 'l' : 'u';
        out.push_back({kind, r.id, r.version});
    }
    return out;
}

// What the player's game shows (once per session; an on-screen note has 3 rows): the risk first, at most kNoteMaxMods
// mods, the rest in the console. "" when nothing is missing.
inline std::string player_note(const ModDiff& d) {
    if (d.missing.empty()) return "";
    bool content = false;
    for (const auto& [m, mine] : d.missing) content |= m.kind == 'c';
    std::string s = content ? "Missing mods for this server (items from them can be lost): "
                            : "Missing mods for this server (their features may not work): ";
    for (size_t i = 0; i < d.missing.size() && i < kNoteMaxMods; ++i) {
        const auto& [m, mine] = d.missing[i];
        s += (i ? ", " : "") + m.id + " " + m.version + " (you: " + (mine.empty() ? std::string("none") : mine) + ")";
    }
    if (d.missing.size() > kNoteMaxMods)
        s += ", and " + std::to_string(d.missing.size() - kNoteMaxMods) + " more in the console";
    s += ". Install the same versions with EMF.exe.";
    return s;
}

// The differences for the log and the console: "same mods", or "missing: ...; extra: ...".
inline std::string diff_text(const ModDiff& d) {
    if (d.missing.empty() && d.extra.empty()) return "same mods";
    std::string s;
    if (!d.missing.empty()) {
        s = "missing: ";
        for (size_t i = 0; i < d.missing.size(); ++i) {
            const auto& [m, mine] = d.missing[i];
            s += (i ? ", " : "") + std::string(1, m.kind) + " " + m.id + " " + m.version + " (" +
                 (mine.empty() ? std::string("none") : mine) + ")";
        }
    }
    if (!d.extra.empty()) {
        s += s.empty() ? "extra: " : "; extra: ";
        for (size_t i = 0; i < d.extra.size(); ++i)
            s += (i ? ", " : "") + std::string(1, d.extra[i].kind) + " " + d.extra[i].id + " " + d.extra[i].version;
    }
    return s;
}

}  // namespace emf::core
