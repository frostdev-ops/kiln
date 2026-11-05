// SPDX-License-Identifier: MIT
#pragma once
#include <string>
#include <utility>
#include <vector>

#include "nlohmann/json.hpp"

namespace kset {

using Json = nlohmann::ordered_json;

// Sets "gameSettingsPreset": "Custom" and every (name, value) under "gameSettings" in the JSON file at `path`.
bool save_config(const std::wstring& path, const std::vector<std::pair<std::string, Json>>& values,
                 std::string& message);

}  // namespace kset
