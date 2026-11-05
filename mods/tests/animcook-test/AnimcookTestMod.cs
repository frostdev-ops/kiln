// SPDX-License-Identifier: MIT
// animcook lab test, player-free: our own glTF (tools/animcook/samples/make_golem.py), cooked by tools/animcook, running in the server's animation
// engine. Prints "animcook-test: PASS ..." / "FAIL ..." / "INFO ..." to kiln.log. The arm's rotation from the HandR slot, relative to its rest,
// is compared with the numbers the glTF was authored with. Nothing here is drawn: what a client shows is a manual check (docs/kiln/modding/animations.md).
using System.Text.Json;
using Kiln;

namespace AnimcookTest;

public sealed class AnimcookTestMod : Mod
{
    private static readonly WorldPosition Pos = new(3865.8, 833.0, 1433.8);
    private int _pass, _fail;
    private readonly List<EntityId> _mine = [];
    private readonly List<HookHandle> _hooks = [];
    private nuint _world;
    private Dictionary<string, uint> _states = new(), _nested = new();
    private Dictionary<string, Guid> _seq = new();

    private void Info(string s) => Log.Info("animcook-test: INFO " + s);
    private bool Check(bool ok, string what)
    {
        if (ok) _pass++; else _fail++;
        Log.Info($"animcook-test: {(ok ? "PASS" : "FAIL")} {what}");
        return ok;
    }

    public override void OnLoad() => _ = Guarded();
    public override void OnUnload() { foreach (var e in _mine) try { Spawn.Destroy(e); } catch { } _mine.Clear(); foreach (var h in _hooks) try { h.Remove(); } catch { } _hooks.Clear(); }
    private async Task Steps(int n) { for (var i = 0; i < n; i++) await Context.Scheduler.NextTick(); }

    private async Task Guarded()
    {
        try
        {
            var t0 = DateTime.UtcNow;
            while (!Spawn.WorldRunning && (DateTime.UtcNow - t0).TotalSeconds < 240) await Context.Scheduler.NextTick();
            await Steps(60);
            var h = Hooks.Detour("GetComponent", c => { if (_world == 0) _world = c[1]; return c.CallOriginal(); });
            for (var i = 0; i < 200 && _world == 0; i++) await Steps(1);
            h.Remove();
            LoadExpect();
            await Nested();
            await Puppet();
            await RootMotion();
            await Inputs();
        }
        catch (Exception e) { Check(false, "threw: " + e); }
        finally { Log.Info($"animcook-test: summary: {_pass} PASS, {_fail} FAIL"); OnUnload(); }
    }

    private void LoadExpect()
    {
        using var d = JsonDocument.Parse(File.ReadAllText(Path.Combine(Context.Info.Directory, "assets/cooked/expect.json")));
        static uint Hex(JsonElement e) => Convert.ToUInt32(e.GetProperty("id").GetString()![2..], 16);
        foreach (var s in d.RootElement.GetProperty("states").EnumerateObject()) _states[s.Name] = Hex(s.Value);
        foreach (var s in d.RootElement.GetProperty("nested").EnumerateObject()) _nested[s.Name] = Hex(s.Value);
        foreach (var s in d.RootElement.GetProperty("seq").EnumerateObject()) _seq[s.Name] = Guid.Parse(s.Value.GetString()!);
    }

    private double[,]? Slot(EntityId e, int slot, out double[] pos)
    {
        pos = new double[3];
        var b = World.ReadBytes(e, "ServerAnimationGraphSlotComponent");
        if (b == null) return null;
        var o = 0x20 + slot * 48;
        var m = new double[3, 3];
        for (var c = 0; c < 3; c++) for (var r = 0; r < 3; r++) m[r, c] = BitConverter.ToSingle(b, o + (c * 3 + r) * 4);
        for (var r = 0; r < 3; r++) pos[r] = BitConverter.ToSingle(b, o + (9 + r) * 4);
        return m;
    }

