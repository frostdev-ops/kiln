# OnBoot: content that is there on the first start

Kiln's services collect some of the game's content before any mod's `OnLoad` runs: the programs behind statuses,
effects and spells, the skill tree, VFX, the world tables. What a mod defines in C# after that comes too late for the
first start; Kiln keeps it, and the game has it from the next start (data files and baked files aside:
[baked.md](baked.md)). A server's first start with a new mod is then not quite the real thing.

`Mod.OnBoot` runs earlier: before those collectors and before the game starts. What a mod stages there is checked
together with every mod's files and handed to the game in one piece, before the game reads any of it. The first start
has it, with the same ids as a definition made in `OnLoad`.

Since 0.2.0-dev.28, on dedicated servers. The native side is kiln.boot ([../services/boot.md](../services/boot.md)).

## Turning it on
Add `boot` to `kiln.mod.json` ([manifest.md](manifest.md#boot-content-boot-since-020-dev28)):

```jsonc
{
  "id": "frost-tokens",
  "boot": true            // or "required"
}
```

- `true`: the mod's `OnBoot` runs. If its boot content fails, only this mod (and the mods that require it) is left out;
  the server starts without them.
- `"required"`: the same, but a failure stops the start (below). Use it when a server without your content would be
  worse than no server: a world that already holds your items, for example.

A Kiln.Sdk project generates its manifest. To add `boot`, keep your own `kiln.mod.json` beside the project file: the
build checks it and ships it.

## What OnBoot is
```csharp
public override void OnBoot(BootContext boot) { ... }
```

- It runs **once per process**, before the game's entry point, on Kiln's boot thread.
- It runs on a **throwaway copy** of your mod: Kiln loads the mod's assembly on its own, creates an instance, calls
  `OnBoot`, and unloads it all again. The instance that later gets `OnLoad` is a new one. Nothing you keep in a field or
  a static survives: hand everything over by staging it. What `OnBoot` read (`boot.GameData`) cannot be handed to
  `OnLoad` in memory either: write what you need later into a file of your mod's data folder (`Context.DataDirectory`
  is the same folder in `OnBoot` and in `OnLoad`; create it first), or read it again through the run-time services.
- `boot.ModId`, `boot.Generation` (the process's boot generation), `boot.Roles` (where the mod runs: a dedicated
  server in this build) and `boot.GameData` (the game's data, read only, below).

**What you may do:** stage definitions with `BootDefinitions.For(boot)` (Kiln.Blocks), read `boot.GameData`, log
(`Log`), read your mod's own files (the folder is `Context.Info.Directory`).

**What you may not do:** anything that needs the running game. There is no tick, no world, no player and no live
registry yet. `Blocks.Define` throws `InvalidOperationException` ("PhaseClosed: ... stage the block with
BootDefinitions.For(boot).Define"). Run-time services (`World`, `Players`, `Content`, `Items`, ...) are not there to
call.

## Staging definitions
```csharp
var defs = BootDefinitions.For(boot);
var r = defs.Define("chill", new StatusBlock { Name = "Chill", Seconds = 6 });
defs.DefineJson("items", "frost-token", """{ "name": "Frost Token", "stack": 50 }""");
```

- `Define(name, block)` copies the block at once. It must be plain data: a delegate or a stream anywhere in it is
  refused (`ArgumentException`). Its type must be exactly one of the kinds below, not a subclass.
- `DefineJson(kind, name, json)` takes the block as it would stand in `blocks/*.json` under `<kind>.<name>`.
- A refusal is a result, not an exception: `Ok` false with `Reason` starting with a code.
  - `DuplicateDefinition`: the same kind and name was staged already with other content. The same content again is fine.
  - `PhaseClosed`: `OnBoot` has returned. Stage inside `OnBoot` only.
  - `TooMany`: more than 4096 definitions.
  - `NotInBootKinds`: a kind this build does not stage (below). Define it in `OnLoad` or a data file instead.
- `Ok` means staged, not committed. `Mods.BootState` tells you later whether it made it in.

**The kinds this build stages:** statuses, effects, items, spells, talents, and resource patches
([resource-patches.md](resource-patches.md)). Every other kind (creatures, NPCs, materials, props, stations, quests,
VFX, ...) answers `NotInBootKinds`.

**Names and limits:** a name is `^[a-z][a-z0-9_.-]{0,95}$`, without `..`. One mod stages at most 4096 definitions; one
definition is at most 1 MiB as JSON.

**References:** a boot definition can name a definition the same mod staged before it (`OnHit = ["chill-touch"]` in the
example). It cannot name another mod's block, or a block of your own data files: each mod's boot definitions are
compiled on their own, before any block is registered. Such a definition does not compile, and the mod is left out, with
the block error `BootReferenceOutside` in kiln.log and `state/boot.json`, for example: `BootReferenceOutside: "other-mod:frost"
is a block of mod other-mod; a boot definition names only blocks its own OnBoot staged before it`, or `BootReferenceOutside:
"frost" is a block of this mod's data files (blocks/*.json)`. Stage the block in the same `OnBoot`, before the definition
that names it.

**A data file wins.** If your `blocks/*.json` defines the same kind and name, the file's block is used and the staged
one is dropped, with a warning (`ShadowedByFile`) in kiln.log and `state/boot.json`.

## Reading the game's data
`boot.GameData.Read(key)` reads one record of the game's data as shipped, from a snapshot made before the start
([boot-snapshot.md](boot-snapshot.md)): `item:<debugName>`, `template:<debugName>`, `skill:0x<8 hex digits>`,
`material:<name>`, `resource:<GUID>/<type hash>/<part>`. It answers `BootDataResult`, with the record as camelCase JSON.

- Without a snapshot of this game build, `mods.boot.game_data` is unavailable and `Read` throws
  `KilnUnavailableException`. The row's reason says what to run.
- One record is at most 4 MiB; one mod reads at most 16 MiB in all.
- It shows the game's own values: not another loader's patches, and not resource patches (they are written later).
- The format and the producer are in [boot-gamedata.md](boot-gamedata.md).

## Reading your mod's settings
In this build `Config.Register<T>()` cannot be called in `OnBoot`: it needs the tick thread and throws
`InvalidOperationException` ("This must run on the tick thread"), and the mod is then left out. Keep values `OnBoot`
needs in code or in a data file of the mod for now.

## When a mod fails
- **`OnBoot` throws**, or a staged definition does not compile: the mod is out. None of its definitions and none of its
  files are in the generation, and every mod that requires it is out too. The server starts without them.
- **A collector refuses a definition** (a resource patch on a field another mod sets, for example): the same.
- An excluded mod does not load later in this process either: kiln.log says `RestartRequiredBoot: ... restart after
  fixing it`. Fix it and restart the server.
- **A mod with `"boot": "required"`** that fails stops the start. The game is never entered: no world or save is loaded.
  The process ends with exit code `0x4B01`; on Linux, under Wine, the exit status is its low byte, 1. kiln.log says
  `boot: the start stops before the game's entry point (exit code 0x4B01)`, and `kiln/state/boot.json` names the mod,
  the definition and the reason. A required mod's resource patch that cannot be written when the game's data comes up
  stops the start the same way.
- A mod with `boot` that was enabled after the start (`MissedPhase`), or whose manifest or assembly changed since, does
  not load until the next start.

`kiln/state/boot.json`, section `boot`: whether the phase ran (`deferred`), the boot mods, the collectors, each mod's
state and definition count, every issue (mod, kind, key, phase, code, reason) and the exit code when the start stopped.

## Afterwards
- `Mods.BootState(modId)` (any thread) gives the mod's state, its committed definitions as `kind/key`, and its issues.
  `Committed` means its definitions are in the game. `NotParticipating` means no boot phase took it.
- `BootDefinitionsCommittedEvent` goes out once each time the mod's run-time instance loads, on Kiln.Blocks' next step
  after `OnLoad`, when its committed definitions are active (their run-time parts: rules, icons). Subscribe in
  `OnLoad`. A mod whose `OnBoot` staged nothing gets the event too, with no definitions.
- The staged blocks keep their names: `mod:name` from another mod, as for a block defined in `OnLoad`.

## Where it runs
- **Dedicated servers.** The phase runs when hooks and .NET are on, an enabled mod of the server says `boot`, and
  `mods.boot` is available.
- **Game clients** hold `mods.boot.client` in this build, until its guided client checks pass. On a game client a mod
  with `boot` does not load (`RestartRequiredBoot: ... this process ran no boot phase`). `"preview": true` in
  `kiln/kiln.json` lifts the hold, for testing.
- A server where the phase does not run (safe idle, .NET off) does not load boot mods either. The row `mods.boot.phase`
  says why.

## Testing
Unit tests of `OnBoot` without a game: the test kit's boot runner, `KilnHarness.BootPhase` ([unit-tests.md](unit-tests.md#boot-phase-since-020-dev28)).
`kiln.BootPhase.SetRecord("item:Material_T1_Wood_Logs", """{"maxStackSize": 250}""")` scripts what `boot.GameData` reads;
`var run = kiln.BootPhase.Run(modFolder)` runs your `OnBoot` as a start does and gives what it staged (`run.Staged`: kind,
key and the JSON Kiln lowers), what lowered (`run.Definitions`), and why it failed (`run.Reason`, `run.Issues`). Then
`kiln.LoadFolder(modFolder)` loads the mod with its boot content counted as committed. The SDK's bake runs `OnBoot` the
same way and fails the build when it fails ([baked.md](baked.md)).

## A complete example
`kiln.mod.json` beside the project, with `"boot": true`:

```jsonc
{ "id": "frost-tokens", "name": "Frost Tokens", "version": "1.0.0", "apiVersion": "0.2",
  "side": "both", "clients": "kiln", "entryAssembly": "FrostTokens.dll", "boot": true }
```

`FrostTokens.csproj` with `<KilnServices>Blocks</KilnServices>`, and:

```csharp
using Kiln;

namespace FrostTokens;

public sealed class FrostTokensMod : Mod
{
    private const string Id = "frost-tokens";

    // Before the game starts, on a copy of the mod that is thrown away afterwards.
    public override void OnBoot(BootContext boot)
    {
        var defs = BootDefinitions.For(boot);
        Stage(defs.Define("chill", new StatusBlock { Name = "Chill", Seconds = 6 }));
        Stage(defs.Define("chill-touch", new EffectBlock { Applies = new StatusApply { Status = "chill" } }));
        Stage(defs.Define("frost-token", new ItemBlock { Name = "Frost Token", Stack = 50 }));
        Stage(defs.Define("frost-dart", new SpellBlock
        {
            Name = "Frost Dart",
            Cast = new CastBlock { ManaCost = 4 },
            Delivery = new ProjectileBlock { OnHit = ["chill-touch"] },
        }));
    }

    // A refusal is a result: throw, and the whole mod stays out rather than half of it.
    private static void Stage<T>(BootDefinitionResult<T> result) where T : class, IBlock
    {
        if (!result.Ok) throw new InvalidOperationException(result.Reason);
    }

    // The mod's run-time instance: a new one.
    public override void OnLoad()
    {
        var state = Mods.BootState(Id);
        Log.Info($"boot: {state.State}, {state.Definitions.Count} definition(s) in generation {state.Generation}");
        Events.On<BootDefinitionsCommittedEvent>(e =>
        {
            if (e.ModId == Id) Log.Info($"boot definitions active: {string.Join(", ", e.Definitions)}");
        });
    }
}
```

The lab mod `mods/tests/boot-definitions-test` does the same and checks each kind on the first start.
