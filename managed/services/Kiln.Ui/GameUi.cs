// SPDX-License-Identifier: MIT
// The game's own UI (Forge: ScreenEvent / GuiScreenEvent.InitGuiEvent to add buttons, the current Screen):
// which game screen is open (Ui.Screens), entries in the game's main menu and pause menu (Ui.Menus), and texts in the
// game's language (UiText). Native side: kiln.ui's game.cpp (docs/re/game-ui.md). Usage: docs/kiln/ui.md.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>A text in several languages: Kiln shows the one for the game's text language (exact key like "pt-br", then
/// the primary language "pt", then "en", then "*"). A plain string converts to a text for every language.</summary>
public sealed class UiText
{
    private readonly Dictionary<string, string> _texts;

    public UiText(string text) => _texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["*"] = text ?? "" };

    public UiText(IReadOnlyDictionary<string, string> texts)
    {
        _texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in texts) _texts[Key(k)] = v ?? "";
        if (_texts.Count == 0) _texts["*"] = "";
    }

    /// <summary><c>UiText.Of(("en", "Mods"), ("de", "Mods"), ("fr", "Mods"))</c>.</summary>
    public static UiText Of(params (string Language, string Text)[] texts) =>
        new(texts.ToDictionary(t => t.Language, t => t.Text, StringComparer.OrdinalIgnoreCase));

    public static implicit operator UiText(string text) => new(text);

    /// <summary>The languages given ("en", "de", "*", ...).</summary>
    public IReadOnlyCollection<string> Languages => _texts.Keys;

    /// <summary>The text for a game language key ("de-de", "en-us"; see <see cref="GameLanguage"/>).</summary>
    public string For(string languageKey)
    {
        var k = Key(languageKey);
        if (k.Length > 0)
        {
            if (_texts.TryGetValue(k, out var t)) return t;
            var dash = k.IndexOf('-');
            if (dash > 0 && _texts.TryGetValue(k[..dash], out t)) return t;
        }
        if (_texts.TryGetValue("en", out var en)) return en;
        if (_texts.TryGetValue("en-us", out en)) return en;
        if (_texts.TryGetValue("*", out var all)) return all;
        return _texts.Values.First();
    }

    /// <summary>The text in the game's current language.</summary>
    public override string ToString() => For(GameLanguage.Current);

    /// <summary>As JSON {"en": "...", ...} (what kiln.craftmenu takes).</summary>
    internal string ToJson() => System.Text.Json.JsonSerializer.Serialize(_texts);

    private static string Key(string k) => (k ?? "").Trim().Replace('_', '-').ToLowerInvariant();
}

/// <summary>The game's text language (keen::LanguageId, from the game's reflection; docs/re/content.md 8.1).</summary>
public static class GameLanguage
{
    private static readonly string[] Keys =
    [
        "", "en-us", "en-gb", "de-de", "nl-nl", "fr-fr", "it-it", "es-es", "es-mx", "ru-ru", "pl-pl", "ja-jp", "id-id",
        "ko-kr", "ms-my", "pt-pt", "pt-br", "th-th", "tr-tr", "vi-vn", "zh-cn", "zh-tw", "fr-ca", "es-419", "bg-bg",
        "cs-cz", "da-dk", "fi-fi", "el-gr", "hu-hu", "no-no", "nb-no", "ro-ro", "sv-se", "uk-ua",
    ];

    /// <summary>"de-de", "en-us", ...; "en-us" when unknown (dedicated server, the game's source strings, an older build).</summary>
    public static string Current => KeyOf(Ui.Screens.Current.LanguageId);

    /// <summary>The key of a keen::LanguageId value.</summary>
    public static string KeyOf(uint languageId) => languageId > 0 && languageId < Keys.Length ? Keys[languageId] : "en-us";
}

/// <summary>keen::UiState: the game's top-level UI state.</summary>
public enum GameUiState : byte
{
    None = 0, Preload = 1, PreGame = 2, Load = 3, LoreIntro = 4, LoreOutro = 5, Credits = 6, Play = 7,
}

