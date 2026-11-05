// SPDX-License-Identifier: MIT
// Kiln.Skills, part 2: Kiln talent trees (whole new trees that do not need the game's tree). Definitions; the runtime
// is TreeRuntime.cs, the network part TreeNet.cs, the screen TreeScreen.cs. Usage: docs/kiln/services/skills.md.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>A Kiln talent tree: nodes on a grid, their links (requirements), costs, levels, icons, texts and
/// effects. Kiln keeps each character's unlocks and points on the server (Kiln.Saves player data of the mod that
/// registered the tree), applies the effects at unlock, join and respec, and draws the tree as a screen in the game's
/// look (pause-menu entry and/or a key). Register it with <see cref="Skills.Register(TalentTree)"/> on both sides (a
/// <c>side: "both"</c> mod): the server runs it, Kiln clients with the mod draw it; others use the chat command
/// <c>/talents</c>.</summary>
public sealed class TalentTree
{
    /// <summary>Unique id, "&lt;mod&gt;:&lt;name&gt;" (letters, digits, '.', '_', '-', ':'). The save key: never change a
    /// released id.</summary>
    public required string Id { get; init; }
    public UiText Name { get; init; } = "Talents";
    public UiText? Description { get; init; }
    /// <summary>Where the points come from (default: 1 at level 1, plus 1 per level).</summary>
    public TalentPoints Points { get; init; } = TalentPoints.Own(start: 1, perLevel: 1);
    public IReadOnlyList<TalentNode> Nodes { get; init; } = [];
    /// <summary>An entry in the game's pause menu that opens the tree (client). Null = none.</summary>
    public UiText? MenuEntry { get; init; } = null;
    /// <summary>Also in the main menu? (Only shows a note there: there is no character before a world.)</summary>
    public int MenuOrder { get; init; } = 20;
    /// <summary>A key that opens the tree ("K", "Shift+T"); null = none. Players remap it in the Kiln menu.</summary>
    public string? Key { get; init; }
    /// <summary>The respec rule: free (default) or refused.</summary>
    public bool AllowRespec { get; init; } = true;
    /// <summary>Called on the server (tick thread) after a player's tree changed (unlock, lock, respec, grant).</summary>
    public Action<TalentChange>? OnChanged { get; init; }

    /// <summary>The mod folder icons are read from (set by <see cref="Skills.Register(TalentTree)"/> to the calling
    /// mod's folder; icon paths are relative to it).</summary>
    public string? Directory { get; set; }

    internal TalentNode? Node(string id) => Nodes.FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.Ordinal));
}

/// <summary>A node of a <see cref="TalentTree"/>.</summary>
public sealed class TalentNode
{
    /// <summary>Unique in the tree; the save key.</summary>
    public required string Id { get; init; }
    public UiText Name { get; init; } = "";
    public UiText? Description { get; init; }
    /// <summary>Grid position (column, row from the top-left). The screen draws one cell per position.</summary>
    public int Column { get; init; }
    public int Row { get; init; }
    /// <summary>Points per level.</summary>
    public int Cost { get; init; } = 1;
    /// <summary>Levels (1 = a single unlock).</summary>
    public int MaxLevel { get; init; } = 1;
    /// <summary>Nodes of the same tree that lead here: any one of them unlocked (all, with <see cref="RequireAll"/>).
    /// Empty = available from the start. Drawn as links.</summary>
    public IReadOnlyList<string> Requires { get; init; } = [];
    public bool RequireAll { get; init; }
    /// <summary>Points spent in this tree before this node can be taken (tiers).</summary>
    public int PointsSpentRequired { get; init; }
    /// <summary>The character level needed (0 = none).</summary>
    public int LevelRequired { get; init; }
    /// <summary>A PNG, relative to the tree's mod folder (client).</summary>
    public string? Icon { get; init; }
    public IReadOnlyList<TalentEffect> Effects { get; init; } = [];
}

/// <summary>Where a tree's points come from.</summary>
public sealed class TalentPoints
{
    internal enum Kind { Own, Share, Custom }

