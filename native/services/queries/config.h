// SPDX-License-Identifier: MIT
// <config>/kiln.queries.json: {"format":1,"maxRaysPerStep":64,"maxRayMicrosecondsPerStep":500}. Read once at start. A file
// that is wrong in any known part gives the whole default pair, never half of it. Pure: the text in, the limits out.
#ifndef KILN_QUERIES_CONFIG_H
#define KILN_QUERIES_CONFIG_H
#include <string>
#include <string_view>

#include "budget.h"
#include "nlohmann/json.hpp"

namespace kq {

// `warnings`: what to log, empty when the file is as it should be.
inline Limits parse_limits(std::string_view text, std::string* warnings) {
    const Limits defaults{};
    auto bad = [&](const std::string& why) {
        *warnings = "kiln.queries.json: " + why + ": the defaults are used (" + std::to_string(defaults.max_rays) + " rays, " +
                    std::to_string(defaults.max_microseconds) + " microseconds per step)";
        return defaults;
    };
    const nlohmann::json doc = nlohmann::json::parse(text.begin(), text.end(), nullptr, false, true);
    if (doc.is_discarded()) return bad("not JSON");
    if (!doc.is_object()) return bad("not an object");
    Limits out = defaults;
    std::string unknown;
    bool format = false;
    for (auto it = doc.begin(); it != doc.end(); ++it) {
        const std::string& key = it.key();
        const auto& v = it.value();
        auto whole = [&](uint32_t lo, uint32_t hi, uint32_t* to) {
            if (!v.is_number_integer() && !v.is_number_unsigned()) return false;
            if (v.is_number_integer() && !v.is_number_unsigned() && v.get<int64_t>() < 0) return false;
            const uint64_t n = v.get<uint64_t>();
            if (n < lo || n > hi) return false;
            *to = static_cast<uint32_t>(n);
            return true;
        };
        if (key == "format") {
            uint32_t f = 0;
            if (!whole(1, 1, &f)) return bad("\"format\" is not 1");
            format = true;
        } else if (key == "maxRaysPerStep") {
            if (!whole(kMinRays, kMaxRays, &out.max_rays)) return bad("\"maxRaysPerStep\" is not a whole number from 1 to 4096");
        } else if (key == "maxRayMicrosecondsPerStep") {
            if (!whole(kMinMicroseconds, kMaxMicroseconds, &out.max_microseconds)) return bad("\"maxRayMicrosecondsPerStep\" is not a whole number from 1 to 10000");
        } else if (key != "comment" && key != "$schema") {
            unknown += (unknown.empty() ? "" : ", ") + key;
        }
    }
    if (!format) return bad("no \"format\": 1");
    if (!unknown.empty()) *warnings = "kiln.queries.json: unknown field(s) ignored: " + unknown;
    return out;
}

}  // namespace kq
#endif
