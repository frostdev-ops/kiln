// SPDX-License-Identifier: MIT
// Typed mod configs (Forge's ForgeConfigSpec / ModConfig): a mod declares a class, Kiln writes it as commented JSONC
// to <configDir>/<id>.json (kiln/config unless the profile names another configDir), a schema snapshot to
// <configDir>/.schema/<id>.json, keeps the user's values, validates and clamps them, reloads the file while the server runs
// and keeps a registry the dashboard can edit. Usage: docs/kiln/config.md.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>What changed a config.</summary>
public enum ConfigChangeSource
{
    /// <summary>Someone edited the file.</summary>
    File = 0,
    /// <summary><see cref="ConfigFile.Set"/> (a mod, Kiln.Admin, a command).</summary>
    Api = 1,
    /// <summary>Client side: values of <see cref="SyncedAttribute"/> entries arrived from the server (Kiln.Network's
    /// config sync), or the local values came back after leaving it.</summary>
    Server = 2,
    /// <summary>A per-world config (<see cref="Config.RegisterWorld{T}"/>): the world changed, so the values of the new
    /// world's file (or the defaults) replaced the old world's.</summary>
    World = 3,
}

/// <summary>A config's values changed while the server runs (not raised for the first load). Posted on the tick
/// thread, after the new values are in place. Not cancelable.</summary>
public sealed class ConfigChangedEvent : KilnEvent
{
    internal ConfigChangedEvent(ConfigFile config, IReadOnlyList<string> changed, IReadOnlyList<string> pending,
        ConfigChangeSource source)
    {
        Config = config;
        Changed = changed;
        PendingRestart = pending;
        Source = source;
    }

    public ConfigFile Config { get; }
    public string ModId => Config.ModId;

    /// <summary>Key paths ("pvp.enabled") whose running value changed.</summary>
    public IReadOnlyList<string> Changed { get; }

    /// <summary>Key paths marked <see cref="RequiresRestartAttribute"/> whose file value changed: the running value
    /// stays until the mod reloads.</summary>
    public IReadOnlyList<string> PendingRestart { get; }

    public ConfigChangeSource Source { get; }

    public bool Has(string path) => Changed.Contains(path, StringComparer.OrdinalIgnoreCase);

    public override string ToString() =>
        $"{Config.Id}: {string.Join(", ", Changed)}{(PendingRestart.Count > 0 ? $" (after restart: {string.Join(", ", PendingRestart)})" : "")}";
}

/// <summary>One entry of a config, as of now. <c>Path</c> is the dotted key path ("pvp.enabled"); <c>Type</c> is
/// "bool", "int", "float", "string", "enum", "duration" or "list&lt;...&gt;"; <c>Value</c> is the running value and
/// <c>FileValue</c> the value in the file (they differ while a restart is pending).</summary>
public sealed record ConfigEntry(string Path, string Type, string? Comment, object? Default, object? Value, object? FileValue,
    double? Min, double? Max, IReadOnlyList<string> Options, bool RequiresRestart, bool Synced)
{
    /// <summary>The running value came from the server (client side, while connected; see
    /// <see cref="ConfigFile.IsFromServer"/>). <c>FileValue</c> is then this game's own value, which comes back on leave.</summary>
    public bool FromServer { get; init; }

    public bool PendingRestart => !FromServer && !ConfigSchema.ValueEquals(Value, FileValue);
}

/// <summary>A registered config file (see <see cref="Config"/>). Members that change state run on the tick thread.</summary>
public abstract class ConfigFile
{
    private readonly List<ConfigNode> _nodes;
    private readonly List<(ConfigNode Node, ConfigNode[] Chain)> _values = new();
    private readonly Dictionary<string, (ConfigNode Node, ConfigNode[] Chain)> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object?> _file = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<(string Key, string Raw)>> _unknown = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(Action<ConfigChangedEvent> Handler, ModContext? Owner)> _handlers = new();
    // Client side, while connected to a Kiln server: [Synced] entries whose running value is the server's (path ->
    // the server's value). Tick thread; _serverPaths is the snapshot other threads read.
    private readonly Dictionary<string, object?> _server = new(StringComparer.OrdinalIgnoreCase);
    private volatile string[] _serverPaths = [];
    private string? _lastText;
    private string? _lastBadText;
    internal (long Ticks, long Length) Stamp;
    internal readonly ModContext? Owner;

    private protected ConfigFile(ModContext? owner, string id, ConfigKind kind, Type type, object value, bool perWorld = false)
    {
        Owner = owner;
        Id = id;
        ModId = owner?.Info.Id ?? "kiln";
        Kind = kind;
        Type = type;
        ValueObject = value;
        PerWorld = perWorld;
        FilePath = perWorld ? "" : System.IO.Path.Combine(KilnPaths.Config, id + ".json");
        var defaults = Activator.CreateInstance(type)!;
        _nodes = ConfigSchema.Build(type, defaults);
        Collect(_nodes, []);
    }

    private void Collect(List<ConfigNode> nodes, ConfigNode[] chain)
    {
        foreach (var n in nodes)
        {
            if (n.IsSection) Collect(n.Children, [.. chain, n]);
            else
            {
                _values.Add((n, chain));
                _byPath[n.Path] = (n, chain);
            }
        }
    }

