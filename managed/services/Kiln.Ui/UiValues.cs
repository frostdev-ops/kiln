// SPDX-License-Identifier: MIT
// Ui.Values: values in the game's own UI (chapter 05a of the dev.28 dossier; native kiln.ui.values,
// native/core/abi/services/kiln_ui_values.h). A provider replaces what the game draws for an item's bar or number, the
// held item's indicator or a HUD pair. Picture only: the server keeps the real values.
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>Which value of the game's UI a provider replaces. The numbers are the native kinds.</summary>
public enum UiValueKind
{
    /// <summary>The number and bar in the item detail view. Current/Max: what the game prints (water: charges).</summary>
    Detail = 0,
    /// <summary>The bar on an inventory slot. Current/Max: raw fuel units; the picture is the fraction.</summary>
    Slot = 1,
    /// <summary>The held item's indicator. Current/Max: raw fuel units.</summary>
    Held = 2,
    /// <summary>The local player's health pair on the HUD (health units).</summary>
    Health = 3,
    /// <summary>The local player's stamina pair (stamina units; its bar on screen is not identified yet).</summary>
    Stamina = 4,
    /// <summary>The local player's mana pair (mana units).</summary>
    Mana = 5,
}

/// <summary>The HUD pairs a provider can replace.</summary>
public enum HudValue
{
    Health = 3,
    Stamina = 4,
    Mana = 5,
}

/// <summary>The part of the game screen a presentation identity names: no frame number, language or thread.</summary>
public readonly record struct UiScreenKey(GameUiState State, HudScreen Hud, byte Crafting, uint Station);

/// <summary>A presentation identity (what <see cref="UiValueChangedEvent"/> keys on). HUD values: kind, session and local
/// player only; the held item adds its entity and item; a detail or slot value has the screen key, page, slot, entity and
/// item (also for a stack without a pide); a synthetic detail (built for an item id alone) is grouped by kind, session,
/// screen and item with <see cref="SyntheticGroup"/> set and the rest empty. Not an item's lifetime handle: never use it
/// for accounting.</summary>
public readonly record struct UiValueIdentity(UiValueKind Kind, ulong Session, EntityId LocalPlayer, UiScreenKey Screen,
    EntityId InventoryPage, byte SlotIndex, EntityId ClientEntity, ItemId Item, bool SyntheticGroup);

/// <summary>What a provider does with the label of the detail bar.</summary>
public enum UiLabelMode
{
    /// <summary>Leave the label as the earlier providers left it.</summary>
    Keep,
    /// <summary>Show <see cref="UiLabelEdit.Text"/>.</summary>
    Set,
    /// <summary>Show no label.</summary>
    Clear,
    /// <summary>Show the game's own label again, also after another provider replaced it.</summary>
    Game,
}

/// <summary>A label edit: <see cref="UiLabelMode.Set"/> needs a <paramref name="Text"/>, the other modes take none.
/// Label edits are for <see cref="UiValueKind.Detail"/> only. A label is interned for the process (it is never freed):
/// use the numbers for what changes, keep the label text stable.</summary>
public sealed record UiLabelEdit(UiLabelMode Mode, UiText? Text = null);

/// <summary>A value as the game would draw it after the earlier providers. <paramref name="LabelOverride"/>: null while the
/// game's own label shows; an empty text when a provider cleared it.</summary>
public sealed record UiBarValue(float Fraction, uint Current, uint Max, UiText? LabelOverride);

/// <summary>What a provider changes; a null field keeps the value. Fraction (0 to 1) only for detail and slot; for the
/// held item and HUD pairs the picture follows Current/Max and Max must be above 0. Detail numbers are at most
/// 2,147,483,647. Units are the kind's (<see cref="UiValueKind"/>).</summary>
public sealed record UiBarEdit(float? Fraction = null, uint? Current = null, uint? Max = null, UiLabelEdit? Label = null)
{
    /// <summary>Changes nothing.</summary>
    public static readonly UiBarEdit None = new();
}

