// SPDX-License-Identifier: MIT
namespace Kiln.Testing;

/// <summary>What the player did on the check screen.</summary>
internal enum UiActionKind
{
    Run,
    /// <summary>Several checks, in the order given (the list's "Run these N").</summary>
    RunThese,
    RunAll,
    Answer,
    Refresh,
}

internal sealed record UiAction(UiActionKind Kind, ViewItem? Item = null, AnswerKind Answer = AnswerKind.Pass, string? Note = null,
    IReadOnlyList<ViewItem>? Items = null);

/// <summary>The screen's own state (render thread only): the note being typed after fail.</summary>
internal sealed class ScreenState
{
    public bool Noting;
    public string Note = "";
    public bool FocusNote;
    public bool OpenedForNote;
    public string? ConfirmKey;
    public DateTime ConfirmShown;
    /// <summary>The list shows only the checks that came with the running Kiln version (the player's choice).</summary>
    public bool OnlyNew;

    /// <summary>A confirmation must be on screen this long before its yes counts (no click goes through by accident).</summary>
    public static readonly TimeSpan ConfirmDelay = TimeSpan.FromSeconds(1.5);
}

/// <summary>The runner's screen: the checks by area with each one's last verdict and refusal, the current instruction and
/// buttons, the guard's state. Drawn from a <see cref="RunnerView"/> alone, so the UI preview can draw it from sample
/// state.</summary>
internal static class CheckScreen
{
    /// <summary>Shown beside every group with checks tagged cheats: the guard covers the session, not the character.</summary>
    internal static string CheatsNote => KilnText.T("checks.cheats_note");

    private static readonly Color Green = new(110, 200, 120), Red = new(235, 95, 85), Amber = new(235, 190, 90), Dim = new(150, 150, 150);

    public static void Draw(UiDraw ui, RunnerView v, ScreenState s, Action<UiAction> act, DateTime now, string keys = "")
    {
        ui.Text(KilnText.T("checks.header", ("session", SessionLabel(v.Session))));
        ui.SameLine();
        ui.TextDisabled(KilnText.T("checks.totals", ("passed", v.Passes), ("failed", v.Fails), ("skipped", v.Skips), ("never", v.NeverRun)));
        if (v.Changed is { } changed) ui.Text(Red, changed);
        ui.Text(v.Protecting ? Green : Dim, KilnText.T("checks.guard", ("guard", v.Guard)));
        if (v.Warning is { } w) ui.Text(Red, KilnText.T("checks.guard_warning", ("warning", w)));
        if (v.Ledger is { } l) ui.Text(Red, KilnText.T("checks.ledger", ("ledger", l)));
        if (v.Message is { } m) ui.TextDisabled(m);
        if (keys.Length > 0) ui.TextDisabled(keys);
        ui.Separator();

        if (v.Current is { } c) DrawCurrent(ui, c, s, act, now);
        else
        {
            s.Noting = false;
            ui.TextDisabled(KilnText.T("checks.idle"));
            var runnable = v.Session is "hosted" or "joined" or "server";
            if (s.OnlyNew && NewCount(v) > 0)
            {
                // only what the list shows, in its order, as one request: hosted, each is queued as if its own Run had been
                // pressed; joined, the client sends them one after the other (the server takes one start at a time)
                var these = Shown(v, true).Where(i => i.Refusal is null).ToList();
                if (runnable && these.Count > 0 && ui.Button(KilnText.T("checks.run_these", ("count", these.Count))))
                    act(new UiAction(UiActionKind.RunThese, Items: these));
            }
            else if (runnable && ui.Button(KilnText.T("checks.run_all"))) act(new UiAction(UiActionKind.RunAll));
            if (v.Session == "joined")
            {
                ui.SameLine();
                if (ui.Button(KilnText.T("checks.refresh"))) act(new UiAction(UiActionKind.Refresh));
            }
        }
        if (v.Queued > 0) ui.TextDisabled(KilnText.T("checks.queued", ("count", v.Queued)));
        ui.Separator();
        // a release's pass is about the checks that came with it: one switch hides the rest (most of a long list)
        if (NewCount(v) is > 0 and var fresh) ui.Checkbox(KilnText.T("checks.only_new", ("count", fresh), ("version", Release(v.Kiln))) + "##kiln.checks.onlynew", ref s.OnlyNew);

        if (ui.BeginChild("kiln.checks.list"))
        {
            if (v.Items.Count == 0) ui.TextDisabled(v.Session == "joined" ? KilnText.T("checks.none_server") : KilnText.T("checks.none_mods"));
            foreach (var area in Shown(v, s.OnlyNew).GroupBy(i => i.Area, StringComparer.OrdinalIgnoreCase))
            {
                var items = area.ToList();
                var done = items.Count(i => i.Verdict == "pass");
                if (!ui.CollapsingHeader(KilnText.T("checks.area", ("area", area.Key), ("done", done), ("count", items.Count)) + $"###area.{area.Key}", defaultOpen: true)) continue;
                if (items.Any(i => i.Cheats)) ui.Text(Amber, CheatsNote);
                foreach (var i in items)
                {
                    using var _ = ui.Id(i.Mod + "/" + i.Id);
                    var (tag, color) = i.Verdict switch
                    {
                        "pass" => (KilnText.T("checks.verdict.pass"), Green),
                        "fail" => (KilnText.T("checks.verdict.fail"), Red),
                        "skip" => (KilnText.T("checks.verdict.skip"), Amber),
                        _ => ("----", Dim),
                    };
                    // Run comes first in its row: after a long title it was beyond the window's right edge
                    if (i.Refusal is null && v.Current is null && v.Session is "hosted" or "joined" or "server")
                    {
                        if (ui.SmallButton(KilnText.T("checks.run"))) act(new UiAction(UiActionKind.Run, i));
                        ui.SameLine();
                    }
                    ui.Text(color, tag);
                    ui.SameLine();
                    ui.Text(i.Title);
                    ui.SameLine();
                    var ids = $"{i.Mod}/{i.Id}" + (i.Tags.Count > 0 ? "  [" + string.Join(", ", i.Tags) + "]" : "");
                    ui.TextDisabled(i.Needs != "any" ? KilnText.T("checks.item.needs", ("ids", ids), ("needs", i.Needs)) : ids);
                    if (i.Refusal is { } r) ui.TextWrapped(KilnText.T("checks.item.refused", ("reason", r)));
                    if (i.Verdict is "fail" or "skip" && i.Note is { Length: > 0 } note) ui.TextDisabled(KilnText.T("checks.item.note", ("note", note)));
                }
            }
        }
        ui.EndChild();
    }

