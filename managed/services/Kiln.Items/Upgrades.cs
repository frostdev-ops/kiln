// SPDX-License-Identifier: MIT
// Rarity upgrades (package P3 of the equipment wave): Items.UpgradeRarity checks an item against a rule, prices the step,
// asks the GearUpgradingEvent handlers, rolls the chance on the server and sends one kiln.items v8 gear_upgrade
// (native/core/abi/services/kiln_items.h "upgrades"): kiln_items_gear takes the cost on one inventory transaction and
// makes the gear write in the same step, all or nothing. Rules as blocks come from Kiln.Blocks (RarityUpgradeBlock) and
// are registered here by id. Capability items.gear.upgrade.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Kiln;

public static unsafe partial class Items
{
    /// <summary>True when <see cref="UpgradeRarity(EntityId, ItemInstanceRef, RarityUpgradeRule, UpgradeOptions?)"/> can run
    /// (capability <c>items.gear.upgrade</c>).</summary>
    public static bool UpgradeAvailable => Can(Caps.GearUpgrade);

    /// <summary>
    /// Raises an item's rarity by one step of <paramref name="rule"/>, in place: the item gets its next enhancement slot
    /// (locked: the player unlocks it at the workbench as usual, unless the step says <see cref="UpgradeStep.Unlock"/>), the
    /// same item entity with its item data and affixes. The step's cost is taken from the player (or
    /// <see cref="UpgradeOptions.PayFrom"/>) in the same server step as the change, all or nothing; its chance is rolled on the
    /// server. A refusal is a result, never an exception: nothing is paid then.
    /// </summary>
    /// <param name="player">The player it runs for: who pays by default, and who the events name.</param>
    /// <param name="item">The item: <see cref="Held"/>, a slot's <see cref="InventorySlot.Ref"/>, a give result's ref.</param>
    /// <param name="rule">The rule: which items, up to which rarity, what each step costs.</param>
    /// <param name="options">Who pays, free, guaranteed, dry run.</param>
    /// <returns>Completes on the tick thread one step after the step it ran in. The game shows the new rarity from its next
    /// step; <see cref="GearUpgradedEvent"/> and <see cref="GearRarityChangedEvent"/> (source
    /// <see cref="GearChangeSource.Upgrade"/>) are posted before the task continues.</returns>
    /// <exception cref="ArgumentException"><paramref name="player"/> is none, <paramref name="item"/> is none or not an item
    /// entity's ref, or the rule is malformed (a chance outside 0..1, a cost outside 1..100000, more than 8 cost items, a
    /// negative level scale, a step below Uncommon).</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or the calling mod is unloading.</exception>
    /// <exception cref="KilnUnavailableException">The capability <c>items.gear.upgrade</c> is off (or
    /// <c>items.gear.durability</c> for a durability rule other than <see cref="DurabilityRule.Keep"/>).</exception>
    /// <example><code>
    /// UpgradeResult r = await Items.UpgradeRarity(player, Items.Held(player), rule);
    /// if (!r.Ok) Chat.Tell(player, $"Not upgraded: {r.Reason}");
    /// </code></example>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(Caps.GearUpgrade)]
    public static Task<UpgradeResult> UpgradeRarity(EntityId player, ItemInstanceRef item, RarityUpgradeRule rule, UpgradeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return ItemUpgrades.Run(player, item, rule, options, "Items.UpgradeRarity");
    }

    /// <summary>As <see cref="UpgradeRarity(EntityId, ItemInstanceRef, RarityUpgradeRule, UpgradeOptions?)"/>, with a rule a
    /// mod defined as a block (<c>RarityUpgradeBlock</c>, <c>rarityUpgrades</c> in <c>blocks/*.json</c>): its name in the
    /// calling mod, or <c>"other-mod:name"</c> (a block handle's <c>ToString()</c>).</summary>
    /// <exception cref="ArgumentException">No loaded mod defines such a rule (or as the other overload).</exception>
    /// <exception cref="InvalidOperationException">As the other overload.</exception>
    /// <exception cref="KilnUnavailableException">As the other overload.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(Caps.GearUpgrade)]
    public static Task<UpgradeResult> UpgradeRarity(EntityId player, ItemInstanceRef item, string rule, UpgradeOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rule);
        var found = ItemUpgrades.Find(rule, ModContext.CurrentMod?.Info.Id)
                    ?? throw new ArgumentException($"no loaded mod defines the rarity upgrade rule \"{rule}\" (a block name of this mod, or \"mod:name\")", nameof(rule));
        return ItemUpgrades.Run(player, item, found, options, "Items.UpgradeRarity");
    }
}

