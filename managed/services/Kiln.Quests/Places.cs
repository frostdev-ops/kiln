// SPDX-License-Identifier: MIT
// Where a quest happens. Written after a sample's two Vukah spawned 18 m east of a player standing in their base, at (3844.8,
// 841.5, 1435.5), inside the base's build zone (center 3800,832,1434, half-size 60), at a guessed height, with nothing on the map
// pointing at them. The game pacifies enemies in a base and may remove them. So:
//  - a place is found on real ground (the voxel surface, kiln.build), outside every base zone, not on a cliff or a roof;
//  - it is kept per world: one marker entity serves a step, so everyone doing that step goes to one place;
//  - a step's map marker (Native/) and waypoint point at it;
//  - creatures a step spawns are checked every second. Ones the game removed are logged and spawned again, further out; when
//    that keeps failing it says so as an error. They are never saved, so a restart or a rejoin spawns what is still owed.
// Tick thread. Usage: docs/kiln/modding/quests.md "Places".
using System.Runtime.CompilerServices;

namespace Kiln;

internal static class QuestPlaces
{
    private const string BookKey = "quest-sites";
    private const double MaxSearch = 450, RingStep = 14;
    private const double ColumnUp = 40, ColumnDown = 60, Headroom = 3, MaxStep = 2.5, SameLevel = 12;
    private const long VerifyAfterMs = 3000, StableMs = 60_000;

    private sealed class SiteBook
    {
        public Dictionary<string, double[]> Sites { get; set; } = new(StringComparer.Ordinal);
    }

    private static readonly Dictionary<string, double[]> MemorySites = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Task<WorldPosition?>> Searching = new(StringComparer.Ordinal);
    private static bool _noBuildLogged, _noTilesLogged, _tilesFailed;
    private static Task? _basesSettled;

    // ---- the world's record of its sites ----------------------------------------------------------------------------------
    private static Dictionary<string, double[]> Sites
    {
        get
        {
            try
            {
                return QuestsInternals.RunAs(QuestsInternals.DataOwner, () => WorldData.Get<SiteBook>(BookKey)).Sites;
            }
            catch (Exception e) when (e is NotSupportedException or InvalidOperationException or InvalidDataException)
            {
                return MemorySites;  // no world data here: a place lasts this session
            }
        }
    }

    private static WorldPosition? Recall(string key) => Sites.TryGetValue(key, out var p) && p.Length == 3 ? new WorldPosition(p[0], p[1], p[2]) : null;
    private static void Remember(string key, WorldPosition p) => Sites[key] = [p.X, p.Y, p.Z];
    private static void Forget(string key) => Sites.Remove(key);

    private static string KeyOf(QuestDefinition q, QuestStep s) => $"{q.FullId}.{s.Id}";

    /// <summary>The step's place, once it is found.</summary>
    public static WorldPosition? KnownFor(QuestDefinition q, QuestStep s) => Recall(KeyOf(q, s));

    /// <summary>A new world runs: nothing of the last one holds (entity ids, searches, waiting steps).</summary>
    public static void WorldStarted()
    {
        Encounters.Clear();
        Waiting.Clear();
        Searching.Clear();
        Checks.Clear();
        MemorySites.Clear();
        _basesSettled = null;
    }

    // The game's base zones come up with their altars, a few steps into the run: read at ServerStartedEvent, every world has none.
    private static Task BasesSettled() => _basesSettled ??= WaitForBases();

    private static async Task WaitForBases()
    {
        var until = Environment.TickCount64 + 8000;
        while (Environment.TickCount64 < until)
        {
            try
            {
                if (!Build.Available || (Spawn.WorldRunning && Build.Bases.Count > 0)) return;
            }
            catch (Exception e) when (e is NotSupportedException or InvalidOperationException or BuildException)
            {
                return;
            }
            await QuestTick.NextStep();
        }
    }

    // ---- bases and ground -------------------------------------------------------------------------------------------------

