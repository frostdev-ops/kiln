// SPDX-License-Identifier: MIT
// Mods' entries in the game's own menus (Ui.Menus.Add, a native plugin's menu_add) and where a player reaches them.
// The game's menus do not scroll: every entry Kiln draws before the Settings entry pushes the game's own entries down,
// and with enough of them Quit left a 1080p screen (a player's report, 2026-11-13). So Kiln's setting "modEntries"
// (kiln/config/menus-client.json, shown under "Kiln settings" on the Mods page) says when kiln.ui draws them: Few (the
// default: while a menu has at most 3), Always, Never. Kiln's own Mods entry is always drawn, and the Mods page lists
// every mod's entry ("Menu entries"), each doing what its row in the menu does (kiln.ui v6: menu_shown, menu_entries,
// menu_activate; native/services/ui/menu_rows.h).
namespace Kiln;

/// <summary>When the game's menus draw the entries mods added (Kiln's setting <c>modEntries</c>).</summary>
internal enum ModMenuEntries
{
    /// <summary>While a menu has at most 3 of them, else none (they stay on the Mods page).</summary>
    Few = 0,
    /// <summary>Every entry, however many (a menu may then run off the screen).</summary>
    Always = 1,
    /// <summary>None: the Mods page's "Menu entries" list only.</summary>
    Never = 2,
}

/// <summary>Kiln's config "menus" (client): kiln/config/menus-client.json.</summary>
[Comment("Kiln's entries in the game's own menus (the main menu and the pause menu).")]
internal sealed class MenuSettings
{
    [Comment("When the game's menus show the entries mods add to them: Few (only while a menu has at most 3 of them; " +
             "more would push the game's own entries, such as Quit, off the screen), Always, or Never. Kiln's own Mods " +
             "entry is always there, and every mod's entry is also on Kiln's Mods page under \"Menu entries\".")]
    public ModMenuEntries ModEntries { get; set; } = ModMenuEntries.Few;
}

/// <summary>One mod's entry in a game menu, as the Mods page lists it.</summary>
internal sealed record GameMenuRow(ulong Handle, MenuLocation Menu, string Text, string? Mod, bool Drawn, bool Hidden);

internal static unsafe class GameMenuEntries
{
    /// <summary>The config's name (file menus-client.json).</summary>
    internal const string ConfigName = "menus";

    private static int _started;
    private static ModConfig<MenuSettings>? _config;

    /// <summary>The mode kiln.ui is told (KILN_UI_MENU_SHOW_*).</summary>
    internal static uint NativeMode(ModMenuEntries m) => m switch
    {
        ModMenuEntries.Always => 1u,
        ModMenuEntries.Never => 2u,
        _ => 0u,
    };

    /// <summary>The setting in use (Few until the config is registered).</summary>
    internal static ModMenuEntries Mode => _config?.Value.ModEntries ?? ModMenuEntries.Few;

    /// <summary>Registers Kiln's config "menus" and hands its value to kiln.ui (a game client with kiln.ui v6; once).
    /// Tick thread now, else at the next service tick.</summary>
    internal static void Start()
    {
        if (UiNative.T6 == null || UiNative.T6->MenuShown == null) return;
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        // kiln.ui draws every entry until told otherwise (an older Kiln.Ui has no list to reach hidden ones); this one
        // lists them, so the default (Few) applies from now, and the saved setting once the config is registered
        Apply();
        if (Game.IsTickThread) Register();
        else
        {
            IDisposable? once = null;
            once = ServiceTick.Add("kiln menu entries", (in TickInfo _) =>
            {
                once?.Dispose();
                Register();
            });
        }
    }

