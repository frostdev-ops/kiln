// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using Xunit;

namespace Kiln.Network.Tests;

/// <summary>NetAdmission's registry on a fake gate: which mode the gate is set to, who keeps a peer out, deadlines,
/// refusals and the unavailable paths. The transport half is tools/nettest's admission cases.</summary>
[Collection(nameof(SteamGuardRelayTests))]  // the engine's statics (TestTransport, TestServerRole): never at once
public sealed class AdmissionTests : IDisposable
{
    private const uint A = 0xA1, B = 0xA2;
    private readonly FakeGate _gate = new();
    private readonly FakeNet _net = new();

    internal sealed class FakeGate : NetAdmission.IGate
    {
        public uint Caps = NetNative.GateCanHold | NetAdmission.GateCanHoldAll;
        public uint Mode = uint.MaxValue;
        public readonly List<uint> Modes = new();
        public readonly List<uint> Released = new();
        public readonly List<uint> Kicked = new();
        public int Set(uint mode)
        {
            Mode = mode;
            Modes.Add(mode);
            return NetNative.Ok;
        }
        public int Release(uint peer)
        {
            Released.Add(peer);
            return NetNative.Ok;
        }
        public NetNative.GateInfo Read() => new() { Caps = Caps, Mode = Mode };
        public int Kick(uint peer, uint reason)
        {
            Kicked.Add(peer);
            return NetNative.Ok;
        }
    }

    internal sealed class FakeNet : INetTransport
    {
        public readonly ConcurrentDictionary<uint, uint> Flags = new();
        public readonly ConcurrentQueue<(uint Peer, byte[] Frame)> Sent = new();
        public int Send(uint peer, byte[] frame)
        {
            Sent.Enqueue((peer, frame));
            return NetNative.Ok;
        }
        public NetNative.Status Status() => new() { Roles = NetNative.RoleServer, MaxFrame = 1200 };
        public NetNative.PeerInfo? Info(uint peer) => new NetNative.PeerInfo { Peer = peer, Flags = Flags.GetValueOrDefault(peer) };
        public int Kick(uint peer, uint reason) => NetNative.Ok;
    }

    public AdmissionTests()
    {
        NetNative.TestTransport = _net;
        NetEngine.TestServerRole = true;
        NetAdmission.ResetForTests();
        NetAdmission.TestGate = _gate;
    }

    public void Dispose()
    {
        foreach (var p in new[] { A, B }) NetEngine.OnPeer(p, false);
        NetAdmission.ResetForTests();
        NetAdmission.TestGate = null;
        NetNative.TestTransport = null;
        NetEngine.TestServerRole = null;
    }

    private NetPeer Up(uint id, bool held = false)
    {
        _net.Flags[id] = held ? NetNative.PeerHeld : 0;
        NetEngine.OnPeer(id, true);
        var p = NetEngine.ServerPeers().Single(x => x.Id == id);
        if (held) p.HeldSince = DateTime.UtcNow;
        return p;
    }

    private static readonly TimeSpan Ten = TimeSpan.FromSeconds(10);

    [Fact]
    public void With_nothing_registered_the_gate_follows_todays_rule()
    {
        NetAdmission.UpdateGate(modListKnown: true, builtInRefuse: false, kilnTemplates: 0);
        Assert.Equal(NetNative.GateOff, _gate.Mode);
        NetAdmission.UpdateGate(true, true, 0);
        Assert.Equal(NetNative.GateHold, _gate.Mode);
        NetAdmission.UpdateGate(false, false, 0);
        Assert.Equal(NetNative.GateHold, _gate.Mode);
        NetAdmission.UpdateGate(true, false, 3);
        Assert.Equal(NetNative.GateHold, _gate.Mode);
        Assert.Equal([NetNative.GateOff, NetNative.GateHold], _gate.Modes.Distinct());  // set only when it changes
        Assert.Equal(2, _gate.Modes.Count);
    }

    [Fact]
    public void Two_holders_one_ends_the_peer_stays_held()
    {
        var p = Up(A, held: true);
        var first = NetAdmission.HoldPeer(p, "one", "waits for one", Ten);
        var second = NetAdmission.HoldPeer(p, "two", "waits for two", Ten);
        Assert.False(NetAdmission.MayRelease(p, out var holder));
        Assert.Equal("one", holder);
        first.End();
        Assert.False(NetAdmission.MayRelease(p, out holder));
        Assert.Equal("two", holder);
        second.Dispose();
        Assert.True(NetAdmission.MayRelease(p, out holder));
        Assert.Null(holder);
    }

    [Fact]
    public void A_join_hold_keeps_every_peer_out_while_it_lives()
    {
        var a = Up(A, held: true);
        var b = Up(B, held: true);
        var h = NetAdmission.HoldJoins("menus", "menu declarations are not ready", Ten);
        Assert.False(NetAdmission.MayRelease(a, out var holder));
        Assert.Equal("menus", holder);
        Assert.False(NetAdmission.MayRelease(b, out _));
        var peerHold = NetAdmission.HoldPeer(b, "policy", "waits for its ack", Ten);
        h.End();
        Assert.True(NetAdmission.MayRelease(a, out _));
        Assert.False(NetAdmission.MayRelease(b, out holder));  // its own hold still lives
        Assert.Equal("policy", holder);
        peerHold.End();
        Assert.True(NetAdmission.MayRelease(b, out _));
    }

