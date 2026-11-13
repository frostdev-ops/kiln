// SPDX-License-Identifier: MIT
// Instance data on players' games (InstanceSync.cs): the server's passes against the real store and the fake game, with a
// loopback in place of kiln.net. Every update the server makes is written by the real encoder, read by the real decoder
// and taken by a real client store, one per player, so what a test reads is what a player's game would hold. The channel
// itself (Kiln.Network, the handshake, lab peers) is the lab test's part (mods/tests/instance-sync-test).
using Xunit;
using static Kiln.InstanceHarness;

namespace Kiln;

/// <summary>Stands in for kiln.net and kiln.players: the games that take updates, where the players stand, and the wire.</summary>
internal sealed class FakeSyncLink : IInstanceSyncLink
{
    public sealed class Game
    {
        public readonly InstanceSyncClient Store = new();
        public readonly List<InstanceDataSyncedEvent> Events = [];
        public readonly List<(SyncUpdate Update, int Bytes)> Updates = [];
    }

    public readonly List<SyncViewer> Now = [];
    public readonly Dictionary<uint, WorldPosition> Positions = new();
    public readonly Dictionary<ulong, Game> Games = new();
    public bool Refuse;
    public int MaxUpdateBytes { get; set; } = 256 * 1024;

    public IReadOnlyList<SyncViewer> Viewers() => Now.Select(v => v with { Position = Positions.TryGetValue(v.Player.Value, out var p) ? p : null }).ToList();

    public WorldPosition? PositionOf(EntityId player) => Positions.TryGetValue(player.Value, out var p) ? p : null;

