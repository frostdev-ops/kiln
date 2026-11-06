// SPDX-License-Identifier: MIT
// Playing full-screen effects. A client plays them through kiln.screen; a server sends them to the Kiln clients that should see them over the Kiln
// channel kiln:screen (ScreenRelay.cs). docs/kiln/services/screen.md.
using System.Text;

namespace Kiln;

/// <summary>A playing screen effect: from <see cref="ScreenEffects"/>, for <see cref="ScreenEffects.Stop"/>.</summary>
public readonly record struct ScreenHandle(uint Value)
{
    public static readonly ScreenHandle None = default;
    public bool IsNone => Value == 0;
    public override string ToString() => IsNone ? "none" : (Value & ScreenRelay.RemoteBit) != 0 ? $"relayed #{Value & ~ScreenRelay.RemoteBit}" : $"#{Value}";
}

/// <summary>What kiln.screen and the relay are doing.</summary>
/// <param name="Available">A play would show: on a client, kiln.screen is on; on a server, the relay is open.</param>
/// <param name="Native">kiln.screen is in this process (a client).</param>
/// <param name="Enabled">It is not switched off, by kiln/config/screen.json or by a failure.</param>
/// <param name="Ready">The pass is built for the current swapchain: the next frame with an effect draws it.</param>
/// <param name="Transfer">How the swapchain's values are read: "linear", "sRGB" or "PQ".</param>
/// <param name="Skipped">Frames left alone because the GPU was still using that image.</param>
/// <param name="Reason">Why it is off, "" when it isn't.</param>
public sealed record ScreenStatus(bool Available, bool Native, bool Enabled, bool Ready, int Playing, string Transfer, int Width, int Height, uint Format,
    long Plays, long Refused, long Frames, long Skipped, double PipelineMs, string Reason, bool Relay, long Sent, long Received);

/// <summary>
/// Full-screen effects: colour grading, a tint, a vignette, fades and flashes (<see cref="ScreenEffect"/>).
/// <code>
/// var h = ScreenEffects.Play(new ScreenEffect { Fill = "#000000", FadeIn = 1.5f });   // fade to black, until stopped
/// ScreenEffects.Stop(h);
/// </code>
/// Client code plays on this client, from any thread. Server code (a dedicated server, or the hosting client's world) sends the effect to Kiln
/// clients: <see cref="Play"/> to all of them, <see cref="PlayFor"/> to one player, <see cref="PlayNear"/> to those near a place. The message carries
/// the whole effect, so a client needs Kiln and not the mod; a vanilla client sees nothing. A dedicated server draws nothing itself.
/// The effect is drawn over the frame the game presents, so it covers the game's UI too.
/// </summary>
public static unsafe class ScreenEffects
{
    private static readonly object Lock = new();
    private static readonly HashSet<ModContext> Watched = new(ReferenceEqualityComparer.Instance);
    private static int _warnedOff;

    /// <summary>A play here would show (client), or the relay is open (server).</summary>
    public static bool Available => VfxCore.ServerSide ? ScreenRelay.Open : LocalAvailable;

    internal static bool LocalAvailable
    {
        get
        {
            var t = ScreenNative.T;
            if (t == null) return false;
            var s = new ScreenNative.Status { Size = (uint)sizeof(ScreenNative.Status) };
            return t->GetStatus(&s) == 0 && s.Enabled != 0;
        }
    }

    /// <summary>Client: plays here. Server: on every Kiln client, and on the host's own screen.</summary>
    /// <exception cref="ArgumentException">A field is out of its range.</exception>
    public static ScreenHandle Play(ScreenEffect effect)
    {
        Check(effect);
        return VfxCore.ServerSide ? ScreenRelay.Play(effect, ScreenRelay.ToAll, EntityId.None, default, 0) : PlayLocal(effect, Owner());
    }

    /// <summary>Server: on one player's Kiln client (the host's own screen when that player is the host). Client: plays only when
    /// <paramref name="player"/> is the local player.</summary>
    public static ScreenHandle PlayFor(EntityId player, ScreenEffect effect)
    {
        Check(effect);
        if (VfxCore.ServerSide) return ScreenRelay.Play(effect, ScreenRelay.ToPlayer, player, default, 0);
        return player == Client.LocalPlayer ? PlayLocal(effect, Owner()) : ScreenHandle.None;
    }

    /// <summary>Server: on the Kiln clients whose player is within <paramref name="radius"/> metres of <paramref name="at"/>, when it starts. Tick
    /// thread. Client: plays here when the local player is that near, or its position isn't known.</summary>
    public static ScreenHandle PlayNear(WorldPosition at, float radius, ScreenEffect effect)
    {
        Check(effect);
        if (!(radius > 0)) throw new ArgumentOutOfRangeException(nameof(radius), radius, "a radius is more than 0 metres");
        if (VfxCore.ServerSide) return ScreenRelay.Play(effect, ScreenRelay.ToNear, EntityId.None, at, radius);
        var me = Client.HasLocalPlayer ? World.Position(Client.LocalPlayer) : null;
        return me is not { } p || Distance(p, at) <= radius ? PlayLocal(effect, Owner()) : ScreenHandle.None;
    }