    internal Kind Source { get; private init; }
    internal int Start { get; private init; }
    internal int PerLevel { get; private init; }
    internal double Share { get; private init; }
    internal Func<TalentHolder, int>? Total { get; private init; }

    /// <summary>The tree's own currency: <paramref name="start"/> + <paramref name="perLevel"/> x (character level - 1)
    /// + what code granted (<see cref="Skills.Grant(string, TalentHolder, int)"/>). Independent of the game's skill points.</summary>
    public static TalentPoints Own(int start = 0, int perLevel = 1) => new() { Source = Kind.Own, Start = start, PerLevel = perLevel };

    /// <summary>A share of the game's skill points: floor(<paramref name="share"/> x the game's skill-point total)
    /// + granted. The game's own tree keeps all of its points (this is a budget on top, derived from them; the game's
    /// total is capped by its level and skill-point knowledge, see docs/kiln/services/progression.md).</summary>
    public static TalentPoints ShareOfGame(double share) => new() { Source = Kind.Share, Share = share };

    /// <summary>Code decides the total (server, tick thread); spent points are subtracted.</summary>
    public static TalentPoints Custom(Func<TalentHolder, int> total) => new() { Source = Kind.Custom, Total = total };

    public override string ToString() => Source switch
    {
        Kind.Own => $"own ({Start} + {PerLevel}/level)",
        Kind.Share => $"{Share:P0} of the game's skill points",
        _ => "custom",
    };
}

/// <summary>A character as a tree sees it: the game's character (the save key: <see cref="Character"/>), the Steam id, and
/// the player entity while in game. Use <see cref="Skills.HolderOf(EntityId)"/> or
/// <see cref="Skills.HolderOf(CharacterKey, ulong)"/>. A holder with only a Steam id means the character that id last
/// played in this world, or (no such character: Kiln data from before characters) the account's data.</summary>
public readonly record struct TalentHolder(ulong SteamId, EntityId Entity)
{
    /// <summary>The character whose unlocks these are; none when only the Steam id is known.</summary>
    public CharacterKey Character { get; init; }

    public override string ToString() =>
        (Entity.IsNone ? $"steam {SteamId}" : $"entity {Entity} (steam {SteamId})") + (Character.IsNone ? "" : $" character {Character}");
}

/// <summary>What a node does while it is unlocked. Effects start at unlock and at every join, end at lock and respec,
/// and are re-applied when the level changes.</summary>
public abstract class TalentEffect
{
    internal abstract string Describe();

    /// <summary>A buff (a game buff's or a kiln.gamedata buff clone's GUID) kept on the player: applied for
    /// <paramref name="refreshSeconds"/> + 5 s and renewed every <paramref name="refreshSeconds"/>, so it ends within
    /// that time after a lock (Kiln cannot take a game buff off early).</summary>
    public static TalentEffect Buff(Guid buff, double refreshSeconds = 30) => new BuffEffect(buff, null, refreshSeconds);
    /// <summary>A buff by name: a buff clone's name ("MyMod_Frost") or "0x&lt;buff id&gt;", resolved through
    /// kiln.gamedata when it is applied.</summary>
    public static TalentEffect Buff(string buff, double refreshSeconds = 30) => new BuffEffect(null, buff, refreshSeconds);

    /// <summary>The game's own attribute-node program (the "+1 Strength" kind of node) with
    /// <paramref name="amountPerLevel"/> x level of <paramref name="attribute"/> (its AttributeReference GUID, e.g.
    /// <see cref="TalentAttributes"/>).</summary>
    public static TalentEffect Attribute(Guid attribute, int amountPerLevel = 1) => new AttributeEffect(attribute, amountPerLevel);

    /// <summary>What a node of the game's own tree does, run for this player (its program with that node's config
    /// values, as the game runs it for its own unlocks). The node is not unlocked in the game's tree.</summary>
    public static TalentEffect GameNode(uint nodeId) => new GameNodeEffect(nodeId);

    /// <summary>Any Impact program kept running on the player (root = the player) with config values.</summary>
    public static TalentEffect Program(Guid program, IReadOnlyDictionary<string, object>? configs = null) =>
        new ProgramEffect(program, configs);

