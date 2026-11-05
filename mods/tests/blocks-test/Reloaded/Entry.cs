// SPDX-License-Identifier: MIT
using Kiln;

namespace BlocksReloaded;

public sealed class Entry : Mod
{
    public override void OnLoad()
    {
        Blocks.Define("reload-bolt", new SpellBlock
        {
            Name = "Reload Bolt",
            Cast = new CastBlock { ManaCost = 12 },
            Delivery = new ProjectileBlock { OnImpact = [new EffectBlock { Area = new AreaBlock { Radius = 2, Effects = [new EffectBlock { Damage = Damage.Ice(5) }] } }] },
        });
    }
}
