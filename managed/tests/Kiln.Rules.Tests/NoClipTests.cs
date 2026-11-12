// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Rules.Tests;

/// <summary>NoClip as a rule: the server's limit, how rule sets combine, what the hooks and a player's game get, which
/// requests a death or a teleport used up, and what counts as open space.</summary>
public class NoClipTests
{
    private static readonly EntityId P = new(7);
    private static readonly RulesLimits Allowed = RulesLimits.Default with { AllowNoClip = true };

    private static EffectiveRules Combine(RulesLimits limits, params PlayerRules[] sets) =>
        RulesMath.Combine(P, sets.Select(r => ((RuleSet?)null, r)).ToList(), limits);

    [Fact]
    public void NoClipIsOffUntilTheServerAllowsIt()
    {
        var asked = new PlayerRules { NoClip = true };
        var held = Combine(RulesLimits.Default, asked);
        Assert.False(held.NoClip);
        Assert.Contains("noClip", held.Denied);
        Assert.Equal(0u, held.Native.Flags & RulesNative.NoClip);
        Assert.True(held.IsDefault);
        Assert.False(new RulesPolicy().AllowNoClip);
        Assert.False(new RulesPolicy().Limits.AllowNoClip);
        Assert.True(new RulesPolicy { AllowNoClip = true }.Limits.AllowNoClip);

        var on = Combine(Allowed, asked);
        Assert.True(on.NoClip);
        Assert.Empty(on.Denied);
        Assert.Equal(RulesNative.NoClip, on.Native.Flags);  // its own flag: no character flight, no building flight
        Assert.Contains("NoClip", on.ToString());
        Assert.False(on.IsDefault);
    }

    [Fact]
    public void ItIsSeparateFromCharacterFlightAndCreativeMode()
    {
        var both = RulesLimits.Default with { AllowNoClip = true, AllowCharacterFlight = true };
        Assert.Equal(RulesNative.NoClip | RulesNative.CharacterFlight,
            Combine(both, new PlayerRules { NoClip = true, CharacterFlight = CharacterFlightMode.Unlimited }).Native.Flags);
        // the flight limit does not gate it, and it does not open flight
        var clipOnly = Combine(Allowed, new PlayerRules { NoClip = true, CharacterFlight = CharacterFlightMode.Unlimited });
        Assert.True(clipOnly.NoClip);
        Assert.Equal(CharacterFlightMode.Off, clipOnly.CharacterFlight);
        Assert.Null(PlayerRules.Creative.NoClip);
        Assert.Null(PlayerRules.CreativeExtras.NoClip);
        Assert.False(Combine(Allowed, PlayerRules.Creative).NoClip);
    }

    [Fact]
    public void AnExplicitFalseWinsOverEveryRequest()
    {
        var yes = new PlayerRules { NoClip = true };
        var no = new PlayerRules { NoClip = false };
        Assert.True(Combine(Allowed, yes, new PlayerRules()).NoClip);
        Assert.False(Combine(Allowed, yes, no).NoClip);
        Assert.False(Combine(Allowed, no, yes, yes).NoClip);
        Assert.False(Combine(Allowed, new PlayerRules()).NoClip);
    }

    [Fact]
    public void ItReadsAndWritesJsonAndMerges()
    {
        var r = PlayerRules.FromJson("""{ "noClip": true }""");
        Assert.True(r.NoClip);
        Assert.Contains("\"noClip\": true", r.ToJson());
        Assert.False(r.With(new PlayerRules { NoClip = false }).NoClip);
        Assert.True(r.With(new PlayerRules()).NoClip);
        Assert.Null(new PlayerRules().With(new PlayerRules()).NoClip);
        Assert.Empty(r.Problems());
    }

