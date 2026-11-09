// SPDX-License-Identifier: MIT
// World progress: typed access to what the game stores about a world and its characters: the flame level,
// the difficulty, Ancient Spires, Flame Shrines and Sanctums, Shroud Roots and Elixir Wells, Ancient Obelisks, bosses,
// survivors and chests. Facts are read with the game's own knowledge evaluator (kiln.knowledge) and entity components
// (kiln.world); the lists come from the game's data (WorldIndex.cs). Changes stay with Knowledge and Quests.
// Usage: docs/kiln/services/world-progress.md. Evidence: docs/re/world-status.md.
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>What the game stores about this world's progress. Lists are available from a moment after the
/// first call (they are read from the game's data in the background); live values are read on the tick thread.</summary>
public static unsafe class WorldProgress
{
    /// <summary>The knowledge service is loaded (most facts are knowledge).</summary>
    public static bool Available => Knowledge.Available;

    public static WorldProgressStatus Status
    {
        get
        {
            WorldIndex.EnsureStarted();
            var p = WorldIndex.Places;
            var b = WorldIndex.Bosses;
            return new WorldProgressStatus(Knowledge.Available, World.Available, Settings.Available, Assets.Available, WorldIndex.PlacesState,
                WorldIndex.BossesState, p?.Spires.Count ?? 0, p?.Shrines.Count ?? 0, p?.Roots.Count ?? 0, p?.Obelisks.Count ?? 0,
                b?.Count ?? 0, b?.Count(x => x.Tracked) ?? 0, WorldIndex.PlacesSeconds, WorldIndex.BossesSeconds, WorldIndex.Problems);
        }
    }

    /// <summary>True once the spires, shrines, roots and obelisks are listed.</summary>
    public static bool PlacesReady => WorldIndex.Places is not null;

    /// <summary>True once the bosses are listed (every chunk of the world read, a few seconds after the first call).</summary>
    public static bool BossesReady => WorldIndex.Bosses is not null;

    /// <summary>Waits off the tick thread until the lists are ready (or failed). False on a timeout or failure.</summary>
    public static Task<bool> WhenReady(bool bosses = false, TimeSpan? timeout = null)
    {
        WorldIndex.EnsureStarted();
        return Task.Run(() => WorldIndex.Wait(bosses, timeout ?? TimeSpan.FromMinutes(2)));
    }

    // ---- the flame -----------------------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct NetworkServerProgress
    {
        public ushort Level, WaterSources, Altars, HighestAltarLevel, LogicEntities, FactoryBudget;
    }

    private static EntityId _progressEntity;

    /// <summary>The world's flame (tick thread). Null when kiln.world is not loaded or the world's progress entity is not
    /// found (before the world runs).</summary>
    public static FlameStatus? Flame
    {
        get
        {
            if (!World.Available) return null;
            Services.RequireTickThread("WorldProgress.Flame");
            if (_progressEntity.IsNone || !World.Has(_progressEntity, "NetworkServerProgress"))
            {
                var found = World.Find("NetworkServerProgress");
                _progressEntity = found.Count > 0 ? found[0] : EntityId.None;
            }
            if (_progressEntity.IsNone || !World.TryRead<NetworkServerProgress>(_progressEntity, "NetworkServerProgress", out var p)) return null;
            var status = new FlameStatus(p.Level, p.Altars, p.HighestAltarLevel, p.WaterSources, _progressEntity);
            if (KnowledgeNative.T != null)
            {
                KnowledgeNative.FlameState st = default;
                st.Size = (uint)sizeof(KnowledgeNative.FlameState);
                if (KnowledgeNative.T->FlameStatus(&st) == KnowledgeNative.Ok && st.Found != 0 && st.Entity == _progressEntity.Value)
                    status = status with
                    {
                        Stored = (int)st.Level,
                        Max = st.Cap > 0 ? (int)st.Cap : null,
                        SystemLevel = st.SystemLevel == uint.MaxValue ? null : (int)st.SystemLevel,
                    };
            }
            return status;
        }
    }

    /// <summary>The Mother Flame's level (tick thread); null when unknown.</summary>
    public static int? FlameLevel => Flame?.Level;

    /// <summary>What the game derives from a flame level: how many Flame Altars and water sources the world may have, the
    /// Shroud resistance a player gets, and whether the level is high enough to remove the fog for altar placement (the game's
    /// <c>flameLevelFogRemoval</c> setting). Read from the game's balancing table (kiln.assets); null when it cannot be read or
    /// the level is above the game's maximum. Tick thread (it reads the setting).</summary>
    public static FlameLimits? LimitsAt(int level)
    {
        Services.RequireTickThread("WorldProgress.LimitsAt");
        return FlameLimitsReader.At(level);
    }