/// <summary>keen::HudScreenType (reflected enum of client 26404966): the screen open in game.</summary>
public enum HudScreen : byte
{
    Invalid = 0, InGame = 1, SystemMenu = 2, SettingsMenu = 3, InGameMenu = 4, Map = 5, Journal = 6, Character = 7,
    PlayerCrafting = 8, Backpack = 9, SkillTree = 10, SkillTreeRespec = 11, FlameAltar = 12, AltarUpgrade = 13,
    FlameUpgrade = 14, AltarDeactivate = 15, AltarReskill = 16, AltarItemPickup = 17, LoreReading = 18, Signpost = 19,
    LogicEditor = 20, ReadOnlyLootChest = 21, StorageChest = 22, Collection = 23, FeedingStation = 24,
    StationBasedCrafting = 25, NpcDialog = 26, NpcCrafting = 27, FactoryCrafting = 28, BlueprintMultiTool = 29,
    BlueprintMultiToolMaterialCatalog = 30, OvergrowthMultiTool = 31, DecayMultiTool = 32,
    ScrewdriverMultiToolBuildingMode = 33, ScrewdriverMultiToolGraphManager = 34, ScrewdriverMultiToolEntityConfig = 35,
    TerraformingSideMenu = 36, NpcSummonerMenu = 37, UserFeedback = 38, TwitchDrops = 39, RedeemCommerceProducts = 40,
    CommerceProductChest = 41, ItemTinting = 42, TextChat = 43, Social = 44, ShroudForge = 45,
    CharacterCustomization = 46, Rename = 47, TakeScreenshot = 48, InfoPopup = 49, NewCommerceUnlock = 50, NoUi = 51,
    TechGallery = 52, YcPlayground = 53,
}

/// <summary>What kind of screen the player looks at (a summary of <see cref="GameUiState"/> and <see cref="HudScreen"/>).</summary>
public enum ScreenKind
{
    /// <summary>No game UI yet (boot), or a dedicated server.</summary>
    None,
    /// <summary>Start-up logos and the title screen.</summary>
    Title,
    /// <summary>The main menu (landing page, character and world selection, the main menu's settings).</summary>
    MainMenu,
    Loading,
    /// <summary>The lore intro / outro.</summary>
    Cinematic,
    Credits,
    /// <summary>Playing: the HUD, no menu open.</summary>
    InGame,
    /// <summary>The pause (system) menu, opened with Escape.</summary>
    PauseMenu,
    Settings,
    /// <summary>The tabbed in-game menu (character, map, journal, crafting, backpack, skills) or the backpack.</summary>
    Inventory,
    Map,
    Journal,
    Character,
    /// <summary>A crafting menu: the player's own, a crafting station's, an NPC's or a factory's (see <see cref="GameScreen.Crafting"/>).</summary>
    Crafting,
    SkillTree,
    /// <summary>A storage or loot chest.</summary>
    Storage,
    /// <summary>NPC dialog.</summary>
    Dialog,
    Chat,
    /// <summary>Any other game screen (see <see cref="GameScreen.Hud"/>).</summary>
    Other,
}

/// <summary>A crafting menu that is open.</summary>
/// <param name="AtStation">A crafting station's menu (workbench, forge, an NPC's, ...); false for the player's own.</param>
/// <param name="Workshop">keen::WorkshopId of the menu (the station's kind, e.g. the Workbench 0x4da88f73; 0 = the
/// player's own crafting). Pass it to <c>Ui.Crafting.AddTab</c> / <c>AddSubcategory</c>.</param>
/// <param name="Recipe">The recipe id selected in the menu, 0 = none.</param>
/// <param name="Journal">The journal's recipe book.</param>
public readonly record struct CraftingScreen(bool AtStation, uint Workshop, uint Recipe, bool Journal);

