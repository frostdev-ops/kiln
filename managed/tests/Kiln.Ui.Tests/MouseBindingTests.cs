// SPDX-License-Identifier: MIT
// Key bindings on the mouse (GitHub issue 29, since 0.2.0-dev.27): "Ctrl+WheelUp", "Ctrl+WheelDown", "Shift+Mouse4". The
// names in KeyChord (parse, print, the refusal of left and right click), keybinds.json (the new names round trip, a file
// an older Kiln wrote reads as before), the Kiln menu's key (a button, never the wheel), and UiEngine.Key on the window
// thread (a press per notch, no hold for the wheel, nothing while a Kiln screen has the mouse, the Keys page's capture).
// kiln.ui's half (one press per notch from raw input and the message, what the game gets) is
// native/services/ui/tests/window_input/harness.cpp. No game: a fake kiln.ui table.
using System.Reflection;
using System.Text.Json.Nodes;
using Kiln.FakeUi;
using Xunit;

namespace Kiln.MouseBindingTests;

[Collection(UiSerial.Name)]
public sealed class MouseBindingTests : IDisposable
{
    private const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly FieldInfo Remapping = typeof(UiEngine).GetField("_remapping", Static)!;
    private readonly IKilnBackend _previous = Backend.Current;
    private readonly string _previousRoot = KilnPaths.Root;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kiln-mousebinds-" + Guid.NewGuid().ToString("N"));
    private readonly List<KeyBinding> _bindings = [];

    public MouseBindingTests()
    {
        FakeUiTable.Install();
        Backend.Current = new FakeUiTable.Backend();
        UiNative.ResetForTests();
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        KilnPaths.Root = _root;
        KilnPaths.Config = null!;
        ForgetSaved();
    }

