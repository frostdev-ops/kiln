// SPDX-License-Identifier: MIT
// Kiln.Items in the capability registry (Kiln.Capabilities; native/core/abi/services/kiln_caps.h). The service reports
// its rows when it starts (the first use of Items): what its table has and what the native side resolved is fixed from
// then on. The one row that follows the session, items.moves, is refreshed on the service tick, never in a getter.
namespace Kiln;

public static unsafe partial class Items
{
    /// <summary>The capability names Kiln.Items answers for: the sigdb feature where there is one, kiln.items for the
    /// table, items.&lt;thing&gt; for the rest.</summary>
    internal static class Caps
    {
        /// <summary>The native table is here (the core's row; Services.Get lowers it when the table is older).</summary>
        public const string Service = "kiln.items";

        /// <summary>Kiln.Items' own row: the table has peek_result (an older runtime's has not).</summary>
        public const string ResultPeek = "items.result_peek";

        /// <summary>sigdb: <see cref="Gear.Unlocked"/> and <see cref="Gear.GemSlot"/> at creation.</summary>
        public const string GearOptions = "items.pide_options";

        /// <summary>sigdb: water in containers (<see cref="Items.Fuel"/>).</summary>
        public const string Fuel = "items.fuel";

        /// <summary>sigdb: moves that keep item state.</summary>
        public const string Move = "items.move";

        /// <summary>sigdb: container names.</summary>
        public const string Names = "items.names";

        /// <summary>sigdb: renaming a container through the game's rename (kiln.items v9, <see cref="Items.SetContainerName"/>);
        /// Kiln.Items lowers it when the table is older or kiln.items turned renames off.</summary>
        public const string Rename = "items.rename";

        /// <summary>sigdb: the quick-stack and move-all hooks (<see cref="Items.Transfers"/>).</summary>
        public const string Transfer = "items.transfer";

        /// <summary>Kiln.Items' session row: moves run right now (the kiln_items program runs in a server world).</summary>
        public const string Moves = "items.moves";

        /// <summary>sigdb: item instances (kiln.items v4: stamps, locations, save-record fields, following), what
        /// <see cref="ItemInstanceRef"/>, <see cref="Items.Held"/>, <see cref="Items.Equipped"/> and Kiln.Saves'
        /// InstanceData need.</summary>
        public const string Instances = "items.instances";

        /// <summary>sigdb: the game's equip and unequip (kiln.items v5, <see cref="Items.Equip"/>, <see cref="Items.Unequip"/>);
        /// Kiln.Items lowers it when the table is older, kiln.world is missing, or kiln.items turned equip off.</summary>
        public const string Equip = "items.equip";
        /// <summary>sigdb: create and equip one stack in one transaction (kiln.items, <see cref="Items.GiveEquip"/>);
        /// Kiln.Items reports it again every 25 steps: off on an older runtime, without a live server session, or while
        /// <see cref="Transaction"/> is off.</summary>
        public const string GiveEquip = "items.give_equip";

        /// <summary>sigdb: equipment sets (kiln.items v6, <see cref="Items.CaptureSet"/>): the equip functions and the
        /// commit's own rollback guards; Kiln.Items lowers it when the table is older, equip or item instances are off,
        /// kiln.decisions or kiln.window_clock are missing, this build's component layouts differ, or kiln.items switched
        /// sets off after an invariant failure.</summary>
        public const string EquipSets = "items.equip_sets";

        /// <summary>sigdb: what one of the game's wear debits costs a tool (<see cref="Items.SetToolWearPerDebit"/>); kiln.items lowers it when
        /// its guards do not pass, Kiln.Items when the kiln.toolwear table is not here.</summary>
        public const string ToolWear = "items.tool_wear_debit";

        /// <summary>sigdb: gear writes (kiln.items v8): the tracked other-entity write, Level's attribute formulas and the
        /// registry's replication and save bits. kiln-items.dll lowers it when items.fuel or items.instances is off or this
        /// build's layouts differ, and after a fault.</summary>
        public const string Gear = "items.gear";

