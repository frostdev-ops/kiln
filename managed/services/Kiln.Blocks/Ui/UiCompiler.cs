// SPDX-License-Identifier: MIT
// UI blocks -> what the player's game draws. Nothing goes into the game's data: a window, a HUD line and a key binding are Kiln.Ui objects, made on a client when
// the block is committed (UiRuntime) and checked everywhere. This file is the checking: every field, every name a field takes (a value, a window, an ability, an
// effect, a key), by block and field, when the block compiles. What it makes is a plan, plain objects with the values resolved, that UiRuntime draws from.
//   WindowBlock      WindowPlan: rows with their bindings, conditions, texts and actions     -> Ui.Window or Ui.Screen, a pause/main menu entry
//   HudLineBlock     HudPlan: a template, a condition, a rate                               -> Hud.Show / Hud.Remove, at its own pace
//   KeyBindingBlock  KeyPlan: a chord and an action                                         -> KeyBindings.Register (id <mod>:<name>)
//   an effect a button applies: a hidden ability "ui-apply.<effect>" that casts it on the caster, cast through the same request a key makes
using System.Globalization;
using System.Text.Json.Nodes;

namespace Kiln;

internal static partial class Compiler
{
    private static BlockHandle? DefineUiBlock<T>(BlockContext ctx, Lowered low, string name, T block) where T : class, IBlock => block switch
    {
        WindowBlock w => UiCompile.DefineWindow(ctx, low, name, w),
        HudLineBlock h => UiCompile.DefineHud(ctx, low, name, h),
        KeyBindingBlock k => UiCompile.DefineKey(ctx, low, name, k),
        _ => null,
    };
}

/// <summary>What an action does, with its names resolved.</summary>
internal abstract record ActionPlan
{
    public abstract JsonObject Describe();
}

internal sealed record CommandPlan(string Line, UiCommandTarget Target = UiCommandTarget.Local) : ActionPlan
{
    public override JsonObject Describe() => Target == UiCommandTarget.Server
        ? new JsonObject { ["command"] = Line, ["commandTarget"] = "server" }
        : new JsonObject { ["command"] = Line };
}

internal sealed record TogglePlan(UiBinding Config) : ActionPlan
{
    public override JsonObject Describe() => new() { ["toggle"] = Config.Name };
}

/// <summary>Flips a bool state entry of the window the button is in.</summary>
internal sealed record StateTogglePlan(string Key) : ActionPlan
{
    public override JsonObject Describe() => new() { ["toggle"] = "state." + Key };
}

/// <summary>Sets a state entry of the window the button is in.</summary>
internal sealed record StateSetPlan(string Key, UiStateValue Value) : ActionPlan
{
    public override JsonObject Describe() => new() { ["set"] = "state." + Key, ["to"] = Value switch
    {
        UiStateValue.Bool b => b.Value, UiStateValue.Int64 l => l.Value.ToString(CultureInfo.InvariantCulture), UiStateValue.Decimal d => UiCanonical.Text(d.Value),
        UiStateValue.String s => s.Value, UiStateValue.StringSet ss => new JsonArray(ss.Value.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()), _ => null,
    } };
}

internal sealed record SetPlan(UiBinding Config, object Value) : ActionPlan
{
    public override JsonObject Describe() => new() { ["set"] = Config.Name, ["to"] = Value switch { bool b => b, double d => d, _ => Value.ToString() } };
}

/// <summary>Cast an ability by its id, as a key press does (a hidden ability for an applied effect).</summary>
internal sealed record CastPlan(string AbilityId, string? Effect = null) : ActionPlan
{
    public override JsonObject Describe() => Effect == null ? new JsonObject { ["castAbility"] = AbilityId } : new JsonObject { ["applyEffect"] = Effect, ["viaAbility"] = AbilityId };
}

internal sealed record WindowActionPlan(string Mod, string Window, bool Open, bool Toggle = false) : ActionPlan
{
    public override JsonObject Describe() => new() { [Toggle ? "toggleWindow" : Open ? "openWindow" : "closeWindow"] = $"{Mod}:{Window}" };
}

internal sealed record ToastPlan(UiText Text) : ActionPlan
{
    public override JsonObject Describe() => new() { ["toast"] = Text.For("en") };
}

/// <summary>One row, resolved.</summary>
internal sealed class RowPlan
{
    public required string Type { get; init; }
    public required string Id { get; init; }
    public bool SameLine { get; init; }
    public UiLine? Text { get; set; }
    public UiLine? Label { get; set; }
    public UiLine? Tooltip { get; set; }
    public UiCondition? When { get; set; }
    public UiBinding? Bind { get; set; }
    public string? Format { get; set; }
    public Color? Color { get; set; }
    public bool Wrap { get; set; }
    public bool IsInt { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public IReadOnlyList<string> Options { get; set; } = [];
    public ActionPlan? Does { get; set; }

    // ---- the rows of 0.2.0-dev.31 (dossier 09 §4.1) ----
    /// <summary>An authored id (new rows): scopes and selection follow it, never a label or an index.</summary>
    public bool Authored { get; init; }
    public int MaxBytes { get; set; }
    public UiInputCommit Commit { get; set; }
    public bool Multiline { get; set; }
    public UiBinding? Items { get; set; }
    public IReadOnlyList<ColumnPlan> Columns { get; set; } = [];
    public int MaxRows { get; set; }
    public UiBinding? Selected { get; set; }
    public IReadOnlyList<TabPlan> Tabs { get; set; } = [];
    public UiBinding? Nodes { get; set; }
    public UiTreeValue? StaticTree { get; set; }
    public IReadOnlyList<RowPlan> NodeRows { get; set; } = [];
    public UiBinding? Expanded { get; set; }
    public string? ImageFile { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }

    /// <summary>Every value the row reads, nested rows and cells included (a hidden one too: demand counts it).</summary>
    public IEnumerable<UiBinding> Reads =>
        (Text?.Bindings ?? []).Concat(Tooltip?.Bindings ?? []).Concat(When?.Bindings ?? []).Concat(Bind != null ? [Bind] : [])
            .Concat(Items != null ? [Items] : []).Concat(Selected != null ? [Selected] : []).Concat(Nodes != null ? [Nodes] : []).Concat(Expanded != null ? [Expanded] : [])
            .Concat(Columns.SelectMany(col => col.Cell.Bindings))
            .Concat(Tabs.SelectMany(t => (t.When?.Bindings ?? []).Concat(t.Rows.SelectMany(r => r.Reads))))
            .Concat(NodeRows.SelectMany(r => r.Reads));
}

internal sealed record ColumnPlan(string Id, UiLine Label, UiLine Cell);

internal sealed record TabPlan(string Id, UiLine Label, UiCondition? When, IReadOnlyList<RowPlan> Rows);

internal sealed class WindowPlan
{
    public required string Mod { get; init; }
    public required string Name { get; init; }
    public required WindowBlock Block { get; init; }
    public required UiLine Title { get; init; }
    public required IReadOnlyList<RowPlan> Rows { get; init; }
    public UiLine? MenuText { get; init; }
    /// <summary>The window's state declarations (empty when none).</summary>
    public IReadOnlyDictionary<string, UiStateDefinition> State { get; init; } = System.Collections.Immutable.ImmutableDictionary<string, UiStateDefinition>.Empty;
    public UiScope Scope { get; init; } = null!;

    public IReadOnlyList<UiBinding> Reads => Rows.SelectMany(r => r.Reads).DistinctBy(b => b.Name).ToList();

    /// <summary>What the live-values collector gathers while the window is open: every reachable name, hidden rows too.</summary>
    public UiWorldFields World => Reads.Aggregate(UiWorldFields.None, (f, b) => f | b.World);
}

internal sealed record HudPlan(string Mod, string Name, string Id, HudLineBlock Block, UiLine Text, UiCondition? When, Color Color);

internal sealed record KeyPlan(string Mod, string Name, string Id, KeyBindingBlock Block, KeyChord Key, ActionPlan Does);

internal static class UiColors
{
    private static readonly Dictionary<string, Color> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["white"] = Color.White, ["black"] = Color.Black, ["gray"] = Color.Gray, ["grey"] = Color.Gray, ["red"] = Color.Red, ["green"] = Color.Green,
        ["yellow"] = Color.Yellow, ["orange"] = Color.Orange, ["cyan"] = Color.Cyan,
    };

