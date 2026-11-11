// SPDX-License-Identifier: MIT
// The Keys tab's "press a key..." (UiEngine.Key on the window thread): kiln.ui closes the Kiln menu on Escape before any
// subscriber sees the key, so a rebind armed in the Keys tab must not outlive the menu (or the tab) and take the next key
// pressed in play. No game: a fake kiln.ui table.
using System.Reflection;
using Kiln.FakeUi;
using Xunit;

namespace Kiln.KeyRemapTests;

[Collection(UiSerial.Name)]
public sealed class KeyRemapTests : IDisposable
{
    private readonly IKilnBackend _previous;
    private readonly KeyBinding _binding;
    private static readonly FieldInfo Remapping = typeof(UiEngine).GetField("_remapping", BindingFlags.NonPublic | BindingFlags.Static)!;

    public KeyRemapTests()
    {
        _previous = Backend.Current;
        FakeUiTable.Install();
        Backend.Current = new FakeUiTable.Backend();
        UiNative.ResetForTests();
        _binding = KeyBindings.Register("test.remap." + Guid.NewGuid().ToString("N"), "F2", null, "a test binding", "Tests");
    }

    public void Dispose()
    {
        Remapping.SetValue(null, null);
        _binding.Dispose();
        Backend.Current = _previous;
        UiNative.ResetForTests();
        FakeUiTable.Free();
    }

    private static bool Press(uint vk)
    {
        var down = new UiNative.Key { Size = 24, Vk = vk, Down = 1 };
        var consumed = UiEngine.Key(down);
        UiEngine.Key(down with { Down = 0 });
        return consumed;
    }

    [Fact]
    public void A_rebind_armed_in_the_keys_tab_does_not_take_a_key_after_the_menu_closed()
    {
        // the player clicked the binding in Keys, then pressed Escape: kiln.ui closed the menu and kept the key to itself
        Remapping.SetValue(null, _binding.Id);
        FakeUiTable.MenuOpen = false;
        Assert.False(Press(0x57));  // W, in play: the game's
        Assert.Equal("F2", _binding.Key.ToString());
        Assert.Null(Remapping.GetValue(null));
        Assert.False(Press(0x57));  // and later presses too
    }

    [Fact]
    public void While_the_menu_shows_the_keys_tab_the_next_key_is_the_binding()
    {
        Remapping.SetValue(null, _binding.Id);
        FakeUiTable.MenuOpen = true;
        Assert.True(Press(0x4B));  // K
        Assert.Equal("K", _binding.Key.ToString());
        Assert.Null(Remapping.GetValue(null));
    }

    [Fact]
    public void A_frame_without_the_keys_tab_drops_the_pending_rebind()
    {
        Remapping.SetValue(null, _binding.Id);
        // the Keys tab was not drawn this frame (another section, or the menu closed)
        var end = typeof(UiEngine).GetMethod("EndMenuFrame", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(end);
        end.Invoke(null, null);
        Assert.Null(Remapping.GetValue(null));
        FakeUiTable.MenuOpen = true;
        Assert.False(Press(0x4B));
        Assert.Equal("F2", _binding.Key.ToString());
    }
}
