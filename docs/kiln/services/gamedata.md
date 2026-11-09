# kiln.gamedata: spells, abilities, creatures and their attacks as data

Change the game's own ability and creature data at run time, the way kiln.content changes items: templates
(creatures, projectiles, explosions), enemy arsenals (attacks and behaviour rules), action sequences (cast timing,
costs, cast locks), buffs, perks, skill-tree nodes and the config values items pass to Impact programs (mana cost,
damage, durations). Every change is a layer of the mod that made it: it comes off when the mod unloads, is applied
again at the next world load, and reaches Kiln clients through the content sync. Template **clones** add new
templates (an "Elite Vukah"), sequences, perks and buffs with their own GUID (perks and buffs with their own names
per language). Evidence and layouts: `docs/re/gamedata.md`; research:
`docs/re/abilities.md`, `docs/re/creatures.md`.

| Layer | Where |
|---|---|
| Symbols | none for tweaks (the game's reflection + the `assets.resources` feature / kiln.assets); clones: `sigdb/symbols/{server,client}.d/gamedata.json` (feature `gamedata.template_clones`; `gamedata.buff_lookup`, optional: the game's getBuffType for the buff self-check and the BuffRegistry offset check) |
| C table | `native/core/abi/services/kiln_gamedata.h` (`kiln.gamedata` v1, published by kiln-content.dll) |
| Native | `native/services/content/gamedata*.cpp` (in `kiln/services/kiln-content.dll`, sharing kiln.content's store) |
| Managed | `managed/services/Kiln.Content/GameData.cs` (`Content.Templates`, `Arsenals`, `Sequences`, `Buffs`, `Perks`, `Skills`, `ItemData`, `item.Config(...)`, `Content.RegisterSection`) |
| Test | `mods/tests/gamedata-test/` (PASS / FAIL lines) + `Observer/` (logs value changes) + `Clones/` (mod `gamedata-clones`: perk and buff clones; disable it for the placeholders) |
| Samples | `mods/samples/SpellTweaks/` (Fireball I and the double jump), `mods/samples/CreatureTweaks/` (every Vukah, an Elite Vukah clone), `mods/samples/NewItems/` (`content/frostbite.json`: a perk and a buff clone with texts, on Glacier) |

## Data packs
The sections sit in the same `content/*.json` files as kiln.content's `items` / `recipes` (same discovery, load
order, conflicts, reload and revert rules: docs/kiln/services/content.md):

```json
{ "format": 1,
  "items": {
    "Ammo_T1_Spell_Fire_Ball_lvl10": { "impactValues.ManaCost": 30 },
    "Ability_DoubleJump": { "impactValues.StaminaCost": 5 } },
  "templates": {
    "Explosion_Player_Spell_Fireball_1": {
      "ColliderResourceComponent.colliders[0].dataArray[0].shape.radius": { "mul": 1.5 },
      "ImpactConfiguration.configValues.PushBackStrength": "Big" },
    "Enemy_Vukah_*": { "@where": { "Faction.faction": "Vukah" },
                       "Enemy01BehaviorState.combatCommitmentSetup.view.range": { "mul": 1.5 } },
    "add": [ { "guid": "6b1e0c52-93a4-4f7e-b2d8-1c5a7e9f3d40", "name": "Kiln_Elite_Vukah", "base": "Enemy_Vukah_Melee",
               "fields": { "Level.dataStorage[0]": 20, "ScaleComponent.uniformScale": 1.4,
                           "Enemy01BehaviorState.combatCommitmentSetup.view.range": 36 } } ] },
  "sequences": {
    "Attack_Player_Staff_FixedAnim_Fireball_fast": { "events[SpawnEntityEvent#0].time": 0.45,
                                                     "events[PayUsageCost#0].time": 0.45 } },
  "arsenals": { "template:Enemy_Vukah_Melee": { "attacks[0].description.maxRange": 3.5,
                                                "attacks[*].description.selectionChance": { "mul": 0.5 } } },
  "buffs":  { "0xcdbe9c80": { "defaultLifeTime": 10 } },
  "perks":  { "Perk_Damage_Ice": { "damageModifier.distribution.magicalIceDamage": { "mul": 2 } } },
  "skills": { "item:Ability_DoubleJump": { "costs": 1 } } }
```

