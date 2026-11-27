// SPDX-License-Identifier: MIT
// Gem wave G3: a Shroud Forge's four gem actions from code (Gems.Socket, Unsocket, Upgrade, Recycle) and the players' own
// forge actions before (GemCraftingEvent, cancelable) and after (GemCraftedEvent). The native side is kiln.gemcraft in
// kiln-items.dll (native/core/abi/services/kiln_gemcraft.h): it runs the game's own weapon-gem handler, the one a
// player's forge action runs, with every check and cost of the game. Capability gems.craft. docs/kiln/services/gems-forge.md.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>A Shroud Forge's gem actions (the game's <c>WeaponGemActionType</c>).</summary>
public enum GemCraftAction : byte
{
    /// <summary>A gem goes into a carrier's empty socket.</summary>
    Socket = 1,
    /// <summary>The gem in a carrier's socket goes back into the player's backpack.</summary>
    Unsocket = 2,
    /// <summary>A gem's level goes up by one, paid in the forge's material.</summary>
    Upgrade = 3,
    /// <summary>A gem is destroyed for the forge's essence.</summary>
    Recycle = 4,
}

/// <summary>An amount of one item: what a forge action costs, or what Recycle gives. <see cref="Free"/> (the default) is
/// nothing.</summary>
/// <param name="Item">The item.</param>
/// <param name="Count">How many; 0 is nothing.</param>
public readonly record struct GemCost(ItemId Item, int Count)
{
    /// <summary>Nothing.</summary>
    public static GemCost Free => default;

    /// <summary>True when this is nothing.</summary>
    public bool IsFree => Count <= 0 || Item.IsNone;

    /// <summary><c>"5 x 0x1234abcd"</c>, or <c>"nothing"</c>.</summary>
    public override string ToString() => IsFree ? "nothing" : $"{Count} x {Item}";
}

/// <summary>Options of the forge actions (<see cref="Gems.Socket(EntityId, ItemInstanceRef, ItemInstanceRef, EntityId, GemCraftOptions?)"/>
/// and the others).</summary>
public sealed record GemCraftOptions
{
    /// <summary>Where the carrier and the gem are: none (the default) for the player's own inventories, or an entity with
    /// an inventory your mod vouches for (a chest the player may use). <see cref="GemHolder"/> and
    /// <see cref="CarrierHolder"/>, when set, name each one's holder instead.</summary>
    public EntityId Holder { get; init; }

    /// <summary>Where the gem is (Socket; a loose gem for Upgrade and Recycle): none (the default) for
    /// <see cref="Holder"/>, else a container your mod vouches for. The gem and the carrier may be in two different
    /// holders: the game's own action takes the gem from one and puts it into the carrier in the other in one
    /// transaction. Since 0.2.0-dev.29.15.</summary>
    public EntityId GemHolder { get; init; }

    /// <summary>Where the carrier (the item with the socket) is: none (the default) for <see cref="Holder"/>, else a
    /// container your mod vouches for. Since 0.2.0-dev.29.15.</summary>
    public EntityId CarrierHolder { get; init; }

    /// <summary>Who pays the game's cost (Socket, Upgrade; Unsocket is free on this game build): the player (the
    /// default), a container (<see cref="GemPayer.From"/>), or the base's Magic chests as the game's crafting from base
    /// storage uses them (<see cref="GemPayer.BaseStorage"/>, <see cref="GemPayer.PlayerThenBaseStorage"/>). The game
    /// still decides the cost and takes it inside the action's own transaction: when the payers cannot pay it, nothing
    /// is paid and nothing moves (<see cref="GemCraftStatus.NotEnough"/>). Other than the player it needs the capability
    /// <c>gems.payers</c>, and a replaced <see cref="Cost"/> (not free) is refused with it: Kiln takes a replacement
    /// from the player. Since 0.2.0-dev.29.15.</summary>
    public GemPayer PayFrom { get; init; }

    /// <summary>Where an unsocketed gem (Unsocket) and Recycle's essence go: none (the default) for the player, else a
    /// container your mod vouches for (it needs <c>gems.payers</c>). Since 0.2.0-dev.29.15.</summary>
    public EntityId ReturnTo { get; init; }

    /// <summary>Null (the default): the game's cost, paid by the player in the forge's material. <see cref="GemCost.Free"/>:
    /// the player pays nothing. Any other amount: the player pays that instead. For Recycle it replaces what the player
    /// gets: nothing for <see cref="GemCost.Free"/>, that amount instead of the forge's essence otherwise. Meant for
    /// creative tools and quest rewards; the game's action still runs with all its other checks.</summary>
    public GemCost? Cost { get; init; }
}

/// <summary>How a forge action ended.</summary>
public enum GemCraftStatus
{
    /// <summary>It happened.</summary>
    Done = 0,
    /// <summary>The game's own checks refused it: nothing changed.</summary>
    Refused = 1,
    /// <summary>The player cannot pay.</summary>
    NotEnough = 2,
    /// <summary>The forge entity is no Shroud Forge.</summary>
    NoForge = 3,
    /// <summary>The carrier, its socket or the gem is not where the call says, or the item is not a gem.</summary>
    NotFound = 4,
    /// <summary>No free backpack slot (Unsocket), or no room for the payment Kiln hands over for a free or replaced cost.</summary>
    NoRoom = 5,
    /// <summary>Upgrade: the gem is at its own cap or at the forge's maximum level.</summary>
    MaxLevel = 6,
    /// <summary>A player's action that a <see cref="GemCraftingEvent"/> handler canceled.</summary>
    Canceled = 7,
    /// <summary><c>gems.craft</c> went off after the call.</summary>
    Unavailable = 8,
    /// <summary>Not run: the player left, the world closed, or the player's program did not run it within 10 s.</summary>
    Dropped = 9,
    /// <summary>Socket: the socket holds a gem already. Unsocket: the socket is empty.</summary>
    Occupied = 10,
}