/// <summary>The upgrade path: rules by id, the checks, the roll, the native request and its result. Internal to Kiln.Items
/// and Kiln.Blocks (which registers the block rules).</summary>
internal static class ItemUpgrades
{
    /// <summary>The game's chance of a socket on a new weapon, Common .. Legendary (BalancingTable gemSlotProbability of the
    /// unchanged game, the equipment dossier section 2): what <see cref="SocketRule.GameChance"/> rolls.</summary>
    internal static readonly double[] GameSocketChances = [0.0, 0.05, 0.25, 0.5, 1.0];

    // ---- rules by id (Kiln.Blocks' blocks) ----

    private static readonly object Gate = new();
    private static readonly Dictionary<string, RarityUpgradeRule> Rules = new(StringComparer.Ordinal);

    /// <summary>A block rule compiled (id "mod:name"): the same id again replaces it.</summary>
    internal static void Register(string id, RarityUpgradeRule rule)
    {
        lock (Gate) Rules[id] = rule;
    }

    /// <summary>A mod's blocks were reset (it reloads, or unloaded): its rules go.</summary>
    internal static void Forget(string mod)
    {
        lock (Gate)
            foreach (var k in Rules.Keys.Where(k => k.StartsWith(mod + ":", StringComparison.Ordinal)).ToArray()) Rules.Remove(k);
    }

    /// <summary>A rule by "mod:name", or by name in <paramref name="mod"/>.</summary>
    internal static RarityUpgradeRule? Find(string name, string? mod)
    {
        lock (Gate)
        {
            if (Rules.TryGetValue(name, out var r)) return r;
            return mod != null && !name.Contains(':') && Rules.TryGetValue($"{mod}:{name}", out r) ? r : null;
        }
    }

    // ---- the roll: the server's own random numbers; the test kit can name the rolls ----

    private static Func<double>? _roller;

    /// <summary>Tests (the test kit's HarnessItems.SetUpgradeRolls): the rolls to use, null for the server's random numbers.</summary>
    internal static Func<double>? Roller
    {
        get => Volatile.Read(ref _roller);
        set => Volatile.Write(ref _roller, value);
    }

    private static double Roll()
    {
        if (Roller is { } r) return Math.Clamp(r(), 0.0, Math.BitDecrement(1.0));
        return RandomNumberGenerator.GetInt32(int.MaxValue) / (double)int.MaxValue;  // a CSPRNG: nothing a player sees predicts it
    }

    /// <summary>Whether a roll succeeds, and the roll (null when none was needed). Pure but for the roller.</summary>
    internal static (bool Success, double? Rolled) Decide(double chance, bool guaranteed)
    {
        if (guaranteed || chance >= 1) return (true, null);
        if (!(chance > 0)) return (false, null);
        var rolled = Roll();
        return (rolled < chance, rolled);
    }

    // ---- checks (pure) ----

