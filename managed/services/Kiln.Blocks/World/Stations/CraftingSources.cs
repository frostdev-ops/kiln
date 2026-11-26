// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>Where linked storage pays, after the bag and the crafting base's stock. Since 0.2.0-dev.30.6.</summary>
public enum CraftSourceOrder
{
    /// <summary>Before opted-in station outputs (the default).</summary>
    BeforeStationOutputs = 0,
    /// <summary>After opted-in station outputs.</summary>
    AfterStationOutputs = 1,
}

/// <summary>How <see cref="CraftSourceOptions.Items"/> is interpreted. Since 0.2.0-dev.30.6.</summary>
public enum CraftSourceFilter
{
    /// <summary>Every item except the set; an empty set allows everything (the default).</summary>
    Deny = 0,
    /// <summary>Only items in the set; an empty set allows nothing.</summary>
    Allow = 1,
}

/// <summary>Options copied when a linked crafting source is registered. Since 0.2.0-dev.30.6.</summary>
public sealed class CraftSourceOptions
{
    /// <summary>After the bag/base stock, before or after opted-in station outputs.</summary>
    public CraftSourceOrder Order { get; init; }
    /// <summary>Allow-list or deny-list interpretation of <see cref="Items"/>; default deny.</summary>
    public CraftSourceFilter Filter { get; init; }
    /// <summary>The item set, copied eagerly on registration; null means an empty set. No managed predicate runs in
    /// the native payment hook, which may run on a game worker thread. Register again to change the filter.</summary>
    public IReadOnlyCollection<ItemId>? Items { get; init; }
    /// <summary>Locks too (default false): a lock that requires an item (the game's InteractionLock: a lockpick, a key)
    /// also counts this container, through <see cref="Filter"/> and <see cref="Items"/>, and a lock that uses the item
    /// up takes it from here after the player's bag, in link order, in the game's own all-or-nothing interaction
    /// transaction. A lock that only needs the item to be held takes nothing. Needs capability
    /// <see cref="Crafting.LockSourcesCapability"/> on the server or host: the server's own lock check decides every
    /// player's press, so the lock opens for a vanilla client too, but only a Kiln client's locked prompt and predicted
    /// press count the link (a vanilla client still shows the lock locked and may first play the failed attempt).
    /// Without the capability the link still serves crafting. Since 0.2.0-dev.31.12, mod API 0.2.21.</summary>
    public bool Locks { get; init; }
    /// <summary>Production-station fills too (default false): the station screen's "+1" and "+10" (the game's "fill from
    /// chests", which Kiln.Blocks then runs as Kiln's fill) and a <see cref="Stations.Fill"/> with
    /// <see cref="FillOptions.UseLinkedSources"/> take from this container, through <see cref="Filter"/> and
    /// <see cref="Items"/>, what the player's bag, the station base's Magic chests and named chests cannot cover: before or
    /// after opted-in station outputs by <see cref="Order"/>, in the fill's one all-or-nothing transaction, in any base.
    /// Off by default because a fill moves whole crafts' worth of inputs out of chests in other bases at once, which a
    /// link made for crafting did not ask for. Needs <see cref="Stations.FillCapability"/> on the server or host; a Kiln
    /// client's "+1" / "+10" light up from the server's answer (a vanilla client's stay greyed when the bag and the Magic
    /// chests cannot cover the fill). A row transfer of one ingredient stays the game's. Since 0.2.0-dev.31.12.</summary>
    public bool Fills { get; init; }
    /// <summary>Building too (default false): the build cursor's material check and the Construction Hammer's blueprint
    /// placements (blocks and terrain shapes) count and pay from this container, through <see cref="Filter"/> and
    /// <see cref="Items"/>: after the player's bag and the current base's Magic chests (in the game's own order), only what
    /// they cannot cover, in the placement's all-or-nothing payment, in any base. Props, undo, dismantling and terraforming
    /// stay the game's. Needs capability <see cref="Crafting.BuildSourcesCapability"/> on the server or host, and a Kiln
    /// client for the player's own cursor and build menu counts (a vanilla client shows the placement as missing materials
    /// and does not send it). Since 0.2.0-dev.31.12.</summary>
    public bool Building { get; init; }
    /// <summary>The skill respec too (default false): a skill node's unlearn and the Flame Altar's skill reset pay their
    /// respec item (the game's Runes) from this container, through <see cref="Filter"/> and <see cref="Items"/>: after the
    /// player's bag and the Magic chests of the base the player stands in (the game's own order), only what they cannot
    /// cover, in the respec's one all-or-nothing payment, in any base. The reset the game makes for free (a character whose
    /// skill tree no longer checks out at join) stays free. Needs capability <see cref="Crafting.RespecSourcesCapability"/>
    /// on the server or host: the server pays, so it works for every player, but only a Kiln client's skill tree and Flame
    /// Altar offer the unlearn and the reset when the bag and the Magic chests alone are short (a vanilla client greys
    /// them out and sends nothing). Without the capability the link still serves crafting. Since 0.2.0-dev.31.18.</summary>
    public bool Respec { get; init; }
}

