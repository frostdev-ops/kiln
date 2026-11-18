// SPDX-License-Identifier: MIT
// The creative menu (game client): a Kiln screen on F7 and in the pause menu. Items from the server's catalogue, the
// time of day, the day and night cycle, creative mode on or off. Every action is a request the server checks
// (CreativeServer); the menu only shows what the server answers.
namespace Kiln;

internal static class CreativeMenu
{
    public const string KeyId = "kiln.creative";
    public const string DefaultKey = "F7";
    public const string FlightKeyId = "kiln.creative.flight";
    private static readonly string[] FlightKeys = ["creative.flight.off", "creative.flight.unlimited", "creative.flight.building"];
    private static int _flightPick = (int)CharacterFlightMode.Unlimited;  // what the key turns on

    private static readonly object Lock = new();
    private static UiScreen? _screen;
    private static MenuEntry? _entry;
    private static HudLine? _hud;
    private static KeyBinding? _openKey, _flightKey;
    private static bool _started;

    // what the server said (any thread writes, the render thread reads)
    private static CreativeStateMsg? _state;
    private static string _reply = "";
    private static bool _replyOk = true;
    private static readonly List<CreativeItem> Catalog = new();
    private static int _catalogTotal = -1;
    private static HashSet<uint>? _found;      // journey mode: the items the player has found (null: not asked or unknown)
    private static IReadOnlyList<CreativeSlot> _backpack = [];  // the backpack grid (empty: not asked yet)
    private static uint _held;                 // the item picked up for the backpack grid (0: none)
    private static DateTime _askedBackpack;
    private static bool _foundUnavailable;
    private static DateTime _askedState, _askedCatalog;

    // the menu's own inputs (render thread)
    private static string _search = "";
    private static int _category;
    private static bool _noEquipment, _journey;
    private static uint _selected;
    private static int _page;
    private static int _catalogVersion, _foundVersion, _connectionVersion;
    private static int _seenConnection = -1, _refreshRequested;
    private static int _count = 1, _level = 1, _rarity, _upgrades;
    private static bool _gemSlot;
    private static float _hours = 12;
    private static List<CreativeItem> _shown = new();
    private static (string, int, bool, int, bool, int) _shownFor = ("", -1, false, -1, false, -1);

    // the combo lists by text key; their texts are made again when Kiln's language changes (Names, render thread)
    private static readonly (string Key, ItemCategory? Category)[] Categories =
    [
        ("creative.category.all", null), ("creative.category.materials", ItemCategory.Materials),
        ("creative.category.building", ItemCategory.BuildTools), ("creative.category.consumables", ItemCategory.Consumables),
        ("creative.category.ammunition", ItemCategory.Ammunition), ("creative.category.weapons", ItemCategory.Weapons),
        ("creative.category.equipment", ItemCategory.Equipment), ("creative.category.tools", ItemCategory.Tools),
        ("creative.category.instruments", ItemCategory.Instrument), ("creative.category.fishing", ItemCategory.FishingRod),
        ("creative.category.customization", ItemCategory.Customization), ("creative.category.gems", ItemCategory.WeaponGem),
        ("creative.category.palettes", ItemCategory.ColorPalette), ("creative.category.animal_food", ItemCategory.AnimalFood),
        ("creative.category.collectibles", ItemCategory.Collectibles), ("creative.category.blueprints", ItemCategory.Blueprints),
        ("creative.category.currency", ItemCategory.Currency),
    ];
    private static readonly string[] RarityKeys =
        ["creative.rarity.common", "creative.rarity.uncommon", "creative.rarity.rare", "creative.rarity.epic", "creative.rarity.legendary"];
    // CreativeMap.Choices, in its order
    private static readonly string[] MapKeys = ["creative.map.game", "creative.map.r200", "creative.map.r400", "creative.map.whole"];
    private static int _namesGeneration = int.MinValue;
    private static string[] _categoryNames = [], _rarityNames = [], _flightNames = [], _mapNames = [];

    // The combo lists in the language Kiln's UI shows (render thread; made again only when it changes).
    private static void Names()
    {
        var g = KilnText.Generation;
        if (g == _namesGeneration) return;
        _categoryNames = Categories.Select(c => KilnText.T(c.Key)).ToArray();
        _rarityNames = RarityKeys.Select(k => KilnText.T(k)).ToArray();
        _flightNames = FlightKeys.Select(k => KilnText.T(k)).ToArray();
        _mapNames = MapKeys.Length == CreativeMap.Choices.Length ? MapKeys.Select(k => KilnText.T(k)).ToArray() : CreativeMap.Names;
        _namesGeneration = g;
    }

