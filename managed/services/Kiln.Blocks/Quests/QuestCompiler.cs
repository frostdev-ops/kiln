// SPDX-License-Identifier: MIT
// Quest and dialogue blocks -> what Kiln.Quests takes, and the agreement between their parts, checked here so that a block that is wrong registers nothing:
//   QuestBlock     a QuestDefinition (Quests.Define): its steps, objectives, conditions and actions as the engine runs them; item names, NPCs and statuses of the
//                  mod's own blocks resolved; a reward's status as a code action. The game's journal builds from what Quests.Define exports (Kiln.Quests/Native).
//   DialogueBlock  a DialogueDefinition (Dialogues.Define), native: the game's NPC menu
// An id of the engine's (quest, step, dialogue) is the block's name and the step's Id: players' progress is filed under it.
// docs/kiln/modding/quests.md "Quests as blocks".
using System.Text.Json.Nodes;

namespace Kiln;

internal static partial class Compiler
{
    /// <summary>A quest or a dialogue, or null for another kind of block.</summary>
    internal static BlockHandle? DefineQuestBlock(BlockContext ctx, Lowered low, string name, IBlock block) => block switch
    {
        QuestBlock q => QuestCompiler.DefineQuest(ctx, low, name, q),
        DialogueBlock d => QuestCompiler.DefineDialogue(ctx, low, name, d),
        _ => null,
    };
}

internal static class QuestCompiler
{
    // Data-only blocks commit from the service tick, without a mod context or Retiring event. Their registry entries belong to this block lifetime.
    internal static void Forget(ModBlocks mod)
    {
        foreach (var handle in mod.Handles.Values)
            switch (handle)
            {
                case QuestHandle quest: Quests.ForgetBlock(quest.Definition); break;
                case DialogueHandle dialogue: Dialogues.ForgetBlock(dialogue.Definition); break;
            }
    }

    private static readonly string[] QuestStates = ["locked", "available", "active", "done", "failed", "started"];
    private static readonly string[] StepStates = ["pending", "active", "done"];
    // keen::QuestSourceType, as schema/kiln-quests.schema.json lists them (the boot build refuses another)
    private static readonly string[] Sources =
    [
        "None", "Flame", "Blacksmith", "Alchemist", "Huntress", "Farmer", "Carpenter", "CryptKeeper", "Bard", "ChineseNewYearTrader", "Barber", "Fisher", "AncientResearcher",
        "Grassland", "Deepforest", "Steppes", "Desert", "ColdHeights", "Wetlands", "AncientLands",
    ];
    private static readonly string[] Hints = ["None", "Hint", "Attention", "MainQuest"];

    // ---- the blocks -------------------------------------------------------------------------------------------------

    public static BlockHandle DefineQuest(BlockContext ctx, Lowered low, string name, QuestBlock q)
    {
        var mod = ctx.Mod.Id;
        ctx.Need(ClientsLevel.Mod, null, "a quest is built into the game's journal by every player's game at start, from the mod");
        var def = Quest(ctx, name, q);
        low.Json["quest"] = Describe(def);
        var fromData = ctx.Mod.Compiling.Contains(name);   // a data file's block compiles under its name; one from code does not
        low.AfterCommit(() =>
        {
            def.ModId = mod;
            // one of a data file the game's start-up build reads from the mod's folder itself; one from code is written for the next start (Kiln.Quests/Native)
            NativeExport.DefinedInData('q', def.FullId, fromData);
            try { Quests.Define(def); }
            catch (ArgumentException e) { throw new BlockError(ctx.Block, null, e.Message); }
        });
        return new QuestHandle(mod, name, BlockIds.Derive(mod, "quest", name), $"{mod}:{name}", q, def);
    }

    public static BlockHandle DefineDialogue(BlockContext ctx, Lowered low, string name, DialogueBlock d)
    {
        var mod = ctx.Mod.Id;
        ctx.Need(ClientsLevel.Mod, null, "a dialogue is built into the game's NPC menu by every player's game at start, from the mod");
        var def = Dialogue(ctx, name, d);
        var fromData = ctx.Mod.Compiling.Contains(name);
        low.Json["dialogue"] = new JsonObject { ["id"] = def.Id, ["start"] = def.Start, ["nodes"] = new JsonArray(def.Nodes.Keys.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray()) };
        low.AfterCommit(() =>
        {
            def.ModId = mod;
            NativeExport.DefinedInData('d', def.FullId, fromData);
            try { Dialogues.Define(def); }
            catch (ArgumentException e) { throw new BlockError(ctx.Block, null, e.Message); }
        });
        return new DialogueHandle(mod, name, BlockIds.Derive(mod, "dialogue", name), $"{mod}:{name}", d, def);
    }

    // ---- quests -----------------------------------------------------------------------------------------------------

