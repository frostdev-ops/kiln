// SPDX-License-Identifier: MIT
// kiln.spawn lab test. Prints "spawn-test: PASS ..." / "FAIL ..." / "INFO ..." lines to kiln.log.
// It runs over three server boots, keeping its phase in its data folder (kiln/data/spawn-test/state.json):
//   boot 1: templates, anchor point, spawn creature / item drop / prop / transient prop, inspect, AI and gravity
//           observation, destroy, error cases, sidecar check. Leaves 4 tracked entities in the world.
//   boot 2: which of them came back from the save (and were re-found), cleanup, sidecar empty.
//   boot 3: after the save that followed the cleanup: nothing of ours is left in the world.
// Stop the server with SIGINT between boots (tools/lab/lab.sh stop) so it writes the save.
// Delete kiln/data/spawn-test/state.json to start over.
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kiln;

namespace SpawnTest;

public sealed class SpawnTestMod : Mod
{
    private const uint WoodLogs = 722268629;  // Material_T1_Wood_Logs
    private int _pass, _fail;
    private string StatePath => Path.Combine(Context.DataDirectory, "state.json");

    public override void OnLoad() => _ = RunGuarded();

    private async Task RunGuarded()
    {
        try
        {
            // The server steps its world while it still loads the save: wait until it runs.
            var sw = Stopwatch.StartNew();
            while (!Spawn.WorldRunning && sw.Elapsed.TotalSeconds < 180) await Context.Scheduler.NextTick();
            Info($"world running after {sw.Elapsed.TotalSeconds:F1} s: {Spawn.WorldRunning}");
            await Wait(3);
            var phase = LoadState()?["phase"]?.GetValue<int>() ?? 1;
            Info($"boot with phase {phase}, world {Spawn.WorldKey}, service {(Spawn.Available ? "available" : "UNAVAILABLE")}");
            switch (phase)
            {
                case 1: await Phase1(); break;
                case 2: await Phase2(); break;
                case 3: await Phase3(); break;
                default: Info("all phases done; delete state.json to run again"); break;
            }
        }
        catch (Exception e)
        {
            Fail($"test threw: {e}");
        }
        Info($"summary: {_pass} PASS, {_fail} FAIL");
    }

