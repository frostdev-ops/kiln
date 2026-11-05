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

    [Fact]
    public void Tokenizer_is_unchanged()
    {
        Assert.Equal(["a b", "say \"hi\"", "c\\d"], Commands.Tokenize(@"a\ b ""say \""hi\"""" c\\d"));
        Assert.Equal(["abc de"], Commands.Tokenize("ab'c d'e"));
        Assert.True(Commands.IsCommandLine("/help") && !Commands.IsCommandLine("//x") && !Commands.IsCommandLine("/ x"));
    }
}