    [Fact]
    public void WithoutTheCollisionHookItIsDenied()
    {
        var on = Combine(Allowed, new PlayerRules { NoClip = true });
        var none = RulesCrafting.Supported(on, RulesNative.HookFlight);  // flight's hooks alone are not enough
        Assert.False(none.NoClip);
        Assert.Contains("noClipUnavailable", none.Denied);
        Assert.Same(on, RulesCrafting.Supported(on, RulesNative.HookNoClip | RulesNative.HookFlight));
        var off = Combine(Allowed, new PlayerRules());
        Assert.Same(off, RulesCrafting.Supported(off, 0));  // nothing asked, nothing denied
    }

    [Fact]
    public void AGameThatDoesNotPredictItGetsNone()
    {
        var part = Combine(Allowed, new PlayerRules { NoClip = true, FlyAnywhere = true }).Native;
        Assert.True(part.HasPredicted);  // NoClip alone is predicted movement
        Assert.Equal(RulesNative.FlyAnywhere, part.WithoutNoClip.Flags);
        Assert.Equal(RulesNative.FlyAnywhere, part.WithoutPredicted.Flags);
        Assert.Equal(part.Flags, part.WithoutFlight.Flags);  // taking character flight away leaves NoClip (its own confirmation decides)
        Assert.Equal(part.Flags, part.WithoutSteer.Flags);
        Assert.False(new RulesNativePart(RulesNative.FlyAnywhere, 1, 1, 1).HasPredicted);
    }

    [Fact]
    public void AGameConfirmsTheHookPerConnection()
    {
        var a = (NetPeer)Activator.CreateInstance(typeof(NetPeer), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, [(uint)200], null)!;
        typeof(NetPeer).GetProperty(nameof(NetPeer.State))!.SetValue(a, NetPeerState.Ready);
        Assert.False(RulesNet.NoClipReady(a));
        Assert.False(RulesNet.NoClipReady(null));
        RulesNet.ClientHooksOf(a, RulesNative.HookGlideSteer);  // an older game: steering only
        try
        {
            Assert.False(RulesNet.NoClipReady(a));
            Assert.True(RulesNet.SteerReady(a));
            RulesNet.ClientHooksOf(a, RulesNative.HookNoClip | RulesNative.HookGlideSteer);
            Assert.True(RulesNet.NoClipReady(a));
            RulesNet.ClientHooksOf(a, RulesNative.HookGlideSteer);  // the hook went away (a late Creative Mode start)
            Assert.False(RulesNet.NoClipReady(a));
            RulesNet.ClientHooksOf(a, RulesNative.HookNoClip);
            typeof(NetPeer).GetProperty(nameof(NetPeer.State))!.SetValue(a, NetPeerState.Closed);
            Assert.False(RulesNet.NoClipReady(a));  // a closed connection confirms nothing
        }
        finally
        {
            RulesNet.ClientHooksOf(a, 0);
        }
        // the message is the hook bits as they are
        var w = new NetWriter();
        RulesNet.WriteClientHooks(new RulesNet.ClientHooks(RulesNative.HookNoClip | RulesNative.HookGlideSteer), w);
        var r = new NetReader(w.ToArray());
        Assert.Equal(RulesNative.HookNoClip | RulesNative.HookGlideSteer, RulesNet.ReadClientHooks(ref r).Hooks);
    }

    [Fact]
    public void ADeathOrATeleportUsesUpARequestUntilItIsAskedAgain()
    {
        var yes = new PlayerRules { NoClip = true, FlyAnywhere = true };
        var spent = new HashSet<(long Id, int Version)> { (4, 0) };
        Assert.Same(yes, NoClipMath.WithoutSpent(yes, 4, 0, null));
        Assert.Same(yes, NoClipMath.WithoutSpent(yes, 5, 0, spent));   // another rule set
        Assert.Same(yes, NoClipMath.WithoutSpent(yes, 4, 1, spent));   // the same set, updated since: it asks anew
        var used = NoClipMath.WithoutSpent(yes, 4, 0, spent);
        Assert.Null(used.NoClip);
        Assert.True(used.FlyAnywhere);                                 // only NoClip is used up
        var no = new PlayerRules { NoClip = false };
        Assert.Same(no, NoClipMath.WithoutSpent(no, 4, 0, spent));     // a refusal is never used up
        var silent = new PlayerRules();
        Assert.Same(silent, NoClipMath.WithoutSpent(silent, 4, 0, spent));
        // combined: the used-up request no longer grants, another set's still does, a refusal still wins
        Assert.False(Combine(Allowed, used).NoClip);
        Assert.True(Combine(Allowed, used, yes).NoClip);
        Assert.False(Combine(Allowed, used, yes, no).NoClip);
    }