    /// <summary>The base zone a spot is in or next to.</summary>
    public static PlayerBase? BaseAt(double x, double z, double margin)
    {
        try
        {
            if (!Build.Available) return null;
            foreach (var b in Build.Bases)
                if (Math.Abs(x - b.Center.X) <= b.Half.X + margin && Math.Abs(z - b.Center.Z) <= b.Half.Z + margin) return b;
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or BuildException)
        {
            QuestsInternals.Warn($"the base zones cannot be read: {e.Message}");
        }
        return null;
    }

    /// <summary>The ground at (x, z) nearest to a height: the top of solid terrain with room to stand above it, read from the
    /// voxel surface. Null: none in reach, too steep, a building's roof, or the tiles did not load.</summary>
    public static async Task<WorldPosition?> Ground(double x, double z, double near)
    {
        var box = Build.BoxBetween(new WorldPosition(x - 2, Math.Max(1, near - ColumnDown), z - 2), new WorldPosition(x + 2, near + ColumnUp, z + 2));
        Voxel[] cells;
        for (var wait = 0; !Build.TryRead(box, out cells); wait++)
        {
            if (wait > 375)  // about 15 s; the game streams a tile in within 1-3 s
            {
                _tilesFailed = true;
                return null;
            }
            await QuestTick.NextStep();
        }
        var grid = Build.Grid;
        if (grid is null) return null;
        var size = grid.CellSize;
        var bottom = Build.CornerOf(new CellPos(box.X, box.Y, box.Z)).Y;
        var room = (int)Math.Ceiling(Headroom / size);
        var mid = Top(cells, box, box.SizeX / 2, box.SizeZ / 2, bottom, size, room);
        if (mid is not { } top) return null;
        var cell = cells[box.SizeX / 2 + box.SizeZ / 2 * box.SizeX * box.SizeY + (int)Math.Clamp((top - size / 2 - bottom) / size, 0, box.SizeY - 1) * box.SizeX];
        if (cell.IsBuilding) return null;  // somebody's roof or floor
        foreach (var (ix, iz) in new[] { (0, 0), (box.SizeX - 1, 0), (0, box.SizeZ - 1), (box.SizeX - 1, box.SizeZ - 1) })
            if (Top(cells, box, ix, iz, bottom, size, room) is not { } h || Math.Abs(h - top) > MaxStep) return null;  // a cliff
        return new WorldPosition(x, top, z);
    }

    // The uppermost solid cell with room to stand above it: under the open sky, not in a cave below it.
    private static double? Top(Voxel[] cells, CellBox box, int ix, int iz, double bottom, double size, int room)
    {
        var clear = 0;
        var baseIndex = ix + iz * box.SizeX * box.SizeY;
        for (var y = box.SizeY - 1; y >= 0; y--)
        {
            if (cells[baseIndex + y * box.SizeX].Fill <= 127) clear++;
            else if (clear >= room) return bottom + (y + 1) * size;
            else clear = 0;
        }
        return null;
    }

