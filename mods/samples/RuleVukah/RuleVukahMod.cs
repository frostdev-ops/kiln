// SPDX-License-Identifier: MIT
// Rules on a game body. Nothing here runs on a client, and nothing runs on the game's job threads: the game's own rule walker
// decides when, Kiln reads what the tick published, and the code below runs on the tick thread.
using Kiln;

namespace RuleVukah;

public sealed class RuleVukahMod : Mod
{
    private static readonly RuleCondition InCombat = Is.Game("StateConstrain", ("states", new[] { "Combat" }));
    private static readonly Guid Shout = Guid.Parse("20266648-bb30-41cf-8987-a1fe8761a192");    // the Vukah's Retaliate_Shout
    private static readonly Guid Throw = Guid.Parse("99a97b35-10e9-45aa-916a-5c53f7a20f5f");    // its ThrowStone

    public override void OnLoad()
    {
        CreatureAdminCommands.Register();

        // Every Vukah, through the game's own arsenal.
        Arsenals.Extend("Enemy_Vukah_Melee", new ArsenalSpec
        {
            Rules =
            [
                new ArsenalRule
                {
                    Name = "rally",
                    Every = 1,
                    Cooldown = 20,
                    If = [InCombat, Is.HealthBelow(0.5)],
                    Do =
                    [
                        Act.Game("PlayActionSequenceAction", ("sequence", Shout), ("waitUntilFinished", false)),
                        Act.Custom(npc =>
                        {
                            Log.Info($"rule-vukah: {npc.Entity} rallies at {npc.HealthFraction:P0}, target {npc.Target}");
                            Combat.Heal(npc.Entity, npc.HealthMax / 4);
                        }),
                    ],
                },
            ],
        });

        // A new arsenal for this sample's own creatures: /creature set <id> arsenal <the GUID below>.
        var thrower = Arsenals.Define("thrower", new ArsenalSpec
        {
            From = Arsenals.OfTemplate("Enemy_Vukah_Melee"),
            Attacks =
            [
                new ArsenalAttack
                {
                    Name = "long-throw",
                    Sequence = Throw,
                    Style = ["Normal"],
                    MinRange = 3,
                    MaxRange = 40,
                    Selection = [Is.TargetBetween(3, 40), Is.Game("CooldownConstrain", ("duration", 3.0))],
                },
            ],
            Rules =
            [
                new ArsenalRule
                {
                    Name = "night-throw",
                    Every = 2,
                    Cooldown = 8,
                    If = [InCombat, Is.Night, Is.TargetBeyond(4)],
                    Do = [Act.Attack("long-throw")],
                },
            ],
        });
        Log.Info($"rule-vukah: rules on the Vukah's arsenal; the thrower's arsenal is {thrower.Id}");
    }
}
