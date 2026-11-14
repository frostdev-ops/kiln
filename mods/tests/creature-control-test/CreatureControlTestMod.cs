// SPDX-License-Identifier: MIT
// kiln.creature_control lab test (19d-creature-control S1, S2), player-free. Prints "creature-control-test: PASS ..." /
// "FAIL ..." / "INFO ..." lines to kiln.log. Forced-ready creatures (kiln/data/kiln.creatures/force-ready) near the lab's
// base, Transient, destroyed at the end:
//   rows      kiln.creature_control and creature_control available (the service's own "guard:" line lists the paths);
//   acquire   a lease is Pending and cannot drive;
//   active    Active with the SteeringAgent gone, the AI off, transition sequence 1;
//   drive     tick writes of ActorInput (desiredWorldMoveInput, locomotionSpeed Run) move the body >= 5 m in 40 steps;
//   release   can_drive false at the call; Released after the restore: an 80-byte SteeringAgent with the saved
//             configuration, queryId 0 then made anew by the game within 20 steps; the AI back on and roaming;
//   attack    a lease asked during a swing stays Pending until the swing ended (INFO when no swing could be started);
//   lost      a destroyed body fails its Active lease BodyGone.
// Layout offsets are those kiln.creature_control's layout guard checks (docs/kiln/services/creature-control.md).
// Lab only: without a player the game does not simulate a body's movement where it thinks the terrain is not streamed
// in; the test detours the two terrain checks of actor_is_ready_for_gameplay (the pattern noclip-test uses), so the
// forced-ready creatures fall, ground and walk as they do near a player.
using System.Diagnostics;
using System.Numerics;
using Kiln;

namespace CreatureControlTest;

public sealed class CreatureControlTestMod : Mod
{
    private const string Me = "creature-control-test";
    private const int AI_WorldMove = 0x198, AI_Speed = 0x1c4;           // ActorInput
    private const int Actor_PlayState = 0x1b1, Actor_State = 0xbf8;     // Actor: currentAction.playbackContext.state, currentState
    private const int SA_QueryId = 0, SA_DesiredVelocity = 8, SA_Velocity = 0x1c;  // SteeringAgent
    private const int Flag_Grounded = 0, Flag_Ready = 50;

    private int _pass, _fail;
    private readonly List<EntityId> _spawned = [];
    private readonly List<ControlLeaseId> _leases = [];
    private readonly List<HookHandle> _hooks = [];

