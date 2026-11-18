// SPDX-License-Identifier: MIT
// Looks and themes (0.2.0-dev.29.16, docs/kiln/ui.md "Looks and themes"): the palette of the look in use (UiPalette), mods'
// themes (Ui.Themes.Define) for their own windows, screens and crafting panels, themes a mod offers the player for all of
// Kiln's UI, and mods' fonts (Ui.LoadFont). Native side: kiln.ui's looks tail (kiln_ui.h theme_create ... push_font_handle).
// A theme only applies where its mod puts it; nothing here sets the player's look (PlayerLook does, from the player's
// setting).
using System.Text.RegularExpressions;

namespace Kiln;

/// <summary>A colour role of a look (<see cref="UiPalette"/>, <see cref="UiThemeSpec.Roles"/>). Kiln makes ImGui's colours
/// from them, so a theme that changes <see cref="Accent"/> also changes check marks, slider grabs, the chosen tab's line and
/// the focus outline.</summary>
public enum UiThemeRole : uint
{
    /// <summary>Text.</summary>
    Text = 0,
    /// <summary>Disabled and secondary text.</summary>
    TextMuted = 1,
    /// <summary>Windows, title bars and pop-ups.</summary>
    WindowBackground = 2,
    /// <summary>Child regions and tiles, the chosen tab, table headers.</summary>
    PanelBackground = 3,
    /// <summary>Text fields, check boxes, slider tracks, idle tabs.</summary>
    FrameBackground = 4,
    /// <summary>Outlines, separators, table lines.</summary>
    Border = 5,
    /// <summary>Check marks, slider grabs, the chosen tab's line, the focus outline, a held button.</summary>
    Accent = 6,
    /// <summary>Buttons (hovered: <see cref="Header"/>; held: <see cref="Accent"/>).</summary>
    Button = 7,
    /// <summary>Chosen and hovered rows, collapsing headers.</summary>
    Header = 8,
    /// <summary>A status colour for your own drawing: needs a look.</summary>
    Attention = 9,
    /// <summary>A status colour: a problem.</summary>
    Warning = 10,
    /// <summary>A status colour: fine.</summary>
    Ok = 11,
}

/// <summary>The colours of a look by role (<see cref="UiDraw.Palette"/>): draw your own shapes and text in the look in use.
/// <see cref="WindowBackground"/>'s alpha is the windows' opacity.</summary>
public readonly record struct UiPalette(Color Text, Color TextMuted, Color WindowBackground, Color PanelBackground,
    Color FrameBackground, Color Border, Color Accent, Color Button, Color Header, Color Attention, Color Warning, Color Ok)
{
    /// <summary>The colour of <paramref name="role"/>.</summary>
    public Color this[UiThemeRole role] => role switch
    {
        UiThemeRole.Text => Text,
        UiThemeRole.TextMuted => TextMuted,
        UiThemeRole.WindowBackground => WindowBackground,
        UiThemeRole.PanelBackground => PanelBackground,
        UiThemeRole.FrameBackground => FrameBackground,
        UiThemeRole.Border => Border,
        UiThemeRole.Accent => Accent,
        UiThemeRole.Button => Button,
        UiThemeRole.Header => Header,
        UiThemeRole.Attention => Attention,
        UiThemeRole.Warning => Warning,
        UiThemeRole.Ok => Ok,
        _ => Text,
    };

    internal const int Count = 12;

    internal static UiPalette From(ReadOnlySpan<uint> rgba)
    {
        Span<uint> v = stackalloc uint[Count];
        v.Fill(0xffffffffu);
        rgba[..Math.Min(rgba.Length, Count)].CopyTo(v);
        static Color C(uint c) => Color.FromRgba(c);
        return new UiPalette(C(v[0]), C(v[1]), C(v[2]), C(v[3]), C(v[4]), C(v[5]), C(v[6]), C(v[7]), C(v[8]), C(v[9]), C(v[10]), C(v[11]));
    }

    /// <summary>The game look's stand-in (native/services/ui/look_palette.h look::neutral): what Kiln.Ui draws with when kiln.ui
    /// cannot say (an older kiln-ui.dll, no overlay).</summary>
    internal static readonly UiPalette Neutral = From([
        0xebe8e3ffu, 0xb9bec4abu, 0x1c1c21e6u, 0x393841a9u, 0x1414165au, 0xb8a26affu, 0xdcc27affu, 0x393841ffu, 0x6e5f3cffu,
        0xf2d873ffu, 0xe57373ffu, 0x8fc47fffu,
    ]);
}

