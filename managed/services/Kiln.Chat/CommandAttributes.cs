// SPDX-License-Identifier: MIT
// Commands declared as methods: [Command("heal")] void Heal(CommandContext c, Player target, [Range(1, 1000)] int amount = 100).
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Kiln;

/// <summary>
/// Declares a method as a command (or a subcommand: <c>[Command("zone create")]</c>). Kiln registers the
/// <c>[Command]</c> methods of a <see cref="Mod"/> before its <c>OnLoad</c>; for other objects call
/// <see cref="Commands.RegisterAll"/>. Methods with the same first word form one command.
/// </summary>
/// <remarks>
/// <para>Parameters: optionally a <see cref="CommandContext"/> first, then the arguments in order. Types: int, long,
/// double, float, bool, string (one word or quoted; <see cref="GreedyAttribute"/>: the rest of the line;
/// <see cref="ChoiceAttribute"/>: one of a list), enums, <see cref="Player"/> or <see cref="ChatPlayer"/> (one
/// player), <c>IReadOnlyList&lt;Player&gt;</c> / <c>Player[]</c> (also <c>@a</c>), <see cref="EntityId"/>,
/// <c>IReadOnlyList&lt;EntityId&gt;</c> / <c>EntityId[]</c> (selectors such as <c>@e[type=creature,distance=..30]</c>),
/// <see cref="WorldPosition"/>, <see cref="ItemInfo"/>, <see cref="TimeSpan"/>, and double with
/// <see cref="TimeOfDayAttribute"/>. <see cref="RangeAttribute"/> limits numbers (durations: seconds). Parameters with
/// a default value are optional.</para>
/// <para>Return void, a string (sent back as a reply) or a Task (its failure is reported to the source).</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class CommandAttribute(string path) : Attribute
{
    /// <summary>"name" or "name sub ...".</summary>
    public string Path { get; } = path;

    public string? Description { get; init; }

    /// <summary>Who may run it by default.</summary>
    public CommandPermission Permission { get; init; } = CommandPermission.Everyone;

    /// <summary>The permission node (default <c>&lt;mod&gt;.command.&lt;name&gt;[.&lt;sub&gt;]</c>).</summary>
    public string? Node { get; init; }

    /// <summary>Other names of the command (on the first word) or of the subcommand (last word).</summary>
    public string[] Aliases { get; init; } = [];
}

/// <summary>A string parameter that takes the rest of the line.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class GreedyAttribute : Attribute;

/// <summary>A string parameter that is one word (no quotes).</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class WordAttribute : Attribute;

/// <summary>A double parameter that is a time of day in hours ("7:30", "7pm", "noon").</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class TimeOfDayAttribute : Attribute;

/// <summary>A string parameter that is one of <see cref="Options"/>.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class ChoiceAttribute(params string[] options) : Attribute
{
    public string[] Options { get; } = options;
}

internal static class CommandMethods
{
    private sealed record Method(MethodInfo Info, CommandAttribute Attr, object? Target, string[] Words);