    /// <summary>The file name without ".json": "&lt;modid&gt;", "&lt;modid&gt;-server", "&lt;modid&gt;-&lt;name&gt;".</summary>
    public string Id { get; }

    /// <summary>The mod that registered it ("kiln" for Kiln's own).</summary>
    public string ModId { get; }

    public ConfigKind Kind { get; }

    /// <summary>The config class.</summary>
    public Type Type { get; }

    /// <summary>Full path of the JSONC file. A per-world config (<see cref="PerWorld"/>) has the file of the world that
    /// is open now, and an empty path while no world is (its values are the defaults, in memory only).</summary>
    public string FilePath { get; internal set; }

    /// <summary>The config belongs to a world (<see cref="Config.RegisterWorld{T}"/>): its file lies in the world's
    /// sidecar folder beside <c>WorldData</c> (<c>kiln/data/worlds/&lt;world id&gt;/config/&lt;id&gt;.json</c>) and its values
    /// follow the world that is open.</summary>
    public bool PerWorld { get; }

    /// <summary>Per-world configs: the id of the world whose file is loaded, or null (no world open yet).</summary>
    public string? WorldId => WorldDirectory is { } d ? System.IO.Path.GetFileName(d) : null;

    /// <summary>Per-world configs: the world's sidecar folder, or null.</summary>
    internal string? WorldDirectory { get; private set; }

    /// <summary>The live config object (the mod's <c>Value</c>).</summary>
    internal object ValueObject { get; }

    /// <summary>When the file was last read (UTC); null before.</summary>
    public DateTime? LoadedAt { get; private set; }

    /// <summary>Why the last read of the file was refused (bad JSON), or null.</summary>
    public string? LastError { get; private set; }

    /// <summary>Corrections made at the last read (out-of-range values clamped, bad values replaced).</summary>
    public IReadOnlyList<string> Warnings { get; private set; } = [];

    /// <summary>Raised on the tick thread after values changed (file edit or <see cref="Set"/>), in the context of the
    /// mod that added the handler. Also posted on the bus as <see cref="ConfigChangedEvent"/>.</summary>
    public event Action<ConfigChangedEvent>? Changed
    {
        add
        {
            if (value is null) return;
            var mod = ModContext.CurrentMod;
            lock (_handlers) _handlers.Add((value, mod));
            if (mod is not null && !ReferenceEquals(mod, Owner))
                mod.Retiring += () =>
                {
                    lock (_handlers) _handlers.RemoveAll(h => ReferenceEquals(h.Owner, mod));
                };
        }
        remove
        {
            if (value is null) return;
            lock (_handlers)
            {
                var i = _handlers.FindLastIndex(h => h.Handler == value);
                if (i >= 0) _handlers.RemoveAt(i);
            }
        }
    }

    /// <summary>Every entry with its running and file value.</summary>
    public IReadOnlyList<ConfigEntry> Entries =>
        _values.Select(v => Entry(v.Node, v.Chain)).ToArray();

    /// <summary>Key paths waiting for a restart (file value differs from the running value).</summary>
    public IReadOnlyList<string> PendingRestart => Entries.Where(e => e.PendingRestart).Select(e => e.Path).ToArray();

    /// <summary>Client side: the key paths whose running value is the server's ([Synced] entries of a server or common
    /// config, while connected to a Kiln server). They are read-only meanwhile; the local values come back on leave.
    /// Any thread.</summary>
    public IReadOnlyList<string> ServerValues => _serverPaths;