    /// <summary>How many of the list's checks came with the running Kiln version (0 when the version is not known).</summary>
    internal static int NewCount(RunnerView v) => Release(v.Kiln) is { Length: > 0 } k ? v.Items.Count(i => i.Since == k) : 0;

    /// <summary>A version without its build stamp ("0.2.0-dev.27+20261113.791e8b2" is "0.2.0-dev.27"): a check's
    /// <c>Since</c> names a release, and a test build of that release runs the same checks.</summary>
    internal static string? Release(string? version) =>
        version is null ? null : version.IndexOf('+') is >= 0 and var plus ? version[..plus] : version;

    /// <summary>The checks the list shows: all of them, or with <paramref name="onlyNew"/> the ones that came with the
    /// running Kiln version; all of them again when there is none of those (the switch is not offered then).</summary>
    internal static IReadOnlyList<ViewItem> Shown(RunnerView v, bool onlyNew) =>
        onlyNew && NewCount(v) > 0 && Release(v.Kiln) is { } k ? v.Items.Where(i => i.Since == k).ToList() : v.Items;

    private static void DrawCurrent(UiDraw ui, ViewCurrent c, ScreenState s, Action<UiAction> act, DateTime now)
    {
        ui.Separator(KilnText.T("checks.now", ("title", c.Title)));
        ui.TextDisabled(c.Cheats ? KilnText.T("checks.now_cheats", ("mod", c.Mod), ("id", c.Id), ("area", c.Area)) : $"{c.Mod}/{c.Id} · {c.Area}");
        if (c.Phase == "confirming" && c.Confirm is { } confirm)
        {
            var key = c.Mod + "/" + c.Id + "\n" + confirm;  // a second question for the same check waits again
            if (s.ConfirmKey != key)
            {
                s.ConfirmKey = key;
                s.ConfirmShown = now;
            }
            ui.Text(Amber, KilnText.T("checks.confirm.before"));
            ui.TextWrapped(confirm);
            // "No" first: the focus a gamepad or Enter lands on first is never the yes
            if (ui.Button(KilnText.T("checks.confirm.no"))) act(new UiAction(UiActionKind.Answer, Answer: AnswerKind.No));
            ui.SameLine();
            var ready = now - s.ConfirmShown >= ScreenState.ConfirmDelay;
            if (ui.Button(ready ? KilnText.T("checks.confirm.yes") : KilnText.T("checks.confirm.yes_wait")) && ready)
                act(new UiAction(UiActionKind.Answer, Answer: AnswerKind.Yes));
            return;
        }
        s.ConfirmKey = null;
        ui.TextWrapped(c.Instruction.Length > 0 ? c.Instruction : KilnText.T("checks.setting_up"));
        if (s.Noting)
        {
            ui.Text(Amber, KilnText.T("checks.fail_note"));
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
            if (ui.Button(KilnText.T("checks.pass"))) act(new UiAction(UiActionKind.Answer, Answer: AnswerKind.Pass));
            ui.SameLine();
            if (ui.Button(KilnText.T("checks.fail")))
            {
                s.Noting = true;
                s.FocusNote = true;
                s.Note = "";
            }
            ui.SameLine();
            if (ui.Button(KilnText.T("checks.skip"))) act(new UiAction(UiActionKind.Answer, Answer: AnswerKind.Skip));
            ui.SameLine();
            if (ui.Button(KilnText.T("checks.stop"))) act(new UiAction(UiActionKind.Answer, Answer: AnswerKind.Stop));
        }
        else ui.TextDisabled(KilnText.T("checks.writing"));
        foreach (var a in c.Assertions)
            ui.Text(a.Ok ? Green : Red, a.Detail is { Length: > 0 } d
                ? a.Ok ? KilnText.T("checks.assert.ok_detail", ("what", a.What), ("detail", d)) : KilnText.T("checks.assert.fail_detail", ("what", a.What), ("detail", d))
                : a.Ok ? KilnText.T("checks.assert.ok", ("what", a.What)) : KilnText.T("checks.assert.fail", ("what", a.What)));
    }

