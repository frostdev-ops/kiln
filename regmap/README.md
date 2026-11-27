# regmap: Kiln's registries mapped onto the game

Mods name Kiln's registry fields and ids (`items.maxStack`, `game:material_t1_wood_logs`; schema/registry/,
docs/kiln/modding/registries/). This folder is how Kiln translates them for each game build. It is Kiln's part: no mod
reads it, and a game update that moves a member changes a row here, not a mod.

| File | What |
|---|---|
| `common/<registry>.json` | where the registry's entries come from and one row per Kiln field |
| `server/<exe sha256>.json`, `client/<exe sha256>.json` | the record of one game build: verified or not, each row's state, the root types' layout hashes, the alias deltas |

Read by kiln.content (`native/services/content/registry/regmap.{h,cpp}`); checked offline by
`tools/registrygen/regmap_check.py` (against the committed type dumps under `sigdb/types/`, or against a game
executable's own reflection with `--exe`, the file read as data and never run).

Adding a registry (schema, mapping, back-ends, proof, tests, and the pitfalls the first registries hit):
[docs/kiln/adding-a-registry.md](../docs/kiln/adding-a-registry.md).

## common/<registry>.json

```jsonc
{ "regmap": 1, "registry": "items",
  "entries": { "kind": "resource",            // resource: one resource per entry; array: the elements of one resource's
                                              // array member ("array"); single: one resource ("resourceGuid")
               "resourceType": "keen::ItemInfo", "name": "debugName", "guid": "objectId", "id": "itemId",
               "unnamed": "item_%08x",        // the stable name of an entry without a debug name (printf over its id;
                                              // "%.8s" takes the GUID text instead)
               "nameFrom": "",                // drop tables: "<component>.<member>": the templates that use the entry name it
               "key": "",                     // loot: "items": the entry has the id of the item with its 32-bit id;
                                              // creatures: "templates": the id of the template with its GUID
               "keyMember": "",               // stations: with "key": "items", the member whose 32-bit item id names the
                                              // entry (the item that builds it); none, or an item no items entry has: the
                                              // name rules ("unnamed")
               "withComponent": [] },         // creatures: only the templates holding one of these components (by type name)
  "rows": {
    "maxStack": { "paths": ["maxStackSize"], "conv": "int", "read": "worldLoad", "backend": "live", "clientNeeds": true,
                  "evidence": "docs/re/content.md (StackClamp)" },
    "fuel.burnTime": { "paths": ["fuelSetup.burnTime"], "conv": "time", "read": "unknown", "backend": "live",
                  "state": "unproven", "reason": "no game code reads an item's burn time on this build, ..." } } }
```

A row: `paths` (the game's member paths from the entry's root; for a field of a list's elements, from the element; from a
template root the first part names a component by its type, `Level.dataStorage[0]`, and `name[i]` is element i of an array),
`conv` (below), `read` (when the game reads the member: now, worldLoad, restart, unknown), `backend` (live: kiln.content's
store at world load; boot: the boot resource patch before the game reads it; none: read-only), `clientNeeds` (a client
shows or predicts from its own copy), `state` (mapped, unproven, unmapped; the last two with a `reason`), `evidence`.

| conv | paths | Kiln value |
|---|---|---|
| int, float, bool | the member | the number, as the schema's range allows |
| time | a `keen::Time` | seconds (the game keeps nanoseconds) |
| enum | an enum member; `values` maps Kiln names to the game's | one Kiln name |
| flags | a bitmask member; `values` as enum | a set of Kiln names |
| optionalEnum | [the switch (bool), the enum]; `values` with `none` for the switch off | one Kiln name |
| ref | [the 32-bit id, the reference (GUID)], kept in step; or [the reference] alone | an entry's Kiln id |
| refList | [the array of references, the array of ids], kept in step | a list of Kiln ids |
| formula | the members the formula reads and writes; `formula` names it (formulas.h) | the Kiln number (tooltip power, armour) |
| impactConfig | the impact configurations; `config` names the configuration, or `shown`: the entry's one whole-number or fractional configuration (its tooltip's value; the id differs per entry: perks.value) | the number |
| list | the array (blob or static) | a list; its elements' fields have rows of their own (`inputs[].count`) |
| stackCount | the count members of the element's alternatives (item stack, input category) | the count |
| inventoryWalk | the root group: stacks first, then each child group, depth first | the table's entries in walk order |
| unlock | the knowledge requirement | game or always |
| component | template components by type name; `values` maps one Kiln name to each, read-only | the Kiln name of the first one the entry holds (creatures.kind) |

