// SPDX-License-Identifier: MIT
// Skinned models cooked by tools/modelcook: commands for the manual checks in the client (docs/kiln/modding/models.md), and on a dedicated server a test that needs no player: the server's
// animation engine runs the figure's skeleton, clip and graph, and its joint transforms, applied to the cooked model's vertices with the skeleton's inverse binds, must land where the cook
// says the client will draw them. Prints "skincheck: PASS ..." / "FAIL ...". Nothing here is drawn on a server: what a client shows is a manual check.
using System.Text.Json;
using Kiln;

namespace SkinCheck;

public sealed class SkinCheckMod : Mod
{
    private const string Figure = "Kiln_SkinCheck_Figure", Puppet = "Kiln_SkinCheck_Puppet", Vukah = "Enemy_Vukah_Melee";
    private static readonly WorldPosition LabPos = new(3865.8, 833.0, 1433.8);
    private readonly List<EntityId> _mine = [];
    private int _pass, _fail;

    public override void OnLoad()
    {
        if (Mods.Side == ModSide.Server) _ = LabTest();
    }

    public override void OnUnload()
    {
        foreach (var e in _mine) try { Spawn.Destroy(e); } catch { }
        _mine.Clear();
    }

    private async Task<EntityId?> NextToPlayer(CommandContext c, string template, double distance)
    {
        var player = c.Source.Player;
        if (player.IsNone)
            foreach (var p in Players.All) { player = p.Entity; break; }
        if (player.IsNone || World.Position(player) is not { } at)
        {
            c.Reply("no player in game");
            return null;
        }
        var e = await Spawn.Entity(template, new WorldPosition(at.X + distance, at.Y, at.Z), 270, new SpawnOptions { Tag = "skincheck", Flags = SpawnFlags.Transient });
        _mine.Add(e);
        var text = $"skincheck: {template} at ({at.X + distance:F0}, {at.Y:F0}, {at.Z:F0}), entity {e}";
        c.Reply(text);
        Log.Info(text);
        return e;
    }

    [Command("skincheck vukah", Description = "a Vukah with the cooked stick figure, 12 m from you, hostile: watch it walk, attack and die", Permission = CommandPermission.Admin)]
    private async Task SpawnVukah(CommandContext c) => await NextToPlayer(c, Vukah, 12);

    [Command("skincheck vukah still", Description = "the same, friendly and standing still, to look at its idle", Permission = CommandPermission.Admin)]
    private async Task SpawnStill(CommandContext c)
    {
        if (await NextToPlayer(c, Vukah, 5) is not { } e) return;
        var cr = Creatures.Of(e);
        cr.Faction = Faction.PlayerAlly;
        cr.Features.Disable(EnemyFeatures.Move | EnemyFeatures.Roaming | EnemyFeatures.Combat | EnemyFeatures.Awareness | EnemyFeatures.Investigate);
    }

    [Command("skincheck figure", Description = "the creature of our own skeleton, clips, graph and model, 12 m from you, hostile", Permission = CommandPermission.Admin)]
    private async Task SpawnFigure(CommandContext c) => await NextToPlayer(c, Figure, 12);

    [Command("skincheck figure still", Description = "the same, friendly and standing still", Permission = CommandPermission.Admin)]
    private async Task SpawnFigureStill(CommandContext c)
    {
        if (await NextToPlayer(c, Figure, 5) is not { } e) return;
        var cr = Creatures.Of(e);
        cr.Faction = Faction.PlayerAlly;
        cr.Features.Disable(EnemyFeatures.Move | EnemyFeatures.Roaming | EnemyFeatures.Combat | EnemyFeatures.Awareness | EnemyFeatures.Investigate);
    }

    [Command("skincheck kill", Description = "kills what this mod spawned, to see the death", Permission = CommandPermission.Admin)]
    private async Task KillAll(CommandContext c)
    {
        var n = 0;
        foreach (var e in _mine.ToArray())
            try { await Combat.Kill(e); n++; } catch (Exception x) { Log.Info("skincheck: kill: " + x.Message); }
        c.Reply($"skincheck: killed {n}");
    }

