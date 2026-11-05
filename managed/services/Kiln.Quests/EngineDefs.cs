// SPDX-License-Identifier: MIT
// Kiln.Quests engine: what a quest chain is. The JSON loader (Json.cs) and C# mods (Quests.Define) both produce these
// objects; the engine (Engine.cs) only reads them. Usage: docs/kiln/services/quests.md, docs/kiln/modding/quests.md.
namespace Kiln;

/// <summary>Where a quest stands for one holder.</summary>
public enum QuestStatus
{
    /// <summary>Its <c>requires</c> does not hold yet.</summary>
    Locked = 0,
    /// <summary>It can be started.</summary>
    Available = 1,
    Active = 2,
    Done = 3,
    Failed = 4,
}

/// <summary>Who owns a quest's progress.</summary>
public enum QuestScope
{
    /// <summary>Each player on their own (the default).</summary>
    Player = 0,
    /// <summary>Each player has their own state, but players within <see cref="QuestDefinition.PartyRadius"/> metres
    /// share kills and other objective credit, and each gets the rewards.</summary>
    Party = 1,
    /// <summary>One shared state for the world.</summary>
    World = 2,
}

/// <summary>Where a quest is shown (JSON <c>journal</c>).</summary>
public enum QuestJournalMode
{
    /// <summary>Chat lines and <c>/quests</c>, for a mod that vanilla clients join (<c>clients: "none"</c>): the game's UI cannot
    /// reach them, because a client builds it from the mod's files.</summary>
    Chat = 0,
    /// <summary>The default: the game's own journal, tracked-quest HUD, notifications and map markers, built at boot from the
    /// mod's files on the server and on every client (<c>clients: "mod"</c>). The engine runs the quest.</summary>
    Native = 2,
}

/// <summary>A quest chain: steps that run in order, each with one or more objectives.</summary>
public sealed class QuestDefinition
{
    /// <summary>Letters, digits, '_' and '-'. Unique within the mod.</summary>
    public required string Id { get; init; }
    public UiText Name { get; set; } = "";
    public UiText? Description { get; set; }
    /// <summary>The NPC who offers it (an id of the same mod's <c>npcs</c>, or <c>mod:npc</c>). A quest with a giver can only be
    /// started through that NPC, or by an action or code.</summary>
    public string? Giver { get; set; }
    public QuestScope Scope { get; set; } = QuestScope.Player;
    public double PartyRadius { get; set; } = 60;
    public bool Main { get; set; }
    /// <summary>Starts by itself as soon as it is available (a world quest when the world runs, a player quest while
    /// the player is in game).</summary>
    public bool Auto { get; set; }
    /// <summary>Can be started again after it is done.</summary>
    public bool Repeatable { get; set; }
    /// <summary>Can be started again after it failed or was abandoned (default true).</summary>
    public bool Retryable { get; set; } = true;
    /// <summary>Players may give it up (never a world quest; default true).</summary>
    public bool Abandonable { get; set; } = true;
    /// <summary>Not listed until it is active.</summary>
    public bool Hidden { get; set; }
    public QuestJournalMode Journal { get; set; } = QuestJournalMode.Native;
    /// <summary>The game's journal: the portrait (keen::QuestSourceType: None, Flame, Blacksmith, Alchemist, ...).</summary>
    public string? Source { get; set; }
    /// <summary>The game's journal: its place in the list (the game uses 40..100; default 50).</summary>
    public int? Priority { get; set; }
    public List<QuestCondition> Requires { get; set; } = new();
    public List<QuestStep> Steps { get; set; } = new();
    public List<QuestAction> Rewards { get; set; } = new();
    public List<QuestAction> OnStart { get; set; } = new();
    public List<QuestAction> OnFail { get; set; } = new();

    /// <summary>The mod that defined it ("" for code outside a mod).</summary>
    public string ModId { get; internal set; } = "";
    /// <summary><c>&lt;modId&gt;:&lt;id&gt;</c>.</summary>
    public string FullId => ModId.Length > 0 ? $"{ModId}:{Id}" : Id;
    public QuestStep? Step(string id) => Steps.Find(s => s.Id.Equals(id, StringComparison.Ordinal));
    public override string ToString() => $"{FullId} ({Scope}, {Steps.Count} steps)";
}

