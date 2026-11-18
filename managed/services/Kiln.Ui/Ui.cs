// SPDX-License-Identifier: MIT
// Kiln.Ui: the in-game overlay for client mods (Forge: Screens, GUI overlays / HUD, toasts). Native side: kiln.ui
// (Dear ImGui on the game's Vulkan presents). Usage: docs/kiln/ui.md.
using System.Text;

namespace Kiln;

/// <summary>
/// Immediate-mode drawing inside a Kiln window or a frame handler (a small wrapper over Dear ImGui). Only valid
/// during the callback it was passed to.
/// </summary>
/// <remarks>Runs on the game's render thread, once per frame while visible. Keep it quick, read game state through
/// snapshots the mod keeps, and hop to the game's threads to act (<c>Client.Post</c> for the client world,
/// <c>Context.Scheduler.Post</c> for the server world).</remarks>
[ClientOnly]
[RenderThread]
public sealed unsafe partial class UiDraw
{
    internal UiDraw() { }

    internal UiNative.Frame Frame;

    private static UiNative.Table* T => UiNative.T;

    /// <summary>Screen size in pixels.</summary>
    public float Width => Frame.Width;
    public float Height => Frame.Height;

    /// <summary>UI scale (DPI); multiply your own pixel sizes by it.</summary>
    public float Scale => Frame.DpiScale;

    /// <summary>Seconds since the previous overlay frame.</summary>
    public float DeltaSeconds => Frame.DeltaSeconds;

    /// <summary>The Kiln menu is open (input belongs to Kiln).</summary>
    public bool MenuOpen => Frame.MenuOpen != 0;

    /// <summary>Kiln captures mouse and keyboard right now.</summary>
    public bool Capturing => Frame.Capturing != 0;

    public ulong FrameNumber => Frame.Number;

    public void Text(string text) => T->Text(Utf8.Of(text));
    public void Text(Color color, string text) => T->TextColored(color.Rgba, Utf8.Of(text));
    public void TextWrapped(string text) => T->TextWrapped(Utf8.Of(text));
    public void TextDisabled(string text) => T->TextDisabled(Utf8.Of(text));
    public bool Button(string label, float width = 0, float height = 0) => T->Button(Utf8.Of(label), width, height) != 0;
    public bool SmallButton(string label) => T->SmallButton(Utf8.Of(label)) != 0;

    public bool Checkbox(string label, ref bool value)
    {
        byte v = value ? (byte)1 : (byte)0;
        var changed = T->Checkbox(Utf8.Of(label), &v) != 0;
        value = v != 0;
        return changed;
    }

    public bool Slider(string label, ref float value, float min, float max)
    {
        var v = value;
        var changed = T->SliderFloat(Utf8.Of(label), &v, min, max) != 0;
        value = v;
        return changed;
    }

    public bool Slider(string label, ref int value, int min, int max)
    {
        var v = value;
        var changed = T->SliderInt(Utf8.Of(label), &v, min, max) != 0;
        value = v;
        return changed;
    }

    [ThreadStatic] private static byte[]? _textBuf;

    private const uint InputReadOnly = 1u << 9;  // ImGuiInputTextFlags_ReadOnly (the ImGui kiln.ui is built with)

    /// <summary>Copies <paramref name="value"/> as UTF-8 into <paramref name="buf"/> (at most <paramref name="maxBytes"/>
    /// bytes, then a NUL). False when ImGui could not show all of it (too long, or a NUL inside): the field then shows
    /// what fits, cut at a character, and must not write it back.</summary>
    private static bool Fill(string value, byte[] buf, int maxBytes)
    {
        var whole = Encoding.UTF8.GetByteCount(value) <= maxBytes;
        int n;
        if (whole) n = Encoding.UTF8.GetBytes(value, buf);
        else Encoding.UTF8.GetEncoder().Convert(value, buf.AsSpan(0, maxBytes), true, out _, out n, out _);
        buf[n] = 0;
        return whole && !value.Contains('\0');
    }

    /// <summary>A text field of <paramref name="maxBytes"/> can edit <paramref name="value"/> whole (its UTF-8 fits and it
    /// holds no NUL, where ImGui would end it); otherwise it shows it cut and read-only.</summary>
    internal static bool Editable(string value, int maxBytes) => Encoding.UTF8.GetByteCount(value) <= maxBytes && !value.Contains('\0');

