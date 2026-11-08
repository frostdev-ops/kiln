// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Api.Tests;

/// <summary>A world of entities with component sets; changes apply at <see cref="EndStep"/>, as the engine's are.</summary>
internal sealed class FakeOfferWorld : IOfferWorld
{
    public readonly Dictionary<uint, Dictionary<uint, byte[]>> Entities = new();
    public readonly HashSet<uint> Players = [];
    public readonly List<(uint Entity, uint Hash, bool Add, byte[]? Data)> Queue = [];
    public readonly List<(uint Entity, uint Hash, bool Add)> Log = [];
    public bool Ignore;  // the engine drops every change

    public bool Available => true;
    public bool Exists(uint e) => Entities.ContainsKey(e);
    public bool Has(uint e, uint h) => Entities.TryGetValue(e, out var c) && c.ContainsKey(h);
    public bool IsPlayer(uint e) => Players.Contains(e);

    public (uint Sequence, uint Verb)? ReadRuntime(uint e) =>
        Entities.TryGetValue(e, out var c) && c.TryGetValue(Offers.InteractionOfferRuntime, out var b)
            ? (BitConverter.ToUInt32(b, 8), BitConverter.ToUInt32(b, 16))
            : null;

    public int Change(ulong owner, uint e, uint h, byte[]? data, bool add)
    {
        Queue.Add((e, h, add, data));
        Log.Add((e, h, add));
        return 0;
    }

    public void EndStep()
    {
        if (!Ignore)
            foreach (var (e, h, add, data) in Queue)
                if (Entities.TryGetValue(e, out var c))
                {
                    if (add) c.TryAdd(h, data!);
                    else c.Remove(h);
                }
        Queue.Clear();
    }

    public void Spawn(uint e, params uint[] components) => Entities[e] = components.ToDictionary(h => h, _ => new byte[56]);
}

public sealed class OffersTests : KilnTest
{
    private readonly FakeOfferWorld _w = new();
    private const uint Prop = 500;

    public OffersTests()
    {
        Offers.Reset();
        Offers.World = _w;
        _w.Spawn(Prop);
    }

    public override void Dispose()
    {
        base.Dispose();
        Offers.Reset();
    }

    private void Run(int steps = 1)
    {
        for (var i = 0; i < steps; i++)
        {
            Step();
            _w.EndStep();
        }
    }

    private static readonly uint[] Three =
        [Offers.InteractionOfferRuntime, Offers.ClientInteractionOffer, Offers.CheckPlayerInRange];

    [Fact]
    public void Add_puts_the_three_components_on_and_becomes_active()
    {
        var mod = NewMod("a");
        As(mod, () => Offers.Add(new EntityId(Prop), null, OfferVerbs.Talk, _ => { }));
        Assert.Equal(3, _w.Queue.Count);
        Assert.Equal(OfferStatus.Pending, As(mod, () => Offers.Status(new EntityId(Prop))));
        Assert.True(As(mod, () => Offers.IsActive(new EntityId(Prop))));
        _w.EndStep();
        Run();
        Assert.All(Three, h => Assert.True(_w.Has(Prop, h)));
        Assert.Equal(OfferStatus.Active, As(mod, () => Offers.Status(new EntityId(Prop))));
        Assert.Equal((OfferSequences.Touch, OfferVerbs.Talk), _w.ReadRuntime(Prop));
        Assert.Equal(32, _w.Entities[Prop][Offers.CheckPlayerInRange][0]);
        // another mod sees no offer of its own
        Assert.False(As(NewMod("b"), () => Offers.IsActive(new EntityId(Prop))));
    }

    [Fact]
    public void Refusals_are_the_same_as_kiln_emf()
    {
        var a = NewMod("a");
        var b = NewMod("b");
        _w.Spawn(1);
        _w.Players.Add(1);
        _w.Spawn(2, Offers.InteractionOfferRuntime);  // a chest
        Assert.Contains("never a player", As(a, () => Try(1)));
        Assert.Contains("own offer from the game", As(a, () => Try(2)));
        Assert.Contains("does not exist", As(a, () => Try(999)));
        Assert.Equal("", As(a, () => Try(Prop)));
        Assert.Contains("another mod's offer", As(b, () => Try(Prop)));
        Assert.Throws<InvalidOperationException>(() => As(b, () => Offers.Add(new EntityId(Prop), null, 0, _ => { })));
        // the same offer again: only the handler changes; a different one is refused
        Assert.Equal("", As(a, () => Try(Prop)));
        Assert.Contains("remove it first", As(a, () => Try(Prop, OfferVerbs.Open)));
        Assert.Throws<ArgumentException>(() => As(a, () => Offers.Add(new EntityId(Prop), "", 0, _ => { })));
    }

    private static string Try(uint e, uint verb = OfferVerbs.Use)
    {
        Offers.TryAdd(new EntityId(e), null, verb, _ => { }, out var why);
        return why;
    }