/// <summary>One step of a quest. It is done when all its objectives are.</summary>
public sealed class QuestStep
{
    public required string Id { get; init; }
    public UiText Text { get; set; } = "";
    /// <summary>The game's journal: the step's short name (default its text).</summary>
    public UiText? Title { get; set; }
    /// <summary>The game's journal: the step's voice-over line (a name in the mod's cooked audio, or a GUID).</summary>
    public string? Voice { get; set; }
    /// <summary>The game's journal: the recommended level shown with the step.</summary>
    public int RecommendedLevel { get; set; }
    public List<QuestObjective> Objectives { get; set; } = new();
    /// <summary>The map marker and waypoint of the step. Null: one is made anyway when the step has a place (a site, a spawn, a
    /// reach or kill place, an NPC to talk to or deliver to). See <see cref="NoMarker"/>.</summary>
    public QuestMarker? Marker { get; set; }
    /// <summary>This step shows no marker, even though it has a place (JSON <c>"marker": false</c>).</summary>
    public bool NoMarker { get; set; }
    /// <summary>Where the step happens, found at run time on real ground outside every base, once per world.</summary>
    public QuestPlace? Site { get; set; }
    /// <summary>Creatures the step brings into the world. Kiln verifies they exist, and spawns them again when the game removed them.</summary>
    public List<QuestSpawn> Spawns { get; set; } = new();
    public List<QuestAction> OnEnter { get; set; } = new();
    public List<QuestAction> OnDone { get; set; } = new();
}

/// <summary>A step's map marker. <paramref name="At"/>: a fixed place (null: the step's other place decides).
/// <paramref name="Waypoint"/>: also write the player's waypoint, the game's own map and compass pin.
/// <paramref name="Name"/>: the marker's name on the map (default the step's text). <paramref name="Base"/>: the marker template to copy.</summary>
public sealed record QuestMarker(WorldPosition? At = null, double Radius = 0, bool Waypoint = true, UiText? Name = null, string? Base = null);

/// <summary>A place found at run time: on real ground (the voxel surface), outside every player base, not too steep. Kiln keeps
/// it per world, so everyone doing that step goes to one place.</summary>
public sealed class QuestPlace
{
    /// <summary>Search around this place.</summary>
    public WorldPosition? At { get; set; }
    /// <summary>Search around this NPC of the mod's <c>npcs</c>.</summary>
    public string? NearNpc { get; set; }
    /// <summary>Search around the player who gets there first (the default when nothing else is given).</summary>
    public bool NearPlayer { get; set; }
    /// <summary>Metres from the centre of the search. It widens by itself until a good spot is found, so a base in the way never fails it.</summary>
    public double MinDistance { get; set; }
    public double MaxDistance { get; set; }
    /// <summary>Metres to keep from a base's build zone (default 20).</summary>
    public double BaseMargin { get; set; } = 20;
    /// <summary>A base is fine (a friendly NPC that belongs in one). Enemies never take this: the game pacifies them in bases and may remove them.</summary>
    public bool InBase { get; set; }
    /// <summary>Exactly <see cref="At"/>: no ground, no base check.</summary>
    public bool Exact { get; set; }
}

/// <summary>Creatures a step brings into the world.</summary>
public sealed class QuestSpawn
{
    public required string Template { get; init; }
    public int Count { get; set; } = 1;
    /// <summary>Default the step's <see cref="QuestStep.Site"/>; without one, near the player at 30..60 m.</summary>
    public QuestPlace? Place { get; set; }
    /// <summary>The Kiln.Spawn tag: a kill objective can name it.</summary>
    public string? Tag { get; set; }
    /// <summary>Metres between them.</summary>
    public double Spread { get; set; } = 4;
    /// <summary>The game's level to spawn at (0: the template's).</summary>
    public int Level { get; set; }
    /// <summary>How many times Kiln spawns them again when the game removes them before they die (default 5).</summary>
    public int Retries { get; set; } = 5;
}

