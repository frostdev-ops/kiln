// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.Api.Tests;

public enum Mode
{
    Forward,
    Reset,
}

public sealed class AttributeCommands
{
    public readonly List<string> Calls = [];

    [Command("heal", Description = "heals a player", Permission = CommandPermission.Admin, Aliases = ["hl"])]
    public string Heal(CommandContext c, Player target, [Range(1, 1000)] int amount = 100)
    {
        Calls.Add($"heal {target.Name} {amount}");
        return $"healed {target.Name} by {amount}";
    }

    [Command("zone create", Description = "creates a zone")]
    public void Create(CommandContext c, string name, WorldPosition at, [Range(1, 500)] double radius = 10)
    {
        Calls.Add($"create {name} {at} {radius}");
    }

    [Command("zone list")]
    public string List() => "zones: none";

    [Command("zone delete", Permission = CommandPermission.Console)]
    public void Delete(string name) => Calls.Add("delete " + name);

    [Command("say")]
    public string Say([Greedy] string text) => "said " + text;

    [Command("wait")]
    public string Wait(TimeSpan time, [TimeOfDay] double until, Mode mode = Mode.Forward) =>
        $"{time.TotalSeconds}s until {until} {mode}";

    [Command("pick")]
    public string Pick([Choice("red", "green")] string color, bool loud = false) => $"{color} {loud}";

    [Command("all")]
    public string All(IReadOnlyList<Player> who) => string.Join(",", who.Select(p => p.Name));
}

/// <summary>Async commands: one that waits for the test, one that refuses later, one that refuses before its first await.</summary>
public sealed class AsyncCommands
{
    public readonly TaskCompletionSource Gate = new();

    [Command("slow")]
    public async Task Slow(CommandContext c)
    {
        await Gate.Task;
        c.Reply("done");
    }

    [Command("slowfail")]
    public async Task SlowFail(CommandContext c)
    {
        await Gate.Task;
        throw new CommandException("refused later");
    }

    [Command("refuse")]
    public async Task Refuse(CommandContext c)
    {
        if (c.Source.Player.IsNone) throw new CommandException("only a player in the world can do this");
        await Gate.Task;
    }
}

public sealed class CommandsTests : KilnTest
{
    private static readonly Player Alder = new()
    {
        Entity = new EntityId(1), Name = "Alder", SteamId = 76561190000000001, State = PlayerState.InGame,
        Permissions = PlayerPermissions.KickBan | PlayerPermissions.EditWorld, PermissionsKnown = true,
    };

    private static readonly Player Alan = new()
    {
        Entity = new EntityId(2), Name = "Alan", SteamId = 76561190000000002, State = PlayerState.InGame,
        Permissions = PlayerPermissions.EditWorld, PermissionsKnown = true,
    };

    public CommandsTests()
    {
        Arg.OnlinePlayers = () => [Alder, Alan];
        Arg.PositionOf = e => e.Value == 1 ? new WorldPosition(100, 50, -20) : null;
        Permissions.FindPlayer = e => e.Value switch { 1 => Alder, 2 => Alan, _ => null };
        Permissions.FindPlayerBySteamId = id => id == Alder.SteamId ? Alder : id == Alan.SteamId ? Alan : null;
    }

    private static CommandResult Run(string line, CommandSource? source = null) => Commands.Execute(source ?? CommandSource.Console, line);

    [Fact]
    public void Plain_commands_still_work()
    {
        var mod = NewMod("legacy");
        As(mod, () => Commands.Register("echo", c => c.Reply(string.Join("|", c.Args)), CommandPermission.Everyone, "<words...>", "echoes", "ec"));
        var r = Run("/EC a \"b c\"");
        Assert.True(r.Ok, r.Text);
        Assert.Equal(["a|b c"], r.Output);
        Assert.Equal("legacy.command.echo", Commands.Find("echo")!.PermissionNode);
        Assert.NotNull(Permissions.Find("legacy.command.echo"));
        Assert.False(Run("nope").Found);
        mod.Retire();
        Assert.Null(Commands.Find("echo"));
    }