    /// <summary>What is wrong with a rule: <see cref="ArgumentException"/> naming the field.</summary>
    internal static void Check(RarityUpgradeRule rule, string param)
    {
        if (!Enum.IsDefined(rule.Max)) throw new ArgumentException($"Max: {rule.Max} is no rarity", param);
        if (!Enum.IsDefined(rule.Durability)) throw new ArgumentException($"Durability: {rule.Durability} is no durability rule", param);
        if (!Enum.IsDefined(rule.Socket)) throw new ArgumentException($"Socket: {rule.Socket} is no socket rule", param);
        if (rule.Items is { Count: > 1024 }) throw new ArgumentException("Items: at most 1024 items or patterns", param);
        if (rule.Items != null)
            for (var i = 0; i < rule.Items.Count; i++)
                if (string.IsNullOrWhiteSpace(rule.Items[i])) throw new ArgumentException($"Items[{i}]: an item's name, not empty", param);
        if (rule.Steps is null) throw new ArgumentException("Steps: none", param);
        foreach (var (rarity, step) in rule.Steps)
        {
            var at = $"Steps[{rarity}]";
            if (!Enum.IsDefined(rarity) || rarity == ItemRarity.Common) throw new ArgumentException($"{at}: a step reaches Uncommon to Legendary", param);
            if (step is null) throw new ArgumentException($"{at}: no step", param);
            if (!double.IsFinite(step.Chance) || step.Chance is < 0 or > 1) throw new ArgumentException($"{at}.Chance: {step.Chance} is not 0 to 1", param);
            if (!Enum.IsDefined(step.OnFail)) throw new ArgumentException($"{at}.OnFail: {step.OnFail} is no failure rule", param);
            if (!double.IsFinite(step.LevelScale) || step.LevelScale is < 0 or > 1000) throw new ArgumentException($"{at}.LevelScale: {step.LevelScale} is not 0 to 1000", param);
            if (step.Cost is null) throw new ArgumentException($"{at}.Cost: none (empty is free)", param);
            if (step.Cost.Count > ItemsNative.UpgradeMaxCosts) throw new ArgumentException($"{at}.Cost: at most {ItemsNative.UpgradeMaxCosts} items", param);
            foreach (var (name, count) in step.Cost)
            {
                if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException($"{at}.Cost: an item's name, not empty", param);
                if (count is < 1 or > ItemsNative.UpgradeMaxCount) throw new ArgumentException($"{at}.Cost[{name}]: {count} is not 1 to {ItemsNative.UpgradeMaxCount}", param);
            }
        }
    }

    /// <summary>A step's count at an item's level: <c>count × (1 + scale × (level - 1))</c>, rounded up, 1..100000.</summary>
    internal static int Scaled(int count, double scale, int level)
    {
        if (scale <= 0 || level <= 1) return count;
        var v = Math.Ceiling(count * (1 + scale * (level - 1)));
        return v >= ItemsNative.UpgradeMaxCount ? ItemsNative.UpgradeMaxCount : Math.Max(1, (int)v);
    }

    /// <summary>The game name an entry stands for: "other-mod:name" is "other-mod.name"; a bare name is a game name, or the
    /// mod's own item ("mod.name") when the game has no such name.</summary>
    internal static ItemId? Resolve(string entry, string? mod, Func<string, ItemId?> lookup)
    {
        var s = entry.Trim();
        var colon = s.IndexOf(':');
        if (colon > 0) return lookup($"{s[..colon]}.{s[(colon + 1)..]}");
        return lookup(s) ?? (mod != null ? lookup($"{mod}.{s}") : null);
    }

    /// <summary>Whether the rule takes an item type (by its game name): no list takes every item; an entry is a name as
    /// <see cref="Resolve"/> reads it, or a pattern with <c>*</c> over game names (<c>"mod:*"</c> is <c>"mod.*"</c>).</summary>
    internal static bool Takes(RarityUpgradeRule rule, string? mod, ItemId type, string typeName, Func<string, ItemId?> lookup)
    {
        if (rule.Items is not { Count: > 0 } items) return true;
        foreach (var entry in items)
        {
            var s = entry.Trim();
            if (s.Contains('*'))
            {
                var colon = s.IndexOf(':');
                if (colon > 0) s = $"{s[..colon]}.{s[(colon + 1)..]}";
                var rx = "^" + Regex.Escape(s).Replace("\\*", ".*") + "$";
                if (typeName.Length > 0 && Regex.IsMatch(typeName, rx, RegexOptions.CultureInvariant)) return true;
            }
            else if (Resolve(s, mod, lookup) == type) return true;
        }
        return false;
    }

    // ---- the call ----

    private static unsafe ItemsNative.TableV8* RequireUpgrade(string api, DurabilityRule durability)
    {
        var t = Items.V8;
        if (t == null || ((ItemsNative.Table*)t)->Header.Size < ItemsNative.V8UpgradeEnd || t->GearUpgrade == null)
            throw new KilnUnavailableException(Items.Caps.GearUpgrade, t == null ? "kiln.items here has no gear functions (update Kiln)" : "kiln.items here has no upgrades (gear_upgrade; update Kiln)");
        if (!Capabilities.Is(Items.Caps.GearUpgrade)) throw new KilnUnavailableException(Items.Caps.GearUpgrade, Capabilities.Get(Items.Caps.GearUpgrade).ReasonText);
        return t;
    }