/// <summary>The player's choice of look for Kiln's UI (kiln.json <c>ui.look</c>).</summary>
public enum UiLook
{
    /// <summary>The game's look (the default).</summary>
    Game = 0,
    /// <summary>The game's look with see-through window backgrounds (<see cref="UiPlayerLook.Background"/>).</summary>
    Translucent = 1,
    /// <summary>Kiln's classic ember look.</summary>
    Kiln = 2,
    /// <summary>A theme a mod offers (<see cref="UiPlayerLook.ThemeId"/>).</summary>
    Theme = 3,
}

/// <summary>The player's look setting (<see cref="Ui.Themes.Player"/>; read only: the player chooses).</summary>
/// <param name="Look">The look.</param>
/// <param name="ThemeId">With <see cref="UiLook.Theme"/>: the theme's <see cref="UiTheme.Id"/>, else null.</param>
/// <param name="Background">The translucent look's window background opacity (0.1 to 1).</param>
/// <param name="Opacity">Kiln's whole overlay, text included (0.3 to 1).</param>
/// <param name="Scale">The overlay's size on top of the display's scale (0.75 to 1.5).</param>
public readonly record struct UiPlayerLook(UiLook Look, string? ThemeId, float Background, float Opacity, float Scale);

/// <summary>A theme's definition (<see cref="Ui.Themes.Define"/>).</summary>
public sealed record UiThemeSpec
{
    /// <summary>Its name in Kiln's Look list (per language); null: the name given to <see cref="Ui.Themes.Define"/>.</summary>
    public UiText? Name { get; init; }

    /// <summary>What it starts from: <see cref="UiStyle.Game"/> (the game's look, default) or <see cref="UiStyle.Kiln"/>.</summary>
    public UiStyle Base { get; init; } = UiStyle.Game;

    /// <summary>Colours by role over the base's (the alpha is ignored: <see cref="Background"/> is the windows' opacity).</summary>
    public Dictionary<UiThemeRole, Color> Roles { get; init; } = new();

    /// <summary>Single colours (with alpha), set after the roles.</summary>
    public Dictionary<UiColorSlot, Color> Colors { get; init; } = new();

    /// <summary>Spacings and sizes in pixels at UI scale 1 (Kiln applies the scale); the <c>Align</c> ones 0..1. Y is unused
    /// for one-value vars.</summary>
    public Dictionary<UiStyleVar, (float X, float Y)> Vars { get; init; } = new();

    /// <summary>The windows' background opacity, 0..1 (null: the base's).</summary>
    public float? Background { get; init; }

    /// <summary>Every corner (windows, frames, panels, pop-ups, grabs, tabs), pixels at scale 1 (null: the base's).</summary>
    public float? Rounding { get; init; }

    /// <summary>Its windows' text (<see cref="Ui.LoadFont(byte[])"/>; null: the base's font, the game's for the game look).</summary>
    public UiFontFile? Font { get; init; }

    /// <summary>Lists it in Kiln's Look setting, where the player may pick it for all of Kiln's UI. Nothing else changes until
    /// they do.</summary>
    public bool OfferToPlayer { get; init; }
}

/// <summary>A mod's theme (<see cref="Ui.Themes.Define"/>): give it to your windows (<see cref="UiWindowOptions.Theme"/>,
/// <see cref="UiScreenOptions.Theme"/>, <see cref="CraftingPanelOptions.Theme"/>) or use it inside your own drawing
/// (<see cref="UiDraw.Theme(UiTheme)"/>). Owned by the mod that defined it: gone when it unloads.</summary>
public sealed class UiTheme : IDisposable
{
    internal UiTheme(string id, string name, ModContext? owner)
    {
        Id = id;
        Name = name;
        Owner = owner;
    }

