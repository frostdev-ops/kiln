// SPDX-License-Identifier: MIT
// The creative menu (game client): a Kiln screen on F7 and in the pause menu. Items from the server's catalogue, the
// time of day, the day and night cycle, creative mode on or off. Every action is a request the server checks
// (CreativeServer); the menu only shows what the server answers.
namespace Kiln;

internal static class CreativeMenu
{
    public const string KeyId = "kiln.creative";
    public const string DefaultKey = "F7";

    private static readonly object Lock = new();
    private static UiScreen? _screen;
    private static MenuEntry? _entry;
    private static HudLine? _hud;
    private static bool _started;

    // what the server said (any thread writes, the render thread reads)
    private static CreativeStateMsg? _state;
    private static string _reply = "";
    private static bool _replyOk = true;
    private static readonly List<CreativeItem> Catalog = new();
    private static int _catalogTotal = -1;
    private static DateTime _askedState, _askedCatalog;

    // the menu's own inputs (render thread)
    private static string _search = "";
    private static int _category;
    private static bool _noEquipment;
    private static int _selected = -1;
    private static int _count = 1, _level = 1, _rarity;
    private static float _hours = 12;
    private static List<CreativeItem> _shown = new();
    private static (string, int, bool, int) _shownFor = ("", -1, false, -1);

    private static readonly (string Name, ItemCategory? Category)[] Categories =
    [
        ("All", null), ("Materials", ItemCategory.Materials), ("Building", ItemCategory.BuildTools), ("Consumables", ItemCategory.Consumables),
        ("Ammunition", ItemCategory.Ammunition), ("Weapons", ItemCategory.Weapons), ("Equipment", ItemCategory.Equipment),
        ("Tools", ItemCategory.Tools), ("Instruments", ItemCategory.Instrument), ("Fishing", ItemCategory.FishingRod),
        ("Customization", ItemCategory.Customization), ("Gems", ItemCategory.WeaponGem), ("Colour palettes", ItemCategory.ColorPalette),
        ("Animal food", ItemCategory.AnimalFood), ("Collectibles", ItemCategory.Collectibles), ("Blueprints", ItemCategory.Blueprints),
        ("Currency", ItemCategory.Currency),
    ];
    private static readonly string[] CategoryNames = Categories.Select(c => c.Name).ToArray();
    private static readonly string[] RarityNames = ["Common", "Uncommon", "Rare", "Epic", "Legendary"];

    /// <summary>Game client only: the screen, its key and the pause-menu entry.</summary>
    public static void Start()
    {
        lock (Lock)
        {
            if (_started) return;
            _started = true;
        }
        try
        {
            if (!Ui.Available) return;
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException)
        {
            return;  // no Kiln.Ui (a dedicated server's host folder)
        }
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = null;  // Kiln's, not the mod whose tick happened to start it
        try
        {
            _screen = Ui.Screen("Creative", Draw, new UiScreenOptions { Style = UiStyle.Game, Width = 820, Height = 600 });
            _screen.Opened += () => Refresh(catalog: true);
            KeyBindings.Register(KeyId, DefaultKey, () => _screen.Toggle(), "Open the creative menu", "Creative");
            _entry = Ui.Menus.Add(MenuLocation.PauseMenu, "Creative", () => _screen.Open(), order: 900);
            if (_entry != null) _entry.Visible = false;
            RulesRuntime.Log(LogLevel.Info, $"creative menu: on {KeyBindings.All.FirstOrDefault(b => b.Id == KeyId)?.Key.ToString() ?? DefaultKey}" +
                                            (_entry != null ? " and in the pause menu" : ""));
        }
        catch (Exception e)
        {
            RulesRuntime.Log(LogLevel.Warn, $"creative menu unavailable: {e.Message}");
        }
        finally
        {
            ModContext.CurrentMod = outer;
        }
    }

    // ---------------------------------------------------------------------------------------- from the server --

    public static void OnServerReady() => Refresh(catalog: false);

    public static void OnDisconnected()
    {
        lock (Lock)
        {
            _state = null;
            Catalog.Clear();
            _catalogTotal = -1;
            _reply = "";
        }
        Apply(null);
    }

    public static void OnState(CreativeStateMsg m)
    {
        lock (Lock) _state = m;
        Apply(m);
    }

    public static void OnReply(CreativeReplyMsg m)
    {
        lock (Lock)
        {
            _reply = m.Text;
            _replyOk = m.Ok;
        }
    }

    public static void OnCatalog(CreativeCatalogMsg m)
    {
        var next = -1;
        lock (Lock)
        {
            if (m.Start == 0) Catalog.Clear();
            if (m.Start != Catalog.Count) return;  // an old answer
            Catalog.AddRange(m.Items);
            _catalogTotal = m.Total;
            if (Catalog.Count < m.Total && m.Items.Count > 0) next = Catalog.Count;
            _shownFor = ("", -1, false, -1);
        }
        if (next >= 0) CreativeNet.Ask(new CreativeRequest(CreativeAsk.Catalog, Count: next));
    }

