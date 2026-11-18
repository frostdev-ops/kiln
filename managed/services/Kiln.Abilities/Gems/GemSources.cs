// SPDX-License-Identifier: MIT
// GitHub issue 61 (0.2.0-dev.29.15): gems a player may use from chests at the Shroud Forge, in the game's own GEMS grid,
// and who pays. A server mod decides which containers a player may use (GemForgeOpenedEvent, then Gems.OfferChestGems);
// Kiln's native side (kiln.gemcraft) mints a short-lived token per offered gem and later finds it again from the game's
// own request, which names the gem's real slot; Kiln's channel "kiln:gemsources" (required: false) carries the offers to
// the player's Kiln client, which lists them in the GEMS grid (kiln.ui.values gemforge_sources, ui.gemforge.sources).
// Nothing a client sends is authority: the client only says "my forge screen is open" (a material list on the wire is
// ignored). docs/kiln/services/gems-forge.md ("Chest gems"), docs/kiln/ui-gemforge.md.
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Kiln;

/// <summary>Who pays a forge action's cost (<see cref="GemPayer"/>).</summary>
public enum GemPayerKind : uint
{
    /// <summary>The player's own inventories, as the game does.</summary>
    Player = 0,
    /// <summary>One container (<see cref="GemPayer.Container"/>).</summary>
    Container = 1,
    /// <summary>The crafting-stock containers (the Magic chests) of the base the player stands in, in the game's order.</summary>
    BaseStorage = 2,
    /// <summary>The player's inventories first, then the base's crafting-stock containers (the game's crafting order).</summary>
    PlayerThenBaseStorage = 3,
}

/// <summary>Who pays a forge action's cost (<see cref="GemCraftOptions.PayFrom"/>, <see cref="GemSourceOptions.PayFrom"/>):
/// the player (the default), a container your mod vouches for, or the base's Magic chests as the game's crafting from
/// base storage uses them (the player must stand in the base and have the server's CanAccessInventories right; outside
/// a base the payment is <see cref="GemCraftStatus.NotEnough"/>). Since 0.2.0-dev.29.15.</summary>
public readonly record struct GemPayer
{
    private GemPayer(GemPayerKind kind, EntityId container)
    {
        Kind = kind;
        Container = container;
    }

    /// <summary>Which payer.</summary>
    public GemPayerKind Kind { get; }

    /// <summary>The paying container (<see cref="GemPayerKind.Container"/>); none otherwise.</summary>
    public EntityId Container { get; }

    /// <summary>The player pays, as the game does (the default).</summary>
    public static GemPayer Player => default;

    /// <summary>The base's crafting-stock containers pay.</summary>
    public static GemPayer BaseStorage => new(GemPayerKind.BaseStorage, default);

    /// <summary>The player pays what it can, the base's crafting-stock containers the rest.</summary>
    public static GemPayer PlayerThenBaseStorage => new(GemPayerKind.PlayerThenBaseStorage, default);

    /// <summary>One container pays (a chest your mod vouches for).</summary>
    /// <exception cref="ArgumentException">No container.</exception>
    public static GemPayer From(EntityId container) =>
        container.IsNone ? throw new ArgumentException("no container", nameof(container)) : new(GemPayerKind.Container, container);

    /// <summary>True for the player (the game's own payment).</summary>
    public bool IsPlayer => Kind == GemPayerKind.Player;

    /// <summary><c>"player"</c>, <c>"container 1234"</c>, <c>"base storage"</c>, <c>"player then base storage"</c>.</summary>
    public override string ToString() => Kind switch
    {
        GemPayerKind.Container => $"container {Container}",
        GemPayerKind.BaseStorage => "base storage",
        GemPayerKind.PlayerThenBaseStorage => "player then base storage",
        _ => "player",
    };
}

/// <summary>Options of <see cref="Gems.OfferChestGems"/>.</summary>
public sealed record GemSourceOptions
{
    /// <summary>Who pays when the player inserts or upgrades an offered gem (default: the player).</summary>
    public GemPayer PayFrom { get; init; }

    /// <summary>Where Recycle's essence goes when the player recycles an offered gem: none (the default) for the player,
    /// else a container your mod vouches for (the chest itself, for example).</summary>
    public EntityId ReturnTo { get; init; }