    public const string Known = "white, black, gray, red, green, yellow, orange, cyan, or #rrggbb / #rrggbbaa";

    public static bool TryParse(string? text, out Color color)
    {
        color = Color.White;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (Names.TryGetValue(text, out color)) return true;
        if (text[0] != '#' || text.Length is not (7 or 9)) return false;
        if (!uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v)) return false;
        color = text.Length == 7 ? new Color((byte)(v >> 16), (byte)(v >> 8), (byte)v) : new Color((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }
}

internal static class UiCompile
{
    internal const int MaxRows = 200;
    internal const double MinRefresh = 0.1, MaxRefresh = 3600;

    /// <summary>Runs a look at the window names that were used before their blocks were defined, some steps after the first such name: a mod's blocks all compile in one pass.</summary>
    internal static Action<int, Action> Later = BlocksBridge.After;

    // the hidden abilities a block has made so far (a block's own two buttons for one effect share one)
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Lowered, Dictionary<string, (string Id, string Effect)>> Made = new();

    // window names a block used while the block they name was still to compile (a data file's other block): looked at once the pass is over
    private static readonly object Gate = new();
    private static readonly Dictionary<string, List<(string Window, string Block, string Path)>> Deferred = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, object> Scheduled = new(StringComparer.Ordinal);

    public static void Forget(string mod)
    {
        UiOwnedAbilities.Forget(mod);
        lock (Gate)
        {
            Deferred.Remove(mod);
            Scheduled.Remove(mod);
        }
    }

    // ---- shared checks ---------------------------------------------------------------------------------------------

    private static BlockError Fail(BlockContext ctx, string path, string problem) => new(ctx.Block, path, problem);

    private static T Resolved<T>(BlockContext ctx, string path, Func<T> make)
    {
        try { return make(); }
        catch (FormatException e) { throw Fail(ctx, path, e.Message); }
    }

    // The scope names resolve in: the window's state while a window compiles, a table's row or a tree's node fields inside
    // their cells and node rows; outside a window (a HUD line, a key) the mod alone.
    [ThreadStatic] private static UiScope? _scope;

    private static UiScope ScopeOf(BlockContext ctx) => _scope is { } s && s.Mod == ctx.Mod.Id ? s : new UiScope { Mod = ctx.Mod.Id };

    private static UiBinding Resolve(BlockContext ctx, string spec) => UiBindings.Resolve(ScopeOf(ctx), spec);

    private static T InScope<T>(UiScope scope, Func<T> run)
    {
        var outer = _scope;
        _scope = scope;
        try { return run(); }
        finally { _scope = outer; }
    }

    private static UiBinding Bind(BlockContext ctx, string spec, string path) => Resolved(ctx, path, () => Resolve(ctx, spec));

    private static UiLine Line(BlockContext ctx, UiText? text, string path, bool templated, bool required, string what)
    {
        if (text == null) throw Fail(ctx, path, $"{what} needs a text");
        if (required && text.Languages.Any(l => string.IsNullOrWhiteSpace(text.For(l)))) throw Fail(ctx, path, $"{what} needs a text players can read, in every language it gives");
        return Resolved(ctx, path, () => UiLine.Compile(text, s => Resolve(ctx, s), templated));
    }

    private static UiCondition? When(BlockContext ctx, string? text, string path)
    {
        if (text == null) return null;
        if (string.IsNullOrWhiteSpace(text)) throw Fail(ctx, path, "an empty condition: leave it out, or say what must hold (game.inGame, !config.x, game.fps < 30)");
        return Resolved(ctx, path, () => UiCondition.Parse(text, s => Resolve(ctx, s)));
    }

    private static Color ColorOf(BlockContext ctx, string? text, string path, Color fallback)
    {
        if (text == null) return fallback;
        return UiColors.TryParse(text, out var c) ? c : throw Fail(ctx, path, $"\"{text}\" is not a colour: {UiColors.Known}");
    }

    private static void Refresh(BlockContext ctx, double seconds, string path)
    {
        if (!(seconds >= MinRefresh && seconds <= MaxRefresh && double.IsFinite(seconds))) throw Fail(ctx, path, $"{seconds} is outside {MinRefresh} to {MaxRefresh} seconds: a window or a line updates at a pace, never every frame");
    }

    private static void Size(BlockContext ctx, float v, string path)
    {
        if (!(v >= 0 && v <= 4096 && float.IsFinite(v))) throw Fail(ctx, path, $"{v} is outside 0..4096 pixels");
    }

    // ---- actions ---------------------------------------------------------------------------------------------------

    /// <summary>An action's plan. <paramref name="owner"/> is the block's name, for what an applied effect is called.</summary>
    internal static ActionPlan Action(BlockContext ctx, Lowered low, string owner, UiAction? a, string path)
    {
        if (a == null) throw Fail(ctx, path, "needs an action: command, toggle, set, castAbility, applyEffect, openWindow, closeWindow, toggleWindow or toast");
        var given = new List<string>();
        if (a.Command != null) given.Add("command");
        if (a.Toggle != null) given.Add("toggle");
        if (a.Set != null) given.Add("set");
        if (a.CastAbility != null) given.Add("castAbility");
        if (a.ApplyEffect != null) given.Add("applyEffect");
        if (a.OpenWindow != null) given.Add("openWindow");
        if (a.CloseWindow != null) given.Add("closeWindow");
        if (a.ToggleWindow != null) given.Add("toggleWindow");
        if (a.Toast != null) given.Add("toast");
        if (a.CommandTarget is { } target)
        {
            if (a.Command == null) throw Fail(ctx, $"{path}.commandTarget", "goes with a command only: it says where the command runs (local or server)");
            if (target is not (UiCommandTarget.Local or UiCommandTarget.Server)) throw Fail(ctx, $"{path}.commandTarget", "is local or server");
        }
        if (given.Count == 0) throw Fail(ctx, path, "an action does something: give it one of command, toggle, set, castAbility, applyEffect, openWindow, closeWindow, toggleWindow, toast");
        if (given.Count > 1) throw Fail(ctx, path, $"an action does one thing, and this has {string.Join(" and ", given)}: use one button or key for each");

        if (a.Command is { } command)
        {
            var line = command.Trim().TrimStart('/').Trim();
            if (line.Length == 0) throw Fail(ctx, $"{path}.command", "an empty command line");
            if (line.Length > 256 || line.Contains('\n') || line.Contains('\r')) throw Fail(ctx, $"{path}.command", "a command is one line of at most 256 characters");
            if (a.CommandTarget == UiCommandTarget.Server && UiCommandWire.LineProblem(line) is { } problem) throw Fail(ctx, $"{path}.command", problem);
            return new CommandPlan(line, a.CommandTarget ?? UiCommandTarget.Local);
        }
        if (a.Toggle is { } toggle)
        {
            var b = Bind(ctx, toggle, $"{path}.toggle");
            if (b.Source == UiSource.State && b.Type == UiValueType.Bool) return new StateTogglePlan(b.Key!);
            if (!b.IsConfig || b.Type != UiValueType.Bool) throw Fail(ctx, $"{path}.toggle", $"{b.Name} is not a true/false config entry or bool state entry: a toggle flips one");
            return new TogglePlan(b);
        }
        if (a.Set is { } set)
        {
            var b = Bind(ctx, set.Bind, $"{path}.set.bind");
            if (b.Source == UiSource.State)
            {
                var v = UiWindowState.FromLiteral(b.State!, set.To, out var problem);
                if (v == null) throw Fail(ctx, $"{path}.set.to", $"{b.Name}: {problem}");
                return new StateSetPlan(b.Key!, v);
            }
            if (!b.IsConfig) throw Fail(ctx, $"{path}.set.bind", $"{b.Name} is read-only: set takes a config entry or a state entry of the window");
            return new SetPlan(b, ConfigValue(ctx, b, set.To, $"{path}.set.to"));
        }
        if (a.CastAbility is { } cast) return new CastPlan(AbilityRuntime.IdOf(ctx, cast, $"{path}.castAbility"));
        if (a.ApplyEffect is { } effect) return ApplyEffect(ctx, low, owner, effect, $"{path}.applyEffect");
        if (a.OpenWindow is { } open) return WindowAction(ctx, low, open, true, $"{path}.openWindow");
        if (a.CloseWindow is { } close) return WindowAction(ctx, low, close, false, $"{path}.closeWindow");
        if (a.ToggleWindow is { } either) return (WindowActionPlan)WindowAction(ctx, low, either, true, $"{path}.toggleWindow") with { Toggle = true };
        var toast = Line(ctx, a.Toast, $"{path}.toast", templated: false, required: true, "a toast");
        return new ToastPlan(toast.Text);
    }

