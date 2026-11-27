// SPDX-License-Identifier: MIT
// Creature and NPC blocks -> what the services take, and the agreement between their parts, checked here because the game checks none of it:
//   a body of its own   a template clone of the AI's creature (kiln.gamedata) whose graph and model are the cooked ones, with its state fields, size and
//                       collision; a game body stays the game's template
//   every creature      a kiln.creatures variant (level, faction, scale, features, AI values, arsenal) and its spawn rules
//   BehaviourBlock      an arsenal (kiln.creatures' writer): attacks with a generated sequence each (a clone of a game attack whose state is the attack's, and
//                       whose spawn event places the hit), rules with Kiln's kinds
//   Brain               the game's AI off for each creature, NpcRuntime drives it
//   NpcBlock            the quests' NPC (QuestNpcDefinition, DialogueDefinition: as they are), vendor recipes, and its roster entry for the boot build
// docs/kiln/modding/npc-blocks.md.
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

internal static partial class Compiler
{
    /// <summary>A creature, NPC or one of their parts, or null for another kind of block.</summary>
    internal static BlockHandle? DefineNpcBlock(BlockContext ctx, Lowered low, string name, IBlock block) =>
        block is INpcBlock ? NpcCompiler.Define(ctx, low, name, block) : null;
}

internal static class NpcCompiler
{
    // The game attack every generated attack is a clone of: a SetAnimationEvent at 0, a spawn event, turning to the target, nothing that names a state but its own.
    internal const string AttackBase = "Enemy_Vukah_Melee_Attack_ThrowStone";
    private const double AttackBaseLength = 1.55;
    private const string AiDefault = "Enemy_Vukah_Melee";
    private const string GraphType = "keen::anim_graph::runtime_graph::AnimationGraphResource2_0";
    // The game's motions a base creature plays as its own sequences, in states a graph of yours doesn't have: off on a creature with its own animation.
    private const EnemyFeatures OwnStatesOff = EnemyFeatures.UseTaunt | EnemyFeatures.UseBlocking | EnemyFeatures.ShowSpawnSequence | EnemyFeatures.CallForHelp | EnemyFeatures.IdleTurn | EnemyFeatures.Sleep;

    public static BlockHandle Define(BlockContext ctx, Lowered low, string name, IBlock b)
    {
        var mod = ctx.Mod.Id;
        switch (b)
        {
            case BodyBlock body:
                CheckBody(ctx, Liked(ctx, body, null), "", name);
                NpcPhysics.WarnHeld(ctx, low, body);
                low.Json["body"] = name;
                return new BlockHandle<BodyBlock>(mod, name, BlockIds.Derive(mod, "body", name), $"{mod}.{name}", body);
            case NpcMotionsBlock motions:
                NpcBodyRig.Shape(ctx, motions, "");
                low.Json["npcMotions"] = name;
                return new BlockHandle<NpcMotionsBlock>(mod, name, BlockIds.Derive(mod, "npcmotions", name), $"{mod}.{name}", motions);
            case AnimationBlock anim:
                CheckAnimation(ctx, Liked(ctx, anim, null), "");
                low.Json["animation"] = name;
                return new BlockHandle<AnimationBlock>(mod, name, BlockIds.Derive(mod, "graph", name), $"{mod}.{name}", anim);
            case AIBlock ai:
                CheckAi(ctx, Liked(ctx, ai, null), "");
                low.Json["ai"] = name;
                return new BlockHandle<AIBlock>(mod, name, BlockIds.Derive(mod, "ai", name), $"{mod}.{name}", ai);
            case BehaviourBlock bh:
                CheckBehaviour(ctx, Liked(ctx, bh, null), "", used: false);
                low.Json["behaviour"] = name;
                return new BlockHandle<BehaviourBlock>(mod, name, BlockIds.Derive(mod, "behaviour", name), $"{mod}.{name}", bh);
            case AttackBlock at:
                CheckAttack(ctx, at, "", 0);
                low.Json["attack"] = name;
                return new BlockHandle<AttackBlock>(mod, name, RuleIds.Derive(mod, "attack", at.Name ?? name), at.Name ?? name, at);
            case RuleBlock r:
                CheckRule(ctx, r, "", used: false);
                low.Json["rule"] = name;
                return new BlockHandle<RuleBlock>(mod, name, RuleIds.Derive(mod, "rule", r.Name ?? name), r.Name ?? name, r);
            case CreatureBlock c:
                return DefineCreature(ctx, low, name, c);
            case NpcBlock n:
                return DefineNpc(ctx, low, name, n);
        }
        throw new BlockError(ctx.Block, null, $"{b.GetType().Name} is not a block Kiln can compile yet");
    }

    // ---- parts --------------------------------------------------------------------------------------------------

    /// <summary>A part as written: the block, the mod whose files it names, and its block name (an inline one: "&lt;owner&gt;.&lt;field&gt;"; a premade: null).</summary>
    private sealed record Part<T>(T Block, string Mod, string? Name) where T : class, IBlock;

    private static Part<T> PartOf<T>(BlockContext ctx, PartRef<T>? r, T? fallback, string owner, string field) where T : class, IBlock
    {
        if (r == null)
            return fallback != null ? new Part<T>(fallback, ctx.Mod.Id, null) : throw new BlockError(ctx.Block, field, $"needs a {KindOf(typeof(T))}");
        var block = ctx.Part(r, field);
        string? name = null;
        var mod = ctx.Mod.Id;
        if (r.Name is { } n && !IsPremade<T>(n))
        {
            var colon = n.IndexOf(':');
            if (colon > 0) (mod, name) = (n[..colon], n[(colon + 1)..]);
            else name = n;
        }
        else if (r.Name == null) name = $"{owner}.{field}";
        return new Part<T>(Liked(ctx, block, field), mod, name);
    }

    private static bool IsPremade<T>(string key) =>
        (typeof(T).GetField("All", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as Array)?.Cast<object>().OfType<IPremade>().Any(p => p.Key == key) == true;

    private static string KindOf(Type t) => t.Name.Replace("Block", "").ToLowerInvariant();

    /// <summary>A block with <c>Like</c> is that premade with the block's own values on top.</summary>
    internal static T Liked<T>(BlockContext ctx, T block, string? path) where T : class, IBlock
    {
        var like = typeof(T).GetProperty("Like")?.GetValue(block) as string;
        if (like == null) return block;
        var all = (typeof(T).GetField("All", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as Array)?.Cast<T>().ToList() ?? [];
        var start = all.FirstOrDefault(p => ((IPremade)p).Key == like)
                    ?? throw new BlockError(ctx.Block, Join(path, "like"), $"\"{like}\" is not a {KindOf(typeof(T))} premade; there are {string.Join(", ", all.Select(p => ((IPremade)p).Key))}");
        var clone = (T)typeof(T).GetMethod("<Clone>$")!.Invoke(start, null)!;
        foreach (var p in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanWrite || p.Name is "Key" or "Like") continue;
            var v = p.GetValue(block);
            if (v == null || v is System.Collections.ICollection { Count: 0 }) continue;
            p.SetValue(clone, v);
        }
        return clone;
    }

    private static string Join(string? path, string field) => string.IsNullOrEmpty(path) ? field : $"{path}.{field}";

    // ---- checks of each part on its own ---------------------------------------------------------------------------

    private static void CheckBody(BlockContext ctx, BodyBlock b, string path, string? name)
    {
        if (b.Template == null && b.Model == null) throw new BlockError(ctx.Block, Join(path, "model"), "a body is a model of the mod (a glTF file) or a game creature's: say model, or start from a premade (like)");
        if (b.Template != null && b.Model != null) throw new BlockError(ctx.Block, Join(path, "model"), $"a body is the game's {b.Template} or a model of yours, not both");
        if (b.Scale is <= 0 or > 20) throw new BlockError(ctx.Block, Join(path, "scale"), $"{b.Scale} must be more than 0 and at most 20");
        if (b.Model != null)
        {
            CheckFile(ctx, b.Model, Join(path, "model"));
            if (b.Size is { } s && (s.Radius < 0 || s.Height <= 0)) throw new BlockError(ctx.Block, Join(path, "size"), "a radius of 0 or more and a height above 0");
        }
        else if (b.Colliders != null || b.Slots != null || b.Size != null)
            throw new BlockError(ctx.Block, Join(path, b.Colliders != null ? "colliders" : b.Slots != null ? "slots" : "size"), $"a game body has its own; this is for a model of yours");
        foreach (var (c, i) in (b.Colliders ?? []).Select((c, i) => (c, i)))
        {
            if (c.Shape is not ("capsule" or "sphere")) throw new BlockError(ctx.Block, Join(path, $"colliders[{i}].shape"), $"\"{c.Shape}\": capsule or sphere");
            if (c.Radius <= 0 || c.Height < c.Radius * 2 && c.Shape == "capsule") throw new BlockError(ctx.Block, Join(path, $"colliders[{i}]"), "a radius above 0, a height of 0 or more, and a capsule at least as high as it is wide");
            // a capsule below 0 is refused above (under twice its radius); a sphere's height is not used (lowered: length 0, offset its radius)
            if (c.Height < 0) ctx.Tolerate(Join(path, $"colliders[{i}].height"), $"{c.Height} must be at least 0; a sphere does not use its height, so it is ignored");
        }
        if (b.Colliders is { Count: > 2 }) throw new BlockError(ctx.Block, Join(path, "colliders"), "at most two: one for hits, one for what it bumps into");
        NpcPhysics.Check(ctx, b, path);
    }

    private static void CheckFile(BlockContext ctx, string file, string path)
    {
        // rooted as the game's Windows runtime sees it, whatever this process runs on: /, \ or a drive (C:)
        var rooted = file.StartsWith('/') || file.StartsWith('\\') || (file.Length >= 2 && file[1] == ':' && char.IsAsciiLetter(file[0])) || Path.IsPathRooted(file);
        if (rooted || file.Contains("..", StringComparison.Ordinal)) throw new BlockError(ctx.Block, path, $"\"{file}\": a path in the mod's folder");
        if (!file.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase))
            throw new BlockError(ctx.Block, path, $"\"{file}\" is not a glTF file (.glb or .gltf)");
        var dir = ctx.Mod.DirectoryOf(ctx.Mod.Id);
        if (dir.Length > 0 && !File.Exists(Path.Combine(dir, file))) throw new BlockError(ctx.Block, path, $"no file {file} in the mod's folder");
    }

