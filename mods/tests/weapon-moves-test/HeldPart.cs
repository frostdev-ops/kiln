// SPDX-License-Identifier: MIT
// Held models with moving parts (VisualBlock.Animated, docs/kiln/modding/item-blocks.md "Parts") on the server: what blocks/held.json made, cooked on this
// machine (assets/cooked/ in the mod's folder, from build-assets.py) or not. The hand is compared with what the class's starter's own visual template says
// in the running game, never with a table here.
using System.Text.Json.Nodes;
using Kiln;

namespace WeaponMovesTest;

public sealed partial class WeaponMovesTestMod
{
    private const string Staff = "Visuals_Staff_01_Magic_Basic01", Pair = "Visuals_DualWield_Daggers02", LitAxe = "Visuals_Axe_02_Light";

    /// <summary>A weapon with an animated look: the block, its class's starter, its models and graphs by cook step, its VFX.</summary>
    internal sealed record HeldCase(string Block, string Starter, string Model, string Graph, string? LeftModel, string? LeftGraph, string? Vfx, double VfxUp)
    {
        public bool Pair => LeftModel != null || Starter.Contains("Daggers", StringComparison.Ordinal);
    }

    internal static readonly HeldCase[] HeldCases =
    [
        new("held-sword", SwordStarter, "moves_blade_anim", "moves_blade", null, null, null, 0),
        new("held-greataxe", AxeStarter, "moves_blade_anim", "moves_blade", null, null, LitAxe, 0.6),
        new("held-shield", "Shield_T1_DPS", "moves_blade_anim", "moves_blade", null, null, "c17b1434-3e08-4c03-b9f9-746cd0a59338", 0.2),
        new("held-daggers", "Weapon_T1_2H_Daggers_01_DPS", "moves_blade_anim", "moves_blade", "moves_blade_anim", "moves_blade", null, 0),
        new("held-daggers-left", "Weapon_T1_2H_Daggers_01_DPS", "moves_blade_anim", "moves_blade", "moves_blade_l_anim", "moves_blade_l", null, 0),
    ];

    private static readonly Guid RigidBlade = BlockIds.Derive(Me, "model", "moves_blade");

    /// <summary>Whether this machine cooked the held models: what kiln.assets serves comes from assets/cooked/ in the mod's folder.</summary>
    private bool HeldCooked => File.Exists(Path.Combine(Context.Info.Directory, "assets", "cooked", "moves_blade_anim.bin"));

    private static string? SlotOf(GameDataEntry t)
    {
        try { return t.GetJson("SlotAttachment.attachmentSlot")?["value"]?.ToString(); }
        catch (KeyNotFoundException) { return null; }
    }

    private static List<(int Index, Guid Template, string? Slot)> Children(GameDataEntry t)
    {
        var r = new List<(int, Guid, string?)>();
        for (var i = 0; i < 16; i++)
        {
            Guid g;
            try { g = t.Get($"SubEntities.subEntities[{i}].templateRef").Guid; }
            catch (Exception e) when (e is KeyNotFoundException or ArgumentException) { break; }
            r.Add((i, g, Content.Templates.Get(g.ToString()) is { } c ? SlotOf(c) : null));
        }
        return r;
    }

    private static bool Has(GameDataEntry t, string component)
    {
        try { t.GetJson(component); return true; }
        catch (KeyNotFoundException) { return false; }
    }

    /// <summary>The starter's own visual template, as the running game has it.</summary>
    private static GameDataEntry? StarterVisual(string starter) =>
        Content.ItemData.Get(starter) is { } item && item.Get("equipment.visualEntity").Guid is var g && g != Guid.Empty ? Content.Templates.Get(g.ToString()) : null;