**The routing rule.** The schema's `applies` is the contract. A row's `read` may not be later than its field's
`applies` (now < worldLoad < restart); a `boot` back-end serves only restart fields and `live` only now and worldLoad
ones; a read-only field has back-end `none`; a read time that is not traced makes the row unproven, and its field ships the
cautious `applies` (worldLoad or restart, never now). regmap_check.py fails on a row that breaks it; the native reader
(`load_dir`) loads such a row as unproven (refused per entry, its reason the rule it breaks) and names it in its errors.

**Reasons are shown to players and mod authors.** A row's `reason` is in Kiln words (the field, what is not known); the
game's member and type names go in `evidence` only. regmap_check.py refuses a reason that names one.

## <side>/<exe sha256>.json: one game build

```jsonc
{ "regmap": 1, "side": "server", "exeSha256": "f0d8...", "gameBuild": "1287369", "steamBuild": "26430253",
  "verified": true, "proof": "regmap_check.py --exe: every row resolved and type-checked against the exe's reflection",
  "layouts": { "keen::ItemInfo": "0x07009d37" },          // the reflection's layout hash of each root type
  "rows": { "items.maxStack": "ok" },                      // ok | changed | unproven | unmapped
  "reasons": { "recipes.station": "a recipe's station is a stations id, ..." },   // unproven rows only, in Kiln words
  "kinds": { "items.manaCost": { "sint32": "proven (138 entries on this build)", "float": "none on this build",
                                 "notInWorld": "3 entries not in the lab's world (no edit reaches them on this build)" } },
  "regmapDigest": "<sha256>",                              // the regmap/common it was proven on (rows enabled only on it)
  "provenBy": "lab",                                       // lab (kilnctl regproof on lab evidence) | mirror (client, --mirror)
  "aliases": { "renames": [], "removals": [], "suffixes": [] } }
```

Row states: `ok` (proven on this build), `changed` (the build changed what the row maps, or a lab step disproved it: a
Kiln update is needed), `unproven` (nothing is wrong that kilnctl can see, but the lab gave no evidence for it yet, for
example no round-trip sample in its generation; the reason says what is missing), `unmapped` (no mapping on this build).
Only `ok` is applied. An edit of an `unproven` row is refused "Unproven: <field> is not proven for game build <b>
(<reason>); this edit was not applied" (the reason from `reasons`, else "no lab evidence on this build"); a `changed` or
`unmapped` one "Unmapped: ... (a Kiln update is needed)". kiln.content reads `unproven` and `reasons` since dev.31; an
older reader refuses a whole record with any other state than ok, changed or unmapped (every field of that build stays off).

A field is applied only on a build whose record is `verified` and lists its row `ok`; everything else is refused per
entry with the reason, which names the field and the game build and ends "; this edit was not applied" (BRIEF 6.2:
"Unmapped: items.power is not mapped for game build 1287369 yet (a Kiln update is needed); this edit was not applied").
**No record for the running exe** (a game update Kiln has not mapped yet): every field is Unproven ("... is not proven
for this game build (server exe f0d824be) (no verified regmap record for it yet; a Kiln update is needed)"), so Kiln
never writes a field it has not proven on that exact build; reads of the game's own values are not affected.

A record is written by `kilnctl regproof` (and by `kilnctl update` / `kilnctl port` after the lab gate; docs/kiln/sigport.md
"The registry proof"), never by hand. kilnctl runs `regmap_check.py --exe <the exe, read as data> --record --side <side>
--game-build <b>` in a scratch copy of `regmap/` and `schema/` for the candidate, then the proof below, and marks the
record `verified` only when every step of it ran green. With `--entries` (this build's entries, made by
`regmap_entries.py --exe <copy> --out <file>`), `--ids-in` (the previous build's id table) and `--ids-out` it also computes
the alias deltas below; the entries file and the id tables hold the game's names and stay outside the repository (a
build folder; at run time kiln.content keeps the id table of the running build in its own data folder). Without
`--entries` the previous record's aliases are carried as they are.

**Client records mirror the server proof** (owner ruling, 2026-11-23): no game client is ever run for a proof. `kilnctl
regproof --side client --tree <tree> --new-exe <client exe> --mirror regmap/server/<server sha>.json --server-exe <server
exe> --game-build <b> --out <dir>` (both exes read as data) writes the client record into `<dir>`: a client row is `ok` only
when the same row is `ok` in the verified server record AND the client exe's reflection gives the row the same layout as the
server exe's (the members its paths walk, their types and offsets, the enum values it maps, and the registry's entry
members). Every other mapped row is `unproven`: with the server's reason where the server proof left it unproven, else
"layout differs from the proven server build". The mirror is `verified` only when the server record is verified, lab-proven
(`"provenBy": "lab"`, which kilnctl regproof writes only when it judged fresh lab evidence: the gate copy's reflection-only
candidate never qualifies; review3 S5) and of the same game build and both reflections are of the two exes; its layouts and aliases are the client exe's own (regmap_check.py,
the aliases carried from the tree's record of that exe). Commit `regmap-client-<sha>.json` from `<dir>` as
`regmap/client/<sha>.json`, and mirror again whenever the server record changes.

