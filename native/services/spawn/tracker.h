// SPDX-License-Identifier: MIT
// kiln.spawn tracker: what Kiln spawned, per world, in <kiln>/data/spawn/<world-guid>.json. Spawned entities can be
// written to the save and come back with new ids after a restart; the tracker finds them again by template and an
// exact SpawnTime marker (see docs/kiln/services/spawn.md), so cleanup also works across restarts.
#pragma once
#include <cstdint>
#include <filesystem>
#include <mutex>
#include <string>
#include <vector>

#include "game.h"

namespace kspawn::tracker {

struct Record {
    uint64_t serial = 0;
    uint32_t entity = 0;  // this session's id; 0 = not found (yet)
    Guid tmpl;
    std::string name;
    int64_t spawn_time = 0;  // the SpawnTime value Kiln gave it (0 = none)
    double pos[3]{}, spawn_pos[3]{};
    int64_t spawned_unix = 0;
    uint32_t flags = 0;
    std::string tag, origin;
    uint32_t missed_sessions = 0;  // server sessions in which it was never found (dropped at 3)
    bool live = false;
    bool counted = false;          // this session's miss is counted (not saved)
};

// Sets the sidecar file and world key, then loads it (records start not live: ids are per session).
void open(const std::filesystem::path& file, const std::string& world_key, const std::string& save_file);
bool opened();
// Moves the records to another sidecar (the world got its first save after boot) and deletes the old file.
void rekey(const std::filesystem::path& file, const std::string& world_key, const std::string& save_file);
std::string world_key();
std::filesystem::path file();

uint64_t next_serial();
void add(Record r);                 // and save
bool remove_entity(uint32_t id);    // drops the live record for this id (and saves); false if none
void remove_serial(uint64_t serial);
std::vector<Record> snapshot();     // copy, thread-safe
bool find_live(uint32_t id, Record* out);
size_t count();
bool at(size_t i, Record* out);

// Replaces the records with `list` (after refresh/cleanup on the tick thread) and saves if `dirty`.
void replace(std::vector<Record>&& list, bool dirty);
void save_now();

}  // namespace kspawn::tracker