    // ------------------------------------------------------------------ boot 1
    private async Task Phase1()
    {
        if (!Check(Spawn.Available, "service kiln.spawn is available")) return;

        // Templates
        var all = Spawn.Templates;
        var loaded = all.Count(t => t.LoadedByGame);
        Check(loaded > 1000, $"engine template list read: {all.Count} templates, {loaded} loaded by the game, " +
                             $"{all.Count(t => t.Shipped)} from the shipped catalog");
        foreach (var kind in Enum.GetValues<TemplateKind>()) Info($"  kind {kind}: {all.Count(t => t.Kind == kind)}");
        foreach (var word in new[] { "altar", "spawnpoint", "barrel", "townsfolk", "chest", "lootpickup" })
            Info($"  names with '{word}': " + string.Join(", ", all.Where(t => t.Name.Contains(word, StringComparison.OrdinalIgnoreCase)).Take(8).Select(t => t.Name)));
        foreach (var n in new[] { "Enemy_Vukah_Melee", "Base_LootPickup_Generic_Gravity", "Prop_Deco_Steppeprops_Barrel_01", "Home_Base_FlameAltar" })
            if (Spawn.Lookup(n) is { } ti) Info($"  template {n}: NeverSaved={ti.NeverSaved?.ToString() ?? "?"}, Expires={ti.Expires}");
        Info($"  templates with DoNotSave: {all.Count(t => t.NeverSaved == true)} (creatures {all.Count(t => t.NeverSaved == true && t.Kind == TemplateKind.Creature)} of {all.Count(t => t.Kind == TemplateKind.Creature)}, " +
             $"pickups {all.Count(t => t.NeverSaved == true && t.Kind == TemplateKind.Pickup)} of {all.Count(t => t.Kind == TemplateKind.Pickup)}); with LifeTime: {all.Count(t => t.Expires)}");
        var vukah = Spawn.Lookup("Enemy_Vukah_Melee");
        Check(vukah is { LoadedByGame: true, Kind: TemplateKind.Creature }, $"Lookup(Enemy_Vukah_Melee) = {vukah?.Id} {vukah?.DisplayName}");
        var byDisplay = Spawn.Lookup("Vukah");
        Check(byDisplay?.Id == vukah?.Id, $"Lookup by display name 'Vukah' = {byDisplay?.Name}");
        var pickup = Spawn.Lookup("Base_LootPickup_Generic_Gravity");
        Check(pickup is { LoadedByGame: true }, $"generic pickup template {pickup?.Id}");
        var barrel = Spawn.Lookup("Prop_Deco_Steppeprops_Barrel_01") ??
                     all.FirstOrDefault(t => t.LoadedByGame && t.Kind == TemplateKind.Prop && t.Name.Contains("Barrel", StringComparison.OrdinalIgnoreCase));
        Check(barrel != null, $"a barrel prop template: {barrel?.Name}");

        // Anchor: a flame altar (the base) if there is one, else a villager, else the first prop.
        var (anchor, anchorWhat) = FindAnchor();
        if (!Check(anchor is not null, $"anchor point: {anchorWhat} at {anchor}")) return;
        var a = anchor!.Value;
        WorldPosition At(double dx, double dz, double dy = 0.5) => new(a.X + dx, a.Y + dy, a.Z + dz);

        // Error cases
        await ExpectFault<SpawnException>(() => Spawn.Entity("No_Such_Template_Kiln", At(3, 0)), "unknown template name is refused");
        await ExpectFault<SpawnException>(() => Spawn.Entity("Enemy_Vukah_Melee", new WorldPosition(double.NaN, 0, 0)), "NaN position is refused");
        Expect<ArgumentException>(() => Spawn.Destroy(new EntityId(1)), "destroying a player id is refused");
        Check(!Spawn.Destroy(new EntityId(0x7ffffff0)), "destroying a missing entity returns false");
        var offThread = await Task.Run(() =>
        {
            try { _ = Spawn.Entity("Enemy_Vukah_Melee", At(3, 0)); return "no exception"; }
            catch (InvalidOperationException) { return "InvalidOperationException"; }
            catch (Exception e) { return e.GetType().Name; }
        });
        await Context.Scheduler.NextTick();  // back on the tick thread
        Check(offThread == "InvalidOperationException", $"spawn off the tick thread is refused ({offThread})");

        // Spawn the four entities that stay
        var creature = await Spawn.Entity(vukah!.Id, At(6, 0), 90, new SpawnOptions { Tag = "spawn-test" });
        var drop = await Spawn.Item(WoodLogs, 5, At(-4, 2, 2.0), options: new SpawnOptions { Tag = "spawn-test" });
        var prop = await Spawn.Entity(barrel!.Name, At(0, 5, 0), 0, new SpawnOptions { Tag = "spawn-test" });
        var transient = await Spawn.Entity(barrel.Name, At(6, 6, 0), 0, new SpawnOptions { Tag = "spawn-test", Flags = SpawnFlags.Transient });
        Info($"spawned creature {creature}, item drop {drop}, prop {prop}, transient prop {transient}");

        CheckEntity(creature, vukah.Id, At(6, 0), "creature");
        CheckEntity(drop, pickup!.Id, At(-4, 2, 2.0), "item drop");
        CheckEntity(prop, barrel.Id, At(0, 5, 0), "prop");
        CheckEntity(transient, barrel.Id, At(6, 6, 0), "transient prop");
        var yaw = Spawn.Inspect(creature)?.YawDegrees ?? float.NaN;
        Check(Math.Abs(yaw - 90) < 0.5, $"creature yaw reads back as {yaw:F1} deg");

        // A second creature (by display name, with a level override): spawn, confirm, destroy, confirm gone
        var temp = await Spawn.Entity("Vukah", At(8, -3), options: new SpawnOptions { Tag = "spawn-test-temp", Level = 12 });
        Check(Spawn.Exists(temp), $"second creature {temp} exists");
        await Context.Scheduler.NextTick();
        Check(Spawn.Destroy(temp), $"destroy({temp}) returned true");
        Check(!Spawn.Exists(temp), $"entity {temp} is gone right after destroy");
        Check(Spawn.Tracked.All(t => t.Serial == 0 || t.Entity != temp), "its tracked record was dropped");
        await Context.Scheduler.NextTick();
        Check(!Spawn.Exists(temp), $"entity {temp} is still gone one step later");

        // Child entities: does a creature spawn bring more entities, and do they go when it is destroyed?
        var before = Spawn.EntityCount;
        var probe = await Spawn.Entity(vukah.Id, At(12, 12), options: new SpawnOptions { Tag = "spawn-test-temp" });
        for (var i = 0; i < 5; i++) await Context.Scheduler.NextTick();
        var withChildren = Spawn.EntityCount;
        var children = Enumerable.Range(1, 12).Select(k => new EntityId(probe.Value + (uint)k))
            .Select(Spawn.Inspect).Where(x => x is not null && x.Parent == probe).ToList();
        Info($"creature {probe}: entity count {before} -> {withChildren} after 5 steps; children by parent link: " +
             string.Join(", ", children.Select(c => $"{c!.Entity} {c.TemplateName}")));
        Spawn.Destroy(probe);
        for (var i = 0; i < 25; i++) await Context.Scheduler.NextTick();
        var after = Spawn.EntityCount;
        Info($"after destroying {probe} and 25 steps: entity count {after}; children left: " +
             string.Join(", ", children.Where(c => Spawn.Exists(c!.Entity)).Select(c => c!.Entity.ToString())));
        Check(after <= before, $"destroying a creature also removes the entities it brought ({before} -> {withChildren} -> {after})");

        // The admin contract: a group around a point, removed again by tag
        var group = await Spawn.Group(vukah.Id.ToString(), At(-10, -10), 3, 3, new SpawnOptions { Tag = "spawn-test-group" });
        Check(group.Count == 3 && group.All(Spawn.Exists), $"Group spawned 3 around a point: {string.Join(", ", group)}");
        var gr = Spawn.CleanupTracked(tag: "spawn-test-group");
        Check(gr.Destroyed == 3 && !group.Any(Spawn.Exists), $"cleanup by tag removed the group ({gr})");
        Check(Spawn.Tracked.Count(t => t.Tag == "spawn-test") == 4, "cleanup by tag kept the other records");

        // Observe for 12 s: does the creature move (AI), does the item drop fall, are they culled?
        var c0 = Spawn.Inspect(creature)?.Position;
        var d0 = Spawn.Inspect(drop)?.Position;
        await Wait(12);
        var c1 = Spawn.Inspect(creature)?.Position;
        var d1 = Spawn.Inspect(drop)?.Position;
        Info($"creature after 12 s: {(c1 is null ? "GONE (despawned/culled)" : $"{c1}, moved {c0!.Value.DistanceTo(c1.Value):F2} m")}");
        Info($"item drop after 12 s: {(d1 is null ? "GONE" : $"{d1}, y {d0!.Value.Y:F2} -> {d1.Value.Y:F2}")}");
        Check(Spawn.Exists(prop), "prop still exists after 12 s");

        // Sidecar
        Check(Spawn.Refresh() >= 3, $"refresh: {Spawn.Tracked.Count(t => t.IsLive)} live tracked records");
        var tracked = Spawn.Tracked.Where(t => t.Tag == "spawn-test").ToList();
        Check(tracked.Count == 4, $"4 tracked records with tag spawn-test ({tracked.Count})");
        var side = ReadSidecar();
        var serials = side?["entities"]?.AsArray().Select(e => e!["serial"]!.GetValue<ulong>()).ToHashSet() ?? [];
        Check(side != null && tracked.All(t => serials.Contains(t.Serial)), $"sidecar {SidecarPath} lists them ({serials.Count} entries)");
        Check(tracked.All(t => t.Origin == Context.Info.Id), "records carry origin = this mod's id");

        var state = new JsonObject
        {
            ["phase"] = 2,
            ["entities"] = new JsonArray(tracked.Select(t => (JsonNode)new JsonObject
            {
                ["serial"] = t.Serial, ["name"] = t.TemplateName, ["template"] = t.Template.ToString(),
                ["transient"] = t.Flags.HasFlag(SpawnFlags.Transient), ["x"] = t.Position.X, ["y"] = t.Position.Y, ["z"] = t.Position.Z,
                ["entity"] = t.Entity.Value,
            }).ToArray()),
        };
        SaveState(state);
        Info("phase 1 done: stop the server with SIGINT (it saves), then start it again for phase 2");
    }

