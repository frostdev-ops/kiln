// SPDX-License-Identifier: MIT
// Kiln.Skills, part 3: the managed side of the shadow table (nodes of the game's own tree past its caps; native part
// native/services/skills/shadow.cpp). Kiln.Skills keeps each character's shadow levels in Kiln.Saves carried character
// data ("gameShadow": they go with the character, like the game's own nodes in the character save), loads them into the
// native table when the player is in game, saves what the game's unlock path changed, runs the shadow nodes' effects (the
// game's handle_skill_impacts only walks UnlockedSkillNodes) and sends the levels to the player's client (its skill-tree
// screen and its own queries read them there).
//
// The client's copy is kept level-triggered, not by events: a player is in game before their client finishes Kiln's
// handshake, so what the server sends at load goes nowhere (seen: the table loaded at 00:39:43.644, the peer was
// ready at .725, and the reload after the carried import changed nothing, so nothing was sent again: the screen showed
// Locked with 5 nodes on the server, and the server refused Space because the node was already taken). So each step: a
// player whose current peer has not got the table's current state gets it, whenever that becomes possible.
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>A character's levels of shadow nodes (carried character data "gameShadow" of Kiln.Skills' data owner).</summary>
public sealed class GameShadowState
{
    /// <summary>"0x&lt;node id&gt;" -> level.</summary>
    public Dictionary<string, int> Levels { get; set; } = new(StringComparer.Ordinal);
    public long Revision { get; set; }
}

/// <summary>Server to client: the local player's shadow levels.</summary>
public sealed record GameShadowMessage(Dictionary<string, int> Levels, long Revision);

internal static unsafe class GameShadow
{
    private static ulong _seq;
    private static readonly Dictionary<uint, TalentHolder> Loaded = new();   // entity -> whose levels the table holds
    private static readonly Dictionary<uint, uint> SentTo = new();           // entity -> the peer that has the table's state
    private static readonly HashSet<uint> BudgetPending = new();             // entities whose shadow costs aren't checked yet
    private static readonly Dictionary<uint, Dictionary<uint, int>> FromSave = new();  // entity -> what the join check moved out of its save
    private static readonly Dictionary<uint, ToNativeMove> ToNative = new();  // entity -> levels on their way into its native list
    internal const string DataKey = "gameShadow";
    private static readonly Dictionary<(uint Entity, uint Node), (int Level, EntityId Impact)> Effects = new();
    private static HashSet<uint> _nodes = new();
    private static Dictionary<uint, (Guid Program, int MaxLevel)>? _programs;
    private static ulong _ticks;
    private static int _lastNodeCount = -1;
    private static bool _kilnNodes;

    public static bool On => SkillsNative.T is var t && t != null && t->ShadowOn() != 0;

    private static EntityId _labPlayer;
    private static TalentHolder? _labHolder;
    private static bool _labForceNative;

    /// <summary>Lab: this entity (a player template) is treated as a player in game by the client sync. With a holder its table
    /// also loads and saves through that holder's data, as a real player's.</summary>
    internal static void LabPlayer(EntityId e, TalentHolder? holder = null, bool forceNative = false)
    {
        _labPlayer = e;
        _labHolder = holder;
        _labForceNative = forceNative;
    }

    public static IReadOnlySet<uint> Nodes
    {
        get
        {
            var t = SkillsNative.T;
            if (t == null) return _nodes;
            uint n = 0;
            t->ShadowNodes(null, 0, &n);
            if ((int)n != _lastNodeCount)
            {
                var ids = new uint[n];
                fixed (uint* p = ids) t->ShadowNodes(p, n, &n);
                _nodes = new HashSet<uint>(ids);
                _lastNodeCount = (int)n;
                _programs = null;
            }
            return _nodes;
        }
    }

    public static Dictionary<uint, int> Get(uint world, EntityId e)
    {
        var t = SkillsNative.T;
        var r = new Dictionary<uint, int>();
        if (t == null) return r;
        uint n = 0;
        t->ShadowGet(world, e.Value, null, 0, &n);
        var buf = new SkillsNative.ShadowEntry[Math.Max(1, (int)n)];
        fixed (SkillsNative.ShadowEntry* p = buf) t->ShadowGet(world, e.Value, p, (uint)buf.Length, &n);
        for (var i = 0; i < Math.Min(n, (uint)buf.Length); ++i) r[buf[i].Node] = buf[i].Level;
        return r;
    }