/// <summary>The game screen of the last UI frame. <c>StationWorkshop</c>: the workshop id of the crafting station the
/// player uses (0 when none).</summary>
public readonly record struct GameScreen(GameUiState State, HudScreen Hud, ScreenKind Kind, uint StationWorkshop,
    CraftingScreen? Crafting, uint LanguageId, ulong Frame)
{
    public static readonly GameScreen None = new(GameUiState.None, HudScreen.Invalid, ScreenKind.None, 0, null, 0, 0);

    /// <summary>The game's text language key ("de-de", ...).</summary>
    public string Language => GameLanguage.KeyOf(LanguageId);

    public bool IsMainMenu => Kind == ScreenKind.MainMenu;
    public bool IsPaused => Kind == ScreenKind.PauseMenu;
    public bool InGame => State == GameUiState.Play;

    internal static ScreenKind KindOf(GameUiState s, HudScreen h, byte crafting) => s switch
    {
        GameUiState.None => ScreenKind.None,
        GameUiState.Preload => ScreenKind.Title,
        GameUiState.PreGame => ScreenKind.MainMenu,
        GameUiState.Load => ScreenKind.Loading,
        GameUiState.LoreIntro or GameUiState.LoreOutro => ScreenKind.Cinematic,
        GameUiState.Credits => ScreenKind.Credits,
        _ when crafting != 0 && h is HudScreen.InGameMenu or HudScreen.Journal => ScreenKind.Crafting,
        _ => h switch
        {
            HudScreen.InGame or HudScreen.NoUi or HudScreen.Invalid => ScreenKind.InGame,
            HudScreen.SystemMenu => ScreenKind.PauseMenu,
            HudScreen.SettingsMenu => ScreenKind.Settings,
            HudScreen.InGameMenu or HudScreen.Backpack => ScreenKind.Inventory,
            HudScreen.Map => ScreenKind.Map,
            HudScreen.Journal => ScreenKind.Journal,
            HudScreen.Character or HudScreen.CharacterCustomization => ScreenKind.Character,
            HudScreen.PlayerCrafting or HudScreen.StationBasedCrafting or HudScreen.NpcCrafting or HudScreen.FactoryCrafting =>
                ScreenKind.Crafting,
            HudScreen.SkillTree or HudScreen.SkillTreeRespec => ScreenKind.SkillTree,
            HudScreen.StorageChest or HudScreen.ReadOnlyLootChest or HudScreen.CommerceProductChest => ScreenKind.Storage,
            HudScreen.NpcDialog => ScreenKind.Dialog,
            HudScreen.TextChat => ScreenKind.Chat,
            _ => ScreenKind.Other,
        },
    };

    internal static GameScreen From(in UiNative.Screen s)
    {
        var state = (GameUiState)s.UiState;
        var hud = (HudScreen)s.HudScreen;
        CraftingScreen? c = s.Crafting != 0 ? new CraftingScreen(s.Station != 0, s.Workshop != 0 ? s.Workshop : s.Station, s.Recipe, s.Crafting == 2) : null;
        return new GameScreen(state, hud, KindOf(state, hud, s.Crafting), s.Interaction, c, s.Language, s.Frame);
    }
}

/// <summary>Posted on Kiln's event bus when the game screen changes (Forge: ScreenEvent.Opening / Closing). Tick thread,
/// at the next step after the UI frame that changed it (with the client's mods loaded in the main menu, the main menu's
/// UI tick also delivers it). Observes only: the game's own screen change has already happened. For the same change
/// without delay, on the game's UI thread, use <see cref="Ui.Screens.Changed"/>.</summary>
public sealed class ScreenChangedEvent(GameScreen previous, GameScreen current) : KilnEvent
{
    public GameScreen Previous { get; } = previous;
    public GameScreen Current { get; } = current;
    /// <summary>The kind opened (<see cref="Current"/>.Kind) when it differs from the previous one.</summary>
    public bool KindChanged => Previous.Kind != Current.Kind;
}

public static unsafe partial class Ui
{
/// <summary>The game's screens (client): <c>Ui.Screens.Current</c>, <c>Ui.Screens.Changed</c>.</summary>
[ClientOnly]
public static class Screens
{
    private static StrongBox<GameScreen> _current = new(GameScreen.None);  // replaced whole: readers never see a torn value
    private static ulong _screenHandle, _tickHandle;
    private static readonly OwnedList<Action<GameScreen, GameScreen>> Handlers = new();
    private static readonly OwnedList<Action<GameScreen>> TickHandlers = new();

    /// <summary>The screen kiln.ui reads from the game's UI (any thread). <see cref="GameScreen.None"/> when not available.</summary>
    [AnyThread]
    public static GameScreen Current
    {
        get
        {
            if (!UiNative.HasGameUi) return Volatile.Read(ref _current).Value;
            UiNative.Screen s = default;
            s.Size = (uint)sizeof(UiNative.Screen);
            return UiNative.T->GetScreen(&s) == UiNative.Ok ? GameScreen.From(s) : GameScreen.None;
        }
    }

    /// <summary>The game-UI integration runs on this build (feature ui.screens).</summary>
    public static bool Available
    {
        get
        {
            if (!UiNative.HasGameUi) return false;
            UiNative.Screen s = default;
            s.Size = (uint)sizeof(UiNative.Screen);
            return UiNative.T->GetScreen(&s) == UiNative.Ok;
        }
    }

