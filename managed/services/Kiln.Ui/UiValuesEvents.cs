// SPDX-License-Identifier: MIT
// The events of Ui.Values (chapter 05a §5.4). Change and reset events come from kiln.ui.values' change publication
// (subscribe_changes, started when the first handler subscribes to one of the two); provider events from the bridge.
// All go on Kiln's event bus with Events.Enqueue: handlers run on the tick thread at the next step, never inside the
// game's UI frame and never while a descriptor is edited.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>How a published value changed.</summary>
public enum UiValueChangeKind
{
    /// <summary>The identity was shown for the first time (no Before).</summary>
    Added = 0,
    /// <summary>It shows another value.</summary>
    Updated = 1,
    /// <summary>It was not shown for two completed frames (held and HUD values: two player-UI updates); no After.</summary>
    Removed = 2,
    /// <summary>The cache of 4096 identities was full and this one was seen least recently (then the smallest key).</summary>
    Evicted = 3,
}

/// <summary>Why the published values were forgotten at once.</summary>
public enum UiValuesResetReason
{
    /// <summary>A new client session: a world was left or another one joined.</summary>
    World = 0,
    /// <summary>A capability went off while values were published.</summary>
    Capability = 1,
    /// <summary>The last value provider went.</summary>
    Registry = 2,
}

/// <summary>A value shown by the game's UI changed (also a game value changing while every provider passes). Coalesced per
/// completed UI frame: the last occurrence of an identity in the frame counts. Observes what was shown, never an engine
/// write; the identity is a presentation identity, not an item's lifetime. Posted on the tick thread at the next step.
/// Only while a provider of the value's kind is registered.</summary>
[ClientOnly]
public sealed class UiValueChangedEvent(UiValueIdentity identity, UiBarValue? before, UiBarValue? after, ulong frame,
    UiValueChangeKind changeKind) : KilnEvent
{
    static UiValueChangedEvent() => UiValueEvents.EnsureFeed();

    public UiValueIdentity Identity { get; } = identity;
    public UiBarValue? Before { get; } = before;
    public UiBarValue? After { get; } = after;
    /// <summary>The completed game UI frame it was published at.</summary>
    public ulong Frame { get; } = frame;
    public UiValueChangeKind ChangeKind { get; } = changeKind;
}

/// <summary>Every published value was forgotten at once (instead of a Removed per identity). Tick thread, next step.</summary>
[ClientOnly]
public sealed class UiValuesResetEvent(UiValuesResetReason reason, ulong oldSession, ulong newSession) : KilnEvent
{
    static UiValuesResetEvent() => UiValueEvents.EnsureFeed();

    public UiValuesResetReason Reason { get; } = reason;
    public ulong OldSession { get; } = oldSession;
    public ulong NewSession { get; } = newSession;
}

/// <summary>How a value provider's registration changed.</summary>
public enum UiValueProviderChange
{
    Added = 0,
    /// <summary>Disposed, or its mod unloaded.</summary>
    Removed = 1,
    /// <summary>Removed after five exceptions or invalid edits in a row.</summary>
    Faulted = 2,
}

/// <summary>A value provider was registered, removed, or removed for its faults. Tick thread, next step.</summary>
[ClientOnly]
public sealed class UiValueProviderChangedEvent(UiValueKind kind, ItemId item, string mod, UiValueProviderChange change)
    : KilnEvent
{
    public UiValueKind Kind { get; } = kind;
    /// <summary>The provider's item filter (<see cref="ItemId"/> 0: every item).</summary>
    public ItemId Item { get; } = item;
    /// <summary>The id of the mod that registered it ("kiln" for Kiln's own).</summary>
    public string Mod { get; } = mod;
    public UiValueProviderChange Change { get; } = change;
}

/// <summary>A value provider refused (Ok false, or its label did not fit the text budget). Deduplicated: the same
/// provider's same reason again posts nothing until the reason changes or it answers Ok. Tick thread, next step.</summary>
[ClientOnly]
public sealed class UiValueProviderRefusedEvent(UiValueKind kind, string mod, ulong frame, string reason) : KilnEvent
{
    public UiValueKind Kind { get; } = kind;
    public string Mod { get; } = mod;
    public ulong Frame { get; } = frame;
    /// <summary>The provider's reason, cut at 256 characters.</summary>
    public string Reason { get; } = reason;
}

