// SPDX-License-Identifier: MIT
// Tick-side bounded copies for SET's entity inspector. No game pointers escape into the overlay.
#pragma once
#include <cstdint>
#include <array>
#include <string>
#include <vector>
namespace setinspect {
struct Field {
    std::string component, path, value;
    bool editable = false;
    std::array<uint8_t, 8> original{}; // exact compare-before-write, not formatted-text equality
    uint8_t bytes = 0;
};
struct Pin { uint32_t entity = 0; Field field; bool gone = false, have = false; };
struct Row { uint32_t entity = 0; double distance = -1, position[3]{}; bool havePosition = false; };
struct View {
    uint64_t serial = 0;
    uint32_t selected = 0, matched = 0;
    bool inWorld = false, gone = false, off = false;
    std::string note;
    std::vector<uint32_t> entities;
    std::vector<Row> rows;
    std::vector<Field> fields;
    std::vector<Pin> pins;
};
void request(bool open, std::string component = "CurrentTransform", uint32_t entity = 0, bool reselect = false);
void pin(uint64_t serial, uint32_t entity, const Field& field);
void unpin(uint32_t entity, const Field& field);
void edit(uint64_t serial, const Field& field, std::string value);
View view();
void tick(uintptr_t world); // under rt's Lua lock, tick thread only; faults disable only the inspector
void clear();              // same lock, on world leave/reload/teardown
}
