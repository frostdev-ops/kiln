// SPDX-License-Identifier: MIT
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>
/// Kiln's world backups: <c>kiln/backups/&lt;UTC stamp&gt;-&lt;name&gt;/</c> with <c>backup.json</c>,
/// <c>savegame/world&lt;N&gt;.ksd</c> and <c>worlds/&lt;world id&gt;/{manifests,blobs}</c> (the sidecar generation of
/// that world file). Restores are staged in <c>kiln/backups/restore.json</c> and applied by the native service at the
/// next server start, before the game opens its save.
/// </summary>
internal static class SavesBackups
{
    internal static void CheckName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 64 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) || name.StartsWith('.'))
            throw new ArgumentException("backup names are 1-64 characters of A-Z a-z 0-9 - _ .", nameof(name));
    }

    private static string Dir => SavesNative.GetPath(SavesNative.PathBackups) ?? throw SavesCaps.Off(SavesCaps.Service, "kiln.saves is unavailable");

    /// <summary>Copies the world file (revision <paramref name="rev"/>, just written) and its sidecar generation. Tick
    /// thread, right after the write: the game cannot start another save before this returns.</summary>
    internal static BackupInfo Create(string name, uint rev, uint unix, SaveReason reason)
    {
        var store = SavesRuntime.Store;
        var world = SavesRuntime.WorldFile ?? throw new InvalidOperationException("the world file is not known");
        var h = WorldStore.ReadHeader(world) ?? throw new InvalidOperationException($"{world} is not readable");
        if (h.Revision != rev) throw new InvalidOperationException($"{Path.GetFileName(world)} is at revision {h.Revision}, expected {rev}");
        var id = h.Guid.ToString();
        var created = DateTimeOffset.UtcNow;
        var folder = $"{created:yyyyMMdd-HHmmss}-{name}";
        var dest = Path.Combine(Dir, folder);
        for (var n = 2; Directory.Exists(dest); n++) dest = Path.Combine(Dir, folder = $"{created:yyyyMMdd-HHmmss}-{name}-{n}");
        var tmp = dest + ".partial";
        Directory.CreateDirectory(Path.Combine(tmp, "savegame"));
        var files = new JsonArray();
        long bytes = 0;
        void Add(string src, string rel)
        {
            var to = Path.Combine(tmp, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            var data = File.ReadAllBytes(src);
            File.WriteAllBytes(to, data);
            bytes += data.Length;
            files.Add(new JsonObject { ["path"] = rel.Replace('\\', '/'), ["size"] = data.Length, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(data)) });
        }
        var worldName = Path.GetFileName(world);
        Add(world, Path.Combine("savegame", worldName));
        var copied = WorldStore.ReadHeader(Path.Combine(tmp, "savegame", worldName));
        if (copied is not { } c || c.Revision != rev || c.Guid != h.Guid)
        {
            Directory.Delete(tmp, true);
            throw new InvalidOperationException("the world file changed while it was copied");
        }
        var bundles = 0;
        var manifest = store.WorldId == id ? store.CommittedManifest(rev, h.Unix) : null;
        if (manifest != null)
        {
            Add(manifest, Path.Combine("worlds", id, "manifests", Path.GetFileName(manifest)));
            foreach (var blob in store.BlobsOf(manifest))
            {
                Add(blob, Path.Combine("worlds", id, "blobs", Path.GetFileName(Path.GetDirectoryName(blob))!, Path.GetFileName(blob)));
                bundles++;
            }
        }
        var info = new JsonObject
        {
            ["format"] = 1,
            ["name"] = name,
            ["created"] = created.ToString("O"),
            ["worldId"] = id,
            ["revision"] = rev,
            ["worldUnix"] = h.Unix,
            ["reason"] = Saves.Text(reason),
            ["saveFiles"] = new JsonArray(worldName),
            ["dataManifest"] = manifest != null ? Path.GetFileName(manifest) : null,
            ["dataBundles"] = bundles,
            ["files"] = files,
            ["bytes"] = bytes,
            ["kiln"] = Game.KilnVersion,
        };
        File.WriteAllText(Path.Combine(tmp, "backup.json"), info.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Directory.Move(tmp, dest);
        SavesRuntime.Log(LogLevel.Info, $"Kiln.Saves: backup {folder}: {worldName} revision {rev} + mod data generation " +
                                        $"{(manifest != null ? rev.ToString() : "none")} ({bundles} bundles), {files.Count} files, {bytes / 1024} KB");
        return Read(dest)!;
    }

    internal static BackupInfo? Read(string dir)
    {
        try
        {
            var j = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "backup.json")))!.AsObject();
            Saves.TryParse(j["reason"]?.GetValue<string>(), out var reason);
            return new BackupInfo(j["name"]!.GetValue<string>(), dir, DateTimeOffset.Parse(j["created"]!.GetValue<string>()),
                j["worldId"]!.GetValue<string>(), j["revision"]!.GetValue<uint>(), j["worldUnix"]?.GetValue<uint>() ?? 0,
                reason ?? SaveReason.Unknown, (j["files"] as JsonArray)?.Count ?? 0, j["bytes"]?.GetValue<long>() ?? 0,
                j["dataBundles"]?.GetValue<int>() ?? 0);
        }
        catch (Exception e) when (e is IOException or JsonException or NullReferenceException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<BackupInfo> List()
    {
        var d = SavesNative.GetPath(SavesNative.PathBackups);
        if (d == null || !Directory.Exists(d)) return [];
        return Directory.EnumerateDirectories(d).Where(p => !Path.GetFileName(p).StartsWith('_') && !p.EndsWith(".partial"))
            .Select(Read).OfType<BackupInfo>().OrderBy(b => b.Created).ToList();
    }

    internal static BackupInfo StageRestore(string name)
    {
        if (!SavesRuntime.Ensure()) throw SavesCaps.Off(SavesCaps.Service, "kiln.saves is unavailable");
        if (SavesNative.ReadStatus() is { } st && (st.Features & SavesNative.ClientHosted) != 0)
            throw new NotSupportedException("restores are for dedicated servers: a game client's worlds live in its Steam " +
                                            "cloud or local save folder, which Kiln never writes");
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var all = List();
        var b = all.LastOrDefault(x => Path.GetFileName(x.Directory) == name) ?? all.LastOrDefault(x => x.Name == name)
                ?? throw new ArgumentException($"no backup '{name}' in {Dir}", nameof(name));
        var req = new JsonObject
        {
            ["backup"] = Path.GetFileName(b.Directory),
            ["worldId"] = b.WorldId,
            ["revision"] = b.Revision,
            ["staged"] = DateTimeOffset.UtcNow.ToString("O"),
            ["by"] = ModContext.CurrentMod?.Info.Id,
        };
        WorldStore.WriteAtomic(Path.Combine(Dir, "restore.json"), System.Text.Encoding.UTF8.GetBytes(req.ToJsonString()));
        SavesRuntime.Log(LogLevel.Info, $"Kiln.Saves: restore of {Path.GetFileName(b.Directory)} (revision {b.Revision}) staged for the next server start");
        return b;
    }

    internal static bool CancelRestore()
    {
        var p = Path.Combine(Dir, "restore.json");
        if (!File.Exists(p)) return false;
        File.Delete(p);
        return true;
    }

    internal static string? PendingRestore()
    {
        var d = SavesNative.GetPath(SavesNative.PathBackups);
        if (d == null) return null;
        var p = Path.Combine(d, "restore.json");
        try
        {
            return File.Exists(p) ? JsonNode.Parse(File.ReadAllText(p))?["backup"]?.GetValue<string>() : null;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }
}