    private static UpgradeResult Refuse(UpgradeStatus status, EntityId player, ItemInstanceRef item, GearState? g, ItemRarity to, string message, string? rule, bool dry) =>
        new(status, player, item, g?.Rarity ?? to, to, g, g, Empty, 0, null, message) { Rule = rule, DryRun = dry };

    private static readonly IReadOnlyDictionary<ItemId, int> Empty = new Dictionary<ItemId, int>();

    internal static unsafe Task<UpgradeResult> Run(EntityId player, ItemInstanceRef item, RarityUpgradeRule rule, UpgradeOptions? options, string api)
    {
        Services.RequireTickThread(api);
        options ??= UpgradeOptions.Default;
        if (player.IsNone) throw new ArgumentException("no player: name who upgrades", nameof(player));
        Check(rule, nameof(rule));
        var stamp = Items.StampOf(item, nameof(item));
        var t = RequireUpgrade(api, rule.Durability);
        var mod = ModContext.CurrentMod;
        if (mod is { Alive: false }) throw new InvalidOperationException("the mod is unloading");
        var owner = rule.Mod ?? mod?.Info.Id;
        var origin = mod?.Info.Id ?? "kiln";
        var dry = options.DryRun;
        var g = Items.GearOf(item);
        if (g == null) return Task.FromResult(Refuse(UpgradeStatus.Stale, player, item, null, ItemRarity.Common, "the item is gone, or its ref names another item now", rule.Id, dry));
        var to = g.Rarity == ItemRarity.Legendary ? g.Rarity : g.Rarity + 1;
        if (!g.HasEnhancementSlots) return Task.FromResult(Refuse(UpgradeStatus.NotGear, player, item, g, to, "the item has no enhancement slots: its type has no rarity of its own to raise", rule.Id, dry));
        static ItemId? Lookup(string n) => Items.Lookup(n) is { } i && string.Equals(i.Name, n, StringComparison.OrdinalIgnoreCase) ? i.Id : null;
        var typeName = Items.Get(g.Type)?.Name ?? "";
        if (!Takes(rule, owner, g.Type, typeName, Lookup))
            return Task.FromResult(Refuse(UpgradeStatus.NotAllowed, player, item, g, to, $"the rule does not take {(typeName.Length > 0 ? typeName : g.Type.ToString())}", rule.Id, dry));
        if (g.Rarity >= rule.Max || g.Rarity == ItemRarity.Legendary)
            return Task.FromResult(Refuse(UpgradeStatus.AtMax, player, item, g, g.Rarity, $"the item is {g.Rarity}, the rule's highest", rule.Id, dry));
        if (!rule.Steps.TryGetValue(to, out var step))
            return Task.FromResult(Refuse(UpgradeStatus.NotAllowed, player, item, g, to, $"the rule has no step to {to}", rule.Id, dry));
        if (to > g.MaxRarity && !rule.AllowEmptySlot)
            return Task.FromResult(Refuse(UpgradeStatus.NoSlot, player, item, g, to, $"its type has no enhancement slot for {to} (it stops at {g.MaxRarity})", rule.Id, dry));
        // the price, by item id, scaled by the item's level
        var cost = new Dictionary<ItemId, int>();
        if (!options.Free)
            foreach (var (name, count) in step.Cost)
            {
                if (Resolve(name, owner, Lookup) is not { } id)
                    return Task.FromResult(Refuse(UpgradeStatus.Refused, player, item, g, to, $"cost item \"{name}\" is not an item the game has", rule.Id, dry));
                if (Items.Get(id) is { IsGear: true })
                    return Task.FromResult(Refuse(UpgradeStatus.Refused, player, item, g, to, $"cost item \"{name}\" has an item entity of its own (gear, a gem): not a cost", rule.Id, dry));
                cost[id] = Math.Min(ItemsNative.UpgradeMaxCount, (cost.TryGetValue(id, out var have) ? have : 0) + Scaled(count, step.LevelScale, g.Level));
            }
        var chance = step.Chance;
        if (!dry && Events.HasSubscribers<GearUpgradingEvent>())
        {
            var e = Events.Post(new GearUpgradingEvent(player, g, to, cost, chance, rule.Id, origin));
            if (e.Canceled) return Task.FromResult(Refuse(UpgradeStatus.Canceled, player, item, g, to, $"canceled by {e.CanceledBy ?? "a handler"}", rule.Id, dry));
            chance = double.IsNaN(e.Chance) ? 0 : Math.Clamp(e.Chance, 0, 1);
            if (!ReferenceEquals(e.Cost, cost))
            {
                cost = new Dictionary<ItemId, int>();
                foreach (var (id, count) in e.Cost ?? Empty)
                    if (count > 0) cost[id] = Math.Min(count, ItemsNative.UpgradeMaxCount);
                if (cost.Count > ItemsNative.UpgradeMaxCosts)
                    return Task.FromResult(Refuse(UpgradeStatus.Refused, player, item, g, to, $"a handler's cost names {cost.Count} items (at most {ItemsNative.UpgradeMaxCosts})", rule.Id, dry));
                foreach (var id in cost.Keys)
                    if (Items.Get(id) is not { IsGear: false })
                        return Task.FromResult(Refuse(UpgradeStatus.Refused, player, item, g, to, $"a handler's cost item {id} is not a plain item the game has", rule.Id, dry));
            }
        }
        var (success, rolled) = dry ? (true, (double?)null) : Decide(chance, options.Guaranteed);
        var payer = options.PayFrom.IsNone ? player : options.PayFrom;
        // a failed roll that keeps the cost has nothing to pay or write: the item is checked once more, nothing else
        var request = RequestOf(player, payer, item, stamp, g, to, step, rule, success, cost, dry)
                      ?? RequestOf(player, payer, item, stamp, g, to, step, rule, false, Empty, dry, check: true)!.Value;
        var pending = new Pending(player, item, g, to, step, rule, cost, chance, rolled, success, mod, origin, dry);
        return Submit(t, request, pending, api);
    }

