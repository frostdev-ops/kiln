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
    public static Table* T => _table != null ? _table : (_table = Services.Get<Table>("kiln.ui", 1));
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
            if (T->SubscribeKey(0, &OnKey, null, &k) != Ok) return false;
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
