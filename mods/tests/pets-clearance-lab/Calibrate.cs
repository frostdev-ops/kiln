// SPDX-License-Identifier: MIT
// One creature's calibration: its body (spawned without an owner, held by Kiln.Pets' leases), the route sweep per fixture,
// the real follower's walk of every accepted pair (catch-up and teleport off), and the body's own walker as the comparison.
// Every time here is game time (Now: the game's clock), never a count of steps.
using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Nodes;
using Kiln;

namespace PetsClearanceLab;

public sealed partial class PetsClearanceLabMod
{
    private async Task Calibrate(PlanCreature c, Site site)
    {
        var tag = $"{c.Id} x{Out.F(c.Scale)}";
        var info = PetCreatures.Resolve(c.Id, out var why);
        if (info is null || info.Problem is not null)
        {
            Info($"{tag}: refused: {why ?? info?.Problem}");
            Evidence.Write("creature", new JsonObject { ["creature"] = c.Id, ["scale"] = c.Scale, ["refused"] = why ?? info?.Problem });
            return;
        }
        var control = site.Control ?? site.Lanes[0];
        var e = await SpawnCreature(info, control.Start with { Y = control.Start.Y + 0.6 });
        if (e.IsNone)
        {
            Info($"{tag}: FAIL did not spawn");
            Evidence.Write("creature", new JsonObject { ["creature"] = c.Id, ["scale"] = c.Scale, ["refused"] = "SpawnFailed" });
            return;
        }
        var cr = Creatures.Of(e);
        try { cr.ForceReady(); } catch (Exception x) { Info($"{tag}: force ready: {x.Message}"); }
        try
        {
            cr.Faction = Faction.PlayerAlly;
            if (Math.Abs(c.Scale - 1) > 1e-6) cr.Scale = (float)c.Scale;
        }
        catch (Exception x) { Info($"{tag}: faction/scale: {x.Message}"); }
        await Until(() => Has(e, Flag_Ready), 15);
        await Until(() => Grounded(e), 15);
        // after readiness: a body has no health to protect the step it spawns
        try { Combat.SetInvulnerable(e); } catch (Exception x) { Info($"{tag}: invulnerable: {x.Message}"); }

        // the body as Kiln.Pets takes it: PetStay to prepare, then PetFollow
        var body = new PetBody(new EntityRef(e, 0), 0, 1);   // the follower and the mover read neither the epoch nor the generation
        var pc = PetControl.Prepare(body.Entity, out why);
        if (pc is null)
        {
            Info($"{tag}: FAIL control refused: {why}");
            Evidence.Write("creature", new JsonObject { ["creature"] = c.Id, ["scale"] = c.Scale, ["refused"] = why });
            Forget(e);
            return;
        }
        Track(pc);
        body.Tag = new Held(pc);
        PetBodyPoll poll = PetBodyPoll.Pending;
        string? pollWhy = null;
        await Until(() => (poll = pc.PollPreparation(ControlRole.PetFollow, out pollWhy)) != PetBodyPoll.Pending, 30);
        why = pollWhy;
        var def = new PetDefinition
        {
            Id = $"{Me}:probe", Creature = c.Id, Level = new PetLevelPolicy { Scale = c.Scale },
            Movement = new PetMovementPolicy { FollowDistance = 1, AllowCatchUp = false },
        };
        GameDataEntry? entry = null;
        try { entry = Content.Templates.Get(info.Template.ToString("D")); } catch (Exception) { }
        var numbers = BodyNumbers(entry);
        var measured = PetClearance.BodyOf(def);   // what Kiln.Pets reads when it checks a profile
        numbers["creature"] = c.Id;
        numbers["scale"] = c.Scale;
        numbers["template"] = entry?.Name;
        numbers["liveScale"] = Out.R(SafeScale(cr));
        numbers["control"] = poll.ToString();
        numbers["controlReason"] = why;
        numbers["ready"] = Has(e, Flag_Ready);
        numbers["grounded"] = Grounded(e);
        numbers["at"] = Out.Pos(Pos(e));
        numbers["kilnBody"] = measured is null ? null : new JsonObject
        {
            ["walkerRadius"] = measured.WalkerRadius, ["walkerHeight"] = measured.WalkerHeight,
            ["colliderWidth"] = measured.ColliderWidth, ["colliderHeight"] = measured.ColliderHeight,
            ["templateScale"] = measured.TemplateScale, ["walker"] = measured.Walker,
        };
        Evidence.Write("body", numbers);
        Info($"{tag}: body {e}: control {poll} ({why ?? "ok"}), ready {Has(e, Flag_Ready)}, grounded {Grounded(e)}, Kiln.Pets reads {measured?.ToString() ?? "no body"}");
        if (poll != PetBodyPoll.Ready)
        {
            Untrack(pc);
            Forget(e);
            return;
        }

        var mover = new GamePetMover();
        foreach (var lane in site.Lanes)
        {
            if (!await Place(e, lane.Start))
            {
                Info($"{tag}: {lane.Lane.Spec.Name}: FAIL the body did not stand at the start");
                Evidence.Write("query", new JsonObject { ["creature"] = c.Id, ["scale"] = c.Scale, ["fixture"] = lane.Lane.Spec.Name, ["error"] = "PlaceFailed" });
                continue;
            }
            var accepted = await Sweep(c, e, lane);
            foreach (var (h, v) in accepted) await Walk(c, def, e, body, pc, mover, lane, h, v);
        }
        if (Plan.Native) await NativeWalks(c, e, body, pc, mover, site);
        Untrack(pc);
        Forget(e);
    }