    /// <summary>The native request (pure): on success the write to the next rarity (one more unlocked with
    /// <see cref="UpgradeStep.Unlock"/>) and the cost; on a failed roll the step's <see cref="UpgradeStep.OnFail"/>: the cost
    /// alone, a downgrade with the cost, or (KeepItemKeepCost) null, and with <paramref name="check"/> a request that only
    /// names the item.</summary>
    internal static unsafe ItemsNative.UpgradeRequest? RequestOf(EntityId player, EntityId payer, ItemInstanceRef item, uint stamp, GearState g, ItemRarity to,
        UpgradeStep step, RarityUpgradeRule rule, bool success, IReadOnlyDictionary<ItemId, int> cost, bool dry, bool check = false)
    {
        // priced for the item's slots now: a second upgrade of the same item in the same step finds them changed
        var r = new ItemsNative.UpgradeRequest { Size = (uint)sizeof(ItemsNative.UpgradeRequest), Player = player.Value, ExpectAvailable = (uint)g.Enhancements };
        r.Gear = new ItemsNative.GearRequest { Size = (uint)sizeof(ItemsNative.GearRequest), Entity = item.Entity.Id.Value, Stamp = stamp, Item = g.Type.Value };
        var pays = !check;
        if (!check)
        {
            Items.GearChange? change = null;
            if (success) change = new((int)to + 1, step.Unlock ? Math.Min(g.Unlocked + 1, (int)to + 1) : null, null);
            else if (step.OnFail == UpgradeFail.KeepItemKeepCost) return null;
            else if (step.OnFail == UpgradeFail.Downgrade && g.Enhancements > 1) change = new(g.Enhancements - 1, null, null);
            if (change is { } c)
            {
                var opts = new GearWriteOptions { Durability = rule.Durability, AllowEmptySlot = rule.AllowEmptySlot };
                r.Gear = Items.GearRequestOf(item, stamp, c, opts);
                r.Gear.Item = g.Type.Value;
                r.Flags |= ItemsNative.UpgradeWrite;
            }
        }
        if (dry) r.Flags |= ItemsNative.UpgradeDryRun;
        if (pays && cost.Count > 0)
        {
            var i = 0;
            foreach (var (id, count) in cost)
            {
                r.Costs[2 * i] = id.Value;
                r.Costs[2 * i + 1] = (uint)count;
                i++;
            }
            r.CostCount = (uint)i;
            r.Payers[0] = payer.Value;
            r.PayerCount = 1;
        }
        return r;
    }

    // ---- in flight ----