/// <summary>What a forge action did. <see cref="Ok"/> when it happened; otherwise <see cref="Reason"/> says why and
/// nothing changed.</summary>
/// <param name="Status">The outcome.</param>
/// <param name="Action">The action.</param>
/// <param name="Player">The player it ran for.</param>
/// <param name="Forge">The forge; none for <see cref="Gems.ReturnSocketedGems"/>' forgeless return.</param>
/// <param name="Carrier">The item with the socket; none for a loose gem.</param>
/// <param name="Gem">The gem.</param>
/// <param name="LevelBefore">The gem's level before.</param>
/// <param name="LevelAfter">After (Upgrade: one more; Recycle: 0).</param>
/// <param name="Paid">What the player paid.</param>
/// <param name="Received">What the player got (Recycle).</param>
/// <param name="Message">Why, for anything but <see cref="GemCraftStatus.Done"/>; a note otherwise (may be empty).</param>
public sealed record GemCraftResult(
    GemCraftStatus Status,
    GemCraftAction Action,
    EntityId Player,
    EntityId Forge,
    ItemInstanceRef Carrier,
    ItemInstanceRef Gem,
    int LevelBefore,
    int LevelAfter,
    GemCost Paid,
    GemCost Received,
    string Message) : IKilnResult
{
    /// <summary>True when the action happened.</summary>
    public bool Ok => Status == GemCraftStatus.Done;

    /// <summary>Why it did not happen; null when <see cref="Ok"/>.</summary>
    public string? Reason => Ok ? null : Message.Length > 0 ? Message : Status.ToString();

    /// <summary>The outcome in one line, for a log.</summary>
    public override string ToString() =>
        $"{Action} {Status} player {Player} carrier {Carrier} gem {Gem} level {LevelBefore} -> {LevelAfter} paid {Paid} got {Received}" +
        (Message.Length > 0 ? $": {Message}" : "");
}

/// <summary>
/// A player is about to run a forge action: socket, unsocket, upgrade or recycle a gem at a Shroud Forge. Posted on the
/// tick thread in Pre, one step after the player's input arrived; the game holds the action until then. Cancel it to
/// refuse it: the game consumes it as a request without a forge, so nothing is paid and nothing moves. Not canceled, it
/// runs in the next step with the game's own checks (which can still refuse it). Only the players' own actions, not
/// <see cref="Gems"/>' calls. A held action nobody answers within 8 steps runs.
/// </summary>
/// <remarks>The client is not told about a cancel: its forge screen shows the action as sent until the next update of the
/// inventory. Capability <c>gems.craft</c> (the game's gem handler hooked; on a dedicated server and a hosting client).</remarks>
[ServerOnly, TickThreadOnly]
public sealed class GemCraftingEvent : CancelableEvent, IEntityEvent
{
    static GemCraftingEvent() => GemCrafting.EnsureProducers();

    /// <summary>Makes the event (Kiln posts it; a mod has no reason to).</summary>
    public GemCraftingEvent(EntityId player, EntityId forge, GemCraftAction action, ItemInstanceRef carrier, ItemInstanceRef gem,
        int gemLevel, GemCost cost, GemCost yield, uint version)
    {
        Player = player;
        Forge = forge;
        Action = action;
        Carrier = carrier;
        Gem = gem;
        GemLevel = gemLevel;
        Cost = cost;
        Yield = yield;
        Version = version;
    }

    /// <summary>The player.</summary>
    public EntityId Player { get; }

    /// <summary>The forge the player uses.</summary>
    public EntityId Forge { get; }

    /// <summary>What the player asked for.</summary>
    public GemCraftAction Action { get; }

    /// <summary>The item with the socket; none for a loose gem (Upgrade, Recycle).</summary>
    public ItemInstanceRef Carrier { get; }

    /// <summary>The gem.</summary>
    public ItemInstanceRef Gem { get; }

    /// <summary>The gem's level now.</summary>
    public int GemLevel { get; }

    /// <summary>What the game will charge the player (the game's own cost function, with this forge); nothing for Recycle.</summary>
    public GemCost Cost { get; }

    /// <summary>Recycle: the essence the game will give; nothing for the other actions.</summary>
    public GemCost Yield { get; }

    /// <summary>The version of the player's input (one action of one player).</summary>
    public uint Version { get; }

    /// <summary>Whether the event is about <paramref name="entity"/>: the player, the forge, the carrier or the gem.</summary>
    public bool Involves(EntityId entity) =>
        !entity.IsNone && (entity == Player || entity == Forge || Carrier.Entity.Id == entity || Gem.Entity.Id == entity);

    /// <summary>The action in one line, for a log.</summary>
    public override string ToString() => $"gem crafting: player {Player} {Action} carrier {Carrier} gem {Gem} at forge {Forge} for {Cost}";
}

/// <summary>A forge action happened: a player's own (after the game committed it) or one of <see cref="Gems"/>' calls,
/// <see cref="Gems.ReturnSocketedGems"/> included (Unsocket, no forge). Posted on the tick thread in Pre, one step after
/// the step it ran in. Not cancelable. Only actions that changed something: a refused or canceled one posts nothing.
/// Capability <c>gems.craft</c>. Gems moved any other way (a mod's own item moves) are not seen here;
/// <see cref="GemSocketChangedEvent"/> sees every socket change, these included, a few steps later.</summary>
[ServerOnly]
public sealed class GemCraftedEvent : KilnEvent, IEntityEvent
{
    static GemCraftedEvent() => GemCrafting.EnsureProducers();

    internal GemCraftedEvent(GemCraftResult result, bool byCode)
    {
        Result = result;
        ByCode = byCode;
    }

    /// <summary>What happened.</summary>
    public GemCraftResult Result { get; }

    /// <summary>True for a call of <see cref="Gems"/>, false for a player's own forge action.</summary>
    public bool ByCode { get; }

    /// <summary>The player.</summary>
    public EntityId Player => Result.Player;

    /// <summary>The forge; none for <see cref="Gems.ReturnSocketedGems"/>' forgeless return.</summary>
    public EntityId Forge => Result.Forge;

    /// <summary>The action.</summary>
    public GemCraftAction Action => Result.Action;

    /// <summary>The item with the socket; none for a loose gem.</summary>
    public ItemInstanceRef Carrier => Result.Carrier;

    /// <summary>The gem.</summary>
    public ItemInstanceRef Gem => Result.Gem;

    /// <summary>The gem's level before.</summary>
    public int LevelBefore => Result.LevelBefore;

    /// <summary>The gem's level after (Recycle: 0).</summary>
    public int LevelAfter => Result.LevelAfter;

    /// <summary>What the player paid.</summary>
    public GemCost Paid => Result.Paid;

    /// <summary>What the player got (Recycle).</summary>
    public GemCost Received => Result.Received;

    /// <summary>Whether the event is about <paramref name="entity"/>: the player, the forge, the carrier or the gem.</summary>
    public bool Involves(EntityId entity) =>
        !entity.IsNone && (entity == Player || entity == Forge || Carrier.Entity.Id == entity || Gem.Entity.Id == entity);

    /// <summary>The action in one line, for a log.</summary>
    public override string ToString() => $"gem crafted{(ByCode ? " (code)" : "")}: {Result}";
}

public static partial class Gems
{
    // gem wave integration: G4's ReturnSocketedGems takes gems out of non-weapon sockets through G3's free, forge-less
    // unslot (KILN_GEM_RETURN, the game's own unslot move)
    static partial void UnsocketForReturn(EntityId owner, ItemInstanceRef carrier, ref Task<(bool Ok, string? Reason)>? unsocket) =>
        unsocket = GemCrafting.UnsocketWithoutForge(owner, carrier);

