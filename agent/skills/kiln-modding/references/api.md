# The mod API, by area

Namespace `Kiln` for everything. Each service is its own assembly: list it in `KilnServices` (setup.md). Signatures here are the
common ones; the XML docs and the wiki have the rest. Talents, quests, NPCs and sounds:
[quests-talents-sounds.md](quests-talents-sounds.md).

## Threads, in one table
| Code | Thread |
|---|---|
| `OnLoad`, `OnUnload`, `Context.Tick.Pre/Post`, `[Subscribe]` handlers, IMC handlers, command handlers, `await` continuations in mod code | server tick thread (dedicated server, or the hosted world on a client) |
| `Client.Tick`, `Client.Post`, network handlers on a client, key binding handlers | client main thread |
| `Ui.Menus` clicks | game UI thread (main thread), after the frame |
| screen draw functions, crafting panels, `Ui.Frame`, `Hud.Show` text functions | render thread, every frame: draw only |
| `EcsPrograms.Bind` callbacks (dev.20+) | game worker thread; declared component/event access only |
| `Abilities.Natives.Wrap`, `Abilities.OnDamage` | job thread inside the sim step: touch only the call |

Back to the tick thread: `Context.Scheduler.Post(action)` or `await Context.Scheduler.NextTick()`. Check with
`Game.IsTickThread` / `Game.AssertTickThread()`. On a client in the main menu there is no world: `Scheduler.Post`
work waits for the first world step.

## The API contract (dev.25+, in development; not seen by a player)
dev.25 is not released: these need a dev.25 runtime and MDK. Page: `docs/kiln/modding/contract.md`.
- **One error rule.** The game refused: a result with `Ok = false` and `Reason` (`IKilnResult`). A bad argument:
  `ArgumentException`. A capability that is off: `KilnUnavailableException` (`Capability`, `Reason`; a
  `NotSupportedException`). Few results implement `IKilnResult` yet (`PlayerAnimDefineResult`), and older services still
  throw their own exception types: catch `NotSupportedException`.
- **Capabilities.** `Capabilities.Is(name)`, `Get(name)` (`Capability`: `State`, `Reason`, `ReasonText`, `Source`,
  `SessionDependent`, `IsAvailable`), `Why(name)` (one sentence to log), `All`; any thread. `CapabilityChangedEvent`
  on the tick thread. `Game.HasFeature(name)` asks the same table. Names: every sigdb feature, `kiln.<service>`,
  `identity.entities`, `identity.players`, `items.*`, `saves.instance_data`, `anim.player_states`, `blocks.weapon_moves`.
  A row a managed service registers exists only after its first use (Kiln.Items: the first tick; item blocks: the
  first compile), so name only sigdb features, `kiln.<service>` and `anim.player_states` in a manifest's `features`;
  check the rest with `Capabilities.Is` at run time. `[RequiresCapability(name)]` marks members of Kiln.Items, item data,
  Kiln.PlayerAnim and the move blocks; it is documentation: the member's own code throws, nothing checks the attribute.
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
  beside `[TickThreadOnly]`. Markers only; few members carry them yet.
- **API version:** `Mods.RuntimeApiVersion` is the running Kiln's mod API. `Mods.ApiVersion` is obsolete: a const,
  copied into your mod at build time. Release builds carry their full version (`0.2.0-dev.25`).
- **`[Experimental]` ids:** `KILNX001` lab and test members (hidden; a build error unless the project adds it to
  `NoWarn`), `KILNX002` API not yet proven in a real client. Neither is covered by the additive-API promise.

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
  `DataDirectory` (`<dataDir>/<id>`: your files).
- `Game`: `KilnVersion`, `IsTickThread`, `HasFeature(name)`, `AssertTickThread()`. dev.25+: `HasFeature` is
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

## Events
```csharp
[Subscribe] private void OnJoin(PlayerJoinedEvent e) => Log.Info($"{e.Player.Name} joined");
[Subscribe(EventPriority.High, ReceiveCanceled = true)] private void OnChat(ChatMessageEvent e) { e.Cancel(); }
var sub = Events.On<CommandExecutedEvent>(e => { });   // sub.Dispose() to stop
Events.Post(new MyEvent());        // tick thread; Events.Enqueue(ev) from any thread
```
Priorities `Highest, High, Normal, Low, Lowest, Monitor`. `CancelableEvent.Cancel()`, `ResultEvent` (`Allow`,
`Deny`). Build your own events only when `Events.HasSubscribers<T>()`. Each service page lists its events with
thread, cancel meaning and writable fields: read it before relying on one. Some player-facing events are built but not yet
confirmed with a real player.

