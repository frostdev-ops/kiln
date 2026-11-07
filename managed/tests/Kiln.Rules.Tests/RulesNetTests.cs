// SPDX-License-Identifier: MIT
using System.Collections;
using System.Reflection;
using Xunit;

namespace Kiln.Rules.Tests;

public sealed class RulesNetTests
{
    // Exercise the real network remappers without a native transport or a running game.
    private static readonly Type Engine = typeof(Network).Assembly.GetType("Kiln.NetEngine", throwOnError: true)!;
    private static readonly string ChannelName = (string)typeof(RulesNet).Assembly.GetType("Kiln.Rules")!
        .GetField("ChannelName")!.GetRawConstantValue()!;
    private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;

    private static NetPeer Peer(uint id)
    {
        var p = (NetPeer)Activator.CreateInstance(typeof(NetPeer), InstancePrivate, null, [id], null)!;
        typeof(NetPeer).GetProperty(nameof(NetPeer.State))!.SetValue(p, NetPeerState.Ready);
        return p;
    }

    private static void Remap(string method, NetPeer peer) => Engine.GetMethod(method, StaticPrivate)!.Invoke(null, [peer]);

    [Fact]
    public void FlightRequiresAnAcknowledgedLiveConnectionAndCannotCarryAcrossReusedPeerIds()
    {
        var a = Peer(100);
        var reused = Peer(100);
        Assert.False(RulesNet.FlightReady(a));
        RulesNet.FlightCapabilityOf(a, true);
        try
        {
            Assert.True(RulesNet.FlightReady(a));
            Assert.False(RulesNet.FlightReady(reused));
            RulesNet.FlightCapabilityOf(a, false);
            Assert.False(RulesNet.FlightReady(a)); // a missing pattern or late Flame start withdraws the acknowledgement
            RulesNet.FlightCapabilityOf(a, true);
            typeof(NetPeer).GetProperty(nameof(NetPeer.State))!.SetValue(a, NetPeerState.Closed);
            Assert.False(RulesNet.FlightReady(a));
            var part = new RulesNativePart(RulesNative.CharacterFlight | RulesNative.FlyAnywhere, 0, 1, 1);
            Assert.Equal(RulesNative.FlyAnywhere, part.WithoutFlight.Flags);
        }
        finally { RulesNet.FlightCapabilityOf(a, false); }
    }

    [Fact]
    public void NativeRulesCanBeFormattedWithoutRecursingThroughWithoutGlide()
    {
        var part = new RulesNativePart(RulesNative.GlideBoost, 1, 1, 1, GlideClimb: 15);
        Assert.Contains("flags 0x100", part.ToString());
        Assert.Contains("climb 15", part.ToString());
        Assert.Contains("climb 0", part.WithoutGlide.ToString());
    }

    [Theory]
    [InlineData("2")]
    [InlineData("3")]
    public void LegacyPeerGetsNoFlightRulesOrStaleChannelMapping(string legacy)
    {
        Assert.Equal("4", RulesNet.ChannelVersion);
        using var channel = Network.Channel(ChannelName, RulesNet.ChannelVersion, required: false);
        var peer = Peer(1);
        var declared = (Dictionary<string, string>)typeof(NetPeer).GetField("Declared", InstancePrivate)!.GetValue(peer)!;

        declared[ChannelName] = legacy;
        Remap("RemapServerChannels", peer);
        Assert.False(peer.HasChannel(ChannelName));
        Assert.False(channel.SendRaw(peer, 0, new byte[44]));
        Assert.Equal(0, peer.MessagesOut);

        declared[ChannelName] = RulesNet.ChannelVersion;
        Remap("RemapServerChannels", peer);
        Assert.True(peer.HasChannel(ChannelName));

        declared[ChannelName] = legacy;
        Remap("RemapServerChannels", peer);
        Assert.False(peer.HasChannel(ChannelName));
        Assert.False(channel.SendRaw(peer, 0, new byte[44]));
    }

    [Fact]
    public void VersionThreeClientDoesNotMapAVersionTwoServersRules()
    {
        using var channel = Network.Channel(ChannelName, RulesNet.ChannelVersion, required: false);
        var tableField = Engine.GetField("_serverTable", StaticPrivate)!;
        var previous = tableField.GetValue(null);
        var entryType = tableField.FieldType.GetGenericArguments()[0];
        var peer = Peer(uint.MaxValue);
        try
        {
            foreach (var (version, expected) in new[] { (RulesNet.ChannelVersion, true), ("2", false) })
            {
                var table = (IList)Activator.CreateInstance(tableField.FieldType)!;
                table.Add(Activator.CreateInstance(entryType, (ushort)7, ChannelName, version, false));
                tableField.SetValue(null, table);
                Remap("RemapClientChannels", peer);
                Assert.Equal(expected, peer.HasChannel(ChannelName));
                var names = (Dictionary<ushort, string>)typeof(NetPeer).GetField("ChannelNames", InstancePrivate)!.GetValue(peer)!;
                Assert.Equal(expected, names.ContainsKey(7));
            }
        }
        finally
        {
            tableField.SetValue(null, previous);
        }
    }

    [Fact]
    public async Task LocalRulesArePublishedAsOneSnapshotAcrossThreads()
    {
        var previous = RulesNet.Local;
        var a = new RulesNativePart(1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1);
        var b = new RulesNativePart(2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2);
        using var start = new ManualResetEventSlim();
        RulesNet.SetLocal(a);
        try
        {
            var writer = Task.Run(() =>
            {
                start.Wait(TestContext.Current.CancellationToken);
                for (var i = 0; i < 200_000; i++) RulesNet.SetLocal((i & 1) == 0 ? b : a);
            }, TestContext.Current.CancellationToken);
            var reader = Task.Run(() =>
            {
                start.Wait(TestContext.Current.CancellationToken);
                for (var i = 0; i < 200_000; i++)
                {
                    var got = RulesNet.Local;
                    Assert.True(got == a || got == b, $"Torn local rules: flags {got.Flags}, cost {got.BuildCost}, climb {got.GlideClimb}");
                }
            }, TestContext.Current.CancellationToken);
            start.Set();
            await Task.WhenAll(writer, reader);
        }
        finally
        {
            RulesNet.SetLocal(previous);
        }
    }
}
