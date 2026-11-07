// SPDX-License-Identifier: MIT
// A projectile that flies through creatures. The flight itself needs terrain streamed near a player, so the lab checks what the program is: registered and
// accepted by kiln.abilities' verifier, one remembered creature per pierce, and the errors of a wrong one. Its programs are written out for tools/impact lint.
using System.Text.Json.Nodes;
using Kiln;

namespace BlocksMoreTest;

public sealed partial class BlocksMoreTestMod
{
    private void DefinePierce()
    {
        Blocks.Define("piercing-shard", new SpellBlock
        {
            Name = "Piercing Shard",
            Cast = new CastBlock { ManaCost = 20 },
            Delivery = new ProjectileBlock { Speed = 40, Pierce = 3, OnHit = [new EffectBlock { Damage = Damage.Ice(12) }], OnImpact = [new EffectBlock { Area = new AreaBlock { Radius = 2, Effects = [new EffectBlock { Damage = Damage.Ice(5) }] } }] },
        });
        Refused("pierce without onHit", () => Blocks.Define("pierce-nothing", new SpellBlock { Name = "x", Cast = new CastBlock { ManaCost = 1 }, Delivery = new ProjectileBlock { Pierce = 2, OnImpact = [new EffectBlock { Area = new AreaBlock { Radius = 2, Effects = [new EffectBlock { Heal = 1 }] } }] } }), "delivery.pierce", "onHit");
        Refused("pierce too far", () => Blocks.Define("pierce-many", new SpellBlock { Name = "x", Cast = new CastBlock { ManaCost = 1 }, Delivery = new ProjectileBlock { Pierce = 9, OnHit = [new EffectBlock { Heal = 1 }] } }), "delivery.pierce", "0..8");
    }

    private void Pierce()
    {
        var dir = Path.Combine(Context.DataDirectory, "programs");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Info($"{Blocks.DumpPrograms(dir)} program(s) written to {dir}");
        foreach (var twin in new[] { "piercing-shard", "piercing-shard-json" })
        {
            var low = JsonNode.Parse(Blocks.Lowered(twin) ?? "{}");
            var listings = Directory.GetFiles(dir, "*.txt").Select(File.ReadAllText).Where(t => t.Contains($"{twin}.projectile", StringComparison.Ordinal)).ToList();
            var text = listings.FirstOrDefault() ?? "";
            var slots = Enumerable.Range(0, 9).Count(k => text.Contains($"pierced{k}", StringComparison.Ordinal));
            Check(listings.Count == 1 && slots == 3, $"{twin}: its flight's program remembers 3 creatures: {slots} slot(s) in {listings.Count} listing(s)");
            var errors = Blocks.Report()[Me].Errors.Where(e => e.Contains(twin, StringComparison.Ordinal)).ToList();
            Check(errors.Count == 0, $"{twin}: registered with no error from the verifier: {string.Join("; ", errors)}");
            Check(low?.ToJsonString().Contains("projectile") == true, $"{twin}: lowered to a projectile template and its program");
        }
    }
}