    // ---- changing the flame (docs/re/world-status.md section 11) ----

    private static readonly object FlameLock = new();
    private static readonly Dictionary<ulong, (TaskCompletionSource<FlameChange> Tcs, int Requested)> FlameWaiting = new();
    private static IDisposable? _flamePump;

    /// <summary>Sets the Mother Flame's level, as the game's own admin command does (the <c>Level</c> attribute of the world's
    /// progress entity, written like the game writes it, so it is saved with the world and replicated; the game then copies
    /// it into <c>NetworkServerProgress</c> and the altar, water-source and fog rules follow). Any thread; applied in the next
    /// server-world step after earlier changes. The task completes on the tick thread once the game mirrored the level,
    /// the native mirror wait ended (180 program updates), or the world ended, with
    /// the write outcome and any reason it failed (no world, no Mother Flame, unsupported build, or attribute fault).
    /// A failure does not guarantee unchanged state if rollback also failed; <see cref="FlameChange.Reason"/> says so.
    /// Lowering the level is allowed, as for the admin command; the game decides what happens to altars
    /// above the new limit. Throws <see cref="ArgumentOutOfRangeException"/> for a level below 1 or above the game's maximum
    /// (<see cref="FlameStatus.Max"/>, 20 in 1.0).</summary>
    public static Task<FlameChange> SetFlameLevel(int level)
    {
        var t = KnowledgeNative.T;
        if (level < 1 || level > 255) throw new ArgumentOutOfRangeException(nameof(level), level, "the flame level is 1 to 255");
        if (t == null) return Task.FromResult(FlameChange.Failed(level, "unavailable", "kiln.knowledge is not loaded on this server"));
        KnowledgeNative.FlameState st = default;
        st.Size = (uint)sizeof(KnowledgeNative.FlameState);
        if (t->FlameStatus(&st) == KnowledgeNative.Ok && st.Cap > 0 && level > st.Cap)
            throw new ArgumentOutOfRangeException(nameof(level), level, $"the flame level is 1 to {st.Cap}");
        ulong rid = 0;
        var rc = t->FlameSet(Services.CallerOwner, (uint)level, &rid);
        if (rc != KnowledgeNative.Ok)
        {
            var why = rc switch
            {
                KnowledgeNative.InvalidArgument => "the flame level is 1 to 255",
                KnowledgeNative.Unavailable => FlameWhyNot(),
                KnowledgeNative.BufferTooSmall => "too many flame changes are waiting; try again in a moment",
                _ => $"kiln.knowledge returned {rc}",
            };
            return Task.FromResult(FlameChange.Failed(level, KnowledgeNative.Code(rc), why));
        }
        var tcs = new TaskCompletionSource<FlameChange>(TaskCreationOptions.None);  // continuations: tick thread
        lock (FlameLock)
        {
            FlameWaiting[rid] = (tcs, level);
            _flamePump ??= ServiceTick.Add("kiln.flame results", FlamePump);
        }
        return tcs.Task;
    }

    private static string FlameWhyNot()
    {
        var t = KnowledgeNative.T;
        if (t == null) return "kiln.knowledge is not loaded";
        KnowledgeNative.FlameState st = default;
        st.Size = (uint)sizeof(KnowledgeNative.FlameState);
        if (t->FlameStatus(&st) != KnowledgeNative.Ok) return "the flame service did not answer";
        if (st.Available == 0) return KnowledgeNative.Text(st.Reason, 96) is { Length: > 0 } r ? r : "the flame is not supported on this game build";
        return "no Mother Flame is running (the world has not started, or it has no progress entity yet)";
    }

    private static void FlamePump(in TickInfo tick)
    {
        var t = KnowledgeNative.T;
        if (t == null) return;
        List<(ulong Id, TaskCompletionSource<FlameChange> Tcs, int Level)> due;
        lock (FlameLock)
        {
            if (FlameWaiting.Count == 0) return;
            due = [.. FlameWaiting.Select(kv => (kv.Key, kv.Value.Tcs, kv.Value.Requested))];
        }
        foreach (var (id, tcs, level) in due)
        {
            KnowledgeNative.FlameOutcome r = default;
            r.Size = (uint)sizeof(KnowledgeNative.FlameOutcome);
            // Only the native service can finish or cancel an accepted write. With one active and eight queued,
            // serial 180-update mirror waits can take 9 * (180 + 2) = 1638 program updates, or longer if paused.
            // A caller-side deadline must not report a failure while a queued request can still mutate the world.
            if (t->FlameResult(id, &r) != KnowledgeNative.Ok) continue;
            lock (FlameLock) FlameWaiting.Remove(id);
            tcs.TrySetResult(new FlameChange(id, r.Status == KnowledgeNative.Ok, KnowledgeNative.Code(r.Status), level, (int)r.Before,
                (int)r.After, (int)r.Cap, r.Clamped != 0, r.Marked != 0, r.Mirrored != 0, (int)r.StepsToMirror, KnowledgeNative.Text(r.Reason, 96)));
        }
    }

