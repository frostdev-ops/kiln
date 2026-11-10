// SPDX-License-Identifier: MIT
// Layout and look for full-display screens (kiln.ui v3, kiln_ui.h "v3"): font sizes, child regions with flags, the
// cursor and the last widget's rectangle, sized selectables, keyboard focus, drawing into a window, colour and spacing
// pushes, and key queries. The Kiln mod manager is built with them; mods may use them too. On an older kiln.ui each
// call falls back to the nearest older one or does nothing (HasLayout says which).
namespace Kiln;

/// <summary>Flags of <see cref="UiDraw.BeginChild(string, float, float, UiChildFlags, UiWindowFlags)"/>.</summary>
[Flags]
public enum UiChildFlags : uint
{
    None = 0,
    /// <summary>An outline (and the window padding).</summary>
    Border = 1u << 0,
    /// <summary>The window padding without an outline.</summary>
    Padding = 1u << 1,
    /// <summary>As tall as its contents (pass height 0).</summary>
    AutoHeight = 1u << 5,
    /// <summary>Looks like a frame (frame colours and padding).</summary>
    FrameStyle = 1u << 7,
    /// <summary>Gamepad and keyboard navigation moves into and out of it like plain widgets (use it for columns).</summary>
    NavFlattened = 1u << 8,
}

/// <summary>Window flags for child regions.</summary>
[Flags]
public enum UiWindowFlags : uint
{
    None = 0,
    NoScrollbar = 1u << 3,
    NoScrollWithMouse = 1u << 4,
    NoBackground = 1u << 7,
    AlwaysVerticalScrollbar = 1u << 14,
}

/// <summary>Flags of the sized <see cref="UiDraw.Selectable(string, bool, float, float, UiSelectableFlags)"/>.</summary>
[Flags]
public enum UiSelectableFlags : uint
{
    None = 0,
    SpanAllColumns = 1u << 1,
    AllowDoubleClick = 1u << 2,
    Disabled = 1u << 3,
    /// <summary>Widgets drawn over it later take their own clicks.</summary>
    AllowOverlap = 1u << 4,
    /// <summary>Drawn as if hovered.</summary>
    Highlight = 1u << 5,
}

/// <summary>Where <see cref="UiDraw.DrawRect(UiLayer, float, float, float, float, Color, float, float)"/> draws.</summary>
public enum UiLayer : uint
{
    /// <summary>The current window: clipped to it, scrolls with it, under the widgets drawn after.</summary>
    Window = 0,
    /// <summary>Behind every window.</summary>
    Background = 1,
    /// <summary>Over everything.</summary>
    Foreground = 2,
}

/// <summary>A colour of the current look (<see cref="UiDraw.PushColor"/>).</summary>
public enum UiColorSlot : uint
{
    Text, TextDisabled, WindowBg, ChildBg, Border, FrameBg, FrameBgHovered, FrameBgActive, Button, ButtonHovered,
    ButtonActive,
    /// <summary>Selectables: selected.</summary>
    Header,
    /// <summary>Selectables: hovered.</summary>
    HeaderHovered,
    /// <summary>Selectables: held.</summary>
    HeaderActive,
    Separator, ScrollbarBg, ScrollbarGrab,
    /// <summary>The gamepad / keyboard focus outline.</summary>
    NavCursor,
    CheckMark, SliderGrab,
}

/// <summary>A spacing or rounding of the current look (<see cref="UiDraw.PushVar"/>); pixels (multiply by
/// <see cref="UiDraw.Scale"/>), the <c>Align</c> ones 0..1.</summary>
public enum UiStyleVar : uint
{
    WindowPadding, FramePadding, ItemSpacing, ItemInnerSpacing, FrameRounding, FrameBorderSize, ChildRounding,
    ChildBorderSize, SelectableTextAlign, ButtonTextAlign, ScrollbarSize, WindowBorderSize,
}

/// <summary>Keys for <see cref="UiDraw.KeyPressed"/> (keyboard and gamepad).</summary>
public enum UiKey : uint
{
    Tab = 1, Left, Right, Up, Down, PageUp, PageDown, Home, End, Enter, Escape, Backspace,
    /// <summary>The letter F (Ctrl+F: find).</summary>
    F,
    GamepadLB, GamepadRB,
    /// <summary>The left face button (Xbox X).</summary>
    GamepadX,
    /// <summary>The top face button (Xbox Y).</summary>
    GamepadY,
    GamepadStart, GamepadBack,
}

/// <summary>Where the next widget goes (<see cref="UiDraw.Cursor"/>). X/Y are window-local (for
/// <see cref="UiDraw.SetCursor"/>), ScreenX/ScreenY screen pixels (for the Draw calls).</summary>
public readonly record struct UiCursor(float X, float Y, float ScreenX, float ScreenY, float AvailWidth, float AvailHeight,
    float WindowX, float WindowY, float WindowWidth, float WindowHeight, float FontSize, float LineHeight, float ScrollY,
    float ScrollMaxY);

