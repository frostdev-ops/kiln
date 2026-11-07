// SPDX-License-Identifier: MIT
// kiln-shroudtopia: shared between the plugin (plugin.cpp: Kiln, discovery, the update loop) and the ModContext
// functions (context.cpp: config over shroudtopia.json, memory helpers on Kiln's services, logging).
#pragma once

#include <windows.h>

#include <vector>

#include <shroudtopia/shroudtopia.h>

#include "../../compat/compat.h"
#include "services/kiln_hooks.h"

namespace kshroud {

constexpr const char* kPluginId = "kiln-shroudtopia";
constexpr const char* kPluginVersion = "1.0.0";
// The Shroudtopia version mods see in ModContext::shroudtopia.version (the version every known mod targets).
constexpr const char* kShroudtopiaVersion = "0.1.1";

extern kcompat::Host H;
extern const KilnHooksApi* g_hooks;  // may be null: then the memory helpers work on their own

// The mod whose code runs now on this thread (Log attributes lines to it), else the last one the loop drove, as
// Shroudtopia's own loader does.
void set_current_mod(const std::string& name);
std::string current_mod();

// shroudtopia.json, as Shroudtopia reads it: { active, bootDelay, updateDelay, enableLogging, logLevel, mods: {
// "<mod name>": { active, ... } } }. Kiln keeps it in <configDir>/shroudtopia/shroudtopia.json (the first start
// copies <game>/shroudtopia.json when it exists). Thread-safe.
namespace config {
void init(const std::filesystem::path& kiln_file, const std::filesystem::path& game_file);
bool reload_if_changed();
nlohmann::json get(const char* key);                       // top-level value, null when absent
nlohmann::json mod_get(const char* mod, const char* key);  // mods.<mod>.<key>, null when absent
// The "shroudtopia": {"default": {...}} block of a mod.json: written as mods.<id> the first time the id is seen.
void apply_manifest_defaults(const std::string& id, const nlohmann::json& manifest);
std::filesystem::path file();
}  // namespace config

// Builds the ModContext every mod receives (one object, as in Shroudtopia).
ModContext* make_context(bool is_server, const std::string& game_version, const std::filesystem::path& config_file,
                         const std::filesystem::path& log_file, const std::filesystem::path& mod_folder);
void log_line(const std::string& mod, const std::string& text);  // [shroudtopia:<mod>] + logs/shroudtopia.log
void loader_log(int32_t level, const std::string& text);         // [shroudtopia] + logs/shroudtopia.log

}  // namespace kshroud
