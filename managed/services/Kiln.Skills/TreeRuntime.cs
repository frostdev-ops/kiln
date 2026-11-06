// SPDX-License-Identifier: MIT
// Kiln.Skills runtime (server side of Kiln talent trees): the registry, each character's state in Kiln.Saves carried
// character data (it goes with the character, like the game's own skill nodes in the character save), unlock / lock /
// respec / grant, points, and the effects (applied at unlock and join, removed at lock, respec, leave and unload; kept
// alive by a maintenance pass every ~2 s).
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>A character's state in one tree, as stored in carried character data ("talents:&lt;tree id&gt;").</summary>
public sealed class TalentTreeState
{
    /// <summary>node id -> level (1..MaxLevel); absent = locked.</summary>
    public Dictionary<string, int> Levels { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Points given by code (<see cref="Skills.Grant(string, TalentHolder, int)"/>).</summary>
    public int Granted { get; set; }
    /// <summary>The character level / game skill-point total seen last (for offline reads).</summary>
    public int LastLevel { get; set; }
    public int LastGameTotal { get; set; }
    /// <summary>+1 per change.</summary>
    public long Revision { get; set; }
}

/// <summary>A character's points in one tree.</summary>
public readonly record struct TalentPointsInfo(int Total, int Spent, int Available)
{
    public override string ToString() => $"{Available} of {Total} ({Spent} spent)";
}

/// <summary>The answer of an unlock / lock / respec / grant.</summary>
public sealed record TalentResult(bool Ok, string Reason, int Level, TalentPointsInfo Points)
{
    internal static TalentResult No(string why, int level = 0, TalentPointsInfo points = default) => new(false, why, level, points);
    public override string ToString() => Ok ? $"ok (level {Level}, points {Points})" : $"refused: {Reason}";
}

/// <summary>Kiln talent trees (and, in <see cref="GameTree"/>, nodes in the game's own skill tree).</summary>
public static partial class Skills
{
    internal sealed class Entry
    {
        public required TalentTree Tree;
        public ModContext? Owner;
        public object? Ui;  // TreeScreen (client)
    }

    internal sealed class ActiveEffect
    {
        public required TalentEffect Effect;
        public EntityId Impact;
        public long NextRefresh;
        public TalentContext? Context;
    }

    internal sealed class ActiveNode
    {
        public int Level;
        public readonly List<ActiveEffect> Effects = new();
    }

    internal static readonly object Gate = new();
    internal static readonly Dictionary<string, Entry> Registry = new(StringComparer.Ordinal);
    private static readonly Dictionary<(uint Entity, string Tree, string Node), ActiveNode> Active = new();
    private static readonly Dictionary<(string Tree, uint Entity), TalentTreeState> Ephemeral = new();
    private static readonly Dictionary<uint, ulong> SteamOf = new();
    private static readonly HashSet<(string Tree, uint Entity)> BudgetPending = new();
    private static bool _started;

    public static bool Available => true;

    /// <summary>Every registered tree.</summary>
    public static IReadOnlyList<TalentTree> Trees
    {
        get
        {
            lock (Gate) return Registry.Values.Select(e => e.Tree).ToArray();
        }
    }

    public static TalentTree? Find(string id)
    {
        lock (Gate) return Registry.TryGetValue(id, out var e) ? e.Tree : null;
    }

    /// <summary>Registers a tree as the calling mod's (it goes away when the mod unloads; players' states stay in
    /// that mod's player data). Call it on both sides (OnLoad of a side "both" mod): the server runs it, a client
    /// draws it. Re-registering an id replaces the tree.</summary>
    public static TalentTree Register(TalentTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        Validate(tree);
        var mod = SkillsInternals.CurrentMod;
        tree.Directory ??= mod?.Info.Directory;
        Entry e;
        lock (Gate)
        {
            if (Registry.TryGetValue(tree.Id, out var old)) Unregister(old, "replaced");
            e = new Entry { Tree = tree, Owner = mod };
            Registry[tree.Id] = e;
        }
        Start();
        if (mod is not null) SkillsInternals.OnRetiring(mod, () => Unregister(e, "its mod unloaded"));
        TalentUi.Attach(e);
        SkillsInternals.Info($"tree {tree.Id} registered ({tree.Nodes.Count} nodes, points {tree.Points}, owner {mod?.Info.Id ?? "kiln"})");
        return tree;
    }

