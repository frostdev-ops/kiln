// SPDX-License-Identifier: MIT
// Retargeted animations on a dedicated server, no player: the packling's clips were authored on a rig of other joint names and 1.3 times its size
// (models/pack.gltf) and cooked onto its body's skeleton. Its slam attack plays the retargeted clip in the server's animation engine; the engine's right hand must rise as high as the motion
// authored on the body's own rig says, not as high as the bigger rig's. Prints "retarget-test: PASS ..." / "FAIL ..." and a summary.
using System.Text.Json;
using Kiln;

namespace RetargetTest;

public sealed class RetargetTestMod : Mod
{
    private const string Me = "retarget-test";
    private static readonly WorldPosition LabPos = new(3860.8, 833.0, 1420.8);
    private const int HandR = 8;   // keen::AttachmentSlot
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

    private void Check(bool ok, string what)
    {
        if (ok) _pass++; else _fail++;
        Log.Info($"{Me}: {(ok ? "PASS" : "FAIL")} {what}");
    }

    private async Task Steps(int n) { for (var i = 0; i < n; i++) await Context.Scheduler.NextTick(); }

    private static uint StateOf(EntityId e) => World.ReadBytes(e, "ServerAnimationGraphInput") is { Length: > 0x230 } b ? BitConverter.ToUInt32(b, 0x22c) : 0;

    private static double? HandHeight(EntityId e) =>
        World.ReadBytes(e, "ServerAnimationGraphSlotComponent") is { } b && b.Length >= 0x20 + (HandR + 1) * 48 ? BitConverter.ToSingle(b, 0x20 + HandR * 48 + (3 * 3 + 1) * 4) : null;

    private static uint Hex(JsonElement e) => Convert.ToUInt32(e.GetString()![2..], 16);

    private async Task LabTest()
    {
        try
        {
            var t0 = DateTime.UtcNow;
            while (!Spawn.WorldRunning && (DateTime.UtcNow - t0).TotalSeconds < 240) await Context.Scheduler.NextTick();
            await Steps(60);
            using var d = JsonDocument.Parse(File.ReadAllText(Path.Combine(Context.Info.Directory, "assets/cooked/expect.json")));
            var x = d.RootElement;
            Log.Info($"{Me}: INFO offline, the retargeted clip's hand is {x.GetProperty("offlineWorst").GetDouble() * 1000:F2} mm at most from the body's own clip");

            var e = await Blocks.Spawn("packling", LabPos, 0, new CreatureSpawnOptions { Tag = Me, Flags = SpawnFlags.Transient });
            _mine.Add(e);
            await Steps(30);
            var g = World.ReadBytes(e, "ServerAnimationGraphComponent2");
            Check(g != null && BitConverter.ToInt32(g, 0) != 0, $"the packling spawns with a running graph: handle {(g == null ? 0 : BitConverter.ToInt32(g, 0))}");
            Check(StateOf(e) == Hex(x.GetProperty("idleState")), $"standing in the engine's Idle: 0x{StateOf(e):x8}");
            var rest = x.GetProperty("handRest").GetDouble();
            var idleLo = double.MaxValue; var idleHi = double.MinValue;
            for (var i = 0; i < 120; i++)
            {
                await Steps(1);
                if (HandHeight(e) is { } y) { idleLo = Math.Min(idleLo, y); idleHi = Math.Max(idleHi, y); }
            }
            Check(Math.Abs(idleLo - rest) < 0.15 && Math.Abs(idleHi - rest) < 0.15, $"in Idle the hand stays near its rest height {rest:F3} m: {idleLo:F3}..{idleHi:F3} m");

            // the AI off, so only this test starts anything: the creature's slam attack, whose sequence plays the slam state (the Attack clip, retargeted)
            Creatures.Of(e).AiEnabled = false;
            await Steps(5);
            var slam = Hex(x.GetProperty("slamState"));
            var played = Abilities.PlaySequence(e, BlockIds.Derive(Me, "sequence", "packling.slam"));
            var high = double.MinValue;
            var inSlam = 0;
            for (var i = 0; i < 90; i++)
            {
                await Steps(1);
                if (StateOf(e) == slam) inSlam++;
                if (HandHeight(e) is { } y) high = Math.Max(high, y);
            }
            Check(played && inSlam > 0, $"the slam attack's sequence plays the slam state: started {played}, in the state {inSlam} of 90 steps");
            var want = x.GetProperty("handHighest").GetDouble();
            Check(Math.Abs(high - want) < 0.03, $"the retargeted slam raises the hand to {high:F3} m of the body's own clip's {want:F3} m (the bigger rig's own bones would lift it about {(want - rest) * 1.3 + rest:F3} m)");
        }
        catch (Exception ex) { Check(false, "threw: " + ex); }
        finally
        {
            Log.Info($"{Me}: summary: {_pass} PASS, {_fail} FAIL");
            OnUnload();
        }
    }
}
