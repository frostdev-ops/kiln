# Commands v2 (`Kiln.Commands`, Brigadier-style)

`managed/services/Kiln.Chat/{Commands,CommandTree,CommandArgs,CommandAttributes,Console}.cs`. The plain registry of
`docs/kiln/services/chat.md` keeps working unchanged; typed commands add parsing, help, completion and permissions.
Tests: `managed/tests/Kiln.Api.Tests/CommandsTests.cs`; lab: `mods/tests/api-test`.

## Declaring
Attribute methods (Kiln registers them on a `Mod` before `OnLoad`; elsewhere `Commands.RegisterAll(obj)`):
```csharp
[Command("heal", Description = "heals a player", Permission = CommandPermission.Admin, Aliases = ["hl"])]
private string Heal(CommandContext c, Player target, [Range(1, 1000)] int amount = 100) => $"healed {target.Name}";

[Command("zone create")]  private void Create(string name, WorldPosition at, double radius = 10) { ... }
[Command("zone delete", Permission = CommandPermission.Console)] private void Delete(string name) { ... }
[Command("say")]          private string Say([Greedy] string text) => text;
[Command("wait")]         private string Wait(TimeSpan time, [TimeOfDay] double until, Mode mode = Mode.Forward) => "...";
```
Parameters: an optional `CommandContext` first, then arguments in order; a default value makes it optional. Return
void, a string (the reply) or a Task (its failure is told to the source). Methods sharing the first word form one
command; a subcommand stricter than the command gets its own node (`<mod>.command.zone.delete`), `Node = "..."`
overrides it.

Builder:
```csharp
Commands.Register(Commands.Literal("give").Describe("gives items")
    .Then(Commands.Argument("player", Arg.Player())
        .Then(Commands.Argument("item", Arg.Item())
            .Executes(c => Give(c.Get<Player>("player"), c.Get<ItemInfo>("item"), 1))
            .Then(Commands.Argument("count", Arg.Int(1, 999)).Executes(c => ...))))
    .Then(Commands.Literal("all").Requires(CommandPermission.Admin).Executes(...)),
    CommandPermission.Everyone);
```

## Argument types (`Arg.*`)
| Factory | Text | Value |
|---|---|---|
| `Int(min,max)`, `Double(min,max)` | `5`, `-2.5` (invariant) | int, double; out of range is an error |
| `Bool()` | on/off, true/false, yes/no, 1/0 | bool |
| `Word()`, `String()`, `Greedy()` | one word / word or "quoted text" / the rest as typed | string |
| `Enum<T>()`, `Choice(...)` | a name, any case | T / string |
| `Player()`, `Players()` | name (exact, else unique prefix), entity id, `@s`, `@p`, `@r`; `Players` also `@a` and filtered selectors | `Player` / `IReadOnlyList<Player>` (a `ChatPlayer` parameter works too) |
| `Entity()`, `Entities()` | entity id, `@s`, `@p`, `@r`, `@e[...]` (one match for `Entity`); `Entities` also comma lists of ids and player names | `EntityId` / `IReadOnlyList<EntityId>` (`EntityId[]` parameters work too) |
| `Position()` | `x y z`, `~` / `~5` relative to the source player | `WorldPosition` |
| `Item()` | name, display name or id (Kiln.Items catalog) | `ItemInfo` |
| `Duration(min,max)` | `90s`, `5m`, `1h30m`, `250ms`, `00:05:00`, seconds | `TimeSpan` |
| `TimeOfDay()` | `7`, `07:30`, `7pm`, `noon`, `dusk` | double hours |

### Target selectors (`@s @a @p @r @e`)
`Selectors.cs` (`EntitySelector`, `Selectors.Select/Entities/Players` for mods). Players come from Kiln.Players,
other entities from Kiln.Spawn's entity map (template name, display name, kind, position).

| Selector | Matches | Default sort / limit |
|---|---|---|
| `@s` | the player running the command | - |
| `@a` | every player in game | arbitrary |
| `@p` | the nearest player (to the source, or `x= y= z=`) | nearest, 1 |
| `@r` | a random player | random, 1 |
| `@e` | every entity, players included | arbitrary |

