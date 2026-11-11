// SPDX-License-Identifier: MIT
// The Kiln menu inside the game's own main menu (kiln.ui v4, feature ui.menu_page; docs/kiln/ui.md "Two hosts").
// The main menu's MODS entry makes the game go to a page of its own (its Credits slot, left empty by kiln.ui): the game
// glides its camera to the close-up shot, hides its main menu and draws its vignette. GamePageHost draws the mod
// manager into the rectangle the game's pages use (its canvas, viewport and padding, published by kiln.ui each UI
// frame), with the game's header and hint-bar look and no dimming of its own: each column sits on a translucent tile
// (menu.tileBackgroundColor) and soft bands darken the top and bottom edges, so it reads on any shot. Leaving (Escape, gamepad B, the Back
// hint, the menu key) asks kiln.ui, which makes the game go back itself. When the game leaves the page by itself (an
// invite) the page is simply no longer shown. Render thread.
namespace Kiln;

/// <summary>KilnUiMenuPage as plain values.</summary>
internal readonly record struct MenuPageView(bool Available, uint State, bool ErrorPopup, bool Metrics, ulong UiFrame, ulong ShownFrame,
    float CanvasWidth, float CanvasHeight, float ViewportX, float ViewportY, float ViewportWidth, float ViewportHeight,
    float PixelsPerUnit, float PadLeft, float PadTop, float PadRight, float PadBottom, float X0, float Y0, float X1, float Y1)
{
    public const uint Hidden = 0, Opening = 1, Shown = 2;

    /// <summary>The game shows the page now: shown, and the game drew it in one of the last two UI frames.</summary>
    public bool IsShown => State == Shown && UiFrame <= ShownFrame + 2;

    /// <summary>Opening or shown: the Kiln menu belongs to the game page.</summary>
    public bool IsOpen => State != Hidden;

    internal static MenuPageView From(in UiNative.MenuPage p) => new(p.Available != 0, p.State, p.ErrorPopup != 0, p.Metrics != 0,
        p.UiFrame, p.ShownFrame, p.CanvasWidth, p.CanvasHeight, p.ViewportX, p.ViewportY, p.ViewportWidth, p.ViewportHeight,
        p.PixelsPerUnit, p.PadLeft, p.PadTop, p.PadRight, p.PadBottom, p.X0, p.Y0, p.X1, p.Y1);
}

/// <summary>Which host draws the Kiln menu this frame.</summary>
internal enum MenuHost
{
    None,
    /// <summary><see cref="ModManagerHost"/>: the whole display, over the game.</summary>
    Standalone,
    /// <summary><see cref="GamePageHost"/>: inside the game's main menu.</summary>
    GamePage,
}

/// <summary>The rules without drawing (tests use them).</summary>
internal static class MenuHosts
{
    /// <summary>The game page while the game shows it; nothing while it opens or closes (the game's main menu is still
    /// or again on screen); else the stand-alone page when the Kiln menu is open.</summary>
    public static MenuHost Choose(bool menuOpen, in MenuPageView page) =>
        page.IsShown ? MenuHost.GamePage : page.IsOpen ? MenuHost.None : menuOpen ? MenuHost.Standalone : MenuHost.None;

    /// <summary>The page's content rectangle and scale on a display of <paramref name="width"/> x <paramref name="height"/>
    /// overlay pixels. The game's numbers are in its viewport's pixels: when the overlay's display differs (it should
    /// not), they are stretched to it. Without the game's metrics the rectangle is what the game would use on this display
    /// (1920x1080 UI units, grown along the longer side, padding 80 / 50).</summary>
    public static (PageRect Rect, float Scale) Layout(in MenuPageView p, float width, float height)
    {
        if (!p.Metrics || p.PixelsPerUnit <= 0 || p.X1 <= p.X0 || p.Y1 <= p.Y0)
        {
            var ppu = Math.Min(width / 1920f, height / 1080f);
            if (ppu <= 0) ppu = 1;
            float padX = p.PadLeft > 0 ? p.PadLeft : 80, padY = p.PadTop > 0 ? p.PadTop : 50;
            var r = new PageRect(padX * ppu, padY * ppu, width - 2 * padX * ppu, height - 2 * padY * ppu);
            return (r, Math.Clamp(ppu, 0.5f, 4f));
        }
        var extW = 2 * p.ViewportX + p.ViewportWidth;
        var extH = 2 * p.ViewportY + p.ViewportHeight;
        var fx = extW > 1 ? Math.Clamp(width / extW, 0.25f, 4f) : 1;
        var fy = extH > 1 ? Math.Clamp(height / extH, 0.25f, 4f) : 1;
        var rect = new PageRect(p.X0 * fx, p.Y0 * fy, (p.X1 - p.X0) * fx, (p.Y1 - p.Y0) * fy);
        return (rect, Math.Clamp(p.PixelsPerUnit * Math.Min(fx, fy), 0.5f, 4f));
    }

