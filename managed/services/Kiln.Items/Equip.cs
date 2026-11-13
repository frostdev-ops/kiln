// SPDX-License-Identifier: MIT
// Equip and unequip a player's items: kiln.items v5 (native/core/abi/services/kiln_items.h "equipment"). The native side
// runs the game's own equip and unequip inside its program kiln_items_equip (an entity program over the player
// characters, through kiln.world); docs/re/items.md section 16. Capability items.equip.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>Options of <see cref="Items.Equip"/> and <see cref="Items.Unequip"/>.</summary>
[Flags]
public enum EquipOptions
{
    /// <summary>Nothing special.</summary>
    None = 0,

    /// <summary>Run the game's equip or unequip and roll it back: the result says what would happen, nothing
    /// changes.</summary>
    DryRun = 1,
}

/// <summary>What <see cref="Items.Equip"/> or <see cref="Items.Unequip"/> did. <see cref="Ok"/> when the game put the item
/// on or took it off (a dry run: would have); otherwise <see cref="Reason"/> says why and nothing changed. Statuses:
/// <see cref="ItemsStatus.Done"/>; <see cref="ItemsStatus.SlotChanged"/> (the slot is not one of the player's, is empty,
/// holds another item entity, or nothing is equipped there); <see cref="ItemsStatus.NoInventory"/> (no player character
/// with that id in the world); <see cref="ItemsStatus.NoRoom"/> (an unequip found no free backpack slot);
/// <see cref="ItemsStatus.Refused"/> (the game refused, <see cref="GameError"/>: the item does not fit the slot; or Kiln
/// refused: the player is dead right before the transaction, or 1024 requests are waiting); <see cref="ItemsStatus.Unavailable"/> and <see cref="ItemsStatus.Dropped"/> (equip went off,
/// or the server world closed, after the call).</summary>
/// <param name="Status">The outcome.</param>
/// <param name="Player">The player character.</param>
/// <param name="Slot">The equipment slot: the one asked for, or after an equip into the item's own slot the one it went
/// to; null when unknown.</param>
/// <param name="Unequipped">True for <see cref="Items.Unequip"/>.</param>
/// <param name="Item">The item put on or taken off; none when nothing was found.</param>
/// <param name="ItemEntity">Its item entity; none for a plain stack (arrows, bait).</param>
/// <param name="From">Where the item was.</param>
/// <param name="To">Where it is now (a dry run: would be); none when nothing changed.</param>
/// <param name="Replaced">An equip: what the equipment slot held before; none when it was empty.</param>
/// <param name="ReplacedEntity">The item entity of <paramref name="Replaced"/>.</param>
/// <param name="ReplacedTo">Where <paramref name="Replaced"/> went; none when not found.</param>
/// <param name="Message">Why, for anything but <see cref="ItemsStatus.Done"/>.</param>
/// <param name="GameError">The game's error code (0x11 refused, 0x15 no item, 0x1f no equipment or no room); 0 none.</param>
/// <param name="Steps">Sim steps between the call and the run.</param>
/// <param name="DryRun">Run with <see cref="EquipOptions.DryRun"/>: nothing changed.</param>
public sealed record EquipResult(
    ItemsStatus Status,
    EntityId Player,
    EquipmentSlot? Slot,
    bool Unequipped,
    ItemId Item,
    EntityId ItemEntity,
    SlotId From,
    SlotId To,
    ItemId Replaced,
    EntityId ReplacedEntity,
    SlotId ReplacedTo,
    string Message,
    int GameError,
    int Steps,
    bool DryRun) : IKilnResult
{
    /// <summary>True when the item was put on or taken off (a dry run: would have been).</summary>
    public bool Ok => Status == ItemsStatus.Done;

    /// <summary>Why it did not happen; null when <see cref="Ok"/>.</summary>
    public string? Reason => Ok ? null : Message.Length > 0 ? Message : Status.ToString();

    /// <summary>The item as one particular item (<see cref="ItemInstanceRef.None"/> for a plain stack, or when the capability
    /// <c>items.instances</c> is off).</summary>
    public ItemInstanceRef Ref { get; init; }

    /// <summary><see cref="Replaced"/> as one particular item.</summary>
    public ItemInstanceRef ReplacedRef { get; init; }

    /// <summary>The outcome in one line, for a log.</summary>
    public override string ToString() =>
        $"{(Unequipped ? "unequip" : "equip")} {Status} player {Player} slot {Slot?.ToString() ?? "?"} item {Item} {From} -> {To}" +
        (DryRun ? " (dry run)" : "") + (Message.Length > 0 ? $": {Message}" : "");

    internal static EquipResult Fail(ItemsStatus status, EntityId player, EquipmentSlot? slot, bool unequip, SlotId from,
        string message, bool dryRun) =>
        new(status, player, slot, unequip, default, default, from, default, default, default, default, message, 0, 0, dryRun);
}

