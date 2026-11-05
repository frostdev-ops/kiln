// SPDX-License-Identifier: MIT
// kiln.spawn template catalog: the shipped list (catalog_data.inc) merged with the template infos the engine loaded.
// Thread-safe.
#pragma once
#include <string>
#include <string_view>
#include <vector>

#include "game.h"

namespace kspawn::catalog {

struct Entry {
    Guid guid;
    std::string name;     // template name ("Enemy_Vukah_Melee")
    std::string display;  // in-game name if known ("Vukah")
    uint32_t kind = 0;    // KILN_TEMPLATE_*
    uint32_t source = 0;  // 1 shipped, 2 engine, 3 both
    uint32_t flags = 0;   // KILN_TEMPLATE_FLAG_*
};

void init();                                   // loads the shipped list
size_t merge_engine(const std::vector<TemplateRow>& rows);  // returns engine templates now known
bool engine_loaded();                          // the engine's template map was read at least once
bool engine_has(const Guid& g);                // the engine knows this template (valid only if engine_loaded)

bool find(std::string_view name, Entry* out);  // exact, case-insensitive, display name
bool get(const Guid& g, Entry* out);
size_t count();
bool at(size_t index, Entry* out);             // sorted by name
uint32_t kind_of(std::string_view template_name);
std::vector<Guid> match_names(std::string_view lower_substring);  // GUIDs whose name contains it (case-insensitive)

}  // namespace kspawn::catalog