        /// <summary>Kiln.Items' row: <see cref="Items.GearOf"/>, <see cref="Items.RarityOf"/> and the gear events read
        /// (kiln.items v8 and <see cref="Gear"/>).</summary>
        public const string GearRead = "items.gear.read";

        /// <summary>Kiln.Items' row: <see cref="Items.SetRarity"/>, <see cref="Items.SetEnhancements"/> and
        /// <see cref="Items.SetLevel"/> write (the read row, and kiln.items' program kiln_items_gear can run); followed every
        /// 25 steps.</summary>
        public const string GearWrite = "items.gear.write";

        /// <summary>Kiln.Items' row: a gear write can have the game compute max durability again
        /// (<see cref="DurabilityRule.KeepRatio"/>, <see cref="DurabilityRule.Repair"/>): the write row, with the
        /// <c>ItemStateFlag</c> reflection kiln_items_gear checks before its first write.</summary>
        public const string GearDurability = "items.gear.durability";

        /// <summary>Kiln.Items' row: &lt;see cref="Items.UpgradeRarity(EntityId, ItemInstanceRef, RarityUpgradeRule, UpgradeOptions?)"/&gt;
        /// (the write row, kiln.items' gear_upgrade of package P3 and the inventory transaction it pays on); followed with the
        /// gear rows.</summary>
        public const string GearUpgrade = "items.gear.upgrade";

        /// <summary>sigdb: per-instance power (kiln.items v8, package P7): the detour of the game's item power. kiln-items.dll
        /// lowers it without item instances, when this build's layouts differ, or when the detour cannot be installed or
        /// faults. <see cref="Items.SetPower"/>.</summary>
        public const string GearPower = "items.gear.power";

        /// <summary>sigdb: per-instance armour (package P7): the two mid hooks around one worn piece's armour.
        /// <see cref="Items.SetArmor"/>.</summary>
        public const string GearArmor = "items.gear.armor";

        /// <summary>Kiln.Items' row: the kiln.items.inventory_kinds table is here (<see cref="Items.FindInventoryOwners"/>).</summary>
        public const string InventoryKinds = InventoryKindsCapability;

        // sigdb rows the ones above need
        public const string Transaction = "items.transaction";
        public const string Read = "items.read";
        public const string Program = "items.program";
    }

    // Every Available property reads the registry through here, so Items' static constructor (the service's start,
    // which reports the rows) has run before the first read, also from a nested type (Fuel, Transfers).
    private static bool Can(string capability) => Capabilities.Is(capability);

    // dev.27: the rows run on a ServiceCapRows, so they are reported again under a new registry binding (the test kit's,
    // a contract test's) and a failure is logged once, never thrown out of the static constructor.
    internal static readonly ServiceCapRows Rows = new(Caps.Service, ReportCapabilities);

    static Items() => Rows.Ensure();

    private static IDisposable? _capabilityTick;
    private static (CapabilityState State, CapabilityReason Reason, string Text) _moves = ((CapabilityState)(-1), 0, "");
    private static long _movesEpoch = -1;