/// <summary>Kiln's <see cref="Items.Equip"/> or <see cref="Items.Unequip"/> changed a player's equipment: posted once per
/// committed change, on the tick thread when its result arrives (also when one ring replaces another of the same item, and
/// for an equip and an unequip within a few steps). Not for a refusal or a dry run. Kiln.Players'
/// <c>PlayerEquipmentChangedEvent</c> is the poll that also sees a player's own changes (every four steps, by item
/// type). Not cancelable (it already happened). Capability <c>items.equip</c>.</summary>
[ServerOnly]
public sealed class EquipChangedEvent : KilnEvent
{
    internal EquipChangedEvent(EquipResult result) => Result = result;

    /// <summary>The committed change.</summary>
    public EquipResult Result { get; }

    /// <summary>The player.</summary>
    public EntityId Player => Result.Player;

    /// <summary>The equipment slot; null when the game's choice could not be read back.</summary>
    public EquipmentSlot? Slot => Result.Slot;

    /// <summary>What the slot held before (an unequip: the item taken off; an equip: what it replaced, none when it was
    /// empty).</summary>
    public ItemInstanceRef Before => Result.Unequipped ? Result.Ref : Result.ReplacedRef;

    /// <summary>What it holds now (none after an unequip).</summary>
    public ItemInstanceRef After => Result.Unequipped ? ItemInstanceRef.None : Result.Ref;

    /// <summary>The item type before (see <see cref="Before"/>).</summary>
    public ItemId BeforeItem => Result.Unequipped ? Result.Item : Result.Replaced;

    /// <summary>The item type now.</summary>
    public ItemId AfterItem => Result.Unequipped ? default : Result.Item;

    /// <summary>The change in one line, for a log.</summary>
    public override string ToString() => $"equip changed: player {Player} {Slot?.ToString() ?? "?"}: {BeforeItem} -> {AfterItem}";
}

public static unsafe partial class Items
{
    private static ItemsNative.TableV5* _v5;

    // The v5 table, or null when the native service is older.
    private static ItemsNative.TableV5* V5
    {
        get
        {
            if (_v5 != null) return _v5;
            var t = T;
            if (t == null || t->Header.Size < (uint)sizeof(ItemsNative.TableV5)) return null;
            return _v5 = (ItemsNative.TableV5*)t;
        }
    }

