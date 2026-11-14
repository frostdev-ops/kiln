// SPDX-License-Identifier: MIT
// Ui.Values.TooltipLines and Kiln's "Item details" window (chapter 05a §5.5): extra text for an item, collected while
// the game draws the item's detail view, shown in a separate, explicitly opened window with its own wrapping and
// scrolling. Nothing is drawn into the game's tooltip (the game's own layout neither wraps nor makes room for added
// lines). Capability ui.values.tooltip_lines, held until check 05a-c4-viewer.
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln;

/// <summary>A tooltip-lines provider's answer: Ok with the lines to add (at most 16 per detail view, all providers
/// together; a provider whose lines do not fit adds none), or not Ok (adds none; the reason is logged once). Lines are
/// interned for the process: keep them stable, do not make a new text every frame.</summary>
public sealed record UiTooltipDecision(bool Ok, string? Reason, IReadOnlyList<UiText> Lines) : IKilnResult
{
    /// <summary>No lines.</summary>
    public static readonly UiTooltipDecision None = new(true, null, []);

    /// <summary>These lines.</summary>
    public static UiTooltipDecision With(params UiText[] lines) => new(true, null, lines ?? throw new ArgumentNullException(nameof(lines)));

    /// <summary>Refuse, saying why (for the log).</summary>
    public static UiTooltipDecision Refuse(string reason) => new(false, reason, []);
}

/// <summary>A tooltip-lines provider. Runs on the game client's main thread while the game draws an item's detail view;
/// same rules as <see cref="UiBarProvider"/>.</summary>
[ClientMainThread]
public delegate UiTooltipDecision UiTooltipProvider(in UiValueContext context, in UiItemView item);

/// <summary>The details window was opened: Ok with the snapshot's session and source frame, or not Ok
/// ("no tooltip": no item detail with lines in this session; "viewport too small").</summary>
public sealed record UiTooltipOpenResult(bool Ok, string? Reason, ulong Session, ulong SourceFrame) : IKilnResult;

/// <summary>Kiln's details window opened or closed (also closed by a world change or a provider change). Tick thread, next
/// step.</summary>
[ClientOnly]
public sealed class UiTooltipPanelChangedEvent(bool open, ulong session, ulong sourceFrame, ItemId item, string? reason) : KilnEvent
{
    public bool Open { get; } = open;
    public ulong Session { get; } = session;
    public ulong SourceFrame { get; } = sourceFrame;
    public ItemId Item { get; } = item;
    /// <summary>Why it closed or could not open; null when opened or closed by a call.</summary>
    public string? Reason { get; } = reason;
}

public static partial class Ui
{
    public static partial class Values
    {
        /// <summary>The capability row of the tooltip lines and the details window.</summary>
        public const string TooltipCapability = "ui.values.tooltip_lines";

        /// <summary>Adds lines for <paramref name="item"/> (0: every item) to Kiln's "Item details" window. The lines are
        /// collected when the game draws the item's detail view; the player opens the window from the pause menu ("Item
        /// details") or a mod opens it with <see cref="OpenTooltipDetails"/>.</summary>
        [ClientOnly, AnyThread, RequiresCapability(TooltipCapability)]
        public static UiValueRegistration TooltipLines(ItemId item, UiTooltipProvider provider) =>
            UiTooltipBridge.Register(item, provider);

        /// <summary>Opens the details window on the last inspected item's lines (a frozen snapshot, refreshed only by its
        /// Refresh button). Opening it again changes nothing. Not Ok when nothing was inspected in this session or the
        /// screen is too small for it (160 pixels a side). Call it from a mod's own button or key; Kiln adds no hotkey.</summary>
        [ClientOnly, AnyThread, RequiresCapability(TooltipCapability)]
        public static UiTooltipOpenResult OpenTooltipDetails() => UiTooltipBridge.Open();

        /// <summary>Closes the details window. Idempotent.</summary>
        [ClientOnly, AnyThread, RequiresCapability(TooltipCapability)]
        public static void CloseTooltipDetails() => UiTooltipBridge.Close(null);
    }
}

/// <summary>Word, then text-element wrapping to a measured width: a word longer than the width is split between text
/// elements (graphemes), never inside one. Empty lines stay; the order stays.</summary>
internal static class UiTooltipWrap
{
    public static List<string> Wrap(IEnumerable<string> lines, float width, Func<string, float> measure)
    {
        var output = new List<string>();
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                output.Add("");
                continue;
            }
            var current = new StringBuilder();
            foreach (var word in line.Split(' '))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (measure(candidate) <= width)
                {
                    current.Clear().Append(candidate);
                    continue;
                }
                if (current.Length > 0)
                {
                    output.Add(current.ToString());
                    current.Clear();
                }
                if (measure(word) <= width)
                {
                    current.Append(word);
                    continue;
                }
                // a word longer than the line: split between text elements
                var e = StringInfo.GetTextElementEnumerator(word);
                while (e.MoveNext())
                {
                    var element = e.GetTextElement();
                    if (current.Length > 0 && measure(current + element) > width)
                    {
                        output.Add(current.ToString());
                        current.Clear();
                    }
                    current.Append(element);
                }
            }
            output.Add(current.ToString());
        }
        return output;
    }
}

