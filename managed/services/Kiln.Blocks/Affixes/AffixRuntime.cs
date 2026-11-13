// SPDX-License-Identifier: MIT
// The engine behind Affixes (dev.27).
//
// Definitions: affixes and pools by "mod:name", gone when their mod unloads (the rolls on items stay).
// Storage: one item data key of Kiln's own (owner "kiln-affixes", key "affixes", synced to players near the item): the
// item's rolls as { "Rolls": [ { "Id", "Name", "Stat", "Tier", "Value" } ] }.
// Effect: a pass about twice a second derives, for every player, the stat changes of what is worn and in hand, and makes
// what Kiln holds on the player equal to it. One stat change is one instance of a Kiln Impact program on the game's
// generic impact entity: applyAttributeValue(attribute, op, value) on the player in its code, the matching
// revertApplyAttributeValue in its shutdown code, which the game runs when the entity is destroyed. That is the pair the
// game's own buffs use (docs/re/impact.md), with the attribute, the op and the value as config values of the instance,
// so two programs (whole numbers and shares) serve every stat and every value.
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Kiln;

/// <summary>The rolls of one item, as they are stored and sent.</summary>
internal sealed class AffixData
{
    public List<AffixRoll> Rolls { get; set; } = [];
}

internal sealed class AffixRoll
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Stat { get; set; } = "";
    public int Tier { get; set; }
    public double Value { get; set; }
}

internal sealed record AffixDef(string Id, string Mod, AffixHandle Handle, AffixBlock Block);

internal sealed record PoolDef(string Id, string Mod, AffixPoolHandle Handle, AffixPoolBlock Block, IReadOnlyList<(string Affix, double Weight)> Entries);

internal static partial class AffixRuntime
{
    internal const int PollEverySteps = 10, CapsEverySteps = 25, ProbeTimeoutSteps = 50;
    internal const uint ProbeTag = 0xAF1C0001;
    private const uint ProgramType = 0xd4922d88;

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex NameRule();