    /// <summary>A move in one step longer than this is a jump (a teleport), not a walk; at 15 steps a second and a run
    /// of 8 m/s a step is about 0.55 m.</summary>
    private const double JumpMeters = 1.5;

    private static float SafeScale(CreatureRef c)
    {
        try { return c.Scale; } catch (Exception) { return float.NaN; }
    }

    /// <summary>A body's leases as GamePetMover reads them (GamePetWorld's own tag type is private).</summary>
    private sealed class Held(PetControl control) : GamePetWorld.IHasControl
    {
        public PetControl? Control { get; } = control;
    }

    // ---- the sweep ----

    private async Task<List<(double H, double V)>> Sweep(PlanCreature c, EntityId e, WorldLane lane)
    {
        var from = Pos(e);
        var pairs = Plan.Horizontal.SelectMany(h => Plan.Vertical.Select(v => (h, v))).ToList();
        var accepted = new List<(double, double)>();
        var line = new List<string>();
        foreach (var batch in pairs.Chunk(4))
        {
            var asked = new List<((double H, double V) Pair, QueryPathTicket Ticket)>();
            foreach (var p in batch)
            {
                QueryPathTicket t;
                try
                {
                    t = Queries.RequestPath(new QueryPathRequest(e, from, lane.Target)
                    {
                        HorizontalSpace = p.h, VerticalSpace = p.v, MaxIterations = 2000, ClosestFallback = false,
                    });
                }
                catch (Exception x) when (x is ArgumentException or InvalidOperationException or NotSupportedException)
                {
                    Evidence.Write("query", Q(c, lane, p.h, p.v, from) .Add2("error", x.Message));
                    continue;
                }
                CountQuery();
                asked.Add((p, t));
            }
            await Until(() => asked.All(a => a.Ticket.Completion.IsCompleted), 30);
            foreach (var (p, t) in asked)
            {
                var o = Q(c, lane, p.H, p.V, from);
                if (!t.Completion.IsCompleted)
                {
                    try { Queries.CancelPath(t.Request); } catch (Exception) { }
                    o["status"] = "NotCompleted";
                    Evidence.Write("query", o);
                    line.Add($"{Out.F(p.H)}/{Out.F(p.V)} -");
                    continue;
                }
                var r = t.Completion.Result;
                var (route, refusal) = GamePetMover.Accept(r);
                o["ticketOk"] = t.Ok;
                o["ok"] = r.Ok;
                o["status"] = r.Status.ToString();
                o["reaches"] = r.ReachesTarget;
                o["reason"] = r.Reason;
                o["gameResult"] = r.GameResult;
                o["length"] = Out.R(r.Length);
                o["steps"] = r.Steps;
                o["points"] = new JsonArray(r.Points.Select(pt => (JsonNode)new JsonArray(Out.R(pt.Position.X), Out.R(pt.Position.Y), Out.R(pt.Position.Z), pt.RawMove)).ToArray());
                o["accepted"] = route is not null;
                o["refusal"] = refusal;
                Evidence.Write("query", o);
                if (route is not null) accepted.Add(p);
                line.Add($"{Out.F(p.H)}/{Out.F(p.V)} {(route is not null ? "walk" : r.Status.ToString())}");
            }
        }
        Info($"{c.Id} x{Out.F(c.Scale)}: {lane.Lane.Spec.Name}: {string.Join(", ", line)}");
        return accepted;
    }

