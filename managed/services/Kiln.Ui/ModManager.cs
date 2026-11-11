// SPDX-License-Identifier: MIT
// The Kiln menu as a full-display mod manager in the game's look (docs/kiln/ui.md "The Kiln menu"). Two parts:
//  - ModManagerPage: the content (a tab row with Mods, Network, Keys, Overlay; the Mods section's three columns). It
//    draws into a rectangle it is given at a scale it is given, so another host (the game's own menu page, later) can
//    draw it after its own title and transition.
//  - ModManagerHost: the stand-alone host: a full-display page over a dimmed background with a large title and a
//    footer of key hints, opened by the pause menu's MODS entry, the menu key in a world and every build without the
//    game page; closed by Escape (kiln.ui closes the menu natively).
//  - GamePageHost (GamePage.cs): the page inside the game's own main menu (feature ui.menu_page).
//  - ModManagerChrome: the title and the footer, with a look for each host.
// Render thread only. Nothing here reads a file: the mod list is ModCatalog.Snapshot (a worker refreshes it), status
// lines are StatusLines.Snapshot. Strings shown per row are made when the list changes, not per frame.
namespace Kiln;

/// <summary>A rectangle in screen pixels.</summary>
internal readonly record struct PageRect(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;
}

/// <summary>The sections of the mod manager (its tab row).</summary>
internal enum ModManagerSection
{
    Mods = 0,
    Network = 1,
    Keys = 2,
    Overlay = 3,
}

/// <summary>The left column's selection: every mod, the problems, or one engine.</summary>
internal readonly record struct EngineFilter(string Kind)
{
    public const string AllKind = "*all", AttentionKind = "*attention", SettingsKind = "*settings";
    public static readonly EngineFilter All = new(AllKind), Attention = new(AttentionKind), Settings = new(SettingsKind);
    public bool IsAll => Kind == AllKind;
    public bool IsAttention => Kind == AttentionKind;
    public bool IsSettings => Kind == SettingsKind;
    public bool IsEngine => !Kind.StartsWith('*');
}

/// <summary>A row of the left column.</summary>
internal sealed record EngineRow(EngineFilter Filter, string Label, string Count, EngineHealth Health, string? Note, string Id);

/// <summary>A row of the middle column.</summary>
internal sealed record ModRow(CatalogMod Mod, string Id, string Sub, string Key);

/// <summary>The screen's logic without drawing: what the columns list for a snapshot, a filter and a search (tests use it).</summary>
internal static class ModManagerLogic
{
    public static string KeyOf(CatalogMod m) => m.Engine + "/" + m.Id;

    /// <summary>The left column: All, Needs attention (only when something failed or is missing), then each engine.</summary>
    public static IReadOnlyList<EngineRow> EngineRows(CatalogSnapshot s, bool kilnSettings)
    {
        var rows = new List<EngineRow> { new(EngineFilter.All, "All mods", s.Mods.Count.ToString(), EngineHealth.Ok, null, "##eng.all") };
        var problems = s.Problems;
        if (problems > 0)
            rows.Add(new(EngineFilter.Attention, "Needs attention", problems.ToString(), EngineHealth.Problem, null, "##eng.attention"));
        foreach (var e in s.Engines)
            rows.Add(new(new EngineFilter(e.Kind), e.Name, e.Count.ToString(), e.Health, e.Note, "##eng." + e.Kind));
        if (kilnSettings) rows.Add(new(EngineFilter.Settings, "Kiln settings", "", EngineHealth.Ok, null, "##eng.settings"));
        return rows;
    }

    /// <summary>The filter to use when the chosen one is gone (an engine without mods now, attention with no problem left).</summary>
    public static EngineFilter Valid(CatalogSnapshot s, EngineFilter f, bool kilnSettings)
    {
        if (f.IsAll) return f;
        if (f.IsAttention) return s.Problems > 0 ? f : EngineFilter.All;
        if (f.IsSettings) return kilnSettings ? f : EngineFilter.All;
        return s.Engines.Any(e => e.Kind == f.Kind) ? f : EngineFilter.All;
    }

