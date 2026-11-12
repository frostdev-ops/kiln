// SPDX-License-Identifier: MIT
// The public InstanceData API: declaring keys, the error rule of contract.md as PLAN D4 spells it, the capability, the
// events, and the admin command bound late to the real Kiln.Chat.
using System.Reflection;
using Xunit;
using static Kiln.InstanceHarness;

namespace Kiln;

[Collection("instance data")]
public sealed class InstanceApiTests : IDisposable
{
    private readonly InstanceHarness _h = new();
    private static readonly InstanceFields Ring = Rec.Of(Rec.Ring, level: 2);

    public void Dispose() => _h.Dispose();

    private ItemInstanceRef NewRing(int slot = 3) => _h.World.Give(_h.World.Holders.Values.FirstOrDefault()?.Entity ?? _h.World.Chest(ChestTemplate, A), slot, Ring);

    [Fact]
    public void TheSameNameReturnsTheSameKeyAndAnotherTypeOrOptionsThrow()
    {
        Assert.Same(_h.Kills, _h.Declare<Kills>("kills"));
        Assert.Same(_h.Kills, _h.Declare<Kills>("kills", new InstanceKeyOptions()));
        Assert.Equal(("kills", "sword-mod", Persist.World), (_h.Kills.Name, _h.Kills.Mod, _h.Kills.Persist));
        Assert.Throws<ArgumentException>(() => _h.Declare<Note>("kills"));
        Assert.Throws<ArgumentException>(() => _h.Declare<Kills>("kills", new InstanceKeyOptions { Persist = Persist.Session }));
        Assert.Throws<ArgumentException>(() => _h.Declare<Kills>("kills", new InstanceKeyOptions { Items = [Rec.Ring] }));
        var only = _h.Declare<Note>("only", new InstanceKeyOptions { Items = [Rec.Ring, Rec.Can] });
        Assert.Same(only, _h.Declare<Note>("only", new InstanceKeyOptions { Items = [Rec.Can, Rec.Ring] }));
        Assert.Throws<ArgumentException>(() => _h.Declare<Kills>(""));
        Assert.Throws<ArgumentException>(() => _h.Declare<Kills>(new string('k', 201)));
        Assert.Throws<ArgumentException>(() => _h.Declare<Kills>("bad", new InstanceKeyOptions { Persist = (Persist)7 }));
        Assert.Throws<ArgumentException>(() => _h.Declare<Kills>("none", new InstanceKeyOptions { Items = [] }));
    }

    [Fact]
    public void CarriedIsRefusedByName()
    {
        var e = Assert.Throws<KilnUnavailableException>(() => _h.Declare<Kills>("travels", new InstanceKeyOptions { Persist = Persist.Carried }));
        Assert.Equal("saves.instance_data.carried", e.Capability);
        Assert.Contains("Persist.World", e.Reason);
        Assert.Contains("player's inventory", e.Reason);
        Assert.Contains("not available yet", e.Reason);
    }

    [Fact]
    public void AKeyNeedsAModsCode()
    {
        TestMods.CurrentMod = null;
        Assert.Throws<InvalidOperationException>(() => InstanceData.Key<Kills>("kills"));
    }

    [Fact]
    public void KeysBelongToTheModThatDeclaredThem()
    {
        var other = TestMods.Create("other-mod");
        var theirs = _h.Declare<Kills>("kills", mod: other);
        Assert.NotSame(_h.Kills, theirs);
        var ring = NewRing();
        _h.Tag(ring, 1);
        Assert.False(TestMods.As(other, () => ring.Has(theirs)));
        TestMods.As(other, () => ring.Set(theirs, new Kills { Count = 2 }));
        Assert.Equal(1, _h.Count(ring));
        Assert.Equal(2, TestMods.As(other, () => ring.Get(theirs).Count));
    }

    [Fact]
    public void ANoneRefOrARefWithoutAStampThrowsArgumentExceptionEverywhere()
    {
        var k = _h.Kills;
        foreach (var bad in new[] { ItemInstanceRef.None, new ItemInstanceRef(new EntityRef(new EntityId(77), 0), Rec.Ring) })
            TestMods.As(_h.Mod, () =>
            {
                Assert.Throws<ArgumentException>(() => bad.Get(k));
                Assert.Throws<ArgumentException>(() => bad.TryGet(k, out _));
                Assert.Throws<ArgumentException>(() => bad.Has(k));
                Assert.Throws<ArgumentException>(() => bad.Set(k, new Kills()));
                Assert.Throws<ArgumentException>(() => bad.Remove(k));
            });
    }

    [Fact]
    public void AnItemTypeTheKeyDoesNotAllowThrows()
    {
        var cans = _h.Declare<Note>("water", new InstanceKeyOptions { Items = [Rec.Can] });
        var ring = NewRing();
        var can = _h.World.Give(_h.World.Holders.Values.First().Entity, 4, Rec.Of(Rec.Can, fuel: 0, fuelMax: 6400));
        TestMods.As(_h.Mod, () =>
        {
            Assert.Throws<ArgumentException>(() => ring.Get(cans));
            Assert.Throws<ArgumentException>(() => ring.Set(cans, new Note()));
            Assert.Throws<ArgumentException>(() => ring.TryGet(cans, out _));
            can.Get(cans).Text = "full";
            Assert.Equal("full", can.Get(cans).Text);
        });
        Assert.Null(Store.Instances.LiveOf(ring));   // a refused call leaves nothing behind
    }