    private static IEnumerable<(string Field, Clip Clip)> EngineClips(AnimationBlock a) =>
        new (string, Clip?)[] { ("Idle", a.Idle), ("Walk", a.Walk), ("Run", a.Run), ("Sprint", a.Sprint), ("IdleCombat", a.IdleCombat), ("Fall", a.Fall), ("IdleSneak", a.IdleSneak), ("Sneak", a.Sneak), ("Dead", a.Dead), ("Stun", a.Stun) }
            .Where(x => x.Item2 != null).Select(x => (x.Item1, x.Item2!));

    private static void CheckAnimation(BlockContext ctx, AnimationBlock a, string path)
    {
        if (a.Template != null && a.Clips != null) throw new BlockError(ctx.Block, Join(path, "clips"), $"an animation is the game's graph of {a.Template} or clips of yours, not both");
        if (a.Template == null && a.Clips == null) throw new BlockError(ctx.Block, Join(path, "clips"), "an animation is a glTF of the mod with its clips, or a game creature's: say clips, or start from a premade (like)");
        if (a.Template != null)
        {
            if (EngineClips(a).Any() || a.States.Count > 0 || a.Hit != null || a.Layers is { Count: > 0 })
                throw new BlockError(ctx.Block, Join(path, "states"), $"the game's graph of {a.Template} is as it is: states of your own need clips of your own");
            if (a.Stagger != null) ctx.Tolerate(Join(path, "stagger"), $"the game's graph of {a.Template} is as it is: its own stagger plays, and this one is ignored");
            return;
        }
        CheckFile(ctx, a.Clips!, Join(path, "clips"));
        Retargets.Check(ctx, a, path);
        if (a.Idle == null) throw new BlockError(ctx.Block, Join(path, "idle"), "every graph needs the engine's Idle: the clip it stands in");
        foreach (var (state, clip) in a.States)
        {
            if (state.EngineName is { } e) throw new BlockError(ctx.Block, Join(path, $"states[{state}]"), $"the engine's {e} is a field of its own: {e} = ...");
            if (state.Name == "Stagger") throw new BlockError(ctx.Block, Join(path, "states[Stagger]"), "Stagger is a field of its own: Stagger = ...");
            if (state.IsGame) throw new BlockError(ctx.Block, Join(path, $"states[{state}]"), "a state of your own has a name; ids are the game's graphs'");
            CheckClip(ctx, clip, Join(path, $"states[{state}]"));
        }
        foreach (var (field, clip) in EngineClips(a)) CheckClip(ctx, clip, Join(path, field.ToLowerInvariant()));
        var names = a.States.Keys.Select(k => k.Name).Concat(EngineClips(a).Select(x => x.Field)).ToHashSet(StringComparer.Ordinal);
        foreach (var (state, clip) in a.States.Select(x => (x.Key.Name, x.Value)).Concat(EngineClips(a)))
            if (clip.Then is { } then && !names.Contains(then.ToString()))
                throw new BlockError(ctx.Block, Join(path, $"states[{state}].then"), $"\"{then}\" is not a state of this animation; it has {string.Join(", ", names.Order())}");
        if (a.Hit is { } hit) CheckClip(ctx, hit, Join(path, "hit"));
        if (a.Hit is { Blend: not null }) throw new BlockError(ctx.Block, Join(path, "hit"), "a clip, not a blend");
        if (a.Stagger is { } stg) CheckClip(ctx, stg, Join(path, "stagger"));
    }

    private static void CheckClip(BlockContext ctx, Clip c, string path)
    {
        if (c.Name == null && c.Blend == null) throw new BlockError(ctx.Block, path, "a glTF animation's name, or a blend");
        if (c.Name is { Length: 0 }) ctx.Tolerate(path, "a glTF animation's name, or a blend; no glTF animation has an empty name, so the cook makes no clip of it");
        if (c.BlendIn is < 0) ctx.Tolerate($"{path}.blendIn", $"{c.BlendIn} must be at least 0; the cook writes it into the graph as written");
        // refused on every clip, also a hit clip (0.2.0-dev.30.6 loaded one): at 0 or below the clip never ends, and the hit layer leaves its
        // play state only at the clip's end, so after the first hit the creature would stay in that pose
        if (c.Speed is <= 0) throw new BlockError(ctx.Block, $"{path}.speed", $"{c.Speed} must be more than 0");
        if (c.Blend is { } b)
        {
            if (b.Clips.Count < 2) throw new BlockError(ctx.Block, $"{path}.blend.clips", "a blend mixes two animations or more");
            foreach (var k in b.Clips.Keys)
                // refused also when not finite (0.2.0-dev.30.6 loaded "NaN" and "1e999"): the cook wrote it into the game's blend space, NaN weights
                if (!double.TryParse(k, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var point) || !double.IsFinite(point))
                    throw new BlockError(ctx.Block, $"{path}.blend.clips", $"\"{k}\" is not a number: the input's value where that animation plays");
        }
    }

    private static void CheckAi(BlockContext ctx, AIBlock a, string path)
    {
        foreach (var (field, v) in new[] { ("sight", a.Sight), ("aggro", a.Aggro), ("leash", a.Leash) })
            if (v is < 0 or > 1000) throw new BlockError(ctx.Block, Join(path, field), $"{v} is outside 0..1000 metres");
        // 0.2.0-dev.30.6 took 0 to 1000 and used it as written (0: a decision at every AI update), and so does this release
        if (a.PickEvery is < 0 or > 1000) throw new BlockError(ctx.Block, Join(path, "pickEvery"), $"{a.PickEvery} must be more than 0 and at most 60 seconds");
        if (a.PickEvery is 0 or > 60) ctx.Tolerate(Join(path, "pickEvery"), $"{a.PickEvery} must be more than 0 and at most 60 seconds; it is used as written{(a.PickEvery == 0 ? ": a decision at every AI update" : "")}");
        if (a.SightAngle is < 0 or > 180) throw new BlockError(ctx.Block, Join(path, "sightAngle"), $"{a.SightAngle} is outside 0..180 degrees");
        if (a.PickTargetName is { } n && NpcRuntime.Lookup(NpcRuntime.Targets, n) == null)
            throw new BlockError(ctx.Block, Join(path, "pickTarget"), $"no target picker \"{n}\": register it with Blocks.Target(\"{n}\", npc => ...) in OnLoad");
        if (a.Template != null && Creatures.Available && Creatures.Template(a.Template) is null)
            throw new BlockError(ctx.Block, Join(path, "template"), $"the game has no creature \"{a.Template}\"");
        AiLowering.Check(ctx, a, path);   // npcai's fields (Ai/AiLowering.cs)
    }

    /// <param name="ctx">The block being compiled.</param>
    /// <param name="b">The behaviour.</param>
    /// <param name="path">Where it is in the block, for errors.</param>
    /// <param name="used">A creature's behaviour (its rules are checked as it lowers them), or one on its own: its rules only warn, as
    /// 0.2.0-dev.30.6 checked them only where a creature used it.</param>
    private static void CheckBehaviour(BlockContext ctx, BehaviourBlock b, string path, bool used)
    {
        if (b.Brain != null && b.BrainName != null) throw new BlockError(ctx.Block, Join(path, "brain"), "a brain from code or by name, not both");
        if (b.BrainName is { } bn && NpcRuntime.Lookup(NpcRuntime.Brains, bn) == null)
            throw new BlockError(ctx.Block, Join(path, "brain"), $"no brain \"{bn}\": register it with Blocks.Brain(\"{bn}\", () => new MyBrain()) in OnLoad");
        if (b.PickAttackName is { } pn && NpcRuntime.Lookup(NpcRuntime.AttackPicks, pn) == null)
            throw new BlockError(ctx.Block, Join(path, "pickAttack"), $"no attack picker \"{pn}\": register it with Blocks.AttackPicker(\"{pn}\", npc => ...) in OnLoad");
        if (b.PickEvery is <= 0 or > 60) throw new BlockError(ctx.Block, Join(path, "pickEvery"), $"{b.PickEvery} must be more than 0 and at most 60 seconds");
        var attacks = b.Attacks ?? [];
        if (attacks.Count > 32) throw new BlockError(ctx.Block, Join(path, "attacks"), $"{attacks.Count} attacks: an arsenal holds 32");
        if ((b.Rules?.Count ?? 0) > 32) throw new BlockError(ctx.Block, Join(path, "rules"), $"{b.Rules!.Count} rules: an arsenal holds 32");
        foreach (var (rule, i) in (b.Rules ?? []).Select((r, i) => (r, i)))
        {
            var at = Join(path, $"rules[{i}]");
            if (used) CheckRule(ctx, ctx.Part(rule, at), at, used: true);
            else
                try { CheckRule(ctx, ctx.Part(rule, at), at, used: false); }
                catch (BlockError e) { ctx.Tolerate(e.Field ?? at, $"{e.Problem}{UnusedRule}"); }
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < attacks.Count; i++)
        {
            var a = ctx.Part(attacks[i], Join(path, $"attacks[{i}]"));
            CheckAttack(ctx, a, Join(path, $"attacks[{i}]"), i);
            if (!names.Add(a.Name ?? $"attack{i}")) throw new BlockError(ctx.Block, Join(path, $"attacks[{i}].name"), $"two attacks are called {a.Name}");
        }
    }

