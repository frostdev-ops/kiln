// SPDX-License-Identifier: MIT
// UiWheel: the radial wheel for client mods (EMF's wheel_open as a C# API). Native side: kiln.ui v2's wheel
// (native/services/ui/wheel.cpp; its rules are wheel_math.h). Usage: docs/kiln/ui.md (Radial wheel).
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln;

/// <summary>Options of a <see cref="UiWheel"/>.</summary>
public sealed record UiWheelOptions
{
    /// <summary>Shown in the middle of the wheel (cut at 64 bytes); null = none.</summary>
    public string? Title { get; init; }

    /// <summary>0, or a keyboard key's generic virtual-key code that the player holds while the wheel is open (<see
    /// cref="UiWheel.HoldShift"/>, <see cref="UiWheel.HoldCtrl"/>, <see cref="UiWheel.HoldAlt"/>, a letter such as
    /// <c>'Q'</c>...): releasing it picks the highlighted entry, or cancels in the middle. A tap (released within 300 ms
    /// with nothing highlighted) leaves the wheel open for a click. A key that is not held as the wheel opens counts as
    /// no key: a left click picks. No mouse buttons and no left/right-specific codes (VK_LSHIFT...).</summary>
    public uint HoldKey { get; init; }
}

/// <summary>
/// A radial wheel in the middle of the screen: 1 to 64 entries, 8 per page (the mouse wheel turns the page). While it is
/// open Kiln has the mouse and keyboard: the pointer picks the highlighted entry, a left click on it chooses it, Esc or a
/// right click cancels (so does letting go of the hold key in the middle). "Name (details)" shows the details on a second
/// line. One wheel at a time. This is what EMF's <c>wheel_open</c> gives its mods.
/// </summary>
/// <remarks>Open it from any thread; the result callback runs on the game's render thread (as your mod), so hop to the
/// game's threads to act (<c>Client.Post</c> for the client world, <c>Context.Scheduler.Post</c> for the server world), or
/// <c>await</c> <see cref="Result"/>: the continuation resumes on the context that awaited. Not available on a dedicated
/// server (<see cref="Ui.OpenWheel"/> returns null).</remarks>
public sealed unsafe class UiWheel : IDisposable
{
    /// <summary>The result of a wheel that was cancelled or closed.</summary>
    public const int Cancelled = -1;

    public const int MaxEntries = 64;
    public const int PageEntries = 8;

    /// <summary>Generic virtual-key codes for <see cref="UiWheelOptions.HoldKey"/>.</summary>
    public const uint HoldShift = 0x10, HoldCtrl = 0x11, HoldAlt = 0x12;

    private static readonly object Lock = new();
    private static UiWheel? _current;
    private static ulong _serial;

    private readonly TaskCompletionSource<int> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<int>? _onResult;
    private ulong _id, _handle;
    private volatile bool _open;

    private UiWheel(string[] entries, string? title, uint holdKey, Action<int>? onResult, ModContext? owner)
    {
        Entries = entries;
        Title = title;
        HoldKey = holdKey;
        _onResult = onResult;
        Owner = owner;
    }

    /// <summary>The entries, as given.</summary>
    public IReadOnlyList<string> Entries { get; }

    public string? Title { get; }
    public uint HoldKey { get; }
    internal ModContext? Owner { get; }

    /// <summary>The wheel is still showing.</summary>
    public bool IsOpen => _open;

    /// <summary>Completes with the index of the chosen entry, or <see cref="Cancelled"/> when the player cancelled or the
    /// wheel was closed (<see cref="Close"/>, the owning mod unloading).</summary>
    public Task<int> Result => _result.Task;

    /// <summary>Some wheel (any mod's) is open now.</summary>
    public static bool IsAnyOpen
    {
        get
        {
            var t = UiNative.T2;
            return t != null && t->WheelActive() != 0;
        }
    }

    /// <summary>The wheel exists in this process (a game client whose kiln.ui has it).</summary>
    public static bool Available => UiNative.T2 != null;

    /// <summary>How many pages <paramref name="entries"/> entries make (8 per page).</summary>
    public static int PageCount(int entries) => entries <= 0 ? 0 : (entries + PageEntries - 1) / PageEntries;

    /// <summary>A hold key is 0 or a keyboard key's generic virtual-key code (no mouse buttons, no VK_LSHIFT-style codes).</summary>
    public static bool IsValidHoldKey(uint vk) => vk <= 0xFF && !(vk >= 0x01 && vk <= 0x06) && !(vk >= 0xA0 && vk <= 0xA5);

    /// <summary>Why a definition is refused, or null when it is fine.</summary>
    internal static string? Check(IReadOnlyList<string>? entries, UiWheelOptions? options)
    {
        if (entries is null || entries.Count < 1 || entries.Count > MaxEntries) return $"a wheel has 1 to {MaxEntries} entries";
        for (var i = 0; i < entries.Count; i++)
            if (entries[i] is null) return $"entry {i} is null";
        if (options is { } o && !IsValidHoldKey(o.HoldKey))
            return "HoldKey must be 0 or a keyboard key's generic virtual-key code (0x10 Shift, 0x11 Ctrl, 0x12 Alt, a letter; no mouse buttons, no left/right-specific keys)";
        return null;
    }

