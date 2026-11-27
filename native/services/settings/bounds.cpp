// SPDX-License-Identifier: MIT
// kiln.settings: the difficulty bounds of Kiln's registry (bounds.h).
#include "bounds.h"

#include <cmath>

#include "nlohmann/json.hpp"
#include "services/kiln_settings.h"

namespace kset {

namespace {

std::string lower(std::string s) {
    for (char& c : s)
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    return s;
}

}  // namespace

std::map<std::string, RegistryBound> registry_bounds(const std::string& report) {
    std::map<std::string, RegistryBound> out;
    const nlohmann::json doc = nlohmann::json::parse(report, nullptr, false);
    if (!doc.is_object() || !doc.contains("fields") || !doc["fields"].is_array()) return out;
    for (const nlohmann::json& row : doc["fields"]) {
        if (!row.is_object() || row.value("raw", false)) continue;
        const nlohmann::json* reg = row.contains("registry") ? &row["registry"] : nullptr;
        const nlohmann::json* id = row.contains("id") ? &row["id"] : nullptr;
        const nlohmann::json* field = row.contains("field") ? &row["field"] : nullptr;
        if (!reg || !reg->is_string() || *reg != "difficulty" || !id || !id->is_string() || *id != "game:difficulty" || !field || !field->is_string())
            continue;
        const std::string f = field->get<std::string>();
        const size_t dot = f.rfind('.');
        if (dot == std::string::npos || dot == 0) continue;
        const std::string bound = f.substr(dot + 1), setting = lower(f.substr(0, dot));
        if (bound != "min" && bound != "max") continue;
        if (!row.contains("result") || !row["result"].is_number()) continue;
        const double v = row["result"].get<double>();
        if (!std::isfinite(v)) continue;
        const std::string by = row.contains("winner") && row["winner"].is_string() ? row["winner"].get<std::string>() : std::string();
        RegistryBound& b = out[setting];
        (bound == "min" ? b.min : b.max) = v;
        (bound == "min" ? b.min_by : b.max_by) = by;
    }
    return out;
}

void effective_range(uint32_t kind, double cat_min, double cat_max, const RegistryBound* b, double* lo, double* hi) {
    *lo = cat_min;
    *hi = cat_max;
    if (!b) return;
    const double unit = kind == KILN_SETTING_DURATION ? 1e9 : 1.0;  // the registry's durations are seconds, kiln.settings' ns
    if (b->min) *lo = *b->min * unit;
    if (b->max) *hi = *b->max * unit;
    if (*lo <= *hi) return;
    // crossed: the registry's bound decides (where both are the registry's, the max: the game keeps a value at most there)
    if (b->max) *lo = *hi;
    else *hi = *lo;
}

}  // namespace kset