    /// <summary>The page's parts inside its rectangle at scale <paramref name="s"/>: the header row (title), the body
    /// (the mod manager page) and the hint bar at the bottom.</summary>
    public static (PageRect Header, PageRect Body, PageRect Footer) Parts(PageRect r, float s)
    {
        r = r with { Width = ModManagerChrome.ContentWidth(r.Width, s) };  // ultrawide: left-aligned, the rest stays empty
        var titleH = 46 * s;
        var footerH = 48 * s;
        var header = new PageRect(r.X, r.Y, r.Width, titleH);
        var bodyTop = r.Y + titleH + 30 * s;
        var footer = new PageRect(r.X, r.Bottom - footerH, r.Width, footerH);
        var body = new PageRect(r.X, bodyTop, r.Width, Math.Max(0, footer.Y - 14 * s - bodyTop));
        return (header, body, footer);
    }
}

/// <summary>The host of <see cref="ModManagerPage"/> inside the game's main menu.</summary>
internal static unsafe class GamePageHost
{
    private static bool _wasShown;

    /// <summary>This kiln.ui has the game page (v4) and the feature is on for this build.</summary>
    public static bool Supported => UiNative.T4 != null && Read() is { } p && p.Available;

    /// <summary>The page state now, or null on a kiln.ui without it (older than v4, or the feature off).</summary>
    public static MenuPageView? Read()
    {
        var t = UiNative.T4;
        if (t == null) return null;
        UiNative.MenuPage p = default;
        p.Size = (uint)sizeof(UiNative.MenuPage);
        return t->GetMenuPage(&p) == UiNative.Ok ? MenuPageView.From(p) : null;
    }

    /// <summary>Tells kiln.ui that this host draws the page (the MODS entry then opens it). Once, at start.</summary>
    public static bool Claim()
    {
        var t = UiNative.T4;
        if (t == null) return false;
        t->MenuPageHost(1);
        return true;
    }

    /// <summary>Back to the game's main menu (the game moves its camera back and plays its back sound).</summary>
    public static void Leave()
    {
        var t = UiNative.T4;
        if (t != null) t->MenuPageLeave();
    }

    public static void Draw(UiDraw ui, in MenuPageView page)
    {
        var t = UiNative.T4;
        if (t == null) return;
        t->MenuPageDrawn();  // the watchdog: a host is here (also while the content steps aside for a popup)
        var first = !_wasShown;
        _wasShown = true;
        if (page.ErrorPopup) return;  // the game's error popup is on top: let the player use it
        var (rect, s) = MenuHosts.Layout(page, ui.Width, ui.Height);
        var (header, body, footer) = MenuHosts.Parts(rect, s);
        var w = ui.Width;
        var h = ui.Height;
        ui.PushStyleRaw(UiStyle.Game);
        try
        {
            // no background: the game draws its 3D scene and vignette behind the page, as for its own pages
            ui.NextWindowPos(0, 0, 1);
            ui.NextWindowSize(w, h, 1);
            if (first) ui.FocusNextWindow();  // keyboard and gamepad go to the page when it appears
            using var pad = ui.PushVar(UiStyleVar.WindowPadding, 0, 0);
            using var border = ui.PushVar(UiStyleVar.WindowBorderSize, 0);
            // no dimming either, but soft dark bands behind the title and tabs and behind the hints (the game draws a
            // vignette; the shot behind may still be bright)
            // full strength through the tab row (the first 46 units of the body) and the hint bar, fading after
            ModManagerChrome.Bands(ui, (body.Y + 52 * s) / 0.65f, h - (h - footer.Y + 8 * s) / 0.65f, 170);
            if (ui.BeginWindowNoClose($"Kiln {Game.KilnVersion}###kiln.menu.page", ModManagerChrome.PageFlags(first)))
            {
                ModManagerHost.Page.Backed = true;
                ModManagerChrome.Title(ui, ChromeLook.GamePage, header.X, header.Y, header.Right, header.Height, s);
                ModManagerHost.Page.Draw(ui, body, s);
                if (ModManagerChrome.Footer(ui, ChromeLook.GamePage, footer.X, footer.Y, footer.Width, footer.Height, s, ModManagerHost.Page.Hints))
                    Leave();
            }
            ui.EndWindow();
        }
        finally
        {
            ui.PopStyleRaw();
        }
    }

    /// <summary>The page is not drawn this frame (hidden, opening, or the game left it): the next show starts afresh.</summary>
    public static void NotShown() => _wasShown = false;

    internal static void ResetForTests() => _wasShown = false;
}
