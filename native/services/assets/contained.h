// SPDX-License-Identifier: MIT
// kiln.assets: a file a mod's manifest names is read from that mod's own assets folder and nowhere else (contained.cpp;
// the portable half, with its host test, is scan.cpp).
#pragma once
#include <cstdint>
#include <filesystem>
#include <string>
#include <vector>

#include "scan.h"

namespace ka {

// The identities of the cooked files of every mod of the load order (scan_load_order with Windows queries: every enabled
// mod, with or without an assets/assets.json).
CookedIds cooked_ids_of(const std::vector<std::filesystem::path>& mod_dirs);

// Reads `file` (base / a manifest's path, already free of "..") when it physically is a file of `base` (the mod's
// assets folder): `base` itself and every folder and the file below it are no link or junction, by name (Wine reports
// its Unix links to folders as reparse points there); on Windows the assets folder's final path lies in the mod
// folder's, and the opened file's final path in the assets folder's (Windows resolves links there). The bytes come
// from the handle that was checked. With `cooked_ids`: a file that is a cooked file of a mod of the load order under a
// name outside `base`/cooked is refused (a Unix link to a file is a plain file to every Windows query under Wine), and
// so is any file outside `base`/cooked when the scan was cut and `policy` is "local"; under "warn" that file is read
// and `*unproven` set. false with `why` otherwise.
bool read_contained(const std::filesystem::path& base, const std::filesystem::path& file, std::vector<uint8_t>* out, std::string* why,
                    const CookedIds* cooked_ids = nullptr, CookedPolicy policy = kCookedDefault, bool* unproven = nullptr);

}  // namespace ka
