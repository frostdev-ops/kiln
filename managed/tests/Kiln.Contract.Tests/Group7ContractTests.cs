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

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeRelease(ulong owner)
    {
        _god = 0;  // the writer was the only mod holding it
        return 0;
    }

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
        t->ReleaseOwner = &FakeRelease;
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
    public void A_mod_s_unload_that_ends_god_mode_posts_the_change()
    {
        var e = new EntityId(5000);
        Combat.SetInvulnerable(e);              // as the writer mod
        HostAccess.Current = null;
        var seen = new List<CombatChangedEvent>();
        using var sub = Events.On<CombatChangedEvent>(seen.Add);  // an observer that outlives the writer
        Mod.Retire();                           // the writer unloads: kiln.combat releases its holds
        var ev = Assert.Single(seen);
        Assert.Equal(("Invulnerable", 1, 0, "writer"), (ev.What, ev.Before, ev.After, ev.Origin));
        Assert.True(ev.Involves(e));
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

/// <summary>combat.attributes on a fake v2 kiln.combat table: the ids kiln.combat resolved stay after the server world
/// closes, the attribute calls do not; the row is pending between worlds.</summary>
public sealed unsafe class CombatAttributesRowTests : FakeTableTest
{
    private static byte _health;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeIds(uint kind, CombatNative.AttributeIds* ids)
    {
        ids->Kind = kind;
        ids->CurrentId = 0x100 + kind;
        return 0;  // resolved once, kept across worlds
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeStatus(CombatNative.Status* s)
    {
        s->Health = _health;  // health access and the world's registry now
        return 0;
    }

    public CombatAttributesRowTests()
    {
        var t = Alloc<CombatNative.TableV2>();
        t->V1.Header = new ServiceHeader { Size = (uint)sizeof(CombatNative.TableV2), Version = 1 };
        t->V1.GetStatus = &FakeStatus;
        t->AttributeIdsFn = &FakeIds;
        Swap(typeof(Combat), "_listening", 1);
        Serve("kiln.combat", (CombatNative.Table*)t, typeof(CombatNative), "_t");
    }

    [Fact]
    public void The_row_is_pending_without_a_world_and_available_with_one()
    {
        _health = 1;
        StartRows(typeof(Combat).Assembly);
        Assert.True(Capabilities.Is("combat.attributes"), Capabilities.Why("combat.attributes"));
        _health = 0;  // the hosted world closed: kiln.combat dropped its world and registry
        CombatCaps.Rows.Refresh();  // the service tick's refresh
        Assert.Equal(CapabilityState.Pending, Capabilities.Get("combat.attributes").State);
    }
}

/// <summary>Rows of a service that is missing: a service published after the first lookup is found by the rows' refresh.</summary>
public sealed unsafe class MissingServiceRowTests : FakeTableTest
{
    private CombatNative.Table* NewTable()
    {
        var t = Alloc<CombatNative.Table>();
        t->Header = new ServiceHeader { Size = (uint)sizeof(CombatNative.Table), Version = 1 };
        return t;
    }

    [Fact]
    public void A_service_published_after_the_first_lookup_is_found_by_the_refresh()
    {
        Swap(typeof(Combat), "_listening", 1);
        Swap(typeof(CombatNative), "_t", Box<CombatNative.Table>(null));
        StartRows(typeof(Combat).Assembly);
        Capabilities.ReportService("kiln.combat", CapabilityReason.ServiceMissing, "kiln.combat is not here");  // as with a core
        Backend.Tables["kiln.combat"] = (nint)NewTable();  // published late
        Step(25);
        Assert.True(Capabilities.Is("kiln.combat"), Capabilities.Why("kiln.combat"));
    }
}
