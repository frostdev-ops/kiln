// SPDX-License-Identifier: MIT
// Mod.OnBoot (docs/kiln/modding/onboot.md, chapter 08 §6): OnBoot runs once per process, before the game is entered, on an instance of its
// own. It reads the game's data from the boot snapshot (boot.GameData) and stages plain-data blocks and one resource patch (BootDefinitions);
// Kiln prepares them with every mod's files and publishes them as one generation, so the first start of a server already has them. OnLoad
// defines nothing; it reads the patched field back once the game's content is ready.
// NPCs, building materials and props are not boot kinds in this build: staging one answers NotInBootKinds (see README.md).
using Kiln;

namespace BootGallery;

public sealed class BootGalleryMod : Mod
{
    public override void OnBoot(BootContext boot)
    {
        var definitions = BootDefinitions.For(boot);

        // the game's data before the game runs: five records of the snapshot (a missing one stops this OnBoot: the mod is left out, named)
        var wood = boot.GameData.Read("item:Material_T1_Wood_Logs");
        if (!wood.Ok) throw new InvalidOperationException(wood.Reason);
        foreach (var key in new[] { "item:Prop_Decoration_T2_Vase_Clay_01", "template:NPC_Workshop_Fisher_Trader", "skill:0x8efcc472", "material:StoneRough" })
            if (boot.GameData.Read(key) is { Ok: false } missing) throw new InvalidOperationException($"snapshot missing {key}: {missing.Reason}");

        static void Required(bool ok, string? reason) { if (!ok) throw new InvalidOperationException(reason); }
        var status = definitions.Define("cold", new StatusBlock { Name = "Boot Cold", Seconds = 4 });
        Required(status.Ok, status.Reason);
        var effect = definitions.Define("cold-hit", new EffectBlock { Damage = Damage.Ice(4), Applies = new StatusApply { Status = "cold" } });
        Required(effect.Ok, effect.Reason);
        // a number from the game's data: the token stacks like the game's wood logs (at most 250)
        var stack = wood.Data!.Value.TryGetProperty("maxStackSize", out var max) && max.TryGetInt32(out var n) ? Math.Min(250, n) : 20;
        var item = definitions.Define("wood-token", new ItemBlock { Name = "Boot Token", Stack = stack });
        Required(item.Ok, item.Reason);
        var spell = definitions.Define("cold-bolt", new SpellBlock { Name = "Boot Bolt", Cast = new CastBlock { ManaCost = 2 }, Delivery = new ProjectileBlock { OnHit = ["cold-hit"] } });
        Required(spell.Ok, spell.Reason);
        var talent = definitions.Define("cold-talent", new TalentBlock { Name = "Boot Cold Talent", Keeps = "cold", Game = new GameTreePlace { From = ["0x8efcc472"], Offset = [170, 0] } });
        Required(talent.Ok, talent.Reason);
        // a field of a game resource, set before the game reads it: the first terrain config's friction factor of the terraforming
        // registry, 1.0 in the game, 0.999 here (a 0.1 % change nobody can see; it marks that the patch is applied)
        var patch = definitions.Define("friction-mark", new ResourcePatchBlock
        {
            Resource = FrictionResource, Type = FrictionType, Set = { [FrictionPath] = FrictionValue },
        });
        Required(patch.Ok, patch.Reason);
        Context.Log.Info($"boot-gallery: staged 6 definitions in generation {boot.Generation} (token stack {stack}, from the game's wood logs)");
    }

    internal static readonly Guid FrictionResource = Guid.Parse("15506ca1-f0b2-8a43-a087-75a7c83d5596");
    internal const string FrictionType = "keen::TerraformingEfficiencyRegistryResource";
    internal const string FrictionPath = "terrainConfigs[0].frictionFactor";
    internal const double FrictionValue = 0.999;

    public override void OnLoad()
    {
        // no definitions here: they are in the boot generation already
        var report = Mods.BootState("boot-gallery");
        Context.Log.Info($"boot-gallery: {report.State}, generation {report.Generation}: {string.Join(", ", report.Definitions)}");
        Content.OnReady(_ => ReadPatch());
    }

    // the patched field as the game holds it now (ReflectedResources reads the resource the game is served)
    private void ReadPatch()
    {
        var r = ReflectedResources.Get(FrictionResource, FrictionType);
        Context.Log.Info(r is { Exists: true }
            ? $"boot-gallery: {FrictionPath} = {r.Get<double>(FrictionPath):0.###} (the resource patch friction-mark set {FrictionValue})"
            : $"boot-gallery: {FrictionType} is not readable here (ReflectedResources available: {ReflectedResources.Available})");
    }
}
