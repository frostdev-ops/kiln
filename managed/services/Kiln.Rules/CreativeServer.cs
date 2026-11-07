// SPDX-License-Identifier: MIT
// The server half of the creative menu: who may do what, and doing it. Runs on the server's tick thread (a dedicated
// server, or a game hosting its world).
namespace Kiln;

/// <summary>
/// Creative mode's tools beyond the rules: taking items from the game's catalogue, the time of day and the day and
/// night cycle. The same checks serve the creative menu, <c>/creative give|time|cycle</c> and remote tools. Tick thread.
/// </summary>
public static class CreativeTools
{
    /// <summary>Items one request may give at most.</summary>
    public const int MaxGive = 9999;

    /// <summary>Whether <paramref name="player"/> may take items: creative mode is on for them (which only an admin can
    /// turn on), and this build can give items.</summary>
    public static bool CanGive(EntityId player) => Creative.IsOn(player) && ItemsReady();

    /// <summary>Whether <paramref name="player"/> may change the world's time: they have the creative permission
    /// (<see cref="Creative.PermissionNode"/>; admins, a hosted world's host).</summary>
    public static bool MayChangeTime(EntityId player) => HasPermission(player);

    /// <summary>Gives <paramref name="count"/> of <paramref name="item"/> to the player, as a stack the game makes
    /// (gear with a level and rarity when given). Refused (Ok false) unless <see cref="CanGive"/>; the server's catalogue
    /// decides what exists.</summary>
    public static async Task<CreativeResult> Give(EntityId player, ItemId item, int count = 1, int? level = null, ItemRarity? rarity = null)
    {
        Game.AssertTickThread();
        if (!Creative.IsOn(player)) return new(false, false, "Taking items needs creative mode (/creative).");
        if (!ItemsReady()) return new(false, true, "This server's game build cannot give items (kiln.items).");
        var info = Items.Get(item);
        if (info is null) return new(false, true, $"No item {item} in this world's catalogue.");
        if (ValidateGive(info, count, level, rarity) is { } error) return new(false, true, error);
        if (!Gives.TryEnter(player, Environment.TickCount64, out var ticket)) return new(false, true, "Wait for your previous take, then try again.");
        var name = NameOf(player);  // continuations without a mod scheduler may run off the tick thread
        try
        {
            Gear? gear = info.IsGear && (level is not null || rarity is not null)
                ? new Gear(level, rarity)
                : null;
            var r = await Items.Give(player, item, count, gear, GiveOptions.Notify);
            var what = $"{r.Done} x {info.Label}" + (gear is { } g ? $" (level {g.Level?.ToString() ?? "rolled"}, {g.Rarity?.ToString() ?? "default rarity"})" : "");
            RulesRuntime.Log(LogLevel.Info, $"creative: {name} took {what} ({info.Name}, {item}): {r.Status}");
            return r.Status switch
            {
                ItemsStatus.Done => new(true, true, $"Took {what}."),
                ItemsStatus.Partial => new(true, true, $"Took {what}; the rest did not fit."),
                ItemsStatus.NoRoom => new(false, true, "No room in your inventory."),
                _ => new(false, true, $"The game refused it: {r.Message}"),
            };
        }
        finally { Gives.Complete(player, ticket); }
    }

    private static readonly CreativeGiveGate Gives = new();

    internal static string? ValidateGive(ItemInfo info, int count, int? level, ItemRarity? rarity)
    {
        if (!CreativeCatalog.Listed(info.Name, info.Flags)) return "This item is excluded from the creative catalogue.";
        var max = info.IsGear ? 1 : Math.Min(MaxGive, Math.Max(1, info.MaxStack));
        if (count < 1 || count > max) return $"Take between 1 and {max} items (at most one stack).";
        if (!info.IsGear && (level is not null || rarity is not null)) return "Only gear has a level or rarity.";
        int lo = Math.Max(info.LevelMin, 1), hi = Math.Min(Math.Max(info.LevelMax, lo), 255);
        if (info.IsGear && lo > hi) return "This gear has no supported level range.";
        if (level is { } l && (l < lo || l > hi)) return $"Level must be {lo}..{hi}.";
        if (rarity is { } r && !Enum.IsDefined(r)) return "Unknown rarity.";
        return null;
    }

    internal static void WorldClosed() => Gives.Clear();