/// <summary>A provider's answer. Ok with an edit commits exactly its non-null fields; Ok with <see cref="UiBarEdit.None"/>
/// passes the value on; not Ok refuses (the earlier providers' value stays; the reason is logged once and posted as
/// <see cref="UiValueProviderRefusedEvent"/>, never shown to the player).</summary>
public sealed record UiProviderDecision(bool Ok, string? Reason, UiBarEdit Edit) : IKilnResult
{
    /// <summary>Change nothing.</summary>
    public static readonly UiProviderDecision Pass = new(true, null, UiBarEdit.None);

    /// <summary>Commit <paramref name="edit"/>.</summary>
    public static UiProviderDecision Replace(UiBarEdit edit) => new(true, null, edit ?? throw new ArgumentNullException(nameof(edit)));

    /// <summary>Refuse, saying why (for the log and the event).</summary>
    public static UiProviderDecision Refuse(string reason) => new(false, reason, UiBarEdit.None);
}

/// <summary>What the game drew the value for, copied for the callback. Flags: 1 a client entity, 2 a proven server entity,
/// 4 a real inventory slot, 8 the item detail view's own copy, 16 a synthetic detail (an item id with no slot, count or
/// entity: type-only providers may answer it, instance providers pass). <see cref="ServerEntity"/> is
/// <see cref="EntityId.None"/> in this version: Kiln does not prove that a client entity is the server's (a client-local
/// id must never be matched to server data by number). Fuel/FuelMax are always raw fuel units;
/// <see cref="UnitsPerCharge"/> is the detail view's divisor (at least 1). Durability fields are the raw values.</summary>
public readonly record struct UiItemView(uint Flags, EntityId LocalPlayer, EntityId ClientEntity, EntityId ServerEntity,
    ItemId Item, EntityId InventoryPage, byte SlotIndex, uint Fuel, uint FuelMax, uint UnitsPerCharge, uint Durability,
    uint DurabilityMax, uint Count, uint Level, ItemRarity Rarity, GameScreen Screen);

/// <summary>Where a provider call happens. Detail and slot: <see cref="Frame"/> is the game UI frame being drawn. Held and
/// HUD: the last completed frame, and <see cref="ClientStep"/> the client world's step.</summary>
public readonly record struct UiValueContext(UiValueKind Kind, ulong Session, ulong Frame, ulong ClientStep);

/// <summary>A value provider. Runs on the game client's main thread inside the game's UI frame (detail, slot) or right
/// after its player-UI update (held, HUD): read values cached on a tick, never wait, never call server-world APIs, never
/// keep the arguments. Five exceptions or invalid edits in a row remove it.</summary>
[ClientMainThread]
public delegate UiProviderDecision UiBarProvider(in UiValueContext context, in UiItemView item, UiBarValue accumulated);

/// <summary>A registration of <see cref="Ui.Values"/>. Ok false with a <see cref="Reason"/> when it was refused (the mod
/// holds 128 registrations). Dispose removes it; so does the mod's unload.</summary>
public sealed class UiValueRegistration : IDisposable, IKilnResult
{
    internal UiValueRegistration(bool ok, string? reason, ulong handle, Action<UiValueRegistration>? remove)
    {
        Ok = ok;
        Reason = reason;
        Handle = handle;
        _remove = remove;
    }

    private Action<UiValueRegistration>? _remove;

    /// <summary>True when the registration was accepted.</summary>
    public bool Ok { get; }

    /// <summary>Why it was refused; null when <see cref="Ok"/>.</summary>
    public string? Reason { get; }

    /// <summary>The native handle (0 when refused).</summary>
    public ulong Handle { get; }

    /// <summary>Removes the registration. Idempotent; any thread.</summary>
    [AnyThread]
    public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke(this);
}