    /// <summary>What a set action gives a config entry, as the UI holds values (a bool, a double or a text), checked against the entry.</summary>
    private static object ConfigValue(BlockContext ctx, UiBinding b, object to, string path)
    {
        var v = to is System.Text.Json.JsonElement j
            ? j.ValueKind switch
            {
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                System.Text.Json.JsonValueKind.Number => j.GetDouble(),
                System.Text.Json.JsonValueKind.String => (object)j.GetString()!,
                _ => throw Fail(ctx, path, "a value is true or false, a number or a text"),
            }
            : UiBindings.Normalize(to) is { } n ? (UiBindings.ToDouble(n) is { } nd && n is not bool and not string ? nd : n) : throw Fail(ctx, path, "needs a value");
        switch (b.EntryType)
        {
            case "bool":
                return v is bool ? v : throw Fail(ctx, path, $"{b.Name} is a true/false entry: give true or false");
            case "int" or "float":
                if (v is not double d || !double.IsFinite(d)) throw Fail(ctx, path, $"{b.Name} is a number: give one");
                if (b.EntryType == "int" && d != Math.Floor(d)) throw Fail(ctx, path, $"{b.Name} takes whole numbers: {d.ToString(CultureInfo.InvariantCulture)} has a fraction");
                // a soft side of the entry's range (kiln#155) bounds nothing: the config takes a value past it
                if ((b.HardMin is { } lo && d < lo) || (b.HardMax is { } hi && d > hi)) throw Fail(ctx, path, $"{d.ToString(CultureInfo.InvariantCulture)} is outside {b.Name}'s range {b.HardMin?.ToString(CultureInfo.InvariantCulture) ?? "-"}..{b.HardMax?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
                return d;
            case "enum":
                if (v is not string s) throw Fail(ctx, path, $"{b.Name} takes one of: {string.Join(", ", b.Options ?? [])}");
                return (b.Options ?? []).FirstOrDefault(o => string.Equals(o, s, StringComparison.OrdinalIgnoreCase)) ?? throw Fail(ctx, path, $"\"{s}\" is not an option of {b.Name}: {string.Join(", ", b.Options ?? [])}");
            default:
                // text, durations and lists: the config parses them when it is set
                return v is string ? v : UiBindings.Text(v, null);
        }
    }

    // An effect a button applies: a hidden ability that casts it on whoever casts, defined last so that nothing is left behind when the block is wrong.
    private static ActionPlan ApplyEffect(BlockContext ctx, Lowered low, string owner, EffectRef effect, string path)
    {
        if (effect.Name == null && effect.Inline == null) throw Fail(ctx, path, "needs an effect: a block's name, or one written here");
        // "burst", and "<this mod>:burst" (what a handle says), are one effect: one ability
        var named = effect.Name != null && effect.Name.StartsWith(ctx.Mod.Id + ":", StringComparison.Ordinal) ? effect.Name[(ctx.Mod.Id.Length + 1)..] : effect.Name;
        var key = named != null ? named.Replace(':', '.') : $"{owner}.{path.Replace('[', '.').Replace("]", "").Replace(".applyEffect", "")}";
        var hidden = $"ui-apply.{key}";
        var identity = named == null ? $"inline:{owner}:{path}" : named.Contains(':') ? named : $"{ctx.Mod.Id}:{named}";
        // one ability per effect: the one this block already made, or one an earlier block committed (a handle alone is not enough: its block may have failed after)
        var mine = Made.GetValue(low, static _ => new(StringComparer.OrdinalIgnoreCase));
        if (mine.TryGetValue(hidden, out var made))
        {
            if (made.Effect != identity) throw Fail(ctx, path, $"two different effects need the hidden ability \"{hidden}\": rename the named effect");
            return new CastPlan(made.Id, effect.Name ?? hidden);
        }
        // Ability ids are case-insensitive even though ordinary block names are case-sensitive.
        var collision = ctx.Mod.Handles.FirstOrDefault(pair => string.Equals(pair.Key, hidden, StringComparison.OrdinalIgnoreCase));
        if (collision.Value is { } known)
        {
            if (known is BlockHandle<AbilityBlock> && ctx.Mod.Lowered.TryGetValue(collision.Key, out var existing) && (string?)existing["uiApplyEffect"] == identity)
                return new CastPlan(known.GameName, effect.Name ?? hidden);
            throw Fail(ctx, path, $"\"{hidden}\" is already a different block: this name is reserved for the UI action's hidden ability; rename the existing block or effect");
        }
        // the effect is looked at first, so that a wrong name is said at this field
        if (effect.Name != null) ctx.Effect(effect, path);
        var ability = new AbilityBlock { Name = $"Apply {effect.Name ?? key}", OnCast = [effect], PlayersOnly = true };
        BlockHandle handle;
        Lowered child;
        string label;
        try { (handle, child, label) = Compiler.Lower(ctx.Mod, hidden, ability, uiGenerated: true); }
        catch (BlockError e) { throw Fail(ctx, path, e.Field is { Length: > 0 } inner ? $"{e.Problem} (in the hidden ability, {inner})" : e.Problem); }
        mine[hidden] = (handle.GameName, identity);
        child.Json["uiApplyEffect"] = identity;
        low.AfterCommit(() =>
        {
            // AbilityRegistry also checks its actual owner's Alive on every cast, before the data poll notices unload.
            if (!BlocksBridge.RunAsUiOwner(ctx.Mod.Id, child.Commit)) return;
            ctx.Mod.Handles[hidden] = handle;
            if (AbilityRuntime.Find(handle.GameName) is { } registered) UiOwnedAbilities.Track(ctx.Mod.Id, registered);
            ctx.Mod.Lowered[hidden] = child.Json;
            ctx.Mod.ReportDirty = true;
            BlocksBridge.Log(LogLevel.Info, $"blocks: {ctx.Mod.Id}: {label} -> {child.Summary()}");
        });
        return new CastPlan(handle.GameName, effect.Name ?? hidden);
    }

    private static ActionPlan WindowAction(BlockContext ctx, Lowered low, WindowRef r, bool open, string path)
    {
        var name = r.Value;
        if (string.IsNullOrWhiteSpace(name)) throw Fail(ctx, path, "needs a window: a block's name, or \"other-mod:name\"");
        var colon = name.IndexOf(':');
        if (colon > 0)
        {
            var other = Blocks.Find(name[(colon + 1)..], name[..colon]) ?? throw Fail(ctx, path, $"no block \"{name[(colon + 1)..]}\" in mod {name[..colon]} (is it loaded before this one?)");
            if (other is not BlockHandle<WindowBlock>) throw Fail(ctx, path, $"\"{name}\" is {Article(other)}, not a window");
            return new WindowActionPlan(name[..colon], name[(colon + 1)..], open);
        }
        if (ctx.Mod.Handles.TryGetValue(name, out var h))
            return h is BlockHandle<WindowBlock> ? new WindowActionPlan(ctx.Mod.Id, name, open) : throw Fail(ctx, path, $"\"{name}\" is {Article(h)}, not a window");
        // not defined yet: a data file's block still to compile, or one the mod's code defines later. Windows open each other, so nothing is compiled for it here: the name is
        // looked at when the pass is over (and by KILN111 when a C# mod is built). Only a block that was committed leaves a name to look at.
        var block = ctx.Block;
        low.AfterCommit(() => Defer(ctx.Mod, name, block, path));
        return new WindowActionPlan(ctx.Mod.Id, name, open);
    }

