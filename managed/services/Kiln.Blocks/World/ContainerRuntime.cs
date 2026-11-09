// SPDX-License-Identifier: MIT
// What a container block holds when it is placed (ContainerBlock.DefaultLoot): Kiln rolls the loot table and gives the stacks into the container, once for each container.
//
// The game's own DefaultInventory is no help: it puts its stacks in at placement, twice, and sets the inventory back to them at every load (docs/kiln/world-blocks-plan.md), which would
// eat what a player put in. So Kiln keeps the memory: a container is known by its template and where it stands, and a record of each one filled is kept in the world's own data
// (Kiln.Saves, a bundle of Kiln.Blocks'), so it reaches the disk with the world save that holds the container and its stacks, and a crash gives back both or neither.
//
// Finding the containers: every few steps the world is searched for the templates that have a default loot. This sees a container placed by a player, by code or by the world's
// build, and one that came back from the save, in the same way; it needs no event the game may or may not raise for a prop. A container that is not ready to take items yet is tried again
// at the next search. Server side only (a dedicated server, or a game client's own hosted world).
using System.Text.Json.Serialization;

namespace Kiln;

/// <summary>The containers of a world that were given their default loot: its template and spot. Kept in the world's data by Kiln.Blocks.</summary>
public sealed class FilledContainers : IJsonOnSerializing
{
    /// <summary>"template/x/y/z" (the position in 1/16 m cells) -> the step counter's time, for the record only.</summary>
    [JsonInclude] public Dictionary<string, long> Filled { get; set; } = new();

    /// <summary>A roll in progress. Completed stacks are recorded with the inventory in each world save, so a restart gives only the remaining stacks.</summary>
    [JsonInclude] public Dictionary<string, FillProgress> Pending { get; set; } = new();

    // The shutdown WorldSavingEvent is announced before mods unload; its actual snapshot comes later, after service ticks stop. Drain again
    // when WorldData serializes this bundle, so a last native inventory completion cannot land between that early announcement and the snapshot.
    internal Action? BeforeSnapshot;
    void IJsonOnSerializing.OnSerializing() => BeforeSnapshot?.Invoke();

    public sealed class FillProgress
    {
        public List<LootStack> Stacks { get; set; } = new();
        /// <summary>Canonical item identities captured before a grant or save. Labels stay readable; a block's Was rename keeps the GUID.</summary>
        public Dictionary<string, Guid> ItemGuids { get; set; } = new(StringComparer.Ordinal);
        public int Next { get; set; }
        public int Lost { get; set; }
    }
}

internal static class ContainerRuntime
{
    private sealed record Plan(string Mod, string Block, Guid Template, LootPlan Loot);
    private sealed class Work(Plan plan, EntityId entity, FilledContainers.FillProgress progress)
    {
        public readonly Plan Plan = plan;
        public readonly EntityId Entity = entity;
        public readonly FilledContainers.FillProgress Progress = progress;
        public Task<ItemsResult>? Transaction;
    }

    private const string Key = "containers-filled";
    private const int EveryStep = 20, MaxPerSearch = 4096, MaxSearch = 1 << 20, MaxActive = 256, MaxTries = 12;

    private static readonly object Gate = new();
    private static readonly Dictionary<Guid, Plan> Plans = new();
    private static readonly Dictionary<string, Work> Active = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> Tries = new(StringComparer.Ordinal);
    private static readonly Dictionary<Guid, int> SearchLimits = new();
    private static readonly HashSet<Guid> SearchWarnings = new();
    private static Dictionary<string, Guid>? _legacyAliases;
    private static long _aliasVersion;
    private const int MaxAliasHandles = 65536;
    private static IDisposable? _tick, _saving;
    private static ModContext? _data;
    private static string? _world;
    private static FilledContainers? _book;
    private static bool _peekWarned;

    /// <summary>Containers given their loot since start, and the ones that could not be (for tests and the lab).</summary>
    internal static long Filled, Failed;

    /// <summary>Set by tests that run without a game: where the records are kept, instead of the world's data.</summary>
    internal static FilledContainers? TestBook;

    private static bool ServerSide => Client.IsDedicatedServer || Client.IsHosting;

