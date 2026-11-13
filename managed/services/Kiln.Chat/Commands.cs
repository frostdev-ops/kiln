// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>Who may run a command (the default level of its permission node).</summary>
public enum CommandPermission
{
    /// <summary>Every player, the console and remote tools.</summary>
    Everyone = 0,
    /// <summary>Admins: players for whom <see cref="Commands.IsAdmin"/> is true, the console, admin remote tools.</summary>
    Admin = 1,
    /// <summary>Only the console and remote tools (Kiln.Admin, the control folder); from chat only for players granted
    /// the command's node by name in permissions.json.</summary>
    Console = 2,
}

public enum CommandSourceKind
{
    /// <summary>Server-side code or the operator (always admin).</summary>
    Console = 0,
    /// <summary>A player typing in chat.</summary>
    Player = 1,
    /// <summary>A remote tool such as a dashboard (admin or not, as it says).</summary>
    Remote = 2,
}

/// <summary>Who runs a command.</summary>
public sealed class CommandSource
{
    private readonly bool _admin;

    private CommandSource(CommandSourceKind kind, EntityId player, string name, bool admin, ulong steamId = 0, bool hostConsole = false)
    {
        Kind = kind;
        Player = player;
        Name = name;
        _admin = admin;
        _steamId = steamId;
        IsHostConsole = hostConsole;
    }

    private ulong _steamId;

    public CommandSourceKind Kind { get; }

    /// <summary>The player (for <see cref="CommandSourceKind.Player"/>), else none.</summary>
    public EntityId Player { get; }

    public string Name { get; }

    public bool IsPlayer => Kind == CommandSourceKind.Player;

    /// <summary>The console of a game that hosts its own world, acting as that game's local player: <see cref="Player"/>
    /// is the host's character, so commands that need a player (a position, <c>@s</c>, <c>~</c>) act on it, and the
    /// rights are the console's. The console folder runs its lines so on a hosting client; a dedicated server's console
    /// is <see cref="Console"/>.</summary>
    public bool IsHostConsole { get; }

    /// <summary>Admin rights: <see cref="Commands.IsAdmin"/>.</summary>
    public bool IsAdmin => Commands.IsAdmin(this);

    internal bool DeclaredAdmin => _admin;

    /// <summary>The player's SteamID64 (looked up through Kiln.Players for online players); 0 when unknown or not a
    /// player.</summary>
    public ulong SteamId
    {
        get
        {
            if (_steamId != 0 || Kind != CommandSourceKind.Player || Player.IsNone) return _steamId;
            try
            {
                return _steamId = Permissions.FindPlayer(Player)?.SteamId ?? 0;
            }
            catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException)
            {
                return 0;
            }
        }
    }

    /// <summary>A player known only by SteamID64 (offline, or acting through a tool): permissions come from
    /// permissions.json and, when online, their user group. No entity, so no <c>@s</c> or <c>~</c>.</summary>
    internal bool SteamOnly => Kind == CommandSourceKind.Player && Player.IsNone && _steamId != 0;

    /// <summary>The operator / server code. Admin.</summary>
    public static CommandSource Console { get; } = new(CommandSourceKind.Console, EntityId.None, "console", true);

    /// <summary>The console of a hosting game, acting as its local player <paramref name="player"/> (<see cref="IsHostConsole"/>).</summary>
    internal static CommandSource HostConsole(EntityId player, string? name = null) =>
        new(CommandSourceKind.Player, player, name ?? Chat.FindPlayer(player)?.Name ?? $"player {player}", true, hostConsole: true);

    /// <summary>A player (admin rights come from <see cref="Commands.IsAdmin"/>).</summary>
    public static CommandSource ForPlayer(EntityId player, string? name = null) =>
        new(CommandSourceKind.Player, player, name ?? Chat.FindPlayer(player)?.Name ?? $"player {player}", false);

    /// <summary>A player by SteamID64, online or not (a dashboard "run as", tests). Their rights come from
    /// permissions.json (and their user group while they are online).</summary>
    public static CommandSource ForSteamId(ulong steamId, string? name = null)
    {
        if (steamId == 0) throw new ArgumentException("SteamID64 0", nameof(steamId));
        return new(CommandSourceKind.Player, EntityId.None, name ?? $"steam {steamId}", false, steamId);
    }

    /// <summary>A remote tool (e.g. a dashboard request), with or without admin rights.</summary>
    public static CommandSource Remote(string name, bool admin = true) => new(CommandSourceKind.Remote, EntityId.None, name, admin);

    /// <summary>The source as Kiln.Permissions sees it.</summary>
    public PermissionSubject Subject => Kind switch
    {
        _ when IsHostConsole => PermissionSubject.Console,
        CommandSourceKind.Console => PermissionSubject.Console,
        CommandSourceKind.Remote => PermissionSubject.Remote(Name, _admin),
        _ when SteamOnly => PermissionSubject.ForSteamId(_steamId, Name),
        _ => new PermissionSubject
        {
            Kind = PermissionSubjectKind.Player, Name = Name, Entity = Player, AdminOverride = Commands.BaseAdmin(this),
        },
    };

    public static implicit operator PermissionSubject(CommandSource source) => source.Subject;

    /// <summary>Whether the source has a permission node (<see cref="Permissions.Has(PermissionSubject, string)"/>).</summary>
    public bool Has(string node) => Permissions.Has(Subject, node);

    public override string ToString() => Kind == CommandSourceKind.Player
        ? IsHostConsole ? $"console as {Name} (entity {Player})" : SteamOnly ? $"{Name} (steam {_steamId})" : $"{Name} (entity {Player})"
        : $"{Kind.ToString().ToLowerInvariant()} {Name}";
}

/// <summary>A registered command.</summary>
public sealed class CommandInfo
{
    internal CommandInfo(string name, string[] aliases, string usage, string description, CommandPermission permission,
        string owner)
    {
        Name = name;
        Aliases = aliases;
        Usage = usage;
        Description = description;
        Permission = permission;
        Owner = owner;
        PermissionNode = $"{owner}.command.{name}";
    }

    public string Name { get; }
    public IReadOnlyList<string> Aliases { get; }

    /// <summary>Arguments, e.g. "&lt;hours&gt; [mode]" (typed commands: of the first usage line).</summary>
    public string Usage { get; }

    public string Description { get; }

