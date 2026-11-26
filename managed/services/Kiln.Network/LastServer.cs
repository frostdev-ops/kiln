// SPDX-License-Identifier: MIT
// Client side of server modpacks: kiln/state/last-server.json (schema/state-last-server.schema.json), written after a
// server answered the join with REJECT or WELCOME (or did not answer: vanilla). The launcher watches the file and
// offers the server's modpack (consent modal; nothing downloads by itself).
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

internal static class LastServer
{
    public const int HistoryMax = 20;

    public static string FilePath => Path.Combine(KilnPaths.State, "last-server.json");

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly object WriteLock = new();

    /// <summary>Records an answer from the server. Hashing the local mods runs on the thread pool; the file is written
    /// when that is done (a REJECT's kick follows 1.5 s later, the process keeps running). For a refusal,
    /// <paramref name="refusedFor"/> is the REJECT's details: <c>missing</c> then lists the mods the server named
    /// (<see cref="NetRefusal.Missing"/>), not every required mod this client lacks.</summary>
    public static void Record(string outcome, string? reason, NetServerAdvert advert, string? serverKiln, IReadOnlyList<string>? refusedFor = null)
    {
        var local = LocalMods();
        Task.Run(() =>
        {
            try
            {
                var entry = Answer(outcome, reason, advert, serverKiln, refusedFor, IsHosted(advert), local, KilnPaths.Server, DateTime.UtcNow);
                Write(FilePath, entry);
                NetEngine.Log(LogLevel.Info, LogLine(entry));
            }
            catch (Exception e)
            {
                NetEngine.Log(LogLevel.Warn, $"last-server.json: {e.Message}");
            }
        });
    }

    /// <summary>The server is this game's own world (a player hosting it, single-player or co-op): the server role runs in
    /// this process and the server id it advertised is this game's (NetAdvertise.ServerId; one id file per game folder).
    /// Then the client and the server are one install and nothing it advertises can be missing here. A client that serves
    /// no world never reads (and so never makes) a server id.</summary>
    internal static bool IsHosted(NetServerAdvert advert)
    {
        if (advert.ServerId is not { } id) return false;
        var serving = NetEngine.TestServerRole ?? NetNative.ReadStatus() is { ServerReady: not 0 };
        return serving && string.Equals(id, NetAdvertise.ServerId(NetEngine.Config), StringComparison.Ordinal);
    }

    /// <summary>The entry for an answer. <c>missing</c>: on a refusal the mods the server named (a refusal without
    /// details: the required mods this game lacks); from the hosted world none; else the required mods this game lacks
    /// (<see cref="Lacking"/>, which hashes them: only when its answer is used).</summary>
    internal static JsonObject Answer(string outcome, string? reason, NetServerAdvert advert, string? serverKiln,
        IReadOnlyList<string>? refusedFor, bool hosted, List<Local> local, string game, DateTime now)
    {
        IReadOnlyList<string> missing = refusedFor is not null
            ? NetRefusal.Missing(refusedFor, refusedFor.Count == 0 ? Lacking(advert.Required, local, game) : [])
            : hosted ? [] : Lacking(advert.Required, local, game);
        var entry = Build(outcome, reason, advert, serverKiln, missing, now);
        entry["hosted"] = hosted;
        return entry;
    }

    /// <summary>kiln.log's line for an entry: which server (the hosted world said as such, so a player's own world is not
    /// read as a dedicated server lacking mods), what is missing, the modpack.</summary>
    internal static string LogLine(JsonObject entry)
    {
        var outcome = entry["outcome"]?.GetValue<string>() ?? "?";
        var hosted = entry["hosted"]?.GetValue<bool>() == true;
        var id = entry["server"]?["id"]?.GetValue<string>();
        var name = entry["server"]?["name"]?.GetValue<string>();
        var who = id is null ? "a server without Kiln's server properties"
            : hosted ? $"your hosted world ({id}, this game)"
            : $"server {id}" + (string.IsNullOrWhiteSpace(name) ? "" : $" \"{name}\"");
        var line = $"last-server.json: {outcome} {(outcome == "rejected" ? "by" : "from")} {who}";
        var missing = entry["missing"] is JsonArray m ? m.Select(x => x?.GetValue<string>()).ToList() : [];
        if (missing.Count > 0) line += $", missing {string.Join(", ", missing)}" + (hosted ? "" : " (this game lacks them, has them switched off or has another version)");
        else if (outcome == "welcome") line += ", nothing missing";
        if (entry["modpack"]?["url"]?.GetValue<string>() is { } url) line += $", modpack {url}";
        return line;
    }