    /// <summary>A good spot around a place: from <paramref name="min"/> to <paramref name="max"/> metres, then further out until
    /// one is found (a base in the way never fails it). Null: nothing within 450 m, or no voxel world.</summary>
    public static async Task<WorldPosition?> Find(WorldPosition center, double min, double max, double margin, bool inBase, string what)
    {
        if (!Build.Available)
        {
            if (!_noBuildLogged)
            {
                _noBuildLogged = true;
                QuestsInternals.Warn("kiln.build is unavailable on this build: quest places cannot check the ground or the bases");
            }
            return center;
        }
        await BasesSettled().OnTick();
        var turn = (StableHash(what) & 0xffff) / 65536.0 * Math.PI * 2;
        // a walk away on about the same level first; only then the valley below or the ridge above
        foreach (var level in new[] { SameLevel, double.MaxValue })
        {
            foreach (var r in Rings(min, Math.Max(min, max)))
            {
                var points = r < 1 ? 1 : (int)Math.Clamp(r * 2 * Math.PI / 12, 8, 48);
                for (var i = 0; i < points; i++)
                {
                    var a = turn + i * 2 * Math.PI / points;
                    var x = center.X + r * Math.Cos(a);
                    var z = center.Z + r * Math.Sin(a);
                    if (!inBase && BaseAt(x, z, margin) is not null) continue;
                    WorldPosition? g;
                    _tilesFailed = false;
                    try
                    {
                        g = await Ground(x, z, center.Y).OnTick();
                    }
                    catch (BuildException)
                    {
                        g = null;  // outside the world
                    }
                    if (_tilesFailed)
                    {
                        // no voxels to read here (a build where the tiles do not come): outside the bases at least, with the height asked for
                        if (!_noTilesLogged)
                        {
                            _noTilesLogged = true;
                            QuestsInternals.Warn($"{what}: the voxel tiles do not load here: quest places cannot check the ground, only the bases");
                        }
                        return new WorldPosition(x, center.Y, z);
                    }
                    await QuestTick.NextStep();  // at most one column read per step
                    if (g is { } found && Math.Abs(found.Y - center.Y) <= level) return found;
                }
            }
        }
        return null;
    }

    private static uint StableHash(string s)
    {
        var h = 0x811c9dc5u;
        foreach (var c in s) h = (h ^ c) * 0x01000193u;
        return h;
    }

    // the middle of the wanted distance first, then around it, then outwards
    private static IEnumerable<double> Rings(double min, double max)
    {
        var mid = (min + max) / 2;
        yield return mid;
        for (var d = RingStep; mid - d >= min || mid + d <= max; d += RingStep)
        {
            if (mid + d <= max) yield return mid + d;
            if (mid - d >= min) yield return mid - d;
        }
        for (var r = Math.Max(max, RingStep) + RingStep; r <= MaxSearch; r += RingStep) yield return r;
    }

    /// <summary>A wanted position put right: on the ground, outside every base. Logs when it had to move. Null: nothing good nearby.</summary>
    public static async Task<WorldPosition?> Settle(WorldPosition wanted, string what, bool inBase = false, double margin = 20)
    {
        await BasesSettled().OnTick();
        var inside = inBase ? null : BaseAt(wanted.X, wanted.Z, margin);
        var p = await Find(wanted, 0, 0, margin, inBase, what).OnTick();
        if (p is null) QuestsInternals.Error($"{what}: no ground outside a base within {MaxSearch} m of {wanted}");
        else if (inside is not null) QuestsInternals.Warn($"{what}: {wanted} is in or next to the base at {inside.Center}: moved to {p}");
        else if (Math.Abs(p.Value.X - wanted.X) > 1 || Math.Abs(p.Value.Z - wanted.Z) > 1) QuestsInternals.Warn($"{what}: no ground at {wanted}: moved to {p}");
        else if (Math.Abs(p.Value.Y - wanted.Y) > 1) QuestsInternals.Info($"{what}: {wanted} is not on the ground: the voxel surface there is at {p.Value.Y:F1}");
        return p;
    }

    /// <summary>A place of its own for a key: remembered per world, found once.</summary>
    public static Task<WorldPosition?> SiteOf(string key, QuestPlace spec, WorldPosition anchor, string what)
    {
        if (Recall(key) is { } known && (spec.InBase || BaseAt(known.X, known.Z, 0) is null)) return Task.FromResult<WorldPosition?>(known);
        if (Searching.TryGetValue(key, out var running)) return running;
        var t = Search(key, spec, anchor, what);
        Searching[key] = t;
        return t;
    }

