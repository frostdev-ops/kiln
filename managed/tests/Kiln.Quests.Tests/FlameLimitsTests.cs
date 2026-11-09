// SPDX-License-Identifier: MIT
// What the game derives from a flame level (WorldProgress.LimitsAt): the balancing table's per-level lists and the fog rule,
// on a synthetic table laid out like the game's (docs/re/world-status.md section 11). Numbers here are invented.
using Xunit;

namespace Kiln.QuestTests;

[Collection("Flame service")]
public sealed class FlameLimitsTests
{
    private static byte[] Table(int max, byte[]? altars = null, byte[]? water = null, uint fogBase = 100, uint fogPer = 10)
    {
        var w = new BlobWriter(0x1d0);
        w.U32(4, (uint)max);
        w.U32(0x64, fogBase);
        w.U32(0x68, fogPer);
        altars ??= Enumerable.Range(0, max + 1).Select(i => (byte)(i + 1)).ToArray();
        water ??= Enumerable.Range(0, max + 1).Select(i => (byte)(i * 2)).ToArray();
        var a = w.Array(0x1b8, altars.Length, 1);
        for (var i = 0; i < altars.Length; i++) w.U8(a + i, altars[i]);
        var b = w.Array(0x1c0, water.Length, 1);
        for (var i = 0; i < water.Length; i++) w.U8(b + i, water[i]);
        return w.ToArray();
    }

    [Fact]
    public void A_table_gives_the_limits_of_each_level()
    {
        var t = FlameLimitsReader.Parse(Table(20));
        Assert.NotNull(t);
        Assert.Equal(20, t!.Max);
        var l = FlameLimitsReader.Of(t, 7, 11)!;
        Assert.Equal(new FlameLimits(7, 8, 14, 170, 11, false), l);
        Assert.Equal(true, FlameLimitsReader.Of(t, 11, 11)!.FogRemoved);
        Assert.Equal(true, FlameLimitsReader.Of(t, 20, 11)!.FogRemoved);
        Assert.Null(FlameLimitsReader.Of(t, 21, 11));   // above the maximum
        Assert.Null(FlameLimitsReader.Of(t, -1, 11));
        var unknown = FlameLimitsReader.Of(t, 3, null)!;
        Assert.Null(unknown.FogRemovalLevel);
        Assert.Null(unknown.FogRemoved);
    }

    [Fact]
    public void A_table_that_does_not_look_like_the_game_s_reads_as_null()
    {
        Assert.Null(FlameLimitsReader.Parse(Table(20, altars: new byte[5])));                    // a list shorter than the levels
        Assert.Null(FlameLimitsReader.Parse(Table(20, water: new byte[40])));
        Assert.Null(FlameLimitsReader.Parse(Table(0)));                                           // no maximum
        Assert.Null(FlameLimitsReader.Parse(Table(300, altars: new byte[301], water: new byte[301])));
        Assert.Null(FlameLimitsReader.Parse(new byte[16]));                                       // too small
        Assert.Null(FlameLimitsReader.Parse(ReadOnlySpan<byte>.Empty));
        var broken = Table(20);
        BitConverter.GetBytes(0x7fff0000).CopyTo(broken, 0x1b8);                                  // an offset far outside the resource
        Assert.Null(FlameLimitsReader.Parse(broken));
    }

    [Fact]
    public void The_flame_records_print_what_they_know()
    {
        var f = new FlameStatus(3, 1, 3, 0, new EntityId(7)) { Stored = 3, Max = 20, SystemLevel = 3 };
        Assert.Equal("flame level 3, 1 altar(s) (highest level 3), 0 water source(s)", f.ToString());
        Assert.Equal(20, f.Max);
        var limits = new FlameLimits(4, 8, 5, 480, 11, false);
        Assert.Equal("flame level 4: 8 altar(s), 5 water source(s), fog resistance 480, fog removal from level 11 (no)", limits.ToString());
        var ok = new FlameChange(9, true, "ok", 4, 3, 4, 20, false, true, true, 1, "");
        Assert.Equal("flame level 3 -> 4, mirrored after 1 step(s)", ok.ToString());
        Assert.Contains("refused: kiln.knowledge is not loaded", FlameChange.Failed(4, "unavailable", "kiln.knowledge is not loaded").ToString());
        var clamped = new FlameChange(9, true, "ok", 30, 3, 20, 20, true, true, true, 1, "the game's formula limited 30 to 20 (maximum 20)");
        Assert.True(clamped.Clamped);
    }

    [Fact]
    public async Task SetFlameLevel_checks_its_arguments_and_reports_a_server_without_the_service()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = WorldProgress.SetFlameLevel(0); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = WorldProgress.SetFlameLevel(256); });
        var r = await WorldProgress.SetFlameLevel(5);   // no game here: kiln.knowledge is not loaded, so no change is made
        Assert.False(r.Ok);
        Assert.Equal("unavailable", r.Status);
        Assert.Equal(5, r.Requested);
        Assert.NotEmpty(r.Reason);
    }
}
