// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln.Admin;

/// <summary>Ops of the launcher's admin pages (docs/kiln/launcher/admin-protocol.md section 8, "New ops"):
/// admin_info, network_status, network_reload, native_status, log_tail, admin_token_rotate. Both transports serve
/// them. Kiln.Network calls sit in their own methods so a server without Kiln.Network still loads Kiln.Admin.</summary>
internal static partial class Ops
{
    /// <summary>Set by AdminMod: the KAP listener (null when TCP is off) and what admin_info reports.</summary>
    internal static TcpAdmin? Tcp { get; set; }
    internal static string AdminVersion { get; set; } = "";
    internal static long LoadedAt { get; set; } = Environment.TickCount64;

    internal const int LogTailDefault = 200, LogTailMax = 5000;

    internal static void RegisterAdmin(Bridge bridge, Logger log)
    {
        bridge.Register("admin_info", _ => AdminInfo(bridge));
        bridge.Register("network_status", _ => NetworkGuard(NetStatusJson));
        bridge.Register("network_reload", _ => NetworkGuard(() =>
        {
            ReloadNetwork();
            log.Info("dashboard reloaded network.json");
            return NetStatusJson();
        }));
        bridge.Register("native_status", _ => NativeStatus());
        // {file?: "kiln" | "game" | <name under kiln/logs>, lines?: 1..5000}
        bridge.Register("log_tail", LogTail);
        bridge.Register("admin_token_rotate", _ =>
        {
            if (Tcp is { } tcp) tcp.RotateToken(tcp.CurrentCaller);
            else AdminToken.Rotate(DataDirectory);
            log.Info("admin token rotated");
            return new JsonObject { ["rotated"] = true };
        });
    }

    private static JsonObject AdminInfo(Bridge bridge)
    {
        var (id, name) = ServerIdent();
        var tcp = Tcp;
        return new JsonObject
        {
            ["admin"] = AdminVersion, ["kiln"] = Game.KilnVersion, ["api"] = Mods.ApiVersion, ["protocol"] = TcpAdmin.Protocol,
            ["transports"] = tcp is null ? new JsonArray((JsonNode)"file") : new JsonArray((JsonNode)"file", (JsonNode)"tcp"),
            ["endpoint"] = tcp is null ? null : new JsonObject { ["host"] = "127.0.0.1", ["port"] = tcp.Port },
            ["server"] = new JsonObject { ["id"] = id, ["name"] = name },
            ["uptimeS"] = Math.Round((Environment.TickCount64 - LoadedAt) / 1000.0, 1),
            ["connections"] = tcp?.Connections ?? 0,
            ["bridge"] = bridge.Directory,
            ["ops"] = new JsonArray([.. bridge.OpNames.Select(o => (JsonNode)o)]),
            ["topics"] = new JsonArray([.. TcpAdmin.Topics.Select(t => (JsonNode)t)]),
        };
    }