    /// <summary>The default level of <see cref="PermissionNode"/>.</summary>
    public CommandPermission Permission { get; }

    /// <summary>Id of the mod that registered it ("kiln" for built-ins and host code).</summary>
    public string Owner { get; }

    /// <summary>The permission node a player needs (default <c>&lt;owner&gt;.command.&lt;name&gt;</c>).</summary>
    public string PermissionNode { get; internal init; }

    /// <summary>The tree of a typed command (null for a command registered with a plain handler).</summary>
    public LiteralNode? Tree { get; internal init; }

    /// <summary>Every way to run it, without the prefix ("heal &lt;target&gt; [amount]").</summary>
    public IReadOnlyList<string> UsageLines { get; internal init; } = [];

    public bool IsTyped => Tree is not null;

    /// <summary>"/name usage".</summary>
    public string Syntax => $"{Commands.Prefix}{Name}{(Usage.Length > 0 ? " " + Usage : "")}";

    /// <summary>Name, aliases, description, permission, usage and the argument tree (for the dashboard).</summary>
    public JsonObject ToJson(CommandSource? visibleTo = null)
    {
        var o = new JsonObject
        {
            ["name"] = Name,
            ["aliases"] = new JsonArray([.. Aliases.Select(a => (JsonNode)a)]),
            ["usage"] = Usage,
            ["description"] = Description,
            ["permission"] = Permission.ToString().ToLowerInvariant(),
            ["node"] = PermissionNode,
            ["owner"] = Owner,
            ["typed"] = IsTyped,
            ["usageLines"] = new JsonArray([.. (Tree is null ? UsageLines : CommandTree.Usage(Tree, visibleTo)).Select(u => (JsonNode)(Commands.Prefix + u))]),
        };
        if (Tree is not null) o["tree"] = Tree.ToJson(visibleTo);
        return o;
    }

    public override string ToString() => Syntax;
}

/// <summary>Thrown by a handler (or <see cref="CommandContext.Fail"/>) to end with an error message for the
/// source.</summary>
public sealed class CommandException(string message) : Exception(message);

public delegate void CommandHandler(CommandContext context);

/// <summary>One run of a command.</summary>
public sealed class CommandContext
{
    private readonly List<string> _output = new();
    private readonly IReadOnlyList<CommandLine.Token> _tokens;
    private readonly string _line;
    private Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

    internal CommandContext(CommandSource source, CommandInfo command, string label, string line,
        IReadOnlyList<CommandLine.Token> tokens)
    {
        Source = source;
        Command = command;
        Label = label;
        _line = line;
        _tokens = tokens;
        Args = tokens.Skip(1).Select(t => t.Text).ToArray();
    }

    public CommandSource Source { get; }
    public CommandInfo Command { get; }

    /// <summary>The name as typed (may be an alias).</summary>
    public string Label { get; }

    /// <summary>Arguments after the name, unquoted.</summary>
    public IReadOnlyList<string> Args { get; }

    public int Count => Args.Count;

    /// <summary>Everything after the name, as typed.</summary>
    public string ArgText => Rest(0);

    /// <summary>Lines sent back so far.</summary>
    public IReadOnlyList<string> Output => _output;

    /// <summary>Chat only: also pass the typed line on to the chat (by default a command line is not shown).</summary>
    public bool KeepLine { get; set; }

    /// <summary>Typed commands: the parsed arguments by name.</summary>
    public IReadOnlyDictionary<string, object?> Values => _values;

    /// <summary>Typed commands: the node whose handler runs.</summary>
    public CommandNode? Node { get; private set; }

    internal void Bind(CommandNode node, Dictionary<string, object?> values)
    {
        Node = node;
        _values = values;
    }

    /// <summary>Typed commands: whether argument <paramref name="name"/> was given.</summary>
    public bool Has(string name) => _values.ContainsKey(name);

    /// <summary>Typed commands: the value of argument <paramref name="name"/> (see <see cref="ArgumentType.ValueType"/>;
    /// a <see cref="Player"/> also converts to <see cref="ChatPlayer"/>, numbers to other number types).</summary>
    /// <exception cref="CommandException">the argument was not given</exception>
    public T Get<T>(string name) =>
        TryGet<T>(name, out var v) ? v : throw new CommandException($"missing <{name}>; usage: {Commands.Prefix}{string.Join($" | {Commands.Prefix}", Command.UsageLines.Take(3))}");

    public bool TryGet<T>(string name, out T value)
    {
        if (_values.TryGetValue(name, out var raw) && Convert(raw, typeof(T)) is T t)
        {
            value = t;
            return true;
        }
        value = default!;
        return false;
    }

    public T GetOrDefault<T>(string name, T fallback) => TryGet<T>(name, out var v) ? v : fallback;

    internal static object? Convert(object? value, Type to)
    {
        if (value is null || to.IsInstanceOfType(value)) return value;
        if (to == typeof(ChatPlayer) && value is global::Kiln.Player p)
            return Chat.FindPlayer(p.Entity) ?? new ChatPlayer(p.Entity, p.Name, p.Handle, 0, p.AccountId, (ChatPermissions)(uint)p.Permissions);
        if (value is IReadOnlyList<global::Kiln.Player> list)
        {
            if (to == typeof(global::Kiln.Player[])) return list.ToArray();
            if (to == typeof(List<global::Kiln.Player>)) return list.ToList();
            if (to == typeof(global::Kiln.Player) && list.Count == 1) return list[0];
        }
        if (value is IReadOnlyList<EntityId> ids)
        {
            if (to == typeof(EntityId[])) return ids.ToArray();
            if (to == typeof(List<EntityId>)) return ids.ToList();
            if (to == typeof(EntityId) && ids.Count == 1) return ids[0];
        }
        if (value is IConvertible && (to.IsPrimitive || to == typeof(decimal)))
            return System.Convert.ChangeType(value, to, CultureInfo.InvariantCulture);
        return value;
    }

    /// <summary>Argument <paramref name="index"/>; a usage error when missing.</summary>
    public string Arg(int index) => index < Args.Count ? Args[index] : throw Usage($"missing argument {index + 1}");

    public string? ArgOrDefault(int index, string? fallback = null) => index < Args.Count ? Args[index] : fallback;

    public int Int(int index)
    {
        var s = Arg(index);
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : throw Usage($"'{s}' is not a whole number");
    }

