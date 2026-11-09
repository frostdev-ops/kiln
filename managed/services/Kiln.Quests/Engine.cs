// SPDX-License-Identifier: MIT
// Kiln.Quests engine (server side): the registry of definitions, each holder's
// state in Kiln.Saves (character data for characters, world data for the world, owner "kiln-quests"; a world's quests
// stay on that world), and the state machine.
// Everything runs on the tick thread (QuestTick.cs). Objectives: Objectives.cs. Actions and conditions: Actions.cs. Places and
// creatures in the world: Places.cs. What players see: the game's journal, Native/. Chat conversations for mods vanilla clients
// join: EngineConversations.cs. Usage: docs/kiln/services/quests.md.
using System.Text.Json.Serialization;

namespace Kiln;

/// <summary>Everything one holder has done: one per character (per world) and one for the world. Stored as JSON.</summary>
public sealed class QuestBook
{
    /// <summary>Quest full id -> its record. No record: never started.</summary>
    public Dictionary<string, QuestRecord> Quests { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Old saves' tracked quest. The game tracks quests itself now, in the character's save (<c>activeQuestId</c>).</summary>
    public string? Tracked { get; set; }
    /// <summary>+1 per change.</summary>
    public long Revision { get; set; }
}

/// <summary>A holder's state in one quest, as stored.</summary>
public sealed class QuestRecord
{
    [JsonConverter(typeof(JsonStringEnumConverter<QuestStatus>))]
    public QuestStatus Status { get; set; } = QuestStatus.Active;
    /// <summary>The current step (an id, so a changed definition keeps its place); null when not active.</summary>
    public string? StepId { get; set; }
    public int StepIndex { get; set; }
    /// <summary>"&lt;step&gt;#&lt;objective&gt;" -> count.</summary>
    public Dictionary<string, int> Counters { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Where the player stood when the step started (for reach offsets).</summary>
    public double[]? StepOrigin { get; set; }
    /// <summary>When the step started (not saved): a step the player already fulfils stays on screen a moment before it passes.</summary>
    [JsonIgnore]
    public long EnteredAt { get; set; }
    public long StartedAt { get; set; }
    public long FinishedAt { get; set; }
    public int Completions { get; set; }
    /// <summary>Rewards still to pay (the player was offline when an admin completed it).</summary>
    public bool RewardsPending { get; set; }

    public static string CounterKey(string step, int objective) => $"{step}#{objective}";
}

/// <summary>Kiln quests: chains defined by mods (quests/*.json or <see cref="Define"/>), run by the server for players,
/// parties and the world. See docs/kiln/services/quests.md.</summary>
/// <remarks>Changes and reads of state run on the tick thread. Definitions follow their mod's load state; state stays in
/// the world's sidecar when a mod is removed.</remarks>
public static partial class Quests
{
    internal sealed record Entry(QuestDefinition Def, ModContext? Owner, bool FromJson);

    internal static readonly object Gate = new();
    internal static readonly Dictionary<string, Entry> Registry = new(StringComparer.Ordinal);
    internal static readonly Dictionary<string, (DialogueDefinition Def, ModContext? Owner, bool FromJson)> DialogueRegistry = new(StringComparer.Ordinal);
    internal static readonly Dictionary<string, (QuestNpcDefinition Def, ModContext? Owner, bool FromJson)> NpcRegistry = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, (Func<QuestActionContext, Task> Run, ModContext? Owner)> NamedActions = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Func<QuestContext, bool>> NamedPredicates = new(StringComparer.Ordinal);
    private static readonly List<string> LoadErrorList = new();
    private static readonly HashSet<string> JsonMods = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, QuestBook> SessionBooks = new(StringComparer.Ordinal);
    private static bool _started, _commands, _dataFallbackLogged;
    private static ulong _ticks, _transitions;
    internal static ulong ActionsRun, ActionsFailed;

    /// <summary>Text language of chat lines (vanilla clients do not tell the server theirs).</summary>
    public static string ChatLanguage { get; set; } = "en";

    public static bool Available => true;

    /// <summary>Maps a talk objective's <c>line</c> to a line id when the knowledge names do not know it (modId, text):
    /// Kiln.Quests/Native sets it for the lines it adds.</summary>
    public static Func<string, string, uint?>? LineResolver { get; set; }

    /// <summary>True in the process that runs the quests: a dedicated server, or a client hosting a world, while the world
    /// runs. A client joined to a server only shows what its server sends.</summary>
    internal static bool ServerSide => Lifecycle.IsServerRunning;

    public static QuestEngineStatus Status
    {
        get
        {
            lock (Gate)
                return new QuestEngineStatus(_started, ServerSide, Registry.Count, DialogueRegistry.Count, NpcRegistry.Count, Dialogues.OpenCount,
                    LoadErrorList.Count, _transitions, ActionsRun, ActionsFailed, QuestMarkers.Available, LoadErrorList.ToArray());
        }
    }

    /// <summary>The place a step found: on the ground, outside every base. Null until it is found. Tick thread.</summary>
    public static WorldPosition? PlaceOf(string quest, string step)
    {
        Game.AssertTickThread();
        return Find(quest) is { } q && q.Step(step) is { } s ? QuestPlaces.KnownFor(q, s) : null;
    }

    /// <summary>The creatures a step's <c>spawn</c> holds in the world for a holder now. Tick thread.</summary>
    public static IReadOnlyList<EntityId> SpawnedBy(string quest, string step, QuestHolder holder)
    {
        Game.AssertTickThread();
        return QuestPlaces.AliveOf(Find(quest)?.FullId ?? quest, step, holder.Key);
    }

