// SPDX-License-Identifier: MIT
// Instance data on players' games (InstanceSync.cs): the server's passes against the real store and the fake game, with a
// loopback in place of kiln.net. Every update the server makes is written by the real encoder, read by the real decoder
// and taken by a real client store, one per player, so what a test reads is what a player's game would hold; the games'
// acknowledgements go back the same way, after the pass, as they would arrive from the network. The channel itself
// (Kiln.Network, the handshake, lab peers) is the lab test's part (mods/tests/instance-sync-test).
#define SYNC_ACKS
#define SYNC_BACKOFF
using Xunit;
using static Kiln.InstanceHarness;

namespace Kiln;

/// <summary>Stands in for kiln.net and kiln.players: the games that take updates, where the players and the holders stand,
/// and the wire.</summary>
internal sealed class FakeSyncLink : IInstanceSyncLink
{
    public sealed class Game
    {
        public readonly InstanceSyncClient Store = new();
        public readonly List<InstanceDataSyncedEvent> Events = [];
        public readonly List<(SyncUpdate Update, int Bytes)> Updates = [];

        /// <summary>Updates the network took and then lost.</summary>
        public int Lost;
    }

    public readonly List<SyncViewer> Now = [];
    public readonly Dictionary<uint, WorldPosition> Positions = new();
    public readonly Dictionary<ulong, Game> Games = new();
    public bool Refuse;

    /// <summary>The network takes the next this many updates (Send says true) and loses them, as kiln.net's retry queue
    /// does with a frame it could not hand to the game within two seconds (native/services/net/net.cpp).</summary>
    public int Lose;

    public int MaxUpdateBytes { get; set; } = 256 * 1024;

    /// <summary>Where a holder that is not a connected player stands now (the fake world's).</summary>
    public Func<EntityId, WorldPosition?>? HolderAt;

#if SYNC_ACKS
    /// <summary>The games' acknowledgements, as they come back over the network: delivered after the pass.</summary>
    public readonly List<(ulong Viewer, SyncAck Ack)> Acks = [];

    /// <summary>The acknowledgements are lost on the way (the game took the updates, the server never hears of it).</summary>
    public bool LoseAcks;
#endif

    public IReadOnlyList<SyncViewer> Viewers() => Now.Select(v => v with { Position = Positions.TryGetValue(v.Player.Value, out var p) ? p : null }).ToList();

    public WorldPosition? PositionOf(EntityId holder) => Positions.TryGetValue(holder.Value, out var p) ? p : HolderAt?.Invoke(holder);

    public bool Send(in SyncViewer viewer, SyncUpdate update)
    {
        if (Refuse) return false;
        var w = new NetWriter();
        update.Encode(w);
        Assert.True(w.Length <= MaxUpdateBytes, $"an update of {w.Length} bytes for a channel that carries {MaxUpdateBytes}");
        var game = Games[viewer.Id];
        if (Lose > 0)
        {
            Lose--;
            game.Lost++;
            return true;  // taken, never delivered
        }
        var r = new NetReader(w.WrittenSpan);
        var read = SyncUpdate.Decode(ref r);
        Assert.True(r.End);
        game.Updates.Add((read, w.Length));
#if SYNC_ACKS
        game.Events.AddRange(game.Store.Apply(read, out var ack));
        if (ack is not null && !LoseAcks)
        {
            var aw = new NetWriter();
            ack.Encode(aw);
            var ar = new NetReader(aw.WrittenSpan);
            Acks.Add((viewer.Id, SyncAck.Decode(ref ar)));
        }
#else
        game.Events.AddRange(game.Store.Apply(read));
#endif
        return true;
    }

    /// <summary>The acknowledgements of the pass reach the server.</summary>
    public void Deliver(InstanceSyncServer server)
    {
#if SYNC_ACKS
        foreach (var (viewer, ack) in Acks) server.Ack(viewer, ack);
        Acks.Clear();
#endif
    }

    /// <summary>A player's game connects: it takes updates from the next pass on.</summary>
    public Game Connect(ulong id, EntityId player, WorldPosition? at = null)
    {
        Now.RemoveAll(v => v.Id == id);
        Now.Add(new SyncViewer(id, player, null, $"game-{id}"));
        if (at is { } p) Positions[player.Value] = p;
        return Games[id] = new Game();
    }

    public void Disconnect(ulong id) => Now.RemoveAll(v => v.Id == id);
}