    /// <summary><c>&lt;mod id&gt;:&lt;name&gt;</c>: what kiln.json's <c>ui.theme</c> names when the player picks it.</summary>
    public string Id { get; }

    /// <summary>The name given to <see cref="Ui.Themes.Define"/>.</summary>
    public string Name { get; }

    /// <summary>Its name in Kiln's Look list.</summary>
    public UiText DisplayName { get; internal set; } = "";

    /// <summary>What it starts from (<see cref="UiStyle.Game"/> or <see cref="UiStyle.Kiln"/>).</summary>
    public UiStyle Base { get; internal set; } = UiStyle.Game;

    /// <summary>Listed in Kiln's Look setting (<see cref="UiThemeSpec.OfferToPlayer"/>), on a game client whose overlay draws.</summary>
    public bool OfferedToPlayer => Offer && Handle != 0;

    /// <summary>Removed (disposed, or its mod unloaded).</summary>
    public bool IsDisposed => _disposed;

    internal ModContext? Owner { get; }
    internal volatile bool Offer;
    internal ulong Handle;  // kiln.ui's theme (0: none here); swapped when the mod defines the name again
    private volatile bool _disposed;

    /// <summary>The mod's name for Kiln's Look list.</summary>
    internal string ModName => Owner?.Info.Name ?? "Kiln";

    /// <summary>Removes the theme (its windows fall back to the player's look; a player who picked it sees the game's look
    /// until it is offered again). Any thread.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UiThemes.Remove(this);
    }
}

/// <summary>A mod's font (<see cref="Ui.LoadFont(byte[])"/>) for its own windows: <c>using (ui.ModFont(font)) ...</c> or a theme's
/// <see cref="UiThemeSpec.Font"/>. Owned by the mod that loaded it: freed when it unloads or by <see cref="Dispose"/>.</summary>
public sealed class UiFontFile : IDisposable
{
    internal UiFontFile() { }

    internal ulong Handle;
    internal ModContext? Owner;

    /// <summary>The size of its file in bytes.</summary>
    public int Bytes { get; internal init; }

    public unsafe void Dispose()
    {
        var h = Interlocked.Exchange(ref Handle, 0);
        if (h == 0) return;
        var t = UiNative.Looks;
        if (t != null && t->FontFree != null) t->FontFree(h);
        UiThemes.ForgetFont(this);
    }
}