    private static void CheckAttack(BlockContext ctx, AttackBlock a, string path, int index)
    {
        if (a.Name is { } n && (n.Length == 0 || n.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.')))
            throw new BlockError(ctx.Block, Join(path, "name"), "letters, digits, '-', '_' and '.'");
        if (a.Range <= 0 || a.Range > 200) throw new BlockError(ctx.Block, Join(path, "range"), $"{a.Range} must be more than 0 and at most 200 metres");
        if (a.MinRange is { } min && (min < 0 || min >= a.Range)) throw new BlockError(ctx.Block, Join(path, "minRange"), $"{min} must be at least 0 and less than its range {a.Range}");
        if (a.Cooldown is < 0) throw new BlockError(ctx.Block, Join(path, "cooldown"), $"{a.Cooldown} is less than 0");
        if (a.Chance is < 0 or > 1) throw new BlockError(ctx.Block, Join(path, "chance"), $"{a.Chance} is outside 0..1");
        // NaN fails every comparison: written this way it is refused, not taken for a full circle
        if (!(a.Angle > 0 && a.Angle <= 360)) throw new BlockError(ctx.Block, Join(path, "angle"), $"{a.Angle} must be more than 0 and at most 360 degrees");
        if (a.Sequence == null)
        {
            if (a.Length is <= 0 or > 30) throw new BlockError(ctx.Block, Join(path, "length"), $"{a.Length} must be more than 0 and at most 30 seconds");
            if (a.HitAt < 0) throw new BlockError(ctx.Block, Join(path, "hitAt"), $"{a.HitAt} must be at least 0 seconds");
            if (a.Length is { } len && a.HitAt >= len) throw new BlockError(ctx.Block, Join(path, "hitAt"), $"{a.HitAt} must be less than the attack's length {len} seconds");
        }
        else
        {
            if (a.Plays != null || a.OnHit != null || a.Delivery != null) throw new BlockError(ctx.Block, Join(path, "sequence"), "a game sequence plays as it is: no plays, onHit or delivery with it");
            // 0.2.0-dev.30.6 returned here before checking them: a game sequence plays as it is and uses neither
            const string unused = "; a game sequence plays as it is, so it is ignored";
            if (a.Length is <= 0 or > 30) ctx.Tolerate(Join(path, "length"), $"{a.Length} must be more than 0 and at most 30 seconds{unused}");
            if (a.HitAt < 0) ctx.Tolerate(Join(path, "hitAt"), $"{a.HitAt} must be at least 0 seconds{unused}");
            else if (a.Length is { } slen && a.HitAt >= slen) ctx.Tolerate(Join(path, "hitAt"), $"{a.HitAt} must be less than the attack's length {slen} seconds{unused}");
            return;
        }
        if (a.Plays == null) throw new BlockError(ctx.Block, Join(path, "plays"), "the state the attack plays: one of the animation's");
        if (a.OnHit is not { Count: > 0 } && a.Delivery == null) throw new BlockError(ctx.Block, Join(path, "onHit"), "an attack that does nothing: give it onHit or a delivery");
        if (a.OnHit is { Count: > 0 } && a.Delivery != null) throw new BlockError(ctx.Block, Join(path, "delivery"), "a delivery carries its own effects: onHit is for a blow");
        if (a.OnHit != null && a.Delivery != null) ctx.Tolerate(Join(path, "onHit"), "a delivery carries its own effects: onHit is for a blow; this empty one is ignored");
    }

    private const string UnusedRule = "; a rule is checked where a creature's behaviour uses it, and that creature is refused";

    /// <param name="ctx">The block being compiled.</param>
    /// <param name="r">The rule.</param>
    /// <param name="path">Where it is in the block, for errors.</param>
    /// <param name="used">A creature lowers it: wrong values refuse the creature. A rule (or behaviour) block on its own only warns, as
    /// 0.2.0-dev.30.6 checked a rule only where a creature used it.</param>
    private static void CheckRule(BlockContext ctx, RuleBlock r, string path, bool used)
    {
        void Bad(string field, string problem)
        {
            if (used) throw new BlockError(ctx.Block, Join(path, field), problem);
            ctx.Tolerate(Join(path, field), problem + UnusedRule);
        }
        if (r.Every <= 0 || r.Every > 3600) Bad("every", $"{r.Every} must be more than 0 and at most 3600 seconds");
        if (r.Cooldown < 0) Bad("cooldown", $"{r.Cooldown} is less than 0");
        if (r.Chance is < 0 or > 1) Bad("chance", $"{r.Chance} is outside 0..1");
        if (r.Do.Count == 0) Bad("do", "a rule that does nothing");
    }

    // ---- cooked files -----------------------------------------------------------------------------------------------

    /// <summary>What the cook made of an animation for one body: the graph's GUID, skeleton and states (name -> state input value and GUID).</summary>
    internal sealed record Graph(string Name, Guid Guid, Guid Skeleton, Dictionary<string, (uint Hash, Guid? Guid, string? Engine)> States, Dictionary<string, int> Slots, bool Game);

    private static string Cooked(BlockContext ctx, string mod) => Path.Combine(ctx.Mod.DirectoryOf(mod), "assets", "cooked");

    private static JsonNode? ReadJson(string file)
    {
        try { return File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) : null; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private static Graph CookedGraph(BlockContext ctx, string mod, string graph, string path, string file)
    {
        var j = ReadJson(Path.Combine(Cooked(ctx, mod), $"{graph}.graph.json"))
                ?? throw new BlockError(ctx.Block, path, $"its graph {graph} is not cooked: an animation with clips is cooked from {file} when the mod builds (kiln build, or kiln cook in the mod's folder). An animation written in C# is not seen by the cook: put it in blocks/*.json and name it");
        var states = new Dictionary<string, (uint, Guid?, string?)>(StringComparer.Ordinal);
        foreach (var (k, v) in j["states"]?.AsObject() ?? [])
        {
            var id = Convert.ToUInt32(v!["id"]!.GetValue<string>()[2..], 16);
            states[k] = (id, Guid.TryParse(v["guid"]?.GetValue<string>(), out var g) ? g : null, v["engine"]?.GetValue<string>());
        }
        var slots = (j["slots"]?.AsObject() ?? []).ToDictionary(x => x.Key, x => x.Value!.GetValue<int>());
        return new Graph(graph, Guid.Parse(j["guid"]!.GetValue<string>()), Guid.Parse(j["skeleton"]!.GetValue<string>()), states, slots, false);
    }

    /// <summary>A game creature's graph: its GUID from the creature catalog, the state ids it compares against read from the resource (<c>usedInputIds</c>, a
    /// superset of its states). States unknown when the resource isn't readable here.</summary>
    private static Graph? GameGraph(string template)
    {
        if (!Creatures.Available || Creatures.Template(template) is not { } t || t.AnimationGraph == Guid.Empty) return null;
        var states = new Dictionary<string, (uint, Guid?, string?)>(StringComparer.Ordinal);
        try
        {
            if (Assets.Available && Assets.ReadResource(new AssetKey(t.AnimationGraph, Assets.TypeHash(GraphType), 0)) is { Length: >= 0x30 } raw)
            {
                // usedInputIds: a BlobArray<u32> at +0x28 (relative offset from the field, count)
                var rel = BitConverter.ToInt32(raw, 0x28);
                var count = BitConverter.ToInt32(raw, 0x2c);
                var at = 0x28 + rel;
                for (var i = 0; i < count && at + 4 * i + 4 <= raw.Length; i++)
                {
                    var h = BitConverter.ToUInt32(raw, at + 4 * i);
                    var engine = AnimState.Engine.FirstOrDefault(e => e.Hash == h).Name;
                    states[engine ?? $"0x{h:x8}"] = (h, null, engine);
                }
            }
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException) { }
        return new Graph(template, t.AnimationGraph, Guid.Empty, states, [], true);
    }

    private static (Guid Guid, Guid Skeleton, JsonNode? Box) CookedModel(BlockContext ctx, string mod, string model, string path, string file, BodyBlock? body = null)
    {
        var stem = model.Replace('/', '_');
        var j = ReadJson(Path.Combine(Cooked(ctx, mod), $"{stem}.model.json"))
                ?? throw new BlockError(ctx.Block, path, $"its model {model} is not cooked: a body with a model is cooked from {file} when the mod builds (kiln build, or kiln cook in the mod's folder). A body written in C# is not seen by the cook: put it in blocks/*.json and name it");
        if (body != null) NpcPhysics.CheckCooked(ctx, body, j, model, "body");
        return (Guid.Parse(j["guid"]!.GetValue<string>()), Guid.Parse(j["skeleton"]?.GetValue<string>() ?? Guid.Empty.ToString()), j["boundingBox"]);
    }

    /// <summary>On a dedicated server whose cook skipped the renderer (no <c>&lt;stem&gt;.model.json</c>): the model's GUID from the body
    /// manifest assets/cooked/npc-bodies.v1.json, its <c>keen::RenderModel</c> row for <c>&lt;stem&gt;.bin</c> marked
    /// <c>skippedOnServer</c>. Null on a game client, with the sidecar there, or without such a row (then the sidecar is missing).</summary>
    private static Guid? ServerModel(BlockContext ctx, string mod, string model)
    {
        if (NpcServerSide.IsClient()) return null;
        var stem = model.Replace('/', '_');
        var dir = Cooked(ctx, mod);
        if (File.Exists(Path.Combine(dir, $"{stem}.model.json"))) return null;
        if (ReadJson(Path.Combine(dir, "npc-bodies.v1.json"))?["entries"] is not JsonArray entries) return null;
        foreach (var e in entries)
        {
            try
            {
                if (e is JsonObject row && (string?)row["type"] == "keen::RenderModel" && (bool?)row["skippedOnServer"] == true &&
                    ((string?)row["file"])?.Replace('\\', '/').EndsWith($"/{stem}.bin", StringComparison.Ordinal) == true &&
                    Guid.TryParse((string?)row["guid"], out var guid) && guid != Guid.Empty)
                    return guid;
            }
            catch (Exception x) when (x is InvalidOperationException or FormatException) { }  // a field of another type: not this row
        }
        return null;
    }

    private static string ModelName(string file) => file.Contains('.') ? file[..file.LastIndexOf('.')].Replace('\\', '/') : file;
    private static string Stem(string file) => Path.GetFileNameWithoutExtension(file);

    /// <summary>A clip's length in seconds, as the cook made it (clips.json), or null.</summary>
    private static double? ClipLength(BlockContext ctx, string mod, string skeleton, Clip c, bool loopDefault)
    {
        if (c.Name == null) return null;
        var key = $"{skeleton}.{c.Name}" + ((c.Loop ?? loopDefault) ? "" : ".once");
        if (c.RootMotion == true) key += ".rm";
        var d = ReadJson(Path.Combine(Cooked(ctx, mod), "clips.json"))?["clips"]?[key]?["duration"]?.GetValue<double>();
        return d / (c.Speed ?? 1);
    }

    // ---- creatures --------------------------------------------------------------------------------------------------

    private sealed record Parts(Part<BodyBlock> Body, Part<AnimationBlock> Anim, Part<AIBlock> Ai, Part<BehaviourBlock> Behaviour);

    private static Parts Resolve(BlockContext ctx, string name, PartRef<BodyBlock> bodyRef, PartRef<AnimationBlock>? animRef, PartRef<AIBlock>? aiRef, PartRef<BehaviourBlock>? bhRef)
    {
        var body = PartOf<BodyBlock>(ctx, bodyRef, null, name, "body");
        CheckBody(ctx, body.Block, "body", body.Name);
        var game = body.Block.Template;
        var anim = PartOf(ctx, animRef, game != null ? Animations.All.FirstOrDefault(a => a.Template == game) ?? new AnimationBlock { Template = game } : null, name, "animation");
        CheckAnimation(ctx, anim.Block, "animation");
        var ai = PartOf(ctx, aiRef, new AIBlock { Template = game ?? AiDefault }, name, "ai");
        CheckAi(ctx, ai.Block, "ai");
        var bh = PartOf(ctx, bhRef, game != null ? new BehaviourBlock { Template = game } : Behaviours.None, name, "behaviour");
        CheckBehaviour(ctx, bh.Block, "behaviour", used: true);
        return new Parts(body, anim, ai, bh);
    }

    private static BlockHandle DefineCreature(BlockContext ctx, Lowered low, string name, CreatureBlock c)
    {
        var mod = ctx.Mod.Id;
        var key = c.Was ?? name;
        var p = Resolve(ctx, name, c.Body, c.Animation, c.AI, c.Behaviour);
        var (body, anim, ai, bh) = (p.Body.Block, p.Anim.Block, p.Ai.Block, p.Behaviour.Block);
        NpcPhysics.WarnHeld(ctx, low, body);
        var own = body.Model != null || anim.Clips != null;
        if (body.Template != null && anim.Clips != null)
            throw new BlockError(ctx.Block, "animation", $"the game's {body.Template} is posed by its own skeleton, and this animation has a skeleton of its own: give the creature a body of its own on that skeleton");
        if (body.Model != null && anim.Template != null)
            throw new BlockError(ctx.Block, "animation", $"a body of your own is posed by its own skeleton, and {anim.Template}'s graph is for another: give it an animation of its own");
        if (body.Template != null && anim.Template != null && anim.Template != body.Template && GameGraph(anim.Template)?.Guid != GameGraph(body.Template)?.Guid)
            throw new BlockError(ctx.Block, "animation", $"{anim.Template}'s graph is not {body.Template}'s: a game body plays its own");
        if (own) ctx.Need(ClientsLevel.Mod, body.Model != null ? "body.model" : "animation.clips", "a body or an animation of its own has to be on every player's game: the template, model, skeleton, clips and graph are read at start");

        // the graph and what it answers to
        Graph graph;
        Guid model = Guid.Empty;
        JsonNode? box = null;
        if (own)
        {
            if (p.Anim.Name == null || p.Body.Name == null) throw new BlockError(ctx.Block, "animation", "a body and an animation of your own are blocks of this mod");
            var skeleton = anim.Skeleton ?? Stem(anim.Clips!);
            var bodySkeleton = body.Skeleton ?? Stem(body.Model!);
            NpcBodyRig.SkeletonNames(ctx, bodySkeleton, skeleton);
            var graphName = body.Slots is { Count: > 0 } ? $"{p.Anim.Name}.{p.Body.Name}" : p.Anim.Name;
            graph = CookedGraph(ctx, p.Anim.Mod, graphName, "animation", "blocks/*.json");
            (model, var modelSkeleton, box) = CookedModel(ctx, p.Body.Mod, ModelName(body.Model!), "body.model", "blocks/*.json", body);
            NpcBodyRig.Cooked(ctx, body, graph, modelSkeleton);
        }
        else
        {
            graph = GameGraph(anim.Template!) ?? new Graph(anim.Template!, Guid.Empty, Guid.Empty, [], [], true);
        }

        // what the AI and the behaviour ask of the animation
        var moves = ai.Moves != false;
        if (own)
        {
            foreach (var need in moves ? new[] { "Walk", "Run" } : [])
                if (!graph.States.Values.Any(s => s.Engine == need))
                    throw new BlockError(ctx.Block, $"animation.{need.ToLowerInvariant()}", $"a creature that moves plays the engine's {need}; this animation has none. Give it one, or ai.moves = false");
            if (bh.Template != null)
                throw new BlockError(ctx.Block, "behaviour", $"behaviour {p.Behaviour.Name ?? ((IPremade)bh).Key} keeps the attacks of {bh.Template}, whose sequences play that creature's states; animation {p.Anim.Name} has none of them. Give it attacks of its own (Behaviours.None with attacks)");
        }
        var states = graph.States;
        string StateList() => string.Join(", ", states.Keys.Order());
        var attacks = (bh.Attacks ?? []).Select((a, i) => (Block: ctx.Part(a, $"behaviour.attacks[{i}]"), Index: i)).ToList();
        foreach (var (a, i) in attacks)
        {
            if (a.Plays is not { } plays) continue;
            var path = $"behaviour.attacks[{i}].plays";
            if (states.Count == 0)
            {
                if (!plays.IsGame && plays.EngineName == null) throw new BlockError(ctx.Block, path, $"\"{plays}\" is a state of this mod's, and {graph.Name}'s graph is the game's: name one of its states by id");
                continue;  // a game graph not readable here: the game's own ids, taken as they are
            }
            var hash = plays.Hash(own ? p.Anim.Mod : mod);
            if (!states.Values.Any(s => s.Hash == hash))
                throw new BlockError(ctx.Block, path, $"\"{plays}\" is not a state of {(own ? $"animation {p.Anim.Name}" : $"{graph.Name}'s graph")}; it has {StateList()}. A sequence that sets a state its graph lacks plays nothing, and the hit that follows the clip never comes");
            if (plays.Guid(own ? p.Anim.Mod : mod) == null)
                throw new BlockError(ctx.Block, path, $"a sequence names a state by GUID, and the engine's {plays} has none known: play a state of your own");
        }
        var phases = bh.Phases?.ToHashSet(StringComparer.Ordinal);

        // the template: a game body's own, or a new one for a body of yours
        string template;
        var templateGuid = Guid.Empty;
        var aiBase = ai.Template ?? AiDefault;
        if (own)
        {
            templateGuid = BlockIds.Derive(mod, "template", key);
            template = $"{mod}.{name}";
            var fields = TemplateFields(ctx, body, ai, graph, model, box, moves);
            // the base creature's gear hangs on its own slots: a body of your own does without it
            low.Clone("templates", templateGuid, template, aiBase, fields, removeComponents: GearOf(aiBase));
            // and its hit, stun and death sequences play this animation's states, not the base creature's
            low.AfterCommit(() => NpcReactions.Add(ctx.Mod, key, name, aiBase, template, graph));
        }
        else template = body.Template!;

        // how its brain sees: checked here, with no world; without a brain it would claim to change the game's own AI
        if (NpcPerception.Problem(bh.Perception) is { } wrong) throw new BlockError(ctx.Block, "behaviour.perception", wrong);
        if (bh.Perception != new NpcPerceptionPolicy() && bh.Brain == null && bh.BrainName == null)
            throw new BlockError(ctx.Block, "behaviour.perception", "perception is how a brain sees: this behaviour has no brain, and the game's own AI sees as the game decides");
        var owner = OwnerFor(ctx);
        var live = ModContext.CurrentMod is { } current && current.Info.Id == mod ? current : Mods.Get(mod)?.Info is { } modInfo ? ModContext.FindLive(modInfo) : null;
        var info = new NpcCreature
        {
            Perception = bh.Perception, Context = live,
            Mod = mod, Name = name, Variant = $"{mod}:{name}", Template = template, TemplateGuid = templateGuid, Owner = owner,
            Sight = ai.Sight ?? 30, Faction = ai.Faction,
            Brain = bh.Brain ?? (bh.BrainName is { } bn ? NpcRuntime.Lookup(NpcRuntime.Brains, bn) : null),
            States = states.ToDictionary(x => x.Key, x => x.Value.Hash, StringComparer.Ordinal),
        };

        // the behaviour: an arsenal of its own when it adds anything
        var spec = Arsenal(ctx, low, mod, key, name, bh, ai, attacks, graph, own, p.Anim, phases, info);
        if (spec != null)
        {
            info.Arsenal = RuleIds.Derive(mod, "arsenal", key);
            if (!own && attacks.Any(a => a.Block.Sequence == null)) ctx.Need(ClientsLevel.Kiln, "behaviour.attacks", "an attack of its own is a new sequence: Kiln clients get it from the server");
        }

        // what it drops when it dies: Kiln rolls the table where the death is reported (Items/LootTables.cs)
        var drops = c.Drops is { } dropsRef ? Compiler.LootPlanOf(ctx, low, dropsRef, "drops") : null;

        // the variant every creature of the block is, and where it turns up by itself
        var variant = Variant(ctx, c, info, body, ai, bh, own, moves);
        low.Json["creature"] = new JsonObject
        {
            ["variant"] = info.Variant, ["template"] = template, ["base"] = own ? aiBase : null, ["graph"] = graph.Guid == Guid.Empty ? null : graph.Guid.ToString(),
            ["model"] = model == Guid.Empty ? null : model.ToString(), ["states"] = new JsonArray(states.Keys.Order().Select(k => (JsonNode)k).ToArray()),
            ["arsenal"] = info.Arsenal?.ToString(), ["brain"] = info.Brain != null, ["clients"] = own ? "mod" : spec != null && attacks.Any(a => a.Block.Sequence == null) ? "kiln" : "none",
            ["variantValues"] = JsonSerializer.SerializeToNode(new { variant.Level, Faction = variant.Faction?.ToString(), variant.Scale, variant.DamageFactor, variant.XpFactor,
                Disable = variant.DisableFeatures.ToString(), Ai = new SortedDictionary<string, string>(variant.Ai) }),
        };
        if (drops != null) ((JsonObject)low.Json["creature"]!)["drops"] = drops.ToJson();
        var spawns = c.Spawns ?? [];
        foreach (var (s, i) in spawns.Select((s, i) => (s, i)))
        {
            if (s.Chance is < 0 or > 1) throw new BlockError(ctx.Block, $"spawns[{i}].chance", $"{s.Chance} is outside 0..1");
            if (s.NightOnly && s.DayOnly) throw new BlockError(ctx.Block, $"spawns[{i}]", "night only or day only, not both");
            if (Creatures.Available && Creatures.Template(s.Replaces) is null) throw new BlockError(ctx.Block, $"spawns[{i}].replaces", $"the game has no creature \"{s.Replaces}\"");
        }
        // The arsenal, the variant and the spawn rules are the server world's. On the tick thread (a server's OnLoad, a data file's block) they are
        // written now, and what the arsenal's writer refuses is refused by name before anything else of the block is registered. A game client runs
        // OnLoad off the tick thread and has no server world until it hosts one: there they wait for it (NpcServerSide).
        if (spec != null && Creatures.Available && NpcServerSide.Now)
            DefineArsenal(ctx, info, spec, owner, mod, name, thrown: true);
        else if (spec != null)
            NpcServerSide.Post(ctx.Mod, ctx.Block, () => DefineArsenal(ctx, info, spec, owner, mod, name, thrown: false));
        low.AfterCommit(() =>
        {
            NpcRuntime.Register(info);
            LootRuntime.Register(info.Variant, ctx.Mod.Id, name, drops);
            AiRuntime.Attach(info, ai);   // npcai: relations, sense filter (Ai/AiRuntime.cs)
            NpcServerSide.Post(ctx.Mod, ctx.Block, () => Register(ctx, info, variant, spawns));
        });
        return new CreatureHandle(mod, name, templateGuid == Guid.Empty ? BlockIds.Derive(mod, "creature", key) : templateGuid, template, c, info);
    }

    /// <summary>What of a base creature's gear its template has: removed from a template on a body of your own.</summary>
    private static IReadOnlyList<string>? GearOf(string template)
    {
        // Before the game's templates are read, or off the tick thread (a game client's OnLoad), what the default base is known to have: a client and its
        // server must make the same clone, so the answer can't depend on where it was asked.
        IReadOnlyList<string>? Known() => template == AiDefault ? ["SubEntities", "HookShotAnchorSubEntity"] : null;
        if (template == AiDefault || !Game.IsTickThread) return Known();
        GameDataEntry? t;
        try { t = Content.Templates.Get(template); }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException) { return Known(); }
        if (t == null) return Known();
        var gear = new List<string>();
        foreach (var c in new[] { "SubEntities", "HookShotAnchorSubEntity" })
        {
            try
            {
                if (t.GetJson(c) is { } j && j["absent"] is null) gear.Add(c);
            }
            catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException or KeyNotFoundException) { }
        }
        return gear.Count > 0 ? gear : null;
    }

    private static Creatures.OwnerKey OwnerFor(BlockContext ctx) =>
        BlocksBridge.CallerModId == ctx.Mod.Id ? Creatures.OwnerKey.Caller : Creatures.OwnerKey.Named($"blocks:{ctx.Mod.Id}");

    /// <summary>The template a body of your own is: the AI's creature with the cooked graph and model, its own state fields and size.</summary>
    private static JsonObject TemplateFields(BlockContext ctx, BodyBlock body, AIBlock ai, Graph graph, Guid model, JsonNode? box, bool moves)
    {
        string? G(string engine) => AnimState.Engine.First(e => e.Name == engine).Guid?.ToString();
        var t = new JsonObject
        {
            ["AnimationGraphResourceComponent2.animationGraph"] = graph.Guid.ToString(),
            ["ModelResource.model"] = model.ToString(),
            // states the AI names by field: what this animation has. Run has no GUID a field can name, so fleeing walks.
            ["Enemy01BehaviorState.combatSetup.combatIdleAnimation"] = G("IdleCombat"),
            ["Enemy01BehaviorState.fleeSetup.fleeAnimation"] = G("Walk"),
            ["Enemy01BehaviorState.patrolSetup.patrolAnimation"] = G("Walk"),
            ["Enemy01BehaviorState.sleepSetup.sleepAnimation"] = G("Idle"),
            ["Locomotion.waterConfig.swimIdleAnimation"] = G("Idle"),
            ["Locomotion.waterConfig.swimMoveAnimation"] = G("Walk"),
            ["Locomotion.waterConfig.swimSprintAnimation"] = G("Walk"),
            ["Locomotion.waterConfig.diveIdleAnimation"] = G("Idle"),
            ["Locomotion.waterConfig.diveMoveAnimation"] = G("Walk"),
            ["Locomotion.waterConfig.diveSprintAnimation"] = G("Walk"),
        };
        var scale = body.Scale ?? 1;
        if (body.Scale is { } s) t["ScaleComponent.uniformScale"] = s;
        // what the server culls and aims by: the model's own bounds
        if (box?["min"] is { } mn && box["max"] is { } mx)
        {
            static double At(JsonNode v, string axis, int i) => (v is JsonArray a ? a[i] : v[axis])?.GetValue<double>() ?? 0;
            foreach (var (axis, i) in new[] { ("x", 0), ("y", 1), ("z", 2) })
            {
                t[$"Enemy01BehaviorState.defaultLocalBoundingBoxMin.{axis}"] = At(mn, axis, i) * scale;
                t[$"Enemy01BehaviorState.defaultLocalBoundingBoxMax.{axis}"] = At(mx, axis, i) * scale;
            }
        }
        if (body.Size is { } size)
        {
            t["PathWalker.horizontalRadius"] = size.Radius;
            t["PathWalker.height"] = size.Height;
            t["MovementColliderConfig.width"] = size.Radius * 2;
            t["MovementColliderConfig.height"] = size.Height;
        }
        if (body.Colliders is { Count: > 0 } cs)
        {
            // every collider mode's first two capsules (hits, then what it bumps into); a single shape is both
            for (var k = 0; k < 2; k++)
            {
                var c = cs[Math.Min(k, cs.Count - 1)];
                var at = $"ColliderResourceComponent.colliders[*].dataArray[CapsuleColliderData#{k}]";
                t[$"{at}.shape.radius"] = c.Radius;
                t[$"{at}.shape.length"] = c.Shape == "capsule" ? Math.Max(0, c.Height - 2 * c.Radius) : 0;
                t[$"{at}.offset.y"] = c.Shape == "capsule" ? c.Height / 2 : c.Radius;
                t[$"{at}.offset.z"] = 0.0;
            }
        }
        if (ai.Faction is { } f) t["Faction.faction"] = f.ToString();
        // an AI value names a component when it starts with one (upper case); without one it is Enemy01BehaviorState's
        foreach (var (k, v) in AiValues(ai)) t[char.IsUpper(k[0]) ? k : $"Enemy01BehaviorState.{k}"] = Number(v);
        return t;
    }

    private static JsonNode Number(string v) =>
        double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(d) : v is "true" or "false" ? JsonValue.Create(v == "true") : JsonValue.Create(v);

    /// <summary>The AI's values by path, <c>Enemy01BehaviorState</c> paths without the component's name.</summary>
    private static SortedDictionary<string, string> AiValues(AIBlock ai)
    {
        var v = new SortedDictionary<string, string>(StringComparer.Ordinal);
        static string N(double d) => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        // The game's sight check reads combatCommitmentSetup. aggroSetup.viewRange is only the radius of the search for candidates (never below
        // 64 m), so it follows a sight beyond that; aggroSetup's aggroRange and viewConeHalfAngle are not read at all.
        if (ai.Sight is { } s)
        {
            v["combatCommitmentSetup.view.range"] = N(s);
            if (s > 64) v["aggroSetup.viewRange"] = N(s);
        }
        if (ai.SightAngle is { } sa) v["combatCommitmentSetup.view.coneHalfAngle"] = N(sa);
        if (ai.Aggro is { } ag) v["combatCommitmentSetup.gain.inRange.range"] = N(ag);
        if (ai.Leash is { } l) v["homeZoneSetup.radius"] = N(l);
        AiLowering.Values(ai, v);   // npcai's fields; Tune still wins
        foreach (var (k, x) in ai.Tune ?? new Dictionary<string, string>()) v[k] = x;
        return v;
    }

    private static CreatureVariant Variant(BlockContext ctx, CreatureBlock c, NpcCreature info, BodyBlock body, AIBlock ai, BehaviourBlock bh, bool own, bool moves)
    {
        var stats = c.Stats ?? new CreatureStats();
        if (stats.Level is < 1 or > 60) throw new BlockError(ctx.Block, "stats.level", $"{stats.Level} is outside 1..60");
        if (stats.Damage is < 0 or > 100) throw new BlockError(ctx.Block, "stats.damage", $"{stats.Damage} is outside 0..100");
        if (stats.Experience is < 0 or > 100) throw new BlockError(ctx.Block, "stats.experience", $"{stats.Experience} is outside 0..100");
        var off = own ? OwnStatesOff : EnemyFeatures.None;
        if (!moves) off |= EnemyFeatures.Move | EnemyFeatures.Roaming | EnemyFeatures.Patrol | EnemyFeatures.Run;
        if (ai.Fights == false) off |= EnemyFeatures.Combat;
        off |= AiLowering.Disabled(ai);   // npcai
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!own)
            foreach (var (k, v) in AiValues(ai)) values[k] = v;   // a game body's template is the game's: the values go on each creature
        if (info.Arsenal is { } arsenal) values["EnemyArsenalReference.arsenal"] = arsenal.ToString();
        var resist = new Dictionary<DamageKind, float>();
        foreach (var (k, v) in stats.Takes ?? new Dictionary<DamageKind, double>())
        {
            if (v is < 0 or > 10) throw new BlockError(ctx.Block, $"stats.takes[{k}]", $"{v} is outside 0..10");
            resist[k] = (float)v;
        }
        return new CreatureVariant
        {
            Id = info.Variant, Base = info.Template, Name = c.Name, Level = stats.Level, Faction = ai.Faction, Scale = own ? null : (float?)body.Scale,
            DamageFactor = (float?)stats.Damage, XpFactor = (float?)stats.Experience, Resistances = resist, DisableFeatures = off, EnableFeatures = AiLowering.Enabled(ai) & ~off, Ai = values,
        };
    }

    private static void DefineArsenal(BlockContext ctx, NpcCreature info, ArsenalSpec spec, Creatures.OwnerKey owner, string mod, string name, bool thrown)
    {
        if (!Creatures.Available) return;
        try { Arsenals.DefineFor(info.Arsenal!.Value, spec, owner, mod, $"blocks:{name}"); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or InvalidOperationException or KeyNotFoundException)
        {
            if (thrown) throw new BlockError(ctx.Block, "behaviour", e.Message);
            NpcServerSide.Fail(ctx.Mod, new BlockError(ctx.Block, "behaviour", e.Message).Message);
        }
    }

    private static void Register(BlockContext ctx, NpcCreature info, CreatureVariant variant, IReadOnlyList<SpawnBlock> spawns)
    {
        if (!Creatures.Available)
        {
            BlocksBridge.Log(LogLevel.Warn, $"blocks: {ctx.Mod.Id}: {ctx.Block}: kiln.creatures is not here: the creature is defined, and nothing spawns it");
            return;
        }
        var source = $"blocks:{info.Name}";
        bool Add()
        {
            CreatureVariants.RemoveOwned(info.Owner, source);
            try { CreatureVariants.Add(variant, info.Owner, source); }
            catch (ArgumentException) when (variant.Base == info.Template && info.TemplateGuid != Guid.Empty) { return false; }  // its template is registered a little later
            foreach (var s in spawns)
                CreatureVariants.AddRule(new CreatureSpawnRule(s.Replaces, variant.Id, s.Chance, s.NightOnly, s.DayOnly), info.Owner, source);
            BlocksBridge.Log(LogLevel.Info, $"blocks: {ctx.Mod.Id}: {ctx.Block}: variant {variant.Id} on {variant.Base}{(spawns.Count > 0 ? $", {spawns.Count} spawn rule(s)" : "")}");
            return true;
        }
        if (Add()) return;
        var done = false;
        BlocksBridge.OnReady(() => done || (done = Add()), () => !done);
    }

    // ---- the arsenal ------------------------------------------------------------------------------------------------

    private static ArsenalSpec? Arsenal(BlockContext ctx, Lowered low, string mod, string key, string name, BehaviourBlock bh, AIBlock ai,
        List<(AttackBlock Block, int Index)> attacks, Graph graph, bool own, Part<AnimationBlock> anim, HashSet<string>? phases, NpcCreature info)
    {
        var rules = bh.Rules ?? [];
        var aiRules = AiLowering.Rules(ai, name);   // npcai: the AI block's own rules (flee)
        var pickTarget = ai.PickTarget ?? (ai.PickTargetName is { } tn ? NpcRuntime.Lookup(NpcRuntime.Targets, tn) : null);
        var pickAttack = bh.PickAttack ?? (bh.PickAttackName is { } an ? NpcRuntime.Lookup(NpcRuntime.AttackPicks, an) : null);
        if (attacks.Count == 0 && rules.Count == 0 && aiRules.Count == 0 && pickTarget == null && pickAttack == null) return null;
        if (bh.Template != null && Creatures.Available && Creatures.Template(bh.Template) is null)
            throw new BlockError(ctx.Block, "behaviour.template", $"the game has no creature \"{bh.Template}\"");

        var list = new List<ArsenalAttack>();
        var names = new List<string>();
        foreach (var (a, i) in attacks)
        {
            var aname = a.Name ?? $"attack{i}";
            names.Add(aname);
            var path = $"behaviour.attacks[{i}]";
            var sequence = a.Sequence ?? AttackSequence(ctx, low, mod, key, name, aname, a, path, anim, own, info);
            info.AttackSequences[aname] = sequence;
            var selection = new List<RuleCondition> { Is.TargetBetween(a.MinRange ?? 0, a.Range) };
            if (a.Cooldown is { } cd && cd > 0) selection.Add(Is.Game("CooldownConstrain", ("duration", cd)));
            list.Add(new ArsenalAttack
            {
                Name = aname, Id = RuleIds.Derive(mod, "attack", aname), Sequence = sequence,
                Style = (a.Style is { Count: > 0 } st ? st : [AttackStyle.Normal]).Select(x => x.ToString()).ToArray(),
                Chance = a.Chance, MinRange = a.MinRange, MaxRange = a.Range, Selection = selection,
            });
        }

        var outRules = new List<ArsenalRule>();
        for (var i = 0; i < rules.Count; i++)
        {
            var r = ctx.Part(rules[i], $"behaviour.rules[{i}]");
            var path = $"behaviour.rules[{i}]";
            CheckRule(ctx, r, path, used: true);
            var when = r.When.Select((x, k) => NpcRules.Condition(x, mod, ctx, $"{path}.when[{k}]")).ToList();
            var actions = new List<RuleAction>();
            for (var k = 0; k < r.Do.Count; k++)
            {
                var act = NpcRules.Action(r.Do[k], mod, ctx, $"{path}.do[{k}]");
                if (NpcRuntime.EffectOf(act) is { } eff) act = EffectAction(ctx, low, mod, key, eff.Effect, eff.On, $"{path}.do[{k}].effect", info);
                act = AiLowering.Lower(ctx, low, mod, key, name, act, $"{path}.do[{k}]", info) ?? act;   // npcai: casts
                CheckNames(ctx, act.Json, $"{path}.do[{k}]", mod, phases, names);
                actions.Add(act);
            }
            foreach (var (cond, k) in when.Select((x, k) => (x, k))) CheckNames(ctx, cond.Json, $"{path}.when[{k}]", mod, phases, null);
            outRules.Add(new ArsenalRule { Name = r.Name ?? $"{name}.rule{i}", When = r.On, Every = r.Every, Cooldown = r.Cooldown, Chance = r.Chance, If = when, Do = actions });
        }
        outRules.AddRange(aiRules);   // npcai

        Func<NpcView, string?>? pick = null;
        if (pickAttack != null)
            pick = npc => pickAttack(npc) is { } n ? names.Contains(n) ? RuleIds.Derive(mod, "attack", n).ToString() : n : null;
        low.Json["arsenal"] = new JsonObject
        {
            ["from"] = bh.Template,
            ["attacks"] = new JsonArray(list.Select(a => (JsonNode)new JsonObject { ["name"] = a.Name, ["sequence"] = a.Sequence?.ToString(), ["style"] = string.Join(",", a.Style ?? []), ["range"] = a.MaxRange, ["chance"] = a.Chance }).ToArray()),
            ["rules"] = new JsonArray(outRules.Select(r => (JsonNode)new JsonObject { ["name"] = r.Name, ["on"] = r.When.ToString(), ["every"] = r.Every, ["cooldown"] = r.Cooldown,
                ["when"] = new JsonArray(r.If.Select(x => (JsonNode)Shown(x.Json)).ToArray()), ["do"] = new JsonArray(r.Do.Select(x => (JsonNode)Shown(x.Json)).ToArray()) }).ToArray()),
            ["pickAttack"] = pickAttack != null, ["pickTarget"] = pickTarget != null,
        };
        return new ArsenalSpec
        {
            From = bh.Template is { } t ? Arsenals.OfTemplate(t) : null,
            Attacks = list, Rules = outRules, PickAttack = pick, PickTarget = pickTarget, PickEvery = bh.PickEvery ?? ai.PickEvery ?? 0.25,
        };
    }

    // What a rule's JSON shows in Lowered: names in place of process-local slots and ids.
    private static JsonNode Shown(JsonObject j)
    {
        var c = (JsonObject)j.DeepClone();
        c.Remove("slot");
        c.Remove("id");
        if (c["kiln"]?.GetValue<string>() == "queue") c["kiln"] = "code";
        if (c["kiln"]?.GetValue<string>() == "condition") c["kiln"] = "code";
        return c;
    }

    /// <summary>A phase or attack a rule names must be the behaviour's.</summary>
    private static void CheckNames(BlockContext ctx, JsonObject j, string path, string mod, HashSet<string>? phases, List<string>? attacks)
    {
        var kind = j["kiln"]?.GetValue<string>();
        if (kind == "phase" && phases != null && Guid.TryParse(j["phase"]?.GetValue<string>(), out var ph) && !phases.Any(n => RuleIds.Derive(mod, "phase", n) == ph))
            throw new BlockError(ctx.Block, path, $"a phase that is not one of the behaviour's: {string.Join(", ", phases.Order())}");
        if (kind == "attack" && attacks != null && Guid.TryParse(j["attack"]?.GetValue<string>(), out var at) && !attacks.Any(n => RuleIds.Derive(mod, "attack", n) == at))
            throw new BlockError(ctx.Block, path, $"an attack that is not one of the behaviour's: {string.Join(", ", attacks)}");
    }

    /// <summary>A rule's effect: a program run from the creature, on its target or on itself.</summary>
    private static RuleAction EffectAction(BlockContext ctx, Lowered low, string mod, string key, EffectRef effect, NpcEffectOn on, string path, NpcCreature info)
    {
        var e = ctx.Effect(effect, path);
        var tag = effect.Name ?? $"{path.Replace('[', '.').Replace("]", "")}";
        var pkey = $"{key}.{tag}.{on.ToString().ToLowerInvariant()}";
        Guid program;
        if (on == NpcEffectOn.Self)
        {
            var (g, blob, _, listing) = EffectEmitter.SelfProgram(ctx, pkey, [e], path);
            low.Program(g, pkey, blob, listing);
            program = g;
        }
        else
        {
            var b = new ProgramBuilder(pkey);
            var em = new EffectEmitter(b, ctx);
            var target = b.V("target", ImpactType.Entity, default, EffectEmitter.CfgTarget);
            b.BeginCode();
            b.Call("getRoot", null, em.Caster);
            b.Pop();
            em.OnTarget([e], target, path);
            b.DestroySelf();
            b.Halt();
            b.BeginShutdown();
            em.EmitShutdown();
            b.Halt();
            var (g, blob) = ctx.Build(b);
            low.Program(g, pkey, blob, b.Listing.ToString());
            program = g;
        }
        info.Effects[tag] = (program, on);
        return Act.Custom(npc => NpcRuntime.RunProgram(info, program, on, npc.Entity, npc.Target));
    }

