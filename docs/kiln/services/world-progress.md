# World progress: the world's flame, difficulty, spires, shrines, bosses, chests and a character's quests

Typed answers to "how far is this world, and this character?": the flame level, the difficulty, Ancient
Spires, Flame Shrines and Sanctums, Shroud Roots and Elixir Wells, Ancient Obelisks, bosses, survivors, chests, and a
character's quests (the game's and Kiln's), with events when they change. Evidence: [docs/re/world-status.md](../../re/world-status.md).

| Layer | File |
|---|---|
| Managed | `managed/services/Kiln.Quests/WorldProgress*.cs` (`Kiln.WorldProgress`, records, events), `WorldIndex.cs`, `WorldCatalog.cs`, `GameDataReader.cs` (the game's data), `QuestLog.cs` (`Quests.Of`) |
| Built on | kiln.knowledge ([knowledge.md](knowledge.md)), kiln.world ([world.md](world.md)), kiln.settings ([settings.md](settings.md)), kiln.assets (resource reads), Kiln.Combat (deaths) |
| Admin | `mods/Kiln.Admin/Ops.WorldStatus.cs`: `world_status`, `world_status_player`, `world_chests` |
| Catalog | `kiln catalog` sections `spires`, `shrines`, `shroudRoots`, `obelisks`, `bosses` ([catalogs.md](../modding/catalogs.md)) |
| Tests | `managed/tests/Kiln.Quests.Tests` (no game), `mods/tests/worldstatus-test` (lab) |

`SetFlameLevel` changes the Mother Flame. To change other progress use [Knowledge](knowledge.md) (grant, revoke, set:
their rules apply) and [Quests](quests.md).

## What the game keeps, and where
| Fact | Per | Read as |
|---|---|---|
| Flame level, altars, water sources | world | `WorldProgress.Flame` (the world's progress entity) |
| Difficulty | world | `WorldProgress.Difficulty` (preset + every game setting, kiln.settings) |
| Spire unlocked / beacon lit | world (a twin per character in personal progression) | `spire.Unlocked`, `spire.BeaconLit`, `spire.UnlockedFor(player)` |
| Spire top reached | character | `spire.TopReachedBy(player)` |
| Flame Shrine / Sanctum found | character | `shrine.FoundBy(player)` (the game keeps no other state for a shrine: finding it is what its achievement counts) |
| Shroud Root destroyed, Elixir Well cleared | character | `root.DestroyedBy(player)`, `WorldProgress.ElixirWells` (a well's root gives 3 skill points; one Cold Heights well has three roots of 1) |
| Obelisk read | character | `obelisk.ReadBy(player)` |
| Boss killed | world (most), or character | `boss.Killed`, `boss.KilledBy(player)`; 13 of the 30 placed bosses have a record (`boss.Tracked`) |
| Survivor rescued | world | `survivor.Rescued` |
| Chest opened / looted | world, only while its area is loaded | `WorldProgress.Chest(at)` |
| Quest status and step | character | `Quests.Of(player)` |

Every fact is a [`ProgressFact`](#facts) evaluated with the game's own evaluator, so personal progression and the game's
rule for world and player objects apply as in the game's journal. A character fact asked of the world reads `null`.

## C#
Reference Kiln.Quests as other services: `<ProjectReference Include=".../managed/services/Kiln.Quests/Kiln.Quests.csproj" Private="false" ExcludeAssets="runtime" />`.

```csharp
using Kiln;

// the world (tick thread)
Log.Info($"{WorldProgress.Flame}");                         // flame level 4, 2 altar(s) (highest level 3), 1 water source(s)
if (WorldProgress.Difficulty is { } d) Log.Info($"{d.Preset}, enemy damage x{d.Number("enemyDamageFactor")}");

await WorldProgress.WhenReady(bosses: true);                // the lists are read from the game's data in the background
foreach (var s in WorldProgress.Spires) Log.Info($"{s.Id}: {(s.Unlocked == true ? "unlocked" : "locked")}");
var dragon = WorldProgress.Boss("enemy_boss_fog_dragon");
Log.Info($"the dragon is {(dragon?.Killed == true ? "dead" : "alive")}");

// a character
var me = WorldProgress.For(player);
Log.Info($"{me.ShrinesFound.Count} of {WorldProgress.Shrines.Count} shrines, {me.RootsDestroyed.Count(r => r.ElixirWell)} wells");
var wells = WorldProgress.ElixirWells.Count(w => w.DestroyedBy(player) == true);

// quests: the game's journal and Kiln's, one list
var log = Quests.Of(player);
foreach (var q in log.Active) Log.Info($"{q.Name ?? q.Id}: step {q.Step + 1} of {q.Steps}");
var shelter = log.Find("NPC_Alchemist_Hint01_HAS_Shelter_Journal");   // or "quest:0x8755c335", or "mymod:tobin"

// a chest near a point, with its state where its area is loaded
if (WorldProgress.Chest(new WorldPosition(3800, 833, 1434)) is { } c) Log.Info(c.ToString());

// events
[Subscribe] void OnBoss(BossKilledEvent e) => Log.Info($"{e.Boss?.Id ?? e.Template} killed by {e.KillerPlayer?.Name}");
[Subscribe] void OnQuest(QuestStatusChangedEvent e) => Log.Info($"{e.Player?.Name}: {e.Quest.Name ?? e.Quest.Id} {e.OldStatus} -> {e.NewStatus}");
```

| Member | Threads | Notes |
|---|---|---|
| `WorldProgress.Available`, `Status` | any | `WorldProgressStatus`: the services in use, the index state (`Places`, `Bosses`: NotStarted, Building, Ready, Failed), counts, seconds, problems |
| `PlacesReady`, `BossesReady`, `WhenReady(bosses, timeout)` | any | the lists are read once per process in the background, from a moment after the first use (places well under a second, bosses a second or two) |
| `Flame`, `FlameLevel` | tick | `FlameStatus {Level, Altars, HighestAltarLevel, WaterSources, Entity}`; null before the world runs |
| `Flame.Stored`, `Flame.Max`, `Flame.SystemLevel` | tick | the authoritative Level attribute, its maximum, and the level the game's rules read; null when unavailable |
| `SetFlameLevel(level)` | any | `Task<FlameChange>`; writes the world's Level attribute, marks it for saving, runs the game's formula and waits for its replicated copy |
| `LimitsAt(level)` | tick | `FlameLimits {Level, Altars, WaterSources, FogResistance, FogRemovalLevel?, FogRemoved?}` from the balancing table and current server setting; null when unavailable or outside the table |
| `Difficulty` | tick | `WorldDifficulty {Preset, Settings, IsCustom, Number(name)}`; null until kiln.settings is ready |
| `Spires`, `Spire(id)` | any (reads: tick) | `Spire {Id, Template, NameTag, Position, Unlock, TopReached?, Beacon?}`, `Unlocked`, `UnlockedFor(p)`, `TopReachedBy(p)`, `BeaconLit` |
| `Shrines`, `Shrine(id)` | any (reads: tick) | `FlameShrine {Id, Kind (Shrine, Sanctum), Region, Template, NameTag, Position, Discovery}`, `FoundBy(p)` |
| `ShroudRoots`, `ElixirWells`, `ShroudRoot(id)` | any (reads: tick) | `ShroudRoot {Id, Region, ElixirWell, SkillPoints, Position, Destruction}`, `DestroyedBy(p)` |
| `Obelisks`, `Obelisk(id)` | any (reads: tick) | `Obelisk {Id, Region, Position, Reading}`, `ReadBy(p)` |
| `Bosses`, `Boss(id or template)`, `BossesNear(at, r)` | any (reads: tick) | `Boss {Id, Template, Spawner, NameTag, Position, Kill?}`, `Tracked`, `Killed`, `KilledBy(p)` |
| `Survivors` | tick | `Survivor {Id (the NPC's debug name), NameTag, Rescue}`, `Rescued` |
| `Chest(at, radius = 3)`, `Chests(at, radius)`, `Chest(lootId, near)`, `ChestOf(entity)` | tick | `ChestStatus {LootId, Template, Position, Entity, Loaded, Opened?, Looted?}`; the first lookup in an area reads that part of the game's data (a few ms) |
| `For(player)` | any (lists: tick) | `CharacterProgress`: `SpiresUnlocked`, `SpireTopsReached`, `ShrinesFound`, `RootsDestroyed`, `ObelisksRead`, `BossesKilled`, `Quests` |
| `Quests.Of(player)` | tick | `QuestLog {All, Active, Completed, Vanilla, Kiln, Find(id or name), StatusOf}` of `QuestEntry {Id, Name, Kind, Status, Step, Steps, StepId, NameTag, Journal, Definition}` |

### Ids
Readable, stable for a game build, and the same as the catalog's `name` (rules: [world-status.md §6](../../re/world-status.md#6-ids)):
- spires: the fast-travel marker's region, lower-case: `grassland`, `lakedistrict`, `capital`, `steppes`, `talltrees`, `coldheights`, `desert`, `wetland` (and two Ancient Lands teleports);
- shrines, roots, obelisks: the game's own achievement names with the region: `grassland_flameshrine_01`, `deepforest_flametemple_02`, `desert_well_09`, `steppes_additional_skillpoint_01`, `wetlands_obelisk_above_fog_read`;
- bosses: the boss template, lower-case, numbered when placed more than once: `enemy_boss_fog_dragon`, `enemy_midboss_vukah_bigfoot_bosshealthbar`;
- vanilla quests: `quest:0x<entryId>`, with a `Name` from the game's query behind them (142 of 188); Kiln quests: `<mod>:<quest>`.

Display names are loca: the server's game data has none. `NameTag` is the loca tag id; [Texts.Get](../modding/texts.md)
reads the English text from a client catalog installed on the server machine.

### Change the Mother Flame

```csharp
var change = await WorldProgress.SetFlameLevel(4);
if (!change.Ok || !change.Mirrored) Log.Warn(change.ToString());
Log.Info($"{WorldProgress.Flame}; {WorldProgress.LimitsAt(4)}");
```

The write uses the same attribute functions as the game's admin command. The level is saved in the world; it stays after
the mod unloads. `FlameChange` reports the stored value before and after, whether it was marked written, whether the game
mirrored it, and any refusal. `Ok` means the write ran; check `Mirrored` for the replicated copy. With no running Mother
Flame or unsupported game functions it returns a failed result. An invalid level throws `ArgumentOutOfRangeException`
(1 through `Flame.Max`, normally 20). Lowering a level is allowed; the game decides what happens to existing altars.
If the attribute formula fails, Kiln restores and verifies the old level and maximum; a failed restoration is explicit
in the result. Accepted changes complete from the native result. They serialize, so a full queue can take 1638 program
updates, or longer while the program is paused; callers do not report a timeout while a queued write can still run.

`Flame.Altars` and `Flame.WaterSources` count existing objects. `LimitsAt` reports their permitted maxima. The
`flameLevelFogRemoval` setting is a threshold, not a stored flame level; changing the level does not change that setting.
The game's `ServerProgressSystem` reads the new level for the altar, water-source and fog rules. A joined client is still
needed to check menus, placement and visible fog.

### Facts
`ProgressFact {Query, Scope}`: the requirement as the game stores it and where it lives (`World`, `Character`, `Both` = a
world object with a per-character twin). `Holds(target)` (tick thread): `target` None = the world, else a player in game;
null when kiln.knowledge is not loaded, the player is not in game, or a `Character` fact is asked of the world.
`fact.Id` is the knowledge id for [Knowledge](knowledge.md).

### Quests
- Vanilla: the game's journal states for the player (`Journal.States`). `Locked` = not started, `Active`, `Done` (completed
  or rewarded); the game has no failed state. `Step` = the last visible step (the game's journal shows every earlier step
  as done). World quests read the world's knowledge in shared progression, so a new character sees the world's quests.
- Kiln: the engine's status (`Locked`, `Available`, `Active`, `Done`, `Failed`) and step. Hidden quests only once started.

## Events
All on the tick thread, built only while someone subscribes, none cancelable (they report what already happened).

| Event | When | Notes |
|---|---|---|
| `FlameLevelChangedEvent {OldLevel, NewLevel, Flame}` | the world's flame level changed | sampled every 30 steps (half a second) |
| `SpireUnlockedEvent {Spire, TopReached, Target, IsWorld, Player}` | a spire's unlock was set in the world (`IsWorld`) or a character's twin, or a character's top reached (`TopReached`) | from `KnowledgeChangedEvent`, one step after the game's step |
| `ShrineFoundEvent {Shrine, Target, Player}` | a character found a Flame Shrine or Sanctum | |
| `ShroudRootDestroyedEvent {Root, Target, Player}` | a character destroyed a root (`Root.ElixirWell`: a well cleared) | |
| `BossKilledEvent {Boss, Template, Entity, Killer, KillerPlayer, Position, Recorded}` | a boss (a creature with a boss health bar) died | from the game's death (`Entity`, `Killer`); `Recorded` when its kill record changed too. A kill Kiln did not see dying (the record alone) comes ~2 s after the record with `Entity` None. One event per kill |
| `ChestLootedEvent {Chest, NearestPlayer}` | a loaded chest was emptied after it was opened | sampled every 30 steps; `NearestPlayer` is a guess |
| `QuestStatusChangedEvent {PlayerEntity, Player, Quest, OldStatus, NewStatus, OldStep, NewStep}` | a quest's status or step changed for a player | Kiln quests right after the engine's change (`PlayerEntity` None for world and synthetic holders); vanilla quests re-evaluated within a quarter second of the knowledge change. A character's journal at join is not a change (10 s baseline) |

## How it works
- **The lists** come from the game's own data on this machine (server or client), read once per process on a background
  thread through kiln.assets: the knowledge query db (named queries: the game's achievement lists name every shrine,
  root and obelisk by region), the world scene's global chunk (the spires' fast-travel markers and beacons, shrine
  positions), then every template and every chunk of the scene (bosses, root positions). On a dedicated server: 0.04 s
  and 0.21 s. Chests are read one chunk at a time on demand.
- **Facts** are knowledge ids or named queries, evaluated with the game's evaluator (`Knowledge.TryQuery`); their scope
  comes from `GameKnowledgeResource` (`Knowledge.Info`).
- **The flame** is `NetworkServerProgress` on the world's progress entity (`World.TryRead`).
- **Chests**: a placement's `SceneLootNodeId` is its id; the live entity (`Spawn.Find` of its template at its position)
  gives `RandomLootContainer.spawnedLootVersion` (opened) and `InventoryEmptyState.isEmpty` (emptied). Chests whose area
  is not loaded read `Loaded = false` and null states: the game keeps them in the save's persistent chunk data, which
  Kiln does not read.
- **Fail soft**: no kiln.assets, an unknown layout (a component of another size is skipped), a missing resource: the
  list stays empty and `Status.Problems` says why; reads return null. Nothing throws into the game.
- Layouts are from game data 1287369 (docs/re/world-status.md §7).

## Kiln.Admin
| Op | Args | Answer |
|---|---|---|
| `world_status` | `detail?` | `{index, flame, difficulty {preset, custom, settings}, spires [{id, template, nameTag, position, unlock, unlocked, beaconLit}], bosses [{id, template, spawner, nameTag, position, kill, killed}], survivors [{id, nameTag, rescued}], counts, players [{entity, name, spiresUnlocked, spireTopsReached, shrinesFound, rootsDestroyed, elixirWellsCleared, obelisksRead, bossesKilled, questsActive, questsDone}]}`; `detail` adds `shrines`, `shroudRoots`, `obelisks` |
| `world_status_player` | `target` (entity or name) | the player's counts plus every spire, shrine, root, obelisk and tracked boss with that player's value, and `quests [{id, name, kind, status, step, steps, nameTag}]` (started ones) |
| `world_chests` | `x, y, z, radius?` (default 8, at most 64 m) | `{chests [{lootId, template, position, loaded, entity, opened, looted}]}` |

The launcher's admin pages do not show these yet.

## Testing
```sh
export KILN_OUT=<build dir>
dotnet test managed/tests/Kiln.Quests.Tests --artifacts-path $KILN_OUT/dotnet                 # readers and rules, no game
KILN_WORLD_RES=<dumps> dotnet test managed/tests/Kiln.Quests.Tests --artifacts-path $KILN_OUT/dotnet   # + the real data
dotnet build mods/tests/worldstatus-test -c Release -o $KILN_OUT/mods/worldstatus-test --artifacts-path $KILN_OUT/dotnet
tools/deploy-server.sh ~/srv/enshrouded/labs/<lab> --policy any; tools/lab/lab.sh start <lab>
grep worldstatus-test ~/srv/enshrouded/labs/<lab>/kiln/logs/kiln.log
```
`<dumps>`: a folder `<type hash hex>/<guid>.<part>.bin` with the query db, every scene chunk and every template of a game
folder (read with `tools/kfc`). The lab mod changes the lab world's knowledge and flame for a few seconds and puts them
back: lab worlds only.

## Lab
Server 26430253, a copy of a lab save (one base, flame level 3, 7 survivors, 3 spires unlocked), no player:
`mods/tests/worldstatus-test`, 44 PASS, 0 FAIL. From kiln.log:
```
quests: world progress: 10 spires, 68 flame shrines and sanctums, 75 shroud roots, 13 obelisks from the game's data in 0.04 s
quests: world progress: 30 boss placements (13 with a kill record) from 12269 templates and 1601 chunks in 0.21 s
PASS every fact's scope matches the game's knowledge object
PASS spire and boss reads equal the world's knowledge (3 unlocked, 2 killed in this world)
PASS the flame level equals the progress entity's Level attribute
PASS the difficulty is kiln.settings': preset Custom, 38 settings, enemy damage 1
PASS survivors: 7 of 28 rescued, as the NPC registry says
event: world: spire coldheights unlocked                                  (Knowledge.World.Grant, then revoked)
event: boss enemy_fogger_mageheavy_bosshealtbar killed (recorded)          (its kill record granted: one event, then revoked)
event: player 16: found sanctum grassland_flametemple_01                    (a free player block, direct writes)
event: player 16: destroyed elixir well root grassland_well_00
event: player 16: spire capital top reached
event: player 16 quest:0xc2fbdab1: Locked -> Active, step - -> 1             (its first step's knowledge set)
event: flame level 3 -> 4                                                   (the progress entity's Level written; put back)
event: looted chest 0x00000000 LootChest_Random_Cairn_Sarcophagus_Wood at (3805.8, 832.5, 1433.8): looted
event: boss Enemy_Fogger_Heavy_unarmored_BossHealthBar killed               (a spawned boss, Combat.Kill: the game's death)
event: holder without a player World progress test quest: Available -> Active, step - -> 1   (a Kiln quest)
```
Kiln.Admin over the file bridge: `world_status` (17 ms), `world_status_player` (refuses without a player), `world_chests`.

What this proves: the lists come out of the server's own data with the catalog's ids; every fact's scope matches the
game's objects; world facts read the same as direct knowledge reads; the flame level is what the game's set-flame-level
writes (the game copied a written `Level` into `NetworkServerProgress` within half a second); each event fires once for
a knowledge change, a death the game reports, a chest's components and a Kiln quest's change.

What it does not: anything a real character brings (its knowledge is written straight into a free player block here,
not loaded from a character); a real player looting a placed chest (no placed chest is loaded without a player; the
chest was a spawned one written as looted); a boss killed by a player; personal progression; a client-hosted world.
Those are the manual test.

## Manual test (reading only)
A lab `mylab` and a LOCAL copy of a character with progress (shrines found, wells cleared, spires climbed, quests).
Nothing here grants, unlocks or levels anything.
```sh
export KILN_OUT=<build dir>
dotnet build mods/Kiln.Admin -c Release -o $KILN_OUT/mods/kiln-admin --artifacts-path $KILN_OUT/dotnet
tools/deploy-server.sh ~/srv/enshrouded/labs/mylab --policy any; tools/lab/lab.sh start mylab
B=~/srv/enshrouded/labs/mylab/shroudforge/exports/emberwatch        # the file bridge
op() { echo "$2" > $B/requests/$1.json; sleep 1; cat $B/results/$1.json; echo; }
```
kiln.log at start: `world status ops: world_status, world_status_player, world_chests` and `world progress: 10 spires, …`.
Join the lab from the DEDICATED SERVER tab with the character, then:
1. `op w1 '{"op":"world_status"}'`: `flame.level` is the level the Flame Altar's menu shows; `flame.altars` the number of
   altars; `difficulty.preset` the server's preset.
2. `op p1 '{"op":"world_status_player","target":"<character name>"}'`:
   - `shrinesFound` and the `shrines` with `found: true` are the Flame Shrines and Sanctums the character has found
     (their markers on the map);
   - `elixirWellsCleared` and the `shroudRoots` with `destroyed: true`: the Elixir Wells shown cleared on the map and the
     roots the character destroyed (each gave skill points);
   - `spireTopsReached`: the spires this character has climbed;
   - `quests`: every started quest. The `active` ones are the journal's open quests, `done` its finished ones; `step` + 1
     is the step the journal shows last. Compare a few by `name` (or `kiln catalog search` for the title).
3. Optional, normal play in the lab world: stand next to an unopened chest and run `op c1 '{"op":"world_chests","x":<x>,
   "y":<y>,"z":<z>}'` with the position from `op q1 '{"op":"players"}'`: the chest shows `loaded: true`, `opened: false`.
   Open it: `opened: true`; take everything: `looted: true`.