    // ------------------------------------------------------------------ boot 2
    private async Task Phase2()
    {
        if (!Check(Spawn.Available, "service kiln.spawn is available")) return;
        await Wait(15);  // the service re-finds missing records every 10 s early in a session
        Spawn.Refresh();
        var state = LoadState()!;
        var expected = state["entities"]!.AsArray();
        var tracked = Spawn.Tracked.ToDictionary(t => t.Serial);
        foreach (var e in expected)
        {
            var serial = e!["serial"]!.GetValue<ulong>();
            var name = e["name"]!.GetValue<string>();
            var transient = e["transient"]!.GetValue<bool>();
            var oldId = e["entity"]!.GetValue<uint>();
            var pos = new WorldPosition(e["x"]!.GetValue<double>(), e["y"]!.GetValue<double>(), e["z"]!.GetValue<double>());
            tracked.TryGetValue(serial, out var rec);
            var near = Spawn.Find(Guid.Parse(e["template"]!.GetValue<string>()), pos, 2.0);
            var what = $"#{serial} {name}{(transient ? " (transient)" : "")}";
            foreach (var id in near)
                Info($"{what}: entity {id} near the spot has SpawnTime {Spawn.Inspect(id)?.SpawnTime?.ToString() ?? "absent"}");
            Info($"{what}: record {(rec is null ? "absent" : rec.IsLive ? $"live as {rec.Entity} (was {oldId})" : "missing")}, " +
                 $"{near.Count} entities of its template within 2 m of {pos}");
            if (transient)
                Check(rec is not { IsLive: true } && near.Count == 0, $"{what} did not come back from the save");
            else
                Info($"PERSIST {name}: {(rec is { IsLive: true } ? "saved and re-found" : near.Count > 0 ? "saved but NOT re-found" : "not saved (gone after restart)")}");
        }

        var before = Spawn.Tracked.Count(t => t.IsLive);
        var r = Spawn.CleanupTracked(tag: "spawn-test");
        Check(r.Destroyed == before, $"cleanup destroyed {r.Destroyed} (live before: {before}), dropped {r.Missing} missing, kept {r.Kept}");
        Check(Spawn.Tracked.All(t => t.Tag != "spawn-test"), "no spawn-test records left");
        var side = ReadSidecar();
        Check(side?["entities"]?.AsArray().Count(x => x!["tag"]?.GetValue<string>() == "spawn-test") == 0, "sidecar has no spawn-test entries");
        await Context.Scheduler.NextTick();
        foreach (var e in expected)
        {
            var pos = new WorldPosition(e!["x"]!.GetValue<double>(), e["y"]!.GetValue<double>(), e["z"]!.GetValue<double>());
            var near = Spawn.Find(Guid.Parse(e["template"]!.GetValue<string>()), pos, 2.0);
            Check(near.Count == 0, $"no {e["name"]} left within 2 m of {pos} after cleanup ({near.Count})");
        }
        state["phase"] = 3;
        SaveState(state);
        Info("phase 2 done: stop the server with SIGINT (it saves), then start it again for phase 3");
    }

