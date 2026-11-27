# Adding a registry (maintainer guide)

For Kiln maintainers who add the next registry (perks, skills, stations, NPCs, ...) to the seven that ship today (items,
recipes, loot, drops, balance, and since 0.2.0-dev.31.19 lootRules and randomLoot). It says what a new registry gets for free, the steps in order, every pitfall the first
five hit and the fix that is in the code now, and when a new registry is done. Mod-facing docs are
[modding/registries/README.md](modding/registries/README.md); the native tables are [services/registry.md](services/registry.md);
the mapping and its proof are [regmap/README.md](../../regmap/README.md) and [sigport.md](sigport.md#the-registry-proof-kilnctl-regproof).

Written against dev.31, including entry operations, the proof producer and the client mirror. `file:line` references
are to that tree; line numbers drift, the function and test names do not.

## 1. What a registry is, and what is shared

A registry is a slice of the game's data under **Kiln-owned names**: a registry name (`items`), Kiln ids for its entries
(`game:material_t1_wood_logs`, `<mod>:<name>`) and Kiln fields with types, ranges, list ops and an `applies` time
(`items.maxStack`, `worldLoad`). Mods and data packs edit those names; they never see a game function, member, offset or
reflection name (owner ruling, 2026-11-20: "mods and data packs never know game functions or memory"). Kiln translates
the names per game build through `regmap/`, and enables a field on a build only when a lab proof says the mapping holds
there. Registries hold data only; services and events stay services.

Registry edits are layers. They sit above the older writers (boot resource patches, base rules, data packs, other
loaders' archive values) and below `Content.*` code; an absolute registry set wins and is reported, a relative op
composes on the earlier value (ruling D1). An old mod is never refused because of a registry edit.

The path of one edit, and who owns each step:

| Step | Shared code (a new registry plugs in) | What is per registry |
|---|---|---|
| Schema: names, types, ranges, ops, applies, ids rule | `schema/registry/<r>.json`, `tools/registrygen/registrygen.py` (writes `schema.g.h`, `Kiln.Registry/Generated/<R>.gen.cs`, `RegistrySchema.gen.cs`, `schema/registry-section.schema.json`, the doc page) | the schema file; `ids.lock.json` rows via `registrygen.py lock` |
| Mapping onto the game | `regmap/common/<r>.json` rows read by `registry/regmap.cpp`, checked by `regmap_check.py` (types against reflection, routing rule, reasons in Kiln words) | the `entries` block and one row per field |
| Base values | `registry/model.cpp` (`decode` :352, `encode` :509) over `BaseView`/`PristineView`, decoded through reflection (`model_bytes.cpp`) | nothing, unless a new `conv` or entries kind is needed |
| Ids | `map::assign_ids` (regmap.cpp), `Model::list_all` (model.cpp:898), the per-build id table, alias deltas in the build record; Python twin `regmap_ids.py` / `regmap_entries.py` | the stable-name rule if it is new (`unnamed`, `nameFrom`, `key`) |
| Edits in | IR (`registry/ir.cpp`), data sections (`ir.cpp` `lower_section`, read by `stage.cpp`), C# `OnRegistry` and `Registry.Change` (Kiln.Host `RegistryDispatch.cs`), native `kiln.registry` / `kiln.registry.live` | nothing |
| Composition | `registry/compose.cpp` (stages, claims, `exclusive`/`yield`, references), `claims.cpp` | ref targets if the registry is referenced |
| Mapping guard | `registry/unmapped.h` `refuse_unmapped_edits` (stage and live share it) | nothing |
| Restart fields | boot back-end: `registry/boot_bridge.cpp` `write` (:156), scalar leaves on a resource copy before the game reads it | nothing for scalars |
| Now / worldLoad fields | live back-end: `registry_live.cpp` `bind` (:713) over kiln.content's layered store (`store.cpp`) | `kind_of` (:129), entry lookup, and a binder for any non-scalar `conv` |
| Report | `registry/report.cpp` -> `kiln/state/registry.json`; launcher Conflicts view `launcher/src/registry/report.ts` (field names from `ids.lock.json`) | nothing |
| Capabilities | `stage.cpp` `report_caps` (:368): `registry.<r>` and `registry.<r>.<field>` rows from the schema and the record | list-field capability names in the registries README table |
| Proof | `registry/proof.cpp` (boot round trips, read-only reads, values export), `mods/tests/registry-proof-test` (live round trips), `kilnctl regproof` (runner/kilnctl/src/regproof/) | producer probes for the registry; record rows |
| Typed C# | generated entry types; hand-written registry property on `RegistryStage` (`Kiln.Registry/RegistryStage.cs:35-44`) | that property + its PublicAPI line |

So a registry whose fields are plain scalars (int, float, bool), enums and times, with one resource per entry, needs a schema, a
regmap file, one `kind_of` line and its store kind, one `RegistryStage` property, producer probes and tests. Lists,
references, per-entry storage kinds, lazily loaded tables and entries without a GUID each add work; section 2 says where.

**The last two, as a worked example (0.2.0-dev.31.19, the Loot Rarity Leveler lane).** `lootRules` (one entry: the five
world-wide rarity weights) and `randomLoot` (797 tables of two resource types) needed: the schemas; regmap rows with
fixed-array element paths (`globalRarityDropRates[0]`, `modifier.rarities[2].value`, which `regmap_check.py` now resolves);
the new entries kind `arrays` for randomLoot's two sources and five scene resources (regmap.cpp, model.cpp `entries()`, the
Python twin in `regmap_entries.py`; names from each source's pattern, numbers from `registry/element_num.h`); two kiln.content
store kinds (5 and 6, `registries.cpp` `build_loot_tables`, one reflected schema shared by both table types) and their
`kind_of` rows; the proof producer's `Live` list, `RegistryOf` (kinds 5 and 6 by kind) and `ResourceOf`; and, because every
regmap/common edit unbinds the records (P9), a fresh lab proof of all seven registries and a client mirror in the same change.
All their fields are plain floats and enums, so the generic `bind_scalar` writes them; no new binder or conv.

## 2. Checklist

Each step says what "done" means. Write the failing test first wherever the step changes behaviour (red, then green, both
counts in the report).

### 2.1 Reverse engineering: where the data lives, its exact layout, and when the game reads each field

Write it down in `docs/re/<area>.md` (evidence the regmap rows cite) before any schema work. For every field you want:

| Question | Why it matters | Example from the first registries |
|---|---|---|
| Which resource type backs an entry, and how many per entry (one resource, an element of one resource's array, one singleton)? | the regmap `entries.kind` (`resource`, `array`, `single`) | items: one `ItemInfo` per item; recipes: elements of one registry resource; balance: one table |
| What identifies an entry (GUID member, 32-bit id member, debug name)? Do all entries have names? Are names unique? | the id rule (`name`, `guid`, `id`, `unnamed`, clash suffixes) | drop tables have no names: named after the templates that use them (`nameFrom`) |
| The member path and storage type of each field, per build (reflection) | the row's `paths` and `conv`; the schema range must fit the storage | `maxStackSize` is u16, so `items.maxStack` max 65535 |
| Is the storage the same for every entry? | a field whose storage varies per entry needs per-entry checks (pitfall P3) | mana/stamina costs: Sint32 or float impact config per item |
| Is the data resident and when is it loaded? | lazily loaded data cannot be bound at world load; a playerless lab must make the game load it (P6) | drop tables are acquired when something needs them: the lab spawns loot containers |
| **When does the game read the value**: at each use (`now`), once before the save is read (`worldLoad`), once at process start (`restart`)? | the row's `read` and the schema's `applies`; picks the back-end | balance table: restart (boot back-end); `maxStack`: before the save is read (stacks are cut) |
| Does a vanilla client show or predict it from its own copy? | the row's `clientNeeds` | item costs: the tooltip follows on the client |

A read time you have not traced is a finding, not a guess: the row ships `"read": "unknown"`, `"state": "unproven"` with a
reason, and the schema field gets the cautious `applies` (worldLoad or restart, never now). That is how `items.block`,
`items.comfort`, `items.fuel.burnTime` shipped in dev.31 (P7); 31.x traced them (docs/re/content.md section 11). A field the
game never reads (`items.fuel.burnTime`: no reader in either exe) stays unproven with that as its reason: an edit of it
would change nothing, so it is refused.

### 2.2 Schema rows and registrygen

- Add `schema/registry/<r>.json` (`registrySchema: 1`, `version: "0.1.0"`, `stability: "experimental"`,
  `experimentalId: "KILNX031"`, `ids` rule text, `fields`). Kiln words only: registrygen refuses game type names, `keen::`
  names and hex offsets (`GAME_WORDS`, registrygen.py).
- Every number gets a range that fits its storage (ruled after the first IR review);
  every leaf has `applies` (its own or its parent's); ops must fit the type (`OPS_FOR`).
- A `ref` names a registry Kiln has; a registry that exists only as a reference target so far goes in
  `LATER_REGISTRIES` (registrygen.py) until it has a schema.
- `python3 -I tools/registrygen/registrygen.py lock`, then `registrygen.py` (writes the outputs), then `--check`.
  `ids.lock.json` ids are never reused; a removed field is `retire`d, not deleted. Within 0.x a breaking change needs a
  minor bump plus a migration file (README "Versions"). registrygen builds the migration into `schema.g.h` (`kRenames`) and
  `ir.cpp` `parse_batch` reads every edit through it at run time; it does not keep an `[Obsolete]` C# property under a renamed
  field's old name yet, so add one by hand (PublicAPI is additive).
- The generated doc page has a Mapping column from regmap; never edit the page by hand.

Done when `registrygen.py --check` passes and `git status` shows the generated files.

### 2.3 regmap/common mapping

`regmap/common/<r>.json`: the `entries` block and one row per schema field (format in
[regmap/README.md](../../regmap/README.md)). Rules that bit us:

- `read` may not be later than the field's `applies`; `boot` serves only restart fields, `live` only now/worldLoad,
  read-only fields have `none`. `regmap_check.py` fails a row that breaks this; the native loader turns it into an
  unproven row (`check_routing`, regmap.cpp:362).
- `reason` is shown to players and mod authors: Kiln words only. `regmap_check.py` refuses a reason that names a game
  member or type (regmap_check.py:324-330); game names go in `evidence`.
- Paths are the reflection's member paths from the entry root (from the element for list element rows). Know the
  reflection's collapsing rules: a struct with one scalar member is that scalar at its parent path (refl.cpp:141-150,
  pitfalls P1 and P2).
- **Any edit of a file in `regmap/common` turns every row of every build off until the build is proven again** (the
  record's `regmapDigest` no longer matches, regmap.cpp:326). Plan the mapping edit, the lab proof and the client
  mirror as one change (P9).

Done when `python3 -I tools/registrygen/regmap_check.py` passes (tools/check.sh's registry step) and, with `--exe` on the
current server and client exes, every row resolves and type-checks.

### 2.4 Ids and the id table

- Most registries reuse the generic rule: `game:<debug name, lower case>`, `<unnamed>` printf for nameless entries,
  `~<GUID hex>` clash suffixes given once and kept, a removed entry refuses every edit naming it. Ids are lower case.
- A new rule (no GUID, names taken from another resource, an id shared with another registry like loot's `key: "items"`)
  goes in `map::assign_ids` (regmap.cpp) **and** its Python twin (`regmap_ids.py`, `regmap_entries.py`), with cases in
  `native/services/content/tests/registry_ids_cases.json` that both run. kilnctl compares the two implementations
  (`pins_hold_ids_are_unique_and_both_implementations_agree`, ids.rs).
- The per-build id table must list every registry and the reference tables (P12). `Model::list_all` (model.cpp:898)
  iterates the schema registries plus `templates`, `perks`, `inputCategories`; a new reference-only table goes in that
  list.
- Records commit identifiers only (GUID deltas: renames, removals, suffixes), never name lists or game values
  (ruling 2026-11-20).

### 2.5 Value conversions (and when a new `conv` needs a binder)

The model decodes and encodes every `conv` in the table in regmap/README.md (`decode`/`encode`, model.cpp:352/:509).
Reuse one if the shape fits. A new conv touches all of these, in one change:

1. `regmap_check.py` `check_conv` (which member types it needs);
2. `model.cpp` `decode`/`encode` (base values, boot writes, values export);
3. `registry_live.cpp` `bind` if any field using it is `now`/`worldLoad` (without a binder the live back-end refuses
   it: "the live back-end does not write <conv> fields yet");
4. `kilnctl` round-trip comparison (`roundtrip.rs` raw kinds) and the producer's raw read (`RegistryProofTestMod.cs`);
5. the conv table in regmap/README.md.

Scalar convs (`int`, `float`, `bool`, `time`, `enum`) already bind generically (`bind_scalar`). `flags` binds generically too
(`bind_flags`: the bitmask's bits for the row's names, written over the value below so bits Kiln has no name for stay the
game's; a name this build's enum lacks is refused, named), and so does `optionalEnum` (`bind_optional_enum`: two slots, the
switch and the enum, each evaluated over the other's value below the registry; `none` turns the switch off and leaves the
enum below it as it is; the switch is a store gate, `store_set_gate`, so a flush writes it after the enum when it turns on
and before the enum when it turns off). Both keep compare-before-write, rollback and per-entry refusals (31.x; tests `loot_flags_*`,
`loot_fixed_rarity_*` in registry_live_transactions_test.cpp). Lists, references, impact configs, unlock and drop walks each
have their own binder (`bind_recipe_list`, `bind_perks`, `bind_impact`, `bind_unlock`, `bind_drops`) tied to their store
kind, so a list or ref field in a new registry needs its own.

Pick the schema type from the storage: a schema `int` rounds a relative op's result to the nearest whole number by
design (ir.cpp:639); where some entries store whole numbers and others fractions, the schema field is `float` and the
binder refuses an inexact value per entry with `not_exact: ` (P3). Never round silently.

### 2.6 References and dependency refusal

- `ref`/`refList` fields name entries by Kiln id; composition checks them (`check_references`, compose.cpp:1071) and a
  reference to a missing, removed or refused entry refuses that edit with the cause chain.
- Raw-path edits (Advanced tier) are checked against the Kiln fields they cover (`raw_cover`, compose.h:65): a raw path
  inside a mapped field composes as that field or is refused, never silently doubles it.
- If your registry is referenced by another (recipes -> items), check both directions: an edit that would point at a
  removed entry is refused on that target only; valid data stays.

### 2.7 Boot back-end (restart fields)

Restart fields are written into a copy of the resource at resources-ready, after the old boot documents, before the game
reads it (`boot_hooks.h`, `boot_bridge.cpp:156`). It is generic for scalar leaves: each write is resolved and bounds-checked
before any is made, so a field is written whole or not at all; references, GUIDs and list resizing are refused
("not a scalar member of the resource"). Nothing to add for a scalar restart field. A restart field edited from a later
entry point is `RestartRequired`, never applied (owner ruling 2026-11-22 on entry points).

### 2.8 Live back-end (now / worldLoad fields)

- Add the registry to `kind_of` (registry_live.cpp:129) with its kiln.content store kind (`KILN_CONTENT_*`, `reg(kind)`,
  registries.cpp). Without it every live field is refused "this registry has no live back-end" (:726). A registry
  kiln.content has no store kind for yet needs one first: entries in the world (`find_entry`, :165) and the store's
  writable field schema come from it.
- Writes go through the store's layers (packs < registry < code) with compare-before-write: a slot another writer changed
  after Kiln's layer stays with that writer for the world and is reported "another writer" (store.cpp:519-538, ruling
  2026-11-22 5:22 PM).
- Data the world loads later (drop tables) must bind when it loads, on every load, and rebind if something repoints it
  (P6).
- Runtime entry points refuse per entry; Manual transactions are atomic (P19). A failed write is charged to the change
  that owns the slot (P17).

### 2.8a Fields that change existing saves

Decide for each field whether an edit of it changes what a save holds (a cap the game enforces on saved data, a range a load
clamps saved copies into, a list a saved index points into) and add its case to `registry/saves.cpp` `judge`: refuse, guard,
warn or report (README "Edits that change existing saves"), with a `registry_saves` test. Restart fields are judged at the
stage (`stage.cpp` `compose_checked`), live scalar fields at each write over the value under them (`registry_live.cpp`
`save_check`; a new entry's first values are not judged), and every field of a rollback's plan also against the plan it rolls
away from (`stage.cpp`, whole plan); a field read only at use or for new copies needs nothing.

### 2.9 Managed typed API and analyzers

- Generated: `Kiln.Registry/Generated/<R>.gen.cs` and the catalog. Hand-written: the registry property on `RegistryStage`
  (RegistryStage.cs:35-44: `RegistryEntries<TEntry>` for a keyed registry, a single entry for a singleton like balance).
- New public lines go in `PublicAPI.Unshipped.txt` only, under KILNX031; no version bump (the coordinator bumps once).
- Analyzers: KILN122 (declared points without an override) and KILN123 (invalid points) are registry-independent;
  nothing to add unless the registry brings a new authoring shape.
- Offline authoring (`KilnHarness.Registry.Seed`/`Author`) is generic by registry name; add a TestKit case if the registry
  has a new shape (list of entries, singleton).
- Update `mods/samples/RegistryEdits` if the registry is one authors will reach for first.

### 2.10 Report and the Conflicts view

`registry.json` rows, conflicts and refusals are generic (report.cpp). The launcher takes field names from
`schema/registry/ids.lock.json` (report.ts:3-6), so `registrygen.py lock` is enough for it to show a new registry's fields
as Kiln fields. Raw-path refusals carry `"raw": true` so the view hides the member path (report.cpp:101/131/143,
report.ts:52-60). Check the view with a fixture that has your registry's rows if its values are lists or structs.

### 2.11 Proof producer probes

The lab writes the evidence kilnctl judges (regmap/README.md "The producer"):

- Boot fields and read-only fields: `registry/proof.cpp` samples them generically from the base; nothing to add unless
  the registry's entries are array elements (boot samples skip array entries: "boot fields of array entries are not
  sampled").
- Live fields: `mods/tests/registry-proof-test/RegistryProofTestMod.cs` lists the live registries (`Live`), enumerates their
  entries through `Content.*` (`RegistryOf`) and reads raw bytes by resource (`ResourceOf`). A new live registry needs
  an entry there. Phase 1 runs at the world's first content ready; phase 2 (`RegistryProofTestMod.Later.cs`) runs once the
  world runs: it loads drop tables the way the game does for a player (`LoadDropTables`: a few of the game's loot containers
  spawned next to a container of the lab's world, or at the lab world's spot when it has none, whose default_inventory
  program acquires their tables), samples the drop
  rows over them, and drives real loot rolls (`Rolls`). Data the game loads on demand needs such a loader, or its rows stay
  unproven with the producer's `unsampled` reason.
- **When the game reads a field is regmap/common's decision, never the round trip's** (P7). Where a lab can show it, the
  producer shows it: `Rolls` gives every loot entry each step's values in one Manual change, spawns the game's random pickup
  spawners (with a creature's loot labels; a default inventory never rolls random loot, docs/re/content.md section 11) and
  reads each roll from the spawner's own record (count for `stack.scalable`/`maxScaled`, rarity for `rarities`/`fixedRarity`).
  The results go into the suite's `effects` and kiln.log (PASS/FAIL/INCONCLUSIVE); kilnctl does not judge them. In the
  playerless lab of 31.x the spawners recorded no roll (INCONCLUSIVE; also with the slot's generic pickup given, which the
  spawner program needs: the gate left is its loot generation version check, docs/re/content.md section 11), so these
  effects need a lab with a player.
- A probe must change the value, be in range, and exercise every storage kind the build has (P3, P4, P10). A field the
  producer cannot sample gets an `unsampled` reason in Kiln words, which becomes the row's unproven reason.

### 2.12 The lab proof and the record

1. Lab dedicated server on the current server exe (`kilnctl gate`: its default tests include `registry-proof-test`, and
   the gate writes the request file into its copy).
2. `kilnctl regproof <name> --mark` (`--first-build` only where the side has no proven build; `--replace` over a verified
   record). Exit 0 = verified.
3. Commit the record from `--out` as `regmap/server/<sha>.json` (carries `regmapDigest` and `"provenBy": "lab"`).
4. Mirror the client: `kilnctl regproof --side client --mirror regmap/server/<sha>.json --server-exe <exe> --new-exe
   <client exe> ...`; commit `regmap/client/<sha>.json`. No game client is ever run for a proof (owner ruling
   2026-11-23); a client row is ok only where the server row is ok and the client's layout of it is the server's.

Commands and file contracts: [sigport.md](sigport.md#the-registry-proof-kilnctl-regproof), [kilnctl.md](kilnctl.md),
[regmap/README.md](../../regmap/README.md).

### 2.13 Tests

| Suite (native: `native/services/content/tests/<name>_test.sh`, needs `KILN_OUT`) | What a new registry adds |
|---|---|
| `registry_generated`, `registry_schema` | the generated descriptor holds the registry's fields, ranges, applies |
| `registry_ir`, `registry_compose`, `registry_claims`, `registry_refs`, `registry_raw`, `registry_review`, `registry_review2` | only if the registry adds op shapes, references or raw coverage |
| `registry_model` | decoding its entries and fields from bytes (one-scalar structs, byte arrays, nested lists: P1, P5) |
| `registry_regmap` | its rows load, unproven rows keep reasons; reads the **real** committed records |
| `registry_formulas` | only for a new formula |
| `registry_stage`, `registry_stage_review` | a start with a section editing it; caps rows; id table complete (P12) |
| `registry_live`, `registry_live_review`, `registry_live_transactions`, `registry_store` | each live field: write, read, revert, foreign write, pending/late data, per-entry refusal |
| `registry_report`, `registry_proof` | report rows; boot/read-only producer samples |
| `registry_saves` | the field's save class, if it has one (2.8a) |
| Python `tools/registrygen/tests` | id rule cases, conv checks |
| `cargo test` in `runner/kilnctl` | new raw kind, new kind counts, id rule agreement |
| managed: Kiln.Registry.Tests, Kiln.Host.Tests, Kiln.Contract.Tests, Kiln.TestKit.Tests, Kiln.Analyzers.Tests; `gen-apis` | the typed property, PublicAPI |
| `mods/tests/registry-proof-test/host-tests` | new probe paths |
| launcher units/typecheck | only if report.ts changes |

The back-end suites run on a copy of `regmap/` with every row ok (`regmap_fixture.sh`); only `registry_regmap` reads the
committed records (P11). Run the suites through the wave's slot runner (one heavy command at a time) and build the
Windows DLLs once (`win1-native`) before calling it done.

### 2.14 Docs

- The generated page (`docs/kiln/modding/registries/<r>.md`) and a row in the registries README table.
- Capability names for list fields in the README table (names drop `[]`).
- New limits in [modding/limits.md](modding/limits.md); a new conv or entries kind in regmap/README.md.
- `tools/docs-site/drift.py` clean for the files you touched.

## 3. Pitfalls we hit, and the fix in the code

Each row: the symptom we saw, why, the fix that is in now, and the code or test that holds it.

### Reading the game's data

| # | Symptom | Cause | Fix | Where |
|---|---|---|---|---|
| P1 | Every live read/write of items and recipes failed "entry is not in this world"; the loot id table had 1 entry instead of 4,213; formulas 0/10 | the model read one-value structs (item ids, `HashKey32`) as `{"value": n}`, so every entry number was 0 | check for a single leaf first, as the gamedata walker does | `model_bytes.cpp:44` (`json_of`); `registry_model_test` `single_scalar_structs` |
| P2 | `items.socket` could not bind (`gemSlotOverride.isSet` not found) | reflection exposes a struct with one scalar at its parent path (refl.cpp:141-150), so the row's `.isSet` leaf does not exist in the store schema | bind the flattened parent when it is a bool; raw reads fall back the same way | `registry_live.cpp:754-764`; `registry_live_transactions_test` `flattened_socket_read_write_revert`, `staged_flattened_socket_binds`, `flattened_socket_foreign_revert_is_skipped` |
| P3 | `manaCost 67.5` became 68, `2.5` became 2 in game | some items store the cost as a Sint32 config, others as float; `encode` rounds into an int (refl.cpp:412) | refuse an inexact value per entry with the stable token `not_exact: `, never round; float-config entries keep fractions (ruling 2026-11-22 9:09 PM; wire form 10:45 PM) | `registry_live.cpp:659-662` (`bind_impact`); `kiln_registry_live.h:62-64` `KILN_REGISTRY_LIVE_REASON_NOT_EXACT`; tests `fractional_integer_cost_is_refused`, `whole_and_float_costs_still_work`, `staged_fractional_integer_cost_is_refused`, `entry_point_inexact_cost_drops_only_that_entry` |
| P4 | mana/stamina could never be proven on f0d824be; then the record said "138 entries" while 141 items hold a cost | kilnctl demanded a float-config round trip on a build with no float configs; and the producer's count skipped 3 items the lab world does not load | evidence only for kinds the build has ("none on this build" recorded); entries in the id table but not in the world are counted `notInWorld` and listed; counts are checked against the values export; unknown kind names block | `RegistryProofTestMod.cs` `CountKinds` (:430); `roundtrip.rs` tests `impact_configs_need_evidence_only_for_the_kinds_the_build_has`, `every_entry_that_holds_an_impact_config_is_classified`; record `"kinds"` (regmap/README.md) |
| P5 | `balance.altarsPerFlameLevel` read as `"(21 bytes)"`; missing from the values export | byte arrays went through the dump summary, not as numbers | read byte arrays as lists, through guarded reads bounded to the resource; over 65,536 is refused, never cut | `model_bytes.cpp:45-56`; `registry_model_test` `byte_array_bounds`; `registry_proof_test` `byte_array_lists` |
| P6 | drop-table edits reported changed but the game kept the old chance; later, 6 drops rows could not be proven | the game acquires drop tables when something needs them: the bind ran only for packs with drop rules and only once, and newcontent repoints a table's stacks; a playerless lab world loads no table | bind on every acquire, after newcontent, rules or not; a table not loaded yet is pending, not refused; the producer's phase 2 loads tables in the playerless lab by spawning the game's loot containers next to a container of the world, or at the lab world's spot (31.x), and says why in Kiln words when it cannot | `content.cpp` -> `registry_live.cpp` `drops_table_loaded`; tests `drop_table_without_old_rules`, `drop_table_repointed_by_newcontent`; producer `LoadDropTables` (`RegistryProofTestMod.Later.cs`), `unsampled` |
| P7 | `items.block`, `comfort`, `fuel.burnTime` and four loot rows were off on every build | when the game reads them was not traced (or not seen in a lab roll) | ship `read: unknown` / `state: unproven` with the reason and the cautious `applies`; a lab round trip does not lift a row regmap/common marks unproven (it proves the mapping, not the read time). 31.x traced them (displacement scans of both exes, LootFind's callers; docs/re/content.md section 11): block, comfort and the loot rows read now; `fuel.burnTime` has no reader and stays unproven with that reason. No playerless lab path rolls random loot (a default inventory skips random stacks; the pickup spawner waits for more than a spawn), so the loot rows' read times rest on the disassembly, as `stack.min`/`stack.max` did | `regmap/common/items.json`, `loot.json`; `regmap.cpp` `check_routing`; kilnctl `a_row_unproven_in_common_stays_unproven_whatever_the_lab_shows` (mod.rs); producer `Rolls` |
| P8 | `KILN_REGISTRY_VIEW_BASE` and registry.json showed data-pack values as the game's | with no registry user at the stage, the base was decoded at first use from resident pages kiln.content had already written | Kiln.Host flags mods whose code uses the registry API (`"registry": true` in the stage order) so the stage decodes at resources-ready; a later first-use decode restores Kiln's own store originals over the bytes (`PristineView`) and runs outside the store lock | `stage.cpp:434-440`, `:754-758`; `registry/pristine.h`; `stage_service.cpp:68-80`; `store.cpp:487` `store_pristine`; tests `registry_users_decode_at_the_stage`, `lazy_base_keeps_pre_patch_resource_bytes`, `lazy_decode_reads_pristine_bytes` |

### Proof and records

| # | Symptom | Cause | Fix | Where |
|---|---|---|---|---|
| P9 | a cosmetic edit of an `evidence` string would turn every field off for every player and pass tools/check.sh | the record is bound to the mapping it was proven on (`regmapDigest` over every `regmap/common/*.json`); a mismatch disables the record | intended: re-prove after any regmap/common edit. `regmap_check.py` (check.sh) now fails when a committed verified record's digest is missing or not the tree's; the refusal names the cause (common changed, stray file, local mark) | `regmap.cpp:326-341`; `regmap_check.py:145-176` (`record_problems`); `registry_regmap_test` (digest checks); kilnctl `a_suite_of_another_mapping_is_set_aside` |
| P10 | the proof marked manaCost ok while the back-end still rounded | the probe used a whole number (+1) on a whole-number entry, so the rounding never showed | probe each kind on purpose: Sint32 whole (round trip), Sint32 fractional (must be refused with `not_exact`), float fractional (round trip); rounding or accepting the fraction makes the row `changed` | `RegistryProofTestMod.cs` probes (:549 and the impact-config samples); `roundtrip.rs` `impact_configs_require_whole_fractional_and_refusal_evidence` |
| P11 | after the first real record, live/transactions/stage suites went red on unproven rows | the back-end suites assumed the committed record had every row ok | back-end suites run on an all-ok copy (`regmap_fixture.sh`); `registry_regmap` tests the real records and pins shipped behaviour | `native/services/content/tests/regmap_fixture.sh`; `registry_regmap_test.cpp` |
| P12 | id table lost perks and input categories on starts without registry content; regmap_check could not read kiln.content's table; the table was rewritten with only the decoded registries | the id ledger came from what a start happened to decode; wrong file shape | write it once per build when missing, entries only, every schema registry plus the reference tables; regmap_check unwraps kiln.content's shape | `stage.cpp:746-747`, `:793-799`; `model.cpp:898` `Model::list_all`; `registry_stage_review_test` `id_table_overwritten`, `id_table_lists_reference_tables` |
| P13 | an element row (`drops.entries[].item`) counted ok while its list was unproven; a bool read from zero padding passed as `false == false` | element evidence alone; zero is the default value | element rows need their list row ok; read-only bools prefer an entry whose value is `true` | kilnctl `element_rows_require_their_list_row_to_be_ok` (mod.rs); `proof.cpp:43-55` (bools prefer `true`) |
| P14 | a structural write refusal (no such member) came out `unproven`, pointing maintainers at the lab instead of the mapping; a null raw value hid other failed steps | all refusals were filed as missing evidence; null raw skipped the sample | structural refusals are an `error` sample (`changed`); guard, range and entry-specific refusals stay missing evidence; a null raw member skips only the raw comparison | `proof.cpp:144` `disagreement`; `RegistryProofTestMod.cs:258` `IsDisagreement`; `registry_proof_test` `structural_refusals_are_disagreements`; `roundtrip.rs` `unreadable_raw_members_do_not_hide_failed_steps` |
| P15 | the default lab gate went red on every new build and blocked the sigport mark | no record for the new exe = every live read refused = zero samples = FAIL | the proof test is evidence only (never decides the gate); the gate installs a reflection-checked candidate record only into its disposable copy so the live guard can sample | `runner/kilnctl/src/gate.rs` `evidence_only` (:189), `proof_candidate` (:251), `COPY_CLEAN` (:239) |
| P16 | a stale record or stale lab files could verify a mapping no lab ran | kilnctl took the tree's old record when regmap_check failed; lab files carried no mapping identity; removals were seen only in the candidate | fresh candidate only; suites and exports carry `regmapDigest`; the old build is the newest with a verified regmap record and its removal history must carry; `--mirror` accepts only `provenBy: lab` | kilnctl `the_old_builds_record_carries_the_removal_history`, `the_default_old_build_has_a_verified_regmap_record`, `a_values_export_of_another_mapping_proves_nothing`; mirror `the_mirror_fails_closed` |
| P33 | a one-flag or empty bitmask could never prove `loot.rarities` | kiln.content gives a bitmask's names as text (`"Common,Rare"`, `"Epic"`, `""`); the producer turned only text with a comma into a list | a flags row's raw value is always the list of the game's names | `RegistryProofTestMod.cs` `FlagsRaw`, `FlagNames`; host test `Flags_raw_values_are_lists` |
| P34 | drop entries' counts and chances got no sample; a chance of 1.0 was probed to 1.25 | kiln.registry.live's struct values have flat dotted keys (`"count.min"`) and the producer looked them up nested; probes ignored the range | leaves read flat keys first; a list element's probe changes a count first and halves a chance; a float probe refused for its range is tried again at 0.75 x on the same entry | `Leaf`, `SetLeaf`, `Probe`, `LowerProbe`; host tests `Element_leaves_read_and_write_flat_dotted_keys`, `Drop_entry_probe_stays_in_range`, `A_float_probe_refused_for_its_range_is_tried_lower` |
| P35 | a later `none` over a fixed rarity seemed to leave the earlier change's enum | live changes compose into one registry layer per slot, evaluated over the bytes below the registry, not one layer per change | expected: `none` leaves the game's own enum below the switch; reverting the later change restores the earlier one | `bind_optional_enum`; test `loot_fixed_rarity_read_write_revert` |
| P36 | with an item removal active, every Manual change (an item's maxStack) reported PendingResource, so `Result.Ok` was false | the commit's outcome looked at every drop-table field waiting for its table (the removal's disabled slots in tables the world had not loaded), not at the change's own | the outcome is PendingResource only when a pending field's chain holds a row of this change | `registry_live.cpp` `registry_live_commit` (`own_pending`); test `unrelated_pending_table_keeps_a_change_applied`; lab: registry-entries-test "later field edits bind the stable added id" |
| P37 | a fixed rarity turned on was, for one write, on over the enum it had before (a roll on a game thread could take that rarity); a failed enum write left the switch on over it until the rollback | the switch (+5) and the enum (+6) are two slots of one flush, and write_batch writes in address order: switch first | a gate slot (`store_set_gate`, the switch) is written after the flush's other writes when it opens and before them when it closes; it does not open when another write of its entry failed | `store.cpp` `store_flush_checked`; `bind_optional_enum`; test `loot_fixed_rarity_switch_never_on_over_a_stale_enum` |
| P38 | the lab round trip of `loot.fixedRarity` wrote the switch alone (`none` -> `common` over an entry whose enum below was already Common), so the enum's write was never seen | the probe took the first rarity, whatever the enum below the switch holds | from `none` the producer takes the first rarity whose game enum differs from the raw enum below, so both members change | `RegistryProofTestMod.cs` `ProbeOf`; host test `An_optional_enum_probe_changes_the_enum_below_too` |
| P46 | the compat rehearsal's producer (the 31.x registry bundle's tree) gave `items.levels.min` no sample on either build, while the committed record has it ok | the producer probes a whole number with +1; a higher lowest level narrows an item's level range, which the registry's save check (31.x, `registry/saves.h` `levelRange`) refuses, so every entry it tried was refused ("saves: ... this edit was not applied"); the committed record was proven before the save checks landed | a probe a save check refuses is tried the other way on the same entry (one lower, staying at 1 or more), and an entry that cannot go that way is skipped without counting as a failed try. The committed ok stands: it rests on a real round trip of the mapping on that build; the save check is a guard added after it, not a change of what the row maps | `RegistryProofTestMod.cs` `IsSaveCheck`, `SaveCheckProbe`; host test `A_probe_a_save_check_refuses_is_tried_the_other_way` |

### Composition, live changes, entry points

| # | Symptom | Cause | Fix | Where |
|---|---|---|---|---|
| P17 | one failed write removed every other mod's live changes and their report rows | `store_flush` returned one error for the batch, charged to every retained change | the flush returns the unwritten slots; a change is rejected only for its own slots, for this world only (handle stays valid, a ModLoad change retries next world); rows survive the rollback; reasons name Kiln places, never addresses | `registry_live.cpp:1033` `reject_change`, `:1057` `reject_unwritten_changes`, `:1105` `apply_changes`; `store.cpp:503` `store_flush_checked`; tests `candidate_write_failure_keeps_other_owners`, `unrelated_application_write_failure_keeps_live`, `rejected_rows_survive_the_rollback_pass`, `multi_change_write_failure_is_attributed_per_change` |
| P18 | `x = x * 2` gave 20 at GameStart but 40 at WorldLoad (two mods in one pass, or one mod declaring two points); Alter results depended on load order | runtime passes read live bytes that included the reading mod's own earlier layers and the pass's earlier Alters | every callback in a runtime pass reads the pre-pass values without its own registry layers (`read_pass`); the pass commits Adds, then Alters, then Finals at the end, in load order (ruling NS5, 2026-11-23) | `RegistryNative.cs:128` `ReadPass`; `stage_service.cpp:475` `h_read_pass`; tests `RuntimePassesReadPrePassValuesAndCommitAtTheEndInLoadOrder`, `ARuntimeReadExcludesTheReadersOwnLayers`, `TwoModsDoublingInOnePassDoNotStack`; native `pass_read_excludes_own_layers` |
| P19 | one inexact cost or one exclusive-claimed entry refused a whole runtime pass over `Select("*")` | the pass was one transaction | entry points (GameStart, ModLoad, WorldLoad, HotReload) refuse **per entry** and return Applied with `refused > 0`; **Manual** stays all-or-nothing for binding and evaluation failures. Known per-field exceptions inside Manual: parse, composition-conflict and restart-only refusals refuse that edit alone (ruling NS6; slice 5e ruling 8:40 PM). Docs must say which is which | `registry_live.cpp:109` `per_entry`, `:1071` `exclude_refused_entries`; tests `entry_point_selector_refuses_per_entry`, `selector_conflict_refuses_one_entry_at_entry_points`, and Manual: `claims_and_binding_failure`, `restart_is_per_edit_and_begin_is_bounded` (registry_live_transactions_test.cpp:817-895) |
| P20 | a ModLoad author retried every 5 s for the whole session with the real error hidden; before that, one transient UNAVAILABLE faulted a ModLoad author forever | every `KilnUnavailableException` was treated as transient, including the author's own unreadable field | only Kiln's own steps (begin/commit during install, budgets, lazy preparation) retry, with backoff, at most 10 times, then fault with the real error; an exception from the author's code faults once | `RegistryDispatch.cs:268-326`, `MaxRetries` :373; tests `ATransientUnavailableModLoadIsRetriedOnALaterTick`, `Review4R_PermanentlyUnreadableFieldIsNotRetriedForever` |
| P21 | after a pack reload, a mod's callback edits applied before its own data edits (45 became 60) | recompose appended re-read data batches after code batches | data batches go back in front: data, queued code, recorded callback turns | `registry_live.cpp:1228`; `registry_live_test` `callback_order_survives_reloads` |
| P22 | an old pack's change was lost in the same flush that detected another writer | detection skipped the slot with `old_changed` cleared | mark the slot foreign, keep the pack's change | `store.cpp:519-538`; `registry_store_test` `detection_in_the_flush_of_a_pack_change`; `registry_live_review_test` `pack_write_lost_on_detection` |
| P23 | a commit sent WORLDLOAD to every subscriber, which re-entered commit without bound | live applications reused the world-load publish path | commits and reverts only queue a report and bump the generation; nested commit/revert is refused | `registry_live.cpp:950` `queue_change_report`; tests `commit_does_not_reenter_ready`, `revert_defers_its_snapshot`, `nested_transactions_are_refused` |
| P24 | writes could run off the world thread or into a world being torn down | the world-thread check ran outside the store lock on another variable | decide under the store lock on the variable that gates writes; world reset stops live before clearing the world | `registry_live.cpp:119` `on_live_thread`; test `wrong_world_thread_is_refused_under_lock` |
| P25 | one commit took 539 ms on the world thread | a full-base composition per retained change | prepared base once, touched-entry overlays per change | test `retained_changes_have_bounded_commit_cost` (64 changes over 20k entries under 1 s; limits.md) |
| P26 | data sections of mods Kiln.Host did not load were applied; then hosting clients skipped ordered server mods | the stage read every pack folder; then a side check overrode Kiln.Host's order | with Kiln.Host's order, membership alone decides; the side check is only for the folder-order fallback | `stage.cpp:642-650`; `registry_stage_review_test` `skipped_mod_data_applied`, `hosting_client_keeps_ordered_server_mods` |
| P27 | runtime mods' `OnRegistry` ran on the native loader thread | the ready callback ran dispatch inline | ready only records; dispatch happens on the next host tick; tick-thread asserts | `RegistryDispatch.cs`; test `ForeignReadyWaitsForTheNextHostPreBeforeRunningRuntimeAuthors` |
| P28 | a base cache hit missed an added resource and a changed regmap | the cache key lacked listed keys and the mapping | cache listed types' key digests; the family includes `regmapDigest` and `commonDigest` | `stage.cpp:195-220`; test `cache_misses_added_resource` |
| P29 | a hot reload wiped stage refusals and boot outcomes from registry.json | recompose rebuilt the result from scratch | keep outside-compose refusals and boot outcomes across recompose; run the mapping guard on reloaded batches | `registry_live_review_test` `reload_loses_stage_refusals`, `reload_keeps_boot_outcomes_and_checks_sections` |
| P39 | a mod that only calls `Registry.Change`/`Registry.Read` had its base decoded on the tick thread at first use | the order's `"registry": true` flag came from `MightOverride`, which looks for `RegistryContext` only | `RegistryDeclarations.UsesRegistry`: also a reference to Kiln.Registry's `Registry` type, in the entry assembly or another DLL of the mod's folder; GameStart authorship stays `MightOverride`'s | test `AModThatOnlyCallsRegistryChangeIsARegistryUser` |
| P40 | a flagged start whose stage could not read the base composed the old boot writers' edits over their own patched bytes at first use (x2 twice) | without a base nothing was frozen, and the first use read the live bytes | the first use refuses with the reason (`lazy_error`: restart to use the registry API) instead of decoding unfrozen bytes | test `flagged_stage_without_a_base_refuses_first_use` |
| P41 | the first start of a new build with an old boot writer kept every resource the id ledger read in memory until the first registry use | the ledger and the freeze shared one source, kept whole for the lazy decode | the ledger's source is released; the old writers' resources are frozen in a fresh one, before any boot writer writes | test `ledger_reads_are_not_kept_for_first_use` |
| P42 | a bulk WorldLoad/HotReload/Manual author paid a native status call (store lock) per cell; the code identity hashed every DLL when a mod loaded on the tick thread | the registry context asked for the world token on every cell access; the code identity was hashed when the mod loaded | the world check runs once per tick step (`CurrentStep`); the code identity is hashed at the first `Identity` read and each DLL's hash is kept by path, size and write time | tests `AWorldBoundContextChecksItsWorldOncePerStep`, `TheCodeIdentityIsHashedAtFirstUseAndCached` |
| P43 | (constraint, not fixed) a first-use decode restores only store slots to the game's bytes | `PristineView` takes back what the store wrote; a resident write outside the store (newcontent's crafting-menu header writes) stays | never map a registry field onto memory Kiln writes outside the store; such a field needs its own original copy first | review4 recheck nit |
| P44 | (kept, lane decision for review) one failed game memory write takes back the whole change for that world, also in an entry-point pass | a write that fails after every retry is rolled back with its change, so the change is all there or not there (`Unverified` if the take-back fails); entry-point passes refuse per entry only at binding and evaluation (DECISIONS NS6) | keep: a per-entry write refusal would leave a change half-written after the store rolled the flush back; the failure is charged to the change that owns the slot only (P17) | review4 recheck nit, registries README "Refusals per entry" |

### Honesty and privacy

| # | Symptom | Cause | Fix | Where |
|---|---|---|---|---|
| P30 | registry.json reported 67 removals; the game removed nothing | entry add/remove composed but no back-end applied it | refuse what no back-end applies, per edit, with a reason; never report an unapplied change as done (ruling 2026-11-21 5:47 PM). Slice 5 lifts this for item and recipe add/remove and loot remove only | `registry/unmapped.h` `refuse_unmapped_edits` |
| P31 | a live raw refusal repeated the mod's raw member path in the Conflicts view; reasons carried process addresses | refusal text was copied verbatim | refusals carry `registry/id/field` plus `raw: true`; the view hides raw paths; write failures name Kiln places | `report.cpp:101/131/143`; `launcher/src/registry/report.ts:52-60` `redactRaw`; `registry_live.cpp:1014` `places_of`; tests `live_raw_refusals_keep_place_metadata`, `raw_evaluation_refusal_is_marked_but_unknown_kiln_field_is_not` |
| P32 | `store_skipped` rows named the game's debug and member names ("Material_T1_Wood_Logs.maxStackSize was written by another writer") | older store.cpp text | the report names the Kiln place the slot's bound field had (`registry_live.cpp` `skipped_text`: "items[game:material_t1_wood_logs].maxStack was written by another writer ..."), also for a revert after the field was unbound; `StoreSkipped::what` keeps the game names for diagnostics only. A new registry's messages name Kiln places the same way | review4 nits; closed in registry-hygiene (`registry_live_test` `another_writer_in_the_report`, `skipped_revert_names_the_kiln_place`) |
| P45 | live refusals named the game's member ("items[game:x].maxStack: the game member maxStackSize is not writable on this game build", "\"legendary\" is not a value of uiRarity", "300 does not fit maxStackSize (...)") | the live binder's mapping checks and refl.cpp's `encode` texts were copied into the reason | refusals name the Kiln field and entry ("the game's value of this field is not writable on this game build", "is not a value of loot.fixedRarity", "does not fit the game's storage of this field", "this game build's items have no perk slots Kiln can write"); the regmap path goes to kiln.log at debug level (`refuse_mapping`, `encode_kiln`). The words keep the phrases the proof producer reads as a disagreement ("is missing on this game build", "is not writable on this game build", " is not a value of "). A raw member (the Advanced tier) keeps the path the mod named. A new binder names Kiln places the same way | `registry_live.cpp` `refuse_mapping`, `encode_kiln`, `bind_perks`; test `live_refusals_name_kiln_fields_never_game_members` (registry-fixes, its review) |
| P47 | a live enum value the build's game lacks was refused "\"legendary\" is not a value of Legendary on this game build": the game's name of the value, not the Kiln field (P45) | `to_game` looked the value up into a local `name` that shadowed the Kiln field's name its refusal meant | the local is `game`; the refusal names the field ("is not a value of items.rarity") | `registry_live.cpp` `to_game`; `registry_live_test` `enum_refusal_names_the_kiln_field` |

Regmap reasons are player-facing (P31's rule for records): `regmap_check.py:324-330`. A reason must say what is not
known in Kiln words, without nested parentheses (kiln.content wraps it in parentheses).

## 4. Entry add and remove

Data packs and C# use the same entry operations. C# authoring starts in `RegistryStage.Add` and uses the typed
`AddLike` and `Remove` extensions in `managed/services/Kiln.Registry/RegistryEntryOperations.cs`. The existing
registry tables carry the entry requests; a collection with no entry backend refuses the edit with a reason.

| Topic | Contract |
|---|---|
| Scope | add: items, recipes; remove: items, recipes, loot. Loot add and drops/balance add/remove stay refused with reasons |
| When | entry operations are GameStart-only; later entry points return RestartRequired |
| New ids | `<mod>:<name>` in the bound mod's namespace; GUID from `model::added_guid` (UUIDv5), game numeric id derived from it (zero or collision refused, never another id picked); an explicit `like` that ends in a present game entry |
| Definitions | through kiln.content's existing definition paths (`nc_define`/newcontent) under per-mod internal owners |
| Remove | hide, never delete: the record and lookup stay so owned copies keep working; removed from loot, drops and recipe lists |
| Drop slots | removal makes stack slots inert (spawnProbability 0 and weight 0 layers), slots and indexes kept, reported disabled; gated on proven drops rows on the running build, otherwise the item removal is refused whole ("drop tables not proven on this build"); a group that would become all zero is refused; a later live edit of a disabled slot conflicts and the removal wins while active (ruling 4:35 PM) |
| Group spans | group spans are copied `BaseModel` metadata (`entryMetadata: 1` in the cache), not a filtered array; physical drop arrays never shrink |
| Dependencies | refusal closes to a fixed point: a new entry referencing a refused/removed/missing entry is refused whole, and that propagates; existing recipes needing a hidden item are hidden with the reason, never made input-free |
| Persistence | `MissingContent` placeholders keep stable metadata, the flattened base and effective fields; the entry structure is pinned in `kiln/data/registry/entry-compat.json` (per mod, entry and registry; cross-process lock, atomic write, a corrupt row fails closed for that entry); a reused id with another structural base needs a new id |
| Manual | a Manual draft with any entry-operation refusal commits nothing (ruling 8:40 PM); its field-only refusals stay as in P19 |

Keep the Add-stage values separate from the values after Alter and Final. The clone starts with inherited values and
its Add overrides; later layers apply once, and removing a layer restores the Add baseline. Save the effective values
for placeholders, not the constructor baseline. Test with a relative edit and with a reload that removes a layer.
Replay saved references even when no registry mod is installed: that path can need stable IDs on an otherwise lazy
start. Recheck group viability when later field edits touch a group with disabled drop slots, not just when hiding it.

For the next registry: decide early whether entries can be added or removed at all. If yes, the registry needs a
definition path in kiln.content (clone from a `like`), a hide path that keeps records, an implicit dependency rule to
the registries that reference it, and its compat pins.

## 5. Definition of done

A new registry is done when all of this holds, with the counts in the report.

**Green:**
- `registrygen.py --check`, `regmap_check.py` (and `--exe` against the current server and client exes), Python tests.
- Every native registry suite in 2.13, including new red-first cases for each live field (write, read, revert, foreign
  write, per-entry refusal) and each pitfall row that applies to the registry's shapes.
- Managed suites (Kiln.Registry, Kiln.Host, Kiln.Contract, Kiln.TestKit, Kiln.Analyzers), `gen-apis`, PublicAPI
  Unshipped only; `cargo test` in runner/kilnctl if it changed; launcher units if report.ts changed; `abigen --check` if
  a header changed; the Windows DLL build; `drift.py` clean for touched docs.
- A compat lab run (`tools/registry-compat/session.sh`) if the registry has live fields: old packs alone, registry over
  old packs, reload, section removed with no residue, byte comparison against a Kiln-off boot.

**The proof shows:**
- A committed server record for the current build from a fresh lab: `verified`, `provenBy: lab`, `regmapDigest` equal to
  the tree's, `0 changed`.
- Every `ok` row has a passing sample: live fields a real write, read back, raw read, revert and equal resource bytes;
  boot fields a write and read back on a copy; read-only fields a read. Each storage kind the build has is proven
  (`kinds` in the record; entries not in the world counted, not dropped).
- A client record mirrored from that server record.

**What stays unproven, and how it is reported:**
- Each unproven row carries its reason in Kiln words in the record's `reasons` (or regmap/common's `reason`); an edit of
  it is refused "Unproven: <field> is not proven for <build> (<reason>); this edit was not applied"; its capability row
  is unavailable ("unverified mapping on this game build"); the generated page's Mapping column shows it.
- Release notes list the fields that are off on the shipping build (for example drop-table fields while no lab can load
  a drop table).
- The report lists what is not verified in a real game client, one line each (no guided client checks).
