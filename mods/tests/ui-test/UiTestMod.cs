// SPDX-License-Identifier: MIT
using Kiln;

namespace UiTest;

/// <summary>
/// kiln.ui game-UI test (client). Writes PASS/FAIL lines to kiln.log ("ui-test: ...").
///  At load (no player action):
///   - the mods load in the game's menu (Kiln.Host's UI tick), the game screen is known, its language resolves;
///   - UiText picks exact / primary / en / * texts;
///   - main and pause menu entries are added (game renderer), a Kiln screen opens and closes, a PNG loads;
///   - the crafting menu extension accepts a subcategory and a tab (kiln.craftmenu) and describes them.
///  Observed while the tester plays (each logged once):
///   - screens seen: MainMenu, Loading, InGame, PauseMenu, Inventory/Crafting, Map; all on one UI thread;
///   - "ui-test" entry chosen in the main menu and in the pause menu (activation callbacks);
///   - a crafting menu reports its station / workshop / selected recipe.
/// Summary lines "ui-test summary: N PASS, M FAIL, K waiting" when the game reaches the pause menu and at unload.
/// </summary>
public sealed class UiTestMod : Mod
{
    private int _pass, _fail;
    private readonly HashSet<string> _seen = new();
    private readonly HashSet<string> _waiting = new()
    {
        "screen MainMenu", "screen Loading", "screen InGame", "screen PauseMenu", "screen Crafting", "screen Map",
        "main menu entry chosen", "pause menu entry chosen", "crafting state",
    };
    private int _uiThread;
    private bool _threadOk = true;

    // a 2x2 RGBA PNG (red, green / blue, white)
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAEklEQVR4nGP4z8DwHwyBNBgAAEnICff5q7YNAAAAAElFTkSuQmCC");

    public override void OnLoad()
    {
        if (!Ui.Available)
        {
            Log.Info("ui-test done: 0 PASS, 0 FAIL (no overlay: dedicated server or kiln.ui off)");
            return;
        }
        var s = Ui.Screens.Current;
        Check(Ui.Screens.Available, $"game screens available (feature ui.screens): {s.Kind} {s.State}/{s.Hud}");
        Check(s.Kind is ScreenKind.MainMenu or ScreenKind.Title or ScreenKind.Loading or ScreenKind.InGame,
            $"OnLoad sees a game screen ({s.Kind}); {(s.Kind is ScreenKind.MainMenu or ScreenKind.Title ? "mods loaded in the game's menu" : "mods loaded with a world")}");
        Check(GameLanguage.Current.Length >= 2, $"game language {GameLanguage.Current} (id {s.LanguageId})");

        // UiText
        var t = UiText.Of(("en", "Hello"), ("de", "Hallo"), ("pt-br", "Olá"), ("*", "Hi"));
        Check(t.For("de-de") == "Hallo" && t.For("pt-br") == "Olá" && t.For("pt-pt") == "Hello" && t.For("ja-jp") == "Hello",
            "UiText picks exact, primary, en");
        Check(new UiText("x").For("fr-fr") == "x", "UiText from a string serves every language");

        // menu entries
        var main = Ui.Menus.Add(MenuLocation.MainMenu, "ui-test", () => Seen("main menu entry chosen"), order: 20);
        var pause = Ui.Menus.Add(MenuLocation.PauseMenu, UiText.Of(("en", "ui-test"), ("de", "ui-test (de)")), () => Seen("pause menu entry chosen"), order: 20);
        Check(main != null, "main menu entry added (ui.main_menu)");
        Check(pause != null, "pause menu entry added (ui.system_menu)");
        if (pause != null)
        {
            pause.Attention = true;
            Check(pause.Attention && pause.Visible, "pause entry flags settable");
        }

        // a screen
        var screen = Ui.Screen("ui-test screen", ui => ui.Text("ui-test"), new UiScreenOptions { Style = UiStyle.Kiln });
        screen.Open();
        var opened = screen.IsOpen && Ui.OpenScreens.Contains(screen);
        screen.Close();
        Check(opened && !screen.IsOpen && !Ui.OpenScreens.Contains(screen), "screen opens and closes (OpenScreens follows)");
        screen.Dispose();

        // an image
        var img = Ui.LoadImage(Png);
        Check(img is { Width: 2, Height: 2 }, $"PNG loads as a 2x2 image ({img?.Width}x{img?.Height})");
        img?.Dispose();
        Check(Ui.LoadImage([1, 2, 3, 4, 5, 6, 7, 8, 9]) == null, "non-PNG bytes are refused");

        // crafting menu extension (kiln.craftmenu, client kiln.content)
        if (Ui.Crafting.MenuAvailable)
        {
            var sub = Ui.Crafting.AddSubcategory(0, -1, UiText.Of(("en", "ui-test"), ("de", "ui-test")), [3647579549u]);
            var tab = Ui.Crafting.AddTab(0, "ui-test tab", [600408661u], iconFromTab: 0);
            Check(sub != null && tab != null, "crafting subcategory and tab accepted for workshop 0");
            var json = Ui.Crafting.Describe(0);
            Check(json.Contains("\"additions\"") && json.Contains("ui-test"), $"crafting menu describe lists them ({json.Length} bytes)");
        }
        else
        {
            Check(false, "kiln.craftmenu available (client kiln.content with content.recipe_menu)");
        }

        // observations
        Ui.Screens.Changed += (before, now) =>
        {
            var id = Environment.CurrentManagedThreadId;
            if (_uiThread == 0) _uiThread = id;
            else if (_uiThread != id && _threadOk)
            {
                _threadOk = false;
                Check(false, $"screen changes on one UI thread (saw {_uiThread} and {id})");
            }
            Seen($"screen {now.Kind}");
            if (now.Kind == ScreenKind.Inventory && now.Crafting is not null) Seen("screen Crafting");
            if (now.Crafting is { } c && (c.Recipe != 0 || c.AtStation || c.Workshop != 0))
                Seen("crafting state", $"{(c.AtStation ? "station" : "own")}, workshop 0x{c.Workshop:x8}, recipe 0x{c.Recipe:x8}");
            if (now.Kind == ScreenKind.PauseMenu) Summary();
        };
        var ticks = 0;
        Ui.Screens.UiTick += _ =>
        {
            if (++ticks == 600) Check(true, $"600 UI ticks (UI thread {Environment.CurrentManagedThreadId})");
        };
        Log.Info($"ui-test: {_pass} PASS, {_fail} FAIL at load; waiting for: {string.Join(", ", _waiting)}");
    }

    public override void OnUnload() => Summary();

    private void Seen(string what, string detail = "")
    {
        lock (_seen)
        {
            if (!_seen.Add(what)) return;
            _waiting.Remove(what);
        }
        Check(true, $"{what}{(detail.Length > 0 ? ": " + detail : "")}");
    }

    private void Summary()
    {
        string waiting;
        lock (_seen) waiting = string.Join(", ", _waiting);
        Log.Info($"ui-test summary: {_pass} PASS, {_fail} FAIL{(waiting.Length > 0 ? $", waiting: {waiting}" : ", nothing waiting")}" +
                 $"{(_threadOk && _uiThread != 0 ? $"; UI thread {_uiThread}" : "")}");
    }

    private void Check(bool ok, string what)
    {
        if (ok) Interlocked.Increment(ref _pass);
        else Interlocked.Increment(ref _fail);
        Log.Info($"ui-test: {(ok ? "PASS" : "FAIL")} {what}");
    }
}