/// <summary>The widget drawn last (<see cref="UiDraw.LastItem"/>), in screen pixels.</summary>
public readonly record struct UiItem(float MinX, float MinY, float MaxX, float MaxY, bool Hovered, bool Active, bool Focused,
    bool Clicked, bool Visible, bool Edited, bool Activated, bool Deactivated)
{
    public float Width => MaxX - MinX;
    public float Height => MaxY - MinY;
}

public sealed unsafe partial class UiDraw
{
    private static UiNative.Table3* T3 => UiNative.T3;

    /// <summary>This kiln.ui has the layout calls in this file (version 3). Without them each falls back or does nothing.</summary>
    public bool HasLayout => T3 != null;

    /// <summary>Text drawn inside the scope uses <paramref name="size"/> pixels at UI scale 1 (the scale applies on top):
    /// <c>using (ui.FontSize(36)) ui.Text("Mods");</c>. The font stays (the game's under <see cref="UiStyle.Game"/>).</summary>
    public FontScope FontSize(float size)
    {
        var t = T3;
        if (t == null) return new FontScope(false);
        t->PushFontSize(size);
        return new FontScope(true);
    }

    public readonly struct FontScope(bool pushed) : IDisposable
    {
        public void Dispose()
        {
            if (pushed) UiNative.T3->PopFontSize();
        }
    }

    /// <summary>A child region (call <see cref="EndChild"/> always). Width/height 0 = the rest, negative = the rest minus
    /// that much. No outline unless <see cref="UiChildFlags.Border"/>.</summary>
    public bool BeginChild(string id, float width, float height, UiChildFlags flags, UiWindowFlags windowFlags = UiWindowFlags.None)
    {
        var t = T3;
        if (t == null) return T->BeginChild(Utf8.Of(id), width, height, (uint)windowFlags) != 0;
        return t->BeginChildEx(Utf8.Of(id), width, height, (uint)flags, (uint)windowFlags) != 0;
    }

    /// <summary>The width of the next widget (pixels; negative = the rest of the line minus that much).</summary>
    public void NextItemWidth(float width)
    {
        var t = T3;
        if (t != null) t->SetNextItemWidth(width);
    }

    /// <summary>Where the next widget goes and the space left.</summary>
    public UiCursor Cursor
    {
        get
        {
            var t = T3;
            if (t == null) return default;
            UiNative.Cursor c = default;
            c.Size = (uint)sizeof(UiNative.Cursor);
            t->GetCursor(&c);
            return new UiCursor(c.X, c.Y, c.ScreenX, c.ScreenY, c.AvailWidth, c.AvailHeight, c.WindowX, c.WindowY, c.WindowWidth,
                c.WindowHeight, c.FontSize, c.LineHeight, c.ScrollY, c.ScrollMaxY);
        }
    }

    /// <summary>Moves the next widget to (x, y), window-local like <see cref="UiCursor.X"/>.</summary>
    public void SetCursor(float x, float y)
    {
        var t = T3;
        if (t != null) t->SetCursorPos(x, y);
    }

    /// <summary>The rectangle and state of the widget drawn last.</summary>
    public UiItem LastItem
    {
        get
        {
            var t = T3;
            if (t == null) return default;
            UiNative.Item i = default;
            i.Size = (uint)sizeof(UiNative.Item);
            t->GetItem(&i);
            return new UiItem(i.MinX, i.MinY, i.MaxX, i.MaxY, i.Hovered != 0, i.Active != 0, i.Focused != 0, i.Clicked != 0,
                i.Visible != 0, i.Edited != 0, i.Activated != 0, i.Deactivated != 0);
        }
    }

    /// <summary>A selectable row of a size (0 = the rest of the line / one text line). True when clicked or activated
    /// (Enter, gamepad A).</summary>
    public bool Selectable(string label, bool selected, float width, float height = 0, UiSelectableFlags flags = UiSelectableFlags.None)
    {
        var t = T3;
        if (t == null) return Selectable(label, selected);
        return t->SelectableEx(Utf8.Of(label), selected ? (byte)1 : (byte)0, (uint)flags, width, height) != 0;
    }

    /// <summary>The keyboard goes to the next widget (a text field: type into it at once).</summary>
    public void FocusKeyboardHere(int offset = 0)
    {
        var t = T3;
        if (t != null) t->SetKeyboardFocusHere(offset);
    }

    /// <summary>The widget drawn last gets the gamepad / keyboard focus when its window appears.</summary>
    public void DefaultFocus()
    {
        var t = T3;
        if (t != null) t->SetItemDefaultFocus();
    }