internal static unsafe class UiTooltipBridge
{
    internal const float MinSide = 160;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, UiValueBridge.Reg> Regs = new();
    private static long _next;
    private static readonly object Lock = new();
    private static UiWindow? _window;
    private static MenuEntry? _menu;
    private static (ulong Session, ulong Frame, float Width, float Scale) _cacheKey;
    private static bool _watching;
    private static readonly Action<UiDraw> Watch = WatchWindow;
    private static List<string> _cache = [];

    /// <summary>The usable screen (pixels and scale), null when Kiln's overlay is not here. Tests replace it.</summary>
    internal static Func<(float Width, float Height, float Dpi)?> Viewport = ReadViewport;

    private static (float, float, float)? ReadViewport()
    {
        var t = UiNative.T;
        if (t == null) return null;
        UiNative.Status s = default;
        s.Size = (uint)sizeof(UiNative.Status);
        if (t->Status(&s) != UiNative.Ok || s.Width == 0 || s.Height == 0) return null;
        return (s.Width, s.Height, s.DpiScale > 0 ? s.DpiScale : 1);
    }

    internal static UiValueRegistration Register(ItemId item, UiTooltipProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var row = Ui.Values.TooltipCapability;
        UiValueBridge.RequireCapability(row);
        var t = UiValuesNative.T;
        if (t == null) throw new KilnUnavailableException(row, Capabilities.Why(UiValuesNative.Service));
        var mod = UiModCalls.Caller;
        UiModCalls.RequireAlive(mod);
        var reg = new UiValueBridge.Reg
        {
            Id = Interlocked.Increment(ref _next), Kind = UiValueKind.Detail, Item = item, Lines = provider, Mod = mod,
            ModName = mod?.Info.Id ?? "kiln",
        };
        Regs[reg.Id] = reg;
        UiValuesNative.Registration r = default;
        r.Size = (uint)sizeof(UiValuesNative.Registration);
        var rc = t->TooltipLines(UiValueBridge.OwnerOf(mod), item.Value, &OnLines, (void*)reg.Id, &r);
        if (rc != UiValuesNative.Ok || r.Ok == 0) Regs.TryRemove(reg.Id, out _);
        switch (rc)
        {
            case UiValuesNative.Ok when r.Ok != 0:
                break;
            case UiValuesNative.Ok:
                return new UiValueRegistration(false, UiValueBridge.ReasonText(r.Reason), 0, null);
            case UiValuesNative.Unavailable:
                throw new KilnUnavailableException(row, Capabilities.Is(row) ? "kiln.ui.values could not hook the item detail view (see kiln.log)" : Capabilities.Why(row));
            case UiValuesNative.Revoked:
                throw new InvalidOperationException("the mod is unloading");
            default:
                throw new ArgumentException($"kiln.ui.values refused tooltip_lines ({rc})");
        }
        reg.Handle = r.Handle;
        var id = reg.Id;
        var handle = r.Handle;
        reg.Public = new UiValueRegistration(true, null, handle, _ =>
        {
            if (!Regs.TryRemove(id, out var gone)) return;
            var table = UiValuesNative.T;
            if (table != null) table->Unsubscribe(handle);
            UiValueEvents.ProviderChanged(gone, UiValueProviderChange.Removed);
        });
        if (mod is not null)
            mod.Retiring += () => reg.Public.Dispose();
        UiValueEvents.ProviderChanged(reg, UiValueProviderChange.Added);
        EnsureMenuEntry();
        return reg.Public;
    }

    // The pause menu's "Item details" entry, with the first provider.
    private static void EnsureMenuEntry()
    {
        lock (Lock)
        {
            if (_menu is not null || !UiNative.HasGameUi) return;
            var outer = ModContext.CurrentMod;
            ModContext.CurrentMod = null;  // Kiln's own entry, not the registering mod's
            try
            {
                _menu = Ui.Menus.Add(MenuLocation.PauseMenu, "Item details", () => Open());
            }
            finally
            {
                ModContext.CurrentMod = outer;
            }
        }
    }