    /// <summary>Builds and registers the trees of <paramref name="target"/>'s [Command] methods (instance and static;
    /// a <see cref="Type"/> for static only).</summary>
    public static IReadOnlyList<CommandRegistration> RegisterAll(object target)
    {
        var staticOnly = target is Type;
        var type = target as Type ?? target.GetType();
        var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly |
                    (staticOnly ? 0 : BindingFlags.Instance);
        var methods = new List<Method>();
        for (var t = type; t != null && t != typeof(object) && t != typeof(Mod); t = t.BaseType)
            foreach (var m in t.GetMethods(flags))
                foreach (var a in m.GetCustomAttributes<CommandAttribute>())
                {
                    var words = a.Path.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()).ToArray();
                    if (words.Length == 0) throw new ArgumentException($"[Command] on {t.Name}.{m.Name} has no name");
                    methods.Add(new Method(m, a, m.IsStatic ? null : target, words));
                }
        var result = new List<CommandRegistration>();
        foreach (var group in methods.GroupBy(m => m.Words[0]))
        {
            var list = group.ToList();
            var root = Commands.Literal(group.Key);
            var rootLevel = list.Min(m => m.Attr.Permission);
            string? rootNode = null;
            string? description = null;
            foreach (var m in list.OrderBy(m => m.Words.Length))
            {
                if (m.Words.Length == 1)
                {
                    description ??= m.Attr.Description;
                    rootNode ??= m.Attr.Node;
                    root.Alias(m.Attr.Aliases);
                }
                Add(root, m, rootLevel);
            }
            if (rootNode is not null) root.Requires(rootLevel, rootNode);
            if (description is not null) root.Describe(description);
            result.Add(Commands.Register(root, rootLevel, description));
        }
        return result;
    }

    private static void Add(LiteralNode root, Method m, CommandPermission rootLevel)
    {
        CommandNode node = root;
        for (var i = 1; i < m.Words.Length; i++)
        {
            var existing = node.ChildList.OfType<LiteralNode>().FirstOrDefault(l => l.Matches(m.Words[i]));
            if (existing is null)
            {
                existing = Commands.Literal(m.Words[i]);
                Then(node, existing);
            }
            node = existing;
        }
        var level = m.Attr.Permission;
        if (m.Words.Length > 1)
        {
            var leaf = (LiteralNode)node;
            if (m.Attr.Description is { } d && leaf.Description is null) leaf.Describe(d);
            leaf.Alias(m.Attr.Aliases);
            if (level > rootLevel || m.Attr.Node is not null) leaf.Requires(level, m.Attr.Node);
        }
        var ps = m.Info.GetParameters();
        var first = ps.Length > 0 && ps[0].ParameterType == typeof(CommandContext) ? 1 : 0;
        // A root-level method stricter than its siblings (e.g. "zone" admin, "zone list" everyone) checks its own node.
        string? extra = null;
        if (m.Words.Length == 1 && level > rootLevel)
        {
            extra = m.Attr.Node ?? $"{ModCalls.OwnerName(ModCalls.Caller)}.command.{root.Name}.run";
            Commands.RegisterNode(extra, level, $"run {Commands.Prefix}{root.Name} itself");
        }
        var handler = MakeHandler(m, ps, first, extra, level, root.Name);
        // Optional parameters: the node before the first optional one already executes (with the defaults).
        for (var i = first; i < ps.Length; i++)
        {
            var p = ps[i];
            if (p.HasDefaultValue || p.IsOptional) Executes(node, handler);
            var arg = Commands.Argument(p.Name ?? $"arg{i}", TypeOf(m, p));
            if (p.GetCustomAttribute<CommentAttribute>() is { } c) arg.Describe(c.Text);
            Then(node, arg);
            node = arg;
        }
        Executes(node, handler);
    }

    private static void Then(CommandNode parent, CommandNode child)
    {
        if (parent is LiteralNode l) l.Then(child);
        else ((ArgumentNode)parent).Then(child);
    }

    private static void Executes(CommandNode node, CommandHandler handler)
    {
        if (node.Handler is not null) return;  // two methods end at the same node: the first wins
        if (node is LiteralNode l) l.Executes(handler);
        else ((ArgumentNode)node).Executes(handler);
    }

    private static ArgumentType TypeOf(Method m, ParameterInfo p)
    {
        var t = p.ParameterType;
        var (min, max) = ConfigSchema.RangeOf(p.GetCustomAttributes());
        string Where() => $"{m.Info.DeclaringType?.Name}.{m.Info.Name}({p.Name})";
        if (t == typeof(string))
        {
            if (p.GetCustomAttribute<ChoiceAttribute>() is { } ch) return Arg.Choice(ch.Options);
            if (p.GetCustomAttribute<GreedyAttribute>() is not null) return Arg.Greedy();
            if (p.GetCustomAttribute<WordAttribute>() is not null) return Arg.Word();
            return Arg.String();
        }
        if (t == typeof(int) || t == typeof(long) || t == typeof(short))
            return Arg.Int(min is { } a ? (int)Math.Ceiling(Math.Max(a, int.MinValue)) : int.MinValue,
                max is { } b ? (int)Math.Floor(Math.Min(b, int.MaxValue)) : int.MaxValue);
        if (t == typeof(double) && p.GetCustomAttribute<TimeOfDayAttribute>() is not null) return Arg.TimeOfDay();
        if (t == typeof(double) || t == typeof(float)) return Arg.Double(min ?? double.MinValue, max ?? double.MaxValue);
        if (t == typeof(bool)) return Arg.Bool();
        if (t.IsEnum) return Arg.Enum(t);
        if (t == typeof(Player) || t == typeof(ChatPlayer)) return Arg.Player();
        if (t == typeof(IReadOnlyList<Player>) || t == typeof(Player[]) || t == typeof(IEnumerable<Player>) || t == typeof(List<Player>))
            return Arg.Players();
        if (t == typeof(EntityId)) return Arg.Entity();
        if (t == typeof(IReadOnlyList<EntityId>) || t == typeof(EntityId[]) || t == typeof(IEnumerable<EntityId>) || t == typeof(List<EntityId>))
            return Arg.Entities();
        if (t == typeof(WorldPosition)) return Arg.Position();
        if (t == typeof(ItemInfo)) return Arg.Item();
        if (t == typeof(TimeSpan))
            return Arg.Duration(min is { } a ? TimeSpan.FromSeconds(a) : null, max is { } b ? TimeSpan.FromSeconds(b) : null);
        throw new ArgumentException($"[Command] {Where()}: parameters of type {t.Name} are not supported");
    }

    private static CommandHandler MakeHandler(Method m, ParameterInfo[] ps, int first, string? extraNode, CommandPermission level,
        string root)
    {
        return ctx =>
        {
            if (extraNode is not null && !Commands.Allowed(ctx.Source, extraNode, level))
                throw new CommandException($"You may not use {Commands.Prefix}{root}.");
            var args = new object?[ps.Length];
            if (first == 1) args[0] = ctx;
            for (var i = first; i < ps.Length; i++)
            {
                var p = ps[i];
                args[i] = ctx.Values.TryGetValue(p.Name ?? $"arg{i}", out var v)
                    ? CommandContext.Convert(v, p.ParameterType)
                    : p.HasDefaultValue ? p.DefaultValue : null;
            }
            object? result;
            try
            {
                result = m.Info.Invoke(m.Target, args);
            }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
            switch (result)
            {
                case string s:
                    ctx.Reply(s);
                    break;
                case Task t:
                    ctx.Observe(t);
                    break;
            }
        };
    }
}