    /// <summary>A rectangle on <paramref name="layer"/> (screen pixels). Thickness 0 fills it, else its outline.</summary>
    public void DrawRect(UiLayer layer, float x0, float y0, float x1, float y1, Color color, float rounding = 0, float thickness = 0)
    {
        var t = T3;
        if (t == null)
        {
            DrawRect(x0, y0, x1, y1, color, rounding, thickness <= 0);
            return;
        }
        t->DrawRectIn((uint)layer, x0, y0, x1, y1, color.Rgba, rounding, thickness);
    }

    /// <summary>Text on <paramref name="layer"/> at (x, y) screen pixels; size 0 = the current font size; wrap &gt; 0
    /// wraps at that width. No shadow.</summary>
    public void DrawText(UiLayer layer, float x, float y, string text, Color color, float size = 0, float wrap = 0)
    {
        var t = T3;
        if (t == null)
        {
            DrawText(x, y, text, color, size);
            return;
        }
        t->DrawTextIn((uint)layer, x, y, color.Rgba, size, wrap, Utf8.Of(text));
    }

    /// <summary>The size of <paramref name="text"/> in the current font (size 0 = current size), wrapped at
    /// <paramref name="wrap"/> when &gt; 0.</summary>
    public (float Width, float Height) MeasureText(string text, float size, float wrap)
    {
        var t = T3;
        if (t == null) return MeasureText(text, size);
        float w, h;
        t->TextSizeEx(Utf8.Of(text), size, wrap, &w, &h);
        return (w, h);
    }

    /// <summary>A colour of the current look until the scope ends: <c>using (ui.PushColor(UiColorSlot.Text, gold)) …</c>.</summary>
    public PopScope PushColor(UiColorSlot slot, Color color)
    {
        var t = T3;
        if (t == null) return new PopScope(0);
        t->PushColor((uint)slot, color.Rgba);
        return new PopScope(1);
    }

    /// <summary>A spacing or rounding of the current look until the scope ends (y is ignored for one-value vars).</summary>
    public PopScope PushVar(UiStyleVar var, float x, float y = 0)
    {
        var t = T3;
        if (t == null) return new PopScope(0);
        t->PushVar((uint)var, x, y);
        return new PopScope(2);
    }

    /// <summary>Ends a <see cref="PushColor"/> (kind 1) or <see cref="PushVar"/> (kind 2).</summary>
    public readonly struct PopScope(int kind) : IDisposable
    {
        public void Dispose()
        {
            if (kind == 1) UiNative.T3->PopColor(1);
            else if (kind == 2) UiNative.T3->PopVar(1);
        }
    }

    /// <summary><paramref name="key"/> went down this frame (only while Kiln has the input). <paramref name="claim"/> takes it
    /// from ImGui's own navigation (Tab then stops moving the focus): claim it every frame you use it.</summary>
    public bool KeyPressed(UiKey key, bool repeat = false, bool claim = false)
    {
        var t = T3;
        return t != null && t->KeyPressed((uint)key, (repeat ? 1u : 0u) | (claim ? 2u : 0u)) != 0;
    }

    /// <summary>The modifier keys held now.</summary>
    public KeyMods HeldMods
    {
        get
        {
            var t = T3;
            return t == null ? KeyMods.None : (KeyMods)(t->KeyMods() & 7);
        }
    }

    /// <summary>Scrolls the current window so the cursor sits at <paramref name="ratio"/> (0 top, 0.5 middle, 1 bottom).</summary>
    public void ScrollHere(float ratio = 0.5f)
    {
        var t = T3;
        if (t != null) t->SetScrollHereY(ratio);
    }
}

/// <summary>A font for <see cref="UiDraw.Font"/> (kiln.ui v4).</summary>
public enum UiFont : uint
{
    /// <summary>The current look's font.</summary>
    Default = 0,
    /// <summary>The game's UI font (regular).</summary>
    Game = 1,
    /// <summary>The game's UI font, bold: what its page titles use.</summary>
    GameBold = 2,
}

public sealed unsafe partial class UiDraw
{
    /// <summary>Text drawn inside the scope uses <paramref name="font"/> at the current size:
    /// <c>using (ui.Font(UiFont.GameBold)) ui.Text("MODS");</c>. <see cref="FontChoice.Pushed"/> is false (and nothing changes)
    /// when that font is not loaded or kiln.ui is older than v4.</summary>
    public FontChoice Font(UiFont font)
    {
        var t = UiNative.T4;
        return new FontChoice(t != null && t->PushFont((uint)font) != 0);
    }

    public readonly struct FontChoice(bool pushed) : IDisposable
    {
        public bool Pushed => pushed;

        public void Dispose()
        {
            if (pushed) UiNative.T4->PopFont();
        }
    }
}
