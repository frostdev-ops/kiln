// SPDX-License-Identifier: MIT
// Strict, bounded declarations; portable so malformed sidecars are host-tested without Wine.
#pragma once
#include <array>
#include <algorithm>
#include <map>
#include <set>
#include <string>
#include <vector>
#include <stdexcept>
#include "nlohmann/json.hpp"
namespace kw {
constexpr size_t kBootProgramBytes = 65536;
constexpr std::array<const char*, 16> kBootLists = {"tpl", "tplOptional", "read", "readOptional", "write", "writeOptional",
    "singletonsRead", "singletonsWrite", "otherTpl", "otherRead", "otherWrite", "with", "without", "anyOf", "eventsIn", "eventsOut"};
struct BootProgram {
    std::string key;
    uint32_t flags = 0x14; // SYSTEM | HOST_ONLY
    std::array<std::vector<std::string>, 16> lists;
};
inline bool boot_name(std::string_view name, bool scoped = false) {
    if (name.empty() || name.size() > 120) return false;
    for (char c : name)
        if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '_' && !(scoped && c == ':')) return false;
    return true;
}
inline bool boot_mod_id(std::string_view id) {
    if (id.empty() || id.size() > 64 || !((id[0] >= 'a' && id[0] <= 'z') || (id[0] >= '0' && id[0] <= '9'))) return false;
    for (char c : id) if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '.' && c != '_' && c != '-') return false;
    return true;
}
inline std::vector<BootProgram> parse_boot_programs(std::string_view text) {
    if (text.size() > kBootProgramBytes) throw std::runtime_error("kiln.programs.json exceeds 64 KiB");
    std::vector<std::set<std::string>> keys(33);
    auto callback = [&](int depth, nlohmann::json::parse_event_t event, nlohmann::json& value) {
        if (depth < 0 || depth > 32) throw std::runtime_error("JSON nesting exceeds 32");
        if (event == nlohmann::json::parse_event_t::object_start) keys[depth].clear();
        if (event == nlohmann::json::parse_event_t::key && !keys[depth - 1].insert(value.get<std::string>()).second)
            throw std::runtime_error("duplicate JSON key");
        return true;
    };
    auto json = nlohmann::json::parse(text, callback);
    if (!json.is_object() || json.size() != 2 || !json.contains("format") || json["format"] != 1 ||
        !json.contains("programs") || !json["programs"].is_object() || json["programs"].size() > 8 || json["programs"].empty())
        throw std::runtime_error("expected format 1 and 1..8 programs");
    std::vector<BootProgram> out;
    for (auto& [key, value] : json["programs"].items()) {
        if (!boot_name(key) || key.size() > 63 || !value.is_object()) throw std::runtime_error("bad program key or descriptor");
        BootProgram program; program.key = key;
        for (auto& [field, list] : value.items()) {
            if (field == "flags") {
                if (!list.is_array()) throw std::runtime_error("flags must be an array");
                program.flags = 0;
                const std::map<std::string, uint32_t> flags = {{"system",4},{"init",8},{"hostOnly",16},{"exclusive",128},{"chunked",512},{"pinned",1024},{"barrier",2048}};
                std::set<std::string> unique_flags;
                for (auto& flag : list) {
                    if (!flag.is_string() || !flags.count(flag.get<std::string>()) || !unique_flags.insert(flag.get<std::string>()).second) throw std::runtime_error("unknown program flag");
                    program.flags |= flags.at(flag.get<std::string>());
                }
                continue;
            }
            auto at = std::find(kBootLists.begin(), kBootLists.end(), field);
            if (at == kBootLists.end() || !list.is_array() || list.size() > 160) throw std::runtime_error("unknown field or invalid access list");
            const size_t index = static_cast<size_t>(at - kBootLists.begin());
            std::set<std::string> unique;
            for (auto& name : list) {
                if (!name.is_string() || !boot_name(name.get<std::string>(), index == 6 || index == 7) || !unique.insert(name.get<std::string>()).second)
                    throw std::runtime_error("invalid or repeated access name");
                program.lists[index].push_back(name.get<std::string>());
            }
        }
        auto count = [&](std::initializer_list<size_t> ids) { size_t total=0; for (auto id : ids) total+=program.lists[id].size(); return total; };
        if (count({0,1})>8 || count({2,3,4,5})>42 || count({6})>64 || count({7})>64 || count({8})>9 ||
            count({9,10})>92 || count({14,15})>24) throw std::runtime_error("program exceeds engine access limits");
        for (const auto& group : std::vector<std::vector<size_t>>{{0,1},{2,3,4,5},{11,12,13}}) {
            std::set<std::string> names;
            for (auto id : group) for (const auto& name : program.lists[id])
                if (!names.insert(name).second) throw std::runtime_error("component appears twice in one access group");
        }
        const bool query=count({0,1,2,3,4,5,11,12,13})!=0;
        if ((program.flags&4) ? query : count({0,1,2,3,4,5,11,13})==0)
            throw std::runtime_error("system/query flag and entity access lists disagree");
        out.push_back(std::move(program));
    }
    return out;
}
// Profile entries win even when disabled; duplicate drop-in IDs are ambiguous and reserve nothing.
struct BootCandidate { std::string id; bool enabled = true, profile = false, server = true; size_t token = 0; };
inline std::vector<size_t> select_boot_candidates(const std::vector<BootCandidate>& input, bool dropins) {
    std::set<std::string> listed;
    std::map<std::string, size_t> profiles, drops;
    for (const auto& item : input) {
        if (item.profile) { listed.insert(item.id); ++profiles[item.id]; }
        else if (item.enabled) ++drops[item.id];
    }
    std::vector<size_t> result;
    for (const auto& item : input) {
        if (!item.enabled || !item.server || !boot_mod_id(item.id)) continue;
        if (item.profile ? profiles[item.id] == 1 : dropins && !listed.count(item.id) && drops[item.id] == 1) result.push_back(item.token);
    }
    return result;
}
} // namespace kw