    /// <summary>One last-server entry (without history).</summary>
    internal static JsonObject Build(string outcome, string? reason, NetServerAdvert advert, string? serverKiln,
        IReadOnlyList<string> missing, DateTime now) => new()
    {
        ["format"] = 1,
        ["written"] = now.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
        ["outcome"] = outcome,
        ["reason"] = reason,
        ["server"] = new JsonObject
        {
            ["id"] = advert.ServerId, ["name"] = advert.ServerName, ["address"] = null, ["kiln"] = advert.Kiln ?? serverKiln,
        },
        ["modpack"] = advert.Modpack?.ToJson(),
        ["required"] = new JsonArray([.. advert.Required.Select(r => (JsonNode)r.ToJsonFull())]),
        ["truncated"] = advert.Truncated,
        ["missing"] = new JsonArray([.. missing.Select(m => (JsonNode)m)]),
    };

    /// <summary>Writes <paramref name="entry"/> atomically; the previous entry moves to the front of history (at most 20).</summary>
    internal static void Write(string path, JsonObject entry)
    {
        lock (WriteLock)
        {
            var history = new JsonArray();
            try
            {
                if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject prev && prev["format"]?.GetValue<int>() == 1)
                {
                    var older = prev["history"] as JsonArray;
                    prev.Remove("history");
                    history.Add(prev);
                    if (older != null)
                        foreach (var h in older.ToList())
                        {
                            if (history.Count >= HistoryMax) break;
                            if (h is not JsonObject ho) continue;
                            older.Remove(ho);
                            ho.Remove("history");
                            history.Add(ho);
                        }
                }
            }
            catch (Exception)
            {
                // a broken file starts a fresh history
            }
            entry["history"] = history;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, entry.ToJsonString(Indented) + "\n");
            File.Move(tmp, path, overwrite: true);
        }
    }

    internal sealed record Local(string Kind, string Id, string Folder);

    /// <summary>This client's Kiln and native mods (managed mods from Kiln.Host, native mods from state/native.json). Other
    /// engines' mods are read from the game folder by <see cref="Lacking"/>.</summary>
    private static List<Local> LocalMods()
    {
        var list = new List<Local>();
        try
        {
            foreach (var m in Mods.All)
                if (!string.IsNullOrEmpty(m.Directory) && m.State is ModState.Loaded or ModState.Disabled)
                    list.Add(new Local("kiln", m.Id, m.Directory));
        }
        catch (Exception)
        {
        }
        try
        {
            var native = Path.Combine(KilnPaths.State, "native.json");
            if (File.Exists(native) && JsonNode.Parse(File.ReadAllText(native))?["mods"] is JsonArray mods)
                foreach (var n in mods)
                    if (n?["id"]?.GetValue<string>() is { } id && n["folder"]?.GetValue<string>() is { Length: > 0 } folder &&
                        n["state"]?.GetValue<string>() == "loaded")
                        list.Add(new Local("native", id, folder));
        }
        catch (Exception)
        {
        }
        return list;
    }

    /// <summary>The required ids this game lacks: <paramref name="local"/> (its Kiln and native mods) and the other
    /// engines' mods in <paramref name="game"/>'s folders (ShroudForge, Flame, Green Eye, SET ...: the folders and ids the
    /// server reads its own from, NetAdvertise.EngineMods), each kind compared as the server advertises it. Only mods
    /// that run here count: one switched off (ShroudForge's <c>enabled: false</c>, which Kiln writes for a mod switched
    /// off in the profile; <c>targets</c> without "client") is missing, the server needs it running, as a Kiln mod
    /// switched off in the profile is. Mods a Kiln client runs from the Library (EML's, EMF's DLL mods and Shroudtopia's
    /// without that engine's own loader) are not in those folders and so still count as missing. Up to 0.2.0-dev.31.17
    /// only Kiln and native mods were looked at, so every other engine's mod was "missing", even in the player's own
    /// hosted world.</summary>
    internal static List<string> Lacking(IReadOnlyList<NetRequiredMod> required, List<Local> local, string game)
    {
        var kinds = required.Select(r => r.Kind).Where(k => k is not ("kiln" or "native")).ToHashSet(StringComparer.Ordinal);
        var all = new List<Local>(local);
        if (kinds.Count > 0)
            try
            {
                all.AddRange(NetAdvertise.EngineMods(game, kinds).Select(c => new Local(c.Kind, c.Id, c.Dir)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        return Missing(required, all, game);
    }

    /// <summary>Required ids none of <paramref name="local"/>'s mods of the same kind and id matches by the hash the
    /// server advertises (<see cref="NetAdvertise.AdvertisedHash"/>: a Kiln mod by its identity, what the cook made on
    /// either machine does not count; another engine's mod by what stays the same while it runs).
    /// <paramref name="game"/>: the game folder whose kiln/state holds the receipts of Kiln's copies (null: this one).</summary>
    internal static List<string> Missing(IReadOnlyList<NetRequiredMod> required, List<Local> local, string? game = null)
    {
        game ??= KilnPaths.Server;
        var missing = new List<string>();
        foreach (var r in required)
            if (!local.Any(l => l.Kind == r.Kind && l.Id == r.Id && Has(l, r.Hash, game)))
                missing.Add(r.Id);
        return missing;
    }

    private static bool Has(Local l, string hash, string game)
    {
        try
        {
            return NetAdvertise.AdvertisedHash(l.Kind, l.Folder, game) == hash;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
