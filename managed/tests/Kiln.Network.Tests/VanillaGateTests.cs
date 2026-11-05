// SPDX-License-Identifier: MIT
using System.Text.Json;
using Xunit;

namespace Kiln.Network.Tests;

public sealed class VanillaGateTests
{
    private static readonly DateTime T0 = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly NetworkConfig Warn = new();
    private static readonly NetworkConfig Refuse = new() { ModSync = ModSyncPolicy.Refuse };
    private static readonly NetworkConfig Off = new() { ModSync = ModSyncPolicy.Off };
    private static readonly NetworkConfig Require = new() { ModSync = ModSyncPolicy.Off, RequireKiln = true };

    [Fact] public void Nothing_at_stake_lets_a_vanilla_client_in() => Assert.False(VanillaGate.ShouldRefuse(Warn, false, 0, 0, true));
    [Fact] public void A_mod_stating_clients_refuses() => Assert.True(VanillaGate.ShouldRefuse(Warn, true, 1, 0, true));
    [Fact] public void Modsync_off_lets_it_in_despite_such_a_mod() => Assert.False(VanillaGate.ShouldRefuse(Off, true, 1, 3, true));
    [Fact] public void Modsync_refuse_refuses_when_something_is_required() => Assert.True(VanillaGate.ShouldRefuse(Refuse, true, 0, 0, true));
    [Fact] public void Modsync_refuse_without_anything_required_lets_it_in() => Assert.False(VanillaGate.ShouldRefuse(Refuse, false, 0, 0, true));
    [Fact] public void Require_kiln_always_refuses() => Assert.True(VanillaGate.ShouldRefuse(Require, false, 0, 0, true));

    [Fact]
    public void Kiln_only_templates_that_can_be_withheld_let_it_in() => Assert.False(VanillaGate.ShouldRefuse(Warn, false, 0, 2, true));

    [Fact]
    public void Kiln_only_templates_that_cannot_be_withheld_refuse_under_every_policy()
    {
        Assert.True(VanillaGate.ShouldRefuse(Warn, false, 0, 2, false));
        Assert.True(VanillaGate.ShouldRefuse(Off, false, 0, 2, false));
    }

    [Fact]
    public void Missing_withholding_alone_changes_nothing_when_no_template_needs_it() => Assert.False(VanillaGate.ShouldRefuse(Off, false, 0, 0, false));

    [Fact]
    public void No_wait_when_nothing_is_at_stake() => Assert.Equal(NetNative.GateOff, VanillaGate.GateMode(true, false, 0));

    [Fact]
    public void Waits_when_refusing_or_when_templates_exist_or_before_the_mods_are_known()
    {
        Assert.Equal(NetNative.GateHold, VanillaGate.GateMode(true, true, 0));
        Assert.Equal(NetNative.GateHold, VanillaGate.GateMode(true, false, 1));
        Assert.Equal(NetNative.GateHold, VanillaGate.GateMode(false, false, 0));
    }

    [Fact]
    public void A_hold_ends_at_its_deadline_not_before()
    {
        var c = new NetworkConfig { VanillaHoldSeconds = 5 };
        Assert.False(VanillaGate.TimeIsUp(NetPeerState.Connecting, false, T0, null, T0.AddSeconds(4.9), c));
        Assert.True(VanillaGate.TimeIsUp(NetPeerState.Connecting, false, T0, null, T0.AddSeconds(5), c));
    }

    [Fact]
    public void A_hold_uses_its_own_deadline_not_the_in_game_one()
    {
        var c = new NetworkConfig { VanillaHoldSeconds = 2, HandshakeTimeoutSeconds = 100 };
        Assert.True(VanillaGate.TimeIsUp(NetPeerState.Connecting, false, T0, T0, T0.AddSeconds(2), c));
    }

    [Fact]
    public void Past_the_gate_the_in_game_timer_applies() // a build without the gate
    {
        var c = new NetworkConfig { HandshakeTimeoutSeconds = 20 };
        Assert.False(VanillaGate.TimeIsUp(NetPeerState.Connecting, false, null, T0, T0.AddSeconds(19), c));
        Assert.True(VanillaGate.TimeIsUp(NetPeerState.Connecting, false, null, T0, T0.AddSeconds(20), c));
    }