    /// <summary>Whether <paramref name="path"/>'s running value is the server's (see <see cref="ServerValues"/>).</summary>
    public bool IsFromServer(string path) => Array.Exists(_serverPaths, p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>The running value at <paramref name="path"/>. <exception cref="KeyNotFoundException">unknown path</exception></summary>
    public object? Get(string path)
    {
        var (node, chain) = Find(path);
        return node.Get(Holder(chain));
    }

    /// <summary>Like <see cref="TrySet"/>; throws <see cref="ArgumentException"/> when the value is refused.</summary>
    public void Set(string path, object? value)
    {
        if (!TrySet(path, value, out var error)) throw new ArgumentException(error, nameof(value));
    }

    /// <summary>Changes one entry: validates it (out of range is refused, not clamped), applies it (unless it
    /// requires a restart: then only the file changes), writes the file and raises <see cref="Changed"/>.
    /// <paramref name="value"/>: a CLR value, a <see cref="JsonElement"/>/<see cref="JsonNode"/> or text ("12",
    /// "on", "Hard", "5m", "[1, 2]"). Tick thread.</summary>
    public bool TrySet(string path, object? value, out string error)
    {
        Game.AssertTickThread();
        if (!_byPath.TryGetValue(path ?? "", out var entry))
        {
            error = $"{Id} has no entry '{path}'";
            return false;
        }
        var (node, chain) = entry;
        if (_server.ContainsKey(node.Path))
        {
            error = $"{Id}: '{node.Path}' is the server's value while you are connected (synced); it can be changed after leaving";
            return false;
        }
        if (!ConfigSchema.FromObject(node, value, out var v, out error)) return false;
        var holder = Holder(chain);
        var running = node.Get(holder);
        var before = _file.GetValueOrDefault(node.Path);
        if (ConfigSchema.ValueEquals(before, v) && (node.RequiresRestart || ConfigSchema.ValueEquals(running, v))) return true;
        _file[node.Path] = ConfigSchema.Copy(node, v);
        IReadOnlyList<string> changed = [], pending = [];
        if (node.RequiresRestart && !ConfigSchema.ValueEquals(running, v)) pending = [node.Path];
        else if (!ConfigSchema.ValueEquals(running, v))
        {
            node.Set(holder, v);
            changed = [node.Path];
        }
        Write();
        Log(LogLevel.Info, $"config {Id}: {node.Path} = {ConfigSchema.Format(node, v)}{(pending.Count > 0 ? " (applies after a restart)" : "")}");
        Raise(changed, pending, ConfigChangeSource.Api);
        return true;
    }

    /// <summary>Writes the running values into the file (after the mod changed its <c>Value</c> in code). Entries
    /// waiting for a restart keep their file value. Tick thread.</summary>
    public void Save()
    {
        Game.AssertTickThread();
        foreach (var (node, chain) in _values)
        {
            if (_server.ContainsKey(node.Path)) continue;  // the server's value: the file keeps this game's own
            var running = node.Get(Holder(chain));
            var pending = node.RequiresRestart && _file.TryGetValue(node.Path, out var fv) && !ConfigSchema.ValueEquals(fv, running);
            if (!pending) _file[node.Path] = ConfigSchema.Copy(node, running);
        }
        Write();
    }

    /// <summary>Reads the file now (Kiln also does this by itself when the file changes). Tick thread.</summary>
    public void Reload()
    {
        Game.AssertTickThread();
        _lastText = null;
        Load(initial: false);
    }

    /// <summary>The schema and values as JSON (for the dashboard and an in-game editor).</summary>
    public JsonObject Schema()
    {
        var entries = new JsonArray();
        foreach (var (node, chain) in _values)
        {
            var e = Entry(node, chain);
            var o = new JsonObject
            {
                ["path"] = e.Path,
                ["type"] = e.Type,
                ["comment"] = e.Comment,
                ["default"] = ConfigSchema.ToJson(node, e.Default),
                ["value"] = ConfigSchema.ToJson(node, e.Value),
                ["requiresRestart"] = e.RequiresRestart,
                ["synced"] = e.Synced,
            };
            if (e.PendingRestart || e.FromServer) o["fileValue"] = ConfigSchema.ToJson(node, e.FileValue);
            if (e.FromServer) o["fromServer"] = true;
            if (e.Min is { } min) o["min"] = min;
            if (e.Max is { } max) o["max"] = max;
            if (e.Options.Count > 0) o["options"] = new JsonArray([.. e.Options.Select(x => (JsonNode)x)]);
            entries.Add(o);
        }
        var sections = new JsonArray();
        foreach (var s in Sections(_nodes)) sections.Add(new JsonObject { ["path"] = s.Path, ["comment"] = s.Comment, ["synced"] = s.Synced });
        return new JsonObject
        {
            ["id"] = Id,
            ["mod"] = ModId,
            ["kind"] = Kind.ToString().ToLowerInvariant(),
            ["file"] = FilePath,
            ["perWorld"] = PerWorld,
            ["world"] = WorldId,
            ["loadedAt"] = LoadedAt?.ToString("O"),
            ["error"] = LastError,
            ["warnings"] = new JsonArray([.. Warnings.Select(w => (JsonNode)w)]),
            ["sections"] = sections,
            ["entries"] = entries,
        };
    }

    public string SchemaJson(bool indented = true) => Schema().ToJsonString(new JsonSerializerOptions { WriteIndented = indented });

    /// <summary>The offline schema snapshot (schema/config-schema.schema.json): <see cref="Schema"/> without values,
    /// for the launcher to edit the file while the game is not running. Kiln writes it to
    /// <c>&lt;configDir&gt;/.schema/&lt;id&gt;.json</c> on <see cref="Config.Register{T}"/>.</summary>
    public JsonObject SchemaSnapshot()
    {
        var entries = new JsonArray();
        foreach (var (node, _) in _values)
        {
            var o = new JsonObject
            {
                ["path"] = node.Path,
                ["type"] = node.TypeName,
                ["comment"] = node.Comment,
                ["default"] = ConfigSchema.ToJson(node, node.Default),
                ["requiresRestart"] = node.RequiresRestart,
                ["synced"] = node.Synced,
            };
            if (node.Min is { } min) o["min"] = min;
            if (node.Max is { } max) o["max"] = max;
            if (node.Options.Length > 0) o["options"] = new JsonArray([.. node.Options.Select(x => (JsonNode)x)]);
            entries.Add(o);
        }
        var sections = new JsonArray();
        foreach (var sec in Sections(_nodes)) sections.Add(new JsonObject { ["path"] = sec.Path, ["comment"] = sec.Comment, ["synced"] = sec.Synced });
        return new JsonObject
        {
            ["format"] = 1,
            ["source"] = "kiln",
            ["file"] = Id + ".json",
            ["valueFormat"] = "jsonc",
            ["id"] = Id,
            ["mod"] = ModId,
            ["modVersion"] = Owner?.Info.Version,
            ["kind"] = Kind.ToString().ToLowerInvariant(),
            ["kiln"] = Kiln.Backend.Current.Version,
            ["sections"] = sections,
            ["entries"] = entries,
        };
    }

    /// <summary><c>&lt;configDir&gt;/.schema/&lt;id&gt;.json</c> (contracts.md 5.5).</summary>
    public string SnapshotPath => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(FilePath)!, ".schema", Id + ".json");

