using Kiln;

namespace KilnUi1;

/// <summary>On a client, mods load in the game's menu (before any world): register menu entries, screens and key
/// bindings in OnLoad. The draw callback runs on the UI thread every frame while the screen is open; keep it cheap and
/// hand world work to the tick thread (Context.Scheduler.Post).</summary>
public sealed class KilnUi1Mod : Mod
{
    private static readonly UiText Label = UiText.Of(("en", "KilnUi1"), ("de", "KilnUi1"));
    private static readonly UiText Title = UiText.Of(("en", "KilnUi1 screen"), ("de", "KilnUi1-Fenster"));

    private UiScreen? _screen;
    private float _volume = 0.5f;
    private bool _enabled = true;
    private int _clicks;

    public override void OnLoad()
    {
        if (!Ui.Available)
        {
            Log.Info("no game UI here (a dedicated server, or kiln.ui is unavailable): nothing to do");
            return;
        }
        _screen = Ui.Screen(Title, Draw, new UiScreenOptions { Style = UiStyle.Game, Width = 520, Height = 360 });
        var (main, pause) = Ui.Menus.AddToBoth(Label, () => _screen.Open(), order: 20);
        KeyBindings.Register("kiln-mod-id.screen", "F9", () => _screen.Toggle(), "Open the KilnUi1 screen");
        Log.Info($"menu entries: main {(main != null ? "added" : "unavailable")}, pause {(pause != null ? "added" : "unavailable")}; F9 opens the screen");
    }

    private void Draw(UiDraw ui)
    {
        ui.Text("Hello from KilnUi1.");
        ui.Separator();
        ui.Checkbox("Enabled", ref _enabled);
        ui.Slider("Volume", ref _volume, 0f, 1f);
        if (ui.Button($"Click me ({_clicks})"))
        {
            _clicks++;
            Hud.Toast($"KilnUi1: clicked {_clicks} time(s)");
        }
    }
}