    public static void Set(uint world, EntityId e, IReadOnlyDictionary<uint, int> levels)
    {
        var t = SkillsNative.T;
        if (t == null) return;
        var buf = levels.Where(kv => kv.Value > 0).Select(kv => new SkillsNative.ShadowEntry { Node = kv.Key, Level = (byte)Math.Min(255, kv.Value) }).ToArray();
        fixed (SkillsNative.ShadowEntry* p = buf) t->ShadowSet(world, e.Value, p, (uint)buf.Length);
    }

    private static bool HasKey(TalentHolder h) => h.SteamId != 0 || !h.Character.IsNone;

    private static GameShadowState StateOf(TalentHolder h) => Skills.DataOf<GameShadowState>(h, DataKey);

    /// <summary>The player's data changed under the table (a client's copy came in): load it again.</summary>
    internal static void Reload(EntityId e) => Loaded.Remove(e.Value);

    /// <summary>Each service tick (cheap when nothing changed): the server part, or a joined client's.</summary>
    public static void Tick()
    {
        ++_ticks;
        var t = SkillsNative.T;
        if (t == null || t->ShadowOn() == 0) return;
        if (!TalentNet.ServerSide)
        {
            // this process stopped hosting (or never did): nothing here may keep a hosted world's players
            if (Loaded.Count > 0 || Effects.Count > 0) ForgetServerSide();
            return;
        }
        if (!Players.Available) return;
        // A tree with only native Kiln nodes has work too: a character's carried entries may be native nodes here (a mod list that
        // lost a big mod). Only a tree without Kiln nodes has none.
        if (_ticks % 50 == 1) _kilnNodes = GameTree.Status is { AddedNodes: > 0 };
        if (Nodes.Count == 0 && Loaded.Count == 0 && Effects.Count == 0 && !_kilnNodes) return;
        // What changed since the last tick goes first: the join check may have moved nodes out of the save, and the load below must
        // not overwrite them (seen: adding a mod made Ember Ward a shadow node. The join check moved it, and the character's
        // carried copy, taken a moment later, knew nothing of it: 3 points back, and not a line in the log).
        var buf = new SkillsNative.ShadowChange[256];
        uint n;
        fixed (SkillsNative.ShadowChange* b = buf) t->ShadowChanges(_seq, b, (uint)buf.Length, &n);
        var touched = new HashSet<uint>();
        for (var i = 0; i < Math.Min(n, (uint)buf.Length); ++i)
        {
            ref var c = ref buf[i];
            _seq = Math.Max(_seq, c.Seq);
            if (c.World != 0) continue;
            touched.Add(c.Entity);
            if (c.Reason != 4) SkillsInternals.Info($"shadow node 0x{c.Node:x8} of entity {c.Entity}: level {c.OldLevel} -> {c.NewLevel} ({ReasonName(c.Reason)})");
            NoteFromSave(c.Entity, c.Node, c.NewLevel, c.Reason);
        }
        // players in game: load their table (their saved levels, cut to the nodes this tree has)
        var inGame = new HashSet<uint>();
        if (!_labPlayer.IsNone)
        {
            inGame.Add(_labPlayer.Value);
            if (!Loaded.TryGetValue(_labPlayer.Value, out var ls) || (_labHolder is { } lh && ls != lh))
            {
                var holder = _labHolder ?? Skills.HolderOf(_labPlayer);
                Loaded[_labPlayer.Value] = holder;
                if (_labHolder is not null) LoadTable(_labPlayer, holder, $"lab player {_labPlayer}");
            }
        }
        foreach (var p in Players.All)
        {
            if (!p.InGame || p.Entity.IsNone) continue;
            inGame.Add(p.Entity.Value);
            var holder = Skills.HolderOf(p.Entity);
            if (Loaded.TryGetValue(p.Entity.Value, out var s) && s == holder) continue;
            Loaded[p.Entity.Value] = holder;
            if (!HasKey(holder))
            {
                SkillsInternals.Warn($"shadow table: {p} has no character, Steam or account id: their shadow nodes are kept for this session only");
                continue;
            }
            LoadTable(p.Entity, holder, p.ToString() ?? $"entity {p.Entity}");
        }
        foreach (var gone in Loaded.Keys.Where(e => !inGame.Contains(e)).ToArray())
        {
            Loaded.Remove(gone);
            SentTo.Remove(gone);
            BudgetPending.Remove(gone);
            FromSave.Remove(gone);
            ToNative.Remove(gone);
            LastRequests.Remove(gone);
            Set(0, new EntityId(gone), new Dictionary<uint, int>());
            foreach (var k in Effects.Keys.Where(k => k.Entity == gone).ToArray()) StopEffect(k);
        }
        CheckBudgets();
        PumpToNative();
        // what the game's unlock path (or a load) changed: save, effects, the player's client
        foreach (var e in touched)
        {
            var levels = Get(0, new EntityId(e));
            if (Loaded.TryGetValue(e, out var holder) && HasKey(holder))
            {
                var st = StateOf(holder);
                // The save follows the table, plus the levels of nodes this tree does not have: the character took them
                // on another world and takes them back there.
                var next = levels.ToDictionary(kv => $"0x{kv.Key:x8}", kv => kv.Value, StringComparer.Ordinal);
                foreach (var (k, v) in st.Levels)
                    if (!Nodes.Contains(GameTree.ParseHex(k))) next[k] = v;
                st.Levels = next;
                st.Revision++;
            }
            ApplyEffects(new EntityId(e), levels);
            SentTo.Remove(e);
        }
        // the player's client gets the table's state once its peer can take it, and after every change
        foreach (var e in inGame)
        {
            if (!Loaded.ContainsKey(e)) continue;
            var entity = new EntityId(e);
            if (Network.PeerOf(entity) is not { State: NetPeerState.Ready } peer || !peer.HasChannel(TalentNet.ChannelName)) continue;
            if (SentTo.TryGetValue(e, out var sent) && sent == peer.Id) continue;
            var levels = Get(0, entity);
            if (!Send(entity, levels)) continue;
            SentTo[e] = peer.Id;
            SkillsInternals.Info($"shadow table: sent {levels.Count} shadow node(s) to the client of entity {e} (peer 0x{peer.Id:x})");
        }
        // effect entities that are gone (streamed out): restart
        if (_ticks % 50 == 0)
            foreach (var (k, v) in Effects.ToArray())
                if (!v.Impact.IsNone && !World.Exists(v.Impact))
                {
                    Effects.Remove(k);
                    StartEffect(new EntityId(k.Entity), k.Node, v.Level);
                }
    }