    /// <summary>Writes the snapshot atomically unless the file already holds the same schema (its
    /// <c>schemaHash</c>, a sha256 of everything but <c>written</c>): a reload with an unchanged config class leaves
    /// it alone; a changed class or mod version rewrites it.</summary>
    internal bool WriteSnapshot()
    {
        var snap = SchemaSnapshot();
        var hash = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(snap.ToJsonString(ConfigSchema.JsonText))));
        var file = SnapshotPath;
        try
        {
            if (File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonObject old &&
                old["schemaHash"]?.GetValue<string>() == hash)
                return false;
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            // unreadable: write it again
        }
        snap["written"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        snap["schemaHash"] = hash;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            File.WriteAllText(file + ".tmp", snap.ToJsonString(new JsonSerializerOptions(ConfigSchema.JsonText) { WriteIndented = true }) + "\n",
                new UTF8Encoding(false));
            File.Move(file + ".tmp", file, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log(LogLevel.Warn, $"config {Id}: cannot write its schema snapshot {file}: {e.Message}");
            return false;
        }
    }

    /// <summary>The running values as a JSON object (only <see cref="SyncedAttribute"/> entries with
    /// <paramref name="syncedOnly"/>): what a server would send its clients.</summary>
    public string ValuesJson(bool syncedOnly = false)
    {
        var root = new JsonObject();
        foreach (var (node, chain) in _values)
        {
            if (syncedOnly && !node.Synced) continue;
            var o = root;
            foreach (var s in chain)
            {
                if (o[s.Key] is not JsonObject child) o[s.Key] = child = new JsonObject();
                o = child;
            }
            o[node.Key] = ConfigSchema.ToJson(node, node.Get(Holder(chain)));
        }
        return root.ToJsonString();
    }

    /// <summary>The values marked <see cref="SyncedAttribute"/>, as JSON.</summary>
    public string SyncedValuesJson() => ValuesJson(syncedOnly: true);

    public override string ToString() => $"{Id} ({Kind}, {FilePath})";

    // ---------------------------------------------------------------------------------------- internals ----------

    private static IEnumerable<ConfigNode> Sections(IEnumerable<ConfigNode> nodes) =>
        nodes.Where(n => n.IsSection).SelectMany(n => new[] { n }.Concat(Sections(n.Children)));

    private (ConfigNode Node, ConfigNode[] Chain) Find(string path) =>
        _byPath.TryGetValue(path ?? "", out var e) ? e : throw new KeyNotFoundException($"{Id} has no entry '{path}'");

    private ConfigEntry Entry(ConfigNode node, ConfigNode[] chain)
    {
        var running = node.Get(Holder(chain));
        var file = _file.TryGetValue(node.Path, out var f) ? f : running;
        return new ConfigEntry(node.Path, node.TypeName, node.Comment, node.Default, running, file, node.Min, node.Max,
            node.Options, node.RequiresRestart, node.Synced) { FromServer = IsFromServer(node.Path) };
    }

    /// <summary>The object holding a value: the config object or one of its sections (created when null).</summary>
    private object Holder(ConfigNode[] chain)
    {
        var o = ValueObject;
        foreach (var s in chain)
        {
            var child = s.Get(o);
            if (child is null)
            {
                child = Activator.CreateInstance(s.ClrType)!;
                s.Set(o, child);
            }
            o = child;
        }
        return o;
    }

    /// <summary>Reads (or creates) the file, applies its values, rewrites it in canonical form when needed.</summary>
    internal void Load(bool initial)
    {
        if (FilePath.Length == 0) return;  // a per-world config while no world is open: the defaults, in memory
        string text;
        try
        {
            if (!File.Exists(FilePath))
            {
                foreach (var (node, chain) in _values)
                    _file[node.Path] = ConfigSchema.Copy(node, node.Get(Holder(chain)));
                if (!initial) Log(LogLevel.Warn, $"config {Id}: {FilePath} disappeared; written again with the running values");
                Write();
                LoadedAt = DateTime.UtcNow;
                return;
            }
            text = File.ReadAllText(FilePath);
        }
        catch (IOException e)
        {
            Log(LogLevel.Warn, $"config {Id}: cannot read {FilePath}: {e.Message} (retrying)");
            Stamp = default;
            return;
        }
        UpdateStamp();
        if (text == _lastText || text == _lastBadText) return;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, ConfigSchema.ReadOptions);
        }
        catch (JsonException e)
        {
            _lastBadText = text;
            LastError = $"line {(e.LineNumber ?? 0) + 1}: {e.Message.Split(" LineNumber")[0]}";
            Log(LogLevel.Error, $"config {Id}: {FilePath} is not valid JSON ({LastError}); keeping the previous values");
            return;
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                _lastBadText = text;
                LastError = "the file must hold one { ... } object";
                Log(LogLevel.Error, $"config {Id}: {LastError}; keeping the previous values");
                return;
            }
            LastError = null;
            _lastBadText = null;
            var warnings = new List<string>();
            var unknown = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
            var read = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var missing = 0;
            ReadSection(_nodes, doc.RootElement, "", read, warnings, unknown, ref missing);
            _unknown = unknown;
            var changed = new List<string>();
            var pending = new List<string>();
            foreach (var (node, chain) in _values)
            {
                var v = read.TryGetValue(node.Path, out var fv) ? fv : ConfigSchema.Copy(node, node.Default);
                _file[node.Path] = v;
                if (_server.ContainsKey(node.Path)) continue;  // the server's value runs; this one comes back on leave
                var holder = Holder(chain);
                var running = node.Get(holder);
                if (ConfigSchema.ValueEquals(running, v)) continue;
                if (!initial && node.RequiresRestart)
                {
                    pending.Add(node.Path);
                    continue;
                }
                node.Set(holder, ConfigSchema.Copy(node, v));
                if (!initial) changed.Add(node.Path);
            }
            Warnings = warnings;
            foreach (var w in warnings) Log(LogLevel.Warn, $"config {Id}: {w}");
            if (unknown.Count > 0)
                Log(LogLevel.Warn, $"config {Id}: unknown key(s) kept: {string.Join(", ", unknown.SelectMany(kv => kv.Value.Select(k => (kv.Key.Length > 0 ? kv.Key + "." : "") + k.Item1)))}");
            var canonical = Generate();
            if (canonical != text) Write(canonical);
            else _lastText = text;
            LoadedAt = DateTime.UtcNow;
            if (!initial)
            {
                Log(LogLevel.Info, $"config {Id}: reloaded from the file: {(changed.Count == 0 && pending.Count == 0 ? "no changes" : string.Join(", ", changed.Concat(pending.Select(p => p + " (after restart)"))))}");
                if (changed.Count > 0 || pending.Count > 0) Raise(changed, pending, ConfigChangeSource.File);
            }
        }
    }

    private void ReadSection(List<ConfigNode> nodes, JsonElement obj, string sectionPath, Dictionary<string, object?> read,
        List<string> warnings, Dictionary<string, List<(string, string)>> unknown, ref int missing)
    {
        var seen = new HashSet<ConfigNode>(ReferenceEqualityComparer.Instance);
        foreach (var p in obj.EnumerateObject())
        {
            var node = nodes.FirstOrDefault(n => string.Equals(n.Key, p.Name, StringComparison.OrdinalIgnoreCase));
            if (node is null)
            {
                if (!unknown.TryGetValue(sectionPath, out var list)) unknown[sectionPath] = list = new();
                list.RemoveAll(x => x.Item1 == p.Name);
                list.Add((p.Name, p.Value.GetRawText()));
                continue;
            }
            seen.Add(node);
            if (node.IsSection)
            {
                if (p.Value.ValueKind != JsonValueKind.Object)
                {
                    warnings.Add($"\"{node.Path}\" must be a section ({{ ... }}); using its defaults");
                    continue;
                }
                ReadSection(node.Children, p.Value, node.Path, read, warnings, unknown, ref missing);
                continue;
            }
            if (ConfigSchema.FromJson(node, p.Value, out var v, warnings)) read[node.Path] = v;
        }
        foreach (var n in nodes)
            if (!seen.Contains(n))
                missing++;
    }

    private string Generate()
    {
        var sb = new StringBuilder();
        sb.Append(PerWorld
            ? $"// {Id}: config of mod \"{ModId}\" for the world {WorldId}, written by Kiln from the mod's schema. Every world has its own.\n"
            : $"// {Id}: {Kind.ToString().ToLowerInvariant()} config of mod \"{ModId}\", written by Kiln from the mod's schema.\n");
        sb.Append("// Edit and save while the server runs: Kiln reloads it. Entries marked \"Requires a restart\" apply after the\n");
        sb.Append("// mod reloads. Values outside a range are clamped; keys the mod does not know are kept.\n");
        sb.Append("{\n");
        ConfigSchema.WriteSection(sb, _nodes, ValueObject, 1, (node, _) => _file.GetValueOrDefault(node.Path), _unknown, "");
        sb.Append("}\n");
        return sb.ToString();
    }

    private void Write(string? text = null)
    {
        if (FilePath.Length == 0) return;  // per-world, no world open
        text ??= Generate();
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            File.Move(tmp, FilePath, overwrite: true);
            _lastText = text;
            UpdateStamp();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log(LogLevel.Error, $"config {Id}: cannot write {FilePath}: {e.Message}");
        }
    }

    internal (long, long) ReadStamp()
    {
        if (FilePath.Length == 0) return (0, 0);
        try
        {
            var fi = new FileInfo(FilePath);
            return fi.Exists ? (fi.LastWriteTimeUtc.Ticks, fi.Length) : (0, -1);
        }
        catch (IOException)
        {
            return (0, -2);
        }
    }

    private void UpdateStamp() => Stamp = ReadStamp();

    private void Raise(IReadOnlyList<string> changed, IReadOnlyList<string> pending, ConfigChangeSource source)
    {
        if (changed.Count == 0 && pending.Count == 0) return;
        Config.NotifyChanged(this);
        var ev = new ConfigChangedEvent(this, changed, pending, source);
        (Action<ConfigChangedEvent> Handler, ModContext? Owner)[] handlers;
        lock (_handlers) handlers = _handlers.ToArray();
        foreach (var h in handlers) ModInvoke.Run(h.Owner, () => h.Handler(ev), $"config {Id} Changed handler");
        if (Events.HasSubscribers<ConfigChangedEvent>()) Events.Post(ev);
    }

    // ---------------------------------------------------------------------------------------- per-world -----------
    // A per-world config (WorldConfig.cs): the file lies in the open world's sidecar folder, and a world switch (a game
    // client hosts one world after another) moves the values with it: the old world's values are left in its file, the new
    // world's file (or the defaults) is read in their place.

    /// <summary>Makes <paramref name="worldDirectory"/> (the world's sidecar folder; null: no world is open) the
    /// folder of this config's file. Leaving a world puts the defaults back first, so nothing of the old world is kept;
    /// a new world without a file gets one written from the defaults. Raises <see cref="Changed"/> with
    /// <see cref="ConfigChangeSource.World"/> for every entry whose value differs afterwards. Tick thread.</summary>
    internal void BindWorld(string? worldDirectory)
    {
        if (!PerWorld || string.Equals(worldDirectory, WorldDirectory, StringComparison.Ordinal)) return;
        var before = _values.ToDictionary(v => v.Node.Path, v => ConfigSchema.Copy(v.Node, v.Node.Get(Holder(v.Chain))), StringComparer.OrdinalIgnoreCase);
        var leaving = WorldDirectory is not null;
        var from = WorldId;
        WorldDirectory = worldDirectory;
        if (leaving) ResetToDefaults();
        FilePath = worldDirectory is null ? "" : System.IO.Path.Combine(worldDirectory, "config", Id + ".json");
        _lastText = _lastBadText = null;
        Stamp = default;
        LastError = null;
        Warnings = [];
        if (worldDirectory is not null) Load(initial: true);  // reads the file, or writes it from the running values
        var changed = new List<string>();
        foreach (var (node, chain) in _values)
            if (!ConfigSchema.ValueEquals(before[node.Path], node.Get(Holder(chain)))) changed.Add(node.Path);
        Log(LogLevel.Info, worldDirectory is null
            ? $"config {Id}: no world open; the defaults apply{(changed.Count > 0 ? $" ({string.Join(", ", changed)})" : "")}"
            : $"config {Id}: world {WorldId}: {(changed.Count == 0 ? "no values differ from before" : string.Join(", ", changed.Select(p => $"{p} = {FormatPath(p)}")))} ({FilePath})" +
              (from is not null ? $" (was world {from})" : ""));
        Raise(changed, [], ConfigChangeSource.World);
    }

    private void ResetToDefaults()
    {
        _unknown = new Dictionary<string, List<(string Key, string Raw)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (node, chain) in _values)
        {
            var def = ConfigSchema.Copy(node, node.Default);
            _file[node.Path] = def;
            if (_server.ContainsKey(node.Path)) continue;  // the server's value runs while connected
            node.Set(Holder(chain), ConfigSchema.Copy(node, def));
        }
    }

    // ---------------------------------------------------------------------------------------- config sync -------
    // Client side of Kiln.Network's config sync (docs/kiln/sync.md): the server's values of [Synced] entries replace
    // the running values while connected; the file keeps this game's own values, which come back on leave.

    /// <summary>Takes the server's values (a JSON object shaped like <see cref="SyncedValuesJson"/>) for the
    /// [Synced] entries it holds; synced entries it no longer holds go back to the local value. Values the schema
    /// refuses (another mod version) keep the local value and are listed in Errors. <paramref name="initial"/>: the
    /// config is being registered (mod load while connected): [RequiresRestart] entries apply too and nothing is
    /// raised. Tick thread.</summary>
    internal (List<string> Changed, List<string> Pending, List<string> Errors) ApplyServerValues(JsonElement root, bool initial = false)
    {
        Game.AssertTickThread();
        var changed = new List<string>();
        var pending = new List<string>();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var (node, chain) in _values)
            {
                if (!node.Synced || !TryElement(root, chain, node.Key, out var el)) continue;
                if (!ConfigSchema.FromObject(node, el, out var v, out var err))
                {
                    errors.Add($"{node.Path}: {err}");
                    continue;
                }
                seen.Add(node.Path);
                _server[node.Path] = ConfigSchema.Copy(node, v);
                var holder = Holder(chain);
                if (ConfigSchema.ValueEquals(node.Get(holder), v)) continue;
                if (node.RequiresRestart && !initial)
                {
                    pending.Add(node.Path);
                    continue;
                }
                node.Set(holder, ConfigSchema.Copy(node, v));
                changed.Add(node.Path);
            }
        }
        foreach (var path in _server.Keys.Where(k => !seen.Contains(k)).ToList())
            if (RestoreLocal(path, initial)) changed.Add(path);
            else if (_byPath.TryGetValue(path, out var e) && e.Node.RequiresRestart) pending.Add(path);
        _serverPaths = [.. _server.Keys];
        if (changed.Count > 0 || pending.Count > 0 || errors.Count > 0)
            Log(errors.Count > 0 ? LogLevel.Warn : LogLevel.Info,
                $"config {Id}: server values {(changed.Count == 0 ? "(no change)" : string.Join(", ", changed.Select(p => $"{p} = {FormatPath(p)}")))}" +
                (pending.Count > 0 ? $"; after a restart: {string.Join(", ", pending)}" : "") +
                (errors.Count > 0 ? $"; refused: {string.Join("; ", errors)}" : ""));
        if (!initial) Raise(changed, pending, ConfigChangeSource.Server);
        return (changed, pending, errors);
    }

    /// <summary>Back to this game's own values for every entry that held the server's (leaving the server). Tick
    /// thread. Returns the paths whose running value changed.</summary>
    internal List<string> ClearServerValues()
    {
        Game.AssertTickThread();
        var changed = new List<string>();
        var pending = new List<string>();
        foreach (var path in _server.Keys.ToList())
            if (RestoreLocal(path, initial: false)) changed.Add(path);
            else if (_byPath.TryGetValue(path, out var e) && e.Node.RequiresRestart &&
                     !ConfigSchema.ValueEquals(e.Node.Get(Holder(e.Chain)), _file.GetValueOrDefault(path))) pending.Add(path);
        _server.Clear();
        _serverPaths = [];
        if (changed.Count > 0 || pending.Count > 0)
            Log(LogLevel.Info, $"config {Id}: local values back ({string.Join(", ", changed.Concat(pending.Select(p => p + " (after a restart)")))})");
        Raise(changed, pending, ConfigChangeSource.Server);
        return changed;
    }

    internal bool HasServerValues => _server.Count > 0;

    private bool RestoreLocal(string path, bool initial)
    {
        _server.Remove(path);
        if (!_byPath.TryGetValue(path, out var e)) return false;
        var holder = Holder(e.Chain);
        var local = _file.TryGetValue(path, out var fv) ? fv : e.Node.Default;
        if (ConfigSchema.ValueEquals(e.Node.Get(holder), local)) return false;
        if (e.Node.RequiresRestart && !initial) return false;
        e.Node.Set(holder, ConfigSchema.Copy(e.Node, local));
        return true;
    }

    private string FormatPath(string path) =>
        _byPath.TryGetValue(path, out var e) ? ConfigSchema.Format(e.Node, e.Node.Get(Holder(e.Chain))) : "?";

    private static bool TryElement(JsonElement root, ConfigNode[] chain, string key, out JsonElement el)
    {
        el = root;
        foreach (var s in chain)
            if (!TryProperty(el, s.Key, out el) || el.ValueKind != JsonValueKind.Object) return false;
        return TryProperty(el, key, out el);
    }

    private static bool TryProperty(JsonElement obj, string key, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
            foreach (var p in obj.EnumerateObject())
                if (string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
        value = default;
        return false;
    }

    private void Log(LogLevel level, string text)
    {
        if (Owner is not null) Owner.Log.Write(level, text);
        else Backend.Current.Log(0, level, text);
    }
}