    /// <summary>Loads the "trees" of every skills/*.json in <paramref name="modDirectory"/> (default: the calling mod's
    /// folder) and registers them as the calling mod's. Returns the trees; errors are logged.</summary>
    public static IReadOnlyList<TalentTree> LoadTrees(string? modDirectory = null) =>
        LoadTreesOf(modDirectory ?? SkillsInternals.CurrentMod?.Info.Directory ?? throw new ArgumentException("no mod directory"), false);

    /// <summary>Starts Kiln.Skills' channel, /talents and its tick (Kiln does this at its first service tick).</summary>
    public static void EnsureStarted() => Start();

    internal static IReadOnlyList<TalentTree> LoadTreesOf(string dir, bool skipRegistered)
    {
        var list = new List<TalentTree>();
        var skills = Path.Combine(dir, "skills");
        if (!System.IO.Directory.Exists(skills)) return list;
        foreach (var f in System.IO.Directory.GetFiles(skills, "*.json").OrderBy(x => x, StringComparer.Ordinal))
        {
            try
            {
                var j = JsonNode.Parse(File.ReadAllText(f), documentOptions: new System.Text.Json.JsonDocumentOptions
                {
                    CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
                if (j?["trees"] is not JsonArray a) continue;
                foreach (var t in a.OfType<JsonObject>())
                {
                    if (skipRegistered && t["id"]?.GetValue<string>() is { } tid && Find(tid) is not null) continue;
                    list.Add(Register(TalentJson.Tree(t, dir)));
                }
            }
            catch (Exception ex)
            {
                SkillsInternals.Warn($"{f}: {ex.Message}");
            }
        }
        return list;
    }

    private static void Validate(TalentTree t)
    {
        if (string.IsNullOrWhiteSpace(t.Id) || t.Id.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or ':')))
            throw new ArgumentException($"tree id \"{t.Id}\": letters, digits, '.', '_', '-', ':'");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in t.Nodes)
        {
            if (!ids.Add(n.Id)) throw new ArgumentException($"tree {t.Id}: node \"{n.Id}\" twice");
            if (n.MaxLevel < 1 || n.MaxLevel > 99) throw new ArgumentException($"tree {t.Id} node {n.Id}: MaxLevel 1..99");
            if (n.Cost < 0) throw new ArgumentException($"tree {t.Id} node {n.Id}: Cost >= 0");
            if (n.Column < 0 || n.Row < 0 || n.Column > 31 || n.Row > 31) throw new ArgumentException($"tree {t.Id} node {n.Id}: grid 0..31");
        }
        foreach (var n in t.Nodes)
            foreach (var r in n.Requires)
                if (!ids.Contains(r)) throw new ArgumentException($"tree {t.Id} node {n.Id}: requires unknown node \"{r}\"");
        var cells = t.Nodes.GroupBy(n => (n.Column, n.Row)).FirstOrDefault(g => g.Count() > 1);
        if (cells is not null) throw new ArgumentException($"tree {t.Id}: two nodes at column {cells.Key.Column}, row {cells.Key.Row}");
    }

    private static void Unregister(Entry e, string why)
    {
        lock (Gate)
            if (Registry.TryGetValue(e.Tree.Id, out var cur) && cur == e)
                Registry.Remove(e.Tree.Id);
        TalentUi.Detach(e);
        if (Game.IsTickThread)
            foreach (var key in Active.Keys.Where(k => k.Tree == e.Tree.Id).ToArray())
                RemoveNode(e, key);
        SkillsInternals.Info($"tree {e.Tree.Id} unregistered ({why})");
    }

    private static void Start()
    {
        if (_started) return;
        _started = true;
        SkillsInternals.RunAs(null, () =>
        {
            try
            {
                Events.On<AbilityCastEvent>(OnAbilityCast);
            }
            catch (Exception ex)
            {
                SkillsInternals.Warn($"ability gate: {ex.Message}");
            }
            var step = 0UL;
            ServiceTick.Add("skills", (in TickInfo _) =>
            {
                try
                {
                    GameShadow.Tick();
                    TalentNet.DrainHost();
                }
                catch (Exception ex)
                {
                    SkillsInternals.Warn($"shadow table: {ex.Message}");
                }
                if (++step % 50 != 0) return;
                Entry[] all;
                lock (Gate) all = Registry.Values.ToArray();
                foreach (var en in all)
                {
                    Maintain(en);
                    TalentNet.RefreshHost(en);
                }
                foreach (var gone in GameTree.TakeStrips())
                    if (Events.HasSubscribers<GameNodesRefundedEvent>()) Events.Post(gone);
            });
            TalentNet.Start();
            TalentCommands.Start();
            try
            {
                CharacterData.EnsureStarted();
                Events.On<CharacterImportedEvent>(OnCharacterImported);
            }
            catch (Exception ex)
            {
                SkillsInternals.Warn($"character data: {ex.Message}");
            }
        });
    }

