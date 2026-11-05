// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Kiln;

/// <summary>permissions.json on disk: created with defaults, read (JSONC), written after edits, re-read when it or
/// enshrouded_server.json changes (polled once a second on the service tick).</summary>
internal static class PermissionStore
{
    private static readonly object Lock = new();
    private static volatile PermissionModel _model = PermissionModel.Default();
    private static (long, long) _stamp, _serverStamp;
    private static string? _lastText, _badText;
    private static IDisposable? _tick;
    private static bool _loaded;
    private const int PollEverySteps = 25;

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string FilePath => Path.Combine(KilnPaths.Config, "permissions.json");
    public static string ServerConfigPath => Path.Combine(KilnPaths.Server, "enshrouded_server.json");

    public static PermissionModel Model
    {
        get
        {
            if (!_loaded) Load("file");
            return _model;
        }
    }

    public static void Start()
    {
        lock (Lock) _tick ??= ServiceTick.Add("kiln permissions", OnTick);
        try
        {
            if (!_loaded) Load("file");  // the default file appears as soon as Kiln.Permissions is used
        }
        catch (Exception e)
        {
            Backend.Current.Log(0, LogLevel.Error, $"permissions: first load failed: {e.Message}");
        }
    }

    private static void OnTick(in TickInfo tick)
    {
        if (tick.Step % PollEverySteps != 0) return;
        if (Stamp(FilePath) != _stamp || Stamp(ServerConfigPath) != _serverStamp) Load("file");
    }

    /// <summary>Test hook: start over (the next read loads the file again).</summary>
    internal static void Reset()
    {
        lock (Lock)
        {
            _model = PermissionModel.Default();
            _loaded = false;
            _stamp = _serverStamp = default;
            _lastText = _badText = null;
        }
    }

    public static void Load(string reason)
    {
        bool changed;
        lock (Lock)
        {
            var first = !_loaded;
            _loaded = true;
            var serverGroups = ReadServerGroups(out var serverChanged);
            var model = _model;
            var fileChanged = false;
            _stamp = Stamp(FilePath);
            if (!File.Exists(FilePath))
            {
                if (first || _lastText is not null)
                {
                    model = PermissionModel.Default();
                    model.ServerGroups = serverGroups;
                    Write(model);
                    fileChanged = !first;
                    Backend.Current.Log(0, LogLevel.Info, $"permissions: wrote the default {FilePath}");
                }
            }
            else
            {
                string text;
                try
                {
                    text = File.ReadAllText(FilePath);
                }
                catch (IOException e)
                {
                    Backend.Current.Log(0, LogLevel.Warn, $"permissions: cannot read {FilePath}: {e.Message}");
                    _stamp = default;
                    return;
                }
                if (text != _lastText && text != _badText)
                {
                    if (Parse(text, out var parsed, out var error))
                    {
                        model = parsed;
                        _lastText = text;
                        _badText = null;
                        fileChanged = true;
                    }
                    else
                    {
                        _badText = text;
                        Backend.Current.Log(0, LogLevel.Error, $"permissions: {FilePath}: {error}; keeping the previous rules");
                    }
                }
            }
            if (fileChanged || serverChanged)
            {
                if (!fileChanged) model = model.Clone();
                model.ServerGroups = serverGroups;
                _model = model;
                Backend.Current.Log(0, LogLevel.Info,
                    $"permissions: {(first ? "loaded" : "reloaded")} {model.Groups.Count} group(s), {model.Players.Count} player(s), " +
                    $"{model.UserGroups.Count} user-group mapping(s); server user groups: {string.Join(", ", serverGroups.Select(g => $"{g.Name}=0x{g.Bits:x}"))}");
            }
            changed = !first && (fileChanged || serverChanged);
        }
        if (changed) PostChanged(reason == "file" ? "file" : reason);
    }

    /// <summary>Applies <paramref name="edit"/> to a copy of the model, publishes it and writes the file. Tick thread.</summary>
    public static void Edit(Action<PermissionModel> edit, string what)
    {
        Game.AssertTickThread();
        lock (Lock)
        {
            var m = Model.Clone();
            edit(m);
            Write(m);
            _model = m;
        }
        Backend.Current.Log(0, LogLevel.Info, $"permissions: {what} (written to {FilePath})");
        PostChanged("api");
    }

    private static void PostChanged(string reason)
    {
        if (!Events.HasSubscribers<PermissionsChangedEvent>()) return;
        var ev = new PermissionsChangedEvent(reason);
        if (Game.IsTickThread) Events.Post(ev);
        else Events.Enqueue(ev);
    }