public static partial class Ui
{
    /// <summary>Values in the game's own UI: replace what the game draws for an item's detail number and bar, a slot's
    /// bar, the held item's indicator and the local player's HUD pairs. Picture only: the server keeps the real values,
    /// a player without the mod and every vanilla client see the game unchanged. Each kind has its capability row
    /// (<c>ui.values.detail</c>, <c>.slot</c>, <c>.held</c>, <c>.hud</c>), off on a server and until its client check
    /// passed. Bad arguments throw <see cref="ArgumentException"/>; a capability that is off throws
    /// <see cref="KilnUnavailableException"/> naming it; a refused registration comes back with Ok false.</summary>
    [ClientOnly]
    public static partial class Values
    {
        /// <summary>The capability rows.</summary>
        public const string DetailCapability = "ui.values.detail", SlotCapability = "ui.values.slot",
            HeldCapability = "ui.values.held", HudCapability = "ui.values.hud";

        /// <summary>Replaces the number and bar of <paramref name="item"/>'s detail view (<see cref="ItemId"/> 0: every
        /// item). Current/Max are what the game prints (water: charges, numbers up to 2,147,483,647); the fraction is
        /// the bar. A label edit replaces the bar's label.</summary>
        [ClientOnly, AnyThread, RequiresCapability(DetailCapability)]
        public static UiValueRegistration ItemBar(ItemId item, UiBarProvider provider) =>
            UiValueBridge.Register(UiValueKind.Detail, item, provider);

        /// <summary>Replaces the bar on <paramref name="item"/>'s inventory slots (0: every item). Only the fraction is
        /// drawn; Current/Max are raw fuel units. A slot whose water is 0/0 is drawn full by the game and stays full
        /// when the providers pass.</summary>
        [ClientOnly, AnyThread, RequiresCapability(SlotCapability)]
        public static UiValueRegistration SlotBar(ItemId item, UiBarProvider provider) =>
            UiValueBridge.Register(UiValueKind.Slot, item, provider);

        /// <summary>Replaces the held item's indicator pair (raw fuel units; Max above 0). Not called while nothing with
        /// fuel is held or the game's maximum is 0.</summary>
        [ClientOnly, AnyThread, RequiresCapability(HeldCapability)]
        public static UiValueRegistration HeldBar(UiBarProvider provider) =>
            UiValueBridge.Register(UiValueKind.Held, default, provider);

        /// <summary>Replaces one of the local player's HUD pairs (Current/Max in the vital's units; Max above 0). The
        /// stamina pair's bar on screen is not identified yet.</summary>
        [ClientOnly, AnyThread, RequiresCapability(HudCapability)]
        public static UiValueRegistration Hud(HudValue vital, UiBarProvider provider)
        {
            if (vital is not (HudValue.Health or HudValue.Stamina or HudValue.Mana))
                throw new ArgumentOutOfRangeException(nameof(vital), vital, "Health, Stamina or Mana");
            return UiValueBridge.Register((UiValueKind)vital, default, provider);
        }
    }
}

/// <summary>Native callbacks into managed providers, the registration records and their events.</summary>
internal static unsafe class UiValueBridge
{
    internal const int FaultLimit = 5;

    internal sealed class Reg
    {
        public long Id;
        public UiValueKind Kind;
        public ItemId Item;
        public UiBarProvider Provider = null!;
        public UiTooltipProvider? Lines;
        public ModContext? Mod;
        public string ModName = "kiln";
        public ulong Handle;
        public int Faults;
        public string? LastRefusal;
        public UiValueRegistration? Public;
    }

    private static readonly ConcurrentDictionary<long, Reg> Regs = new();
    private static long _nextId;
    private static readonly HashSet<ModContext> Watched = new(ReferenceEqualityComparer.Instance);
    private static ulong _ownOwner;

    internal static string RowOf(UiValueKind kind) => kind switch
    {
        UiValueKind.Detail => Ui.Values.DetailCapability,
        UiValueKind.Slot => Ui.Values.SlotCapability,
        UiValueKind.Held => Ui.Values.HeldCapability,
        _ => Ui.Values.HudCapability,
    };

    /// <summary>The owner a registration is made with: the calling mod's, else Kiln.Ui's own.</summary>
    internal static ulong OwnerOf(ModContext? mod)
    {
        if (mod is { Owner: not 0 }) return mod.Owner;
        if (_ownOwner == 0) _ownOwner = Backend.Current.OwnerCreate("kiln.ui.values");
        return _ownOwner;
    }