    /// <summary>Stops it, by its fade out unless <paramref name="immediate"/>. Any thread on a client; the tick thread for a relayed one.</summary>
    public static void Stop(ScreenHandle h, bool immediate = false)
    {
        if (h.IsNone) return;
        if ((h.Value & ScreenRelay.RemoteBit) != 0)
        {
            ScreenRelay.Stop(h.Value, immediate);
            return;
        }
        var t = ScreenNative.T;
        if (t != null) t->Stop(h.Value, immediate ? 1 : 0);
    }

    /// <summary>Changes how much of it shows, 0 to 1, while it plays: for what follows a value, such as health.</summary>
    public static void SetStrength(ScreenHandle h, float strength)
    {
        if (h.IsNone) return;
        if (!float.IsFinite(strength) || strength < 0 || strength > 1) throw new ArgumentOutOfRangeException(nameof(strength), strength, "0 to 1");
        if ((h.Value & ScreenRelay.RemoteBit) != 0)
        {
            ScreenRelay.SetStrength(h.Value, strength);
            return;
        }
        var t = ScreenNative.T;
        if (t != null) t->SetStrength(h.Value, strength);
    }

    /// <summary>Client: it still shows, fading out included. Server: it has not been stopped and its length has not run out.</summary>
    public static bool IsPlaying(ScreenHandle h)
    {
        if (h.IsNone) return false;
        if ((h.Value & ScreenRelay.RemoteBit) != 0) return ScreenRelay.IsPlaying(h.Value);
        var t = ScreenNative.T;
        return t != null && t->IsPlaying(h.Value) == 1;
    }

    public static ScreenStatus Status()
    {
        VfxStartup.Ensure();
        var t = ScreenNative.T;
        if (t == null)
            return new ScreenStatus(Available, false, false, false, 0, "", 0, 0, 0, 0, 0, 0, 0, 0, Client.IsDedicatedServer ? "no screen on a dedicated server" : "kiln.screen is not here",
                ScreenRelay.Open, ScreenRelay.Sent, ScreenRelay.ReceivedCount);
        var s = new ScreenNative.Status { Size = (uint)sizeof(ScreenNative.Status) };
        t->GetStatus(&s);
        var n = 0;
        while (n < 128 && s.Reason[n] != 0) n++;
        var reason = Encoding.UTF8.GetString(s.Reason, n);
        return new ScreenStatus(Available, true, s.Enabled != 0, s.Ready != 0, (int)s.Playing, s.Transfer switch { 0 => "linear", 1 => "sRGB", _ => "PQ" },
            (int)s.Width, (int)s.Height, s.Format, (long)s.Plays, (long)s.Refused, (long)s.Frames, (long)s.Skipped, s.PipelineMs, reason, ScreenRelay.Open,
            ScreenRelay.Sent, ScreenRelay.ReceivedCount);
    }

    private static void Check(ScreenEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        var problems = effect.Problems();
        if (problems.Count > 0) throw new ArgumentException($"screen effect: {string.Join("; ", problems.Select(p => $"{p.Field}: {p.Problem}"))}", nameof(effect));
    }

    private static double Distance(WorldPosition a, WorldPosition b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    internal static ScreenHandle PlayLocal(ScreenEffect effect, ulong owner)
    {
        var t = ScreenNative.T;
        if (t == null || !LocalAvailable)
        {
            if (Interlocked.Exchange(ref _warnedOff, 1) == 0)
                ScreenLog.Warn(t == null ? "kiln.screen is not here (a dedicated server, kiln-screen.dll missing, or off by kiln/config/screen.json): nothing shows"
                                         : "kiln.screen is off: nothing shows (its log line says why)");
            return ScreenHandle.None;
        }
        var n = ScreenNative.From(effect);
        return new ScreenHandle(t->Play(owner, &n));
    }

    /// <summary>The calling mod's owner, watched so that its effects stop when it unloads.</summary>
    private static ulong Owner()
    {
        var mod = ModContext.CurrentMod;
        if (mod is null) return 0;
        lock (Lock)
        {
            if (Watched.Add(mod))
                mod.Retiring += () =>
                {
                    var t = ScreenNative.T;
                    if (t != null && t->StopOwner(mod.Owner, 0) is var n and > 0) ScreenLog.Info($"{mod.Info.Id} unloaded: {n} screen effect(s) stopped");
                    ScreenRelay.Retire(mod);
                    lock (Lock) Watched.Remove(mod);
                };
        }
        return mod.Owner;
    }

    /// <summary>For the relay: the owner of a play from server code, watched the same way.</summary>
    internal static ModContext? Caller()
    {
        Owner();
        return ModContext.CurrentMod;
    }
}

/// <summary>Test seams, for mods/tests/screen-test. Not for mods.</summary>
public static class ScreenTestSeams
{
    /// <summary>The mirror's sizes, which must equal the C header's: 100, 192, 56.</summary>
    public static (int Effect, int Status, int Table) NativeSizes => ScreenNative.Sizes;

    /// <summary>A relayed play as the client gets it, encoded and decoded in this process: the native effect's fields in order.</summary>
    public static float[] RoundTrip(ScreenEffect effect) => ScreenRelay.RoundTrip(effect);

    /// <summary>The native effect's fields in order, as kiln.screen gets them.</summary>
    public static float[] Native(ScreenEffect effect) => ScreenRelay.Fields(ScreenNative.From(effect));
}