    internal static UiTooltipOpenResult Open()
    {
        var row = Ui.Values.TooltipCapability;
        UiValueBridge.RequireCapability(row);
        var t = UiValuesNative.T;
        if (t == null) throw new KilnUnavailableException(row, Capabilities.Why(UiValuesNative.Service));
        var vp = Viewport() ?? throw new KilnUnavailableException(row, "Kiln's overlay (kiln.ui) is not here");
        var (w, h) = Size(vp);
        if (w < MinSide || h < MinSide)
        {
            UiValueEvents.TooltipPanel(false, 0, 0, default, "viewport too small");
            return new UiTooltipOpenResult(false, "viewport too small", 0, 0);
        }
        UiValuesNative.TooltipOpen o = default;
        o.Size = (uint)sizeof(UiValuesNative.TooltipOpen);
        var rc = t->OpenTooltipDetails(UiValueBridge.OwnerOf(UiModCalls.Caller), &o);
        if (rc == UiValuesNative.Unavailable) throw new KilnUnavailableException(row, Capabilities.Why(row));
        if (rc != UiValuesNative.Ok) throw new InvalidOperationException($"kiln.ui.values: open_tooltip_details failed ({rc})");
        if (o.Ok == 0) return new UiTooltipOpenResult(false, "no tooltip", 0, 0);
        var item = Read(0) is { Ok: not 0 } d ? new ItemId(d.Item) : default;
        lock (Lock)
        {
            if (_window is null && Ui.Available)
            {
                var outer = ModContext.CurrentMod;
                ModContext.CurrentMod = null;  // Kiln's window, not the caller's
                try
                {
                    _window = Ui.Window("Item details", Draw, new UiWindowOptions { StartOpen = false, Width = w / vp.Dpi, Height = h / vp.Dpi });
                }
                finally
                {
                    ModContext.CurrentMod = outer;
                }
            }
            if (_window is not null)
            {
                _window.Open = true;
                if (!_watching)
                {
                    _watching = true;
                    Ui.Frame += Watch;  // its X closes only the window: the frozen snapshot must end with it
                }
            }
        }
        UiValueEvents.TooltipPanel(true, o.Session, o.SourceFrame, item, null);
        return new UiTooltipOpenResult(true, null, o.Session, o.SourceFrame);
    }

    /// <summary>The window's size: min(560 x dpi, width - 64) by min(600 x dpi, height - 64) pixels.</summary>
    internal static (float W, float H) Size((float Width, float Height, float Dpi) vp) =>
        (Math.Min(560 * vp.Dpi, vp.Width - 64), Math.Min(600 * vp.Dpi, vp.Height - 64));

    // While the window is open (the overlay draws every frame anyway): a window closed by its X ends the native open state.
    private static void WatchWindow(UiDraw ui)
    {
        lock (Lock)
            if (_window is not { Open: false }) return;
        StopWatch();
        var t = UiValuesNative.T;
        if (t != null) t->CloseTooltipDetails(UiValueBridge.OwnerOf(null));
        UiValueEvents.TooltipPanel(false, 0, 0, default, "closed by its window's X");
    }

    private static void StopWatch()
    {
        lock (Lock)
        {
            if (!_watching) return;
            _watching = false;
        }
        Ui.Frame -= Watch;
    }

    internal static void Close(string? reason)
    {
        var t = UiValuesNative.T;
        if (reason is null)
        {
            UiValueBridge.RequireCapability(Ui.Values.TooltipCapability);
            if (t == null) throw new KilnUnavailableException(Ui.Values.TooltipCapability, Capabilities.Why(UiValuesNative.Service));
        }
        var wasOpen = false;
        lock (Lock)
            if (_window is { Open: true })
            {
                wasOpen = true;
                _window.Open = false;
            }
        StopWatch();
        if (t != null) t->CloseTooltipDetails(UiValueBridge.OwnerOf(null));
        if (wasOpen) UiValueEvents.TooltipPanel(false, 0, 0, default, reason);
    }

    internal static UiValuesNative.TooltipDetails Read(uint which)
    {
        UiValuesNative.TooltipDetails d = default;
        d.Size = (uint)sizeof(UiValuesNative.TooltipDetails);
        var t = UiValuesNative.T;
        if (t == null || t->Header.Size < (uint)sizeof(UiValuesNative.Table)) return d;
        t->GetTooltipDetails(UiValueBridge.OwnerOf(null), which, &d);
        return d;
    }

    internal static List<string> LinesOf(in UiValuesNative.TooltipDetails d)
    {
        var lines = new List<string>((int)d.Count);
        for (var i = 0; i < d.Count && i < 16; i++) lines.Add(d.Lines[i].ToString());
        return lines;
    }