    /// <summary>Throws for a row that is off now. A row Kiln.Ui lowered because the table was missing is decided again
    /// when the table is here now (it would read on if reported again), never refused on an old report.</summary>
    internal static void RequireCapability(string row)
    {
        UiCaps.Rows.Ensure();
        if (!Capabilities.Is(row))
        {
            ServiceCaps.LookAgain();  // withdraw stale missing-service reports under this same registry
            UiCaps.Rows.Refresh();  // composed rows decide again before refusing
        }
        if (!Capabilities.Is(row)) throw new KilnUnavailableException(row, Capabilities.Why(row));
    }

    internal static UiValueRegistration Register(UiValueKind kind, ItemId item, UiBarProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var row = RowOf(kind);
        RequireCapability(row);
        var t = UiValuesNative.T;
        if (t == null) throw new KilnUnavailableException(row, Capabilities.Why(UiValuesNative.Service));
        var mod = UiModCalls.Caller ?? ModContext.OwnerOf(provider);
        UiModCalls.RequireAlive(mod);
        var reg = new Reg
        {
            Id = Interlocked.Increment(ref _nextId), Kind = kind, Item = item, Provider = provider, Mod = mod,
            ModName = mod?.Info.Id ?? "kiln",
        };
        Regs[reg.Id] = reg;
        UiValuesNative.Registration r = default;
        r.Size = (uint)sizeof(UiValuesNative.Registration);
        var rc = t->Subscribe(OwnerOf(mod), (uint)kind, item.Value, &OnValue, (void*)reg.Id, &r);
        if (rc != UiValuesNative.Ok || r.Ok == 0) Regs.TryRemove(reg.Id, out _);
        switch (rc)
        {
            case UiValuesNative.Ok when r.Ok != 0:
                break;
            case UiValuesNative.Ok:
                return new UiValueRegistration(false, ReasonText(r.Reason), 0, null);
            case UiValuesNative.InvalidArgument:
                throw new ArgumentException($"kiln.ui.values refused the arguments of a {kind} provider");
            case UiValuesNative.Revoked:
                throw new InvalidOperationException("the mod is unloading");
            case UiValuesNative.Unavailable:
                throw new KilnUnavailableException(row, Capabilities.Is(row)
                    ? "kiln.ui.values could not hook the game for it (see kiln.log)"
                    : Capabilities.Why(row));
            default:
                throw new InvalidOperationException($"kiln.ui.values: subscribe failed ({rc})");
        }
        reg.Handle = r.Handle;
        reg.Public = new UiValueRegistration(true, null, r.Handle, Remove);
        UiValueEvents.ProviderChanged(reg, UiValueProviderChange.Added);
        Watch(mod);
        return reg.Public;
    }

    internal static string ReasonText(uint reason) => reason switch
    {
        UiValuesNative.ReasonRegistrationBudget => "registration budget: a mod holds at most 128 ui.values registrations",
        UiValuesNative.ReasonTextBudget => "text budget",
        UiValuesNative.ReasonInvalidEdit => "invalid edit",
        _ => $"refused ({reason})",
    };

    private static void Remove(UiValueRegistration pub)
    {
        foreach (var (id, reg) in Regs)
            if (ReferenceEquals(reg.Public, pub))
            {
                Drop(id, UiValueProviderChange.Removed);
                return;
            }
    }

    // Takes a registration out (natively too) and says so.
    private static void Drop(long id, UiValueProviderChange why)
    {
        if (!Regs.TryRemove(id, out var reg)) return;
        var t = UiValuesNative.T;
        if (t != null && reg.Handle != 0) t->Unsubscribe(reg.Handle);  // NOT_FOUND after a fault removal: fine
        UiValueEvents.ProviderChanged(reg, why);
    }

    private static void Watch(ModContext? mod)
    {
        if (mod is null) return;
        lock (Watched)
            if (Watched.Add(mod)) mod.Retiring += () => Retire(mod);
        if (!mod.Alive) Retire(mod);  // its retirement may have run before the handler was attached
    }

