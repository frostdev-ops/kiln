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
//   lost      a destroyed body fails its Active lease BodyGone;
//   roles     (S5) a ride preempts the follow without a second snapshot and gives it back with a new sequence, another
//             owner's base lease is Busy, a Vehicle removes the body's behaviour for good and ends only with its body;
//   quarantine (S6) can_drive 0 in the same step, the root denied, a retired root's queued hit dropped (victim unharmed);
//   worker    (06a's lab parallel switch) an entity program on the game's workers reads the lease and feeds the body only
//             while it can drive; the broker's own program is SYSTEM and stays on the world's thread.
// The roles, quarantine and retirement checks call the native table directly (another owner, a Vehicle, quarantine are
// native clients' calls): through Kiln.CreatureControl's internal table, found by reflection (lab only).
// Layout offsets are those kiln.creature_control's layout guard checks (docs/kiln/services/creature-control.md).
// Lab only: without a player the game does not simulate a body's movement where it thinks the terrain is not streamed
// in; the test detours the two terrain checks of actor_is_ready_for_gameplay (the pattern noclip-test uses), so the
// forced-ready creatures fall, ground and walk as they do near a player.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Kiln;
using Kiln.Testing;

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
        await Roles(at with { Z = at.Z - 12 });
        await QuarantineAndRoot(at with { X = at.X - 12 });
        await Worker(at with { X = at.X + 24 });
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
        var far = 0.0;
        for (var i = 0; i < 400; i += 10)  // 400 steps (about 16 s) after the release, the farthest it got
        {
            await Steps(10);
            roam = Flat(r0, Pos(m));
            far = Math.Max(far, roam);
        }
        Check(far >= 1, $"release: AI roams (farthest {far:0.00} m from where it was released, {roam:0.00} m after 400 steps)");
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

    // ---- the native table, for the checks only a native client makes (lab only) ----

    [StructLayout(LayoutKind.Sequential)]
    private struct Key { public ulong Epoch, Generation; public uint Entity, Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Req { public uint Size, Role; public Key Body; public ulong RequestId; public uint Flags, Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct St
    {
        public uint Size, LeaseState, Result, RequestedRole, EffectiveRole, ChangedMask;
        public Key Body;
        public ulong Lease, Winning, RequestId, Seq, Notice;
        public fixed byte Reason[160];
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Table
    {
        public ulong Header;
        public delegate* unmanaged[Cdecl]<ulong, uint, uint, uint, Key*, int> Bind;
        public delegate* unmanaged[Cdecl]<ulong, Req*, St*, int> Acquire;
        public delegate* unmanaged[Cdecl]<ulong, ulong, St*, int> Query;
        public delegate* unmanaged[Cdecl]<ulong, ulong, Key, ulong, uint*, int> CanDrive;
        public delegate* unmanaged[Cdecl]<ulong, ulong, int> Release;
        public delegate* unmanaged[Cdecl]<ulong, St*, uint, uint*, uint*, int> Notices;
        public delegate* unmanaged[Cdecl]<Key, uint, int> Quarantine;
        public delegate* unmanaged[Cdecl]<ulong, Key, int> BodyRetired;
        public delegate* unmanaged[Cdecl]<ulong, uint, uint> RootPolicy;
        public delegate* unmanaged[Cdecl]<ulong, ulong*, int> NextRequest;
        public delegate* unmanaged[Cdecl]<ulong, Key, int> ReleaseBody;
    }

    private const ulong RawOwner = 0x7E57_C0DE;  // a native client of the test's own

    // The raw table, behind non-async helpers (async methods take no pointers).
    private sealed unsafe class RawTable(nint table)
    {
        private Table* T => (Table*)table;
        public int Bind(uint entity, uint kind, out Key key) { Key k; var rc = T->Bind(RawOwner, entity, kind, 1, &k); key = k; return rc; }
        public St Acquire(Key k, uint role)
        {
            ulong id = 0;
            T->NextRequest(RawOwner, &id);
            var q = new Req { Size = (uint)sizeof(Req), Role = role, Body = k, RequestId = id };
            St s = default;
            s.Size = (uint)sizeof(St);
            T->Acquire(RawOwner, &q, &s);
            return s;
        }
        public St Query(ulong lease) { St s = default; s.Size = (uint)sizeof(St); T->Query(RawOwner, lease, &s); return s; }
        public int Release(ulong lease) => T->Release(RawOwner, lease);
        public int ReleaseBody(Key k) => T->ReleaseBody(RawOwner, k);
        public int Quarantine(Key k, uint cause) => T->Quarantine(k, cause);
        public int Retired(Key k) => T->BodyRetired(RawOwner, k);
        public uint RootPolicy(ulong epoch, uint root) => T->RootPolicy(epoch, root);
    }

    private static unsafe RawTable? Raw()
    {
        var t = typeof(CreatureControl).Assembly.GetType("Kiln.CreatureControlNative", throwOnError: true)!;
        var p = t.GetProperty("T", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null);
        return p is null ? null : new RawTable((nint)Pointer.Unbox(p));
    }

    private static unsafe string ReasonOf(St s) => Marshal.PtrToStringUTF8((nint)s.Reason) ?? "";

    private static Key KeyOf(ControlBodyKey k) => new() { Epoch = k.WorldEpoch, Generation = k.BodyGeneration, Entity = k.Entity.Value };

    private async Task<bool> RawWaitActive(RawTable t, ulong lease, int steps = 150)
    {
        for (var i = 0; i < steps; i++)
        {
            var s = t.Query(lease);
            if (s.LeaseState == 1 && s.Winning == lease) return true;
            if (s.LeaseState >= 3) return false;
            await Steps(1);
        }
        return false;
    }

    // ---- S5: roles ----

    private async Task Roles(WorldPosition at)
    {
        var t = Raw();
        if (t is null)
        {
            Check(false, "roles: the native table is found");
            return;
        }
        var m = await Creature(Ground, at);
        if (m.IsNone) return;
        var f = Acquire(m, ControlRole.PetFollow);
        var fw = await CreatureControl.WaitActive(f.Lease, 5);
        if (!Check(fw.Ok, $"roles: Follow Active ({fw.Reason ?? "granted"})")) return;
        var key = fw.State!.Body;
        // another owner's base lease: Busy, the winner untouched
        var k = KeyOf(key);
        var bind = t.Bind(m.Value, 0, out var rk);
        var other = t.Acquire(k, 6);
        Check(bind == 0 && other.Lease == 0 && other.Result == 5 && CreatureControl.Inspect(f.Lease) is { CanDrive: true },
            $"busy: another owner's base lease refused ({ReasonOf(other)}), the Follow still drives");
        // a ride preempts the follow: the body stays without its SteeringAgent the whole time (no restore, no snapshot)
        var r = Acquire(m, ControlRole.Ridden);
        var steering = 0;
        var rideActive = false;
        for (var i = 0; i < 60 && !rideActive; i++)
        {
            await Steps(1);
            if (World.Has(m, "SteeringAgent")) steering++;
            rideActive = CreatureControl.Inspect(r.Lease) is { CanDrive: true };
        }
        var fs = CreatureControl.Inspect(f.Lease)!;
        var rs = CreatureControl.Inspect(r.Lease)!;
        Check(rideActive && fs.State == ControlLeaseState.Preempted && rs.TransitionSequence == 2 && steering == 0,
            $"ridden: preempts Follow, no second snapshot (Follow {fs.State}, Ridden seq {rs.TransitionSequence}, SteeringAgent back in {steering} steps)");
        var end = CreatureControl.Release(r.Lease);
        var back = false;
        for (var i = 0; i < 60 && !back; i++)
        {
            await Steps(1);
            if (World.Has(m, "SteeringAgent")) steering++;
            back = CreatureControl.Inspect(f.Lease) is { CanDrive: true };
        }
        var fb = CreatureControl.Inspect(f.Lease)!;
        Check(back && end.IsCompleted && fb.TransitionSequence == 3 && !AiOn(m) && steering == 0,
            $"ridden released: Follow Active seq {fb.TransitionSequence}, AI still off {!AiOn(m)}, SteeringAgent absent throughout {steering == 0}");
        await CreatureControl.Release(f.Lease);
        _leases.Remove(f.Lease);
        _leases.Remove(r.Lease);
        t.ReleaseBody(rk);
        await Steps(5);

        // a Vehicle: the behaviour goes for good; a driver comes and goes; the lease ends with its body
        var v = await Creature(Ground, at with { X = at.X + 5 });
        if (v.IsNone) return;
        Check(t.Bind(v.Value, 0, out var vk) == 0, "vehicle: bound");
        var vl = t.Acquire(vk, 5);
        var vActive = await RawWaitActive(t, vl.Lease);
        Check(vActive && !World.Has(v, "DynamicEnemy01BehaviorState") && !World.Has(v, "SteeringAgent") && (t.Query(vl.Lease).ChangedMask & 4) != 0,
            $"vehicle: Active, its behaviour and SteeringAgent removed ({ReasonOf(t.Query(vl.Lease))})");
        var dl = t.Acquire(vk, 3);
        var dActive = await RawWaitActive(t, dl.Lease);
        var vp = t.Query(vl.Lease);
        t.Release(dl.Lease);
        var vBack = await RawWaitActive(t, vl.Lease, 60);
        await Steps(20);
        Check(dActive && vp.LeaseState == 2 && vBack && !World.Has(v, "DynamicEnemy01BehaviorState"),
            $"vehicle: behaviour absent after driver left (driver Active {dActive}, Vehicle was {vp.LeaseState}, Active again {vBack})");
        var rr = t.Release(vl.Lease);
        Spawn.Destroy(v);  // synchronous: gone now; the owner says so in the same step, before the broker's next Pre
        _spawned.Remove(v);
        var ret = t.Retired(vk);
        var vr = t.Query(vl.Lease);
        Check(rr == 1 && ret == 0 && vr.LeaseState == 4 && vr.Result == 12,
            $"vehicle: release refused on the live body (RetireRequired, {rr}), retired with it ({ret}: {ReasonOf(vr)})");
    }

    // ---- S6: quarantine and the retired-root deny ----

    private async Task QuarantineAndRoot(WorldPosition at)
    {
        var t = Raw();
        if (t == null) return;
        var m = await Creature(Ground, at);
        var victim = await Creature(Ground, at with { X = at.X + 3 });
        if (m.IsNone || victim.IsNone) return;
        var f = Acquire(m, ControlRole.PetFollow);
        var w = await CreatureControl.WaitActive(f.Lease, 5);
        if (!Check(w.Ok, $"quarantine: Follow Active ({w.Reason ?? "granted"})")) return;
        var k = KeyOf(w.State!.Body);
        var before = CreatureControl.Inspect(f.Lease)!.CanDrive;
        var q = t.Quarantine(k, 5);
        var same = CreatureControl.Inspect(f.Lease)!;  // no step in between: the broker's published state, the latch over it
        Check(before && q == 0 && !same.CanDrive && same.EffectiveRole == ControlRole.Quarantine && t.RootPolicy(k.Epoch, m.Value) == 1,
            $"quarantine: can_drive 0 in the same step ({same.State}, effective {same.EffectiveRole}), root denied {t.RootPolicy(k.Epoch, m.Value)}");
        // retire it: the owner (here the test) destroys the body; the leases end Retired, the root stays denied
        _ = CreatureControl.Release(f.Lease);
        Spawn.Destroy(m);
        _spawned.Remove(m);
        await Steps(3);
        var fr = CreatureControl.Inspect(f.Lease);
        Check(fr is { State: ControlLeaseState.Failed, Result: ControlResultCode.Retired } && t.RootPolicy(k.Epoch, m.Value) == 1,
            $"quarantine: the body gone, its lease {fr?.Result}, its root still denied {t.RootPolicy(k.Epoch, m.Value)}");
        _leases.Remove(f.Lease);
        // a queued hit whose root is the retired body: dropped, the victim unharmed
        var hp0 = Combat.GetHealth(victim)?.Current ?? -1;
        var hit = await Combat.Damage(victim, 10, m, new DamageOptions { AsGameHit = true });
        await Steps(3);
        var hp1 = Combat.GetHealth(victim)?.Current ?? -1;
        Check(hit.State == HitState.Dropped && hp0 > 0 && hp1 == hp0,
            $"root: queued hit from a retired root {hit.State}, victim health {hp0} -> {hp1}");
        var ok = await Combat.Damage(victim, 10, default, new DamageOptions { AsGameHit = true });
        Info($"root: the same hit without that root: {ok.State}, health {ok.HealthBefore} -> {ok.HealthAfter}");
    }

    // ---- the worker-thread proof (06a's lab parallel switch) ----
    // cct_feed is an entity program over every ActorInput (read only, so it is not serial): with the parallel switch it runs
    // as tasks on the game's workers. For the leased body it reads the broker's state (Inspect, any thread) and the input
    // the tick wrote. The tick drives the body as a mod does, only while the lease can drive.

    private static volatile uint _feedBody;
    private static long _feedLease;
    private static int _tickThread;
    private static readonly ConcurrentDictionary<int, int> FeedThreads = new();
    private static long _feedReads, _feedDrive, _feedSawMove, _feedDriveAfterRelease;
    private static volatile bool _released;
    private IDisposable? _feed;

    [StructLayout(LayoutKind.Explicit, Size = 456)]
    private unsafe struct InputBytes { [FieldOffset(0)] public fixed byte B[456]; }

    private static unsafe void FeedProgram(EcsProgramContext ctx)
    {
        if (_feedBody == 0 || ctx.Entity.Value != _feedBody) return;
        Interlocked.Increment(ref _feedReads);
        FeedThreads.TryAdd(Environment.CurrentManagedThreadId, 0);
        if (CreatureControl.Inspect(new ControlLeaseId((ulong)Interlocked.Read(ref _feedLease))) is not { CanDrive: true }) return;
        Interlocked.Increment(ref _feedDrive);
        if (_released) Interlocked.Increment(ref _feedDriveAfterRelease);
        if (ctx.TryRead("ActorInput", out InputBytes input) && BitConverter.ToSingle(new ReadOnlySpan<byte>(input.B + AI_WorldMove, 4)) > 0.5f)
            Interlocked.Increment(ref _feedSawMove);
    }

    private async Task Worker(WorldPosition at)
    {
        _tickThread = Environment.CurrentManagedThreadId;
        try { _feed = EcsPrograms.Bind("cct_feed", FeedProgram); }
        catch (Exception e) { Info($"worker: the program could not be bound ({e.Message}); the worker check did not run"); return; }
        if (!LabSystems.Enabled)
        {
            Info("worker: the lab parallel switch is off (kiln/control/systems.parallel before the start); the worker check did not run");
            return;
        }
        var m = await Creature(Ground, at);
        if (m.IsNone) return;
        var f = Acquire(m, ControlRole.PetFollow);
        var w = await CreatureControl.WaitActive(f.Lease, 5);
        if (!Check(w.Ok, $"worker: Follow Active ({w.Reason ?? "granted"})")) return;
        Interlocked.Exchange(ref _feedLease, (long)f.Lease.Value);
        _feedBody = m.Value;
        var forced = LabSystems.ForceParallel(400);
        await Steps(2);
        Check(forced && LabSystems.PlayersMask is { } mask && (mask & 0x8000) != 0, $"worker: the lab parallel switch forced (players mask {LabSystems.PlayersMask:x4})");
        var p0 = Pos(m);
        for (var i = 0; i < 60; i++)
        {
            if (CreatureControl.Inspect(f.Lease) is { CanDrive: true })
            {
                World.WriteRaw(m, "ActorInput", AI_WorldMove, new Vector3(1, 0, 0));
                World.WriteRaw(m, "ActorInput", AI_Speed, (byte)3);
            }
            await Steps(1);
        }
        var moved = Flat(p0, Pos(m));
        var workers = FeedThreads.Keys.Count(id => id != _tickThread);
        Check(workers > 0 && Interlocked.Read(ref _feedDrive) > 0 && moved >= 5,
            $"worker: the lease was read on {workers} worker thread(s) (tick thread excluded, {FeedThreads.Count} in all): {Interlocked.Read(ref _feedReads)} reads, {Interlocked.Read(ref _feedDrive)} saw can_drive, {Interlocked.Read(ref _feedSawMove)} saw the tick's input; the body moved {moved:0.00} m in 60 steps on the parallel path");
        _released = true;
        var rel = CreatureControl.Release(f.Lease);
        var reads = Interlocked.Read(ref _feedReads);
        await Steps(30);
        Check(Interlocked.Read(ref _feedDriveAfterRelease) == 0 && Interlocked.Read(ref _feedReads) > reads && rel.IsCompleted && rel.Result.Ok,
            $"worker: after the release no worker saw can_drive ({Interlocked.Read(ref _feedDriveAfterRelease)} of {Interlocked.Read(ref _feedReads) - reads} reads), the lease {rel.Result?.State?.State}");
        _leases.Remove(f.Lease);
        LabSystems.StopParallel();
        _feedBody = 0;
        _feed?.Dispose();
    }
}