    /// <summary>A step's <c>spawn</c> used up its retries: the game kept removing them (kiln.log has the error). Tick thread.</summary>
    public static bool SpawnGaveUp(string quest, string step, QuestHolder holder)
    {
        Game.AssertTickThread();
        return QuestPlaces.GaveUp(Find(quest)?.FullId ?? quest, step, holder.Key);
    }

    /// <summary>The last action outcomes: rewards paid, items taken, what failed and why (newest last).</summary>
    public static IReadOnlyList<string> RecentActions => QuestActions.RecentLines;

    /// <summary>Waypoint requests and writes of kiln.questmark so far.</summary>
    public static (ulong Requests, ulong Written) WaypointCounters => QuestMarkers.Counters;

    /// <summary>Evaluates a condition the way the engine does. Tick thread.</summary>
    public static bool Evaluate(QuestCondition condition, QuestContext context)
    {
        Game.AssertTickThread();
        return QuestActions.Holds(condition, context);
    }

    /// <summary>Starts the engine (Kiln does this at its first service tick; a mod may call it earlier).</summary>
    public static void EnsureStarted()
    {
        if (_started) return;
        _started = true;
        QuestsInternals.RunAs(null, () =>
        {
            ServiceTick.Add("quests", Tick);
            QuestCharacters.Start();
            Events.On<ModReloadedEvent>(e => ReloadMod(e.Id));
            NativeQuests.Start();  // the game's own journal, dialogue and markers (Native/)
        });
    }

    // ------------------------------------------------------------------------------------------ definitions --------

    /// <summary>Registers a quest as the calling mod's (it goes away when the mod unloads; states stay). Re-registering an
    /// id replaces it. Throws <see cref="ArgumentException"/> for an invalid definition.</summary>
    public static QuestDefinition Define(QuestDefinition quest)
    {
        ArgumentNullException.ThrowIfNull(quest);
        var mod = QuestsInternals.CurrentMod;
        quest.ModId = mod?.Info.Id ?? quest.ModId;
        return Add(quest, mod, false);
    }

    /// <summary>Registers an NPC a quest or conversation can refer to, as the calling mod's.</summary>
    public static QuestNpcDefinition DefineNpc(QuestNpcDefinition npc)
    {
        ArgumentNullException.ThrowIfNull(npc);
        var mod = QuestsInternals.CurrentMod;
        npc.ModId = mod?.Info.Id ?? npc.ModId;
        CheckId(npc.Id, "npc");
        EnsureStarted();
        lock (Gate) NpcRegistry[npc.FullId] = (npc, mod, false);
        if (mod is not null) QuestsInternals.OnRetiring(mod, () => { lock (Gate) NpcRegistry.Remove(npc.FullId); });
        NativeExport.Changed(npc.ModId);
        return npc;
    }

    private static QuestDefinition Add(QuestDefinition q, ModContext? owner, bool fromJson)
    {
        Validate(q);
        EnsureStarted();
        var entry = new Entry(q, owner, fromJson);
        lock (Gate) Registry[q.FullId] = entry;
        if (owner is not null) QuestsInternals.OnRetiring(owner, () => Remove(entry));
        QuestObjectives.Rebuild();
        if (!fromJson) NativeExport.Changed(q.ModId);
        QuestsInternals.Info($"quest {q.FullId} defined ({q.Scope}, {q.Steps.Count} steps, journal {q.Journal}, owner {owner?.Info.Id ?? (fromJson ? q.ModId + " data" : "kiln")})");
        return q;
    }

    private static void Remove(Entry e)
    {
        lock (Gate)
            if (Registry.TryGetValue(e.Def.FullId, out var cur) && ReferenceEquals(cur, e))
                Registry.Remove(e.Def.FullId);
        QuestObjectives.Rebuild();
        QuestsInternals.Info($"quest {e.Def.FullId} removed (its mod unloaded)");
    }

    // Kiln.Blocks resets data-only definitions without a ModContext. A stale block lifetime must not remove a later definition with the same id.
    internal static void ForgetBlock(QuestDefinition definition)
    {
        lock (Gate)
        {
            if (Registry.TryGetValue(definition.FullId, out var current) && !ReferenceEquals(current.Def, definition)) return;
            Registry.Remove(definition.FullId);
        }
        QuestObjectives.Rebuild();
        NativeExport.DefinedInData('q', definition.FullId, false);
        NativeExport.Changed(definition.ModId);
    }

    private static void CheckId(string id, string what)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
            throw new ArgumentException($"{what} id \"{id}\": letters, digits, '_' and '-'");
    }

    private static void Validate(QuestDefinition q)
    {
        CheckId(q.Id, "quest");
        if (q.Steps.Count == 0) throw new ArgumentException($"quest {q.Id}: no steps");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in q.Steps)
        {
            CheckId(s.Id, $"quest {q.Id}: step");
            if (!ids.Add(s.Id)) throw new ArgumentException($"quest {q.Id}: step \"{s.Id}\" twice");
            foreach (var o in s.Objectives)
                if (o.Count < 1) throw new ArgumentException($"quest {q.Id} step {s.Id}: an objective's count must be 1 or more");
        }
    }

