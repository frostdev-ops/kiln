// SPDX-License-Identifier: MIT
// The /creature commands (admins). A mod registers them with CreatureAdminCommands.Register() (they belong to that mod
// and go away when it unloads); the EliteVukah sample does. Changes made by commands are layers of that mod.
using System.Globalization;
using System.Text;

namespace Kiln;

/// <summary>The <c>/creature</c> command tree (admins only).</summary>
public static class CreatureAdminCommands
{
    private static readonly string[] Fields =
        ["level", "faction", "scale", "xp", "speed", "damage", "home", "loot-labels", "loot-slots", "loot-off", "feature-on",
         "feature-off", "arsenal"];

    /// <summary>Registers <c>/creature</c> for the calling mod.</summary>
    public static CommandRegistration Register()
    {
        var root = Commands.Literal("creature").Describe("creatures: inspect, change, variants, AI, commands")
            .Executes(StatusCmd)
            .Then(Commands.Literal("status").Describe("what kiln.creatures can do here").Executes(StatusCmd))
            .Then(Commands.Literal("near").Describe("creatures near you (or x y z), radius in metres")
                .Executes(Near)
                .Then(Commands.Argument("radius", Arg.Double(1, 2000)).Executes(Near)
                    .Then(Commands.Argument("at", Arg.Position()).Executes(Near))))
            .Then(Commands.Literal("inspect").Describe("everything about one creature")
                .Then(Commands.Argument("entity", Arg.Entity()).Executes(Inspect)))
            .Then(Commands.Literal("set").Describe("changes a value: " + string.Join(", ", Fields) + ", resist:<kind>")
                .Then(Commands.Argument("entity", Arg.Entity())
                    .Then(Commands.Argument("field", Arg.Word())
                        .Then(Commands.Argument("value", Arg.Greedy()).Executes(Set)))))
            .Then(Commands.Literal("ai").Describe("reads or sets a private AI setting (path, e.g. aggroSetup.viewRange)")
                .Then(Commands.Argument("entity", Arg.Entity())
                    .Then(Commands.Argument("path", Arg.Word()).Executes(AiGet)
                        .Then(Commands.Argument("value", Arg.Greedy()).Executes(AiSet)))))
            .Then(Commands.Literal("reset").Describe("drops what /creature changed on a creature")
                .Then(Commands.Argument("entity", Arg.Entity()).Executes(Reset)))
            .Then(Commands.Literal("spawn").Describe("spawns a variant or template at you (or x y z)")
                .Then(Commands.Argument("what", Arg.Word()).Executes(SpawnCmd)
                    .Then(Commands.Argument("at", Arg.Position()).Executes(SpawnCmd))))
            .Then(Commands.Literal("variants").Describe("registered variants and spawn rules").Executes(Variants))
            .Then(Commands.Literal("catalog").Describe("creature templates matching a filter")
                .Then(Commands.Argument("filter", Arg.Word()).Executes(Catalog)))
            .Then(Commands.Literal("cmd").Describe("attack <target> | guard [target] [eventonly|nocatchup|nodefend] | stopguard <target> | stay | heal | moveto x y z")
                .Then(Commands.Argument("entity", Arg.Entity())
                    .Then(Commands.Argument("action", Arg.Choice("attack", "guard", "stopguard", "stay", "heal", "moveto"))
                        .Executes(Cmd)
                        .Then(Commands.Argument("rest", Arg.Greedy()).Executes(Cmd)))))
            .Then(Commands.Literal("watch").Describe("logs a creature's AI state, path walker and guard order every 0.4 s (seconds, 0 = stop)")
                .Then(Commands.Argument("entity", Arg.Entity()).Executes(WatchCmd)
                    .Then(Commands.Argument("seconds", Arg.Double(0, 3600)).Executes(WatchCmd))))
            .Then(Commands.Literal("ready").Describe("lab only: holds a creature ready for gameplay (on/off)")
                .Then(Commands.Argument("entity", Arg.Entity())
                    .Then(Commands.Argument("on", Arg.Bool()).Executes(Ready))))
            .Then(Commands.Literal("rules").Describe("rules and arsenals: what resolved, what ran; with an entity, its arsenal")
                .Executes(RulesCmd)
                .Then(Commands.Argument("entity", Arg.Entity()).Executes(RulesCmd)))
            .Then(Commands.Literal("attack").Describe("overrides a creature's attack: force <index> | allow <i,j,...> | natural | clear")
                .Then(Commands.Argument("entity", Arg.Entity())
                    .Then(Commands.Argument("how", Arg.Choice("force", "allow", "natural", "clear")).Executes(AttackCmd)
                        .Then(Commands.Argument("which", Arg.Word()).Executes(AttackCmd)))))
            .Then(Commands.Literal("aiswitch").Describe("turns a creature's AI off or on: the body keeps running, decisions stop")
                .Then(Commands.Argument("entity", Arg.Entity())
                    .Then(Commands.Argument("on", Arg.Bool()).Executes(AiSwitchCmd))))
            .Then(Commands.Literal("target").Describe("makes another entity the creature's combat target")
                .Then(Commands.Argument("entity", Arg.Entity())
                    .Then(Commands.Argument("target", Arg.Entity()).Executes(TargetCmd))));
        return Commands.Register(root, CommandPermission.Admin, "Kiln creatures (kiln.creatures)");
    }

