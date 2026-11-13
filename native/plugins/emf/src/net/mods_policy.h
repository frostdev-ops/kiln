// SPDX-License-Identifier: MIT
// From matenm/emf-enshrouded v0.6.8, src/core/mods_policy.h.
// Copyright (c) 2026 EMF contributors. See licenses/EMF-LICENSE.txt.
#pragma once
// [net] mods_check, mods_required and mods_info in emf\emf_core.ini (the mod check on join; refusing a join): read the
// same way by the core and by EMF.exe's Server tab, so this header needs the standard library only.
//   mods_check: "warn" (the default) or "refuse"; anything else warns, with a line that says so.
//   mods_required: mod ids every player's game must have too (commas or spaces between them), besides the content mods
//   and the "side": "both" mods that are always compared.
//   mods_info: where to get them, said in the chat line of a refusal.
#include <algorithm>
#include <cstddef>
#include <string>
#include <string_view>
#include <vector>

namespace emf::core {

constexpr size_t kMaxRequiredMods = 64;
constexpr size_t kMaxModsInfoBytes = 200;

// A mod id as the mod list carries it: 1-48 of a-z 0-9 _ . -
inline bool valid_list_id(std::string_view id) {
    if (id.empty() || id.size() > 48) return false;
    for (char c : id)
        if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-')) return false;
    return true;
}

struct ModsPolicy {
    bool refuse = false;                // mods_check = refuse
    std::vector<std::string> required;  // mods_required, lower case, each once, in order
    std::string info;                   // mods_info
};

// `problems` (when given) gets one line per value that was not taken as written.
inline ModsPolicy parse_mods_policy(std::string_view check, std::string_view required, std::string_view info,
                                    std::vector<std::string>* problems) {
    const auto say = [&](const std::string& s) {
        if (problems) problems->push_back(s);
    };
    const auto is_space = [](char c) { return c == ' ' || c == '\t' || c == '\r' || c == '\n'; };
    const auto lower = [](std::string s) {
        for (char& c : s)
            if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
        return s;
    };
    const auto trim = [&](std::string_view s) {
        while (!s.empty() && is_space(s.front())) s.remove_prefix(1);
        while (!s.empty() && is_space(s.back())) s.remove_suffix(1);
        return s;
    };
    ModsPolicy p;
    const std::string mode = lower(std::string(trim(check)));
    if (mode == "refuse")
        p.refuse = true;
    else if (!mode.empty() && mode != "warn")
        say("[net] mods_check: \"" + std::string(trim(check)) + "\" is neither warn nor refuse: warn");
    bool full = false;
    size_t i = 0;
    while (i < required.size()) {
        while (i < required.size() && (required[i] == ',' || is_space(required[i]))) ++i;
        size_t j = i;
        while (j < required.size() && required[j] != ',' && !is_space(required[j])) ++j;
        if (j > i) {
            const std::string id = lower(std::string(required.substr(i, j - i)));
            if (!valid_list_id(id)) {
                say("[net] mods_required: \"" + std::string(required.substr(i, j - i)) +
                    "\" is not a mod id (a-z, 0-9, _ . -, at most 48): skipped");
            } else if (std::find(p.required.begin(), p.required.end(), id) == p.required.end()) {
                if (p.required.size() < kMaxRequiredMods)
                    p.required.push_back(id);
                else
                    full = true;
            }
        }
        i = j;
    }
    if (full) say("[net] mods_required: more than " + std::to_string(kMaxRequiredMods) + " ids: the rest are skipped");
    std::string text(trim(info));
    for (char& c : text)
        if (static_cast<unsigned char>(c) < 0x20 || c == 0x7F) c = ' ';
    if (text.size() > kMaxModsInfoBytes) {
        say("[net] mods_info: over " + std::to_string(kMaxModsInfoBytes) + " bytes (a letter such as \xC3\xA5 is 2): cut");
        size_t n = kMaxModsInfoBytes;
        while (n > 0 && (static_cast<unsigned char>(text[n]) & 0xC0) == 0x80) --n;  // not inside a UTF-8 character
        text.resize(n);
    }
    p.info = std::string(trim(text));
    return p;
}

}  // namespace emf::core