    // ---- difficulty ----------------------------------------------------------------------------------------------

    /// <summary>The server's preset and the game settings in effect (tick thread). Null until kiln.settings found them.</summary>
    public static WorldDifficulty? Difficulty
    {
        get
        {
            if (!Settings.Available || !Settings.Ready) return null;
            return new WorldDifficulty(Settings.Status.Preset, Settings.Values);
        }
    }

    // ---- lists ---------------------------------------------------------------------------------------------------

    /// <summary>Every Ancient Spire (empty until <see cref="PlacesReady"/>).</summary>
    public static IReadOnlyList<Spire> Spires => WorldIndex.Places?.Spires ?? [];

    /// <summary>Every Flame Shrine and Flame Sanctum the game's achievements count, by region.</summary>
    public static IReadOnlyList<FlameShrine> Shrines => WorldIndex.Places?.Shrines ?? [];

    /// <summary>Every Shroud Root the game's achievements count (with positions once <see cref="BossesReady"/>).</summary>
    public static IReadOnlyList<ShroudRoot> ShroudRoots => WorldIndex.Roots ?? [];

    /// <summary>The roots at Elixir Wells.</summary>
    public static IReadOnlyList<ShroudRoot> ElixirWells => [.. ShroudRoots.Where(r => r.ElixirWell)];

    public static IReadOnlyList<Obelisk> Obelisks => WorldIndex.Places?.Obelisks ?? [];

    /// <summary>Every boss placed in the world (empty until <see cref="BossesReady"/>).</summary>
    public static IReadOnlyList<Boss> Bosses => WorldIndex.Bosses ?? [];

    public static Spire? Spire(string id) => Find(Spires, id, s => s.Id, s => s.Template);
    public static FlameShrine? Shrine(string id) => Find(Shrines, id, s => s.Id, s => s.Template);
    public static ShroudRoot? ShroudRoot(string id) => Find(ShroudRoots, id, r => r.Id, _ => null);
    public static Obelisk? Obelisk(string id) => Find(Obelisks, id, o => o.Id, _ => null);

    /// <summary>A boss by id (<c>enemy_boss_fog_dragon</c>), or the first of a template's placements by template name.</summary>
    public static Boss? Boss(string id) => Find(Bosses, id, b => b.Id, b => b.Template);

    /// <summary>The bosses within <paramref name="radius"/> metres of a point, nearest first.</summary>
    public static IReadOnlyList<Boss> BossesNear(WorldPosition at, double radius) =>
        [.. Bosses.Where(b => b.Position.DistanceTo(at) <= radius).OrderBy(b => b.Position.DistanceTo(at))];