/// <summary>The registry of mods' themes and fonts (Kiln.Ui's side of kiln.ui's looks tail).</summary>
internal static unsafe partial class UiThemes
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, UiTheme> Themes = new(StringComparer.Ordinal);
    private static readonly List<UiFontFile> Fonts = [];
    private static readonly Regex NameRule = new("^[a-z0-9._-]{1,64}$", RegexOptions.CultureInvariant);

    /// <summary>The look's role colours in the order of kiln_ui.h's KILN_UI_ROLE_*.</summary>
    internal static readonly UiThemeRole[] RoleOrder = Enum.GetValues<UiThemeRole>();

    internal static string ModId(ModContext? mod) => mod?.Info.Id ?? "kiln";

    internal static UiTheme Define(string name, UiThemeSpec spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(spec);
        if (!NameRule.IsMatch(name)) throw new ArgumentException("a theme's name is 1 to 64 of a-z, 0-9, '.', '_' and '-'", nameof(name));
        if (spec.Base is not (UiStyle.Game or UiStyle.Kiln)) throw new ArgumentException("a theme's Base is UiStyle.Game or UiStyle.Kiln", nameof(spec));
        if (spec.Background is { } bg && !(bg >= 0 && bg <= 1)) throw new ArgumentException("Background is 0 to 1", nameof(spec));
        if (spec.Rounding is { } r && !(r >= 0 && r <= 64)) throw new ArgumentException("Rounding is 0 to 64 pixels", nameof(spec));
        foreach (var (k, v) in spec.Vars)
            if (!float.IsFinite(v.X) || !float.IsFinite(v.Y)) throw new ArgumentException($"Vars[{k}] is not a number", nameof(spec));
        var owner = UiModCalls.Caller;
        UiModCalls.Watch(owner);
        UiModCalls.RequireAlive(owner);
        var id = ModId(owner) + ":" + name;
        var def = ToNative(spec);
        ulong handle = 0;
        var t = UiNative.Looks;
        if (t != null && t->ThemeCreate != null && UiNative.DrawingOffReason == null)
        {
            ulong h = 0;
            if (t->ThemeCreate(UiModCalls.OwnerId(owner), &def, &h) == UiNative.Ok) handle = h;
            else UiEngine.Log(LogLevel.Warn, $"theme {id}: kiln.ui refused its definition");
        }
        UiTheme theme;
        ulong old = 0;
        lock (Lock)
        {
            if (owner is { Alive: false })
            {
                if (handle != 0 && t != null && t->ThemeFree != null) t->ThemeFree(handle);  // the mod unloads: nothing is kept
                UiModCalls.RequireAlive(owner);
            }
            if (Themes.TryGetValue(id, out var existing) && !existing.IsDisposed && ReferenceEquals(existing.Owner, owner))
            {
                theme = existing;
                old = theme.Handle;
            }
            else
            {
                theme = new UiTheme(id, name, owner);
                Themes[id] = theme;
            }
            theme.Handle = handle;
            theme.DisplayName = spec.Name ?? name;
            theme.Base = spec.Base;
            theme.Offer = spec.OfferToPlayer;
        }
        if (old != 0 && t != null && t->ThemeFree != null) t->ThemeFree(old);
        PlayerLook.ThemesChanged();
        return theme;
    }


    internal static void Remove(UiTheme theme)
    {
        ulong h;
        lock (Lock)
        {
            if (Themes.TryGetValue(theme.Id, out var t) && ReferenceEquals(t, theme)) Themes.Remove(theme.Id);
            h = theme.Handle;
            theme.Handle = 0;
        }
        var tb = UiNative.Looks;
        if (h != 0 && tb != null && tb->ThemeFree != null) tb->ThemeFree(h);
        PlayerLook.ThemesChanged();
    }

    /// <summary>The themes mods offer to the player now, by id.</summary>
    internal static IReadOnlyList<UiTheme> Offered()
    {
        lock (Lock) return Themes.Values.Where(t => t.OfferedToPlayer).OrderBy(t => t.Id, StringComparer.Ordinal).ToArray();
    }

    internal static UiTheme? Find(string id)
    {
        lock (Lock) return Themes.TryGetValue(id, out var t) && !t.IsDisposed ? t : null;
    }

    internal static UiFontFile? LoadFont(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var t = UiNative.Looks;
        if (t == null || t->FontLoad == null || bytes.Length == 0) return null;
        var owner = UiModCalls.Caller;
        UiModCalls.Watch(owner);
        ulong h;
        fixed (byte* p = bytes) h = t->FontLoad(UiModCalls.OwnerId(owner), p, (nuint)bytes.Length);
        if (h == 0) return null;
        var font = new UiFontFile { Handle = h, Owner = owner, Bytes = bytes.Length };
        lock (Lock)
        {
            if (owner is { Alive: false })
            {
                if (t->FontFree != null) t->FontFree(h);
                return null;
            }
            Fonts.Add(font);
        }
        return font;
    }

    internal static void ForgetFont(UiFontFile f)
    {
        lock (Lock) Fonts.Remove(f);
    }

    /// <summary>A mod unloads: its themes and fonts go.</summary>
    internal static void RemoveOwnedBy(ModContext mod)
    {
        UiTheme[] themes;
        UiFontFile[] fonts;
        lock (Lock)
        {
            themes = Themes.Values.Where(t => ReferenceEquals(t.Owner, mod)).ToArray();
            fonts = Fonts.Where(f => ReferenceEquals(f.Owner, mod)).ToArray();
        }
        foreach (var t in themes) t.Dispose();
        foreach (var f in fonts) f.Dispose();
    }

    internal static void ResetForTests()
    {
        lock (Lock)
        {
            Themes.Clear();
            Fonts.Clear();
        }
    }

    /// <summary>kiln.ui's definition of a spec (KilnUiThemeDef).</summary>
    internal static UiNative.ThemeDef ToNative(UiThemeSpec spec)
    {
        UiNative.ThemeDef d = default;
        d.Size = (uint)sizeof(UiNative.ThemeDef);
        d.Base = spec.Base == UiStyle.Kiln ? 0u : 1u;
        foreach (var (role, color) in spec.Roles)
        {
            if ((uint)role >= UiPalette.Count) continue;
            d.Roles[(int)role] = color.Rgba;
            d.RoleMask |= 1u << (int)role;
        }
        foreach (var (slot, color) in spec.Colors)
        {
            if ((uint)slot >= 32) continue;
            d.Colors[(int)slot] = color.Rgba;
            d.ColorMask |= 1ul << (int)slot;
        }
        foreach (var (v, xy) in spec.Vars)
        {
            if ((uint)v >= 16) continue;
            d.Vars[(int)v * 2] = xy.X;
            d.Vars[(int)v * 2 + 1] = xy.Y;
            d.VarMask |= 1u << (int)v;
        }
        d.Background = spec.Background ?? -1;
        d.Rounding = spec.Rounding ?? -1;
        d.Font = spec.Font?.Handle ?? 0;
        return d;
    }
}