    // The character's levels into the table. A node of this tree that is a native node here goes to the game's own list instead, and
    // what the join check moved out of the save stays: the save no longer has it, so this is its only record.
    private static void LoadTable(EntityId e, TalentHolder holder, string who)
    {
        var st = StateOf(holder);
        var levels = new Dictionary<uint, int>();
        var want = new Dictionary<uint, int>();
        var dropped = 0;
        var native = NativeNodes();
        foreach (var (k, v) in st.Levels)
        {
            var id = GameTree.ParseHex(k);
            if (Nodes.Contains(id)) levels[id] = v;
            else if (native.Contains(id)) want[id] = v;
            else dropped++;
        }
        if (FromSave.TryGetValue(e.Value, out var moved))
            foreach (var (id, level) in moved)
            {
                if (levels.GetValueOrDefault(id) >= level) continue;
                levels[id] = level;
                st.Levels[$"0x{id:x8}"] = level;
                st.Revision++;
            }
        Set(0, e, levels);
        BudgetPending.Add(e.Value);
        if (want.Count > 0) ToNative[e.Value] = new ToNativeMove(holder, want);
        SkillsInternals.Info($"shadow table: {who} has {levels.Count} shadow node(s)" +
                             (moved is { Count: > 0 } ? $", {moved.Count} of them moved from the character's save at this join" : "") +
                             (want.Count > 0 ? $", {want.Count} that are native nodes in this world's tree (their levels go to the game's own list)" : "") +
                             (dropped > 0 ? $", {dropped} of a mod this world doesn't have (not applied, their points are free here; the entries stay with the character)" : ""));
    }

    private static void NoteFromSave(uint entity, uint node, int level, byte reason)
    {
        if (reason == 3)
        {
            if (!FromSave.TryGetValue(entity, out var m)) FromSave[entity] = m = new Dictionary<uint, int>();
            m[node] = Math.Max(m.GetValueOrDefault(node), level);
        }
        else if (reason is 2 or 5)
        {
            FromSave.Remove(entity);  // a respec, or the game's reset: they went with the tree
        }
        else if (reason == 1 && FromSave.TryGetValue(entity, out var m))
        {
            // the player's own unlock or refund: the table is right, and a later load must not bring an old level back
            if (level > 0) m[node] = level;
            else m.Remove(node);
        }
    }