    private double[,] _rest = new double[3, 3];
    /// <summary>The arm's extra rotation about its own z, as authored in glTF (the game's space mirrors it: the sign flips).</summary>
    private double Arm(EntityId e)
    {
        var m = Slot(e, 8, out _);
        if (m == null) return double.NaN;
        double r10 = 0, r00 = 0;
        for (var i = 0; i < 3; i++) { r10 += _rest[i, 1] * m[i, 0]; r00 += _rest[i, 0] * m[i, 0]; }
        return -Math.Atan2(r10, r00) * 180 / Math.PI;
    }

    private void SetState(EntityId e, uint state)
    {
        var a = Raw.Dyn(_world, "ActorInput", e);
        Raw.SetU32(a, state);
    }

    private async Task Nested()
    {
        var opts = new SpawnOptions { Tag = "animcook", Flags = SpawnFlags.Transient };
        var e = await Spawn.Entity("Kiln_AnimCook_Nested", Pos, 0, opts);
        _mine.Add(e);
        await Steps(30);
        var comp = World.ReadBytes(e, "ServerAnimationGraphComponent2");
        Check(comp != null && BitConverter.ToInt32(comp, 0) != 0, $"nested graph of 40 states runs: handle {(comp == null ? 0 : BitConverter.ToInt32(comp, 0))}");
        var r = Slot(e, 8, out var p0);
        if (r == null) { Check(false, "no slots"); return; }
        _rest = r;
        Info($"rest HandR at ({p0[0]:F3}, {p0[1]:F3}, {p0[2]:F3})");
        Check(Math.Abs(p0[1]) > 1.0, "the slot carries our skeleton's rest pose");
        var prev = 0.0;
        foreach (var i in new[] { 39, 1, 33, 34, 35, 20, 0, 34, 33, 2, 38 })
        {
            var want = _nested[i == 0 ? "idle" : $"s{i:00}"];
            var lo = double.MaxValue; var hi = double.MinValue;
            var target = 2.0 * i;
            var trace = new List<string>();
            for (var s = 0; s < 18; s++)
            {
                SetState(e, want);
                await Steps(1);
                var a = Arm(e);
                lo = Math.Min(lo, a); hi = Math.Max(hi, a);
                if (s < 8) trace.Add(a.ToString("F1"));
            }
            var end = Arm(e);
            var inBounds = lo >= Math.Min(prev, target) - 1 && hi <= Math.Max(prev, target) + 1;
            Check(Math.Abs(end - target) < 0.5, $"nested state s{i:00}: arm {end:F2} deg of {target:F0}");
            Info($"  from {prev:F0} to {target:F0} trace {string.Join(' ', trace)} range [{lo:F1}, {hi:F1}] {(inBounds ? "stays between" : "leaves the range: a nested entry state shows")}");
            prev = end;
        }
        Spawn.Destroy(e);
    }

    private async Task<(double lo, double hi, double end)> Watch(EntityId e, uint? state, int steps)
    {
        var lo = double.MaxValue; var hi = double.MinValue; var a = 0.0;
        for (var s = 0; s < steps; s++)
        {
            if (state != null) SetState(e, state.Value);
            await Steps(1);
            a = Arm(e);
            lo = Math.Min(lo, a); hi = Math.Max(hi, a);
        }
        return (lo, hi, a);
    }