    private void Info(string s) => Log.Info($"{Me}: INFO {s}");
    private bool Check(bool ok, string what)
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"{Me}: {(ok ? "PASS" : "FAIL")} {what}");
        return ok;
    }

    public override void OnLoad() => _ = RunGuarded();

    public override void OnUnload() => Cleanup();

    private void Cleanup()
    {
        foreach (var l in _leases)
            try { _ = CreatureControl.Release(l); } catch { }
        _leases.Clear();
        foreach (var e in _spawned)
            try { Spawn.Destroy(e); } catch { }
        _spawned.Clear();
        foreach (var h in _hooks)
            try { h.Remove(); } catch { }
        _hooks.Clear();
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    private async Task RunGuarded()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            while (!(Spawn.WorldRunning && Build.Available) && sw.Elapsed.TotalSeconds < 240) await Steps(1);
            Info($"world running after {sw.Elapsed.TotalSeconds:F1} s");
            // "the terrain at the actor is loaded" (lab only; see the header)
            var ready = Symbols.FindAll("48 89 5C 24 08 57 48 83 EC 30 F3 0F 10 15 ?? ?? ?? ?? 0F 57 C0 F3 49 0F 2A 00 48 8B C2 48 8B F9 48 8D 54 24 20 48 8B C8 49 8B D8");
            foreach (var r in ready) _hooks.Add(Hooks.Detour(r, _ => 1));
            Info($"terrain readiness checks detoured: {ready.Length}");
            await Steps(50);
            await Run();
        }
        catch (Exception e)
        {
            Check(false, $"test threw: {e}");
        }
        finally
        {
            Cleanup();
        }
        await Steps(20);
        Log.Info($"{Me}: summary: {_pass} PASS, {_fail} FAIL");
    }

    // ---- helpers ----

    private static WorldPosition Pos(EntityId e) => World.Position(e) ?? new WorldPosition(double.NaN, double.NaN, double.NaN);

    private static double Flat(WorldPosition a, WorldPosition b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    private static ulong StateWord(EntityId e) => World.ReadBytes(e, "Actor") is { Length: > Actor_State + 8 } a ? BitConverter.ToUInt64(a, Actor_State) : 0;

    private static bool Has(EntityId e, int flag) => ((StateWord(e) >> flag) & 1) != 0;

    private static bool ActionPlaying(EntityId e) => World.ReadBytes(e, "Actor") is { Length: > Actor_PlayState } a && (a[Actor_PlayState] & 1) != 0;

    private static bool AiOn(EntityId e)
    {
        try { return Creatures.Of(e).AiEnabled; }
        catch (Exception) { return false; }
    }

    private async Task<WorldPosition> Anchor()
    {
        var bases = Build.Bases;
        var anchor = bases.Count > 0 ? bases[0].Center : new WorldPosition(3685, 835, 1480);
        var at = anchor with { X = Math.Floor(anchor.X) + 160.25, Z = Math.Floor(anchor.Z) + 0.25 };
        try { await Build.Load(Build.BoxAround(at, 32, 60, 32)); } catch (Exception e) { Info($"loading the terrain: {e.Message}"); }
        return at with { Y = (GroundY(at) ?? anchor.Y) + 2 };  // a little above the ground: the body falls onto it
    }

    private static double? GroundY(WorldPosition at)
    {
        try
        {
            var box = Build.BoxAround(at with { Y = at.Y + 100 - 128 }, 0.1, 128, 0.1);
            if (!Build.TryRead(box, out var cells) || cells is null) return null;
            var layer = Math.Max(1, box.SizeX * box.SizeZ);
            for (var i = cells.Length - 1; i >= 0; i--)
                if (!cells[i].IsAir) return Build.CornerOf(new CellPos(box.X, box.Y + i / layer, box.Z)).Y + (Build.Grid?.CellSize ?? 0.5);
        }
        catch (Exception) { }
        return null;
    }

    private async Task<EntityId> Creature(string[] templates, WorldPosition at)
    {
        foreach (var template in templates)
        {
            EntityId e;
            try { e = await Spawn.Entity(template, at with { Y = at.Y + 0.5 }, 0, new SpawnOptions { Tag = Me, Flags = SpawnFlags.Transient }); }
            catch (Exception ex) { Info($"spawn {template}: {ex.Message}"); continue; }
            if (e.IsNone) continue;
            _spawned.Add(e);
            for (var i = 0; i < 125 && !(World.Exists(e) && double.IsFinite(Pos(e).X)); i++) await Steps(1);
            try { Creatures.Of(e).ForceReady(); } catch (Exception ex) { Info($"force ready {template}: {ex.Message}"); }
            try { Combat.SetInvulnerable(e); } catch (Exception ex) { Info($"invulnerable {template}: {ex.Message}"); }
            for (var i = 0; i < 300 && !Has(e, Flag_Ready); i++) await Steps(1);
            for (var i = 0; i < 300 && !Has(e, Flag_Grounded); i++) await Steps(1);
            Info($"{template} {e}: ready {Has(e, Flag_Ready)} grounded {Has(e, Flag_Grounded)} AI {AiOn(e)} SteeringAgent {World.Has(e, "SteeringAgent")} at {Pos(e)}");
            return e;
        }
        return default;
    }

    private ControlRequestResult Acquire(EntityId body, ControlRole role)
    {
        var r = CreatureControl.Acquire(new EntityRef(body, 0), role, CreatureControl.NewRequestId());
        if (r.Ok) _leases.Add(r.Lease);
        return r;
    }

    // ---- the test ----

    private static readonly string[] Ground = ["Enemy_Scavenger_ColdHeights_SnowSabertooth_Tamed", "Enemy_Wildbeast_Wolf_Heavy", "Enemy_Wildbeast_Boar"];

    private async Task Run()
    {
        var rows = Check(CreatureControl.Available && Capabilities.Is("kiln.creature_control"), "rows: kiln.creature_control (the table) resolves");
        Check(Capabilities.Is("creature_control"), $"rows: creature_control is available ({Capabilities.Why("creature_control")})");
        if (!rows || !Capabilities.Is("creature_control")) return;
        if (!File.Exists(Path.Combine(Context.DataDirectory, "..", "kiln.creatures", "force-ready")))
            Info("kiln/data/kiln.creatures/force-ready may be missing: the creatures will not become ready without a player");
        var at = await Anchor();
        Info($"anchor {at}");
        await LeaseLife(at);
        await Attack(at with { X = at.X + 12 });
        await Lost(at with { Z = at.Z + 12 });
    }

    private async Task LeaseLife(WorldPosition at)
    {
        var m = await Creature(Ground, at with { X = at.X + 6 });
        if (!Check(!m.IsNone, "a ground creature spawned")) return;
        var saved = World.ReadBytes(m, "SteeringAgent");
        Info($"SteeringAgent before: {saved?.Length ?? 0} bytes, queryId {(saved is null ? 0 : BitConverter.ToUInt32(saved, SA_QueryId))}");

        // acquire
        var r = Acquire(m, ControlRole.PetFollow);
        if (!Check(r.Ok, $"acquire: admitted ({r.Reason ?? "ok"})")) return;
        var lease = r.Lease;
        var p = CreatureControl.Inspect(lease)!;
        Check(p.State == ControlLeaseState.Pending && !p.CanDrive && p.TransitionSequence == 0,
            $"acquire: Pending, can_drive {(p.CanDrive ? 1 : 0)} (state {p.State}, seq {p.TransitionSequence})");

        // active
        var w = await CreatureControl.WaitActive(lease, 5);
        var a = CreatureControl.Inspect(lease);
        var steps = 0;
        Check(w.Ok && a is { CanDrive: true }, $"active: WaitActive answered {w.Ok} ({w.Reason ?? "granted"})");
        Check(!World.Has(m, "SteeringAgent") && !AiOn(m) && a?.TransitionSequence == 1 && (a.ChangedMask & 3) == 3,
            $"active: SteeringAgent absent {!World.Has(m, "SteeringAgent")}, AI off {!AiOn(m)}, seq {a?.TransitionSequence}, mask {a?.ChangedMask}");
        if (!w.Ok) return;

        // drive: the mod's own tick-thread write, only while can_drive says so
        var p0 = Pos(m);
        var written = 0;
        for (var i = 0; i < 40; i++)
        {
            if (CreatureControl.Inspect(lease) is { CanDrive: true })
            {
                World.WriteRaw(m, "ActorInput", AI_WorldMove, new Vector3(1, 0, 0));
                World.WriteRaw(m, "ActorInput", AI_Speed, (byte)3);
                written++;
            }
            await Steps(1);
        }
        var moved = Flat(p0, Pos(m));
        Check(moved >= 5, $"drive: tick write moved {moved:0.00} m in 40 steps ({written} writes under can_drive)");

        // release
        var release = CreatureControl.Release(lease);
        var right = CreatureControl.Inspect(lease);
        Check(right is { CanDrive: false } && right.WinningLease.Value == 0, $"release: can_drive 0 at the call (state {right?.State}, winner {right?.WinningLease.Value})");
        var pr = Pos(m);
        while (!release.IsCompleted && steps < 60)
        {
            await Steps(1);
            steps++;
        }
        var rr = release.IsCompleted ? release.Result : null;
        var back = World.ReadBytes(m, "SteeringAgent");
        var q0 = back is null ? uint.MaxValue : BitConverter.ToUInt32(back, SA_QueryId);
        var drift = Flat(pr, Pos(m));
        Check(rr is { Ok: true, State.State: ControlLeaseState.Released }, $"release: Released after {steps} steps ({rr?.Reason ?? rr?.State?.Reason})");
        var config = saved is not null && back is not null && back.Length == saved.Length && Same(saved, back);
        Check(back is { Length: 80 } && config, $"release: restored {back?.Length ?? 0} bytes, configuration as saved {config}, queryId {q0} at the release");
        Check(drift < 1.5, $"release: the body stopped while the input was zeroed (moved {drift:0.00} m from the call to Released)");
        var made = -1;
        for (var i = 0; i <= 40; i++)
        {
            if (World.ReadBytes(m, "SteeringAgent") is { } now && BitConverter.ToUInt32(now, SA_QueryId) != 0)
            {
                made = i;
                break;
            }
            await Steps(1);
        }
        Check(q0 == 0 && made >= 0 && made <= 20, $"release: queryId 0 then recreated within 20 steps (made after {made} steps)");
        Check(AiOn(m), $"release: AI back on ({AiOn(m)})");
        var r0 = Pos(m);
        var roam = 0.0;
        for (var i = 0; i < 400 && roam < 1; i += 10)
        {
            await Steps(10);
            roam = Flat(r0, Pos(m));
        }
        Check(roam >= 1, $"release: AI roams ({roam:0.00} m after the release)");
        _leases.Remove(lease);
    }

    // the configuration fields the restore keeps (all but the transient query id and the two velocity vectors)
    private static bool Same(byte[] a, byte[] b)
    {
        for (var i = 0; i < a.Length; i++)
        {
            if (i < 4 || (i >= SA_DesiredVelocity && i < SA_DesiredVelocity + 12) || (i >= SA_Velocity && i < SA_Velocity + 12)) continue;
            if (i >= 0x48) continue;  // nextAllowedKeepDistanceCheckTime: a time the game moves on its own
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    private async Task Attack(WorldPosition at)
    {
        var v = await Creature(["Enemy_Vukah_Melee"], at);
        var t = await Creature(Ground, at with { X = at.X + 2.5 });
        if (v.IsNone || t.IsNone)
        {
            Info("attack: the creatures did not spawn; the attack check did not run");
            return;
        }
        try { Creatures.Of(t).Faction = Faction.PlayerAlly; } catch (Exception e) { Info($"attack: faction: {e.Message}"); }
        var swing = -1;
        for (var i = 0; i < 300 && swing < 0; i++)
        {
            if (i % 25 == 0)
                try { Creatures.Of(v).Command.Attack(t); } catch (Exception e) { Info($"attack: command: {e.Message}"); }
            await Steps(1);
            if (ActionPlaying(v)) swing = i;
        }
        if (swing < 0)
        {
            Info("attack: no swing started within 300 steps (no target in reach without a player?); the attack check did not run");
            return;
        }
        var r = Acquire(v, ControlRole.PetStay);
        if (!Check(r.Ok, $"attack: admitted during a swing ({r.Reason ?? "ok"})")) return;
        int pendingWhilePlaying = 0, activeWhilePlaying = 0, step = 0;
        var wasPlaying = true;
        for (; step < 200; step++)
        {
            var playing = ActionPlaying(v);
            var s = CreatureControl.Inspect(r.Lease);
            if (s is { CanDrive: true })
            {
                if (playing || wasPlaying) activeWhilePlaying += playing ? 1 : 0;
                break;
            }
            if (playing) pendingWhilePlaying++;
            wasPlaying = playing;
            await Steps(1);
        }
        var active = CreatureControl.Inspect(r.Lease) is { CanDrive: true };
        Check(active && pendingWhilePlaying > 0 && activeWhilePlaying == 0,
            $"attack: lease waits for the swing to end (Pending {pendingWhilePlaying} steps while it played, Active {(active ? $"after {step} steps" : "never")}, playing at the grant {activeWhilePlaying > 0})");
        var rel = await CreatureControl.Release(r.Lease);
        _leases.Remove(r.Lease);
        Info($"attack: released {rel.Ok} ({rel.Reason ?? rel.State?.Reason})");
    }

    private async Task Lost(WorldPosition at)
    {
        var m = await Creature(Ground, at);
        if (m.IsNone) return;
        var r = Acquire(m, ControlRole.Ridden);
        if (!Check(r.Ok, $"lost: admitted ({r.Reason ?? "ok"})")) return;
        var w = await CreatureControl.WaitActive(r.Lease, 5);
        if (!Check(w.Ok, $"lost: Active ({w.Reason ?? "granted"})")) return;
        Spawn.Destroy(m);
        _spawned.Remove(m);
        await Steps(3);
        var s = CreatureControl.Inspect(r.Lease);
        Check(s is { State: ControlLeaseState.Failed, Result: ControlResultCode.BodyGone, CanDrive: false },
            $"lost: a destroyed body fails its lease ({s?.State} {s?.Result}: {s?.Reason})");
        var rel = await CreatureControl.Release(r.Lease);
        Check(!rel.Ok && rel.Reason?.StartsWith("BodyGone", StringComparison.Ordinal) == true, $"lost: Release answers the loss ({rel.Ok}: {rel.Reason})");
        _leases.Remove(r.Lease);
    }
}