    private static async Task<WorldPosition?> Search(string key, QuestPlace spec, WorldPosition anchor, string what)
    {
        try
        {
            if (Recall(key) is { } old) QuestsInternals.Warn($"{what}: the place it had, {old}, is in a base now: it moves");
            var p = spec.MinDistance > 0 || spec.MaxDistance > 0
                ? await Find(anchor, spec.MinDistance, spec.MaxDistance, spec.BaseMargin, spec.InBase, what).OnTick()
                : await Settle(anchor, what, spec.InBase, spec.BaseMargin).OnTick();
            if (p is { } found)
            {
                Remember(key, found);
                QuestsInternals.Info($"{what}: at {found} (the voxel surface, {DistanceToBase(found)})");
            }
            else QuestsInternals.Error($"{what}: no place found around {anchor}");
            return p;
        }
        finally
        {
            Searching.Remove(key);
        }
    }

    private static string DistanceToBase(WorldPosition p)
    {
        try
        {
            var bases = Build.Available ? Build.Bases : [];
            if (bases.Count == 0) return "no base in this world";
            var d = bases.Min(b => Math.Max(Math.Abs(p.X - b.Center.X) - b.Half.X, Math.Abs(p.Z - b.Center.Z) - b.Half.Z));
            return d < 0 ? "inside a base" : $"{d:F0} m from the nearest base zone";
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or BuildException)
        {
            return "bases unknown";
        }
    }

    // ---- a step's place ---------------------------------------------------------------------------------------------------

    /// <summary>What a step says about where it happens, when it needs Kiln to look.</summary>
    private static QuestPlace? SpecOf(QuestStep s)
    {
        if (s.Site is { } site) return site;
        foreach (var sp in s.Spawns)
            if (sp.Place is { } place) return place;
        if (s.Spawns.Count > 0) return new QuestPlace { NearPlayer = true, MinDistance = 30, MaxDistance = 60 };
        if (s.Marker?.At is { } m) return new QuestPlace { At = m };
        foreach (var o in s.Objectives)
        {
            if (o.Kind == QuestObjectiveKind.Reach && o.At is { } at) return new QuestPlace { At = at };
            if (o.Kind == QuestObjectiveKind.Kill && o.Near is { } near) return new QuestPlace { At = near };
        }
        return null;
    }

    /// <summary>The NPC of the mod's <c>npcs</c> a step sends the player to, when it has one.</summary>
    private static string? NpcOf(QuestStep s, string mod)
    {
        foreach (var o in s.Objectives)
        {
            if (o.Kind == QuestObjectiveKind.Talk && o.Npc is { } t && Quests.FindNpcDef(t, mod) is not null) return t;
            if (o.Kind == QuestObjectiveKind.Deliver && o.To is { } d && Quests.FindNpcDef(d, mod) is not null) return d;
        }
        return null;
    }

    /// <summary>Where a named NPC stands now: its bound entity, its template's entity, or its spot.</summary>
    public static WorldPosition? NpcPosition(string name, string modId)
    {
        if (Quests.FindNpcDef(name, modId) is not { } d) return null;
        if (Dialogues.BoundEntity(d) is { IsNone: false } e && World.Available && World.Position(e) is { } p) return p;
        if (d.Template is { Length: > 0 } t && Spawn.Available)
        {
            var guid = NativeQuests.Npc(d.FullId)?.Guid;
            var found = guid is { } g ? Spawn.Find(g, max: 1) : Spawn.FindByName(t, max: 1);
            if (found.Count > 0 && Spawn.Inspect(found[0]) is { } info) return info.Position;
        }
        return d.At;
    }

    private static WorldPosition? AnchorOf(QuestPlace spec, QuestDefinition q, QuestHolder h)
    {
        if (spec.At is { } at) return at;
        if (spec.NearNpc is { } npc) return NpcPosition(npc, q.ModId);
        if (QuestObjectives.PositionOf(h) is { } mine) return mine;
        foreach (var o in Quests.Online())
            if (QuestObjectives.PositionOf(o) is { } theirs) return theirs;
        return null;
    }

