// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Kiln.Contract.Tests;

/// <summary>Kiln.Combat (health, hits, god mode, attributes) by the contract.</summary>
public sealed class CombatContractTests : ContractTest
{
    [Fact]
    public void With_no_service_every_row_is_off_with_the_reason()
    {
        StartRows(typeof(Combat).Assembly);
        Assert.False(Combat.Available);
        Assert.Null(Combat.Status);
        Assert.False(Attributes.Available);
        AssertOffline("kiln.combat");
        foreach (var name in (string[])["combat.health", "combat.hits", "combat.deaths", "combat.falls", "combat.hold_filter", "combat.attributes"])
            AssertOffOffline(name, "needs kiln.combat");
        Assert.False(Capabilities.Get("combat.attributes").SessionDependent);  // never pending without the service
    }

    [Fact]
    public void A_call_that_needs_the_service_throws_unavailable_naming_it()
    {
        var e = new EntityId(5000);
        AssertUnavailable("kiln.combat", () => Combat.GetHealth(e));
        AssertUnavailable("kiln.combat", () => Combat.SetHealth(e, 10));
        AssertUnavailable("kiln.combat", () => Combat.Heal(e, 10));
        AssertUnavailable("kiln.combat", () => Combat.Damage(e, 10));
        AssertUnavailable("kiln.combat", () => Combat.SetInvulnerable(e));
        AssertUnavailable("kiln.combat", () => Attributes.Get(e, 0x1234u));
        // a pair's ids are unknown without the service: that stays the InvalidOperationException it was
        Assert.Throws<InvalidOperationException>(() => Attributes.Set(e, GameAttribute.Stamina, 5));
        Assert.False(Attributes.TrySet(e, GameAttribute.Stamina, 5, out _));
        Assert.False(Combat.IsInvulnerable(e));
    }

    [Fact]
    public void A_hit_that_was_not_applied_is_a_refusal_with_a_reason()
    {
        IKilnResult applied = new HitResult(HitState.Applied, new EntityId(5), 10, 50, 40, false, 1);
        Assert.True(applied.Ok);
        Assert.Null(applied.Reason);
        IKilnResult dropped = new HitResult(HitState.Dropped, new EntityId(5), 10, 50, 50, false, 0);
        Assert.False(dropped.Ok);
        Assert.StartsWith("dropped", dropped.Reason);
        Assert.False(new HitResult(HitState.Pending, new EntityId(5), 10, 50, 50, false, 0).Ok);
    }

    [Fact]
    public void Members_carry_their_side_thread_and_capability()
    {
        AssertHas<ServerOnlyAttribute>(typeof(Combat));
        AssertHas<ServerOnlyAttribute>(typeof(Attributes));
        var set = Method(typeof(Combat), "SetHealth", typeof(EntityId), typeof(int));
        AssertHas<TickThreadOnlyAttribute>(set);
        AssertRequires(set, "kiln.combat");
        AssertHas<AnyThreadAttribute>(Property(typeof(Combat), "Status"));
        AssertRequires(Method(typeof(Attributes), "Get", typeof(EntityId), typeof(uint)), "combat.attributes");
        AssertRequires(Method(typeof(Attributes), "Get", typeof(EntityId), typeof(GameAttribute)));  // null, not a throw
    }
}

/// <summary>Kiln.Combat's own writes on a fake kiln.combat table: an event for a change, none for no change, and a
/// handler that writes again cannot loop.</summary>
public sealed unsafe class CombatChangeTests : FakeTableTest
{
    private static int _health;
    private static byte _god;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeGetHealth(uint e, CombatNative.Health* h)
    {
        h->Current = _health;
        h->Max = 100;
        h->HasActor = 1;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeSetHealth(uint e, int value, int* applied)
    {
        _health = Math.Clamp(value, 0, 100);
        *applied = _health;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeSetGod(ulong owner, uint e, byte on)
    {
        _god = on;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte FakeGod(uint e) => _god;

    public CombatChangeTests()
    {
        _health = 50;
        _god = 0;
        var t = Alloc<CombatNative.Table>();
        t->Header = new ServiceHeader { Size = (uint)sizeof(CombatNative.Table), Version = 1 };
        t->GetHealth = &FakeGetHealth;
        t->SetHealth = &FakeSetHealth;
        t->SetInvulnerable = &FakeSetGod;
        t->Invulnerable = &FakeGod;
        Swap(typeof(Combat), "_listening", 1);  // no native listener on a fake table
        Serve("kiln.combat", t, typeof(CombatNative), "_t");
    }

    [Fact]
    public void Setting_health_posts_one_event_and_none_when_it_did_not_change()
    {
        var seen = new List<CombatChangedEvent>();
        using var sub = Events.On<CombatChangedEvent>(seen.Add);
        var e = new EntityId(5000);
        Assert.Equal(30, Combat.SetHealth(e, 30));
        Assert.Equal(30, Combat.SetHealth(e, 30));  // no change
        var ev = Assert.Single(seen);
        Assert.Equal(("Health", 50, 30, "writer"), (ev.What, ev.Before, ev.After, ev.Origin));
        Assert.True(ev.Involves(e));
        Combat.SetInvulnerable(e);
        Combat.SetInvulnerable(e);  // already on
        Assert.Equal("Invulnerable", seen[1].What);
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public void A_catch_all_handler_that_writes_again_keeps_working()
    {
        var calls = 0;
        var others = 0;
        var e = new EntityId(5000);
        var next = 10;
        var sub = Events.On<KilnEvent>(ev =>
        {
            if (ev is CombatChangedEvent) calls++;
            else others++;
            Combat.SetHealth(e, next++ % 90 + 1);  // a different value every time: a real change, every time
        });
        for (var i = 0; i < Events.MaxFaults + 1; i++) Combat.SetHealth(e, next++ % 90 + 1);
        Assert.True(sub.Active);
        Assert.Equal(0, sub.Stats.Faults);
        Assert.Equal(Events.MaxFaults + 1 + others, calls);
        Step(3);
        Assert.Equal(Events.MaxFaults + 4 + others, calls);
        sub.Dispose();
        Step();
        Assert.Equal(0, Combat.Notices.Waiting);
    }
}
