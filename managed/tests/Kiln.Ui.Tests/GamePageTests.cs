// SPDX-License-Identifier: MIT
// The Kiln menu's two hosts: which one draws (MenuHosts.Choose), where the game page's content goes at 1920x1080,
// 2560x1440 and 3440x1440 (MenuHosts.Layout / Parts), and GamePageHost against a fake kiln.ui v4 table (claim, read,
// leave; an older kiln.ui). No game.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Kiln.GamePageTests;

internal static unsafe class FakeKilnUi4
{
    public static UiNative.Table4* Table;
    public static UiNative.MenuPage Page;
    public static int Leaves, Drawn, Result;
    public static uint Host = 99;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetPage(UiNative.MenuPage* p)
    {
        if (p->Size < (uint)sizeof(UiNative.MenuPage)) return 1;
        *p = Page;
        p->Size = (uint)sizeof(UiNative.MenuPage);
        return Result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDrawn() => Drawn++;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLeave() => Leaves++;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnHost(uint on) => Host = on;

    public sealed class Backend : IKilnBackend
    {
        public string Version => "test";
        public bool IsTickThread => true;
        public bool HasFeature(string name) => false;
        public void Log(ulong owner, LogLevel level, string text) { }
        public nint Service(string name, uint minVersion) =>
            name == "kiln.ui" && Table != null && ((ServiceHeader*)Table)->Version >= minVersion ? (nint)Table : 0;
    }

    public static void Install(uint version, uint size)
    {
        Free();
        Table = (UiNative.Table4*)NativeMemory.AllocZeroed((nuint)sizeof(UiNative.Table4));
        var h = (ServiceHeader*)Table;
        h->Size = size;
        h->Version = version;
        Table->GetMenuPage = &GetPage;
        Table->MenuPageDrawn = &OnDrawn;
        Table->MenuPageLeave = &OnLeave;
        Table->MenuPageHost = &OnHost;
        Page = default;
        Leaves = Drawn = Result = 0;
        Host = 99;
    }

    public static void Free()
    {
        if (Table != null) NativeMemory.Free(Table);
        Table = null;
    }
}

public sealed class MenuHostRulesTests
{
    private static MenuPageView View(uint state, ulong uiFrame = 100, ulong shown = 100, bool metrics = false) =>
        new(true, state, false, metrics, uiFrame, shown, 0, 0, 0, 0, 0, 0, 0, 80, 50, 80, 50, 0, 0, 0, 0);

    [Fact]
    public void MenuPageMatchesTheNativeLayout() => Assert.Equal(104, Unsafe.SizeOf<UiNative.MenuPage>());

    [Fact]
    public void TheGamePageWinsWhileTheGameShowsIt()
    {
        Assert.Equal(MenuHost.GamePage, MenuHosts.Choose(false, View(MenuPageView.Shown)));
        Assert.Equal(MenuHost.GamePage, MenuHosts.Choose(true, View(MenuPageView.Shown)));
        Assert.Equal(MenuHost.GamePage, MenuHosts.Choose(false, View(MenuPageView.Shown, uiFrame: 102, shown: 100)));
    }

    [Fact]
    public void NothingWhileThePageOpensOrTheGameStoppedShowingIt()
    {
        // the frame of the click: the game's main menu is still on screen
        Assert.Equal(MenuHost.None, MenuHosts.Choose(false, View(MenuPageView.Opening)));
        // the game left the page by itself (an invite): no request to the game, the menu just stops drawing
        Assert.Equal(MenuHost.None, MenuHosts.Choose(false, View(MenuPageView.Shown, uiFrame: 103, shown: 100)));
        Assert.Equal(MenuHost.None, MenuHosts.Choose(true, View(MenuPageView.Opening)));
    }

    [Fact]
    public void TheStandAloneHostOtherwise()
    {
        Assert.Equal(MenuHost.Standalone, MenuHosts.Choose(true, default));      // an older kiln.ui, the feature off
        Assert.Equal(MenuHost.Standalone, MenuHosts.Choose(true, View(MenuPageView.Hidden)));
        Assert.Equal(MenuHost.None, MenuHosts.Choose(false, View(MenuPageView.Hidden)));
        Assert.Equal(MenuHost.None, MenuHosts.Choose(false, default));
    }

    // what kiln.ui publishes for the game's canvas at a window size (the UI system's update, 0xa4fb40)
    private static MenuPageView Game(float w, float h)
    {
        var ppu = Math.Min(w / 1920f, h / 1080f);
        float cw = w / ppu, ch = h / ppu;
        return new(true, MenuPageView.Shown, false, true, 10, 10, cw, ch, 0, 0, w, h, ppu, 80, 50, 80, 50,
            80 * ppu, 50 * ppu, (cw - 80) * ppu, (ch - 50) * ppu);
    }

