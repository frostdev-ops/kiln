// SPDX-License-Identifier: MIT
// Who a creature fights, and what it does, beyond the game's own: docs/kiln/modding/npc-ai.md. Everything runs on the server's tick thread (or in
// the game's own rule walker, as data); vanilla clients see the creatures fight, flee and call for help.
using Kiln;

namespace CreatureAi;

public sealed class CreatureAiMod : Mod
{
    public override void OnLoad()
    {
        // The world's table: every Vukah and every Scavenger fight each other (the game already has them hostile; this makes it a feud both ways
        // even where a species rule would not).
        void Relate()
        {
            if (Factions.Available) Factions.Relate(Faction.Vukah, Faction.Scavenger, Relation.Hostile);
            else Log.Warn("Faction relations are unavailable on this game build.");
        }
        Events.On<ServerStartedEvent>(_ => Relate());
        if (Lifecycle.IsServerRunning) Relate();

        // A Vukah with its own view: players are none of its business, Scavengers are.
        Blocks.Define("warden", new CreatureBlock
        {
            Name = "Vukah Warden",
            Body = Bodies.Vukah,
            AI = AIs.Heavy with
            {
                Sight = 30, NightSight = 1, ReturnsHome = false,
                Relations = new Dictionary<Faction, Relation> { [Faction.Player] = Relation.Neutral, [Faction.Scavenger] = Relation.Hostile },
            },
            Stats = new CreatureStats { Level = 25 },
            Spawns = [new SpawnBlock { Replaces = "Enemy_Vukah_Melee", Chance = 0.05, DayOnly = true }],
        });

        // A friendly fighter: on the players' side, hunting Vukahs; its filter leaves the ones at low level alone. `/creature spawn creature-ai:ranger`.
        Blocks.Define("ranger", new CreatureBlock
        {
            Name = "Ranger",
            Body = Bodies.Vukah,
            AI = AIs.Vukah with
            {
                Faction = Faction.PlayerAlly, Sight = 35,
                Relations = new Dictionary<Faction, Relation> { [Faction.Vukah] = Relation.Hostile },
                SenseFilter = (npc, other) => Creatures.Available && Creatures.Of(other).Level >= 5,
                Roam = new RoamBlock { Min = 4, Max = 12, FromHome = 25 },
            },
            Behaviour = Behaviours.VukahMelee with
            {
                Rules = [new RuleBlock { Name = "rest", Every = 5, Cooldown = 20, When = [Is.HealthBelow(0.4), Is.EnemiesNear(1, 15).Not()], Do = [Act.Heal(0.25)] }],
            },
            Stats = new CreatureStats { Level = 20 },
        });
    }
}
