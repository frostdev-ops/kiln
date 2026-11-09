// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using Kiln.Testing;

namespace Kiln.Admin;

/// <summary>Mod management (Kiln.Host's loader): list, reload/unload/load, run a mod's tests, crash reports.
/// Kiln.Admin refuses to unload or reload itself from the dashboard (its answer would never arrive).</summary>
internal static partial class Ops
{
    private const string SelfId = "kiln-admin";

    static partial void RegisterMods(Bridge bridge, Logger log)
    {
        bridge.Register("mods", _ => new JsonArray([.. Mods.All.Select(m => (JsonNode)ModJson(m))]));
        bridge.RegisterAsync("mod_reload", async req =>
        {
            var id = ModId(req);
            var r = await Mods.ReloadAsync(id);
            log.Info($"dashboard reload {id}: {r.Message}");
            return OpResult(r);
        });
        bridge.RegisterAsync("mod_unload", async req =>
        {
            var id = ModId(req);
            var r = await Mods.UnloadAsync(id);
            log.Info($"dashboard unload {id}: {r.Message}");
            return OpResult(r);
        });
        // {folder}: a folder name under kiln/mods (only there; no paths)
        bridge.RegisterAsync("mod_load", async req =>
        {
            var folder = req["folder"]?.GetValue<string>() ?? throw new AdminError("folder required");
            if (folder.Length == 0 || folder.Contains('/') || folder.Contains('\\') || folder.Contains(".."))
                throw new AdminError("folder must be a folder name under kiln/mods");
            var r = await Mods.LoadAsync(folder);
            log.Info($"dashboard load {folder}: {r.Message}");
            return OpResult(r);
        });
        // {id, filter?} -> the mod's [KilnTest] results
        bridge.RegisterAsync("mod_test", async req =>
        {
            var id = req["id"]?.GetValue<string>() ?? throw new AdminError("id required");
            TestReport report;
            try
            {
                report = await Tests.RunAsync(id, req["filter"]?.GetValue<string>());
            }
            catch (InvalidOperationException e)
            {
                throw new AdminError(e.Message);
            }
            return new JsonObject
            {
                ["mod"] = report.Mod, ["version"] = report.Version, ["ms"] = Math.Round(report.Ms, 1), ["passed"] = report.Passed,
                ["failed"] = report.Failed, ["skipped"] = report.Skipped, ["file"] = report.File,
                ["results"] = new JsonArray([.. report.Results.Select(r => (JsonNode)new JsonObject
                {
                    ["name"] = r.Name, ["outcome"] = r.Outcome.ToString(), ["message"] = r.Message,
                })]),
            };
        });
        bridge.Register("crash_reports", req =>
        {
            var dir = CrashDirectory();
            if (req["name"]?.GetValue<string>() is { Length: > 0 } name)
            {
                if (name.Contains('/') || name.Contains('\\') || name.Contains("..")) throw new AdminError("bad report name");
                var file = Path.Combine(dir, name);
                if (!File.Exists(file)) throw new AdminError($"no crash report '{name}'");
                var text = File.ReadAllText(file);
                return new JsonObject { ["name"] = name, ["text"] = text.Length > 200_000 ? text[..200_000] : text };
            }
            var list = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.txt").Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).Take(100)
                : [];
            return new JsonArray([.. list.Select(f => (JsonNode)new JsonObject
            {
                ["name"] = f.Name, ["bytes"] = f.Length, ["time"] = f.LastWriteTimeUtc.ToString("O"),
            })]);
        });
    }

    static partial void StateMods(JsonObject state)
    {
        var all = Mods.All;
        state["mods"] = new JsonObject
        {
            ["loaded"] = all.Count(m => m.State == ModState.Loaded),
            ["failed"] = all.Count(m => m.State == ModState.Failed),
            ["disabled"] = all.Count(m => m.State == ModState.Disabled),
            ["phase"] = Lifecycle.Phase.ToString(),
        };
    }

    private static string ModId(JsonObject req)
    {
        var id = req["id"]?.GetValue<string>() ?? throw new AdminError("id required");
        if (string.Equals(id, SelfId, StringComparison.OrdinalIgnoreCase))
            throw new AdminError("Kiln.Admin can't reload or unload itself from the dashboard; use kiln/control or a restart");
        return id;
    }

    private static JsonObject ModJson(ModEntry m)
    {
        var s = m.Stats;
        return new JsonObject
        {
            ["id"] = m.Id, ["name"] = m.Name, ["version"] = m.Version, ["side"] = m.Side.ToString(), ["state"] = m.State.ToString(),
            ["error"] = m.Error, ["loadIndex"] = m.LoadIndex, ["advanced"] = m.Info?.Advanced,
            // for the dashboard: mod_load takes the folder name; description/authors from the manifest
            ["folder"] = string.IsNullOrEmpty(m.Directory) ? null : Path.GetFileName(Path.TrimEndingDirectorySeparator(m.Directory)),
            ["description"] = m.Info?.Description, ["authors"] = new JsonArray([.. (m.Info?.Authors ?? []).Select(a => (JsonNode)a)]),
            ["dependencies"] = new JsonArray([.. m.Dependencies.Select(d => (JsonNode)$"{d.Id} {d.VersionRange}{(d.Optional ? " (optional)" : "")}")]),
            ["preMsPerStep"] = Math.Round(s.PreMsPerStep, 4), ["postMsPerStep"] = Math.Round(s.PostMsPerStep, 4),
            ["maxMs"] = Math.Round(s.MaxMs, 2), ["faults"] = s.Faults, ["lastFault"] = s.LastFaultMessage,
            ["statusLines"] = StatusLinesOf(m.Id),
        };
    }

    private static bool _noStatusLines;

    // What the mod says about itself (StatusLines, Kiln.Ui); null when it says nothing or Kiln.Ui is not there. The assembly is
    // only touched in ReadStatusLines, so its absence surfaces here (when that method is jitted) and not in the mod list.
    private static JsonArray? StatusLinesOf(string modId)
    {
        if (_noStatusLines) return null;
        try
        {
            return ReadStatusLines(modId);
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException or MissingMethodException)
        {
            _noStatusLines = true;
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // A cached snapshot (refreshed about once a second on a worker) that never holds this tick up, however slow a mod's status
    // source is: called once per mod, it answers from the last snapshot and starts at most one refresh.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static JsonArray? ReadStatusLines(string modId)
    {
        var lines = StatusLines.RecentLines(modId);
        return lines.Count == 0 ? null : new JsonArray([.. lines.Select(l => (JsonNode)l)]);
    }

    private static JsonObject OpResult(ModOperationResult r) => new()
    {
        ["ok"] = r.Ok, ["message"] = r.Message, ["affected"] = new JsonArray([.. r.Affected.Select(a => (JsonNode)a)]),
    };

    private static string CrashDirectory() =>
        Path.GetFullPath(Path.Combine(DataDirectory, "..", "..", "crash-reports"));  // kiln/data/kiln-admin → kiln/crash-reports
}