    /// <summary>A step's place for a holder. Null: not known yet (no player to search around, the NPC is not placed yet).</summary>
    private static async Task<WorldPosition?> Where(QuestDefinition q, QuestStep s, QuestHolder h, QuestRecord rec)
    {
        var what = $"{q.FullId} step {s.Id}";
        var spec = SpecOf(s);
        // a synthetic holder of a lab test, at a fixed place: nobody stands there, nothing to look up
        if (spec is { At: { } raw, MinDistance: 0, MaxDistance: 0 } && (spec.Exact || (QuestInput.IsSynthetic(h) && s.Site is null && s.Spawns.Count == 0))) return raw;
        if (spec is not null)
        {
            // nothing to search around right now (no player in game, the NPC not placed yet): the place it has, or wait
            if (AnchorOf(spec, q, h) is not { } anchor) return Recall(KeyOf(q, s));
            return await SiteOf(KeyOf(q, s), spec, anchor, what).OnTick();
        }
        if (NpcOf(s, q.ModId) is { } npc) return NpcPosition(npc, q.ModId);
        foreach (var o in s.Objectives)
            if (o.Kind == QuestObjectiveKind.Reach && o.Offset is { } off && rec.StepOrigin is { } from)
            {
                var wanted = new WorldPosition(from[0] + off.X, from[1] + off.Y, from[2] + off.Z);
                return QuestInput.IsSynthetic(h) ? wanted : await Settle(wanted, what).OnTick();
            }
        return null;
    }

    private static bool HasPlace(QuestDefinition q, QuestStep s) =>
        SpecOf(s) is not null || NpcOf(s, q.ModId) is not null || s.Objectives.Any(o => o.Kind == QuestObjectiveKind.Reach && o.Offset is not null);

    // ---- entering and leaving ---------------------------------------------------------------------------------------------
    private static readonly List<(QuestDefinition Q, QuestHolder H, QuestRecord R, QuestStep S, long Since)> Waiting = [];

    /// <summary>A holder is at a step now: the place is found, the marker and waypoint go there, its creatures come.</summary>
    public static void EnterStep(QuestDefinition q, QuestHolder h, QuestRecord rec, QuestStep step)
    {
        QuestMarkers.Clear(h, q.FullId);
        if (!HasPlace(q, step)) return;
        _ = Show(q, h, rec, step);
    }

    private static async Task Show(QuestDefinition q, QuestHolder h, QuestRecord rec, QuestStep step)
    {
        try
        {
            var at = await Where(q, step, h, rec).OnTick();
            if (rec.Status != QuestStatus.Active || rec.StepId != step.Id) return;
            if (at is not { } p)
            {
                // nobody to search around yet, or the NPC comes later: asked again
                if (!Waiting.Any(w => w.Q == q && w.H.Key == h.Key && w.S == step))
                {
                    Waiting.Add((q, h, rec, step, Environment.TickCount64));
                    QuestsInternals.Info($"{h.Key} {q.FullId} step {step.Id}: its place is not known yet: waiting");
                }
                return;
            }
            if (!step.NoMarker)
            {
                NativeQuests.ShowMarker(q, step, p);
                if (step.Marker?.Waypoint ?? true) QuestMarkers.Set(h, p, q.FullId);
            }
            for (var i = 0; i < step.Spawns.Count; i++) StartEncounter(q, step, step.Spawns[i], i, h, rec, p);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            QuestsInternals.Error($"{h.Key} {q.FullId} step {step.Id}: its place: {e}");
        }
    }

    /// <summary>A player came (back) in game: the steps they are at show again, and what is still owed spawns.</summary>
    public static void Restore(QuestHolder h)
    {
        foreach (var p in Quests.ProgressOf(h))
        {
            if (p.Status != QuestStatus.Active || p.StepId is null || p.Quest.Step(p.StepId) is not { } step || !HasPlace(p.Quest, step)) continue;
            if (Quests.RecordFor(p.Quest, h) is { } rec) _ = Show(p.Quest, p.Quest.Scope == QuestScope.World ? QuestHolder.World : h, rec, step);
        }
    }