    // The window (render thread): the frozen snapshot, wrapped to the pane's measured width and scrolled in its child.
    private static void Draw(UiDraw ui)
    {
        var d = Read(0);
        if (d.Open == 0)
        {
            Close("closed by Kiln (world or provider change)");
            return;
        }
        ui.Text($"Last inspected item — snapshot (item {d.Item:x8}, frame {d.SourceFrame})");
        if (ui.Button("Refresh"))
        {
            UiValuesNative.TooltipOpen o = default;
            o.Size = (uint)sizeof(UiValuesNative.TooltipOpen);
            UiValuesNative.T->RefreshTooltipDetails(UiValueBridge.OwnerOf(null), &o);
        }
        ui.SameLine();
        if (ui.Button("Close")) Close(null);
        ui.Separator();
        if (ui.BeginChild("##kiln.item.details.lines"))
        {
            var (_, _, width, _) = ui.WindowRect;
            width = Math.Max(1, width - 24 * ui.Scale);
            var key = (d.Session, d.SourceFrame, MathF.Round(width), ui.Scale);
            if (key != _cacheKey)
            {
                _cache = UiTooltipWrap.Wrap(LinesOf(d), width, s => ui.MeasureText(s).Width);
                _cacheKey = key;
            }
            foreach (var line in _cache) ui.Text(line);
        }
        ui.EndChild();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static int OnLines(UiValuesNative.ProviderContext* c, UiValuesNative.ItemView* v, UiValuesNative.LineWriter* w, void* user)
    {
        try
        {
            return Call(c, v, w, (long)user);
        }
        catch (Exception e)
        {
            UiEngine.Log(LogLevel.Error, $"ui.values: tooltip bridge: {e}");
            return UiValuesNative.Internal;
        }
    }

    internal static int Call(UiValuesNative.ProviderContext* c, UiValuesNative.ItemView* v, UiValuesNative.LineWriter* w, long id)
    {
        if (!Regs.TryGetValue(id, out var reg) || reg.Mod is { Alive: false } || reg.Lines is null) return UiValuesNative.Ok;
        var ctx = new UiValueContext(UiValueKind.Detail, c->Session, c->Frame, c->ClientStep);
        var view = UiValueBridge.ViewOf(v);
        UiTooltipDecision? d;
        var prevMod = ModContext.CurrentMod;
        if (reg.Mod is not null) ModContext.CurrentMod = reg.Mod;
        try
        {
            d = reg.Lines(in ctx, in view);
        }
        catch (Exception e)
        {
            UiValueBridge.FaultOf(reg, $"threw {e.GetType().Name}: {e.Message}", e, Drop);
            return UiValuesNative.Internal;
        }
        finally
        {
            if (reg.Mod is not null) ModContext.CurrentMod = prevMod;
        }
        if (d is null || d.Lines is null)
        {
            UiValueBridge.FaultOf(reg, "returned no decision", null, Drop);
            return UiValuesNative.Internal;
        }
        if (!d.Ok)
        {
            UiValueBridge.RefusedOf(reg, ctx.Frame, d.Reason ?? "refused");
            return UiValuesNative.Unavailable;
        }
        foreach (var line in d.Lines)
        {
            if (line is null)
            {
                UiValueBridge.FaultOf(reg, "returned a null line", null, Drop);
                return UiValuesNative.Internal;
            }
            var rc = UiValuesNative.Intern(c, line.ToString(), out var tid);
            if (rc == UiValuesNative.BufferTooSmall)
            {
                UiValueBridge.RefusedOf(reg, ctx.Frame, "text budget");
                return UiValuesNative.Unavailable;
            }
            if (rc != UiValuesNative.Ok)
            {
                UiValueBridge.FaultOf(reg, "returned a line that is not valid UTF-8 text", null, Drop);
                return UiValuesNative.Internal;
            }
            if (w->Append(w, tid) != UiValuesNative.Ok)
            {
                UiValueBridge.RefusedOf(reg, ctx.Frame, "more than 16 lines in the detail view");
                return UiValuesNative.Unavailable;  // the native writer refuses this provider's lines as a whole
            }
        }
        reg.Faults = 0;
        reg.LastRefusal = null;
        return UiValuesNative.Ok;
    }

    private static void Drop(long id)
    {
        if (!Regs.TryRemove(id, out var reg)) return;
        var t = UiValuesNative.T;
        if (t != null && reg.Handle != 0) t->Unsubscribe(reg.Handle);
        UiValueEvents.ProviderChanged(reg, UiValueProviderChange.Faulted);
    }

    internal static void ResetForTests()
    {
        Regs.Clear();
        StopWatch();
        lock (Lock)
        {
            _window = null;
            _menu = null;
        }
        Viewport = ReadViewport;
    }
}