internal static unsafe class UiValueEvents
{
    private static readonly object Lock = new();
    private static ulong _feed;

    private static void Enqueue(KilnEvent e)
    {
        try
        {
            Events.Enqueue(e);
        }
        catch (InvalidOperationException ex)
        {
            UiEngine.Log(LogLevel.Warn, $"ui.values: an event was dropped: {ex.Message}");
        }
    }

    internal static void ProviderChanged(UiValueBridge.Reg reg, UiValueProviderChange change)
    {
        if (Events.HasSubscribers<UiValueProviderChangedEvent>())
            Enqueue(new UiValueProviderChangedEvent(reg.Kind, reg.Item, reg.ModName, change));
    }

    internal static void TooltipPanel(bool open, ulong session, ulong frame, ItemId item, string? reason)
    {
        if (Events.HasSubscribers<UiTooltipPanelChangedEvent>())
            Enqueue(new UiTooltipPanelChangedEvent(open, session, frame, item, reason));
    }

    internal static void ProviderRefused(UiValueBridge.Reg reg, ulong frame, string reason)
    {
        if (Events.HasSubscribers<UiValueProviderRefusedEvent>())
            Enqueue(new UiValueProviderRefusedEvent(reg.Kind, reg.ModName, frame, reason));
    }

    /// <summary>Subscribes to kiln.ui.values' change publication once (Kiln.Ui's own owner). Quiet where there is none.</summary>
    internal static void EnsureFeed()
    {
        lock (Lock)
        {
            if (_feed != 0) return;
            var t = UiValuesNative.T;
            if (t == null) return;
            UiValuesNative.Registration r = default;
            r.Size = (uint)sizeof(UiValuesNative.Registration);
            var rc = t->SubscribeChanges(UiValueBridge.OwnerOf(null), &OnChange, null, &r);
            if (rc == UiValuesNative.Ok && r.Ok != 0) _feed = r.Handle;
            else UiEngine.Log(LogLevel.Info, $"ui.values: no change events here (rc {rc}, reason {r.Reason})");
        }
    }

    internal static void ResetForTests()
    {
        lock (Lock) _feed = 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void OnChange(UiValuesNative.ValueChange* c, void* user)
    {
        try
        {
            if (Translate(c) is { } e) Enqueue(e);
        }
        catch (Exception ex)
        {
            UiEngine.Log(LogLevel.Error, $"ui.values: change feed: {ex}");
        }
    }

    internal static KilnEvent? Translate(UiValuesNative.ValueChange* c)
    {
        if (c->ChangeKind == UiValuesNative.ChangeReset)
            return Events.HasSubscribers<UiValuesResetEvent>()
                ? new UiValuesResetEvent((UiValuesResetReason)c->Reason, c->OldSession, c->NewSession)
                : null;
        if (c->ChangeKind > UiValuesNative.ChangeEvicted || !Events.HasSubscribers<UiValueChangedEvent>()) return null;
        var id = c->Identity;
        var identity = new UiValueIdentity((UiValueKind)id.Kind, id.Session, new EntityId(id.LocalPlayer),
            new UiScreenKey((GameUiState)id.UiState, (HudScreen)id.HudScreen, (byte)id.Crafting, id.Station),
            new EntityId(id.InventoryPage), (byte)id.SlotIndex, new EntityId(id.ClientEntity), new ItemId(id.Item),
            id.SyntheticGroup != 0);
        UiBarValue? Bar(UiValuesNative.BarValue v, NativeStr label) =>
            new(v.Fraction, v.Current, v.Maximum, v.LabelIsOverride != 0 ? new UiText(label.ToString()) : null);
        return new UiValueChangedEvent(identity, c->HasBefore != 0 ? Bar(c->Before, c->BeforeLabel) : null,
            c->HasAfter != 0 ? Bar(c->After, c->AfterLabel) : null, c->Frame, (UiValueChangeKind)c->ChangeKind);
    }
}