    [Fact]
    public void The_gate_goes_off_only_when_no_requirement_wants_a_hold()
    {
        NetAdmission.UpdateGate(true, false, 0);
        Assert.Equal(NetNative.GateOff, _gate.Mode);
        var joins = NetAdmission.HoldJoins("x", "x", Ten);
        NetAdmission.UpdateGate(true, false, 0);
        Assert.Equal(NetAdmission.GateHoldAll, _gate.Mode);
        var vanilla = NetAdmission.Require("remap", () => false, "no remap");
        joins.End();
        NetAdmission.UpdateGate(true, false, 0);
        Assert.Equal(NetNative.GateHold, _gate.Mode);  // only clients without Kiln wait now
        vanilla.Dispose();
        NetAdmission.UpdateGate(true, false, 0);
        Assert.Equal(NetNative.GateOff, _gate.Mode);
    }

    [Fact]
    public void Each_kind_of_requirement_wants_the_hold_all_mode_and_is_listed()
    {
        var p = Up(A, held: true);
        using var onJoin = NetAdmission.OnJoin("late-join", _ => null);
        Assert.True(NetAdmission.HoldsKilnPeers);
        Assert.Contains(NetAdmission.Active, e => e is { Kind: AdmissionKind.OnJoin, Holder: "late-join" });
        onJoin.Dispose();
        Assert.False(NetAdmission.HoldsKilnPeers);
        var h = NetAdmission.HoldPeer(p, "ack", "waits for the ack", Ten);
        Assert.Contains(NetAdmission.Active, e => e is { Kind: AdmissionKind.PeerHold, Holder: "ack", Peer: A } && e.Deadline != null);
        h.End();
        p.Declared["kiln:x"] = "1";  // a connected peer without it would keep it from being installed
        Assert.Equal(AdmissionResult.Success, NetAdmission.SetMandatoryProtocol("kiln:x", "1", true, "x protocol 1 required"));
        Assert.True(NetAdmission.HoldsKilnPeers);
        Assert.Contains(NetAdmission.Active, e => e is { Kind: AdmissionKind.MandatoryProtocol, Holder: "kiln:x" });
        NetAdmission.SetMandatoryProtocol("kiln:x", "1", false, "");
        Assert.False(NetAdmission.HoldsKilnPeers);
    }

    [Fact]
    public void Vanilla_is_refused_by_the_built_in_rule_a_protocol_or_a_condition()
    {
        Assert.False(NetAdmission.VanillaRefused(false, out _));
        Assert.True(NetAdmission.VanillaRefused(true, out var why));
        Assert.Null(why);
        var allowed = true;
        using (NetAdmission.Require("remap", () => allowed, "Kiln-only items without a look-alike"))
        {
            Assert.False(NetAdmission.VanillaRefused(false, out _));
            allowed = false;
            Assert.True(NetAdmission.VanillaRefused(false, out why));
            Assert.Equal("Kiln-only items without a look-alike", why);
        }
        Assert.False(NetAdmission.VanillaRefused(false, out _));
        using (NetAdmission.Require("broken", () => throw new InvalidOperationException("boom"), "broken says no"))
            Assert.True(NetAdmission.VanillaRefused(false, out why));  // a condition that throws refuses
        NetAdmission.SetMandatoryProtocol("kiln:x", "1", true, "x protocol 1 required");
        Assert.True(NetAdmission.VanillaRefused(false, out why));
        Assert.Equal("x protocol 1 required", why);
    }

    [Fact]
    public void A_peer_hold_past_its_deadline_refuses_the_peer_with_the_holders_reason()
    {
        var p = Up(A, held: true);
        var h = NetAdmission.HoldPeer(p, "policy", "base policy ack missing", TimeSpan.FromSeconds(1));
        NetAdmission.Expire(DateTime.UtcNow);
        Assert.False(h.Ended);
        NetAdmission.Expire(DateTime.UtcNow.AddSeconds(2));
        Assert.True(h.Ended);
        Assert.Equal(NetPeerState.Rejected, p.State);
        Assert.NotNull(p.KickAt);
        Assert.Contains(_net.Sent, s => s.Peer == A && Wire.TryHeader(s.Frame, out var t) && t == Wire.Type.Reject &&
                                        Wire.DecodeReject(s.Frame).Reason == "base policy ack missing");
        Assert.True(NetAdmission.MayRelease(p, out _));  // nothing holds it; it is on its way out
        Assert.Empty(_gate.Released);
    }

    [Fact]
    public void A_join_hold_past_its_deadline_ends_and_lets_the_others_decide()
    {
        var p = Up(A, held: true);
        var h = NetAdmission.HoldJoins("menus", "menus", TimeSpan.FromSeconds(1));
        NetAdmission.Expire(DateTime.UtcNow.AddSeconds(2));
        Assert.True(h.Ended);
        Assert.Equal(NetPeerState.Connecting, p.State);  // not refused
        Assert.True(NetAdmission.MayRelease(p, out _));
    }