    // ------------------------------------------------------------------------------------------- holders, state ----

    /// <summary>The holder of a player entity in game: the game's character, the Steam id from kiln.players.</summary>
    public static TalentHolder HolderOf(EntityId player)
    {
        var p = Players.Available ? Players.Find(player) : null;
        if (p is null) return new TalentHolder(SteamOf.GetValueOrDefault(player.Value), player);
        var steam = AccountOf(p);
        if (steam != 0) SteamOf[player.Value] = steam;
        return new TalentHolder(steam, player) { Character = CharacterData.KeyOf(player) };
    }

    /// <summary>The holder of a character, online or not. <paramref name="legacySteamId"/>: the Steam or account id its data
    /// may still be filed under from before characters (it moves to the first character that asks).</summary>
    public static TalentHolder HolderOf(CharacterKey character, ulong legacySteamId = 0) => new(legacySteamId, EntityId.None) { Character = character };

    /// <summary>The ids a holder's data may be filed under from before characters.</summary>
    private static ulong[] LegacyIds(TalentHolder h)
    {
        var p = h.Entity.IsNone || !Players.Available ? null : Players.Find(h.Entity);
        return [h.SteamId, p?.SteamId ?? 0, p?.AccountId ?? 0, p is not null && Kiln.Client.Available && Kiln.Client.IsHosting && p.Slot == 0 ? HostKey : 0];
    }

    /// <summary>A holder's data under the owner "kiln-skills": carried with the character (the old account-wide entry moves
    /// to the first character that asks). Account data only when the game gave no character id.</summary>
    internal static T DataOf<T>(TalentHolder h, string key) where T : class, new() => SkillsInternals.RunAs(SkillsInternals.DataOwner, () =>
    {
        var c = h.Character;
        if (c.IsNone && h.Entity.IsNone && h.SteamId != 0) c = CharacterData.LastOf(h.SteamId);
        if (c.IsNone || c.IsAccountOnly) return PlayerData.Get<T>(h.SteamId != 0 ? h.SteamId : c.Account, key);
        if (!CharacterData.Carried.Contains(c, key)) CharacterData.Carried.MigrateFromPlayerData(c, key, LegacyIds(h));
        return CharacterData.Carried.Get<T>(c, key);
    });

    /// <summary>The save key of a player: the Steam id, else the game's account id (a hosting player in a local game
    /// may have no Steam id in kiln.players).</summary>
    internal static ulong AccountOf(Player p) =>
        p.SteamId != 0 ? p.SteamId : p.AccountId != 0 ? p.AccountId
        : Kiln.Client.Available && Kiln.Client.IsHosting && p.Slot == 0 ? HostKey : 0;

    /// <summary>The save key of the hosting player of a local game when the game gives no Steam / account id: the
    /// world's host (whichever character hosts it).</summary>
    internal const ulong HostKey = 1;

    internal static Entry Need(string tree)
    {
        lock (Gate) return Registry.TryGetValue(tree, out var e) ? e : throw new KeyNotFoundException($"no talent tree \"{tree}\"");
    }

    internal static TalentTreeState StateOf(Entry e, TalentHolder h)
    {
        if (h.SteamId == 0 && h.Character.IsNone)
        {
            // no character and no Steam id (a lab entity, a player kiln.players does not know): session only
            if (!Ephemeral.TryGetValue((e.Tree.Id, h.Entity.Value), out var s)) Ephemeral[(e.Tree.Id, h.Entity.Value)] = s = new TalentTreeState();
            return s;
        }
        return DataOf<TalentTreeState>(h, "talents:" + e.Tree.Id);
    }

    // ------------------------------------------------------------------------------------- what a client brought ----