    /// <summary>A one-line text field editing <paramref name="value"/> (at most <paramref name="maxBytes"/> UTF-8 bytes).
    /// Returns true when the text changed. A value that does not fit (or holds a NUL) is shown cut and read-only, and
    /// <paramref name="value"/> stays as it is.</summary>
    public bool InputText(string label, ref string value, int maxBytes = 256)
    {
        maxBytes = Math.Clamp(maxBytes, 2, 1 << 16);
        if (_textBuf == null || _textBuf.Length < maxBytes + 1) _textBuf = GC.AllocateArray<byte>(maxBytes + 1, pinned: true);
        var whole = Fill(value, _textBuf, maxBytes);
        bool changed;
        fixed (byte* p = _textBuf) changed = T->InputText(Utf8.Of(label), p, (nuint)(maxBytes + 1), whole ? 0 : InputReadOnly) != 0;
        if (!changed || !whole) return false;
        var len = Array.IndexOf(_textBuf, (byte)0, 0, maxBytes + 1);
        value = Encoding.UTF8.GetString(_textBuf, 0, len < 0 ? maxBytes : len);
        return true;
    }

    public bool CollapsingHeader(string label, bool defaultOpen = false) => T->CollapsingHeader(Utf8.Of(label), defaultOpen ? 1u << 5 : 0) != 0;

    /// <summary>A tree node; call <see cref="TreePop"/> when it returns true.</summary>
    public bool TreeNode(string label) => T->TreeNode(Utf8.Of(label)) != 0;
    public void TreePop() => T->TreePop();
    public bool Selectable(string label, bool selected = false) => T->Selectable(Utf8.Of(label), selected ? (byte)1 : (byte)0) != 0;
    public void Separator() => T->Separator();
    public void Separator(string text) => T->SeparatorText(Utf8.Of(text));
    public void SameLine(float offset = 0, float spacing = -1) => T->SameLine(offset, spacing);
    public void Spacing() => T->Spacing();

    /// <summary>Scopes widget ids (needed when the same label appears twice).</summary>
    public IdScope Id(string id)
    {
        T->PushId(Utf8.Of(id));
        return new IdScope();
    }

    public readonly struct IdScope : IDisposable
    {
        public void Dispose() => UiNative.T->PopId();
    }

    public void Progress(float fraction, string? overlay = null, float width = -1, float height = 0) =>
        T->ProgressBar(Math.Clamp(fraction, 0, 1), width, height, Utf8.Of(overlay ?? ""));

    /// <summary>A table; returns false when hidden (then skip the rows). Pair with <see cref="EndTable"/> only when true.</summary>
    public bool BeginTable(string id, params string[] columns)
    {
        if (columns.Length == 0) return false;
        if (T->BeginTable(Utf8.Of(id), columns.Length, (1u << 0) | (1u << 6) | (1u << 7)) == 0) return false;  // resizable, borders, row bg
        foreach (var c in columns) T->TableSetupColumn(Utf8.Of(c), 0, 0);
        T->TableHeadersRow();
        return true;
    }

    /// <summary>Starts a row and fills its cells with <paramref name="cells"/> as text.</summary>
    public void Row(params string[] cells)
    {
        T->TableNextRow();
        foreach (var c in cells)
        {
            T->TableNextColumn();
            T->Text(Utf8.Of(c));
        }
    }

    public void NextRow() => T->TableNextRow();
    public bool NextColumn() => T->TableNextColumn() != 0;
    public void EndTable() => T->EndTable();

    public bool BeginTabBar(string id) => T->BeginTabBar(Utf8.Of(id)) != 0;
    public bool BeginTab(string label) => T->BeginTabItem(Utf8.Of(label)) != 0;
    public void EndTab() => T->EndTabItem();
    public void EndTabBar() => T->EndTabBar();

    /// <summary>A scrolling region (call <see cref="EndChild"/> always).</summary>
    public bool BeginChild(string id, float width = 0, float height = 0) => T->BeginChild(Utf8.Of(id), width, height, 0) != 0;
    public void EndChild() => T->EndChild();

    /// <summary>Shows <paramref name="text"/> as a tooltip while the previous widget is hovered.</summary>
    public void Tooltip(string text)
    {
        if (T->IsItemHovered() != 0) T->SetTooltip(Utf8.Of(text));
    }

    public bool ItemHovered => T->IsItemHovered() != 0;

    /// <summary>True on the frame <paramref name="button"/> went down over the widget drawn last: <c>if (ui.Button("Item"))</c>
    /// answers a left click, <c>if (ui.ItemClicked(UiMouseButton.Right))</c> right after it a right click. Costs nothing
    /// unless called. False when this kiln.ui has no such query (an older Kiln).</summary>
    public bool ItemClicked(UiMouseButton button = UiMouseButton.Left)
    {
        var t = UiNative.T2;
        return t != null && t->IsItemClicked((uint)button) != 0;
    }

    // ---- free drawing on top of everything (pixels) ----
    public void DrawText(float x, float y, string text, Color? color = null, float size = 0) =>
        T->DrawText(x, y, (color ?? Color.White).Rgba, size, Utf8.Of(text));