/// <summary>A session-only player crafting source. Dispose or unload its registering mod to remove it. In-flight
/// payments may finish. After a restart the mod must register its sources again. Since 0.2.0-dev.30.6.</summary>
public sealed class CraftSourceHandle : IDisposable
{
    private long _handle;
    private readonly ulong _owner;
    private readonly ModContext? _mod;
    private readonly bool _locks;
    private readonly object? _lockSession;  // the lock session this handle counts in (GitHub #147)
    private readonly object? _fillSession, _buildSession;  // GitHub #132
    private readonly object? _respecSession;               // GitHub #150
    internal CraftSourceHandle(ulong owner, ulong handle, EntityId player, EntityId container, ModContext? mod, bool locks = false,
        bool fills = false, bool building = false, bool respec = false)
    {
        _owner = owner; _handle = unchecked((long)handle); Player = player; Container = container; _mod = mod; _locks = locks;
        if (mod is not null) mod.Retiring += Dispose;
        if (locks) _lockSession = LockStock.Track(player);
        if (fills)
        {
            _fillSession = LinkPurposes.Fills.Track(player);
            StationsCore.StartPump();  // the station screen's fill button is followed from the stations pump
        }
        if (building) _buildSession = BuildStock.Track(player);
        if (respec) _respecSession = RespecStock.Track(player);
    }
    /// <summary>The only player this registration can supply.</summary>
    public EntityId Player { get; }
    /// <summary>The registered storage container, independent of its base.</summary>
    public EntityId Container { get; }
    /// <summary>Whether this handle has been disposed.</summary>
    public bool IsDisposed => Interlocked.Read(ref _handle) == 0;
    /// <summary>Removes this registration, on any thread. Idempotent; no other registration is removed.</summary>
    [AnyThread]
    public void Dispose()
    {
        var id = Interlocked.Exchange(ref _handle, 0);
        if (id == 0) return;
        try { CraftingCore.Remove(_owner, unchecked((ulong)id)); }
        finally
        {
            if (_mod is not null) _mod.Retiring -= Dispose;
            if (_locks) LockStock.Untrack(Player, _lockSession);
            if (_fillSession is not null) LinkPurposes.Fills.Untrack(Player, _fillSession);
            if (_buildSession is not null) BuildStock.Untrack(Player, _buildSession);
            if (_respecSession is not null) RespecStock.Untrack(Player, _respecSession);
        }
    }
}

