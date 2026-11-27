# The mod API: the contract, threads and the mod

Part of the mod API guides ([api.md](api.md) is the index). Here: which thread runs your code, the error contract and
capabilities, the `Mod` class, lifecycle events, other mods, and which `KilnServices` entry a type needs. Signatures are the
common ones; the XML docs and the generated API reference ([docs-map.md](docs-map.md)) have the rest.

## Threads, in one table
| Code | Thread |
|---|---|
| `OnLoad`, `OnUnload`, `Context.Tick.Pre/Post`, `[Subscribe]` handlers, IMC handlers, command handlers, `await` continuations in mod code | server tick thread (dedicated server, or the hosted world on a client) |
| `Client.Tick`, `Client.Post`, network handlers on a client, key binding handlers | client main thread |
| `Ui.Menus` clicks | game UI thread (main thread), after the frame |
| screen draw functions, crafting panels, `Ui.Frame`, `Hud.Show` text functions | render thread, every frame: draw only |
| `EcsPrograms.Bind` callbacks (dev.20+), `Systems.Wrap` stages and `Systems.Define` handlers (dev.29+) | game worker thread; declared component/event access only |
| `Abilities.Natives.Wrap`, `Abilities.OnDamage`, dev.29+ `RegisterSync` natives and `Attach` handlers | the thread that runs the Impact program, inside the sim step: touch only the call |
| `Context.Tasks.Run` work (dev.29.18+) | Kiln's worker threads: `[AnyThread]` APIs and `WorldConcurrent` only ([tasks.md](tasks.md)) |
| `OnBoot` (dev.28+) | the boot thread, before the game is entered: no tick, world or service calls ([boot.md](boot.md)) |

Back to the tick thread: `Context.Scheduler.Post(action)` or `await Context.Scheduler.NextTick()`. Check with
`Game.IsTickThread` / `Game.AssertTickThread()`. On a client in the main menu there is no world: `Scheduler.Post`
work waits for the first world step.

## The API contract (dev.26+; every service dev.27+)
Page: `docs/kiln/modding/contract.md`.
```csharp
if (!Capabilities.Is("items.equip")) { c.Reply(Capabilities.Why("items.equip")); return; }   // or catch KilnUnavailableException
var r = await Items.Equip(player, from, EquipmentSlot.Ring1);
if (!r.Ok) c.Reply($"not equipped: {r.Reason}");                                              // the game refused
```
- **One error rule.** The game refused: a result with `Ok = false` and `Reason` (`IKilnResult`). A bad argument:
  `ArgumentException`. A capability that is off: `KilnUnavailableException` (`Capability`, `Reason`; a
  `NotSupportedException`; message `"<row> is not available: ..."`: filter on `Capability`, not the text). dev.27+
  results include `HitResult`, `CastResult`, `FuelResult`, `QuestResult`, `ConversationResult`, `ProgressionResult`,
  `CreativeResult`, `SaveResult`, `TeleportResult` and every new call's. On dev.26 most services still threw their own
  exception types: catch `NotSupportedException` there.
- **A change Kiln makes has an event** (dev.27+ also `CombatChangedEvent`, `TimeSpeedChangedEvent`,
  `ImpactVariableChangedEvent`, `PlayerProgressionChangedEvent`). A change made inside the handler of its own event is
  announced at the start of the next step, never recursively.
- **Held rows (dev.28).** dev.28 shipped game-only features switched off: such a row reads unavailable with the reason
  `policy` and the text `game test pending:`; `Capabilities.IsHeld(name)` tells "built, not tested" from "does not work
  here", and `"preview"` in `kiln/kiln.json` (`true` or a list of names) switches them on for testing. dev.29+ holds
  nothing.
- **Capabilities.** `Capabilities.Is(name)`, `Get(name)` (`Capability`: `State`, `Reason`, `ReasonText`, `Source`,
  `SessionDependent`, `IsAvailable`), `Why(name)` (one sentence to log), `All`; any thread. `CapabilityChangedEvent`
  on the tick thread. `Game.HasFeature(name)` asks the same table. Names: every sigdb feature, `kiln.<service>`,
  `identity.entities`, `identity.players`, `items.*`, `saves.instance_data`, `anim.player_states`, `blocks.weapon_moves`;
  dev.27+ `rules.noclip`, `abilities.buffs`, `abilities.buff_list`, `players.aim`, `players.kick`, `players.bans`,
  `items.equip`, `blocks.affixes`, `saves.instance_data.sync`, `world.tracked_writes`, `time.weather_choose`,
  `ui.menu_scroll`. dev.27+ rows exist from the first tick; session rows are pending until a server world runs. A
  manifest's `features` is checked before the managed services start: name only sigdb features, `kiln.<service>` and
  `anim.player_states` there; check the rest with `Capabilities.Is` at run time.
