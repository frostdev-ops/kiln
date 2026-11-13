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
                var lacking = Missing(advert.Required, local);
                var missing = refusedFor is null ? lacking : NetRefusal.Missing(refusedFor, lacking);
                var entry = Build(outcome, reason, advert, serverKiln, missing, DateTime.UtcNow);
                Write(FilePath, entry);
                NetEngine.Log(LogLevel.Info, $"last-server.json: {outcome}" +
                                              (advert.ServerId is { } id ? $" from {id}" : "") +
                                              (entry["missing"] is JsonArray m && m.Count > 0 ? $", missing {string.Join(", ", m.Select(x => x?.GetValue<string>()))}" : "") +
                                              (advert.Modpack is { } mp ? $", modpack {mp.Url}" : ""));
            }
            catch (Exception e)
            {
                NetEngine.Log(LogLevel.Warn, $"last-server.json: {e.Message}");
            }
        });
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

    /// <summary>This client's mod folders (managed mods from Kiln.Host, native mods from state/native.json).</summary>
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

    /// <summary>Required ids this client lacks, or has with another hash. A Kiln mod is compared by its identity
    /// (<see cref="DirHash.ModIdentity"/>): what the cook made on either machine does not count.</summary>
    internal static List<string> Missing(IReadOnlyList<NetRequiredMod> required, List<Local> local)
    {
        var missing = new List<string>();
        foreach (var r in required)
        {
            var have = local.FirstOrDefault(l => l.Kind == r.Kind && l.Id == r.Id);
            if (have is null)
            {
                missing.Add(r.Id);
                continue;
            }
            try
            {
                if ((have.Kind == "kiln" ? DirHash.ModIdentity(have.Folder) : DirHash.Compute(have.Folder)) != r.Hash) missing.Add(r.Id);
            }
            catch (Exception)
            {
                missing.Add(r.Id);
            }
        }
        return missing;
    }
}
