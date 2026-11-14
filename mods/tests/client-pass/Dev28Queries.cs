// SPDX-License-Identifier: MIT
using Kiln;
using Kiln.Testing;
using static Kiln.Testing.ClientCheckTags;

namespace ClientPass;

// 01-world-queries (chapter 01): Kiln.Queries in the server world of a game that hosts. The four queries.* rows are held on
// a game client until 01-queries.voxel-ray-hosted passes (native/core/gates/dev28-01-world-queries.json). Orders 2000-2099.
public sealed partial class ClientPassMod
{
    private const string QueriesArea = "World queries";

    // The player's position, or a failed expectation.
    private static bool QueriesPlayerAt(CheckContext c, out EntityId entity, out WorldPosition at)
    {
        entity = default;
        at = default;
        var player = Players.All.FirstOrDefault(p => !p.Entity.IsNone);
        var ok = player != null && World.TryGetPosition(player.Entity, out at);
        if (ok) entity = player!.Entity;
        c.Expect(ok, "a player with a position is in the hosted world");
        return ok;
    }

    [ClientCheck("01-queries.voxel-ray-hosted", "Ground, wall and query-only safety", Area = QueriesArea, Since = Dev28,
        Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = 2000)]
    public async Task QueriesVoxelRayHosted(CheckContext c)
    {
        if (!await NeedOpen(c, "queries.voxel_ray", "queries.ground", "queries.line_of_sight", "queries.overlap_origins")) return;
        c.Instruct("Host a disposable world and stand on flat, natural ground well away from any base (a Flame Altar's area " +
                   "refuses the test wall). Stand still, then press continue.");
        await c.Verdict();
        if (!QueriesPlayerAt(c, out var me, out var at)) return;

        // ground under the feet: asked again over a few steps while the ray's cells load
        VoxelRayResult ground = new(false, "NotAsked", null);
        for (var i = 0; i < 240 && !ground.Ok; i++)
        {
            ground = Queries.GroundBelow(new WorldPosition(at.X, at.Y + 2, at.Z), 20);
            if (!ground.Ok) await c.Delay(TimeSpan.FromMilliseconds(50));
        }
        c.Expect(ground is { Ok: true, Hit: not null }, $"GroundBelow found the ground under the player (ok={ground.Ok}, reason={ground.Reason})");
        if (ground.Hit == null) return;
        var foot = Math.Abs(ground.Hit.Position.Y - at.Y);
        c.Expect(foot <= 0.1, $"the ground is within 0.1 m of the player's feet (off by {foot:F3} m; ground {ground.Hit.Position.Y:F3}, player {at.Y:F3})");

        // a wall of terrain voxels 4 m in front (world +X), written transient and checked before a ray counts
        var a = new WorldPosition(at.X, at.Y + 1.5, at.Z);
        var b = new WorldPosition(at.X + 8, at.Y + 1.5, at.Z);
        var corner = Build.CellOf(new WorldPosition(at.X + 4, at.Y, at.Z - 1.5));
        var box = new CellBox(corner.X, corner.Y, corner.Z, 2, 8, 6);
        var loaded = await Build.Load(box, TimeSpan.FromSeconds(30));
        c.Expect(loaded, "the voxels around the test wall are loaded");
        if (!loaded) return;
        var before = Build.Read(box);
        var options = new BuildOptions { Flags = BuildFlags.Transient, Journal = false };
        var open = await QueriesAsk(c, () => Queries.RaycastVoxels(a, b), _ => true);
        c.Expect(open is { Ok: true, Hit: null }, $"before the wall the 8 m ray is loaded and clear (ok={open.Ok}, hit={open.Hit != null}, reason={open.Reason})");
        try
        {
            // A failed verification can follow a partial write. Restore the snapshot on that path too.
            var write = Build.Write(box, Enumerable.Repeat(Voxel.Of(1), (int)box.Count).ToArray(), options);
            c.Expect(write.Changed && write.Verified, $"the test wall was written ({write.CellsChanged} cells, refusal {write.Refusal}): without it no wall ray counts");
            if (!(write.Changed && write.Verified)) return;
            var hit = await QueriesAsk(c, () => Queries.RaycastVoxels(a, b), r => r.Hit != null);
            c.Expect(hit is { Ok: true, Hit: not null }, $"the ray stops at the wall (ok={hit.Ok}, reason={hit.Reason}, at {hit.Hit?.Position})");
            c.Instruct("A wall of earth, 1 m thick and 4 m high, now stands 4 m east of you (it is taken away again after this " +
                       "check). Nothing else should have changed: no stutter, no effect, nothing drawn where the rays went. " +
                       "Is the wall there, and is the game otherwise as before?");
            await c.Verdict();
        }
        finally
        {
            var undo = Build.Write(box, before, options);
            c.Expect(undo.Verified, "the test wall was taken away again");
        }
        var clear = await QueriesAsk(c, () => Queries.RaycastVoxels(a, b), r => r.Hit == null);
        c.Expect(clear is { Ok: true, Hit: null }, $"after the wall is gone the same ray is clear (ok={clear.Ok}, hit={clear.Hit != null}, reason={clear.Reason})");
        var near = Queries.EntitiesInSphere(at, 0.5);
        c.Expect(near.Ok && near.Entities.Contains(me), $"EntitiesInSphere around the player finds the player (ok={near.Ok}, {near.Total} found)");
    }

