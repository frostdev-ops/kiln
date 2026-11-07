// SPDX-License-Identifier: MIT
// Creative mode: the "creative" rules preset for one player, for the session, and its /creative command. Both are
// Kiln's (this service loads on dedicated servers and in every game client), so the command is there wherever a world
// is hosted: a dedicated server, a game hosting a co-op world, or a world played offline.
namespace Kiln;

/// <summary>
/// Creative mode for one player: the <c>creative</c> preset (<see cref="Rules.Preset"/>: Kiln's own, or the server's
/// <c>&lt;configDir&gt;/rules/creative.json</c>) applied as a Kiln rule set for the session. It ends with
/// <see cref="Set"/> off, when the player leaves, or when the world closes. Tick thread.
/// </summary>
public static class Creative
{
    /// <summary>The preset's name.</summary>
    public const string PresetName = "creative";

    /// <summary>The command's permission node (level Admin; a hosted world's host and the console have it).</summary>
    public const string PermissionNode = "kiln.command.creative";

    private static readonly Dictionary<EntityId, RuleSet> Sets = new();

    /// <summary>Whether <paramref name="player"/> is in creative mode.</summary>
    public static bool IsOn(EntityId player) => Sets.TryGetValue(player, out var s) && s.Active;

    /// <summary>The players in creative mode.</summary>
    public static IReadOnlyList<EntityId> Players => Sets.Where(kv => kv.Value.Active).Select(kv => kv.Key).ToArray();

    /// <summary>Turns creative mode on or off for <paramref name="player"/>. On is refused (Ok false, with why) for a
    /// player whose game has no Kiln with the rules channel: flight and building need it. The player is told in chat.</summary>
    public static CreativeResult Set(EntityId player, bool on)
    {
        Game.AssertTickThread();
        var name = NameOf(player);
        if (!on)
        {
            if (!Sets.Remove(player, out var set) || !set.Active) return new(true, false, $"{name} is not in creative mode.");
            set.Dispose();
            CreativeServer.Push(player);
            Tell(player, "Creative mode is off: the game's own costs, vitals and flight rules are back. A flight outside a base ends and sets you down on the ground.");
            return new(true, false, $"Creative mode off for {name}.");
        }
        if (IsOn(player)) return new(true, true, $"{name} is already in creative mode.");
        var client = Rules.ClientOf(player);
        if (client is not (RulesClient.Kiln or RulesClient.Host or RulesClient.Simulated))
            return new(false, false, client switch
            {
                RulesClient.Pending => $"{name}'s game is still joining; try again in a moment.",
                RulesClient.KilnWithoutRules => $"{name}'s Kiln is too old for creative mode (it has no {Rules.ChannelName} channel): flight and building need it.",
                _ => $"{name}'s game has no Kiln: creative mode needs Kiln in the player's game for flight and building.",
            });
        PlayerRules rules;
        try
        {
            rules = Rules.Preset(PresetName) ?? PlayerRules.Creative;
        }
        catch (System.Text.Json.JsonException e)
        {
            return new(false, false, $"the creative preset is broken: {e.Message}");
        }
        // Kiln's rule set, whoever asked: it outlives the mod or tool that turned it on
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = null;
        try
        {
            Sets[player] = Rules.Apply(player, rules, label: PresetName);
        }
        finally
        {
            ModContext.CurrentMod = outer;
        }
        var now = Rules.Of(player);
        CreativeServer.Push(player);
        Tell(player, $"Creative mode is on: {now}. For throwaway or creative worlds: it grants no levels, XP, skill points or unlocks.");
        return new(true, true, $"Creative mode on for {name}: {now}" +
                               (now.Denied.Count > 0 ? $" (this server's rules.json denies {string.Join(", ", now.Denied)})" : ""));
    }

    /// <summary>One line on who is in creative mode and which hooks work on this build.</summary>
    public static string Status()
    {
        var names = Players.Select(NameOf).ToList();
        var s = Rules.Status();
        var hooks = s is null
            ? "kiln.rules is not loaded"
            : $"hooks: flight {On(s.Flight)}, building zone {On(s.BuildingZone)}, placement {On(s.Placement)}, costs {On(s.Costs)}, " +
              $"mining {On(s.Mining)}, Shroud timer {On(s.ShroudTimer)}, gliding {On(s.Gliding)}";
        return (names.Count == 0 ? "Nobody is in creative mode" : "Creative mode: " + string.Join(", ", names)) + "; " + hooks + ".";
        static string On(bool b) => b ? "on" : "off";
    }

    /// <summary>A world closed (a hosted session ends): its players' creative mode goes with it.</summary>
    internal static void Forget() => Sets.Clear();

    private static string NameOf(EntityId e)
    {
        try
        {
            return Kiln.Players.Find(e)?.Name is { Length: > 0 } n ? n : $"player {e}";
        }
        catch (Exception)
        {
            return $"player {e}";
        }
    }