    /// <summary>
    /// Puts an item a player has on: the stack in <paramref name="from"/> (a slot of the player's own inventories) goes
    /// into the equipment slot <paramref name="slot"/>, or, with null, the slot the item names itself (a ring goes to
    /// the first ring slot the game picks). Runs the game's own equip, the one a player's equip action runs, with all its
    /// checks; what the slot held comes off as the game swaps it. The change reaches the player like their own equip,
    /// and Kiln.Players' <c>PlayerEquipmentChangedEvent</c> follows within four steps.
    /// </summary>
    /// <param name="player">The player character.</param>
    /// <param name="from">The slot holding the item (<see cref="InventorySlot.Slot"/> of <see cref="Read"/>).</param>
    /// <param name="slot">The equipment slot; null = the item's own.</param>
    /// <param name="options"><see cref="EquipOptions.DryRun"/> to only see what would happen.</param>
    /// <param name="itemEntity">The item entity the slot must hold (none = whatever it holds): a check that the slot
    /// did not change since it was read.</param>
    /// <returns>Completes on the tick thread one step after the step it ran in. A refusal is a result
    /// (<see cref="EquipResult.Ok"/> false): the inventory is then as it was.</returns>
    /// <remarks>The request runs in the next sim step, after the game handled the player's own equip actions of that step:
    /// both happen, the player's first. A player's own equip is never held back or replayed.</remarks>
    /// <exception cref="ArgumentException"><paramref name="player"/> or <paramref name="from"/> is none, or
    /// <paramref name="slot"/> is not an <see cref="EquipmentSlot"/>.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or the calling mod is unloading.</exception>
    /// <exception cref="KilnUnavailableException">The capability <c>items.equip</c> is off: an older Kiln, a game build
    /// without the game's equip, kiln.world or kiln.players not loaded, no death check on this build, equip switched off
    /// after a fault, or a game client that joined another machine's world.</exception>
    /// <example><code>
    /// var ring = Items.Read(player)!.Slots.First(s => s.Item == ringId);
    /// EquipResult r = await Items.Equip(player, ring.Slot, EquipmentSlot.Ring1);
    /// if (!r.Ok) Log.Info($"not equipped: {r.Reason}");
    /// </code></example>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(Caps.Equip)]
    public static Task<EquipResult> Equip(EntityId player, SlotId from, EquipmentSlot? slot = null,
        EquipOptions options = EquipOptions.None, EntityId itemEntity = default)
    {
        Services.RequireTickThread("Items.Equip");
        if (player.IsNone) throw new ArgumentException("no player", nameof(player));
        if (from.IsNone) throw new ArgumentException("no slot", nameof(from));
        if (slot is { } s && !Enum.IsDefined(s)) throw new ArgumentException("not an equipment slot", nameof(slot));
        CheckOptions(options);
        var r = new ItemsNative.EquipRequest
        {
            Size = (uint)sizeof(ItemsNative.EquipRequest),
            Player = player.Value,
            EquipmentSlot = slot is { } k ? GameSlot(k) : ItemsNative.EquipAny,
            Slot = from.Value,
            Pide = itemEntity.Value,
        };
        if (options.HasFlag(EquipOptions.DryRun)) r.Flags |= ItemsNative.EquipDryRun;
        return SubmitEquip(r, slot, from, "Items.Equip");
    }

    /// <summary>
    /// Takes off what <paramref name="player"/> wears or holds in <paramref name="slot"/>: the game's own unequip moves it
    /// into a free slot of the backpack. The change reaches the player like their own unequip, and Kiln.Players'
    /// <c>PlayerEquipmentChangedEvent</c> follows within four steps.
    /// </summary>
    /// <returns>Completes on the tick thread one step after the step it ran in. <see cref="ItemsStatus.SlotChanged"/>
    /// when nothing is equipped there, <see cref="ItemsStatus.NoRoom"/> when the backpack has no free slot (the game
    /// then shows the player its "inventory full" notice, as for their own unequip).</returns>
    /// <exception cref="ArgumentException"><paramref name="player"/> is none, or <paramref name="slot"/> is not an
    /// <see cref="EquipmentSlot"/>.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or the calling mod is unloading.</exception>
    /// <exception cref="KilnUnavailableException">The capability <c>items.equip</c> is off.</exception>
    /// <example><code>EquipResult r = await Items.Unequip(player, EquipmentSlot.Glider);</code></example>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(Caps.Equip)]
    public static Task<EquipResult> Unequip(EntityId player, EquipmentSlot slot, EquipOptions options = EquipOptions.None)
    {
        Services.RequireTickThread("Items.Unequip");
        if (player.IsNone) throw new ArgumentException("no player", nameof(player));
        if (!Enum.IsDefined(slot)) throw new ArgumentException("not an equipment slot", nameof(slot));
        CheckOptions(options);
        var r = new ItemsNative.EquipRequest
        {
            Size = (uint)sizeof(ItemsNative.EquipRequest),
            Flags = ItemsNative.EquipUnequip,
            Player = player.Value,
            EquipmentSlot = GameSlot(slot),
        };
        if (options.HasFlag(EquipOptions.DryRun)) r.Flags |= ItemsNative.EquipDryRun;
        return SubmitEquip(r, slot, default, "Items.Unequip");
    }

