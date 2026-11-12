// SPDX-License-Identifier: MIT
// A held model with moving parts (VisualBlock.Animated): a template of the item's own that shows a skinned model of the mod's with the graph that plays its
// clip, and the item pointed at it. It starts from one of the game's staffs, which are skinned models with a graph already, less the staff's VFX parameters;
// daggers get a parent of the game's daggers with a child of that kind in each hand. The hand is the class's own: read when content is ready from the
// visual template of the item's starter, never from a table here. docs/kiln/modding/item-blocks.md, "Parts".
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>What an item is held in.</summary>
internal enum HeldIn { None, OneHand, BothHands }

/// <summary>A visual template as content ready reads it: the slot it is attached to, and its children's (a parent of two daggers has two).</summary>
internal sealed record HeldRow(string? Slot, IReadOnlyList<string?> Children);

/// <summary>Which hand, from rows read when content is ready: pure.</summary>
internal static class HeldHands
{
    private static bool Left(string? slot) => slot is { } s && s.EndsWith('L');

    /// <summary>The slot one held model goes in: the starter's own, or that of its only child. Null with a problem when it is held in none.</summary>
    public static string? One(HeldRow? starter, out string? problem)
    {
        problem = null;
        if (starter == null)
        {
            problem = "its starter has no visual template to say which hand holds it";
            return null;
        }
        if (starter.Slot is { Length: > 0 } s && s != "None") return s;
        var kids = starter.Children.Where(c => c is { Length: > 0 } && c != "None").ToList();
        if (kids.Count == 1) return kids[0];
        problem = kids.Count == 0 ? "its starter's visual template is attached to no slot, so it is held in no hand" : $"its starter is held in {kids.Count} hands ({string.Join(", ", kids)}): this class's look is a pair";
        return null;
    }

    /// <summary>The two hands of a pair: which child of the parent is the left and which the right, and each one's slot. The slots are the starter's children's; the indices
    /// are those of the parent's children with the same slots (the parent is a copy of the game's daggers, whose list may be in another order).</summary>
    public static (int Left, int Right, string LeftSlot, string RightSlot)? Two(HeldRow? starter, HeldRow? parent, out string? problem)
    {
        problem = null;
        var hands = starter?.Children.Where(c => c is { Length: > 0 } && c != "None").ToList() ?? [];
        var left = hands.FirstOrDefault(Left);
        var right = hands.FirstOrDefault(c => !Left(c));
        if (hands.Count != 2 || left == null || right == null)
        {
            problem = $"its starter's visual template does not hold one thing in each hand ({(hands.Count == 0 ? "no children" : string.Join(", ", hands))})";
            return null;
        }
        var kids = (parent ?? starter)!.Children;
        var li = kids.ToList().IndexOf(left);
        var ri = kids.ToList().IndexOf(right);
        if (li < 0 || ri < 0)
        {
            problem = $"the daggers' parent template has no child in {(li < 0 ? left : right)} (its children: {string.Join(", ", kids.Select(k => k ?? "none"))})";
            return null;
        }
        return (li, ri, left, right);
    }
}

internal static partial class Compiler
{
    /// <summary>The template a held model with moving parts starts from: one of the game's staffs (a skinned model with a graph, proven in a client by Kiln Equipment).</summary>
    internal const string AnimatedStarter = "Visuals_Staff_01_Magic_Basic01";
    /// <summary>The parent of a pair of daggers: the game's, which names a template per hand.</summary>
    internal const string AnimatedPairStarter = "Visuals_DualWield_Daggers02";
    private const string ModelType = "keen::RenderModel", GraphType = "keen::anim_graph::runtime_graph::AnimationGraphResource2_0";

