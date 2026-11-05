// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using Kiln;

namespace ApiTest;

public enum Weather
{
    Clear,
    Rain,
    Storm,
}

[Comment("Limits of the api-test mod.")]
public sealed class Limits
{
    [Comment("Movement speed factor."), Range(0, 10), Synced]
    public double Speed { get; set; } = 1.0;

    public List<string> Blocked { get; set; } = ["tnt"];
}

public sealed class ApiTestConfig
{
    [Comment("How many homes a player may set."), Range(1, 50)]
    public int MaxHomes { get; set; } = 3;

    public string Motd { get; set; } = "hello";

    public Weather Forecast { get; set; } = Weather.Clear;

    [Range(1, 600)]
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromSeconds(30);

    [RequiresRestart]
    public int Port { get; set; } = 8080;

    public Limits Limits { get; set; } = new();
}

/// <summary>The mod's own events.</summary>
public sealed class PingEvent : CancelableEvent
{
    public List<string> Seen { get; } = [];
}

public sealed class VoteEvent : ResultEvent;

/// <summary>
/// Exercises the API backbone on a lab server and prints PASS/FAIL lines: the event bus (priorities, cancel,
/// receiveCanceled, results, re-entrancy, Enqueue, [Subscribe] on the mod, nothing left after a reload), config
/// (generated file, edit -> change event, clamping, Set, restart-pending, schema), typed commands (attribute methods,
/// builder, parse errors, help, completion, the console folder), permissions (server user groups, wildcards, console
/// nodes, file reload), and the service events (chat, commands, players via simulation, time, settings, spawn, items).
/// </summary>
public sealed class ApiTestMod : Mod
{
    private int _pass, _fail;
    private bool _running;
    private int _load;
    private ModConfig<ApiTestConfig> _config = null!;
    private readonly List<ConfigChangedEvent> _configEvents = [];
    private readonly List<PlayerJoinedEvent> _joined = [];
    private readonly List<PlayerLeftEvent> _left = [];
    private readonly List<string> _monitor = [];

    // ---- [Subscribe] methods (Kiln registers them before OnLoad) ----
    [Subscribe(EventPriority.High)]
    private void OnPingHigh(PingEvent e) => e.Seen.Add("mod-high");

    [Subscribe(EventPriority.Monitor, ReceiveCanceled = true)]
    private void OnPingMonitor(PingEvent e) => e.Seen.Add("mod-monitor");

    [Subscribe]
    private void OnConfigChanged(ConfigChangedEvent e)
    {
        if (e.ModId != "api-test") return;
        _configEvents.Add(e);
        Log.Info($"config event: {e} (source {e.Source})");
    }

    private const int SubscribeMethods = 3;

    // ---- [Command] methods (Kiln registers them before OnLoad) ----
    [Command("apitest echo", Description = "echoes text")]
    private string Echo([Greedy] string text) => text;

    [Command("apitest heal", Description = "pretends to heal a player", Permission = CommandPermission.Admin)]
    private string Heal(CommandContext c, Player target, [Range(1, 1000)] int amount = 100) => $"healed {target.Name} by {amount}";

    [Command("apitest tp", Description = "shows a position")]
    private string Tp(WorldPosition to) => $"to {to}";

    [Command("apitest wait")]
    private string Wait(TimeSpan time, [TimeOfDay] double until) => $"{time.TotalSeconds}s until {Time.Format(until)}";

    [Command("apitest item")]
    private string GiveItem(ItemInfo item, [Range(1, 99)] int count = 1) => $"{count} x {item.Name}";

    [Command("apitest mode")]
    private string ModeCmd(Weather weather, bool loud = false) => $"{weather} {loud}";

    [Command("apitest secret", Permission = CommandPermission.Console)]
    private string Secret() => "42";

    [Command("apitest whoami")]
    private string WhoAmI(CommandContext c) => c.Source.ToString();

    [Command("apitest vip", Node = "api-test.vip", Permission = CommandPermission.Admin)]
    private string Vip() => "vip ok";

    [Command("apitest-alias")]
    private string Alias([Greedy] string text) => "alias " + text;

