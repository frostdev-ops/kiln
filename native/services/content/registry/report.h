// SPDX-License-Identifier: MIT
// kiln.content registry stage: registry.json (registry P1 slice S7; P1-PLAN.md section 8).
//
// <game>/kiln/state/registry.json is the one report of a start's registry stage: for every field an edit touched, the base
// value and where it came from, every writer in the order they apply (Kiln's older paths and archive-writer packages below,
// then the registry's edits), the result and who decided it; every conflict line (overridden, composed, composedOn,
// exclusive, dropped, yielded) with its certainty; every refused edit with its reason. Format: schema/state-registry.schema.json
// (example: schema/examples/state-registry.json); contract row: docs/kiln/launcher/contracts.md.
//
// report_json() builds the document from compose()'s Result; the caller (the stage runner, S11) writes it atomically at
// phase Start and again at WorldLoad (the generation grows) and logs report_log()'s lines to kiln.log. Caps keep a large mod
// set's file bounded: rows past a cap are left out and counted under "truncated" ("counts" always has the totals).
// Deterministic: rows in the Result's order (fields by registry, id, field; lines as they happened). Plain data: no file,
// no clock, no lock.
#pragma once
#include <functional>

#include "compose.h"

namespace kc::registry {

struct ReportMeta {
    uint32_t pid = 0;
    std::string written;      // RFC 3339 UTC, "2026-11-20T16:00:00Z"
    std::string kiln;         // Kiln's version
    uint64_t generation = 0;  // kiln.registry's generation (KilnRegistryStatus.generation)
    bool world_load = false;  // the phase: false "start" (restart fields final), true "worldLoad" (every field applied)
    struct Regmap {
        std::string side;        // "server" | "client"
        std::string exe_sha256;  // lower-case hex
        std::string record;      // "regmap/server/<sha256>.json", empty when this build has none
        bool verified = false;
    } regmap;
    // Where the stage's base came from (S5 / S10 fill it): "live" = kiln.assets' bytes at resources-ready; the archive
    // writer of this start ("kiln-eml", "shroudforge-hosted", "shroudforge-installed", "external", "none"; archive_base.h)
    // and its token.
    struct Base {
        std::string origin = "live", writer = "none", token;
        // The archive writer's change journal (archive_journal.h): read (its packages are named on the entries they wrote),
        // none, stale or unreadable, with a sentence for players; how many packages it lists and entries it named.
        struct Journal {
            std::string state = "none", text;
            size_t packages = 0, entries = 0;
            bool truncated = false;
        } journal;
        // registry.json base.archive (archive_base.h): what Kiln knows the archive writer did and what it rests on; null: none.
        json archive;
    } base;
    struct Order {
        std::string mod, version, edits_hash;
    };
    std::vector<Order> order;                // the mods, in the order the stage ran them
    json boot_sync = nullptr;  // immutable start snapshot: resolved client-needed boot values + participating registry order
    std::string order_source = "kiln.host";  // "kiln.host" (Kiln.Host's runtime load order) | "folders" (no .NET: kiln.content's mod folders)
    struct Plan {
        std::string hash, file, previous;  // the plan this start runs on; previous: the last good plan before it (a rollback's target)
    } plan;
    // registry.json "rollback" (stage.cpp fills it when kiln/data/content/registry/rollback.json asks for one): {plan, from,
    // requested, state applied | refused, reason, changedBack [{field, from, to}], changedBackTotal, leftOut}; null: none asked.
    json rollback;
    // registry.json "saves": edits that change what existing saves hold (saves.h), one row each; null: none.
    json saves;
    // registry.json "migrated": what the schemas' id migrations renamed in this start's edits ({mod, source, stage, note}).
    json migrated;
    // Conflict lines with another mod's or plugin's own hook of the game function a hooks registry's value answers through
    // (limits.h hook_lines; dev.32): kept apart from the stage's Result, which a live application rebuilds, and written after
    // its lines in every report (counted with them).
    std::vector<Line> hook_lines;
    // registry.json "frontEnds": this start's front-end documents (stage.h FrontEnd: {mod, key, state, fields, reason}); null: none.
    json front_ends;
    // A game entry's base origin per field (S5 / S10); unset: "archive" (the game's data as the archive writer published it).
    // An entry a mod added always reports "added".
    std::function<std::string(const FieldRef&)> origin;
};

struct ReportCaps {
    size_t fields = 20000, conflicts = 2000, refused = 2000, entries = 20000;  // rows in registry.json
    size_t writers = 64;  // writers kept per field row: the last ones (the result's); the earlier ones are counted ("writersLeftOut")
    size_t log = 50;                                                          // kiln.log lines per start, plus one total
    size_t list_entries = 64;                                                 // first entries kept per list value or operand
};

// A refused row that waits for a restart starts with this stable token: a reload or a runtime pass changed
// an edit the start fixed (a restart field, a field the start never decoded, a section or an entry operation added after
// it), or a live edit named such a field. registry.json counts them (counts.restartRequired); the kiln/control reload reply,
// kilnctl status and the launcher's Conflicts view list them.
inline constexpr const char* RESTART_REQUIRED_TOKEN = "RestartRequired: ";
inline bool restart_required(const Refusal& r) { return r.reason.rfind(RESTART_REQUIRED_TOKEN, 0) == 0; }

// The registry.json document (format 1).
json report_json(const Schema& schema, const Result& r, const ReportMeta& meta, const ReportCaps& caps = {});

// kiln.log's lines for the stage: one per refused edit, then one per conflict line, at most caps.log in all, then one total
// line ("registry: 3 conflicts, 1 refused edit; state/registry.json has them all"). Empty when there is nothing to say.
std::vector<std::string> report_log(const Result& r, const ReportCaps& caps = {});

}  // namespace kc::registry