    /// <summary>The capability row of the forge actions and their events.</summary>
    public const string CraftCapability = "gems.craft";

    /// <summary>
    /// Puts <paramref name="gem"/> into the empty socket of <paramref name="carrier"/>, as the player's own Insert at
    /// <paramref name="forge"/> does: the game's action with its checks and its cost (paid by <paramref name="player"/>
    /// in the forge's material). Both items are looked up in the player's inventories (or in
    /// <see cref="GemCraftOptions.Holder"/>'s).
    /// </summary>
    /// <param name="player">The player character the action runs for.</param>
    /// <param name="carrier">The item with the socket (any item with a gem socket: a weapon, or what sockets other mods add).</param>
    /// <param name="gem">The gem item.</param>
    /// <param name="forge">A placed Shroud Forge: its tier sets the cost and the maximum level.</param>
    /// <param name="options">A holder, or a free or replaced cost.</param>
    /// <returns>Completes on the tick thread one step after the step it ran in. A refusal is a result
    /// (<see cref="GemCraftResult.Ok"/> false); nothing changed then.</returns>
    /// <exception cref="ArgumentException">An argument is none, or the cost is negative, over 100000, or an item with an
    /// entity of its own (gear, a gem): a replaced price must be a plain item.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or the calling mod is unloading.</exception>
    /// <exception cref="KilnUnavailableException">The capability <c>gems.craft</c> is off.</exception>
    /// <example><code>
    /// GemCraftResult r = await Gems.Socket(player, sword.Ref, gem.Ref, forge);
    /// if (!r.Ok) Log.Info($"not socketed: {r.Reason}");
    /// </code></example>
    [ServerOnly, TickThreadOnly, RequiresCapability(CraftCapability)]
    public static Task<GemCraftResult> Socket(EntityId player, ItemInstanceRef carrier, ItemInstanceRef gem, EntityId forge,
        GemCraftOptions? options = null)
    {
        GemCrafting.Check("Gems.Socket", player, forge);
        if (carrier.IsNone) throw new ArgumentException("no carrier", nameof(carrier));
        if (gem.IsNone) throw new ArgumentException("no gem", nameof(gem));
        return GemCrafting.Submit(GemCraftAction.Socket, player, carrier, gem, forge, options, "Gems.Socket");
    }

    /// <summary>Takes the gem out of <paramref name="carrier"/>'s socket into <paramref name="player"/>'s backpack, as the
    /// player's own Remove at <paramref name="forge"/> does (the game's cost; free in the game's data today).</summary>
    /// <returns>As <see cref="Socket(EntityId, ItemInstanceRef, ItemInstanceRef, EntityId, GemCraftOptions?)"/>;
    /// <see cref="GemCraftStatus.Occupied"/> when the socket is empty, <see cref="GemCraftStatus.NoRoom"/> when the
    /// backpack is full.</returns>
    /// <exception cref="ArgumentException">An argument is none, or the cost is negative, over 100000, or an item with an
    /// entity of its own (gear, a gem): a replaced price must be a plain item.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or the calling mod is unloading.</exception>
    /// <exception cref="KilnUnavailableException">The capability <c>gems.craft</c> is off.</exception>
    [ServerOnly, TickThreadOnly, RequiresCapability(CraftCapability)]
    public static Task<GemCraftResult> Unsocket(EntityId player, ItemInstanceRef carrier, EntityId forge, GemCraftOptions? options = null)
    {
        GemCrafting.Check("Gems.Unsocket", player, forge);
        if (carrier.IsNone) throw new ArgumentException("no carrier", nameof(carrier));
        return GemCrafting.Submit(GemCraftAction.Unsocket, player, carrier, default, forge, options, "Gems.Unsocket");
    }

    /// <summary>Raises a gem's level by one, as the player's own Upgrade at <paramref name="forge"/> does: the game's cost
    /// in the forge's material, refused at the gem's own cap or the forge's maximum level.</summary>
    /// <param name="player">The player character the action runs for (it pays).</param>
    /// <param name="gemOrCarrier">A loose gem, or an item whose socket holds the gem.</param>
    /// <param name="forge">A placed Shroud Forge.</param>
    /// <param name="options">A holder, or a free or replaced cost.</param>
    /// <returns>As <see cref="Socket(EntityId, ItemInstanceRef, ItemInstanceRef, EntityId, GemCraftOptions?)"/>;
    /// <see cref="GemCraftStatus.MaxLevel"/> at a cap.</returns>
    /// <exception cref="ArgumentException">An argument is none, or the cost is negative, over 100000, or an item with an
    /// entity of its own (gear, a gem): a replaced price must be a plain item.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or the calling mod is unloading.</exception>
    /// <exception cref="KilnUnavailableException">The capability <c>gems.craft</c> is off.</exception>
    [ServerOnly, TickThreadOnly, RequiresCapability(CraftCapability)]
    public static Task<GemCraftResult> Upgrade(EntityId player, ItemInstanceRef gemOrCarrier, EntityId forge, GemCraftOptions? options = null)
    {
        GemCrafting.Check("Gems.Upgrade", player, forge);
        if (gemOrCarrier.IsNone) throw new ArgumentException("no gem or carrier", nameof(gemOrCarrier));
        return GemCrafting.Submit(GemCraftAction.Upgrade, player, gemOrCarrier, default, forge, options, "Gems.Upgrade");
    }

    /// <summary>Destroys a gem for the forge's essence, as the player's own Recycle at <paramref name="forge"/> does (the
    /// amount by the gem's level and the forge's tier).</summary>
    /// <param name="player">The player character the action runs for (it gets the essence).</param>
    /// <param name="gemOrCarrier">A loose gem, or an item whose socket holds the gem.</param>
    /// <param name="forge">A placed Shroud Forge (its essence is what the player gets).</param>
    /// <param name="options">A holder, or what the player gets instead (<see cref="GemCraftOptions.Cost"/>).</param>
    /// <returns>As <see cref="Socket(EntityId, ItemInstanceRef, ItemInstanceRef, EntityId, GemCraftOptions?)"/>.</returns>
    /// <exception cref="ArgumentException">An argument is none, or the cost is negative or over 100000.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or the calling mod is unloading.</exception>
    /// <exception cref="KilnUnavailableException">The capability <c>gems.craft</c> is off.</exception>
    [ServerOnly, TickThreadOnly, RequiresCapability(CraftCapability)]
    public static Task<GemCraftResult> Recycle(EntityId player, ItemInstanceRef gemOrCarrier, EntityId forge, GemCraftOptions? options = null)
    {
        GemCrafting.Check("Gems.Recycle", player, forge);
        if (gemOrCarrier.IsNone) throw new ArgumentException("no gem or carrier", nameof(gemOrCarrier));
        return GemCrafting.Submit(GemCraftAction.Recycle, player, gemOrCarrier, default, forge, options, "Gems.Recycle");
    }
}