    private bool Check(string name, bool ok, string detail = "")
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? ": " + detail : "")}");
        return ok;
    }

    private void Try(string name, Action body)
    {
        try { body(); }
        catch (Exception e) { Check(name, false, $"threw {e.GetType().Name}: {e.Message}"); }
    }

    private async Task TryAsync(string name, Func<Task> body)
    {
        try { await body(); }
        catch (Exception e) { Check(name, false, $"threw {e.GetType().Name}: {e.Message}"); }
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    private async Task<bool> Until(Func<bool> done, int maxSteps)
    {
        for (var i = 0; i < maxSteps; i++)
        {
            if (done()) return true;
            await Context.Scheduler.NextTick();
        }
        return done();
    }

    public override void OnLoad()
    {
        _load = (AppContext.GetData("api-test.loads") as int? ?? 0) + 1;
        AppContext.SetData("api-test.loads", _load);
        Log.Info($"api-test: load #{_load} in this process; kiln.chat {(Chat.Available ? "available" : "unavailable")}");

        // A reload must leave nothing of the old instance: exactly this instance's [Subscribe] handlers, one command.
        var mine = Events.Subscriptions.Where(s => s.Owner == "api-test").ToList();
        Check("unload.subscriptions-clean", mine.Count == SubscribeMethods,
            $"{mine.Count} subscription(s) of api-test before OnLoad: {string.Join(", ", mine.Select(s => s.Name))}");
        Check("commands.auto-registered", Commands.Find("apitest") is { Owner: "api-test", IsTyped: true } c &&
                                          Commands.Find("apitest-alias") is not null,
            string.Join(" | ", Commands.Find("apitest")?.UsageLines ?? []));
        Check("unload.one-config", Config.All.Count(f => f.ModId == "api-test") == 0, "no config of an old instance");

        if (_load == 1)
        {
            var file = Path.Combine(Config.Directory, "api-test.json");
            if (File.Exists(file)) File.Delete(file);  // start from the schema's defaults
        }
        _config = Config.Register<ApiTestConfig>();
        if (_load > 1 && Game.HasFeature("tick"))  // the first instance's live run edited the port
            Check("config.restart-value-applied", _config.Value.Port == 9090 && _config.PendingRestart.Count == 0,
                $"port {_config.Value.Port} after the reload (the first instance edited it to 9090)");
        _config.Changed += e => _monitor.Add("changed-handler:" + string.Join(",", e.Changed));
        Permissions.Register("api-test.fly", PermissionLevel.Everyone, "fly around");
        Events.On<PlayerJoinedEvent>(e =>
        {
            _joined.Add(e);
            Log.Info($"event: {e} steam {e.Player.SteamId} rights {e.Player.Permissions}");
        });
        Events.On<PlayerLeftEvent>(e =>
        {
            _left.Add(e);
            Log.Info($"event: {e}");
        });

        Try("events.sync", BusChecks);
        Try("config.sync", ConfigSyncChecks);
        Try("commands.sync", CommandChecks);
        Try("permissions.sync", PermissionChecks);

        if (!Game.HasFeature("tick")) return;  // unit tests load this mod too: no game behind it
        _running = Spawn.Available && Spawn.WorldRunning;  // a reload: the world runs already
        Context.GameLog.Line += (_, line) =>
        {
            if (line.Contains("Switching state from LoadEcsScene to Run")) _running = true;
        };
        _ = RunAsync();
    }

    public override void OnUnload() => Log.Info($"api-test: unloading instance #{_load}");

    // ------------------------------------------------------------------------------- bus (synchronous) -------
    private void BusChecks()
    {
        var subs = new List<EventSubscription>
        {
            Events.On<PingEvent>(e => e.Seen.Add("lowest"), EventPriority.Lowest),
            Events.On<PingEvent>(e => e.Seen.Add("normal")),
            Events.On<PingEvent>(e => e.Seen.Add("highest"), EventPriority.Highest),
        };
        var ev = Events.Post(new PingEvent());
        Check("events.priority-order", string.Join(",", ev.Seen) == "highest,mod-high,normal,lowest,mod-monitor", string.Join(",", ev.Seen));

        subs.Add(Events.On<PingEvent>(e => e.Cancel(), EventPriority.Normal));
        subs.Add(Events.On<PingEvent>(e => e.Seen.Add("low-sees-canceled"), EventPriority.Low, receiveCanceled: true));
        ev = Events.Post(new PingEvent());
        Check("events.cancel", ev.Canceled && ev.CanceledBy == "api-test" && !ev.Seen.Contains("lowest") &&
                               ev.Seen.Contains("low-sees-canceled") && ev.Seen.Contains("mod-monitor"), string.Join(",", ev.Seen));
        foreach (var s in subs) s.Dispose();
        Check("events.dispose", Events.Subscriptions.Count(s => s.Owner == "api-test" && s.Type == typeof(PingEvent)) == 2);

        var vote = Events.Post(new VoteEvent());
        using (Events.On<VoteEvent>(e => e.Deny()))
        using (Events.On<VoteEvent>(e => e.Allow(), EventPriority.Low))
        {
            var v2 = Events.Post(new VoteEvent());
            Check("events.result", vote.Result == EventResult.Default && v2.Result == EventResult.Allow && v2.Decide(false),
                $"{vote.Result} then {v2.Result}");
        }

        var order = new List<string>();
        using (Events.On<VoteEvent>(_ => order.Add("inner")))
        using (Events.On<PingEvent>(_ =>
        {
            order.Add("outer-before");
            Events.Post(new VoteEvent());
            order.Add("outer-after");
        }))
        {
            Events.Post(new PingEvent());
            Check("events.reentrant", string.Join(",", order) == "outer-before,inner,outer-after", string.Join(",", order));
        }

        var late = 0;
        var adder = Events.On<PingEvent>(_ => subs.Add(Events.On<PingEvent>(_ => late++)));
        Events.Post(new PingEvent());
        var first = late;
        adder.Dispose();
        Events.Post(new PingEvent());
        Check("events.subscribe-during-dispatch", first == 0 && late == 1, $"first post {first}, second {late}");
        foreach (var s in subs) s.Dispose();

        var n = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 1_000_000; i++)
            if (!Events.HasSubscribers<VoteEvent>() && Events.HasSubscribers<PingEvent>()) n++;
        sw.Stop();
        Check("events.has-subscribers-fast", n == 1_000_000 && sw.Elapsed.TotalMilliseconds < 500,
            $"2,000,000 checks in {sw.Elapsed.TotalMilliseconds:F1} ms");
        Check("events.handler-stats", Events.Diagnostics.Handlers.Any(h => h.Owner == "api-test" && h.Handler.EndsWith("OnPingHigh") && h.Stats.Calls >= 5),
            string.Join("; ", Events.Diagnostics.Handlers.Where(h => h.Owner == "api-test").Select(h => $"{h.Handler} {h.Stats.Calls} calls {h.Stats.AverageMs:F4} ms")));
    }

    // ------------------------------------------------------------------------------- config (synchronous) ----
    private void ConfigSyncChecks()
    {
        var text = File.ReadAllText(_config.FilePath);
        Check("config.file-generated", text.Contains("// How many homes a player may set.") && text.Contains("Range: 1 .. 50") &&
                                       text.Contains(_load == 1 ? "\"maxHomes\": 3" : "\"maxHomes\": ") && text.Contains("\"limits\": {"),
            _config.FilePath);
        Check("config.registry", ReferenceEquals(Config.Find("api-test"), _config) && _config.Kind == ConfigKind.Common);
        Check("config.refuses-out-of-range", !_config.TrySet("limits.speed", 20, out var err) && err.Contains("0..10"), err);
        var schema = _config.Schema();
        Check("config.schema", schema["entries"]!.AsArray().Any(e => e!["path"]!.GetValue<string>() == "maxHomes" && e["max"]!.GetValue<double>() == 50),
            $"{schema["entries"]!.AsArray().Count} entries");
    }

    // ------------------------------------------------------------------------------- commands (synchronous) --
    private void CommandChecks()
    {
        CommandResult Run(string line, CommandSource? s = null) => Commands.Execute(s ?? CommandSource.Console, line);
        Check("commands.greedy", Run("apitest echo hello  big world").Output.SingleOrDefault() == "hello  big world");
        var bad = Run("apitest echo");
        Check("commands.missing-arg", bad.ParseError && bad.Error!.Contains("incomplete command, expected <text>"), bad.Error ?? "");
        var r = Run("apitest wait 1m30s 7:30pm");
        Check("commands.duration+time", r.Output.SingleOrDefault() == "90s until 19:30", r.Text);
        r = Run("apitest mode STORM on");
        Check("commands.enum+bool", r.Output.SingleOrDefault() == "Storm True", r.Text);
        r = Run("apitest mode hail");
        Check("commands.enum-error", r.Error?.Contains("'hail' is not one of clear, rain, storm") == true, r.Error ?? "");
        r = Run("apitest tp 1 2.5 -3");
        Check("commands.position", r.Output.SingleOrDefault() == "to (1.0, 2.5, -3.0)", r.Text);
        r = Run("apitest tp ~ ~ ~");
        Check("commands.relative-needs-player", r.Error?.Contains("~ needs a player position") == true, r.Error ?? "");
        r = Run("apitest heal nobody");
        Check("commands.player-error", r.Error?.Contains("no player 'nobody' is online") == true, r.Error ?? "");
        r = Run("apitest bogus");
        Check("commands.unknown-sub", r.Error?.Contains("unknown 'bogus', expected") == true && r.Error.Contains("Usage: /apitest"), r.Error ?? "");
        var help = Run("help apitest");
        Check("commands.help", help.Ok && help.Output.Any(l => l.Contains("/apitest heal <target> [amount]")) &&
                               help.Output.Any(l => l.Contains("<amount>: whole number 1..1000")) &&
                               help.Output.Any(l => l.Contains("permission: api-test.command.apitest")), string.Join(" / ", help.Output));
        var c = Commands.Complete(CommandSource.Console, "/apitest m");
        Check("commands.complete-sub", c.Suggestions.SequenceEqual(["mode"]) && c.Start == 9, $"{c.Start}: {string.Join(",", c.Suggestions)}");
        c = Commands.Complete(CommandSource.Console, "/apitest mode ");
        Check("commands.complete-enum", c.Suggestions.SequenceEqual(["clear", "rain", "storm"]), string.Join(",", c.Suggestions));
        var userRemote = CommandSource.Remote("api-test-tool", admin: false);
        Check("commands.remote-user-denied", Run("apitest heal x", userRemote).Denied && Run("apitest echo ok", userRemote).Ok);
        Check("commands.secret-console-only", Run("apitest secret").Output.SingleOrDefault() == "42" &&
                                              Run("apitest secret", CommandSource.Remote("tool")).Ok);
        var json = Commands.Find("apitest")!.ToJson();
        Check("commands.describe", json["tree"]?["children"]?.AsArray().Count == 9, $"{json["tree"]?["children"]?.AsArray().Count} subcommands");

        Commands.Register(Commands.Literal("apitest-b").Describe("builder command")
            .Then(Commands.Argument("n", Arg.Int(0, 9)).Executes(x => x.Reply($"n={x.Get<int>("n")}"))
                .Then(Commands.Literal("twice").Executes(x => x.Reply($"n={x.Get<int>("n") * 2}")))));
        Check("commands.builder", Run("apitest-b 4").Output.SingleOrDefault() == "n=4" && Run("apitest-b 4 twice").Output.SingleOrDefault() == "n=8" &&
                                  Run("apitest-b 12").Error!.Contains("out of range (whole number 0..9)"));
    }

    // ------------------------------------------------------------------------------- permissions (synchronous)
    private void PermissionChecks()
    {
        var groups = Permissions.ServerUserGroups;  // first use reads (or writes the default) permissions.json
        Check("permissions.file", File.Exists(Permissions.FilePath), Permissions.FilePath);
        Check("permissions.server-user-groups", groups.Count >= 3, string.Join(", ", groups.Select(g => $"{g.Name}={g.Rights}")));
        var nodes = Permissions.Nodes.Select(n => n.Node).ToHashSet();
        Check("permissions.nodes-registered", nodes.Contains("api-test.command.apitest") && nodes.Contains("api-test.vip") &&
                                              nodes.Contains("api-test.command.apitest.secret") && nodes.Contains("api-test.fly"),
            string.Join(", ", nodes.Where(n => n.StartsWith("api-test"))));
        var guestRights = PlayerPermissions.EditWorld | PlayerPermissions.ReceiveExp;
        var guest = PermissionSubject.ForSteamId(76561190000000002, "Gus", guestRights);
        var admin = PermissionSubject.ForSteamId(76561190000000001, "Alder", (PlayerPermissions)0x3F);
        Check("permissions.user-group-mapping", Permissions.UserGroupOf(admin) == "Admin" && Permissions.GroupsOf(admin).Contains("admin"),
            $"admin -> {Permissions.UserGroupOf(admin)} [{string.Join(",", Permissions.GroupsOf(admin))}], guest -> {Permissions.UserGroupOf(guest)}");
        Check("permissions.defaults", Permissions.Has(guest, "api-test.fly") && !Permissions.Has(guest, "api-test.vip") &&
                                      Permissions.Has(admin, "api-test.vip"));
        var secret = Permissions.Explain(admin, "api-test.command.apitest.secret");
        Check("permissions.wildcard-not-console", !secret.Allowed, secret.ToString());
    }

    // ------------------------------------------------------------------------------- the lab run ------------
    private async Task RunAsync()
    {
        await TryAsync("events.enqueue", async () =>
        {
            var tick = 0;
            var thread = false;
            using var sub = Events.On<VoteEvent>(e =>
            {
                tick++;
                thread = Game.IsTickThread;
            });
            var threw = await Task.Run(() =>
            {
                try
                {
                    Events.Post(new VoteEvent());
                    return false;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            });
            await Task.Run(() => Events.Enqueue(new VoteEvent()));
            var ok = await Until(() => tick == 1, 10);
            Check("events.thread-check", threw, "Post off the tick thread throws");
            Check("events.enqueue", ok && thread, $"delivered {tick}x, on the tick thread: {thread}");
        });

        if (!await Until(() => _running, 24 * 240))
        {
            Check("world.running", false, "the world did not reach Run in 4 minutes");
            return;
        }
        await Steps(25);
        if (_load == 1) await TryAsync("config", ConfigLive);
        await TryAsync("console", ConsoleFolder);
        await TryAsync("chat", ChatEvents);
        await TryAsync("time", TimeEvents);
        await TryAsync("settings", SettingsEvents);
        await TryAsync("spawn", SpawnEvents);
        await TryAsync("items", ItemsEvents);
        if (_load == 1) await TryAsync("players", PlayerEvents);
        await TryAsync("permissions.live", PermissionsLive);
        Log.Info($"api-test: {_pass} PASS, {_fail} FAIL");
    }

    private async Task ConfigLive()
    {
        var file = _config.FilePath;
        var before = _configEvents.Count;
        var text = File.ReadAllText(file).Replace("\"maxHomes\": 3", "\"maxHomes\": 99").Replace("\"motd\": \"hello\"", "\"motd\": \"edited\"")
            .Replace("\"port\": 8080", "\"port\": 9090");
        File.WriteAllText(file, text);
        var steps = 0;
        var got = await Until(() =>
        {
            steps++;
            return _configEvents.Count > before;
        }, 120);
        var ev = got ? _configEvents[before] : null;
        Check("config.edit-event", ev is { Source: ConfigChangeSource.File } && ev.Changed.Contains("maxHomes") && ev.Changed.Contains("motd"),
            $"after {steps} step(s) ({(steps <= 6 ? "file watcher" : "polling")}): {ev}");
        Check("config.clamped", _config.Value.MaxHomes == 50 && _config.Value.Motd == "edited" && _config.Warnings.Any(w => w.Contains("using 50")),
            string.Join("; ", _config.Warnings));
        Check("config.file-corrected", File.ReadAllText(file).Contains("\"maxHomes\": 50"));
        Check("config.requires-restart", ev?.PendingRestart.Contains("port") == true && _config.Value.Port == 8080 &&
                                         _config.PendingRestart.Contains("port"), $"running {_config.Value.Port}");
        Check("config.local-handler", _monitor.Any(m => m.StartsWith("changed-handler:") && m.Contains("maxHomes")), string.Join(" ", _monitor));
        var n = _configEvents.Count;
        _config.Set("limits.speed", 2.5);
        Check("config.set", _configEvents.Count == n + 1 && _configEvents[^1].Source == ConfigChangeSource.Api &&
                            _config.Value.Limits.Speed == 2.5 && File.ReadAllText(file).Contains("\"speed\": 2.5"));
        Check("config.synced-values", _config.SyncedValuesJson() == "{\"limits\":{\"speed\":2.5}}", _config.SyncedValuesJson());
        // Broken JSON: values kept, the file left alone.
        File.WriteAllText(file, "{ \"maxHomes\": 7, oops");
        await Steps(30);
        Check("config.broken-file-kept", _config.Value.MaxHomes == 50 && _config.LastError is not null &&
                                         File.ReadAllText(file).StartsWith("{ \"maxHomes\": 7, oops"), _config.LastError ?? "");
        _config.Save();
        Check("config.save", File.ReadAllText(file).Contains("\"maxHomes\": 50"));
    }

    private async Task ConsoleFolder()
    {
        // kiln/control: next to kiln/host (Kiln.Api's folder), not next to the config folder (a profile moves that)
        var kiln = Path.GetDirectoryName(Path.GetDirectoryName(typeof(Kiln.Mod).Assembly.Location)!)!;
        var dir = Path.Combine(kiln, "control", "console");
        Directory.CreateDirectory(dir);
        var name = $"api-test-{Environment.ProcessId}";
        File.WriteAllText(Path.Combine(dir, name + ".cmd"), """
            apitest echo from the console folder
            apitest whoami
            #as remote api-test-remote user
            apitest heal Alder
            apitest whoami
            #as steam 76561190000000002 Gus
            apitest vip
            #complete /apitest e
            """);
        var result = Path.Combine(dir, name + ".result.json");
        var ok = await Until(() => File.Exists(result), 100);
        if (!Check("console.result-file", ok, result)) return;
        var doc = JsonNode.Parse(File.ReadAllText(result))!;
        var r = doc["results"]!.AsArray();
        string Out(int i) => r[i]?["output"]?.AsArray().FirstOrDefault()?.GetValue<string>() ?? "";
        Check("console.runs-as-console", Out(0) == "from the console folder" && Out(1) == "console console", $"{Out(0)} / {Out(1)}");
        Check("console.remote-user", r[2]!["denied"]!.GetValue<bool>() && Out(3) == "remote api-test-remote", r[2]!.ToJsonString());
        Check("console.steam-source-denied", r[4]!["denied"]!.GetValue<bool>(), r[4]!.ToJsonString());
        Check("console.complete", r[5]!["completions"]!.AsArray().Select(x => x!.GetValue<string>()).SequenceEqual(["echo"]), r[5]!.ToJsonString());
        File.Delete(result);
    }

    private async Task ChatEvents()
    {
        if (!Chat.Available || !Chat.Status.SendReady)
        {
            Check("chat.available", false, $"kiln.chat {(Chat.Available ? "not ready" : "unavailable")}: {Chat.Status}");
            return;
        }
        var seen = new List<ChatMessageEvent>();
        var executed = new List<CommandExecutedEvent>();
        using var a = Events.On<ChatMessageEvent>(e =>
        {
            seen.Add(e);
            if (e.Text.Contains("drop me")) e.Cancel();
            if (e.Text.StartsWith("/apitest-alias ")) e.Text = "/apitest echo rewritten " + e.Text["/apitest-alias ".Length..];
        });
        using var b = Events.On<CommandExecutedEvent>(executed.Add);
        using var c = Events.On<CommandExecutingEvent>(e =>
        {
            if (e.Line.Contains("blocked"))
            {
                e.Cancel();
                e.CancelMessage = "blocked by api-test";
            }
        });
        var consumed = Chat.Status.Consumed;
        Chat.Diagnostics.Inject(0, "api-test plain line");
        Chat.Diagnostics.Inject(0, "api-test drop me");
        Chat.Diagnostics.Inject(0, "/apitest-alias hi there");
        await Steps(5);
        Check("chat.event", seen.Count == 3 && seen[0].Text == "api-test plain line" && !seen[0].Canceled && !seen[0].IsCommand,
            string.Join(" | ", seen.Select(s => $"{s.Text} canceled={s.Canceled}")));
        Check("chat.cancel-consumes", seen.Count > 1 && seen[1].Canceled && Chat.Status.Consumed >= consumed + 2,
            $"consumed {consumed} -> {Chat.Status.Consumed}");
        var last = Chat.Diagnostics.LastLine();
        Check("chat.plain-passed-on", last?.Text == "api-test plain line", $"inbox: {last?.Text}");
        var rewritten = executed.FirstOrDefault(e => e.Command.Name == "apitest");
        Check("chat.rewrite-runs-command", rewritten?.Result.Output.SingleOrDefault() == "rewritten hi there",
            $"{rewritten?.Line} -> {rewritten?.Result}");
        var blocked = Commands.Execute(CommandSource.Console, "apitest echo blocked");
        Check("commands.executing-cancel", blocked.Canceled && blocked.Error == "blocked by api-test" &&
                                           executed[^1].Result.Canceled, blocked.Text);
        Check("commands.executed-event", executed.Count >= 2 && executed.All(e => e.ElapsedMs >= 0));
    }

    private async Task TimeEvents()
    {
        if (!Time.Available || !Time.Ready)
        {
            Check("time.available", false);
            return;
        }
        var original = Time.OfDay;
        var sets = new List<TimeSetEvent>();
        var days = new List<DayStartedEvent>();
        var nights = new List<NightStartedEvent>();
        using var a = Events.On<TimeSetEvent>(sets.Add);
        using var b = Events.On<DayStartedEvent>(days.Add);
        using var c = Events.On<NightStartedEvent>(nights.Add);
        var st = Time.State;
        var midday = (st.DayStart + st.NightStart) / 2;
        Time.Set(midday);
        Check("time.set-event", sets.Count == 1 && Math.Abs(sets[0].Hours - midday) < 0.01 && sets[0].By == "api-test",
            $"{sets.FirstOrDefault()} (day phase {Time.Format(st.DayStart)}-{Time.Format(st.NightStart)})");
        await Steps(30);
        Time.Set(st.NightStart + 0.5);
        var night = await Until(() => nights.Count > 0, 60);
        Check("time.night-started", night, nights.FirstOrDefault() is { } n ? $"at {Time.Format(n.Hours)}" : "none");
        Time.Set(st.DayStart + 0.5);
        var day = await Until(() => days.Count > 0, 60);
        Check("time.day-started", day, days.FirstOrDefault() is { } d ? $"at {Time.Format(d.Hours)}" : "none");
        Time.Set(original);
        Check("time.restored", sets.Count == 4, $"{sets.Count} set events, back to {Time.Format(original)}");
    }

    private Task SettingsEvents()
    {
        if (!Settings.Available || !Settings.Ready)
        {
            Check("settings.available", false);
            return Task.CompletedTask;
        }
        var changes = new List<SettingChangedEvent>();
        using var a = Events.On<SettingChangedEvent>(changes.Add);
        const string name = "playerHealthFactor";
        var before = Convert.ToDouble(Settings.Get(name));
        var target = Math.Abs(before - 1.5) < 0.01 ? 1.25 : 1.5;
        Settings.Set(name, target, resend: false);
        Settings.Set(name, before, resend: false);
        Check("settings.changed-event", changes.Count == 2 && changes[0].Name == name && changes[0].By == "api-test" &&
                                        Math.Abs(Convert.ToDouble(changes[0].After) - target) < 0.001 && !changes[0].FromGame,
            string.Join(" | ", changes));
        return Task.CompletedTask;
    }

    private async Task SpawnEvents()
    {
        if (!Spawn.Available || !await Until(() => Spawn.WorldRunning, 200))
        {
            Check("spawn.available", false);
            return;
        }
        WorldPosition? anchor = null;
        foreach (var filter in new[] { "FlameAltar", "Altar", "Townsfolk" })
        {
            foreach (var id in Spawn.FindByName(filter, max: 4))
                if (Spawn.Inspect(id) is { } info)
                {
                    anchor = info.Position;
                    break;
                }
            if (anchor is not null) break;
        }
        if (!Check("spawn.anchor", anchor is not null)) return;
        var spawned = new List<EntitySpawnedEvent>();
        var destroyed = new List<EntityDestroyedEvent>();
        using var a = Events.On<EntitySpawnedEvent>(spawned.Add);
        using var b = Events.On<EntityDestroyedEvent>(destroyed.Add);
        var at = new WorldPosition(anchor!.Value.X + 25, anchor.Value.Y + 0.5, anchor.Value.Z + 25);
        var barrel = Spawn.Lookup("Prop_Deco_Steppeprops_Barrel_01")?.Name ?? "Prop_Deco_Steppeprops_Barrel_01";
        var e = await Spawn.Entity(barrel, at, 0, new SpawnOptions { Tag = "api-test", Flags = SpawnFlags.Transient });
        Check("spawn.spawned-event", spawned.Count == 1 && spawned[0].Entity == e && spawned[0].Origin == "api-test" && spawned[0].Kind == SpawnedKind.Entity,
            spawned.FirstOrDefault()?.ToString() ?? "none");
        Spawn.Destroy(e);
        Check("spawn.destroyed-event", destroyed.Count == 1 && destroyed[0].Entity == e && destroyed[0].Reason == "destroy",
            destroyed.FirstOrDefault()?.ToString() ?? "none");
    }

    private async Task ItemsEvents()
    {
        if (!Items.Available || !Items.CanTransact)
        {
            Check("items.available", false, Items.Status()?.ToString() ?? "kiln.items unavailable");
            return;
        }
        var wood = Items.Lookup("Material_T1_Wood_Logs");
        if (!Check("items.catalog", wood is not null)) return;
        EntityId chest = default;
        foreach (var id in Items.FindInventories(InventoryKinds.Containers))
            if (Items.Read(id) is { Initialized: true } c && c.FreeSlots() >= 2)
            {
                chest = id;
                break;
            }
        if (!Check("items.container", !chest.IsNone, $"container {chest}")) return;
        var given = new List<ItemsGivenEvent>();
        var taken = new List<ItemsTakenEvent>();
        using var a = Events.On<ItemsGivenEvent>(given.Add);
        using var b = Events.On<ItemsTakenEvent>(taken.Add);
        var g = await Items.Give(chest, wood!.Id, 2);
        Check("items.given-event", g.Succeeded && given.Count == 1 && given[0].Count == 2 && given[0].Origin == "api-test" && given[0].Target == chest,
            $"{g} / {given.FirstOrDefault()}");
        var slot = g.Changes.FirstOrDefault().Slot;
        var t = await Items.Take(chest, wood.Id, 2, slot, exactSlot: true);
        Check("items.taken-event", t.Succeeded && taken.Count == 1 && taken[0].Count == 2, $"{t} / {taken.FirstOrDefault()}");
    }

    private async Task PlayerEvents()
    {
        var sim = Players.Diagnostics.Status?.Simulated == true;
        if (!sim)
        {
            Log.Info("api-test: players are not simulated (KILN_PLAYERS_SIMULATE unset): join/leave events need a client");
            return;
        }
        var both = await Until(() => _joined.Count >= 2, 24 * 120);
        Check("players.joined-events", both && _joined.Any(j => j.Player.Name == "Alder") && _joined.Any(j => j.Player.Name == "Gus"),
            string.Join(", ", _joined.Select(j => $"{j.Player.Name} {j.Player.Entity}")));
        var gus = Players.All.FirstOrDefault(p => p.Name == "Gus");
        var alder = Players.All.FirstOrDefault(p => p.Name == "Alder");
        if (gus is not null && alder is not null && _left.Count == 0)
        {
            var heal = Commands.Execute(CommandSource.Console, "apitest heal gu 5");
            Check("commands.player-arg-live", heal.Output.SingleOrDefault() == "healed Gus by 5", heal.Text);
            var asGus = CommandSource.ForPlayer(gus.Entity, gus.Name);
            var asAlder = CommandSource.ForPlayer(alder.Entity, alder.Name);
            var who = Commands.Execute(asGus, "apitest whoami");
            Check("commands.player-source", who.Output.SingleOrDefault()?.StartsWith("Gus (entity") == true, who.Text);
            Check("permissions.player-denied", Commands.Execute(asGus, "apitest vip").Denied && Commands.Execute(asGus, "apitest heal alder").Denied,
                $"Gus rights {gus.Permissions} ({(gus.PermissionsKnown ? "known" : "unknown")}), user group {Permissions.UserGroupOf(PermissionSubject.ForPlayer(gus))}");
            Check("permissions.player-admin", Commands.Execute(asAlder, "apitest vip").Ok && Commands.Execute(asAlder, "apitest heal gus 1").Ok,
                $"Alder rights {alder.Permissions}, groups {string.Join(",", Permissions.GroupsOf(PermissionSubject.ForPlayer(alder)))}");
            Check("permissions.player-console-node", Commands.Execute(asAlder, "apitest secret").Denied, "admin '*' does not grant console nodes");
            var tp = Commands.Execute(asGus, "apitest tp ~ ~2 ~");
            Check("commands.relative-position-live", tp.Ok || tp.Error?.Contains("position is unknown") == true, tp.Text);
            Permissions.SetPlayerNode(gus.SteamId, "api-test.vip", true, "Gus");
            Check("permissions.grant", Commands.Execute(asGus, "apitest vip").Ok);
            Permissions.SetPlayerNode(gus.SteamId, "api-test.vip", null);
        }
        var gone = await Until(() => _left.Count >= 2, 24 * 400);
        Check("players.left-events", gone, string.Join(", ", _left.Select(l => l.Player.Name)));
    }

    private async Task PermissionsLive()
    {
        var changed = new List<PermissionsChangedEvent>();
        using var a = Events.On<PermissionsChangedEvent>(changed.Add);
        const ulong steam = 76561190000000002;
        var subject = PermissionSubject.ForSteamId(steam, "Gus", PlayerPermissions.EditWorld | PlayerPermissions.ReceiveExp);
        Permissions.SetPlayerNode(steam, "api-test.vip", true, "Gus");
        Check("permissions.api-edit", Permissions.Has(subject, "api-test.vip") && changed.Count == 1 && changed[0].Reason == "api" &&
                                      File.ReadAllText(Permissions.FilePath).Contains("\"api-test.vip\""));
        var text = File.ReadAllText(Permissions.FilePath).Replace("\"nodes\": [\"api-test.vip\"]", "\"nodes\": [\"-api-test.*\"]");
        await Steps(2);
        File.WriteAllText(Permissions.FilePath, text);
        var reloaded = await Until(() => changed.Count >= 2, 24 * 4);
        var d = Permissions.Explain(subject, "api-test.fly");
        Check("permissions.file-reload", reloaded && changed[^1].Reason == "file" && !d.Allowed && d.From == "player", d.ToString());
        Permissions.SetPlayerNode(steam, "api-test.*", null);
        Permissions.SetPlayerNode(steam, "api-test.vip", null);
        Check("permissions.cleared", Permissions.Has(subject, "api-test.fly") && !Permissions.Has(subject, "api-test.vip"));
    }
}