    public bool Send(in SyncViewer viewer, SyncUpdate update)
    {
        if (Refuse) return false;
        var w = new NetWriter();
        update.Encode(w);
        Assert.True(w.Length <= MaxUpdateBytes, $"an update of {w.Length} bytes for a channel that carries {MaxUpdateBytes}");
        var r = new NetReader(w.WrittenSpan);
        var read = SyncUpdate.Decode(ref r);
        Assert.True(r.End);
        var game = Games[viewer.Id];
        game.Updates.Add((read, w.Length));
        game.Events.AddRange(game.Store.Apply(read));
        return true;
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

    public InstanceSyncTests()
    {
        _server = new InstanceSyncServer(Store.Instances, _link);
        _mine = _h.Declare<Kills>("mine", new InstanceKeyOptions { Sync = InstanceSync.Holder });
        _shown = _h.Declare<Kills>("shown", new InstanceKeyOptions { Sync = InstanceSync.Nearby, NearbyRadius = 10 });
        _secret = _h.Kills;  // declared by the harness without Sync: it stays on the server
        _hero = _h.World.Join(Hero);
        _friend = _h.World.Join(Friend);
        _chest = _h.World.Chest(ChestTemplate, A);
    }

    public void Dispose() => _h.Dispose();

    private void Set(ItemInstanceRef item, InstanceKey<Kills> key, int count) => TestMods.As(_h.Mod, () => item.Set(key, new Kills { Count = count }));

    private int? Held(FakeSyncLink.Game game, ItemInstanceRef item, InstanceKey<Kills> key) =>
        game.Store.Find(item.Entity.Id) is { } i && i.TryGet(key, out var k) ? k.Count : null;

    private static string Text(FakeSyncLink.Game game) => string.Join("; ", game.Events.Select(e => $"{e.Change} {e.Key} {e.Item.Entity}"));

    [Fact]
    public void TheHolderGetsItsValuesAndNobodyElseDoes()
    {
        var sword = _h.World.Give(_hero, 3, Sword);
        Set(sword, _mine, 4);
        Set(sword, _secret, 9);
        var hero = _link.Connect(1, _hero);
        var friend = _link.Connect(2, _friend);
        _server.Pass();

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
        for (var i = 0; i < 3; i++) _server.Pass();
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
        _server.Pass();
        hero.Events.Clear();

        Set(sword, _mine, 2);                                        // Set
        _server.Pass();
        Assert.Equal(2, Held(hero, sword, _mine));

        TestMods.As(_h.Mod, () => sword.Get(_mine).Count = 3);        // the live object, changed in place
        _server.Pass();
        Assert.Equal(3, Held(hero, sword, _mine));

        var before = hero.Updates.Count;
        _server.Pass();                                              // nothing changed: nothing sent
        Assert.Equal(before, hero.Updates.Count);

        Set(sword, _shown, 8);                                       // a second key on the same item
        _server.Pass();
        Assert.Equal((3, 8), (Held(hero, sword, _mine), Held(hero, sword, _shown)));

        TestMods.As(_h.Mod, () => Assert.True(sword.Remove(_mine)));  // Remove
        _server.Pass();
        Assert.Null(Held(hero, sword, _mine));
        Assert.Equal(8, Held(hero, sword, _shown));

        TestMods.As(_h.Mod, () => Assert.True(sword.Remove(_shown)));
        _server.Pass();
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
        _server.Pass();
        Assert.Equal(2, Held(near, seen, _shown));
        Assert.Null(near.Store.Find(kept.Entity.Id));
        Assert.Empty(far.Store.All());

        _link.Positions[_hero.Value] = new WorldPosition(A.X + 11, A.Y, A.Z);   // walks out of range
        _link.Positions[_friend.Value] = new WorldPosition(A.X + 9, A.Y, A.Z);  // walks in
        _server.Pass();
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
        _server.Pass();
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
        _server.Pass();
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
        _server.Pass();
        hero.Events.Clear();

        _h.World.Move(sword, _hero, 12);                          // another slot of the same player
        _server.Pass();
        Assert.Equal(12, hero.Store.Find(sword.Entity.Id)!.FlatSlot);
        Assert.Equal($"Moved mine {sword.Entity.Id}", Text(hero));
        Assert.Equal(5, Held(hero, sword, _mine));

        _h.World.Move(sword, _friend, 0);                         // a gift
        _server.Pass();
        Assert.Empty(hero.Store.All());
        Assert.Equal(InstanceSyncChange.Gone, hero.Events[^1].Change);
        Assert.Equal(5, Held(friend, sword, _mine));
        Assert.Equal((_friend, 0), (friend.Store.Find(sword.Entity.Id)!.Holder, friend.Store.Find(sword.Entity.Id)!.FlatSlot));

        _h.World.Move(sword, _chest, 2);                          // into a chest: nobody's
        _server.Pass();
        Assert.Empty(friend.Store.All());
    }

    [Fact]
    public void ADestroyedItemLeavesTheHoldersGame()
    {
        var sword = _h.World.Give(_hero, 3, Sword);
        Set(sword, _mine, 5);
        var hero = _link.Connect(1, _hero);
        _server.Pass();
        _h.World.Destroy(sword);
        _server.Pass();
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
        var server = new InstanceSyncServer(Store.Instances, _link);
        var game = _link.Connect(1, new EntityId(7), A);          // a player standing at the chest
        server.Pass();
        Assert.Empty(game.Store.All());                           // set aside: on no item, so on no game

        var target = _h.World.At(chest, 6);
        var claim = TestMods.As(_h.Mod, () => InstanceData.Claim(parked.Single(p => p.Value!.Count == 40), target));
        Assert.True(claim.Ok, claim.Reason);
        server.Pass();
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
        _server.Pass();
        Assert.Equal(2, first.Store.All().Count);

        _link.Disconnect(1);
        _server.Pass();                                           // the server forgets what it sent that game
        var again = _link.Connect(1, _hero);                      // the same connection id, a new game
        _server.Pass();
        Assert.True(again.Updates[0].Update.Reset);
        Assert.Equal((5, 6), (Held(again, sword, _mine), Held(again, ring, _mine)));
    }

    [Fact]
    public void AConnectionThatStandsForAnotherPlayerStartsOver()
    {
        var sword = _h.World.Give(_hero, 3, Sword);
        Set(sword, _mine, 5);
        var game = _link.Connect(1, _hero);
        _server.Pass();
        Assert.Single(game.Store.All());
        // the same connection, now the other player (the same game came back with another character in that slot)
        _link.Now[0] = _link.Now[0] with { Player = _friend };
        _server.Pass();
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
        _server.Pass();
        Assert.Equal(5, Held(game, sword, _mine));
        _h.Save(1);
        w.SendCharacter(_hero);
        w.Crash(_hero);                                           // the connection is lost: no leave save
        _link.Disconnect(1);
        _h.Tick();
        _server.Pass();
        var back = w.Join(Hero);                                  // the character comes back with new item entities
        _h.Tick();
        var again = _link.Connect(1, back);
        _server.Pass();
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
            _server.Pass();
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
        _server.Pass();
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
        _server.Pass();
        Assert.True(game.Store.Find(sword.Entity.Id)!.TryGet(big, out var note));
        Assert.Equal(text.Length, note.Text.Length);
    }

    [Fact]
    public void AValueLargerThanTheChannelCarriesStaysOnTheServer()
    {
        // an operator lowered network.json maxMessageBytes: 8 KB, so 4 KB of values per update
        _link.MaxUpdateBytes = 8 * 1024;
        var big = _h.Declare<Note>("story", new InstanceKeyOptions { Sync = InstanceSync.Holder });
        var sword = _h.World.Give(_hero, 3, Sword);
        TestMods.As(_h.Mod, () => sword.Set(big, new Note { Text = new string('x', 6000) }));
        Set(sword, _mine, 5);
        var game = _link.Connect(1, _hero);
        for (var i = 0; i < 3; i++) _server.Pass();
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
        Assert.Equal(0, _server.Pass());
        Assert.Empty(game.Store.All());
        Assert.Equal(1, _server.Unsent);
        _link.Refuse = false;
        Assert.Equal(1, _server.Pass());
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
        _server.Pass();
        Assert.Equal("{\"Count\":2,\"Tag\":null}", game.Store.Find(ring.Entity.Id)!.Json("ring-mod", "glow"));
        TestMods.Retire(other);   // its key is gone: nothing says who may see the value, so nobody does
        _server.Pass();
        Assert.Empty(game.Store.All());
        Assert.False(Store.Instances.HasSyncedKeys && Store.Instances.KeyOf("ring-mod", "glow") is not null);
    }

    [Fact]
    public void NoPlayersGameMeansNoWork()
    {
        var sword = _h.World.Give(_hero, 3, Sword);
        Set(sword, _mine, 5);
        Assert.Equal(0, _server.Pass());
        Assert.Equal(0, _server.ViewerCount);
    }

    [Fact]
    public void AnItemThatIsPlacedNowhereIsNobodys()
    {
        var sword = _h.World.Give(_hero, 3, Sword);
        Set(sword, _mine, 5);
        var game = _link.Connect(1, _hero);
        _server.Pass();
        _h.World.DestroyPlayerEntity(_hero);  // the leave's teardown: for a step the item entities outlive the player entity
        _link.Disconnect(1);
        Assert.Equal(0, _server.Pass());
        Assert.Equal(0, _server.ViewerCount);
        Assert.Single(game.Store.All());      // the game keeps its copy until its own connection ends (InstanceSyncRuntime)
    }

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
        _server.Pass();
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
        var u = new SyncUpdate();
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
        var u = new SyncUpdate();
        var op = new SyncItemUpdate { Entity = 7000, Item = Rec.Sword.Value, Holder = 1, Slot = 3 };
        op.Values.Add(("bad mod id", "k", "1"));
        op.Values.Add(("sword-mod", "", "1"));
        op.Values.Add(("sword-mod", new string('k', 201), "1"));
        u.Items.Add(op);
        Assert.Empty(store.Apply(u));
        Assert.Equal(0, store.Count);

        var many = new SyncUpdate();
        for (uint e = 0; e < SyncUpdate.MaxItems; e++)
        {
            var i = new SyncItemUpdate { Entity = 10_000 + e, Item = Rec.Ring.Value, Holder = 1, Slot = (int)e };
            i.Values.Add(("sword-mod", "mine", "{}"));
            many.Items.Add(i);
        }
        store.Apply(many);
        Assert.Equal(InstanceSyncClient.MaxItems, store.Count);
        var more = new SyncUpdate();
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
        var first = new SyncUpdate();
        var op = new SyncItemUpdate { Entity = 7000, Item = Rec.Sword.Value, Holder = 1, Slot = 3 };
        op.Values.Add(("sword-mod", "mine", "{\"Count\":1}"));
        first.Items.Add(op);
        store.Apply(first);
        var reset = new SyncUpdate { Reset = true };
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
        Refused(w => w.Byte(2).Bool(false).VarUInt(0).VarUInt(0));                                   // another version
        Refused(w => w.Byte(1).Bool(false).VarUInt(SyncUpdate.MaxGone + 1));                         // too many gone
        Refused(w => w.Byte(1).Bool(false).VarUInt(0).VarUInt(SyncUpdate.MaxItems + 1));             // too many items
        Refused(w => w.Byte(1).Bool(false).VarUInt(0).VarUInt(1).UInt32(1).UInt32(2).UInt32(3).VarInt(0).VarUInt(SyncUpdate.MaxValuesPerItem + 1));
        Refused(w => w.Byte(1).Bool(false).VarUInt(0).VarUInt(1).UInt32(1).UInt32(2).UInt32(3).VarInt(0).VarUInt(1)
            .String(new string('m', SyncUpdate.MaxModBytes + 1)).String("k").Bool(false));           // a mod id too long
        Refused(w => w.Byte(1).Bool(false).VarUInt(0).VarUInt(1).UInt32(1).UInt32(2).UInt32(3).VarInt(0).VarUInt(1)
            .String("m").String("k").Bool(true).String(new string('x', InstanceStore.MaxValueBytes + 1)));  // a value over the store's cap
        Refused(w => w.Byte(1).Bool(false).VarUInt(0).VarUInt(1).UInt32(1).UInt32(2));               // cut short

        var u = new SyncUpdate { Reset = true };
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
        Assert.Equal([41u], back.Gone);
        var item = Assert.Single(back.Items);
        Assert.Equal((1u, 2u, 3u, 300), (item.Entity, item.Item, item.Holder, item.Slot));
        Assert.Equal([("m", "k", "{\"a\":\"ä\"}"), ("m", "gone", null)], item.Values);
    }
}