    private async Task Puppet()
    {
        var opts = new SpawnOptions { Tag = "animcook", Flags = SpawnFlags.Transient };
        var e = await Spawn.Entity("Kiln_AnimCook_Puppet", Pos with { Z = Pos.Z + 10 }, 0, opts);
        _mine.Add(e);
        await Steps(20);
        var w = await Watch(e, _states["idle"], 70);
        Check(Math.Abs(w.hi - 30) < 1.5 && Math.Abs(w.lo + 30) < 1.5, $"Idle, LINEAR keys, loop: arm {w.lo:F1}..{w.hi:F1} deg of -30..30");
        w = await Watch(e, _states["run"], 15); w = await Watch(e, _states["run"], 30);
        Check(Math.Abs(w.hi - 70) < 4 && Math.Abs(w.lo + 70) < 4, $"Run: arm {w.lo:F1}..{w.hi:F1} deg of -70..70");
        w = await Watch(e, _states["walk"], 15); w = await Watch(e, _states["walk"], 30);
        Check(Math.Abs(w.hi - 40) < 3, $"Walk blend space at input 0 plays walk_f: arm peak {w.hi:F1} deg of 40");
        await Watch(e, _states["idle"], 20);
        w = await Watch(e, _states["fall"], 16);  // 0.65 s into STEP keys at 0, 0.5, 1 s
        Check(Math.Abs(w.end - 45) < 1, $"Fall, STEP keys: arm {w.end:F1} deg of 45 at 0.65 s");
        w = await Watch(e, _states["fall"], 20);
        Check(Math.Abs(w.end - 90) < 1, $"Fall holds its last key: arm {w.end:F1} deg of 90");
        await Watch(e, _states["idle"], 20);
        w = await Watch(e, _states["chain_a"], 12);
        Check(Math.Abs(w.end - 20) < 1.5, $"chain_a: arm {w.end:F1} deg of 20");
        w = await Watch(e, _states["chain_a"], 25);
        Check(Math.Abs(w.end - 60) < 1.5, $"then: chain_a moved on to chain_b at its end by itself, the input still on chain_a: arm {w.end:F1} deg of 60");
        await Watch(e, _states["idle"], 15);
        w = await Watch(e, _states["chain_a"], 12);
        Check(Math.Abs(w.end - 20) < 1.5, $"a chain starts over when entered again: arm {w.end:F1} deg of 20");
        w = await Watch(e, _states["chain_a"], 25);
        Check(Math.Abs(w.end - 60) < 1.5, $"and moves on again: arm {w.end:F1} deg of 60");
        w = await Watch(e, _states["chain_b"], 10);
        Check(Math.Abs(w.end - 60) < 1.5, $"the chain's second state answers its own id: arm {w.end:F1} deg of 60");
        // an additive overlay layer with a bone mask, on top of a held pose
        var head0 = Slot(e, 1, out var hp0);
        var ok = Abilities.PlaySequence(e, _seq["overlay"], abortCurrent: true);
        var peak = double.MinValue; var headMoved = 0.0;
        for (var s = 0; s < 30; s++)
        {
            await Steps(1);
            peak = Math.Max(peak, Arm(e));
            Slot(e, 1, out var hp);
            headMoved = Math.Max(headMoved, Math.Abs(hp[0] - hp0[0]) + Math.Abs(hp[1] - hp0[1]) + Math.Abs(hp[2] - hp0[2]));
        }
        Check(ok && peak > 75 && peak < 90, $"overlay 'hit' plays the additive flinch over the held 60 deg: arm peak {peak:F1} deg of 85 (the blend rounds it)");
        Check(headMoved < 0.001, $"the bone mask keeps the layer off the head: head moved {headMoved * 1000:F2} mm");
        w = await Watch(e, null, 25);
        Info($"after the overlay, arm {w.end:F1}");
        // a clip with events
        ok = Abilities.PlaySequence(e, _seq["slam"], abortCurrent: true);
        var lo = double.MaxValue;
        for (var s = 0; s < 30; s++) { await Steps(1); lo = Math.Min(lo, Arm(e)); }
        Check(ok && Math.Abs(lo + 90) < 5, $"Slam, CUBICSPLINE keys and events: arm low {lo:F1} deg of -90");
        Spawn.Destroy(e);
    }