/// <summary>The native table of kiln.gemcraft (kiln_gemcraft.h). Blittable; field order is the ABI.</summary>
internal static unsafe class GemcraftNative
{
    public const string Name = "kiln.gemcraft";
    public const uint Version = 1;
    public const int Ok = 0, InvalidArgument = 1, NotFound = 2, Unavailable = 3, BufferTooSmall = 5;
    public const uint Free = 0x1, Cost = 0x2, Return = 0x4;
    public const uint ByCode = 0x1, Priced = 0x8;
    public const int MaxCost = 100000;

    public const uint HasPayers = 0x1, HasSources = 0x2;
    public const uint SourceFlag = 0x10, RoutedFlag = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    public struct Request
    {
        public uint Size, Action, Flags, Player, Holder, Carrier;
        public ulong GemSlot;
        public uint Gem, Forge, CostItem, CostCount;
        public uint Reserved0, Reserved1;
        // appended in 0.2.0-dev.29.15
        public uint GemHolder, CarrierHolder, PayFrom, PayHolder, ReturnTo, Reserved2;
    }

    /// <summary>KilnGemSource: one offered gem (0.2.0-dev.29.15).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Source
    {
        public uint Size, Player, Holder;
        public ulong Slot;
        public uint Gem, Item, Level, PayFrom, PayHolder, ReturnTo, LifetimeMs, Rarity, Reserved;
        public ulong Token, ExpiresMs;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Craft
    {
        public uint Size, Action;
        public int Status;
        public uint Flags, Player, Forge, Carrier, CarrierItem, Gem, GemItem;
        public ulong GemSlot;
        public uint LevelBefore, LevelAfter, LevelCap, ForgeMaxLevel, CostItem, CostCount, YieldItem, YieldCount, Version, Reserved;
        public ulong Ticket, Step;
        public fixed byte Message[128];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public uint Size;
        public byte Available, Hook, Program, Holding;
        public uint Held, Canceled, Crafted, Pending, Reserved;
        public fixed byte Detail[160];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<Status*, int> GetStatus;
        public delegate* unmanaged[Cdecl]<ulong, Request*, delegate* unmanaged[Cdecl]<Craft*, void*, void>, void*, ulong*, int> Submit;
        public delegate* unmanaged[Cdecl]<ulong, int> Cancel;
        public delegate* unmanaged[Cdecl]<uint, int> Hold;
        public delegate* unmanaged[Cdecl]<Craft*, uint, uint*, int> Held;
        public delegate* unmanaged[Cdecl]<uint, uint, uint, int> Decide;
        public delegate* unmanaged[Cdecl]<uint, int> Watch;
        public delegate* unmanaged[Cdecl]<Craft*, uint, uint*, int> Crafted;
        // appended in 0.2.0-dev.29.15 (read only when Header.Size covers them)
        public delegate* unmanaged[Cdecl]<uint*, byte*, uint, int> Extras;
        public delegate* unmanaged[Cdecl]<ulong, Source*, uint, int> Offer;
        public delegate* unmanaged[Cdecl]<ulong, uint, uint, uint*, int> Withdraw;
        public delegate* unmanaged[Cdecl]<uint, Source*, uint, uint*, int> Sources;
        public delegate* unmanaged[Cdecl]<uint, uint, uint, uint*, uint, uint*, int> Payers;
        public delegate* unmanaged[Cdecl]<uint, uint*, int> ForgeMaterial;
    }

    /// <summary>The table's size before 0.2.0-dev.29.15 (eight functions).</summary>
    public static readonly uint V1TableSize = (uint)(sizeof(ServiceHeader) + 8 * sizeof(nint));

    public static string Text(byte* p, int max)
    {
        var n = 0;
        while (n < max && p[n] != 0) n++;
        return System.Text.Encoding.UTF8.GetString(p, n);
    }
}

/// <summary>What Kiln asks of kiln.gemcraft (the table, or a test's fake).</summary>
internal interface IGemcraftBackend
{
    /// <summary>Null when the service is not here; else its status.</summary>
    (bool Available, string Detail)? Status();
    int Submit(in GemcraftNative.Request r, long key, out ulong ticket);
    int Hold(uint steps);
    int Held(Span<GemcraftNative.Craft> into, out uint count);
    int Decide(uint player, uint version, bool cancel);
    int Watch(bool on);
    int Crafted(Span<GemcraftNative.Craft> into, out uint count);
    /// <summary>The appended parts (0.2.0-dev.29.15): null when this kiln.gemcraft predates them.</summary>
    (uint Flags, string Why)? Extras();
    int Offer(ulong owner, Span<GemcraftNative.Source> sources);
    int Withdraw(ulong owner, uint player, uint holder, out uint count);
    int Sources(uint player, Span<GemcraftNative.Source> into, out uint count);
    int Payers(uint player, uint payFrom, uint payHolder, Span<uint> into, out uint count);
    /// <summary>The forge's own upgrade material (Ok); NotFound: not read yet (ask again); InvalidArgument: no forge.</summary>
    int ForgeMaterial(uint forge, out uint item);
}

/// <summary>Kiln.Abilities' side of kiln.gemcraft: submissions and results, the pump of held and crafted actions.</summary>
internal static unsafe class GemCrafting
{
    private const uint HoldSteps = 4;   // renewed every Pre while a GemCraftingEvent handler listens
    private const int Batch = 16;

    private sealed class NativeBackend : IGemcraftBackend
    {
        private static GemcraftNative.Table* _t;

        private static GemcraftNative.Table* T
        {
            get
            {
                if (_t != null) return _t;
                var t = Services.Get<GemcraftNative.Table>(GemcraftNative.Name, GemcraftNative.Version);
                if (t == null || t->Header.Size < GemcraftNative.V1TableSize) return null;
                return _t = t;
            }
        }

        public (bool Available, string Detail)? Status()
        {
            var t = T;
            if (t == null) return null;
            GemcraftNative.Status s = default;
            s.Size = (uint)sizeof(GemcraftNative.Status);
            if (t->GetStatus(&s) != GemcraftNative.Ok) return null;
            return (s.Available != 0, GemcraftNative.Text(s.Detail, 160));
        }

        public int Submit(in GemcraftNative.Request r, long key, out ulong ticket)
        {
            ulong tk = 0;
            var copy = r;
            var rc = T->Submit(0, &copy, &OnDone, (void*)key, &tk);
            ticket = tk;
            return rc;
        }

        public int Hold(uint steps) => T == null ? GemcraftNative.Unavailable : T->Hold(steps);

        public int Held(Span<GemcraftNative.Craft> into, out uint count)
        {
            uint n = 0;
            int rc;
            fixed (GemcraftNative.Craft* p = into) rc = T->Held(p, (uint)into.Length, &n);
            count = n;
            return rc;
        }