    private static HashSet<uint> NativeNodes()
    {
        var r = new HashSet<uint>();
        foreach (var d in GameTree.Definitions)
            if (d.State == "in-tree" && !Nodes.Contains(d.Id)) r.Add(d.Id);
        return r;
    }

    // ---- shadow to native: a node that is a native node in this tree, because the mod list changed ----
    // Back through the game's own rules, one level at a time, once the game has set the tree up: links and points as if the player
    // took it again (their points came back when the table stopped counting it). Anchors first: a node waits while a node it hangs on
    // (its definition's "from", or another node's "to" into it) is still on its way, whatever order the carried data lists them in.
    // A refusal waits until a pass with no progress (a link to a game node the move itself unlocks). What is still refused then can't
    // be kept here, and says why, with the points or the anchor that decided it.
    private sealed class ToNativeMove(TalentHolder holder, Dictionary<uint, int> want)
    {
        public readonly TalentHolder Holder = holder;
        public readonly Dictionary<uint, int> Want = want;
        public readonly Dictionary<uint, string> Refused = new();
        public readonly List<uint> Requests = new();  // in the order they were asked (lab: GameTree.LabNativeRequests)
        public Task<ProgressionResult>? Busy;
        public uint BusyNode;
        public bool Progress;
    }

    private static readonly Dictionary<uint, List<uint>> LastRequests = new();  // entity -> its last move's requests (lab)

    internal static IReadOnlyList<uint> RequestsOf(EntityId e) =>
        ToNative.TryGetValue(e.Value, out var m) ? m.Requests.ToArray() : LastRequests.TryGetValue(e.Value, out var l) ? l.ToArray() : [];

    // Which in-tree Kiln node hangs on which (by the definitions' own links; the game unlocks along a link from either end, so a node's
    // anchors are the nodes it links from and the nodes that link to it). Built once per definitions change.
    private static Dictionary<uint, HashSet<uint>>? _anchors;
    private static int _anchorsKey = -1;

