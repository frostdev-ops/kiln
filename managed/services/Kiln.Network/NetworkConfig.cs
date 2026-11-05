// SPDX-License-Identifier: MIT
using System.Text.Json;

namespace Kiln;

/// <summary>What the server does when a client's mods do not match (Forge's mod rejection).</summary>
public enum ModSyncPolicy
{
    /// <summary>No check: everyone joins, even vanilla clients on a server whose mods need Kiln.</summary>
    Off = 0,

    /// <summary>Join anyway; the mismatch is logged and shown to the player (Kiln clients) or logged (vanilla). Vanilla
    /// clients are still refused when a loaded mod declares <c>clients: "kiln"</c> or <c>"mod"</c>. This is also the value
    /// when <c>network.json</c> doesn't set <c>modSync</c>, with one difference: a Kiln client without a mod that states
    /// <c>clients: "mod"</c> is refused (see <see cref="NetworkConfig.ModSyncStated"/>).</summary>
    Warn = 1,

    /// <summary>A Kiln client that lacks a required mod is refused; a vanilla client is refused when the server has
    /// required mods.</summary>
    Refuse = 2,
}

/// <summary>How a client's mod version must match the server's.</summary>
public enum ModVersionMatch
{
    /// <summary>The same version string.</summary>
    Exact = 0,

    /// <summary>The same major version (semver: 1.2.3 and 1.4.0 match; 0.x: the same minor).</summary>
    Major = 1,

    /// <summary>Any version, as long as the mod is present.</summary>
    Any = 2,
}

/// <summary><c>kiln/config/network.json</c>: Kiln.Network's server settings. Unknown keys are ignored; a missing file
/// gives the defaults.</summary>
public sealed record NetworkConfig
{
    /// <summary>What to do with clients that lack a required (<c>side: both</c>) mod. Default: warn.</summary>
    public ModSyncPolicy ModSync { get; init; } = ModSyncPolicy.Warn;

    /// <summary>Version rule for required mods. Default: exact.</summary>
    public ModVersionMatch VersionMatch { get; init; } = ModVersionMatch.Exact;

    /// <summary><c>network.json</c> sets <see cref="ModSync"/>. When it doesn't, a Kiln client that lacks a mod stating
    /// <c>clients: "mod"</c> is refused, with a message naming the mods. A stated <c>warn</c> or <c>off</c> lets it in.</summary>
    public bool ModSyncStated { get; init; }

    /// <summary>Refuse every client without Kiln, whatever <see cref="ModSync"/> says. Default: false.</summary>
    public bool RequireKiln { get; init; }

    /// <summary><c>side: both</c> mods that clients may lack.</summary>
    public IReadOnlyList<string> OptionalMods { get; init; } = [];

    /// <summary>The game's login error shown to refused players (1 = Version, 5 = Generic).</summary>
    public uint KickReason { get; init; } = 1;

    /// <summary>A player who got past the gate (a build without it) and is in game this long without a Kiln hello is
    /// treated as a vanilla client.</summary>
    public double HandshakeTimeoutSeconds { get; init; } = 20;

    /// <summary>How long a client that finished loading without proof of Kiln waits in the gate before it is treated as
    /// a vanilla client: refused, or let in without what it can't show (docs/kiln/network.md). 1..20.</summary>
    public double VanillaHoldSeconds { get; init; } = 5;

    /// <summary>How long Kiln-only entities wait for a Kiln client's content ack, so that definitions the sync brings are in
    /// place before those entities arrive. Then they go out anyway. 1..30.</summary>
    public double DefinitionsWaitSeconds { get; init; } = 5;

    /// <summary>Largest message (after reassembly) in either direction.</summary>
    public int MaxMessageBytes { get; init; } = 256 * 1024;

    /// <summary>Messages a client may send per second (burst = 2 s worth); excess is dropped.</summary>
    public double MessagesPerSecond { get; init; } = 200;

    /// <summary>Bytes a client may send per second (burst = 2 s worth); excess is dropped.</summary>
    public double BytesPerSecond { get; init; } = 512 * 1024;

    /// <summary>Kick a client whose messages were dropped this many times within 10 s (0 = never).</summary>
    public int FloodKickDrops { get; init; } = 2000;

    /// <summary>Lab only: UDP port for tools/nettest's fake clients (read by the native service). 0 = off.</summary>
    public int LabPort { get; init; }

    /// <summary>The server's stable id (<c>^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$</c>) sent to clients in REJECT/WELCOME.
    /// Null (default): the id generated once into <c>kiln/data/kiln-network/server-id</c>.</summary>
    public string? ServerId { get; init; }

    /// <summary>The server's name sent to clients. Null (default): <c>enshrouded_server.json</c> <c>name</c>.</summary>
    public string? ServerName { get; init; }