    // The rows that are fixed once the service has started. A sigdb row is only lowered, never raised.
    private static void ReportCapabilities()
    {
        PlayerStorageRuntime.ReportCapabilities();  // kiln.items.storage is a table of its own: its rows do not follow kiln.items'
        ReportInventoryKinds();                     // so is kiln.items.inventory_kinds (0.2.0-dev.31)
        ItemCosts.Runtime.ReportCapabilities();     // and kiln.item_costs (0.2.0-dev.32): items.costs.exact, items.costs.wear
        RefreshGiveEquip();
        var t = T;  // Services.Get puts the reason on kiln.items when the table is missing or older
        if (t == null)
        {
            var (reason, text) = Cause(CapabilityReason.ServiceMissing, "kiln.items is not loaded", Caps.Service);
            Capabilities.Report(Caps.ResultPeek, CapabilityState.Unavailable, reason, text);
            foreach (var name in (string[])[Caps.Fuel, Caps.Move, Caps.Names, Caps.Transfer, Caps.Moves, Caps.Instances, Caps.Equip, Caps.EquipSets, Caps.GearOptions, Caps.ToolWear,
                         Caps.GearPower, Caps.GearArmor, Caps.Rename]) Lower(name, reason, text);
            foreach (var name in (string[])[Caps.GearRead, Caps.GearWrite, Caps.GearDurability, Caps.GearUpgrade]) Capabilities.Report(name, CapabilityState.Unavailable, reason, text);
            return;
        }
        // equip (v5): kiln.items decides it at its start (the symbols, kiln.world, the program's registration)
        var v5 = V5;
        var equip = v5 == null ? null : EquipStatusOf(v5);
        switch (DecideEquip(t->Header.Version, v5 != null, equip?.Available ?? false, equip?.Detail ?? ""))
        {
            case (EquipVerdict.NoV5, var text):
                Lower(Caps.Equip, CapabilityReason.ServiceOlder, text);
                break;
            case (EquipVerdict.Off, var text):
                Off(Caps.Equip, text, Caps.Transaction, Caps.Read);
                break;
        }
        // equipment sets (v6): kiln.items decides what it can at its start (the feature, its layouts, kiln.decisions);
        // the session follows items.equip's row (RefreshEquipSets)
        var v6 = V6;
        var sets = v6 == null ? null : SetStatusOf(v6);
        switch (DecideEquipSets(t->Header.Version, v6 != null, sets?.Available ?? false, sets?.Detail ?? ""))
        {
            case (EquipSetsVerdict.NoV6, var text):
                Lower(Caps.EquipSets, CapabilityReason.ServiceOlder, text);
                break;
            case (EquipSetsVerdict.Off, var text):
                _setsOff = true;
                Off(Caps.EquipSets, text, Caps.Equip, Caps.Instances, Caps.Transaction, Caps.Read);
                break;
        }
        var older = $"kiln.items v{t->Header.Version} has no {{0}} (update Kiln)";
        if (ResultTable != null) Capabilities.Report(Caps.ResultPeek, CapabilityState.Available);
        else Capabilities.Report(Caps.ResultPeek, CapabilityState.Unavailable, CapabilityReason.ServiceOlder, string.Format(older, "result peek"));
        if (V2 == null) Lower(Caps.Fuel, CapabilityReason.ServiceOlder, string.Format(older, "water functions"));
        // item instances: kiln.items decides its flag once, when it resolves at its start (items.cpp, instances_ok)
        var v4 = V4 != null;
        switch (DecideInstances(t->Header.Version, v4, v4 && ItemInstances.On, v4 ? ItemInstances.WhyOff : ""))
        {
            case (InstancesVerdict.NoV4, var text):
                Lower(Caps.Instances, CapabilityReason.ServiceOlder, text);
                break;
            case (InstancesVerdict.Off, var text):
                // kiln.items turns item instances off when its inventory reads are off: then that row's reason is the cause
                Off(Caps.Instances, text, Caps.Read);
                break;
        }
        var s3 = Status3();
        if (s3 == null)
        {
            foreach (var name in (string[])[Caps.Move, Caps.Names, Caps.Transfer, Caps.Moves])
                Lower(name, CapabilityReason.ServiceOlder, string.Format(older, "move, name or transfer functions"));
        }
        else
        {
            // kiln.items decided these when it started (items.cpp resolve()): names and the transfer hooks stay as they are
            if (!s3.Names) Off(Caps.Names, s3.Detail, Caps.Read);
            if (!s3.TransferHooks) Off(Caps.Transfer, s3.Detail, Caps.Transaction);
            RefreshMoves();
            _capabilityTick ??= ServiceTickAccess.Add("kiln.items capabilities", step =>
            {
                if (step % 25 != 0) return;
                RefreshMoves();
                RefreshEquip();
                RefreshGiveEquip();
                RefreshEquipSets();
                SetHeartbeat(step);
            });
        }
        // gear (v8): the rows are Kiln.Items' own, followed every 25 steps on a tick of their own (the kit's table has no v3)
        RefreshGear();
        if (V8 != null) _gearTick ??= ServiceTickAccess.Add("kiln.items gear capabilities", step => { if (step % 25 == 0) RefreshGear(); });
        // per-instance values (v8, package P7): sigdb rows of their own, lowered here only by a table without the scales
        if (t->Header.Size < ItemsNative.V8ScaleEnd)
            foreach (var name in (string[])[Caps.GearPower, Caps.GearArmor]) Lower(name, CapabilityReason.ServiceOlder, string.Format(older, "per-instance values"));
        // container renames (v9): the sigdb row stands unless the table is older or kiln.items turned renames off when it
        // resolved (the session part, a server world in this process, is ContainerRenameAvailable's)
        var v9 = V9;
        if (v9 == null) Lower(Caps.Rename, CapabilityReason.ServiceOlder, string.Format(older, "container renames"));
        else if (RenameStatusOf(v9) is { Feature: false } renames) Off(Caps.Rename, renames.Detail, Caps.Names, Caps.Read);
        // tool wear (dev.28): its own table, published by the same DLL
        if (ToolWear.Current == null) Lower(Caps.ToolWear, CapabilityReason.ServiceOlder, "kiln.items here has no kiln.toolwear table (update Kiln)");
        // give, take, moves and water all run through the game's inventory transaction
        if (Capabilities.Get(Caps.Transaction) is { State: CapabilityState.Unavailable, Reason: not CapabilityReason.Unknown } tx)
            foreach (var name in (string[])[Caps.Fuel, Caps.Move])
                Lower(name, tx.Reason, $"needs {Caps.Transaction}: {tx.ReasonText}");
    }

