// SPDX-License-Identifier: MIT
// kiln.queries lab test, on a dedicated server with no player. Prints "queries-test: PASS ..." / "FAIL ..." / "INFO ..."
// and ends with "queries-test: DONE pass=<n> fail=<n>". It works on the flat plateau build-test uses, 790 m from the lab
// world's only base (3024, ~903, 1548): the walls it writes are transient, outside bases, checked for success before a ray
// counts, and taken away again.
// Rays are spread over steps: the service admits 64 per step for everyone.
using Kiln;

namespace QueriesTest;

public sealed class QueriesTestMod : Mod
{
    private const double X = 3024.25, Z = 1548.25;
    private const int PerStep = 50, Wanted = 1000;
    private int _pass, _fail;

    public override void OnLoad() => _ = Run();

    private void Check(bool ok, string name, string detail)
    {
        if (ok) { _pass++; Context.Log.Info($"queries-test: PASS {name} {detail}"); }
        else { _fail++; Context.Log.Warn($"queries-test: FAIL {name} {detail}"); }
    }

    private void Info(string m) => Context.Log.Info($"queries-test: INFO {m}");

    private async Task Next() => await Context.Scheduler.NextTick();

    private async Task Run()
    {
        try
        {
            for (var i = 0; i < 24 * 300 && !(Spawn.Available && Spawn.WorldRunning && Build.Available && Build.Grid is not null); i++) await Next();
            for (var i = 0; i < 48; i++) await Next();
            Info($"rows: kiln.queries={Capabilities.Is("kiln.queries")} voxel_ray={Capabilities.Is("queries.voxel_ray")} ground={Capabilities.Is("queries.ground")} " +
                 $"line_of_sight={Capabilities.Is("queries.line_of_sight")} overlap_origins={Capabilities.Is("queries.overlap_origins")}");
            if (!Capabilities.Is("queries.voxel_ray")) { Check(false, "queries.rows", Capabilities.Why("queries.voxel_ray")); return; }

            await NotLoaded();
            var ground = await Ground();
            if (ground is not { } y) return;
            await Air(y);
            await Wall(y, Voxel.Of(1), "queries.ray.wall.terrain");
            await Wall(y, Voxel.Of(0x81), "queries.ray.wall.building");
            await Arguments(y);
            await Budget(y);
            await Overlap(y);
            await Paths(y);
        }
        catch (Exception e)
        {
            Check(false, "queries.run", e.ToString());
        }
        finally
        {
            Context.Log.Info($"queries-test: DONE pass={_pass} fail={_fail}");
        }
    }

    // Before anything asked for it, far from every player and base: not loaded is a refusal, never "clear".
    private async Task NotLoaded()
    {
        var from = new WorldPosition(11000, 1500, 11000);
        var to = new WorldPosition(11000, 1300, 11000);
        int refused = 0, clear = 0, hits = 0;
        string? reason = null;
        for (var i = 0; i < 100; i++)
        {
            if (i % PerStep == 0) await Next();
            var r = Queries.RaycastVoxels(from, to);
            if (!r.Ok) { refused++; reason = r.Reason; }
            else if (r.Hit == null) clear++;
            else hits++;
        }
        Check(refused == 100 && reason == "NotLoaded", "queries.ray.not-loaded", $"n=100 refused={refused} reason={reason} clear={clear} hits={hits}");
    }

    // One answer: a ray that is refused for the step's budget or for data that is still loading is asked again in a later
    // step (at most 20 s). The ray's own cell cache catches up some steps after Build.Load or a write.
    private async Task<VoxelRayResult> One(Func<VoxelRayResult> ray, Func<VoxelRayResult, bool>? until = null)
    {
        var r = ray();
        for (var i = 0; i < 24 * 20 && ((!r.Ok && r.Reason is "BudgetExceeded" or "NotLoaded") || (r.Ok && until != null && !until(r))); i++)
        {
            await Next();
            r = ray();
        }
        return r;
    }