    /// <summary>(previous, current) on the game's UI thread right after the UI frame in which the screen changed, in
    /// the main menu too. Runs as the subscribing mod; keep it short. Owned by the calling mod.</summary>
    public static event Action<GameScreen, GameScreen>? Changed
    {
        add
        {
            UiEngine.EnsureStarted();
            Handlers.Add(value);
        }
        remove => Handlers.Remove(value);
    }

    /// <summary>Every game UI frame on the game's UI thread (menus included). Owned by the calling mod.</summary>
    public static event Action<GameScreen>? UiTick
    {
        add
        {
            UiEngine.EnsureStarted();
            TickHandlers.Add(value);
            EnsureTick();
        }
        remove => TickHandlers.Remove(value);
    }

    internal static void Start()
    {
        if (!UiNative.HasGameUi || _screenHandle != 0) return;
        ulong h = 0;
        if (UiNative.T->SubscribeScreen(0, &OnScreen, null, &h) != UiNative.Ok) return;
        UiNative.Screen s = default;
        s.Size = (uint)sizeof(UiNative.Screen);
        if (UiNative.T->GetScreen(&s) == UiNative.Ok) Volatile.Write(ref _current, new StrongBox<GameScreen>(GameScreen.From(s)));
        Volatile.Write(ref _screenHandle, h);
        var c0 = _current.Value;
        UiEngine.Log(LogLevel.Info, $"game screens: {c0.Kind} ({c0.State}/{c0.Hud})");
    }

    private static void EnsureTick()
    {
        if (!UiNative.HasGameUi || _tickHandle != 0) return;
        ulong h = 0;
        if (UiNative.T->SubscribeUiTick(0, &OnTick, null, &h) == UiNative.Ok) _tickHandle = h;
    }