    private static string Article(BlockHandle h)
    {
        var kind = h.BlockType.Name.Replace("Block", "").ToLowerInvariant();
        return (kind.Length > 0 && "aeiou".Contains(kind[0]) ? "an " : "a ") + kind;
    }

    private static void Defer(ModBlocks mod, string window, string block, string path)
    {
        bool first;
        object token;
        lock (Gate)
        {
            if (!Deferred.TryGetValue(mod.Id, out var list)) Deferred[mod.Id] = list = new();
            list.Add((window, block, path));
            first = !Scheduled.TryGetValue(mod.Id, out token!);
            if (first) Scheduled[mod.Id] = token = new object();
        }
        if (first) Later(12, () => VerifyDeferred(mod, token));
    }

    /// <summary>Looks at the windows that blocks named before they were defined: each must be one by now. What is wrong goes to the mod's errors and log, by block and field.
    /// Returns how many names it checked.</summary>
    internal static int VerifyDeferred(ModBlocks mod, object? expected = null)
    {
        List<(string Window, string Block, string Path)> todo;
        lock (Gate)
        {
            if (expected != null && (!Scheduled.TryGetValue(mod.Id, out var current) || current != expected)) return 0;
            Scheduled.Remove(mod.Id);
            if (!Deferred.Remove(mod.Id, out todo!)) return 0;
        }
        foreach (var (window, block, path) in todo)
        {
            if (mod.Handles.TryGetValue(window, out var h) && h is BlockHandle<WindowBlock>) continue;
            var problem = h != null
                ? $"\"{window}\" is {Article(h)}, not a window"
                : $"no window \"{window}\" in this mod (a block that failed to compile is not defined); define it, or say \"other-mod:{window}\"; there are: {string.Join(", ", mod.Handles.Where(x => x.Value is BlockHandle<WindowBlock>).Select(x => x.Key).Order())}";
            var text = $"block {block}, {path}: {problem}";
            lock (mod.Errors) mod.Errors.Add(text);
            mod.ReportDirty = true;
            BlocksBridge.Log(LogLevel.Error, $"blocks: {mod.Id}: {text}");
        }
        return todo.Count;
    }

    // ---- windows ---------------------------------------------------------------------------------------------------