[Collection("instance data")]
public sealed class InstanceSyncTests : IDisposable
{
    private static readonly InstanceFields Sword = Rec.Of(Rec.Sword, level: 7), Ring = Rec.Of(Rec.Ring, level: 2);
    private static readonly CharacterKey Friend = new(0x2233445566778899, Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-000000000002"));

    private readonly InstanceHarness _h = new();
    private readonly FakeSyncLink _link = new();
    private readonly InstanceSyncServer _server;
    private readonly InstanceKey<Kills> _mine, _shown, _secret;
    private readonly EntityId _hero, _friend, _chest;
    private long _clock = 1_000_000;

    public InstanceSyncTests()
    {
        _server = NewServer();
        // a holder that is no connected player stands where the fake world has it now (an NPC walks; a chest stays)
        _link.HolderAt = e => _h.World.Holders.TryGetValue(e.Value, out var h) && h.Identity.Kind == HolderKind.World ? h.Identity.Position : null;
        _mine = _h.Declare<Kills>("mine", new InstanceKeyOptions { Sync = InstanceSync.Holder });
        _shown = _h.Declare<Kills>("shown", new InstanceKeyOptions { Sync = InstanceSync.Nearby, NearbyRadius = 10 });
        _secret = _h.Kills;  // declared by the harness without Sync: it stays on the server
        _hero = _h.World.Join(Hero);
        _friend = _h.World.Join(Friend);
        _chest = _h.World.Chest(ChestTemplate, A);
    }

    public void Dispose() => _h.Dispose();

#if SYNC_ACKS
    private InstanceSyncServer NewServer() => new(Store.Instances, _link, () => _clock);
#else
    private InstanceSyncServer NewServer() => new(Store.Instances, _link);
#endif

    /// <summary>One pass of <paramref name="server"/> (the test's own by default) a quarter of a second after the last, as
    /// the runtime's timer makes them, then the games' acknowledgements.</summary>
    private int Pass(InstanceSyncServer? server = null)
    {
        _clock += 250;
        server ??= _server;
        var n = server.Pass();
        _link.Deliver(server);
        return n;
    }

    private void Set(ItemInstanceRef item, InstanceKey<Kills> key, int count) => TestMods.As(_h.Mod, () => item.Set(key, new Kills { Count = count }));

    private int? Held(FakeSyncLink.Game game, ItemInstanceRef item, InstanceKey<Kills> key) =>
        game.Store.Find(item.Entity.Id) is { } i && i.TryGet(key, out var k) ? k.Count : null;

    /// <summary>A hand-made update with its number and the number of the one before it.</summary>
    private static SyncUpdate Numbered(SyncUpdate u, uint seq, uint prev = 0)
    {
#if SYNC_ACKS
        u.Seq = seq;
        u.Prev = prev;
#endif
        return u;
    }

    private static string Text(FakeSyncLink.Game game) => string.Join("; ", game.Events.Select(e => $"{e.Change} {e.Key} {e.Item.Entity}"));

    [Fact]
    public void TheHolderGetsItsValuesAndNobodyElseDoes()
    {
        var sword = _h.World.Give(_hero, 3, Sword);
        Set(sword, _mine, 4);
        Set(sword, _secret, 9);
        var hero = _link.Connect(1, _hero);
        var friend = _link.Connect(2, _friend);
        Pass();

        Assert.Equal(4, Held(hero, sword, _mine));
        var item = Assert.Single(hero.Store.All());
        Assert.Equal((sword.Entity.Id, Rec.Sword, _hero, 3), (item.Entity, item.Item, item.Holder, item.FlatSlot));
        Assert.Equal([("sword-mod", "mine")], item.Keys);       // the key without Sync is not there
        Assert.Null(item.Json("sword-mod", "kills"));
        Assert.True(item.Has(_mine));
        Assert.False(item.Has(_secret));
        Assert.Empty(friend.Store.All());                        // another player holds nothing of it
        Assert.DoesNotContain(friend.Updates, u => u.Update.Items.Count > 0);
        Assert.Contains(hero.Updates, u => u.Update.Reset);      // the first update starts the game's copies over
        var ev = Assert.Single(hero.Events);
        Assert.Equal((InstanceSyncChange.Set, "sword-mod", "mine"), (ev.Change, ev.Mod, ev.Key));
        Assert.Same(item, ev.Item);
    }

    [Fact]
    public void AKeyWithoutSyncNeverLeavesTheServer()
    {
        var sword = _h.World.Give(_hero, 3, Sword);
        Set(sword, _secret, 9);
        var hero = _link.Connect(1, _hero, A);
        _link.Connect(2, _friend, A);
        for (var i = 0; i < 3; i++) Pass();
        Assert.All(_link.Games.Values, g => Assert.Empty(g.Store.All()));
        Assert.All(_link.Games.Values, g => Assert.DoesNotContain(g.Updates, u => u.Update.Items.Count > 0));
        Assert.Single(hero.Updates);  // the greeting, and nothing after it
    }

    [Fact]
    public void ChangesReachTheHolder()
    {
        var sword = _h.World.Give(_hero, 3, Sword);
        Set(sword, _mine, 1);
        var hero = _link.Connect(1, _hero);
        Pass();
        hero.Events.Clear();

        Set(sword, _mine, 2);                                        // Set
        Pass();
        Assert.Equal(2, Held(hero, sword, _mine));

        TestMods.As(_h.Mod, () => sword.Get(_mine).Count = 3);        // the live object, changed in place
        Pass();
        Assert.Equal(3, Held(hero, sword, _mine));

        var before = hero.Updates.Count;
        Pass();                                              // nothing changed: nothing sent
        Assert.Equal(before, hero.Updates.Count);

        Set(sword, _shown, 8);                                       // a second key on the same item
        Pass();
        Assert.Equal((3, 8), (Held(hero, sword, _mine), Held(hero, sword, _shown)));

        TestMods.As(_h.Mod, () => Assert.True(sword.Remove(_mine)));  // Remove
        Pass();
        Assert.Null(Held(hero, sword, _mine));
        Assert.Equal(8, Held(hero, sword, _shown));

        TestMods.As(_h.Mod, () => Assert.True(sword.Remove(_shown)));
        Pass();
        Assert.Empty(hero.Store.All());                              // the last value went: the game holds no copy of the item any more
        Assert.Equal("Set mine " + sword.Entity.Id + "; Set mine " + sword.Entity.Id + "; Set shown " + sword.Entity.Id +
                     "; Removed mine " + sword.Entity.Id + "; Gone shown " + sword.Entity.Id, Text(hero));
    }

    [Fact]
    public void AnItemInAChestIsNobodysUnlessItsKeySaysNearby()
    {
        var kept = _h.World.Give(_chest, 0, Sword);
        var seen = _h.World.Give(_chest, 1, Ring);
        Set(kept, _mine, 1);    // Holder: a chest is no player
        Set(seen, _shown, 2);   // Nearby, 10 m
        var near = _link.Connect(1, _hero, new WorldPosition(A.X + 6, A.Y, A.Z));
        var far = _link.Connect(2, _friend, new WorldPosition(A.X + 30, A.Y, A.Z));
        Pass();
        Assert.Equal(2, Held(near, seen, _shown));
        Assert.Null(near.Store.Find(kept.Entity.Id));
        Assert.Empty(far.Store.All());

        _link.Positions[_hero.Value] = new WorldPosition(A.X + 11, A.Y, A.Z);   // walks out of range
        _link.Positions[_friend.Value] = new WorldPosition(A.X + 9, A.Y, A.Z);  // walks in
        Pass();
        Assert.Empty(near.Store.All());
        Assert.Equal(InstanceSyncChange.Gone, near.Events[^1].Change);
        Assert.Equal(2, Held(far, seen, _shown));
    }

    [Fact]
    public void APlayerWithoutAKnownPositionGetsNothingAsNearby()
    {
        var ring = _h.World.Give(_chest, 1, Ring);
        Set(ring, _shown, 2);
        var game = _link.Connect(1, _hero);  // no position: the server does not know where it stands
        Pass();
        Assert.Empty(game.Store.All());
    }

    [Fact]
    public void AnotherPlayersItemIsSeenNearbyWithItsNearbyValuesOnly()
    {
        var sword = _h.World.Give(_hero, 3, Sword);
        Set(sword, _mine, 5);
        Set(sword, _shown, 6);
        var hero = _link.Connect(1, _hero, A);
        var friend = _link.Connect(2, _friend, new WorldPosition(A.X + 4, A.Y, A.Z));
        Pass();
        Assert.Equal((5, 6), (Held(hero, sword, _mine), Held(hero, sword, _shown)));
        Assert.Equal(6, Held(friend, sword, _shown));
        Assert.Null(Held(friend, sword, _mine));                 // the holder's own value stays the holder's
        Assert.Null(friend.Store.Find(sword.Entity.Id)!.Json("sword-mod", "mine"));
        Assert.DoesNotContain(friend.Updates, u => u.Update.Items.Any(i => i.Values.Any(v => v.Key == "mine")));
    }

    [Fact]
    public void AMoveTakesTheCopyAlong()
    {
        var sword = _h.World.Give(_hero, 3, Sword);
        Set(sword, _mine, 5);
        var hero = _link.Connect(1, _hero);
        var friend = _link.Connect(2, _friend);
        Pass();
        hero.Events.Clear();

        _h.World.Move(sword, _hero, 12);                          // another slot of the same player
        Pass();
        Assert.Equal(12, hero.Store.Find(sword.Entity.Id)!.FlatSlot);
        Assert.Equal($"Moved mine {sword.Entity.Id}", Text(hero));
        Assert.Equal(5, Held(hero, sword, _mine));

        _h.World.Move(sword, _friend, 0);                         // a gift
        Pass();
        Assert.Empty(hero.Store.All());
        Assert.Equal(InstanceSyncChange.Gone, hero.Events[^1].Change);
        Assert.Equal(5, Held(friend, sword, _mine));
        Assert.Equal((_friend, 0), (friend.Store.Find(sword.Entity.Id)!.Holder, friend.Store.Find(sword.Entity.Id)!.FlatSlot));

        _h.World.Move(sword, _chest, 2);                          // into a chest: nobody's
        Pass();
        Assert.Empty(friend.Store.All());
    }

    [Fact]
    public void ADestroyedItemLeavesTheHoldersGame()
    {
        var sword = _h.World.Give(_hero, 3, Sword);
        Set(sword, _mine, 5);
        var hero = _link.Connect(1, _hero);
        Pass();
        _h.World.Destroy(sword);
        Pass();
        Assert.Empty(hero.Store.All());
        Assert.Equal((InstanceSyncChange.Gone, "mine"), (hero.Events[^1].Change, hero.Events[^1].Key));
        Assert.Equal(sword.Entity.Id, hero.Events[^1].Item.Entity);  // the event still says which item it was
    }

    [Fact]
    public void AClaimedRecordReachesThePlayersWhoMaySeeIt()
    {
        // two identical swords in a chest that changed while Kiln did not watch: both records set aside after the restart
        var w = _h.World;
        Set(w.Give(_chest, 4, Sword), _shown, 30);
        Set(w.Give(_chest, 5, Sword), _shown, 40);
        _h.Save(1);
        var snap = w.SaveGame();
        _h.Restart(2, FakeInstanceWorld.Load(new FakeInstanceWorld.Snapshot([(snap.World[0].Holder, [(4, Sword), (6, Sword)])], [])));
        var chest = _h.World.HolderAt(ChestTemplate, A);
        var parked = TestMods.As(_h.Mod, () => InstanceData.Parked(_shown));
        Assert.Equal(2, parked.Count);
        var server = NewServer();
        var game = _link.Connect(1, new EntityId(7), A);          // a player standing at the chest
        Pass(server);
        Assert.Empty(game.Store.All());                           // set aside: on no item, so on no game

        var target = _h.World.At(chest, 6);
        var claim = TestMods.As(_h.Mod, () => InstanceData.Claim(parked.Single(p => p.Value!.Count == 40), target));
        Assert.True(claim.Ok, claim.Reason);
        Pass(server);
        Assert.Equal(40, Held(game, target, _shown));
        Assert.Equal((chest, 6), (game.Store.Find(target.Entity.Id)!.Holder, game.Store.Find(target.Entity.Id)!.FlatSlot));
    }

    [Fact]
    public void ARejoinStartsOverWithTheWholeSet()
    {
        var sword = _h.World.Give(_hero, 3, Sword);
        var ring = _h.World.Give(_hero, 4, Ring);
        Set(sword, _mine, 5);
        Set(ring, _mine, 6);
        var first = _link.Connect(1, _hero);
        Pass();
        Assert.Equal(2, first.Store.All().Count);

        _link.Disconnect(1);
        Pass();                                           // the server forgets what it sent that game
        var again = _link.Connect(1, _hero);                      // the same connection id, a new game
        Pass();
        Assert.True(again.Updates[0].Update.Reset);
        Assert.Equal((5, 6), (Held(again, sword, _mine), Held(again, ring, _mine)));
    }

    [Fact]
    public void AConnectionThatStandsForAnotherPlayerStartsOver()
    {
        var sword = _h.World.Give(_hero, 3, Sword);
        Set(sword, _mine, 5);
        var game = _link.Connect(1, _hero);
        Pass();
        Assert.Single(game.Store.All());
        // the same connection, now the other player (the same game came back with another character in that slot)
        _link.Now[0] = _link.Now[0] with { Player = _friend };
        Pass();
        Assert.True(game.Updates[^1].Update.Reset);
        Assert.Empty(game.Store.All());                           // nothing of the first player's stays behind
    }

    [Fact]
    public void ValuesComeBackToTheHoldersGameAfterARejoin()
    {
        var w = _h.World;
        var sword = w.Give(_hero, 3, Sword);
        Set(sword, _mine, 5);
        _h.Tick();
        var game = _link.Connect(1, _hero);
        Pass();
        Assert.Equal(5, Held(game, sword, _mine));
        _h.Save(1);
        w.SendCharacter(_hero);
        w.Crash(_hero);                                           // the connection is lost: no leave save
        _link.Disconnect(1);
        _h.Tick();
        Pass();
        var back = w.Join(Hero);                                  // the character comes back with new item entities
        _h.Tick();
        var again = _link.Connect(1, back);
        Pass();
        var item = Assert.Single(again.Store.All());
        Assert.NotEqual(sword.Entity.Id, item.Entity);
        Assert.Equal((back, 3), (item.Holder, item.FlatSlot));
        Assert.True(item.TryGet(_mine, out var kills));
        Assert.Equal(5, kills.Count);
    }

    [Fact]
    public void WhatDoesNotFitOneUpdateFollowsInTheNext()
    {
        // five values of 40 KB: 96 KB of values per update, so two, two and one
        var big = _h.Declare<Note>("story", new InstanceKeyOptions { Sync = InstanceSync.Holder });
        var items = new List<ItemInstanceRef>();
        for (var i = 0; i < 5; i++)
        {
            var item = _h.World.Give(_hero, i, Rec.Of(Rec.Sword, level: i + 1));
            items.Add(item);
            var text = new string((char)('a' + i), 40_000);
            TestMods.As(_h.Mod, () => item.Set(big, new Note { Text = text }));
        }
        var game = _link.Connect(1, _hero);
        var passes = 0;
        while (game.Store.All().Count < 5 && passes < 10)
        {
            Pass();
            passes++;
        }
        Assert.Equal(3, passes);
        Assert.Equal([2, 2, 1], game.Updates.Select(u => u.Update.Items.Count));
        Assert.All(game.Updates, u => Assert.True(u.Bytes <= InstanceSyncServer.UpdateBudgetBytes + 1024, $"{u.Bytes} bytes"));
        for (var i = 0; i < 5; i++)
        {
            Assert.True(game.Store.Find(items[i].Entity.Id)!.TryGet(big, out var note));
            Assert.Equal(new string((char)('a' + i), 40_000), note.Text);  // nothing dropped, nothing cut
        }
        var before = game.Updates.Count;
        Pass();
        Assert.Equal(before, game.Updates.Count);
    }

    [Fact]
    public void TheLargestValueTheStoreKeepsStillTravels()
    {
        var big = _h.Declare<Note>("story", new InstanceKeyOptions { Sync = InstanceSync.Holder });
        var sword = _h.World.Give(_hero, 3, Sword);
        var text = new string('x', InstanceStore.MaxValueBytes - 64);  // the JSON is just under the store's 64 KB cap
        TestMods.As(_h.Mod, () => sword.Set(big, new Note { Text = text }));
        Assert.Throws<ArgumentException>(() => TestMods.As(_h.Mod, () => sword.Set(big, new Note { Text = new string('x', InstanceStore.MaxValueBytes) })));
        var game = _link.Connect(1, _hero);
        Pass();
        Assert.True(game.Store.Find(sword.Entity.Id)!.TryGet(big, out var note));
        Assert.Equal(text.Length, note.Text.Length);
    }

    [Fact]
    public void AValueLargerThanTheChannelCarriesStaysOnTheServer()
    {
        // an operator lowered network.json maxMessageBytes: 8 KB per update, everything in it counted
        _link.MaxUpdateBytes = 8 * 1024;
        var big = _h.Declare<Note>("story", new InstanceKeyOptions { Sync = InstanceSync.Holder });
        var sword = _h.World.Give(_hero, 3, Sword);
        TestMods.As(_h.Mod, () => sword.Set(big, new Note { Text = new string('x', 9000) }));
        Set(sword, _mine, 5);
        var game = _link.Connect(1, _hero);
        for (var i = 0; i < 3; i++) Pass();
        var item = Assert.Single(game.Store.All());
        Assert.Equal(5, Held(game, sword, _mine));        // the small value goes
        Assert.False(item.Has(big));                      // the large one never does, and nothing blocks behind it
        Assert.Single(game.Updates);                      // the first update with the small value, and no retries of the large one
    }

    [Fact]
    public void AnUpdateThatDidNotGoOutIsSentAgain()
    {
        var sword = _h.World.Give(_hero, 3, Sword);
        Set(sword, _mine, 5);
        var game = _link.Connect(1, _hero);
        _link.Refuse = true;
        Assert.Equal(0, Pass());
        Assert.Empty(game.Store.All());
        Assert.Equal(1, _server.Unsent);
        _link.Refuse = false;
        Assert.Equal(1, Pass());
        Assert.True(game.Updates[0].Update.Reset);        // still the first one: nothing was counted as sent
        Assert.Equal(5, Held(game, sword, _mine));
    }

    [Fact]
    public void AModThatUnloadsTakesItsValuesOffPlayersGames()
    {
        var other = TestMods.Create("ring-mod");
        var theirs = _h.Declare<Kills>("glow", new InstanceKeyOptions { Sync = InstanceSync.Holder }, other);
        var ring = _h.World.Give(_hero, 4, Ring);
        TestMods.As(other, () => ring.Set(theirs, new Kills { Count = 2 }));
        var game = _link.Connect(1, _hero);
        Pass();
        Assert.Equal("{\"Count\":2,\"Tag\":null}", game.Store.Find(ring.Entity.Id)!.Json("ring-mod", "glow"));
        TestMods.Retire(other);   // its key is gone: nothing says who may see the value, so nobody does
        Pass();
        Assert.Empty(game.Store.All());
        Assert.False(Store.Instances.HasSyncedKeys && Store.Instances.KeyOf("ring-mod", "glow") is not null);
    }

    [Fact]
    public void NoPlayersGameMeansNoWork()
    {
        var sword = _h.World.Give(_hero, 3, Sword);
        Set(sword, _mine, 5);
        Assert.Equal(0, Pass());
        Assert.Equal(0, _server.ViewerCount);
    }

    [Fact]
    public void AnItemThatIsPlacedNowhereIsNobodys()
    {
        var sword = _h.World.Give(_hero, 3, Sword);
        Set(sword, _mine, 5);
        var game = _link.Connect(1, _hero);
        Pass();
        _h.World.DestroyPlayerEntity(_hero);  // the leave's teardown: for a step the item entities outlive the player entity
        _link.Disconnect(1);
        Assert.Equal(0, Pass());
        Assert.Equal(0, _server.ViewerCount);
        Assert.Single(game.Store.All());      // the game keeps its copy until its own connection ends (InstanceSyncRuntime)
    }

    // ---- review of 83ae1a98: where a holder stands, lost updates, large removals, the game's cap ----

    [Fact]
    public void NearbyMeansWhereTheHolderStandsNowNotWhereItLastPlacedTheItem()
    {
        // an NPC carries a ring with a Nearby value (10 m) and walks 100 m away; the ring does not move in its inventory, so
        // the store's record of the holder still says where it stood
        var npc = _h.World.Chest(ChestTemplate, B, mobile: true, name: "trader");
        var ring = _h.World.Give(npc, 0, Ring);
        Set(ring, _shown, 3);
        var there = _link.Connect(1, _hero, new WorldPosition(B.X + 3, B.Y, B.Z));
        Pass();
        Assert.Equal(3, Held(there, ring, _shown));

        var away = new WorldPosition(B.X + 100, B.Y, B.Z);
        var holder = _h.World.Holders[npc.Value];
        holder.Identity = holder.Identity with { Position = away };   // it walked
        Pass();
        Assert.Null(Held(there, ring, _shown));                       // the game at its old place loses the copy
        var late = _link.Connect(2, _friend, new WorldPosition(B.X + 2, B.Y, B.Z));
        Pass();
        Assert.Empty(late.Store.All());                               // a game that joins at the old place gets nothing
        _link.Positions[_friend.Value] = new WorldPosition(away.X + 4, away.Y, away.Z);
        Pass();
        Assert.Equal(3, Held(late, ring, _shown));                    // one beside where it stands now gets it

        _link.HolderAt = _ => null;                                   // where it stands cannot be read now: near nobody
        Pass();
        Assert.Empty(late.Store.All());
        Assert.Empty(there.Store.All());
    }

    [Fact]
    public void AChangeAndARemovalTheNetworkTookAndLostAreMadeGood()
    {
        var sword = _h.World.Give(_hero, 3, Sword);
        var ring = _h.World.Give(_hero, 4, Ring);
        Set(sword, _mine, 1);
        Set(ring, _mine, 1);
        var game = _link.Connect(1, _hero);
        Pass();
        Assert.Equal((1, 1), (Held(game, sword, _mine), Held(game, ring, _mine)));

        // a change, then a removal: the network takes both and loses them, and nothing follows them
        _link.Lose = 2;
        Set(sword, _mine, 2);
        Pass();
        TestMods.As(_h.Mod, () => Assert.True(ring.Remove(_mine)));
        Pass();
        Assert.Equal(2, game.Lost);
        for (var i = 0; i < 3; i++) Pass();
        Assert.Equal((1, 1), (Held(game, sword, _mine), Held(game, ring, _mine)));   // the game cannot know yet

        _clock += 10_000;                                             // ten seconds without an acknowledgement
        Pass();
        Assert.True(game.Updates[^1].Update.Reset);                   // the game starts over with the whole set
        Assert.Equal(2, Held(game, sword, _mine));
        Assert.Null(game.Store.Find(ring.Entity.Id));                 // and the removal holds
    }

    [Fact]
    public void ALostUpdateIsNoticedByTheNextOneAndTheGameStartsOver()
    {
        var sword = _h.World.Give(_hero, 3, Sword);
        Set(sword, _mine, 1);
        var game = _link.Connect(1, _hero);
        Pass();
        _link.Lose = 1;
        Set(sword, _mine, 2);
        Pass();                                                       // lost
        Set(sword, _shown, 5);
        Pass();                                                       // arrives, but does not follow what the game took
        Assert.Null(Held(game, sword, _shown));                       // so it is not taken: the game says it lost one
        Assert.Equal(1, Held(game, sword, _mine));
        Pass();
        Assert.True(game.Updates[^1].Update.Reset);                   // started over at the next pass
        Assert.Equal((2, 5), (Held(game, sword, _mine), Held(game, sword, _shown)));
    }

    [Fact]
    public void ManyGoneItemsAndManyRemovedKeysFollowInUpdatesTheChannelCarries()
    {
        _link.MaxUpdateBytes = 8 * 1024;  // an operator lowered network.json maxMessageBytes; FakeSyncLink checks every update
        var swords = new List<ItemInstanceRef>();
        for (var i = 0; i < 2048; i++)
        {
            var s = _h.World.Give(_hero, i, Rec.Of(Rec.Sword, level: 1 + i % 50));
            Set(s, _mine, i);
            swords.Add(s);
        }
        var game = _link.Connect(1, _hero);
        for (var i = 0; i < 40 && game.Store.Count < 2048; i++) Pass();
        Assert.Equal(2048, game.Store.Count);
        foreach (var s in swords) _h.World.Destroy(s);                // 2,048 items gone at once: 8 KB of ids alone
        for (var i = 0; i < 5 && game.Store.Count > 0; i++) Pass();
        Assert.Equal(0, game.Store.Count);

        // one item with 60 keys of long names; 59 of them removed at once: more than 8 KB of names
        var keys = Enumerable.Range(0, 60).Select(i => _h.Declare<Kills>($"k{i:00}-" + new string('n', 190), new InstanceKeyOptions { Sync = InstanceSync.Holder })).ToList();
        var ring = _h.World.Give(_hero, 3000, Ring);
        foreach (var k in keys) Set(ring, k, 1);
        for (var i = 0; i < 5; i++) Pass();
        Assert.Equal(60, game.Store.Find(ring.Entity.Id)!.Keys.Count);
        TestMods.As(_h.Mod, () =>
        {
            foreach (var k in keys.Skip(1)) Assert.True(ring.Remove(k));
        });
        for (var i = 0; i < 5 && game.Store.Find(ring.Entity.Id)!.Keys.Count > 1; i++) Pass();
        Assert.Equal([keys[0].Name], game.Store.Find(ring.Entity.Id)!.Keys.Select(k => k.Key));
    }

    [Fact]
    public void AGamesCapIsKeptByTheServerItsOwnItemsFirstAndAWaitingItemFollowsWhenThereIsRoom()
    {
        // a chest beside the player holds as many rings with a Nearby value as a game keeps copies of
        var rings = new List<ItemInstanceRef>();
        for (var i = 0; i < InstanceSyncClient.MaxItems; i++)
        {
            var r = _h.World.Give(_chest, i, Ring);
            Set(r, _shown, i);
            rings.Add(r);
        }
        var sword = _h.World.Give(_hero, 3, Sword);                   // the player's own, with the highest entity id
        Set(sword, _mine, 7);
        var game = _link.Connect(1, _hero, new WorldPosition(A.X + 2, A.Y, A.Z));
        for (var i = 0; i < 20; i++) Pass();
        Assert.Equal(InstanceSyncClient.MaxItems, game.Store.Count);
        Assert.Equal(7, Held(game, sword, _mine));                    // its own item is there
        var waiting = Assert.Single(rings, r => game.Store.Find(r.Entity.Id) is null);   // one ring waits for room

        TestMods.As(_h.Mod, () => Assert.True(rings[0].Remove(_shown)));
        Pass();
        Assert.Equal(InstanceSyncClient.MaxItems, game.Store.Count);
        Assert.Null(game.Store.Find(rings[0].Entity.Id));
        Assert.Equal(InstanceSyncClient.MaxItems - 1, Held(game, waiting, _shown));   // there is room again: it came
    }


    // ---- confirm review of f114cec0: a game that keeps reporting losses, bytes per game, removals behind eight ----

    [Fact]
    public void AGameThatReportsALossEverySecondIsStartedOverAtABackedOffRateAndAnHonestOneWithinASecond()
    {
        var swords = Enumerable.Range(0, 20).Select(i => _h.World.Give(_hero, i, Rec.Of(Rec.Sword, level: 1 + i))).ToList();
        for (var i = 0; i < swords.Count; i++) Set(swords[i], _mine, i);
        var game = _link.Connect(1, _hero);
        Pass();
        var t0 = _clock;
        var starts = new List<long>();
        for (var pass = 0; pass < 4 * 130; pass++)                    // 130 seconds of a hostile game
        {
            if (pass % 4 == 0)                                        // once a second: "lost one after your last reset"
                _server.Ack(1, new SyncAck(game.Updates.Last(u => u.Update.Reset).Update.Seq, Lost: true));
            var before = game.Updates.Count;
            Pass();
            if (game.Updates.Count > before && game.Updates[^1].Update.Reset) starts.Add(_clock - t0);
        }
        var gaps = starts.Zip(starts.Skip(1), (a, b) => b - a).ToList();
        Assert.InRange(starts.Count, 6, 8);                           // 1, 2, 4, ... 64 s apart: seven, not 130
        Assert.All(gaps.Zip(gaps.Skip(1)), g => Assert.True(g.Second >= 2 * g.First - 300, $"gaps {string.Join(", ", gaps)}"));
        Assert.True(gaps[^1] >= 32_000, $"gaps {string.Join(", ", gaps)}");

        // it stops reporting. Its last report is still honoured at its backed-off time (a minute on); then five calm
        // minutes, every update acknowledged, step the wait back, one step per 30 s
        for (var pass = 0; pass < 4 * 60 * 5; pass++) Pass();
#if SYNC_BACKOFF
        Assert.Equal(InstanceSyncServer.LostEveryMs, _server.WaitOf(1).GapMs);   // the wait stepped back to one second
#endif
        // an honest game that lost one update now is made good within about a second
        _link.Lose = 1;
        Set(swords[0], _mine, 100);
        Pass();                                                       // lost
        Set(swords[1], _mine, 101);
        Pass();                                                       // noticed: the game reports the loss
        var noticed = _clock;
        while (Held(game, swords[0], _mine) != 100 && _clock - noticed < 5_000) Pass();
        Assert.Equal((100, 101), (Held(game, swords[0], _mine), Held(game, swords[1], _mine)));
        Assert.True(_clock - noticed <= 1_000, $"made good {_clock - noticed} ms after the loss was noticed");
    }

    [Fact]
    public void AGameIsSentAtMostItsByteBudgetASecondAndStillEverything()
    {
        var big = _h.Declare<Note>("story", new InstanceKeyOptions { Sync = InstanceSync.Holder });
        for (var i = 0; i < 20; i++)
        {
            var item = _h.World.Give(_hero, i, Rec.Of(Rec.Sword, level: i + 1));
            TestMods.As(_h.Mod, () => item.Set(big, new Note { Text = new string('a', 40_000) }));
        }
        var game = _link.Connect(1, _hero);
        for (var pass = 0; pass < 8; pass++) Pass();                  // two seconds
        var sent = game.Updates.Sum(u => u.Bytes);
        Assert.True(sent <= 96 * 1024 + 2 * 256 * 1024, $"{sent} bytes in two seconds: more than one update and 256 KiB a second");
        for (var pass = 0; pass < 40 && game.Store.Count < 20; pass++) Pass();
        Assert.Equal(20, game.Store.Count);                           // nothing is dropped, only spread out
    }

    [Fact]
    public void ARemovalDoesNotWaitBehindEightUnacknowledgedUpdates()
    {
        var sword = _h.World.Give(_hero, 3, Sword);
        var ring = _h.World.Give(_hero, 4, Ring);
        Set(sword, _mine, 0);
        Set(ring, _mine, 1);
        var game = _link.Connect(1, _hero);
        _link.LoseAcks = true;                                        // the game's acknowledgements are slow: none arrives yet
        Pass();
        for (var i = 1; i < 8; i++)
        {
            Set(sword, _mine, i);
            Pass();
        }
        Assert.Equal(8, game.Updates.Count);                          // eight under way
        TestMods.As(_h.Mod, () => Assert.True(ring.Remove(_mine)));  // the player may no longer see the ring's value
        Set(sword, _mine, 50);
        Pass();
        Assert.Null(game.Store.Find(ring.Entity.Id));                 // that goes at once
        Assert.Equal(7, Held(game, sword, _mine));                    // a new value waits for an acknowledgement
    }

#if SYNC_ACKS
    [Fact]
    public void AGameThatAcknowledgesNothingGetsEightUpdatesThenStartsOver()
    {
        var sword = _h.World.Give(_hero, 3, Sword);
        var game = _link.Connect(1, _hero);
        _link.LoseAcks = true;
        for (var i = 1; i <= 12; i++)
        {
            Set(sword, _mine, i);
            Pass();
        }
        Assert.Equal(InstanceSyncServer.MaxInFlight, game.Updates.Count);   // then nothing more while none is acknowledged
        Assert.Equal(8, Held(game, sword, _mine));
        _link.LoseAcks = false;
        _clock += InstanceSyncServer.AckTimeoutMs;
        Pass();
        Assert.True(game.Updates[^1].Update.Reset);
        Assert.Equal(12, Held(game, sword, _mine));
        Assert.Equal(1, _server.StartOvers);
        Set(sword, _mine, 13);
        Pass();
        Assert.False(game.Updates[^1].Update.Reset);                 // acknowledged again: plain differences
        Assert.Equal(13, Held(game, sword, _mine));
    }

    [Fact]
    public void WhatAGameSaysCannotMakeTheServerSendMoreThanALossAllows()
    {
        var sword = _h.World.Give(_hero, 3, Sword);
        Set(sword, _mine, 1);
        var game = _link.Connect(1, _hero);
        Pass();
        var sent = game.Updates.Count;
        _server.Ack(1, new SyncAck(999_999, Lost: false));            // an update never sent
        _server.Ack(1, new SyncAck(0, Lost: true));                   // a loss before the game's reset
        _server.Ack(1, new SyncAck(game.Updates[^1].Update.Seq + 5, Lost: true));   // a loss after what was sent
        _server.Ack(77, new SyncAck(1, Lost: true));                  // no such game
        Pass();
        Assert.Equal(sent, game.Updates.Count);
        Assert.Equal(0, _server.StartOvers);

        // a loss after an update it took starts it over once, and not again within the next wait (2 s after a first start
        // over) however often it is said
        for (var i = 0; i < 5; i++) _server.Ack(1, new SyncAck(game.Updates[^1].Update.Seq, Lost: true));
        Pass();
        Assert.Equal(1, _server.StartOvers);
        for (var i = 0; i < 5; i++)
        {
            _server.Ack(1, new SyncAck(game.Updates[^1].Update.Seq, Lost: true));
            Pass();
        }
        Assert.Equal(1, _server.StartOvers);
        _clock += InstanceSyncServer.LostEveryMs;
        Pass();
        Assert.Equal(2, _server.StartOvers);
    }

    [Fact]
    public void TheFirstUpdateLostIsMadeGoodByTheTimeout()
    {
        var sword = _h.World.Give(_hero, 3, Sword);
        Set(sword, _mine, 1);
        var game = _link.Connect(1, _hero);
        _link.Lose = 1;
        Pass();                                                       // the reset is lost
        Set(sword, _mine, 2);
        Pass();                                                       // the game has nothing to follow: it takes nothing
        Assert.Empty(game.Store.All());
        _clock += InstanceSyncServer.AckTimeoutMs;
        Pass();
        Assert.Equal(2, Held(game, sword, _mine));
    }

    [Fact]
    public void AGameTakesOnlyTheUpdateThatFollowsTheOneItTookAndSaysALossOnce()
    {
        static SyncUpdate U(uint seq, uint prev, int count, bool reset = false)
        {
            var u = new SyncUpdate { Reset = reset, Seq = seq, Prev = prev };
            var op = new SyncItemUpdate { Entity = 7000, Item = Rec.Sword.Value, Holder = 1, Slot = 3 };
            op.Values.Add(("sword-mod", "mine", $"{{\"Count\":{count}}}"));
            u.Items.Add(op);
            return u;
        }
        var store = new InstanceSyncClient();
        int Count() => store.Find(new EntityId(7000)) is { } i && i.TryGet(_mine, out var k) ? k.Count : -1;
        store.Apply(U(5, 0, 1, reset: true), out var ack);
        Assert.Equal((new SyncAck(5, false), 1), (ack, Count()));
        store.Apply(U(9, 5, 2), out ack);
        Assert.Equal((new SyncAck(9, false), 2), (ack, Count()));
        store.Apply(U(12, 11, 3), out ack);                           // 11 never came
        Assert.Equal((new SyncAck(9, true), 2), (ack, Count()));
        store.Apply(U(13, 12, 4), out ack);
        Assert.Equal((null, 2), (ack, Count()));                      // said once
        store.Apply(U(20, 0, 5, reset: true), out ack);
        Assert.Equal((new SyncAck(20, false), 5), (ack, Count()));
        store.Clear();                                                // a new connection takes nothing before its reset
        store.Apply(U(21, 20, 6), out ack);
        Assert.Equal((new SyncAck(0, true), -1), (ack, Count()));
    }

    // ---- dev.27 rehearsal: "walking back in brings it again" (syncnet.py) waited 4 s in vain ----

    public enum GoneAck { BeforeTheReturn, BetweenTheReturnAndThePass, AfterThePass, OnlyAfterTheTimeout }

    /// <summary>The failing run's timing: the game stands 3 m from a chest and holds a Nearby value (10 m) of an item in
    /// it, walks to 30 m (a pass sends the item "gone"), and is back at 9 m 0.29 s later, before the next pass or the
    /// acknowledgement of the gone has to be in. However the acknowledgement of the gone is timed, the first pass after
    /// the return sends the value again, as a plain difference that follows the gone: nothing waits for a timer.</summary>
    [Theory]
    [InlineData(GoneAck.BeforeTheReturn)]
    [InlineData(GoneAck.BetweenTheReturnAndThePass)]
    [InlineData(GoneAck.AfterThePass)]
    [InlineData(GoneAck.OnlyAfterTheTimeout)]
    public void WalkingBackInWithinAPassBringsTheValueAtTheNextPass(GoneAck when)
    {
        var ring = _h.World.Give(_chest, 1, Ring);
        Set(ring, _shown, 7);
        var game = _link.Connect(1, _hero, new WorldPosition(A.X + 3, A.Y, A.Z));
        Pass();
        Assert.Equal(7, Held(game, ring, _shown));

        _link.Positions[_hero.Value] = new WorldPosition(A.X + 30, A.Y, A.Z);   // out of range
        _clock += 250;
        Assert.Equal(1, _server.Pass());
        Assert.Equal(ring.Entity.Id.Value, Assert.Single(game.Updates[^1].Update.Gone));
        Assert.Null(game.Store.Find(ring.Entity.Id));
        var held = _link.Acks.ToList();                                // the acknowledgement of the gone is on its way
        _link.Acks.Clear();
        if (when == GoneAck.BeforeTheReturn) Deliver(held);

        _clock += 40;                                                  // 0.29 s after it left: back at 9 m
        _link.Positions[_hero.Value] = new WorldPosition(A.X + 9, A.Y, A.Z);
        if (when == GoneAck.BetweenTheReturnAndThePass) Deliver(held);
        _clock += 210;                                                 // the next pass, 250 ms after the one that sent the gone
        var updates = game.Updates.Count;
        Assert.Equal(1, _server.Pass());
        var back = game.Updates[^1].Update;
        Assert.Equal(updates + 1, game.Updates.Count);
        Assert.False(back.Reset);                                      // a plain difference, right behind the gone
        Assert.Equal(game.Updates[^2].Update.Seq, back.Prev);
        Assert.Equal(7, Held(game, ring, _shown));                     // the game took it: the value is there again
        var later = _link.Acks.ToList();
        _link.Acks.Clear();
        if (when == GoneAck.AfterThePass) Deliver([.. held, .. later]);
        if (when == GoneAck.OnlyAfterTheTimeout)
        {
            // no acknowledgement for ten seconds (a stalled server handles none): the server starts the game over, which
            // also carries the value; the game ends up with it either way
            _clock += InstanceSyncServer.AckTimeoutMs;
            Assert.Equal(1, _server.Pass());
            Assert.True(game.Updates[^1].Update.Reset);
            _link.Deliver(_server);
        }
        else if (when != GoneAck.AfterThePass) Deliver(later);
        Assert.Equal(7, Held(game, ring, _shown));
        for (var i = 0; i < 4; i++) Pass();                            // and then nothing more is owed
        Assert.Equal(when == GoneAck.OnlyAfterTheTimeout ? 1 : 0, _server.StartOvers);
        Assert.Equal(7, Held(game, ring, _shown));

        void Deliver(List<(ulong Viewer, SyncAck Ack)> acks)
        {
            foreach (var (viewer, ack) in acks) _server.Ack(viewer, ack);
        }
    }

    /// <summary>What the failing run's server did: it was back in range 31 ms before an autosave that held the server's
    /// steps for 9.3 s, so no pass ran. The passes are the only sender: the value goes at the first pass once the server
    /// steps again, and, if the network lost the update that carried it (kiln.net's retry queue gives up after two
    /// seconds) with nothing behind it, the acknowledgement timeout starts the game over ten seconds after it was sent.
    /// That is the bound syncnet.py waits for.</summary>
    [Fact]
    public void AfterAStallTheValueGoesAtTheFirstPassAndALostOneAtTheAcknowledgementTimeout()
    {
        var ring = _h.World.Give(_chest, 1, Ring);
        Set(ring, _shown, 7);
        var game = _link.Connect(1, _hero, new WorldPosition(A.X + 3, A.Y, A.Z));
        Pass();
        _link.Positions[_hero.Value] = new WorldPosition(A.X + 30, A.Y, A.Z);
        Pass();
        Assert.Null(game.Store.Find(ring.Entity.Id));

        _link.Positions[_hero.Value] = new WorldPosition(A.X + 9, A.Y, A.Z);
        _clock += 9_336;                                               // no step, so no pass, for 9.3 s
        Pass();
        Assert.Equal(7, Held(game, ring, _shown));                     // the first pass after it sends the value

        // the same walk once more, and the network loses the update that brings the value back
        _link.Positions[_hero.Value] = new WorldPosition(A.X + 30, A.Y, A.Z);
        Pass();
        Assert.Null(game.Store.Find(ring.Entity.Id));
        _link.Positions[_hero.Value] = new WorldPosition(A.X + 9, A.Y, A.Z);
        _link.Lose = 1;
        Pass();
        var sentAt = _clock;
        Assert.Equal(1, game.Lost);
        while (Held(game, ring, _shown) is null && _clock - sentAt < 20_000) Pass();
        Assert.Equal(7, Held(game, ring, _shown));
        Assert.True(game.Updates[^1].Update.Reset);                    // the start over brought it
        Assert.InRange(_clock - sentAt, InstanceSyncServer.AckTimeoutMs, InstanceSyncServer.AckTimeoutMs + 250);
    }
#endif

    // ---- the options ----

    [Fact]
    public void TheKeySaysWhoSeesIt()
    {
        Assert.Equal((InstanceSync.Holder, InstanceSync.Nearby, InstanceSync.None), (_mine.Sync, _shown.Sync, _secret.Sync));
        Assert.Same(_mine, _h.Declare<Kills>("mine", new InstanceKeyOptions { Sync = InstanceSync.Holder }));
        Assert.Same(_mine, _h.Declare<Kills>("mine", new InstanceKeyOptions { Sync = InstanceSync.Holder, NearbyRadius = 99 }));  // the radius only counts for Nearby
        Assert.Throws<ArgumentException>(() => _h.Declare<Kills>("mine"));
        Assert.Throws<ArgumentException>(() => _h.Declare<Kills>("mine", new InstanceKeyOptions { Sync = InstanceSync.Nearby }));
        Assert.Throws<ArgumentException>(() => _h.Declare<Kills>("shown", new InstanceKeyOptions { Sync = InstanceSync.Nearby }));  // 32 m is not its 10 m
        Assert.Throws<ArgumentException>(() => _h.Declare<Kills>("x", new InstanceKeyOptions { Sync = (InstanceSync)9 }));
        Assert.Throws<ArgumentException>(() => _h.Declare<Kills>("x", new InstanceKeyOptions { Sync = InstanceSync.Nearby, NearbyRadius = 0.5 }));
        Assert.Throws<ArgumentException>(() => _h.Declare<Kills>("x", new InstanceKeyOptions { Sync = InstanceSync.Nearby, NearbyRadius = 257 }));
        Assert.Throws<ArgumentException>(() => _h.Declare<Kills>("x", new InstanceKeyOptions { Sync = InstanceSync.Nearby, NearbyRadius = double.NaN }));
        Assert.True(Store.Instances.HasSyncedKeys);
    }

    [Fact]
    public void ASessionKeyTravelsTooAndIsStillNeverSaved()
    {
        var temp = _h.Declare<Kills>("buff", new InstanceKeyOptions { Persist = Persist.Session, Sync = InstanceSync.Holder });
        var sword = _h.World.Give(_hero, 3, Sword);
        Set(sword, temp, 3);
        var game = _link.Connect(1, _hero);
        Pass();
        Assert.Equal(3, Held(game, sword, temp));
        _h.World.SendCharacter(_hero);
        _h.Save(1);
        Assert.DoesNotContain(_h.Scopes(1), s => s.StartsWith(InstanceStore.CharacterPrefix, StringComparison.Ordinal));
    }

    // ---- the client's copies ----

    [Fact]
    public void AValueThatDoesNotReadAsTheTypeThrowsOnTheClient()
    {
        var store = new InstanceSyncClient();
        var u = Numbered(new SyncUpdate { Reset = true }, 1);
        var op = new SyncItemUpdate { Entity = 7000, Item = Rec.Sword.Value, Holder = 1, Slot = 3 };
        op.Values.Add(("sword-mod", "mine", "{\"Count\":\"many\"}"));   // another version of the mod on the server
        op.Values.Add(("sword-mod", "shown", "{\"Count\":2}"));
        u.Items.Add(op);
        store.Apply(u);
        var item = store.Find(new EntityId(7000))!;
        Assert.Throws<InvalidDataException>(() => item.TryGet(_mine, out _));
        Assert.True(item.TryGet(_shown, out var ok));
        Assert.Equal(2, ok.Count);
        Assert.False(item.TryGet(_secret, out _));
        Assert.Throws<ArgumentNullException>(() => item.Json(null!, "mine"));
        Assert.Throws<ArgumentNullException>(() => item.Has<Kills>(null!));
        Assert.Contains("7000", item.ToString());
    }

    [Fact]
    public void TheClientLeavesOutWhatIsNoModIdOrKeyAndKeepsAtMostItsCap()
    {
        var store = new InstanceSyncClient();
        var u = Numbered(new SyncUpdate { Reset = true }, 1);
        var op = new SyncItemUpdate { Entity = 7000, Item = Rec.Sword.Value, Holder = 1, Slot = 3 };
        op.Values.Add(("bad mod id", "k", "1"));
        op.Values.Add(("sword-mod", "", "1"));
        op.Values.Add(("sword-mod", new string('k', 201), "1"));
        u.Items.Add(op);
        Assert.Empty(store.Apply(u));
        Assert.Equal(0, store.Count);

        var many = Numbered(new SyncUpdate(), 2, 1);
        for (uint e = 0; e < SyncUpdate.MaxItems; e++)
        {
            var i = new SyncItemUpdate { Entity = 10_000 + e, Item = Rec.Ring.Value, Holder = 1, Slot = (int)e };
            i.Values.Add(("sword-mod", "mine", "{}"));
            many.Items.Add(i);
        }
        store.Apply(many);
        Assert.Equal(InstanceSyncClient.MaxItems, store.Count);
        var more = Numbered(new SyncUpdate(), 3, 2);
        var extra = new SyncItemUpdate { Entity = 99_999, Item = Rec.Ring.Value, Holder = 1, Slot = 1 };
        extra.Values.Add(("sword-mod", "mine", "{}"));
        more.Items.Add(extra);
        Assert.Empty(store.Apply(more));
        Assert.Equal(InstanceSyncClient.MaxItems, store.Count);

        var events = store.Clear();   // the connection ended
        Assert.Equal(InstanceSyncClient.MaxItems, events.Count);
        Assert.All(events, e => Assert.Equal(InstanceSyncChange.Gone, e.Change));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void AResetVoidsWhatTheGameHeld()
    {
        var store = new InstanceSyncClient();
        var first = Numbered(new SyncUpdate { Reset = true }, 1);
        var op = new SyncItemUpdate { Entity = 7000, Item = Rec.Sword.Value, Holder = 1, Slot = 3 };
        op.Values.Add(("sword-mod", "mine", "{\"Count\":1}"));
        first.Items.Add(op);
        store.Apply(first);
        var reset = Numbered(new SyncUpdate { Reset = true }, 5);
        var op2 = new SyncItemUpdate { Entity = 7001, Item = Rec.Ring.Value, Holder = 1, Slot = 4 };
        op2.Values.Add(("sword-mod", "mine", "{\"Count\":2}"));
        reset.Items.Add(op2);
        var events = store.Apply(reset);
        Assert.Equal([InstanceSyncChange.Gone, InstanceSyncChange.Set], events.Select(e => e.Change));
        Assert.Equal([new EntityId(7001)], store.All().Select(i => i.Entity));
    }

    [Fact]
    public void ASyncEventPostedWhileTheStoreDeliversWaitsItsTurn()
    {
        // The client side's events go through the store's own queue. A save finds two items gone: two events wait. A
        // handler of the first posts a sync event: it goes out after the one already waiting, not from inside the handler.
        var a = _h.World.Give(_chest, 0, Sword);
        var b = _h.World.Give(_chest, 1, Ring);
        Set(a, _secret, 1);
        Set(b, _secret, 2);
        var order = new List<string>();
        var copy = new InstanceDataSyncedEvent(new SyncedItem(new EntityId(7000), Rec.Sword, _hero, 3, new Dictionary<(string, string), string> { [("sword-mod", "mine")] = "{}" }),
            "sword-mod", "mine", InstanceSyncChange.Set);
        using var lost = Events.On<InstanceDataLostEvent>(e =>
        {
            order.Add($"lost {e.Item}");
            if (order.Count == 1) Store.Instances.PostSynced(copy);
            order.Add("handler done");
        });
        using var synced = Events.On<InstanceDataSyncedEvent>(e => order.Add($"synced {e.Key}"));
        _h.World.Destroy(a, seen: false);
        _h.World.Destroy(b, seen: false);
        _h.Save(1);
        Assert.Equal([$"lost {Rec.Sword}", "handler done", $"lost {Rec.Ring}", "handler done", "synced mine"], order);
        // outside a pass it is delivered at once
        Store.Instances.PostSynced(copy);
        Assert.Equal("synced mine", order[^1]);
        Assert.Equal(6, order.Count);
    }

    // ---- the wire ----

#if SYNC_ACKS
    [Fact]
    public void TheWireRefusesWhatBreaksItsLimits()
    {
        static void Refused(Action<NetWriter> write)
        {
            var w = new NetWriter();
            write(w);
            Assert.Throws<NetFormatException>(() =>
            {
                var r = new NetReader(w.WrittenSpan);
                SyncUpdate.Decode(ref r);
            });
        }
        Refused(w => w.Byte(2).Bool(false).UInt32(1).UInt32(0).VarUInt(0).VarUInt(0));                       // another version
        Refused(w => w.Byte(1).Bool(true).UInt32(0).UInt32(0).VarUInt(0).VarUInt(0));                        // no number
        Refused(w => w.Byte(1).Bool(false).UInt32(1).UInt32(0).VarUInt(SyncUpdate.MaxGone + 1));             // too many gone
        Refused(w => w.Byte(1).Bool(false).UInt32(1).UInt32(0).VarUInt(0).VarUInt(SyncUpdate.MaxItems + 1)); // too many items
        Refused(w => w.Byte(1).Bool(false).UInt32(1).UInt32(0).VarUInt(0).VarUInt(1).UInt32(1).UInt32(2).UInt32(3).VarInt(0).VarUInt(SyncUpdate.MaxValuesPerItem + 1));
        Refused(w => w.Byte(1).Bool(false).UInt32(1).UInt32(0).VarUInt(0).VarUInt(1).UInt32(1).UInt32(2).UInt32(3).VarInt(0).VarUInt(1)
            .String(new string('m', SyncUpdate.MaxModBytes + 1)).String("k").Bool(false));                   // a mod id too long
        Refused(w => w.Byte(1).Bool(false).UInt32(1).UInt32(0).VarUInt(0).VarUInt(1).UInt32(1).UInt32(2).UInt32(3).VarInt(0).VarUInt(1)
            .String("m").String("k").Bool(true).String(new string('x', InstanceStore.MaxValueBytes + 1)));  // a value over the store's cap
        Refused(w => w.Byte(1).Bool(false).UInt32(1).UInt32(0).VarUInt(0).VarUInt(1).UInt32(1).UInt32(2));   // cut short

        var u = new SyncUpdate { Reset = true, Seq = 77, Prev = 0 };
        u.Gone.Add(41);
        var op = new SyncItemUpdate { Entity = 1, Item = 2, Holder = 3, Slot = 300 };
        op.Values.Add(("m", "k", "{\"a\":\"ä\"}"));
        op.Values.Add(("m", "gone", null));
        u.Items.Add(op);
        var ok = new NetWriter();
        u.Encode(ok);
        var reader = new NetReader(ok.WrittenSpan);
        var back = SyncUpdate.Decode(ref reader);
        Assert.True(back.Reset);
        Assert.Equal((77u, 0u), (back.Seq, back.Prev));
        Assert.Equal([41u], back.Gone);
        var item = Assert.Single(back.Items);
        Assert.Equal((1u, 2u, 3u, 300), (item.Entity, item.Item, item.Holder, item.Slot));
        Assert.Equal([("m", "k", "{\"a\":\"ä\"}"), ("m", "gone", null)], item.Values);
    }
#endif
}
