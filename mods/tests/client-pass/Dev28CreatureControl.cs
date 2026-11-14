// SPDX-License-Identifier: MIT
using Kiln;
using Kiln.Testing;
using static Kiln.Testing.ClientCheckTags;

namespace ClientPass;

// 19d-creature-control: the movement broker kiln.creature_control as a player sees it. The fixture is the sample
// mods/samples/CreatureControlSample (mod id creature-control-sample): its pacer is a creature block whose brain walks its
// body under the lease Kiln.Blocks takes for it (the broker holds the game's AI off and removes the SteeringAgent whose
// program overwrote every input write). Orders 6000-6099 (2000 + (28400 - 28000) * 10).
//
// The dossier's 19d-c1 to 19d-c6 are 19d-pets' (dev28-releases.json "owners"); 19d-c2 reads this package's lease log lines.
public sealed partial class ClientPassMod
{
    private const string CreatureControlSample = "creature-control-sample";

    [ClientCheck("19d-brain-walks", "A creature with a Kiln.Blocks brain walks to you and stops", Area = "Creature control", Since = Dev28,
        Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = 6000)]
    public async Task CreatureControlBrainWalks(CheckContext c)
    {
        if (!await Need(c, CreatureControlSample) || !await NeedPlayer(c)) return;
        c.Expect(Capabilities.Is("creature_control"), "creature_control is available here", Capabilities.Why("creature_control"));
        c.Instruct("Host a disposable world and stand on open, flat ground away from any base, with 15 m of clear ground in front of " +
                   "you. Stand still: in a few seconds a Vukah (the Pacer) appears about 12 m ahead and walks to you. Do not move " +
                   "until this text changes.");
        await c.Delay(TimeSpan.FromSeconds(8));
        if (Ahead(c, 12) is not { } at)
        {
            c.Expect(false, "the player's place is known");
            return;
        }
        var e = await SpawnHere(c, $"{CreatureControlSample}:pacer", at, 180);
        if (e.IsNone) return;
        // the lease drives: the broker removed the body's SteeringAgent (the player is near, so the body gets ready)
        if (!await c.Until(() => World.Exists(e) && !World.Has(e, "SteeringAgent"), TimeSpan.FromSeconds(15),
                "the broker took the Pacer's body (its SteeringAgent removed)")) return;

        var (start, path, jump, end, took) = await CreatureControlFollow(c, e);
        c.Log.Info($"client-pass: {c.Id}: first walk: from {start:0.00} m, walked {path:0.00} m in {took:0.0} s, longest step {jump:0.00} m, stopped {end:0.00} m from the player");
        c.Expect(start >= 8, $"the Pacer started at least 8 m away ({start:0.00} m)");
        c.Expect(path >= 5, $"it walked at least 5 m ({path:0.00} m in {took:0.0} s)");
        c.Expect(jump < 1, $"no jump in one step: it walked, it was not moved there ({jump:0.00} m at most)");
        c.Expect(end is >= 2 and <= 4.5, $"it stopped 2-4.5 m from you ({end:0.00} m)");

        c.Instruct("Now walk about 10 m to your left or right and stand still again. The Pacer walks after you and stops about 3 m " +
                   "from you, facing you.");
        if (World.Position(c.Player) is not { } stood) return;
        if (!await c.Until(() => World.Position(c.Player) is { } p && CreatureControlFlat(p, stood) >= 6, TimeSpan.FromSeconds(40),
                "the player moved 6 m or more")) return;
        await c.Delay(TimeSpan.FromSeconds(1.5));
        var again = await CreatureControlFollow(c, e);
        c.Log.Info($"client-pass: {c.Id}: second walk: from {again.Start:0.00} m, walked {again.Path:0.00} m in {again.Took:0.0} s, longest step {again.Jump:0.00} m, stopped {again.End:0.00} m from the player");
        c.Expect(again.Path >= 3 && again.End is >= 2 and <= 4.5, $"it followed and stopped 2-4.5 m from you again (walked {again.Path:0.00} m, {again.End:0.00} m away)");

        c.Instruct("Did the Pacer walk to you both times on its legs (the feet stepping, not sliding, floating or jumping into " +
                   "place), turn towards you, and stop in front of you without attacking? The check removes it when you answer.");
        await c.Verdict();
    }

    // Watches the Pacer walk to the player until it has stood still for 2 s (or 20 s pass): the distance at the start, the
    // path it walked, its longest single step, the distance where it stopped, and the seconds it took.
    private static async Task<(double Start, double Path, double Jump, double End, double Took)> CreatureControlFollow(CheckContext c, EntityId e)
    {
        double Gap() => World.Position(e) is { } a && World.Position(c.Player) is { } b ? CreatureControlFlat(a, b) : double.NaN;
        var start = Gap();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var last = World.Position(e);
        double path = 0, jump = 0, still = 0, walked = 0;
        while (clock.Elapsed.TotalSeconds < 20 && World.Exists(e))
        {
            var before = clock.Elapsed.TotalSeconds;
            await c.Ticks();
            var now = World.Position(e);
            if (now is null || last is null) continue;
            var step = CreatureControlFlat(now.Value, last.Value);
            last = now;
            path += step;
            jump = Math.Max(jump, step);
            if (step > 0.01) walked = clock.Elapsed.TotalSeconds;
            still = step > 0.01 ? 0 : still + (clock.Elapsed.TotalSeconds - before);
            if (still >= 2 && path > 0.5) break;
        }
        return (start, path, jump, Gap(), walked);
    }

    private static double CreatureControlFlat(WorldPosition a, WorldPosition b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