    // Kiln's language changed (KilnText.Changed, any thread): the screen's title, the pause-menu entry, the HUD line and the
    // Keys page's lines follow it.
    private static void Relabel()
    {
        try
        {
            if (_screen is { } sc) sc.Title = KilnText.T("creative.title");
            if (_entry is { } en) en.Text = KilnText.T("creative.menu");
            if (_openKey is { } k)
            {
                k.Description = KilnText.T("creative.key.open");
                k.Category = KilnText.T("creative.key.category");
            }
            if (_flightKey is { } f)
            {
                f.Description = KilnText.T("creative.key.flight");
                f.Category = KilnText.T("creative.key.category");
            }
            lock (Lock)
                if (_hud is { } h) h.Text = KilnText.T("creative.hud");
        }
        catch (Exception)
        {
            // no UI
        }
    }

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
        if (CreativeFlame.Running)
        {
            // Creative Mode's own menu serves this game: no Kiln key, screen or pause-menu entry to compete with it
            RulesRuntime.Log(LogLevel.Info, "creative menu: idle (Creative Mode runs in this game; its own menu applies)");
            return;
        }
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = null;  // Kiln's, not the mod whose tick happened to start it
        try
        {
            _screen = Ui.Screen(KilnText.T("creative.title"), Draw, new UiScreenOptions { Style = UiStyle.Player, Width = 820, Height = 600 });
            _screen.Opened += () => Interlocked.Exchange(ref _refreshRequested, 1);
            _openKey = KeyBindings.Register(KeyId, DefaultKey, () => _screen.Toggle(), KilnText.T("creative.key.open"), KilnText.T("creative.key.category"));
            _flightKey = KeyBindings.Register(FlightKeyId, "None", ToggleFlight, KilnText.T("creative.key.flight"), KilnText.T("creative.key.category"));
            _entry = Ui.Menus.Add(MenuLocation.PauseMenu, KilnText.T("creative.menu"), () => _screen.Open(), order: 900);
            KilnText.Changed += Relabel;
            lock (Lock) Apply(_state);
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

    public static void OnServerReady()
    {
        CreativeNet.Ask(new CreativeRequest(CreativeAsk.State));
        Interlocked.Exchange(ref _refreshRequested, 1);
    }

    public static void OnDisconnected()
    {
        lock (Lock)
        {
            _state = null;
            Catalog.Clear();
            _catalogTotal = -1;
            _found = null;
            _foundUnavailable = false;
            _backpack = [];
            _held = 0;
            _catalogVersion++;
            _foundVersion++;
            _connectionVersion++;
            _reply = "";
            Apply(null);
        }
        _screen?.Close();
    }

    public static void OnState(CreativeStateMsg m)
    {
        lock (Lock)
        {
            _state = m;
            Apply(m);
        }
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
        if (m.Start < 0 || m.Total < 0 || m.Total > 16384 || m.Items.Count > 150 || m.Start > m.Total - m.Items.Count) return;
        var next = -1;
        lock (Lock)
        {
            if (m.Start == 0) Catalog.Clear();
            if (m.Start != Catalog.Count) return;  // an old answer
            Catalog.AddRange(m.Items);
            _catalogTotal = m.Total;
            if (Catalog.Count < m.Total && m.Items.Count > 0) next = Catalog.Count;
            _catalogVersion++;
        }
        if (next >= 0) CreativeNet.Ask(new CreativeRequest(CreativeAsk.Catalog, Count: next));
    }

    public static void OnBackpack(CreativeBackpackMsg m)
    {
        lock (Lock) _backpack = m.Slots;
    }

    public static void OnFound(CreativeFoundMsg m)
    {
        lock (Lock)
        {
            _found = m.Available ? new HashSet<uint>(m.Items) : null;
            _foundUnavailable = !m.Available;
            _foundVersion++;
        }
    }

    // The pause-menu entry shows for those who may use the menu; a HUD line while creative mode is on.
    private static void Apply(CreativeStateMsg? m)
    {
        try
        {
            var access = m?.Access ?? CreativeAccess.None;
            if (_entry != null) _entry.Visible = (access & (CreativeAccess.Admin | CreativeAccess.On)) != 0;
            var on = (access & CreativeAccess.On) != 0;
            if (!on) CreativeMap.Reset();
            if (on && _hud is null)
            {
                var outer = ModContext.CurrentMod;
                ModContext.CurrentMod = null;
                try
                {
                    _hud = Hud.Show("kiln.creative", KilnText.T("creative.hud"), HudAnchor.TopRight, new Color(255, 196, 92));
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
        if (catalog && _journey) CreativeNet.Ask(new CreativeRequest(CreativeAsk.Found));
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
        if (CreativeFlame.Running) { ui.TextWrapped(CreativeFlame.Note + "."); return; }
        lock (Lock)
        {
            if (_seenConnection != _connectionVersion)
            {
                _seenConnection = _connectionVersion;
                _selected = 0;
                _shownFor = ("", -1, false, -1, false, -1);
                _askedCatalog = _askedState = default;
            }
        }
        if (Interlocked.Exchange(ref _refreshRequested, 0) != 0) Refresh(catalog: true);
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
                ? KilnText.T("creative.asking")
                : KilnText.T("creative.no_server"));
            return;
        }
        if ((s.Access & CreativeAccess.Idle) != 0)
        {
            ui.TextWrapped(KilnText.T("creative.idle"));
            return;
        }
        var on = (s.Access & CreativeAccess.On) != 0;
        var admin = (s.Access & CreativeAccess.Admin) != 0;
        ui.Text(on ? new Color(255, 196, 92) : new Color(200, 200, 200), on ? KilnText.T("creative.on") : KilnText.T("creative.off"));
        if (admin)
        {
            ui.SameLine();
            if (ui.SmallButton(on ? KilnText.T("creative.turn_off") : KilnText.T("creative.turn_on"))) CreativeNet.Ask(new CreativeRequest(CreativeAsk.Creative, Count: on ? 0 : 1));
        }
        else if (!on)
        {
            ui.SameLine();
            ui.TextDisabled(KilnText.T("creative.admin_turns_on"));
        }
        if (reply.Length > 0) ui.TextWrapped(ok ? reply : KilnText.T("creative.refused", ("reason", reply)));
        ui.Separator();
        if (ui.BeginTabBar("creative"))
        {
            if (ui.BeginTab(KilnText.T("creative.tab.items") + "###creative.items"))
            {
                DrawItems(ui, s);
                ui.EndTab();
            }
            if (ui.BeginTab(KilnText.T("creative.tab.world") + "###creative.world"))
            {
                DrawWorld(ui, s);
                ui.EndTab();
            }
            if (ui.BeginTab(KilnText.T("creative.tab.rules") + "###creative.rules"))
            {
                ui.TextWrapped(on || s.Rules.Length > 0 ? KilnText.T("creative.rules.yours", ("rules", s.Rules)) : KilnText.T("creative.rules.game"));
                ui.Spacing();
                ui.TextWrapped(s.Hooks);
                ui.Spacing();
                ui.TextDisabled(KilnText.T("creative.rules.key", ("key", KeyBindings.All.FirstOrDefault(b => b.Id == KeyId)?.Key.ToString() ?? DefaultKey)));
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
                ? KilnText.T("creative.items.cannot_give")
                : KilnText.T("creative.items.needs_creative"));
        Names();
        ui.InputText(KilnText.T("creative.items.search") + "##creative.search", ref _search, 64);
        ui.SameLine();
        ui.Checkbox(KilnText.T("creative.items.no_equipment") + "##creative.noequip", ref _noEquipment);
        ui.SameLine();
        if (ui.Checkbox(KilnText.T("creative.items.journey") + "##creative.journey", ref _journey) && _journey) CreativeNet.Ask(new CreativeRequest(CreativeAsk.Found));
        ui.Tooltip(KilnText.T("creative.items.journey_tip"));
        ui.Combo(KilnText.T("creative.items.category") + "##creative.category", ref _category, _categoryNames);
        List<CreativeItem> shown;
        int total, have;
        bool foundUnavailable;
        lock (Lock)
        {
            total = _catalogTotal;
            have = Catalog.Count;
            foundUnavailable = _foundUnavailable;
            var key = (_search, _category, _noEquipment, _catalogVersion, _journey, _foundVersion);
            if (key != _shownFor)
            {
                var cat = Categories[Math.Clamp(_category, 0, Categories.Length - 1)].Category;
                var words = CreativeCatalog.SearchWords(_search);
                _shown = Catalog.Where(i => (cat is null || i.Category == cat) &&
                                            (!_noEquipment || !CreativeCatalog.IsEquipment(i.Category, i.IsGear)) &&
                                            CreativeCatalog.FoundMatches(_journey, _found, i.Id) &&
                                            CreativeCatalog.MatchesWords(words, i.Label, i.Name)).ToList();
                _shownFor = key;
                _page = 0;
                if (!_shown.Any(i => i.Id == _selected)) _selected = 0;
            }
            shown = _shown;
        }
        if (_journey && foundUnavailable) ui.TextDisabled(KilnText.T("creative.items.found_unknown"));
        if (total < 0) ui.TextDisabled(KilnText.T("creative.items.loading"));
        else if (have < total) ui.Progress(total == 0 ? 1 : (float)have / total, KilnText.T("creative.items.of", ("count", have), ("total", total)));
        else ui.TextDisabled(KilnText.T("creative.items.of", ("count", shown.Count), ("total", total)));

        var h = 340 * Math.Max(ui.Scale, 0.5f);
        if (ui.BeginChild("list", 420 * ui.Scale, h))
        {
            foreach (var i in Page(shown, _page))
                if (ui.Selectable($"{i.Label}##{i.Id}", i.Id == _selected))
                {
                    _selected = i.Id;
                    _count = 1;
                    _level = Math.Max(1, i.LevelMax);
                    _rarity = (int)i.Rarity;
                }

        }
        ui.EndChild();
        ui.SameLine();
        if (ui.BeginChild("item", 0, h))
        {
            var sel = shown.FirstOrDefault(i => i.Id == _selected);
            if (sel.Id == 0) ui.TextWrapped(shown.Count == 0 && have > 0 ? (_journey ? KilnText.T("creative.items.none_found") : KilnText.T("creative.items.none_match")) : KilnText.T("creative.items.pick"));
            else DrawItem(ui, sel, canGive);
        }
        ui.EndChild();
        if (_page > 0 && ui.SmallButton(KilnText.T("creative.items.previous"))) _page--;
        ui.SameLine();
        ui.Text(KilnText.T("creative.items.page", ("page", _page + 1), ("pages", Math.Max(1, (shown.Count + PageSize - 1) / PageSize))));
        ui.SameLine();
        if ((_page + 1) * PageSize < shown.Count && ui.SmallButton(KilnText.T("creative.items.next"))) _page++;
        if (canGive) DrawBackpack(ui);
    }

    // The backpack grid: pick an item up in the catalogue ("Place..."), then click a backpack slot to put it there.
    private static void DrawBackpack(UiDraw ui)
    {
        if (!ui.CollapsingHeader(KilnText.T("creative.backpack.caption") + "###creative.backpack")) return;
        if (DateTime.UtcNow - _askedBackpack > TimeSpan.FromSeconds(2))
        {
            _askedBackpack = DateTime.UtcNow;
            CreativeNet.Ask(new CreativeRequest(CreativeAsk.Backpack));
        }
        IReadOnlyList<CreativeSlot> slots;
        lock (Lock) slots = _backpack;
        var held = _held;
        CreativeItem heldItem = default;
        if (held != 0)
        {
            lock (Lock) heldItem = Catalog.FirstOrDefault(i => i.Id == held);
            ui.Text(new Color(255, 196, 92), KilnText.T("creative.backpack.holding", ("item", heldItem.Label),
                ("count", Math.Min(_count, heldItem.IsGear ? 1 : Math.Max(1, heldItem.MaxStack)))));
            ui.SameLine();
            if (ui.SmallButton(KilnText.T("creative.backpack.put_back"))) _held = 0;
        }
        else ui.TextDisabled(KilnText.T("creative.backpack.hint"));
        if (slots.Count == 0)
        {
            ui.TextDisabled(KilnText.T("creative.backpack.reading"));
            return;
        }
        for (var k = 0; k < slots.Count; k++)
        {
            var sl = slots[k];
            if (k % 8 != 0) ui.SameLine();
            string label;
            string? name = null;
            if (sl.Item == 0) label = "   .   ";
            else
            {
                CreativeItem it;
                lock (Lock) it = Catalog.FirstOrDefault(i => i.Id == sl.Item);
                name = it.Id != 0 ? it.Label : $"0x{sl.Item:x8}";
                label = (name.Length > 6 ? name[..6] : name.PadRight(6)) + (sl.Count > 1 ? $" {sl.Count}" : "");
            }
            if (ui.Button($"{label}##slot{k}", 74 * Math.Max(ui.Scale, 0.5f), 0) && held != 0 && (sl.Item == 0 || sl.Item == held))
            {
                Give(heldItem, Math.Min(_count, heldItem.IsGear ? 1 : Math.Max(1, heldItem.MaxStack)), sl.Slot);
                _held = 0;
            }
            if (name is not null) ui.Tooltip(sl.Count > 1 ? $"{name} x{sl.Count}" : name);
        }
    }

    internal const int PageSize = 100;
    internal static IEnumerable<CreativeItem> Page(IReadOnlyList<CreativeItem> items, int page) =>
        items.Skip(Math.Max(0, page) * PageSize).Take(PageSize);

    private static void DrawItem(UiDraw ui, CreativeItem i, bool canGive)
    {
        ui.Text(i.Label);
        ui.TextDisabled($"{i.Name}  0x{i.Id:x8}");
        ui.Text(KilnText.T("creative.item.stacks", ("category", i.Category), ("count", Math.Max(1, i.MaxStack))));
        var max = i.IsGear ? 1 : Math.Clamp(i.MaxStack, 1, CreativeTools.MaxGive);
        if (i.IsGear)
        {
            int lo = Math.Max(1, i.LevelMin), hi = Math.Max(lo, i.LevelMax);
            if (hi > lo) ui.Slider(KilnText.T("creative.item.level") + "##creative.level", ref _level, lo, hi);
            else ui.Text(KilnText.T("creative.item.level_fixed", ("level", lo)));
            _level = Math.Clamp(_level, lo, hi);
            ui.Combo(KilnText.T("creative.item.rarity") + "##creative.rarity", ref _rarity, _rarityNames);
            ui.Slider(KilnText.T("creative.item.upgrades") + "##creative.upgrades", ref _upgrades, 0, 5);
            ui.Tooltip(KilnText.T("creative.item.upgrades_tip"));
            if (i.Category == ItemCategory.Weapons) ui.Checkbox(KilnText.T("creative.item.gem_slot") + "##creative.gemslot", ref _gemSlot);
            if (_journey) ui.TextDisabled(KilnText.T("creative.item.journey_level"));
        }
        ui.Slider(KilnText.T("creative.item.count") + "##creative.count", ref _count, 1, Math.Min(CreativeTools.MaxGive, max));
        if (!canGive) return;
        ui.Spacing();
        if (ui.Button(KilnText.T("creative.item.take", ("count", 1)))) Give(i, 1);
        ui.SameLine();
        if (max > 1 && ui.Button(KilnText.T("creative.item.take", ("count", max)))) Give(i, max);
        if (max > 1) ui.SameLine();
        if (ui.Button(KilnText.T("creative.item.take", ("count", _count)))) Give(i, _count);
        ui.SameLine();
        if (ui.Button(KilnText.T("creative.item.place"))) _held = i.Id;
        ui.Tooltip(KilnText.T("creative.item.place_tip"));
    }

    private static void Give(CreativeItem i, int count, ulong slot = 0)
    {
        if (i.Id == 0) return;
        lock (Lock) _reply = "";
        var options = i.IsGear ? CreativeGiveOptions.Of(_upgrades, _gemSlot && i.Category == ItemCategory.Weapons, _journey) : 0;
        CreativeNet.Ask(new CreativeRequest(CreativeAsk.Give, i.Id, count, i.IsGear ? _level : -1, i.IsGear ? _rarity : -1, Options: options,
            Slot: slot));
    }

    private static string? _mapReply;

    private static void DrawMap(UiDraw ui, bool on)
    {
        ui.Separator(KilnText.T("creative.map.caption"));
        if (!on)
        {
            ui.TextDisabled(KilnText.T("creative.needs_creative"));
            return;
        }
        if (!CreativeMap.Available)
        {
            ui.TextDisabled(KilnText.T("creative.map.unavailable"));
            return;
        }
        Names();
        var choice = CreativeMap.Choice;
        var pick = choice;
        ui.Combo(KilnText.T("creative.map.radius") + "##creative.map", ref pick, _mapNames);
        if (pick != choice) _mapReply = CreativeMap.Set(pick);
        ui.TextDisabled(KilnText.T("creative.map.help"));
        if (_mapReply is { } r) ui.TextWrapped(KilnText.T("creative.refused", ("reason", r)));
    }

    internal static CharacterFlightMode FlightOf(CreativeAccess a) =>
        (a & CreativeAccess.FlightUnlimited) != 0 ? CharacterFlightMode.Unlimited
        : (a & CreativeAccess.FlightBuildingArea) != 0 ? CharacterFlightMode.BuildingArea : CharacterFlightMode.Off;

    // The key: off when flying, else the mode last picked in the menu.
    private static void ToggleFlight()
    {
        if (CreativeFlame.Running) return;
        CreativeStateMsg? st;
        lock (Lock) st = _state;
        if (st is not { } s || (s.Access & CreativeAccess.On) == 0) return;
        var now = FlightOf(s.Access);
        var next = now != CharacterFlightMode.Off ? CharacterFlightMode.Off : (CharacterFlightMode)Math.Clamp(_flightPick, 1, 2);
        CreativeNet.Ask(new CreativeRequest(CreativeAsk.Flight, Count: (int)next));
    }

    private static void DrawFlight(UiDraw ui, CreativeStateMsg s)
    {
        ui.Separator(KilnText.T("creative.flight.caption"));
        if ((s.Access & CreativeAccess.On) == 0)
        {
            ui.TextDisabled(KilnText.T("creative.needs_creative"));
            return;
        }
        var now = (int)FlightOf(s.Access);
        if ((s.Access & CreativeAccess.FlightAllowed) == 0 && now == 0)
        {
            ui.TextDisabled(KilnText.T("creative.flight.unavailable"));
            return;
        }
        Names();
        var pick = now;
        ui.Combo(KilnText.T("creative.flight.flight") + "##creative.flight", ref pick, _flightNames);
        if (pick != now)
        {
            if (pick != 0) _flightPick = pick;
            CreativeNet.Ask(new CreativeRequest(CreativeAsk.Flight, Count: pick));
        }
        ui.TextDisabled(KilnText.T("creative.flight.help", ("key", KeyBindings.All.FirstOrDefault(b => b.Id == FlightKeyId)?.Key.ToString() ?? "none")));
    }

    private static void DrawWorld(UiDraw ui, CreativeStateMsg s)
    {
        var canTime = (s.Access & CreativeAccess.Time) != 0;
        var canCycle = (s.Access & CreativeAccess.Cycle) != 0;
        DrawMap(ui, (s.Access & CreativeAccess.On) != 0);
        DrawFlight(ui, s);
        ui.Separator(KilnText.T("creative.time.caption"));
        ui.Text(s.Hours >= 0 ? KilnText.T("creative.time.now", ("time", Time.Format(s.Hours))) : KilnText.T("creative.time.unknown"));
        if (!canTime)
        {
            ui.TextWrapped((s.Access & CreativeAccess.Admin) != 0 ? KilnText.T("creative.time.cannot") : KilnText.T("creative.time.admin_only"));
        }
        else
        {
            ui.TextDisabled(KilnText.T("creative.time.help"));
            for (var hh = 0; hh < 24; hh += 3)
            {
                if (hh > 0) ui.SameLine();
                if (ui.Button($"{hh:D2}:00")) CreativeNet.Ask(new CreativeRequest(CreativeAsk.Time, Value: hh));
            }
            ui.Slider(KilnText.T("creative.time.hour") + "##creative.hour", ref _hours, 0, 23.99f);
            ui.SameLine();
            if (ui.Button(KilnText.T("creative.time.set"))) CreativeNet.Ask(new CreativeRequest(CreativeAsk.Time, Value: _hours));
        }
        ui.Separator(KilnText.T("creative.cycle.caption"));
        ui.Text(s.Cycle switch
        {
            TimeCycle.Stopped => KilnText.T("creative.cycle.now_stopped"),
            TimeCycle.Fast => KilnText.T("creative.cycle.now_fast"),
            _ => KilnText.T("creative.cycle.now_normal"),
        });
        if (!canCycle)
        {
            ui.TextWrapped((s.Access & CreativeAccess.Admin) != 0 ? KilnText.T("creative.cycle.cannot") : KilnText.T("creative.cycle.admin_only"));
            return;
        }
        if (ui.Button(KilnText.T("creative.cycle.normal"))) CreativeNet.Ask(new CreativeRequest(CreativeAsk.Cycle, Count: (int)TimeCycle.Normal));
        ui.SameLine();
        if (ui.Button(KilnText.T("creative.cycle.stopped"))) CreativeNet.Ask(new CreativeRequest(CreativeAsk.Cycle, Count: (int)TimeCycle.Stopped));
        ui.SameLine();
        if (ui.Button(KilnText.T("creative.cycle.fast"))) CreativeNet.Ask(new CreativeRequest(CreativeAsk.Cycle, Count: (int)TimeCycle.Fast));
        ui.TextDisabled(KilnText.T("creative.cycle.help"));
    }
}