public enum QuestObjectiveKind
{
    Kill,
    Collect,
    Deliver,
    Reach,
    Talk,
    Craft,
    Build,
    Knowledge,
    Custom,
}

/// <summary>One thing to do in a step. Which fields count depends on <see cref="Kind"/>; the factories set them.</summary>
public sealed class QuestObjective
{
    public required QuestObjectiveKind Kind { get; init; }
    /// <summary>How many (kills, items, crafts, signals). Reach, talk, knowledge: 1.</summary>
    public int Count { get; set; } = 1;
    /// <summary>Shown instead of the step's text when a step has several objectives.</summary>
    public UiText? Text { get; set; }

    /// <summary>Kill: exact template name.</summary>
    public string? Template { get; set; }
    /// <summary>Kill: part of the template name ("Scavenger", "Vukah").</summary>
    public string? Family { get; set; }
    /// <summary>Kill: the Kiln.Spawn tag of the victim.</summary>
    public string? Tag { get; set; }
    /// <summary>Kill: only near this place (with <see cref="Radius"/>).</summary>
    public WorldPosition? Near { get; set; }
    /// <summary>Kill: Kiln.Spawn destroying a matching entity counts too (off: only deaths count).</summary>
    public bool CountDestroyed { get; set; }

    /// <summary>Collect, deliver, craft, build: item name or id.</summary>
    public string? Item { get; set; }
    /// <summary>Craft: recipe id ("0x…").</summary>
    public string? Recipe { get; set; }
    /// <summary>Deliver: the NPC who receives the items.</summary>
    public string? To { get; set; }

    /// <summary>Reach: the place.</summary>
    public WorldPosition? At { get; set; }
    /// <summary>Reach: an offset in metres from where the player stood when the step started, instead of <see cref="At"/>.</summary>
    public WorldPosition? Offset { get; set; }
    /// <summary>Reach: a template name; the nearest entity of that template is the place.</summary>
    public string? Marker { get; set; }
    /// <summary>Reach: the step's <see cref="QuestStep.Site"/> is the place.</summary>
    public bool Site { get; set; }
    /// <summary>Reach, kill: metres.</summary>
    public double Radius { get; set; } = 10;

    /// <summary>Talk: the NPC (an id of <c>npcs</c>, a template name, or "npc:&lt;debugName&gt;").</summary>
    public string? Npc { get; set; }
    /// <summary>Talk: a game dialogue line ("line:…", "0x…"), or a line of Kiln.Quests/Native.</summary>
    public string? Line { get; set; }
    /// <summary>Talk: a node of a Kiln conversation that must be reached.</summary>
    public string? Node { get; set; }

    /// <summary>Knowledge: the name that must hold ("npc:…", "quest:…", "query:…", "0x…").</summary>
    public string? Knowledge { get; set; }

    /// <summary>Custom: <see cref="Quests.Signal"/> with this name adds to the counter.</summary>
    public string? Signal { get; set; }
    /// <summary>Custom, C#: polled about once a second, returns the current count.</summary>
    public Func<QuestContext, int>? Progress { get; set; }

    public static QuestObjective Kill(string? template = null, string? family = null, string? tag = null, int count = 1) =>
        new() { Kind = QuestObjectiveKind.Kill, Template = template, Family = family, Tag = tag, Count = count };
    public static QuestObjective Collect(string item, int count = 1) => new() { Kind = QuestObjectiveKind.Collect, Item = item, Count = count };
    public static QuestObjective Deliver(string item, int count, string to) =>
        new() { Kind = QuestObjectiveKind.Deliver, Item = item, Count = count, To = to };
    public static QuestObjective Reach(WorldPosition at, double radius = 10) => new() { Kind = QuestObjectiveKind.Reach, At = at, Radius = radius };
    public static QuestObjective Talk(string npc, string? node = null, string? line = null) =>
        new() { Kind = QuestObjectiveKind.Talk, Npc = npc, Node = node, Line = line };
    public static QuestObjective Custom(string signal, int count = 1) => new() { Kind = QuestObjectiveKind.Custom, Signal = signal, Count = count };
    public static QuestObjective HasKnowledge(string name) => new() { Kind = QuestObjectiveKind.Knowledge, Knowledge = name };