    public static BlockHandle DefineWindow(BlockContext ctx, Lowered low, string name, WindowBlock w)
    {
        ctx.Need(ClientsLevel.Mod, null, "a window is drawn by each player's game from the mod's own file: the server cannot send one");
        var mod = ctx.Mod.Id;
        var title = Line(ctx, w.Title, "title", templated: false, required: true, "a window");
        if (!Enum.IsDefined(w.Kind)) throw Fail(ctx, "kind", "a window's kind is window or screen");
        Size(ctx, w.Width, "width");
        Size(ctx, w.Height, "height");
        Refresh(ctx, w.Refresh, "refresh");
        if (w.Kind == WindowKind.Window)
        {
            if (w.Dim != null) throw Fail(ctx, "dim", "dim darkens the game behind a screen: set kind to screen, or leave it out");
        }
        else
        {
            if (w.Position != null) throw Fail(ctx, "position", "position is for a window; a screen is always in the middle");
            if (w.AutoResize) throw Fail(ctx, "autoResize", "autoResize is for a window; a screen has the size you give it");
            if (w.Alpha != null) throw Fail(ctx, "alpha", "alpha is for a window; a screen has the game's look");
            if (!w.Interactive) throw Fail(ctx, "interactive", "a screen always takes the mouse and keyboard; interactive: false is for a window");
        }
        if (w.Position is { } p)
        {
            if (p.Length != 2) throw Fail(ctx, "position", $"[x, y] as 0..1 of the screen, and this has {p.Length} number(s)");
            if (p.Any(v => !(v >= 0 && v <= 1))) throw Fail(ctx, "position", $"[{string.Join(", ", p)}] is outside 0..1");
        }
        if (w.Alpha is { } alpha && !(alpha >= 0 && alpha <= 1)) throw Fail(ctx, "alpha", $"{alpha} is outside 0..1");
        if (w.Rows == null || w.Rows.Count == 0) throw Fail(ctx, "rows", "a window with no rows shows nothing: give it text, a value or a button");
        if (w.Rows.Count > MaxRows) throw Fail(ctx, "rows", $"{w.Rows.Count} rows is more than {MaxRows}");
        UiLine? menuText = null;
        if (w.Menu is { } menu)
        {
            if (!Enum.IsDefined(menu.In)) throw Fail(ctx, "menu.in", "a menu entry goes in pause, main or both");
            if (menu.Text == null) throw Fail(ctx, "menu.text", "a menu entry needs its label");
            menuText = Line(ctx, menu.Text, "menu.text", templated: false, required: true, "a menu entry");
        }

        var state = StateOf(ctx, w.State);
        var scope = new UiScope { Mod = mod, State = state };
        var wc = new WindowCompile(w.Kind == WindowKind.Screen || w.Interactive);
        var rows = InScope(scope, () =>
        {
            var list = new List<RowPlan>();
            for (var i = 0; i < w.Rows.Count; i++) list.Add(Row(ctx, low, name, w.Rows[i], i, $"rows[{i}]", wc, 0));
            return list;
        });
        if (wc.Authored > MaxRows) throw Fail(ctx, "rows", $"{wc.Authored} rows in all (nested ones counted) is more than {MaxRows}");
        var plan = new WindowPlan { Mod = mod, Name = name, Block = w, Title = title, Rows = rows, MenuText = menuText, State = state, Scope = scope };

        low.Json["window"] = new JsonObject
        {
            ["kind"] = w.Kind.ToString().ToLowerInvariant(), ["title"] = w.Title.For("en"), ["refresh"] = w.Refresh, ["openAtStart"] = w.OpenAtStart,
            ["menu"] = w.Menu is { } m ? new JsonObject { ["text"] = m.Text.For("en"), ["in"] = m.In.ToString().ToLowerInvariant(), ["order"] = m.Order } : null,
            ["rows"] = new JsonArray(rows.Select(DescribeRow).ToArray()),
            ["reads"] = new JsonArray(plan.Reads.Select(b => (JsonNode?)JsonValue.Create(b.Name)).ToArray()),
        };
        if (state.Count > 0)
            ((JsonObject)low.Json["window"]!)["state"] = new JsonObject(state.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, UiWindowState.Name(kv.Value))));
        low.AfterCommit(() => UiRuntime.Register(plan));
        return new BlockHandle<WindowBlock>(mod, name, BlockIds.Derive(mod, "window", name), $"{mod}:{name}", w);
    }

    internal const int MaxDepth = 8, MaxColumns = 16, MaxTabs = 16, MaxNodeRows = 8, MaxDynamic = 512;
    internal const int MaxStateKeys = 64, MaxInputBytes = 4096;

    /// <summary>What a window's rows share while they compile: ids, the authored count, whether it takes the keyboard.</summary>
    private sealed class WindowCompile(bool keyboard)
    {
        public readonly bool Keyboard = keyboard;
        public readonly HashSet<string> Ids = new(StringComparer.Ordinal);
        public int Authored;
    }

    private static System.Collections.Immutable.ImmutableDictionary<string, UiStateDefinition> StateOf(BlockContext ctx, IReadOnlyDictionary<string, UiStateDefinition>? state)
    {
        var b = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<string, UiStateDefinition>(StringComparer.Ordinal);
        if (state == null) return b.ToImmutable();
        if (state.Count > MaxStateKeys) throw Fail(ctx, "state", $"{state.Count} state entries is more than {MaxStateKeys}");
        long bytes = 0;
        foreach (var (key, def) in state)
        {
            var path = $"state.{key}";
            if (!UiTypedNames.IsId(key)) throw Fail(ctx, path, $"\"{key}\" is not a state key: [a-z][a-z0-9_-]{{0,63}}");
            if (def == null) throw Fail(ctx, path, "a state entry is an object with a \"type\": bool, int64, decimal, string or stringSet");
            switch (def)
            {
                case UiInt64State l when l.Min > l.Max: throw Fail(ctx, path, $"min {l.Min} is above max {l.Max}");
                case UiDecimalState m when m.Scale is < 0 or > 6: throw Fail(ctx, $"{path}.scale", $"{m.Scale} is outside 0..6");
                case UiDecimalState m when m.Min > m.Max: throw Fail(ctx, path, $"min {UiCanonical.Text(m.Min)} is above max {UiCanonical.Text(m.Max)}");
                case UiDecimalState m when UiCanonical.Scale(m.Min) > m.Scale || UiCanonical.Scale(m.Max) > m.Scale:
                    throw Fail(ctx, path, $"min and max need at most {m.Scale} digits after the point");
                case UiStringState s when s.MaxBytes is < 1 or > MaxInputBytes: throw Fail(ctx, $"{path}.maxBytes", $"{s.MaxBytes} is outside 1..{MaxInputBytes}");
                case UiStringSetState ss when ss.Initial == null: throw Fail(ctx, $"{path}.initial", "a stringSet needs its initial items ([] for none)");
                case UiStringSetState ss when ss.MaxItems is < 1 or > MaxDynamic: throw Fail(ctx, $"{path}.maxItems", $"{ss.MaxItems} is outside 1..{MaxDynamic}");
                case UiStringSetState ss when ss.MaxItemBytes is < 1 or > 128: throw Fail(ctx, $"{path}.maxItemBytes", $"{ss.MaxItemBytes} is outside 1..128");
            }
            UiStateValue initial;
            try { initial = UiWindowState.InitialOf(def); }
            catch (ArgumentException e) { throw Fail(ctx, path, e.Message); }
            if (UiWindowState.Problem(def, initial) is { } problem) throw Fail(ctx, $"{path}.initial", problem);
            if (def is UiStringSetState set && set.Initial.Count != set.Initial.Distinct(StringComparer.Ordinal).Count())
                throw Fail(ctx, $"{path}.initial", "a stringSet's initial items are unique");
            bytes += key.Length + System.Text.Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(initial, initial.GetType()));
            b[key] = def;
        }
        if (bytes > UiWindowState.MaxWindowBytes) throw Fail(ctx, "state", $"the initial state is {bytes} bytes, more than {UiWindowState.MaxWindowBytes}");
        return b.ToImmutable();
    }

    private static string AuthoredId(BlockContext ctx, WindowCompile wc, string? id, string path)
    {
        if (!UiTypedNames.IsId(id)) throw Fail(ctx, $"{path}.id", $"\"{id}\" is not a row id: [a-z][a-z0-9_-]{{0,63}} (input, table, tabs, tree and image rows need one)");
        // the older rows' ids are r0, r1, ... (their place): an authored id of that form would share an ImGui id with one
        if (id!.Length > 1 && id[0] == 'r' && id.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0)
            throw Fail(ctx, $"{path}.id", $"\"{id}\" has the form of an older row's place (r0, r1, ...): give the row another id");
        if (!wc.Ids.Add(id!)) throw Fail(ctx, $"{path}.id", $"\"{id}\" is the id of another row of this window: ids are unique in a window, nested rows included");
        return id!;
    }

    private static UiBinding StateString(BlockContext ctx, string spec, string path, bool set = false)
    {
        var b = Bind(ctx, spec, path);
        if (b.Source != UiSource.State || b.Type != (set ? UiValueType.StringSet : UiValueType.Text))
            throw Fail(ctx, path, $"{b.Name} is not a {(set ? "stringSet" : "string")} state entry of this window");
        return b;
    }

    private static RowPlan Row(BlockContext ctx, Lowered low, string owner, UiRow? row, int index, string path, WindowCompile wc, int depth)
    {
        if (row == null) throw Fail(ctx, path, "a row is an object with a \"type\": text, value, toggle, slider, choice, separator, button, input, table, tabs, tree or image");
        if (row.SameLine && index == 0) throw Fail(ctx, $"{path}.sameLine", "the first row has no row before it to share a line with");
        string Type() => row switch
        {
            TextRow => "text", ValueRow => "value", ToggleRow => "toggle", SliderRow => "slider", ChoiceRow => "choice", SeparatorRow => "separator", ButtonRow => "button",
            InputRow => "input", TableRow => "table", TabsRow => "tabs", TreeRow => "tree", ImageRow => "image", _ => "?",
        };
        var authoredId = row switch
        {
            InputRow r => r.Id, TableRow r => r.Id, TabsRow r => r.Id, TreeRow r => r.Id, ImageRow r => r.Id, _ => null,
        };
        var isNew = row is InputRow or TableRow or TabsRow or TreeRow or ImageRow;
        wc.Authored++;
        var plan = new RowPlan { Type = Type(), Id = isNew ? AuthoredId(ctx, wc, authoredId, path) : $"r{index}", SameLine = row.SameLine, Authored = isNew };
        plan.When = When(ctx, row.When, $"{path}.when");
        if (row.Tooltip != null) plan.Tooltip = Line(ctx, row.Tooltip, $"{path}.tooltip", templated: true, required: true, "a tooltip");
        switch (row)
        {
            case TextRow t:
                plan.Text = Line(ctx, t.Text, $"{path}.text", templated: true, required: true, "a text row");
                plan.Color = t.Color == null ? null : ColorOf(ctx, t.Color, $"{path}.color", Color.White);
                if (t.Wrap && t.Color != null) throw Fail(ctx, $"{path}.color", "colour and wrap together: the overlay's wrapped text has no colour; use one");
                plan.Wrap = t.Wrap;
                break;
            case ValueRow v:
                plan.Label = Line(ctx, v.Label, $"{path}.label", templated: false, required: true, "a value row");
                plan.Bind = Bind(ctx, v.Bind, $"{path}.bind");
                if (!plan.Bind.Scalar) throw Fail(ctx, $"{path}.bind", $"{plan.Bind.Name} is a set, rows or a tree: a table or a tree shows it");
                if (UiBindings.CheckFormat(plan.Bind.Type, v.Format) is { } problem) throw Fail(ctx, $"{path}.format", problem);
                plan.Format = v.Format;
                break;
            case ToggleRow g:
                plan.Label = Line(ctx, g.Label, $"{path}.label", templated: false, required: true, "a toggle");
                plan.Bind = Bind(ctx, g.Bind, $"{path}.bind");
                if (plan.Bind.Type != UiValueType.Bool || !(plan.Bind.IsConfig || plan.Bind.Source == UiSource.State))
                    throw Fail(ctx, $"{path}.bind", $"{plan.Bind.Name} is not a true/false config entry or bool state entry: a toggle edits one (a value row shows any value)");
                break;
            case SliderRow s:
                plan.Label = Line(ctx, s.Label, $"{path}.label", templated: false, required: true, "a slider");
                plan.Bind = Bind(ctx, s.Bind, $"{path}.bind");
                if (plan.Bind.Source == UiSource.State)
                {
                    if (plan.Bind.State is not (UiInt64State or UiDecimalState)) throw Fail(ctx, $"{path}.bind", $"{plan.Bind.Name} is not an int64 or decimal state entry: a slider edits one");
                    plan.IsInt = plan.Bind.State is UiInt64State;
                    StateSliderRange(ctx, plan, s, path);
                    break;
                }
                if (!plan.Bind.IsConfig || plan.Bind.Type is not (UiValueType.Number or UiValueType.Int64)) throw Fail(ctx, $"{path}.bind", $"{plan.Bind.Name} is not a whole-number or decimal config entry: a slider edits one");
                plan.IsInt = plan.Bind.EntryType == "int";
                SliderRange(ctx, plan, s, path);
                break;
            case ChoiceRow ch:
                plan.Label = Line(ctx, ch.Label, $"{path}.label", templated: false, required: true, "a choice");
                plan.Bind = Bind(ctx, ch.Bind, $"{path}.bind");
                if (!plan.Bind.IsConfig || plan.Bind.EntryType != "enum" || plan.Bind.Options is not { Count: > 0 }) throw Fail(ctx, $"{path}.bind", $"{plan.Bind.Name} is not an enum config entry: a choice picks one of its options");
                plan.Options = plan.Bind.Options;
                break;
            case SeparatorRow sp:
                if (sp.Text != null) plan.Text = Line(ctx, sp.Text, $"{path}.text", templated: true, required: true, "a separator's heading");
                break;
            case ButtonRow b:
                plan.Label = Line(ctx, b.Label, $"{path}.label", templated: false, required: true, "a button");
                plan.Does = Action(ctx, low, owner, b.Does, $"{path}.does");
                break;
            case InputRow i:
                if (!wc.Keyboard) throw Fail(ctx, path, "a text field needs the keyboard: put it in an interactive window or a screen (interactive: false only shows)");
                plan.Label = Line(ctx, i.Label, $"{path}.label", templated: false, required: true, "a text field");
                plan.Bind = Bind(ctx, i.Bind, $"{path}.bind");
                if (plan.Bind.Source == UiSource.State)
                {
                    if (plan.Bind.State is not UiStringState st) throw Fail(ctx, $"{path}.bind", $"{plan.Bind.Name} is not a string state entry: a text field edits one");
                    if (i.MaxBytes > st.MaxBytes) throw Fail(ctx, $"{path}.maxBytes", $"{i.MaxBytes} is more than {plan.Bind.Name}'s own maxBytes {st.MaxBytes}");
                }
                else if (!plan.Bind.IsConfig || plan.Bind.EntryType != "string")
                    throw Fail(ctx, $"{path}.bind", $"{plan.Bind.Name} is not a text config entry or string state entry: a text field edits one (the text never becomes a command)");
                if (i.MaxBytes is < 2 or > MaxInputBytes) throw Fail(ctx, $"{path}.maxBytes", $"{i.MaxBytes} is outside 2..{MaxInputBytes} UTF-8 bytes");
                if (!Enum.IsDefined(i.Commit)) throw Fail(ctx, $"{path}.commit", "commit is blur or enter");
                if (i.Multiline && i.Commit == UiInputCommit.Enter) throw Fail(ctx, $"{path}.commit", "Enter is a new line in a multiline field: it commits when it loses the keyboard (commit: blur)");
                plan.MaxBytes = i.MaxBytes;
                plan.Commit = i.Commit;
                plan.Multiline = i.Multiline;
                break;
            case TableRow t:
                CompileTable(ctx, plan, t, path);
                break;
            case TabsRow t:
                if (depth + 1 >= MaxDepth) throw Fail(ctx, path, $"rows nest at most {MaxDepth} levels");
                if (t.Tabs is not { Count: > 0 }) throw Fail(ctx, $"{path}.tabs", "tabs need at least one tab");
                if (t.Tabs.Count > MaxTabs) throw Fail(ctx, $"{path}.tabs", $"{t.Tabs.Count} tabs is more than {MaxTabs}");
                var tabIds = new HashSet<string>(StringComparer.Ordinal);
                var tabs = new List<TabPlan>();
                for (var k = 0; k < t.Tabs.Count; k++)
                {
                    var tab = t.Tabs[k];
                    var tp = $"{path}.tabs[{k}]";
                    if (tab == null) throw Fail(ctx, tp, "a tab is an object with an id, a label and rows");
                    if (!UiTypedNames.IsId(tab.Id)) throw Fail(ctx, $"{tp}.id", $"\"{tab.Id}\" is not a tab id: [a-z][a-z0-9_-]{{0,63}}");
                    if (!tabIds.Add(tab.Id)) throw Fail(ctx, $"{tp}.id", $"\"{tab.Id}\" is the id of another tab of this row");
                    var label = Line(ctx, tab.Label, $"{tp}.label", templated: false, required: true, "a tab");
                    var when = When(ctx, tab.When, $"{tp}.when");
                    if (tab.Rows is not { Count: > 0 }) throw Fail(ctx, $"{tp}.rows", "a tab with no rows shows nothing");
                    var inner = new List<RowPlan>();
                    for (var r = 0; r < tab.Rows.Count; r++) inner.Add(Row(ctx, low, owner, tab.Rows[r], r, $"{tp}.rows[{r}]", wc, depth + 1));
                    tabs.Add(new TabPlan(tab.Id, label, when, inner));
                }
                plan.Tabs = tabs;
                if (t.Selected != null)
                {
                    plan.Selected = StateString(ctx, t.Selected, $"{path}.selected");
                    // the row writes its tab ids into the entry: each must fit, or a click would break the declaration
                    if (plan.Selected.State is UiStringState { MaxBytes: var room } &&
                        tabs.FirstOrDefault(x => System.Text.Encoding.UTF8.GetByteCount(x.Id) > room) is { } wide)
                        throw Fail(ctx, $"{path}.selected", $"{plan.Selected.Name} holds at most {room} UTF-8 bytes and the tab id \"{wide.Id}\" has {System.Text.Encoding.UTF8.GetByteCount(wide.Id)}: give the entry a maxBytes of at least {tabs.Max(x => System.Text.Encoding.UTF8.GetByteCount(x.Id))}");
                }
                break;
            case TreeRow t:
                CompileTree(ctx, low, owner, plan, t, path, wc, depth);
                break;
            case ImageRow im:
                plan.ImageFile = BlockFiles.InMod(ctx, im.Asset, $"{path}.asset");
                if (!plan.ImageFile.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw Fail(ctx, $"{path}.asset", $"\"{im.Asset}\": a picture is a PNG file");
                if (UiImages.Preflight(plan.ImageFile) is { } bad) throw Fail(ctx, $"{path}.asset", $"\"{im.Asset}\": {bad}");
                Size(ctx, im.Width, $"{path}.width");
                Size(ctx, im.Height, $"{path}.height");
                plan.Width = im.Width;
                plan.Height = im.Height;
                if (im.Does != null) plan.Does = Action(ctx, low, owner, im.Does, $"{path}.does");
                break;
            default:
                throw Fail(ctx, path, $"{row.GetType().Name} is not a row Kiln knows");
        }
        return plan;
    }

    private static readonly UiField KeyField = new("key", UiScalarType.String);

    private static void CompileTable(BlockContext ctx, RowPlan plan, TableRow t, string path)
    {
        if (string.IsNullOrWhiteSpace(t.Items)) throw Fail(ctx, $"{path}.items", "a table needs its rows: world.local.inventory or mod.<id> of a rows source");
        plan.Items = Bind(ctx, t.Items, $"{path}.items");
        if (plan.Items.Type != UiValueType.Rows) throw Fail(ctx, $"{path}.items", $"{plan.Items.Name} is not rows: world.local.inventory or mod.<id> of a rows source");
        if (t.Columns is not { Count: > 0 }) throw Fail(ctx, $"{path}.columns", "a table needs at least one column");
        if (t.Columns.Count > MaxColumns) throw Fail(ctx, $"{path}.columns", $"{t.Columns.Count} columns is more than {MaxColumns}");
        if (t.MaxRows is < 1 or > MaxDynamic) throw Fail(ctx, $"{path}.maxRows", $"{t.MaxRows} is outside 1..{MaxDynamic}");
        IReadOnlyList<UiField> fields = plan.Items.Source == UiSource.WorldLocal ? UiTypedNames.InventoryFields : [KeyField, .. plan.Items.Fields ?? []];
        var cellScope = ScopeOf(ctx).With(rowFields: fields);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var cols = new List<ColumnPlan>();
        for (var k = 0; k < t.Columns.Count; k++)
        {
            var col = t.Columns[k];
            var cp = $"{path}.columns[{k}]";
            if (col == null) throw Fail(ctx, cp, "a column is an object with an id, a label and a cell");
            if (!UiTypedNames.IsId(col.Id)) throw Fail(ctx, $"{cp}.id", $"\"{col.Id}\" is not a column id: [a-z][a-z0-9_-]{{0,63}}");
            if (!ids.Add(col.Id)) throw Fail(ctx, $"{cp}.id", $"\"{col.Id}\" is the id of another column");
            var label = Line(ctx, col.Label, $"{cp}.label", templated: false, required: true, "a column");
            var cell = InScope(cellScope, () => Line(ctx, col.Cell, $"{cp}.cell", templated: true, required: false, "a cell"));
            cols.Add(new ColumnPlan(col.Id, label, cell));
        }
        plan.Columns = cols;
        plan.MaxRows = t.MaxRows;
        if (t.Selected != null) plan.Selected = StateString(ctx, t.Selected, $"{path}.selected");
    }

    private static void CompileTree(BlockContext ctx, Lowered low, string owner, RowPlan plan, TreeRow t, string path, WindowCompile wc, int depth)
    {
        if ((t.Nodes == null) == (t.Roots == null)) throw Fail(ctx, path, "a tree takes exactly one of nodes (mod.<id> of a tree source) and roots (the tree written here)");
        IReadOnlyList<UiField> fields;
        if (t.Nodes != null)
        {
            if (t.Fields != null) throw Fail(ctx, $"{path}.fields", "the source's schema says the fields of nodes from a source: leave fields out");
            plan.Nodes = Bind(ctx, t.Nodes, $"{path}.nodes");
            if (plan.Nodes.Type != UiValueType.Tree) throw Fail(ctx, $"{path}.nodes", $"{plan.Nodes.Name} is not a tree source");
            fields = plan.Nodes.Fields ?? [];
        }
        else
        {
            fields = t.Fields ?? [];
            try
            {
                UiDataRegistry.CheckFields(UiDataShape.Tree, fields);
                var flat = new List<(string, string?, UiText, IReadOnlyDictionary<string, UiScalar>?)>();
                void Flatten(IReadOnlyList<UiTreeNodeDefinition>? nodes, string? parent)
                {
                    foreach (var n in nodes ?? [])
                    {
                        if (n == null) throw new ArgumentException("a null node");
                        flat.Add((n.Key, parent, n.Label, Coerce(fields, n.Values)));
                        Flatten(n.Children, n.Key);
                    }
                }
                Flatten(t.Roots, null);
                if (flat.Count == 0) throw new ArgumentException("a tree with no nodes shows nothing");
                plan.StaticTree = UiDataRegistry.Tree(fields, flat).Item1;
            }
            catch (Exception e) when (e is ArgumentException or UiDataRegistry.UiDataRefusal) { throw Fail(ctx, $"{path}.roots", e.Message); }
        }
        if (t.NodeRows is { Count: > 0 } nodeRows)
        {
            if (nodeRows.Count > MaxNodeRows) throw Fail(ctx, $"{path}.nodeRows", $"{nodeRows.Count} node rows is more than {MaxNodeRows}");
            var nodeScope = ScopeOf(ctx).With(nodeFields: fields);
            plan.NodeRows = InScope(nodeScope, () =>
            {
                var list = new List<RowPlan>();
                for (var r = 0; r < nodeRows.Count; r++)
                {
                    if (nodeRows[r] is not (TextRow or ValueRow)) throw Fail(ctx, $"{path}.nodeRows[{r}]", "node rows are text and value rows only (no inputs, buttons or containers)");
                    list.Add(Row(ctx, low, owner, nodeRows[r], r, $"{path}.nodeRows[{r}]", wc, depth + 1));
                }
                return list;
            });
        }
        if (t.Selected != null) plan.Selected = StateString(ctx, t.Selected, $"{path}.selected");
        if (t.Expanded != null) plan.Expanded = StateString(ctx, t.Expanded, $"{path}.expanded", set: true);
    }

    /// <summary>A static tree's JSON values in its fields' types (a JSON number reads as int64 or decimal; a string as any number type).</summary>
    private static IReadOnlyDictionary<string, UiScalar>? Coerce(IReadOnlyList<UiField> fields, IReadOnlyDictionary<string, UiScalar>? values)
    {
        if (values == null) return null;
        var r = new Dictionary<string, UiScalar>(StringComparer.Ordinal);
        foreach (var (k, v) in values)
        {
            var f = fields.FirstOrDefault(x => x.Id == k);
            r[k] = f == null || v == null || v.Type == f.Type ? v! : (f.Type, v) switch
            {
                (UiScalarType.Int64, UiScalar.String s) when UiCanonical.TryInt64(s.Value, out var l) => new UiScalar.Int64(l),
                (UiScalarType.UInt64, UiScalar.Int64 l) when l.Value >= 0 => new UiScalar.UInt64((ulong)l.Value),
                (UiScalarType.UInt64, UiScalar.String s) when ulong.TryParse(s.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var u) => new UiScalar.UInt64(u),
                (UiScalarType.Decimal, UiScalar.Int64 l) => new UiScalar.Decimal(l.Value),
                (UiScalarType.Decimal, UiScalar.String s) when UiCanonical.TryDecimal(s.Value, out var m) => new UiScalar.Decimal(m),
                (UiScalarType.Float64, UiScalar.Int64 l) => new UiScalar.Float64(l.Value),
                (UiScalarType.Float64, UiScalar.Decimal m) => new UiScalar.Float64((double)m.Value),
                _ => v,
            };
        }
        return r;
    }

    /// <summary>A slider on an int64 state entry (bounds inside int32) or a decimal one (bounds inside ±1,000,000).</summary>
    private static void StateSliderRange(BlockContext ctx, RowPlan plan, SliderRow s, string path)
    {
        decimal lo, hi;
        if (plan.Bind!.State is UiInt64State l)
        {
            lo = s.Min is { } a ? (decimal)a : l.Min;
            hi = s.Max is { } b ? (decimal)b : l.Max;
            if (lo != Math.Floor(lo) || hi != Math.Floor(hi) || lo < int.MinValue / 2 || hi > int.MaxValue / 2)
                throw Fail(ctx, $"{path}.min", $"{plan.Bind.Name}: a slider on an int64 entry needs whole bounds inside the slider's range (±{int.MaxValue / 2}): give min and max, or use a set action");
            if (lo < l.Min || hi > l.Max) throw Fail(ctx, $"{path}.min", $"{lo}..{hi} is wider than {plan.Bind.Name}'s range {l.Min}..{l.Max}");
        }
        else
        {
            var m = (UiDecimalState)plan.Bind.State!;
            lo = s.Min is { } a ? (decimal)a : m.Min;
            hi = s.Max is { } b ? (decimal)b : m.Max;
            if (lo < -1_000_000m || hi > 1_000_000m) throw Fail(ctx, $"{path}.min", $"{plan.Bind.Name}: a slider on a decimal entry works within -1000000..1000000 (it is approximate input); give min and max, or use a set action");
            if (lo < m.Min || hi > m.Max) throw Fail(ctx, $"{path}.min", $"{UiCanonical.Text(lo)}..{UiCanonical.Text(hi)} is wider than {plan.Bind.Name}'s range");
            if ((float)lo >= (float)hi) throw Fail(ctx, $"{path}.min", $"{UiCanonical.Text(lo)}..{UiCanonical.Text(hi)} has no distinct ends at a slider's precision");
        }
        if (lo >= hi) throw Fail(ctx, $"{path}.min", $"{lo}..{hi} is not a range");
        plan.Min = (double)lo;
        plan.Max = (double)hi;
    }

    private static void SliderRange(BlockContext ctx, RowPlan plan, SliderRow s, string path)
    {
        var b = plan.Bind!;
        var (lo, hi) = (s.Min ?? b.Min, s.Max ?? b.Max);
        if (lo == null || hi == null)
            throw Fail(ctx, $"{path}.{(lo == null ? "min" : "max")}", $"{b.Name} has no {(lo == null ? "minimum" : "maximum")} of its own (a Range on the config field): give min and max here");
        if (!(double.IsFinite(lo.Value) && double.IsFinite(hi.Value)) || lo >= hi) throw Fail(ctx, $"{path}.min", $"{lo}..{hi} is not a range");
        // the slider stops at a soft side by default (s.Min ?? b.Min), and may run past it: the config takes such values
        if ((b.HardMin is { } blo && lo < blo) || (b.HardMax is { } bhi && hi > bhi))
            throw Fail(ctx, $"{path}.{(s.Min != null && lo < b.HardMin ? "min" : "max")}", $"{lo}..{hi} is wider than {b.Name}'s own range {b.HardMin?.ToString(CultureInfo.InvariantCulture) ?? "-"}..{b.HardMax?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
        // ImGui's SliderBehavior requires half the integer/float range, even when the endpoints' span is small.
        if (plan.IsInt && (lo != Math.Floor(lo.Value) || hi != Math.Floor(hi.Value) || lo < int.MinValue / 2 || hi > int.MaxValue / 2)) throw Fail(ctx, $"{path}.min", $"{b.Name} takes whole numbers: {lo}..{hi} is not a whole-number range supported by the slider");
        if (!plan.IsInt && (lo < -float.MaxValue / 2 || hi > float.MaxValue / 2 || (float)lo >= (float)hi)) throw Fail(ctx, $"{path}.min", $"{lo}..{hi} is outside the slider's finite decimal range or has no distinct endpoints at its precision");
        plan.Min = lo.Value;
        plan.Max = hi.Value;
    }

    private static JsonNode DescribeRow(RowPlan r)
    {
        var o = new JsonObject { ["type"] = r.Type };
        if (r.SameLine) o["sameLine"] = true;
        if (r.Text != null) o["text"] = r.Text.Text.For("en");
        if (r.Label != null) o["label"] = r.Label.Text.For("en");
        if (r.Bind != null) o["bind"] = r.Bind.Name;
        if (r.Type == "slider") o["range"] = new JsonArray(r.Min, r.Max);
        if (r.Type == "choice") o["options"] = new JsonArray(r.Options.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        if (r.When != null) o["when"] = r.When.Source;
        if (r.Does != null) o["does"] = r.Does.Describe();
        if (r.Authored) o["id"] = r.Id;
        if (r.Items != null) o["items"] = r.Items.Name;
        if (r.Columns.Count > 0) o["columns"] = new JsonArray(r.Columns.Select(col => (JsonNode?)JsonValue.Create(col.Id)).ToArray());
        if (r.Selected != null) o["selected"] = r.Selected.Name;
        if (r.Expanded != null) o["expanded"] = r.Expanded.Name;
        if (r.Nodes != null) o["nodes"] = r.Nodes.Name;
        if (r.StaticTree != null) o["nodeCount"] = r.StaticTree.Nodes.Length;
        if (r.Tabs.Count > 0) o["tabs"] = new JsonArray(r.Tabs.Select(t => (JsonNode?)new JsonObject { ["id"] = t.Id, ["rows"] = new JsonArray(t.Rows.Select(DescribeRow).ToArray()) }).ToArray());
        if (r.NodeRows.Count > 0) o["nodeRows"] = new JsonArray(r.NodeRows.Select(DescribeRow).ToArray());
        if (r.Type == "input") { o["maxBytes"] = r.MaxBytes; o["commit"] = r.Commit.ToString().ToLowerInvariant(); if (r.Multiline) o["multiline"] = true; }
        return o;
    }

    // ---- HUD lines -------------------------------------------------------------------------------------------------

    public static BlockHandle DefineHud(BlockContext ctx, Lowered low, string name, HudLineBlock h)
    {
        ctx.Need(ClientsLevel.Mod, null, "a HUD line is drawn by each player's game from the mod's own file: the server cannot send one");
        var mod = ctx.Mod.Id;
        var text = Line(ctx, h.Text, "text", templated: true, required: true, "a HUD line");
        var when = When(ctx, h.When, "when");
        Refresh(ctx, h.Refresh, "refresh");
        var color = ColorOf(ctx, h.Color, "color", Color.White);
        if (!Enum.IsDefined(h.Anchor)) throw Fail(ctx, "anchor", $"{h.Anchor} is not an anchor: {string.Join(", ", Enum.GetNames<HudAnchor>().Select(n => char.ToLowerInvariant(n[0]) + n[1..]))}");
        var id = $"{mod}:{name}";
        var plan = new HudPlan(mod, name, id, h, text, when, color);
        low.Json["hudLine"] = new JsonObject
        {
            ["id"] = id, ["text"] = h.Text.For("en"), ["anchor"] = h.Anchor.ToString(), ["when"] = h.When, ["refresh"] = h.Refresh,
            ["reads"] = new JsonArray(text.Bindings.Concat(when?.Bindings ?? []).DistinctBy(b => b.Name).Select(b => (JsonNode?)JsonValue.Create(b.Name)).ToArray()),
        };
        low.AfterCommit(() => UiRuntime.Register(plan));
        return new BlockHandle<HudLineBlock>(mod, name, BlockIds.Derive(mod, "hudline", name), id, h);
    }

    // ---- key bindings ----------------------------------------------------------------------------------------------

    public static BlockHandle DefineKey(BlockContext ctx, Lowered low, string name, KeyBindingBlock k)
    {
        ctx.Need(ClientsLevel.Mod, null, "a key is bound on each player's game from the mod's own file: the server cannot bind one");
        var mod = ctx.Mod.Id;
        if (string.IsNullOrWhiteSpace(k.Key)) throw Fail(ctx, "key", "needs a default key: \"F8\", \"Ctrl+K\", or \"None\"");
        if (!KeyChord.TryParse(k.Key, out var chord)) throw Fail(ctx, "key", $"unknown key '{k.Key}' (examples: F8, Ctrl+K, Shift+Insert, Numpad5, None)");
        if (k.Description != null) Line(ctx, k.Description, "description", templated: false, required: true, "a description");
        if (k.Category != null && string.IsNullOrWhiteSpace(k.Category)) throw Fail(ctx, "category", "a category with no name: leave it out for the mod's name");
        var does = Action(ctx, low, name, k.Does, "does");
        var id = $"{mod}:{k.Was ?? name}";
        var plan = new KeyPlan(mod, name, id, k, chord, does);
        low.Json["keyBinding"] = new JsonObject { ["id"] = id, ["key"] = k.Key.Trim(), ["does"] = does.Describe(), ["onGameScreens"] = k.OnGameScreens, ["whileCapturing"] = k.WhileCapturing };
        low.AfterCommit(() => UiRuntime.Register(plan));
        return new BlockHandle<KeyBindingBlock>(mod, name, BlockIds.Derive(mod, "keybinding", k.Was ?? name), id, k);
    }
}

/// <summary>Only the hidden abilities UI actions generated. Regular ability blocks keep their existing ownership.</summary>
internal static class UiOwnedAbilities
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Dictionary<string, UiLifetime>> Owned = new(StringComparer.Ordinal);

    internal static void Track(string mod, AbilityHandle ability)
    {
        var id = ability.Definition.Id;
        var lifetime = new UiLifetime(mod);
        UiLifetime? old;
        lock (Gate)
        {
            if (!Owned.TryGetValue(mod, out var entries)) Owned[mod] = entries = new(StringComparer.Ordinal);
            entries.Remove(id, out old);
            entries[id] = lifetime;
        }
        old?.Dispose();
        lifetime.WhenRetired(() =>
        {
            AbilityRuntime.Remove(id, ability);
            lock (Gate)
                if (Owned.TryGetValue(mod, out var entries) && entries.TryGetValue(id, out var current) && ReferenceEquals(current, lifetime))
                {
                    entries.Remove(id);
                    if (entries.Count == 0) Owned.Remove(mod);
                }
        });
    }

    internal static void Forget(string mod)
    {
        Dictionary<string, UiLifetime>? entries;
        lock (Gate) if (!Owned.Remove(mod, out entries)) return;
        foreach (var lifetime in entries.Values) lifetime.Dispose();
    }
}