    /// <summary>A Kiln.Abilities ability (id) that only players with this node can cast.</summary>
    public static TalentEffect Ability(string abilityId) => new AbilityEffect(abilityId);

    /// <summary>Code (server, tick thread): <paramref name="apply"/> at unlock / join / level change,
    /// <paramref name="remove"/> before a level change, at lock, respec, leave and unload.</summary>
    public static TalentEffect Callback(Action<TalentContext> apply, Action<TalentContext>? remove = null) =>
        new CallbackEffect(apply, remove);
}

internal sealed class BuffEffect(Guid? guid, string? name, double refresh) : TalentEffect
{
    public Guid? Guid = guid;
    public readonly string? Name = name;
    public readonly double Refresh = Math.Max(5, refresh);
    internal override string Describe() => TalentUi.Frame.F(TalentUi.Frame.EffectBuff, Name ?? Guid?.ToString());
}

internal sealed class AttributeEffect(Guid attribute, int amount) : TalentEffect
{
    public readonly Guid AttributeGuid = attribute;
    public readonly int Amount = amount;
    internal override string Describe() => TalentUi.Frame.F(TalentUi.Frame.EffectAttribute, TalentAttributes.DisplayName(AttributeGuid), Amount);
}

internal sealed class GameNodeEffect(uint node) : TalentEffect
{
    public readonly uint Node = node;
    internal override string Describe() => TalentUi.Frame.F(TalentUi.Frame.EffectGameNode, $"0x{Node:x8}");
}

internal sealed class ProgramEffect(Guid program, IReadOnlyDictionary<string, object>? configs) : TalentEffect
{
    public readonly Guid ProgramGuid = program;
    public readonly IReadOnlyDictionary<string, object>? Configs = configs;
    internal override string Describe() => TalentUi.Frame.F(TalentUi.Frame.EffectProgram, ProgramGuid);
}

internal sealed class AbilityEffect(string id) : TalentEffect
{
    public readonly string AbilityId = id;
    internal override string Describe() => TalentUi.Frame.F(TalentUi.Frame.EffectAbility, AbilityId);
}

internal sealed class CallbackEffect(Action<TalentContext> apply, Action<TalentContext>? remove) : TalentEffect
{
    public readonly Action<TalentContext> ApplyFn = apply;
    public readonly Action<TalentContext>? RemoveFn = remove;
    internal override string Describe() => TalentUi.Frame.EffectCode.ToString();
}

/// <summary>The AttributeReference GUIDs the game's attribute nodes use (from its SkillTreeResource, svn 1287369; the
/// names were matched in the lab through each attribute component of a player entity).</summary>
public static class TalentAttributes
{
    public static readonly Guid Strength = new("64810163-59a0-4b55-82b9-5bb6290b22d7");
    public static readonly Guid Constitution = new("0b1ceec5-402d-43ff-9a12-43392d53cf26");
    public static readonly Guid Intelligence = new("2609852d-a281-4176-804e-b2dc4b8eb8e8");
    public static readonly Guid Spirit = new("0e31beac-22c5-4572-a98a-e39dca2bd1f7");
    public static readonly Guid Dexterity = new("4be83310-d59c-4f6d-89dd-1e61e033db86");
    public static readonly Guid Endurance = new("6bd1a147-1ea7-441e-8c6e-6f8b68dbcbfe");