        public int Decide(uint player, uint version, bool cancel) => T->Decide(player, version, cancel ? 1u : 0u);

        public int Watch(bool on) => T == null ? GemcraftNative.Unavailable : T->Watch(on ? 1u : 0u);

        public int Crafted(Span<GemcraftNative.Craft> into, out uint count)
        {
            uint n = 0;
            int rc;
            fixed (GemcraftNative.Craft* p = into) rc = T->Crafted(p, (uint)into.Length, &n);
            count = n;
            return rc;
        }

        // the appended functions: only when the table is the 0.2.0-dev.29.15 one
        private static GemcraftNative.Table* T2
        {
            get
            {
                var t = T;
                return t != null && t->Header.Size >= (uint)sizeof(GemcraftNative.Table) ? t : null;
            }
        }

        public (uint Flags, string Why)? Extras()
        {
            var t = T2;
            if (t == null) return null;
            uint f = 0;
            var why = stackalloc byte[160];
            why[0] = 0;
            if (t->Extras(&f, why, 160) != GemcraftNative.Ok) return null;
            return (f, GemcraftNative.Text(why, 160));
        }

        public int Offer(ulong owner, Span<GemcraftNative.Source> sources)
        {
            var t = T2;
            if (t == null) return GemcraftNative.Unavailable;
            fixed (GemcraftNative.Source* p = sources) return t->Offer(owner, p, (uint)sources.Length);
        }

        public int Withdraw(ulong owner, uint player, uint holder, out uint count)
        {
            uint n = 0;
            var t = T2;
            var rc = t == null ? GemcraftNative.Unavailable : t->Withdraw(owner, player, holder, &n);
            count = n;
            return rc;
        }

        public int Sources(uint player, Span<GemcraftNative.Source> into, out uint count)
        {
            uint n = 0;
            var t = T2;
            int rc;
            if (t == null) rc = GemcraftNative.Unavailable;
            else
                fixed (GemcraftNative.Source* p = into) rc = t->Sources(player, p, (uint)into.Length, &n);
            count = n;
            return rc;
        }

        public int Payers(uint player, uint payFrom, uint payHolder, Span<uint> into, out uint count)
        {
            uint n = 0;
            var t = T2;
            int rc;
            if (t == null) rc = GemcraftNative.Unavailable;
            else
                fixed (uint* p = into) rc = t->Payers(player, payFrom, payHolder, p, (uint)into.Length, &n);
            count = n;
            return rc;
        }

        public int ForgeMaterial(uint forge, out uint item)
        {
            uint m = 0;
            var t = T2;
            var rc = t == null ? GemcraftNative.Unavailable : t->ForgeMaterial(forge, &m);
            item = m;
            return rc;
        }
    }

    private static IGemcraftBackend _backend = new NativeBackend();

    /// <summary>The backend in use (the native table, or a test's fake).</summary>
    internal static IGemcraftBackend Backend => _backend;
    private static int _producers;
    private static IDisposable? _tick;
    private static bool _holding, _watching;
    private static readonly object Gate = new();
    private static readonly Dictionary<long, (TaskCompletionSource<GemCraftResult> Tcs, ModContext? Mod)> InFlight = new();
    private static long _next;

    /// <summary>Test hook: a fake backend (null: the native table again).</summary>
    internal static void UseBackend(IGemcraftBackend? backend)
    {
        _backend = backend ?? new NativeBackend();
        _holding = _watching = false;
    }

    /// <summary>The events' static constructors: both are produced only while gems.craft reads on and kiln.gemcraft is here.</summary>
    internal static void EnsureProducers()
    {
        if (Interlocked.Exchange(ref _producers, 1) != 0) return;
        EventProducers.Register(typeof(GemCraftingEvent), Gems.CraftCapability, WhyOff);
        EventProducers.Register(typeof(GemCraftedEvent), Gems.CraftCapability, WhyOff);
        Start();
    }

    // Why the events cannot be produced now although gems.craft reads on; null: they can.
    internal static string? WhyOff() => _backend.Status() switch
    {
        null => "kiln.gemcraft is not here (an older kiln-items.dll, or no server world in this process)",
        { Available: false } s => s.Detail.Length > 0 ? s.Detail : "kiln.gemcraft reports the forge actions off",
        _ => null,
    };

    /// <summary>gems.craft is a sigdb feature: where kiln.gemcraft is not here (an older kiln-items.dll, the test kit)
    /// Kiln.Abilities' start lowers it with the reason, so it never reads on without its service.</summary>
    internal static readonly ServiceCapRows Rows = new("kiln.gemcraft", LowerWithoutService);

    /// <summary>Kiln.Abilities' start (AbilitiesStartup).</summary>
    internal static void EnsureCaps() => Rows.Ensure();

    private static void LowerWithoutService()
    {
        try
        {
            if (_backend.Status() is null) ServiceCaps.LowerFeatures(GemcraftNative.Name, Gems.CraftCapability, Gems.PayersCapability);
            else if (_backend.Extras() is null)  // a kiln.gemcraft before 0.2.0-dev.29.15: no payers
                ServiceCaps.LowerOff(Gems.PayersCapability, CapabilityReason.ServiceOlder, "kiln.gemcraft predates payers (an older kiln-items.dll)");
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            ModLog.Warn($"kiln.abilities: {Gems.CraftCapability} was not lowered: {e.Message}");
        }
    }

    internal static void Start()
    {
        lock (Gate) _tick ??= ServiceTick.Add("kiln.gemcraft", Pump);
    }

    internal static void Check(string api, EntityId player, EntityId forge)
    {
        Services.RequireTickThread(api);
        if (player.IsNone) throw new ArgumentException("no player", nameof(player));
        if (forge.IsNone) throw new ArgumentException("no forge: the game prices every forge action with a Shroud Forge", nameof(forge));
        if (!Capabilities.Is(Gems.CraftCapability)) throw ServiceCaps.Unavailable(Gems.CraftCapability, $"{api} cannot run");
        if (WhyOff() is { } why) throw new KilnUnavailableException(Gems.CraftCapability, $"{api}: {why}");
    }

    // The slot that holds item entity x now, by its entity id.
    private static InventorySlot? SlotOf(IReadOnlyList<InventorySlot> slots, ItemInstanceRef x) =>
        slots.FirstOrDefault(s => !s.IsEmpty && s.ItemEntity == x.Entity.Id);