/// <summary>Player crafting sources for station, NPC and inventory crafting. Since 0.2.0-dev.30.6.</summary>
public static class Crafting
{
    /// <summary>Server payment through the recipe transaction, with player-bound linked storage.</summary>
    public const string SourcesCapability = "crafting.sources";
    /// <summary>A Kiln client's crafting screens count the server's linked-storage snapshot.</summary>
    public const string SourceUiCapability = "crafting.source_ui";
    /// <summary>Locks count and pay their required item from links registered with <see cref="CraftSourceOptions.Locks"/>:
    /// on a server or host its lock check and lock payment, on a Kiln client its lock check and locked prompt (from the
    /// server's snapshot). Off, or faulted, when a symbol, a hook or the game's code it reads is missing or patched by
    /// another module (kiln.log names it). Since 0.2.0-dev.31.12, mod API 0.2.21.</summary>
    public const string LockSourcesCapability = "items.lock_sources";
    /// <summary>Block placements count and pay from links registered with <see cref="CraftSourceOptions.Building"/>: on a
    /// server or host the build cursor's material check and the blueprint placement's payment, on a Kiln client its own
    /// build cursor (from the server's snapshot). Off, or faulted, when a symbol, a hook or the game's code it relies on is
    /// missing or patched by another module (kiln.log names it). Since 0.2.0-dev.31.12.</summary>
    public const string BuildSourcesCapability = "crafting.build_sources";
    /// <summary>The skill respec (a node's unlearn, the Flame Altar's reset) counts and pays its respec item from links
    /// registered with <see cref="CraftSourceOptions.Respec"/>: on a server or host the respec payment (the bag and the Magic
    /// chests first), on a Kiln client the skill tree's unlearn check and the Flame Altar's reset check (from the server's
    /// snapshot). Off, or faulted, when a symbol, a hook or the game's code it relies on is missing or patched by another
    /// module (kiln.log names it). Since 0.2.0-dev.31.18.</summary>
    public const string RespecSourcesCapability = "crafting.respec_sources";

    /// <summary>Registers a container for this player, in any base or none. The mod decides which containers the player
    /// may use; the server rechecks the player's own global CanAccessInventories permission and a readable initialized
    /// container at payment time. It does not enforce base ownership. Player/NPC inventories and production stations
    /// are refused; production stations lend their outputs through <see cref="Stations.SetOutputSource"/>. Sources pay
    /// only a shortfall after the bag and crafting base's stock, in one all-or-nothing recipe transaction. Equal-order
    /// sources use registration order; each container is counted once per item. A vanilla client keeps its own counts.
    /// Since 0.2.0-dev.31.12, mod API 0.2.21, <see cref="CraftSourceOptions.Locks"/> lets the same link supply a lock's
    /// required item.</summary>
    /// <returns>A session-only handle. Dispose or unload the registering mod to remove it; re-register after restart.</returns>
    /// <exception cref="ArgumentException">Invalid player, non-container, production station, or zero item id.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Unknown option or more than 65536 filter items.</exception>
    /// <exception cref="InvalidOperationException">Wrong thread, unloading mod, or 4096 registrations already exist.</exception>
    /// <exception cref="KilnUnavailableException">Wrong side, missing native service/payment symbols, offline or no server session.</exception>
    [ServerOnly, TickThreadOnly, RequiresCapability(SourcesCapability)]
    public static CraftSourceHandle AddSource(EntityId player, EntityId container, CraftSourceOptions? options = null)
    {
        Services.RequireTickThread("Crafting.AddSource");
        if (player.IsNone || player.Value > 1023) throw new ArgumentException("player must be a player entity (1..1023)", nameof(player));
        if (container.IsNone) throw new ArgumentException("no container", nameof(container));
        var o = options ?? new CraftSourceOptions();
        if (o.Order is not (CraftSourceOrder.BeforeStationOutputs or CraftSourceOrder.AfterStationOutputs)) throw new ArgumentOutOfRangeException(nameof(options), "unknown source order");
        if (o.Filter is not (CraftSourceFilter.Allow or CraftSourceFilter.Deny)) throw new ArgumentOutOfRangeException(nameof(options), "unknown item filter");
        if (o.Items?.Count > 65536) throw new ArgumentOutOfRangeException(nameof(options), "at most 65536 filter items");
        uint[] items = o.Items?.Select(i => i.Value).Distinct().Order().ToArray() ?? [];
        if (items.Contains(0u)) throw new ArgumentException("filter contains no item", nameof(options));
        var mod = ModContext.CurrentMod;
        if (mod is { Alive: false }) throw new InvalidOperationException("the mod is unloading");
        var owner = Services.CallerOwner;
        if (o.Locks) CraftingCore.NoteLocks();
        var purposes = (o.Fills || o.Building) && CraftingCore.NotePurposes();
        var fills = o.Fills && purposes;
        var building = o.Building && purposes;
        if (building) CraftingCore.NoteBuilding();
        var respec = o.Respec && CraftingCore.NoteRespec();
        var handle = CraftingCore.Add(owner, player, container, o.Order, o.Filter, items, o.Locks, fills, building, respec);
        return new CraftSourceHandle(owner, handle, player, container, mod, o.Locks, fills, building, respec);
    }
}

