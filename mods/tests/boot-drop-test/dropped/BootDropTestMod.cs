// SPDX-License-Identifier: MIT
// Chapter 08's forced-drop proof (review B2/B3): OnBoot stages one definition of each kind that lowers to the assets, content and skills
// collectors; the mod's files feed worldtables, knowledge and patches. In a start where kiln.json boot.testRefuse names this mod, one
// collector refuses it, every collector that prepared before is destroyed, and the generation is prepared again without it: then this mod
// must leave nothing behind (run.sh compares that start with one where the mod is absent).
using Kiln;

namespace BootDropTest;

public sealed class BootDropTestMod : Mod
{
    public override void OnBoot(BootContext boot)
    {
        var d = BootDefinitions.For(boot);
        void Must(bool ok, string what) { if (!ok) throw new InvalidOperationException(what); }
        Must(d.Define("drop-cold", new StatusBlock { Name = "Drop Cold", Seconds = 4 }).Ok, "drop-cold");
        Must(d.Define("drop-cold-hit", new EffectBlock { Applies = new StatusApply { Status = "drop-cold" } }).Ok, "drop-cold-hit");
        Must(d.Define("drop-token", new ItemBlock { Name = "Drop Token", Stack = 20 }).Ok, "drop-token");
        Must(d.Define("drop-bolt", new SpellBlock { Name = "Drop Bolt", Cast = new CastBlock { ManaCost = 2 }, Delivery = new ProjectileBlock { OnHit = ["drop-cold-hit"] } }).Ok, "drop-bolt");
        Must(d.Define("drop-talent", new TalentBlock { Name = "Drop Talent", Keeps = "drop-cold", Game = new GameTreePlace { From = ["0x8efcc472"], Offset = [170, 0] } }).Ok, "drop-talent");
    }

    public override void OnLoad() => Context.Log.Info($"boot-drop-test: loaded: {Mods.BootState("boot-drop-test").State}");
}