    [Fact]
    public void Builder_tree_parses_typed_arguments()
    {
        var got = new List<string>();
        Commands.Register(Commands.Literal("give")
            .Then(Commands.Argument("player", Arg.Player())
                .Then(Commands.Argument("count", Arg.Int(1, 64)).Executes(c => got.Add($"{c.Get<Player>("player").Name} x{c.Get<int>("count")}"))))
            .Then(Commands.Literal("all").Executes(c => got.Add("all"))));
        Assert.True(Run("give al 5").Ok == false);  // ambiguous prefix
        Assert.Contains("matches 2 players", Run("give al 5").Error);
        Assert.True(Run("give ALDER 5").Ok);
        Assert.True(Run("give 2 64").Ok);
        Assert.True(Run("give all").Ok);
        Assert.Equal(["Alder x5", "Alan x64", "all"], got);
        var r = Run("give alder 65");
        Assert.True(r.ParseError);
        Assert.Contains("65 is out of range (whole number 1..64)", r.Error);
        Assert.Contains("Usage: /give <player> <count> | /give all", r.Error);
        Assert.Contains("incomplete command, expected <count>", Run("give alder").Error);
        Assert.Contains("too many arguments: 'extra'", Run("give alder 3 extra").Error);
        Assert.Contains("no player 'zed' is online", Run("give zed 1").Error);
    }

    [Fact]
    public void Attribute_commands_with_optional_arguments_and_subcommands()
    {
        var target = new AttributeCommands();
        var mod = NewMod("attr");
        var regs = As(mod, () => Commands.RegisterAll(target));
        Assert.Equal(6, regs.Count);  // heal, zone, say, wait, pick, all
        Assert.Equal("healed Alder by 100", Run("heal alder").Output.Single());
        Assert.Equal("healed Alan by 7", Run("/hl Alan 7").Output.Single());
        Assert.Contains("out of range", Run("heal alder 0").Error);
        var heal = Commands.Find("heal")!;
        Assert.Equal(["heal <target> [amount]"], heal.UsageLines);
        Assert.Equal("attr.command.heal", heal.PermissionNode);
        Assert.Equal(CommandPermission.Admin, heal.Permission);

        Assert.True(Run("zone create spawn 1 2 3").Ok);
        Assert.True(Run("zone create \"big zone\" 1 2 3 250").Ok);
        Assert.Equal(["create spawn (1.0, 2.0, 3.0) 10", "create big zone (1.0, 2.0, 3.0) 250"], target.Calls.Skip(2));
        Assert.Equal("zones: none", Run("zone list").Output.Single());
        Assert.Contains("unknown 'explode', expected", Run("zone explode").Error);
        Assert.Equal("said hello  there world", Run("say hello  there world").Output.Single());
        Assert.Equal("90s until 19.5 Forward", Run("wait 1m30s 7:30pm").Output.Single());
        Assert.Equal("5s until 12 Reset", Run("wait 5 noon reset").Output.Single());
        Assert.Contains("is not a duration", Run("wait soon noon").Error);
        Assert.Equal("green True", Run("pick GREEN on").Output.Single());
        Assert.Contains("one of red, green", Run("pick blue").Error);
        Assert.Equal("Alder,Alan", Run("all @a").Output.Single());
        Assert.Contains("@s needs a player", Run("all @s").Error);
    }

    [Fact]
    public void Relative_positions_use_the_source_player()
    {
        WorldPosition? at = null;
        Commands.Register(Commands.Literal("tp").Then(Commands.Argument("to", Arg.Position()).Executes(c => at = c.Get<WorldPosition>("to"))));
        Assert.True(Commands.Execute(CommandSource.ForPlayer(new EntityId(1), "Alder"), "tp ~ ~10 ~-5.5").Ok);
        Assert.Equal(new WorldPosition(100, 60, -25.5), at);
        Assert.Contains("~ needs a player position", Run("tp ~ 1 1").Error);
        Assert.Contains("not a coordinate", Run("tp 1 x 1").Error);
    }