    internal enum InstancesVerdict
    {
        /// <summary>On: the sigdb row stands.</summary>
        On,
        /// <summary>No kiln.items table: lowered with the other rows (the reason of kiln.items' row).</summary>
        NoTable,
        /// <summary>A table without v4: lowered, service-older.</summary>
        NoV4,
        /// <summary>v4, but kiln.items turned the feature off (its status flag is clear): off, with its detail.</summary>
        Off,
    }

    /// <summary>What <c>items.instances</c> becomes, from what kiln.items has: the table's version (null: no table),
    /// whether it is v4, its InstancesOn flag and its status detail. Pure, so the three branches are tested without the
    /// native table.</summary>
    internal static (InstancesVerdict Verdict, string Text) DecideInstances(uint? tableVersion, bool v4, bool flagOn, string detail) =>
        tableVersion is not { } version ? (InstancesVerdict.NoTable, "kiln.items is not loaded")
        : !v4 ? (InstancesVerdict.NoV4, $"kiln.items v{version} has no item instances (update Kiln)")
        : !flagOn ? (InstancesVerdict.Off, detail.Length > 0 ? detail : "kiln.items turned item instances off (see kiln.log)")
        : (InstancesVerdict.On, "");

    internal enum EquipVerdict
    {
        /// <summary>On: the sigdb row stands.</summary>
        On,
        /// <summary>No kiln.items table: lowered with the other rows.</summary>
        NoTable,
        /// <summary>A table without v5: lowered, service-older.</summary>
        NoV5,
        /// <summary>v5, but kiln.items says equip cannot run (feature off, no kiln.world, the program refused): off, with
        /// its detail.</summary>
        Off,
    }

    /// <summary>What <c>items.equip</c> becomes, from what kiln.items has: the table's version (null: no table), whether
    /// it has v5, its equip status' available flag and detail. Pure, so every branch is tested without the native
    /// table.</summary>
    internal static (EquipVerdict Verdict, string Text) DecideEquip(uint? tableVersion, bool v5, bool available, string detail) =>
        tableVersion is not { } version ? (EquipVerdict.NoTable, "kiln.items is not loaded")
        : !v5 ? (EquipVerdict.NoV5, $"kiln.items v{version} has no equip (update Kiln)")
        : !available && !detail.StartsWith(NoServerWorld, StringComparison.Ordinal)
            ? (EquipVerdict.Off, detail.Length > 0 ? detail : "kiln.items turned equip off (see kiln.log)")
        : (EquipVerdict.On, "");

    internal enum EquipSetsVerdict
    {
        /// <summary>On: the sigdb row stands.</summary>
        On,
        /// <summary>No kiln.items table: lowered with the other rows.</summary>
        NoTable,
        /// <summary>A table without v6: lowered, service-older.</summary>
        NoV6,
        /// <summary>v6, but kiln.items says sets cannot run (the feature, equip or item instances off, kiln.decisions or
        /// kiln.window_clock missing, the layouts differ, switched off after a fault): off, with its detail.</summary>
        Off,
    }

