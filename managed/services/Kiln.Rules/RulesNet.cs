// SPDX-License-Identifier: MIT
// kiln:rules: the server tells each Kiln player's game its own rules for the client-side hooks (the fly-mode gate and
// the glider in the prediction world, the build cursor). Opened by Kiln itself on both sides (not by a mod), not required: a client
// without it joins as before and gets only the server-side rules.
namespace Kiln;

internal static unsafe class RulesNet
{
    private const ushort MsgRules = 0;
    private static readonly object Lock = new();
    private static NetChannel? _channel;
    private static RulesNativePart _local = RulesNativePart.Default;

    // v2 (0.2.0-dev.15): + the Shroud floor and the glider values; v3 (0.2.0-dev.16): + the glider's climb rate
    private readonly record struct RulesMsg(uint Flags, float BuildCost, float BuildRefund, float DigDamage, float ShroudFloor,
        float GlideSink, float GlideThrust, float GlideMaxSpeed, float GlideLift, float GlideCoast, float GlideClimb);

    public static bool Open => _channel is not null;

    /// <summary>The local player's rules as this game applies them (client).</summary>
    public static RulesNativePart Local => _local;

    public static void Start()
    {
        lock (Lock)
        {
            if (_channel is not null || !Network.Available) return;
            var outer = ModContext.CurrentMod;
            ModContext.CurrentMod = null;
            try
            {
                _channel = Network.Channel(Rules.ChannelName, "3", required: false)
                    .Register<RulesMsg>(MsgRules,
                        static (m, w) => w.UInt32(m.Flags).Single(m.BuildCost).Single(m.BuildRefund).Single(m.DigDamage)
                            .Single(m.ShroudFloor).Single(m.GlideSink).Single(m.GlideThrust).Single(m.GlideMaxSpeed).Single(m.GlideLift)
                            .Single(m.GlideCoast).Single(m.GlideClimb),
                        static (ref NetReader r) => new RulesMsg(r.UInt32(), r.Single(), r.Single(), r.Single(), r.Single(), r.Single(),
                            r.Single(), r.Single(), r.Single(), r.Single(), r.Single()),
                        static (m, _) => OnRules(m), NetDirection.ToClient);
                Network.Disconnected += _ => SetLocal(RulesNativePart.Default);
            }
            catch (Exception e)
            {
                RulesRuntime.Log(LogLevel.Warn, $"channel {Rules.ChannelName} unavailable: {e.Message}");
            }
            finally
            {
                ModContext.CurrentMod = outer;
            }
        }
    }

    // Client: the server's word on this player's rules. A hosting game applies its own player's rules directly.
    private static void OnRules(RulesMsg m)
    {
        try
        {
            if (Client.IsHosting) return;
        }
        catch (Exception)
        {
            // no kiln.client
        }
        SetLocal(new RulesNativePart(m.Flags, Clean(m.BuildCost), Clean(m.BuildRefund), Clean(m.DigDamage), Clean(m.ShroudFloor, 0f),
            Clean(m.GlideSink), Clean(m.GlideThrust, 0f), Clean(m.GlideMaxSpeed, 0f), Clean(m.GlideLift, 0f), Clean(m.GlideCoast, 0f),
            Clean(m.GlideClimb, 0f)));
    }

    private static float Clean(float v, float fallback = 1f) => float.IsFinite(v) && v >= 0 ? v : fallback;

    public static void SetLocal(RulesNativePart part)
    {
        if (part == _local) return;
        _local = part;
        var t = RulesNative.T;
        if (t == null) return;
        var p = RulesNative.Of(part);
        t->SetLocal(part.IsDefault ? null : &p);
        RulesRuntime.Log(LogLevel.Info, $"local player's rules: flags 0x{part.Flags:x}, build cost x{part.BuildCost}, glide sink x{part.GlideSink}" +
            ((part.Flags & RulesNative.GlideBoost) != 0
                ? $", boost {part.GlideThrust} m/s² to {part.GlideMaxSpeed} m/s, lift {part.GlideLift}, climb {part.GlideClimb} m/s"
                : ""));
    }

    /// <summary>Server: sends a player's rules to their game; false when it has no Kiln with this channel.</summary>
    public static bool Send(EntityId player, RulesNativePart part)
    {
        var ch = _channel;
        if (ch is null) return false;
        try
        {
            return ch.SendTo(player, new RulesMsg(part.Flags, part.BuildCost, part.BuildRefund, part.DigDamage, part.ShroudFloor,
                part.GlideSink, part.GlideThrust, part.GlideMaxSpeed, part.GlideLift, part.GlideCoast, part.GlideClimb));
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>Started by Kiln at the first service tick on both sides: every Kiln game has the rules channel, and every
/// world (a dedicated server's, or one a game hosts or plays offline) has <c>/creative</c>.</summary>
internal static class RulesStartup
{
    static RulesStartup()
    {
        try
        {
            RulesNet.Start();
        }
        catch (Exception e)
        {
            RulesRuntime.Log(LogLevel.Warn, $"startup: {e.Message}");
        }
        try
        {
            Creative.RegisterCommand();
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException or InvalidOperationException)
        {
            RulesRuntime.Log(LogLevel.Warn, $"/creative is not available: {e.Message}");
        }
    }
}