    internal static QuestDefinition Quest(BlockContext ctx, string name, QuestBlock q)
    {
        CheckId(ctx, name, null, "a quest's id (its block name)");
        if (q.Name is null) throw new BlockError(ctx.Block, "name", "a quest needs a name players can read");
        if (q.Steps is null || q.Steps.Count == 0) throw new BlockError(ctx.Block, "steps", "a quest needs at least one step");
        if (q.Source != null && !Sources.Contains(q.Source)) throw new BlockError(ctx.Block, "source", $"\"{q.Source}\": a keen::QuestSourceType: {string.Join(", ", Sources)}");
        if (q.Priority is < 0) throw new BlockError(ctx.Block, "priority", $"{q.Priority} is less than 0");
        if (q.PartyRadius is { } pr && !(pr > 0 && double.IsFinite(pr))) throw new BlockError(ctx.Block, "partyRadius", $"{pr} must be more than 0 metres");
        var def = new QuestDefinition
        {
            Id = name, Name = q.Name, Description = q.Description, Giver = q.Giver is { } g ? Npc(ctx, g, "giver") : null, Scope = q.Scope,
            PartyRadius = q.PartyRadius ?? 60, Main = q.Main, Auto = q.Auto, Repeatable = q.Repeatable, Retryable = q.Retryable, Abandonable = q.Abandonable,
            Hidden = q.Hidden, Journal = QuestJournalMode.Native, Source = q.Source, Priority = q.Priority,
        };
        def.ModId = ctx.Mod.Id;
        def.Requires = Conditions(ctx, q.Requires, "requires");
        def.OnStart = Actions(ctx, q.OnStart, "onStart");
        def.OnFail = Actions(ctx, q.OnFail, "onFail");
        if (q.Rewards != null) def.Rewards = Reward(ctx, q.Rewards, "rewards");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < q.Steps.Count; i++) def.Steps.Add(Step(ctx, q.Steps[i], $"steps[{i}]", ids));
        return def;
    }

    private static QuestStep Step(BlockContext ctx, StepBlock s, string path, HashSet<string> ids)
    {
        if (s is null) throw new BlockError(ctx.Block, path, "a step is an object");
        CheckId(ctx, s.Id, $"{path}.id", "a step's id");
        if (!ids.Add(s.Id)) throw new BlockError(ctx.Block, $"{path}.id", $"step \"{s.Id}\" twice");
        if (s.RecommendedLevel < 0) throw new BlockError(ctx.Block, $"{path}.recommendedLevel", $"{s.RecommendedLevel} is less than 0");
        // the game's journal shows a step by its text: the boot build leaves the mod's whole journal out for one without
        if (s.Text is null && s.Title is null) throw new BlockError(ctx.Block, $"{path}.text", "a step in the journal needs text (or a title)");
        var step = new QuestStep
        {
            Id = s.Id, Text = s.Text ?? new UiText(""), Title = s.Title, Voice = s.Voice, RecommendedLevel = s.RecommendedLevel,
            OnEnter = Actions(ctx, s.OnEnter, $"{path}.onEnter"),
        };
        if (s.Objective != null) step.Objectives.Add(Objective(ctx, s.Objective, $"{path}.objective"));
        for (var i = 0; i < (s.Objectives?.Count ?? 0); i++) step.Objectives.Add(Objective(ctx, s.Objectives![i], $"{path}.objectives[{i}]"));
        if (s.Site is null && step.Objectives.FindIndex(o => o is { Kind: QuestObjectiveKind.Reach, Site: true }) is var site and >= 0)
            throw new BlockError(ctx.Block, s.Objective != null && site == 0 ? $"{path}.objective.reach.site" : $"{path}.objectives[{site - (s.Objective != null ? 1 : 0)}].reach.site", "reach the site: the step has no site (say where in \"site\")");
        if (s.Marker is { Hidden: true }) step.NoMarker = true;
        else if (s.Marker is { } m)
            step.Marker = new QuestMarker(Pos(ctx, m.At, $"{path}.marker.at"), m.Radius ?? 0, m.Waypoint, m.Name, m.Base);
        if (s.Site != null) step.Site = Place(ctx, s.Site, $"{path}.site");
        for (var i = 0; i < (s.Spawn?.Count ?? 0); i++) step.Spawns.Add(Spawn(ctx, s.Spawn![i], $"{path}.spawn[{i}]"));
        // what the step pays comes before its own hooks: "Well done" after the reward
        if (s.Rewards != null) step.OnDone.AddRange(Reward(ctx, s.Rewards, $"{path}.rewards"));
        step.OnDone.AddRange(Actions(ctx, s.OnDone, $"{path}.onDone"));
        return step;
    }

    private static QuestSpawn Spawn(BlockContext ctx, StepSpawnBlock s, string path)
    {
        if (s is null) throw new BlockError(ctx.Block, path, "a spawn is an object");
        if (string.IsNullOrWhiteSpace(s.Template.Value)) throw new BlockError(ctx.Block, $"{path}.template", "a spawn needs a template");
        if (s.Count < 1) throw new BlockError(ctx.Block, $"{path}.count", $"{s.Count} is less than 1");
        if (s.Retries is < 0) throw new BlockError(ctx.Block, $"{path}.retries", $"{s.Retries} is less than 0");
        if (s.Spread is { } sp && !(sp >= 0 && double.IsFinite(sp))) throw new BlockError(ctx.Block, $"{path}.spread", $"{sp} must be 0 metres or more");
        return new QuestSpawn
        {
            Template = s.Template.Value, Count = s.Count, Tag = s.Tag, Level = s.Level, Retries = s.Retries ?? 5, Spread = s.Spread ?? 4,
            // as quests/*.json: only a spawn that says where has a place of its own; the step's site is the other
            Place = s.At != null || s.Near != null ? Place(ctx, s, path) : null,
        };
    }

    private static QuestPlace Place(BlockContext ctx, PlaceBlock p, string path)
    {
        var place = new QuestPlace { At = Pos(ctx, p.At, $"{path}.at"), InBase = p.InBase, Exact = p.Exact };
        if (p.Near is { Length: > 0 } near)
        {
            if (near.Equals("player", StringComparison.OrdinalIgnoreCase)) place.NearPlayer = true;
            else place.NearNpc = Npc(ctx, near, $"{path}.near");
        }
        if (place.At is null && place.NearNpc is null) place.NearPlayer = true;
        if (p.Distance is { } d)
        {
            if (d.Length is < 1 or > 2 || d.Any(x => !(x >= 0 && double.IsFinite(x)))) throw new BlockError(ctx.Block, $"{path}.distance", "metres: [min, max]");
            place.MinDistance = d[0];
            place.MaxDistance = d[^1];
        }
        if (p.Margin is { } margin)
        {
            if (!(margin >= 0 && double.IsFinite(margin))) throw new BlockError(ctx.Block, $"{path}.margin", $"{margin} must be 0 metres or more");
            place.BaseMargin = margin;
        }
        if (place.Exact && place.At is null) throw new BlockError(ctx.Block, $"{path}.exact", "exactly where? It needs at");
        return place;
    }

    // ---- objectives -------------------------------------------------------------------------------------------------

    private static QuestObjective Objective(BlockContext ctx, ObjectiveBlock o, string path)
    {
        if (o is null) throw new BlockError(ctx.Block, path, "an objective is an object");
        var kinds = new List<string>();
        if (o.Kill != null) kinds.Add("kill");
        if (o.Collect != null) kinds.Add("collect");
        if (o.Deliver != null) kinds.Add("deliver");
        if (o.Reach != null) kinds.Add("reach");
        if (o.Talk != null) kinds.Add("talk");
        if (o.Craft != null) kinds.Add("craft");
        if (o.Build != null) kinds.Add("build");
        if (o.Knowledge != null) kinds.Add("knowledge");
        if (o.Custom != null) kinds.Add("custom");
        if (kinds.Count != 1)
            throw new BlockError(ctx.Block, path, kinds.Count == 0 ? "an objective needs one of kill, collect, deliver, reach, talk, craft, build, knowledge, custom"
                : $"an objective is one thing: {string.Join(" and ", kinds)} are two");
        QuestObjective r;
        switch (kinds[0])
        {
            case "kill":
            {
                var k = o.Kill!;
                if (k.Template is null && k.Family is null && k.Tag is null) throw new BlockError(ctx.Block, $"{path}.kill", "kill needs template, family or tag");
                Count(ctx, k.Count, $"{path}.kill.count");
                r = new QuestObjective { Kind = QuestObjectiveKind.Kill, Template = k.Template, Family = k.Family, Tag = k.Tag, Count = k.Count, CountDestroyed = k.CountDestroyed, Near = Pos(ctx, k.Near, $"{path}.kill.near") };
                if (k.Radius is { } kr) r.Radius = Radius(ctx, kr, $"{path}.kill.radius");
                break;
            }
            case "collect":
            {
                var c = o.Collect!;
                Count(ctx, c.Count, $"{path}.collect.count");
                r = new QuestObjective { Kind = QuestObjectiveKind.Collect, Item = ctx.ItemName(c.Item, $"{path}.collect.item"), Count = c.Count };
                break;
            }
            case "deliver":
            {
                var d = o.Deliver!;
                Count(ctx, d.Count, $"{path}.deliver.count");
                r = new QuestObjective { Kind = QuestObjectiveKind.Deliver, Item = ctx.ItemName(d.Item, $"{path}.deliver.item"), Count = d.Count, To = d.To is { } to ? Npc(ctx, to, $"{path}.deliver.to") : null };
                break;
            }
            case "reach":
            {
                var a = o.Reach!;
                r = new QuestObjective { Kind = QuestObjectiveKind.Reach, At = Pos(ctx, a.At, $"{path}.reach.at"), Offset = Pos(ctx, a.Offset, $"{path}.reach.offset"), Marker = a.Marker, Site = a.Site };
                if (r.At is null && r.Offset is null && r.Marker is null && !r.Site) throw new BlockError(ctx.Block, $"{path}.reach", "reach needs at, offset, marker or site");
                if (a.Radius is { } ar) r.Radius = Radius(ctx, ar, $"{path}.reach.radius");
                break;
            }
            case "talk":
            {
                var t = o.Talk!;
                if (string.IsNullOrWhiteSpace(t.Npc)) throw new BlockError(ctx.Block, $"{path}.talk.npc", "talk needs an NPC");
                r = new QuestObjective { Kind = QuestObjectiveKind.Talk, Npc = Npc(ctx, t.Npc, $"{path}.talk.npc"), Line = t.Line, Node = t.Node };
                break;
            }
            case "craft":
            {
                var c = o.Craft!;
                if (c.Item is null && c.Recipe is null) throw new BlockError(ctx.Block, $"{path}.craft", "craft needs item or recipe");
                Count(ctx, c.Count, $"{path}.craft.count");
                r = new QuestObjective { Kind = QuestObjectiveKind.Craft, Item = c.Item is { } item ? ctx.ItemName(item, $"{path}.craft.item") : null, Recipe = c.Recipe, Count = c.Count };
                break;
            }
            case "build":
            {
                var b = o.Build!;
                Count(ctx, b.Count, $"{path}.build.count");
                r = new QuestObjective { Kind = QuestObjectiveKind.Build, Item = ctx.ItemName(b.Prop, $"{path}.build.prop"), Count = b.Count };
                break;
            }
            case "knowledge":
                if (string.IsNullOrWhiteSpace(o.Knowledge)) throw new BlockError(ctx.Block, $"{path}.knowledge", "a knowledge name: npc:…, quest:…, query:…, 0x…");
                r = new QuestObjective { Kind = QuestObjectiveKind.Knowledge, Knowledge = o.Knowledge };
                break;
            default:
            {
                var c = o.Custom!;
                if (string.IsNullOrWhiteSpace(c.Signal)) throw new BlockError(ctx.Block, $"{path}.custom.signal", "custom needs a signal name");
                Count(ctx, c.Count, $"{path}.custom.count");
                r = new QuestObjective { Kind = QuestObjectiveKind.Custom, Signal = c.Signal, Count = c.Count };
                break;
            }
        }
        r.Text = o.Text;
        return r;
    }

    // ---- conditions -------------------------------------------------------------------------------------------------

    private static List<QuestCondition> Conditions(BlockContext ctx, IReadOnlyList<ConditionBlock>? list, string path)
    {
        var r = new List<QuestCondition>();
        for (var i = 0; i < (list?.Count ?? 0); i++) r.Add(Condition(ctx, list![i], $"{path}[{i}]"));
        return r;
    }

    internal static QuestCondition Condition(BlockContext ctx, ConditionBlock c, string path)
    {
        if (c is null) throw new BlockError(ctx.Block, path, "a condition is an object");
        var kinds = new List<string>();
        if (c.Knowledge != null) kinds.Add("knowledge");
        if (c.Level != null) kinds.Add("level");
        if (c.Quest != null) kinds.Add("quest");
        if (c.Step != null) kinds.Add("step");
        if (c.Item != null) kinds.Add("item");
        if (c.Predicate != null) kinds.Add("predicate");
        if (c.All != null) kinds.Add("all");
        if (c.Any != null) kinds.Add("any");
        if (c.Not != null) kinds.Add("not");
        if (kinds.Count != 1)
            throw new BlockError(ctx.Block, path, kinds.Count == 0 ? "a condition needs one of knowledge, level, quest, step, item, predicate, all, any, not"
                : $"a condition is one thing: {string.Join(" and ", kinds)} are two (put them in all or any)");
        if (c.Is != null && kinds[0] is not ("quest" or "step")) throw new BlockError(ctx.Block, $"{path}.is", "\"is\" goes with quest and step");
        switch (kinds[0])
        {
            case "knowledge":
                return string.IsNullOrWhiteSpace(c.Knowledge) ? throw new BlockError(ctx.Block, $"{path}.knowledge", "a knowledge name") : QuestCondition.HasKnowledge(c.Knowledge);
            case "level":
                return c.Level < 1 ? throw new BlockError(ctx.Block, $"{path}.level", $"{c.Level} is less than 1") : QuestCondition.Level(c.Level!.Value);
            case "quest":
            {
                var state = (c.Is ?? "done");
                if (!QuestStates.Contains(state)) throw new BlockError(ctx.Block, $"{path}.is", $"\"{state}\": {string.Join(", ", QuestStates)}");
                NotOtherKind(ctx, c.Quest!, $"{path}.quest", typeof(QuestBlock));
                return QuestCondition.Quest(c.Quest!, state);
            }
            case "step":
            {
                var state = (c.Is ?? "done");
                if (!StepStates.Contains(state)) throw new BlockError(ctx.Block, $"{path}.is", $"\"{state}\": {string.Join(", ", StepStates)}");
                var dot = c.Step!.LastIndexOf('.');
                if (dot < 1 || dot == c.Step.Length - 1) throw new BlockError(ctx.Block, $"{path}.step", $"\"{c.Step}\": a step is \"quest.step\"");
                NotOtherKind(ctx, c.Step[..dot], $"{path}.step", typeof(QuestBlock));
                return QuestCondition.QuestStep(c.Step, state);
            }
            case "item":
            {
                Count(ctx, c.Count, $"{path}.count");
                return QuestCondition.HasItem(ctx.ItemName(c.Item!.Value, $"{path}.item"), c.Count);
            }
            case "predicate":
                return string.IsNullOrWhiteSpace(c.Predicate) ? throw new BlockError(ctx.Block, $"{path}.predicate", "the name of a function registered with Quests.RegisterPredicate")
                    : new QuestCondition { Kind = QuestConditionKind.Predicate, Name = c.Predicate };
            case "all":
            case "any":
            {
                var list = kinds[0] == "all" ? c.All! : c.Any!;
                if (list.Count == 0) throw new BlockError(ctx.Block, $"{path}.{kinds[0]}", $"{kinds[0]} of nothing");
                return new QuestCondition { Kind = kinds[0] == "all" ? QuestConditionKind.All : QuestConditionKind.Any, Children = Conditions(ctx, list, $"{path}.{kinds[0]}") };
            }
            default:
                return new QuestCondition { Kind = QuestConditionKind.Not, Children = [Condition(ctx, c.Not!, $"{path}.not")] };
        }
    }

    // ---- actions ----------------------------------------------------------------------------------------------------

    private static List<QuestAction> Actions(BlockContext ctx, IReadOnlyList<ActionBlock>? list, string path)
    {
        var r = new List<QuestAction>();
        for (var i = 0; i < (list?.Count ?? 0); i++) r.Add(Action(ctx, list![i], $"{path}[{i}]"));
        return r;
    }

    internal static QuestAction Action(BlockContext ctx, ActionBlock a, string path)
    {
        if (a is null) throw new BlockError(ctx.Block, path, "an action is an object");
        var verbs = new List<string>();
        void Say(bool set, string verb) { if (set) verbs.Add(verb); }
        Say(a.Xp != null, "xp"); Say(a.Item != null, "item"); Say(a.Take != null, "take"); Say(a.Knowledge != null, "knowledge"); Say(a.Revoke != null, "revoke");
        Say(a.Start != null, "start"); Say(a.Complete != null, "complete"); Say(a.Fail != null, "fail"); Say(a.Advance != null, "advance"); Say(a.Reset != null, "reset");
        Say(a.Spawn != null, "spawn"); Say(a.Despawn != null, "despawn"); Say(a.Teleport != null, "teleport"); Say(a.Chat != null, "chat"); Say(a.Talk != null, "talk");
        Say(a.Signal != null, "signal"); Say(a.Call != null, "call");
        if (verbs.Count != 1)
            throw new BlockError(ctx.Block, path, verbs.Count == 0 ? "an action needs one of xp, item, take, knowledge, revoke, start, complete, fail, advance, reset, spawn, despawn, teleport, chat, talk, signal, call"
                : $"an action is one thing: {string.Join(" and ", verbs)} are two (make two actions)");
        string Name(string? s, string field) => string.IsNullOrWhiteSpace(s) ? throw new BlockError(ctx.Block, $"{path}.{field}", "a name") : s;
        switch (verbs[0])
        {
            case "xp":
                return new QuestAction { Kind = QuestActionKind.Xp, Value = a.Xp!.Value };
            case "item":
                Count(ctx, a.Count, $"{path}.count");
                return new QuestAction { Kind = QuestActionKind.Give, Name = ctx.ItemName(a.Item!.Value, $"{path}.item"), Count = a.Count };
            case "take":
                Count(ctx, a.Count, $"{path}.count");
                return new QuestAction { Kind = QuestActionKind.Take, Name = ctx.ItemName(a.Take!.Value, $"{path}.take"), Count = a.Count };
            case "knowledge":
                return new QuestAction { Kind = QuestActionKind.Knowledge, Name = Name(a.Knowledge, "knowledge"), Value = a.Value ?? 1, Target = Target(ctx, a.Target, path) };
            case "revoke":
                if (a.Flag is < 0 or > 31) throw new BlockError(ctx.Block, $"{path}.flag", $"{a.Flag} is outside 0..31");
                return new QuestAction { Kind = QuestActionKind.Revoke, Name = Name(a.Revoke, "revoke"), Value = (uint)((a.Flag ?? -1) + 1), Target = Target(ctx, a.Target, path) };
            case "start":
                NotOtherKind(ctx, a.Start!, $"{path}.start", typeof(QuestBlock));
                return new QuestAction { Kind = QuestActionKind.Start, Name = Name(a.Start, "start"), Force = a.Force };
            case "complete":
                NotOtherKind(ctx, a.Complete!, $"{path}.complete", typeof(QuestBlock));
                return new QuestAction { Kind = QuestActionKind.Complete, Name = Name(a.Complete, "complete") };
            case "fail":
                NotOtherKind(ctx, a.Fail!, $"{path}.fail", typeof(QuestBlock));
                return new QuestAction { Kind = QuestActionKind.Fail, Name = Name(a.Fail, "fail") };
            case "advance":
            {
                var dot = a.Advance!.LastIndexOf('.');
                if (dot < 1 || dot == a.Advance.Length - 1) throw new BlockError(ctx.Block, $"{path}.advance", $"\"{a.Advance}\": a step is \"quest.step\"");
                NotOtherKind(ctx, a.Advance[..dot], $"{path}.advance", typeof(QuestBlock));
                return new QuestAction { Kind = QuestActionKind.Advance, Name = a.Advance, Force = a.Force };
            }
            case "reset":
                NotOtherKind(ctx, a.Reset!, $"{path}.reset", typeof(QuestBlock));
                return new QuestAction { Kind = QuestActionKind.Reset, Name = Name(a.Reset, "reset") };
            case "spawn":
                Count(ctx, a.Count, $"{path}.count");
                return new QuestAction
                {
                    Kind = QuestActionKind.Spawn, Name = Name(a.Spawn!.Value.Value, "spawn"), Count = a.Count, At = Pos(ctx, a.At, $"{path}.at"), Offset = Pos(ctx, a.Offset, $"{path}.offset"),
                    Yaw = a.Yaw ?? 0, Tag = a.Tag, BindNpc = a.BindNpc is { } bind ? Npc(ctx, bind, $"{path}.bindNpc") : null, Transient = a.Transient, Exact = a.Exact,
                };
            case "despawn":
                return new QuestAction { Kind = QuestActionKind.Despawn, Name = Name(a.Despawn, "despawn") };
            case "teleport":
                return new QuestAction { Kind = QuestActionKind.Teleport, At = Pos(ctx, a.Teleport, $"{path}.teleport"), Offset = Pos(ctx, a.Offset, $"{path}.offset") };
            case "chat":
                return new QuestAction { Kind = QuestActionKind.Chat, Text = a.Chat };
            case "talk":
                return new QuestAction { Kind = QuestActionKind.Talk, Name = Dialogue(ctx, Name(a.Talk, "talk"), $"{path}.talk"), Node = a.Node };
            case "signal":
                Count(ctx, a.Amount, $"{path}.amount");
                return new QuestAction { Kind = QuestActionKind.Signal, Name = Name(a.Signal, "signal"), Count = a.Amount };
            default:
                return new QuestAction { Kind = QuestActionKind.Call, Name = Name(a.Call, "call") };
        }
    }

    private static string? Target(BlockContext ctx, string? target, string path)
    {
        if (target != null && target is not ("world" or "player")) throw new BlockError(ctx.Block, $"{path}.target", $"\"{target}\": world or player");
        return target;
    }

    // ---- rewards ----------------------------------------------------------------------------------------------------

    /// <summary>What a reward pays, in this order: experience, items, knowledge, the status, the loot table.</summary>
    internal static List<QuestAction> Reward(BlockContext ctx, RewardBlock r, string path)
    {
        var list = new List<QuestAction>();
        if (r.Xp is { } xp) list.Add(QuestAction.Experience(xp));
        foreach (var (item, count) in r.Items ?? new Dictionary<ItemRef, int>())
        {
            if (count < 1) throw new BlockError(ctx.Block, $"{path}.items[{item}]", $"{count} is less than 1");
            list.Add(QuestAction.Item(ctx.ItemName(item, $"{path}.items[{item}]"), count));
        }
        foreach (var k in r.Knowledge ?? [])
        {
            if (string.IsNullOrWhiteSpace(k)) throw new BlockError(ctx.Block, $"{path}.knowledge", "a knowledge name: npc:….unlock");
            list.Add(QuestAction.GrantKnowledge(k));
        }
        if (r.Status is { } status) list.Add(StatusAction(ctx, status, $"{path}.status"));
        if (r.LootTable is { } table) list.Add(LootAction(ctx, table, $"{path}.lootTable"));
        if (list.Count == 0) throw new BlockError(ctx.Block, path, "a reward pays nothing: say xp, items, status, knowledge or lootTable");
        return list;
    }

    // A loot table rolled when the reward is paid, and what came up given as the reward's items are (the same path: the game's "+N" popup, the rest of a full inventory
    // at the player's feet). One roll for whoever is paid: a world quest gives every player in game what that roll gave, as it does with items.
    private static QuestAction LootAction(BlockContext ctx, string table, string path)
    {
        if (string.IsNullOrWhiteSpace(table)) throw new BlockError(ctx.Block, path, "the name of a lootTables block");
        var h = ctx.Find(table, path) ?? throw new BlockError(ctx.Block, path, $"no lootTables block \"{table}\" in this mod; define it, or say \"other-mod:{table}\"");
        if (h is not LootTableHandle loot) throw new BlockError(ctx.Block, path, $"\"{table}\" is {Kind(h.BlockType)} block, not a loot table");
        var plan = loot.Plan;
        var name = $"lootTable {(table.Contains(':') ? table : $"{ctx.Mod.Id}:{table}")}";
        var action = QuestAction.Code(async c =>
        {
            var stacks = plan.Roll(Random.Shared);
            var who = $"{c.Holder.Key} {c.Quest?.FullId ?? c.ModId} reward";
            QuestActions.Note($"{who}: {name}: rolled {(stacks.Count == 0 ? "nothing" : string.Join(", ", stacks.Select(s => $"{s.Item} x{s.Count}")))}");
            if (stacks.Count == 0) return;
            await QuestActions.RunAll(stacks.Select(s => QuestAction.Item(s.Item, s.Count)).ToList(), c, $"reward {name}", keepGoing: true).OnTick();
        });
        action.Name = name;
        return action;
    }

    // A status put on whoever is paid: a code action, as the engine has no kind for it. A block's status is its GUID; a game status's GUID is found when it is paid.
    private static QuestAction StatusAction(BlockContext ctx, StatusApply apply, string path)
    {
        if (apply.Status is null) throw new BlockError(ctx.Block, path, "a status to put on the player");
        if (apply.Seconds is { } s && !(s > 0 && double.IsFinite(s))) throw new BlockError(ctx.Block, $"{path}.seconds", $"{s} must be more than 0 seconds");
        Guid? known = null;
        string? game = null;
        double? seconds = apply.Seconds;
        if (apply.Status.Name is { } n && !n.Contains(':') && !ctx.Mod.Handles.ContainsKey(n) && !ctx.Mod.Pending.ContainsKey(n))
        {
            if (Guid.TryParse(n, out var g)) known = g;
            else if (n.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || n.StartsWith("buff_", StringComparison.Ordinal)) game = n;
        }
        if (known is null && game is null)
        {
            var (guid, own) = ctx.Status(apply.Status, path);
            known = guid;
            seconds ??= own > 0 ? own : null;
        }
        var label = apply.Status.Name ?? ctx.Block;
        return QuestAction.Code(async c =>
        {
            var to = c.Holder.IsWorld ? Quests.Online().Select(h => h.Entity).Where(e => !e.IsNone).ToArray() : c.Holder.Entity.IsNone ? [] : [c.Holder.Entity];
            var who = $"{c.Holder.Key} {c.Quest?.FullId ?? c.ModId} reward";
            if (to.Length == 0)
            {
                QuestActions.Note($"{who}: status {label}: skipped (no player)");
                return;
            }
            var buff = known ?? ResolveGame(game!) ?? throw new InvalidOperationException($"the game has no status \"{game}\"");
            foreach (var e in to)
            {
                await Abilities.ApplyBuff(e, buff, seconds is { } sec ? TimeSpan.FromSeconds(sec) : null, e).OnTick();
                QuestActions.Note($"{who}: status {label} on {e}: applied");
            }
        });
    }

    // as BlockRuntime does for a program that names a game status: the data has the buff's id, any 16 bytes with that hash are the reference to it
    private static Guid? ResolveGame(string name) =>
        Content.Buffs.Get(name) is { } buff ? buff.Guid != Guid.Empty ? buff.Guid : BlockIds.WithFnv(buff.Id) : null;

    // ---- dialogues --------------------------------------------------------------------------------------------------

    internal static DialogueDefinition Dialogue(BlockContext ctx, string name, DialogueBlock d)
    {
        CheckId(ctx, name, null, "a dialogue's id (its block name)");
        if (d.Nodes is null || d.Nodes.Count == 0) throw new BlockError(ctx.Block, "nodes", "a dialogue needs nodes");
        if (string.IsNullOrWhiteSpace(d.Start)) throw new BlockError(ctx.Block, "start", "the first node's name");
        Hint(ctx, d.Hint, "hint");
        var def = new DialogueDefinition { Id = name, Start = d.Start, Native = true, Greet = d.Greet, Hint = d.Hint };
        def.ModId = ctx.Mod.Id;
        for (var i = 0; i < (d.Greetings?.Count ?? 0); i++)
        {
            var g = d.Greetings![i];
            var gp = $"greetings[{i}]";
            if (g?.Text is null) throw new BlockError(ctx.Block, $"{gp}.text", "a greeting needs text");
            Hint(ctx, g.Hint, $"{gp}.hint");
            def.Greetings.Add(new DialogueGreeting { Text = g.Text, If = g.If != null ? Condition(ctx, g.If, $"{gp}.if") : null, Voice = g.Voice, Hint = g.Hint });
        }
        def.Farewell.AddRange(d.Farewell ?? []);
        def.Transition.AddRange(d.Transition ?? []);
        foreach (var (id, n) in d.Nodes)
        {
            var np = $"nodes.{id}";
            if (n is null) throw new BlockError(ctx.Block, np, "a node is an object");
            Hint(ctx, n.Hint, $"{np}.hint");
            var node = new DialogueNode { Speaker = n.Speaker, Text = n.Text ?? new UiText(""), Voice = n.Voice, Hint = n.Hint, Goto = n.Goto, Do = Actions(ctx, n.Do, $"{np}.do") };
            for (var i = 0; i < (n.Options?.Count ?? 0); i++)
            {
                var o = n.Options![i];
                var op = $"{np}.options[{i}]";
                if (o?.Text is null) throw new BlockError(ctx.Block, $"{op}.text", "an option needs text");
                Hint(ctx, o.Hint, $"{op}.hint");
                node.Options.Add(new DialogueOption
                {
                    Text = o.Text, If = o.If != null ? Condition(ctx, o.If, $"{op}.if") : null, Do = Actions(ctx, o.Do, $"{op}.do"), Goto = o.Goto, Reply = o.Reply, Voice = o.Voice,
                    Once = o.Once, Close = o.Close, Hint = o.Hint,
                });
            }
            def.Nodes[id] = node;
        }
        if (Dialogues.Check(def) is { Count: > 0 } problems) throw new BlockError(ctx.Block, "nodes", string.Join("; ", problems));
        return def;
    }

    private static void Hint(BlockContext ctx, string? hint, string path)
    {
        if (hint != null && !Hints.Contains(hint)) throw new BlockError(ctx.Block, path, $"\"{hint}\": {string.Join(", ", Hints)}");
    }

    // ---- what a name means ------------------------------------------------------------------------------------------

    /// <summary>A dialogue by name: a block of this mod (or "other-mod:name") becomes its id; a name that is no block is one of the mod's quests/*.json, and stays as written.</summary>
    internal static string Dialogue(BlockContext ctx, string name, string path) => Named(ctx, name, path, typeof(DialogueBlock), "a dialogue", h => h.Name);

    internal static string NpcDialogue(BlockContext ctx, string name)
    {
        var colon = name.IndexOf(':');
        if (colon >= 0)
        {
            if (!name[..colon].Equals(ctx.Mod.Id, StringComparison.Ordinal))
                throw new BlockError(ctx.Block, "talk", "an NPC's native dialogue must belong to this mod; other-mod:name is not supported by the startup build");
            name = name[(colon + 1)..];
        }
        return Dialogue(ctx, name, "talk");
    }

    /// <summary>An NPC by name: an NPC block of this mod (or "other-mod:name") becomes its id in the engine; any other name is an NPC of quests/*.json, a template or
    /// <c>npc:…</c>, and stays as written.</summary>
    internal static string Npc(BlockContext ctx, string name, string path) =>
        Named(ctx, name, path, typeof(NpcBlock), "an NPC", h => (h as BlockHandle<NpcBlock>)?.Block.Was ?? h.Name);

    private static string Named(BlockContext ctx, string name, string path, Type want, string what, Func<BlockHandle, string> id)
    {
        var colon = name.IndexOf(':');
        // "npc:NPC_Workshop_Bard01" or another mod's NPC from its quests/*.json is no block: the lookup that throws is for a name a block can be
        var h = colon > 0 ? Blocks.Find(name[(colon + 1)..], name[..colon]) : ctx.Find(name, path);
        if (h is null) return name;
        if (h.BlockType != want) throw new BlockError(ctx.Block, path, $"\"{name}\" is {Kind(h.BlockType)} block, not {what}");
        return colon > 0 ? $"{h.Mod}:{id(h)}" : id(h);
    }

    // a name that is a block of the wrong kind: no compile is forced (a quest names a quest that may name it back), only what is known now
    private static void NotOtherKind(BlockContext ctx, string name, string path, Type want)
    {
        if (name.Contains(':')) return;
        if (ctx.Mod.Handles.TryGetValue(name, out var h) && h.BlockType != want)
            throw new BlockError(ctx.Block, path, $"\"{name}\" is {Kind(h.BlockType)} block, not a quest");
    }

    private static string Kind(Type t)
    {
        var kind = t.Name.Replace("Block", "").ToLowerInvariant();
        return (kind.Length > 0 && "aeiou".Contains(kind[0]) ? "an " : "a ") + kind;
    }

    // ---- small checks -----------------------------------------------------------------------------------------------

    private static void CheckId(BlockContext ctx, string? id, string? field, string what)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
            throw new BlockError(ctx.Block, field, $"{what} \"{id}\": letters, digits, '_' and '-'");
    }

    private static void Count(BlockContext ctx, int count, string path)
    {
        if (count < 1) throw new BlockError(ctx.Block, path, $"{count} is less than 1");
    }

    private static double Radius(BlockContext ctx, double radius, string path) =>
        radius > 0 && double.IsFinite(radius) ? radius : throw new BlockError(ctx.Block, path, $"{radius} must be more than 0 metres");

    private static WorldPosition? Pos(BlockContext ctx, double[]? p, string path)
    {
        if (p is null) return null;
        if (p.Length != 3 || p.Any(x => !double.IsFinite(x))) throw new BlockError(ctx.Block, path, "a place is [x, y, z], in metres");
        return new WorldPosition(p[0], p[1], p[2]);
    }

    // ---- what it became, for `kiln blocks show` and the tests -------------------------------------------------------

    private static JsonObject Describe(QuestDefinition q)
    {
        static string Action(QuestAction a) => a.Kind switch
        {
            QuestActionKind.Xp => $"xp {a.Value}",
            QuestActionKind.Give => $"item {a.Name} x{a.Count}",
            QuestActionKind.Call when a.Run is not null => a.Name ?? "status",
            _ => $"{a.Kind.ToString().ToLowerInvariant()} {a.Name}".TrimEnd(),
        };
        static JsonArray Actions(IEnumerable<QuestAction> list) => new(list.Select(a => (JsonNode)JsonValue.Create(Action(a))!).ToArray());
        var steps = new JsonArray();
        foreach (var s in q.Steps)
            steps.Add(new JsonObject
            {
                ["id"] = s.Id,
                ["objectives"] = new JsonArray(s.Objectives.Select(o => (JsonNode)JsonValue.Create($"{o.Kind.ToString().ToLowerInvariant()}{(o.Count > 1 ? $" x{o.Count}" : "")}")!).ToArray()),
                ["onDone"] = Actions(s.OnDone),
            });
        return new JsonObject
        {
            ["id"] = q.Id, ["scope"] = q.Scope.ToString().ToLowerInvariant(), ["giver"] = q.Giver, ["steps"] = steps, ["rewards"] = Actions(q.Rewards),
        };
    }
}