    // `count` answered rays, spread over steps. A ray the budget refuses is not an answer: it is asked again in the next step
    // (the first rays into cold cells are slow, and the budget is also time). Budget counts those refusals.
    private async Task<(int Ok, int Hits, int Refused, string? Reason, VoxelRayHit? Last, int Steps, int Budget)> Many(Func<VoxelRayResult> ray, int count)
    {
        await One(ray);
        int ok = 0, hits = 0, refused = 0, steps = 0, budget = 0, inStep = PerStep;
        string? reason = null;
        VoxelRayHit? last = null;
        while (ok + refused < count && steps < 24 * 60)
        {
            if (inStep >= PerStep) { await Next(); steps++; inStep = 0; }
            inStep++;
            var r = ray();
            if (!r.Ok && r.Reason == "BudgetExceeded") { budget++; inStep = PerStep; continue; }
            if (!r.Ok) { refused++; reason = r.Reason; continue; }
            ok++;
            if (r.Hit != null) { hits++; last = r.Hit; }
        }
        return (ok, hits, refused, reason, last, steps, budget);
    }

    private async Task<double?> Ground()
    {
        var area = Build.BoxAround(new WorldPosition(X, 900, Z), 12, 60, 12);
        var loaded = await Build.Load(area, TimeSpan.FromSeconds(60));
        if (!loaded) { Check(false, "queries.ray.ground", "Build.Load timed out"); return null; }
        var from = new WorldPosition(X, 960, Z);
        var r = await Many(() => Queries.GroundBelow(from, 120), Wanted);
        var p = r.Last?.Position ?? default;
        var finite = r.Last != null && double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z) && r.Last.Material != 0;
        Check(r.Ok == Wanted && r.Hits == Wanted && finite, "queries.ray.ground",
            $"n={Wanted} ok={r.Ok} hits={r.Hits} refused={r.Refused} reason={r.Reason} budget-waits={r.Budget} pos={p} y={p.Y:F4} dist={r.Last?.Distance:F4} mat={r.Last?.Material:x8} voxel={r.Last?.Voxel:x4} steps={r.Steps}");
        if (r.Last == null) return null;
        // upward from below the surface to above it: the same surface from the other side, or the ground's underside
        var up = await One(() => Queries.RaycastVoxels(new WorldPosition(X, p.Y + 30, Z), new WorldPosition(X, p.Y + 0.5, Z)));
        Check(up.Ok && up.Hit == null, "queries.ray.above-ground", $"30 m above to 0.5 m above the surface: ok={up.Ok} hit={up.Hit != null} reason={up.Reason}");
        return p.Y;
    }

    private async Task Air(double ground)
    {
        var a = new WorldPosition(X, ground + 20, Z);
        var b = new WorldPosition(X + 10, ground + 20, Z);
        var r = await Many(() => Queries.RaycastVoxels(a, b), Wanted);
        Check(r.Ok == Wanted && r.Hits == 0, "queries.ray.air", $"n={Wanted} ok={r.Ok} hits={r.Hits} refused={r.Refused} reason={r.Reason} budget-waits={r.Budget} steps={r.Steps}");
    }

    private async Task Wall(double ground, Voxel voxel, string name)
    {
        var a = new WorldPosition(X, ground + 20, Z);
        var b = new WorldPosition(X + 10, ground + 20, Z);
        // 2 cells thick, 12 high, 6 wide, across the ray, 2 m along it
        var corner = Build.CellOf(new WorldPosition(X + 2, ground + 18, Z - 1.5));
        var box = new CellBox(corner.X, corner.Y, corner.Z, 2, 12, 6);
        var before = Build.Read(box);
        var options = new BuildOptions { Flags = BuildFlags.Transient, Journal = false };
        var write = Build.Write(box, Enumerable.Repeat(voxel, (int)box.Count).ToArray(), options);
        if (!(write.Changed && write.Verified))
        {
            Check(false, name, $"the wall was not written (changed={write.Changed} verified={write.Verified} cells={write.CellsChanged}): no ray counts");
            return;
        }
        // the ray's cache sees the write some steps later: wait for the first hit
        await One(() => Queries.RaycastVoxels(a, b), x => x.Hit != null);
        var r = await Many(() => Queries.RaycastVoxels(a, b), Wanted);
        var wallX = Build.CornerOf(corner).X;
        var hitX = r.Last?.Position.X ?? double.NaN;
        Check(r.Ok == Wanted && r.Hits == Wanted && Math.Abs(hitX - wallX) < 0.05 && r.Last!.Voxel == voxel.Raw, name,
            $"n={Wanted} ok={r.Ok} hits={r.Hits} refused={r.Refused} reason={r.Reason} hitX={hitX:F4} wallX={wallX:F4} dist={r.Last?.Distance:F4} mat={r.Last?.Material:x8} voxel={r.Last?.Voxel:x4} write={write.CellsChanged}");
        // from the other side the wall's far face is the first surface
        var back = await One(() => Queries.RaycastVoxels(b, a));
        Check(back is { Ok: true, Hit: not null } && back.Hit.Position.X > hitX + 0.5, name + ".reverse", $"ok={back.Ok} reason={back.Reason} hitX={back.Hit?.Position.X:F4} (the wall's far face)");
        // the wall goes, and the same ray is clear again
        var undo = Build.Write(box, before, options);
        var clear = await One(() => Queries.RaycastVoxels(a, b), x => x.Hit == null) is { Ok: true, Hit: null };
        Check(undo.Verified && clear, name + ".removed", $"restored={undo.CellsChanged} cells verified={undo.Verified} clear={clear}");
    }

    private async Task Arguments(double ground)
    {
        await Next();
        var a = new WorldPosition(X, ground + 20, Z);
        var threw = 0;
        void Throws(Action call) { try { call(); } catch (ArgumentException) { threw++; } }
        Throws(() => Queries.RaycastVoxels(a, a));
        Throws(() => Queries.RaycastVoxels(a, new WorldPosition(X + 300, ground + 20, Z)));
        Throws(() => Queries.RaycastVoxels(a, new WorldPosition(double.NaN, 0, 0)));
        Throws(() => Queries.RaycastVoxels(new WorldPosition(1000, 0, 0), new WorldPosition(1000.000001, 0, 0)));
        Throws(() => Queries.RaycastVoxels(a, new WorldPosition(X + 1, ground + 20, Z), realm: 200));   // no such realm
        Throws(() => Queries.GroundBelow(a, 0));
        Check(threw == 6, "queries.ray.arguments", $"{threw} of 6 bad rays threw ArgumentException");

        // outside the realm's grid on each side, in both directions: refused before the game is asked, never clear
        var outside = 0;
        var reasons = new HashSet<string?>();
        var far = new[]
        {
            new WorldPosition(-1e6, ground, Z), new WorldPosition(1e6, ground, Z), new WorldPosition(X, -1e6, Z),
            new WorldPosition(X, 1e6, Z), new WorldPosition(X, ground, -1e6), new WorldPosition(X, ground, 1e6),
        };
        foreach (var p in far)
        {
            var near = new WorldPosition(p.X + (Math.Abs(p.X) > 1e5 ? Math.Sign(p.X) * -10 : 0), p.Y + (Math.Abs(p.Y) > 1e5 ? Math.Sign(p.Y) * -10 : 0), p.Z + (Math.Abs(p.Z) > 1e5 ? Math.Sign(p.Z) * -10 : 0));
            foreach (var r in new[] { Queries.RaycastVoxels(p, near), Queries.RaycastVoxels(near, p) })
            {
                reasons.Add(r.Reason);
                if (!r.Ok && r.Reason == "OutOfGrid") outside++;
            }
        }
        Check(outside == 12, "queries.ray.out-of-grid", $"{outside} of 12 rays outside the grid refused; reasons: {string.Join(",", reasons)}");
    }

    private async Task Budget(double ground)
    {
        await Next();
        var a = new WorldPosition(X, ground + 20, Z);
        var b = new WorldPosition(X + 10, ground + 20, Z);
        int ok = 0, over = 0, other = 0;
        for (var i = 0; i < 100; i++)
        {
            var r = Queries.RaycastVoxels(a, b);
            if (r.Ok) ok++;
            else if (r.Reason == "BudgetExceeded") over++;
            else other++;
        }
        var ground2 = Queries.GroundBelow(a).Reason;   // ground shares the budget
        await Next();
        var again = Queries.RaycastVoxels(a, b);
        // 64 by count, or fewer when the 500 microseconds ran out first; never more, and the next step starts again
        Check(ok is > 0 and <= 64 && ok + over == 100 && other == 0 && ground2 == "BudgetExceeded" && again.Ok, "queries.budget",
            $"100 rays in one step: {ok} admitted, {over} BudgetExceeded, {other} other; ground in the same step: {ground2}; next step ok={again.Ok}");
    }

    private async Task Overlap(double ground)
    {
        var at = new WorldPosition(X + 4, ground + 1, Z);
        var one = await Spawn.Entity("Base_LootPickup_Generic_Gravity", at);
        var two = await Spawn.Entity("Base_LootPickup_Generic_Gravity", new WorldPosition(X + 6, ground + 1, Z));
        for (var i = 0; i < 12; i++) await Next();
        if (!World.TryGetPosition(one, out var p1) || !World.TryGetPosition(two, out var p2)) { Check(false, "queries.overlap", "the test pickups have no position"); return; }
        var both = Queries.EntitiesInSphere(p1, p1.DistanceTo(p2) + 0.01);
        var onlyOne = Queries.EntitiesInSphere(p1, 0.01);
        var without = Queries.EntitiesInSphere(p1, p1.DistanceTo(p2) + 0.01, exclude: one);
        var box = Queries.EntitiesInBox(p1, new WorldPosition(0.01, 0.01, 0.01));
        var cut = Queries.EntitiesInSphere(p1, 5000, max: 1);
        var i1 = both.Entities.ToList().IndexOf(one);
        var i2 = both.Entities.ToList().IndexOf(two);
        Check(both.Ok && i1 >= 0 && i2 > i1 && onlyOne.Entities.Contains(one) && !onlyOne.Entities.Contains(two) && !without.Entities.Contains(one) && without.Entities.Contains(two)
              && box.Entities.Contains(one) && !box.Entities.Contains(two) && cut.Entities.Count == 1 && cut.Truncated && cut.Total > 1, "queries.overlap",
            $"sphere over both: {both.Total} (one at {i1}, two at {i2}); r=0.01: {onlyOne.Total}; excluding one: {without.Total}; box: {box.Total}; 5 km max 1: total {cut.Total} truncated {cut.Truncated}");

        var sight = Queries.VoxelLineOfSight(one, two);
        for (var i = 0; i < 24 * 20 && !sight.Ok && sight.Reason is "NotLoaded" or "BudgetExceeded"; i++)
        {
            await Next();
            sight = Queries.VoxelLineOfSight(one, two);
        }
        Check(sight.Ok && sight.Visible, "queries.sight.open", $"ok={sight.Ok} visible={sight.Visible} reason={sight.Reason}");
        Spawn.Destroy(two);
        for (var i = 0; i < 6; i++) await Next();
        var gone = Queries.VoxelLineOfSight(one, two);
        Check(!gone.Ok && gone.Reason == "EntityGone" && !gone.Visible, "queries.sight.entity-gone", $"ok={gone.Ok} reason={gone.Reason}");
        Spawn.Destroy(one);
    }

    // ---- paths (02): the game's own path search, with a pickup as the anchor and no player --------------------------------

    private async Task<QueryPathResult> Ended(Task<QueryPathResult> task, int steps = 24 * 15)
    {
        for (var i = 0; i < steps && !task.IsCompleted; i++) await Next();
        return task.IsCompleted ? task.Result : new QueryPathResult(false, "TestGaveUp", 0, 0, QueryPathStatus.Pending, null, false, [], 0, 0);
    }

    private static string Show(QueryPathResult r) =>
        $"status={r.Status} ok={r.Ok} reason={r.Reason} game={r.GameResult} reaches={r.ReachesTarget} points={r.Points.Count} length={r.Length:F2} steps={r.Steps}";

    private async Task Paths(double ground)
    {
        Info($"rows: queries.path={Capabilities.Is("queries.path")} queries.reachability={Capabilities.Is("queries.reachability")}");
        if (!Capabilities.Is("queries.path")) { Check(false, "queries.path.rows", Capabilities.Why("queries.path")); return; }
        var from = new WorldPosition(X, ground + 0.5, Z);
        var anchor = await Spawn.Entity("Base_LootPickup_Generic_Gravity", from);
        for (var i = 0; i < 24; i++) await Next();
        if (!World.TryGetPosition(anchor, out var at)) { Check(false, "queries.path.short", "the anchor pickup has no position"); return; }
        var to = new WorldPosition(at.X + 3, at.Y, at.Z);

        // a short walk: found, with points, the states in order, the event of the end before the task
        var states = new List<string>();
        var endSeenBeforeTask = false;
        QueryPathTicket? ticket = null;
        using (Events.On<PathQueryChangedEvent>(e =>
               {
                   if (ticket == null || e.Request != ticket.Request) return;
                   states.Add($"{e.Before}->{e.After}");
                   if (e.TerminalStatus != null) endSeenBeforeTask = !ticket.Completion.IsCompleted;
               }))
        {
            ticket = Queries.RequestPath(new QueryPathRequest(anchor, at, to));
            var r = await Ended(ticket.Completion);
            Check(ticket.Ok && r.Status == QueryPathStatus.Found && r.ReachesTarget && r.Points.Count >= 2 && r.Points.All(p => p.Move == QueryPathMove.Walk) && r.GameResult == 1,
                "queries.path.short", $"{Show(r)} first={r.Points.FirstOrDefault()?.Position} last={r.Points.LastOrDefault()?.Position}");
            Check(string.Join(",", states) == "Created->Queued,Queued->Searching,Searching->Completed" && endSeenBeforeTask, "queries.path.states",
                $"{string.Join(",", states)}; the end's event came before the task: {endSeenBeforeTask}");
            // the points are a copy: the game's query is destroyed by now, and they still read the same some steps later
            var copy = r.Points.Select(p => p.Position).ToList();
            for (var i = 0; i < 24; i++) await Next();
            Check(copy.SequenceEqual(r.Points.Select(p => p.Position)) && copy.All(p => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z)), "queries.path.copy-after-destroy",
                $"{copy.Count} points unchanged 24 steps after the result");
        }

        // numbers that are no request, and what is refused as a ticket
        var threw = 0;
        void Throws(Action call) { try { call(); } catch (ArgumentException) { threw++; } }
        Throws(() => Queries.RequestPath(new QueryPathRequest(anchor, at, new WorldPosition(at.X + 200.5, at.Y, at.Z))));
        Throws(() => Queries.RequestPath(new QueryPathRequest(default, at, to)));
        Throws(() => Queries.RequestPath(new QueryPathRequest(anchor, at, to) { MaxIterations = 0 }));
        Check(threw == 3, "queries.path.arguments", $"{threw} of 3 bad requests threw ArgumentException");
        var far = Queries.RequestPath(new QueryPathRequest(anchor, new WorldPosition(-1e6, at.Y, at.Z), new WorldPosition(-1e6 + 3, at.Y, at.Z)));
        Check(!far.Ok && far.Completion.IsCompleted && far.Completion.Result.Status == QueryPathStatus.OutOfGrid && far.Request == 0, "queries.path.out-of-grid",
            $"ok={far.Ok} reason={far.Reason} status={far.Completion.Result.Status}");

        // a target the search cannot stand on (30 m up in the air): never "found", and an empty answer is not success
        var air = await Ended(Queries.FindPath(new QueryPathRequest(anchor, at, new WorldPosition(at.X + 3, at.Y + 30, at.Z))));
        Check(!air.ReachesTarget && air.Status != QueryPathStatus.Found && air.Status != QueryPathStatus.Pending && (air.Ok || air.Points.Count == 0), "queries.path.empty-not-success", Show(air));
        var reachAir = Queries.CanReach(new QueryPathRequest(anchor, at, new WorldPosition(at.X + 3, at.Y + 30, at.Z)));
        for (var i = 0; i < 24 * 15 && !reachAir.IsCompleted; i++) await Next();
        var reachGround = Queries.CanReach(new QueryPathRequest(anchor, at, to));
        for (var i = 0; i < 24 * 15 && !reachGround.IsCompleted; i++) await Next();
        Check(reachGround.IsCompleted && reachGround.Result is { Ok: true, Reachable: true } && reachAir.IsCompleted && !reachAir.Result.Reachable, "queries.path.can-reach",
            $"ground: ok={reachGround.Result.Ok} reachable={reachGround.Result.Reachable}; air: ok={reachAir.Result.Ok} reachable={reachAir.Result.Reachable} reason={reachAir.Result.Reason} status={reachAir.Result.Status}");

        // the closest-node fallback ends somewhere: partial or found, never a refusal by itself
        var fallback = await Ended(Queries.FindPath(new QueryPathRequest(anchor, at, new WorldPosition(at.X + 3, at.Y + 30, at.Z)) { ClosestFallback = true }));
        Info($"closest fallback to a point in the air: {Show(fallback)}");

        // an iteration limit of 1 on a longer walk
        var limited = await Ended(Queries.FindPath(new QueryPathRequest(anchor, at, new WorldPosition(at.X + 9, at.Y, at.Z + 6)) { MaxIterations = 1 }));
        Check(limited.Status is QueryPathStatus.IterationLimit or QueryPathStatus.Found or QueryPathStatus.Partial, "queries.path.iteration-limit", Show(limited));

        // cancel: one end, as Cancelled; a second cancel finds nothing
        var cancelled = Queries.RequestPath(new QueryPathRequest(anchor, at, new WorldPosition(at.X + 9, at.Y, at.Z + 6)));
        var c1 = Queries.CancelPath(cancelled.Request);
        var c2 = Queries.CancelPath(cancelled.Request);
        var cr = await Ended(cancelled.Completion);
        Check(c1.Ok && !c2.Ok && c2.Reason == "UnknownRequest" && cr.Status == QueryPathStatus.Cancelled, "queries.path.cancel-once", $"first={c1.Ok} second={c2.Ok}/{c2.Reason} end={cr.Status}");

        // eight at once are admitted, the ninth is refused QueueFull; all eight end, and the slots come back
        var many = Enumerable.Range(0, 9).Select(i => Queries.RequestPath(new QueryPathRequest(anchor, at, new WorldPosition(at.X + 2 + i * 0.5, at.Y, at.Z)))).ToList();
        var admitted = many.Count(t => t.Ok);
        var ninth = many[8];
        var ends = new List<QueryPathResult>();
        foreach (var t in many.Take(8)) ends.Add(await Ended(t.Completion));
        var again = Queries.RequestPath(new QueryPathRequest(anchor, at, to));
        var againEnd = await Ended(again.Completion);
        Check(admitted == 8 && !ninth.Ok && ninth.Reason == "QueueFull" && ends.All(e => e.Status != QueryPathStatus.Pending) && again.Ok && againEnd.Status == QueryPathStatus.Found, "queries.path.queue-full",
            $"{admitted} of 9 admitted, the ninth: {ninth.Reason}; ends: {string.Join(",", ends.Select(e => e.Status))}; after them: {againEnd.Status}");

        // the anchor goes while a request waits
        var doomed = await Spawn.Entity("Base_LootPickup_Generic_Gravity", from);
        for (var i = 0; i < 12; i++) await Next();
        var orphan = Queries.RequestPath(new QueryPathRequest(doomed, at, new WorldPosition(at.X + 9, at.Y, at.Z + 6)));
        Spawn.Destroy(doomed);
        var gone = await Ended(orphan.Completion);
        Check(orphan.Ok && gone.Status is QueryPathStatus.OwnerGone or QueryPathStatus.Found or QueryPathStatus.Partial, "queries.path.owner-gone", Show(gone));

        // everything this test asked for has ended and left nothing open: a fresh batch of eight is admitted
        for (var i = 0; i < 24; i++) await Next();
        var fresh = Enumerable.Range(0, 8).Select(_ => Queries.RequestPath(new QueryPathRequest(anchor, at, to))).ToList();
        var freshEnds = new List<QueryPathResult>();
        foreach (var t in fresh) freshEnds.Add(await Ended(t.Completion));
        Check(fresh.All(t => t.Ok) && freshEnds.All(e => e.Status == QueryPathStatus.Found), "queries.path.cleanup-baseline", $"8 of 8 admitted and found after the earlier requests: {fresh.Count(t => t.Ok)}/{freshEnds.Count(e => e.Status == QueryPathStatus.Found)}");
        Spawn.Destroy(anchor);
    }
}