    [Command("skincheck clear", Description = "removes what this mod spawned", Permission = CommandPermission.Admin)]
    private void Clear(CommandContext c)
    {
        var n = _mine.Count;
        OnUnload();
        c.Reply($"skincheck: removed {n}");
    }

    // ---- lab: no player, no client ----

    private bool Check(bool ok, string what)
    {
        if (ok) _pass++; else _fail++;
        Log.Info($"skincheck: {(ok ? "PASS" : "FAIL")} {what}");
        return ok;
    }

    private async Task Steps(int n) { for (var i = 0; i < n; i++) await Context.Scheduler.NextTick(); }

    private static double[,] Matrix(JsonElement rows)
    {
        var m = new double[3, 4];
        var r = 0;
        foreach (var row in rows.EnumerateArray())
        {
            var c = 0;
            foreach (var v in row.EnumerateArray()) m[r, c++] = v.GetDouble();
            r++;
        }
        return m;
    }

    private static double[] Apply(double[,] m, double[] p) =>
        [m[0, 0] * p[0] + m[0, 1] * p[1] + m[0, 2] * p[2] + m[0, 3], m[1, 0] * p[0] + m[1, 1] * p[1] + m[1, 2] * p[2] + m[1, 3], m[2, 0] * p[0] + m[2, 1] * p[1] + m[2, 2] * p[2] + m[2, 3]];

    private static double[] Vec(JsonElement e) => e.EnumerateArray().Select(x => x.GetDouble()).ToArray();

    private static double Dist(double[] a, double[] b) => Math.Sqrt(a.Zip(b, (x, y) => (x - y) * (x - y)).Sum());

    private double[,]? Slot(EntityId e, int index)
    {
        var b = World.ReadBytes(e, "ServerAnimationGraphSlotComponent");
        if (b == null) return null;
        var o = 0x20 + index * 48;
        var m = new double[3, 4];
        for (var c = 0; c < 4; c++) for (var r = 0; r < 3; r++) m[r, c] = BitConverter.ToSingle(b, o + (c * 3 + r) * 4);
        return m;
    }