    /// <summary>What <c>items.equip_sets</c> becomes, from what kiln.items has: the table's version (null: no table),
    /// whether it has v6, its set status' available flag and detail. Pure, so every branch is tested without the native
    /// table.</summary>
    internal static (EquipSetsVerdict Verdict, string Text) DecideEquipSets(uint? tableVersion, bool v6, bool available, string detail) =>
        tableVersion is not { } version ? (EquipSetsVerdict.NoTable, "kiln.items is not loaded")
        : !v6 ? (EquipSetsVerdict.NoV6, $"kiln.items v{version} has no equipment sets (update Kiln)")
        : !available ? (EquipSetsVerdict.Off, detail.Length > 0 ? detail : "kiln.items turned equipment sets off (see kiln.log)")
        : (EquipSetsVerdict.On, "");

    internal enum GearVerdict
    {
        /// <summary>On.</summary>
        On,
        /// <summary>No kiln.items table.</summary>
        NoTable,
        /// <summary>A table without v8: service-older.</summary>
        NoV8,
        /// <summary>The sigdb row items.gear is off: its reason.</summary>
        NoFeature,
        /// <summary>v8 and the feature, but kiln.items says writes cannot run (no kiln.world, the program refused, switched
        /// off after a fault): faulted, with its detail. Reads still work.</summary>
        WritesOff,
    }

    /// <summary>What the gear rows become, from what kiln.items has: the table's version (null: no table), whether it has
    /// v8, whether the sigdb row <c>items.gear</c> is available, and its gear status' available flag and detail. The read
    /// row is on for <see cref="GearVerdict.On"/> and <see cref="GearVerdict.WritesOff"/>; the write and durability rows for
    /// <see cref="GearVerdict.On"/> only. Pure, so every branch is tested without the native table.</summary>
    internal static (GearVerdict Verdict, string Text) DecideGear(uint? tableVersion, bool v8, bool feature, bool writes, string detail) =>
        tableVersion is not { } version ? (GearVerdict.NoTable, "kiln.items is not loaded")
        : !v8 ? (GearVerdict.NoV8, $"kiln.items v{version} has no gear functions (update Kiln)")
        : !feature ? (GearVerdict.NoFeature, "")
        : !writes ? (GearVerdict.WritesOff, detail.Length > 0 ? detail : "kiln.items turned gear writes off (see kiln.log)")
        : (GearVerdict.On, "");

    private static (GearVerdict, string) _gearReported = ((GearVerdict)(-1), "");
    private static IDisposable? _gearTick;
    private static long _gearEpoch = -1;

    // The three gear rows from the table, the sigdb row and kiln.items' gear status: at the start and every 25 steps (a
    // fault switches writes off for good; the program waits for a server world without going off).
    internal static void RefreshGear()
    {
        var t = T;
        var v8 = V8;
        var status = v8 == null ? null : GearStatusOf(v8);
        var gearRow = Capabilities.Get(Caps.Gear);
        var now = DecideGear(t == null ? null : t->Header.Version, v8 != null, gearRow.State == CapabilityState.Available,
            status?.Available ?? false, status?.Detail ?? "");
        if (now == _gearReported && _gearEpoch == Capabilities.CurrentBinding.Epoch) return;
        _gearReported = now;
        _gearEpoch = Capabilities.CurrentBinding.Epoch;
        var (verdict, text) = now;
        (CapabilityState, CapabilityReason, string) read, write;
        switch (verdict)
        {
            case GearVerdict.On:
                read = write = (CapabilityState.Available, CapabilityReason.None, "");
                break;
            case GearVerdict.WritesOff:
                read = (CapabilityState.Available, CapabilityReason.None, "");
                write = (CapabilityState.Unavailable, CapabilityReason.Faulted, text);
                break;
            case GearVerdict.NoFeature:
                read = write = (CapabilityState.Unavailable, gearRow.Reason is CapabilityReason.None or CapabilityReason.Unknown ? CapabilityReason.SymbolMissing : gearRow.Reason,
                    $"needs {Caps.Gear}: {gearRow.ReasonText}");
                break;
            case GearVerdict.NoV8:
                read = write = (CapabilityState.Unavailable, CapabilityReason.ServiceOlder, text);
                break;
            default:
                var (reason, why) = Cause(CapabilityReason.ServiceMissing, text, Caps.Service);
                read = write = (CapabilityState.Unavailable, reason, why);
                break;
        }
        Capabilities.Report(Caps.GearRead, read.Item1, read.Item2, read.Item3);
        Capabilities.Report(Caps.GearWrite, write.Item1, write.Item2, write.Item3);
        Capabilities.Report(Caps.GearDurability, write.Item1, write.Item2,
            write.Item1 == CapabilityState.Available ? "" : $"needs {Caps.GearWrite}: {write.Item3}");
        var (upState, upReason, upText) = DecideUpgrade(write, v8 != null && t->Header.Size >= ItemsNative.V8UpgradeEnd && v8->GearUpgrade != null,
            Capabilities.Get(Caps.Transaction));
        Capabilities.Report(Caps.GearUpgrade, upState, upReason, upText);
    }

