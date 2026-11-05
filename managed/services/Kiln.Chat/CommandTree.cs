// SPDX-License-Identifier: MIT
// Typed commands as a tree of literal and argument nodes (Brigadier's CommandNode / ArgumentBuilder): parsing with
// backtracking and clear errors, usage lines, completion.
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>A node of a typed command: a <see cref="LiteralNode"/> (a fixed word, the command name or a subcommand)
/// or an <see cref="ArgumentNode"/> (a typed value). Build with <see cref="Commands.Literal"/> and
/// <see cref="Commands.Argument"/>; register the root with <see cref="Commands.Register(LiteralNode, CommandPermission, string?, string[])"/>.</summary>
public abstract class CommandNode
{
    private protected CommandNode(string name) => Name = name;

    public string Name { get; }

    /// <summary>One line for help.</summary>
    public string? Description { get; private protected set; }

    internal readonly List<CommandNode> ChildList = new();
    public IReadOnlyList<CommandNode> Children => ChildList;

    internal CommandHandler? Handler;

    /// <summary>A command line may end here.</summary>
    public bool Executable => Handler is not null;

    /// <summary>Extra permission needed to go through this node (a subcommand), or null.</summary>
    public string? RequiredNode { get; private protected set; }

    /// <summary>Default level of <see cref="RequiredNode"/>.</summary>
    public CommandPermission RequiredLevel { get; private protected set; }

    internal bool Frozen;