    public double Double(int index)
    {
        var s = Arg(index);
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? v
            : throw Usage($"'{s}' is not a number");
    }

    public bool Bool(int index)
    {
        var s = Arg(index).ToLowerInvariant();
        return s switch
        {
            "1" or "true" or "on" or "yes" => true,
            "0" or "false" or "off" or "no" => false,
            _ => throw Usage($"'{s}' is not on/off"),
        };
    }

    /// <summary>A connected player by entity id or (unique prefix of) name.</summary>
    public ChatPlayer Player(int index)
    {
        var s = Arg(index);
        if (uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && Chat.FindPlayer(new EntityId(id)) is { } byId)
            return byId;
        return Chat.FindPlayer(s) ?? throw new CommandException($"no connected player '{s}'");
    }

    /// <summary>The raw text from argument <paramref name="index"/> to the end, as typed (quotes kept).</summary>
    public string Rest(int index)
    {
        var t = index + 1;
        return t < _tokens.Count ? _line[_tokens[t].Start..].TrimEnd() : "";
    }

    /// <summary>Sends a line back to the source (a player gets it in chat).</summary>
    [TickThreadOnly]
    public void Reply(string text)
    {
        foreach (var line in Chat.Lines("", text))
        {
            _output.Add(line);
            if (Source.IsPlayer && !Source.Player.IsNone && Chat.Available)
            {
                try { Chat.Tell(Source.Player, line); }
                catch (InvalidOperationException) { /* the player left; the output is still recorded */ }
            }
        }
    }

    /// <summary>Ends the command with an error message: a refusal ("only a player in the world can do this") or a
    /// failure. The source is told, the log line says it, and the result is not ok; a reply alone counts as success.</summary>
    public void Fail(string message) => throw new CommandException(message);

    /// <summary>A <see cref="CommandException"/> with the usage line appended.</summary>
    public CommandException Usage(string? problem = null) =>
        new(problem is null ? $"usage: {Command.Syntax}" : $"{problem}; usage: {Command.Syntax}");

    /// <summary>An async handler's task that had not finished when the handler returned: <see cref="Commands.Execute"/>
    /// logs its outcome, and tells the source about a failure, when it ends.</summary>
    internal Task? Pending { get; private set; }

    /// <summary>An async handler's task. Finished already: its failure ends the command now; else it is <see cref="Pending"/>.</summary>
    internal void Observe(Task task)
    {
        if (!task.IsCompleted)
        {
            Pending = task;
            return;
        }
        if (task.IsCanceled) throw new CommandException($"{Commands.Prefix}{Command.Name} was canceled");
        if (task.Exception?.InnerException is { } done) ExceptionDispatchInfo.Capture(done).Throw();
    }
}

/// <summary>Outcome of <see cref="Commands.Execute"/>. An <see cref="IKilnResult"/>: <see cref="Ok"/> false with
/// <see cref="Reason"/> when the command did not run to its end.</summary>
public sealed class CommandResult : IKilnResult
{
    internal CommandResult(string name, bool found, bool denied, bool ok, string? error, IReadOnlyList<string> output,
        bool keepLine)
    {
        Name = name;
        Found = found;
        Denied = denied;
        Ok = ok;
        Error = error;
        Output = output;
        KeepLine = keepLine;
    }

    /// <summary>The command name as typed ("" for an empty line).</summary>
    public string Name { get; }

    /// <summary>A command with that name exists.</summary>
    public bool Found { get; }

    /// <summary>The source may not run it.</summary>
    public bool Denied { get; }

    /// <summary>It ran and finished without error.</summary>
    public bool Ok { get; }

    /// <summary>A <see cref="CommandExecutingEvent"/> handler canceled it.</summary>
    public bool Canceled { get; internal init; }

    /// <summary>The line did not match the command's arguments (typed commands).</summary>
    public bool ParseError { get; internal init; }

    public string? Error { get; }

    /// <summary>Why the command did not run to its end (<see cref="Error"/>: unknown, denied, canceled, a bad
    /// argument, or the handler's own failure); null when <see cref="Ok"/>. Since 0.2.0-dev.27.</summary>
    public string? Reason => Ok ? null : Error;

    public IReadOnlyList<string> Output { get; }
    internal bool KeepLine { get; }

    /// <summary>The handler's task was still running when this result was made (an async command): its final result,
    /// with every line it sent back and its error, when it ends.</summary>
    internal Task<CommandResult>? Completion { get; init; }

    /// <summary>Output lines, then the error, one per line.</summary>
    public string Text => string.Join("\n", Error is null ? Output : Output.Append(Error));

    public override string ToString() => $"{(Ok ? "ok" : "failed")}: {Text}";
}

/// <summary>Disposing it unregisters the command. For a command Kiln shares between mods (such as <c>/creature</c>)
/// it lets go of it instead: the command goes with the last registration.</summary>
public sealed class CommandRegistration : IDisposable
{
    private readonly Action? _release;

    internal CommandRegistration(CommandInfo info, Action? release = null)
    {
        Info = info;
        _release = release;
    }

    public CommandInfo Info { get; }

    public void Dispose()
    {
        if (_release is not null) _release();
        else Commands.Remove(Info);
    }
}

/// <summary>What a chat line naming an unknown command does.</summary>
public enum UnknownCommandPolicy
{
    /// <summary>Tell the player it is unknown; the line is not shown in chat.</summary>
    Reply = 0,
    /// <summary>Let the line through to the chat as ordinary text.</summary>
    PassThrough = 1,
}

/// <summary>
/// The command registry: "/name args" from chat, and <see cref="Execute"/> for the console, Kiln.Admin and the
/// control folder. Commands belong to the mod that registered them and disappear when it unloads.
/// </summary>
/// <remarks>
/// Two ways to declare a command: a handler that reads <see cref="CommandContext.Args"/> itself
/// (<see cref="Register(string, CommandHandler, CommandPermission, string?, string?, string[])"/>), or a typed tree
/// (Brigadier-style: <see cref="Literal"/>, <see cref="Argument"/>, <see cref="Register(LiteralNode, CommandPermission, string?, string[])"/>,
/// or <see cref="CommandAttribute"/> methods) that Kiln parses, explains in <c>/help</c> and completes. Every command
/// has a permission node (Kiln.Permissions) whose default level is its <see cref="CommandPermission"/>.
/// </remarks>
public static class Commands
{
    private sealed class Entry(CommandInfo info, CommandHandler? handler, ModContext? owner)
    {
        public CommandInfo Info { get; } = info;
        public CommandHandler? Handler { get; } = handler;
        public ModContext? Owner { get; } = owner;

