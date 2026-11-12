// SPDX-License-Identifier: MIT
// kiln.items v4 on the managed side (native/core/abi/services/kiln_items.h "item instances"): one particular item as an
// ItemInstanceRef, where it sits, the fields of its save record, the item entity source of EntityRef, and the tracker
// that follows chosen items (ItemMovedEvent). Capability items.instances. Guide: docs/kiln/services/items.md.
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>Where one item sits: the holder whose inventory holds it (a chest, a player, a villager; a chest's first
/// inventory is the chest itself), the slot, the link of the holder's inventory setup, and the flat slot.</summary>
/// <param name="Holder">The entity whose inventory setup links the slot's inventory; <see cref="EntityId.None"/> when the
/// item entity sits in no holder's slot right now.</param>
/// <param name="Slot">The slot (inventory entity and index).</param>
/// <param name="Link">The inventory setup's link (0-15; an inventory nested in a Virtual link: the outer link); -1 when
/// not placed.</param>
/// <param name="FlatSlot">0-255: the slot number the game's world and character save records use (8 per Generic link,
/// counted over the Generic links only). 256 and up: a slot the game's save does not number this way (equipment,
/// currency, customization, nested links), unique in the holder. -1 when not placed.</param>
public readonly record struct ItemLocation(EntityId Holder, SlotId Slot, int Link, int FlatSlot)
{
    /// <summary>True when the item sits in a slot of a holder's inventory.</summary>
    public bool IsPlaced => !Holder.IsNone;

    /// <summary>True when <see cref="FlatSlot"/> is the slot number of the game's save record.</summary>
    public bool IsSavedSlot => IsPlaced && FlatSlot is >= 0 and < 256;

    /// <summary><c>"1042/13 (1043:5)"</c>, <c>"nowhere (1043:5)"</c>.</summary>
    public override string ToString() => IsPlaced ? $"{Holder}/{FlatSlot} ({Slot})" : $"nowhere ({Slot})";
}

/// <summary>How Kiln learned that a followed item moved (<see cref="ItemMovedEvent.Cause"/>).</summary>
public enum ItemMoveCause
{
    /// <summary>Kiln's own <see cref="Items.Move"/> named the item entity in its result.</summary>
    Transaction = 0,

    /// <summary>The game's quick stack or move all (a player's R key, a Quick Stack Station, <see cref="Items.QuickStack"/>,
    /// <see cref="Items.MoveAll"/>) ran between the item's holder and another: Kiln read the item again at once.</summary>
    Transfer = 1,

    /// <summary>Kiln's read of the followed items, once per step, found it somewhere else: a move no hook names (a
    /// player's single drag, equip, a gift, death to a tombstone, a station taking it). Reported one step after the read
    /// that first saw it, so a hooked report of the same move wins.</summary>
    Observed = 2,
}

/// <summary>A followed item moved: another holder or another slot. Kiln follows the items that carry instance data
/// (Kiln.Saves' <c>InstanceData</c>); there is no event for other items. Posted on the tick thread, only while the
/// event has subscribers. Not cancelable (it already happened). Capability <c>items.instances</c>.</summary>
[ServerOnly]
public sealed class ItemMovedEvent : KilnEvent
{
    internal ItemMovedEvent(ItemInstanceRef item, ItemLocation from, ItemLocation to, ItemMoveCause cause, ulong step)
    {
        Item = item;
        From = from;
        To = to;
        Cause = cause;
        Step = step;
    }

    /// <summary>The item (the same entity and stamp before and after).</summary>
    public ItemInstanceRef Item { get; }

    /// <summary>Where it was when Kiln saw it last.</summary>
    public ItemLocation From { get; }

    /// <summary>Where it is now.</summary>
    public ItemLocation To { get; }

    /// <summary>How Kiln learned it.</summary>
    public ItemMoveCause Cause { get; }

    /// <summary>The step it was reported in.</summary>
    public ulong Step { get; }

    public override string ToString() => $"{Item} moved {From} -> {To} ({Cause}, step {Step})";
}

/// <summary>One of a player's equipment slots, named and ordered as the game's <c>SlotSelection.equipment</c> fields
/// (the same values as Kiln.Players' <c>PlayerEquipSlot</c>).</summary>
public enum EquipmentSlot
{
    VanityHead, VanityUpperBody, VanityArms, VanityLowerBody, VanityFeet,
    ArmourHead, ArmourUpperBody, ArmourArms, ArmourLowerBody, ArmourFeet,
    Ring0, Ring1, RangedWeapon, Shield, Arrow, Spell, FishingBait, HookShot, Glider, Backpack,
}

/// <summary>Where an <see cref="ItemInstanceRef"/> is (capability <c>items.instances</c>). The data calls on an item
/// (<c>Get</c>, <c>Set</c>, ...) are in Kiln.Saves.</summary>
public static class ItemInstanceExtensions
{
    /// <summary>Where the item is now; null when it is gone or the ref is stale (the id now names another item
    /// entity). A placed item has a <see cref="ItemLocation.Holder"/>; an item entity in no holder's slot has
    /// <see cref="EntityId.None"/> there.</summary>
    /// <exception cref="ArgumentException"><paramref name="item"/> is none.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <exception cref="KilnUnavailableException">The capability <c>items.instances</c> is off (an older Kiln, an
    /// unknown game build, or the server world is not running).</exception>
    [ServerOnly]
    [TickThreadOnly]
    public static ItemLocation? Location(this ItemInstanceRef item)
    {
        Services.RequireTickThread("ItemInstanceRef.Location");
        if (item.IsNone) throw new ArgumentException("no item", nameof(item));
        return ItemInstances.Read(item)?.Location;
    }
}

public static unsafe partial class Items
{
    private static ItemsNative.TableV4* _v4;

    // The v4 table, or null when the native service is older.
    internal static ItemsNative.TableV4* V4
    {
        get
        {
            if (_v4 != null) return _v4;
            var t = T;
            if (t == null || t->Header.Size < (uint)sizeof(ItemsNative.TableV4)) return null;
            return _v4 = (ItemsNative.TableV4*)t;
        }
    }

    /// <summary>The item in <paramref name="player"/>'s hand: what the running action uses, else the selected
    /// action-bar slot (as the game picks it). <see cref="ItemInstanceRef.None"/> when the hand is empty, holds a plain
    /// stack (no item entity), or the entity is not a player.</summary>
    /// <exception cref="ArgumentException"><paramref name="player"/> is none.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <exception cref="KilnUnavailableException">The capability <c>items.instances</c> is off, or the game's type
    /// reflection (kiln.types) is unavailable.</exception>
    [ServerOnly]
    [TickThreadOnly]
    public static ItemInstanceRef Held(EntityId player)
    {
        Services.RequireTickThread("Items.Held");
        if (player.IsNone) throw new ArgumentException("no player", nameof(player));
        ItemInstances.Require();
        var sel = PlayerSlots.Selection(player);
        return sel == null ? ItemInstanceRef.None : ItemInstances.At(player, PlayerSlots.Held(sel));
    }

    /// <summary>The item <paramref name="player"/> wears or holds in an equipment slot.
    /// <see cref="ItemInstanceRef.None"/> when the slot is empty or the entity is not a player.</summary>
    /// <exception cref="ArgumentException"><paramref name="player"/> is none, or <paramref name="slot"/> is not an
    /// <see cref="EquipmentSlot"/>.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <exception cref="KilnUnavailableException">The capability <c>items.instances</c> is off, the game's type reflection
    /// (kiln.types) is unavailable, or this game build has no such equipment field.</exception>
    [ServerOnly]
    [TickThreadOnly]
    public static ItemInstanceRef Equipped(EntityId player, EquipmentSlot slot)
    {
        Services.RequireTickThread("Items.Equipped");
        if (player.IsNone) throw new ArgumentException("no player", nameof(player));
        if (!Enum.IsDefined(slot)) throw new ArgumentException("not an equipment slot", nameof(slot));
        ItemInstances.Require();
        var sel = PlayerSlots.Selection(player);
        return sel == null ? ItemInstanceRef.None : ItemInstances.At(player, PlayerSlots.Equipment(sel, slot));
    }

    /// <summary>Lab only: follows an item as Kiln.Saves does for an item with data (<see cref="ItemMovedEvent"/>);
    /// <paramref name="gone"/> runs once when it is gone, with its last known location and the step. Dispose to
    /// stop.</summary>
    [Experimental("KILNX001")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [ServerOnly]
    [TickThreadOnly]
    public static IDisposable LabFollow(ItemInstanceRef item, Action<ItemLocation, ulong>? gone = null)
    {
        var handle = ItemInstances.Follow(item);
        if (gone == null) return handle;
        Action<ItemInstanceRef, ItemLocation, ulong>? watch = null;
        watch = (i, last, step) =>
        {
            if (i != item) return;
            ItemInstances.Gone -= watch;
            gone(last, step);
        };
        ItemInstances.Gone += watch;
        return new LabFollowHandle(handle, () => ItemInstances.Gone -= watch);
    }

    private sealed class LabFollowHandle(IDisposable inner, Action detach) : IDisposable
    {
        public void Dispose()
        {
            detach();
            inner.Dispose();
        }
    }

    /// <summary>Lab only: <see cref="Move"/>, with the result kept away from the item tracker, so only its read of the
    /// followed items once per step can see the move (cause <see cref="ItemMoveCause.Observed"/>).</summary>
    [Experimental("KILNX001")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [ServerOnly]
    [TickThreadOnly]
    public static Task<MoveResult> LabMoveUnseen(EntityId from, SlotId slot, EntityId to)
    {
        if (slot.IsNone) return MoveInvalid(MoveKind.Stack, from, to, "no slot");
        var r = NewMove(MoveKind.Stack, from, to, MoveOptions.None);
        r.Slot = slot.Value;
        return SubmitMove(r, "Items.LabMoveUnseen", unseen: true);
    }

    /// <summary>Lab only: the tracker's last read of all followed items: how many, and how long it took
    /// (microseconds), averaged over the last 64 steps with something followed; and the stamps' state.</summary>
    [Experimental("KILNX001")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static string LabInstancesStatus => ItemTracker.Describe() + "; " + ItemInstances.DescribeNative();
}

/// <summary>The fields the game writes into an item's save record, read from the item entity now (as the game's record
/// builder reads them). A field the item does not have is 0.</summary>
internal readonly record struct ItemRecord(ItemId Item, int Count, int Level, int Durability, int DurabilityMax,
    int Fuel, int FuelMax, ItemId Dye, int PerksAvailable, int PerksUnlocked, ItemId Gem, int GemLevel);

/// <summary>One item entity now: the ref, where it sits, and its save record's fields.</summary>
internal readonly record struct ItemInstanceInfo(ItemInstanceRef Item, ItemLocation Location, ItemRecord Record);

/// <summary>The internal face of item instances for Kiln.Saves (instance data: storage and matching).</summary>
internal static unsafe class ItemInstances
{
    internal const string Capability = "items.instances";

    /// <summary>Throws <see cref="KilnUnavailableException"/>(<c>items.instances</c>) when off.</summary>
    internal static void Require()
    {
        if (Items.V4 == null)
            throw new KilnUnavailableException(Capability, Items.Available
                ? "this Kiln runtime's kiln.items has no item instances (v4)"
                : "kiln.items is unavailable");
        var st = NativeStatus();
        if ((st.Flags & ItemsNative.InstancesOn) == 0)
            throw new KilnUnavailableException(Capability, ItemsNative.Text(st.Detail, 160) is { Length: > 0 } d ? d : "off on this build");
    }

    /// <summary>True when the native service has v4 and the feature is on (startup checks).</summary>
    internal static bool On => Items.V4 != null && (NativeStatus().Flags & ItemsNative.InstancesOn) != 0;

    /// <summary>Why it is off, in words ("" when on).</summary>
    internal static string WhyOff
    {
        get
        {
            if (Items.V4 == null)
                return Items.Available ? "this Kiln runtime's kiln.items has no item instances (v4)" : "kiln.items is unavailable";
            var st = NativeStatus();
            return (st.Flags & ItemsNative.InstancesOn) != 0 ? "" : ItemsNative.Text(st.Detail, 160);
        }
    }

    private static ItemsNative.InstancesStatus NativeStatus()
    {
        ItemsNative.InstancesStatus s = default;
        s.Size = (uint)sizeof(ItemsNative.InstancesStatus);
        var t = Items.V4;
        if (t != null) t->GetInstancesStatus(&s);
        return s;
    }

    internal static string DescribeNative()
    {
        var s = NativeStatus();
        return $"stamps issued {s.StampsIssued}, table {s.TableSize}, resets {s.Resets}, entity id counter {s.NextEntityId}, " +
               $"mismatches {s.Mismatches}, holder map builds {s.HolderRebuilds}, reads {s.Reads}" +
               ((s.Flags & ItemsNative.InstancesOn) == 0 ? $", OFF: {ItemsNative.Text(s.Detail, 160)}" : "");
    }

    /// <summary>The item now; null when it is gone or the ref's stamp is no longer the id's stamp (or none). Tick thread.</summary>
    internal static ItemInstanceInfo? Read(ItemInstanceRef item)
    {
        Services.RequireTickThread("ItemInstances.Read");
        Require();
        if (item.IsNone || IdentitySources.PlaceOf(item.Entity.Serial) != EntitySourceOrder.Items) return null;
        ItemsNative.Instance x = default;
        x.Entity = item.Entity.Id.Value;
        Check(Items.V4->ReadInstances(&x, 1, (uint)sizeof(ItemsNative.Instance), 0), "ItemInstances.Read");
        if ((x.Flags & ItemsNative.InstFound) == 0 || RefOf(in x) != item) return null;
        return InfoOf(in x);
    }

    /// <summary>Every item entity in the holder's inventories, in flat-slot order (saved slots first); empty when it has
    /// no inventory or the inventory is not initialized yet. Tick thread.</summary>
    internal static IReadOnlyList<ItemInstanceInfo> ReadHolder(EntityId holder, out bool initialized)
    {
        Services.RequireTickThread("ItemInstances.ReadHolder");
        Require();
        initialized = false;
        if (holder.IsNone) return [];
        var cap = 64u;
        while (true)
        {
            var buf = new ItemsNative.Instance[cap];
            uint n = 0;
            byte init = 0;
            int rc;
            fixed (ItemsNative.Instance* p = buf)
                rc = Items.V4->ReadHolder(holder.Value, p, cap, (uint)sizeof(ItemsNative.Instance), &n, &init);
            if (rc == ItemsNative.NotFound) return [];
            if (rc == ItemsNative.BufferTooSmall && cap < 65536)
            {
                cap = Math.Max(cap * 2, n + 16);
                continue;
            }
            Check(rc == ItemsNative.BufferTooSmall ? ItemsNative.Ok : rc, "ItemInstances.ReadHolder");
            initialized = init != 0;
            var list = new List<ItemInstanceInfo>((int)Math.Min(n, cap));
            for (var i = 0; i < n && i < cap; i++)
                if ((buf[i].Flags & ItemsNative.InstFound) != 0) list.Add(InfoOf(in buf[i]));
            return list;
        }
    }

    /// <summary>Counted: the item is followed until every handle is disposed or it is gone. Tick thread.</summary>
    internal static IDisposable Follow(ItemInstanceRef item) => ItemTracker.Follow(item);

    /// <summary>Tick thread, in the service tick or a result callback. A followed item changed its holder or slot.</summary>
    internal static event Action<ItemInstanceRef, ItemLocation, ItemLocation, ItemMoveCause>? Moved
    {
        add => ItemTracker.Moved += value;
        remove => ItemTracker.Moved -= value;
    }

    /// <summary>Tick thread. A followed item no longer exists: its last known location and the step it was noticed in.</summary>
    internal static event Action<ItemInstanceRef, ItemLocation, ulong>? Gone
    {
        add => ItemTracker.Gone += value;
        remove => ItemTracker.Gone -= value;
    }

    // ---- plumbing ----

    /// <summary>Reads item entities in one native call (flags: ItemsNative.ReadLocation for locations only).</summary>
    internal static void ReadRaw(Span<ItemsNative.Instance> items, uint flags, string api)
    {
        if (items.Length == 0) return;
        fixed (ItemsNative.Instance* p = items)
            Check(Items.V4->ReadInstances(p, (uint)items.Length, (uint)sizeof(ItemsNative.Instance), flags), api);
    }

    internal static void Check(int rc, string api)
    {
        switch (rc)
        {
            case ItemsNative.Ok:
                return;
            case ItemsNative.Unavailable:
                throw new KilnUnavailableException(Capability, $"{api}: kiln.items cannot read the world right now (the server world is not running)");
            case ItemsNative.WrongThread:
                throw new InvalidOperationException($"{api} must be called on the tick thread");
            default:
                throw new InvalidOperationException($"{api} failed ({rc})");
        }
    }

    internal static ItemInstanceRef RefOf(in ItemsNative.Instance x) =>
        (x.Flags & ItemsNative.InstFound) == 0 ? ItemInstanceRef.None : ItemRefs.Of(x.Entity, x.Item, x.Stamp);

    internal static ItemLocation LocationOf(in ItemsNative.Instance x) => (x.Flags & ItemsNative.InstPlaced) != 0
        ? new ItemLocation(new EntityId(x.Holder), new SlotId(x.Slot), x.Link, (int)x.FlatSlot)
        : new ItemLocation(EntityId.None, new SlotId(x.Slot), -1, -1);

    internal static ItemInstanceInfo InfoOf(in ItemsNative.Instance x) => new(RefOf(in x), LocationOf(in x),
        new ItemRecord(new ItemId(x.Item), (int)x.Count,
            (x.Flags & ItemsNative.InstLevel) != 0 ? (int)(x.Level & 0xFF) : 0,  // the record keeps the low byte
            x.Durability, x.DurabilityMax, x.Fuel, x.FuelMax,
            (x.Flags & ItemsNative.InstDye) != 0 ? new ItemId(x.Dye) : ItemId.None,
            x.PerksAvailable, x.PerksUnlocked,
            (x.Flags & ItemsNative.InstGem) != 0 ? new ItemId(x.Gem) : ItemId.None, x.GemLevel));

    /// <summary>The item entity in <paramref name="slot"/> of <paramref name="holder"/>'s inventories, or none.</summary>
    internal static ItemInstanceRef At(EntityId holder, SlotId slot)
    {
        if (slot.IsNone) return ItemInstanceRef.None;
        foreach (var info in ReadHolder(holder, out _))
            if (info.Location.Slot == slot) return info.Item;
        return ItemInstanceRef.None;
    }
}

/// <summary>Refs from what the native service delivered: the item entity and its stamp (0 = none).</summary>
internal static class ItemRefs
{
    internal static ItemInstanceRef Of(uint pide, uint item, uint stamp) =>
        pide == 0 || pide == 0xFFFFFFFFu || stamp == 0
            ? ItemInstanceRef.None
            : new ItemInstanceRef(new EntityRef(new EntityId(pide), IdentitySources.Stamp(EntitySourceOrder.Items, stamp)),
                new ItemId(item));

    /// <summary>The ids kiln.items stamps: the engine's counter range (players are 1..1023; bit 31 is another id space).</summary>
    internal static bool Stampable(uint id) => id is >= 1024 and <= 0x7FFFFFFF;
}

/// <summary>Kiln.Items' start (ServiceTick.AutoStart runs this static constructor at the first service tick): puts the
/// item entity source into <see cref="IdentitySources"/>' order when kiln.items has item instances; otherwise logs once
/// why item entities have no stamps (kiln.spawn's source then answers for them, with weaker serials).</summary>
internal static class ItemsStartup
{
    static ItemsStartup()
    {
        if (Environment.GetEnvironmentVariable("KILN_LAB_NO_ENTITY_SOURCES") == "1")
        {
            Backend.Current.Log(0, LogLevel.Warn, "kiln.items: KILN_LAB_NO_ENTITY_SOURCES=1: no item entity source (lab switch)");
            return;
        }
        if (!ItemInstances.On)
        {
            Backend.Current.Log(0, LogLevel.Info, $"kiln.items: item entities get no stamps ({ItemInstances.WhyOff}); " +
                "EntityRef answers for them through kiln.spawn's source");
            return;
        }
        IdentitySources.SetEntitySource(EntitySourceOrder.Items, ItemStampSource.Instance);
    }
}

/// <summary>kiln.items' entity stamps: an item entity is <c>Alive</c> with its stamp in the Items place; an id that is
/// not an item entity is <c>NotMine</c> (the next source answers); an item entity kiln.items saw destroyed is
/// <c>Absent</c>. kiln.items unable to read right now throws <c>identity.entities</c>.</summary>
internal sealed unsafe class ItemStampSource : IEntityStampSource
{
    internal static readonly ItemStampSource Instance = new();

    public EntitySerialAnswer Query(EntityId entity, out ulong stamp)
    {
        stamp = 0;
        if (!ItemRefs.Stampable(entity.Value)) return EntitySerialAnswer.NotMine;
        var t = Items.V4;
        if (t == null) throw new KilnUnavailableException(EntityRef.Capability, "kiln.items is unavailable");
        ItemsNative.Instance x = default;
        x.Entity = entity.Value;
        var rc = t->ReadInstances(&x, 1, (uint)sizeof(ItemsNative.Instance), ItemsNative.ReadLocation);
        if (rc == ItemsNative.Unavailable)
            throw new KilnUnavailableException(EntityRef.Capability, "kiln.items cannot read the world right now (the server world is not running)");
        if (rc != ItemsNative.Ok)
            throw new KilnUnavailableException(EntityRef.Capability, $"kiln.items could not read entity {entity} ({rc})");
        if ((x.Flags & ItemsNative.InstGone) != 0) return EntitySerialAnswer.Absent;
        if ((x.Flags & ItemsNative.InstFound) == 0) return EntitySerialAnswer.NotMine;
        stamp = IdentitySources.Stamp(EntitySourceOrder.Items, x.Stamp);
        return EntitySerialAnswer.Alive;
    }
}

/// <summary>A player's slots from the game's <c>SlotSelection</c>, by the game's own field names (kiln.types), as
/// kiln.players reads them.</summary>
internal static class PlayerSlots
{
    internal static ReflectedValue? Selection(EntityId player)
    {
        if (!GameTypes.Available)
            throw new KilnUnavailableException(ItemInstances.Capability, "the game's type reflection (kiln.types) is unavailable");
        try
        {
            return Reflected.Component(player, "SlotSelection");
        }
        catch (ArgumentException e)
        {
            throw new KilnUnavailableException(ItemInstances.Capability, $"SlotSelection is not known on this build: {e.Message}");
        }
    }

    /// <summary>What the running action uses, else the selected action-bar slot (8 slots in each of two bar inventories).</summary>
    internal static SlotId Held(ReflectedValue sel)
    {
        var action = SlotOf(Field(sel, "actionItem.slotId"));
        if (!action.IsNone) return action;
        var index = (int)U32(Field(sel, "actionbarSlotSelection.index"));
        if (index is < 0 or >= 16) return default;
        var bar = U32(Field(sel, $"actionbarEntityIds[{index >> 3}]"));
        return bar == 0 ? default : SlotId.Of(new EntityId(bar), index & 7);
    }

    internal static SlotId Equipment(ReflectedValue sel, EquipmentSlot slot)
    {
        var eq = sel.Type.Field("equipment")?.Type.Resolved
            ?? throw new KilnUnavailableException(ItemInstances.Capability, "SlotSelection has no equipment field on this build");
        var fields = eq.Fields;
        var i = (int)slot;
        if (i >= fields.Count || !SameName(fields[i].Name, slot.ToString()))
            throw new KilnUnavailableException(ItemInstances.Capability,
                $"SlotSelection.equipment has no field {slot} at {i} on this build ({(i < fields.Count ? fields[i].Name : "none")})");
        return SlotOf(Field(sel, "equipment." + fields[i].Name));
    }

    private static bool SameName(string game, string ours) =>
        string.Equals(game.Replace("_", ""), ours, StringComparison.OrdinalIgnoreCase);

    private static object? Field(ReflectedValue sel, string path)
    {
        try
        {
            return sel[path];
        }
        catch (ArgumentException e)
        {
            throw new KilnUnavailableException(ItemInstances.Capability, $"SlotSelection.{path} is not known on this build: {e.Message}");
        }
    }

    // An InventorySlotId {u32 inventory entity; u8 index} (docs/re/player-state.md).
    private static SlotId SlotOf(object? v)
    {
        if (v is not ReflectedValue r) return default;
        var b = r.Bytes;
        if (b.Length < 5) return default;
        var e = BitConverter.ToUInt32(b, 0);
        var index = b[4];
        return e == 0 || index > 7 ? default : SlotId.Of(new EntityId(e), index);
    }

    private static uint U32(object? v) => v switch
    {
        long l => (uint)l,
        ulong u => (uint)u,
        bool b => b ? 1u : 0u,
        ReflectedValue r when r.Bytes is { Length: >= 4 } b => BitConverter.ToUInt32(b, 0),
        ReflectedValue r when r.Bytes is { Length: >= 1 } b => b[0],
        _ => 0,
    };
}

/// <summary>Follows chosen items (Kiln.Saves' items with data; <see cref="Items.LabFollow"/> in labs) and says when one
/// moved or is gone. In order: Kiln's own move results name the item entity (<see cref="ItemMoveCause.Transaction"/>);
/// a transfer report (quick stack, move all) makes it read again at once the followed items whose holder is the
/// report's from or to (<see cref="ItemMoveCause.Transfer"/>); one batched read of every followed item per step catches
/// the rest (<see cref="ItemMoveCause.Observed"/>, reported once two reads in a row agree, so a hooked report of the
/// same move wins). Nothing is read per step while nothing is followed. Tick thread.</summary>
internal static unsafe class ItemTracker
{
    private sealed class Followed(ItemInstanceRef item, ItemLocation last)
    {
        public readonly ItemInstanceRef Item = item;
        public ItemLocation Last = last;
        public int Handles = 1;
        public ItemLocation? Candidate;  // seen somewhere else by the per-step read once; reported when seen again
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<EntityRef, Followed> ByRef = new();
    private static IDisposable? _tick;
    private static ulong _reporter;
    private static bool _reportFailed;
    private static readonly double[] Costs = new double[64];
    private static int _costN, _costAt, _lastCount;

    internal static event Action<ItemInstanceRef, ItemLocation, ItemLocation, ItemMoveCause>? Moved;
    internal static event Action<ItemInstanceRef, ItemLocation, ulong>? Gone;

    internal static int Count
    {
        get
        {
            lock (Gate) return ByRef.Count;
        }
    }

    internal static IDisposable Follow(ItemInstanceRef item)
    {
        Services.RequireTickThread("ItemInstances.Follow");
        ItemInstances.Require();
        if (item.IsNone) throw new ArgumentException("no item", nameof(item));
        if (IdentitySources.PlaceOf(item.Entity.Serial) != EntitySourceOrder.Items)
            throw new ArgumentException("not a ref of an item entity (its stamp is not kiln.items')", nameof(item));
        var now = ItemInstances.Read(item);
        lock (Gate)
        {
            if (ByRef.TryGetValue(item.Entity, out var f)) f.Handles++;
            else ByRef[item.Entity] = new Followed(item, now?.Location ?? default);
            _tick ??= ServiceTickAccess.Add("kiln.items followed items", Step);
        }
        SyncReporter();
        return new Handle(item.Entity);
    }

    private sealed class Handle(EntityRef key) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            lock (Gate)
            {
                if (ByRef.TryGetValue(key, out var f) && --f.Handles <= 0) ByRef.Remove(key);
            }
            SyncReporter();
        }
    }

    internal static string Describe()
    {
        lock (Gate)
        {
            var avg = _costN == 0 ? 0 : Costs.Take(_costN).Average();
            return $"followed {ByRef.Count}, last read {_lastCount} item(s), {avg:0.0} us per step (avg of {_costN})";
        }
    }

    // ---- the per-step read ----
    private static void Step(ulong step)
    {
        Followed[] all;
        lock (Gate)
        {
            if (ByRef.Count == 0) return;
            all = ByRef.Values.ToArray();
        }
        if (!ItemInstances.On) return;
        var buf = new ItemsNative.Instance[all.Length];
        for (var i = 0; i < all.Length; i++) buf[i].Entity = all[i].Item.Entity.Id.Value;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            ItemInstances.ReadRaw(buf, ItemsNative.ReadLocation, "the item tracker");
        }
        catch (KilnUnavailableException)
        {
            return;  // the world is not running: nothing moves
        }
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        lock (Gate)
        {
            Costs[_costAt] = us;
            _costAt = (_costAt + 1) % Costs.Length;
            _costN = Math.Min(_costN + 1, Costs.Length);
            _lastCount = all.Length;
        }
        for (var i = 0; i < all.Length; i++)
        {
            var f = all[i];
            if (ItemInstances.RefOf(in buf[i]) != f.Item)
            {
                ReportGone(f, step);
                continue;
            }
            var loc = ItemInstances.LocationOf(in buf[i]);
            if (loc == f.Last)
            {
                f.Candidate = null;
                continue;
            }
            if (f.Candidate == loc) Report(f, loc, ItemMoveCause.Observed, step);
            else f.Candidate = loc;
        }
    }

    // ---- what the hooks and results say ----

    /// <summary>A give or take of Kiln completed (tick thread, Pre). A take names the item entities it destroyed.</summary>
    internal static void OnTransaction(ItemsResult result, bool take)
    {
        if (!take || result.DryRun || !result.Succeeded || Count == 0) return;
        foreach (var c in result.Changes)
        {
            if (c.Ref.IsNone) continue;
            Followed? f;
            lock (Gate) ByRef.TryGetValue(c.Ref.Entity, out f);
            if (f != null) ReportGone(f, ServiceTick.Step);
        }
    }

    /// <summary>A move of Kiln completed (tick thread, Pre). A stack move names the item entities it moved: cause
    /// Transaction. Kiln's quick stack and move all run the game's own functions, whose transfer report follows (cause
    /// Transfer); silent ones send none, so their results count as the transaction.</summary>
    internal static void OnMove(MoveResult result, bool silent)
    {
        if (result.DryRun || !result.Succeeded || Count == 0) return;
        if (result.Kind != MoveKind.Stack && !silent) return;
        var named = new List<Followed>();
        lock (Gate)
        {
            foreach (var c in result.Changes)
                if (!c.Ref.IsNone && ByRef.TryGetValue(c.Ref.Entity, out var f)) named.Add(f);
        }
        Reread(named, ItemMoveCause.Transaction);
    }

    private static void OnTransfer(EntityId from, EntityId to)
    {
        var near = new List<Followed>();
        lock (Gate)
        {
            foreach (var f in ByRef.Values)
                if (f.Last.Holder == from || f.Last.Holder == to) near.Add(f);
        }
        Reread(near, ItemMoveCause.Transfer);
    }

    private static void Reread(List<Followed> items, ItemMoveCause cause)
    {
        if (items.Count == 0 || !ItemInstances.On) return;
        var buf = new ItemsNative.Instance[items.Count];
        for (var i = 0; i < items.Count; i++) buf[i].Entity = items[i].Item.Entity.Id.Value;
        try
        {
            ItemInstances.ReadRaw(buf, ItemsNative.ReadLocation, "the item tracker");
        }
        catch (KilnUnavailableException)
        {
            return;
        }
        var step = ServiceTick.Step;
        for (var i = 0; i < items.Count; i++)
        {
            var f = items[i];
            if (ItemInstances.RefOf(in buf[i]) != f.Item)
            {
                ReportGone(f, step);
                continue;
            }
            var loc = ItemInstances.LocationOf(in buf[i]);
            if (loc != f.Last) Report(f, loc, cause, step);
            else f.Candidate = null;
        }
    }

    private static void Report(Followed f, ItemLocation to, ItemMoveCause cause, ulong step)
    {
        ItemLocation from;
        lock (Gate)
        {
            if (!ByRef.ContainsKey(f.Item.Entity)) return;  // no longer followed
            from = f.Last;
            f.Last = to;
            f.Candidate = null;
        }
        if (Moved is { } moved)
            foreach (var d in moved.GetInvocationList())
            {
                try
                {
                    ((Action<ItemInstanceRef, ItemLocation, ItemLocation, ItemMoveCause>)d)(f.Item, from, to, cause);
                }
                catch (Exception e)
                {
                    Backend.Current.Log(0, LogLevel.Error, $"kiln.items: an item-moved handler threw: {e}");
                }
            }
        if (Events.HasSubscribers<ItemMovedEvent>()) Events.Post(new ItemMovedEvent(f.Item, from, to, cause, step));
    }

    private static void ReportGone(Followed f, ulong step)
    {
        lock (Gate)
        {
            if (!ByRef.Remove(f.Item.Entity)) return;  // reported once
        }
        if (Gone is { } gone)
            foreach (var d in gone.GetInvocationList())
            {
                try
                {
                    ((Action<ItemInstanceRef, ItemLocation, ulong>)d)(f.Item, f.Last, step);
                }
                catch (Exception e)
                {
                    Backend.Current.Log(0, LogLevel.Error, $"kiln.items: an item-gone handler threw: {e}");
                }
            }
        SyncReporter();
    }

    // ---- the transfer report listener: registered while something is followed ----
    private static readonly object ReporterGate = new();

    private static void SyncReporter()
    {
        lock (ReporterGate) SyncReporterLocked();
    }

    private static void SyncReporterLocked()
    {
        var want = Count > 0;
        if (want == (_reporter != 0) || (want && _reportFailed)) return;
        var t = Items.V4;
        if (t == null) return;
        if (want)
        {
            ulong handle = 0;
            var rc = t->V3.V3.TransferReportAdd(0UL, &OnReport, null, &handle);
            if (rc == ItemsNative.Ok) _reporter = handle;
            else
            {
                _reportFailed = true;
                Backend.Current.Log(0, LogLevel.Warn,
                    "kiln.items: the item tracker gets no transfer reports (quick stack and move all are seen by its per-step read only)");
            }
        }
        else
        {
            t->V3.V3.TransferEnd(_reporter);
            _reporter = 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnReport(ItemsNative.TransferReport* r, void* user)
    {
        try
        {
            if ((r->Flags & ItemsNative.TransferCanceled) != 0 && r->Moved == 0) return;
            OnTransfer(new EntityId(r->From), new EntityId(r->To));
        }
        catch (Exception e)
        {
            Backend.Current.Log(0, LogLevel.Error, $"kiln.items: the item tracker's transfer report failed: {e.Message}");
        }
    }
}
