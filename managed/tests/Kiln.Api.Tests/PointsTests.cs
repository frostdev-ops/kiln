// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Api.Tests;

public sealed class PointsTests : KilnTest
{
    private readonly record struct Sale(int Item, int Count);

    private static (PointVerdict, int) Set(int v) => (PointVerdict.Set, v);

    [Fact]
    public void NoLinkKeepsTheOwnersAnswer()
    {
        var r = new PointRegistry();
        var price = r.Define<Sale, int>("you.trading-post", "price", PointMode.Override);
        Assert.Equal("you.trading-post:price", price.Name);
        var d = price.Decide(new Sale(1, 2), 10);
        Assert.Equal(PointOutcome.Kept, d.Outcome);
        Assert.Equal(10, d.Value);
    }

    [Fact]
    public void LinksChangeTheAnswerInOrder()
    {
        var r = new PointRegistry();
        var price = r.Define<Sale, int>("you.trading-post", "price", PointMode.Override);
        r.Extend<Sale, int>("you.b", price.Name, (s, v) => Set(v * 2));
        r.Extend<Sale, int>("you.a", price.Name, (s, v) => Set(v + 1));  // same priority: by mod id, so a first
        var d = price.Decide(new Sale(1, 1), 10);
        Assert.Equal(PointOutcome.Changed, d.Outcome);
        Assert.Equal(22, d.Value);
        Assert.Equal("you.b", d.DecidedBy);
        Assert.Equal(["you.a", "you.b"], price.Links.Select(l => l.Owner));
    }

    [Fact]
    public void PriorityAndBeforeAfterOrderTheLinks()
    {
        var r = new PointRegistry();
        var p = r.Define<int, string>("o", "p", PointMode.Advise);
        r.Extend<int, string>("z.late", p.Name, (_, v) => (PointVerdict.Set, v + "z"), new PointOrder { Priority = 10 });
        r.Extend<int, string>("m.mid", p.Name, (_, v) => (PointVerdict.Set, v + "m"));
        r.Extend<int, string>("a.first", p.Name, (_, v) => (PointVerdict.Set, v + "a"), new PointOrder { After = ["m.mid"] });
        r.Extend<int, string>("b.x", p.Name, (_, v) => (PointVerdict.Set, v + "b"), new PointOrder { Before = ["m.mid"] });
        Assert.Equal("bmaz", p.Decide(0, "").Value);
    }

    [Fact]
    public void ACycleFallsBackToPriorityAndIsNoted()
    {
        var notes = new List<string>();
        var r = new PointRegistry { Log = notes.Add };
        var p = r.Define<int, string>("o", "p", PointMode.Override);
        r.Extend<int, string>("a", p.Name, (_, v) => (PointVerdict.Set, v + "a"), new PointOrder { After = ["b"] });
        r.Extend<int, string>("b", p.Name, (_, v) => (PointVerdict.Set, v + "b"), new PointOrder { After = ["a"] });
        Assert.Equal("ab", p.Decide(0, "").Value);
        Assert.Contains(notes, n => n.Contains("before/after"));
    }

    [Fact]
    public void RefuseStopsTheChainOnlyWhereAllowed()
    {
        var r = new PointRegistry();
        var buys = r.Define<Sale, int>("post", "buy", PointMode.Override, allowRefuse: true);
        var ran = false;
        r.Extend<Sale, int>("a.refuser", buys.Name, (s, v) => s.Item == 7 ? (PointVerdict.Refuse, v) : (PointVerdict.Pass, v));
        r.Extend<Sale, int>("b.after", buys.Name, (s, v) => { ran = true; return Set(v); });
        var d = buys.Decide(new Sale(7, 1), 5);
        Assert.True(d.IsRefused);
        Assert.Equal(-1, d.ValueOr(-1));
        Assert.Equal("a.refuser", d.DecidedBy);
        Assert.False(ran);

        var notes = new List<string>();
        var r2 = new PointRegistry { Log = notes.Add };
        var strict = r2.Define<Sale, int>("post", "buy", PointMode.Override);
        r2.Extend<Sale, int>("a.refuser", strict.Name, (_, v) => (PointVerdict.Refuse, v));
        Assert.Equal(PointOutcome.Kept, strict.Decide(new Sale(7, 1), 5).Outcome);
        Assert.Contains(notes, n => n.Contains("does not allow refusing"));
    }