    private static readonly JsonSerializerOptions FileJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed class FileModel
    {
        public int Format { get; set; }
        public Dictionary<string, AffixBlock>? Affixes { get; set; }
        public Dictionary<string, AffixPoolBlock>? Pools { get; set; }
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, AffixDef> Defs = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, PoolDef> Pools = new(StringComparer.Ordinal);
    private static readonly HashSet<ModContext> Watched = new(ReferenceEqualityComparer.Instance);
    private static ModContext? _owner;
    private static bool _started;

    /// <summary>How near an item's holder a player's game gets its rolls, in metres: a chest one stands at, a player one meets.</summary>
    internal const double SyncRadius = 16;

    // ------------------------------------------------------------------------------------------- definitions ----

    internal static ModContext CallerMod(string api)
    {
        Services.RequireTickThread(api);
        return ModContext.CurrentMod ?? throw new InvalidOperationException($"{api} must be called from a mod's code (OnLoad or its handlers)");
    }

    /// <summary>"name" of the calling mod as "mod:name"; "mod:name" as it is.</summary>
    internal static string Qualify(string name) =>
        name.Contains(':') ? name : ModContext.CurrentMod is { } mod ? $"{mod.Info.Id}:{name}" : name;

    internal static AffixDef? FindAffix(string id)
    {
        lock (Gate) return Defs.GetValueOrDefault(id);
    }

    internal static PoolDef? FindPool(string id)
    {
        lock (Gate) return Pools.GetValueOrDefault(id);
    }

    private static void Watch(ModContext mod)
    {
        lock (Gate)
            if (!Watched.Add(mod)) return;
        mod.Retiring += () =>
        {
            var id = mod.Info.Id;
            lock (Gate)
            {
                Watched.Remove(mod);
                foreach (var k in Defs.Where(d => d.Value.Mod == id).Select(d => d.Key).ToArray()) Defs.Remove(k);
                foreach (var k in Pools.Where(d => d.Value.Mod == id).Select(d => d.Key).ToArray()) Pools.Remove(k);
            }
            _dirty = true;  // its affixes stop acting at the next pass
        };
    }

    internal static AffixHandle Define(ModContext mod, string name, AffixBlock block)
    {
        var what = $"affix {name}";
        if (name is null || !NameRule().IsMatch(name)) throw new BlockError(what, null, "a name is lower-case letters, digits, '-' and '_', at most 64, starting with a letter or digit");
        if (string.IsNullOrWhiteSpace(block.Name) || block.Name.Length > 64) throw new BlockError(what, "name", "the display name is 1 to 64 characters");
        if (block.Stat is null) throw new BlockError(what, "stat", "an affix changes a stat");
        if (block.Group is { } g && (g.Length == 0 || g.Length > 64)) throw new BlockError(what, "group", "a group is 1 to 64 characters");
        if (block.Tiers is not { Count: >= 1 and <= 16 }) throw new BlockError(what, "tiers", "an affix has 1 to 16 tiers");
        for (var i = 0; i < block.Tiers.Count; i++)
        {
            var t = block.Tiers[i] ?? throw new BlockError(what, $"tiers[{i}]", "a tier is missing");
            var at = $"tiers[{i}]";
            if (!double.IsFinite(t.Min) || !double.IsFinite(t.Max)) throw new BlockError(what, at, "min and max are numbers");
            if (t.Min == 0 || t.Max == 0 || Math.Sign(t.Min) != Math.Sign(t.Max)) throw new BlockError(what, at, $"{t.Min} to {t.Max}: a range never holds 0 (0 changes nothing) and keeps one sign");
            if (t.Max < t.Min) throw new BlockError(what, $"{at}.max", $"{t.Max} is less than min {t.Min}");
            if (block.Stat.Whole && (t.Min != Math.Floor(t.Min) || t.Max != Math.Floor(t.Max))) throw new BlockError(what, at, $"{block.Stat.Key} takes whole numbers");
            if (t.Step is { } step && (!double.IsFinite(step) || step <= 0 || (block.Stat.Whole && step != Math.Floor(step)))) throw new BlockError(what, $"{at}.step", $"{step}: a step is more than 0{(block.Stat.Whole ? ", and whole for this stat" : "")}");
            if (!double.IsFinite(t.Weight) || t.Weight < 0) throw new BlockError(what, $"{at}.weight", $"{t.Weight}: a weight is 0 or more");
            if (t.MinItemLevel < 0) throw new BlockError(what, $"{at}.minItemLevel", $"{t.MinItemLevel}: an item level is 0 or more");
        }
        if (block.Tiers.All(t => t.Weight <= 0)) throw new BlockError(what, "tiers", "no tier has a weight above 0: nothing could roll");
        if (block.Tiers.All(t => t.MinItemLevel > 0)) throw new BlockError(what, "tiers", "every tier asks an item level: give one tier minItemLevel 0, so the affix can roll on any item of its pool");
        var handle = new AffixHandle(mod.Info.Id, name, block);
        Watch(mod);
        lock (Gate) Defs[handle.Id] = new AffixDef(handle.Id, mod.Info.Id, handle, block);
        EnsureStarted();
        _dirty = true;
        return handle;
    }

    internal static AffixPoolHandle DefinePool(ModContext mod, string name, AffixPoolBlock block)
    {
        var what = $"affix pool {name}";
        if (name is null || !NameRule().IsMatch(name)) throw new BlockError(what, null, "a name is lower-case letters, digits, '-' and '_', at most 64, starting with a letter or digit");
        if (block.Affixes is not { Count: >= 1 and <= 256 }) throw new BlockError(what, "affixes", "a pool has 1 to 256 affixes");
        if (block.Min < 0 || block.Min > Affixes.MaxPerItem) throw new BlockError(what, "min", $"{block.Min}: 0 to {Affixes.MaxPerItem}");
        if (block.Max < block.Min || block.Max > Affixes.MaxPerItem) throw new BlockError(what, "max", $"{block.Max}: at least min ({block.Min}), at most {Affixes.MaxPerItem}");
        if (block.Items is { Count: 0 }) throw new BlockError(what, "items", "lists no item: name at least one, or leave it out for any item");
        var entries = new List<(string, double)>();
        for (var i = 0; i < block.Affixes.Count; i++)
        {
            var e = block.Affixes[i] ?? throw new BlockError(what, $"affixes[{i}]", "an entry is missing");
            if (string.IsNullOrWhiteSpace(e.Affix)) throw new BlockError(what, $"affixes[{i}].affix", "an entry names an affix");
            var id = e.Affix.Contains(':') ? e.Affix : $"{mod.Info.Id}:{e.Affix}";
            if (FindAffix(id) is null) throw new BlockError(what, $"affixes[{i}].affix", $"no affix \"{id}\" is defined (define the affixes before the pool; is the other mod loaded before this one?)");
            if (!double.IsFinite(e.Weight) || e.Weight < 0) throw new BlockError(what, $"affixes[{i}].weight", $"{e.Weight}: a weight is 0 or more");
            if (entries.Any(x => x.Item1 == id)) throw new BlockError(what, $"affixes[{i}].affix", $"\"{id}\" is in the pool twice");
            entries.Add((id, e.Weight));
        }
        if (entries.All(e => e.Item2 <= 0)) throw new BlockError(what, "affixes", "no entry has a weight above 0: nothing could roll");
        var handle = new AffixPoolHandle(mod.Info.Id, name, block);
        Watch(mod);
        lock (Gate) Pools[handle.Id] = new PoolDef(handle.Id, mod.Info.Id, handle, block, entries);
        EnsureStarted();
        return handle;
    }

    internal static AffixFile Load(ModContext mod, string json)
    {
        FileModel file;
        try
        {
            file = JsonSerializer.Deserialize<FileModel>(json, FileJson) ?? throw new JsonException("the file is empty");
        }
        catch (JsonException e)
        {
            throw new BlockError("affix file", e.Path, e.Message);
        }
        if (file.Format != 1) throw new BlockError("affix file", "format", $"{file.Format}: this Kiln reads format 1");
        var affixes = (file.Affixes ?? []).Select(a => Define(mod, a.Key, a.Value ?? throw new BlockError($"affix {a.Key}", null, "is null"))).ToList();
        var pools = (file.Pools ?? []).Select(p => DefinePool(mod, p.Key, p.Value ?? throw new BlockError($"affix pool {p.Key}", null, "is null"))).ToList();
        return new AffixFile(affixes, pools);
    }

    // ------------------------------------------------------------------------------------------------ storage ----

    private static ModContext Owner => _owner ??= HostAccess.CreateContext(
        new ModInfo(Affixes.DataMod, "Kiln affixes", "1.0.0", Path.Combine(KilnDir(), "host")), 0, Path.Combine(KilnDir(), "data", Affixes.DataMod));

    private static string KilnDir()
    {
        var host = Path.GetDirectoryName(typeof(AffixRuntime).Assembly.Location) ?? ".";
        return Path.GetDirectoryName(host) ?? host;
    }

    /// <summary>The item data key of the rolls, Kiln's own. Asked for at every use: the same name gives the same key, and a
    /// store that started over (the test kit's next session) gets it declared again. Tick thread.</summary>
    private static InstanceKey<AffixData> Key
    {
        get
        {
            var outer = ModContext.CurrentMod;
            ModContext.CurrentMod = Owner;
            try
            {
                return InstanceData.Key<AffixData>(Affixes.DataKey, new InstanceKeyOptions { Sync = InstanceSync.Nearby, NearbyRadius = SyncRadius });
            }
            finally
            {
                ModContext.CurrentMod = outer;
            }
        }
    }

    /// <summary>Runs a call of the item API: the tick thread, the engine started, and item data's refusal named as affixes'.</summary>
    internal static T Guarded<T>(string api, Func<InstanceKey<AffixData>, T> body)
    {
        Services.RequireTickThread(api);
        EnsureStarted();
        try
        {
            return body(Key);
        }
        catch (KilnUnavailableException e) when (e.Capability != Affixes.Capability)
        {
            throw new KilnUnavailableException(Affixes.Capability, $"needs {e.Capability}: {e.Reason ?? "off"}", e);
        }
    }

    internal static IReadOnlyList<RolledAffix> Read(ItemInstanceRef item, InstanceKey<AffixData> key) =>
        item.TryGet(key, out var data) ? ToRolled(data) : [];

    private static IReadOnlyList<RolledAffix> ToRolled(AffixData data) =>
        data.Rolls.Where(r => r is { Id.Length: > 0 }).Select(r => new RolledAffix(r.Id, r.Name, r.Stat, r.Tier, r.Value)).ToList();

    /// <summary>The server's JSON of an item's rolls, on a client; null when it is not that.</summary>
    internal static IReadOnlyList<RolledAffix>? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AffixData>(json) is { } data ? ToRolled(data) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AffixResult Write(ItemInstanceRef item, InstanceKey<AffixData> key, IReadOnlyList<RolledAffix> before, IReadOnlyList<RolledAffix> now)
    {
        if (now.Count == 0) item.Remove(key);
        else item.Set(key, new AffixData { Rolls = now.Select(a => new AffixRoll { Id = a.Affix, Name = a.Name, Stat = a.Stat, Tier = a.Tier, Value = a.Value }).ToList() });
        _dirty = true;  // what is worn may act differently now: the next step's pass sees it
        if (Events.HasSubscribers<AffixesChangedEvent>()) Events.Post(new AffixesChangedEvent(item, before, now));
        return new AffixResult(item, now, null);
    }

    private static ArgumentException Stale(ItemInstanceRef item) =>
        new($"{item} is stale: the item is gone, or its id belongs to another item now", nameof(item));

    /// <summary>The item's level, for the tiers that ask one (0 when it cannot be read): kiln.items' read of the slot it
    /// sits in. A seam: the offline tests have no kiln.items.</summary>
    internal static Func<ItemInstanceRef, int> LevelOf = static item =>
    {
        if (item.Location() is not { IsPlaced: true } at) return 0;
        return Items.Read(at.Holder)?.Slots.FirstOrDefault(s => s.Slot == at.Slot)?.Level ?? 0;
    };

    private static double RollValue(AffixTier tier, Stat stat, Random rng)
    {
        var step = tier.Step ?? (stat.Whole ? 1 : 0.01);
        var steps = (int)Math.Floor((tier.Max - tier.Min) / step + 1e-9);
        var v = tier.Min + step * rng.Next(0, steps + 1);
        return stat.Whole ? Math.Round(v) : Math.Round(v, 6);
    }

    private static T? Pick<T>(IReadOnlyList<(T Item, double Weight)> from, Random rng) where T : class
    {
        var total = from.Sum(x => Math.Max(0, x.Weight));
        if (total <= 0) return null;
        var at = rng.NextDouble() * total;
        foreach (var (item, weight) in from)
        {
            if (weight <= 0) continue;
            at -= weight;
            if (at < 0) return item;
        }
        return from.Last(x => x.Weight > 0).Item;
    }

    private static RolledAffix? RollOne(AffixDef def, int level, Random rng)
    {
        var tiers = new List<(AffixTier, double)>();
        for (var i = 0; i < def.Block.Tiers.Count; i++)
            if (def.Block.Tiers[i].MinItemLevel <= level) tiers.Add((def.Block.Tiers[i], def.Block.Tiers[i].Weight));
        if (Pick(tiers, rng) is not { } tier) return null;
        var index = 0;
        for (var i = 0; i < def.Block.Tiers.Count; i++)
            if (ReferenceEquals(def.Block.Tiers[i], tier)) index = i;
        return new RolledAffix(def.Id, def.Block.Name, def.Block.Stat.Key, index + 1, RollValue(tier, def.Block.Stat, rng));
    }

    private static readonly HashSet<string> NotedItems = new(StringComparer.Ordinal);

    private static bool Allows(PoolDef pool, ItemId item)
    {
        if (pool.Block.Items is not { } items) return true;
        foreach (var r in items)
        {
            var id = LootRuntime.IdOf(r.Value) ?? LootRuntime.IdOf($"{pool.Mod}.{r.Value}");
            if (id is null)
            {
                bool first;
                lock (Gate) first = NotedItems.Add($"{pool.Id}|{r.Value}");
                if (first) Backend.Current.Log(0, LogLevel.Warn, $"affixes: pool {pool.Id}: the game has no item \"{r.Value}\" (yet); it rolls on none");
                continue;
            }
            if (id.Value == item.Value) return true;
        }
        return false;
    }

    internal static AffixResult Roll(ItemInstanceRef item, InstanceKey<AffixData> key, AffixPoolHandle handle, Random rng, bool replace)
    {
        var pool = FindPool(handle.Id);
        if (pool is null || !ReferenceEquals(pool.Handle, handle))
            throw new ArgumentException($"affix pool {handle.Id} belongs to a mod instance that has unloaded; define it again", nameof(handle));
        if (item.IsNone) throw new ArgumentException("no item (ItemInstanceRef.None)", nameof(item));
        var before = Read(item, key);
        if (!Allows(pool, item.Item)) return new AffixResult(item, before, $"pool {pool.Id} is not for item type {item.Item}");
        if (before.Count > 0 && !replace) return new AffixResult(item, before, "the item already has affixes (replace: true rolls again)");
        var level = LevelOf(item);
        var left = new List<(AffixDef, double)>();
        foreach (var (id, weight) in pool.Entries)
            if (FindAffix(id) is { } def) left.Add((def, weight));  // an affix whose mod has unloaded since does not come up
        var count = rng.Next(pool.Block.Min, pool.Block.Max + 1);
        var rolled = new List<RolledAffix>();
        var groups = new HashSet<string>(StringComparer.Ordinal);
        while (rolled.Count < count && Pick(left, rng) is { } def)
        {
            left.RemoveAll(x => ReferenceEquals(x.Item1, def));
            if (def.Block.Group is { } g && groups.Contains(g)) continue;
            if (RollOne(def, level, rng) is not { } one) continue;  // no tier for this item level
            if (def.Block.Group is { } group) groups.Add(group);
            rolled.Add(one);
        }
        if (rolled.Count == 0 && before.Count == 0) return new AffixResult(item, before, null);  // the pool gave nothing this time: nothing to store
        return Write(item, key, before, rolled);
    }

    private static void CheckValue(double value, bool whole, string name)
    {
        if (!double.IsFinite(value) || value == 0) throw new ArgumentException($"{value} is no affix value: a number that is not 0", name);
        if (whole && value != Math.Floor(value)) throw new ArgumentException($"{value}: this stat takes whole numbers", name);
    }

    internal static AffixResult Add(ItemInstanceRef item, InstanceKey<AffixData> key, string id, int tier, double? value, Random rng)
    {
        var def = FindAffix(id) ?? throw new ArgumentException($"no loaded mod defines an affix \"{id}\"", "affix");
        if (tier < 1 || tier > def.Block.Tiers.Count) throw new ArgumentException($"affix {id} has tiers 1 to {def.Block.Tiers.Count}, not {tier}", nameof(tier));
        if (item.IsNone) throw new ArgumentException("no item (ItemInstanceRef.None)", nameof(item));
        var v = value ?? RollValue(def.Block.Tiers[tier - 1], def.Block.Stat, rng);
        CheckValue(v, def.Block.Stat.Whole, nameof(value));
        var before = Read(item, key);
        if (before.Any(a => a.Affix == id)) return new AffixResult(item, before, $"the item already has {id}");
        if (def.Block.Group is { } g && before.FirstOrDefault(a => FindAffix(a.Affix)?.Block.Group == g) is { } same)
            return new AffixResult(item, before, $"the item already has {same.Affix}, of the same group \"{g}\"");
        if (before.Count >= Affixes.MaxPerItem) return new AffixResult(item, before, $"the item has {before.Count} affixes already (at most {Affixes.MaxPerItem})");
        return Write(item, key, before, [.. before, new RolledAffix(id, def.Block.Name, def.Block.Stat.Key, tier, v)]);
    }

    internal static AffixResult Set(ItemInstanceRef item, InstanceKey<AffixData> key, IReadOnlyList<RolledAffix> affixes)
    {
        if (item.IsNone) throw new ArgumentException("no item (ItemInstanceRef.None)", nameof(item));
        if (affixes.Count > Affixes.MaxPerItem) throw new ArgumentException($"{affixes.Count} affixes; an item carries at most {Affixes.MaxPerItem}", nameof(affixes));
        foreach (var a in affixes)
        {
            if (a is null) throw new ArgumentException("an affix is null", nameof(affixes));
            var colon = a.Affix?.IndexOf(':') ?? -1;
            if (colon <= 0 || colon == a.Affix!.Length - 1) throw new ArgumentException($"\"{a.Affix}\" is no affix id: \"mod:name\"", nameof(affixes));
            if (affixes.Count(x => x.Affix == a.Affix) > 1) throw new ArgumentException($"{a.Affix} is in the list twice", nameof(affixes));
            CheckValue(a.Value, FindAffix(a.Affix)?.Block.Stat.Whole ?? false, nameof(affixes));
        }
        var before = Read(item, key);
        if (affixes.Count == 0 && before.Count == 0) return new AffixResult(item, before, null);
        return Write(item, key, before, affixes.ToList());
    }

    /// <summary>Takes one affix (or all: <paramref name="id"/> null) off the item. False: nothing to take.</summary>
    internal static bool Remove(ItemInstanceRef item, InstanceKey<AffixData> key, string? id)
    {
        if (item.IsNone) throw new ArgumentException("no item (ItemInstanceRef.None)", nameof(item));
        var before = Read(item, key);
        var now = id is null ? [] : before.Where(a => a.Affix != id).ToList();
        if (now.Count == before.Count) return false;
        Write(item, key, before, now);
        return true;
    }
}