    /// <summary>Every quest defined now.</summary>
    public static IReadOnlyList<QuestDefinition> Definitions
    {
        get
        {
            lock (Gate) return Registry.Values.Select(e => e.Def).OrderBy(d => d.FullId, StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>A quest by full id, or by its own id when only one mod has it.</summary>
    public static QuestDefinition? Find(string id, string? modId = null)
    {
        lock (Gate)
        {
            if (Registry.TryGetValue(id, out var e)) return e.Def;
            if (modId is { Length: > 0 } && Registry.TryGetValue($"{modId}:{id}", out e)) return e.Def;
            if (id.Contains(':')) return null;
            var m = Registry.Values.Where(x => x.Def.Id.Equals(id, StringComparison.Ordinal)).Take(2).ToArray();
            return m.Length == 1 ? m[0].Def : null;
        }
    }

    internal static QuestNpcDefinition? FindNpcDef(string id, string? modId)
    {
        lock (Gate)
        {
            if (NpcRegistry.TryGetValue(id, out var e)) return e.Def;
            if (modId is { Length: > 0 } && NpcRegistry.TryGetValue($"{modId}:{id}", out e)) return e.Def;
            if (id.Contains(':')) return null;
            var m = NpcRegistry.Values.Where(x => x.Def.Id.Equals(id, StringComparison.Ordinal)).Take(2).ToArray();
            return m.Length == 1 ? m[0].Def : null;
        }
    }

    /// <summary>Every NPC defined now.</summary>
    public static IReadOnlyList<QuestNpcDefinition> NpcDefinitions
    {
        get
        {
            lock (Gate) return NpcRegistry.Values.Select(e => e.Def).ToArray();
        }
    }

    /// <summary>Says that this entity is that NPC (it wins over the search by template). Kiln.Quests/Native and spawn
    /// actions call this; the binding lasts while the entity exists.</summary>
    public static void BindNpc(string fullNpcId, EntityId entity) => Dialogues.Bind(fullNpcId, entity);

    /// <summary>A code action for JSON's <c>{"call": "name"}</c>.</summary>
    public static void RegisterAction(string name, Func<QuestActionContext, Task> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(action);
        var mod = QuestsInternals.CurrentMod;
        lock (Gate) NamedActions[name] = (action, mod);
        if (mod is not null) QuestsInternals.OnRetiring(mod, () => { lock (Gate) NamedActions.Remove(name); });
    }

    /// <summary>A code condition for JSON's <c>{"predicate": "name"}</c>.</summary>
    public static void RegisterPredicate(string name, Func<QuestContext, bool> predicate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(predicate);
        var mod = QuestsInternals.CurrentMod;
        lock (Gate) NamedPredicates[name] = predicate;
        if (mod is not null) QuestsInternals.OnRetiring(mod, () => { lock (Gate) NamedPredicates.Remove(name); });
    }

    internal static (Func<QuestActionContext, Task> Run, ModContext? Owner)? NamedAction(string name)
    {
        lock (Gate) return NamedActions.TryGetValue(name, out var a) ? a : null;
    }

    /// <summary>The mod that defined a quest with code (null for a quest from a file).</summary>
    internal static ModContext? OwnerOf(QuestDefinition? q)
    {
        if (q is null) return null;
        lock (Gate) return Registry.TryGetValue(q.FullId, out var e) ? e.Owner : null;
    }

    internal static Func<QuestContext, bool>? NamedPredicate(string name)
    {
        lock (Gate) return NamedPredicates.GetValueOrDefault(name);
    }

    // ---------------------------------------------------------------------------------------- JSON of the mods -----

    /// <summary>Reads quests/*.json of one loaded mod (replacing what that mod's files defined before). Tick thread.</summary>
    internal static int LoadMod(string modId, string directory)
    {
        var pack = QuestJson.LoadDirectory(directory);
        lock (Gate)
        {
            LoadErrorList.RemoveAll(e => e.StartsWith(modId + ": ", StringComparison.Ordinal));
            foreach (var k in Registry.Where(kv => kv.Value.FromJson && kv.Value.Def.ModId == modId).Select(kv => kv.Key).ToArray()) Registry.Remove(k);
            foreach (var k in DialogueRegistry.Where(kv => kv.Value.FromJson && kv.Value.Def.ModId == modId).Select(kv => kv.Key).ToArray()) DialogueRegistry.Remove(k);
            foreach (var k in NpcRegistry.Where(kv => kv.Value.FromJson && kv.Value.Def.ModId == modId).Select(kv => kv.Key).ToArray()) NpcRegistry.Remove(k);
            JsonMods.Add(modId);
            foreach (var n in pack.Npcs)
            {
                n.ModId = modId;
                NpcRegistry[n.FullId] = (n, null, true);
            }
            foreach (var d in pack.Dialogues)
            {
                d.ModId = modId;
                DialogueRegistry[d.FullId] = (d, null, true);
            }
        }
        var count = 0;
        foreach (var q in pack.Quests)
        {
            q.ModId = modId;
            try
            {
                Add(q, null, true);
                count++;
            }
            catch (ArgumentException e)
            {
                pack.Errors.Add(e.Message);
            }
        }
        foreach (var d in pack.Dialogues) pack.Errors.AddRange(Dialogues.Check(d));
        lock (Gate) LoadErrorList.AddRange(pack.Errors.Select(e => $"{modId}: {e}"));
        foreach (var e in pack.Errors) QuestsInternals.Warn($"{modId}: {e}");
        QuestObjectives.Rebuild();
        return count;
    }

    /// <summary>Reads quests/*.json of every loaded mod again and keeps what code defined. States stay: a quest whose
    /// step is gone continues at the step of the same place. Tick thread. Returns the number of quests from files.</summary>
    public static int Reload()
    {
        Game.AssertTickThread();
        var n = 0;
        foreach (var m in Mods.All)
        {
            if (!m.IsLoaded || m.Directory.Length == 0) continue;
            if (!Directory.Exists(Path.Combine(m.Directory, "quests")) && !JsonMods.Contains(m.Id)) continue;
            n += LoadMod(m.Id, m.Directory);
        }
        CheckMods();
        QuestsInternals.Info($"definitions reloaded: {n} quest(s) from files, {Registry.Count} in all");
        return n;
    }

    private static void ReloadMod(string id)
    {
        var m = Mods.All.FirstOrDefault(x => x.Id == id);
        if (m is not { IsLoaded: true, Directory.Length: > 0 }) return;
        if (Directory.Exists(Path.Combine(m.Directory, "quests")) || JsonMods.Contains(id)) LoadMod(id, m.Directory);
    }

    /// <summary>Definitions from a mod that is no longer loaded go away (every ~5 s).</summary>
    private static void CheckMods()
    {
        string[] gone;
        var all = Mods.All;
        // a side "server" mod comes in when a hosted world starts, after this service started
        foreach (var m in all)
        {
            bool known;
            lock (Gate) known = JsonMods.Contains(m.Id);
            if (!known && m.IsLoaded && m.Directory.Length > 0 && Directory.Exists(Path.Combine(m.Directory, "quests"))) LoadMod(m.Id, m.Directory);
        }
        lock (Gate)
            gone = JsonMods.Where(id => !all.Any(m => m.Id == id && m.IsLoaded)).ToArray();
        foreach (var id in gone)
        {
            lock (Gate)
            {
                JsonMods.Remove(id);
                foreach (var k in Registry.Where(kv => kv.Value.FromJson && kv.Value.Def.ModId == id).Select(kv => kv.Key).ToArray()) Registry.Remove(k);
                foreach (var k in DialogueRegistry.Where(kv => kv.Value.FromJson && kv.Value.Def.ModId == id).Select(kv => kv.Key).ToArray()) DialogueRegistry.Remove(k);
                foreach (var k in NpcRegistry.Where(kv => kv.Value.FromJson && kv.Value.Def.ModId == id).Select(kv => kv.Key).ToArray()) NpcRegistry.Remove(k);
            }
            QuestObjectives.Rebuild();
            QuestsInternals.Info($"definitions of {id} removed (it is no longer loaded)");
        }
    }

    // ------------------------------------------------------------------------------------------- service tick ------
    private static void Tick(in TickInfo tick)
    {
        QuestTick.Drain();
        _ticks++;
        if (_ticks % 25 == 0) NativeExport.Flush();
        if (_ticks % 125 == 0) CheckMods();
        if (!ServerSide) return;
        if (!_commands && Lifecycle.ModsLoaded)
        {
            _commands = true;
            QuestCommands.Start();
        }
        lock (Gate)
            if (Registry.Count == 0 && DialogueRegistry.Count == 0) return;
        if (_ticks % 12 == 0) QuestObjectives.PollFast();
        if (_ticks % 25 == 0)
        {
            QuestMarkers.LogWrites();
            QuestObjectives.PollSlow();
            AutoStart();
            Dialogues.Maintain();
            NativeQuests.Poll();
            QuestPlaces.Poll();
        }
    }

    private static void AutoStart()
    {
        QuestDefinition[] autos;
        lock (Gate) autos = Registry.Values.Where(e => e.Def.Auto).Select(e => e.Def).ToArray();
        if (autos.Length == 0) return;
        var ready = (!Knowledge.Available || Knowledge.Status is { Applying: true }) && Saves.WorldDataReady;
        if (!ready) return;
        foreach (var q in autos)
        {
            if (q.Scope == QuestScope.World)
            {
                if (CanStart(q, QuestHolder.World)) StartOne(q, QuestHolder.World, false);
                continue;
            }
            foreach (var h in Online())
                if (CanStart(q, h)) StartOne(q, h, false);
        }
    }

    /// <summary>Players in game, as holders.</summary>
    internal static IReadOnlyList<QuestHolder> Online()
    {
        if (!Players.Available) return [];
        var list = new List<QuestHolder>();
        foreach (var p in Players.All)
            if (p.InGame) list.Add(QuestHolder.Of(p));
        return list;
    }

    // ---------------------------------------------------------------------------------------------- the books ------
    internal static QuestBook BookOf(QuestHolder h)
    {
        if (!h.IsWorld && h.Account == 0 && h.Character.IsNone) return Session(h.Key);
        try
        {
            return QuestsInternals.RunAs(QuestsInternals.DataOwner, () => h.IsWorld ? WorldData.Get<QuestBook>("book") : CharacterBook(h));
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or InvalidDataException)
        {
            if (!_dataFallbackLogged)
            {
                _dataFallbackLogged = true;
                QuestsInternals.Warn($"quest state stays in memory: {e.Message}");
            }
            return Session(h.Key);
        }
    }

    private static QuestBook Session(string key)
    {
        if (!SessionBooks.TryGetValue(key, out var b)) SessionBooks[key] = b = new QuestBook();
        return b;
    }

    /// <summary>The holder that owns a quest's state: a world quest's is the world's.</summary>
    private static QuestHolder Owner(QuestDefinition q, QuestHolder h) => q.Scope == QuestScope.World ? QuestHolder.World : h;

    private static QuestRecord? Record(QuestDefinition q, QuestHolder h) =>
        BookOf(Owner(q, h)).Quests.GetValueOrDefault(q.FullId);

    /// <summary>A holder's record of a quest (a world quest's is the world's); null: never started.</summary>
    internal static QuestRecord? RecordFor(QuestDefinition q, QuestHolder h) => Record(q, h);

    /// <summary>A record's current step (repairs the place when the definition changed).</summary>
    internal static QuestStep? CurrentStep(QuestDefinition q, QuestRecord r)
    {
        if (r.Status != QuestStatus.Active || q.Steps.Count == 0) return null;
        if (r.StepId is { } id && q.Step(id) is { } s)
        {
            r.StepIndex = q.Steps.IndexOf(s);
            return s;
        }
        var i = Math.Clamp(r.StepIndex, 0, q.Steps.Count - 1);
        QuestsInternals.Warn($"{q.FullId}: step \"{r.StepId}\" no longer exists; continuing at \"{q.Steps[i].Id}\"");
        r.StepIndex = i;
        r.StepId = q.Steps[i].Id;
        return q.Steps[i];
    }

    // ----------------------------------------------------------------------------------------- reading state -------

    /// <summary>A holder's status in a quest. Tick thread.</summary>
    public static QuestStatus StatusOf(string quest, QuestHolder holder)
    {
        Game.AssertTickThread();
        return StatusOf(Need(quest), holder);
    }

    internal static QuestStatus StatusOf(QuestDefinition q, QuestHolder h)
    {
        if (Record(q, h) is { } r) return r.Status;
        return RequiresHold(q, Owner(q, h)) ? QuestStatus.Available : QuestStatus.Locked;
    }

    /// <summary>A quest can be started now: never started, or done and repeatable, or failed and retryable, and its
    /// requirements hold. Tick thread.</summary>
    public static bool CanStart(string quest, QuestHolder holder)
    {
        Game.AssertTickThread();
        return CanStart(Need(quest), holder);
    }

    internal static bool CanStart(QuestDefinition q, QuestHolder h)
    {
        var o = Owner(q, h);
        var r = Record(q, o);
        if (r is not null)
        {
            if (r.Status == QuestStatus.Active) return false;
            if (r.Status == QuestStatus.Done && !q.Repeatable) return false;
            if (r.Status == QuestStatus.Failed && !q.Retryable) return false;
        }
        return RequiresHold(q, o);
    }

    private static bool RequiresHold(QuestDefinition q, QuestHolder h)
    {
        var ctx = new QuestContext(h, q, EntityId.None, q.ModId);
        foreach (var c in q.Requires)
            if (!QuestActions.Holds(c, ctx)) return false;
        return true;
    }

    /// <summary>A holder's standing in a quest (null when never started). Tick thread.</summary>
    public static QuestProgress? Progress(string quest, QuestHolder holder)
    {
        Game.AssertTickThread();
        var q = Need(quest);
        var r = Record(q, holder);
        return r is null ? null : ProgressOf(q, Owner(q, holder), r);
    }

    private static QuestProgress ProgressOf(QuestDefinition q, QuestHolder h, QuestRecord r)
    {
        var step = CurrentStep(q, r);
        return new QuestProgress(q, h, r.Status, step?.Id, r.StepIndex, new Dictionary<string, int>(r.Counters), r.Completions);
    }

    /// <summary>Every quest a holder has a record of, world quests included. Tick thread.</summary>
    public static IReadOnlyList<QuestProgress> ProgressOf(QuestHolder holder)
    {
        Game.AssertTickThread();
        var list = new List<QuestProgress>();
        foreach (var q in Definitions)
            if (Record(q, holder) is { } r) list.Add(ProgressOf(q, Owner(q, holder), r));
        return list;
    }

    /// <summary>Every quest a player in game has a record of (<see cref="ProgressOf(QuestHolder)"/>).</summary>
    public static IReadOnlyList<QuestProgress> ProgressOf(EntityId player) => ProgressOf(QuestHolder.Of(player));

    /// <summary>The tracked quest an old save holds. The game tracks quests itself now, in the character's save. For Kiln.Admin's state op, until it drops the field.</summary>
    public static string? TrackedBy(QuestHolder holder)
    {
        Game.AssertTickThread();
        return BookOf(holder).Tracked;
    }

    private static QuestDefinition Need(string id) => Find(id) ?? throw new KeyNotFoundException($"no quest \"{id}\"");

    // ------------------------------------------------------------------------------------------------ changes ------

    /// <summary>Starts a quest for a holder (a party quest also for players near them who can start it). Refused when
    /// it cannot start, unless <paramref name="force"/>. Tick thread.</summary>
    public static QuestResult Start(string quest, QuestHolder holder, bool force = false)
    {
        Game.AssertTickThread();
        var q = Find(quest);
        return q is null ? QuestResult.No($"no quest \"{quest}\"") : Start(q, holder, force);
    }

    internal static QuestResult Start(QuestDefinition q, QuestHolder h, bool force)
    {
        var r = StartOne(q, h, force);
        if (r.Ok && q.Scope == QuestScope.Party)
            foreach (var o in QuestObjectives.PartyOf(h, q.PartyRadius))
                if (CanStart(q, o)) StartOne(q, o, force);
        return r;
    }

    private static QuestResult StartOne(QuestDefinition q, QuestHolder h, bool force)
    {
        var o = Owner(q, h);
        var book = BookOf(o);
        var old = book.Quests.GetValueOrDefault(q.FullId);
        if (old is { Status: QuestStatus.Active }) return QuestResult.No("it is already active", QuestStatus.Active);
        if (!force && !CanStart(q, o))
            return QuestResult.No(old is null ? "its requirements do not hold" : old.Status == QuestStatus.Done ? "it is done" : "it failed and cannot be retried",
                old?.Status ?? QuestStatus.Locked);
        if (Events.HasSubscribers<QuestStartedEvent>() && Events.Post(new QuestStartedEvent(q, o)) is { Canceled: true } ev)
            return QuestResult.No($"canceled by {ev.CanceledBy ?? "a handler"}", old?.Status ?? QuestStatus.Available);
        var oldStatus = old?.Status ?? QuestStatus.Available;
        var rec = new QuestRecord { StepId = q.Steps[0].Id, StartedAt = Now, Completions = old?.Completions ?? 0 };
        book.Quests[q.FullId] = rec;
        book.Revision++;
        _transitions++;
        QuestsInternals.Info($"{o.Key} {q.FullId}: started");
        QuestPresent.Started(o, q);  // chat quests; the game's journal tells of native ones by itself
        var ctx = new QuestActionContext(o, q, EntityId.None, q.ModId);
        if (q.OnStart.Count > 0) _ = QuestActions.RunAll(q.OnStart, ctx, "onStart", true);
        EnterStep(q, o, rec, q.Steps[0]);
        Changed(q, o, oldStatus, rec);
        return new QuestResult(true, "", QuestStatus.Active);
    }

    private static void EnterStep(QuestDefinition q, QuestHolder h, QuestRecord rec, QuestStep step)
    {
        rec.StepId = step.Id;
        rec.StepIndex = q.Steps.IndexOf(step);
        rec.StepOrigin = null;
        rec.EnteredAt = Environment.TickCount64;
        if (!h.Entity.IsNone && QuestObjectives.PositionOf(h) is { } p) rec.StepOrigin = [p.X, p.Y, p.Z];
        else if (QuestInput.PositionOf(h) is { } sp) rec.StepOrigin = [sp.X, sp.Y, sp.Z];
        QuestPresent.Step(h, q, rec, step);
        QuestPlaces.EnterStep(q, h, rec, step);
        if (step.OnEnter.Count > 0) _ = QuestActions.RunAll(step.OnEnter, new QuestActionContext(h, q, EntityId.None, q.ModId), "onEnter", true);
    }

    /// <summary>How long a step in the game's journal stays open when a player already fulfils it, so the journal, the
    /// notification and the marker get to show it (collect passed at once for a player already holding the items).</summary>
    private const long MinimumStepMs = 3000;

    /// <summary>Called when a counter changed: finishes the step when all objectives are full.</summary>
    internal static void CheckStep(QuestDefinition q, QuestHolder h, QuestRecord rec, QuestStep step)
    {
        if (step.Objectives.Count == 0) return;
        for (var i = 0; i < step.Objectives.Count; i++)
            if (rec.Counters.GetValueOrDefault(QuestRecord.CounterKey(step.Id, i)) < step.Objectives[i].Count) return;
        // Asked again by the next poll: only objectives that are a state of the world can be full at once, and those are polled.
        if (q.Journal == QuestJournalMode.Native && !QuestInput.IsSynthetic(h) && step.Objectives.Any(o => o.IsLevel) &&
            Environment.TickCount64 - rec.EnteredAt < MinimumStepMs) return;
        FinishStep(q, h, rec, step);
    }

    private static QuestResult FinishStep(QuestDefinition q, QuestHolder h, QuestRecord rec, QuestStep step)
    {
        var index = q.Steps.IndexOf(step);
        var next = index + 1 < q.Steps.Count ? q.Steps[index + 1] : null;
        if (Events.HasSubscribers<QuestStepEvent>() && Events.Post(new QuestStepEvent(q, h, step, next)) is { Canceled: true } ev)
            return QuestResult.No($"canceled by {ev.CanceledBy ?? "a handler"}", QuestStatus.Active);
        _transitions++;
        QuestsInternals.Info($"{h.Key} {q.FullId}: step {step.Id} done");
        QuestPlaces.LeaveStep(q, h, step);
        if (step.OnDone.Count > 0) _ = QuestActions.RunAll(step.OnDone, new QuestActionContext(h, q, EntityId.None, q.ModId), "onDone", true);
        var book = BookOf(h);
        book.Revision++;
        if (next is null) return CompleteOne(q, h, rec, false);
        EnterStep(q, h, rec, next);
        Changed(q, h, QuestStatus.Active, rec);
        return new QuestResult(true, "", QuestStatus.Active);
    }

    /// <summary>Finishes a holder's current step: objectives that are still open are refused unless they are talk or
    /// custom (a conversation or code finishes those) or <paramref name="force"/>; a deliver objective takes its items
    /// now and is refused without them. <paramref name="step"/>: the step that must be current. Tick thread; completes on it.</summary>
    public static async Task<QuestResult> Advance(string quest, QuestHolder holder, string? step = null, bool force = false)
    {
        Game.AssertTickThread();
        var q = Find(quest);
        if (q is null) return QuestResult.No($"no quest \"{quest}\"");
        var o = Owner(q, holder);
        if (BookOf(o).Quests.GetValueOrDefault(q.FullId) is not { Status: QuestStatus.Active } rec) return QuestResult.No("it is not active");
        var cur = CurrentStep(q, rec)!;
        if (step is not null && cur.Id != step) return QuestResult.No($"the current step is \"{cur.Id}\", not \"{step}\"", QuestStatus.Active);
        for (var i = 0; i < cur.Objectives.Count; i++)
        {
            var obj = cur.Objectives[i];
            var key = QuestRecord.CounterKey(cur.Id, i);
            var have = rec.Counters.GetValueOrDefault(key);
            if (have >= obj.Count) continue;
            if (obj.Kind == QuestObjectiveKind.Deliver && !force)
            {
                var taken = await QuestObjectives.TakeForDeliver(holder, obj, obj.Count - have).OnTick();
                if (taken is not null) return QuestResult.No(taken, QuestStatus.Active);
                rec.Counters[key] = obj.Count;
                continue;
            }
            if (!force && obj.Kind is not (QuestObjectiveKind.Talk or QuestObjectiveKind.Custom))
                return QuestResult.No($"objective {i + 1} of step \"{cur.Id}\" is not finished ({have}/{obj.Count})", QuestStatus.Active);
            rec.Counters[key] = obj.Count;
        }
        // the state may have moved while the items were taken
        if (rec.Status != QuestStatus.Active || rec.StepId != cur.Id) return new QuestResult(true, "", rec.Status);
        return FinishStep(q, o, rec, cur);
    }

    /// <summary>Completes a quest for a holder now (rewards paid; past the remaining steps). Tick thread.</summary>
    public static QuestResult Complete(string quest, QuestHolder holder)
    {
        Game.AssertTickThread();
        var q = Find(quest);
        if (q is null) return QuestResult.No($"no quest \"{quest}\"");
        var o = Owner(q, holder);
        var book = BookOf(o);
        var rec = book.Quests.GetValueOrDefault(q.FullId);
        if (rec is { Status: QuestStatus.Done }) return QuestResult.No("it is done already", QuestStatus.Done);
        rec ??= book.Quests[q.FullId] = new QuestRecord { StartedAt = Now };
        return CompleteOne(q, o, rec, true);
    }

    private static QuestResult CompleteOne(QuestDefinition q, QuestHolder h, QuestRecord rec, bool forced)
    {
        var old = rec.Status;
        if (Events.HasSubscribers<QuestCompletedEvent>() && Events.Post(new QuestCompletedEvent(q, h)) is { Canceled: true } ev)
            return QuestResult.No($"canceled by {ev.CanceledBy ?? "a handler"}", old);
        rec.Status = QuestStatus.Done;
        rec.StepId = null;
        rec.FinishedAt = Now;
        rec.Completions++;
        rec.RewardsPending = q.Rewards.Count > 0;
        BookOf(h).Revision++;
        _transitions++;
        QuestsInternals.Info($"{h.Key} {q.FullId}: complete{(forced ? " (forced)" : "")} ({rec.Completions}x)");
        QuestPresent.Completed(h, q);
        QuestPlaces.LeaveQuest(q, h);
        PayRewards(q, h, rec);
        Changed(q, h, old, rec);
        return new QuestResult(true, "", QuestStatus.Done);
    }

    private static void PayRewards(QuestDefinition q, QuestHolder h, QuestRecord rec)
    {
        if (!rec.RewardsPending) return;
        // an offline player gets them at the next join; a synthetic holder never comes: its rewards are run and skipped
        if (h.Entity.IsNone && !h.IsWorld && !QuestInput.IsSynthetic(h)) return;
        rec.RewardsPending = false;
        _ = QuestActions.RunAll(q.Rewards, new QuestActionContext(h, q, EntityId.None, q.ModId), "rewards", true);
    }

    /// <summary>Rewards owed to a player who just came in.</summary>
    internal static void PayPending(QuestHolder h)
    {
        foreach (var q in Definitions)
            if (q.Scope != QuestScope.World && Record(q, h) is { RewardsPending: true } r)
            {
                QuestsInternals.Info($"{h.Key} {q.FullId}: paying the rewards owed since an earlier completion");
                PayRewards(q, h, r);
            }
    }

    /// <summary>Fails an active quest for a holder. Tick thread.</summary>
    public static QuestResult Fail(string quest, QuestHolder holder, string reason = "")
    {
        Game.AssertTickThread();
        var q = Find(quest);
        if (q is null) return QuestResult.No($"no quest \"{quest}\"");
        var o = Owner(q, holder);
        if (BookOf(o).Quests.GetValueOrDefault(q.FullId) is not { Status: QuestStatus.Active } rec) return QuestResult.No("it is not active");
        if (Events.HasSubscribers<QuestFailedEvent>() && Events.Post(new QuestFailedEvent(q, o, reason)) is { Canceled: true } ev)
            return QuestResult.No($"canceled by {ev.CanceledBy ?? "a handler"}", QuestStatus.Active);
        rec.Status = QuestStatus.Failed;
        rec.StepId = null;
        rec.FinishedAt = Now;
        BookOf(o).Revision++;
        _transitions++;
        QuestsInternals.Info($"{o.Key} {q.FullId}: failed{(reason.Length > 0 ? $" ({reason})" : "")}");
        QuestPresent.Failed(o, q);
        QuestPlaces.LeaveQuest(q, o);
        if (q.OnFail.Count > 0) _ = QuestActions.RunAll(q.OnFail, new QuestActionContext(o, q, EntityId.None, q.ModId), "onFail", true);
        Changed(q, o, QuestStatus.Active, rec);
        return new QuestResult(true, "", QuestStatus.Failed);
    }

    /// <summary>A player gives up a quest: back to available (when it may be retried), else failed.</summary>
    public static QuestResult Abandon(string quest, QuestHolder holder)
    {
        Game.AssertTickThread();
        var q = Find(quest);
        if (q is null) return QuestResult.No($"no quest \"{quest}\"");
        if (q.Scope == QuestScope.World || !q.Abandonable) return QuestResult.No("this quest cannot be given up", StatusOf(q, holder));
        if (Record(q, holder) is not { Status: QuestStatus.Active }) return QuestResult.No("it is not active");
        if (!q.Retryable) return Fail(quest, holder, "abandoned");
        return Reset(quest, holder);
    }

    /// <summary>Forgets a holder's record of a quest (as if never started). Knowledge the quest granted stays. Tick thread.</summary>
    public static QuestResult Reset(string quest, QuestHolder holder)
    {
        Game.AssertTickThread();
        var q = Find(quest);
        if (q is null) return QuestResult.No($"no quest \"{quest}\"");
        var o = Owner(q, holder);
        var book = BookOf(o);
        if (!book.Quests.Remove(q.FullId, out var rec)) return QuestResult.No("no record", StatusOf(q, o));
        book.Revision++;
        _transitions++;
        QuestsInternals.Info($"{o.Key} {q.FullId}: reset");
        QuestPlaces.LeaveQuest(q, o);
        var now = StatusOf(q, o);
        if (Events.HasSubscribers<QuestStateChangedEvent>()) Events.Post(new QuestStateChangedEvent(q, o, rec.Status, now, null, 0));
        return new QuestResult(true, "", now);
    }

    /// <summary>Puts a holder at a step: active there with the step's counters at 0 (an admin's tool: no rewards, no
    /// hooks of skipped steps). Tick thread.</summary>
    public static QuestResult SetStep(string quest, QuestHolder holder, string step)
    {
        Game.AssertTickThread();
        var q = Find(quest);
        if (q is null) return QuestResult.No($"no quest \"{quest}\"");
        var s = q.Step(step);
        if (s is null) return QuestResult.No($"no step \"{step}\" (it has {string.Join(", ", q.Steps.Select(x => x.Id))})");
        var o = Owner(q, holder);
        var book = BookOf(o);
        var rec = book.Quests.GetValueOrDefault(q.FullId);
        var old = rec?.Status ?? QuestStatus.Available;
        rec ??= book.Quests[q.FullId] = new QuestRecord { StartedAt = Now };
        rec.Status = QuestStatus.Active;
        rec.RewardsPending = false;
        foreach (var k in rec.Counters.Keys.Where(k => k.StartsWith(s.Id + "#", StringComparison.Ordinal)).ToArray()) rec.Counters.Remove(k);
        book.Revision++;
        _transitions++;
        QuestsInternals.Info($"{o.Key} {q.FullId}: set to step {s.Id}");
        EnterStep(q, o, rec, s);
        Changed(q, o, old, rec);
        return new QuestResult(true, "", QuestStatus.Active);
    }

    /// <summary>Adds to every open custom objective with this signal: the holder's quests (and party's, and world quests);
    /// with no holder every player in game and the world. Tick thread.</summary>
    public static void Signal(string name, QuestHolder? holder = null, int amount = 1)
    {
        Game.AssertTickThread();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        QuestObjectives.OnSignal(name, holder, amount);
    }

    private static void Changed(QuestDefinition q, QuestHolder h, QuestStatus old, QuestRecord rec)
    {
        if (Events.HasSubscribers<QuestStateChangedEvent>())
            Events.Post(new QuestStateChangedEvent(q, h, old, rec.Status, rec.StepId, rec.StepIndex));
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

/// <summary>Kiln.Quests' use of Kiln.Api internals (InternalsVisibleTo Kiln.Quests): the calling mod, its Retiring event,
/// the data owner of quest state, Kiln's log.</summary>
internal static class QuestsInternals
{
    public static ModContext? CurrentMod
    {
        get => ModContext.CurrentMod;
        set => ModContext.CurrentMod = value;
    }

    public static void RunAs(ModContext? mod, Action action)
    {
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = mod;
        try
        {
            action();
        }
        finally
        {
            ModContext.CurrentMod = outer;
        }
    }

    public static T RunAs<T>(ModContext? mod, Func<T> f)
    {
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = mod;
        try
        {
            return f();
        }
        finally
        {
            ModContext.CurrentMod = outer;
        }
    }

    public static void OnRetiring(ModContext mod, Action cleanup) => mod.Retiring += cleanup;

    /// <summary>Calls a mod's code as that mod: its log, and <c>await</c> in it comes back to the tick thread through its scheduler.</summary>
    public static Task CallAs(ModContext? mod, Func<Task> run)
    {
        if (mod is null) return run();
        var outerMod = ModContext.CurrentMod;
        var outerSync = SynchronizationContext.Current;
        ModContext.CurrentMod = mod;
        SynchronizationContext.SetSynchronizationContext(mod.Scheduler.SynchronizationContext);
        try
        {
            return run();
        }
        finally
        {
            ModContext.CurrentMod = outerMod;
            SynchronizationContext.SetSynchronizationContext(outerSync);
        }
    }

    private static ModContext? _data;

    /// <summary>Quest state lives in Kiln.Saves under this owner ("kiln-quests"), whichever mod defined the quest, so a
    /// reload or a quest moved to another mod keeps every character's progress, and data-only mods have an owner.</summary>
    public static ModContext DataOwner => _data ??= HostAccess.CreateContext(
        new ModInfo("kiln-quests", "Kiln quests", "1.0.0", Path.Combine(KilnDir(), "host")), 0, Path.Combine(KilnDir(), "data", "kiln-quests"));

    private static string KilnDir()
    {
        var host = Path.GetDirectoryName(typeof(QuestsInternals).Assembly.Location) ?? ".";
        return Path.GetDirectoryName(host) ?? host;
    }

    public static void Log(LogLevel level, string text) => Backend.Current.Log(0, level, "quests: " + text);
    public static void Info(string s) => Log(LogLevel.Info, s);
    public static void Warn(string s) => Log(LogLevel.Warn, s);
    public static void Error(string s) => Log(LogLevel.Error, s);
}

/// <summary>Started by Kiln (ServiceTick.AutoStart) at its first service tick: the JSON of every loaded mod.</summary>
internal static class QuestsStartup
{
    static QuestsStartup()
    {
        try
        {
            Quests.EnsureStarted();
            var n = 0;
            foreach (var m in Mods.All)
            {
                if (!m.IsLoaded || m.Directory.Length == 0 || !Directory.Exists(Path.Combine(m.Directory, "quests"))) continue;
                n += Quests.LoadMod(m.Id, m.Directory);
            }
            QuestsInternals.Info($"started: {n} quest(s) from loaded mods' quests/*.json, {Quests.Definitions.Count} in all");
        }
        catch (Exception ex)
        {
            QuestsInternals.Warn($"startup: {ex.Message}");
        }
    }
}