    internal static readonly Dictionary<string, Guid> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Strength"] = Strength, ["Constitution"] = Constitution, ["Intelligence"] = Intelligence, ["Spirit"] = Spirit,
        ["Dexterity"] = Dexterity, ["Endurance"] = Endurance,
    };

    public static string NameOf(Guid g) => ByName.FirstOrDefault(kv => kv.Value == g).Key ?? g.ToString();

    // the game's own German names (its De_De loca)
    private static readonly Dictionary<string, string> German = new(StringComparer.Ordinal)
    {
        ["Strength"] = "Stärke", ["Constitution"] = "Konstitution", ["Intelligence"] = "Intelligenz",
        ["Spirit"] = "Willenskraft", ["Dexterity"] = "Geschick", ["Endurance"] = "Kondition",
    };

    /// <summary>The attribute's name in the game's text language.</summary>
    internal static string DisplayName(Guid g)
    {
        var name = NameOf(g);
        return German.TryGetValue(name, out var de) ? UiText.Of(("en", name), ("de", de)).ToString() : name;
    }

    /// <summary>The game's attribute-node program and its two config ids (attribute, amount).</summary>
    internal static readonly Guid Program = new("937e39f4-4863-490c-96a4-b7b9b385d0ed");
    internal const uint ConfigAttribute = 0x36f0ee47, ConfigAmount = 0x0a0c87e7;
}

/// <summary>Passed to callbacks and effects (server, tick thread).</summary>
public sealed class TalentContext
{
    internal TalentContext(TalentTree tree, TalentNode node, TalentHolder holder, int level)
    {
        Tree = tree;
        Node = node;
        Holder = holder;
        Level = level;
    }

    public TalentTree Tree { get; }
    public TalentNode Node { get; }
    public TalentHolder Holder { get; }
    public EntityId Player => Holder.Entity;
    /// <summary>The level being applied (or the one being removed).</summary>
    public int Level { get; }
    /// <summary>Anything the effect wants to keep until its remove (per player and node).</summary>
    public object? State { get; set; }
}

/// <summary>What changed in a player's tree.</summary>
public enum TalentChangeKind { Unlock, Lock, Respec, Grant, Reset }

public sealed record TalentChange(TalentTree Tree, TalentHolder Holder, TalentChangeKind Kind, string? Node, int OldLevel,
    int NewLevel, int Points);

/// <summary>A player is about to take a node (all checks passed, nothing changed yet). Cancel to refuse; set
/// <see cref="Reason"/>. Server, tick thread.</summary>
public sealed class TalentUnlockingEvent : CancelableEvent
{
    internal TalentUnlockingEvent(TalentTree tree, TalentNode node, TalentHolder holder, int level, int cost)
    {
        Tree = tree;
        Node = node;
        Holder = holder;
        Level = level;
        Cost = cost;
    }

    public TalentTree Tree { get; }
    public TalentNode Node { get; }
    public TalentHolder Holder { get; }
    /// <summary>The level the node gets.</summary>
    public int Level { get; }
    public int Cost { get; }
    public string Reason { get; set; } = "refused";
}

/// <summary>A player's tree changed (after the effects were applied). Server, tick thread; observe only.</summary>
public sealed class TalentChangedEvent : KilnEvent
{
    internal TalentChangedEvent(TalentChange change) => Change = change;
    public TalentChange Change { get; }
    public override string ToString() =>
        $"{Change.Tree.Id} {Change.Kind} {Change.Node} {Change.OldLevel}->{Change.NewLevel} ({Change.Holder})";
}

/// <summary>A tree as JSON (skills/*.json "trees": [...]); see docs/kiln/services/skills.md.</summary>
internal static class TalentJson
{
    public static UiText Text(JsonNode? n, string fallback)
    {
        if (n is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        if (n is JsonObject o)
            return new UiText(o.Where(kv => kv.Value is JsonValue).ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>()));
        return fallback;
    }

    public static TalentTree Tree(JsonObject j, string directory)
    {
        var id = j["id"]?.GetValue<string>() ?? throw new FormatException("a tree needs \"id\"");
        var points = TalentPoints.Own(start: 1, perLevel: 1);
        if (j["points"] is JsonObject p)
        {
            if (p["share"] is JsonValue sh) points = TalentPoints.ShareOfGame(sh.GetValue<double>());
            else points = TalentPoints.Own(p["start"]?.GetValue<int>() ?? 0, p["perLevel"]?.GetValue<int>() ?? 1);
        }
        var nodes = new List<TalentNode>();
        if (j["nodes"] is JsonArray na)
            foreach (var x in na.OfType<JsonObject>()) nodes.Add(Node(x));
        return new TalentTree
        {
            Id = id,
            Name = Text(j["name"], id),
            Description = j["description"] is { } d ? Text(d, "") : null,
            Points = points,
            Nodes = nodes,
            MenuEntry = j["menu"] is JsonValue mv && mv.TryGetValue<bool>(out var mb) ? (mb ? Text(j["name"], id) : null)
                : j["menu"] is { } m ? Text(m, id) : null,
            Key = j["key"]?.GetValue<string>(),
            AllowRespec = j["respec"]?.GetValue<bool>() ?? true,
            Directory = directory,
        };
    }

