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

## The file
Commented JSONC, written from the schema: each entry has its comment and a meta line (`Default`, `Range`, options,
"Requires a restart", "Synced to clients"). On every load Kiln keeps the user's values, clamps out-of-range numbers
(warning in kiln.log, file corrected), replaces unusable values with the default (warning), adds missing entries,
keeps unknown keys (marked "Unknown key"), and rewrites the file in canonical form only when that changes it. A file
that is not valid JSON is left alone and the previous values stay (`LastError`).

## Offline schema snapshots
`Config.Register` also writes `<configDir>/.schema/<id>.json`: the config's schema without values (format
`schema/config-schema.schema.json`, contracts.md 5.5), so the launcher can edit the file while the game is not
running. It holds `format`, `source: "kiln"`, `file` (`<id>.json`), `valueFormat: "jsonc"`, `id`, `mod`,
`modVersion`, `kind`, `kiln`, `sections[]`, `entries[]` (path, type, comment, default, requiresRestart, synced, min,
max, options), `written` and `schemaHash` (sha256 of the rest). Written atomically, and only when the schema changed:
a reload of the same mod version with the same class keeps the file; a changed class or mod version rewrites it.
`ConfigFile.SchemaSnapshot()` / `SnapshotPath` give the same in code.

Reload: a `FileSystemWatcher` on the config folder (works under Wine; reacts within ~2 steps) and, as a fallback, an
mtime/size poll every 25 steps (~1 s), both on the service tick. Kiln's own writes are not taken for edits.
After a reload with changes: `ConfigFile.Changed` (in the context of the mod that added the handler) and
`ConfigChangedEvent` on the bus (tick thread; `Changed`, `PendingRestart`, `Source` File/Api). Entries marked
`[RequiresRestart]` keep their running value: they show in `PendingRestart` and apply at the next load of the mod.

## Registry (for the dashboard / an in-game menu)
- `Config.All`, `Config.Find(id)`, `Config.Schema()` (JSON array), `Config.Directory`.
- `ConfigFile`: `Id`, `ModId`, `Kind`, `FilePath`, `Entries` (path, type, comment, default, value, file value, min,
  max, options, requiresRestart, synced), `Get(path)`, `TrySet(path, value, out error)` / `Set` (validates, refuses
  out-of-range, writes the file, raises the change; values may be CLR values, `JsonElement`/`JsonNode` or text such as
  "12", "on", "Hard", "5m", "[1, 2]"), `Save()` (after changing `Value` in code), `Reload()`, `Schema()`/`SchemaJson()`,
  `ValuesJson()`, `SyncedValuesJson()`, `PendingRestart`, `Warnings`, `LastError`, `LoadedAt`.
- Threads: `Register`, `Set`, `Save`, `Reload` on the tick thread; reads anywhere (values are plain objects).
- A mod's configs leave the registry when it unloads; the file stays.