    /// <summary>Whether kiln.assets serves this resource of the mod on this machine: what its <c>assets/assets.json</c> named and whose cooked file was there when it
    /// loaded the mod, at boot, before any block compiles. Asked once per model and graph when the block compiles. Tests set it.</summary>
    internal static Func<Guid, string, bool> Served = (guid, type) =>
    {
        try { return Assets.Available && Assets.ReadResource(new AssetKey(guid, Assets.TypeHash(type), 0)) != null; }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or NotSupportedException or KeyNotFoundException) { return false; }
    };

    /// <summary>The fields of a held template that each machine keeps its own of (the content sync's machine-local fields).</summary>
    internal static readonly string[] MachineLocalPaths = ["ModelResource.model", "AnimationGraphResourceComponent2.animationGraph"];

    /// <summary>The staff's component of parameters for its own VFX: left out, as a VFX of ours has none.</summary>
    private static readonly string[] AnimatedRemove = ["VfxParametersTemplateComponent"];

    private static bool CookName(string s) => s.Length is > 0 and <= 128 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    /// <summary>A model's or a graph's GUID: written, or derived from its cook step's name as the cook derives it.</summary>
    private static Guid CookedGuid(BlockContext ctx, string written, string kind, string path)
    {
        if (Guid.TryParse(written, out var g))
            return g != Guid.Empty ? g : throw new BlockError(ctx.Block, path, $"the empty GUID is no {kind}");
        if (!CookName(written))
            throw new BlockError(ctx.Block, path, $"\"{written}\" is neither a GUID nor the name of a {kind} step of assets/cook.json (letters, digits, '_', '-', '.')");
        return BlockIds.Derive(ctx.Mod.Id, kind, written);
    }

    /// <summary>The parts of an animated look, checked: the right hand's model and graph, and the left hand's for a pair.</summary>
    private static (Guid Model, Guid Graph, Guid LeftModel, Guid LeftGraph) AnimatedParts(BlockContext ctx, AnimatedModel a, string path, bool twoHands)
    {
        if (a.Model == null && a.Graph == null) throw new BlockError(ctx.Block, path, "an animated model needs its model and its graph");
        if (a.Model == null) throw new BlockError(ctx.Block, $"{path}.model", "a graph needs the skinned model it moves: give model");
        if (a.Graph == null) throw new BlockError(ctx.Block, $"{path}.graph", "a skinned model needs the graph that plays its clip: give graph");
        if (!twoHands && (a.LeftModel != null || a.LeftGraph != null))
            throw new BlockError(ctx.Block, a.LeftModel != null ? $"{path}.leftModel" : $"{path}.leftGraph", "only daggers are held in both hands: leftModel and leftGraph are a pair's");
        if (a.LeftGraph != null && a.LeftModel == null) throw new BlockError(ctx.Block, $"{path}.leftModel", "a left graph needs the left model it moves: give leftModel");
        if (a.LeftModel != null && a.LeftGraph == null) throw new BlockError(ctx.Block, $"{path}.leftGraph", "a left model needs the graph that plays its clip: give leftGraph");
        var model = CookedGuid(ctx, a.Model, "model", $"{path}.model");
        var graph = CookedGuid(ctx, a.Graph, "graph", $"{path}.graph");
        var lm = a.LeftModel != null ? CookedGuid(ctx, a.LeftModel, "model", $"{path}.leftModel") : model;
        var lg = a.LeftGraph != null ? CookedGuid(ctx, a.LeftGraph, "graph", $"{path}.leftGraph") : graph;
        return (model, graph, lm, lg);
    }

    // The fields of one hand's template: the model, the graph, and the look's VFX on the model's own root (or none: the staff's own would show otherwise).
    // Where this machine has not cooked the moving model or its graph, the same template stands in with the rigid model and no graph of ours: a server spawns
    // the held entity from this template whether or not it cooked (it never runs a held model's graph), and every machine must have it under the same GUID.
    private static JsonObject AnimatedFields(Draft d, Guid model, Guid graph, VfxRef? vfx, string vfxPath, string template, JsonObject json)
    {
        var ctx = d.Ctx;
        var cooked = Served(model, ModelType) && Served(graph, GraphType);
        JsonObject fields;
        if (cooked) fields = new JsonObject { ["ModelResource.model"] = model.ToString(), ["AnimationGraphResourceComponent2.animationGraph"] = graph.ToString() };
        else
        {
            fields = new JsonObject();
            // the look's rigid model: one of the mod's by GUID, else the icon model of the item it looks like, or of its starter (the class's own weapon)
            if (d.Model is { } m && Guid.TryParse(m, out _)) fields["ModelResource.model"] = m;
            else
            {
                var from = d.Model is { } im && im.StartsWith("item:", StringComparison.Ordinal) ? im[5..] : d.Starter;
                d.Low.TemplateRules(template, new JsonObject { ["ModelResource.model"] = $"item:{from}.iconModel" });
            }
        }
        json["cooked"] = cooked;
        if (vfx != null) Merge(fields, VfxData.Component(ctx, VfxData.Lower(ctx, vfx, vfxPath, Slot.Root), template));
        else fields["VfxComponentResource.vfx"] = "none";
        return fields;
    }

    private static void AnimatedHeld(Draft d, VisualBlock v, AnimatedModel a, string path)
    {
        var ctx = d.Ctx;
        var low = d.Low;
        if (d.Held == HeldIn.None) throw new BlockError(ctx.Block, path, "this is not held in a hand: an animated model is for what a player holds (a weapon, a wand, a tool)");
        var pair = d.Held == HeldIn.BothHands;
        var (model, graph, lm, lg) = AnimatedParts(ctx, a, path, pair);
        ctx.Need(ClientsLevel.Mod, path, "a model of the mod's own has to be on every player's game: nothing can send it");
        var mod = ctx.Mod.Id;
        var name = $"{d.Game}.moving";
        var guid = BlockIds.Derive(mod, "template", $"{d.Key}.moving");
        var json = new JsonObject { ["template"] = name, ["guid"] = guid.ToString(), ["model"] = model.ToString(), ["graph"] = graph.ToString(), ["starter"] = d.Starter };
        if (v.Vfx != null) json["vfx"] = v.Vfx.Name;
        if (!pair)
        {
            low.Clone("templates", guid, name, AnimatedStarter, AnimatedFields(d, model, graph, v.Vfx, path.Replace(".animated", ".vfx"), name, json), removeComponents: AnimatedRemove);
            json["base"] = AnimatedStarter;
            json["hand"] = $"the slot of {d.Starter}'s visual template, when content is ready";
        }
        else
        {
            var right = (Name: $"{name}.r", Guid: BlockIds.Derive(mod, "template", $"{d.Key}.moving.r"));
            var left = (Name: $"{name}.l", Guid: BlockIds.Derive(mod, "template", $"{d.Key}.moving.l"));
            var vfxPath = path.Replace(".animated", ".vfx");
            var rj = new JsonObject { ["template"] = right.Name, ["guid"] = right.Guid.ToString(), ["base"] = AnimatedStarter };
            var lj = new JsonObject { ["template"] = left.Name, ["guid"] = left.Guid.ToString(), ["base"] = AnimatedStarter, ["model"] = lm.ToString(), ["graph"] = lg.ToString() };
            low.Clone("templates", right.Guid, right.Name, AnimatedStarter, AnimatedFields(d, model, graph, v.Vfx, vfxPath, right.Name, rj), removeComponents: AnimatedRemove);
            low.Clone("templates", left.Guid, left.Name, AnimatedStarter, AnimatedFields(d, lm, lg, v.Vfx, vfxPath, left.Name, lj), removeComponents: AnimatedRemove);
            // the parent names its children by a rule once all three are registered
            low.Clone("templates", guid, name, AnimatedPairStarter, new JsonObject());
            json["base"] = AnimatedPairStarter;
            json["right"] = rj;
            json["left"] = lj;
            json["hands"] = $"the slots of {d.Starter}'s visual template's children, and their places among {AnimatedPairStarter}'s, when content is ready";
        }
        d.Refs["equipment.visualEntity"] = guid.ToString();
        low.Json["animatedHeld"] = json;
        // Each machine keeps its own model and graph for the template that holds them: a machine that cooked has the moving model, one that did not stands in
        // with the rigid one. The content sync would send a server's over a client's (the server's layer wins, and matchServer puts the client's graph back),
        // so these two fields are machine-local. Safe: only the machine that draws reads them, and a dedicated server never starts a held model's graph and
        // draws nothing (player-anim's lab, docs/kiln/modding/models.md "On a dedicated server"). The hand, the VFX and the rest sync as any clone's.
        Guid[] hands = pair ? [BlockIds.Derive(mod, "template", $"{d.Key}.moving.r"), BlockIds.Derive(mod, "template", $"{d.Key}.moving.l")] : [guid];
        var marks = hands.SelectMany(h => MachineLocalPaths.Select(f => (Template: h, Field: f))).ToList();
        json["machineLocal"] = new JsonArray(marks.Select(m => (JsonNode)new JsonObject { ["template"] = m.Template.ToString(), ["field"] = m.Field }).ToArray());
        low.AfterCommit(() =>
        {
            foreach (var (template, field) in marks) ContentSync.MarkMachineLocal("templates", template, field);
        });
        var starter = d.Starter;
        var block = ctx.Block;
        var owner = ctx.Mod;
        low.AfterCommit(() => BlockRuntime.AddReady(owner, problem => AnimatedReady(owner, block, path, starter, name, pair, problem)));
    }

    /// <summary>What content ready makes of the rows: the rules that put each template in its hand. Pure.</summary>
    internal static List<(string Template, JsonObject Rules)> AnimatedRules(string block, string path, string template, bool pair, HeldRow? starter, HeldRow? parent, List<string> problems)
    {
        var rules = new List<(string, JsonObject)>();
        if (!pair)
        {
            if (HeldHands.One(starter, out var why) is { } slot) rules.Add((template, new JsonObject { ["SlotAttachment.attachmentSlot"] = slot }));
            else problems.Add($"block {block}, {path}: {why}; the moving model stays in the staff's hand (HandR)");
            return rules;
        }
        if (HeldHands.Two(starter, parent, out var problem) is not { } h)
        {
            problems.Add($"block {block}, {path}: {problem}; the item keeps its pair of the game's daggers");
            return rules;
        }
        rules.Add(($"{template}.l", new JsonObject { ["SlotAttachment.attachmentSlot"] = h.LeftSlot }));
        rules.Add(($"{template}.r", new JsonObject { ["SlotAttachment.attachmentSlot"] = h.RightSlot }));
        rules.Add((template, new JsonObject { [$"SubEntities.subEntities[{h.Left}].templateRef"] = $"{template}.l", [$"SubEntities.subEntities[{h.Right}].templateRef"] = $"{template}.r" }));
        return rules;
    }

    private static void AnimatedReady(ModBlocks mod, string block, string path, string starter, string template, bool pair, Action<string> problem)
    {
        var problems = new List<string>();
        try
        {
            var visual = Content.ItemData.Get(starter) is { } item ? item.Get("equipment.visualEntity").Guid : Guid.Empty;
            var rules = AnimatedRules(block, path, template, pair, visual == Guid.Empty ? null : ReadHeld(visual.ToString()), pair ? ReadHeld(AnimatedPairStarter) : null, problems);
            foreach (var (entry, r) in rules) BlockRuntime.AddRules(mod, "templates", entry, r);
        }
        catch (Exception e) when (e is KeyNotFoundException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            problems.Add($"block {block}, {path}: the game's data could not be read: {e.Message}");
        }
        foreach (var p in problems) problem(p);
    }

    /// <summary>A template's slot and its children's, from the game's data. Null: no such template.</summary>
    private static HeldRow? ReadHeld(string template)
    {
        if (Content.Templates.Get(template) is not { } t) return null;
        string? SlotOf(GameDataEntry e)
        {
            try { return e.GetJson("SlotAttachment.attachmentSlot")?["value"]?.ToString(); }
            catch (KeyNotFoundException) { return null; }
        }
        var children = new List<string?>();
        for (var i = 0; i < 16; i++)
        {
            Guid child;
            try { child = t.Get($"SubEntities.subEntities[{i}].templateRef").Guid; }
            catch (Exception e) when (e is KeyNotFoundException or ArgumentException) { break; }
            children.Add(child != Guid.Empty && Content.Templates.Get(child.ToString()) is { } c ? SlotOf(c) : null);
        }
        return new HeldRow(SlotOf(t), children);
    }
}