    /// <summary>Opens the wheel. <paramref name="onResult"/> runs once when it ends (render thread, as the calling mod):
    /// the index of the chosen entry, or <see cref="Cancelled"/>. Returns null when no wheel can open: another wheel is
    /// open, the Kiln menu is open, there is no overlay (a dedicated server, no game window yet).</summary>
    /// <exception cref="ArgumentException">No entries, more than 64, a null entry, or a bad hold key.</exception>
    internal static UiWheel? OpenCore(IReadOnlyList<string> entries, Action<int>? onResult, UiWheelOptions? options)
    {
        if (Check(entries, options) is { } bad) throw new ArgumentException(bad, nameof(entries));
        var t = UiNative.T2;
        if (t == null) return null;
        var owner = UiModCalls.Caller;
        UiModCalls.Watch(owner);
        var w = new UiWheel(entries.ToArray(), options?.Title, options?.HoldKey ?? 0, onResult, owner);

        // every label and the title in one pinned buffer, the pointers in a pinned array: kiln.ui copies them during the call
        var enc = Encoding.UTF8;
        var title = enc.GetBytes(w.Title ?? "");
        var labels = new byte[w.Entries.Count][];
        var total = title.Length;
        for (var i = 0; i < labels.Length; i++)
        {
            labels[i] = enc.GetBytes(w.Entries[i]);
            total += labels[i].Length;
        }
        var bytes = GC.AllocateArray<byte>(Math.Max(total, 1), pinned: true);
        var strs = GC.AllocateArray<NativeStr>(labels.Length, pinned: true);
        var at = 0;
        var basePtr = (byte*)Unsafe.AsPointer(ref bytes[0]);
        title.CopyTo(bytes, at);
        var titleStr = new NativeStr { Ptr = basePtr + at, Len = (nuint)title.Length };
        at += title.Length;
        for (var i = 0; i < labels.Length; i++)
        {
            labels[i].CopyTo(bytes, at);
            strs[i] = new NativeStr { Ptr = basePtr + at, Len = (nuint)labels[i].Length };
            at += labels[i].Length;
        }
        var def = new UiNative.WheelDef
        {
            Size = (uint)sizeof(UiNative.WheelDef),
            Flags = 0,
            Title = titleStr,
            Items = (NativeStr*)Unsafe.AsPointer(ref strs[0]),
            Count = (uint)labels.Length,
            HoldKey = w.HoldKey,
        };
        lock (Lock)
        {
            UiModCalls.RequireAlive(owner);
            if (_current != null) return null;
            w._id = ++_serial;
            ulong h = 0;
            // kiln.ui may call back at once from its render thread, which waits for this lock: _current is set before it can end
            _current = w;
            w._open = true;
            if (t->WheelOpen(UiModCalls.OwnerId(owner), &def, &OnNative, (void*)w._id, &h) != UiNative.Ok)
            {
                _current = null;
                w._open = false;
                return null;
            }
            w._handle = h;
        }
        GC.KeepAlive(bytes);
        GC.KeepAlive(strs);
        return w;
    }

    /// <summary>Ends the wheel without a pick: <see cref="Result"/> completes with <see cref="Cancelled"/> and the result
    /// callback does not run. Does nothing when it already ended.</summary>
    public void Close()
    {
        ulong handle;
        lock (Lock)
        {
            if (_current != this) return;
            _current = null;
            _open = false;
            handle = _handle;
        }
        var t = UiNative.T2;
        if (t != null) t->WheelClose(handle);  // waits for a callback that is running; it finds nothing and does nothing
        _result.TrySetResult(Cancelled);
    }

    public void Dispose() => Close();

    /// <summary>Forgets the wheel that is open (tests swap the native table under it).</summary>
    internal static void ResetForTests()
    {
        lock (Lock) _current = null;
    }

    /// <summary>Closes the wheel a mod left open when it unloads.</summary>
    internal static void RemoveOwnedBy(ModContext mod)
    {
        UiWheel? w;
        lock (Lock) w = _current is { } c && ReferenceEquals(c.Owner, mod) ? c : null;
        w?.Close();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNative(int choice, void* user)
    {
        try
        {
            Finish((ulong)user, choice);
        }
        catch (Exception e)
        {
            UiEngine.Log(LogLevel.Error, $"wheel: {e}");
        }
    }

    /// <summary>The native wheel ended (render thread): the pick or -1.</summary>
    internal static void Finish(ulong id, int choice)
    {
        UiWheel? w;
        lock (Lock)
        {
            if (_current is not { } c || c._id != id) return;
            w = c;
            _current = null;
            w._open = false;
        }
        if (choice < -1 || choice >= w.Entries.Count) choice = Cancelled;
        if (w._onResult is { } cb) UiModCalls.Invoke(w.Owner, cb, choice, "a wheel callback");
        w._result.TrySetResult(choice);
    }
}

public static partial class Ui
{
    /// <summary>Opens a radial wheel (<see cref="UiWheel"/>) with <paramref name="entries"/>; <paramref name="onResult"/> gets
    /// the chosen index or -1 (render thread). Null when none can open (another wheel or the Kiln menu is open, no overlay).</summary>
    /// <example><code>
    /// var wheel = Ui.OpenWheel(["Wolf", "Bear (level 3)", "Fox"], pick => Log.Info($"picked {pick}"),
    ///     new UiWheelOptions { Title = "Pets", HoldKey = UiWheel.HoldShift });
    /// </code></example>
    /// <exception cref="ArgumentException">No entries, more than 64, a null entry, or a bad hold key.</exception>
    public static UiWheel? OpenWheel(IReadOnlyList<string> entries, Action<int>? onResult = null, UiWheelOptions? options = null) =>
        UiWheel.OpenCore(entries, onResult, options);
}

/// <summary>A mouse button for <see cref="UiDraw.ItemClicked"/>.</summary>
public enum UiMouseButton
{
    Left = 0,
    Right = 1,
    Middle = 2,
}