    /// <summary>A character's carried data came from its client: client-side and editable, like the game's character save. So it
    /// gets the checks the game gives a character at login: broken values go, levels are cut to what the tree allows, nodes whose
    /// requirements don't hold go, and a tree that costs more than the character has is reset for free. Entries of trees and
    /// nodes this server doesn't have stay as they are (not applied, passed on).</summary>
    private static void OnCharacterImported(CharacterImportedEvent ev)
    {
        var h = ev.Player.IsNone ? HolderOf(ev.Character) : HolderOf(ev.Player);
        SkillsInternals.RunAs(SkillsInternals.DataOwner, () =>
        {
            foreach (var key in CharacterData.Carried.Keys(ev.Character))
            {
                try
                {
                    if (key == GameShadow.DataKey) CharacterData.Carried.TryGet<GameShadowState>(ev.Character, key, out _);
                    else if (key.StartsWith("talents:", StringComparison.Ordinal)) CharacterData.Carried.TryGet<TalentTreeState>(ev.Character, key, out _);
                }
                catch (InvalidDataException ex)
                {
                    SkillsInternals.Warn($"character {ev.Character}: imported '{key}' is unusable, dropped: {ex.Message}");
                    CharacterData.Carried.Remove(ev.Character, key);
                }
            }
        });
        Entry[] all;
        lock (Gate) all = Registry.Values.ToArray();
        foreach (var e in all)
        {
            CheckImported(e, h);
            if (!h.Entity.IsNone) TalentNet.Push(e, h);
        }
        if (!h.Entity.IsNone) GameShadow.Reload(h.Entity);
    }

    private static void CheckImported(Entry e, TalentHolder h)
    {
        var s = StateOf(e, h);
        var fixes = new List<string>();
        foreach (var (id, level) in s.Levels.ToArray())
        {
            if (e.Tree.Node(id) is not { } n) continue;
            if (level < 1)
            {
                s.Levels.Remove(id);
                fixes.Add($"{id}: level {level}");
            }
            else if (level > n.MaxLevel)
            {
                s.Levels[id] = n.MaxLevel;
                fixes.Add($"{id}: level {level} of {n.MaxLevel}");
            }
        }
        for (var again = true; again;)
        {
            again = false;
            foreach (var n in e.Tree.Nodes)
            {
                if (!s.Levels.ContainsKey(n.Id) || n.Requires.Count == 0) continue;
                var have = n.Requires.Count(r => s.Levels.GetValueOrDefault(r) > 0);
                if (n.RequireAll ? have == n.Requires.Count : have > 0) continue;
                s.Levels.Remove(n.Id);
                fixes.Add($"{n.Id}: its requirements don't hold");
                again = true;
            }
        }
        if (fixes.Count > 0)
        {
            s.Revision++;
            SkillsInternals.Warn($"{e.Tree.Id}: imported state of {h} corrected: {string.Join("; ", fixes)}");
        }
        CheckBudget(e, h, s);
    }

    /// <summary>A tree that costs more than the character has is reset for free, as the game does at login. The total depends on
    /// the character's level and skill points: while those aren't readable yet, the check waits for the maintenance pass, so a
    /// stale total never resets a good tree.</summary>
    private static void CheckBudget(Entry e, TalentHolder h, TalentTreeState s)
    {
        BudgetPending.Remove((e.Tree.Id, h.Entity.Value));
        if (!h.Entity.IsNone && Progression.Available && Progression.Get(h.Entity) is not { HasLevel: true, HasPoints: true })
        {
            BudgetPending.Add((e.Tree.Id, h.Entity.Value));
            return;
        }
        var pts = PointsOf(e, h, s);
        if (pts.Available >= 0) return;
        foreach (var n in e.Tree.Nodes) s.Levels.Remove(n.Id);
        s.Revision++;
        SkillsInternals.Warn($"{e.Tree.Id}: imported state of {h} costs {pts.Spent} of {pts.Total} points: reset for free, as the game does at login");
    }

    /// <summary>A character's state in a tree (live object; the player need not be online). Tick thread.</summary>
    public static TalentTreeState State(string tree, TalentHolder holder)
    {
        Game.AssertTickThread();
        return StateOf(Need(tree), holder);
    }

    public static TalentTreeState State(string tree, EntityId player) => State(tree, HolderOf(player));

    internal static TalentPointsInfo PointsOf(Entry e, TalentHolder h, TalentTreeState s)
    {
        var p = e.Tree.Points;
        var snap = !h.Entity.IsNone && Progression.Available ? Progression.Get(h.Entity) : null;
        if (snap is { HasLevel: true }) s.LastLevel = snap.Level;
        if (snap is { HasPoints: true }) s.LastGameTotal = snap.SkillPointsTotal;
        var total = p.Source switch
        {
            TalentPoints.Kind.Own => p.Start + p.PerLevel * Math.Max(0, Math.Max(1, s.LastLevel) - 1),
            TalentPoints.Kind.Share => (int)Math.Floor(p.Share * s.LastGameTotal),
            _ => SafeCustom(p, h),
        } + s.Granted;
        var spent = 0;
        foreach (var (id, lv) in s.Levels)
            if (e.Tree.Node(id) is { } n) spent += n.Cost * lv;
        return new TalentPointsInfo(total, spent, total - spent);
    }