    // ------------------------------------------------------------------ boot 3
    private async Task Phase3()
    {
        await Wait(15);
        var state = LoadState()!;
        Check(Spawn.Tracked.All(t => t.Tag != "spawn-test"), "still no spawn-test records after restart");
        foreach (var e in state["entities"]!.AsArray())
        {
            var pos = new WorldPosition(e!["x"]!.GetValue<double>(), e["y"]!.GetValue<double>(), e["z"]!.GetValue<double>());
            var near = Spawn.Find(Guid.Parse(e["template"]!.GetValue<string>()), pos, 2.0);
            Check(near.Count == 0, $"after cleanup + save + restart: no {e["name"]} within 2 m of {pos} ({near.Count})");
        }
        state["phase"] = 4;
        SaveState(state);
        Info("phase 3 done: all phases complete");
    }

    // ------------------------------------------------------------------ helpers
    private (WorldPosition?, string) FindAnchor()
    {
        foreach (var filter in new[] { "FlameAltar", "Altar", "Townsfolk" })
        {
            var hits = Spawn.FindByName(filter, max: 4);
            foreach (var id in hits)
                if (Spawn.Inspect(id) is { } info)
                    return (info.Position, $"{info.TemplateName} (entity {id})");
        }
        return (null, "none");
    }

