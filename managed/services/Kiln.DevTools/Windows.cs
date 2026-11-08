// SPDX-License-Identifier: MIT
// The SET-parity windows (Bindings, Resources, Hooks, Game Log, Status, Console) and the /devtools info commands.
// Drawing reads the views' snapshots only; anything that changes state goes through ChangeOnTick; gathering happens in
// PanelScheduler on the tick thread while a window is open (Views.cs).
using System.Globalization;

namespace Kiln;

public static partial class KilnDevTools
{
    internal static readonly BindingsView Bindings = new();
    internal static readonly ResourcesView Resources = new();
    internal static readonly HooksView HooksPanel = new();
    internal static readonly GameLogView GameLog = new();
    internal static readonly StatusView Status = new();
    internal static readonly ConsoleView ConsoleV = new();
    private static readonly List<(DevView View, UiWindow Window)> Panels = new();

    /// <summary>Tick thread: refreshes the views of open windows (closed windows cost one bool read each).</summary>
    internal static void PumpPanels()
    {
        if (Panels.Count == 0) return;
        PanelScheduler.Pump(Panels.Select(p => (p.View, p.Window.Open)), Environment.TickCount64);
    }

    /// <summary>Text forms of the views for the console and servers: /devtools bindings|hooks|status|log|resources.</summary>
    private static LiteralNode InfoCommands(LiteralNode root) => root
        .Then(Commands.Literal("bindings").Executes(c => { Bindings.Refresh(); c.Reply(BindingsView.Describe(Bindings.Rows)); }))
        .Then(Commands.Literal("hooks").Executes(c => { HooksPanel.Refresh(); c.Reply(HooksPanel.Text); }))
        .Then(Commands.Literal("status").Executes(c => c.Reply(StatusText())))
        .Then(Commands.Literal("log").Executes(c => c.Reply(LogText(40)))
            .Then(Commands.Argument("count", Arg.Int(1, 500)).Executes(c => c.Reply(LogText(c.Get<int>("count"))))))
        .Then(Commands.Literal("resources").Then(Commands.Argument("type", Arg.Word()).Executes(c => c.Reply(ResourcesText(c.Get<string>("type"))))));

    internal static string StatusText()
    {
        Status.Refresh();
        var lines = new List<string> { $"{Status.Files.Count} state file(s) in {Status.Dir}" };
        foreach (var f in Status.Files)
        {
            lines.Add($"{f.Level,-5} {f.Name} ({f.Written:u})");
            lines.AddRange(f.Lines.Where(l => l.Level >= RowLevel.Warn).Take(10).Select(l => $"      {l.Level}: {l.Text}"));
        }
        return string.Join('\n', lines);
    }

    internal static string LogText(int count)
    {
        var v = new GameLogView();
        v.Refresh();
        var lines = v.Lines;
        return $"{v.Path}: last {Math.Min(count, lines.Length)} line(s)\n" + string.Join('\n', lines.Skip(Math.Max(0, lines.Length - count)).Select(l => l.Text));
    }

    internal static string ResourcesText(string type)
    {
        var v = new ResourcesView();
        v.Load(type);
        return v.Message + string.Concat(v.Rows.Take(40).Select(r => "\n" + r.Label)) + (v.Rows.Count > 40 ? "\n..." : "");
    }

    private static void CreatePanelWindows()
    {
        Add("bindings", "Kiln: Bindings", Bindings, DrawBindings, 640, 480);
        Add("resources", "Kiln: Resources", Resources, DrawResources, 820, 560);
        Add("hooks", "Kiln: Hooks", HooksPanel, DrawHooks, 420, 200);
        Add("log", "Kiln: Game Log", GameLog, DrawLog, 820, 480);
        Add("status", "Kiln: Status", Status, DrawStatus, 640, 520);
        Add("console", "Kiln: Console", ConsoleV, DrawConsole, 720, 420);
    }

    private static readonly Dictionary<string, UiWindow> WindowsById = new();

    private static void Add(string id, string title, DevView view, Action<UiDraw> draw, float w, float h)
    {
        var win = Ui.Window(title, draw, new UiWindowOptions { Width = w, Height = h, StartOpen = false });
        Panels.Add((view, win));
        WindowsById[id] = win;
        KeyBindings.Register("kiln.devtools." + id, "None", () => win.Toggle(), title + " window");
    }

    private static Color ColorOf(RowLevel l) => l switch
    {
        RowLevel.Ok => Color.Green, RowLevel.Warn => Color.Yellow, RowLevel.Error => Color.Red, _ => Color.White,
    };

    // -------------------------------------------------------------------------------------------- Bindings ----
    private static string _bindFilter = "", _bindEdit = "";
    private static string? _bindSelected;