    private static int SafeCustom(TalentPoints p, TalentHolder h)
    {
        try
        {
            return p.Total?.Invoke(h) ?? 0;
        }
        catch (Exception ex)
        {
            SkillsInternals.Warn($"points: {ex.Message}");
            return 0;
        }
    }

    public static TalentPointsInfo Points(string tree, TalentHolder holder)
    {
        Game.AssertTickThread();
        var e = Need(tree);
        return PointsOf(e, holder, StateOf(e, holder));
    }

    /// <summary>The level of a node for a character (0 = locked). Tick thread.</summary>
    public static int LevelOf(string tree, string node, TalentHolder holder)
    {
        Game.AssertTickThread();
        return StateOf(Need(tree), holder).Levels.TryGetValue(node, out var l) ? l : 0;
    }

    /// <summary>True when the player has the node (level 1 or more). Tick thread.</summary>
    public static bool Has(EntityId player, string tree, string node) => LevelOf(tree, node, HolderOf(player)) > 0;

    // ------------------------------------------------------------------------------------------- changes ----------

    /// <summary>Takes a node (one more level). Checks: requirements, tier, level, points, then
    /// <see cref="TalentUnlockingEvent"/>. Tick thread.</summary>
    public static TalentResult Unlock(string tree, string node, TalentHolder holder, bool force = false)
    {
        Game.AssertTickThread();
        var e = Need(tree);
        var n = e.Tree.Node(node);
        var s = StateOf(e, holder);
        var pts = PointsOf(e, holder, s);
        if (n is null) return TalentResult.No($"no node \"{node}\" in {tree}", 0, pts);
        var level = s.Levels.TryGetValue(n.Id, out var l) ? l : 0;
        if (level >= n.MaxLevel) return TalentResult.No(n.MaxLevel > 1 ? "already at its last level" : "already taken", level, pts);
        if (!force)
        {
            if (n.Requires.Count > 0)
            {
                var have = n.Requires.Count(r => s.Levels.GetValueOrDefault(r) > 0);
                if (n.RequireAll ? have < n.Requires.Count : have == 0)
                    return TalentResult.No($"needs {(n.RequireAll ? "all of" : "one of")}: {string.Join(", ", n.Requires.Select(r => e.Tree.Node(r)?.Name.ToString() ?? r))}", level, pts);
            }
            if (pts.Spent < n.PointsSpentRequired)
                return TalentResult.No($"needs {n.PointsSpentRequired} points spent in this tree ({pts.Spent} so far)", level, pts);
            if (n.LevelRequired > 0 && s.LastLevel < n.LevelRequired)
                return TalentResult.No($"needs character level {n.LevelRequired}", level, pts);
            if (pts.Available < n.Cost) return TalentResult.No($"needs {n.Cost} point(s), {pts.Available} available", level, pts);
            if (Events.HasSubscribers<TalentUnlockingEvent>())
            {
                var ev = Events.Post(new TalentUnlockingEvent(e.Tree, n, holder, level + 1, n.Cost));
                if (ev.Canceled) return TalentResult.No(ev.Reason, level, pts);
            }
        }
        s.Levels[n.Id] = level + 1;
        s.Revision++;
        Changed(e, holder, TalentChangeKind.Unlock, n, level, level + 1);
        return new TalentResult(true, "", level + 1, PointsOf(e, holder, s));
    }

    public static TalentResult Unlock(EntityId player, string tree, string node) => Unlock(tree, node, HolderOf(player));

