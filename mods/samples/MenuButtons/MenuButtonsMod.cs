// SPDX-License-Identifier: MIT
using Kiln;

namespace MenuButtons;

/// <summary>
/// The game-UI parts of Kiln.Ui (docs/kiln/ui.md "The game's own UI"):
///  - a "Kiln" entry in the game's main menu and pause menu, drawn by the game's own menu renderer (mouse, keyboard
///    and gamepad), that opens a custom screen in the game's look: a list, buttons, a slider, a text field, a
///    drop-down, an image (Kiln's mark) and the current game screen; F7 opens it too;
///  - an info panel next to every crafting menu with the station, the workshop and the selected recipe;
///  - a Kiln subcategory and a Kiln tab in the player's own crafting menu (workshop 0), listing a few of the game's
///    own hand-crafting recipes;
///  - every game screen change in the log.
/// </summary>
public sealed class MenuButtonsMod : Mod
{
    // Two recipes of the player's own crafting menu (keen::RecipeId, from its first tab on client 26404966). The menu
    // lists only the recipes the player has unlocked.
    private static readonly uint[] HandRecipes = [3647579549u, 600408661u, 1498713992u, 573557460u];

    private UiScreen? _screen;
    private UiImage? _mark;
    private int _selected, _clicks, _dropdown;
    private float _volume = 0.5f;
    private string _note = "";
    private readonly List<string> _log = new();
    private static readonly string[] Items = ["Embervale", "Revelwood", "Nomad Highlands", "Kindlewastes"];
    private static readonly string[] Choices = ["Game look", "Kiln look"];

    // The game has no HUD screen outside a world (main menu, loading): HudScreen.Invalid there, so leave it out.
    private static string HudPart(GameScreen s) => s.Hud == HudScreen.Invalid ? "" : $" / {s.Hud}";

    private static readonly UiText Label = UiText.Of(("en", "Kiln"), ("de", "Kiln"), ("fr", "Kiln"));
    private static readonly UiText ScreenTitle = UiText.Of(("en", "Kiln sample screen"), ("de", "Kiln-Beispielfenster"));

    public override void OnLoad()
    {
        if (!Ui.Available)
        {
            Log.Info("menubuttons: no overlay here (dedicated server or no kiln.ui): nothing to do");
            return;
        }
        _mark = Ui.LoadImage(Path.Combine(Context.Info.Directory, "kiln-mark.png"));
        _screen = Ui.Screen(ScreenTitle, Draw, new UiScreenOptions { Style = UiStyle.Game, Width = 660, Height = 560 });
        _screen.Opened += () => Log.Info("menubuttons: screen opened");
        _screen.Closed += () => Log.Info("menubuttons: screen closed");

        // the game's menus: right above Settings, before Kiln's own "Mods" entry (order 1000)
        var (main, pause) = Ui.Menus.AddToBoth(Label, () => _screen.Open(), order: 10);
        Log.Info($"menubuttons: main menu entry {(main != null ? "added" : "unavailable")}, pause menu entry {(pause != null ? "added" : "unavailable")}");
        KeyBindings.Register("menubuttons.screen", "F7", () => _screen.Toggle(), "Open the menu buttons sample screen");

        // screen changes (UI thread)
        Ui.Screens.Changed += (before, now) =>
        {
            var line = $"{before.Kind} -> {now.Kind} ({now.State}{HudPart(now)}{(now.Crafting is { } c ? $", {(c.AtStation ? "station" : "own crafting")}, workshop 0x{c.Workshop:x8}, recipe 0x{c.Recipe:x8}" : "")})";
            Log.Info($"menubuttons: screen {line}");
            lock (_log)
            {
                _log.Add(line);
                if (_log.Count > 8) _log.RemoveAt(0);
            }
        };

        // crafting menus: an info panel, a subcategory and a tab
        Ui.Crafting.AddPanel(UiText.Of(("en", "Kiln: recipe"), ("de", "Kiln: Rezept")), DrawCraftPanel);
        var sub = Ui.Crafting.AddSubcategory(workshop: 0, tab: -1, UiText.Of(("en", "Kiln picks"), ("de", "Kiln-Auswahl")), HandRecipes[..2]);
        var tab = Ui.Crafting.AddTab(workshop: 0, UiText.Of(("en", "Kiln"), ("de", "Kiln")), HandRecipes[2..], iconFromTab: 0);
        Log.Info($"menubuttons: crafting subcategory {(sub != null ? "added" : "unavailable")}, tab {(tab != null ? "added" : "unavailable")}; screens {(Ui.Screens.Available ? "on" : "off")}");
    }

    private void Draw(UiDraw ui)
    {
        var s = ui.Scale;
        if (_mark != null)
        {
            ui.Image(_mark, 48 * s, 48 * s);
            ui.SameLine();
        }
        ui.TextWrapped("A Kiln screen opened from the game's own menu (or F7). It has the mouse and keyboard while it is open; Escape closes it.");
        ui.Separator("Game screen");
        var cur = Ui.Screens.Current;
        ui.Text($"{cur.Kind}: {cur.State}{HudPart(cur)}, language {cur.Language}, frame {cur.Frame}");
        lock (_log)
            foreach (var l in _log) ui.TextDisabled(l);
        ui.Separator("Widgets");
        if (ui.BeginChild("menubuttons.list", 220 * s, 170 * s))
        {
            for (var i = 0; i < Items.Length; i++)
                if (ui.Selectable(Items[i], _selected == i)) _selected = i;
        }
        ui.EndChild();
        ui.SameLine();
        if (ui.BeginChild("menubuttons.right", 0, 170 * s))
        {
            ui.Slider("Volume", ref _volume, 0, 1);
            ui.InputText("Note", ref _note, 64);
            if (ui.Combo("Look", ref _dropdown, Choices)) Log.Info($"menubuttons: look {Choices[_dropdown]}");
            if (ui.Button($"Clicked {_clicks}x")) _clicks++;
            ui.SameLine();
            if (ui.Button("Toast")) Hud.Toast($"{Items[_selected]}: {_note}", ToastKind.Success);
        }
        ui.EndChild();
        ui.Spacing();
        if (ui.Button("Close", 120 * s)) ui.CloseScreen();
    }

    private void DrawCraftPanel(UiDraw ui, CraftingScreen c)
    {
        ui.Text(c.AtStation ? "A crafting station" : "Your own crafting");
        ui.TextDisabled(c.Workshop != 0 ? $"Workshop 0x{c.Workshop:x8}" : "Workshop: the player's own");
        ui.Text(c.Recipe != 0 ? $"Selected recipe 0x{c.Recipe:x8}" : "No recipe selected");
        if (c.Recipe != 0 && ui.Button("Log this recipe")) Log.Info($"menubuttons: recipe 0x{c.Recipe:x8} at workshop 0x{c.Workshop:x8}{(c.AtStation ? " (station)" : "")}");
    }
}