    internal bool IsLevel => Kind is QuestObjectiveKind.Collect or QuestObjectiveKind.Reach or QuestObjectiveKind.Knowledge || Progress is not null;
}

public enum QuestConditionKind
{
    Knowledge,
    Level,
    Quest,
    Step,
    Item,
    All,
    Any,
    Not,
    Predicate,
}

/// <summary>A condition on a holder, checked on the server: a quest's <c>requires</c>, an option's <c>if</c>.</summary>
public sealed class QuestCondition
{
    public required QuestConditionKind Kind { get; init; }
    /// <summary>Knowledge name, quest id, "quest.step", item, or predicate name.</summary>
    public string? Name { get; set; }
    /// <summary>Quest: locked, available, active, done, failed. Step: pending, active, done.</summary>
    public string? Is { get; set; }
    /// <summary>Level: at least this level. Item: at least this many.</summary>
    public int Count { get; set; } = 1;
    public List<QuestCondition> Children { get; set; } = new();
    public Func<QuestContext, bool>? Predicate { get; set; }

    public static QuestCondition HasKnowledge(string name) => new() { Kind = QuestConditionKind.Knowledge, Name = name };
    public static QuestCondition Level(int level) => new() { Kind = QuestConditionKind.Level, Count = level };
    public static QuestCondition Quest(string quest, string state) => new() { Kind = QuestConditionKind.Quest, Name = quest, Is = state };
    public static QuestCondition QuestStep(string questDotStep, string state) => new() { Kind = QuestConditionKind.Step, Name = questDotStep, Is = state };
    public static QuestCondition HasItem(string item, int count = 1) => new() { Kind = QuestConditionKind.Item, Name = item, Count = count };
    public static QuestCondition When(Func<QuestContext, bool> predicate) => new() { Kind = QuestConditionKind.Predicate, Predicate = predicate };
}

public enum QuestActionKind
{
    Give,
    Take,
    Xp,
    Knowledge,
    Revoke,
    Start,
    Complete,
    Fail,
    Advance,
    Reset,
    Spawn,
    Despawn,
    Teleport,
    Chat,
    Toast,
    Waypoint,
    Talk,
    Signal,
    Call,
}

/// <summary>A reward or side effect (a quest's rewards and hooks, a conversation option's <c>do</c>).</summary>
public sealed class QuestAction
{
    public required QuestActionKind Kind { get; init; }
    /// <summary>Item, knowledge name, quest id, "quest.step", template, dialogue, signal or action name.</summary>
    public string? Name { get; set; }
    /// <summary>Items, spawned entities, or a signal's amount.</summary>
    public int Count { get; set; } = 1;
    /// <summary>Xp: the amount. Knowledge: the counter amount or flag number. Revoke: the flag number + 1 (0 = all).</summary>
    public uint Value { get; set; } = 1;
    public UiText? Text { get; set; }
    /// <summary>Spawn, teleport, waypoint: the place.</summary>
    public WorldPosition? At { get; set; }
    /// <summary>Spawn, teleport, waypoint: an offset in metres from the player, instead of <see cref="At"/>.</summary>
    public WorldPosition? Offset { get; set; }
    public double Yaw { get; set; }
    /// <summary>Spawn: the Kiln.Spawn tag.</summary>
    public string? Tag { get; set; }
    /// <summary>Spawn: never written to the save.</summary>
    public bool Transient { get; set; }
    /// <summary>Spawn: the entity becomes this NPC of <c>npcs</c> (see <see cref="Quests.BindNpc"/>).</summary>
    public string? BindNpc { get; set; }
    /// <summary>Spawn: exactly at the place given. Otherwise Kiln puts it on real ground outside every base.</summary>
    public bool Exact { get; set; }
    /// <summary>Knowledge, revoke: "world" or "player"; default: the player's object if the id has one, else the world's.</summary>
    public string? Target { get; set; }
    /// <summary>Toast: info, success, warning, error.</summary>
    public string? ToastKind { get; set; }
    /// <summary>Talk: the node to open at.</summary>
    public string? Node { get; set; }
    /// <summary>Waypoint: true clears it.</summary>
    public bool Clear { get; set; }
    /// <summary>Force an advance past unfinished objectives.</summary>
    public bool Force { get; set; }
    /// <summary>Call, C#: the code to run.</summary>
    public Func<QuestActionContext, Task>? Run { get; set; }