**Id tables kept per build.** kilnctl keeps one id table per game build in a maintainer folder, `$KILN_REGMAP_IDS`
(default `~/.local/state/kiln/regmap-ids/<side>/ids-<side>-<exe sha256>.json`, beside each proven build's values export
`values-<side>-<exe sha256>.json`), harvests the tables kiln.content wrote into a lab's or instance's data folder
(`<game>/kiln/data/content/registry/ids-<side>-<exe sha256>.json`; kiln.content writes it once per build when it is missing,
also on a start without registry content, with every schema registry and the reference tables templates, perks and input
categories, entries only) and deploys the new build's table there. The kept table
is regmap_check.py's (its pins applied) plus kiln.content's entries regmap_check.py does not cover (loot entries, keyed
`#<id>`; input categories). Only a verified proof keeps its tables and export, and a kept one is never overwritten;
tables harvested from data folders are kept apart, under `<side>/harvested/`. kilnctl refuses a maintainer or output
folder inside a Kiln tree: tables, entries and values hold the game's names and never go into the repository.

## The proof (`kilnctl regproof`)

1. **Reflection diff**: the old and new exe's reflection (`tools/kfc/kfc.py --exe <the exe, read as data> types --closure <root types>`),
   per row the members its paths walk, the leaf types and the enum values it maps. Same, carried (offsets moved, the enum
   gained values) or new (resolves only on the new build) rows go on; a changed row is reviewed by a human, with a rename
   or move proposal where one fits (`--accept <registry.field>` after the review).
2. **Formulas**: kilnctl computes gear.power and gear.armor itself (the same numbers as gear.cpp and GearMath) over the
   lab's raw member values, with the build's gear constants.
3. **Round trip** in the lab, live fields in a live generation and boot fields in a boot generation (file below).
4. **Values diff** of the old and new build's exports, with each base's origin (file below); never assumed vanilla.
5. **Aliases**: ids never move, removals are recorded and never reused, pins hold, kiln.content's table agrees.

A row is `ok` when every step proved it, `unproven` when only lab evidence is missing, `changed` otherwise. The lab writes
its files into `<game>/kiln/data/content/registry/proof/`; kilnctl finds them (and kiln.content's id tables) by content.
Both formats are format 1; kilnctl refuses another kind or format.

**The producer** (lab only). The lab gate (`kilnctl gate`, whose default tests include `registry-proof-test`) writes
`content/registry/proof/request.json` into its copy. With that file, and only then: kiln.content
(`native/services/content/registry/proof.cpp`) writes `roundtrip-<side>-boot.json` and `values-<side>.json` at
resources-ready, before any boot patch (boot fields round-tripped in memory on a copy of their resource by the boot
back-end itself; the read-only fields of the other registries read from the base, the first entry that reads a value;
every entry exported through the schema from the base); `mods/tests/registry-proof-test` writes
`roundtrip-<side>-live.json` at the world's first content ready (each mapped live field of one entry that reads:
kiln.registry.live begin/submit/commit/read/revert, raw reads by the game's member paths, the resource bytes hashed before
the write and after the revert; a structured value's raw view is its resource's digest). A field it finds no probe for
gets no sample and stays unproven (kiln.log says why: "registry-proof-test: no sample of <field>"); where the producer
knows why in Kiln words it says so in the suite's `"unsampled": {"<field>": "<why>"}`, and kilnctl takes that as the row's
reason. Drop tables, which the game loads only when something needs them, are loaded by the producer's second phase once the
world runs: it spawns a few of the game's loot containers next to a container of the lab's world, or at the lab world's spot
when it has none (their default_inventory
program acquires their tables), then samples the drop rows over them; only when that fails does `unsampled` say why. The
same phase drives real loot rolls (the loot entries edited through kiln.registry.live, the game's random pickup spawners
spawned with a creature's loot labels, each roll read from the spawner's record) and writes what came out into the suite's
`"effects"` (field, setting, observed, result followed / not followed / inconclusive) and kiln.log: evidence for
regmap/common's read times, which kilnctl does not judge. A write refusal that shows the mapping disagrees with the build (the schema or regmap has no such field, a game
member or enum value is missing, a member is not a writable scalar) is a sample with an `error` (the row is `changed`);
guard, range and entry-specific refusals give no sample. A boot sample's "revert" composes the base again without the
probe, in memory: it shows that composing no rows keeps the base's bytes; the proof of a boot field is its write and read
back on the copy. Without the request file neither does anything (kiln.content checks once per start that the file is
there, without reading it). The test mod is never shipped (`tools/package/stage.sh` builds no `mods/tests`).

