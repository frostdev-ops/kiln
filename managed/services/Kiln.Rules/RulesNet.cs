// SPDX-License-Identifier: MIT
// kiln:rules: the server tells each Kiln player's game its own rules for the client-side hooks (the fly-mode gate in
// the prediction world, the build cursor). Opened by Kiln itself on both sides (not by a mod), not required: a client
// without it joins as before and gets only the server-side rules.
namespace Kiln;

internal static unsafe class RulesNet
{
    private const ushort MsgRules = 0;
    private static readonly object Lock = new();
    private static NetChannel? _channel;
    private static RulesNativePart _local = RulesNativePart.Default;

    private readonly record struct RulesMsg(uint Flags, float BuildCost, float BuildRefund, float DigDamage);

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
                _channel = Network.Channel(Rules.ChannelName, "1", required: false)
                    .Register<RulesMsg>(MsgRules,
                        static (m, w) => w.UInt32(m.Flags).Single(m.BuildCost).Single(m.BuildRefund).Single(m.DigDamage),
                        static (ref NetReader r) => new RulesMsg(r.UInt32(), r.Single(), r.Single(), r.Single()),
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
        SetLocal(new RulesNativePart(m.Flags, Clean(m.BuildCost), Clean(m.BuildRefund), Clean(m.DigDamage)));
    }

    private static float Clean(float v) => float.IsFinite(v) && v >= 0 ? v : 1f;

    public static void SetLocal(RulesNativePart part)
    {
        if (part == _local) return;
        _local = part;
        var t = RulesNative.T;
        if (t == null) return;
        var p = RulesNative.Of(part);
        t->SetLocal(part.IsDefault ? null : &p);
        RulesRuntime.Log(LogLevel.Info, $"local player's rules: flags 0x{part.Flags:x}, build cost x{part.BuildCost}");
    }

    /// <summary>Server: sends a player's rules to their game; false when it has no Kiln with this channel.</summary>
    public static bool Send(EntityId player, RulesNativePart part)
    {
        var ch = _channel;
        if (ch is null) return false;
        try
        {
            return ch.SendTo(player, new RulesMsg(part.Flags, part.BuildCost, part.BuildRefund, part.DigDamage));
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>Started by Kiln at the first service tick on both sides, so that every Kiln game has the rules channel.</summary>
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
    }
}
