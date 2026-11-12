// SPDX-License-Identifier: MIT
// A weapon's moves -> clones of sequences, their rules, and the item's slots pointing at them. Offline, at compile: what can be known without the game (the
// class has the slot, the numbers make sense, a GUID or an id is a state), the clones of the sequences a move names by `like`, and the plan, under
// low.Json["moves"]. When content is ready (MovePlanner, on the game's data): which index of the item each slot is, the clones of the class's own
// sequences, the states, the retime (MoveTiming) and the chain links, then the item's slots. docs/kiln/modding/item-blocks.md, "Moves".
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>One move of a block, as the compile made it.</summary>
internal sealed record PlannedMove(MoveSlot? Slot, string Path, int Link, int Links, string CloneName, Guid Guid, string? Like,
    IReadOnlyList<AnimationRef?>? Animation, double? HitAt, double? Length);

/// <summary>A weapon's moves, waiting for content ready.</summary>
internal sealed record MovePlan(string Mod, string Block, string Item, IReadOnlyDictionary<MoveSlot, SlotSelector> Slots, IReadOnlyList<PlannedMove> Moves, bool HeavyAirFollowsPrimary);

internal static partial class Compiler
{
    private static (MoveSlot Slot, MoveChain? Chain, MoveBlock? One)[] Written(WeaponMoves m) =>
    [
        (MoveSlot.Primary, m.Primary, null), (MoveSlot.Heavy, m.Heavy, null), (MoveSlot.HeavyAir, null, m.HeavyAir), (MoveSlot.DodgeAttack, null, m.DodgeAttack),
        (MoveSlot.JumpAttack, null, m.JumpAttack), (MoveSlot.PlungeAttack, null, m.PlungeAttack), (MoveSlot.Block, null, m.Block), (MoveSlot.Parry, null, m.Parry),
        (MoveSlot.Special, null, m.Special), (MoveSlot.Execution, null, m.Execution), (MoveSlot.SneakAttack, null, m.SneakAttack),
    ];

    /// <summary>The plan a block's lowered JSON came with: what content ready works from (tests read it).</summary>
    internal static readonly System.Runtime.CompilerServices.ConditionalWeakTable<JsonObject, MovePlan> Plans = new();

    /// <summary>A move's numbers and names, offline. <paramref name="blow"/>: the slot lands one (a block and a parry do not).</summary>
    internal static IReadOnlyList<AnimationRef?>? CheckMove(BlockContext ctx, MoveAnimation? animation, string? like, double? hitAt, double? length, string path, bool blow = true)
    {
        if (animation == null && like == null && hitAt == null && length == null)
            throw new BlockError(ctx.Block, path, "this move changes nothing: give it an animation, a like, a hitAt or a length");
        if (like is { } l && string.IsNullOrWhiteSpace(l)) throw new BlockError(ctx.Block, $"{path}.like", "like names a game sequence: a sub-sequence name or a sequence GUID");
        if (hitAt is { } h)
        {
            if (!blow) throw new BlockError(ctx.Block, $"{path}.hitAt", "a block and a parry land no blow, so there is nothing to move: leave hitAt out (length times the whole motion)");
            if (!double.IsFinite(h) || h <= 0) throw new BlockError(ctx.Block, $"{path}.hitAt", $"{h} must be a number of seconds more than 0");
        }
        if (length is { } n)
        {
            if (!double.IsFinite(n) || n <= 0) throw new BlockError(ctx.Block, $"{path}.length", $"{n} must be a number of seconds more than 0");
            if (hitAt is { } h2 && h2 >= n) throw new BlockError(ctx.Block, $"{path}.hitAt", $"{h2} s is not inside the move's length (0, {n})");
        }
        if (animation == null) return null;
        if (animation.Count == 0 || animation.All(a => a == null)) throw new BlockError(ctx.Block, $"{path}.animation", "the list names no state: give at least one, a null keeps that one");
        return animation.Select((a, i) => MoveStates.Parse(ctx, a, animation.Count == 1 ? $"{path}.animation" : $"{path}.animation[{i}]")).ToList();
    }