    /// <summary>An attack's sequence: a clone of a game attack, whose states are the attack's and whose spawn event places its hit (a cone in front, or its delivery) at <see cref="AttackBlock.HitAt"/>.</summary>
    private static Guid AttackSequence(BlockContext ctx, Lowered low, string mod, string key, string name, string aname, AttackBlock a, string path, Part<AnimationBlock> anim, bool own, NpcCreature info)
    {
        var akey = $"{key}.{aname}";
        var stateMod = own ? anim.Mod : mod;
        var state = a.Plays!.Guid(stateMod)!.Value;
        var length = a.Length;
        if (length == null && own && anim.Block.States.TryGetValue(a.Plays!, out var clip))
            length = ClipLength(ctx, anim.Mod, anim.Block.Skeleton ?? Stem(anim.Block.Clips!), clip, false);
        length ??= Math.Max(a.HitAt + 0.5, 1.2);
        if (a.HitAt >= length) throw new BlockError(ctx.Block, $"{path}.hitAt", $"{a.HitAt} is not inside the attack's {length:0.###} seconds (its clip's length)");
        var delivery = a.Delivery ?? new ConeBlock { Range = (float)a.Range, Angle = (float)a.Angle, Effects = a.OnHit! };
        var lowered = Compiler.LowerDelivery(ctx, low, akey, $"{name}.{aname}", delivery, a.Delivery != null ? $"{path}.delivery" : $"{path}.onHit", primary: false);
        var seqGuid = BlockIds.Derive(mod, "sequence", akey);
        var seqName = $"{mod}.{name}.{aname}";
        // The base's events keep their order, stretched over the attack's length; its three state events all play the attack's state.
        var f = length.Value / AttackBaseLength;
        double T(double s) => Math.Round(s * f, 4);
        var fields = new JsonObject
        {
            ["length"] = length.Value,
            ["events[EnableAbilityEvent#1].time"] = T(0.883),
            ["events[SetAnimationEvent#0].animationName"] = state.ToString(),
            ["events[SetAnimationEvent#1].animationName"] = state.ToString(),
            ["events[SetAnimationEvent#1].retriggerAnimation"] = false,
            ["events[SetAnimationEvent#1].time"] = T(0.167),
            ["events[SetAnimationEvent#2].animationName"] = state.ToString(),
            ["events[SetAnimationEvent#2].retriggerAnimation"] = false,
            ["events[SetAnimationEvent#2].time"] = T(0.667),
            ["events[TriggerNoiseEvent#0].time"] = T(0.05),
            ["events[SfxNotifierEvent#0].time"] = T(0.05),
            ["events[SfxNotifierEvent#1].time"] = T(0.9),
            ["events[RotationLerpEvent#0].time"] = T(0.083),
            ["events[RotationLerpEvent#0].duration"] = Math.Min(T(0.65), Math.Max(0.05, a.HitAt - T(0.083))),
            ["events[SetTriggerStateEvent#0].time"] = T(0.15),
            ["events[SetTriggerStateEvent#1].time"] = T(0.767),
            ["events[DisableAbilityEvent#0].time"] = T(0.583),
            ["events[DisableAbilityEvent#1].time"] = T(0.733),
            ["events[EnemyCommandEvent#0].time"] = T(0.833),
            ["events[SpawnEntityEvent#0].time"] = a.HitAt,
        };
        low.Clone("sequences", seqGuid, seqName, AttackBase, fields);
        // a clone names another clone once both are registered: a rule
        var rules = new JsonObject
        {
            ["events[SpawnEntityEvent#0].templateReference"] = lowered.PlacedEntity.ToString(),
            ["events[SpawnEntityEvent#0].requiredEntityTag"] = "none",
            ["events[SpawnEntityEvent#0].useTargetingPosition"] = false,
            ["events[SpawnEntityEvent#0].spawnOffset.worldOffset.y"] = 1.0,
        };
        if (a.Delivery is ProjectileBlock)
        {
            rules["events[SpawnEntityEvent#0].ignoreSpawnVelocity"] = true;
            rules["events[SpawnEntityEvent#0].spawnVelocity.z"] = (double)lowered.Speed;
        }
        low.AfterCommit(() => BlockRuntime.AddRules(ctx.Mod, "sequences", seqName, rules));
        (low.Json["sequenceRules"] ??= new JsonObject()).AsObject()[aname] = rules.DeepClone();
        return seqGuid;
    }