    // The pause-menu entry shows for those who may use the menu; a HUD line while creative mode is on.
    private static void Apply(CreativeStateMsg? m)
    {
        try
        {
            var access = m?.Access ?? CreativeAccess.None;
            if (_entry != null) _entry.Visible = (access & (CreativeAccess.Admin | CreativeAccess.On)) != 0;
            var on = (access & CreativeAccess.On) != 0;
            if (on && _hud is null)
            {
                var outer = ModContext.CurrentMod;
                ModContext.CurrentMod = null;
                try
                {
                    _hud = Hud.Show("kiln.creative", "Creative", HudAnchor.TopRight, new Color(255, 196, 92));
                }
                finally
                {
                    ModContext.CurrentMod = outer;
                }
            }
            else if (!on && _hud is not null)
            {
                _hud.Dispose();
                _hud = null;
            }
        }
        catch (Exception)
        {
            // no UI (never on a dedicated server)
        }
    }

    private static void Refresh(bool catalog)
    {
        _askedState = DateTime.UtcNow;
        CreativeNet.Ask(new CreativeRequest(CreativeAsk.State));
        bool need;
        lock (Lock) need = catalog && (_catalogTotal < 0 || Catalog.Count < _catalogTotal) && DateTime.UtcNow - _askedCatalog > TimeSpan.FromSeconds(5);
        if (!need) return;
        _askedCatalog = DateTime.UtcNow;
        lock (Lock) Catalog.Clear();
        CreativeNet.Ask(new CreativeRequest(CreativeAsk.Catalog, Count: 0));
    }

    // ---------------------------------------------------------------------------------------- drawing ----------

    private static void Draw(UiDraw ui)
    {
        if (DateTime.UtcNow - _askedState > TimeSpan.FromSeconds(2)) Refresh(catalog: false);
        CreativeStateMsg? st;
        string reply;
        bool ok;
        lock (Lock)
        {
            st = _state;
            reply = _reply;
            ok = _replyOk;
        }
        if (st is not { } s)
        {
            ui.TextWrapped(CreativeNet.Open || CreativeNet.Hosting(out _)
                ? "Asking the server..."
                : "This server has no Kiln creative menu (it needs Kiln on the server).");
            return;
        }
        var on = (s.Access & CreativeAccess.On) != 0;
        var admin = (s.Access & CreativeAccess.Admin) != 0;
        ui.Text(on ? new Color(255, 196, 92) : new Color(200, 200, 200), on ? "Creative mode is on" : "Creative mode is off");
        if (admin)
        {
            ui.SameLine();
            if (ui.SmallButton(on ? "Turn off" : "Turn on")) CreativeNet.Ask(new CreativeRequest(CreativeAsk.Creative, Count: on ? 0 : 1));
        }
        else if (!on)
        {
            ui.SameLine();
            ui.TextDisabled("(an admin turns it on: /creative on <you>)");
        }
        if (reply.Length > 0) ui.TextWrapped((ok ? "" : "Refused: ") + reply);
        ui.Separator();
        if (ui.BeginTabBar("creative"))
        {
            if (ui.BeginTab("Items"))
            {
                DrawItems(ui, s);
                ui.EndTab();
            }
            if (ui.BeginTab("World"))
            {
                DrawWorld(ui, s);
                ui.EndTab();
            }
            if (ui.BeginTab("Rules"))
            {
                ui.TextWrapped(on ? "Your rules: " + s.Rules : "Your rules: " + (s.Rules.Length > 0 ? s.Rules : "the game's"));
                ui.Spacing();
                ui.TextWrapped(s.Hooks);
                ui.Spacing();
                ui.TextDisabled("Key: " + (KeyBindings.All.FirstOrDefault(b => b.Id == KeyId)?.Key.ToString() ?? DefaultKey) + " (Kiln menu > Keys to change it).");
                ui.EndTab();
            }
            ui.EndTabBar();
        }
    }