        /// <summary>The registrations holding a shared Kiln command (<see cref="RegisterShared"/>); null for every
        /// other command.</summary>
        public List<SharedHold>? Holds { get; init; }

        /// <summary>Runs once a shared command is gone.</summary>
        public Action? Removed { get; init; }
    }

    private sealed class SharedHold(Entry entry, ModContext? mod)
    {
        public Entry Entry { get; } = entry;
        public ModContext? Mod { get; } = mod;
    }

    private static readonly object Lock = new();
    private static readonly Dictionary<string, Entry> ByName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<Entry> All = new();
    private static (Func<CommandSource, bool> Check, ModContext? Owner)? _adminCheck;

    static Commands()
    {
        ModCalls.OnRetire(mod =>
        {
            var gone = new List<Entry>();
            lock (Lock)
            {
                foreach (var e in All.Where(e => ReferenceEquals(e.Owner, mod)).ToList()) RemoveLocked(e);
                foreach (var e in All.Where(e => e.Holds is not null).ToList())
                    if (e.Holds!.RemoveAll(h => ReferenceEquals(h.Mod, mod)) > 0 && e.Holds.Count == 0)
                    {
                        RemoveLocked(e);
                        gone.Add(e);
                    }
                if (_adminCheck is { } c && ReferenceEquals(c.Owner, mod)) _adminCheck = null;
            }
            foreach (var e in gone) SharedGone(e);
        });
        var help = Literal("help").Alias("?").Describe("lists the commands you can use, or explains one")
            .Executes(HelpList)
            .Then(Argument("command", Arg.Word()).Executes(HelpOne)
                .Then(Argument("sub", Arg.Greedy()).Executes(HelpOne)));
        AddTyped(help, CommandPermission.Everyone, null, null);
    }

    /// <summary>What starts a command in chat. Default "/".</summary>
    public static string Prefix { get; set; } = "/";

    /// <summary>What a chat line naming an unknown command does.</summary>
    public static UnknownCommandPolicy UnknownCommands { get; set; } = UnknownCommandPolicy.Reply;