    /// <summary>Server id and name for hello/endpoint.json: Kiln.Network's, else the files it keeps.</summary>
    internal static (string? Id, string? Name) ServerIdent()
    {
        try
        {
            return NetIdent();
        }
        catch (Exception)
        {
            var kiln = Path.GetFullPath(Path.Combine(DataDirectory, "..", ".."));
            string? id = null, name = null;
            try
            {
                var file = Path.Combine(kiln, "data", "kiln-network", "server-id");
                if (File.Exists(file)) id = File.ReadAllText(file).Trim();
                var cfg = Path.Combine(kiln, "..", "enshrouded_server.json");
                if (File.Exists(cfg)) name = JsonNode.Parse(File.ReadAllText(cfg))?["name"]?.GetValue<string>();
            }
            catch (Exception)
            {
            }
            return (id, name);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (string? Id, string? Name) NetIdent() => (Network.ServerId, Network.ServerName);

    private static JsonNode? NetworkGuard(Func<JsonNode?> f)
    {
        try
        {
            return f();
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException or MissingMethodException)
        {
            throw new AdminError("Kiln.Network is not available on this server: " + e.Message);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReloadNetwork()
    {
        Network.ReloadConfig();
        Network.RefreshRequired();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static JsonObject NetStatusJson()
    {
        var c = Network.Config;
        var adv = Network.Advertised;
        var st = Network.Status;
        var config = new JsonObject
        {
            ["modSync"] = c.ModSync.ToString().ToLowerInvariant(), ["versionMatch"] = c.VersionMatch.ToString().ToLowerInvariant(),
            ["requireKiln"] = c.RequireKiln, ["optionalMods"] = new JsonArray([.. c.OptionalMods.Select(m => (JsonNode)m)]),
            ["kickReason"] = c.KickReason, ["handshakeTimeoutSeconds"] = c.HandshakeTimeoutSeconds, ["maxMessageBytes"] = c.MaxMessageBytes,
            ["messagesPerSecond"] = c.MessagesPerSecond, ["bytesPerSecond"] = c.BytesPerSecond, ["floodKickDrops"] = c.FloodKickDrops,
            ["labPort"] = c.LabPort, ["serverId"] = c.ServerId, ["serverName"] = c.ServerName, ["modpack"] = ModpackJson(c.Modpack),
            ["advertise"] = c.Advertise,
        };
        var clients = new JsonArray();
        foreach (var p in Network.Peers)
        {
            clients.Add(new JsonObject
            {
                ["entity"] = p.Player.IsNone ? null : p.Player.Value, ["name"] = p.Name, ["state"] = p.State.ToString().ToLowerInvariant(),
                ["kiln"] = p.KilnVersion, ["version"] = p.KilnVersion, ["lab"] = p.IsLab,
                ["mods"] = new JsonArray([.. p.Mods.Select(m => (JsonNode)new JsonObject { ["id"] = m.Id, ["version"] = m.Version, ["side"] = m.Side.ToString().ToLowerInvariant() })]),
                ["missing"] = new JsonArray([.. p.Warnings.Where(w => w.StartsWith("server mod not on this client: ", StringComparison.Ordinal))
                    .Select(w => (JsonNode)w["server mod not on this client: ".Length..])]),
                ["warnings"] = p.Warnings.Count,
                ["roundTripMs"] = p.RoundTrip is { } rt ? Math.Round(rt.TotalMilliseconds, 1) : null,
            });
        }
        return new JsonObject
        {
            ["config"] = config, ["configFile"] = NetworkConfig.FilePath, ["configError"] = Network.ConfigError,
            ["serverId"] = adv.ServerId, ["serverName"] = adv.ServerName, ["kiln"] = adv.Kiln,
            ["modpack"] = ModpackJson(adv.Modpack), ["advertise"] = c.Advertise, ["pending"] = adv.Pending,
            ["required"] = new JsonArray([.. adv.Required.Select(r => (JsonNode)new JsonObject
            {
                ["id"] = r.Id, ["version"] = r.Version, ["hash"] = r.Hash, ["kind"] = r.Kind, ["name"] = r.Name,
            })]),
            ["clients"] = clients,
            ["transport"] = new JsonObject
            {
                ["available"] = st.Available, ["server"] = st.ServerRole, ["lab"] = st.LabTransport, ["peers"] = st.Peers,
                ["ready"] = st.ReadyPeers, ["channels"] = st.Channels, ["maxFrame"] = st.MaxFrame, ["framesIn"] = st.FramesIn,
                ["framesOut"] = st.FramesOut, ["dropped"] = st.Dropped,
            },
        };
    }

    private static JsonObject? ModpackJson(NetModpack? m) => m is null ? null : new JsonObject
    {
        ["url"] = m.Url, ["sha256"] = m.Sha256, ["name"] = m.Name, ["version"] = m.Version, ["size"] = m.Size,
    };

    /// <summary>state/native.json (the native loader's report) plus state/boot.json chain[].</summary>
    private static JsonObject NativeStatus()
    {
        var state = Path.GetFullPath(Path.Combine(DataDirectory, "..", "..", "state"));
        var native = ReadJson(Path.Combine(state, "native.json")) as JsonObject;
        var boot = ReadJson(Path.Combine(state, "boot.json")) as JsonObject;
        var result = native ?? new JsonObject();
        result["present"] = native is not null;
        var chain = boot?["chain"] as JsonArray;
        boot?.Remove("chain");
        result["chain"] = chain ?? new JsonArray();
        result["boot"] = boot is null ? null : new JsonObject
        {
            ["kiln"] = boot["kiln"]?.DeepClone(), ["pid"] = boot["pid"]?.DeepClone(), ["written"] = boot["written"]?.DeepClone(),
            ["policy"] = boot["policy"]?.DeepClone(), ["hooks"] = boot["hooks"]?.DeepClone(), ["safeIdle"] = boot["safeIdle"]?.DeepClone(),
            ["native"] = boot["native"]?.DeepClone(), ["profile"] = boot["profile"]?.DeepClone(),
        };
        return result;
    }

    private static JsonNode? ReadJson(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            throw new AdminError($"{Path.GetFileName(path)}: {e.Message}");
        }
    }

    private static JsonObject LogTail(JsonObject req)
    {
        var lines = LogTailDefault;
        if (req["lines"] is not null)
        {
            var n = Number(req, "lines");
            if (n < 1 || n > LogTailMax || n != Math.Floor(n)) throw new AdminError($"lines: 1 to {LogTailMax}");
            lines = (int)n;
        }
        var which = req["file"] is JsonValue fv && fv.TryGetValue<string>(out var f) && f.Length > 0 ? f : "kiln";
        var kiln = Path.GetFullPath(Path.Combine(DataDirectory, "..", ".."));
        string path, label;
        if (which is "kiln" or "kiln.log")
        {
            path = Path.Combine(kiln, "logs", "kiln.log");
            label = "kiln.log";
        }
        else if (which == "game")
        {
            path = GameLogPath(Path.GetDirectoryName(kiln)!);
            label = "enshrouded_server.log";
        }
        else
        {
            if (which.Length > 120 || which.Contains('/') || which.Contains('\\') || which.Contains("..") || which.StartsWith('.'))
                throw new AdminError("file: kiln, game or a file name under kiln/logs");
            path = Path.Combine(kiln, "logs", which);
            label = which;
        }
        if (!File.Exists(path)) throw new AdminError($"no log file {label}");
        var (text, size) = Tail(path, lines);
        return new JsonObject { ["file"] = label, ["lines"] = new JsonArray([.. text.Select(l => (JsonNode)l)]), ["size"] = size };
    }

    /// <summary>The game's log: enshrouded_server.json logDirectory (default ./logs) + enshrouded_server.log.</summary>
    private static string GameLogPath(string server)
    {
        var dir = "logs";
        try
        {
            var cfg = Path.Combine(server, "enshrouded_server.json");
            if (File.Exists(cfg) && JsonNode.Parse(File.ReadAllText(cfg))?["logDirectory"]?.GetValue<string>() is { Length: > 0 } d) dir = d;
        }
        catch (Exception)
        {
        }
        return Path.GetFullPath(Path.Combine(server, dir, "enshrouded_server.log"));
    }

    /// <summary>The last <paramref name="lines"/> lines (reads at most the last 8 MiB).</summary>
    internal static (List<string> Lines, long Size) Tail(string path, int lines)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var size = fs.Length;
        var chunk = 64 * 1024;
        var pos = size;
        var data = new List<byte[]>();
        var newlines = 0;
        long read = 0;
        while (pos > 0 && newlines <= lines && read < 8L << 20)
        {
            var n = (int)Math.Min(chunk, pos);
            pos -= n;
            var buf = new byte[n];
            fs.Seek(pos, SeekOrigin.Begin);
            fs.ReadExactly(buf);
            data.Insert(0, buf);
            read += n;
            foreach (var b in buf) if (b == (byte)'\n') newlines++;
        }
        var text = Encoding.UTF8.GetString([.. data.SelectMany(b => b)]);
        var all = text.Split('\n');
        var list = all.Select(l => l.TrimEnd('\r')).ToList();
        if (list.Count > 0 && list[^1].Length == 0) list.RemoveAt(list.Count - 1);
        if (pos > 0 && list.Count > 0) list.RemoveAt(0);  // a partial first line
        return (list.Count > lines ? list[^lines..] : list, size);
    }
}