    private static void DrawItems(UiDraw ui, CreativeStateMsg s)
    {
        var canGive = (s.Access & CreativeAccess.Give) != 0;
        if (!canGive)
            ui.TextWrapped((s.Access & CreativeAccess.On) != 0
                ? "This server's game build cannot give items."
                : "Taking items needs creative mode.");
        ui.InputText("Search", ref _search, 64);
        ui.SameLine();
        ui.Checkbox("Without equipment", ref _noEquipment);
        ui.Combo("Category", ref _category, CategoryNames);
        List<CreativeItem> shown;
        int total, have;
        lock (Lock)
        {
            total = _catalogTotal;
            have = Catalog.Count;
            var key = (_search, _category, _noEquipment, have);
            if (key != _shownFor)
            {
                var cat = Categories[Math.Clamp(_category, 0, Categories.Length - 1)].Category;
                _shown = Catalog.Where(i => (cat is null || i.Category == cat) &&
                                            (!_noEquipment || !CreativeCatalog.IsEquipment(i.Category, i.IsGear)) &&
                                            CreativeCatalog.Matches(_search, i.Label, i.Name)).ToList();
                _shownFor = key;
                if (_selected >= 0 && !_shown.Any(i => i.Id == (uint)_selected)) _selected = -1;
            }
            shown = _shown;
        }
        if (total < 0) ui.TextDisabled("Loading the catalogue...");
        else if (have < total) ui.Progress(total == 0 ? 1 : (float)have / total, $"{have} of {total} items");
        else ui.TextDisabled($"{shown.Count} of {total} items");

        var h = 340 * Math.Max(ui.Scale, 0.5f);
        if (ui.BeginChild("list", 420 * ui.Scale, h))
        {
            foreach (var i in shown.Take(600))
                if (ui.Selectable($"{i.Label}##{i.Id}", i.Id == (uint)_selected))
                {
                    _selected = (int)i.Id;
                    _count = 1;
                    _level = Math.Max(1, i.LevelMax);
                    _rarity = (int)i.Rarity;
                }
            if (shown.Count > 600) ui.TextDisabled($"... {shown.Count - 600} more: search to narrow it down");
        }
        ui.EndChild();
        ui.SameLine();
        if (ui.BeginChild("item", 0, h))
        {
            var sel = shown.FirstOrDefault(i => i.Id == (uint)_selected);
            if (sel.Id == 0) ui.TextWrapped(shown.Count == 0 && have > 0 ? "No items match." : "Pick an item.");
            else DrawItem(ui, sel, canGive);
        }
        ui.EndChild();
    }

    private static void DrawItem(UiDraw ui, CreativeItem i, bool canGive)
    {
        ui.Text(i.Label);
        ui.TextDisabled($"{i.Name}  0x{i.Id:x8}");
        ui.Text($"{i.Category}, stacks to {Math.Max(1, i.MaxStack)}");
        var max = Math.Max(1, i.MaxStack);
        if (i.IsGear)
        {
            int lo = Math.Max(1, i.LevelMin), hi = Math.Max(lo, i.LevelMax);
            if (hi > lo) ui.Slider("Level", ref _level, lo, hi);
            else ui.Text($"Level {lo}");
            _level = Math.Clamp(_level, lo, hi);
            ui.Combo("Rarity", ref _rarity, RarityNames);
        }
        ui.Slider("Count", ref _count, 1, Math.Min(CreativeTools.MaxGive, Math.Max(max * 10, 10)));
        if (!canGive) return;
        ui.Spacing();
        if (ui.Button("Take 1")) Give(i, 1);
        ui.SameLine();
        if (max > 1 && ui.Button($"Take {max}")) Give(i, max);
        if (max > 1) ui.SameLine();
        if (ui.Button($"Take {_count}")) Give(i, _count);
    }

    private static void Give(CreativeItem i, int count)
    {
        lock (Lock) _reply = "";
        CreativeNet.Ask(new CreativeRequest(CreativeAsk.Give, i.Id, count, i.IsGear ? _level : -1, i.IsGear ? _rarity : -1));
    }

    private static void DrawWorld(UiDraw ui, CreativeStateMsg s)
    {
        var canTime = (s.Access & CreativeAccess.Time) != 0;
        var canCycle = (s.Access & CreativeAccess.Cycle) != 0;
        ui.Separator("Time of day");
        ui.Text(s.Hours >= 0 ? $"It is {Time.Format(s.Hours)}." : "The time of day is not known.");
        if (!canTime)
        {
            ui.TextWrapped((s.Access & CreativeAccess.Admin) != 0 ? "This build cannot set the time of day." : "Only an admin changes the time of day.");
        }
        else
        {
            ui.TextDisabled("Jumps forward to the hour you pick.");
            for (var hh = 0; hh < 24; hh += 3)
            {
                if (hh > 0) ui.SameLine();
                if (ui.Button($"{hh:D2}:00")) CreativeNet.Ask(new CreativeRequest(CreativeAsk.Time, Value: hh));
            }
            ui.Slider("Hour", ref _hours, 0, 23.99f);
            ui.SameLine();
            if (ui.Button("Set")) CreativeNet.Ask(new CreativeRequest(CreativeAsk.Time, Value: _hours));
        }
        ui.Separator("Day and night cycle");
        ui.Text("Now: " + s.Cycle switch
        {
            TimeCycle.Stopped => "stopped (the sun stands still)",
            TimeCycle.Fast => "fast (a day and night in about 90 s)",
            _ => "normal",
        });
        if (!canCycle)
        {
            ui.TextWrapped((s.Access & CreativeAccess.Admin) != 0 ? "This build cannot change the cycle's speed." : "Only an admin changes the cycle.");
            return;
        }
        if (ui.Button("Normal")) CreativeNet.Ask(new CreativeRequest(CreativeAsk.Cycle, Count: (int)TimeCycle.Normal));
        ui.SameLine();
        if (ui.Button("Stopped")) CreativeNet.Ask(new CreativeRequest(CreativeAsk.Cycle, Count: (int)TimeCycle.Stopped));
        ui.SameLine();
        if (ui.Button("Fast")) CreativeNet.Ask(new CreativeRequest(CreativeAsk.Cycle, Count: (int)TimeCycle.Fast));
        ui.TextDisabled("For the whole world, until it is set back or the server restarts.");
    }
}
