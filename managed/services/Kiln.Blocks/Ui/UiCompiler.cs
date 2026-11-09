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

internal sealed record CommandPlan(string Line) : ActionPlan
{
    public override JsonObject Describe() => new() { ["command"] = Line };
}

internal sealed record TogglePlan(UiBinding Config) : ActionPlan
{
    public override JsonObject Describe() => new() { ["toggle"] = Config.Name };
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

internal sealed record WindowActionPlan(string Mod, string Window, bool Open) : ActionPlan
{
    public override JsonObject Describe() => new() { [Open ? "openWindow" : "closeWindow"] = $"{Mod}:{Window}" };
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

    public IEnumerable<UiBinding> Reads =>
        (Text?.Bindings ?? []).Concat(Tooltip?.Bindings ?? []).Concat(When?.Bindings ?? []).Concat(Bind != null ? [Bind] : []);
}

internal sealed class WindowPlan
{
    public required string Mod { get; init; }
    public required string Name { get; init; }
    public required WindowBlock Block { get; init; }
    public required UiLine Title { get; init; }
    public required IReadOnlyList<RowPlan> Rows { get; init; }
    public UiLine? MenuText { get; init; }

    public IReadOnlyList<UiBinding> Reads => Rows.SelectMany(r => r.Reads).DistinctBy(b => b.Name).ToList();
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

    private static UiBinding Bind(BlockContext ctx, string spec, string path) => Resolved(ctx, path, () => UiBindings.Resolve(ctx.Mod.Id, spec));

    private static UiLine Line(BlockContext ctx, UiText? text, string path, bool templated, bool required, string what)
    {
        if (text == null) throw Fail(ctx, path, $"{what} needs a text");
        if (required && text.Languages.Any(l => string.IsNullOrWhiteSpace(text.For(l)))) throw Fail(ctx, path, $"{what} needs a text players can read, in every language it gives");
        return Resolved(ctx, path, () => UiLine.Compile(text, s => UiBindings.Resolve(ctx.Mod.Id, s), templated));
    }