    /// <summary>The upgrade row from the write row, whether the table has gear_upgrade, and the transaction row it pays on
    /// (pure).</summary>
    internal static (CapabilityState, CapabilityReason, string) DecideUpgrade((CapabilityState State, CapabilityReason Reason, string Text) write,
        bool hasUpgrade, Capability transaction)
    {
        if (write.State != CapabilityState.Available) return (write.State, write.Reason, $"needs {Caps.GearWrite}: {write.Text}");
        if (!hasUpgrade) return (CapabilityState.Unavailable, CapabilityReason.ServiceOlder, "kiln.items here has no upgrades (gear_upgrade, 0.2.0-dev.29.19; update Kiln)");
        if (transaction.State == CapabilityState.Unavailable)
            return (CapabilityState.Unavailable, transaction.Reason is CapabilityReason.None or CapabilityReason.Unknown ? CapabilityReason.SymbolMissing : transaction.Reason,
                $"needs {Caps.Transaction}: {transaction.ReasonText}");
        return (CapabilityState.Available, CapabilityReason.None, "");
    }

    // items.equip_sets every 25 steps: off for good once kiln.items switched sets off (an invariant failure: until the
    // server restarts); for the session like items.equip (a client that joined another machine's world has no server
    // world: a set would never run), and on again when items.equip is. Never raised above the core's sigdb verdict.
    private static bool _setsOff;
    private static (CapabilityState, CapabilityReason, string) _setsSession = (CapabilityState.Available, CapabilityReason.None, "");

    internal static void RefreshEquipSets()
    {
        var t = V6;
        if (_setsOff || t == null || SetStatusOf(t) is not { } s) return;
        if (!s.Available)  // kiln.items' set status leaves the session out: anything it says is off stays off
        {
            _setsOff = true;
            Off(Caps.EquipSets, s.Detail, Caps.Equip, Caps.Instances, Caps.Transaction, Caps.Read);
            return;
        }
        (CapabilityState, CapabilityReason, string) now = Capabilities.Get(Caps.Equip) is { State: CapabilityState.Unavailable, Reason: CapabilityReason.Session } equip
            ? (CapabilityState.Unavailable, CapabilityReason.Session, $"needs {Caps.Equip}: {equip.ReasonText}")
            : (CapabilityState.Available, CapabilityReason.None, s.Detail);  // which components a rollback is compared over, once a set ran
        if (now == _setsSession) return;
        _setsSession = now;
        Capabilities.Report(Caps.EquipSets, now.Item1, now.Item2, now.Item3);
    }

