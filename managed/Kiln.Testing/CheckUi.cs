// SPDX-License-Identifier: MIT
namespace Kiln.Testing;

/// <summary>What the player did on the check screen.</summary>
internal enum UiActionKind
{
    Run,
    RunAll,
    Answer,
    Refresh,
}

internal sealed record UiAction(UiActionKind Kind, ViewItem? Item = null, AnswerKind Answer = AnswerKind.Pass, string? Note = null);

/// <summary>The screen's own state (render thread only): the note being typed after fail.</summary>
internal sealed class ScreenState
{
    public bool Noting;
    public string Note = "";
    public bool FocusNote;
    public bool OpenedForNote;
    public string? ConfirmKey;
    public DateTime ConfirmShown;

    /// <summary>A confirmation must be on screen this long before its yes counts (no click goes through by accident).</summary>
    public static readonly TimeSpan ConfirmDelay = TimeSpan.FromSeconds(1.5);
}

/// <summary>The runner's screen: the checks by area with each one's last verdict and refusal, the current instruction and
/// buttons, the guard's state. Drawn from a <see cref="RunnerView"/> alone, so the UI preview can draw it from sample
/// state.</summary>
internal static class CheckScreen
{
    /// <summary>Shown beside every group with checks tagged cheats: the guard covers the session, not the character.</summary>
    internal const string CheatsNote = "Checks tagged cheats change the character for good: use a throwaway character and delete it afterwards.";

    private static readonly Color Green = new(110, 200, 120), Red = new(235, 95, 85), Amber = new(235, 190, 90), Dim = new(150, 150, 150);

    public static void Draw(UiDraw ui, RunnerView v, ScreenState s, Action<UiAction> act, DateTime now, string keys = "")
    {
        ui.Text($"Guided checks · {SessionLabel(v.Session)}");
        ui.SameLine();
        ui.TextDisabled($"   {v.Passes} passed · {v.Fails} failed · {v.Skips} skipped · {v.NeverRun} never run");
        if (v.Changed is { } changed) ui.Text(Red, changed);
        ui.Text(v.Protecting ? Green : Dim, "Achievement guard: " + v.Guard);
        if (v.Warning is { } w) ui.Text(Red, "GUARD WARNING: " + w);
        if (v.Ledger is { } l) ui.Text(Red, "Ledger: " + l);
        if (v.Message is { } m) ui.TextDisabled(m);
        if (keys.Length > 0) ui.TextDisabled(keys);
        ui.Separator();

        if (v.Current is { } c) DrawCurrent(ui, c, s, act, now);
        else
        {
            s.Noting = false;
            ui.TextDisabled("No check is running. Pick one below, or run them all in order.");
            var runnable = v.Session is "hosted" or "joined" or "server";
            if (runnable && ui.Button("Run all")) act(new UiAction(UiActionKind.RunAll));
            if (v.Session == "joined")
            {
                ui.SameLine();
                if (ui.Button("Refresh")) act(new UiAction(UiActionKind.Refresh));
            }
        }
        if (v.Queued > 0) ui.TextDisabled($"{v.Queued} more queued");
        ui.Separator();

        if (ui.BeginChild("kiln.checks.list"))
        {
            if (v.Items.Count == 0) ui.TextDisabled(v.Session == "joined" ? "The server lists no checks." : "No loaded mod registered checks.");
            foreach (var area in v.Items.GroupBy(i => i.Area, StringComparer.OrdinalIgnoreCase))
            {
                var items = area.ToList();
                var done = items.Count(i => i.Verdict == "pass");
                if (!ui.CollapsingHeader($"{area.Key}  ({done}/{items.Count} passed)###area.{area.Key}", defaultOpen: true)) continue;
                if (items.Any(i => i.Cheats)) ui.Text(Amber, CheatsNote);
                foreach (var i in items)
                {
                    using var _ = ui.Id(i.Mod + "/" + i.Id);
                    var (tag, color) = i.Verdict switch
                    {
                        "pass" => ("PASS", Green),
                        "fail" => ("FAIL", Red),
                        "skip" => ("SKIP", Amber),
                        _ => ("----", Dim),
                    };
                    ui.Text(color, tag);
                    ui.SameLine();
                    ui.Text(i.Title);
                    ui.SameLine();
                    ui.TextDisabled($"{i.Mod}/{i.Id}" + (i.Tags.Count > 0 ? "  [" + string.Join(", ", i.Tags) + "]" : "") +
                                    (i.Needs != "any" ? $"  needs {i.Needs}" : ""));
                    if (i.Refusal is { } r) ui.TextWrapped("    refused: " + r);
                    else if (v.Current is null && v.Session is "hosted" or "joined" or "server")
                    {
                        ui.SameLine();
                        if (ui.SmallButton("Run")) act(new UiAction(UiActionKind.Run, i));
                    }
                    if (i.Verdict is "fail" or "skip" && i.Note is { Length: > 0 } note) ui.TextDisabled("    note: " + note);
                }
            }
        }
        ui.EndChild();
    }

