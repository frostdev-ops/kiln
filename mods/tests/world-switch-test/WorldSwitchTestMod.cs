// SPDX-License-Identifier: MIT
// A game client hosts one world after another in a process. Each starts (ServerStartedEvent, server-side mods' OnLoad) about 10 s before
// the game loads it. Seen before the fix: in every later hosted world the Warden's check read the last world's record, so he "was placed once in
// this world and is gone" while he stood there, and was never bound to his entity. A world he had never been in would have stayed without him.
//
// A lab has one world, so this plays the order on Kiln.Saves' lab hooks (the same close and load code a hosted game runs):
//   world A saved -> closed -> the next world started, not loaded -> another world B loads -> closed -> A loads again.
// PASS / FAIL lines in kiln.log. Boot it on a world you can spare: B's sidecar stays in kiln/data/worlds/.
using Kiln;

namespace WorldSwitchTest;

public sealed class Marker
{
    public string Text { get; set; } = "";
}

public sealed class WorldSwitchTestMod : Mod
{
    private const string Key = "marker", Early = "early";
    private static readonly Guid OtherWorld = Guid.Parse("c0ffee00-0000-4000-8000-00000000000b");
    private int _pass, _fail;
    private bool _ran;

    private void Check(bool ok, string what, string why = "")
    {
        if (ok)
        {
            _pass++;
            Log.Info($"world-switch-test: PASS {what}");
        }
        else
        {
            _fail++;
            Log.Error($"world-switch-test: FAIL {what}: {why}");
        }
    }

    public override void OnLoad() => Events.On<ServerStartedEvent>(e =>
    {
        if (_ran) return;  // this test posts the event itself
        _ran = true;
        Directory.CreateDirectory(Path.GetFullPath(Path.Combine(Context.DataDirectory, "..", "skills")));
        File.WriteAllText(Path.GetFullPath(Path.Combine(Context.DataDirectory, "..", "skills", "lab-ops")), "");   // the test-only requests of kiln.skills are off without it
        _ = Run();
    });

    // The other change a character can meet between two sessions: with only the sample in the tree, there are no shadow nodes at all,
    // and a character carrying the sample's nodes as shadow nodes (from a mod list with more nodes) still gets them back. Only with
    // the sample deployed and no other mod with skill nodes.
    private async Task SampleOnlyTree()
    {
        var ward = GameTree.Definitions.FirstOrDefault(d => d.Name == "skilltreesample_ember_ward");
        var heart = GameTree.Definitions.FirstOrDefault(d => d.Name == "skilltreesample_emberheart");
        if (ward is null || heart is null || GameTree.ShadowNodes.Count > 0)
        {
            Log.Info("world-switch-test: the sample-only tree check needs the sample and no shadow nodes: skipped");
            return;
        }
        var anchor = Spawn.FindByName("FlameAltar", max: 1).FirstOrDefault();
        if (anchor.IsNone || Spawn.Inspect(anchor) is not { } info)
        {
            Check(false, "the sample-only tree check", "no flame altar to stand a player entity next to");
            return;
        }
        var a = info.Position;
        var p = await Spawn.Entity("1_Player", new WorldPosition(a.X + 40, a.Y + 0.5, a.Z + 40), 0, new SpawnOptions { Tag = "world-switch-test", Flags = SpawnFlags.Transient });
        await Steps(6);
        var holder = Skills.HolderOf(new CharacterKey(0x1122334455667788, Guid.Parse("c0ffee00-0000-4000-8000-000000000002")));
        Progression.Diagnostics.LabPlayer(p);
        GameTree.GameRequest(p, 3);
        GameTree.LabSetCarried(holder, new Dictionary<string, int> { [$"0x{ward.Id:x8}"] = 1, [$"0x{heart.Id:x8}"] = 2 });
        GameTree.LabPlayer(p, holder, forceNative: true);
        for (var i = 0; i < 200 && GameTree.LabCarried(holder).Count > 0; i++) await Steps(1);
        await Steps(5);
        var nodes = Progression.UnlockedNodes(p);
        Check(nodes.Any(n => n.Id == ward.Id) && nodes.Any(n => n.Id == heart.Id && n.Level == 2),
            $"a tree with no shadow nodes: a character's carried Ember Ward and Emberheart 2/3 are in its list, with their levels: {string.Join(", ", nodes.Where(n => n.Id == ward.Id || n.Id == heart.Id).Select(n => $"0x{n.Id:x8} {n.Level}"))}");
        Check(GameTree.LabCarried(holder).Count == 0, "and the carried entries are gone");
        GameTree.LabPlayer(default);
        Progression.Diagnostics.LabPlayer(default);
        Spawn.Destroy(p);
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    private static void PostServerStarted()
    {
        var ctor = typeof(ServerStartedEvent).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)[0];
        Events.Post((ServerStartedEvent)ctor.Invoke([Lifecycle.StartedAtStep, "lab: the next hosted world"]));
    }