- **`[RequiresCapability(name)]`** marks a member that throws `KilnUnavailableException` for that row. The attribute
  checks nothing at run time; dev.27+ the analyzer KILN121 reads it ([devkit.md](devkit.md#analyzers-in-every-kilnsdk-build)).
- **For a person or a living thing** (dev.27+): `Spawn.DestroyIfAlive(EntityRef)`, `Chat.TellIfOnline(PlayerRef, text)`,
  `Permissions.PersonHas(PlayerRef, node)`, `PermissionSubject.ForPerson`, `Players.FindPerson(PlayerRef)`.
- **Identity.** `EntityRef(EntityId Id, ulong Serial)`: `EntityRef.Of(id)`, `IsAlive`, `HasSerial`, `IsNone`;
  converts implicitly to `EntityId`. `PlayerRef(ulong SteamId, Guid CharacterId, EntityRef Entity)`: `PlayerRef.Of(entity)`,
  `Resolve()`, `IsOnline`, `IsSamePerson(other)`; explicit conversions from `Player`, `TalentHolder`, `QuestHolder`,
  `CharacterKey`.
  `ItemInstanceRef(EntityRef Entity, ItemId Item)` (not Kiln.Blocks' `ItemRef`). `ItemId` moved to Kiln.Api (old builds
  load unchanged). Tick thread, from the first tick, not in `OnLoad`. Sources: players, item entities, every entity
  kiln.spawn can inspect. An entity loaded from the save with no `SpawnTime` has serial 0 (`HasSerial` false): its
  `IsAlive` only says the id exists. Test `IsNone`, not `== None`. Refs ran on a test server; player refs with
  simulated players only.
- **Side and thread markers:** `[ServerOnly]`, `[ClientOnly]`, `[AnyThread]`, `[RenderThread]`, `[WorkerThread]`
  beside `[TickThreadOnly]`. Markers, read by the analyzers; dev.27+ the services' members carry them.
- **API version:** `Mods.RuntimeApiVersion` is the running Kiln's mod API (0.2.15 on dev.30.6; the table in
  [versions.md](versions.md)). `Mods.ApiVersion` is obsolete: a const, copied into your mod at build time. Release
  builds carry their full version (`0.2.0-dev.30.6`).
- **`[Experimental]` ids:** `KILNX001` lab and test members (hidden; a build error unless the project adds it to
  `NoWarn`), `KILNX002` API not yet proven in a real client. Neither is covered by the additive-API promise.
  The task-thread world reads and writes (`WorldConcurrent` and its types) first shipped in dev.29.18 under
  `KILNX003`. Since 0.2.0-dev.30.7, mod API 0.2.16 (no longer experimental). No opt-in is needed now.
  dev.28's `KILNX028` (capacity types) is not needed from dev.29 on.

## The mod
```csharp
public sealed class MyMod : Mod
{
    public override void OnLoad()
    {
        Log.Info($"{Context.Info.Name} {Context.Info.Version} (Kiln {Game.KilnVersion}, phase {Lifecycle.Phase})");
        Context.Tick.Pre += OnPre;                     // (in TickInfo t): t.Step, t.ClockDelta, t.WallDeltaMs
        Context.GameLog.Line += (level, text) => { };  // the game's log, on the tick thread
        _ = LaterAsync();
    }
    private void OnPre(in TickInfo t) { }
    private async Task LaterAsync() { await Context.Scheduler.NextTick(); /* still the tick thread */ }
    public override void OnUnload() { }                // release threads, files, sockets you created
}
```
- `Context`: `Info` (the whole manifest as `ModInfo`), `Log`, `Tick`, `GameLog`, `Scheduler`, `Messages`,
  `DataDirectory` (`<dataDir>/<id>`: your files), dev.29.18+ `Tasks` ([tasks.md](tasks.md)).
- `Mod.OnBoot(BootContext)` (dev.28+, with `"boot"` in the manifest) runs before the game is entered: [boot.md](boot.md).
- `Game`: `KilnVersion`, `IsTickThread`, `HasFeature(name)`, `AssertTickThread()`. dev.26+: `HasFeature` is
  `Capabilities.Is`.
- The dedicated server steps about 24.5 times a second (~41 ms). 1470 steps ≈ 1 minute.

## Lifecycle events (Kiln.Api; tick thread; not cancelable)
`ModsLoadedEvent` (once, after the last `OnLoad`), `ServerStartingEvent`, `ServerStartedEvent` (`Step`, `Source`:
the world is live, players can join), `ServerStoppingEvent` (`Reason`: save your state here, the world is intact),
`ModReloadedEvent`, `ModDisabledEvent`. `Lifecycle.Phase` (`Booting`, `Starting`, `Running`, `Stopping`, client
`Idle`), `Lifecycle.IsServerRunning`, `Lifecycle.StartedAtStep` tell a hot-loaded mod what it missed.

## Other mods
`Mods.All`, `Mods.IsLoaded(id)`, `Mods.Get(id)` (`ModEntry`: `State`, `Error`, `Version`, `Stats`), `Mods.Current`,
`Mods.Side`, `await Mods.ReloadAsync(id)` / `UnloadAsync(id)` / `LoadAsync(folder)`.
Messages: receiver `Context.Messages.On("method", m => ... m.PayloadAs<T>() ... m.Sender)`; sender (any thread)
`Mods.Send("target-id", "method", payload)`. Delivered on the receiver's next step. Payloads: BCL types, Kiln types or
types from an assembly the receiver exports. Shared APIs: put them in `MyLib.Api.dll`, list it in `exports`,
dependents reference it with `Private="false"`. Reloading a provider reloads its dependents.

## Which assembly holds a type (`KilnServices`)
Everything is in namespace `Kiln` (only the test helpers are in `Kiln.Testing`), so a `using` never tells you which assembly
a type needs. A service is the assembly `Kiln.<Name>.dll`, and `KilnServices` lists it as `<Name>` (`Players;Chat`).
`Kiln.Api` is always referenced. List what your code names: the SDK adds the assemblies those types need, and KILN108 warns
about a service you use and did not list ([setup.md](setup.md)). The table is built from the `PublicAPI.*.txt` files (every service, dev.32);
look a type up here before you guess its entry.

| `KilnServices` | Types and entry points (nested ones as `Ui.Inventory`) |
|---|---|
| (`Kiln.Api`, always) | `Mod`, `ModContext`, `Events`, `Config`, `Mods`, `Capabilities`, `Lifecycle`, `Game`, `Overrides`, `Pacing`, `Decisions`, `EcsPrograms`, `KilnLanguage`, `GameTexture`; `EntityId`, `EntityRef`, `PlayerRef`, `ItemId`, `ItemInstanceRef`, `WorldPosition`, `WorldKind`, `TickInfo`; `Context.Tasks` |
| `Abilities` | `Abilities`, `Gems` |
| `Assets` | `Assets` |
| `Audio` | `Sound`, `Voiceover` |
| `Blocks` | `Blocks` and every block type (`ItemBlock`, `RecipeBlock`, `PropBlock`, `NpcBlock`, `RarityRollBlock`, `RarityUpgradeBlock`, `KnowledgeBlock`, `BuildingToolBlock`, `TerrainBrushBlock`, `ToolMaterialPoolBlock`, ...), `PetBlock`, `TamingBlock` (dev.32), `Npcs`, `NpcSpeech`, `SpeechPick`, `NpcTopic` (NPC speech and dialogue), `Rarity`, `Stat`, `ItemRef`, `BlockIdentity`, `UpgradeAction`, `UpgradeStation`, `Affixes`, `WorldTables`; with the `Is` and `Act` rule extensions (below). **`Stations`, `Bases` and `Crafting` are here too** |
| `Stations` | no assembly of its own: references Kiln.Blocks for `Stations`, `Bases` and `Crafting` without `Blocks`' side and clients defaults |
| `Build` | `Build`, `Schematic`, `BuildShape`, `CellBox`, `BuildOptions`, `BlockInfo`, `BlockQueryResult`, `BlockBreakOptions`, `BlockBreakResult` |
| `Camera` | `Camera`, `CameraLease`, `CameraBlend` (leases on the camera) |
| `Chat` | `Chat`, `Commands`, `Arg`, `Selectors` |
| `Client` | `Client`, `BuildingInput`, `SteamGuard` |
| `Combat` | `Combat`, `Attributes` |
| `Content` | `Content`, `Texts`, `ItemDefinition`, `RecipeDefinition`, `ContentEntry`, `GearStats`, `CraftingGroups`, `CraftingGroupRef` |
| `CreatureControl` | `CreatureControl` |
| `Creatures` | `Creatures`, `CreatureVariants`, `Arsenals`, `Villagers`; the plain members of `Is` and `Act` |
| `Game` | `Ecs`: a typed struct for each game component and event (generated per game build; Kiln.Game) |
| `Gameplay` | `Gameplay`, `Offers` |
| `Hooks` | `Hooks`, `Symbols`, `Memory`, `OriginalCode`, `Keep` |
| `Items` | `Items`, `Gear`, `ItemInfo`, `ItemRarity`, `ItemCategory`, `EquipmentSlot`, `InventorySlot`, `GearState`, `GearResult`, `RarityUpgradeRule`, `UpgradeResult`; since dev.32 `ItemCosts`, `ItemSelection`, `CostReceipt` |
| `Markers` | `Markers`, `MarkerIcon`, `MarkerAnchor` (map markers, the compass, world labels) |
| `ModValues` | `ModValues`, `ModValueDefinition`, `ModValueKey` (per-character counters and currencies) |
| `Network` | `Network`, `SteamMessages`, `ConfigSync` |
| `Permissions` | `Permissions` |
| `PlayerAnim` | `PlayerAnimStates` |
| `Pets` | dev.32: `Pets`, `PetsClient`, `PetDefinition` and its policies, `PetSnapshot`, `PetRequest`, `PetResult`, the `Pet...Event`s (`PetBlock` is Kiln.Blocks') |
| `Players` | `Players`, `Player`, `PlayerStates`, `Bans`, `PlayerCharacters` |
| `Queries` | `Queries` |
| `Quests` | `Quests`, `WorldProgress`, `Knowledge`, `Dialogues`, `Journal`, `NativeQuests`, `QuestInput` |
| `Registry` | `Registry`, `RegistryViews`, `RegistrySchema`, `RegistryStage`, the entry types; experimental (`KILNX031`): [api-registry.md](api-registry.md). `Mod.OnRegistry`, `RegistryContext` and `RegistryEntryPoints` are Kiln.Api |
| `Rules` | `Rules`, `NoClip`, `Creative` |
| `Saves` | `Saves`, `WorldData`, `PlayerData`, `EntityData`, `InstanceData`, `CharacterData` |
| `Settings` | `Settings` |
| `Sigs` | the generated typed-hook classes (needs `Hooks`) |
| `Skills` | `Skills`, `GameTree`, `TalentHolder`, `TalentAttributes` |
| `Spawn` | `Spawn` |
| `Systems` | `Systems` |
| `Taming` | dev.32: `Taming`, `NativeTaming`, `TamingDefinition`, the steps (`FeedStep`, `WeakenStep`, `UseToolStep`, `NativeStep`), `TamingProgress`, `TamingToken`, `TamedEvent` |
| `Terrain` | `Terrain` |
| `Time` | `Time` |
| `Tools` | `TerrainToolAction`, `TerrainToolMode`, `ToolMaterials`, `ToolMaterialSnapshot` |
| `Ui` | `Ui` (`Ui.Menus`, `Ui.Screens`, `Ui.Crafting`, `Ui.Inventory`, `Ui.Tooltip`, `Ui.Values`, `Ui.World`), `Hud`, `KeyBindings`, `KeyInput`, `KeyChord`, `UiText`, `UiDraw`, `StatusLines`, `Color`, `SlotMark` |
| `Vfx` | `Vfx`, `ScreenEffects` |
| `World` | `World`, `WorldConcurrent`, `ComponentChanges`, `Teleport`, `Nearby` |
| `Testing` | namespace `Kiln.Testing`: `KilnTestAttribute`, `Assert`, `Tests`, `ClientCheckAttribute`, `ClientChecks`; `Kiln.TestKit` (offline tests) is a package of its own ([testing.md](testing.md)) |

`Eml` (EML hash and data helpers) is also a service name. `DevTools`, `Replication` and `Example` are Kiln's own and a
mod does not list them. Two things this table settles:
- **Same name in two places.** `Rarity` (Kiln.Blocks) is what a block says; `ItemRarity` (Kiln.Items) is what an item or the
  gear API says. `Color` and `KeyChord` are Kiln.Ui's. A type that sits in `Kiln.Api` (`ItemInstanceRef`, `EntityId`) needs
  no entry.
- **`Is.*` and `Act.*` are C# 14 extension members.** `Is` and `Act` are static classes in Kiln.Creatures with a few plain
  members (`Is.Day`, `Is.HealthBelow`, `Act.Attack`, `Act.SetPhase`). Most of the rule helpers (`Is.InCombat`,
  `Is.HasTarget`, `Is.TargetIsPlayer`, `Is.EnemiesNear`, `Is.SummonsBelow`, `Act.Flee`, `Act.Heal`, `Act.Cast`,
  `Act.Summon`, `Act.Immune`, `Act.Effect`) and the `Blocks.Action`, `Blocks.Brain`, `Blocks.Condition`, `Blocks.Target`,
  `Blocks.AttackPicker`, `Blocks.SenseFilter` and `Blocks.Spawn` registrations are extension members declared in Kiln.Blocks'
  `AiRulesApi` and `NpcBlocksApi` (`extension(Is) { ... }`). You write `Is.InCombat` and `Act.Flee()`, but they need
  `Blocks` in `KilnServices`, and a PublicAPI or XML search for `Is.InCombat` finds nothing: search `AiRulesApi`. Rules:
  `docs/kiln/modding/npc-ai.md`.