    private static void DrawCurrent(UiDraw ui, ViewCurrent c, ScreenState s, Action<UiAction> act, DateTime now)
    {
        ui.Separator($"Now: {c.Title}");
        ui.TextDisabled($"{c.Mod}/{c.Id} · {c.Area}{(c.Cheats ? " · cheats" : "")}");
        if (c.Phase == "confirming" && c.Confirm is { } confirm)
        {
            var key = c.Mod + "/" + c.Id;
            if (s.ConfirmKey != key)
            {
                s.ConfirmKey = key;
                s.ConfirmShown = now;
            }
            ui.Text(Amber, "Before this check starts:");
            ui.TextWrapped(confirm);
            // "No" first: the focus a gamepad or Enter lands on first is never the yes
            if (ui.Button("No, do not run it")) act(new UiAction(UiActionKind.Answer, Answer: AnswerKind.No));
            ui.SameLine();
            var ready = now - s.ConfirmShown >= ScreenState.ConfirmDelay;
            if (ui.Button(ready ? "Yes, run it" : "Yes, run it (read the text first)") && ready)
                act(new UiAction(UiActionKind.Answer, Answer: AnswerKind.Yes));
            return;
        }
        s.ConfirmKey = null;
        ui.TextWrapped(c.Instruction.Length > 0 ? c.Instruction : "(setting up…)");
        if (s.Noting)
        {
            ui.Text(Amber, "What went wrong? Enter records the fail with this line; Escape records it without one.");
            if (s.FocusNote)
            {
                ui.FocusKeyboardHere();
                s.FocusNote = false;
            }
            ui.InputText("##kiln.checks.note", ref s.Note, 280);
            if (ui.KeyPressed(UiKey.Enter))
            {
                var note = s.Note;
                s.Noting = false;
                s.Note = "";
                act(new UiAction(UiActionKind.Answer, Answer: AnswerKind.Fail, Note: note));
                if (s.OpenedForNote) ui.CloseScreen();
                s.OpenedForNote = false;
            }
        }
        else if (c.Phase is "running" or "asking")
        {
            if (ui.Button("Pass")) act(new UiAction(UiActionKind.Answer, Answer: AnswerKind.Pass));
            ui.SameLine();
            if (ui.Button("Fail…"))
            {
                s.Noting = true;
                s.FocusNote = true;
                s.Note = "";
            }
            ui.SameLine();
            if (ui.Button("Skip")) act(new UiAction(UiActionKind.Answer, Answer: AnswerKind.Skip));
            ui.SameLine();
            if (ui.Button("Stop (no entry)")) act(new UiAction(UiActionKind.Answer, Answer: AnswerKind.Stop));
        }
        else ui.TextDisabled("Writing the verdict…");
        foreach (var a in c.Assertions)
            ui.Text(a.Ok ? Green : Red, $"{(a.Ok ? "ok  " : "FAIL")} {a.What}{(a.Detail is { Length: > 0 } d ? " — " + d : "")}");
    }

    private static string SessionLabel(string session) => session switch
    {
        "hosted" => "hosted world",
        "joined" => "joined to a server (the checks run there)",
        "server" => "dedicated server",
        _ => "main menu: host a world or join a server to run checks",
    };

    /// <summary>The HUD line: the current instruction, always, so the player can play the check with the list closed.</summary>
    public static string HudText(RunnerView v, string pass, string fail, string skip, string open)
    {
        // one line each, so a long instruction does not push the warnings off the screen
        var lines = new List<string>();
        if (v.Current is { } c)
            lines.Add(c.Phase switch
            {
                "confirming" => $"Check \"{c.Title}\" asks a question first: open the list ({open})",
                "finishing" => $"Check \"{c.Title}\": writing the verdict",
                _ => $"Check \"{c.Title}\": {(c.Instruction.Length > 0 ? c.Instruction : "setting up…")}   [pass {pass} · fail {fail} · skip {skip}]",
            });
        if (v.Warning is { } w) lines.Add("GUARD WARNING: " + w);
        if (v.Changed is { } ch) lines.Add(ch);
        return string.Join('\n', lines);
    }
}