    private static JsonNode? AnimationJson(IReadOnlyList<AnimationRef?>? a) => a == null ? null : new JsonArray(a.Select(x => x == null ? null
        : (JsonNode)(x.Guid is { } g ? new JsonObject { ["written"] = x.Written, ["guid"] = g.ToString() } : new JsonObject { ["written"] = x.Written, ["lookup"] = x.Name })).ToArray());

    private static void LowerMoves(Draft d, string display, WeaponClass cls, WeaponMoves m)
    {
        var ctx = d.Ctx;
        var low = d.Low;
        var mod = ctx.Mod.Id;
        var map = MoveSlots.Of(cls);
        var moves = new List<PlannedMove>();
        var written = Written(m).Where(w => w.Chain != null || w.One != null).ToList();
        if (written.Count == 0) throw new BlockError(ctx.Block, "moves", "no slot is named: leave moves out, or say which slot plays what (primary, heavy, block, ...)");
        foreach (var (slot, chain, one) in written)
        {
            var key = MoveSlots.Key(slot);
            if (!map.ContainsKey(slot))
            {
                var has = map.Count == 0 ? "none" : string.Join(", ", map.Keys.Select(MoveSlots.Key));
                throw new BlockError(ctx.Block, $"moves.{key}", $"weapon {display}: a {cls.Key} has no {key} slot. Its slots: {has}{(MoveSlots.Note(cls) is { } note ? $" ({note})" : "")}");
            }
            var list = chain?.ToList() ?? [one!];
            if (list.Count == 0) throw new BlockError(ctx.Block, $"moves.{key}", "an empty chain: give one move or several, or leave the slot out");
            if (list.Count > 16) throw new BlockError(ctx.Block, $"moves.{key}", $"{list.Count} links: a chain takes at most 16");
            for (var i = 0; i < list.Count; i++)
            {
                var mv = list[i] ?? throw new BlockError(ctx.Block, $"moves.{key}[{i}]", "a chain's link is a move, not null");
                var path = list.Count > 1 ? $"moves.{key}[{i}]" : $"moves.{key}";
                var anim = CheckMove(ctx, mv.Animation, mv.Like, mv.HitAt, mv.Length, path, blow: slot is not (MoveSlot.Block or MoveSlot.Parry));
                var suffix = list.Count > 1 ? $"{key}.{i + 1}" : key;
                var clone = $"{d.Game}.{suffix}";
                var guid = BlockIds.Derive(mod, "sequence", $"{d.Key}.{suffix}");
                // a move that names its sequence is cloned now; one that keeps the class's is cloned when content is ready, from the slot it replaces
                if (mv.Like is { } like) low.Clone("sequences", guid, clone, like.Trim(), new JsonObject());
                moves.Add(new PlannedMove(slot, path, i, list.Count, clone, guid, mv.Like?.Trim(), anim, mv.HitAt, mv.Length));
            }
        }
        var follows = m.Primary != null && m.HeavyAir == null && map.ContainsKey(MoveSlot.HeavyAir);
        var plan = new MovePlan(mod, ctx.Block, d.Game, map, moves, follows);
        low.Json["moves"] = MovePlanJson(plan);
        var json = low.Json;
        Plans.AddOrUpdate(json, plan);
        low.AfterCommit(() => BlockRuntime.AddReady(ctx.Mod, problem => MoveRuntime.Weapon(ctx.Mod, plan, json, problem)));
    }