    // ---- NPCs -------------------------------------------------------------------------------------------------------

    private static BlockHandle DefineNpc(BlockContext ctx, Lowered low, string name, NpcBlock n)
    {
        var mod = ctx.Mod.Id;
        var key = n.Was ?? name;
        if (string.IsNullOrWhiteSpace(n.Name)) throw new BlockError(ctx.Block, "name", "an NPC needs a name players can read");
        var body = PartOf<BodyBlock>(ctx, n.Body, null, name, "body");
        JsonObject? ownBody = null;
        var authored = body.Block.Model != null;
        NpcBodyCapRows.Rows.Ensure();  // an NPC block is a first use of Kiln.Blocks: blocks.npc_body from here
        if (authored)
        {
            // The chassis is Kiln's choice, never an author-facing reflection/template recipe.
            var row = BlockCapabilities.CurrentNpcBodyRow();
            if (row.State != CapabilityState.Available)
                throw new BlockError(ctx.Block, "body.model", "an NPC's body is a game NPC's for now (Bodies.Villager, Bodies.Fisher, ...): the game builds NPCs at start from a template, and a custom body there needs that build to set its graph and model. A body of your own fights as a creature block. " + BlockCapabilities.NpcBody + ": " + row.Text);
            NpcBodyRig.Geometry(ctx, body.Block);
            body = body with { Block = body.Block with { Template = null, Workshop = body.Block.Workshop ?? Bodies.Fisher.Workshop, Tool = body.Block.Tool ?? Bodies.Fisher.Tool } };
            CheckBody(ctx, body.Block, "body", body.Name);
            var animation = PartOf<AnimationBlock>(ctx, n.Animation, null, name, "animation");
            CheckAnimation(ctx, animation.Block, "animation");
            var motions = PartOf<NpcMotionsBlock>(ctx, n.Motions, null, name, "motions");
            if (body.Mod != mod || animation.Mod != mod || motions.Mod != mod)
                throw new BlockError(ctx.Block, "body.model", "a roster body's model, animation and motions must belong to this mod; cross-mod body dependencies are not supported");
            if (animation.Block.Clips == null || animation.Name == null || body.Name == null)
                throw new BlockError(ctx.Block, "animation", "an authored roster body needs this mod's cooked animation");
            NpcBodyRig.SkeletonNames(ctx, body.Block.Skeleton ?? Stem(body.Block.Model!), animation.Block.Skeleton ?? Stem(animation.Block.Clips));
            var graph = CookedGraph(ctx, mod, $"{animation.Name}.{body.Name}", "animation", "blocks/*.json");
            // A dedicated server's cook makes no renderer output (no <stem>.model.json): its model GUID is in the body manifest, and
            // the bounds are the client's to check (a game client always has the sidecar its cook made)
            Guid model, skeleton;
            if (ServerModel(ctx, mod, ModelName(body.Block.Model!)) is { } serverModel)
            {
                model = serverModel;
                skeleton = graph.Skeleton;
            }
            else
            {
                (model, skeleton, var box) = CookedModel(ctx, mod, ModelName(body.Block.Model!), "body.model", "blocks/*.json", body.Block);
                NpcBodyRig.Cooked(ctx, body.Block, graph, skeleton);
                if (box == null) throw new BlockError(ctx.Block, "body.size", "the cooked model has no bounds; cook again");
                NpcBodyRig.Geometry(ctx, body.Block, box);
            }
            ownBody = new JsonObject
            {
                ["model"] = model.ToString(), ["graph"] = graph.Guid.ToString(), ["skeleton"] = skeleton.ToString(),
                ["size"] = new JsonObject { ["radius"] = body.Block.Size!.Radius, ["height"] = body.Block.Size.Height },
                ["bindings"] = NpcBodyRig.Bindings(ctx, motions.Block, mod, animation.Block, graph,
                    (clip, loop) => ClipLength(ctx, mod, animation.Block.Skeleton ?? Stem(animation.Block.Clips), clip, loop)),
            };
        }
        else
        {
            CheckBody(ctx, body.Block, "body", body.Name);
            if (n.Animation != null || n.Motions != null)
                throw new BlockError(ctx.Block, n.Animation != null ? "animation" : "motions", "a game NPC body uses its own graph and motions; authored motions need a body.model of this mod");
        }
        if (n.AI != null || n.Behaviour != null)
            throw new BlockError(ctx.Block, n.AI != null ? "ai" : "behaviour", "a villager moves, decides and talks as the game's villagers do; a friendly fighter is a creature block with ai.faction = PlayerAlly");
        if (n.Talk != null && n.TalkName != null) throw new BlockError(ctx.Block, "talk", "a dialogue from code or by name, not both");
        ctx.Need(ClientsLevel.Mod, null, "an NPC, its lines and its place in the roster are built by every player's game at start, from the mod");

        var fromData = ctx.Mod.Compiling.Contains(name);   // a data file's block compiles under its name; one from code does not
        var cloneGuid = BlockIds.Derive(mod, "npc", key);
        var cloneName = $"Kiln_{mod}_{key}";
        var names = new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = n.Name };
        foreach (var (lang, text) in n.Names ?? new Dictionary<string, string>()) names[lang] = text;
        // Native dialogue compilation runs per mod at startup: an NPC can use only this mod's dialogue definitions.
        if (n.Talk is { ModId.Length: > 0 } externalTalk && !externalTalk.ModId.Equals(mod, StringComparison.Ordinal))
            throw new BlockError(ctx.Block, "talk", "an NPC's native dialogue must belong to this mod; another mod's dialogue is not supported by the startup build");
        var dialogue = n.Talk?.Id ?? (n.TalkName is { } talkName ? QuestCompiler.NpcDialogue(ctx, talkName) : null);