## Configs
```csharp
public sealed class MyConfig
{
    [Comment("How many homes a player may set."), Range(1, 50)] public int MaxHomes { get; set; } = 3;
    [RequiresRestart] public int Port { get; set; } = 8080;
    [Synced] public double DamageFactor { get; set; } = 1.0;   // the server's value on connected Kiln clients
}
_cfg = Config.Register<MyConfig>();            // ConfigKind.Common -> <configDir>/<modid>.json (Server / Client kinds too)
_cfg.Changed += e => { };                      // or [Subscribe] ConfigChangedEvent
var n = _cfg.Value.MaxHomes;                   // always current
```
Commented JSONC, validated, clamped, hot-reloaded on edit. Never build the config path yourself: `Config.Directory`.
**dev.20+:** `Config.RegisterWorld<T>()` keeps one config per world in its sidecar; values switch before `WorldLoadedEvent`
and use defaults with no world. `[RequiresRestart]` edits apply when that world next opens. `Context.Locale.Get(key, args)`
reads this mod's `locales/<language>.json`, falling back to English/key; use it for mod text, not game-tag lookup.

## Commands and permissions
```csharp
[Command("heal", Description = "heals a player", Permission = CommandPermission.Admin)]
private string Heal(CommandContext c, Player target, [Range(1, 1000)] int amount = 100) => $"healed {target.Name}";

Commands.Register(Commands.Literal("give").Then(Commands.Argument("player", Arg.Player())
    .Then(Commands.Argument("item", Arg.Item()).Executes(c => Give(c.Get<Player>("player"), c.Get<ItemInfo>("item"))))));

Permissions.Register("mymod.fly", PermissionLevel.Everyone, "fly around");
if (Permissions.Has(player, "mymod.fly")) { }
```
Arg types: `Int`, `Double`, `Bool`, `Word`, `String`, `Greedy`, `Enum<T>`, `Choice`, `Player(s)`, `Entity(ies)`
(`@s @a @p @r @e[...]`), `Position` (`~` relative), `Item`, `Duration`, `TimeOfDay`. `/help` and completion are
built in. Console: `kiln/control/console/*.cmd`.

## Chat
`Chat.Received += m => { m.Cancel = true; }`, `Chat.Broadcast(text)`, `Chat.Tell(player, text)`,
`Chat.Say(player, text, to)`, `Chat.Players`, `Chat.FindPlayer(name)`. Tick thread for sends.