    private static Dictionary<uint, HashSet<uint>> Anchors()
    {
        var t = SkillsNative.T;
        if (t == null) return new();
        var text = SkillsNative.Text((b, c, l) => t->Definitions((byte*)b, c, (nuint*)l), out _);
        var key = text.GetHashCode();
        if (_anchors is not null && key == _anchorsKey) return _anchors;
        var map = new Dictionary<uint, HashSet<uint>>();
        if (JsonNode.Parse(text) is not JsonArray a) return _anchors = map;
        var byName = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var n in a)
            if (n?["name"]?.GetValue<string>() is { Length: > 0 } name) byName[name] = GameTree.ParseHex(n["id"]?.GetValue<string>());
        uint Resolve(string r) =>
            r.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? GameTree.ParseHex(r)
            : Guid.TryParse(r, out var g) ? GameNodeDefinition.Fnv(g.ToByteArray())
            : byName.GetValueOrDefault(r);
        HashSet<uint> Of(uint id) => map.TryGetValue(id, out var s) ? s : map[id] = new HashSet<uint>();
        foreach (var n in a)
        {
            if (n?["state"]?.GetValue<string>() != "in-tree") continue;
            var id = GameTree.ParseHex(n["id"]?.GetValue<string>());
            if (n["from"] is JsonArray from)
                foreach (var f in from)
                    if (f?.GetValue<string>() is { } r && Resolve(r) is var src and not 0 && src != id) Of(id).Add(src);
            if (n["to"] is JsonArray to)
                foreach (var x in to)
                    if (x?.GetValue<string>() is { } r && Resolve(r) is var dst and not 0 && dst != id) Of(dst).Add(id);
        }
        _anchorsKey = key;
        return _anchors = map;
    }

    /// <summary>The next node to ask for: one whose anchors in this move are all done (in the list or refused).</summary>
    private static uint NextOf(ToNativeMove m, Dictionary<uint, HashSet<uint>> anchors)
    {
        uint fallback = 0;
        foreach (var id in m.Want.Keys)
        {
            if (m.Refused.ContainsKey(id)) continue;
            if (fallback == 0) fallback = id;
            if (!anchors.TryGetValue(id, out var on) || !on.Any(x => x != id && m.Want.ContainsKey(x) && !m.Refused.ContainsKey(x))) return id;
        }
        return fallback;  // anchors that hang on each other: the game's rules decide, pass by pass
    }

    private static void PumpToNative()
    {
        if (ToNative.Count == 0) return;
        if (!Progression.Available)
        {
            foreach (var (e, m) in ToNative) GiveUpToNative(new EntityId(e), m, "kiln.progression is not available");
            ToNative.Clear();
            return;
        }
        foreach (var (key, m) in ToNative.ToArray())
        {
            var e = new EntityId(key);
            if (m.Busy is { } busy)
            {
                if (!busy.IsCompleted) continue;
                m.Busy = null;
                var ok = busy is { IsCompletedSuccessfully: true, Result.Ok: true };
                var reason = busy.IsCompletedSuccessfully ? busy.Result.Reason : busy.Exception?.GetBaseException().Message ?? "failed";
                if (ok) m.Progress = true;
                else if (reason.Contains("has not set up", StringComparison.Ordinal)) continue;  // the join check hasn't run: again later
                else m.Refused[m.BusyNode] = reason;
            }
            if (World.ReadBytes(e, "UnlockedSkillNodes") is not { Length: > 0 } u || u[0] == 0) continue;  // not set up yet
            var have = Progression.UnlockedNodes(e).ToDictionary(x => x.Id, x => x);
            foreach (var (id, level) in m.Want.ToArray())
            {
                if (!have.TryGetValue(id, out var cur) || (cur.MaxLevel > 0 && cur.Level < level)) continue;
                m.Want.Remove(id);
                m.Refused.Remove(id);
                DropEntry(m.Holder, id);
                SkillsInternals.Info($"shadow table: entity {e}: node 0x{id:x8} is a native node in this tree: level {level} is in the character's list now");
            }
            var next = m.Want.Count > 0 ? NextOf(m, Anchors()) : 0;
            if (next != 0)
            {
                m.BusyNode = next;
                m.Requests.Add(next);
                m.Busy = Progression.Unlock(e, next, force: _labForceNative && e == _labPlayer);  // the lab's entity has no points to read
                continue;
            }
            if (m.Want.Count > 0 && m.Progress)
            {
                m.Progress = false;
                m.Refused.Clear();
                continue;
            }
            GiveUpToNative(e, m, null);
            LastRequests[key] = m.Requests;
            ToNative.Remove(key);
        }
    }

    private static void GiveUpToNative(EntityId e, ToNativeMove m, string? why)
    {
        Dictionary<uint, UnlockedSkillNode>? have = null;
        ProgressionSnapshot? points = null;
        if (why is null && m.Want.Count > 0 && Progression.Available)
        {
            have = Progression.UnlockedNodes(e).ToDictionary(x => x.Id, x => x);
            points = Progression.Get(e);
        }
        var anchors = m.Want.Count > 0 && why is null ? Anchors() : new();
        foreach (var (id, level) in m.Want)
        {
            var reason = why ?? m.Refused.GetValueOrDefault(id, "?");
            // say what decided it: the anchor that couldn't come back, or where the character's points are
            if (why is null && anchors.TryGetValue(id, out var on) && on.FirstOrDefault(x => m.Want.ContainsKey(x)) is var anchor and not 0 &&
                reason.StartsWith("not linked", StringComparison.Ordinal))
                reason = $"it hangs on 0x{anchor:x8}, which can't be taken back either";
            else if (points is { HasPoints: true } p && reason.Contains("skill point", StringComparison.Ordinal))
                reason += $"; the character's tree here spends {p.SkillPointsSpent} of its {p.SkillPointsTotal} points on other nodes " +
                          "(its save is older than its carried data, or the points went elsewhere while this node wasn't in the tree)";
            var partial = have is not null && have.TryGetValue(id, out var cur) && cur.Level > 0 ? $" (level {cur.Level} of it came back)" : "";
            SkillsInternals.Warn($"shadow table: entity {e}: node 0x{id:x8} (level {level}) is a native node in this tree, and it can't be taken back{partial}: " +
                                 $"{reason}. Its unlock is lost here and its points are free again");
            DropEntry(m.Holder, id);
        }
        m.Want.Clear();
    }

    private static void DropEntry(TalentHolder holder, uint id)
    {
        var st = StateOf(holder);
        if (st.Levels.Remove($"0x{id:x8}")) st.Revision++;
    }

    private static string ReasonName(byte r) => r switch
    {
        1 => "the game's skill tree",
        2 => "respec",
        3 => "moved from the save",
        4 => "loaded",
        5 => "the game's join check reset the tree",
        _ => "?",
    };

    /// <summary>Shadow costs are Kiln's to check: the game's join check judges the native tree alone (hk_login), so a
    /// character whose shadow nodes no longer fit (an older character save with newer carried data, an edited file) keeps its
    /// native tree and loses only the shadow nodes, free, as the game does with its own. Waits until the game has set the tree
    /// up: before that the total isn't real (the join check itself waits for the skill-point knowledge).</summary>
    private static void CheckBudgets()
    {
        if (BudgetPending.Count == 0 || !Progression.Available) return;
        foreach (var e in BudgetPending.ToArray())
        {
            var entity = new EntityId(e);
            if (World.ReadBytes(entity, "UnlockedSkillNodes") is not { Length: > 0 } u || u[0] == 0) continue;
            if (Progression.Get(entity) is not { HasLevel: true, HasSkills: true, HasPoints: true } p) continue;
            BudgetPending.Remove(e);
            if (p.SkillPointsSpent <= p.SkillPointsTotal) continue;
            var levels = Get(0, entity);
            if (levels.Count == 0) continue;  // a native tree over its budget is the game's to handle
            SkillsInternals.Warn($"shadow table: entity {e}: the tree costs {p.SkillPointsSpent} of {p.SkillPointsTotal} points with its {levels.Count} shadow node(s): " +
                                 "they are reset for free, the character's native tree stays");
            Set(0, entity, new Dictionary<uint, int>());
        }
    }

    private static void ForgetServerSide()
    {
        foreach (var e in Loaded.Keys.ToArray()) Set(0, new EntityId(e), new Dictionary<uint, int>());
        Loaded.Clear();
        SentTo.Clear();
        BudgetPending.Clear();
        FromSave.Clear();
        ToNative.Clear();
        LastRequests.Clear();
        Effects.Clear();  // their entities went with the world
    }

    private static void ApplyEffects(EntityId e, Dictionary<uint, int> levels)
    {
        foreach (var k in Effects.Keys.Where(k => k.Entity == e.Value).ToArray())
            if (!levels.TryGetValue(k.Node, out var l) || l != Effects[k].Level) StopEffect(k);
        foreach (var (node, level) in levels)
            if (!Effects.ContainsKey((e.Value, node))) StartEffect(e, node, level);
    }

    private static void StartEffect(EntityId e, uint node, int level)
    {
        try
        {
            _programs ??= Programs();
            if (!_programs.TryGetValue(node, out var pm)) return;  // a node without a program: nothing to run
            var program = pm.Program;
            var pos = World.Position(e);
            if (pos is null) return;
            var comps = new List<SpawnComponent>
            {
                new(SpawnComponent.HashOf("SkillIdComponent"), BitConverter.GetBytes(node)),
                new(SpawnComponent.HashOf("DespawnLink"), [.. BitConverter.GetBytes(e.Value), 0, 0, 0, 0]),
            };
            // nodes with levels: the game's skill impact carries a Level {.., storage = {level, 99}}
            if (pm.MaxLevel > 0)
            {
                var b = new byte[16];
                BitConverter.GetBytes((uint)level).CopyTo(b, 8);
                BitConverter.GetBytes(99u).CopyTo(b, 12);
                comps.Add(new SpawnComponent(SpawnComponent.HashOf("Level"), b));
            }
            var impact = Abilities.RunProgram(program, pos.Value, new ImpactOptions { Root = e, Components = comps, Tag = "skills-shadow" })
                .GetAwaiter().GetResult();
            Effects[(e.Value, node)] = (level, impact);
        }
        catch (Exception ex)
        {
            SkillsInternals.Warn($"shadow node 0x{node:x8}: effect for entity {e}: {ex.Message}");
        }
    }

    private static void StopEffect((uint Entity, uint Node) k)
    {
        if (!Effects.Remove(k, out var v)) return;
        try
        {
            if (!v.Impact.IsNone) Spawn.Destroy(v.Impact);
        }
        catch (Exception ex)
        {
            SkillsInternals.Warn($"shadow node 0x{k.Node:x8}: {ex.Message}");
        }
    }

    private static Dictionary<uint, (Guid, int)> Programs()
    {
        var map = new Dictionary<uint, (Guid, int)>();
        if (JsonNode.Parse(GameTree.Dump(addedOnly: true))?["nodes"] is JsonArray a)
            foreach (var x in a)
                if (Guid.TryParse(x?["program"]?.GetValue<string>(), out var g) && g != Guid.Empty)
                    map[GameTree.ParseHex(x?["id"]?.GetValue<string>())] = (g, x?["maxLevel"]?.GetValue<int>() ?? 0);
        return map;
    }

    /// <summary>The effect entities running for a player's shadow nodes (diagnostics).</summary>
    public static IReadOnlyList<(uint Node, int Level, EntityId Impact)> EffectsOf(EntityId e) =>
        Effects.Where(kv => kv.Key.Entity == e.Value).Select(kv => (kv.Key.Node, kv.Value.Level, kv.Value.Impact)).ToArray();

    private static bool Send(EntityId e, Dictionary<uint, int> levels) =>
        TalentNet.SendShadow(e, new GameShadowMessage(levels.ToDictionary(kv => $"0x{kv.Key:x8}", kv => kv.Value), 0));

    // ---- client ----
    private static GameShadowMessage? _pending;   // main thread: from the server, not applied yet
    private static EntityId _clientPlayer;        // whose levels the client's table holds

    /// <summary>From the server. Kept until the local player is known: the message can come first.</summary>
    internal static void OnMessage(GameShadowMessage m)
    {
        if (!Kiln.Client.Available || Kiln.Client.IsHosting) return;  // a host reads the server world's table directly
        _pending = m;
        ClientTick();
    }

    /// <summary>Each client-world step: applies what the server sent. The table holds one session's state: another session's
    /// would show on the screen as if unlocked (the entity id is usually the same), so it goes with the session.</summary>
    internal static void ClientTick()
    {
        if (!Kiln.Client.Available || Kiln.Client.IsHosting)
        {
            if (!_clientPlayer.IsNone) ForgetClientSide();
            return;
        }
        var local = Kiln.Client.LocalPlayer;
        if (!_clientPlayer.IsNone && (local != _clientPlayer || !Network.ServerHasKiln)) ForgetClientSide();
        if (_pending is not { } m || local.IsNone) return;
        _pending = null;
        _clientPlayer = local;
        Set(2, local, m.Levels.ToDictionary(kv => GameTree.ParseHex(kv.Key), kv => kv.Value));
        SkillsInternals.Info($"shadow table: local player {local} has {m.Levels.Count} shadow node(s) from the server");
    }

    /// <summary>The connection ended: the next session starts with an empty table.</summary>
    internal static void ForgetClientSide()
    {
        _pending = null;
        if (_clientPlayer.IsNone) return;
        Set(2, _clientPlayer, new Dictionary<uint, int>());
        SkillsInternals.Info($"shadow table: local player {_clientPlayer}: the session ended, its shadow nodes are off the screen");
        _clientPlayer = default;
    }
}

