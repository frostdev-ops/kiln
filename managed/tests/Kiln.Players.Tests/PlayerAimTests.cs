// SPDX-License-Identifier: MIT
using System.Numerics;
using Xunit;

namespace Kiln.PlayerTests;

/// <summary>Player.Aim without a game: the conversion of kiln.playerstate's camera read (32.32 fixed point position, a
/// quaternion turning the game's forward +Z) and the error rule. The read itself runs in a lab (mods/tests/playerstate-test).</summary>
public sealed class PlayerAimTests
{
    private static PlayerStates.Native.AimData Raw(WorldPosition at, Quaternion q, uint world = 0)
    {
        const double Fixed = 4294967296.0;
        return new PlayerStates.Native.AimData
        {
            Size = 64, World = world,
            X = (long)Math.Round(at.X * Fixed), Y = (long)Math.Round(at.Y * Fixed), Z = (long)Math.Round(at.Z * Fixed),
            Qx = q.X, Qy = q.Y, Qz = q.Z, Qw = q.W, Distance = 3.5f, FovY = 1.1f,
        };
    }

    [Fact]
    public void TheCameraBecomesAnOriginAndADirection()
    {
        var at = new WorldPosition(3841.75, 837.8125, -12.5);
        var q = Quaternion.CreateFromYawPitchRoll(MathF.PI / 2, 20f * MathF.PI / 180f, 0);
        var a = PlayerAim.From(Raw(at, q))!;
        Assert.Equal(at, a.Origin);
        Assert.True((a.Direction - new Vector3(MathF.Cos(20f * MathF.PI / 180f), -MathF.Sin(20f * MathF.PI / 180f), 0)).Length() < 1e-5);
        Assert.Equal(90f, a.YawDegrees, 2);
        Assert.Equal(-20f, a.PitchDegrees, 2);
        Assert.Equal(3.5f, a.Distance);
        Assert.Equal(1.1f, a.FieldOfView);
        Assert.Equal(PlayerStateOrigin.Server, a.Source);
        var ten = a.At(10);
        Assert.True(Math.Abs(ten.DistanceTo(at) - 10) < 1e-4);
        Assert.Equal(PlayerStateOrigin.ClientPrediction, PlayerAim.From(Raw(at, q, world: 1))!.Source);
    }

    [Fact]
    public void AnUnrotatedCameraLooksAlongPlusZ()
    {
        var a = PlayerAim.From(Raw(new WorldPosition(1, 2, 3), Quaternion.Identity))!;
        Assert.Equal(Vector3.UnitZ, a.Direction);
        Assert.Equal(0f, a.YawDegrees);
        Assert.Equal(0f, a.PitchDegrees);
        var west = PlayerAim.From(Raw(new WorldPosition(1, 2, 3), Quaternion.CreateFromYawPitchRoll(-MathF.PI / 2, 0, 0)))!;
        Assert.Equal(270f, west.YawDegrees, 2);  // 0..360, as EntityTransform.YawDegrees counts
    }

    [Fact]
    public void ANonRotationIsNoAim()
    {
        Assert.Null(PlayerAim.From(Raw(new WorldPosition(1, 2, 3), new Quaternion(0, 0, 0, 0))));
        Assert.Null(PlayerAim.From(Raw(new WorldPosition(1, 2, 3), new Quaternion(5, 5, 5, 5))));
        // a slightly unnormalised quaternion (float noise from the client) is normalised
        var a = PlayerAim.From(Raw(new WorldPosition(1, 2, 3), new Quaternion(0, 0, 0, 1.01f)))!;
        Assert.Equal(1f, a.Rotation.Length(), 5);
    }

    [Fact]
    public void WithoutKilnPlayerstateAimOfThrowsKilnUnavailable()
    {
        var e = Assert.Throws<KilnUnavailableException>(() => PlayerStates.AimOf(new EntityId(1)));
        Assert.Equal("players.aim", e.Capability);
        Assert.False(Capabilities.Is("players.aim"));
    }
}
