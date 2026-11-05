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
| `Abilities.Natives.Wrap`, `Abilities.OnDamage` | job thread inside the sim step: touch only the call |

Back to the tick thread: `Context.Scheduler.Post(action)` or `await Context.Scheduler.NextTick()`. Check with
`Game.IsTickThread` / `Game.AssertTickThread()`. On a client in the main menu there is no world: `Scheduler.Post`
work waits for the first world step.

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
- `Game`: `KilnVersion`, `IsTickThread`, `HasFeature(name)`, `AssertTickThread()`.
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

Player state (any of 350 states from the game's own enums, `docs/kiln/modding/player-states.md`): `player.Is(PlayerStateId.Soaked)`, `player.IsGliding`, `IsSwimming`, `IsAttacking`..., `player.Snapshot()` (`Health`, `Mana`, `Stamina`, `ShroudTimeLeft`, `Action`, `Active`), `player.Equipment`, `player.Buffs`. Live on the tick thread. Events: `PlayerStateChangedEvent` (any state), `PlayerActionEvent` (any action sequence, `Cancel` stops it), `PlayerConsumeEvent` (`e.Item == GameItems.Food_...`, `Cancel`: item stays), `PlayerConsumedEvent`, `PlayerClientActionEvent` (any client command), `PlayerJumpEvent`, `PlayerGlideEvent`, `PlayerEquipmentChangedEvent`, `PlayerHotbarChangedEvent`. Real players not yet confirmed.

## Items, spawn, world, time, settings, combat
- Items: `Items.Lookup(name)`, `Items.Search`, `await Items.Give(target, id, n, gear, options)`,
  `Items.Read(inventory)`, `await Items.Take(...)`, `Items.FindInventories(kinds)`.
- Spawn: `Spawn.Lookup(template)`, `await Spawn.Entity(template, pos, yaw, options)`, `Spawn.Item`,
  `Spawn.Group`, `Spawn.Inspect`, `Spawn.Destroy`, `Spawn.Tracked`, `Spawn.CleanupTracked(tag)`. Check
  `Spawn.WorldRunning` first.
- World: positions are `WorldPosition` in metres; teleport and entity reads.
- Time: `Time.State`, `Time.Set("19:45")`, `Time.ResetWeather()`. Set the time only after the world runs
  (`ServerStartedEvent`), or the save load overwrites it.
- Settings: `Settings.Get(name)`, `Settings.Set(name, value)`, `TrySet`, `Settings.All`.
- Combat: `Combat.GetHealth`, `SetHealth`, `Heal`, `await Combat.Damage(target, n, attacker)`, `Kill`,
  `SetInvulnerable`; `EntityHurtEvent` (cancel, change `Amount`), `EntityDeathEvent`, ...
- Gameplay: `InteractEvent`, `CraftEvent`, `LootEvent`, `TeleportEvent`, `NaturalSpawnEvent`,
  protection zones (`Gameplay.Protect`).

## Persistent data
`WorldData.Get<T>(key)` (live object: change it in place; saved with the world), `PlayerData.Get<T>(steamId, key)`,
`EntityData.Get<T>(entity, key)` / `Set`. Tick thread. JSON via System.Text.Json. Keys are per mod. Kept across hot
reloads. `WorldSavingEvent` runs before the game's snapshot. Backups: `Saves.Request()`, `Saves.Backup(...)`.
In a hosted world, `OnLoad` sees empty world data until `WorldLoadedEvent`: read per-world data from that event.

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

## Creatures
`Creatures.Template(name)`, `Creatures.Of(entity)` → `CreatureRef` (`Level`, `Faction`, `Scale`, `Resistance[kind]`,
`SpeedFactor`, `DamageFactor`, `XpFactor`, `Loot`, `Features`, `Ai.Set(path, v)`, `Reset()`), `Command.Guard /
Attack / Stay / MoveTo / Heal`. Variants: `CreatureVariants.Register(new CreatureVariant { Id, Base, Level, ... })`,
`await Creatures.Spawn(variantOrTemplate, pos)`, `CreatureVariants.AddSpawnRule(...)`. Setters are layers of your mod.

## Abilities
`Abilities.Register(new AbilityDefinition { Id = "mymod:name", DefaultKey = "G", ChatCommand, Cost, Cooldown,
CastTime, Targeting, OnCast = async cast => { await cast.Impact(template, pos, configs); } })`,
`Abilities.Cast(actor, id)`, `ImpactStartingEvent` (change configs or cancel before a game impact runs; narrow with
`Abilities.HoldPrograms(ids)`), `await Abilities.RunImpact(...)`, `Abilities.PlaySequence(actor, guid)`,
`await Abilities.ApplyBuff(target, buffGuid, duration, caster)`, `Abilities.Attributes.Get/Add`,
`Abilities.Natives.Register(name, argc, call => ...)` for Impact programs. A Kiln impact has no used item: give it
its own `Damage1` config or it hits for 0.

## World edits
`await Build.Dig / Add / Flatten / Smooth / Paint / Replace(BuildShape.Sphere|Box|Cylinder(...))`,
`Build.Read/Write(box, cells)`, `Build.PlaceProp(template, pos, rotation)`, `Schematic.CaptureAsync`, `.Paste`,
`await Build.PlaceOnce(key, schematic, at)`, `await Build.Undo()`. Results carry `Refusal` (zone, base, not loaded).
Vanilla clients see the edits and the game saves them.

## Game UI, HUD, keys
`Ui.Menus.Add(MenuLocation.MainMenu|PauseMenu, text, onClick)`, `Ui.Screen(title, ui => { ... }, options)` →
`Open/Close/Toggle`, `Ui.Crafting.AddTab / AddSubcategory / AddPanel`, `Ui.Screens.Current`, `Hud.Show(id, text)`,
`Hud.Toast(text)`, `KeyBindings.Register(id, "F8", action, description)`, `UiText.Of(("en", ...), ("de", ...))`.
`Ui.Available` is false on a dedicated server.

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