    private static UiCondition? When(BlockContext ctx, string? text, string path)
    {
        if (text == null) return null;
        if (string.IsNullOrWhiteSpace(text)) throw Fail(ctx, path, "an empty condition: leave it out, or say what must hold (game.inGame, !config.x, game.fps < 30)");
        return Resolved(ctx, path, () => UiCondition.Parse(text, s => UiBindings.Resolve(ctx.Mod.Id, s)));
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
        if (a == null) throw Fail(ctx, path, "needs an action: command, toggle, set, castAbility, applyEffect, openWindow, closeWindow or toast");
        var given = new List<string>();
        if (a.Command != null) given.Add("command");
        if (a.Toggle != null) given.Add("toggle");
        if (a.Set != null) given.Add("set");
        if (a.CastAbility != null) given.Add("castAbility");
        if (a.ApplyEffect != null) given.Add("applyEffect");
        if (a.OpenWindow != null) given.Add("openWindow");
        if (a.CloseWindow != null) given.Add("closeWindow");
        if (a.Toast != null) given.Add("toast");
        if (given.Count == 0) throw Fail(ctx, path, "an action does something: give it one of command, toggle, set, castAbility, applyEffect, openWindow, closeWindow, toast");
        if (given.Count > 1) throw Fail(ctx, path, $"an action does one thing, and this has {string.Join(" and ", given)}: use one button or key for each");

        if (a.Command is { } command)
        {
            var line = command.Trim().TrimStart('/').Trim();
            if (line.Length == 0) throw Fail(ctx, $"{path}.command", "an empty command line");
            if (line.Length > 256 || line.Contains('\n') || line.Contains('\r')) throw Fail(ctx, $"{path}.command", "a command is one line of at most 256 characters");
            return new CommandPlan(line);
        }
        if (a.Toggle is { } toggle)
        {
            var b = Bind(ctx, toggle, $"{path}.toggle");
            if (!b.IsConfig || b.Type != UiValueType.Bool) throw Fail(ctx, $"{path}.toggle", $"{b.Name} is not a true/false config entry: a toggle flips one");
            return new TogglePlan(b);
        }
        if (a.Set is { } set)
        {
            var b = Bind(ctx, set.Bind, $"{path}.set.bind");
            if (!b.IsConfig) throw Fail(ctx, $"{path}.set.bind", $"{b.Name} is a game value, which is read-only: set takes a config entry");
            return new SetPlan(b, ConfigValue(ctx, b, set.To, $"{path}.set.to"));
        }
        if (a.CastAbility is { } cast) return new CastPlan(AbilityRuntime.IdOf(ctx, cast, $"{path}.castAbility"));
        if (a.ApplyEffect is { } effect) return ApplyEffect(ctx, low, owner, effect, $"{path}.applyEffect");
        if (a.OpenWindow is { } open) return WindowAction(ctx, low, open, true, $"{path}.openWindow");
        if (a.CloseWindow is { } close) return WindowAction(ctx, low, close, false, $"{path}.closeWindow");
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
            : UiBindings.Normalize(to) ?? throw Fail(ctx, path, "needs a value");
        switch (b.EntryType)
        {
            case "bool":
                return v is bool ? v : throw Fail(ctx, path, $"{b.Name} is a true/false entry: give true or false");
            case "int" or "float":
                if (v is not double d || !double.IsFinite(d)) throw Fail(ctx, path, $"{b.Name} is a number: give one");
                if (b.EntryType == "int" && d != Math.Floor(d)) throw Fail(ctx, path, $"{b.Name} takes whole numbers: {d.ToString(CultureInfo.InvariantCulture)} has a fraction");
                if ((b.Min is { } lo && d < lo) || (b.Max is { } hi && d > hi)) throw Fail(ctx, path, $"{d.ToString(CultureInfo.InvariantCulture)} is outside {b.Name}'s range {b.Min?.ToString(CultureInfo.InvariantCulture) ?? "-"}..{b.Max?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
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

        var rows = new List<RowPlan>();
        for (var i = 0; i < w.Rows.Count; i++) rows.Add(Row(ctx, low, name, w.Rows[i], i));
        var plan = new WindowPlan { Mod = mod, Name = name, Block = w, Title = title, Rows = rows, MenuText = menuText };

        low.Json["window"] = new JsonObject
        {
            ["kind"] = w.Kind.ToString().ToLowerInvariant(), ["title"] = w.Title.For("en"), ["refresh"] = w.Refresh, ["openAtStart"] = w.OpenAtStart,
            ["menu"] = w.Menu is { } m ? new JsonObject { ["text"] = m.Text.For("en"), ["in"] = m.In.ToString().ToLowerInvariant(), ["order"] = m.Order } : null,
            ["rows"] = new JsonArray(rows.Select(DescribeRow).ToArray()),
            ["reads"] = new JsonArray(plan.Reads.Select(b => (JsonNode?)JsonValue.Create(b.Name)).ToArray()),
        };
        low.AfterCommit(() => UiRuntime.Register(plan));
        return new BlockHandle<WindowBlock>(mod, name, BlockIds.Derive(mod, "window", name), $"{mod}:{name}", w);
    }

    private static RowPlan Row(BlockContext ctx, Lowered low, string owner, UiRow? row, int index)
    {
        var path = $"rows[{index}]";
        if (row == null) throw Fail(ctx, path, "a row is an object with a \"type\": text, value, toggle, slider, choice, separator or button");
        if (row.SameLine && index == 0) throw Fail(ctx, $"{path}.sameLine", "the first row has no row before it to share a line with");
        string Type() => row switch { TextRow => "text", ValueRow => "value", ToggleRow => "toggle", SliderRow => "slider", ChoiceRow => "choice", SeparatorRow => "separator", _ => "button" };
        var plan = new RowPlan { Type = Type(), Id = $"r{index}", SameLine = row.SameLine };
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
                if (UiBindings.CheckFormat(plan.Bind.Type, v.Format) is { } problem) throw Fail(ctx, $"{path}.format", problem);
                plan.Format = v.Format;
                break;
            case ToggleRow g:
                plan.Label = Line(ctx, g.Label, $"{path}.label", templated: false, required: true, "a toggle");
                plan.Bind = Bind(ctx, g.Bind, $"{path}.bind");
                if (!plan.Bind.IsConfig || plan.Bind.Type != UiValueType.Bool) throw Fail(ctx, $"{path}.bind", $"{plan.Bind.Name} is not a true/false config entry: a toggle edits one (a value row shows any value)");
                break;
            case SliderRow s:
                plan.Label = Line(ctx, s.Label, $"{path}.label", templated: false, required: true, "a slider");
                plan.Bind = Bind(ctx, s.Bind, $"{path}.bind");
                if (!plan.Bind.IsConfig || plan.Bind.Type != UiValueType.Number) throw Fail(ctx, $"{path}.bind", $"{plan.Bind.Name} is not a whole-number or decimal config entry: a slider edits one");
                plan.IsInt = plan.Bind.EntryType == "int";
                SliderRange(ctx, plan, s, path);
                break;
            case ChoiceRow c:
                plan.Label = Line(ctx, c.Label, $"{path}.label", templated: false, required: true, "a choice");
                plan.Bind = Bind(ctx, c.Bind, $"{path}.bind");
                if (!plan.Bind.IsConfig || plan.Bind.EntryType != "enum" || plan.Bind.Options is not { Count: > 0 }) throw Fail(ctx, $"{path}.bind", $"{plan.Bind.Name} is not an enum config entry: a choice picks one of its options");
                plan.Options = plan.Bind.Options;
                break;
            case SeparatorRow s:
                if (s.Text != null) plan.Text = Line(ctx, s.Text, $"{path}.text", templated: true, required: true, "a separator's heading");
                break;
            case ButtonRow b:
                plan.Label = Line(ctx, b.Label, $"{path}.label", templated: false, required: true, "a button");
                plan.Does = Action(ctx, low, owner, b.Does, $"{path}.does");
                break;
            default:
                throw Fail(ctx, path, $"{row.GetType().Name} is not a row Kiln knows");
        }
        return plan;
    }

    private static void SliderRange(BlockContext ctx, RowPlan plan, SliderRow s, string path)
    {
        var b = plan.Bind!;
        var (lo, hi) = (s.Min ?? b.Min, s.Max ?? b.Max);
        if (lo == null || hi == null)
            throw Fail(ctx, $"{path}.{(lo == null ? "min" : "max")}", $"{b.Name} has no {(lo == null ? "minimum" : "maximum")} of its own (a Range on the config field): give min and max here");
        if (!(double.IsFinite(lo.Value) && double.IsFinite(hi.Value)) || lo >= hi) throw Fail(ctx, $"{path}.min", $"{lo}..{hi} is not a range");
        if ((b.Min is { } blo && lo < blo) || (b.Max is { } bhi && hi > bhi))
            throw Fail(ctx, $"{path}.{(s.Min != null && lo < b.Min ? "min" : "max")}", $"{lo}..{hi} is wider than {b.Name}'s own range {b.Min?.ToString(CultureInfo.InvariantCulture) ?? "-"}..{b.Max?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
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