    /// <summary>Gives one level back (the points return). Refused when another taken node needs it, or a tier would
    /// fall short. Tick thread.</summary>
    public static TalentResult Lock(string tree, string node, TalentHolder holder, bool force = false)
    {
        Game.AssertTickThread();
        var e = Need(tree);
        var n = e.Tree.Node(node);
        var s = StateOf(e, holder);
        var pts = PointsOf(e, holder, s);
        if (n is null) return TalentResult.No($"no node \"{node}\" in {tree}", 0, pts);
        var level = s.Levels.GetValueOrDefault(n.Id);
        if (level == 0) return TalentResult.No("not taken", 0, pts);
        if (!force)
        {
            var after = new Dictionary<string, int>(s.Levels, StringComparer.Ordinal);
            if (level == 1) after.Remove(n.Id);
            else after[n.Id] = level - 1;
            foreach (var other in e.Tree.Nodes)
            {
                if (!after.TryGetValue(other.Id, out var ol) || ol == 0 || other.Requires.Count == 0) continue;
                var have = other.Requires.Count(r => after.GetValueOrDefault(r) > 0);
                if (other.RequireAll ? have < other.Requires.Count : have == 0)
                    return TalentResult.No($"{other.Name} needs it (give that back first, or respec)", level, pts);
                var spent = after.Sum(kv => (e.Tree.Node(kv.Key)?.Cost ?? 0) * kv.Value) - other.Cost * ol;
                if (spent < other.PointsSpentRequired)
                    return TalentResult.No($"{other.Name} needs {other.PointsSpentRequired} points spent first", level, pts);
            }
        }
        if (level == 1) s.Levels.Remove(n.Id);
        else s.Levels[n.Id] = level - 1;
        s.Revision++;
        Changed(e, holder, TalentChangeKind.Lock, n, level, level - 1);
        return new TalentResult(true, "", level - 1, PointsOf(e, holder, s));
    }

    /// <summary>Gives every point of the tree back (free). Tick thread.</summary>
    public static TalentResult Respec(string tree, TalentHolder holder, bool force = false)
    {
        Game.AssertTickThread();
        var e = Need(tree);
        var s = StateOf(e, holder);
        if (!force && !e.Tree.AllowRespec) return TalentResult.No("this tree has no respec", 0, PointsOf(e, holder, s));
        var old = s.Levels.ToArray();
        s.Levels.Clear();
        s.Revision++;
        foreach (var (id, lv) in old)
            if (e.Tree.Node(id) is { } n) ApplyLevel(e, holder, n, 0);
        Notify(e, new TalentChange(e.Tree, holder, TalentChangeKind.Respec, null, old.Sum(x => x.Value), 0, PointsOf(e, holder, s).Available));
        return new TalentResult(true, "", 0, PointsOf(e, holder, s));
    }

    /// <summary>Gives points of the tree's own (any points source; negative takes back). Tick thread.</summary>
    public static TalentPointsInfo Grant(string tree, TalentHolder holder, int points)
    {
        Game.AssertTickThread();
        var e = Need(tree);
        var s = StateOf(e, holder);
        s.Granted += points;
        s.Revision++;
        var p = PointsOf(e, holder, s);
        Notify(e, new TalentChange(e.Tree, holder, TalentChangeKind.Grant, null, 0, 0, p.Available));
        return p;
    }

    public static TalentPointsInfo Grant(string tree, EntityId player, int points) => Grant(tree, HolderOf(player), points);

    private static void Changed(Entry e, TalentHolder h, TalentChangeKind kind, TalentNode n, int oldLevel, int newLevel)
    {
        ApplyLevel(e, h, n, newLevel);
        var s = StateOf(e, h);
        Notify(e, new TalentChange(e.Tree, h, kind, n.Id, oldLevel, newLevel, PointsOf(e, h, s).Available));
    }

    private static void Notify(Entry e, TalentChange c)
    {
        SkillsInternals.Info($"{c.Tree.Id}: {c.Holder} {c.Kind} {c.Node} {c.OldLevel} -> {c.NewLevel} ({c.Points} point(s) left)");
        if (e.Tree.OnChanged is { } f)
        {
            try
            {
                SkillsInternals.RunAs(e.Owner, () => f(c));
            }
            catch (Exception ex)
            {
                SkillsInternals.Warn($"{c.Tree.Id} OnChanged: {ex.Message}");
            }
        }
        if (Events.HasSubscribers<TalentChangedEvent>()) Events.Post(new TalentChangedEvent(c));
        TalentNet.Push(e, c.Holder);
    }

    // ------------------------------------------------------------------------------------------- effects ----------

    /// <summary>Applies the node's effects for <paramref name="level"/> (0 = removes them). Tick thread.</summary>
    internal static void ApplyLevel(Entry e, TalentHolder h, TalentNode n, int level)
    {
        if (h.Entity.IsNone) return;  // offline: applied at the next join
        var key = (h.Entity.Value, e.Tree.Id, n.Id);
        if (Active.TryGetValue(key, out var cur))
        {
            if (cur.Level == level) return;
            RemoveNode(e, key);
        }
        if (level <= 0 || n.Effects.Count == 0) return;
        var a = new ActiveNode { Level = level };
        foreach (var eff in n.Effects)
        {
            var ae = new ActiveEffect { Effect = eff };
            try
            {
                Start(e, h, n, level, ae);
            }
            catch (Exception ex)
            {
                SkillsInternals.Warn($"{e.Tree.Id}/{n.Id}: {eff.Describe()}: {ex.Message}");
            }
            a.Effects.Add(ae);
        }
        Active[key] = a;
    }