    /// <summary>How long the offer is good for, real time (default 30 s; 1 ms to 120 s). <see cref="GemForgeOpenedEvent"/>
    /// comes again every 5 s while the player's forge screen stays open: offer again there.</summary>
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>One gem an offer listed: its token (Kiln's, unguessable), the gem, its slot and level when offered.</summary>
public sealed record OfferedGem(ulong Token, ItemInstanceRef Gem, SlotId Slot, int Level);

/// <summary>What <see cref="Gems.OfferChestGems"/> did.</summary>
/// <param name="Player">The player.</param>
/// <param name="Holder">The container.</param>
/// <param name="Gems">The gems offered (loose gems of the container, at most 256 per player).</param>
/// <param name="Refusal">Why nothing was offered; null when it worked (an empty list is fine: no gems there).</param>
public sealed record GemSourceOffer(EntityId Player, EntityId Holder, IReadOnlyList<OfferedGem> Gems, string? Refusal)
{
    /// <summary>True when the offer was taken (possibly of no gems).</summary>
    public bool Ok => Refusal is null;
}

/// <summary>
/// A player's game has the Shroud Forge screen open (a Kiln client told the server): posted when it opens, every 5 s while
/// it stays open, and right after the player used an offered gem. Offer the gems the player may use from your containers
/// here (<see cref="Gems.OfferChestGems"/>); the offers reach the player's GEMS grid at once. When the screen closes, every
/// offer to the player is withdrawn. Server, tick thread. Capability <c>gems.sources</c>. Since 0.2.0-dev.29.15.
/// </summary>
/// <remarks>The client says only that the screen is open: who may use which container is your mod's decision (the
/// player's base, a permission, a distance). Kiln finds <see cref="Forge"/> itself: the nearest Shroud Forge within 12 m
/// of the player, or none.</remarks>
[ServerOnly, TickThreadOnly]
public sealed class GemForgeOpenedEvent : KilnEvent, IEntityEvent
{
    internal GemForgeOpenedEvent(EntityId player, EntityId forge, bool refresh)
    {
        Player = player;
        Forge = forge;
        Refresh = refresh;
    }

    /// <summary>The player.</summary>
    public EntityId Player { get; }

    /// <summary>The nearest Shroud Forge within 12 m of the player; none when Kiln found none.</summary>
    public EntityId Forge { get; }

    /// <summary>False when the screen just opened; true for the 5-second refresh and after an action on an offered gem.</summary>
    public bool Refresh { get; }

    /// <summary>Whether the event is about <paramref name="entity"/>: the player or the forge.</summary>
    public bool Involves(EntityId entity) => !entity.IsNone && (entity == Player || entity == Forge);

    /// <summary>The event in one line, for a log.</summary>
    public override string ToString() => $"gem forge {(Refresh ? "still open" : "opened")}: player {Player} at forge {Forge}";
}

public static partial class Gems
{
    /// <summary>The capability row of the offered chest gems (<see cref="OfferChestGems"/>, <see cref="GemForgeOpenedEvent"/>).</summary>
    public const string SourcesCapability = "gems.sources";

    /// <summary>The capability row of payers and returns other than the player (<see cref="GemCraftOptions.PayFrom"/>,
    /// <see cref="GemCraftOptions.ReturnTo"/>, <see cref="GemSourceOptions"/>): a sigdb feature, the handler's own calls
    /// Kiln moves.</summary>
    public const string PayersCapability = "gems.payers";

    /// <summary>
    /// Offers the loose gems in <paramref name="holder"/> (a container your mod lets the player use) to
    /// <paramref name="player"/> at the Shroud Forge: on the player's Kiln client they appear in the game's own GEMS grid,
    /// after the player's own gems, drawn as the game draws a gem. When the player inserts, upgrades or recycles one, the
    /// game's own request names the gem's real slot; Kiln finds the offer by player and slot, checks that the slot still
    /// holds that gem at that level, and runs the game's action with the gem in the container and the carrier in the
    /// player's inventories in one transaction, the cost paid by <see cref="GemSourceOptions.PayFrom"/>. A request naming
    /// a container's gem without a live offer to that player is refused: nothing moves.
    /// </summary>
    /// <param name="player">The player character.</param>
    /// <param name="holder">A container (a chest; not a player or an NPC) whose loose gems the player may use now.</param>
    /// <param name="options">Who pays, where Recycle's essence goes, how long the offer is good for.</param>
    /// <returns>The gems offered with their tokens, or a refusal (<see cref="GemSourceOffer.Refusal"/>) and nothing offered.</returns>
    /// <exception cref="ArgumentException">No player or holder, the player as the holder, or a lifetime outside 1 ms to 120 s.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or the calling mod is unloading.</exception>
    /// <exception cref="KilnUnavailableException"><c>gems.sources</c> is off, or a payer or return other than the player
    /// while <c>gems.payers</c> is off.</exception>
    /// <example><code>
    /// Events.On&lt;GemForgeOpenedEvent&gt;(e =&gt;
    /// {
    ///     foreach (var chest in MyChestsFor(e.Player))
    ///         Gems.OfferChestGems(e.Player, chest, new GemSourceOptions { PayFrom = GemPayer.From(chest), ReturnTo = chest });
    /// });
    /// </code></example>
    [ServerOnly, TickThreadOnly, RequiresCapability(SourcesCapability)]
    public static GemSourceOffer OfferChestGems(EntityId player, EntityId holder, GemSourceOptions? options = null)
    {
        Services.RequireTickThread("Gems.OfferChestGems");
        if (!Capabilities.Is(SourcesCapability)) throw ServiceCaps.Unavailable(SourcesCapability, "Gems.OfferChestGems cannot run");
        return GemSources.Offer(player, holder, options ?? new GemSourceOptions(), null, null);
    }

    /// <summary>Withdraws your mod's offers to <paramref name="player"/> in <paramref name="holder"/> (none: every container).
    /// Returns how many went. Offers also lapse by themselves, go when the player's forge screen closes, and go with your
    /// mod.</summary>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <exception cref="KilnUnavailableException"><c>gems.sources</c> is off.</exception>
    [ServerOnly, TickThreadOnly, RequiresCapability(SourcesCapability)]
    public static int WithdrawChestGems(EntityId player, EntityId holder = default) => GemSources.Withdraw(player, holder);
}

// ---- the wire (channel "kiln:gemsources" v1) ----

/// <summary>Client to server: the forge screen is open (or closed). <c>Wants</c> (at most 8 materials) is kept on the wire
/// but ignored by the server since the review of 2026-11-17 (the counts are of the forge's own upgrade material); Kiln's
/// client sends none.</summary>
internal sealed record GemForgeHello(bool Open, uint[] Wants)
{
    public const ushort Id = 1;
    public static void Encode(GemForgeHello m, NetWriter w)
    {
        w.Bool(m.Open).Byte((byte)Math.Min(m.Wants.Length, 8));
        for (var i = 0; i < m.Wants.Length && i < 8; i++) w.UInt32(m.Wants[i]);
    }
    public static GemForgeHello Decode(ref NetReader r)
    {
        var open = r.Bool();
        var n = Math.Min((int)r.Byte(), 8);
        var w = new uint[n];
        for (var i = 0; i < n; i++) w[i] = r.UInt32();
        return new GemForgeHello(open, w);
    }
}

/// <summary>Server to client: the offered rows and what each payer group holds of the wanted materials.</summary>
internal sealed record GemForgeOffers(GemForgeOfferRow[] Rows, GemForgeOfferPayer[] Payers)
{
    public const ushort Id = 2;
    public static void Encode(GemForgeOffers m, NetWriter w)
    {
        w.UInt16((ushort)Math.Min(m.Rows.Length, 256));
        for (var i = 0; i < m.Rows.Length && i < 256; i++)
        {
            var x = m.Rows[i];
            w.UInt32(x.Page).Byte(x.Slot).UInt32(x.Item).UInt32(x.Pide).UInt32(x.Level).Byte(x.Rarity).Byte(x.Group).UInt64(x.Token).UInt32(x.LifetimeMs);
        }
        w.Byte((byte)Math.Min(m.Payers.Length, 64));
        for (var i = 0; i < m.Payers.Length && i < 64; i++)
        {
            var p = m.Payers[i];
            w.Byte(p.Group).Bool(p.PlayerFirst).UInt32(p.Item).UInt32(p.Count);
        }
    }
    public static GemForgeOffers Decode(ref NetReader r)
    {
        var n = Math.Min((int)r.UInt16(), 256);
        var rows = new GemForgeOfferRow[n];
        for (var i = 0; i < n; i++)
            rows[i] = new GemForgeOfferRow(r.UInt32(), r.Byte(), r.UInt32(), r.UInt32(), r.UInt32(), r.Byte(), r.Byte(), r.UInt64(), r.UInt32());
        var k = Math.Min((int)r.Byte(), 64);
        var payers = new GemForgeOfferPayer[k];
        for (var i = 0; i < k; i++) payers[i] = new GemForgeOfferPayer(r.Byte(), r.Bool(), r.UInt32(), r.UInt32());
        return new GemForgeOffers(rows, payers);
    }
}

/// <summary>One offered row on the wire: the slot (page, index), the gem, its level and rarity, its payer group, the
/// token and how long the row lives (ms from now).</summary>
internal readonly record struct GemForgeOfferRow(uint Page, byte Slot, uint Item, uint Pide, uint Level, byte Rarity, byte Group,
    ulong Token, uint LifetimeMs);

/// <summary>What one payer group holds of one material, on the wire.</summary>
internal readonly record struct GemForgeOfferPayer(byte Group, bool PlayerFirst, uint Item, uint Count);

/// <summary>Server to client: how an action on an offered gem ended (the forge screen shows nothing for a refusal).</summary>
internal sealed record GemForgeOutcome(ulong Token, int Status, string Message)
{
    public const ushort Id = 3;
    public static void Encode(GemForgeOutcome m, NetWriter w) => w.UInt64(m.Token).Int32(m.Status).String(m.Message);
    public static GemForgeOutcome Decode(ref NetReader r) => new(r.UInt64(), r.Int32(), r.String(256));
}

/// <summary>Both halves of the offered chest gems: the server's offers and forge sessions, the client's screen watch.</summary>
internal static class GemSources
{
    public const string ChannelName = "kiln:gemsources";
    public const string ChannelVersion = "1";
    internal const int MaxLifetimeMs = 120_000;
    internal static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(5);
    private const long PostEveryMs = 1000;  // a player's hellos post GemForgeOpenedEvent at most once a second
    private const int MaterialTries = 250;   // pumps a forge's material is asked for before the counts are left out
    private const double ForgeRadius = 12;

    private static readonly object Gate = new();
    private static NetChannel? _channel;
    private static bool _started, _clientStarted, _playersWatched;

    // server: the open forge screens (player -> its forge) and players whose offers changed this tick
    private sealed class Session
    {
        public EntityId Forge;
        public bool Posted;        // the opening was posted (later posts are refreshes)
        public bool PostDue;       // a hello inside the player's second: the pump posts once the second is over
        public uint Material;      // the forge's upgrade material (what containers pay in); 0 not known
        public int MaterialAsks;   // pumps that asked kiln.gemcraft for it
    }
    private static readonly Dictionary<uint, Session> Sessions = new();
    private static readonly Dictionary<uint, long> LastPost = new();  // player -> ms of the last post (kept across a close)
    private static readonly HashSet<uint> Dirty = new();
    private static readonly ConcurrentQueue<(EntityId Player, GemForgeHello Hello)> Inbox = new();
    private static readonly ConditionalWeakTable<ModContext, StrongBox<ulong>> Owners = new();
    private static long _nextOwner = 1;  // 1: Kiln's own (no mod); 0 means every owner to kiln.gemcraft

    /// <summary>Real time in ms: the clock kiln.gemcraft's expiries use (GetTickCount64). Tests set their own.</summary>
    internal static Func<long> Clock = static () => Environment.TickCount64;
    internal static long NowMs() => Clock();

    /// <summary>Test hook: what the server sent (player, message) instead of the channel; null: the channel.</summary>
    internal static Action<EntityId, object>? SendHook;

    /// <summary>Test hook: the forge near a player; null: the world's nearest Shroud Forge.</summary>
    internal static Func<EntityId, EntityId>? ForgeNearHook;

    /// <summary>Kiln.Abilities' start (both sides): the channel, and on a game client the forge screen's watch.</summary>
    internal static void Start()
    {
        lock (Gate)
        {
            if (_started) return;
            _started = true;
            if (!Network.Available) return;
            var outer = ModContext.CurrentMod;
            ModContext.CurrentMod = null;  // Kiln's own channel, not a mod's
            try
            {
                _channel = Network.Channel(ChannelName, ChannelVersion, required: false)
                    .Register<GemForgeHello>(GemForgeHello.Id, static (m, w) => GemForgeHello.Encode(m, w),
                        static (ref NetReader r) => GemForgeHello.Decode(ref r), static (m, ctx) => { Hello(ctx.Player, m); GemCrafting.Start(); }, NetDirection.ToServer)
                    .Register<GemForgeOffers>(GemForgeOffers.Id, static (m, w) => GemForgeOffers.Encode(m, w),
                        static (ref NetReader r) => GemForgeOffers.Decode(ref r), static (m, _) => { if (!Client.IsHosting) ClientOffers(m); }, NetDirection.ToClient)
                    .Register<GemForgeOutcome>(GemForgeOutcome.Id, static (m, w) => GemForgeOutcome.Encode(m, w),
                        static (ref NetReader r) => GemForgeOutcome.Decode(ref r), static (m, _) => { if (!Client.IsHosting) ClientOutcome(m); }, NetDirection.ToClient);
            }
            catch (Exception e)
            {
                ModLog.Warn($"kiln.abilities: the channel {ChannelName} is unavailable: {e.Message}");
            }
            finally
            {
                ModContext.CurrentMod = outer;
            }
        }
        StartClient();
    }

    // ================================================================ server ====

    private static ulong OwnerOf(ModContext? mod)
    {
        if (mod is null) return 1;
        return Owners.GetValue(mod, m =>
        {
            var box = new StrongBox<ulong>((ulong)Interlocked.Increment(ref _nextOwner));
            m.Retiring += () => Retire(box.Value);
            return box;
        }).Value;
    }

    private static void Retire(ulong owner)
    {
        try
        {
            GemCrafting.Backend.Withdraw(owner, 0, 0, out _);
            lock (Gate)
                foreach (var s in Sessions.Keys) Dirty.Add(s);
        }
        catch (Exception e) when (e is KilnUnavailableException or InvalidOperationException)
        {
            // the offers lapse by themselves
        }
    }

    // The server's watch of players leaving and joining (a player entity is the slot + 1: the next player in the slot
    // must never find the last one's session or offers). Started with the first hello or offer.
    private static void WatchPlayers()
    {
        lock (Gate)
        {
            if (_playersWatched) return;
            _playersWatched = true;
        }
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = null;  // Kiln's own subscription
        try
        {
            Events.On<PlayerLeftEvent>(static e => PlayerGone(e.Player.Entity));
            Events.On<PlayerJoinedEvent>(static e => PlayerGone(e.Player.Entity));
        }
        catch (Exception e) when (e is KilnUnavailableException or InvalidOperationException or TypeInitializationException or FileNotFoundException)
        {
            // kiln.players is not here: the offers stay bound to the session natively (kiln.gemcraft checks it)
            ModLog.Warn($"kiln.abilities: chest gems do not see players leave ({e.Message}); offers still end with the player's session");
        }
        finally
        {
            ModContext.CurrentMod = outer;
        }
    }

    /// <summary>A player left, or a player joined into the slot (tick thread): the forge session, its rate limit and every
    /// owner's offers to the entity go.</summary>
    internal static void PlayerGone(EntityId player)
    {
        if (player.IsNone) return;
        lock (Gate)
        {
            Sessions.Remove(player.Value);
            LastPost.Remove(player.Value);
            Dirty.Remove(player.Value);
        }
        try
        {
            GemCrafting.Backend.Withdraw(0, player.Value, 0, out _);
        }
        catch (Exception e) when (e is KilnUnavailableException or InvalidOperationException)
        {
        }
    }

    /// <summary>Why offers cannot be made here; null: they can.</summary>
    internal static string? WhyOff()
    {
        if (GemCrafting.Backend.Extras() is not { } x)
            return "kiln.gemcraft predates gem sources (an older kiln-items.dll), or is not here (no server world in this process)";
        if ((x.Flags & GemcraftNative.HasSources) == 0) return x.Why.Length > 0 ? x.Why : "kiln.gemcraft reports gem sources off";
        return null;
    }

    internal static int LifetimeMs(TimeSpan t)
    {
        var ms = Math.Round(t.TotalMilliseconds);
        if (double.IsNaN(ms) || ms < 1 || ms > MaxLifetimeMs)
            throw new ArgumentException("a lifetime of 1 ms to 120 s", nameof(t));
        return (int)ms;
    }

    /// <summary>The native offer entries for the loose gems of <paramref name="slots"/> (a gem item, one of it, with its
    /// own item entity). Pure (tests).</summary>
    internal static List<GemcraftNative.Source> Entries(EntityId player, EntityId holder, IReadOnlyList<InventorySlot> slots,
        GemSourceOptions o, int lifetimeMs, Func<ItemId, bool> isGem)
    {
        var list = new List<GemcraftNative.Source>();
        foreach (var s in slots)
        {
            if (s.IsEmpty || s.Count != 1 || s.ItemEntity.IsNone || !isGem(s.Item)) continue;
            if (list.Count >= 256) break;
            list.Add(new GemcraftNative.Source
            {
                Size = (uint)Unsafe.SizeOf<GemcraftNative.Source>(), Player = player.Value, Holder = holder.Value, Slot = s.Slot.Value,
                Gem = s.ItemEntity.Value, Item = s.Item.Value, Level = (uint)Math.Max(0, s.Level), PayFrom = (uint)o.PayFrom.Kind,
                PayHolder = o.PayFrom.Kind == GemPayerKind.Container ? o.PayFrom.Container.Value : 0, ReturnTo = o.ReturnTo.Value,
                LifetimeMs = (uint)lifetimeMs, Rarity = s.Rarity is { } r ? (uint)r : 0,
            });
        }
        return list;
    }

    /// <summary>Gems.OfferChestGems; <paramref name="contents"/> and <paramref name="isGem"/> given by tests.</summary>
    internal static GemSourceOffer Offer(EntityId player, EntityId holder, GemSourceOptions o, InventoryContents? contents,
        Func<ItemId, bool>? isGem)
    {
        Services.RequireTickThread("Gems.OfferChestGems");
        if (player.IsNone) throw new ArgumentException("no player", nameof(player));
        if (holder.IsNone) throw new ArgumentException("no holder", nameof(holder));
        if (holder == player) throw new ArgumentException("the holder is a container, not the player", nameof(holder));
        var ms = LifetimeMs(o.Lifetime);
        var mod = ModContext.CurrentMod;
        if (mod is { Alive: false }) throw new InvalidOperationException("the mod is unloading");
        if (WhyOff() is { } off) throw new KilnUnavailableException(Gems.SourcesCapability, $"Gems.OfferChestGems: {off}");
        if ((!o.PayFrom.IsPlayer || (!o.ReturnTo.IsNone && o.ReturnTo != player)) && !Capabilities.Is(Gems.PayersCapability))
            throw ServiceCaps.Unavailable(Gems.PayersCapability, "Gems.OfferChestGems: PayFrom or ReturnTo other than the player cannot run");
        GemSourceOffer No(string why) => new(player, holder, [], why);
        var inv = contents ?? Items.Read(holder);
        if (inv is null || !inv.Initialized) return No("the holder has no initialized inventory (not loaded, or no container)");
        if (inv.IsPlayer || inv.IsNpc) return No("the holder is a player or an NPC, not a container");
        WatchPlayers();
        var entries = Entries(player, holder, inv.Slots, o, ms, isGem ?? (id => Items.Get(id)?.Category == ItemCategory.WeaponGem));
        var offered = new List<OfferedGem>();
        if (entries.Count > 0)
        {
            var arr = entries.ToArray();
            var rc = GemCrafting.Backend.Offer(OwnerOf(mod), arr);
            if (rc != GemcraftNative.Ok)
                return No(rc switch
                {
                    GemcraftNative.BufferTooSmall => "the player would hold more than 256 offered gems (or more than 4096 are live)",
                    GemcraftNative.InvalidArgument => "kiln.gemcraft refused an entry, or the player is not in game",
                    _ => $"kiln.gemcraft refused the offer (error {rc}): {WhyOff() ?? "see kiln.log"}",
                });
            lock (Gate)
            {
                foreach (var e in arr)
                {
                    var slot = inv.Slots.FirstOrDefault(s => s.Slot.Value == e.Slot);
                    offered.Add(new OfferedGem(e.Token, slot?.Ref is { IsNone: false } r ? r : new ItemInstanceRef(new EntityRef(new EntityId(e.Gem), 0), new ItemId(e.Item)),
                        new SlotId(e.Slot), (int)e.Level));
                }
                if (Sessions.ContainsKey(player.Value)) Dirty.Add(player.Value);
            }
            GemCrafting.Start();  // the pump sends the rows at the end of the tick
        }
        return new GemSourceOffer(player, holder, offered, null);
    }

    internal static int Withdraw(EntityId player, EntityId holder)
    {
        Services.RequireTickThread("Gems.WithdrawChestGems");
        if (!Capabilities.Is(Gems.SourcesCapability)) throw ServiceCaps.Unavailable(Gems.SourcesCapability, "Gems.WithdrawChestGems cannot run");
        var mod = ModContext.CurrentMod;
        var rc = GemCrafting.Backend.Withdraw(OwnerOf(mod), player.Value, holder.Value, out var n);
        if (rc != GemcraftNative.Ok) return 0;
        lock (Gate)
            if (player.IsNone) foreach (var s in Sessions.Keys) Dirty.Add(s);
            else if (Sessions.ContainsKey(player.Value)) Dirty.Add(player.Value);
        GemCrafting.Start();
        return (int)n;
    }

    /// <summary>The nearest Shroud Forge within 12 m of the player; none when Kiln cannot tell.</summary>
    private static EntityId ForgeNear(EntityId player)
    {
        if (ForgeNearHook is { } hook) return hook(player);
        try
        {
            if (World.Transform(player) is not { } t) return EntityId.None;
            var found = Spawn.FindByName("Shroud_Gem_Forge", t.Position, ForgeRadius, 8);
            return found.Count > 0 ? found[0] : EntityId.None;
        }
        catch (Exception)  // best effort: no forge named
        {
            return EntityId.None;
        }
    }

    /// <summary>The server's part of the pump (tick thread, every Pre): the clients' hellos, openings due after the
    /// player's second, the forges' materials still to read, then the rows of players whose offers changed.</summary>
    internal static void PumpServer()
    {
        var n = 0;
        while (n++ < 256 && Inbox.TryDequeue(out var item)) Hello(item.Player, item.Hello);
        var now = NowMs();
        List<(uint Player, EntityId Forge, bool Refresh)>? due = null;
        uint[] dirty;
        lock (Gate)
        {
            foreach (var (p, s) in Sessions)
            {
                if (s.PostDue && !(LastPost.TryGetValue(p, out var last) && now - last < PostEveryMs && now >= last))
                {
                    s.PostDue = false;
                    LastPost[p] = now;
                    (due ??= []).Add((p, s.Forge, s.Posted));
                    s.Posted = true;
                }
                // the forge's material not read yet: ask again (the rows go again once it is known)
                if (s.Material == 0 && !s.Forge.IsNone && s.MaterialAsks is > 0 and < MaterialTries) Dirty.Add(p);
            }
            if (LastPost.Count > 64)  // limits long past matter no more
                foreach (var p in LastPost.Where(kv => now - kv.Value > 60_000 && !Sessions.ContainsKey(kv.Key)).Select(kv => kv.Key).ToList())
                    LastPost.Remove(p);
            dirty = Dirty.ToArray();
            Dirty.Clear();
        }
        if (due is not null)
            foreach (var (p, forge, refresh) in due) Post(new EntityId(p), forge, refresh);
        foreach (var p in dirty) SendRows(new EntityId(p));
    }

    // A client's hello (tick thread). Only "the screen is open / closed" counts: the materials a client names are ignored
    // (the counts are of the forge's own upgrade material, what containers pay in).
    internal static void Hello(EntityId player, GemForgeHello m)
    {
        if (player.IsNone) return;
        if (!m.Open)
        {
            lock (Gate) Sessions.Remove(player.Value);
            try
            {
                GemCrafting.Backend.Withdraw(0, player.Value, 0, out _);  // every mod's offers to this player
            }
            catch (Exception e) when (e is KilnUnavailableException or InvalidOperationException)
            {
            }
            return;
        }
        if (WhyOff() is not null) return;  // the hello came over kiln.net: the row's other needs are kiln.gemcraft's
        WatchPlayers();
        bool fresh, limited, refresh;
        Session s;
        var now = NowMs();
        lock (Gate)
        {
            fresh = !Sessions.TryGetValue(player.Value, out s!);
            if (fresh) Sessions[player.Value] = s = new Session();
            // the player's limit, kept across a close: a close and an open inside the second post nothing more either
            limited = LastPost.TryGetValue(player.Value, out var last) && now - last < PostEveryMs && now >= last;
            if (limited)
            {
                if (!s.Posted) s.PostDue = true;  // the opening: posted by the pump once the second is over
            }
            else
            {
                LastPost[player.Value] = now;
                s.PostDue = false;
            }
            refresh = s.Posted;
            if (!limited) s.Posted = true;
        }
        if (fresh) s.Forge = ForgeNear(player);
        if (!limited) Post(player, s.Forge, refresh);
    }

    private static void Post(EntityId player, EntityId forge, bool refresh)
    {
        try
        {
            Events.Post(new GemForgeOpenedEvent(player, forge, refresh));
        }
        catch (Exception e)
        {
            ModLog.Warn($"kiln.abilities: GemForgeOpenedEvent for player {player}: {e.Message}");
        }
        lock (Gate) Dirty.Add(player.Value);
    }

    /// <summary>The rows and payer counts of a player's live offers, as the client gets them: <paramref name="material"/>
    /// is the forge's own upgrade material (0: not known, no counts). Pure but for the reads it is given (tests).</summary>
    internal static GemForgeOffers Rows(IReadOnlyList<GemcraftNative.Source> live, uint material, uint player, long nowMs,
        Func<GemcraftNative.Source, uint[]> payersOf, Func<uint, uint, uint> countIn)
    {
        var groups = new List<(uint PayFrom, uint PayHolder)>();
        var rows = new List<GemForgeOfferRow>();
        var payers = new List<GemForgeOfferPayer>();
        foreach (var s in live)
        {
            var left = (long)Math.Min(s.ExpiresMs, long.MaxValue) - nowMs;
            if (left <= 0) continue;
            byte group = 0;
            if (s.PayFrom != 0)
            {
                var g = groups.IndexOf((s.PayFrom, s.PayHolder));
                if (g < 0)
                {
                    if (groups.Count >= 255) continue;
                    groups.Add((s.PayFrom, s.PayHolder));
                    g = groups.Count - 1;
                    if (material != 0 && payers.Count < 64)
                    {
                        long total = 0;
                        foreach (var e in payersOf(s))
                            if (e != player) total += countIn(e, material);
                        payers.Add(new GemForgeOfferPayer((byte)(g + 1), s.PayFrom == (uint)GemPayerKind.PlayerThenBaseStorage, material,
                            (uint)Math.Min(total, int.MaxValue)));
                    }
                }
                group = (byte)(g + 1);
            }
            rows.Add(new GemForgeOfferRow((uint)s.Slot, (byte)((s.Slot >> 32) & 0xff), s.Item, s.Gem, s.Level, (byte)s.Rarity, group, s.Token,
                (uint)Math.Min(left, MaxLifetimeMs)));
        }
        return new GemForgeOffers(rows.ToArray(), payers.ToArray());
    }

    private static void SendRows(EntityId player)
    {
        if (GemCrafting.Backend.Extras() is null) return;
        Session? s;
        lock (Gate)
            if (!Sessions.TryGetValue(player.Value, out s)) return;  // the screen closed: nothing to send
        if (s.Material == 0 && !s.Forge.IsNone)
        {
            var rc = GemCrafting.Backend.ForgeMaterial(s.Forge.Value, out var item);
            lock (Gate)
            {
                if (rc == GemcraftNative.Ok) s.Material = item;
                else if (rc == GemcraftNative.NotFound) s.MaterialAsks++;
                else s.MaterialAsks = MaterialTries;  // no forge: the rows go without counts
            }
        }
        var buf = new GemcraftNative.Source[256];
        for (var i = 0; i < buf.Length; i++) buf[i].Size = (uint)Unsafe.SizeOf<GemcraftNative.Source>();
        var sr = GemCrafting.Backend.Sources(player.Value, buf, out var n);
        if (sr != GemcraftNative.Ok && sr != GemcraftNative.BufferTooSmall) return;
        var live = buf.AsSpan(0, (int)Math.Min(n, (uint)buf.Length)).ToArray();
        var msg = Rows(live, s.Material, player.Value, NowMs(),
            src =>
            {
                Span<uint> list = stackalloc uint[64];
                return GemCrafting.Backend.Payers(player.Value, src.PayFrom, src.PayHolder, list, out var k) is GemcraftNative.Ok or GemcraftNative.BufferTooSmall
                    ? list[..(int)Math.Min(k, 64u)].ToArray() : [];
            },
            (e, item) =>
            {
                try
                {
                    return (uint)Math.Max(0, Items.Read(new EntityId(e))?.Count(new ItemId(item)) ?? 0);
                }
                catch (Exception x) when (x is KilnUnavailableException or InvalidOperationException)
                {
                    return 0;  // unreadable: the client's price test shows less, the server still checks the payment
                }
            });
        Send(player, msg);
    }

    private static void Send(EntityId player, object msg)
    {
        if (SendHook is { } hook)
        {
            hook(player, msg);
            return;
        }
        // a hosting client's own player: delivered here (its channel copy, if any, is dropped on arrival)
        if (Client.IsHosting && Client.HasLocalPlayer && Client.LocalPlayer == player)
        {
            if (msg is GemForgeOffers o) ClientOffers(o);
            else if (msg is GemForgeOutcome r) ClientOutcome(r);
            return;
        }
        var ch = _channel;
        if (ch is null) return;
        _ = msg switch
        {
            GemForgeOffers o => ch.SendTo(player, o),
            GemForgeOutcome r => ch.SendTo(player, r),
            _ => false,
        };
    }

    /// <summary>An action on an offered gem ended (the pump's crafted records with the SOURCE flag).</summary>
    internal static void OnSourceResult(in GemcraftNative.Craft c)
    {
        var player = new EntityId(c.Player);
        var message = GemCrafting.Message(c);
        Send(player, new GemForgeOutcome(c.Ticket, c.Status, c.Status == 0 ? "" : message.Length > 0 ? message : ((GemCraftStatus)c.Status).ToString()));
        Session? s;
        lock (Gate) Sessions.TryGetValue(player.Value, out s);
        if (s is not null) Post(player, s.Forge, true);  // the chest changed: the mods offer again
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Sessions.Clear();
            LastPost.Clear();
            Dirty.Clear();
            _playersWatched = false;
        }
        while (Inbox.TryDequeue(out _)) { }
        SendHook = null;
        ForgeNearHook = null;
        Clock = static () => Environment.TickCount64;
        TickSwitchHook = null;
        ServerSideHook = null;
        _open = _ticking = false;
    }

    // ================================================================ client ====

    private static bool _open, _ticking;
    private static long _lastHello;
    private static int _setFailures;

    /// <summary>Test hooks: the frame watch switched on or off; whether the server takes hellos (hosting, or Kiln there).</summary>
    internal static Action<bool>? TickSwitchHook;
    internal static Func<bool>? ServerSideHook;

    private static void StartClient()
    {
        lock (Gate)
        {
            if (_clientStarted) return;
            _clientStarted = true;
        }
        try
        {
            if (!GemForgeSources.TableHere || !Ui.Screens.Available && !UiScreensMayCome()) return;
            var outer = ModContext.CurrentMod;
            ModContext.CurrentMod = null;
            try
            {
                Ui.Screens.Changed += ClientChanged;  // screen changes only: no work in other frames
            }
            finally
            {
                ModContext.CurrentMod = outer;
            }
        }
        catch (Exception e) when (e is KilnUnavailableException or InvalidOperationException or TypeInitializationException)
        {
            ModLog.Warn($"kiln.abilities: the Gem Forge's chest gems are off on this client: {e.Message}");
        }
    }

    // ui.screens reads unavailable before the game's UI exists (boot): the watch starts anyway and stays idle until then.
    private static bool UiScreensMayCome() => GemForgeSources.TableHere;

    private static bool ServerSide() => ServerSideHook?.Invoke() ?? (Client.IsHosting || Network.ServerHasKiln);

    // A screen change (the game's UI thread): the forge screen opened (the frame watch starts, the server is told) or
    // closed (the watch stops, the rows go, the server is told).
    internal static void ClientChanged(GameScreen before, GameScreen now)
    {
        var open = now.Hud == HudScreen.ShroudForge && ServerSide();
        if (open == _ticking) return;
        _ticking = open;
        SwitchTick(open);
        if (open)
        {
            _open = true;
            _lastHello = Environment.TickCount64;
            Hello(true);
        }
        else if (_open)
        {
            _open = false;
            Hello(false);
            GemForgeSources.Set([], []);
        }
    }

    private static void SwitchTick(bool on)
    {
        if (TickSwitchHook is { } hook)
        {
            hook(on);
            return;
        }
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = null;
        try
        {
            if (on) Ui.Screens.UiTick += ClientTick;
            else Ui.Screens.UiTick -= ClientTick;
        }
        catch (Exception e) when (e is KilnUnavailableException or InvalidOperationException)
        {
        }
        finally
        {
            ModContext.CurrentMod = outer;
        }
    }

    // Every UI frame while the forge screen is open (the game's UI thread): the keep-alive every 5 s.
    private static void ClientTick(GameScreen now)
    {
        if (now.Hud != HudScreen.ShroudForge) return;  // Changed switches the watch off
        var tick = Environment.TickCount64;
        if (tick - _lastHello < (long)KeepAlive.TotalMilliseconds) return;
        _lastHello = tick;
        Hello(true);
    }

    private static void Hello(bool open)
    {
        var m = new GemForgeHello(open, []);  // the server counts the forge's own material: no materials to name
        if (Client.IsHosting && Client.HasLocalPlayer)
        {
            Inbox.Enqueue((Client.LocalPlayer, m));  // the server half in this process takes it on its tick
            GemCrafting.Start();
            return;
        }
        _channel?.SendToServer(m);
    }

    // The server's rows (the client's main thread, or the hosting process' tick thread).
    internal static void ClientOffers(GemForgeOffers m)
    {
        var now = Environment.TickCount64;
        var rows = m.Rows.Select(r => new GemForgeSourceRow(r.Page, r.Slot, r.Item, r.Pide, r.Level, r.Rarity, r.Group, r.Token,
            now + r.LifetimeMs)).ToArray();
        var (ok, why) = GemForgeSources.Set(rows, m.Payers.Select(p => new GemForgeSourcePayer(p.Group, p.PlayerFirst, p.Item, p.Count)).ToArray());
        if (!ok && rows.Length > 0 && Interlocked.Increment(ref _setFailures) <= 3)
            ModLog.Warn($"kiln.abilities: the server's chest gems are not listed in the Gem Forge: {why}");
    }

    internal static void ClientOutcome(GemForgeOutcome m)
    {
        if (m.Status == 0) return;
        try
        {
            Hud.Toast(KilnText.T("gemforge.outcome", ("message", m.Message)), ToastKind.Warning, 4);
        }
        catch (Exception e) when (e is InvalidOperationException or KilnUnavailableException)
        {
        }
    }
}