### Sections
| Section | Entries (selector keys) | Root struct | Count (1285601) |
|---|---|---|---|
| `templates` | template name (`Enemy_Vukah_Melee`), GUID, `0x` id (FNV-1a of the GUID), glob | `keen::ecs::TemplateResource`; paths start with a component type name | 12,269 |
| `arsenals` | arsenal GUID, `template:<template name>` (the template's `EnemyArsenalReference.arsenal`) | `keen::enemy::EnemyArsenalResource` (`guid`, `attacks[]`, `behaviors[]`) | 201 |
| `sequences` | sub-sequence name (`Attack_Player_Staff_FixedAnim_Fireball_fast`), sequence GUID (every sub-sequence of it), `<guid>#k`, glob | `keen::actor::ActorSubSequenceResource` (`events`, `length`, `loop`, `mask`, ...) | 2,162 sub-sequences of 2,157 sequences |
| `buffs` | `0x` buff type id (`buff_<id>` as name) | `keen::BuffType` (`defaultLifeTime`, `slot`, `applyType`, `impact`, `impactValues`, `uiValues`, ...) | 274 |
| `perks` | debug name (`Perk_Damage_Ice`), GUID | `keen::Perk` (`impactValues`, `damageModifier`, `perkArmorSetup`, `uiValues`, ...) | 365 |
| `skills` | `0x` node id (`skill_<id>`), `item:<ability item>` (the node that unlocks it) | `keen::SkillTreeNode` (`costs`, `maxLevel`, `configValues`, `uiPosition`, ...) | 243 |
| `items` (kiln.content's section) | as in kiln.content | `keen::ItemInfo`: the paths kiln.content's flat schema lacks: configs, lists | 4,213 |

Other services add sections of their own (`creatures`: kiln.creatures' variants and spawn rules); see "Pack sections
of other services".
An enemy's sight is `combatCommitmentSetup.view.range` (the example's 1.5 makes a Vukah's 18 m by day 27 m), not `aggroSetup.viewRange`,
which only sizes the search for candidates; see [creatures.md](creatures.md), "Sight and aggro".

### Paths
- **The game's reflection**, read at run time: dotted field names (`combatCommitmentSetup.view.range`), `[i]` for static arrays
  and BlobArrays, struct base fields included. Every leaf with its value: `entry.Fields()`, `GetJson(path)`, the dump.
- **Templates** start with a component's type name: `Enemy01BehaviorState.…`, `LifeTime.lifeTime`,
  `ColliderResourceComponent.colliders[0].dataArray[0].shape.radius`; `Name#1` for the second component of a type. The
  template's own fields (`family`, `farCulling`, ...) work too.
- **Variant lists** (sequence events, collider shapes, arsenal constraints and actions): `[i]`, `[Type#k]` (the k-th
  element of type `Type`, counted in the list's current order), `[Type]` = `#0`; `[<guid>]` picks the element whose
  `guid` field is that GUID (arsenal attacks); `[*]` = every element (fans out).
- **Config values** (`keen::impact::Configurations`: `impactValues`, `configValues`, `ImpactConfiguration.configValues`,
  sequence events' `impactValues`): `.<alias>` or `.0x<config id>`, then optionally a field of the config
  (`.scaleFactor`, `.function`); without one, its value. Aliases:
  - C++-read: `ManaCost` (`0xaf236ece`), `StaminaCost` (`0x727d7f78`), `SpecialAbilityCost` (`0x2de5f1f5`),
    `FuelAmount`, `FuelCharges`, `UsedItem`;
  - every config-bound variable name of the 571 Impact programs (`Damage`, `BuffType`, `Duration`, `NoiseRange`,
    `PushBackStrength`, `SpawnTemplate`, `hitReactionType`, ...): 289 ids, 459 names. A name used for several ids
    (`Speed`) is ambiguous where none of them is present: use the `0x` id. `Content.ConfigVocabulary()` /
    `configs()` list them; programs kiln.assets serves later (tools/impact) are picked up when their count changes.
  - a config a program declares by string (tools/impact `config "mymod.Radius"`): its id is FNV-1a-32 of the string,
    and names with dots go in brackets: `"impactValues[impactsample.HealPerTick]": 4`.
  - **Adding a config**: a config the struct does not have yet (the Fireball explosion has no `PushBackStrength`, so
    its program uses its default Small) is added: the list is re-pointed at a longer copy with an element built from
    an existing config of that id elsewhere in the game data (for an id no game data sets: one of the same program
    variable type, given the new id). Its value type is that config's (`PushBackForce` enum names, numbers, template
    names, ...).
- **Read-only**: identity fields (`guid`, `configId`, `configGuid`, `objectId`, `buffTypeId`, `resourceId`, `itemId`,
  `programId`); BlobStrings (names). References (`TemplateReference`, `ObjectReference<…>`) are writable when Kiln can
  check the target: a loaded resource of that type, by GUID or by name (`"SpawnTemplate": "Explosion_Player_Spell_IceBolt_1"`),
  including resources kiln.assets serves (new Impact programs, clones). Program references (`impact` of a buff, perk,
  skill node: `ImpactProgramReference`) take a loaded ImpactProgram's GUID; sequence references take a sequence's
  GUID or sub-sequence name, and a sibling `sequenceId` follows (FNV-1a of the GUID: `"sequences[0].sequence":
  "My_Cast"` on an item rebinds its trigger). A VFX reference (`VfxComponentResource.vfx`, a sequence's `VfxNotifierEvent`) takes a GUID and is not
  checked on a server, which has no VFX; a client without kiln.vfx keeps only the VFX its game has, and a mod's becomes `"none"`. Other references stay read-only.
- **Lists of rows**: `uiValues`, and a buff's `whileApplied` and `onCreate`, take a JSON list in place of the game's: `"whileApplied": [ { "slot": "Chest", "vfx": "<guid>",
  "offset.orientationOffset.w": 1 } ]`. A field a row does not name is zero, so a rotation has to say its `w`.

### Values
As in kiln.content: numbers, `true` / `false`, enum names (`"Big"`), flag lists (`["Magic", "AoE"]`, also
`["+Run", "-FleeHome"]` relative to the original), times in seconds, `"0x…"`, GUIDs / names for references, and ops on
the game's original value: `{"mul": 1.5}`, `{"add": 2}`, `{"min": 1, "max": 9}`, `raiseOnly` / `lowerOnly`,
`{"set": v}`, `{"reset": true}` (this owner's layer off). An object for a struct sets its fields
(`"damageModifier.distribution": {"magicalIceDamage": 1}`). `@where` takes conditions on the **original** values of
any path (`"Faction.faction": "Vukah"`, `"LifeTime.lifeTime": ">2"`, a list = any of) and `name` (a glob).

### What follows a change
- **Tooltips**: a config with a loca tag next to a `uiValues` list (items, perks, buffs, skill nodes) has a tooltip
  value with `locaId` = FNV-1a(locaTag): it follows, scaled by the config's `valueFormat` (Normal ×1, Percentage ×100,
  Duration ms ×1000, Float ×100). Fireball I: ManaCost 30 → `uiValues[0].value` 30; the cast time stays 2500.
  The row is found by its `locaId`, never by its place: a list of rows in another order than the game's keeps each value. In a rule, a
  list of rows applies before the rule's configs, so the tooltip follows on the list the entry keeps. The content sync sends the config and
  a list as a whole, never a followed row or a value set in a list the entry no longer has; the receiver's config brings the row.
- **Event order**: the game walks a sub-sequence's events in order and stops at the first one at or after the current
  time window (server 0x34620), so events must stay sorted by time. After a time change Kiln points the events list
  at a sorted copy (an internal layer), recomputed whenever layers change. `[Type#k]` counts in that current order.

## When a change takes effect, and for whom
| Data | Read by the game | Takes effect | Vanilla client | Kiln client |
|---|---|---|---|---|
| template-only components (`Enemy01BehaviorState`, `EnemyArsenalReference`, `Aggro`, `EnemyScaling`, `ColliderResourceComponent`, `ImpactConfiguration`, ...) | shared: every entity of the template reads the template data through its template slot | **at once**, existing and new entities (lab: a spawned Vukah's GetTemplateComponent is the data Kiln changed) | server-only data: plays along | same values (sync) |
| dynamic components' template values (`Level`, `ScaleComponent`, `LifeTime`, `Faction`, `Movement`, ...) | copied into each entity at spawn | **next spawn** (lab: a Fireball projectile spawned after the change had LifeTime 8 s) | replicated state (level, scale, faction) | same |
| config values on items, templates, sequence events, buffs, perks, skills | copied into each Impact instance when it starts | next cast / projectile / buff | server decides; tooltips vanilla | tooltips follow (sync) |
| costs (`ManaCost`, `StaminaCost`, ... on the item or the ability's cost item) | at every payment | next payment | pays the server's cost; tooltip / prediction vanilla [client checklist] | right |
| sequence event times, lengths | while the sequence plays | next playback | server acts at the new time, the client plays VFX/animation at the vanilla time | in step (sync) |
| arsenal attacks and rules | by the AI while choosing | next decision | server-only | same |
| skill node costs | at unlock | next unlock | the client's skill-tree UI checks its own costs [client checklist] | right |

### Global edits vs one creature
A `templates` / `arsenals` change is **global**: template data is one shared, read-only copy per template, so every
entity of that template, now and later, sees it. Per-creature changes are kiln.creatures': its per-entity setters
(level, faction, scale, resistances, ...) and its private AI-settings copies (one entity's template slot pointed at a
copy). An entity with a private copy keeps reading its copy; kiln.creatures re-copies the template's current bytes on
`ContentReadyEvent` (and keeps its overrides on top), so global edits reach it after the next application. A variant
that needs its own permanent numbers and its own name is a **clone**.

## Clones (`"add"`)
```json
"templates": { "add": [ { "guid": "6b1e0c52-93a4-4f7e-b2d8-1c5a7e9f3d40", "name": "Kiln_Elite_Vukah",
                          "base": "Enemy_Vukah_Melee", "fields": { "Level.dataStorage[0]": 20 } } ] }
```
- A clone starts from the game's original, not from what other mods changed; a mod that wants another mod's values states them. A clone of a clone takes the definitions before it, oldest first, then its own.
- A new template with a fixed GUID (saves and clients find it again; never change a released GUID), a name, a base
  template and field values (any templates path, configs included; adds allowed). Kiln copies the base template's
  resource, serves it through kiln.assets as a new resource, and registers it in the running world with the game's
  own template registration (the same call the game makes for every template at world start), then sets the fields.
  kiln.spawn, kiln.creatures and the game spawn it by name like any template (`Spawn.Entity("Kiln_Elite_Vukah", pos)`).
- The fields are a layer of an internal owner ("gamedata definitions"): the clone's originals are the base's values, so
  later rules (`"Kiln_Elite_Vukah": {...}`, globs) and mods' code layers work on clones and win over the definition.
- **Lifecycle**: registered at world load (before the save is read) or at once when defined later; it stays
  registered until the process ends (a template cannot be removed); a pack definition that goes away is listed as
  `orphaned`. Redefining a GUID updates its fields; its base and name cannot change. Room: the template table holds
  13,000 (12,269 game templates), Kiln refuses clones beyond that.
- **Clients**: a clone is a new template, so clients need its definition. The content sync sends every definition as the
  section's `"add"` list and a Kiln client registers it in its own world before the values. A mod with clones declares
  `"clients": "kiln"` in its manifest; servers then refuse vanilla clients unless `modSync` is `off`.
  Where one is let in, entities of the clone are kept from it: it would crash on them (docs/kiln/network.md "Clients without Kiln").
  A Kiln client gets them after its content ack, so the definition is in place first.
- **Sequences**: `"sequences": {"add": [{"guid", "name", "base": <sub-sequence name or sequence GUID>, "fields":
  {...}}]}` copies the base ActorSequenceResource (all its sub-sequences; fields apply to sub-sequence 0, the others
  are `<name>#k`), serves it through kiln.assets and inserts it into the world's ActionSequenceRegistry (key
  FNV-1a(GUID), the id items and NetworkActor use). An item uses it through `sequences[i].sequence` (its
  `sequenceId` follows): a new spell = an ammo item clone (kiln.content) + a cast sequence clone (here) + a
  projectile template clone. `"add"` lists are registered before the pack's other sections, so rules of the same
  pack may name the clone.
- **A template's base by item**: `"base": "item:<name>"` is the template that item places (`equipment.placedEntity`),
  found when the items are there, a mod's new item included.
- **Components the base lacks**: `"addComponents": ["ComfortProvider"]` gives the clone those components of the game's
  types, zeroed, for its `fields` to fill in. The component list is kept sorted by type name, as the game's are.
- **Components the base has and the clone doesn't**: `"removeComponents": ["SubEntities", "HookShotAnchorSubEntity"]` leaves them out of the
  clone's list (the base's gear, on a creature with a body of its own). A name the base doesn't have is refused, and so are `CurrentTransform`
  and, on an actor, what a puppet needs to live (`Actor`, `ActorInput`, `NetworkActor`, `Velocity`, `LastTransform`, `Faction` and the four
  animation graph components).
- **A list the base has too short**: `"growArrays": {"ColliderResourceComponent.colliders[0].dataArray": 3}` makes that list (a BlobArray of the base's data, named as a field
  path is) at least that long: it moves behind the clone's data, and each new element is a copy of the last (a variant gets an object of its own), so the clone's `fields` can set
  `dataArray[1]` and `dataArray[2]` by index. A list that is long enough stays; one with no element to copy, or no list at that path, is refused by name. A prop block with several
  `Colliders` uses it. A collider's `joltObjects[0].shapeIndexList` must also grow to include each new shape, with indices `0, 1, 2`: `dataArray` alone does not add those boxes
  to the physics object. The block compiler sets both lists, and gives the placed compound shape and the cursor's bounding box different cache hashes.
- **A reference from an item**: a reference field takes `"item:<name>.<path>"`, what that item has in that field:
  `"ModelResource.model": "item:Prop_Production_T1_Drying_Rack.iconModel"`. A dedicated server has no models or
  sounds, so it carries a model's or a sound's GUID for its clients unchecked.
- Perks and buffs: see "Perk and buff clones" below.

## Perk and buff clones
```json
{ "format": 1,
  "buffs": { "add": [ { "guid": "c7a1e5d2-49b6-4f80-a3d1-2e6b9f0c4a58", "name": "Kiln_Sample_Frostbite", "base": "0x5f425faf",
                        "displayName": "Frostbite", "description": "Bitter cold gnaws at you.",
                        "texts": { "de": { "name": "Frostbiss", "description": "Beißende Kälte nagt an dir." } },
                        "fields": { "defaultLifeTime": 8 } } ] },
  "perks": { "add": [ { "guid": "3b8f0d6e-15c2-4a97-8e4b-d1f6a2c97e03", "name": "Kiln_Sample_FrostbiteEdge",
                        "base": "Perk_OnDamage_Dealt_Debuff_Poisoned_T2", "displayName": "Frostbite Edge",
                        "description": "Hits inflict Frostbite.",
                        "fields": { "impactValues.BuffType": "Kiln_Sample_Frostbite",
                                    "impact": "c40bd1c7-48dd-44a5-8d6b-8222e62acb84" } } ] } }
```
- The same `"add"` rules as templates: a fixed GUID (never change a released one), a `name`, a `base` of the same
  section (perks: debug name, GUID, `0x` id; buffs: `buff_<id>` / `0x<id>`, a GUID, or a clone's name), `fields` as
  layers of the "gamedata definitions" owner (any path of the section; mods' rules and code sit on top; rules of the
  same pack may name the clone). The id is FNV-1a of the GUID, as the game pairs its own: `PerkId` (`perkIds[i]`, the
  PerkRegistry's key) and `buffTypeId` (a `BuffTypeReference`'s GUID hashes to it).
- **Texts** (perks and buffs only): `displayName` / `description` (every language) and `texts` per language key
  (`"en"`, `"de"`, `"pt-BR"`, `"*"`; a string = the name). The clone gets new loca tags (GUIDs derived from its own);
  kiln.content's loca hooks answer them on clients, by the game's current language (fallback: the primary language,
  then `en`, then `*`). Without texts the clone shows its base's name. Icons stay the base's (`icon` is a UiTexture).
- **Perks**: Kiln copies the base `keen::Perk` resource (debug name = the clone's name), serves it through kiln.assets
  as a new resource and inserts it into each world's PerkRegistry id map (the map the game's PerkRegistryFind reads):
  kiln.log "perk Kiln_Sample_FrostbiteEdge (…, id 0x4b76be40, clone of Perk_OnDamage_Dealt_Debuff_Poisoned_T2)
  registered in the server world's perk registry (366 perks)".
- **A perk clone runs its base's program.** Read that program before choosing a base: `python3 tools/impact/impact.py dis <its impact GUID>`.
  The only on-hit program in the game (`4abf7534-…`, the seven `Perk_OnDamage_Dealt_Debuff_Poisoned_T*`) checks the weapon category
  and lets **daggers** through, so a clone of it on a sword never fires; it does read `BuffType` and `ChanceValue` from the clone.
  NewItems' Frostbite Edge therefore runs its own program (`mods/samples/NewItems/impact/frostbite_edge.kimp`, served from `assets/`, no weapon
  check), set by `"impact"`. mods/tests/perkhit-test proves all of it through the game's hit path.
- **A perk runs only once its enhancement is unlocked.** `equipment_impacts` (`0x145570`) starts a perk's program for an item in an equipment
  slot for slot indices below `PerkContainerNew.unlockedPerks`. A new item has 0, `Items.Give` with an Epic `Gear` too: the tooltip lists the
  enhancements, but nothing runs until the game's upgrade (`UnlockPerkEvent`) unlocks them. Frostbite Edge is in slot 2: three upgrades.
  It starts and stops with the item in hand: a hotbar switch ends the perk's entity, switching back starts a new one (lab).
- **Buffs**: buffs are records of the one buff collection, which the game reads through each world's BuffRegistry with
  no runtime map to add to. With the first buff clone Kiln serves the collection from its own copy: the game's records
  at the same indices, then the clones, and a rebuilt map; each world's BuffRegistry is pointed at it. The game's
  records stay where every layer writes (buff tweaks, config adds, tooltips work as before); Kiln's copy follows them
  after each change. Room: 256 buff clones per process. kiln.log "the server world's BuffRegistry (… +0x20) reads
  Kiln's buff collection: 276 buffs (2 clones)", and the game's own lookup as a self-check: "the game's
  getBuffType(0x26317bd2) in the server world = … (the clone's record); a game buff 0xcdbe9c80 = … (Kiln's copy)".
- **Referencing them**:
  - an item's perk slot: code `Content.ItemData.Get(item).Set("perkReferences[2]", "Kiln_Sample_FrostbiteEdge")`
    (`perkIds[2]` follows: FNV-1a of the GUID); new items: kitbash `"perks": [null, null, "<perk clone GUID>"]`
    (kiln.content). A data-pack rule on a **game** item's perk slot needs the references opt-in (`"@refs": true`), as
    every reference of a game item. New items are registered before the pack's clones: set their slots in `OnReady`
    (NewItems does);
  - buffs from configs: `impactValues.BuffType` of perks and items, `configValues.<id>` of skill nodes,
    `ImpactConfiguration.configValues.BuffType` of templates, `ApplyBuffEvent.buffTypeOverride` of sequence events,
    `onTimeout.followUp` of buffs. A reference takes a clone's name or GUID, or a game buff's GUID (also its `buff_<id>`
    name when some game data references it: Kiln learns the GUIDs of the game's buffs from their references). Where an
    alias names several config ids (`BuffType` on a skill node that has none of them), use the `0x` id
    (`configValues.0x5adb2644`);
  - abilities: `Abilities.ApplyBuff(target, Content.Buffs.Get("Kiln_Sample_Frostbite")!.Guid)` (kiln.abilities' run of
    the game's applyBuff), or a `BuffType` config of `Abilities.RunImpact`.
- **Lifecycle** as for templates: registered at world load (before the save is read), stays registered until the
  process ends, a pack definition that goes away is `orphaned`. Saved buffs and saved items keep the ids, so they
  resolve again after a restart.
- **Placeholders** (like kiln.content's missing items): every perk and buff clone the server world registered is
  recorded in the world's WorldData (Kiln's bundle `kiln.gamedata`, key `missing-gamedata`, written with every world
  save). At world load (before mods' `OnReady` and the save), a recorded clone that no loaded mod defines gets a
  placeholder: same GUID and id, the recorded base made inert (no program, no configs, no tooltip values, no VFX, no
  follow-up), named `[Missing: <mod>:<name>]`. kiln.log "missing gamedata: 2 perk / buff clone(s) of mods that are not
  loaded are kept as placeholders (saved references stay): buffs gamedata-clones:Kiln_Test_Frostbite (0x9887fff3), …".
  When the mod comes back its definition takes the GUID over ("… is back (pack gamedata-clones): it takes its GUID …
  back from the placeholder"; at the next start no placeholder is made).
- **Clients**: the content sync sends the definitions as the sections' `"add"` lists (texts included) and Kiln clients
  register them in their own world before the values; item references kiln.gamedata changed (perk slots) travel in
  an `"itemdata"` section (kiln.gamedata's item paths: the slot's id goes with it). A clone of this game's own mod is
  kept (state `local`). Declare `"clients": "kiln"` (servers then refuse vanilla clients unless `modSync` is `off`): a vanilla client
  knows neither the ids nor the texts.

### `itemdata` (pack section)
`"itemdata": { "<item>": { "<path>": value } }`: items through kiln.gamedata's paths (references, configs, lists),
the paths `Content.ItemData` takes; for a game item's references `"@refs": true` in the rule. The content sync uses it
for item references changed through kiln.gamedata; mods normally use `items`.

## C#
```csharp
using Kiln;

Content.OnReady(_ =>
{
    // items: config values (tooltips follow)
    Content.Items.Get("Ammo_T1_Spell_Fire_Ball_lvl10")!.Config("ManaCost").Set(30);
    // templates
    var expl = Content.Templates.Get("Explosion_Player_Spell_Fireball_1")!;
    expl.Set("ColliderResourceComponent.colliders[0].dataArray[0].shape.radius", 3.9);
    expl.Config("PushBackStrength").Set("Big");                 // added: the template had none
    // sequences
    var cast = Content.Sequences.Get("Attack_Player_Staff_FixedAnim_Fireball_fast")!;
    cast.Event("SpawnEntityEvent").Time = TimeSpan.FromSeconds(0.45);
    // arsenals, skills, buffs, perks
    Content.Arsenals.Get("template:Enemy_Vukah_Melee")!.SetJson("attacks[*].description.maxRange", "{\"mul\": 1.2}");
    Content.Skills.Get("item:Ability_DoubleJump")!.Set("costs", 1);
    foreach (var f in expl.Fields("ImpactConfiguration")) Log.Info($"{f.Path} = {f.Value}");
});
```
- Registries `Content.Templates`, `Arsenals`, `Sequences`, `Buffs`, `Perks`, `Skills`, `ItemData` (`GameDataRegistry`:
  `Count`, `this[i]`, `Get(key | id | Guid)`, `IsClone(Guid)`, `Dump(key?, path?)`, `Define(JsonObject | GameDataClone)`
  (templates, sequences, perks, buffs: the `"add"` entry from code; tick thread), `Definitions()`);
  `Content.GameData(section)`, `Content.GameDataDefinitions()` (`GameDataDefinition`: `Section`, `Guid`, `Id`, `Name`,
  `Base`, `State`, `Owner`, `Mod`, `IsPlaceholder`, `Error`, `Warnings`, `DefinitionJson`).
  ```csharp
  var frost = Content.Buffs.Define(new GameDataClone { Guid = myGuid, Name = "MyMod_Frost", Base = "0x5f425faf",
      DisplayName = "Frost", Texts = { ["de"] = new("Frost", "Kälte") },
      Fields = { ["defaultLifeTime"] = JsonValue.Create(6) } });
  ```
- `GameDataEntry`: `Id`, `Name`, `Guid`, `Modified`, `IsClone`, `Address`; `Get(path)` / `Original(path)` →
  `ContentValue`; `GetNumber`; `GetJson(path)` (any node: `{path, type, value, original, writable, address}`, an array
  for `[*]`, `{"absent": true}` for a config not there yet); `Set(path, double | long | int | bool | TimeSpan | string |
  Guid)`; `SetJson(path, json)` (any pack value; returns the leaves changed); `Reset(path)`; `Fields(prefix?, limit?)`;
  `Config(alias)` (`GameDataConfig`: `Exists`, `Get`, `Original`, `Set`, `Reset`); `Event(type, k)` (`GameDataEvent`:
  `Time`, `Duration`, `Get`, `SetJson`).
- `item.Config(alias)` / `item.Data()` on kiln.content's item entries.
- Typed paths: every call above that takes a path also takes a field from the generated `GameFields`
  (`entry.Set(GameFields.Templates.LifeTime.LifeTime, TimeSpan.FromSeconds(5))`, modding/typed-fields.md). String paths are checked at
  compile time against the local catalog (modding/analyzers.md, KILN194).
- `Content.GameDataStatus()` (JSON), `Content.ConfigVocabulary()` (JSON), `Content.GameDataAvailable`.
- Rules as in kiln.content: tick thread only; changes belong to the calling mod and come off in its `Retiring`; a bad
  path or value throws `ArgumentException` / `KeyNotFoundException`.

### Pack sections of other services
```csharp
Content.RegisterSection("creatures",
    call => Creatures.ApplyJson(call.Json, call.Owner, call.Source),   // -> ContentSectionResult(applied, errors, warnings)
    (owner, source) => Creatures.RevertJson(owner, source));
```
- `apply` gets every pack document's (and `Content.Apply` call's) value of that top-level key: `ContentSectionCall
  (Section, Json, Owner, Source, Generation, Reload)`. Owners: `pack:<mod>` (data packs), `mod:<mod>` (code), `sync`
  (the content sync's layer on a client).
- `revert(owner, source)`: a pack file is no longer applied at a re-application; `revert(owner, null)`: the owner went
  away (mod unload, `Content.Revert`); `revert("", null)`: the world resets.
- Both run on the tick thread, inside kiln.content's application, in pack order. Sections that arrive before their
  service registers are kept and applied at registration (managed services load after the world's packs); for
  known sections (`creatures` → Kiln.Creatures, `abilities` → Kiln.Abilities) Kiln.Content starts that service's
  module itself when a data-only mod uses the section. Handler exceptions become pack errors. C:
  `register_section(owner, name, apply, revert, user)`; `status()` lists `waiting` sections.

## C (native plugins and services)
```c
const KilnGamedataApi* g = api->service((KilnStr){"kiln.gamedata", 13}, 1);   /* in on_ready */
KilnGdEntry e = { sizeof e };
if (g->find(KILN_GD_TEMPLATES, (KilnStr){"Enemy_Vukah_Melee", 17}, &e) == KILN_OK) {
    const char* v = "{\"mul\": 1.5}";
    g->set_json(owner, KILN_GD_TEMPLATES, e.id, (KilnStr){"Enemy01BehaviorState.combatCommitmentSetup.view.range", 53},
                (KilnStr){v, strlen(v)}, NULL, 0, NULL);
}
```
`status`, `count` / `entry_at` / `find`, `get` / `get_json`, `set` / `set_json` / `reset`, `fields`, `dump`, `configs`,
`register_section` / `unregister_section`, `define` / `definitions`. Every function: the header. `define` takes
`KILN_GD_PERKS` and `KILN_GD_BUFFS` too (the same JSON as an `"add"` entry); `definitions` lists `{section, guid, id,
name, base, owner, mod, source, state, placeholder, worlds, version, definition, texts?, error?, warnings?}`; `status`
has `clones`: `{buffCopy: {active, gameBuffs, clones, room, worlds, syncs, wordsFollowed}, perkMaps, texts,
gameBuffLookup}`.

## Content sync
kiln.content's `export_changes` with `KILN_CONTENT_EXPORT_GAMEDATA` (ContentSync passes it) adds the gamedata
sections: every changed leaf as `"<section>": {"<guid or 0x id>": {"@name": ..., "<path>": value}}`, item config
paths under `items`, added configs as their paths, and the clone definitions as each section's `"add"` list (first, so
the client registers clones before their values). The client applies the document through the same pack code
(`"sync"` owner); `matchServer`'s resets (`{"mul": 1}`) work on every path, added configs included. Lab:
`ContentSync.CheckRoundTrip()` "ok: 27 change(s) exported, 28 re-applied unchanged, 27 put back for a vanilla server".

## For Kiln.Admin
| op | Kiln |
|---|---|
| `gamedata_status {}` | `Content.GameDataStatus()` |
| `template_get {entry, path?}` → `{path, type, value, original, writable}` (no path: the whole template) | `Content.Templates.Get(entry)?.GetJson(path ?? "")` |
| `template_set {entry, path, value}` (value: any pack JSON) | `entry.SetJson(path, value)` (Kiln.Admin's layer; reverted with it) |
| `template_reset {entry, path}` | `entry.Reset(path)` |
| `template_fields {entry, prefix?}` | `entry.Fields(prefix)` |
| `arsenal_get / arsenal_set / arsenal_reset {entry, path, value?}` | `Content.Arsenals` (entry: GUID or `template:<name>`) |
| `sequence_get / sequence_set / sequence_reset {entry, path, value?}` | `Content.Sequences` (entry: sub-sequence name or GUID) |
| `gamedata_get / gamedata_set / gamedata_reset {section, entry, path, value?}` (buffs, perks, skills, items) | `Content.GameData(section)` |
| `gamedata_dump {section, entry?}` | `Content.GameData(section).Dump(entry)` |
| `gamedata_configs {}` | `Content.ConfigVocabulary()` |
| `gamedata_definitions {}` | kiln.gamedata `definitions()` (clones and their state) |

## Test (lab, no player)
```sh
export KILN_OUT=<build dir>
dotnet build mods/tests/gamedata-test/GamedataTest.csproj -c Release -o $KILN_OUT/mods/gamedata-test --artifacts-path $KILN_OUT/dotnet
dotnet build mods/tests/gamedata-test/Observer/GamedataObserver.csproj -c Release -o $KILN_OUT/mods/gamedata-observer --artifacts-path $KILN_OUT/dotnet
tools/lab/lab.sh new gamedata 26100 --save <savegame dir>
tools/deploy-server.sh ~/srv/enshrouded/labs/gamedata --policy any --samples
tools/lab/lab.sh start gamedata     # fresh: 30 PASS (registries, the pack, tooltips, config add, event order, template and
                                    #   sequence clones, an item rebound to the cast clone, code layers, conflict, refusals,
                                    #   live data of a spawned Vukah / projectile / clone, sync round trip)
echo "reload gamedata-test" > ~/srv/enshrouded/labs/gamedata/kiln/control/r.cmd    # 4 PASS: code layers off, pack stayed
echo "unload gamedata-test" > …/u.cmd; echo "load gamedata-test" > …/l.cmd          # OBSERVE lines: everything back / again
tools/lab/lab.sh stop gamedata; tools/lab/lab.sh start gamedata                       # 3 PASS: pack at world load, code re-applied
```
Delete `kiln/data/gamedata-test/` for a fresh run. The samples conflict with the test's pack; run them apart
(`touch kiln/mods/gamedata-test/.disabled`).

Perk and buff clones: the mod `gamedata-clones` (`mods/tests/gamedata-test/
Clones/`, a data pack + one code reference) is part of the fresh phase (41 PASS in all): the clones by name, GUID and
id, their loca tags, the perk applying the buff clone, an item's perk slot with its id, a skill node's added BuffType
config, the definitions; live: `Abilities.ApplyBuff` of the buff clone on a spawned Vukah ("buff entity … with
BuffNetworkData buffTypeId 0x9887fff3, lifeTime 7 s (the clone's 7 s; the base's 5 s); the Vukah's BuffContainer
lists 0xc0c267ae, 0x9887fff3") and a game buff changed by code read through Kiln's copy ("0xc0c267ae defaultLifeTime
3 -> 9 s applied with lifeTime 9 s"). Then:
```sh
dotnet build mods/tests/gamedata-test/Clones/GamedataClones.csproj -c Release -o $KILN_OUT/mods/gamedata-clones --artifacts-path $KILN_OUT/dotnet
touch kiln/mods/gamedata-clones/.disabled; lab.sh stop; lab.sh start   # 6 PASS: placeholders from world load, inert, same ids
rm kiln/mods/gamedata-clones/.disabled; echo "load gamedata-clones" > kiln/control/l.cmd   # "... is back": the GUIDs taken over
echo "reload gamedata-test" > kiln/control/r.cmd                     # 41 PASS again (the clones restored in place)
```

## In a client (client 26404966)
- **Hosted** (main world, both samples on the client; they are `side: both`): kiln.log "gamedata ready (server world,
  through kiln.assets)", "pack spelltweaks …: 11 change(s), 0 error(s)", "template Kiln_Elite_Vukah … registered in
  the server world: … 12270 of 13000" and a second registration in the hosted client world. The Fireball I tooltip
  showed **30 Mana Cost** (vanilla 53).
- **Joined the dedicated lab** (samples on the server only): "Alder: content state ae272dc5 applied: 27 change(s)"; the
  Fireball I tooltip showed **30** from the sync alone.
- Not verified (synthetic mouse input does not fire the staff in gameplay; spawned creatures, the vanilla Vukah too, did
  not show inside that base): the mana bar per cast, the 0.45 s release, the 3.9 m explosion, the double jump's 5
  stamina, the Elite Vukah's look. `/elitevukah [ally|clear] [dx dz] [template:<name>]` also works from the console
  folder (`kiln/control/console/<x>.cmd`), for offline worlds without chat.

- With kiln.creatures: CreatureTweaks' `creatures` section applied with the clone as a variant's
  base ("pack creaturetweaks/content/creaturetweaks.json: 14 change(s), 0 error(s)"), the natural-spawn swap
  Vukah → Kiln_Elite_Vukah at chance 0.15, and `/creature spawn creaturetweaks_elite` gave "Kiln_Elite_Vukah (Enemy)
  variant creaturetweaks_elite: level 20/105, health 1031/1031, scale 1.4" with view range 36 from the clone's own
  template data.

## In a client (client 26430625, a hosted LOCAL world, NewItems + ModelSample)
- Both worlds of the hosting client: "the server world's BuffRegistry … reads Kiln's buff collection: 275 buffs (1
  clones)", "the client world's BuffRegistry …", the game's getBuffType of the clone in each world = the clone's
  record; "perk Kiln_Sample_FrostbiteEdge … registered in the client world's perk registry (366 perks)".
- Glacier's tooltip (crafting menu, Item info): level-4 enhancement **"+5% Frostbite Edge — Hits inflict Frostbite."**:
  the perk clone with its own texts (the value is the base perk's chance).
- `/frostbite` (console folder): the buff list showed **"Frostbite"** with its countdown and the base's poison VFX: the
  client resolved the clone id from the replicated buff and drew its own name.
- Not covered: German / French texts (needs a language switch), a joined client receiving the clones through the sync.

## Client checklist (needs a joined player)
1. Fireball I with SpellTweaks on the server only: the mana bar drops 30 per cast; the vanilla client's tooltip says 53
   (does its prediction refuse a cast between 30 and 52 mana?); a Kiln client's tooltip says 30.
2. The cast releases the projectile at 0.45 s: on a Kiln client the VFX and sound come with it; on a vanilla client the
   VFX stay at 0.63 s.
3. The explosion damages and pushes an enemy at up to 3.9 m (the VFX stays vanilla size); push-back Big.
4. The double jump costs 5 stamina.
5. CreatureTweaks: Vukahs notice the player from about 27 m; `/elitevukah` spawns a Vukah 1.4x bigger at level 20 that
   notices from 36 m; a vanilla client is refused (clients: kiln), a Kiln client draws the clone.
6. A skill node cost changed on the server only: what the vanilla skill-tree UI allows.
7. NewItems (both sides): Glacier's third perk reads "Frostbite Edge" / "Hits inflict Frostbite." (German: "Frostbiss-
   Klinge"); `/frostbite` (console folder offline) shows the Frostbite debuff in the buff bar with its own name and 8 s. On a hit it needs three
   unlocked enhancements and has 5% (lab test mod: `/perkhit-arm` unlocks them on the server and sets 100%).
8. A joined Kiln client gets server-only perk / buff clones (`clients: kiln`) through the sync: names in tooltips and
   the buff bar; a vanilla client is refused.