    private async Task RootMotion()
    {
        var opts = new SpawnOptions { Tag = "animcook", Flags = SpawnFlags.Transient };
        var e = await Spawn.Entity("Kiln_AnimCook_Golem", Pos with { Z = Pos.Z + 20 }, 0, opts);
        var v = await Spawn.Entity("Enemy_Vukah_Melee", Pos with { X = Pos.X + 12, Z = Pos.Z + 20 }, 0, opts);
        _mine.Add(e); _mine.Add(v);
        await Steps(25);
        // a mod's own float input, from a sequence, through a blend space: actor_locomotion applies these, so the body needs Locomotion
        var ok = Abilities.PlaySequence(e, _seq["lean1"], abortCurrent: true);
        await Steps(20);
        var l1 = Arm(e);
        Check(ok && Math.Abs(l1 - 40) < 2, $"a mod's own float input from a sequence drives a blend space: input 1: arm {l1:F1} deg of 40");
        ok = Abilities.PlaySequence(e, _seq["lean2"], abortCurrent: true);
        await Steps(20);
        var l2 = Arm(e);
        Check(ok && Math.Abs(l2 - 78) < 2, $"input 2: arm {l2:F1} deg of 78");
        foreach (var (who, ent, seq) in new[] { ("golem", e, _seq["step"]), ("vukah control", v, _seq["control"]) })
        {
            var p0 = World.Position(ent)!.Value;
            ok = Abilities.PlaySequence(ent, seq, abortCurrent: true);
            var maxTr = 0.0; var rmMode = 0; var src = 0; var state = 0;
            for (var s = 0; s < 50; s++)
            {
                await Steps(1);
                var d = World.ReadBytes(ent, "DynamicLocomotion");
                if (d != null)
                {
                    maxTr = Math.Max(maxTr, Math.Sqrt(Math.Pow(BitConverter.ToSingle(d, 0x4c), 2) + Math.Pow(BitConverter.ToSingle(d, 0x50), 2) + Math.Pow(BitConverter.ToSingle(d, 0x54), 2)));
                    rmMode |= d[0x58]; src |= d[0x3c]; state = d[0x59];
                }
            }
            var p1 = World.Position(ent)!.Value;
            Info($"root motion, {who}: sequence {ok}, moved {p1.X - p0.X:F3} {p1.Y - p0.Y:F3} {p1.Z - p0.Z:F3} m, largest rootMotionTranslation {maxTr:F4}, mode 0x{rmMode:x}, source {src}, locomotion state {state}");
            if (who == "golem") Check(maxTr > 0.001 || Math.Abs(p1.Z - p0.Z) > 0.2, $"root motion reaches the body: rootMotionTranslation {maxTr:F4} per step, moved {p1.Z - p0.Z:F3} m");
        }
        Spawn.Destroy(e); Spawn.Destroy(v);
    }

    private async Task Inputs()
    {
        var opts = new SpawnOptions { Tag = "animcook", Flags = SpawnFlags.Transient };
        var e = await Spawn.Entity("Enemy_Vukah_Melee", Pos with { Z = Pos.Z - 30 }, 0, opts);
        _mine.Add(e);
        await Steps(50);
        var inp = World.ReadBytes(e, "ServerAnimationGraphInput");
        if (inp != null)
        {
            var used = 0; var list = new List<string>();
            for (var i = 0; i < 16; i++) { var h = BitConverter.ToUInt32(inp, 0x10 + i * 8); if (h != 0) { used++; list.Add($"{h:x8}"); } }
            Info($"float inputs in use on a standing Vukah: {used} of 16: {string.Join(' ', list)}");
        }
        // With mods/tests/clipcheck loaded, the Vukah's head turns against its chest: compare this line between a run with and without it.
        double sum = 0, lo = 999, hi = -999; var n = 0;
        for (var s = 0; s < 240; s++)
        {
            await Steps(1);
            var c = Slot(e, 2, out _); var h = Slot(e, 1, out _);
            if (c == null || h == null) continue;
            double tr = 0;
            for (var i = 0; i < 3; i++) for (var j = 0; j < 3; j++) if (i == j) { double d = 0; for (var k = 0; k < 3; k++) d += c[k, i] * h[k, j]; tr += d; }
            var a = Math.Acos(Math.Clamp((tr - 1) / 2, -1, 1)) * 180 / Math.PI;
            sum += a; n++; lo = Math.Min(lo, a); hi = Math.Max(hi, a);
        }
        Info($"Vukah head against chest over {n} steps: mean {sum / Math.Max(1, n):F2} deg, range {lo:F2}..{hi:F2}");
        Spawn.Destroy(e);
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