    private async Task Run()
    {
        try
        {
            for (var i = 0; i < 4000 && !(Spawn.WorldRunning && Knowledge.Status?.Applying == true); i++) await Context.Scheduler.NextTick();
            await Steps(60);
            var worldA = Saves.WorldId;
            var giver = NativeQuests.Npc("world-switch-test:switch_giver");
            Check(giver is not null && worldA is not null, $"a world ({worldA}) and an NPC placed once");
            for (var i = 0; i < 400 && Spawn.Find(giver!.Guid).Count == 0; i++) await Context.Scheduler.NextTick();
            await Steps(240);  // verified after 3 s: the world's record says he was placed
            var before = Spawn.Find(giver!.Guid);
            Check(before.Count == 1, $"world A has him: {before.Count}");
            var stamp = Guid.NewGuid().ToString("N");
            WorldData.Set(Key, new Marker { Text = stamp });
            var saved = await Saves.Request();
            Check(saved.Success, $"world A saved with this mod's marker: {saved}");
            var revision = Saves.Status?.Revision ?? 0;
            var unix = 0u;
            await Steps(5);

            // ---- closed: the next world started, and the game has not loaded it ----
            Saves.LabCloseWorld();
            Check(!Saves.WorldDataReady, "the world closed: its data isn't ready for the next one");
            Check(Saves.DataDirectory is null, "no world's sidecar is open");
            Check(!WorldData.TryGet<Marker>(Key, out _), "the next world, before its load: the closed world's data is gone");
            WorldData.Set(Early, new Marker { Text = "written before the load" });
            var placed0 = NativeQuests.Counters.Placed;
            Log.Info("world-switch-test: --- the next world starts: \"placed once ... is gone\" must not follow in kiln.log ---");
            PostServerStarted();
            await Steps(30);
            Check(NativeQuests.Counters.Placed == placed0, "and no NPC is placed or judged from that");

            // ---- another world loads: nothing of A, nothing written before the load ----
            Saves.LabWorldLoaded(OtherWorld.ToString(), 1);
            Check(Saves.WorldDataReady, "world B loaded: its data is ready");
            Check(Saves.DataDirectory?.Contains(OtherWorld.ToString()) == true, $"world B has its own sidecar: {Saves.DataDirectory}");
            Check(!WorldData.TryGet<Marker>(Key, out _), "world B doesn't see world A's data");
            Check(!WorldData.TryGet<Marker>(Early, out _), "nor what was written before its load");
            WorldData.Set(Key, new Marker { Text = "world B" });
            await Steps(10);

            // ---- back to A, in the same process ----
            Saves.LabCloseWorld();
            Check(!WorldData.TryGet<Marker>(Key, out _), "world B closed: its data is gone too");
            Saves.LabWorldLoaded(worldA, revision, unix);
            Check(WorldData.TryGet<Marker>(Key, out var back) && back.Text == stamp, $"world A again: its marker came back from its sidecar ({back?.Text})");
            await Steps(30);
            Check(Spawn.Find(giver.Guid).Count == 1, "and its NPC is still one (the load placed nothing)");
            var again = await Saves.Request();
            Check(again.Success, $"world A saves into its own sidecar afterwards: {again}");
            var dir = Saves.DataDirectory ?? "";
            Check(dir.Contains(worldA!), $"its sidecar: {dir}");
            await SampleOnlyTree();
        }
        catch (Exception e)
        {
            Check(false, "exception", e.ToString());
        }
        Log.Info($"world-switch-test done: {_pass} PASS, {_fail} FAIL");
    }
}
