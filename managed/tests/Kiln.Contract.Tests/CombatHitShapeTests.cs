// SPDX-License-Identifier: MIT
// What a hit "about an entity" is, for every shape of hit kiln.combat reports (dev.27): the native hold names a hit by
// four entities (native/services/combat/hold_filter.h hit_entities: the root target or else the target, the target, the
// root source, the source) and the events carry the same four (Target, Part, Attacker, Source), so a filtered handler
// gets exactly the hits the hold kept for it. The shapes are hold_test.cpp's. The owner's first run of
// event-filter-test/events.filter-hits (13 Nov 2026): the watched Vukah's own attacks on the player came, Target the player.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Kiln.Contract.Tests;

public sealed unsafe class CombatHitShapeTests : FakeTableTest
{
    private const uint Player = 1, Doll = 17887, Other = 17888, WeakSpot = 17889, Swing = 18001, Claw = 18002,
        OtherClaw = 18003, Arrow = 18004, Area = 18005;

    private static nint _listener;
    private static uint[] _held = [];
    private static readonly List<CombatNative.DamageRequest> Requests = [];

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeSetListener(delegate* unmanaged[Cdecl]<CombatNative.Event*, uint, void*, uint> fn, void* user)
    {
        _listener = (nint)fn;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeHoldFilter(uint* ids, uint count, uint interest)
    {
        _held = new ReadOnlySpan<uint>(ids, (int)count).ToArray();
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeGetHealth(uint e, CombatNative.Health* h)
    {
        h->Current = 100;
        h->Max = 100;
        h->HasActor = 1;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeDamage(ulong owner, CombatNative.DamageRequest* r, ulong* id)
    {
        Requests.Add(*r);
        *id = (ulong)Requests.Count;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeHitStatus(ulong id, CombatNative.HitStatus* s)
    {
        s->State = 2;  // applied
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeStatus(CombatNative.Status* s)
    {
        s->Health = s->Hits = 1;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeIds(uint kind, CombatNative.AttributeIds* ids) => 3;  // no attribute resource read

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeRelease(ulong owner) => 0;

    public CombatHitShapeTests()
    {
        _listener = 0;
        _held = [];
        Requests.Clear();
        var t = Alloc<CombatNative.TableV3>();
        t->V2.V1.Header = new ServiceHeader { Size = (uint)sizeof(CombatNative.TableV3), Version = 1 };
        t->V2.V1.SetListener = &FakeSetListener;
        t->V2.V1.GetHealth = &FakeGetHealth;
        t->V2.V1.Damage = &FakeDamage;
        t->V2.V1.HitStatus = &FakeHitStatus;
        t->V2.V1.GetStatus = &FakeStatus;
        t->V2.V1.ReleaseOwner = &FakeRelease;
        t->V2.AttributeIdsFn = &FakeIds;
        t->HoldFilter = &FakeHoldFilter;
        Swap(typeof(Combat), "_listening", 0);
        Serve("kiln.combat", (CombatNative.Table*)t, typeof(CombatNative), "_t");
    }

    public override void Dispose()
    {
        Mod.Retire();  // the test's Combat.Damage calls are done with
        base.Dispose();
    }

    /// <summary>A held hit record as kiln.combat turns it into an event (fill_from_hit, through hit_entities).</summary>
    private static CombatNative.Event FromRecord(ulong id, uint target, uint rootTarget, uint source, uint rootSource) => new()
    {
        Size = (uint)sizeof(CombatNative.Event),
        Kind = CombatNative.Hurt,
        Id = id,
        Target = rootTarget != 0 ? rootTarget : target,
        Part = target,
        Attacker = rootSource,
        Source = source,
        Amount = 10,
        HealthBefore = 100,
        HealthMax = 100,
    };

    // hold_test.cpp's shapes: (target, root target, source, root source)
    private static readonly (string Name, uint T, uint Rt, uint S, uint Rs)[] Shapes =
    [
        ("the player's swing on the doll", Doll, Doll, Swing, Player),
        ("the player's swing on the doll's weak spot", WeakSpot, Doll, Swing, Player),
        ("the doll's own attack on the player", Player, Player, Claw, Doll),
        ("the player's arrow on the doll", Doll, Doll, Arrow, Player),
        ("the player's spell area on the doll", Doll, Doll, Area, Player),
        ("a hit on the doll with no root target", Doll, 0, Player, Player),
        ("Kiln's Combat.Damage on the doll (AsGameHit)", Doll, Doll, Player, Player),
        ("the player's swing on the other", Other, Other, Swing, Player),
        ("the other's attack on the player", Player, Player, OtherClaw, Other),
    ];

    [Fact]
    public void Each_shape_of_hit_reaches_the_handlers_of_the_entities_it_names_and_the_hold_watches_the_same()
    {
        Assert.True(Combat.EnsureListener());  // kiln.combat's table is found: the hold filter is pushed from now
        var byEntity = new Dictionary<uint, List<(string Name, EntityHurtEvent E)>>();
        var subs = new List<EventSubscription>();
        foreach (var w in (uint[])[Doll, Player, WeakSpot, Arrow, Other])
        {
            var list = byEntity[w] = [];
            subs.Add(Events.On<EntityHurtEvent>(e => list.Add((Shapes[(int)e.Step].Name, e)), EventFilter.ForEntity(new EntityId(w))));
        }
        // the hold kiln.combat gets: the watched entities, nothing else (a hit naming none of them applies at once)
        Assert.Equal([Player, Doll, Other, WeakSpot, Arrow], _held);
        Assert.NotEqual(0, _listener);
        var events = Shapes.Select((s, i) => FromRecord((ulong)i + 1, s.T, s.Rt, s.S, s.Rs) with { Step = (ulong)i }).ToArray();
        fixed (CombatNative.Event* p = events)
            ((delegate* unmanaged[Cdecl]<CombatNative.Event*, uint, void*, uint>)_listener)(p, (uint)events.Length, null);

        // every handler got exactly the shapes that name its entity as target, part, attacker or source
        foreach (var (w, got) in byEntity)
        {
            var want = Shapes.Where(s => (s.Rt != 0 ? s.Rt : s.T) == w || s.T == w || s.Rs == w || s.S == w).Select(s => s.Name);
            Assert.Equal(want, got.Select(g => g.Name));
        }
        // the doll's: the hits it takes (Target the doll, also on its weak spot: Part) and the one it deals
        var doll = byEntity[Doll];
        Assert.Equal(7, doll.Count);
        Assert.All(doll, g => Assert.True(g.E.Involves(new EntityId(Doll))));
        Assert.Equal(new EntityId(WeakSpot), doll.Single(g => g.Name.Contains("weak spot")).E.Part);
        Assert.All(doll.Where(g => !g.Name.Contains("own attack")), g => Assert.Equal(new EntityId(Doll), g.E.Target));
        // the owner's three events were this one: about the doll, but the player is its Target
        var own = doll.Single(g => g.Name == "the doll's own attack on the player").E;
        Assert.Equal((new EntityId(Player), new EntityId(Doll), new EntityId(Claw)), (own.Target, own.Attacker, own.Source));
        // a handler that wants only the hits the doll takes asks the Target: 6 of the doll's 7
        Assert.Equal(6, doll.Count(g => g.E.Target == new EntityId(Doll)));
        // nothing between the player and the other Vukah reached the doll's handler
        Assert.DoesNotContain(doll, g => g.E.Involves(new EntityId(Other)));
        Assert.Equal(["the player's swing on the doll's weak spot"], byEntity[WeakSpot].Select(g => g.Name));
        Assert.Equal(["the player's arrow on the doll"], byEntity[Arrow].Select(g => g.Name));
        foreach (var s in subs) s.Dispose();
    }

    /// <summary>Kiln's own hit (Combat.Damage without AsGameHit: its EntityHurtEvent is posted inside the call) names
    /// the source it was given, as the same hit through the hold does (the record's source is the request's) and as the
    /// game's hits do: a handler filtered on the source gets it.</summary>
    [Fact]
    public void A_Kiln_hit_with_a_source_names_the_source_like_the_held_one_does()
    {
        var bySource = new List<EntityHurtEvent>();
        var knocks = new List<EntityKnockbackEvent>();
        using var a = Events.On<EntityHurtEvent>(bySource.Add, EventFilter.ForEntity(new EntityId(Arrow)));
        using var b = Events.On<EntityKnockbackEvent>(knocks.Add, EventFilter.ForEntity(new EntityId(Arrow)));
        _ = Combat.Damage(new EntityId(Doll), 5, new EntityId(Player),
            new DamageOptions { Source = new EntityId(Arrow), PushBack = 20, Reaction = HitReaction.Light });
        var e = Assert.Single(bySource);
        Assert.Equal((new EntityId(Doll), new EntityId(Doll), new EntityId(Player), new EntityId(Arrow)), (e.Target, e.Part, e.Attacker, e.Source));
        var k = Assert.Single(knocks);
        Assert.Equal((new EntityId(Doll), new EntityId(Player), new EntityId(Arrow)), (k.Target, k.Attacker, k.Source));
        Assert.Equal(Arrow, Assert.Single(Requests).Source);  // the record kiln.combat builds names the arrow too
        // without a source the attacker is the source, as kiln.combat's record has it
        var byPlayer = new List<EntityHurtEvent>();
        using var c = Events.On<EntityHurtEvent>(byPlayer.Add, EventFilter.ForEntity(new EntityId(Player)));
        _ = Combat.Damage(new EntityId(Doll), 5, new EntityId(Player));
        Assert.Equal(new EntityId(Player), Assert.Single(byPlayer).Source);
    }
}