public static unsafe partial class GameTree
{
    /// <summary>The shadow table is on (nodes past the game's 256-node / 100-level caps work): 1 server side, 2 with the
    /// client's skill-tree screen.</summary>
    public static int ShadowLevel => SkillsNative.T is var t && t != null ? (int)t->ShadowOn() : 0;

    /// <summary>Ids of the shadow nodes (index 256 and up) in the tree Kiln serves now.</summary>
    public static IReadOnlySet<uint> ShadowNodes => GameShadow.Nodes;

    /// <summary>A player's shadow levels (server world: world 0; a joined client's local player: world 2).</summary>
    public static IReadOnlyDictionary<uint, int> ShadowLevels(EntityId player, int world = 0) => GameShadow.Get((uint)world, player);

    /// <summary>Replaces a player's shadow levels (tick thread). The change is saved and sent like a game unlock.</summary>
    public static void SetShadowLevels(EntityId player, IReadOnlyDictionary<uint, int> levels) => GameShadow.Set(0, player, levels);

    /// <summary>The game's own PointsSpent for a player's real components (the shadow table answers inside it).</summary>
    public static int? PointsSpent(EntityId player)
    {
        Game.AssertTickThread();
        var t = SkillsNative.T;
        if (t == null) return null;
        int v;
        return t->Spent(player.Value, &v) == SkillsNative.Ok ? v : null;
    }