    [Fact]
    public void Refusing_a_join_hold_refuses_the_peers_it_holds()
    {
        var a = Up(A, held: true);
        var b = Up(B);  // not held: not in the gate yet
        var h = NetAdmission.HoldJoins("policy", "policy change", Ten);
        h.Refuse("the policy change failed");
        Assert.Equal(NetPeerState.Rejected, a.State);
        Assert.NotEqual(NetPeerState.Rejected, b.State);
        Assert.False(NetAdmission.HoldsKilnPeers);
    }

    [Fact]
    public void Deadlines_are_bounded()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NetAdmission.HoldJoins("x", "x", TimeSpan.FromSeconds(26)));
        Assert.Throws<ArgumentOutOfRangeException>(() => NetAdmission.HoldJoins("x", "x", TimeSpan.Zero));
        Assert.False(NetAdmission.HoldsKilnPeers);
    }

    [Fact]
    public void Without_hold_all_the_kiln_holds_are_unavailable()
    {
        var p = Up(A, held: true);
        _gate.Caps = NetNative.GateCanHold;
        var e = Assert.Throws<KilnUnavailableException>(() => NetAdmission.HoldJoins("x", "x", Ten));
        Assert.Equal("net.gate.hold_all", e.Capability);
        Assert.Equal("net.gate.hold_all", Assert.Throws<KilnUnavailableException>(() => NetAdmission.HoldPeer(p, "x", "x", Ten)).Capability);
        Assert.Equal("net.gate.hold_all", Assert.Throws<KilnUnavailableException>(() => NetAdmission.OnJoin("x", _ => null)).Capability);
        Assert.False(NetAdmission.CanHoldKilnPeers);
        // a protocol needs only the plain gate: clients without it are refused in the hello
        Assert.False(NetAdmission.SetMandatoryProtocol("kiln:x", "1", true, "x").Ok);  // peer A lacks it: not installed
        Assert.False(NetAdmission.HoldsKilnPeers);
        p.Declared["kiln:x"] = "1";
        Assert.True(NetAdmission.SetMandatoryProtocol("kiln:x", "1", true, "x").Ok);
        NetAdmission.UpdateGate(true, false, 0);
        Assert.Equal(NetNative.GateHold, _gate.Mode);  // no hold-all here: today's hold for clients without Kiln
    }

    [Fact]
    public void Without_the_gate_hook_holds_and_protocols_are_refused()
    {
        _gate.Caps = 0;
        Assert.Equal("net.gate", Assert.Throws<KilnUnavailableException>(() => NetAdmission.HoldJoins("x", "x", Ten)).Capability);
        var r = NetAdmission.SetMandatoryProtocol("kiln:x", "1", true, "x");
        Assert.False(r.Ok);
        Assert.Contains("net.gate", r.Reason);
        Assert.False(NetAdmission.HoldsKilnPeers);
    }

    [Fact]
    public void Without_a_server_role_every_call_answers_ok_and_registers_nothing()
    {
        NetEngine.TestServerRole = false;
        Assert.True(NetAdmission.SetMandatoryProtocol("kiln:x", "1", true, "x").Ok);
        Assert.True(NetAdmission.HoldJoins("x", "x", Ten).Ended);
        NetAdmission.OnJoin("x", _ => null).Dispose();
        NetAdmission.Require("x", () => false, "x").Dispose();
        Assert.False(NetAdmission.HoldsKilnPeers);
        Assert.Empty(NetAdmission.Active.Where(e => e.Kind != AdmissionKind.BuiltIn));
    }

    [Fact]
    public void A_peer_in_the_world_is_not_held_again_and_a_leaving_peer_takes_its_holds()
    {
        var p = Up(A, held: true);
        p.InGameSince = DateTime.UtcNow;
        Assert.True(NetAdmission.HoldPeer(p, "late", "x", Ten).Ended);
        var q = Up(B, held: true);
        var h = NetAdmission.HoldPeer(q, "ack", "x", Ten);
        NetEngine.OnPeer(B, false);
        Assert.True(h.Ended);
        Assert.False(NetAdmission.HoldsKilnPeers);
    }

    [Fact]
    public void Letting_a_vanilla_client_in_or_refusing_it_goes_through_one_place()
    {
        var p = Up(A, held: true);
        NetAdmission.DecideVanilla(p, refuse: false, "unused", DateTime.UtcNow);
        Assert.Equal([A], _gate.Released);
        Assert.True(NetAdmission.InWorld(p));
        var q = Up(B, held: true);
        NetAdmission.DecideVanilla(q, refuse: true, "needs Kiln", DateTime.UtcNow);
        Assert.NotNull(q.KickAt);
        Assert.Single(_gate.Released);
        Assert.Contains(_net.Sent, s => s.Peer == B && Wire.DecodeReject(s.Frame).Reason == "needs Kiln");
    }
}
