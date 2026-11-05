// SPDX-License-Identifier: MIT
// What a real mod leaves in kiln.creatures: rules on the Vukah's arsenal with its own code in them, plus an attack override
// and an AI switch on the creatures the test names. The test unloads this mod and checks that it all went.
using Kiln;

namespace NpcRulesCompanion;

public sealed class NpcRulesCompanionMod : Mod
{
    public override void OnLoad()
    {
        Arsenals.Extend("Enemy_Vukah_Melee", new ArsenalSpec
        {
            Rules =
            [
                new ArsenalRule
                {
                    Name = "companion",
                    Every = 0.25,
                    If = [Is.Custom(_ => true, every: 0.25)],
                    Do = [Act.Custom(npc => Log.Debug($"companion rule on {npc.Entity}"))],
                },
            ],
        });
        Context.Messages.On("own", msg =>
        {
            var parts = ((string)msg.Payload!).Split(' ');
            var c = Creatures.Of(new EntityId(uint.Parse(parts[0])));
            c.Attacks.Force(1);
            var d = Creatures.Of(new EntityId(uint.Parse(parts[1])));
            d.AiEnabled = false;
        });
        Log.Info("companion: extended the Vukah's arsenal");
    }
}