    [Fact]
    public void NullKeyOrValueThrowArgumentNullException()
    {
        var ring = NewRing();
        TestMods.As(_h.Mod, () =>
        {
            Assert.Throws<ArgumentNullException>(() => ring.Get<Kills>(null!));
            Assert.Throws<ArgumentNullException>(() => ring.Set(_h.Kills, null!));
            Assert.Throws<ArgumentNullException>(() => InstanceData.All<Kills>(null!));
        });
    }

    [Fact]
    public void TheCapabilityOffThrowsKilnUnavailableExceptionNamingIt()
    {
        var ring = NewRing();
        Store.Instances.World = null;
        var e = Assert.Throws<KilnUnavailableException>(() => _h.Get(ring));
        Assert.Equal("saves.instance_data", e.Capability);
        Assert.Contains("items.instances", e.Reason);
        Store.Instances.World = _h.World;
        _h.World.Off = true;
        e = Assert.Throws<KilnUnavailableException>(() => _h.Count(ring));
        Assert.Equal("saves.instance_data", e.Capability);
        Assert.Equal("items.instances", Assert.IsType<KilnUnavailableException>(e.InnerException).Capability);
        _h.World.Off = false;
        var state = typeof(SavesRuntime).GetField("_state", BindingFlags.NonPublic | BindingFlags.Static)!;
        state.SetValue(null, 2);
        try
        {
            Assert.Equal("saves.instance_data", Assert.Throws<KilnUnavailableException>(() => TestMods.As(_h.Mod, () => InstanceData.Parked(_h.Kills))).Capability);
            Assert.Same(_h.Kills, _h.Declare<Kills>("kills"));   // declaring needs nothing
        }
        finally
        {
            state.SetValue(null, 1);
        }
    }

    [Fact]
    public void TryGetHasRemoveAndTheLastRemoveStopsFollowing()
    {
        var ring = NewRing();
        Assert.Null(_h.Count(ring));
        Assert.False(TestMods.As(_h.Mod, () => ring.Has(_h.Kills)));
        Assert.Null(Store.Instances.LiveOf(ring));       // asking does not create anything
        _h.Tag(ring, 4);
        Assert.True(_h.World.IsFollowed(ring));
        Assert.True(TestMods.As(_h.Mod, () => ring.Has(_h.Kills)));
        Assert.Equal(4, _h.Count(ring));
        Assert.True(TestMods.As(_h.Mod, () => ring.Remove(_h.Kills)));
        Assert.False(TestMods.As(_h.Mod, () => ring.Remove(_h.Kills)));
        Assert.False(_h.World.IsFollowed(ring));
        Assert.Null(Store.Instances.LiveOf(ring));
    }

    [Fact]
    public void GetGivesALiveObjectThatIsSaved()
    {
        var ring = NewRing();
        var k = _h.Get(ring);
        k.Count = 5;
        Assert.Same(k, _h.Get(ring));
        Assert.Equal([(ring, 5)], TestMods.As(_h.Mod, () => InstanceData.All(_h.Kills)).Select(x => (x.Item, x.Value.Count)));
        _h.Save(1);
        Assert.Equal(5, _h.Bundle(1, "instances/sword-mod")!["holders"]![0]!["records"]![0]!["keys"]!["kills"]!["Count"]!.GetValue<int>());
    }

    [Fact]
    public void AStoredValueThatDoesNotReadAsTheTypeThrowsInvalidDataExceptionAndIsKept()
    {
        var ring = NewRing();
        TestMods.As(_h.Mod, () => ring.Get(_h.Declare<Note>("text")).Text = "words");
        var wrong = _h.Declare<Kills>("text2");
        var live = Store.Instances.LiveOf(ring)!;
        live.Values.ByMod["sword-mod"]["text2"] = new InstanceSlot { Json = "{\"Count\":\"not a number\"}" };
        Assert.Throws<InvalidDataException>(() => TestMods.As(_h.Mod, () => ring.Get(wrong)));
        Assert.Equal("{\"Count\":\"not a number\"}", live.Values.ByMod["sword-mod"]["text2"].Json);
    }

    [Fact]
    public void RestoredEventsNameTheModTheKeyAndHowItMatched()
    {
        var ring = NewRing();
        _h.Tag(ring, 1);
        TestMods.As(_h.Mod, () => ring.Get(_h.Declare<Note>("text")).Text = "x");
        _h.Save(1);
        _h.Restart(1, FakeInstanceWorld.Load(_h.World.SaveGame()));
        var restored = _h.Of<InstanceDataRestoredEvent>();
        Assert.Equal(["kills", "text"], restored.Select(e => e.Key).Order());
        Assert.All(restored, e => Assert.Equal(("sword-mod", InstanceMatch.Exact), (e.Mod, e.Match)));
        Assert.All(restored, e => Assert.Equal(_h.World.At(_h.World.HolderAt(ChestTemplate, A), 3), e.Item));
    }