    [Fact]
    public void Still_loading_never_counts() => Assert.False(VanillaGate.TimeIsUp(NetPeerState.Connecting, false, null, null, T0.AddHours(1), Warn));

    [Theory]
    [InlineData(NetPeerState.Ready)]
    [InlineData(NetPeerState.Vanilla)]
    [InlineData(NetPeerState.Rejected)]
    [InlineData(NetPeerState.Closed)]
    public void Only_an_undecided_peer_is_judged(NetPeerState s) => Assert.False(VanillaGate.TimeIsUp(s, false, T0, T0, T0.AddHours(1), Warn));

    [Fact]
    public void Lab_peers_are_judged_only_when_a_test_puts_them_in_the_gate()
    {
        Assert.False(VanillaGate.TimeIsUp(NetPeerState.Connecting, true, null, T0, T0.AddHours(1), Warn));
        Assert.True(VanillaGate.TimeIsUp(NetPeerState.Connecting, true, T0, null, T0.AddHours(1), Warn));
    }

    [Theory]
    [InlineData("{}", 5)]
    [InlineData("{\"vanillaHoldSeconds\": 8}", 8)]
    [InlineData("{\"vanillaHoldSeconds\": 0}", 1)]
    [InlineData("{\"vanillaHoldSeconds\": 500}", 20)]
    public void The_hold_is_short_whatever_the_config_says(string json, double want)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(want, NetworkConfig.Parse(doc.RootElement).VanillaHoldSeconds);
    }

    private static readonly HashSet<string> HasOne = new(StringComparer.Ordinal) { "has-it" };

    private static NetworkConfig Parsed(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return NetworkConfig.Parse(doc.RootElement);
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"modSync\": \"warn\"}", true)]
    [InlineData("{\"modSync\": \"Off\"}", true)]
    [InlineData("{\"modSync\": \"refuse\"}", true)]
    [InlineData("{\"modSync\": \"nonsense\"}", false)]
    public void Only_a_real_value_counts_as_stating_modsync(string json, bool stated)
    {
        var c = Parsed(json);
        Assert.Equal(stated, c.ModSyncStated);
    }

    [Fact]
    public void By_default_a_kiln_client_needs_the_mods_that_state_clients_mod() =>
        Assert.Equal(["lacks-it"], VanillaGate.MissingStatedMods(Parsed("{}"), ["has-it", "lacks-it"], HasOne));

    [Theory]
    [InlineData("warn")]
    [InlineData("off")]
    [InlineData("refuse")] // its own wider rule in the handshake
    public void A_stated_modsync_decides_for_itself(string policy) =>
        Assert.Empty(VanillaGate.MissingStatedMods(Parsed($"{{\"modSync\": \"{policy}\"}}"), ["lacks-it"], HasOne));

    [Fact]
    public void An_optional_mod_is_not_needed() =>
        Assert.Empty(VanillaGate.MissingStatedMods(Parsed("{\"optionalMods\": [\"lacks-it\"]}"), ["lacks-it"], HasOne));

    [Fact]
    public void Kiln_only_entities_wait_for_a_content_ack_only_if_one_can_help()
    {
        Assert.True(VanillaGate.AwaitsDefinitions(1, true));
        Assert.False(VanillaGate.AwaitsDefinitions(0, true));
        Assert.False(VanillaGate.AwaitsDefinitions(1, false));
    }

    [Fact]
    public void The_wait_for_definitions_has_a_deadline()
    {
        var c = new NetworkConfig { DefinitionsWaitSeconds = 5 };
        Assert.False(VanillaGate.DefinitionsWaitOver(null, T0, c));
        Assert.False(VanillaGate.DefinitionsWaitOver(T0, T0.AddSeconds(4.9), c));
        Assert.True(VanillaGate.DefinitionsWaitOver(T0, T0.AddSeconds(5), c));
    }

    [Fact]
    public void The_knock_is_a_kiln_frame_header()
    {
        Assert.True(Wire.TryHeader(new byte[] { (byte)'K', (byte)'N', 1, 10 }, out var type));
        Assert.Equal(Wire.Type.Knock, type);
    }
}
