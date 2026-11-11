// SPDX-License-Identifier: MIT
// The mod profile this process boots with (service kiln.profile, abi/services/kiln_profile.h; format
// schema/profile.schema.json; rules docs/kiln/profiles.md). Read once, right after kiln.json:
//   play-once no-mods            -> NO_MODS: nothing of any kind, configs in kiln/config
//   play-once profile            -> ONCE: the file the proxy passed (kiln/profile.once.json by default)
//   <game>/kiln/profile.json     -> ACTIVE
//   none                         -> LEGACY: the drop-in folders (kiln/mods, kiln/native, kiln/plugins, emf/mods)
// A file that exists but cannot be read or validated -> INVALID: no mods, the reason in kiln.log, boot.json and
// json(). Entries whose folder (or plugin DLL) is missing are left out of the lists and listed in missing.
// Never changes after boot; every string stays valid for the life of the process.
#pragma once
#include <string>
#include <vector>

#include "common.h"
#include "services/kiln_profile.h"

// The profile.json forms this core reads, as one number. tools/package/stage.sh copies it into kiln-runtime.json
// ("profileReader"), and the launcher writes a form only for a runtime whose bundle declares it (kiln-manage
// profile::render). 2: a mods.eml path may be Copy mode's ../mods/<folder>, a mods.greeneye path ../greeneye/<id>
// (every core before calls such a profile invalid). 3: a mods.flame path may be ../mods/<id> (Kiln's copy of a Flame mod
// in the folder it reads its own files from; a core of revision 2 calls such a profile invalid). Raise it with the next
// form an older core would refuse.
#define KILN_PROFILE_READER 3

namespace kiln::profile {

struct Entry {
    uint32_t kind = KILN_PROFILE_KIND_KILN;
    std::string id, version;
    std::string path;  // absolute, Windows form, UTF-8
    bool enabled = true;
    std::string disabled_by;  // why it is off when a compat rule switched it off (compat.h), else empty
};

struct Missing {
    uint32_t kind;
    std::string id, path;
};

// Mod kinds of other engines (profile.json mods.<kind>, schema/profile.schema.json). They are not kiln.profile kinds:
// each engine's plugin reads its own list from json(). The core validates them like the others (a broken entry makes
// the profile INVALID), counts them for kiln.log and boot.json, and reports missing folders. Internal kind numbers
// for Missing: KILN_PROFILE_KINDS + index into kEngineKinds.
inline constexpr uint32_t kEngineKindCount = 9;
inline constexpr const char* kEngineKinds[kEngineKindCount] = {"eml",         "shroudforge", "set",
                                                               "greeneye",    "flame",       "shroudtopia",
                                                               "enshwick",    "shroudlink",  "ember"};

struct State {
    uint32_t mode = KILN_PROFILE_LEGACY;
    std::string id, name;
    uint64_t generation = 0;
    std::string file;   // the profile file read (UTF-8), empty for LEGACY / NO_MODS
    std::string text;   // its text as read; INVALID: the error
    std::string error;  // INVALID: why
    std::string config_dir, data_dir;  // absolute, UTF-8
    bool data_dir_set = false;
    uint32_t dropins = 0;  // bits 1u << KILN_PROFILE_KIND_*
    std::vector<Entry> mods[KILN_PROFILE_KINDS];
    std::vector<Entry> engine_mods[kEngineKindCount];  // kEngineKinds order; present folders only
    std::vector<Missing> missing;
};

void load();  // boot: after load_config(); sets KILN_PATH_CONFIG
const State& state();
const char* mode_name(uint32_t mode);  // legacy | active | once | no-mods | invalid
const char* kind_name(uint32_t kind);  // kiln | native | plugin | emf, then kEngineKinds (KILN_PROFILE_KINDS + i)
bool dropins(uint32_t kind);           // load that kind's drop-in folder too

// A game-side path: absolute (X:\..., \\server\...) as is, anything else relative to <game>\kiln\. '/' = '\'.
std::filesystem::path resolve(std::string_view game_path);

// A compat rule with action "disable" on a hosted mod (compat.h): switches the profile's entry of `engine` (kiln, native,
// plugin, emf or an engine word of kEngineKinds) with this id off, before any loader reads the profile. The entry's
// `enabled` becomes false (kiln.profile get(), native loader, plugins) and the "enabled" key in json() (the text other
// engines' plugins read) is rewritten to false. True when an enabled entry was found. Boot only: strings handed out
// earlier by json() are invalidated.
bool disable_entry(std::string_view engine, std::string_view id, std::string_view reason);

// The kiln.profile v1 table (services/profile.cpp). KilnApi.service answers it in safe idle too.
const KilnProfileApi* table();

}  // namespace kiln::profile