    /// <summary>Why <paramref name="s"/> (the slot that holds <paramref name="x"/>'s entity id) is not the item
    /// <paramref name="x"/> names; null when it is. The ref names another item type, or carries another stamp: the item it
    /// named is gone and its entity id names a new one.</summary>
    internal static string? Mismatch(InventorySlot s, ItemInstanceRef x)
    {
        if (!x.Item.IsNone && s.Item != x.Item) return $"the item {x} is not there any more (that entity holds {s.Item} now)";
        if (x.Entity.Serial != 0 && !s.Ref.IsNone && s.Ref.Entity.Serial != 0 && s.Ref.Entity.Serial != x.Entity.Serial)
            return $"the item {x} no longer exists (a stale ref: its entity id names another item now)";
        return null;
    }

    /// <summary>The request for an action, from what the player's (or holder's) inventories hold now: the carrier by its
    /// entity, a loose gem by its slot. Null with a refusal when an item is not there, or its slot holds another item than
    /// the ref names (another type, or the same entity id with another stamp).</summary>
    /// <param name="action">What the player asked.</param>
    /// <param name="player">The player whose inventories are read.</param>
    /// <param name="first">The carrier (a gear item) the action works on.</param>
    /// <param name="gem">The gem the action socketed or takes out.</param>
    /// <param name="forge">The forge entity the player stands at.</param>
    /// <param name="options">The action's options, or null.</param>
    /// <param name="slots">The slots of the player's (or holder's) inventories now.</param>
    /// <param name="isGem">Whether an item is a gem.</param>
    /// <param name="isGear">Whether an item has an entity of its own (gear, gems): such an item is no replaced cost.</param>
    /// <exception cref="ArgumentException">The cost is negative, over 100000, or (but for Recycle) an item with an entity
    /// of its own.</exception>
    internal static (GemcraftNative.Request Request, GemCraftResult? Refusal) Build(GemCraftAction action, EntityId player,
        ItemInstanceRef first, ItemInstanceRef gem, EntityId forge, GemCraftOptions? options, IReadOnlyList<InventorySlot> slots,
        Func<ItemId, bool> isGem, Func<ItemId, bool>? isGear = null) =>
        Build(action, player, first, gem, forge, options, slots, slots, isGem, isGear);

    /// <summary><see cref="Build(GemCraftAction, EntityId, ItemInstanceRef, ItemInstanceRef, EntityId, GemCraftOptions?, IReadOnlyList{InventorySlot}, Func{ItemId, bool}, Func{ItemId, bool}?)"/>
    /// with the gem's holder's slots (<paramref name="gemSlots"/>) and the carrier's (<paramref name="carrierSlots"/>)
    /// apart (0.2.0-dev.29.15).</summary>
    /// <exception cref="ArgumentException">Also: a replaced cost (not free) with a payer other than the player.</exception>
    internal static (GemcraftNative.Request Request, GemCraftResult? Refusal) Build(GemCraftAction action, EntityId player,
        ItemInstanceRef first, ItemInstanceRef gem, EntityId forge, GemCraftOptions? options, IReadOnlyList<InventorySlot> gemSlots,
        IReadOnlyList<InventorySlot> carrierSlots, Func<ItemId, bool> isGem, Func<ItemId, bool>? isGear = null)
    {
        var pay = options?.PayFrom ?? GemPayer.Player;
        var r = new GemcraftNative.Request
        {
            Size = (uint)sizeof(GemcraftNative.Request),
            Action = (uint)action,
            Player = player.Value,
            Holder = options?.Holder.Value ?? 0,
            Forge = forge.Value,
            GemHolder = options?.GemHolder.Value ?? 0,
            CarrierHolder = options?.CarrierHolder.Value ?? 0,
            PayFrom = (uint)pay.Kind,
            PayHolder = pay.Kind == GemPayerKind.Container ? pay.Container.Value : 0,
            ReturnTo = options?.ReturnTo.Value ?? 0,
        };
        if (options?.Cost is { IsFree: false } && action != GemCraftAction.Recycle && pay.Kind != GemPayerKind.Player)
            throw new ArgumentException("a replaced cost is taken from the player: PayFrom applies to the game's own cost", nameof(options));
        if (options?.Cost is { } cost)
        {
            if (cost.Count < 0 || cost.Count > GemcraftNative.MaxCost) throw new ArgumentException("a cost of 0 to 100000 items", nameof(options));
            if (cost.IsFree) r.Flags = GemcraftNative.Free;
            else
            {
                // Kiln takes it by item id from anywhere in the inventory: an item with an entity of its own would be
                // destroyed (it may be the action's own carrier or gem) and come back new from a refund
                if (action != GemCraftAction.Recycle && isGear?.Invoke(cost.Item) == true)
                    throw new ArgumentException("a replaced cost must be a plain item (not gear or a gem with an entity of its own)", nameof(options));
                r.Flags = GemcraftNative.Cost;
                r.CostItem = cost.Item.Value;
                r.CostCount = (uint)cost.Count;
            }
        }
        GemCraftResult Refuse(string why) => new(GemCraftStatus.NotFound, action, player, forge, first, gem, 0, 0, default, default, why);
        // a carrier found in the slots must be the item the ref names; one not found there is checked by kiln.gemcraft
        string? CarrierWrong(ItemInstanceRef x) => SlotOf(carrierSlots, x) is { } c ? Mismatch(c, x) : null;
        switch (action)
        {
            case GemCraftAction.Socket:
            {
                r.Carrier = first.Entity.Id.Value;
                if (CarrierWrong(first) is { } cw) return (r, Refuse(cw));
                if (SlotOf(gemSlots, gem) is not { } g) return (r, Refuse("the gem is not in the player's (or the holder's) inventories"));
                if (Mismatch(g, gem) is { } gw) return (r, Refuse(gw));
                if (!isGem(g.Item)) return (r, Refuse("the item to socket is not a gem"));
                r.GemSlot = g.Slot.Value;
                r.Gem = gem.Entity.Id.Value;
                break;
            }
            case GemCraftAction.Unsocket:
                if (CarrierWrong(first) is { } uw) return (r, Refuse(uw));
                r.Carrier = first.Entity.Id.Value;
                break;
            default:
            {
                // a loose gem by its slot (in the gem's holder), a carrier by its entity (in the carrier's holder)
                var s = SlotOf(gemSlots, first);
                if (s is not null && Mismatch(s, first) is { } fw) return (r, Refuse(fw));
                if (s is not null && isGem(s.Item))
                {
                    r.GemSlot = s.Slot.Value;
                    r.Gem = first.Entity.Id.Value;
                }
                else
                {
                    if (CarrierWrong(first) is { } cw2) return (r, Refuse(cw2));
                    r.Carrier = first.Entity.Id.Value;
                }
                break;
            }
        }
        return (r, null);
    }

