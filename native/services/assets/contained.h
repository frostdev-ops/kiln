// SPDX-License-Identifier: MIT
// kiln.assets: a file a mod's manifest names is read from that mod's own assets folder and nowhere else (contained.cpp).
#pragma once
#include <cstdint>
#include <filesystem>
#include <string>
#include <vector>

#include <set>
#include <tuple>

namespace ka {

// A file's identity (volume serial number, file index): the same file under any name, a link's target included.
using FileIds = std::set<std::tuple<uint32_t, uint32_t, uint32_t>>;

// Adds the identity of every file below `mod_dir`/assets/cooked (links followed) to `ids`.
void add_cooked_ids(const std::filesystem::path& mod_dir, FileIds* ids);

// Reads `file` (base / a manifest's path, already free of "..") when it physically is a file of `base` (the mod's
// assets folder): no link or junction from `base` down, by name (Wine reports its Unix symlinks as reparse points
// there), and the opened file's final path inside `base`'s (Windows resolves links there). The bytes come from the
// handle that was checked. false with `why` otherwise: a link to another mod's assets/cooked never lends its files.
// `cooked_ids` (may be null): the identities of the cooked files of every mod being loaded. A file that is one of them and
// is not named below `base`/cooked is one mod's cooked file reached by another name (a link Wine shows as a plain file:
// a Unix symlink to a file is a regular file to every Windows query there), and is refused too.
bool read_contained(const std::filesystem::path& base, const std::filesystem::path& file, std::vector<uint8_t>* out, std::string* why,
                    const FileIds* cooked_ids = nullptr);

}  // namespace ka
