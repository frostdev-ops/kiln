// SPDX-License-Identifier: MIT
// A look's VFX: in the hand of whoever holds the item (a visual template of the item's own, cloned from the game's lit axe, with the item's model and this VFX),
// and on what stands there for a placed thing. What a client draws is the manual test's.
using System.Numerics;
using System.Text.Json.Nodes;
using Kiln;

namespace BlocksMoreTest;

public sealed partial class BlocksMoreTestMod
{
    private const string Glow = "826ea0d6-2181-4fea-8aba-3491cd40c39d";   // the game's lit axe's VFX, by GUID

    private void DefineLookVfx()
    {
        Blocks.Define("glow-sword", new WeaponBlock { Name = "Glow Sword", Class = WeaponClass.Sword, Stats = new WeaponStats { Level = 3, Power = 12 },
            Visual = new VisualBlock { Vfx = new VfxRef(Glow) { Offset = new Vector3(0, 0.8f, 0) } } });
        Blocks.Define("glow-vase", new PropBlock { Name = "Glow Vase", Visual = new VisualBlock { Like = "Prop_Decoration_T2_Vase_Clay_01", Vfx = new VfxRef(Glow) } });
        Refused("a light and a look's VFX", () => Blocks.Define("two-lights", new PropBlock { Name = "x", Visual = new VisualBlock { Like = "Prop_Decoration_T2_Vase_Clay_01", Vfx = new VfxRef(Glow) }, Light = new VfxRef(Glow) }), "visual.vfx", "light already");
    }

    private void LookVfx()
    {
        foreach (var twin in new[] { "", "-json" })
        {
            var sword = Item("glow-sword" + twin);
            var held = Content.Templates.Get($"{Me}.glow-sword{twin}.held");
            Check(held != null && S(sword, "equipment.visualEntity") == held.Guid.ToString(), $"sword{twin}: held as a visual template of its own: {S(sword, "equipment.visualEntity")} = {held?.Guid}");
            if (held == null) continue;
            Check(S(held, "VfxComponentResource.vfx") == Glow && Math.Abs(N(held, "VfxComponent.attachmentOffset.localOffset.y") - 0.8) < 1e-4 && S(held, "VfxComponent.slot") == "HandR",
                $"sword{twin}: the VFX in the right hand, 0.8 m up the blade: {S(held, "VfxComponentResource.vfx")}, {N(held, "VfxComponent.attachmentOffset.localOffset.y")}, {S(held, "VfxComponent.slot")}");
            var starterModel = S(Content.ItemData.Get("Weapon_T1_1H_Sword_Craft")!, "iconModel");
            Check(S(held, "ModelResource.model") == starterModel && starterModel != "none", $"sword{twin}: with the sword's model: {S(held, "ModelResource.model")} = {starterModel}");
        }
        var vase = Content.Templates.Get($"{Me}.glow-vase.prop")!;
        Check(S(vase, "VfxComponentResource.vfx") == Glow, $"a placed thing carries its look's VFX: {S(vase, "VfxComponentResource.vfx")}");
    }
}