Registry evidence is diagnostic, not a sigdb self-test: no samples, missing state/ids, a producer failure or a producer
that does not build never blocks the gate or the sigport build mark. `regproof` still judges the rows fail-closed; no
evidence is `unproven`, and a disproof is `changed`, never `ok`. Internal producer breakage remains a FAIL in the log.
Before boot, the gate runs `regmap_check.py --record` against the **copy's** exe and deployed common mapping and installs
that reflection-checked candidate only into the disposable copy. This lets the live guard sample a new build without
requiring an already-proven record. It never copies the candidate into the source instance; it expires with the copy.
If candidate generation fails, the gate logs no evidence and the live guard stays closed.

`regmapDigest` ties a lab file, and the record a proof writes, to the mapping the run used: sha256 (lower-case hex) over `<file name>\n<sha256 of its
bytes>\n` for each `regmap/common/*.json` of the game's Kiln folder, in name order (build records are left out: they are
what the proof writes). kilnctl sets aside every suite whose digest is not the tree's, refuses a new build's values export
of another mapping, and with an instance refuses to judge a tree whose regmap/common differs from the deployed
`<game>/kiln/regmap/common`. The lab gate's copy starts with an empty `content/registry/proof/`. The record carries the
digest it was proven on (`"regmapDigest"`); kiln.content computes the deployed regmap/common's digest when it loads the
regmap and enables no row of a record whose digest differs, or that has none, and does not call that record verified
(registry.json's `regmap.verified`, the registry capabilities: "unverified regmap record"). The refusal says why
(review3 S4): "regmap/common differs from the one this build's record was proven on; a Kiln update is needed"; with files
in regmap/common that are not a registry's own `<registry>.json` (a copy left beside one), it names them and says to remove
them or repair or reinstall Kiln; for a server's own record (`"markedLocally": true`, which `kilnctl regproof --mark` adds
to the instance's copy only, never to the `--out` copy that is committed) it says to run kilnctl regproof again. An edit of
regmap/common needs a new proof (and a new client mirror).
`regmap_check.py` without `--exe` (tools/check.sh's registry step) fails when a committed verified record has no digest or
not this tree's, so such an edit cannot pass the checks unproven.

`registry-roundtrip`, one file per side and generation:
```jsonc
{ "format": 1, "kind": "registry-roundtrip", "side": "server", "exeSha256": "<sha256>", "generation": "live",  // live | boot
  "regmapDigest": "<sha256>",                                        // the regmap/common the run used (below)
  "constants": { "gear.seed": 10, "gear.creep": 0.15, "gear.armorOffset": 23, "gear.armorGradient": 15 },
  "kinds": { "items.manaCost": { "sint32": 138, "float": 0, "unknown": 0, "notInWorld": 55 } },   // live: impact config kinds per row
  "notInWorld": { "items.manaCost": ["game:...", ...] },             // live: the entries counted notInWorld
  "unsampled": { "drops.entries": "the game loaded no drop table for the loot containers the lab spawned" },  // live: why
  "effects": [ { "field": "loot.fixedRarity", "setting": "legendary for every entry", "observed": "...", "result": "followed" } ],
  "samples": [ { "field": "items.maxStack", "entry": "game:...", "element": null,     // element: a list element's index
                 "read": 20, "probe": 21, "readBack": 21,                            // through the schema
                 "rawBefore": [20], "rawAfter": [21],                                // each row path, through reflection
                 "reverted": true, "bytesBefore": "<sha256>", "bytesAfter": "<sha256>", "error": null } ] }
```
Raw values: the number of an int, float or bool member (a bool may be 0/1), a time member's nanoseconds, an enum's game
name, a bitmask's list of game names, an optional enum's [switch, name]. A probe must differ from the value before; a
read-only field is read, never written; the bytes are the entry's resource before the write and after the revert.

For `impactConfig` rows the live producer identifies the raw config type, never guesses it from a whole value, and
counts every entry of the id table per config kind (the suite's `"kinds": {"items.manaCost": {"sint32": 138, "float": 0,
"unknown": 0, "notInWorld": 55}}`; `unknown`: the entry holds the value but its kind cannot be read; `notInWorld`: the entry
is in the id table but not in the lab's world, so nothing reads it, and its id is listed under `"notInWorld"`). kilnctl
compares the counts with the values export (review3 S1): every entry the export holds the value for must be counted in a
kind or listed as not in the world. Entries not in the world do not block a live row (no edit reaches them on this build;
the record says how many, e.g. `"notInWorld": "3 entries not in the lab's world ..."`), but do block a boot row; any other
difference between the counts and the export, a kind name kilnctl does not know, or a missing export counts as unknown and
leaves the row unproven. Evidence is required only for the kinds
the build has (coordinator ruling, 2026-11-23): Sint32 configs need a Sint32 whole-number round trip and a fractional Sint32
probe refused without changing the value or bytes; float configs need a float-config fractional round trip. A kind with no
entry is recorded in the record's `"kinds"` as "none on this build" and does not block the row (a later build that has it
must prove it); an `unknown` entry, or a suite without counts (all three cases are then required), leaves the row unproven. Refusal samples carry `configKind: "sint32"`, `expectRefused: true`,
`refused: true`, `refusalCode: "not_exact"` and `refusalReason`; the code is emitted only for a native REFUSED outcome
whose reason starts with the stable `not_exact: ` token. Accepting or rounding the fractional probe makes the row
`changed`; missing any of these cases leaves it `unproven`. Whole-only evidence cannot prove the row.

`registry-values`, one file per side and build: every entry of every registry read through the schema, with the base as
state/registry.json names it:
```jsonc
{ "format": 1, "kind": "registry-values", "side": "server", "exeSha256": "<sha256>", "gameBuild": "1287369",
  "regmapDigest": "<sha256>",
  "base": { "origin": "live", "writer": "none" },                    // + "token" when the archive writer has one
  "registries": { "items": { "game:...": { "maxStack": 20, "power": 31 } } } }
```
An export whose base origin is empty, `none` or `unknown`, or whose writer is empty or `unknown`, is refused (writer `none`
is the game's own data, as shipped; any other writer, `kiln-eml`, `shroudforge-hosted`, `shroudforge-installed` or `external`,
is named in a note of the diff, and two exports with different writers get one more). Since 0.2.0-dev.31.18 the writer is
this start's real archive writer (state/registry.json `base.writer` and `base.archive`, registries README "Who else wrote a
field"); before, every start but a kiln-mode one said `none`, also where kiln-eml or hosted ShroudForge had patched the
archive. kilnctl compares the same way: a lab with EML or ShroudForge packages now gives a note instead of passing as the
game's own data.

**The old build** is the newest cached exe whose build has a verified regmap record (the instance's `kiln/regmap` or the
tree), not sigport's baseline. Its record, from the tree or else the instance (marked there and not committed yet),
carries the removal history and the pins: the candidate must carry every earlier removal and never give such an id
again, and keep every earlier pin or retire its id. Without that record the proof blocks; `--first-build` waives it only
where the side has no proven build yet. A row regmap/common marks unproven stays unproven with its reason whatever the lab
shows: a round trip proves the mapping, not when the game reads the value. A list or reference write must change the
game's members.

**Alias deltas (D3).** The full name-to-GUID table is made at every start from the game's own data; a record keeps only
what must not move, carried forward from the previous build's record:
- `renames` ({registry, guid, id}): this entry keeps this id: its debug name changed, the order of the archive changed,
  its drop table's templates changed, it holds a name another entry also has, or its name had characters outside the id
  grammar;
- `suffixes` ({registry, guid, id}): a clash suffix given once and kept;
- `removals` ({registry, id, removedIn}): an entry the build no longer has; its id is retired (never given again) and
  every edit naming it is refused "removed by game build ...".

Identifiers only (GUIDs and Kiln ids), never names or values of the game's data (coordinator ruling, 2026-11-20). Drop
tables are pinned when their name would move (or clash), not all at once: pinning every template-derived table name would
put a list of the game's template names in the repository; the previous build's id table plus these deltas keep every
id. How ids are made: docs/kiln/modding/registries/README.md "Ids"; the same rules in regmap_ids.py and regmap.cpp, tested
on native/services/content/tests/registry_ids_cases.json.