    private async Task LabTest()
    {
        try
        {
            var t0 = DateTime.UtcNow;
            while (!Spawn.WorldRunning && (DateTime.UtcNow - t0).TotalSeconds < 240) await Context.Scheduler.NextTick();
            await Steps(60);
            nuint world = 0;
            var h = Hooks.Detour("GetComponent", c => { if (world == 0) world = c[1]; return c.CallOriginal(); });
            for (var i = 0; i < 200 && world == 0; i++) await Steps(1);
            h.Remove();
            using var d = JsonDocument.Parse(File.ReadAllText(Path.Combine(Context.Info.Directory, "assets/cooked/expect.json")));
            var root = d.RootElement;
            var hold = Convert.ToUInt32(root.GetProperty("holdState").GetString()![2..], 16);

            var full = await Spawn.Entity(Figure, LabPos, 0, new SpawnOptions { Tag = "skincheck", Flags = SpawnFlags.Transient });
            _mine.Add(full);
            await Steps(30);
            var g = World.ReadBytes(full, "ServerAnimationGraphComponent2");
            Check(g != null && BitConverter.ToInt32(g, 0) != 0, $"the creature of our own files spawns with a running graph: handle {(g == null ? 0 : BitConverter.ToInt32(g, 0))}");

            var e = await Spawn.Entity(Puppet, LabPos, 0, new SpawnOptions { Tag = "skincheck", Flags = SpawnFlags.Transient });
            _mine.Add(e);
            await Steps(30);
            var input = Raw.Dyn(world, "ActorInput", e);
            for (var i = 0; i < 40; i++)
            {
                Raw.SetU32(input, hold);
                await Steps(1);
            }
            foreach (var s in root.GetProperty("slots").EnumerateObject())
            {
                var want = Matrix(s.Value.GetProperty("pose"));
                var inv = Matrix(s.Value.GetProperty("skinToJoint"));
                var m = Slot(e, s.Value.GetProperty("index").GetInt32());
                if (m == null) { Check(false, $"no slots on the puppet"); break; }
                double worst = 0;
                for (var r = 0; r < 3; r++) for (var c = 0; c < 4; c++) worst = Math.Max(worst, Math.Abs(m[r, c] - want[r, c]));
                Check(worst < 0.002, $"{s.Name} ({s.Value.GetProperty("joint").GetString()}): the engine's joint against the cooked clip's: largest element off by {worst:F5}");
                var far = 0.0;
                var n = 0;
                foreach (var v in s.Value.GetProperty("samples").EnumerateArray())
                {
                    var q = Apply(m, Apply(inv, Vec(v.GetProperty("bind"))));
                    far = Math.Max(far, Dist(q, Vec(v.GetProperty("posed"))));
                    n++;
                }
                Check(n > 0 && far < 0.003, $"{s.Name}: {n} model vertices skinned by the engine's joint land {far * 1000:F2} mm from what the cook computed");
            }

            // The state the cloned AI's attack sequence asks for, by its id on the creature it was cloned from, and the engine's Dead state.
            var attack = root.GetProperty("attack");
            var attackState = Convert.ToUInt32(attack.GetProperty("state").GetString()![2..], 16);
            var high = double.MinValue;
            for (var i = 0; i < 60; i++)
            {
                Raw.SetU32(input, attackState);
                await Steps(1);
                if (Slot(e, 8) is { } m) high = Math.Max(high, m[1, 3]);
            }
            var wantHigh = attack.GetProperty("handHighest").GetDouble();
            Check(Math.Abs(high - wantHigh) < 0.05, $"the attack state raises the hand to {high:F3} m of the clip's {wantHigh:F3} m");

            var dead = root.GetProperty("dead");
            var deadState = Convert.ToUInt32(dead.GetProperty("state").GetString()![2..], 16);
            for (var i = 0; i < 60; i++)
            {
                Raw.SetU32(input, deadState);
                await Steps(1);
            }
            var wantLow = dead.GetProperty("pelvisEnd").GetDouble();
            var low = Slot(e, 3)?[1, 3] ?? double.NaN;
            Check(Math.Abs(low - wantLow) < 0.02, $"the Dead state lays the pelvis at {low:F3} m of the clip's {wantLow:F3} m and holds it");

            // Not a pass or fail: what the body does when it really dies with no player around.
            await Combat.Kill(full);
            await Steps(40);
            var si = World.ReadBytes(full, "ServerAnimationGraphInput");
            Log.Info($"skincheck: INFO after Combat.Kill the creature's state id is 0x{(si == null ? 0 : BitConverter.ToUInt32(si, 0x22c)):x8} (Dead is 0x{deadState:x8})");
            var control = await Spawn.Entity(Vukah, LabPos, 0, new SpawnOptions { Tag = "skincheck", Flags = SpawnFlags.Transient });
            _mine.Add(control);
            await Steps(30);
            await Combat.Kill(control);
            await Steps(40);
            si = World.ReadBytes(control, "ServerAnimationGraphInput");
            Log.Info($"skincheck: INFO the same on the game's own Vukah: 0x{(si == null ? 0 : BitConverter.ToUInt32(si, 0x22c)):x8}");
        }
        catch (Exception x) { Check(false, "threw: " + x); }
        finally
        {
            Log.Info($"skincheck: summary: {_pass} PASS, {_fail} FAIL");
            OnUnload();
        }
    }
}

internal static unsafe class Raw
{
    public static nuint Dyn(nuint world, string comp, EntityId e)
    {
        ulong* r = stackalloc ulong[2];
        r[0] = r[1] = 0;
        ((delegate* unmanaged<ulong*, nuint, uint, uint, ulong*>)Symbols.Get("GetComponent"))(r, world, World.ComponentHash(comp), e.Value);
        return (nuint)r[0];
    }

    public static void SetU32(nuint a, uint v) => *(uint*)a = v;
}