    // items.moves follows the kiln_items program: available while moves run, pending while it waits for a server world
    // (and again after one closed), unavailable when it broke or moves are off on this build.
    private static void RefreshMoves()
    {
        var s3 = Status3();
        var s = Status();
        (CapabilityState, CapabilityReason, string) now =
            s3 is { Moves: true } ? (CapabilityState.Available, CapabilityReason.None, "")
            : !Capabilities.Is(Caps.Move) || s is null or { Program: 0 or 4 }
                ? Unavailable(Cause(CapabilityReason.Faulted, s?.Detail ?? "", Caps.Move, Caps.Transaction, Caps.Program))
                : (CapabilityState.Pending, CapabilityReason.Session, s.Detail.Length > 0 ? s.Detail : "waiting for the server world");
        if (now == _moves && _movesEpoch == Capabilities.CurrentBinding.Epoch) return;
        _moves = now;
        _movesEpoch = Capabilities.CurrentBinding.Epoch;
        Capabilities.Report(Caps.Moves, now.Item1, now.Item2, now.Item3);
    }

    // items.equip follows what kiln.items says every 25 steps: off for good when its program was refused or broke; off for
    // the session on a game client that joined another machine's world (no server world here: a request would never
    // run), and on again when this client hosts. The sigdb row is never raised above the core's verdict: an "available"
    // report only takes back Kiln.Items' own session report.
    private static bool _equipBroken;
    internal const string NoServerWorld = "no server world in this process";
    private static (CapabilityState, CapabilityReason, string) _equipSession = (CapabilityState.Available, CapabilityReason.None, "");

    private static void RefreshEquip()
    {
        var t = V5;
        if (_equipBroken || t == null || EquipStatusOf(t) is not { } s) return;
        var remote = !s.Available && s.Detail.StartsWith(NoServerWorld, StringComparison.Ordinal);
        if (!s.Available && !remote)
        {
            _equipBroken = true;
            Off(Caps.Equip, s.Detail, Caps.Transaction, Caps.Read);
            return;
        }
        (CapabilityState, CapabilityReason, string) now = remote
            ? (CapabilityState.Unavailable, CapabilityReason.Session, s.Detail)
            : (CapabilityState.Available, CapabilityReason.None, "");
        if (now == _equipSession) return;
        _equipSession = now;
        Capabilities.Report(Caps.Equip, now.Item1, now.Item2, now.Item3);
    }

    private static (CapabilityState, CapabilityReason, string) Unavailable((CapabilityReason Reason, string Text) c) =>
        (CapabilityState.Unavailable, c.Reason, c.Text);

    // Why something is off: the first row it needs that is off ("needs <row>: <its text>", its reason), else `fallback`.
    private static (CapabilityReason Reason, string Text) Cause(CapabilityReason fallback, string detail, params string[] needs)
    {
        foreach (var n in needs)
            if (Capabilities.Get(n) is { State: CapabilityState.Unavailable, Reason: not CapabilityReason.Unknown } row)
                return (row.Reason, n == Caps.Service ? $"kiln.items is unavailable: {row.ReasonText}" : $"needs {n}: {row.ReasonText}");
        return (fallback, detail.Length > 0 ? detail : "kiln.items turned it off (see kiln.log)");
    }

    // items.inventory_kinds: the table of its own and kiln.items (its find runs find_inventories); fixed once reported.
    private static void ReportInventoryKinds()
    {
        if (InventoryKindsNative.T != null && T != null)
        {
            Capabilities.Report(Caps.InventoryKinds, CapabilityState.Available);
            return;
        }
        var (reason, text) = T == null
            ? Cause(CapabilityReason.ServiceMissing, "kiln.items is not loaded", Caps.Service)
            : (CapabilityReason.ServiceMissing, "kiln.items.inventory_kinds is not loaded (an older kiln-items.dll)");
        Capabilities.Report(Caps.InventoryKinds, CapabilityState.Unavailable, reason, text);
    }

    // `name` is off although its sigdb row may say it resolved: kiln.items' own checks said no.
    private static void Off(string name, string detail, params string[] needs)
    {
        var (reason, text) = Cause(CapabilityReason.Faulted, detail, needs);
        Lower(name, reason, text);
    }

    // Reports `name` unavailable unless its row already says why it is off (the core's reason stands).
    private static void Lower(string name, CapabilityReason reason, string text)
    {
        if (Capabilities.Get(name) is { State: CapabilityState.Unavailable, Reason: not CapabilityReason.Unknown }) return;
        Capabilities.Report(name, CapabilityState.Unavailable, reason, text);
    }
}