    [Fact]
    public void ParkedListsWaitingRecordsWithTheirReasonAsCopies()
    {
        var ring = NewRing();
        _h.Tag(ring, 6);
        _h.Save(1);
        _h.Restart(1, FakeInstanceWorld.Load(new FakeInstanceWorld.Snapshot([], [])));
        var p = Assert.Single(_h.Parked());
        Assert.Equal((ParkedState.Waiting, 3, Rec.Ring, 6), (p.State, p.FlatSlot, p.Item, p.Value!.Count));
        Assert.Contains(ChestTemplate.ToString(), p.Holder);
        p.Value.Count = 99;
        Assert.Equal(6, _h.Parked()[0].Value!.Count);
    }

    [Fact]
    public void CharacterKeyConvertsToAPlayerRefWithTheCharacterOnly()
    {
        var r = (PlayerRef)Hero;
        Assert.Equal((0UL, Hero.Id), (r.SteamId, r.CharacterId));
        Assert.True(r.Entity.IsNone);
    }

    [Fact]
    public void TheListingShowsAttachedWaitingAndSetAside()
    {
        var w = _h.World;
        var chest = w.Chest(ChestTemplate, A);
        _h.Tag(w.Give(chest, 3, Ring), 1);
        _h.Tag(w.Give(chest, 4, Rec.Of(Rec.Sword)), 2);
        _h.Tag(w.Give(chest, 5, Rec.Of(Rec.Sword)), 3);
        _h.Save(1);
        var snap = w.SaveGame();
        // older generation and the swords moved: the ring waits nowhere, the swords are set aside
        var changed = new FakeInstanceWorld.Snapshot([(snap.World[0].Holder, [(4, Rec.Of(Rec.Sword)), (6, Rec.Of(Rec.Sword))])], []);
        _h.Restart(2, FakeInstanceWorld.Load(changed));
        _h.Tag(_h.World.Give(_h.World.HolderAt(ChestTemplate, A), 9, Ring), 7);
        var lines = Store.Instances.Listing();
        Assert.Contains(lines, l => l.StartsWith("sword-mod:"));
        Assert.Contains(lines, l => l.TrimStart().StartsWith("attached") && l.Contains("slot 9"));
        Assert.Contains(lines, l => l.TrimStart().StartsWith("waiting") && l.Contains("slot 3"));
        Assert.Equal(2, lines.Count(l => l.TrimStart().StartsWith("set aside")));
        var filtered = Store.Instances.Listing(entity: new EntityId(424242));
        Assert.Equal("nothing for that holder", filtered[^1]);
    }
}

[Collection("instance data")]
public sealed class InstanceCommandTests : IDisposable
{
    private readonly InstanceHarness _h = new();

    public void Dispose()
    {
        Commands.Unregister(InstanceCommand.Name);
        InstanceCommand.ResetForTests();
        _h.Dispose();
    }

    [Fact]
    public void TheCommandIsBoundLateToTheRealKilnChatAsKilnsAdminCommand()
    {
        Commands.Unregister(InstanceCommand.Name);
        InstanceCommand.ResetForTests();
        _ = typeof(Commands).Assembly;  // Kiln.Chat is loaded, as the first service tick does in a game
        Assert.True(InstanceCommand.TryRegister(final: true));
        var info = Commands.Find("instances")!;
        Assert.Equal(("kiln", CommandPermission.Admin), (info.Owner, info.Permission));
        var ring = _h.World.Give(_h.World.Chest(ChestTemplate, A), 3, Rec.Of(Rec.Ring));
        _h.Tag(ring, 5);
        var all = Commands.Execute(CommandSource.Console, "/instances");
        Assert.True(all.Ok, all.Text);
        Assert.Contains(all.Output, l => l.StartsWith("instance data:"));
        Assert.Contains(all.Output, l => l.Contains("attached") && l.Contains(ring.ToString()));
        var one = Commands.Execute(CommandSource.Console, $"/instances {ring.Entity.Id.Value}");
        Assert.True(one.Ok, one.Text);
        Assert.Contains(one.Output, l => l.Contains(ring.ToString()));
        var none = Commands.Execute(CommandSource.Console, "/instances 999999");
        Assert.Equal("nothing for that holder", none.Output[^1]);
        var bad = Commands.Execute(CommandSource.Console, "/instances nobody-here");
        Assert.False(bad.Ok);
        Assert.Contains("no player or entity", bad.Error);
        Assert.True(Commands.Execute(CommandSource.ForSteamId(76561198000000001, "Guest"), "/instances").Denied);
    }

    [Fact]
    public void DeclaringTheFirstKeyRegistersTheCommand()
    {
        Commands.Unregister(InstanceCommand.Name);
        InstanceCommand.ResetForTests();
        _h.Declare<Note>("another");
        Assert.NotNull(Commands.Find("instances"));
    }
}