    public void Dispose()
    {
        Remapping.SetValue(null, null);
        foreach (var b in _bindings) b.Dispose();
        ForgetSaved();
        KilnPaths.Root = _previousRoot;
        KilnPaths.Config = null!;
        Backend.Current = _previous;
        UiNative.ResetForTests();
        FakeUiTable.Free();
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    /// <summary>KeyBindings reads keybinds.json once per process: make it read this test's file next.</summary>
    private static void ForgetSaved()
    {
        typeof(KeyBindings).GetField("_loaded", Static)!.SetValue(null, false);
        typeof(KeyBindings).GetField("_saved", Static)!.SetValue(null, new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private KeyBinding Register(string id, string key, Action? onPress = null)
    {
        var b = KeyBindings.Register(id, key, onPress, id, "Tests");
        _bindings.Add(b);
        return b;
    }

    private static bool Send(uint vk, KeyMods mods = KeyMods.None, bool down = true, bool captured = false) =>
        UiEngine.Key(new UiNative.Key { Size = 20, Vk = vk, Mods = (uint)mods, Down = down ? (byte)1 : (byte)0, Captured = captured ? (byte)1 : (byte)0 });

    private string File => Path.Combine(_root, "config", "keybinds.json");

    private JsonObject SavedBindings() => (JsonObject)JsonNode.Parse(System.IO.File.ReadAllText(File))!["bindings"]!;

    // ---- names ----

    [Theory]
    [InlineData("Mouse4", 0x05u, KeyMods.None, "Mouse4")]
    [InlineData("Mouse5", 0x06u, KeyMods.None, "Mouse5")]
    [InlineData("MouseMiddle", 0x04u, KeyMods.None, "MouseMiddle")]
    [InlineData("Mouse3", 0x04u, KeyMods.None, "MouseMiddle")]
    [InlineData("WheelUp", 0x100u, KeyMods.None, "WheelUp")]
    [InlineData("WheelDown", 0x101u, KeyMods.None, "WheelDown")]
    [InlineData("WheelLeft", 0x102u, KeyMods.None, "WheelLeft")]
    [InlineData("WheelRight", 0x103u, KeyMods.None, "WheelRight")]
    [InlineData("Ctrl+WheelUp", 0x100u, KeyMods.Ctrl, "Ctrl+WheelUp")]
    [InlineData("Ctrl+WheelDown", 0x101u, KeyMods.Ctrl, "Ctrl+WheelDown")]
    [InlineData("Shift+Mouse4", 0x05u, KeyMods.Shift, "Shift+Mouse4")]
    [InlineData("shift + mouse4", 0x05u, KeyMods.Shift, "Shift+Mouse4")]
    [InlineData("Alt+Shift+WheelLeft", 0x102u, KeyMods.Shift | KeyMods.Alt, "Shift+Alt+WheelLeft")]
    [InlineData("Control+Alt+Mouse5", 0x06u, KeyMods.Ctrl | KeyMods.Alt, "Ctrl+Alt+Mouse5")]
    [InlineData("Ctrl+Shift+Alt+MouseMiddle", 0x04u, KeyMods.Ctrl | KeyMods.Shift | KeyMods.Alt, "Ctrl+Shift+Alt+MouseMiddle")]
    [InlineData("Alt+WheelRight", 0x103u, KeyMods.Alt, "Alt+WheelRight")]
    public void Mouse_names_parse_and_print(string text, uint vk, KeyMods mods, string printed)
    {
        Assert.True(KeyChord.TryParse(text, out var c));
        Assert.Equal(new KeyChord(vk, mods), c);
        Assert.Equal(printed, c.ToString());
        Assert.True(KeyChord.TryParse(printed, out var again));
        Assert.Equal(c, again);
        Assert.Equal(c, KeyChord.Parse(text));
    }

    [Fact]
    public void The_wheel_codes_are_above_every_key_and_the_buttons_keep_their_virtual_keys()
    {
        Assert.Equal(0x100u, KeyChord.Parse("WheelUp").VirtualKey);
        Assert.All(new[] { "WheelUp", "WheelDown", "WheelLeft", "WheelRight" }, n => Assert.True(KeyChord.Parse(n).VirtualKey > 0xFF));
        Assert.Equal(0x05u, KeyChord.Parse("Mouse4").VirtualKey);  // VK_XBUTTON1
        Assert.Equal(0x06u, KeyChord.Parse("Mouse5").VirtualKey);  // VK_XBUTTON2
        Assert.Equal(0x04u, KeyChord.Parse("MouseMiddle").VirtualKey);  // VK_MBUTTON
        // a chord made from the codes prints the names, whatever the native table says
        Assert.Equal("Ctrl+WheelUp", new KeyChord(0x100, KeyMods.Ctrl).ToString());
        Assert.Equal("Shift+Mouse4", new KeyChord(0x05, KeyMods.Shift).ToString());
    }

    [Theory]
    [InlineData("Mouse1")]
    [InlineData("Mouse2")]
    [InlineData("LeftClick")]
    [InlineData("RightClick")]
    [InlineData("Ctrl+LeftClick")]
    [InlineData("Shift+Mouse1")]
    [InlineData("MouseLeft")]
    [InlineData("Alt+RButton")]
    public void Left_and_right_clicks_are_refused_and_the_error_says_why(string text)
    {
        Assert.False(KeyChord.TryParse(text, out var c));
        Assert.True(c.IsNone);
        var e = Assert.Throws<FormatException>(() => KeyChord.Parse(text));
        Assert.Contains("the left and right mouse buttons cannot be bound", e.Message);
        Assert.Contains("Mouse4", e.Message);
        Assert.Throws<FormatException>(() => KeyBindings.Register("t.click." + Guid.NewGuid().ToString("N"), text, null));
    }

    [Fact]
    public void An_unknown_key_names_the_mouse_examples_and_two_inputs_are_not_one_key()
    {
        var e = Assert.Throws<FormatException>(() => KeyChord.Parse("Hyper+9"));
        Assert.Contains("unknown key 'Hyper+9'", e.Message);
        Assert.Contains("Ctrl+WheelUp", e.Message);
        Assert.Contains("left and right mouse buttons cannot be bound", e.Message);
        Assert.False(KeyChord.TryParse("Mouse4+WheelUp", out _));
        Assert.False(KeyChord.TryParse("Ctrl+", out _));
        Assert.False(KeyChord.TryParse("Wheel", out _));
    }

    [Fact]
    public void Keys_parse_as_before()
    {
        Assert.Equal(new KeyChord(0x77, KeyMods.None), KeyChord.Parse("F8"));
        Assert.Equal(new KeyChord(0x4B, KeyMods.Ctrl), KeyChord.Parse("Ctrl+K"));
        Assert.Equal(new KeyChord(0x71, KeyMods.Shift | KeyMods.Alt), KeyChord.Parse("Shift+Alt+F2"));
        Assert.Equal(new KeyChord(0x2D, KeyMods.None), KeyChord.Parse("Insert"));
        Assert.Equal(new KeyChord(0x65, KeyMods.None), KeyChord.Parse("Numpad5"));
        Assert.Equal(new KeyChord(0xC0, KeyMods.None), KeyChord.Parse("Backquote"));
        Assert.True(KeyChord.Parse("None").IsNone);
        Assert.Equal("Ctrl+K", KeyChord.Parse("Ctrl+K").ToString());  // keys still named by kiln.ui's table
        Assert.Equal("F8", KeyChord.Parse("F8").ToString());
    }

    // ---- keybinds.json ----

    [Fact]
    public void Mouse_bindings_round_trip_through_keybinds_json()
    {
        Assert.False(KeyBindings.Rebind("t.zoom-in", "Ctrl+WheelUp"));  // not registered yet: saved for when it is
        KeyBindings.Rebind("t.zoom-out", KeyChord.Parse("ctrl+wheeldown"));
        KeyBindings.Rebind("t.side", "Shift+Mouse4");
        KeyBindings.Rebind("t.pan", "Alt+WheelRight");
        var saved = SavedBindings();
        Assert.Equal("Ctrl+WheelUp", (string?)saved["t.zoom-in"]);
        Assert.Equal("Ctrl+WheelDown", (string?)saved["t.zoom-out"]);
        Assert.Equal("Shift+Mouse4", (string?)saved["t.side"]);
        Assert.Equal("Alt+WheelRight", (string?)saved["t.pan"]);

        ForgetSaved();  // a new start reads the file
        Assert.Equal(new KeyChord(0x100, KeyMods.Ctrl), Register("t.zoom-in", "F8").Key);
        Assert.Equal(new KeyChord(0x101, KeyMods.Ctrl), Register("t.zoom-out", "F9").Key);
        Assert.Equal(new KeyChord(0x05, KeyMods.Shift), Register("t.side", "F10").Key);
        Assert.Equal(new KeyChord(0x103, KeyMods.Alt), Register("t.pan", "None").Key);
        Assert.Equal(new KeyChord(0x77, KeyMods.None), _bindings[0].Default);
    }

    [Fact]
    public void A_keybinds_json_an_older_Kiln_wrote_reads_as_before()
    {
        // as 0.2.0-dev.26 writes it
        System.IO.File.WriteAllText(File, """
            {
              "comment": "Kiln key bindings: id -> key (F8, Ctrl+K, None). Edited by the Kiln menu (Keys).",
              "bindings": {
                "kiln.menu": "Home",
                "t.a": "F9",
                "t.b": "Ctrl+K",
                "t.c": "None",
                "t.d": "Shift+Alt+F2",
                "t.e": "Numpad5"
              }
            }
            """);
        Assert.Equal(new KeyChord(0x78, KeyMods.None), Register("t.a", "F8").Key);
        Assert.Equal(new KeyChord(0x4B, KeyMods.Ctrl), Register("t.b", "F8").Key);
        Assert.True(Register("t.c", "F8").Key.IsNone);
        Assert.Equal(new KeyChord(0x71, KeyMods.Shift | KeyMods.Alt), Register("t.d", "F8").Key);
        Assert.Equal(new KeyChord(0x65, KeyMods.None), Register("t.e", "F8").Key);
        Assert.Equal(new KeyChord(0x24, KeyMods.None), KeyBindings.MenuKey);
        // saving a new mouse binding keeps every older line as it was
        KeyBindings.Rebind("t.a", "Ctrl+WheelUp");
        var saved = SavedBindings();
        Assert.Equal("Ctrl+WheelUp", (string?)saved["t.a"]);
        Assert.Equal("Ctrl+K", (string?)saved["t.b"]);
        Assert.Equal("None", (string?)saved["t.c"]);
        Assert.Equal("Shift+Alt+F2", (string?)saved["t.d"]);
        Assert.Equal("Numpad5", (string?)saved["t.e"]);
        Assert.Equal("Home", (string?)saved["kiln.menu"]);
    }

    [Fact]
    public void The_Kiln_menu_key_takes_a_mouse_button_but_not_the_wheel()
    {
        Assert.False(KeyBindings.Rebind(KeyBindings.MenuId, "Ctrl+WheelUp"));
        Assert.Equal(KeyBindings.MenuDefault, KeyBindings.MenuKey);
        Assert.False(System.IO.File.Exists(File));  // nothing saved
        Assert.True(KeyBindings.Rebind(KeyBindings.MenuId, "Mouse4"));
        Assert.Equal(new KeyChord(0x05, KeyMods.None), KeyBindings.MenuKey);
        // a hand-edited file that puts it on the wheel: the default
        System.IO.File.WriteAllText(File, """{"bindings": {"kiln.menu": "WheelDown"}}""");
        ForgetSaved();
        Assert.Equal(KeyBindings.MenuDefault, KeyBindings.MenuKey);
    }

    // ---- presses (UiEngine.Key, the window thread) ----

    [Fact]
    public void A_wheel_binding_fires_once_per_notch_and_keeps_the_notch_from_the_game()
    {
        var n = 0;
        var zoom = Register("t.wheel.zoom", "Ctrl+WheelUp", () => n++);
        var released = 0;
        zoom.OnRelease = () => released++;
        Assert.True(Send(0x100, KeyMods.Ctrl));
        Assert.True(Send(0x100, KeyMods.Ctrl));
        Assert.Equal(2, n);
        Assert.Equal(2, zoom.TakePresses());
        Assert.False(zoom.IsDown);  // a notch has no hold and no release
        Assert.Equal(0, released);
        Assert.False(Send(0x100));  // the wheel without Ctrl is the game's (its camera zoom)
        Assert.False(Send(0x101, KeyMods.Ctrl));  // the other way is not bound
        Assert.Equal(2, n);
        zoom.Consume = false;
        Assert.False(Send(0x100, KeyMods.Ctrl));  // fires, but the game gets the notch too
        Assert.Equal(3, n);
    }

    [Fact]
    public void No_wheel_binding_fires_while_a_Kiln_screen_has_the_mouse()
    {
        var n = 0;
        var b = Register("t.wheel.captured", "Ctrl+WheelDown", () => n++);
        b.WhileCapturing = true;
        Assert.False(Send(0x101, KeyMods.Ctrl, captured: true));  // a mod's window or the Kiln menu: the wheel scrolls it
        FakeUiTable.MenuOpen = true;
        Assert.False(Send(0x101, KeyMods.Ctrl, captured: true));
        FakeUiTable.MenuOpen = false;
        Assert.Equal(0, n);
        Assert.True(Send(0x101, KeyMods.Ctrl));
        Assert.Equal(1, n);
    }

    [Fact]
    public void A_side_button_binding_is_pressed_held_and_released_like_a_key()
    {
        var pressed = 0;
        var released = 0;
        var b = Register("t.side.button", "Shift+Mouse4", () => pressed++);
        b.OnRelease = () => released++;
        Assert.True(Send(0x05, KeyMods.Shift));
        Assert.True(b.IsDown);
        Assert.False(Send(0x05, KeyMods.Shift, down: false));  // releases always reach the game
        Assert.False(b.IsDown);
        Assert.Equal((1, 1), (pressed, released));
        Assert.False(Send(0x05));  // Mouse4 alone is the game's
        Assert.False(Send(0x06, KeyMods.Shift));  // Mouse5 is not bound
        // as a key: it fires while a mod's own window has the input, not while the Kiln menu has it
        Assert.True(Send(0x05, KeyMods.Shift, captured: true));
        Send(0x05, KeyMods.Shift, down: false, captured: true);
        FakeUiTable.MenuOpen = true;
        Assert.False(Send(0x05, KeyMods.Shift, captured: true));
        FakeUiTable.MenuOpen = false;
        Assert.Equal(2, pressed);
    }

    [Fact]
    public void The_Keys_page_takes_the_wheel_and_the_buttons_with_modifiers()
    {
        var b = Register("t.remap.mouse", "F2");
        FakeUiTable.MenuOpen = true;  // the Kiln menu on Keys: Kiln captures input
        Remapping.SetValue(null, b.Id);
        Assert.True(Send(0x101, KeyMods.Alt, captured: true));
        Assert.Equal("Alt+WheelDown", b.Key.ToString());
        Assert.Null(Remapping.GetValue(null));
        Remapping.SetValue(null, b.Id);
        Assert.True(Send(0x05, KeyMods.Ctrl | KeyMods.Shift, captured: true));
        Assert.Equal("Ctrl+Shift+Mouse4", b.Key.ToString());
        Remapping.SetValue(null, b.Id);
        Assert.True(Send(0x04, captured: true));
        Assert.Equal("MouseMiddle", b.Key.ToString());
        Assert.Equal("MouseMiddle", (string?)SavedBindings()[b.Id]);
        // the Kiln menu's own key: the wheel is refused (scrolling the menu would close it), the press is still taken
        Remapping.SetValue(null, KeyBindings.MenuId);
        Assert.True(Send(0x100, KeyMods.Ctrl, captured: true));
        Assert.Equal(KeyBindings.MenuDefault, KeyBindings.MenuKey);
        Assert.Null(Remapping.GetValue(null));
        FakeUiTable.MenuOpen = false;
    }

    [Fact]
    public void The_wheel_without_Ctrl_or_Alt_waits_for_play_like_a_typing_key()
    {
        Assert.True(UiEngine.ForPlay(0x100, KeyMods.None));
        Assert.True(UiEngine.ForPlay(0x101, KeyMods.Shift));
        Assert.False(UiEngine.ForPlay(0x100, KeyMods.Ctrl));
        Assert.False(UiEngine.ForPlay(0x103, KeyMods.Alt));
        Assert.False(UiEngine.ForPlay(0x05, KeyMods.None));  // the buttons fire on every screen, like F8
        Assert.False(UiEngine.ForPlay(0x04, KeyMods.Shift));
        Assert.True(UiEngine.ForPlay(0x4B, KeyMods.None));  // K, as before
        Assert.False(UiEngine.ForPlay(0x77, KeyMods.None));  // F8, as before
    }
}
