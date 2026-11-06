// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln.Admin;

/// <summary>/creative and the rules ops (Kiln.Rules; docs/kiln/services/rules.md). Creative mode is the "creative" preset
/// (Kiln's, or the server's <c>&lt;configDir&gt;/rules/creative.json</c>) applied to one player for the session: it ends
/// with /creative off, when the player leaves, or when Kiln.Admin unloads.</summary>
internal static partial class Ops
{
    private static readonly Dictionary<EntityId, RuleSet> CreativeSets = new();

    internal static bool RegisterRulesSafe(Bridge bridge, Logger log)
    {
        try
        {
            return RegisterRules(bridge, log);
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException or MissingMethodException)
        {
            log.Info($"creative and rules ops off: Kiln.Rules is not available ({e.GetType().Name})");
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool RegisterRules(Bridge bridge, Logger log)
    {
        // {entity, on?} -> creative mode on/off for a player (on omitted: toggles)
        bridge.Register("creative", req =>
        {
            var p = RequirePlayer(req);
            var on = req["on"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : !IsCreative(p.Entity);
            var (ok, message) = SetCreative(p, on);
            if (!ok) throw new AdminError(message);
            return CreativeJson(p, message);
        });
        // {entity?} -> the rules hooks, active rule sets and (with entity) one player's rules
        bridge.Register("rules", req =>
        {
            var s = Rules.Status();
            var o = new JsonObject
            {
                ["available"] = s is not null,
                ["hooks"] = s is null ? null : new JsonObject { ["flight"] = s.Flight, ["placement"] = s.Placement, ["costs"] = s.Costs, ["mining"] = s.Mining },
                ["detail"] = s?.Detail,
                ["presets"] = new JsonArray([.. Rules.Presets.Select(n => (JsonNode)n)]),
                ["sets"] = new JsonArray([.. Rules.Active.Select(r => (JsonNode)new JsonObject
                {
                    ["id"] = r.Id, ["owner"] = r.Owner, ["label"] = r.Label, ["scope"] = r.Scope.ToString(),
                    ["expires"] = r.Expires?.ToString("o"), ["rules"] = JsonNode.Parse(r.Rules.ToJson()),
                })]),
                ["creative"] = new JsonArray([.. CreativeSets.Where(kv => kv.Value.Active).Select(kv => (JsonNode)kv.Key.Value)]),
            };
            if (req["entity"] is JsonValue) o["player"] = EffectiveJson(Rules.Of(RequirePlayer(req).Entity));
            return o;
        });

        Commands.Register(Commands.Literal("creative").Requires(CommandPermission.Admin, "kiln.command.creative")
            .Describe("creative mode: fly anywhere, build anywhere for free, instant mining, no damage, nothing runs out")
            .Executes(c => c.Reply(Command(c, Self(c), null)))
            .Then(Commands.Literal("on").Executes(c => c.Reply(Command(c, Self(c), true)))
                .Then(Commands.Argument("player", Arg.Player()).Executes(c => c.Reply(Command(c, c.Get<Player>("player"), true)))))
            .Then(Commands.Literal("off").Executes(c => c.Reply(Command(c, Self(c), false)))
                .Then(Commands.Argument("player", Arg.Player()).Executes(c => c.Reply(Command(c, c.Get<Player>("player"), false)))))
            .Then(Commands.Literal("status").Executes(c => c.Reply(CreativeStatus())))
            .Then(Commands.Argument("player", Arg.Player()).Executes(c => c.Reply(Command(c, c.Get<Player>("player"), null)))),
            CommandPermission.Admin);
        log.Info("/creative and the creative, rules ops are on");
        return true;
    }

    private static Player Self(CommandContext c) =>
        c.Source.IsPlayer && Players.Find(c.Source.Player) is { } p ? p : throw new CommandException("name a player: /creative [on|off] <player>");

    private static bool IsCreative(EntityId e) => CreativeSets.TryGetValue(e, out var s) && s.Active;

    private static string Command(CommandContext c, Player p, bool? on)
    {
        var (ok, message) = SetCreative(p, on ?? !IsCreative(p.Entity));
        if (!ok) c.Fail(message);
        return message;
    }

    /// <summary>Turns creative mode on or off for one player; (false, why) when it can't.</summary>
    private static (bool Ok, string Message) SetCreative(Player p, bool on)
    {
        if (!on)
        {
            if (!CreativeSets.Remove(p.Entity, out var set) || !set.Active) return (true, $"{p.Name} is not in creative mode.");
            set.Dispose();
            Chat.Tell(p.Entity, "Creative mode is off: the game's own costs, vitals and flight rules are back. A flight outside a base ends and sets you down on the ground.");
            return (true, $"Creative mode off for {p.Name}.");
        }
        if (IsCreative(p.Entity)) return (true, $"{p.Name} is already in creative mode.");
        var client = Rules.ClientOf(p.Entity);
        if (client is not (RulesClient.Kiln or RulesClient.Host))
            return (false, client switch
            {
                RulesClient.Pending => $"{p.Name}'s game is still joining; try again in a moment.",
                RulesClient.KilnWithoutRules => $"{p.Name}'s Kiln is too old for creative mode (it has no {Rules.ChannelName} channel): flight and building need it.",
                _ => $"{p.Name}'s game has no Kiln: creative mode needs Kiln in the player's game for flight and building.",
            });
        PlayerRules rules;
        try
        {
            rules = Rules.Preset("creative") ?? PlayerRules.Creative;
        }
        catch (JsonException e)
        {
            return (false, $"the creative preset is broken: {e.Message}");
        }
        CreativeSets[p.Entity] = Rules.Apply(p.Entity, rules, label: "creative");
        var now = Rules.Of(p.Entity);
        Chat.Tell(p.Entity, $"Creative mode is on: {now}. For throwaway or creative worlds: it grants no levels, XP, skill points or unlocks.");
        return (true, $"Creative mode on for {p.Name}: {now}" + (now.Denied.Count > 0 ? $" (this server's rules.json denies {string.Join(", ", now.Denied)})" : ""));
    }

    private static string CreativeStatus()
    {
        var names = CreativeSets.Where(kv => kv.Value.Active).Select(kv => Players.Find(kv.Key)?.Name ?? kv.Key.ToString()).ToList();
        var s = Rules.Status();
        var hooks = s is null ? "kiln.rules is not loaded" : $"hooks: flight {On(s.Flight)}, placement {On(s.Placement)}, costs {On(s.Costs)}, mining {On(s.Mining)}";
        return (names.Count == 0 ? "Nobody is in creative mode" : "Creative mode: " + string.Join(", ", names)) + "; " + hooks + ".";
        static string On(bool b) => b ? "on" : "off";
    }

    private static JsonObject CreativeJson(Player p, string message) => new()
    {
        ["entity"] = p.Entity.Value, ["name"] = p.Name, ["creative"] = IsCreative(p.Entity), ["client"] = Rules.ClientOf(p.Entity).ToString(),
        ["message"] = message, ["rules"] = EffectiveJson(Rules.Of(p.Entity)),
    };

    private static JsonObject EffectiveJson(EffectiveRules e) => new()
    {
        ["summary"] = e.ToString(), ["flyAnywhere"] = e.FlyAnywhere, ["flightCarriesBody"] = e.FlightCarriesBody,
        ["buildOutsideBases"] = e.BuildOutsideBases, ["ignoreNoBuildZones"] = e.IgnoreNoBuildZones, ["ignoreEnemiesNearby"] = e.IgnoreEnemiesNearby,
        ["buildInShroud"] = e.BuildInShroud, ["ignoreBasePermissions"] = e.IgnoreBasePermissions, ["keepBuildsOutsideBases"] = e.KeepBuildsOutsideBases,
        ["buildCost"] = e.BuildCost, ["buildRefund"] = e.BuildRefund, ["digDamage"] = e.DigDamage, ["invulnerable"] = e.Invulnerable,
        ["minHealth"] = e.MinHealth, ["minStamina"] = e.MinStamina, ["minMana"] = e.MinMana, ["water"] = e.Water?.ToString(),
        ["denied"] = new JsonArray([.. e.Denied.Select(d => (JsonNode)d)]),
        ["from"] = new JsonArray([.. e.From.Select(r => (JsonNode)r.ToString())]),
    };
}