    /// <summary>A container block with a default loot: containers of this template are filled once. A mod that makes its blocks again replaces it.</summary>
    internal static void Register(ModBlocks mod, Guid template, string block, LootPlan loot)
    {
        lock (Gate)
        {
            Plans[template] = new Plan(mod.Id, block, template, loot);
            _legacyAliases = null;
            _aliasVersion++;
            _tick ??= BlocksBridge.AddTick("kiln blocks containers", OnTick);
        }
    }

    /// <summary>A mod reloads or unloads (<see cref="ModBlocks.Reset"/>): its containers are filled by nothing until its blocks are made again.</summary>
    internal static void Forget(string mod)
    {
        lock (Gate)
        {
            _legacyAliases = null;
            _aliasVersion++;
            foreach (var t in Plans.Where(x => x.Value.Mod == mod).Select(x => x.Key).ToList())
                Plans.Remove(t);
        }
    }

    /// <summary>The key of a container in the record: its template and where it stands (1/16 m cells: a prop never moves, and a position read back from a save is the same to far less).</summary>
    internal static string Spot(Guid template, WorldPosition at) =>
        $"{template:N}/{(long)Math.Round(at.X * 16)}/{(long)Math.Round(at.Y * 16)}/{(long)Math.Round(at.Z * 16)}";

