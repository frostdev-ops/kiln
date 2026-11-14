// SPDX-License-Identifier: MIT
// Chapter 08's forced-drop proof: once the world is up, one line per thing a service holds, sorted within each service, so that two starts
// compare line by line ("boot-drop-observer: <service>: ..."). Present in every start; run.sh's compare.py diffs these lines.
using Kiln;

namespace BootDropObserver;

public sealed class BootDropObserverMod : Mod
{
    public override void OnLoad()
    {
        if (Mods.Side != ModSide.Server) return;
        _ = Run();
    }

    void Line(string service, string text) => Context.Log.Info($"boot-drop-observer: {service}: {text}");

    void Each(string service, IEnumerable<string> lines)
    {
        var list = lines.OrderBy(x => x, StringComparer.Ordinal).ToList();
        Line(service, $"{list.Count} line(s)");
        foreach (var l in list) Line(service, l);
    }

    void Try(string service, Action a)
    {
        try { a(); }
        catch (Exception e) { Line(service, $"read failed: {e.GetType().Name}: {e.Message}"); }
    }

    async Task Run()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!Progression.Available && sw.Elapsed.TotalSeconds < 240) await Context.Scheduler.NextTick();
        for (var i = 0; i < 240; i++) await Context.Scheduler.NextTick();
        // run.sh snapshot only: a code definition, which kiln.skills keeps in its cache (data/skills/defined/<mod>.json) and reads back
        // in the next starts' prepare, so that a drop's destroy (it clears that cache in memory, whole) is seen if it loses it (review B3)
        if (File.Exists(Path.Combine(Context.Info.Directory, "define-cached-node")))
            Try("skills", () => Line("skills", "cached node defined: " + string.Join(", ", GameTree.DefineJson(
                """{"mod":"boot-drop-observer","nodes":[{"guid":"b07d0a5e-5c1e-4c0d-9e11-7a2b3c4d5e6f","name":"dropobs_cached","class":"Warrior","like":"0xa598792e","from":["0x0b61f08f"],"offset":[150,120],"cost":1}]}""")
                .Select(n => $"{n.Name} {n.State} {n.Error}"))));
        Try("assets", () => Line("assets", Assets.Status().ReplaceLineEndings(" | ")));
        Try("assets", () => Each("assets", Assets.ListAllBlobs().Select(b => b.ToString())));
        Try("content", () => Each("content", NewContent.Definitions().Select(d => $"item {d.Name} {d.State} owner {d.Mod}")));
        Try("content", () => Each("content", Content.Buffs.Definitions().Select(b => $"buff {b.Name} {b.State} {b.Error}")));
        Try("content", () => Each("content", Content.Templates.Definitions().Select(t => $"template {t.Name} {t.State} {t.Error}")));
        Try("content", () => Each("content", Content.GameDataDefinitions().Select(g => g.ToString())));
        Try("skills", () => Each("skills", GameTree.Definitions.Select(n => $"node {n.Name} {n.State} index {n.Index} {n.Error}")));
        Try("skills", () => Line("skills", $"status {GameTree.Status}"));
        Try("worldtables", () => Line("worldtables", $"state {WorldTables.State}, hash {WorldTables.Hash}"));
        Try("worldtables", () => Each("worldtables", WorldTables.Tweaks.Select(t => $"tweak cell {t.CellId} {t.Mod}/{t.Block}")));
        Try("worldtables", () => Each("worldtables", WorldTables.Materials.Select(m => m.ToString())));
        Try("knowledge", () => Each("knowledge", Dialogues.Definitions.Select(d => $"dialogue {d.Id}")));
        Try("patches", () => Each("patches", Enumerable.Range(1, 3).Select(l => $"level {l} needs {Progression.Diagnostics.ExperienceForLevel(l).Item1}")));
        Line("done", $"after {sw.Elapsed.TotalSeconds:F0} s");
    }
}
