// SPDX-License-Identifier: MIT
// A seeded random test of a player's visit against the real store: a join with a character file full of look-alikes,
// tags, swaps and moves the store sees, saves the client receives or misses, and an end at a random point: a crash (no
// leave save; the client keeps the last file it received) or a menu leave (the leave save, its file sent). Then the
// player comes back with the file it holds. Ground truth: every item carries its identity as its value, so no item may
// ever carry another one's. The second theory adds what the first real player showed (2026-11-12): worn gear (flat slots
// 256 and up) that the character loader makes before the backpack, look-alikes going into a chest and back, and a menu
// leave whose save read the player only once, a step before the game's snapshot.
using Xunit;
using static Kiln.InstanceHarness;

namespace Kiln;

[Collection("instance data")]
public sealed class InstanceLeaveRandomTests(ITestOutputHelper output)
{
    private static readonly InstanceFields[] Pool =
    [
        Rec.Of(Rec.Sword, level: 5),
        Rec.Of(Rec.Sword, level: 5),
        Rec.Of(Rec.Sword, level: 5),
        Rec.Of(Rec.Ring, level: 2, perksAvailable: 2),
        Rec.Of(Rec.Ring, level: 2, perksAvailable: 2, dye: 0xd7e00001),
    ];

    private sealed record Outcome(int Restored, int Wrong, int Untagged, string Story, List<string> Failures);