    /// <summary>A cast's motion with moves (a wand attack's, a staff spell's): its clone is made by the caller; its states and retime wait for content ready.
    /// What it decided goes under low.Json["moves"][path].</summary>
    internal static void CastMoves(BlockContext ctx, Lowered low, string path, string clone, string like, string baseName, IReadOnlyList<AnimationRef?>? animation, double? hitAt,
        Func<EventRow, bool> damage)
    {
        var c = new JsonObject { ["clone"] = clone, ["like"] = like, ["sequence"] = baseName };
        if (AnimationJson(animation) is { } a) c["animation"] = a;
        if (hitAt != null) c["timing"] = new JsonObject { ["hitAt"] = hitAt };
        ((low.Json["moves"] ??= new JsonObject()).AsObject())[path] = c;
        var json = low.Json;
        low.AfterCommit(() => BlockRuntime.AddReady(ctx.Mod, problem => MoveRuntime.Cast(ctx.Mod, ctx.Block, path, clone, baseName, animation, hitAt, damage, json, problem)));
    }

    /// <summary>What the block decided, for tests and <c>kiln blocks</c>: the slots' selectors, the clones, the states, the timing asked for, the links.</summary>
    internal static JsonObject MovePlanJson(MovePlan plan)
    {
        var slots = new JsonObject();
        foreach (var s in plan.Moves.Select(x => x.Slot!.Value).Append(MoveSlot.HeavyAir).Distinct().Order())
        {
            if (s == MoveSlot.HeavyAir && !plan.HeavyAirFollowsPrimary && plan.Moves.All(x => x.Slot != MoveSlot.HeavyAir)) continue;
            var sel = plan.Slots[s];
            slots[MoveSlots.Key(s)] = new JsonObject
            {
                ["triggeredBy"] = sel.Trigger, ["requirements"] = new JsonArray(sel.Requirements.Select(r => (JsonNode)JsonValue.Create(r)).ToArray()), ["specialInteractionType"] = sel.Special,
                ["plays"] = s == MoveSlot.HeavyAir && plan.HeavyAirFollowsPrimary ? plan.Moves.First(x => x.Slot == MoveSlot.Primary).CloneName : plan.Moves.First(x => x.Slot == s).CloneName,
            };
        }
        var clones = new JsonArray();
        foreach (var m in plan.Moves)
        {
            var c = new JsonObject
            {
                ["slot"] = MoveSlots.Key(m.Slot!.Value), ["link"] = m.Link + 1, ["name"] = m.CloneName, ["guid"] = m.Guid.ToString(),
                ["like"] = m.Like ?? (m.Link == 0 ? "(the slot's own)" : $"(the class's chain, link {m.Link + 1})"),
            };
            if (AnimationJson(m.Animation) is { } a) c["animation"] = a;
            if (m.HitAt != null || m.Length != null) c["timing"] = new JsonObject { ["hitAt"] = m.HitAt, ["length"] = m.Length };
            c["links"] = LinkPlan(plan, m);
            clones.Add(c);
        }
        return new JsonObject { ["slots"] = slots, ["clones"] = clones };
    }

    // Where a clone's follow-ups go, by the input that triggers them: another clone, or "kept" (the motion's own).
    private static JsonObject LinkPlan(MovePlan plan, PlannedMove m)
    {
        var p = plan.Moves.Where(x => x.Slot == MoveSlot.Primary).ToList();
        var h = plan.Moves.Where(x => x.Slot == MoveSlot.Heavy).ToList();
        var o = new JsonObject();
        if (m.Slot == MoveSlot.Primary)
        {
            o["Primary"] = p[(m.Link + 1) % p.Count].CloneName;
            o["Heavy"] = h.Count > 0 ? h[0].CloneName : "kept";
        }
        else if (m.Slot == MoveSlot.Heavy)
        {
            o["Heavy"] = h.Count > 1 ? h[(m.Link + 1) % h.Count].CloneName : $"{h[0].CloneName} where the motion names itself, else kept";
            o["Primary"] = p.Count > 0 ? p[0].CloneName : "kept";
        }
        return o;
    }
}

/// <summary>What content ready makes of a plan: clones to define, rules, problems by block and field, and what it found (the report).</summary>
internal sealed class MoveWork
{
    public readonly List<JsonObject> Clones = new();
    public readonly List<(string Section, string Entry, JsonObject Rules)> Rules = new();
    public readonly List<string> Problems = new();
    public readonly JsonObject Report = new();
}

