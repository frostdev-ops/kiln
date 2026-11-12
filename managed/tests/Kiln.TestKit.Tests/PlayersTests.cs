// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using TkPlayers;
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class PlayersTests
{
    private const ulong AdaSteam = 76561198000000001, BobSteam = 76561198000000002;

    [Fact]
    public void JoinAndLeaveReachALoadedModAtTheNextStep()
    {
        using var kiln = new KilnHarness();
        kiln.Load<TkPlayersMod>();
        kiln.Step();
        Assert.Equal(["present: "], kiln.Lines("tk-players", "present:"));

        var ada = kiln.Players.Join("Ada", AdaSteam, PlayerPermissions.EditBase, isAdmin: true, position: new WorldPosition(1, 2, 3), userGroupId: 7);
        Assert.False(ada.InGame);
        Assert.Empty(kiln.Run(() => Players.All));  // the service sees the records at the start of the next step
        kiln.Step();
        Assert.True(ada.InGame);
        var joined = Assert.Single(kiln.LogsOf("tk-players"), l => l.Text.StartsWith("joined:", StringComparison.Ordinal));
        Assert.Equal($"joined: Ada entity 1 steam {AdaSteam}", joined.Text);
        Assert.Equal(2UL, joined.Step);
        Assert.Single(kiln.Lines("tk-players", "bus joined: Ada"));

        var p = Assert.Single(kiln.Run(() => Players.All));
        Assert.Equal(("Ada", 1u, 0, AdaSteam), (p.Name, p.Entity.Value, p.Slot, p.SteamId));
        Assert.True(p.IsAdmin && p.IsAdminKnown && p.PermissionsKnown && p.InGame);
        Assert.Equal(PlayerPermissions.EditBase, p.Permissions);
        Assert.Equal(7u, p.UserGroupId);
        Assert.Equal(new WorldPosition(1, 2, 3), p.Position);
        Assert.Equal(2UL, p.JoinedStep);
        Assert.Equal("Ada", kiln.Run(() => Players.Find("ada"))?.Name);
        Assert.Equal("Ada", kiln.Run(() => Players.FindBySteamId(AdaSteam))?.Name);
        Assert.Equal("Ada", kiln.Run(() => Players.Find(new EntityId(1)))?.Name);
        Assert.Null(kiln.Run(() => Players.Find("nobody")));
        Assert.Equal(1, kiln.Run(() => Players.Count));

        Assert.True(ada.Leave());
        Assert.False(ada.Leave());  // leaving twice changes nothing
        Assert.Single(kiln.Run(() => Players.All));  // still there until the next step's scan
        kiln.Step();
        Assert.False(ada.InGame);
        Assert.Empty(kiln.Run(() => Players.All));
        kiln.Step(3);
        Assert.Equal(["left: Ada entity 1"], kiln.Lines("tk-players", "left:"));
        Assert.Single(kiln.Lines("tk-players", "bus left: Ada"));
        Assert.Empty(kiln.Players.All);
    }

    [Fact]
    public void AModLoadedLaterSeesThePlayersAlreadyThereAndNoJoinForThem()
    {
        using var kiln = new KilnHarness();
        kiln.Boot();
        kiln.Players.Join("Ada", AdaSteam);
        kiln.Players.Join("Bob", BobSteam);
        kiln.Step();
        kiln.Load<TkPlayersMod>();  // a hot load
        Assert.Equal(["present: Ada,Bob"], kiln.Lines("tk-players", "present:"));
        kiln.Step(2);
        Assert.Empty(kiln.Lines("tk-players", "joined:"));
        Assert.Empty(kiln.Lines("tk-players", "bus joined:"));
        kiln.Players.Join("Cy");
        kiln.Step();
        Assert.Equal(["joined: Cy"], kiln.Lines("tk-players", "joined:").Select(l => l[..10]).ToList());
    }

    [Fact]
    public void AFullServerRefusesAJoinUntilASlotIsFreedAtTheNextStep()
    {
        using var kiln = new KilnHarness();
        kiln.Boot();
        var all = Enumerable.Range(0, kiln.Players.Slots).Select(i => kiln.Players.Join($"P{i}")).ToList();
        Assert.Equal(16, all.Count);
        Assert.Equal(Enumerable.Range(1, 16).Select(i => (uint)i), all.Select(p => p.Entity.Value));
        Assert.Equal(16, all.Select(p => p.SteamId).Distinct().Count());
        kiln.Step();
        Assert.Equal(16, kiln.Run(() => Players.All).Count);
        Assert.Throws<InvalidOperationException>(() => kiln.Players.Join("Late"));
        Assert.True(all[4].Leave());
        Assert.Throws<InvalidOperationException>(() => kiln.Players.Join("Late"));  // the slot is free once the leave is seen
        kiln.Step();
        var late = kiln.Players.Join("Late");
        Assert.Equal(5u, late.Entity.Value);  // the freed slot
        kiln.Step();
        Assert.Equal("Late", kiln.Run(() => Players.Find(new EntityId(5)))?.Name);
        Assert.Throws<InvalidOperationException>(() => kiln.Players.Join("Twin", late.SteamId));
    }

    [Fact]
    public void ReadsWorkFromAWorkerThreadOnTheLastStepsSnapshot()
    {
        using var kiln = new KilnHarness();
        kiln.Boot();
        var ada = kiln.Players.Join("Ada", AdaSteam, position: new WorldPosition(10, 0, 0));
        kiln.Step();
        ada.Position = new WorldPosition(20, 0, 0);
        // the test thread is not the tick thread: the snapshot of the last step
        Assert.Equal(new WorldPosition(10, 0, 0), Players.PositionOf(ada.Entity));
        Assert.Equal(new WorldPosition(10, 0, 0), Players.All.Single().Position);
        WorldPosition? fromWorker = null;
        var worker = new Thread(() => fromWorker = Players.PositionOf(ada.Entity));
        worker.Start();
        worker.Join();
        Assert.Equal(new WorldPosition(10, 0, 0), fromWorker);
        // the tick thread reads the position now
        Assert.Equal(new WorldPosition(20, 0, 0), kiln.Run(() => Players.PositionOf(ada.Entity)));
        kiln.Step();
        Assert.Equal(new WorldPosition(20, 0, 0), Players.PositionOf(ada.Entity));
        Assert.Null(kiln.Run(() => Players.PositionOf(new EntityId(9))));  // not a player
        // tick-thread-only diagnostics refuse a worker thread, as in the game
        Assert.Throws<InvalidOperationException>(() => Players.Diagnostics.RawSlot(0));
        Assert.Throws<InvalidOperationException>(() => Players.Diagnostics.ProbePosition(ada.Entity));
    }

    [Fact]
    public void WhatTheFakeDoesNotModelAnswersUnavailable()
    {
        using var kiln = new KilnHarness();
        kiln.Boot();
        var ada = kiln.Players.Join("Ada", AdaSteam, position: new WorldPosition(1, 1, 1));
        kiln.Step();
        var status = kiln.Run(() => Players.Diagnostics.Status)!;
        Assert.True(status.Simulated);
        Assert.Equal(PlayerSource.Records, status.Source);
        Assert.False(status.Records);
        Assert.Equal(16, status.Slots);
        Assert.Equal(1, status.InGame);
        Assert.Null(kiln.Run(() => Players.Diagnostics.RawSlot(0)));  // no game record behind the player
        var replay = Assert.Throws<InvalidOperationException>(() => kiln.Run(() => Players.Diagnostics.ReplayLog(["[server] Player 'Ada' logged in"])));
        Assert.Contains("(3)", replay.Message);  // KILN_ERR_UNAVAILABLE
        Assert.Equal(new WorldPosition(1, 1, 1), kiln.Run(() => Players.Diagnostics.ProbePosition(ada.Entity)));
        Assert.Null(kiln.Run(() => Players.Diagnostics.ProbePosition(new EntityId(2000))));  // no world for other entities
    }

    [Fact]
    public void SwitchedOffThePlayersAreUnavailableAndWithNoHarnessToo()
    {
        using (var kiln = new KilnHarness())
        {
            kiln.Boot();
            kiln.Players.Join("Ada", AdaSteam);
            kiln.Step();
            Assert.True(kiln.Run(() => Players.Available));
            kiln.Capabilities.Override("kiln.players", false);
            Assert.False(kiln.Run(() => Players.Available));
            Assert.Throws<NotSupportedException>(() => kiln.Run(() => Players.All));
            kiln.Capabilities.ClearOverride("kiln.players");
            Assert.Single(kiln.Run(() => Players.All));
        }
        Assert.False(Players.Available);
        Assert.Equal(PlayerSource.None, Players.Source);
    }

    [Fact]
    public void TheNextHarnessStartsWithNoPlayersAndSequencesGoOn()
    {
        ulong first;
        using (var kiln = new KilnHarness())
        {
            kiln.Load<TkPlayersMod>();
            kiln.Step();
            kiln.Players.Join("Ada", AdaSteam);
            kiln.Players.Join("Bob", BobSteam);
            kiln.Step();
            first = Players.Diagnostics.Status!.LastSequence;
            Assert.True(first >= 2);
        }
        using (var kiln = new KilnHarness())
        {
            kiln.Load<TkPlayersMod>();
            kiln.Step();
            Assert.Equal(["present: "], kiln.Lines("tk-players", "present:"));
            Assert.Empty(kiln.Run(() => Players.All));
            Assert.Empty(kiln.Players.All);
            Assert.Empty(kiln.Lines("tk-players", "left:"));  // the first server's players do not leave into this one
            var cy = kiln.Players.Join("Cy");
            Assert.Equal(1u, cy.Entity.Value);
            kiln.Step();
            Assert.Single(kiln.Lines("tk-players", "joined: Cy"));
            Assert.True(SequenceOfBusJoin(kiln, "Cy") > first);
        }
    }

    private static ulong SequenceOfBusJoin(KilnHarness kiln, string name)
    {
        var line = kiln.Lines("tk-players", $"bus joined: {name} seq ").Single();
        return ulong.Parse(line[line.LastIndexOf(' ')..]);
    }

    [Fact]
    public void TheTablesMirrorTheHeaderAsTheServicesRead()
    {
        // the numbers Players.cs refuses to run without
        Assert.Equal(224, Unsafe.SizeOf<PlayersAbi.Player>());
        Assert.Equal(248, Unsafe.SizeOf<PlayersAbi.Event>());
        Assert.Equal(88, Unsafe.SizeOf<PlayersAbi.Status>());
        Assert.Equal(56, Unsafe.SizeOf<PlayersAbi.SlotRaw>());
        // and the services' own private mirrors
        var players = typeof(Players);
        Assert.Equal(Unsafe.SizeOf<PlayersAbi.Player>(), Mirror.Size(players, "NativePlayer"));
        Assert.Equal(Unsafe.SizeOf<PlayersAbi.Event>(), Mirror.Size(players, "NativeEvent"));
        Assert.Equal(Unsafe.SizeOf<PlayersAbi.Status>(), Mirror.Size(players, "NativeStatus"));
        Assert.Equal(Unsafe.SizeOf<PlayersAbi.SlotRaw>(), Mirror.Size(players, "NativeSlotRaw"));
        Assert.Equal(Unsafe.SizeOf<PlayersAbi.Table>(), Mirror.Size(players, "Table"));
        var saves = typeof(WorldData).Assembly;
        Assert.Equal(Unsafe.SizeOf<PlayersAbi.Player>(), Mirror.Size(saves.GetType("Kiln.PlayersLink", true)!, "Player"));
        var savesNative = saves.GetType("Kiln.SavesNative", true)!;
        Assert.Equal(Unsafe.SizeOf<SavesAbi.Event>(), Mirror.Size(savesNative, "Event"));
        Assert.Equal(Unsafe.SizeOf<SavesAbi.Status>(), Mirror.Size(savesNative, "Status"));
        Assert.Equal(Unsafe.SizeOf<SavesAbi.Table>(), Mirror.Size(savesNative, "Table"));
    }
}

internal static class Mirror
{
    /// <summary>The size of the nested struct <paramref name="name"/> of <paramref name="owner"/> (a service's private mirror).</summary>
    public static int Size(Type owner, string name)
    {
        var t = owner.GetNestedType(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"{owner.FullName} has no nested type {name} any more");
        return (int)typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!.MakeGenericMethod(t).Invoke(null, null)!;
    }
}