    internal static Task<GemCraftResult> Submit(GemCraftAction action, EntityId player, ItemInstanceRef first, ItemInstanceRef gem,
        EntityId forge, GemCraftOptions? options, string api)
    {
        var mod = ModContext.CurrentMod;
        if (mod is { Alive: false }) throw new InvalidOperationException("the mod is unloading");
        var holder = options?.Holder is { IsNone: false } h ? h : player;
        var gemHolder = options?.GemHolder is { IsNone: false } gh ? gh : holder;
        var carrierHolder = options?.CarrierHolder is { IsNone: false } ch ? ch : holder;
        var gemSlots = Items.Read(gemHolder)?.Slots ?? [];
        var carrierSlots = carrierHolder == gemHolder ? gemSlots : Items.Read(carrierHolder)?.Slots ?? [];
        var (r, refusal) = Build(action, player, first, gem, forge, options, gemSlots, carrierSlots,
            id => Items.Get(id)?.Category == ItemCategory.WeaponGem, id => Items.Get(id)?.IsGear == true);
        if (refusal is not null) return Task.FromResult(refusal);
        if (Routed(r) && !Capabilities.Is(Gems.PayersCapability))
            throw ServiceCaps.Unavailable(Gems.PayersCapability, $"{api}: PayFrom or ReturnTo other than the player cannot run");
        // an item ref whose entity is gone, or whose id names another spawn now (found nowhere in the slots above)
        foreach (var x in new[] { first, gem })
            if (!x.IsNone && Alive(x) == false)
                return Task.FromResult(new GemCraftResult(GemCraftStatus.NotFound, action, player, forge, first, gem, 0, 0, default, default,
                    $"the item {x} no longer exists (a stale ref)"));
        return Send(r, action, player, first, gem, forge, mod, api);
    }

    /// <summary>Whether a request needs <c>gems.payers</c>: a payer other than the player for a cost the game charges,
    /// or a return other than the player (gems_logic.h request_routed).</summary>
    internal static bool Routed(in GemcraftNative.Request r)
    {
        var pays = r.Action != (uint)GemCraftAction.Recycle && (r.Flags & (GemcraftNative.Free | GemcraftNative.Cost)) == 0;
        return (pays && r.PayFrom != 0) || (r.ReturnTo != 0 && r.ReturnTo != r.Player);
    }

    // The ref's entity is the spawn it captured; null when Kiln cannot tell here (no entity source).
    private static bool? Alive(ItemInstanceRef x)
    {
        try
        {
            return x.IsAlive;
        }
        catch (KilnUnavailableException)
        {
            return null;
        }
    }

    /// <summary>The forgeless return's request (kiln.gemcraft KILN_GEM_RETURN): the carrier must be in the owner's own
    /// inventories (worn included) and be the item the ref names.</summary>
    internal static (GemcraftNative.Request Request, string? Refusal) BuildReturn(EntityId owner, ItemInstanceRef carrier,
        IReadOnlyList<InventorySlot> slots)
    {
        var r = new GemcraftNative.Request
        {
            Size = (uint)sizeof(GemcraftNative.Request),
            Action = (uint)GemCraftAction.Unsocket,
            Flags = GemcraftNative.Return,
            Player = owner.Value,
            Carrier = carrier.Entity.Id.Value,
        };
        if (SlotOf(slots, carrier) is not { } s) return (r, "the carrier is not in the owner's inventories");
        return (r, Mismatch(s, carrier));
    }

    /// <summary>
    /// Gem wave G4's uninstall path (<c>Gems.ReturnSocketedGems</c>): takes the gem out of <paramref name="carrier"/>'s
    /// socket into <paramref name="owner"/>'s own inventory without a forge and free of charge. kiln.gemcraft runs the
    /// game's own unslot move (the forge Unslot's) on a transaction it sets up as the forge's handler does, inside the
    /// owner's update of kiln_items_gems, and checks the socket and the backpack afterwards (KILN_GEM_RETURN).
    /// </summary>
    /// <param name="owner">A player character (the entity kiln_items_gems runs for); a chest or any other holder is
    /// refused: move the items into the player's inventory first.</param>
    /// <param name="carrier">An item in the owner's inventories (backpack, action bars, worn) whose socket holds a gem; any
    /// carrier, a weapon included.</param>
    /// <returns>Never throws: (true, null) when the gem is in the owner's inventory now and the socket empty; otherwise
    /// (false, why) and nothing moved (<c>Occupied</c>: the socket is empty; <c>NoRoom</c>: no free backpack slot;
    /// <c>Unavailable</c>, <c>Dropped</c>, a stale or foreign carrier, gems.craft off, not the tick thread). Completes on
    /// the tick thread one step after the step it ran in, or at once for a refusal Kiln sees before. A
    /// <see cref="GemCraftedEvent"/> (Unsocket, by code, no forge) follows a return that happened.</returns>
    [ServerOnly, TickThreadOnly]
    internal static Task<(bool Ok, string? Reason)> UnsocketWithoutForge(EntityId owner, ItemInstanceRef carrier) =>
        UnsocketWithoutForge(owner, carrier, null, IsPlayerCharacter);

    /// <summary>Whether an entity is a player character (kiln_items_gems iterates those: PlayerInput); null when Kiln
    /// cannot tell here (no kiln.world).</summary>
    private static bool? IsPlayerCharacter(EntityId e) => World.Available ? World.Has(e, "PlayerInput") : null;