/// <summary>Content ready's part, on rows read from the game (or made up by a test): pure.</summary>
internal static class MovePlanner
{
    /// <summary>The <c>events[Type#k]</c> field a state goes into, by kind.</summary>
    public static (string Type, string Field) StateEvent(PlayerStateKind kind) =>
        kind == PlayerStateKind.State ? ("SetAnimationEvent", "animationName") : ("SetAnimationOverlayEvent", "overlayName");

    /// <summary>The states of a move, resolved: each name once, its problem once.</summary>
    public static List<(Guid Guid, PlayerStateKind? Kind)?>? States(string mod, string block, string path, IReadOnlyList<AnimationRef?>? names, IPlayerStateLookup? lookup, List<string> problems)
    {
        if (names == null) return null;
        var list = new List<(Guid, PlayerStateKind?)?>();
        for (var i = 0; i < names.Count; i++)
        {
            if (names[i] is not { } a)
            {
                list.Add(null);
                continue;
            }
            var r = MoveStates.Resolve(mod, a, lookup, out var problem);
            if (problem != null) problems.Add($"block {block}, {path}.animation{(names.Count > 1 ? $"[{i}]" : "")}: {problem}");
            list.Add(r);
        }
        return list;
    }

    /// <summary>The rules for one sub-sequence of a clone: its states, its retime, and (<paramref name="link"/>) its follow-ups.</summary>
    public static JsonObject SubRules(string block, string path, SubSequence s, List<(Guid Guid, PlayerStateKind? Kind)?>? states, double? hitAt, double? length,
        Func<EventRow, bool> damage, Func<EventRow, string?>? link, List<string> problems)
    {
        var rules = new JsonObject();
        var where = s.Index > 0 ? $" (sub-sequence {s.Index})" : "";
        if (states is { Count: > 0 } && states.Any(x => x != null))
        {
            var count = (Type: s.Events.Count(e => e.Type == "SetAnimationEvent"), Overlay: s.Events.Count(e => e.Type == "SetAnimationOverlayEvent"));
            // the kind: the first state that says one; else what the motion has, a full-body state first (a staff's FixedAnim cast has both, its cast motion is the full-body one)
            var kind = states.FirstOrDefault(x => x?.Kind != null)?.Kind ?? (count.Type > 0 ? PlayerStateKind.State : PlayerStateKind.Overlay);
            var (type, field) = StateEvent(kind);
            var have = kind == PlayerStateKind.State ? count.Type : count.Overlay;
            if (count.Type == 0 && count.Overlay == 0)
                problems.Add($"block {block}, {path}.animation: the motion{where} has no SetAnimationEvent and no SetAnimationOverlayEvent, so it names no state that could be replaced");
            else if (states.Any(x => x?.Kind is { } k && k != kind))
                problems.Add($"block {block}, {path}.animation: its states are of both kinds, full-body states and overlays: one move takes one kind");
            else if (have == 0)
                problems.Add($"block {block}, {path}.animation: the motion{where} has no {type}, so it cannot play {(kind == PlayerStateKind.State ? "a full-body state" : "an overlay")}");
            else if (states.Count > have)
                problems.Add($"block {block}, {path}.animation: {states.Count} states, and the motion{where} has {have} {type}{(have == 1 ? "" : "s")}");
            else
                for (var k = 0; k < states.Count; k++)
                    if (states[k] is { } st) rules[$"events[{type}#{k}].{field}"] = st.Guid.ToString();
        }
        if (hitAt != null || length != null)
        {
            var (map, problem) = MoveTiming.Map(s, damage, hitAt, length);
            if (map == null) problems.Add($"block {block}, {path}{(hitAt != null ? ".hitAt" : ".length")}: the motion{where} cannot be retimed: {problem}");
            else Merge(rules, MoveTiming.Rules(s, map, damage));
        }
        if (link != null)
            foreach (var e in s.Events)
                if (e.Type == "SetActionOportunity" && e.FollowUp != Guid.Empty && link(e) is { } to)
                    rules[$"{e.Path}.followUp"] = to;
        return rules;
    }