    private static Outcome Visit(Random rng, bool real = false)
    {
        using var h = new InstanceHarness();
        var w = h.World;
        w.PlayerLoaderWornFirst = real;
        var chest = real ? w.Chest(ChestTemplate, A) : EntityId.None;
        int[] worn = real ? [256, 257, 258, 259, 264] : [];
        var slots = Enumerable.Range(0, 12).Concat(worn).ToArray();
        var story = new List<string>();
        var nextIdentity = 1;
        // the file the client joins with
        var file = new List<(int Slot, InstanceFields Fields)>();
        var ids = new Dictionary<int, int>();
        foreach (var slot in slots.OrderBy(_ => rng.Next()).Take(rng.Next(2, 7)))
        {
            file.Add((slot, Pool[rng.Next(Pool.Length)]));
            ids[slot] = nextIdentity++;
        }
        w.CharacterFiles[Hero] = file.OrderBy(f => f.Slot).ToList();
        w.FileIdentities[Hero] = ids;
        var p = w.Join(Hero);
        h.Tick();
        var tagged = new HashSet<int>();
        void TagSome()
        {
            foreach (var i in w.Items.Values.Where(i => i.Holder == p && !tagged.Contains(i.Identity) && rng.NextDouble() < 0.6).ToList())
            {
                h.Tag(i.Ref, i.Identity);
                tagged.Add(i.Identity);
            }
        }
        TagSome();
        story.Add($"join with {file.Count} items, {tagged.Count} tagged");
        uint rev = 1;
        var sinceReceived = 0;  // states newer than the client's file (the store keeps the newest 5: the client's must be among them)
        var ops = rng.Next(2, 14);
        for (var o = 0; o < ops; o++)
        {
            var mine = w.Items.Values.Where(i => i.Holder == p).ToList();
            var free = Enumerable.Range(0, 16).Concat(worn).Where(s => mine.All(i => i.Slot != s)).ToArray();
            switch (rng.Next(real ? 8 : 6))
            {
                case 0 when mine.Count > 1:
                {
                    var a = mine[rng.Next(mine.Count)];
                    var b = mine[rng.Next(mine.Count)];
                    if (a == b) break;
                    w.Swap(a.Ref, b.Ref);
                    story.Add($"swap {a.Slot}<->{b.Slot}");
                    break;
                }
                case 1 when mine.Count > 0 && free.Length > 0:
                {
                    var a = mine[rng.Next(mine.Count)];
                    var to = free[rng.Next(free.Length)];
                    story.Add($"move {a.Slot}->{to}");
                    w.Move(a.Ref, p, to);
                    break;
                }
                case 2 or 3:
                {
                    var received = rng.NextDouble() < 0.75 || sinceReceived >= 4;
                    h.Save(rev);
                    if (received)
                    {
                        w.SendCharacter(p);
                        sinceReceived = 0;
                    }
                    else sinceReceived++;
                    story.Add($"save {rev}{(received ? "" : " (missed by the client)")}");
                    rev++;
                    break;
                }
                case 4 when free.Length > 0:
                {
                    var f = Pool[rng.Next(Pool.Length)];
                    var to = free[rng.Next(free.Length)];
                    w.Give(p, to, f, identity: nextIdentity++);
                    story.Add($"new item in {to}");
                    break;
                }
                case 5:
                    TagSome();
                    story.Add("tag");
                    break;
                case 6 when mine.Count > 0:
                {
                    var a = mine[rng.Next(mine.Count)];
                    var to = Enumerable.Range(0, 24).First(s => w.Items.Values.All(i => i.Holder != chest || i.Slot != s));
                    story.Add($"{a.Slot} into the chest");
                    w.Move(a.Ref, chest, to);
                    break;
                }
                case 7 when free.Length > 0 && w.Items.Values.Any(i => i.Holder == chest):
                {
                    var stored = w.Items.Values.Where(i => i.Holder == chest).ToList();
                    var a = stored[rng.Next(stored.Count)];
                    var to = free[rng.Next(free.Length)];
                    story.Add($"from the chest into {to}");
                    w.Move(a.Ref, p, to);
                    break;
                }
            }
            if (rng.NextDouble() < 0.3) h.Tick();
        }
        if (rng.NextDouble() < 0.5)
        {
            story.Add("crash");
            w.Crash(p);
            h.Tick();
        }
        else
        {
            // the real leaves were read once, at the request: the snapshot staging did not read the leaving player again
            var once = real && rng.NextDouble() < 0.5;
            story.Add($"menu leave (save {rev}{(once ? ", read once" : "")})");
            Store.Stage(rev, SaveReason.PlayerLeft, late: false);
            w.BeginLeave(p);
            h.Tick();
            if (!once) Store.Stage(rev, SaveReason.PlayerLeft, late: false, snapshot: true);
            Store.Commit(rev, 1000 + rev, SaveReason.PlayerLeft);
            w.SendCharacter(p);
            w.DestroyPlayerEntity(p);
            h.Tick();
            w.TearDown(p);
            h.Tick();
            rev++;
        }
        if (rng.NextDouble() < 0.5)
        {
            h.Save(rev);
            story.Add($"save {rev} while away");
        }
        var back = w.Join(Hero);
        h.Tick();
        int restored = 0, wrong = 0, untagged = 0;
        var failures = new List<string>();
        foreach (var i in w.Items.Values.Where(i => i.Holder == back))
        {
            var v = h.Count(i.Ref);
            if (v is null)
            {
                if (tagged.Contains(i.Identity)) untagged++;
                continue;
            }
            if (v == i.Identity) restored++;
            else
            {
                wrong++;
                failures.Add($"item #{i.Identity} in slot {i.Slot} carries #{v}'s value");
            }
        }
        return new Outcome(restored, wrong, untagged, string.Join("; ", story), failures);
    }

    [Theory]
    [InlineData(20261112, 150)]
    [InlineData(11, 150)]
    [InlineData(31337, 150)]
    public void NoItemEverCarriesAnotherItemsValueAfterALeaveAndARejoin(int seed, int visits) => Run(seed, visits, real: false);

    [Theory]
    [InlineData(20261112, 300)]
    [InlineData(7, 300)]
    [InlineData(4242, 300)]
    public void NoItemEverCarriesAnotherItemsValueWithWornGearChestsAndALeaveReadOnce(int seed, int visits) => Run(seed, visits, real: true);

    private void Run(int seed, int visits, bool real)
    {
        var rng = new Random(seed);
        int restored = 0, aside = 0;
        var failures = new List<string>();
        for (var v = 0; v < visits; v++)
        {
            var o = Visit(rng, real);
            restored += o.Restored;
            aside += o.Untagged;
            foreach (var f in o.Failures)
                if (failures.Count < 20) failures.Add($"seed {seed} visit {v} ({o.Story}): {f}");
        }
        output.WriteLine($"seed {seed}: {visits} visits, {restored} values back on their own items, {aside} tagged items without their value (set aside or waiting)");
        foreach (var f in failures) output.WriteLine(f);
        Assert.True(restored > 0, "no value came back at all: the test exercised nothing");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