    private protected void AddChild(CommandNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (Frozen) throw new InvalidOperationException("the command is registered already");
        if (child is LiteralNode l && ChildList.OfType<LiteralNode>().Any(x => x.Matches(l.Name) || l.Aliases.Any(x.Matches)))
            throw new ArgumentException($"'{Name}' already has a subcommand '{l.Name}'");
        if (child is ArgumentNode a && ChildList.OfType<ArgumentNode>().Any(x => string.Equals(x.Name, a.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"'{Name}' already has an argument '{a.Name}'");
        ChildList.Add(child);
    }

    private protected void SetHandler(CommandHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (Frozen) throw new InvalidOperationException("the command is registered already");
        Handler = handler;
    }

    private protected void SetRequirement(CommandPermission level, string? node)
    {
        if (Frozen) throw new InvalidOperationException("the command is registered already");
        RequiredLevel = level;
        RequiredNode = node ?? "";  // "" = derive from the path at registration
    }

    internal void Freeze()
    {
        Frozen = true;
        foreach (var c in ChildList) c.Freeze();
    }

    /// <summary>"name" for a literal, "&lt;name&gt;" for an argument.</summary>
    public abstract string Display { get; }

    internal virtual JsonObject ToJson(CommandSource? visibleTo)
    {
        var o = new JsonObject
        {
            ["kind"] = this is LiteralNode ? "literal" : "argument",
            ["name"] = Name,
            ["executable"] = Executable,
        };
        if (Description is { Length: > 0 }) o["description"] = Description;
        if (RequiredNode is { Length: > 0 }) o["permission"] = RequiredNode;
        var children = new JsonArray();
        foreach (var c in ChildList)
            if (visibleTo is null || CommandTree.Permitted(visibleTo, c))
                children.Add(c.ToJson(visibleTo));
        if (children.Count > 0) o["children"] = children;
        return o;
    }
}

/// <summary>A fixed word: the command name (root) or a subcommand.</summary>
public sealed class LiteralNode : CommandNode
{
    private readonly List<string> _aliases = new();

    internal LiteralNode(string name) : base(name.Trim().ToLowerInvariant())
    {
        if (!Commands.ValidName(Name)) throw new ArgumentException($"invalid command word '{name}'", nameof(name));
    }

    public IReadOnlyList<string> Aliases => _aliases;
    public override string Display => Name;

    internal bool Matches(string token) =>
        string.Equals(token, Name, StringComparison.OrdinalIgnoreCase) || _aliases.Any(a => string.Equals(a, token, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds a child node (subcommand or argument).</summary>
    public LiteralNode Then(CommandNode child)
    {
        AddChild(child);
        return this;
    }

    /// <summary>Runs <paramref name="handler"/> when the line ends here.</summary>
    public LiteralNode Executes(CommandHandler handler)
    {
        SetHandler(handler);
        return this;
    }

    public LiteralNode Describe(string description)
    {
        Description = description;
        return this;
    }

    /// <summary>Other words for this subcommand (on the root: the command's aliases).</summary>
    public LiteralNode Alias(params string[] names)
    {
        foreach (var n in names)
        {
            var a = n.Trim().ToLowerInvariant();
            if (!Commands.ValidName(a)) throw new ArgumentException($"invalid alias '{n}'");
            if (!_aliases.Contains(a) && a != Name) _aliases.Add(a);
        }
        return this;
    }

    /// <summary>Going through this node needs <paramref name="node"/> (default
    /// <c>&lt;mod&gt;.command.&lt;path&gt;</c>), which <paramref name="level"/> has by default.</summary>
    public LiteralNode Requires(CommandPermission level, string? node = null)
    {
        SetRequirement(level, node);
        return this;
    }

    internal override JsonObject ToJson(CommandSource? visibleTo)
    {
        var o = base.ToJson(visibleTo);
        if (_aliases.Count > 0) o["aliases"] = new JsonArray([.. _aliases.Select(a => (JsonNode)a)]);
        return o;
    }
}

/// <summary>A typed value; its parsed value is in <see cref="CommandContext.Get{T}"/> under <see cref="CommandNode.Name"/>.</summary>
public sealed class ArgumentNode : CommandNode
{
    internal ArgumentNode(string name, ArgumentType type) : base(name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsWhiteSpace)) throw new ArgumentException($"invalid argument name '{name}'", nameof(name));
        Type = type ?? throw new ArgumentNullException(nameof(type));
    }

    public ArgumentType Type { get; }
    public override string Display => $"<{Name}>";

    public ArgumentNode Then(CommandNode child)
    {
        AddChild(child);
        return this;
    }

    public ArgumentNode Executes(CommandHandler handler)
    {
        SetHandler(handler);
        return this;
    }

    public ArgumentNode Describe(string description)
    {
        Description = description;
        return this;
    }

    public ArgumentNode Requires(CommandPermission level, string? node = null)
    {
        SetRequirement(level, node);
        return this;
    }

    internal override JsonObject ToJson(CommandSource? visibleTo)
    {
        var o = base.ToJson(visibleTo);
        foreach (var (k, v) in Type.ToJson()) o[k] = v?.DeepClone();
        return o;
    }
}

/// <summary>Completion of a partly typed line: replace the text from <see cref="Start"/> with one of
/// <see cref="Suggestions"/>.</summary>
public sealed record CommandCompletion(int Start, IReadOnlyList<string> Suggestions);

internal static class CommandTree
{
    internal sealed class ParseResult
    {
        public CommandNode? Node;
        public Dictionary<string, object?> Values = new(StringComparer.OrdinalIgnoreCase);
        public string? Error;
        public bool Denied;
        public CommandNode? ErrorNode;
    }

    private sealed class State(CommandSource source, string line, IReadOnlyList<CommandLine.Token> tokens)
    {
        public readonly CommandSource Source = source;
        public readonly string Line = line;
        public readonly IReadOnlyList<CommandLine.Token> Tokens = tokens;
        public int ErrorDepth = -1, ErrorWeight = -1;
        public string? Error;
        public bool Denied;
        public CommandNode? ErrorNode;

        public void Fail(int depth, int weight, string error, CommandNode node, bool denied = false)
        {
            if (depth < ErrorDepth || depth == ErrorDepth && weight <= ErrorWeight) return;
            ErrorDepth = depth;
            ErrorWeight = weight;
            Error = error;
            Denied = denied;
            ErrorNode = node;
        }
    }

    public static bool Permitted(CommandSource source, CommandNode node) =>
        node.RequiredNode is not { Length: > 0 } n || Commands.Allowed(source, n, node.RequiredLevel);

    /// <summary>Parses the tokens after the command name (token 0).</summary>
    public static ParseResult Parse(LiteralNode root, CommandSource source, string line, IReadOnlyList<CommandLine.Token> tokens)
    {
        var st = new State(source, line, tokens);
        var found = Walk(st, root, 1, []);
        if (found is { } f)
        {
            var r = new ParseResult { Node = f.Node };
            foreach (var (k, v) in f.Values) r.Values[k] = v;
            return r;
        }
        return new ParseResult { Error = st.Error ?? "invalid command", Denied = st.Denied, ErrorNode = st.ErrorNode };
    }

    private static (CommandNode Node, List<(string, object?)> Values)? Walk(State st, CommandNode node, int i, List<(string, object?)> values)
    {
        if (i >= st.Tokens.Count)
        {
            if (node.Executable) return (node, values);
            st.Fail(i, 0, $"incomplete command, expected {Expected(st.Source, node)}", node);
            return null;
        }
        var token = st.Tokens[i].Text;
        foreach (var child in node.ChildList.OfType<LiteralNode>())
        {
            if (!child.Matches(token)) continue;
            if (!Permitted(st.Source, child))
            {
                st.Fail(i + 1, 2, $"You may not use {Commands.Prefix}{PathTo(st, i)} {child.Name}.", child, denied: true);
                continue;
            }
            if (Walk(st, child, i + 1, values) is { } r) return r;
        }
        foreach (var child in node.ChildList.OfType<ArgumentNode>())
        {
            if (!Permitted(st.Source, child))
            {
                st.Fail(i, 1, $"You may not use {Commands.Prefix}{PathTo(st, i)} {child.Display}.", child, denied: true);
                continue;
            }
            var reader = new ArgReader(st.Source, st.Line, st.Tokens, i);
            object? value;
            string error;
            bool ok;
            try
            {
                ok = child.Type.TryParse(reader, out value, out error);
            }
            catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException)
            {
                (ok, value, error) = (false, null, $"{child.Type.Name} arguments are not available on this server");
            }
            if (!ok)
            {
                st.Fail(i, 1, $"{error} ({child.Display})", child);
                continue;
            }
            var consumed = child.Type.Tokens == 0 ? st.Tokens.Count - i : child.Type.Tokens;
            if (Walk(st, child, i + consumed, [.. values, (child.Name, value)]) is { } r) return r;
        }
        if (node.ChildList.Count == 0 || node.Executable && !node.ChildList.Any(c => c is LiteralNode l ? l.Matches(token) : true))
            st.Fail(i, 0, $"too many arguments: '{Rest(st, i)}'", node);
        else
            st.Fail(i, 0, $"unknown '{token}', expected {Expected(st.Source, node)}", node);
        return null;
    }

    private static string Rest(State st, int i)
    {
        var s = st.Line[st.Tokens[i].Start..].TrimEnd();
        return s.Length > 40 ? s[..40] + "..." : s;
    }

    private static string PathTo(State st, int i) => string.Join(" ", st.Tokens.Take(i).Select(t => t.Text));

    private static string Expected(CommandSource source, CommandNode node)
    {
        var options = node.ChildList.Where(c => Permitted(source, c)).Select(c => c.Display).ToList();
        return options.Count switch
        {
            0 => "nothing more",
            1 => options[0],
            _ => string.Join(" | ", options.Take(8)) + (options.Count > 8 ? " | ..." : ""),
        };
    }

    // ---------------------------------------------------------------------------------------- usage ----------

    /// <summary>One line per way to run the command (without the prefix), visible to <paramref name="source"/>;
    /// trailing optional arguments are folded: "heal &lt;target&gt; [amount]".</summary>
    public static IReadOnlyList<string> Usage(LiteralNode root, CommandSource? source)
    {
        var raw = new List<(string Line, CommandNode Node)>();
        Collect(root, root.Name, source, raw);
        var lines = new List<string>();
        var i = 0;
        while (i < raw.Count)
        {
            var line = raw[i].Line;
            var basis = line;
            var j = i + 1;
            while (j < raw.Count && raw[j].Node is ArgumentNode a && raw[j].Line == basis + " " + a.Display)
            {
                line += $" [{a.Name}]";
                basis = raw[j].Line;
                j++;
            }
            lines.Add(line);
            i = j;
        }
        return lines;
    }

    private static void Collect(CommandNode node, string line, CommandSource? source, List<(string, CommandNode)> into)
    {
        if (node.Executable) into.Add((line, node));
        foreach (var c in node.ChildList)
        {
            if (source is not null && !Permitted(source, c)) continue;
            Collect(c, line + " " + c.Display, source, into);
        }
    }

    /// <summary>"&lt;amount&gt;: whole number 1..1000" for every argument (help detail).</summary>
    public static IReadOnlyList<string> Arguments(LiteralNode root)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        void Visit(CommandNode n)
        {
            if (n is ArgumentNode a && seen.Add(a.Name)) list.Add($"<{a.Name}>: {a.Type.Describe()}{(a.Description is { Length: > 0 } d ? " - " + d : "")}");
            foreach (var c in n.ChildList) Visit(c);
        }
        Visit(root);
        return list;
    }