    public static void Left(QuestHolder h)
    {
        Waiting.RemoveAll(w => w.H.Key == h.Key);
        QuestMarkers.Forget(h);
        // what they are owed waits for them: Restore picks it up
        foreach (var e in Encounters.Where(e => e.Holder.Key == h.Key)) e.Holder = e.Holder with { Entity = EntityId.None };
    }

    /// <summary>A step is over for a holder.</summary>
    public static void LeaveStep(QuestDefinition q, QuestHolder h, QuestStep step)
    {
        Waiting.RemoveAll(w => w.Q == q && w.H.Key == h.Key && w.S == step);
        foreach (var e in Encounters.Where(e => e.Quest == q && e.Step == step && e.Holder.Key == h.Key).ToArray()) End(e, "the step is over");
    }

    /// <summary>A quest is over, given up or reset for a holder.</summary>
    public static void LeaveQuest(QuestDefinition q, QuestHolder h)
    {
        QuestMarkers.Clear(h, q.FullId);
        Waiting.RemoveAll(w => w.Q == q && w.H.Key == h.Key);
        foreach (var e in Encounters.Where(e => e.Quest == q && e.Holder.Key == h.Key).ToArray()) End(e, "the quest is over");
    }

    // ---- creatures a step spawns ------------------------------------------------------------------------------------------
    private sealed class Encounter
    {
        public required string Key;
        public required QuestDefinition Quest;
        public required QuestStep Step;
        public required QuestSpawn Spec;
        public required QuestHolder Holder;
        public required QuestRecord Record;
        public required string Tag;
        public WorldPosition Site;
        public readonly List<EntityId> Alive = [];
        public int Attempts;
        public long NextCheck, StableSince;
        public bool Busy, GaveUp;
    }

    private static readonly List<Encounter> Encounters = [];
    private static readonly HashSet<uint> Died = [];
    private static bool _deathsHooked;
    private static readonly List<(EntityId Entity, string What, long At)> Checks = [];

    /// <summary>What a creature's death means to an encounter: it is gone, and the game did not remove it.</summary>
    private static void HookDeaths()
    {
        if (_deathsHooked) return;
        _deathsHooked = true;
        QuestsInternals.RunAs(null, () => Events.On<EntityDeathEvent>(e =>
        {
            if (Died.Count > 4096) Died.Clear();
            Died.Add(e.Victim.Value);
        }));
    }

    private static void StartEncounter(QuestDefinition q, QuestStep step, QuestSpawn spec, int index, QuestHolder h, QuestRecord rec, WorldPosition site)
    {
        var key = $"{h.Key}|{q.FullId}.{step.Id}#{index}";
        var e = Encounters.FirstOrDefault(x => x.Key == key);
        if (e is not null)
        {
            e.Holder = h;  // the same character, back with a new entity
            e.Site = site;
            e.GaveUp = false;
            e.NextCheck = 0;
            return;
        }
        HookDeaths();
        e = new Encounter
        {
            Key = key, Quest = q, Step = step, Spec = spec, Holder = h, Record = rec, Site = site,
            Tag = spec.Tag ?? $"quest:{q.FullId}.{step.Id}",
        };
        Encounters.Add(e);
        _ = Fill(e, false);
    }

    /// <summary>How many of them the step still needs in the world: what its kill objective has not counted yet.</summary>
    private static int Owed(Encounter e)
    {
        var counted = 0;
        for (var i = 0; i < e.Step.Objectives.Count; i++)
            if (e.Step.Objectives[i].Kind == QuestObjectiveKind.Kill)
                counted = Math.Max(counted, e.Record.Counters.GetValueOrDefault(QuestRecord.CounterKey(e.Step.Id, i)));
        return Math.Max(0, e.Spec.Count - counted);
    }

