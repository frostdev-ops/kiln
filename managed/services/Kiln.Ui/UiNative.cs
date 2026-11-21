// SPDX-License-Identifier: MIT
// The native overlay kiln.ui (native/core/abi/services/kiln_ui.h).
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln;

internal static unsafe class UiNative
{
    public const int Ok = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public uint Size;
        public byte Hooked, Device, Swapchain, Window, MenuOpen, Capturing, Drawing, Wine;
        public uint Width, Height, Format, ColorSpace;
        public float DpiScale;
        public uint Subscribers;
        public ulong Presents, FramesDrawn, Swapchains;
        public double FrameMs;
        public fixed byte Error[128];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Frame
    {
        public uint Size;
        public float Width, Height, DpiScale, DeltaSeconds;
        public byte MenuOpen, Capturing, R0, R1;
        public ulong Number;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Key
    {
        public uint Size, Vk, Scan, Mods;
        public byte Down, Repeat, TextInput, Captured;
    }

    /// <summary>KilnUiScreen (kiln_ui.h).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Screen
    {
        public uint Size;
        public byte UiState, HudScreen, Crafting, Available;
        public uint Interaction, Station, Workshop, Recipe, Language, UiThread;
        public ulong Frame;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<Status*, int> Status;
        public delegate* unmanaged[Cdecl]<ulong, delegate* unmanaged[Cdecl]<Frame*, void*, void>, void*, ulong*, int> SubscribeFrame;
        public delegate* unmanaged[Cdecl]<ulong, delegate* unmanaged[Cdecl]<Key*, void*, byte>, void*, ulong*, int> SubscribeKey;
        public delegate* unmanaged[Cdecl]<ulong, int> Unsubscribe;
        public delegate* unmanaged[Cdecl]<uint, void> SetDemand;
        public delegate* unmanaged[Cdecl]<uint, void> SetCapture;
        public delegate* unmanaged[Cdecl]<int, void> SetMenu;
        public delegate* unmanaged[Cdecl]<uint, uint, void> SetMenuKey;
        public delegate* unmanaged[Cdecl]<uint, byte*, nuint, void> KeyName;
        public delegate* unmanaged[Cdecl]<NativeStr, byte*, uint, byte> Begin;
        public delegate* unmanaged[Cdecl]<void> End;
        public delegate* unmanaged[Cdecl]<NativeStr, void> Text;
        public delegate* unmanaged[Cdecl]<uint, NativeStr, void> TextColored;
        public delegate* unmanaged[Cdecl]<NativeStr, void> TextWrapped;
        public delegate* unmanaged[Cdecl]<NativeStr, void> TextDisabled;
        public delegate* unmanaged[Cdecl]<NativeStr, float, float, byte> Button;
        public delegate* unmanaged[Cdecl]<NativeStr, byte> SmallButton;
        public delegate* unmanaged[Cdecl]<NativeStr, byte*, byte> Checkbox;
        public delegate* unmanaged[Cdecl]<NativeStr, float*, float, float, byte> SliderFloat;
        public delegate* unmanaged[Cdecl]<NativeStr, int*, int, int, byte> SliderInt;
        public delegate* unmanaged[Cdecl]<NativeStr, byte*, nuint, uint, byte> InputText;
        public delegate* unmanaged[Cdecl]<NativeStr, uint, byte> CollapsingHeader;
        public delegate* unmanaged[Cdecl]<NativeStr, byte> TreeNode;
        public delegate* unmanaged[Cdecl]<void> TreePop;
        public delegate* unmanaged[Cdecl]<NativeStr, byte, byte> Selectable;
        public delegate* unmanaged[Cdecl]<void> Separator;
        public delegate* unmanaged[Cdecl]<NativeStr, void> SeparatorText;
        public delegate* unmanaged[Cdecl]<float, float, void> SameLine;
        public delegate* unmanaged[Cdecl]<void> Spacing;
        public delegate* unmanaged[Cdecl]<NativeStr, void> PushId;
        public delegate* unmanaged[Cdecl]<void> PopId;
        public delegate* unmanaged[Cdecl]<float, float, uint, float, float, void> SetNextWindowPos;
        public delegate* unmanaged[Cdecl]<float, float, uint, void> SetNextWindowSize;
        public delegate* unmanaged[Cdecl]<float, void> SetNextWindowBgAlpha;
        public delegate* unmanaged[Cdecl]<float, float, float, NativeStr, void> ProgressBar;
        public delegate* unmanaged[Cdecl]<NativeStr, int, uint, byte> BeginTable;
        public delegate* unmanaged[Cdecl]<NativeStr, uint, float, void> TableSetupColumn;
        public delegate* unmanaged[Cdecl]<void> TableHeadersRow;
        public delegate* unmanaged[Cdecl]<void> TableNextRow;
        public delegate* unmanaged[Cdecl]<byte> TableNextColumn;
        public delegate* unmanaged[Cdecl]<void> EndTable;
        public delegate* unmanaged[Cdecl]<NativeStr, byte> BeginTabBar;
        public delegate* unmanaged[Cdecl]<NativeStr, byte> BeginTabItem;
        public delegate* unmanaged[Cdecl]<void> EndTabItem;
        public delegate* unmanaged[Cdecl]<void> EndTabBar;
        public delegate* unmanaged[Cdecl]<NativeStr, float, float, uint, byte> BeginChild;
        public delegate* unmanaged[Cdecl]<void> EndChild;
        public delegate* unmanaged[Cdecl]<byte> IsItemHovered;
        public delegate* unmanaged[Cdecl]<NativeStr, void> SetTooltip;
        public delegate* unmanaged[Cdecl]<float, float, uint, float, NativeStr, void> DrawText;
        public delegate* unmanaged[Cdecl]<float, float, float, float, uint, float, int, void> DrawRect;
        public delegate* unmanaged[Cdecl]<NativeStr, float, float*, float*, void> TextSize;
        public delegate* unmanaged[Cdecl]<uint*, uint> TakePressedKey;
        // ---- later slots (check Has(...) first) ----
        public delegate* unmanaged[Cdecl]<Screen*, int> GetScreen;
        public delegate* unmanaged[Cdecl]<ulong, delegate* unmanaged[Cdecl]<Screen*, Screen*, void*, void>, void*, ulong*, int> SubscribeScreen;
        public delegate* unmanaged[Cdecl]<ulong, delegate* unmanaged[Cdecl]<Screen*, void*, void>, void*, ulong*, int> SubscribeUiTick;
        public delegate* unmanaged[Cdecl]<ulong, uint, int, NativeStr, delegate* unmanaged[Cdecl]<ulong, void*, void>, void*, ulong*, int> MenuAdd;
        public delegate* unmanaged[Cdecl]<ulong, NativeStr, uint, int> MenuSet;
        public delegate* unmanaged[Cdecl]<uint, void> PushStyle;
        public delegate* unmanaged[Cdecl]<void> PopStyle;
        public delegate* unmanaged[Cdecl]<ulong, void*, nuint, ulong> ImageLoad;
        public delegate* unmanaged[Cdecl]<ulong, uint*, uint*, byte> ImageSize;
        public delegate* unmanaged[Cdecl]<ulong, float, float, void> Image;
        public delegate* unmanaged[Cdecl]<NativeStr, ulong, float, float, byte> ImageButton;
        public delegate* unmanaged[Cdecl]<ulong, void> ImageFree;
        public delegate* unmanaged[Cdecl]<void> SetNextWindowFocus;
        public delegate* unmanaged[Cdecl]<NativeStr, int*, byte*, byte> Combo;
        public delegate* unmanaged[Cdecl]<NativeStr, byte*, nuint, float, float, byte> InputMultiline;
        public delegate* unmanaged[Cdecl]<float, float, void> Dummy;
        public delegate* unmanaged[Cdecl]<float*, float*, float*, float*, void> WindowRect;
        public delegate* unmanaged[Cdecl]<uint, void> SetHoverCapture;
    }

    /// <summary>KilnUiWheel (kiln_ui.h v2): a radial wheel's definition; the strings are copied by kiln.ui during the call.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct WheelDef
    {
        public uint Size, Flags;
        public NativeStr Title;
        public NativeStr* Items;
        public uint Count, HoldKey;
    }

    /// <summary>The whole kiln.ui v2 table: <see cref="Table"/>'s slots, the three pre-overlay slots Kiln.Ui does not use, then
    /// the v2 ones (is_item_clicked and the radial wheel). Only a kiln.ui whose table reaches the end has it (version 2).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Table2
    {
        public Table Base;
        public nint SubscribePreOverlay, SetPreOverlayDemand, AddSwapchainUsage;
        public delegate* unmanaged[Cdecl]<uint, byte> IsItemClicked;
        public delegate* unmanaged[Cdecl]<ulong, WheelDef*, delegate* unmanaged[Cdecl]<int, void*, void>, void*, ulong*, int> WheelOpen;
        public delegate* unmanaged[Cdecl]<ulong, int> WheelClose;
        public delegate* unmanaged[Cdecl]<byte> WheelActive;
    }

    /// <summary>KilnUiCursor (kiln_ui.h v3).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Cursor
    {
        public uint Size;
        public float X, Y, ScreenX, ScreenY, AvailWidth, AvailHeight, WindowX, WindowY, WindowWidth, WindowHeight;
        public float FontSize, LineHeight, ScrollY, ScrollMaxY;
    }

    /// <summary>KilnUiItem (kiln_ui.h v3).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Item
    {
        public uint Size;
        public float MinX, MinY, MaxX, MaxY;
        public byte Hovered, Active, Focused, Clicked, Visible, Edited, Activated, Deactivated;
    }

    /// <summary>The whole kiln.ui v3 table: <see cref="Table2"/>, then the layout and look slots for full-display screens.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Table3
    {
        public Table2 Base;
        public delegate* unmanaged[Cdecl]<float, void> PushFontSize;
        public delegate* unmanaged[Cdecl]<void> PopFontSize;
        public delegate* unmanaged[Cdecl]<NativeStr, float, float, uint, uint, byte> BeginChildEx;
        public delegate* unmanaged[Cdecl]<float, void> SetNextItemWidth;
        public delegate* unmanaged[Cdecl]<Cursor*, void> GetCursor;
        public delegate* unmanaged[Cdecl]<float, float, void> SetCursorPos;
        public delegate* unmanaged[Cdecl]<Item*, void> GetItem;
        public delegate* unmanaged[Cdecl]<NativeStr, byte, uint, float, float, byte> SelectableEx;
        public delegate* unmanaged[Cdecl]<int, void> SetKeyboardFocusHere;
        public delegate* unmanaged[Cdecl]<void> SetItemDefaultFocus;
        public delegate* unmanaged[Cdecl]<uint, float, float, float, float, uint, float, float, void> DrawRectIn;
        public delegate* unmanaged[Cdecl]<uint, float, float, uint, float, float, NativeStr, void> DrawTextIn;
        public delegate* unmanaged[Cdecl]<NativeStr, float, float, float*, float*, void> TextSizeEx;
        public delegate* unmanaged[Cdecl]<uint, uint, void> PushColor;
        public delegate* unmanaged[Cdecl]<uint, void> PopColor;
        public delegate* unmanaged[Cdecl]<uint, float, float, void> PushVar;
        public delegate* unmanaged[Cdecl]<uint, void> PopVar;
        public delegate* unmanaged[Cdecl]<uint, uint, byte> KeyPressed;
        public delegate* unmanaged[Cdecl]<uint> KeyMods;
        public delegate* unmanaged[Cdecl]<float, void> SetScrollHereY;
    }

    /// <summary>KilnUiMenuPage (kiln_ui.h v4): the Kiln page inside the game's main menu.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MenuPage
    {
        public uint Size;
        public byte Available, State, ErrorPopup, Metrics, OpenedBy;
        public byte R0, R1, R2, R3, R4, R5, R6;
        public ulong UiFrame, OpenedFrame, ShownFrame;
        public float CanvasWidth, CanvasHeight, ViewportX, ViewportY, ViewportWidth, ViewportHeight, PixelsPerUnit;
        public float PadLeft, PadTop, PadRight, PadBottom;
        public float X0, Y0, X1, Y1;
        public uint Reserved2;
    }

    /// <summary>The whole kiln.ui v4 table: <see cref="Table3"/>, then the main-menu page and the font choice.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Table4
    {
        public Table3 Base;
        public delegate* unmanaged[Cdecl]<MenuPage*, int> GetMenuPage;
        public delegate* unmanaged[Cdecl]<void> MenuPageDrawn;
        public delegate* unmanaged[Cdecl]<void> MenuPageLeave;
        public delegate* unmanaged[Cdecl]<uint, void> MenuPageHost;
        public delegate* unmanaged[Cdecl]<uint, byte> PushFont;
        public delegate* unmanaged[Cdecl]<void> PopFont;
    }

    /// <summary>KilnUiMenuEntry (kiln_ui.h v6): one entry a mod added to the game's menus.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MenuEntryInfo
    {
        public uint Size, Menu;
        public ulong Entry;
        public byte* Text;
        public uint TextLength, Flags;
        public int Order;
        public uint Drawn;
    }

    /// <summary>The whole kiln.ui v6 table: <see cref="Table4"/>, the window input records (v5, not used here), then the
    /// mods' entries in the game's menus.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Table6
    {
        public Table4 Base;
        public void* SubscribeWindowInput;
        public delegate* unmanaged[Cdecl]<uint, void> MenuShown;
        public delegate* unmanaged[Cdecl]<MenuEntryInfo*, uint, uint> MenuEntries;
        public delegate* unmanaged[Cdecl]<ulong, int> MenuActivate;
    }

    /// <summary>The v6 table with its last slot, menu_scroll (added after the first v6 tables: read it only when the
    /// table's size holds it, <see cref="MenuScrollSlot"/>).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Table6Scroll
    {
        public Table6 Base;
        public delegate* unmanaged[Cdecl]<uint> MenuScroll;
    }

    /// <summary>kiln.ui's menu_scroll, or null on a v6 table without it (a mixed install: the rule applies, nothing
    /// scrolls).</summary>
    public static delegate* unmanaged[Cdecl]<uint> MenuScrollSlot
    {
        get
        {
            var t = T6;
            if (t == null || t->Base.Base.Base.Base.Header.Size < (uint)sizeof(Table6Scroll)) return null;
            return ((Table6Scroll*)t)->MenuScroll;
        }
    }

    /// <summary>The kiln.ui v7 table (0.2.0-dev.27): <see cref="Table6Scroll"/>, then subscribe_key_ex (key callbacks
    /// that also get the middle and side mouse buttons and the wheel's notches, <see cref="KeysMouse"/>).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Table7
    {
        public Table6Scroll Base;
        public delegate* unmanaged[Cdecl]<ulong, delegate* unmanaged[Cdecl]<Key*, void*, byte>, void*, uint, ulong*, int> SubscribeKeyEx;
    }

    // Additive tail after v7; the window-position/size helpers remain internal to Kiln.Ui.
    [StructLayout(LayoutKind.Sequential)]
    public struct TableConstraints
    {
        public Table7 Base;
        public delegate* unmanaged[Cdecl]<float, float, float, float, void> SetNextWindowSizeConstraints;
    }

    /// <summary>The tail after the size constraints (0.2.0-dev.29.11): menu_footer, Kiln's line in the main menu's footer
    /// (<see cref="MainMenuFooter"/>).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TableFooter
    {
        public TableConstraints Base;
        public delegate* unmanaged[Cdecl]<int, uint, void> MenuFooter;
    }

    /// <summary>The tail after menu_footer (0.2.0-dev.29.15): image_load_rgba, an overlay image from pixels (the game's item
    /// icons, <see cref="ItemIcons"/>).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TableImageRgba
    {
        public TableFooter Base;
        public delegate* unmanaged[Cdecl]<ulong, byte*, uint, uint, ulong> ImageLoadRgba;
    }

    /// <summary>kiln.ui's image_load_rgba, or null on a table without it (a kiln-ui.dll older than 0.2.0-dev.29.15).</summary>
    public static delegate* unmanaged[Cdecl]<ulong, byte*, uint, uint, ulong> ImageLoadRgbaSlot
    {
        get
        {
            var t = T;
            if (t == null || t->Header.Size < (uint)sizeof(TableImageRgba)) return null;
            return ((TableImageRgba*)t)->ImageLoadRgba;
        }
    }

    /// <summary>The tail after image_load_rgba (0.2.0-dev.29.15): screen_target, a factory screen's station entity.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TableScreenTarget
    {
        public TableImageRgba Base;
        public delegate* unmanaged[Cdecl]<uint> ScreenTargetFn;
    }

    /// <summary>kiln.ui's screen_target (a factory screen's station entity of the last UI frame, else 0); 0 on an older kiln.ui.
    /// Tests: <see cref="ScreenTargetOverride"/>.</summary>
    public static uint ScreenTarget()
    {
        if (ScreenTargetOverride is { } o) return o();
        var t = T;
        if (t == null || t->Header.Size < (uint)sizeof(TableScreenTarget)) return 0;
        var f = ((TableScreenTarget*)t)->ScreenTargetFn;
        return f == null ? 0 : f();
    }

    internal static Func<uint>? ScreenTargetOverride;

    /// <summary>The tail after screen_target (0.2.0-dev.29.15): image_state, whether an overlay image is drawn yet.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TableImageState
    {
        public TableScreenTarget Base;
        public delegate* unmanaged[Cdecl]<ulong, int> ImageStateFn;
    }

    /// <summary>KilnUiThemeDef (kiln_ui.h, 0.2.0-dev.29.16): a theme's definition, copied by theme_create.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ThemeDef
    {
        public uint Size, Base, RoleMask, VarMask;
        public ulong ColorMask;
        public fixed uint Roles[16];
        public fixed uint Colors[32];
        public fixed float Vars[32];  // [16][2]
        public float Background, Rounding;
        public ulong Font;
    }

    /// <summary>KilnUiLook (kiln_ui.h, 0.2.0-dev.29.16): the player's look.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Look
    {
        public uint Size, Kind;
        public ulong Theme;
        public float Background, Opacity, Scale;
        public uint Reserved;
    }

    /// <summary>KilnUiLookInfo (kiln_ui.h, 0.2.0-dev.29.16): the player's look and a palette.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct LookInfo
    {
        public uint Size, Kind;
        public ulong Theme;
        public float Background, Opacity, Scale;
        public byte GameColors, GameFont, Glyphs, GlyphLanguage;
        public fixed uint Roles[16];
        public uint RoleCount, Reserved2;
    }

    /// <summary>The tail after image_state (0.2.0-dev.29.16): looks, themes and fonts.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TableLooks
    {
        public TableImageState Base;
        public delegate* unmanaged[Cdecl]<ulong, ThemeDef*, ulong*, int> ThemeCreate;
        public delegate* unmanaged[Cdecl]<ulong, int> ThemeFree;
        public delegate* unmanaged[Cdecl]<ulong, byte> PushTheme;
        public delegate* unmanaged[Cdecl]<Look*, int> SetLook;
        public delegate* unmanaged[Cdecl]<LookInfo*, int> GetLookInfo;
        public delegate* unmanaged[Cdecl]<ulong, void*, nuint, ulong> FontLoad;
        public delegate* unmanaged[Cdecl]<ulong, void> FontFree;
        public delegate* unmanaged[Cdecl]<ulong, byte> PushFontHandle;
    }

    /// <summary>kiln.ui's looks tail (0.2.0-dev.29.16), or null on a table without all of it (an older kiln-ui.dll). Each
    /// slot is still checked for null by its caller.</summary>
    public static TableLooks* Looks
    {
        get
        {
            if (LooksOverride != null) return LooksOverride;
            var t = T;
            if (t == null || t->Header.Size < (uint)sizeof(TableLooks)) return null;
            return (TableLooks*)t;
        }
    }

    /// <summary>Tests: a table of the looks tail in place of kiln.ui's.</summary>
    internal static TableLooks* LooksOverride;

    /// <summary>KILN_UI_IMAGE_DRAW_TRIM / FIT (kiln_ui.h image_ex, 0.2.0-dev.29.16).</summary>
    public const uint ImageDrawTrim = 1, ImageDrawFit = 2;

    /// <summary>KilnUiImageDraw (kiln_ui.h, 0.2.0-dev.29.16): how image_ex draws an image.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ImageDraw
    {
        public uint Size, Flags;
        public float Width, Height, U0, V0, U1, V1;
    }

    /// <summary>The tail after the looks (0.2.0-dev.29.16): image_ex (part of an image, trimmed images) and
    /// image_opaque_rect.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TableImageEx
    {
        public TableLooks Base;
        public delegate* unmanaged[Cdecl]<NativeStr, ulong, ImageDraw*, byte> ImageEx;
        public delegate* unmanaged[Cdecl]<ulong, uint*, uint*, uint*, uint*, byte> ImageOpaqueRect;
    }

    /// <summary>kiln.ui's image_ex tail (0.2.0-dev.29.16), or null on a table without it (an older kiln-ui.dll). Each slot
    /// is still checked for null by its caller.</summary>
    public static TableImageEx* ImageEx
    {
        get
        {
            if (ImageExOverride != null) return ImageExOverride;
            var t = T;
            if (t == null || t->Header.Size < (uint)sizeof(TableImageEx)) return null;
            return (TableImageEx*)t;
        }
    }

    /// <summary>Tests: a table of the image_ex tail in place of kiln.ui's.</summary>
    internal static TableImageEx* ImageExOverride;

    /// <summary>Draws through image_ex; false when this kiln.ui has no image_ex (the caller draws the old way).</summary>
    public static bool DrawImageEx(string? id, ulong image, uint flags, float width, float height, float u0, float v0, float u1,
        float v1, out bool clicked)
    {
        clicked = false;
        var t = ImageEx;
        if (t == null || t->ImageEx == null) return false;
        var d = new ImageDraw
        {
            Size = (uint)sizeof(ImageDraw), Flags = flags, Width = width, Height = height, U0 = u0, V0 = v0, U1 = u1, V1 = v1,
        };
        clicked = t->ImageEx(id is null ? default : Utf8.Of(id), image, &d) != 0;
        return true;
    }

    /// <summary>kiln.ui's image_opaque_rect: the image's opaque bounds in pixels (the whole image when nothing is opaque).
    /// False: no such image, or a kiln.ui without it.</summary>
    public static bool ImageOpaqueRect(ulong image, out uint x, out uint y, out uint width, out uint height)
    {
        x = y = width = height = 0;
        var t = ImageEx;
        if (t == null || t->ImageOpaqueRect == null) return false;
        uint a, b, c, d;
        if (t->ImageOpaqueRect(image, &a, &b, &c, &d) == 0) return false;
        (x, y, width, height) = (a, b, c, d);
        return true;
    }

    /// <summary>KilnUiTexts (kiln_ui.h, 0.2.0-dev.29.16): Kiln's own texts kiln.ui draws, and the language.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Texts
    {
        public uint Size, Count;
        public NativeStr Language;
        public NativeStr* Keys;
        public NativeStr* Values;
    }

    /// <summary>The tail after image_opaque_rect (0.2.0-dev.29.16): set_texts.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TableTexts
    {
        public TableImageEx Base;
        public delegate* unmanaged[Cdecl]<Texts*, int> SetTexts;
    }

    /// <summary>kiln.ui's set_texts, or null on a table without it (a kiln-ui.dll older than 0.2.0-dev.29.16). Tests:
    /// <see cref="SetTextsOverride"/>.</summary>
    public static delegate* unmanaged[Cdecl]<Texts*, int> SetTextsSlot
    {
        get
        {
            if (SetTextsOverride != null) return SetTextsOverride;
            var t = T;
            if (t == null || t->Header.Size < (uint)sizeof(TableTexts)) return null;
            return ((TableTexts*)t)->SetTexts;
        }
    }

    internal static delegate* unmanaged[Cdecl]<Texts*, int> SetTextsOverride;

    /// <summary>KilnUiGraphics (kiln_ui.h, 0.2.0-dev.29.18): the player's graphics settings (graphics_set).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Graphics
    {
        public uint Size, FpsCap, DlssPreset, DlssScale;
        public float CustomScale;
        public fixed float ModeScale[4];
        public uint Readout;
        /// <summary>(phase B) KILN_UI_UPSCALER_* and KILN_UI_AMD_UPSCALER_* (were reserved; 0: the game's own, AMD's auto).</summary>
        public uint Upscaler, AmdUpscaler;
    }

    /// <summary>KilnUiGraphicsStatus (kiln_ui.h, 0.2.0-dev.29.18): what Kiln's graphics tools do now (graphics_status). 248
    /// bytes with phase B's upscaler fields from offset 72; kiln.ui fills them when the size says they are there.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GraphicsStatus
    {
        public uint Size, Flags, AsideCap, AsideDlss, FpsCap;
        public float RenderedFps, OutputFps, FrameMs;
        public uint OutputWidth, OutputHeight;
        public int DlssQuality;
        public uint DlssPreset, RenderWidth, RenderHeight, DlssOutputWidth, DlssOutputHeight;
        public fixed uint Reserved[2];
        // ---- phase B (KILN_UI_GRAPHICS_STATUS_SIZE_V2): the upscaler behind the game's FSR 3 option ----
        public uint Upscaler, AmdUpscaler, UpscaleRunning, UpscaleReason, UpscaleFlags, AsideUpscale, UpscaleVersion;
        public uint UpscaleRenderWidth, UpscaleRenderHeight, UpscaleOutputWidth, UpscaleOutputHeight;
        public uint Reserved2;
        /// <summary>The reason in English words, UTF-8, NUL-terminated (for logs and tooltips).</summary>
        public fixed byte UpscaleDetail[UpscaleDetailBytes];
    }

    /// <summary>KILN_UI_GRAPHICS_STATUS_SIZE_V1 / _V2: phase A's status, and with phase B's fields.</summary>
    public const uint GraphicsStatusSizeV1 = 72, GraphicsStatusSizeV2 = 248;

    /// <summary>KilnUiGraphicsStatus.upscale_detail's size.</summary>
    public const int UpscaleDetailBytes = 128;

    /// <summary>The tail after set_texts (0.2.0-dev.29.18): graphics_set, graphics_status.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TableGraphics
    {
        public TableTexts Base;
        public delegate* unmanaged[Cdecl]<Graphics*, int> GraphicsSet;
        public delegate* unmanaged[Cdecl]<GraphicsStatus*, int> GraphicsStatusFn;
    }

    /// <summary>The tail after chat_completion (<see cref="TableChat"/>, bisdat's PR #73, Kiln.Ui's own):
    /// subscribe_pointer_press (0.2.0-dev.30: presses on the game's own UI, used by the core's kiln.ui.inventory). Pinned
    /// here so the offsets cannot drift; Kiln.Ui does not call it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct TablePointer
    {
        public TableChat Base;
        public delegate* unmanaged[Cdecl]<ulong, void*, void*, ulong*, int> SubscribePointerPress;
    }

    /// <summary>kiln.ui's graphics tail, or null on a table without it (a kiln-ui.dll older than 0.2.0-dev.29.18). Tests:
    /// <see cref="GraphicsOverride"/>.</summary>
    public static TableGraphics* GraphicsTable
    {
        get
        {
            if (GraphicsOverride != null) return GraphicsOverride;
            var t = T;
            if (t == null || t->Header.Size < (uint)sizeof(TableGraphics)) return null;
            return (TableGraphics*)t;
        }
    }

    /// <summary>Tests: a table of the graphics tail in place of kiln.ui's.</summary>
    internal static TableGraphics* GraphicsOverride;

    /// <summary>Issue #44: internal completion handoff at kiln.ui offset 1032, after the graphics tail.</summary>
    /// <remarks>Native writes text and the native-chat rectangle on its UI
    /// thread. Only managed Kiln.Ui may use this slot. No game pointers cross
    /// threads. Layout: 44-byte header, 16-byte rect, two 257-byte buffers;
    /// total 576 bytes after 2-byte padding.</remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ChatCompletion
    {
        public uint Size, Operation;
        public uint Focused, Action, Cursor, TextLen, ReplacementLen;
        public uint Suggestions, ListOpen, Field, Revision;
        public float X, Y, Width, Height;
        public fixed byte Text[257];
        public fixed byte Replacement[257];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TableChat
    {
        public TableGraphics Base;
        public delegate* unmanaged[Cdecl]<ChatCompletion*, int> ChatCompletion;
    }

    private static delegate* unmanaged[Cdecl]<ChatCompletion*, int> ChatCompletionSlot
    {
        get
        {
            var t = T;
            if (t == null || t->Header.Size < (uint)sizeof(TableChat)) return null;
            return ((TableChat*)t)->ChatCompletion;
        }
    }

    /// <summary>Native read: 1 (snapshot). Null when chat is not focused,
    /// disabled, unsupported or the input rectangle could not be validated.</summary>
    internal static ChatCompletion? ChatInput()
    {
        var slot = ChatCompletionSlot;
        if (slot == null) return null;
        var result = new ChatCompletion { Size = (uint)sizeof(ChatCompletion), Operation = 1 };
        return slot(&result) == Ok && result.Focused != 0 ? result : null;
    }

    // One stable native call site: kiln.ui's hover capture is per caller.
    // Always clear it when suggestions disappear or focus changes.
    internal static void ChatHover(bool hovering)
    {
        var t = T;
        if (t != null) t->SetHoverCapture(hovering ? 1u : 0u);
    }

    // Turning suggestions on names the snapshot (field, revision) they belong to: native refuses a stale one, so a
    // focus loss on the game's UI thread is never undone by this render frame. Turning them off needs no snapshot.
    internal static bool ChatCandidates(bool hasSuggestions, bool active, uint field = 0, uint revision = 0)
    {
        var slot = ChatCompletionSlot;
        if (slot == null) return false;
        var request = new ChatCompletion {
            Size = (uint)sizeof(ChatCompletion), Operation = 2,
            Suggestions = hasSuggestions ? 1u : 0u, ListOpen = active ? 1u : 0u,
            Field = field, Revision = revision,
        };
        return slot(&request) == Ok;
    }

    internal static bool ChatReplace(in ChatCompletion snapshot, string expected, string replacement)
    {
        var slot = ChatCompletionSlot;
        if (slot == null || System.Text.Encoding.UTF8.GetByteCount(expected) > 256 ||
            System.Text.Encoding.UTF8.GetByteCount(replacement) > 256) return false;
        var request = new ChatCompletion {
            Size = (uint)sizeof(ChatCompletion), Operation = 3,
            Field = snapshot.Field, Revision = snapshot.Revision,
        };
        var left = System.Text.Encoding.UTF8.GetBytes(expected);
        var right = System.Text.Encoding.UTF8.GetBytes(replacement);
        request.TextLen = (uint)left.Length;
        request.ReplacementLen = (uint)right.Length;
        for (var i = 0; i < left.Length; ++i) request.Text[i] = left[i];
        for (var i = 0; i < right.Length; ++i) request.Replacement[i] = right[i];
        return slot(&request) == Ok;
    }

    internal static string ChatText(in ChatCompletion snapshot)
    {
        if (snapshot.TextLen > 256) return "";
        // A fixed buffer cannot be used from an unfixed in parameter: copy
        // the blittable value to a local before pinning.
        var copy = snapshot;
        return System.Text.Encoding.UTF8.GetString(copy.Text, (int)copy.TextLen);
    }

    /// <summary>Phase 1c: private read-only reverse-search snapshot.
    /// No pointers to the game's editor or persisted history cross this ABI.
    /// Native layout: twelve uint32, four floats, 257-byte query and
    /// ten UTF-8 match buffers of 1025 bytes (10572 bytes total).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ChatSearch
    {
        public uint Size, Operation, Active, Suppressed, Field, Revision;
        public uint QueryLen, Selected, First, Count, Total, Index;
        public float X, Y, Width, Height;
        public fixed byte Query[257];
        public fixed byte Lines[10 * 1025];
    }

    /// <summary>The issue #44 search slot follows subscribe_pointer_press at offset 1048.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct TableSearch
    {
        public TablePointer Base;
        public delegate* unmanaged[Cdecl]<ChatSearch*, int> ChatSearchFn;
    }

    private static delegate* unmanaged[Cdecl]<ChatSearch*, int> ChatSearchSlot
    {
        get
        {
            var t = T;
            if (t == null || t->Header.Size < (uint)sizeof(TableSearch)) return null;
            return ((TableSearch*)t)->ChatSearchFn;
        }
    }

    // Only new managed Kiln.Ui explicitly enables search key interception.
    // Without this handshake (e.g. a partial DLL overlay), Ctrl+R remains native.
    internal static bool ChatSearchEnable()
    {
        var slot = ChatSearchSlot;
        if (slot == null) return false;
        var request = new ChatSearch { Size = (uint)sizeof(ChatSearch), Operation = 3 };
        return slot(&request) == Ok;
    }

    internal static ChatSearch? ChatSearchInput()
    {
        var slot = ChatSearchSlot;
        if (slot == null) return null;
        var value = new ChatSearch { Size = (uint)sizeof(ChatSearch), Operation = 1 };
        return slot(&value) == Ok ? value : null;
    }

    internal static bool ChatSearchSelect(in ChatSearch snapshot, uint index)
    {
        var slot = ChatSearchSlot;
        if (slot == null || snapshot.Active == 0 || index >= snapshot.Total) return false;
        var request = new ChatSearch {
            Size = (uint)sizeof(ChatSearch), Operation = 2,
            // Op 1 returns the result-list generation in Index. Op 2 echoes
            // it in Selected; Index is now the clicked row. Same ABI layout.
            Field = snapshot.Field, Revision = snapshot.Revision,
            Selected = snapshot.Index, Index = index,
        };
        return slot(&request) == Ok;
    }

    internal static string ChatSearchQuery(in ChatSearch snapshot)
    {
        if (snapshot.QueryLen > 256) return "";
        var value = snapshot;
        return System.Text.Encoding.UTF8.GetString(value.Query, (int)value.QueryLen);
    }

    internal static string ChatSearchLine(in ChatSearch snapshot, int row)
    {
        if (snapshot.Count > 10 || row < 0 || row >= (int)snapshot.Count) return "";
        var value = snapshot;
        var p = value.Lines + row * 1025;
        var n = 0;
        while (n < 1025 && p[n] != 0) n++;
        return System.Text.Encoding.UTF8.GetString(p, n);
    }

    /// <summary>KILN_UI_GLYPHS_* (kiln_ui.h look_info): the overlay's glyphs of a Chinese, Japanese, Korean or Cyrillic language.</summary>
    public const byte GlyphsLatin = 0, GlyphsLoading = 1, GlyphsReady = 2, GlyphsFailed = 3;

    /// <summary>KILN_UI_IMAGE_WAITING / READY / FAILED (kiln_ui.h image_state).</summary>
    public const int ImageUnknown = 0, ImageWaiting = 1, ImageReady = 2, ImageFailed = 3;

    /// <summary>kiln.ui's image_state of an image (<see cref="ImageUnknown"/>, waiting, ready, failed); ready on a kiln.ui
    /// without it (it draws what it has).</summary>
    public static int ImageState(ulong image)
    {
        var t = T;
        if (t == null || t->Header.Size < (uint)sizeof(TableImageState)) return ImageReady;
        var f = ((TableImageState*)t)->ImageStateFn;
        return f == null ? ImageReady : f(image);
    }

    /// <summary>KILN_UI_FOOTER_SHOWN.</summary>
    public const uint FooterShown = 1;

    /// <summary>kiln.ui's menu_footer, or null on a table without it (a kiln-ui.dll older than 0.2.0-dev.29.11).</summary>
    public static delegate* unmanaged[Cdecl]<int, uint, void> MenuFooterSlot
    {
        get
        {
            var t = T;
            if (t == null || t->Header.Size < (uint)sizeof(TableFooter)) return null;
            return ((TableFooter*)t)->MenuFooter;
        }
    }

    public static delegate* unmanaged[Cdecl]<float, float, float, float, void> WindowConstraintsSlot
    {
        get
        {
            var t = T;
            if (t == null || t->Header.Size < (uint)(sizeof(Table7) + sizeof(void*))) return null;
            return ((TableConstraints*)t)->SetNextWindowSizeConstraints;
        }
    }

    /// <summary>KILN_UI_KEYS_MOUSE (kiln_ui.h v7).</summary>
    public const uint KeysMouse = 1;

    /// <summary>kiln.ui's subscribe_key_ex, or null on a table without it (a kiln-ui.dll older than 0.2.0-dev.27).</summary>
    public static delegate* unmanaged[Cdecl]<ulong, delegate* unmanaged[Cdecl]<Key*, void*, byte>, void*, uint, ulong*, int> SubscribeKeyExSlot
    {
        get
        {
            var t = T;
            if (t == null || t->Header.Size < (uint)sizeof(Table7)) return null;
            return ((Table7*)t)->SubscribeKeyEx;
        }
    }

    /// <summary>The key subscription gets mouse presses (kiln.ui v7): key bindings on the mouse can fire.</summary>
    public static bool MouseKeysAvailable { get; private set; }

    /// <summary>KilnCraftMenuApi (kiln_ui.h): kiln.content's crafting-menu extension point.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CraftMenuTable
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<ulong, void*, void*, ulong*, int> Subscribe;
        public delegate* unmanaged[Cdecl]<ulong, uint, int, NativeStr, uint*, uint, ulong*, int> SetAdd;
        public delegate* unmanaged[Cdecl]<ulong, uint, int, NativeStr, uint*, uint, ulong*, int> TabAdd;
        public delegate* unmanaged[Cdecl]<ulong, int> Remove;
        public delegate* unmanaged[Cdecl]<uint, byte*, nuint, nuint*, int> Describe;
    }

    private static CraftMenuTable* _craft;
    private static bool _craftTried;
    public static CraftMenuTable* Craft
    {
        get
        {
            if (!_craftTried)
            {
                _craftTried = true;
                _craft = Services.Get<CraftMenuTable>("kiln.craftmenu", 1);
            }
            return _craft;
        }
    }

    /// <summary>The table has the later slots (an older kiln.ui ends before them).</summary>
    public static bool HasGameUi
    {
        get
        {
            var t = T;
            if (t == null) return false;
            var end = (nuint)((byte*)&t->SetHoverCapture - (byte*)t) + (nuint)sizeof(void*);
            return t->Header.Size >= end;
        }
    }

    private static Table* _table;
    private static Table2* _table2;
    private static Table3* _table3;
    private static Table4* _table4;
    public static Table* T => _table != null ? _table : (_table = Services.Get<Table>("kiln.ui", 1));

    /// <summary>The v2 table (right clicks, the wheel), or null on a kiln.ui without them.</summary>
    public static Table2* T2 => _table2 != null ? _table2 : (_table2 = Services.Get<Table2>("kiln.ui", 2));

    /// <summary>The v3 table (font sizes, child flags, cursor and item queries, layered drawing, key queries), or null on
    /// an older kiln.ui (version below 3, or a table that ends before the v3 slots).</summary>
    public static Table3* T3 => _table3 != null ? _table3 : (_table3 = Services.Get<Table3>("kiln.ui", 3));

    /// <summary>The v4 table (the main-menu page, the font choice), or null on an older kiln.ui.</summary>
    public static Table4* T4 => _table4 != null ? _table4 : (_table4 = Services.Get<Table4>("kiln.ui", 4));

    /// <summary>The v6 table (mods' entries in the game's menus), or null on an older kiln.ui.</summary>
    public static Table6* T6 => _table6 != null ? _table6 : (_table6 = Services.Get<Table6>("kiln.ui", 6));

    private static Table6* _table6;

    /// <summary>Forgets the cached tables (tests swap the backend).</summary>
    internal static void ResetForTests()
    {
        _table = null;
        _table2 = null;
        _table3 = null;
        _table4 = null;
        _table6 = null;
        LooksOverride = null;
        ImageExOverride = null;

        SetTextsOverride = null;
        GraphicsOverride = null;
        MouseKeysAvailable = false;
    }

    public static bool Available => T != null;
    private static ulong _frameHandle, _keyHandle;
    private static readonly object Lock = new();

    public static bool Subscribe()
    {
        lock (Lock)
        {
            if (T == null) return false;
            if (_frameHandle != 0) return true;
            ulong f = 0, k = 0;
            if (T->SubscribeFrame(0, &OnFrame, null, &f) != Ok) return false;
            // keys and, from kiln.ui v7, the mouse buttons and the wheel (subscribe_key gets keyboard keys only)
            var ex = SubscribeKeyExSlot;
            if (ex != null ? ex(0, &OnKey, null, KeysMouse, &k) != Ok : T->SubscribeKey(0, &OnKey, null, &k) != Ok) return false;
            MouseKeysAvailable = ex != null;
            if (ex == null)
                UiEngine.Log(LogLevel.Warn, "this kiln-ui.dll is older than Kiln.Ui (no subscribe_key_ex): key bindings on the mouse " +
                                            "(Mouse4, Ctrl+WheelUp, ...) register but do not fire, and the Keys page takes keys only");
            _frameHandle = f;
            _keyHandle = k;
            return true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnFrame(Frame* f, void* user)
    {
        try
        {
            UiEngine.Frame(*f);
        }
        catch (Exception e)
        {
            UiEngine.Log(LogLevel.Error, $"frame: {e}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte OnKey(Key* k, void* user)
    {
        try
        {
            return UiEngine.Key(*k) ? (byte)1 : (byte)0;
        }
        catch (Exception e)
        {
            UiEngine.Log(LogLevel.Error, $"key: {e}");
            return 0;
        }
    }

    public static Status? ReadStatus()
    {
        if (T == null) return null;
        Status s = default;
        s.Size = (uint)sizeof(Status);
        return T->Status(&s) == Ok ? s : null;
    }

    // status.error is also the native service's degraded-state surface. An absent
    // device during ordinary startup must never be mistaken for drawing switched off.
    internal static string? DrawingOffReason
    {
        get
        {
            if (ReadStatus() is not { } status) return null;
            var text = ErrorText(status);
            return text.StartsWith("the overlay's drawing is off:", StringComparison.Ordinal) ? text : null;
        }
    }

    public static string KeyName(uint vk)
    {
        if (T == null) return $"Key{vk}";
        var buf = stackalloc byte[64];
        T->KeyName(vk, buf, 64);
        var n = 0;
        while (n < 64 && buf[n] != 0) n++;
        return Encoding.UTF8.GetString(buf, n);
    }

    public static string ErrorText(in Status s)
    {
        fixed (byte* p = s.Error)
        {
            var n = 0;
            while (n < 128 && p[n] != 0) n++;
            return Encoding.UTF8.GetString(p, n);
        }
    }
}

/// <summary>UTF-8 copies of strings for native calls, in pinned per-thread buffers (no allocation per call). A pointer
/// stays valid until 8 more strings were encoded on the same thread.</summary>
internal static unsafe class Utf8
{
    [ThreadStatic] private static byte[][]? _bufs;
    [ThreadStatic] private static int _next;

    public static NativeStr Of(string? s)
    {
        s ??= "";
        _bufs ??= new byte[8][];
        var i = _next++ & 7;
        var max = Encoding.UTF8.GetMaxByteCount(s.Length);
        ref var b = ref _bufs[i];
        if (b == null || b.Length < max) b = GC.AllocateArray<byte>(Math.Max(256, max), pinned: true);
        var n = Encoding.UTF8.GetBytes(s, b);
        fixed (byte* p = b) return new NativeStr { Ptr = p, Len = (nuint)n };  // pinned array: the pointer stays valid
    }
}