    [Fact]
    public void ObservePointsAndObserversNeverChangeTheAnswer()
    {
        var r = new PointRegistry();
        var seen = new List<int>();
        var p = r.Define<int, int>("o", "count", PointMode.Observe);
        r.Extend<int, int>("x", p.Name, (_, v) => Set(v + 100));
        r.Observe<int, int>("y", p.Name, (_, v) => seen.Add(v));
        var d = p.Decide(1, 3);
        Assert.Equal(PointOutcome.Kept, d.Outcome);
        Assert.Equal(3, d.Value);
        Assert.Equal([3], seen);
    }

    [Fact]
    public void AThrowingLinkIsTurnedOffAlone()
    {
        var notes = new List<string>();
        var r = new PointRegistry { Log = notes.Add };
        var p = r.Define<int, int>("o", "p", PointMode.Override);
        var bad = r.Extend<int, int>("a.bad", p.Name, (_, _) => throw new InvalidOperationException("boom"));
        r.Extend<int, int>("b.good", p.Name, (_, v) => Set(v + 1));
        Assert.Equal(2, p.Decide(0, 1).Value);
        Assert.False(bad.Enabled);
        Assert.Contains("boom", bad.OffReason);
        Assert.Equal(2, p.Decide(0, 1).Value);  // stays off
        Assert.Single(p.Links);
        Assert.Contains(notes, n => n.Contains("a.bad"));
    }

    [Fact]
    public void LinksMayComeFirstAndWrongTypesStayOff()
    {
        var r = new PointRegistry();
        var early = r.Extend<int, int>("x", "o:p", (_, v) => Set(v * 10));
        var wrong = r.Extend<string, int>("y", "o:p", (_, v) => Set(0));
        var p = r.Define<int, int>("o", "p", PointMode.Override);
        Assert.True(early.Enabled);
        Assert.False(wrong.Enabled);
        Assert.StartsWith("does not match", wrong.OffReason);
        Assert.Equal(20, p.Decide(0, 2).Value);
    }

    [Fact]
    public void SwitchOffDisposeAndOwnerRemoval()
    {
        var r = new PointRegistry();
        var p = r.Define<int, int>("o", "p", PointMode.Override);
        var a = r.Extend<int, int>("a", p.Name, (_, v) => Set(v + 1));
        r.Extend<int, int>("b", p.Name, (_, v) => Set(v + 10));
        r.SetEnabled(p.Name, "b", false);
        Assert.Equal(1, p.Decide(0, 0).Value);
        Assert.Equal("switched off", r.AllLinks.Single(l => l.Owner == "b").OffReason);
        r.SetEnabled(p.Name, "b", true);
        Assert.Equal(11, p.Decide(0, 0).Value);
        a.Dispose();
        Assert.Equal(10, p.Decide(0, 0).Value);
        r.RemoveOwner("b");
        Assert.Equal(PointOutcome.Kept, p.Decide(0, 0).Outcome);
        r.RemoveOwner("o");
        Assert.Empty(r.Defined);
    }

    [Fact]
    public void NamesAreChecked()
    {
        var r = new PointRegistry();
        r.Define<int, int>("o", "p", PointMode.Override);
        Assert.Throws<InvalidOperationException>(() => r.Define<int, int>("o", "p", PointMode.Override));
        Assert.Throws<ArgumentException>(() => r.Define<int, int>("o", "x:y", PointMode.Override));
        Assert.Throws<ArgumentException>(() => r.Extend<int, int>("a", "no-owner", (_, v) => Set(v)));
        Assert.Throws<ArgumentException>(() => r.Define<int, int>("", "p", PointMode.Override));
        Assert.Throws<ArgumentException>(() => r.Define<int, int>("o:other", "p", PointMode.Override));
        foreach (var name in new[] { ":p", "o:", "o:a:b", "o: " })
            Assert.Throws<ArgumentException>(() => r.Extend<int, int>("a", name, (_, v) => Set(v)));
    }

    [Fact]
    public void TheSharedRegistryOutsideAModIsOwnedByKiln()
    {
        var p = Points.Define<int, int>("points-test-" + Guid.NewGuid().ToString("N"), PointMode.Override);
        Assert.StartsWith("kiln:", p.Name);
        Points.Shared.RemoveOwner("kiln");
    }