    /// <summary>Every search word must be in the mod's name, id or engine (case does not matter).</summary>
    public static bool Matches(CatalogMod m, string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        var text = m.SearchText.Length > 0 ? m.SearchText : $"{m.Name} {m.Id} {ModCatalog.EngineName(m.Engine)}".ToLowerInvariant();
        foreach (var word in search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!text.Contains(word.ToLowerInvariant(), StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>The middle column: the filter's mods that match the search. With All and attention the engine shows on each
    /// row; problems come first under attention.</summary>
    public static IReadOnlyList<ModRow> Rows(CatalogSnapshot s, EngineFilter f, string search)
    {
        IEnumerable<CatalogMod> mods = f.IsSettings ? [] : s.Mods;
        if (f.IsAttention) mods = mods.Where(m => m.NeedsAttention);
        else if (f.IsEngine) mods = mods.Where(m => m.Engine == f.Kind);
        var engineShown = f.IsAll || f.IsAttention;
        return mods.Where(m => Matches(m, search))
            .Select(m => new ModRow(m, "##mod." + KeyOf(m),
                engineShown ? ModCatalog.EngineName(m.Engine) + (m.Id != m.Name ? "  ·  " + m.Id : "") : m.Id != m.Name ? m.Id : "",
                KeyOf(m)))
            .ToArray();
    }

    /// <summary>The selection to keep: the same mod when it is still listed, else the first row (null when none).</summary>
    public static string? Select(IReadOnlyList<ModRow> rows, string? selected)
    {
        if (selected is not null && rows.Any(r => r.Key == selected)) return selected;
        return rows.Count > 0 ? rows[0].Key : null;
    }

    /// <summary>The next section for Tab (+1) / Shift+Tab (-1), wrapping.</summary>
    public static ModManagerSection Step(ModManagerSection s, int by)
    {
        const int n = 4;
        return (ModManagerSection)((((int)s + by) % n + n) % n);
    }
}

/// <summary>The game's menu palette (docs/re/game-ui.md section 4) and the mod manager's state colours.</summary>
internal static class GameColors
{
    public static readonly Color Text = Color.FromRgba(0xeeeae2ff);
    public static readonly Color Muted = Color.FromRgba(0xc0cdd5ab);
    public static readonly Color Faint = Color.FromRgba(0xc0cdd570);
    public static readonly Color Gold = Color.FromRgba(0xcaac54ff);
    public static readonly Color GoldBright = Color.FromRgba(0xf5c558ff);
    public static readonly Color GoldDeep = Color.FromRgba(0x896828ff);
    public static readonly Color Tile = Color.FromRgba(0x363246ff);
    public static readonly Color Attention = Color.FromRgba(0xffe06bff);
    public static readonly Color Warning = Color.FromRgba(0xf26e6eff);
    public static readonly Color Ok = Color.FromRgba(0x9fcf8aff);
    public static readonly Color Idle = Color.FromRgba(0x9aa3adff);
    public static readonly Color Night = Color.FromRgba(0x121118ff);
    /// <summary>menu.tileBackgroundColor's alpha in the game's data (169): the panels on the game page.</summary>
    public const byte PanelAlpha = 169;
    /// <summary>A lighter tile for rows hovered or chosen on a tile panel.</summary>
    public static readonly Color TileLight = Color.FromRgba(0x56506eff);

    public static Color Of(ModRunState s, bool missing) => missing ? Warning : s switch
    {
        ModRunState.Loaded => Ok, ModRunState.Waiting => Attention, ModRunState.Failed => Warning, _ => Idle,
    };

    public static Color Of(EngineHealth h) => h switch { EngineHealth.Problem => Warning, EngineHealth.Idle => Idle, _ => Ok };
}

/// <summary>The mod manager's content: draws into a rectangle at a scale. Render thread.</summary>
internal sealed class ModManagerPage
{
    public ModManagerSection Section = ModManagerSection.Mods;
    public EngineFilter Filter = EngineFilter.All;
    public string Search = "";
    public string? Selected;
    /// <summary>Put the keyboard into the search box at the next frame.</summary>
    public bool FocusSearch;
    /// <summary>Each column on its own tile panel (the game page: the game's 3D view is behind, with no dimming). Set by the
    /// host before <see cref="Draw"/>.</summary>
    public bool Backed;

    private long _snapVersion = -1;
    private EngineFilter _rowsFilter;
    private string _rowsSearch = "\0";
    private bool _rowsSettings;
    private IReadOnlyList<EngineRow> _engines = [];
    private IReadOnlyList<ModRow> _rows = [];
    private CatalogSnapshot _snap = CatalogSnapshot.Empty;
    private string _emptyTitle = "", _emptyHint = "";
    private bool _searchActive;
    // Selection follows the gamepad/keyboard focus, but not the focus ImGui places by itself when the page appears.
    private ulong _lastFrame;
    private int _framesShown;
    private string? _focusEngine, _focusMod;
    // FontSize takes sizes at UI scale 1 and kiln.ui applies the UI scale; the page's scale may be larger (the host follows
    // the display size like the game's menus), so font sizes are multiplied by this.
    private float _k = 1;

    private static readonly string[] SectionNames = ["Mods", "Network", "Keys", "Overlay"];
    private static readonly string[] SectionIds = ["##sec.mods", "##sec.network", "##sec.keys", "##sec.overlay"];

    /// <summary>Key hints for the footer of the host, for the current section: (keys, what they do).</summary>
    public IReadOnlyList<(string Keys, string Label)> Hints => Section == ModManagerSection.Mods ? ModsHints : OtherHints;

    private static readonly (string, string)[] ModsHints =
        [("Esc", "Back"), ("Tab", "Next section"), ("Arrows", "Move"), ("Enter", "Choose"), ("Ctrl+F", "Search")];
    private static readonly (string, string)[] OtherHints = [("Esc", "Back"), ("Tab", "Next section"), ("Arrows", "Move"), ("Enter", "Choose")];

    /// <summary>The snapshot to show (a test or the preview sets the catalog; the game uses the worker's).</summary>
    internal Func<CatalogSnapshot> Source = () => ModCatalog.Snapshot(TimeSpan.FromSeconds(2));

    /// <summary>Recomputes the columns when the snapshot, the filter or the search changed (not every frame).</summary>
    internal void Update(CatalogSnapshot snap, bool kilnSettings)
    {
        var changed = snap.Version != _snapVersion || kilnSettings != _rowsSettings;
        if (changed)
        {
            _snap = snap;
            _snapVersion = snap.Version;
            _rowsSettings = kilnSettings;
            _engines = ModManagerLogic.EngineRows(snap, kilnSettings);
        }
        Filter = ModManagerLogic.Valid(snap, Filter, kilnSettings);
        if (!changed && Filter == _rowsFilter && Search == _rowsSearch) return;
        _rowsFilter = Filter;
        _rowsSearch = Search;
        _rows = ModManagerLogic.Rows(snap, Filter, Search);
        if (!Filter.IsSettings) Selected = ModManagerLogic.Select(_rows, Selected);
        if (snap.Mods.Count == 0)
        {
            _emptyTitle = ModCatalog.Loading ? "Reading the mod list..." : "No mods are installed.";
            _emptyHint = ModCatalog.Loading ? "" : "Add mods in the Kiln launcher (Library), put them in your profile, then start the game again.";
        }
        else if (Search.Trim().Length > 0)
        {
            _emptyTitle = $"No mod matches \"{Search.Trim()}\".";
            _emptyHint = "Try another word, or clear the search box.";
        }
        else
        {
            _emptyTitle = "Nothing here.";
            _emptyHint = "Pick All mods on the left.";
        }
    }

    internal IReadOnlyList<ModRow> Rows => _rows;

    private string? _request;

    /// <summary>Shows the Mods section on <paramref name="f"/> at the next frame (any thread: a key binding, ShroudForge's
    /// menu request).</summary>
    public void Request(EngineFilter f) => Volatile.Write(ref _request, f.Kind);
    internal IReadOnlyList<EngineRow> EngineRows => _engines;

    /// <summary>Draws the page into <paramref name="r"/> (screen pixels) at UI scale <paramref name="s"/>. Call inside a
    /// window (the host's); the page makes its own child regions.</summary>
    public void Draw(UiDraw ui, PageRect r, float s)
    {
        _k = s / (ui.Scale > 0 ? ui.Scale : 1);
        if (Interlocked.Exchange(ref _request, null) is { } wanted)
        {
            Section = ModManagerSection.Mods;
            Filter = new EngineFilter(wanted);
            Search = "";
        }
        var kilnSettings = ConfigScreens.HasConfigs("kiln");
        Update(Source(), kilnSettings);
        if (ui.FrameNumber != _lastFrame + 1) _framesShown = 0;  // opened again (or the first time)
        _lastFrame = ui.FrameNumber;
        _framesShown++;
        // Tab / Shift+Tab and the shoulder buttons switch sections (claimed, so ImGui's own focus does not move too)
        var tab = ui.KeyPressed(UiKey.Tab, repeat: false, claim: true);
        if (!_searchActive && tab) Section = ModManagerLogic.Step(Section, (ui.HeldMods & KeyMods.Shift) != 0 ? -1 : 1);
        if (ui.KeyPressed(UiKey.GamepadRB, claim: true)) Section = ModManagerLogic.Step(Section, 1);
        if (ui.KeyPressed(UiKey.GamepadLB, claim: true)) Section = ModManagerLogic.Step(Section, -1);
        if (Section == ModManagerSection.Mods && (ui.HeldMods & KeyMods.Ctrl) != 0 && ui.KeyPressed(UiKey.F)) FocusSearch = true;

        var win = ui.Cursor;
        ui.SetCursor(r.X - win.WindowX, r.Y - win.WindowY);
        using (ui.PushVar(UiStyleVar.WindowPadding, 0, 0))
        using (ui.PushVar(UiStyleVar.ItemSpacing, 10 * s, 8 * s))
        {
            if (ui.BeginChild("kiln.page", r.Width, r.Height, UiChildFlags.NavFlattened, UiWindowFlags.NoScrollbar | UiWindowFlags.NoBackground | UiWindowFlags.NoScrollWithMouse))
            {
                using var baseFont = ui.FontSize(17 * _k);
                var tabsH = TabRow(ui, r, s);
                var body = new PageRect(r.X, r.Y + tabsH + 22 * s, r.Width, r.Height - tabsH - 22 * s);
                switch (Section)
                {
                    case ModManagerSection.Mods: ModsSection(ui, body, s); break;
                    case ModManagerSection.Network: Plain(ui, body, s, "kiln.sec.network", "Network", UiEngine.NetworkTab); break;
                    case ModManagerSection.Keys: Plain(ui, body, s, "kiln.sec.keys", "Keys", UiEngine.KeysTab); break;
                    default: Plain(ui, body, s, "kiln.sec.overlay", "Overlay", UiEngine.OverlayTab); break;
                }
            }
            ui.EndChild();
        }
    }

    // The tab row: the section names in the game's tab colours, the chosen one gold with a bar under it. Returns its height.
    private float TabRow(UiDraw ui, PageRect r, float s)
    {
        var h = 46 * s;
        var x = r.X;
        using var font = ui.FontSize(23 * _k);
        using var c0 = ui.PushColor(UiColorSlot.Header, Color.FromRgba(0x00000000));
        using var c1 = ui.PushColor(UiColorSlot.HeaderHovered, GameColors.Tile.WithAlpha(150));
        using var c2 = ui.PushColor(UiColorSlot.HeaderActive, GameColors.GoldDeep.WithAlpha(150));
        using var align = ui.PushVar(UiStyleVar.SelectableTextAlign, 0.5f, 0.5f);
        for (var i = 0; i < SectionNames.Length; i++)
        {
            var (tw, _) = ui.MeasureText(SectionNames[i]);
            var w = tw + 44 * s;
            ui.SetCursor(x - ui.Cursor.WindowX, r.Y - ui.Cursor.WindowY);
            var chosen = (int)Section == i;
            if (ui.Selectable(SectionIds[i], chosen, w, h)) Section = (ModManagerSection)i;
            var it = ui.LastItem;
            var color = chosen ? GameColors.GoldBright : it.Hovered ? GameColors.Text : GameColors.Muted;
            ui.DrawText(UiLayer.Window, it.MinX + (w - tw) / 2, it.MinY + (h - ui.Cursor.FontSize) / 2 - 1 * s, SectionNames[i], color);
            if (chosen) ui.DrawRect(UiLayer.Window, it.MinX + 10 * s, it.MaxY - 3 * s, it.MaxX - 10 * s, it.MaxY, GameColors.Gold);
            x += w + 6 * s;
        }
        // the line under the whole row
        ui.DrawRect(UiLayer.Window, r.X, r.Y + h + 1 * s, r.Right, r.Y + h + 2 * s, GameColors.Gold.WithAlpha(70));
        ui.SetCursor(r.X - ui.Cursor.WindowX, r.Y + h + 4 * s - ui.Cursor.WindowY);
        return h;
    }

    private static void Caption(UiDraw ui, float x, float y, string text, float s) =>
        ui.DrawText(UiLayer.Window, x, y, text, GameColors.Gold.WithAlpha(210), 14 * s);

    // ---- Mods: engines | mods | details ----
    private void ModsSection(UiDraw ui, PageRect r, float s)
    {
        var gap = 28 * s;
        var left = Math.Clamp(r.Width * 0.21f, 230 * s, 360 * s);
        var right = Math.Clamp(r.Width * 0.37f, 360 * s, 680 * s);
        var mid = r.Width - left - right - 2 * gap;
        if (mid < 300 * s)
        {
            right = Math.Max(300 * s, right - (300 * s - mid));
            mid = r.Width - left - right - 2 * gap;
        }
        var leftR = new PageRect(r.X, r.Y, left, r.Height);
        var midR = new PageRect(r.X + left + gap, r.Y, mid, r.Height);
        var rightR = new PageRect(midR.Right + gap, r.Y, right, r.Height);
        if (Backed)
        {
            // each column on a tile panel (captions inside), its content inset so text never touches the panel's edge
            Panel(ui, leftR, s);
            if (Filter.IsSettings) Panel(ui, new PageRect(midR.X, midR.Y, rightR.Right - midR.X, midR.Height), s);
            else
            {
                Panel(ui, midR, s);
                Panel(ui, rightR, s);
            }
            var p = PanelPad * s;
            leftR = Inset(leftR, p);
            midR = Inset(midR, p);
            rightR = Inset(rightR, p);
        }
        EnginesColumn(ui, leftR, s);
        if (Filter.IsSettings)
        {
            SettingsColumns(ui, midR, rightR, s);
            return;
        }
        ModsColumn(ui, midR, s);
        DetailsColumn(ui, rightR, s);
    }

    /// <summary>The inner padding of a column's panel on the game page (UI units).</summary>
    internal const float PanelPad = 14;

    // The game's tile (menu.tileBackgroundColor at its alpha) over a light dark base, so a bright or busy shot behind
    // still leaves the muted text readable; the hue stays the game's.
    private static void Panel(UiDraw ui, PageRect r, float s)
    {
        ui.DrawRect(UiLayer.Window, r.X, r.Y, r.Right, r.Bottom, GameColors.Night.WithAlpha(110), 2 * s);
        ui.DrawRect(UiLayer.Window, r.X, r.Y, r.Right, r.Bottom, GameColors.Tile.WithAlpha(GameColors.PanelAlpha), 2 * s);
    }

    private static PageRect Inset(PageRect r, float p) => new(r.X + p, r.Y + p, Math.Max(0, r.Width - 2 * p), Math.Max(0, r.Height - 2 * p));

    // Rows: on a tile panel the chosen / hovered rows use a lighter tile, so they still stand out from it.
    private Color RowChosen => Backed ? GameColors.TileLight.WithAlpha(235) : GameColors.Tile.WithAlpha(235);
    private Color RowHovered(byte a) => Backed ? GameColors.TileLight.WithAlpha(a) : GameColors.Tile.WithAlpha(a);

    private void Place(UiDraw ui, float x, float y)
    {
        var c = ui.Cursor;
        ui.SetCursor(x - c.WindowX, y - c.WindowY);
    }

    private void EnginesColumn(UiDraw ui, PageRect r, float s)
    {
        Caption(ui, r.X + 2 * s, r.Y, "ENGINES", s);
        var top = r.Y + 26 * s;
        Place(ui, r.X, top);
        if (ui.BeginChild("kiln.engines", r.Width, r.Bottom - top, UiChildFlags.NavFlattened, UiWindowFlags.NoBackground))
        {
            var rowH = 46 * s;
            using var c0 = ui.PushColor(UiColorSlot.Header, RowChosen);
            using var c1 = ui.PushColor(UiColorSlot.HeaderHovered, RowHovered(150));
            using var c2 = ui.PushColor(UiColorSlot.HeaderActive, GameColors.GoldDeep.WithAlpha(160));
            using var sp = ui.PushVar(UiStyleVar.ItemSpacing, 0, 4 * s);
            var w = ui.Cursor.AvailWidth;
            foreach (var e in _engines)
            {
                if (e.Filter.IsSettings) ui.Space(1, 14 * s);
                var chosen = Filter == e.Filter;
                if (chosen && _framesShown == 1) ui.FocusKeyboardHere();  // the gamepad/keyboard starts on the chosen engine
                var clicked = ui.Selectable(e.Id, chosen, w, rowH);
                var it = ui.LastItem;
                if (chosen) ui.DefaultFocus();
                if (clicked || FocusMoved(it, e.Id, ref _focusEngine)) Filter = e.Filter;
                if (chosen) ui.DrawRect(UiLayer.Window, it.MinX, it.MinY, it.MinX + 3 * s, it.MaxY, GameColors.Gold);
                var ty = it.MinY + (rowH - 18 * s) / 2 - 1 * s;
                var labelColor = e.Filter.IsAttention ? GameColors.Warning : chosen ? GameColors.Text : GameColors.Text.WithAlpha(215);
                ui.DrawText(UiLayer.Window, it.MinX + 16 * s, ty, e.Label, labelColor, 18 * s);
                if (e.Count.Length > 0)
                {
                    var (cw, _) = ui.MeasureText(e.Count, 16 * s, 0);
                    ui.DrawText(UiLayer.Window, it.MaxX - 14 * s - cw, ty + 1 * s, e.Count, GameColors.Muted, 16 * s);
                    if (e.Filter.IsEngine)
                    {
                        var dot = GameColors.Of(e.Health);
                        var cx = it.MaxX - 26 * s - cw;
                        var cy = it.MinY + rowH / 2;
                        ui.DrawRect(UiLayer.Window, cx - 4 * s, cy - 4 * s, cx + 4 * s, cy + 4 * s, dot.WithAlpha(e.Health == EngineHealth.Ok ? (byte)170 : (byte)255), 4 * s);
                    }
                }
                if (e.Note is { } note && it.Hovered) ui.Tooltip(note);
            }
        }
        ui.EndChild();
    }

    private void ModsColumn(UiDraw ui, PageRect r, float s)
    {
        // search box with a placeholder (the field has no hint text of its own)
        Caption(ui, r.X + 2 * s, r.Y, Filter.IsAll ? "ALL MODS" : Filter.IsAttention ? "NEEDS ATTENTION" : ModCatalog.EngineName(Filter.Kind).ToUpperInvariant(), s);
        var y = r.Y + 26 * s;
        Place(ui, r.X, y);
        using (ui.PushVar(UiStyleVar.FramePadding, 14 * s, 10 * s))
        using (ui.PushColor(UiColorSlot.FrameBg, GameColors.Night.WithAlpha(200)))
        using (ui.PushColor(UiColorSlot.Border, GameColors.Gold.WithAlpha(90)))
        {
            if (FocusSearch)
            {
                ui.FocusKeyboardHere();
                FocusSearch = false;
            }
            ui.NextItemWidth(r.Width);
            ui.InputText("##kiln.search", ref Search, 128);
            var it = ui.LastItem;
            _searchActive = it.Active;
            if (Search.Length == 0 && !it.Active)
                ui.DrawText(UiLayer.Window, it.MinX + 14 * s, it.MinY + (it.Height - ui.Cursor.FontSize) / 2, "Search mods (Ctrl+F)", GameColors.Faint);
            y = it.MaxY + 12 * s;
        }
        // the engine's own line, when it says something
        if (Filter.IsEngine && _snap.Engines.FirstOrDefault(e => e.Kind == Filter.Kind) is { Note: { } note } eng)
        {
            var (_, nh) = ui.MeasureText(note, 15 * s, r.Width - 34 * s);
            var bh = nh + 18 * s;
            var c = GameColors.Of(eng.Health);
            ui.DrawRect(UiLayer.Window, r.X, y, r.Right, y + bh, Backed ? GameColors.Night.WithAlpha(110) : GameColors.Tile.WithAlpha(150), 2 * s);
            ui.DrawRect(UiLayer.Window, r.X, y, r.X + 3 * s, y + bh, c);
            ui.DrawText(UiLayer.Window, r.X + 16 * s, y + 9 * s, note, GameColors.Text.WithAlpha(230), 15 * s, r.Width - 34 * s);
            y += bh + 10 * s;
        }
        Place(ui, r.X, y);
        if (ui.BeginChild("kiln.mods", r.Width, r.Bottom - y, UiChildFlags.NavFlattened, UiWindowFlags.NoBackground))
        {
            var w = ui.Cursor.AvailWidth;
            if (_rows.Count == 0)
            {
                var c = ui.Cursor;
                ui.DrawText(UiLayer.Window, c.ScreenX + 4 * s, c.ScreenY + 40 * s, _emptyTitle, GameColors.Text, 20 * s, w - 8 * s);
                if (_emptyHint.Length > 0)
                    ui.DrawText(UiLayer.Window, c.ScreenX + 4 * s, c.ScreenY + 72 * s, _emptyHint, GameColors.Muted, 16 * s, w - 8 * s);
                ui.Space(w, 120 * s);
            }
            var rowH = 58 * s;
            using var c0 = ui.PushColor(UiColorSlot.Header, RowChosen);
            using var c1 = ui.PushColor(UiColorSlot.HeaderHovered, RowHovered(140));
            using var c2 = ui.PushColor(UiColorSlot.HeaderActive, GameColors.GoldDeep.WithAlpha(160));
            using var sp = ui.PushVar(UiStyleVar.ItemSpacing, 0, 3 * s);
            foreach (var row in _rows)
            {
                var m = row.Mod;
                var chosen = row.Key == Selected;
                var clicked = ui.Selectable(row.Id, chosen, w, rowH);
                var it = ui.LastItem;
                if (clicked || FocusMoved(it, row.Id, ref _focusMod)) Selected = row.Key;
                if (!it.Visible) continue;
                if (chosen) ui.DrawRect(UiLayer.Window, it.MinX, it.MinY, it.MinX + 3 * s, it.MaxY, GameColors.Gold);
                // the chips sit right-aligned in a column as wide as the widest word, so the versions line up
                var chipW = ChipWidth(ui, m.Chip, s);
                var chipCol = it.MaxX - 14 * s - ChipWidth(ui, "Not loaded", s);
                var chipX = it.MaxX - 14 * s - chipW;
                var verW = 0f;
                if (m.Version.Length > 0) (verW, _) = ui.MeasureText(m.Version, 15 * s, 0);
                var nameW = chipCol - (it.MinX + 18 * s) - verW - 28 * s;
                var oneLine = row.Sub.Length == 0;
                var ny = oneLine ? it.MinY + (rowH - 19 * s) / 2 - 1 * s : it.MinY + 9 * s;
                ui.DrawText(UiLayer.Window, it.MinX + 18 * s, ny, Fit(ui, m.Name, 19 * s, nameW), GameColors.Text, 19 * s);
                if (!oneLine)
                    ui.DrawText(UiLayer.Window, it.MinX + 18 * s, it.MinY + 33 * s, Fit(ui, row.Sub, 14 * s, nameW), GameColors.Muted, 14 * s);
                if (verW > 0)
                    ui.DrawText(UiLayer.Window, chipCol - 18 * s - verW, it.MinY + (rowH - 15 * s) / 2 - 1 * s, m.Version, GameColors.Muted, 15 * s);
                Chip(ui, chipX, it.MinY + (rowH - 24 * s) / 2, m.Chip, GameColors.Of(m.State, m.Missing), s);
            }
        }
        ui.EndChild();
    }

    // True when the focus came to this row from another row of the same column (arrows, d-pad), after the page settled.
    private bool FocusMoved(in UiItem it, string id, ref string? last)
    {
        if (!it.Focused || string.Equals(last, id, StringComparison.Ordinal)) return false;
        var moved = last is not null && _framesShown > 2;
        last = id;
        return moved;
    }

    // Long names end with "..." instead of running under the version and the chip (cut per frame only when too long).
    private static string Fit(UiDraw ui, string text, float size, float width)
    {
        if (width <= 0) return "";
        var (w, _) = ui.MeasureText(text, size, 0);
        if (w <= width) return text;
        var lo = 0;
        var hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (ui.MeasureText(string.Concat(text.AsSpan(0, mid), "..."), size, 0).Width <= width) lo = mid;
            else hi = mid - 1;
        }
        return string.Concat(text.AsSpan(0, lo).TrimEnd(), "...");
    }

    private static float ChipWidth(UiDraw ui, string text, float s) => ui.MeasureText(text, 14 * s, 0).Width + 20 * s;

    private static float Chip(UiDraw ui, float x, float y, string text, Color c, float s)
    {
        var w = ChipWidth(ui, text, s);
        var h = 24 * s;
        ui.DrawRect(UiLayer.Window, x, y, x + w, y + h, c.WithAlpha(38), 3 * s);
        ui.DrawRect(UiLayer.Window, x, y, x + w, y + h, c.WithAlpha(150), 3 * s, 1 * s);
        ui.DrawText(UiLayer.Window, x + 10 * s, y + (h - 14 * s) / 2 - 1 * s, text, c, 14 * s);
        return w;
    }

    private void DetailsColumn(UiDraw ui, PageRect r, float s)
    {
        Caption(ui, r.X + 2 * s, r.Y, "DETAILS", s);
        var top = r.Y + 26 * s;
        Place(ui, r.X, top);
        var mod = Selected is null ? null : _rows.FirstOrDefault(x => x.Key == Selected)?.Mod;
        // on the game page the column's panel is the background (no second tile inside it)
        using (ui.PushColor(UiColorSlot.ChildBg, Backed ? Color.FromRgba(0x00000000) : GameColors.Tile.WithAlpha(115)))
        using (ui.PushVar(UiStyleVar.WindowPadding, Backed ? 12 * s : 26 * s, Backed ? 8 * s : 22 * s))
        using (ui.PushVar(UiStyleVar.ChildRounding, 2 * s))
        {
            if (ui.BeginChild("kiln.details", r.Width, r.Bottom - top, UiChildFlags.NavFlattened | UiChildFlags.Padding))
            {
                if (mod is null)
                {
                    using (ui.PushColor(UiColorSlot.Text, GameColors.Muted))
                        ui.TextWrapped(_rows.Count == 0 ? "Pick a mod in the list to see what it is, whether it loaded, and its settings." : "Pick a mod to see its details.");
                }
                else Details(ui, mod, s);
            }
            ui.EndChild();
        }
    }

    private void Label(UiDraw ui, string text, float s)
    {
        ui.Space(1, 10 * s);
        using (ui.FontSize(14 * _k))
        using (ui.PushColor(UiColorSlot.Text, GameColors.Gold.WithAlpha(210)))
            ui.Text(text);
    }

    private void Details(UiDraw ui, CatalogMod m, float s)
    {
        using (ui.FontSize(31 * _k))
        using (ui.PushColor(UiColorSlot.Text, GameColors.Text))
            ui.TextWrapped(m.Name);
        using (ui.FontSize(15 * _k))
        using (ui.PushColor(UiColorSlot.Text, GameColors.Muted))
            ui.TextWrapped(m.Version.Length > 0 ? m.Id + "   ·   version " + m.Version : m.Id);
        ui.Space(1, 6 * s);
        // the chips: state, engine, side
        var c = ui.Cursor;
        var x = c.ScreenX;
        x += Chip(ui, x, c.ScreenY, m.Chip, GameColors.Of(m.State, m.Missing), s) + 8 * s;
        x += Chip(ui, x, c.ScreenY, ModCatalog.EngineName(m.Engine), GameColors.Gold, s) + 8 * s;
        if (m.Side.Length > 0) Chip(ui, x, c.ScreenY, SideText(m.Side), GameColors.Idle, s);
        ui.Space(1, 26 * s);

        if (m.Reason is not null || m.Help is not null)
        {
            Label(ui, m.State == ModRunState.Failed || m.Missing ? "WHAT WENT WRONG" : "STATE", s);
            if (m.Reason is { } why)
                using (ui.PushColor(UiColorSlot.Text, m.State == ModRunState.Failed || m.Missing ? GameColors.Warning : GameColors.Text))
                    ui.TextWrapped(why);
            if (m.Help is { } help)
                using (ui.PushColor(UiColorSlot.Text, GameColors.Attention))
                    ui.TextWrapped(help);
        }
        var engine = _snap.Engines.FirstOrDefault(e => e.Kind == m.Engine);
        Label(ui, "ENGINE", s);
        ui.TextWrapped(ModCatalog.EngineAbout(m.Engine));
        if (engine?.Note is { } note)
            using (ui.PushColor(UiColorSlot.Text, GameColors.Muted)) ui.TextWrapped(note);

        if (m.Engine == "kiln")
        {
            if (m.LoadIndex >= 0)
            {
                Label(ui, "LOAD ORDER", s);
                ui.Text($"Loads as number {m.LoadIndex + 1}.");
            }
            if (m.Dependencies.Count > 0)
            {
                Label(ui, "NEEDS", s);
                foreach (var d in m.Dependencies)
                {
                    var have = d.Id == "kiln" || _snap.Mods.Any(x => x.Engine == "kiln" && x.Id == d.Id && x.State == ModRunState.Loaded);
                    using (ui.PushColor(UiColorSlot.Text, have ? GameColors.Text : d.Optional ? GameColors.Muted : GameColors.Warning))
                        ui.TextWrapped($"{d.Id} {(d.VersionRange is "*" or "" ? "" : d.VersionRange)}{(d.Optional ? " (optional)" : "")}{(have ? "" : d.Optional ? ": not loaded" : ": missing")}");
                }
            }
        }
        if (m.Folder is { } folder)
        {
            Label(ui, "FOLDER", s);
            using (ui.FontSize(14 * _k))
            using (ui.PushColor(UiColorSlot.Text, GameColors.Muted))
                ui.TextWrapped(folder);
        }
        if (m.Engine == "shroudforge") ShroudForgeUi.Draw(ui, m, s, _k, Label);
        if (m.Engine == "kiln")
        {
            IReadOnlyList<ModStatus> status;
            try { status = StatusLines.Snapshot(TimeSpan.FromMilliseconds(500)); }
            catch (Exception) { status = []; }
            var mine = status.FirstOrDefault(x => x.ModId == m.Id);
            if (mine is { Lines.Count: > 0 })
            {
                Label(ui, "STATUS", s);
                foreach (var line in mine.Lines) ui.TextWrapped(line);
            }
            UiEngine.DrawConfig(ui, m.Id, s, Label);
        }
    }

    private static string SideText(string side) => side switch
    {
        "client" => "Game only", "server" => "Server only", "both" => "Game and server", _ => side,
    };

    private void SettingsColumns(UiDraw ui, PageRect mid, PageRect right, float s)
    {
        var r = new PageRect(mid.X, mid.Y, right.Right - mid.X, mid.Height);
        Caption(ui, r.X + 2 * s, r.Y, "KILN SETTINGS", s);
        var top = r.Y + 26 * s;
        Place(ui, r.X, top);
        using (ui.PushColor(UiColorSlot.ChildBg, Backed ? Color.FromRgba(0x00000000) : GameColors.Tile.WithAlpha(115)))
        using (ui.PushVar(UiStyleVar.WindowPadding, Backed ? 12 * s : 26 * s, Backed ? 8 * s : 22 * s))
        {
            if (ui.BeginChild("kiln.settings", r.Width, r.Bottom - top, UiChildFlags.NavFlattened | UiChildFlags.Padding))
                ConfigScreens.Draw(ui, "kiln");
            ui.EndChild();
        }
    }

    // Network, Keys and Overlay: the old tabs' contents in a tile.
    private void Plain(UiDraw ui, PageRect r, float s, string id, string title, Action<UiDraw> draw)
    {
        var w = Math.Min(r.Width, 1180 * s);
        var x = r.X;
        var c = ui.Cursor;
        ui.SetCursor(x - c.WindowX, r.Y - c.WindowY);
        using (ui.PushColor(UiColorSlot.ChildBg, Backed ? Color.FromRgba(0x00000000) : GameColors.Tile.WithAlpha(115)))
        using (ui.PushVar(UiStyleVar.WindowPadding, 30 * s, 24 * s))
        {
            if (Backed) Panel(ui, new PageRect(x, r.Y, w, r.Height), s);
            if (ui.BeginChild(id, w, r.Height, UiChildFlags.NavFlattened | UiChildFlags.Padding))
            {
                using (ui.FontSize(17 * _k)) draw(ui);
            }
            ui.EndChild();
        }
    }
}

/// <summary>The stand-alone host of <see cref="ModManagerPage"/>: the whole display, a dimmed background, the title and the
/// footer. Shown while the Kiln menu is open outside the game page (the pause menu's MODS entry, the menu key in a world,
/// a build without feature ui.menu_page); Escape closes it (kiln.ui does that natively).</summary>
internal static class ModManagerHost
{
    public static readonly ModManagerPage Page = new();

    public static void Draw(UiDraw ui)
    {
        var w = ui.Width;
        var h = ui.Height;
        // like the game's own menus the page grows with the display (1920x1080 = 1), on top of the UI scale
        var k = Math.Clamp(Math.Min(h / 1080f, w / 1920f), 0.7f, 3f);
        var s = (ui.Scale > 0 ? ui.Scale : 1) * k;
        ui.PushStyleRaw(UiStyle.Game);
        try
        {
            // the dimmed game behind: darker at the top and bottom, like the game's own menus
            ui.DrawRect(UiLayer.Background, 0, 0, w, h, GameColors.Night.WithAlpha(200));
            const int bands = 10;  // a little darker towards the top and the bottom edge, in soft steps
            for (var i = 0; i < bands; i++)
            {
                var a = (byte)(5 * (bands - i));
                var y0 = MathF.Floor(h * 0.18f * i / bands);
                var y1 = MathF.Floor(h * 0.18f * (i + 1) / bands);
                ui.DrawRect(UiLayer.Background, 0, y0, w, y1, Color.Black.WithAlpha(a));
                ui.DrawRect(UiLayer.Background, 0, h - y1, w, h - y0, Color.Black.WithAlpha(a));
            }
            ui.NextWindowPos(0, 0, 1);
            ui.NextWindowSize(w, h, 1);
            using var pad = ui.PushVar(UiStyleVar.WindowPadding, 0, 0);
            using var border = ui.PushVar(UiStyleVar.WindowBorderSize, 0);
            if (ui.BeginWindowNoClose($"Kiln {Game.KilnVersion}###kiln.menu.main", ModManagerChrome.PageWindowFlags))
            {
                var margin = Math.Max(48 * s, w * 0.055f);
                var top = Math.Max(36 * s, h * 0.055f);
                var footerH = 64 * s;
                var cw = ModManagerChrome.ContentWidth(w - 2 * margin, s);  // ultrawide: the extra room stays empty on the right
                ModManagerChrome.Title(ui, ChromeLook.Standalone, margin, top, margin + cw, 46 * s, s);
                var bodyTop = top + 46 * s + 44 * s;
                var body = new PageRect(margin, bodyTop, cw, h - bodyTop - footerH - 18 * s);
                Page.Backed = false;
                Page.Draw(ui, body, s);
                if (ModManagerChrome.Footer(ui, ChromeLook.Standalone, margin, h - footerH, cw, footerH, s, Page.Hints))
                    Ui.MenuOpen = false;
            }
            ui.EndWindow();
        }
        finally
        {
            ui.PopStyleRaw();
        }
    }

    internal static void ResetForTests() => ModManagerChrome.ResetForTests();
}

/// <summary>The two looks of the mod manager's title and footer.</summary>
internal enum ChromeLook
{
    /// <summary>The stand-alone page: a large regular title with the subtitle under it, a gold line over the footer.</summary>
    Standalone,
    /// <summary>Inside the game's main menu: its page header (bold, upper case, letter-spaced, white) and its hint bar.</summary>
    GamePage,
}

/// <summary>The title and the footer of the mod manager, shared by both hosts.</summary>
internal static class ModManagerChrome
{
    // no title bar, resize, move, scrollbar, mouse-wheel scroll, collapse, background, saved settings
    public const uint PageWindowFlags = (1u << 0) | (1u << 1) | (1u << 2) | (1u << 3) | (1u << 4) | (1u << 5) | (1u << 7) | (1u << 8);

    /// <summary>The widest the page's content gets, in UI units (1920x1080 = 1 unit per pixel). 16:9 and 16:10 pages are
    /// narrower (1760 units inside the game's padding); on ultrawide displays the rest stays empty on the right, so lines
    /// keep a comfortable length.</summary>
    internal const float MaxContentUnits = 2000;

    /// <summary>The content width for <paramref name="available"/> pixels at scale <paramref name="s"/>.</summary>
    public static float ContentWidth(float available, float s) => Math.Min(available, MaxContentUnits * s);

    /// <summary>Soft dark bands at the top (<paramref name="topTo"/> down) and the bottom (<paramref name="bottomFrom"/> down)
    /// of the display, behind every window: the title, the tabs and the hints stay readable on a bright picture.</summary>
    public static void Bands(UiDraw ui, float topTo, float bottomFrom, byte alpha)
    {
        // full strength over most of each band (the text sits there), fading out over its last third
        const int steps = 16;
        var h = ui.Height;
        var bh = h - bottomFrom;
        for (var i = 0; i < steps; i++)
        {
            var mid = (i + 0.5f) / steps;  // 0 at the display's edge, 1 at the band's inner end
            var a = (byte)(alpha * Math.Clamp((1 - mid) / 0.35f, 0, 1));
            var t0 = MathF.Floor(topTo * i / steps);
            var t1 = MathF.Floor(topTo * (i + 1) / steps);
            ui.DrawRect(UiLayer.Background, 0, t0, ui.Width, t1, Color.Black.WithAlpha(a));
            var b0 = MathF.Floor(h - bh * (i + 1) / steps);
            var b1 = MathF.Floor(h - bh * i / steps);
            ui.DrawRect(UiLayer.Background, 0, b0, ui.Width, b1, Color.Black.WithAlpha(a));
        }
    }

    /// <summary>The game page's title: what its own pages show for "Mods" (made once: drawn a letter at a time).</summary>
    internal const string GameTitle = "MODS";
    private static readonly string[] GameTitleLetters = GameTitle.Select(c => c.ToString()).ToArray();
    /// <summary>The game's header tracking: 0.05 of the font size between letters (namedStyles.menu.header1).</summary>
    internal const float Tracking = 0.05f;

    private static string? _subtitle;
    private static long _subtitleAt;

    /// <summary>The title at (x, y) in pixels, <paramref name="size"/> pixels high; the subtitle under it (stand-alone) or
    /// right-aligned to <paramref name="right"/> on the title's line (game page).</summary>
    public static void Title(UiDraw ui, ChromeLook look, float x, float y, float right, float size, float s)
    {
        var sub = Subtitle();
        if (look == ChromeLook.Standalone)
        {
            using (ui.FontSize(size / (ui.Scale > 0 ? ui.Scale : 1)))
            {
                var titleSize = ui.Cursor.FontSize;
                ui.DrawText(UiLayer.Window, x, y, "Mod Manager", GameColors.Text, titleSize);
                ui.DrawText(UiLayer.Window, x + 2 * s, y + titleSize + 6 * s, sub, GameColors.Muted, 16 * s);
            }
            return;
        }
        // bold when the game's bold face is loaded, else the game look's regular face; upper case either way
        using (ui.Font(UiFont.GameBold))
        {
            var cx = x;
            var shadow = MathF.Max(1, MathF.Round(size / 23));  // a soft drop shadow: readable on a bright picture too
            foreach (var letter in GameTitleLetters)
            {
                ui.DrawText(UiLayer.Window, cx + shadow, y + shadow, letter, Color.Black.WithAlpha(150), size);
                ui.DrawText(UiLayer.Window, cx, y, letter, Color.White, size);
                cx += ui.MeasureText(letter, size, 0).Width + Tracking * size;
            }
        }
        var (sw, _) = ui.MeasureText(sub, 16 * s, 0);
        ui.DrawText(UiLayer.Window, right - sw, y + size - 22 * s, sub, GameColors.Muted, 16 * s);
    }

    /// <summary>Kiln's version, the profile and how many mods loaded (made at most every 2 seconds).</summary>
    public static string Subtitle()
    {
        var now = Environment.TickCount64;
        if (_subtitle is not null && now - _subtitleAt < 2000) return _subtitle;
        _subtitleAt = now;
        var snap = ModCatalog.Snapshot(TimeSpan.FromSeconds(2));
        var n = snap.Mods.Count(m => m.State == ModRunState.Loaded);
        var parts = new List<string> { $"Kiln {Game.KilnVersion}" };
        if (snap.Profile is { } p) parts.Add(p);
        if (snap.Mods.Count > 0) parts.Add($"{n} of {snap.Mods.Count} mods loaded");
        return _subtitle = string.Join("   ·   ", parts);
    }

    internal static void ResetForTests() => _subtitle = null;

    /// <summary>The key hints, right to left from the right edge like the game's own hint bar. The "Back" hint is also a
    /// button: true when it was clicked (or activated with the keyboard / gamepad) this frame.</summary>
    public static bool Footer(UiDraw ui, ChromeLook look, float x, float y, float w, float h, float s, IReadOnlyList<(string Keys, string Label)> hints)
    {
        var game = look == ChromeLook.GamePage;
        if (!game) ui.DrawRect(UiLayer.Window, x, y, x + w, y + 1 * s, GameColors.Gold.WithAlpha(70));
        var labelSize = (game ? 18 : 16) * s;
        var keySize = (game ? 15 : 14) * s;
        var capH = (game ? 28 : 26) * s;
        var ty = y + (h - labelSize) / 2;
        var right = x + w;
        var back = false;
        for (var i = hints.Count - 1; i >= 0; i--)
        {
            var (keys, label) = hints[i];
            var (lw, _) = ui.MeasureText(label, labelSize, 0);
            var (kw, _) = ui.MeasureText(keys, keySize, 0);
            var capW = kw + 16 * s;
            var start = right - lw - 10 * s - capW;
            var capY = ty + (labelSize - capH) / 2;
            if (label == "Back")
            {
                // a click target over the whole hint (key cap and label)
                var c = ui.Cursor;
                ui.SetCursor(start - 6 * s - c.WindowX, capY - 4 * s - c.WindowY);
                using (ui.PushColor(UiColorSlot.Header, Color.FromRgba(0x00000000)))
                using (ui.PushColor(UiColorSlot.HeaderHovered, GameColors.Tile.WithAlpha(150)))
                using (ui.PushColor(UiColorSlot.HeaderActive, GameColors.GoldDeep.WithAlpha(150)))
                    back = ui.Selectable("##kiln.back", false, right - start + 12 * s, capH + 8 * s);
            }
            var capFill = game ? Color.Black.WithAlpha(110) : GameColors.Text.WithAlpha(22);
            ui.DrawRect(UiLayer.Window, start, capY, start + capW, capY + capH, capFill, 3 * s);
            ui.DrawRect(UiLayer.Window, start, capY, start + capW, capY + capH, (game ? GameColors.Text : GameColors.Muted).WithAlpha(game ? (byte)150 : (byte)120), 3 * s, 1 * s);
            ui.DrawText(UiLayer.Window, start + 8 * s, capY + (capH - keySize) / 2, keys, GameColors.Text, keySize);
            ui.DrawText(UiLayer.Window, start + capW + 10 * s, ty, label, game ? GameColors.Text : GameColors.Muted, labelSize);
            right = start - 30 * s;
        }
        const string pad = "Gamepad: LB / RB sections, A choose, B back";
        if (ui.MeasureText(pad, 15 * s, 0).Width + 40 * s < right - x)
            ui.DrawText(UiLayer.Window, x, ty, pad, GameColors.Faint, 15 * s);
        return back;
    }
}