    private static T? Find<T>(IReadOnlyList<T> list, string id, Func<T, string> key, Func<T, string?> alt) where T : class
    {
        ArgumentNullException.ThrowIfNull(id);
        var s = id.Trim();
        return list.FirstOrDefault(x => string.Equals(key(x), s, StringComparison.OrdinalIgnoreCase))
               ?? list.FirstOrDefault(x => string.Equals(alt(x), s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The survivors (NPCs of the game's roster with a rescue requirement) and whether they are rescued (tick thread).</summary>
    public static IReadOnlyList<Survivor> Survivors
    {
        get
        {
            if (!Knowledge.Available) return [];
            return [.. Knowledge.Npcs.Where(n => n.Unlock.Id != 0).Select(n => new Survivor(n.DebugName, n.NameTag,
                new ProgressFact(n.Unlock, n.Unlock.Type == KnowledgeQueryType.Extern ? ProgressScope.World : WorldIndex.ScopeOf(n.Unlock.Id))))];
        }
    }

    // ---- chests --------------------------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct InventoryEmptyState
    {
        public byte ToggleVfx, IsEmpty;
    }

    /// <summary>The loot chest placed nearest to <paramref name="at"/> within <paramref name="radius"/> metres
    /// (horizontally), with its state when its entity is loaded. Null when there is none. Tick thread (the first lookup in
    /// an area reads that part of the game's data: a few milliseconds).</summary>
    public static ChestStatus? Chest(WorldPosition at, double radius = 3)
    {
        Services.RequireTickThread("WorldProgress.Chest");
        var near = WorldIndex.ChestsNear(at, radius);
        return near.Count == 0 ? null : StateOf(near[0].P, near[0].T);
    }

    /// <summary>Every loot chest placed within <paramref name="radius"/> metres of a point, nearest first. Tick thread.</summary>
    public static IReadOnlyList<ChestStatus> Chests(WorldPosition at, double radius)
    {
        Services.RequireTickThread("WorldProgress.Chests");
        return [.. WorldIndex.ChestsNear(at, radius).Select(c => StateOf(c.P, c.T))];
    }

    /// <summary>A chest by its loot container id, searched around <paramref name="near"/> (its chunk and the ones next to it).</summary>
    public static ChestStatus? Chest(uint lootId, WorldPosition near)
    {
        Services.RequireTickThread("WorldProgress.Chest");
        return WorldIndex.ChestById(lootId, near) is { } c ? StateOf(c.P, c.T) : null;
    }

    /// <summary>The chest state of a loaded entity (a chest's live entity, e.g. from <see cref="World.Find"/>); null when
    /// it is not a chest. Tick thread.</summary>
    public static ChestStatus? ChestOf(EntityId entity)
    {
        Services.RequireTickThread("WorldProgress.ChestOf");
        if (!World.Available || World.Position(entity) is not { } pos) return null;
        var near = WorldIndex.ChestsNear(pos, 2);
        if (near.Count > 0) return StateOf(near[0].P, near[0].T, entity);
        return ReadChest(entity, out var opened, out var looted)
            ? new ChestStatus(0, Spawn.Available ? Spawn.Inspect(entity)?.TemplateName ?? "" : "", pos, entity, opened, looted) : null;  // not a placed chest
    }

    internal static ChestStatus StateOf(ScenePlacement p, TemplateData t, EntityId known = default)
    {
        var e = known;
        if (e.IsNone && Spawn.Available)
        {
            foreach (var c in Spawn.Find(t.Guid, p.Position, 1.5, 2))
                if (!c.IsNone) { e = c; break; }
        }
        if (!e.IsNone && ReadChest(e, out var opened, out var looted)) return new ChestStatus(p.LootId, t.Name, p.Position, e, opened, looted);
        return new ChestStatus(p.LootId, t.Name, p.Position, EntityId.None, null, null);
    }

    /// <summary>Opened: the loot was generated (RandomLootContainer.spawnedLootVersion is not 0). Looted: opened and its
    /// inventory is empty (InventoryEmptyState.isEmpty).</summary>
    internal static bool ReadChest(EntityId e, out bool? opened, out bool? looted)
    {
        opened = looted = null;
        if (!World.Available) return false;
        // a chest has both; a loot prop without an inventory (a cupboard that drops its loot) is not a chest
        if (!World.TryRead<uint>(e, "RandomLootContainer", out var version) ||
            !World.TryRead<InventoryEmptyState>(e, "InventoryEmptyState", out var empty)) return false;
        opened = version != 0;
        looted = version != 0 && empty.IsEmpty != 0;
        return true;
    }

    // ---- a character's progress ----------------------------------------------------------------------------------

    /// <summary>One character's progress (a player in game). Tick thread for the reads.</summary>
    public static CharacterProgress For(Player player) => new(player.Entity);

    public static CharacterProgress For(EntityId player) => new(player);
}

/// <summary>One character's progress, read with the game's evaluator for that player (personal progression included).</summary>
public sealed class CharacterProgress
{
    internal CharacterProgress(EntityId player) => Player = player;

    public EntityId Player { get; }

    public IReadOnlyList<Spire> SpiresUnlocked => [.. WorldProgress.Spires.Where(s => s.Unlock.Holds(Player) == true)];
    public IReadOnlyList<Spire> SpireTopsReached => [.. WorldProgress.Spires.Where(s => s.TopReached?.Holds(Player) == true)];
    public IReadOnlyList<FlameShrine> ShrinesFound => [.. WorldProgress.Shrines.Where(s => s.Discovery.Holds(Player) == true)];
    public IReadOnlyList<ShroudRoot> RootsDestroyed => [.. WorldProgress.ShroudRoots.Where(r => r.Destruction.Holds(Player) == true)];
    public IReadOnlyList<Obelisk> ObelisksRead => [.. WorldProgress.Obelisks.Where(o => o.Reading.Holds(Player) == true)];
    public IReadOnlyList<Boss> BossesKilled => [.. WorldProgress.Bosses.Where(b => b.Kill?.Holds(Player) == true)];

    /// <summary>The character's quests, vanilla and Kiln (<see cref="Quests.Of(EntityId)"/>).</summary>
    public QuestLog Quests => global::Kiln.Quests.Of(Player);
}