Filters in brackets, comma separated (spaces inside the brackets are fine: the tokenizer keeps `@e[...]` together):
`type=` a kind (`player creature npc prop pickup other`) or a template name (`Enemy_Vukah_Melee`, wildcards
`Enemy_Vukah*`, or the catalog display name `Vukah`), `name=` a player name (or a template's display name),
`distance=` metres from the origin (`10`, `..10`, `5..`, `5..20`), `x= y= z=` the origin (default: the source
player's position; the console has none, so `@p`, `sort=nearest` and `distance` need them there), `limit=`,
`sort=nearest|furthest|random|arbitrary`. `type` and `name` take `!` (`type=!player`) and may repeat.
Note: `type=creature` uses Kiln.Spawn's name-based kinds, so creature weakspots (`Enemy_Weakspot_*`, child entities)
count as creatures; use a template name to get only the creatures themselves.
Errors are specific: `@e[type=creature] matched 9 entities; add limit=1 (and sort=nearest) for one`, `@p needs a
position: run it as a player in game or give x=,y=,z=`, `no player is online`, `unknown filter 'colour'`.
```
/kill @e[type=Enemy_Vukah*,distance=..30]          /tp @p ~ ~10 ~          /heal @a[distance=..20]
/inspect @e[type=npc,sort=nearest,limit=1]          /give @r[limit=3] Wood 10
```
Lab (events-test, console source with `x= y= z=`): `@e[type=Enemy_Vukah*,distance=..5]` = the two Vukahs spawned
there, `sort=furthest,limit=2`, `type=Vukah` (display name) with `distance=1..9`, `@e[type=npc]` = the 6 villagers
and the base's NPC entities, spaces inside brackets, the error lines above.

Parsing tries subcommands first, then arguments in order, with backtracking; the deepest, most specific error is
reported with the usage: `65 is out of range (whole number 1..64) (<count>). Usage: /give <player> <item> [count] | ...`.
`CommandContext.Get<T>(name)`, `TryGet`, `GetOrDefault`, `Has`, `Values`; the plain `Args`/`Int(i)`/... still work.

## Help, completion, describe
- `/help` lists what the source may use; `/help <cmd> [sub]` shows the description, every usage line the source may
  use (`/zone create <name> <at> [radius]`), the argument types and (for admins/console) the permission node.
- `Commands.Complete(source, "/heal Al")` → `CommandCompletion(Start, Suggestions)`: command names, subcommands, player
  names, enum values, `@s @p @r @a @e[`, `~`, item names (2+ characters), `on/off`, placeholders like `<amount>`.
- `Commands.Describe(source)` / `CommandInfo.ToJson(source)`: name, aliases, description, node, level, owner, usage
  lines and the argument tree (types, ranges, options) for a dashboard.

## Permissions
Every command has a node, default `<owner>.command.<name>`, registered with Kiln.Permissions at the command's level
(`Everyone`, `Admin`, `Console`). Console: always allowed. Remote tools: as before (Everyone, or declared admin).
Players: `Permissions.Has(source, node)` (permissions.json rules, groups, user groups; default by level). Console-level
nodes need a rule naming them exactly. `CommandSource.ForSteamId(id)` runs as a player known by SteamID64 only
(dashboard "run as", tests); `CommandSource.Has(node)`; a `CommandSource` converts to a `PermissionSubject`.

## Events
`CommandExecutingEvent` (cancelable, `CancelMessage`), `CommandExecutedEvent` (result, ms), `ChatMessageEvent`
(cancelable, rewritable). See `events.md`.

## Console folder and `kilnctl cmd`
`kiln/control/console/<name>.cmd` → `<name>.result.json` (checked every 6 steps on the service tick). One command per
line, `//` comments; directives `#as console | #as remote <name> [admin|user] | #as steam <id> [name] | #as player
<name>` switch the source of the following lines; `#complete <text>` returns completions. Result:
`{ file, ok, time, results: [{ line, source, ok, found, denied, canceled, parseError, error, output[] | completions[], running? }] }`.

Who the lines run as: on a dedicated server, the console. In a game that hosts its own world (single player or a
co-op host, where the folder is the only way in when chat is off), once its local player is in the world, the console
*acting as that player*: `source` reads `console as <name> (entity <id>)`, `CommandSource.IsHostConsole` is true,
`Player` is the host's character, so commands that need a player (a position, `@s`, `~`) act on it, and the rights
stay the console's. `#as console` switches back to the plain console.

An async command (a handler returning a Task) gets its outcome in the result when it ends: the file is written then,
with every line it replied and its error, or after 10 s with `"running": true`.

The log line `command /<name> by <source>: <outcome>` says what the command ended with: `ok`, or its error. A refusal
counts as an error only when the handler says so: `c.Fail("...")` or `throw new CommandException("...")`. A reply is a
success, so a command that can't do its job should fail rather than reply.

```sh
target/release/kilnctl cmd <instance|server dir> help apitest      # cargo build --release -p kilnctl
kilnctl cmd ~/srv/enshrouded/labs/api --as steam:76561190000000002:Gus apitest vip     # exit 1: denied
kilnctl cmd ~/srv/enshrouded/labs/api --complete "/apitest m"
kilnctl cmd <instance> --json time set 7                                                # raw result
```
Exit codes: 0 ok, 1 a line failed, 2 no answer within `--timeout` (default 15 s; the service tick needs one loaded mod).

## Kiln.Admin's commands
### `/creative [on|off] [player]`, `/creative status`
Creative mode for one player: the `creative` rules preset (Kiln.Rules: Kiln's own, or the server's
`<configDir>/rules/creative.json`) applied for the session. Node `kiln.command.creative`, level Admin.
- `/creative` toggles it for yourself; `/creative on|off` sets it; a player name does it for that player;
  `/creative status` lists who has it and which hooks work on this build.
- What it gives (Kiln's preset): the game's building fly mode anywhere (the character lands where the flight ends),
  building outside bases and in no-build zones, near enemies and in the Shroud, free building (dismantling gives
  nothing back), tools that break blocks and terrain in one hit, no damage, health, stamina and mana kept full, water
  containers kept full. Not other players' bases: the server's `rules.json` must allow that. It grants no levels, XP,
  skill points or unlocks; it is meant for throwaway or creative worlds.
- Refused for a player whose game has no Kiln with the rules channel (flight and building need it).
- It ends with `/creative off`, when the player leaves, or when Kiln.Admin unloads. A flight outside a base ends at
  once and sets the player down on the ground. Builds placed outside bases stay in the save.
- The same for remote tools: the admin op `creative {entity, on?}`; `rules` lists the active rule sets
  (docs/kiln/launcher/admin-protocol.md).
