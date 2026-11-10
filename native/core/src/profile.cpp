// SPDX-License-Identifier: MIT
// Reads the boot profile (profile.h). Validation follows schema/profile.schema.json; any violation makes the whole
// profile INVALID (no mods), so a half-written or hand-broken file never loads a surprising mod set.
#include "profile.h"

#include <algorithm>
#include <fstream>
#include <regex>

#include "log.h"
#include "nlohmann/json.hpp"

namespace kiln::profile {
namespace {

using nlohmann::json;
namespace fs = std::filesystem;

State g_state;

constexpr size_t kMaxProfileBytes = 16 * 1024 * 1024;
constexpr size_t kMaxModsPerKind = 4096;
constexpr int kMaxJsonDepth = 128;

const char* const kKinds[KILN_PROFILE_KINDS] = {"kiln", "native", "plugin", "emf"};

bool is_absolute(std::string_view p) {
    auto sep = [](char c) { return c == '\\' || c == '/'; };
    const bool drive = p.size() >= 3 && ((p[0] >= 'A' && p[0] <= 'Z') || (p[0] >= 'a' && p[0] <= 'z')) && p[1] == ':' &&
                       sep(p[2]);
    return drive || (p.size() >= 2 && sep(p[0]) && sep(p[1]));
}

bool has_dotdot(std::string_view p) {
    size_t start = 0;
    for (size_t i = 0; i <= p.size(); ++i) {
        if (i == p.size() || p[i] == '\\' || p[i] == '/') {
            if (p.substr(start, i - start) == "..") return true;
            start = i + 1;
        }
    }
    return false;
}

struct Invalid {
    std::string what;
};

[[noreturn]] void bad(const std::string& what) { throw Invalid{what}; }

bool read_bounded(const fs::path& file, std::string* text) {
    std::ifstream in(file, std::ios::binary);
    if (!in) return false;
    char chunk[65536];
    while (in) {
        in.read(chunk, sizeof chunk);
        const size_t n = static_cast<size_t>(in.gcount());
        if (text->size() + n > kMaxProfileBytes) bad("the profile exceeds 16 MiB");
        text->append(chunk, n);
    }
    return !in.bad();
}

std::string str(const json& o, const char* key, const std::string& where, bool required) {
    if (!o.contains(key) || o[key].is_null()) {
        if (required) bad(where + "\"" + key + "\" is required");
        return {};
    }
    if (!o[key].is_string()) bad(where + "\"" + key + "\" must be a string");
    return o[key].get<std::string>();
}

std::string game_path(const json& o, const char* key, const std::string& where, bool required) {
    const std::string p = str(o, key, where, required);
    if (!required && p.empty() && (!o.contains(key) || o[key].is_null())) return {};
    if (p.empty()) bad(where + "\"" + key + "\" must not be empty");
    if (p.size() > 32767 || std::any_of(p.begin(), p.end(), [](unsigned char c) { return c < 32; }))
        bad(where + "\"" + key + "\" must be a Windows path without control characters (at most 32767 bytes)");
    if (has_dotdot(p)) bad(where + "\"" + key + "\" must not contain '..' (" + p + ")");
    return p;
}

void parse(const std::string& text) {
    if (text.size() > kMaxProfileBytes) bad("the profile exceeds 16 MiB");
    json doc;
    try {
        doc = json::parse(text, [](int depth, json::parse_event_t, json&) {
            if (depth > kMaxJsonDepth) bad("JSON nesting exceeds 128 levels");
            return true;
        }, true, true);
    } catch (const std::exception& e) {
        bad(std::string("not valid JSON: ") + e.what());
    }
    if (!doc.is_object()) bad("the file must hold one { ... } object");
    if (!doc.contains("format") || !doc["format"].is_number_integer() || doc["format"].get<int64_t>() != 1)
        bad("\"format\" must be 1");
    static const std::regex kId("^[a-z0-9][a-z0-9-]{0,47}$");
    static const std::regex kModId("^[a-z0-9][a-z0-9._-]{0,63}$");
    static const std::regex kSemver("^[vV]?(0|[1-9][0-9]*)(\\.(0|[1-9][0-9]*)){0,2}(-[0-9A-Za-z-]+(\\.[0-9A-Za-z-]+)*)?(\\+[0-9A-Za-z-]+(\\.[0-9A-Za-z-]+)*)?$");
    static const std::regex kHash("^kdh1:[0-9a-f]{64}$");
    static const std::regex kSha("^[0-9a-f]{64}$");
    const std::string id = str(doc, "id", "", true);
    if (!std::regex_match(id, kId)) bad("\"id\" '" + id + "' must match [a-z0-9][a-z0-9-]{0,47}");
    if (!doc.contains("generation") || !doc["generation"].is_number_integer() || doc["generation"].get<int64_t>() < 1)
        bad("\"generation\" must be an integer >= 1");
    g_state.id = id;
    g_state.generation = doc["generation"].get<uint64_t>();
    g_state.name = str(doc, "name", "", false);
    const std::string config_dir = game_path(doc, "configDir", "", false);
    std::string data_dir;
    if (doc.contains("dataDir") && !doc["dataDir"].is_null()) data_dir = game_path(doc, "dataDir", "", true);
    uint32_t drop = 0;
    if (doc.contains("dropins")) {
        const json& d = doc["dropins"];
        if (d.is_boolean()) {
            drop = d.get<bool>() ? 0xFu : 0u;
        } else if (d.is_array()) {
            for (const auto& k : d) {
                bool known = false;
                for (uint32_t i = 0; i < KILN_PROFILE_KINDS; ++i)
                    if (k.is_string() && k.get<std::string>() == kKinds[i]) drop |= 1u << i, known = true;
                // engine kinds: their plugins read "dropins" from json() themselves
                for (uint32_t i = 0; i < kEngineKindCount; ++i)
                    if (k.is_string() && k.get<std::string>() == kEngineKinds[i]) known = true;
                if (!known) bad("\"dropins\" holds an unknown kind " + k.dump());
            }
        } else if (!d.is_null()) {
            bad("\"dropins\" must be true, false or a list of kinds");
        }
    }
    if (!doc.contains("mods") || !doc["mods"].is_object()) bad("\"mods\" must be an object");
    std::vector<Entry> mods[KILN_PROFILE_KINDS + kEngineKindCount];
    for (const auto& [kind_name, list] : doc["mods"].items()) {
        uint32_t kind = KILN_PROFILE_KINDS + kEngineKindCount;
        for (uint32_t i = 0; i < KILN_PROFILE_KINDS; ++i)
            if (kind_name == kKinds[i]) kind = i;
        for (uint32_t i = 0; i < kEngineKindCount; ++i)
            if (kind_name == kEngineKinds[i]) kind = KILN_PROFILE_KINDS + i;
        if (kind == KILN_PROFILE_KINDS + kEngineKindCount) {
            KLOG_W("profile: unknown mod kind \"mods.%s\" ignored (a newer launcher?)", kind_name.c_str());
            continue;
        }
        if (!list.is_array()) bad("\"mods." + kind_name + "\" must be an array");
        if (list.size() > kMaxModsPerKind) bad("\"mods." + kind_name + "\" exceeds 4096 entries");
        size_t n = 0;
        for (const auto& m : list) {
            const std::string where = "mods." + kind_name + "[" + std::to_string(n++) + "]: ";
            if (!m.is_object()) bad(where + "must be an object");
            Entry e;
            e.kind = kind;
            e.id = str(m, "id", where, kind != KILN_PROFILE_KIND_PLUGIN);
            if (kind != KILN_PROFILE_KIND_PLUGIN && !std::regex_match(e.id, kModId))
                bad(where + "\"id\" '" + e.id + "' is not a mod id");
            e.version = str(m, "version", where, false);
            if (m.contains("version") && (!m["version"].is_string() ||
                (kind != KILN_PROFILE_KIND_PLUGIN && (e.version.size() > 256 || !std::regex_match(e.version, kSemver)))))
                bad(where + "\"version\" must be a semantic version (at most 256 bytes)");
            if (m.contains("hash")) {
                const std::string hash = str(m, "hash", where, true);
                if (hash.size() != (kind == KILN_PROFILE_KIND_PLUGIN ? 64 : 69) ||
                    !std::regex_match(hash, kind == KILN_PROFILE_KIND_PLUGIN ? kSha : kHash))
                    bad(where + "\"hash\" must be " + (kind == KILN_PROFILE_KIND_PLUGIN ? "a lowercase SHA-256" : "kdh1:<lowercase SHA-256>"));
            }
            e.path = game_path(m, "path", where, true);
            if (m.contains("enabled")) {
                if (!m["enabled"].is_boolean()) bad(where + "\"enabled\" must be true or false");
                e.enabled = m["enabled"].get<bool>();
            }
            mods[kind].push_back(std::move(e));
        }
    }
    // Valid: commit, resolving paths.
    g_state.dropins = drop;
    if (!config_dir.empty()) g_state.config_dir = u8(resolve(config_dir));
    if (!data_dir.empty()) {
        g_state.data_dir = u8(resolve(data_dir));
        g_state.data_dir_set = true;
    }
    std::error_code ec;
    for (uint32_t k = 0; k < KILN_PROFILE_KINDS + kEngineKindCount; ++k) {
        for (auto& e : mods[k]) {
            const fs::path p = resolve(e.path);
            e.path = u8(p);
            // a Shroudtopia entry may name the mod DLL itself
            const bool present = k == KILN_PROFILE_KIND_PLUGIN ? fs::is_regular_file(p, ec)
                                 : fs::is_directory(p, ec) || (std::string_view(kind_name(k)) == "shroudtopia" && fs::is_regular_file(p, ec));
            if (!present) {
                KLOG_W("profile: %s mod %s: %s %s is missing; skipped", kind_name(k), e.id.empty() ? "(plugin)" : e.id.c_str(),
                       k == KILN_PROFILE_KIND_PLUGIN ? "DLL" : "folder", e.path.c_str());
                g_state.missing.push_back({k, e.id, e.path});
                continue;
            }
            if (k < KILN_PROFILE_KINDS) g_state.mods[k].push_back(std::move(e));
            else g_state.engine_mods[k - KILN_PROFILE_KINDS].push_back(std::move(e));
        }
    }
}

void read_profile(const fs::path& file, uint32_t mode) {
    g_state = State{};
    g_state.file = u8(file);
    std::string text;
    std::error_code ec;
    if (!fs::exists(file, ec)) {
        g_state.mode = KILN_PROFILE_INVALID;
        g_state.error = "the profile file is missing";
    } else if (fs::file_size(file, ec) > kMaxProfileBytes && !ec) {
        g_state.mode = KILN_PROFILE_INVALID;
        g_state.error = "the profile exceeds 16 MiB";
    } else {
        try {
            if (!read_bounded(file, &text)) bad("the profile file cannot be read");
            g_state.text = text;
            parse(text);
            g_state.mode = mode;
        } catch (const Invalid& e) {
            g_state = State{};
            g_state.file = u8(file);
            g_state.mode = KILN_PROFILE_INVALID;
            g_state.error = e.what;
        } catch (const std::exception& e) {
            // Invalid filesystem encodings and JSON conversions must fail closed too, not unwind through boot.
            g_state = State{};
            g_state.file = u8(file);
            g_state.mode = KILN_PROFILE_INVALID;
            g_state.error = std::string("cannot read the profile: ") + e.what();
        }
    }
    if (g_state.mode == KILN_PROFILE_INVALID) {
        g_state.text = g_state.error;
        KLOG_E("profile: %s is invalid (%s): NO MODS load this start (fix or delete the file; the launcher rewrites it "
               "when it activates a profile)",
               g_state.file.c_str(), g_state.error.c_str());
    }
}

}  // namespace

const State& state() { return g_state; }

const char* mode_name(uint32_t mode) {
    switch (mode) {
        case KILN_PROFILE_ACTIVE: return "active";
        case KILN_PROFILE_ONCE: return "once";
        case KILN_PROFILE_NO_MODS: return "no-mods";
        case KILN_PROFILE_INVALID: return "invalid";
        default: return "legacy";
    }
}

const char* kind_name(uint32_t kind) {
    if (kind < KILN_PROFILE_KINDS) return kKinds[kind];
    if (kind < KILN_PROFILE_KINDS + kEngineKindCount) return kEngineKinds[kind - KILN_PROFILE_KINDS];
    return "?";
}

bool dropins(uint32_t kind) { return kind < KILN_PROFILE_KINDS && (g_state.dropins & (1u << kind)) != 0; }

bool disable_entry(std::string_view engine, std::string_view id, std::string_view reason) {
    if (g_state.mode != KILN_PROFILE_ACTIVE && g_state.mode != KILN_PROFILE_ONCE) return false;
    std::vector<Entry>* list = nullptr;
    for (uint32_t i = 0; i < KILN_PROFILE_KINDS; ++i)
        if (engine == kKinds[i]) list = &g_state.mods[i];
    for (uint32_t i = 0; i < kEngineKindCount; ++i)
        if (engine == kEngineKinds[i]) list = &g_state.engine_mods[i];
    if (!list) return false;
    bool found = false;
    for (Entry& e : *list) {
        if (e.id != id || !e.enabled) continue;
        e.enabled = false;
        e.disabled_by = std::string(reason);
        found = true;
    }
    if (!found) return false;
    // The text other engines' plugins parse (kiln.profile json()): same document, the entry's "enabled" false.
    try {
        json doc = json::parse(g_state.text, nullptr, true, true);
        const std::string key(engine);
        if (doc.contains("mods") && doc["mods"].is_object() && doc["mods"].contains(key) && doc["mods"][key].is_array())
            for (auto& m : doc["mods"][key])
                if (m.is_object() && m.contains("id") && m["id"].is_string() && m["id"].get<std::string>() == id)
                    m["enabled"] = false;
        g_state.text = doc.dump();
    } catch (const std::exception&) {
        // the entries above are off for the core's own readers; the text keeps what it said
    }
    return true;
}

fs::path resolve(std::string_view game_path) {
    std::string p(game_path);
    for (char& c : p)
        if (c == '/') c = '\\';
    fs::path out = is_absolute(p) ? fs::path(to_wide(p)) : paths().kiln / to_wide(p);
    return out.lexically_normal();
}

void load() {
    const KilnBootInfo& bi = boot_info();
    const auto json_file = paths().kiln / L"profile.json";
    std::error_code ec;
    if (bi.play_once == KILN_PLAY_ONCE_NO_MODS) {
        g_state.mode = KILN_PROFILE_NO_MODS;
        KLOG_I("profile: play-once no-mods: no managed, native, plugin or EMF mods this start");
    } else if (bi.play_once == KILN_PLAY_ONCE_PROFILE) {
        const std::string once(sv(bi.play_once_profile));
        read_profile(once.empty() ? paths().kiln / L"profile.once.json" : resolve(once), KILN_PROFILE_ONCE);
    } else if (fs::exists(json_file, ec)) {
        read_profile(json_file, KILN_PROFILE_ACTIVE);
    } else {
        g_state.mode = KILN_PROFILE_LEGACY;
    }
    if (g_state.mode == KILN_PROFILE_LEGACY) g_state.dropins = 0xFu;
    if (g_state.mode == KILN_PROFILE_NO_MODS || g_state.mode == KILN_PROFILE_INVALID) g_state.dropins = 0;
    if (g_state.config_dir.empty()) g_state.config_dir = u8(paths().kiln / L"config");
    if (g_state.data_dir.empty()) g_state.data_dir = u8(paths().data);
    fs::path config = to_wide(g_state.config_dir);
    fs::create_directories(config, ec);
    if (!fs::is_directory(config, ec)) {
        KLOG_E("profile: configDir %s cannot be created (%s); configs go to kiln/config", g_state.config_dir.c_str(),
               ec.message().c_str());
        config = paths().kiln / L"config";
        fs::create_directories(config, ec);
        g_state.config_dir = u8(config);
    }
    set_config_dir(config);
    if (g_state.mode == KILN_PROFILE_ACTIVE || g_state.mode == KILN_PROFILE_ONCE) {
        std::string engines;  // "eml 2, shroudforge 1": the lists each engine's plugin reads
        for (uint32_t i = 0; i < kEngineKindCount; ++i)
            if (!g_state.engine_mods[i].empty())
                engines += (engines.empty() ? "" : ", ") + std::string(kEngineKinds[i]) + " " +
                           std::to_string(g_state.engine_mods[i].size());
        if (!engines.empty()) KLOG_I("profile: other engines' mods (their plugins load them): %s", engines.c_str());
        KLOG_I("profile: %s '%s'%s%s%s generation %llu from %s: %zu kiln, %zu native, %zu plugin, %zu emf mod(s), "
               "%zu missing, drop-ins %s",
               mode_name(g_state.mode), g_state.id.c_str(), g_state.name.empty() ? "" : " (", g_state.name.c_str(),
               g_state.name.empty() ? "" : ")", static_cast<unsigned long long>(g_state.generation), g_state.file.c_str(),
               g_state.mods[0].size(), g_state.mods[1].size(), g_state.mods[2].size(), g_state.mods[3].size(),
               g_state.missing.size(),
               g_state.dropins == 0 ? "off" : g_state.dropins == 0xF ? "all" : "some");
    } else if (g_state.mode == KILN_PROFILE_LEGACY) {
        KLOG_I("profile: none (no kiln/profile.json): the drop-in folders (kiln/mods, kiln/native, kiln/plugins)");
    }
    KLOG_I("profile: configs in %s, mod data in %s", g_state.config_dir.c_str(), g_state.data_dir.c_str());
}

}  // namespace kiln::profile
