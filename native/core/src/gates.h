// SPDX-License-Identifier: MIT
// The client gate: capabilities that ship switched off until their guided client checks have passed in a real game.
// The list is native/core/gates/*.json (README.md there), compiled in. While a name is held:
//   - kiln.caps shows it unavailable, reason policy, text "game test pending: <check ids>" (caps.cpp, the core's own
//     report: unavailable beats whatever a service reports, and a sigdb feature whose symbols resolved);
//   - KilnApi.feature() answers false for it (api.cpp).
// kiln.json's "preview" lifts it (config.cpp): true for every name, or a list of names.
#pragma once
#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

namespace kiln::gates {

enum : uint32_t { kBoth = 0, kServer = 1, kClient = 2 };  // Gate.side (gen.py writes the numbers)

struct Gate {
    const char* name;    // the capability
    const char* checks;  // "03-c11, 03-c12": the guided checks that open it
    uint32_t side;       // kBoth, or the one side it is held on
};

struct Held {
    std::string name;
    std::string text;  // "game test pending: 03-c11, 03-c12"
};

// Once at boot, after kiln.json's side section was applied, before early plugins or service init. `side` is KILN_SIDE_*.
// preview_all / preview: kiln.json's "preview". A name in `preview` that the list does not hold is logged as a warning.
void configure(uint32_t side, bool preview_all, const std::vector<std::string>& preview);

// A snapshot of what is held on this side, sorted by name. Before configure(): everything in the list. Any thread.
std::vector<Held> held();
// `name` is held. Any thread, including before or during boot configuration.
bool is_held(std::string_view name);

// Host tests: another list than the compiled one (nullptr: the compiled one). Call before configure().
void use_list_for_tests(const Gate* list);

}  // namespace kiln::gates
