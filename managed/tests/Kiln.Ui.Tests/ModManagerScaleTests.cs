// SPDX-License-Identifier: MIT
// The stand-alone Mods page's scale (ModManagerHost.PageScale) and the three columns it must leave room for
// (ModManagerPage.Columns). GitHub PR 27: at 300 % Windows scaling on a 3840x2160 display the UI scale (3) times the
// display's growth (2) left the columns 544 units of the 886 they need; the mods column's width went below zero and
// the details covered its search box. No game.
using Xunit;

namespace Kiln.ModManagerScaleTests;

public sealed class PageScaleTests
{
    /// <summary>The content between the stand-alone page's margins, as ModManagerHost.Draw lays it out.</summary>
    private static float Content(float w, float s) => ModManagerChrome.ContentWidth(w - 2 * ModManagerHost.Margin(w, s), s);

    [Fact]
    public void ThreeSmallestColumnsAndTwoGapsAreThePagesMinimum() =>
        Assert.Equal(230 + 300 + 300 + 2 * 28, ModManagerPage.MinContentUnits);

    [Fact]
    public void TheProductOfUiScaleAndDisplayGrowthLeftNoRoomForTheModsColumn()
    {
        // what the page used before the bound: 3 x 2 on a 4K display at 300 %
        const float w = 3840, s = 6;
        var (_, mid, _) = ModManagerPage.Columns(Content(w, s), s);
        Assert.Equal(-252, mid, 0.5f);
    }

    [Theory]
    // display, UI scale -> the scale the page had before the bound, kept
    [InlineData(1280, 720, 1f, 0.7f)]
    [InlineData(1920, 1080, 1f, 1f)]
    [InlineData(1920, 1080, 1.5f, 1.5f)]
    [InlineData(1920, 1200, 1f, 1f)]
    [InlineData(2560, 1440, 1f, 4f / 3)]
    [InlineData(2560, 1440, 1.5f, 2f)]
    [InlineData(3440, 1440, 1f, 4f / 3)]
    [InlineData(3440, 1440, 2f, 8f / 3)]
    [InlineData(3840, 2160, 1.5f, 3f)]
    [InlineData(640, 480, 1f, 0.7f)]  // a small window: not below the smallest scale the page ever had
    public void ADisplayWithRoomForTheColumnsKeepsItsScale(float w, float h, float ui, float expected)
    {
        Assert.Equal(expected, ModManagerHost.PageScale(w, h, ui), 0.001f);
    }

    [Theory]
    [InlineData(3840, 2160, 3f, 3.857f)]   // PR 27's display: was 6
    [InlineData(3840, 2160, 2.5f, 3.857f)] // was 5
    [InlineData(3840, 2160, 2f, 3.857f)]   // was 4: the mods column was 268 units, under its smallest
    [InlineData(2560, 1440, 3f, 2.572f)]   // was 4: the same 544 units as 4K at 300 %
    [InlineData(1920, 1080, 3f, 1.929f)]   // was 3
    public void ADisplayWithoutRoomGetsTheLargestScaleThatFits(float w, float h, float ui, float expected)
    {
        var s = ModManagerHost.PageScale(w, h, ui);
        Assert.Equal(expected, s, 0.001f);
        Assert.Equal(ModManagerPage.MinContentUnits, Content(w, s) / s, 0.01f);
    }

    public static TheoryData<float, float> Displays => new()
    {
        { 1280, 720 }, { 1366, 768 }, { 1600, 900 }, { 1920, 1080 }, { 1920, 1200 }, { 2560, 1080 }, { 2560, 1440 },
        { 2560, 1600 }, { 2880, 1800 }, { 3440, 1440 }, { 3840, 1600 }, { 3840, 2160 }, { 5120, 1440 }, { 5120, 2880 },
        { 7680, 4320 },
    };

    [Theory]
    [MemberData(nameof(Displays))]
    public void AtEveryUiScaleTheThreeColumnsHaveTheirSmallestWidth(float w, float h)
    {
        // kiln.ui reports a UI scale from 1 to 3 (the window's DPI, at least the display's height / 1440)
        for (var ui = 1f; ui <= 3.001f; ui += 0.125f)
        {
            var s = ModManagerHost.PageScale(w, h, ui);
            Assert.InRange(s, 0.7f, ui * 3);
            var content = Content(w, s);
            var (left, mid, right) = ModManagerPage.Columns(content, s);
            var at = $"{w}x{h} at UI scale {ui}: page scale {s}, columns {left / s:0} | {mid / s:0} | {right / s:0}";
            Assert.True(left >= ModManagerPage.MinEngines * s - 0.01f, at);
            Assert.True(mid >= ModManagerPage.MinMods * s - 0.01f, at);
            Assert.True(right >= ModManagerPage.MinDetails * s - 0.01f, at);
            Assert.Equal(content, left + mid + right + 2 * ModManagerPage.ColumnGap * s, 0.01f);
        }
    }

    [Fact]
    public void AUiScaleKilnUiDidNotReportCountsAsOne()
    {
        Assert.Equal(ModManagerHost.PageScale(1920, 1080, 1), ModManagerHost.PageScale(1920, 1080, 0));
    }
}