    private static void Require()
    {
        if (!Creatures.Available) throw new CommandException("kiln.creatures is not available on this server");
    }

    private static WorldPosition Here(CommandContext c)
    {
        if (c.TryGet<WorldPosition>("at", out var at)) return at;
        if (c.Source.IsPlayer && World.TryGetPosition(c.Source.Player, out var p)) return p;
        throw new CommandException("give a position (x y z): the console has none");
    }

    private static void StatusCmd(CommandContext c)
    {
        var s = Creatures.Status ?? throw new CommandException("kiln.creatures is not available on this server");
        c.Reply($"kiln.creatures: slots {On(s.Slots)}, AI hooks {On(s.AiHooks)}, reflection {On(s.Reflection)}, commands {On(s.Commands)}, " +
                $"marks {On(s.Marks)}, arsenals {s.ArsenalCount}; catalog {s.Catalog} ({s.CatalogLoaded} loaded); " +
                $"{s.Entities} creatures changed ({s.Layers} layers, {s.PrivateCopies} private copies); " +
                $"{CreatureVariants.All.Count} variants, {CreatureVariants.SpawnRules.Count} spawn rules");
        static string On(bool b) => b ? "on" : "off";
    }

    private static void RulesCmd(CommandContext c)
    {
        Require();
        var s = Arsenals.Status ?? throw new CommandException("kiln.creatures is not available on this server");
        if (c.Has("entity"))
        {
            var cr = Creatures.Of(c.Get<EntityId>("entity"));
            var a = Arsenals.Info(cr.Arsenal) ?? throw new CommandException($"{cr.Entity}: its arsenal {cr.Arsenal} is not in the registry");
            var text = new StringBuilder($"{cr.Entity}: arsenal {a.Id} ({(a.IsKiln ? "Kiln's" : "the game's")}), AI {(cr.AiEnabled ? "on" : "off")}, phase {cr.Phase:x8}");
            foreach (var x in a.Attacks) text.Append($"\n  attack {x.Index}: sequence {x.Sequence} ({RuleIds.Fnv(x.Sequence):x8}), style 0x{x.Style:x}");
            foreach (var r in a.Rules) text.Append($"\n  rule {r.Index}: mode {(RuleMode)r.Mode}, {r.When} condition(s), {r.Actions} action(s)");
            foreach (var p in a.Problems) text.Append($"\n  problem: {p}");
            c.Reply(text.ToString());
            return;
        }
        c.Reply($"rules: writer {On(s.Writer)}, dispatcher {On(s.Dispatcher)} (hooks {On(s.RuleHooks)}), attack overrides {On(s.Attacks)} (hooks {On(s.AttackHooks)}), AI switch {On(s.AiSwitch)}; " +
                $"{s.Arsenals} Kiln arsenals, map {s.MapCount}/{s.MapCapacity}; table {s.Tracked} creatures, {s.Overrides} overrides, {s.AiOff} AI off\n" +
                $"  constraints {s.Constraints} ({s.ConstraintsTrue} true), actions {s.Actions} ({s.ActionsDropped} dropped); selects {s.Selects} (forced {s.SelectsForced}, refused {s.SelectsFiltered}, waited {s.PicksWaiting}); " +
                $"starts {s.Starts} (against an override {s.StartsOffOverride}), follow-ups refused {s.FollowupsRefused}, sequences refused {s.SequencesRefused}");
        static string On(bool b) => b ? "on" : "off";
    }

