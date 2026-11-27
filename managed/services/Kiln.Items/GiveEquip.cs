// SPDX-License-Identifier: MIT

namespace Kiln;

/// <summary>Where GiveEquip puts the equipment slot's previous stack. Since 0.2.0-dev.30.6.</summary>
public enum Replaced
{
    /// <summary>Preserve it in a free backpack slot. NoRoom if none is free; nothing changes.</summary>
    Backpack = 0,
    /// <summary>Destroy it: the old stack, and for gear its item entity, is removed from the game, not moved or dropped
    /// anywhere. The result reports its item, count and instance. Works with a full backpack.</summary>
    Take = 1,
}

/// <summary>Options of Items.GiveEquip. Every request is all-or-nothing. Since 0.2.0-dev.30.6.</summary>
public sealed record GiveEquipOptions
{
    /// <summary>Where the old stack goes; defaults to Backpack.</summary>
    public Replaced Replaced { get; init; } = Replaced.Backpack;
    /// <summary>Run the transaction and roll it back, creating no item entity and changing nothing.</summary>
    public bool DryRun { get; init; }
}

public static unsafe partial class Items
{
    private static ItemsNative.TableWithGiveEquip* GiveEquipTable => T != null && T->Header.Size >= sizeof(ItemsNative.TableWithGiveEquip) &&
        ((ItemsNative.TableWithGiveEquip*)T)->GiveEquip != null ? (ItemsNative.TableWithGiveEquip*)T : null;

    /// <summary>Whether give-and-equip can run in this server session. Since 0.2.0-dev.30.6.</summary>
    public static bool GiveEquipAvailable
    {
        get
        {
            RefreshGiveEquip();
            return Can(Caps.GiveEquip);
        }
    }

    /// <summary>Creates one stack straight into an empty or occupied equipment slot, atomically. No free backpack slot
    /// is needed for an empty gear/vanity target or Replaced.Take of existing gear. Arrow, Spell and FishingBait instead
    /// require a verified Generic backpack cell: reuse the selected cell, or choose an empty unselected cell when the
    /// selection is zero. A non-Generic ammo selection is refused, never repaired or relocated. Existing occupied
    /// ammo contents must still resolve to Ammunition of that slot's type, or SlotChanged is returned before
    /// mutation, even for Take/DryRun. Different valid ammo of the matching type remains replaceable. New ammo must
    /// also be in the Ammunition category. Since 0.2.0-dev.31.12 that includes instance-backed ammo (eternal spells and
    /// arrows, count 1): created once with its item entity in that Generic cell and selected there, as the game equips
    /// one from the backpack; a replaced eternal keeps its entity (Backpack) or is destroyed (Take). Without the sigdb
    /// feature items.give_equip.instance_ammo an eternal is refused before any change. EquipmentSlotType must fit.
    /// Count must fit one stack (one for gear and eternal ammo). Backpack items cannot be given into the bag slot.
    /// Since 0.2.0-dev.30.6.</summary>
    /// <returns>One transaction's outcome. A refusal or DryRun changes nothing. A committed change posts
    /// EquipChangedEvent; ReplacedItem and ReplacedCount report the old stack. From is None: no intermediate backpack
    /// placement. New gear has the game's default level and rarity.</returns>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(Caps.GiveEquip)]
    public static Task<EquipResult> GiveEquip(EntityId player, ItemId item, int count, EquipmentSlot slot,
        GiveEquipOptions? options = null)
    {
        Services.RequireTickThread("Items.GiveEquip");
        if (player.IsNone) throw new ArgumentException("no player", nameof(player));
        if (item.IsNone) throw new ArgumentException("no item", nameof(item));
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 65535);
        var gameSlot = GameSlot(slot);
        options ??= new();
        if (!Enum.IsDefined(options.Replaced)) throw new ArgumentException("unknown replaced-item policy", nameof(options));
        RefreshGiveEquip();
        var t = GiveEquipTable;
        if (t == null || !Can(Caps.GiveEquip))
            throw new KilnUnavailableException(Caps.GiveEquip, Capabilities.Why(Caps.GiveEquip) ?? "this runtime has no give-and-equip");
        if (t->GiveEquip(0, null, null, null, null) != ItemsNative.Ok)
            throw new KilnUnavailableException(Caps.GiveEquip, "give-and-equip needs a live server session, equipment, moves and creation support");
        var mod = ModContext.CurrentMod;
        if (mod is { Alive: false }) throw new InvalidOperationException("the mod is unloading");
        var completion = new TaskCompletionSource<EquipResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var key = Track(completion, mod);
        var request = new ItemsNative.GiveEquipRequest
        {
            Size = (uint)sizeof(ItemsNative.GiveEquipRequest), Player = player.Value, Item = item.Value, Count = (uint)count,
            EquipmentSlot = gameSlot, Flags = (options.DryRun ? ItemsNative.EquipDryRun : 0) | (options.Replaced == Replaced.Take ? 4u : 0),
        };
        ulong ticket = 0;
        var rc = t->GiveEquip(0, &request, &OnEquipDone, (void*)key, &ticket);
        if (rc == ItemsNative.Ok) return completion.Task;
        lock (EquipGate) EquipsInFlight.Remove(key);
        if (SubmitRefusal(rc, player, slot, false, default, options.DryRun) is { } refused) return Task.FromResult(refused);
        if (rc == ItemsNative.InvalidArgument) throw new ArgumentException("Items.GiveEquip: invalid native request");
        throw new KilnUnavailableException(Caps.GiveEquip, "give-and-equip became unavailable");
    }

    private static void RefreshGiveEquip()
    {
        var table = GiveEquipTable;
        var available = table != null && table->GiveEquip(0, null, null, null, null) == ItemsNative.Ok;
        if (available)
        {
            Capabilities.Report(Caps.GiveEquip, CapabilityState.Available);
            return;
        }
        if (table != null && !Can(Caps.Transaction))
        {
            var transaction = Capabilities.Get(Caps.Transaction);
            Capabilities.Report(Caps.GiveEquip, CapabilityState.Unavailable,
                transaction.Reason == CapabilityReason.Unknown ? CapabilityReason.Faulted : transaction.Reason,
                $"needs {Caps.Transaction}: {(transaction.ReasonText.Length > 0 ? transaction.ReasonText : "inventory transactions are unavailable")}");
            return;
        }
        var fallback = table == null
            ? T == null ? CapabilityReason.ServiceMissing : CapabilityReason.ServiceOlder
            : CapabilityReason.Session;
        var detail = table == null ? "this runtime has no give-and-equip (update Kiln)" :
            "give-and-equip needs a live server session and resolved equipment, move, transaction guards and creation support";
        var equipment = V5;
        var equipmentStatus = equipment != null ? EquipStatusOf(equipment) : null;
        if (equipmentStatus is { Available: false, Detail.Length: > 0 } status) detail = status.Detail;
        var (reason, text) = equipmentStatus != null
            ? Cause(fallback, detail, Caps.Service, Caps.Transaction, Caps.Equip, Caps.Move, Caps.GearOptions)
            : Cause(fallback, detail, Caps.Service, Caps.Transaction);
        Capabilities.Report(Caps.GiveEquip, CapabilityState.Unavailable, reason, text);
    }
}