    private static JsonObject Q(PlanCreature c, WorldLane lane, double h, double v, WorldPosition from) => new()
    {
        ["creature"] = c.Id, ["scale"] = c.Scale, ["fixture"] = lane.Lane.Spec.Name, ["horizontalSpace"] = h, ["verticalSpace"] = v,
        ["from"] = Out.Pos(from), ["to"] = Out.Pos(lane.Target),
    };

    // ---- the real follower's walk ----

    private async Task Walk(PlanCreature c, PetDefinition def, EntityId e, PetBody body, PetControl pc, GamePetMover mover, WorldLane lane, double h, double v)
    {
        var o = Q(c, lane, h, v, Pos(e));
        if (!await Place(e, lane.Start))
        {
            o["outcome"] = "PlaceFailed";
            Evidence.Write("walk", o);
            return;
        }
        CountWalk();
        PetClearance.SetForTests(c.Id, c.Scale, new PetClearanceProfile(h, v));
        var follower = new PetFollower();
        var pet = new PetState { Id = new PetId(Guid.NewGuid()), Body = body, Presence = PetPresence.Live, Life = PetLife.Ready, Order = PetOrder.Follow };
        var owner = new PetOwner(new CharacterKey(1, Guid.Empty), EntityRef.None, lane.Target, 1, 1);
        var trace = new JsonArray();
        var limited = new JsonArray();
        string? last = null;
        var wall = Stopwatch.StartNew();
        var outcome = "Timeout";
        var prev = Pos(e);
        var start = Now;
        var steps0 = StepCount;
        var stillSince = Now;
        var driveSince = Now;
        var jumps = 0;
        // game time bounds the walk; the wall cap only guards a stalled server
        while (Now - start < Plan.WalkSeconds && wall.Elapsed.TotalSeconds < Plan.WalkSeconds * 4 + 10)
        {
            follower.Step(pet, def, owner, mover, Now);
            if (follower.Limited != last)
            {
                last = follower.Limited;
                limited.Add(new JsonArray(Out.R(Now - start), last));
            }
            await Steps(1);
            var p = Pos(e);
            trace.Add(new JsonArray(Out.R(Now - start), Out.R(p.X), Out.R(p.Y), Out.R(p.Z)));
            var moved = p.DistanceTo(prev);
            if (moved > JumpMeters) jumps++;
            if (moved >= 0.01) stillSince = Now;
            prev = p;
            if (pc.CanDrive) driveSince = Now;
            if (Now - driveSince > 3) { outcome = "LostAuthority"; break; }
            if (last is { } l && l.StartsWith("Stuck", StringComparison.Ordinal)) { outcome = "Stuck"; break; }
            if (Flat(p, lane.Target) <= def.Movement.FollowDistance + 0.6 && Now - stillSince >= 0.5) { outcome = "Arrived"; break; }
            if (last is { } r && r.StartsWith("NoRoute", StringComparison.Ordinal) && Now - stillSince >= def.Movement.StuckSeconds) { outcome = "NoRoute"; break; }
        }
        follower.Forget(mover);
        if (pc.CanDrive) mover.Write(body, Vector3.Zero, Vector3.Zero, PetFollower.Stop);
        PetClearance.SetForTests(c.Id, c.Scale, null);
        var end = Pos(e);
        o["outcome"] = outcome;
        o["catchUps"] = follower.CatchUps;
        o["jumps"] = jumps;
        o["wallSeconds"] = Out.R(wall.Elapsed.TotalSeconds);
        o["gameSeconds"] = Out.R(Now - start);
        o["steps"] = StepCount - steps0;
        o["end"] = Out.Pos(end);
        o["limited"] = limited;
        o["trace"] = trace;
        Evidence.Write("walk", o);
        Info($"{c.Id} x{Out.F(c.Scale)}: {lane.Lane.Spec.Name} {Out.F(h)}/{Out.F(v)}: {outcome}, end z {Out.F(end.Z - lane.PassageZ1)} m past the passage, x {Out.F(end.X)} ({Now - start:F1} s, last \"{last}\")");
    }