    private static void DrawBindings(UiDraw ui)
    {
        ui.InputText("Filter", ref _bindFilter, 64);
        if (_bindSelected != null)
        {
            ui.Text("Key for " + _bindSelected + ":");
            ui.SameLine();
            ui.InputText("##key", ref _bindEdit, 32);
            ui.SameLine();
            var id = _bindSelected;
            var key = _bindEdit;
            if (ui.Button("Set")) ChangeOnTick(() => KilnLog.Info("devtools: " + BindingsView.Rebind(id, key)));
            ui.SameLine();
            if (ui.Button("Unbind")) ChangeOnTick(() => BindingsView.Rebind(id, "None"));
            ui.SameLine();
            if (ui.Button("Default")) ChangeOnTick(() => BindingsView.Rebind(id, "default"));
        }
        if (!ui.BeginTable("bindings", "Mod", "Binding", "Key", "Description")) return;
        foreach (var r in Bindings.Rows)
        {
            if (!TextFilter.Matches($"{r.Category} {r.Id} {r.Key} {r.Description}", _bindFilter)) continue;
            ui.NextRow();
            ui.Text(r.Category);
            ui.NextColumn();
            if (ui.Selectable(r.Id, r.Id == _bindSelected)) { _bindSelected = r.Id; _bindEdit = r.Key; }
            ui.NextColumn();
            ui.Text(r.Conflict != null ? Color.Red : r.Changed ? Color.Cyan : Color.White, r.Key);
            if (r.Conflict != null && ui.ItemHovered) ui.Tooltip("Also bound: " + r.Conflict);
            ui.NextColumn();
            if (r.Enabled) ui.Text(r.Description); else ui.TextDisabled(r.Description);
        }
        ui.EndTable();
    }

    // ------------------------------------------------------------------------------------------- Resources ----
    private static string _resType = "", _resFilter = "";
    private static ResourceRow? _resSelected;

    private static void DrawResources(UiDraw ui)
    {
        if (ui.InputText("Type", ref _resType, 128)) Resources.SuggestText = _resType;
        ui.SameLine();
        if (ui.Button("List")) { Resources.Request(_resType); _resSelected = null; }
        foreach (var s in Resources.Suggestions)
            if (ui.SmallButton(s)) { _resType = s; Resources.Request(s); _resSelected = null; }
        ui.TextWrapped(Resources.Message);
        ui.InputText("Filter", ref _resFilter, 64);
        if (ui.BeginChild("reslist", 330 * ui.Scale, 0))
        {
            var shown = 0;
            foreach (var r in Resources.Rows)
            {
                if (!TextFilter.Matches(r.Label, _resFilter)) continue;
                if (++shown > 2000) { ui.TextDisabled("... (filter to see more)"); break; }
                if (ui.Selectable(r.Label, r == _resSelected)) { _resSelected = r; Resources.Select(r); }
            }
        }
        ui.EndChild();
        ui.SameLine();
        if (ui.BeginChild("resdetail", 0, 0))
        {
            if (_resSelected is { } sel && ui.SmallButton("Log GUID")) KilnLog.Info($"devtools: resource {sel.Guid} part {sel.Part} type 0x{Resources.TypeHash:x8}");
            ui.Text(Resources.DetailText);
        }
        ui.EndChild();
    }

    // ----------------------------------------------------------------------------------------------- Hooks ----
    private static void DrawHooks(UiDraw ui) => ui.Text(HooksPanel.Text);

    // -------------------------------------------------------------------------------------------- Game log ----
    private static string _logFilter = "";
    private static bool _logNoise = true;

    private static void DrawLog(UiDraw ui)
    {
        ui.InputText("Filter", ref _logFilter, 96);
        ui.SameLine();
        ui.Checkbox("Hide noise", ref _logNoise);
        ui.SameLine();
        if (ui.Button("Clear")) ChangeOnTick(GameLog.Clear);
        ui.TextDisabled(GameLog.Path);
        if (ui.BeginChild("log", 0, 0))
            foreach (var l in GameLogView.Filtered(GameLog.Lines, _logFilter, _logNoise))
                ui.Text(ColorOf(l.Level), l.Text);
        ui.EndChild();
    }

    // ---------------------------------------------------------------------------------------------- Status ----
    private static void DrawStatus(UiDraw ui)
    {
        if (Status.Files.Count == 0) { ui.TextWrapped("No state files in " + Status.Dir); return; }
        foreach (var f in Status.Files)
        {
            ui.Text(ColorOf(f.Level), f.Level == RowLevel.Ok ? "OK " : f.Level == RowLevel.Warn ? "(!)" : f.Level == RowLevel.Error ? "ERR" : "   ");
            ui.SameLine();
            if (!ui.CollapsingHeader($"{f.Name}  {f.Written.ToString("u", CultureInfo.InvariantCulture)}", f.Level >= RowLevel.Warn)) continue;
            foreach (var (level, text) in f.Lines) ui.Text(ColorOf(level), text);
        }
    }

    // --------------------------------------------------------------------------------------------- Console ----
    private static string _conLine = "", _conFilter = "";

    private static void DrawConsole(UiDraw ui)
    {
        ui.InputText("##line", ref _conLine, 512);
        ui.SameLine();
        if (ui.Button("Run"))
        {
            var line = _conLine;
            _conLine = "";
            ChangeOnTick(() => ConsoleV.Submit(line));
        }
        ui.SameLine();
        if (ui.Button("Clear")) ChangeOnTick(ConsoleV.Clear);
        ui.SameLine();
        ui.InputText("Filter", ref _conFilter, 64);
        var hist = ConsoleV.History;
        if (hist.Length > 0)
        {
            ui.TextDisabled("History:");
            for (var i = hist.Length - 1; i >= Math.Max(0, hist.Length - 5); i--)
            {
                ui.SameLine();
                if (ui.SmallButton(hist[i] + "##h" + i)) _conLine = hist[i];
            }
        }
        if (ui.BeginChild("out", 0, 0))
            foreach (var l in ConsoleV.Output)
                if (TextFilter.Matches(l.Text, _conFilter)) ui.Text(ColorOf(l.Level), l.Text);
        ui.EndChild();
    }
}
