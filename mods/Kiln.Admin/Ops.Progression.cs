// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Kiln.Admin;

/// <summary>Character progression ops over kiln.progression (Kiln.Players' <see cref="Progression"/>;
/// docs/kiln/services/progression.md "Kiln.Admin"), and the admin chat commands /level, /xp, /skillpoints, /skill.
/// Changes run through the game's own paths and answer two steps later with what the game made of them.</summary>
internal static partial class Ops
{
    internal static bool RegisterProgressionSafe(Bridge bridge, Logger log)
    {
        try
        {
            return RegisterProgression(bridge, log);
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException or MissingMethodException)
        {
            log.Info($"progression ops off: {e.GetType().Name}");
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool RegisterProgression(Bridge bridge, Logger log)
    {
        if (!Progression.Available)
        {
            foreach (var op in new[] { "players_progression", "players_progression_get", "players_progression_set", "give_xp",
                                       "give_skill_points", "skill_unlock", "skill_lock", "skill_respec", "skill_nodes", "skill_tree" })
                bridge.Register(op, _ => throw new AdminError("character progression (kiln.progression) is unavailable on this server build"));
            return false;
        }
        bridge.Register("progression_status", _ => ProgStatusJson());
        // {entity?}: one player's progression, or every player in game
        JsonNode? Get(JsonObject req)
        {
            if (req["entity"] is not null) return ProgJson(RequirePlayer(req));
            var list = new JsonArray();
            foreach (var p in Players.All)
                if (p.InGame) list.Add(ProgJson(p));
            return list;
        }
        bridge.Register("players_progression", Get);
        bridge.Register("players_progression_get", Get);
        // {entity, level?, experience?, skillPoints? (give), respec?}: in that order
        bridge.RegisterAsync("players_progression_set", async req =>
        {
            var p = RequirePlayer(req);
            var steps = new JsonArray();
            var guard = await SteamGuardRelay.LatchFor(p.Entity, "admin bridge players_progression_set");
            await TickThread();  // the answer may come back off the tick thread: the writes below run on it
            if (req["level"] is not null) steps.Add(await Run(Progression.SetLevel(p.Entity, ProgInt(req, "level", 1, 1000))));
            if (req["experience"] is not null)
                steps.Add(await Run(Progression.SetExperience(p.Entity, (uint)ProgInt(req, "experience", 0, 100_000_000))));
            if (req["skillPoints"] is not null) steps.Add(await Run(Progression.GiveSkillPoints(p.Entity, ProgInt(req, "skillPoints", 1, 1000))));
            if (req["respec"] is JsonValue rv && rv.TryGetValue<bool>(out var respec) && respec) steps.Add(await Run(Progression.Respec(p.Entity)));
            if (steps.Count == 0) throw new AdminError("give level, experience, skillPoints or respec");
            return Guarded(new JsonObject { ["steps"] = steps, ["progression"] = ProgJson(p) }, guard);
        });
        // {entity, amount, source?}
        bridge.RegisterAsync("give_xp", async req =>
        {
            var p = RequirePlayer(req);
            var source = ProgStr(req, "source") is { } s
                ? Enum.TryParse<ExperienceSource>(s, true, out var src) ? src : throw new AdminError($"unknown experience source '{s}'")
                : ExperienceSource.None;
            var guard = await SteamGuardRelay.LatchFor(p.Entity, "admin bridge give_xp");
            await TickThread();  // the answer may come back off the tick thread: the writes below run on it
            return Guarded(await Run(Progression.AddExperience(p.Entity, (uint)ProgInt(req, "amount", 1, 100_000_000), source)), guard);
        });
        bridge.RegisterAsync("give_skill_points", async req =>
        {
            var p = RequirePlayer(req);
            var guard = await SteamGuardRelay.LatchFor(p.Entity, "admin bridge give_skill_points");
            await TickThread();  // the answer may come back off the tick thread: the writes below run on it
            return Guarded(await Run(Progression.GiveSkillPoints(p.Entity, ProgInt(req, "amount", 1, 1000))), guard);
        });
        // {entity, node ("0x…" or a number), force?}
        bridge.RegisterAsync("skill_unlock", async req =>
        {
            var p = RequirePlayer(req);
            var guard = await SteamGuardRelay.LatchFor(p.Entity, "admin bridge skill_unlock");
            await TickThread();  // the answer may come back off the tick thread: the writes below run on it
            return Guarded(await Run(Progression.Unlock(p.Entity, NodeOf(req), ProgBool(req, "force"))), guard);
        });
        bridge.RegisterAsync("skill_lock", async req =>
        {
            var p = RequirePlayer(req);
            var guard = await SteamGuardRelay.LatchFor(p.Entity, "admin bridge skill_lock");
            await TickThread();  // the answer may come back off the tick thread: the writes below run on it
            return Guarded(await Run(Progression.Lock(p.Entity, NodeOf(req), ProgBool(req, "force"))), guard);
        });
        bridge.RegisterAsync("skill_respec", async req =>
        {
            var p = RequirePlayer(req);
            var guard = await SteamGuardRelay.LatchFor(p.Entity, "admin bridge skill_respec");
            await TickThread();  // the answer may come back off the tick thread: the writes below run on it
            return Guarded(await Run(Progression.Respec(p.Entity)), guard);
        });
        bridge.Register("skill_nodes", req =>
        {
            var p = RequirePlayer(req);
            return new JsonArray([.. Progression.UnlockedNodes(p.Entity).Select(n => (JsonNode)new JsonObject
            {
                ["id"] = $"0x{n.Id:x8}", ["name"] = NodeName(n.Id), ["level"] = n.Level, ["maxLevel"] = n.MaxLevel, ["cost"] = n.Cost,
            })]);
        });
        bridge.Register("skill_tree", _ => new JsonArray([.. Progression.Tree.Select(n => (JsonNode)new JsonObject
        {
            ["id"] = $"0x{n.Id:x8}", ["name"] = NodeName(n.Id), ["type"] = n.Type.ToString(), ["class"] = ClassName(n.ClassAlignment),
            ["cost"] = n.Cost, ["maxLevel"] = n.MaxLevel, ["x"] = n.UiX, ["y"] = n.UiY,
        })]));
        var commands = Commands.RegisterAll(new ProgressionCommands());
        log.Info($"progression ops on ({Progression.Status}); chat commands: {string.Join(", ", commands.Select(c => "/" + c.Info.Name))}");
        return true;
    }

    // The bridge's answer carries the achievement guard's outcome for the affected player(s): "Protected",
    // "UnprotectedFailed" or "UnprotectedVanilla", plus a plain note when not protected.
    internal static JsonNode? Guarded(JsonNode? result, GuardOutcome guard)
    {
        if (result is JsonObject o)
        {
            o["steamGuard"] = guard.ToString();
            if (SteamGuardRelay.Note(guard) is { } note) o["steamGuardNote"] = note;
        }
        return result;
    }

    private static async Task<JsonNode?> Run(Task<ProgressionResult> task)
    {
        var r = await task;
        if (!r.Ok && r.Status is "not_found" && r.Request == 0) throw new AdminError("That player is no longer in the world.");
        return ResultJson(r);
    }

    internal static JsonObject ResultJson(ProgressionResult r) => new()
    {
        ["ok"] = r.Ok, ["op"] = r.Op.ToString(), ["status"] = r.Status, ["applied"] = r.Applied, ["steps"] = r.Steps,
        ["reason"] = r.Reason, ["before"] = r.Before is { } b ? SnapJson(b) : null, ["after"] = r.After is { } a ? SnapJson(a) : null,
    };

    internal static JsonObject ProgJson(Player p)
    {
        var o = new JsonObject { ["entity"] = p.Entity.Value, ["name"] = p.Name };
        if (Progression.Get(p.Entity) is { } s) o["progression"] = SnapJson(s);
        return o;
    }

    internal static JsonObject SnapJson(ProgressionSnapshot s) => new()
    {
        ["level"] = s.Level, ["levelCap"] = s.LevelCap, ["experience"] = s.Experience, ["experienceNeeded"] = s.ExperienceNeeded,
        ["experiencePending"] = s.ExperiencePending, ["skillPoints"] = s.SkillPoints, ["skillPointsTotal"] = s.SkillPointsTotal,
        ["skillPointsSpent"] = s.SkillPointsSpent, ["skillPointsFromLevel"] = s.SkillPointsFromLevel,
        ["skillPointsFromKnowledge"] = s.SkillPointsFromKnowledge, ["givableSkillPoints"] = s.GivableSkillPoints,
        ["unlockedNodes"] = s.UnlockedNodes, ["atLevelCap"] = s.AtLevelCap, ["overBudget"] = s.OverBudget,
        ["hasLevel"] = s.HasLevel, ["hasSkills"] = s.HasSkills, ["hasPoints"] = s.HasPoints,
    };

    private static JsonObject ProgStatusJson()
    {
        var s = Progression.Status;
        if (s is null) return new JsonObject { ["available"] = false };
        return new JsonObject
        {
            ["available"] = true, ["levels"] = s.Levels, ["skills"] = s.Skills, ["skillKnowledge"] = s.SkillKnowledge,
            ["experienceEvents"] = s.ExperienceEvents, ["program"] = s.Program, ["treeNodes"] = s.TreeNodes,
            ["givablePointsMax"] = s.KnowledgePlusOne, ["levelCap"] = s.LevelCap, ["skillPointsPerLevel"] = s.SkillPointsPerLevel,
            ["pending"] = s.Pending, ["applied"] = s.Applied, ["failed"] = s.Failed, ["reason"] = s.Reason,
        };
    }

    private static uint NodeOf(JsonObject req)
    {
        var text = req["node"] switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonValue v when v.TryGetValue<double>(out var d) && d >= 0 && d <= uint.MaxValue => ((uint)d).ToString(),
            _ => throw new AdminError("node must be a skill node id (\"0x…\")"),
        };
        return Progression.ParseNode(text)?.Id ?? throw new AdminError($"no skill node {text} in the skill tree");
    }

    /// <summary>A readable name: the ability item an active node unlocks, else type and class.</summary>
    internal static string NodeName(uint id)
    {
        if (Progression.FindNode(id) is not { } n) return $"0x{id:x8}";
        if (n.ActiveSkillItem != 0)
        {
            try
            {
                if (Items.Available && Items.Get(new ItemId(n.ActiveSkillItem)) is { } it) return it.Label;
            }
            catch (Exception e) when (e is NotSupportedException or InvalidOperationException) { }
        }
        return n.Type == SkillNodeType.Root ? $"{ClassName(n.ClassAlignment)} root" : $"{ClassName(n.ClassAlignment)} {n.Type} 0x{id:x8}";
    }

    private static string ClassName(int c) => c switch { 0 => "Warrior", 1 => "Mage", 2 => "Ranger", 3 => "Neutral", _ => $"class {c}" };

    private static int ProgInt(JsonObject req, string key, int min, int max) =>
        req[key] is JsonValue v && v.TryGetValue<double>(out var d) && d == Math.Floor(d) && d >= min && d <= max
            ? (int)d : throw new AdminError($"{key} must be a whole number {min}..{max}");

    private static string? ProgStr(JsonObject req, string key) => req[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool ProgBool(JsonObject req, string key) => req[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}

/// <summary>Admin chat commands for character progression (permission level Admin; nodes kiln-admin.command.*).</summary>
internal sealed class ProgressionCommands
{
    private static string Line(Player p) => Progression.Get(p.Entity) is { } s ? $"{p.Name}: {s}" : $"{p.Name}: no progression data";

    [Command("level", Description = "shows or sets a player's level", Permission = CommandPermission.Admin)]
    private async Task Level(CommandContext c, Player player, [Range(1, 1000)] int level = 0)
    {
        if (level == 0)
        {
            c.Reply(Line(player));
            return;
        }
        c.Reply($"{player.Name}: setting level {level}...");
        var guard = await SteamGuardRelay.LatchFor(player.Entity, "admin level");
        await Ops.TickThread();  // the answer may come back off the tick thread: the writes below run on it
        var r = await Progression.SetLevel(player.Entity, level);
        var note = SteamGuardRelay.Note(guard);
        c.Reply(r.Ok ? $"{player.Name} is level {r.After?.Level}{(r.Reason.Length > 0 ? $" ({r.Reason})" : "")}{(note is null ? "" : $" — {note}")}" : $"{player.Name}: {r.Reason}");
    }

    [Command("xp", Description = "gives a player experience (the game levels them up)", Permission = CommandPermission.Admin)]
    private async Task Xp(CommandContext c, Player player, [Range(1, 100_000_000)] int amount)
    {
        c.Reply($"{player.Name}: granting {amount} experience...");
        var guard = await SteamGuardRelay.LatchFor(player.Entity, "admin xp");
        await Ops.TickThread();  // the answer may come back off the tick thread: the writes below run on it
        var r = await Progression.AddExperience(player.Entity, (uint)amount, ExperienceSource.Quest);
        var note = SteamGuardRelay.Note(guard);
        c.Reply(r.Ok ? $"gave {player.Name} {r.Applied} experience: {r.After}{(note is null ? "" : $" — {note}")}" : $"{player.Name}: {r.Reason}");
    }

    [Command("skillpoints", Description = "shows a player's skill points, or gives that many (as the game's skill-point knowledge)", Permission = CommandPermission.Admin)]
    private async Task SkillPoints(CommandContext c, Player player, [Range(0, 1000)] int amount = 0)
    {
        if (amount == 0)
        {
            c.Reply(Progression.Get(player.Entity) is { } s
                ? $"{player.Name}: {s.SkillPoints} unspent of {s.SkillPointsTotal} ({s.SkillPointsFromLevel} from levels, " +
                  $"{s.SkillPointsFromKnowledge} from knowledge; {s.GivableSkillPoints} more can be given)"
                : $"{player.Name}: no progression data");
            return;
        }
        c.Reply($"{player.Name}: giving {amount} skill point(s)...");
        var guard = await SteamGuardRelay.LatchFor(player.Entity, "admin skillpoints");
        await Ops.TickThread();  // the answer may come back off the tick thread: the writes below run on it
        var r = await Progression.GiveSkillPoints(player.Entity, amount);
        var note = SteamGuardRelay.Note(guard);
        c.Reply(r.Ok ? $"gave {player.Name} {r.Applied} skill point(s): {r.After?.SkillPoints} unspent{(r.Reason.Length > 0 ? $" ({r.Reason})" : "")}{(note is null ? "" : $" — {note}")}"
                     : $"{player.Name}: {r.Reason}");
    }

    [Command("skill", Description = "skill tree: /skill <player> list | unlock <node> [force] | lock <node> [force] | respec",
        Permission = CommandPermission.Admin)]
    private async Task Skill(CommandContext c, Player player, [Choice("list", "unlock", "lock", "respec")] string action, string node = "",
        [Choice("force")] string force = "")
    {
        var forced = force.Equals("force", StringComparison.OrdinalIgnoreCase);
        switch (action.ToLowerInvariant())
        {
            case "list":
                var nodes = Progression.UnlockedNodes(player.Entity);
                c.Reply($"{player.Name}: {nodes.Count} node(s): " +
                        string.Join(", ", nodes.Select(n => $"{Ops.NodeName(n.Id)}{(n.MaxLevel > 0 ? $" {n.Level}/{n.MaxLevel}" : "")}")));
                return;
            case "respec":
                c.Reply($"{player.Name}: respec...");
                var gs = await SteamGuardRelay.LatchFor(player.Entity, "admin skill respec");
                await Ops.TickThread();  // the answer may come back off the tick thread: the writes below run on it
                var rs = await Progression.Respec(player.Entity);
                var notes = SteamGuardRelay.Note(gs);
                c.Reply(rs.Ok ? $"{player.Name}: {rs.Applied} node(s) reset, {rs.After?.SkillPoints} unspent points{(notes is null ? "" : $" — {notes}")}" : $"{player.Name}: {rs.Reason}");
                return;
            case "unlock":
            case "lock":
                var n2 = Progression.ParseNode(node) ?? throw new CommandException($"no skill node '{node}' (give its id, e.g. 0xffd5f940)");
                c.Reply($"{player.Name}: {action} {Ops.NodeName(n2.Id)}...");
                var g = await SteamGuardRelay.LatchFor(player.Entity, $"admin skill {action}");
                await Ops.TickThread();  // the answer may come back off the tick thread: the writes below run on it
                var r = action.Equals("unlock", StringComparison.OrdinalIgnoreCase)
                    ? await Progression.Unlock(player.Entity, n2.Id, forced)
                    : await Progression.Lock(player.Entity, n2.Id, forced);
                var noteu = SteamGuardRelay.Note(g);
                c.Reply(r.Ok ? $"{player.Name}: {action}ed {Ops.NodeName(n2.Id)}, {r.After?.SkillPoints} unspent points{(r.Reason.Length > 0 ? $" ({r.Reason})" : "")}{(noteu is null ? "" : $" — {noteu}")}" : $"{player.Name}: {r.Reason}");
                return;
            default:
                throw new CommandException("action: list, unlock, lock or respec");
        }
    }
}