    /// <summary><see cref="UnsocketWithoutForge(EntityId, ItemInstanceRef)"/> with the owner's slots and the player test
    /// given (tests); <paramref name="slots"/> null: read now.</summary>
    internal static Task<(bool Ok, string? Reason)> UnsocketWithoutForge(EntityId owner, ItemInstanceRef carrier,
        IReadOnlyList<InventorySlot>? slots, Func<EntityId, bool?> isPlayer)
    {
        static Task<(bool Ok, string? Reason)> No(string why) => Task.FromResult<(bool Ok, string? Reason)>((false, why));
        try
        {
            if (owner.IsNone) return No("no owner");
            if (carrier.IsNone) return No("no carrier");
            Services.RequireTickThread("Gems.ReturnSocketedGems");
            if (!Capabilities.Is(Gems.CraftCapability)) return No($"{Gems.CraftCapability} is off: {Capabilities.Why(Gems.CraftCapability)}");
            if (WhyOff() is { } off) return No(off);
            if (isPlayer(owner) == false)
                return No("the owner is not a player character: gems go back into a player's own inventory (move the items there first)");
            var mod = ModContext.CurrentMod;
            if (mod is { Alive: false }) return No("the mod is unloading");
            var (r, refusal) = BuildReturn(owner, carrier, slots ?? Items.Read(owner)?.Slots ?? []);
            if (refusal is not null) return No(refusal);
            if (Alive(carrier) == false) return No($"the item {carrier} no longer exists (a stale ref)");
            return Send(r, GemCraftAction.Unsocket, owner, carrier, default, EntityId.None, mod, "Gems.ReturnSocketedGems")
                .ContinueWith(t => t.IsCompletedSuccessfully ? (t.Result.Ok, t.Result.Reason)
                        : (false, t.Exception?.GetBaseException().Message ?? "canceled"),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception e) when (e is KilnUnavailableException or InvalidOperationException or ArgumentException)
        {
            return No(e.Message);
        }
    }

    // Queues a built request; its task completes from the native result.
    private static Task<GemCraftResult> Send(in GemcraftNative.Request r, GemCraftAction action, EntityId player, ItemInstanceRef first,
        ItemInstanceRef gem, EntityId forge, ModContext? mod, string api)
    {
        var tcs = new TaskCompletionSource<GemCraftResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        long key;
        lock (Gate)
        {
            key = ++_next;
            InFlight[key] = (tcs, mod);
        }
        Start();
        var rc = _backend.Submit(r, key, out _);
        if (rc == GemcraftNative.Ok) return tcs.Task;
        lock (Gate) InFlight.Remove(key);
        if (rc == GemcraftNative.BufferTooSmall)
            return Task.FromResult(new GemCraftResult(GemCraftStatus.Refused, action, player, forge, first, gem, 0, 0, default, default,
                "256 forge actions are waiting: try again in a step"));
        if (rc == GemcraftNative.InvalidArgument) throw new ArgumentException($"{api}: kiln.gemcraft refused the request's arguments");
        throw new KilnUnavailableException(Gems.CraftCapability, _backend.Status()?.Detail is { Length: > 0 } d ? d : $"kiln.gemcraft refused it (error {rc})");
    }

    // ---- results ----

    internal static GemCost CostOf(uint item, uint count) => item == 0 || count == 0 ? default : new GemCost(new ItemId(item), (int)count);

    internal static string Message(in GemcraftNative.Craft c)
    {
        fixed (byte* m = c.Message) return GemcraftNative.Text(m, 128);
    }

    /// <summary>A native record as a result; the item refs from what <paramref name="slots"/> hold (with their stamps) when
    /// found there, else by id alone.</summary>
    internal static GemCraftResult ResultOf(in GemcraftNative.Craft c, IReadOnlyList<InventorySlot>? slots = null)
    {
        ItemInstanceRef Ref(uint pide, uint item)
        {
            if (pide == 0) return ItemInstanceRef.None;
            if (slots?.FirstOrDefault(s => s.ItemEntity.Value == pide) is { Ref.IsNone: false } s) return s.Ref;
            return new ItemInstanceRef(new EntityRef(new EntityId(pide), 0), new ItemId(item));
        }
        var status = Enum.IsDefined(typeof(GemCraftStatus), c.Status) ? (GemCraftStatus)c.Status : GemCraftStatus.Refused;
        return new GemCraftResult(status, (GemCraftAction)c.Action, new EntityId(c.Player), new EntityId(c.Forge),
            Ref(c.Carrier, c.CarrierItem), Ref(c.Gem, c.GemItem), (int)c.LevelBefore, (int)c.LevelAfter,
            CostOf(c.CostItem, c.CostCount), CostOf(c.YieldItem, c.YieldCount), Message(c));
    }

    internal static GemCraftingEvent EventOf(in GemcraftNative.Craft c, IReadOnlyList<InventorySlot>? slots = null)
    {
        var r = ResultOf(c, slots);
        return new GemCraftingEvent(r.Player, r.Forge, r.Action, r.Carrier, r.Gem, r.LevelBefore, r.Paid, r.Received, c.Version);
    }

    /// <summary>A code action finished (its native callback, or a test).</summary>
    internal static void Complete(long key, in GemcraftNative.Craft c)
    {
        (TaskCompletionSource<GemCraftResult> Tcs, ModContext? Mod) entry;
        lock (Gate)
        {
            if (!InFlight.Remove(key, out entry)) return;
        }
        if (entry.Mod is { Alive: false }) return;  // the asking mod is gone: its continuation is its own code
        entry.Tcs.TrySetResult(ResultOf(c, SlotsOf(new EntityId(c.Player))));
    }

    private static IReadOnlyList<InventorySlot>? SlotsOf(EntityId player)
    {
        try
        {
            return Items.Read(player)?.Slots;
        }
        catch (Exception e) when (e is KilnUnavailableException or InvalidOperationException)
        {
            return null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnDone(GemcraftNative.Craft* c, void* user)
    {
        try
        {
            Complete((long)user, *c);
        }
        catch
        {
            // never across into native code
        }
    }

    // ---- the pump: held players' actions (GemCraftingEvent), what ran (GemCraftedEvent) ----

    internal static void Pump(in TickInfo tick) => PumpOnce();

    internal static void PumpOnce()
    {
        GemSources.PumpServer();
        var crafting = Events.HasSubscribers<GemCraftingEvent>();
        var crafted = Events.HasSubscribers<GemCraftedEvent>();
        if (crafting) _holding = _backend.Hold(HoldSteps) == GemcraftNative.Ok;
        else if (_holding)
        {
            _backend.Hold(0);  // what is held runs
            _holding = false;
        }
        if (crafted != _watching) _watching = _backend.Watch(crafted) == GemcraftNative.Ok && crafted;
        Span<GemcraftNative.Craft> buf = stackalloc GemcraftNative.Craft[Batch];
        // held: one event each, the answer right away
        for (var round = 0; round < 64; round++)
        {
            for (var i = 0; i < Batch; i++) buf[i].Size = (uint)sizeof(GemcraftNative.Craft);
            var rc = _backend.Held(buf, out var n);
            for (var i = 0; i < (int)n && i < Batch; i++)
            {
                var cancel = false;
                if (crafting)
                {
                    var ev = Events.Post(EventOf(buf[i], SlotsOf(new EntityId(buf[i].Player))));
                    cancel = ev.Canceled;
                }
                _backend.Decide(buf[i].Player, buf[i].Version, cancel);
            }
            if (rc != GemcraftNative.BufferTooSmall || n == 0) break;
        }
        // crafted: players' (while watched) and code's
        for (var round = 0; round < 64; round++)
        {
            for (var i = 0; i < Batch; i++) buf[i].Size = (uint)sizeof(GemcraftNative.Craft);
            var rc = _backend.Crafted(buf, out var n);
            for (var i = 0; i < (int)n && i < Batch; i++)
            {
                if (crafted && buf[i].Status == 0)
                    Events.Post(new GemCraftedEvent(ResultOf(buf[i], SlotsOf(new EntityId(buf[i].Player))), (buf[i].Flags & GemcraftNative.ByCode) != 0));
                if ((buf[i].Flags & GemcraftNative.SourceFlag) != 0) GemSources.OnSourceResult(buf[i]);  // the player's client is told
            }
            if (rc != GemcraftNative.BufferTooSmall || n == 0) break;
        }
    }
}
