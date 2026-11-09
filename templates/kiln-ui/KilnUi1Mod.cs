using Kiln;

namespace KilnUi1;

//#if (blocks)
/// <summary>The settings the screen in blocks/ui.json binds to by path: <c>config.audio.volume</c>, <c>config.hud.show</c>. The player's own file is kiln/config/kiln-mod-id-client.json.</summary>
public sealed class KilnUi1Config
{
    public HudSection Hud { get; set; } = new();
    public AudioSection Audio { get; set; } = new();
}

public sealed class HudSection
{
    [Comment("Show the line while playing.")]
    public bool Show { get; set; } = true;
}

public sealed class AudioSection
{
    [Comment("A number for a slider to change."), Range(0, 100)]
    public int Volume { get; set; } = 50;
}

/// <summary>The screen, a HUD line and the key are blocks: see blocks/ui.json. A block names the config's entries, so it is registered first.
/// A window, a line and a key are drawn and bound only in a player's game; a dedicated server skips this mod (<c>KilnSide</c> client).</summary>
public sealed class KilnUi1Mod : Mod
{
    public override void OnLoad() => Config.Register<KilnUi1Config>(ConfigKind.Client);
}
//#else
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
//#endif