    private static void Merge(JsonObject into, JsonObject add)
    {
        foreach (var (k, v) in add) into[k] = v?.DeepClone();
    }

    private static string Entry(string clone, SubSequence s) => s.Index == 0 ? clone : $"{clone}#{s.Index}";

    /// <summary>The first follow-up of a sequence for an input: how the class's own chain goes on.</summary>
    private static Guid? Next(IReadOnlyList<SubSequence> subs, string trigger) =>
        subs[0].Events.FirstOrDefault(e => e.Type == "SetActionOportunity" && e.TriggeredBy == trigger && e.FollowUp != Guid.Empty)?.FollowUp;

    /// <summary>Phase one: the slots on the item, the sequence each move starts from, and the clones of the class's own sequences that are made now.
    /// <paramref name="read"/>: a sequence's sub-sequences by name or GUID, null when the game has none.</summary>
    public static (MoveWork Work, Dictionary<PlannedMove, IReadOnlyList<SubSequence>> Bases, Dictionary<MoveSlot, int[]> At) Find(MovePlan plan, IReadOnlyList<SlotRow>? slots,
        Func<string, IReadOnlyList<SubSequence>?> read)
    {
        var w = new MoveWork();
        var bases = new Dictionary<PlannedMove, IReadOnlyList<SubSequence>>();
        var at = new Dictionary<MoveSlot, int[]>();
        if (slots == null)
        {
            w.Problems.Add($"block {plan.Block}, moves: the game has no item \"{plan.Item}\" to give its moves to");
            return (w, bases, at);
        }
        var used = plan.Moves.Select(m => m.Slot!.Value).ToList();
        if (plan.HeavyAirFollowsPrimary) used.Add(MoveSlot.HeavyAir);
        var report = new JsonObject();
        foreach (var s in used.Distinct())
        {
            var idx = MoveSlots.Match(slots, plan.Slots[s]);
            if (idx.Length == 0)
            {
                w.Problems.Add($"block {plan.Block}, moves.{MoveSlots.Key(s)}: the item has no slot {plan.Slots[s]} (a game update?): this slot keeps its class's sequence, the others still apply");
                continue;
            }
            at[s] = idx;
            report[MoveSlots.Key(s)] = new JsonArray(idx.Select(i => (JsonNode)JsonValue.Create(i)).ToArray());
        }
        w.Report["slots"] = report;
        var cloned = new JsonObject();
        foreach (var chain in plan.Moves.GroupBy(m => m.Slot!.Value))
        {
            var trigger = chain.Key == MoveSlot.Heavy ? "Heavy" : "Primary";
            // the class's own chain, from its slot: the n-th link is its follow-up n-1 times
            var game = new List<Guid>();
            if (at.TryGetValue(chain.Key, out var first)) game.Add(slots[first[0]].Sequence);
            foreach (var m in chain.OrderBy(x => x.Link))
            {
                string? from = m.Like;
                if (from == null)
                {
                    while (game.Count > 0 && game.Count <= m.Link && read(game[^1].ToString()) is { } prev && Next(prev, trigger) is { } next && !game.Contains(next)) game.Add(next);
                    if (game.Count <= m.Link)
                    {
                        if (at.ContainsKey(chain.Key))
                            w.Problems.Add($"block {plan.Block}, {m.Path}: the class's own chain has {game.Count} link{(game.Count == 1 ? "" : "s")}, so link {m.Link + 1} has none to start from: give it a like");
                        continue;
                    }
                    from = game[m.Link].ToString();
                }
                if (read(from) is not { Count: > 0 } subs)
                {
                    w.Problems.Add($"block {plan.Block}, {m.Path}{(m.Like != null ? ".like" : "")}: the game has no sequence \"{from}\"");
                    continue;
                }
                bases[m] = subs;
                cloned[m.CloneName] = m.Like ?? subs[0].Sequence.ToString();
                if (m.Like == null)
                    w.Clones.Add(new JsonObject { ["guid"] = m.Guid.ToString(), ["name"] = m.CloneName, ["base"] = subs[0].Sequence.ToString(), ["fields"] = new JsonObject() });
            }
        }
        w.Report["bases"] = cloned;
        return (w, bases, at);
    }