    private static void Retire(ModContext mod)
    {
        lock (Watched) Watched.Remove(mod);
        foreach (var (id, reg) in Regs)
            if (ReferenceEquals(reg.Mod, mod)) Drop(id, UiValueProviderChange.Removed);
    }

    internal static int Count => Regs.Count;

    internal static void ResetForTests()
    {
        Regs.Clear();
        lock (Watched) Watched.Clear();
        _ownOwner = 0;
    }

    // ---- the callback ----
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static int OnValue(UiValuesNative.ProviderContext* c, UiValuesNative.ItemView* v, UiValuesNative.BarValue* input,
        UiValuesNative.BarEdit* edit, void* user)
    {
        try
        {
            return Call(c, v, input, edit, (long)user);
        }
        catch (Exception e)
        {
            UiEngine.Log(LogLevel.Error, $"ui.values: provider bridge: {e}");
            return UiValuesNative.Internal;
        }
    }

    internal static int Call(UiValuesNative.ProviderContext* c, UiValuesNative.ItemView* v, UiValuesNative.BarValue* input,
        UiValuesNative.BarEdit* edit, long id)
    {
        if (!Regs.TryGetValue(id, out var reg) || reg.Mod is { Alive: false }) return UiValuesNative.Ok;  // pass
        var ctx = new UiValueContext((UiValueKind)c->Kind, c->Session, c->Frame, c->ClientStep);
        var view = ViewOf(v);
        var acc = new UiBarValue(input->Fraction, input->Current, input->Maximum,
            input->LabelIsOverride != 0 ? new UiText(UiValuesNative.Resolve(c, input->LabelId) ?? "") : null);
        UiProviderDecision? d;
        var prevMod = ModContext.CurrentMod;
        var prevSync = SynchronizationContext.Current;
        if (reg.Mod is not null)
        {
            ModContext.CurrentMod = reg.Mod;
            SynchronizationContext.SetSynchronizationContext(reg.Mod.Scheduler.SynchronizationContext);
        }
        try
        {
            d = reg.Provider(in ctx, in view, acc);
        }
        catch (Exception e)
        {
            FaultOf(reg, $"threw {e.GetType().Name}: {e.Message}", e, DropFaulted);
            return UiValuesNative.Internal;
        }
        finally
        {
            if (reg.Mod is not null)
            {
                ModContext.CurrentMod = prevMod;
                SynchronizationContext.SetSynchronizationContext(prevSync);
            }
        }
        if (d is null)
        {
            FaultOf(reg, "returned no decision", null, DropFaulted);
            return UiValuesNative.Internal;
        }
        if (!d.Ok)
        {
            edit->Reason = UiValuesNative.ReasonRejected;
            RefusedOf(reg, ctx.Frame, d.Reason ?? "refused");
            return UiValuesNative.Unavailable;  // a refusal, not a fault
        }
        var invalid = Fill(reg.Kind, d.Edit ?? UiBarEdit.None, c, edit, out var budget);
        if (budget)
        {
            RefusedOf(reg, ctx.Frame, "text budget");
            edit->Flags = 0;
            edit->Reason = UiValuesNative.ReasonTextBudget;
            return UiValuesNative.Unavailable;
        }
        if (invalid is not null)
        {
            edit->Flags = 0;
            FaultOf(reg, "made an invalid edit: " + invalid, null, DropFaulted);
            return UiValuesNative.Internal;
        }
        reg.Faults = 0;
        reg.LastRefusal = null;
        return UiValuesNative.Ok;
    }

    internal static UiItemView ViewOf(UiValuesNative.ItemView* v) => new(v->Flags, new EntityId(v->LocalPlayer),
        new EntityId(v->ClientEntity), new EntityId(v->ServerEntity), new ItemId(v->Item), new EntityId(v->InventoryPage),
        (byte)v->SlotIndex, v->Fuel, v->FuelMax, v->UnitsPerCharge, v->Durability, v->DurabilityMax, v->Count, v->Level,
        (ItemRarity)v->Rarity, v->Screen.Size != 0 ? GameScreen.From(v->Screen) : GameScreen.None);