public static partial class Ui
{
    /// <summary>Themes for mods' own windows, and the ones mods offer the player (docs/kiln/ui.md "Looks and themes").</summary>
    [ClientOnly]
    public static class Themes
    {
        /// <summary>
        /// Defines (or, for a name this mod defined before, changes in place) a theme: the base look's palette with the spec's
        /// roles, colours, spacing, rounding, background and font. Give it to your own windows, screens and crafting panels, or
        /// use it inside your drawing with <see cref="UiDraw.Theme(UiTheme)"/>. With <see cref="UiThemeSpec.OfferToPlayer"/> it is
        /// also listed in Kiln's Look setting; only the player can pick it for Kiln's UI.
        /// </summary>
        /// <param name="name">1 to 64 of <c>a-z 0-9 . _ -</c>; the theme's <see cref="UiTheme.Id"/> is <c>&lt;mod id&gt;:&lt;name&gt;</c>.</param>
        /// <param name="spec">What it looks like.</param>
        /// <returns>The theme, owned by the calling mod. On a dedicated server, or where the overlay does not draw, it draws
        /// nothing of its own and is never offered.</returns>
        /// <exception cref="ArgumentException">A bad name, a base other than Game or Kiln, a value out of range.</exception>
        [AnyThread]
        public static UiTheme Define(string name, UiThemeSpec spec)
        {
            UiEngine.EnsureStarted();
            return UiThemes.Define(name, spec);
        }

        /// <summary>The themes mods offer to the player now (Kiln's Look setting lists them).</summary>
        [AnyThread]
        public static IReadOnlyList<UiTheme> Offered => UiThemes.Offered();

        /// <summary>The player's look setting (kiln.json <c>ui</c>). Read only: the player chooses it.</summary>
        [AnyThread]
        public static UiPlayerLook Player => PlayerLook.Current.ToPublic();
    }

    /// <summary>A TrueType or OpenType font (TTF or OTF) for your own windows (a theme's <see cref="UiThemeSpec.Font"/>, or
    /// <see cref="UiDraw.ModFont(UiFontFile)"/>). Null when it is no font, on a server or without drawing, or past Kiln's font
    /// budget (8 mod fonts at once, 8 MiB each, 24 MiB together; kiln.log says why). It joins the overlay's font atlas at the
    /// next frame. Since 0.2.0-dev.29.16.</summary>
    [AnyThread]
    public static UiFontFile? LoadFont(byte[] ttf)
    {
        UiEngine.EnsureStarted();
        return UiThemes.LoadFont(ttf);
    }

