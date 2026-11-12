// SPDX-License-Identifier: MIT
// Where a player looks: the camera the player's client sends with its input every step (PlayerInput.fromClient.camera,
// keen::ecs::ClientCamera), read by kiln.playerstate's aim() (since 0.2.0-dev.27). Docs: docs/kiln/services/player-state.md.
using System.Numerics;

namespace Kiln;

/// <summary>Where a player's camera is and where it looks, as the player's client last sent it. Enshrouded's camera is
/// third person: <see cref="Origin"/> is the camera, behind and above the character (about <see cref="Distance"/>
/// metres), not the character's eyes, so a ray for "what the player looks at" starts at <see cref="Origin"/> and goes
/// along <see cref="Direction"/>. It is the camera the client sent with its input for the step, the one the server's own
/// code uses (a parry's deflection, kiln.rules' steered glider).</summary>
/// <param name="Origin">The camera's position.</param>
/// <param name="Direction">Where the camera looks: a unit vector, <see cref="Rotation"/> applied to (0, 0, 1), the game's
/// forward axis.</param>
/// <param name="Rotation">The camera's orientation.</param>
/// <param name="Distance">The camera's distance setting (the third-person boom), metres.</param>
/// <param name="FieldOfView">The vertical field of view, radians.</param>
/// <param name="Source">Server: read in the server world (any player); ClientPrediction: a joined client's own player.</param>
public sealed record PlayerAim(WorldPosition Origin, Vector3 Direction, Quaternion Rotation, float Distance, float FieldOfView,
    PlayerStateOrigin Source)
{
    /// <summary>The capability this read needs.</summary>
    internal const string Capability = "players.aim";

    /// <summary>The point <paramref name="metres"/> along the view ray from the camera.</summary>
    public WorldPosition At(double metres) =>
        new(Origin.X + Direction.X * metres, Origin.Y + Direction.Y * metres, Origin.Z + Direction.Z * metres);

    /// <summary>The look direction's heading about +Y in degrees, 0..360 (0 = +Z, 90 = +X), counted as
    /// <c>EntityTransform.YawDegrees</c> counts an entity's.</summary>
    public float YawDegrees
    {
        get
        {
            var yaw = MathF.Atan2(Direction.X, Direction.Z) * 180f / MathF.PI;
            return yaw < 0 ? yaw + 360f : yaw;
        }
    }

    /// <summary>The look direction's pitch in degrees: positive up.</summary>
    public float PitchDegrees => MathF.Asin(Math.Clamp(Direction.Y, -1f, 1f)) * 180f / MathF.PI;

    /// <summary>A read from the native struct: null when the orientation is not a rotation.</summary>
    internal static PlayerAim? From(in PlayerStates.Native.AimData a)
    {
        var q = new Quaternion(a.Qx, a.Qy, a.Qz, a.Qw);
        var len = q.Length();
        if (!(len > 0.5f && len < 2f)) return null;
        q = Quaternion.Normalize(q);
        var dir = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, q));
        const double Fixed = 4294967296.0;  // keen::WorldPosition: 32.32 fixed point metres
        var source = a.World == 1 ? PlayerStateOrigin.ClientPrediction : PlayerStateOrigin.Server;
        return new PlayerAim(new WorldPosition(a.X / Fixed, a.Y / Fixed, a.Z / Fixed), dir, q, a.Distance, a.FovY, source);
    }

    private static int _reported;

    /// <summary>Reports <c>players.aim</c> once (Kiln.Players' start, or the first read). Never throws.</summary>
    internal static void ReportCapability()
    {
        if (Interlocked.Exchange(ref _reported, 1) == 1) return;
        try
        {
            var (state, reason, text) = Decide();
            Capabilities.Report(Capability, state, reason, text);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            Backend.Current.Log(0, LogLevel.Warn, $"kiln.players: {Capability} was not reported: {e.Message}");
        }
    }

    private static unsafe (CapabilityState, CapabilityReason, string) Decide()
    {
        var t = PlayerStates.T;
        if (t == null)
        {
            var svc = Capabilities.Get("kiln.playerstate");
            return svc is { State: CapabilityState.Unavailable, Reason: not CapabilityReason.Unknown }
                ? (CapabilityState.Unavailable, svc.Reason, $"kiln.playerstate is unavailable: {svc.ReasonText}")
                : (CapabilityState.Unavailable, CapabilityReason.ServiceMissing, "kiln.playerstate is not loaded");
        }
        if (!PlayerStates.Native.Has(t, &t->Aim))
            return (CapabilityState.Unavailable, CapabilityReason.ServiceOlder, $"kiln.playerstate v{t->Header.Version} has no aim() (update Kiln)");
        PlayerStates.Native.Status s = default;
        s.Size = (uint)sizeof(PlayerStates.Native.Status);
        if (t->GetStatus(&s) != PlayerStates.Native.Ok || s.Reflection == 0)
        {
            var refl = Capabilities.Get("players.reflection");
            return refl is { State: CapabilityState.Unavailable, Reason: not CapabilityReason.Unknown }
                ? (CapabilityState.Unavailable, refl.Reason, $"needs players.reflection: {refl.ReasonText}")
                : (CapabilityState.Unavailable, CapabilityReason.Faulted, "kiln.playerstate read nothing from the game's reflection");
        }
        return s.Aim != 0
            ? (CapabilityState.Available, CapabilityReason.None, "")
            : (CapabilityState.Unavailable, CapabilityReason.SymbolMissing,
                "keen::ecs::PlayerInput.fromClient.camera is not in the game's reflection on this build");
    }
}

public static unsafe partial class PlayerStates
{
    /// <summary>Where a player's camera is and where it looks (<see cref="PlayerAim"/>): read now from the player's
    /// <c>PlayerInput.fromClient.camera</c>. On the server (a dedicated server or a host) any player; on a joined client
    /// only its own player (the prediction world). Null when the entity is no player, its client has not sent a camera
    /// yet, or (on a joined client) it is another player. World's thread.</summary>
    /// <exception cref="KilnUnavailableException"><c>players.aim</c> is off: kiln.playerstate is missing or older, or this
    /// game build has no camera field.</exception>
    /// <exception cref="InvalidOperationException">Not on the world's thread.</exception>
    [TickThreadOnly]
    [RequiresCapability(PlayerAim.Capability)]
    public static PlayerAim? AimOf(EntityId player)
    {
        PlayerAim.ReportCapability();
        var row = Capabilities.Get(PlayerAim.Capability);
        if (row.State != CapabilityState.Available)
            throw new KilnUnavailableException(PlayerAim.Capability, row.ReasonText.Length > 0 ? row.ReasonText : null);
        Game.AssertTickThread();  // the world's thread: the tick thread on a server or host, the main thread on a joined client
        if (player.IsNone) return null;
        var t = T;
        Native.AimData a = default;
        a.Size = (uint)sizeof(Native.AimData);
        var rc = t->Aim(player.Value, &a);
        if (rc == Native.WrongThread) throw new InvalidOperationException("PlayerStates.AimOf must run on the world's thread (the tick thread)");
        return rc == Native.Ok ? PlayerAim.From(in a) : null;
    }
}