    [Theory]
    [InlineData(1920, 1080, 80, 50, 1840, 1030, 1)]
    [InlineData(2560, 1440, 106.67f, 66.67f, 2453.33f, 1373.33f, 1.3333f)]
    [InlineData(3440, 1440, 106.67f, 66.67f, 3333.33f, 1373.33f, 1.3333f)]
    [InlineData(1920, 1200, 80, 50, 1840, 1150, 1)]
    public void TheRectangleIsTheGamesPaddedCanvas(float w, float h, float x0, float y0, float x1, float y1, float scale)
    {
        var (r, s) = MenuHosts.Layout(Game(w, h), w, h);
        Assert.Equal(x0, r.X, 0.05f);
        Assert.Equal(y0, r.Y, 0.05f);
        Assert.Equal(x1, r.Right, 0.05f);
        Assert.Equal(y1, r.Bottom, 0.05f);
        Assert.Equal(scale, s, 0.001f);
        // without the game's numbers the same rectangle comes from the display size
        var (r2, s2) = MenuHosts.Layout(View(MenuPageView.Shown), w, h);
        Assert.Equal(r.X, r2.X, 0.05f);
        Assert.Equal(r.Bottom, r2.Bottom, 0.05f);
        Assert.Equal(s, s2, 0.001f);
    }

    [Fact]
    public void AnOverlayOfAnotherSizeStretchesTheGamesNumbers()
    {
        var (r, s) = MenuHosts.Layout(Game(1920, 1080), 3840, 2160);
        Assert.Equal(160, r.X, 0.05f);
        Assert.Equal(2060, r.Bottom, 0.05f);
        Assert.Equal(2, s, 0.001f);
    }

    [Fact]
    public void TitleBodyAndHintsFillTheRectangle()
    {
        var r = new PageRect(80, 50, 1760, 980);
        var (head, body, foot) = MenuHosts.Parts(r, 1);
        Assert.Equal(r.Y, head.Y);
        Assert.Equal(46, head.Height);
        Assert.True(body.Y > head.Bottom && body.Bottom < foot.Y);
        Assert.Equal(r.Bottom, foot.Bottom, 0.01f);
        Assert.Equal(r.X, body.X);
        Assert.Equal(r.Width, body.Width);
    }
}

[Collection("bus")]
public sealed unsafe class GamePageHostTests : IDisposable
{
    private readonly IKilnBackend _previous;

    public GamePageHostTests()
    {
        _previous = Backend.Current;
        Backend.Current = new FakeKilnUi4.Backend();
        UiNative.ResetForTests();
        GamePageHost.ResetForTests();
    }

    public void Dispose()
    {
        Backend.Current = _previous;
        UiNative.ResetForTests();
        FakeKilnUi4.Free();
    }

    [Fact]
    public void ClaimReadAndLeaveGoThroughKilnUi()
    {
        FakeKilnUi4.Install(4, (uint)sizeof(UiNative.Table4));
        Assert.True(GamePageHost.Claim());
        Assert.Equal(1u, FakeKilnUi4.Host);
        FakeKilnUi4.Page.Available = 1;
        FakeKilnUi4.Page.State = 2;
        FakeKilnUi4.Page.UiFrame = 41;
        FakeKilnUi4.Page.ShownFrame = 41;
        FakeKilnUi4.Page.ErrorPopup = 1;
        FakeKilnUi4.Page.X0 = 80;
        var p = GamePageHost.Read();
        Assert.NotNull(p);
        Assert.True(p.Value.Available && p.Value.IsShown && p.Value.ErrorPopup);
        Assert.Equal(80, p.Value.X0);
        Assert.True(GamePageHost.Supported);
        GamePageHost.Leave();
        Assert.Equal(1, FakeKilnUi4.Leaves);
    }

    [Fact]
    public void TheFeatureOffMeansNoGamePage()
    {
        FakeKilnUi4.Install(4, (uint)sizeof(UiNative.Table4));
        FakeKilnUi4.Result = 3;  // KILN_ERR_UNAVAILABLE
        Assert.Null(GamePageHost.Read());
        Assert.False(GamePageHost.Supported);
    }

    [Fact]
    public void AnOlderKilnUiHasNoGamePage()
    {
        FakeKilnUi4.Install(3, (uint)sizeof(UiNative.Table3));
        Assert.False(GamePageHost.Claim());
        Assert.Null(GamePageHost.Read());
        GamePageHost.Leave();  // no slot: nothing happens
        Assert.Equal(0, FakeKilnUi4.Leaves);
        Assert.Equal(99u, FakeKilnUi4.Host);
    }
}