        // the vendor: recipes at the body's workshop
        if (n.Vendor is { } v)
        {
            var workshop = v.Workshop ?? body.Block.Workshop
                           ?? throw new BlockError(ctx.Block, "vendor.workshop", $"the body {body.Block.Template} has no workshop Kiln knows: name it (\"0x…\", GameWorkshops)");
            if (!workshop.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) throw new BlockError(ctx.Block, "vendor.workshop", $"\"{workshop}\": a workshop's 0x id");
            if (v.Sells.Count == 0) throw new BlockError(ctx.Block, "vendor.sells", "a vendor that sells nothing");
            var i = 0;
            foreach (var (item, price) in v.Sells)
            {
                var path = $"vendor.sells[{item}]";
                if (price.Count < 1) throw new BlockError(ctx.Block, $"{path}.count", $"{price.Count} is less than 1");
                var gives = v.Gives != null && v.Gives.TryGetValue(item, out var g) ? g : 1;
                if (gives < 1) throw new BlockError(ctx.Block, $"vendor.gives[{item}]", $"{gives} is less than 1");
                low.Recipe(new RecipeDefinition
                {
                    Guid = BlockIds.Derive(mod, "recipe", $"{key}.sells.{item}"), Name = $"{mod}.{name}.sells{i++}", Station = workshop,
                    Inputs = [new RecipeIngredient(ctx.ItemName(price.Item, $"{path}.item"), price.Count)], Outputs = [new RecipeIngredient(ctx.ItemName(item, path), gives)],
                });
            }
        }