    /// <summary>Phase two, once every clone is in the game: the clones' rules and the item's slots.</summary>
    public static void Apply(MovePlan plan, MoveWork w, Dictionary<PlannedMove, IReadOnlyList<SubSequence>> bases, Dictionary<MoveSlot, int[]> at, IPlayerStateLookup? lookup)
    {
        var p = plan.Moves.Where(m => m.Slot == MoveSlot.Primary && bases.ContainsKey(m)).OrderBy(m => m.Link).ToList();
        var h = plan.Moves.Where(m => m.Slot == MoveSlot.Heavy && bases.ContainsKey(m)).OrderBy(m => m.Link).ToList();
        foreach (var m in plan.Moves)
        {
            if (!bases.TryGetValue(m, out var subs)) continue;
            var states = States(plan.Mod, plan.Block, m.Path, m.Animation, lookup, w.Problems);
            var self = subs[0].Sequence;
            Func<EventRow, string?>? link = m.Slot switch
            {
                MoveSlot.Primary => e => e.TriggeredBy switch
                {
                    "Primary" => p[(p.IndexOf(m) + 1) % p.Count].CloneName,
                    "Heavy" => h.Count > 0 ? h[0].CloneName : null,
                    _ => null,
                },
                MoveSlot.Heavy => e => e.TriggeredBy switch
                {
                    "Heavy" => h.Count > 1 ? h[(h.IndexOf(m) + 1) % h.Count].CloneName : e.FollowUp == self ? m.CloneName : null,
                    "Primary" => p.Count > 0 ? p[0].CloneName : null,
                    _ => null,
                },
                _ => null,
            };
            foreach (var s in subs)
            {
                var rules = SubRules(plan.Block, m.Path, s, states, m.HitAt, m.Length, MoveTiming.Swing, link, w.Problems);
                if (rules.Count > 0) w.Rules.Add(("sequences", Entry(m.CloneName, s), rules));
            }
        }
        var items = new JsonObject();
        foreach (var (slot, idx) in at)
        {
            var plays = slot == MoveSlot.HeavyAir && plan.HeavyAirFollowsPrimary ? p.FirstOrDefault()
                : plan.Moves.FirstOrDefault(m => m.Slot == slot && m.Link == 0 && bases.ContainsKey(m));
            if (plays == null) continue;
            foreach (var i in idx) items[$"sequences[{i}].sequence"] = plays.CloneName;
        }
        if (items.Count > 0) w.Rules.Add(("itemdata", plan.Item, items));
    }

    /// <summary>A cast's or a wand attack's motion: a clone made at compile, with its states and retime (no slots, no links).
    /// <paramref name="damage"/>: the events whose moment hitAt is (the shots).</summary>
    public static MoveWork Cast(string mod, string block, string path, string clone, IReadOnlyList<SubSequence>? subs, IReadOnlyList<AnimationRef?>? animation, double? hitAt,
        Func<EventRow, bool> damage, IPlayerStateLookup? lookup)
    {
        var w = new MoveWork();
        if (subs is not { Count: > 0 })
        {
            w.Problems.Add($"block {block}, {path}: the game has no sequence for {clone} to start from");
            return w;
        }
        var states = States(mod, block, path, animation, lookup, w.Problems);
        foreach (var s in subs)
        {
            var rules = SubRules(block, path, s, states, hitAt, null, damage, null, w.Problems);
            if (rules.Count > 0) w.Rules.Add(("sequences", Entry(clone, s), rules));
        }
        return w;
    }
}
