// SPDX-License-Identifier: MIT
// kiln.settings: what Kiln knows about each game setting beyond the game's reflection: group, the documented range and
// default (enshrouded_server_readme.txt of the server, version 1.0.0.0), whether players' games read it (EMF 0.3.3
// world_settings.md), and when a change takes effect (docs/re/settings.md).
#pragma once
#include <cstdint>

namespace kset {

struct Known {
    const char* name;     // enshrouded_server.json key = reflected field name
    const char* group;
    uint32_t kind;        // KILN_SETTING_* expected from reflection
    double min, max;      // FLOAT/INT: value; DURATION: nanoseconds; BOOL/ENUM: unused
    double def;           // FLOAT/INT/BOOL/DURATION default
    const char* def_option;  // ENUM default option name
    uint32_t flags;       // KILN_SETTING_F_* (LIVE, CLIENT, NEXT, VERIFIED); RANGE is added for FLOAT/INT/DURATION
    const char* effect;
};

const Known* known(const char* name);
const Known* known_at(uint32_t i);
uint32_t known_count();

}  // namespace kset