    // A ray asked again in later steps while it is refused for loading or budget, or until `done` (at most 10 s).
    private static async Task<VoxelRayResult> QueriesAsk(CheckContext c, Func<VoxelRayResult> ray, Func<VoxelRayResult, bool> done)
    {
        var r = ray();
        for (var i = 0; i < 240 && (!r.Ok || !done(r)); i++)
        {
            await c.Delay(TimeSpan.FromMilliseconds(50));
            r = ray();
        }
        return r;
    }

    [ClientCheck("01-queries.voxel-only-prop", "Props are not voxel hits", Area = QueriesArea, Since = Dev28,
        Needs = ClientCheckNeeds.Hosted, Order = 2001)]
    public async Task QueriesVoxelOnlyProp(CheckContext c)
    {
        if (!await NeedOpen(c, "queries.voxel_ray")) return;
        c.Instruct("Place a chest (or stand so that a large creature is) directly east of you, 2 to 4 m away, with nothing else " +
                   "between you and it and open ground behind it. Stand still, then press continue.");
        await c.Verdict();
        if (!QueriesPlayerAt(c, out _, out var at)) return;
        var a = new WorldPosition(at.X, at.Y + 0.6, at.Z);
        var b = new WorldPosition(at.X + 6, at.Y + 0.6, at.Z);
        var r = await QueriesAsk(c, () => Queries.RaycastVoxels(a, b), _ => true);
        c.Log.Info($"client-pass: {c.Id}: voxel-only ray through the prop: ok={r.Ok} hit={r.Hit != null} reason={r.Reason}");
        c.Expect(r is { Ok: true, Hit: null }, $"the voxel ray passes the prop: a voxel ray never reports a prop or a creature (ok={r.Ok}, hit={r.Hit != null}, reason={r.Reason})");
        c.Instruct("The ray went from you 6 m east at knee height and reported no hit. Was the chest (or creature) really in that " +
                   "line, with no terrain or building block in it?");
        await c.Verdict();
    }

    [ClientCheck("01-los-eye-height", "Short and tall creatures at low walls", Area = QueriesArea, Since = Dev28,
        Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = 2002)]
    public async Task QueriesLosEyeHeight(CheckContext c)
    {
        if (!await NeedOpen(c, "queries.line_of_sight")) return;
        c.Instruct("Find a creature standing behind a low wall or ledge that hides its legs but not its head, 5 to 20 m from you, " +
                   "and look at it. Stand still, then press continue. (This check records numbers; it opens no capability.)");
        await c.Verdict();
        if (!QueriesPlayerAt(c, out var me, out var at)) return;
        var target = Queries.EntitiesInSphere(at, 25, exclude: me).Entities.FirstOrDefault(e => Creatures.Available && Creatures.Of(e).Inspect() is { HealthMax: > 0 });
        c.Expect(!target.IsNone, "a creature is within 25 m");
        if (target.IsNone) return;
        foreach (var height in new[] { 0.3, 0.8, 1.5, 2.2 })
        {
            var r = Queries.VoxelLineOfSight(me, target, new VoxelSightOptions { ToOffset = new WorldPosition(0, height, 0) });
            c.Log.Info($"client-pass: {c.Id}: to {target} at +{height} m: ok={r.Ok} visible={r.Visible} reason={r.Reason} obstruction={r.Obstruction?.Position}");
            await c.Delay(TimeSpan.FromMilliseconds(50));
        }
        c.Instruct("The log now has four lines for that creature: line of sight to 0.3, 0.8, 1.5 and 2.2 m above its origin. " +
                   "Do the low ones read not visible and the ones at its head visible, as you see it?");
        await c.Verdict();
    }

    [ClientCheck("01-queries.brain-perception-opt-in", "A creature brain that opts in to line-of-sight perception", Area = QueriesArea, Since = Dev28,
        Needs = ClientCheckNeeds.Hosted, Order = 2003)]
    public async Task QueriesBrainPerceptionOptIn(CheckContext c)
    {
        if (!await Need(c, "queries-brain-test") || !await NeedOpen(c, "queries.line_of_sight")) return;
        // the fixture mod (mods/tests/queries-brain-test) spawns the two brains and the wall and writes its own PASS lines
        c.Instruct("The fixture has spawned two still creatures with brains 6 m apart from a target, one with the default sight and " +
                   "one with voxel sight, and a wall between them and the target. Read kiln.log for the lines " +
                   "\"queries-brain-test: PASS\". Are all of its lines PASS, and did the creatures stand where the wall is?");
        await c.Verdict();
    }
}