    /// <summary>Registers a command owned by the calling mod (removed when it unloads). Names are case-insensitive,
    /// made of letters, digits and <c>_ - . :</c>. Throws when the name or an alias is taken by another mod; the
    /// same mod registering a name again replaces its command.</summary>
    /// <param name="name">Name without the prefix, e.g. "time".</param>
    /// <param name="handler">Runs on the tick thread, in the registering mod's context.</param>
    /// <param name="permission">Who may run it (default level of its node <c>&lt;mod&gt;.command.&lt;name&gt;</c>).</param>
    /// <param name="usage">Arguments for help, e.g. "&lt;hours&gt;".</param>
    /// <param name="description">One line for help.</param>
    /// <param name="aliases">Other names.</param>
    public static CommandRegistration Register(string name, CommandHandler handler,
        CommandPermission permission = CommandPermission.Everyone, string? usage = null, string? description = null,
        params string[] aliases)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var owner = ModCalls.Caller;
        var names = new[] { name }.Concat(aliases ?? Array.Empty<string>()).Select(n => (n ?? "").Trim()).ToArray();
        foreach (var n in names)
            if (!ValidName(n)) throw new ArgumentException($"invalid command name '{n}'", nameof(name));
        var primary = names[0].ToLowerInvariant();
        var info = new CommandInfo(primary, names.Skip(1).Select(a => a.ToLowerInvariant()).Distinct().ToArray(),
            usage ?? "", description ?? "", permission, ModCalls.OwnerName(owner))
        {
            UsageLines = [(primary + " " + (usage ?? "")).TrimEnd()],
        };
        return Add(new Entry(info, handler, owner), names);
    }

    /// <summary>A command word: the root of a typed command (its name) or a subcommand.</summary>
    public static LiteralNode Literal(string name) => new(name);

    /// <summary>A typed argument node; its value is <c>context.Get&lt;T&gt;(name)</c>.</summary>
    public static ArgumentNode Argument(string name, ArgumentType type) => new(name, type);

    /// <summary>Registers a typed command: <paramref name="root"/> is its name (aliases: <see cref="LiteralNode.Alias"/>
    /// on the root, or <paramref name="aliases"/>). Kiln parses the line along the tree (subcommands first, then
    /// arguments in order), tells the source what is wrong, writes usage and completions. Owned by the calling mod.
    /// <see cref="LiteralNode.Requires"/> on the root sets the command's own node and level.</summary>
    public static CommandRegistration Register(LiteralNode root, CommandPermission permission = CommandPermission.Everyone,
        string? description = null, params string[] aliases)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (aliases is { Length: > 0 }) root.Alias(aliases);
        return AddTyped(root, permission, description, ModCalls.Caller);
    }

    /// <summary>Registers every <see cref="CommandAttribute"/> method of <paramref name="target"/> (a
    /// <see cref="Type"/>: its static methods). Kiln does this for each <see cref="Mod"/> before <c>OnLoad</c>.</summary>
    public static IReadOnlyList<CommandRegistration> RegisterAll(object target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return CommandMethods.RegisterAll(target);
    }

    /// <summary>Asks for a command that belongs to Kiln on behalf of the calling mod: a service's command tree (such as
    /// <c>/creature</c> or <c>/build</c>) that any number of mods may want. The first call adds it as Kiln's (owner
    /// "kiln", node <c>kiln.command.&lt;name&gt;</c>; <paramref name="build"/> makes the tree then), later calls share it.
    /// It stays while one of the returned registrations is held and goes when the last one is disposed or its mod
    /// unloads; <paramref name="removed"/> runs then. Handlers run as Kiln, whoever runs the line. Throws when the name or
    /// an alias belongs to another command.</summary>
    internal static CommandRegistration RegisterShared(string name, Func<LiteralNode> build, CommandPermission permission,
        string? description = null, Action? removed = null)
    {
        ArgumentNullException.ThrowIfNull(build);
        var caller = ModCalls.Caller;
        ModCalls.Watch(caller);
        lock (Lock)
            if (HoldLocked(name, caller) is { } held) return held;
        // The tree, its permission nodes and the command are Kiln's: none of them may go with the mod that asked first.
        ModContext.CurrentMod = null;
        try
        {
            var root = build();
            if (!string.Equals(root.Name, name, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"the tree of '{name}' is named '{root.Name}'", nameof(build));
            var entry = new Entry(TypedInfo(root, permission, description, ModCalls.OwnerName(null)), null, null)
            {
                Holds = [],
                Removed = removed,
            };
            CommandRegistration registration;
            lock (Lock)
            {
                if (HoldLocked(name, caller) is { } meanwhile) return meanwhile;
                foreach (var n in (string[])[root.Name, .. root.Aliases])
                    if (ByName.TryGetValue(n, out var other))
                        throw new InvalidOperationException($"command '{n}' is already registered by {other.Info.Owner}");
                AddLocked(entry);
                registration = HoldLocked(name, caller)!;
            }
            RegisterNode(entry.Info.PermissionNode, entry.Info.Permission, $"{Prefix}{entry.Info.Name}");
            Backend.Current.Log(0, LogLevel.Info, $"kiln.chat: {Prefix}{entry.Info.Name} added (Kiln's, node {entry.Info.PermissionNode}) for {ModCalls.OwnerName(caller)}");
            if (caller is not null) Chat.EnsureSubscribed();
            return registration;
        }
        finally
        {
            ModContext.CurrentMod = caller;
        }
    }

    // One more hold on the shared command called name; null when there is none. Under Lock.
    private static CommandRegistration? HoldLocked(string name, ModContext? mod)
    {
        if (!ByName.TryGetValue(name, out var e)) return null;
        if (e.Holds is null) throw new InvalidOperationException($"command '{name}' is already registered by {e.Info.Owner}");
        var hold = new SharedHold(e, mod);
        e.Holds.Add(hold);
        return new CommandRegistration(e.Info, () => Release(hold));
    }

    private static void Release(SharedHold hold)
    {
        var e = hold.Entry;
        lock (Lock)
        {
            if (!e.Holds!.Remove(hold) || e.Holds.Count > 0 || !All.Contains(e)) return;
            RemoveLocked(e);
        }
        SharedGone(e);
    }

    private static void SharedGone(Entry e)
    {
        Backend.Current.Log(0, LogLevel.Info, $"kiln.chat: {Prefix}{e.Info.Name} removed (no mod asks for it any more)");
        try
        {
            e.Removed?.Invoke();
        }
        catch (Exception ex)
        {
            Backend.Current.Log(0, LogLevel.Error, $"kiln.chat: cleanup of {Prefix}{e.Info.Name} threw: {ex}");
        }
    }

    private static CommandRegistration AddTyped(LiteralNode root, CommandPermission permission, string? description, ModContext? owner) =>
        Add(new Entry(TypedInfo(root, permission, description, ModCalls.OwnerName(owner)), null, owner), [root.Name, .. root.Aliases]);

    // A typed command's info; registers the permission nodes of its subcommand requirements.
    private static CommandInfo TypedInfo(LiteralNode root, CommandPermission permission, string? description, string ownerName)
    {
        var level = root.RequiredNode is not null ? root.RequiredLevel : permission;
        var node = root.RequiredNode is { Length: > 0 } explicitNode ? explicitNode : $"{ownerName}.command.{root.Name}";
        root.Freeze();
        // Subcommand requirements without an explicit node get <owner>.command.<path>.
        foreach (var (n, path) in CommandTree.Requirements(root, root.Name).ToList())
        {
            if (ReferenceEquals(n, root)) continue;
            if (n.RequiredNode is "") SetRequiredNode(n, $"{ownerName}.command.{path}");
            RegisterNode(n.RequiredNode!, n.RequiredLevel, $"{Prefix}{path.Replace('.', ' ')}");
        }
        var usage = CommandTree.Usage(root, null);
        var first = usage.Count > 0 ? usage[0] : root.Name;
        return new CommandInfo(root.Name, root.Aliases.ToArray(), first.Length > root.Name.Length ? first[(root.Name.Length + 1)..] : "",
            description ?? root.Description ?? "", level, ownerName)
        {
            PermissionNode = node,
            Tree = root,
            UsageLines = usage,
        };
    }

    private static void SetRequiredNode(CommandNode n, string node)
    {
        if (n is LiteralNode l) RequiresUnfrozen(l, node);
        else if (n is ArgumentNode a) RequiresUnfrozen(a, node);
    }

    private static void RequiresUnfrozen(CommandNode n, string node)
    {
        var frozen = n.Frozen;
        n.Frozen = false;
        if (n is LiteralNode l) l.Requires(n.RequiredLevel, node);
        else ((ArgumentNode)n).Requires(n.RequiredLevel, node);
        n.Frozen = frozen;
    }

    private static CommandRegistration Add(Entry entry, string[] names)
    {
        var owner = entry.Owner;
        lock (Lock)
        {
            foreach (var n in names)
                if (ByName.TryGetValue(n, out var other) && !ReferenceEquals(other.Owner, owner))
                    throw new InvalidOperationException($"command '{n}' is already registered by {other.Info.Owner}");
            foreach (var n in names)
                if (ByName.TryGetValue(n, out var mine)) RemoveLocked(mine);
            AddLocked(entry);
        }
        RegisterNode(entry.Info.PermissionNode, entry.Info.Permission, $"{Prefix}{entry.Info.Name}");
        ModCalls.Watch(owner);
        if (owner is not null) Chat.EnsureSubscribed();
        return new CommandRegistration(entry.Info);
    }

    /// <summary>Registers a node with Kiln.Permissions (as the calling mod); without Kiln.Permissions nothing happens.</summary>
    internal static void RegisterNode(string node, CommandPermission level, string description)
    {
        try
        {
            RegisterNodeCore(node, level, description);
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException)
        {
            // Kiln.Permissions is missing: commands fall back to their levels
        }
        catch (ArgumentException e)
        {
            Backend.Current.Log(0, LogLevel.Warn, $"kiln.chat: permission node '{node}': {e.Message}");
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RegisterNodeCore(string node, CommandPermission level, string description) =>
        Permissions.Register(node, (PermissionLevel)(int)level, description);

    /// <summary>Removes a command the calling mod registered (or any, when called outside mod code).</summary>
    public static bool Unregister(string name)
    {
        var caller = ModCalls.Caller;
        lock (Lock)
        {
            if (!ByName.TryGetValue(name, out var e)) return false;
            if (caller is not null && !ReferenceEquals(e.Owner, caller)) return false;
            if (e.Owner is null && e.Info.Owner == "kiln" && caller is not null) return false;
            RemoveLocked(e);
            return true;
        }
    }

    internal static void Remove(CommandInfo info)
    {
        lock (Lock)
        {
            var e = All.FirstOrDefault(x => ReferenceEquals(x.Info, info));
            if (e is not null) RemoveLocked(e);
        }
    }

    public static CommandInfo? Find(string name)
    {
        lock (Lock) return ByName.TryGetValue(name, out var e) ? e.Info : null;
    }

    /// <summary>Registered commands, sorted; with <paramref name="visibleTo"/>, only those it may run.</summary>
    public static IReadOnlyList<CommandInfo> List(CommandSource? visibleTo = null)
    {
        CommandInfo[] all;
        lock (Lock) all = All.Select(e => e.Info).ToArray();
        return all.Where(i => visibleTo is null || Allowed(visibleTo, i)).OrderBy(i => i.Name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Every command (visible to <paramref name="visibleTo"/>) as JSON: name, usage lines, node, tree.</summary>
    public static JsonArray Describe(CommandSource? visibleTo = null) => new([.. List(visibleTo).Select(i => (JsonNode)i.ToJson(visibleTo))]);

    /// <summary>Decides who is an admin for <see cref="CommandPermission.Admin"/> commands. The players service or
    /// Kiln.Admin sets this; the setting belongs to the calling mod and is dropped when it unloads. Without one, a
    /// player is admin when their user group may kick and ban.</summary>
    public static void SetAdminCheck(Func<CommandSource, bool>? check)
    {
        var owner = ModCalls.Caller;
        ModCalls.Watch(owner);
        lock (Lock) _adminCheck = check is null ? null : (check, owner);
    }

    /// <summary>Admin rights of a source: console always; remote as declared; players through the admin check
    /// (default: their user group may kick and ban) or a permissions.json group with <c>"admin": true</c>.</summary>
    public static bool IsAdmin(CommandSource source)
    {
        if (source.Kind != CommandSourceKind.Player || source.IsHostConsole) return source.DeclaredAdmin;
        var basic = BaseAdmin(source);
        if (basic == true) return true;
        try
        {
            return GroupAdmin(source, basic);
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException)
        {
            return false;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool GroupAdmin(CommandSource source, bool? basic) => Permissions.IsAdmin(source.SteamOnly
        ? PermissionSubject.ForSteamId(source.SteamId, source.Name)
        : PermissionSubject.ForPlayer(source.Player) with { AdminOverride = basic });

    /// <summary>The admin check (custom, else the user group's kick/ban right as the chat sees it), without
    /// permissions.json groups. Null when unknown (no custom check, the chat does not know the player): Kiln.Permissions
    /// then reads the right from Kiln.Players.</summary>
    internal static bool? BaseAdmin(CommandSource source)
    {
        if (source.Kind != CommandSourceKind.Player || source.IsHostConsole) return source.DeclaredAdmin;
        if (source.SteamOnly) return null;  // no live entity: permissions.json and the online record decide
        (Func<CommandSource, bool> Check, ModContext? Owner)? custom;
        lock (Lock) custom = _adminCheck;
        if (custom is { } c)
        {
            var result = false;
            if (ModCalls.Invoke(c.Owner, () => result = c.Check(source), "admin check")) return result;
            return false;
        }
        if (Chat.FindPlayer(source.Player) is { } p) return p.CanKickBan;
        return null;
    }

    /// <summary>Whether <paramref name="source"/> may run <paramref name="command"/>: the console always (also when it acts
    /// as a hosting game's player); remote tools by their declared rights; players by the command's permission node.</summary>
    public static bool Allowed(CommandSource source, CommandInfo command) =>
        Allowed(source, command.PermissionNode, command.Permission);

    /// <summary>Whether <paramref name="source"/> has <paramref name="node"/>, whose default level is
    /// <paramref name="level"/> (Kiln.Permissions decides for players; without it, the level does).</summary>
    internal static bool Allowed(CommandSource source, string node, CommandPermission level)
    {
        if (source.IsHostConsole) return true;
        switch (source.Kind)
        {
            case CommandSourceKind.Console:
                return true;
            case CommandSourceKind.Remote:
                return level == CommandPermission.Everyone || source.DeclaredAdmin;
        }
        try
        {
            return PlayerHas(source, node);
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException)
        {
            return level switch
            {
                CommandPermission.Everyone => true,
                CommandPermission.Admin => IsAdmin(source),
                _ => false,
            };
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool PlayerHas(CommandSource source, string node) => Permissions.Has(source.Subject, node);

    /// <summary>Runs a command line ("time set 7" or "/time set 7") as <paramref name="source"/>. Tick thread only.
    /// Never throws for a bad line or a failing handler: the result says what happened.</summary>
    [TickThreadOnly]
    public static CommandResult Execute(CommandSource source, string line)
    {
        ArgumentNullException.ThrowIfNull(source);
        Services.RequireTickThread("Commands.Execute");
        line = (line ?? "").Trim();
        if (Prefix.Length > 0 && line.StartsWith(Prefix, StringComparison.Ordinal)) line = line[Prefix.Length..];
        var tokens = CommandLine.Tokenize(line);
        if (tokens.Count == 0) return new CommandResult("", false, false, false, "empty command", Array.Empty<string>(), false);
        var label = tokens[0].Text;
        Entry? entry;
        lock (Lock) ByName.TryGetValue(label, out entry);
        if (entry is null)
            return new CommandResult(label, false, false, false, $"Unknown command '{Prefix}{label}'. Type {Prefix}help for a list.",
                Array.Empty<string>(), false);
        if (!Allowed(source, entry.Info))
            return new CommandResult(label, true, true, false, $"You may not use {Prefix}{entry.Info.Name}.", Array.Empty<string>(), false);

        var info = entry.Info;
        var ctx = new CommandContext(source, info, label, line, tokens);
        if (Events.HasSubscribers<CommandExecutingEvent>())
        {
            var ev = Events.Post(new CommandExecutingEvent(source, info, label, line, ctx.Args));
            if (ev.Canceled)
            {
                var canceled = new CommandResult(label, true, false, false,
                    ev.CancelMessage ?? $"{Prefix}{info.Name} was stopped{(ev.CanceledBy is { } by ? $" by {by}" : "")}.",
                    Array.Empty<string>(), false) { Canceled = true };
                Backend.Current.Log(entry.Owner?.Owner ?? 0, LogLevel.Info, $"command {Prefix}{info.Name} by {source}: canceled by {ev.CanceledBy ?? "?"}");
                return Executed(source, info, line, canceled, 0);
            }
        }
        var start = Stopwatch.GetTimestamp();
        CommandHandler? handler = entry.Handler;
        if (info.Tree is { } tree)
        {
            var parsed = CommandTree.Parse(tree, source, line, tokens);
            if (parsed.Node is null)
            {
                var usage = CommandTree.Usage(tree, source);
                var error = parsed.Error + (usage.Count > 0 && !parsed.Denied
                    ? $". Usage: {string.Join(" | ", usage.Take(3).Select(u => Prefix + u))}{(usage.Count > 3 ? $" (+{usage.Count - 3}, {Prefix}help {info.Name})" : "")}"
                    : "");
                var failed = new CommandResult(label, true, parsed.Denied, false, error, Array.Empty<string>(), false) { ParseError = !parsed.Denied };
                Backend.Current.Log(entry.Owner?.Owner ?? 0, LogLevel.Info, $"command {Prefix}{info.Name} by {source}: {error}");
                return Executed(source, info, line, failed, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            ctx.Bind(parsed.Node, parsed.Values);
            handler = parsed.Node.Handler;
        }
        string? err = null;
        var invoked = ModCalls.Invoke(entry.Owner, () =>
        {
            try { handler!(ctx); }
            catch (CommandException ce) { err = ce.Message; }
        }, $"command {Prefix}{info.Name}", asKiln: entry.Holds is not null);
        if (!invoked) err ??= $"{Prefix}{info.Name} failed (see the server log)";
        if (err is null && ctx.Pending is { } pending)
        {
            // an async handler still running: what it ends with is logged then, not "ok" now
            var running = new CommandResult(label, true, false, true, null, ctx.Output.ToArray(), ctx.KeepLine)
            {
                Completion = Finish(pending, ctx, entry.Owner?.Owner ?? 0, label),
            };
            return Executed(source, info, line, running, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        Logged(entry.Owner?.Owner ?? 0, info, source, err);
        var result = new CommandResult(label, true, false, err is null, err, ctx.Output.ToArray(), ctx.KeepLine);
        return Executed(source, info, line, result, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    private static void Logged(ulong owner, CommandInfo info, CommandSource source, string? err) =>
        Backend.Current.Log(owner, err is null ? LogLevel.Info : LogLevel.Warn, $"command {Prefix}{info.Name} by {source}: {err ?? "ok"}");

    // The end of an async handler: its outcome in the log line, a failure told to the source, and the final result.
    private static Task<CommandResult> Finish(Task pending, CommandContext ctx, ulong owner, string label) =>
        pending.ContinueWith(t =>
        {
            var name = ctx.Command.Name;
            string? err = null;
            if (t.IsCanceled) err = $"{Prefix}{name} was canceled";
            else if (t.Exception?.InnerException is { } e) err = e is CommandException ce ? ce.Message : $"{Prefix}{name} failed: {e.Message}";
            Logged(owner, ctx.Command, ctx.Source, err);
            if (err is not null && ctx.Source.IsPlayer && !ctx.Source.Player.IsNone && Chat.Available && Game.IsTickThread)
            {
                try { Chat.Tell(ctx.Source.Player, err); }
                catch (InvalidOperationException) { }
            }
            return new CommandResult(label, true, false, err is null, err, ctx.Output.ToArray(), ctx.KeepLine);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static CommandResult Executed(CommandSource source, CommandInfo info, string line, CommandResult result, double ms)
    {
        if (Events.HasSubscribers<CommandExecutedEvent>()) Events.Post(new CommandExecutedEvent(source, info, line, result, ms));
        return result;
    }

    /// <summary>Completions for a partly typed line ("/he", "/heal Al", "/time set "): command names, subcommands,
    /// player names, enum values, items... as <paramref name="source"/> may use them. Tick thread (looks up players
    /// and items).</summary>
    public static CommandCompletion Complete(CommandSource source, string text, int max = 50)
    {
        ArgumentNullException.ThrowIfNull(source);
        text ??= "";
        var offset = Prefix.Length > 0 && text.StartsWith(Prefix, StringComparison.Ordinal) ? Prefix.Length : 0;
        var line = text[offset..];
        var tokens = CommandLine.Tokenize(line);
        var trailingSpace = line.Length > 0 && char.IsWhiteSpace(line[^1]);
        var complete = trailingSpace ? tokens.Count : Math.Max(0, tokens.Count - 1);
        var partial = trailingSpace || tokens.Count == 0 ? "" : tokens[^1].Text;
        var start = offset + (trailingSpace || tokens.Count == 0 ? line.Length : tokens[^1].Start);
        if (complete == 0)
        {
            var names = new List<string>();
            foreach (var c in List(source))
                foreach (var n in new[] { c.Name }.Concat(c.Aliases))
                    if (n.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                        names.Add(n);
            return new CommandCompletion(start, names.Distinct().OrderBy(n => n, StringComparer.Ordinal).Take(max).ToArray());
        }
        Entry? entry;
        lock (Lock) ByName.TryGetValue(tokens[0].Text, out entry);
        if (entry?.Info.Tree is not { } tree || !Allowed(source, entry.Info)) return new CommandCompletion(start, []);
        var items = CommandTree.Complete(tree, source, line, tokens, complete, partial).Take(max).ToArray();
        return new CommandCompletion(start, items);
    }

    /// <summary>The text starts with the prefix followed by a name character.</summary>
    public static bool IsCommandLine(string text) =>
        Prefix.Length > 0 && text.Length > Prefix.Length && text.StartsWith(Prefix, StringComparison.Ordinal) &&
        !char.IsWhiteSpace(text[Prefix.Length]) && !text.AsSpan(Prefix.Length).StartsWith(Prefix);

    /// <summary>Chat entry: runs the command as the sender. Returns true when the line must not reach the chat.</summary>
    internal static bool RunFromChat(ChatMessage message) => RunFromChat(message, message.Text);

    /// <summary>Runs <paramref name="text"/> (a command line, possibly rewritten by a ChatMessageEvent handler) as
    /// the sender of <paramref name="message"/>.</summary>
    internal static bool RunFromChat(ChatMessage message, string text)
    {
        var name = CommandLine.Tokenize(text.StartsWith(Prefix, StringComparison.Ordinal) ? text[Prefix.Length..] : text).FirstOrDefault().Text ?? "";
        bool known;
        lock (Lock) known = ByName.ContainsKey(name);
        if (!known && UnknownCommands == UnknownCommandPolicy.PassThrough) return false;
        var source = message.Player is { Entity.IsNone: false } p
            ? CommandSource.ForPlayer(p.Entity, p.Name)
            : CommandSource.ForPlayer(EntityId.None, message.SenderName.Length > 0 ? message.SenderName : $"connection 0x{message.Connection:x}");
        var result = Execute(source, text);
        if (result.Error is not null && !source.Player.IsNone && Chat.Available)
        {
            try { Chat.Tell(source.Player, result.Error); }
            catch (InvalidOperationException) { }
        }
        return !result.KeepLine;
    }

    /// <summary>Splits a command line like a shell: spaces separate, "double" or 'single' quotes group, backslash
    /// escapes the next character.</summary>
    public static IReadOnlyList<string> Tokenize(string text) => CommandLine.Tokenize(text).Select(t => t.Text).ToArray();

    internal static bool ValidName(string name) =>
        name.Length is > 0 and <= 48 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':' or '?');

    private static void AddLocked(Entry e)
    {
        All.Add(e);
        ByName[e.Info.Name] = e;
        foreach (var a in e.Info.Aliases) ByName[a] = e;
    }

    private static void RemoveLocked(Entry e)
    {
        All.Remove(e);
        foreach (var key in ByName.Where(kv => ReferenceEquals(kv.Value, e)).Select(kv => kv.Key).ToList()) ByName.Remove(key);
    }

    /// <summary>Test hook: drop every command except the built-ins.</summary>
    internal static void ResetForTests()
    {
        lock (Lock)
        {
            foreach (var e in All.Where(e => e.Info.Owner != "kiln" || e.Owner is not null || e.Holds is not null).ToList()) RemoveLocked(e);
            _adminCheck = null;
        }
    }

    private static void HelpList(CommandContext ctx)
    {
        var list = List(ctx.Source);
        var sb = new StringBuilder("Commands:");
        var lines = new List<string>();
        foreach (var c in list)
        {
            var item = " " + Prefix + c.Name;
            if (sb.Length + item.Length > 200)
            {
                lines.Add(sb.ToString());
                sb.Clear();
            }
            sb.Append(item);
        }
        lines.Add(sb.ToString());
        lines.Add($"{Prefix}help <command> explains one.");
        foreach (var l in lines) ctx.Reply(l);
    }

    private static void HelpOne(CommandContext ctx)
    {
        var name = ctx.Get<string>("command").TrimStart(Prefix.ToCharArray());
        var info = Find(name);
        if (info is null || !Allowed(ctx.Source, info)) ctx.Fail($"no command '{Prefix}{name}'");
        var head = $"{Prefix}{info!.Name}{(info.Description.Length > 0 ? " - " + info.Description : "")}" +
                   (info.Aliases.Count > 0 ? $" (also {string.Join(", ", info.Aliases.Select(a => Prefix + a))})" : "");
        ctx.Reply(head);
        if (info.Tree is { } tree)
        {
            IReadOnlyList<string> usage = CommandTree.Usage(tree, ctx.Source);
            if (ctx.TryGet<string>("sub", out var sub) && sub.Length > 0)
            {
                var prefix = info.Name + " " + string.Join(" ", Tokenize(sub));
                usage = usage.Where(u => u.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (usage.Count == 0) ctx.Fail($"{Prefix}{info.Name} has no '{sub}'");
            }
            foreach (var u in usage.Take(12)) ctx.Reply("  " + Prefix + u);
            if (usage.Count > 12) ctx.Reply($"  ... {usage.Count - 12} more");
            var args = CommandTree.Arguments(tree);
            if (args.Count > 0) ctx.Reply("  " + string.Join("; ", args));
        }
        else ctx.Reply("  " + info.Syntax);
        if (ctx.Source.Kind != CommandSourceKind.Player || ctx.Source.IsAdmin)
            ctx.Reply($"  permission: {info.PermissionNode} (default: {info.Permission.ToString().ToLowerInvariant()})");
    }
}

/// <summary>Command-line tokenizer (kept separate so tests can reach it without a server).</summary>
internal static class CommandLine
{
    internal readonly record struct Token(string Text, int Start);

    public static IReadOnlyList<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var sb = new StringBuilder();
        var i = 0;
        var n = text?.Length ?? 0;
        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(text![i])) i++;
            if (i >= n) break;
            var start = i;
            sb.Clear();
            char quote = '\0';
            var brackets = 0;  // inside a selector's [...] (@e[type=x, limit=2]): whitespace does not split
            while (i < n)
            {
                var c = text![i];
                if (quote != '\0')
                {
                    if (c == quote) { quote = '\0'; i++; continue; }
                    if (c == '\\' && i + 1 < n && (text[i + 1] == quote || text[i + 1] == '\\')) { sb.Append(text[i + 1]); i += 2; continue; }
                    sb.Append(c);
                    i++;
                    continue;
                }
                if (c == '[' && sb.Length == 2 && sb[0] == '@') brackets = 1;
                else if (brackets > 0 && c == ']') brackets = 0;
                if (char.IsWhiteSpace(c))
                {
                    if (brackets == 0) break;
                    i++;
                    continue;  // dropped: "@e[type=x, limit=2]" reads as "@e[type=x,limit=2]"
                }
                if (c is '"' or '\'') { quote = c; i++; continue; }
                if (c == '\\' && i + 1 < n) { sb.Append(text[i + 1]); i += 2; continue; }
                sb.Append(c);
                i++;
            }
            tokens.Add(new Token(sb.ToString(), start));
        }
        return tokens;
    }
}