    /// <summary>Jumps the world's clock forward to <paramref name="hours"/> (0..24). Refused unless
    /// <see cref="MayChangeTime"/>.</summary>
    public static CreativeResult SetTime(EntityId player, double hours)
    {
        Game.AssertTickThread();
        if (!HasPermission(player)) return new(false, Creative.IsOn(player), "Only an admin may change the time of day.");
        return SetTime(hours, NameOf(player)) with { On = Creative.IsOn(player) };
    }

    /// <summary>Jumps the world's clock forward to <paramref name="hours"/>, with no check of who asks: for the console,
    /// commands that check their own permission, and remote tools. <paramref name="who"/> goes into the log.</summary>
    public static CreativeResult SetTime(double hours, string who)
    {
        Game.AssertTickThread();
        if (!double.IsFinite(hours)) return new(false, false, "not a time of day");
        if (!TimeReady()) return new(false, false, "This build cannot set the time of day (kiln.time).");
        Time.Set(hours);
        RulesRuntime.Log(LogLevel.Info, $"creative: {who} moved the time of day to {Time.Format(hours)}");
        return new(true, false, $"The time of day is {Time.Format(hours)}.");
    }

    /// <summary>Sets the day and night cycle (Normal, Stopped, Fast) for the whole world, for this session. Refused unless
    /// <see cref="MayChangeTime"/>.</summary>
    public static CreativeResult SetCycle(EntityId player, TimeCycle cycle)
    {
        Game.AssertTickThread();
        if (!HasPermission(player)) return new(false, Creative.IsOn(player), "Only an admin may change the day and night cycle.");
        return SetCycle(cycle, NameOf(player)) with { On = Creative.IsOn(player) };
    }

    /// <summary>Sets the day and night cycle, with no check of who asks (see <see cref="SetTime(double, string)"/>).</summary>
    public static CreativeResult SetCycle(TimeCycle cycle, string who)
    {
        Game.AssertTickThread();
        if (!Enum.IsDefined(cycle)) return new(false, false, "unknown cycle");
        if (!CycleReady()) return new(false, false, "This build cannot hold the clock's speed (kiln.time, feature time.scale).");
        Time.SetCycle(cycle);
        RulesRuntime.Log(LogLevel.Info, $"creative: {who} set the day and night cycle to {cycle}" +
                                        (Time.HeldSpeed is { } s ? $" (clock speed {s:0.##})" : ""));
        return new(true, false, cycle switch
        {
            TimeCycle.Stopped => "The sun stands still until the cycle is set going again (or the server restarts).",
            TimeCycle.Fast => $"Fast: a whole day and night in about {Time.FastCycle.TotalSeconds:0} seconds.",
            _ => "Day and night pass as the game runs them.",
        });
    }