    internal static void RemoveOwnedBy(ModContext mod)
    {
        Handlers.RemoveOwnedBy(mod);
        TickHandlers.RemoveOwnedBy(mod);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnScreen(UiNative.Screen* now, UiNative.Screen* before, void* user)
    {
        try
        {
            var prev = Volatile.Read(ref _current).Value;
            var cur = GameScreen.From(*now);
            Volatile.Write(ref _current, new StrongBox<GameScreen>(cur));
            if (prev.LanguageId != cur.LanguageId) Menus.LanguageChanged();
            if (prev.Kind != cur.Kind) UiEngine.Log(LogLevel.Debug, $"game screen {prev.Kind} -> {cur.Kind} ({cur.State}/{cur.Hud})");
            Crafting.ScreenChanged(prev, cur);
            RefusalNotice.ScreenChanged(prev, cur);
            foreach (var (h, owner) in Handlers.Snapshot()) UiModCalls.Invoke(owner, x => h(x.prev, x.cur), (prev, cur), "Ui.Screens.Changed");
            if (Events.HasSubscribers<ScreenChangedEvent>()) Events.Enqueue(new ScreenChangedEvent(prev, cur));
        }
        catch (Exception e)
        {
            UiEngine.Log(LogLevel.Error, $"screen: {e}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnTick(UiNative.Screen* now, void* user)
    {
        try
        {
            var cur = GameScreen.From(*now);
            foreach (var (h, owner) in TickHandlers.Snapshot()) UiModCalls.Invoke(owner, h, cur, "Ui.Screens.UiTick");
        }
        catch (Exception e)
        {
            UiEngine.Log(LogLevel.Error, $"ui tick: {e}");
        }
    }
}
}

/// <summary>Where an entry goes.</summary>
public enum MenuLocation
{
    /// <summary>The main menu's entry list (next to Play, Settings, Quit).</summary>
    MainMenu = 1,
    /// <summary>The pause (system) menu opened with Escape in game.</summary>
    PauseMenu = 2,
}

/// <summary>An entry in one of the game's menus, drawn by the game's own entry renderer (its look and sounds, mouse,
/// keyboard and gamepad navigation), right above the menu's Settings entry.</summary>
public sealed class MenuEntry : IDisposable
{
    internal ulong Handle;
    private UiText _text;
    private bool _visible = true, _attention;

    internal MenuEntry(MenuLocation where, UiText text, Action onClick, int order, ModContext? owner)
    {
        Location = where;
        _text = text;
        OnClick = onClick;
        Order = order;
        Owner = owner;
    }

    public MenuLocation Location { get; }
    public int Order { get; }
    internal ModContext? Owner { get; }

    /// <summary>Runs on the game's UI thread after the frame in which the player chose the entry, as the owning mod.</summary>
    public Action OnClick { get; set; }

    /// <summary>The label (any thread; per language).</summary>
    [AnyThread]
    public UiText Text
    {
        get => _text;
        set
        {
            _text = value;
            Ui.Menus.Push(this);
        }
    }

    public bool Visible
    {
        get => _visible;
        set
        {
            _visible = value;
            Ui.Menus.Push(this);
        }
    }

    /// <summary>The game's attention marker next to the label (pause menu only).</summary>
    public bool Attention
    {
        get => _attention;
        set
        {
            _attention = value;
            Ui.Menus.Push(this);
        }
    }

    internal uint Flags => (_visible ? 0u : 1u) | (_attention ? 2u : 0u);

    /// <summary>Takes the entry out of the menu (also done when the owning mod unloads).</summary>
    public void Dispose() => Ui.Menus.Remove(this);
}

public static unsafe partial class Ui
{
/// <summary>Entries in the game's own menus (Forge: adding buttons in ScreenEvent.Init): <c>Ui.Menus.Add</c>.</summary>
[ClientOnly]
public static class Menus
{
    private static readonly object Lock = new();
    private static readonly Dictionary<ulong, MenuEntry> Entries = new();

    /// <summary>The game-menu entries work on this build (features ui.main_menu / ui.system_menu).</summary>
    public static bool Available => Screens.Available;

    /// <summary>Adds an entry to the main menu or the pause menu. Lower <paramref name="order"/> first; Kiln's own
    /// "Mods" entry has order 1000. Owned by the calling mod. Returns null when this menu is not available (dedicated
    /// server, unsupported build).</summary>
    public static MenuEntry? Add(MenuLocation where, UiText text, Action onClick, int order = 0)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(onClick);
        UiEngine.EnsureStarted();
        if (!UiNative.HasGameUi) return null;
        var e = new MenuEntry(where, text, onClick, order, UiModCalls.Caller);
        ulong h = 0;
        var label = text.ToString();
        var rc = UiNative.T->MenuAdd(0, (uint)where, order, Utf8.Of(label), &OnEntry, null, &h);
        if (rc != UiNative.Ok)
        {
            UiEngine.Log(LogLevel.Warn, $"menu entry '{label}' ({where}) not added: rc {rc}");
            return null;
        }
        e.Handle = h;
        lock (Lock) Entries[h] = e;
        UiModCalls.Watch(e.Owner);
        return e;
    }

    /// <summary>Adds the same entry to the main menu and the pause menu.</summary>
    public static (MenuEntry? Main, MenuEntry? Pause) AddToBoth(UiText text, Action onClick, int order = 0) =>
        (Add(MenuLocation.MainMenu, text, onClick, order), Add(MenuLocation.PauseMenu, text, onClick, order));

    /// <summary>Kiln's own "Mods" entry (both menus; opens the Kiln menu).</summary>
    public static bool KilnEntryVisible
    {
        set
        {
            if (UiNative.HasGameUi) UiNative.T->MenuSet(0, Utf8.Of(value ? "Mods" : "Mods"), value ? 0u : 1u);
        }
    }

    internal static void Push(MenuEntry e)
    {
        if (e.Handle == 0 || !UiNative.HasGameUi) return;
        UiNative.T->MenuSet(e.Handle, Utf8.Of(e.Text.ToString()), e.Flags);
    }

    internal static void Remove(MenuEntry e)
    {
        lock (Lock)
            if (!Entries.Remove(e.Handle)) return;
        if (UiNative.HasGameUi) UiNative.T->Unsubscribe(e.Handle);
        e.Handle = 0;
    }

    internal static void RemoveOwnedBy(ModContext mod)
    {
        MenuEntry[] mine;
        lock (Lock) mine = Entries.Values.Where(e => ReferenceEquals(e.Owner, mod)).ToArray();
        foreach (var e in mine) Remove(e);
    }

    internal static void LanguageChanged()
    {
        MenuEntry[] all;
        lock (Lock) all = Entries.Values.ToArray();
        foreach (var e in all) Push(e);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnEntry(ulong handle, void* user)
    {
        try
        {
            MenuEntry? e;
            lock (Lock) Entries.TryGetValue(handle, out e);
            if (e is null) return;
            UiModCalls.Invoke(e.Owner, static a => a(), e.OnClick, $"menu entry '{e.Text}'");
        }
        catch (Exception ex)
        {
            UiEngine.Log(LogLevel.Error, $"menu entry: {ex}");
        }
    }
}
}