    /// <summary>The game's own IsUnlocked and Reachable for a node of a player (the shadow table answers inside them).</summary>
    public static (bool Unlocked, bool Reachable)? Query(EntityId player, uint node)
    {
        Game.AssertTickThread();
        var t = SkillsNative.T;
        if (t == null) return null;
        int u, r;
        return t->IsUnlocked(player.Value, node, &u, &r) == SkillsNative.Ok ? (u != 0, r != 0) : null;
    }

    /// <summary>Asks the game to unlock (0) / unlearn (1) a node or reset the tree (2) for a server-world player entity the way its client would (the game's own
    /// input handler, this step). Tick thread.
    /// <para>Ops 3 and up are for tests, and answer false unless <c>&lt;kiln&gt;/data/skills/lab-ops</c> exists. Op 3 marks the tree initialized (it skips the join check, which
    /// waits for skill-point knowledge a player entity without a client never gets). Op 4 sets an item entity's unlocked enhancements to <paramref name="node"/> (no cost, no
    /// replication). Op 5 sets a player entity's selected hotbar slot. Op <c>8 | slot &lt;&lt; 8</c> says which inventory slot an equipment slot is (<paramref name="node"/>: the
    /// inventory's entity, the slot's index in the top byte), on a player entity with none set.</para></summary>
    public static bool GameRequest(EntityId player, int op, uint node = 0)
    {
        Game.AssertTickThread();
        var t = SkillsNative.T;
        return t != null && t->GameRequest(player.Value, (uint)op, node) == SkillsNative.Ok;
    }