    [Fact]
    public void Usage_help_and_completion()
    {
        As(NewMod("attr"), () => Commands.RegisterAll(new AttributeCommands()));
        var help = Run("help zone");
        Assert.True(help.Ok, help.Text);
        Assert.Contains("  /zone create <name> <at> [radius]", help.Output);
        Assert.Contains("  /zone list", help.Output);
        Assert.Contains("  /zone delete <name>", help.Output);
        Assert.Contains(help.Output, l => l.Contains("<radius>: number 1..500"));
        Assert.Contains(help.Output, l => l.Contains("permission: attr.command.zone"));
        var sub = Run("help zone create");
        Assert.DoesNotContain("  /zone list", sub.Output);

        Assert.Equal(["heal", "help", "hl"], Commands.Complete(CommandSource.Console, "/h").Suggestions);
        var c = Commands.Complete(CommandSource.Console, "/zone ");
        Assert.Equal(6, c.Start);
        Assert.Equal(["create", "list", "delete"], c.Suggestions);
        Assert.Equal(["Alder", "Alan"], Commands.Complete(CommandSource.Console, "/heal Al").Suggestions);
        Assert.Equal(["forward"], Commands.Complete(CommandSource.Console, "/wait 5 noon f").Suggestions);
        Assert.Contains("<amount>", Commands.Complete(CommandSource.Console, "/heal alder ").Suggestions);
        var json = Commands.Find("zone")!.ToJson();
        Assert.Equal("zone", json["name"]!.GetValue<string>());
        Assert.Equal(3, json["tree"]!["children"]!.AsArray().Count);
    }

    [Fact]
    public void Permissions_decide_for_players()
    {
        As(NewMod("attr"), () => Commands.RegisterAll(new AttributeCommands()));
        var guest = CommandSource.ForSteamId(Alan.SteamId, "Alan");
        var admin = CommandSource.ForPlayer(Alder.Entity, "Alder");
        // heal is Admin: Alan (no kick/ban) may not, Alder may (kick/ban right).
        Assert.True(Commands.Execute(guest, "heal alder").Denied);
        Assert.True(Commands.Execute(admin, "heal alder").Ok);
        // zone delete is Console: the admin group's "*" does not grant it.
        Permissions.Reload();
        var del = Commands.Execute(admin, "zone delete x");
        Assert.True(del.Denied, del.Text);
        Assert.Contains("You may not use /zone delete", del.Error);
        Assert.DoesNotContain("  /zone delete <name>", Commands.Execute(admin, "help zone").Output);
        // Grant by exact name in permissions.json.
        Permissions.SetPlayerNode(Alan.SteamId, "attr.command.heal", true);
        Assert.True(Commands.Execute(guest, "heal alder").Ok);
        Permissions.SetPlayerNode(Alder.SteamId, "attr.command.zone.delete", true);
        Assert.True(Commands.Execute(admin, "zone delete x").Ok);
        // Remote tools: declared rights.
        Assert.True(Commands.Execute(CommandSource.Remote("tool", admin: false), "heal alder").Denied);
        Assert.True(Commands.Execute(CommandSource.Remote("tool"), "heal alder").Ok);
        Assert.True(Commands.Allowed(guest, Commands.Find("zone")!));
    }

    [Fact]
    public void Executing_events_cancel_and_executed_events_report()
    {
        Commands.Register("ping", c => c.Reply("pong"));
        var mod = NewMod("gate");
        As(mod, () => Events.On<CommandExecutingEvent>(e =>
        {
            if (e.Args.Count > 0 && e.Args[0] == "no")
            {
                e.Cancel();
                e.CancelMessage = "not today";
            }
        }));
        var done = new List<CommandExecutedEvent>();
        Events.On<CommandExecutedEvent>(done.Add);
        Assert.Equal(["pong"], Run("ping").Output);
        var r = Run("ping no");
        Assert.True(r.Canceled);
        Assert.Equal("not today", r.Error);
        Assert.Equal(2, done.Count);
        Assert.True(done[0].Result.Ok);
        Assert.True(done[1].Result.Canceled);
    }