    /// <summary>A font file (absolute, or relative to the calling mod's folder); null when it is missing (see
    /// <see cref="LoadFont(byte[])"/>).</summary>
    [AnyThread]
    public static UiFontFile? LoadFont(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.IsPathRooted(path) ? path : Path.Combine(ModContext.CurrentMod?.Info.Directory ?? AppContext.BaseDirectory, path);
        var info = new FileInfo(full);
        if (!info.Exists || info.Length > 8 << 20) return null;
        return LoadFont(File.ReadAllBytes(full));
    }
}

public sealed unsafe partial class UiDraw
{
    // Looks pushed through this UiDraw (Look, PushStyleRaw, a window's theme) and not popped yet, each with a serial: Kiln takes
    // off what a mod's callback left before the next window (RestoreLooks), and a scope disposed later pops only its own.
    private static readonly List<long> Looks = [];
    private static ulong _looksFrame;
    private static long _lookSerial;

    /// <summary>Looks pushed now (render thread).</summary>
    internal int LookDepth => _looksFrame == Frame.Number ? Looks.Count : 0;

    /// <summary>The serial of the look on top, 0 for none.</summary>
    internal long TopLook => LookDepth > 0 ? Looks[^1] : 0;

    private void LookPushed()
    {
        if (_looksFrame != Frame.Number)
        {
            _looksFrame = Frame.Number;
            Looks.Clear();  // kiln.ui pops at the end of a frame what was left
        }
        Looks.Add(++_lookSerial);
    }

    private void LookPopped()
    {
        if (LookDepth > 0) Looks.RemoveAt(Looks.Count - 1);
    }

    /// <summary>Takes off the looks pushed after <paramref name="depth"/> (a mod's callback that did not dispose a Look scope).</summary>
    internal void RestoreLooks(int depth)
    {
        while (LookDepth > depth)
        {
            if (UiNative.HasGameUi) T->PopStyle();
            LookPopped();
        }
    }

    /// <summary>Switches to a mod's theme for the windows begun (and the text drawn) inside the scope:
    /// <c>using (ui.Theme(myTheme)) { ... }</c>. A disposed theme, or one kiln.ui cannot draw, switches to the player's look.
    /// Ended like <see cref="Look(UiStyle)"/>.
    /// Since 0.2.0-dev.29.16.</summary>
    public LookScope Theme(UiTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return PushLookRaw(theme, UiStyle.Player) ? new LookScope(TopLook) : default;
    }

    /// <summary>Pushes <paramref name="theme"/> when it draws here, else <paramref name="style"/>; true when something was pushed.</summary>
    internal bool PushLookRaw(UiTheme? theme, UiStyle style)
    {
        if (!UiNative.HasGameUi) return false;
        var t = UiNative.Looks;
        var h = theme?.Handle ?? 0;
        if (h != 0 && t != null && t->PushTheme != null && t->PushTheme(h) != 0)
        {
            LookPushed();
            return true;
        }
        T->PushStyle((uint)style);
        LookPushed();
        return true;
    }

    /// <summary>The colours of the look in use here (the player's outside any <see cref="Look(UiStyle)"/>): draw your own shapes
    /// and text to match it. Since 0.2.0-dev.29.16.</summary>
    public UiPalette Palette => KilnLook.PaletteHere();

    /// <summary>Text drawn inside the scope uses a mod's font at the current size. <see cref="FontChoice.Pushed"/> is false (and
    /// nothing changes) until it joined the atlas (the frame after <see cref="Ui.LoadFont(byte[])"/>), or once disposed.
    /// Since 0.2.0-dev.29.16.</summary>
    public FontChoice ModFont(UiFontFile font)
    {
        ArgumentNullException.ThrowIfNull(font);
        var t = UiNative.Looks;
        var h = font.Handle;
        return new FontChoice(h != 0 && t != null && t->PushFontHandle != null && t->PushFontHandle(h) != 0);
    }
}