## Players and progression
`Players.Available`, `Players.All`, `Players.Find(name | EntityId)`, `FindBySteamId`, `PositionOf`,
`Players.Joined/Left` (`Action<Player>`). Progression: `player.Progression.Read()`, `AddExperience(n, source)`,
`SetLevel`, `GiveSkillPoints(n)` (up to the game's givable flags), `Unlock(node)`, `Lock`, `Respec()`;
`PlayerLevelChangedEvent`, `PlayerSkillsChangedEvent`. Changes return `Task<ProgressionResult>`; game refusals come
back as `Ok = false`, not exceptions. Level cap 50. New nodes and trees: [talents](quests-talents-sounds.md).

Player state (any of 350 states from the game's own enums, `docs/kiln/modding/player-states.md`): `player.Is(PlayerStateId.Soaked)`, `player.IsGliding`, `IsSwimming`, `IsAttacking`..., `player.Snapshot()` (`Health`, `Mana`, `Stamina`, `ShroudTimeLeft`, `Action`, `Active`), `player.Equipment`, `player.Buffs`. Live on the tick thread. Events: `PlayerStateChangedEvent` (any state), `PlayerActionEvent` (any action sequence, `Cancel` stops it), `PlayerConsumeEvent` (`e.Item == GameItems.Food_...`, `Cancel`: item stays), `PlayerConsumedEvent`, `PlayerClientActionEvent` (any client command), `PlayerJumpEvent`, `PlayerGlideEvent`, `PlayerEquipmentChangedEvent`, `PlayerHotbarChangedEvent`. Check the dated wiki status for player-facing verification.

## Items, spawn, world, time, settings, combat
- Items: `Items.Lookup(name)`, `Items.Search`, `await Items.Give(target, id, n, gear, options)`,
  `Items.Read(inventory)`, `await Items.Take(...)`, `Items.FindInventories(kinds)`.
- Spawn: `Spawn.Lookup(template)`, `await Spawn.Entity(template, pos, yaw, options)`, `Spawn.Item`,
  `Spawn.Group`, `Spawn.Inspect`, `Spawn.Destroy`, `Spawn.Tracked`, `Spawn.CleanupTracked(tag)`. Check
  `Spawn.WorldRunning` first. dev.20+: `SpawnOptions.Components` takes up to 64 `SpawnComponent.Of(name, bytes/value)`
  entries applied with the spawn, before its first tick; check `Spawn.SupportsComponents`. Exact reflected sizes are
  required, and `SpawnTime` needs `SpawnFlags.NoTrack`. This is distinct from queued component changes later.
- World: positions are `WorldPosition` in metres; teleport and entity reads.
- Time: `Time.State`, `Time.Set("19:45")`, `Time.ResetWeather()`. Set the time only after the world runs
  (`ServerStartedEvent`), or the save load overwrites it.
- Settings: `Settings.Get(name)`, `Settings.Set(name, value)`, `TrySet`, `Settings.All`.
- Combat (dev.20+): `Attributes.Get(entity, GameAttribute.Stamina)` and `Set` / `Add` (also Mana, BodyHeat, Oxygen);
  check `Available`/read results. These use the game attribute path, on the tick thread.
- Combat: `Combat.GetHealth`, `SetHealth`, `Heal`, `await Combat.Damage(target, n, attacker)`, `Kill`,
  `SetInvulnerable`; `EntityHurtEvent` (cancel, change `Amount`), `EntityDeathEvent`, ...
- Gameplay (dev.20+): `Offers.Add/Remove/IsActive/Status` puts an E prompt on a suitable world entity, owned by this mod;
  it needs collision and cannot already have a game/other-mod offer. Check availability/status; pending is not visible proof.
- Gameplay: `InteractEvent`, `CraftEvent`, `LootEvent`, `TeleportEvent`, `NaturalSpawnEvent`,
  protection zones (`Gameplay.Protect`).

## Inventory transfers (dev.20+)
`await Items.Move(from, slot, to, count: 0)`, `MoveAll(from, to)`, `QuickStack(from, to)` keep the same gear entity and its
level, upgrades, gem slot and durability. A take followed by a give makes new gear. `MoveResult` reports refusals and a
bounded `Changes` ledger. Check `Items.MovesAvailable`; run on the tick thread and await completion there.

`MoveOptions.DryRun` rolls back and emits no transfer event; `Equipped` and `ActionBar` opt into those slots; `Silent` skips
transfer rules/events. `Items.ContainerName(chest)` reads a player's name, never renames; check `ContainerNamesAvailable`.
`Items.Transfers.Keep(slot)`, `KeepItem(item, owner)`, `Deny(container, TransferKinds.QuickStack)` return disposable rules.
`InventoryTransferEvent` is a report after quick stack/move all, not cancelable. To cancel an incoming player request,
first require `e.Name == "inventoryTransferAction"` and `e.CanCancel` on `PlayerClientActionEvent`; then parse `e.Payload`
with `Items.Transfers.ParseAction` and cancel the held action. The parser does not identify an action from its bytes.
Joined-client events cannot cancel the server's action, and a Quick Stack Station has no such request. Rules and
subscriptions end with the original mod lifetime.
dev.21+: a native `transfer_listen` handler (kiln.items) also runs on a dry-run move (not a `Silent` one) with
`KILN_TRANSFER_DRY_RUN` in `ev->flags`. Keep and cancel as usual; count, log and change nothing outside the transfer.
Wiki: [inventory](https://frostdev.io/kiln/wiki/inventory).

## Persistent data
`WorldData.Get<T>(key)` (live object: change it in place; saved with the world), `PlayerData.Get<T>(steamId, key)`,
`EntityData.Get<T>(entity, key)` / `Set`. Tick thread. JSON via System.Text.Json. Keys are per mod. Kept across hot
reloads. `WorldSavingEvent` runs before the game's snapshot. Backups: `Saves.Request()`, `Saves.Backup(...)`.
In a hosted world, `OnLoad` sees empty world data until `WorldLoadedEvent`: read per-world data from that event.
**dev.20+:** `EntityData.IsPersistent(gearEntity)` is false. Gear has no stable world-position identity: its data follows a
move within the session, not a restart. Kiln skips these attachments on save and refuses old positional gear records.
World entities such as saved chests retain their normal persistence. dev.24 and older supply no persistent per-item data.

**Data on one item (dev.25+, in development; a player's inventory not seen by a player):** Kiln.Saves.
```csharp
_kills = InstanceData.Key<Kills>("kills");          // OnLoad; options: new InstanceKeyOptions { Persist = Persist.Session }
var sword = Items.Held(player);                     // ItemInstanceRef; also Items.Equipped(player, EquipmentSlot.ArmourHead),
sword.Get(_kills).Count++;                          //   InventorySlot.Ref, give/take/move results' Ref
```
- `Get` (the live object, saved with the world), `TryGet`, `Has`, `Set`, `Remove`; `InstanceData.All(key)`,
  `InstanceData.Parked(key)` (waiting or set aside, with the reason). Tick thread.
- Events: `InstanceDataChangedEvent` (a `Set`/`Remove`), `InstanceDataRestoredEvent` (`Exact`, `Moved`),
  `InstanceDataParkedEvent`, `InstanceDataLostEvent` (`Destroyed`, `Unmatched`, `Ambiguous`, `Capped`). Kiln.Items:
  `ItemMovedEvent` (`Transaction`, `Transfer`, `Observed`), `item.Location()` (`ItemLocation`). Admins: `/instances`.
- When Kiln is not sure which of two identical items a value belongs to, it sets the value aside, never on another item.
  Identical items swapped where Kiln did not see it (another world, an offline edit) trade their data.
- `Persist.Carried` throws `KilnUnavailableException("saves.instance_data.carried")`: not in this release. Plain stacks
  have no item entity. 256 records per holder per mod, 64 KB per value, waiting records dropped after 3 sessions.
- Chests proven on a test server; backpack, action bar and worn items wait for the guided client pass. Affixes: none (perks
  come from the item type). Page: `docs/kiln/modding/inventory.md` "Data on one item"; sample `mods/samples/ItemData`.

## Content and game data
```csharp
Content.OnReady(_ =>                              // after the packs, at every world load and pack reload
{
    Content.Items.Get("Material_T1_Wood_Logs")!.MaxStack = 2000;
    Content.Recipes.Get("Tool_basic_FishingRod_T0_PK")!.CraftingDuration = TimeSpan.FromSeconds(5);
    var r = Content.Apply("""{ "items": { "Material_*": { "maxStackSize": { "mul": 2 } } } }""");
});
Content.Items.Register(new ItemDefinition { Guid = ..., Name = "MyMod_FrostEdge", BaseOn = "Weapon_T1_1H_SwordEpic_01",
    DisplayName = "Frost Edge", Fields = new() { ["damageSetup.dmgMod"] = 1.5, ["rarity"] = "Rare" } });   // in OnLoad
Content.Recipes.Register(new RecipeDefinition { Guid = ..., Name = "MyMod_FrostEdge_Recipe", Station = "hand",
    Inputs = [("Material_T1_Twigs", 2)], Outputs = [("MyMod_FrostEdge", 1)] });
```
Registries: `Content.Items`, `Recipes`, `Loot`, `Drops`; game data: `Content.Templates`, `Arsenals`, `Sequences`,
`Buffs`, `Perks`, `Skills`. `entry.Get(path)`, `Original(path)`, `Set(path, value)`, `Reset(path)`.
`Content.Dump()` writes every registry with its fields to `kiln/data/content/dump.json`: look names and paths up
there instead of guessing. `Content.Status()`, `Content.PackReport()`.
dev.21+: `Overrides.Claim`/`Release`/`RestoreAll` from a thread where no mod's code runs (`Task.Run`, a timer) throw
`InvalidOperationException`; post to the tick thread with `Context.Scheduler.Post`. A `ReflectedResources` handle got in
`OnLoad` stays your claim from any thread. `Assets.Reflect(key)` returns `null` on a build without kiln.types.

## Typed ECS reads (dev.25+, in development; not seen by a player)
`<KilnServices>Game</KilnServices>`, then `Kiln.Ecs` (Kiln.Game): a typed struct for every component and event of
the game, generated per verified build (`Ecs.Health`, `Ecs.LastTransform`, `Ecs.Enemy01_State`, `Ecs.FactionType`).
```csharp
if (!Ecs.Available) return;                                   // kiln.world off: the reads would throw
if (!Ecs.Supports<Ecs.LastTransform>()) { Log.Warn(Ecs.WhyNot<Ecs.LastTransform>()!); return; }
if (Ecs.TryGet<Ecs.LastTransform>(entity, out var t)) Log.Info($"x {t.transform.position.x}");
foreach (var e in Ecs.All<Ecs.Health>()) { }                  // also Ecs.Get<T>(e), Ecs.Has<T>(e)
```
- `TryGet`, `Get`, `Has`, `All`: tick thread; `Available`, `Supports`, `WhyNot`: any thread.
- Each type is checked against the running game at first use. A mismatch reads as nothing (false, null, empty) with
  one warning: never a partial read. Kiln also stamps the mod (`kiln.gametypes.json`) and refuses it by name at load
  when a type it uses changed; an enum constant the compiler folds into a number is not stamped.
- Reads only: a struct is a copy. Writes stay `World.TryWrite` (raw, not saved) until dev.26.
- `Kiln.GameTypes.Components` and the nine old structs are obsolete (`Use Kiln.Ecs.`) and still work.
- Server lab: typed reads equal raw bytes for 118 components; the rest and the client side are unproven.
  Page: `docs/kiln/typegen.md` §4.

## Creatures
`Creatures.Template(name)`, `Creatures.Of(entity)` → `CreatureRef` (`Level`, `Faction`, `Scale`, `Resistance[kind]`,
`SpeedFactor`, `DamageFactor`, `XpFactor`, `Loot`, `Features`, `Ai.Set(path, v)`, `Reset()`), `Command.Guard /
Attack / Stay / MoveTo / Heal`. Variants: `CreatureVariants.Register(new CreatureVariant { Id, Base, Level, ... })`,
`await Creatures.Spawn(variantOrTemplate, pos)`, `CreatureVariants.AddSpawnRule(...)`. Setters are layers of your mod.
dev.25+: `Arsenals.RulesState` (`ArsenalRulesStatus`) replaces the obsolete `Arsenals.Status` (`RulesStatus`).

## Abilities
`Abilities.Register(new AbilityDefinition { Id = "mymod:name", DefaultKey = "G", ChatCommand, Cost, Cooldown,
CastTime, Targeting, OnCast = async cast => { await cast.Impact(template, pos, configs); } })`,
`Abilities.Cast(actor, id)`, `ImpactStartingEvent` (change configs or cancel before a game impact runs; narrow with
`Abilities.HoldPrograms(ids)`), `await Abilities.RunImpact(...)`, `Abilities.PlaySequence(actor, guid)`,
`await Abilities.ApplyBuff(target, buffGuid, duration, caster)`, `Abilities.Attributes.Get/Add`,
`Abilities.Natives.Register(name, argc, call => ...)` for Impact programs.
dev.25+ (not seen by a player): a mod's own **player animation states** come from `assets/player-states.json`
(`{"format": 1, "states": {"my-slash": {"clip": "my_slash", "like": "Attack_Sword_Shield_01"}}}`), merged into the
player's graph at boot, never hot-reloaded; the mod needs `"side": "both"`, `"clients": "mod"` and its cook on the server.
Sequences enter them: a weapon's `Moves` or a cast's `Animation` names them ([blocks.md](blocks.md)). `Kiln.PlayerAnim`'s
`PlayerAnimStates` (any thread) only reads: `Available`, `Graph`, `GraphHash`, `Find(mod, name)`, `FindGameStates(name)`,
`All`, `GuidOf(mod, name)`, `IdOf(guid)`; `Define(state)` is refused in this release. The player graph's key cannot be
overridden (`Assets.Override`/`Register` throw). Page: `docs/kiln/modding/animations.md` "Player states". A Kiln impact has no used item: give it
its own `Damage1` config or it hits for 0.

## World edits
`await Build.Dig / Add / Flatten / Smooth / Paint / Replace(BuildShape.Sphere|Box|Cylinder(...))`,
`Build.Read/Write(box, cells)`, `Build.PlaceProp(template, pos, rotation)`, `Schematic.CaptureAsync`, `.Paste`,
`await Build.PlaceOnce(key, schematic, at)`, `await Build.Undo()`. Results carry `Refusal` (zone, base, not loaded).
Vanilla clients see the edits and the game saves them.

**dev.20+ flame writes:** `await WorldProgress.SetFlameLevel(level)` (Quests) returns `FlameChange`: inspect `Ok`,
`Reason`, `Marked` and `Mirrored`, not just queue acceptance. `LimitsAt(level)` reads derived limits. Client UI/fog/placement
checks remain pending; a failed rollback is explicitly reported and may leave changed state.

## ECS world programs (dev.20+)
For `kiln.programs.json` boot declarations and `EcsPrograms.Bind` in `OnLoad`, read
[world-programs.md](world-programs.md). Ordinary `OnLoad` is too late to add a scheduler declaration. A callback is a worker
context, never a place to call tick-thread game services or retain component pointers.

## Game UI, HUD, keys
`Ui.Menus.Add(MenuLocation.MainMenu|PauseMenu, text, onClick)`, `Ui.Screen(title, ui => { ... }, options)` →
`Open/Close/Toggle`, `Ui.Crafting.AddTab / AddSubcategory / AddPanel`, `Ui.Screens.Current`, `Hud.Show(id, text)`,
`Hud.Toast(text)`, `KeyBindings.Register(id, "F8", action, description)`, `UiText.Of(("en", ...), ("de", ...))`.
`Ui.Available` is false on a dedicated server. dev.20+: `WindowBlock`, `HudLineBlock`, `KeyBindingBlock` and the equivalent
JSON sections bind config and safe `game.*` values without draw code: [blocks.md](blocks.md#ui-as-data).
dev.20+: `StatusLines.Add(() => cachedText)` / `Set("state", text)` report mod status when asked, including on servers.
A source may run on a worker: use a tick-produced snapshot, never game state or tick-held locks. dev.21+: the menu and the
dashboard never wait for a source. They show the lines read last and refresh on a worker, so a new line appears a moment later.
dev.22+: `new UiScreenOptions { FullScreen = true }` is a whole-display screen (no title bar, not movable); draw your own
layout with `ui.FontSize(36)`, `ui.BeginChild(id, w, h, UiChildFlags.NavFlattened)`, `ui.Cursor` / `ui.SetCursor`, `ui.LastItem`,
`ui.Selectable(label, selected, width, height)`, `ui.DrawRect(UiLayer.Window, ...)`, `ui.PushColor` / `PushVar` and
`ui.KeyPressed(UiKey.Tab, claim: true)` (docs/kiln/ui.md "Layout for full screens"); `ui.HasLayout` is false on an older kiln.ui.
The Kiln menu (Insert, the MODS entries) lists every engine's mods; `Ui.AddConfigPage` pages show inline in its details.
A UI `command` runs in this game's registry; it does not send chat to a joined server. Use `castAbility` / `applyEffect`
with the same mod on the server for server-side actions. Old callbacks, queued edits and commands cannot outlive their mod load.

**dev.20+ game text:** `Texts.Get/GetPlain(tag)` in `Kiln.Content` resolves English loca. A server needs a client catalog
from the same game build (`kiln catalog build --side client`, local `kiln/catalog/client.json`). `Texts.Status` explains a
missing catalog; `Preload` loads ahead of time. dev.21+: while another thread loads the catalog (`Preload`, or a first call),
`Texts.Get` returns `null` at once instead of waiting; `Texts.Status` says "still loading". Catalogs contain game data and
must not ship in a mod.

## Assets
`assets/assets.json` entries `icon`, `texture`, `resource`, `blob`; loaded at boot. Icons and textures are client
only. Models and sounds: sources + `assets/cook.json`, cooked on the player's machine with `kiln cook`.

## Network
```csharp
var ch = Network.Channel("mymod:main", version: "1")
    .Register<Ping>((m, ctx) => ctx.Reply(new Pong(...)), NetDirection.ToServer)
    .Register<Pong>((m, ctx) => Hud.Toast("pong"), NetDirection.ToClient);
ch.SendToServer(new Ping(...)); ch.SendTo(player, msg); ch.SendToAll(msg);
```
Register messages in the same order on both sides. Server handlers on the tick thread, client handlers on the main
thread. Content changes reach Kiln clients without any code (content sync); configs need `[Synced]`.

## Client
`Client.State` (`Menu`, `DedicatedServer`, `Hosting`, `Remote`), `Client.IsClient`, `IsHosting`, `LocalPlayer`,
`Client.Tick += (in TickInfo t) => ...`, `Client.Post(action)`, `await Client.NextTick()`.
dev.25+, experimental (`KILNX002`), never run in a real client: `SteamGuard` (Kiln.Client, client only, any thread) stops
the game client reporting achievements to Steam. `SteamGuard.Hold(reason)` (an `IDisposable`; throws
`KilnUnavailableException` when it cannot protect), `TryHold(reason, out hold)`, `Latch(reason)` (until the game exits),
`Status` (`SteamGuardStatus`), `Protecting`. Call `Hold`/`TryHold`/`Latch` on the game's tick thread. Not covered: the game's
own stat reset, other mods' Steam calls, a player without Kiln.

## Hooks (advanced tier only) and other DLL mods
Only when no service does the job, and only with `"advanced": true`. dev.25+: the gate reads the manifest's `advanced`
(`ModInfo.Advanced`) and also covers `OriginalCode.Read`/`FindUnique`/`TryFindUnique`, `Keep.Block`/`Keep.Span`,
`Arsenals.JobHandler`, `Is.Job`, `Act.Job`, `Abilities.Programs.Register` and `NativeCall.Bytes`: without it they throw
`InvalidOperationException`. An unloaded mod can no longer install hooks. Since dev.21 every hook Kiln places on the game exe goes
through one registry, `kiln.hooks` v2: one detour per game function, shared by Kiln's services, EMF and Shroudtopia mod hooks
and your mod. Your hook is a **link** in that function's chain. An address outside the game image is refused
(`KILN_ERR_INVALID_ARGUMENT`); hooks on system DLL exports and EMF/Shroudtopia hooks outside the exe stay plain safetyhook.
- **Order on one function:** `before`/`after` constraints, then priority (higher first), then registration order. EMF mods'
  `hook`/`hook_pattern`/`hook_address` and Shroudtopia's `CreateDetour` are links owned by `emf:<id>` and `shroudtopia:<mod>` at
  priority 1 with `KILN_HOOK_NEWEST_FIRST`: they run newest first, before Kiln's priority-0 links. To run ahead of them, take a
  higher priority or `before` their owner.
- C#: `Hooks.Detour(symbolOrAddress, call => call.CallOriginal(), priority)` and
  `Hooks.Mid(symbolOrAddress, (ref Registers r) => { ... }, priority)` return a `HookHandle` (`Remove()`; unload removes it).
  A detour reads and writes `call[0]`..`call[3]` (rcx, rdx, r8, r9); returning a value without `CallOriginal()` replaces the
  function. Since dev.21 the original gets **every** argument, not only the first four. Floats: a mid hook (`r.XmmF(n)`) or a
  native mod.
- Mid hooks follow safetyhook's stack rules: `Rsp` is read-only, `TrampolineRsp` starts at `Rsp - 8`, and the game resumes with
  `rsp = TrampolineRsp; ret`. Return early from a function's first instruction with `r.Rax = value; r.TrampolineRsp = r.Rsp;`
  (C: `r->trampoline_rsp = r->rsp`).
- C++ (a native mod's `KilnNativeContext::api` and `owner`): `#include "kiln_hook.hpp"`, a global `kiln::RawHook`, any signature:
  ```cpp
  static kiln::RawHook g;
  static void hk(void* chat, int type, const char* text) {
      if (g.caller() == g_player_line_ret) { /* the game's call site: read it before calling on, never _ReturnAddress() */ }
      g.call<void>(chat, type, text);          // the rest of the chain, then the game (the game's exact argument types)
  }
  // install(api, owner, target, fn, label, priority = 0, before = nullptr, after = nullptr): here after "kiln.chat"
  if (!g.install(api, owner, target, (void*)&hk, "mymod/Thing", 0, nullptr, "kiln.chat")) { /* g.error() says why */ }
  ```
  `before`/`after` are comma-separated owner names (`kiln.chat`, `emf:coolmod`; kind prefix and `.dll` optional); a constraint
  that contradicts an earlier one is dropped with a kiln.log warning. Other kinds via `kiln::hooks_v2(api)`: `detour2` (up to 12
  integer/pointer arguments, `KilnHookCall2` with `c->caller`) and `kiln::MidHook2` (`void(*)(Ctx&)`, `Ctx` = `KilnRegisters` or
  `safetyhook::Context`).
- **`KilnHookOrder.flags`** (set `size = sizeof(KilnHookOrder)`; a smaller size reads 0): `KILN_HOOK_NEWEST_FIRST` runs the
  flagged links of one priority first, newest first. `RawHook::install` has no `flags` argument: call `hooks->raw_detour` with
  your own `KilnHookOrder`.
- **Same handler again:** installing the same raw handler on a function replaces the earlier link in one step (its handle stops
  working; the handler never runs twice for one call). A replaced `MidHook2` can run its handler twice for one call at that moment.
- **Link slots:** 65,536 per process, 512 hooked addresses (`KilnHooksStats.targets_cap`). A revoked link keeps its slot; an
  identical re-install (kind, function, user pointer, owner, label) takes it back. Managed `Hooks.Detour`/`Hooks.Mid` take a new
  slot on every install up to dev.24: install once, switch a flag. dev.25+: a managed re-install by the same mod, kind and
  address reuses its slot, also after a hot reload (test server); each new address still takes one. kiln.log warns at 75%
  and when full.
- Dropping a hook object does not remove the link: `revoke()` or `revoke_owner(owner)`. Reordering links already installed is not
  atomic: a call in flight at that moment can skip a link or run one twice (no crash).
- `kiln/state/hooks.json`: every function with its links in chain order, `foreignBefore`/`foreignOver` (other modules' hooks
  found before or over Kiln), `overlaps`, `rules`, `linkSlots` `{used, cap}`. kiln.log: `hooks: <label> chained over <module>+0x...`.

**Closed-source DLL mods** (XHL-Core, Auto Loot Plus, trainers) hook in their `DllMain`, before Kiln, and Kiln chains over them.
If a hosted engine needs Kiln's hook first, load the DLL late:
- `kiln/chain.ini` `[late]` repeats the path of an entry that `[chain]`, `[profile]` or `dinput8-chain.ini` already holds. The
  launcher's profile native page has the switch "Load after Kiln and ShroudForge" per chain entry and "Load late" on an overlap.
  kiln-core then loads the DLL after Kiln's services, plugins and ShroudForge: Kiln hooks first, the DLL hooks over it. An older
  proxy ignores `[late]` and loads the DLL early.
- Limits: EMF's own loader is never deferred (the launcher says "Always loads first"). A DLL another loader already started is
  `already-loaded` (`state/native.json` `chain.late`). A late DLL of a disabled or policy-refused native mod is skipped. An
  `early` or `boot` native mod cannot `requires` a mod whose DLL is late; a `world` mod can.
- A late DLL (or a native mod) may patch the game's entry point: the proxy puts the entry's own bytes back before the core
  starts, and the patch runs before the game's own entry.
- Play-once with a profile writes `kiln/chain/once.ini` with that profile's `[chain]` and `[late]`, so that start follows its
  own compat rules; the next normal start is back to the active profile's.
- Overlaps: kiln.log `compat: overlap:` lines and `hooks.json` `overlaps[]` (`foreign-before`, `foreign-over`, `refused`) name both
  sides and a fix (`late` or `rule`).
- `<configDir>/compat-rules.json` (after Kiln's `kiln/compat-rules.json`; the same `id` replaces it) rules can `warn`, load a
  chain DLL `late`, or `disable` a chain DLL or a profile-listed mod. The launcher applies `late` and `disable` of chain entries
  when it writes chain.ini; kiln-core logs `warn` rules, skips a disabled profile-listed mod and reports the rest as not applied,
  also `disable` of a native mod whose DLL the proxy already loaded (activate the profile again in the launcher).

## Other engines' mods next to yours (EML, EMF)
- EML: each game file is read at most once per start, and not at all while the OS shows it unchanged (change stamps in
  `kiln/eml/cache/files.json`). The first start after the update still reads all game files once (about 48 GiB on a full client
  install). `KILN_EML_FULL_HASH=1` turns reuse off; set it for one start after a power loss or hard crash. `state/eml.json`
  `fingerprint` and the kiln.log line `[eml] Fingerprints: hashed ... reused ...` show the cost. An EML mod whose `src/mod.lua`
  cannot be read is switched off before the game-data step, with the reason in `state/eml.json` and kiln.log; the others run.
- EMF portraits: `image_load("portrait:<key>")` from a tick or render thread returns a handle at once while the picture is still
  being drawn. It shows nothing until the drawing finishes, then the picture; no retry is needed. Called while the mod's DLL loads, it
  waits (at most 120 s for the whole start). kiln-eml's portrait cache codes: 5 busy and 7 cache I/O error (both tried again at
  the next start), 6 full (delete unused folders in `emf/cache/portraits`). Use kiln-emf and kiln-eml from the same release.
- Most of these paths were verified under Wine and on lab servers, not on a real client: report them as "needs a client check".