    /// <summary>The edit for native, validated by the kind's rules (chapter 05a §5.1); the text of what is invalid, or
    /// null. <paramref name="budget"/>: the label's text did not fit the text store.</summary>
    internal static string? Fill(UiValueKind kind, UiBarEdit e, UiValuesNative.ProviderContext* c, UiValuesNative.BarEdit* edit,
        out bool budget)
    {
        budget = false;
        uint flags = 0;
        var pair = kind is UiValueKind.Held or UiValueKind.Health or UiValueKind.Stamina or UiValueKind.Mana;
        if (e.Fraction is { } f)
        {
            if (pair) return "a fraction for a held or HUD value (its picture follows Current/Max)";
            if (!float.IsFinite(f) || f < 0 || f > 1) return $"fraction {f} is not between 0 and 1";
            flags |= UiValuesNative.EditFraction;
            edit->Fraction = f;
        }
        if (e.Current is { } cur)
        {
            if (kind == UiValueKind.Detail && cur > int.MaxValue) return $"detail number {cur} above 2147483647";
            flags |= UiValuesNative.EditCurrent;
            edit->Current = cur;
        }
        if (e.Max is { } max)
        {
            if (kind == UiValueKind.Detail && max > int.MaxValue) return $"detail number {max} above 2147483647";
            if (pair && max == 0) return "a held or HUD maximum of 0";
            flags |= UiValuesNative.EditMaximum;
            edit->Maximum = max;
        }
        if (e.Label is { } label && label.Mode != UiLabelMode.Keep)
        {
            if (kind != UiValueKind.Detail) return "a label edit for a value without a label";
            switch (label.Mode)
            {
                case UiLabelMode.Set when label.Text is null:
                    return "Set without a text";
                case UiLabelMode.Set:
                    var rc = UiValuesNative.Intern(c, label.Text.ToString(), out var tid);
                    if (rc == UiValuesNative.BufferTooSmall)
                    {
                        budget = true;
                        return null;
                    }
                    if (rc != UiValuesNative.Ok) return "the label is not valid UTF-8 text (or holds a NUL)";
                    flags |= UiValuesNative.EditLabel;
                    edit->LabelId = tid;
                    break;
                case UiLabelMode.Clear when label.Text is not null:
                case UiLabelMode.Game when label.Text is not null:
                    return $"{label.Mode} with a text";
                case UiLabelMode.Clear:
                    flags |= UiValuesNative.EditLabel;
                    edit->LabelId = 0;
                    break;
                case UiLabelMode.Game:
                    flags |= UiValuesNative.EditGameLabel;
                    break;
                default:
                    return $"label mode {label.Mode}";
            }
        }
        edit->Flags = flags;
        return null;
    }

    private static void DropFaulted(long id) => Drop(id, UiValueProviderChange.Faulted);

    /// <summary>A fault of a provider (an exception, no decision, an invalid edit): the fifth in a row drops it, as native
    /// removes it at the same count.</summary>
    internal static void FaultOf(Reg reg, string what, Exception? e, Action<long> drop)
    {
        reg.Faults++;
        if (reg.Faults == 1)
        {
            if (reg.Mod is not null && e is not null) reg.Mod.Log.Error(e, $"ui.values {reg.Kind} provider {what}");
            else if (reg.Mod is not null) reg.Mod.Log.Error($"ui.values {reg.Kind} provider {what}");
            else UiEngine.Log(LogLevel.Error, $"ui.values {reg.Kind} provider {what}");
        }
        if (reg.Faults >= FaultLimit) drop(reg.Id);
    }

    internal static void RefusedOf(Reg reg, ulong frame, string reason)
    {
        if (reason.Length > 256) reason = reason[..256];
        if (reg.LastRefusal == reason) return;  // deduplicated until the reason changes
        reg.LastRefusal = reason;
        if (reg.Mod is not null) reg.Mod.Log.Info($"ui.values {reg.Kind} provider refused: {reason}");
        UiValueEvents.ProviderRefused(reg, frame, reason);
    }
}
