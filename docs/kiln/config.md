# Mod configs (`Kiln.Config`, like Forge's `ForgeConfigSpec` + `ModConfig`)

`managed/Kiln.Api/Config*.cs`. Tests: `managed/tests/Kiln.Api.Tests/ConfigTests.cs`; lab: `mods/tests/api-test`.

## Declaring
```csharp
public sealed class MyConfig
{
    [Comment("How many homes a player may set."), Range(1, 50)]
    public int MaxHomes { get; set; } = 3;

    public Difficulty Mode { get; set; } = Difficulty.Normal;    // enums are written by name
    [Range(1, 3600)] public TimeSpan Cooldown { get; set; } = TimeSpan.FromSeconds(30);   // "30s", "5m", "1h30m"
    [RequiresRestart] public int Port { get; set; } = 8080;     // a live edit waits for the next mod load
    public List<string> Banned { get; set; } = ["griefer"];
    public PvpSection Pvp { get; set; } = new();                // a nested class = a section
}

[Comment("PvP rules.")]
public sealed class PvpSection
{
    public bool Enabled { get; set; }
    [Range(0, 10), Synced] public double DamageFactor { get; set; } = 1.0;   // [Synced]: sent to Kiln clients, see docs/kiln/sync.md
    [Range(0, 1, 0.05)] public double Chance { get; set; } = 0.25;          // slider in steps of 0.05 (0, 0.05, 0.1 ...)
    [Range(0, 1, 0.1, SoftMax = true)] public double CostMultiplier { get; set; } = 1;   // slider 0..1, a typed 2 or 3 is kept
}

// OnLoad:
_cfg = Config.Register<MyConfig>();                 // ConfigKind.Common -> <configDir>/<modid>.json
_cfg.Changed += e => Log.Info($"changed: {string.Join(", ", e.Changed)}");
var homes = _cfg.Value.MaxHomes;                    // always the current value (updated in place)
```
- Kinds: `Common` → `<configDir>/<modid>.json`, `Server` → `<modid>-server.json`, `Client` → `<modid>-client.json`;
  `Register<T>(kind, name)` for more files (`<modid>-<name>.json`).
- `<configDir>` is `kiln/config`, or the active profile's `configDir` ([profiles.md](profiles.md)); `Config.Directory`
  tells which. Never build the path yourself (and never derive other folders from it).