    // one hand's template: the model, the graph, the hand, the VFX
    private void OneHand(HeldCase h, GameDataEntry t, string label, string model, string graph, string? hand, bool cooked)
    {
        var staff = Content.Templates.Get(Staff)!;
        Check(t.IsClone && t.Original("ModelResource.model").Guid == staff.Get("ModelResource.model").Guid, $"{label}: starts from the staff {Staff} (its original model is the staff's)");
        var wantModel = cooked ? BlockIds.Derive(Me, "model", model) : RigidBlade;
        var wantGraph = cooked ? BlockIds.Derive(Me, "graph", graph) : staff.Get("AnimationGraphResourceComponent2.animationGraph").Guid;
        if (_wrong && h.Block == "held-sword") wantGraph = Guid.Empty;
        Check(t.Get("ModelResource.model").Guid == wantModel, $"{label}: {(cooked ? "the skinned model" : "not cooked here: the rigid stand-in")} {t.Get("ModelResource.model").Guid}, {wantModel} expected");
        Check(t.Get("AnimationGraphResourceComponent2.animationGraph").Guid == wantGraph, $"{label}: {(cooked ? "the graph of its clip" : "not cooked here: the staff's own graph")} {t.Get("AnimationGraphResourceComponent2.animationGraph").Guid}, {wantGraph} expected");
        var slot = SlotOf(t);
        var want = _wrong && h.Block == "held-greataxe" ? "HandR" : hand;
        Check(hand != null && slot == want, $"{label}: held in {slot}, the slot the game's {h.Starter} is held in ({want ?? "none read"})");
        Check(!Has(t, "VfxParametersTemplateComponent"), $"{label}: the staff's VFX parameters are left out");
        var vfx = t.Get("VfxComponentResource.vfx").Guid;
        if (h.Vfx is { } v)
        {
            var theirs = Guid.TryParse(v, out var byGuid) ? byGuid : Content.Templates.Get(v)!.Get("VfxComponentResource.vfx").Guid;
            Info($"{label}: the look's VFX {theirs} ({v}); the staff's own {staff.Get("VfxComponentResource.vfx").Guid}{(theirs == staff.Get("VfxComponentResource.vfx").Guid ? ": the same, so the template's value is its original and the state sends none" : "")}");
            Check(vfx == theirs && vfx != Guid.Empty && Math.Abs(t.GetNumber("VfxComponent.attachmentOffset.localOffset.y") - h.VfxUp) < 1e-4 && t.Get("VfxComponent.slot").Name == "Root",
                $"{label}: carries the look's VFX {vfx} ({v}'s is {theirs}) on its root, {t.GetNumber("VfxComponent.attachmentOffset.localOffset.y"):0.###} m up");
        }
        else Check(vfx == Guid.Empty && staff.Get("VfxComponentResource.vfx").Guid != Guid.Empty, $"{label}: no VFX, not the staff's ({staff.Get("VfxComponentResource.vfx").Guid}): {vfx}");
    }

    private void HeldChecks()
    {
        var cooked = HeldCooked;
        Info($"held models: {(cooked ? "cooked on this machine (assets/cooked/ is in the mod's folder)" : "not cooked on this machine")}");
        foreach (var h in HeldCases)
        {
            var name = $"{G(h.Block)}.moving";
            var t = Content.Templates.Get(name);
            var guid = BlockIds.Derive(Me, "template", $"{h.Block}.moving");
            if (!Check(t != null && t.Guid == guid, $"{name}: the moving template, GUID {t?.Guid} by derive(mod, \"template\", \"{h.Block}.moving\") = {guid}")) continue;
            var item = Content.Items.Get(G(h.Block))!;
            Check(item.GetRef("equipment.visualEntity") == guid, $"{h.Block}: the item holds it: equipment.visualEntity {item.GetRef("equipment.visualEntity")}");
            var low = JsonNode.Parse(Blocks.Lowered(h.Block, Me) ?? "{}")!;
            Check((bool?)low["animatedHeld"]?["cooked"] == cooked || (bool?)low["animatedHeld"]?["right"]?["cooked"] == cooked,
                $"{h.Block}: the block says cooked {low["animatedHeld"]?["cooked"] ?? low["animatedHeld"]?["right"]?["cooked"]}, as this machine is");
            // the blocks report says which fields stay each machine's own: the model and the graph of each hand's template
            var marks = (low["animatedHeld"]?["machineLocal"] as JsonArray ?? []).Select(m => $"{m?["template"]} {m?["field"]}").ToHashSet();
            var hands = h.Pair ? new[] { $"{h.Block}.moving.r", $"{h.Block}.moving.l" } : [$"{h.Block}.moving"];
            var wantMarks = hands.SelectMany(x => new[] { "ModelResource.model", "AnimationGraphResourceComponent2.animationGraph" }.Select(f => $"{BlockIds.Derive(Me, "template", x)} {f}")).ToHashSet();
            if (_wrong && h.Block == "held-shield") wantMarks.Add("x");
            Check(marks.SetEquals(wantMarks), $"{h.Block}: the blocks report marks the model and the graph of {hands.Length} hand(s) machine-local: [{string.Join(", ", marks)}]");
            var starterItem = Content.Items.Get(h.Starter)!;
            foreach (var f in new[] { "equipment.equipmentEntityModelSetup.model", "equipment.visualModel", "iconModel" })
            {
                Guid mineRef, theirsRef;
                try { mineRef = item.GetRef(f); theirsRef = starterItem.GetRef(f); }
                catch (Exception e) when (e is KeyNotFoundException or ArgumentException) { continue; }
                Info($"{h.Block}: {f} = {mineRef} (the look's rigid model {RigidBlade}{(mineRef == RigidBlade ? "" : mineRef == theirsRef ? "; it is the starter's" : "")}; the starter {h.Starter}'s {theirsRef})");
            }
            var starter = StarterVisual(h.Starter);
            if (!h.Pair)
            {
                // the hand: the starter's own visual template's slot, or its only child's
                var hand = starter is null ? null : SlotOf(starter) is { Length: > 0 } s && s != "None" ? s : Children(starter).Select(c => c.Slot).SingleOrDefault(x => x is { Length: > 0 } && x != "None");
                Info($"{h.Block}: the game's {h.Starter} is held by {starter?.Name} in {hand}");
                OneHand(h, t!, name, h.Model, h.Graph, hand, cooked);
                continue;
            }
            // a pair: the parent is a copy of the game's daggers, its two children ours, each in the hand the starter's pair has
            var pair = Content.Templates.Get(Pair)!;
            Check(t!.IsClone && t.Original("SubEntities.subEntities[0].templateRef").Guid == pair.Get("SubEntities.subEntities[0].templateRef").Guid, $"{name}: the parent starts from the game's {Pair}");
            var theirs = starter is null ? [] : Children(starter);
            var mine = Children(t);
            Info($"{h.Block}: the game's {h.Starter} is held by {starter?.Name} with children in [{string.Join(", ", theirs.Select(c => c.Slot))}]; ours [{string.Join(", ", mine.Select(c => $"{Content.Templates.Get(c.Template.ToString())?.Name} in {c.Slot}"))}]");
            foreach (var (side, model, graph) in new[] { ("l", h.LeftModel!, h.LeftGraph!), ("r", h.Model, h.Graph) })
            {
                var child = Content.Templates.Get($"{name}.{side}");
                var cg = BlockIds.Derive(Me, "template", $"{h.Block}.moving.{side}");
                if (!Check(child != null && child.Guid == cg && mine.Any(c => c.Template == cg), $"{name}.{side}: a child of its own under derive(mod, \"template\", \"{h.Block}.moving.{side}\") = {cg}, named by the parent")) continue;
                var hand = theirs.Select(c => c.Slot).FirstOrDefault(x => x is { Length: > 0 } && x != "None" && x.EndsWith(side == "l" ? 'L' : 'R'));
                OneHand(h, child!, $"{name}.{side}", model, graph, hand, cooked);
            }
        }
    }

    // A held entity: the weapon in a spawned character's hand, as blocks-items-test puts one there; the server spawns what the item's visualEntity names.
    private async Task HeldInHand(EntityId p)
    {
        foreach (var block in new[] { "held-sword", "held-daggers-left" })
        {
            var name = $"{G(block)}.moving";
            // the entities that came with putting it in hand (as player-anim's parts-check finds them): the world before and after
            var before = Spawn.Find(Guid.Empty, max: 1 << 16).ToHashSet();
            if (!await ToHand(p, block)) continue;
            var first = Spawn.Find(Guid.Empty, max: 1 << 16).Where(e => !before.Contains(e))
                .Where(e => Spawn.Inspect(e)?.TemplateName is { } n && (n == name || n.StartsWith(name + ".", StringComparison.Ordinal))).ToList();
            Info($"{block} in hand: entities spawned from {name}*: [{string.Join(", ", first.Select(e => $"{e} {Spawn.Inspect(e)?.TemplateName}"))}]");
            var want = block == "held-daggers-left" ? 3 : 1;
            if (_wrong && block == "held-sword") want = 2;
            if (!Check(first.Count == want, $"{block} in hand: the server spawned the held entity from its moving template ({first.Count} of {want}: {string.Join(", ", first.Select(e => Spawn.Inspect(e)?.TemplateName))})")) continue;
            await Steps(60);
            var later = Spawn.FindByName(name, max: 64).Where(e => Spawn.Inspect(e)?.TemplateName is { } n && (n == name || n.StartsWith(name + ".", StringComparison.Ordinal))).ToList();
            Check(later.Count == want, $"{block}: 60 steps later the hand still holds {later.Count} of {want} from the moving template ({(later.ToHashSet().SetEquals(first) ? "the same entities" : $"{string.Join(", ", first)} then {string.Join(", ", later)}")})");
        }
    }
}
