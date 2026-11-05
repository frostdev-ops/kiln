// SPDX-License-Identifier: MIT
using Kiln;

namespace ChatTest;

/// <summary>
/// Checks kiln.time and kiln.chat on a lab server and prints PASS/FAIL lines.
/// 1. Command registry unit checks (no game state).
/// 2. Time: read, set forward, check the game wrote it, set with the game's function, parse, restore, sync evidence,
///    weather re-roll.
/// 3. Chat without a client: server lines with nobody connected, then lines injected as if a player typed them:
///    passed on (lands in the server's chat inbox), cancelled by a handler, commands run and consumed.
/// Also registers /kt-time (admin) and /kt-say so a joined player can try them (client checklist).
/// </summary>
public sealed class ChatTestMod : Mod
{
    private int _pass, _fail;
    private readonly List<ChatMessage> _seen = new();
    private readonly List<string> _echo = new();

    private void Check(string name, bool ok, string detail = "")
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? ": " + detail : "")}");
    }

    private void Try(string name, Action body)
    {
        try { body(); }
        catch (Exception e) { Check(name, false, $"threw {e.GetType().Name}: {e.Message}"); }
    }

    private async Task Ticks(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    private bool _running;

    public override void OnLoad()
    {
        Log.Info($"chat-test: kiln.chat {(Chat.Available ? "available" : "UNAVAILABLE")}, kiln.time {(Time.Available ? "available" : "UNAVAILABLE")}");
        // The world steps while the save loads; the save then sets its own time of day. Test once it runs.
        Context.GameLog.Line += (_, line) =>
        {
            if (line.Contains("Switching state from LoadEcsScene to Run")) _running = true;
        };
        Try("commands", CommandChecks);
        RegisterPlayerCommands();
        _ = RunAsync();
    }

    // ------------------------------------------------------------------ 1. command registry
    private void CommandChecks()
    {
        static string J(IReadOnlyList<string> l) => string.Join("|", l);
        Check("tokenize.plain", J(Commands.Tokenize("a b  c")) == "a|b|c", J(Commands.Tokenize("a b  c")));
        Check("tokenize.quotes", J(Commands.Tokenize("say \"hello world\" 'x y' z")) == "say|hello world|x y|z",
            J(Commands.Tokenize("say \"hello world\" 'x y' z")));
        Check("tokenize.escapes", J(Commands.Tokenize(@"a\ b ""say \""hi\"""" c\\d")) == "a b|say \"hi\"|c\\d",
            J(Commands.Tokenize(@"a\ b ""say \""hi\"""" c\\d")));
        Check("tokenize.glued-quote", J(Commands.Tokenize("ab'c d'e")) == "abc de", J(Commands.Tokenize("ab'c d'e")));
        Check("tokenize.unterminated", J(Commands.Tokenize("x \"open end")) == "x|open end", J(Commands.Tokenize("x \"open end")));
        Check("tokenize.empty", Commands.Tokenize("   ").Count == 0);
        Check("is-command", Commands.IsCommandLine("/help") && Commands.IsCommandLine("/kt-echo a") &&
                            !Commands.IsCommandLine("/ help") && !Commands.IsCommandLine("hello") &&
                            !Commands.IsCommandLine("//x") && !Commands.IsCommandLine("/"));

        Commands.Register("kt-echo", c => { _echo.Add(c.ArgText); c.Reply(string.Join("|", c.Args)); },
            CommandPermission.Everyone, "<words...>", "echoes its arguments", "kte");
        var r = Commands.Execute(CommandSource.Console, "kt-echo a \"b c\"");
        Check("execute.console", r.Ok && r.Found && J(r.Output) == "a|b c", r.ToString());
        r = Commands.Execute(CommandSource.Console, "/KTE x");
        Check("execute.alias+prefix+case", r.Ok && J(r.Output) == "x", r.ToString());
        r = Commands.Execute(CommandSource.Console, "kt-nope 1");
        Check("execute.unknown", !r.Found && !r.Ok && r.Error!.Contains("Unknown"), r.ToString());
        r = Commands.Execute(CommandSource.Console, "   ");
        Check("execute.empty", !r.Found && !r.Ok, r.ToString());

        Commands.Register("kt-admin", c => c.Reply("admin ok"), CommandPermission.Admin, "", "admin only");
        Commands.Register("kt-console", c => c.Reply("console ok"), CommandPermission.Console, "", "console only");
        var bob = CommandSource.ForPlayer(new EntityId(77), "bob");
        r = Commands.Execute(bob, "kt-admin");
        Check("permission.player-denied", r.Found && r.Denied && !r.Ok, r.ToString());
        r = Commands.Execute(CommandSource.Console, "kt-admin");
        Check("permission.console-admin", r.Ok, r.ToString());
        Check("permission.console-only", Commands.Execute(CommandSource.Remote("dash"), "kt-console").Ok &&
                                         Commands.Execute(bob, "kt-console").Denied &&
                                         Commands.Execute(CommandSource.Remote("guest", admin: false), "kt-console").Denied);
        Commands.SetAdminCheck(s => s.Name == "alice");
        r = Commands.Execute(CommandSource.ForPlayer(new EntityId(78), "alice"), "kt-admin");
        var rb = Commands.Execute(bob, "kt-admin");
        Commands.SetAdminCheck(null);
        Check("permission.admin-check", r.Ok && rb.Denied, $"alice {r}, bob {rb}");

        var replaced = false;
        try
        {
            Commands.Register("kt-echo", c => { _echo.Add(c.ArgText); c.Reply(string.Join("|", c.Args)); },
                CommandPermission.Everyone, "<words...>", "echoes its arguments", "kte");
            replaced = Commands.Find("kte")?.Name == "kt-echo";
        }
        catch (Exception) { }
        Check("register.same-mod-replaces", replaced);
        var stolen = false;
        try { Commands.Register("help", _ => { }); stolen = true; }
        catch (InvalidOperationException) { }
        Check("register.taken-name-throws", !stolen);
        var bad = false;
        try { Commands.Register("bad name", _ => { }); bad = true; }
        catch (ArgumentException) { }
        Check("register.invalid-name-throws", !bad);

        Commands.Register("kt-fail", c => c.Fail("nope, said the handler"));
        Commands.Register("kt-throw", _ => throw new InvalidDataException("boom"));
        Commands.Register("kt-int", c => c.Reply((c.Int(0) * 2).ToString()), usage: "<n>");
        r = Commands.Execute(CommandSource.Console, "kt-fail");
        Check("handler.fail", !r.Ok && r.Error == "nope, said the handler", r.ToString());
        r = Commands.Execute(CommandSource.Console, "kt-throw");
        Check("handler.exception", !r.Ok && r.Error!.Contains("failed"), r.ToString());
        r = Commands.Execute(CommandSource.Console, "kt-int 21");
        var r2 = Commands.Execute(CommandSource.Console, "kt-int abc");
        Check("args.int", r.Ok && J(r.Output) == "42" && !r2.Ok && r2.Error!.Contains("usage: /kt-int <n>"), $"{r} / {r2}");

        Commands.Register("kt-rest", c => c.Reply(c.Rest(1)));
        r = Commands.Execute(CommandSource.Console, "kt-rest a  b   \"c d\"  ");
        Check("args.rest", r.Ok && J(r.Output) == "b   \"c d\"", r.ToString());

        r = Commands.Execute(CommandSource.Console, "help");
        var rh = Commands.Execute(bob, "help");
        Check("help.lists-visible", r.Ok && r.Text.Contains("/kt-echo") && r.Text.Contains("/kt-admin") &&
                                    rh.Ok && rh.Text.Contains("/kt-echo") && !rh.Text.Contains("/kt-admin"), $"{r.Text} || {rh.Text}");
        r = Commands.Execute(CommandSource.Console, "help kt-echo");
        Check("help.one", r.Ok && r.Text.Contains("/kt-echo <words...>") && r.Text.Contains("/kte"), r.Text);

        using (Commands.Register("kt-temp", _ => { })) Check("register.find", Commands.Find("kt-temp") is not null);
        Check("register.dispose-removes", Commands.Find("kt-temp") is null);
        Commands.Register("kt-temp2", _ => { });
        Check("unregister", Commands.Unregister("kt-temp2") && Commands.Find("kt-temp2") is null);
        Check("list.owner", Commands.Find("kt-echo")?.Owner == Context.Info.Id && Commands.Find("help")?.Owner == "kiln",
            $"kt-echo by {Commands.Find("kt-echo")?.Owner}, help by {Commands.Find("help")?.Owner}; {Commands.List().Count} commands");
    }

    private void RegisterPlayerCommands()
    {
        // For a joined player (client checklist).
        Commands.Register("kt-time", c =>
        {
            if (c.Count == 0) { c.Reply($"time is {Time.State}"); return; }
            if (!Time.TryParse(c.Arg(0), out var h)) c.Fail($"'{c.Arg(0)}' is not a time; try 7:30, 19, noon");
            var mode = c.ArgOrDefault(1) == "game" ? TimeSetMode.GameReset : TimeSetMode.Forward;
            Time.Set(h, mode);
            c.Reply($"time set to {Time.Format(h)} ({mode}); now {Time.State.Clock}");
        }, CommandPermission.Admin, "[time] [game]", "shows or sets the time of day");
        Commands.Register("kt-say", c => c.Reply($"you said: {c.ArgText}"), CommandPermission.Everyone, "<text>", "replies to you only");
        Commands.Register("kt-all", c => Chat.Broadcast($"{c.Source.Name} says: {c.ArgText}"), CommandPermission.Everyone, "<text>", "server line to everyone");
        Commands.Register("kt-keep", c => { c.KeepLine = true; c.Reply("kept"); });
    }

    // ------------------------------------------------------------------ 2 + 3. game
    private async Task RunAsync()
    {
        try
        {
            // Loaded with the server: wait for the game's "Run" state. Loaded later (reload): it is running already.
            var waited = 0;
            for (; waited < 2450 && !_running; waited++) await Ticks(1);
            Log.Info($"chat-test: world {(_running ? "running" : "assumed running")} after {waited} steps");
            for (var i = 0; i < 250 && !(Time.Available && Time.Ready && Chat.Status.SendReady); i++) await Ticks(1);
            await Ticks(10);
            await PersistCheck();
            await TimeChecks();
            await ChatChecks();
        }
        catch (Exception e)
        {
            Check("run", false, $"threw {e}");
        }
        try { await ArmPersist(); }
        catch (Exception e) { Check("time.persist.arm", false, e.Message); }
        Log.Info($"chat-test: {_pass} passed, {_fail} failed");
    }

    // Opt-in: create <data>/persist.arm. Then the run ends by setting 09:15 and writing persist.json; after a server
    // restart the next run checks that the saved world came back with that time (plus what ran until the save).
    private async Task PersistCheck()
    {
        if (!Time.Available) return;
        var expectFile = Path.Combine(Context.DataDirectory, "persist.json");
        if (File.Exists(expectFile))
        {
            var lines = File.ReadAllLines(expectFile);
            var expected = double.Parse(lines[0], System.Globalization.CultureInfo.InvariantCulture);
            var s = Time.State;
            var diff = ((s.Hours - expected) % 24 + 24) % 24;  // forward drift since the set
            Check("time.persist", diff < 1.0,
                $"set {Time.Format(expected)} before the restart ({lines.ElementAtOrDefault(1)}), the loaded world shows {s.Clock} " +
                $"(+{diff * 60:F1} game minutes)");
            File.Delete(expectFile);
        }
        await Task.CompletedTask;
    }

    private async Task ArmPersist()
    {
        if (!File.Exists(Path.Combine(Context.DataDirectory, "persist.arm"))) return;
        File.Delete(Path.Combine(Context.DataDirectory, "persist.arm"));
        Time.Set(9.25);
        await Ticks(2);
        Directory.CreateDirectory(Context.DataDirectory);
        File.WriteAllLines(Path.Combine(Context.DataDirectory, "persist.json"),
            new[] { "9.25", $"at {DateTime.UtcNow:O}, counter {Time.State.ChangeCounter}" });
        Log.Info($"INFO time.persist: set 09:15 (now {Time.State.Clock}); restart the server to check the save kept it");
    }

    private async Task TimeChecks()
    {
        if (!Time.Available) { Check("time.available", false, "kiln.time missing"); return; }
        Check("time.ready", Time.Ready);
        var original = Time.State;
        Check("time.read", original.Hours is >= 0 and < 24 && original.DayLength > TimeSpan.Zero && original.NightLength > TimeSpan.Zero,
            $"{original} (field {Time.Format(original.HoursField)}, game time {original.GameTime}, ecs {original.EcsTime})");

        Time.Set(13.5);
        var s = Time.State;
        Check("time.set.forward.immediate", Math.Abs(s.Hours - 13.5) < 0.01 && s.ChangeCounter == original.ChangeCounter + 1 &&
                                            s.GameTime >= original.GameTime,
            $"{Time.Format(original.Hours)} -> {s.Clock} ({s.Hours:F4} h), counter {original.ChangeCounter} -> {s.ChangeCounter}, " +
            $"game time {original.GameTime} -> {s.GameTime}");
        await Ticks(3);
        s = Time.State;
        Check("time.set.forward.game-field", Math.Abs(s.HoursField - 13.5) < 0.05 && s.IsDay == (13.5 >= s.DayStart && 13.5 < s.NightStart),
            $"game's time-of-day field {s.HoursField:F4} h ({Time.Format(s.HoursField)}), day={s.IsDay} " +
            $"(day phase {Time.Format(s.DayStart)}-{Time.Format(s.NightStart)})");

        Time.Set(TimeSpan.FromHours(20.25), TimeSetMode.GameReset);
        s = Time.State;
        Check("time.set.game", Math.Abs(s.Hours - 20.25) < 0.01 && s.GameTime < s.DayLength + s.NightLength,
            $"{s.Clock}, game time {s.GameTime} (restarted inside the first cycle of {(s.DayLength + s.NightLength)})");
        await Ticks(3);
        s = Time.State;
        var dayExpected = s.HoursField >= s.DayStart && s.HoursField < s.NightStart;
        Check("time.set.game.game-field", Math.Abs(s.HoursField - 20.25) < 0.05 && s.IsDay == dayExpected,
            $"field {s.HoursField:F4} h, day={s.IsDay} (day phase {Time.Format(s.DayStart)}-{Time.Format(s.NightStart)})");

        Check("time.parse", Time.TryParse("7:30pm", out var a) && Math.Abs(a - 19.5) < 1e-9 &&
                            Time.TryParse("noon", out var b) && b == 12 && Time.TryParse("07:15", out var c2) &&
                            Math.Abs(c2 - 7.25) < 1e-9 && !Time.TryParse("25", out _) && !Time.TryParse("7:61", out _) &&
                            Time.Format(13.5) == "13:30");
        var threw = false;
        try { Time.Set(double.NaN); }
        catch (ArgumentOutOfRangeException) { threw = true; }
        Check("time.set.rejects-nan", threw);

        // Replication: the GameServer's ServerInfo sender copies the anchor when the counter moved.
        var counter = Time.State.ChangeCounter;
        uint? synced = null;
        for (var i = 0; i < 245 && synced != counter; i++)
        {
            await Ticks(1);
            synced = Time.State.SyncedCounter;
        }
        if (synced == counter) Check("time.sync", true, $"ServerInfo carried change counter {counter} to clients");
        else Log.Info($"INFO time.sync: ServerInfo has not carried counter {counter} within 10 s (last sent {synced?.ToString() ?? "unknown"}); the server sends it to connected clients only");

        if (Time.State.WeatherAvailable)
        {
            var layers = Time.State.WeatherLayers;
            Try("time.weather", () =>
            {
                Time.ResetWeather();
                Check("time.weather.reset", true, $"{layers} layers re-roll (see the kiln.time log line)");
            });
        }
        else Log.Info("INFO time.weather: WeatherSystem not seen yet; reset skipped");

        Time.Set(original.Hours);
        await Ticks(2);
        s = Time.State;
        Check("time.restore", Math.Abs(s.Hours - original.Hours) < 0.05, $"back to {s.Clock} (was {original.Clock}), field {Time.Format(s.HoursField)}");
    }

    private async Task ChatChecks()
    {
        if (!Chat.Available) { Check("chat.available", false, "kiln.chat missing"); return; }
        var st = Chat.Status;
        Check("chat.status", st.ReceiveHooked && st.SendReady, $"{st}");
        Check("chat.players.none", Chat.Players.Count == 0, $"{Chat.Players.Count} connected");
        Check("chat.broadcast.nobody", Chat.Broadcast("chat-test broadcast") == 0);
        Check("chat.tell.not-connected", !Chat.Tell(new EntityId(1), "hello"));

        Chat.Received += m =>
        {
            _seen.Add(m);
            if (m.Text.Contains("drop-me")) m.Cancel = true;
        };
        var nonce = Environment.TickCount64 % 100000;

        // a: an ordinary line passes on into the chat inbox, unchanged.
        var before = Chat.Status;
        var text = $"chat-test hello {nonce}";
        Chat.Diagnostics.Inject(0, text);
        await Ticks(3);
        var last = Chat.Diagnostics.LastLine();
        var after = Chat.Status;
        Check("chat.receive.event", _seen.Any(m => m.Text == text && !m.IsCommand), $"{_seen.Count} seen");
        Check("chat.receive.passed-on", last?.Text == text && last.Value.Connection == 0 && after.Received == before.Received + 1 &&
                                        after.Consumed == before.Consumed,
            $"inbox newest: '{last?.Text}' (type {last?.Type}, reader mask 0x{last?.ReaderMask:x8}); inbox {before.InboxCount} -> {after.InboxCount}");

        // b: a handler cancels.
        before = after;
        var drop = $"chat-test drop-me {nonce}";
        Chat.Diagnostics.Inject(0, drop);
        await Ticks(3);
        last = Chat.Diagnostics.LastLine();
        after = Chat.Status;
        Check("chat.receive.cancel", _seen.Any(m => m.Text == drop && m.Cancel) && last?.Text != drop &&
                                     after.Consumed == before.Consumed + 1,
            $"newest still '{last?.Text}', consumed {before.Consumed} -> {after.Consumed}");

        // c: a command from chat runs and is not broadcast.
        before = after;
        var echoes = _echo.Count;
        Chat.Diagnostics.Inject(0, "/kt-echo from \"the chat\"");
        await Ticks(3);
        last = Chat.Diagnostics.LastLine();
        after = Chat.Status;
        Check("chat.command.runs", _echo.Count == echoes + 1 && _echo[^1] == "from \"the chat\"", $"echo saw '{(_echo.Count > echoes ? _echo[^1] : "")}'");
        Check("chat.command.consumed", last?.Text?.StartsWith("/kt-echo") != true && after.Consumed == before.Consumed + 1,
            $"newest '{last?.Text}'");

        // d: unknown command: consumed (policy Reply).
        before = after;
        Chat.Diagnostics.Inject(0, "/kt-nope 1 2");
        await Ticks(3);
        last = Chat.Diagnostics.LastLine();
        after = Chat.Status;
        Check("chat.command.unknown-consumed", last?.Text != "/kt-nope 1 2" && after.Consumed == before.Consumed + 1);

        // e: a command that keeps its line.
        Chat.Diagnostics.Inject(0, "/kt-keep 1");
        await Ticks(3);
        last = Chat.Diagnostics.LastLine();
        Check("chat.command.keep-line", last?.Text == "/kt-keep 1", $"newest '{last?.Text}'");

        Log.Info($"chat-test: chat status {Chat.Status}");
    }

    public override void OnUnload() => Log.Info("chat-test: unloading (its commands and handlers go with it)");
}