- Types: bool, whole numbers, float/double/decimal, string, enums, `TimeSpan`, lists/arrays of those, nested classes.
  Attributes: `[Comment]`, `[Range]` (also DataAnnotations' `RangeAttribute`), `[RequiresRestart]`, `[Synced]`,
  `[ConfigKey("name")]`, `[ConfigIgnore]`. Keys are camelCase member names.
- `[Range(min, max, step)]` (mod API 0.2.17) gives the slider of the in-game config page and the launcher a step:
  it moves from `min` in steps of `step` (`min + n * step`, the last one at or below `max`; `[Range(-0.35, 1.05, 0.1)]`
  gives -0.35, -0.25 ... 0.95). It is editor metadata only: a value typed into the number field, read from the file,
  set with `TrySet` or sent by a server keeps the plain bounds check and is never rounded to the step, and a saved
  value off the step stays as it is until the slider is moved. A command argument's `[Range]` ignores the step.
  `ConfigEntry.Step` and the schemas' `step` carry it only when it suits the entry: an int or float entry (not a list
  or duration), a positive finite step no larger than `max - min` that changes a value of the range, and for whole
  numbers a whole step and a whole `min`. Any other step is ignored and the slider moves as with `[Range(min, max)]`.
  The step is the third argument only: `Step = 0.1` as a named argument does not compile (`Step` is a `double?`,
  which C# does not allow in an attribute's named arguments), so write `[Range(0, 1, 0.1, SoftMax = true)]`.
- `SoftMin = true` / `SoftMax = true` (mod API 0.2.26, kiln#155) make a side of the range soft: the slider of the
  in-game config page and the launcher still stops there, but a value past it, typed into the number field, read from
  the file, set with `TrySet` or sent by a server, is kept as it is: no refusal, no clamp, no warning, and the file is
  not corrected. Only the number's own type still bounds it (an `int` entry stays an int, a `byte` 0..255, a `decimal`
  about ±7.9e28). The other
  side stays hard: `[Range(0, 1, 0.1, SoftMax = true)]` refuses a typed -1 (`"costMultiplier" must be in 0..max (got
  -1)`; the soft side shows the type's bound, `max` for a double) and clamps -1 in the file with the usual warning.
  The slider is given the real value and puts its grab at its end; dragging it gives a value of `min..max` again.
  `ConfigEntry.SoftMin` / `SoftMax` and the schemas' `softMin` / `softMax` (written only when true) carry it; the
  file's meta line reads `Range: 0 .. 1 (slider; higher values allowed)`. Durations and lists of numbers follow the
  same rule (their meta line names no slider: `Range: 1s .. 1m (higher values allowed)`), a command argument has no
  bound on a soft side, and a UI block's `set` action or slider may go past it ([ui-blocks.md](modding/ui-blocks.md)).
  DataAnnotations' `RangeAttribute` has no soft sides.
  A mod that sets them needs mod API 0.2.26 (the SDK writes it; an older Kiln refuses the mod at load instead of
  failing on the attribute). An older launcher ignores the flags and keeps enforcing that side. A joined client whose
  copy of the mod still has that side hard keeps its own value for a `[Synced]` entry the server holds past it (the
  server's value is in `ConfigSyncedEvent.Errors`, docs/kiln/sync.md).

## The file
Commented JSONC, written from the schema: each entry has its comment and a meta line (`Default`, `Range`, options,
"Requires a restart", "Synced to clients"). On every load Kiln keeps the user's values, clamps out-of-range numbers
(warning in kiln.log, file corrected; a value past a soft side is in range), replaces unusable values with the default (warning), adds missing entries,
keeps unknown keys (marked "Unknown key"), and rewrites the file in canonical form only when that changes it. A file
that is not valid JSON is left alone and the previous values stay (`LastError`).

## Offline schema snapshots
`Config.Register` also writes `<configDir>/.schema/<id>.json`: the config's schema without values (format
`schema/config-schema.schema.json`, contracts.md 5.5), so the launcher can edit the file while the game is not
running. It holds `format`, `source: "kiln"`, `file` (`<id>.json`), `valueFormat: "jsonc"`, `id`, `mod`,
`modVersion`, `kind`, `kiln`, `sections[]`, `entries[]` (path, type, comment, default, requiresRestart, synced, min,
max, step, softMin, softMax, options), `written` and `schemaHash` (sha256 of the rest). Written atomically, and only when the schema changed:
a reload of the same mod version with the same class keeps the file; a changed class or mod version rewrites it.
`ConfigFile.SchemaSnapshot()` / `SnapshotPath` give the same in code.

Reload: a `FileSystemWatcher` on the config folder (works under Wine; reacts within ~2 steps) and, as a fallback, an
mtime/size poll every 5 s (every 1 s when the watcher could not start), both on the service tick. Kiln's own writes are not taken for edits.
After a reload with changes: `ConfigFile.Changed` (in the context of the mod that added the handler) and
`ConfigChangedEvent` on the bus (tick thread; `Changed`, `PendingRestart`, `Source` File/Api). Entries marked
`[RequiresRestart]` keep their running value: they show in `PendingRestart` and apply at the next load of the mod.

## In `Mod.OnBoot`
A boot mod (`"boot"` in kiln.mod.json, `KilnBoot` in the SDK) can read its settings in `OnBoot`, before the game's first tick,
and stage boot content with them:
```csharp
public override void OnBoot(BootContext boot)
{
    var altars = Config.Register<Settings>().Value.Altars;   // the file's value, else the default
    BootDefinitions.For(boot).Define("altars", new StatusBlock { Name = "Altars", Seconds = altars });
}
```
- `Register` reads the file and its defaults as in `OnLoad` (values validated and clamped, the same warnings) and `Value`
  holds them. Nothing is written in `OnBoot`: a missing file, the canonical form and the schema snapshot are written when the
  mod's runtime instance registers the same config (in its `OnLoad`).
- That runtime registration takes over what `OnBoot` read: the same text, not read again while the file is unchanged, so
  `OnBoot` and the running mod see the same values. A file edited in between is read again and kiln.log says so ("changed after
  the mod's OnBoot read it"); `OnBoot` used the earlier values.
- The boot content was made with these values: they are frozen with the mod's files. A later load of the mod in the same
  process (after an unload) whose config file now gives other values is refused with `RestartRequiredBoot`, naming the
  changed entries; the same values written another way (Kiln's canonical rewrite included) load
  ([modding/onboot.md](modding/onboot.md#after-a-change-restart)).
- `OnBoot` runs on an instance of the mod that is unloaded when it returns, so `Register` again in `OnLoad`: the object from
  `OnBoot` is not the one the running mod keeps. The config is in `Config.All` from that registration on.
- Refused in `OnBoot`, with "… is not available in OnBoot (it runs before the game's first tick); call it in OnLoad":
  `Set`/`TrySet`, `Save`, `Reload`, adding a `Changed` handler, and `RegisterWorld` (no world exists yet). The same message
  comes from every other tick-thread member called there.
- The build says the same: [KILN035](modding/analyzers.md#kiln035) reports those members in `OnBoot`, and leaves
  `Config.Register` and reading `Value` alone.

## Per world
`Config.RegisterWorld<T>()` (or `Mod.WorldConfig<T>()`) keeps one file of the config for each world, in the world's folder beside
`WorldData`, and the values follow the world that is open: [modding/lifecycle.md](modding/lifecycle.md#per-world-config-worldconfigt).
The file is a server-kind config (`<modid>-world`); `ConfigFile.PerWorld`, `WorldId`; `ConfigChangeSource.World` when another world replaces the values.

## Registry (for the dashboard / an in-game menu)
- `Config.All`, `Config.Find(id)`, `Config.Schema()` (JSON array), `Config.Directory`.
- `ConfigFile`: `Id`, `ModId`, `Kind`, `FilePath`, `Entries` (path, type, comment, default, value, file value, min,
  max, options, requiresRestart, synced), `Get(path)`, `TrySet(path, value, out error)` / `Set` (validates, refuses
  out-of-range, writes the file, raises the change; values may be CLR values, `JsonElement`/`JsonNode` or text such as
  "12", "on", "Hard", "5m", "[1, 2]"), `Save()` (after changing `Value` in code), `Reload()`, `Schema()`/`SchemaJson()`,
  `ValuesJson()`, `SyncedValuesJson()`, `PendingRestart`, `Warnings`, `LastError`, `LastWriteError`, `LoadedAt`.
- A file that cannot be written (a read-only folder, a full disk) is never reported as saved. `TrySet` is all or
  nothing: it returns false with "cannot write" and the reason in `error`, the entry keeps its running value and its
  file value, and no change is raised. `Set` and `Save` do not throw for it, as before 0.2.0-dev.28: the value applies
  for this session and the failure is logged. `LastWriteError` (since 0.2.0-dev.28) says why the last write failed
  and is null after one that succeeded: read it after `Set` or `Save` before you tell a player "saved". A value `Set`
  could not save is written by the next write that succeeds; until then `TrySet` of that same value answers false.
  Reading a config never fails for this: a file Kiln cannot rewrite at registration is only logged.
- Threads: `Register`, `Set`, `Save`, `Reload` on the tick thread (`Register` also in `Mod.OnBoot`, see above); reads anywhere
  (values are plain objects).
- A mod's configs leave the registry when it unloads; the file stays.