    private static void Register()
    {
        if (_config is not null) return;
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = null;  // Kiln's own config, not the mod whose tick happened to start it
        try
        {
            _config = Config.Find(ConfigName + "-client") as ModConfig<MenuSettings> ??
                      Config.Register<MenuSettings>(ConfigKind.Client, ConfigName);
            _config.Changed += _ => Apply();
            Apply();
        }
        catch (Exception e)
        {
            UiEngine.Log(LogLevel.Warn, $"menus-client.json unavailable ({e.Message}): mods' menu entries follow the default (few)");
        }
        finally
        {
            ModContext.CurrentMod = outer;
        }
    }

    private static void Apply()
    {
        var t = UiNative.T6;
        if (t != null && t->MenuShown != null) t->MenuShown(NativeMode(Mode));
    }

    /// <summary>Every mod's entry in the game's menus, in drawing order (empty without kiln.ui v6). Any thread.</summary>
    internal static IReadOnlyList<GameMenuRow> List()
    {
        var t = UiNative.T6;
        if (t == null || t->MenuEntries == null) return [];
        var n = t->MenuEntries(null, 0);
        if (n == 0) return [];
        var buf = new UiNative.MenuEntryInfo[n + 8];  // a few more: an entry may be added meanwhile
        uint got;
        fixed (UiNative.MenuEntryInfo* p = buf)
        {
            p[0].Size = (uint)sizeof(UiNative.MenuEntryInfo);
            got = Math.Min(t->MenuEntries(p, (uint)buf.Length), (uint)buf.Length);
        }
        var rows = new GameMenuRow[got];
        for (var i = 0; i < got; i++) rows[i] = Row(buf[i], Ui.Menus.OwnerName(buf[i].Entry));
        return rows;
    }

    /// <summary>A row from kiln.ui's record and the owning mod's name (pure: tests).</summary>
    internal static GameMenuRow Row(in UiNative.MenuEntryInfo e, string? mod)
    {
        var text = e.Text == null || e.TextLength == 0 ? "" : System.Text.Encoding.UTF8.GetString(e.Text, (int)e.TextLength);
        return new GameMenuRow(e.Entry, (MenuLocation)e.Menu, text, mod, e.Drawn != 0, (e.Flags & 1u) != 0);
    }

    /// <summary>The rows the Mods page lists: the entries of the menu the player can open where they are (the main menu,
    /// or the pause menu in a world) that their mod has not hidden (pure: tests).</summary>
    internal static IReadOnlyList<GameMenuRow> Listed(IReadOnlyList<GameMenuRow> all, MenuLocation where) =>
        all.Where(r => !r.Hidden && r.Menu == where).ToArray();

    /// <summary>The menu the player can open now: the pause menu in a world, else the main menu.</summary>
    internal static MenuLocation Here()
    {
        try { return Ui.Screens.Current.InGame ? MenuLocation.PauseMenu : MenuLocation.MainMenu; }
        catch (Exception) { return MenuLocation.MainMenu; }
    }

    /// <summary>What the Mods page says above the list (pure: tests).</summary>
    internal static string Summary(ModMenuEntries mode, IReadOnlyList<GameMenuRow> rows)
    {
        var notDrawn = rows.Count(r => !r.Drawn);
        var how = mode switch
        {
            ModMenuEntries.Always => "The game's menus show every one of them (Kiln settings, modEntries: Always).",
            ModMenuEntries.Never => "The game's menus show none of them (Kiln settings, modEntries: Never).",
            _ => "A game menu shows them while it has at most 3, so its own entries (Quit) stay on the screen " +
                 "(Kiln settings, modEntries: Few).",
        };
        return notDrawn == 0 ? how : $"{how} {notDrawn} of them {(notDrawn == 1 ? "is" : "are")} not in the game's menu now; choose {(notDrawn == 1 ? "it" : "them")} here.";
    }

    /// <summary>Does what choosing the entry in its menu does: its action runs after the next UI frame. False when kiln.ui
    /// refuses it (gone, hidden by its mod, or an entry of the other menu).</summary>
    internal static bool Activate(ulong handle)
    {
        var t = UiNative.T6;
        return t != null && t->MenuActivate != null && t->MenuActivate(handle) == UiNative.Ok;
    }
}
