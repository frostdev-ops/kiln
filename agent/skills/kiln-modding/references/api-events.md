# The mod API: events, config, commands and players

Part of the mod API guides ([api.md](api.md) is the index). Here: events, configs, commands and permissions, chat, players
and progression, player rules and NoClip. Signatures are the common ones; the XML docs and the generated API reference
([docs-map.md](docs-map.md)) have the rest.

## Events
```csharp
[Subscribe] private void OnJoin(PlayerJoinedEvent e) => Log.Info($"{e.Player.Name} joined");
[Subscribe(EventPriority.High, ReceiveCanceled = true)] private void OnChat(ChatMessageEvent e) { e.Cancel(); }
var sub = Events.On<CommandExecutedEvent>(e => { });   // sub.Dispose() to stop
Events.Post(new MyEvent());        // tick thread; Events.Enqueue(ev) from any thread
```
dev.27+ filters: `Events.On<EntityHurtEvent>(OnHit, EventFilter.ForEntity(dummy))` (or `ForEntities`) delivers only the
events about those entities; it works for every `IEntityEvent` (the combat events, `ComponentWrittenEvent`). A hit is
about an entity as its target, the part hit, its attacker or its source, so a creature's filter also gets the hits it
deals: test `e.Target == creature` for the hits it takes. A filtered hit handler no longer has every hit in the world
held for it. `Events.On<T>(handler, default)` no longer compiles (priority or filter?): write `EventPriority.Normal`.
Priorities `Highest, High, Normal, Low, Lowest, Monitor`. `CancelableEvent.Cancel()`, `ResultEvent` (`Allow`,
`Deny`). Build your own events only when `Events.HasSubscribers<T>()`. Each service page lists its events with
thread, cancel meaning and writable fields: read it before relying on one. Some player-facing events are built but not yet
confirmed with a real player. dev.29+ events a mod can refuse (`EntityDyingEvent`, `ItemPickingUpEvent`,
`PlayerEquippingEvent`) and component watches: [gameplay.md](gameplay.md).

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
`[Range(0, 1, 0.1, SoftMax = true)]` (API 0.2.26): slider 0..1 in 0.1 steps, a typed or file value above 1 is kept
(`SoftMin` likewise); the step is the third argument (`Step = 0.1` does not compile).
Commented JSONC, validated, clamped, hot-reloaded on edit. Never build the config path yourself: `Config.Directory`.
dev.28+: `ConfigFile.TrySet` returns false with the reason when the file cannot be written (`ConfigFile.LastWriteError`);
before, it returned true. `Config.Register<T>()` and `.Value` also work in `OnBoot`.
**dev.20+:** `Config.RegisterWorld<T>()` keeps one config per world in its sidecar; values switch before `WorldLoadedEvent`
and use defaults with no world. `[RequiresRestart]` edits apply when that world next opens. `Context.Locale.Get(key, args)`
reads this mod's `locales/<language>.json`, falling back to English/key; use it for mod text, not game-tag lookup.
dev.29.19+ `KilnLanguage` (Kiln.Api, any thread) for a mod whose pages should read in Kiln's UI language, not the game's:
`Current` (a lower-case tag, `"zh-cn"`), `Setting` (`ui.language`: `"auto"` or the tag), `OverlayInEnglish` (Kiln's overlay
cannot draw that script yet: show English too), event `Changed` (on the thread that changed it; keep it short; dropped at
unload). `Context.Locale.GetIn(KilnLanguage.OverlayInEnglish ? "en" : KilnLanguage.Current, key)`.

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
built in. Console: `kiln/control/console/*.cmd`. Commands run on the tick thread: dev.29.18+ KILN105 warns about any
blocking wait in a `[Command]` method, a `Commands.Register`/`Executes` handler or a `Chat.Received` handler.

## Chat
`Chat.Received += m => { m.Cancel = true; }`, `Chat.Broadcast(text)`, `Chat.Tell(player, text)`,
`Chat.Say(player, text, to)`, `Chat.Players`, `Chat.FindPlayer(name)`. Tick thread for sends.
dev.29.17+: a `/` line that names no Kiln command passes through untouched to the game and other mods (other loaders'
slash commands work). `"unknownCommands": "reply"` in `kiln/config/chat.json` brings back Kiln's "Unknown command";
`Commands.UnknownCommands` reads or sets it. Page `docs/kiln/services/chat.md`.