    public void DrawRect(float x0, float y0, float x1, float y1, Color color, float rounding = 0, bool filled = true) =>
        T->DrawRect(x0, y0, x1, y1, color.Rgba, rounding, filled ? 1 : 0);

    public (float Width, float Height) MeasureText(string text, float size = 0)
    {
        float w, h;
        T->TextSize(Utf8.Of(text), size, &w, &h);
        return (w, h);
    }

    // ---- images, more widgets, looks ----

    /// <summary>Draws an image (<see cref="Ui.LoadImage(byte[])"/>, or an item's icon from <see cref="Ui.ItemImage"/>); 0 = its
    /// own size (scaled by <see cref="Scale"/>).</summary>
    public void Image(UiImage image, float width = 0, float height = 0)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!UiNative.HasGameUi) return;
        float w = width > 0 ? width : image.Width * Scale, h = height > 0 ? height : image.Height * Scale;
        if (image.Item is { } item)
        {
            ItemIconBox(item, w, h);
            return;
        }
        if (image.Handle == 0) return;
        T->Image(image.Handle, w, h);
    }

    /// <summary>
    /// Draws only an image's art (since 0.2.0-dev.29.16), otherwise like <see cref="Image(UiImage, float, float)"/>: the
    /// transparent margin around it is left out and the art is fitted inside
    /// <paramref name="width"/> x <paramref name="height"/>, keeping its aspect ratio, centred. The whole box is still taken.
    /// For the game's item icons (<see cref="Ui.ItemImage"/>), whose art sits small in the middle of a wide margin.
    /// </summary>
    public void ImageTrimmed(UiImage image, float width, float height)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!UiNative.HasGameUi) return;
        float w = width > 0 ? width : image.Width * Scale, h = height > 0 ? height : image.Height * Scale;
        if (image.Item is { } item)
        {
            ItemIconBox(item, w, h, trim: true);
            return;
        }
        if (image.Handle == 0) return;
        if (!UiNative.DrawImageEx(null, image.Handle, UiNative.ImageDrawTrim | UiNative.ImageDrawFit, w, h, 0, 0, 0, 0, out _))
            T->Image(image.Handle, w, h);
    }

    /// <summary>
    /// Draws part of an image (since 0.2.0-dev.29.16): the rectangle (<paramref name="u0"/>, <paramref name="v0"/>) to
    /// (<paramref name="u1"/>, <paramref name="v1"/>) in texture coordinates (0..1 across the image; u1 &lt; u0 flips it)
    /// stretched over <paramref name="width"/> x <paramref name="height"/> (0 = that part's own size, scaled by
    /// <see cref="Scale"/>). A sprite sheet's cell, a crop of a picture.
    /// </summary>
    public void ImagePart(UiImage image, float width, float height, float u0, float v0, float u1, float v1)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!UiNative.HasGameUi) return;
        if (u0 == 0 && v0 == 0 && u1 == 0 && v1 == 0) (u1, v1) = (1, 1);  // as kiln.ui reads it: the whole image
        float du = Math.Abs(u1 - u0), dv = Math.Abs(v1 - v0);
        float w = width > 0 ? width : du * image.Width * Scale, h = height > 0 ? height : dv * image.Height * Scale;
        if (image.Item is not null && !IconOnScreen(w, h))
        {
            T->Dummy(w, h);  // scrolled out of its window: neither loaded nor drawn
            return;
        }
        // an item's icon: the level whose part drawn has about as many pixels as the box
        var handle = image.Item is { } item ? ItemIcons.Handle(item, Math.Max(w, h) / Math.Max(Math.Max(du, dv), 1f / 256), out _, out _) : image.Handle;
        if (handle == 0)
        {
            T->Dummy(w, h);
            return;
        }
        if (!UiNative.DrawImageEx(null, handle, 0, w, h, u0, v0, u1, v1, out _))
            T->Image(handle, w, h);  // a kiln.ui without image_ex: the whole image
    }

    /// <summary>An image as a button; true when clicked. An item's icon (<see cref="Ui.ItemImage"/>) is a button while it
    /// loads too (on its placeholder).</summary>
    public bool ImageButton(string id, UiImage image, float width = 0, float height = 0)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!UiNative.HasGameUi) return false;
        float w = width > 0 ? width : image.Width * Scale, h = height > 0 ? height : image.Height * Scale;
        if (image.Item is not null && !IconOnScreen(w, h))
        {
            T->Dummy(w, h);  // scrolled out of its window: neither loaded nor drawn
            return false;
        }
        var handle = image.Item is { } item ? ItemIcons.Handle(item, Math.Max(w, h), out _, out _) : image.Handle;
        if (handle == 0)
        {
            if (image.Item is not null) return Button(id + "##icon", w, h);  // no overlay image at all: a plain button of the size
            return false;
        }
        return T->ImageButton(Utf8.Of(id), handle, w, h) != 0;
    }

    /// <summary>An image as a button, like <see cref="ImageButton(string, UiImage, float, float)"/>, showing only its art,
    /// fitted inside the image's box (see <see cref="ImageTrimmed"/>; since 0.2.0-dev.29.16).</summary>
    public bool ImageButtonTrimmed(string id, UiImage image, float width, float height)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!UiNative.HasGameUi) return false;
        float w = width > 0 ? width : image.Width * Scale, h = height > 0 ? height : image.Height * Scale;
        if (image.Item is not null && !IconOnScreen(w, h))
        {
            T->Dummy(w, h);
            return false;
        }
        var handle = image.Item is { } item ? ItemIcons.Handle(item, ItemIcons.TrimmedPixels(Math.Max(w, h)), out _, out _) : image.Handle;
        if (handle == 0)
        {
            if (image.Item is not null) return Button(id + "##icon", w, h);
            return false;
        }
        if (UiNative.DrawImageEx(id, handle, UiNative.ImageDrawTrim | UiNative.ImageDrawFit, w, h, 0, 0, 0, 0, out var clicked))
            return clicked;
        return T->ImageButton(Utf8.Of(id), handle, w, h) != 0;
    }

    /// <summary>
    /// Draws the game's own icon of <paramref name="item"/> (since 0.2.0-dev.29.15), <paramref name="size"/> pixels square
    /// (0: 64 times <see cref="Scale"/>), as the inventory and crafting menus show it: a game item's, or a new item's of
    /// kiln.content (its PNG icon once the game shows it, its base item's before). Read from the game's resources at run time
    /// and cached; while it loads, and for an id that is no item, a neutral placeholder of the same size is drawn, so the
    /// layout never jumps. A picture that is not square keeps its shape inside the square.
    /// </summary>
    /// <remarks>Never throws for a bad or unknown id. On a dedicated server, or without the overlay, nothing is drawn.</remarks>
    public void ItemIcon(ItemId item, float size = 0)
    {
        var s = size > 0 ? size : 64 * Scale;
        ItemIconBox(item, s, s);
    }

    /// <summary>
    /// Draws the game's own icon of <paramref name="item"/> like <see cref="ItemIcon(ItemId, float)"/>, but only its art
    /// (since 0.2.0-dev.29.16): the game's icons sit small in the middle of a wide
    /// transparent margin, so at 32-40 pixels the art fills a fraction of the box. Trimmed, the art's own bounds (found once
    /// when the icon is loaded) fill <paramref name="size"/> pixels square, keeping their aspect ratio, centred; a sharper
    /// level of the icon is read for it. The box taken stays <paramref name="size"/> square.
    /// </summary>
    /// <example><c>ui.ItemIconTrimmed(item, 40 * ui.Scale);</c></example>
    public void ItemIconTrimmed(ItemId item, float size)
    {
        var s = size > 0 ? size : 64 * Scale;
        ItemIconBox(item, s, s, trim: true);
    }

    /// <summary>Whether a box of w x h at the cursor shows in the current window (an icon scrolled out of a list is neither
    /// loaded nor drawn). True when the layout calls are missing.</summary>
    private bool IconOnScreen(float w, float h)
    {
        var t3 = UiNative.T3;
        if (t3 == null) return true;
        UiNative.Cursor c = default;
        c.Size = (uint)sizeof(UiNative.Cursor);
        t3->GetCursor(&c);
        return ItemIcons.OnScreen(c.ScreenX, c.ScreenY, w, h, c.WindowX, c.WindowY, c.WindowWidth, c.WindowHeight);
    }

    private void ItemIconBox(ItemId item, float width, float height, bool trim = false)
    {
        if (!UiNative.HasGameUi) return;
        if (!IconOnScreen(width, height))
        {
            T->Dummy(width, height);  // scrolled out of its window: neither loaded nor drawn
            return;
        }
        var px = Math.Max(width, height);
        var handle = ItemIcons.Handle(item, trim ? ItemIcons.TrimmedPixels(px) : px, out var iw, out var ih);
        if (handle == 0)
        {
            T->Dummy(width, height);  // keeps the layout: no overlay image could be made
            return;
        }
        // trimmed: the art's opaque bounds fitted in the box (kiln.ui image_ex); the placeholder has no margin, so it fills it
        if (trim && UiNative.DrawImageEx(null, handle, UiNative.ImageDrawTrim | UiNative.ImageDrawFit, width, height, 0, 0, 0, 0, out _))
            return;
        var t3 = UiNative.T3;
        if (iw > 0 && ih > 0 && iw != ih && t3 != null)
        {
            // fit inside the box, centred, the box itself still taken
            var k = Math.Min(width / iw, height / ih);
            float fw = iw * k, fh = ih * k;
            UiNative.Cursor c = default;
            c.Size = (uint)sizeof(UiNative.Cursor);
            t3->GetCursor(&c);
            t3->SetCursorPos(c.X + (width - fw) / 2, c.Y + (height - fh) / 2);
            T->Image(handle, fw, fh);
            t3->SetCursorPos(c.X, c.Y);
            T->Dummy(width, height);
            return;
        }
        T->Image(handle, width, height);
    }

    /// <summary>A drop-down list; true when <paramref name="index"/> changed.</summary>
    public bool Combo(string label, ref int index, IReadOnlyList<string> items) =>
        items.Count > 0 && Combo(label, ref index, ComboItems(items), items.Count);

    /// <summary>Combo items as ImGui takes them (each ends with a NUL, the list with another): made once, drawn many times.</summary>
    internal static byte[] ComboItems(IReadOnlyList<string> items)
    {
        var sb = new StringBuilder();
        foreach (var i in items) sb.Append(i.Replace('\0', ' ')).Append('\0');
        sb.Append('\0');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    internal bool Combo(string label, ref int index, byte[] items, int count)
    {
        if (!UiNative.HasGameUi || count == 0 || items.Length == 0) return false;
        var v = Math.Clamp(index, 0, count - 1);
        bool changed;
        fixed (byte* p = items) changed = T->Combo(Utf8.Of(label), &v, p) != 0;
        index = v;
        return changed;
    }

    [ThreadStatic] private static byte[]? _multiBuf;

    /// <summary>A multi-line text field (at most <paramref name="maxBytes"/> UTF-8 bytes). A value that does not fit (or
    /// holds a NUL) is shown cut, edits to it are ignored and <paramref name="value"/> stays as it is (this slot has no
    /// read-only mode).</summary>
    public bool InputMultiline(string label, ref string value, int maxBytes = 4096, float width = -1, float height = 0)
    {
        if (!UiNative.HasGameUi) return false;
        maxBytes = Math.Clamp(maxBytes, 2, 1 << 20);
        if (_multiBuf == null || _multiBuf.Length < maxBytes + 1) _multiBuf = GC.AllocateArray<byte>(maxBytes + 1, pinned: true);
        var whole = Fill(value, _multiBuf, maxBytes);
        bool changed;
        fixed (byte* p = _multiBuf) changed = T->InputMultiline(Utf8.Of(label), p, (nuint)(maxBytes + 1), width, height > 0 ? height : 120 * Scale) != 0;
        if (!changed || !whole) return false;
        var len = Array.IndexOf(_multiBuf, (byte)0, 0, maxBytes + 1);
        value = Encoding.UTF8.GetString(_multiBuf, 0, len < 0 ? maxBytes : len);
        return true;
    }

    /// <summary>Empty space.</summary>
    public void Space(float width, float height)
    {
        if (UiNative.HasGameUi) T->Dummy(width, height);
    }

    /// <summary>Switches the look (the player's, the game's, Kiln's) for the windows begun inside the scope:
    /// <c>using (ui.Look(UiStyle.Game)) { ... }</c>. For a mod's own theme: <see cref="Theme(UiTheme)"/>.</summary>
    public LookScope Look(UiStyle style) => PushLookRaw(null, style) ? new LookScope(TopLook) : default;

    /// <summary>Ends a <see cref="Look(UiStyle)"/> or <see cref="Theme(UiTheme)"/>. It pops only its own look, while it is the
    /// one on top: one Kiln already took off (a callback that ended without disposing it) is not popped again.</summary>
    public readonly struct LookScope : IDisposable
    {
        private readonly long _serial;

        internal LookScope(long serial) => _serial = serial;

        public void Dispose()
        {
            if (_serial == 0) return;
            var ui = UiEngine.DrawContext;
            if (ui.TopLook == _serial) ui.PopStyleRaw();
        }
    }

    /// <summary>Position and size of the current window in pixels.</summary>
    public (float X, float Y, float Width, float Height) WindowRect
    {
        get
        {
            if (!UiNative.HasGameUi) return (0, 0, 0, 0);
            float x, y, w, h;
            T->WindowRect(&x, &y, &w, &h);
            return (x, y, w, h);
        }
    }

    /// <summary>Closes the <see cref="UiScreen"/> being drawn (inside its draw callback).</summary>
    public void CloseScreen() => CurrentScreen?.Close();

    /// <summary>The <see cref="UiScreen"/> being drawn, if any.</summary>
    public UiScreen? CurrentScreen { get; internal set; }

    internal void PushStyleRaw(UiStyle style) => PushLookRaw(null, style);

    internal void PopStyleRaw()
    {
        if (LookDepth == 0) return;
        if (UiNative.HasGameUi) T->PopStyle();
        LookPopped();
    }

    internal void FocusNextWindow()
    {
        if (UiNative.HasGameUi) T->SetNextWindowFocus();
    }

    internal bool BeginWindowNoClose(string title, uint flags) => T->Begin(Utf8.Of(title), null, flags) != 0;

    // low-level window calls (used by UiWindow and the Kiln menu)
    internal bool BeginWindow(string title, ref bool open, uint flags)
    {
        byte o = open ? (byte)1 : (byte)0;
        var visible = T->Begin(Utf8.Of(title), &o, flags) != 0;
        open = o != 0;
        return visible;
    }

    internal void EndWindow() => T->End();
    internal void NextWindowPos(float x, float y, uint cond, float px = 0, float py = 0) => T->SetNextWindowPos(x, y, cond, px, py);
    internal void NextWindowSize(float w, float h, uint cond) => T->SetNextWindowSize(w, h, cond);
    internal void NextWindowAlpha(float a) => T->SetNextWindowBgAlpha(a);
}

