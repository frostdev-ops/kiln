// SPDX-License-Identifier: MIT
// The mod platform part of the settings lab test (dev.20 WP-10): a per-world config that follows the world across a world
// switch (the order a game client hosts one world after another, played on Kiln.Saves' lab hooks as world-switch-test does),
// the mod's own texts under the real host, status lines and pacing. PASS / FAIL lines in kiln.log.
using Kiln;

namespace SettingsTest;

/// <summary>What a mod keeps for each world it plays in.</summary>
public sealed class WorldProbe
{
    [Comment("A text each world keeps for itself.")]
    public string Motto { get; set; } = "none";

    [Range(1, 100)]
    public int Limit { get; set; } = 10;

    [RequiresRestart]
    public int Slots { get; set; } = 4;
}

public sealed partial class SettingsTestMod
{
    private static readonly Guid OtherWorld = Guid.Parse("c0ffee00-0000-4000-8000-0000000000a1");
    private WorldConfig<WorldProbe>? _world;
    private string? _worldIdAtLoad;
    private readonly List<ConfigChangedEvent> _worldEvents = [];

    // Registered in OnLoad, as a mod does: on the first boot of a fresh lab world the world has no id yet, and the config
    // runs on its defaults until the world's first save gives it one.
    private void RegisterPlatform()
    {
        try
        {
            _worldIdAtLoad = Saves.Available ? Saves.WorldId : null;
            _world = WorldConfig<WorldProbe>();
            _world.Changed += _worldEvents.Add;
            Log.Info($"settings-test: per-world config {_world.Id} registered; world at load: {_worldIdAtLoad ?? "(no id yet)"}, bound to {_world.WorldId ?? "(none)"}");
        }
        catch (Exception e)
        {
            Fail("world.register", e.ToString());
        }
    }

    private async Task Platform()
    {
        await WorldConfigAcrossWorlds();
        Texts();
        Status();
        PacingChecks();
    }

    private async Task WorldConfigAcrossWorlds()
    {
        if (_world is null || !Saves.Available)
        {
            Fail("world", "the per-world config or kiln.saves is not there");
            return;
        }
        var cfg = _world;
        var saved = await Saves.Request();
        await Steps(5);
        var worldA = Saves.WorldId;
        Check(saved.Success && worldA is not null, "world.saved", $"world saved ({saved}), id {worldA}");
        if (worldA is null) return;
        var revision = Saves.Status?.Revision ?? 0;
        var dirA = Saves.DataDirectory ?? "";
        Check(cfg.WorldId == worldA, "world.bound", $"the config is bound to world {cfg.WorldId} (world at load: {_worldIdAtLoad ?? "no id yet"})");
        Check(cfg.FilePath == Path.Combine(dirA, "config", "settings-test-world.json") && File.Exists(cfg.FilePath), "world.file",
            $"its file lies beside the world's data: {cfg.FilePath}");
        Check(File.ReadAllText(cfg.FilePath).Contains("A text each world keeps for itself."), "world.file.comments", "the file is commented from the class");

        _worldEvents.Clear();
        cfg.Set("motto", "world A");
        cfg.Set("limit", 33);
        Check(cfg.Value.Motto == "world A" && cfg.Value.Limit == 33 && File.ReadAllText(cfg.FilePath).Contains("\"motto\": \"world A\""),
            "world.set", "values set in world A are in its file");

        // ---- the next world: the game closes this one and loads another in the same process ----
        _worldEvents.Clear();
        Saves.LabCloseWorld();
        Check(cfg.WorldId is null && cfg.FilePath.Length == 0 && cfg.Value.Motto == "none" && cfg.Value.Limit == 10, "world.closed",
            $"closed: no world, the defaults ({cfg.Value.Motto}, {cfg.Value.Limit})");
        Saves.LabWorldLoaded(OtherWorld.ToString(), 1);
        Check(cfg.WorldId == OtherWorld.ToString() && cfg.Value.Motto == "none" && cfg.Value.Limit == 10, "world.b.defaults",
            $"world B ({cfg.WorldId}): its own defaults, nothing of A ({cfg.Value.Motto}, {cfg.Value.Limit})");
        var dirB = Saves.DataDirectory ?? "";
        Check(cfg.FilePath == Path.Combine(dirB, "config", "settings-test-world.json") && File.Exists(cfg.FilePath) && dirB != dirA, "world.b.file",
            $"and a file of its own: {cfg.FilePath}");
        Check(_worldEvents.Any(e => e.Source == ConfigChangeSource.World && e.Has("motto") && e.Has("limit")), "world.b.event",
            $"Changed fired with source World: {string.Join("; ", _worldEvents.Select(e => $"{e.Source} [{string.Join(",", e.Changed)}]"))}");
        cfg.Set("motto", "world B");
        await Steps(3);

        // ---- back to A ----
        _worldEvents.Clear();
        Saves.LabCloseWorld();
        Saves.LabWorldLoaded(worldA, revision);
        Check(cfg.WorldId == worldA && cfg.Value.Motto == "world A" && cfg.Value.Limit == 33, "world.a.back",
            $"world A again: its values came back from its file ({cfg.Value.Motto}, {cfg.Value.Limit})");
        Check(File.ReadAllText(Path.Combine(dirB, "config", "settings-test-world.json")).Contains("\"motto\": \"world B\""), "world.b.kept",
            "world B's file still holds its own value");
        Check(_worldEvents.Any(e => e.Source == ConfigChangeSource.World && e.Has("motto") && e.Has("limit")), "world.a.event",
            $"Changed fired with source World: {string.Join("; ", _worldEvents.Select(e => $"{e.Source} [{string.Join(",", e.Changed)}]"))}");
        Check(Saves.WorldDataReady && Saves.DataDirectory == dirA, "world.a.sidecar", $"WorldData's sidecar is A's again: {Saves.DataDirectory}");

        // ---- an edit of the file while the world runs (the file watcher, or the five-second poll) ----
        _worldEvents.Clear();
        File.WriteAllText(cfg.FilePath, File.ReadAllText(cfg.FilePath).Replace("\"world A\"", "\"edited on disk\"").Replace("\"slots\": 4", "\"slots\": 8"));
        var waited = 0;
        while (cfg.Value.Motto != "edited on disk" && waited < 400)
        {
            await Steps(1);
            waited++;
        }
        Check(cfg.Value.Motto == "edited on disk", "world.edit", $"an edit of the world's file applied after {waited} steps ({waited / 24.5:F1} s)");
        Check(cfg.Value.Slots == 4 && cfg.PendingRestart.Contains("slots"), "world.edit.restart",
            $"[RequiresRestart] 'slots' waits for the world to load again (running {cfg.Value.Slots}, pending {string.Join(",", cfg.PendingRestart)})");
        Check(_worldEvents.Any(e => e.Source == ConfigChangeSource.File && e.Has("motto")), "world.edit.event",
            $"Changed fired with source File: {string.Join("; ", _worldEvents.Select(e => $"{e.Source} [{string.Join(",", e.Changed)}]"))}");
        Saves.LabCloseWorld();
        Saves.LabWorldLoaded(worldA, revision);
        Check(cfg.Value.Slots == 8, "world.edit.reload", $"loading the world again applies it (slots {cfg.Value.Slots})");
        Log.Info("settings-test: per-world config sequence ended in world A, as it began");
    }

