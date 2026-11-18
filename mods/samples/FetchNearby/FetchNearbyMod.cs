// SPDX-License-Identifier: MIT
// Fetch nearby: /fetch <item> [count] brings items from the containers within 12 m into the player's inventory.
//
// The search for containers is the heavy part (a walk over the world's entities), so it runs in a Kiln task, on one of
// Kiln's worker threads, through the task-thread world reads (WorldConcurrent). Where those are not available (a game
// build Kiln has no exact profile for) the same search runs on the tick instead. What a container holds and every item
// that moves are the tick's: the task hops back to read the contents, and the mod's own tick code moves the items once
// the task has handed over its plan. A fetch that is cancelled before then (a new /fetch of the same player, the mod
// unloading) moves nothing. Items that fit neither the player nor, given back, their container wait in the mod and are
// placed as soon as one of them has room. docs/kiln/modding/tasks.md walks through it.
using System.Runtime.InteropServices;
using Kiln;

namespace FetchNearby;

public sealed class FetchNearbyMod : Mod
{
    private const double Reach = 12;  // metres

    // the fetch each player has running (tick thread only)
    private readonly Dictionary<EntityId, KilnTask> _running = [];

    // items taken that fit neither the player nor their container: tried again every step (tick thread only)
    private readonly List<Waiting> _waiting = [];

    private sealed class Waiting(EntityId player, EntityId container, ItemId item, string name, int maxStack, int count)
    {
        public readonly EntityId Player = player, Container = container;
        public readonly ItemId Item = item;
        public readonly string Name = name;
        public readonly int MaxStack = maxStack;
        public int Count = count;
    }

    /// <summary>CurrentTransform starts with where the entity stands: x, y and z as 32.32 fixed-point metres.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Position
    {
        public long X, Y, Z;

        public readonly WorldPosition ToWorld() => new(X / 4294967296.0, Y / 4294967296.0, Z / 4294967296.0);
    }

    /// <summary>A container that holds some of the item, and how many it held when the task looked.</summary>
    private readonly record struct Source(EntityId Container, int Has);

    public override void OnUnload()
    {
        // Kiln cancels an unloading mod's tasks anyway; cancelling them here says so in the mod's own code
        foreach (var task in _running.Values) task.Cancel();
        _running.Clear();
        // what still waits for room cannot be kept past the mod: say so rather than lose it quietly
        foreach (var w in _waiting)
            Log.Warn($"fetch for {w.Player}: {w.Count} x {w.Name} were still waiting for room in the player or container {w.Container} when the mod unloaded; they are lost");
        _waiting.Clear();
    }

    [Command("fetch", Description = "bring items from the containers within 12 m into your inventory")]
    private string Fetch(CommandContext c, string item, int count = 1)
    {
        if (!c.Source.IsPlayer) throw new CommandException("only a player can fetch");
        if (count < 1) throw new CommandException("the count is at least 1");
        if (!Items.Available || !World.Available || !Chat.Available)
            throw new CommandException("fetching needs kiln.items, kiln.world and kiln.chat, which are not all available on this server");
        var info = Items.Lookup(item) ?? throw new CommandException($"there is no item called {item}");
        var player = c.Source.Player;
        var at = World.Position(player) ?? throw new CommandException("you are not in the world yet");
        // one fetch per player: a new one replaces the last
        if (_running.Remove(player, out var last)) last.Cancel();
        var search = Context.Tasks.Run("fetch-search", ctx => SearchAsync(ctx, player, at, info.Id));
        _running[player] = search;
        _ = DeliverAsync(player, search, info, count);
        return $"looking for {count} x {info.Name} within {Reach:0} m";
    }

    // ---- the task: on a worker, then on the tick for the contents ----

    private static async Task<IReadOnlyList<Source>> SearchAsync(KilnTaskContext ctx, EntityId player, WorldPosition at, ItemId item)
    {
        // The containers within reach, nearest first: from this worker where the reads work, else on the tick. A
        // search from the task that finds the reads gone after all says null, and the tick does it.
        var near = await WorldConcurrent.OrOnTick(ctx, () => NearFromTask(at, ctx.Cancellation), () => NearOnTick(at))
                   ?? await ctx.OnTick(() => NearOnTick(at));
        ctx.Cancellation.ThrowIfCancellationRequested();
        // a word for the player, on the tick when it comes; the task goes on at once
        var found = near.Count;
        ctx.PostToTick(() => Chat.Tell(player, found == 0 ? $"no container within {Reach:0} m" : $"searching {found} container(s) within {Reach:0} m"));
        if (found == 0) return [];
        // what a container holds is the tick's to read
        await ctx.ToTick();
        var sources = new List<Source>();
        foreach (var container in near)
            if (Items.Read(container)?.Count(item) is > 0 and var has) sources.Add(new Source(container, has));
        return sources;
    }