## Players and progression
`Players.Available`, `Players.All`, `Players.Find(name | EntityId)`, `FindBySteamId`, `PositionOf`,
`Players.Joined/Left` (`Action<Player>`). Progression: `player.Progression.Read()`, `AddExperience(n, source)`,
`SetLevel`, `GiveSkillPoints(n)` (up to the game's givable flags), `Unlock(node)`, `Lock`, `Respec()`;
`PlayerLevelChangedEvent`, `PlayerSkillsChangedEvent`. Changes return `Task<ProgressionResult>`; game refusals come
back as `Ok = false`, not exceptions. Level cap 50. New nodes and trees: [talents](quests-talents-sounds.md).

**Admin (dev.27+; test connections only, no real player's game).** `await Players.Kick(playerRef, reason)` /
`Network.Kick(peer, reason)` → `KickResult`; `PlayerKickedEvent`. `Bans.Add(playerRef | steamId, reason, duration?)`,
`Bans.Remove(id)`, `Bans.Find(id)`, `Bans.All` (Kiln's own list, `kiln/data/kiln-players/bans.json`, enforced at
login); `PlayerBannedEvent`, `PlayerUnbannedEvent`. Capabilities `players.kick`, `players.bans`. `PlayerLoginEvent`
comes once per connection when it names its player (`Name`, `SteamId`, `HasKiln`): set `Reason` and `Cancel()` to
refuse it. A player with Kiln reads the reason; one without gets the game's own "could not join".

Player state (any of 350 states from the game's own enums, `docs/kiln/modding/player-states.md`): `player.Is(PlayerStateId.Soaked)`, `player.IsGliding`, `IsSwimming`, `IsAttacking`..., `player.Snapshot()` (`Health`, `Mana`, `Stamina`, `ShroudTimeLeft`, `Action`, `Active`), `player.Equipment`, `player.Buffs`. Live on the tick thread. Events: `PlayerStateChangedEvent` (any state), `PlayerActionEvent` (any action sequence, `Cancel` stops it), `PlayerConsumeEvent` (`e.Item == GameItems.Food_...`, `Cancel`: item stays), `PlayerConsumedEvent`, `PlayerClientActionEvent` (any client command), `PlayerJumpEvent`, `PlayerGlideEvent`, `PlayerEquipmentChangedEvent`, `PlayerHotbarChangedEvent`. Check the dated wiki status for player-facing verification.
dev.27+: `PlayerStates.AimOf(player)` → `PlayerAim?` (`Origin`, `Direction`, `At(metres)`; null before the player's game
sent a camera; capability `players.aim`). It is the camera, behind the character: a "what does the player look at"
ray starts at `Origin`.

## Player rules and NoClip (Kiln.Rules)
`Rules.Apply(player | RuleScope, new PlayerRules { ... }, duration)` → a `RuleSet` (`Update(rules)`, dispose to end);
`Rules.Of(player)` reads what counts now (dev.27+ also right after `Update`). Rules of several sets combine; a player's
`Water` rule refills containers every step (see [tracked world writes](api-world.md#tracked-world-writes-dev27)).
dev.27+ **NoClip** (one real game: switched on and off; death, teleport and other players' view not tried):
`PlayerRules.NoClip = true` in a rule set, or Kiln's own switch `NoClip.Set(player, on)` → `NoClipResult` (`Ok`, `On`,
`Reason`). `NoClip.Of(player)` (`On`, `Active`, `LastFree`, `Restoring`), `NoClip.Available`, capability
`rules.noclip`. The character flies through terrain, blocks and props, with no other grant (no invulnerability). Off on
every server until its `kiln/config/rules.json` has `"allowNoClip": true` (refused with a reason); the player's game needs
Kiln dev.27. A death or a teleport ends it, and it must be asked for again; `PlayerNoClipEndedEvent` (`Reason`:
`RuleEnded`, `Death`, `Teleport`, dev.28+ `Left`: the player left in NoClip and is put at the last free position next
time; `RestoredTo` when Kiln moved the character back to open space first). Does nothing while the Creative Mode mod
flies the character. dev.29+ fixed `/noclip` and `rules.json` handling from player reports.