internal static unsafe class CraftingCore
{
    // Only the prefix through SourceStock is required; later appended members must not enlarge this requirement.
    internal static bool HasSourcePrefix(uint size) => size >= (uint)(StationsNative.TableFillUi + 4 * sizeof(nint));
    private static StationsNative.Table* Table
    {
        get
        {
            var header = Services.Get<ServiceHeader>(StationsNative.Name, 1);
            return header != null && HasSourcePrefix(header->Size) ? (StationsNative.Table*)header : null;
        }
    }
    internal static bool HasSources => Table is var t && t != null && t->AddSource != null && t->RemoveSource != null && t->SourceStock != null;
    private static readonly ServiceCapRows Rows = new(StationsNative.Name, () =>
    {
        if (!HasSources) ServiceCaps.Static(Crafting.SourcesCapability, false, CapabilityReason.ServiceOlder,
            "kiln.stations has no player crafting-source members", StationsNative.Name);
    });
    private static StationsNative.Table* Need()
    {
        Rows.Ensure();
        if (!Capabilities.Is(Crafting.SourcesCapability)) throw ServiceCaps.Unavailable(Crafting.SourcesCapability, "Crafting.AddSource cannot run");
        var t = Table;
        if (t == null || !HasSources) throw new KilnUnavailableException(Crafting.SourcesCapability, "kiln.stations has no player crafting-source members");
        return t;
    }
    // GitHub #147: kiln.stations' lock members (set_lock_stock, lock_asks) and the request's flags came in one release.
    internal static bool HasLockMembers(uint size) => size >= (uint)StationsNative.TableLocks;
    internal static bool HasLocks => Table is var t && t != null && HasLockMembers(t->Header.Size) && t->SetLockStock != null && t->LockAsks != null;
    private static int _locksNoted;
    // A link asked for Locks: say once why locks will not use it when they cannot (the link still serves crafting).
    internal static void NoteLocks()
    {
        if (!HasLocks)
            ServiceCaps.Static(Crafting.LockSourcesCapability, false, CapabilityReason.ServiceOlder,
                "kiln.stations has no lock-source members (older kiln-items.dll)", StationsNative.Name);
        if (Capabilities.Is(Crafting.LockSourcesCapability) || Interlocked.Exchange(ref _locksNoted, 1) != 0) return;
        ModLog.Warn($"crafting: a source asked for Locks, but locks do not use linked sources here (the link still serves crafting): " +
                    Capabilities.Why(Crafting.LockSourcesCapability));
    }
    // GitHub #132: kiln.stations' fill and building flags and set_build_stock came in one release (after #147's members).
    internal static bool HasPurposeMembers(uint size) => size >= (uint)StationsNative.TableBuild;
    internal static bool HasPurposes => Table is var t && t != null && HasPurposeMembers(t->Header.Size) && t->SetBuildStock != null;
    private static int _purposesNoted, _buildingNoted;
    /// <summary>A link asked for Fills or Building: false (and one kiln.log line) when this kiln-items.dll cannot keep them;
    /// the link then serves crafting only.</summary>
    internal static bool NotePurposes()
    {
        if (HasPurposes) return true;
        if (Interlocked.Exchange(ref _purposesNoted, 1) == 0)
            ModLog.Warn("crafting: a source asked for Fills or Building, but kiln.stations has no such members (older kiln-items.dll): " +
                        "the link serves crafting only");
        return false;
    }
    /// <summary>A link asked for Building: say once why placements will not use it when the capability is off.</summary>
    internal static void NoteBuilding()
    {
        if (Capabilities.Is(Crafting.BuildSourcesCapability) || Interlocked.Exchange(ref _buildingNoted, 1) != 0) return;
        ModLog.Warn("crafting: a source asked for Building, but block placements do not use linked sources here (the link still " +
                    "serves crafting): " + Capabilities.Why(Crafting.BuildSourcesCapability));
    }
    // GitHub #150: kiln.stations' respec flag and set_respec_stock came in one release (after fill_flags).
    internal static bool HasRespecMembers(uint size) => size >= (uint)StationsNative.TableRespec;
    internal static bool HasRespec => Table is var t && t != null && HasRespecMembers(t->Header.Size) && t->SetRespecStock != null;
    private static int _respecNoted;
    /// <summary>A link asked for Respec: false (the link serves crafting only) when this kiln-items.dll has no respec
    /// members; says once why the respec will not use it when the capability is off.</summary>
    internal static bool NoteRespec()
    {
        if (!HasRespec)
        {
            ServiceCaps.Static(Crafting.RespecSourcesCapability, false, CapabilityReason.ServiceOlder,
                "kiln.stations has no respec-source members (older kiln-items.dll)", StationsNative.Name);
            if (Interlocked.Exchange(ref _respecNoted, 1) == 0)
                ModLog.Warn("crafting: a source asked for Respec, but kiln.stations has no such member (older kiln-items.dll): " +
                            "the link serves crafting only");
            return false;
        }
        if (!Capabilities.Is(Crafting.RespecSourcesCapability) && Interlocked.Exchange(ref _respecNoted, 1) == 0)
            ModLog.Warn("crafting: a source asked for Respec, but the skill respec does not use linked sources here (the link still " +
                        "serves crafting): " + Capabilities.Why(Crafting.RespecSourcesCapability));
        return true;
    }
    internal static ulong Add(ulong owner, EntityId player, EntityId container, CraftSourceOrder order, CraftSourceFilter filter, uint[] items,
        bool locks = false, bool fills = false, bool building = false, bool respec = false)
    {
        var t = Need(); ulong id = 0; int rc;
        fixed (uint* p = items)
        {
            var r = new StationsNative.SourceRequest
            {
                Size = (uint)sizeof(StationsNative.SourceRequest),
                Player = player.Value,
                Container = container.Value,
                Order = (uint)order,
                Filter = (uint)filter,
                ItemCount = (uint)items.Length,
                Items = p,
                Flags = (locks && HasLocks ? StationsNative.SourceLocks : 0u) |
                        (fills && HasPurposes ? StationsNative.SourceFills : 0u) | (building && HasPurposes ? StationsNative.SourceBuilding : 0u) |
                        (respec && HasRespec ? StationsNative.SourceRespec : 0u),
            };
            rc = t->AddSource(owner, &r, &id);
        }
        if (rc == StationsNative.InvalidArgument) throw new ArgumentException("Crafting.AddSource: player or initialized storage container is invalid (player/NPC inventories are not containers; production stations lend their outputs through Stations.SetOutputSource)");
        if (rc == StationsNative.BufferTooSmall) throw new InvalidOperationException("Crafting.AddSource: 4096 sources are registered already");
        if (rc != StationsNative.Ok || id == 0) throw new KilnUnavailableException(Crafting.SourcesCapability, "Crafting.AddSource: no server session, or native payment/access checks are unavailable");
        return id;
    }
    internal static void Remove(ulong owner, ulong handle)
    {
        var t = Table; if (t != null && t->RemoveSource != null) t->RemoveSource(owner, handle);
    }
    internal static (int Rc, StationsNative.Stack[] Rows) ExtraStock(EntityId player) => Stock(player, 0);
    internal static (int Rc, StationsNative.Stack[] Rows) AllStock(EntityId player) => Stock(player, 1);
    /// <summary>The lock-opted links' counts alone (no bag, no base; GitHub #147).</summary>
    internal static (int Rc, StationsNative.Stack[] Rows) LockStock(EntityId player) =>
        HasLocks ? Stock(player, StationsNative.StockLocks) : (StationsNative.Unavailable, []);
    internal static int SetLockStock(bool on, StationsNative.Stack[] rows)
    {
        if (!HasLocks) return StationsNative.Unavailable;
        fixed (StationsNative.Stack* p = rows) return Table->SetLockStock(on ? 1u : 0u, p, (uint)rows.Length);
    }
    /// <summary>GitHub #132: one purpose's links alone (no bag, no base): Fills or Building.</summary>
    internal static (int Rc, StationsNative.Stack[] Rows) FillStock(EntityId player) =>
        HasPurposes ? Stock(player, StationsNative.StockFills) : (StationsNative.Unavailable, []);
    internal static (int Rc, StationsNative.Stack[] Rows) BuildStockRows(EntityId player) =>
        HasPurposes ? Stock(player, StationsNative.StockBuilding) : (StationsNative.Unavailable, []);
    internal static int SetBuildStock(bool on, StationsNative.Stack[] rows)
    {
        if (!HasPurposes) return StationsNative.Unavailable;
        fixed (StationsNative.Stack* p = rows) return Table->SetBuildStock(on ? 1u : 0u, p, (uint)rows.Length);
    }
    /// <summary>GitHub #150: the respec links' row of the respec item (no bag, no Magic chests of the player's base).</summary>
    internal static (int Rc, StationsNative.Stack[] Rows) RespecStockRows(EntityId player) =>
        HasRespec ? Stock(player, StationsNative.StockRespec) : (StationsNative.Unavailable, []);
    internal static int SetRespecStock(bool on, StationsNative.Stack[] rows)
    {
        if (!HasRespec) return StationsNative.Unavailable;
        fixed (StationsNative.Stack* p = rows) return Table->SetRespecStock(on ? 1u : 0u, p, (uint)rows.Length);
    }
    internal static (int Rc, uint[] Items) LockAsks()
    {
        if (!HasLocks) return (StationsNative.Unavailable, []);
        var items = new uint[StationsNative.MaxLockAsks]; uint n = 0;
        int rc;
        fixed (uint* p = items) rc = Table->LockAsks(p, (uint)items.Length, &n);
        return rc == StationsNative.Ok && n <= items.Length ? (rc, items[..(int)n]) : (rc, []);
    }
    private static (int Rc, StationsNative.Stack[] Rows) Stock(EntityId player, uint flags)
    {
        if (!HasSources || !Capabilities.Is(Crafting.SourcesCapability)) return (StationsNative.Unavailable, []);
        var rows = new StationsNative.Stack[StationsNative.MaxCraftStock]; uint n = 0;
        fixed (StationsNative.Stack* p = rows)
        {
            var rc = Table->SourceStock(player.Value, flags, p, (uint)rows.Length, &n);
            if (rc != StationsNative.Ok || n > rows.Length) return (rc == StationsNative.Ok ? StationsNative.BufferTooSmall : rc, []);
        }
        return (StationsNative.Ok, rows[..(int)n]);
    }
}