    /// <summary>The containers within reach of <paramref name="at"/>, nearest first, read from a task's worker; null when
    /// the reads are not available here.</summary>
    private static IReadOnlyList<EntityId>? NearFromTask(WorldPosition at, CancellationToken cancellation)
    {
        var refs = new ConcurrentEntity[64];
        var query = new EntityQuery(WorldKind.Server, "Inventory") { Center = at, Radius = Reach };
        for (var attempt = 0; attempt < 5; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            var result = WorldConcurrent.Query(query, refs);
            switch (result.Status)
            {
                case ReadStatus.Ok:
                    var near = new List<(EntityId Id, double Distance)>();
                    for (var i = 0; i < result.Count; i++)
                    {
                        // a container that went or changed since the query is left out (Stale, Absent)
                        var where = WorldConcurrent.Read<Position>(refs[i], "CurrentTransform");
                        if (where.IsOk) near.Add((refs[i].Id, where.Value.ToWorld().DistanceTo(at)));
                    }
                    return [.. near.OrderBy(n => n.Distance).Select(n => n.Id)];
                case ReadStatus.TooSmall:
                    refs = new ConcurrentEntity[result.Required + 16];  // more containers than places: ask again with room
                    continue;
                case ReadStatus.Changed:
                    continue;  // a container came or went during the walk: walk again
                default:
                    return null;  // Unavailable here: the tick searches
            }
        }
        return null;
    }

    /// <summary>The same search on the tick: every inventory of a container, by distance.</summary>
    private static IReadOnlyList<EntityId> NearOnTick(WorldPosition at) => Nearby.Within(Items.FindInventories(), at, Reach);

    // ---- the mod's own tick code: it moves the items once the task has handed over its plan ----

    private async Task DeliverAsync(EntityId player, KilnTask<IReadOnlyList<Source>> search, ItemInfo info, int count)
    {
        var (item, name) = (info.Id, info.Name);
        IReadOnlyList<Source> sources;
        try
        {
            sources = await search;
        }
        catch (OperationCanceledException)
        {
            return;  // cancelled before the plan was made: nothing moved
        }
        catch (Exception e)
        {
            Log.Warn($"fetch for {player} failed: {e.Message}");
            if (Chat.Available) Chat.Tell(player, "the fetch failed (see the server log)");
            return;
        }
        finally
        {
            if (_running.TryGetValue(player, out var now) && now == search) _running.Remove(player);
        }
        var moved = 0;
        foreach (var source in sources)
        {
            if (moved >= count) break;
            // all or nothing: a container that changed since the task looked keeps what it has
            var taken = await Items.Take(source.Container, item, Math.Min(source.Has, count - moved));
            if (!taken.Succeeded) continue;
            var given = await Items.Give(player, item, taken.Done);
            moved += given.Done;
            if (given.Done < taken.Done)
            {
                // no room: back where it came from; if the container filled up meanwhile, they wait for room
                var rest = taken.Done - given.Done;
                var back = await Items.Give(source.Container, item, rest);
                if (back.Done < rest)
                {
                    var waiting = new Waiting(player, source.Container, item, name, info.MaxStack, rest - back.Done);
                    _waiting.Add(waiting);
                    if (Chat.Available)
                        Chat.Tell(player, $"{waiting.Count} {name} fit neither you nor the container; they go to whichever has room first");
                    _ = PlaceAsync(waiting);
                }
                break;
            }
        }
        if (Chat.Available) Chat.Tell(player, moved == 0 ? $"no {name} found within {Reach:0} m" : $"fetched {moved} of {count} {name}");
    }

    /// <summary>Items that fit nowhere: every step, into the player when the player has room, else back into the container
    /// when it has, until all are placed (or the mod unloads: OnUnload says what was left). A give is only asked for where
    /// a read of the inventory shows room, so none waits in kiln.items while there is none.</summary>
    private async Task PlaceAsync(Waiting w)
    {
        while (w.Count > 0)
        {
            await Context.Scheduler.NextTick();
            if (!_waiting.Contains(w)) return;  // the mod is unloading
            if (HasRoom(w.Player, w))
            {
                var toPlayer = await Items.Give(w.Player, w.Item, w.Count);
                w.Count -= toPlayer.Done;
                if (toPlayer.Done > 0 && Chat.Available) Chat.Tell(w.Player, $"{toPlayer.Done} {w.Name} that were waiting are in your inventory now");
            }
            else if (HasRoom(w.Container, w))
            {
                var toContainer = await Items.Give(w.Container, w.Item, w.Count);
                w.Count -= toContainer.Done;
            }
        }
        _waiting.Remove(w);
    }

    // an empty slot, or a stack of the item with room on it
    private static bool HasRoom(EntityId target, Waiting w) =>
        Items.Read(target) is { } inv && inv.Slots.Any(s => (s.IsEmpty && s.Category == InventoryCategory.Generic) || (s.Item == w.Item && s.Count < w.MaxStack));
}