    /// <summary>Options other than <see cref="EquipOptions.DryRun"/> are a bad argument (a bit Kiln does not know would
    /// otherwise run a real change).</summary>
    internal static void CheckOptions(EquipOptions options)
    {
        if ((options & ~EquipOptions.DryRun) != 0) throw new ArgumentException($"unknown equip options {options}", nameof(options));
    }

    /// <summary>The game's keen::EquipmentSlot value of an <see cref="EquipmentSlot"/> (SlotSelection.equipment order:
    /// 0-16 are the same, HookShot 21, Glider 22, Backpack 23).</summary>
    internal static byte GameSlot(EquipmentSlot slot) => (int)slot switch
    {
        >= 0 and <= 16 => (byte)slot,
        >= 17 and <= 19 => (byte)((int)slot + 4),
        _ => throw new ArgumentException("not an equipment slot", nameof(slot)),
    };

    /// <summary>The <see cref="EquipmentSlot"/> of a keen::EquipmentSlot value; null for one that is no equipment slot
    /// (the building materials, Invalid, MainHand).</summary>
    internal static EquipmentSlot? SlotOfGame(byte game) => game switch
    {
        <= 16 => (EquipmentSlot)game,
        >= 21 and <= 23 => (EquipmentSlot)(game - 4),
        _ => null,
    };

    // ---- the capability ----

    private static ItemsNative.TableV5* RequireEquip(string api)
    {
        var t = V5;
        if (t == null)
            throw new KilnUnavailableException(Caps.Equip, T == null ? "kiln.items is not loaded" : $"kiln.items v{T->Header.Version} has no equip (update Kiln)");
        if (!Can(Caps.Equip)) throw new KilnUnavailableException(Caps.Equip, Capabilities.Get(Caps.Equip).ReasonText);
        var s = EquipStatusOf(t);
        if (s is { Available: false }) throw new KilnUnavailableException(Caps.Equip, s.Value.Detail);
        return t;
    }

    private static (bool Available, int Program, string Detail)? EquipStatusOf(ItemsNative.TableV5* t)
    {
        ItemsNative.EquipStatus s = default;
        s.Size = (uint)sizeof(ItemsNative.EquipStatus);
        if (t->GetEquipStatus(&s) != ItemsNative.Ok) return null;
        return (s.Available != 0, s.Program, ItemsNative.Text(s.Detail, 160));
    }

    // ---- submit and results ----

    private sealed class PendingEquip(TaskCompletionSource<EquipResult> tcs, ModContext? mod)
    {
        public readonly TaskCompletionSource<EquipResult> Tcs = tcs;
        public readonly ModContext? Mod = mod;
        public ulong Ticket;
    }

    private static readonly object EquipGate = new();
    private static readonly Dictionary<long, PendingEquip> EquipsInFlight = new();
    private static readonly HashSet<ModContext> EquipsWatched = new();
    private static long _nextEquip;