    private static TalentNode Node(JsonObject j)
    {
        var id = j["id"]?.GetValue<string>() ?? throw new FormatException("a node needs \"id\"");
        var effects = new List<TalentEffect>();
        if (j["effects"] is JsonArray ea)
            foreach (var e in ea.OfType<JsonObject>()) effects.Add(Effect(e, id));
        int col = 0, row = 0;
        if (j["position"] is JsonArray pos && pos.Count == 2)
        {
            col = pos[0]!.GetValue<int>();
            row = pos[1]!.GetValue<int>();
        }
        return new TalentNode
        {
            Id = id,
            Name = Text(j["name"], id),
            Description = j["description"] is { } d ? Text(d, "") : null,
            Column = j["col"]?.GetValue<int>() ?? col,
            Row = j["row"]?.GetValue<int>() ?? row,
            Cost = j["cost"]?.GetValue<int>() ?? 1,
            MaxLevel = j["maxLevel"]?.GetValue<int>() ?? 1,
            Requires = j["requires"] is JsonArray r ? r.Select(x => x!.GetValue<string>()).ToArray() :
                j["requires"] is JsonValue rv ? [rv.GetValue<string>()] : [],
            RequireAll = j["requireAll"]?.GetValue<bool>() ?? false,
            PointsSpentRequired = j["pointsSpent"]?.GetValue<int>() ?? 0,
            LevelRequired = j["level"]?.GetValue<int>() ?? 0,
            Icon = j["icon"]?.GetValue<string>(),
            Effects = effects,
        };
    }

    private static TalentEffect Effect(JsonObject e, string node)
    {
        double refresh = e["refresh"]?.GetValue<double>() ?? 30;
        if (e["buff"] is JsonValue b)
        {
            var s = b.GetValue<string>();
            return Guid.TryParse(s, out var g) ? TalentEffect.Buff(g, refresh) : TalentEffect.Buff(s, refresh);
        }
        if (e["attribute"] is JsonValue a)
        {
            var s = a.GetValue<string>();
            var g = TalentAttributes.ByName.TryGetValue(s, out var n) ? n :
                Guid.TryParse(s, out var gg) ? gg : throw new FormatException($"node {node}: unknown attribute \"{s}\"");
            return TalentEffect.Attribute(g, e["amount"]?.GetValue<int>() ?? 1);
        }
        if (e["gameNode"] is JsonValue gn)
        {
            var id = GameTree.ParseHex(gn.GetValue<string>());
            if (id == 0) throw new FormatException($"node {node}: \"gameNode\" must be \"0x<node id>\"");
            return TalentEffect.GameNode(id);
        }
        if (e["program"] is JsonValue pg)
        {
            var configs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (e["configs"] is JsonObject co)
                foreach (var (k, v) in co)
                    configs[k] = v is JsonValue jv && jv.TryGetValue<double>(out var dv) ? dv :
                        v is JsonValue sv && sv.TryGetValue<string>(out var ss) && Guid.TryParse(ss, out var cg) ? cg :
                        v is JsonValue bv && bv.TryGetValue<bool>(out var bb) ? bb : (object)(v?.ToJsonString() ?? "");
            return TalentEffect.Program(Guid.Parse(pg.GetValue<string>()), configs);
        }
        if (e["ability"] is JsonValue ab) return TalentEffect.Ability(ab.GetValue<string>());
        throw new FormatException($"node {node}: an effect needs \"buff\", \"attribute\", \"gameNode\", \"program\" or \"ability\"");
    }
}