    private static void AttackCmd(CommandContext c)
    {
        Require();
        var e = c.Get<EntityId>("entity");
        var how = c.Get<string>("how");
        var which = c.GetOrDefault("which", "");
        var a = Creatures.Of(e).Attacks;
        switch (how)
        {
            case "force":
                if (!int.TryParse(which, CultureInfo.InvariantCulture, out var i)) throw new CommandException("force <attack index>; /creature rules <entity> lists them");
                a.Force(i);
                break;
            case "allow":
                try { a.Allow(which.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray()); }
                catch (FormatException) { throw new CommandException("allow <i,j,...>"); }
                break;
            case "natural":
                a.Natural();
                break;
            default:
                a.Clear();
                break;
        }
        c.Reply($"{e}: attack override {how} {which}");
    }

    private static void AiSwitchCmd(CommandContext c)
    {
        Require();
        var e = c.Get<EntityId>("entity");
        var cr = Creatures.Of(e);
        cr.AiEnabled = c.Get<bool>("on");
        c.Reply($"{e}: AI {(cr.AiEnabled ? "on" : "off")}, state {cr.State}");
    }

    private static void TargetCmd(CommandContext c)
    {
        Require();
        var e = c.Get<EntityId>("entity");
        var t = c.Get<EntityId>("target");
        Creatures.Of(e).PreferTarget(t, 10);
        c.Reply($"{e}: holds {t} as its target for 10 s");
    }

    private static void Near(CommandContext c)
    {
        Require();
        var at = Here(c);
        var r = c.GetOrDefault("radius", 40.0);
        var list = Creatures.Near(at, r);
        if (list.Count == 0)
        {
            c.Reply($"no creatures within {r:0} m of {at}");
            return;
        }
        var sb = new StringBuilder($"{list.Count} creature(s) within {r:0} m of {at}:");
        foreach (var e in list.Take(12))
        {
            var i = Creatures.Of(e).Inspect();
            if (i is null) continue;
            var d = World.TryGetPosition(e, out var p) ? p.DistanceTo(at) : 0;
            sb.Append($"\n  {e} {i.TemplateName} lvl {i.Level} {i.Faction} {(i.HasAi ? i.State.ToString() : "")} {d:0} m");
        }
        c.Reply(sb.ToString());
    }

    private static void Inspect(CommandContext c)
    {
        Require();
        var e = c.Get<EntityId>("entity");
        var i = Creatures.Of(e).Inspect() ?? throw new CommandException($"no entity {e}");
        var v = CreatureVariants.Of(e);
        c.Reply($"{e} {i.TemplateName} ({i.Kind}){(v is null ? "" : $" variant {v.Id}")}: level {i.Level}/{i.LevelMax}, " +
                $"health {i.Health}/{i.HealthMax}, faction {i.Faction}, scale {i.Scale:0.##}, xp x{i.XpFactor:0.##}, " +
                $"speed x{i.SpeedFactor:0.##}, damage x{i.DamageMultiplier:0.##}" +
                (i.HasAi ? $"\n  AI {i.State}, awareness {i.AwarenessState} {i.Awareness:0.##}, target {i.Target}, guard {i.Guard}, " +
                           $"home {i.Home}, features +{i.EnabledFeatures} -{i.DisabledFeatures}" : "") +
                $"\n  loot {i.LootLabels} slots {i.LootSlotsMin}-{i.LootSlotsMax}{(i.LootDisabled ? " (off)" : "")}; " +
                $"{i.Layers} layer(s), {i.PrivateCopies} private copy(ies){(i.Dead ? ", dead" : "")}{(i.Ready ? ", ready" : "")}" +
                $"{(i.ForcedReady ? " (forced)" : "")}");
    }