    private static void OnTick(ulong step)
    {
        if (!ServerSide || !Spawn.Available || !Spawn.WorldRunning) return;
        if (!Items.ResultPeekAvailable || !Saves.SnapshotStagingAvailable)
        {
            if (!_peekWarned) BlocksBridge.Log(LogLevel.Warn, "blocks: containers: default loot needs kiln.items' committed-result peek and kiln.saves' final Post staging to save safely; no loot is given on this runtime");
            _peekWarned = true;
            return;
        }
        // A service tick has no mod SynchronizationContext: never await an inventory transaction here. Poll on the tick thread instead.
        _saving ??= Events.On<WorldSavingEvent>(_ => { if (_world == Saves.WorldId && Book(out var records)) Pump(records, submit: false); }, EventPriority.Lowest);
        var world = Saves.WorldId;
        if (_world != world)
        {
            Active.Clear();
            Tries.Clear();
            SearchLimits.Clear();
            SearchWarnings.Clear();
            _world = world;
        }
        if (!Book(out var book)) return;
        Pump(book, submit: true);
        if (step % EveryStep != 11) return;
        Plan[] plans;
        lock (Gate) plans = [.. Plans.Values];
        foreach (var plan in plans)
        {
            if (Active.Count >= MaxActive) break;
            IReadOnlyList<EntityId> found;
            try { found = Find(plan.Template, (template, max) => Spawn.Find(template, null, 0, max)); }
            catch (Exception e) when (e is NotSupportedException or InvalidOperationException) { continue; }
            foreach (var entity in found)
            {
                if (Active.Count >= MaxActive) break;
                if (World.Position(entity) is not { } at) continue;
                var spot = Spot(plan.Template, at);
                bool known;
                lock (Gate) known = book.Filled.ContainsKey(spot) || Active.ContainsKey(spot) || Tries.GetValueOrDefault(spot) >= MaxTries;
                if (known) continue;
                try
                {
                    if (Items.Read(entity) is not { AvailableSlots: > 0 }) { Retry(spot, plan, "its inventory is not there yet"); continue; }
                    if (!book.Pending.TryGetValue(spot, out var progress))
                        book.Pending[spot] = progress = new FilledContainers.FillProgress { Stacks = [.. plan.Loot.Roll(Random.Shared)] };
                    Active[spot] = new Work(plan, entity, progress);
                }
                catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException or KeyNotFoundException) { Retry(spot, plan, e.Message); }
            }
        }
    }

    // One bounded query per search. The fixed native order must not let already-filled entries hide every later one.
    // Spawn.Find's own maximum is 2^20; grow only on later searches, and diagnose that explicit ceiling.
    internal static IReadOnlyList<EntityId> Find(Guid template, Func<Guid, int, IReadOnlyList<EntityId>> find)
    {
        var limit = SearchLimits.GetValueOrDefault(template, MaxPerSearch);
        var found = find(template, limit);
        if (found.Count >= limit)
        {
            if (limit < MaxSearch) SearchLimits[template] = Math.Min(MaxSearch, limit * 2);
            else if (SearchWarnings.Add(template))
                BlocksBridge.Log(LogLevel.Warn, $"blocks: containers: template {template}: discovery reached {MaxSearch} containers; later entities cannot be discovered until that count falls");
        }
        return found;
    }

    private static void BindBook(FilledContainers book)
    {
        if (ReferenceEquals(_book, book)) return;
        // A close/reopen of the same world keeps its GUID but detaches and deserializes its WorldData objects.
        Active.Clear();
        Tries.Clear();
        SearchLimits.Clear();
        SearchWarnings.Clear();
        _book = book;
    }

    // The world's data, as Kiln.Blocks' own bundle: the keys of a mod's own are the mod's, and a service has no mod, so this has a context of its own, as Kiln.Content's missing-content record does.
    private static bool Book(out FilledContainers book)
    {
        if (TestBook != null)
        {
            book = TestBook;
            BindBook(book);
            return true;
        }
        book = null!;
        if (!BlocksBridge.WorldDataReady) return false;
        try
        {
            var records = BlocksBridge.AsData(ref _data, "kiln.blocks", () => WorldData.Get<FilledContainers>(Key));
            records.BeforeSnapshot ??= () => Pump(records, submit: false);
            BindBook(records);
            book = records;
            return true;
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or InvalidDataException)
        {
            BlocksBridge.Log(LogLevel.Warn, $"blocks: containers: the world's data is not usable: {e.Message}");
            return false;
        }
    }

    // Both normal ticks and WorldSavingEvent drain completed transactions. The save handler only records results: it starts no transaction inside a snapshot.
    private static void Pump(FilledContainers book, bool submit)
    {
        foreach (var (spot, work) in Active.ToArray())
        {
            // Only the bundle that owns this roll may harvest it (also when an old world's data is serialized during a world switch).
            if (!book.Pending.TryGetValue(spot, out var progress) || !ReferenceEquals(progress, work.Progress)) continue;
            try
            {
                bool live;
                lock (Gate) live = Plans.TryGetValue(work.Plan.Template, out var plan) && ReferenceEquals(plan, work.Plan);
                // A rolled but not yet submitted stack can be saved too: capture its stable identity before names change.
                if (!submit && work.Transaction == null) CaptureItemGuids(work.Progress);
                var complete = Advance(book, spot, work.Progress, ref work.Transaction,
                    stack => Give(work.Entity, work.Progress, stack), submit && live, out var retry);
                if (!submit && !complete && retry == null && work.Transaction == null) CaptureItemGuids(work.Progress);
                if (retry != null) Retry(spot, work.Plan, retry);
                if (complete)
                {
                    Interlocked.Increment(ref Filled);
                    var text = work.Progress.Stacks.Count == 0 ? "nothing" : string.Join(", ", work.Progress.Stacks.Select(s => $"{s.Item} x{s.Count}"));
                    BlocksBridge.Log(LogLevel.Info, $"blocks: {work.Plan.Mod}: container {work.Plan.Block} {work.Entity} at {spot[(spot.IndexOf('/') + 1)..]}: default loot {text}{(work.Progress.Lost > 0 ? $" ({work.Progress.Lost} did not fit)" : "")}");
                }
                if (complete || retry != null || (!live && work.Transaction == null)) Active.Remove(spot);
            }
            catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException or KeyNotFoundException or OperationCanceledException)
            {
                Retry(spot, work.Plan, e.Message);
                Active.Remove(spot);
            }
        }
    }

    internal static void CaptureItemGuids(FilledContainers.FillProgress progress)
    {
        for (var i = progress.Next; i < progress.Stacks.Count; i++)
        {
            var label = progress.Stacks[i].Item;
            if (progress.ItemGuids.ContainsKey(label)) continue;
            var item = Items.Lookup(label);
            if (item == null && LegacyAlias(label) is { } alias && alias != Guid.Empty)
                item = Items.Get(new ItemId(BlockIds.Fnv(alias))) is { } candidate && candidate.Guid == alias ? candidate : null;
            if (item == null || item.Guid == Guid.Empty || item.Id.Value == 0 || BlockIds.Fnv(item.Guid) != item.Id.Value)
                throw new InvalidOperationException($"pending default loot item '{label}' has no unambiguous native GUID; its roll is kept unchanged");
            progress.ItemGuids[label] = item.Guid;
        }
    }

    private static Task<ItemsResult> Give(EntityId entity, FilledContainers.FillProgress progress, LootStack stack)
    {
        CaptureItemGuids(progress);
        var guid = progress.ItemGuids[stack.Item];
        var id = new ItemId(BlockIds.Fnv(guid));
        if (guid == Guid.Empty || id.Value == 0 || Items.Get(id) is not { } current || current.Guid != guid)
            throw new InvalidOperationException($"pending default loot item '{stack.Item}' GUID {guid} is not in the native item registry; its roll is kept unchanged");
        return Items.Give(entity, id, stack.Count, options: GiveOptions.None);
    }

    // Legacy records had only debug names. Build one bounded index per mod generation, not a scan for every missing stack
    // on every tick. Only explicit Was aliases qualify; two different canonical GUIDs for one old name are refused.
    private static Guid? LegacyAlias(string label)
    {
        Dictionary<string, Guid>? aliases;
        long version;
        lock (Gate) { aliases = _legacyAliases; version = _aliasVersion; }
        if (aliases == null)
        {
            aliases = new(StringComparer.Ordinal);
            var count = 0;
            foreach (var handle in Blocks.AllMods.SelectMany(mod => mod.Handles.Values))
            {
                if (++count > MaxAliasHandles)
                {
                    aliases.Clear();
                    BlocksBridge.Log(LogLevel.Warn, "blocks: containers: too many block handles for legacy loot aliases; unresolved pending rolls are kept unchanged");
                    break;
                }
                var block = handle.GetType().GetProperty("Block")?.GetValue(handle);
                if (block?.GetType().GetProperty("Was")?.GetValue(block) is not string { Length: > 0 } was) continue;
                var name = handle.Mod + "." + was;
                if (aliases.TryGetValue(name, out var previous) && previous != handle.Guid) aliases[name] = Guid.Empty;
                else aliases.TryAdd(name, handle.Guid);
            }
            lock (Gate)
            {
                if (version != _aliasVersion) return null; // a concurrent reload changed what the names mean; retry next search
                _legacyAliases = aliases;
            }
        }
        return aliases.TryGetValue(label, out var guid) ? guid : null;
    }

    /// <summary>Advances one persisted roll on the calling tick. No continuation touches game state. Tests use completed/delayed transactions to exercise save boundaries.</summary>
    internal static bool Advance(FilledContainers book, string spot, FilledContainers.FillProgress progress, ref Task<ItemsResult>? transaction,
        Func<LootStack, Task<ItemsResult>> give, bool submit, out string? retry)
    {
        retry = null;
        if (transaction != null)
        {
            if (!Items.TryGetResult(transaction, out var result)) return false; // observes committed native work even before its callback; never blocks
            transaction = null;
            // NoRoom deliberately loses this stack; temporary failures retain this exact roll and retry only this stack.
            if (result.Status is not (ItemsStatus.Done or ItemsStatus.Partial or ItemsStatus.NoRoom))
            {
                retry = result.Message.Length > 0 ? result.Message : result.Status.ToString();
                return false;
            }
            progress.Lost += Math.Max(0, result.Requested - result.Done);
            progress.Next++;
        }
        if (progress.Next >= progress.Stacks.Count)
        {
            book.Filled[spot] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            book.Pending.Remove(spot);
            return true;
        }
        if (submit) transaction = give(progress.Stacks[progress.Next]);
        return false;
    }

    private static void Retry(string spot, Plan plan, string why)
    {
        int n;
        lock (Gate) Tries[spot] = n = Tries.GetValueOrDefault(spot) + 1;
        if (n < MaxTries) return;
        Interlocked.Increment(ref Failed);
        BlocksBridge.Log(LogLevel.Warn, $"blocks: {plan.Mod}: container {plan.Block}: no default loot given after {n} tries ({why}); it is left alone until the next start");
    }
}