    private static async Task Fill(Encounter e, bool retry)
    {
        if (e.Busy) return;
        e.Busy = true;
        try
        {
            var missing = Owed(e) - e.Alive.Count;
            for (var i = 0; i < missing; i++)
            {
                var a = i * 2.4 + e.Attempts;
                var r = i == 0 ? 0 : e.Spec.Spread;
                var x = e.Site.X + r * Math.Cos(a);
                var z = e.Site.Z + r * Math.Sin(a);
                WorldPosition? ground = null;
                try
                {
                    if (Build.Available) ground = await Ground(x, z, e.Site.Y).OnTick();
                }
                catch (BuildException)
                {
                }
                var at = ground ?? e.Site;
                var id = await Spawn.Entity(e.Spec.Template, new WorldPosition(at.X, at.Y + 0.2, at.Z), (float)(a * 57.3 % 360), new SpawnOptions
                {
                    Tag = e.Tag, Flags = SpawnFlags.Transient, Level = e.Spec.Level,
                }).OnTick();
                e.Alive.Add(id);
                QuestsInternals.Info($"{e.Holder.Key} {e.Quest.FullId} step {e.Step.Id}: spawned {e.Spec.Template} as {id} at ({at.X:F1}, {at.Y:F1}, {at.Z:F1}){(retry ? $", attempt {e.Attempts + 1}" : "")}");
            }
            e.NextCheck = Environment.TickCount64 + VerifyAfterMs;
            e.StableSince = Environment.TickCount64;
        }
        catch (Exception x) when (x is not OutOfMemoryException)
        {
            e.Attempts++;
            e.NextCheck = Environment.TickCount64 + VerifyAfterMs;
            QuestsInternals.Error($"{e.Holder.Key} {e.Quest.FullId} step {e.Step.Id}: {e.Spec.Template} could not spawn: {x.Message}");
        }
        finally
        {
            e.Busy = false;
        }
    }

    private static void End(Encounter e, string why)
    {
        Encounters.Remove(e);
        var left = 0;
        foreach (var id in e.Alive)
            if (Spawn.Exists(id) && !Died.Contains(id.Value) && Spawn.Destroy(id)) left++;
        if (left > 0) QuestsInternals.Info($"{e.Holder.Key} {e.Quest.FullId} step {e.Step.Id}: {why}: {left} {e.Spec.Template} removed");
    }

    private static void CheckEncounter(Encounter e, long now)
    {
        if (e.Busy || e.GaveUp || now < e.NextCheck) return;
        e.NextCheck = now + 1000;
        if (e.Record.Status != QuestStatus.Active || e.Record.StepId != e.Step.Id)
        {
            End(e, "the step moved on");
            return;
        }
        if (!e.Holder.IsWorld && !QuestInput.IsSynthetic(e.Holder) && e.Holder.Entity.IsNone) return;  // offline: what is owed comes when they do
        var removed = new List<EntityId>();
        var died = 0;
        foreach (var id in e.Alive.ToArray())
        {
            if (Died.Contains(id.Value))
            {
                e.Alive.Remove(id);
                died++;
            }
            else if (!Spawn.Exists(id))
            {
                e.Alive.Remove(id);
                removed.Add(id);
            }
        }
        var owed = Owed(e);
        // a kill is counted in the same step or the next: only then is one that died without credit still owed
        if (removed.Count == 0 && e.Alive.Count >= owed)
        {
            if (e.Attempts > 0 && now - e.StableSince > StableMs) e.Attempts = 0;
            return;
        }
        if (removed.Count == 0 && died > 0) return;
        if (removed.Count == 0 && e.Alive.Count < owed && now - e.StableSince < VerifyAfterMs * 2) return;
        if (removed.Count > 0)
            QuestsInternals.Warn($"{e.Holder.Key} {e.Quest.FullId} step {e.Step.Id}: the game removed {e.Spec.Template} {string.Join(", ", removed)} at {e.Site}, {DistanceToBase(e.Site)}");
        if (++e.Attempts > e.Spec.Retries)
        {
            e.GaveUp = true;
            QuestsInternals.Error($"{e.Holder.Key} {e.Quest.FullId} step {e.Step.Id}: gave up on {e.Spec.Template} after {e.Spec.Retries} retries: the step cannot be finished by killing them; " +
                                  "/questadmin advance moves a player on");
            return;
        }
        _ = Retry(e, removed.Count > 0);
    }