        // the quests' NPC, as it is: the engine finds it, and its roster entry goes to the boot build
        // 0.2.0-dev.30.6 ignored either when not of its length (below), and so does this release
        if (n.Place?.At is { Length: not 3 }) ctx.Tolerate("place.at", "takes [x, y, z]; it is ignored");
        if (n.Place?.Distance is { Length: not 2 }) ctx.Tolerate("place.distance", "takes [min, max]; it is ignored");
        if (n.Place is { Exact: true, At: null }) throw new BlockError(ctx.Block, "place.exact", "exactly where? It needs place.at");
        var place = n.Place is { } pl ? new QuestPlace
        {
            At = pl.At is { Length: 3 } at ? new WorldPosition(at[0], at[1], at[2]) : null, NearPlayer = pl.At == null,
            MinDistance = pl.Distance is { Length: 2 } d ? d[0] : 0, MaxDistance = pl.Distance is { Length: 2 } d2 ? d2[1] : 0,
            InBase = pl.InBase, Exact = pl.Exact,
        } : null;
        var def = new QuestNpcDefinition
        {
            Id = key, Template = authored ? Bodies.Fisher.Template! : body.Block.Template!, Clone = true, CloneGuid = cloneGuid, CloneName = cloneName, Dialogue = dialogue,
            Name = new UiText(names), Place = place,
        };
        RoutineNpc.ForNpc(ctx, low, name, key, n.Routine, cloneGuid);
        var file = NpcFile(ctx, key, n, body.Block, cloneGuid, cloneName, names, dialogue);
        if (ownBody != null) file["body"] = ownBody;
        low.Json["npc"] = file.DeepClone();
        low.AfterCommit(() =>
        {
            if (n.Talk is { } talk && !Dialogues.Definitions.Any(x => ReferenceEquals(x, talk)))
            {
                if (talk.ModId.Length == 0) talk.ModId = mod;
                try { Dialogues.Define(talk); }
                catch (ArgumentException e) { throw new BlockError(ctx.Block, "talk", e.Message); }
            }
            def.ModId = mod;
            Quests.DefineNpc(def);
            // one of a data file the game's start-up build reads from the mod's folder itself (native_pack.cpp): only one from code is written for
            // the next start, and never one with a body of its own: this version builds those from blocks/*.json only (NpcBodies.CodeReason, its status)
            if (!fromData && ownBody == null) NpcFiles.Put(mod, key, file);
            else NpcFiles.Forget(mod, key);
            if (ownBody == null || fromData) NpcBodies.FromData(mod, name);
            else
            {
                NpcBodies.DefinedFromCode(mod, name);
                BlocksBridge.Log(LogLevel.Warn, $"blocks: {mod}: npc {name}: {NpcBodies.CodeReason}");
            }
        });
        return new BlockHandle<NpcBlock>(mod, name, cloneGuid, cloneName, n);
    }

    /// <summary>The NPC in the quests' file format, for the boot build: what QuestNpcDefinition has, and its roster entry.</summary>
    private static JsonObject NpcFile(BlockContext ctx, string key, NpcBlock n, BodyBlock body, Guid cloneGuid, string cloneName, Dictionary<string, string> names, string? dialogue)
    {
        var o = new JsonObject
        {
            ["template"] = body.Model != null ? Bodies.Fisher.Template : body.Template,
            ["clone"] = new JsonObject { ["guid"] = cloneGuid.ToString(), ["name"] = cloneName },
            ["names"] = new JsonObject(names.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => KeyValuePair.Create(x.Key, (JsonNode?)x.Value))),
        };
        if (dialogue != null) o["dialogue"] = dialogue;
        if (n.Roster?.Description is { } desc) o["description"] = new JsonObject { ["en"] = desc };
        if (n.Place is { } p)
        {
            var po = new JsonObject { ["once"] = p.Once, ["unlock"] = false };
            if (p.At is { Length: 3 } at) po["at"] = new JsonArray(at[0], at[1], at[2]);
            else po["near"] = "player";
            if (p.Distance is { Length: 2 } d) po["distance"] = new JsonArray(d[0], d[1]);
            po["inBase"] = p.InBase;
            if (p.Exact) po["exact"] = true;
            o["place"] = po;
        }
        if (n.Roster is { } r)
        {
            var tool = body.Tool ?? throw new BlockError(ctx.Block, "roster", $"the body {body.Template} has no summon tool Kiln knows to start from: a roster NPC needs one");
            if (r.RoamingRange is < 0 or > 200) throw new BlockError(ctx.Block, "roster.roamingRange", $"{r.RoamingRange} is outside 0..200");
            var ro = new JsonObject
            {
                ["summonItem"] = new JsonObject
                {
                    ["guid"] = BlockIds.Derive(ctx.Mod.Id, "item", $"{key}.tool").ToString(), ["name"] = $"{ctx.Mod.Id}.{key}.tool", ["base"] = tool,
                    ["texts"] = new JsonObject { ["en"] = new JsonObject { ["caption"] = r.SummonTool ?? n.Name } },
                    ["refs"] = new JsonObject { ["equipment.placedEntity"] = cloneGuid.ToString() },
                },
            };
            if (r.UnlockedBy is { } u) ro["unlock"] = u;
            if (r.RoamingRange is { } rr) ro["roamingRange"] = rr;
            if (body.Workshop is { } w) ro["workshop"] = w;
            o["roster"] = ro;
        }
        return o;
    }
}

