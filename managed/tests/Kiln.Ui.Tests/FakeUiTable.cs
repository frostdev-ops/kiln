// SPDX-License-Identifier: MIT
// A kiln.ui v4 table for tests that drive Kiln.Ui's own code (the Keys tab's rebind, the ShroudForge page's text fields)
// without the game: every slot a test does not set is a no-op or answers "nothing", the text field slots answer what the
// test scripts, and every call that matters is recorded. Render thread rules do not apply: there is no ImGui.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln.Ui.Tests;

/// <summary>Tests that swap the backend and drive Kiln.Ui's shared state (the ShroudForge settings writer, the Keys tab, the
/// Kiln menu's page): they run alone, so no other class's writes or backend swap interleave with theirs.</summary>
[Xunit.CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UiSerial
{
    public const string Name = "ui-serial";
}

internal static unsafe class FakeUiTable
{
    public static UiNative.Table4* Table;
    public static bool MenuOpen;
    /// <summary>What the next text field call does to its buffer (null: nothing), and the item state it reports after.</summary>
    public static Func<string, string?>? Type;
    public static UiNative.Item Item;
    /// <summary>Every text field call: (the text it was given, its buffer size, its flags).</summary>
    public static readonly List<(string Text, int Capacity, uint Flags)> Fields = [];
    public static readonly List<string> Texts = [];
    /// <summary>Keys that "went down" this frame for <see cref="UiDraw.KeyPressed"/>.</summary>
    public static readonly HashSet<UiKey> Pressed = [];
    /// <summary>Only the text fields with this label report <see cref="Item"/> (the others, and every other widget, report
    /// nothing): the search box and a setting's field are told apart.</summary>
    public static string FieldLabel = "##v";
    private static bool _lastWasField;

    public sealed class Backend : IKilnBackend
    {
        public readonly List<string> Logs = [];
        public string Version => "test";
        public bool IsTickThread => true;
        public bool HasFeature(string name) => false;
        public void Log(ulong owner, LogLevel level, string text) { lock (Logs) Logs.Add($"{level}: {text}"); }
        public nint Service(string name, uint minVersion) => name == "kiln.ui" && Table != null && minVersion <= 4 ? (nint)Table : 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Status(UiNative.Status* s)
    {
        if (s->Size < (uint)sizeof(UiNative.Status)) return 1;
        *s = default;
        s->Size = (uint)sizeof(UiNative.Status);
        s->MenuOpen = MenuOpen ? (byte)1 : (byte)0;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int NoSubscribe(ulong o, delegate* unmanaged[Cdecl]<UiNative.Frame*, void*, void> f, void* u, ulong* h) => 3;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int NoSubscribeKey(ulong o, delegate* unmanaged[Cdecl]<UiNative.Key*, void*, byte> f, void* u, ulong* h) => 3;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void U32(uint n) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SetMenu(int open) => MenuOpen = open < 0 ? !MenuOpen : open != 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void U32U32(uint a, uint b) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void KeyName(uint vk, byte* buf, nuint cap)
    {
        var name = vk is >= 0x41 and <= 0x5A ? ((char)vk).ToString() : vk is >= 0x70 and <= 0x87 ? "F" + (vk - 0x6F) : "Key" + vk;
        var n = Encoding.UTF8.GetBytes(name, new Span<byte>(buf, (int)cap - 1));
        buf[n] = 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Str(NativeStr s) { lock (Texts) Texts.Add(s.ToString()); }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Void() { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void FloatFloat(float a, float b) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Float(float a) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ColorStr(uint c, NativeStr s) { lock (Texts) Texts.Add(s.ToString()); }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void PushColor(uint slot, uint c) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void PushVar(uint slot, float a, float b) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte FalseStrFloat2(NativeStr s, float a, float b) { _lastWasField = false; return 0; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte FalseStr(NativeStr s) { _lastWasField = false; return 0; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte Checkbox(NativeStr s, byte* v) { _lastWasField = false; return 0; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void GetCursor(UiNative.Cursor* c)
    {
        var size = c->Size;
        *c = default;
        c->Size = size;
        c->AvailWidth = 600;
        c->AvailHeight = 400;
        c->FontSize = 16;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void GetItem(UiNative.Item* it)
    {
        var size = it->Size;
        *it = _lastWasField ? Item : default;
        it->Size = size;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte InputText(NativeStr label, byte* buf, nuint cap, uint flags) => Field(label, buf, cap, flags);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte InputMultiline(NativeStr label, byte* buf, nuint cap, float w, float h) => Field(label, buf, cap, uint.MaxValue);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte KeyPressed(uint key, uint flags) => Pressed.Contains((UiKey)key) ? (byte)1 : (byte)0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint NoMods() => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte BeginChildEx(NativeStr id, float w, float h, uint flags, uint windowFlags) { _lastWasField = false; return 1; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte SelectableEx(NativeStr label, byte selected, uint flags, float w, float h) { _lastWasField = false; return 0; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Int(int n) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DrawRectIn(uint layer, float x0, float y0, float x1, float y1, uint c, float r, float t) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DrawTextIn(uint layer, float x, float y, uint c, float size, float wrap, NativeStr s) { lock (Texts) Texts.Add(s.ToString()); }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void TextSizeEx(NativeStr s, float size, float wrap, float* w, float* h)
    {
        *w = 8 * (float)s.Len;
        *h = 16;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte Hovered() => 0;

    // Every slot a test does not set: does nothing and answers 0 (x64: the caller cleans up any arguments it passed).
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Zero() => 0;

    private static byte Field(NativeStr label, byte* buf, nuint cap, uint flags)
    {
        _lastWasField = label.ToString() == FieldLabel;
        var len = 0;
        while ((nuint)len < cap && buf[len] != 0) len++;
        var text = Encoding.UTF8.GetString(buf, len);
        if (!_lastWasField) return 0;
        Fields.Add((text, (int)cap, flags));
        if (Type?.Invoke(text) is not { } typed) return 0;
        var bytes = Encoding.UTF8.GetBytes(typed);
        var n = Math.Min(bytes.Length, (int)cap - 1);  // ImGui keeps what fits
        bytes.AsSpan(0, n).CopyTo(new Span<byte>(buf, n));
        buf[n] = 0;
        return 1;
    }

    public static void Install()
    {
        Free();
        Table = (UiNative.Table4*)NativeMemory.AllocZeroed((nuint)sizeof(UiNative.Table4));
        ref var t = ref Table->Base.Base.Base;
        t.Header.Size = (uint)sizeof(UiNative.Table4);
        t.Header.Version = 4;
        t.Status = &Status;
        t.SubscribeFrame = &NoSubscribe;
        t.SubscribeKey = &NoSubscribeKey;
        t.SetDemand = &U32;
        t.SetCapture = &U32;
        t.SetHoverCapture = &U32;
        t.SetMenu = &SetMenu;
        t.SetMenuKey = &U32U32;
        t.KeyName = &KeyName;
        t.Text = &Str;
        t.TextWrapped = &Str;
        t.TextDisabled = &Str;
        t.TextColored = &ColorStr;
        t.PushId = &Str;
        t.PopId = &Void;
        t.Button = &FalseStrFloat2;
        t.SmallButton = &FalseStr;
        t.Checkbox = &Checkbox;
        t.InputText = &InputText;
        t.InputMultiline = &InputMultiline;
        t.Dummy = &FloatFloat;
        t.PushStyle = &U32;
        t.PopStyle = &Void;
        ref var t3 = ref Table->Base;
        t3.PushFontSize = &Float;
        t3.PopFontSize = &Void;
        t3.SetNextItemWidth = &Float;
        t3.GetCursor = &GetCursor;
        t3.GetItem = &GetItem;
        t3.PushColor = &PushColor;
        t3.PopColor = &U32;
        t3.PushVar = &PushVar;
        t3.PopVar = &U32;
        t3.KeyPressed = &KeyPressed;
        t3.KeyMods = &NoMods;
        t3.SetCursorPos = &FloatFloat;
        t3.BeginChildEx = &BeginChildEx;
        t3.SelectableEx = &SelectableEx;
        t3.SetKeyboardFocusHere = &Int;
        t3.SetItemDefaultFocus = &Void;
        t3.DrawRectIn = &DrawRectIn;
        t3.DrawTextIn = &DrawTextIn;
        t3.TextSizeEx = &TextSizeEx;
        t3.SetScrollHereY = &Float;
        t.EndChild = &Void;
        t.IsItemHovered = &Hovered;
        t.SetTooltip = &Str;
        t.Separator = &Void;
        var slots = (nint*)((byte*)Table + sizeof(ServiceHeader));
        for (var i = 0; i < (sizeof(UiNative.Table4) - sizeof(ServiceHeader)) / sizeof(nint); i++)
            if (slots[i] == 0) slots[i] = (nint)(delegate* unmanaged[Cdecl]<nint>)&Zero;
        Reset();
    }

    public static void Reset()
    {
        MenuOpen = false;
        Type = null;
        Item = default;
        Pressed.Clear();
        FieldLabel = "##v";
        _lastWasField = false;
        Fields.Clear();
        lock (Texts) Texts.Clear();
    }

    public static void Free()
    {
        if (Table != null) NativeMemory.Free(Table);
        Table = null;
    }
}
