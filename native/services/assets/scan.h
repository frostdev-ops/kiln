// SPDX-License-Identifier: MIT
// kiln.assets: the portable half of reading a mod's files from its own folder (contained.cpp is the Windows half).
// Pure C++ with the file system behind callbacks, so the host test (tests/test_scan.cpp) runs it on real folders.
#pragma once
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <functional>
#include <optional>
#include <set>
#include <string>
#include <tuple>
#include <vector>

#include "cooktrust.h"

namespace ka {

// A file's identity (volume serial number, file index): the same file under any name, a link's target included.
using FileId = std::tuple<uint32_t, uint32_t, uint32_t>;
using FileIds = std::set<FileId>;

// The identities of the cooked files of the mods of a load order, and whether every cooked folder was scanned whole.
struct CookedIds {
    FileIds ids;
    bool complete = true;
    std::vector<std::string> cut;  // why a scan was cut, one line per mod folder
};

// What a folder holds, for the scan: each entry's name and kind. A link (or junction) is never followed.
struct Entry {
    std::wstring name;
    enum Kind { File, Dir, Link } kind;
};
using ListFn = std::function<std::optional<std::vector<Entry>>(const std::filesystem::path&)>;
using IdFn = std::function<std::optional<FileId>(const std::filesystem::path&)>;

// What a path is, by its name (no link followed): missing, a link or junction, or a plain file or folder.
enum class NameKind { Missing, Link, Plain };
using AttrFn = std::function<NameKind(const std::filesystem::path&)>;

// Most entries one cooked folder's scan looks at; past it the scan is cut (and the set not complete).
constexpr size_t kScanBudget = 200000;

// Adds the identity of every file below `root` (a plain folder that is there: the caller looks first) to `ids`, walking
// with an explicit stack (no depth limit; links are not followed, so there is no cycle). false (the set is not complete)
// when a folder cannot be listed, a file's identity cannot be read, or more than `budget` entries were met; `why` says
// which.
bool scan_tree(const std::filesystem::path& root, const ListFn& list, const IdFn& id, size_t budget, FileIds* ids, std::string* why);

// The identities of the cooked files of every mod of a load order: `mod_dirs` is every enabled mod, with or without an
// assets/assets.json (a data-only mod's cooked folder is reachable from another mod's manifest all the same). A mod
// with no plain assets/cooked (none, or a link there: nothing of it is the mod's) adds nothing; a scan that is cut makes
// the set not complete and says why in `cut`.
CookedIds scan_load_order(const std::vector<std::filesystem::path>& mod_dirs, const ListFn& list, const IdFn& id, const AttrFn& attr,
                          size_t budget = kScanBudget);

// What may become of a file a manifest names, by what is known of its identity: Ok, Alias (a cooked file of a mod of the
// load order under a name outside the reader's own assets/cooked: refused under every policy), Unproven (the cooked
// folders were not all scanned, so it cannot be shown to be no such alias: refused under "local"), UnprovenWarn (the
// same under "warn": loaded and said). A file named below its own assets/cooked is Ok here (its receipt decides).
enum class AliasVerdict { Ok, Alias, Unproven, UnprovenWarn };
AliasVerdict alias_verdict(bool named_cooked, bool in_cooked_ids, bool ids_complete, CookedPolicy policy);


// The by-name check of a file a manifest names: `base` (the mod's assets folder) itself and every component of `rel`
// below it are plain (not a link or junction, by name), and are there. false with `why` otherwise.
bool names_plain(const std::filesystem::path& base, const std::filesystem::path& rel, const AttrFn& attr, std::string* why);

// Whether a manifest path below the assets folder is in the mod's own assets/cooked ("cooked/..." in any case).
bool named_in_cooked(const std::filesystem::path& rel);

}  // namespace ka