    private void CheckEntity(EntityId id, Guid template, WorldPosition at, string what)
    {
        var info = Spawn.Inspect(id);
        Check(info is not null, $"{what} {id} exists after spawn");
        if (info is null) return;
        Check(info.Template == template, $"{what} {id} has template {info.TemplateName}");
        Check(info.Position.DistanceTo(at) < 0.05, $"{what} {id} is at {info.Position} (asked {at})");
        Check(info.IsTracked && info.Serial > 0, $"{what} {id} is tracked (serial {info.Serial})");
        var rec = Spawn.Tracked.FirstOrDefault(t => t.Serial == info.Serial);
        Info($"{what} {id}: SpawnTime component {(info.SpawnTime is { } st ? st.ToString() : "absent")}");
    }

    private string SidecarPath => Path.Combine(Path.GetDirectoryName(Context.DataDirectory.TrimEnd('/', '\\'))!, "spawn", $"{Spawn.WorldKey}.json");

    private JsonNode? ReadSidecar()
    {
        try { return JsonNode.Parse(File.ReadAllText(SidecarPath)); }
        catch (Exception e) { Info($"sidecar unreadable: {e.Message}"); return null; }
    }

    private JsonObject? LoadState()
    {
        try { return File.Exists(StatePath) ? JsonNode.Parse(File.ReadAllText(StatePath))?.AsObject() : null; }
        catch { return null; }
    }

    private void SaveState(JsonObject s)
    {
        Directory.CreateDirectory(Context.DataDirectory);
        File.WriteAllText(StatePath, s.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task Wait(double seconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds) await Context.Scheduler.NextTick();
    }

    private async Task ExpectFault<T>(Func<Task> f, string what) where T : Exception
    {
        try
        {
            await f();
            Fail($"{what} (no exception)");
        }
        catch (T e) { Pass($"{what} ({e.Message})"); }
        catch (Exception e) { Fail($"{what} ({e.GetType().Name}: {e.Message})"); }
    }

    private void Expect<T>(Action f, string what) where T : Exception
    {
        try
        {
            f();
            Fail($"{what} (no exception)");
        }
        catch (T) { Pass(what); }
        catch (Exception e) { Fail($"{what} ({e.GetType().Name}: {e.Message})"); }
    }

    private bool Check(bool ok, string what)
    {
        if (ok) Pass(what); else Fail(what);
        return ok;
    }

    private void Pass(string s) { _pass++; Log.Info($"spawn-test: PASS {s}"); }
    private void Fail(string s) { _fail++; Log.Error($"spawn-test: FAIL {s}"); }
    private void Info(string s) => Log.Info($"spawn-test: INFO {s}");
}
