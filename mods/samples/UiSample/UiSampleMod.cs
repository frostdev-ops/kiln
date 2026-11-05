// SPDX-License-Identifier: MIT
using Kiln;

namespace UiSample;

/// <summary>
/// Kiln.Ui in three parts:
///  - a HUD line (top right): overlay frames per second and the client state;
///  - a window (F8, remappable in the Kiln menu's Keys tab) with a checkbox, a slider, a text field and a button;
///  - a toast when the window's button is pressed.
/// </summary>
public sealed class UiSampleMod : Mod
{
    private UiWindow? _window;
    private HudLine? _hud;
    private int _frames;
    private double _fps, _acc;
    private bool _showHud = true;
    private float _size = 1.0f;
    private string _name = "Kiln";
    private int _clicks;

    public override void OnLoad()
    {
        if (!Ui.Available)
        {
            Log.Info("uisample: no overlay here (dedicated server or no kiln.ui): nothing to do");
            return;
        }
        // frame counter on the render thread
        Ui.Frame += ui =>
        {
            _frames++;
            _acc += ui.DeltaSeconds;
            if (_acc >= 1)
            {
                _fps = _frames / _acc;
                _frames = 0;
                _acc = 0;
            }
        };
        _hud = Hud.Show("uisample.fps", () => _showHud ? $"overlay {_fps:F0} fps | {Describe()}" : "", HudAnchor.TopRight, Color.Orange);
        _window = Ui.Window("UI sample", Draw, new UiWindowOptions { StartOpen = false, Width = 380, Height = 260, Position = (0.75f, 0.4f) });
        KeyBindings.Register("uisample.window", "F8", () => _window.Toggle(), "Toggle the sample window");
        Log.Info("uisample: HUD line and window ready; F8 toggles the window, Insert opens the Kiln menu");
    }

    private static string Describe()
    {
        try
        {
            return Client.Available ? $"{Client.State}{(Client.HasLocalPlayer ? $", player {Client.LocalPlayer}" : "")}" : "client ?";
        }
        catch (Exception)
        {
            return "client ?";
        }
    }

    private void Draw(UiDraw ui)
    {
        ui.Text("Hello from a Kiln mod.");
        ui.TextDisabled($"{ui.Width:F0}x{ui.Height:F0}, scale {ui.Scale:F2}");
        ui.Separator();
        ui.Checkbox("Show the HUD line", ref _showHud);
        ui.Slider("Size", ref _size, 0.5f, 3f);
        ui.InputText("Name", ref _name, 64);
        if (ui.Button($"Greet {_name}"))
        {
            _clicks++;
            Hud.Toast($"Hello, {_name}! ({_clicks})", ToastKind.Success);
        }
        ui.SameLine();
        if (ui.Button("Close")) _window!.Hide();
        ui.Progress(_size / 3f, $"{_size:F2}");
    }

    public override void OnUnload() => Log.Info("uisample: unloaded");
}