    private sealed record Pending(EntityId Player, ItemInstanceRef Item, GearState Before, ItemRarity To, UpgradeStep Step, RarityUpgradeRule Rule,
        IReadOnlyDictionary<ItemId, int> Cost, double Chance, double? Rolled, bool Success, ModContext? Mod, string Origin, bool Dry)
    {
        public TaskCompletionSource<UpgradeResult> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Retired { get; set; }
    }

    private static readonly Dictionary<long, Pending> InFlight = new();
    private static readonly HashSet<ModContext> Watched = new();
    private static long _next;

    private static unsafe Task<UpgradeResult> Submit(ItemsNative.TableV8* t, ItemsNative.UpgradeRequest request, Pending p, string api)
    {
        long key;
        lock (Gate)
        {
            key = ++_next;
            InFlight[key] = p;
            if (p.Mod != null && Watched.Add(p.Mod)) p.Mod.Retiring += () => Retire(p.Mod);
        }
        GearWatch.Begin(p.Item);
        ulong ticket = 0;
        var rc = t->GearUpgrade(0, &request, &OnDone, (void*)key, &ticket);
        if (rc == ItemsNative.Ok) return p.Tcs.Task;
        lock (Gate) InFlight.Remove(key);
        GearWatch.End(p.Item);
        if (rc == ItemsNative.InvalidArgument) throw new ArgumentException($"{api}: kiln.items refused the upgrade's arguments");
        var s = Items.GearStatusOf(t);
        if (s is { Available: true, Pending: >= 1024 })
            return Task.FromResult(Refuse(UpgradeStatus.Refused, p.Player, p.Item, p.Before, p.To, "1024 gear writes are waiting: try again in a step", p.Rule.Id, p.Dry));
        throw new KilnUnavailableException(Items.Caps.GearUpgrade, s?.Detail is { Length: > 0 } d ? d : $"kiln.items refused it (error {rc})");
    }

    private static void Retire(ModContext mod)
    {
        lock (Gate)
        {
            Watched.Remove(mod);
            foreach (var p in InFlight.Values)
                if (p.Mod == mod) p.Retired = true;
        }
    }

    /// <summary>The upgrade's status of a native one (pure): a write that went through is Done for a success and Failed for a
    /// failed roll; the rest by meaning.</summary>
    internal static UpgradeStatus StatusOf(int native, bool success) => native switch
    {
        ItemsNative.GearDone => success ? UpgradeStatus.Done : UpgradeStatus.Failed,
        ItemsNative.GearStale => UpgradeStatus.Stale,
        ItemsNative.GearNotGear => UpgradeStatus.NotGear,
        ItemsNative.GearNoSlot => UpgradeStatus.NoSlot,
        ItemsNative.GearUnavailableStatus => UpgradeStatus.Unavailable,
        ItemsNative.GearDropped => UpgradeStatus.Dropped,
        ItemsNative.UpgradeCantPay => UpgradeStatus.CantPay,
        _ => UpgradeStatus.Refused,  // OUT_OF_RANGE, EQUIPPED, REFUSED, BAD_PAYER, CHANGED: the message says which
    };

