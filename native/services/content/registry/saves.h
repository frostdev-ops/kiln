// SPDX-License-Identifier: MIT
// kiln.content registry stage: field edits that change what existing saves hold (registry-rollback, package K; audit
// section 4 "No checks of structural or persistent changes against existing saves").
//
// A registry edit changes the game's data, never a save file. Some fields, though, are read against what a save holds:
//   - a lower max stack: the game deletes the part of a saved stack above it at the stack's next move (docs/re/content.md,
//     validateItemStack "Deleted %k items from stack");
//   - a narrower level range: a world load clamps saved copies into it and the save keeps the clamped level
//     (docs/re/items.md section 10, "A save load re-clamps the level");
//   - an item's perk slots: the perks a copy has are read live from its type's slots (docs/re/content.md, perkIds), so a
//     saved copy has the slot's new perk;
//   - the balance table: durability is recomputed for every copy at load keeping its share (docs/re/items.md "Max
//     durability"); level cap, skill points, the experience curve, flame-level limits and build-zone sizes are read against
//     saved characters and bases, and a saved altar's level indexes the build-zone list.
//   - the difficulty settings' bounds: the game clamps a world's own (Custom) settings into them when it reads them at start
//     (docs/re/settings.md section 8).
// judge() says, per field, what an edit from `before` (the value without the registry: the game's, or an older path's) to
// `after` does to existing saves, and what Kiln does:
//   Refuse  the edit is refused for that entry, the value stays `before` (nothing a save holds is lost or left dangling);
//   Guard   applied, and Kiln protects the saves: the stack guard keeps a bigger saved stack whole (refused where the guard is
//           not available);
//   Warn    applied, with a warning: saves keep their data, but what it means changes, or what the game does is not traced;
//   Report  applied, with a line: saves follow the new value and nothing is lost.
// Every other field (recipes, loot, drops; an item's rarity, rolled, socket, power, speed, damage, armour, costs; the balance
// table's gear, unlock, socket chance and fog numbers) is read at use or only for new copies: a save holds nothing it changes.
// Plain data in, plain data out: no game, no file, no lock.
#pragma once
#include <optional>
#include <string>
#include <vector>

#include "compose.h"

namespace kc::registry::saves {

enum class Decision : uint8_t { Refuse, Guard, Warn, Report };
const char* decision_name(Decision d);  // "refuse", "guard", "warn", "report"

struct Verdict {
    std::string cls;   // "stackCut", "levelRange", "perkSlots", "durability", "progressionCap", "experienceCurve", "baseLimits", "listShape",
                       // "settingsRange"
    Decision decision = Decision::Warn;
    std::string text;  // what it does to saves, in Kiln words, for registry.json and kiln.log (no game names)
    std::string unguarded;  // Guard: the refusal when the protection is not available ("...; this edit was not applied")
};

// registry.json "saves": one row per field whose registry edit changes what saves hold (decision guard, warn or report;
// refused edits are in "refused").
struct SaveNote {
    FieldRef field;
    std::string cls, decision, text;
};

// registry and field as the schema names them ("items", "levels.max"; a list's whole value for list fields). nullopt: the edit
// changes nothing a save holds.
std::optional<Verdict> judge(const std::string& registry, const std::string& field, const Value& before, const Value& after);

// The value a field's registry rows start from: after the chain's last row that is not the registry's, else its base.
Value before_registry(const FieldOutcome& fo);

// A value as a plan's "values" (or registry.json) writes it, read back for judge(): numbers, true/false, names, lists and
// elements as they are (no schema check; null is none).
Value of_json(const json& j);

// The notes of a composed (or live) result: every field with registry rows whose judge() verdict is not Refuse (a refused edit
// is no longer in the result's value), in the result's order.
std::vector<SaveNote> notes(const Result& r);
// registry.json "saves": one row per note.
json notes_json(const std::vector<SaveNote>& notes);

}  // namespace kc::registry::saves