    private static void Start(Entry e, TalentHolder h, TalentNode n, int level, ActiveEffect ae)
    {
        var pos = World.Position(h.Entity);
        switch (ae.Effect)
        {
            case BuffEffect b:
            {
                var g = b.Guid ?? ResolveBuff(b.Name!);
                if (g is null) throw new InvalidOperationException($"unknown buff \"{b.Name}\"");
                b.Guid ??= g;
                _ = Abilities.ApplyBuff(h.Entity, g.Value, TimeSpan.FromSeconds(b.Refresh + 5), h.Entity);
                ae.NextRefresh = Environment.TickCount64 + (long)(b.Refresh * 1000);
                break;
            }
            case AttributeEffect at when pos is { } p:
                ae.Impact = Abilities.RunProgram(TalentAttributes.Program, p, new ImpactOptions
                {
                    Root = h.Entity,
                    Configs = new ImpactConfigs
                    {
                        [$"0x{TalentAttributes.ConfigAttribute:x8}"] = at.AttributeGuid,
                        [$"0x{TalentAttributes.ConfigAmount:x8}"] = at.Amount * level,
                    },
                    Tag = "skills",
                }).GetAwaiter().GetResult();
                break;
            case GameNodeEffect gn when pos is { } p:
            {
                var program = GameProgramOf(gn.Node) ?? throw new InvalidOperationException($"game node 0x{gn.Node:x8} has no program");
                ae.Impact = Abilities.RunProgram(program, p, new ImpactOptions
                {
                    Root = h.Entity,
                    Components = [new SpawnComponent(SpawnComponent.HashOf("SkillIdComponent"), BitConverter.GetBytes(gn.Node))],
                    Tag = "skills",
                }).GetAwaiter().GetResult();
                break;
            }
            case ProgramEffect pe when pos is { } p:
            {
                var cfg = new ImpactConfigs();
                if (pe.Configs is not null) foreach (var (k, v) in pe.Configs) cfg[k] = v;
                if (pe.LevelConfig is { } levelConfig) cfg[levelConfig] = (float)level;
                ae.Impact = Abilities.RunProgram(pe.ProgramGuid, p, new ImpactOptions { Root = h.Entity, Configs = cfg, Tag = "skills" })
                    .GetAwaiter().GetResult();
                break;
            }
            case CallbackEffect cb:
                ae.Context = new TalentContext(e.Tree, n, h, level);
                SkillsInternals.RunAs(e.Owner, () => cb.ApplyFn(ae.Context));
                break;
            case AbilityEffect:
                break;  // checked when the ability is cast
            default:
                throw new InvalidOperationException("the player has no position");
        }
    }

    private static void RemoveNode(Entry e, (uint Entity, string Tree, string Node) key)
    {
        if (!Active.Remove(key, out var a)) return;
        foreach (var ae in a.Effects)
        {
            try
            {
                if (!ae.Impact.IsNone) Spawn.Destroy(ae.Impact);
                if (ae.Effect is CallbackEffect { RemoveFn: { } rm } && ae.Context is { } c) SkillsInternals.RunAs(e.Owner, () => rm(c));
            }
            catch (Exception ex)
            {
                SkillsInternals.Warn($"{key.Tree}/{key.Node}: removing {ae.Effect.Describe()}: {ex.Message}");
            }
        }
    }