    /// <summary>The result of a native one (pure but for the item ref it was asked for).</summary>
    internal static unsafe UpgradeResult ResultOf(in ItemsNative.UpgradeResult n, EntityId player, ItemInstanceRef item, GearState before, ItemRarity to,
        IReadOnlyDictionary<ItemId, int> cost, double chance, double? rolled, bool success, string? rule, bool dry)
    {
        var status = StatusOf(n.Status, success);
        string message;
        fixed (byte* m = n.Gear.Message) message = ItemsNative.Text(m, 160);
        var ran = n.Gear.Before.Item != 0;
        var b = ran ? Items.StateOf(item, in n.Gear.Before) : before;
        var a = ran ? Items.StateOf(item, in n.Gear.After) : before;
        var paid = new Dictionary<ItemId, int>();
        if (n.Status == ItemsNative.GearDone)
        {
            var i = 0;
            foreach (var id in cost.Keys)
            {
                if (i >= n.CostCount || i >= ItemsNative.UpgradeMaxCosts) break;
                if (n.Paid[i] > 0) paid[id] = (int)n.Paid[i];
                i++;
            }
        }
        if (status == UpgradeStatus.Failed && message.Length == 0) message = rolled is { } r ? $"the roll failed ({r:0.###} for a chance of {chance:0.###})" : "the roll failed (a chance of 0)";
        return new UpgradeResult(status, player, item, b.Rarity, to, b, a, paid, chance, rolled, message) { Rule = rule, DryRun = dry };
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void OnDone(ItemsNative.UpgradeResult* r, void* user)
    {
        try
        {
            Complete((long)user, in *r);
        }
        catch (Exception e)
        {
            try
            {
                Backend.Current.Log(0, LogLevel.Error, $"kiln.items: an upgrade's result failed: {e.Message}");
            }
            catch
            {
                // nothing left to say
            }
        }
    }

    /// <summary>A native result arrived (tick thread, Pre): the gear watch and the events hear of it; the socket roll of
    /// <see cref="SocketRule.GameChance"/>; then the asking mod's task, when it is still loaded.</summary>
    internal static unsafe void Complete(long key, in ItemsNative.UpgradeResult n)
    {
        Pending? p;
        lock (Gate)
            if (!InFlight.Remove(key, out p)) return;
        var result = ResultOf(in n, p.Player, p.Item, p.Before, p.To, p.Cost, p.Chance, p.Rolled, p.Success, p.Rule.Id, p.Dry);
        var wrote = !p.Dry && n.Status == ItemsNative.GearDone && (n.Flags & ItemsNative.UpgradeWrite) != 0 && n.Gear.Changed != 0;
        GearWatch.End(p.Item, wrote ? result.After : null);
        if (wrote && result is { Before: { } b, After: { } a }) GearEvents.Changed(p.Item, b, a, GearChangeSource.Upgrade, p.Origin);
        if (!p.Dry && result.Status == UpgradeStatus.Done && p.Rule.Socket == SocketRule.GameChance && result.After is { } after)
        {
            _ = RollSocket(p, result, after);
            return;
        }
        Finish(p, result);
    }

    private static void Finish(Pending p, UpgradeResult result)
    {
        if (!p.Dry && result.Status is UpgradeStatus.Done or UpgradeStatus.Failed && Events.HasSubscribers<GearUpgradedEvent>())
        {
            try
            {
                Events.Post(new GearUpgradedEvent(result, p.Origin));
            }
            catch (Exception e)
            {
                Backend.Current.Log(0, LogLevel.Error, $"kiln.items: posting an upgrade event failed: {e.Message}");
            }
        }
        if (!p.Retired) p.Tcs.TrySetResult(result);
    }

    // SocketRule.GameChance: a weapon without a socket rolls one at its new rarity; a win re-creates it in its slot (kiln.items'
    // REPLACE give, as Gems.AddSocket does). Anything that stops it leaves the upgraded item as it is and says so.
    private static async Task RollSocket(Pending p, UpgradeResult result, GearState after)
    {
        try
        {
            var info = Items.Get(after.Type);
            var r = (int)after.Rarity;
            if (info is not { Category: ItemCategory.Weapons } || after.HasSocket || r < 0 || r >= GameSocketChances.Length)
            {
                Finish(p, result);
                return;
            }
            var (won, _) = Decide(GameSocketChances[r], false);
            if (!won)
            {
                Finish(p, result);
                return;
            }
            var g = ItemSockets.Read(after.Item);
            if (!g.Found || !g.Placed || g.Equipped || g.HasSocket)
            {
                Finish(p, result with { Message = g.Equipped ? "won a socket, but the weapon is equipped: it keeps none" : "won a socket, but the weapon could not be re-created where it is" });
                return;
            }
            var rx = ItemSockets.RecreateRequest(g.Holder, g, after.Rarity, socket: true);
            var res = await ItemSockets.Recreate(g.Holder, after.Item.Entity.Id, rx);
            var made = res.Succeeded ? res.Changes.FirstOrDefault(c => !c.Ref.IsNone).Ref : ItemInstanceRef.None;
            var now = made.IsNone ? null : Items.GearOf(made);
            Finish(p, now == null
                ? result with { Message = $"won a socket, but the re-creation did not take: {res.Message}" }
                : result with { After = now, SocketAdded = true, Message = "won a gem socket: the weapon was re-created with it" });
        }
        catch (Exception e)
        {
            Backend.Current.Log(0, LogLevel.Error, $"kiln.items: an upgrade's socket roll failed: {e.Message}");
            Finish(p, result with { Message = $"the socket roll failed: {e.Message}" });
        }
    }
}
