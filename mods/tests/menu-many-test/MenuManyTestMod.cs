// SPDX-License-Identifier: MIT
using Kiln;

namespace MenuManyTest;

public sealed class Settings
{
    [Comment("How many entries this mod adds to the pause menu. More than 3 makes the menu scroll (or, where it can't, moves the entries to Kiln's Mods page).")]
    [Range(0, 24)]
    public int Entries { get; set; } = 8;
}

/// <summary>
/// Fills the pause menu for the guided checks <c>menu.rows-quit</c> and <c>menu.scroll</c>: one mod instead of four,
/// and a number the player can change without restarting. Every entry says in a toast and in kiln.log that it was
/// chosen, so the check can tell that an entry reached from the scrolled menu or from the Mods page runs its handler.
/// </summary>
public sealed class MenuManyTestMod : Mod
{
    private ModConfig<Settings> _config = null!;
    private readonly List<MenuEntry> _entries = new();

    public override void OnLoad()
    {
        if (!Ui.Available || !Ui.Menus.Available)
        {
            Log.Info("menu-many-test: no game menus here (dedicated server or an unsupported build): nothing to do");
            return;
        }
        _config = Config.Register<Settings>();
        _config.Changed += _ => Fill();
        Fill();
    }

    public override void OnUnload()
    {
        foreach (var e in _entries) e.Dispose();
        _entries.Clear();
    }

    private void Fill()
    {
        var want = Math.Clamp(_config.Value.Entries, 0, 24);
        while (_entries.Count > want)
        {
            _entries[^1].Dispose();
            _entries.RemoveAt(_entries.Count - 1);
        }
        for (var i = _entries.Count; i < want; i++)
        {
            var n = i + 1;
            // below the game's own entries of the same order and before Kiln's Mods entry (order 1000)
            var e = Ui.Menus.Add(MenuLocation.PauseMenu, $"Test entry {n}", () => Chosen(n), order: 100 + n);
            if (e == null)
            {
                Log.Warn($"menu-many-test: entry {n} was not added; the pause menu has {_entries.Count} of this mod's entries");
                return;
            }
            _entries.Add(e);
        }
        Log.Info($"menu-many-test: the pause menu has {_entries.Count} of this mod's entries");
    }

    private void Chosen(int n)
    {
        Log.Info($"menu-many-test: entry {n} chosen");
        Hud.Toast($"Test entry {n} chosen", ToastKind.Success);
    }
}