    /// <summary>The server's catalogue for the menu: every item of this world (mods' items included), without the
    /// game's debug, test and retired entries, sorted by category and name.</summary>
    public static IReadOnlyList<ItemInfo> Catalog()
    {
        if (!ItemsReady()) return Array.Empty<ItemInfo>();
        var all = Items.All;
        if (!ReferenceEquals(all, _catalogFrom))
        {
            _catalog = all.Where(i => CreativeCatalog.Listed(i.Name, i.Flags)).OrderBy(i => i.Category).ThenBy(i => i.Label, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _catalogFrom = all;
        }
        return _catalog;
    }

    /// <summary>The catalogue items <paramref name="player"/> has found: the game's item knowledge (keyed by the item
    /// id; set when the player picks the item up, crafts, uses or inspects it) is not empty. Null when this server
    /// cannot read knowledge (kiln.knowledge) or the player is not in game. The creative menu's journey mode lists only
    /// these.</summary>
    public static IReadOnlyList<ItemId>? Found(EntityId player)
    {
        Game.AssertTickThread();
        try
        {
            return FoundIn(player, Catalog());
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException or NotSupportedException or InvalidOperationException or KeyNotFoundException)
        {
            RulesRuntime.Log(LogLevel.Info, $"creative: the items {NameOf(player)} has found are not known here: {e.Message}");
            return null;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static IReadOnlyList<ItemId>? FoundIn(EntityId player, IReadOnlyList<ItemInfo> catalog)
    {
        if (player.IsNone || Kiln.Players.Find(player) is null || !Knowledge.Available) return null;
        // Read the map once, rather than crossing the native boundary for every catalogue entry.
        var found = Knowledge.List(player).Where(k => k.Value != 0).Select(k => k.Id).ToHashSet();
        return catalog.Where(i => found.Contains(i.Id.Value)).Select(i => i.Id).ToArray();
    }

    private static IReadOnlyList<ItemInfo> _catalog = Array.Empty<ItemInfo>();
    private static IReadOnlyList<ItemInfo>? _catalogFrom;

    internal static bool HasPermission(EntityId player)
    {
        if (player.IsNone) return false;
        try
        {
            if (CreativeNet.Hosting(out var self) && self == player) return true;  // a hosted world's host has every right
            return Permissions.Has(player, Creative.PermissionNode);
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool ItemsReady()
    {
        try
        {
            return Items.Available && Items.CanTransact;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool TimeReady()
    {
        try
        {
            return Time.Available && Time.Ready;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool CycleReady()
    {
        try
        {
            return TimeReady() && Time.CanSetSpeed;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static string NameOf(EntityId e)
    {
        try
        {
            return Kiln.Players.Find(e)?.Name is { Length: > 0 } n ? n : $"player {e}";
        }
        catch (Exception)
        {
            return $"player {e}";
        }
    }
}

/// <summary>The creative menu's server half: answers <see cref="CreativeNet"/> requests.</summary>
internal static class CreativeServer
{
    private const int PageSize = 150;

    public static void Handle(EntityId player, CreativeRequest req, bool local)
    {
        Game.AssertTickThread();
        if (player.IsNone) return;
        switch (req.Kind)
        {
            case CreativeAsk.State:
                CreativeNet.SendState(player, StateOf(player), local);
                return;
            case CreativeAsk.Give:
                _ = GiveAsync(player, req, local);
                return;
            case CreativeAsk.Time:
                Answer(player, CreativeTools.SetTime(player, req.Value), local);
                return;
            case CreativeAsk.Cycle:
                Answer(player, Enum.IsDefined((TimeCycle)req.Count) ? CreativeTools.SetCycle(player, (TimeCycle)req.Count)
                    : new CreativeResult(false, Creative.IsOn(player), "unknown cycle"), local);
                return;
            case CreativeAsk.Creative:
                // only the permission's holders: Creative.Set itself does not check who asks
                Answer(player, CreativeTools.HasPermission(player) ? Creative.Set(player, req.Count != 0)
                    : new CreativeResult(false, Creative.IsOn(player), "You may not use /creative."), local);
                return;
            case CreativeAsk.Catalog:
                SendPage(player, req.Count, local);
                return;
            case CreativeAsk.Found:
                var found = CreativeTools.Found(player);
                CreativeNet.SendFound(player, new CreativeFoundMsg(found is not null, found?.Select(i => i.Value).ToArray() ?? []), local);
                return;
        }
    }

    private static async Task GiveAsync(EntityId player, CreativeRequest req, bool local)
    {
        var session = CreativeNet.Session;
        CreativeResult r;
        try
        {
            if (req.Level < -1 || req.Rarity < -1 || req.Rarity > (int)ItemRarity.Legendary)
            {
                CreativeNet.SendReply(player, new CreativeReplyMsg(false, "Invalid level or rarity."), local);
                return;
            }
            r = await CreativeTools.Give(player, new ItemId(req.Item), req.Count,
                req.Level >= 0 ? req.Level : null, req.Rarity is >= 0 and <= (int)ItemRarity.Legendary ? (ItemRarity)req.Rarity : null);
        }
        catch (Exception e)
        {
            r = new CreativeResult(false, false, e.Message);
        }
        CreativeNet.Post(() => CreativeNet.SendReply(player, new CreativeReplyMsg(r.Ok, r.Message), local), session);
    }

    private static void Answer(EntityId player, CreativeResult r, bool local)
    {
        CreativeNet.SendReply(player, new CreativeReplyMsg(r.Ok, r.Message), local);
        CreativeNet.SendState(player, StateOf(player), local);
    }

    /// <summary>Tells a player's game what changed (creative mode on or off for them).</summary>
    public static void Push(EntityId player)
    {
        try
        {
            CreativeNet.SendState(player, StateOf(player), local: false);
        }
        catch (Exception)
        {
            // no channel, or the player left
        }
    }

    public static CreativeStateMsg StateOf(EntityId player)
    {
        var a = CreativeAccess.None;
        var admin = CreativeTools.HasPermission(player);
        if (admin) a |= CreativeAccess.Admin;
        if (Creative.IsOn(player)) a |= CreativeAccess.On;
        if (CreativeTools.CanGive(player)) a |= CreativeAccess.Give;
        if (admin && CreativeTools.TimeReady()) a |= CreativeAccess.Time;
        if (admin && CreativeTools.CycleReady()) a |= CreativeAccess.Cycle;
        var hours = -1.0;
        var cycle = TimeCycle.Normal;
        if (CreativeTools.TimeReady())
        {
            try
            {
                hours = Time.OfDay;
                cycle = Time.Cycle;
            }
            catch (Exception)
            {
                // the world is closing
            }
        }
        string rules;
        try
        {
            rules = Rules.Of(player).ToString();
        }
        catch (Exception)
        {
            rules = "";
        }
        return new CreativeStateMsg(a, cycle, hours, rules, Creative.Status());
    }

    private static void SendPage(EntityId player, int start, bool local)
    {
        var all = CreativeTools.Catalog();
        start = Math.Clamp(start, 0, all.Count);
        var page = new List<CreativeItem>(Math.Min(PageSize, all.Count - start));
        for (var i = start; i < all.Count && page.Count < PageSize; i++)
        {
            var x = all[i];
            page.Add(new CreativeItem(x.Id.Value, x.Name, x.Label, x.Category, x.Rarity, x.IsGear, x.LevelMin, x.LevelMax, x.MaxStack));
        }
        CreativeNet.SendCatalog(player, new CreativeCatalogMsg(start, all.Count, page), local);
    }
}

/// <summary>Which catalogue entries the creative menu lists (pure: unit-tested).</summary>
internal static class CreativeCatalog
{
    // debug names of entries a player should not take: the game's test, debug and retired items
    private static readonly string[] Hidden =
        ["debug", "_test", "test_", "deprecated", "unused", "hasbugs", "dummy", "placeholder", "_wip", "cheat", "_dev_", "template"];

    public static bool Listed(string name, uint flags)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        foreach (var h in Hidden)
            if (name.Contains(h, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>Weapons, armour, rings and gems: what "without equipment" leaves out (bombs, arrows, food, potions and
    /// tools stay).</summary>
    public static bool IsEquipment(ItemCategory category, bool isGear) =>
        category is ItemCategory.Weapons or ItemCategory.Equipment or ItemCategory.WeaponGem || (isGear && category != ItemCategory.Tools);

    /// <summary>Does <paramref name="query"/> (words, any order, any case) match the item?</summary>
    public static bool Matches(string query, string label, string name)
    {
        return MatchesWords(SearchWords(query), label, name);
    }

    internal static string[] SearchWords(string query) => query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    internal static bool FoundMatches(bool journey, IReadOnlySet<uint>? found, uint id) => !journey || found?.Contains(id) == true;

    internal static bool MatchesWords(IReadOnlyList<string> words, string label, string name)
    {
        foreach (var w in words)
            if (!label.Contains(w, StringComparison.OrdinalIgnoreCase) && !name.Contains(w, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}

// Shared by the channel, commands and admin ops. Tick-thread admission; a request occupies its slot until the
// inventory transaction completes. Old completions cannot release a new world's slot for a reused entity id.
internal sealed class CreativeGiveGate
{
    private readonly Dictionary<EntityId, (long At, long Ticket, bool Pending)> _players = new();
    private long _next;
    public bool TryEnter(EntityId player, long now, out long ticket)
    {
        lock (_players)
        {
            ticket = 0;
            if (_players.TryGetValue(player, out var last) && (last.Pending || now - last.At < 250)) return false;
            if (_players.Count >= 1024)
                foreach (var p in _players.Where(p => !p.Value.Pending && now - p.Value.At >= 250).Select(p => p.Key).ToArray())
                    _players.Remove(p);
            if (_players.Count >= 1024 && !_players.ContainsKey(player)) return false;
            ticket = ++_next;
            _players[player] = (now, ticket, true);
            return true;
        }
    }
    public void Complete(EntityId player, long ticket)
    {
        lock (_players)
            if (_players.TryGetValue(player, out var p) && p.Ticket == ticket) _players[player] = (p.At, p.Ticket, false);
    }
    public void Clear() { lock (_players) _players.Clear(); }
}