    // ---- the body's own walker (comparison) ----

    private async Task NativeWalks(PlanCreature c, EntityId e, PetBody body, PetControl pc, GamePetMover mover, Site site)
    {
        if (!pc.SetOrder(PetOrder.Follow, ControlRole.EngineGuard, out var why))
        {
            Info($"{c.Id}: native: EngineGuard refused: {why}");
            Evidence.Write("native", new JsonObject { ["creature"] = c.Id, ["scale"] = c.Scale, ["error"] = why });
            return;
        }
        await Until(() => pc.CanDrive, 15);
        if (!pc.CanDrive)
        {
            Info($"{c.Id}: native: the EngineGuard lease did not become Active ({pc.Effective})");
            Evidence.Write("native", new JsonObject { ["creature"] = c.Id, ["scale"] = c.Scale, ["error"] = "EngineGuardNotActive" });
            return;
        }
        foreach (var lane in site.Lanes)
        {
            var o = Q(c, lane, double.NaN, double.NaN, Pos(e));
            o.Remove("horizontalSpace");
            o.Remove("verticalSpace");
            if (!await Place(e, lane.Start))
            {
                o["outcome"] = "PlaceFailed";
                Evidence.Write("native", o);
                continue;
            }
            EntityId marker;
            try { marker = await Spawn.Entity("Base_LootPickup_Generic_Gravity", lane.Target with { Y = lane.Target.Y + 0.3 }, 0, new SpawnOptions { Tag = Me, Flags = SpawnFlags.Transient }); }
            catch (Exception x) { o["outcome"] = $"NoMarker: {x.Message}"; Evidence.Write("native", o); continue; }
            _spawned.Add(marker);
            await Wait(0.5);
            mover.Guard(body, marker);
            var trace = new JsonArray();
            var walker = new JsonArray();
            string? lastWalker = null;
            var wall = Stopwatch.StartNew();
            var start = Now;
            var outcome = "Timeout";
            var stillSince = Now;
            var prev = Pos(e);
            while (Now - start < Plan.NativeSeconds && wall.Elapsed.TotalSeconds < Plan.NativeSeconds * 4 + 10)
            {
                await Steps(1);
                var p = Pos(e);
                trace.Add(new JsonArray(Out.R(Now - start), Out.R(p.X), Out.R(p.Y), Out.R(p.Z)));
                var w = WalkerState(e);
                if (w != lastWalker)
                {
                    lastWalker = w;
                    walker.Add(new JsonArray(Out.R(Now - start), w));
                }
                if (p.DistanceTo(prev) >= 0.01) stillSince = Now;
                prev = p;
                if (p.Z > lane.PassageZ1 + 1 && Flat(p, lane.Target) <= 4.5 && Now - stillSince >= 1) { outcome = "Arrived"; break; }
            }
            try { Creatures.Of(e).Command.StopGuarding(marker); } catch (Exception) { }
            Forget(marker);
            var end = Pos(e);
            o["outcome"] = outcome;
            o["end"] = Out.Pos(end);
            o["walker"] = walker;
            o["trace"] = trace;
            Evidence.Write("native", o);
            Info($"{c.Id} x{Out.F(c.Scale)}: native {lane.Lane.Spec.Name}: {outcome}, end z {Out.F(end.Z - lane.PassageZ1)} m past the passage (walker {lastWalker})");
        }
        pc.SetOrder(PetOrder.Follow, ControlRole.PetFollow, out _);
        await Until(() => pc.CanDrive, 15);
    }

    /// <summary>DynamicPathWalker's state (+0) and its last path result (+0x24), as "state/result".</summary>
    private static string WalkerState(EntityId e)
    {
        var b = World.ReadBytes(e, "DynamicPathWalker");
        if (b is null || b.Length < 0x28) return "none";
        return $"{BitConverter.ToUInt32(b, 0)}/{b[0x24]}";
    }
}

internal static class JsonExt
{
    public static JsonObject Add2(this JsonObject o, string key, JsonNode? value)
    {
        o[key] = value;
        return o;
    }
}
