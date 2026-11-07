// SPDX-License-Identifier: MIT
// Kiln's developer tools over kiln.types (docs/kiln/services/types.md): the game's types, systems and components from
// commands, and the Systems and Types windows in the game. Switching systems and writing components change the world:
// those commands are Admin. Everything turns itself off when kiln.types is unavailable.
using System.Globalization;
using System.Text;

namespace Kiln;

/// <summary>Started at the first service tick (or by <see cref="Start"/>): registers the commands and, in the game,
/// the windows.</summary>
public static class KilnDevTools
{
    private static int _started;
    private static UiWindow? _systems, _types;

    /// <summary>Registers the commands (once). Safe to call from any mod.</summary>
    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        RegisterCommands();
        if (Ui.Available) CreateWindows();
    }

    private static void RegisterCommands()
    {
        Commands.Register(Commands.Literal("types").Describe("lists the game's reflected types whose name holds every word")
            .Then(Commands.Argument("words", Arg.Greedy()).Executes(c => c.Reply(ListTypes(c.Get<string>("words"))))), CommandPermission.Everyone);
        Commands.Register(Commands.Literal("type").Describe("a reflected type's fields (offset, type) or enum values")
            .Then(Commands.Argument("name", Arg.Word()).Executes(c => c.Reply(DescribeType(c.Get<string>("name"))))), CommandPermission.Everyone);
        Commands.Register(Commands.Literal("systems").Describe("the game's ECS systems: list, profile, top, off, allon")
            .Executes(c => c.Reply(ListSystems("")))
            .Then(Commands.Literal("off").Executes(c => c.Reply(OffSystems())))
            .Then(Commands.Literal("allon").Requires(CommandPermission.Admin).Executes(c => c.Reply($"{GameSystems.TurnAllOn()} system(s) switched back on")))
            .Then(Commands.Literal("profile").Requires(CommandPermission.Admin)
                .Then(Commands.Literal("on").Executes(c => { GameSystems.Profile(true); c.Reply("profiling every system from now"); }))
                .Then(Commands.Literal("off").Executes(c => { GameSystems.Profile(false); c.Reply(Top(30)); })))
            .Then(Commands.Literal("top").Executes(c => c.Reply(Top(30)))
                .Then(Commands.Argument("count", Arg.Int(1, 500)).Executes(c => c.Reply(Top(c.Get<int>("count"))))))
            .Then(Commands.Literal("find").Then(Commands.Argument("words", Arg.Greedy()).Executes(c => c.Reply(ListSystems(c.Get<string>("words")))))),
            CommandPermission.Everyone);
        Commands.Register(Commands.Literal("system").Requires(CommandPermission.Admin)
            .Describe("switches one of the game's systems off or on, in every world, until switched back or restart")
            .Then(Commands.Argument("name", Arg.Word())
                .Then(Commands.Literal("on").Executes(c => c.Reply(Switch(c.Get<string>("name"), true))))
                .Then(Commands.Literal("off").Executes(c => c.Reply(Switch(c.Get<string>("name"), false))))), CommandPermission.Admin);
        Commands.Register(Commands.Literal("component").Describe("reads (or, for admins, writes) an entity's component by name")
            .Then(Commands.Argument("entity", Arg.Int(1, int.MaxValue))
                .Then(Commands.Argument("component", Arg.Word())
                    .Executes(c => c.Reply(ReadComponent(c.Get<int>("entity"), c.Get<string>("component"), null)))
                    .Then(Commands.Argument("path", Arg.Word())
                        .Executes(c => c.Reply(ReadComponent(c.Get<int>("entity"), c.Get<string>("component"), c.Get<string>("path"))))
                        .Then(Commands.Argument("value", Arg.Word()).Requires(CommandPermission.Admin)
                            .Executes(c => c.Reply(WriteComponent(c.Get<int>("entity"), c.Get<string>("component"), c.Get<string>("path"), c.Get<string>("value")))))))),
            CommandPermission.Everyone);
    }

    internal static string ListTypes(string words)
    {
        if (!GameTypes.Available) return "the game's types are unavailable: " + GameTypes.Status;
        var hits = GameTypes.Search(words).ToList();
        var sb = new StringBuilder($"{hits.Count} type(s)");
        foreach (var t in hits.Take(40)) sb.Append('\n').Append($"{t.QualifiedName} ({t.Kind}, {t.Size} B)");
        if (hits.Count > 40) sb.Append("\n...");
        return sb.ToString();
    }

    internal static string DescribeType(string name)
    {
        var t = GameTypes.Find(name);
        if (t == null) return $"no type '{name}' (or several share the name: use the qualified name)";
        var sb = new StringBuilder($"{t.QualifiedName}: {t.Kind}, {t.Size} B, hash 0x{t.Hash:x8}");
        if (t.Base is { } b) sb.Append(", base ").Append(b.QualifiedName);
        if (t.Defaults != null) sb.Append(", has defaults");
        foreach (var f in t.AllFields) sb.Append('\n').Append($"  +0x{f.Offset:x}  {f.Name}: {f.Type.QualifiedName}");
        foreach (var (n, v) in t.EnumValues) sb.Append('\n').Append($"  {n} = {v}");
        return sb.ToString();
    }

    internal static string ListSystems(string words)
    {
        var all = (words.Length == 0 ? GameSystems.All : GameSystems.Search(words)).ToList();
        if (all.Count == 0) return GameSystems.All.Count == 0 ? "the game's systems are unavailable: " + GameTypes.Status : "no system matches";
        var sb = new StringBuilder($"{all.Count} system(s){(GameSystems.Available ? "" : " (switching unavailable)")}");
        foreach (var s in all.Take(60)) sb.Append('\n').Append($"{(s.Enabled ? "  " : "x ")}{s.Name}  exe+{s.Rva:x}");
        if (all.Count > 60) sb.Append("\n...");
        return sb.ToString();
    }

    internal static string OffSystems()
    {
        var off = GameSystems.All.Where(s => !s.Enabled).ToList();
        return off.Count == 0 ? "every system is on" : $"{off.Count} off: " + string.Join(", ", off.Select(s => s.Name));
    }

    internal static string Top(int n)
    {
        var stats = GameSystems.Stats();
        var secs = Math.Max(GameSystems.ProfileSeconds, 1e-9);
        if (stats.Count == 0) return GameSystems.Profiling ? "no system ran yet" : "the profiler has not run: /systems profile on";
        var sb = new StringBuilder($"top {Math.Min(n, stats.Count)} of {stats.Count} systems over {secs:F1} s (CPU ms per second, calls per second, world)");
        foreach (var s in stats.Take(n))
            sb.Append('\n').Append(string.Create(CultureInfo.InvariantCulture, $"{s.Milliseconds / secs,8:F3} ms/s {s.Calls / secs,8:F1}/s  {s.World,-6} {s.Name}"));
        return sb.ToString();
    }

    internal static string Switch(string name, bool on)
    {
        GameSystems.SetEnabled(name, on);
        KilnLog.Info($"devtools: system {name} switched {(on ? "on" : "off")}");
        return $"{name} is {(on ? "on" : "off")}{(on ? "" : ": the game skips it in every world until /system " + name + " on or a restart")}";
    }

    internal static string ReadComponent(int entity, string component, string? path)
    {
        var v = Reflected.Component(new EntityId((uint)entity), component);
        if (v == null) return $"entity {entity} has no {component}";
        return path == null ? $"{v.Type.QualifiedName} of {entity}:\n{v.Describe()}" : $"{path} = {v[path]}";
    }

    internal static string WriteComponent(int entity, string component, string path, string value)
    {
        var v = Reflected.Component(new EntityId((uint)entity), component) ?? throw new CommandException($"entity {entity} has no {component}");
        object parsed = value.Equals("true", StringComparison.OrdinalIgnoreCase) ? true
            : value.Equals("false", StringComparison.OrdinalIgnoreCase) ? false
            : long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l
            : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : value;
        var before = v[path];
        try
        {
            v[path] = parsed;
        }
        catch (ArgumentException e)
        {
            throw new CommandException(e.Message);
        }
        KilnLog.Info($"devtools: {component}.{path} of {entity}: {before} -> {v[path]}");
        return $"{path}: {before} -> {v[path]}";
    }

    // ------------------------------------------------------------------------------------------------ windows ----
    private static string _sysFilter = "", _typeFilter = "";
    private static bool _offOnly;
    private static GameType? _selected;

    private static void CreateWindows()
    {
        _systems = Ui.Window("Kiln: Systems", DrawSystems, new UiWindowOptions { Width = 560, Height = 520, StartOpen = false });
        _types = Ui.Window("Kiln: Types", DrawTypes, new UiWindowOptions { Width = 720, Height = 560, StartOpen = false });
        KeyBindings.Register("kiln.devtools.systems", "None", () => _systems.Toggle(), "Kiln: Systems window");
        KeyBindings.Register("kiln.devtools.types", "None", () => _types.Toggle(), "Kiln: Types window");
        Commands.Register(Commands.Literal("devtools").Describe("opens Kiln's Systems or Types window (in the game)")
            .Then(Commands.Literal("systems").Executes(c => { _systems.Toggle(); c.Reply("Systems window toggled"); }))
            .Then(Commands.Literal("types").Executes(c => { _types.Toggle(); c.Reply("Types window toggled"); })), CommandPermission.Everyone);
    }

    private static void DrawSystems(UiDraw ui)
    {
        if (!GameSystems.Available) { ui.TextWrapped("Systems are unavailable on this build: " + GameTypes.Status); return; }
        ui.InputText("Filter", ref _sysFilter, 64);
        ui.SameLine();
        ui.Checkbox("Off only", ref _offOnly);
        var profiling = GameSystems.Profiling;
        if (ui.Button(profiling ? "Stop profiling" : "Profile")) GameSystems.Profile(!profiling);
        ui.SameLine();
        if (ui.Button("Turn all on")) GameSystems.TurnAllOn();
        ui.SameLine();
        if (ui.Button("Log top 30")) KilnLog.Info(Top(30));
        var secs = Math.Max(GameSystems.ProfileSeconds, 1e-9);
        IEnumerable<GameSystem> list = _sysFilter.Length == 0 ? GameSystems.All : GameSystems.Search(_sysFilter);
        if (_offOnly) list = list.Where(s => !s.Enabled);
        if (profiling || GameSystems.ProfileSeconds > 0) list = list.OrderByDescending(s => s.Milliseconds);
        if (ui.BeginTable("systems", "On", "System", "ms/s", "calls/s", "World", "exe+"))
        {
            foreach (var s in list.Take(400))
            {
                ui.NextRow();
                var on = s.Enabled;
                if (ui.Checkbox("##" + s.Name, ref on)) GameSystems.SetEnabled(s.Name, on);
                ui.NextColumn();
                ui.Text(s.Name);
                ui.NextColumn();
                ui.Text(s.Calls == 0 ? "" : (s.Milliseconds / secs).ToString("F3", CultureInfo.InvariantCulture));
                ui.NextColumn();
                ui.Text(s.Calls == 0 ? "" : (s.Calls / secs).ToString("F1", CultureInfo.InvariantCulture));
                ui.NextColumn();
                ui.Text(s.World);
                ui.NextColumn();
                ui.Text(s.Rva.ToString("x", CultureInfo.InvariantCulture));
            }
            ui.EndTable();
        }
    }

    private static void DrawTypes(UiDraw ui)
    {
        if (!GameTypes.Available) { ui.TextWrapped("Types are unavailable on this build: " + GameTypes.Status); return; }
        ui.InputText("Search", ref _typeFilter, 96);
        if (ui.BeginChild("list", 300 * ui.Scale, 0))
        {
            if (_typeFilter.Length >= 2)
                foreach (var t in GameTypes.Search(_typeFilter).Take(300))
                    if (ui.Selectable(t.QualifiedName, t == _selected)) _selected = t;
        }
        ui.EndChild();
        ui.SameLine();
        if (ui.BeginChild("detail", 0, 0))
        {
            if (_selected is { } s)
            {
                ui.Text(DescribeType(s.QualifiedName));
                if (s.Base is { } b && ui.Button("Base: " + b.Name)) _selected = b;
            }
        }
        ui.EndChild();
    }
}

/// <summary>Started at the first service tick by Kiln (the AutoStart list in Kiln.Api).</summary>
internal static class DevToolsStartup
{
    static DevToolsStartup()
    {
        try
        {
            KilnDevTools.Start();
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException or InvalidOperationException)
        {
            KilnLog.Warn($"devtools: not available: {e.Message}");
        }
    }
}