    /// <summary>Lab: this entity (a player template, no client of its own) is treated as a player in game by the shadow table's
    /// client sync, whichever Kiln peer has it as its player. With a holder (<see cref="Skills.HolderOf(CharacterKey, ulong)"/>) its
    /// table also loads and saves through that holder's data, as a real player's. <see cref="EntityId.None"/> ends it.</summary>
    [System.Diagnostics.CodeAnalysis.Experimental("KILNX001", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx001")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static void LabPlayer(EntityId player, TalentHolder? holder = null, bool forceNative = false) => GameShadow.LabPlayer(player, holder, forceNative);

    /// <summary>Lab: a holder's carried shadow levels ("0x&lt;node id&gt;" -> level), as a test reads them.</summary>
    [System.Diagnostics.CodeAnalysis.Experimental("KILNX001", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx001")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static IReadOnlyDictionary<string, int> LabCarried(TalentHolder holder) => new Dictionary<string, int>(Skills.DataOf<GameShadowState>(holder, GameShadow.DataKey).Levels);

    /// <summary>Lab: replaces a holder's carried shadow levels, as a client's copy coming in does.</summary>
    [System.Diagnostics.CodeAnalysis.Experimental("KILNX001", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx001")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static void LabSetCarried(TalentHolder holder, IReadOnlyDictionary<string, int> levels) =>
        Skills.DataOf<GameShadowState>(holder, GameShadow.DataKey).Levels = new Dictionary<string, int>(levels, StringComparer.Ordinal);

    /// <summary>Lab: what a character's carried copy coming in does: the table loads again from the holder's data.</summary>
    [System.Diagnostics.CodeAnalysis.Experimental("KILNX001", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx001")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static void LabReload(EntityId player) => GameShadow.Reload(player);

    /// <summary>Lab: the unlock requests of a player's latest move of carried entries into the game's own list (nodes that are native
    /// nodes in this tree), one per level, in the order they were asked.</summary>
    [System.Diagnostics.CodeAnalysis.Experimental("KILNX001", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx001")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static IReadOnlyList<uint> LabNativeRequests(EntityId player) => GameShadow.RequestsOf(player);

    /// <summary>Lab: writes a node into a player entity's UnlockedSkillNodes as a character's save holds it. False unless <c>&lt;kiln&gt;/data/skills/lab-ops</c> exists.</summary>
    [System.Diagnostics.CodeAnalysis.Experimental("KILNX001", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx001")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static bool LabPutInSave(EntityId player, uint node, int level)
    {
        Game.AssertTickThread();
        var t = SkillsNative.T;
        return t != null && t->GameRequest(player.Value, 6u | ((uint)level << 8), node) == SkillsNative.Ok;
    }

    /// <summary>Lab: does to that list what the login guard does with the shadow ids it finds at a join. False unless <c>&lt;kiln&gt;/data/skills/lab-ops</c> exists.</summary>
    [System.Diagnostics.CodeAnalysis.Experimental("KILNX001", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx001")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static bool LabLoginGuard(EntityId player)
    {
        Game.AssertTickThread();
        var t = SkillsNative.T;
        return t != null && t->GameRequest(player.Value, 7u, 0) == SkillsNative.Ok;
    }

    /// <summary>The effect entities Kiln runs for a player's shadow nodes.</summary>
    public static IReadOnlyList<(uint Node, int Level, EntityId Impact)> ShadowEffects(EntityId player) => GameShadow.EffectsOf(player);
}