    [Fact]
    public void Console_folder_runs_lines_as_its_sources()
    {
        Commands.Register("whoami", c => c.Reply(c.Source.ToString()));
        Commands.Register("secret", c => c.Reply("42"), CommandPermission.Admin);
        var dir = Path.Combine(Root, "control", "console");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "t1.cmd"), """
            whoami
            // a comment
            #as remote dash user
            whoami
            secret
            #as steam 76561190000000002 Alan
            secret
            #as player alder
            whoami
            #complete /wh
            nope
            """);
        Assert.Equal(1, ConsoleControl.Poll(dir));
        Assert.False(File.Exists(Path.Combine(dir, "t1.cmd")));
        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "t1.result.json")))!;
        var results = doc["results"]!.AsArray();
        Assert.False(doc["ok"]!.GetValue<bool>());
        Assert.Equal("console console", results[0]!["output"]![0]!.GetValue<string>());
        Assert.Equal("remote dash", results[1]!["output"]![0]!.GetValue<string>());
        Assert.True(results[2]!["denied"]!.GetValue<bool>());
        Assert.True(results[3]!["denied"]!.GetValue<bool>());
        Assert.Equal("Alder (entity 1)", results[4]!["output"]![0]!.GetValue<string>());
        Assert.Equal("whoami", results[5]!["completions"]![0]!.GetValue<string>());
        Assert.False(results[6]!["found"]!.GetValue<bool>());
    }

    // A plain command that needs a player, as the samples' world commands do. Registered by a mod, so the reset between tests removes it.
    private void RegisterPlayerOnly() => As(NewMod("here"), () =>
        Commands.Register("here", c =>
        {
            if (c.Source.Player.IsNone || Arg.PositionOf(c.Source.Player) is not { } at) throw new CommandException("only a player in the world can do this");
            c.Reply($"{c.Source.Name} at {at.X}");
        }));

    private JsonNode RunFile(string name, string text)
    {
        var dir = Path.Combine(Root, "control", "console");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".cmd"), text);
        Assert.Equal(1, ConsoleControl.Poll(dir));
        return JsonNode.Parse(File.ReadAllText(Path.Combine(dir, name + ".result.json")))!;
    }

    [Fact]
    public void Console_folder_acts_as_the_local_player_on_a_hosting_game()
    {
        RegisterPlayerOnly();
        Commands.Register("whoami", c => c.Reply($"{c.Source} host={c.Source.IsHostConsole}"));
        Commands.Register("secret", c => c.Reply("42"), CommandPermission.Console);
        ConsoleControl.LocalHost = () => Alder.Entity;  // a hosting game: its local player is Alder, in the world
        var doc = RunFile("hosted", """
            whoami
            here
            secret
            #as console
            here
            """);
        var results = doc["results"]!.AsArray();
        Assert.Equal("console as Alder (entity 1) host=True", results[0]!["output"]![0]!.GetValue<string>());
        Assert.True(results[1]!["ok"]!.GetValue<bool>());
        Assert.Equal("Alder at 100", results[1]!["output"]![0]!.GetValue<string>());
        Assert.True(results[2]!["ok"]!.GetValue<bool>());  // the console's rights: a console-only command runs
        Assert.Equal("console as Alder (entity 1)", results[2]!["source"]!.GetValue<string>());
        Assert.False(results[3]!["ok"]!.GetValue<bool>());  // #as console: the plain console again, which has no player
        Assert.Equal("console console", results[3]!["source"]!.GetValue<string>());
        Assert.Equal("only a player in the world can do this", results[3]!["error"]!.GetValue<string>());
        Assert.True(Backend.Logged("Info: command /here by console as Alder (entity 1): ok"));
        Assert.True(Backend.Logged("Warn: command /here by console console: only a player in the world can do this"));
    }

    [Fact]
    public void Typed_commands_see_the_hosts_player_through_selectors()
    {
        var target = new AttributeCommands();
        As(NewMod("attr"), () => Commands.RegisterAll(target));
        ConsoleControl.LocalHost = () => Alder.Entity;
        var doc = RunFile("selectors", "all @s\nheal @s 5\n");
        var results = doc["results"]!.AsArray();
        Assert.Equal("Alder", results[0]!["output"]![0]!.GetValue<string>());
        Assert.Equal("healed Alder by 5", results[1]!["output"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Console_folder_stays_the_console_without_a_hosts_player_in_the_world()
    {
        RegisterPlayerOnly();
        // a dedicated server (no local player), and a hosting game whose player isn't in the world yet
        foreach (var host in new[] { EntityId.None, new EntityId(9) })
        {
            ConsoleControl.LocalHost = () => host;
            var doc = RunFile($"plain{host.Value}", "here\n");
            var r = doc["results"]![0]!;
            Assert.Equal("console console", r["source"]!.GetValue<string>());
            Assert.False(r["ok"]!.GetValue<bool>());
            Assert.False(doc["ok"]!.GetValue<bool>());
        }
        Assert.False(CommandSource.Console.IsHostConsole);
        Assert.True(Backend.Logged("Warn: command /here by console console: only a player in the world can do this"));
        Assert.False(Backend.Logged("command /here by console console: ok"));
    }

    [Fact]
    public void A_hosts_console_keeps_the_consoles_rights()
    {
        var host = CommandSource.HostConsole(Alan.Entity, "Alan");  // Alan is no admin as a player
        Assert.True(host.IsPlayer && host.IsHostConsole && host.IsAdmin);
        Assert.Equal(Alan.Entity, host.Player);
        Assert.Equal(PermissionSubjectKind.Console, host.Subject.Kind);
        Commands.Register("secret", c => c.Reply("42"), CommandPermission.Console);
        Assert.True(Commands.Execute(host, "secret").Ok);
        Assert.True(Commands.Execute(CommandSource.ForPlayer(Alan.Entity, "Alan"), "secret").Denied);
    }

    [Fact]
    public async Task The_log_line_says_what_an_async_command_ended_with()
    {
        var target = new AsyncCommands();
        Commands.RegisterAll(target);  // Kiln's own: a mod's continuations would wait for its scheduler, which the host runs

        var refused = Run("refuse");
        Assert.False(refused.Ok);
        Assert.Equal("only a player in the world can do this", refused.Error);
        Assert.True(Backend.Logged("Warn: command /refuse by console console: only a player in the world can do this"));

        var slow = Run("slow");
        var slowFail = Run("slowfail");
        Assert.True(slow.Ok && slow.Completion is { IsCompleted: false });
        Assert.False(Backend.Logged("command /slow by console console"));  // nothing said before it ends
        target.Gate.SetResult();
        var done = await slow.Completion!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var failed = await slowFail.Completion!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(done.Ok);
        Assert.Equal(["done"], done.Output);
        Assert.True(Backend.Logged("Info: command /slow by console console: ok"));
        Assert.False(failed.Ok);
        Assert.Equal("refused later", failed.Error);
        Assert.True(Backend.Logged("Warn: command /slowfail by console console: refused later"));
        Assert.False(Backend.Logged("command /slowfail by console console: ok"));
    }

    [Fact]
    public async Task Console_folder_waits_for_commands_still_running()
    {
        var target = new AsyncCommands();
        Commands.RegisterAll(target);  // Kiln's own: a mod's continuations would wait for its scheduler, which the host runs
        var dir = Path.Combine(Root, "control", "console");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "w.cmd"), "slow\nslowfail\n");
        Assert.Equal(1, ConsoleControl.Poll(dir));
        var result = Path.Combine(dir, "w.result.json");
        Assert.False(File.Exists(result));  // still running: no result yet
        target.Gate.SetResult();
        for (var i = 0; i < 500 && !File.Exists(result); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);  // the commands' continuations run on the test's scheduler
            ConsoleControl.Poll(dir);
        }
        var doc = JsonNode.Parse(File.ReadAllText(result))!;
        var results = doc["results"]!.AsArray();
        Assert.Equal("done", results[0]!["output"]![0]!.GetValue<string>());
        Assert.True(results[0]!["ok"]!.GetValue<bool>());
        Assert.False(results[1]!["ok"]!.GetValue<bool>());
        Assert.Equal("refused later", results[1]!["error"]!.GetValue<string>());
        Assert.False(doc["ok"]!.GetValue<bool>());

        // one that doesn't end in time is written anyway, marked running
        var stuck = new AsyncCommands();
        Commands.ResetForTests();
        Commands.RegisterAll(stuck);
        ConsoleControl.WaitForRunning = TimeSpan.Zero;
        File.WriteAllText(Path.Combine(dir, "s.cmd"), "slow\n");
        ConsoleControl.Poll(dir);
        var late = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "s.result.json")))!;
        Assert.True(late["results"]![0]!["running"]!.GetValue<bool>());
    }

    [Fact]
    public void Tokenizer_is_unchanged()
    {
        Assert.Equal(["a b", "say \"hi\"", "c\\d"], Commands.Tokenize(@"a\ b ""say \""hi\"""" c\\d"));
        Assert.Equal(["abc de"], Commands.Tokenize("ab'c d'e"));
        Assert.True(Commands.IsCommandLine("/help") && !Commands.IsCommandLine("//x") && !Commands.IsCommandLine("/ x"));
    }
}