/// <summary>NPC blocks in the quests' file format, written to kiln/data/kiln-quests/native/&lt;mod&gt;.blocks.json, beside what the quests a mod defines in
/// code become: the game builds NPCs at start, so what changes is there from the next start, on a server and on a player's game alike. It lives in the data
/// folder, so a redeploy of the mod keeps it; a quests/*.json of the mod's own that names the same NPC wins.</summary>
internal static class NpcFiles
{
    private static readonly Dictionary<string, SortedDictionary<string, JsonObject>> ByMod = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Dirty = new(StringComparer.Ordinal);
    private static IDisposable? _tick;

    /// <summary>An NPC that is no longer one of code's: out of the file at its next write.</summary>
    public static void Forget(string mod, string npc)
    {
        lock (ByMod)
        {
            if (ByMod.TryGetValue(mod, out var m) && m.Remove(npc)) Dirty.Add(mod);
            else if (!ByMod.ContainsKey(mod)) { ByMod[mod] = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal); Dirty.Add(mod); }
            _tick ??= BlocksBridge.AddTick("kiln npc blocks", Flush);
        }
    }

    public static void Put(string mod, string npc, JsonObject entry)
    {
        lock (ByMod)
        {
            if (!ByMod.TryGetValue(mod, out var m)) ByMod[mod] = m = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
            m[npc] = entry;
            Dirty.Add(mod);
            _tick ??= BlocksBridge.AddTick("kiln npc blocks", Flush);
        }
    }

    private static void Flush(ulong step)
    {
        if (step % 25 != 11) return;
        List<(string Mod, JsonObject Doc)> work;
        lock (ByMod)
        {
            work = Dirty.Select(m => (m, new JsonObject { ["format"] = 1, ["comment"] = "written by Kiln from the mod's NPC blocks: docs/kiln/modding/npc-blocks.md",
                ["npcs"] = new JsonObject(ByMod[m].Select(x => KeyValuePair.Create(x.Key, (JsonNode?)x.Value.DeepClone()))) })).ToList();
            Dirty.Clear();
        }
        foreach (var (mod, doc) in work)
        {
            var file = Path.Combine(QuestsInternals.DataOwner.DataDirectory, "native", mod + ".blocks.json");
            if (doc["npcs"] is JsonObject { Count: 0 })
            {
                try { if (File.Exists(file)) File.Delete(file); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { BlocksBridge.Log(LogLevel.Warn, $"blocks: {mod}: {file}: {e.Message}"); }
                continue;
            }
            var text = doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            try
            {
                if (File.Exists(file) && File.ReadAllText(file) == text) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file + ".tmp", text);
                File.Move(file + ".tmp", file, true);
                BlocksBridge.Log(LogLevel.Warn, $"blocks: {mod}: its NPC blocks from code are written for the game's start: {file}. They are in the roster and the game's menus from the next start; one in blocks/*.json is there from the first");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                BlocksBridge.Log(LogLevel.Error, $"blocks: {mod}: its NPC blocks could not be written for the game's start ({file}): {e.Message}");
            }
        }
    }
}
