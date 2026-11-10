// SPDX-License-Identifier: MIT
// Custom full screens for mods (Forge: a Screen subclass + Minecraft.setScreen). Drawn with Kiln's ImGui overlay in
// the game's look (UiStyle.Game: the game's colours and its own UI font, read at run time) or Kiln's (UiStyle.Kiln).
// While a screen is open it has the mouse and keyboard (the game's input pauses); Escape closes the top screen.
namespace Kiln;

/// <summary>The look of a Kiln window or screen.</summary>
public enum UiStyle
{
    /// <summary>The game's menu colours and its own UI font (read from the game's resources at run time).</summary>
    Game = 1,
    /// <summary>Kiln's ember theme (branding/palette.json).</summary>
    Kiln = 0,
}

/// <summary>Options of a <see cref="UiScreen"/>.</summary>
public sealed record UiScreenOptions
{
    public UiStyle Style { get; init; } = UiStyle.Game;
    /// <summary>Size in pixels at UI scale 1, centred on the screen.</summary>
    public float Width { get; init; } = 720;
    public float Height { get; init; } = 520;
    /// <summary>Escape closes the screen (the top one first).</summary>
    public bool CloseOnEscape { get; init; } = true;
    /// <summary>Darken the game behind the screen.</summary>
    public bool DimBackground { get; init; } = true;
    public bool TitleBar { get; init; } = true;
    public bool Resizable { get; init; }
    /// <summary>A close button (X) in the title bar.</summary>
    public bool CloseButton { get; init; } = true;

    /// <summary>The whole display: no title bar, not movable or resizable, no frame (Width and Height are ignored). Draw
    /// your own title and layout; <see cref="UiDraw.Width"/> / <see cref="UiDraw.Height"/> are the display's size.
    /// Escape still closes it.</summary>
    public bool FullScreen { get; init; }
}

/// <summary>A full screen of a mod: a menu with lists, buttons, sliders, text fields and images. Open it from a
/// <see cref="Ui.Menus"/> entry, a key binding or a command (any thread). While open it takes the mouse and keyboard
/// (the game's input pauses; key releases still reach the game), and Escape closes it.</summary>
/// <example><code>
/// var screen = Ui.Screen("My mod", ui => { ui.Text("Hello"); if (ui.Button("Close")) ui.CloseScreen(); });
/// Menus.Add(MenuLocation.PauseMenu, "My mod", screen.Open);
/// KeyBindings.Register("mymod.screen", "F7", screen.Toggle);
/// </code></example>
public sealed class UiScreen : IDisposable
{
    private static int _ids;
    private volatile bool _open;

    internal UiScreen(UiText title, Action<UiDraw> draw, UiScreenOptions options, ModContext? owner)
    {
        Title = title;
        Draw = draw;
        Options = options;
        Owner = owner;
        Id = $"###kiln.screen{Interlocked.Increment(ref _ids)}";
    }

    public UiText Title { get; set; }
    public UiScreenOptions Options { get; }
    internal Action<UiDraw> Draw { get; }
    internal ModContext? Owner { get; }
    internal string Id { get; }
    internal bool Disposed;
    internal long OpenedAt;
    internal bool FocusNext;

    public bool IsOpen => _open;

    /// <summary>Runs (as the owning mod, on the thread that closed it) when the screen closes.</summary>
    public event Action? Closed;

    /// <summary>Runs when the screen opens.</summary>
    public event Action? Opened;

    public void Open()
    {
        if (Disposed || _open) return;
        _open = true;
        OpenedAt = Interlocked.Increment(ref UiEngine.ScreenOrder);
        FocusNext = true;
        UiEngine.Recount();
        if (Opened is { } o) UiModCalls.Invoke(Owner, static a => a(), o, "screen Opened");
    }

    public void Close()
    {
        if (!_open) return;
        _open = false;
        UiEngine.Recount();
        if (Closed is { } c) UiModCalls.Invoke(Owner, static a => a(), c, "screen Closed");
    }

    public void Toggle()
    {
        if (_open) Close();
        else Open();
    }

    /// <summary>Removes the screen (also done when the owning mod unloads).</summary>
    public void Dispose()
    {
        Close();
        UiEngine.RemoveScreen(this);
    }
}

public static partial class Ui
{
    /// <summary>A full screen drawn by <paramref name="draw"/> every frame while open (closed at first). Owned by the
    /// calling mod (removed when it unloads).</summary>
    public static UiScreen Screen(UiText title, Action<UiDraw> draw, UiScreenOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(draw);
        UiEngine.EnsureStarted();
        return UiEngine.AddScreen(new UiScreen(title, draw, options ?? new UiScreenOptions(), UiModCalls.Caller));
    }

    /// <summary>The screens open now, the top one last.</summary>
    public static IReadOnlyList<UiScreen> OpenScreens => UiEngine.OpenScreens();
}