    /// <summary>Every ~2 s per tree: applies the states of players in game (joins, restarts), renews buffs, restarts
    /// effect entities that are gone (streamed out with their area).</summary>
    internal static void Maintain(Entry e)
    {
        if (!Players.Available || !TalentNet.ServerSide) return;
        var now = Environment.TickCount64;
        // players that left: their effect entities go (DespawnLink takes most with the player anyway)
        var present = Players.All.Where(p => p.InGame).Select(p => p.Entity.Value).ToHashSet();
        foreach (var key in Active.Keys.Where(k => k.Tree == e.Tree.Id && !present.Contains(k.Entity) && !Ephemeral.ContainsKey((k.Tree, k.Entity))).ToArray())
        {
            RemoveNode(e, key);
            SteamOf.Remove(key.Entity);
        }
        BudgetPending.RemoveWhere(k => k.Tree == e.Tree.Id && !present.Contains(k.Entity));
        foreach (var p in Players.All)
        {
            if (!p.InGame || p.Entity.IsNone) continue;
            var h = HolderOf(p.Entity);
            TalentTreeState s;
            try
            {
                s = StateOf(e, h);
            }
            catch (Exception ex)
            {
                SkillsInternals.Warn($"{e.Tree.Id}: state of {h}: {ex.Message}");
                continue;
            }
            if (BudgetPending.Contains((e.Tree.Id, p.Entity.Value))) CheckBudget(e, h, s);
            foreach (var n in e.Tree.Nodes)
            {
                var lv = s.Levels.GetValueOrDefault(n.Id);
                var key = (p.Entity.Value, e.Tree.Id, n.Id);
                if (!Active.TryGetValue(key, out var a) || a.Level != lv)
                {
                    if (lv > 0 || a is not null) ApplyLevel(e, h, n, lv);
                    continue;
                }
                foreach (var ae in a.Effects)
                {
                    if (ae.Effect is BuffEffect b && now >= ae.NextRefresh && b.Guid is { } g)
                    {
                        _ = Abilities.ApplyBuff(p.Entity, g, TimeSpan.FromSeconds(b.Refresh + 5), p.Entity);
                        ae.NextRefresh = now + (long)(b.Refresh * 1000);
                    }
                    else if (!ae.Impact.IsNone && !World.Exists(ae.Impact))
                    {
                        try
                        {
                            ae.Impact = default;
                            Start(e, h, n, lv, ae);
                        }
                        catch (Exception ex)
                        {
                            SkillsInternals.Warn($"{e.Tree.Id}/{n.Id}: restarting {ae.Effect.Describe()}: {ex.Message}");
                        }
                    }
                }
            }
        }
    }

    /// <summary>The effects running for a player now (diagnostics): tree/node -> level and effect entities.</summary>
    public static IReadOnlyList<(string Tree, string Node, int Level, IReadOnlyList<EntityId> Entities)> ActiveEffects(EntityId player)
    {
        Game.AssertTickThread();
        return Active.Where(kv => kv.Key.Entity == player.Value)
            .Select(kv => (kv.Key.Tree, kv.Key.Node, kv.Value.Level,
                (IReadOnlyList<EntityId>)kv.Value.Effects.Where(x => !x.Impact.IsNone).Select(x => x.Impact).ToArray()))
            .ToArray();
    }

    /// <summary>Applies the player's state now (instead of at the next maintenance pass). Tick thread.</summary>
    public static void Refresh(string tree, TalentHolder holder)
    {
        Game.AssertTickThread();
        var e = Need(tree);
        var s = StateOf(e, holder);
        foreach (var n in e.Tree.Nodes) ApplyLevel(e, holder, n, s.Levels.GetValueOrDefault(n.Id));
    }

    private static Guid? ResolveBuff(string name)
    {
        try
        {
            return Content.Buffs.Get(name)?.Guid;
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<uint, Guid>? _programs;

    private static Guid? GameProgramOf(uint node)
    {
        if (_programs is null)
        {
            _programs = new Dictionary<uint, Guid>();
            if (JsonNode.Parse(GameTree.Dump())?["nodes"] is JsonArray a)
                foreach (var x in a)
                    if (Guid.TryParse(x?["program"]?.GetValue<string>(), out var g) && g != Guid.Empty)
                        _programs[GameTree.ParseHex(x?["id"]?.GetValue<string>())] = g;
        }
        return _programs.TryGetValue(node, out var p) ? p : null;
    }

    private static void OnAbilityCast(AbilityCastEvent ev)
    {
        List<(Entry E, TalentNode N)> gates = new();
        lock (Gate)
            foreach (var e in Registry.Values)
                foreach (var n in e.Tree.Nodes)
                    if (n.Effects.OfType<AbilityEffect>().Any(a => a.AbilityId == ev.Cast.Ability.Id)) gates.Add((e, n));
        if (gates.Count == 0) return;
        var h = HolderOf(ev.Cast.Caster);
        if (gates.Any(g => StateOf(g.E, h).Levels.GetValueOrDefault(g.N.Id) > 0)) return;
        ev.Reason = $"needs the talent {gates[0].N.Name} ({gates[0].E.Tree.Name})";
        ev.Cancel();
    }
}