    private static string SessionLabel(string session) => session switch
    {
        "hosted" => KilnText.T("checks.session.hosted"),
        "joined" => KilnText.T("checks.session.joined"),
        "server" => KilnText.T("checks.session.server"),
        _ => KilnText.T("checks.session.menu"),
    };

    /// <summary>The HUD line: the current instruction, always, so the player can play the check with the list closed.</summary>
    public static string HudText(RunnerView v, string pass, string fail, string skip, string open)
    {
        // one line each, so a long instruction does not push the warnings off the screen
        var lines = new List<string>();
        if (v.Current is { } c)
            lines.Add(c.Phase switch
            {
                "confirming" => KilnText.T("checks.hud.confirming", ("title", c.Title), ("open", open)),
                "finishing" => KilnText.T("checks.hud.finishing", ("title", c.Title)),
                _ => KilnText.T("checks.hud.running", ("title", c.Title), ("instruction", c.Instruction.Length > 0 ? c.Instruction : KilnText.T("checks.hud.setting_up")),
                    ("pass", pass), ("fail", fail), ("skip", skip)),
            });
        if (v.Warning is { } w) lines.Add(KilnText.T("checks.guard_warning", ("warning", w)));
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
        _screen = Ui.Screen(KilnText.T("checks.title"), ui => CheckScreen.Draw(ui, View(), State, Act, DateTime.UtcNow, KeyLine()),
            new UiScreenOptions { Width = 980, Height = 700, Resizable = true });
        _screen.Opened += () => Act(new UiAction(UiActionKind.Refresh));
        _screen.Closed += OnClosed;
        Hud.Show("kiln.checks", () => CheckScreen.HudText(View(), Name(_pass), Name(_fail), Name(_skip), Name(_open)), HudAnchor.TopCenter,
            new Color(255, 225, 150));
        var category = KilnText.T("checks.key.category");
        _open = KeyBindings.Register(OpenKey, "Ctrl+Shift+F5", () => _screen?.Toggle(), KilnText.T("checks.key.open"), category);
        _pass = KeyBindings.Register(PassKey, "Ctrl+Shift+F6", () => Answer(AnswerKind.Pass), KilnText.T("checks.key.pass"), category);
        _fail = KeyBindings.Register(FailKey, "Ctrl+Shift+F7", FailPressed, KilnText.T("checks.key.fail"), category);
        _skip = KeyBindings.Register(SkipKey, "Ctrl+Shift+F8", () => Answer(AnswerKind.Skip), KilnText.T("checks.key.skip"), category);
        KilnText.Changed -= Relabel;
        KilnText.Changed += Relabel;
    }

    // Kiln's language changed (KilnText.Changed, any thread): the screen's title and the Keys page's lines follow it.
    private static void Relabel()
    {
        if (_screen is { } sc) sc.Title = KilnText.T("checks.title");
        var category = KilnText.T("checks.key.category");
        foreach (var (b, key) in new[] { (_open, "checks.key.open"), (_pass, "checks.key.pass"), (_fail, "checks.key.fail"), (_skip, "checks.key.skip") })
            if (b is not null)
            {
                b.Description = KilnText.T(key);
                b.Category = category;
            }
    }

    private static string Name(KeyBinding? b) => b?.Key.ToString() ?? "?";

    private static string KeyLine() => KilnText.T("checks.keys", ("open", Name(_open)), ("pass", Name(_pass)), ("fail", Name(_fail)), ("skip", Name(_skip)));

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

    internal static void ActJoined(UiAction a, CheckNet net, Action<Action>? post = null)
    {
        (post ?? (work => Client.Post(work)))(() =>
        {
        switch (a.Kind)
        {
            case UiActionKind.Run when a.Item is { } item: net.Start(item); break;
            case UiActionKind.RunThese when a.Items is { } items: net.StartThese(items); break;
            case UiActionKind.RunAll: net.StartAll(); break;
            case UiActionKind.Answer: net.Answer(a.Answer, a.Note); break;
            case UiActionKind.Refresh: net.RequestCatalog(); break;
        }
        });
    }

    private static void Act(UiAction a)
    {
        var runner = _runner;
        if (runner is null) return;
        if (_net is { Joined: true } net)
        {
            ActJoined(a, net);
            return;
        }
        runner.Post(() =>
        {
            switch (a.Kind)
            {
                case UiActionKind.Run when a.Item is { } item:
                    runner.Request(new StartRequest(item.Mod, item.Id, CheckOrigin.Local));
                    break;
                case UiActionKind.RunThese when a.Items is { } items:
                    foreach (var i in items) runner.Request(new StartRequest(i.Mod, i.Id, CheckOrigin.Local));
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
