// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Api.Tests;

public sealed class PointsTests
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
    }

    [Fact]
    public void TheSharedRegistryOutsideAModIsOwnedByKiln()
    {
        var p = Points.Define<int, int>("points-test-" + Guid.NewGuid().ToString("N"), PointMode.Override);
        Assert.StartsWith("kiln:", p.Name);
        Points.Shared.RemoveOwner("kiln");
    }
}