    [Fact]
    public void RetiredPointHandlesCannotDisableReplacementLinks()
    {
        var r = new PointRegistry();
        var old = r.Define<int, int>("o", "p", PointMode.Override);
        r.RemoveOwner("o");
        var replacement = r.Define<string, string>("o", "p", PointMode.Override);
        var link = r.Extend<string, string>("x", replacement.Name, (_, v) => (PointVerdict.Set, v + "!"));
        Assert.Equal(7, old.Decide(0, 7).Value);
        Assert.Empty(old.Links);
        Assert.True(link.Enabled);
        Assert.Equal("yes!", replacement.Decide("", "yes").Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingALinkDuringDispatchSkipsItsSnapshot(bool unload)
    {
        var r = new PointRegistry();
        var p = r.Define<int, int>("o", "p", PointMode.Override);
        PointLink? later = null;
        r.Observe<int, int>("a", p.Name, (_, _) =>
        {
            if (unload) r.RemoveOwner("b");
            else later!.Dispose();
        });
        later = r.Extend<int, int>("b", p.Name, (_, v) => Set(v + 100));
        Assert.Equal(1, p.Decide(0, 1).Value);
        Assert.False(later.Enabled);
    }

    [Fact]
    public void BrokenDiagnosticsDoNotBreakFaultContainment()
    {
        var r = new PointRegistry { Log = _ => throw new Exception("log failed") };
        var p = r.Define<int, int>("o", "p", PointMode.Override);
        var bad = r.Extend<int, int>("a", p.Name, (_, _) => throw new Exception("bad link"));
        r.Extend<int, int>("b", p.Name, (_, v) => Set(v + 1));
        Assert.Equal(2, p.Decide(0, 1).Value);
        Assert.False(bad.Enabled);
    }

    [Fact]
    public void OrderingInputsAndPublishedChainsCannotBeMutated()
    {
        var r = new PointRegistry();
        var p = r.Define<int, string>("o", "p", PointMode.Override);
        var after = new List<string> { "b" };
        r.Extend<int, string>("a", p.Name, (_, v) => (PointVerdict.Set, v + "a"), new PointOrder { After = after });
        after.Clear();
        r.Extend<int, string>("b", p.Name, (_, v) => (PointVerdict.Set, v + "b"));
        Assert.Equal("ba", p.Decide(0, "").Value);
        Assert.False(p.Links is PointLink[]);
    }

    [Fact]
    public void SharedLinksRunAsTheirOwnerAndRetirementRemovesThem()
    {
        var owner = NewMod("point-owner");
        var extender = NewMod("point-extender");
        var p = As(owner, () => Points.Define<int, int>("p", PointMode.Override));
        Point<int, int>? nested = null;
        As(extender, () => Points.Observe<int, int>(p.Name, (_, _) =>
        {
            Assert.Same(extender, ModContext.CurrentMod);
            nested = Points.Define<int, int>("nested", PointMode.Observe);
        }));
        As(owner, () =>
        {
            p.Decide(0, 1);
            Assert.Same(owner, ModContext.CurrentMod);
        });
        Assert.Equal("point-extender:nested", nested!.Name);
        extender.Retire();
        Assert.Empty(p.Links);
        Assert.DoesNotContain(Points.Shared.Defined, d => d.Owner == extender.Info.Id);
        Assert.Throws<InvalidOperationException>(() => As(extender, () => Points.Define<int, int>("late", PointMode.Observe)));
        owner.Retire();
    }

    [Fact]
    public void ConcurrentRegistrationAndRetirementLeaveNoLinks()
    {
        for (var i = 0; i < 500; ++i)
        {
            var mod = NewMod("point-race-" + i);
            Parallel.Invoke(
                () => As(mod, () =>
                {
                    try { Points.Extend<int, int>("kiln:race", (_, v) => Set(v)); }
                    catch (InvalidOperationException) { }
                }),
                mod.Retire);
            Assert.DoesNotContain(Points.Shared.AllLinks, l => l.Owner == mod.Info.Id);
        }
    }

    [Fact]
    public void UnloadingAPointOwnerDropsForeignAndPendingLinks()
    {
        var r = new PointRegistry();
        var p = r.Define<int, int>("provider", "p", PointMode.Override);
        var attached = r.Extend<int, int>("consumer", p.Name, (_, v) => Set(v));
        var pending = r.Extend<int, int>("consumer", "provider:pending", (_, v) => Set(v));
        var other = r.Extend<int, int>("consumer", "other:p", (_, v) => Set(v));
        r.RemoveOwner("provider");
        Assert.False(attached.Enabled);
        Assert.False(pending.Enabled);
        Assert.Equal([other], r.AllLinks);
        Assert.Empty(p.Links);
    }
}