    private static Task<EquipResult> SubmitEquip(ItemsNative.EquipRequest r, EquipmentSlot? slot, SlotId from, string api)
    {
        var t = RequireEquip(api);
        var player = new EntityId(r.Player);
        var unequip = (r.Flags & ItemsNative.EquipUnequip) != 0;
        var dry = (r.Flags & ItemsNative.EquipDryRun) != 0;
        var mod = ModContext.CurrentMod;
        if (mod is { Alive: false }) throw new InvalidOperationException("the mod is unloading");
        var tcs = new TaskCompletionSource<EquipResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var p = new PendingEquip(tcs, mod);
        long key;
        lock (EquipGate)
        {
            key = ++_nextEquip;
            EquipsInFlight[key] = p;
            if (mod != null && EquipsWatched.Add(mod)) mod.Retiring += () => RetireEquips(mod);
        }
        ulong ticket = 0;
        var rc = t->Equip(Services.CallerOwner, &r, &OnEquipDone, (void*)key, &ticket);
        if (rc != ItemsNative.Ok)
        {
            lock (EquipGate) EquipsInFlight.Remove(key);
            if (SubmitRefusal(rc, player, slot, unequip, from, dry) is { } refused) return Task.FromResult(refused);
            if (rc == ItemsNative.InvalidArgument) throw new ArgumentException($"{api}: kiln.items refused the request's arguments");
            throw new KilnUnavailableException(Caps.Equip, EquipStatusOf(t)?.Detail is { Length: > 0 } d ? d : $"kiln.items refused it (error {rc})");
        }
        lock (EquipGate)
        {
            if (EquipsInFlight.TryGetValue(key, out var still)) still.Ticket = ticket;
        }
        return tcs.Task;
    }

    /// <summary>A submission the native side refused for now (the queue is full) is a result, not an exception: the
    /// capability is there (the one error rule). Null for every other code.</summary>
    internal static EquipResult? SubmitRefusal(int rc, EntityId player, EquipmentSlot? slot, bool unequip, SlotId from, bool dry) =>
        rc == ItemsNative.BufferTooSmall
            ? EquipResult.Fail(ItemsStatus.Refused, player, slot, unequip, from, "1024 equip requests are waiting: try again in a step", dry)
            : null;

    private static void RetireEquips(ModContext mod)
    {
        List<ulong> tickets = new();
        lock (EquipGate)
        {
            EquipsWatched.Remove(mod);
            foreach (var (k, p) in EquipsInFlight.Where(kv => kv.Value.Mod == mod).ToList())
            {
                EquipsInFlight.Remove(k);
                if (p.Ticket != 0) tickets.Add(p.Ticket);
            }
        }
        var t = _table;
        if (t != null) foreach (var tk in tickets) t->Cancel(tk);
    }

    /// <summary>The managed result of a native one (pure: the unit tests build native results by hand).</summary>
    internal static EquipResult ToResult(in ItemsNative.EquipResult n)
    {
        var status = Enum.IsDefined(typeof(ItemsStatus), n.Status) ? (ItemsStatus)n.Status : ItemsStatus.Refused;
        string message;
        fixed (byte* m = n.Message) message = ItemsNative.Text(m, 160);
        return new EquipResult(status, new EntityId(n.Player), SlotOfGame(n.EquipmentSlot),
            (n.Flags & ItemsNative.EquipUnequip) != 0, new ItemId(n.Item), new EntityId(n.Pide), new SlotId(n.From),
            new SlotId(n.To), new ItemId(n.Replaced), new EntityId(n.ReplacedPide), new SlotId(n.ReplacedTo), message,
            n.GameError, (int)n.Steps, (n.Flags & ItemsNative.EquipDryRun) != 0)
        {
            Ref = ItemRefs.Of(n.Pide, n.Item, n.Stamp),
            ReplacedRef = ItemRefs.Of(n.ReplacedPide, n.Replaced, n.ReplacedStamp),
        };
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnEquipDone(ItemsNative.EquipResult* r, void* user)
    {
        try
        {
            PendingEquip? p;
            lock (EquipGate)
            {
                if (!EquipsInFlight.Remove((long)user, out p)) return;
            }
            var result = ToResult(*r);
            ItemTracker.OnEquip(result);
            if (result is { Ok: true, DryRun: false } && Events.HasSubscribers<EquipChangedEvent>())
                Events.Post(new EquipChangedEvent(result));
            p.Tcs.TrySetResult(result);
        }
        catch
        {
            // never let an exception cross into native code
        }
    }
}