/// <summary>A mod's typed config. <see cref="Value"/> is updated in place when the file changes, so references to it
/// (and to its sections) stay current.</summary>
public sealed class ModConfig<T> : ConfigFile where T : class, new()
{
    internal ModConfig(ModContext? owner, string id, ConfigKind kind) : base(owner, id, kind, typeof(T), new T())
    {
    }

    /// <summary>The running values. Read them where you need them (on the tick thread); after changing them in code
    /// call <see cref="ConfigFile.Save"/>.</summary>
    public T Value => (T)ValueObject;
}

/// <summary>The registry of every loaded mod's configs (Forge's <c>ModLoadingContext.registerConfig</c> +
/// <c>ConfigTracker</c>).</summary>
/// <remarks>
/// A config class has public read-write properties or fields of type bool, whole numbers, float/double/decimal,
/// string, enums, <see cref="TimeSpan"/>, lists/arrays of those, and nested classes (sections). Members take
/// <see cref="CommentAttribute"/>, <see cref="RangeAttribute"/>, <see cref="RequiresRestartAttribute"/>,
/// <see cref="SyncedAttribute"/>, <see cref="ConfigKeyAttribute"/>, <see cref="ConfigIgnoreAttribute"/>.
/// Kiln notices file edits within a step or two (file watcher), with a safety poll every five seconds, or every
/// second when the watcher could not start. Reloads run on the service tick, so wait while it is paused.
/// </remarks>
public static partial class Config
{
    private static readonly object Lock = new();
    private static readonly List<ConfigFile> Files = new();
    private static IDisposable? _tick;
    private static FileSystemWatcher? _watcher;
    private static volatile bool _signal;
    private static ulong _signalStep;
    // The fallback poll stats every registered file. By the clock, not the step count; with a working file watcher
    // it is only a safety net, so it runs less often.
    private const long PollEveryMs = 1000, WatchedPollEveryMs = 5000;
    private static long _nextPoll;