    private static async Task Retry(Encounter e, bool move)
    {
        try
        {
            if (move)
            {
                // the game took them away from this spot: further out, further from the bases
                var key = KeyOf(e.Quest, e.Step);
                Forget(key);
                var spec = SpecOf(e.Step) ?? new QuestPlace { NearPlayer = true };
                var wider = new QuestPlace
                {
                    At = spec.At, NearNpc = spec.NearNpc, NearPlayer = spec.NearPlayer, BaseMargin = spec.BaseMargin + 20 * e.Attempts,
                    MinDistance = Math.Max(spec.MinDistance, 30) + 25 * e.Attempts, MaxDistance = Math.Max(spec.MaxDistance, 60) + 25 * e.Attempts,
                };
                var anchor = AnchorOf(wider, e.Quest, e.Holder) ?? e.Site;
                if (await SiteOf(key, wider, anchor, $"{e.Quest.FullId} step {e.Step.Id}, retry {e.Attempts}").OnTick() is { } p)
                {
                    e.Site = p;
                    if (!e.Step.NoMarker)
                    {
                        NativeQuests.ShowMarker(e.Quest, e.Step, p);
                        if (e.Step.Marker?.Waypoint ?? true) QuestMarkers.Set(e.Holder, p, e.Quest.FullId);
                    }
                }
            }
            await Fill(e, true).OnTick();
        }
        catch (Exception x) when (x is not OutOfMemoryException)
        {
            QuestsInternals.Error($"{e.Quest.FullId} step {e.Step.Id}: retry {e.Attempts}: {x.Message}");
        }
    }

    /// <summary>A one-off spawn action: says so in the log when the game removes it. Use a step's <c>spawn</c> for ones that must stay.</summary>
    public static void Verify(EntityId e, string what) => Checks.Add((e, what, Environment.TickCount64 + VerifyAfterMs));

    /// <summary>Every ~1 s.</summary>
    public static void Poll()
    {
        var now = Environment.TickCount64;
        foreach (var e in Encounters.ToArray())
        {
            try
            {
                CheckEncounter(e, now);
            }
            catch (Exception x) when (x is NotSupportedException or InvalidOperationException or SpawnException)
            {
                QuestsInternals.Warn($"{e.Quest.FullId} step {e.Step.Id}: the check of its creatures: {x.Message}");
            }
        }
        for (var i = Checks.Count - 1; i >= 0; i--)
        {
            var (entity, what, at) = Checks[i];
            if (now < at) continue;
            Checks.RemoveAt(i);
            if (Spawn.Available && !Spawn.Exists(entity) && !Died.Contains(entity.Value)) QuestsInternals.Warn($"{what}: entity {entity} is gone: the game removed it");
        }
        // steps whose place could not be known yet
        foreach (var w in Waiting.ToArray())
        {
            if (w.R.Status != QuestStatus.Active || w.R.StepId != w.S.Id)
            {
                Waiting.Remove(w);
                continue;
            }
            if (now - w.Since < 2000) continue;
            Waiting.Remove(w);
            _ = Show(w.Q, w.H, w.R, w.S);
        }
    }

    /// <summary>For tools and tests: the entities a step's spawn holds now.</summary>
    public static IReadOnlyList<EntityId> AliveOf(string fullQuest, string step, string holderKey) =>
        Encounters.Where(e => e.Quest.FullId == fullQuest && e.Step.Id == step && e.Holder.Key == holderKey).SelectMany(e => e.Alive).ToArray();

    /// <summary>For tools and tests: a step's encounter gave up.</summary>
    public static bool GaveUp(string fullQuest, string step, string holderKey) =>
        Encounters.Any(e => e.Quest.FullId == fullQuest && e.Step.Id == step && e.Holder.Key == holderKey && e.GaveUp);
}