    /// <summary>Where players get the mods this server needs (<c>modpack {url, sha256, name, version, size?}</c>);
    /// written by the launcher's modpack publish. Null: no modpack.</summary>
    public NetModpack? Modpack { get; init; }

    /// <summary>Send server id/name, Kiln version, the modpack and the required mods (kdh1) in REJECT and WELCOME
    /// (docs/kiln/network.md, "Server modpacks"). Default true.</summary>
    public bool Advertise { get; init; } = true;

    /// <summary><c>network.json</c> in the config folder (<c>kiln/config</c>, or the active profile's configDir).</summary>
    public static string? FilePath
    {
        get
        {
            try
            {
                return Path.Combine(KilnPaths.Config, "network.json");
            }
            catch
            {
                return null;
            }
        }
    }

    public static NetworkConfig Load(out string? error)
    {
        error = null;
        var path = FilePath;
        if (path is null || !File.Exists(path)) return new NetworkConfig();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            return Parse(doc.RootElement);
        }
        catch (Exception e)
        {
            error = $"{path}: {e.Message}";
            return new NetworkConfig();
        }
    }

    internal static NetworkConfig Parse(JsonElement root)
    {
        var c = new NetworkConfig();
        if (root.ValueKind != JsonValueKind.Object) return c;
        foreach (var p in root.EnumerateObject())
        {
            switch (p.Name)
            {
                case "modSync":
                    var modSync = p.Value.GetString()?.ToLowerInvariant();
                    c = c with
                    {
                        ModSync = modSync switch { "off" => ModSyncPolicy.Off, "refuse" => ModSyncPolicy.Refuse, _ => ModSyncPolicy.Warn },
                        ModSyncStated = modSync is "off" or "warn" or "refuse",
                    };
                    break;
                case "versionMatch":
                    c = c with { VersionMatch = p.Value.GetString()?.ToLowerInvariant() switch { "major" => ModVersionMatch.Major, "any" => ModVersionMatch.Any, _ => ModVersionMatch.Exact } };
                    break;
                case "requireKiln": c = c with { RequireKiln = p.Value.ValueKind == JsonValueKind.True }; break;
                case "optionalMods":
                    c = c with { OptionalMods = p.Value.ValueKind == JsonValueKind.Array ? p.Value.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToArray() : [] };
                    break;
                case "kickReason": c = c with { KickReason = p.Value.TryGetUInt32(out var kr) ? Math.Min(kr, 5u) : 1 }; break;
                case "handshakeTimeoutSeconds": c = c with { HandshakeTimeoutSeconds = Math.Clamp(p.Value.GetDouble(), 2, 600) }; break;
                case "vanillaHoldSeconds": c = c with { VanillaHoldSeconds = Math.Clamp(p.Value.GetDouble(), 1, 20) }; break;
                case "definitionsWaitSeconds": c = c with { DefinitionsWaitSeconds = Math.Clamp(p.Value.GetDouble(), 1, 30) }; break;
                case "maxMessageBytes": c = c with { MaxMessageBytes = Math.Clamp(p.Value.GetInt32(), 1024, 16 << 20) }; break;
                case "messagesPerSecond": c = c with { MessagesPerSecond = Math.Max(1, p.Value.GetDouble()) }; break;
                case "bytesPerSecond": c = c with { BytesPerSecond = Math.Max(1024, p.Value.GetDouble()) }; break;
                case "floodKickDrops": c = c with { FloodKickDrops = Math.Max(0, p.Value.GetInt32()) }; break;
                case "labPort": c = c with { LabPort = p.Value.GetInt32() }; break;
                case "serverId": c = c with { ServerId = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null }; break;
                case "serverName": c = c with { ServerName = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null }; break;
                case "advertise": c = c with { Advertise = p.Value.ValueKind != JsonValueKind.False }; break;
                case "modpack": c = c with { Modpack = NetModpack.Parse(p.Value) }; break;
            }
        }
        return c;
    }

    /// <summary>Whether <paramref name="client"/> satisfies <paramref name="server"/> under <see cref="VersionMatch"/>.</summary>
    public bool VersionsMatch(string server, string client)
    {
        if (VersionMatch == ModVersionMatch.Any) return true;
        if (string.Equals(server, client, StringComparison.Ordinal)) return true;
        if (VersionMatch == ModVersionMatch.Exact) return false;
        static (int, int)? Lead(string v)
        {
            var core = v.Split('-', '+')[0].Split('.');
            if (core.Length == 0 || !int.TryParse(core[0], out var major)) return null;
            var minor = core.Length > 1 && int.TryParse(core[1], out var m) ? m : 0;
            return (major, minor);
        }
        var a = Lead(server);
        var b = Lead(client);
        if (a is null || b is null) return false;
        return a.Value.Item1 == b.Value.Item1 && (a.Value.Item1 != 0 || a.Value.Item2 == b.Value.Item2);
    }
}