    public static QuestAction Item(string item, int count = 1) => new() { Kind = QuestActionKind.Give, Name = item, Count = count };
    public static QuestAction Experience(uint amount) => new() { Kind = QuestActionKind.Xp, Value = amount };
    public static QuestAction GrantKnowledge(string name, string? target = null) => new() { Kind = QuestActionKind.Knowledge, Name = name, Target = target };
    public static QuestAction Code(Func<QuestActionContext, Task> run) => new() { Kind = QuestActionKind.Call, Run = run };
}

/// <summary>What a condition or a custom objective is asked about.</summary>
public sealed record QuestContext(QuestHolder Holder, QuestDefinition? Quest, EntityId Npc, string ModId)
{
    public Player? Player => Holder.Entity.IsNone || !Players.Available ? null : Players.Find(Holder.Entity);
}

/// <summary>What an action runs for.</summary>
public sealed record QuestActionContext(QuestHolder Holder, QuestDefinition? Quest, EntityId Npc, string ModId)
{
    public Player? Player => Holder.Entity.IsNone || !Players.Available ? null : Players.Find(Holder.Entity);
    internal QuestContext Condition => new(Holder, Quest, Npc, ModId);
}

/// <summary>A conversation: nodes of text and options, per NPC or per quest. The server decides what is shown and checks
/// every choice again.</summary>
public sealed class DialogueDefinition
{
    public required string Id { get; init; }
    public string Start { get; set; } = "start";
    public Dictionary<string, DialogueNode> Nodes { get; set; } = new(StringComparer.Ordinal);
    /// <summary>The default: its lines are in the game's own NPC menu, with the game's voice playback. False: a chat conversation
    /// through <c>/talk</c>, for a mod that vanilla clients join.</summary>
    public bool Native { get; set; } = true;
    /// <summary>The game's menu: the start node's text is a greeting. Default: yes on a clone, no on a game NPC, whose own greetings stay.</summary>
    public bool? Greet { get; set; }
    /// <summary>The game's menu: the marker over the NPC's head on the greeting (Hint, Attention, MainQuest).</summary>
    public string? Hint { get; set; }
    /// <summary>The game's menu: more greetings, shown before the plain one while their condition holds.</summary>
    public List<DialogueGreeting> Greetings { get; set; } = new();
    /// <summary>The game's menu: replaces the NPC's farewell lines.</summary>
    public List<UiText> Farewell { get; set; } = new();
    /// <summary>The game's menu: replaces the line between topics.</summary>
    public List<UiText> Transition { get; set; } = new();
    public string ModId { get; internal set; } = "";
    public string FullId => ModId.Length > 0 ? $"{ModId}:{Id}" : Id;
    public override string ToString() => $"{FullId} ({Nodes.Count} nodes)";
}

/// <summary>A greeting that wins over the plain one while its condition holds.</summary>
public sealed class DialogueGreeting
{
    public UiText Text { get; set; } = "";
    public QuestCondition? If { get; set; }
    public string? Voice { get; set; }
    public string? Hint { get; set; }
}

public sealed class DialogueNode
{
    /// <summary>Overrides the NPC's name for this node.</summary>
    public UiText? Speaker { get; set; }
    public UiText Text { get; set; } = "";
    /// <summary>A voice-over line, played by the game's dialogue: a name in the mod's cooked audio, or a GUID (any line of the game).</summary>
    public string? Voice { get; set; }
    /// <summary>The game's menu: the marker over the NPC's head on this node's greeting.</summary>
    public string? Hint { get; set; }
    public List<DialogueOption> Options { get; set; } = new();
    /// <summary>A node without options: where "Continue" goes (default: the conversation ends).</summary>
    public string? Goto { get; set; }
    /// <summary>Actions when the node opens.</summary>
    public List<QuestAction> Do { get; set; } = new();
}