    /// <summary>Every requirement node of the tree, with its path ("zone.create").</summary>
    public static IEnumerable<(CommandNode Node, string Path)> Requirements(CommandNode node, string path)
    {
        if (node.RequiredNode is not null) yield return (node, path);
        foreach (var c in node.ChildList)
            foreach (var r in Requirements(c, c is LiteralNode ? path + "." + c.Name : path))
                yield return r;
    }

    // ---------------------------------------------------------------------------------------- completion -----

    /// <summary>Suggestions for the token being typed after <paramref name="complete"/> whole tokens (token 0 is the
    /// command name).</summary>
    public static IEnumerable<string> Complete(LiteralNode root, CommandSource source, string line, IReadOnlyList<CommandLine.Token> tokens,
        int complete, string partial)
    {
        var reached = new List<CommandNode>();
        Reach(root, 1, complete, source, line, tokens, reached, 0);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in reached)
            foreach (var child in node.ChildList)
            {
                if (!Permitted(source, child)) continue;
                IEnumerable<string> items;
                if (child is LiteralNode l) items = new[] { l.Name }.Concat(l.Aliases).Where(n => n.StartsWith(partial, StringComparison.OrdinalIgnoreCase));
                else
                {
                    var a = (ArgumentNode)child;
                    try
                    {
                        items = a.Type.Suggest(partial, source).ToList();
                    }
                    catch (Exception)
                    {
                        items = [];
                    }
                    if (partial.Length == 0 && !items.Any()) items = [a.Display];
                }
                foreach (var s in items)
                    if (seen.Add(s))
                        yield return s;
            }
    }

    private static void Reach(CommandNode node, int i, int complete, CommandSource source, string line,
        IReadOnlyList<CommandLine.Token> tokens, List<CommandNode> into, int depth)
    {
        if (depth > 32) return;
        if (i == complete)
        {
            into.Add(node);
            return;
        }
        if (i > complete) return;
        foreach (var child in node.ChildList)
        {
            if (!Permitted(source, child)) continue;
            if (child is LiteralNode l)
            {
                if (l.Matches(tokens[i].Text)) Reach(child, i + 1, complete, source, line, tokens, into, depth + 1);
                continue;
            }
            var a = (ArgumentNode)child;
            if (a.Type.Tokens == 0) continue;  // greedy text: nothing to complete after it
            if (i + a.Type.Tokens > complete) continue;
            try
            {
                if (a.Type.TryParse(new ArgReader(source, line, tokens, i), out _, out _))
                    Reach(child, i + a.Type.Tokens, complete, source, line, tokens, into, depth + 1);
            }
            catch (Exception)
            {
                // a lookup failed: no completion along this path
            }
        }
    }
}