    [Fact]
    public void OpenSpaceIsJudgedAtTheKneesTheWaistAndTheHead()
    {
        var at = new WorldPosition(100, 50, 100);
        var asked = new List<double>();
        Voxel? Air(WorldPosition p) { asked.Add(Math.Round(p.Y - at.Y, 2)); return Voxel.Air; }
        Assert.Equal(NoClipSpace.Free, NoClipMath.Space(at, Air));
        Assert.Equal([0.5, 1.0, 1.7], asked);  // not the cell at the feet: standing on the ground puts matter there

        Voxel? SolidAt(WorldPosition p, double dy, byte fill) => Math.Abs(p.Y - at.Y - dy) < 0.01 ? Voxel.Of(1, fill) : Voxel.Air;
        Assert.Equal(NoClipSpace.Inside, NoClipMath.Space(at, p => SolidAt(p, 0.5, 255)));
        Assert.Equal(NoClipSpace.Inside, NoClipMath.Space(at, p => SolidAt(p, 1.0, 255)));
        Assert.Equal(NoClipSpace.Inside, NoClipMath.Space(at, p => SolidAt(p, 1.7, 255)));
        Assert.Equal(NoClipSpace.Inside, NoClipMath.Space(at, p => SolidAt(p, 1.0, 1)));  // a surface passing through the cell counts
        Assert.Equal(NoClipSpace.Inside, NoClipMath.Space(at, _ => Voxel.Of(200)));       // a building block
        // the game leaves a surface's material on the air cell next to it (fill 0): still air
        Assert.Equal(NoClipSpace.Free, NoClipMath.Space(at, _ => Voxel.Of(1, 0)));

        Assert.Equal(NoClipSpace.Unknown, NoClipMath.Space(at, _ => null));               // not loaded: not known to be free
        Assert.Equal(NoClipSpace.Unknown, NoClipMath.Space(at, p => Math.Abs(p.Y - at.Y - 1.7) < 0.01 ? null : Voxel.Air));
        Assert.Equal(NoClipSpace.Inside, NoClipMath.Space(at, p => Math.Abs(p.Y - at.Y - 0.5) < 0.01 ? null : Voxel.Of(1)));  // matter wins over unknown
    }

    [Fact]
    public void TheEndedEventSaysWhyAndWhereTheCharacterWasPut()
    {
        var moved = new PlayerNoClipEndedEvent(P, NoClipEnd.RuleEnded, new WorldPosition(1, 2, 3));
        Assert.Equal(P, moved.Player);
        Assert.Equal(NoClipEnd.RuleEnded, moved.Reason);
        Assert.Equal(new WorldPosition(1, 2, 3), moved.RestoredTo);
        Assert.Contains("back at", moved.ToString());
        var died = new PlayerNoClipEndedEvent(P, NoClipEnd.Death, null);
        Assert.Null(died.RestoredTo);
        Assert.Contains("Death", died.ToString());
        Assert.IsNotAssignableFrom<CancelableEvent>(died);
    }

    [Fact]
    public void TheResultFollowsTheOneErrorRule()
    {
        IKilnResult ok = new NoClipResult(true, true, null, "NoClip on for someone.");
        Assert.True(ok.Ok);
        Assert.Null(ok.Reason);
        IKilnResult refused = new NoClipResult(false, false, "policy", "rules.json does not allow it");
        Assert.False(refused.Ok);
        Assert.Equal("policy", refused.Reason);
        Assert.Equal("rules.noclip", NoClip.Capability);
        Assert.Equal("kiln.command.noclip", NoClip.PermissionNode);
    }
}