/// <summary>An RGBA colour.</summary>
public readonly record struct Color(byte R, byte G, byte B, byte A = 255)
{
    public uint Rgba => ((uint)R << 24) | ((uint)G << 16) | ((uint)B << 8) | A;
    public static Color FromRgba(uint rgba) => new((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba);
    public Color WithAlpha(byte a) => this with { A = a };
    public static readonly Color White = new(255, 255, 255), Black = new(0, 0, 0), Gray = new(160, 160, 160),
        Red = new(235, 80, 70), Green = new(110, 210, 110), Yellow = new(240, 210, 90), Orange = new(255, 150, 60),
        Cyan = new(90, 210, 230);
}

/// <summary>Where HUD text sits.</summary>
public enum HudAnchor
{
    TopLeft, TopCenter, TopRight, MiddleLeft, Center, MiddleRight, BottomLeft, BottomCenter, BottomRight,
}

public enum ToastKind
{
    Info, Success, Warning, Error,
}

/// <summary>Options of a Kiln window.</summary>
public sealed record UiWindowOptions
{
    /// <summary>While open, Kiln takes mouse and keyboard from the game (a screen). False: an overlay panel the player
    /// only looks at (no input).</summary>
    public bool Interactive { get; init; } = true;

    public bool StartOpen { get; init; } = true;

    /// <summary>Initial size in pixels at scale 1 (0 = fit the contents).</summary>
    public float Width { get; init; }

    public float Height { get; init; }

    /// <summary>Initial position as a fraction of the screen (0..1), the window's centre placed there; null = ImGui's default.</summary>
    public (float X, float Y)? Position { get; init; }

    public bool AutoResize { get; init; }
    public bool TitleBar { get; init; } = true;

    /// <summary>Background opacity (0..1), null = the theme's.</summary>
    public float? BackgroundAlpha { get; init; }

    /// <summary>The window's look: <see cref="UiStyle.Player"/> (the default: the look the player chose for Kiln's UI),
    /// <see cref="UiStyle.Game"/> or <see cref="UiStyle.Kiln"/>. Since 0.2.0-dev.29.16 (before, windows were drawn in Kiln's
    /// ember look).</summary>
    public UiStyle Style { get; init; } = UiStyle.Player;

    /// <summary>Your own theme for the window (<see cref="Ui.Themes.Define"/>), over <see cref="Style"/>. Since 0.2.0-dev.29.16.</summary>
    public UiTheme? Theme { get; init; }
}

/// <summary>A Kiln window (Forge: a Screen). Draws every frame while open; closes with its X or <see cref="Open"/>.</summary>
public sealed class UiWindow : IDisposable
{
    private static int _ids;

    internal UiWindow(string title, Action<UiDraw> draw, UiWindowOptions options, ModContext? owner)
    {
        Title = title;
        Draw = draw;
        Options = options;
        Owner = owner;
        _id = $"###kiln.w{Interlocked.Increment(ref _ids)}";
        Key = title + _id;
        _open = options.StartOpen && UiNative.DrawingOffReason == null;
    }

    private readonly string _id;

    public string Title { get; private set; }
    public UiWindowOptions Options { get; }
    internal Action<UiDraw> Draw { get; }
    internal ModContext? Owner { get; }
    internal string Key { get; private set; }

    /// <summary>Kiln's own windows: a new title in the language Kiln's UI shows (KilnText.Changed); the ImGui id stays, so
    /// the window keeps its place and size. Any thread.</summary>
    internal void Retitle(string title)
    {
        Title = title;
        Key = title + _id;
    }
    internal bool Disposed;
    private volatile bool _open;

    /// <summary>Shown right now. Setting it opens or closes the window (any thread).</summary>
    [AnyThread]
    public bool Open
    {
        get => _open;
        set
        {
            if (value && UiNative.DrawingOffReason != null) return;
            if (_open == value) return;
            _open = value;
            UiEngine.Recount();
        }
    }

    public void Show() => Open = true;
    public void Hide() => Open = false;
    public void Toggle() => Open = !Open;

    /// <summary>Removes the window for good (also done when the owning mod unloads).</summary>
    public void Dispose() => UiEngine.Remove(this);

    internal void SetOpenFromUi(bool open)
    {
        if (_open == open) return;
        _open = open;
        UiEngine.Recount();
    }
}

/// <summary>A HUD line created by <c>Hud.Show</c>.</summary>
public sealed class HudLine : IDisposable
{
    internal HudLine(string id, HudAnchor anchor, ModContext? owner)
    {
        Id = id;
        Anchor = anchor;
        Owner = owner;
    }

    public string Id { get; }
    public HudAnchor Anchor { get; set; }
    public Color Color { get; set; } = Color.White;

    /// <summary>Text shown (any thread).</summary>
    [AnyThread]
    public string Text { get; set; } = "";

    /// <summary>When set, called every frame for the text instead of <see cref="Text"/> (render thread).</summary>
    public Func<string>? Provider { get; set; }

    internal ModContext? Owner { get; }
    internal bool Disposed;

    public void Dispose() => Hud.Remove(this);
}

/// <summary>Text on the game's screen and short pop-up messages (Forge: GUI overlays, toasts).</summary>
[ClientOnly]
public static class Hud
{
    /// <summary>Shows (or returns the existing) line <paramref name="id"/> at <paramref name="anchor"/>.</summary>
    public static HudLine Show(string id, string text = "", HudAnchor anchor = HudAnchor.TopLeft, Color? color = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        UiEngine.EnsureStarted();
        var line = UiEngine.AddHud(id, anchor);
        line.Text = text;
        if (color is { } c) line.Color = c;
        return line;
    }

    /// <summary>A line whose text is computed every frame (render thread).</summary>
    public static HudLine Show(string id, Func<string> text, HudAnchor anchor = HudAnchor.TopLeft, Color? color = null)
    {
        var line = Show(id, "", anchor, color);
        line.Provider = text;
        return line;
    }

    public static void Remove(HudLine line) => UiEngine.RemoveHud(line);

    public static void Remove(string id)
    {
        if (UiEngine.FindHud(id) is { } l) UiEngine.RemoveHud(l);
    }

    /// <summary>A message in the corner of the screen for <paramref name="seconds"/> (any thread).</summary>
    [AnyThread]
    public static void Toast(string text, ToastKind kind = ToastKind.Info, double seconds = 4)
    {
        UiEngine.EnsureStarted();
        UiEngine.AddToast(text, kind, seconds);
    }
}

/// <summary>Kiln's overlay: windows, per-frame drawing, the Kiln menu; the game's screens (<see cref="Screens"/>), entries
/// in its menus (<see cref="Menus"/>), crafting menus (<see cref="Crafting"/>) and full mod screens (<see cref="Screen"/>).</summary>
[ClientOnly]
public static partial class Ui
{
    /// <summary>The overlay exists in this process (a game client with a supported renderer).</summary>
    public static bool Available => UiNative.Available;

    /// <summary>Creates a window drawn by <paramref name="draw"/> every frame while open. Owned by the calling mod
    /// (removed when it unloads).</summary>
    public static UiWindow Window(string title, Action<UiDraw> draw, UiWindowOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(draw);
        UiEngine.EnsureStarted();
        return UiEngine.AddWindow(new UiWindow(title, draw, options ?? new UiWindowOptions(), UiModCalls.Caller));
    }

    /// <summary>Every overlay frame (render thread). While someone listens the overlay draws every frame.</summary>
    public static event Action<UiDraw>? Frame
    {
        add
        {
            UiEngine.EnsureStarted();
            UiEngine.FrameHandlers.Add(value);
            UiEngine.Recount();
        }
        remove
        {
            UiEngine.FrameHandlers.Remove(value);
            UiEngine.Recount();
        }
    }

    /// <summary>The Kiln menu (mods, network, keys). Toggled by the <c>kiln.menu</c> key binding (Insert by default).</summary>
    public static bool MenuOpen
    {
        get => UiNative.ReadStatus() is { MenuOpen: 1 };
        set
        {
            if (UiNative.Available)
                unsafe { UiNative.T->SetMenu(value ? 1 : 0); }
        }
    }

    /// <summary>Overlay diagnostics.</summary>
    public static UiStatus Status
    {
        get
        {
            if (UiNative.ReadStatus() is not { } s) return new UiStatus(false, false, false, false, 0, 0, 0, 0, 0, 0, 1, "kiln.ui unavailable");
            return new UiStatus(s.Hooked != 0, s.Device != 0, s.Swapchain != 0, s.Window != 0, s.Width, s.Height, s.Format,
                (long)s.Presents, (long)s.FramesDrawn, s.FrameMs, s.DpiScale, UiNative.ErrorText(s));
        }
    }

    /// <summary>Adds a page to the Kiln menu's config tab for <paramref name="modId"/> (the seam for the config
    /// registry: a config editor registers here). Owned by the calling mod.</summary>
    public static IDisposable AddConfigPage(string modId, Action<UiDraw> draw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        ArgumentNullException.ThrowIfNull(draw);
        UiEngine.EnsureStarted();
        return UiEngine.AddConfigPage(modId, draw);
    }
}

/// <summary>A PNG image for Kiln windows (<see cref="UiDraw.Image(UiImage, float, float)"/>); Dispose frees it (also when the owning mod unloads).
/// An item's icon (<see cref="Ui.ItemImage"/>) is shared and owned by Kiln: its Dispose does nothing.</summary>
public sealed class UiImage : IDisposable
{
    internal ulong Handle;
    internal ModContext? Owner;
    public int Width { get; internal init; }
    public int Height { get; internal init; }

    /// <summary>The item whose game icon this image draws (<see cref="Ui.ItemImage"/>), or null for a mod's own picture.
    /// Since 0.2.0-dev.29.15.</summary>
    public ItemId? Item { get; internal init; }

    public unsafe void Dispose()
    {
        if (Handle == 0) return;
        if (UiNative.HasGameUi) UiNative.T->ImageFree(Handle);
        Handle = 0;
    }
}

public static unsafe partial class Ui
{
    /// <summary>A PNG (8-bit RGBA/RGB/grey, up to 4096x4096) as an image for Kiln windows; null when it is not a PNG or
    /// there is no overlay.</summary>
    public static UiImage? LoadImage(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (!UiNative.HasGameUi) return null;
        ulong h;
        fixed (byte* p = png) h = UiNative.T->ImageLoad(0, p, (nuint)png.Length);
        if (h == 0) return null;
        uint w = 0, hh = 0;
        UiNative.T->ImageSize(h, &w, &hh);
        var img = new UiImage { Handle = h, Owner = UiModCalls.Caller, Width = (int)w, Height = (int)hh };
        UiEngine.TrackImage(img);
        return img;
    }

    /// <summary>A PNG file (absolute, or relative to the calling mod's folder).</summary>
    public static UiImage? LoadImage(string path)
    {
        var full = Path.IsPathRooted(path) ? path : Path.Combine(ModContext.CurrentMod?.Info.Directory ?? AppContext.BaseDirectory, path);
        return File.Exists(full) ? LoadImage(File.ReadAllBytes(full)) : null;
    }

    /// <summary>
    /// The game's own icon of <paramref name="item"/> as an image for <see cref="UiDraw.Image(UiImage, float, float)"/>, <see cref="UiDraw.ImageButton(string, UiImage, float, float)"/>
    /// and <see cref="RenderDraw.Image(UiImage, float, float, float, float, Color?)"/> (since 0.2.0-dev.29.15): the picture the inventory and crafting menus show for it,
    /// read from the game's resources at run time (Kiln ships no game art). Works for the game's items and for kiln.content's
    /// new items (their PNG icon once the game shows it, their base item's before). One object per item, shared and kept by
    /// Kiln (its Dispose does nothing): keep it, or ask again each frame; both are cheap. Its <see cref="UiImage.Width"/> and
    /// <see cref="UiImage.Height"/> are 64, the size an <c>Image(img)</c> without a size draws (times the UI scale).
    /// </summary>
    /// <returns>Null on a dedicated server and where the overlay cannot draw (an older kiln.ui, drawing off). An unknown id
    /// still gets an image: it draws the neutral placeholder.</returns>
    /// <remarks>Any thread. The icon is loaded off the render thread the first time it is drawn: the first frames show a
    /// placeholder of the same size. Kiln keeps at most 512 icons (24 MiB); icons not drawn for a second are dropped first
    /// past that, and loaded again when drawn. See also <see cref="UiDraw.ItemIconTrimmed"/> (only the art).</remarks>
    [AnyThread]
    public static UiImage? ItemImage(ItemId item) => ItemIcons.View(item);

    /// <summary>Whether <see cref="ItemImage"/> draws the game's icons here (a game client whose overlay draws, with this
    /// Kiln's kiln.ui and kiln.assets). False: item images draw the placeholder, or nothing on a server. Since
    /// 0.2.0-dev.29.15.</summary>
    [AnyThread]
    public static bool ItemIconsAvailable => ItemIcons.Available;
}

public readonly record struct UiStatus(bool Hooked, bool Device, bool Swapchain, bool Window, uint Width, uint Height,
    uint Format, long Presents, long FramesDrawn, double FrameMs, float Scale, string Error);