/// <summary>The screen, HUD line and keys in a game client. Everything the player does is posted to the tick thread
/// (hosted) or sent to the server (joined); the screen reads snapshots.</summary>
internal static class CheckUiHost
{
    internal const string OpenKey = "kiln.checks.open", PassKey = "kiln.checks.pass", FailKey = "kiln.checks.fail", SkipKey = "kiln.checks.skip";

    private static CheckRunner? _runner;
    private static CheckNet? _net;
    private static UiScreen? _screen;
    private static readonly ScreenState State = new();
    private static KeyBinding? _open, _pass, _fail, _skip;

    public static void Start(CheckRunner runner, CheckNet? net)
    {
        _runner = runner;
        _net = net;
        _screen = Ui.Screen("Guided checks", ui => CheckScreen.Draw(ui, View(), State, Act, DateTime.UtcNow, KeyLine()),
            new UiScreenOptions { Width = 980, Height = 700, Resizable = true });
        _screen.Opened += () => _net?.RequestCatalog();
        _screen.Closed += OnClosed;
        Hud.Show("kiln.checks", () => CheckScreen.HudText(View(), Name(_pass), Name(_fail), Name(_skip), Name(_open)), HudAnchor.TopCenter,
            new Color(255, 225, 150));
        const string category = "Guided checks";
        _open = KeyBindings.Register(OpenKey, "Ctrl+Shift+F5", () => _screen?.Toggle(), "Open the list of guided checks", category);
        _pass = KeyBindings.Register(PassKey, "Ctrl+Shift+F6", () => Answer(AnswerKind.Pass), "Pass the running check", category);
        _fail = KeyBindings.Register(FailKey, "Ctrl+Shift+F7", FailPressed, "Fail the running check (then type one line)", category);
        _skip = KeyBindings.Register(SkipKey, "Ctrl+Shift+F8", () => Answer(AnswerKind.Skip), "Skip the running check", category);
    }

    private static string Name(KeyBinding? b) => b?.Key.ToString() ?? "?";

    private static string KeyLine() => $"Keys: list {Name(_open)} · pass {Name(_pass)} · fail {Name(_fail)} · skip {Name(_skip)} (Kiln menu, Keys)";

    private static RunnerView View()
    {
        var local = _runner?.View ?? RunnerView.Empty;
        return _net is { Joined: true } n ? n.View(local) : local;
    }

    private static bool Answerable(RunnerView v) => v.Current is { Phase: "running" or "asking" };

    private static void Answer(AnswerKind kind)
    {
        if (!Answerable(View())) return;
        Act(new UiAction(UiActionKind.Answer, Answer: kind));
    }

    private static void FailPressed()
    {
        if (!Answerable(View())) return;
        State.Noting = true;
        State.FocusNote = true;
        State.Note = "";
        if (_screen is { IsOpen: false } s)
        {
            State.OpenedForNote = true;
            s.Open();  // the player asked for it: the note needs the keyboard
        }
    }

    private static void OnClosed()
    {
        if (!State.Noting) return;
        // Escape while typing the note: the fail counts, without a note
        State.Noting = false;
        State.OpenedForNote = false;
        State.Note = "";
        Act(new UiAction(UiActionKind.Answer, Answer: AnswerKind.Fail));
    }

    private static void Act(UiAction a)
    {
        var runner = _runner;
        if (runner is null) return;
        if (_net is { Joined: true } net)
        {
            switch (a.Kind)
            {
                case UiActionKind.Run when a.Item is { } item: net.Start(item); break;
                case UiActionKind.RunAll: net.StartAll(); break;
                case UiActionKind.Answer: net.Answer(a.Answer, a.Note); break;
                case UiActionKind.Refresh: net.RequestCatalog(); break;
            }
            return;
        }
        runner.Post(() =>
        {
            switch (a.Kind)
            {
                case UiActionKind.Run when a.Item is { } item:
                    runner.Request(new StartRequest(item.Mod, item.Id, CheckOrigin.Local));
                    break;
                case UiActionKind.RunAll:
                    runner.RequestAll(null, CheckOrigin.Local);
                    break;
                case UiActionKind.Answer:
                    runner.Answer(a.Answer, a.Note);
                    break;
            }
        });
    }
}