    private static void Set(CommandContext c)
    {
        Require();
        var e = c.Get<EntityId>("entity");
        var field = c.Get<string>("field").ToLowerInvariant();
        var value = c.Get<string>("value").Trim();
        var cr = Creatures.Of(e);
        try
        {
            switch (field)
            {
                case "level": cr.Level = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "faction":
                    cr.Faction = Enum.TryParse<Faction>(value, true, out var f) ? f : throw new CommandException($"unknown faction '{value}'");
                    break;
                case "scale": cr.Scale = F(value); break;
                case "xp": cr.XpFactor = F(value); break;
                case "speed": cr.SpeedFactor = F(value); break;
                case "damage": cr.DamageFactor = F(value); break;
                case "home": cr.Home = Pos(value); break;
                case "loot-labels": cr.Loot.Labels = Guid.Parse(value); break;
                case "loot-slots":
                {
                    var p = value.Split(' ', ',', '-');
                    cr.Loot.Slots = (int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[^1], CultureInfo.InvariantCulture));
                    break;
                }
                case "loot-off": cr.Loot.Disabled = value is "1" or "true" or "on" or "yes"; break;
                case "feature-on": cr.Features.Enable(Features(value)); break;
                case "feature-off": cr.Features.Disable(Features(value)); break;
                case "arsenal": cr.Arsenal = Guid.Parse(value); break;
                default:
                    if (field.StartsWith("resist:") && Enum.TryParse<DamageKind>(field[7..], true, out var k))
                    {
                        cr.Resistance[k] = F(value);
                        break;
                    }
                    throw new CommandException($"unknown field '{field}' ({string.Join(", ", Fields)}, resist:<kind>)");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or NotSupportedException or KeyNotFoundException)
        {
            throw new CommandException(ex.Message);
        }
        c.Reply($"{e}: {field} = {value}");
    }

    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    private static WorldPosition Pos(string s)
    {
        var p = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != 3) throw new CommandException("give x y z");
        return new WorldPosition(double.Parse(p[0], CultureInfo.InvariantCulture), double.Parse(p[1], CultureInfo.InvariantCulture),
            double.Parse(p[2], CultureInfo.InvariantCulture));
    }

    private static EnemyFeatures Features(string s)
    {
        EnemyFeatures r = 0;
        foreach (var n in s.Split([' ', ',', '|'], StringSplitOptions.RemoveEmptyEntries))
            r |= Enum.TryParse<EnemyFeatures>(n, true, out var f) && f != 0 ? f : throw new CommandException($"unknown feature '{n}'");
        return r;
    }

    private static void AiGet(CommandContext c)
    {
        Require();
        var e = c.Get<EntityId>("entity");
        var path = c.Get<string>("path");
        try
        {
            var v = Creatures.Of(e).Ai.Get(path, out var priv);
            c.Reply($"{e} {path} = {v}{(priv ? " (private copy)" : " (template)")}");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or KeyNotFoundException)
        {
            throw new CommandException(ex.Message);
        }
    }

    private static void AiSet(CommandContext c)
    {
        Require();
        var e = c.Get<EntityId>("entity");
        var path = c.Get<string>("path");
        var value = c.Get<string>("value").Trim();
        try
        {
            var ai = Creatures.Of(e).Ai;
            ai.Set(path, value);
            c.Reply($"{e} {path} = {ai.Get(path)} (private copy)");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or KeyNotFoundException)
        {
            throw new CommandException(ex.Message);
        }
    }

    private static void Reset(CommandContext c)
    {
        Require();
        var e = c.Get<EntityId>("entity");
        c.Reply($"{e}: {Creatures.Of(e).Reset()} change(s) dropped");
    }

    private static void SpawnCmd(CommandContext c)
    {
        Require();
        var what = c.Get<string>("what");
        var at = Here(c);
        var t = Creatures.Spawn(what, at, 0, new CreatureSpawnOptions { Tag = "admin", DespawnOnUnload = false });
        if (t.IsFaulted) throw new CommandException(t.Exception!.InnerException?.Message ?? "spawn failed");
        c.Reply($"spawned {what} as {t.Result} at {at}");
    }

    private static void Variants(CommandContext c)
    {
        var sb = new StringBuilder($"{CreatureVariants.All.Count} variant(s):");
        foreach (var v in CreatureVariants.All) sb.Append($"\n  {v.Id} = {v.Base}{(v.Level is { } l ? $" lvl {l}" : "")}{(v.Scale is { } s ? $" x{s}" : "")}{(v.Ai.Count > 0 ? $" ai {v.Ai.Count}" : "")}");
        foreach (var r in CreatureVariants.SpawnRules) sb.Append($"\n  rule {r.Template} -> {r.Variant} ({r.Chance:P0}{(r.NightOnly ? ", night" : r.DayOnly ? ", day" : "")})");
        c.Reply(sb.ToString());
    }

    private static void Catalog(CommandContext c)
    {
        var f = c.Get<string>("filter");
        var hits = Creatures.Templates.Where(t => t.Name.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                                                  t.DisplayName.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        var sb = new StringBuilder($"{hits.Count} template(s):");
        foreach (var t in hits.Take(15)) sb.Append($"\n  {t} {t.Kind} {t.Faction} lvl {t.Level}{(t.IsBoss ? " boss" : "")}");
        c.Reply(sb.ToString());
    }

    private static void Cmd(CommandContext c)
    {
        Require();
        var e = c.Get<EntityId>("entity");
        var action = c.Get<string>("action");
        var rest = c.GetOrDefault("rest", "").Trim();
        var cmd = Creatures.Of(e).Command;
        EntityId Target()
        {
            if (rest.Length == 0) return c.Source.IsPlayer ? c.Source.Player : throw new CommandException("give a target entity");
            return uint.TryParse(rest, out var id) ? new EntityId(id) : throw new CommandException($"bad target '{rest}'");
        }
        try
        {
            switch (action)
            {
                case "attack": cmd.Attack(Target()); break;
                case "guard":
                {
                    // guard [target] [eventonly] [nocatchup] [nodefend]
                    var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                    var o = GuardOptions.Default;
                    if (words.Remove("eventonly")) o = GuardOptions.EventOnly;
                    if (words.Remove("nocatchup")) o = o with { CatchUpDistance = 0, StuckSeconds = 0 };
                    if (words.Remove("nodefend")) o = o with { Defend = false, Assist = false };
                    rest = string.Join(' ', words);
                    cmd.Guard(Target(), o);
                    break;
                }
                case "stopguard": cmd.StopGuarding(Target()); break;
                case "stay": cmd.Stay(); break;
                case "heal": cmd.Heal(); break;
                case "moveto": cmd.MoveTo(rest.Length > 0 ? Pos(rest) : Here(c)); break;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or KeyNotFoundException)
        {
            throw new CommandException(ex.Message);
        }
        c.Reply($"{e}: {action} sent");
    }

    private static void WatchCmd(CommandContext c)
    {
        Require();
        var e = c.Get<EntityId>("entity");
        var secs = c.GetOrDefault("seconds", 60.0);
        Guarding.Watch(e, secs);
        c.Reply(secs > 0 ? $"{e}: watched for {secs:0} s (kiln.log: Kiln.Creatures: watch ...)" : $"{e}: watch stopped");
    }

    private static void Ready(CommandContext c)
    {
        Require();
        var e = c.Get<EntityId>("entity");
        try
        {
            Creatures.Of(e).ForceReady(c.Get<bool>("on"));
        }
        catch (NotSupportedException)
        {
            throw new CommandException("the readiness switch is lab-only (kiln/data/kiln.creatures/force-ready)");
        }
        c.Reply($"{e}: readiness {(c.Get<bool>("on") ? "forced" : "back to the game")}");
    }
}