public sealed class DialogueOption
{
    public UiText Text { get; set; } = "";
    /// <summary>Shown only while this holds; checked again when chosen.</summary>
    public QuestCondition? If { get; set; }
    public List<QuestAction> Do { get; set; } = new();
    /// <summary>The next node; "end" or null ends the conversation.</summary>
    public string? Goto { get; set; }
    /// <summary>The game's menu: what the NPC answers. Default the target node's text, else the option's own.</summary>
    public UiText? Reply { get; set; }
    /// <summary>The game's menu: the reply's voice-over. Default the target node's.</summary>
    public string? Voice { get; set; }
    /// <summary>The game's menu: the topic goes away once heard.</summary>
    public bool Once { get; set; }
    /// <summary>The game's menu: the NPC plays its outro after the reply.</summary>
    public bool Close { get; set; }
    /// <summary>The game's menu: the topic's marker (Hint, Attention, MainQuest).</summary>
    public string? Hint { get; set; }
}

/// <summary>An NPC a quest or conversation refers to: a game NPC, or a new one (<see cref="Clone"/>), found by its template or
/// placed in the world (<see cref="Place"/>).</summary>
public sealed class QuestNpcDefinition
{
    public required string Id { get; init; }
    /// <summary>Template name of the entity (of a clone: the base).</summary>
    public string? Template { get; set; }
    /// <summary>A new NPC: a copy of <see cref="Template"/> under its own GUID, one per world. Needs <c>clients: "mod"</c>.</summary>
    public bool Clone { get; set; }
    /// <summary>The clone's GUID. Keep it forever; null derives it from the mod and this id.</summary>
    public Guid? CloneGuid { get; set; }
    /// <summary>The clone's template name; null derives it.</summary>
    public string? CloneName { get; set; }
    /// <summary>Where Kiln puts it in the world, once it can: on real ground, outside every base unless <see cref="QuestPlace.InBase"/>.</summary>
    public QuestPlace? Place { get; set; }
    /// <summary>The conversation it opens.</summary>
    public string? Dialogue { get; set; }
    public UiText? Name { get; set; }
    /// <summary>Where it stands (the JSON's <c>place.at</c>): with no entity found, this spot still counts as the NPC.</summary>
    public WorldPosition? At { get; set; }
    /// <summary>How close a player must be to talk (metres).</summary>
    public double TalkRange { get; set; } = 8;
    public string ModId { get; internal set; } = "";
    public string FullId => ModId.Length > 0 ? $"{ModId}:{Id}" : Id;
}