    // The mod's locales/*.json under the real host: the deployed folder, a server that speaks English.
    private void Texts()
    {
        var l = Context.Locale;
        Check(l.Languages.Contains("en") && l.Languages.Contains("de"), "locale.files", $"locales found beside the manifest: {string.Join(", ", l.Languages)} ({l.Directory})");
        Check(Game.Language == "en-us", "locale.server", $"a dedicated server speaks English: Game.Language = {Game.Language}");
        Check(Text("greeting", new { name = "Ada" }) == "Hello Ada", "locale.text", $"Mod.Text: {Text("greeting", new { name = "Ada" })}");
        Check(TextIn("de-de", "probe.count", 2) == "2 Welten gesehen", "locale.language", $"another language on request: {TextIn("de-de", "probe.count", 2)}");
        Check(TextIn("de-de", "only.en") == "English only", "locale.fallback", "a key the German file lacks falls back to English");
        Check(l.Missing("de").SequenceEqual(["only.en"]), "locale.missing", $"German still owes: {string.Join(", ", l.Missing("de"))}");
        Check(Text("no.such.key") == "no.such.key", "locale.key", "a key nobody has comes back as the key");
    }

    private void Status()
    {
        StatusLines.Set("probe", "settings-test: platform checks ran");
        StatusLines.Add(() => $"{_pass} checks passed so far", "count");
        var lines = StatusLines.Lines("settings-test");
        Check(lines.Count == 2 && lines[0].Contains("platform checks ran") && lines[1].EndsWith("checks passed so far"), "status.lines", string.Join(" | ", lines));
        Check(StatusLines.Summary().Contains("settings-test"), "status.summary", StatusLines.Summary().Replace("\n", " / "));
        StatusLines.WriteToLog(Log);
    }

    // Kiln's native half does not hook the game's waits yet: the API runs, the handlers are never called, and says so.
    private void PacingChecks()
    {
        var calls = 0;
        var h = Pacing.OnServerWait((ref long w) => calls++);
        Log.Info($"settings-test: pacing sites on this build: {Pacing.Sites}");
        Check(h.Found == ((Pacing.Sites & PacingSites.ServerWait) != 0), "pacing.found", $"a server-wait handler: Found = {h.Found}");
        Check(!Pacing.CapTaskWorkers(TaskWorkerCaps.AtMost(2)) || (Pacing.Sites & PacingSites.TaskWorkers) != 0, "pacing.caps",
            $"worker caps: effective {Pacing.EffectiveTaskWorkerCaps}");
        h.Dispose();
    }
}