    private static (long, long) Stamp(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? (fi.LastWriteTimeUtc.Ticks, fi.Length) : (0, -1);
        }
        catch (IOException)
        {
            return (0, -2);
        }
    }

    private static IReadOnlyList<ServerUserGroup> ReadServerGroups(out bool changed)
    {
        var stamp = Stamp(ServerConfigPath);
        changed = stamp != _serverStamp;
        _serverStamp = stamp;
        if (!changed) return _model.ServerGroups;
        try
        {
            if (!File.Exists(ServerConfigPath)) return [];
            using var doc = JsonDocument.Parse(File.ReadAllText(ServerConfigPath), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (!doc.RootElement.TryGetProperty("userGroups", out var ug) || ug.ValueKind != JsonValueKind.Array) return [];
            var list = new List<ServerUserGroup>();
            foreach (var g in ug.EnumerateArray())
            {
                if (g.ValueKind != JsonValueKind.Object || !g.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) continue;
                uint bits = 0;
                bool Flag(string key) => g.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
                if (Flag("canKickBan")) bits |= 0x01;
                if (Flag("canAccessInventories")) bits |= 0x02;
                if (Flag("canEditBase")) bits |= 0x04;
                if (Flag("canEditWorld")) bits |= 0x08;
                if (Flag("canExtendBase")) bits |= 0x10;
                list.Add(new ServerUserGroup(n.GetString()!, bits));
            }
            return list;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Backend.Current.Log(0, LogLevel.Warn, $"permissions: cannot read the user groups from {ServerConfigPath}: {e.Message}");
            return _model.ServerGroups;
        }
    }

    // ---- file format ----

    internal static bool Parse(string text, out PermissionModel model, out string error)
    {
        model = new PermissionModel();
        error = "";
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "the file must hold one { ... } object";
                return false;
            }
            var problems = new List<string>();
            foreach (var p in root.EnumerateObject())
            {
                switch (p.Name.ToLowerInvariant())
                {
                    case "kickbanisadmin":
                        model.KickBanIsAdmin = p.Value.ValueKind != JsonValueKind.False;
                        break;
                    case "defaultgroup":
                        if (p.Value.ValueKind == JsonValueKind.String) model.DefaultGroup = p.Value.GetString()!;
                        break;
                    case "groups" when p.Value.ValueKind == JsonValueKind.Object:
                        foreach (var g in p.Value.EnumerateObject())
                        {
                            var grp = new PermissionGroup(g.Name);
                            if (g.Value.ValueKind == JsonValueKind.Object)
                            {
                                if (g.Value.TryGetProperty("admin", out var a)) grp.Admin = a.ValueKind == JsonValueKind.True;
                                grp.Inherits = Strings(g.Value, "inherits");
                                foreach (var r in Strings(g.Value, "nodes"))
                                    if (PermissionRule.Parse(r) is { } rule) grp.Rules.Add(rule);
                                    else problems.Add($"group {g.Name}: invalid node '{r}'");
                            }
                            model.Groups[g.Name] = grp;
                        }
                        break;
                    case "usergroups" when p.Value.ValueKind == JsonValueKind.Object:
                        foreach (var u in p.Value.EnumerateObject())
                            model.UserGroups[u.Name] = u.Value.ValueKind == JsonValueKind.String
                                ? [u.Value.GetString()!]
                                : u.Value.ValueKind == JsonValueKind.Array
                                    ? u.Value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                                    : [];
                        break;
                    case "usergroupids" when p.Value.ValueKind == JsonValueKind.Object:
                        foreach (var u in p.Value.EnumerateObject())
                            if (uint.TryParse(u.Name, out var id) && u.Value.ValueKind == JsonValueKind.String) model.UserGroupIds[id] = u.Value.GetString()!;
                            else problems.Add($"userGroupIds: '{u.Name}' must map a number to a user group name");
                        break;
                    case "players" when p.Value.ValueKind == JsonValueKind.Object:
                        foreach (var pl in p.Value.EnumerateObject())
                        {
                            if (!ulong.TryParse(pl.Name, out var steam) || steam == 0)
                            {
                                problems.Add($"players: '{pl.Name}' is not a SteamID64");
                                continue;
                            }
                            var entry = new PermissionPlayer(steam);
                            if (pl.Value.ValueKind == JsonValueKind.Object)
                            {
                                if (pl.Value.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String) entry.Name = nm.GetString();
                                entry.Groups.AddRange(Strings(pl.Value, "groups"));
                                foreach (var r in Strings(pl.Value, "nodes"))
                                    if (PermissionRule.Parse(r) is { } rule) entry.Rules.Add(rule);
                                    else problems.Add($"player {pl.Name}: invalid node '{r}'");
                            }
                            model.Players[steam] = entry;
                        }
                        break;
                }
            }
            if (!model.Groups.ContainsKey(model.DefaultGroup)) model.Groups[model.DefaultGroup] = new PermissionGroup(model.DefaultGroup);
            foreach (var pr in problems) Backend.Current.Log(0, LogLevel.Warn, $"permissions: {pr} (ignored)");
            return true;
        }
        catch (JsonException e)
        {
            error = $"not valid JSON (line {(e.LineNumber ?? 0) + 1}): {e.Message.Split(" LineNumber")[0]}";
            return false;
        }
    }

    private static List<string> Strings(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];

    internal static string Format(PermissionModel m)
    {
        string S(string s) => JsonSerializer.Serialize(s, Json);
        string L(IEnumerable<string> l) => "[" + string.Join(", ", l.Select(S)) + "]";
        var sb = new StringBuilder();
        sb.Append("""
            // Kiln permissions (like Forge's PermissionAPI). Edit and save: Kiln reloads this file while the server runs.
            // Nodes look like "mymod.command.home". "mymod.*" matches every node below "mymod"; "*" matches every node
            // except console-only ones (those need their exact name). "-node" denies. The most specific rule wins (a deny
            // wins a tie); a player's own rules beat their groups' rules.
            {
              // A node no rule mentions: "everyone" nodes are allowed, "admin" nodes need an admin (a player in a group with
              // "admin": true or, when kickBanIsAdmin is true, whose server user group may kick and ban), "console" nodes
              // are denied to players.

            """);
        sb.Append($"  \"kickBanIsAdmin\": {(m.KickBanIsAdmin ? "true" : "false")},\n");
        sb.Append("  // The group every player is in.\n");
        sb.Append($"  \"defaultGroup\": {S(m.DefaultGroup)},\n");
        sb.Append("  // Kiln groups: \"admin\" (counts as admin), \"inherits\" (other groups), \"nodes\" (rules).\n");
        sb.Append("  \"groups\": {");
        var gi = 0;
        foreach (var g in m.Groups.Values.OrderBy(g => g.Name == m.DefaultGroup ? 0 : 1).ThenBy(g => g.Name, StringComparer.Ordinal))
        {
            sb.Append(gi++ == 0 ? "\n" : ",\n");
            sb.Append($"    {S(g.Name)}: {{ \"admin\": {(g.Admin ? "true" : "false")}, \"inherits\": {L(g.Inherits)}, \"nodes\": {L(g.Rules.Select(r => r.ToString()))} }}");
        }
        sb.Append(gi > 0 ? "\n  },\n" : "},\n");
        sb.Append("  // The server's user groups (enshrouded_server.json \"userGroups\", by name) -> Kiln groups. Kiln finds a\n");
        sb.Append("  // player's user group by its rights (the only user group with exactly those rights) or by userGroupIds.\n");
        sb.Append("  \"userGroups\": {");
        var ui = 0;
        foreach (var (k, v) in m.UserGroups.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append(ui++ == 0 ? "\n" : ",\n");
            sb.Append($"    {S(k)}: {L(v)}");
        }
        sb.Append(ui > 0 ? "\n  },\n" : "},\n");
        sb.Append("  // Optional: the game's user-group id (Kiln.Players UserGroupId, logged at join) -> user group name.\n");
        sb.Append("  \"userGroupIds\": {");
        var ii = 0;
        foreach (var (k, v) in m.UserGroupIds.OrderBy(kv => kv.Key))
        {
            sb.Append(ii++ == 0 ? "\n" : ",\n");
            sb.Append($"    \"{k}\": {S(v)}");
        }
        sb.Append(ii > 0 ? "\n  },\n" : "},\n");
        sb.Append("  // Players by SteamID64: extra groups and their own rules (\"name\" is a note for you).\n");
        sb.Append("  \"players\": {");
        var pi = 0;
        foreach (var p in m.Players.Values.OrderBy(p => p.SteamId))
        {
            sb.Append(pi++ == 0 ? "\n" : ",\n");
            sb.Append($"    \"{p.SteamId}\": {{ {(p.Name is { Length: > 0 } ? $"\"name\": {S(p.Name)}, " : "")}\"groups\": {L(p.Groups)}, \"nodes\": {L(p.Rules.Select(r => r.ToString()))} }}");
        }
        sb.Append(pi > 0 ? "\n  }\n" : "}\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    private static void Write(PermissionModel m)
    {
        var text = Format(m);
        try
        {
            Directory.CreateDirectory(KilnPaths.Config);
            File.WriteAllText(FilePath + ".tmp", text, new UTF8Encoding(false));
            File.Move(FilePath + ".tmp", FilePath, overwrite: true);
            _lastText = text;
            _stamp = Stamp(FilePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Backend.Current.Log(0, LogLevel.Error, $"permissions: cannot write {FilePath}: {e.Message}");
        }
    }
}