/// <summary>Who owns a quest's state: a character on this world (the game's own key: account + CharacterId), or the world.</summary>
/// <param name="Key">"world", "character:&lt;account&gt;.&lt;id&gt;", "player:&lt;account&gt;" (a synthetic holder, an offline
/// account, or a build without character ids) or "session:&lt;entity&gt;".</param>
/// <param name="Account">The Steam or account id (where the data was filed before characters); 0 for the world and for a session holder.</param>
/// <param name="Entity">The player's entity while in game; none when offline, for the world, and for synthetic holders.</param>
public readonly record struct QuestHolder(string Key, ulong Account, EntityId Entity)
{
    /// <summary>The character whose book this is; none for the world, synthetic holders and builds without character ids.</summary>
    public CharacterKey Character { get; init; }

    public static QuestHolder World { get; } = new("world", 0, EntityId.None);
    public bool IsWorld => Key == "world";
    public bool IsNone => string.IsNullOrEmpty(Key);

    /// <summary>The save key of the hosting player of a local game when the game gives no Steam or account id.</summary>
    internal const ulong HostKey = 1;

    /// <summary>The holder of a player in game.</summary>
    public static QuestHolder Of(EntityId player)
    {
        if (player.IsNone) return default;
        var p = Players.Available ? Players.Find(player) : null;
        return p is null ? new QuestHolder($"session:{player.Value}", 0, player) : Of(p);
    }

    public static QuestHolder Of(Player p)
    {
        var account = p.SteamId != 0 ? p.SteamId : p.AccountId != 0 ? p.AccountId
            : Client.Available && Client.IsHosting && p.Slot == 0 ? HostKey : 0;
        var c = CharacterData.KeyOf(p.Entity);
        if (c is { IsNone: false, IsAccountOnly: false }) return new QuestHolder($"character:{c.Text}", account, p.Entity) { Character = c };
        return account == 0 ? new QuestHolder($"session:{p.Entity.Value}", 0, p.Entity) : new QuestHolder($"player:{account}", account, p.Entity);
    }

    /// <summary>A character, online or not. <paramref name="legacyAccount"/>: the Steam or account id its book may still be
    /// filed under from before characters (it moves to the first character that asks).</summary>
    public static QuestHolder OfCharacter(CharacterKey character, ulong legacyAccount = 0) =>
        new($"character:{character.Text}", legacyAccount, EntityId.None) { Character = character };

    /// <summary>A player known only by their account (offline): the character they last played in this world, or the account's
    /// book from before characters.</summary>
    public static QuestHolder OfAccount(ulong account) => new($"player:{account}", account, EntityId.None);

    /// <summary>A holder without an entity for tests and tools: its state is saved like a player's, it has no position
    /// or inventory of its own (<see cref="QuestInput"/> feeds them), and actions that need a player are skipped and logged.</summary>
    public static QuestHolder Synthetic(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var h = 0xcbf29ce484222325ul;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(name)) h = (h ^ b) * 0x100000001b3ul;
        return new QuestHolder($"player:{h | 0x8000000000000000ul}", h | 0x8000000000000000ul, EntityId.None);
    }

    /// <summary>"world", "synthetic:&lt;name&gt;", or the name or entity number of a player in game. Throws
    /// <see cref="ArgumentException"/> for anything else. Tick thread.</summary>
    public static QuestHolder Parse(string who)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(who);
        if (who.Equals("world", StringComparison.OrdinalIgnoreCase)) return World;
        if (who.StartsWith("synthetic:", StringComparison.OrdinalIgnoreCase)) return Synthetic(who["synthetic:".Length..]);
        var p = !Players.Available ? null : uint.TryParse(who, out var e) ? Players.Find(new EntityId(e)) : Players.Find(who);
        return p is { InGame: true } ? Of(p) : throw new ArgumentException($"'{who}' is not \"world\", \"synthetic:<name>\" or a player in game");
    }

    public override string ToString() => Entity.IsNone ? Key : $"{Key} (entity {Entity})";
}

/// <summary>One holder's standing in one quest.</summary>
public sealed record QuestProgress(QuestDefinition Quest, QuestHolder Holder, QuestStatus Status, string? StepId, int StepIndex,
    IReadOnlyDictionary<string, int> Counters, int Completions)
{
    /// <summary>The counter of an objective of the current step.</summary>
    public int Counter(int objective = 0) => StepId is not null && Counters.TryGetValue(QuestRecord.CounterKey(StepId, objective), out var v) ? v : 0;
    public override string ToString() => $"{Quest.FullId}: {Status}{(StepId is null ? "" : $" at {StepId}")}";
}

/// <summary>The answer to a quest change. Refusals are not exceptions: <see cref="Reason"/> says why.</summary>
public sealed record QuestResult(bool Ok, string Reason, QuestStatus Status)
{
    internal static QuestResult No(string why, QuestStatus status = QuestStatus.Locked) => new(false, why, status);
    public override string ToString() => Ok ? $"ok ({Status})" : $"refused: {Reason}";
}

/// <summary>What the engine holds and has done.</summary>
public sealed record QuestEngineStatus(bool Started, bool ServerSide, int Quests, int Dialogues, int Npcs, int Conversations,
    int Errors, ulong Transitions, ulong Actions, ulong ActionFailures, bool Waypoints, IReadOnlyList<string> LoadErrors)
{
    /// <summary>Always false: the channel kiln.quests is gone with Kiln's journal and windows. For Kiln.Admin's status op, until it drops the field.</summary>
    public bool Channel => false;
}