    private static void Tell(EntityId player, string text)
    {
        try
        {
            Chat.Tell(player, text);
        }
        catch (Exception)
        {
            // no chat (an offline world): the command's reply says it
        }
    }

    // ---------------------------------------------------------------------------------------- /creative ------

    private static int _registered;

    /// <summary>Adds <c>/creative</c> (once; Kiln's command, node <see cref="PermissionNode"/>, level Admin).</summary>
    internal static void RegisterCommand()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0) return;
        Commands.Register(Commands.Literal("creative").Requires(CommandPermission.Admin, PermissionNode)
            .Describe("creative mode: fly anywhere, build anywhere for free, instant mining, no damage, nothing runs out")
            .Executes(c => c.Reply(Run(c, Self(c), null)))
            .Then(Commands.Literal("on").Executes(c => c.Reply(Run(c, Self(c), true)))
                .Then(Commands.Argument("player", Arg.Player()).Executes(c => c.Reply(Run(c, c.Get<Player>("player").Entity, true)))))
            .Then(Commands.Literal("off").Executes(c => c.Reply(Run(c, Self(c), false)))
                .Then(Commands.Argument("player", Arg.Player()).Executes(c => c.Reply(Run(c, c.Get<Player>("player").Entity, false)))))
            .Then(Commands.Literal("status").Executes(c => c.Reply(Status())))
            .Then(Commands.Literal("give").Describe("takes items from the catalogue for a player in creative mode")
                .Then(Commands.Argument("player", Arg.Player())
                    .Then(Commands.Argument("item", Arg.Item()).Executes(c => Give(c, 1, null))
                        .Then(Commands.Argument("count", Arg.Int(1, CreativeTools.MaxGive)).Executes(c => Give(c, c.Get<int>("count"), null))
                            .Then(Commands.Argument("level", Arg.Int(1, 100)).Executes(c => Give(c, c.Get<int>("count"), c.Get<int>("level"))))))))
            .Then(Commands.Literal("time").Describe("jumps the world's clock forward to a time of day")
                .Then(Commands.Argument("time", Arg.TimeOfDay()).Executes(c => c.Reply(Tools(c, CreativeTools.SetTime(c.Get<double>("time"), Who(c)))))))
            .Then(Commands.Literal("cycle").Describe("the day and night cycle: normal, stopped or fast")
                .Then(Commands.Argument("mode", Arg.Enum<TimeCycle>()).Executes(c => c.Reply(Tools(c, CreativeTools.SetCycle(c.Get<TimeCycle>("mode"), Who(c)))))))
            .Then(Commands.Argument("player", Arg.Player()).Executes(c => c.Reply(Run(c, c.Get<Player>("player").Entity, null)))),
            CommandPermission.Admin);
    }

    // A player runs it for themselves; on a game playing offline, the console acts as the host's character.
    private static EntityId Self(CommandContext c) =>
        c.Source.IsPlayer && !c.Source.Player.IsNone ? c.Source.Player : throw new CommandException("name a player: /creative [on|off] <player>");

    private static string Who(CommandContext c) => c.Source.IsPlayer && !c.Source.Player.IsNone ? NameOf(c.Source.Player) : "the console";

    private static string Tools(CommandContext c, CreativeResult r)
    {
        if (!r.Ok) c.Fail(r.Message);
        return r.Message;
    }

    // Items.Give finishes on a later step: the command says it asked, the player and the log get the outcome.
    private static void Give(CommandContext c, int count, int? level)
    {
        var player = c.Get<Player>("player").Entity;
        var item = c.Get<ItemInfo>("item");
        if (!Creative.IsOn(player)) c.Fail($"{NameOf(player)} is not in creative mode (/creative on {NameOf(player)}).");
        _ = GiveAndTell(player, item.Id, count, level);
        c.Reply($"Giving {count} x {item.Label} to {NameOf(player)}...");
    }

    private static async Task GiveAndTell(EntityId player, ItemId item, int count, int? level)
    {
        CreativeResult r;
        try
        {
            r = await CreativeTools.Give(player, item, count, level);
        }
        catch (Exception e)
        {
            r = new CreativeResult(false, IsOn(player), e.Message);
        }
        Tell(player, r.Message);
    }

    private static string Run(CommandContext c, EntityId player, bool? on)
    {
        var r = Set(player, on ?? !IsOn(player));
        if (!r.Ok) c.Fail(r.Message);
        return r.Message;
    }
}

/// <summary>What <see cref="Creative.Set"/> did.</summary>
/// <param name="Ok">False when it was refused (Message says why).</param>
/// <param name="On">Whether the player is in creative mode now.</param>
/// <param name="Message">One line for the one who asked.</param>
public sealed record CreativeResult(bool Ok, bool On, string Message);