    /// <summary>The folder of the config files: the profile's configDir (KILN_PATH_CONFIG), else <c>kiln/config</c>.</summary>
    public static string Directory => KilnPaths.Config;

    /// <summary>Every registered config, by id.</summary>
    public static IReadOnlyList<ConfigFile> All
    {
        get
        {
            lock (Lock) return Files.OrderBy(f => f.Id, StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>A config by id ("mymod", "mymod-server"), or null.</summary>
    public static ConfigFile? Find(string id)
    {
        lock (Lock) return Files.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Registers the calling mod's config of type <typeparamref name="T"/> and loads it: the file
    /// <c>&lt;configDir&gt;/&lt;modid&gt;[-&lt;name&gt;][-server|-client].json</c> is created from the schema when
    /// missing, otherwise read (values validated and clamped, missing entries added, comments refreshed), and the
    /// schema snapshot <c>&lt;configDir&gt;/.schema/&lt;id&gt;.json</c> is written for the launcher. The config goes
    /// away when the mod unloads. Tick thread (e.g. <c>OnLoad</c>).
    /// </summary>
    /// <param name="kind">Common, Server or Client: picks the file name.</param>
    /// <param name="name">For a second config of the same kind: becomes part of the file name.</param>
    public static ModConfig<T> Register<T>(ConfigKind kind = ConfigKind.Common, string? name = null) where T : class, new()
    {
        Game.AssertTickThread();
        var mod = ModContext.CurrentMod;
        if (mod is { Alive: false }) throw new InvalidOperationException("the mod is unloading");
        if (mod is null && string.IsNullOrEmpty(name)) throw new InvalidOperationException("Config.Register outside a mod needs a name");
        if (name is not null && !ValidName(name)) throw new ArgumentException($"config name '{name}' must be [a-z0-9_.-]", nameof(name));
        var id = (mod?.Info.Id ?? "") is { Length: > 0 } modId ? modId + (name is null ? "" : "-" + name) : name!;
        id += kind switch
        {
            ConfigKind.Server => "-server",
            ConfigKind.Client => "-client",
            _ => "",
        };
        var file = new ModConfig<T>(mod, id, kind);
        lock (Lock)
        {
            if (Files.Any(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"config '{id}' is already registered");
            Files.Add(file);
        }
        if (mod is not null) mod.Retiring += () => Unregister(file);
        file.Load(initial: true);
        file.WriteSnapshot();
        try
        {
            if (ServerValuesFor?.Invoke(file) is { } server) file.ApplyServerValues(server, initial: true);
        }
        catch (Exception e)
        {
            Backend.Current.Log(0, LogLevel.Error, $"config {id}: server values could not be applied: {e.Message}");
        }
        StartWatching();
        RegistryChanged?.Invoke();
        return file;
    }

    /// <summary>Schema and values of every config, as one JSON array.</summary>
    public static JsonArray Schema() => new([.. All.Select(f => (JsonNode)f.Schema())]);

    /// <summary>Kiln.Network's config sync (client side): the server's values for a config being registered while
    /// connected, applied before <see cref="Register{T}"/> returns.</summary>
    internal static Func<ConfigFile, JsonElement?>? ServerValuesFor;

    /// <summary>Kiln.Network's config sync (server side): a config's values changed (tick thread).</summary>
    internal static event Action<ConfigFile>? AnyChanged;

    /// <summary>A config was registered or unregistered (any thread).</summary>
    internal static event Action? RegistryChanged;

    internal static void NotifyChanged(ConfigFile file)
    {
        try { AnyChanged?.Invoke(file); }
        catch (Exception e) { Backend.Current.Log(0, LogLevel.Error, $"config: change hook threw: {e}"); }
    }

    internal static void Unregister(ConfigFile file)
    {
        lock (Lock) Files.Remove(file);
        RegistryChanged?.Invoke();
    }

    /// <summary>Test hook: forget every config and stop watching.</summary>
    internal static void Reset()
    {
        lock (Lock)
        {
            Files.Clear();
            _tick?.Dispose();
            _tick = null;
            _watcher?.Dispose();
            _watcher = null;
        }
        WorldConfigs.Reset();
    }

    /// <summary>One check of every file (on watcher signals or the fallback interval). Tests call it.</summary>
    internal static void Poll()
    {
        foreach (var f in All)
        {
            if (f.Owner is { Alive: false }) continue;
            if (f.ReadStamp() == f.Stamp) continue;
            f.Load(initial: false);
        }
    }

    private static bool ValidName(string s) =>
        s.Length is > 0 and <= 48 && s.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '.' or '-');

    private static void StartWatching()
    {
        lock (Lock)
        {
            if (_tick is not null) return;
            _tick = ServiceTick.Add("kiln config", OnTick);
            try
            {
                System.IO.Directory.CreateDirectory(KilnPaths.Config);
                var w = new FileSystemWatcher(KilnPaths.Config, "*.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                };
                w.Changed += (_, _) => _signal = true;
                w.Created += (_, _) => _signal = true;
                w.Renamed += (_, _) => _signal = true;
                w.Error += (_, e) => Backend.Current.Log(0, LogLevel.Warn, $"config: file watcher error ({e.GetException().Message}); polling continues");
                w.EnableRaisingEvents = true;
                _watcher = w;
                Backend.Current.Log(0, LogLevel.Info, $"config: watching {KilnPaths.Config} (file watcher, polling every {WatchedPollEveryMs / 1000} s as fallback)");
            }
            catch (Exception e)
            {
                Backend.Current.Log(0, LogLevel.Info, $"config: no file watcher ({e.Message}); polling {KilnPaths.Config} every {PollEveryMs / 1000} s");
            }
        }
    }

    private static void OnTick(in TickInfo tick)
    {
        if (_signal)
        {
            _signal = false;
            _signalStep = tick.Step;  // editors write in pieces: look two steps later
        }
        var due = _signalStep != 0 && tick.Step >= _signalStep + 2;
        var now = Environment.TickCount64;
        if (!due && now < _nextPoll) return;
        if (due) _signalStep = 0;
        _nextPoll = now + (_watcher is null ? PollEveryMs : WatchedPollEveryMs);
        Poll();
    }
}
