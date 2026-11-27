// SPDX-License-Identifier: MIT
// kiln.content registry stage: effect replay of archive-writer packages (owner decision D6, 2026-11-20: "opt-in per package";
// registry BRIEF sections 8 and 12).
//
// A ShroudForge or EML package edits the game's archive by its own Lua, which names game types, member paths and GUIDs. After
// a game update that Lua can fail; the package then changes nothing and its effect is gone until its author updates it. When
// the player opts a package in (<data>/replay/opt-in.json, below), Kiln keeps the package's last effect in registry terms and,
// on a later game build where its Lua was not applied, applies that effect through the registry instead:
//  - Record: in a start where the package ran, the change journal names it on the registry fields it changed (format 2's
//    member paths, archive_journal.h); Kiln reads their values from the base it decoded (what the package's write left in the
//    archive) and keeps them with the package's identity (id, version, content digest), the game build and the side:
//    <data>/replay/<replay mod id>.json. What of its record names no Kiln field (a member no regmap row maps, a resource that
//    is no registry entry, a creation, a content, an entry named whole, a field another package changed too, a journal cut
//    short) is the description's residue.
//  - Replay: in a start whose journal lists the package as not applied (notApplied: its Lua failed, or it needs one that
//    did), on another game build than the description's, with the same identity, Kiln adds the description's values as
//    absolute registry edits of a stand-in writer, the first of every mod (registry mods' edits win over them and relative
//    ones compose on them, as on the package's own archive values, D1; they never make another edit's exclusive or yield
//    apply, Edit::stand_in). It says so in kiln.log and registry.json "replay".
//  - Refused per package, never partly: a description with residue, a value or entry this build no longer takes, a field the
//    build's mapping has not proven (regmap.h), or any edit of it the stage refuses: nothing of the package is replayed, and
//    the reason is given.
// Mechanism, not policy: nothing is replayed unless the player opts the package in; the description is kept only for
// opted-in packages (recording costs a whole base read in each start where one ran).
// Host-tested through the stage (tests/registry_stage_test.cpp effect_replay) and the reader (registry_writers_test.cpp).
#pragma once
#include <map>
#include <string>
#include <vector>

#include "archive_journal.h"
#include "compose.h"

namespace kc::registry::replay {

// <data>/replay/opt-in.json: {"format": 1, "packages": {"shroudforge:<id>": true, "eml:<id>": false}}.
constexpr const char* kOptInFile = "replay/opt-in.json";
constexpr int kFormatOptIn = 1;
constexpr int kFormat = 1;  // a description's format
struct OptIn {
    std::map<std::string, bool> packages;  // who ("shroudforge:<id>", "eml:<id>") -> opted in
    std::string error;                     // the file exists but cannot be read: nothing is opted in (said once)
    bool on(const std::string& who) const {
        const auto it = packages.find(who);
        return it != packages.end() && it->second;
    }
    bool any() const {
        for (const auto& [who, on] : packages)
            if (on) return true;
        return false;
    }
};
OptIn parse_opt_in(const std::string& text);

// A package's recorded effect.
struct Description {
    std::string who, id, kind, version, content;  // content: its content digest (empty: none was frozen)
    std::string side, exe, kiln, recorded, writer;  // the build it was recorded on; whose journal ("kiln-eml" | "shroudforge")
    struct Field {
        std::string registry, id, field;
        json value;
    };
    std::vector<Field> fields;
    std::vector<std::string> residue;  // one line each: what of its effect no proven Kiln field holds
};

// The Kiln mod id a replayed package's edits carry: "replay.sf.<id>" / "replay.eml.<id>" (lower case; a long one cut and
// told apart by 8 hex digits of its SHA-256), and its description's file: "replay/<that id>.json". Its writer rows in
// registry.json say "standIn": true; a start with a Kiln mod of that id refuses the replay (the id is the mod's).
std::string mod_id_of(const std::string& who);
std::string file_of(const std::string& who);

json description_json(const Description& d);
bool parse_description(const std::string& text, Description* out, std::string* why);

// The description of package `p` (who) from this start's base, which name_archive_writers named, and the journal's lines of
// what of its record named no Kiln field (`unmatched`); `truncated`: the journal left some of its record out.
Description describe(const ArchiveJournal::Package& p, const std::string& who, const BaseModel& base, const std::vector<std::string>& unmatched,
                     bool truncated);

// One absolute edit per field of `d`, in a batch of stage alter whose edits stand in for the package (Edit::stand_in).
// false + *why: an edit the IR refuses (a value that no longer fits its field, a field this schema lacks).
bool replay_batch(const Description& d, const Schema& schema, Batch* out, std::string* why);

// What became of one opted-in package in this start (registry.json "replay").
struct Outcome {
    std::string who;
    std::string state;  // "recorded" | "replayed" | "refused" | "none"
    std::string text;   // one sentence
    size_t fields = 0;
    std::string recorded_on;  // the description's game build (exe SHA-256), when there is one
};
json outcomes_json(const std::vector<Outcome>& rows);

}  // namespace kc::registry::replay