    [Fact]
    public void Remove_takes_off_only_what_kiln_added()
    {
        var mod = NewMod("a");
        _w.Spawn(Prop, Offers.CheckPlayerInRange);  // the template already has a range check
        As(mod, () => Offers.Add(new EntityId(Prop), "Ring the bell", 0, _ => { }));
        Assert.Equal(2, _w.Queue.Count);
        _w.EndStep();
        Run();
        Assert.True(As(mod, () => Offers.Remove(new EntityId(Prop))));
        Assert.Equal(OfferStatus.Removing, As(mod, () => Offers.Status(new EntityId(Prop))));
        _w.EndStep();
        Run();
        Assert.False(_w.Has(Prop, Offers.InteractionOfferRuntime));
        Assert.False(_w.Has(Prop, Offers.ClientInteractionOffer));
        Assert.True(_w.Has(Prop, Offers.CheckPlayerInRange));
        Assert.Equal(OfferStatus.None, As(mod, () => Offers.Status(new EntityId(Prop))));
        Assert.False(As(mod, () => Offers.Remove(new EntityId(Prop))));
        Assert.Empty(Offers.List());
    }

    [Fact]
    public void Remove_before_the_components_land_still_cleans_up()
    {
        var mod = NewMod("a");
        As(mod, () => Offers.Add(new EntityId(Prop), null, 0, _ => { }));
        Assert.True(As(mod, () => Offers.Remove(new EntityId(Prop))));
        _w.EndStep();  // the adds land
        Run(3);
        Assert.All(Three, h => Assert.False(_w.Has(Prop, h)));
        Assert.Empty(Offers.List());
    }

    [Fact]
    public void Accept_runs_the_handler_as_its_mod_and_only_while_the_offer_is_kilns()
    {
        var mod = NewMod("a");
        var got = new List<OfferAccepted>();
        ModContext? who = null;
        As(mod, () => Offers.Add(new EntityId(Prop), null, 0, a =>
        {
            got.Add(a);
            who = ModContext.CurrentMod;
        }));
        Offers.OnAccepted(7, Prop);  // still pending: ignored
        Assert.Empty(got);
        _w.EndStep();
        Run();
        Assert.True(Offers.Wanted);
        Offers.OnAccepted(7, Prop);
        Assert.Equal(new OfferAccepted(new EntityId(7), new EntityId(Prop)), Assert.Single(got));
        Assert.Same(mod, who);
        Assert.Equal(1, Offers.List()[0].Accepts);
        // a throwing handler is logged, not propagated
        As(mod, () => Offers.Add(new EntityId(Prop), null, 0, _ => throw new InvalidOperationException("boom")));
        Offers.OnAccepted(7, Prop);
        // the game replaced the runtime: not ours, no handler
        BitConverter.TryWriteBytes(_w.Entities[Prop][Offers.InteractionOfferRuntime].AsSpan(16), OfferVerbs.Open);
        got.Clear();
        As(mod, () => Offers.Add(new EntityId(Prop), null, 0, a => got.Add(a)));
        Offers.OnAccepted(7, Prop);
        Assert.Empty(got);
    }

    [Fact]
    public void Unload_takes_the_mods_offers_off()
    {
        var mod = NewMod("a");
        _w.Spawn(501);
        As(mod, () =>
        {
            Offers.Add(new EntityId(Prop), null, 0, _ => { });
            Offers.Add(new EntityId(501), null, 0, _ => { });
        });
        _w.EndStep();
        Run();
        mod.Retire();
        _w.EndStep();
        Run();
        foreach (var e in new uint[] { Prop, 501 })
            Assert.All(Three, h => Assert.False(_w.Has(e, h)));
        Assert.Empty(Offers.List());
        Assert.False(Offers.Wanted);
    }

    [Fact]
    public void Gone_entities_and_lost_offers_are_dropped()
    {
        var mod = NewMod("a");
        As(mod, () => Offers.Add(new EntityId(Prop), null, 0, _ => { }));
        _w.EndStep();
        Run();
        _w.Entities.Remove(Prop);
        Run(Offers.UpkeepSteps);
        Assert.Empty(Offers.List());
        Assert.True(Backend.Logged("dropped: the entity is gone"));

        // the engine ignores the adds: the offer is lost and logged
        _w.Spawn(600);
        _w.Ignore = true;
        As(mod, () => Offers.Add(new EntityId(600), null, 0, _ => { }));
        Run(Offers.VerifySteps + 2);
        Assert.True(Backend.Logged("did not land"));
        Assert.False(As(mod, () => Offers.IsActive(new EntityId(600))));
        Assert.Empty(Offers.List());
    }

    [Fact]
    public void Runtime_bytes_and_custom_verbs_match_emf()
    {
        var s = Offers.SpecOf("Ring", 0, new OfferOptions { LowPriority = true, BuildMode = true, RangeMeters = 5 });
        var b = Offers.RuntimeBytes(s);
        Assert.Equal(56, b.Length);
        Assert.Equal(OfferSequences.Touch, BitConverter.ToUInt32(b, 8));
        Assert.Equal(Offers.CustomVerbId("Ring"), BitConverter.ToUInt32(b, 16));
        Assert.Equal(1, b[47]);
        Assert.Equal(1, b[48]);
        Assert.Equal(5, s.Range);
        Assert.Equal(OfferVerbs.Use, Offers.SpecOf(null, 0, null).Verb);
        // FNV-1a-32 of "emf.verb:" (empty suffix) computed independently
        var h = 2166136261u;
        foreach (var c in "emf.verb:Ring"u8) h = (h ^ c) * 16777619u;
        Assert.Equal(h, Offers.CustomVerbId("Ring"));
    }
}
